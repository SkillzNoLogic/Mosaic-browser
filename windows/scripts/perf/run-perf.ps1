<#
.SYNOPSIS
    Measures a published Mosaic: start-up time, time to first page, memory of the whole process
    tree (Mosaic, WebView2, Gecko), idle CPU, memory with N tabs in each engine and what
    sleeping tabs give back. Writes results.json and summary.md.

.DESCRIPTION
    Every launch gets its own data folder (MOSAIC_DATA_DIR), the automation pipe
    (--mosaic-automation) and the start-up trace file (MOSAIC_STARTUP_TRACE, written by the app's
    StartupTrace). Pages come from a local web server, so nothing depends on the Internet.

    Start-up scenarios (each iteration is one launch, ended with "quit"):
      firstRun      an empty data folder: the very first launch on a PC
      cold          an existing data folder, with Windows' file cache emptied first (as after a
                    reboot; needs an elevated shell, which CI runners are)
      warm          an existing data folder, files cached: an everyday launch
      warmChromium  as warm, with Chromium as the engine for new tabs
    Each launch records the time from process creation to Mosaic's window being shown (visible
    and no longer cloaked by DWM, measured from outside the app), every StartupTrace mark
    (main, windowActivated, firstFrame, modelReady, geckoReady, firstNavigation...), the time
    to the first page and how long "quit" takes.

    Memory scenarios (each iteration is one launch per engine):
      one page loaded, settled -> memory; then idle CPU over -IdleSeconds; then -Tabs tabs of the
      test page -> memory; then the tabs area's "unloadBackground" (sleeping tabs) -> memory.
    Memory is the private working set (Task Manager's "Memory" column) and the commit of every
    process of the run, grouped as mosaic / webview2 / gecko / other.

    Times are in milliseconds, memory in MB. Each metric gets n, median, p90, min, max, mean
    and the samples. Check them against budgets with check-budgets.ps1.

.PARAMETER AppPath
    Mosaic.exe to measure. Default: windows\publish\<this machine's rid>\Mosaic.exe.

.PARAMETER OutputDirectory
    Where results.json, summary.md and the per-launch traces go. Emptied first.
    Default: windows\artifacts\perf.

.PARAMETER GeckoExe
    firefox.exe for the Gecko engine. Default: %MOSAIC_GECKO_EXE%, then bootstrap.ps1's copy,
    then a Firefox in Program Files. Without one, Gecko is not measured (noted in the results).

.PARAMETER Label
    A name for this build, stored in the results (e.g. "r2r" when comparing publish settings).

.PARAMETER Scenario
    startup, memory or both (default).

.EXAMPLE
    .\run-perf.ps1
.EXAMPLE
    .\run-perf.ps1 -AppPath C:\Mosaic\Mosaic.exe -WarmIterations 10 -Scenario startup
#>
[CmdletBinding()]
param(
    [string]$AppPath,
    [string]$OutputDirectory,
    [string]$GeckoExe,
    [string]$Label = 'default',
    [ValidateSet('startup', 'memory')]
    [string[]]$Scenario = @('startup', 'memory'),
    [ValidateRange(0, 50)] [int]$FirstRunIterations = 3,
    [ValidateRange(0, 50)] [int]$ColdIterations = 3,
    [ValidateRange(0, 50)] [int]$WarmIterations = 5,
    [ValidateRange(0, 10)] [int]$MemoryIterations = 2,
    [ValidateRange(5, 600)] [int]$IdleSeconds = 30,
    [ValidateRange(2, 20)] [int]$Tabs = 10,
    [ValidateRange(0, 120)] [int]$SettleSeconds = 10
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'PerfHarness.psm1') -Force
Initialize-PerfNative

$windowsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# ---- inputs ------------------------------------------------------------------------------------

if (-not $AppPath) {
    $rid = 'win-' + (Get-NativeArchitecture)
    $AppPath = Join-Path $windowsRoot "publish\$rid\Mosaic.exe"
}
if (-not (Test-Path -LiteralPath $AppPath)) { throw "Mosaic.exe not found at '$AppPath'. Run scripts\build.ps1 first, or pass -AppPath." }
$AppPath = (Resolve-Path -LiteralPath $AppPath).Path

if (-not $GeckoExe) {
    $candidates = @()
    if ($env:MOSAIC_GECKO_EXE) { $candidates += $env:MOSAIC_GECKO_EXE }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Mosaic\Engines\Firefox\firefox.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Mozilla Firefox\firefox.exe') }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { $GeckoExe = (Resolve-Path -LiteralPath $candidate).Path; break }
    }
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $windowsRoot 'artifacts\perf' }
if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$traces = Join-Path $OutputDirectory 'traces'
$dataRoot = Join-Path $OutputDirectory 'data'
New-Item -ItemType Directory -Path $traces, $dataRoot -Force | Out-Null

