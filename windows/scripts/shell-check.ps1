<#
.SYNOPSIS
    The shell area's end-to-end check: every Mosaic, Settings in the window, the materials
    toggle, layout export/import and a screenshot gallery of the shell (light and dark).

.DESCRIPTION
    Starts Mosaic with --mosaic-automation and a throw-away data folder, loads a local page on the
    Chromium engine whose title is set once per load, then switches through every Mosaic and
    checks (through "dump") that the layout changed, the page was not reloaded (same title) and
    the page area moved; opens Settings at several categories and checks the page is covered;
    toggles Mica/solid; exports and re-imports the layout; finally asks the shell for its gallery
    (shellGallery). Real window screenshots and the gallery go to -OutputDirectory.

    Exit code 0 when every check passed, 1 otherwise. Owned by the shell area.
#>
[CmdletBinding()]
param(
    [string]$AppPath,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\shell'),
    [string]$GeckoExe,
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MosaicAutomation.psm1') -Force

$script:Failures = @()
$script:Passed = @()
$script:App = $null
$Utf8 = New-Object System.Text.UTF8Encoding($false)

function Resolve-AppPath {
    if ($AppPath) { return (Resolve-Path -LiteralPath $AppPath).Path }
    $rid = 'win-x64'
    $native = $env:PROCESSOR_ARCHITEW6432
    if (-not $native) { $native = $env:PROCESSOR_ARCHITECTURE }
    if ($native -eq 'ARM64') { $rid = 'win-arm64' }
    (Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) "publish\$rid\Mosaic.exe")).Path
}

function Resolve-GeckoExe {
    $candidates = @()
    if ($GeckoExe) { $candidates += $GeckoExe }
    if ($env:MOSAIC_GECKO_EXE) { $candidates += $env:MOSAIC_GECKO_EXE }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Mosaic\Engines\Firefox\firefox.exe')
    $candidates += (Join-Path $env:ProgramFiles 'Mozilla Firefox\firefox.exe')
    foreach ($candidate in $candidates) { if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate } }
    $null
}

function Send([string]$Do, [string]$Value = '') {
    $result = Send-MosaicCommand -Do $Do -Value $Value -ReplyTimeoutSeconds 120
    if ((Get-MosaicProperty $result.Reply 'ok') -ne $true) {
        throw "'$Do $Value' failed: $(Get-MosaicProperty $result.Reply 'error')"
    }
    $result.Reply
}

function Get-State { Get-MosaicProperty (Send 'dump') 'state' }

function Save-Json($Object, [string]$Name) {
    [System.IO.File]::WriteAllText((Join-Path $OutputDirectory "$Name.json"), ($Object | ConvertTo-Json -Depth 30), $Utf8)
}

function Wait-For([string]$Name, [string]$What, [scriptblock]$Condition) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $state = Get-State
        $ok = $false
        try { $ok = [bool](& $Condition $state) } catch { $ok = $false }
        if ($ok) { Save-Json $state $Name; return $state }
        if ((Get-Date) -ge $deadline) { Save-Json $state "$Name-timeout"; throw "Timed out waiting for: $What" }
        Start-Sleep -Milliseconds 400
    }
}

function Check([string]$Name, [scriptblock]$Body) {
    Write-Host "==> $Name"
    try {
        & $Body
        $script:Passed += $Name
        Write-Host '    ok'
    }
    catch {
        $script:Failures += "${Name}: $($_.Exception.Message)"
        Write-Host "    FAILED: $($_.Exception.Message)"
    }
}

function Shot([string]$Name) {
    try { Send 'screenshot' (Join-Path $OutputDirectory "window-$Name.png") | Out-Null } catch { Write-Host "    (screenshot $Name failed: $($_.Exception.Message))" }
}

function Shell($State) { Get-MosaicProperty $State 'shell' }

