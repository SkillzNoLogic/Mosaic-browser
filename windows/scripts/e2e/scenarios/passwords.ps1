# passwords (windows/To Do.md "passwords"). The passwords* commands of
# Sources/Mosaic/Features/Passwords/PasswordsAutomation.swift; dump key "passwords" (never a
# password). passwordsAdd/Delete/Import/Export only work on an isolated profile, which every
# e2e run is (MOSAIC_DATA_DIR). Secrets used here are made up for the test.

function Get-Passwords {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'passwords' -Owner 'passwords'
}

function Get-PasswordTab {
    param([Parameter(Mandatory = $true)] $State)
    $index = Get-SelectedIndex $State
    @(Get-Field $State 'passwords.tabs') | Where-Object { [int](Get-Field $_ 'index') -eq $index } | Select-Object -First 1
}


Register-Scenario -Area passwords -Name 'save prompt after a login, in both engines; verify the saved password' -Tags smoke -Todo 'Save prompt and autofill in both engines; never in private tabs; cross-origin frames refused' -Body {
    $null = Get-Passwords (Get-MosaicState)
    $origin = (Get-TestUrl '/').TrimEnd('/')
    Invoke-OnEngines {
        param($engine)
        $user = "user-$engine"
        $secret = "E2e-Secret-$engine-7x!"
        $null = Open-TestPage "/login.html?autosubmit=${user}:$secret" -Engine $engine -Title 'Mosaic Login Page'
        $state = Wait-State 'a save prompt for the login' -Condition { param($s) (Get-Field (Get-PasswordTab $s) 'prompt') -in @('save', 'update') }
        Assert-Equal $user (Get-Field (Get-PasswordTab $state) 'promptUsername') 'username in the save prompt'
        $null = Save-Screenshot "password-prompt-$engine"
        Invoke-Mosaic 'passwordsPrompt' 'save' | Out-Null
        $null = Wait-State 'the credential saved' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'passwords.credentials' | Where-Object { (Get-Field $_ 'username') -eq $user }).Count -eq 1 }
        $out = New-OutputPath "verify-$engine.txt"
        Invoke-Mosaic 'passwordsVerify' "$origin|$user|$secret" -Out $out | Out-Null
        Assert-Equal 'match' (Wait-OutputFile $out).Trim() 'the stored password'
    }
}

Register-Scenario -Area passwords -Name 'autofill on the login page in both engines' -Todo 'Save prompt and autofill in both engines; never in private tabs; cross-origin frames refused' -Body {
    $null = Get-Passwords (Get-MosaicState)
    $origin = (Get-TestUrl '/').TrimEnd('/')
    Invoke-Mosaic 'passwordsAdd' "$origin|filler|E2e-Fill-Secret-3" | Out-Null
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        $null = Open-TestPage "/login.html?expect=E2e-Fill-Secret-3&token=$token" -Engine $engine -Title 'Mosaic Login Page'
        $null = Wait-State 'the login form detected with one fillable credential' -Condition { param($s)
            $t = Get-PasswordTab $s; (Get-Field $t 'hasLogin') -eq $true -and [int](Get-Field $t 'fillable') -ge 1 }
        Invoke-Mosaic 'passwordsFill' 'filler' | Out-Null
        Assert-Equal 'filler' (Wait-Report -Token $token -Key 'filledUser') 'the filled username'
        Assert-Equal 'yes' (Wait-Report -Token $token -Key 'filledMatches') 'the filled password matches'
    }
}

Register-Scenario -Area passwords -Name 'never offered in a private tab' -Todo 'Save prompt and autofill in both engines; never in private tabs; cross-origin frames refused' -Body {
    $null = Get-Passwords (Get-MosaicState)
    Invoke-Mosaic 'privacy.newPrivateTab' (Get-TestUrl '/login.html?autosubmit=privateuser:E2e-Private-Secret-1') | Out-Null
    $null = Wait-State 'the private login submitted' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'title') -eq 'Mosaic Logged In' }
    Start-Sleep -Seconds 2
    $state = Get-MosaicState
    Assert-Equal '' (Get-Field (Get-PasswordTab $state) 'prompt') 'save prompt in a private tab'
    Assert-True (@(Get-Field $state 'passwords.credentials' | Where-Object { (Get-Field $_ 'username') -eq 'privateuser' }).Count -eq 0) 'nothing saved from the private tab'
    Invoke-Mosaic 'closeTab' | Out-Null
}

Register-Scenario -Area passwords -Name 'cross-origin frames are not filled' -Todo 'Save prompt and autofill in both engines; never in private tabs; cross-origin frames refused' -Body {
    $null = Get-Passwords (Get-MosaicState)
    $origin = (Get-TestUrl '/').TrimEnd('/')
    Invoke-Mosaic 'passwordsAdd' "$origin|framed|E2e-Frame-Secret-2" | Out-Null
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        $null = Open-TestPage "/frame-login.html?token=$token" -Engine $engine -Title 'Mosaic Frame Login Page'
        Start-Sleep -Seconds 2
        Invoke-Mosaic 'passwordsFill' 'framed' -AllowError | Out-Null
        Start-Sleep -Seconds 3
        $filled = @(Get-TestReports -Token $token -Key 'filledUser')
        Assert-True ($filled.Count -eq 0) "the cross-site frame was not filled with this site's password (reports: $($filled.Count))"
    }
}

