// SPDX-License-Identifier: GPL-3.0-or-later
// Reuses the bounded-block, same-read multi-hash processing and retained failure
// semantics of OpenHashTab 3.1.1 / CSV1 FileHashTask (namazso, 2019-2025).
// Native shell/COM code and comparison behavior are intentionally not ported.
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HashVista;

public sealed class ScanEngine
{
    private readonly ScanStore store;
    private readonly MediaProcessor media;
    private readonly DiskIdentityResolver disks = new();
    private readonly string logicalApplication, logicalDatabase, physicalApplication, physicalDatabase;
    private long discovered, processed, failed, skipped, bytes;
    private long handledBytes, currentFileBytes;
    private IProgress<ScanProgress>? progress;
    private ScanControl? control;
    private string? pausedStatus;
    private readonly Stopwatch reportClock = Stopwatch.StartNew();
    private bool enumerationErrors, resumed;
    public ScanEngine(ScanStore store, MediaProcessor media) { this.store = store; this.media = media; logicalApplication = ScanStore.NormalizePath(AppContext.BaseDirectory); logicalDatabase = ScanStore.NormalizePath(store.DatabasePath); physicalApplication = ScanStore.ResolvePhysicalPath(AppContext.BaseDirectory); physicalDatabase = ScanStore.ResolvePhysicalPath(store.DatabasePath); }

    public Task RunAsync(bool resume, IProgress<ScanProgress>? progress, CancellationToken cancellationToken, ScanControl? control = null)
        => Task.Run(() => RunCoreAsync(resume, progress, cancellationToken, control), CancellationToken.None);

    private async Task RunCoreAsync(bool resume, IProgress<ScanProgress>? reporter, CancellationToken token, ScanControl? scanControl)
    {
        progress = reporter; resumed = resume; control = scanControl;
        discovered = processed = failed = skipped = bytes = handledBytes = currentFileBytes = 0;
        enumerationErrors = false;
        var info = store.Info;
        info.Status = resume ? "Resuming" : "Enumerating";
        info.EnumerationComplete = false;
        info.FinishedUtc = null;
        store.SaveInfo();
        Report("Preparing file inventory…", true);
        FileResult? active = null;
        try
        {
            if (resume)
            {
                store.PrepareResume(token, n => Report("Invalidating previous results: " + n, true));
            }
            else if (store.Count() != 0)
                throw new InvalidOperationException("Existing results require Resume.");
            ResetInventoryTotals(info);
            info.Status = "Enumerating";
            store.SaveInfo();
            Report("Inventory started. No hashing is performed until enumeration finishes.", true);
            Checkpoint(token);
            Enumerate(token);
            Checkpoint(token);
            if (resume) store.MarkUndiscovered();
            store.RebuildInventoryTotals();
            info.EnumerationComplete = !enumerationErrors;
            info.Status = "Scanning";
            store.SaveInfo();
            discovered = info.TotalFiles;
            failed = store.CountStatus("Failed");
            processed = failed;
            handledBytes = store.BytesForStatus("Failed");
            Report($"Inventory complete: {info.TotalFiles:N0} files, {info.TotalBytes:N0} bytes. Hashing started.", true);
            foreach (var row in store.QueuedResults())
            {
                Checkpoint(token);
                active = row;
                currentFileBytes = 0;
                row.ScanStatus = "Scanning";
                // Fold the Hashing transition into the existing start-of-file write.
                // Hash-only scans still perform just this write and the final result write.
                if (store.Info.Options.Algorithms.Length > 0) row.HashStatus = "Hashing";
                store.SaveResult(row);
                Report("Processing: " + row.FullPath, true);
                await ProcessFileAsync(row, token).ConfigureAwait(false);
                row.ScanStatus = row.Errors.Count == 0 ? "Completed" :
                    row.HashStatus == "ChangedDuringScan" ? "ChangedDuringScan" :
                    row.HashStatus == "Failed" ? "Failed" : "CompletedWithErrors";
                store.SaveResult(row);
                processed++;
                if (row.Errors.Count > 0) failed++;
                handledBytes += Math.Max(0, row.SizeBytes ?? currentFileBytes);
                currentFileBytes = 0;
                active = null;
                Report("Completed: " + row.FullPath, true);
                Checkpoint(token);
            }
            info.Status = !info.EnumerationComplete ? "Partial" :
                info.TotalFiles == 0 ? "NoFilesFound" :
                failed > 0 || skipped > 0 ? "CompletedWithIssues" : "Completed";
            info.FinishedUtc = DateTime.UtcNow;
            if (info.Status is "Completed" or "CompletedWithIssues") handledBytes = info.TotalBytes;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (active != null)
            {
                active.ScanStatus = "Cancelled";
                foreach (var stage in new[] { "Hash", "Metadata", "Fingerprint", "StreamHash" })
                    SetCancelledIfPending(active, stage);
                active.Errors.Add(new StageError { Stage = "Scan", Code = "Cancelled", Message = "Cancelled by user. Completed stages are retained; queued stages were not completed." });
                store.SaveResult(active);
            }
            info.Status = "Cancelled";
            info.FinishedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            info.Status = "Failed";
            info.FinishedUtc = DateTime.UtcNow;
            store.AddIssue(new ScanIssue(active?.FullPath ?? "", "Scan", ErrorCode(ex), ex.Message));
            if (active != null)
            {
                active.ScanStatus = "Failed";
                active.Errors.Add(new StageError { Stage = "Scan", Code = ErrorCode(ex), Message = ex.Message });
                store.SaveResult(active);
            }
            throw;
        }
        finally
        {
            // A cancelled resume retains old queued rows as well as rediscovered rows.
            // Refresh once at the boundary instead of counting the whole table per file.
            if (!info.EnumerationComplete) store.RebuildInventoryTotals();
            discovered = info.TotalFiles;
            store.SaveInfo();
            Report(info.Status, true);
        }
    }

