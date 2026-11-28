# Scenarios that test the e2e framework itself against FakeMosaic.ps1 (Run-E2E.ps1 -SelfTest):
# pipe and dump, page loads through the test server, pending detection, screenshots, hang
# detection (with a hang dump), crash detection (with a Windows Error Reporting dump).

Register-Scenario -Area selftest -Name 'pipe answers dump with the contract keys' -Body {
    $state = Get-MosaicState
    foreach ($key in @('tabs', 'engine', 'searchEngine', 'chromiumAvailable', 'geckoStatus', 'blocker')) {
        Assert-True ($null -ne $state.PSObject.Properties[$key]) "dump has '$key'"
    }
}

Register-Scenario -Area selftest -Name 'test server pages load (title through the server)' -Body {
    $token = New-Token
    $null = Open-TestPage "/page?title=Self%20Test%20$token" -Engine chromium -Title "Self Test $token"
    Assert-True (@(Get-TestRequests -PathPrefix '/page' | Where-Object { $_.Query -like "*$token*" }).Count -ge 1) 'the server saw the request'
    $null = Open-TestPage '/blocker.html' -Engine gecko -Title 'Mosaic Blocker Page' -NewTab
    $null = Open-TestPage '/article.html' -Title 'The Quiet Harbour'
}

Register-Scenario -Area selftest -Name 'back, forward, tabs and pin through the shared helpers' -Body {
    Reset-Tabs
    $null = Open-TestPage '/page?title=One' -Title 'One'
    $null = Open-TestPage '/page?title=Two' -Title 'Two'
    Invoke-Mosaic 'back' | Out-Null
    $null = Wait-State 'back to One' { param($s) Test-TabShows (Get-SelectedTab $s) -Title 'One' }
    Invoke-Mosaic 'pin' | Out-Null
    $null = Wait-State 'pinned' { param($s) (Get-Field (Get-SelectedTab $s) 'pinned') -eq $true }
    $refused = Invoke-Mosaic 'selectTab' '42' -AllowError
    Assert-True ((Get-Field $refused 'ok') -eq $false) 'a refused command is returned with -AllowError'
}

Register-Scenario -Area selftest -Name 'pending: an unknown command raises PendingException' -Body {
    $pending = $false
    try { Invoke-Mosaic 'page.find' 'needle' | Out-Null } catch { $pending = $_.Exception -is [MosaicE2E.PendingException] }
    Assert-True $pending 'Invoke-Mosaic turns "Unknown command" into a pending scenario'
    $missing = $false
    try { $null = Require-Field (Get-MosaicState) 'library' -Owner 'library' } catch { $missing = $_.Exception -is [MosaicE2E.PendingException] }
    Assert-True $missing 'Require-Field turns a missing dump key into a pending scenario'
}

Register-Scenario -Area selftest -Name 'pending: this scenario is reported as skipped' -Body {
    Invoke-Mosaic 'page.find' 'needle' | Out-Null
    throw 'not reached: the command above is unknown to FakeMosaic'
}

Register-Scenario -Area selftest -Name 'screenshot through the app and from outside' -Body {
    $path = Save-Screenshot 'selftest-app' -NoFallback
    Assert-True ($null -ne $path) 'the app wrote a screenshot'
    $stats = Get-ImageStats $path
    Assert-True ($stats.DistinctColours -ge 2) 'the screenshot is not blank'
    $window = Get-MosaicMainWindow
    $outside = Join-Path (Get-MosaicSession).LogDirectory 'outside.png'
    $problem = [MosaicE2E.Win32]::CaptureWindow($window.Handle, $outside)
    Assert-True ($null -eq $problem) "capture from outside works ($problem)"
    $tree = Save-WindowTree 'selftest'
    Assert-True ($null -ne $tree) 'the window tree was written'
}

