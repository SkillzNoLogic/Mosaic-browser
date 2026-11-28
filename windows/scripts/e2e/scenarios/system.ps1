# system: Windows integration (windows/To Do.md "system"). There is no macOS vocabulary for
# these, so most checks look at Windows itself (registry, fonts, file activation); the system
# area's dump key "system" is used when it exists.

Register-Scenario -Area system -Name 'Fluent icon font available' -NoPipe -Todo 'Fluent icons everywhere (Segoe Fluent Icons; Segoe MDL2 Assets on Windows 10)' -Body {
    Add-Type -AssemblyName System.Drawing
    $families = @((New-Object System.Drawing.Text.InstalledFontCollection).Families | ForEach-Object { $_.Name })
    $fluent = $families -contains 'Segoe Fluent Icons'
    $mdl2 = $families -contains 'Segoe MDL2 Assets'
    Write-Step "Segoe Fluent Icons: $fluent; Segoe MDL2 Assets: $mdl2"
    Assert-True ($fluent -or $mdl2) 'an icon font Icons.cs relies on is installed'
}

Register-Scenario -Area system -Name 'opening an .html file from outside shows it' -Todo 'Set as default browser (registration, URL and file activation)' -Body {
    $file = Join-Path (Get-MosaicSession).LogDirectory 'activation-test.html'
    Write-TextFile $file '<!doctype html><html><head><meta charset="utf-8"><title>Mosaic File Activation</title></head><body>file</body></html>'
    $saved = $env:MOSAIC_DATA_DIR
    $env:MOSAIC_DATA_DIR = (Get-MosaicSession).DataDirectory
    try { $second = Start-Process -FilePath (Get-E2EConfig).AppPath -ArgumentList @("`"$file`"") -PassThru; $null = $second.Handle }
    finally { $env:MOSAIC_DATA_DIR = $saved }
    if (-not $second.WaitForExit(20000)) { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue; throw 'the second Mosaic did not hand the file over and exit' }
    $null = Wait-State 'the file open in the running window' -TimeoutSeconds (Get-E2EConfig).LoadSeconds -Condition { param($s)
        @(@(Get-Field $s 'tabs') | Where-Object { (Get-Field $_ 'title') -eq 'Mosaic File Activation' }).Count -gt 0 }
}

Register-Scenario -Area system -Name 'default-browser registration (Capabilities, URL associations)' -Todo 'Set as default browser (registration, URL and file activation)' -Body {
    $state = Get-MosaicState
    $null = Require-Field $state 'system' -Owner 'system'
    $registered = Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\RegisteredApplications' -ErrorAction SilentlyContinue
    $names = @()
    if ($registered) { $names = @($registered.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { $_.Name }) }
    Write-Step "RegisteredApplications (HKCU): $($names -join ', ')"
    Write-Step "system: $((Get-Field $state 'system') | ConvertTo-Json -Compress -Depth 4)"
    Assert-True (@($names | Where-Object { $_ -like '*Mosaic*' }).Count -ge 1) 'Mosaic is registered as a browser for the user'
}

Register-Scenario -Area system -Name 'notifications, jump list, taskbar state' -Todo 'Notifications (app notifications), jump list, taskbar integration' -Body {
    $state = Get-MosaicState
    $system = Require-Field $state 'system' -Owner 'system'
    Write-Step "system: $($system | ConvertTo-Json -Compress -Depth 4)"
}
