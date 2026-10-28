using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HashVista;
using Microsoft.VisualBasic.FileIO;

internal static class Program
{
    private static readonly List<string> Checks = [];
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + message);
        Checks.Add(message);
        Console.WriteLine("PASS: " + message);
    }

    private static int Main(string[] args)
    {
        string stage = Path.GetFullPath(args[0]);
        string run = Path.Combine(stage, "evidence", "filtered-export130-runs", "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        string? failure = null;
        try { Run(run); }
        catch (Exception exception) { failure = exception.ToString(); Console.Error.WriteLine(failure); }
        string assembly = typeof(ScanStore).Assembly.Location;
        using var assemblyInput = File.OpenRead(assembly);
        var report = new
        {
            Passed = Checks.Count, Checks, Failure = failure, TimestampUtc = DateTime.UtcNow,
            Assembly = assembly, AssemblySHA256 = Convert.ToHexString(SHA256.HashData(assemblyInput)),
            RunDirectory = run,
            Scope = "901 synthetic stored rows plus one real locked source file; SQL filtering and stored-result export correctness, not a drive-scan benchmark"
        };
        File.WriteAllText(Path.Combine(stage, "evidence", "filtered-export130.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"RESULT: {Checks.Count} passed; failure={(failure is null ? "none" : "yes")}");
        return failure is null ? 0 : 1;
    }

    private static void Run(string run)
    {
        string input = Path.Combine(run, "input");
        Directory.CreateDirectory(input);
        string database = Path.Combine(run, "filter.ghscan");
        using (var store = new ScanStore(database))
        {
            store.Create([input], new ScanOptions { Algorithms = ["SHA256"] });
            var sourceRows = new List<FileResult>();
            string[] kinds = ["Image", "Video", "Other"];
            string[] disks = ["0", "1", "", "12"];
            for (int index = 0; index < 900; index++)
            {
                var row = new FileResult
                {
                    FileName = $"row-{index:0000}.bin", FullPath = Path.Combine(input, $"row-{index:0000}.bin"),
                    Kind = kinds[index % kinds.Length], DiskNumber = disks[index % disks.Length],
                    SizeBytes = index, ScanStatus = index % 17 == 0 ? "Failed" : "Completed",
                    HashStatus = index % 17 == 0 ? "Failed" : "Completed",
                    Hashes = index % 17 == 0 ? [] : new() { ["SHA256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index.ToString(System.Globalization.CultureInfo.InvariantCulture)))) }
                };
                if (row.ScanStatus == "Failed") row.Errors.Add(new StageError { Stage = "Hash", Code = "ControlledFailure", Message = "خطأ, تجريبي\r\n\"quoted\"" });
                sourceRows.Add(row);
            }
            sourceRows.Add(new FileResult
            {
                FileName = "حقن, حرفي.bin", FullPath = Path.Combine(input, "حقن, حرفي.bin"), Kind = "Other",
                DiskNumber = "0' OR 1=1 --", SizeBytes = 0, ScanStatus = "Failed", HashStatus = "Failed"
            });
            store.SaveDiscoveries(sourceRows);
            store.Info.Status = "Partial";
            store.Info.EnumerationComplete = false;
            store.RebuildInventoryTotals();
            store.AddIssue(new ScanIssue(input, "Enumeration", "Controlled", "Incomplete traversal for export provenance test"));
            store.SaveInfo();
            Check(store.Count() == sourceRows.Count, "all 901 synthetic rows stored independently including failures");
            Check(store.GetDiskNumbers().Order().SequenceEqual(disks.Append("0' OR 1=1 --").Order()), "disk selector values retain unknown and exact unusual identities");

            foreach (string kind in new[] { "All", "Image", "Video", "Other" })
            foreach (string? disk in new string?[] { null, "0", "1", "", "12", "0' OR 1=1 --", "not-present" })
            {
                var filter = new ResultFilter(kind, disk);
                var expected = sourceRows.Where(row => (kind == "All" || row.Kind == kind) && (disk is null || row.DiskNumber == disk)).Select(row => row.FileId).ToArray();
                var actual = store.Results(filter).Select(row => row.FileId).ToArray();
                var paged = new List<string>();
                for (int offset = 0; offset < expected.Length + 1; offset += 103)
                    paged.AddRange(store.GetPage(filter, offset, 103).Select(row => row.FileId));
                Check(store.Count(filter) == expected.Length && actual.SequenceEqual(expected) && paged.SequenceEqual(expected),
                    $"count, keyset enumeration, and offset pages agree: {kind}, disk={(disk is null ? "ALL" : "[" + disk + "]")}");
            }
            Check(store.Count(new ResultFilter("Image' OR 1=1 --")) == 0 && store.Count() == 901, "kind SQL-like text is a bound value and cannot broaden or change the store");
            Check(store.GetPage("Images", 0, 1000).Count == 300 && store.Count("Videos") == 300, "legacy plural-kind paging and count remain usable");

            var filterToExport = new ResultFilter("Image", "0");
            string csv = Path.Combine(run, "images-disk0.csv");
            long exported = CsvExporter.Export(store, csv, filterToExport);
            var records = ParseCsv(csv);
            var expectedExport = sourceRows.Where(row => row.Kind == "Image" && row.DiskNumber == "0").ToList();
            Check(exported == expectedExport.Count && records.Select(row => row["FileId"]).SequenceEqual(expectedExport.Select(row => row.FileId)), "filtered export matches complete stored selection, independent of current visible page");
            Check(records.Count(row => row["ScanStatus"] == "Failed") == expectedExport.Count(row => row.ScanStatus == "Failed") && records.Any(row => row["ErrorMessage"].Contains("\r\n\"quoted\"")), "filtered export retains failed rows and exact multiline Arabic errors");
            Check(records.All(row => row["ExportScope"] == "Filtered" && row["ExportFilterKind"] == "Image" && row["ExportFilterDiskNumber"] == "0" && row["ScanCompleteness"] == "Partial"), "CSV records filter and scan completeness");
            using (var metadata = JsonDocument.Parse(File.ReadAllText(csv + ".scan.json")))
            {
                var root = metadata.RootElement;
                Check(root.GetProperty("ExportScope").GetString() == "Filtered" && root.GetProperty("ExportedRows").GetInt64() == exported &&
                    root.GetProperty("Filter").GetProperty("Kind").GetString() == "Image" && root.GetProperty("Filter").GetProperty("DiskNumber").GetString() == "0" &&
                    !root.GetProperty("Scan").GetProperty("EnumerationComplete").GetBoolean() && root.GetProperty("Issues").GetArrayLength() == 1,
                    "companion keeps filter, exact exported count, incomplete scan, and traversal issue");
            }
            Check(File.ReadAllBytes(csv).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "filtered CSV uses UTF-8 BOM");
            string unknownCsv = Path.Combine(run, "unknown-disk.csv");
            CsvExporter.Export(store, unknownCsv, new ResultFilter("All", ""));
            using (var metadata = JsonDocument.Parse(File.ReadAllText(unknownCsv + ".scan.json")))
                Check(metadata.RootElement.GetProperty("Filter").GetProperty("DiskNumber").GetString() == "" && metadata.RootElement.GetProperty("ExportScope").GetString() == "Filtered", "unknown disk is explicitly distinct from the all-disks null filter");

            string allCsv = Path.Combine(run, "all.csv");
            Check(CsvExporter.Export(store, allCsv) == 901 && ParseCsv(allCsv).All(row => row["ExportScope"] == "All"), "default overload always exports all stored rows");
            using (var metadata = JsonDocument.Parse(File.ReadAllText(allCsv + ".scan.json")))
                Check(metadata.RootElement.GetProperty("Filter").GetProperty("DiskNumber").ValueKind == JsonValueKind.Null, "default export metadata records no disk restriction");
            string emptyCsv = Path.Combine(run, "empty.csv");
            Check(CsvExporter.Export(store, emptyCsv, new ResultFilter("Image", "not-present")) == 0 && ParseCsv(emptyCsv).Count == 0, "empty filter creates a valid header-only report with zero rows");
            CheckReject(() => CsvExporter.Export(store, Path.Combine(input, "unsafe.csv"), filterToExport), "filtered export retains source-root overwrite protection");

            string realSource = Path.Combine(input, "مغلق, proof.txt");
            File.WriteAllText(realSource, "abc", new UTF8Encoding(false));
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(realSource)));
            var lockedRow = new FileResult
            {
                FileName = Path.GetFileName(realSource), FullPath = realSource, Kind = "Other", DiskNumber = "locked",
                SizeBytes = 3, HashStatus = "Completed", ScanStatus = "Completed", Hashes = new() { ["SHA256"] = hash }
            };
            store.SaveResult(lockedRow);
            using (var locked = new FileStream(realSource, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                string lockedCsv = Path.Combine(run, "locked-source.csv");
                Check(CsvExporter.Export(store, lockedCsv, new ResultFilter("Other", "locked")) == 1 && ParseCsv(lockedCsv).Single()["SHA256"] == hash,
                    "filtered export reuses stored SHA256 while original source is exclusively locked; it does not rehash");
            }
            store.Info.Status = "Paused";
            store.SaveInfo();
            CheckReject(() => CsvExporter.Export(store, Path.Combine(run, "paused.csv"), filterToExport), "paused active scan cannot be exported as a stable snapshot");
        }
        using (var reopened = new ScanStore(database, requireExisting: true))
        {
            Check(reopened.Info.Status == "Interrupted" && reopened.Count() == 902, "reopening a paused scan becomes Interrupted with all durable rows retained");
            Check(reopened.Count(new ResultFilter("Image", "0")) == 75, "disk and kind filtering survives reopening the database");
        }
    }

    private static void CheckReject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }

    private static List<Dictionary<string, string>> ParseCsv(string path)
    {
        using var parser = new TextFieldParser(path, Encoding.UTF8, true) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        string[] headers = parser.ReadFields() ?? throw new InvalidDataException("Missing CSV header");
        var result = new List<Dictionary<string, string>>();
        while (!parser.EndOfData)
        {
            string[] cells = parser.ReadFields()!;
            if (cells.Length != headers.Length) throw new InvalidDataException("Wrong CSV column count");
            result.Add(headers.Zip(cells).ToDictionary(pair => pair.First, pair => pair.Second));
        }
        return result;
    }
}
