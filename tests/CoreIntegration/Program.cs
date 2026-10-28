using System.IO;
using HashVista;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

internal static class CoreIntegration
{
    static readonly string Project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    static string Workspace = "";
    static string RunRoot = "";
    static string Evidence = "";
    static readonly List<object> Checks = [];
    static int failures;
    static readonly ScanOptions HashOnly = new() { Algorithms = ["SHA256", "SHA512", "SHA1", "MD5"], Metadata = false, Fingerprints = false };
    static string CaseName = "";
    static readonly string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    static readonly string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    static readonly string AbcSha512 = "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f";

    static async Task<int> Main(string[] args)
    {
        Workspace = args.Length > 0 ? Path.GetFullPath(args[0]) : Project;
        Evidence = Path.Combine(Workspace, "evidence");
        RunRoot = Path.Combine(Workspace, "tests", "CoreIntegration", "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Evidence); Directory.CreateDirectory(RunRoot);
        var watch = Stopwatch.StartNew();
        Console.WriteLine("HashVista core Windows integration: " + DateTime.UtcNow.ToString("O"));
        Console.WriteLine("OS: " + Environment.OSVersion + "; Framework: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("Fixtures: " + RunRoot);
        await Case("inventory-hashes-csv-resume", InventoryCsvResume);
        await Case("errors-and-stage-isolation", ErrorsAndStageIsolation);
        await Case("hidden-system-recursion", AttributePolicies);
        await Case("junction-loop", LinkLoop);
        await Case("cancel-and-change-during-read", CancelAndChange);
        await Case("database-validation", DatabaseValidation);
        await Case("synthetic-50000-paging-export", Synthetic);
        watch.Stop();
        var report = new {
            Schema = "HashVista-CoreIntegration-v1", CompletedUtc = DateTime.UtcNow,
            OS = Environment.OSVersion.ToString(),
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            FixtureRoot = RunRoot, ElapsedSeconds = watch.Elapsed.TotalSeconds,
            Failed = failures, Checks
        };
        File.WriteAllText(Path.Combine(Evidence, "core-integration-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine($"RESULT: {Checks.Count} checks, {failures} failures, {watch.Elapsed.TotalSeconds:F2}s");
        return failures == 0 ? 0 : 1;
    }

    static async Task Case(string name, Func<Task> test)
    {
        CaseName = name;
        Console.WriteLine("CASE " + name);
        try { await test(); }
        catch (Exception ex)
        {
            failures++;
            Checks.Add(new { Case = name, Test = "Unhandled case failure", Passed = false, Detail = ex.ToString() });
            Console.WriteLine("FAIL " + name + ": " + ex);
        }
    }
    static void Check(bool value, string name, string detail = "")
    {
        Checks.Add(new { Case = CaseName, Test = name, Passed = value, Detail = detail });
        Console.WriteLine((value ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? ": " + detail : ""));
        if (!value) throw new InvalidOperationException(name + ": " + detail);
    }
    static string Dir(string name) { var p = Path.Combine(RunRoot, name); Directory.CreateDirectory(p); return p; }
    static ScanStore Store(string name, string[] roots, ScanOptions? options = null)
    {
        var store = new ScanStore(Path.Combine(Dir("databases"), name + ".sqlite"));
        Check(!store.HasScan, name + " new database has no scan before Create");
        store.Create(roots, options ?? HashOnly);
        Check(store.HasScan, name + " Create establishes scan");
        return store;
    }
    static MediaProcessor Media() => new(Path.Combine(Workspace, "dependencies"));
    static Task Scan(ScanStore store, bool resume = false, IProgress<ScanProgress>? progress = null, CancellationToken token = default)
        => new ScanEngine(store, Media()).RunAsync(resume, progress, token);
    static FileResult Find(ScanStore store, string path) => store.Results().Single(r => string.Equals(r.FullPath, path, StringComparison.OrdinalIgnoreCase));
    static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    static List<Dictionary<string,string>> ParseCsv(string path)
    {
        using var parser = new TextFieldParser(path, Encoding.UTF8, true);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;
        var headers = parser.ReadFields()!;
        var rows = new List<Dictionary<string,string>>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            if (fields.Length != headers.Length) throw new InvalidDataException($"CSV field count {fields.Length} != {headers.Length}");
            rows.Add(headers.Zip(fields).ToDictionary(x => x.First, x => x.Second));
        }
        return rows;
    }

    static async Task InventoryCsvResume()
    {
        var input = Dir("inventory");
        var nested = Path.Combine(input, "nested"); Directory.CreateDirectory(nested);
        var abc = Path.Combine(nested, "abc.txt");
        var copy = Path.Combine(input, "copy.txt");
        var empty = Path.Combine(input, "empty.txt");
        var other = Path.Combine(input, "different.txt");
        var arabic = Path.Combine(input, "مرحبا, الملف.txt");
        File.WriteAllBytes(abc, "abc"u8.ToArray());
        File.WriteAllBytes(copy, "abc"u8.ToArray());
        File.WriteAllBytes(empty, []);
        File.WriteAllBytes(other, "xyz"u8.ToArray());
        File.WriteAllBytes(arabic, "abc"u8.ToArray());
        var before = Directory.GetFiles(input, "*", System.IO.SearchOption.AllDirectories).ToDictionary(p => p, p => (Hash: Digest(p), Time: File.GetLastWriteTimeUtc(p)));
        string dbPath; Dictionary<string,string> fileIds;
        using (var store = Store("inventory", [input, nested, abc]))
        {
            dbPath = store.DatabasePath;
            await Scan(store);
            Check(store.Count() == 5, "nested and overlapping roots produce exactly 5 distinct paths");
            Check(store.Info.EnumerationComplete, "inventory traversal complete");
            Check(store.Results().All(r => r.HashStatus == "Completed"), "all files including unique sizes and empty were hashed");
            Check(new[] { abc, copy, arabic }.All(p => Find(store,p).SHA256 == AbcSha256), "SHA256 abc matches independent known constant");
            Check(new[] { abc, copy, arabic }.All(p => Find(store,p).Hashes["SHA512"] == AbcSha512), "SHA512 abc matches independent known constant");
            Check(Find(store,abc).Hashes["MD5"] == "900150983cd24fb0d6963f7d28e17f72", "MD5 abc known constant");
            Check(Find(store,abc).Hashes["SHA1"] == "a9993e364706816aba3e25717850c26c9cd0d89d", "SHA1 abc known constant");
            Check(Find(store,empty).SizeBytes == 0 && Find(store,empty).SHA256 == EmptySha256, "zero-byte size and full empty-file SHA256 preserved");
            Check(Find(store,other).SHA256 == Digest(other), "different file hash independently calculated from bytes");
            Check(before.All(x => Digest(x.Key) == x.Value.Hash && File.GetLastWriteTimeUtc(x.Key) == x.Value.Time), "scanning did not modify source bytes or last-write times");
            var issue = "Access \"denied\", تعذرت القراءة.\r\nsecond line";
            var errorRow = Find(store,empty);
            errorRow.Errors.Add(new StageError { Stage = "Fixture", Code = "TEST_MULTILINE", Message = issue });
            errorRow.ScanStatus = "CompletedWithErrors";
            store.SaveResult(errorRow);
            var previousComputed = Find(store,abc).ComputedUtc;
            File.WriteAllText(abc, "changed after scan", new UTF8Encoding(false));
            var csvPath = Path.Combine(Dir("exports"), "inventory.csv");
            var rowsExported = CsvExporter.Export(store, csvPath);
            var raw = File.ReadAllBytes(csvPath);
            Check(raw.AsSpan(0,3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "CSV starts with UTF8 BOM");
            var csv = ParseCsv(csvPath);
            Check(rowsExported == 5 && csv.Count == store.Count(), "CSV exports every stored row");
            Check(csv.Single(r => r["FullPath"] == arabic)["FileName"] == "مرحبا, الملف.txt", "Arabic/comma path round trips through independent CSV parser");
            Check(csv.Single(r => r["FullPath"] == empty)["ErrorMessage"] == issue, "quoted multiline error round trips exactly");
            Check(csv.Single(r => r["FullPath"] == abc)["SHA256"] == AbcSha256 && Find(store,abc).ComputedUtc == previousComputed, "export reuses computed results after source changed; no rehash");
            Check(csv.All(r => r["ExportScope"] == "All"), "all-results export scope recorded");
            using (var scanJson = JsonDocument.Parse(File.ReadAllText(csvPath + ".scan.json")))
                Check(scanJson.RootElement.GetProperty("ExportedRows").GetInt64() == 5 && scanJson.RootElement.GetProperty("Issues").GetArrayLength() > 0, "companion JSON preserves row count and overlap exclusions");
            bool rejected = false;
            try { CsvExporter.Export(store, Path.Combine(input,"report.csv")); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(input,"report.csv")), "export inside selected input root refused");
            fileIds = store.Results().ToDictionary(r => r.FullPath, r => r.FileId);
        }
        File.Delete(empty); // Disposable fixture: explicitly exercises disappeared-file invalidation.
        var added = Path.Combine(input, "added.txt"); File.WriteAllText(added, "new", new UTF8Encoding(false));
        using (var reopened = new ScanStore(dbPath, requireExisting:true))
        {
            Check(reopened.HasScan && reopened.Count() == 5, "saved scan reopened with previous partial/error rows");
            await Scan(reopened, resume:true);
            Check(reopened.Count() == 6, "resume includes new file and retains disappeared file row");
            Check(Find(reopened,abc).SHA256 == Digest(abc) && Find(reopened,abc).SHA256 != AbcSha256, "changed input recomputed freshly on resume");
            var missing = Find(reopened,empty);
            Check(missing.ScanStatus == "Failed" && missing.Hashes.Count == 0 && missing.SizeBytes == null && missing.Errors.Any(e => e.Code == "NotRediscovered"), "removed input is Failed with old hashes/size invalidated");
            Check(fileIds.All(x => Find(reopened,x.Key).FileId == x.Value), "FileId remains stable across resume");
            Check(reopened.Results().All(r => r.Provenance == "FreshAfterResume"), "fresh-after-resume provenance recorded");
        }
        var companionInput = Path.Combine(Dir("collision"), "out.csv.scan.json");
        File.WriteAllText(companionInput, "source must survive");
        using var collision = Store("collision", [companionInput]);
        await Scan(collision);
        bool companionRejected = false;
        try { CsvExporter.Export(collision, Path.Combine(Path.GetDirectoryName(companionInput)!, "out.csv")); }
        catch (InvalidOperationException) { companionRejected = true; }
        Check(companionRejected && File.ReadAllText(companionInput) == "source must survive", "companion export cannot overwrite selected input");
    }

    static async Task ErrorsAndStageIsolation()
    {
        var input = Dir("failures");
        var locked = Path.Combine(input,"locked.txt"); File.WriteAllText(locked,"locked bytes");
        var bad = Path.Combine(input,"bad.png"); File.WriteAllText(bad,"not an image");
        var missing = Path.Combine(input,"missing.txt");
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var store = Store("failures", [input, missing], new ScanOptions { Algorithms=["SHA256"], Metadata=true, Fingerprints=true });
        await Scan(store);
        Check(store.Count() == 3, "locked, corrupt image and missing explicit file all retain rows");
        Check(Find(store,locked).HashStatus == "Failed" && Find(store,locked).Hashes.Count == 0 && Find(store,locked).Errors.Count > 0, "real FileShare.None read failure retained");
        var badRow = Find(store,bad);
        Check(badRow.HashStatus == "Completed" && badRow.SHA256 == Digest(bad), "corrupt-image metadata/fingerprint failure preserves SHA256");
        Check(badRow.MetadataStatus is "Failed" or "Unsupported" && badRow.FingerprintStatus is "Failed" or "Unsupported", "corrupt image stages explicitly failed or unsupported");
        Check(Find(store,missing).ScanStatus == "Failed" && Find(store,missing).SizeBytes == null, "missing selected path has failed row and unknown size");
        Check(store.Info.Status == "Partial" && !store.Info.EnumerationComplete && store.IssueCount("Enumeration") > 0, "enumeration failure prevents complete successful scan");
        var csv = Path.Combine(Dir("exports"),"failures.csv"); CsvExporter.Export(store,csv);
        Check(ParseCsv(csv).Count == 3 && ParseCsv(csv).Single(r => r["FullPath"] == missing)["SizeBytes"] == "", "failed-row CSV count and missing-value semantics");
    }

    static async Task AttributePolicies()
    {
        var input = Dir("attributes");
        var normal=Path.Combine(input,"normal.txt"); var hidden=Path.Combine(input,"hidden.txt"); var system=Path.Combine(input,"system.txt");
        foreach(var path in new[]{normal,hidden,system}) File.WriteAllText(path,"abc",new UTF8Encoding(false));
        File.SetAttributes(hidden, FileAttributes.Hidden); File.SetAttributes(system, FileAttributes.System);
        using (var store=Store("attributes-default",[input]))
        {
            await Scan(store);
            Check(store.Count()==1 && store.IssueCount("Excluded")==2,"hidden/system off skips both with durable exclusion issues");
        }
        using (var store=Store("attributes-included",[input],new ScanOptions{Algorithms=["SHA256"],Metadata=false,Fingerprints=false,IncludeHidden=true,IncludeSystem=true}))
        {
            await Scan(store);
            Check(store.Count()==3 && store.Results().All(r=>r.HashStatus=="Completed"),"hidden/system on includes and hashes both");
        }
        var nested=Path.Combine(input,"nested"); Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(nested,"child.txt"),"child");
        using (var store=Store("non-recursive",[input],new ScanOptions{Algorithms=["SHA256"],Metadata=false,Fingerprints=false,Recursive=false,IncludeHidden=true,IncludeSystem=true}))
        {
            await Scan(store);
            Check(store.Count()==3 && store.GetIssues().Any(x=>x.Code=="NonRecursive"),"nonrecursive traversal excludes child directory explicitly");
        }
    }

    static async Task LinkLoop()
    {
        var input=Dir("links"); File.WriteAllText(Path.Combine(input,"leaf.txt"),"abc",new UTF8Encoding(false));
        var junction=Path.Combine(input,"loop");
        var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(input);
        using(var child=Process.Start(start)!)
        {
            var stdout=child.StandardOutput.ReadToEndAsync(); var stderr=child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            Check(child.ExitCode==0,"controlled Windows junction loop created",await stdout+await stderr);
        }
        using(var store=Store("links-off",[input]))
        {
            await Scan(store);
            Check(store.Count()==1&&store.GetIssues().Any(i=>i.Code=="Link"),"follow-links off records link exclusion");
        }
        using(var store=Store("links-on",[input],new ScanOptions{Algorithms=["SHA256"],Metadata=false,Fingerprints=false,FollowLinks=true}))
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Scan(store,token:timeout.Token);
            Check(store.Count()==1&&store.Info.EnumerationComplete&&store.GetIssues().Any(i=>i.Code=="AlreadyVisitedDirectory"),"follow-links on terminates junction cycle without duplicate work");
        }
        Directory.Delete(junction);
        var outside = Dir("junction-destination");
        var alias = Path.Combine(outside, "alias");
        await MakeJunction(alias, input);
        var victim = Path.Combine(input, "victim.csv");
        File.WriteAllText(victim, "SOURCE CSV MUST SURVIVE");
        using(var store=Store("junction-export",[input]))
        {
            await Scan(store);
            bool rejected=false;
            try{CsvExporter.Export(store,Path.Combine(alias,"victim.csv"));}
            catch(InvalidOperationException){rejected=true;}
            Check(rejected&&File.ReadAllText(victim)=="SOURCE CSV MUST SURVIVE","junction destination cannot overwrite scanned source CSV");
            bool newRejected=false;
            try{CsvExporter.Export(store,Path.Combine(alias,"new-subfolder","report.csv"));}
            catch(InvalidOperationException){newRejected=true;}
            Check(newRejected&&!Directory.Exists(Path.Combine(input,"new-subfolder")),"new output subdirectory through junction also refused");
        }
        using(var store=Store("junction-file-export",[victim]))
        {
            await Scan(store);
            bool rejected=false;
            try{CsvExporter.Export(store,Path.Combine(alias,"victim.csv"));}
            catch(InvalidOperationException){rejected=true;}
            Check(rejected&&File.ReadAllText(victim)=="SOURCE CSV MUST SURVIVE","explicit source file protected through directory alias");
        }
        Directory.Delete(alias);
        var ownAlias=Path.Combine(outside,"own-app");
        await MakeJunction(ownAlias,AppContext.BaseDirectory);
        using(var store=Store("junction-own-app",[ownAlias],new ScanOptions{Metadata=false,Fingerprints=false,FollowLinks=true}))
        {
            await Scan(store);
            Check(store.Count()==0&&store.GetIssues().Any(i=>i.Code=="ApplicationData"),"application folder physical alias excluded from self-scan");
        }
        Directory.Delete(ownAlias);
        var dbDirectory=Dir("selfscan-database");
        File.WriteAllText(Path.Combine(dbDirectory,"only-input.txt"),"abc",new UTF8Encoding(false));
        var dbAlias=Path.Combine(outside,"own-database");
        await MakeJunction(dbAlias,dbDirectory);
        using(var store=new ScanStore(Path.Combine(dbDirectory,"self.sqlite")))
        {
            store.Create([dbAlias],new ScanOptions{Metadata=false,Fingerprints=false,FollowLinks=true});
            await Scan(store);
            Check(store.Count()==1&&store.Results().Single().FileName=="only-input.txt"&&store.GetIssues().Any(i=>i.Code=="ApplicationData"),
                "active database and WAL sidecars excluded through physical directory alias");
        }
        Directory.Delete(dbAlias);
    }

    static async Task MakeJunction(string path,string target)
    {
        var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"/d","/c","mklink","/J",path,target})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if(process.ExitCode!=0)throw new IOException("Junction creation failed: "+await stdout+await stderr);
    }

    static async Task CancelAndChange()
    {
        var input=Dir("large");
        var path=Path.Combine(input,"controlled-256MiB.bin");
        using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        { stream.SetLength(256L*1024*1024); stream.Write("controlled fixture"u8); }
        using(var store=Store("cancel",[path]))
        {
            using var cancel=new CancellationTokenSource();
            long observedBytes=0;
            var progress=new SyncProgress(p=>{if(p.BytesProcessed>0&&p.BytesProcessed<256L*1024*1024){observedBytes=p.BytesProcessed;cancel.Cancel();}});
            await Scan(store,progress:progress,token:cancel.Token);
            var row=store.Results().Single();
            Check(observedBytes>0&&observedBytes<256L*1024*1024,"cancel requested after actual bounded reads",observedBytes.ToString(CultureInfo.InvariantCulture)+" bytes");
            Check(store.Info.Status=="Cancelled"&&row.HashStatus=="Cancelled"&&row.Hashes.Count==0,"cancelled full-file hash never published as complete");
            var export=Path.Combine(Dir("exports"),"cancelled.csv");
            Check(CsvExporter.Export(store,export)==1&&ParseCsv(export)[0]["ScanCompleteness"]=="Cancelled","cancelled scan remains exportable");
        }
        using(var reopened=new ScanStore(Path.Combine(Dir("databases"),"cancel.sqlite"),requireExisting:true))
        {
            Check(reopened.Info.Status=="Cancelled","cancelled scan state persisted across close/reopen");
            await Scan(reopened,resume:true);
            var row=reopened.Results().Single();
            using var verificationStream=File.OpenRead(path);
            var independentlyComputed=Convert.ToHexString(SHA256.HashData(verificationStream)).ToLowerInvariant();
            Check(row.HashStatus=="Completed"&&row.Provenance=="FreshAfterResume"&&row.SHA256==independentlyComputed,
                "reopened cancelled scan resumes with freshly computed complete hash");
            using var stop=new CancellationTokenSource();
            await Scan(reopened,resume:true,progress:new SyncProgress(p=>{
                if(p.Activity=="Invalidating previous results: 1")stop.Cancel();
            }),token:stop.Token);
            Check(reopened.Info.Status=="Cancelled"&&reopened.Results().All(r=>r.Hashes.Count==0&&r.ComputedUtc==null&&r.HashStatus=="Queued"),
                "cancellation after atomic resume invalidation cannot expose stale successful hashes");
        }
        using(var store=Store("changed-during-read",[path]))
        {
            bool changed=false; long changedAt=0;
            var progress=new SyncProgress(p=>
            {
                if(changed||p.BytesProcessed<=0||p.BytesProcessed>=256L*1024*1024)return;
                using(var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))
                { writer.Position=0;writer.WriteByte(91);writer.Flush(true); }
                File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddSeconds(5));
                changed=true;changedAt=p.BytesProcessed;
            });
            await Scan(store,progress:progress);
            var row=store.Results().Single();
            Check(changed&&changedAt>0,"source fixture mutated during a real streamed read",changedAt+" bytes");
            Check(row.HashStatus=="ChangedDuringScan"&&row.Hashes.Count==0&&row.Errors.Any(e=>e.Code=="ChangedDuringScan"),"observable in-read mutation discards full-file digests");
        }
        File.Delete(path); // Disposable generated large fixture; keeps the delivered source lightweight.
    }

    static Task DatabaseValidation()
    {
        var path=Path.Combine(Dir("databases"),"unrelated.sqlite");
        using(var connection=new SqliteConnection("Data Source="+path+";Pooling=False"))
        {
            connection.Open(); using var cmd=connection.CreateCommand();cmd.CommandText="CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated(value) VALUES('keep');";cmd.ExecuteNonQuery();
        }
        var before=File.ReadAllBytes(path);
        bool refused=false;
        try{using var bad=new ScanStore(path,requireExisting:true);}
        catch(Exception ex) when(ex is SqliteException or InvalidDataException){refused=true;}
        Check(refused&&File.ReadAllBytes(path).AsSpan().SequenceEqual(before),"unrelated database refused without byte changes");
        var empty=Path.Combine(Dir("databases"),"empty.sqlite");
        using(var store=new ScanStore(empty)) Check(!store.HasScan,"empty valid schema does not falsely claim a saved scan");
        bool missing=false;
        try{using var store=new ScanStore(Path.Combine(Dir("databases"),"does-not-exist.sqlite"),requireExisting:true);}
        catch(FileNotFoundException){missing=true;}
        Check(missing,"requireExisting refuses missing path without creating it");
        return Task.CompletedTask;
    }

    static Task Synthetic()
    {
        const int count=50000;
        var syntheticRoot=Dir("synthetic-input-location");
        using var store=Store("synthetic-50000",[syntheticRoot],new ScanOptions{Metadata=false,Fingerprints=false});
        var total=Stopwatch.StartNew();var batch=new List<FileResult>(256);
        for(int i=0;i<count;i++)
        {
            var row=ScanStore.NewQueued(Path.Combine(syntheticRoot,$"synthetic-{i:D6}.txt"),"Other",store.Info);
            row.ScanStatus="Completed";row.HashStatus="Completed";row.SizeBytes=3;
            row.Hashes["SHA256"]=AbcSha256;row.Provenance="SyntheticTestFixture";row.ComputedUtc=DateTime.UtcNow;
            batch.Add(row);
            if(batch.Count==256){store.SaveDiscoveries(batch);batch.Clear();}
        }
        if(batch.Count>0)store.SaveDiscoveries(batch);
        var insertionMs=total.Elapsed.TotalMilliseconds;
        store.Info.Status="SyntheticCompleted";store.Info.EnumerationComplete=true;store.Info.FinishedUtc=DateTime.UtcNow;store.SaveInfo();
        Check(store.Count()==count,"50,000 explicitly synthetic result rows stored",insertionMs.ToString("F1",CultureInfo.InvariantCulture)+" ms");
        var pageWatch=Stopwatch.StartNew();var page=store.GetPage("All",49750,250);pageWatch.Stop();
        Check(page.Count==250&&page[^1].FileName=="synthetic-049999.txt","paged read reaches last synthetic row",pageWatch.Elapsed.TotalMilliseconds.ToString("F1",CultureInfo.InvariantCulture)+" ms");
        var export=Path.Combine(Dir("exports"),"synthetic-50000.csv");
        var exportWatch=Stopwatch.StartNew();var exported=CsvExporter.Export(store,export);exportWatch.Stop();
        long parsed=0;
        using(var parser=new TextFieldParser(export,Encoding.UTF8,true))
        {
            parser.TextFieldType=FieldType.Delimited;parser.SetDelimiters(",");parser.HasFieldsEnclosedInQuotes=true;
            var headers=parser.ReadFields()!;
            while(!parser.EndOfData){if(parser.ReadFields()!.Length!=headers.Length)throw new InvalidDataException("Synthetic CSV field count");parsed++;}
        }
        Check(exported==count&&parsed==count,"export covers all 50,000 stored rows beyond the GUI page",exportWatch.Elapsed.TotalMilliseconds.ToString("F1",CultureInfo.InvariantCulture)+" ms; "+new FileInfo(export).Length+" bytes");
        Check(store.GetPage("Images",0,250).Count==0,"filtered paging uses shared inventory");
        Console.WriteLine("SYNTHETIC ONLY: 50,000 generated result rows, no archive files hashed; total "+total.Elapsed.TotalSeconds.ToString("F2",CultureInfo.InvariantCulture)+"s");
        return Task.CompletedTask;
    }
    sealed class SyncProgress(Action<ScanProgress> callback):IProgress<ScanProgress>
    { public void Report(ScanProgress value)=>callback(value); }
}





