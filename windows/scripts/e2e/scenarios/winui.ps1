# winui: the app, window and engines (windows/To Do.md "winui"). The v0.1 parity checks that
# smoke.ps1 makes, as separate scenarios, plus window, Gecko-hiding and single-instance checks.

$script:ContractKeys = @('tabs', 'engine', 'searchEngine', 'addressText', 'chromiumAvailable', 'geckoStatus',
    'geckoProcessId', 'geckoAttached', 'notice', 'extensions', 'blocker', 'extensionError', 'extensionsBusy')

Register-Scenario -Area winui -Name 'starts and opens the automation pipe' -Tags smoke -NoPipe -Todo 'WinUI 3 app replaces the WPF app' -Body {
    $session = Get-MosaicSession
    $window = Get-MosaicMainWindow
    if ($window) { Write-Step "main window: $($window.ToString())" } else { Write-Step 'no visible main window' }
    $null = Save-Screenshot 'start'
    if (-not $session.PipeUp) { throw "No automation pipe: $($session.StartError) (CONTRACT.md: \\.\pipe\mosaic-automation with --mosaic-automation)" }
    Write-Step "pipe up after $($session.PipeSeconds) s"
    Assert-True ($null -ne $window) 'Mosaic shows a window'
}

Register-Scenario -Area winui -Name 'window fits 1024x768 and reports its DPI' -Tags smoke -NoPipe -Todo 'Per-monitor DPI, resize/maximise/minimise, restores size and position, fits 1024x768 and 1366x768' -Body {
    $window = Get-MosaicMainWindow
    Assert-True ($null -ne $window) 'Mosaic has a visible main window'
    $screenWidth = [MosaicE2E.Win32]::ScreenWidth()
    $screenHeight = [MosaicE2E.Win32]::ScreenHeight()
    $dpi = [MosaicE2E.Win32]::Dpi($window.Handle)
    Write-Step "screen ${screenWidth}x${screenHeight}; window $($window.Width)x$($window.Height) at $($window.X),$($window.Y); DPI $dpi"
    Assert-True ($dpi -ge 96) "the window reports a DPI ($dpi)"
    # A maximised window overhangs the screen by its invisible resize borders (8 px at 100 %).
    $slack = [math]::Ceiling(8 * $dpi / 96) + 1
    Assert-True ($window.Width -le $screenWidth + 2 * $slack -and $window.Height -le $screenHeight + 2 * $slack) "the window ($($window.Width)x$($window.Height)) fits the ${screenWidth}x${screenHeight} screen"
    Assert-True ($window.X -ge -$slack -and $window.Y -ge -$slack) "the window starts on screen ($($window.X),$($window.Y))"
    $state = $null
    if ((Get-MosaicSession).PipeUp) { $state = Get-MosaicState }
    $reported = Get-Field $state 'window'
    if ($reported) { Write-Step "dump.window: $($reported | ConvertTo-Json -Compress)" }
}

Register-Scenario -Area winui -Name 'dump has the contract keys' -Tags smoke -Todo 'Parity with the v0.1 WPF app' -Body {
    $state = Get-MosaicState
    Save-State $state 'contract'
    $missing = @($script:ContractKeys | Where-Object { $null -eq $state.PSObject.Properties[$_] })
    Assert-True ($missing.Count -eq 0) "dump has every CONTRACT.md key (missing: $($missing -join ', '))"
    Assert-True (@(Get-Field $state 'tabs').Count -ge 1) 'there is at least one tab'
}

Register-Scenario -Area winui -Name 'Mozilla engine loads a page; Gecko window attached' -Tags smoke -Todo 'Gecko embedded as a child window of the WinUI window' -Body {
    $state = Open-TestPage '/page?title=Mosaic%20Gecko%20Page' -Engine gecko -Title 'Mosaic Gecko Page'
    if ((Get-Field $state 'geckoAttached') -ne $true) {
        $state = Wait-State "Gecko's window to be attached (geckoAttached)" { param($s) (Get-Field $s 'geckoAttached') -eq $true }
    }
    $gecko = @(Get-GeckoWindows $state)
    Write-Step "Gecko process $(Get-Field $state 'geckoProcessId'); $($gecko.Count) Gecko window(s) inside Mosaic's"
    Assert-True ($gecko.Count -gt 0) "a window of the Gecko process is a child of Mosaic's window"
    Assert-True (@($gecko | Where-Object { $_.Visible }).Count -gt 0) 'the Gecko window is visible'
    $null = Save-Screenshot 'gecko-page'
    $null = Save-WindowTree 'gecko-page'
}