# The pipe name is fixed, so a second automated Mosaic would be driven by mistake.
$other = $null
try { $other = Send-MosaicCommand -Do 'dump' -ConnectTimeoutMilliseconds 300 -ReplyTimeoutSeconds 5 } catch { }
if ($other) { throw 'Another Mosaic is already running with --mosaic-automation. Quit it first.' }

$results = [ordered]@{
    schema     = 1
    label      = $Label
    startedUtc = [DateTime]::UtcNow.ToString('o')
    machine    = Get-PerfMachineInfo -AppPath $AppPath -GeckoExe $GeckoExe
    app        = Get-PerfAppInfo -AppPath $AppPath
    settings   = [ordered]@{
        firstRunIterations = $FirstRunIterations; coldIterations = $ColdIterations; warmIterations = $WarmIterations
        memoryIterations = $MemoryIterations; idleSeconds = $IdleSeconds; tabs = $Tabs; settleSeconds = $SettleSeconds
        geckoExe = $GeckoExe
    }
    metrics    = [ordered]@{}
    runs       = @()
    notes      = @()
}
$script:AllRuns = New-Object System.Collections.ArrayList
$script:AutomationMissing = $false
$script:FailuresInARow = 0
$runStarted = Get-Date
$script:Notes = New-Object System.Collections.ArrayList

function Add-Note {
    param([string]$Text)
    if (-not $script:Notes.Contains($Text)) {
        [void]$script:Notes.Add($Text)
        Write-Host "    note: $Text"
    }
}

Write-Host "==> Mosaic $($results.app.version) ($($results.app.architecture), $($results.app.sizeMB) MB in $($results.app.files) files)"
Write-Host "    $AppPath"
Write-Host "    machine: $($results.machine.cpu), $($results.machine.logicalProcessors) logical processors, $($results.machine.memoryGB) GB, $($results.machine.os) $($results.machine.osBuild) ($($results.machine.architecture))"
Write-Host "    WebView2 $($results.machine.webView2); Gecko: $(if ($GeckoExe) { $GeckoExe } else { 'none' })"
if (-not $GeckoExe) { Add-Note 'No Gecko engine was found; Gecko pages are not measured.' }
if ($results.app.architecture -ne $results.machine.architecture) {
    Add-Note "Mosaic is built for $($results.app.architecture) but this machine is $($results.machine.architecture) (emulated): the numbers are not representative."
}

$server = Start-PerfPageServer
$pageUrl = Get-PerfPageUrl $server 1

# ---- start-up ----------------------------------------------------------------------------------

