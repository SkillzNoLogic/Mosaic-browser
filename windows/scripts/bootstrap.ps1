<#
.SYNOPSIS
    Prepares a PC to build and run Mosaic: installs the Gecko engine (a private copy of Firefox
    ESR), checks that the Chromium engine (the WebView2 runtime) is present, and builds the
    content blocker. Windows counterpart of Scripts/bootstrap.sh. Safe to re-run; finished
    steps are skipped.

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
    Blocker  -> uBlock Origin's source at a pinned release (checksum-verified), from which its
                network filtering engine is bundled into windows\vendor\ublock\ublock-engine.js,
                plus a snapshot of uBO's default filter lists in windows\vendor\ublock\lists\.
                The build ships both. Bundling needs Node.js (for npx, which fetches the pinned
                esbuild); the bundle and the lists are rebuilt and refetched on every run.

    Authenticity: the download is fetched over HTTPS from download.mozilla.org, and both the
    installer and the extracted firefox.exe must carry a valid Authenticode signature whose
    signer is Mozilla Corporation, or nothing is installed.

    The private copy gets a policies.json that turns off Firefox's own updater and default-
    browser check, so that it never schedules background update tasks or prompts. To update
    the engine, re-run this script with -Force (with Mosaic closed).

.PARAMETER Force
    Download and replace the Gecko engine even if one is already in place.

.PARAMETER BlockerOnly
    Only build the content blocker (what CI needs to build the app).

.PARAMETER SkipBlocker
    Do not build the content blocker, for instance on a PC without Node.js. The app then builds
    without it, and its shield says so.

.PARAMETER Architecture
    x64 or arm64. Default: the architecture of this machine.

.EXAMPLE
    .\bootstrap.ps1
.EXAMPLE
    .\bootstrap.ps1 -Force
.EXAMPLE
    .\bootstrap.ps1 -BlockerOnly
#>
[CmdletBinding()]
param(
    [switch]$Force,

    [switch]$BlockerOnly,

    [switch]$SkipBlocker,

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

# The content blocker: the same uBlock Origin release, checksum and esbuild as Scripts/bootstrap.sh,
# so both apps bundle the same engine.
$UboVersion = '1.77.2'
$UboSha256 = '470b6d6338ba3402a74d8412026871b0ec54ba45f29147270d932dd6a72e800e'
$EsbuildVersion = '0.28.2'
$NodeDownloadPage = 'https://nodejs.org/'
$WindowsRoot = Split-Path -Parent $PSScriptRoot

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

# ---- content blocker --------------------------------------------------------------------------

function Get-Ubo {
    # Unpacks the pinned uBlock Origin source into vendor\ublock-src (only what the bundle and
    # the licence need) and returns that folder. The archive is kept in vendor\downloads.
    param([Parameter(Mandatory = $true)] [string]$Vendor)

    $source = Join-Path $Vendor 'ublock-src'
    $stamp = Join-Path $source '.mosaic-version'
    if ((Test-Path -LiteralPath $stamp) -and ([System.IO.File]::ReadAllText($stamp).Trim() -eq $UboVersion)) {
        return $source
    }

    $downloads = Join-Path $Vendor 'downloads'
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    $archive = Join-Path $downloads "uBlock-$UboVersion.tar.gz"
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $UboSha256) {
        Write-Host "==> Downloading uBlock Origin $UboVersion source"
        Get-FileWithRetry -Uri "https://github.com/gorhill/uBlock/archive/refs/tags/$UboVersion.tar.gz" -Destination $archive
    }
    Write-Host '==> Verifying checksum'
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $UboSha256) {
        Remove-Item -LiteralPath $archive -Force
        throw "uBlock Origin's source archive has SHA-256 $actual, expected $UboSha256. Not using it."
    }

    if (Test-Path -LiteralPath $source) { Remove-Item -LiteralPath $source -Recurse -Force }
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    # Windows' own tar (bsdtar, part of Windows 10 1803 and later), named by its full path: a GNU
    # tar earlier on PATH (Git's) would take "C:" in the archive path for a remote host.
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tar)) { throw "Windows' tar.exe was not found at $tar (it ships with Windows 10 1803 and later)." }
    $top = "uBlock-$UboVersion"
    # Out-Null: this function's output is the folder it returns.
    & $tar -xzf $archive -C $source --strip-components 1 "$top/src/js" "$top/src/lib" "$top/LICENSE.txt" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "tar could not unpack $archive (exit code $LASTEXITCODE)." }
    [System.IO.File]::WriteAllText($stamp, $UboVersion)
    $source
}