Register-Scenario -Area winui -Name 'Chromium engine: the tab switches and reloads' -Tags smoke -Todo 'Chromium tabs on WinUI''s WebView2 control, one shared environment' -Body {
    $state = Open-TestPage '/page?title=Mosaic%20Switch%20Page' -Engine gecko -Title 'Mosaic Switch Page'
    $before = @(Get-TestRequests -PathPrefix '/page').Count
    Invoke-Mosaic 'engine' 'chromium' | Out-Null
    $state = Wait-State 'the tab to load the page again on Chromium' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition {
        param($s) $t = Get-SelectedTab $s
        (Test-TabShows $t -Title 'Mosaic Switch Page' -Engine chromium) -and (Get-Field $t 'hasChromiumView') -eq $true
    }
    Assert-True (@(Get-TestRequests -PathPrefix '/page').Count -gt $before) 'the page was requested again by Chromium'
    $null = Save-Screenshot 'chromium-page'
    $null = Save-WindowTree 'chromium-page'
}

Register-Scenario -Area winui -Name 'toggleEngine moves the tab back to Mozilla' -Todo 'Parity with the v0.1 WPF app' -Body {
    $state = Open-TestPage '/page?title=Mosaic%20Toggle%20Page' -Engine chromium -Title 'Mosaic Toggle Page'
    Invoke-Mosaic 'toggleEngine' | Out-Null
    $null = Wait-State 'the tab to be on gecko with the same page' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition {
        param($s) Test-TabShows (Get-SelectedTab $s) -Title 'Mosaic Toggle Page' -Engine gecko
    }
}

Register-Scenario -Area winui -Name 'back, forward and reload' -Tags smoke -Todo 'Parity with the v0.1 WPF app' -Body {
    Invoke-OnEngines {
        param($engine)
        $null = Open-TestPage "/page?title=First%20$engine" -Engine $engine -Title "First $engine"
        $null = Open-TestPage "/page?title=Second%20$engine" -Engine $engine -Title "Second $engine"
        $null = Wait-State 'canGoBack' { param($s) (Get-Field (Get-SelectedTab $s) 'canGoBack') -eq $true }
        Invoke-Mosaic 'back' | Out-Null
        $null = Wait-State 'back to the first page' { param($s) Test-TabShows (Get-SelectedTab $s) -Title "First $engine" }
        $null = Wait-State 'canGoForward' { param($s) (Get-Field (Get-SelectedTab $s) 'canGoForward') -eq $true }
        Invoke-Mosaic 'forward' | Out-Null
        $null = Wait-State 'forward to the second page' { param($s) Test-TabShows (Get-SelectedTab $s) -Title "Second $engine" }
        $before = @(Get-TestRequests -PathPrefix '/page' | Where-Object { $_.Query -like "*Second%20$engine*" }).Count
        Invoke-Mosaic 'reload' | Out-Null
        $null = Wait-Until -What 'the reload to request the page again' -Condition {
            @(Get-TestRequests -PathPrefix '/page' | Where-Object { $_.Query -like "*Second%20$engine*" }).Count -gt $before
        }
    }
}

