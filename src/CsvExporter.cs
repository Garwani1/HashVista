// SPDX-License-Identifier: GPL-3.0-or-later
// CSV field quoting, UTF-8 BOM, CRLF, known-zero/unknown-size distinction,
// computed-result export and retained error rows are ported from supplied
// OpenHashTab 3.1.1 CSV1 CsvWriter.h and CSVExporter in Exporter.cpp.
// OpenHashTab copyright 2019-2025 namazso <admin@namazso.eu>.
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HashVista;

public static class CsvExporter
{
    private static readonly string[] InventoryColumns =
    [
        "ScanId", "FileId", "FileName", "Extension", "FullPath", "VolumeRoot",
        "DiskSerialNumber", "DiskNumber", "DiskSerialStatus", "Kind", "SizeBytes",
        "CreatedTimeUtc", "LastWriteTimeUtc", "ScanStatus", "HashStatus", "MetadataStatus",
        "FingerprintStatus", "StreamHashStatus", "SHA256", "SHA512", "SHA1", "MD5",
        "ComputedUtc", "Provenance", "ReuseValidation", "ConfigurationJson",
        "ErrorStage", "ErrorCode", "ErrorMessage", "ErrorsJson"
    ];
    private static readonly string[] MediaColumns =
    [
        "Format", "Width", "Height", "AspectRatio", "Orientation", "CaptureTime", "CaptureTimeOffset",
        "CameraMake", "CameraModel", "Lens", "Description", "GPSLatitude", "GPSLongitude",
        "PixelSource", "ImagePixelSHA256", "PixelProfile", "PerceptualHash", "PerceptualAlgorithm",
        "PerceptualBits", "FingerprintProfile", "DurationSeconds", "Container", "VideoCodec",
        "AudioCodec", "FrameRate", "AverageFrameRate", "BitRate", "AudioSampleRate", "AudioChannels",
        "Title", "CreationTime", "SamplingProfile", "ToolVersions", "StreamHashProfile"
    ];
    private static readonly string[] DetailColumns =
    [
        "MediaJson", "StreamsJson", "FramesJson", "StreamHashesJson",
        "ExportScope", "ScanCompleteness", "EnumerationComplete", "EnumerationIssueCount",
        "ExclusionCount", "ScanCreatedUtc", "ScanFinishedUtc", "ScanOptionsJson",
        "ExportFilterKind", "ExportFilterDiskNumber", "ExportFilterJson"
    ];
    public static string[] Columns => [.. InventoryColumns, .. MediaColumns, .. DetailColumns];

    public static long Export(ScanStore store, string path) => Export(store, path, ResultFilter.All);

    public static long Export(ScanStore store, string path, ResultFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (store.Info.Status is "Scanning" or "Enumerating" or "Resuming" or "Paused")
            throw new InvalidOperationException("Wait for the scan to finish or cancel it before exporting a consistent report.");
        path = Path.GetFullPath(path);
        ValidateDestination(store, path);
        ValidateDestination(store, path + ".scan.json");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var companion = path + ".scan.json";
        var companionTemp = companion + ".tmp-" + Guid.NewGuid().ToString("N");
        var info = JsonSerializer.Deserialize<ScanInfo>(JsonSerializer.Serialize(store.Info))!;
        string scope = filter.IsAll ? "All" : "Filtered";
        long count = 0;
        long issueCount = store.IssueCount("Enumeration");
        long exclusionCount = store.IssueCount("Excluded");
        try
        {
            using (var output = new StreamWriter(new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(true), 64 * 1024))
            {
                output.NewLine = "\r\n";
                WriteRow(output, Columns);
                foreach (var row in store.Results(filter))
                {
                    WriteRow(output, Values(row, info, scope, issueCount, exclusionCount, filter));
                    count++;
                }
            }
            using (var stream = new FileStream(companionTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("Schema", "HashVista-ScanExport-v1");
                json.WriteString("ExportScope", scope);
                json.WritePropertyName("Filter");
                JsonSerializer.Serialize(json, new { filter.Kind, filter.DiskNumber });
                json.WriteNumber("ExportedRows", count);
                json.WriteString("ExportedUtc", DateTime.UtcNow);
                json.WritePropertyName("Scan");
                JsonSerializer.Serialize(json, info);
                json.WritePropertyName("Issues");
                json.WriteStartArray();
                foreach (var issue in store.Issues()) JsonSerializer.Serialize(json, issue);
                json.WriteEndArray();
                json.WriteEndObject();
                json.Flush();
                stream.Flush(true);
            }
            // Never leave a partly-written CSV at the chosen filename.
            File.Move(companionTemp, companion, true);
            File.Move(temp, path, true);
            return count;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(companionTemp)) File.Delete(companionTemp);
        }
    }

