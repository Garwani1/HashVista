# Media profiles — HashVista 1.3.0

The application produces independent file records. It never compares images, videos, hashes, or fingerprints. The following profiles are application-defined; they do not reproduce another product's proprietary algorithm. Profile identifiers use the `HashVista-*` prefix.

## Tools and process boundary

The tested Windows x64 tools are FFmpeg/ffprobe `9.0.2-essentials_build-www.gyan.dev` and ExifTool `13.59`. Their version output is stored in every processed media record as `ToolVersions`. Exact downloads, licenses, and source information are in `dependencies/DEPENDENCIES.json`.

FFmpeg and ffprobe run with a `file,pipe` protocol whitelist, so HTTP/TCP media URLs are not fetched. All arguments are passed using `ProcessStartInfo.ArgumentList`, without a shell. ExifTool's Windows Perl argv conversion lost Arabic filenames in an observed test; the application therefore supplies its arguments using a UTF-8 stdin argument file (`-charset filename=UTF8 -@ -`). ExifTool is only invoked with read/extract options in application code. Test-fixture creation deliberately writes metadata only into disposable test files.

Each call drains stdout and stderr concurrently. Stdout limits: metadata 4 MiB, ffprobe JSON 16 MiB, embedded RAW JPEG 64 MiB, normalized frame exactly 262,144 bytes, preview PNG 8 MiB, streamhash output 1 MiB. Stderr is drained but retained only up to 256 KiB, with at most 4,000 characters in an error. Timeouts are 45 seconds for metadata/probes, 90 seconds per decoded frame, and 1,800 seconds for a requested stream-hash operation. Cancellation and timeout kill the child process tree. CPU decoding uses one FFmpeg worker/filter thread. Output limits do not promise a fixed upper bound on the native decoder's internal allocations.

Extracted RAW JPEGs are stored only in the application's own `Data/cache/media-<random GUID>` directory and removed after the operation. Source files are never rewritten. A preview failure does not change successful full-file hashes.

## Image metadata

ExifTool supplies the detected file type, stored image width/height, available EXIF orientation, capture-time text, capture subseconds and explicit offset, make/model, lens, description/comment, and numeric GPS coordinates. The raw selected-tag JSON is also retained. The capture time remains text exactly as supplied by ExifTool; an absent EXIF offset stays absent and is never interpreted as UTC. A file whose contents are recognized as text is not reported as successful image metadata merely because its filename ends with `.heic` or `.jpg`.

`Width` and `Height` describe the stored image metadata, before orientation. `AspectRatio` is width/height. Unknown properties remain absent. HEIC/HEIF actual container type is retained as reported: the tested `.heic` fixture reports `HEIF`.

## Normalized pixels: HashVista-RGB256-black-first-oriented-v1

1. Use the first decoded video/image stream and its first frame/page. Animated GIF/WebP and multipage TIFF use only that frame/page; later frames are not merged.
2. Disable FFmpeg automatic image rotation. Apply EXIF orientation 1–8 explicitly using transpose/flip operations. If the tag is absent, apply no transform; an invalid supplied value fails explicitly. This profile handles EXIF orientation, not every possible HEIF container mirror/rotation transform. Original metadata dimensions remain unchanged.
3. For supported camera RAW extensions, attempt ExifTool `JpgFromRaw`, then `PreviewImage`. Use the first non-empty JPEG. Store `PixelSource=EmbeddedPreview` and the chosen `EmbeddedPreviewTag`; apply the parent RAW EXIF orientation. The RAW sensor raster is not decoded. If neither embedded JPEG exists, fingerprint status is `Unsupported` with an explanation.
4. FFmpeg scales the chosen oriented frame to exactly 256×256 with Lanczos. Aspect ratio is intentionally normalized to a square for this profile.
5. Decode to 8-bit RGBA. Composite RGB onto black: `(channel * alpha + 127) / 255`, integer division. Serialize exactly 196,608 bytes in top-to-bottom row order, left-to-right pixels, interleaved R,G,B, no header or padding.
6. SHA-256 of those bytes becomes `ImagePixelSHA256`. It is a hash of this normalized decoded representation, not of the original file.

Color conversion follows the bundled FFmpeg decoder/scaler's color metadata/defaults. ICC-managed conversion to a calibrated sRGB space, HDR tone mapping, and full camera color science are not implemented. Changing decoder version may change normalized output; the decoder version is retained for this reason.

## Perceptual fingerprint: HashVista-pHash64-DCT32-RGB256-v1

The application's small reference DCT implementation consumes the normalized RGB256 representation above. It averages each 8×8 block into a 32×32 grayscale value using `(299*R + 587*G + 114*B)/1000`. It computes the orthonormal-factor low 8×8 DCT coefficients (common overall scaling omitted). Coefficients are rounded to eight decimal places, ties to even, to stabilize numerical noise. The threshold is the median of the 63 non-DC coefficients. All 64 coefficients, including DC, become one bit each using a strict `value > median` comparison; bits are row-major with the first coefficient as the most significant bit. The result is 16 uppercase hexadecimal characters (`PerceptualBits=64`). There is no distance calculation or comparison workflow.

## Video metadata and six sample frames

ffprobe's full format JSON and every stream JSON are retained. Summary values include container, duration when known, first non-attached-picture video resolution/codec, first audio codec/sample rate/channels, bitrate, and available title/creation tags. Rational `r_frame_rate` and `avg_frame_rate` strings are preserved separately; neither is asserted to be a constant frame rate. Stream JSON retains supplied frame counts and tags without inventing missing values.

