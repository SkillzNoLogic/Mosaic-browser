# shell: Mosaics and the composed window (windows/To Do.md "shell"). Commands and dump fields
# of Sources/Mosaic/Shell/ShellAutomation.swift: mosaic, previewMosaic, restoreMosaic,
# mosaicSet, importLayout, exportLayout, settings, closeSettings, sidebar, shellSet, home;
# dump: mosaic, previewMosaic, settingsCategory, sidebarVisible, layout.

Register-Scenario -Area shell -Name 'apply each Mosaic preset (Zen/Arc, Chrome, Opera, Opera GX, Edge)' -Tags smoke -Todo 'Mosaic: Zen / Arc; Chrome; Opera / Opera GX; Microsoft Edge' -Body {
    $null = Open-TestPage '/page?title=Mosaic%20Shell%20Page' -Engine chromium -Title 'Mosaic Shell Page'
    foreach ($preset in @('zenArc', 'chrome', 'opera', 'edge')) {
        $p = $preset
        Invoke-Mosaic 'mosaic' $p | Out-Null
        $state = Get-MosaicState
        $null = Require-Field $state 'mosaic' -Owner 'shell'
        $null = Wait-State "mosaic=$p" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'mosaic') -eq $p }
        Assert-Equal $p (Get-Field (Get-MosaicState) 'layout.preset') "layout.preset after 'mosaic $p'"
        $null = Save-Screenshot "preset-$p"
    }
    Invoke-Mosaic 'mosaic' 'opera' | Out-Null
    Invoke-Mosaic 'mosaicSet' 'gx=on' | Out-Null
    $state = Wait-State 'GX styling (dark scheme)' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'layout.appearance.colorScheme') -eq 'dark' }
    $null = Save-Screenshot 'preset-operaGX'
    Invoke-Mosaic 'mosaicSet' 'gx=off' | Out-Null
}

Register-Scenario -Area shell -Name 'no Safari Mosaic on Windows' -Todo 'No Safari Mosaic on Windows (macOS only), and layouts imported from macOS that use it fall back cleanly' -Body {
    Invoke-Mosaic 'mosaic' 'chrome' | Out-Null
    $reply = Invoke-Mosaic 'mosaic' 'safari' -AllowError
    $state = Get-MosaicState
    $null = Require-Field $state 'mosaic' -Owner 'shell'
    Write-Step "reply: $($reply | ConvertTo-Json -Compress); mosaic=$(Get-Field $state 'mosaic')"
    Assert-True ((Get-Field $state 'mosaic') -ne 'safari') 'the Safari preset cannot be applied on Windows'
}

Register-Scenario -Area shell -Name 'a macOS layout using the Safari preset falls back cleanly' -Todo 'No Safari Mosaic on Windows (macOS only), and layouts imported from macOS that use it fall back cleanly' -Body {
    $exported = New-OutputPath 'layout-for-safari.json'
    Invoke-Mosaic 'exportLayout' $exported | Out-Null
    $layout = Wait-OutputFile $exported -Json
    $layout.preset = 'safari'
    if ($layout.PSObject.Properties['tabStyle']) { $layout.tabStyle = 'safari' }
    if ($layout.PSObject.Properties['addressBar']) { $layout.addressBar = 'unified' }
    $path = New-OutputPath 'layout-safari.json'
    Write-TextFile $path ($layout | ConvertTo-Json -Depth 12)
    Invoke-Mosaic 'importLayout' $path -AllowError | Out-Null
    $state = Get-MosaicState
    Write-Step "after import: mosaic=$(Get-Field $state 'mosaic'), layout.preset=$(Get-Field $state 'layout.preset'), tabStyle=$(Get-Field $state 'layout.tabStyle')"
    Assert-True ((Get-Field $state 'mosaic') -ne 'safari') 'the imported layout does not activate a Safari Mosaic'
    $null = Save-Screenshot 'safari-layout-fallback'
}

