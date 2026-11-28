# Mosaic end-to-end framework (scripts/e2e). Windows PowerShell 5.1 compatible, no modules
# beyond what ships with Windows. See windows/docs/testing.md.
#
# What it does:
#   - starts the published Mosaic.exe with --mosaic-automation on an isolated MOSAIC_DATA_DIR
#     (one fresh instance per area), drives it through the automation pipe (CONTRACT.md);
#   - runs scenarios registered with Register-Scenario; a command the app answers with
#     "Unknown command" (or a dump key that is not there yet) makes the scenario *pending*
#     (reported as skipped, with the reason), so the suite fills in as areas land;
#   - detects crashes (the app exiting, new Windows Error Reporting / Crashpad / Gecko dumps)
#     and hangs (pipe time-outs, IsHungAppWindow), writing a dump of a hung app;
#   - writes results.json, junit.xml and summary.md, with logs, states and screenshots.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:LibDirectory = $PSScriptRoot
$script:E2EDirectory = Split-Path -Parent $PSScriptRoot
$script:ScriptsDirectory = Split-Path -Parent $script:E2EDirectory
# The pipe client is Send-RawCommand below rather than MosaicAutomation.psm1's
# Send-MosaicCommand: that one writes without a time-out, which blocks for as long as a hung
# Mosaic does not read (its pipe has no buffer), so a hang could not be detected.

$script:Utf8 = New-Object System.Text.UTF8Encoding($false)

# ---- types ------------------------------------------------------------------------------------

if (-not ('MosaicE2E.PendingException' -as [type])) {
    Add-Type -TypeDefinition @'
namespace MosaicE2E {
    /// A scenario that cannot run yet: the command or dump field it needs is not in this build.
    public class PendingException : System.Exception { public PendingException(string message) : base(message) { } }
    /// Mosaic stopped answering (pipe time-out with a hung window).
    public class HangException : System.Exception { public HangException(string message) : base(message) { } }
    /// Mosaic exited while a scenario needed it.
    public class CrashException : System.Exception { public CrashException(string message) : base(message) { } }
}
'@
}
if (-not ('MosaicE2E.Win32' -as [type])) {
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -AssemblyName System.Drawing.Common
        $references = @([System.Drawing.Bitmap].Assembly.Location, 'System.Drawing.Primitives', 'System.ComponentModel.Primitives',
            'System.Diagnostics.Process', 'Microsoft.Win32.Primitives', 'System.Collections')
        Add-Type -Path (Join-Path $script:LibDirectory 'Win32.cs') -ReferencedAssemblies $references
    }
    else {
        Add-Type -Path (Join-Path $script:LibDirectory 'Win32.cs') -ReferencedAssemblies System.Drawing
    }
}
if (-not ('MosaicE2E.TestServer' -as [type])) {
    Add-Type -Path (Join-Path $script:LibDirectory 'TestServer.cs')
}

# ---- state ------------------------------------------------------------------------------------

$script:Config = $null          # Initialize-E2E
$script:Session = $null         # the running app (Start-MosaicApp)
$script:Server = $null          # MosaicE2E.TestServer
$script:Scenarios = New-Object System.Collections.ArrayList
$script:Results = New-Object System.Collections.ArrayList
$script:Current = $null         # the result record of the scenario being run
$script:StartedProcessIds = New-Object System.Collections.ArrayList
$script:DumpBaseline = @{}
$script:LaunchCount = 0
$script:PipeSeen = $false       # some launch of this run opened the pipe

function Initialize-E2E {
    <#
    .SYNOPSIS
        Sets the run's options and starts the test web server. Call once, before anything else.
    #>
    param(
        [Parameter(Mandatory = $true)] [string]$AppPath,
        [Parameter(Mandatory = $true)] [string]$OutputDirectory,
        [string]$GeckoExe,
        [string]$GeckoHostMode = 'child',
        [int]$WaitSeconds = 30,
        [int]$LoadSeconds = 90,
        [int]$StartSeconds = 60,
        [int]$BudgetMinutes = 50,
        [string]$DumpDirectory,
        [string[]]$AppArguments = @()
    )
    if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
    foreach ($sub in @('areas', 'data', 'dumps', 'screenshots', 'fixtures')) {
        New-Item -ItemType Directory -Path (Join-Path $OutputDirectory $sub) -Force | Out-Null
    }
    $script:Config = [pscustomobject]@{
        AppPath        = $AppPath
        OutputDirectory = $OutputDirectory
        GeckoExe       = $GeckoExe
        GeckoHostMode  = $GeckoHostMode
        WaitSeconds    = $WaitSeconds
        LoadSeconds    = $LoadSeconds
        StartSeconds   = $StartSeconds
        Deadline       = (Get-Date).AddMinutes($BudgetMinutes)
        DumpDirectory  = $DumpDirectory
        AppArguments   = $AppArguments
        Started        = Get-Date
        SiteDirectory  = (Join-Path $script:E2EDirectory 'site')
        FixtureDirectory = (Join-Path $script:E2EDirectory 'fixtures')
    }
    $script:Server = New-Object MosaicE2E.TestServer($script:Config.SiteDirectory)
    $script:Server.Start()
    Write-Host "==> Test server: $($script:Server.Origin) (alt $($script:Server.AltOrigin))"
    Update-DumpBaseline
    $script:Config
}

function Get-E2EConfig { $script:Config }
function Get-TestServer { $script:Server }
function Get-MosaicSession { $script:Session }

# ---- small helpers ----------------------------------------------------------------------------

function Get-Field {
    <#
    .SYNOPSIS
        Reads a dotted path ("tabsArea.workspaces", "tabs.0.url") from parsed JSON; $null when
        any step is missing. Safe under strict mode.
    #>
    param([Parameter(Position = 0)] $Object, [Parameter(Mandatory = $true, Position = 1)] [string]$Path)
    $current = $Object
    foreach ($step in $Path.Split('.')) {
        if ($null -eq $current) { return $null }
        $index = 0
        if (($current -is [System.Collections.IList]) -and [int]::TryParse($step, [ref]$index)) {
            if ($index -lt 0 -or $index -ge $current.Count) { return $null }
            $current = $current[$index]
            continue
        }
        if ($current -is [System.Collections.IDictionary]) {
            if (-not $current.Contains($step)) { return $null }
            $current = $current[$step]
            continue
        }
        $property = $current.PSObject.Properties[$step]
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }
    # Arrays are written to the pipeline element by element: wrap calls in @() to get an array.
    $current
}

function Write-TextFile {
    param([Parameter(Mandatory = $true)] [string]$Path, [AllowEmptyString()] [string]$Text)
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, $script:Utf8)
}

function New-Token {
    # A short unique word for a scenario's page reports and records.
    'e2e' + ([guid]::NewGuid().ToString('N').Substring(0, 10))
}

function ConvertTo-SafeName {
    param([string]$Text)
    ($Text -replace '[^A-Za-z0-9._-]+', '-').Trim('-')
}

function Write-Step {
    <# Prints a line and keeps it in the current scenario's log. #>
    param([Parameter(Position = 0)] [string]$Text)
    Write-Host "    $Text"
    if ($null -ne $script:Current) { [void]$script:Current.Log.Add(('{0:HH:mm:ss} {1}' -f (Get-Date), $Text)) }
}

