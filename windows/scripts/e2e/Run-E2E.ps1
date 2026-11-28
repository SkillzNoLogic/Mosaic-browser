<#
.SYNOPSIS
    Mosaic for Windows end-to-end suite: drives the published app through its automation pipe,
    area by area, and writes JUnit/JSON results, logs, screenshots and crash dumps.

.DESCRIPTION
    For each area (winui, shell, tabs, library, engine, downloads, privacy, passwords,
    productivity, platform, canvas, system, gallery, accessibility) a fresh Mosaic is started
    with --mosaic-automation on its own MOSAIC_DATA_DIR, the area's scenarios run against a
    local test web server, and Mosaic is quit and checked for left-over processes.

    Scenarios use the macOS automation vocabulary (Sources/Mosaic/**/*Automation*.swift). A
    command this build answers with "Unknown command", or a dump field it does not have yet,
    makes the scenario *pending*: reported as skipped with the reason, never as passed. So the
    suite is useful before an area has landed and fills in as areas merge into win-next.

    Crashes (Mosaic exiting, new dumps from Windows Error Reporting LocalDumps, WebView2's
    Crashpad or Gecko) and hangs (pipe time-outs with IsHungAppWindow; a dump is written) fail
    the scenario they happen in. Run Enable-CrashDumps.ps1 (as an administrator) first so WER
    writes dumps for Mosaic.exe, msedgewebview2.exe and firefox.exe.

    Output (OutputDirectory): results.json, junit.xml, summary.md, areas\<area>\ (logs, states,
    command outputs), screenshots\, gallery\ (index.html), accessibility\, dumps\.
    Exit code: 0 when nothing failed, 1 when a scenario failed, 2 when the suite itself broke.

.PARAMETER AppPath
    Mosaic.exe to test. Default: windows\publish\<this machine's rid>\Mosaic.exe.

.PARAMETER Areas
    Areas to run, in this order. Default: all.

.PARAMETER Tags
    Only scenarios with one of these tags (smoke = the subset run on ARM64).

.PARAMETER Name
    Only scenarios whose name contains this text.

.PARAMETER GeckoExe
    firefox.exe for the Mozilla engine. Default: %MOSAIC_GECKO_EXE%, the copy bootstrap.ps1
    installs, then a Firefox in Program Files.

.PARAMETER DumpDirectory
    Folder WER LocalDumps writes to (Enable-CrashDumps.ps1). Default: %MOSAIC_E2E_DUMPS%, else
    C:\MosaicCrashDumps if it exists.

.PARAMETER SelfTest
    Tests the framework itself against selftest\FakeMosaic.ps1 (a stand-in for the app that
    speaks the automation protocol, hangs and crashes on request). No build needed.

.EXAMPLE
    .\Run-E2E.ps1
.EXAMPLE
    .\Run-E2E.ps1 -Areas winui,tabs -AppPath C:\Mosaic\Mosaic.exe
.EXAMPLE
    .\Run-E2E.ps1 -Tags smoke