Register-Scenario -Area shell -Name 'switching Mosaics is instant: no restart, no reload' -Tags smoke -Todo 'Switching Mosaics is instant: no restart, no page reload' -Body {
    Invoke-OnEngines {
        param($engine)
        $state = Open-TestPage "/page?title=No%20Reload%20$engine" -Engine $engine -Title "No Reload $engine"
        $before = @(Get-TestRequests -PathPrefix '/page').Count
        $process = (Get-MosaicSession).Process.Id
        foreach ($preset in @('chrome', 'zenArc', 'edge', 'opera', 'chrome')) {
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            Invoke-Mosaic 'mosaic' $preset | Out-Null
            $p = $preset
            $null = Wait-State "mosaic=$p" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'mosaic') -eq $p }
            Write-Step "mosaic $p applied in $([int]$clock.Elapsed.TotalMilliseconds) ms"
        }
        $state = Get-MosaicState
        Assert-True (Test-TabShows (Get-SelectedTab $state) -Title "No Reload $engine" -Engine $engine) 'the tab still shows the page on the same engine'
        Assert-Equal $before @(Get-TestRequests -PathPrefix '/page').Count 'requests for the page (a reload would add one)'
        Assert-Equal $process (Get-MosaicSession).Process.Id 'Mosaic''s process (no restart)'
    }
}

Register-Scenario -Area shell -Name 'appearance: scheme, density, corner radius, accent, materials' -Todo 'Accent colour, light/dark/system, density, corner radius, materials toggle (Mica/Acrylic <-> solid)' -Body {
    Invoke-Mosaic 'mosaic' 'chrome' | Out-Null
    $checks = [ordered]@{
        'scheme=dark'     = @('layout.appearance.colorScheme', 'dark')
        'scheme=light'    = @('layout.appearance.colorScheme', 'light')
        'density=compact' = @('layout.appearance.density', 'compact')
        'radius=14'       = @('layout.appearance.cornerRadius', '14')
        'accent=#FF0066'  = @('layout.appearance.accent', '#FF0066')
        'glass=off'       = @('layout.appearance.material', 'solid')
    }
    foreach ($assignment in $checks.Keys) {
        $field = $checks[$assignment][0]; $expected = $checks[$assignment][1]
        Invoke-Mosaic 'mosaicSet' $assignment | Out-Null
        $null = Require-Field (Get-MosaicState) 'layout' -Owner 'shell'
        $null = Wait-State "$field=$expected" -TimeoutSeconds 10 -Condition { param($s) [string](Get-Field $s $field) -eq $expected }
    }
    $null = Save-Screenshot 'appearance-custom'
    Invoke-Mosaic 'restoreMosaic' 'chrome' | Out-Null
    $null = Wait-State 'chrome defaults restored (radius back)' -TimeoutSeconds 10 -Condition { param($s) [string](Get-Field $s 'layout.appearance.cornerRadius') -ne '14' }
}

Register-Scenario -Area shell -Name 'Settings opens inside the window, all 14 categories' -Tags smoke -Todo 'Settings inside the main window, all 14 categories, searchable; Mosaics, General and Accessibility pages' -Body {
    $windowsBefore = @([MosaicE2E.Win32]::TopLevelWindows(@((Get-MosaicSession).Process.Id)) | Where-Object { $_.Visible }).Count
    foreach ($category in @('mosaics', 'general', 'accessibility', 'tabs', 'performance', 'search', 'privacy',
            'passwords', 'downloads', 'engine', 'extensions', 'languages', 'shortcuts', 'about')) {
        $c = $category
        Invoke-Mosaic 'settings' $c | Out-Null
        $null = Require-Field (Get-MosaicState) 'settingsCategory' -Owner 'shell'
        $null = Wait-State "settingsCategory=$c" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'settingsCategory') -eq $c }
    }
    $windowsAfter = @([MosaicE2E.Win32]::TopLevelWindows(@((Get-MosaicSession).Process.Id)) | Where-Object { $_.Visible }).Count
    Assert-True ($windowsAfter -le $windowsBefore) "Settings did not open another window ($windowsBefore -> $windowsAfter visible top-level windows)"
    Invoke-Mosaic 'closeSettings' | Out-Null
    $null = Wait-State 'settings closed' -TimeoutSeconds 10 -Condition { param($s) [string](Get-Field $s 'settingsCategory') -eq '' }
}

