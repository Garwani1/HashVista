# Build HashVista 1.3.0

Windows x64; pinned .NET SDK 10.0.402; WPF `net10.0-windows`; self-contained `win-x64`. The resulting `HashVista.exe` opens a normal desktop window. The source project file is `HashVista.csproj`. Existing `.ghscan` files remain supported.

## Source and pinned dependencies

`src/` contains the application. `COPYING`, source notices, and `THIRD_PARTY_NOTICES.md` retain the GPL-3.0-or-later OpenHashTab/CSV1 adaptations. The original native shell-extension baseline is preserved in the local development workspace but is not linked or needed to build this C# application.

- SDK: 10.0.402, selected by `global.json`.
- Runtime: .NET/WPF 10.0.13 in the verified self-contained publish.
- Microsoft.Data.Sqlite 10.0.13; SQLitePCLRaw.bundle_e_sqlite3 3.0.5; exact transitive resolution in `packages.lock.json`.
- Optional local media tools tested: FFmpeg/ffprobe 9.0.2 essentials (Gyan) and ExifTool 13.59 x64 with its complete runtime tree.

Use the pinned SDK on Windows. An installed SDK can be found with `Get-Command dotnet`; alternatively set `$dotnet` below to an acquired SDK's `dotnet.exe`. SDK acquisition is a build step, not an end-user runtime requirement.

## Direct public-core build

Run from the application source directory in PowerShell. Use a new output directory so an earlier media-enabled output cannot leave extra tools behind.

```powershell
$dotnet = (Get-Command dotnet).Source
& $dotnet --version
& $dotnet restore .\HashVista.csproj --locked-mode
if ($LASTEXITCODE) { throw 'Restore failed' }
& $dotnet publish .\HashVista.csproj -c Release -r win-x64 --self-contained true --no-restore -p:IncludeMediaTools=false -o .\out\HashVista-1.3.0-core
if ($LASTEXITCODE) { throw 'Publish failed' }
```

The public core excludes external media executables; hashing, inventory, persistence, and CSV work without them. Source and portable notices must be shipped with the binary. Use the public staging script to assemble an allowlisted copy rather than uploading the whole working directory:

```powershell
.\scripts\prepare-public.ps1 -DestinationRoot ..\HashVista-1.3.0-public -CorePublishDirectory .\out\HashVista-1.3.0-core
```

The destination must be new. The script creates `repository/`, optionally `core-portable/`, and a relative-path manifest. It excludes private evidence, scan databases, user logs, and media tools. It does not upload to GitHub. Publish the matching application source alongside the executable; preserve the licenses. See `PUBLIC_RELEASE.md` for distribution details.

## Local media-enabled build and tool setup

Use FFmpeg/ffprobe from the [official FFmpeg download links](https://ffmpeg.org/download.html) and ExifTool from [its original project](https://exiftool.org/). The pinned acquisition script downloads checked archives into the development workspace:

```powershell
.\scripts\dependencies.ps1 -InstallSdk
```

The checked-in script is authoritative for acquisition switches and pinned URLs/hashes. Media tools are put into the application's `dependencies/` directory. Keep `ffmpeg.exe`, `ffprobe.exe`, `exiftool.exe`, and the entire `exiftool_files/` tree together. For a previously unpacked public core, place that directory beside `HashVista.exe`. `Tools & profiles` reports the detected files. The application does not silently download tools.

To produce a separate local full build after acquiring tools:

```powershell
& $dotnet publish .\HashVista.csproj -c Release -r win-x64 --self-contained true --no-restore -p:IncludeMediaTools=true -o .\out\HashVista-1.3.0-local-full
if ($LASTEXITCODE) { throw 'Publish failed' }
```

This local full build is not the public redistribution artifact. The current static FFmpeg package has an incomplete corresponding-source audit for linked external libraries; its inclusion in a public binary release is deliberately avoided. Retained manifests/notices document the local tools, not a claim of a reproducible FFmpeg toolchain.

## Run the six Windows suites against the published DLL

All six harnesses reference `HashVista.dll` from `AppDir`. They do not substitute copied application source. Set `$publish` to the publish being tested. Preserve logs and compare the tested DLL SHA-256 with the release DLL.

```powershell
$source = (Get-Location).Path
$publish = (Resolve-Path .\out\HashVista-1.3.0-local-full).Path
$suites = @('CoreIntegration','Regression122','Regression123','Regression130','FilteredExport130','MediaIntegration')
foreach ($suite in $suites) {
    $project = Join-Path $source ('tests\' + $suite + '\' + $suite + '.csproj')
    & $dotnet build $project -c Release ("-p:AppDir=" + $publish)
    if ($LASTEXITCODE) { throw ('Build failed: ' + $suite) }
    $testDll = Join-Path $source ('tests\' + $suite + '\bin\Release\net10.0-windows\' + $suite + '.dll')
    & $dotnet $testDll $source
    if ($LASTEXITCODE) { throw ('Tests failed: ' + $suite) }
}
```

`MediaIntegration` uses the source tree's `dependencies/` and generates fixtures under `evidence/media-fixtures`. `Regression122` also requires real media tools and the generated multistream fixture at `tests/fixtures/media/فيديو, two-audio.mp4`. They are not core-only CI suites. CoreIntegration, Regression123, Regression130, and FilteredExport130 can run without media tools; CoreIntegration still exercises explicit missing/invalid-media failure handling. Windows junction/link tests require the normal Windows permissions needed to create their controlled fixtures.

The source distribution contains generated test fixture media and provenance. Optional upstream Nikon NEF and libheif HEIC samples are described with hashes in `MEDIA_PROFILES.md`; put them at `evidence/media-fixtures/ExifTool-Nikon.nef` and `evidence/media-fixtures/libheif-example.heic` to run those additional checks. The media harness conditionally skips them when absent, so the number of checks must be reported from the actual run, not assumed to be 72.

For an already populated offline SDK/package cache, append `-p:NuGetAudit=false --ignore-failed-sources` to restore/build commands as needed, keeping the application's `--locked-mode`. That mode does not conduct a new network vulnerability audit. Missing cached packages still fail. The direct commands above are the public-source build path; local workspace helper scripts are not required.

## Verification and packaging boundaries

The six suites are programmatic Windows checks. GUI launch, root selection, pause/continue, previews, and saving CSV through the UI are separate observations recorded in `VERIFICATION.md`. A successful build does not establish a successful GUI workflow. Synthetic large-result tests are not full-archive throughput benchmarks. Hosted GitHub Actions execution is not claimed until an actual run exists.

`tests/Performance130` is a separate bounded benchmark; `RootProbe123` is an opt-in diagnostic and is never part of the automatic release suite. No build command automatically scans user archive drives.

Before packaging, update the verification report, confirm the tested and shipped DLL hashes, and run the publication allowlist. Keep `Data/`, development evidence, personal paths, and scan databases out of the public source and ZIP. A ZIP integrity check establishes the copied bytes, not runtime correctness. The application is unsigned and has no installer or automatic updater.
