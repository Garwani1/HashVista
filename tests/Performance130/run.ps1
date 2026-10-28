[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$Dotnet,
 [Parameter(Mandatory=$true)][string]$BaselineAssembly,
 [Parameter(Mandatory=$true)][string]$FixedAssembly,
 [Parameter(Mandatory=$true)][string]$Dependencies,
 [string]$RunRoot
)
$ErrorActionPreference='Stop'
if(!$RunRoot){$RunRoot=Join-Path $PSScriptRoot ('runs\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))}
$runner=Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Performance130.dll'
if(!(Test-Path -LiteralPath $runner)){throw 'Build Performance130.csproj in Release first.'}
& $Dotnet $runner prepare $RunRoot
if($LASTEXITCODE -ne 0){throw 'Dataset preparation failed'}
$runs=@()
foreach($item in @(
 @{Label='baseline-warmup';Assembly=$BaselineAssembly;Version='baseline';Measured=$false},
 @{Label='fixed-warmup';Assembly=$FixedAssembly;Version='fixed';Measured=$false},
 @{Label='baseline-1';Assembly=$BaselineAssembly;Version='baseline';Measured=$true},
 @{Label='fixed-1';Assembly=$FixedAssembly;Version='fixed';Measured=$true},
 @{Label='fixed-2';Assembly=$FixedAssembly;Version='fixed';Measured=$true},
 @{Label='baseline-2';Assembly=$BaselineAssembly;Version='baseline';Measured=$true},
 @{Label='baseline-3';Assembly=$BaselineAssembly;Version='baseline';Measured=$true},
 @{Label='fixed-3';Assembly=$FixedAssembly;Version='fixed';Measured=$true}
)){
 $resultPath=Join-Path $RunRoot ($item.Label+'.json')
 & $Dotnet $runner scan $item.Assembly $Dependencies $RunRoot $item.Label $resultPath
 if($LASTEXITCODE -ne 0){throw ('Benchmark validation failed: '+$item.Label)}
 $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
 $result | Add-Member -NotePropertyName VersionLabel -NotePropertyValue $item.Version
 $result | Add-Member -NotePropertyName Measured -NotePropertyValue $item.Measured
 $runs+=$result
}
function Median([object[]]$values){$ordered=@($values|Sort-Object);return [double]$ordered[[int][Math]::Floor($ordered.Count/2)]}
$baseline=@($runs|Where-Object {$_.Measured -and $_.VersionLabel -eq 'baseline'})
$fixed=@($runs|Where-Object {$_.Measured -and $_.VersionLabel -eq 'fixed'})
$baseMedian=Median @($baseline.WallSeconds)
$fixedMedian=Median @($fixed.WallSeconds)
$hashes=@($runs.ResultSetSha256|Select-Object -Unique)
if($hashes.Count -ne 1){throw 'Baseline and fixed result sets differ'}
$summary=[ordered]@{
 Schema='HashVista-Performance130-v1';CompletedUtc=[DateTime]::UtcNow.ToString('O')
 Dataset=(Get-Content -LiteralPath (Join-Path $RunRoot 'dataset.json') -Raw|ConvertFrom-Json)
 Method='Separate process per run; one warmup per version; three measured runs per version with alternating order; time covers ScanEngine.RunAsync only, excludes fixture generation, database creation, startup and independent result verification.'
 Limitations='Warm-cache local Windows filesystem; single controlled dataset and SHA256-only configuration. No claim about cold storage, network drives, media work, or 10 TB workloads.'
 BaselineMedianSeconds=$baseMedian;FixedMedianSeconds=$fixedMedian
 FixedVersusBaselinePercent=100*($fixedMedian/$baseMedian-1)
 BaselineMedianInventorySeconds=(Median @($baseline.InventorySeconds));FixedMedianInventorySeconds=(Median @($fixed.InventorySeconds))
 BaselineMedianProcessingSeconds=(Median @($baseline.ProcessingSeconds));FixedMedianProcessingSeconds=(Median @($fixed.ProcessingSeconds))
 AllCountsAndIndependentHashesMatch=$true;ResultSetSha256=$hashes[0];Runs=$runs
}
$summaryPath=Join-Path $RunRoot 'summary.json'
$summary|ConvertTo-Json -Depth 9|Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Output ('Baseline median: {0:F3}s; fixed median: {1:F3}s; change: {2:F1}%; all counts and hashes match.' -f $baseMedian,$fixedMedian,$summary.FixedVersusBaselinePercent)
Write-Output ('Evidence: '+$summaryPath)