`HashVista-six-10-26-42-58-74-90-v1` requests frames at 10%, 26%, 42%, 58%, 74%, and 90% of a known positive duration. Each request seeks with FFmpeg, selects the primary video stream, and decodes one frame. Video automatic display rotation is left enabled. The decoded frame is normalized to RGB256 and given the same pHash64 profile above. This is sampled media data, not a cryptographic hash of the whole video.

Each sample stores `TargetSeconds`, fingerprint, profile, status, and any error. `ActualSeconds` is null: the chosen invocation does not observe the original presentation timestamp. Very short videos may produce repeated or unavailable samples, all retained with explicit status. Unknown/zero duration yields an explicit unsupported fingerprint stage, not invented sampling timestamps.

## Optional encoded stream hashing

The operation is off by default. It maps all discovered audio/video stream indices, preserving their mapping, and invokes:

```text
ffmpeg -v error -nostdin -threads 1 -protocol_whitelist file,pipe -i INPUT -map 0:INDEX ... -c copy -f streamhash -hash sha256 pipe:1
```

`HashVista-FFmpeg-streamhash-copy-sha256-v1` hashes encoded stream-copy packet payload bytes. It excludes container structure/timestamps and is neither the full-file hash nor a decoded-samples hash. Each JSON record retains the original input stream index, mapped output index, stream kind, algorithm, hash, profile, tool version, and command shape. Streams that cannot be copied into this muxer produce an explicit stage error.

[FFmpeg's streamhash documentation](https://ffmpeg.org/ffmpeg-formats.html#streamhash) explains its decoded rawvideo/PCM defaults. [The streamcopy documentation](https://ffmpeg.org/ffmpeg.html#Streamcopy) specifies that `-c copy` avoids decoding/re-encoding. Local `ffmpeg -h muxer=streamhash` confirmed the default codecs. The test suite independently ran both encoded-copy and decoded-default invocations: the application's hashes matched the former and differed from the latter.

## Lazy previews

Selecting a preview decodes one image or up to six video frames into PNGs bounded by 640×480, maintaining aspect ratio. Previews are generated only on request and not held for the whole inventory. RAW preview provenance is recorded as `PreviewPixelSource=EmbeddedPreview`, with the extraction tag, when the selected result is saved. Video frames are shown in sampling order; unavailable frames may be omitted from the visual contact sheet while per-sample scan errors remain in exported data.

## Measured capabilities and remaining boundaries

The observations below were established with controlled Windows media fixtures and the stated tool versions during development. The full historical media run included optional upstream NEF/HEIC fixtures; those files are not part of the public source package. `VERIFICATION.md` records the checks actually rerun for the current release and its exact DLL. Do not assume the historical count of 72 applies to a run without those optional fixtures. Public source includes the reviewed synthetic fixtures and their digest manifest; raw local evidence is excluded.

- JPEG, PNG, GIF, BMP, TIFF, WebP: real generated images passed metadata, dimensions, normalized pixel SHA256, pHash64, repeatability, and lazy preview.
- EXIF capture time/offset, camera, lens, description and GPS: generated JPEG tags were read correctly. EXIF orientation 6 produced the exact normalized pixel SHA256 of an independent FFmpeg rotated RGB24 reference (the reference explicitly uses the profile's RGBA intermediate); unrotated pixels differed. A fully transparent PNG matched the expected SHA256 of exactly 196,608 zero bytes. Animated GIF output matched the first decoded frame and differed from the second.
- HEIF/HEIC: a real libheif HEVC image passed metadata, fingerprinting, and preview. This does not establish support for every HEIF item arrangement, HDR variant, transformation, or camera encoding.
- NEF: ExifTool's compact Nikon test file passed metadata. Its absent JPEG generated a clear `Unsupported` fingerprint outcome. A copy of that container with a generated JPEG embedded by ExifTool passed extraction, fingerprinting, and preview with `PixelSource=EmbeddedPreview`. This is an integration fixture, not a test of a full camera sensor image. Other RAW extensions use the same embedded-JPEG policy but were not individually verified with real camera files.
- MP4: a generated two-second MPEG-4/AAC video passed two-stream metadata, six sampled fingerprints, repeatability, six lazy previews, and stream-copy hashes. A separate video with two audio streams retained all three stream records/indices and matched three independent stream-copy hashes. A variable-frame-rate fixture had independently measured variable frame intervals; both rational rate fields matched ffprobe and its six samples decoded. A one-frame short clip retained explicit six-sample outcomes.
- Invalid media preserved a precomputed full-file SHA256 and reported unsuccessful media stages. Disabled media options performed no decode. Pre-cancelled previews propagated cancellation.

This harness tests the production media engine, not the packaged GUI. The parent project's verification report separately describes GUI integration and other tests. It does not benchmark a 10 TB archive.

## Fixture provenance

Generated `testsrc` images and sine-wave videos are disposable test material created by the harness. The two downloaded small fixtures were obtained from the upstream project repositories and are not runtime dependencies:

- ExifTool Nikon metadata fixture: https://raw.githubusercontent.com/exiftool/exiftool/master/t/images/Nikon.nef — SHA256 `6FAE30A2809B52ECE100316F33A3FADE4B65CF5690AF2D62BB00F44AE052BDD6` (ExifTool source licensing).
- libheif example HEIC: https://raw.githubusercontent.com/strukturag/libheif/master/examples/example.heic — SHA256 `7F8B363E4936C0666A25F64F3A92FDA10BD8E5453BE4592530B65A55DD98F3F2` (upstream example fixture).

The NEF preview test modifies only a disposable copy of the Nikon metadata fixture with a generated JPEG. The untouched downloaded fixture remains available for provenance checking. Upstream downloaded example images need not be included in distributable runtime/source packages; the test harness can be supplied their paths after downloading the exact checked files.