function Test-ListContent {
    # The guard FilterListStore.LooksValid applies before the app saves a list: rejects an error
    # page or a truncated download. Keep the two in step.
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [switch]$SuffixList
    )
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 1024) { return $false }
    $head = [System.Text.Encoding]::UTF8.GetString($bytes, 0, [Math]::Min($bytes.Length, 4096))
    $lowered = $head.ToLowerInvariant()
    if ($lowered.Contains('<html') -or $lowered.Contains('<!doctype')) { return $false }
    if ($SuffixList) {
        return $head.Contains('publicsuffix.org') -or $head.Contains('===BEGIN ICANN DOMAINS===')
    }
    # Filter lists open with a header or comment line: "[Adblock Plus 2.0]", "! Title: ...", "# ...".
    $lines = @($head.Split([char[]]"`r`n", [System.StringSplitOptions]::RemoveEmptyEntries))
    if ($lines.Count -eq 0) { return $false }
    $first = $lines[0]
    $first.StartsWith('[') -or $first.StartsWith('!') -or $first.StartsWith('#')
}

function Save-FilterList {
    # Fetches one list into the snapshot. A failed or implausible download keeps the copy from an
    # earlier run, if there is one; without one there is no snapshot, which is an error.
    param(
        [Parameter(Mandatory = $true)] [string]$Directory,
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$Uri
    )
    $destination = Join-Path $Directory $Name
    $partial = "$destination.part"
    $problem = $null
    try {
        Get-FileWithRetry -Uri $Uri -Destination $partial
        if (Test-ListContent -Path $partial -SuffixList:($Name -eq 'public_suffix_list.dat')) {
            if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
            Move-Item -LiteralPath $partial -Destination $destination
            return
        }
        $problem = 'the download does not look like a filter list'
    }
    catch {
        $problem = $_.Exception.Message
    }
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    if (-not (Test-Path -LiteralPath $destination)) { throw "Could not fetch the filter list $Name ($problem)." }
    Write-Warning "Could not fetch $Name ($problem); keeping the copy from an earlier run."
}

