# platform: engines, extensions, updates (windows/To Do.md "platform"). Commands and dump of
# Sources/Mosaic/Features/Platform/PlatformAutomation.swift; dump key "platform".

function Get-Platform {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'platform' -Owner 'platform'
}

Register-Scenario -Area platform -Name 'engine settings: runtime status and versions' -Tags smoke -Todo 'Engine settings: Chromium (WebView2) and Gecko cards, versions, status' -Body {
    $state = Get-MosaicState
    $platform = Get-Platform $state
    Write-Step "runtime: $((Get-Field $platform 'runtime') | ConvertTo-Json -Compress)"
    Assert-True ([string](Get-Field $platform 'runtime.inUseVersion') -ne '') 'the Gecko version in use is reported'
    Invoke-Mosaic 'openSettings' 'engine' | Out-Null
    $null = Wait-State 'Settings > Engine open' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'platform.settingsCategory') -eq 'engine' -or (Get-Field $s 'settingsCategory') -eq 'engine' }
    Start-Sleep -Milliseconds 800
    $null = Save-Screenshot 'settings-engine'
    Invoke-Mosaic 'openSettings' '' | Out-Null
}

Register-Scenario -Area platform -Name 'Gecko runtime: check for the current ESR' -Todo 'In-app Gecko runtime install/update/remove with Authenticode verification' -Body {
    Invoke-Mosaic 'runtimeCheck' | Out-Null
    $null = Get-Platform (Get-MosaicState)
    $state = Wait-State 'the latest ESR version known' -TimeoutSeconds 60 -Condition { param($s) [string](Get-Field $s 'platform.runtime.latest') -ne '' -or [string](Get-Field $s 'platform.runtime.message') -ne '' }
    Write-Step "latest $(Get-Field $state 'platform.runtime.latest'); message '$(Get-Field $state 'platform.runtime.message')'; managed path $(Get-Field $state 'platform.runtime.managedPath')"
    Assert-True ([string](Get-Field $state 'platform.runtime.latest') -ne '') 'Mozilla''s current ESR version was found (needs Internet)'
}

Register-Scenario -Area platform -Name 'per-site engine rule opens the site in Chromium' -Tags smoke -Todo 'Per-site engine rules; default engine; safe engine switching (keep the page until the other engine has loaded it)' -Body {
    Invoke-Mosaic 'defaultEngine' 'gecko' | Out-Null
    Invoke-Mosaic 'ruleAdd' '127.0.0.1=chromium,exact' | Out-Null
    $null = Get-Platform (Get-MosaicState)
    $null = Wait-State 'the rule saved' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'platform.rules' | Where-Object { (Get-Field $_ 'domain') -eq '127.0.0.1' -and (Get-Field $_ 'engine') -eq 'chromium' }).Count -eq 1 }
    Invoke-Mosaic 'newTab' (Get-TestUrl '/page?title=Rule%20Page') | Out-Null
    $null = Wait-State 'the page opened in Chromium by the rule' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title 'Rule Page' -Engine chromium }
    Invoke-Mosaic 'ruleRemove' '127.0.0.1' | Out-Null
    $null = Wait-State 'the rule removed' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'platform.rules').Count -eq 0 }
    Invoke-Mosaic 'closeTab' | Out-Null
}

Register-Scenario -Area platform -Name 'default engine for new tabs' -Todo 'Per-site engine rules; default engine; safe engine switching (keep the page until the other engine has loaded it)' -Body {
    foreach ($engine in @('chromium', 'gecko')) {
        $e = $engine
        Invoke-Mosaic 'defaultEngine' $e | Out-Null
        $null = Get-Platform (Get-MosaicState)
        $null = Wait-State "defaultEngine=$e" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'platform.defaultEngine') -eq $e }
        Invoke-Mosaic 'newTab' (Get-TestUrl "/page?title=Default%20$e") | Out-Null
        $null = Wait-State "a new tab on $e" -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title "Default $e" -Engine $e }
        Invoke-Mosaic 'closeTab' | Out-Null
    }
}

