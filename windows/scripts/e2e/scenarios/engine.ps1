# engine: page features (windows/To Do.md "engine"). The page.* commands of
# Sources/Mosaic/Features/Engine/PageFeatures+Commands.swift; dump key "page" (findStatus,
# permissionPrompts, popupNotices, dialogs, siteZoom, sitePermissions, tabs[]: zoom, muted...).

function Get-PageState {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'page' -Owner 'engine'
}

function Get-PageTab {
    # The page.tabs entry of the selected tab.
    param([Parameter(Mandatory = $true)] $State)
    Get-Field $State ("page.tabs.{0}" -f (Get-SelectedIndex $State))
}

Register-Scenario -Area engine -Name 'find in page: matches, next, case, close' -Tags smoke -Todo 'Find in page; per-site zoom; print and save as PDF; developer tools (both engines)' -Body {
    Invoke-OnEngines {
        param($engine)
        $null = Open-TestPage '/find.html' -Engine $engine -Title 'Mosaic Find Page'
        Invoke-Mosaic 'page.findCase' 'false' | Out-Null
        Invoke-Mosaic 'page.find' 'needle' | Out-Null
        $null = Get-PageState (Get-MosaicState)
        $state = Wait-State 'four matches (case ignored)' -TimeoutSeconds 15 -Condition { param($s) (Get-Field $s 'page.findBarVisible') -eq $true -and [int](Get-Field $s 'page.findStatus.total') -eq 4 }
        $null = Save-Screenshot "find-$engine"
        Invoke-Mosaic 'page.findNext' | Out-Null
        $null = Wait-State 'second match current' -TimeoutSeconds 10 -Condition { param($s) [int](Get-Field $s 'page.findStatus.current') -eq 2 }
        Invoke-Mosaic 'page.findPrevious' | Out-Null
        $null = Wait-State 'first match current' -TimeoutSeconds 10 -Condition { param($s) [int](Get-Field $s 'page.findStatus.current') -eq 1 }
        Invoke-Mosaic 'page.findCase' 'true' | Out-Null
        $null = Wait-State 'three matches (match case)' -TimeoutSeconds 10 -Condition { param($s) [int](Get-Field $s 'page.findStatus.total') -eq 3 }
        Invoke-Mosaic 'page.findClose' | Out-Null
        $null = Wait-State 'find bar closed' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'page.findBarVisible') -eq $false }
    }
}

Register-Scenario -Area engine -Name 'per-site zoom is remembered' -Todo 'Find in page; per-site zoom; print and save as PDF; developer tools (both engines)' -Body {
    Invoke-OnEngines {
        param($engine)
        $null = Open-TestPage "/page?title=Zoom%20$engine" -Engine $engine -Title "Zoom $engine"
        Invoke-Mosaic 'page.zoomReset' | Out-Null
        Invoke-Mosaic 'page.zoomIn' | Out-Null
        Invoke-Mosaic 'page.zoomIn' | Out-Null
        $null = Get-PageState (Get-MosaicState)
        $state = Wait-State 'zoom above 100 %' -TimeoutSeconds 10 -Condition { param($s) [double](Get-Field (Get-PageTab $s) 'zoom') -gt 1.05 }
        $zoom = [double](Get-Field (Get-PageTab $state) 'zoom')
        Write-Step "zoom $zoom; siteZoom: $(@(Get-Field $state 'page.siteZoom') -join '; ')"
        Assert-True (@(Get-Field $state 'page.siteZoom' | Where-Object { $_ -like '127.0.0.1*' }).Count -ge 1) 'the zoom is stored for the site'
        $null = Open-TestPage "/page?title=Zoom%20Again%20$engine" -Engine $engine -Title "Zoom Again $engine" -NewTab
        $null = Wait-State 'the same zoom on another page of the site' -TimeoutSeconds 10 -Condition { param($s) [math]::Abs([double](Get-Field (Get-PageTab $s) 'zoom') - $zoom) -lt 0.01 }
        Invoke-Mosaic 'page.zoomReset' | Out-Null
        Invoke-Mosaic 'closeTab' | Out-Null
        Invoke-Mosaic 'page.zoomReset' | Out-Null
    }
}

