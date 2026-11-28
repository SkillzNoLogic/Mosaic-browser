# tabs: tabs, workspaces, sessions (windows/To Do.md "tabs"). Commands and dump fields of
# Sources/Mosaic/Features/Tabs/TabsAutomation.swift (tab index = among the current workspace's
# tabs; "a|b" for two-part values); per-tab dump fields pinned, muted, workspace, group,
# asleep; top-level "tabsArea".

function Get-TabsArea {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'tabsArea' -Owner 'tabs'
}

function Open-TabSet {
    # Resets to known tabs: one, two, three (on Gecko unless told otherwise).
    param([string]$Engine = 'gecko', [string[]]$Titles = @('Tab One', 'Tab Two', 'Tab Three'))
    Reset-Tabs -Engine $Engine
    $first = $true
    foreach ($title in $Titles) {
        $path = '/page?title=' + [uri]::EscapeDataString($title)
        if ($first) { $null = Open-TestPage $path -Engine $Engine -Title $title; $first = $false }
        else { $null = Open-TestPage $path -Engine $Engine -Title $title -NewTab }
    }
}

Register-Scenario -Area tabs -Name 'pin and unpin a tab' -Tags smoke -Todo 'Pin, duplicate, mute; recently closed (Ctrl+Shift+T); tab search (Ctrl+Shift+A)' -Body {
    Open-TabSet
    Invoke-Mosaic 'pin' | Out-Null
    $state = Get-MosaicState
    $null = Require-Field (Get-SelectedTab $state) 'pinned' -Owner 'tabs'
    $null = Wait-State 'the selected tab pinned' -TimeoutSeconds 10 -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'pinned') -eq $true }
    $null = Save-Screenshot 'pinned'
    Invoke-Mosaic 'unpin' | Out-Null
    $null = Wait-State 'unpinned' -TimeoutSeconds 10 -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'pinned') -eq $false }
}

Register-Scenario -Area tabs -Name 'mute and unmute a playing tab' -Todo 'Pin, duplicate, mute; recently closed (Ctrl+Shift+T); tab search (Ctrl+Shift+A)' -Body {
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        $null = Open-TestPage "/media.html?autoplay=1&token=$token" -Engine $engine -Title 'Mosaic Audio Page'
        Invoke-Mosaic 'page.click' '300,200' -AllowError | Out-Null   # a gesture, where autoplay is not allowed
        $null = Wait-Report -Token $token -Key 'playing' -Value 'yes' -What 'the page to play audio'
        Invoke-Mosaic 'mute' | Out-Null
        $null = Require-Field (Get-SelectedTab (Get-MosaicState)) 'muted' -Owner 'tabs'
        $null = Wait-State 'tab muted' -TimeoutSeconds 10 -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'muted') -eq $true }
        Invoke-Mosaic 'unmute' | Out-Null
        $null = Wait-State 'tab unmuted' -TimeoutSeconds 10 -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'muted') -eq $false }
    }
}

Register-Scenario -Area tabs -Name 'duplicate a tab' -Todo 'Pin, duplicate, mute; recently closed (Ctrl+Shift+T); tab search (Ctrl+Shift+A)' -Body {
    Open-TabSet -Titles @('Dup Source')
    $count = Get-TabCount (Get-MosaicState)
    Invoke-Mosaic 'duplicate' | Out-Null
    $state = Wait-State 'a duplicate of the page' -Condition { param($s)
        (Get-TabCount $s) -eq $count + 1 -and @(@(Get-Field $s 'tabs') | Where-Object { (Get-Field $_ 'title') -eq 'Dup Source' -and (Get-Field $_ 'loading') -eq $false }).Count -eq 2
    }
}

