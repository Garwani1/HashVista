using System.IO;
using HashVista;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static readonly List<string> Passes = [];
    private static readonly List<object> Observations = [];
    private static readonly List<string> Failures = [];
    private static string stage = "", run = "";
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static ScanOptions HashOnly => new()
    {
        Algorithms = ["SHA256", "SHA512", "SHA1", "MD5"], Metadata = false,
        Fingerprints = false, StreamHashes = false, Recursive = true,
        IncludeHidden = false, IncludeSystem = false, FollowLinks = false
    };
    private static MediaProcessor Media => new(Path.Combine(stage, "dependencies"));

    private static async Task<int> Main(string[] args)
    {
        stage = Path.GetFullPath(args[0]);
        run = Path.Combine(stage, "evidence", "regression123-runs", "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(run);
        var elapsed = Stopwatch.StartNew();
        await Case("explicit-hidden-system-root", ExplicitHiddenSystemRoot);
        await Case("timed-inventory-flush", TimedInventoryFlush);
        await Case("explicit-hidden-system-file", ExplicitHiddenSystemFile);
        await Case("empty-and-filtered-roots", EmptyAndFilteredRoots);
        await Case("empty-incomplete-traversal", EmptyIncompleteTraversal);
        await Case("application-database-root-protection", ProtectedRoots);
        await Case("explicit-link-root-policy", ExplicitLinks);
        await Case("resume-old-zero-result-scan", ResumeOldZeroResultScan);
        elapsed.Stop();
        var report = new
        {
            Passed = Passes.Count, Tests = Passes, Failures,
            TimestampUtc = DateTime.UtcNow, RunDirectory = run,
            OS = Environment.OSVersion.ToString(),
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Assembly = typeof(ScanEngine).Assembly.Location,
            AssemblySHA256 = Digest(typeof(ScanEngine).Assembly.Location, "SHA256"),
            ElapsedSeconds = elapsed.Elapsed.TotalSeconds, Observations
        };
        await File.WriteAllTextAsync(Path.Combine(stage, "evidence", "regression123.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"RESULT: {Passes.Count} passed; {Failures.Count} failures; {elapsed.Elapsed.TotalSeconds:F2}s");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static async Task Case(string name, Func<Task> test)
    {
        Console.WriteLine("CASE: " + name);
        try { await test(); }
        catch (Exception ex) { Failures.Add(name + ": " + ex); Console.Error.WriteLine("FAIL: " + name + ": " + ex); }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Passes.Add(name); Console.WriteLine("PASS: " + name);
    }
    private static string DirectoryFixture(string name)
    {
        string path = Path.Combine(run, name); Directory.CreateDirectory(path); return path;
    }
    private static void WriteFixture(string path, string content) => File.WriteAllText(path, content, new UTF8Encoding(false));
    private static void Mark(string path, FileAttributes flags) => File.SetAttributes(path, File.GetAttributes(path) | flags);
    private static ScanStore Store(string name, string[] roots, ScanOptions? options = null)
    {
        var store = new ScanStore(Path.Combine(run, name + ".ghscan"));
        store.Create(roots, options ?? HashOnly); return store;
    }
    private static async Task<List<ScanProgress>> Scan(ScanStore store, bool resume = false, Action<ScanProgress>? callback = null)
    {
        var updates = new List<ScanProgress>();
        await new ScanEngine(store, Media).RunAsync(resume, new SyncProgress(p =>
        {
            updates.Add(p); callback?.Invoke(p);
        }), CancellationToken.None);
        return updates;
    }
    private static string Digest(string path, string algorithm)
    {
        using var file = File.OpenRead(path);
        using HashAlgorithm hasher = algorithm switch
        {
            "SHA256" => SHA256.Create(), "SHA512" => SHA512.Create(),
            "SHA1" => SHA1.Create(), "MD5" => MD5.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
        return Convert.ToHexString(hasher.ComputeHash(file)).ToLowerInvariant();
    }
    private static void VerifyHashes(ScanStore store, string label)
    {
        var rows = store.Results().ToList();
        Check(rows.Count > 0 && rows.All(r => r.HashStatus == "Completed" && r.Hashes.Count == 4 &&
            r.Hashes.All(h => h.Value.Equals(Digest(r.FullPath, h.Key), StringComparison.OrdinalIgnoreCase))),
            label + ": all four hashes independently match every included input");
    }
    private static (string root, string normal, string nested) MixedRoot(string name)
    {
        string root = DirectoryFixture(name);
        string normal = Path.Combine(root, "مرحبا, عادي.txt"); WriteFixture(normal, "abc");
        string hidden = Path.Combine(root, "hidden.txt"); WriteFixture(hidden, "hidden child"); Mark(hidden, FileAttributes.Hidden);
        string system = Path.Combine(root, "system.txt"); WriteFixture(system, "system child"); Mark(system, FileAttributes.System);
        string ordinary = Path.Combine(root, "ordinary-subdir"); Directory.CreateDirectory(ordinary);
        string nested = Path.Combine(ordinary, "nested.txt"); WriteFixture(nested, "ordinary nested child");
        string excludedDirectory = Path.Combine(root, "hidden-system-subdir"); Directory.CreateDirectory(excludedDirectory);
        WriteFixture(Path.Combine(excludedDirectory, "must-stay-excluded.txt"), "excluded descendant");
        Mark(excludedDirectory, FileAttributes.Hidden | FileAttributes.System);
        // Mirror the flags observed on a volume root without touching or traversing a real drive.
        Mark(root, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
        return (root, normal, nested);
    }
    private static void CheckNoFiles(ScanStore store, IReadOnlyList<ScanProgress> updates, string label)
    {
        Check(store.Count() == 0 && store.Info.TotalFiles == 0 && store.Info.Status == "NoFilesFound" &&
            store.Info.EnumerationComplete, label + ": complete empty inventory is NoFilesFound");
        Check(updates.Count > 0 && updates.All(p => p.OverallPercentage == 0),
            label + ": no progress callback reports 100% or nonzero completion");
    }

    private static async Task ExplicitHiddenSystemRoot()
    {
        var fixture = MixedRoot("selected-hidden-system-root");
        using var store = Store("selected-hidden-system-root", [fixture.root]);
        var updates = await Scan(store);
        var rows = store.Results().ToList();
        Check(rows.Count == 2 && rows.Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals([fixture.normal, fixture.nested]),
            "explicit Hidden|System directory scans ordinary child and ordinary subdirectory");
        Check(store.GetIssues().All(i => !i.Path.Equals(fixture.root, StringComparison.OrdinalIgnoreCase)),
            "explicit selected root is not itself excluded by Hidden/System filters");
        Check(store.GetIssues().Count(i => i.Stage == "Excluded") == 3 &&
            store.GetIssues().Any(i => i.Code == "Hidden") && store.GetIssues().Any(i => i.Code == "System"),
            "hidden/system child files and hidden/system child directory remain excluded");
        Check(store.Info.Status == "CompletedWithIssues" && updates[^1].OverallPercentage == 100,
            "nonempty completed scan retains ordinary completed-with-issues semantics");
        Check(rows.Single(r => r.FullPath == fixture.normal).SHA256 == AbcSha256,
            "included Arabic/comma filename has the independent abc SHA256 known vector");
        VerifyHashes(store, "explicit hidden/system directory");
        Check((File.GetAttributes(fixture.root) & (FileAttributes.Hidden | FileAttributes.System)) ==
            (FileAttributes.Hidden | FileAttributes.System), "scanning does not clear source directory attributes");
        Observations.Add(new { Name = "explicit-hidden-system-root", RootAttributes = File.GetAttributes(fixture.root).ToString(),
            Rows = rows.Select(r => r.FullPath).ToArray(), Issues = store.GetIssues(), store.Info.Status });
    }

    private static async Task ExplicitHiddenSystemFile()
    {
        string file = Path.Combine(run, "explicit-hidden-system-file.txt");
        WriteFixture(file, "abc"); Mark(file, FileAttributes.Hidden | FileAttributes.System);
        using var store = Store("explicit-hidden-system-file", [file]);
        await Scan(store);
        Check(store.Count() == 1 && store.GetIssues().Count == 0,
            "explicit Hidden|System file is included while descendant filters stay disabled");
        VerifyHashes(store, "explicit hidden/system file");
    }

    private static async Task TimedInventoryFlush()
    {
        string root = DirectoryFixture("slow-inventory-clock-fixture");
        for (int i = 0; i < 3; i++) WriteFixture(Path.Combine(root, "ordinary-" + i + ".txt"), "abc");
        using var store = Store("timed-inventory", [root]);
        bool delayed = false, partialFlush = false;
        var directoryEvents = new List<long>();
        var watch = Stopwatch.StartNew();
        var updates = await Scan(store, callback: p =>
        {
            if (p.Activity == "Enumerating: " + root)
            {
                directoryEvents.Add(watch.ElapsedMilliseconds);
                if (!delayed)
                {
                    // Controlled elapsed time stands in for a slow enumeration boundary;
                    // this is a deterministic timer regression, not a disk benchmark.
                    delayed = true;
                    Thread.Sleep(1100);
                }
            }
            if (p.Activity.StartsWith("Inventory:", StringComparison.Ordinal) && p.Discovered == 1)
                partialFlush = store.Count() == 1 && store.Results().Single().HashStatus == "Queued" && p.Processed == 0;
        });
        Check(partialFlush,
            "inventory flushes a timed partial batch before the 512-file threshold and before hashing");
        Check(directoryEvents.Count >= 2 && directoryEvents[1] - directoryEvents[0] >= 200,
            "current directory progress repeats after a slow enumeration boundary with throttling");
        Check(store.Count() == 3 && store.Info.Status == "Completed" && updates[^1].OverallPercentage == 100,
            "timed inventory flush retains all files and normal final completion");
        VerifyHashes(store, "timed inventory flush");
        Observations.Add(new { Name = "timed-inventory-flush", ControlledCallbackDelayMs = 1100,
            PartialBatchPersisted = partialFlush, DirectoryProgressTimesMs = directoryEvents,
            FinalCount = store.Count(), Note = "Timer behavior test only; not a slow-disk performance benchmark." });
    }

    private static async Task EmptyAndFilteredRoots()
    {
        string empty = DirectoryFixture("empty-hidden-system-root"); Mark(empty, FileAttributes.Hidden | FileAttributes.System);
        using (var store = Store("empty", [empty]))
        {
            var updates = await Scan(store);
            CheckNoFiles(store, updates, "empty explicit root");
            Check(store.GetIssues().Count == 0, "a genuinely empty explicit root has no fabricated exclusion");
            string csv = Path.Combine(run, "empty.csv");
            Check(CsvExporter.Export(store, csv) == 0, "NoFilesFound scan exports a valid zero-row report");
            using var companion = JsonDocument.Parse(File.ReadAllText(csv + ".scan.json"));
            Check(companion.RootElement.GetProperty("ExportedRows").GetInt64() == 0 &&
                companion.RootElement.GetProperty("Scan").GetProperty("Status").GetString() == "NoFilesFound",
                "empty CSV companion preserves zero-row NoFilesFound state");
        }
        string filtered = DirectoryFixture("all-descendants-filtered");
        string hidden = Path.Combine(filtered, "hidden.txt"), system = Path.Combine(filtered, "system.txt");
        WriteFixture(hidden, "hidden"); WriteFixture(system, "system");
        Mark(hidden, FileAttributes.Hidden); Mark(system, FileAttributes.System);
        using (var store = Store("all-filtered", [filtered]))
        {
            var updates = await Scan(store);
            CheckNoFiles(store, updates, "all children filtered");
            Check(store.GetIssues().Count == 2, "all-filtered inventory retains both descendant exclusions");
        }
    }

    private static async Task EmptyIncompleteTraversal()
    {
        // Delete only this newly created empty fixture at a controlled callback.
        // This makes actual enumeration fail without changing filesystem ACLs.
        string root = DirectoryFixture("disappears-before-enumeration");
        bool removed = false;
        using var store = Store("incomplete-empty", [root]);
        var updates = await Scan(store, callback: p =>
        {
            if (!removed && p.Activity == "Enumerating: " + root)
            {
                Directory.Delete(root); removed = true;
            }
        });
        Check(removed && store.Count() == 0 && !store.Info.EnumerationComplete &&
            store.Info.Status == "Partial" && store.IssueCount("Enumeration") > 0,
            "zero-row enumeration error remains Partial instead of NoFilesFound");
        Check(updates[^1].OverallPercentage == 0, "zero-row incomplete traversal reports zero completion");
    }

    private static async Task ProtectedRoots()
    {
        using (var store = Store("explicit-application-root", [AppContext.BaseDirectory]))
        {
            var updates = await Scan(store);
            CheckNoFiles(store, updates, "explicit application directory");
            Check(store.GetIssues().Any(i => i.Code == "ApplicationData"),
                "explicit application root still has the application-data exclusion");
        }
        using (var store = new ScanStore(Path.Combine(run, "explicit-active-database.ghscan")))
        {
            store.Create([store.DatabasePath], HashOnly);
            var updates = await Scan(store);
            CheckNoFiles(store, updates, "explicit active database file");
            Check(store.GetIssues().Any(i => i.Code == "ApplicationData"),
                "explicit active database root remains protected");
        }
    }

    private static async Task ExplicitLinks()
    {
        string target = Path.Combine(run, "ordinary-link-target.txt"); WriteFixture(target, "abc");
        string link = Path.Combine(run, "explicit-file-link.txt"); File.CreateSymbolicLink(link, target);
        using (var store = Store("explicit-link-disabled", [link]))
        {
            var updates = await Scan(store);
            CheckNoFiles(store, updates, "explicit link with following disabled");
            Check(store.GetIssues().Any(i => i.Code == "Link"),
                "explicit selection does not bypass the disabled-follow-links policy");
        }
        var follow = HashOnly; follow.FollowLinks = true;
        using (var store = Store("explicit-link-enabled", [link], follow))
        {
            await Scan(store);
            Check(store.Count() == 1 && store.Results().Single().SHA256 == AbcSha256,
                "explicit ordinary file link works when following is enabled");
            VerifyHashes(store, "allowed explicit file link");
        }
        string appTarget = Path.Combine(AppContext.BaseDirectory, "regression123-app-target.txt");
        WriteFixture(appTarget, "test application file");
        string appLink = Path.Combine(run, "explicit-link-to-app.txt"); File.CreateSymbolicLink(appLink, appTarget);
        using (var store = Store("explicit-link-app-protected", [appLink], follow))
        {
            var updates = await Scan(store);
            CheckNoFiles(store, updates, "explicit followed link to application");
            Check(store.GetIssues().Any(i => i.Code == "ApplicationData"),
                "physical application target remains protected through an explicitly selected file link");
        }
        using (var store = new ScanStore(Path.Combine(run, "explicit-link-db-protected.ghscan")))
        {
            string databaseLink = Path.Combine(run, "explicit-link-to-active-db.ghscan");
            File.CreateSymbolicLink(databaseLink, store.DatabasePath);
            store.Create([databaseLink], follow);
            var updates = await Scan(store);
            CheckNoFiles(store, updates, "explicit followed link to active database");
            Check(store.GetIssues().Any(i => i.Code == "ApplicationData"),
                "physical active-database target remains protected through an explicit link");
        }
    }

    private static async Task ResumeOldZeroResultScan()
    {
        var fixture = MixedRoot("resume-hidden-system-root");
        string database;
        using (var saved = Store("resume-old-zero-result", [fixture.root]))
        {
            database = saved.DatabasePath;
            // Faithful old-style zero-row record: the earlier root filter excluded
            // this explicitly chosen directory under unchanged default settings.
            saved.Info.Status = "CompletedWithIssues";
            saved.Info.EnumerationComplete = true;
            saved.Info.FinishedUtc = DateTime.UtcNow;
            saved.AddIssue(new ScanIssue(fixture.root, "Excluded", "Hidden", "Hidden entry excluded by scan settings."));
            saved.SaveInfo();
        }
        using var reopened = new ScanStore(database, requireExisting: true);
        string configuration = JsonSerializer.Serialize(reopened.Info.Options);
        Check(reopened.Count() == 0 && !reopened.Info.Options.IncludeHidden && !reopened.Info.Options.IncludeSystem,
            "old-style empty saved scan reopens with hidden/system options still disabled");
        await Scan(reopened, resume: true);
        Check(reopened.Count() == 2 && reopened.Info.TotalFiles == 2 &&
            JsonSerializer.Serialize(reopened.Info.Options) == configuration,
            "resume discovers ordinary children without changing the scan's saved default settings");
        Check(reopened.Results().All(r => r.Provenance == "FreshAfterResume") &&
            reopened.GetIssues().All(i => !i.Path.Equals(fixture.root, StringComparison.OrdinalIgnoreCase)),
            "resume clears the old root exclusion and records freshly recomputed results");
        VerifyHashes(reopened, "resumed old-style empty hidden/system root");
        Observations.Add(new { Name = "resume-old-zero-result", SimulatedOldRows = 0,
            ActualRows = reopened.Count(), reopened.Info.Status, Options = reopened.Info.Options });
    }

    private sealed class SyncProgress(Action<ScanProgress> action) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => action(value);
    }
}

