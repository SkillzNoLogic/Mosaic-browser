# privacy (windows/To Do.md "privacy"). The privacy.* commands of
# Sources/Mosaic/Features/Privacy/BrowserModel+Privacy.swift; dump key "privacy". Page-side
# effects (cosmetic hiding, cookies) are reported by the test pages to the test server.

function Get-Privacy {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'privacy' -Owner 'privacy'
}

Register-Scenario -Area privacy -Name 'private tab: in memory only, nothing on disk' -Tags smoke -Todo 'Private tabs (in-memory profile; nothing on disk)' -Body {
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        Reset-Tabs -Engine $engine
        Invoke-Mosaic 'privacy.newPrivateTab' (Get-TestUrl "/setcookie?name=e2eprivate&value=$token&title=Private%20$token") | Out-Null
        $state = Wait-State 'the private tab loaded' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'title') -eq "Private $token" }
        if ((Get-Field (Get-SelectedTab $state) 'engine') -ne $engine) {
            Invoke-Mosaic 'engine' $engine | Out-Null
            $state = Wait-State "the private tab on $engine" -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title "Private $token" -Engine $engine }
        }
        $null = Get-Privacy $state
        $index = Get-SelectedIndex $state
        Assert-True ((Get-Field $state "privacy.tabs.$index.isPrivate") -eq $true) 'the tab is private'
        $null = Save-Screenshot "private-$engine"
        Invoke-Mosaic 'closeTab' | Out-Null
        Start-Sleep -Seconds 2
        $hits = @(Find-TextInFolder -Folder (Get-MosaicSession).DataDirectory -Text $token)
        Assert-True ($hits.Count -eq 0) "the private page left nothing on disk (found in: $($hits -join ', '))"
    }
}

Register-Scenario -Area privacy -Name 'cosmetic filtering: default lists and My filters, both engines' -Tags smoke -Todo 'Blocker: cosmetic filtering, My filters with validation, optional lists, per-page dashboard' -Body {
    Invoke-Mosaic 'privacy.set' 'cosmetic=on' | Out-Null
    Invoke-Mosaic 'privacy.filters' '127.0.0.1##.e2e-target' | Out-Null
    $null = Get-Privacy (Get-MosaicState)
    $null = Wait-State 'My filters saved' -Condition { param($s) [int](Get-Field $s 'privacy.blocker.userFilterLines') -ge 1 }
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        $null = Open-TestPage "/cosmetic.html?token=$token" -Engine $engine -Title 'Mosaic Cosmetic Page'
        $generic = Wait-Report -Token $token -Key 'generic'
        $specific = Wait-Report -Token $token -Key 'specific'
        $late = Wait-Report -Token $token -Key 'late'
        $null = Save-Screenshot "cosmetic-$engine"
        Assert-Equal 'hidden' $generic 'the generic cosmetic target (.adsbygoogle, default lists)'
        Assert-Equal 'hidden' $specific 'the My filters target (127.0.0.1##.e2e-target)'
        Assert-Equal 'hidden' $late 'an element inserted after load'
    }
    Invoke-Mosaic 'privacy.set' 'cosmetic=off' | Out-Null
    $token = New-Token
    $null = Open-TestPage "/cosmetic.html?token=$token" -Engine chromium -Title 'Mosaic Cosmetic Page'
    Assert-Equal 'visible' (Wait-Report -Token $token -Key 'specific') 'the target with cosmetic filtering off'
    Invoke-Mosaic 'privacy.set' 'cosmetic=on' | Out-Null
}

Register-Scenario -Area privacy -Name 'My filters validation reports bad lines' -Todo 'Blocker: cosmetic filtering, My filters with validation, optional lists, per-page dashboard' -Body {
    $out = New-OutputPath 'filters-validate.json'
    Invoke-Mosaic 'privacy.validate' '||good.example^\n127.0.0.1##.ok\n##[[[broken\n||bad^$unknownoption=1' -Out $out | Out-Null
    $checks = @(Wait-OutputFile $out -Json)
    Write-Step ($checks | ConvertTo-Json -Compress -Depth 4)
    Assert-True (@($checks | Where-Object { (Get-Field $_ 'kind') -match 'error|invalid' }).Count -ge 1) 'a broken filter is reported'
}

