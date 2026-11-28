<#
.SYNOPSIS
    Compares a run of run-perf.ps1 with windows\perf-budgets.json and fails (exit code 1) when a
    budget is exceeded.

.DESCRIPTION
    The budgets are per architecture (x64, arm64; taken from the results' machine.architecture).
    Each budget names a metric of results.json, the statistic to compare (median by default;
    p90, min, max or mean) and its maximum. A metric the run did not produce is reported as
    "not measured" and only fails the check when the budget says "required": true.
    See windows/docs/performance.md, "Budgets", for how the numbers were chosen and changed.

.PARAMETER Results
    results.json written by run-perf.ps1. Default: windows\artifacts\perf\results.json.

.PARAMETER Budgets
    The budgets file. Default: windows\perf-budgets.json.

.PARAMETER WarnOnly
    Report, but exit 0 even when a budget is exceeded.

.EXAMPLE
    .\check-budgets.ps1 -Results ..\..\artifacts\perf\results.json
#>
[CmdletBinding()]
param(
    [string]$Results,
    [string]$Budgets,
    [switch]$WarnOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$windowsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Results) { $Results = Join-Path $windowsRoot 'artifacts\perf\results.json' }
if (-not $Budgets) { $Budgets = Join-Path $windowsRoot 'perf-budgets.json' }

function Get-Field {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

$run = Get-Content -LiteralPath $Results -Raw -Encoding UTF8 | ConvertFrom-Json
$budgetFile = Get-Content -LiteralPath $Budgets -Raw -Encoding UTF8 | ConvertFrom-Json
$architecture = [string](Get-Field (Get-Field $run 'machine') 'architecture')
$budgets = Get-Field (Get-Field $budgetFile 'architectures') $architecture
if ($null -eq $budgets) { throw "perf-budgets.json has no budgets for '$architecture'." }
$metrics = Get-Field $run 'metrics'

$rows = @()
$exceeded = 0
$missing = 0
foreach ($budget in $budgets.PSObject.Properties) {
    # "metric#anything" is a second budget on the same metric (e.g. its p90 as well as its median).
    $name = ($budget.Name -split '#')[0]
    $limit = $budget.Value
    $statistic = [string](Get-Field $limit 'statistic')
    if (-not $statistic) { $statistic = 'median' }
    $max = [double](Get-Field $limit 'max')
    $required = (Get-Field $limit 'required') -eq $true
    $metric = Get-Field $metrics $name
    $value = Get-Field $metric $statistic
    if ($null -eq $value) {
        $status = 'not measured'
        if ($required) { $status = 'MISSING'; $missing++ }
        $rows += [pscustomobject]@{ Metric = $name; Statistic = $statistic; Value = ''; Budget = $max; Status = $status }
        continue
    }
    $value = [double]$value
    $status = 'ok'
    if ($value -gt $max) { $status = 'OVER'; $exceeded++ }
    $rows += [pscustomobject]@{ Metric = $name; Statistic = $statistic; Value = $value; Budget = $max; Status = $status }
}

$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

$lines = @("### Performance budgets: $architecture", '', '| metric | statistic | value | budget | status |', '| --- | --- | ---: | ---: | --- |')
foreach ($row in $rows) {
    $mark = $row.Status
    if ($row.Status -eq 'OVER' -or $row.Status -eq 'MISSING') { $mark = "**$($row.Status)**" }
    $lines += "| ``$($row.Metric)`` | $($row.Statistic) | $($row.Value) | $($row.Budget) | $mark |"
}
$summary = ($lines -join "`n") + "`n"
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding UTF8 }

foreach ($row in @($rows | Where-Object { $_.Status -eq 'OVER' })) {
    Write-Host "::error title=Performance budget exceeded ($architecture)::$($row.Metric) $($row.Statistic) is $($row.Value), budget $($row.Budget). See windows/docs/performance.md (Budgets)."
}
foreach ($row in @($rows | Where-Object { $_.Status -eq 'MISSING' })) {
    Write-Host "::error title=Performance metric missing ($architecture)::$($row.Metric) was not measured but its budget is required."
}

if ($exceeded + $missing -gt 0) {
    Write-Host "$exceeded budget(s) exceeded, $missing required metric(s) missing on $architecture."
    if (-not $WarnOnly) { exit 1 }
    exit 0
}
Write-Host "Every measured metric is within its budget on $architecture."
exit 0
