using System.IO;
using HashVista;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var fixtureRoot = Path.Combine(project, "evidence", "media-fixtures");
Directory.CreateDirectory(fixtureRoot);
var dependencyRoot = Path.Combine(project, "dependencies");
var processor = new MediaProcessor(dependencyRoot);
var passed = new List<string>();
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); passed.Add(name); Console.WriteLine("PASS: " + name); }
async Task<string> Tool(string name, params string[] arguments) {
 using var p = new Process { StartInfo = new ProcessStartInfo(Path.Combine(dependencyRoot, name+".exe")) { RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true } };
 p.StartInfo.Environment["LC_ALL"]="C"; p.StartInfo.Environment["LANG"]="C";
 if(name=="exiftool"){p.StartInfo.RedirectStandardInput=true;p.StartInfo.StandardInputEncoding=new UTF8Encoding(false);foreach(var a in new[]{"-charset","filename=UTF8","-@","-"})p.StartInfo.ArgumentList.Add(a);}else foreach(var a in arguments)p.StartInfo.ArgumentList.Add(a);
 p.Start(); var stdout=p.StandardOutput.ReadToEndAsync(); var stderr=p.StandardError.ReadToEndAsync(); if(name=="exiftool"){await p.StandardInput.WriteAsync(string.Join("\n",arguments)+"\n");p.StandardInput.Close();} await p.WaitForExitAsync();
 if(p.ExitCode!=0)throw new Exception(name+": "+await stderr); return await stdout;
}
FileResult Row(string path,string kind)=> new(){FullPath=path,FileName=Path.GetFileName(path),Extension=Path.GetExtension(path),Kind=kind,HashStatus="Completed",Hashes=new(){["SHA256"]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}};
Console.WriteLine(await processor.GetVersionsAsync(default));
var rows = new List<FileResult>();
foreach(string extension in new[]{"png","jpg","bmp","gif","tiff","webp"}) {
 string path=Path.Combine(fixtureRoot,"صورة, عينة."+extension);
 await Tool("ffmpeg","-v","error","-y","-f","lavfi","-i","testsrc=size=128x96:rate=1","-frames:v","1","-threads","1",path);
 if(extension=="jpg")await Tool("exiftool","-overwrite_original","-DateTimeOriginal=2020:02:03 04:05:06","-OffsetTimeOriginal=+04:00","-Make=HashVistaTests","-Model=TestCamera","-LensModel=TestLens","-ImageDescription=Fixture, description","-GPSLatitude=25.2","-GPSLatitudeRef=N","-GPSLongitude=55.3","-GPSLongitudeRef=E",path);
 var row=Row(path,"Image"); await processor.ProcessAsync(row,new(){Metadata=true,Fingerprints=true},default);
 Check(row.MetadataStatus=="Completed",extension+" metadata: "+row.ErrorMessage);
 Check(row.FingerprintStatus=="Completed",extension+" fingerprint: "+row.ErrorMessage);
 Check(row.Media["ImagePixelSHA256"].Length==64 && row.Media["PerceptualHash"].Length==16,extension+" hash dimensions");
 Check(row.Media.GetValueOrDefault("Width")=="128" && row.Media.GetValueOrDefault("Height")=="96",extension+" actual image dimensions");
 var second=Row(path,"Image");await processor.ProcessAsync(second,new(){Metadata=true,Fingerprints=true},default);
 Check(second.Media["ImagePixelSHA256"]==row.Media["ImagePixelSHA256"] && second.Media["PerceptualHash"]==row.Media["PerceptualHash"],extension+" repeatability");
 var preview=await processor.PreviewAsync(row,default);Check(preview.Length==1 && preview[0].Length>100,extension+" lazy preview");
 if(extension=="jpg") {Check(row.Media["CaptureTime"]=="2020:02:03 04:05:06"&&row.Media["CaptureTimeOffset"]=="+04:00","EXIF raw capture time and offset");Check(row.Media["CameraMake"]=="HashVistaTests"&&row.Media["Lens"]=="TestLens"&&row.Media.ContainsKey("GPSLatitude"),"EXIF camera lens GPS");}
 rows.Add(row);
}
string video=Path.Combine(fixtureRoot,"فيديو, قصير.mp4");
await Tool("ffmpeg","-v","error","-y","-f","lavfi","-i","testsrc=size=128x96:rate=12","-f","lavfi","-i","sine=frequency=440:sample_rate=44100","-t","2","-c:v","mpeg4","-q:v","3","-c:a","aac","-threads","1",video);
var v=Row(video,"Video");await processor.ProcessAsync(v,new(){Metadata=true,Fingerprints=true,StreamHashes=true},default);
Check(v.MetadataStatus=="Completed"&&v.Streams.Count==2,"video metadata and both streams");
Check(v.FingerprintStatus=="Completed"&&v.Frames.Count==6&&v.Frames.All(x=>x.Fingerprint?.Length==16),"six actual video frame fingerprints: "+v.ErrorMessage);
Check(v.Frames.All(x=>x.ActualSeconds==null),"unobserved actual timestamps remain null");
Check(v.StreamHashStatus=="Completed"&&v.StreamHashes.Count==2,"encoded stream hashes: "+v.ErrorMessage);
var direct=await Tool("ffmpeg","-v","error","-nostdin","-threads","1","-protocol_whitelist","file,pipe","-i",video,"-map","0:0","-map","0:1","-c","copy","-f","streamhash","-hash","sha256","pipe:1");
foreach(var entry in v.StreamHashes)Check(direct.Contains(entry.GetProperty("Hash").GetString()!,StringComparison.OrdinalIgnoreCase),"stream "+entry.GetProperty("StreamIndex")+" matches exact direct ffmpeg invocation");
var decoded=await Tool("ffmpeg","-v","error","-nostdin","-threads","1","-protocol_whitelist","file,pipe","-i",video,"-map","0:0","-map","0:1","-f","streamhash","-hash","sha256","pipe:1");
Check(!direct.Equals(decoded,StringComparison.Ordinal),"encoded stream-copy hashes differ from decoded defaults");
var v2=Row(video,"Video");await processor.ProcessAsync(v2,new(){Metadata=true,Fingerprints=true},default);
Check(v.Frames.Select(x=>x.Fingerprint).SequenceEqual(v2.Frames.Select(x=>x.Fingerprint)),"video sampled fingerprints repeatability");
var previews=await processor.PreviewAsync(v,default);Check(previews.Length==6,"six lazy video preview images"); rows.Add(v);
string shortVideo=Path.Combine(fixtureRoot,"very-short.mp4");
await Tool("ffmpeg","-v","error","-y","-f","lavfi","-i","testsrc=size=64x48:rate=10","-t","0.1","-c:v","mpeg4","-threads","1",shortVideo);
var sh=Row(shortVideo,"Video");await processor.ProcessAsync(sh,new(){Metadata=true,Fingerprints=true},default);Check(sh.Frames.Count==6 && sh.Frames.All(x=>x.Status!="Queued")&&sh.FingerprintStatus is "Completed" or "Partial" or "Failed","very short video produces explicit six sample outcomes");rows.Add(sh);
string invalid=Path.Combine(fixtureRoot,"broken.heic");await File.WriteAllTextAsync(invalid,"not a media container");var broken=Row(invalid,"Image");await processor.ProcessAsync(broken,new(){Metadata=true,Fingerprints=true},default);Check(broken.HashStatus=="Completed"&&broken.SHA256.Length==64&&broken.MetadataStatus!="Completed"&&broken.FingerprintStatus!="Completed","invalid media retains file SHA256 and explicit media failure");rows.Add(broken);
var disabled=Row(video,"Video");await processor.ProcessAsync(disabled,new(){Metadata=false,Fingerprints=false,StreamHashes=false},default);Check(disabled.Media.Count==0&&disabled.Streams.Count==0&&disabled.Frames.Count==0,"disabled media stages do not invoke decoding");
using(var cancellation=new CancellationTokenSource()){cancellation.Cancel();try{await processor.PreviewAsync(v,cancellation.Token);throw new Exception("No cancellation");}catch(OperationCanceledException){passed.Add("preview cancellation propagated");}}
string nef=Path.Combine(fixtureRoot,"ExifTool-Nikon.nef");
if(File.Exists(nef)) {
 var raw=Row(nef,"Image");await processor.ProcessAsync(raw,new(){Metadata=true,Fingerprints=true},default);rows.Add(raw);
 Check(raw.MetadataStatus=="Completed"&&raw.Media.GetValueOrDefault("Format")=="NEF","real upstream NEF metadata fixture");
 Check(raw.FingerprintStatus!="Queued"&&raw.FingerprintStatus!="Processing","NEF embedded-preview availability explicitly reported");
 Console.WriteLine("NEF fixture fingerprint status="+raw.FingerprintStatus+" error="+raw.ErrorMessage);
 string syntheticNef=Path.Combine(fixtureRoot,"embedded-preview-fixture.nef");File.Copy(nef,syntheticNef,true);
 try {
 await Tool("exiftool","-m","-overwrite_original","-PreviewImage<="+Path.Combine(fixtureRoot,"صورة, عينة.jpg"),syntheticNef);
 var injected=Row(syntheticNef,"Image");await processor.ProcessAsync(injected,new(){Metadata=true,Fingerprints=true},default);rows.Add(injected);
 Check(injected.FingerprintStatus=="Completed"&&injected.Media.GetValueOrDefault("PixelSource")=="EmbeddedPreview","NEF container with generated embedded JPEG uses explicit EmbeddedPreview pixels");
 Check((await processor.PreviewAsync(injected,default)).Length==1,"NEF generated embedded JPEG lazy preview");
 } catch(Exception ex){throw new Exception("NEF embedded preview fixture failed",ex);}
}
string heic=Path.Combine(fixtureRoot,"libheif-example.heic");
if(File.Exists(heic)) {
 var h=Row(heic,"Image");await processor.ProcessAsync(h,new(){Metadata=true,Fingerprints=true},default);rows.Add(h);
 Check(h.MetadataStatus=="Completed"&&h.Media.GetValueOrDefault("Format") is "HEIC" or "HEIF","real libheif HEIC metadata fixture");
 Check(h.FingerprintStatus=="Completed","real HEIC image fingerprint: "+h.ErrorMessage);
 Check((await processor.PreviewAsync(h,default)).Length==1,"real HEIC lazy preview");
}
var extendedFixtures=Path.Combine(project,"tests","fixtures","media");
string rotatedJpeg=Path.Combine(extendedFixtures,"orientation6-exif.jpg");
if(File.Exists(rotatedJpeg)) {
 var oriented=Row(rotatedJpeg,"Image");await processor.ProcessAsync(oriented,new(){Metadata=true,Fingerprints=true},default);rows.Add(oriented);
 Check(oriented.MetadataStatus=="Completed"&&oriented.Media.GetValueOrDefault("Orientation")=="6","EXIF orientation 6 is read as supplied");
 string rotatedReference=Path.Combine(fixtureRoot,"orientation6-reference.rgb");
 await Tool("ffmpeg","-v","error","-y","-noautorotate","-i",rotatedJpeg,"-frames:v","1","-vf","transpose=1,scale=256:256:flags=lanczos,format=rgba,format=rgb24","-pix_fmt","rgb24","-threads","1","-f","rawvideo",rotatedReference);
 string expected=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(rotatedReference)));
 Check(oriented.Media.GetValueOrDefault("ImagePixelSHA256")==expected,"orientation 6 normalized pixel SHA256 matches independent rotated RGB24 output");
 string unrotatedReference=Path.Combine(fixtureRoot,"orientation6-unrotated.rgb");
 await Tool("ffmpeg","-v","error","-y","-noautorotate","-i",rotatedJpeg,"-frames:v","1","-vf","scale=256:256:flags=lanczos,format=rgba,format=rgb24","-pix_fmt","rgb24","-threads","1","-f","rawvideo",unrotatedReference);
 Check(expected!=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(unrotatedReference))),"orientation transform materially differs from unrotated pixels");
}
string transparentPng=Path.Combine(extendedFixtures,"transparent.png");
if(File.Exists(transparentPng)) {
 var transparent=Row(transparentPng,"Image");await processor.ProcessAsync(transparent,new(){Metadata=true,Fingerprints=true},default);rows.Add(transparent);
 Check(transparent.FingerprintStatus=="Completed"&&transparent.Media.GetValueOrDefault("ImagePixelSHA256")==Convert.ToHexString(SHA256.HashData(new byte[256*256*3])),"fully transparent image composites to exact all-black RGB256 expected hash");
}
string animation=Path.Combine(extendedFixtures,"animated.gif");
if(File.Exists(animation)) {
 var animated=Row(animation,"Image");await processor.ProcessAsync(animated,new(){Metadata=true,Fingerprints=true},default);rows.Add(animated);
 string firstReference=Path.Combine(fixtureRoot,"animation-first.rgb");
 await Tool("ffmpeg","-v","error","-y","-i",animation,"-frames:v","1","-vf",@"select=eq(n\,0),scale=256:256:flags=lanczos,format=rgba,format=rgb24","-pix_fmt","rgb24","-threads","1","-f","rawvideo",firstReference);
 string firstHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(firstReference)));
 Check(animated.FingerprintStatus=="Completed"&&animated.Media.GetValueOrDefault("ImagePixelSHA256")==firstHash,"animated GIF normalized hash uses first frame only");
 string secondReference=Path.Combine(fixtureRoot,"animation-second.rgb");
 await Tool("ffmpeg","-v","error","-y","-i",animation,"-frames:v","1","-vf",@"select=eq(n\,1),scale=256:256:flags=lanczos,format=rgba,format=rgb24","-pix_fmt","rgb24","-threads","1","-f","rawvideo",secondReference);
 Check(firstHash!=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(secondReference))),"animated fixture has distinct first/second normalized frames");
}
string multipleStreams=Path.Combine(extendedFixtures,"فيديو, two-audio.mp4");
if(File.Exists(multipleStreams)) {
 var multi=Row(multipleStreams,"Video");await processor.ProcessAsync(multi,new(){Metadata=true,Fingerprints=true,StreamHashes=true},default);rows.Add(multi);
 Check(multi.MetadataStatus=="Completed"&&multi.Streams.Count==3&&multi.Streams.Count(s=>s.GetProperty("codec_type").GetString()=="audio")==2,"multi-stream MP4 preserves one video and two audio streams");
 Check(multi.StreamHashStatus=="Completed"&&multi.StreamHashes.Count==3&&multi.StreamHashes.Select(s=>s.GetProperty("StreamIndex").GetInt32()).Order().SequenceEqual(new[]{0,1,2}),"three encoded hashes retain original stream indices");
 string multiDirect=await Tool("ffmpeg","-v","error","-nostdin","-threads","1","-protocol_whitelist","file,pipe","-i",multipleStreams,"-map","0:0","-map","0:1","-map","0:2","-c","copy","-f","streamhash","-hash","sha256","pipe:1");
 foreach(var stream in multi.StreamHashes)Check(multiDirect.Contains(stream.GetProperty("Hash").GetString()!,StringComparison.OrdinalIgnoreCase),"multistream direct hash matches stream "+stream.GetProperty("StreamIndex"));
}
string variableRate=Path.Combine(extendedFixtures,"variable-frame-rate.mp4");
if(File.Exists(variableRate)) {
 var variable=Row(variableRate,"Video");await processor.ProcessAsync(variable,new(){Metadata=true,Fingerprints=true},default);rows.Add(variable);
 using var probeDoc=JsonDocument.Parse(await Tool("ffprobe","-v","error","-select_streams","v:0","-show_entries","stream=r_frame_rate,avg_frame_rate","-of","json",variableRate));
 var measured=probeDoc.RootElement.GetProperty("streams")[0];
 Check(variable.MetadataStatus=="Completed"&&variable.Media.GetValueOrDefault("FrameRate")==measured.GetProperty("r_frame_rate").GetString()&&variable.Media.GetValueOrDefault("AverageFrameRate")==measured.GetProperty("avg_frame_rate").GetString(),"VFR r_frame_rate and avg_frame_rate rational strings match independent ffprobe");
 using var timesDoc=JsonDocument.Parse(await Tool("ffprobe","-v","error","-select_streams","v:0","-show_entries","frame=best_effort_timestamp_time","-of","json",variableRate));
 var times=timesDoc.RootElement.GetProperty("frames").EnumerateArray().Where(x=>x.TryGetProperty("best_effort_timestamp_time",out _)).Select(x=>double.Parse(x.GetProperty("best_effort_timestamp_time").GetString()!,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
 var deltas=times.Zip(times.Skip(1),(a,b)=>Math.Round(b-a,4)).Distinct().ToArray();
 Check(deltas.Length>1,"VFR fixture has independently observed variable presentation intervals");
 Check(variable.Frames.Count==6&&variable.Frames.All(x=>x.Status=="Completed"),"VFR six target samples decode with separate status records");
}
await File.WriteAllTextAsync(Path.Combine(project,"evidence","media-integration-results.json"),JsonSerializer.Serialize(new{Passed=passed.Count,Tests=passed,Rows=rows},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
Console.WriteLine($"RESULT: {passed.Count} checks passed.");