Register-Scenario -Area engine -Name 'save as PDF' -Todo 'Find in page; per-site zoom; print and save as PDF; developer tools (both engines)' -Body {
    Invoke-OnEngines {
        param($engine)
        $null = Open-TestPage '/article.html' -Engine $engine -Title 'The Quiet Harbour'
        $pdf = New-OutputPath "page-$engine.pdf"
        $out = New-OutputPath "page-$engine-pdf.json"
        Invoke-Mosaic 'page.pdf' $pdf -Out $out | Out-Null
        $result = Wait-OutputFile $out -Json -TimeoutSeconds 60
        Assert-True ((Get-Field $result 'ok') -eq $true) 'page.pdf reports success'
        $bytes = [System.IO.File]::ReadAllBytes($pdf)
        Assert-True ($bytes.Length -gt 1000 -and [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4) -eq '%PDF') "a PDF was written ($($bytes.Length) bytes)"
        Add-Evidence $pdf
    }
}

Register-Scenario -Area engine -Name 'page screenshots: visible area and full page' -Todo 'Screenshots (visible and full page); mute; Picture in Picture; page full screen' -Body {
    Invoke-OnEngines {
        param($engine)
        $null = Open-TestPage '/long.html' -Engine $engine -Title 'Mosaic Long Page'
        $visible = New-OutputPath "page-visible-$engine.png"
        $out = New-OutputPath "page-visible-$engine.json"
        Invoke-Mosaic 'page.screenshot' $visible -Out $out | Out-Null
        $result = Wait-OutputFile $out -Json -TimeoutSeconds 30
        $visibleStats = Get-ImageStats $visible
        Assert-True ($null -ne $visibleStats) 'the visible-area screenshot is a PNG'
        $full = New-OutputPath "page-full-$engine.png"
        $out = New-OutputPath "page-full-$engine.json"
        Invoke-Mosaic 'page.screenshot' $full -Out $out -Extra @{ full = 'true' } | Out-Null
        $result = Wait-OutputFile $out -Json -TimeoutSeconds 30
        $fullStats = Get-ImageStats $full
        Write-Step "visible $($visibleStats.Width)x$($visibleStats.Height), full $($fullStats.Width)x$($fullStats.Height)"
        Add-Evidence $visible; Add-Evidence $full
        # The page is 8 x 500 CSS px tall. (The Windows pipe only forwards do/value/out to a
        # handler; if "full" is ignored the two images have the same height.)
        Assert-True ($fullStats.Height -gt $visibleStats.Height * 1.5) 'the full-page screenshot is taller than the visible area'
    }
}

Register-Scenario -Area engine -Name 'mute a page' -Todo 'Screenshots (visible and full page); mute; Picture in Picture; page full screen' -Body {
    Invoke-OnEngines {
        param($engine)
        $null = Open-TestPage '/media.html?autoplay=1' -Engine $engine -Title 'Mosaic Audio Page'
        Invoke-Mosaic 'page.mute' 'true' | Out-Null
        $null = Get-PageState (Get-MosaicState)
        $null = Wait-State 'page muted' -TimeoutSeconds 10 -Condition { param($s) (Get-Field (Get-PageTab $s) 'muted') -eq $true }
        Invoke-Mosaic 'page.mute' 'false' | Out-Null
        $null = Wait-State 'page unmuted' -TimeoutSeconds 10 -Condition { param($s) (Get-Field (Get-PageTab $s) 'muted') -eq $false }
    }
}

