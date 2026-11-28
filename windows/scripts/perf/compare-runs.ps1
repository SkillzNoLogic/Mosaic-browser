<#
.SYNOPSIS
    Puts several results.json files (one per build, e.g. per publish variant) side by side:
    size and the medians of the start-up metrics, with the change against a baseline label.

.PARAMETER Results
    The results.json files to compare.

.PARAMETER Baseline
    The label the others are compared with. Default: the first file's label.

.PARAMETER OutputPath
    Where to write the comparison (Markdown). A .json with the same numbers is written beside it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string[]]$Results,
    [string]$Baseline,
    [string]$OutputPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-Field {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

$columns = [ordered]@{
    'first run: window'   = 'startup.firstRun.windowShownMs'
    'cold: window'        = 'startup.cold.windowShownMs'
    'cold: first frame'   = 'startup.cold.mark.firstFrame'
    'warm: main'          = 'startup.warm.mark.main'
    'warm: window'        = 'startup.warm.windowShownMs'
    'warm: first frame'   = 'startup.warm.mark.firstFrame'
    'warm: first page'    = 'startup.warm.firstPageMs'
    'warm: gecko ready'   = 'startup.warm.mark.geckoReady'
}

$runs = @($Results | ForEach-Object { Get-Content -LiteralPath $_ -Raw -Encoding UTF8 | ConvertFrom-Json })
if ($runs.Count -eq 0) { throw 'No results to compare.' }
if (-not $Baseline) { $Baseline = [string](Get-Field $runs[0] 'label') }
$base = @($runs | Where-Object { (Get-Field $_ 'label') -eq $Baseline }) | Select-Object -First 1

$present = [ordered]@{}
foreach ($column in $columns.Keys) {
    foreach ($run in $runs) {
        if ($null -ne (Get-Field (Get-Field $run 'metrics') $columns[$column])) { $present[$column] = $columns[$column]; break }
    }
}

$architecture = [string](Get-Field (Get-Field $runs[0] 'machine') 'architecture')
$header = '| build | size MB |'
$rule = '| --- | ---: |'
foreach ($column in $present.Keys) { $header += " $column (median ms) |"; $rule += ' ---: |' }
$lines = @("### Publish settings compared: $architecture (baseline: $Baseline)", '', $header, $rule)
$table = @()
foreach ($run in $runs) {
    $label = [string](Get-Field $run 'label')
    $size = Get-Field (Get-Field $run 'app') 'sizeMB'
    $row = "| $label | $size |"
    $record = [ordered]@{ label = $label; sizeMB = $size }
    foreach ($column in $present.Keys) {
        $median = Get-Field (Get-Field (Get-Field $run 'metrics') $present[$column]) 'median'
        $baseMedian = Get-Field (Get-Field (Get-Field $base 'metrics') $present[$column]) 'median'
        $cell = ''
        if ($null -ne $median) {
            $cell = "$median"
            if ($null -ne $baseMedian -and [double]$baseMedian -gt 0 -and $label -ne $Baseline) {
                $change = 100.0 * ([double]$median - [double]$baseMedian) / [double]$baseMedian
                $cell += (' ({0:+0;-0;0}%)' -f $change)
            }
        }
        $row += " $cell |"
        $record[$present[$column]] = $median
    }
    $problem = Get-Field $run 'error'
    if ($problem) { $row += " error: $problem" }
    $lines += $row
    $table += $record
}
$markdown = ($lines -join "`n") + "`n"
Write-Host $markdown
if ($OutputPath) {
    [System.IO.File]::WriteAllText($OutputPath, $markdown, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText([System.IO.Path]::ChangeExtension($OutputPath, '.json'), ($table | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
}
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $markdown -Encoding UTF8 }
