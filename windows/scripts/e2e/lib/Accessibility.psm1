# Accessibility scan of Mosaic's UI Automation tree (scripts/e2e). Windows PowerShell 5.1
# compatible; uses the UIAutomationClient assemblies that ship with Windows.
#
# Reports, for Mosaic's own UI (web page content inside WebView2 / Gecko is skipped):
#   - unnamed:      an interactive control (button, edit, check box, list item...) without a
#                   UI Automation Name, so a screen reader announces nothing useful;
#   - notFocusable: an enabled, on-screen interactive control that cannot take keyboard focus;
#   - notReached:   (with -TabWalk) a focusable control that Tab / Shift+Tab never reached.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# The proxies that expose plain Win32 controls (buttons, edits, list views) with their real
# control types; WinUI implements UI Automation itself and does not need them.
try {
    Add-Type -AssemblyName UIAutomationClientsideProviders
    [System.Windows.Automation.ClientSettings]::RegisterClientSideProviderAssembly(
        [UIAutomationClientsideProviders.UIAutomationClientSideProviders].Assembly.GetName())
}
catch { Write-Verbose "UI Automation client-side providers not registered: $($_.Exception.Message)" }

$script:Interactive = @('Button', 'CheckBox', 'ComboBox', 'Edit', 'Hyperlink', 'ListItem', 'MenuItem', 'RadioButton',
    'Slider', 'Spinner', 'SplitButton', 'TabItem', 'TreeItem', 'DataItem', 'MenuBar', 'ScrollBar', 'Document')
# Reached with arrow keys inside their container, not with Tab: not expected in the Tab order.
$script:ArrowNavigated = @('ListItem', 'TabItem', 'TreeItem', 'MenuItem', 'DataItem', 'RadioButton')
# Not expected to have a Name of their own or to be focusable.
$script:NameOptional = @('ScrollBar', 'Document', 'MenuBar')
$script:PageFrameworks = @('Chrome', 'Gecko')
# Window caption buttons are reached with Alt+Space, not Tab, on every Windows app.
$script:CaptionButtons = @('Minimize', 'Maximize', 'Restore', 'Close')

function Get-ControlTypeName {
    param($ControlType)
    if ($null -eq $ControlType) { return '' }
    ([string]$ControlType.ProgrammaticName) -replace '^ControlType\.', ''
}

function Get-RuntimeKey {
    param($Element)
    try { ($Element.GetRuntimeId() -join '.') } catch { '' }
}

function Test-PageContent {
    param($Current)
    if ($script:PageFrameworks -contains [string]$Current.FrameworkId) { return $true }
    $class = [string]$Current.ClassName
    $class -like 'Chrome_*' -or $class -like 'Mozilla*' -or $class -eq 'Intermediate D3D Window'
}

