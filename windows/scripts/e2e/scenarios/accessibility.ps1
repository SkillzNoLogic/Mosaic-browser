# accessibility: UI Automation scan of Mosaic's own UI (windows/To Do.md "qa"; checks the
# shell's "Keyboard access everywhere; UI Automation names on every control"). Reports go to
# accessibility\<surface>.md / .json. Web page content (WebView2, Gecko) is not scanned.
#
# The Tab walk takes the keyboard focus, so it only runs on CI runners ($env:CI) or when
# MOSAIC_E2E_KEYBOARD=1; elsewhere only names and focusability are checked.

function Invoke-AccessibilityScan {
    param([Parameter(Mandatory = $true)] [string]$Surface, [switch]$TabWalk)
    $window = Get-MosaicMainWindow
    Assert-True ($null -ne $window) 'Mosaic has a visible main window'
    $scan = Get-UiaScan -WindowHandle $window.Handle
    if ($TabWalk -and ($env:CI -or $env:MOSAIC_E2E_KEYBOARD -eq '1')) {
        $stops = Invoke-TabWalk -WindowHandle $window.Handle -ProcessId (Get-MosaicSession).Process.Id
        Add-TabWalkFindings -Scan $scan -Stops $stops
        Write-Step "Tab walk: $(@($stops).Count) stops"
    }
    $directory = Join-Path (Get-E2EConfig).OutputDirectory 'accessibility'
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $report = Write-UiaReport -Scan $scan -BasePath (Join-Path $directory (ConvertTo-SafeName $Surface)) -Title "Accessibility: $Surface"
    Add-Evidence $report
    Write-Step "$Surface`: $($scan.Elements.Count) elements, $($scan.Unnamed) unnamed, $($scan.NotFocusable) not focusable, $($scan.NotReached) not reached by Tab"
    foreach ($issue in @($scan.Issues | Select-Object -First 15)) { Write-Step "  $($issue.Kind): $($issue.Type) $($issue.AutomationId) at $($issue.Rect) [$($issue.Path)]" }
    $scan
}

function Assert-ScanClean {
    param([Parameter(Mandatory = $true)] $Scan, [Parameter(Mandatory = $true)] [string]$Surface)
    $total = $Scan.Unnamed + $Scan.NotFocusable + $Scan.NotReached
    Assert-True ($Scan.Elements.Count -gt 1) "$Surface exposes a UI Automation tree"
    if ($total -gt 0) {
        throw "${Surface}: $($Scan.Unnamed) control(s) without a UI Automation name, $($Scan.NotFocusable) not keyboard-focusable, $($Scan.NotReached) not reached by Tab (see accessibility\$(ConvertTo-SafeName $Surface).md)"
    }
}

Register-Scenario -Area accessibility -Name 'main window: names and keyboard access' -Tags smoke -NoPipe -Todo 'Keyboard access everywhere; UI Automation names on every control' -Body {
    if ((Get-MosaicSession).PipeUp) {
        try { $null = Open-TestPage '/links.html' -Engine chromium -Title 'Mosaic Links Page' } catch {
            if ($_.Exception -is [MosaicE2E.HangException] -or $_.Exception -is [MosaicE2E.CrashException]) { throw }
        }
    }
    else { Start-Sleep -Seconds 3 }
    $scan = Invoke-AccessibilityScan -Surface 'main-window' -TabWalk
    Assert-ScanClean $scan 'main window'
}

Register-Scenario -Area accessibility -Name 'every Mosaic preset: names and keyboard access' -Todo 'Keyboard access everywhere; UI Automation names on every control' -Body {
    $problems = @()
    foreach ($preset in @('zenArc', 'chrome', 'opera', 'edge')) {
        Invoke-Mosaic 'mosaic' $preset | Out-Null
        Start-Sleep -Milliseconds 800
        $scan = Invoke-AccessibilityScan -Surface "mosaic-$preset"
        try { Assert-ScanClean $scan "Mosaic $preset" } catch { $problems += $_.Exception.Message }
    }
    if ($problems.Count -gt 0) { throw ($problems -join ' | ') }
}

Register-Scenario -Area accessibility -Name 'every settings page: names and keyboard access' -Todo 'Keyboard access everywhere; UI Automation names on every control' -Body {
    $problems = @()
    foreach ($category in @('mosaics', 'general', 'accessibility', 'tabs', 'performance', 'search', 'privacy',
            'passwords', 'downloads', 'engine', 'extensions', 'languages', 'shortcuts', 'about')) {
        Invoke-Mosaic 'settings' $category | Out-Null
        Start-Sleep -Milliseconds 800
        $scan = Invoke-AccessibilityScan -Surface "settings-$category"
        try { Assert-ScanClean $scan "Settings $category" } catch { $problems += $_.Exception.Message }
    }
    try { Invoke-Mosaic 'closeSettings' -Quiet | Out-Null } catch { }
    if ($problems.Count -gt 0) { throw ($problems -join ' | ') }
}