function Add-Evidence {
    param([Parameter(Mandatory = $true)] [string]$Path)
    if ($null -ne $script:Current) {
        $relative = $Path
        if ($Path.StartsWith($script:Config.OutputDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relative = $Path.Substring($script:Config.OutputDirectory.Length).TrimStart('\', '/')
        }
        [void]$script:Current.Evidence.Add($relative)
    }
}

function Skip-Pending {
    <# Marks the current scenario pending (skipped) with a reason. #>
    param([Parameter(Mandatory = $true, Position = 0)] [string]$Reason)
    throw (New-Object MosaicE2E.PendingException($Reason))
}

function Assert-True {
    param([Parameter(Position = 0)] $Condition, [Parameter(Mandatory = $true, Position = 1)] [string]$Message)
    if (-not $Condition) { throw "Check failed: $Message" }
}

function Assert-Equal {
    param([Parameter(Position = 0)] $Expected, [Parameter(Position = 1)] $Actual, [Parameter(Mandatory = $true, Position = 2)] [string]$What)
    if ([string]$Expected -ne [string]$Actual) { throw "Check failed: $What is '$Actual', expected '$Expected'." }
}

function Wait-Until {
    <#
    .SYNOPSIS
        Polls a condition (no Mosaic state involved) until it returns something true; returns it.
    #>
    param(
        [Parameter(Mandatory = $true)] [string]$What,
        [Parameter(Mandatory = $true)] [scriptblock]$Condition,
        [int]$TimeoutSeconds = 0,
        [int]$IntervalMilliseconds = 300
    )
    if ($TimeoutSeconds -le 0) { $TimeoutSeconds = $script:Config.WaitSeconds }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $result = @(& $Condition)
        if ($result.Count -gt 0 -and $result[-1]) { return $result[-1] }
        if ((Get-Date) -ge $deadline) { throw "Timed out after $TimeoutSeconds s waiting for: $What." }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
}

# ---- the automation pipe ----------------------------------------------------------------------

function Send-RawCommand {
    # One exchange on the automation pipe (CONTRACT.md), with extra request fields when given
    # (macOS commands such as page.screenshot read "full", page.dialog "text"). Every step has a
    # time-out, the write included: the app's pipe has no buffer, so a write blocks until a hung
    # app reads it. Returns the parsed reply; throws on time-outs and on replies that are not JSON.
    param(
        [Parameter(Mandatory = $true)] [string]$Do,
        [string]$Value,
        [string]$Out,
        [hashtable]$Extra,
        [int]$ConnectTimeoutMilliseconds = 5000,
        [int]$ReplyTimeoutSeconds = 30
    )
    $request = [ordered]@{ do = $Do }
    if ($Value) { $request['value'] = $Value }
    if ($Out) { $request['out'] = $Out }
    if ($Extra) { foreach ($key in $Extra.Keys) { $request[$key] = [string]$Extra[$key] } }
    $json = $request | ConvertTo-Json -Compress
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'mosaic-automation', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        try { $pipe.Connect($ConnectTimeoutMilliseconds) }
        catch [System.TimeoutException] { throw 'No Mosaic is listening on \\.\pipe\mosaic-automation.' }
        $bytes = $script:Utf8.GetBytes($json + "`n")
        $writing = $pipe.WriteAsync($bytes, 0, $bytes.Length)
        if (-not $writing.Wait($ReplyTimeoutSeconds * 1000)) { throw "Mosaic did not read '$Do' within $ReplyTimeoutSeconds s." }
        $reader = New-Object System.IO.StreamReader($pipe, $script:Utf8, $false, 4096, $true)
        $pending = $reader.ReadLineAsync()
        if (-not $pending.Wait($ReplyTimeoutSeconds * 1000)) { throw "Mosaic did not answer '$Do' within $ReplyTimeoutSeconds s." }
        $line = $pending.Result
        if ([string]::IsNullOrWhiteSpace($line)) { throw "Mosaic closed the connection without answering '$Do'." }
    }
    finally { $pipe.Dispose() }
    try { $line | ConvertFrom-Json } catch { throw "Mosaic's answer to '$Do' is not JSON: $line" }
}

function Assert-MosaicRunning {
    if ($null -eq $script:Session -or $null -eq $script:Session.Process) { throw 'Mosaic has not been started.' }
    if ($script:Session.Process.HasExited) {
        $script:Session.Crashed = -not $script:Session.ExpectExit
        throw (New-Object MosaicE2E.CrashException("Mosaic exited unexpectedly (exit code $($script:Session.Process.ExitCode))."))
    }
}

function Test-MosaicHung {
    # After a pipe failure: a crash or a hang is thrown as such; otherwise returns quietly and
    # the caller rethrows the original error.
    param([string]$During)
    Assert-MosaicRunning
    $window = Get-MosaicMainWindow
    if ($window -and [MosaicE2E.Win32]::IsHung($window.Handle, 5000)) {
        $dump = Join-Path $script:Config.OutputDirectory ("dumps\hang-{0}-{1:HHmmss}.dmp" -f (ConvertTo-SafeName $script:Session.Name), (Get-Date))
        $problem = [MosaicE2E.Win32]::WriteDump($script:Session.Process.Id, $dump, $false)
        if ($problem) { Write-Step "hang dump failed: $problem" } else { Add-Evidence $dump; Write-Step "hang dump: $dump" }
        $script:Session.Hung = $true
        throw (New-Object MosaicE2E.HangException("Mosaic's window is not responding (IsHungAppWindow) during $During."))
    }
}

function Invoke-Mosaic {
    <#
    .SYNOPSIS
        Sends a command and insists on {"ok": true}. "Unknown command" makes the scenario
        pending; a time-out is checked for a hang or crash.
    #>
    param(
        [Parameter(Mandatory = $true, Position = 0)] [string]$Do,
        [Parameter(Position = 1)] [string]$Value,
        [string]$Out,
        [hashtable]$Extra,
        [int]$TimeoutSeconds = 30,
        [switch]$AllowError,
        [switch]$Quiet
    )
    Assert-MosaicRunning
    if (-not $script:Session.PipeUp) { Skip-Pending 'Mosaic has no automation pipe in this build.' }
    if (-not $Quiet) {
        $shown = $Value
        if ($shown -and $shown.Length -gt 120) { $shown = $shown.Substring(0, 117) + '...' }
        Write-Step "> $Do $shown"
    }
    try {
        $reply = Send-RawCommand -Do $Do -Value $Value -Out $Out -Extra $Extra -ReplyTimeoutSeconds $TimeoutSeconds
    }
    catch {
        $original = $_.Exception.Message
        Test-MosaicHung -During "'$Do'"
        # Not hung, but the pipe is stuck: the next scenario gets a fresh Mosaic.
        $script:Session.Hung = $true
        throw "Pipe error on '$Do': $original"
    }
    if ((Get-Field $reply 'ok') -ne $true) {
        $problem = [string](Get-Field $reply 'error')
        if ($problem -match '^Unknown command') {
            Skip-Pending "command '$Do' is not implemented on Windows yet ($problem)"
        }
        if ($AllowError) { return $reply }
        throw "Mosaic refused '$Do $Value': $problem"
    }
    $reply
}

function Test-MosaicCommand {
    <#
    .SYNOPSIS
        True if Mosaic knows the command (sends it with a harmless value and looks for
        "Unknown command"). For optional steps; scenarios normally just call Invoke-Mosaic.
    #>
    param([Parameter(Mandatory = $true)] [string]$Do, [string]$Value = '')
    try { $reply = Send-RawCommand -Do $Do -Value $Value -ReplyTimeoutSeconds 15 } catch { return $false }
    -not ([string](Get-Field $reply 'error') -match '^Unknown command')
}

function Get-MosaicState {
    $reply = Invoke-Mosaic -Do 'dump' -Quiet -TimeoutSeconds 20
    $state = Get-Field $reply 'state'
    if ($null -eq $state) { throw "Mosaic's reply to 'dump' has no 'state'." }
    $state
}

function Test-Field {
    <# True when the dotted path exists in the parsed JSON (even if its value is empty or null). #>
    param([Parameter(Position = 0)] $Object, [Parameter(Mandatory = $true, Position = 1)] [string]$Path)
    $steps = $Path.Split('.')
    $parent = $Object
    if ($steps.Count -gt 1) { $parent = Get-Field $Object (($steps[0..($steps.Count - 2)]) -join '.') }
    if ($null -eq $parent -or $parent -is [System.Collections.IList]) { return $false }
    $last = $steps[-1]
    if ($parent -is [System.Collections.IDictionary]) { return $parent.Contains($last) }
    $null -ne $parent.PSObject.Properties[$last]
}

function Require-Field {
    <#
    .SYNOPSIS
        Returns a dump field (arrays element by element, like Get-Field), or makes the scenario
        pending when this build does not have it.
    #>
    param([Parameter(Mandatory = $true, Position = 0)] $State, [Parameter(Mandatory = $true, Position = 1)] [string]$Path, [string]$Owner)
    if (-not (Test-Field $State $Path)) {
        $who = ''
        if ($Owner) { $who = " (the $Owner area registers it)" }
        Skip-Pending "dump has no '$Path' yet$who"
    }
    Get-Field $State $Path
}

function Get-SelectedTab {
    param([Parameter(Mandatory = $true, Position = 0)] $State)
    foreach ($tab in @(Get-Field $State 'tabs')) {
        if ((Get-Field $tab 'selected') -eq $true) { return $tab }
    }
    $null
}

function Get-SelectedIndex {
    param([Parameter(Mandatory = $true, Position = 0)] $State)
    $tabs = @(Get-Field $State 'tabs')
    for ($i = 0; $i -lt $tabs.Count; $i++) { if ((Get-Field $tabs[$i] 'selected') -eq $true) { return $i } }
    -1
}

function Format-State {
    # The parts of the state that explain a failure, on one line.
    param($State)
    if ($null -eq $State) { return '(no state)' }
    $parts = @(
        "engine=$(Get-Field $State 'engine')",
        "tabs=$(@(Get-Field $State 'tabs').Count)",
        "geckoStatus=$(Get-Field $State 'geckoStatus')",
        "geckoAttached=$(Get-Field $State 'geckoAttached')",
        "chromiumAvailable=$(Get-Field $State 'chromiumAvailable')",
        "notice='$(Get-Field $State 'notice')'"
    )
    $tab = Get-SelectedTab $State
    if ($tab) {
        $parts += "tab: engine=$(Get-Field $tab 'engine') loading=$(Get-Field $tab 'loading') title='$(Get-Field $tab 'title')' " +
            "url='$(Get-Field $tab 'url')' failure='$(Get-Field $tab 'failure')'"
    }
    $parts -join ', '
}

function Save-State {
    param($State, [Parameter(Mandatory = $true)] [string]$Name)
    if ($null -eq $State -or $null -eq $script:Session) { return }
    $path = Join-Path $script:Session.LogDirectory ("state-{0}.json" -f (ConvertTo-SafeName $Name))
    Write-TextFile $path ($State | ConvertTo-Json -Depth 12)
    Add-Evidence $path
}

function Wait-State {
    <#
    .SYNOPSIS
        Polls dump until the condition (given the state) is true; returns that state.
    #>
    param(
        [Parameter(Mandatory = $true, Position = 0)] [string]$What,
        [Parameter(Mandatory = $true, Position = 1)] [scriptblock]$Condition,
        [int]$TimeoutSeconds = 0,
        [int]$IntervalMilliseconds = 400
    )
    if ($TimeoutSeconds -le 0) { $TimeoutSeconds = $script:Config.WaitSeconds }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $state = $null
    while ($true) {
        $state = Get-MosaicState
        $result = @(& $Condition $state)
        if ($result.Count -gt 0 -and $result[-1]) { return $state }
        if ((Get-Date) -ge $deadline) { break }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
    Save-State $state "timeout-$What"
    throw "Timed out after $TimeoutSeconds s waiting for: $What. Last state: $(Format-State $state)"
}

function Wait-OutputFile {
    <#
    .SYNOPSIS
        Waits for a command's "out" file (macOS commands write it asynchronously) and returns
        its text, or its parsed JSON with -Json.
    #>
    param([Parameter(Mandatory = $true, Position = 0)] [string]$Path, [switch]$Json, [int]$TimeoutSeconds = 0, [string]$What)
    if (-not $What) { $What = "the file $Path" }
    $null = Wait-Until -What $What -TimeoutSeconds $TimeoutSeconds -Condition {
        (Test-Path -LiteralPath $Path) -and (Get-Item -LiteralPath $Path).Length -gt 0
    }
    Start-Sleep -Milliseconds 150   # a writer that is not atomic
    Add-Evidence $Path
    $text = [System.IO.File]::ReadAllText($Path)
    if ($Json) { return ($text | ConvertFrom-Json) }
    $text
}

function New-OutputPath {
    <# A path in the current area's folder for a command's output file. #>
    param([Parameter(Mandatory = $true, Position = 0)] [string]$Name)
    $path = Join-Path $script:Session.LogDirectory (ConvertTo-SafeName $Name)
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    $path
}

# ---- test pages -------------------------------------------------------------------------------

function Get-TestUrl {
    param([Parameter(Mandatory = $true, Position = 0)] [string]$Path, [switch]$Alt)
    if (-not $Path.StartsWith('/')) { $Path = '/' + $Path }
    if ($Alt) { return $script:Server.AltOrigin + $Path }
    $script:Server.Origin + $Path
}

function Get-TestRequests {
    param([string]$PathPrefix = '/', [datetime]$Since = [datetime]::MinValue)
    $sinceUtc = $Since.ToUniversalTime()
    @($script:Server.RequestsSnapshot() | Where-Object { $_.Path.StartsWith($PathPrefix) -and $_.Time -ge $sinceUtc })
}

function Get-TestReports {
    param([string]$Token, [string]$Key)
    @($script:Server.ReportsSnapshot() | Where-Object {
            ((-not $Token) -or $_.Token -eq $Token) -and ((-not $Key) -or $_.Key -eq $Key)
        })
}

function Wait-Report {
    <#
    .SYNOPSIS
        Waits until a test page reports <Key> (with the scenario's token); returns the value.
        -Value waits for that value specifically.
    #>
    param([Parameter(Mandatory = $true)] [string]$Token, [Parameter(Mandatory = $true)] [string]$Key,
        [string]$Value, [int]$TimeoutSeconds = 0, [string]$What)
    if (-not $What) { $What = "the page to report '$Key'" + $(if ($Value) { "=$Value" } else { '' }) }
    $found = Wait-Until -What $What -TimeoutSeconds $TimeoutSeconds -Condition {
        $reports = @(Get-TestReports -Token $Token -Key $Key)
        if ($Value) { $reports = @($reports | Where-Object { $_.Value -eq $Value }) }
        if ($reports.Count -gt 0) { $reports[-1] } else { $null }
    }
    Write-Step "page reported $Key=$($found.Value)"
    $found.Value
}

function Test-TabShows {
    # True when a tab shows the page: loaded, with the title (if given), URL (prefix) and engine.
    param($Tab, [string]$Url, [string]$Title, [string]$Engine)
    if ($null -eq $Tab) { return $false }
    if ((Get-Field $Tab 'loading') -ne $false) { return $false }
    if ($Engine -and (Get-Field $Tab 'engine') -ne $Engine) { return $false }
    if ($Title -and (Get-Field $Tab 'title') -ne $Title) { return $false }
    if ($Url) {
        $actual = ([string](Get-Field $Tab 'url')).TrimEnd('/')
        if (-not $actual.StartsWith($Url.TrimEnd('/'), [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    }
    $true
}

function Set-Engine {
    <# Switches the selected tab to an engine and waits until it reports that engine. #>
    param([Parameter(Mandatory = $true, Position = 0)] [ValidateSet('gecko', 'chromium')] [string]$Engine)
    $state = Get-MosaicState
    $tab = Get-SelectedTab $state
    if ($tab -and (Get-Field $tab 'engine') -eq $Engine) { return $state }
    if ($Engine -eq 'chromium' -and (Get-Field $state 'chromiumAvailable') -eq $false) {
        throw 'Chromium is unavailable (chromiumAvailable is false); is the WebView2 runtime installed?'
    }
    Invoke-Mosaic 'engine' $Engine | Out-Null
    Wait-State "the selected tab to be on $Engine" -TimeoutSeconds $script:Config.LoadSeconds -Condition {
        param($s) (Get-Field (Get-SelectedTab $s) 'engine') -eq $Engine
    }
}

function Open-TestPage {
    <#
    .SYNOPSIS
        Opens a page of the test site in the selected tab (or a new tab) on an engine and waits
        until it has loaded. Returns the state.
    #>
    param(
        [Parameter(Mandatory = $true, Position = 0)] [string]$Path,
        [Parameter(Position = 1)] [string]$Engine,
        [string]$Title,
        [switch]$NewTab,
        [switch]$Alt
    )
    $url = Get-TestUrl $Path -Alt:$Alt
    if ($NewTab) {
        Invoke-Mosaic 'newTab' $url | Out-Null
        if ($Engine) {
            $state = Get-MosaicState
            if ((Get-Field (Get-SelectedTab $state) 'engine') -ne $Engine) { Invoke-Mosaic 'engine' $Engine | Out-Null }
        }
    }
    else {
        if ($Engine) { Set-Engine $Engine | Out-Null }
        Invoke-Mosaic 'navigate' $url | Out-Null
    }
    $what = "the selected tab to load $url"
    if ($Engine) { $what += " on $Engine" }
    if ($Title) { $what += " (title '$Title')" }
    Wait-State $what -TimeoutSeconds $script:Config.LoadSeconds -Condition {
        param($s) Test-TabShows (Get-SelectedTab $s) -Url $url -Title $Title -Engine $Engine
    }
}

function Reset-Tabs {
    <# Closes every tab but one and shows a blank test page in it, on the given engine. #>
    param([string]$Engine = 'gecko')
    $state = Get-MosaicState
    $count = @(Get-Field $state 'tabs').Count
    for ($i = 1; $i -lt $count; $i++) { Invoke-Mosaic 'closeTab' -Quiet | Out-Null }
    Open-TestPage '/page?title=Blank' -Engine $Engine -Title 'Blank' | Out-Null
}

# ---- windows, screenshots, images -------------------------------------------------------------

function Get-MosaicMainWindow {
    # Mosaic's main window: the largest visible top-level window of the process.
    if ($null -eq $script:Session -or $script:Session.Process.HasExited) { return $null }
    $windows = @([MosaicE2E.Win32]::TopLevelWindows(@($script:Session.Process.Id)) | Where-Object { $_.Visible -and $_.Width -gt 100 -and $_.Height -gt 100 })
    if ($windows.Count -eq 0) { return $null }
    $windows | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
}

function Get-ImageStats {
    <# Width, Height and DistinctColours (sampled) of a PNG, or $null. #>
    param([Parameter(Mandatory = $true)] [string]$Path)
    $stats = [MosaicE2E.Win32]::ImageStats($Path)
    if ($null -eq $stats) { return $null }
    [pscustomobject]@{ Width = $stats[0]; Height = $stats[1]; DistinctColours = $stats[2] }
}

function Save-Screenshot {
    <#
    .SYNOPSIS
        A PNG of Mosaic's window: the app's own "screenshot" command, or (no pipe, or not
        implemented) a capture of its window from outside. Returns the path, or $null.
    #>
    param([Parameter(Mandatory = $true, Position = 0)] [string]$Name, [string]$Directory, [switch]$NoFallback)
    if (-not $Directory) { $Directory = Join-Path $script:Config.OutputDirectory 'screenshots' }
    if (-not (Test-Path -LiteralPath $Directory)) { New-Item -ItemType Directory -Path $Directory -Force | Out-Null }
    $prefix = ''
    if ($script:Session) { $prefix = (ConvertTo-SafeName $script:Session.Name) + '-' }
    $path = Join-Path $Directory ($prefix + (ConvertTo-SafeName $Name) + '.png')
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    $source = 'app'
    $problem = $null
    if ($script:Session -and $script:Session.PipeUp -and -not $script:Session.Process.HasExited) {
        try {
            $reply = Send-RawCommand -Do 'screenshot' -Value $path -ReplyTimeoutSeconds 30
            if ((Get-Field $reply 'ok') -ne $true) { $problem = [string](Get-Field $reply 'error') }
            else {
                $deadline = (Get-Date).AddSeconds(10)
                while (-not ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -gt 0)) {
                    if ((Get-Date) -ge $deadline) { $problem = 'no file was written'; break }
                    Start-Sleep -Milliseconds 250
                }
            }
        }
        catch { $problem = $_.Exception.Message }
    }
    else { $problem = 'no automation pipe' }
    if ($problem) {
        if ($NoFallback) { Write-Step "screenshot '$Name' failed: $problem"; return $null }
        $window = Get-MosaicMainWindow
        if ($null -eq $window) { Write-Step "screenshot '$Name' failed: $problem, and Mosaic has no visible window"; return $null }
        $capture = [MosaicE2E.Win32]::CaptureWindow($window.Handle, $path)
        if ($capture) { Write-Step "screenshot '$Name' failed: $problem; capture from outside failed too: $capture"; return $null }
        $source = 'capture'
    }
    Write-Step "screenshot ($source): $path"
    Add-Evidence $path
    $path
}

function Save-WindowTree {
    # Every native window inside Mosaic's main window, with class, process and place.
    param([Parameter(Mandatory = $true)] [string]$Name)
    $window = Get-MosaicMainWindow
    if ($null -eq $window) { return $null }
    $lines = @("root $($window.ToString())")
    foreach ($child in [MosaicE2E.Win32]::ChildWindows($window.Handle)) { $lines += $child.ToString() }
    $path = Join-Path $script:Session.LogDirectory ("windows-{0}.txt" -f (ConvertTo-SafeName $Name))
    Write-TextFile $path (($lines -join "`r`n") + "`r`n")
    Add-Evidence $path
    $path
}

function Get-GeckoWindows {
    <# The native windows of the Gecko process inside Mosaic's window (to check it is hidden or shown). #>
    param([Parameter(Mandatory = $true)] $State)
    $geckoId = [int](Get-Field $State 'geckoProcessId')
    $window = Get-MosaicMainWindow
    if ($geckoId -le 0 -or $null -eq $window) { return @() }
    @([MosaicE2E.Win32]::ChildWindows($window.Handle) | Where-Object { $_.ProcessId -eq $geckoId })
}

# ---- processes, crashes and dumps -------------------------------------------------------------

function Get-ProcessSnapshot {
    # Every process that belongs to this app instance: Mosaic's process tree, Gecko's tree (its
    # launcher may have exited, cutting it off from Mosaic's tree) and any engine process whose
    # command line names the instance's data folder.
    param([int[]]$Roots, [string]$DataDirectory)
    $all = @(Get-CimInstance -ClassName Win32_Process)
    $byId = @{}
    foreach ($process in $all) { $byId[[int]$process.ProcessId] = $process }
    $found = @{}
    $queue = New-Object System.Collections.Queue
    foreach ($root in $Roots) { if ($root -gt 0 -and $byId.ContainsKey($root)) { $queue.Enqueue($byId[$root]) } }
    foreach ($process in $all) {
        $isEngine = $process.Name -eq 'firefox.exe' -or $process.Name -eq 'msedgewebview2.exe'
        if ($isEngine -and $DataDirectory -and $process.CommandLine -and
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
            if ([int]$child.ParentProcessId -eq $parentId -and $child.CreationDate -ge $parent.CreationDate) { $queue.Enqueue($child) }
        }
    }
    @($found.Values | ForEach-Object { [pscustomobject]@{ Id = [int]$_.ProcessId; Name = $_.Name; Created = $_.CreationDate } })
}

function Get-Survivors {
    param($Snapshot)
    $survivors = @()
    foreach ($entry in @($Snapshot)) {
        $current = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($entry.Id)" -ErrorAction SilentlyContinue
        if ($current -and $current.CreationDate -eq $entry.Created) { $survivors += $entry }
    }
    $survivors
}

function Get-DumpRoots {
    $roots = @()
    if ($script:Config.DumpDirectory -and (Test-Path -LiteralPath $script:Config.DumpDirectory)) { $roots += $script:Config.DumpDirectory }
    $data = Join-Path $script:Config.OutputDirectory 'data'
    if (Test-Path -LiteralPath $data) { $roots += $data }
    # WER's default LocalDumps folder, and its report folders (Report.wer names the faulting
    # module and, for WinUI's stowed exceptions, the error; some reports carry a dump).
    $candidates = @()
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'CrashDumps'), (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\WER\ReportArchive'), (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\WER\ReportQueue') }
    if ($env:ProgramData) { $candidates += (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportArchive'), (Join-Path $env:ProgramData 'Microsoft\Windows\WER\ReportQueue') }
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate) { $roots += $candidate } }
    $roots
}

function Get-DumpFiles {
    # Crash dumps that may concern this run: WER LocalDumps (Enable-CrashDumps.ps1), those the
    # engines write inside the data folders (WebView2's Crashpad, Gecko's minidumps), and WER
    # reports of the app or its engines.
    $files = @()
    foreach ($root in Get-DumpRoots) {
        foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -Force -File -Include '*.dmp', '*.mdmp', '*.hdmp', 'Report.wer' -ErrorAction SilentlyContinue)) {
            if ($file.Name -eq 'Report.wer' -or $file.FullName -like '*\WER\*') {
                # Only reports about Mosaic or its engines (WER folders are named after the exe).
                if ($file.Directory.Name -notmatch '(?i)mosaic|msedgewebview2|firefox|powershell') { continue }
            }
            $files += $file
        }
    }
    $files
}

function Update-DumpBaseline {
    $script:DumpBaseline = @{}
    foreach ($file in Get-DumpFiles) { $script:DumpBaseline[$file.FullName] = $true }
}

function Get-NewDumps {
    <# Dumps and WER reports that appeared since the last call; copied into the output's dumps folder. #>
    $new = @()
    foreach ($file in Get-DumpFiles) {
        if ($script:DumpBaseline.ContainsKey($file.FullName)) { continue }
        $script:DumpBaseline[$file.FullName] = $true
        $name = $file.Name
        if ($name -eq 'Report.wer' -or $file.FullName -like '*\WER\*') { $name = $file.Directory.Name + '-' + $file.Name }
        $target = Join-Path $script:Config.OutputDirectory ('dumps\' + $name)
        if ($file.FullName -ne $target) {
            try { Copy-Item -LiteralPath $file.FullName -Destination $target -Force } catch { $target = $file.FullName }
        }
        $new += [pscustomobject]@{ Name = $name; Path = $target; Source = $file.FullName; Size = $file.Length; IsReport = ($file.Name -eq 'Report.wer') }
    }
    $new
}

function Wait-NewDumps {
    <# After a crash: WER writes its dump a few seconds later, so wait a little for it. #>
    param([int]$Seconds = 15)
    $deadline = (Get-Date).AddSeconds($Seconds)
    $found = @(Get-NewDumps)
    while (@($found | Where-Object { -not $_.IsReport }).Count -eq 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
        $found += @(Get-NewDumps)
    }
    $found
}

function Save-EventLog {
    # Unhandled .NET exceptions and native faults of the run, from the Application log.
    param([datetime]$Since, [string]$Path)
    try {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $Since; Level = 1, 2 } -ErrorAction Stop |
                Where-Object { $_.ProviderName -eq '.NET Runtime' -or $_.ProviderName -eq 'Application Error' -or $_.ProviderName -eq 'Windows Error Reporting' -or $_.ProviderName -eq 'Application Hang' })
        if ($events.Count -gt 0) {
            Write-TextFile $Path ($events | Format-List TimeCreated, ProviderName, Id, Message | Out-String -Width 4096)
            return $events.Count
        }
    }
    catch { }
    0
}

