# SPDX-License-Identifier: GPL-3.0-or-later
# Builds a public allowlisted copy. Never uploads, deletes, or overwrites.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DestinationRoot,
    [string]$CorePublishDirectory
)
$ErrorActionPreference = 'Stop'
$appRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$destination = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
if (Test-Path -LiteralPath $destination) { throw 'Public staging destination already exists. Choose a new directory.' }
if ($destination -eq $appRoot -or $appRoot.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The public destination must not be the application root or one of its parents.'
}
$repo = Join-Path $destination 'repository'
$core = Join-Path $destination 'core-portable'
$files = [Collections.Generic.List[object]]::new()
[xml]$project = Get-Content -LiteralPath (Join-Path $appRoot 'HashVista.csproj') -Raw
$assembly = [string]$project.Project.PropertyGroup.AssemblyName
if ($assembly -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Missing or unsafe assembly name.' }
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') { throw 'Missing or invalid release version.' }

function Add-PublicFile([string]$source, [string]$relative) {
    $sourcePath = [IO.Path]::GetFullPath($source)
    if (!(Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw ('Required source missing: ' + $relative) }
    $item = Get-Item -LiteralPath $sourcePath -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Linked files are not staged: ' + $relative) }
    $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$target.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Target escaped public staging directory.' }
    $files.Add([pscustomobject]@{ Source=$sourcePath; Target=$target; Relative=$relative.Replace('\','/') })
}

function Add-AllowedTree([string]$sourceRoot, [string]$relativeRoot, [scriptblock]$include) {
    if (!(Test-Path -LiteralPath $sourceRoot -PathType Container)) { return }
    if ((Get-Item -LiteralPath $sourceRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A public source root must not be a linked directory.' }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($sourceRoot)
    while ($pending.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $relative = $entry.FullName.Substring($sourceRoot.Length + 1)
            if ($entry.PSIsContainer) {
                if ($relative -match '(^|\\)(bin|obj|runs|[^\\]*-runs|\.git|Data|evidence)(\\|$)') { continue }
                if ($relativeRoot -eq 'core-portable' -and $relative -match '^(dependencies|licenses)(\\|$)') { continue }
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Linked directories are not staged: ' + $relative) }
                $pending.Push($entry.FullName)
            } elseif (& $include $relative) {
                Add-PublicFile $entry.FullName (Join-Path $relativeRoot $relative)
            }
        }
    }
}

$rootFiles = @('HashVista.csproj','app.manifest','global.json','packages.lock.json',
    'COPYING','README.md','README_AR.md','PUBLIC_RELEASE.md','CONTRIBUTING.md','THIRD_PARTY_NOTICES.md','MEDIA_PROFILES.md',
    'BUILD.md','VERIFICATION.md','DISCLAIMER.md','TERMS.md','.gitignore')
# Do not accidentally stage a previous release's verification/build report.
foreach ($document in @('BUILD.md','VERIFICATION.md')) {
    $firstLine = Get-Content -LiteralPath (Join-Path $appRoot $document) -TotalCount 1
    if ($firstLine -notmatch [regex]::Escape($version) -or $firstLine -notmatch 'HashVista') {
        throw ('Public document must identify this HashVista release: ' + $document)
    }
}
foreach ($file in $rootFiles) { Add-PublicFile (Join-Path $appRoot $file) ('repository\' + $file) }
Add-AllowedTree (Join-Path $appRoot 'src') 'repository\src' { param($r) $r -match '\.cs$' }
Add-AllowedTree (Join-Path $appRoot 'licenses') 'repository\licenses' { param($r) $r -match '\.(txt|nuspec)$' }
foreach ($script in @('dependencies.ps1','prepare-public.ps1','package-public.ps1')) {
    Add-PublicFile (Join-Path $PSScriptRoot $script) ('repository\scripts\' + $script)
}
Add-PublicFile (Join-Path $appRoot '.github\workflows\build.yml') 'repository\.github\workflows\build.yml'
Add-PublicFile (Join-Path $appRoot 'dependencies\DEPENDENCIES.json') 'repository\dependencies\DEPENDENCIES.json'
foreach ($suite in @('CoreIntegration','Regression122','Regression123','Regression130','FilteredExport130','MediaIntegration')) {
    Add-AllowedTree (Join-Path $appRoot ('tests\' + $suite)) ('repository\tests\' + $suite) { param($r) $r -match '\.(cs|csproj)$' }
}
Add-AllowedTree (Join-Path $appRoot 'tests\Performance130') 'repository\tests\Performance130' {
    param($r) $r -match '\.(cs|csproj|ps1)$' -or $r -eq 'README.md'
}
# Only the reviewed synthetic media fixtures are public; upstream camera/HEIC
# examples and any other local media directory are deliberately not traversed.
$fixtureRoot = Join-Path $appRoot 'tests\fixtures'
$fixtureManifest = Join-Path $fixtureRoot 'SYNTHETIC_PROVENANCE.json'
$fixtureProvenance = Get-Content -LiteralPath $fixtureManifest -Raw | ConvertFrom-Json
$syntheticNames = @('animated.gif','orientation6-exif.jpg','transparent.png','variable-frame-rate.mp4','very-short.mp4',
    'ألوان, sample.bmp','ألوان, sample.gif','ألوان, sample.jpg','ألوان, sample.png','ألوان, sample.tiff','ألوان, sample.webp','فيديو, two-audio.mp4')
foreach ($name in $syntheticNames) {
    $relative = 'media/' + $name
    $record = @($fixtureProvenance.Files | Where-Object { $_.Path.Replace('\','/') -ceq $relative })
    if ($record.Count -ne 1) { throw ('Synthetic fixture lacks a unique provenance record: ' + $name) }
    $fixture = Join-Path $fixtureRoot ('media\' + $name)
    $actual = Get-Item -LiteralPath $fixture
    if ($actual.Length -ne $record[0].Bytes -or (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash -ine $record[0].Sha256) {
        throw ('Synthetic fixture does not match its recorded digest: ' + $name)
    }
    Add-PublicFile $fixture ('repository\tests\fixtures\media\' + $name)
}
Add-PublicFile $fixtureManifest 'repository\tests\fixtures\SYNTHETIC_PROVENANCE.json'
$fixtureReadme = Join-Path $fixtureRoot 'README_PUBLIC.md'
# The already-public source tree stores the same curated text as README.md.
if (!(Test-Path -LiteralPath $fixtureReadme)) { $fixtureReadme = Join-Path $fixtureRoot 'README.md' }
Add-PublicFile $fixtureReadme 'repository\tests\fixtures\README.md'

if ($CorePublishDirectory) {
    $publish = [IO.Path]::GetFullPath($CorePublishDirectory).TrimEnd('\')
    if (!(Test-Path -LiteralPath (Join-Path $publish ($assembly + '.exe')) -PathType Leaf)) { throw 'Published application EXE is missing.' }
    if (!(Test-Path -LiteralPath (Join-Path $publish ($assembly + '.dll')) -PathType Leaf)) { throw 'Published application DLL is missing.' }
    foreach ($extension in @('.exe','.dll')) {
        $productVersion = (Get-Item -LiteralPath (Join-Path $publish ($assembly + $extension))).VersionInfo.ProductVersion
        if (($productVersion -split '\+')[0] -ne $version) { throw ('Published application version does not match source: ' + $assembly + $extension) }
    }
    Add-AllowedTree $publish 'core-portable' {
        param($r)
        if ($r -match '^(dependencies|licenses|Data|evidence)\\') { return $false }
        return $r -match '\.(exe|dll|json)$'
    }
    foreach ($file in @('COPYING','README.md','README_AR.md','BUILD.md','PUBLIC_RELEASE.md','THIRD_PARTY_NOTICES.md','MEDIA_PROFILES.md','VERIFICATION.md','DISCLAIMER.md','TERMS.md')) {
        Add-PublicFile (Join-Path $appRoot $file) ('core-portable\' + $file)
    }
    Add-AllowedTree (Join-Path $appRoot 'licenses') 'core-portable\licenses' { param($r) $r -match '\.(txt|nuspec)$' }
}

# Reject prohibited content before any writes. File lists are allowlisted above;
# this additional guard prevents accidental database or media-tool inclusion.
$duplicate = $files | Group-Object Relative | Where-Object Count -gt 1
if ($duplicate) { throw 'A public target was listed more than once.' }
foreach ($file in $files) {
    if ($file.Relative -match '(^|/)(evidence|Data|runs|[^/]*-runs|bin|obj)/|\.(ghscan|sqlite|db|zip|csv|log|pdb)$|(^|/)(ffmpeg|ffprobe|exiftool)\.exe$|exiftool_files/') {
        throw ('Prohibited public entry: ' + $file.Relative)
    }
}
New-Item -ItemType Directory -Path $repo -Force | Out-Null
$manifest = foreach ($file in $files) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $file.Target) -Force | Out-Null
    Copy-Item -LiteralPath $file.Source -Destination $file.Target
    $originalHash = (Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash.ToLowerInvariant()
    $copiedHash = (Get-FileHash -LiteralPath $file.Target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($originalHash -ne $copiedHash) { throw ('Copied content mismatch: ' + $file.Relative) }
    [ordered]@{ Path=$file.Relative; Bytes=(Get-Item -LiteralPath $file.Target).Length; Sha256=$copiedHash }
}
[ordered]@{
    Schema='public-staging-v1'; Version=$version; AssemblyName=$assembly; CreatedUtc=[DateTime]::UtcNow.ToString('O');
    OptionalMediaBinariesIncluded=$false; PrivateEvidenceIncluded=$false;
    HostedCiRunClaimed=$false; Files=@($manifest)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'PUBLIC_MANIFEST.json') -Encoding UTF8
Write-Output ('Public repository: ' + $repo)
if ($CorePublishDirectory) { Write-Output ('Core portable: ' + $core) }
Write-Output ('Verified copied files: ' + $files.Count)
