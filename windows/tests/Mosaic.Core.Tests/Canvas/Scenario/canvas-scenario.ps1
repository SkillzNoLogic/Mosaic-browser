<#
.SYNOPSIS
    End-to-end check of the layout canvas, Gradient Studio and configurations on Windows,
    through the automation pipe, with screenshots of the editor and the studio.

.DESCRIPTION
    Real mouse drags can't run on a CI runner, so this drives the same controller operations a
    drag, a key press or a click performs: canvasDrag takes the drag-and-drop path (verdict,
    refusal message, drop position), canvasKey the keyboard handler's calls. Each step checks
    `dump` (state.canvas). Owner: canvas (windows/tests/Mosaic.Core.Tests/Canvas/Scenario).

    With -AppPath the script starts Mosaic itself (isolated data folder, --mosaic-automation)
    and quits it at the end; without, it drives a Mosaic that is already listening.

.EXAMPLE
    .\canvas-scenario.ps1 -AppPath windows\publish\win-x64\Mosaic.exe -OutputDirectory windows\artifacts\canvas
#>
[CmdletBinding()]
param(
    [string]$AppPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\..\artifacts\canvas')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$repoWindows = Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')
Import-Module (Join-Path $repoWindows 'scripts\MosaicAutomation.psm1') -Force

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$fixtures = Join-Path $PSScriptRoot '..\Fixtures'
$failures = New-Object System.Collections.Generic.List[string]
$app = $null

function Invoke-Mosaic([string]$Do, [string]$Value, [string]$Out) {
    $result = Send-MosaicCommand -Do $Do -Value $Value -Out $Out
    if (-not $result.Reply.ok) { throw "$Do $Value failed: $($result.Raw)" }
    $result.Reply
}

function Get-Canvas {
    $state = (Invoke-Mosaic 'dump').state
    Get-MosaicProperty $state 'canvas'
}

function Check([string]$Name, [scriptblock]$Condition) {
    try {
        if (& $Condition) { Write-Host "  ok    $Name" }
        else { Write-Host "  FAIL  $Name"; $failures.Add($Name) }
    }
    catch {
        Write-Host "  FAIL  $Name ($($_.Exception.Message))"
        $failures.Add($Name)
    }
}

function Shot([string]$Name) {
    Start-Sleep -Milliseconds 700   # let layout and the background render settle
    $path = Join-Path $OutputDirectory "$Name.png"
    try { Invoke-Mosaic 'screenshot' $path | Out-Null; Write-Host "  shot  $path" }
    catch { Write-Host "  FAIL  screenshot $Name ($($_.Exception.Message))"; $failures.Add("screenshot $Name") }
}

