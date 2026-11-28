<#
.SYNOPSIS
    End-to-end check of passwords (area: passwords) in a published Mosaic, in both engines.

.DESCRIPTION
    Serves a local two-origin sign-in site (two loopback ports), starts Mosaic with
    --mosaic-automation and a throw-away data folder (an isolated profile: its own vault, never
    the real one), and checks through "dump" and the passwords* automation commands that, in
    Mozilla (Gecko) and then Chromium (WebView2) tabs:

      1. a sign-in is offered for saving, with the right site and username, and Save stores it
         (passwordsVerify: match);
      2. signing in again with another password offers Update, which replaces it;
      3. "Never for This Site" silences the prompt for that site;
      4. a cross-origin frame's sign-in (the other port) is never reported or offered;
      5. a saved login fills the page's form on request (the page shows what was filled in its title);
      6. a tab marked private (passwordsPrivate) is never offered a save;
    then CSV import and export (Mosaic's format), and renders the prompt, key button and
    settings page (passwordsSnapshot). Windows Hello is bypassed only because this is an
    isolated, automated profile; it cannot be exercised on CI.

    The exit code is 0 only if every check passed. Screenshots and states go to the output folder.

.PARAMETER AppPath
    Mosaic.exe to test.

.PARAMETER OutputDirectory
    Where screenshots and logs go. Emptied first.

.PARAMETER GeckoExe
    firefox.exe for the Mozilla engine. Default: %MOSAIC_GECKO_EXE%, then Program Files.

.PARAMETER Engines
    Which engines to check (default both).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$AppPath,
    [string]$OutputDirectory = (Join-Path $PWD 'passwords-e2e'),
    [string]$GeckoExe = $env:MOSAIC_GECKO_EXE,
    [string[]]$Engines = @('gecko', 'chromium'),
    [int]$TimeoutSeconds = 90
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$scripts = Join-Path $PSScriptRoot '..\..\..\..\scripts'
Import-Module (Join-Path $scripts 'MosaicAutomation.psm1') -Force

if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$dataDirectory = Join-Path $OutputDirectory 'data'
New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
$results = New-Object System.Collections.Generic.List[string]
$failures = 0

function Pass([string]$what) { $script:results.Add("PASS  $what"); Write-Host "PASS  $what" }
function Fail([string]$what) { $script:results.Add("FAIL  $what"); Write-Host "FAIL  $what" -ForegroundColor Red; $script:failures++ }

# ---- The sign-in site ------------------------------------------------------------------

$loginPage = @'
<!doctype html><html><head><meta charset="utf-8"><title>Login</title></head><body>
<form id="f" method="post" action="/home">
  <input type="email" id="u" name="email" autocomplete="username">
  <input type="password" id="p" name="pw" autocomplete="current-password">
  <button>Sign in</button>
</form>
<script>
  const q = new URLSearchParams(location.search);
  const show = () => { document.title = "filled:" + u.value + ":" + p.value.length; };
  u.addEventListener("input", show);
  p.addEventListener("input", show);
  if (q.get("frame")) {
    for (const src of ["/sameframe", q.get("frame")]) {
      const frame = document.createElement("iframe");
      frame.src = src;
      document.body.appendChild(frame);
    }
  }
  if (q.get("auto")) {
    const [user, pass] = q.get("auto").split(":");
    setTimeout(() => { u.value = user; p.value = pass; f.requestSubmit(); }, 4000);
  }
</script></body></html>
'@

$framePage = @'
<!doctype html><html><head><meta charset="utf-8"><title>frame</title></head><body>
<form id="g"><input id="u" name="user" autocomplete="username"><input type="password" id="p"><button>Go</button></form>
<script>
  g.addEventListener("submit", e => { e.preventDefault(); fetch("/frame-submitted"); g.remove(); });
  setTimeout(() => { u.value = "__USER__"; p.value = "framepw-1"; g.requestSubmit(); }, 1500);
</script></body></html>
'@

function Start-LoginServer {
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $hits = [hashtable]::Synchronized(@{ FrameSubmits = 0 })
    $pages = @{
        '/login'     = $utf8.GetBytes($loginPage)
        '/home'      = $utf8.GetBytes('<!doctype html><title>Home</title>Signed in.')
        '/frame'     = $utf8.GetBytes($framePage.Replace('__USER__', 'eve'))
        '/sameframe' = $utf8.GetBytes($framePage.Replace('__USER__', 'sam'))
    }
    $runspace = [PowerShell]::Create()
    $null = $runspace.AddScript({
            param($Listener, $Pages, $Hits)
            while ($true) {
                try { $client = $Listener.AcceptTcpClient() } catch { return }
                try {
                    $client.ReceiveTimeout = 2000
                    $stream = $client.GetStream()
                    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII, $false, 1024, $true)
                    $requestLine = $reader.ReadLine()
                    $length = 0
                    while (-not [string]::IsNullOrEmpty(($header = $reader.ReadLine()))) {
                        if ($header -match '^Content-Length:\s*(\d+)') { $length = [int]$Matches[1] }
                    }
                    if ($length -gt 0) { $null = $reader.Read((New-Object char[] $length), 0, $length) }
                    $path = '/'
                    if ($requestLine -match '^\w+ ([^ ?]+)') { $path = $Matches[1] }
                    if ($path -eq '/frame-submitted') { $Hits.FrameSubmits++ }
                    $content = $Pages[$path]
                    if ($null -eq $content) { $content = [System.Text.Encoding]::UTF8.GetBytes('<!doctype html><title>Other</title>') }
                    $head = [System.Text.Encoding]::ASCII.GetBytes(
                        "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($content.Length)`r`nCache-Control: no-store`r`nConnection: close`r`n`r`n")
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
        Origin = "http://127.0.0.1:$($listener.LocalEndpoint.Port)"
        Hits = $hits; Listener = $listener; Runspace = $runspace; Handle = $handle
    }
}

function Stop-LoginServer($server) {
    if ($null -eq $server) { return }
    $server.Listener.Stop()
    try { $server.Runspace.EndInvoke($server.Handle) } catch { }
    $server.Runspace.Dispose()
}

# ---- Driving Mosaic ----------------------------------------------------------------------

function Invoke-Mosaic([string]$do, [string]$value, [string]$out) {
    $reply = (Send-MosaicCommand -Do $do -Value $value -Out $out -ReplyTimeoutSeconds 60).Reply
    if (-not $reply.ok) { throw "$do $value failed: $(Get-MosaicProperty $reply 'error')" }
    $reply
}

function Get-State { (Invoke-Mosaic 'dump').state }

function Get-PasswordTab($state) {
    $index = 0
    foreach ($tab in $state.tabs) { if ($tab.selected) { break }; $index++ }
    $state.passwords.tabs | Where-Object { $_.index -eq $index } | Select-Object -First 1
}

function Wait-For([scriptblock]$condition, [string]$what, [int]$seconds = $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($true) {
        $state = Get-State
        if (& $condition $state) { return $state }
        if ((Get-Date) -ge $deadline) {
            $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "timeout-$($what -replace '\W','-').json")
            throw "Timed out waiting for $what"
        }
        Start-Sleep -Milliseconds 400
    }
}

function Test-Verify([string]$origin, [string]$user, [string]$password) {
    $file = Join-Path $OutputDirectory 'verify.txt'
    Invoke-Mosaic 'passwordsVerify' "$origin|$user|$password" $file | Out-Null
    (Get-Content -LiteralPath $file -Raw).Trim()
}

function Save-Shot([string]$name) {
    try { Invoke-Mosaic 'screenshot' (Join-Path $OutputDirectory "$name.png") | Out-Null } catch { Write-Host "  (screenshot $name failed: $_)" }
}

$site = $null; $other = $null; $app = $null
$savedDataDir = $env:MOSAIC_DATA_DIR; $savedGecko = $env:MOSAIC_GECKO_EXE
try {
    $site = Start-LoginServer
    $other = Start-LoginServer
    Write-Host "==> Site $($site.Origin), cross-origin frames from $($other.Origin)"

    $env:MOSAIC_DATA_DIR = $dataDirectory
    if ($GeckoExe) { $env:MOSAIC_GECKO_EXE = $GeckoExe }
    $app = Start-Process -FilePath $AppPath -ArgumentList '--mosaic-automation' -PassThru `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'mosaic-stdout.log') -RedirectStandardError (Join-Path $OutputDirectory 'mosaic-stderr.log')
    $null = $app.Handle
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        if ($app.HasExited) { throw "Mosaic exited ($($app.ExitCode)) before opening its automation pipe." }
        try { Send-MosaicCommand -Do 'dump' -ConnectTimeoutMilliseconds 1000 | Out-Null; break } catch { }
        if ((Get-Date) -ge $deadline) { throw 'Mosaic did not open its automation pipe within 60 s.' }
    }
    $state = Get-State
    if ($state.passwords.store -eq 'dpapi' -and $state.passwords.keychainScope -eq 'dpapi:isolated-profile' -and $state.passwords.vault.StartsWith($dataDirectory)) {
        Pass "vault is this profile's own DPAPI file ($($state.passwords.vault))"
    } else { Fail "vault scope: $($state.passwords | ConvertTo-Json -Compress)" }

    $frame = [uri]::EscapeDataString("$($other.Origin)/frame")
    foreach ($engine in $Engines) {
        Write-Host "==> $engine"
        $user = "alice-$engine"
        # The engine first (it moves the selected tab, so that tab is put on a quiet page), then
        # a new tab, which opens in the selected tab's engine.
        if ((Get-State).tabs | Where-Object { $_.selected -and $_.url }) { Invoke-Mosaic 'navigate' "$($site.Origin)/home" | Out-Null }
        Invoke-Mosaic 'engine' $engine | Out-Null
        Invoke-Mosaic 'newTab' "$($site.Origin)/login?auto=$($user):First-Pass-1&frame=$frame" | Out-Null

        try {
            $state = Wait-For { param($s) $t = Get-PasswordTab $s; $t -and $t.prompt -eq 'save' -and (Get-MosaicProperty $t 'promptUsername') -eq $user } "$engine save prompt"
            $tab = Get-PasswordTab $state
            Save-Shot "$engine-1-save-prompt"
            if ((Get-MosaicProperty $tab 'promptOrigin') -eq $site.Origin -and (Get-MosaicProperty $tab 'promptUsername') -eq $user) { Pass "$engine offers to save $user on $($site.Origin)" }
            else { Fail "$engine save prompt is for $((Get-MosaicProperty $tab 'promptUsername')) on $((Get-MosaicProperty $tab 'promptOrigin'))" }
            Invoke-Mosaic 'passwordsPrompt' 'save' | Out-Null
            if ((Test-Verify $site.Origin $user 'First-Pass-1') -eq 'match') { Pass "$engine saved the password" } else { Fail "$engine did not save the password" }
        } catch { Fail "$engine save: $_" }

        # The cross-origin frame submitted too, and must never have been reported.
        try {
            $null = Wait-For { param($s) $other.Hits.FrameSubmits -gt 0 } "$engine cross-origin frame submit" 30
            $state = Get-State
            $last = Get-MosaicProperty $state.passwords 'lastSubmission'
            $leak = ($last -and ($last.username -eq 'eve' -or $last.origin -eq $other.Origin)) -or
                    @($state.passwords.credentials | Where-Object { $_.username -eq 'eve' }).Count -gt 0
            if (-not $leak) { Pass "$engine refused the cross-origin frame's sign-in" } else { Fail "$engine reported the cross-origin frame: $($last | ConvertTo-Json -Compress)" }
        } catch { Fail "$engine cross-origin check: $_" }

        try {
            Invoke-Mosaic 'navigate' "$($site.Origin)/login?auto=$($user):Second-Pass-2" | Out-Null
            $state = Wait-For { param($s) $t = Get-PasswordTab $s; $t -and $t.prompt -eq 'update' } "$engine update prompt"
            Save-Shot "$engine-2-update-prompt"
            Invoke-Mosaic 'passwordsPrompt' 'save' | Out-Null
            if ((Test-Verify $site.Origin $user 'Second-Pass-2') -eq 'match') { Pass "$engine updated the password" } else { Fail "$engine did not update the password" }
        } catch { Fail "$engine update: $_" }

        try {
            Invoke-Mosaic 'navigate' "$($site.Origin)/login" | Out-Null
            $null = Wait-For { param($s) $t = Get-PasswordTab $s; $t -and $t.hasLogin -and $t.offersFill } "$engine sign-in form with a saved login"
            Invoke-Mosaic 'passwordsFill' $user | Out-Null
            $expected = "filled:$($user):13"
            $state = Wait-For { param($s) @($s.tabs | Where-Object { $_.selected -and $_.title -eq $expected }).Count -gt 0 } "$engine filled form" 30
            Save-Shot "$engine-3-filled"
            if ($state.passwords.lastFill.count -ge 2) { Pass "$engine filled $user into the form" } else { Fail "$engine fill count $($state.passwords.lastFill.count)" }
        } catch { Fail "$engine fill: $_" }

        try {
            Invoke-Mosaic 'navigate' "$($site.Origin)/login?auto=bob-$($engine):Never-Pass-3" | Out-Null
            $null = Wait-For { param($s) $t = Get-PasswordTab $s; $t -and $t.prompt -eq 'save' -and (Get-MosaicProperty $t 'promptUsername') -eq "bob-$engine" } "$engine prompt for bob"
            Invoke-Mosaic 'passwordsPrompt' 'never' | Out-Null
            $state = Get-State
            if (@($state.passwords.neverSave) -contains $site.Origin) { Pass "$engine never-save lists the site" } else { Fail "$engine never-save list: $($state.passwords.neverSave)" }
            Invoke-Mosaic 'navigate' "$($site.Origin)/login?auto=bob-$($engine):Never-Pass-4" | Out-Null
            Start-Sleep -Seconds 8
            $tab = Get-PasswordTab (Get-State)
            if ($tab.prompt -eq '') { Pass "$engine stays quiet on a never-save site" } else { Fail "$engine prompted ($($tab.prompt)) on a never-save site" }
            Invoke-Mosaic 'passwordsNeverSave' "$($site.Origin)|off" | Out-Null
        } catch { Fail "$engine never-save: $_" }

        try {
            Invoke-Mosaic 'passwordsPrivate' 'on' | Out-Null
            Invoke-Mosaic 'navigate' "$($site.Origin)/login?auto=carol-$($engine):Private-Pass-5" | Out-Null
            $null = Wait-For { param($s) $l = Get-MosaicProperty $s.passwords 'lastSubmission'; $l -and $l.username -eq "carol-$engine" } "$engine private sign-in reported"
            Start-Sleep -Seconds 6
            $tab = Get-PasswordTab (Get-State)
            if ($tab.prompt -eq '' -and $tab.private) { Pass "$engine never offers to save in a private tab" } else { Fail "$engine prompted ($($tab.prompt)) in a private tab" }
            Invoke-Mosaic 'passwordsPrivate' 'off' | Out-Null
        } catch { Fail "$engine private: $_" }
    }

    Write-Host '==> Import and export'
    try {
        $csv = Join-Path $OutputDirectory 'import.csv'
        [System.IO.File]::WriteAllText($csv, "name,url,username,password,note`nexample.com,https://example.com/login,imported,Imp0rted-Pass,`nbad,android://x@com.app/,bob,pw,`n", $utf8)
        $summary = Join-Path $OutputDirectory 'import-summary.txt'
        Invoke-Mosaic 'passwordsImport' $csv $summary | Out-Null
        $text = (Get-Content -LiteralPath $summary -Raw).Trim()
        if ($text -eq 'added 1 updated 0 unchanged 0 failed 0 skipped 1') { Pass "import: $text" } else { Fail "import: $text" }
        $export = Join-Path $OutputDirectory 'export.csv'
        Invoke-Mosaic 'passwordsExport' $export | Out-Null
        $lines = Get-Content -LiteralPath $export
        if ($lines[0] -eq 'name,url,username,password' -and ($lines -contains 'example.com,https://example.com,imported,Imp0rted-Pass')) { Pass 'export in the macOS format' }
        else { Fail "export: $($lines -join ' | ')" }
        Remove-Item -LiteralPath $csv, $export -Force
    } catch { Fail "import/export: $_" }

    Write-Host '==> Views'
    foreach ($view in 'prompt', 'button', 'settings') {
        try {
            $png = Join-Path $OutputDirectory "view-$view.png"
            Invoke-Mosaic 'passwordsSnapshot' $view $png | Out-Null
            if ((Test-Path -LiteralPath $png) -and (Get-Item -LiteralPath $png).Length -gt 1000) { Pass "rendered the $view view" } else { Fail "no $view snapshot" }
        } catch { Fail "snapshot $($view): $_" }
    }
    $state = Get-State
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'final-state.json')
    $raw = Get-Content -LiteralPath (Join-Path $OutputDirectory 'final-state.json') -Raw
    if ($raw -notmatch 'First-Pass-1|Second-Pass-2|Imp0rted-Pass|Private-Pass-5') { Pass 'dump never contains a password' } else { Fail 'a password appears in dump' }
}
catch {
    Fail "scenario: $_"
}
finally {
    if ($app -and -not $app.HasExited) {
        try { Send-MosaicCommand -Do 'quit' -ReplyTimeoutSeconds 10 | Out-Null } catch { }
        if (-not $app.WaitForExit(20000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
    Stop-LoginServer $site
    Stop-LoginServer $other
    $env:MOSAIC_DATA_DIR = $savedDataDir
    $env:MOSAIC_GECKO_EXE = $savedGecko
    $results | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.txt')
}

Write-Host ''
Write-Host "$($results.Count - $failures) passed, $failures failed"
if ($failures -gt 0) { exit 1 }
exit 0