Register-Scenario -Area passwords -Name 'strong password generator on a sign-up form' -Todo 'Generator; CSV import/export; Passwords & Autofill settings' -Body {
    $null = Get-Passwords (Get-MosaicState)
    Invoke-Mosaic 'passwordsOption' 'suggestStrongPasswords=on' | Out-Null
    Invoke-OnEngines {
        param($engine)
        $token = New-Token
        $null = Open-TestPage "/signup.html?token=$token" -Engine $engine -Title 'Mosaic Signup Page'
        Invoke-Mosaic 'passwordsSuggest' | Out-Null
        $state = Wait-State 'a generated-password offer' -Condition { param($s) (Get-Field (Get-PasswordTab $s) 'prompt') -eq 'generate' }
        $length = [int](Get-Field (Get-PasswordTab $state) 'promptPasswordLength')
        Write-Step "generated length $length"
        Assert-True ($length -ge 15) 'the suggestion is long enough'
        Invoke-Mosaic 'passwordsPrompt' 'use' | Out-Null
        Assert-Equal 'yes' (Wait-Report -Token $token -Key 'confirmMatches') 'both password fields filled with the same suggestion'
    }
}

Register-Scenario -Area passwords -Name 'CSV import and export (Chrome format)' -Todo 'Generator; CSV import/export; Passwords & Autofill settings' -Body {
    $null = Get-Passwords (Get-MosaicState)
    $out = New-OutputPath 'import-summary.txt'
    Invoke-Mosaic 'passwordsImport' (Get-FixturePath 'passwords.csv') -Out $out | Out-Null
    $summary = (Wait-OutputFile $out).Trim()
    Write-Step "import: $summary"
    Assert-True ($summary -match 'added 2') 'two passwords added'
    $export = New-OutputPath 'export.csv'
    Invoke-Mosaic 'passwordsExport' $export | Out-Null
    $csv = Wait-OutputFile $export
    $header = ($csv -split "`r?`n")[0].ToLowerInvariant()
    Assert-True ($header -match 'url' -and $header -match 'username' -and $header -match 'password') "the export has a CSV header ($header)"
    Assert-True ($csv -match 'e2e-import\.example') 'the export contains the imported site'
    Remove-Item -LiteralPath $export -Force   # it holds (test) passwords in clear
}

Register-Scenario -Area passwords -Name 'no saved password in clear on disk (Credential Manager / DPAPI)' -Tags smoke -Todo 'Passwords stored with Windows Credential Manager / DPAPI; reveal behind Windows Hello' -Body {
    $null = Get-Passwords (Get-MosaicState)
    $origin = (Get-TestUrl '/').TrimEnd('/')
    $secret = 'E2e-Disk-Probe-' + (New-Token)
    Invoke-Mosaic 'passwordsAdd' "$origin|diskprobe|$secret" | Out-Null
    $null = Wait-State 'saved' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'passwords.credentials' | Where-Object { (Get-Field $_ 'username') -eq 'diskprobe' }).Count -eq 1 }
    $state = Get-MosaicState
    Write-Step "store: $(Get-Field $state 'passwords.keychainScope')"
    Start-Sleep -Seconds 1
    $hits = @(Find-TextInFolder -Folder (Get-MosaicSession).DataDirectory -Text $secret)
    Assert-True ($hits.Count -eq 0) "the password is not stored in clear (found in: $($hits -join ', '))"
    $out = New-OutputPath 'dump-for-secret.json'
    Invoke-Mosaic 'dump' $out | Out-Null
    Assert-True (-not ((Get-Content -LiteralPath $out -Raw) -match [regex]::Escape($secret))) 'dump never contains a password'
    Invoke-Mosaic 'passwordsDelete' "$origin|diskprobe" | Out-Null
}

Register-Scenario -Area passwords -Name 'never save for a site; options' -Todo 'Generator; CSV import/export; Passwords & Autofill settings' -Body {
    $null = Get-Passwords (Get-MosaicState)
    $origin = (Get-TestUrl '/').TrimEnd('/')
    Invoke-Mosaic 'passwordsNeverSave' "$origin|on" | Out-Null
    $null = Wait-State 'the site is on the never-save list' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'passwords.neverSave').Count -ge 1 }
    $null = Open-TestPage '/login.html?autosubmit=nosave:E2e-NoSave-Secret-5' -Engine chromium -Title 'Mosaic Login Page'
    $null = Wait-State 'submitted' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s) (Get-Field (Get-SelectedTab $s) 'title') -eq 'Mosaic Logged In' }
    Start-Sleep -Seconds 2
    Assert-Equal '' (Get-Field (Get-PasswordTab (Get-MosaicState)) 'prompt') 'save prompt on a never-save site'
    Invoke-Mosaic 'passwordsNeverSave' "$origin|off" | Out-Null
    Invoke-Mosaic 'passwordsOption' 'offerToSave=off' | Out-Null
    $null = Wait-State 'offerToSave off' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'passwords.offerToSave') -eq $false }
    Invoke-Mosaic 'passwordsOption' 'offerToSave=on' | Out-Null
    $out = New-OutputPath 'audit.json'
    Invoke-Mosaic 'passwordsAudit' -Out $out | Out-Null
    Write-Step "audit: $((Wait-OutputFile $out).Trim())"
}

Register-Scenario -Area passwords -Name 'Passwords & Autofill settings page' -Todo 'Generator; CSV import/export; Passwords & Autofill settings' -Body {
    Invoke-Mosaic 'passwordsSettings' | Out-Null
    Start-Sleep -Milliseconds 800
    $null = Save-Screenshot 'settings-passwords'
    Invoke-Mosaic 'closeSettings' -AllowError | Out-Null
}
