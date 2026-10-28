using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HashVista;

internal static class Program
{
    private static readonly List<string> Passes = [];
    private static readonly List<string> Failures = [];
    private static readonly List<object> Observations = [];
    private static string stage = "", run = "";
    private static MediaProcessor Media => new(Path.Combine(stage, "dependencies"));
    private static async Task<int> Main(string[] args)
    {
        stage = Path.GetFullPath(args[0]);
        run = Path.Combine(stage, "evidence", "regression130-runs", "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(run);
        var elapsed = Stopwatch.StartNew();
        await Case("pause-continue-current-hash", () => MidHash("continue"));
        await Case("cancel-paused-current-hash", () => MidHash("cancel"));
        await Case("file-modified-while-paused", () => MidHash("modified"));
        await Case("pause-during-inventory", Inventory);
        await Case("cancel-before-first-file", BeforeFirstFile);
        await Case("reopen-paused-scan", ReopenPaused);
        var report = new { Passed = Passes.Count, Tests = Passes, Failures, TimestampUtc = DateTime.UtcNow,
            ElapsedSeconds = elapsed.Elapsed.TotalSeconds, Assembly = typeof(ScanEngine).Assembly.Location,
            AssemblySHA256 = Digest(typeof(ScanEngine).Assembly.Location, "SHA256"),
            OS = Environment.OSVersion.ToString(), Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            RunDirectory = run, Observations };
        await File.WriteAllTextAsync(Path.Combine(stage, "evidence", "regression130.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"RESULT: {Passes.Count} passed; {Failures.Count} failures");
        return Failures.Count == 0 ? 0 : 1;
    }
    private static async Task Case(string name, Func<Task> test)
    {
        Console.WriteLine("CASE: " + name);
        try { await test(); }
        catch (Exception ex) { Failures.Add(name + ": " + ex); Console.Error.WriteLine(ex); }
    }
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        Passes.Add(name); Console.WriteLine("PASS: " + name);
    }
    private static string Digest(string path, string algorithm)
    {
        using var file = File.OpenRead(path);
        using HashAlgorithm hash = algorithm switch { "SHA256" => SHA256.Create(), "SHA512" => SHA512.Create(), "SHA1" => SHA1.Create(), "MD5" => MD5.Create(), _ => throw new ArgumentException(algorithm) };
        return Convert.ToHexString(hash.ComputeHash(file)).ToLowerInvariant();
    }
    private static ScanStore Store(string name, string root)
    {
        var store = new ScanStore(Path.Combine(run, name + ".ghscan"));
        store.Create([root], new ScanOptions { Algorithms = ["SHA256", "SHA512", "SHA1", "MD5"] });
        return store;
    }
    private static string Fixture(string name)
    {
        string directory = Path.Combine(run, name); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "مرحبا, bounded.bin");
        byte[] block = new byte[1024 * 1024];
        for (int i = 0; i < block.Length; i++) block[i] = (byte)(i * 17 + 13);
        using var output = File.Create(path);
        for (int i = 0; i < 8; i++) output.Write(block);
        return path;
    }
    private static async Task MidHash(string action)
    {
        string path = Fixture(action);
        using var store = Store(action, path);
        using var control = new ScanControl();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var acknowledged = new TaskCompletionSource<ScanProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        ScanProgress? latest = null;
        int reads = 0;
        bool delayed = false, requested = false;
        var scan = new ScanEngine(store, Media).RunAsync(false, new SyncProgress(p =>
        {
            Volatile.Write(ref latest, p);
            // Deliberately open the 200 ms reporting interval, making the first
            // read-block pause deterministic. This delay is not a speed benchmark.
            if (!delayed && p.Activity.StartsWith("Hashing: ")) { delayed = true; Thread.Sleep(220); }
            if (!requested && p.BytesProcessed >= 1024 * 1024)
            {
                requested = true; control.RequestPause();
            }
            if (p.Activity.StartsWith("Paused —")) acknowledged.TrySetResult(p);
            Interlocked.Increment(ref reads);
        }), cancel.Token, control);
        try
        {
            var paused = await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(control.IsPaused && store.Info.Status == "Paused", action + ": engine acknowledges pause");
            Check(paused.BytesProcessed == 1024 * 1024 && paused.CurrentFileBytes == 1024 * 1024,
                action + ": pauses after exactly one 1 MiB hash block");
            Check(paused.OverallPercentage > 0 && paused.OverallPercentage < 100,
                action + ": pause retains nonzero incomplete byte progress");
            int before = Volatile.Read(ref reads);
            await Task.Delay(120);
            Check(!scan.IsCompleted && Volatile.Read(ref reads) == before && Volatile.Read(ref latest)!.BytesProcessed == paused.BytesProcessed,
                action + ": no hash progress while pause is acknowledged");
            if (action == "continue")
            {
                bool rejected = false;
                try { CsvExporter.Export(store, Path.Combine(run, "while-paused.csv")); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "continue: paused live scan cannot be exported concurrently");
            }
            if (action == "cancel")
            {
                cancel.Cancel();
                await scan.WaitAsync(TimeSpan.FromSeconds(5));
                var row = store.Results().Single();
                Check(store.Info.Status == "Cancelled" && !control.IsPaused, "cancel: cancellation releases paused wait without Continue");
                Check(row.HashStatus == "Cancelled" && row.Hashes.Count == 0, "cancel: incomplete hash is never reported as completed");
                Check(Volatile.Read(ref latest)!.BytesProcessed == 1024 * 1024, "cancel: no extra file bytes read after pause");
                Check(CsvExporter.Export(store, Path.Combine(run, "cancelled.csv")) == 1, "cancel: partial result remains exportable");
            }
            else
            {
                if (action == "modified")
                {
                    DateTime original = File.GetLastWriteTimeUtc(path);
                    using (var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    { output.Position = output.Length - 1; output.WriteByte(99); }
                    File.SetLastWriteTimeUtc(path, original.AddSeconds(5));
                }
                control.Resume();
                await scan.WaitAsync(TimeSpan.FromSeconds(10));
                var row = store.Results().Single();
                Check(!control.IsPaused && !control.PauseRequested, action + ": Continue clears requested and acknowledged pause");
                Check(Volatile.Read(ref latest)!.BytesProcessed == new FileInfo(path).Length,
                    action + ": Continue retains stream position and does not reread completed blocks");
                if (action == "continue")
                {
                    Check(store.Info.Status == "Completed" && row.HashStatus == "Completed", "continue: same scan completes");
                    Check(row.Hashes.Count == 4 && row.Hashes.All(h => h.Value == Digest(path, h.Key)),
                        "continue: all four retained incremental hashes match independent complete-file hashes");
                    Check(row.Provenance == "Fresh", "continue: in-process pause does not masquerade as resumed/reused hash");
                }
                else Check(row.HashStatus == "ChangedDuringScan" && row.Hashes.Count == 0,
                    "modified: mutation while paused invalidates every completed digest");
            }
            Observations.Add(new { Action = action, Paused = paused, Final = Volatile.Read(ref latest), store.Info.Status });
        }
        finally { if (!scan.IsCompleted) { cancel.Cancel(); await scan.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }
    private static async Task Inventory()
    {
        string directory = Path.Combine(run, "inventory"); Directory.CreateDirectory(directory);
        for (int i = 0; i < 520; i++) File.WriteAllText(Path.Combine(directory, i + ".txt"), "abc", new UTF8Encoding(false));
        using var store = Store("inventory", directory);
        using var control = new ScanControl();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var acknowledged = new TaskCompletionSource<ScanProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool requested = false;
        var scan = new ScanEngine(store, Media).RunAsync(false, new SyncProgress(p =>
        {
            if (!requested && p.Activity.StartsWith("Inventory:") && p.Discovered == 512)
            { requested = true; control.RequestPause(); }
            if (p.Activity.StartsWith("Paused —")) acknowledged.TrySetResult(p);
        }), cancel.Token, control);
        try
        {
            var paused = await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(paused.Discovered == 512 && store.Count() == 512, "inventory: pause leaves bounded batch durably visible");
            Check(paused.BytesProcessed == 0 && !store.Info.EnumerationComplete, "inventory: pause does not imply hashing or completed enumeration");
            await Task.Delay(100);
            Check(store.Count() == 512 && !scan.IsCompleted, "inventory: no additional traversal while acknowledged paused");
            control.Resume(); await scan.WaitAsync(TimeSpan.FromSeconds(10));
            Check(store.Info.Status == "Completed" && store.Count() == 520 && store.Results().All(x => x.HashStatus == "Completed"),
                "inventory: Continue completes each of 520 files once");
        }
        finally { if (!scan.IsCompleted) { cancel.Cancel(); await scan.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }
    private static async Task BeforeFirstFile()
    {
        string path = Fixture("early");
        using var store = Store("early", path);
        using var control = new ScanControl();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Exercise request/withdraw/request without leaving a stale signalled gate.
        control.RequestPause(); control.Resume(); control.RequestPause();
        var acknowledged = new TaskCompletionSource<ScanProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = new ScanEngine(store, Media).RunAsync(false, new SyncProgress(p =>
        { if (p.Activity.StartsWith("Paused —")) acknowledged.TrySetResult(p); }), cancel.Token, control);
        try
        {
            var paused = await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(control.IsPaused && paused.BytesProcessed == 0 && store.Count() == 0,
                "early: repeated pause-toggle is honored before any source content is read");
            cancel.Cancel(); await scan.WaitAsync(TimeSpan.FromSeconds(5));
            Check(store.Info.Status == "Cancelled" && !control.IsPaused, "early: cancellation of pre-inventory pause finishes normally");
        }
        finally { if (!scan.IsCompleted) { cancel.Cancel(); await scan.WaitAsync(TimeSpan.FromSeconds(5)); } }
    }
    private static async Task ReopenPaused()
    {
        string path = Fixture("restart");
        string database;
        // Model a process that ended after persisting an acknowledged pause.
        // No live connection is reopened or modified by this compatibility check.
        using (var saved = Store("restart", path))
        {
            database = saved.DatabasePath;
            saved.SaveResult(ScanStore.NewQueued(path, "Other", saved.Info));
            saved.Info.Status = "Paused";
            saved.SaveInfo();
        }
        using var reopened = new ScanStore(database, requireExisting: true);
        Check(reopened.Info.Status == "Interrupted", "restart: saved Paused state is normalized to Interrupted");
        Check(reopened.Results().Single().Hashes.Count == 0, "restart: no persisted incremental hash is fabricated");
        await new ScanEngine(reopened, Media).RunAsync(true, null, CancellationToken.None);
        var row = reopened.Results().Single();
        Check(reopened.Info.Status == "Completed" && row.Provenance == "FreshAfterResume",
            "restart: Refresh recomputes independently with fresh-after-resume provenance");
        Check(row.Hashes.Count == 4 && row.Hashes.All(h => h.Value == Digest(path, h.Key)),
            "restart: all recomputed full-file hashes match independent results");
    }
    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => report(value); }
}
