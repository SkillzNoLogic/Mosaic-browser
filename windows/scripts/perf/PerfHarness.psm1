# Shared code of Mosaic's performance harness (windows/scripts/perf). Windows PowerShell 5.1
# compatible, no dependencies beyond Windows itself. See run-perf.ps1 for what is measured and
# windows/docs/performance.md for the method.

Set-StrictMode -Version 2.0

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'MosaicAutomation.psm1') -Force -Global

$script:Utf8 = New-Object System.Text.UTF8Encoding($false)

function Initialize-PerfNative {
    if (-not ('MosaicPerf.Native' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'PerfNative.cs')
    }
}

function Write-PerfText {
    param([Parameter(Mandatory = $true)] [string]$Path, [AllowEmptyString()] [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, $script:Utf8)
}

function ConvertTo-PerfJson {
    param([Parameter(Mandatory = $true)] $Value)
    $Value | ConvertTo-Json -Depth 12
}

# ---- statistics --------------------------------------------------------------------------------

function Get-PerfStats {
    <#
    .SYNOPSIS
        Median, p90 (nearest rank), min, max and mean of some numbers, with the samples.
        Returns $null for no numbers.
    #>
    param([AllowEmptyCollection()] [object[]]$Values)
    $numbers = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($numbers.Count -eq 0) { return $null }
    $n = $numbers.Count
    if ($n % 2 -eq 1) { $median = $numbers[($n - 1) / 2] }
    else { $median = ($numbers[$n / 2 - 1] + $numbers[$n / 2]) / 2 }
    $p90 = $numbers[[int][Math]::Ceiling(0.9 * $n) - 1]
    $sum = 0.0
    foreach ($number in $numbers) { $sum += $number }
    [ordered]@{
        n       = $n
        median  = [Math]::Round($median, 1)
        p90     = [Math]::Round($p90, 1)
        min     = [Math]::Round($numbers[0], 1)
        max     = [Math]::Round($numbers[$n - 1], 1)
        mean    = [Math]::Round($sum / $n, 1)
        samples = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [Math]::Round([double]$_, 1) })
    }
}

# ---- the machine -------------------------------------------------------------------------------

function Get-NativeArchitecture {
    $native = $env:PROCESSOR_ARCHITEW6432
    if (-not $native) { $native = $env:PROCESSOR_ARCHITECTURE }
    try {
        $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
        $native = (Get-ItemProperty -LiteralPath $key -Name PROCESSOR_ARCHITECTURE).PROCESSOR_ARCHITECTURE
    }
    catch { }
    if ($native -eq 'ARM64') { return 'arm64' }
    'x64'
}