Register-Scenario -Area tabs -Name 'recently closed: reopen the last closed tab' -Tags smoke -Todo 'Pin, duplicate, mute; recently closed (Ctrl+Shift+T); tab search (Ctrl+Shift+A)' -Body {
    Open-TabSet -Titles @('Keep Me', 'Close Me')
    Invoke-Mosaic 'closeTab' | Out-Null
    $state = Wait-State 'Close Me closed' -Condition { param($s) @(@(Get-Field $s 'tabs') | Where-Object { (Get-Field $_ 'title') -eq 'Close Me' }).Count -eq 0 }
    $area = Get-TabsArea $state
    Assert-True (@(Get-Field $area 'recentlyClosed' | Where-Object { (Get-Field $_ 'title') -eq 'Close Me' }).Count -gt 0) 'Close Me is in recently closed'
    Invoke-Mosaic 'reopenClosed' | Out-Null
    $null = Wait-State 'Close Me reopened' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title 'Close Me' }
    Invoke-Mosaic 'clearRecentlyClosed' | Out-Null
    $null = Wait-State 'recently closed cleared' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'tabsArea.recentlyClosed').Count -eq 0 }
}

Register-Scenario -Area tabs -Name 'tab search finds tabs by title' -Todo 'Pin, duplicate, mute; recently closed (Ctrl+Shift+T); tab search (Ctrl+Shift+A)' -Body {
    Open-TabSet -Titles @('Alpha Search', 'Beta Search', 'Gamma Other')
    Invoke-Mosaic 'tabSearch' 'show' | Out-Null
    $null = Get-TabsArea (Get-MosaicState)
    $null = Wait-State 'tab search visible' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'tabsArea.tabSearchVisible') -eq $true }
    Invoke-Mosaic 'tabSearchQuery' 'search' | Out-Null
    $state = Wait-State 'two results for "search"' -TimeoutSeconds 10 -Condition { param($s)
        $r = @(Get-Field $s 'tabsArea.tabSearchResults'); $r.Count -eq 2 -and $r -contains 'Alpha Search' -and $r -contains 'Beta Search'
    }
    $null = Save-Screenshot 'tab-search'
    Invoke-Mosaic 'tabSearch' 'hide' | Out-Null
}

Register-Scenario -Area tabs -Name 'workspaces: create, rename, move a tab, switch, delete' -Tags smoke -Todo 'Tab groups; workspaces (create, switch, delete, restore, move tabs)' -Body {
    Open-TabSet -Titles @('Home Tab', 'Work Tab')
    Invoke-Mosaic 'newWorkspace' 'E2E Work' | Out-Null
    $null = Get-TabsArea (Get-MosaicState)
    $null = Wait-State 'workspace E2E Work exists' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'tabsArea.workspaces' | Where-Object { (Get-Field $_ 'name') -eq 'E2E Work' }).Count -eq 1 }
    $first = [string](Get-Field (Get-MosaicState) 'tabsArea.workspaces.0.name')
    Invoke-Mosaic 'selectWorkspace' $first | Out-Null
    $state = Wait-State "back in $first" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'tabsArea.selectedWorkspace') -eq $first }
    $index = @(Get-Field $state 'tabsArea.visibleTitles').IndexOf('Work Tab')
    Assert-True ($index -ge 0) 'Work Tab is visible in the first workspace'
    Invoke-Mosaic 'moveToWorkspace' "$index|E2E Work" | Out-Null
    $null = Wait-State 'Work Tab moved' -TimeoutSeconds 10 -Condition { param($s) @(@(Get-Field $s 'tabs') | Where-Object { (Get-Field $_ 'title') -eq 'Work Tab' -and (Get-Field $_ 'workspace') -eq 'E2E Work' }).Count -eq 1 }
    Invoke-Mosaic 'renameWorkspace' 'E2E Work|E2E Renamed' | Out-Null
    $null = Wait-State 'renamed' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'tabsArea.workspaces' | Where-Object { (Get-Field $_ 'name') -eq 'E2E Renamed' }).Count -eq 1 }
    Invoke-Mosaic 'selectWorkspace' 'E2E Renamed' | Out-Null
    $null = Wait-State 'only Work Tab visible' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'tabsArea.selectedWorkspace') -eq 'E2E Renamed' -and @(Get-Field $s 'tabsArea.visibleTitles') -contains 'Work Tab' -and @(Get-Field $s 'tabsArea.visibleTitles') -notcontains 'Home Tab' }
    $null = Save-Screenshot 'workspace'
    Invoke-Mosaic 'previousWorkspace' | Out-Null
    $null = Wait-State "previous workspace ($first)" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'tabsArea.selectedWorkspace') -eq $first }
    Invoke-Mosaic 'deleteWorkspace' 'E2E Renamed' | Out-Null
    $null = Wait-State 'workspace deleted' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'tabsArea.workspaces' | Where-Object { (Get-Field $_ 'name') -eq 'E2E Renamed' }).Count -eq 0 }
}

