<#
.SYNOPSIS
    Publishes Mosaic several times with different publish settings (ReadyToRun, composite
    ReadyToRun, TieredPGO, InvariantGlobalization, trimming) so that run-perf.ps1 can measure
    what each one is worth. Used by the "publish settings" job of windows-perf.yml.

.DESCRIPTION
    Each variant goes to windows\publish\variants\<name>\ and gets a variant.json with its MSBuild
    properties, publish time and size, or the error if it did not build. The content blocker is
    packaged when bootstrap.ps1 has built it, as in build.ps1.

    Variants (every one is self-contained, like the release build):
      jit             PublishReadyToRun=false: everything JIT-compiled at run time
      r2r             PublishReadyToRun=true: Mosaic's and the Windows App SDK's assemblies
                      precompiled (the .NET runtime's own assemblies already are)
      r2r-composite   ReadyToRun composite of the app and the framework together
      r2r-nopgo       r2r with TieredPGO=false (no instrumentation of hot methods at run time)
      r2r-invariant   r2r with InvariantGlobalization=true (no ICU; culture-aware formatting,
                      sorting and casing become invariant: measured, not recommended)
      r2r-trimmed     r2r with PublishTrimmed=true, TrimMode=partial (size; WinUI/CsWinRT and
                      ClearScript rely on reflection, so a trimmed build must be smoke-tested)

.PARAMETER Runtime
    win-x64 or win-arm64. Default: this machine's.

.PARAMETER Variant
    Which variants to publish. Default: all.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime,
    [ValidateSet('jit', 'r2r', 'r2r-composite', 'r2r-nopgo', 'r2r-invariant', 'r2r-trimmed')]
    [string[]]$Variant = @('jit', 'r2r', 'r2r-composite', 'r2r-nopgo', 'r2r-invariant', 'r2r-trimmed')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Import-Module (Join-Path $PSScriptRoot 'PerfHarness.psm1') -Force

$windowsRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$appProject = Join-Path $windowsRoot 'src\Mosaic.App\Mosaic.App.csproj'
$variantsRoot = Join-Path $windowsRoot 'publish\variants'
if (-not $Runtime) { $Runtime = 'win-' + (Get-NativeArchitecture) }

$definitions = [ordered]@{
    'jit'           = @('PublishReadyToRun=false')
    'r2r'           = @('PublishReadyToRun=true')
    'r2r-composite' = @('PublishReadyToRun=true', 'PublishReadyToRunComposite=true')
    'r2r-nopgo'     = @('PublishReadyToRun=true', 'TieredPGO=false')
    'r2r-invariant' = @('PublishReadyToRun=true', 'InvariantGlobalization=true')
    'r2r-trimmed'   = @('PublishReadyToRun=true', 'PublishTrimmed=true', 'TrimMode=partial', 'SuppressTrimAnalysisWarnings=true')
}

foreach ($name in $Variant) {
    $output = Join-Path $variantsRoot $name
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $properties = $definitions[$name]
    Write-Host "==> $name ($($properties -join ' '))"
    $arguments = @('publish', $appProject, '--configuration', 'Release', '--runtime', $Runtime, '--self-contained', 'true',
        '--output', $output, '-nologo', '-clp:ErrorsOnly;Summary')
    # A clean build per variant: settings given on the command line do not make an incremental
    # build regenerate everything they affect (runtimeconfig.json, the ReadyToRun images).
    foreach ($project in @('src\Mosaic.App', 'src\Mosaic.Core')) {
        foreach ($folder in @('bin', 'obj')) {
            $path = Join-Path $windowsRoot "$project\$folder"
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    }
    $arguments += @($properties | ForEach-Object { "-p:$_" })
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $variantsRoot "$name.log"
    & dotnet @arguments *> $log
    $code = $LASTEXITCODE
    $watch.Stop()
    $record = [ordered]@{ name = $name; runtime = $Runtime; properties = $properties; publishSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 0) }
    if ($code -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $output 'Mosaic.exe'))) {
        $record['error'] = "dotnet publish failed with exit code $code"
        $record['log'] = @(Get-Content -LiteralPath $log -Tail 40)
        Write-Host "    FAILED ($code); last lines of $log :"
        Get-Content -LiteralPath $log -Tail 40 | ForEach-Object { Write-Host "      $_" }
    }
    else {
        $app = Get-PerfAppInfo -AppPath (Join-Path $output 'Mosaic.exe')
        $record['sizeMB'] = $app.sizeMB
        $record['files'] = $app.files
        Write-Host "    $($app.sizeMB) MB in $($app.files) files, published in $($record.publishSeconds) s"
    }
    Write-PerfText (Join-Path $output 'variant.json') (ConvertTo-PerfJson $record)
}
