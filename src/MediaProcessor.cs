// Copyright 2026 HashVista contributors. GPL-3.0-or-later.
// Uses external ExifTool/FFmpeg read-only subprocesses. See MEDIA_PROFILES.md.
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace HashVista;

public sealed class MediaProcessor
{
    public const string PixelProfile = "HashVista-RGB256-black-first-oriented-v1";
    public const string FingerprintProfile = "HashVista-pHash64-DCT32-RGB256-v1";
    public const string SamplingProfile = "HashVista-six-10-26-42-58-74-90-v1";
    private readonly string dependencies;
    private string? versions;
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".nef", ".nrw", ".cr2", ".cr3", ".arw", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw", ".raw" };
    private static readonly double[] SamplePositions = [ .10, .26, .42, .58, .74, .90 ];
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly double[,] Cosines = BuildCosines();
    private static double[,] BuildCosines() { var c = new double[8,32]; for (int u=0;u<8;u++) for(int x=0;x<32;x++) c[u,x]=Math.Cos((2*x+1)*u*Math.PI/64); return c; }

    public MediaProcessor(string? dependencyDirectory = null) => dependencies = dependencyDirectory ?? Path.Combine(AppContext.BaseDirectory, "dependencies");
    private string Tool(string name) => Path.Combine(dependencies, name + ".exe");
    public string DependencyStatus() => string.Join(" | ", new[] { "ffmpeg", "ffprobe", "exiftool" }.Select(n => n + ": " + (File.Exists(Tool(n)) ? "Ready" : "Missing — " + Tool(n))));
    public async Task<string> GetVersionsAsync(CancellationToken token)
    {
        if (versions != null) return versions;
        var lines = new List<string>();
        foreach (string name in new[] { "ffmpeg", "ffprobe", "exiftool" })
        {
            try { var r = await RunAsync(name, [name == "exiftool" ? "-ver" : "-version"], token, 20, 1024 * 1024); lines.Add(name + ": " + r.Text.Split('\n')[0].Trim()); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lines.Add(name + ": Unavailable (" + ex.Message + ")"); }
        }
        return versions = string.Join("; ", lines);
    }

    public async Task ProcessAsync(FileResult row, ScanOptions options, CancellationToken token, Action<string>? report = null)
    {
        if (row.Kind is not ("Image" or "Video")) return;
        if (!options.Metadata && !options.Fingerprints && !(options.StreamHashes && row.Kind == "Video")) return;
        report?.Invoke("Checking media tools: " + row.FullPath);
        row.Media["ToolVersions"] = await GetVersionsAsync(token);
        if (row.Kind == "Image") await ProcessImageAsync(row, options, token, report);
        else await ProcessVideoAsync(row, options, token, report);
    }

    private async Task<JsonElement> ReadExifAsync(string path, CancellationToken token)
    {
        string[] arguments = ["-charset", "filename=UTF8", "-j", "-n", "-FileType", "-MIMEType", "-ImageWidth", "-ImageHeight", "-ExifImageWidth", "-ExifImageHeight", "-Orientation", "-DateTimeOriginal", "-SubSecTimeOriginal", "-OffsetTimeOriginal", "-CreateDate", "-Make", "-Model", "-LensModel", "-Lens", "-ImageDescription", "-UserComment", "-Comment", "-GPSLatitude", "-GPSLongitude", "-Error", "-Warning", path];
        var r = await RunAsync("exiftool", arguments, token, 45, 4 * 1024 * 1024);
        using var doc = JsonDocument.Parse(r.Bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) throw new InvalidDataException("ExifTool returned no metadata record.");
        var exif = doc.RootElement[0].Clone();
        if (exif.TryGetProperty("Error", out var error)) throw new InvalidDataException(error.ToString());
        return exif;
    }