function Get-UiaScan {
    <#
    .SYNOPSIS
        Walks the control view of a window. Returns Elements, Issues, PageRegions and counts.
    #>
    param([Parameter(Mandatory = $true)] [long]$WindowHandle, [int]$MaxElements = 5000, [int]$MaxDepth = 60)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$WindowHandle)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $elements = New-Object System.Collections.ArrayList
    $issues = New-Object System.Collections.ArrayList
    $pages = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack
    $stack.Push(@($root, 0, ''))
    $truncated = $false
    while ($stack.Count -gt 0) {
        $item = $stack.Pop()
        $element = $item[0]; $depth = $item[1]; $parentPath = $item[2]
        if ($elements.Count -ge $MaxElements) { $truncated = $true; break }
        try { $current = $element.Current } catch { continue }
        $type = Get-ControlTypeName $current.ControlType
        # Plain Win32 controls without a UIA proxy show up as panes; their class says what they are.
        if ($type -eq 'Pane') {
            $class = [string]$current.ClassName
            if ($class -match '(?i)(^|\.)BUTTON(\.|$)') { $type = 'Button' }
            elseif ($class -match '(?i)(^|\.)EDIT(\.|$)') { $type = 'Edit' }
            elseif ($class -match '(?i)(^|\.)COMBOBOX(\.|$)') { $type = 'ComboBox' }
        }
        $name = [string]$current.Name
        $label = $type
        if ($name) { $label += "('" + $name + "')" } elseif ($current.AutomationId) { $label += "#" + $current.AutomationId }
        $path = $label
        if ($parentPath) { $path = $parentPath + ' > ' + $label }
        $rect = $current.BoundingRectangle
        $record = [pscustomobject]@{
            Type        = $type
            Name        = $name
            AutomationId = [string]$current.AutomationId
            ClassName   = [string]$current.ClassName
            Framework   = [string]$current.FrameworkId
            Focusable   = [bool]$current.IsKeyboardFocusable
            Enabled     = [bool]$current.IsEnabled
            Offscreen   = [bool]$current.IsOffscreen
            Rect        = $(if ($rect.IsEmpty) { '' } else { '{0:0},{1:0} {2:0}x{3:0}' -f $rect.X, $rect.Y, $rect.Width, $rect.Height })
            Depth       = $depth
            Path        = $path
            RuntimeId   = Get-RuntimeKey $element
        }
        [void]$elements.Add($record)
        if ($depth -gt 0 -and (Test-PageContent $current)) {
            [void]$pages.Add($record)
            continue   # web page content: not Mosaic's UI
        }
        if ($script:Interactive -contains $type -and $script:NameOptional -notcontains $type) {
            if ([string]::IsNullOrWhiteSpace($name)) {
                [void]$issues.Add([pscustomobject]@{ Kind = 'unnamed'; Type = $type; AutomationId = $record.AutomationId; ClassName = $record.ClassName; Rect = $record.Rect; Path = $path })
            }
            $isCaption = $script:CaptionButtons -contains $record.AutomationId -and $path -like '*Title Bar*'
            if ($record.Enabled -and -not $record.Offscreen -and -not $record.Focusable -and -not $isCaption) {
                [void]$issues.Add([pscustomobject]@{ Kind = 'notFocusable'; Type = $type; AutomationId = $record.AutomationId; ClassName = $record.ClassName; Rect = $record.Rect; Path = $path })
            }
        }
        if ($depth -ge $MaxDepth) { continue }
        $children = @()
        try {
            $child = $walker.GetFirstChild($element)
            while ($null -ne $child) {
                $children += $child
                $child = $walker.GetNextSibling($child)
            }
        }
        catch { }
        for ($i = $children.Count - 1; $i -ge 0; $i--) { $stack.Push(@($children[$i], ($depth + 1), $path)) }
    }
    [pscustomobject]@{
        Elements    = $elements
        Issues      = $issues
        PageRegions = $pages
        Truncated   = $truncated
        Unnamed     = @($issues | Where-Object { $_.Kind -eq 'unnamed' }).Count
        NotFocusable = @($issues | Where-Object { $_.Kind -eq 'notFocusable' }).Count
        NotReached  = 0
        UnnamedTabStops = 0
        TabWalkProblem = ''
        TabStops    = @()
    }
}

function Invoke-TabWalk {
    <#
    .SYNOPSIS
        Brings the window to the front and presses Tab up to -Presses times, recording what
        has keyboard focus after each press. Returns the focused elements in order. Takes the
        keyboard focus: only for CI runners or when asked.
    #>
    param([Parameter(Mandatory = $true)] [long]$WindowHandle, [Parameter(Mandatory = $true)] [int]$ProcessId, [int]$Presses = 120)
    Add-Type -AssemblyName System.Windows.Forms
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$WindowHandle)
    try { $root.SetFocus() } catch { }
    [void][MosaicE2E.Win32]::Activate($WindowHandle)
    Start-Sleep -Milliseconds 400
    $stops = New-Object System.Collections.ArrayList
    $first = $null
    for ($i = 0; $i -lt $Presses; $i++) {
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
        Start-Sleep -Milliseconds 120
        try { $focused = [System.Windows.Automation.AutomationElement]::FocusedElement } catch { continue }
        if ($null -eq $focused) { continue }
        try { $current = $focused.Current } catch { continue }
        $key = Get-RuntimeKey $focused
        $stop = [pscustomobject]@{
            Press = $i + 1; Type = (Get-ControlTypeName $current.ControlType); Name = [string]$current.Name
            AutomationId = [string]$current.AutomationId; Framework = [string]$current.FrameworkId
            Page = (Test-PageContent $current); ProcessId = [int]$current.ProcessId; RuntimeId = $key
            InMosaic = ($null -ne $current.ProcessId -and [int]$current.ProcessId -eq $ProcessId)
        }
        [void]$stops.Add($stop)
        if ($i -eq 0 -and -not $stop.InMosaic -and -not $stop.Page) {
            # Focus is in another application (on some runners a Windows set-up window): the
            # walk says nothing about Mosaic.
            [void]$stops.Add($stop)
            break
        }
        if ($null -eq $first -and -not $stop.Page) { $first = $key }
        elseif ($key -and $key -eq $first -and $i -gt 2) { break }   # wrapped around
    }
    , $stops
}