function Invoke-StartupLaunch {
    param([string]$Name, [int]$Iteration, [string]$DataDirectory, [switch]$Cold)
    $run = [ordered]@{ kind = 'startup'; scenario = $Name; iteration = $Iteration }
    $marks = [ordered]@{}
    $source = $null
    if ($Cold) {
        $problem = [MosaicPerf.Native]::PurgeFileCache()
        $run['fileCachePurged'] = ($null -eq $problem)
        if ($problem) { Add-Note "The file cache could not be emptied, so 'cold' launches ran with it warm: $problem" }
    }
    $launch = Start-PerfMosaic -AppPath $AppPath -DataDirectory $DataDirectory -GeckoExe $GeckoExe `
        -TraceFile (Join-Path $traces "$Name-$Iteration.json") -ExtraArguments @($pageUrl)
    try {
        $run['windowShownMs'] = Wait-PerfWindow $launch 60
        if ($null -eq $run['windowShownMs']) { Add-Note "No window was shown within 60 s ($Name $Iteration)." }
        # Once a launch has shown that this build has no automation pipe, the others do not
        # wait the full time for it.
        $pipeWait = 20
        if ($script:AutomationMissing) { $pipeWait = 3 }
        $automation = Wait-PerfAutomation $launch $pipeWait
        if (-not $automation) { $script:AutomationMissing = $true }
        $run['automation'] = $automation
        if (-not $automation) { Add-Note 'The automation pipe did not answer; only window and trace times are measured.' }

        # The first page: the address given on the command line if Mosaic opens it, otherwise
        # asked for over automation as soon as it can be. It counts as loaded when the trace
        # has firstNavigationCompleted or, for a build without those marks, when dump shows the
        # page loaded (timed from outside, polled every 100 ms).
        $wait = 2
        if ($automation) { $wait = 3 }
        $navigateSentMs = $null
        $externalLoadedMs = $null
        if (Wait-PerfMark $launch @('firstNavigationStarted') $wait) { $source = 'commandLine' }
        elseif ($automation -and (Test-PerfPageLoaded (Get-PerfState $launch) $server.Base 1)) { $source = 'commandLine' }
        elseif ($automation) {
            $navigateSentMs = Get-PerfSinceStart $launch
            Invoke-PerfCommand -Do 'navigate' -Value $pageUrl | Out-Null
            $source = 'automation'
        }
        $run['firstPageSource'] = $source
        if ($source) {
            $deadline = (Get-Date).AddSeconds(90)
            $done = $false
            while (-not $done) {
                if ($null -ne (Get-PerfMark (Read-PerfTrace $launch.TraceFile) 'firstNavigationCompleted')) { $done = $true; break }
                if ($automation -and (Test-PerfPageLoaded (Get-PerfState $launch) $server.Base 1)) {
                    $externalLoadedMs = Get-PerfSinceStart $launch
                    # A build with StartupTrace marks it a moment later; use the mark when it comes.
                    Wait-PerfMark $launch @('firstNavigationCompleted') 2 | Out-Null
                    $done = $true
                    break
                }
                if ((Get-Date) -ge $deadline) { break }
                Start-Sleep -Milliseconds 100
            }
            if (-not $done) { Add-Note "The first page did not finish loading within 90 s ($Name $Iteration)." }
        }
        # Marks that land a moment later (engines finishing their start).
        if ($automation) { Wait-PerfMark $launch @('geckoReady') 3 | Out-Null }
        else { Start-Sleep -Milliseconds 500 }
        $trace = Read-PerfTrace $launch.TraceFile
        if ($trace) {
            foreach ($property in (Get-MosaicProperty $trace 'marks').PSObject.Properties) { $marks[$property.Name] = [double]$property.Value }
            $run['notes'] = Get-MosaicProperty $trace 'notes'
        }
        else {
            Add-Note 'Mosaic wrote no start-up trace (StartupTrace / MOSAIC_STARTUP_TRACE): in-app marks are missing.'
        }
        $run['marks'] = $marks
        $started = $null; $completed = $null
        if ($marks.Contains('firstNavigationStarted')) { $started = $marks['firstNavigationStarted'] }
        if ($marks.Contains('firstNavigationCompleted')) { $completed = $marks['firstNavigationCompleted'] }
        if ($null -ne $completed -and $null -ne $started) { $run['pageLoadMs'] = [Math]::Round($completed - $started, 1) }
        elseif ($null -ne $externalLoadedMs -and $null -ne $navigateSentMs) {
            $run['pageLoadMs'] = [Math]::Round($externalLoadedMs - $navigateSentMs, 1)
            $run['pageLoadMeasured'] = 'outside'
        }
        if ($source -eq 'commandLine') {
            if ($null -ne $completed) { $run['firstPageMs'] = $completed }
            elseif ($null -ne $externalLoadedMs) { $run['firstPageMs'] = [Math]::Round($externalLoadedMs, 1); $run['firstPageMeasured'] = 'outside' }
        }
    }
    finally {
        $stop = Stop-PerfMosaic $launch
        $run['exitMs'] = $stop.exitMs
        $run['cleanExit'] = $stop.cleanExit
        if ($stop.leftovers.Count -gt 0) {
            $run['leftovers'] = $stop.leftovers
            Add-Note "Processes were left running after Mosaic quit: $($stop.leftovers -join ', ')"
        }
    }
    $line = "    $Name #$Iteration window {0} ms" -f $run['windowShownMs']
    foreach ($key in @('firstFrame', 'geckoReady')) { if ($marks.Contains($key)) { $line += ", $key $($marks[$key]) ms" } }
    if ($run.Contains('firstPageMs')) { $line += ", first page $($run['firstPageMs']) ms" }
    if ($run.Contains('pageLoadMs')) { $line += " (load $($run['pageLoadMs']) ms, $source)" }
    Write-Host $line
    [void]$script:AllRuns.Add($run)
    Start-Sleep -Seconds 2   # let the engines' last writes and exits finish
}

function Invoke-Launch {
    # Runs one launch; a launch that fails (Mosaic crashing, a timeout) is recorded and the
    # run goes on, unless three in a row fail.
    param([string]$Name, [int]$Iteration, [scriptblock]$Body)
    try {
        & $Body
        $script:FailuresInARow = 0
    }
    catch {
        $script:FailuresInARow++
        $message = $_.Exception.Message
        Write-Host "    $Name #$Iteration FAILED: $message"
        [void]$script:AllRuns.Add([ordered]@{ kind = 'failed'; scenario = $Name; iteration = $Iteration; error = $message })
        Add-Note "Launches failed: $message"
        if ($script:FailuresInARow -ge 3) { throw "Three launches in a row failed; the last one: $message" }
    }
}

function New-SeedData {
    # A data folder that has been through one launch (profiles created), optionally with
    # Chromium chosen as the engine for new tabs.
    param([string]$Name, [string]$Engine)
    $directory = Join-Path $dataRoot $Name
    $launch = Start-PerfMosaic -AppPath $AppPath -DataDirectory $directory -GeckoExe $GeckoExe -TraceFile (Join-Path $traces "seed-$Name.json")
    try {
        Wait-PerfWindow $launch 60 | Out-Null
        $pipeWait = 20
        if ($script:AutomationMissing) { $pipeWait = 3 }
        if (Wait-PerfAutomation $launch $pipeWait) {
            if ($Engine) { Invoke-PerfCommand -Do 'engine' -Value $Engine | Out-Null }
            Invoke-PerfCommand -Do 'navigate' -Value $pageUrl | Out-Null
            Start-Sleep -Seconds 5
        }
        else {
            Start-Sleep -Seconds 5
        }
    }
    catch {
        Add-Note "Preparing the data folder '$Name' failed: $($_.Exception.Message)"
    }
    finally { Stop-PerfMosaic $launch | Out-Null }
    Start-Sleep -Seconds 2
    $directory
}

$failure = $null
try {
    if ($Scenario -contains 'startup') {
        Write-Host '==> Start-up'
        for ($i = 1; $i -le $FirstRunIterations; $i++) {
            $directory = Join-Path $dataRoot "first-$i"
            Invoke-Launch 'firstRun' $i { Invoke-StartupLaunch -Name 'firstRun' -Iteration $i -DataDirectory $directory }
            Remove-PerfData $directory
        }
        if ($ColdIterations + $WarmIterations -gt 0) {
            $seed = New-SeedData -Name 'seed-gecko'
            for ($i = 1; $i -le $ColdIterations; $i++) { Invoke-Launch 'cold' $i { Invoke-StartupLaunch -Name 'cold' -Iteration $i -DataDirectory $seed -Cold } }
            for ($i = 1; $i -le $WarmIterations; $i++) { Invoke-Launch 'warm' $i { Invoke-StartupLaunch -Name 'warm' -Iteration $i -DataDirectory $seed } }
            Remove-PerfData $seed
        }
        $chromiumUsable = @($script:AllRuns | Where-Object { $_['automation'] }).Count -gt 0 -and $results.machine.webView2
        if ($WarmIterations -gt 0 -and $chromiumUsable) {
            $seed = New-SeedData -Name 'seed-chromium' -Engine 'chromium'
            for ($i = 1; $i -le $WarmIterations; $i++) { Invoke-Launch 'warmChromium' $i { Invoke-StartupLaunch -Name 'warmChromium' -Iteration $i -DataDirectory $seed } }
            Remove-PerfData $seed
        }
    }

    if ($Scenario -contains 'memory') {
        Write-Host '==> Memory and idle CPU'
        $engines = @('chromium')
        if ($GeckoExe) { $engines = @('gecko', 'chromium') }
        for ($i = 1; $i -le $MemoryIterations; $i++) {
            foreach ($engine in $engines) {
                $run = [ordered]@{ kind = 'memory'; scenario = $engine; iteration = $i }
                $directory = Join-Path $dataRoot "memory-$engine-$i"
                $launch = Start-PerfMosaic -AppPath $AppPath -DataDirectory $directory -GeckoExe $GeckoExe -TraceFile (Join-Path $traces "memory-$engine-$i.json")
                try {
                    Wait-PerfWindow $launch 60 | Out-Null
                    $pipeWait = 20
                    if ($script:AutomationMissing) { $pipeWait = 3 }
                    if (-not (Wait-PerfAutomation $launch $pipeWait)) {
                        $script:AutomationMissing = $true
                        # Without automation only the bare app can be measured.
                        $run['scenario'] = 'noAutomation'
                        Start-Sleep -Seconds $SettleSeconds
                        $run['oneTab'] = Measure-PerfMemorySettled $launch
                        $run['idleCpu'] = Measure-PerfCpu $launch $IdleSeconds
                        Add-Note 'Memory was measured without automation (the bare app, no pages).'
                        Write-Host ("    no automation #{0}: {1} MB private working set, idle CPU {2}% of one core" -f $i,
                            $run['oneTab'].groups.total.privateWorkingSetMB, $run['idleCpu'].percentOfOneCore)
                        continue
                    }
                    $state = Get-PerfState $launch
                    if ($engine -eq 'chromium' -and (Get-MosaicProperty $state 'chromiumAvailable') -ne $true) {
                        Add-Note 'Chromium (WebView2) is unavailable; Chromium memory is not measured.'
                        continue
                    }
                    Invoke-PerfCommand -Do 'engine' -Value $engine | Out-Null
                    Invoke-PerfCommand -Do 'navigate' -Value $pageUrl | Out-Null
                    $base = $server.Base
                    Wait-PerfState $launch "the first page to load in $engine" { param($s) Test-PerfPageLoaded $s $base 1 } | Out-Null
                    Start-Sleep -Seconds $SettleSeconds
                    $run['oneTab'] = Measure-PerfMemorySettled $launch
                    $run['appReported'] = Get-MosaicProperty (Get-PerfState $launch) 'performance'
                    $run['idleCpu'] = Measure-PerfCpu $launch $IdleSeconds

                    for ($tab = 2; $tab -le $Tabs; $tab++) {
                        Invoke-PerfCommand -Do 'newTab' -Value (Get-PerfPageUrl $server $tab) | Out-Null
                    }
                    Wait-PerfState $launch "$Tabs pages to load in $engine" { param($s) Test-PerfPageLoaded $s $base $Tabs } -TimeoutSeconds 180 | Out-Null
                    Start-Sleep -Seconds $SettleSeconds
                    $run['tabs'] = Measure-PerfMemorySettled $launch

                    $sleep = Send-MosaicCommand -Do 'unloadBackground'
                    if ((Get-MosaicProperty $sleep.Reply 'ok') -eq $true) {
                        Start-Sleep -Seconds $SettleSeconds
                        $run['asleep'] = Measure-PerfMemorySettled $launch
                    }
                    else {
                        Add-Note "Sleeping tabs could not be measured: 'unloadBackground' was refused ($(Get-MosaicProperty $sleep.Reply 'error'))."
                    }
                }
                finally {
                    $stop = Stop-PerfMosaic $launch
                    $run['exitMs'] = $stop.exitMs
                    if ($stop.leftovers.Count -gt 0) {
                        $run['leftovers'] = $stop.leftovers
                        Add-Note "Processes were left running after Mosaic quit: $($stop.leftovers -join ', ')"
                    }
                    [void]$script:AllRuns.Add($run)
                    Remove-PerfData $directory
                }
                $line = "    $engine #$i"
                if ($run.Contains('oneTab')) { $line += ": 1 page $($run['oneTab'].groups.total.privateWorkingSetMB) MB" }
                if ($run.Contains('idleCpu')) { $line += ", idle CPU $($run['idleCpu'].percentOfOneCore)% of one core" }
                if ($run.Contains('tabs')) { $line += ", $Tabs pages $($run['tabs'].groups.total.privateWorkingSetMB) MB" }
                if ($run.Contains('asleep')) { $line += ", asleep $($run['asleep'].groups.total.privateWorkingSetMB) MB" }
                Write-Host $line
                Start-Sleep -Seconds 2
            }
        }
    }
}
catch {
    $failure = $_
    Add-Note "The run stopped early: $($_.Exception.Message)"
}
finally {
    Stop-PerfPageServer $server
}

# ---- aggregate ---------------------------------------------------------------------------------

$metrics = $results.metrics
function Add-Metric {
    param([string]$Name, [object[]]$Values)
    $stats = Get-PerfStats $Values
    if ($null -ne $stats) { $metrics[$Name] = $stats }
}

$startupRuns = @($script:AllRuns | Where-Object { $_['kind'] -eq 'startup' })
foreach ($name in @('firstRun', 'cold', 'warm', 'warmChromium')) {
    $scenarioRuns = @($startupRuns | Where-Object { $_['scenario'] -eq $name })
    if ($scenarioRuns.Count -eq 0) { continue }
    foreach ($field in @('windowShownMs', 'firstPageMs', 'pageLoadMs', 'exitMs')) {
        Add-Metric "startup.$name.$field" @($scenarioRuns | ForEach-Object { if ($_.Contains($field)) { $_[$field] } })
    }
    $markNames = @($scenarioRuns | ForEach-Object { @($_['marks'].Keys) } | Select-Object -Unique)
    foreach ($mark in $markNames) {
        Add-Metric "startup.$name.mark.$mark" @($scenarioRuns | ForEach-Object { if ($_['marks'].Contains($mark)) { $_['marks'][$mark] } })
    }
}

$memoryRuns = @($script:AllRuns | Where-Object { $_['kind'] -eq 'memory' })
foreach ($engine in @('gecko', 'chromium', 'noAutomation')) {
    $scenarioRuns = @($memoryRuns | Where-Object { $_['scenario'] -eq $engine })
    if ($scenarioRuns.Count -eq 0) { continue }
    foreach ($phase in @('oneTab', 'tabs', 'asleep')) {
        $measured = @($scenarioRuns | Where-Object { $_.Contains($phase) })
        if ($measured.Count -eq 0) { continue }
        $phaseName = $phase
        if ($phase -eq 'tabs') { $phaseName = "tabs$Tabs" }
        foreach ($group in @('total', 'mosaic', 'webview2', 'gecko')) {
            Add-Metric "memory.$engine.$phaseName.$group.privateWorkingSetMB" @($measured | ForEach-Object { $_[$phase].groups[$group].privateWorkingSetMB })
            Add-Metric "memory.$engine.$phaseName.$group.commitMB" @($measured | ForEach-Object { $_[$phase].groups[$group].commitMB })
        }
        Add-Metric "memory.$engine.$phaseName.processes" @($measured | ForEach-Object { $_[$phase].groups.total.processes })
    }
    Add-Metric "memory.$engine.perTabMB" @($scenarioRuns | Where-Object { $_.Contains('tabs') -and $_.Contains('oneTab') } | ForEach-Object {
            ($_['tabs'].groups.total.privateWorkingSetMB - $_['oneTab'].groups.total.privateWorkingSetMB) / ($Tabs - 1) })
    Add-Metric "memory.$engine.sleepSavedMB" @($scenarioRuns | Where-Object { $_.Contains('tabs') -and $_.Contains('asleep') } | ForEach-Object {
            $_['tabs'].groups.total.privateWorkingSetMB - $_['asleep'].groups.total.privateWorkingSetMB })
    Add-Metric "cpu.$engine.idle.percentOfOneCore" @($scenarioRuns | Where-Object { $_.Contains('idleCpu') } | ForEach-Object { $_['idleCpu'].percentOfOneCore })
    foreach ($group in @('mosaic', 'webview2', 'gecko', 'other')) {
        Add-Metric "cpu.$engine.idle.$group.percentOfOneCore" @($scenarioRuns | Where-Object { $_.Contains('idleCpu') } | ForEach-Object { $_['idleCpu'].groups[$group] })
    }
}

$results.runs = @($script:AllRuns)
$results.notes = @($script:Notes)
$results['finishedUtc'] = [DateTime]::UtcNow.ToString('o')
if ($failure) { $results['error'] = $failure.Exception.Message }

# Crashes show up in the Application event log: unhandled .NET exceptions and native faults.
try {
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $runStarted; Level = 1, 2 } -ErrorAction Stop |
            Where-Object { $_.ProviderName -eq '.NET Runtime' -or $_.ProviderName -eq 'Application Error' -or $_.ProviderName -eq 'Windows Error Reporting' })
    if ($events.Count -gt 0) {
        Write-PerfText (Join-Path $OutputDirectory 'events.txt') ($events | Format-List TimeCreated, ProviderName, Id, Message | Out-String -Width 4096)
        Add-Note "$($events.Count) error event(s) in the Application log during the run (events.txt)."
        $results['crashEvents'] = @($events | Select-Object -First 5 | ForEach-Object { $_.Message.Substring(0, [Math]::Min(1500, $_.Message.Length)) })
    }
}
catch { }

$resultsPath = Join-Path $OutputDirectory 'results.json'
Write-PerfText $resultsPath (ConvertTo-PerfJson $results)

# ---- summary -----------------------------------------------------------------------------------

$lines = @(
    "### Mosaic performance: $($results.machine.architecture) ($Label)",
    '',
    "$($results.machine.cpu), $($results.machine.logicalProcessors) logical processors, $($results.machine.memoryGB) GB; $($results.machine.os) build $($results.machine.osBuild); WebView2 $($results.machine.webView2); Gecko $(Get-MosaicProperty ([pscustomobject]$results.machine) 'gecko'). App: $($results.app.sizeMB) MB, $($results.app.files) files.",
    '',
    '| metric | median | p90 | min | max | n |',
    '| --- | ---: | ---: | ---: | ---: | ---: |'
)
foreach ($name in $metrics.Keys) {
    $m = $metrics[$name]
    $lines += "| ``$name`` | $($m.median) | $($m.p90) | $($m.min) | $($m.max) | $($m.n) |"
}
if ($script:Notes.Count -gt 0) {
    $lines += ''
    foreach ($note in $script:Notes) { $lines += "- $note" }
}
$summary = ($lines -join "`n") + "`n"
Write-PerfText (Join-Path $OutputDirectory 'summary.md') $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding UTF8 }

Remove-PerfData $dataRoot
Write-Host ''
Write-Host $summary
Write-Host "Results: $resultsPath"
if ($failure) {
    Write-Host "PERF RUN FAILED: $($failure.Exception.Message)"
    exit 1
}
exit 0
