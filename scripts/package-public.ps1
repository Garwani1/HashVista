# SPDX-License-Identifier: GPL-3.0-or-later
# Package only the allowlisted output of prepare-public.ps1. No uploads/deletes.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$StagingDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$PackagePrefix
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stage = [IO.Path]::GetFullPath($StagingDirectory).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($output -eq $stage -or $output.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must be outside the public staging directory.'
}
$manifestPath = Join-Path $stage 'PUBLIC_MANIFEST.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Schema -ne 'public-staging-v1' -or $manifest.OptionalMediaBinariesIncluded -ne $false -or $manifest.PrivateEvidenceIncluded -ne $false) {
    throw 'A valid core-only public staging manifest is required.'
}
$version = [string]$manifest.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw 'Invalid staging version.' }
if (!$PackagePrefix) { $PackagePrefix = 'HashVista-' + $version }
if ($PackagePrefix -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$') { throw 'Unsafe package prefix.' }
$sourceName = $PackagePrefix + '-source'
$coreName = $PackagePrefix + '-win-x64-core'
$reportName = 'PACKAGE_VERIFICATION-' + $version + '.json'
$sumsName = 'SHA256SUMS-' + $version + '.txt'
foreach ($name in @(($sourceName + '.zip'), ($coreName + '.zip'), $reportName, $sumsName)) {
    if (Test-Path -LiteralPath (Join-Path $output $name)) { throw ('Refusing to overwrite release output: ' + $name) }
}

$expected = @{}
foreach ($file in $manifest.Files) {
    $relative = [string]$file.Path
    if ($relative -notmatch '^(repository|core-portable)/' -or $relative -match '(^|/)\.\.(/|$)') { throw 'Unsafe path in staging manifest.' }
    if ($expected.ContainsKey($relative)) { throw 'Duplicate path in staging manifest.' }
    $expected[$relative] = $file
}

function Get-Entries([string]$folderName, [string]$prefix) {
    $root = Join-Path $stage $folderName
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw ('Missing staged folder: ' + $folderName) }
    if ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staged folder must not be a link.' }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Links are not packaged.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            $relative = $item.FullName.Substring($root.Length + 1).Replace('\','/')
            $manifestKey = $folderName + '/' + $relative
            if (!$expected.ContainsKey($manifestKey)) { throw ('Staged file absent from allowlist manifest: ' + $manifestKey) }
            $entry = $expected[$manifestKey]
            $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($item.Length -ne $entry.Bytes -or $hash -ne $entry.Sha256) { throw ('Staged file changed since verification: ' + $manifestKey) }
            [pscustomobject]@{ Source=$item.FullName; Entry=$prefix + '/' + $relative; Bytes=$item.Length; Sha256=$hash }
        }
    }
}
$sourceFiles = @(Get-Entries 'repository' $sourceName)
$coreFiles = @(Get-Entries 'core-portable' $coreName)
if (!$sourceFiles.Count -or !$coreFiles.Count) { throw 'Both source and core folders must contain files.' }
if (($sourceFiles.Count + $coreFiles.Count) -ne $expected.Count) { throw 'Staged files are missing from the manifest inventory.' }
if (!($sourceFiles.Entry -contains ($sourceName + '/.gitignore')) -or !($sourceFiles.Entry -contains ($sourceName + '/.github/workflows/build.yml'))) {
    throw 'Public source is missing .gitignore or the GitHub workflow.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Package-Files([object[]]$files, [string]$name) {
    $target = Join-Path $output $name
    $zip = [IO.Compression.ZipFile]::Open($target, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.Source, $file.Entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    $zip = [IO.Compression.ZipFile]::OpenRead($target)
    $verified = [Collections.Generic.List[object]]::new()
    try {
        if ($zip.Entries.Count -ne $files.Count) { throw ('ZIP entry count mismatch: ' + $name) }
        foreach ($file in $files) {
            $entry = $zip.GetEntry($file.Entry)
            if (!$entry -or $entry.Length -ne $file.Bytes) { throw ('ZIP entry size mismatch: ' + $file.Entry) }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($hash -ne $file.Sha256) { throw ('ZIP entry hash mismatch: ' + $file.Entry) }
            $verified.Add([ordered]@{ Path=$file.Entry; Bytes=$entry.Length; Sha256=$hash })
        }
    } finally { $zip.Dispose() }
    [ordered]@{
        File=$name; Bytes=(Get-Item -LiteralPath $target).Length;
        Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant();
        Entries=$files.Count; AllEntriesMatchedSourceSha256=$true; Files=@($verified)
    }
}
$packages = @(
    (Package-Files $coreFiles ($coreName + '.zip')),
    (Package-Files $sourceFiles ($sourceName + '.zip'))
)
$checksums = ($packages | ForEach-Object { $_.Sha256 + '  ' + $_.File }) -join "`r`n"
[IO.File]::WriteAllText((Join-Path $output $sumsName), $checksums + "`r`n", [Text.UTF8Encoding]::new($false))
[ordered]@{
    Schema='public-package-v1'; Version=$version; AssemblyName=$manifest.AssemblyName;
    VerifiedUtc=[DateTime]::UtcNow.ToString('O');
    StagingManifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
    Method='All ZIP entries verified by length and SHA-256 against the allowlisted staging manifest';
    PrivateEvidenceIncluded=$false; OptionalMediaBinariesIncluded=$false;
    HiddenRepositoryFilesIncluded=$true; Packages=$packages
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output $reportName) -Encoding UTF8
$packages | ForEach-Object { Write-Output ($_.File + ': ' + $_.Entries + ' entries verified; SHA256 ' + $_.Sha256) }
