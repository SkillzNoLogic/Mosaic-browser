<#
.SYNOPSIS
    Prepares a PC to run Mosaic: installs the Gecko engine (a private copy of Firefox ESR) and
    checks that the Chromium engine (the WebView2 runtime) is present.
    Windows counterpart of Scripts/bootstrap.sh. Safe to re-run; finished steps are skipped.

.DESCRIPTION
    Gecko    -> Firefox ESR for this machine's architecture (x64 or ARM64), downloaded from
                mozilla.org and unpacked into
                    %LOCALAPPDATA%\Mosaic\Engines\Firefox\firefox.exe
                (or under %MOSAIC_DATA_DIR% if that variable is set, as the app does).
                Nothing is installed in the Windows sense: Firefox's installer is only asked to
                extract its files (/ExtractDir), so no registry entries, shortcuts, services,
                scheduled tasks or default-browser registrations are created, and a Firefox you
                already have is never touched.
    Chromium -> Mosaic uses the WebView2 Evergreen runtime that ships with Windows 11 and is
                present on most Windows 10 PCs. This script only checks for it and tells you
                where to get it; it does not install system software.

    Authenticity: the download is fetched over HTTPS from download.mozilla.org, and both the
    installer and the extracted firefox.exe must carry a valid Authenticode signature whose
    signer is Mozilla Corporation, or nothing is installed.

    The private copy gets a policies.json that turns off Firefox's own updater and default-
    browser check, so that it never schedules background update tasks or prompts. To update
    the engine, re-run this script with -Force (with Mosaic closed).

.PARAMETER Force
    Download and replace the Gecko engine even if one is already in place.

.PARAMETER Architecture
    x64 or arm64. Default: the architecture of this machine.

.EXAMPLE
    .\bootstrap.ps1
.EXAMPLE
    .\bootstrap.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force,

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
# Windows PowerShell draws a progress bar for Invoke-WebRequest that slows large downloads
# down by an order of magnitude.
$ProgressPreference = 'SilentlyContinue'

$WebView2ClientKey = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$WebView2DownloadPage = 'https://developer.microsoft.com/microsoft-edge/webview2/'

function Get-MachineArchitecture {
    # The registry value is the machine's native architecture. The PROCESSOR_ARCHITECTURE
    # environment variable only describes this process, which may be running under emulation.
    $native = $null
    try {
        $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
        $native = (Get-ItemProperty -LiteralPath $key -Name PROCESSOR_ARCHITECTURE).PROCESSOR_ARCHITECTURE
    }
    catch {
        $native = $env:PROCESSOR_ARCHITEW6432
        if (-not $native) { $native = $env:PROCESSOR_ARCHITECTURE }
    }
    switch ($native) {
        'AMD64' { return 'x64' }
        'ARM64' { return 'arm64' }
        default { throw "Mosaic supports 64-bit Windows on x64 and ARM64 only (this machine reports '$native')." }
    }
}

function Get-DataRoot {
    # Same rule as Mosaic.Core.AppPaths.DataRoot.
    if ($env:MOSAIC_DATA_DIR) { return $env:MOSAIC_DATA_DIR }
    Join-Path $env:LOCALAPPDATA 'Mosaic'
}

function Assert-MozillaSignature {
    param([Parameter(Mandatory = $true)] [string]$Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') {
        throw "'$Path' does not have a valid Authenticode signature (status: $($signature.Status)). Not installing it."
    }
    $subject = $signature.SignerCertificate.Subject
    if ($subject -notmatch '(^|,\s*)O=Mozilla Corporation(\s*,|$)') {
        throw "'$Path' is signed by '$subject', not by Mozilla Corporation. Not installing it."
    }
}

function Get-FileWithRetry {
    param(
        [Parameter(Mandatory = $true)] [string]$Uri,
        [Parameter(Mandatory = $true)] [string]$Destination
    )
    $attempts = 3
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $Destination -UseBasicParsing
            return
        }
        catch {
            if ($attempt -eq $attempts) {
                throw "Download of $Uri failed after $attempts attempts: $($_.Exception.Message)"
            }
            Write-Host "    attempt $attempt failed ($($_.Exception.Message)); retrying"
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}

function Get-FirefoxVersion {
    param([Parameter(Mandatory = $true)] [string]$Executable)
    $version = (Get-Item -LiteralPath $Executable).VersionInfo.ProductVersion
    if ($version) { return $version }
    '?'
}