Register-Scenario -Area shell -Name 'layout export and import round trip (same JSON as macOS)' -Todo 'Layout model shared with macOS (regions x components, appearance), same JSON' -Body {
    Invoke-Mosaic 'mosaic' 'chrome' | Out-Null
    $path = New-OutputPath 'layout-export.json'
    Invoke-Mosaic 'exportLayout' $path | Out-Null
    $layout = Wait-OutputFile $path -Json
    foreach ($key in @('schemaVersion', 'preset', 'name', 'tabPlacement', 'tabStyle', 'addressBar', 'regions', 'bookmarksBar', 'sidebar', 'appearance')) {
        Assert-True ($null -ne $layout.PSObject.Properties[$key]) "the exported layout has '$key' (macOS MosaicLayout JSON)"
    }
    Assert-Equal 'chrome' $layout.preset 'exported preset'
    Invoke-Mosaic 'mosaicSet' 'radius=21' | Out-Null
    $null = Wait-State 'radius 21' -TimeoutSeconds 10 -Condition { param($s) [string](Get-Field $s 'layout.appearance.cornerRadius') -eq '21' }
    Invoke-Mosaic 'importLayout' $path | Out-Null
    $null = Wait-State 'the imported radius back' -TimeoutSeconds 10 -Condition { param($s) [string](Get-Field $s 'layout.appearance.cornerRadius') -eq [string]$layout.appearance.cornerRadius }
}

Register-Scenario -Area shell -Name 'sidebar show, hide, toggle' -Todo 'Mosaic: Zen / Arc (vertical sidebar, pinned grid, workspaces)' -Body {
    Invoke-Mosaic 'mosaic' 'zenArc' | Out-Null
    Invoke-Mosaic 'sidebar' 'show' | Out-Null
    $null = Require-Field (Get-MosaicState) 'sidebarVisible' -Owner 'shell'
    $null = Wait-State 'sidebar visible' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'sidebarVisible') -eq $true }
    Invoke-Mosaic 'sidebar' 'hide' | Out-Null
    $null = Wait-State 'sidebar hidden' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'sidebarVisible') -eq $false }
    Invoke-Mosaic 'sidebar' 'toggle' | Out-Null
    $null = Wait-State 'sidebar visible again' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'sidebarVisible') -eq $true }
}

Register-Scenario -Area shell -Name 'homepage and the home command' -Todo 'Settings inside the main window, all 14 categories, searchable; Mosaics, General and Accessibility pages' -Body {
    $homeUrl = Get-TestUrl '/page?title=Mosaic%20Home'
    Invoke-Mosaic 'shellSet' "homepage=$homeUrl" | Out-Null
    Invoke-Mosaic 'shellSet' 'homeButton=on' | Out-Null
    Invoke-Mosaic 'home' | Out-Null
    $null = Wait-State 'the homepage in the tab' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title 'Mosaic Home' }
}

Register-Scenario -Area shell -Name 'preview a Mosaic without applying it' -Todo 'Switching Mosaics is instant: no restart, no page reload' -Body {
    Invoke-Mosaic 'mosaic' 'chrome' | Out-Null
    Invoke-Mosaic 'previewMosaic' 'opera' | Out-Null
    $null = Require-Field (Get-MosaicState) 'previewMosaic' -Owner 'shell'
    $null = Wait-State 'previewMosaic=opera, mosaic still chrome' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'previewMosaic') -eq 'opera' -and (Get-Field $s 'mosaic') -eq 'chrome' }
    $null = Save-Screenshot 'preview-opera'
    Invoke-Mosaic 'previewMosaic' 'off' | Out-Null
    $null = Wait-State 'preview ended' -TimeoutSeconds 10 -Condition { param($s) [string](Get-Field $s 'previewMosaic') -eq '' }
}

Register-Scenario -Area shell -Name 'accessibility settings: text size, reduce motion, reduce transparency' -Todo 'Keyboard access everywhere; UI Automation names on every control; high contrast' -Body {
    foreach ($assignment in @('textSize=large', 'reduceMotion=on', 'reduceTransparency=on', 'focusRings=on')) {
        Invoke-Mosaic 'shellSet' $assignment | Out-Null
    }
    Invoke-Mosaic 'settings' 'accessibility' | Out-Null
    Start-Sleep -Milliseconds 700
    $null = Save-Screenshot 'accessibility-settings-large-text'
    foreach ($assignment in @('textSize=standard', 'reduceMotion=system', 'reduceTransparency=system', 'focusRings=off')) {
        Invoke-Mosaic 'shellSet' $assignment -Quiet | Out-Null
    }
    Invoke-Mosaic 'closeSettings' | Out-Null
}