Register-Scenario -Area tabs -Name 'tab groups: create, add, rename, colour, collapse, ungroup' -Todo 'Tab groups; workspaces (create, switch, delete, restore, move tabs)' -Body {
    Open-TabSet -Titles @('Group A', 'Group B', 'Loose')
    Invoke-Mosaic 'selectVisible' '0' | Out-Null
    Invoke-Mosaic 'group' 'E2E Group|blue' | Out-Null
    $null = Get-TabsArea (Get-MosaicState)
    $null = Wait-State 'group created' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'tabsArea.groups' | Where-Object { (Get-Field $_ 'name') -eq 'E2E Group' }).Count -eq 1 }
    Invoke-Mosaic 'addToGroup' '1|E2E Group' | Out-Null
    $null = Wait-State 'two tabs in the group' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field (@(Get-Field $s 'tabsArea.groups' | Where-Object { (Get-Field $_ 'name') -eq 'E2E Group' })[0]) 'tabs').Count -eq 2 }
    Invoke-Mosaic 'renameGroup' 'E2E Group|E2E Team' | Out-Null
    Invoke-Mosaic 'groupColor' 'E2E Team|green' | Out-Null
    $null = Wait-State 'renamed and green' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'tabsArea.groups' | Where-Object { (Get-Field $_ 'name') -eq 'E2E Team' -and (Get-Field $_ 'color') -eq 'green' }).Count -eq 1 }
    Invoke-Mosaic 'collapseGroup' 'E2E Team' | Out-Null
    $null = Wait-State 'collapsed' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'tabsArea.groups' | Where-Object { (Get-Field $_ 'collapsed') -eq $true }).Count -eq 1 }
    $null = Save-Screenshot 'group-collapsed'
    Invoke-Mosaic 'expandGroup' 'E2E Team' | Out-Null
    Invoke-Mosaic 'ungroup' 'E2E Team' | Out-Null
    $null = Wait-State 'no groups' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'tabsArea.groups').Count -eq 0 }
}

Register-Scenario -Area tabs -Name 'reorder tabs (moveTab, the drag-and-drop model path)' -Todo 'Drag-and-drop reordering; duplicate-tab detection' -Body {
    Open-TabSet -Titles @('Order 1', 'Order 2', 'Order 3')
    Invoke-Mosaic 'moveTab' '0|2' | Out-Null
    $null = Get-TabsArea (Get-MosaicState)
    $null = Wait-State 'Order 1 last' -TimeoutSeconds 10 -Condition { param($s) $t = @(Get-Field $s 'tabsArea.visibleTitles'); $t.Count -ge 3 -and $t[-1] -eq 'Order 1' }
}