# ---- the app ----------------------------------------------------------------------------------

function Start-MosaicApp {
    <#
    .SYNOPSIS
        Starts Mosaic for an area on its own data folder and waits for the automation pipe.
        -KeepData reuses the folder (restart checks: sessions, crash recovery).
    #>
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [switch]$KeepData,
        [string[]]$ExtraArguments = @(),
        [hashtable]$Environment = @{}
    )
    $script:LaunchCount++
    $dataDirectory = Join-Path $script:Config.OutputDirectory ('data\' + (ConvertTo-SafeName $Name))
    $logDirectory = Join-Path $script:Config.OutputDirectory ('areas\' + (ConvertTo-SafeName $Name))
    if (-not $KeepData -and (Test-Path -LiteralPath $dataDirectory)) { Remove-Item -LiteralPath $dataDirectory -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $dataDirectory, $logDirectory -Force | Out-Null

    # The pipe name is fixed: a Mosaic still listening would be driven by mistake.
    $other = $null
    try { $other = Send-RawCommand -Do 'dump' -ConnectTimeoutMilliseconds 300 -ReplyTimeoutSeconds 5 } catch { }
    if ($other) {
        foreach ($id in @($script:StartedProcessIds)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 2
        $other = $null
        try { $other = Send-RawCommand -Do 'dump' -ConnectTimeoutMilliseconds 300 -ReplyTimeoutSeconds 5 } catch { }
        if ($other) { throw 'Another Mosaic is listening on \\.\pipe\mosaic-automation (not started by this run). Quit it first.' }
    }

    $n = $script:LaunchCount
    $saved = @{}
    $variables = @{
        MOSAIC_DATA_DIR   = $dataDirectory
        MOSAIC_TRACE_FILE = (Join-Path $logDirectory "mosaic-trace-$n.log")
    }
    if ($script:Config.GeckoExe) { $variables['MOSAIC_GECKO_EXE'] = $script:Config.GeckoExe }
    foreach ($key in $Environment.Keys) { $variables[$key] = $Environment[$key] }
    foreach ($key in $variables.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$variables[$key], 'Process')
    }
    try {
        $arguments = @('--mosaic-automation') + @($script:Config.AppArguments) + @($ExtraArguments)
        if ($script:Config.GeckoHostMode -eq 'owned') { $arguments += '--gecko-host=owned' }
        $file = $script:Config.AppPath
        if ($file.EndsWith('.ps1')) {
            # The framework's self-test drives a stand-in written in PowerShell.
            $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $file + '"')) + $arguments
            $file = (Get-Command powershell.exe).Source
        }
        $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru `
            -RedirectStandardOutput (Join-Path $logDirectory "mosaic-stdout-$n.log") `
            -RedirectStandardError (Join-Path $logDirectory "mosaic-stderr-$n.log")
        $null = $process.Handle   # makes ExitCode available later
    }
    finally {
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
    }
    [void]$script:StartedProcessIds.Add($process.Id)

    $script:Session = [pscustomobject]@{
        Name          = $Name
        Process       = $process
        DataDirectory = $dataDirectory
        LogDirectory  = $logDirectory
        Started       = Get-Date
        PipeUp        = $false
        PipeSeconds   = -1
        StartError    = ''
        ExpectExit    = $false
        Crashed       = $false
        Hung          = $false
        StartCrashed  = $false
        GeckoProcessId = 0
    }
    Write-Host "==> Mosaic for '$Name' (process $($process.Id)), data in $dataDirectory"

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    # A build without the pipe (the first launch never opened it) is not waited for in full
    # again for every area.
    $wait = $script:Config.StartSeconds
    if (-not $script:PipeSeen -and $script:LaunchCount -gt 1) { $wait = [math]::Min($wait, 15) }
    $deadline = (Get-Date).AddSeconds($wait)
    while ($true) {
        if ($process.HasExited) {
            $script:Session.StartError = ('Mosaic exited during start-up (exit code {0}, 0x{1:X8}).' -f $process.ExitCode, $process.ExitCode)
            $script:Session.Crashed = $true
            $script:Session.StartCrashed = $true
            $script:Session.ExpectExit = $true
            break
        }
        try {
            $reply = Send-RawCommand -Do 'dump' -ConnectTimeoutMilliseconds 1000 -ReplyTimeoutSeconds 15
            $script:Session.PipeUp = $true
            $script:PipeSeen = $true
            $script:Session.PipeSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
            $gecko = Get-Field $reply 'state.geckoProcessId'
            if ($gecko) { $script:Session.GeckoProcessId = [int]$gecko }
            break
        }
        catch {
            if ((Get-Date) -ge $deadline) {
                $script:Session.StartError = "Mosaic did not open its automation pipe within $wait s ($($_.Exception.Message))."
                break
            }
            Start-Sleep -Milliseconds 500
        }
    }
    if ($script:Session.PipeUp) { Write-Host "    automation pipe up after $($script:Session.PipeSeconds) s" }
    else { Write-Host "    $($script:Session.StartError)" }
    $script:Session
}