Register-Scenario -Area privacy -Name 'optional filter lists and the per-page dashboard' -Todo 'Blocker: cosmetic filtering, My filters with validation, optional lists, per-page dashboard' -Body {
    $token = New-Token
    $null = Open-TestPage "/blocker.html?token=$token" -Engine chromium -Title 'Mosaic Blocker Page'
    $out = New-OutputPath 'blocked-requests.json'
    Invoke-Mosaic 'privacy.blocked' $out | Out-Null
    $blocked = @(Wait-OutputFile $out -Json)
    Assert-True (@($blocked | Where-Object { (Get-Field $_ 'url') -like '*googlesyndication*' }).Count -ge 1) 'the dashboard lists the blocked ad script with its filter'
    Invoke-Mosaic 'showBlocker' 'true' | Out-Null
    Start-Sleep -Milliseconds 700
    $null = Save-Screenshot 'blocker-dashboard'
    Invoke-Mosaic 'showBlocker' 'false' | Out-Null
    $lists = @(Get-Field (Get-Privacy (Get-MosaicState)) 'blocker.lists')
    Write-Step "enabled lists: $($lists -join ', ')"
}

Register-Scenario -Area privacy -Name 'tracking parameters are stripped' -Todo 'HTTPS-only and the security indicator; tracking-parameter stripping; DNS-over-HTTPS' -Body {
    Invoke-Mosaic 'privacy.set' 'strip=on' | Out-Null
    $null = Get-Privacy (Get-MosaicState)
    Invoke-OnEngines {
        param($engine)
        $marker = (New-Token)
        $since = Get-Date
        $null = Set-Engine $engine
        Invoke-Mosaic 'navigate' (Get-TestUrl "/echo?id=$marker&utm_source=e2e&utm_medium=test&fbclid=abc123&gclid=xyz") | Out-Null
        $null = Wait-State 'the echo page' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) Test-TabShows (Get-SelectedTab $s) -Title 'Echo' }
        $request = @(Get-TestRequests -PathPrefix '/echo' -Since $since | Where-Object { $_.Query -like "*$marker*" })[-1]
        Write-Step "server saw: $($request.Target)"
        Assert-True ($request.Target -notmatch 'utm_|fbclid|gclid') 'tracking parameters were removed before the request'
        Assert-True ($request.Target -match "id=$marker") 'ordinary parameters were kept'
    }
}

Register-Scenario -Area privacy -Name 'HTTPS-only setting and the security indicator' -Todo 'HTTPS-only and the security indicator; tracking-parameter stripping; DNS-over-HTTPS' -Body {
    $null = Open-TestPage '/page?title=Plain%20HTTP' -Engine chromium -Title 'Plain HTTP'
    $state = Get-MosaicState
    $null = Get-Privacy $state
    $security = [string](Get-Field $state ("privacy.tabs.{0}.security" -f (Get-SelectedIndex $state)))
    Write-Step "security of an http:// page: $security"
    Assert-True ($security -ne '' -and $security -ne 'secure') 'an http:// page is not shown as secure'
    Invoke-Mosaic 'privacy.set' 'httpsOnly=on' | Out-Null
    $null = Wait-State 'httpsOnly on' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'privacy.settings.httpsOnly') -eq $true }
    Invoke-Mosaic 'privacy.set' 'httpsOnly=off' | Out-Null
}