function Add-TabWalkFindings {
    <# Marks focusable, Tab-navigable controls that the walk never reached. #>
    param([Parameter(Mandatory = $true)] $Scan, [Parameter(Mandatory = $true)] $Stops)
    $reached = @{}
    foreach ($stop in $Stops) { if ($stop.RuntimeId) { $reached[$stop.RuntimeId] = $true } }
    $Scan.TabStops = $Stops
    $inMosaic = @($Stops | Where-Object { $_.InMosaic })
    if ($inMosaic.Count -eq 0) {
        $Scan.TabWalkProblem = "the Tab walk could not put the keyboard focus in Mosaic (focus was in process $(@($Stops | Select-Object -First 1 | ForEach-Object { $_.ProcessId })) '$(@($Stops | Select-Object -First 1 | ForEach-Object { $_.Name }))')"
        return
    }
    # A Tab stop that a screen reader announces as nothing.
    foreach ($stop in $inMosaic) {
        if (-not $stop.Page -and [string]::IsNullOrWhiteSpace($stop.Name)) {
            [void]$Scan.Issues.Add([pscustomobject]@{ Kind = 'unnamedTabStop'; Type = $stop.Type; AutomationId = $stop.AutomationId; ClassName = ''; Rect = ''; Path = "Tab stop $($stop.Press)" })
            $Scan.UnnamedTabStops++
        }
    }
    $count = 0
    foreach ($element in $Scan.Elements) {
        if ($script:Interactive -notcontains $element.Type -or $script:ArrowNavigated -contains $element.Type -or $script:NameOptional -contains $element.Type) { continue }
        if (-not $element.Focusable -or -not $element.Enabled -or $element.Offscreen) { continue }
        if ($script:PageFrameworks -contains $element.Framework) { continue }
        if (-not $element.RuntimeId -or $reached.ContainsKey($element.RuntimeId)) { continue }
        [void]$Scan.Issues.Add([pscustomobject]@{ Kind = 'notReached'; Type = $element.Type; AutomationId = $element.AutomationId; ClassName = $element.ClassName; Rect = $element.Rect; Path = $element.Path })
        $count++
    }
    $Scan.NotReached = $count
}

function Write-UiaReport {
    <# Writes <base>.json (everything) and <base>.md (issues) and returns the markdown path. #>
    param([Parameter(Mandatory = $true)] $Scan, [Parameter(Mandatory = $true)] [string]$BasePath, [string]$Title = 'Accessibility scan')
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $json = [ordered]@{
        title = $Title; unnamed = $Scan.Unnamed; notFocusable = $Scan.NotFocusable; notReached = $Scan.NotReached
        unnamedTabStops = $Scan.UnnamedTabStops; tabWalkProblem = $Scan.TabWalkProblem
        truncated = $Scan.Truncated; elementCount = $Scan.Elements.Count
        issues = @($Scan.Issues); tabStops = @($Scan.TabStops); pageRegions = @($Scan.PageRegions | ForEach-Object { $_.Path })
        elements = @($Scan.Elements)
    }
    [System.IO.File]::WriteAllText($BasePath + '.json', ($json | ConvertTo-Json -Depth 6), $utf8)
    $lines = @("# $Title", '', "$($Scan.Elements.Count) elements; $($Scan.Unnamed) unnamed, $($Scan.NotFocusable) not focusable, $($Scan.NotReached) not reached by Tab, $($Scan.UnnamedTabStops) unnamed Tab stops.", '')
    if ($Scan.TabWalkProblem) { $lines += "Tab walk: $($Scan.TabWalkProblem)."; $lines += '' }
    if ($Scan.Issues.Count -gt 0) {
        $lines += '| Issue | Control | AutomationId | Class | Where | Path |'
        $lines += '| --- | --- | --- | --- | --- | --- |'
        foreach ($issue in $Scan.Issues) {
            $lines += ('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $issue.Kind, $issue.Type, $issue.AutomationId, $issue.ClassName, $issue.Rect, ($issue.Path -replace '\|', '/'))
        }
    }
    if (@($Scan.TabStops).Count -gt 0) {
        $lines += ''
        $lines += '## Tab order'
        $lines += ''
        foreach ($stop in $Scan.TabStops) {
            $where = 'Mosaic'
            if ($stop.Page) { $where = 'page' } elseif (-not $stop.InMosaic) { $where = "another process ($($stop.ProcessId))" }
            $lines += ('{0}. {1} "{2}" {3} ({4})' -f $stop.Press, $stop.Type, $stop.Name, $stop.AutomationId, $where)
        }
    }
    [System.IO.File]::WriteAllText($BasePath + '.md', ($lines -join "`r`n") + "`r`n", $utf8)
    $BasePath + '.md'
}

Export-ModuleMember -Function Get-UiaScan, Invoke-TabWalk, Add-TabWalkFindings, Write-UiaReport