    private void Enumerate(CancellationToken token)
    {
        var options = store.Info.Options;
        var directories = new Stack<(string path, int depth)>();
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<FileResult>(512);
        var batchClock = Stopwatch.StartNew();
        void Flush()
        {
            if (batch.Count == 0) return;
            var saved = store.SaveDiscoveries(batch);
            foreach (var row in saved) AddInventoryTotals(store.Info, row);
            discovered += saved.Count;
            batch.Clear();
            store.SaveInfo();
            batchClock.Restart();
            Report($"Inventory: {discovered:N0} files, {store.Info.TotalBytes:N0} bytes discovered", true);
        }
        void FlushIfDue()
        {
            if (batch.Count >= 512 || (batch.Count > 0 && batchClock.ElapsedMilliseconds >= 1000)) Flush();
        }
        void InventoryCheckpoint()
        {
            // Make the current bounded inventory batch visible before acknowledging
            // pause, even when fewer than 512 entries have been discovered.
            if (control?.PauseRequested == true) Flush();
            Checkpoint(token);
        }
        void QueueFile(string path, string? physicalPath = null)
        {
            if (physicalPath != null && IsApplicationPhysicalPath(physicalPath))
            {
                Exclude(path, "ApplicationData", "Scanner application files, cache and database are excluded.");
                return;
            }
            var row = ScanStore.NewQueued(path, Classify(path), store.Info);
            ApplyDiskIdentity(row, physicalPath ?? path);
            row.Provenance = resumed ? "FreshAfterResume" : "Fresh";
            try
            {
                // For a leaf symbolic link, inventory the target rather than the
                // link object's zero-byte metadata. Ordinary paths need no extra resolution.
                var file = new FileInfo(physicalPath ?? path);
                file.Refresh();
                if (file.Exists)
                {
                    row.SizeBytes = file.Length;
                    row.CreatedTimeUtc = file.CreationTimeUtc;
                    row.LastWriteTimeUtc = file.LastWriteTimeUtc;
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // Keep the discovered row; the processing pass will retain the failure.
                store.AddIssue(new ScanIssue(path, "InventorySize", ErrorCode(ex), ex.Message));
            }
            batch.Add(row);
            FlushIfDue();
        }
        void QueueFailure(string path, Exception ex)
        {
            // Never retry an unresolved reparse point through a guessed parent path:
            // that could follow a link into the application's own database.
            var row = ScanStore.NewQueued(path, Classify(path), store.Info);
            row.Provenance = resumed ? "FreshAfterResume" : "Fresh";
            row.ScanStatus = "Failed";
            if (row.HashStatus != "NotRequested") row.HashStatus = "Failed";
            if (row.MetadataStatus != "NotRequested") row.MetadataStatus = "Failed";
            if (row.FingerprintStatus != "NotRequested") row.FingerprintStatus = "Failed";
            if (row.StreamHashStatus != "NotRequested") row.StreamHashStatus = "Failed";
            row.Errors.Add(new StageError { Stage = "Inventory", Code = ErrorCode(ex), Message = ex.Message });
            batch.Add(row);
            FlushIfDue();
        }
        bool Allowed(string path, FileAttributes attr, bool isExplicitRoot = false)
        {
            if (IsApplicationPath(path)) { Exclude(path, "ApplicationData", "Scanner application files, cache and database are excluded."); return false; }
            // The user explicitly chose this root. Windows volumes can themselves
            // carry Hidden/System, so applying descendant filters here can skip
            // the entire drive before the first file is discovered.
            if (!isExplicitRoot && !options.IncludeHidden && attr.HasFlag(FileAttributes.Hidden)) { Exclude(path, "Hidden", "Hidden entry excluded by scan settings."); return false; }
            if (!isExplicitRoot && !options.IncludeSystem && attr.HasFlag(FileAttributes.System)) { Exclude(path, "System", "System entry excluded by scan settings."); return false; }
            if (!options.FollowLinks && attr.HasFlag(FileAttributes.ReparsePoint)) { Exclude(path, "Link", "Link/reparse point excluded by scan settings."); return false; }
            return true;
        }
        try
        {
            foreach (var root in store.Info.Roots)
            {
                InventoryCheckpoint();
                try
                {
                    var attr = File.GetAttributes(root);
                    if (!Allowed(root, attr, isExplicitRoot: true)) continue;
                    if (attr.HasFlag(FileAttributes.Directory)) directories.Push((root, 0));
                    else QueueFile(root, ScanStore.ResolvePhysicalPath(root));
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    // An explicitly selected inaccessible/missing file still receives a row.
                    if (!Directory.Exists(root))
                    {
                        QueueFailure(root, ex);
                    }
                    EnumerationError(root, ex);
                }
            }
            while (directories.Count > 0)
            {
                InventoryCheckpoint();
                var (directory, depth) = directories.Pop();
                string physical;
                try { physical = ScanStore.ResolvePhysicalPath(directory); }
                catch (Exception ex) when (IsFileError(ex) || ex is System.ComponentModel.Win32Exception)
                { EnumerationError(directory, ex); continue; }
                if (ScanStore.IsWithin(physical, physicalApplication))
                {
                    Exclude(directory, "ApplicationData", "Scanner application directory is excluded.");
                    continue;
                }
                if (!visitedDirectories.Add(physical))
                {
                    Exclude(directory, "AlreadyVisitedDirectory", "Directory target already visited (overlapping root or link loop).");
                    continue;
                }
                Report("Enumerating: " + directory, true);
                try
                {
                    // Enumerate lazily: no Directory.GetFiles array for a huge archive.
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        InventoryCheckpoint();
                        // Keep slow inventories visible without a task per file or
                        // a concurrent hashing pipeline. Report itself throttles to 200 ms.
                        FlushIfDue();
                        Report("Enumerating: " + directory);
                        try
                        {
                            var attr = File.GetAttributes(entry);
                            if (!Allowed(entry, attr)) continue;
                            if (attr.HasFlag(FileAttributes.Directory))
                            {
                                if (options.Recursive) directories.Push((entry, depth + 1));
                                else Exclude(entry, "NonRecursive", "Subdirectory excluded because recursive scanning is disabled.");
                            }
                            else
                            {
                                // Resolve the leaf only for reparse points. Joining the
                                // already-resolved parent stays the fast path for ordinary files.
                                var target = attr.HasFlag(FileAttributes.ReparsePoint)
                                    ? ScanStore.ResolvePhysicalPath(entry)
                                    : Path.Combine(physical, Path.GetFileName(entry));
                                QueueFile(ScanStore.NormalizePath(entry), target);
                            }
                        }
                        catch (Exception ex) when (IsFileError(ex))
                        {
                            // The name was discovered even if attributes cannot be read.
                            QueueFailure(ScanStore.NormalizePath(entry), ex);
                            EnumerationError(entry, ex);
                        }
                    }
                }
                catch (Exception ex) when (IsFileError(ex))
                { EnumerationError(directory, ex); }
            }
        }
        finally { Flush(); }
    }