function Build-Blocker {
    # Builds Mosaic's content-blocking engine from uBlock Origin's source, as fetch_blocker in
    # Scripts/bootstrap.sh does. The engine is uBO's own static network filtering code.
    # resources\blocker\engine.js is Mosaic's entry point for it (the counterpart of uBO's
    # platform/nodejs/index.js); esbuild bundles the two into one script that loads both in V8
    # (Chromium tabs) and in Gecko. One uBO module is substituted: see
    # resources\blocker\publicsuffix-static.js.
    $npx = Get-Command npx.cmd -ErrorAction SilentlyContinue
    if (-not $npx) {
        throw ("Node.js is needed to build the content blocker (its npx fetches the pinned esbuild), and it was not found. " +
            "Install the LTS release from $NodeDownloadPage and run this script again, or run it with -SkipBlocker to build Mosaic without the blocker.")
    }

    $vendor = Join-Path $WindowsRoot 'vendor'
    $destination = Join-Path $vendor 'ublock'
    $source = Get-Ubo -Vendor $vendor

    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    $nodeVersion = if ($node) { & $node.Source --version } else { 'unknown version' }
    Write-Host "==> Bundling the filtering engine (esbuild $EsbuildVersion, Node.js $nodeVersion)"
    $staged = Join-Path $destination 'src'
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force }
    New-Item -ItemType Directory -Path $staged, (Join-Path $destination 'lists') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'src\js'), (Join-Path $source 'src\lib') -Destination $staged -Recurse
    Copy-Item -LiteralPath (Join-Path $WindowsRoot 'resources\blocker\publicsuffix-static.js') -Destination (Join-Path $staged 'js\publicsuffix.js') -Force
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE.txt') -Destination (Join-Path $destination 'LICENSE-uBlock-Origin.txt') -Force

    # The command line of Scripts/bootstrap.sh, run from windows\ instead of the repository root,
    # less the banner: npx.cmd runs through cmd.exe, which cannot pass an argument containing
    # line breaks. The banner is added afterwards instead, which gives the same file as
    # --banner:js would. The embedded quotes are written \" for the C runtime's argument parser
    # behind cmd.exe; both Windows PowerShell and PowerShell 7 pass them to a .cmd file as written.
    $output = Join-Path $destination 'ublock-engine.js'
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    Push-Location -LiteralPath $WindowsRoot
    # npm reports what it fetches on stderr, which some PowerShell hosts turn into an error that
    # 'Stop' would make fatal; success is judged by the exit code instead.
    $ErrorActionPreference = 'Continue'
    try {
        & $npx.Source --yes "esbuild@$EsbuildVersion" 'resources/blocker/engine.js' --bundle --format=iife `
            --global-name=MosaicBlocker '--alias:ublock=./vendor/ublock/src' `
            ('--define:__UBO_VERSION__=\"{0}\"' -f $UboVersion) --target=es2022 --charset=utf8 `
            --log-level=warning '--outfile=vendor/ublock/ublock-engine.js'
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = 'Stop'
        Pop-Location
    }
    if ($exitCode -ne 0) { throw "esbuild failed with exit code $exitCode." }
    if (-not (Test-Path -LiteralPath $output)) { throw "esbuild reported success but wrote no $output." }

    $banner = @(
        "/* Mosaic content blocker. Contains uBlock Origin $UboVersion's network filtering engine,",
        ' * Copyright (C) 2014-present Raymond Hill and contributors, https://github.com/gorhill/uBlock',
        ' * Licensed under the GNU General Public License, version 3 or later; see LICENSE-uBlock-Origin.txt.',
        ' * Built by windows/scripts/bootstrap.ps1 from resources/blocker/engine.js. Do not edit. */'
    ) -join "`n"
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $bundle = [System.IO.File]::ReadAllText($output, $utf8)
    if (-not $bundle.StartsWith('var MosaicBlocker = ')) { throw "$output is not the bundle esbuild was asked for." }
    [System.IO.File]::WriteAllText($output, "$banner`n$bundle", $utf8)

    # A snapshot of uBO's default lists, shipped in the app so blocking works on first launch.
    # The app refreshes them itself afterwards. Keep in step with FilterListCatalog in
    # src\Mosaic.Core\Blocker\FilterLists.cs.
    Write-Host '==> Fetching filter lists'
    $lists = Join-Path $destination 'lists'
    $uassets = 'https://ublockorigin.github.io/uAssets'
    $snapshot = [ordered]@{
        'public_suffix_list.dat' = 'https://publicsuffix.org/list/public_suffix_list.dat'
        'ublock-filters.txt'     = "$uassets/filters/filters.txt"
        'ublock-badware.txt'     = "$uassets/filters/badware.txt"
        'ublock-privacy.txt'     = "$uassets/filters/privacy.txt"
        'ublock-unbreak.txt'     = "$uassets/filters/unbreak.txt"
        'ublock-quick-fixes.txt' = "$uassets/filters/quick-fixes.txt"
        'easylist.txt'           = "$uassets/thirdparties/easylist.txt"
        'easyprivacy.txt'        = "$uassets/thirdparties/easyprivacy.txt"
        'urlhaus-1.txt'          = 'https://malware-filter.gitlab.io/urlhaus-filter/urlhaus-filter-vivaldi-online.txt'
        'plowe-0.txt'            = 'https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=1&mimetype=plaintext'
    }
    foreach ($name in $snapshot.Keys) {
        Save-FilterList -Directory $lists -Name $name -Uri $snapshot[$name]
    }
    Write-Host "==> Content blocker ready: $destination"
}

# ---- main -----------------------------------------------------------------------------------

# Windows PowerShell on older .NET Framework defaults may not offer TLS 1.2.
[System.Net.ServicePointManager]::SecurityProtocol =
    [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12

if ($BlockerOnly -and $SkipBlocker) { throw '-BlockerOnly and -SkipBlocker cannot be used together.' }

if (-not $BlockerOnly) {
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
}

if (-not $SkipBlocker) { Build-Blocker }

Write-Host 'Done. Next: scripts\build.ps1'
