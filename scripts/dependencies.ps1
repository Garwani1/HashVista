# GPL-3.0-or-later. Reacquire pinned official dependencies without a machine install.
[CmdletBinding()]
param([switch]$InstallSdk)
$ErrorActionPreference = 'Stop'
$app = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $app
$cache = Join-Path $workspace 'downloads'
$dep = Join-Path $app 'dependencies'
New-Item -ItemType Directory -Path $cache,$dep -Force | Out-Null
function Fetch([string]$Uri,[string]$Path,[string]$Hash,[string]$Algorithm='SHA256') {
    if ((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash -ieq $Hash) { return }
    Invoke-WebRequest -Uri $Uri -OutFile $Path -TimeoutSec 180
    # SourceForge intentionally returns a download page before the actual binary.
    for ($attempt=0; $attempt -lt 4; $attempt++) {
        $stream=[IO.File]::OpenRead($Path)
        try { $magic=[byte[]]::new(2); [void]$stream.Read($magic,0,2) } finally { $stream.Dispose() }
        if ($magic[0] -eq 80 -and $magic[1] -eq 75) { break }
        $html=[IO.File]::ReadAllText($Path)
        $match=[regex]::Match($html,'http-equiv="refresh" content="\d+; url=([^"]+)')
        if (!$match.Success) { break }
        $next=[System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
        if (!([uri]$next).Host.EndsWith('sourceforge.net')) { throw 'Unexpected SourceForge redirect host' }
        Invoke-WebRequest -Uri $next -OutFile $Path -TimeoutSec 180
    }
    $actual=(Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash
    if ($actual -ine $Hash) { throw "Checksum mismatch for $Path : $actual" }
}
$ffmpegZip=Join-Path $cache 'ffmpeg-9.0.2-essentials_build.zip'
Fetch 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip' $ffmpegZip '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$ffmpegExtract=Join-Path $cache 'ffmpeg-github-extracted'
Expand-Archive -LiteralPath $ffmpegZip -DestinationPath $ffmpegExtract -Force
$ffmpegPackage=Join-Path $ffmpegExtract 'ffmpeg-9.0.2-essentials_build'
Copy-Item -LiteralPath (Join-Path $ffmpegPackage 'bin\ffmpeg.exe'),(Join-Path $ffmpegPackage 'bin\ffprobe.exe') -Destination $dep -Force
Copy-Item -LiteralPath (Join-Path $ffmpegPackage 'LICENSE') -Destination (Join-Path $dep 'FFMPEG_LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $ffmpegPackage 'README.txt') -Destination (Join-Path $dep 'FFMPEG_README.txt') -Force
$exifZip=Join-Path $cache 'exiftool-13.59_64.zip'
Fetch 'https://sourceforge.net/projects/exiftool/files/exiftool-13.59_64.zip/download' $exifZip '44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec'
$exifExtract=Join-Path $cache 'exiftool-extracted'
Expand-Archive -LiteralPath $exifZip -DestinationPath $exifExtract -Force
$exifPackage=Join-Path $exifExtract 'exiftool-13.59_64'
Copy-Item -LiteralPath (Join-Path $exifPackage 'exiftool(-k).exe') -Destination (Join-Path $dep 'exiftool.exe') -Force
# Copy child contents explicitly; repeated execution must not nest exiftool_files.
$exifSupport=Join-Path $dep 'exiftool_files'
New-Item -ItemType Directory -Path $exifSupport -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $exifPackage 'exiftool_files') -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $exifSupport -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $exifPackage 'README.txt') -Destination (Join-Path $dep 'EXIFTOOL_README.txt') -Force
if ($InstallSdk) {
    $sdkZip=Join-Path $cache 'dotnet-sdk-win-x64.zip'
    Fetch 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-win-x64.zip' $sdkZip 'ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd' 'SHA512'
    $sdkPath=Join-Path $workspace 'tools\dotnet'
    Expand-Archive -LiteralPath $sdkZip -DestinationPath $sdkPath -Force
    & (Join-Path $sdkPath 'dotnet.exe') --version
}
& (Join-Path $dep 'ffmpeg.exe') -version
& (Join-Path $dep 'ffprobe.exe') -version
$oldLocale=@{LC_ALL=$env:LC_ALL; LC_CTYPE=$env:LC_CTYPE; LANG=$env:LANG}
try {
    $env:LC_ALL='C'; $env:LC_CTYPE='C'; $env:LANG='C'
    & (Join-Path $dep 'exiftool.exe') -ver
} finally {
    $env:LC_ALL=$oldLocale.LC_ALL; $env:LC_CTYPE=$oldLocale.LC_CTYPE; $env:LANG=$oldLocale.LANG
}
