<#
.SYNOPSIS
    A stand-in for Mosaic that speaks its automation protocol, for testing the e2e framework
    itself (Run-E2E.ps1 -SelfTest) without building the app.

.DESCRIPTION
    Shows a small Windows Forms window (with one deliberately unnamed button, so the
    accessibility scan has something to find) and answers on \\.\pipe\mosaic-automation:
    dump, navigate (fetches the page to learn its title), newTab, closeTab, selectTab, engine,
    toggleEngine, searchEngine, back, forward, reload, screenshot, pin, unpin, quit; and, for
    the framework's own checks, "hang" (stops pumping messages for 90 s) and "crash" (fails
    fast, so Windows Error Reporting writes a dump). Anything else: "Unknown command".
#>
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$utf8 = New-Object System.Text.UTF8Encoding($false)
$state = @{
    Tabs     = New-Object System.Collections.ArrayList
    Selected = 0
    Search   = 'google'
}
$engines = @{ google = 'https://www.google.com/search?q='; duckduckgo = 'https://duckduckgo.com/?q='; bing = 'https://www.bing.com/search?q=' }

function New-Tab([string]$Url) {
    $tab = @{ Url = ''; Title = 'New Tab'; Engine = 'gecko'; Pinned = $false; History = (New-Object System.Collections.ArrayList); Index = -1 }
    [void]$state.Tabs.Add($tab)
    $state.Selected = $state.Tabs.Count - 1
    if ($Url) { Open-Url $tab $Url }
    $tab
}

function Open-Url($Tab, [string]$Url, [switch]$NoHistory) {
    if ($Url -notmatch '^[a-z]+:' -and $Url -notmatch '\.') { $Url = $engines[$state.Search] + [uri]::EscapeDataString($Url) }
    $Tab.Url = $Url
    $Tab.Title = $Url
    if (-not $NoHistory) {
        while ($Tab.History.Count -gt $Tab.Index + 1) { $Tab.History.RemoveAt($Tab.History.Count - 1) }
        [void]$Tab.History.Add($Url)
        $Tab.Index = $Tab.History.Count - 1
    }
    if ($Url -like 'http://127.0.0.1*' -or $Url -like 'http://localhost*') {
        try {
            $client = New-Object System.Net.WebClient
            $client.Encoding = [System.Text.Encoding]::UTF8
            $html = $client.DownloadString($Url)
            if ($html -match '<title>([^<]*)</title>') { $Tab.Title = [System.Net.WebUtility]::HtmlDecode($Matches[1]) }
        }
        catch { $Tab.Title = 'Failed' }
    }
}

function Get-Dump {
    $tabs = @()
    for ($i = 0; $i -lt $state.Tabs.Count; $i++) {
        $t = $state.Tabs[$i]
        $tabs += [ordered]@{
            engine = $t.Engine; url = $t.Url; title = $t.Title; loading = $false
            canGoBack = ($t.Index -gt 0); canGoForward = ($t.Index -lt $t.History.Count - 1)
            geckoId = $i; hasChromiumView = ($t.Engine -eq 'chromium'); blocked = 0; failure = ''
            selected = ($i -eq $state.Selected); pinned = $t.Pinned
        }
    }
    [ordered]@{
        tabs = $tabs; engine = $state.Tabs[$state.Selected].Engine; searchEngine = $state.Search; addressText = ''
        chromiumAvailable = $true; geckoStatus = 'running'; geckoProcessId = -1; geckoAttached = $true; notice = ''
        extensions = @(); blocker = [ordered]@{ status = 'ready'; enabled = $true; allowlist = @(); engineVersion = 'fake'; updating = $false; filters = 0; error = '' }
        extensionError = ''; extensionsBusy = $false
        tabsArea = [ordered]@{ selectedWorkspace = 'Default'; recentlyClosed = @() }
    }
}

