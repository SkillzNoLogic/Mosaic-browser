<#
.SYNOPSIS
    End-to-end check of a published Mosaic on a real Windows machine (or CI runner).

.DESCRIPTION
    Starts Mosaic with --mosaic-automation and a throw-away data folder, then drives it through
    the automation pipe (windows/CONTRACT.md) and checks, through "dump", that:

      1. a page opened on the Mozilla engine loads, with the expected title, and Gecko's window
         is attached to Mosaic's window;
      2. the same tab switched to Chromium loads the page again;
      3. the search engine can be changed and a search goes to that engine;
      4. a Chrome extension (a minimal MV3 extension this script zips up itself, so the
         Chrome Web Store is not involved) installs and reports loaded;
      5. the content blocker is ready, and in each engine blocks the ad script a page served by
         this script asks for (a non-zero "blocked" count), lets it through once switched off
         for the site (zero after the reload), and blocks it again once switched back on;
      6. "quit" ends Mosaic and leaves no Mosaic or engine process behind.

    A screenshot is requested after each of steps 1-5. Screenshots, the state at each step,
    Mosaic's output and any crash reports from the Windows event log are collected in the
    output folder. Step 3 needs Internet access; the others do not.

    The exit code is 0 only if every check passed. Whatever happens, every process this script
    started is gone when it returns.

.PARAMETER AppPath
    Mosaic.exe to test. Default: windows\publish\<this machine's rid>\Mosaic.exe.

.PARAMETER OutputDirectory
    Where screenshots and logs go. Emptied first. Default: windows\artifacts\smoke.

.PARAMETER GeckoExe
    firefox.exe to use as the Mozilla engine. Default: %MOSAIC_GECKO_EXE%, then the copy
    installed by bootstrap.ps1, then a Firefox installed in Program Files.

.PARAMETER GeckoHostMode
    child (the default embedding) or owned (the fallback; passes --gecko-host=owned).

.PARAMETER TimeoutSeconds
    How long each wait (page load, engine switch, extension install) may take. Default 120.

.PARAMETER KeepData
    Keep the throw-away data folder (profiles of both engines) in the output folder.

.EXAMPLE
    .\smoke.ps1
.EXAMPLE
    .\smoke.ps1 -AppPath C:\Mosaic\Mosaic.exe -GeckoHostMode owned
#>
[CmdletBinding()]
param(
    [string]$AppPath,

    [string]$OutputDirectory,

    [string]$GeckoExe,

    [ValidateSet('child', 'owned')]
    [string]$GeckoHostMode = 'child',

    [ValidateRange(10, 3600)]
    [int]$TimeoutSeconds = 120,

    [switch]$KeepData
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'MosaicAutomation.psm1') -Force

$PageTitle = 'Mosaic Smoke Page'
$ExtensionName = 'Mosaic Smoke Fixture'
$SearchEngineId = 'duckduckgo'
$SearchHost = 'duckduckgo.com'
$SearchWord = 'mosaicsmoketest'
$BlockerPageTitle = 'Mosaic Blocker Page'
# A script every set of default lists blocks. A blocked request is stopped before any connection
# is made, so this step needs no Internet access.
$BlockedScript = 'http://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js'

$script:App = $null             # the Mosaic process this script started
$script:PageServer = $null      # the web server for the content blocker check
$script:SoftFailures = @()      # checks that failed without stopping the run
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)

# ---- helpers ---------------------------------------------------------------------------------

function Resolve-AppPath {
    if ($AppPath) {
        if (-not (Test-Path -LiteralPath $AppPath)) { throw "Mosaic.exe not found at '$AppPath'." }
        return (Resolve-Path -LiteralPath $AppPath).Path
    }
    $rid = 'win-x64'
    $native = $env:PROCESSOR_ARCHITEW6432
    if (-not $native) { $native = $env:PROCESSOR_ARCHITECTURE }
    if ($native -eq 'ARM64') { $rid = 'win-arm64' }
    $default = Join-Path (Split-Path -Parent $PSScriptRoot) "publish\$rid\Mosaic.exe"
    if (-not (Test-Path -LiteralPath $default)) {
        throw "No published app at '$default'. Run scripts\build.ps1 first, or pass -AppPath."
    }
    $default
}