function Selected-Title($State) {
    $tab = @(Get-MosaicProperty $State 'tabs') | Where-Object { (Get-MosaicProperty $_ 'selected') -eq $true } | Select-Object -First 1
    if ($tab) { Get-MosaicProperty $tab 'title' } else { '' }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$data = Join-Path ([System.IO.Path]::GetTempPath()) ("mosaic-shell-check-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data -Force | Out-Null
$saved = @{ Data = $env:MOSAIC_DATA_DIR; Gecko = $env:MOSAIC_GECKO_EXE }

try {
    $exe = Resolve-AppPath
    $gecko = Resolve-GeckoExe
    $env:MOSAIC_DATA_DIR = $data
    if ($gecko) { $env:MOSAIC_GECKO_EXE = $gecko }
    Write-Host "==> Mosaic: $exe (Gecko: $gecko)"
    $script:App = Start-Process -FilePath $exe -ArgumentList @('--mosaic-automation') -PassThru `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'mosaic-stdout.log') `
        -RedirectStandardError (Join-Path $OutputDirectory 'mosaic-stderr.log')
    $null = $script:App.Handle
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        if ($script:App.HasExited) { throw "Mosaic exited with code $($script:App.ExitCode)." }
        try { Send-MosaicCommand -Do 'dump' -ConnectTimeoutMilliseconds 1000 | Out-Null; break } catch { }
        if ((Get-Date) -ge $deadline) { throw 'The automation pipe did not open within 60 s.' }
        Start-Sleep -Milliseconds 500
    }

    Check 'The composed shell is the window''s content' {
        $s = Wait-For 'shell-start' 'the shell to render' { param($s) (Get-MosaicProperty (Shell $s) 'rendered') -eq $true }
        $shell = Shell $s
        if ((Get-MosaicProperty $shell 'pagePlaceholder') -ne $false) { throw 'The shell has no page area from the window.' }
        if ((Get-MosaicProperty $s 'mosaic') -ne 'zenArc') { throw "Default Mosaic is $(Get-MosaicProperty $s 'mosaic')." }
        Write-Host "    backdrop $(Get-MosaicProperty $shell 'backdrop'), theme $(Get-MosaicProperty $shell 'theme'), placeholders: $((Get-MosaicProperty $shell 'placeholders') -join ', ')"
        $unnamed = @(Get-MosaicProperty $shell 'unnamedControls')
        if ($unnamed.Count -gt 0) { throw "Controls without an accessible name: $($unnamed -join ', ')" }
    }

    # A page whose title changes on every load: a reload would show a different title.
    $page = Join-Path $data 'shell-page.html'
    [System.IO.File]::WriteAllText($page, "<!doctype html><meta charset=utf-8><title>loading</title><body style='font:20px Segoe UI;padding:40px'><h1>Shell check</h1><p>This page must not reload when the Mosaic changes.</p><script>document.title='load-'+Math.floor(Math.random()*1e9)</script>", $Utf8)
    $pageUrl = ([System.Uri]$page).AbsoluteUri
    $title = ''
    Check 'A Chromium page to keep running across Mosaics' {
        Send 'engine' 'chromium' | Out-Null
        Send 'navigate' $pageUrl | Out-Null
        $s = Wait-For 'shell-page' 'the page to load' { param($s) (Selected-Title $s) -like 'load-*' }
        $script:title = Selected-Title $s
        Write-Host "    title $script:title"
    }
    $title = $script:title

    $scenes = @(
        @{ Name = 'zenArc'; Do = 'mosaic'; Value = 'zenArc'; Mosaic = 'zenArc'; Caption = 'titleStrip' },
        @{ Name = 'chrome'; Do = 'mosaic'; Value = 'chrome'; Mosaic = 'chrome'; Caption = 'titleRow' },
        @{ Name = 'opera'; Do = 'mosaic'; Value = 'opera'; Mosaic = 'opera'; Caption = 'titleRow' },
        @{ Name = 'opera-gx'; Do = 'mosaic'; Value = 'operaGX'; Mosaic = 'opera'; Caption = 'titleRow' },
        @{ Name = 'edge'; Do = 'mosaic'; Value = 'edge'; Mosaic = 'edge'; Caption = 'titleRow' },
        @{ Name = 'edge-vertical'; Do = 'verticalTabs'; Value = 'on'; Mosaic = 'edge'; Caption = 'toolbar' },
        @{ Name = 'edge-horizontal'; Do = 'verticalTabs'; Value = 'off'; Mosaic = 'edge'; Caption = 'titleRow' }
    )
    $rects = @{}
    foreach ($scene in $scenes) {
        Check "Mosaic $($scene.Name): instant, no reload" {
            $started = Get-Date
            Send $scene.Do $scene.Value | Out-Null
            $s = Wait-For "shell-mosaic-$($scene.Name)" "the $($scene.Name) layout" {
                param($s) (Get-MosaicProperty $s 'mosaic') -eq $scene.Mosaic -and (Get-MosaicProperty (Shell $s) 'captionHost') -eq $scene.Caption
            }
            $elapsed = ((Get-Date) - $started).TotalMilliseconds
            $shell = Shell $s
            $now = Selected-Title $s
            if ($title -and $now -ne $title) { throw "The page reloaded: title '$title' became '$now'." }
            $rect = Get-MosaicProperty $shell 'pageRect'
            $rects[$scene.Name] = "$(Get-MosaicProperty $rect 'x'),$(Get-MosaicProperty $rect 'y') $(Get-MosaicProperty $rect 'width')x$(Get-MosaicProperty $rect 'height')"
            Write-Host "    parts $((Get-MosaicProperty $shell 'parts') -join ','); page $($rects[$scene.Name]); backdrop $(Get-MosaicProperty $shell 'backdrop'); $([int]$elapsed) ms incl. polling"
            $unnamed = @(Get-MosaicProperty $shell 'unnamedControls')
            if ($unnamed.Count -gt 0) { throw "Controls without an accessible name: $($unnamed -join ', ')" }
            Shot "mosaic-$($scene.Name)"
        }
    }
    Check 'The page area moved with the layout' {
        if (($rects.Values | Select-Object -Unique).Count -lt 3) { throw "Page rectangles barely changed: $($rects.Values -join ' | ')" }
    }

    foreach ($id in @('mosaics', 'general', 'accessibility', 'privacy')) {
        Check "Settings in the window: $id" {
            Send 'openSettings' $id | Out-Null
            $s = Wait-For "shell-settings-$id" "Settings at $id" {
                param($s) (Get-MosaicProperty $s 'settingsCategory') -eq $id -and (@(Get-MosaicProperty (Shell $s) 'pageCovers') -contains 'shell.settings')
            }
            $unnamed = @(Get-MosaicProperty (Shell $s) 'unnamedControls')
            if ($unnamed.Count -gt 0) { throw "Controls without an accessible name: $($unnamed -join ', ')" }
            Shot "settings-$id"
        }
    }
    Check 'Settings search' {
        Send 'settingsSearch' 'mica' | Out-Null
        $s = Wait-For 'shell-settings-search' 'search results' {
            param($s) $m = @(Get-MosaicProperty (Shell $s) 'settingsMatches'); $m.Count -eq 1 -and (Get-MosaicProperty $m[0] 'id') -eq 'mosaics'
        }
        Shot 'settings-search'
    }
    Check 'Closing Settings uncovers the page' {
        Send 'closeSettings' | Out-Null
        Wait-For 'shell-settings-closed' 'Settings to close' {
            param($s) (Get-MosaicProperty $s 'settingsCategory') -eq '' -and -not (@(Get-MosaicProperty (Shell $s) 'pageCovers') -contains 'shell.settings')
        } | Out-Null
    }

    Check 'Materials toggle: Mica and solid' {
        Send 'mosaicSet' 'materials=off' | Out-Null
        Wait-For 'shell-solid' 'a solid window' { param($s) (Get-MosaicProperty (Shell $s) 'backdrop') -eq 'Solid' } | Out-Null
        Shot 'materials-solid'
        Send 'mosaicSet' 'materials=on' | Out-Null
        $s = Wait-For 'shell-mica' 'Mica again' { param($s) (Get-MosaicProperty (Shell $s) 'backdrop') -ne 'Solid' -or (Get-MosaicProperty (Shell $s) 'micaSupported') -eq $false }
        Write-Host "    back to $(Get-MosaicProperty (Shell $s) 'backdrop')"
    }

    Check 'Dark, accent, density and corner radius' {
        Send 'mosaicSet' 'scheme=dark' | Out-Null
        Send 'mosaicSet' 'accent=#E8458B' | Out-Null
        Send 'mosaicSet' 'density=comfortable' | Out-Null
        Send 'mosaicSet' 'radius=14' | Out-Null
        $s = Wait-For 'shell-appearance' 'the appearance to apply' {
            param($s) (Get-MosaicProperty (Shell $s) 'theme') -eq 'dark' -and (Get-MosaicProperty (Shell $s) 'accentColor') -eq '#E8458B'
        }
        Shot 'appearance-dark-pink'
    }

    Check 'Layout export and import (macOS format)' {
        $file = Join-Path $OutputDirectory 'edge-layout.json'
        Send 'exportLayout' $file | Out-Null
        $text = [System.IO.File]::ReadAllText($file)
        if ($text -notmatch '"preset" : "edge"') { throw 'The exported layout is not the Edge layout in macOS''s format.' }
        Send 'restoreMosaic' 'edge' | Out-Null
        Send 'importLayout' $file | Out-Null
        $s = Wait-For 'shell-imported' 'the imported layout' { param($s) (Get-MosaicProperty (Get-MosaicProperty (Get-MosaicProperty $s 'layout') 'appearance') 'accent') -eq '#E8458B' }
    }

    Check 'Gallery of every Mosaic and settings page, light and dark' {
        $gallery = Join-Path $OutputDirectory 'gallery'
        $reply = Send 'shellGallery' $gallery
        $files = @(Get-MosaicProperty $reply 'files')
        Write-Host "    $($files.Count) images"
        if ($files.Count -lt 20) { throw "Only $($files.Count) gallery images." }
    }

    Check 'Mosaic still running and the page never reloaded' {
        $s = Get-State
        $now = Selected-Title $s
        if ($title -and $now -ne $title) { throw "The page reloaded during the run: '$title' became '$now'." }
        Save-Json $s 'shell-final'
    }
}
catch {
    $script:Failures += "setup: $($_.Exception.Message)"
    Write-Host "FAILED: $($_.Exception.Message)"
}
finally {
    try { if ($script:App -and -not $script:App.HasExited) { Send-MosaicCommand -Do 'quit' -ReplyTimeoutSeconds 10 | Out-Null } } catch { }
    if ($script:App -and -not $script:App.WaitForExit(20000)) { Stop-Process -Id $script:App.Id -Force -ErrorAction SilentlyContinue }
    $env:MOSAIC_DATA_DIR = $saved.Data
    $env:MOSAIC_GECKO_EXE = $saved.Gecko
    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "Shell check: $($script:Passed.Count) passed, $($script:Failures.Count) failed"
foreach ($failure in $script:Failures) { Write-Host "  - $failure" }
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'summary.txt'), (@("passed: $($script:Passed -join '; ')") + $script:Failures) -join "`r`n", $Utf8)
if ($script:Failures.Count -gt 0) { exit 1 }
exit 0
