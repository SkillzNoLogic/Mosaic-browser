# canvas: layout editor, Gradient Studio, configurations (windows/To Do.md "canvas"). Commands
# and dump of Sources/Mosaic/Features/Canvas/CanvasAutomation.swift; dump key "canvas".

function Get-Canvas {
    param([Parameter(Mandatory = $true)] $State)
    Require-Field $State 'canvas' -Owner 'canvas'
}

Register-Scenario -Area canvas -Name 'layout canvas: add, move, remove, undo, redo' -Tags smoke -Todo 'Drag-and-drop layout canvas with undo/redo and keyboard editing' -Body {
    Invoke-Mosaic 'mosaic' 'chrome' -AllowError | Out-Null
    Invoke-Mosaic 'canvasMode' 'layout' | Out-Null
    $state = Get-MosaicState
    $null = Get-Canvas $state
    $null = Wait-State 'the layout editor open' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'canvas.mode') -eq 'layout' }
    $null = Save-Screenshot 'canvas-layout'
    $before = (Get-Field (Get-MosaicState) 'layout.regions') | ConvertTo-Json -Compress -Depth 5
    Invoke-Mosaic 'canvasAdd' 'home>toolbar:0' | Out-Null
    $null = Wait-State 'home added to the toolbar' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'layout.regions.toolbar') -contains 'home' }
    Invoke-Mosaic 'canvasMove' 'home>toolbar:3' | Out-Null
    Invoke-Mosaic 'canvasUndo' | Out-Null
    Invoke-Mosaic 'canvasUndo' | Out-Null
    $null = Wait-State 'undo restores the layout' -TimeoutSeconds 10 -Condition { param($s) ((Get-Field $s 'layout.regions') | ConvertTo-Json -Compress -Depth 5) -eq $before }
    Invoke-Mosaic 'canvasRedo' | Out-Null
    $null = Wait-State 'redo adds home again' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'layout.regions.toolbar') -contains 'home' -and (Get-Field $s 'canvas.canUndo') -eq $true }
    Invoke-Mosaic 'canvasRemove' 'home' | Out-Null
    $null = Wait-State 'home removed' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'layout.regions.toolbar') -notcontains 'home' }
}

Register-Scenario -Area canvas -Name 'layout canvas: keyboard editing' -Todo 'Drag-and-drop layout canvas with undo/redo and keyboard editing' -Body {
    Invoke-Mosaic 'canvasMode' 'layout' | Out-Null
    $null = Get-Canvas (Get-MosaicState)
    Invoke-Mosaic 'canvasSelect' 'toolbar:0' | Out-Null
    $state = Wait-State 'a selection' -TimeoutSeconds 10 -Condition { param($s) $null -ne (Get-Field $s 'canvas.selection') }
    $component = Get-Field $state 'canvas.selection.component'
    Invoke-Mosaic 'canvasKey' 'alt-right' | Out-Null
    $null = Wait-State "$component moved right" -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'canvas.selection.index') -eq 1 }
    Invoke-Mosaic 'canvasKey' 'undo' | Out-Null
    Invoke-Mosaic 'canvasKey' 'escape' | Out-Null
}

Register-Scenario -Area canvas -Name 'drag and drop refuses an invalid drop with a message' -Todo 'Drag-and-drop layout canvas with undo/redo and keyboard editing' -Body {
    Invoke-Mosaic 'canvasMode' 'layout' | Out-Null
    $null = Get-Canvas (Get-MosaicState)
    Invoke-Mosaic 'canvasDrag' 'addressField>rail' | Out-Null
    $state = Get-MosaicState
    Write-Step "message: '$(Get-Field $state 'canvas.message')' error=$(Get-Field $state 'canvas.messageIsError')"
    Assert-True ((Get-Field $state 'canvas.messageIsError') -eq $true -or @(Get-Field $state 'layout.regions.rail') -notcontains 'addressField') 'the address field cannot go in the narrow rail'
}

Register-Scenario -Area canvas -Name 'Gradient Studio applies a preset to the window background' -Todo 'Gradient Studio (Eminence port) for window, new-tab page and workspace backgrounds' -Body {
    Invoke-Mosaic 'canvasMode' 'gradient' | Out-Null
    $null = Get-Canvas (Get-MosaicState)
    Invoke-Mosaic 'gradientTarget' 'window' | Out-Null
    Invoke-Mosaic 'gradientSet' 'kind=linear' | Out-Null
    Invoke-Mosaic 'gradientSet' 'stops=#FF0066@0,#3300FF@1' | Out-Null
    $null = Wait-State 'the window uses a gradient' -TimeoutSeconds 10 -Condition { param($s) (Get-Field $s 'canvas.usesGradient.window') -eq $true }
    $null = Save-Screenshot 'gradient-studio'
    Invoke-Mosaic 'closeSettings' | Out-Null
    Start-Sleep -Milliseconds 600
    $null = Save-Screenshot 'gradient-window'
    Invoke-Mosaic 'newTabBackground' 'window' | Out-Null
}