function Stop-MosaicApp {
    <#
    .SYNOPSIS
        Quits Mosaic (or kills it with -Kill) and makes sure none of its processes are left.
        Returns @{ Quit; ExitCode; Survivors } where Survivors lists processes that were still
        running 20 s after Mosaic exited (they are then stopped).
    #>
    param([switch]$Kill)
    $result = [pscustomobject]@{ Quit = $false; ExitCode = $null; Survivors = @(); Message = '' }
    if ($null -eq $script:Session) { return $result }
    $session = $script:Session
    $session.ExpectExit = $true
    $roots = @($session.Process.Id)
    if ($session.GeckoProcessId -gt 0) { $roots += $session.GeckoProcessId }
    if ($session.PipeUp -and -not $session.Process.HasExited) {
        try {
            $gecko = Get-Field (Send-RawCommand -Do 'dump' -ReplyTimeoutSeconds 10) 'state.geckoProcessId'
            if ($gecko -and [int]$gecko -gt 0) { $roots += [int]$gecko }
        }
        catch { }
    }
    $snapshot = @(Get-ProcessSnapshot -Roots $roots -DataDirectory $session.DataDirectory)
    if (-not $session.Process.HasExited) {
        if (-not $Kill -and $session.PipeUp) {
            try { Send-RawCommand -Do 'quit' -ReplyTimeoutSeconds 10 | Out-Null } catch { }
            if ($session.Process.WaitForExit(30000)) { $result.Quit = $true }
            else { $result.Message = 'Mosaic was still running 30 s after "quit".' }
        }
        if (-not $session.Process.HasExited) {
            Stop-Process -Id $session.Process.Id -Force -ErrorAction SilentlyContinue
            $session.Process.WaitForExit(10000) | Out-Null
        }
    }
    try { $result.ExitCode = $session.Process.ExitCode } catch { }
    $deadline = (Get-Date).AddSeconds(20)
    $survivors = @(Get-Survivors $snapshot)
    while ($survivors.Count -gt 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $survivors = @(Get-Survivors $snapshot)
    }
    $result.Survivors = $survivors
    foreach ($entry in $survivors) { Stop-Process -Id $entry.Id -Force -ErrorAction SilentlyContinue }
    $script:Session = $null
    Start-Sleep -Milliseconds 500   # let file handles close before the data folder is reused
    $result
}