Register-Scenario -Area winui -Name 'new tab, select tab, close tab' -Tags smoke -Todo 'Parity with the v0.1 WPF app' -Body {
    Reset-Tabs
    $count = Get-TabCount (Get-MosaicState)
    Invoke-Mosaic 'newTab' (Get-TestUrl '/page?title=Tab%20Two') | Out-Null
    $state = Wait-State 'a second tab showing Tab Two' { param($s) (Get-TabCount $s) -eq $count + 1 -and (Test-TabShows (Get-SelectedTab $s) -Title 'Tab Two') }
    Invoke-Mosaic 'selectTab' '0' | Out-Null
    $null = Wait-State 'tab 0 selected' { param($s) (Get-SelectedIndex $s) -eq 0 }
    Invoke-Mosaic 'selectTab' '1' | Out-Null
    $null = Wait-State 'tab 1 selected' { param($s) (Get-SelectedIndex $s) -eq 1 }
    $refused = Invoke-Mosaic 'selectTab' '99' -AllowError
    Assert-True ((Get-Field $refused 'ok') -ne $true) 'selectTab 99 is refused'
    Invoke-Mosaic 'closeTab' | Out-Null
    $null = Wait-State 'back to one tab' { param($s) (Get-TabCount $s) -eq $count }
}

Register-Scenario -Area winui -Name 'search engine: a word goes to the chosen engine' -Todo 'Parity with the v0.1 WPF app' -Body {
    foreach ($engine in @('duckduckgo', 'bing', 'google')) {
        Invoke-Mosaic 'searchEngine' $engine | Out-Null
        $null = Wait-State "searchEngine=$engine" { param($s) (Get-Field $s 'searchEngine') -eq $engine }
    }
    $refused = Invoke-Mosaic 'searchEngine' 'altavista' -AllowError
    Assert-True ((Get-Field $refused 'ok') -ne $true) 'an unknown search engine is refused'
    Invoke-Mosaic 'searchEngine' 'duckduckgo' | Out-Null
    Invoke-Mosaic 'navigate' 'mosaice2esearch' | Out-Null
    $state = Wait-State 'a DuckDuckGo search for the word' { param($s)
        $url = [string](Get-Field (Get-SelectedTab $s) 'url')
        $url -like 'https://duckduckgo.com/*' -and $url -like '*mosaice2esearch*'
    }
}

Register-Scenario -Area winui -Name 'extension: install a local MV3 extension, open its pages, remove it' -Todo 'Parity with the v0.1 WPF app' -Body {
    $zip = New-ExtensionFixture -ZipPath (Join-Path (Get-E2EConfig).OutputDirectory 'fixtures\mosaic-e2e-fixture.zip')
    Invoke-Mosaic 'installFile' $zip | Out-Null
    $state = Wait-State 'the extension to be installed and loaded' -TimeoutSeconds 120 -Condition { param($s)
        $problem = [string](Get-Field $s 'extensionError')
        if ($problem) { throw "Installing the extension failed: $problem" }
        $e = @(Get-Field $s 'extensions') | Where-Object { (Get-Field $_ 'name') -eq 'Mosaic E2E Fixture' } | Select-Object -First 1
        $e -and (Get-Field $e 'loaded') -eq $true -and (Get-Field $s 'extensionsBusy') -ne $true
    }
    $extension = @(Get-Field $state 'extensions') | Where-Object { (Get-Field $_ 'name') -eq 'Mosaic E2E Fixture' } | Select-Object -First 1
    $id = [string](Get-Field $extension 'id')
    Write-Step "installed $id as $(Get-Field $extension 'geckoId')"
    Invoke-Mosaic 'showExtensions' 'true' | Out-Null
    Start-Sleep -Milliseconds 600
    $null = Save-Screenshot 'extensions'
    Invoke-Mosaic 'showExtensions' 'false' | Out-Null
    $tabs = Get-TabCount $state
    Invoke-Mosaic 'extensionPage' "${id}:options" | Out-Null
    $null = Wait-State 'the options page to open in a tab' { param($s) (Get-TabCount $s) -gt $tabs -and (Get-Field (Get-SelectedTab $s) 'title') -eq 'E2E Options' }
    Invoke-Mosaic 'closeTab' | Out-Null
    Invoke-Mosaic 'removeExtension' $id | Out-Null
    $null = Wait-State 'the extension to be removed' { param($s) @(@(Get-Field $s 'extensions') | Where-Object { (Get-Field $_ 'id') -eq $id }).Count -eq 0 }
}