    public static void ValidateDestination(ScanStore store, string path)
    {
        var destination = Path.GetFullPath(path);
        var physical = ScanStore.ResolvePhysicalPath(destination);
        foreach (var root in store.Info.Roots)
        {
            if (ScanStore.IsWithin(destination, root) || ScanStore.IsWithin(physical, ScanStore.ResolvePhysicalPath(root)) ||
                (File.Exists(root) && ScanStore.SameExistingFile(destination, root)))
                throw new InvalidOperationException("Choose an export location outside every selected scan root, including link/junction targets.");
        }
        foreach (var database in new[] { store.DatabasePath, store.DatabasePath + "-wal", store.DatabasePath + "-shm", store.DatabasePath + "-journal" })
            if (physical.Equals(ScanStore.ResolvePhysicalPath(database), StringComparison.OrdinalIgnoreCase) ||
                ScanStore.SameExistingFile(destination, database))
                throw new InvalidOperationException("The export destination cannot replace the scan database or a physical alias of it.");
    }

    public static string ClipboardText(IEnumerable<FileResult> rows, ScanInfo info)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\r\n" };
        WriteRow(output, Columns);
        foreach (var row in rows) WriteRow(output, Values(row, info, "Selected", null, null));
        return output.ToString();
    }

    public static void WriteRow(TextWriter writer, IEnumerable<string?> values)
    {
        bool first = true;
        foreach (var value in values)
        {
            if (!first) writer.Write(',');
            first = false;
            writer.Write('"');
            // Port of CSV1's quote-every-field algorithm: embedded quotes double;
            // commas, Arabic text and embedded CR/LF retain their exact contents.
            writer.Write((value ?? "").Replace("\"", "\"\""));
            writer.Write('"');
        }
        writer.Write("\r\n");
    }

    private static IEnumerable<string?> Values(FileResult row, ScanInfo info, string scope, long? enumerationIssueCount, long? excludedCount, ResultFilter? filter = null)
    {
        var values = new List<string?>
        {
            row.ScanId, row.FileId, row.FileName, row.Extension, row.FullPath, row.VolumeRoot,
            row.DiskSerialNumber, row.DiskNumber, row.DiskSerialStatus, row.Kind,
            row.SizeBytes?.ToString(CultureInfo.InvariantCulture),
            Utc(row.CreatedTimeUtc), Utc(row.LastWriteTimeUtc), row.ScanStatus, row.HashStatus,
            row.MetadataStatus, row.FingerprintStatus, row.StreamHashStatus,
            row.Hashes.GetValueOrDefault("SHA256"), row.Hashes.GetValueOrDefault("SHA512"),
            row.Hashes.GetValueOrDefault("SHA1"), row.Hashes.GetValueOrDefault("MD5"),
            Utc(row.ComputedUtc), row.Provenance, row.ReuseValidation, row.ConfigurationJson,
            string.Join("\n", row.Errors.Select(e => e.Stage)),
            string.Join("\n", row.Errors.Select(e => e.Code)),
            string.Join("\n", row.Errors.Select(e => e.Message)),
            JsonSerializer.Serialize(row.Errors)
        };
        foreach (var column in MediaColumns) values.Add(row.Media.GetValueOrDefault(column));
        values.AddRange([
            JsonSerializer.Serialize(row.Media), JsonSerializer.Serialize(row.Streams),
            JsonSerializer.Serialize(row.Frames), JsonSerializer.Serialize(row.StreamHashes),
            scope, info.Status, info.EnumerationComplete ? "true" : "false",
            enumerationIssueCount?.ToString(CultureInfo.InvariantCulture),
            excludedCount?.ToString(CultureInfo.InvariantCulture),
            Utc(info.CreatedUtc), Utc(info.FinishedUtc), JsonSerializer.Serialize(info.Options),
            filter?.Kind, filter?.DiskNumber,
            filter is null ? null : JsonSerializer.Serialize(new { filter.Kind, filter.DiskNumber })
        ]);
        return values;
    }
    private static string? Utc(DateTime? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}



