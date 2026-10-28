# HashVista controlled synthetic media fixtures

The 12 files in `media/` are generated test material, not user photographs, recordings, or archive samples. `SYNTHETIC_PROVENANCE.json` records their relative paths, lengths, and SHA-256 values, checked against the existing development provenance on 28 October 2026. No absolute workstation paths are included.

The original generation record names FFmpeg 9.0.2 testsrc2 and sine sources. ExifTool wrote fabricated metadata into the orientation fixture. This manifest identifies the exact checked bytes; it does not claim a byte-for-byte reproducible encoder build across tool versions.

- `ألوان, sample.{png,jpg,gif,bmp,tiff,webp}`: a 96×64 generated color test pattern, one frame in each image format.
- `orientation6-exif.jpg`: generated JPEG with orientation 6 and fabricated camera/lens, capture time/offset, GPS and description tags. Those values are test inputs, not personal data.
- `transparent.png`: 32×16 RGBA with zero alpha, for exact black-compositing verification.
- `animated.gif`: three 96×64 frames at 2 fps, for first-frame selection checks.
- `فيديو, two-audio.mp4`: 160×96 H.264 test pattern, two seconds at 10 fps, with two separate AAC sine tracks (440 Hz/44100 Hz and 880 Hz/48000 Hz). Regression122 and MediaIntegration use the real three-stream file.
- `very-short.mp4`: a one-frame short clip.
- `variable-frame-rate.mp4`: generated frames with irregular presentation timestamps, which the media test verifies independently with ffprobe.

These fixtures are development inputs, not application runtime dependencies. The test harnesses generate additional temporary files under their own test/evidence directories. They never need a user's archive drive.

The public fixture set intentionally omits upstream camera and HEIC samples. MediaIntegration optionally tests those when exact checked files are supplied separately at `evidence/media-fixtures/ExifTool-Nikon.nef` and `evidence/media-fixtures/libheif-example.heic`; their sources and hashes are documented in `MEDIA_PROFILES.md` at the repository root. Keep their upstream notices when obtaining them. Without those two samples the optional checks are skipped, so report the actual test count rather than assuming the full local run's count.

Regression122 and MediaIntegration need working local FFmpeg, ffprobe, and ExifTool. The public core executable does not bundle those tools. See the repository's `BUILD.md` for direct build commands, test invocation, and the distinction between public core and local media-enabled use.
