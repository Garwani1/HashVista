using System.Text.Json;
namespace HashVista;
public sealed class ScanOptions {
 public string[] Algorithms {get;set;} = ["SHA256"];
 public bool Metadata {get;set;}
 public bool Fingerprints {get;set;}
 public bool StreamHashes {get;set;}
 public bool Recursive {get;set;} = true;
 public bool IncludeHidden {get;set;}
 public bool IncludeSystem {get;set;}
 public bool FollowLinks {get;set;}
 public string Profile {get;set;} = "HashVista-Media-v1";
}
public sealed class ScanInfo {
 public string ScanId {get;set;} = Guid.NewGuid().ToString("N");
 public string[] Roots {get;set;} = [];
 public ScanOptions Options {get;set;} = new();
 public string Status {get;set;} = "Queued";
 public bool EnumerationComplete {get;set;}
 public DateTime CreatedUtc {get;set;} = DateTime.UtcNow;
 public DateTime? FinishedUtc {get;set;}
 public string ToolVersions {get;set;} = "";
 // Filled during inventory, before hashing starts. Progress is based on these totals.
 public long TotalFiles {get;set;}
 public long TotalBytes {get;set;}
 public long ImageFiles {get;set;}
 public long ImageBytes {get;set;}
 public long VideoFiles {get;set;}
 public long VideoBytes {get;set;}
 public long OtherFiles {get;set;}
 public long OtherBytes {get;set;}
 public List<DiskIdentityInfo> Disks {get;set;} = [];
}
public sealed class DiskIdentityInfo {
 public string VolumeRoot {get;set;} = "";
 public string VolumeGuidPath {get;set;} = "";
 public string DiskSerialNumber {get;set;} = "";
 public string DiskNumber {get;set;} = "";
 public string DiskModel {get;set;} = "";
 public string Status {get;set;} = "NotQueried";
 public string Error {get;set;} = "";
}
public sealed class StageError {
 public string Stage {get;set;} = "";
 public string Code {get;set;} = "";
 public string Message {get;set;} = "";
}
public sealed class FrameSample {
 public double TargetSeconds {get;set;}
 public double? ActualSeconds {get;set;}
 public string Status {get;set;} = "Queued";
 public string? Fingerprint {get;set;}
 public string Profile {get;set;} = "HashVista-pHash64-v1";
 public string? Error {get;set;}
}
public sealed class FileResult {
 public string ScanId {get;set;} = "";
 public string FileId {get;set;} = Guid.NewGuid().ToString("N");
 public string FileName {get;set;} = "";
 public string Extension {get;set;} = "";
 public string FullPath {get;set;} = "";
 public string VolumeRoot {get;set;} = "";
 public string DiskSerialNumber {get;set;} = "";
 public string DiskNumber {get;set;} = "";
 public string DiskSerialStatus {get;set;} = "NotQueried";
 public string Kind {get;set;} = "Other";
 public long? SizeBytes {get;set;}
 public DateTime? CreatedTimeUtc {get;set;}
 public DateTime? LastWriteTimeUtc {get;set;}
 public string ScanStatus {get;set;} = "Queued";
 public string HashStatus {get;set;} = "NotRequested";
 public string MetadataStatus {get;set;} = "NotRequested";
 public string FingerprintStatus {get;set;} = "NotRequested";
 public string StreamHashStatus {get;set;} = "NotRequested";
 public Dictionary<string,string> Hashes {get;set;} = new();
 public Dictionary<string,string> Media {get;set;} = new();
 public List<JsonElement> Streams {get;set;} = [];
 public List<FrameSample> Frames {get;set;} = [];
 public List<JsonElement> StreamHashes {get;set;} = [];
 public List<StageError> Errors {get;set;} = [];
 public DateTime? ComputedUtc {get;set;}
 public string Provenance {get;set;} = "Fresh";
 public string? ReuseValidation {get;set;}
 public string ConfigurationJson {get;set;} = "";
 public string SHA256 => Hashes.GetValueOrDefault("SHA256", "");
 public string ErrorMessage => string.Join("\n", Errors.Select(e => e.Stage+": "+e.Message));
}
public sealed record ScanIssue(string Path, string Stage, string Code, string Message);
public sealed record ScanProgress(
 long Discovered, long Processed, long Failed, long Skipped, long BytesProcessed, string Activity,
 long TotalFiles, long TotalBytes, long HandledBytes, long CurrentFileBytes, double OverallPercentage);