    private async Task ProcessImageAsync(FileResult row, ScanOptions options, CancellationToken token, Action<string>? report)
    {
        JsonElement? exif = null;
        report?.Invoke("Reading image metadata/orientation: " + row.FullPath);
        if (options.Metadata) row.MetadataStatus = "Processing";
        try
        {
            // Orientation is also needed for a reproducible fingerprint even when metadata export was not requested.
            exif = await ReadExifAsync(row.FullPath, token);
            if (!(Text(exif.Value, "MIMEType")?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)) { exif = null; throw new InvalidDataException("File contents were not recognized as an image by ExifTool."); }
            if (options.Metadata)
            {
                Copy(exif.Value, row.Media, "FileType", "Format");
                Copy(exif.Value, row.Media, "ImageWidth", "Width");
                Copy(exif.Value, row.Media, "ImageHeight", "Height");
                Copy(exif.Value, row.Media, "Orientation", "Orientation");
                Copy(exif.Value, row.Media, "DateTimeOriginal", "CaptureTime");
                Copy(exif.Value, row.Media, "SubSecTimeOriginal", "CaptureSubseconds");
                Copy(exif.Value, row.Media, "OffsetTimeOriginal", "CaptureTimeOffset");
                Copy(exif.Value, row.Media, "CreateDate", "ImageCreateDateText");
                Copy(exif.Value, row.Media, "Make", "CameraMake");
                Copy(exif.Value, row.Media, "Model", "CameraModel");
                CopyFirst(exif.Value, row.Media, "Lens", "LensModel", "Lens");
                CopyFirst(exif.Value, row.Media, "Description", "ImageDescription", "UserComment", "Comment");
                Copy(exif.Value, row.Media, "GPSLatitude", "GPSLatitude");
                Copy(exif.Value, row.Media, "GPSLongitude", "GPSLongitude");
                row.Media["ExifJson"] = exif.Value.GetRawText();
                AddAspectRatio(row.Media);
                row.MetadataStatus = "Completed";
            }
        }
        catch (OperationCanceledException) { if (options.Metadata) row.MetadataStatus = "Cancelled"; throw; }
        catch (Exception ex) { if (options.Metadata) { row.MetadataStatus = Status(ex); AddError(row, "Metadata", ex); } }
        if (!options.Fingerprints) return;
        report?.Invoke("Computing image fingerprint: " + row.FullPath);
        row.FingerprintStatus = "Processing";
        try
        {
            if (exif == null) throw new NotSupportedException("Image orientation could not be read; normalized fingerprint was not produced.");
            int orientation = ReadOrientation(exif.Value);
            using var source = await ImageSourceAsync(row.FullPath, token);
            row.Media["PixelSource"] = source.Embedded ? "EmbeddedPreview" : "DecodedFirstFrame";
            row.Media["OrientationPolicy"] = "EXIF 1–8 applied explicitly; absent = top-left; embedded preview uses parent orientation";
            if (source.Embedded) row.Media["EmbeddedPreviewTag"] = source.Tag;
            var rgba = await DecodeRgbaAsync(source.Path, null, null, OrientationFilter(orientation), token);
            var rgb = CompositeBlack(rgba);
            row.Media["ImagePixelSHA256"] = Convert.ToHexString(SHA256.HashData(rgb));
            row.Media["PixelProfile"] = PixelProfile;
            row.Media["PerceptualHash"] = ComputePerceptualHash(rgb);
            row.Media["PerceptualAlgorithm"] = "pHash (DCT32, 8x8 low frequencies, median excluding DC)";
            row.Media["PerceptualBits"] = "64";
            row.Media["FingerprintProfile"] = FingerprintProfile;
            row.FingerprintStatus = "Completed";
        }
        catch (OperationCanceledException) { row.FingerprintStatus = "Cancelled"; throw; }
        catch (Exception ex) { row.FingerprintStatus = Status(ex); AddError(row, "Fingerprint", ex); }
    }

