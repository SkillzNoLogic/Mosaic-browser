<#
.SYNOPSIS
    Makes sure the WebView2 Evergreen runtime is installed (CI only: it installs system software
    when the runtime is missing, after checking Microsoft's signature on the bootstrapper).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Import-Module (Join-Path $PSScriptRoot 'PerfHarness.psm1') -Force

$version = Get-WebView2Version
if (-not $version) {
    Write-Host 'WebView2 runtime not found; installing the Evergreen runtime.'
    $setup = Join-Path ([System.IO.Path]::GetTempPath()) 'MicrosoftEdgeWebview2Setup.exe'
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $setup -UseBasicParsing
    $signature = Get-AuthenticodeSignature -LiteralPath $setup
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "The WebView2 bootstrapper is not validly signed by Microsoft ($($signature.Status))."
    }
    $install = Start-Process -FilePath $setup -ArgumentList '/silent', '/install' -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "WebView2 setup failed with exit code $($install.ExitCode)." }
    $version = Get-WebView2Version
    if (-not $version) { throw 'WebView2 setup finished but the runtime is still not registered.' }
}
Write-Host "WebView2 runtime $version"
