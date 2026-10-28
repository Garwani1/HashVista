# Public release boundaries

This project is prepared for an independent GPL-3.0-or-later GitHub release. Repository creation and public upload are separate actions. The product name is HashVista — Hash & Media Scanner; do not advertise it as an official continuation of another product.

## What to publish

Publish the generated public repository, the matching self-contained Windows x64 **core** portable package, checksums, release notes, and the corresponding source for that exact build. Keep `COPYING`, source attribution, and applicable dependency notices with the release. Link the exact source tag or source archive next to each executable download.

Run from the development source directory after creating a fresh core publish output:

```powershell
dotnet restore .\HashVista.csproj --locked-mode -p:IncludeMediaTools=false
dotnet publish .\HashVista.csproj -c Release -r win-x64 --self-contained true --no-restore -p:IncludeMediaTools=false -o .\artifacts\core
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare-public.ps1 -DestinationRoot ..\public-release -CorePublishDirectory .\artifacts\core
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-public.ps1 -StagingDirectory ..\public-release -OutputDirectory ..\public-packages
```

The destination must not already exist. The script creates `repository/`, optionally `core-portable/`, and a content-hash manifest. It does not upload, create a remote repository, delete files, or overwrite an earlier release. Run the staged tests and launch the staged EXE before publishing. This staging operation verifies the allowed contents and copied file hashes; it is not a substitute for runtime testing.

The package script includes hidden repository files such as `.github/` and `.gitignore`, creates source/core ZIPs without overwriting existing files, and verifies every decompressed entry's size and SHA-256 against the staged source. It emits versioned package verification and SHA256SUMS files. No absolute local paths are written into these manifests.

The public source allowlist contains application source, project/lock files, licenses, publication documents, the setup script, controlled core/regression/export/media harness source, and the Performance130 source/runner. Twelve reviewed synthetic media fixtures are included with a digest/provenance manifest; no personal media or upstream camera/HEIC examples are copied. The media suite also generates its basic fixtures locally and requires optional tools; it does not run in the core-only CI job. Optional upstream examples are skipped when absent, as documented in tests/fixtures/README.md. Performance reproduction requires the caller to supply both exact comparison assemblies; neither the older baseline binary nor timing-run data is included. Sanitized BUILD.md and VERIFICATION.md are included; the build and verification reports must identify the current HashVista version. It excludes all `evidence/`, original archives, saved scans, actual scan outputs, machine-specific diagnostics, build caches, and optional tool binaries. The private development workspace and the older full local package are not the public upload source.

## Optional media tools

The public core package excludes the entire `dependencies/` directory. File inventory, ordinary hashes, SQLite saved scans, and CSV export do not require media executables. Media metadata, fingerprints, thumbnails, and stream hashes require the appropriate external tools. The application must report an unavailable tool explicitly rather than pretending the media stage succeeded.

The versions used in local media validation are FFmpeg/ffprobe 9.0.2 essentials (Gyan build) and ExifTool 13.59. Download only from the publishers' links: [FFmpeg download page](https://ffmpeg.org/download.html), [Gyan builds](https://www.gyan.dev/ffmpeg/builds/), and [ExifTool](https://exiftool.org/). For a portable application folder, place `ffmpeg.exe`, `ffprobe.exe`, and the renamed `exiftool.exe` in its `dependencies/` subfolder. Place the complete ExifTool `exiftool_files/` support folder beside `exiftool.exe`; retain the tool packages' licenses. Use Tools & profiles to inspect availability. A different tool build can change supported formats and must be validated separately.

For developers, `scripts/dependencies.ps1` reacquires the pinned versions for local use and verifies the recorded archive hashes. It downloads tools and does not turn them into approved public redistribution assets. The source project records the pins in `dependencies/DEPENDENCIES.json`.

## Why the full local media package is separate

The locally available FFmpeg/ffprobe binaries are static GPL builds. The retained FFmpeg core snapshot does not establish that every linked external library and required build script has matching corresponding source. Until that material is collected and verified, do not redistribute those binaries in this project's public assets. This is a concrete gap in the current package, not a claim that FFmpeg cannot be redistributed.

The GPL defines corresponding source to include the material needed to generate, install, and modify the covered work, including relevant build scripts. GPL section 6 describes source access alongside executable distribution. See [the GPL text](https://www.gnu.org/licenses/gpl-3.0.html) and [FFmpeg's licensing page](https://ffmpeg.org/legal.html). The latter includes an LGPL linking checklist; its generic advice is not a substitute for auditing this particular GPL static build.

For the application itself, keep GPL-3.0-or-later because it adapts the supplied OpenHashTab work. The upstream baseline is OpenHashTab 3.1.1, commit `f13f3a4491c303a8d2c5644bde0231dd9a7a6046`, [upstream repository](https://github.com/namazso/OpenHashTab). The original supplied baseline remains in the private workspace. The public repository contains the complete adapted C# application source; the unlinked native shell-extension source is not a build dependency.

## Release evidence

Keep a brief, sanitized validation summary for the exact release: OS and runtime, build result, test counts, GUI steps performed, and limitations. Exclude personal paths and disk serials. GitHub workflow files are provided for future hosted checks; do not claim they ran until GitHub reports a completed run. Do not reuse older CSV-export results as proof of a new application's successful execution.