function Restart-MosaicApp {
    <#
    .SYNOPSIS
        Stops the current instance (quit, or -Kill to simulate a crash) and starts it again on
        the same data folder. Returns the new session.
    #>
    param([switch]$Kill, [string[]]$ExtraArguments = @())
    $name = $script:Session.Name
    $stop = Stop-MosaicApp -Kill:$Kill
    if (-not $Kill -and -not $stop.Quit) { Write-Step "previous instance did not quit cleanly: $($stop.Message)" }
    # A kill on purpose is not a crash: forget dumps it may have produced.
    if ($Kill) { $null = Get-NewDumps }
    $session = Start-MosaicApp -Name $name -KeepData -ExtraArguments $ExtraArguments
    if (-not $session.PipeUp) { throw "Mosaic did not come back after the restart: $($session.StartError)" }
    $session
}

# ---- scenarios --------------------------------------------------------------------------------

function Register-Scenario {
    <#
    .SYNOPSIS
        Adds a scenario. -Area groups scenarios (one app instance per area); -Tags select
        subsets (smoke = the ARM64 subset); -NoPipe for checks that do not need the pipe.
    #>
    param(
        [Parameter(Mandatory = $true)] [string]$Area,
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [scriptblock]$Body,
        [string[]]$Tags = @(),
        [string]$Todo = '',
        [switch]$NoPipe
    )
    [void]$script:Scenarios.Add([pscustomobject]@{
            Area = $Area; Name = $Name; Body = $Body; Tags = $Tags; Todo = $Todo; NoPipe = [bool]$NoPipe
        })
}

