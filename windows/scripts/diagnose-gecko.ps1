<#
.SYNOPSIS
    Shows what Firefox does when it is started the way Mosaic starts its Gecko engine.

.DESCRIPTION
    Launches firefox.exe with Mosaic's arguments (a fresh throw-away profile, a loopback
    debugging port, --wait-for-browser) and, for the given number of seconds, prints every
    change in:
      - the Firefox main processes on that profile (process ID <- parent process ID),
      - which process owns the debugging port,
      - whether the process that was launched has exited.
    Firefox's launcher process, a self-restart on first run, or a second browser process all
    show up here, which is what to look at when the engine reports the wrong process or finds
    its profile still in use.

    Everything it starts is stopped before it returns. Nothing outside the throw-away profile
    is touched.

.PARAMETER Firefox
    The firefox.exe to start. Default: %MOSAIC_GECKO_EXE%, then Program Files\Mozilla Firefox.

.PARAMETER Seconds
    How long to watch. Default 25.

.PARAMETER Headless
    Start Firefox with --headless, as the engine's integration tests do.

.EXAMPLE
    .\diagnose-gecko.ps1 -Headless
#>
[CmdletBinding()]
param(
    [string] $Firefox,
    [int] $Seconds = 25,
    [switch] $Headless
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (-not $Firefox) {
    $Firefox = if ($env:MOSAIC_GECKO_EXE) { $env:MOSAIC_GECKO_EXE } else { Join-Path $env:ProgramFiles 'Mozilla Firefox\firefox.exe' }
}
if (-not (Test-Path -LiteralPath $Firefox)) { throw "Firefox not found at $Firefox" }

$profileDir = Join-Path ([System.IO.Path]::GetTempPath()) ('mosaic-diagnose-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $profileDir | Out-Null

# A free loopback port, as the engine picks one.
$probe = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback), 0
$probe.Start()
$port = $probe.LocalEndpoint.Port
$probe.Stop()

$arguments = @('--profile', "`"$profileDir`"", '--no-remote', '--start-debugger-server', "$port", '--wait-for-browser')
if ($Headless) { $arguments += '--headless' }

Write-Host "Firefox:  $Firefox"
Write-Host "Version:  $((Get-Item -LiteralPath $Firefox).VersionInfo.ProductVersion)"
Write-Host "Profile:  $profileDir"
Write-Host "Port:     $port"
Write-Host "Headless: $([bool]$Headless)"

function Get-ProfileProcesses {
    # Main processes carry the profile path; content, GPU and utility processes are
    # started with -contentproc and are left out to keep the picture readable.
    Get-CimInstance Win32_Process -Filter "Name = 'firefox.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profileDir) -and -not $_.CommandLine.Contains('-contentproc') }
}

$launched = Start-Process -FilePath $Firefox -ArgumentList $arguments -PassThru
$null = $launched.Handle   # keeps ExitCode readable after the process is gone
Write-Host "Launched: process $($launched.Id)"
Write-Host ''

$watch = [System.Diagnostics.Stopwatch]::StartNew()
$last = ''
try {
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $main = @(Get-ProfileProcesses | ForEach-Object { "$($_.ProcessId)<-$($_.ParentProcessId)" })
        $owners = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique)
        $exited = if ($launched.HasExited) { "exited ($($launched.ExitCode))" } else { 'running' }
        $snapshot = "main: [$($main -join ' ')]  port owner: [$($owners -join ' ')]  launched: $exited"
        if ($snapshot -ne $last) {
            Write-Host ('{0,6:N2}s  {1}' -f $watch.Elapsed.TotalSeconds, $snapshot)
            $last = $snapshot
        }
        Start-Sleep -Milliseconds 150
    }
    Write-Host ''
    Write-Host "parent.lock present: $(Test-Path -LiteralPath (Join-Path $profileDir 'parent.lock'))"
}
finally {
    foreach ($process in @(Get-ProfileProcesses)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if (-not $launched.HasExited) { Stop-Process -Id $launched.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $profileDir -Recurse -Force -ErrorAction SilentlyContinue
}
