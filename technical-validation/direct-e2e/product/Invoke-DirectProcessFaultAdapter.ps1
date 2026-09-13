[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RequestPath,
    [Parameter(Mandatory)][string]$ResponsePath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,180}$')][string]$ControlPipeName,
    [ValidateRange(1000, 30000)][int]$TimeoutMilliseconds = 5000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$maximumDocumentBytes = 65536

function Write-AdapterResponse {
    param([Parameter(Mandatory)]$Value)
    $json = $Value | ConvertTo-Json -Depth 8 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt $maximumDocumentBytes) {
        throw [InvalidDataException]::new('FAULT_ADAPTER_RESPONSE_TOO_LARGE')
    }
    $full = [IO.Path]::GetFullPath($ResponsePath)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $full)) | Out-Null
    $temporary = "$full.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $full, $true)
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function New-BlockedResponse {
    param([string]$RequestId, [string]$ReasonCode, [string]$FailureType = 'None')
    return [pscustomobject][ordered]@{
        schema_version = 1
        request_id = $RequestId
        ok = $false
        status = 'blocked'
        reason_code = $ReasonCode
        failure_type = $FailureType
        process_scoped = $false
        cleanup_guaranteed = $false
        lease_id_sha256 = $null
        observed_channels = @()
    }
}

function Read-Exact {
    param([Parameter(Mandatory)][IO.Stream]$Stream, [Parameter(Mandatory)][int]$Count)
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) { throw [IO.EndOfStreamException]::new('FAULT_ADAPTER_PIPE_EOF') }
        $offset += $read
    }
    return $buffer
}