Register-Scenario -Area engine -Name 'Picture in Picture and page full screen' -Todo 'Screenshots (visible and full page); mute; Picture in Picture; page full screen' -Body {
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        $null = Open-TestPage "/video.html?token=$token" -Engine $engine -Title 'Mosaic Video Page'
        $null = Wait-Report -Token $token -Key 'video' -Value 'playing'
        Invoke-Mosaic 'page.pip' | Out-Null
        $null = Wait-Report -Token $token -Key 'pip' -Value 'entered' -What 'the video to enter Picture in Picture'
        $null = Save-Screenshot "pip-$engine"
        Invoke-Mosaic 'page.pip' | Out-Null
        Invoke-Mosaic 'page.click' '320,200' | Out-Null
        $null = Wait-Report -Token $token -Key 'fullscreen' -Value 'on' -What 'the video to go full screen after a click'
        $null = Save-Screenshot "fullscreen-$engine"
        Invoke-Mosaic 'page.fullscreenExit' | Out-Null
        $null = Wait-Report -Token $token -Key 'fullscreen' -Value 'off' -What 'full screen to end'
    }
}

Register-Scenario -Area engine -Name 'Mosaic''s own permission prompt: block, remembered per site' -Tags smoke -Todo 'Mosaic''s own permission prompts (camera, microphone, location, notifications, clipboard), remembered per site' -Body {
    Invoke-OnEngines {
        param($engine)
        $site = (Get-TestUrl '/').TrimEnd('/')
        $token = New-Token
        $null = Open-TestPage "/permissions.html?ask=geolocation&token=$token" -Engine $engine -Title 'Mosaic Permissions Page'
        $null = Get-PageState (Get-MosaicState)
        $state = Wait-State 'Mosaic''s permission prompt for the page' -Condition { param($s) @(Get-Field $s 'page.permissionPrompts').Count -gt 0 }
        Write-Step "prompt: $((Get-Field $state 'page.permissionPrompts.0') | ConvertTo-Json -Compress)"
        $null = Save-Screenshot "permission-$engine"
        Invoke-Mosaic 'page.permission' 'block' | Out-Null
        $null = Wait-Report -Token $token -Key 'permission' -What 'the page to be told the permission was denied'
        $value = @(Get-TestReports -Token $token -Key 'permission')[-1].Value
        Assert-True ($value -like 'denied*') "the page was denied ($value)"
        $state = Wait-State 'the decision remembered' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'page.sitePermissions' | Where-Object { $_ -like '*block*' }).Count -gt 0 }
        $token2 = New-Token
        $null = Open-TestPage "/permissions.html?ask=geolocation&token=$token2" -Engine $engine -Title 'Mosaic Permissions Page'
        $value = Wait-Report -Token $token2 -Key 'permission' -What 'the remembered decision to answer without a prompt'
        Assert-True ($value -like 'denied*') "denied again without asking ($value)"
        Assert-True (@(Get-Field (Get-MosaicState) 'page.permissionPrompts').Count -eq 0) 'no prompt the second time'
        foreach ($entry in @(Get-Field (Get-MosaicState) 'page.sitePermissions')) {
            $parts = ([string]$entry).Split(' ')
            if ($parts.Count -ge 2) { Invoke-Mosaic 'page.setPermission' "$($parts[0])|$($parts[1])|forget" | Out-Null }
        }
    }
}

Register-Scenario -Area engine -Name 'permission prompts for notifications, camera, microphone, clipboard' -Todo 'Mosaic''s own permission prompts (camera, microphone, location, notifications, clipboard), remembered per site' -Body {
    Invoke-OnEngines {
        param($engine)
        foreach ($kind in @('notifications', 'camera', 'microphone', 'clipboard')) {
            $k = $kind
            $token = New-Token
            $null = Open-TestPage "/permissions.html?ask=$k&auto=0&token=$token" -Engine $engine -Title 'Mosaic Permissions Page'
            Invoke-Mosaic 'page.click' '200,150' | Out-Null   # the Ask button: a real gesture
            $null = Get-PageState (Get-MosaicState)
            $state = Wait-State "a prompt for $k" -Condition { param($s) @(Get-Field $s 'page.permissionPrompts').Count -gt 0 }
            Write-Step "$k prompt: $(@(Get-Field $state 'page.permissionPrompts.0.kinds') -join ',')"
            Invoke-Mosaic 'page.permission' 'dismiss' | Out-Null
        }
    }
}

