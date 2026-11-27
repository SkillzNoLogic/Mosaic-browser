<#
.SYNOPSIS
    Sends one automation command to a running Mosaic started with --mosaic-automation and
    prints the reply. Windows counterpart of Scripts/drive.swift.

.DESCRIPTION
    Commands and their meaning are listed in windows/CONTRACT.md ("Automation"). The reply is
    printed as received, one line of JSON. The exit code is 0 if Mosaic answered {"ok": true},
    1 if it answered {"ok": false} or could not be reached.

.EXAMPLE
    .\drive.ps1 navigate example.com
.EXAMPLE
    .\drive.ps1 engine chromium
.EXAMPLE
    .\drive.ps1 dump C:\Temp\mosaic-state.json
.EXAMPLE
    .\drive.ps1 geckoEval version C:\Temp\gecko-version.json
#>
[CmdletBinding()]
param(
    # The command, e.g. navigate, newTab, engine, searchEngine, installFile, dump, screenshot, quit.
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Command,

    # The command's argument, if it takes one.
    [Parameter(Position = 1)]
    [string]$Value,

    # Output file, for the commands that write one (geckoEval).
    [Parameter(Position = 2)]
    [string]$Out,

    # Seconds to wait for Mosaic's reply.
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'MosaicAutomation.psm1') -Force

try {
    $result = Send-MosaicCommand -Do $Command -Value $Value -Out $Out -ReplyTimeoutSeconds $TimeoutSeconds
}
catch {
    [Console]::Error.WriteLine("drive.ps1: $($_.Exception.Message)")
    exit 1
}

Write-Output $result.Raw
if ((Get-MosaicProperty $result.Reply 'ok') -ne $true) {
    exit 1
}
exit 0
