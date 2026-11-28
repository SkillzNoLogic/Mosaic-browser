# gallery: the screenshot gallery artifact (windows/To Do.md "qa"). Every Mosaic preset in light
# and dark, every settings category in light and dark, key overlays and Mosaic's own pages,
# through the app's "screenshot" command (or a capture from outside while it has none).
# Writes gallery\index.html and gallery\gallery.json. An item whose command this build does not
# have is listed as pending in the gallery, with the reason.

$script:GalleryItems = New-Object System.Collections.ArrayList

$script:GalleryPresets = @(
    @{ Id = 'zenArc'; Title = 'Zen / Arc'; Set = @() },
    @{ Id = 'chrome'; Title = 'Chrome'; Set = @() },
    @{ Id = 'opera'; Title = 'Opera'; Set = @('gx=off') },
    @{ Id = 'opera'; Title = 'Opera GX'; Set = @('gx=on'); Name = 'operaGX' },
    @{ Id = 'edge'; Title = 'Microsoft Edge'; Set = @() }
)
$script:GallerySettings = @('mosaics', 'general', 'accessibility', 'tabs', 'performance', 'search', 'privacy',
    'passwords', 'downloads', 'engine', 'extensions', 'languages', 'shortcuts', 'about')

function Add-GalleryItem {
    param([string]$Group, [string]$Name, [string]$Path, [string]$Status, [string]$Note = '')
    $relative = ''
    if ($Path) { $relative = $Path.Substring((Get-E2EConfig).OutputDirectory.Length + 1).Replace('\', '/') }
    [void]$script:GalleryItems.Add([pscustomobject]@{ Group = $Group; Name = $Name; Path = $relative; Status = $Status; Note = $Note })
}

function Save-GalleryShot {
    param([string]$Group, [string]$Name, [string]$Note = '')
    $directory = Join-Path (Get-E2EConfig).OutputDirectory ('gallery\' + (ConvertTo-SafeName $Group))
    $path = Save-Screenshot $Name -Directory $directory
    if ($path) { Add-GalleryItem $Group $Name $path 'captured' $Note } else { Add-GalleryItem $Group $Name '' 'failed' 'the screenshot could not be taken' }
    $path
}

function Invoke-GalleryItem {
    <#
    .SYNOPSIS
        Runs one gallery item's steps; a pending command is recorded in the gallery instead of
        ending the scenario. Returns 'captured', 'pending' or 'failed'.
    #>
    param([string]$Group, [string]$Name, [scriptblock]$Steps, [scriptblock]$Cleanup)
    $status = 'captured'
    try {
        $null = & $Steps
        if (-not (Save-GalleryShot $Group $Name)) { $status = 'failed' }
    }
    catch {
        if ($_.Exception -is [MosaicE2E.PendingException]) {
            Add-GalleryItem $Group $Name '' 'pending' $_.Exception.Message
            $status = 'pending'
        }
        elseif ($_.Exception -is [MosaicE2E.HangException] -or $_.Exception -is [MosaicE2E.CrashException]) { throw }
        else {
            Add-GalleryItem $Group $Name '' 'failed' $_.Exception.Message
            Write-Step "gallery item $Group/$Name failed: $($_.Exception.Message)"
            $status = 'failed'
        }
    }
    finally {
        if ($Cleanup) {
            try { $null = & $Cleanup } catch {
                if ($_.Exception -is [MosaicE2E.HangException] -or $_.Exception -is [MosaicE2E.CrashException]) { throw }
            }
        }
    }
    $status
}

function Complete-GalleryScenario {
    # Passed when something was captured and nothing failed; pending when nothing could be
    # captured because commands are missing; failed otherwise.
    param([string[]]$Statuses, [string]$What)
    $captured = @($Statuses | Where-Object { $_ -eq 'captured' }).Count
    $pending = @($Statuses | Where-Object { $_ -eq 'pending' }).Count
    $failed = @($Statuses | Where-Object { $_ -eq 'failed' }).Count
    Write-Step "$What`: $captured captured, $pending pending, $failed failed"
    if ($failed -gt 0) { throw "$failed of $($Statuses.Count) $What screenshots failed (see gallery\index.html)" }
    if ($captured -eq 0) { Skip-Pending "no $What could be captured: the commands are not in this build (see gallery\index.html)" }
}

function Set-Scheme {
    param([string]$Scheme)
    Invoke-Mosaic 'mosaicSet' "scheme=$Scheme" | Out-Null
    Start-Sleep -Milliseconds 500
}

Register-Scenario -Area gallery -Name 'current window (whatever this build shows)' -Tags smoke -NoPipe -Body {
    $session = Get-MosaicSession
    if ($session.PipeUp) {
        try { $null = Open-TestPage '/article.html' -Engine chromium -Title 'The Quiet Harbour' } catch {
            if ($_.Exception -is [MosaicE2E.HangException] -or $_.Exception -is [MosaicE2E.CrashException]) { throw }
            Write-Step "could not open the article: $($_.Exception.Message)"
        }
    }
    else { Start-Sleep -Seconds 3 }
    Assert-True ($null -ne (Save-GalleryShot 'Window' 'current')) 'the current window was captured'
}

Register-Scenario -Area gallery -Name 'every Mosaic preset, light and dark' -Body {
    $null = Open-TestPage '/article.html' -Engine chromium -Title 'The Quiet Harbour'
    $statuses = @()
    foreach ($preset in $script:GalleryPresets) {
        $presetName = $preset.Id
        if ($preset.ContainsKey('Name')) { $presetName = $preset.Name }
        foreach ($scheme in @('light', 'dark')) {
            $p = $preset
            $statuses += Invoke-GalleryItem -Group 'Mosaics' -Name "$presetName-$scheme" -Steps {
                Invoke-Mosaic 'mosaic' $p.Id | Out-Null
                foreach ($assignment in $p.Set) { Invoke-Mosaic 'mosaicSet' $assignment | Out-Null }
                Set-Scheme $scheme
                $state = Get-MosaicState
                $active = Get-Field $state 'mosaic'
                if ($null -ne $active -and $active -ne $p.Id) { throw "dump.mosaic is '$active' after 'mosaic $($p.Id)'" }
                Start-Sleep -Milliseconds 700
            }
        }
    }
    try { Invoke-Mosaic 'mosaicSet' 'gx=off' -Quiet | Out-Null; Set-Scheme 'light' } catch { }
    Complete-GalleryScenario $statuses 'Mosaic preset'
}

Register-Scenario -Area gallery -Name 'every settings category, light and dark' -Body {
    $statuses = @()
    foreach ($scheme in @('light', 'dark')) {
        $schemeStatus = 'captured'
        try { Set-Scheme $scheme } catch {
            if ($_.Exception -isnot [MosaicE2E.PendingException]) { throw }
            if ($scheme -eq 'dark') { Add-GalleryItem 'Settings' 'dark' '' 'pending' $_.Exception.Message; $statuses += 'pending'; continue }
        }
        foreach ($category in $script:GallerySettings) {
            $c = $category
            $statuses += Invoke-GalleryItem -Group 'Settings' -Name "$c-$scheme" -Steps {
                Invoke-Mosaic 'settings' $c | Out-Null
                $state = Get-MosaicState
                if ($null -ne (Get-Field $state 'settingsCategory')) {
                    $null = Wait-State "settingsCategory=$c" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'settingsCategory') -eq $c }
                }
                Start-Sleep -Milliseconds 700
            }
        }
    }
    try { Invoke-Mosaic 'closeSettings' -Quiet | Out-Null; Set-Scheme 'light' } catch { }
    Complete-GalleryScenario $statuses 'settings page'
}

Register-Scenario -Area gallery -Name 'key overlays and panels' -Body {
    $statuses = @()
    $null = Open-TestPage '/find.html' -Engine chromium -Title 'Mosaic Find Page'
    $statuses += Invoke-GalleryItem 'Overlays' 'command-palette' { Invoke-Mosaic 'palette' 'open' | Out-Null; Invoke-Mosaic 'paletteQuery' 'new' | Out-Null; Start-Sleep -Milliseconds 600 } { Invoke-Mosaic 'palette' 'close' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'command-palette-dark' { Set-Scheme 'dark'; Invoke-Mosaic 'palette' 'open' | Out-Null; Start-Sleep -Milliseconds 600 } { Invoke-Mosaic 'palette' 'close' -Quiet | Out-Null; Set-Scheme 'light' }
    $statuses += Invoke-GalleryItem 'Overlays' 'tab-search' { Invoke-Mosaic 'tabSearch' 'show' | Out-Null; Start-Sleep -Milliseconds 600 } { Invoke-Mosaic 'tabSearch' 'hide' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'find-bar' { Invoke-Mosaic 'page.find' 'needle' | Out-Null; Start-Sleep -Milliseconds 800 } { Invoke-Mosaic 'page.findClose' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'blocker-popover' { Invoke-Mosaic 'showBlocker' 'true' | Out-Null; Start-Sleep -Milliseconds 600 } { Invoke-Mosaic 'showBlocker' 'false' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'extensions' { Invoke-Mosaic 'showExtensions' 'true' | Out-Null; Start-Sleep -Milliseconds 600 } { Invoke-Mosaic 'showExtensions' 'false' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'downloads-popover' { Invoke-Mosaic 'downloadsPopover' 'true' | Out-Null; Start-Sleep -Milliseconds 600 } { Invoke-Mosaic 'downloadsPopover' 'false' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'permission-prompt' {
        $null = Open-TestPage '/permissions.html?ask=geolocation' -Engine chromium -Title 'Mosaic Permissions Page'
        $state = Get-MosaicState
        $null = Require-Field $state 'page.permissionPrompts' -Owner 'engine'
        $null = Wait-State 'a permission prompt' { param($s) @(Get-Field $s 'page.permissionPrompts').Count -gt 0 }
    } { Invoke-Mosaic 'page.permission' 'dismiss' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'popup-notice' {
        $null = Open-TestPage '/popup.html' -Engine chromium -Title 'Mosaic Popup Page'
        $null = Require-Field (Get-MosaicState) 'page.popupNotices' -Owner 'engine'
        $null = Wait-State 'a pop-up notice' { param($s) @(Get-Field $s 'page.popupNotices').Count -gt 0 }
    } { Invoke-Mosaic 'page.popup' 'dismiss' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'javascript-dialog' {
        $null = Open-TestPage '/dialogs.html?kind=confirm' -Engine chromium -Title 'Mosaic Dialogs Page'
        $null = Require-Field (Get-MosaicState) 'page.dialogs' -Owner 'engine'
        $null = Wait-State 'a JavaScript dialog' { param($s) @(Get-Field $s 'page.dialogs').Count -gt 0 }
    } { Invoke-Mosaic 'page.dialog' 'cancel' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'password-save-prompt' {
        $null = Require-Field (Get-MosaicState) 'passwords' -Owner 'passwords'
        $null = Open-TestPage "/login.html?autosubmit=gallery:Gallery-Secret-9" -Engine chromium -Title 'Mosaic Login Page'
        $null = Wait-State 'a password save prompt' { param($s) @(@(Get-Field $s 'passwords.tabs') | Where-Object { (Get-Field $_ 'prompt') -in @('save', 'update') }).Count -gt 0 }
    } { Invoke-Mosaic 'passwordsPrompt' 'notnow' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'reader-view' {
        $null = Open-TestPage '/article.html' -Engine chromium -Title 'The Quiet Harbour'
        Invoke-Mosaic 'reader' 'on' | Out-Null
        Start-Sleep -Milliseconds 1200
    } { Invoke-Mosaic 'reader' 'off' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'clear-browsing-data' { Invoke-Mosaic 'privacy.settings' 'clear' | Out-Null; Start-Sleep -Milliseconds 800 } { Invoke-Mosaic 'closeSettings' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'layout-canvas' { Invoke-Mosaic 'canvasMode' 'layout' | Out-Null; Start-Sleep -Milliseconds 800 } { Invoke-Mosaic 'closeSettings' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'gradient-studio' { Invoke-Mosaic 'canvasMode' 'gradient' | Out-Null; Start-Sleep -Milliseconds 800 } { Invoke-Mosaic 'closeSettings' -Quiet | Out-Null }
    $statuses += Invoke-GalleryItem 'Overlays' 'sidebar' { Invoke-Mosaic 'mosaic' 'zenArc' | Out-Null; Invoke-Mosaic 'sidebar' 'show' | Out-Null; Start-Sleep -Milliseconds 600 } $null
    Complete-GalleryScenario $statuses 'overlay'
}

Register-Scenario -Area gallery -Name 'Mosaic''s own pages' -Body {
    $statuses = @()
    $statuses += Invoke-GalleryItem 'Pages' 'new-tab-speed-dial' { Invoke-Mosaic 'newTab' '' | Out-Null; Start-Sleep -Milliseconds 1200 } { Invoke-Mosaic 'closeTab' -Quiet | Out-Null }
    foreach ($page in @('bookmarks', 'history', 'reading-list')) {
        $p = $page
        $statuses += Invoke-GalleryItem 'Pages' $p { Invoke-Mosaic 'libraryOpen' $p | Out-Null; Start-Sleep -Milliseconds 1200 } $null
    }
    $statuses += Invoke-GalleryItem 'Pages' 'downloads' { Invoke-Mosaic 'downloadsPage' | Out-Null; Start-Sleep -Milliseconds 1200 } $null
    Complete-GalleryScenario $statuses 'internal page'
}

Register-Scenario -Area gallery -Name '(index) gallery page' -Tags smoke -NoPipe -Body {
    $out = (Get-E2EConfig).OutputDirectory
    $items = @($script:GalleryItems)
    $html = New-Object System.Text.StringBuilder
    [void]$html.AppendLine('<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Mosaic for Windows: screenshot gallery</title>')
    [void]$html.AppendLine('<style>body{font-family:Segoe UI,system-ui,sans-serif;margin:16px;background:#f6f6f6;color:#1b1b1b}h2{margin-top:32px}')
    [void]$html.AppendLine('.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(320px,1fr));gap:16px}.card{background:#fff;border-radius:8px;padding:8px;box-shadow:0 1px 3px rgba(0,0,0,.15)}')
    [void]$html.AppendLine('.card img{width:100%;border-radius:4px;border:1px solid #ddd}.pending{color:#8a6100}.failed{color:#b3261e}.name{font-weight:600}</style></head><body>')
    [void]$html.AppendLine("<h1>Mosaic for Windows: screenshot gallery</h1><p>$([System.Security.SecurityElement]::Escape([string]$env:GITHUB_SHA)) $(Get-Date -Format 'yyyy-MM-dd HH:mm')</p>")
    $groups = @()
    foreach ($item in $items) { if ($groups -notcontains $item.Group) { $groups += $item.Group } }
    foreach ($group in $groups) {
        [void]$html.AppendLine("<h2>$([System.Security.SecurityElement]::Escape($group))</h2><div class=`"grid`">")
        foreach ($item in @($items | Where-Object { $_.Group -eq $group })) {
            $name = [System.Security.SecurityElement]::Escape($item.Name)
            $note = [System.Security.SecurityElement]::Escape($item.Note)
            if ($item.Status -eq 'captured') {
                $rel = $item.Path.Substring('gallery/'.Length)
                [void]$html.AppendLine("<div class=`"card`"><a href=`"$rel`"><img src=`"$rel`" alt=`"$name`" loading=`"lazy`"></a><div class=`"name`">$name</div><div>$note</div></div>")
            }
            else {
                [void]$html.AppendLine("<div class=`"card`"><div class=`"name`">$name</div><div class=`"$($item.Status)`">$($item.Status): $note</div></div>")
            }
        }
        [void]$html.AppendLine('</div>')
    }
    [void]$html.AppendLine('</body></html>')
    Write-TextFile (Join-Path $out 'gallery\index.html') $html.ToString()
    Write-TextFile (Join-Path $out 'gallery\gallery.json') (ConvertTo-Json -InputObject @($items) -Depth 4)
    Add-Evidence (Join-Path $out 'gallery\index.html')
    $captured = @($items | Where-Object { $_.Status -eq 'captured' }).Count
    Write-Step "$captured of $($items.Count) gallery items captured"
    Assert-True ($captured -gt 0) 'the gallery has at least one screenshot'
}
