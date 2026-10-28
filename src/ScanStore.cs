// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.IO;

namespace HashVista;

/// <summary>One durable scan per database; bounded reads and serialized connection access.</summary>
public sealed class ScanStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private bool disposed;
    public string DatabasePath { get; }
    public ScanInfo Info { get; private set; } = new();
    public bool HasScan { get; private set; }

    public ScanStore(string dbPath, bool requireExisting = false)
    {
        DatabasePath = Path.GetFullPath(dbPath);
        if (requireExisting) ValidateExistingDatabase(DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        connection.Open();
        // WAL + NORMAL avoids a durable flush for every small status update on huge scans.
        // The database remains consistent after an application crash; a sudden power loss
        // can lose the latest committed status updates, never the source files.
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS scan (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, path_key TEXT NOT NULL UNIQUE,
                file_id TEXT NOT NULL UNIQUE, kind TEXT NOT NULL, status TEXT NOT NULL,
                disk_serial TEXT NOT NULL DEFAULT '', disk_number TEXT NOT NULL DEFAULT '',
                seen INTEGER NOT NULL DEFAULT 1, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS files_kind_seq ON files(kind,seq);
            CREATE INDEX IF NOT EXISTS files_status_seq ON files(status,seq);
            CREATE TABLE IF NOT EXISTS issues (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL,
                stage TEXT NOT NULL, code TEXT NOT NULL, message TEXT NOT NULL);
            """);
        // In-place migration for scans created before physical disk identities were added.
        EnsureColumn("files", "disk_serial", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("files", "disk_number", "TEXT NOT NULL DEFAULT ''");
        Execute("CREATE INDEX IF NOT EXISTS files_disk_serial_seq ON files(disk_serial,seq);");
        Execute("CREATE INDEX IF NOT EXISTS files_disk_number_seq ON files(disk_number,seq);");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM scan WHERE id=1";
        if (command.ExecuteScalar() is string json)
        {
            Info = JsonSerializer.Deserialize<ScanInfo>(json) ?? throw new InvalidDataException("Invalid scan record.");
            HasScan = true;
            if (Info.Status is "Scanning" or "Enumerating" or "Resuming" or "Paused")
            {
                Info.Status = "Interrupted";
                Info.FinishedUtc = null;
            }
            // Saved counters from older builds can describe only the rediscovered
            // portion of an interrupted resume. Repair them from all retained rows
            // once on open; the UI can then use these cached totals without queries.
            RebuildInventoryTotals();
            SaveInfo();
        }
    }

    private static void ValidateExistingDatabase(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Saved scan database was not found.", path);
        using var readOnly = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        readOnly.Open();
        using var command = readOnly.CreateCommand();
        command.CommandText = "SELECT payload FROM scan WHERE id=1";
        if (command.ExecuteScalar() is not string json) throw new InvalidDataException("This database does not contain a saved HashVista scan.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("ScanId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
            !root.TryGetProperty("Roots", out var roots) || roots.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("Options", out var options) || options.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("This database does not contain a recognized HashVista scan record.");
        command.CommandText = "SELECT seq,path_key,file_id,kind,status,seen,payload FROM files LIMIT 0";
        using (var reader = command.ExecuteReader()) { }
        command.CommandText = "SELECT seq,path,stage,code,message FROM issues LIMIT 0";
        using (var reader = command.ExecuteReader()) { }
    }
    public ScanInfo Create(string[] roots, ScanOptions options)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM scan";
            if (Convert.ToInt64(check.ExecuteScalar()) != 0)
                throw new InvalidOperationException("This database already contains a scan. Reopen it or choose a new database.");
            if (roots.Length == 0) throw new ArgumentException("Select at least one file or folder.");
            var normalized = roots.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var clone = JsonSerializer.Deserialize<ScanOptions>(JsonSerializer.Serialize(options))!;
            clone.Algorithms = clone.Algorithms.Select(a => a.Replace("-", "").ToUpperInvariant()).Distinct().ToArray();
            if (clone.Algorithms.Any(a => a is not ("SHA256" or "SHA512" or "SHA1" or "MD5")))
                throw new ArgumentException("Supported algorithms: SHA256, SHA512, SHA1 and MD5.");
            Info = new ScanInfo { Roots = normalized, Options = clone };
            HasScan = true;
            SaveInfo();
            return Info;
        }
    }

    public void SaveInfo()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO scan(id,payload) VALUES(1,$p) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
            command.Parameters.AddWithValue("$p", JsonSerializer.Serialize(Info));
            command.ExecuteNonQuery();
        }
    }

    public void SaveResult(FileResult result)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            SaveResultInternal(result, null, 1);
        }
    }

    private void SaveResultInternal(FileResult result, SqliteTransaction? tx, int seen)
    {
        result.ScanId = Info.ScanId;
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO files(path_key,file_id,kind,status,disk_serial,disk_number,seen,payload)
            VALUES($path,$id,$kind,$status,$diskSerial,$diskNumber,$seen,$payload)
            ON CONFLICT(path_key) DO UPDATE SET
                file_id=excluded.file_id,kind=excluded.kind,status=excluded.status,
                disk_serial=excluded.disk_serial,disk_number=excluded.disk_number,
                seen=excluded.seen,payload=excluded.payload
            """;
        command.Parameters.AddWithValue("$path", PathKey(result.FullPath));
        command.Parameters.AddWithValue("$id", result.FileId);
        command.Parameters.AddWithValue("$kind", result.Kind);
        command.Parameters.AddWithValue("$status", result.ScanStatus);
        command.Parameters.AddWithValue("$diskSerial", result.DiskSerialNumber);
        command.Parameters.AddWithValue("$diskNumber", result.DiskNumber);
        command.Parameters.AddWithValue("$seen", seen);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(result));
        command.ExecuteNonQuery();
    }

    // Returns only rows actually inserted/refreshed, so inventory totals never count
    // overlapping roots or duplicate paths twice.
    public List<FileResult> SaveDiscoveries(IReadOnlyList<FileResult> batch)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var tx = connection.BeginTransaction();
            var saved = new List<FileResult>(batch.Count);
            foreach (var row in batch)
            {
                using var query = connection.CreateCommand();
                query.Transaction = tx;
                query.CommandText = "SELECT file_id,seen FROM files WHERE path_key=$path";
                query.Parameters.AddWithValue("$path", PathKey(row.FullPath));
                using var reader = query.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetInt32(1) == 1) continue;
                    row.FileId = reader.GetString(0);
                }
                reader.Close();
                SaveResultInternal(row, tx, 1);
                saved.Add(row);
            }
            tx.Commit();
            return saved;
        }
    }

    public List<FileResult> GetPage(string kind, int offset, int limit) => GetPage(new ResultFilter(kind), offset, limit);

    public List<FileResult> GetPage(ResultFilter filter, int offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM files WHERE 1=1" + FilterClause(command, filter) + " ORDER BY seq LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
            using var reader = command.ExecuteReader();
            var results = new List<FileResult>();
            while (reader.Read()) results.Add(Deserialize(reader.GetString(0)));
            return results;
        }
    }

    public long Count(string kind = "All") => Count(new ResultFilter(kind));

    public long Count(ResultFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files WHERE 1=1" + FilterClause(command, filter);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    public List<string> GetDiskNumbers()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT disk_number FROM files ORDER BY disk_number COLLATE BINARY";
            using var reader = command.ExecuteReader();
            var disks = new List<string>();
            while (reader.Read()) disks.Add(reader.GetString(0));
            return disks;
        }
    }

    public long CountStatus(string status)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files WHERE status=$s";
            command.Parameters.AddWithValue("$s", status);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    public void RebuildInventoryTotals()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            long totalFiles = 0, totalBytes = 0, imageFiles = 0, imageBytes = 0;
            long videoFiles = 0, videoBytes = 0, otherFiles = 0, otherBytes = 0;
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT kind,COUNT(*),COALESCE(SUM(CAST(json_extract(payload,'$.SizeBytes') AS INTEGER)),0)
                FROM files GROUP BY kind;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string kind = reader.GetString(0);
                long count = reader.GetInt64(1), size = reader.GetInt64(2);
                totalFiles += count;
                totalBytes += size;
                switch (kind)
                {
                    case "Image": imageFiles += count; imageBytes += size; break;
                    case "Video": videoFiles += count; videoBytes += size; break;
                    default: otherFiles += count; otherBytes += size; break;
                }
            }
            Info.TotalFiles = totalFiles;
            Info.TotalBytes = totalBytes;
            Info.ImageFiles = imageFiles;
            Info.ImageBytes = imageBytes;
            Info.VideoFiles = videoFiles;
            Info.VideoBytes = videoBytes;
            Info.OtherFiles = otherFiles;
            Info.OtherBytes = otherBytes;
        }
    }

    public long BytesForStatus(string status)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(CAST(json_extract(payload,'$.SizeBytes') AS INTEGER)),0) FROM files WHERE status=$status";
            command.Parameters.AddWithValue("$status", status);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    public long IssueCount(string? stage = null)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM issues" + (stage == null ? "" : " WHERE stage=$s");
            if (stage != null) command.Parameters.AddWithValue("$s", stage);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    public IEnumerable<FileResult> Results() => ReadResults(null, ResultFilter.All);
    public IEnumerable<FileResult> Results(ResultFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return ReadResults(null, filter);
    }
    public IEnumerable<FileResult> QueuedResults() => ReadResults("Queued", ResultFilter.All);

    private IEnumerable<FileResult> ReadResults(string? status, ResultFilter filter)
    {
        long after = 0;
        // Keyset paging never holds a reader or connection lock across a yield/await.
        while (true)
        {
            var batch = new List<(long seq, FileResult row)>();
            lock (gate)
            {
                ThrowIfDisposed();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT seq,payload FROM files WHERE seq>$after" +
                    (status == null ? "" : " AND status=$status") + FilterClause(command, filter) + " ORDER BY seq LIMIT 256";
                command.Parameters.AddWithValue("$after", after);
                if (status != null) command.Parameters.AddWithValue("$status", status);
                using var reader = command.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetInt64(0), Deserialize(reader.GetString(1))));
            }
            if (batch.Count == 0) yield break;
            foreach (var (seq, row) in batch) { after = seq; yield return row; }
        }
    }

    public void PrepareResume(CancellationToken cancellationToken, Action<long>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(0);
        lock (gate)
        {
            // One atomic invalidation prevents a cancellation or crash halfway through
            // resetting rows from exposing old hashes as successes in the new pass.
            // SQLite performs the update on disk; no complete result list is materialized.
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                UPDATE files SET seen=0,status='Queued',disk_serial='',disk_number='',payload=json_set(payload,
                    '$.ScanStatus','Queued',
                    '$.VolumeRoot','','$.DiskSerialNumber','','$.DiskNumber','','$.DiskSerialStatus','NotQueried',
                    '$.HashStatus',CASE WHEN $hash=1 THEN 'Queued' ELSE 'NotRequested' END,
                    '$.MetadataStatus',CASE WHEN $metadata=1 AND kind!='Other' THEN 'Queued' ELSE 'NotRequested' END,
                    '$.FingerprintStatus',CASE WHEN $fingerprint=1 AND kind!='Other' THEN 'Queued' ELSE 'NotRequested' END,
                    '$.StreamHashStatus',CASE WHEN $stream=1 AND kind='Video' THEN 'Queued' ELSE 'NotRequested' END,
                    '$.SizeBytes',NULL,'$.CreatedTimeUtc',NULL,'$.LastWriteTimeUtc',NULL,
                    '$.Hashes',json('{}'),'$.Media',json('{}'),'$.Streams',json('[]'),
                    '$.Frames',json('[]'),'$.StreamHashes',json('[]'),'$.Errors',json('[]'),
                    '$.ComputedUtc',NULL,'$.Provenance','FreshAfterResume','$.ReuseValidation',NULL,
                    '$.SHA256','','$.ErrorMessage','');
                DELETE FROM issues;
                """;
            command.Parameters.AddWithValue("$hash", Info.Options.Algorithms.Length > 0 ? 1 : 0);
            command.Parameters.AddWithValue("$metadata", Info.Options.Metadata ? 1 : 0);
            command.Parameters.AddWithValue("$fingerprint", Info.Options.Fingerprints ? 1 : 0);
            command.Parameters.AddWithValue("$stream", Info.Options.StreamHashes ? 1 : 0);
            command.ExecuteNonQuery();
            tx.Commit();
        }
        progress?.Invoke(Count());
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void MarkUndiscovered()
    {
        long after = 0;
        while (true)
        {
            lock (gate)
            {
                using var query = connection.CreateCommand();
                query.CommandText = "SELECT seq,payload FROM files WHERE seen=0 AND seq>$after ORDER BY seq LIMIT 256";
                query.Parameters.AddWithValue("$after", after);
                var batch = new List<(long seq, FileResult row)>();
                using (var reader = query.ExecuteReader())
                    while (reader.Read()) batch.Add((reader.GetInt64(0), Deserialize(reader.GetString(1))));
                if (batch.Count == 0) break;
                using var tx = connection.BeginTransaction();
                foreach (var item in batch)
                {
                    var row = item.row;
                    row.ScanStatus = "Failed";
                    if (row.HashStatus != "NotRequested") row.HashStatus = "Failed";
                    if (row.MetadataStatus != "NotRequested") row.MetadataStatus = "Failed";
                    if (row.FingerprintStatus != "NotRequested") row.FingerprintStatus = "Failed";
                    if (row.StreamHashStatus != "NotRequested") row.StreamHashStatus = "Failed";
                    row.Errors.Add(new StageError { Stage = "Enumeration", Code = "NotRediscovered",
                        Message = "Previously discovered path was not rediscovered. It may have disappeared, become inaccessible, or been excluded. Previous results were invalidated." });
                    SaveResultInternal(row, tx, 0);
                    after = item.seq;
                }
                tx.Commit();
            }
        }
    }

    public void AddIssue(ScanIssue issue)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO issues(path,stage,code,message) VALUES($p,$s,$c,$m)";
            command.Parameters.AddWithValue("$p", issue.Path);
            command.Parameters.AddWithValue("$s", issue.Stage);
            command.Parameters.AddWithValue("$c", issue.Code);
            command.Parameters.AddWithValue("$m", issue.Message);
            command.ExecuteNonQuery();
        }
    }

    public List<ScanIssue> GetIssues() => Issues().ToList();
    public IEnumerable<ScanIssue> Issues()
    {
        long after = 0;
        while (true)
        {
            var batch = new List<(long seq, ScanIssue issue)>();
            lock (gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT seq,path,stage,code,message FROM issues WHERE seq>$after ORDER BY seq LIMIT 256";
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetInt64(0), new ScanIssue(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))));
            }
            if (batch.Count == 0) yield break;
            foreach (var (seq, issue) in batch) { after = seq; yield return issue; }
        }
    }

    public static FileResult NewQueued(string path, string kind, ScanInfo info) => new()
    {
        ScanId = info.ScanId, FullPath = path, FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path), Kind = kind,
        HashStatus = info.Options.Algorithms.Length > 0 ? "Queued" : "NotRequested",
        MetadataStatus = info.Options.Metadata && kind != "Other" ? "Queued" : "NotRequested",
        FingerprintStatus = info.Options.Fingerprints && kind != "Other" ? "Queued" : "NotRequested",
        StreamHashStatus = info.Options.StreamHashes && kind == "Video" ? "Queued" : "NotRequested",
        ConfigurationJson = JsonSerializer.Serialize(info.Options)
    };

    /// <summary>Resolve existing links/junctions, including ancestors, using Windows handles.
    /// For a new output filename resolve its nearest existing ancestor and append missing segments.</summary>
    public static string ResolvePhysicalPath(string path)
    {
        var current = NormalizePath(path);
        var suffix = new Stack<string>();
        while (true)
        {
            using var handle = CreateFileW(current, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var buffer = new System.Text.StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity)
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                var resolved = buffer.ToString();
                while (suffix.Count > 0) resolved = Path.Combine(resolved, suffix.Pop());
                return NormalizePath(resolved);
            }
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error is not (2 or 3)) throw new System.ComponentModel.Win32Exception(error);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                throw new System.ComponentModel.Win32Exception(error);
            suffix.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    public static bool SameExistingFile(string first, string second)
    {
        using var a = CreateFileW(first, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        using var b = CreateFileW(second, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (a.IsInvalid || b.IsInvalid) return false;
        if (!GetFileInformationByHandle(a, out var ai) || !GetFileInformationByHandle(b, out var bi))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        return ai.VolumeSerialNumber == bi.VolumeSerialNumber &&
            ai.FileIndexHigh == bi.FileIndexHigh && ai.FileIndexLow == bi.FileIndexLow;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle handle, System.Text.StringBuilder path, uint count, uint flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleInfo info);

    public static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static string PathKey(string path) => NormalizePath(path).ToUpperInvariant();
    public static bool IsWithin(string path, string root)
    {
        var p = NormalizePath(path); var r = NormalizePath(root);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(Path.EndsInDirectorySeparator(r) ? r : r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string FilterClause(SqliteCommand command, ResultFilter filter)
    {
        var clause = "";
        if (filter.Kind != "All")
        {
            clause += " AND kind=$kind";
            command.Parameters.AddWithValue("$kind", filter.Kind);
        }
        if (filter.DiskNumber is not null)
        {
            clause += " AND disk_number=$diskNumber";
            command.Parameters.AddWithValue("$diskNumber", filter.DiskNumber);
        }
        return clause;
    }
    private static FileResult Deserialize(string json) => JsonSerializer.Deserialize<FileResult>(json) ?? throw new InvalidDataException("Invalid result record.");
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private void EnsureColumn(string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using (var reader = check.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }
    private void Execute(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            connection.Dispose();
            disposed = true;
        }
    }
}