function Invoke-FakeCommand([string]$Line) {
    $request = $Line | ConvertFrom-Json
    $do = [string]$request.do
    $value = ''
    if ($request.PSObject.Properties['value']) { $value = [string]$request.value }
    $tab = $state.Tabs[$state.Selected]
    switch ($do) {
        'dump' { return (@{ ok = $true; state = (Get-Dump) } | ConvertTo-Json -Depth 8 -Compress) }
        'navigate' { Open-Url $tab $value }
        'newTab' { $null = New-Tab $value }
        'closeTab' {
            if ($state.Tabs.Count -gt 1) { $state.Tabs.RemoveAt($state.Selected); $state.Selected = [math]::Min($state.Selected, $state.Tabs.Count - 1) }
        }
        'selectTab' {
            $index = -1
            if (-not [int]::TryParse($value, [ref]$index) -or $index -lt 0 -or $index -ge $state.Tabs.Count) { return '{"ok":false,"error":"There is no tab ' + $value + '."}' }
            $state.Selected = $index
        }
        'engine' {
            if ($value -ne 'gecko' -and $value -ne 'chromium') { return '{"ok":false,"error":"Unknown engine."}' }
            $tab.Engine = $value
        }
        'toggleEngine' { if ($tab.Engine -eq 'gecko') { $tab.Engine = 'chromium' } else { $tab.Engine = 'gecko' } }
        'searchEngine' {
            if (-not $engines.ContainsKey($value)) { return '{"ok":false,"error":"Unknown search engine."}' }
            $state.Search = $value
        }
        'back' { if ($tab.Index -gt 0) { $tab.Index--; Open-Url $tab $tab.History[$tab.Index] -NoHistory } }
        'forward' { if ($tab.Index -lt $tab.History.Count - 1) { $tab.Index++; Open-Url $tab $tab.History[$tab.Index] -NoHistory } }
        'reload' { if ($tab.Url) { Open-Url $tab $tab.Url -NoHistory } }
        'pin' { $tab.Pinned = $true }
        'unpin' { $tab.Pinned = $false }
        'screenshot' {
            $bitmap = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
            $form.DrawToBitmap($bitmap, (New-Object System.Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
            $bitmap.Save($value, [System.Drawing.Imaging.ImageFormat]::Png)
            $bitmap.Dispose()
        }
        'quit' { $script:QuitAfterReply = $true }
        'hang' { $script:HangAfterReply = $true }
        'crash' { $script:CrashAfterReply = $true }
        default { return ('{"ok":false,"error":"Unknown command \"' + $do + '\"."}') }
    }
    $label.Text = "$($state.Tabs.Count) tab(s); selected: $($state.Tabs[$state.Selected].Title)"
    '{"ok":true}'
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Mosaic'
$form.Width = 1000
$form.Height = 700
$form.StartPosition = 'CenterScreen'
$form.BackColor = [System.Drawing.Color]::FromArgb(243, 243, 243)
$back = New-Object System.Windows.Forms.Button
$back.Text = 'Back'; $back.Left = 10; $back.Top = 10; $back.Width = 80
$unnamed = New-Object System.Windows.Forms.Button
$unnamed.Text = ''; $unnamed.Left = 100; $unnamed.Top = 10; $unnamed.Width = 40
$address = New-Object System.Windows.Forms.TextBox
$address.Left = 150; $address.Top = 12; $address.Width = 600; $address.AccessibleName = 'Address'
$label = New-Object System.Windows.Forms.Label
$label.Left = 10; $label.Top = 60; $label.Width = 900; $label.Height = 40; $label.Text = 'Fake Mosaic'
$panel = New-Object System.Windows.Forms.Panel
$panel.Left = 10; $panel.Top = 110; $panel.Width = 960; $panel.Height = 530; $panel.BackColor = [System.Drawing.Color]::FromArgb(31, 111, 235)
$form.Controls.AddRange(@($back, $unnamed, $address, $label, $panel))

$null = New-Tab ''
$script:QuitAfterReply = $false
$script:HangAfterReply = $false
$script:CrashAfterReply = $false

$pipe = New-Object System.IO.Pipes.NamedPipeServerStream('mosaic-automation', [System.IO.Pipes.PipeDirection]::InOut, 1,
    [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
$script:Pending = $pipe.BeginWaitForConnection($null, $null)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 50
$timer.Add_Tick({
        if (-not $script:Pending.IsCompleted) { return }
        try {
            $pipe.EndWaitForConnection($script:Pending)
            $reader = New-Object System.IO.StreamReader($pipe, $utf8, $false, 4096, $true)
            $line = $reader.ReadLine()
            $reply = '{"ok":false,"error":"empty request"}'
            try { if ($line) { $reply = Invoke-FakeCommand $line } } catch { $reply = (@{ ok = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress) }
            $bytes = $utf8.GetBytes($reply + "`n")
            $pipe.Write($bytes, 0, $bytes.Length)
            $pipe.Flush()
            try { $pipe.WaitForPipeDrain() } catch { }
            $pipe.Disconnect()
        }
        catch {
            try { $pipe.Disconnect() } catch { }
        }
        if ($script:QuitAfterReply) { $timer.Stop(); $form.Close(); return }
        if ($script:CrashAfterReply) { [System.Environment]::FailFast('FakeMosaic: crash requested by the e2e self-test') }
        $script:Pending = $pipe.BeginWaitForConnection($null, $null)
        if ($script:HangAfterReply) {
            # A real hang: Start-Sleep would pump messages on this STA thread.
            $script:HangAfterReply = $false
            $until = [DateTime]::UtcNow.AddSeconds(90)
            while ([DateTime]::UtcNow -lt $until) { }
        }
    })
$timer.Start()
[System.Windows.Forms.Application]::Run($form)
$pipe.Dispose()