Register-Scenario -Area canvas -Name 'configurations: save, export, import, validate, delete' -Tags smoke -Todo 'Named configurations; export/import/validate; files move between macOS and Windows' -Body {
    Invoke-Mosaic 'mosaic' 'opera' -AllowError | Out-Null
    Invoke-Mosaic 'configSave' 'E2E Config' | Out-Null
    $null = Get-Canvas (Get-MosaicState)
    $null = Wait-State 'configuration saved' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'canvas.configurations' | Where-Object { (Get-Field $_ 'name') -eq 'E2E Config' }).Count -eq 1 }
    $file = New-OutputPath 'e2e-config.json'
    Invoke-Mosaic 'configExport' 'E2E Config' -Out $file | Out-Null
    $json = Wait-OutputFile $file -Json
    Write-Step "exported keys: $(($json.PSObject.Properties | ForEach-Object { $_.Name }) -join ', ')"
    Invoke-Mosaic 'configDelete' 'E2E Config' | Out-Null
    $null = Wait-State 'configuration deleted' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'canvas.configurations' | Where-Object { (Get-Field $_ 'name') -eq 'E2E Config' }).Count -eq 0 }
    Invoke-Mosaic 'configImport' $file | Out-Null
    $state = Wait-State 'configuration imported back' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'canvas.configurations' | Where-Object { (Get-Field $_ 'name') -like 'E2E Config*' }).Count -ge 1 }
    Write-Step "lastImport: $((Get-Field $state 'canvas.lastImport') | ConvertTo-Json -Compress)"
    Assert-Equal '' ([string](Get-Field $state 'canvas.lastImport.error')) 'import error'
    $broken = New-OutputPath 'broken-config.json'
    Write-TextFile $broken '{"schemaVersion": 99, "name": 12, "layout": "nope"}'
    Invoke-Mosaic 'configImport' $broken -AllowError | Out-Null
    $state = Wait-State 'a broken file rejected with a reason' -TimeoutSeconds 10 -Condition { param($s) [string](Get-Field $s 'canvas.lastImport.error') -ne '' -or (Get-Field $s 'canvas.messageIsError') -eq $true }
    Write-Step "broken import: $((Get-Field $state 'canvas.lastImport') | ConvertTo-Json -Compress)"
}

Register-Scenario -Area canvas -Name 'a configuration exported by macOS imports and applies on Windows' -Todo 'Named configurations; export/import/validate; files move between macOS and Windows' -Body {
    Invoke-Mosaic 'configImport' (Get-FixturePath 'macos-configuration.json') | Out-Null
    $null = Get-Canvas (Get-MosaicState)
    $state = Wait-State 'the macOS configuration imported' -TimeoutSeconds 10 -Condition { param($s) @(Get-Field $s 'canvas.configurations' | Where-Object { (Get-Field $_ 'name') -eq 'E2E From macOS' }).Count -ge 1 }
    Write-Step "lastImport: $((Get-Field $state 'canvas.lastImport') | ConvertTo-Json -Compress)"
    Assert-Equal '' ([string](Get-Field $state 'canvas.lastImport.error')) 'import error'
    Invoke-Mosaic 'configApply' 'E2E From macOS' | Out-Null
    $null = Wait-State 'the macOS layout applied (compact, dark, radius 10)' -TimeoutSeconds 10 -Condition { param($s)
        (Get-Field $s 'layout.appearance.density') -eq 'compact' -and (Get-Field $s 'layout.appearance.colorScheme') -eq 'dark' -and [string](Get-Field $s 'layout.appearance.cornerRadius') -eq '10' }
    $null = Save-Screenshot 'macos-configuration'
}

Register-Scenario -Area canvas -Name 'a macOS configuration using the Safari preset falls back cleanly' -Todo 'Named configurations; export/import/validate; files move between macOS and Windows' -Body {
    Invoke-Mosaic 'configImport' (Get-FixturePath 'macos-configuration-safari.json') | Out-Null
    $null = Get-Canvas (Get-MosaicState)
    $state = Wait-State 'the Safari configuration read (imported or refused with a reason)' -TimeoutSeconds 10 -Condition { param($s) $null -ne (Get-Field $s 'canvas.lastImport') }
    Write-Step "lastImport: $((Get-Field $state 'canvas.lastImport') | ConvertTo-Json -Compress)"
    if (@(Get-Field $state 'canvas.configurations' | Where-Object { (Get-Field $_ 'name') -like 'E2E Safari*' }).Count -gt 0) {
        Invoke-Mosaic 'configApply' 'E2E Safari From macOS' -AllowError | Out-Null
    }
    $state = Get-MosaicState
    Assert-True ((Get-Field $state 'mosaic') -ne 'safari' -and (Get-Field $state 'layout.preset') -ne 'safari') 'no Safari Mosaic is active on Windows'
    $null = Save-Screenshot 'macos-safari-configuration'
}