Register-Scenario -Area selftest -Name 'accessibility scan finds the unnamed button' -Body {
    $window = Get-MosaicMainWindow
    $scan = Get-UiaScan -WindowHandle $window.Handle
    $report = Write-UiaReport -Scan $scan -BasePath (Join-Path (Get-MosaicSession).LogDirectory 'a11y-fake') -Title 'FakeMosaic'
    Add-Evidence $report
    Write-Step "$($scan.Elements.Count) elements, $($scan.Unnamed) unnamed, $($scan.NotFocusable) not focusable"
    Assert-True ($scan.Elements.Count -ge 4) 'the scan walks the window'
    Assert-True (@($scan.Issues | Where-Object { $_.Kind -eq 'unnamed' -and $_.Type -eq 'Button' }).Count -eq 1) 'exactly the one unnamed button is reported'
    if ($env:CI) {
        $stops = Invoke-TabWalk -WindowHandle $window.Handle -Presses 12
        Write-Step "Tab walk: $(($stops | ForEach-Object { $_.Type + ':' + $_.Name }) -join ', ')"
        Assert-True (@($stops).Count -ge 2) 'the Tab walk moves the focus'
    }
}

Register-Scenario -Area selftest -Name 'hang: a frozen window is detected and dumped' -Body {
    Invoke-Mosaic 'hang' | Out-Null
    Start-Sleep -Seconds 6   # IsHungAppWindow needs about 5 s without messages
    $hang = $null
    try { $null = Get-MosaicState } catch { $hang = $_.Exception }
    Assert-True ($hang -is [MosaicE2E.HangException]) "a HangException (got: $(if ($hang) { $hang.GetType().Name + ': ' + $hang.Message } else { 'no error' }))"
    $dumps = @(Get-ChildItem -LiteralPath (Join-Path (Get-E2EConfig).OutputDirectory 'dumps') -Filter 'hang-*.dmp' -ErrorAction SilentlyContinue)
    Assert-True ($dumps.Count -ge 1) 'a hang dump was written'
    # The runner restarts the app before the next scenario (the session is marked hung).
}

Register-Scenario -Area selftest -Name 'after a hang the next scenario gets a fresh app' -Body {
    $state = Get-MosaicState
    Assert-Equal 1 (@(Get-Field $state 'tabs').Count) 'tabs in the restarted app'
}

Register-Scenario -Area selftest-crash -Name 'crash: the exit and the WER dump are detected' -Body {
    $session = Get-MosaicSession
    Invoke-Mosaic 'crash' | Out-Null
    if (-not $session.Process.WaitForExit(20000)) { throw 'FakeMosaic did not crash' }
    $crash = $null
    try { $null = Get-MosaicState } catch { $crash = $_.Exception }
    Assert-True ($crash -is [MosaicE2E.CrashException]) "a CrashException (got: $(if ($crash) { $crash.GetType().Name } else { 'no error' }))"
    $session.ExpectExit = $true
    if ((Get-E2EConfig).DumpDirectory) {
        $dumps = @()
        $null = Wait-Until -What 'a WER dump of the crash' -TimeoutSeconds 60 -Condition { $script:SelfTestDumps = @(Get-NewDumps); $script:SelfTestDumps.Count -gt 0 }
        Write-Step "dump: $(($script:SelfTestDumps | ForEach-Object { $_.Name }) -join ', ')"
    }
    else { Write-Step 'no dump folder configured (run Enable-CrashDumps.ps1); dump check skipped' }
}

Register-Scenario -Area selftest-crash -Name '(meta) earlier results were classified correctly' -NoPipe -Body {
    $results = @(Get-Results)
    $pending = @($results | Where-Object { $_.Name -eq 'pending: this scenario is reported as skipped' })
    Assert-True ($pending.Count -eq 1 -and $pending[0].Status -eq 'skipped' -and $pending[0].Kind -eq 'pending') 'the unknown-command scenario is skipped as pending'
    $teardown = @($results | Where-Object { $_.Area -eq 'selftest' -and $_.Name -like '(teardown)*' })
    Assert-True ($teardown.Count -eq 1 -and $teardown[0].Status -eq 'passed') "quit left nothing behind ($($teardown[0].Message))"
}