function Get-PeMachine {
    # The architecture an executable was built for, from its PE header.
    param([Parameter(Mandatory = $true)] [string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32() + 4
        switch ($reader.ReadUInt16()) {
            0x8664 { 'x64' }
            0xAA64 { 'arm64' }
            0x014C { 'x86' }
            default { 'unknown' }
        }
    }
    finally { $stream.Dispose() }
}

function Get-WebView2Version {
    $client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    foreach ($key in @(
            "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$client",
            "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\$client",
            "Registry::HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\$client")) {
        $entry = Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue
        if ($entry -and $entry.pv -and $entry.pv -ne '0.0.0.0') { return [string]$entry.pv }
    }
    $null
}

function Get-PerfMachineInfo {
    param([string]$AppPath, [string]$GeckoExe)
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $cpu = @(Get-CimInstance -ClassName Win32_Processor) | Select-Object -First 1
    $info = [ordered]@{
        architecture      = Get-NativeArchitecture
        os                = [string]$os.Caption
        osVersion         = [string]$os.Version
        osBuild           = [string]$os.BuildNumber
        cpu               = ([string]$cpu.Name).Trim()
        cores             = [int]$cpu.NumberOfCores
        logicalProcessors = [int]$cpu.NumberOfLogicalProcessors
        cpuMaxClockMHz    = [int]$cpu.MaxClockSpeed
        memoryGB          = [Math]::Round([double]$os.TotalVisibleMemorySize / 1MB, 1)
        freeMemoryGB      = [Math]::Round([double]$os.FreePhysicalMemory / 1MB, 1)
        webView2          = Get-WebView2Version
        powershell        = $PSVersionTable.PSVersion.ToString()
        runner            = $env:RUNNER_NAME
        image             = $env:ImageOS
        imageVersion      = $env:ImageVersion
        commit            = $env:GITHUB_SHA
        ref               = $env:GITHUB_REF_NAME
    }
    if ($GeckoExe -and (Test-Path -LiteralPath $GeckoExe)) {
        $info['gecko'] = [string](Get-Item -LiteralPath $GeckoExe).VersionInfo.ProductVersion
        $info['geckoArchitecture'] = Get-PeMachine $GeckoExe
    }
    $info
}

function Get-PerfAppInfo {
    param([Parameter(Mandatory = $true)] [string]$AppPath)
    $folder = Split-Path -Parent $AppPath
    $files = @(Get-ChildItem -LiteralPath $folder -Recurse -File)
    $bytes = 0L
    foreach ($file in $files) { $bytes += $file.Length }
    $runtimeConfig = Join-Path $folder 'Mosaic.runtimeconfig.json'
    $options = $null
    if (Test-Path -LiteralPath $runtimeConfig) {
        try { $options = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions } catch { }
    }
    $item = Get-Item -LiteralPath $AppPath
    [ordered]@{
        path           = $AppPath
        version        = [string]$item.VersionInfo.ProductVersion
        architecture   = Get-PeMachine $AppPath
        sizeMB         = [Math]::Round($bytes / 1MB, 1)
        files          = $files.Count
        runtimeOptions = $options
    }
}

# ---- a local web server for the test pages -----------------------------------------------------

function Get-PerfPageHtml {
    # A page of ordinary weight: ~60 KB of text in a few hundred elements, an inline SVG and a
    # script that builds a table once. No timers, animations or network requests, so an idle
    # tab costs nothing and every run loads exactly the same thing.
    param([int]$Number)
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append("<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"><title>Mosaic Perf Page $Number</title>")
    [void]$builder.Append('<style>body{font:16px/1.5 system-ui,sans-serif;margin:2em auto;max-width:60em;color:#222}section{border-bottom:1px solid #ddd;padding:1em 0}td{padding:2px 6px}</style>')
    [void]$builder.Append("</head><body><h1>Mosaic Perf Page $Number</h1>")
    [void]$builder.Append('<svg width="320" height="120" viewBox="0 0 320 120">')
    for ($i = 0; $i -lt 24; $i++) {
        [void]$builder.Append("<rect x=`"$($i * 13)`" y=`"$(100 - ($i * 37 % 90))`" width=`"10`" height=`"$($i * 37 % 90 + 10)`" fill=`"hsl($($i * 15),60%,50%)`"/>")
    }
    [void]$builder.Append('</svg>')
    $sentence = 'Mosaic measures what it costs to keep a page like this one open, in both engines, so that regressions are seen in numbers rather than guessed at. '
    for ($section = 0; $section -lt 40; $section++) {
        [void]$builder.Append("<section><h2>Section $section</h2>")
        for ($paragraph = 0; $paragraph -lt 3; $paragraph++) {
            [void]$builder.Append('<p>').Append($sentence * 3).Append("<a href=`"#s$section`">link $section.$paragraph</a></p>")
        }
        [void]$builder.Append('</section>')
    }
    [void]$builder.Append('<table id="t"></table><script>(function(){var t=document.getElementById("t");for(var r=0;r<200;r++){var row=t.insertRow();for(var c=0;c<6;c++){row.insertCell().textContent=(r*c).toString(16);}}})();</script>')
    [void]$builder.Append('</body></html>')
    $builder.ToString()
}

function Start-PerfPageServer {
    <#
    .SYNOPSIS
        Serves /page/<n> (the test page numbered n) at http://127.0.0.1:<port>/ from a background
        runspace. A plain TcpListener, as in smoke.ps1, because HttpListener needs a URL
        reservation unless run as an administrator.
    #>
    $pages = @{}
    for ($i = 0; $i -le 20; $i++) { $pages["/page/$i"] = $script:Utf8.GetBytes((Get-PerfPageHtml -Number $i)) }
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $hits = [hashtable]::Synchronized(@{ Count = 0 })
    $runspace = [PowerShell]::Create()
    $null = $runspace.AddScript({
            param($Listener, $Pages, $Hits)
            while ($true) {
                try { $client = $Listener.AcceptTcpClient() }
                catch { return }
                try {
                    $client.ReceiveTimeout = 1000
                    $stream = $client.GetStream()
                    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
                    $requestLine = $reader.ReadLine()
                    while (-not [string]::IsNullOrEmpty($reader.ReadLine())) { }
                    $path = ''
                    if ($requestLine -match '^GET (\S+) HTTP/') { $path = $Matches[1] }
                    if ($Pages.ContainsKey($path)) {
                        $Hits.Count++
                        $status = '200 OK'
                        $content = $Pages[$path]
                    }
                    else {
                        $status = '404 Not Found'
                        $content = New-Object byte[] 0
                    }
                    $head = [System.Text.Encoding]::ASCII.GetBytes(
                        "HTTP/1.1 $status`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($content.Length)`r`n" +
                        "Cache-Control: no-store`r`nConnection: close`r`n`r`n")
                    $stream.Write($head, 0, $head.Length)
                    $stream.Write($content, 0, $content.Length)
                    $stream.Flush()
                }
                catch { }
                finally { $client.Close() }
            }
        }).AddArgument($listener).AddArgument($pages).AddArgument($hits)
    $handle = $runspace.BeginInvoke()
    [pscustomobject]@{
        Base     = "http://127.0.0.1:$($listener.LocalEndpoint.Port)"
        Hits     = $hits
        Listener = $listener
        Runspace = $runspace
        Handle   = $handle
    }
}

function Get-PerfPageUrl {
    param([Parameter(Mandatory = $true)] $Server, [int]$Number = 1)
    "$($Server.Base)/page/$Number"
}

function Stop-PerfPageServer {
    param($Server)
    if ($null -eq $Server) { return }
    $Server.Listener.Stop()
    try { $Server.Runspace.EndInvoke($Server.Handle) } catch { }
    $Server.Runspace.Dispose()
}

# ---- launching and stopping Mosaic -------------------------------------------------------------

function Start-PerfMosaic {
    <#
    .SYNOPSIS
        Starts Mosaic with automation on, its own data folder and the start-up trace file, and
        returns a launch record. The environment is set for the child only.
    #>
    param(
        [Parameter(Mandatory = $true)] [string]$AppPath,
        [Parameter(Mandatory = $true)] [string]$DataDirectory,
        [Parameter(Mandatory = $true)] [string]$TraceFile,
        [string]$GeckoExe,
        [string[]]$ExtraArguments = @()
    )
    Initialize-PerfNative
    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $TraceFile) { Remove-Item -LiteralPath $TraceFile -Force }

    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $AppPath
    $info.WorkingDirectory = Split-Path -Parent $AppPath
    $info.UseShellExecute = $false
    $arguments = @('--mosaic-automation') + @($ExtraArguments)
    $info.Arguments = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $info.EnvironmentVariables['MOSAIC_DATA_DIR'] = $DataDirectory
    $info.EnvironmentVariables['MOSAIC_STARTUP_TRACE'] = $TraceFile
    if ($GeckoExe) { $info.EnvironmentVariables['MOSAIC_GECKO_EXE'] = $GeckoExe }

    $process = [System.Diagnostics.Process]::Start($info)
    $created = [MosaicPerf.Native]::CreationFileTime($process.Id)
    [pscustomobject]@{
        Process       = $process
        Id            = $process.Id
        Created       = $created
        DataDirectory = $DataDirectory
        TraceFile     = $TraceFile
        Automation    = $false
        GeckoId       = 0
    }
}

function Get-PerfSinceStart {
    # Milliseconds from the launch's process creation to now.
    param([Parameter(Mandatory = $true)] $Launch)
    ([MosaicPerf.Native]::NowFileTime() - $Launch.Created) / 10000.0
}

function Wait-PerfWindow {
    # Milliseconds from process creation to a shown (visible, uncloaked) window; $null on timeout.
    param([Parameter(Mandatory = $true)] $Launch, [int]$TimeoutSeconds = 60)
    $seen = [MosaicPerf.Native]::WaitForShownWindow($Launch.Process, $TimeoutSeconds * 1000)
    if ($seen -eq -1) {
        $code = $Launch.Process.ExitCode
        throw ("Mosaic exited during start-up (exit code {0}, 0x{1:X8})." -f $code, $code)
    }
    if ($seen -le 0) { return $null }
    ($seen - $Launch.Created) / 10000.0
}

function Read-PerfTrace {
    # The start-up trace the app writes (StartupTrace), or $null if there is none (yet).
    param([Parameter(Mandatory = $true)] [string]$Path)
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) { return $null }
        try { return ([System.IO.File]::ReadAllText($Path) | ConvertFrom-Json) }
        catch { Start-Sleep -Milliseconds 20 }
    }
    $null
}

function Get-PerfMark {
    param($Trace, [Parameter(Mandatory = $true)] [string]$Name)
    if ($null -eq $Trace) { return $null }
    $value = Get-MosaicProperty (Get-MosaicProperty $Trace 'marks') $Name
    if ($null -eq $value) { return $null }
    [double]$value
}

function Wait-PerfMark {
    # Waits for one of the marks to appear in the trace; returns the trace (or $null on timeout).
    param([Parameter(Mandatory = $true)] $Launch, [Parameter(Mandatory = $true)] [string[]]$Names, [double]$TimeoutSeconds = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $trace = Read-PerfTrace $Launch.TraceFile
        foreach ($name in $Names) {
            if ($null -ne (Get-PerfMark $trace $name)) { return $trace }
        }
        if ($Launch.Process.HasExited) { throw "Mosaic exited (exit code $($Launch.Process.ExitCode))." }
        if ((Get-Date) -ge $deadline) { return $null }
        Start-Sleep -Milliseconds 25
    }
}

function Wait-PerfAutomation {
    # Polls the automation pipe; sets $Launch.Automation and returns it.
    param([Parameter(Mandatory = $true)] $Launch, [double]$TimeoutSeconds = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $Launch.Process.HasExited) {
        try {
            $reply = Send-MosaicCommand -Do 'dump' -ConnectTimeoutMilliseconds 200 -ReplyTimeoutSeconds 10
            if ((Get-MosaicProperty $reply.Reply 'ok') -eq $true) {
                $Launch.Automation = $true
                $gecko = Get-MosaicProperty (Get-MosaicProperty $reply.Reply 'state') 'geckoProcessId'
                if ($gecko) { $Launch.GeckoId = [int]$gecko }
                return $true
            }
        }
        catch { }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Milliseconds 100
    }
    $false
}

function Invoke-PerfCommand {
    # Sends a command and insists on {"ok": true}; returns the reply.
    param([Parameter(Mandatory = $true)] [string]$Do, [string]$Value)
    $result = Send-MosaicCommand -Do $Do -Value $Value
    if ((Get-MosaicProperty $result.Reply 'ok') -ne $true) {
        throw "Mosaic refused '$Do $Value': $(Get-MosaicProperty $result.Reply 'error')"
    }
    $result.Reply
}

function Get-PerfState {
    param([Parameter(Mandatory = $true)] $Launch)
    $state = Get-MosaicProperty (Invoke-PerfCommand -Do 'dump') 'state'
    $gecko = Get-MosaicProperty $state 'geckoProcessId'
    if ($gecko) { $Launch.GeckoId = [int]$gecko }
    $state
}

function Wait-PerfState {
    # Polls dump until the condition holds; returns the state, or throws on timeout.
    param(
        [Parameter(Mandatory = $true)] $Launch,
        [Parameter(Mandatory = $true)] [string]$What,
        [Parameter(Mandatory = $true)] [scriptblock]$Condition,
        [int]$TimeoutSeconds = 90
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $state = $null
    while ($true) {
        if ($Launch.Process.HasExited) { throw "Mosaic exited while waiting for $What." }
        $state = Get-PerfState $Launch
        if (& $Condition $state) { return $state }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out after $TimeoutSeconds s waiting for $What."
}

function Get-PerfTree {
    <#
    .SYNOPSIS
        Every process of a Mosaic run: Mosaic's process tree, Gecko's (rooted at the process the
        app reports, as Firefox's launcher may have cut it off from Mosaic's tree) and any engine
        process whose command line names the run's data folder. Each entry has Id, Name,
        ParentId, Created (FILETIME) and Group (mosaic, webview2, gecko, other).
    #>
    param([Parameter(Mandatory = $true)] $Launch)
    $all = @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine, CreationDate)
    $byId = @{}
    foreach ($process in $all) { $byId[[int]$process.ProcessId] = $process }
    $queue = New-Object System.Collections.Queue
    foreach ($root in @($Launch.Id, $Launch.GeckoId)) {
        if ($root -gt 0 -and $byId.ContainsKey($root)) { $queue.Enqueue($byId[$root]) }
    }
    foreach ($process in $all) {
        $isEngine = $process.Name -eq 'firefox.exe' -or $process.Name -eq 'msedgewebview2.exe'
        if ($isEngine -and $process.CommandLine -and
            $process.CommandLine.IndexOf($Launch.DataDirectory, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $queue.Enqueue($process)
        }
    }
    $found = @{}
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        $parentId = [int]$parent.ProcessId
        if ($found.ContainsKey($parentId)) { continue }
        $found[$parentId] = $parent
        foreach ($child in $all) {
            # Process IDs are reused: a "child" older than its parent is somebody else's.
            if ([int]$child.ParentProcessId -eq $parentId -and $child.CreationDate -ge $parent.CreationDate) {
                $queue.Enqueue($child)
            }
        }
    }
    @($found.Values | ForEach-Object {
            $group = 'other'
            if ([int]$_.ProcessId -eq $Launch.Id) { $group = 'mosaic' }
            elseif ($_.Name -eq 'msedgewebview2.exe') { $group = 'webview2' }
            elseif ($_.Name -eq 'firefox.exe') { $group = 'gecko' }
            [pscustomobject]@{
                Id       = [int]$_.ProcessId
                Name     = [string]$_.Name
                ParentId = [int]$_.ParentProcessId
                Created  = $_.CreationDate.ToFileTimeUtc()
                Group    = $group
            }
        })
}

function Test-PerfSameProcess {
    # True when a native sample is of the process a tree entry describes: same ID and the same
    # creation time (to within 10 ms; WMI reports it to the microsecond, Windows to 100 ns).
    param($Sample, $Entry)
    $null -ne $Sample -and [Math]::Abs($Sample.CreationFileTime - $Entry.Created) -lt 100000
}

function Measure-PerfMemory {
    <#
    .SYNOPSIS
        Memory of a run's processes, total and per group, in MB: private working set (what the
        run keeps in RAM for itself; Task Manager's "Memory" column), commit (private bytes) and
        working set (including shared pages, so it double-counts across processes).
    #>
    param([Parameter(Mandatory = $true)] $Launch)
    Initialize-PerfNative
    $tree = @(Get-PerfTree $Launch)
    $groups = [ordered]@{}
    foreach ($name in @('total', 'mosaic', 'webview2', 'gecko', 'other')) {
        $groups[$name] = [ordered]@{ processes = 0; privateWorkingSetMB = 0.0; commitMB = 0.0; workingSetMB = 0.0 }
    }
    $processes = @()
    foreach ($entry in $tree) {
        $sample = [MosaicPerf.Native]::Sample($entry.Id, $true)
        if (-not (Test-PerfSameProcess $sample $entry)) { continue }
        $private = [double]$sample.PrivateWorkingSetBytes
        if ($private -lt 0) { $private = [double]$sample.WorkingSetBytes }
        foreach ($name in @('total', $entry.Group)) {
            $groups[$name]['processes'] += 1
            $groups[$name]['privateWorkingSetMB'] += $private / 1MB
            $groups[$name]['commitMB'] += [double]$sample.CommitBytes / 1MB
            $groups[$name]['workingSetMB'] += [double]$sample.WorkingSetBytes / 1MB
        }
        $processes += [ordered]@{
            id = $entry.Id; name = $entry.Name; group = $entry.Group
            privateWorkingSetMB = [Math]::Round($private / 1MB, 1)
            commitMB = [Math]::Round([double]$sample.CommitBytes / 1MB, 1)
        }
    }
    foreach ($name in @($groups.Keys)) {
        foreach ($field in @('privateWorkingSetMB', 'commitMB', 'workingSetMB')) {
            $groups[$name][$field] = [Math]::Round($groups[$name][$field], 1)
        }
    }
    [ordered]@{ groups = $groups; processes = $processes }
}

function Measure-PerfMemorySettled {
    # Three samples two seconds apart; the one with the median total private working set.
    param([Parameter(Mandatory = $true)] $Launch)
    $samples = @()
    for ($i = 0; $i -lt 3; $i++) {
        if ($i -gt 0) { Start-Sleep -Seconds 2 }
        $samples += , (Measure-PerfMemory $Launch)
    }
    $sorted = @($samples | Sort-Object { $_.groups.total.privateWorkingSetMB })
    $sorted[1]
}

function Measure-PerfCpu {
    # CPU used by the run's processes over $Seconds, as a percentage of one core and of the machine.
    param([Parameter(Mandatory = $true)] $Launch, [int]$Seconds = 30)
    Initialize-PerfNative
    $before = @{}
    foreach ($entry in @(Get-PerfTree $Launch)) {
        $sample = [MosaicPerf.Native]::Sample($entry.Id, $false)
        if ($sample) { $before["$($entry.Id):$($sample.CreationFileTime)"] = $sample.CpuTicks }
    }
    $start = [MosaicPerf.Native]::NowFileTime()
    Start-Sleep -Seconds $Seconds
    $used = 0L
    $count = 0
    foreach ($entry in @(Get-PerfTree $Launch)) {
        $sample = [MosaicPerf.Native]::Sample($entry.Id, $false)
        if (-not $sample) { continue }
        $count++
        $key = "$($entry.Id):$($sample.CreationFileTime)"
        if ($before.ContainsKey($key)) { $used += $sample.CpuTicks - $before[$key] }
        else { $used += $sample.CpuTicks }   # started during the interval
    }
    $elapsed = [MosaicPerf.Native]::NowFileTime() - $start
    $oneCore = 100.0 * $used / $elapsed
    [ordered]@{
        seconds          = [Math]::Round($elapsed / 1e7, 1)
        cpuMs            = [Math]::Round($used / 1e4, 1)
        percentOfOneCore = [Math]::Round($oneCore, 2)
        percentOfMachine = [Math]::Round($oneCore / [Environment]::ProcessorCount, 2)
        processes        = $count
    }
}

function Stop-PerfMosaic {
    <#
    .SYNOPSIS
        Ends a launch: "quit" over automation (else WM_CLOSE), waits, then ends whatever of the
        run is left. Returns how long the exit took and what had to be killed.
    #>
    param([Parameter(Mandatory = $true)] $Launch)
    $tree = @()
    try { $tree = @(Get-PerfTree $Launch) } catch { }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $Launch.Process.HasExited) {
        $asked = $false
        if ($Launch.Automation) {
            try { Send-MosaicCommand -Do 'quit' -ReplyTimeoutSeconds 10 | Out-Null; $asked = $true } catch { }
        }
        if (-not $asked) { try { [void]$Launch.Process.CloseMainWindow() } catch { } }
    }
    $clean = $Launch.Process.WaitForExit(20000)
    $exitMs = $watch.Elapsed.TotalMilliseconds
    if (-not $clean) {
        Stop-Process -Id $Launch.Id -Force -ErrorAction SilentlyContinue
        $Launch.Process.WaitForExit(10000) | Out-Null
    }
    # Engines get a moment to follow Mosaic out, then whatever is left is ended.
    $leftovers = @()
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $alive = @($tree | Where-Object {
                Test-PerfSameProcess ([MosaicPerf.Native]::Sample($_.Id, $false)) $_
            })
        if ($alive.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    foreach ($entry in $alive) {
        $leftovers += "$($entry.Name) ($($entry.Id))"
        Stop-Process -Id $entry.Id -Force -ErrorAction SilentlyContinue
    }
    [ordered]@{ exitMs = [Math]::Round($exitMs, 0); cleanExit = $clean; leftovers = $leftovers }
}

function Remove-PerfData {
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop; return }
        catch { Start-Sleep -Milliseconds 500 }
    }
}

Export-ModuleMember -Function Initialize-PerfNative, Write-PerfText, ConvertTo-PerfJson, Get-PerfStats,
    Get-NativeArchitecture, Get-PeMachine, Get-WebView2Version, Get-PerfMachineInfo, Get-PerfAppInfo,
    Start-PerfPageServer, Get-PerfPageUrl, Stop-PerfPageServer, Start-PerfMosaic, Get-PerfSinceStart, Wait-PerfWindow,
    Read-PerfTrace, Get-PerfMark, Wait-PerfMark, Wait-PerfAutomation, Invoke-PerfCommand, Get-PerfState,
    Wait-PerfState, Get-PerfTree, Measure-PerfMemory, Measure-PerfMemorySettled, Measure-PerfCpu, Stop-PerfMosaic,
    Remove-PerfData
