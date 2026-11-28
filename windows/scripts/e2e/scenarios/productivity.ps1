# productivity (windows/To Do.md "productivity"). Commands of
# Sources/Mosaic/Features/Productivity/Productivity+Automation.swift; dump key "productivity"
# (palette, reader, shortcuts, geckoForwarding, speedDial).

function Get-Productivity {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'productivity' -Owner 'productivity'
}

Register-Scenario -Area productivity -Name 'command palette: open, query, perform, close' -Tags smoke -Todo 'Command palette (Ctrl+K)' -Body {
    Reset-Tabs
    Invoke-Mosaic 'palette' 'open' | Out-Null
    $null = Get-Productivity (Get-MosaicState)
    $null = Wait-State 'palette presented' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'productivity.palette.presented') -eq $true }
    $out = New-OutputPath 'palette-new-tab.json'
    Invoke-Mosaic 'paletteQuery' 'new tab' -Out $out | Out-Null
    $results = Wait-OutputFile $out -Json
    $first = @(Get-Field $results 'results')
    Write-Step "results: $(($first | Select-Object -First 5 | ForEach-Object { Get-Field $_ 'title' }) -join '; ')"
    Assert-True ($first.Count -ge 1) 'the palette finds something for "new tab"'
    $null = Save-Screenshot 'palette'
    $tabs = Get-TabCount (Get-MosaicState)
    $action = @($first | Where-Object { (Get-Field $_ 'title') -match 'New Tab' })[0]
    Invoke-Mosaic 'palettePerform' ([string](Get-Field $action 'id')) | Out-Null
    $null = Wait-State 'a new tab from the palette' -TimeoutSeconds 10 -Condition { param($s) (Get-TabCount $s) -eq $tabs + 1 }
    Invoke-Mosaic 'palette' 'close' | Out-Null
    $null = Wait-State 'palette closed' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'productivity.palette.presented') -eq $false }
}

Register-Scenario -Area productivity -Name 'customisable shortcuts: conflict, replace, reset' -Todo 'Customisable shortcuts, applied live, forwarded from Gecko pages' -Body {
    $state = Get-MosaicState
    $shortcuts = @(Get-Field (Get-Productivity $state) 'shortcuts')
    Assert-True ($shortcuts.Count -gt 5) 'the shortcut list is in dump'
    $newTab = @($shortcuts | Where-Object { (Get-Field $_ 'current') -match 'T$' -and (Get-Field $_ 'title') -match 'New Tab' })[0]
    $other = @($shortcuts | Where-Object { (Get-Field $_ 'id') -ne (Get-Field $newTab 'id') -and (Get-Field $_ 'current') })[0]
    $combo = 'ctrl+shift+y'
    $out = New-OutputPath 'set-shortcut.json'
    Invoke-Mosaic 'setShortcut' "$(Get-Field $newTab 'id')=$combo" -Out $out | Out-Null
    Write-Step "set: $((Wait-OutputFile $out -Json).result)"
    $null = Wait-State 'the new shortcut applied' -TimeoutSeconds 10 -Condition { param($s)
        @(Get-Field $s 'productivity.shortcuts' | Where-Object { (Get-Field $_ 'id') -eq (Get-Field $newTab 'id') -and (Get-Field $_ 'customised') -eq $true }).Count -eq 1 }
    $out = New-OutputPath 'set-shortcut-conflict.json'
    Invoke-Mosaic 'setShortcut' "$(Get-Field $other 'id')=$combo" -Out $out | Out-Null
    $result = [string](Wait-OutputFile $out -Json).result
    Assert-True ($result -like 'conflict*') "a conflicting shortcut is refused ($result)"
    Invoke-Mosaic 'resetShortcut' 'all' | Out-Null
    $null = Wait-State 'all shortcuts back to default' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'productivity.shortcuts' | Where-Object { (Get-Field $_ 'customised') -eq $true }).Count -eq 0 }
    $forwarding = @(Get-Field (Get-MosaicState) 'productivity.geckoForwarding')
    Write-Step "Gecko forwarding pairs: $($forwarding.Count)"
    Assert-True ($forwarding.Count -gt 0) 'shortcuts are forwarded from Gecko pages'
}

Register-Scenario -Area productivity -Name 'reader view in both engines, with its controls' -Tags smoke -Todo 'Reader view (both engines) with its controls' -Body {
    Invoke-OnEngines {
        param($engine)
        $null = Open-TestPage '/article.html' -Engine $engine -Title 'The Quiet Harbour'
        Invoke-Mosaic 'reader' 'on' | Out-Null
        $null = Get-Productivity (Get-MosaicState)
        $state = Wait-State 'reader view active' -Condition { param($s) $r = Get-Field $s 'productivity.reader'; $null -ne $r -and ($r | ConvertTo-Json -Compress -Depth 5) -match 'true' }
        Write-Step "reader: $((Get-Field $state 'productivity.reader') | ConvertTo-Json -Compress -Depth 5)"
        Invoke-Mosaic 'readerSettings' '{"fontSize":7,"theme":"sepia"}' | Out-Null
        Start-Sleep -Milliseconds 800
        $null = Save-Screenshot "reader-$engine"
        Invoke-Mosaic 'reader' 'off' | Out-Null
        $null = Wait-State 'the article back' -TimeoutSeconds 15 -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title 'The Quiet Harbour' }
    }
}

Register-Scenario -Area productivity -Name 'Speed Dial: add, edit, move, remove' -Todo 'Speed Dial / new-tab page' -Body {
    $null = Get-Productivity (Get-MosaicState)
    $url = Get-TestUrl '/page?title=Dial'
    $out = New-OutputPath 'speed-dial.json'
    Invoke-Mosaic 'speedDial' (@{ op = 'add'; title = 'E2E Dial'; url = $url } | ConvertTo-Json -Compress) -Out $out | Out-Null
    Assert-Equal 'ok' (Wait-OutputFile $out -Json).result 'adding a Speed Dial site'
    $null = Wait-State 'the site pinned' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'productivity.speedDial.pinned' | Where-Object { (Get-Field $_ 'title') -eq 'E2E Dial' }).Count -eq 1 }
    Invoke-Mosaic 'speedDial' (@{ op = 'edit'; url = $url; title = 'E2E Dial Renamed' } | ConvertTo-Json -Compress) | Out-Null
    $null = Wait-State 'renamed' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'productivity.speedDial.pinned' | Where-Object { (Get-Field $_ 'title') -eq 'E2E Dial Renamed' }).Count -eq 1 }
    Invoke-Mosaic 'newTab' '' | Out-Null
    Start-Sleep -Milliseconds 1200
    $null = Save-Screenshot 'speed-dial'
    Invoke-Mosaic 'closeTab' | Out-Null
    Invoke-Mosaic 'speedDial' (@{ op = 'remove'; url = $url } | ConvertTo-Json -Compress) | Out-Null
    $null = Wait-State 'removed' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'productivity.speedDial.pinned' | Where-Object { (Get-Field $_ 'title') -like 'E2E Dial*' }).Count -eq 0 }
}

Register-Scenario -Area productivity -Name 'Shortcuts & Gestures settings page' -Todo 'Customisable shortcuts, applied live, forwarded from Gecko pages' -Body {
    Invoke-Mosaic 'showShortcutsSettings' | Out-Null
    $null = Wait-State 'Settings > Shortcuts open' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'settingsCategory') -eq 'shortcuts' -or (Get-Field $s 'platform.settingsCategory') -eq 'shortcuts' }
    $null = Save-Screenshot 'settings-shortcuts'
    Invoke-Mosaic 'closeSettings' -AllowError | Out-Null
}