function Get-Scenarios { $script:Scenarios.ToArray() }

function New-ResultRecord {
    param([string]$Area, [string]$Name, [string]$Todo)
    [pscustomobject]@{
        Area     = $Area
        Name     = $Name
        Todo     = $Todo
        Status   = 'passed'
        Kind     = ''
        Message  = ''
        Seconds  = 0.0
        Started  = (Get-Date).ToString('o')
        Evidence = New-Object System.Collections.Generic.List[string]
        Log      = New-Object System.Collections.Generic.List[string]
    }
}

function Add-Result {
    param([Parameter(Mandatory = $true)] $Record)
    [void]$script:Results.Add($Record)
    $mark = switch ($Record.Status) { 'passed' { 'PASS' } 'failed' { 'FAIL' } default { 'SKIP' } }
    $text = "  [$mark] $($Record.Area) / $($Record.Name)"
    if ($Record.Message) { $text += " -- $($Record.Message)" }
    Write-Host $text
}

function Get-Results { $script:Results.ToArray() }

function Save-FailureEvidence {
    param([string]$Name)
    try {
        if ($script:Session -and $script:Session.PipeUp -and -not $script:Session.Process.HasExited) {
            try {
                $reply = Send-RawCommand -Do 'dump' -ReplyTimeoutSeconds 10
                Save-State (Get-Field $reply 'state') "failure-$Name"
            }
            catch { }
        }
        if ($script:Session -and -not $script:Session.Process.HasExited) {
            $null = Save-Screenshot "failure-$Name"
            $null = Save-WindowTree "failure-$Name"
        }
    }
    catch { Write-Step "could not save failure evidence: $($_.Exception.Message)" }
}

