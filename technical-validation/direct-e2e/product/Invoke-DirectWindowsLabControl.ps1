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

function Read-Exact {
    param([Parameter(Mandatory)][IO.Stream]$Stream, [Parameter(Mandatory)][int]$Count)
    $buffer = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) { throw [IO.EndOfStreamException]::new('WINDOWS_LAB_CONTROL_PIPE_EOF') }
        $offset += $read
    }
    return $buffer
}

function Write-ControlResponse {
    param([Parameter(Mandatory)]$Value)
    $json = $Value | ConvertTo-Json -Depth 10 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt $maximumDocumentBytes) {
        throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_RESPONSE_TOO_LARGE')
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

function Get-PropertyValue {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Name)
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-ExactProperties {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string[]]$Expected)
    $actual = @($Value.PSObject.Properties.Name)
    return $actual.Count -eq $Expected.Count -and
        @($actual | Where-Object { $_ -cnotin $Expected }).Count -eq 0
}

function Assert-ProcessIdentity {
    param([Parameter(Mandatory)]$Identity)
    if (-not (Test-ExactProperties $Identity @('pid', 'start_time_utc', 'image_sha256')) -or
        (Get-PropertyValue $Identity 'pid') -isnot [long] -or
        [long](Get-PropertyValue $Identity 'pid') -le 0 -or
        (Get-PropertyValue $Identity 'start_time_utc') -isnot [string] -or
        (Get-PropertyValue $Identity 'image_sha256') -isnot [string] -or
        [string](Get-PropertyValue $Identity 'image_sha256') -notmatch '^[a-f0-9]{64}$') {
        throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_TARGET_INVALID')
    }
    $expectedStart = [DateTimeOffset]::ParseExact(
        [string](Get-PropertyValue $Identity 'start_time_utc'),
        'o',
        [Globalization.CultureInfo]::InvariantCulture
    ).UtcDateTime
    $process = Get-Process -Id ([int](Get-PropertyValue $Identity 'pid')) -ErrorAction Stop
    $actualPath = $process.MainModule.FileName
    $actualHash = (Get-FileHash -LiteralPath $actualPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualStart = $process.StartTime.ToUniversalTime()
    if ($actualHash -cne [string](Get-PropertyValue $Identity 'image_sha256') -or
        [Math]::Abs(($actualStart - $expectedStart).TotalMilliseconds) -gt 1000) {
        throw [InvalidOperationException]::new('WINDOWS_LAB_CONTROL_TARGET_CHANGED')
    }
}

$requestId = 'unknown'
try {
    $item = Get-Item -LiteralPath ([IO.Path]::GetFullPath($RequestPath))
    if ($item.Length -gt $maximumDocumentBytes) {
        throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_REQUEST_TOO_LARGE')
    }
    $requestText = Get-Content -LiteralPath $item.FullName -Raw -Encoding UTF8
    if ($requestText -match '(?i)"[^"\r\n]*(token|password|secret|authorization|cookie|credential|private_key)[^"\r\n]*"\s*:') {
        throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_SENSITIVE_FIELD')
    }
    $request = $requestText | ConvertFrom-Json -ErrorAction Stop
    if ($request -isnot [pscustomobject]) {
        throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_REQUEST_INVALID')
    }
    $candidateRequestId = Get-PropertyValue $request 'request_id'
    if ($candidateRequestId -is [string] -and $candidateRequestId -match '^[a-f0-9]{32}$') {
        $requestId = $candidateRequestId
    }
    $operationValue = Get-PropertyValue $request 'operation'
    $operation = if ($operationValue -is [string]) { $operationValue } else { '' }
    $baseProperties = @('schema_version', 'kind', 'request_id', 'run_id', 'scenario_id', 'operation')
    $sessionProperties = @('session_id', 'session_role', 'target_process')
    $expectedProperties = switch ($operation) {
        'probe' { $baseProperties }
        'cleanup_all' { $baseProperties }
        'resize_window' { $baseProperties + $sessionProperties + @('columns', 'rows') }
        default { $baseProperties + $sessionProperties }
    }
    $valid = $requestId -match '^[a-f0-9]{32}$' -and
        (Get-PropertyValue $request 'schema_version') -is [long] -and
        [long](Get-PropertyValue $request 'schema_version') -eq 1 -and
        (Get-PropertyValue $request 'kind') -is [string] -and
        [string](Get-PropertyValue $request 'kind') -ceq 'termlink.windows-lab-control-request' -and
        (Get-PropertyValue $request 'run_id') -is [string] -and
        [string](Get-PropertyValue $request 'run_id') -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -and
        (Get-PropertyValue $request 'scenario_id') -is [string] -and
        [string](Get-PropertyValue $request 'scenario_id') -match '^[a-z0-9_]{1,96}$' -and
        $operation -cin @('probe', 'start_long_output', 'resize_window', 'stop_long_output', 'cleanup_all') -and
        (Test-ExactProperties $request $expectedProperties)
    if (-not $valid) { throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_REQUEST_INVALID') }

    if ($operation -notin @('probe', 'cleanup_all')) {
        $sessionId = [Guid]::Empty
        $sessionIdValue = Get-PropertyValue $request 'session_id'
        $sessionRole = Get-PropertyValue $request 'session_role'
        if ($sessionIdValue -isnot [string] -or
            -not [Guid]::TryParseExact([string]$sessionIdValue, 'D', [ref]$sessionId) -or
            $sessionId -eq [Guid]::Empty -or $sessionRole -isnot [string] -or
            [string]$sessionRole -cnotin @('primary', 'late')) {
            throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_SESSION_INVALID')
        }
        Assert-ProcessIdentity (Get-PropertyValue $request 'target_process')
        if ($operation -ceq 'resize_window') {
            $columns = Get-PropertyValue $request 'columns'
            $rows = Get-PropertyValue $request 'rows'
            if ($columns -isnot [long] -or $rows -isnot [long] -or
                [long]$columns -notin 20..400 -or [long]$rows -notin 5..200) {
                throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_GEOMETRY_INVALID')
            }
        }
    }

    $payload = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 8 -Compress))
    try {
        $pipe = [IO.Pipes.NamedPipeClientStream]::new(
            '.', $ControlPipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::None
        )
        try {
            $pipe.Connect($TimeoutMilliseconds)
            $length = [BitConverter]::GetBytes([int]$payload.Length)
            $pipe.Write($length, 0, $length.Length)
            $pipe.Write($payload, 0, $payload.Length)
            $pipe.Flush()
            $responseLength = [BitConverter]::ToInt32((Read-Exact $pipe 4), 0)
            if ($responseLength -lt 2 -or $responseLength -gt $maximumDocumentBytes) {
                throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_RESPONSE_LENGTH_INVALID')
            }
            $bytes = Read-Exact $pipe $responseLength
            try { $response = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -ErrorAction Stop }
            finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        }
        finally { $pipe.Dispose() }
    }
    finally { [Array]::Clear($payload, 0, $payload.Length) }

    if ([int]$response.schema_version -ne 1 -or [string]$response.request_id -cne $requestId -or
        $response.ok -isnot [bool] -or
        [string]$response.status -notin @('ready', 'confirmed', 'cleaned', 'blocked', 'failed') -or
        $response.process_scoped -isnot [bool] -or $response.cleanup_guaranteed -isnot [bool] -or
        ($response.ok -and (-not [bool]$response.process_scoped -or
            -not [bool]$response.cleanup_guaranteed -or $response.confirmed -isnot [bool] -or
            -not [bool]$response.confirmed))) {
        throw [InvalidDataException]::new('WINDOWS_LAB_CONTROL_RESPONSE_INVALID')
    }
    Write-ControlResponse $response
}
catch {
    $reason = if ($_.Exception.Message -match '^[A-Z0-9_]{1,96}$') {
        $_.Exception.Message
    } elseif ($_.Exception -is [TimeoutException] -or $_.Exception -is [IO.IOException]) {
        'WINDOWS_LAB_CONTROL_UNAVAILABLE'
    } else {
        'WINDOWS_LAB_CONTROL_FAILED'
    }
    Write-ControlResponse ([pscustomobject][ordered]@{
        schema_version = 1
        request_id = $requestId
        ok = $false
        status = 'blocked'
        reason_code = $reason
        process_scoped = $false
        cleanup_guaranteed = $false
        confirmed = $false
    })
    exit 2
}
