<#
.SYNOPSIS
    Builds Mosaic for Windows: runs the unit tests, then publishes the app self-contained into
    windows\publish\<rid>\. Windows counterpart of Scripts/build.sh.

.DESCRIPTION
    Needs only the .NET 10 SDK (https://dotnet.microsoft.com/download). The published folder
    contains Mosaic.exe with its own copy of the .NET runtime, so the PC that runs it does not
    need .NET installed. It does need the two engines; see bootstrap.ps1.

.PARAMETER Runtime
    One or both of win-x64 and win-arm64. Default: the one matching this machine.
    Either can be built on either kind of machine.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER Zip
    Also produce windows\publish\Mosaic-<version>-<rid>.zip for each runtime.

.PARAMETER SkipTests
    Do not run the unit tests first.

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Runtime win-x64, win-arm64 -Zip
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$Runtime,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Zip,

    [switch]$SkipTests
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$windowsRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $windowsRoot 'src\Mosaic.App\Mosaic.App.csproj'
$testProject = Join-Path $windowsRoot 'tests\Mosaic.Core.Tests\Mosaic.Core.Tests.csproj'
$publishRoot = Join-Path $windowsRoot 'publish'

function Invoke-DotNet {
    # Runs dotnet and turns a non-zero exit code into an error. (A failing native command
    # does not stop a PowerShell script by itself, whatever $ErrorActionPreference says.)
    param([Parameter(Mandatory = $true)] [string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Get-DefaultRuntime {
    $native = $env:PROCESSOR_ARCHITEW6432
    if (-not $native) { $native = $env:PROCESSOR_ARCHITECTURE }
    try {
        $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
        $native = (Get-ItemProperty -LiteralPath $key -Name PROCESSOR_ARCHITECTURE).PROCESSOR_ARCHITECTURE
    }
    catch { }
    if ($native -eq 'ARM64') { return 'win-arm64' }
    'win-x64'
}

function Get-MosaicVersion {
    # The single source of the version number is windows\Directory.Build.props.
    $props = New-Object System.Xml.XmlDocument
    $props.Load((Join-Path $windowsRoot 'Directory.Build.props'))
    $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $node -or -not $node.InnerText.Trim()) {
        throw 'No <Version> found in windows\Directory.Build.props.'
    }
    $node.InnerText.Trim()
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK (dotnet) was not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download and try again.'
}

if (-not $Runtime) { $Runtime = @(Get-DefaultRuntime) }
$Runtime = @($Runtime | Select-Object -Unique)
$version = Get-MosaicVersion

if (-not $SkipTests) {
    Write-Host '==> Running the unit tests'
    Invoke-DotNet @('test', $testProject, '--configuration', $Configuration)
}

foreach ($rid in $Runtime) {
    $output = Join-Path $publishRoot $rid
    Write-Host "==> Publishing Mosaic $version for $rid ($Configuration)"
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    Invoke-DotNet @(
        'publish', $appProject,
        '--configuration', $Configuration,
        '--runtime', $rid,
        '--self-contained', 'true',
        '--output', $output
    )

    # What the app cannot start without: the executable, and the agent it loads into Gecko.
    foreach ($required in @('Mosaic.exe', 'gecko\agent.sys.mjs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $required))) {
            throw "The published app is incomplete: $required is missing from $output."
        }
    }
    Write-Host "    $output"

    if ($Zip) {
        $archive = Join-Path $publishRoot "Mosaic-$version-$rid.zip"
        Write-Host "==> Packing $archive"
        if (Test-Path -LiteralPath $archive) {
            Remove-Item -LiteralPath $archive -Force
        }
        # Not Compress-Archive: it is slow on a tree this size and has a 2 GB limit.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $output, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    }
}

Write-Host 'Done.'
Write-Host "Run it with: $(Join-Path (Join-Path $publishRoot $Runtime[0]) 'Mosaic.exe')"
