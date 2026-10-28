# HashVista — Hash & Media Scanner

A local Windows application for file inventory, full-file hashes, optional image/video inspection, and CSV export.

The scanner records every in-scope file independently, including unique files, identical copies with different names, empty files, and failures. It does not find duplicates, compare files, group similar media, move files, or delete source files.

## Download and use

Use the Windows x64 **core** package and extract the entire ZIP. Double-click `HashVista.exe`; no SDK, terminal, or separately installed .NET runtime is required. Add files or folders, select options, start a scan, and export the stored results. See [the Arabic guide](README_AR.md).

SHA-256 is the default. SHA-512, SHA-1, and MD5 are optional and share a streamed read. Saved scans use SQLite and can be reopened and resumed. Resumed results retain their provenance. Cryptographic file hashes, decoded-pixel hashes, perceptual fingerprints, and stream hashes remain separate fields.

The public core package includes file inventory, hashing, saved scans, and CSV export. Media tools are optional downloads supplied separately by their publishers; they are not included in this core package. See [PUBLIC_RELEASE.md](PUBLIC_RELEASE.md) for setup and distribution details. Unsupported or missing media capabilities must not prevent ordinary file hashing.

## Build the core on Windows

Install the .NET SDK version in `global.json` (currently 10.0.402), then run from this directory:

```powershell
dotnet restore .\HashVista.csproj --locked-mode -p:IncludeMediaTools=false
dotnet publish .\HashVista.csproj -c Release -r win-x64 --self-contained true --no-restore -p:IncludeMediaTools=false -o .\artifacts\core
$appDir = Join-Path $PWD 'artifacts\core'
dotnet run --project .\tests\CoreIntegration\CoreIntegration.csproj -c Release ('-p:AppDir=' + $appDir) -- .
```

Use a fresh publish directory. For a shareable repository and a clean core portable folder, see `scripts/prepare-public.ps1`; `scripts/package-public.ps1` creates verified ZIPs from that staging directory. Their allowlist excludes local scan databases, personal evidence, media executables, and build caches. The GitHub workflow performs CoreIntegration, Regression123, Regression130, and FilteredExport130 checks. Regression122 and MediaIntegration need optional media tools/fixtures and remain separate. The presence of the workflow does not mean a hosted CI run has passed.

## Accuracy and performance

Hashing all bytes takes time proportional to the data and the storage device. A quick file listing is not a full-content hash scan. Small local benchmarks do not establish performance on a multi-terabyte archive. The tests use controlled fixtures and independent expected hashes. The scanner opens source files for reading; file-size and modification-time checks detect observable changes but are not a filesystem snapshot.

## Disclaimer

HashVista is provided "as is", without any warranty, and the author accepts no liability for data loss, incorrect results, or any other damage arising from its use. Use it at your own risk and verify important results independently. See [DISCLAIMER.md](DISCLAIMER.md) and [TERMS.md](TERMS.md).

## License and attribution

GPL-3.0-or-later; see [COPYING](COPYING) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). CSV and bounded-stream hashing behavior were adapted from the supplied OpenHashTab 3.1.1 / CSV1 work by namazso and contributors. Preserve the source headers and notices. This is an independent application, not an official OpenHashTab release.

Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).