Register-Scenario -Area tabs -Name 'duplicate-tab detection offers to switch' -Todo 'Drag-and-drop reordering; duplicate-tab detection' -Body {
    Invoke-Mosaic 'tabSetting' 'offerSwitchToOpenTab=true' | Out-Null
    Open-TabSet -Titles @('Already Open')
    Invoke-Mosaic 'newTab' '' | Out-Null
    Invoke-Mosaic 'navigate' (Get-TestUrl '/page?title=Already%20Open') | Out-Null
    $state = Wait-State 'a duplicate-tab offer' -Condition { param($s) $null -ne (Get-Field (Get-TabsArea $s) 'duplicateOffer') }
    Write-Step "offer: $((Get-Field $state 'tabsArea.duplicateOffer') | ConvertTo-Json -Compress)"
    $null = Save-Screenshot 'duplicate-offer'
    Invoke-Mosaic 'acceptDuplicate' | Out-Null
    $null = Wait-State 'switched to the open tab' -Condition { param($s) $null -eq (Get-Field $s 'tabsArea.duplicateOffer') -and (Get-Field (Get-SelectedTab $s) 'title') -eq 'Already Open' }
}

Register-Scenario -Area tabs -Name 'session saved and restored after a restart (both engines)' -Tags smoke -Todo 'Session save and restore; crash recovery; private tabs never saved' -Body {
    Invoke-Mosaic 'tabSetting' 'startup=restore' -AllowError | Out-Null
    Reset-Tabs
    $null = Open-TestPage '/page?title=Session%20Gecko' -Engine gecko -Title 'Session Gecko'
    $null = Open-TestPage '/page?title=Session%20Chromium' -Engine chromium -Title 'Session Chromium' -NewTab
    Invoke-Mosaic 'saveSession' | Out-Null
    $state = Get-MosaicState
    $file = [string](Get-Field (Get-TabsArea $state) 'sessionFile')
    Write-Step "session file: $file"
    Assert-True ($file -and (Test-Path -LiteralPath $file)) 'the session file exists'
    Add-Evidence $file
    $null = Restart-MosaicApp
    $state = Get-MosaicState
    $offer = Get-Field $state 'tabsArea.offersCrashRecovery'
    if ((Get-Field $state 'tabsArea.previousSessionPages') -gt 0 -and @(@(Get-Field $state 'tabs') | Where-Object { (Get-Field $_ 'title') -like 'Session *' }).Count -eq 0) {
        Invoke-Mosaic 'restoreSession' | Out-Null
    }
    $state = Wait-State 'both pages back, each on its engine' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s)
        $tabs = @(Get-Field $s 'tabs')
        @($tabs | Where-Object { (Get-Field $_ 'title') -eq 'Session Gecko' -and (Get-Field $_ 'engine') -eq 'gecko' }).Count -eq 1 -and
        @($tabs | Where-Object { (Get-Field $_ 'title') -eq 'Session Chromium' -and (Get-Field $_ 'engine') -eq 'chromium' }).Count -eq 1
    }
}

Register-Scenario -Area tabs -Name 'crash recovery after Mosaic is killed' -Todo 'Session save and restore; crash recovery; private tabs never saved' -Body {
    Reset-Tabs
    $null = Open-TestPage '/page?title=Before%20Crash' -Engine gecko -Title 'Before Crash'
    Invoke-Mosaic 'saveSession' | Out-Null
    $null = Get-TabsArea (Get-MosaicState)
    $null = Restart-MosaicApp -Kill
    $state = Wait-State 'crash recovery offered (or the page restored)' -TimeoutSeconds 30 -Condition { param($s)
        (Get-Field $s 'tabsArea.offersCrashRecovery') -eq $true -or @(@(Get-Field $s 'tabs') | Where-Object { (Get-Field $_ 'title') -eq 'Before Crash' }).Count -gt 0
    }
    Write-Step "previousRun=$(Get-Field $state 'tabsArea.previousRun'), offersCrashRecovery=$(Get-Field $state 'tabsArea.offersCrashRecovery')"
    $null = Save-Screenshot 'crash-recovery'
    if ((Get-Field $state 'tabsArea.offersCrashRecovery') -eq $true) { Invoke-Mosaic 'restoreSession' | Out-Null }
    $null = Wait-State 'Before Crash restored' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) @(@(Get-Field $s 'tabs') | Where-Object { (Get-Field $_ 'title') -eq 'Before Crash' }).Count -gt 0 }
}