#>
[CmdletBinding()]
param(
    [string]$AppPath,
    [string]$OutputDirectory,
    [string[]]$Areas = @(),
    [string[]]$Tags = @(),
    [string]$Name = '',
    [string]$GeckoExe,
    [ValidateSet('child', 'owned')] [string]$GeckoHostMode = 'child',
    [string]$DumpDirectory,
    [ValidateRange(5, 600)] [int]$WaitSeconds = 30,
    [ValidateRange(10, 600)] [int]$LoadSeconds = 90,
    [ValidateRange(10, 600)] [int]$StartSeconds = 60,
    [ValidateRange(1, 600)] [int]$BudgetMinutes = 75,
    [switch]$SelfTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$e2eRoot = $PSScriptRoot
$windowsRoot = Split-Path -Parent (Split-Path -Parent $e2eRoot)
Import-Module (Join-Path $e2eRoot 'lib\E2E.psm1') -Force
Import-Module (Join-Path $e2eRoot 'lib\Accessibility.psm1') -Force
. (Join-Path $e2eRoot 'lib\Fixtures.ps1')

$AllAreas = @('winui', 'shell', 'tabs', 'library', 'engine', 'downloads', 'privacy', 'passwords',
    'productivity', 'platform', 'canvas', 'system', 'gallery', 'accessibility')

function Resolve-App {
    if ($SelfTest) { return (Join-Path $e2eRoot 'selftest\FakeMosaic.ps1') }
    if ($AppPath) {
        if (-not (Test-Path -LiteralPath $AppPath)) { throw "Mosaic.exe not found at '$AppPath'." }
        return (Resolve-Path -LiteralPath $AppPath).Path
    }
    $rid = 'win-x64'
    $native = $env:PROCESSOR_ARCHITEW6432
    if (-not $native) { $native = $env:PROCESSOR_ARCHITECTURE }
    if ($native -eq 'ARM64') { $rid = 'win-arm64' }
    $default = Join-Path $windowsRoot "publish\$rid\Mosaic.exe"
    if (-not (Test-Path -LiteralPath $default)) { throw "No published app at '$default'. Run scripts\build.ps1 first, or pass -AppPath." }
    $default
}

function Resolve-Gecko {
    if ($SelfTest) { return $null }
    $candidates = @()
    if ($GeckoExe) { $candidates += $GeckoExe }
    elseif ($env:MOSAIC_GECKO_EXE) { $candidates += $env:MOSAIC_GECKO_EXE }
    else {
        if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Mosaic\Engines\Firefox\firefox.exe') }
        if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Mozilla Firefox\firefox.exe') }
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    Write-Warning "No Gecko engine found (looked at: $($candidates -join '; ')); the Mozilla-engine checks will fail."
    $null
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $windowsRoot 'artifacts\e2e'
    if ($SelfTest) { $OutputDirectory = Join-Path $windowsRoot 'artifacts\e2e-selftest' }
}
if (-not $DumpDirectory) {
    if ($env:MOSAIC_E2E_DUMPS) { $DumpDirectory = $env:MOSAIC_E2E_DUMPS }
    elseif (Test-Path -LiteralPath 'C:\MosaicCrashDumps') { $DumpDirectory = 'C:\MosaicCrashDumps' }
}

$exitCode = 2
try {
    $app = Resolve-App
    $gecko = Resolve-Gecko
    $config = Initialize-E2E -AppPath $app -OutputDirectory $OutputDirectory -GeckoExe $gecko -GeckoHostMode $GeckoHostMode `
        -WaitSeconds $WaitSeconds -LoadSeconds $LoadSeconds -StartSeconds $StartSeconds -BudgetMinutes $BudgetMinutes -DumpDirectory $DumpDirectory
    Write-Host "==> Mosaic:  $app"
    Write-Host "    Gecko:   $gecko"
    Write-Host "    Output:  $($config.OutputDirectory)"
    Write-Host "    Dumps:   $DumpDirectory"

    if ($SelfTest) {
        . (Join-Path $e2eRoot 'selftest\SelfTest.Scenarios.ps1')
        . (Join-Path $e2eRoot 'scenarios\gallery.ps1')
        $runAreas = @('selftest', 'selftest-crash', 'gallery')
        if ($Areas.Count -gt 0) { $runAreas = $Areas }
    }
    else {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $e2eRoot 'scenarios') -Filter '*.ps1' | Sort-Object Name) {
            . $file.FullName
        }
        $runAreas = $AllAreas
        if ($Areas.Count -gt 0) { $runAreas = $Areas }
    }
    Write-Host "    $(@(Get-Scenarios).Count) scenarios registered; areas: $($runAreas -join ', ')"

    Invoke-E2ESuite -Areas $runAreas -Tags $Tags -NameFilter $Name

    $info = [ordered]@{
        'App'      = $app
        'Machine'  = "$env:COMPUTERNAME, $([System.Environment]::OSVersion.VersionString), $env:PROCESSOR_ARCHITECTURE"
        'Commit'   = "$env:GITHUB_SHA"
        'Duration' = ('{0:0.0} min' -f ((Get-Date) - $config.Started).TotalMinutes)
    }
    $title = 'Mosaic end-to-end'
    if ($SelfTest) { $title = 'Mosaic end-to-end framework self-test' }
    $counts = Write-E2EReports -Title $title -Info $info
    Write-Host ''
    Write-Host "==> $($counts.Passed) passed, $($counts.Failed) failed, $($counts.Skipped) pending/skipped (of $($counts.Total))"
    Write-Host "    $(Join-Path $config.OutputDirectory 'summary.md')"
    if ($env:GITHUB_STEP_SUMMARY) {
        Get-Content -LiteralPath (Join-Path $config.OutputDirectory 'summary.md') | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }
    if ($counts.Failed -gt 0) { $exitCode = 1 } else { $exitCode = 0 }
}
catch {
    Write-Host "E2E SUITE ERROR: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    $exitCode = 2
}
finally {
    Stop-E2E
}
exit $exitCode