function Invoke-Scenario {
    param([Parameter(Mandatory = $true)] $Scenario)
    $record = New-ResultRecord -Area $Scenario.Area -Name $Scenario.Name -Todo $Scenario.Todo
    $script:Current = $record
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "--> $($Scenario.Area) / $($Scenario.Name)"
    try {
        if ((Get-Date) -ge $script:Config.Deadline) { Skip-Pending 'the run''s time budget is used up' }
        if ($null -ne $script:Session -and $script:Session.StartCrashed) { Skip-Pending "blocked: $($script:Session.StartError) (reported once, in this area's '(start)' result)" }
        if (-not $Scenario.NoPipe) {
            if ($null -eq $script:Session) { throw 'Mosaic is not running.' }
            if (-not $script:Session.PipeUp) { Skip-Pending "no automation pipe: $($script:Session.StartError)" }
        }
        $null = & $Scenario.Body
    }
    catch {
        $exception = $_.Exception
        while ($exception -is [System.Management.Automation.RuntimeException] -and $exception.InnerException -and
            ($exception.InnerException -is [MosaicE2E.PendingException] -or $exception.InnerException -is [MosaicE2E.HangException] -or $exception.InnerException -is [MosaicE2E.CrashException])) {
            $exception = $exception.InnerException
        }
        if ($exception -is [MosaicE2E.PendingException]) {
            $record.Status = 'skipped'
            $record.Kind = 'pending'
            $record.Message = $exception.Message
        }
        else {
            $record.Status = 'failed'
            $record.Message = $exception.Message
            if ($exception -is [MosaicE2E.HangException]) { $record.Kind = 'hang' }
            elseif ($exception -is [MosaicE2E.CrashException]) { $record.Kind = 'crash' }
            else {
                $record.Kind = 'failure'
                $where = $_.InvocationInfo.PositionMessage
                if ($where) { [void]$record.Log.Add($where) }
            }
            Save-FailureEvidence (ConvertTo-SafeName $Scenario.Name)
        }
    }
    # A crash nobody noticed (the scenario ended, then the app died), and new dumps.
    if ($script:Session -and $script:Session.Process.HasExited -and -not $script:Session.ExpectExit) {
        $script:Session.Crashed = $true
        if ($record.Status -ne 'failed') {
            $record.Status = 'failed'
            $record.Kind = 'crash'
            $record.Message = "Mosaic exited during the scenario (exit code $($script:Session.Process.ExitCode))."
        }
    }
    if ($record.Kind -eq 'crash') { $dumps = @(Wait-NewDumps) } else { $dumps = @(Get-NewDumps) }
    if ($dumps.Count -gt 0) {
        foreach ($dump in $dumps) { $record.Evidence.Add('dumps\' + $dump.Name); [void]$record.Log.Add("new crash dump: $($dump.Source)") }
        if ($record.Status -ne 'failed') {
            $record.Status = 'failed'
            $record.Kind = 'crash'
            $record.Message = "New crash dump(s): $(($dumps | ForEach-Object { $_.Name }) -join ', ')"
        }
    }
    $record.Seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
    $script:Current = $null
    Add-Result $record
    $record
}

function Invoke-Area {
    <#
    .SYNOPSIS
        Runs one area's scenarios on a fresh Mosaic, restarting it after a crash or hang, then
        quits it and checks that nothing is left running.
    #>
    param([Parameter(Mandatory = $true)] [string]$Area, [Parameter(Mandatory = $true)] $Scenarios)
    Write-Host ''
    Write-Host "=== $Area ($(@($Scenarios).Count) scenario(s)) ==="
    $areaStarted = Get-Date
    if ((Get-Date) -lt $script:Config.Deadline) {
        $null = Start-MosaicApp -Name $Area
        if ($script:Session.StartCrashed) {
            $record = New-ResultRecord -Area $Area -Name '(start) Mosaic starts with --mosaic-automation'
            $record.Status = 'failed'; $record.Kind = 'crash'; $record.Message = $script:Session.StartError
            $script:Current = $record
            foreach ($dump in @(Wait-NewDumps)) { $record.Evidence.Add('dumps\' + $dump.Name) }
            $record.Evidence.Add('areas\' + (ConvertTo-SafeName $Area) + '\events.txt')
            $null = Save-Screenshot 'start-crash'
            $script:Current = $null
            Add-Result $record
        }
    }
    foreach ($scenario in $Scenarios) {
        if ($null -ne $script:Session -and $script:Session.StartCrashed) { $null = Invoke-Scenario $scenario; continue }
        if ($null -eq $script:Session -or $script:Session.Process.HasExited -or $script:Session.Hung) {
            if ($null -ne $script:Session) {
                Write-Host '    restarting Mosaic (it crashed, hung or was stopped)'
                $null = Stop-MosaicApp -Kill
            }
            if ((Get-Date) -lt $script:Config.Deadline) { $null = Start-MosaicApp -Name $Area }
        }
        $null = Invoke-Scenario $scenario
    }
    # Teardown: quit must end Mosaic and both engines (a hang on quit shows here).
    if ($null -ne $script:Session) {
        $record = New-ResultRecord -Area $Area -Name '(teardown) quits cleanly, no processes left'
        $script:Current = $record
        $hadPipe = $script:Session.PipeUp
        $stop = Stop-MosaicApp
        if (-not $hadPipe) {
            $record.Status = 'skipped'; $record.Kind = 'pending'; $record.Message = 'no automation pipe, so no "quit"'
        }
        elseif (-not $stop.Quit) {
            $record.Status = 'failed'; $record.Kind = 'hang'; $record.Message = $stop.Message
        }
        elseif (@($stop.Survivors).Count -gt 0) {
            $record.Status = 'failed'; $record.Kind = 'failure'
            $record.Message = 'Processes left running 20 s after Mosaic quit: ' + (($stop.Survivors | ForEach-Object { "$($_.Name) ($($_.Id))" }) -join ', ')
        }
        $dumps = @(Get-NewDumps)
        if ($dumps.Count -gt 0) {
            foreach ($dump in $dumps) { $record.Evidence.Add('dumps\' + $dump.Name) }
            $record.Status = 'failed'; $record.Kind = 'crash'; $record.Message = "New crash dump(s) at quit: $(($dumps | ForEach-Object { $_.Name }) -join ', ')"
        }
        $script:Current = $null
        Add-Result $record
    }
    $events = Join-Path $script:Config.OutputDirectory ("areas\{0}\events.txt" -f (ConvertTo-SafeName $Area))
    $count = Save-EventLog -Since $areaStarted -Path $events
    if ($count -gt 0) { Write-Host "    $count error event(s) in the Application log: $events" }
}

function Invoke-E2ESuite {
    <#
    .SYNOPSIS
        Runs the registered scenarios, area by area (in -Areas order when given).
    #>
    param([string[]]$Areas = @(), [string[]]$Tags = @(), [string]$NameFilter = '')
    $selected = @($script:Scenarios | Where-Object {
            $scenario = $_
            ($Areas.Count -eq 0 -or $Areas -contains $scenario.Area) -and
            ($Tags.Count -eq 0 -or @($scenario.Tags | Where-Object { $Tags -contains $_ }).Count -gt 0) -and
            (-not $NameFilter -or $scenario.Name -like "*$NameFilter*")
        })
    $order = @()
    if ($Areas.Count -gt 0) { $order = $Areas } else { foreach ($s in $selected) { if ($order -notcontains $s.Area) { $order += $s.Area } } }
    foreach ($area in $order) {
        $scenarios = @($selected | Where-Object { $_.Area -eq $area })
        if ($scenarios.Count -gt 0) { Invoke-Area -Area $area -Scenarios $scenarios }
    }
}

function Stop-E2E {
    <# Stops the app (if any), every process this run started, and the test server. #>
    if ($null -ne $script:Session) { try { $null = Stop-MosaicApp -Kill } catch { } }
    foreach ($id in @($script:StartedProcessIds)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    if ($null -ne $script:Server) { $script:Server.Stop() }
}

# ---- reports ----------------------------------------------------------------------------------

function ConvertTo-XmlText {
    param([AllowNull()] [string]$Text)
    if ($null -eq $Text) { return '' }
    $clean = [regex]::Replace($Text, '[^\x09\x0A\x0D\x20-\uD7FF\uE000-\uFFFD]', '?')
    [System.Security.SecurityElement]::Escape($clean)
}

function Write-E2EReports {
    <#
    .SYNOPSIS
        Writes results.json, junit.xml and summary.md into the output folder. Returns the
        counts.
    #>
    param([string]$Title = 'Mosaic end-to-end', [hashtable]$Info = @{})
    $out = $script:Config.OutputDirectory
    $results = @($script:Results)
    $passed = @($results | Where-Object { $_.Status -eq 'passed' }).Count
    $failed = @($results | Where-Object { $_.Status -eq 'failed' }).Count
    $skipped = @($results | Where-Object { $_.Status -eq 'skipped' }).Count

    $json = [ordered]@{
        title    = $Title
        started  = $script:Config.Started.ToString('o')
        finished = (Get-Date).ToString('o')
        app      = $script:Config.AppPath
        info     = $Info
        counts   = [ordered]@{ passed = $passed; failed = $failed; skipped = $skipped; total = $results.Count }
        results  = @($results | ForEach-Object {
                [ordered]@{
                    area = $_.Area; name = $_.Name; status = $_.Status; kind = $_.Kind; message = $_.Message
                    seconds = $_.Seconds; todo = $_.Todo; evidence = @($_.Evidence); log = @($_.Log)
                }
            })
    }
    Write-TextFile (Join-Path $out 'results.json') ($json | ConvertTo-Json -Depth 8)

    $areas = @()
    foreach ($r in $results) { if ($areas -notcontains $r.Area) { $areas += $r.Area } }
    $xml = New-Object System.Text.StringBuilder
    $total = 0.0
    foreach ($r in $results) { $total += $r.Seconds }
    [void]$xml.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$xml.AppendLine(('<testsuites name="{0}" tests="{1}" failures="{2}" skipped="{3}" time="{4}">' -f (ConvertTo-XmlText $Title), $results.Count, $failed, $skipped, [string]::Format([cultureinfo]::InvariantCulture, '{0:0.0}', $total)))
    foreach ($area in $areas) {
        $cases = @($results | Where-Object { $_.Area -eq $area })
        $time = 0.0
        foreach ($c in $cases) { $time += $c.Seconds }
        [void]$xml.AppendLine(('  <testsuite name="{0}" tests="{1}" failures="{2}" skipped="{3}" time="{4}">' -f (ConvertTo-XmlText $area), $cases.Count,
                @($cases | Where-Object { $_.Status -eq 'failed' }).Count, @($cases | Where-Object { $_.Status -eq 'skipped' }).Count,
                [string]::Format([cultureinfo]::InvariantCulture, '{0:0.0}', $time)))
        foreach ($c in $cases) {
            [void]$xml.AppendLine(('    <testcase classname="mosaic.{0}" name="{1}" time="{2}">' -f (ConvertTo-XmlText $area), (ConvertTo-XmlText $c.Name), [string]::Format([cultureinfo]::InvariantCulture, '{0:0.0}', $c.Seconds)))
            if ($c.Status -eq 'skipped') { [void]$xml.AppendLine(('      <skipped message="{0}"/>' -f (ConvertTo-XmlText $c.Message))) }
            if ($c.Status -eq 'failed') {
                [void]$xml.AppendLine(('      <failure type="{0}" message="{1}">{2}</failure>' -f (ConvertTo-XmlText $c.Kind), (ConvertTo-XmlText $c.Message), (ConvertTo-XmlText (($c.Log) -join "`n"))))
            }
            $outText = @($c.Log) + @($c.Evidence | ForEach-Object { "evidence: $_" })
            if ($outText.Count -gt 0) { [void]$xml.AppendLine(('      <system-out>{0}</system-out>' -f (ConvertTo-XmlText ($outText -join "`n")))) }
            [void]$xml.AppendLine('    </testcase>')
        }
        [void]$xml.AppendLine('  </testsuite>')
    }
    [void]$xml.AppendLine('</testsuites>')
    Write-TextFile (Join-Path $out 'junit.xml') $xml.ToString()

    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine("## $Title")
    [void]$md.AppendLine('')
    foreach ($key in $Info.Keys) { [void]$md.AppendLine("- **$($key):** $($Info[$key])") }
    [void]$md.AppendLine("- **Result:** $passed passed, $failed failed, $skipped pending/skipped (of $($results.Count))")
    [void]$md.AppendLine('')
    [void]$md.AppendLine('| Area | Passed | Failed | Pending | Total |')
    [void]$md.AppendLine('| --- | ---: | ---: | ---: | ---: |')
    foreach ($area in $areas) {
        $cases = @($results | Where-Object { $_.Area -eq $area })
        [void]$md.AppendLine(('| {0} | {1} | {2} | {3} | {4} |' -f $area, @($cases | Where-Object { $_.Status -eq 'passed' }).Count,
                @($cases | Where-Object { $_.Status -eq 'failed' }).Count, @($cases | Where-Object { $_.Status -eq 'skipped' }).Count, $cases.Count))
    }
    $failures = @($results | Where-Object { $_.Status -eq 'failed' })
    if ($failures.Count -gt 0) {
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Failures')
        [void]$md.AppendLine('')
        foreach ($f in $failures) {
            $message = ($f.Message -replace '\r?\n', ' ')
            if ($message.Length -gt 400) { $message = $message.Substring(0, 397) + '...' }
            [void]$md.AppendLine("- **$($f.Area) / $($f.Name)** ($($f.Kind)): $message")
        }
    }
    $pending = @($results | Where-Object { $_.Kind -eq 'pending' })
    if ($pending.Count -gt 0) {
        [void]$md.AppendLine('')
        [void]$md.AppendLine('### Pending (not in this build yet)')
        [void]$md.AppendLine('')
        $byArea = @{}
        foreach ($p in $pending) {
            $what = $p.Message
            if ($what -match "^command '([^']+)'") { $what = "command ``$($Matches[1])``" }
            elseif ($what -match "^dump has no '([^']+)'") { $what = "dump field ``$($Matches[1])``" }
            if (-not $byArea.ContainsKey($p.Area)) { $byArea[$p.Area] = @() }
            if ($byArea[$p.Area] -notcontains $what) { $byArea[$p.Area] += $what }
        }
        foreach ($area in $areas) {
            if ($byArea.ContainsKey($area)) { [void]$md.AppendLine("- **$($area):** $($byArea[$area] -join ', ')") }
        }
    }
    Write-TextFile (Join-Path $out 'summary.md') $md.ToString()
    [pscustomobject]@{ Passed = $passed; Failed = $failed; Skipped = $skipped; Total = $results.Count }
}

Export-ModuleMember -Function `
    Initialize-E2E, Get-E2EConfig, Get-TestServer, Get-MosaicSession, `
    Get-Field, Write-TextFile, New-Token, ConvertTo-SafeName, Write-Step, Add-Evidence, Skip-Pending, `
    Assert-True, Assert-Equal, Wait-Until, `
    Send-RawCommand, Invoke-Mosaic, Test-MosaicCommand, Get-MosaicState, Test-Field, Require-Field, Get-SelectedTab, Get-SelectedIndex, `
    Format-State, Save-State, Wait-State, Wait-OutputFile, New-OutputPath, `
    Get-TestUrl, Get-TestRequests, Get-TestReports, Wait-Report, Test-TabShows, Set-Engine, Open-TestPage, Reset-Tabs, `
    Get-MosaicMainWindow, Get-ImageStats, Save-Screenshot, Save-WindowTree, Get-GeckoWindows, `
    Get-ProcessSnapshot, Get-Survivors, Get-NewDumps, Update-DumpBaseline, Save-EventLog, `
    Start-MosaicApp, Stop-MosaicApp, Restart-MosaicApp, `
    Register-Scenario, Get-Scenarios, Invoke-Scenario, Invoke-Area, Invoke-E2ESuite, Add-Result, New-ResultRecord, Get-Results, `
    Stop-E2E, Write-E2EReports