$requestId = 'unknown'
try {
    $requestFile = Get-Item -LiteralPath ([IO.Path]::GetFullPath($RequestPath))
    if ($requestFile.Length -gt $maximumDocumentBytes) { throw [InvalidDataException]::new('FAULT_ADAPTER_REQUEST_TOO_LARGE') }
    $request = Get-Content -LiteralPath $requestFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    $requestId = [string]$request.request_id
    if ($requestId -notmatch '^[a-f0-9]{32}$' -or
        [int]$request.schema_version -ne 1 -or
        [string]$request.kind -cne 'termlink.direct-fault-adapter-request' -or
        [string]$request.run_id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or
        [string]$request.scenario_id -notmatch '^[a-z0-9_]{1,96}$' -or
        [string]$request.operation -notin @('probe', 'probe_topology', 'apply', 'restore', 'restore_all')) {
        throw [InvalidDataException]::new('FAULT_ADAPTER_REQUEST_INVALID')
    }
    $baseProperties = @('schema_version', 'kind', 'request_id', 'run_id', 'scenario_id', 'operation')
    $operation = [string]$request.operation
    $action = if ($null -ne $request.PSObject.Properties['action']) { [string]$request.action } else { '' }
    $expectedProperties = switch ($operation) {
        'probe' { $baseProperties }
        'probe_topology' { $baseProperties + 'topology' }
        'restore_all' { $baseProperties }
        default {
            if ($action -like '*one_peer*') {
                $baseProperties + @('action', 'target_process', 'target_peer_device_id', 'target_session_id')
            } else {
                $baseProperties + @('action', 'target_process')
            }
        }
    }
    $actualProperties = @($request.PSObject.Properties.Name)
    if ($actualProperties.Count -ne $expectedProperties.Count -or
        @($actualProperties | Where-Object { $_ -cnotin $expectedProperties }).Count -ne 0) {
        throw [InvalidDataException]::new('FAULT_ADAPTER_REQUEST_INVALID')
    }
    if ($action -like '*one_peer*') {
        $peer = [Guid]::Empty
        $session = [Guid]::Empty
        if (-not [Guid]::TryParseExact([string]$request.target_peer_device_id, 'D', [ref]$peer) -or
            $peer -eq [Guid]::Empty -or
            -not [Guid]::TryParseExact([string]$request.target_session_id, 'D', [ref]$session) -or
            $session -eq [Guid]::Empty) {
            throw [InvalidDataException]::new('FAULT_ADAPTER_PEER_SELECTOR_INVALID')
        }
    }
    $serializedRequest = $request | ConvertTo-Json -Depth 8 -Compress
    if ($serializedRequest -match '(?i)"[^"\r\n]*(token|password|secret|authorization|cookie|credential|private_key)[^"\r\n]*"\s*:') {
        throw [InvalidDataException]::new('FAULT_ADAPTER_SENSITIVE_FIELD')
    }

    if ([string]$request.operation -in @('apply', 'restore')) {
        $identity = $request.target_process
        $pidValue = [int]$identity.pid
        $process = Get-Process -Id $pidValue -ErrorAction Stop
        $path = $process.MainModule.FileName
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $actualStart = $process.StartTime.ToUniversalTime()
        $expectedStart = [DateTimeOffset]::ParseExact(
            [string]$identity.start_time_utc,
            'o',
            [Globalization.CultureInfo]::InvariantCulture
        ).UtcDateTime
        if ($actualHash -cne [string]$identity.image_sha256 -or
            [Math]::Abs(($actualStart - $expectedStart).TotalMilliseconds) -gt 1000) {
            throw [InvalidOperationException]::new('FAULT_TARGET_PROCESS_IDENTITY_CHANGED')
        }
    }

    $payload = [Text.Encoding]::UTF8.GetBytes($serializedRequest)
    try {
        if ($payload.Length -gt $maximumDocumentBytes) { throw [InvalidDataException]::new('FAULT_ADAPTER_REQUEST_TOO_LARGE') }
        $pipe = [IO.Pipes.NamedPipeClientStream]::new(
            '.',
            $ControlPipeName,
            [IO.Pipes.PipeDirection]::InOut,
            [IO.Pipes.PipeOptions]::None
        )
        try {
            $pipe.Connect($TimeoutMilliseconds)
            $length = [BitConverter]::GetBytes([int]$payload.Length)
            $pipe.Write($length, 0, $length.Length)
            $pipe.Write($payload, 0, $payload.Length)
            $pipe.Flush()
            $responseLengthBytes = Read-Exact $pipe 4
            $responseLength = [BitConverter]::ToInt32($responseLengthBytes, 0)
            if ($responseLength -lt 2 -or $responseLength -gt $maximumDocumentBytes) {
                throw [InvalidDataException]::new('FAULT_ADAPTER_RESPONSE_LENGTH_INVALID')
            }
            $responseBytes = Read-Exact $pipe $responseLength
            try { $response = [Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json -ErrorAction Stop }
            finally { [Array]::Clear($responseBytes, 0, $responseBytes.Length) }
        }
        finally { $pipe.Dispose() }
    }
    finally { [Array]::Clear($payload, 0, $payload.Length) }

    if ([int]$response.schema_version -ne 1 -or [string]$response.request_id -cne $requestId -or
        $response.ok -isnot [bool] -or [string]$response.status -notin @('ready', 'executed', 'restored', 'blocked', 'failed') -or
        $response.process_scoped -isnot [bool] -or $response.cleanup_guaranteed -isnot [bool]) {
        throw [InvalidDataException]::new('FAULT_ADAPTER_RESPONSE_INVALID')
    }
    if ([bool]$response.ok -and (-not [bool]$response.process_scoped -or -not [bool]$response.cleanup_guaranteed)) {
        throw [InvalidDataException]::new('FAULT_ADAPTER_SCOPE_NOT_PROVEN')
    }
    Write-AdapterResponse $response
}
catch {
    $reason = if ($_.Exception.Message -match '^[A-Z0-9_]{1,96}$') {
        $_.Exception.Message
    } elseif ($_.Exception -is [TimeoutException] -or $_.Exception -is [IO.IOException]) {
        'FAULT_PLATFORM_UNAVAILABLE'
    } else {
        'FAULT_ADAPTER_REQUEST_FAILED'
    }
    Write-AdapterResponse (New-BlockedResponse $requestId $reason $_.Exception.GetType().Name)
    exit 2
}