Register-Scenario -Area tabs -Name 'private tabs are never saved in the session' -Todo 'Session save and restore; crash recovery; private tabs never saved' -Body {
    $token = New-Token
    Reset-Tabs
    Invoke-Mosaic 'privacy.newPrivateTab' (Get-TestUrl "/page?title=Private%20$token") | Out-Null
    $null = Wait-State 'the private page loaded' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'title') -eq "Private $token" }
    Invoke-Mosaic 'saveSession' | Out-Null
    $file = [string](Get-Field (Get-TabsArea (Get-MosaicState)) 'sessionFile')
    Start-Sleep -Seconds 1
    $hits = @(Find-TextInFolder -Folder (Get-MosaicSession).DataDirectory -Text $token)
    Assert-True ($hits.Count -eq 0) "the private tab's address is nowhere in the data folder (found in: $($hits -join ', '))"
}

Register-Scenario -Area tabs -Name 'sleeping tabs: inactive tabs unload, pinned exception, memory pressure' -Todo 'Sleeping tabs with exceptions and memory pressure; Tabs & Workspaces and Performance settings' -Body {
    Open-TabSet -Titles @('Sleepy 1', 'Sleepy 2', 'Awake')
    Invoke-Mosaic 'tabSetting' 'unloadInactiveTabs=true' | Out-Null
    Invoke-Mosaic 'tabSetting' 'inactiveMinutes=5' | Out-Null
    Invoke-Mosaic 'tabSetting' 'neverUnloadPinned=true' | Out-Null
    Invoke-Mosaic 'pin' '1' | Out-Null
    Invoke-Mosaic 'ageTab' '0|120' | Out-Null
    Invoke-Mosaic 'ageTab' '1|120' | Out-Null
    Invoke-Mosaic 'sleepCheck' 'normal' | Out-Null
    $null = Get-TabsArea (Get-MosaicState)
    $state = Wait-State 'the old unpinned tab asleep, the pinned one awake' -Condition { param($s)
        $tabs = @(Get-Field $s 'tabs')
        @($tabs | Where-Object { (Get-Field $_ 'title') -eq 'Sleepy 1' -and (Get-Field $_ 'asleep') -eq $true }).Count -eq 1 -and
        @($tabs | Where-Object { (Get-Field $_ 'title') -eq 'Sleepy 2' -and (Get-Field $_ 'asleep') -eq $true }).Count -eq 0
    }
    Invoke-Mosaic 'unpin' '1' | Out-Null
    Invoke-Mosaic 'memoryPressure' 'critical' | Out-Null
    Invoke-Mosaic 'unloadBackground' | Out-Null
    $null = Wait-State 'background tabs asleep under memory pressure' -Condition { param($s) [int](Get-Field $s 'tabsArea.asleep') -ge 2 }
    Invoke-Mosaic 'memoryPressure' 'normal' | Out-Null
    Invoke-Mosaic 'selectVisible' '0' | Out-Null
    $null = Wait-State 'a selected tab wakes up' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'asleep') -eq $false -and (Get-Field (Get-SelectedTab $s) 'loading') -eq $false }
}

Register-Scenario -Area tabs -Name 'Tabs & Workspaces and Performance settings pages' -Todo 'Sleeping tabs with exceptions and memory pressure; Tabs & Workspaces and Performance settings' -Body {
    foreach ($category in @('tabs', 'performance')) {
        Invoke-Mosaic 'settings' $category | Out-Null
        Start-Sleep -Milliseconds 800
        $null = Save-Screenshot "settings-$category"
    }
    Invoke-Mosaic 'closeSettings' | Out-Null
}
