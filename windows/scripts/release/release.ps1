<#
.SYNOPSIS
    Builds Mosaic for Windows' release assets: Mosaic-<version>-windows-x64.zip and
    Mosaic-<version>-windows-arm64.zip, named as the in-app updater looks for them, and (with
    -Upload) attaches them and their signatures to the GitHub release.

.DESCRIPTION
    Mosaic's updater installs only a zip whose Ed25519 signature verifies against the release key
    compiled into the app (Mosaic.Core/Platform/UpdatePackage.cs, the same key as macOS). The
    private key lives only in the release Mac's Keychain, so signing happens there:

      1. On Windows (or a Windows CI runner):   .\windows\scripts\release\release.ps1
      2. Copy the two zips to the release Mac and sign them there:
             swift Scripts/sign-update.swift Mosaic-0.3.1-windows-x64.zip Mosaic-0.3.1-windows-arm64.zip
         which writes Mosaic-0.3.1-windows-x64.zip.sig and Mosaic-0.3.1-windows-arm64.zip.sig.
         (Sign the exact bytes that will be uploaded: re-zipping changes them.)
      3. Attach all four files to the release v0.3.1 (the same release as the macOS assets):
             gh release upload v0.3.1 Mosaic-0.3.1-windows-*.zip Mosaic-0.3.1-windows-*.zip.sig
         or run this script again with -Upload from a folder that holds them.

    The version is windows\Directory.Build.props' <Version>, which must equal the release tag
    (v<version>): the updater checks the downloaded Mosaic.dll's version against the tag.

.PARAMETER Runtime
    win-x64, win-arm64 or both (default).

.PARAMETER Upload
    Upload the zips and their .sig files (which must exist next to them) to the GitHub release
    v<version> with the gh CLI. Refuses to upload a zip without its signature.

.EXAMPLE
    .\windows\scripts\release\release.ps1
.EXAMPLE
    .\windows\scripts\release\release.ps1 -Upload
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$Runtime = @('win-x64', 'win-arm64'),

    [switch]$Upload
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$windowsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$publishRoot = Join-Path $windowsRoot 'publish'
$props = New-Object System.Xml.XmlDocument
$props.Load((Join-Path $windowsRoot 'Directory.Build.props'))
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if (-not $version) { throw 'No <Version> in windows\Directory.Build.props.' }

function Get-AssetName([string]$rid) {
    # build.ps1 names packages after the runtime identifier; releases name them after the OS.
    $architecture = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
    "Mosaic-$version-windows-$architecture.zip"
}

if (-not $Upload) {
    & (Join-Path $windowsRoot 'scripts\build.ps1') -Runtime $Runtime -Zip
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "build.ps1 failed ($LASTEXITCODE)." }
    foreach ($rid in $Runtime) {
        $built = Join-Path $publishRoot "Mosaic-$version-$rid.zip"
        if (-not (Test-Path -LiteralPath $built)) { throw "build.ps1 did not produce $built." }
        $asset = Join-Path $publishRoot (Get-AssetName $rid)
        Move-Item -LiteralPath $built -Destination $asset -Force
        $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "==> $asset"
        Write-Host "    SHA-256 $hash"
    }
    $names = ($Runtime | ForEach-Object { Get-AssetName $_ }) -join ' '
    Write-Host ''
    Write-Host 'Next, on the release Mac (the release key is in its Keychain):'
    Write-Host "    swift Scripts/sign-update.swift $names"
    Write-Host "Then attach the zips and their .sig files to release v$version (gh release upload, or this script with -Upload)."
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is needed to upload.' }
$files = @()
foreach ($rid in $Runtime) {
    $asset = Join-Path $publishRoot (Get-AssetName $rid)
    foreach ($file in @($asset, "$asset.sig")) {
        if (-not (Test-Path -LiteralPath $file)) { throw "$file is missing. Build, then sign on the release Mac first." }
        $files += $file
    }
    $signature = [System.IO.File]::ReadAllText("$asset.sig").Trim()
    if ([Convert]::FromBase64String($signature).Length -ne 64) { throw "$asset.sig is not an Ed25519 signature." }
}
& gh release upload "v$version" @files --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)." }
Write-Host "Uploaded $($files.Count) files to release v$version."