    private async Task ProcessFileAsync(FileResult row, CancellationToken token)
    {
        FileInfo before;
        string metadataPath = row.FullPath;
        bool leafLink = false;
        try
        {
            before = new FileInfo(row.FullPath);
            before.Refresh();
            if (!before.Exists) throw new FileNotFoundException("The file no longer exists or cannot be accessed.", row.FullPath);
            leafLink = before.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (leafLink)
            {
                if (!store.Info.Options.FollowLinks) throw new IOException("The file became a link after inventory and following links is disabled.");
                metadataPath = ScanStore.ResolvePhysicalPath(row.FullPath);
                if (IsApplicationPhysicalPath(metadataPath)) throw new IOException("The link now targets the scanner application or its active database and cannot be processed.");
                before = new FileInfo(metadataPath);
                before.Refresh();
                if (!before.Exists) throw new FileNotFoundException("The link target is no longer available.", row.FullPath);
                ApplyDiskIdentity(row, metadataPath);
            }
            row.SizeBytes = before.Length;
            row.CreatedTimeUtc = before.CreationTimeUtc;
            row.LastWriteTimeUtc = before.LastWriteTimeUtc;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            row.Errors.Add(new StageError { Stage = "Inventory", Code = ErrorCode(ex), Message = ex.Message });
            if (row.HashStatus != "NotRequested") row.HashStatus = "Failed";
            if (row.MetadataStatus != "NotRequested") row.MetadataStatus = "Failed";
            if (row.FingerprintStatus != "NotRequested") row.FingerprintStatus = "Failed";
            if (row.StreamHashStatus != "NotRequested") row.StreamHashStatus = "Failed";
            return;
        }
        if (store.Info.Options.Algorithms.Length > 0)
        {
            row.HashStatus = "Hashing";
            Report("Hashing: " + row.FullPath, true);
            try
            {
                var algorithms = new Dictionary<string, IncrementalHash>();
                byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                try
                {
                    foreach (var name in store.Info.Options.Algorithms)
                        algorithms.Add(name, IncrementalHash.CreateHash(new HashAlgorithmName(name)));
                    await using var input = new FileStream(row.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var lengthAtOpen = input.Length;
                    long readTotal = 0;
                    while (true)
                    {
                        Checkpoint(token);
                        int read = await input.ReadAsync(buffer.AsMemory(0, 1024 * 1024), token).ConfigureAwait(false);
                        if (read == 0) break;
                        foreach (var algorithm in algorithms.Values) algorithm.AppendData(buffer, 0, read);
                        readTotal += read; bytes += read; currentFileBytes += read;
                        Report($"Hashing {currentFileBytes:N0}/{row.SizeBytes ?? lengthAtOpen:N0} bytes: {row.FullPath}");
                    }
                    var afterPath = leafLink ? ScanStore.ResolvePhysicalPath(row.FullPath) : row.FullPath;
                    var after = new FileInfo(afterPath); after.Refresh();
                    if (!afterPath.Equals(metadataPath, StringComparison.OrdinalIgnoreCase) ||
                        !after.Exists || before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc ||
                        lengthAtOpen != before.Length || input.Length != lengthAtOpen || readTotal != lengthAtOpen)
                    {
                        row.HashStatus = "ChangedDuringScan";
                        row.Hashes.Clear();
                        row.Errors.Add(new StageError { Stage = "Hash", Code = "ChangedDuringScan",
                            Message = "Observable file size or last-write time changed during hashing. Digests were discarded. This metadata check is not a filesystem snapshot." });
                    }
                    else
                    {
                        foreach (var (name, hash) in algorithms)
                            row.Hashes[name] = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                        row.HashStatus = "Completed";
                        row.ComputedUtc = DateTime.UtcNow;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    foreach (var hash in algorithms.Values) hash.Dispose();
                }
            }
            catch (OperationCanceledException) { row.Hashes.Clear(); row.HashStatus = "Cancelled"; throw; }
            catch (Exception ex) when (IsFileError(ex) || ex is CryptographicException)
            {
                row.Hashes.Clear(); row.HashStatus = "Failed";
                row.Errors.Add(new StageError { Stage = "Hash", Code = ErrorCode(ex), Message = ex.Message });
            }
        }
        Checkpoint(token);
        if (HasMediaWork(row))
        {
            // Make the completed/failed hash durable before a potentially slow decoder.
            // No additional checkpoint is needed for hash-only files: the final write follows immediately.
            store.SaveResult(row);
            try { await media.ProcessAsync(row, store.Info.Options, token, message => { Checkpoint(token); Report(message); }).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                row.Errors.Add(new StageError { Stage = "Media", Code = ErrorCode(ex), Message = ex.Message });
                if (row.MetadataStatus is "Queued" or "Processing") row.MetadataStatus = "Failed";
                if (row.FingerprintStatus is "Queued" or "Processing") row.FingerprintStatus = "Failed";
                if (row.StreamHashStatus is "Queued" or "Processing") row.StreamHashStatus = "Failed";
            }
            // Do not mix media from a later observable file revision with a good file hash.
            try
            {
                var endPath = leafLink ? ScanStore.ResolvePhysicalPath(row.FullPath) : row.FullPath;
                var end = new FileInfo(endPath); end.Refresh();
                if (!endPath.Equals(metadataPath, StringComparison.OrdinalIgnoreCase) ||
                    !end.Exists || end.Length != before.Length || end.LastWriteTimeUtc != before.LastWriteTimeUtc)
                {
                    row.Errors.Add(new StageError { Stage = "Media", Code = "ChangedDuringScan",
                        Message = "The file changed during processing. Hash and media outputs cannot be treated as one snapshot." });
                    if (row.HashStatus == "Completed") { row.HashStatus = "ChangedDuringScan"; row.Hashes.Clear(); row.ComputedUtc = null; }
                    if (row.MetadataStatus == "Completed") row.MetadataStatus = "ChangedDuringScan";
                    if (row.FingerprintStatus == "Completed") row.FingerprintStatus = "ChangedDuringScan";
                    if (row.StreamHashStatus == "Completed") row.StreamHashStatus = "ChangedDuringScan";
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            { row.Errors.Add(new StageError { Stage = "Validation", Code = ErrorCode(ex), Message = ex.Message }); }
        }
    }

    private bool HasMediaWork(FileResult row) => row.Kind != "Other" &&
        (store.Info.Options.Metadata || store.Info.Options.Fingerprints ||
         (store.Info.Options.StreamHashes && row.Kind == "Video"));

    private void Checkpoint(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (control?.PauseRequested != true) return;
        string previousStatus = store.Info.Status;
        bool acknowledged = false;
        try
        {
            control.WaitForResume(token, () =>
            {
                acknowledged = true;
                pausedStatus = previousStatus;
                store.Info.Status = "Paused";
                store.SaveInfo();
                Report("Paused — Continue keeps the current scan and hash state. Cancel saves partial results.", true);
            });
        }
        finally
        {
            if (acknowledged && !token.IsCancellationRequested)
            {
                store.Info.Status = previousStatus;
                store.SaveInfo();
                Report("Continuing current scan…", true);
            }
            pausedStatus = null;
        }
        token.ThrowIfCancellationRequested();
    }

    private void ApplyDiskIdentity(FileResult row, string physicalPath)
    {
        var identity = disks.Resolve(physicalPath);
        row.VolumeRoot = identity.VolumeRoot;
        row.DiskSerialNumber = identity.DiskSerialNumber;
        row.DiskNumber = identity.DiskNumber;
        row.DiskSerialStatus = identity.Status;
        if (!store.Info.Disks.Any(x => x.VolumeRoot.Equals(identity.VolumeRoot, StringComparison.OrdinalIgnoreCase)))
            store.Info.Disks.Add(identity);
    }

    private bool IsApplicationPath(string path) =>
        ScanStore.IsWithin(path, logicalApplication) || IsDatabasePath(path, logicalDatabase);
    private bool IsApplicationPhysicalPath(string path) =>
        ScanStore.IsWithin(path, physicalApplication) || IsDatabasePath(path, physicalDatabase);
    private static bool IsDatabasePath(string path, string database) =>
        path.Equals(database, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(database + "-wal", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(database + "-shm", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(database + "-journal", StringComparison.OrdinalIgnoreCase);
    private void EnumerationError(string path, Exception ex)
    {
        enumerationErrors = true;
        store.AddIssue(new ScanIssue(path, "Enumeration", ErrorCode(ex), ex.Message));
    }
    private void Exclude(string path, string code, string message)
    {
        skipped++;
        store.AddIssue(new ScanIssue(path, "Excluded", code, message));
    }
    private void Report(string activity, bool force = false)
    {
        if (!force && reportClock.ElapsedMilliseconds < 200) return;
        reportClock.Restart();
        var info = store.Info;
        string phase = info.Status == "Paused" ? pausedStatus ?? "Paused" : info.Status;
        long progressBytes = handledBytes + currentFileBytes;
        double percentage = 0;
        bool finished = (info.Status is "Completed" or "CompletedWithIssues") &&
            info.FinishedUtc.HasValue && processed >= info.TotalFiles;
        if (finished) percentage = 100;
        else if (phase is "Scanning" or "Cancelled" or "Partial" or "Failed")
        {
            // Bytes can all be read while the last file's media stages still run.
            // Only the completed scan transition is allowed to publish 100%.
            if (info.TotalBytes > 0) percentage = Math.Clamp(progressBytes * 100d / info.TotalBytes, 0, 99.9);
            else if (info.TotalFiles > 0) percentage = Math.Clamp(processed * 100d / info.TotalFiles, 0, 99.9);
        }
        progress?.Report(new ScanProgress(discovered, processed, failed, skipped, bytes, activity,
            info.TotalFiles, info.TotalBytes, progressBytes, currentFileBytes, percentage));
    }
    private static void ResetInventoryTotals(ScanInfo info)
    {
        info.TotalFiles = info.TotalBytes = info.ImageFiles = info.ImageBytes = 0;
        info.VideoFiles = info.VideoBytes = info.OtherFiles = info.OtherBytes = 0;
        info.Disks.Clear();
    }
    private static void AddInventoryTotals(ScanInfo info, FileResult row)
    {
        long size = Math.Max(0, row.SizeBytes ?? 0);
        info.TotalFiles++;
        info.TotalBytes += size;
        switch (row.Kind)
        {
            case "Image": info.ImageFiles++; info.ImageBytes += size; break;
            case "Video": info.VideoFiles++; info.VideoBytes += size; break;
            default: info.OtherFiles++; info.OtherBytes += size; break;
        }
    }
    public static string Classify(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or
        ".nef" or ".nrw" or ".cr2" or ".cr3" or ".arw" or ".dng" or ".orf" or ".raf" or ".rw2" or ".pef" or
        ".srw" or ".raw" or ".heic" or ".heif" or ".avif" => "Image",
        ".mp4" or ".m4v" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".mpg" or ".mpeg" or
        ".mts" or ".m2ts" or ".ts" or ".3gp" or ".ogv" or ".flv" or ".vob" => "Video",
        _ => "Other"
    };
    private static bool IsFileError(Exception ex) => ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or NotSupportedException;
    private static string ErrorCode(Exception ex) => "0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    private static void SetCancelledIfPending(FileResult row, string stage)
    {
        static string Cancel(string value) => value is "Queued" or "Hashing" or "Processing" ? "Cancelled" : value;
        switch (stage)
        {
            case "Hash": row.HashStatus = Cancel(row.HashStatus); break;
            case "Metadata": row.MetadataStatus = Cancel(row.MetadataStatus); break;
            case "Fingerprint": row.FingerprintStatus = Cancel(row.FingerprintStatus); break;
            case "StreamHash": row.StreamHashStatus = Cancel(row.StreamHashStatus); break;
        }
    }

}