function Resolve-GeckoExe {
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
    throw "No Gecko engine found (looked at: $($candidates -join '; ')). Run scripts\bootstrap.ps1 first, or pass -GeckoExe."
}

function Write-TextFile {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, $script:Utf8)
}

function New-ExtensionFixture {
    # A minimal Manifest V3 extension with a background service worker: the smallest thing
    # that exercises Mosaic's CRX handling and manifest conversion. Written entry by entry so
    # that the archive is the same whatever the PowerShell and .NET versions.
    param([string]$ZipPath)

    $files = [ordered]@{
        'manifest.json' = @'
{
  "manifest_version": 3,
  "name": "Mosaic Smoke Fixture",
  "version": "1.0.0",
  "description": "Minimal extension used by Mosaic's smoke test.",
  "permissions": ["storage"],
  "background": { "service_worker": "background.js" }
}
'@
        'background.js' = @'
chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.local.set({ mosaicSmokeInstalledAt: Date.now() });
});
'@
    }

    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $files.Keys) {
                $entryStream = $archive.CreateEntry($name).Open()
                try {
                    $bytes = $script:Utf8.GetBytes($files[$name])
                    $entryStream.Write($bytes, 0, $bytes.Length)
                }
                finally { $entryStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Start-PageServer {
    # Serves one page at http://127.0.0.1:<port>/ from a background runspace of this process: the
    # content blocker filters web pages only, so the page cannot be a file: or data: URL. A plain
    # TcpListener, because HttpListener needs a URL reservation unless run as an administrator.
    # Hits.Page counts requests for the page, so a check can tell that a reload has happened.
    param([Parameter(Mandatory = $true)] [string]$Html)

    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $hits = [hashtable]::Synchronized(@{ Page = 0 })
    $runspace = [PowerShell]::Create()
    $null = $runspace.AddScript({
            param($Listener, $Body, $Hits)
            while ($true) {
                try { $client = $Listener.AcceptTcpClient() }
                catch { return }   # the listener was stopped
                try {
                    # Browsers open connections ahead of need; one that sends nothing is dropped
                    # after a second instead of holding up the requests queued behind it.
                    $client.ReceiveTimeout = 1000
                    $stream = $client.GetStream()
                    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
                    $requestLine = $reader.ReadLine()
                    while (-not [string]::IsNullOrEmpty($reader.ReadLine())) { }
                    if ($requestLine -match '^GET / HTTP/') {
                        $Hits.Page++
                        $status = '200 OK'
                        $content = $Body
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
                catch {
                    # A connection that timed out or was closed by the browser; serve the next one.
                }
                finally {
                    $client.Close()
                }
            }
        }).AddArgument($listener).AddArgument($script:Utf8.GetBytes($Html)).AddArgument($hits)
    $handle = $runspace.BeginInvoke()
    [pscustomobject]@{
        Url      = "http://127.0.0.1:$($listener.LocalEndpoint.Port)/"
        Hits     = $hits
        Listener = $listener
        Runspace = $runspace
        Handle   = $handle
    }
}

function Stop-PageServer {
    param($Server)
    if ($null -eq $Server) { return }
    $Server.Listener.Stop()
    try { $Server.Runspace.EndInvoke($Server.Handle) } catch { }
    $Server.Runspace.Dispose()
}

function Test-BlockerPage {
    # True when the selected tab shows the blocker page in $Engine, loaded, and the page has been
    # served more than $After times; $Blocked says whether its ad script must have been blocked.
    param($State, [string]$Engine, [int]$After, [bool]$Blocked)
    $tab = Get-SelectedTab $State
    if (-not $tab -or $script:PageServer.Hits.Page -le $After) { return $false }
    $count = [int](Get-MosaicProperty $tab 'blocked')
    (Get-MosaicProperty $tab 'engine') -eq $Engine -and (Get-MosaicProperty $tab 'loading') -eq $false `
        -and (Get-MosaicProperty $tab 'title') -eq $BlockerPageTitle -and (($count -gt 0) -eq $Blocked)
}

function Assert-AppRunning {
    if ($null -eq $script:App) { throw 'Mosaic has not been started.' }
    if ($script:App.HasExited) {
        throw "Mosaic exited unexpectedly (exit code $($script:App.ExitCode)). See mosaic-trace.log, mosaic-stderr.log and events.txt in the output folder."
    }
}

function Invoke-Mosaic {
    # Sends a command and insists on {"ok": true}.
    param(
        [Parameter(Mandatory = $true)] [string]$Do,
        [string]$Value,
        [string]$Out
    )
    Assert-AppRunning
    $result = Send-MosaicCommand -Do $Do -Value $Value -Out $Out
    if ((Get-MosaicProperty $result.Reply 'ok') -ne $true) {
        throw "Mosaic refused '$Do $Value': $(Get-MosaicProperty $result.Reply 'error')"
    }
    $result.Reply
}

function Get-MosaicState {
    $reply = Invoke-Mosaic -Do 'dump'
    $state = Get-MosaicProperty $reply 'state'
    if ($null -eq $state) { throw "Mosaic's reply to 'dump' has no 'state'." }
    $state
}

function Get-SelectedTab {
    param($State)
    @(Get-MosaicProperty $State 'tabs') | Where-Object { (Get-MosaicProperty $_ 'selected') -eq $true } | Select-Object -First 1
}

function Format-State {
    # The parts of the state that explain a failure, on one line.
    param($State)
    if ($null -eq $State) { return '(no state)' }
    $tab = Get-SelectedTab $State
    $parts = @(
        "engine=$(Get-MosaicProperty $State 'engine')",
        "geckoStatus=$(Get-MosaicProperty $State 'geckoStatus')",
        "geckoAttached=$(Get-MosaicProperty $State 'geckoAttached')",
        "chromiumAvailable=$(Get-MosaicProperty $State 'chromiumAvailable')",
        "notice='$(Get-MosaicProperty $State 'notice')'",
        "extensionError='$(Get-MosaicProperty $State 'extensionError')'"
    )
    $blocker = Get-MosaicProperty $State 'blocker'
    if ($blocker) {
        $parts += "blocker: status=$(Get-MosaicProperty $blocker 'status') enabled=$(Get-MosaicProperty $blocker 'enabled') " +
            "filters=$(Get-MosaicProperty $blocker 'filters') error='$(Get-MosaicProperty $blocker 'error')'"
    }
    if ($tab) {
        $parts += "tab: engine=$(Get-MosaicProperty $tab 'engine') loading=$(Get-MosaicProperty $tab 'loading') " +
            "title='$(Get-MosaicProperty $tab 'title')' url='$(Get-MosaicProperty $tab 'url')' failure='$(Get-MosaicProperty $tab 'failure')' " +
            "blocked=$(Get-MosaicProperty $tab 'blocked')"
    }
    else {
        $parts += 'no selected tab'
    }
    $parts -join ', '
}

function Save-State {
    param($State, [string]$Name)
    if ($null -ne $State) {
        Write-TextFile (Join-Path $OutputDirectory "state-$Name.json") ($State | ConvertTo-Json -Depth 10)
    }
}

function Wait-MosaicState {
    # Polls "dump" until the condition holds. Returns the state that satisfied it.
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$What,
        [Parameter(Mandatory = $true)] [scriptblock]$Condition
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $state = $null
    while ($true) {
        $state = Get-MosaicState
        if (& $Condition $state) {
            Save-State $state $Name
            return $state
        }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Milliseconds 500
    }
    Save-State $state "$Name-timeout"
    throw "Timed out after $TimeoutSeconds s waiting for: $What. Last state: $(Format-State $state)"
}

function Save-Screenshot {
    # A missing screenshot is reported at the end but does not stop the run: the screenshots
    # are evidence, and the remaining steps are still worth running.
    param([string]$Name)
    $path = Join-Path $OutputDirectory "$Name.png"
    try {
        Invoke-Mosaic -Do 'screenshot' -Value $path | Out-Null
        # The reply may precede the file by a moment.
        $deadline = (Get-Date).AddSeconds(10)
        while (-not ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -gt 0)) {
            if ((Get-Date) -ge $deadline) { throw "no file was written to $path" }
            Start-Sleep -Milliseconds 250
        }
        Write-Host "    screenshot: $path"
    }
    catch {
        $script:SoftFailures += "Screenshot '$Name' failed: $($_.Exception.Message)"
        Write-Host "    screenshot '$Name' FAILED: $($_.Exception.Message)"
    }
}

function Save-WindowTree {
    # Every native window inside Mosaic's main window, with its class, owning process and
    # place. The embedded Gecko window and WebView2 are native windows that WPF knows nothing
    # about, so this is the way to see what is really drawn where, and by which process.
    param([Parameter(Mandatory = $true)] [string] $Name)
    try {
        if (-not ('MosaicSmoke.WindowTree' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MosaicSmoke {
    public static class WindowTree {
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        delegate bool EnumProc(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);

        public static List<string> Describe(IntPtr root) {
            var lines = new List<string>();
            RECT origin;
            GetWindowRect(root, out origin);
            lines.Add(string.Format("root 0x{0:X} at {1},{2} size {3}x{4}", root.ToInt64(), origin.Left, origin.Top,
                origin.Right - origin.Left, origin.Bottom - origin.Top));
            EnumChildWindows(root, (window, data) => {
                var name = new StringBuilder(256);
                GetClassNameW(window, name, name.Capacity);
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                RECT rect;
                GetWindowRect(window, out rect);
                lines.Add(string.Format("0x{0:X} parent 0x{1:X} pid {2} {3} {4} at {5},{6} size {7}x{8}",
                    window.ToInt64(), GetParent(window).ToInt64(), processId, IsWindowVisible(window) ? "visible" : "hidden",
                    name, rect.Left - origin.Left, rect.Top - origin.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
                return true;
            }, IntPtr.Zero);
            return lines;
        }
    }
}
'@
        }
        $process = Get-Process -Id $script:App.Id
        $process.Refresh()
        $lines = [MosaicSmoke.WindowTree]::Describe($process.MainWindowHandle)
        $path = Join-Path $OutputDirectory "windows-$Name.txt"
        Write-TextFile -Path $path -Text (($lines -join "`r`n") + "`r`n")
        Write-Host "    window tree: $path"
        foreach ($line in $lines) { Write-Host "      $line" }
    }
    catch {
        Write-Host "    window tree '$Name' FAILED: $($_.Exception.Message)"
    }
}

function Get-ProcessSnapshot {
    # Every process that belongs to this run: Mosaic's process tree, Gecko's process tree
    # (Firefox's launcher process may have exited, which cuts the browser off from Mosaic's
    # tree, so it is rooted separately at the process ID the app reports), and any engine
    # process whose command line names the throw-away data folder (both engines pass their
    # profile folder on the command line).
    param([int[]]$Roots, [string]$DataDirectory)

    $all = @(Get-CimInstance -ClassName Win32_Process)
    $byId = @{}
    foreach ($process in $all) { $byId[[int]$process.ProcessId] = $process }

    $found = @{}
    $queue = New-Object System.Collections.Queue
    foreach ($root in $Roots) {
        if ($root -gt 0 -and $byId.ContainsKey($root)) { $queue.Enqueue($byId[$root]) }
    }
    foreach ($process in $all) {
        $isEngine = $process.Name -eq 'firefox.exe' -or $process.Name -eq 'msedgewebview2.exe'
        if ($isEngine -and $process.CommandLine -and
            $process.CommandLine.IndexOf($DataDirectory, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $queue.Enqueue($process)
        }
    }
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
            [pscustomobject]@{ Id = [int]$_.ProcessId; Name = $_.Name; Created = $_.CreationDate }
        })
}

function Get-Survivors {
    # The processes of a snapshot that are still running (same ID and same start time).
    param($Snapshot)
    $survivors = @()
    foreach ($entry in @($Snapshot)) {
        $current = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($entry.Id)" -ErrorAction SilentlyContinue
        if ($current -and $current.CreationDate -eq $entry.Created) { $survivors += $entry }
    }
    $survivors
}

function Stop-Leftovers {
    param($Snapshot)
    foreach ($entry in @(Get-Survivors $Snapshot)) {
        Write-Host "    stopping leftover process $($entry.Name) ($($entry.Id))"
        Stop-Process -Id $entry.Id -Force -ErrorAction SilentlyContinue
    }
}

function Save-EventLog {
    # Crashes of a never-before-run app show up here: unhandled .NET exceptions and native faults.
    param([datetime]$Since)
    try {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $Since; Level = 1, 2 } -ErrorAction Stop |
                Where-Object { $_.ProviderName -eq '.NET Runtime' -or $_.ProviderName -eq 'Application Error' -or $_.ProviderName -eq 'Windows Error Reporting' })
        if ($events.Count -gt 0) {
            $text = ($events | Format-List TimeCreated, ProviderName, Id, Message | Out-String -Width 4096)
            Write-TextFile (Join-Path $OutputDirectory 'events.txt') $text
            Write-Host "    $($events.Count) error event(s) saved to events.txt"
        }
    }
    catch {
        # "No events were found" is reported as an error; there is nothing to save then.
    }
}

# ---- run -------------------------------------------------------------------------------------

$windowsRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $windowsRoot 'artifacts\smoke' }
if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$dataDirectory = Join-Path $OutputDirectory 'data'
$fixtures = Join-Path $OutputDirectory 'fixtures'
New-Item -ItemType Directory -Path $dataDirectory, $fixtures -Force | Out-Null

$savedDataDir = $env:MOSAIC_DATA_DIR
$savedGeckoExe = $env:MOSAIC_GECKO_EXE
$savedTraceFile = $env:MOSAIC_TRACE_FILE
$started = Get-Date
$snapshot = @()
$failure = $null

try {
    $appExe = Resolve-AppPath
    $gecko = Resolve-GeckoExe
    Write-Host "==> Mosaic:  $appExe"
    Write-Host "    Gecko:   $gecko"
    Write-Host "    Output:  $OutputDirectory"

    # The pipe name is fixed, so a second automated Mosaic would be driven by mistake.
    $other = $null
    try { $other = Send-MosaicCommand -Do 'dump' -ConnectTimeoutMilliseconds 300 -ReplyTimeoutSeconds 5 } catch { }
    if ($other) { throw 'Another Mosaic is already running with --mosaic-automation. Quit it first.' }

    $pagePath = Join-Path $fixtures 'page.html'
    Write-TextFile $pagePath "<!doctype html>`n<html><head><meta charset=`"utf-8`"><title>$PageTitle</title></head>`n<body><h1>$PageTitle</h1><p>Loaded from a local file by smoke.ps1.</p></body></html>`n"
    $pageUrl = ([System.Uri]$pagePath).AbsoluteUri
    $extensionZip = Join-Path $fixtures 'mosaic-smoke-fixture.zip'
    New-ExtensionFixture -ZipPath $extensionZip

    Write-Host '==> Starting Mosaic'
    # A throw-away data folder keeps the test away from the user's real profile and makes the
    # run repeatable. The app then no longer finds the engine bootstrap.ps1 installed (it
    # lives in the real data folder), so the engine is named explicitly.
    $env:MOSAIC_DATA_DIR = $dataDirectory
    $env:MOSAIC_GECKO_EXE = $gecko
    # The Gecko window host's step-by-step diagnostics, the first thing to read on a failure.
    $env:MOSAIC_TRACE_FILE = Join-Path $OutputDirectory 'mosaic-trace.log'
    $arguments = @('--mosaic-automation')
    if ($GeckoHostMode -eq 'owned') { $arguments += '--gecko-host=owned' }
    $script:App = Start-Process -FilePath $appExe -ArgumentList $arguments -PassThru `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'mosaic-stdout.log') `
        -RedirectStandardError (Join-Path $OutputDirectory 'mosaic-stderr.log')
    # Touching the handle now is what makes ExitCode available later for a process started
    # without -Wait.
    $null = $script:App.Handle

    $deadline = (Get-Date).AddSeconds(60)
    $ready = $false
    while (-not $ready) {
        Assert-AppRunning
        try {
            Send-MosaicCommand -Do 'dump' -ConnectTimeoutMilliseconds 1000 | Out-Null
            $ready = $true
        }
        catch {
            if ((Get-Date) -ge $deadline) {
                throw "Mosaic did not open its automation pipe within 60 s: $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Host "    automation pipe is up (process $($script:App.Id))"

    Write-Host '==> 1. Mozilla engine: open a page'
    Invoke-Mosaic -Do 'engine' -Value 'gecko' | Out-Null
    Invoke-Mosaic -Do 'navigate' -Value $pageUrl | Out-Null
    $state = Wait-MosaicState -Name '1-gecko' -What "the selected tab to finish loading '$PageTitle' on the Mozilla engine" -Condition {
        param($s)
        $tab = Get-SelectedTab $s
        $tab -and (Get-MosaicProperty $tab 'engine') -eq 'gecko' -and (Get-MosaicProperty $tab 'loading') -eq $false `
            -and (Get-MosaicProperty $tab 'title') -eq $PageTitle
    }
    if ((Get-MosaicProperty $state 'geckoAttached') -ne $true) {
        # The page loaded inside Gecko, but Gecko's window is not shown inside Mosaic's.
        $state = Wait-MosaicState -Name '1-gecko-attached' -What "Gecko's window to be attached to Mosaic's window (geckoAttached)" -Condition {
            param($s) (Get-MosaicProperty $s 'geckoAttached') -eq $true
        }
    }
    Write-Host "    loaded; Gecko process $(Get-MosaicProperty $state 'geckoProcessId'), window attached"
    Save-Screenshot '1-gecko'
    Save-WindowTree '1-gecko'

    Write-Host '==> 2. Chromium engine: switch the tab'
    if ((Get-MosaicProperty $state 'chromiumAvailable') -ne $true) {
        throw "Mosaic reports that Chromium is unavailable (chromiumAvailable is not true). Is the WebView2 runtime installed? See scripts\bootstrap.ps1."
    }
    Invoke-Mosaic -Do 'engine' -Value 'chromium' | Out-Null
    $state = Wait-MosaicState -Name '2-chromium' -What "the selected tab to finish loading '$PageTitle' on the Chromium engine" -Condition {
        param($s)
        $tab = Get-SelectedTab $s
        $tab -and (Get-MosaicProperty $tab 'engine') -eq 'chromium' -and (Get-MosaicProperty $tab 'hasChromiumView') -eq $true `
            -and (Get-MosaicProperty $tab 'loading') -eq $false -and (Get-MosaicProperty $tab 'title') -eq $PageTitle
    }
    Write-Host '    loaded'
    Save-Screenshot '2-chromium'
    Save-WindowTree '2-chromium'

    Write-Host "==> 3. Search engine: switch to $SearchEngineId and search"
    Invoke-Mosaic -Do 'searchEngine' -Value $SearchEngineId | Out-Null
    $state = Wait-MosaicState -Name '3-search-engine' -What "the search engine to be '$SearchEngineId'" -Condition {
        param($s) (Get-MosaicProperty $s 'searchEngine') -eq $SearchEngineId
    }
    # One word that is not an address, so the address bar treats it as a search.
    Invoke-Mosaic -Do 'navigate' -Value $SearchWord | Out-Null
    $state = Wait-MosaicState -Name '3-search' -What "the selected tab to go to a $SearchHost search for '$SearchWord'" -Condition {
        param($s)
        $tab = Get-SelectedTab $s
        $url = [string](Get-MosaicProperty $tab 'url')
        $url -like "https://$SearchHost/*" -and $url -like "*$SearchWord*"
    }
    Write-Host "    $(Get-MosaicProperty (Get-SelectedTab $state) 'url')"
    Save-Screenshot '3-search'

    Write-Host '==> 4. Extensions: install a local MV3 extension'
    Invoke-Mosaic -Do 'installFile' -Value $extensionZip | Out-Null
    $state = Wait-MosaicState -Name '4-extension' -What "the extension '$ExtensionName' to be installed and loaded" -Condition {
        param($s)
        $problem = [string](Get-MosaicProperty $s 'extensionError')
        if ($problem) { throw "Installing the extension failed: $problem" }
        $extension = @(Get-MosaicProperty $s 'extensions') | Where-Object { (Get-MosaicProperty $_ 'name') -eq $ExtensionName } | Select-Object -First 1
        $extension -and (Get-MosaicProperty $extension 'loaded') -eq $true -and (Get-MosaicProperty $s 'extensionsBusy') -ne $true
    }
    $installed = @(Get-MosaicProperty $state 'extensions') | Where-Object { (Get-MosaicProperty $_ 'name') -eq $ExtensionName } | Select-Object -First 1
    Write-Host "    loaded as $(Get-MosaicProperty $installed 'geckoId') (version $(Get-MosaicProperty $installed 'version'))"
    Invoke-Mosaic -Do 'showExtensions' -Value 'true' | Out-Null
    Start-Sleep -Milliseconds 500
    Save-Screenshot '4-extension'
    Invoke-Mosaic -Do 'showExtensions' -Value 'false' | Out-Null

    Write-Host '==> 5. Content blocker: block an ad script in both engines'
    $state = Wait-MosaicState -Name '5-blocker-ready' -What 'the content blocker to be ready' -Condition {
        param($s)
        $blocker = Get-MosaicProperty $s 'blocker'
        $status = [string](Get-MosaicProperty $blocker 'status')
        if ($status -eq 'unavailable') {
            throw 'The content blocker is not part of this build (blocker.status is "unavailable"). Run scripts\bootstrap.ps1, then build again.'
        }
        if ($status -eq 'failed') { throw "The content blocker could not start: $(Get-MosaicProperty $blocker 'error')" }
        $status -eq 'ready'
    }
    $blocker = Get-MosaicProperty $state 'blocker'
    Write-Host "    $(Get-MosaicProperty $blocker 'filters') filters, uBlock Origin $(Get-MosaicProperty $blocker 'engineVersion')"
    $script:PageServer = Start-PageServer -Html ("<!doctype html>`n<html><head><meta charset=`"utf-8`"><title>$BlockerPageTitle</title>" +
        "<script src=`"$BlockedScript`"></script></head>`n<body><h1>$BlockerPageTitle</h1><p>Served by smoke.ps1.</p></body></html>`n")
    # Chromium first, as the tab is there already; switching it to Mozilla then reloads the page.
    foreach ($engine in @('chromium', 'gecko')) {
        $served = $script:PageServer.Hits.Page
        Invoke-Mosaic -Do 'engine' -Value $engine | Out-Null
        if ((Get-MosaicProperty (Get-SelectedTab (Get-MosaicState)) 'url') -ne $script:PageServer.Url) {
            Invoke-Mosaic -Do 'navigate' -Value $script:PageServer.Url | Out-Null
        }
        $state = Wait-MosaicState -Name "5-blocker-$engine" -What "the $engine tab to load the blocker page with its ad script blocked (blocked > 0)" -Condition {
            param($s) Test-BlockerPage $s $engine $served $true
        }
        Write-Host "    $engine blocked $(Get-MosaicProperty (Get-SelectedTab $state) 'blocked') request(s)"
        Save-Screenshot "5-blocker-$engine"

        $served = $script:PageServer.Hits.Page
        Invoke-Mosaic -Do 'blockerSite' -Value 'off' | Out-Null
        $state = Wait-MosaicState -Name "5-blocker-$engine-site-off" -What "the $engine tab to reload with the blocker off for the site (blocked = 0)" -Condition {
            param($s) Test-BlockerPage $s $engine $served $false
        }
        Write-Host "    $engine with the site exempted: nothing blocked"

        $served = $script:PageServer.Hits.Page
        Invoke-Mosaic -Do 'blockerSite' -Value 'on' | Out-Null
        $state = Wait-MosaicState -Name "5-blocker-$engine-site-on" -What "the $engine tab to reload with the blocker back on (blocked > 0)" -Condition {
            param($s) Test-BlockerPage $s $engine $served $true
        }
    }

    Write-Host '==> 6. Quit'
    $geckoProcessId = 0
    $reported = Get-MosaicProperty $state 'geckoProcessId'
    if ($reported) { $geckoProcessId = [int]$reported }
    $snapshot = @(Get-ProcessSnapshot -Roots @($script:App.Id, $geckoProcessId) -DataDirectory $dataDirectory)
    Write-Host "    $($snapshot.Count) process(es) belong to this run"
    try {
        Invoke-Mosaic -Do 'quit' | Out-Null
    }
    catch {
        # Mosaic may be gone before it has answered; what matters is checked next.
        Write-Host "    (no reply to quit: $($_.Exception.Message))"
    }
    if (-not $script:App.WaitForExit(30000)) {
        throw 'Mosaic was still running 30 s after "quit".'
    }
    $deadline = (Get-Date).AddSeconds(20)
    $survivors = @(Get-Survivors $snapshot)
    while ($survivors.Count -gt 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $survivors = @(Get-Survivors $snapshot)
    }
    if ($survivors.Count -gt 0) {
        $list = ($survivors | ForEach-Object { "$($_.Name) ($($_.Id))" }) -join ', '
        throw "Processes left running 20 s after Mosaic quit: $list"
    }
    Write-Host '    Mosaic and both engines are gone'
}
catch {
    $failure = $_.Exception.Message
}
finally {
    # Clean up whatever is still there, however the run ended.
    $env:MOSAIC_DATA_DIR = $savedDataDir
    $env:MOSAIC_GECKO_EXE = $savedGeckoExe
    $env:MOSAIC_TRACE_FILE = $savedTraceFile
    Stop-PageServer $script:PageServer
    try {
        if ($null -ne $script:App) {
            if ($snapshot.Count -eq 0) {
                # The run ended before the quit step took its snapshot.
                $snapshot = @(Get-ProcessSnapshot -Roots @($script:App.Id) -DataDirectory $dataDirectory)
            }
            if (-not $script:App.HasExited) {
                Write-Host "==> Stopping Mosaic (process $($script:App.Id))"
                Stop-Process -Id $script:App.Id -Force -ErrorAction SilentlyContinue
                $script:App.WaitForExit(10000) | Out-Null
            }
            Stop-Leftovers $snapshot
        }
        Save-EventLog -Since $started
        if (-not $KeepData -and (Test-Path -LiteralPath $dataDirectory)) {
            Start-Sleep -Milliseconds 500   # let file handles of the stopped processes close
            Remove-Item -LiteralPath $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
        Write-Host "    clean-up problem: $($_.Exception.Message)"
    }
}

Write-Host ''
if ($failure) {
    Write-Host "SMOKE TEST FAILED: $failure"
}
foreach ($soft in $script:SoftFailures) {
    Write-Host "SMOKE TEST FAILED: $soft"
}
if ($failure -or $script:SoftFailures.Count -gt 0) {
    Write-Host "Screenshots and logs: $OutputDirectory"
    exit 1
}
Write-Host "SMOKE TEST PASSED. Screenshots and logs: $OutputDirectory"
exit 0
