using System.IO;
using HashVista;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

internal static class Program
{
    static readonly List<string> Passes = [];
    static readonly List<object> Observations = [];
    static string stage = "", run = "";
    static MediaProcessor Media => new(Path.Combine(stage,"dependencies"));
    static ScanOptions HashOnly => new(){Algorithms=["SHA256"],Metadata=false,Fingerprints=false,StreamHashes=false};
    static void Check(bool ok,string name) { if(!ok) throw new Exception("FAILED: "+name); Passes.Add(name); Console.WriteLine("PASS: "+name); }
    static async Task<int> Main(string[] args)
    {
        if(args.Length>0 && args[0]=="benchmark") return await Benchmark(args);
        stage=Path.GetFullPath(args[0]);
        run=Path.Combine(stage,"evidence","regression122-runs","run-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(run);
        string? failure=null;
        try { await CancelledResume(); await LinkExclusions(); await DurableStagesAndProgress(); }
        catch(Exception ex) { failure=ex.ToString(); Console.Error.WriteLine(failure); }
        var report=new {Passed=Passes.Count,Tests=Passes,Failure=failure,TimestampUtc=DateTime.UtcNow,RunDirectory=run,Assembly=typeof(ScanEngine).Assembly.Location,AssemblySHA256=Digest(typeof(ScanEngine).Assembly.Location),Observations};
        await File.WriteAllTextAsync(Path.Combine(stage,"evidence","regression122.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
        Console.WriteLine($"RESULT: {Passes.Count} passed; failure={(failure==null?"none":"yes")}");
        return failure==null?0:1;
    }
    static string Digest(string path) { using var input=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    static ScanStore Store(string name,string[] roots,ScanOptions? options=null)
    {
        var store=new ScanStore(Path.Combine(run,name+".ghscan"));store.Create(roots,options??HashOnly);return store;
    }
    static async Task CancelledResume()
    {
        string input=Path.Combine(run,"resume-input");Directory.CreateDirectory(input);
        for(int i=0;i<1000;i++)await File.WriteAllTextAsync(Path.Combine(input,i==0?"مرحبا, ملف.txt":$"file-{i:0000}.txt"),i==0?"abc":i==1?"":"x",new UTF8Encoding(false));
        string db;
        using(var store=Store("resume",[input]))
        {
            db=store.DatabasePath;
            await new ScanEngine(store,Media).RunAsync(false,null,CancellationToken.None);
            Check(store.Info.Status=="Completed"&&store.Count()==1000,"initial controlled 1000-file scan completes");
            var initial=store.Results().ToList();
            Check(initial.Single(r=>r.FileName=="مرحبا, ملف.txt").SHA256.Equals("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",StringComparison.OrdinalIgnoreCase),"full-file SHA256 matches independent abc known vector");
            Check(initial.Single(r=>r.FileName=="file-0001.txt").SizeBytes==0&&initial.Single(r=>r.FileName=="file-0001.txt").SHA256.Equals("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",StringComparison.OrdinalIgnoreCase),"empty file preserves zero size and known SHA256");
            Check(initial.All(r=>r.SHA256.Equals(Digest(r.FullPath),StringComparison.OrdinalIgnoreCase)),"all 1000 full-file hashes match independent one-shot calculations");
            var allCsv=Path.Combine(run,"initial.csv");Check(CsvExporter.Export(store,allCsv)==1000,"initial export includes every stored file");
            var parsed=ParseCsv(allCsv);
            Check(parsed.Count==1000&&parsed.Single(r=>r["FileName"]=="مرحبا, ملف.txt")["SHA256"].Equals(initial.Single(r=>r.FileName=="مرحبا, ملف.txt").SHA256,StringComparison.OrdinalIgnoreCase),"independent CSV parser preserves Arabic/comma path and computed hash");
            using var cts=new CancellationTokenSource();ScanProgress? trigger=null;
            await new ScanEngine(store,Media).RunAsync(true,new SyncProgress(p=>{if(trigger==null&&p.Activity.StartsWith("Inventory:",StringComparison.Ordinal)&&p.Discovered>=512){trigger=p;cts.Cancel();}}),cts.Token);
            Check(trigger?.Discovered==512,"resume cancelled deterministically at first 512-file inventory flush");
            Check(store.Info.Status=="Cancelled"&&!store.Info.EnumerationComplete,"cancelled enumeration is explicitly incomplete");
            Check(store.Count()==1000&&store.Info.TotalFiles==1000&&store.Info.OtherFiles==1000,"cancelled resume counters reconcile all 1000 retained rows");
            Check(store.Results().All(r=>r.Hashes.Count==0&&r.HashStatus!="Completed"),"resume cancellation exposes no stale successful digests");
            Check(PageCoverage(store)==1000,"all retained results remain reachable through 250-row paging bounds");
            Observations.Add(new{Name="cancelled-resume",Trigger=trigger,Stored=store.Count(),store.Info.TotalFiles,store.Info.Status});
        }
        using(var reopened=new ScanStore(db,requireExisting:true))
        {
            Check(reopened.Count()==1000&&reopened.Info.TotalFiles==1000&&PageCoverage(reopened)==1000,"reopened cancelled scan retains correct counters and full paging reachability");
            var csv=Path.Combine(run,"cancelled.csv");long count=CsvExporter.Export(reopened,csv);
            using var sidecar=JsonDocument.Parse(await File.ReadAllTextAsync(csv+".scan.json"));
            Check(count==1000&&ParseCsv(csv).Count==1000&&sidecar.RootElement.GetProperty("ExportedRows").GetInt64()==1000&&sidecar.RootElement.GetProperty("Scan").GetProperty("TotalFiles").GetInt64()==1000,"CSV rows and sidecar totals agree after cancelled resume and reopen");
            Check(sidecar.RootElement.GetProperty("Scan").GetProperty("Status").GetString()=="Cancelled"&&!sidecar.RootElement.GetProperty("Scan").GetProperty("EnumerationComplete").GetBoolean(),"CSV companion records cancellation and incomplete traversal");
            // Simulate a scan saved by the reviewed older build with stale nonzero cached totals.
            reopened.Info.TotalFiles=512;reopened.Info.OtherFiles=512;reopened.SaveInfo();
        }
        using(var migrated=new ScanStore(db,requireExisting:true))
        {
            Check(migrated.Count()==1000&&migrated.Info.TotalFiles==1000&&migrated.Info.OtherFiles==1000&&PageCoverage(migrated)==1000,"opening older scan with stale nonzero 512 counters repairs all 1000 retained rows");
            var migrationCsv=Path.Combine(run,"older-counter-migration.csv");CsvExporter.Export(migrated,migrationCsv);
            using var migrationSidecar=JsonDocument.Parse(await File.ReadAllTextAsync(migrationCsv+".scan.json"));
            Check(ParseCsv(migrationCsv).Count==1000&&migrationSidecar.RootElement.GetProperty("Scan").GetProperty("TotalFiles").GetInt64()==1000,"repaired older scan exports all rows with matching sidecar total");
            Observations.Add(new{Name="older-scan-nonzero-counter-repair",SimulatedSavedCounter=512,Stored=migrated.Count(),migrated.Info.TotalFiles,migrated.Info.OtherFiles});
        }
        Check(await File.ReadAllTextAsync(Path.Combine(input,"مرحبا, ملف.txt"))=="abc","scan/resume/export left fixture source contents unchanged");
    }
    static long PageCoverage(ScanStore store)
    {
        var ids=new HashSet<string>();int offset=0;
        do { foreach(var row in store.GetPage("All",offset,250)) ids.Add(row.FileId);if(offset+250>=store.Info.TotalFiles)break;offset+=250; }while(true);
        return ids.Count;
    }
    static async Task LinkExclusions()
    {
        string input=Path.Combine(run,"leaf-links");Directory.CreateDirectory(input);
        string outside=Path.Combine(run,"physical-target.txt");await File.WriteAllTextAsync(outside,"physical target",new UTF8Encoding(false));
        using var store=Store("links",[input],new ScanOptions{Algorithms=["SHA256"],Metadata=false,Fingerprints=false,FollowLinks=true});
        string appLink=Path.Combine(input,"application-link.dll"),databaseLink=Path.Combine(input,"database-link.ghscan"),targetLink=Path.Combine(input,"target-link.txt");
        File.CreateSymbolicLink(appLink,typeof(ScanEngine).Assembly.Location);
        File.CreateSymbolicLink(databaseLink,store.DatabasePath);
        File.CreateSymbolicLink(targetLink,outside);
        Check(File.GetAttributes(appLink).HasFlag(FileAttributes.ReparsePoint)&&File.GetAttributes(databaseLink).HasFlag(FileAttributes.ReparsePoint),"controlled leaf file symlinks created on Windows");
        await new ScanEngine(store,Media).RunAsync(false,null,CancellationToken.None);
        var rows=store.Results().ToList();var issues=store.GetIssues();
        Check(rows.Count==1&&rows[0].FullPath.Equals(ScanStore.NormalizePath(targetLink),StringComparison.OrdinalIgnoreCase),"followed leaf links into application/database are excluded while allowed external link stays");
        Check(issues.Any(i=>i.Path.Equals(ScanStore.NormalizePath(appLink),StringComparison.OrdinalIgnoreCase)&&i.Code=="ApplicationData")&&issues.Any(i=>i.Path.Equals(ScanStore.NormalizePath(databaseLink),StringComparison.OrdinalIgnoreCase)&&i.Code=="ApplicationData"),"both self-scan leaf-link exclusions are recorded explicitly");
        Check(rows[0].SHA256.Equals(Digest(outside),StringComparison.OrdinalIgnoreCase),"allowed file symlink hashes actual target contents");
        Check(ScanStore.ResolvePhysicalPath(targetLink).Equals(ScanStore.ResolvePhysicalPath(outside),StringComparison.OrdinalIgnoreCase),"leaf symlink resolves to the actual physical target path");
        var physicalIdentity=new DiskIdentityResolver().Resolve(ScanStore.ResolvePhysicalPath(outside));
        Check(rows[0].VolumeRoot==physicalIdentity.VolumeRoot&&rows[0].DiskNumber==physicalIdentity.DiskNumber&&rows[0].DiskSerialNumber==physicalIdentity.DiskSerialNumber&&rows[0].DiskSerialStatus==physicalIdentity.Status,"symlink row disk identity matches resolved target identity on tested volume");
        Observations.Add(new{Name="leaf-link-exclusions",Rows=rows,Issues=issues,PhysicalTarget=ScanStore.ResolvePhysicalPath(outside),CrossVolumeTested=false});
    }
    static async Task DurableStagesAndProgress()
    {
        string source=Path.Combine(stage,"tests","fixtures","media","فيديو, two-audio.mp4");
        if(!File.Exists(source))throw new FileNotFoundException("Required generated video fixture missing",source);
        string input=Path.Combine(run,"video-input");Directory.CreateDirectory(input);string video=Path.Combine(input,"test.mp4");File.Copy(source,video);
        var options=new ScanOptions{Algorithms=["SHA256"],Metadata=true,Fingerprints=true,StreamHashes=true};
        using(var store=Store("video",[input],options))
        {
            bool sawHashing=false,sawMedia=false;var events=new List<ScanProgress>();var states=new List<object>();
            var progress=new SyncProgress(p=>{
                events.Add(p);
                if(p.Activity.StartsWith("Hashing:",StringComparison.Ordinal)){
                    var row=store.GetPage("All",0,1).Single();sawHashing=row.HashStatus=="Hashing";states.Add(new{Phase="Hashing",row.HashStatus,Hash=row.SHA256});
                    // Deterministically let the rate-limited next media transition be observable; excluded from performance benchmarks.
                    Thread.Sleep(250);
                }
                if(p.Activity.StartsWith("Checking media tools:",StringComparison.Ordinal)||p.Activity.StartsWith("Video fingerprint frame",StringComparison.Ordinal)){
                    var row=store.GetPage("All",0,1).Single();if(row.HashStatus=="Completed"&&row.SHA256.Equals(Digest(video),StringComparison.OrdinalIgnoreCase))sawMedia=true;
                    states.Add(new{Phase=p.Activity,row.HashStatus,Hash=row.SHA256});
                }
            });
            await new ScanEngine(store,Media).RunAsync(false,progress,CancellationToken.None);
            var final=store.Results().Single();
            Check(sawHashing,"Hashing status is durably visible to readers at hashing-start progress callback");
            Check(sawMedia,"completed full-file digest is persisted before media processing callbacks");
            Check(final.HashStatus=="Completed"&&final.MetadataStatus=="Completed"&&final.FingerprintStatus=="Completed"&&final.StreamHashStatus=="Completed","real video hash/metadata/six-frame/encoded-stream stages complete");
            Check(events.Where(p=>p.Processed<p.TotalFiles&&p.TotalFiles>0).All(p=>p.OverallPercentage<100),"overall percentage stays below 100 while any file/stage is unfinished");
            Check(events.Last().OverallPercentage==100&&store.Info.Status=="Completed","100 percent is emitted only after completed terminal state");
            Check(final.Frames.Count==6&&final.Streams.Count==3&&final.StreamHashes.Count==3,"media details preserve six samples and all three streams");
            Observations.Add(new{Name="durable-stages-progress",Events=events,PersistedStates=states,Final=final});
        }
        using(var store=Store("video-cancel",[input],options))
        using(var cts=new CancellationTokenSource())
        {
            bool cancelledInMedia=false;var events=new List<ScanProgress>();
            var progress=new SyncProgress(p=>{
                events.Add(p);
                if(p.Activity.StartsWith("Hashing:",StringComparison.Ordinal)||p.Activity.StartsWith("Hashing ",StringComparison.Ordinal))Thread.Sleep(250);
                if(!cancelledInMedia&&p.Activity.StartsWith("Checking media tools:",StringComparison.Ordinal)){cancelledInMedia=true;cts.Cancel();}
            });
            await new ScanEngine(store,Media).RunAsync(false,progress,cts.Token);
            var row=store.Results().Single();
            Observations.Add(new{Name="cancelled-media-progress",Events=events,Final=row});
            Check(cancelledInMedia&&store.Info.Status=="Cancelled","cancellation is observed at a real media-stage transition");
            Check(row.HashStatus=="Completed"&&row.SHA256.Equals(Digest(video),StringComparison.OrdinalIgnoreCase),"cancelling media retains the previously completed full-file hash");
            Check(events.Last().OverallPercentage<100&&events.Where(p=>p.Processed<p.TotalFiles&&p.TotalFiles>0).All(p=>p.OverallPercentage<100),"cancelled media scan never reports overall 100 percent");

        }
    }
    static List<Dictionary<string,string>> ParseCsv(string path)
    {
        using var parser=new TextFieldParser(path,Encoding.UTF8,true){HasFieldsEnclosedInQuotes=true,TrimWhiteSpace=false};parser.SetDelimiters(",");
        string[] headers=parser.ReadFields()!;var rows=new List<Dictionary<string,string>>();
        while(!parser.EndOfData){var fields=parser.ReadFields()!;if(fields.Length!=headers.Length)throw new Exception("CSV width mismatch");rows.Add(headers.Zip(fields).ToDictionary(x=>x.First,x=>x.Second));}
        return rows;
    }
    static async Task<int> Benchmark(string[] args)
    {
        // benchmark FIXTURE_DIR DB_PATH RESULT_JSON LABEL; no artificial delays or media stages.
        string input=Path.GetFullPath(args[1]),db=Path.GetFullPath(args[2]),resultPath=Path.GetFullPath(args[3]),label=args[4];
        using var store=new ScanStore(db);store.Create([input],HashOnly);
        var engine=new ScanEngine(store,new MediaProcessor());var clock=Stopwatch.StartNew();
        await engine.RunAsync(false,null,CancellationToken.None);clock.Stop();
        long count=store.Count();bool completed=store.Info.Status=="Completed"&&count==3001&&store.Results().All(r=>r.HashStatus=="Completed");
        var result=new{Label=label,Milliseconds=clock.Elapsed.TotalMilliseconds,Files=count,Bytes=store.Info.TotalBytes,store.Info.Status,Completed=completed,AssemblySHA256=Digest(typeof(ScanEngine).Assembly.Location),TimestampUtc=DateTime.UtcNow};
        await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(result));return completed?0:1;
    }
    sealed class SyncProgress(Action<ScanProgress> callback):IProgress<ScanProgress>{public void Report(ScanProgress value)=>callback(value);}
}