try {
    if ($AppPath) {
        $data = Join-Path $OutputDirectory 'data'
        Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        $env:MOSAIC_DATA_DIR = $data
        $app = Start-Process -FilePath $AppPath -ArgumentList '--mosaic-automation' -PassThru
        $deadline = (Get-Date).AddSeconds(60)
        while ($true) {
            try { Send-MosaicCommand -Do 'dump' -ConnectTimeoutMilliseconds 1000 | Out-Null; break }
            catch { if ((Get-Date) -gt $deadline) { throw "Mosaic did not start listening within 60 s." } }
        }
    }

    Write-Host 'Layout canvas'
    Invoke-Mosaic 'configRestoreAll' | Out-Null
    Invoke-Mosaic 'canvasMode' 'layout' | Out-Null
    $c = Get-Canvas
    Check 'canvas exists and is in layout mode' { $c.created -and $c.mode -eq 'layout' }
    Check 'palette offers flexible space first' { $c.palette[0] -eq 'flexibleSpace' }
    Shot 'canvas-editor'

    Invoke-Mosaic 'canvasTabs' 'top' | Out-Null
    Invoke-Mosaic 'canvasMove' 'home>titleBar:0' | Out-Null
    $c = Get-Canvas
    Check 'move between rows' { $c.layout.titleBar[0] -eq 'home' -and $c.canUndo }

    Invoke-Mosaic 'canvasDrag' 'addressField>rail' | Out-Null
    $c = Get-Canvas
    Check 'refused drop is explained and changes nothing' { $c.messageIsError -and $c.message -like '*too narrow*' -and ($c.layout.toolbar -contains 'addressField') }

    Invoke-Mosaic 'canvasDrag' 'back>rail:0' | Out-Null
    Check 'drag-and-drop path drops' { (Get-Canvas).layout.rail[0] -eq 'back' }
    Invoke-Mosaic 'canvasDragToPalette' 'rail:0' | Out-Null
    Check 'drop on Available Items removes' { (Get-Canvas).palette -contains 'back' }
    Invoke-Mosaic 'canvasDragToPalette' 'addressField' | Out-Null
    Check 'required component refuses removal' { (Get-Canvas).messageIsError }

    Invoke-Mosaic 'canvasAdd' 'settings>toolbar:0' | Out-Null
    Invoke-Mosaic 'canvasSelect' 'toolbar:0' | Out-Null
    Invoke-Mosaic 'canvasKey' 'alt-right' | Out-Null
    $c = Get-Canvas
    Check 'Alt+Right moves the selection' { $c.layout.toolbar[1] -eq 'settings' -and $c.selection.index -eq 1 }
    Invoke-Mosaic 'canvasKey' 'return' | Out-Null
    Invoke-Mosaic 'canvasKey' 'right' | Out-Null
    Check 'picked up moves with arrows' { (Get-Canvas).grabbing -eq 'settings' }
    Invoke-Mosaic 'canvasKey' 'escape' | Out-Null
    Check 'Esc puts it back' { (Get-Canvas).layout.toolbar[1] -eq 'settings' }
    Shot 'canvas-editor-selected'
    Invoke-Mosaic 'canvasUndo' | Out-Null
    Invoke-Mosaic 'canvasRedo' | Out-Null
    Invoke-Mosaic 'canvasTabs' 'sidebarLeading' | Out-Null
    Check 'tabs to a sidebar' { (Get-Canvas).tabPlacement -eq 'sidebarLeading' }
    Shot 'canvas-editor-vertical'
    Invoke-Mosaic 'canvasReset' | Out-Null

    Write-Host 'Gradient Studio'
    Invoke-Mosaic 'gradientTarget' 'window' | Out-Null
    Invoke-Mosaic 'gradientApply' 'Aurora' | Out-Null
    Invoke-Mosaic 'canvasMode' 'gradient' | Out-Null
    Invoke-Mosaic 'gradientSelect' '1' | Out-Null
    $c = Get-Canvas
    Check 'mesh preset applied to the window' { $c.usesGradient.window -and $c.studio.kind -eq 'mesh' -and $c.studio.selection -eq 1 }
    Shot 'canvas-gradient-studio'
    $before = (Get-Canvas).meshImagesRendered
    Start-Sleep -Seconds 2
    Check 'no rendering while idle' { (Get-Canvas).meshImagesRendered -eq $before }
    Invoke-Mosaic 'gradientSet' 'noise=0.35' | Out-Null
    Invoke-Mosaic 'gradientSet' 'kind=linear' | Out-Null
    Invoke-Mosaic 'gradientSelect' '0' | Out-Null
    Check 'linear with grain' { $c = Get-Canvas; $c.background.gradient.kind -eq 'linear' -and $c.background.gradient.noise -eq 0.35 }
    Shot 'canvas-gradient-linear'
    Invoke-Mosaic 'gradientSet' 'kind=angular' | Out-Null
    Shot 'canvas-gradient-angular'
    Invoke-Mosaic 'gradientTarget' 'newTab' | Out-Null
    Invoke-Mosaic 'gradientApply' 'Sunset' | Out-Null
    Check 'New Tab page has its own background' { (Get-Canvas).newTabBackground -eq 'custom' }
    Invoke-Mosaic 'gradientTarget' 'workspace' | Out-Null
    Invoke-Mosaic 'gradientApply' 'Forest' | Out-Null
    Check 'workspace has its own background' { (Get-Canvas).usesGradient.workspace }
    Invoke-Mosaic 'gradientTarget' 'window' | Out-Null

    Write-Host 'Configurations'
    Invoke-Mosaic 'configSave' 'CI Layout' | Out-Null
    $export = Join-Path $OutputDirectory 'ci-layout.json'
    Invoke-Mosaic 'configExport' 'CI Layout' $export | Out-Null
    Check 'export writes the macOS format' { (Get-Content $export -Raw | ConvertFrom-Json).format -eq 'dev.mosaic.layout' }
    Invoke-Mosaic 'configImport' (Resolve-Path (Join-Path $fixtures 'macos-export-custom.json')).Path | Out-Null
    $c = Get-Canvas
    Check 'a macOS file imports cleanly' { $c.lastImport.name -eq 'GX Aurora' -and $c.lastImport.error -eq '' -and $c.lastImport.messages.Count -eq 0 }
    Check 'the imported configuration is in use' { $c.activeConfiguration -eq 'GX Aurora' }
    Invoke-Mosaic 'configImport' (Resolve-Path (Join-Path $fixtures 'macos-export-safari.json')).Path | Out-Null
    Check 'a Safari layout imports as Edge' { $c = Get-Canvas; $c.activePreset -eq 'edge' }
    Invoke-Mosaic 'canvasMode' 'configurations' | Out-Null
    Shot 'canvas-configurations'
    Invoke-Mosaic 'configApply' 'CI Layout' | Out-Null
    Check 'apply switches back' { (Get-Canvas).activeConfiguration -eq 'CI Layout' }
    $saved = Join-Path $env:MOSAIC_DATA_DIR 'Canvas\configurations.json'
    if ($env:MOSAIC_DATA_DIR) { Check 'library saved where macOS keeps it (Canvas\configurations.json)' { Test-Path $saved } }
}
catch {
    Write-Host "  FAIL  $($_.Exception.Message)"
    $failures.Add($_.Exception.Message)
}
finally {
    if ($app) {
        try { Send-MosaicCommand -Do 'quit' | Out-Null } catch { }
        if (-not $app.WaitForExit(15000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) canvas check(s) failed."
    exit 1
}
Write-Host 'All canvas checks passed.'