    private async Task<JsonElement> ProbeAsync(string path, CancellationToken token)
    {
        var r = await RunAsync("ffprobe", ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_format", "-show_streams", "-print_format", "json", path], token, 45, 16 * 1024 * 1024);
        using var doc = JsonDocument.Parse(r.Bytes);
        return doc.RootElement.Clone();
    }

    private async Task ProcessVideoAsync(FileResult row, ScanOptions options, CancellationToken token, Action<string>? report)
    {
        JsonElement? probe = null;
        report?.Invoke("Reading video metadata: " + row.FullPath);
        JsonElement? primary = null;
        double? duration = null;
        if (options.Metadata) row.MetadataStatus = "Processing";
        try
        {
            probe = await ProbeAsync(row.FullPath, token);
            if (probe.Value.TryGetProperty("streams", out var streams))
            {
                row.Streams = streams.EnumerateArray().Select(x => x.Clone()).ToList();
                primary = row.Streams.Where(IsVideo).Where(s => !AttachedPicture(s)).Select(s => (JsonElement?)s).FirstOrDefault();
            }
            if (probe.Value.TryGetProperty("format", out var format))
            {
                duration = Number(format, "duration");
                Copy(format, row.Media, "format_name", "Container");
                Copy(format, row.Media, "bit_rate", "BitRate");
                row.Media["FormatJson"] = format.GetRawText();
                if (format.TryGetProperty("tags", out var tags)) { Copy(tags, row.Media, "title", "Title"); Copy(tags, row.Media, "creation_time", "CreationTime"); }
            }
            if (primary.HasValue)
            {
                duration ??= Number(primary.Value, "duration");
                Copy(primary.Value, row.Media, "width", "Width"); Copy(primary.Value, row.Media, "height", "Height");
                Copy(primary.Value, row.Media, "codec_name", "VideoCodec");
                Copy(primary.Value, row.Media, "r_frame_rate", "FrameRate");
                Copy(primary.Value, row.Media, "avg_frame_rate", "AverageFrameRate");
                row.Media["FrameRateNote"] = "ffprobe rational r_frame_rate and avg_frame_rate; neither asserts constant frame rate";
                row.Media["PrimaryVideoStreamIndex"] = Int(primary.Value, "index")?.ToString(Invariant) ?? "";
                AddAspectRatio(row.Media);
            }
            var audio = row.Streams.Where(s => Text(s, "codec_type") == "audio").Select(s => (JsonElement?)s).FirstOrDefault();
            if (audio.HasValue) { Copy(audio.Value, row.Media, "codec_name", "AudioCodec"); Copy(audio.Value, row.Media, "sample_rate", "AudioSampleRate"); Copy(audio.Value, row.Media, "channels", "AudioChannels"); }
            if (duration.HasValue && double.IsFinite(duration.Value) && duration.Value >= 0) row.Media["DurationSeconds"] = duration.Value.ToString("R", Invariant);
            else duration = null;
            if (options.Metadata) row.MetadataStatus = "Completed";
        }
        catch (OperationCanceledException) { if (options.Metadata) row.MetadataStatus = "Cancelled"; throw; }
        catch (Exception ex) { if (options.Metadata) { row.MetadataStatus = Status(ex); AddError(row, "Metadata", ex); } }
        if (options.Fingerprints)
        {
            row.FingerprintStatus = "Processing";
            row.Media["SamplingProfile"] = SamplingProfile;
            row.Media["PerceptualAlgorithm"] = "pHash (DCT32, 8x8 low frequencies, median excluding DC)";
            row.Media["PerceptualBits"] = "64";
            row.Media["FingerprintProfile"] = FingerprintProfile;
            row.Media["FrameTimestampNote"] = "Target seek times recorded; actual presentation timestamps unavailable and left null";
            try
            {
                if (!primary.HasValue) throw new NotSupportedException("No decodable primary video stream found.");
                if (!duration.HasValue || duration.Value <= 0) throw new NotSupportedException("Known positive duration is required for the six-position sampling profile.");
                row.Frames.Clear();
                int index = Int(primary.Value, "index") ?? 0;
                foreach (double fraction in SamplePositions)
                {
                    var sample = new FrameSample { TargetSeconds = duration.Value * fraction, Profile = FingerprintProfile };
                    row.Frames.Add(sample);
                    try
                    {
                        report?.Invoke($"Video fingerprint frame {row.Frames.Count}/6: {row.FullPath}");
                        var rgba = await DecodeRgbaAsync(row.FullPath, sample.TargetSeconds, index, "", token);
                        sample.Fingerprint = ComputePerceptualHash(CompositeBlack(rgba));
                        sample.Status = "Completed";
                    }
                    catch (OperationCanceledException) { sample.Status = "Cancelled"; throw; }
                    catch (Exception ex) { sample.Status = Status(ex); sample.Error = ex.Message; }
                }
                int completed = row.Frames.Count(x => x.Status == "Completed");
                row.FingerprintStatus = completed == 6 ? "Completed" : completed == 0 ? "Failed" : "Partial";
                if (completed != 6) row.Errors.Add(new StageError { Stage = "Fingerprint", Code = "FrameDecode", Message = $"Decoded {completed}/6 samples; individual sample errors are in FramesJson." });
            }
            catch (OperationCanceledException) { row.FingerprintStatus = "Cancelled"; throw; }
            catch (Exception ex) { row.FingerprintStatus = Status(ex); AddError(row, "Fingerprint", ex); }
        }
        if (options.StreamHashes)
        {
            report?.Invoke("Hashing encoded audio/video streams: " + row.FullPath);
            row.StreamHashStatus = "Processing";
            try
            {
                if (probe == null) throw new NotSupportedException("Stream information is unavailable.");
                var indices = row.Streams.Where(s => Text(s, "codec_type") is "video" or "audio").Select(s => Int(s, "index")).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                if (indices.Length == 0) throw new NotSupportedException("No audio/video streams to hash.");
                var args = new List<string> { "-v", "error", "-nostdin", "-threads", "1", "-protocol_whitelist", "file,pipe", "-i", row.FullPath };
                foreach (int index in indices) { args.Add("-map"); args.Add("0:" + index.ToString(Invariant)); }
                args.AddRange(["-c", "copy", "-f", "streamhash", "-hash", "sha256", "pipe:1"]);
                var result = await RunAsync("ffmpeg", args, token, 1800, 1024 * 1024);
                row.StreamHashes.Clear();
                foreach (string line in result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var match = Regex.Match(line.Trim(), @"^(\d+),([va]),SHA256=([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    int outputIndex = int.Parse(match.Groups[1].Value, Invariant);
                    if (outputIndex >= indices.Length) throw new InvalidDataException("Unexpected streamhash output stream index.");
                    row.StreamHashes.Add(JsonSerializer.SerializeToElement(new { StreamIndex = indices[outputIndex], OutputIndex = outputIndex, Type = match.Groups[2].Value, Algorithm = "SHA256", Hash = match.Groups[3].Value.ToUpperInvariant(), ByteMode = "EncodedStreamCopyPacketPayloads", Profile = "HashVista-FFmpeg-streamhash-copy-sha256-v1", ToolVersion = row.Media.GetValueOrDefault("ToolVersions"), Command = "ffmpeg -v error -nostdin -threads 1 -protocol_whitelist file,pipe -i INPUT " + string.Join(" ", indices.Select(x => "-map 0:" + x)) + " -c copy -f streamhash -hash sha256 pipe:1" }));
                }
                if (row.StreamHashes.Count != indices.Length) throw new InvalidDataException($"Expected {indices.Length} stream hashes, received {row.StreamHashes.Count}.");
                row.Media["StreamHashProfile"] = "HashVista-FFmpeg-streamhash-copy-sha256-v1; encoded packet payload bytes, not decoded samples or entire container";
                row.StreamHashStatus = "Completed";
            }
            catch (OperationCanceledException) { row.StreamHashStatus = "Cancelled"; throw; }
            catch (Exception ex) { row.StreamHashStatus = Status(ex); AddError(row, "StreamHash", ex); }
        }
    }

    public async Task<byte[][]> PreviewAsync(FileResult row, CancellationToken token)
    {
        if (row.Kind == "Image")
        {
            var exif = await ReadExifAsync(row.FullPath, token);
            if (!(Text(exif, "MIMEType")?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)) throw new InvalidDataException("File contents were not recognized as an image by ExifTool.");
            using var source = await ImageSourceAsync(row.FullPath, token);
            var bytes = await DecodePreviewAsync(source.Path, null, null, OrientationFilter(ReadOrientation(exif)), token);
            row.Media["PreviewPixelSource"] = source.Embedded ? "EmbeddedPreview" : "DecodedFirstFrame";
            row.Media["PreviewProfile"] = "HashVista-preview640x480-oriented-first-v1";
            if (source.Embedded) { row.Media["PixelSource"] = "EmbeddedPreview"; row.Media["EmbeddedPreviewTag"] = source.Tag; }
            return [bytes];
        }
        if (row.Kind == "Video")
        {
            var probe = await ProbeAsync(row.FullPath, token);
            var streams = probe.TryGetProperty("streams", out var s) ? s.EnumerateArray().Select(x => x.Clone()).ToArray() : [];
            var primary = streams.Where(IsVideo).Where(x => !AttachedPicture(x)).Select(x => (JsonElement?)x).FirstOrDefault() ?? throw new NotSupportedException("No video stream for preview.");
            double? duration = probe.TryGetProperty("format", out var f) ? Number(f, "duration") : null;
            duration ??= Number(primary, "duration");
            var targets = duration is > 0 ? SamplePositions.Select(x => x * duration.Value).ToArray() : [0.0];
            var frames = new List<byte[]>();
            Exception? lastError = null;
            foreach (double target in targets)
            {
                try { frames.Add(await DecodePreviewAsync(row.FullPath, target, Int(primary, "index"), "", token)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastError = ex; }
            }
            if (frames.Count == 0) throw lastError ?? new InvalidDataException("No preview frames decoded.");
            return frames.ToArray();
        }
        return [];
    }

    private async Task<byte[]> DecodeRgbaAsync(string path, double? seconds, int? stream, string orientation, CancellationToken token)
    {
        var args = DecodeArguments(path, seconds, stream, orientation, "scale=256:256:flags=lanczos,format=rgba");
        args.AddRange(["-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1"]);
        var result = await RunAsync("ffmpeg", args, token, 90, 256 * 256 * 4 + 1);
        if (result.Bytes.Length != 256 * 256 * 4) throw new InvalidDataException($"Decoder produced {result.Bytes.Length} bytes; one 256×256 RGBA frame was required.");
        return result.Bytes;
    }
    private async Task<byte[]> DecodePreviewAsync(string path, double? seconds, int? stream, string orientation, CancellationToken token)
    {
        var args = DecodeArguments(path, seconds, stream, orientation, "scale=640:480:force_original_aspect_ratio=decrease");
        args.AddRange(["-c:v", "png", "-f", "image2pipe", "pipe:1"]);
        var result = await RunAsync("ffmpeg", args, token, 90, 8 * 1024 * 1024);
        if (result.Bytes.Length < 8) throw new InvalidDataException("Decoder returned no preview frame.");
        return result.Bytes;
    }
    private static List<string> DecodeArguments(string path, double? seconds, int? stream, string orientation, string filter)
    {
        var args = new List<string> { "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1" };
        if (seconds.HasValue) { args.Add("-ss"); args.Add(seconds.Value.ToString("0.#########", Invariant)); }
        else args.Add("-noautorotate");
        args.AddRange(["-protocol_whitelist", "file,pipe", "-i", path, "-map", "0:" + (stream.HasValue ? stream.Value.ToString(Invariant) : "v:0"), "-frames:v", "1", "-an", "-sn", "-dn", "-vf", orientation + filter, "-threads", "1"]);
        return args;
    }
    private static int ReadOrientation(JsonElement exif)
    {
        if (!exif.TryGetProperty("Orientation", out var value)) return 1;
        if (!int.TryParse(value.ToString(), NumberStyles.Integer, Invariant, out int orientation) || orientation < 1 || orientation > 8)
            throw new NotSupportedException("Unsupported supplied EXIF orientation: " + value);
        return orientation;
    }
    private static string OrientationFilter(int orientation) => orientation switch
    {
        1 => "", 2 => "hflip,", 3 => "hflip,vflip,", 4 => "vflip,", 5 => "transpose=clock,hflip,", 6 => "transpose=clock,", 7 => "transpose=clock,vflip,", 8 => "transpose=cclock,", _ => throw new NotSupportedException("EXIF orientation outside 1–8.")
    };
    private static byte[] CompositeBlack(byte[] rgba)
    {
        byte[] rgb = new byte[256 * 256 * 3];
        for (int src = 0, dst = 0; src < rgba.Length; src += 4)
            for (int c = 0; c < 3; c++) rgb[dst++] = (byte)((rgba[src + c] * rgba[src + 3] + 127) / 255);
        return rgb;
    }
    public static string ComputePerceptualHash(byte[] rgb256)
    {
        if (rgb256.Length != 256 * 256 * 3) throw new ArgumentException("Expected exactly 256×256 RGB24 pixels.");
        double[,] gray = new double[32,32];
        for (int y=0; y<256; y++) for (int x=0; x<256; x++)
        {
            int at = (y*256+x)*3;
            gray[y/8,x/8] += (299*rgb256[at]+587*rgb256[at+1]+114*rgb256[at+2]) / 64000.0;
        }
        double[] coefficients = new double[64];
        for (int v=0; v<8; v++) for (int u=0; u<8; u++)
        {
            double sum=0;
            for (int y=0; y<32; y++) for (int x=0; x<32; x++) sum += gray[y,x]*Cosines[u,x]*Cosines[v,y];
            coefficients[v*8+u] = Math.Round(sum * (u==0 ? 1/Math.Sqrt(2) : 1) * (v==0 ? 1/Math.Sqrt(2) : 1), 8, MidpointRounding.ToEven);
        }
        var ranked = coefficients.Skip(1).OrderBy(x => x).ToArray();
        double median = ranked[31];
        ulong bits=0;
        for (int i=0; i<64; i++) bits = (bits << 1) | (coefficients[i] > median ? 1UL : 0UL);
        return bits.ToString("X16", Invariant);
    }

    private async Task<ImageSource> ImageSourceAsync(string path, CancellationToken token)
    {
        if (!RawExtensions.Contains(Path.GetExtension(path))) return new ImageSource(path, false, "", null);
        foreach (string tag in new[] { "JpgFromRaw", "PreviewImage" })
        {
            var preview = await RunAsync("exiftool", ["-charset", "filename=UTF8", "-b", "-" + tag, path], token, 45, 64 * 1024 * 1024);
            if (preview.Bytes.Length < 3 || preview.Bytes[0] != 0xff || preview.Bytes[1] != 0xd8) continue;
            string cache = Path.Combine(AppContext.BaseDirectory, "Data", "cache", "media-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cache);
            string output = Path.Combine(cache, "preview.jpg");
            try { await File.WriteAllBytesAsync(output, preview.Bytes, token); return new ImageSource(output, true, tag, cache); }
            catch { try { Directory.Delete(cache, true); } catch {} throw; }
        }
        throw new NotSupportedException("No embedded JPEG (JpgFromRaw/PreviewImage) found. Full RAW sensor decoding is not implemented; ordinary file hashing remains available.");
    }
    private sealed class ImageSource(string path, bool embedded, string tag, string? cache) : IDisposable
    {
        public string Path {get;} = path; public bool Embedded {get;} = embedded; public string Tag {get;} = tag;
        public void Dispose() { if (cache != null) { try { Directory.Delete(cache, true); } catch {} } }
    }

    private sealed record ToolResult(byte[] Bytes, string Error) { public string Text => Encoding.UTF8.GetString(Bytes); }
    private async Task<ToolResult> RunAsync(string name, IEnumerable<string> args, CancellationToken token, int timeoutSeconds, int maxOutput)
    {
        token.ThrowIfCancellationRequested();
        string executable = Tool(name);
        if (!File.Exists(executable)) throw new FileNotFoundException($"Missing media dependency: {executable}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = dependencies } };
        process.StartInfo.Environment["LC_ALL"] = "C";
        process.StartInfo.Environment["LC_CTYPE"] = "C";
        process.StartInfo.Environment["LANG"] = "C";
        var actualArguments = args.ToArray();
        if (name == "exiftool")
        {
            // Windows Perl argv may lose non-ANSI filenames; ExifTool supports UTF-8 argument files on stdin.
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.StandardInputEncoding = new UTF8Encoding(false);
            foreach (string a in new[] { "-charset", "filename=UTF8", "-@", "-" }) process.StartInfo.ArgumentList.Add(a);
        }
        else foreach (var argument in actualArguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Could not start " + name + ".");
        using var registration = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch {} });
        var outputTask = DrainAsync(process.StandardOutput.BaseStream, maxOutput, true);
        var errorTask = DrainAsync(process.StandardError.BaseStream, 256 * 1024, false);
        try
        {
            if (name == "exiftool")
            {
                await process.StandardInput.WriteAsync(string.Join("\n", actualArguments) + "\n");
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(outputTask, errorTask);
            token.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException($"{name} exceeded its {timeoutSeconds}-second timeout.");
            var stderr = Encoding.UTF8.GetString(await errorTask);
            if (process.ExitCode != 0) throw new InvalidDataException($"{name} exit {process.ExitCode}: {TrimError(stderr)}");
            return new ToolResult(await outputTask, stderr);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch {}
            try { await process.WaitForExitAsync(); await Task.WhenAll(outputTask, errorTask); } catch {}
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (timeout.IsCancellationRequested) throw new TimeoutException($"{name} exceeded its {timeoutSeconds}-second timeout.");
            throw;
        }
    }
    private static async Task<byte[]> DrainAsync(Stream stream, int limit, bool rejectOverflow)
    {
        using var memory = new MemoryStream(Math.Min(limit, 64*1024));
        byte[] buffer = new byte[32768];
        bool overflow = false;
        while (true)
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            int keep = (int)Math.Min(read, limit - memory.Length);
            if (keep > 0) memory.Write(buffer, 0, keep);
            if (keep < read) overflow = true;
        }
        if (overflow && rejectOverflow) throw new InvalidDataException("Media tool output exceeded the bounded output limit.");
        return memory.ToArray();
    }
    private static string TrimError(string error) => error.Length > 4000 ? error[..4000] + " [truncated]" : error.Trim();
    private static string Status(Exception ex) => ex is NotSupportedException or FileNotFoundException ? "Unsupported" : "Failed";
    private static void AddError(FileResult row, string stage, Exception ex) => row.Errors.Add(new StageError { Stage = stage, Code = ex is FileNotFoundException ? "MissingDependency" : ex.GetType().Name, Message = ex.Message });
    private static string? Text(JsonElement json, string property) => json.TryGetProperty(property, out var item) && item.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? item.ToString() : null;
    private static int? Int(JsonElement json, string property) => int.TryParse(Text(json, property), NumberStyles.Integer, Invariant, out int n) ? n : null;
    private static double? Number(JsonElement json, string property) => double.TryParse(Text(json, property), NumberStyles.Float, Invariant, out double n) && double.IsFinite(n) ? n : null;
    private static bool IsVideo(JsonElement stream) => Text(stream, "codec_type") == "video";
    private static bool AttachedPicture(JsonElement stream) => stream.TryGetProperty("disposition", out var d) && Int(d, "attached_pic") == 1;
    private static void Copy(JsonElement source, Dictionary<string,string> target, string from, string to) { string? text = Text(source, from); if (!string.IsNullOrEmpty(text)) target[to] = text; }
    private static void CopyFirst(JsonElement source, Dictionary<string,string> target, string to, params string[] from) { foreach (string key in from) { string? text = Text(source, key); if (!string.IsNullOrEmpty(text)) { target[to] = text; return; } } }
    private static void AddAspectRatio(Dictionary<string,string> media) { if (double.TryParse(media.GetValueOrDefault("Width"), Invariant, out double w) && double.TryParse(media.GetValueOrDefault("Height"), Invariant, out double h) && h>0) media["AspectRatio"] = (w/h).ToString("R", Invariant); }
}







