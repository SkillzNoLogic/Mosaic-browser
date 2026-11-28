<#
.SYNOPSIS
    Makes Windows Error Reporting write a dump when Mosaic or one of its engines crashes
    (LocalDumps), for the end-to-end suite. Needs an administrator (CI runners are).

.DESCRIPTION
    Sets HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<exe> for each
    executable (DumpFolder, DumpType, DumpCount) and turns off WER's crash dialog so a crash
    cannot block an unattended run. Run-E2E.ps1 then fails the scenario during which a new
    dump appears and copies the dump into its output. With -Disable, removes the keys again.

    On GitHub Actions the folder is also written to $GITHUB_ENV as MOSAIC_E2E_DUMPS.

.PARAMETER DumpFolder
    Where dumps go. Default C:\MosaicCrashDumps.

.PARAMETER Executables
    Default: Mosaic.exe, msedgewebview2.exe, firefox.exe.

.PARAMETER Full
    Full memory dumps instead of mini dumps (much larger; firefox.exe dumps can be 1 GB).

.EXAMPLE
    .\Enable-CrashDumps.ps1
.EXAMPLE
    .\Enable-CrashDumps.ps1 -Disable
#>
[CmdletBinding()]
param(
    [string]$DumpFolder = 'C:\MosaicCrashDumps',
    [string[]]$Executables = @('Mosaic.exe', 'msedgewebview2.exe', 'firefox.exe'),
    [ValidateRange(1, 100)] [int]$DumpCount = 10,
    [switch]$Full,
    [switch]$Disable
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Enable-CrashDumps.ps1 must run as an administrator (it writes to HKLM).'
}

$werKey = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting'
$localDumps = Join-Path $werKey 'LocalDumps'

if ($Disable) {
    foreach ($exe in $Executables) {
        $key = Join-Path $localDumps $exe
        if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force; Write-Host "LocalDumps off for $exe" }
    }
    foreach ($name in @('DumpFolder', 'DumpType', 'DumpCount')) { Remove-ItemProperty -LiteralPath $localDumps -Name $name -ErrorAction SilentlyContinue }
    return
}

New-Item -ItemType Directory -Path $DumpFolder -Force | Out-Null
if (-not (Test-Path -LiteralPath $localDumps)) { New-Item -Path $localDumps -Force | Out-Null }
$type = 1
if ($Full) { $type = 2 }
foreach ($exe in $Executables) {
    $key = Join-Path $localDumps $exe
    if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null }
    New-ItemProperty -LiteralPath $key -Name 'DumpFolder' -PropertyType ExpandString -Value $DumpFolder -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name 'DumpType' -PropertyType DWord -Value $type -Force | Out-Null
    New-ItemProperty -LiteralPath $key -Name 'DumpCount' -PropertyType DWord -Value $DumpCount -Force | Out-Null
    Write-Host "LocalDumps on for $exe -> $DumpFolder (type $type)"
}
# Any other process too (the folder on the LocalDumps key itself): a crash in a helper
# process with an unexpected name is still caught.
New-ItemProperty -LiteralPath $localDumps -Name 'DumpFolder' -PropertyType ExpandString -Value $DumpFolder -Force | Out-Null
New-ItemProperty -LiteralPath $localDumps -Name 'DumpType' -PropertyType DWord -Value 1 -Force | Out-Null
New-ItemProperty -LiteralPath $localDumps -Name 'DumpCount' -PropertyType DWord -Value $DumpCount -Force | Out-Null
# No "Mosaic has stopped working" dialog waiting for a click on an unattended runner.
New-ItemProperty -LiteralPath $werKey -Name 'DontShowUI' -PropertyType DWord -Value 1 -Force | Out-Null
# Dumps are written locally even if WER is set to send reports.
New-ItemProperty -LiteralPath $werKey -Name 'Disabled' -PropertyType DWord -Value 0 -Force | Out-Null

if ($env:GITHUB_ENV) { "MOSAIC_E2E_DUMPS=$DumpFolder" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append }