Register-Scenario -Area privacy -Name 'DNS-over-HTTPS setting' -Todo 'HTTPS-only and the security indicator; tracking-parameter stripping; DNS-over-HTTPS' -Body {
    Invoke-Mosaic 'privacy.set' 'doh=automatic:quad9' | Out-Null
    $null = Get-Privacy (Get-MosaicState)
    $null = Wait-State 'dohMode automatic' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'privacy.settings.dohMode') -eq 'automatic' }
    $out = New-OutputPath 'privacy-status.json'
    Invoke-Mosaic 'privacy.status' $out | Out-Null
    $status = Wait-OutputFile $out -Json
    Write-Step "template: $(Get-Field $status 'settings.dohTemplate'); gecko: $((Get-Field $status 'gecko') | ConvertTo-Json -Compress -Depth 3)"
    Invoke-Mosaic 'privacy.set' 'doh=off' | Out-Null
}

Register-Scenario -Area privacy -Name 'cookies and site data; third-party cookies blocked' -Todo 'Cookies and site data; clear browsing data with time ranges; third-party cookies; site permissions page' -Body {
    Invoke-Mosaic 'privacy.set' 'thirdPartyCookies=on' | Out-Null
    $null = Get-Privacy (Get-MosaicState)
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        $null = Open-TestPage "/cookies.html?token=$token" -Engine $engine -Title 'Mosaic Cookies Page'
        Assert-Equal 'set' (Wait-Report -Token $token -Key 'firstCookie') 'the first-party cookie'
        $third = Wait-Report -Token $token -Key 'thirdCookie'
        Assert-Equal 'blocked' $third 'the third-party cookie with third-party cookies blocked'
    }
    $out = New-OutputPath 'site-data.json'
    Invoke-Mosaic 'privacy.siteData' $out | Out-Null
    $sites = @(Wait-OutputFile $out -Json -TimeoutSeconds 30)
    Write-Step "sites: $(($sites | ForEach-Object { Get-Field $_ 'site' }) -join ', ')"
    Assert-True (@($sites | Where-Object { (Get-Field $_ 'site') -like '*127.0.0.1*' }).Count -ge 1) 'the test site is listed with its data'
    $done = New-OutputPath 'clear-site.json'
    Invoke-Mosaic 'privacy.clearSite' '127.0.0.1' -Out $done | Out-Null
    $null = Wait-OutputFile $done -Json -TimeoutSeconds 30
}

Register-Scenario -Area privacy -Name 'clear browsing data by time range' -Todo 'Cookies and site data; clear browsing data with time ranges; third-party cookies; site permissions page' -Body {
    $null = Open-TestPage '/page?title=Clear%20Me' -Engine gecko -Title 'Clear Me'
    $null = Open-TestPage '/page?title=Clear%20Me%20Too' -Engine chromium -Title 'Clear Me Too'
    $out = New-OutputPath 'clear.json'
    Invoke-Mosaic 'privacy.clear' 'history,cookiesAndSiteData,cache:lastHour' -Out $out | Out-Null
    $result = Wait-OutputFile $out -Json -TimeoutSeconds 60
    Write-Step "summary: $(Get-Field $result 'summary')"
    $state = Get-MosaicState
    if ($null -ne (Get-Field $state 'library')) { Assert-Equal 0 (Get-Field $state 'library.historyVisits') 'history visits after clearing the last hour' }
    Invoke-Mosaic 'privacy.settings' 'clear' | Out-Null
    Start-Sleep -Milliseconds 800
    $null = Save-Screenshot 'clear-browsing-data'
    Invoke-Mosaic 'closeSettings' -AllowError | Out-Null
}

Register-Scenario -Area privacy -Name 'Privacy & Security settings page' -Todo 'Privacy & Security settings' -Body {
    Invoke-Mosaic 'privacy.settings' | Out-Null
    $null = Wait-State 'Settings > Privacy open' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'settingsCategory') -eq 'privacy' -or (Get-Field $s 'platform.settingsCategory') -eq 'privacy' }
    $null = Save-Screenshot 'settings-privacy'
    Invoke-Mosaic 'closeSettings' -AllowError | Out-Null
}
