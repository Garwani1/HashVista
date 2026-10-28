// SPDX-License-Identifier: GPL-3.0-or-later
// Loads one supplied scanner assembly per process; no project reference or app rebuild.
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "prepare")
            {
                Prepare(Path.GetFullPath(args[1]));
                return 0;
            }
            if (args.Length != 6 || args[0] != "scan")
                throw new ArgumentException("prepare <run-root> OR scan <scanner-dll> <dependencies> <run-root> <label> <result-json>");
            return await Scan(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]),
                Path.GetFullPath(args[3]), args[4], Path.GetFullPath(args[5]));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Prepare(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Refusing to overwrite benchmark data: " + root);
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "input");
        Directory.CreateDirectory(data);
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var rng = new Random(1222026);
        long totalBytes = 0;
        for (int i = 0; i < 3000; i++)
        {
            var bytes = new byte[4096];
            rng.NextBytes(bytes);
            var relative = $"small-{i:D4}.dat";
            File.WriteAllBytes(Path.Combine(data, relative), bytes);
            expected[relative] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            totalBytes += bytes.Length;
        }
        var largeRelative = "large-128MiB.dat";
        using (var output = File.Create(Path.Combine(data, largeRelative)))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var block = new byte[1024 * 1024];
            for (int i = 0; i < 128; i++)
            {
                rng.NextBytes(block);
                output.Write(block);
                hash.AppendData(block);
                totalBytes += block.Length;
            }
            expected[largeRelative] = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        File.WriteAllText(Path.Combine(root, "expected.json"), JsonSerializer.Serialize(expected, JsonOptions));
        File.WriteAllText(Path.Combine(root, "dataset.json"), JsonSerializer.Serialize(new
        {
            CreatedUtc = DateTime.UtcNow, Files = expected.Count, TotalBytes = totalBytes,
            SmallFileCount = 3000, SmallFileBytes = 4096, LargeFileBytes = 128L * 1024 * 1024,
            Seed = 1222026, IndependentExpectedHashes = "System.Security.Cryptography.SHA256 / IncrementalHash on generated content"
        }, JsonOptions));
        Console.WriteLine($"Prepared {expected.Count} files, {totalBytes} bytes.");
    }

    private static async Task<int> Scan(string scannerDll, string dependencies, string root, string label, string resultPath)
    {
        string scannerDirectory = Path.GetDirectoryName(scannerDll)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(scannerDirectory, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            foreach (string candidate in new[] { Path.Combine(scannerDirectory, name), Path.Combine(scannerDirectory, name + ".dll") })
                if (File.Exists(candidate)) return NativeLibrary.Load(candidate);
            return IntPtr.Zero;
        };
        var app = AssemblyLoadContext.Default.LoadFromAssemblyPath(scannerDll);
        // Older comparison builds used a different namespace; fall back to the type name.
        Type TypeOf(string name) => app.GetType("HashVista." + name) ?? app.GetTypes().Single(t => t.Name == name && !t.IsNested);
        var storeType = TypeOf("ScanStore");
        var scanOptions = Activator.CreateInstance(TypeOf("ScanOptions"))!;
        void Option(string name, object value) => scanOptions.GetType().GetProperty(name)!.SetValue(scanOptions, value);
        Option("Algorithms", new[] { "SHA256" });
        foreach (string option in new[] { "Metadata", "Fingerprints", "StreamHashes", "IncludeHidden", "FollowLinks" }) Option(option, false);
        Option("Recursive", true);
        string database = Path.Combine(root, label + ".ghscan");
        if (File.Exists(database)) throw new IOException("Refusing to reuse database: " + database);
        using var store = (IDisposable)Activator.CreateInstance(storeType, database, false)!;
        var info = storeType.GetMethod("Create")!.Invoke(store, [new[] { Path.Combine(root, "input") }, scanOptions])!;
        var media = Activator.CreateInstance(TypeOf("MediaProcessor"), dependencies)!;
        var engine = Activator.CreateInstance(TypeOf("ScanEngine"), store, media)!;
        var progressType = TypeOf("ScanProgress");
        var activityProperty = progressType.GetProperty("Activity")!;
        var watch = new Stopwatch();
        double? inventorySeconds = null;
        long progressEvents = 0;
        long actualHashBytesRead = 0;
        Action<object> report = value =>
        {
            progressEvents++;
            actualHashBytesRead = (long)progressType.GetProperty("BytesProcessed")!.GetValue(value)!;
            if (inventorySeconds == null && ((string)activityProperty.GetValue(value)!).StartsWith("Inventory complete:", StringComparison.Ordinal))
                inventorySeconds = watch.Elapsed.TotalSeconds;
        };
        var progress = Activator.CreateInstance(typeof(ProgressBridge<>).MakeGenericType(progressType), report)!;
        watch.Start();
        var runMethod = engine.GetType().GetMethod("RunAsync")!;
        object?[] runArguments = runMethod.GetParameters().Length == 4
            ? [false, progress, CancellationToken.None, null]
            : [false, progress, CancellationToken.None];
        await (Task)runMethod.Invoke(engine, runArguments)!;
        watch.Stop();

        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "expected.json")))!;
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (object row in (IEnumerable)storeType.GetMethod("Results", Type.EmptyTypes)!.Invoke(store, null)!)
        {
            var rowType = row.GetType();
            var fullPath = (string)rowType.GetProperty("FullPath")!.GetValue(row)!;
            var relative = Path.GetRelativePath(Path.Combine(root, "input"), fullPath);
            var hashStatus = (string)rowType.GetProperty("HashStatus")!.GetValue(row)!;
            var hashes = (IDictionary)rowType.GetProperty("Hashes")!.GetValue(row)!;
            var sha = (string?)hashes["SHA256"] ?? "";
            if (!actual.TryAdd(relative, sha)) failures.Add("Duplicate row: " + relative);
            if (!expected.TryGetValue(relative, out var expectedHash) || !string.Equals(sha, expectedHash, StringComparison.OrdinalIgnoreCase)) failures.Add("Hash mismatch: " + relative);
            if (hashStatus != "Completed") failures.Add($"{relative}: HashStatus={hashStatus}");
        }
        foreach (var missing in expected.Keys.Except(actual.Keys)) failures.Add("Missing row: " + missing);
        string status = (string)info.GetType().GetProperty("Status")!.GetValue(info)!;
        long cachedCount = (long)info.GetType().GetProperty("TotalFiles")!.GetValue(info)!;
        if (status != "Completed") failures.Add("Scan status: " + status);
        if (actual.Count != expected.Count || cachedCount != expected.Count) failures.Add($"Count mismatch: actual={actual.Count}, cached={cachedCount}, expected={expected.Count}");
        if (actualHashBytesRead != 146505728) failures.Add("Actual hash read-byte count differs from dataset size: " + actualHashBytesRead);
        var normalized = string.Join("\n", actual.Select(pair => pair.Key + "=" + pair.Value.ToLowerInvariant()));
        var result = new
        {
            Label = label, CompletedUtc = DateTime.UtcNow, ScannerAssembly = scannerDll,
            ScannerAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(scannerDll))).ToLowerInvariant(),
            ScannerAssemblyVersion = app.GetName().Version?.ToString(),
            OS = Environment.OSVersion.ToString(), Framework = RuntimeInformation.FrameworkDescription,
            Options = new { Algorithms = new[] { "SHA256" }, Metadata = false, Fingerprints = false, StreamHashes = false },
            WallSeconds = watch.Elapsed.TotalSeconds, InventorySeconds = inventorySeconds,
            ProcessingSeconds = inventorySeconds.HasValue ? watch.Elapsed.TotalSeconds - inventorySeconds.Value : (double?)null,
            ProgressEvents = progressEvents, Status = status, ActualFiles = actual.Count, CachedFiles = cachedCount,
            ActualHashBytesRead = actualHashBytesRead,
            ExpectedFiles = expected.Count, IndependentHashesVerified = failures.Count == 0,
            ResultSetSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant(),
            Failures = failures
        };
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, JsonOptions));
        Console.WriteLine($"{label}: {watch.Elapsed.TotalSeconds:F3}s; {actual.Count} files; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;
    }
}

internal sealed class ProgressBridge<T>(Action<object> callback) : IProgress<T>
{
    public void Report(T value) => callback(value!);
}