Register-Scenario -Area engine -Name 'pop-up blocking with a notice' -Todo 'Pop-up blocking with a notice; JavaScript dialogs; context menus; file pickers' -Body {
    Invoke-OnEngines {
        param($engine)
        Reset-Tabs -Engine $engine
        $token = New-Token
        $null = Open-TestPage "/popup.html?token=$token" -Engine $engine -Title 'Mosaic Popup Page'
        $null = Get-PageState (Get-MosaicState)
        $state = Wait-State 'a pop-up notice' -Condition { param($s) @(Get-Field $s 'page.popupNotices').Count -gt 0 }
        $count = [int](Get-Field $state 'page.popupNotices.0.count')
        Write-Step "notice for $count pop-up(s)"
        Assert-True ($count -ge 1) 'the notice counts the blocked pop-ups'
        Assert-Equal 1 (Get-TabCount $state) 'tabs (nothing opened without a gesture)'
        $null = Save-Screenshot "popup-notice-$engine"
        Invoke-Mosaic 'page.popup' 'open' | Out-Null
        $null = Wait-State 'the blocked pop-ups opened on request' -Condition { param($s) (Get-TabCount $s) -gt 1 }
        Reset-Tabs -Engine $engine
        $token = New-Token
        $null = Open-TestPage "/popup.html?token=$token" -Engine $engine -Title 'Mosaic Popup Page'
        Invoke-Mosaic 'page.click' '150,60' | Out-Null
        $null = Wait-Report -Token $token -Key 'opened-click' -Value 'yes' -What 'a pop-up opened from a click to be allowed'
        Reset-Tabs -Engine $engine
    }
}

Register-Scenario -Area engine -Name 'JavaScript dialogs: alert, confirm, prompt' -Todo 'Pop-up blocking with a notice; JavaScript dialogs; context menus; file pickers' -Body {
    Invoke-OnEngines {
        param($engine)
        $cases = @(
            @{ Kind = 'alert'; Answer = 'accept'; Expect = 'alert-closed' },
            @{ Kind = 'confirm'; Answer = 'accept'; Expect = 'confirmed' },
            @{ Kind = 'confirm'; Answer = 'cancel'; Expect = 'cancelled' },
            @{ Kind = 'prompt'; Answer = 'accept'; Expect = 'answer:mosaic'; Text = 'mosaic' }
        )
        foreach ($case in $cases) {
            $token = New-Token
            $c = $case
            $null = Open-TestPage "/dialogs.html?kind=$($c.Kind)&token=$token" -Engine $engine -Title 'Mosaic Dialogs Page'
            $null = Get-PageState (Get-MosaicState)
            $state = Wait-State "a $($c.Kind) dialog" -Condition { param($s) @(Get-Field $s 'page.dialogs').Count -gt 0 }
            Assert-Equal $c.Kind (Get-Field $state 'page.dialogs.0.kind') 'dialog kind'
            $extra = @{}
            if ($c.ContainsKey('Text')) { $extra['text'] = $c.Text }
            Invoke-Mosaic 'page.dialog' $c.Answer -Extra $extra | Out-Null
            $null = Wait-Report -Token $token -Key 'dialog' -Value $c.Expect -What "the page to see '$($c.Expect)'"
        }
    }
}

Register-Scenario -Area engine -Name 'developer tools open' -Todo 'Find in page; per-site zoom; print and save as PDF; developer tools (both engines)' -Body {
    $null = Open-TestPage '/page?title=DevTools%20Page' -Engine chromium -Title 'DevTools Page'
    Invoke-Mosaic 'page.devtools' | Out-Null
    $out = New-OutputPath 'devtools.json'
    $null = Wait-Until -What 'developer tools open' -Condition {
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
        Invoke-Mosaic 'page.devtoolsOpen' -Out $out -Quiet | Out-Null
        (Get-Field (Wait-OutputFile $out -Json -TimeoutSeconds 10) 'open') -eq $true
    }
    $null = Save-Screenshot 'devtools'
    Invoke-Mosaic 'page.devtools' | Out-Null
}
