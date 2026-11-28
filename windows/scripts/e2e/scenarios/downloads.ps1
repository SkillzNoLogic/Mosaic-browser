# downloads (windows/To Do.md "downloads"). The "download..." commands of
# Sources/Mosaic/Features/Downloads/DownloadManager+Automation.swift; dump key "downloads"
# (items[]: fileName, state, receivedBytes, totalBytes, path, fileExists, warning...). The test
# server throttles /download/<name>?size=&rate= so pause and resume have time to act. Mark of
# the Web is read from the file's Zone.Identifier stream directly.

function Get-Downloads {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'downloads' -Owner 'downloads'
}

function Get-DownloadItem {
    param([Parameter(Mandatory = $true)] $State, [Parameter(Mandatory = $true)] [string]$FileName)
    @(Get-Field $State 'downloads.items') | Where-Object { (Get-Field $_ 'fileName') -like "$FileName*" } | Select-Object -First 1
}

function Initialize-DownloadFolder {
    $folder = Join-Path (Get-MosaicSession).LogDirectory 'downloaded'
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    Invoke-Mosaic 'downloadsFolder' $folder | Out-Null
    Invoke-Mosaic 'downloadsSetting' 'askWhereToSave=off' | Out-Null
    Invoke-Mosaic 'downloadsSetting' 'openSafeFiles=off' | Out-Null
    $null = Get-Downloads (Get-MosaicState)
    $null = Wait-State 'the download folder set' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'downloads.folder') -eq $folder }
    $folder
}