function Install-Gecko {
    param(
        [Parameter(Mandatory = $true)] [string]$EngineDirectory,
        [Parameter(Mandatory = $true)] [string]$Arch
    )

    $executable = Join-Path $EngineDirectory 'firefox.exe'
    if ((Test-Path -LiteralPath $executable) -and -not $Force) {
        Write-Host "==> Gecko runtime already installed: Firefox $(Get-FirefoxVersion $executable) in $EngineDirectory"
        Write-Host '    (use -Force to replace it with the current Firefox ESR)'
        return
    }

    # Replacing files that are in use fails half-way; find out before downloading anything.
    $prefix = $EngineDirectory.TrimEnd('\') + '\'
    $running = @(Get-Process -Name firefox -ErrorAction SilentlyContinue | Where-Object {
            $path = $null
            try { $path = $_.Path } catch { }
            $path -and $path.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
        })
    if ($running.Count -gt 0) {
        throw "Mosaic's Gecko engine is running from $EngineDirectory. Quit Mosaic and run this script again."
    }

    $os = if ($Arch -eq 'arm64') { 'win64-aarch64' } else { 'win64' }
    $url = "https://download.mozilla.org/?product=firefox-esr-latest-ssl&os=$os&lang=en-US"

    $enginesRoot = Split-Path -Parent $EngineDirectory
    New-Item -ItemType Directory -Path $enginesRoot -Force | Out-Null
    $token = [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
    $download = Join-Path ([System.IO.Path]::GetTempPath()) "mosaic-bootstrap-$token"
    # Extracted next to the destination so that the final step is a rename on the same volume.
    $staging = Join-Path $enginesRoot "Firefox.staging-$token"

    try {
        New-Item -ItemType Directory -Path $download -Force | Out-Null
        $installer = Join-Path $download 'firefox-esr-setup.exe'

        Write-Host "==> Downloading Firefox ESR ($Arch) from mozilla.org"
        Get-FileWithRetry -Uri $url -Destination $installer

        Write-Host '==> Verifying the download'
        Assert-MozillaSignature -Path $installer

        Write-Host '==> Extracting (no installation is performed)'
        # Firefox's full installer is a self-extracting 7-Zip archive. Its documented
        # /ExtractDir option unpacks the application files and exits without running the
        # installer proper. Two details of that stub matter here (see Mozilla's
        # other-licenses/7zstub/.../SfxSetup.cpp):
        #  - it takes everything after "/ExtractDir=" on the command line, verbatim, as the
        #    directory. The path must therefore NOT be quoted, even if it contains spaces, and
        #    no other option may follow. Start-Process passes a single argument string through
        #    untouched, which the call operator (&) would not: it would add quotes.
        #  - with the option it shows neither progress nor prompts, and reports failure through
        #    its exit code.
        # The trailing backslash makes the value a directory prefix whether or not the stub
        # normalises it.
        if ($staging.Contains('"')) { throw "Unexpected quote character in path: $staging" }
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        $extraction = Start-Process -FilePath $installer -ArgumentList "/ExtractDir=$staging\" -Wait -PassThru
        if ($extraction.ExitCode -ne 0) {
            throw "Firefox's installer could not extract its files (exit code $($extraction.ExitCode))."
        }

        # The archive holds the application in a "core" folder next to the installer program.
        $source = $null
        foreach ($candidate in @((Join-Path $staging 'core'), $staging)) {
            if (Test-Path -LiteralPath (Join-Path $candidate 'firefox.exe')) {
                $source = $candidate
                break
            }
        }
        if (-not $source) {
            throw "firefox.exe was not found in the extracted files under $staging."
        }
        $extracted = Join-Path $source 'firefox.exe'
        Assert-MozillaSignature -Path $extracted
        $version = Get-FirefoxVersion $extracted

        # Keep the private copy from updating itself (which would register background update
        # tasks with Windows) and from asking to become the default browser. Enterprise
        # policies in the application's own "distribution" folder apply to this copy only.
        $distribution = Join-Path $source 'distribution'
        New-Item -ItemType Directory -Path $distribution -Force | Out-Null
        $policies = '{"policies":{"DisableAppUpdate":true,"DontCheckDefaultBrowser":true,"DisableDefaultBrowserAgent":true}}'
        [System.IO.File]::WriteAllText((Join-Path $distribution 'policies.json'), $policies, (New-Object System.Text.UTF8Encoding($false)))

        Write-Host "==> Installing into $EngineDirectory"
        if (Test-Path -LiteralPath $EngineDirectory) {
            Remove-Item -LiteralPath $EngineDirectory -Recurse -Force
        }
        Move-Item -LiteralPath $source -Destination $EngineDirectory
        Write-Host "==> Gecko ready: Firefox $version ($Arch) in $EngineDirectory"
    }
    finally {
        foreach ($leftover in @($download, $staging)) {
            if (Test-Path -LiteralPath $leftover) {
                Remove-Item -LiteralPath $leftover -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Get-WebView2Version {
    # The documented check ("Distribute your app and the WebView2 Runtime", Microsoft Learn):
    # a REG_SZ value "pv" other than empty or 0.0.0.0 under the runtime's EdgeUpdate client key,
    # per machine (in the 32-bit registry view on 64-bit Windows) or per user.
    $locations = @(
        "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$WebView2ClientKey",
        "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\$WebView2ClientKey",
        "Registry::HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\$WebView2ClientKey"
    )
    foreach ($location in $locations) {
        $entry = Get-ItemProperty -LiteralPath $location -Name pv -ErrorAction SilentlyContinue
        if ($entry -and $entry.pv -and $entry.pv -ne '0.0.0.0') {
            return $entry.pv
        }
    }
    $null
}

# ---- main -----------------------------------------------------------------------------------

# Windows PowerShell on older .NET Framework defaults may not offer TLS 1.2.
[System.Net.ServicePointManager]::SecurityProtocol =
    [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12

if (-not $Architecture) { $Architecture = Get-MachineArchitecture }

$engineDirectory = Join-Path (Join-Path (Get-DataRoot) 'Engines') 'Firefox'
Install-Gecko -EngineDirectory $engineDirectory -Arch $Architecture

$webView2 = Get-WebView2Version
if ($webView2) {
    Write-Host "==> WebView2 runtime present: $webView2"
}
else {
    Write-Warning ("The WebView2 runtime was not found. Mozilla tabs will work; Chromium tabs will not until it is installed. " +
        "Get the 'Evergreen Bootstrapper' from $WebView2DownloadPage (it is part of Windows 11 and of most Windows 10 installations).")
}

Write-Host 'Done. Next: scripts\build.ps1'
