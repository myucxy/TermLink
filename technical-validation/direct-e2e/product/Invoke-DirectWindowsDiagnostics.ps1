[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RequestPath,
    [Parameter(Mandatory)][string]$ResponsePath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,180}$')][string]$ControlPipeName,
    [ValidateRange(1000, 30000)][int]$TimeoutMilliseconds = 5000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$maximumDocumentBytes = 1024 * 1024

function Read-Exact {
    param([Parameter(Mandatory)][IO.Stream]$Stream, [Parameter(Mandatory)][int]$Count)
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) { throw [IO.EndOfStreamException]::new('WINDOWS_DIAGNOSTICS_PIPE_EOF') }
        $offset += $read
    }
    return $buffer
}

function Write-Response {
    param([Parameter(Mandatory)]$Value)
    $json = $Value | ConvertTo-Json -Depth 12 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt $maximumDocumentBytes) {
        throw [IO.InvalidDataException]::new('WINDOWS_DIAGNOSTICS_RESPONSE_TOO_LARGE')
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

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $Value -or $null -eq $Value.PSObject) { return $null }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

$requestId = 'unknown'
try {
    $item = Get-Item -LiteralPath ([IO.Path]::GetFullPath($RequestPath))
    if ($item.Length -gt 65536) { throw [IO.InvalidDataException]::new('WINDOWS_DIAGNOSTICS_REQUEST_TOO_LARGE') }
    $requestText = Get-Content -LiteralPath $item.FullName -Raw -Encoding UTF8
    if ($requestText -match '(?i)"[^"\r\n]*(token|password|secret|authorization|cookie|credential|private_key)[^"\r\n]*"\s*:') {
        throw [IO.InvalidDataException]::new('WINDOWS_DIAGNOSTICS_SENSITIVE_FIELD')
    }
    $request = $requestText | ConvertFrom-Json -ErrorAction Stop
    if ($request -isnot [pscustomobject]) {
        throw [IO.InvalidDataException]::new('WINDOWS_DIAGNOSTICS_REQUEST_INVALID')
    }
    $candidateRequestId = Get-JsonPropertyValue $request 'request_id'
    if ($candidateRequestId -is [string] -and $candidateRequestId -cmatch '^[a-f0-9]{32}$') {
        $requestId = $candidateRequestId
    }
    $operationValue = Get-JsonPropertyValue $request 'operation'
    $operation = if ($operationValue -is [string]) { $operationValue } else { '' }
    $baseProperties = @('schema_version', 'kind', 'request_id', 'run_id', 'scenario_id', 'operation')
    $expectedProperties = if ($operation -ceq 'inspect') {
        $baseProperties + @('session_id', 'session_role', 'peer_device_id')
    } else {
        $baseProperties
    }
    $actualProperties = @($request.PSObject.Properties.Name)
    $exactProperties = $actualProperties.Count -eq $expectedProperties.Count -and
        @($actualProperties | Where-Object { $_ -cnotin $expectedProperties }).Count -eq 0
    $selectorValid = $true
    if ($operation -ceq 'inspect') {
        $sessionIdValue = Get-JsonPropertyValue $request 'session_id'
        $sessionRoleValue = Get-JsonPropertyValue $request 'session_role'
        $peerDeviceIdValue = Get-JsonPropertyValue $request 'peer_device_id'
        $sessionId = [Guid]::Empty
        $peerDeviceId = [Guid]::Empty
        $selectorValid = $sessionIdValue -is [string] -and
            [Guid]::TryParseExact($sessionIdValue, 'D', [ref]$sessionId) -and
            $sessionId -ne [Guid]::Empty -and
            $sessionRoleValue -is [string] -and
            @('primary', 'late') -ccontains $sessionRoleValue -and
            $peerDeviceIdValue -is [string] -and
            [Guid]::TryParseExact($peerDeviceIdValue, 'D', [ref]$peerDeviceId) -and
            $peerDeviceId -ne [Guid]::Empty
    }
    if ($requestId -cnotmatch '^[a-f0-9]{32}$' -or
        (Get-JsonPropertyValue $request 'schema_version') -isnot [long] -or
        [long](Get-JsonPropertyValue $request 'schema_version') -ne 1 -or
        (Get-JsonPropertyValue $request 'kind') -isnot [string] -or
        [string](Get-JsonPropertyValue $request 'kind') -cne 'termlink.windows-direct-diagnostics-request' -or
        (Get-JsonPropertyValue $request 'run_id') -isnot [string] -or
        [string](Get-JsonPropertyValue $request 'run_id') -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or
        (Get-JsonPropertyValue $request 'scenario_id') -isnot [string] -or
        [string](Get-JsonPropertyValue $request 'scenario_id') -cnotmatch '^[a-z0-9_]{1,96}$' -or
        $operation -cnotin @('probe', 'inspect') -or -not $exactProperties -or -not $selectorValid) {
        throw [IO.InvalidDataException]::new('WINDOWS_DIAGNOSTICS_REQUEST_INVALID')
    }
    $payload = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 8 -Compress))
    try {
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
            $responseLength = [BitConverter]::ToInt32((Read-Exact $pipe 4), 0)
            if ($responseLength -lt 2 -or $responseLength -gt $maximumDocumentBytes) {
                throw [IO.InvalidDataException]::new('WINDOWS_DIAGNOSTICS_RESPONSE_LENGTH_INVALID')
            }
            $bytes = Read-Exact $pipe $responseLength
            try { $response = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -ErrorAction Stop }
            finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        }
        finally { $pipe.Dispose() }
    }
    finally { [Array]::Clear($payload, 0, $payload.Length) }

    if ([int]$response.schema_version -ne 1 -or [string]$response.request_id -cne $requestId -or
        $response.ok -isnot [bool] -or [string]$response.status -notin @('ready', 'observed', 'blocked', 'failed')) {
        throw [IO.InvalidDataException]::new('WINDOWS_DIAGNOSTICS_RESPONSE_INVALID')
    }
    Write-Response $response
}
catch {
    $reason = if ($_.Exception.Message -match '^[A-Z0-9_]{1,96}$') {
        $_.Exception.Message
    } elseif ($_.Exception -is [TimeoutException] -or $_.Exception -is [IO.IOException]) {
        'WINDOWS_DIAGNOSTICS_UNAVAILABLE'
    } else {
        'WINDOWS_DIAGNOSTICS_FAILED'
    }
    Write-Response ([pscustomobject][ordered]@{
        schema_version = 1
        request_id = $requestId
        ok = $false
        status = 'blocked'
        reason_code = $reason
    })
    exit 2
}