Register-Scenario -Area downloads -Name 'download in each engine: completes, file on disk, Mark of the Web' -Tags smoke -Todo 'Download manager for both engines: progress, pause/resume/cancel/retry, history, search' -Body {
    $folder = Initialize-DownloadFolder
    foreach ($engine in @('chromium', 'gecko')) {
        $name = "mosaic-$engine-$((New-Token).Substring(3)).bin"
        $url = Get-TestUrl "/download/${name}?size=200000"
        if ($engine -eq 'chromium') { Invoke-Mosaic 'download' $url | Out-Null } else { Invoke-Mosaic 'downloadGecko' $url | Out-Null }
        $state = Wait-State "$name to complete" -TimeoutSeconds 60 -Condition { param($s)
            $item = Get-DownloadItem $s $name
            if ($item -and (Get-Field $item 'state') -eq 'failed') { throw "the download failed: $(Get-Field $item 'error')" }
            $item -and (Get-Field $item 'state') -eq 'completed' -and (Get-Field $item 'fileExists') -eq $true
        }
        $item = Get-DownloadItem $state $name
        $path = [string](Get-Field $item 'path')
        Write-Step "$engine -> $path ($(Get-Field $item 'receivedBytes') bytes)"
        Assert-Equal 200000 (Get-Item -LiteralPath $path).Length 'file size'
        Assert-Equal $engine (Get-Field $item 'engine') 'engine recorded for the download'
        $zone = Get-ZoneIdentifier $path
        Write-Step "Zone.Identifier: $($zone -replace "`n", ' | ')"
        Assert-True ($zone -match 'ZoneId=3') 'the file has the Mark of the Web (ZoneId=3)'
        Assert-True ($zone -match [regex]::Escape('127.0.0.1')) 'the Mark of the Web names where it came from (HostUrl)'
    }
}

Register-Scenario -Area downloads -Name 'pause, resume and cancel a throttled download' -Todo 'Download manager for both engines: progress, pause/resume/cancel/retry, history, search' -Body {
    $null = Initialize-DownloadFolder
    $name = "mosaic-slow-$((New-Token).Substring(3)).bin"
    Invoke-Mosaic 'download' (Get-TestUrl "/download/${name}?size=6000000&rate=300000") | Out-Null
    $state = Wait-State 'the download in progress with some bytes' -Condition { param($s)
        $item = Get-DownloadItem $s $name; $item -and [long](Get-Field $item 'receivedBytes') -gt 100000 }
    Write-Step "overall progress $(Get-Field $state 'downloads.overallProgress'), active $(Get-Field $state 'downloads.activeCount')"
    Invoke-Mosaic 'downloadPause' $name | Out-Null
    $state = Wait-State 'paused' -TimeoutSeconds 15 -Condition { param($s) (Get-Field (Get-DownloadItem $s $name) 'state') -eq 'paused' }
    $at = [long](Get-Field (Get-DownloadItem $state $name) 'receivedBytes')
    Start-Sleep -Seconds 3
    $later = [long](Get-Field (Get-DownloadItem (Get-MosaicState) $name) 'receivedBytes')
    Assert-True ($later -le $at + 300000) "no progress while paused ($at -> $later)"
    $null = Save-Screenshot 'download-paused'
    Invoke-Mosaic 'downloadResume' $name | Out-Null
    $null = Wait-State 'resumed and receiving' -TimeoutSeconds 20 -Condition { param($s)
        $item = Get-DownloadItem $s $name; (Get-Field $item 'state') -eq 'downloading' -and [long](Get-Field $item 'receivedBytes') -gt $later }
    $ranged = @(Get-TestRequests -PathPrefix "/download/$name" | Where-Object { $_.Headers.ContainsKey('Range') })
    Write-Step "$($ranged.Count) ranged request(s) on resume"
    Invoke-Mosaic 'downloadCancel' $name | Out-Null
    $null = Wait-State 'cancelled' -TimeoutSeconds 15 -Condition { param($s) (Get-Field (Get-DownloadItem $s $name) 'state') -in @('cancelled', 'canceled') }
    Invoke-Mosaic 'downloadRetry' $name | Out-Null
    $null = Wait-State 'retried' -TimeoutSeconds 15 -Condition { param($s) (Get-Field (Get-DownloadItem $s $name) 'state') -eq 'downloading' }
    Invoke-Mosaic 'downloadCancel' $name | Out-Null
}

Register-Scenario -Area downloads -Name 'dangerous and misleading files are flagged' -Todo 'Mark of the Web (Zone.Identifier) on every file; dangerous and misleading file warnings' -Body {
    $null = Initialize-DownloadFolder
    foreach ($file in @('mosaic-setup.exe', 'invoice.pdf.exe')) {
        $f = $file
        Invoke-Mosaic 'download' (Get-TestUrl "/download/${f}?size=4096&type=application/octet-stream") | Out-Null
        $state = Wait-State "$f flagged" -Condition { param($s)
            $item = Get-DownloadItem $s $f; $item -and [string](Get-Field $item 'warning') -ne '' }
        Write-Step "$f warning: $(Get-Field (Get-DownloadItem $state $f) 'warning')"
    }
    $null = Save-Screenshot 'download-warnings'
}

Register-Scenario -Area downloads -Name 'download history, search, clear' -Todo 'Download manager for both engines: progress, pause/resume/cancel/retry, history, search' -Body {
    $null = Initialize-DownloadFolder
    foreach ($n in @('alpha-report.txt', 'beta-photo.bin')) {
        $name = $n
        Invoke-Mosaic 'download' (Get-TestUrl "/download/${name}?size=2048") | Out-Null
        $null = Wait-State "$name done" -Condition { param($s) (Get-Field (Get-DownloadItem $s $name) 'state') -eq 'completed' }
    }
    Invoke-Mosaic 'downloadsSearch' 'alpha' | Out-Null
    $null = Wait-State 'one match for alpha' -TimeoutSeconds 10 -Condition { param($s) [int](Get-Field $s 'downloads.filteredCount') -eq 1 }
    Invoke-Mosaic 'downloadsSearch' '' | Out-Null
    $file = [string](Get-Field (Get-MosaicState) 'downloads.historyFile')
    Assert-True ($file -and (Test-Path -LiteralPath $file)) "the download history is saved ($file)"
    Invoke-Mosaic 'downloadsPage' | Out-Null
    Start-Sleep -Milliseconds 1000
    $null = Save-Screenshot 'downloads-page'
    Invoke-Mosaic 'downloadsClear' | Out-Null
    $null = Wait-State 'finished entries cleared' -TimeoutSeconds 10 -Condition { param($s) @(@(Get-Field $s 'downloads.items') | Where-Object { (Get-Field $_ 'state') -eq 'completed' }).Count -eq 0 }
}

Register-Scenario -Area downloads -Name 'ask where to save' -Todo 'Download folder, ask where to save, organisation rules' -Body {
    $folder = Initialize-DownloadFolder
    Invoke-Mosaic 'downloadsSetting' 'askWhereToSave=on' | Out-Null
    Invoke-Mosaic 'download' (Get-TestUrl '/download/mosaic-ask.bin?size=1024') | Out-Null
    $state = Wait-State 'a save prompt' -Condition { param($s) $null -ne (Get-Field $s 'downloads.pendingPrompt') }
    Write-Step "prompt: $((Get-Field $state 'downloads.pendingPrompt') | ConvertTo-Json -Compress)"
    $null = Save-Screenshot 'download-ask'
    $target = Join-Path $folder 'chosen-name.bin'
    Invoke-Mosaic 'downloadPromptSave' $target | Out-Null
    $null = Wait-Until -What 'the file saved under the chosen name' -Condition { Test-Path -LiteralPath $target }
    Invoke-Mosaic 'downloadsSetting' 'askWhereToSave=off' | Out-Null
}

Register-Scenario -Area downloads -Name 'Free Download Manager hand-off setting' -Todo 'Free Download Manager hand-off on Windows (off / ask / always)' -Body {
    foreach ($mode in @('ask', 'always', 'off')) {
        $m = $mode
        Invoke-Mosaic 'downloadsSetting' "fdmMode=$m" | Out-Null
        $null = Get-Downloads (Get-MosaicState)
        $null = Wait-State "fdmMode=$m" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'downloads.fdmMode') -eq $m }
    }
    Write-Step "FDM installed: $(Get-Field (Get-MosaicState) 'downloads.fdmInstalled') (not on CI runners)"
}

Register-Scenario -Area downloads -Name 'notification and taskbar progress state' -Todo 'Toast notification and taskbar progress' -Body {
    $null = Initialize-DownloadFolder
    Invoke-Mosaic 'downloadsSetting' 'notify=on' | Out-Null
    $name = "mosaic-notify-$((New-Token).Substring(3)).bin"
    Invoke-Mosaic 'download' (Get-TestUrl "/download/${name}?size=1500000&rate=500000") | Out-Null
    $null = Wait-State 'overall progress reported while downloading' -Condition { param($s) [double](Get-Field $s 'downloads.overallProgress') -gt 0 }
    $state = Wait-State 'completed and notified' -Condition { param($s) (Get-Field (Get-DownloadItem $s $name) 'state') -eq 'completed' -and [string](Get-Field $s 'downloads.lastNotification') -ne '' }
    Write-Step "notification: $(Get-Field $state 'downloads.lastNotification') ($(Get-Field $state 'downloads.notificationAuthorization'))"
}

Register-Scenario -Area downloads -Name 'Downloads settings page and popover' -Todo 'Downloads settings' -Body {
    Invoke-Mosaic 'settings' 'downloads' | Out-Null
    Start-Sleep -Milliseconds 800
    $null = Save-Screenshot 'settings-downloads'
    Invoke-Mosaic 'closeSettings' | Out-Null
    Invoke-Mosaic 'downloadsPopover' 'true' | Out-Null
    $null = Wait-State 'popover shown' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'downloads.popoverShown') -eq $true }
    $null = Save-Screenshot 'downloads-popover'
    Invoke-Mosaic 'downloadsPopover' 'false' | Out-Null
}
