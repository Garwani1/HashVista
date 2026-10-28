# Third-party notices — HashVista 1.3.0

HashVista is an independent GPL-3.0-or-later application. It is not an official OpenHashTab release.

## Distribution scope

The public Windows core contains the application, self-contained .NET/WPF runtime, SQLite packages, and their notices. It excludes FFmpeg, ffprobe, ExifTool, and `exiftool_files/`. Optional media executables can be acquired separately from their publishers for local use. The separate local full package is not the public redistribution artifact.

The public source preserves all application source/configuration needed to build HashVista and the applicable notices. Publish that matching source with the binary, and keep the source accessible as required by the chosen distribution method. The private development workspace and historical delivery archives also contain local evidence and must not be uploaded wholesale. [GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0.html).

## OpenHashTab / CSV1 adaptations

OpenHashTab 3.1.1, upstream commit `f13f3a4491c303a8d2c5644bde0231dd9a7a6046`, copyright 2019–2025 namazso <admin@namazso.eu>, GPL-3.0-or-later. Original project: [namazso/OpenHashTab](https://github.com/namazso/OpenHashTab). The supplied OpenHashTab-3.1.1-CSV1 baseline remains unchanged in the local workspace.

The C# application ports CSV1's quote-every-field escaping, BOM/CRLF output, full-path retention, known-zero versus unknown-size behavior, retained failure rows, and export-from-computed-results semantics. It follows the bounded streamed multi-algorithm pattern. Adaptation notices remain in `src/CsvExporter.cs` and `src/ScanEngine.cs`. The native shell extension, algorithm DLLs, COM/ATL, online verification, and comparison controls are not linked. The separate native baseline is not required to build this C# project.

Retained license: `licenses/OpenHashTab-GPL-3.0.txt`; application license: `COPYING`.

## .NET and WPF

The verified self-contained build uses `Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.13. Copyright Microsoft and .NET contributors; MIT with additional third-party notices. Source commit declared by package metadata: `4099f83977f4d29604a9aeb971fb0a7bc1d5f699`, [dotnet/dotnet](https://github.com/dotnet/dotnet/tree/4099f83977f4d29604a9aeb971fb0a7bc1d5f699).

Retain `licenses/dotnet-runtime-LICENSE.txt`, `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt`, and `licenses/dotnet-windowsdesktop-LICENSE.txt` with distributions. SDK 10.0.402 is a build tool; end users do not need it. Its checked download hash is recorded in `dependencies/DEPENDENCIES.json`.

## SQLite persistence

| Component | Version | License / attribution |
|---|---:|---|
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.13 | MIT; Microsoft Corporation, .NET Foundation and contributors |
| SQLitePCLRaw.bundle_e_sqlite3 / config.e_sqlite3 / core / provider.e_sqlite3 | 3.0.5 | Apache-2.0; SourceGear, LLC |
| SQLite native e_sqlite3 | 3.53.4 | SQLite public domain; binary packaging by Eric Sink / SourceGear |

The resolved versions are pinned in `packages.lock.json`. Sources: [EF Core at the declared .NET commit](https://github.com/dotnet/dotnet/tree/4099f83977f4d29604a9aeb971fb0a7bc1d5f699/src/efcore), [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw), and [SQLite](https://sqlite.org/).

SQLitePCLRaw core/provider package metadata names commit `ed046114d5a30534e13294d94d78eb73de896ad4`; config names `96043b8cff323f21919df86a431c136655d81b4a`. Exact original `.nuspec` files are retained in `licenses/`, together with `Microsoft.Data.Sqlite-MIT.txt`, `SQLitePCLRaw-Apache-2.0.txt`, and `SQLite-PublicDomain.txt`.

## Optional FFmpeg and ffprobe — local package only

The media tests used unmodified `9.0.2-essentials_build-www.gyan.dev`, Windows x64 static GPLv3 binaries. [Gyan](https://www.gyan.dev/ffmpeg/builds/) is linked by the [FFmpeg download page](https://ffmpeg.org/download.html). The pinned archive is [ffmpeg-9.0.2-essentials_build.zip](https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip); its SHA-256 was checked against the distributor's published value. Exact hashes are in `dependencies/DEPENDENCIES.json`.

The private local dependency tree retains `FFMPEG_LICENSE`, `FFMPEG_README.txt`, capabilities/configuration output, and core source snapshot `sources/FFmpeg-946fcce07b.tar.gz` from [FFmpeg commit 946fcce07b](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b).

That core source snapshot is not the complete corresponding source for every statically linked external library and build script. This work did not reproduce the distributor's tool build or complete that audit. Therefore the public HashVista core deliberately excludes these binaries and the partial tool-source tree. A future bundled public media release requires the matching source/build material and applicable notices for the exact build. See [FFmpeg's licensing guidance](https://ffmpeg.org/legal.html); its LGPL linking checklist alone is not an audit of this static GPL build.

## Optional ExifTool — local package only

Media tests used ExifTool 13.59 by Phil Harvey, copyright 2003–2026, distributed under the same terms as Perl. Official project: [ExifTool](https://exiftool.org/). The site's Windows package link was [exiftool-13.59_64.zip](https://sourceforge.net/projects/exiftool/files/exiftool-13.59_64.zip/download). The observed archive SHA-256 is pinned in `dependencies/DEPENDENCIES.json`; an independent upstream signature was not verified.

The small Windows launcher by Oliver Betz is CC0, as documented by its upstream `readme_windows.txt`; source project: [ExifTool for Windows](https://oliverbetz.de/pages/Artikel/ExifTool-for-Windows). `licenses/ExifTool-launcher-CC0.txt` is retained. The launcher is renamed from `exiftool(-k).exe` to `exiftool.exe` as upstream directs. A local installation must retain the complete `exiftool_files/` tree, including readable Perl modules, `LICENSE`, `Licenses_Strawberry_Perl.zip`, runtime files, and upstream instructions. Preserve the Strawberry Perl/GCC notices from that archive. HashVista only invokes read-only metadata/extraction commands; fixture-generation tests intentionally write only their disposable samples.

ExifTool and its runtime are excluded from the public core, along with the other optional media tools. Keeping an attribution or acquisition script in the public source does not imply those binaries are redistributed there.

## Test fixtures

The generated color patterns, sine tones, EXIF fields, and short videos are controlled project fixtures. Their original provenance records are in the development fixture directory. Small upstream ExifTool and libheif samples retain their source/commit, licenses, and hashes where supplied. They are test inputs, not runtime dependencies; optional download paths and hashes are described in `MEDIA_PROFILES.md`.
