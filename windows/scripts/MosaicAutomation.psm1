# Client side of Mosaic's automation protocol (see windows/CONTRACT.md, "Automation").
# Shared by drive.ps1 and smoke.ps1. Windows PowerShell 5.1 compatible, no dependencies.
#
# A Mosaic started with --mosaic-automation listens on the named pipe \\.\pipe\mosaic-automation.
# One exchange per connection: the client writes one line of UTF-8 JSON
#     {"do": "<command>", "value": "<optional string>", "out": "<optional path>"}
# and reads one line of JSON back: {"ok": true}, {"ok": false, "error": "..."} or, for dump,
# {"ok": true, "state": {...}}.

Set-StrictMode -Version 2.0

$script:DefaultPipeName = 'mosaic-automation'

function Send-MosaicCommand {
    <#
    .SYNOPSIS
        Sends one automation command to a running Mosaic and returns the reply.
    .OUTPUTS
        A PSCustomObject with Raw (the reply line as received) and Reply (the parsed JSON).
        Throws if Mosaic cannot be reached, does not answer in time, or answers with something
        that is not JSON. A reply of {"ok": false} is returned, not thrown.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Do,

        [Parameter(Position = 1)]
        [string]$Value,

        [Parameter(Position = 2)]
        [string]$Out,

        # How long to wait for the pipe to accept the connection.
        [int]$ConnectTimeoutMilliseconds = 5000,

        # How long to wait for the reply once the command has been sent.
        [int]$ReplyTimeoutSeconds = 30,

        [string]$PipeName = $script:DefaultPipeName
    )

    $request = [ordered]@{ do = $Do }
    if (-not [string]::IsNullOrEmpty($Value)) { $request['value'] = $Value }
    if (-not [string]::IsNullOrEmpty($Out)) { $request['out'] = $Out }
    # -Compress keeps it on one line; line breaks inside strings are escaped by ConvertTo-Json.
    $json = $request | ConvertTo-Json -Compress

    $utf8 = New-Object System.Text.UTF8Encoding($false)   # no byte-order mark
    # Asynchronous, so that the wait for the reply can time out instead of hanging forever.
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        try {
            $pipe.Connect($ConnectTimeoutMilliseconds)
        }
        catch [System.TimeoutException] {
            throw "No Mosaic is listening on \\.\pipe\$PipeName. Start it with --mosaic-automation."
        }

        $bytes = $utf8.GetBytes($json + "`n")
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()

        $reader = New-Object System.IO.StreamReader($pipe, $utf8, $false, 4096, $true)
        $pending = $reader.ReadLineAsync()
        if (-not $pending.Wait($ReplyTimeoutSeconds * 1000)) {
            throw "Mosaic did not answer '$Do' within $ReplyTimeoutSeconds s."
        }
        $line = $pending.Result
        if ([string]::IsNullOrWhiteSpace($line)) {
            throw "Mosaic closed the connection without answering '$Do'."
        }
    }
    finally {
        $pipe.Dispose()
    }

    try {
        $reply = $line | ConvertFrom-Json
    }
    catch {
        throw "Mosaic's answer to '$Do' is not JSON: $line"
    }
    [pscustomobject]@{ Raw = $line; Reply = $reply }
}

function Get-MosaicProperty {
    <#
    .SYNOPSIS
        Reads a property of a parsed JSON object, or returns $null if it is absent.
        (Under strict mode a plain $object.name throws for a missing property.)
    #>
    param(
        [Parameter(Position = 0)] $Object,
        [Parameter(Mandatory = $true, Position = 1)] [string]$Name
    )
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

Export-ModuleMember -Function Send-MosaicCommand, Get-MosaicProperty