Register-Scenario -Area winui -Name 'content blocker blocks in both engines; site exemption' -Tags smoke -Todo 'Parity with the v0.1 WPF app' -Body {
    $state = Wait-State 'the content blocker to be ready' -TimeoutSeconds 120 -Condition { param($s)
        $status = [string](Get-Field $s 'blocker.status')
        if ($status -eq 'unavailable') { throw 'The content blocker is not part of this build (blocker.status "unavailable").' }
        if ($status -eq 'failed') { throw "The content blocker could not start: $(Get-Field $s 'blocker.error')" }
        $status -eq 'ready'
    }
    Write-Step "$(Get-Field $state 'blocker.filters') filters, uBlock Origin $(Get-Field $state 'blocker.engineVersion')"
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        $state = Open-TestPage "/blocker.html?token=$token" -Engine $engine -Title 'Mosaic Blocker Page'
        $state = Wait-State 'blocked > 0' { param($s) [int](Get-Field (Get-SelectedTab $s) 'blocked') -gt 0 }
        Write-Step "$engine blocked $(Get-Field (Get-SelectedTab $state) 'blocked') request(s)"
        $null = Save-Screenshot "blocker-$engine"
        $served = @(Get-TestRequests -PathPrefix '/blocker.html').Count
        Invoke-Mosaic 'blockerSite' 'off' | Out-Null
        $null = Wait-State 'a reload with nothing blocked' { param($s)
            @(Get-TestRequests -PathPrefix '/blocker.html').Count -gt $served -and (Test-TabShows (Get-SelectedTab $s) -Title 'Mosaic Blocker Page') -and [int](Get-Field (Get-SelectedTab $s) 'blocked') -eq 0
        }
        $served = @(Get-TestRequests -PathPrefix '/blocker.html').Count
        Invoke-Mosaic 'blockerSite' 'on' | Out-Null
        $null = Wait-State 'a reload with the script blocked again' { param($s)
            @(Get-TestRequests -PathPrefix '/blocker.html').Count -gt $served -and (Test-TabShows (Get-SelectedTab $s) -Title 'Mosaic Blocker Page') -and [int](Get-Field (Get-SelectedTab $s) 'blocked') -gt 0
        }
    }
    Invoke-Mosaic 'blocker' 'off' | Out-Null
    $null = Wait-State 'blocker.enabled false' { param($s) (Get-Field $s 'blocker.enabled') -eq $false }
    Invoke-Mosaic 'blocker' 'on' | Out-Null
    $null = Wait-State 'blocker.enabled true' { param($s) (Get-Field $s 'blocker.enabled') -eq $true }
}

Register-Scenario -Area winui -Name 'geckoEval version reports the Gecko process and window' -Todo 'Gecko embedded as a child window of the WinUI window' -Body {
    $null = Wait-State 'Gecko running' -TimeoutSeconds 90 -Condition { param($s) (Get-Field $s 'geckoStatus') -eq 'running' }
    $out = New-OutputPath 'gecko-version.json'
    Invoke-Mosaic 'geckoEval' 'version' -Out $out | Out-Null
    $version = Wait-OutputFile $out -Json
    Write-Step "agent $(Get-Field $version 'agent'), Gecko $(Get-Field $version 'gecko'), pid $(Get-Field $version 'pid'), window $(Get-Field $version 'windowHandle')"
    Assert-True ([int](Get-Field $version 'pid') -gt 0) 'version() reports a pid'
    Assert-True ([long](Get-Field $version 'windowHandle') -ne 0) 'version() reports a window handle'
}

Register-Scenario -Area winui -Name 'screenshot command writes a real image of the window' -Tags smoke -Todo 'Parity with the v0.1 WPF app' -Body {
    $null = Open-TestPage '/long.html' -Engine chromium
    $path = Save-Screenshot 'screenshot-check' -NoFallback
    Assert-True ($null -ne $path) 'the screenshot command wrote a PNG'
    $stats = Get-ImageStats $path
    Assert-True ($null -ne $stats) 'the PNG can be read'
    Write-Step "$($stats.Width)x$($stats.Height), $($stats.DistinctColours) sampled colours"
    Assert-True ($stats.Width -ge 640 -and $stats.Height -ge 400) 'the image has the window''s size'
    Assert-True ($stats.DistinctColours -ge 4) 'the image is not blank (page bands and chrome visible)'
}

