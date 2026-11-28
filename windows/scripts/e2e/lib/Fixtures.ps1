# Helpers shared by the scenario files (dot-sourced by Run-E2E.ps1, so scenario bodies see
# them). Windows PowerShell 5.1 compatible.

function New-ExtensionFixture {
    <#
    .SYNOPSIS
        A minimal Manifest V3 extension (background service worker, a popup and an options
        page) zipped at -ZipPath; the smallest thing that exercises Mosaic's CRX handling.
    #>
    param([Parameter(Mandatory = $true)] [string]$ZipPath, [string]$Name = 'Mosaic E2E Fixture')
    $files = [ordered]@{
        'manifest.json' = @"
{
  "manifest_version": 3,
  "name": "$Name",
  "version": "1.0.0",
  "description": "Minimal extension used by Mosaic's end-to-end checks.",
  "permissions": ["storage"],
  "host_permissions": ["http://127.0.0.1/*"],
  "background": { "service_worker": "background.js" },
  "action": { "default_title": "E2E", "default_popup": "popup.html" },
  "options_page": "options.html"
}
"@
        'background.js' = "chrome.runtime.onInstalled.addListener(() => { chrome.storage.local.set({ installedAt: Date.now() }); });`n"
        'popup.html'    = "<!doctype html><html><head><meta charset=`"utf-8`"><title>E2E Popup</title></head><body><p>E2E popup</p></body></html>`n"
        'options.html'  = "<!doctype html><html><head><meta charset=`"utf-8`"><title>E2E Options</title></head><body><p>E2E options</p></body></html>`n"
    }
    Add-Type -AssemblyName System.IO.Compression
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entryName in $files.Keys) {
                $entry = $archive.CreateEntry($entryName).Open()
                try {
                    $bytes = $utf8.GetBytes($files[$entryName])
                    $entry.Write($bytes, 0, $bytes.Length)
                }
                finally { $entry.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
    $ZipPath
}

function Get-FixturePath {
    <# A file of scripts/e2e/fixtures. #>
    param([Parameter(Mandatory = $true, Position = 0)] [string]$Name)
    Join-Path (Get-E2EConfig).FixtureDirectory $Name
}

function Get-ZoneIdentifier {
    <# The Mark of the Web of a file (its Zone.Identifier stream), or $null. #>
    param([Parameter(Mandatory = $true)] [string]$Path)
    try { (Get-Content -LiteralPath $Path -Stream 'Zone.Identifier' -ErrorAction Stop) -join "`n" } catch { $null }
}

function Find-TextInFolder {
    <#
    .SYNOPSIS
        Files under a folder whose bytes contain the text (as UTF-8 or UTF-16), for "nothing
        on disk" checks (private tabs, passwords). Skips files that cannot be read.
    #>
    param([Parameter(Mandatory = $true)] [string]$Folder, [Parameter(Mandatory = $true)] [string]$Text, [long]$MaxBytes = 67108864)
    $needles = @([System.Text.Encoding]::UTF8.GetBytes($Text), [System.Text.Encoding]::Unicode.GetBytes($Text))
    $hits = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $Folder -Recurse -Force -File -ErrorAction SilentlyContinue)) {
        if ($file.Length -gt $MaxBytes -or $file.Length -eq 0) { continue }
        try {
            $stream = New-Object System.IO.FileStream($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]'ReadWrite, Delete')
            try {
                $bytes = New-Object byte[] $file.Length
                $read = $stream.Read($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
        catch { continue }
        $haystack = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes, 0, $read)   # byte-for-byte
        foreach ($needle in $needles) {
            $pattern = [System.Text.Encoding]::GetEncoding(28591).GetString($needle)
            if ($haystack.IndexOf($pattern, [System.StringComparison]::Ordinal) -ge 0) { $hits += $file.FullName; break }
        }
    }
    $hits
}

function Get-EngineProcesses {
    <# Engine processes started for a data folder: msedgewebview2.exe / firefox.exe whose command line names it. #>
    param([Parameter(Mandatory = $true)] [string]$DataDirectory, [string]$Name = 'msedgewebview2.exe')
    @(Get-CimInstance -ClassName Win32_Process -Filter "Name = '$Name'" | Where-Object {
            $_.CommandLine -and $_.CommandLine.IndexOf($DataDirectory, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        })
}

function Get-StateTab {
    <# Tab $Index of a dump (all tabs), or $null. #>
    param([Parameter(Mandatory = $true, Position = 0)] $State, [Parameter(Mandatory = $true, Position = 1)] [int]$Index)
    $tabs = @(Get-Field $State 'tabs')
    if ($Index -lt 0 -or $Index -ge $tabs.Count) { return $null }
    $tabs[$Index]
}

function Get-TabCount {
    param([Parameter(Mandatory = $true, Position = 0)] $State)
    @(Get-Field $State 'tabs').Count
}

function Invoke-OnEngines {
    <#
    .SYNOPSIS
        Runs a check once per engine (gecko, then chromium); collects failures so one engine's
        failure does not hide the other's result, then fails with both.
    #>
    param([Parameter(Mandatory = $true, Position = 0)] [scriptblock]$Check, [string[]]$Engines = @('gecko', 'chromium'))
    $problems = @()
    foreach ($engine in $Engines) {
        Write-Step "-- on $engine"
        try { & $Check $engine }
        catch {
            if ($_.Exception -is [MosaicE2E.PendingException] -or $_.Exception -is [MosaicE2E.HangException] -or $_.Exception -is [MosaicE2E.CrashException]) { throw }
            $problems += "${engine}: $($_.Exception.Message)"
            Write-Step "FAILED on ${engine}: $($_.Exception.Message)"
        }
    }
    if ($problems.Count -gt 0) { throw ($problems -join ' | ') }
}