Register-Scenario -Area platform -Name 'safe engine switching keeps the page until the other engine loads' -Todo 'Per-site engine rules; default engine; safe engine switching (keep the page until the other engine has loaded it)' -Body {
    $null = Open-TestPage '/page?title=Safe%20Switch' -Engine gecko -Title 'Safe Switch'
    $null = Get-Platform (Get-MosaicState)
    Invoke-Mosaic 'navigate' (Get-TestUrl '/slow?ms=4000&title=Slow%20Target') | Out-Null
    Start-Sleep -Milliseconds 300
    Invoke-Mosaic 'engine' 'chromium' -AllowError | Out-Null
    $state = Get-MosaicState
    Write-Step "while switching: switching=$((Get-Field $state 'platform.switching') | ConvertTo-Json -Compress), tab=$(Format-State $state)"
    $state = Wait-State 'the slow page on Chromium' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title 'Slow Target' -Engine chromium }
    Write-Step "lastSwitch: $(Get-Field $state 'platform.lastSwitch')"
}

Register-Scenario -Area platform -Name 'languages for web content reach both engines (Accept-Language)' -Todo 'Languages for web content in both engines' -Body {
    Invoke-Mosaic 'languages' 'fr-FR,de' | Out-Null
    $null = Get-Platform (Get-MosaicState)
    $null = Wait-State 'languages set' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'platform.languages')[0] -eq 'fr-FR' }
    Invoke-OnEngines {
        param($engine)
        $since = Get-Date
        $null = Set-Engine $engine
        Invoke-Mosaic 'navigate' (Get-TestUrl "/echo?lang=$engine") | Out-Null
        $null = Wait-State 'the echo page' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title 'Echo' }
        $request = @(Get-TestRequests -PathPrefix '/echo' -Since $since | Where-Object { $_.Query -eq "lang=$engine" })[-1]
        $accept = ''
        if ($request.Headers.ContainsKey('Accept-Language')) { $accept = $request.Headers['Accept-Language'] }
        Write-Step "Accept-Language: $accept"
        Assert-True ($accept -like 'fr-FR*') "the page was asked for in French first ($accept)"
    }
    Invoke-Mosaic 'languages' 'system' | Out-Null
}

Register-Scenario -Area platform -Name 'Extensions settings page' -Todo 'Extensions settings page' -Body {
    Invoke-Mosaic 'openSettings' 'extensions' | Out-Null
    Start-Sleep -Milliseconds 800
    $null = Save-Screenshot 'settings-extensions'
    Invoke-Mosaic 'openSettings' '' | Out-Null
}

Register-Scenario -Area platform -Name 'About & Updates: version, licence, update check' -Todo 'About & Updates: version, licences (GPL v3 or later), automatic updates with Mosaic''s Ed25519 release signature (same key and rules as macOS)' -Body {
    $app = Split-Path -Parent (Get-E2EConfig).AppPath
    Assert-True (Test-Path -LiteralPath (Join-Path $app 'LICENSE')) 'the GPL licence ships next to Mosaic.exe'
    Invoke-Mosaic 'updateFrequency' 'manual' | Out-Null
    Invoke-Mosaic 'updateCheck' | Out-Null
    $null = Get-Platform (Get-MosaicState)
    $state = Wait-State 'an update check outcome' -TimeoutSeconds 60 -Condition { param($s) [string](Get-Field $s 'platform.updates.outcome') -ne '' -and (Get-Field $s 'platform.updates.checking') -ne $true }
    Write-Step "outcome: $(Get-Field $state 'platform.updates.outcome'); updater: $((Get-Field $state 'platform.updater') | ConvertTo-Json -Compress)"
    Invoke-Mosaic 'openSettings' 'about' | Out-Null
    Start-Sleep -Milliseconds 800
    $null = Save-Screenshot 'settings-about'
    Invoke-Mosaic 'openSettings' '' | Out-Null
}