Register-Scenario -Area winui -Name 'Gecko is hidden while Mosaic draws over the page' -Todo 'Gecko embedded as a child window of the WinUI window; hidden whenever Mosaic draws over the page' -Body {
    $state = Open-TestPage '/page?title=Mosaic%20Cover%20Page' -Engine gecko -Title 'Mosaic Cover Page'
    $state = Wait-State 'Gecko attached' { param($s) (Get-Field $s 'geckoAttached') -eq $true }
    Assert-True (@(Get-GeckoWindows $state | Where-Object { $_.Visible }).Count -gt 0) 'Gecko is visible before settings open'
    Invoke-Mosaic 'settings' 'general' | Out-Null
    Start-Sleep -Milliseconds 800
    $state = Get-MosaicState
    $visible = @(Get-GeckoWindows $state | Where-Object { $_.Visible -and $_.Width -gt 50 })
    $null = Save-Screenshot 'settings-over-gecko'
    Assert-True ($visible.Count -eq 0) "no Gecko window is visible while Settings is shown (visible: $(($visible | ForEach-Object { $_.ToString() }) -join '; '))"
    Invoke-Mosaic 'closeSettings' | Out-Null
    $null = Wait-Until -What 'Gecko visible again after Settings closed' -Condition { @(Get-GeckoWindows (Get-MosaicState) | Where-Object { $_.Visible }).Count -gt 0 }
}

Register-Scenario -Area winui -Name 'Chromium tabs share one WebView2 environment' -Todo 'Chromium tabs on WinUI''s WebView2 control, one shared environment' -Body {
    $null = Open-TestPage '/page?title=Env%20One' -Engine chromium -Title 'Env One'
    $null = Open-TestPage '/page?title=Env%20Two' -Engine chromium -Title 'Env Two' -NewTab
    $session = Get-MosaicSession
    $browsers = @(Get-EngineProcesses -DataDirectory $session.DataDirectory | Where-Object { $_.CommandLine -notmatch '--type=' })
    if ($browsers.Count -eq 0) {
        # The user-data folder may not be on the command line: count browser processes under Mosaic.
        $tree = @(Get-ProcessSnapshot -Roots @($session.Process.Id))
        $browsers = @($tree | Where-Object { $_.Name -eq 'msedgewebview2.exe' } | ForEach-Object {
                Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($_.Id)" } | Where-Object { $_.CommandLine -notmatch '--type=' })
    }
    Write-Step "$($browsers.Count) WebView2 browser process(es)"
    Assert-True ($browsers.Count -eq 1) "two Chromium tabs run in one WebView2 browser process (found $($browsers.Count))"
    Invoke-Mosaic 'closeTab' | Out-Null
}

Register-Scenario -Area winui -Name 'single instance: a URL opened from outside goes to the running window' -Todo 'Single instance; opening a URL from outside goes to the running window' -Body {
    $session = Get-MosaicSession
    $url = Get-TestUrl '/page?title=Mosaic%20From%20Outside'
    $before = Get-TabCount (Get-MosaicState)
    $saved = $env:MOSAIC_DATA_DIR
    $env:MOSAIC_DATA_DIR = $session.DataDirectory
    try {
        $second = Start-Process -FilePath (Get-E2EConfig).AppPath -ArgumentList @($url) -PassThru
        $null = $second.Handle
    }
    finally { $env:MOSAIC_DATA_DIR = $saved }
    $exited = $second.WaitForExit(20000)
    if (-not $exited) { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue }
    Assert-True $exited 'the second Mosaic hands over and exits within 20 s'
    $null = Wait-State 'the running window to open the URL' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s)
        @(@(Get-Field $s 'tabs') | Where-Object { (Get-Field $_ 'title') -eq 'Mosaic From Outside' }).Count -gt 0
    }
}
