[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AndroidApkPath,
    [Parameter(Mandatory)][string]$WindowsAgentPath,
    [Parameter(Mandatory)][string]$WindowsDiagnosticsContractPath,
    [Parameter(Mandatory)][string]$FaultAdapterContractPath,
    [Parameter(Mandatory)][string]$AndroidAdbPath,
    [Parameter(Mandatory)]
    [ValidateScript({
        $_.IsAbsoluteUri -and $_.Scheme -ceq 'https' -and $_.AbsolutePath -ceq '/' -and
        [string]::IsNullOrEmpty($_.UserInfo) -and -not $_.Query -and -not $_.Fragment
    })]
    [Uri]$AcceptanceOrigin,
    [Parameter(Mandatory)]
    [ValidateScript({
        $_.IsAbsoluteUri -and $_.Scheme -ceq 'https' -and
        [string]::IsNullOrEmpty($_.UserInfo) -and -not $_.Query -and -not $_.Fragment
    })]
    [Uri]$ServerAcceptanceMetadataUri,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$')][string]$PrimaryAndroidSerial,
    [ValidatePattern('^$|^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$')][string]$SecondaryAndroidSerial = '',
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunId,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][ValidateSet('PreflightOnly', 'FullMatrix')][string]$Mode,
    [string]$WindowsLabControlContractPath = '',
    [ValidateRange(5, 120)][int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failureCode = $null
$script:failureStage = $null
$script:stage = 'initialize'
$script:checks = [Collections.Generic.List[object]]::new()
$script:schemaPath = Join-Path $PSScriptRoot 'direct-product-e2e.evidence.schema.v1.json'
$script:matrixPath = Join-Path $PSScriptRoot 'direct-product-e2e.matrix.v1.json'
$script:androidContractEntry = 'assets/termlink-direct-product-e2e-contract-v1.json'
$script:requiredCapabilities = @(
    'direct_transport_shadow_v1',
    'direct_transport_shadow_multipair_v2',
    'direct_transport_data_v1',
    'direct_session_cutover_v1'
)
$script:requiredAndroidDiagnostics = @(
    'app_process_id', 'app_process_start_time', 'candidate_kinds', 'carrier',
    'contiguous_applied_sequence', 'digest_conflict_count', 'duplicate_apply_count',
    'duplicate_drop_count', 'durable_ack', 'fallback_reason', 'gap_count',
    'last_received_sequence', 'marker_digest', 'network_generation', 'path_epoch',
    'relay_generation', 'signalling_generation', 'transport_receipt_high_watermark',
    'viewport_digest'
)
$script:requiredWindowsDiagnostics = @(
    'agent_pid', 'agent_start_time', 'carrier', 'contiguous_applied_sequence',
    'digest_conflict_count', 'direct_frame_received_count', 'direct_frame_sent_count',
    'direct_receipt_count', 'duplicate_apply_count', 'duplicate_drop_count', 'durable_ack',
    'fallback_reason', 'gap_count', 'last_received_sequence', 'outbox_depth', 'path_epoch',
    'process_image_sha256', 'queue_depth', 'relay_generation', 'relay_replay_bytes',
    'relay_replay_count', 'session_host_pid', 'session_host_start_time',
    'session_host_image_sha256', 'signalling_generation', 'transport_receipt_high_watermark'
)
$script:requiredHookOperations = @(
    'probe', 'inspect_transport', 'send_text_marker', 'send_key', 'request_resize',
    'background', 'foreground', 'lock_screen', 'wake_screen', 'open_late_session',
    'start_long_output', 'stop_long_output', 'prepare_session', 'close_test_sessions'
)
$script:requiredFaultChannels = @('direct_quic', 'direct_udp', 'relay_wss', 'signalling_wss')

function Stop-DirectProductE2E {
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9_]{1,96}$')][string]$Code,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,64}$')][string]$Stage
    )
    $script:failureCode = $Code
    $script:failureStage = $Stage
    throw [InvalidOperationException]::new("$Code at $Stage")
}

function Assert-DirectProductE2E {
    param(
        [bool]$Condition,
        [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9_]{1,96}$')][string]$Code,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,64}$')][string]$Stage
    )
    if (-not $Condition) { Stop-DirectProductE2E -Code $Code -Stage $Stage }
}

function Add-PassedCheck {
    param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,96}$')][string]$Id)
    $script:checks.Add([pscustomobject][ordered]@{ id = $Id; result = 'passed' })
}

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9_]{1,96}$')][string]$Code,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,64}$')][string]$Stage
    )
    $resolved = [IO.Path]::GetFullPath($Path)
    Assert-DirectProductE2E (Test-Path -LiteralPath $resolved -PathType Leaf) $Code $Stage
    return $resolved
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextSha256 {
    param([Parameter(Mandatory)][string]$Value)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    try {
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Assert-RequiredProperties {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string[]]$Names,
        [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9_]{1,96}$')][string]$Code,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9_]{1,64}$')][string]$Stage
    )
    $available = @($Value.PSObject.Properties.Name)
    foreach ($name in $Names) {
        Assert-DirectProductE2E ($available -ccontains $name) $Code $Stage
    }
}

function Assert-NoSensitiveFields {
    param($Value)
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return }
    if ($Value -is [Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            Assert-DirectProductE2E ([string]$key -notmatch '(?i)(token|password|secret|authorization|cookie|credential|key_material|private_key)') `
                'SENSITIVE_CONTRACT_FIELD' $script:stage
            Assert-NoSensitiveFields $Value[$key]
        }
        return
    }
    if ($Value -is [Collections.IEnumerable]) {
        foreach ($item in $Value) { Assert-NoSensitiveFields $item }
        return
    }
    foreach ($property in $Value.PSObject.Properties) {
        Assert-DirectProductE2E ($property.Name -notmatch '(?i)(token|password|secret|authorization|cookie|credential|key_material|private_key)') `
            'SENSITIVE_CONTRACT_FIELD' $script:stage
        Assert-NoSensitiveFields $property.Value
    }
}

function Read-JsonFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9_]{1,96}$')][string]$Code,
        [ValidateRange(1024, 1048576)][int]$MaximumBytes = 65536
    )
    Assert-DirectProductE2E ((Get-Item -LiteralPath $Path).Length -le $MaximumBytes) $Code $script:stage
    try { $value = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
    catch { Stop-DirectProductE2E $Code $script:stage }
    Assert-NoSensitiveFields $value
    return $value
}

function Test-ExactSetContains {
    param([object[]]$Actual, [string[]]$Required)
    $normalized = @($Actual | ForEach-Object { [string]$_ })
    return @($normalized | Select-Object -Unique).Count -eq $normalized.Count -and
        @($Required | Where-Object { $normalized -cnotcontains $_ }).Count -eq 0
}

function Get-ApkContract {
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try { $archive = [IO.Compression.ZipFile]::OpenRead($Path) }
    catch { Stop-DirectProductE2E 'ANDROID_APK_INVALID' $script:stage }
    try {
        $entry = $archive.GetEntry($script:androidContractEntry)
        Assert-DirectProductE2E ($null -ne $entry) 'ANDROID_DIRECT_HOOK_CONTRACT_MISSING' $script:stage
        Assert-DirectProductE2E ($entry.Length -le 65536) 'ANDROID_DIRECT_HOOK_CONTRACT_INVALID' $script:stage
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.UTF8Encoding]::new($false), $true)
        try { $raw = $reader.ReadToEnd() } finally { $reader.Dispose() }
        try { $contract = $raw | ConvertFrom-Json -ErrorAction Stop }
        catch { Stop-DirectProductE2E 'ANDROID_DIRECT_HOOK_CONTRACT_INVALID' $script:stage }
    }
    finally { $archive.Dispose() }
    Assert-NoSensitiveFields $contract
    return $contract
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [ValidateRange(1, 120)][int]$Seconds = 30
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Assert-DirectProductE2E ($null -ne $process) 'NATIVE_PROCESS_START_FAILED' $script:stage
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Seconds * 1000)) {
            try { $process.Kill($true) } catch {}
            $process.WaitForExit(5000) | Out-Null
            return [pscustomobject]@{ ExitCode = -2; StdOut = ''; StdErr = '' }
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout.GetAwaiter().GetResult()
            StdErr = $stderr.GetAwaiter().GetResult()
        }
    }
    finally { $process.Dispose() }
}

function Invoke-Adb {
    param(
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    return Invoke-NativeCapture -FilePath $Adb -Arguments (@('-s', $Serial) + $Arguments) -Seconds $TimeoutSeconds
}

function Get-AndroidPowerState {
    param(
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial
    )
    $result = Invoke-Adb $Adb $Serial @('shell', 'dumpsys', 'power')
    if ($result.ExitCode -ne 0) { return 'unavailable' }
    if ($result.StdOut -match '(?im)^\s*mWakefulness(?:Raw)?=Awake\s*$') { return 'awake' }
    if ($result.StdOut -match '(?im)^\s*mWakefulness(?:Raw)?=(Asleep|Dozing)\s*$') { return 'asleep' }
    return 'unavailable'
}

function Test-AndroidDeviceControlAvailable {
    param(
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial
    )
    return (Get-AndroidPowerState $Adb $Serial) -ne 'unavailable'
}

function Invoke-AndroidDeviceAction {
    param(
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)][ValidateSet('lock_android', 'wake_android')][string]$Action
    )
    $keyCode = if ($Action -ceq 'lock_android') { 'KEYCODE_SLEEP' } else { 'KEYCODE_WAKEUP' }
    $expectedState = if ($Action -ceq 'lock_android') { 'asleep' } else { 'awake' }
    $result = Invoke-Adb $Adb $Serial @('shell', 'input', 'keyevent', $keyCode)
    if ($result.ExitCode -ne 0) {
        throw [InvalidOperationException]::new('ANDROID_DEVICE_CONTROL_FAILED')
    }
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Min(10, $TimeoutSeconds))
    do {
        if ((Get-AndroidPowerState $Adb $Serial) -ceq $expectedState) {
            return [pscustomobject][ordered]@{ ok = $true; status = 'confirmed'; action = $Action }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [InvalidOperationException]::new('ANDROID_DEVICE_TRANSITION_UNCONFIRMED')
}

function Invoke-AndroidProbe {
    param(
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)]$Contract,
        [Parameter(Mandatory)][string]$Apk
    )
    $state = Invoke-Adb $Adb $Serial @('get-state')
    Assert-DirectProductE2E ($state.ExitCode -eq 0 -and $state.StdOut.Trim() -ceq 'device') `
        'ANDROID_DEVICE_UNAVAILABLE' $script:stage
    $install = Invoke-Adb $Adb $Serial @('install', '-r', '-t', $Apk)
    Assert-DirectProductE2E ($install.ExitCode -eq 0 -and $install.StdOut -match '(?m)^Success\s*$') `
        'ANDROID_ACCEPTANCE_INSTALL_FAILED' $script:stage

    $packageName = [string]$Contract.application_id
    $receiverClass = [string]$Contract.receiver_class
    $responsePath = [string]$Contract.response_relative_path
    # Android 10+ may silently reject a background Activity launch attempted by
    # the receiver. Bring the installed acceptance app to the foreground from
    # the external device driver before asking the receiver for its bridge.
    $launch = Invoke-Adb $Adb $Serial @(
        'shell', 'monkey', '-p', $packageName, '-c', 'android.intent.category.LAUNCHER', '1'
    )
    Assert-DirectProductE2E ($launch.ExitCode -eq 0 -and
        ($launch.StdOut + $launch.StdErr) -match 'Events injected:\s*1') `
        'ANDROID_PRODUCT_ACTIVITY_UNAVAILABLE' $script:stage
    $requestId = [Guid]::NewGuid().ToString('N')
    $remove = Invoke-Adb $Adb $Serial @('shell', 'run-as', $packageName, 'rm', '-f', $responsePath)
    Assert-DirectProductE2E ($remove.ExitCode -eq 0) 'ANDROID_DIRECT_HOOK_UNAVAILABLE' $script:stage
    $broadcast = Invoke-Adb $Adb $Serial @(
        'shell', 'am', 'broadcast', '--async', '-f', '0x20',
        '-a', [string]$Contract.broadcast_action,
        '-n', "$packageName/$receiverClass",
        '--es', [string]$Contract.operation_extra, [string]$Contract.probe_operation,
        '--es', [string]$Contract.request_id_extra, $requestId,
        '--es', [string]$Contract.run_id_extra, $RunId
    )
    Assert-DirectProductE2E ($broadcast.ExitCode -eq 0) 'ANDROID_DIRECT_HOOK_UNAVAILABLE' $script:stage

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $response = Invoke-Adb $Adb $Serial @('exec-out', 'run-as', $packageName, 'cat', $responsePath)
        if ($response.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($response.StdOut)) {
            Assert-DirectProductE2E ([Text.Encoding]::UTF8.GetByteCount($response.StdOut) -le 1048576) `
                'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
            try { $document = $response.StdOut | ConvertFrom-Json -ErrorAction Stop }
            catch { $document = $null }
            if ($null -ne $document -and [string]$document.request_id -ceq $requestId) {
                Assert-NoSensitiveFields $document
                Assert-RequiredProperties $document @(
                    'schema_version', 'request_id', 'operation', 'ok', 'ready', 'failure_code',
                    'shadow_acceptance', 'data_plane_acceptance', 'diagnostics', 'operations'
                ) `
                    'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
                Assert-DirectProductE2E ([int]$document.schema_version -eq 1 -and
                    [string]$document.operation -ceq [string]$Contract.probe_operation -and
                    $document.ok -is [bool] -and [bool]$document.ok -and
                    $document.ready -is [bool] -and [bool]$document.ready -and
                    [string]::IsNullOrEmpty([string]$document.failure_code) -and
                    $document.shadow_acceptance -is [bool] -and [bool]$document.shadow_acceptance -and
                    $document.data_plane_acceptance -is [bool] -and [bool]$document.data_plane_acceptance) `
                    'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
                Assert-DirectProductE2E (Test-ExactSetContains @($document.diagnostics) $script:requiredAndroidDiagnostics) `
                    'ANDROID_DIRECT_DIAGNOSTICS_MISSING' $script:stage
                Assert-DirectProductE2E (Test-ExactSetContains @($document.operations) $script:requiredHookOperations) `
                    'ANDROID_DIRECT_OPERATIONS_MISSING' $script:stage
                return $document
            }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Stop-DirectProductE2E 'ANDROID_DIRECT_HOOK_TIMEOUT' $script:stage
}

function Invoke-AndroidOperation {
    param(
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)]$Contract,
        [Parameter(Mandatory)][ValidatePattern('^[a-z_]{1,64}$')][string]$Operation,
        [Parameter(Mandatory)]$Arguments
    )
    $packageName = [string]$Contract.application_id
    $responsePath = [string]$Contract.response_relative_path
    $requestId = [Guid]::NewGuid().ToString('N')
    $argumentsJson = $Arguments | ConvertTo-Json -Depth 6 -Compress
    Assert-DirectProductE2E ([Text.Encoding]::UTF8.GetByteCount($argumentsJson) -le 4096) `
        'ANDROID_DIRECT_HOOK_ARGUMENTS_INVALID' $script:stage
    $remove = Invoke-Adb $Adb $Serial @('shell', 'run-as', $packageName, 'rm', '-f', $responsePath)
    Assert-DirectProductE2E ($remove.ExitCode -eq 0) 'ANDROID_DIRECT_HOOK_UNAVAILABLE' $script:stage
    $broadcast = Invoke-Adb $Adb $Serial @(
        'shell', 'am', 'broadcast', '--async', '-f', '0x20',
        '-a', [string]$Contract.broadcast_action,
        '-n', "$packageName/$([string]$Contract.receiver_class)",
        '--es', [string]$Contract.operation_extra, $Operation,
        '--es', [string]$Contract.request_id_extra, $requestId,
        '--es', [string]$Contract.run_id_extra, $RunId,
        '--es', [string]$Contract.arguments_json_extra, $argumentsJson
    )
    Assert-DirectProductE2E ($broadcast.ExitCode -eq 0) 'ANDROID_DIRECT_HOOK_UNAVAILABLE' $script:stage
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $response = Invoke-Adb $Adb $Serial @('exec-out', 'run-as', $packageName, 'cat', $responsePath)
        if ($response.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($response.StdOut)) {
            Assert-DirectProductE2E ([Text.Encoding]::UTF8.GetByteCount($response.StdOut) -le 1048576) `
                'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
            try { $document = $response.StdOut | ConvertFrom-Json -ErrorAction Stop } catch { $document = $null }
            if ($null -ne $document -and [string]$document.request_id -ceq $requestId) {
                Assert-NoSensitiveFields $document
                Assert-RequiredProperties $document @('schema_version', 'request_id', 'operation', 'ok') `
                    'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
                Assert-DirectProductE2E ([int]$document.schema_version -eq 1 -and
                    [string]$document.operation -ceq $Operation -and $document.ok -is [bool]) `
                    'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
                return $document
            }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Stop-DirectProductE2E 'ANDROID_DIRECT_HOOK_TIMEOUT' $script:stage
}

function Invoke-JsonFileDriver {
    param(
        [Parameter(Mandatory)][string]$DriverPath,
        [Parameter(Mandatory)][string]$ControlPipeName,
        [Parameter(Mandatory)]$Request,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [ValidateRange(1024, 1048576)][int]$MaximumResponseBytes = 1048576
    )
    $requestPath = Join-Path $WorkingDirectory ("request-" + [Guid]::NewGuid().ToString('N') + '.json')
    $responsePath = Join-Path $WorkingDirectory ("response-" + [Guid]::NewGuid().ToString('N') + '.json')
    $requestJson = $Request | ConvertTo-Json -Depth 10 -Compress
    Assert-NoSensitiveFields $Request
    Assert-DirectProductE2E ([Text.Encoding]::UTF8.GetByteCount($requestJson) -le 65536) `
        'DRIVER_REQUEST_INVALID' $script:stage
    try {
        [IO.File]::WriteAllText($requestPath, $requestJson, [Text.UTF8Encoding]::new($false))
        $hostExecutable = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $result = Invoke-NativeCapture $hostExecutable @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $DriverPath,
            '-RequestPath', $requestPath, '-ResponsePath', $responsePath,
            '-ControlPipeName', $ControlPipeName,
            '-TimeoutMilliseconds', ([Math]::Min(30000, $TimeoutSeconds * 1000)).ToString()
        ) $TimeoutSeconds
        if (-not (Test-Path -LiteralPath $responsePath -PathType Leaf)) {
            return [pscustomobject]@{ ok = $false; status = 'blocked'; reason_code = 'DRIVER_RESPONSE_MISSING' }
        }
        $response = Read-JsonFile $responsePath 'DRIVER_RESPONSE_INVALID' $MaximumResponseBytes
        Assert-RequiredProperties $response @('schema_version', 'request_id', 'ok', 'status') `
            'DRIVER_RESPONSE_INVALID' $script:stage
        Assert-DirectProductE2E ([int]$response.schema_version -eq 1 -and
            [string]$response.request_id -ceq [string]$Request.request_id -and
            $response.ok -is [bool]) 'DRIVER_RESPONSE_INVALID' $script:stage
        return $response
    }
    finally {
        if (Test-Path -LiteralPath $requestPath) { Remove-Item -LiteralPath $requestPath -Force }
        if (Test-Path -LiteralPath $responsePath) { Remove-Item -LiteralPath $responsePath -Force }
    }
}

function Get-ServerAcceptanceMetadata {
    param([Parameter(Mandatory)][Uri]$Uri)
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    try {
        try { $response = $client.GetAsync($Uri).GetAwaiter().GetResult() }
        catch { Stop-DirectProductE2E 'SERVER_ACCEPTANCE_METADATA_UNAVAILABLE' $script:stage }
        try {
            Assert-DirectProductE2E ([int]$response.StatusCode -eq 200) `
                'SERVER_ACCEPTANCE_METADATA_UNAVAILABLE' $script:stage
            $mediaType = [string]$response.Content.Headers.ContentType.MediaType
            Assert-DirectProductE2E ($mediaType -ceq 'application/json') `
                'SERVER_ACCEPTANCE_METADATA_INVALID' $script:stage
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            Assert-DirectProductE2E ($bytes.Length -le 65536) 'SERVER_ACCEPTANCE_METADATA_INVALID' $script:stage
            try { $metadata = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -ErrorAction Stop }
            catch { Stop-DirectProductE2E 'SERVER_ACCEPTANCE_METADATA_INVALID' $script:stage }
            finally { if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) } }
        }
        finally { $response.Dispose() }
    }
    finally { $client.Dispose(); $handler.Dispose() }
    Assert-NoSensitiveFields $metadata
    return $metadata
}

function New-ScenarioEvidence {
    param([Parameter(Mandatory)]$Scenario)
    return [pscustomobject][ordered]@{
        id = [string]$Scenario.id
        executed = $false
        status = 'not_run'
        reason_code = $null
        expected_carrier_sequence = @($Scenario.expected_carriers | ForEach-Object { [string]$_ })
        carrier_sequence = @()
        fault_actions = @($Scenario.required_fault_actions | ForEach-Object { [string]$_ })
        fault_actions_executed = @()
        assertions = @($Scenario.required_assertions | ForEach-Object {
            [pscustomobject][ordered]@{ id = [string]$_; result = 'not_run' }
        })
        samples = @()
    }
}

function ConvertTo-SessionId {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9_]{1,96}$')][string]$FailureCode
    )
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParseExact($Value, 'D', [ref]$parsed) -or $parsed -eq [Guid]::Empty) {
        Stop-DirectProductE2E $FailureCode $script:stage
    }
    return $parsed.ToString('D')
}

function New-WindowsDiagnosticsRequest {
    param(
        [Parameter(Mandatory)][string]$ScenarioId,
        [Parameter(Mandatory)][ValidateSet('probe', 'inspect')][string]$Operation,
        [string]$SessionId = '',
        [ValidateSet('primary', 'late')][string]$SessionRole = 'primary',
        [string]$PeerDeviceId = ''
    )
    $request = [ordered]@{
        schema_version = 1
        kind = 'termlink.windows-direct-diagnostics-request'
        request_id = [Guid]::NewGuid().ToString('N')
        run_id = $RunId
        scenario_id = $ScenarioId
        operation = $Operation
    }
    if ($Operation -ceq 'inspect') {
        $request.session_id = ConvertTo-SessionId $SessionId 'WINDOWS_DIAGNOSTICS_SESSION_INVALID'
        $request.session_role = $SessionRole
        $request.peer_device_id = ConvertTo-SessionId $PeerDeviceId 'WINDOWS_DIAGNOSTICS_PEER_INVALID'
    } elseif (-not [string]::IsNullOrEmpty($SessionId) -or -not [string]::IsNullOrEmpty($PeerDeviceId)) {
        Stop-DirectProductE2E 'WINDOWS_DIAGNOSTICS_SESSION_INVALID' $script:stage
    }
    return [pscustomobject]$request
}

function New-FaultRequest {
    param(
        [Parameter(Mandatory)][string]$ScenarioId,
        [Parameter(Mandatory)][ValidateSet('probe', 'probe_topology', 'apply', 'restore', 'restore_all')][string]$Operation,
        [string]$Action = '',
        $TargetProcess = $null,
        [string]$Topology = ''
    )
    $request = [ordered]@{
        schema_version = 1
        kind = 'termlink.direct-fault-adapter-request'
        request_id = [Guid]::NewGuid().ToString('N')
        run_id = $RunId
        scenario_id = $ScenarioId
        operation = $Operation
    }
    if (-not [string]::IsNullOrEmpty($Action)) { $request.action = $Action }
    if ($null -ne $TargetProcess) { $request.target_process = $TargetProcess }
    if (-not [string]::IsNullOrEmpty($Topology)) { $request.topology = $Topology }
    return [pscustomobject]$request
}

function New-WindowsLabControlRequest {
    param(
        [Parameter(Mandatory)][string]$ScenarioId,
        [Parameter(Mandatory)]
        [ValidateSet('probe', 'start_long_output', 'resize_window', 'stop_long_output', 'cleanup_all')]
        [string]$Operation,
        [string]$SessionId = '',
        [ValidateSet('primary', 'late')][string]$SessionRole = 'primary',
        $TargetProcess = $null,
        [int]$Columns = 0,
        [int]$Rows = 0
    )
    $request = [ordered]@{
        schema_version = 1
        kind = 'termlink.windows-lab-control-request'
        request_id = [Guid]::NewGuid().ToString('N')
        run_id = $RunId
        scenario_id = $ScenarioId
        operation = $Operation
    }
    if ($Operation -notin @('probe', 'cleanup_all')) {
        $request.session_id = ConvertTo-SessionId $SessionId 'WINDOWS_LAB_CONTROL_SESSION_INVALID'
        $request.session_role = $SessionRole
        Assert-DirectProductE2E ($null -ne $TargetProcess) 'WINDOWS_LAB_CONTROL_TARGET_INVALID' $script:stage
        $request.target_process = $TargetProcess
    }
    if ($Operation -ceq 'resize_window') {
        Assert-DirectProductE2E ($Columns -in 20..400 -and $Rows -in 5..200) `
            'WINDOWS_LAB_CONTROL_GEOMETRY_INVALID' $script:stage
        $request.columns = $Columns
        $request.rows = $Rows
    }
    return [pscustomobject]$request
}

function Invoke-WindowsLabControlOperation {
    param(
        [Parameter(Mandatory)][string]$Driver,
        [Parameter(Mandatory)][string]$Pipe,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$ScenarioId,
        [Parameter(Mandatory)]
        [ValidateSet('probe', 'start_long_output', 'resize_window', 'stop_long_output', 'cleanup_all')]
        [string]$Operation,
        [string]$SessionId = '',
        [ValidateSet('primary', 'late')][string]$SessionRole = 'primary',
        $TargetProcess = $null,
        [int]$Columns = 0,
        [int]$Rows = 0
    )
    $request = New-WindowsLabControlRequest $ScenarioId $Operation $SessionId $SessionRole `
        $TargetProcess $Columns $Rows
    $response = Invoke-JsonFileDriver $Driver $Pipe $request $WorkingDirectory 65536
    if (-not [bool]$response.ok) { return $response }
    Assert-RequiredProperties $response @('confirmed', 'process_scoped', 'cleanup_guaranteed') `
        'WINDOWS_LAB_CONTROL_RESPONSE_INVALID' $script:stage
    Assert-DirectProductE2E ($response.confirmed -is [bool] -and [bool]$response.confirmed -and
        $response.process_scoped -is [bool] -and [bool]$response.process_scoped -and
        $response.cleanup_guaranteed -is [bool] -and [bool]$response.cleanup_guaranteed) `
        'WINDOWS_LAB_CONTROL_RESPONSE_INVALID' $script:stage
    return $response
}

function Get-WindowsObservation {
    param(
        [Parameter(Mandatory)][string]$Driver,
        [Parameter(Mandatory)][string]$Pipe,
        [Parameter(Mandatory)][string]$ScenarioId,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [ValidateSet('probe', 'inspect')][string]$Operation = 'inspect',
        [string]$SessionId = '',
        [ValidateSet('primary', 'late')][string]$SessionRole = 'primary',
        [string]$PeerDeviceId = ''
    )
    $request = New-WindowsDiagnosticsRequest $ScenarioId $Operation $SessionId $SessionRole $PeerDeviceId
    $response = Invoke-JsonFileDriver $Driver $Pipe $request $WorkingDirectory
    if (-not [bool]$response.ok) { return $response }
    Assert-RequiredProperties $response @('diagnostics') 'WINDOWS_DIAGNOSTICS_RESPONSE_INVALID' $script:stage
    Assert-RequiredProperties $response.diagnostics $script:requiredWindowsDiagnostics `
        'WINDOWS_DIAGNOSTICS_RESPONSE_INVALID' $script:stage
    return $response
}

function ConvertTo-ProcessIdentity {
    param(
        [Parameter(Mandatory)]$Diagnostics,
        [Parameter(Mandatory)][string]$PidName,
        [Parameter(Mandatory)][string]$StartName,
        [Parameter(Mandatory)][string]$HashName
    )
    return [pscustomobject][ordered]@{
        pid = [int]$Diagnostics.$PidName
        start_time_utc = ([DateTimeOffset]::Parse([string]$Diagnostics.$StartName)).UtcDateTime.ToString('o')
        image_sha256 = [string]$Diagnostics.$HashName
    }
}

function ConvertTo-ScenarioSample {
    param(
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][ValidateSet('primary_android', 'secondary_android')][string]$DeviceRole,
        [Parameter(Mandatory)][ValidateSet('primary', 'late')][string]$SessionRole,
        [Parameter(Mandatory)]$AndroidResponse,
        [Parameter(Mandatory)]$WindowsResponse
    )
    Assert-RequiredProperties $AndroidResponse @('diagnostics') 'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
    $android = $AndroidResponse.diagnostics
    $windows = $WindowsResponse.diagnostics
    Assert-RequiredProperties $android $script:requiredAndroidDiagnostics 'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
    $carrier = ([string]$android.carrier).ToLowerInvariant()
    Assert-DirectProductE2E ($carrier -in @('relay', 'negotiating', 'lan_direct', 'nat_direct') -and
        ([string]$windows.carrier).ToLowerInvariant() -ceq $carrier) 'CARRIER_OBSERVATION_CONFLICT' $script:stage
    $candidateKinds = @($android.candidate_kinds | ForEach-Object { ([string]$_).ToLowerInvariant() })
    Assert-DirectProductE2E (@($candidateKinds | Where-Object { $_ -notmatch '^[a-z0-9_]{1,64}$' }).Count -eq 0 -and
        @($candidateKinds | Select-Object -Unique).Count -eq $candidateKinds.Count) `
        'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
    return [pscustomobject][ordered]@{
        phase = $Phase
        device_role = $DeviceRole
        session_role = $SessionRole
        observed_utc = [DateTime]::UtcNow.ToString('o')
        carrier = $carrier
        candidate_kinds = @($candidateKinds | Sort-Object)
        fallback_reason = [string]$windows.fallback_reason
        path_epoch = [long]$windows.path_epoch
        relay_generation = [long]$windows.relay_generation
        signalling_generation = [long]$windows.signalling_generation
        network_generation = [long]$android.network_generation
        app_process = [pscustomobject][ordered]@{
            pid = [int]$android.app_process_id
            start_time_utc = ([DateTimeOffset]::Parse([string]$android.app_process_start_time)).UtcDateTime.ToString('o')
        }
        agent_process = ConvertTo-ProcessIdentity $windows 'agent_pid' 'agent_start_time' 'process_image_sha256'
        session_host_process = ConvertTo-ProcessIdentity $windows 'session_host_pid' 'session_host_start_time' 'session_host_image_sha256'
        last_received_sequence = [long]$android.last_received_sequence
        contiguous_applied_sequence = [long]$android.contiguous_applied_sequence
        durable_ack = [long]$windows.durable_ack
        transport_receipt_high_watermark = [long]$windows.transport_receipt_high_watermark
        gap_count = [long]$android.gap_count
        duplicate_drop_count = [long]$android.duplicate_drop_count
        duplicate_apply_count = [long]$android.duplicate_apply_count
        digest_conflict_count = [long]$android.digest_conflict_count
        marker_digest = [string]$android.marker_digest
        viewport_digest = [string]$android.viewport_digest
        direct_frame_received_count = [long]$windows.direct_frame_received_count
        direct_frame_sent_count = [long]$windows.direct_frame_sent_count
        direct_receipt_count = [long]$windows.direct_receipt_count
        outbox_depth = [long]$windows.outbox_depth
        queue_depth = [long]$windows.queue_depth
        relay_replay_bytes = [long]$windows.relay_replay_bytes
        relay_replay_count = [long]$windows.relay_replay_count
    }
}

function Add-ObservedSample {
    param(
        [Parameter(Mandatory)]$ScenarioEvidence,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)]$AndroidContract,
        [Parameter(Mandatory)][string]$WindowsDriver,
        [Parameter(Mandatory)][string]$WindowsPipe,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][ValidateSet('primary_android', 'secondary_android')][string]$DeviceRole,
        [Parameter(Mandatory)][string]$SessionId,
        [Parameter(Mandatory)][ValidateSet('primary', 'late')][string]$SessionRole,
        [Parameter(Mandatory)][string]$PeerDeviceId
    )
    $arguments = [pscustomobject][ordered]@{
        scenario_id = [string]$ScenarioEvidence.id
        phase = $Phase
        session_role = $SessionRole
    }
    $androidResponse = Invoke-AndroidOperation $Adb $Serial $AndroidContract 'inspect_transport' $arguments
    if (-not [bool]$androidResponse.ok) { return $androidResponse }
    $windowsResponse = Get-WindowsObservation $WindowsDriver $WindowsPipe ([string]$ScenarioEvidence.id) `
        $WorkingDirectory 'inspect' $SessionId $SessionRole $PeerDeviceId
    if (-not [bool]$windowsResponse.ok) { return $windowsResponse }
    $sample = ConvertTo-ScenarioSample $Phase $DeviceRole $SessionRole $androidResponse $windowsResponse
    $ScenarioEvidence.samples = @($ScenarioEvidence.samples) + $sample
    $ScenarioEvidence.carrier_sequence = @($ScenarioEvidence.carrier_sequence) + [string]$sample.carrier
    return [pscustomobject]@{ ok = $true; status = 'observed' }
}

function Add-RequiredObservedSample {
    param(
        [Parameter(Mandatory)]$ScenarioEvidence,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)]$AndroidContract,
        [Parameter(Mandatory)][string]$WindowsDriver,
        [Parameter(Mandatory)][string]$WindowsPipe,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][ValidateSet('primary_android', 'secondary_android')][string]$DeviceRole,
        [Parameter(Mandatory)][string]$SessionId,
        [Parameter(Mandatory)][ValidateSet('primary', 'late')][string]$SessionRole,
        [Parameter(Mandatory)][string]$PeerDeviceId
    )
    $result = Add-ObservedSample $ScenarioEvidence $Phase $Adb $Serial $AndroidContract `
        $WindowsDriver $WindowsPipe $WorkingDirectory $DeviceRole $SessionId $SessionRole $PeerDeviceId
    if (-not [bool]$result.ok) {
        $reason = [string]$result.reason_code
        if ($reason -notmatch '^[A-Z0-9_]{1,96}$') { $reason = 'DIAGNOSTIC_OBSERVATION_BLOCKED' }
        throw [InvalidOperationException]::new($reason)
    }
}

function Get-ProcessIdentityKey {
    param([Parameter(Mandatory)]$Identity)
    return "$([int]$Identity.pid)|$([string]$Identity.start_time_utc)|$([string]$Identity.image_sha256)"
}

function Test-GroupedIdentityStable {
    param(
        [Parameter(Mandatory)][object[]]$Samples,
        [Parameter(Mandatory)][ValidateSet('app_process', 'agent_process', 'session_host_process')]
        [string]$Property,
        [Parameter(Mandatory)][ValidateSet('all', 'device', 'session')][string]$Grouping
    )
    if ($Grouping -ceq 'all') {
        $keys = @($Samples | ForEach-Object {
            $identity = $_.$Property
            if ($Property -ceq 'app_process') {
                "$([int]$identity.pid)|$([string]$identity.start_time_utc)"
            } else {
                Get-ProcessIdentityKey $identity
            }
        } | Select-Object -Unique)
        return $keys.Count -eq 1
    }
    $groups = if ($Grouping -ceq 'device') {
        @($Samples | Group-Object device_role)
    } else {
        @($Samples | Group-Object { "$($_.device_role)|$($_.session_role)" })
    }
    foreach ($group in $groups) {
        $keys = @($group.Group | ForEach-Object {
            $identity = $_.$Property
            if ($Property -ceq 'app_process') {
                "$([int]$identity.pid)|$([string]$identity.start_time_utc)"
            } else {
                Get-ProcessIdentityKey $identity
            }
        } | Select-Object -Unique)
        if ($keys.Count -ne 1) { return $false }
    }
    return $groups.Count -gt 0
}

function Test-MonotonicBySession {
    param([Parameter(Mandatory)][object[]]$Samples, [Parameter(Mandatory)][string]$Property)
    foreach ($group in @($Samples | Group-Object { "$($_.device_role)|$($_.session_role)" })) {
        $previous = [long]-1
        foreach ($sample in @($group.Group | Sort-Object { [DateTimeOffset]::Parse([string]$_.observed_utc) })) {
            $current = [long]$sample.$Property
            if ($current -lt $previous) { return $false }
            $previous = $current
        }
    }
    return $true
}

function Complete-ScenarioAssertions {
    param(
        [Parameter(Mandatory)]$ScenarioEvidence,
        [Parameter(Mandatory)][hashtable]$Proofs
    )
    $samples = @($ScenarioEvidence.samples)
    $agentStable = Test-GroupedIdentityStable $samples 'agent_process' 'all'
    $sessionHostStable = Test-GroupedIdentityStable $samples 'session_host_process' 'session'
    $appStable = Test-GroupedIdentityStable $samples 'app_process' 'device'
    $noHistoryGap = @($samples | Where-Object {
        [long]$_.gap_count -ne 0 -or [long]$_.contiguous_applied_sequence -gt [long]$_.last_received_sequence
    }).Count -eq 0
    $durableAckBounded = @($samples | Where-Object {
        [long]$_.durable_ack -gt [long]$_.contiguous_applied_sequence -or
        [long]$_.durable_ack -gt [long]$_.transport_receipt_high_watermark
    }).Count -eq 0 -and (Test-MonotonicBySession $samples 'durable_ack')
    $primarySamples = @($samples | Where-Object device_role -ceq 'primary_android')
    $secondarySamples = @($samples | Where-Object device_role -ceq 'secondary_android')
    $lateSamples = @($primarySamples | Where-Object session_role -ceq 'late' |
        Sort-Object { [DateTimeOffset]::Parse([string]$_.observed_utc) })
    $primarySessionSamples = @($primarySamples | Where-Object session_role -ceq 'primary' |
        Sort-Object { [DateTimeOffset]::Parse([string]$_.observed_utc) })
    $unsupported = $false
    $failed = $false

    foreach ($assertion in @($ScenarioEvidence.assertions)) {
        $value = switch ([string]$assertion.id) {
            'agent_process_unchanged' { $agentStable; break }
            'session_host_process_unchanged' { $sessionHostStable; break }
            'windows_processes_unchanged' { $agentStable -and $sessionHostStable; break }
            'session_resumes_without_recreate' { $agentStable -and $sessionHostStable -and $appStable; break }
            'relay_generation_unchanged' {
                @($samples.relay_generation | Select-Object -Unique).Count -eq 1; break
            }
            'relay_generation_increment_once' {
                ([long]($samples.relay_generation | Measure-Object -Maximum).Maximum -
                    [long]($samples.relay_generation | Measure-Object -Minimum).Minimum) -eq 1; break
            }
            'no_history_gap' { $noHistoryGap; break }
            'no_duplicate_apply' {
                @($samples | Where-Object { [long]$_.duplicate_apply_count -ne 0 }).Count -eq 0; break
            }
            'no_digest_conflict' {
                @($samples | Where-Object { [long]$_.digest_conflict_count -ne 0 }).Count -eq 0; break
            }
            'durable_ack_bounded' { $durableAckBounded; break }
            'srflx_candidate_observed' {
                @($samples.candidate_kinds | Where-Object { $_ -in @('srflx', 'server_reflexive') }).Count -gt 0; break
            }
            'bounded_negotiation_timeout' {
                $duration = if ($samples.Count -gt 1) {
                    ([DateTimeOffset]::Parse([string]$samples[-1].observed_utc) -
                        [DateTimeOffset]::Parse([string]$samples[0].observed_utc)).TotalSeconds
                } else { [double]::PositiveInfinity }
                $duration -le $TimeoutSeconds -and [string]$samples[-1].carrier -ceq 'relay'; break
            }
            'relay_remains_usable' { [bool]$Proofs.relay_marker_confirmed; break }
            'signalling_failure_isolated' {
                $agentStable -and $sessionHostStable -and $noHistoryGap -and [bool]$Proofs.relay_marker_confirmed; break
            }
            'relay_failure_isolated' {
                $agentStable -and $sessionHostStable -and [string]$samples[-1].carrier -ceq 'relay'; break
            }
            'outbox_converges_after_restore' { [long]$samples[-1].outbox_depth -eq 0; break }
            'network_generation_advances' {
                @($primarySamples.network_generation | Select-Object -Unique).Count -gt 1; break
            }
            'path_epoch_retires' {
                @($primarySamples.path_epoch | Select-Object -Unique).Count -gt 1; break
            }
            'cursor_contiguous_after_wake' {
                [long]$samples[-1].last_received_sequence -eq [long]$samples[-1].contiguous_applied_sequence -and
                    [long]$samples[-1].gap_count -eq 0; break
            }
            'at_least_three_resizes' { [int]$Proofs.confirmed_resize_count -ge 3; break }
            'final_geometry_correct' { [bool]$Proofs.final_geometry_confirmed; break }
            'peer_epochs_independent' {
                @($primarySamples.path_epoch | Select-Object -Unique).Count -gt 1 -and
                    @($secondarySamples.path_epoch | Select-Object -Unique).Count -eq 1; break
            }
            'healthy_peer_not_retired' {
                $secondarySamples.Count -gt 1 -and
                    @($secondarySamples | Where-Object carrier -notin @('lan_direct', 'nat_direct')).Count -eq 0 -and
                    @($secondarySamples.path_epoch | Select-Object -Unique).Count -eq 1; break
            }
            'slow_peer_does_not_block_other' {
                @($secondarySamples.marker_digest | Select-Object -Unique).Count -gt 1 -and
                    (Test-MonotonicBySession $secondarySamples 'contiguous_applied_sequence'); break
            }
            'existing_session_remains_direct' {
                $primarySessionSamples.Count -gt 1 -and
                    [string]$primarySessionSamples[-1].carrier -in @('lan_direct', 'nat_direct'); break
            }
            'late_session_relay_until_own_barrier' {
                $firstLateDirect = @($lateSamples | Where-Object carrier -in @('lan_direct', 'nat_direct') |
                    Select-Object -First 1)
                $firstLateDirect.Count -eq 1 -and
                    @($lateSamples | Where-Object {
                        [DateTimeOffset]::Parse([string]$_.observed_utc) -lt
                            [DateTimeOffset]::Parse([string]$firstLateDirect[0].observed_utc) -and
                        [string]$_.carrier -in @('relay', 'negotiating')
                    }).Count -gt 0; break
            }
            'late_session_commits_incrementally' {
                $lateSamples.Count -gt 1 -and [string]$lateSamples[-1].carrier -in @('lan_direct', 'nat_direct') -and
                    [long]$lateSamples[-1].path_epoch -gt 0; break
            }
            # The current redacted product diagnostics cannot establish these
            # statements. They remain not_run and block the scenario rather
            # than accepting an ungrounded Boolean from the fault backend.
            'marker_exactly_once_before_during_after' { $null; break }
            'exact_bytes_replayed_on_relay' { $null; break }
            'transport_receipt_does_not_advance_durable_ack' { $null; break }
            'frame_set_complete' { $null; break }
            default { $null; break }
        }
        if ($null -eq $value) {
            $unsupported = $true
            $assertion.result = 'not_run'
        } elseif ([bool]$value) {
            $assertion.result = 'passed'
        } else {
            $assertion.result = 'failed'
            $failed = $true
        }
    }
    return [pscustomobject][ordered]@{ complete = -not $unsupported; failed = $failed }
}

function Test-ObservedCarrierSequence {
    param([Parameter(Mandatory)]$ScenarioEvidence)
    $expected = @($ScenarioEvidence.expected_carrier_sequence)
    $observed = @($ScenarioEvidence.carrier_sequence)
    if ($observed.Count -lt $expected.Count) { return $false }
    $cursor = 0
    foreach ($carrier in $observed) {
        if ([string]$carrier -ceq [string]$expected[$cursor]) { $cursor += 1 }
        if ($cursor -eq $expected.Count) { return $true }
    }
    return $false
}

function Invoke-AndroidScenarioOperation {
    param(
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)]$ScenarioEvidence,
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)]$AndroidContract,
        [hashtable]$Additional = @{}
    )
    $arguments = [ordered]@{
        scenario_id = [string]$ScenarioEvidence.id
        phase = $Phase
    }
    foreach ($key in $Additional.Keys) { $arguments[$key] = $Additional[$key] }
    $response = Invoke-AndroidOperation $Adb $Serial $AndroidContract $Operation ([pscustomobject]$arguments)
    if (-not [bool]$response.ok) {
        $reason = [string]$response.failure_code
        if ($reason -notmatch '^[A-Z0-9_]{1,96}$') { $reason = 'ANDROID_SCENARIO_OPERATION_FAILED' }
        throw [InvalidOperationException]::new($reason)
    }
    return $response
}

function Get-ScenarioMarker {
    param([Parameter(Mandatory)][string]$ScenarioId, [Parameter(Mandatory)][string]$Phase)
    return 'TLDPE_' + (Get-TextSha256 "$RunId|$ScenarioId|$Phase").Substring(0, 20)
}

function Observe-UntilCarrier {
    param(
        [Parameter(Mandatory)][string]$ExpectedCarrier,
        [Parameter(Mandatory)]$ScenarioEvidence,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)][string]$Serial,
        [Parameter(Mandatory)]$AndroidContract,
        [Parameter(Mandatory)][string]$WindowsDriver,
        [Parameter(Mandatory)][string]$WindowsPipe,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][ValidateSet('primary_android', 'secondary_android')][string]$DeviceRole,
        [Parameter(Mandatory)][string]$SessionId,
        [Parameter(Mandatory)][ValidateSet('primary', 'late')][string]$SessionRole,
        [Parameter(Mandatory)][string]$PeerDeviceId
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $observation = Add-ObservedSample $ScenarioEvidence $Phase $Adb $Serial $AndroidContract `
            $WindowsDriver $WindowsPipe $WorkingDirectory $DeviceRole $SessionId $SessionRole $PeerDeviceId
        if (-not [bool]$observation.ok) {
            $reason = [string]$observation.reason_code
            if ($reason -notmatch '^[A-Z0-9_]{1,96}$') { $reason = 'DIAGNOSTIC_OBSERVATION_BLOCKED' }
            throw [InvalidOperationException]::new($reason)
        }
        if ([string]$ScenarioEvidence.carrier_sequence[-1] -ceq $ExpectedCarrier) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('EXPECTED_CARRIER_NOT_OBSERVED')
}

function Invoke-FaultOperation {
    param(
        [Parameter(Mandatory)][string]$Adapter,
        [Parameter(Mandatory)][string]$Pipe,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$ScenarioId,
        [Parameter(Mandatory)][string]$Action,
        [Parameter(Mandatory)]$TargetProcess,
        [string]$TargetPeerDeviceId = '',
        [string]$TargetSessionId = ''
    )
    $operation = if ($Action.StartsWith('restore_', [StringComparison]::Ordinal)) { 'restore' } else { 'apply' }
    $request = New-FaultRequest $ScenarioId $operation $Action $TargetProcess
    if ($Action -like '*one_peer*') {
        $request | Add-Member -NotePropertyName target_peer_device_id -NotePropertyValue `
            (ConvertTo-SessionId $TargetPeerDeviceId 'FAULT_TARGET_PEER_INVALID')
        $request | Add-Member -NotePropertyName target_session_id -NotePropertyValue `
            (ConvertTo-SessionId $TargetSessionId 'FAULT_TARGET_SESSION_INVALID')
    }
    return Invoke-JsonFileDriver $Adapter $Pipe $request $WorkingDirectory 65536
}

function Invoke-RestoreAllFaults {
    param(
        [Parameter(Mandatory)][string]$Adapter,
        [Parameter(Mandatory)][string]$Pipe,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [string]$ScenarioId = 'all_scenarios'
    )
    return Invoke-JsonFileDriver $Adapter $Pipe `
        (New-FaultRequest $ScenarioId 'restore_all') $WorkingDirectory 65536
}

function Invoke-DirectProductMatrix {
    param(
        [Parameter(Mandatory)]$Matrix,
        [Parameter(Mandatory)]$Evidence,
        [Parameter(Mandatory)][string]$Adb,
        [Parameter(Mandatory)]$AndroidContract,
        [Parameter(Mandatory)][string]$WindowsDriver,
        [Parameter(Mandatory)][string]$WindowsPipe,
        [Parameter(Mandatory)][string]$FaultAdapter,
        [Parameter(Mandatory)][string]$FaultPipe,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [string]$WindowsLabDriver = '',
        [string]$WindowsLabPipe = '',
        [bool]$WindowsLabReady = $false
    )
    for ($index = 0; $index -lt @($Matrix.scenarios).Count; $index++) {
        $definition = @($Matrix.scenarios)[$index]
        $scenario = @($Evidence.scenarios)[$index]
        $script:stage = 'execute_' + [string]$definition.id
        if ([int]$definition.minimum_android_devices -gt @($Evidence.devices | Where-Object hook_ready).Count) {
            $scenario.status = 'skipped'
            $scenario.reason_code = 'SECONDARY_ANDROID_DEVICE_UNAVAILABLE'
            continue
        }
        if (@($definition.required_windows_lab_actions).Count -gt 0 -and -not $WindowsLabReady) {
            $scenario.status = 'blocked'
            $scenario.reason_code = 'WINDOWS_LAB_CONTROL_UNAVAILABLE'
            continue
        }
        if (@($definition.required_device_actions).Count -gt 0 -and
            -not (Test-AndroidDeviceControlAvailable $Adb $PrimaryAndroidSerial)) {
            $scenario.status = 'blocked'
            $scenario.reason_code = 'ANDROID_DEVICE_CONTROL_UNAVAILABLE'
            continue
        }
        $topology = Invoke-JsonFileDriver $FaultAdapter $FaultPipe `
            (New-FaultRequest ([string]$definition.id) 'probe_topology' -Topology ([string]$definition.topology)) `
            $WorkingDirectory 65536
        if (-not [bool]$topology.ok) {
            $scenario.status = 'blocked'
            $scenario.reason_code = if ([string]$topology.reason_code -match '^[A-Z0-9_]{1,96}$') {
                [string]$topology.reason_code
            } else { 'FAULT_PLATFORM_UNAVAILABLE' }
            continue
        }
        Assert-RequiredProperties $topology @('available') 'TOPOLOGY_PROBE_INVALID' $script:stage
        if ($topology.available -isnot [bool]) { Stop-DirectProductE2E 'TOPOLOGY_PROBE_INVALID' $script:stage }
        if (-not [bool]$topology.available) {
            $scenario.status = 'skipped'
            $scenario.reason_code = if ([string]$topology.reason_code -match '^[A-Z0-9_]{1,96}$') {
                [string]$topology.reason_code
            } else { 'PHYSICAL_TOPOLOGY_UNAVAILABLE' }
            continue
        }

        $scenario.executed = $true
        $scenarioFailure = $null
        $primarySessionPrepared = $false
        $secondarySessionPrepared = $false
        $labOutputStarted = $false
        $primarySessionId = ''
        $primaryPeerDeviceId = ''
        $secondarySessionId = ''
        $secondaryPeerDeviceId = ''
        $lateSessionId = ''
        $currentSessionId = ''
        $currentSessionRole = 'primary'
        $proofs = @{
            marker_confirmation_count = 0
            relay_marker_confirmed = $false
            confirmed_resize_count = 0
            final_geometry_confirmed = $false
        }
        try {
            $prepared = Invoke-AndroidScenarioOperation 'prepare_session' 'setup' $scenario $Adb `
                $PrimaryAndroidSerial $AndroidContract
            Assert-RequiredProperties $prepared @('session_id', 'peer_device_id') `
                'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
            $primarySessionId = ConvertTo-SessionId ([string]$prepared.session_id) `
                'ANDROID_DIRECT_SESSION_INVALID'
            $primaryPeerDeviceId = ConvertTo-SessionId ([string]$prepared.peer_device_id) `
                'ANDROID_DIRECT_PEER_INVALID'
            $currentSessionId = $primarySessionId
            $primarySessionPrepared = $true

            if ([int]$definition.minimum_android_devices -gt 1) {
                $secondaryPrepared = Invoke-AndroidScenarioOperation 'prepare_session' 'setup' $scenario $Adb `
                    $SecondaryAndroidSerial $AndroidContract
                Assert-RequiredProperties $secondaryPrepared @('session_id', 'peer_device_id') `
                    'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
                $secondarySessionId = ConvertTo-SessionId ([string]$secondaryPrepared.session_id) `
                    'ANDROID_DIRECT_SESSION_INVALID'
                $secondaryPeerDeviceId = ConvertTo-SessionId ([string]$secondaryPrepared.peer_device_id) `
                    'ANDROID_DIRECT_PEER_INVALID'
                if ($secondarySessionId -ceq $primarySessionId -or
                    $secondaryPeerDeviceId -ceq $primaryPeerDeviceId) {
                    throw [InvalidOperationException]::new('ANDROID_DEVICE_IDENTITIES_NOT_DISTINCT')
                }
                $secondarySessionPrepared = $true
            }

            $windowsInitial = Get-WindowsObservation $WindowsDriver $WindowsPipe ([string]$definition.id) `
                $WorkingDirectory 'inspect' $currentSessionId $currentSessionRole $primaryPeerDeviceId
            if (-not [bool]$windowsInitial.ok) {
                throw [InvalidOperationException]::new('WINDOWS_DIAGNOSTICS_UNAVAILABLE')
            }
            $targetProcess = ConvertTo-ProcessIdentity $windowsInitial.diagnostics `
                'agent_pid' 'agent_start_time' 'process_image_sha256'
            $targetSessionHost = ConvertTo-ProcessIdentity $windowsInitial.diagnostics `
                'session_host_pid' 'session_host_start_time' 'session_host_image_sha256'

            $firstAction = [string]@($definition.required_fault_actions)[0]
            $expected = @($definition.expected_carriers | ForEach-Object { [string]$_ })
            $directBeforeFault = $firstAction -notlike 'block_direct_udp' -and
                @($expected | Where-Object { $_ -in @('lan_direct', 'nat_direct') }).Count -gt 0
            if ($directBeforeFault) {
                $directCarrier = @($expected | Where-Object { $_ -in @('lan_direct', 'nat_direct') })[-1]
                Observe-UntilCarrier $directCarrier $scenario 'before_fault' $Adb $PrimaryAndroidSerial `
                    $AndroidContract $WindowsDriver $WindowsPipe $WorkingDirectory `
                    'primary_android' $currentSessionId $currentSessionRole $primaryPeerDeviceId
            } else {
                Add-RequiredObservedSample $scenario 'before_fault' $Adb $PrimaryAndroidSerial $AndroidContract `
                    $WindowsDriver $WindowsPipe $WorkingDirectory 'primary_android' $currentSessionId `
                    $currentSessionRole $primaryPeerDeviceId | Out-Null
            }
            Invoke-AndroidScenarioOperation 'send_text_marker' 'before_fault' $scenario $Adb `
                $PrimaryAndroidSerial $AndroidContract @{ marker = Get-ScenarioMarker ([string]$definition.id) 'before_fault' } | Out-Null
            $proofs.marker_confirmation_count += 1

            if ($secondarySessionPrepared) {
                Observe-UntilCarrier $directCarrier $scenario 'secondary_before_fault' $Adb `
                    $SecondaryAndroidSerial $AndroidContract $WindowsDriver $WindowsPipe $WorkingDirectory `
                    'secondary_android' $secondarySessionId 'primary' $secondaryPeerDeviceId
                Invoke-AndroidScenarioOperation 'send_text_marker' 'secondary_before_fault' $scenario $Adb `
                    $SecondaryAndroidSerial $AndroidContract `
                    @{ marker = Get-ScenarioMarker ([string]$definition.id) 'secondary_before_fault' } | Out-Null
            }

            $resizeGeometries = @(@(79, 23), @(101, 31), @(87, 27))
            $resizeIndex = 0
            if (@($definition.required_windows_lab_actions) -ccontains 'start_long_output') {
                $labStart = Invoke-WindowsLabControlOperation $WindowsLabDriver $WindowsLabPipe `
                    $WorkingDirectory ([string]$definition.id) 'start_long_output' $primarySessionId `
                    'primary' $targetSessionHost
                if (-not [bool]$labStart.ok) {
                    $reason = [string]$labStart.reason_code
                    if ($reason -notmatch '^[A-Z0-9_]{1,96}$') { $reason = 'WINDOWS_LAB_CONTROL_UNAVAILABLE' }
                    throw [InvalidOperationException]::new($reason)
                }
                $labOutputStarted = $true
                $geometry = $resizeGeometries[$resizeIndex++]
                $labResize = Invoke-WindowsLabControlOperation $WindowsLabDriver $WindowsLabPipe `
                    $WorkingDirectory ([string]$definition.id) 'resize_window' $primarySessionId `
                    'primary' $targetSessionHost $geometry[0] $geometry[1]
                if (-not [bool]$labResize.ok) { throw [InvalidOperationException]::new('WINDOWS_RESIZE_UNCONFIRMED') }
                $proofs.confirmed_resize_count += 1
            }

            foreach ($deviceAction in @($definition.required_device_actions | ForEach-Object { [string]$_ })) {
                Invoke-AndroidDeviceAction $Adb $PrimaryAndroidSerial $deviceAction | Out-Null
                Add-RequiredObservedSample $scenario $deviceAction $Adb $PrimaryAndroidSerial $AndroidContract `
                    $WindowsDriver $WindowsPipe $WorkingDirectory 'primary_android' $primarySessionId `
                    'primary' $primaryPeerDeviceId | Out-Null
            }

            foreach ($action in @($definition.required_fault_actions | ForEach-Object { [string]$_ })) {
                $faultResponse = Invoke-FaultOperation $FaultAdapter $FaultPipe $WorkingDirectory `
                    ([string]$definition.id) $action $targetProcess $primaryPeerDeviceId $primarySessionId
                if (-not [bool]$faultResponse.ok) {
                    $reason = [string]$faultResponse.reason_code
                    if ($reason -notmatch '^[A-Z0-9_]{1,96}$') { $reason = 'FAULT_ACTION_BLOCKED' }
                    throw [InvalidOperationException]::new($reason)
                }
                $scenario.fault_actions_executed = @($scenario.fault_actions_executed) + $action
                if ($action -ceq 'delay_late_session_negotiation') {
                    $lateSession = Invoke-AndroidScenarioOperation 'open_late_session' $action $scenario $Adb `
                        $PrimaryAndroidSerial $AndroidContract
                    Assert-RequiredProperties $lateSession @('session_id', 'session_role') `
                        'ANDROID_DIRECT_HOOK_RESPONSE_INVALID' $script:stage
                    if ([string]$lateSession.session_role -cne 'late') {
                        throw [InvalidOperationException]::new('ANDROID_DIRECT_SESSION_INVALID')
                    }
                    $currentSessionId = ConvertTo-SessionId ([string]$lateSession.session_id) `
                        'ANDROID_DIRECT_SESSION_INVALID'
                    $lateSessionId = $currentSessionId
                    $currentSessionRole = 'late'
                }
                Invoke-AndroidScenarioOperation 'send_text_marker' $action $scenario $Adb $PrimaryAndroidSerial `
                    $AndroidContract @{ marker = Get-ScenarioMarker ([string]$definition.id) $action } | Out-Null
                $proofs.marker_confirmation_count += 1
                if (-not [string]::IsNullOrEmpty($lateSessionId)) {
                    Add-RequiredObservedSample $scenario ($action + '_primary') $Adb $PrimaryAndroidSerial $AndroidContract `
                        $WindowsDriver $WindowsPipe $WorkingDirectory 'primary_android' $primarySessionId `
                        'primary' $primaryPeerDeviceId | Out-Null
                    Add-RequiredObservedSample $scenario ($action + '_late') $Adb $PrimaryAndroidSerial $AndroidContract `
                        $WindowsDriver $WindowsPipe $WorkingDirectory 'primary_android' $lateSessionId `
                        'late' $primaryPeerDeviceId | Out-Null
                } else {
                    Add-RequiredObservedSample $scenario $action $Adb $PrimaryAndroidSerial $AndroidContract `
                        $WindowsDriver $WindowsPipe $WorkingDirectory 'primary_android' $currentSessionId `
                        $currentSessionRole $primaryPeerDeviceId | Out-Null
                }
                if ([string]$scenario.carrier_sequence[-1] -ceq 'relay') {
                    $proofs.relay_marker_confirmed = $true
                }
                if ($secondarySessionPrepared) {
                    Invoke-AndroidScenarioOperation 'send_text_marker' ($action + '_secondary') $scenario $Adb `
                        $SecondaryAndroidSerial $AndroidContract `
                        @{ marker = Get-ScenarioMarker ([string]$definition.id) ($action + '_secondary') } | Out-Null
                    $proofs.marker_confirmation_count += 1
                    Add-RequiredObservedSample $scenario ($action + '_secondary') $Adb $SecondaryAndroidSerial `
                        $AndroidContract $WindowsDriver $WindowsPipe $WorkingDirectory 'secondary_android' `
                        $secondarySessionId 'primary' $secondaryPeerDeviceId | Out-Null
                }
                if ($labOutputStarted -and $resizeIndex -lt $resizeGeometries.Count) {
                    $geometry = $resizeGeometries[$resizeIndex++]
                    $labResize = Invoke-WindowsLabControlOperation $WindowsLabDriver $WindowsLabPipe `
                        $WorkingDirectory ([string]$definition.id) 'resize_window' $primarySessionId `
                        'primary' $targetSessionHost $geometry[0] $geometry[1]
                    if (-not [bool]$labResize.ok) {
                        throw [InvalidOperationException]::new('WINDOWS_RESIZE_UNCONFIRMED')
                    }
                    $proofs.confirmed_resize_count += 1
                    if ($resizeIndex -eq $resizeGeometries.Count -and
                        $geometry[0] -eq 87 -and $geometry[1] -eq 27) {
                        $proofs.final_geometry_confirmed = $true
                    }
                }
            }

            $finalExpected = [string]$expected[-1]
            Observe-UntilCarrier $finalExpected $scenario 'after_faults' $Adb $PrimaryAndroidSerial `
                $AndroidContract $WindowsDriver $WindowsPipe $WorkingDirectory `
                'primary_android' $currentSessionId $currentSessionRole $primaryPeerDeviceId
            Invoke-AndroidScenarioOperation 'send_text_marker' 'after_faults' $scenario $Adb `
                $PrimaryAndroidSerial $AndroidContract @{ marker = Get-ScenarioMarker ([string]$definition.id) 'after_faults' } | Out-Null
            $proofs.marker_confirmation_count += 1
            if ([string]$scenario.carrier_sequence[-1] -ceq 'relay') {
                $proofs.relay_marker_confirmed = $true
            }
            if (-not [string]::IsNullOrEmpty($lateSessionId)) {
                Add-RequiredObservedSample $scenario 'after_faults_primary' $Adb $PrimaryAndroidSerial $AndroidContract `
                    $WindowsDriver $WindowsPipe $WorkingDirectory 'primary_android' $primarySessionId `
                    'primary' $primaryPeerDeviceId | Out-Null
            }
            if ($secondarySessionPrepared) {
                Add-RequiredObservedSample $scenario 'peer_isolation' $Adb $SecondaryAndroidSerial $AndroidContract `
                    $WindowsDriver $WindowsPipe $WorkingDirectory 'secondary_android' $secondarySessionId `
                    'primary' $secondaryPeerDeviceId | Out-Null
            }
            if ($labOutputStarted) {
                $labStop = Invoke-WindowsLabControlOperation $WindowsLabDriver $WindowsLabPipe `
                    $WorkingDirectory ([string]$definition.id) 'stop_long_output' $primarySessionId `
                    'primary' $targetSessionHost
                if (-not [bool]$labStop.ok) {
                    throw [InvalidOperationException]::new('WINDOWS_LONG_OUTPUT_STOP_UNCONFIRMED')
                }
                $labOutputStarted = $false
            }

            $verification = Invoke-JsonFileDriver $FaultAdapter $FaultPipe `
                (New-FaultRequest ([string]$definition.id) 'probe') $WorkingDirectory 65536
            if (-not [bool]$verification.ok) { throw [InvalidOperationException]::new('SCENARIO_VERIFICATION_BLOCKED') }
            $assertionResult = Complete-ScenarioAssertions $scenario $proofs
            $carrierPassed = Test-ObservedCarrierSequence $scenario
            if (-not $carrierPassed -or [bool]$assertionResult.failed) {
                throw [InvalidOperationException]::new('SCENARIO_ASSERTION_FAILED')
            }
            if (-not [bool]$assertionResult.complete) {
                $scenario.status = 'blocked'
                $scenario.reason_code = 'ASSERTION_EVIDENCE_UNAVAILABLE'
            } else {
                $scenario.status = 'passed'
            }
        }
        catch {
            $scenarioFailure = if ($_.Exception.Message -match '^[A-Z0-9_]{1,96}$') {
                $_.Exception.Message
            } else { 'SCENARIO_EXECUTION_FAILED' }
            $blockedReasons = @(
                'ACCOUNT_UNAVAILABLE', 'ACCEPTANCE_PC_UNAVAILABLE', 'SESSION_SELECTION_AMBIGUOUS',
                'DIRECT_DIAGNOSTICS_UNAVAILABLE', 'WINDOWS_DIAGNOSTICS_UNAVAILABLE',
                'WINDOWS_DIAGNOSTICS_COUNTERS_UNAVAILABLE', 'WINDOWS_LAB_CONTROL_UNAVAILABLE',
                'FAULT_PLATFORM_UNAVAILABLE', 'FAULT_ACTION_BLOCKED', 'SCENARIO_VERIFICATION_BLOCKED',
                'DIAGNOSTIC_OBSERVATION_BLOCKED', 'ANDROID_DEVICE_CONTROL_UNAVAILABLE',
                'PRODUCT_ACTIVITY_UNAVAILABLE',
                'PRODUCT_REPOSITORY_CLOSED', 'TEST_SESSION_UNAVAILABLE',
                'REMOTE_RESIZE_UNSUPPORTED', 'LONG_OUTPUT_CONFIRMATION_UNAVAILABLE',
                'DEVICE_LOCK_CONTROL_UNAVAILABLE', 'DEVICE_WAKE_CONTROL_UNAVAILABLE'
            )
            $scenario.status = if ($scenarioFailure -cin $blockedReasons) { 'blocked' } else { 'failed' }
            $scenario.reason_code = $scenarioFailure
            $script:failureCode = $null
            $script:failureStage = $null
        }
        finally {
            try {
                $cleanup = Invoke-RestoreAllFaults $FaultAdapter $FaultPipe $WorkingDirectory ([string]$definition.id)
                if (-not [bool]$cleanup.ok) {
                    $scenario.status = 'failed'
                    $scenario.reason_code = 'FAULT_CLEANUP_FAILED'
                }
            }
            catch {
                $scenario.status = 'failed'
                $scenario.reason_code = 'FAULT_CLEANUP_FAILED'
                $script:failureCode = $null
                $script:failureStage = $null
            }
            if ($labOutputStarted -and $WindowsLabReady) {
                try {
                    Invoke-WindowsLabControlOperation $WindowsLabDriver $WindowsLabPipe $WorkingDirectory `
                        ([string]$definition.id) 'cleanup_all' | Out-Null
                } catch {
                    $scenario.status = 'failed'
                    $scenario.reason_code = 'WINDOWS_LAB_CLEANUP_FAILED'
                    $script:failureCode = $null
                    $script:failureStage = $null
                }
            }
            if ($secondarySessionPrepared) {
                try {
                    Invoke-AndroidScenarioOperation 'close_test_sessions' 'cleanup' $scenario $Adb `
                        $SecondaryAndroidSerial $AndroidContract | Out-Null
                } catch {
                    $scenario.status = 'failed'
                    $scenario.reason_code = 'TEST_SESSION_CLEANUP_FAILED'
                    $script:failureCode = $null
                    $script:failureStage = $null
                }
            }
            if ($primarySessionPrepared) {
                try {
                    Invoke-AndroidScenarioOperation 'close_test_sessions' 'cleanup' $scenario $Adb `
                        $PrimaryAndroidSerial $AndroidContract | Out-Null
                } catch {
                    $scenario.status = 'failed'
                    $scenario.reason_code = 'TEST_SESSION_CLEANUP_FAILED'
                    $script:failureCode = $null
                    $script:failureStage = $null
                }
            }
        }
    }
}

function Write-Evidence {
    param([Parameter(Mandatory)]$Evidence, [Parameter(Mandatory)][string]$Path)
    $Evidence.checks = @($script:checks)
    $json = $Evidence | ConvertTo-Json -Depth 16
    try {
        # Test-Json emits non-terminating diagnostics for losing oneOf/if branches
        # even when another branch accepts the document. The Boolean is the
        # authoritative aggregate result.
        $valid = $json | Test-Json -SchemaFile $script:schemaPath -ErrorAction SilentlyContinue
        Assert-DirectProductE2E ([bool]$valid) 'EVIDENCE_SCHEMA_REJECTED' 'write_evidence'
    }
    catch {
        if ($script:failureCode -ceq 'EVIDENCE_SCHEMA_REJECTED') { throw }
        Stop-DirectProductE2E 'EVIDENCE_SCHEMA_REJECTED' 'write_evidence'
    }
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

$matrix = Read-JsonFile -Path (Resolve-RequiredFile $script:matrixPath 'MATRIX_MISSING' 'load_matrix') `
    -Code 'MATRIX_INVALID' -MaximumBytes 1048576
Assert-RequiredProperties $matrix @('schema_version', 'kind', 'scenarios') 'MATRIX_INVALID' 'load_matrix'
Assert-DirectProductE2E ([int]$matrix.schema_version -eq 1 -and
    [string]$matrix.kind -ceq 'termlink.direct-product-e2e-matrix' -and
    @($matrix.scenarios).Count -ge 10) 'MATRIX_INVALID' 'load_matrix'
foreach ($scenarioDefinition in @($matrix.scenarios)) {
    Assert-RequiredProperties $scenarioDefinition @(
        'id', 'topology', 'minimum_android_devices', 'expected_carriers',
        'required_fault_actions', 'required_device_actions', 'required_windows_lab_actions',
        'relay_generation_expectation', 'required_assertions'
    ) 'MATRIX_INVALID' 'load_matrix'
    Assert-DirectProductE2E ([string]$scenarioDefinition.id -match '^[a-z0-9_]{1,96}$' -and
        [int]$scenarioDefinition.minimum_android_devices -in 1..2 -and
        (Test-ExactSetContains @($scenarioDefinition.required_device_actions) `
            @($scenarioDefinition.required_device_actions)) -and
        @($scenarioDefinition.required_device_actions | Where-Object {
            [string]$_ -cnotin @('lock_android', 'wake_android')
        }).Count -eq 0 -and
        @($scenarioDefinition.required_windows_lab_actions | Where-Object {
            [string]$_ -cnotin @('start_long_output', 'resize_window', 'stop_long_output')
        }).Count -eq 0) 'MATRIX_INVALID' 'load_matrix'
}

$started = [DateTime]::UtcNow
$origin = $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
$evidencePath = Join-Path $evidenceRoot 'evidence.json'
if (Test-Path -LiteralPath $evidencePath) {
    throw [InvalidOperationException]::new('EVIDENCE_ALREADY_EXISTS at initialize')
}
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$driverWorkingDirectory = Join-Path $evidenceRoot '.driver-work'
[IO.Directory]::CreateDirectory($driverWorkingDirectory) | Out-Null
$windowsLabDriverPath = ''
$windowsLabPipe = ''
$windowsLabReady = $false
$evidence = [pscustomobject][ordered]@{
    schema_version = 1
    kind = 'termlink.direct-product-e2e-evidence'
    run_id = $RunId
    matrix_version = 1
    scope = if ($Mode -ceq 'PreflightOnly') { 'preflight_only' } else { 'full_matrix' }
    outcome = 'failed'
    real_network_test_executed = $false
    started_utc = $started.ToString('o')
    completed_utc = $null
    artifacts = [pscustomobject][ordered]@{
        android = [pscustomobject][ordered]@{
            sha256 = $null
            contract_schema_version = $null
            application_id_sha256 = $null
            receiver_class_sha256 = $null
            data_plane_acceptance = $null
        }
        windows = [pscustomobject][ordered]@{
            sha256 = $null
            assembly_configuration = $null
            data_plane_acceptance = $null
        }
    }
    server = [pscustomobject][ordered]@{
        origin_sha256 = Get-TextSha256 $origin
        build_sha256 = $null
        acceptance_environment = $null
        direct_transport_enabled = $null
        direct_routing_permitted = $null
        capabilities = @()
    }
    devices = @(
        [pscustomobject][ordered]@{ role = 'primary_android'; adapter_ready = $false; hook_ready = $false },
        [pscustomobject][ordered]@{ role = 'secondary_android'; adapter_ready = $false; hook_ready = $false }
    )
    diagnostics = [pscustomobject][ordered]@{
        android_required = $script:requiredAndroidDiagnostics
        windows_required = $script:requiredWindowsDiagnostics
        android_available = @()
        windows_available = @()
    }
    fault_adapter = [pscustomobject][ordered]@{
        ready = $false
        process_scoped = $false
        cleanup_guaranteed = $false
        isolated_channels = @()
        supported_actions = @()
    }
    checks = @()
    scenarios = @($matrix.scenarios | ForEach-Object { New-ScenarioEvidence $_ })
    failure = $null
}

try {
    Assert-DirectProductE2E ([string]::IsNullOrEmpty($SecondaryAndroidSerial) -or
        $PrimaryAndroidSerial -cne $SecondaryAndroidSerial) `
        'ANDROID_DEVICE_IDENTITIES_NOT_DISTINCT' 'validate_inputs'
    $metadataOrigin = $ServerAcceptanceMetadataUri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
    Assert-DirectProductE2E ($metadataOrigin -ceq $origin) 'SERVER_METADATA_ORIGIN_MISMATCH' 'validate_inputs'
    Add-PassedCheck 'explicit_inputs_valid'

    $script:stage = 'validate_artifacts'
    $apk = Resolve-RequiredFile $AndroidApkPath 'ANDROID_APK_UNAVAILABLE' $script:stage
    $agent = Resolve-RequiredFile $WindowsAgentPath 'WINDOWS_AGENT_UNAVAILABLE' $script:stage
    $adb = Resolve-RequiredFile $AndroidAdbPath 'ADB_UNAVAILABLE' $script:stage
    $windowsContractPath = Resolve-RequiredFile $WindowsDiagnosticsContractPath `
        'WINDOWS_DIAGNOSTICS_CONTRACT_UNAVAILABLE' $script:stage
    $faultContractPath = Resolve-RequiredFile $FaultAdapterContractPath `
        'FAULT_ADAPTER_CONTRACT_UNAVAILABLE' $script:stage
    $evidence.artifacts.android.sha256 = Get-Sha256 $apk
    $evidence.artifacts.windows.sha256 = Get-Sha256 $agent
    Add-PassedCheck 'artifacts_available'

    $script:stage = 'validate_android_contract'
    $androidContract = Get-ApkContract $apk
    Assert-RequiredProperties $androidContract @(
        'schema_version', 'kind', 'transport', 'application_id', 'receiver_class',
        'broadcast_action', 'response_relative_path', 'server_origin', 'build_type',
        'shadow_acceptance', 'data_plane_acceptance', 'diagnostics', 'operations',
        'required_server_capabilities', 'operation_extra', 'request_id_extra', 'run_id_extra',
        'arguments_json_extra', 'probe_operation'
    ) 'ANDROID_DIRECT_HOOK_CONTRACT_INVALID' $script:stage
    Assert-DirectProductE2E ([int]$androidContract.schema_version -eq 1 -and
        [string]$androidContract.kind -ceq 'termlink.android-direct-product-e2e-contract' -and
        [string]$androidContract.transport -ceq 'explicit_broadcast_run_as_file' -and
        [string]$androidContract.application_id -match '^[a-zA-Z][a-zA-Z0-9_.]{2,191}$' -and
        [string]$androidContract.receiver_class -match '^[a-zA-Z][a-zA-Z0-9_.$]{2,255}$' -and
        [string]$androidContract.broadcast_action -match '^[a-zA-Z][a-zA-Z0-9_.]{2,255}$' -and
        [string]$androidContract.response_relative_path -match '^no_backup/[A-Za-z0-9._/-]{1,200}$' -and
        [string]$androidContract.server_origin -ceq $origin -and
        [string]$androidContract.build_type -ceq 'debug' -and
        $androidContract.shadow_acceptance -is [bool] -and [bool]$androidContract.shadow_acceptance -and
        $androidContract.data_plane_acceptance -is [bool] -and [bool]$androidContract.data_plane_acceptance -and
        [string]$androidContract.operation_extra -match '^[a-z0-9_]{1,64}$' -and
        [string]$androidContract.request_id_extra -match '^[a-z0-9_]{1,64}$' -and
        [string]$androidContract.run_id_extra -match '^[a-z0-9_]{1,64}$' -and
        [string]$androidContract.arguments_json_extra -match '^[a-z0-9_]{1,64}$' -and
        [string]$androidContract.probe_operation -ceq 'probe' -and
        (Test-ExactSetContains @($androidContract.required_server_capabilities) $script:requiredCapabilities) -and
        (Test-ExactSetContains @($androidContract.operations) $script:requiredHookOperations) -and
        (Test-ExactSetContains @($androidContract.diagnostics) $script:requiredAndroidDiagnostics)) `
        'ANDROID_DIRECT_HOOK_CONTRACT_INVALID' $script:stage
    $evidence.artifacts.android.contract_schema_version = 1
    $evidence.artifacts.android.application_id_sha256 = Get-TextSha256 ([string]$androidContract.application_id)
    $evidence.artifacts.android.receiver_class_sha256 = Get-TextSha256 ([string]$androidContract.receiver_class)
    $evidence.artifacts.android.data_plane_acceptance = $true
    $evidence.diagnostics.android_available = @($androidContract.diagnostics | ForEach-Object { [string]$_ } | Sort-Object)
    Add-PassedCheck 'android_acceptance_metadata_valid'

    $script:stage = 'validate_windows_contract'
    try {
        $assembly = [Reflection.Assembly]::LoadFrom($agent)
        $configuration = @($assembly.GetCustomAttributes([Reflection.AssemblyConfigurationAttribute], $false))[0].Configuration
        $directMarker = @($assembly.GetCustomAttributes([Reflection.AssemblyMetadataAttribute], $false) | Where-Object {
            $_.Key -ceq 'TermLink.DirectDataPlaneAcceptance'
        })
    }
    catch { Stop-DirectProductE2E 'WINDOWS_ACCEPTANCE_METADATA_INVALID' $script:stage }
    Assert-DirectProductE2E ([string]$configuration -ceq 'DirectAcceptance' -and
        $directMarker.Count -eq 1 -and [string]$directMarker[0].Value -ceq 'true') `
        'WINDOWS_ACCEPTANCE_BUILD_REQUIRED' $script:stage
    $windowsContract = Read-JsonFile $windowsContractPath 'WINDOWS_DIAGNOSTICS_CONTRACT_INVALID'
    Assert-RequiredProperties $windowsContract @(
        'schema_version', 'kind', 'executable_sha256', 'safe_control_interface', 'diagnostics',
        'driver_relative_path', 'driver_sha256', 'driver_protocol', 'control_pipe_name',
        'length_prefix', 'request_max_bytes', 'response_max_bytes', 'request_operations',
        'inspect_selector', 'required_runtime_capabilities'
    ) 'WINDOWS_DIAGNOSTICS_CONTRACT_INVALID' $script:stage
    Assert-RequiredProperties $windowsContract.inspect_selector @(
        'required_fields', 'session_id_format', 'peer_device_id_format', 'session_roles',
        'exact_session_role_and_peer_required', 'unknown_or_mismatched_selector_fails_closed',
        'probe_forbids_selector', 'excluded_from_evidence'
    ) 'WINDOWS_DIAGNOSTICS_CONTRACT_INVALID' $script:stage
    Assert-DirectProductE2E ([int]$windowsContract.schema_version -eq 1 -and
        [string]$windowsContract.kind -ceq 'termlink.windows-direct-diagnostics-contract' -and
        [string]$windowsContract.executable_sha256 -ceq $evidence.artifacts.windows.sha256 -and
        $windowsContract.safe_control_interface -is [bool] -and [bool]$windowsContract.safe_control_interface -and
        [string]$windowsContract.driver_relative_path -notmatch '(^|[\\/])\.\.([\\/]|$)' -and
        [string]$windowsContract.driver_protocol -ceq 'bounded_json_file_to_length_prefixed_named_pipe_v1' -and
        [string]$windowsContract.length_prefix -ceq 'uint32_little_endian' -and
        [int]$windowsContract.request_max_bytes -eq 65536 -and
        [int]$windowsContract.response_max_bytes -eq 1048576 -and
        [string]$windowsContract.control_pipe_name -match '^[A-Za-z0-9._-]{1,180}$' -and
        (Test-ExactSetContains @($windowsContract.request_operations) @('probe', 'inspect')) -and
        (Test-ExactSetContains @($windowsContract.inspect_selector.required_fields) @(
            'session_id', 'session_role', 'peer_device_id'
        )) -and
        [string]$windowsContract.inspect_selector.session_id_format -ceq 'uuid_d_non_empty' -and
        [string]$windowsContract.inspect_selector.peer_device_id_format -ceq 'uuid_d_non_empty' -and
        (Test-ExactSetContains @($windowsContract.inspect_selector.session_roles) @('primary', 'late')) -and
        [bool]$windowsContract.inspect_selector.exact_session_role_and_peer_required -and
        [bool]$windowsContract.inspect_selector.unknown_or_mismatched_selector_fails_closed -and
        [bool]$windowsContract.inspect_selector.probe_forbids_selector -and
        [bool]$windowsContract.inspect_selector.excluded_from_evidence -and
        (Test-ExactSetContains @($windowsContract.required_runtime_capabilities) @(
            'direct_transport_data_v1', 'direct_session_cutover_v1'
        )) -and
        (Test-ExactSetContains @($windowsContract.diagnostics) $script:requiredWindowsDiagnostics)) `
        'WINDOWS_DIAGNOSTICS_CONTRACT_INVALID' $script:stage
    $windowsDriverPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $windowsContractPath) ([string]$windowsContract.driver_relative_path)))
    Assert-DirectProductE2E ((Test-Path -LiteralPath $windowsDriverPath -PathType Leaf) -and
        [string]$windowsContract.driver_sha256 -ceq (Get-Sha256 $windowsDriverPath)) `
        'WINDOWS_DIAGNOSTICS_DRIVER_INVALID' $script:stage
    $evidence.artifacts.windows.assembly_configuration = [string]$configuration
    $evidence.artifacts.windows.data_plane_acceptance = $true
    $evidence.diagnostics.windows_available = @($windowsContract.diagnostics | ForEach-Object { [string]$_ } | Sort-Object)
    Add-PassedCheck 'windows_acceptance_metadata_valid'
    Add-PassedCheck 'required_diagnostics_declared'

    $script:stage = 'validate_server'
    $serverMetadata = Get-ServerAcceptanceMetadata $ServerAcceptanceMetadataUri
    Assert-RequiredProperties $serverMetadata @(
        'schema_version', 'kind', 'environment', 'origin', 'build_sha256',
        'direct_transport_enabled', 'direct_routing_permitted', 'capabilities'
    ) 'SERVER_ACCEPTANCE_METADATA_INVALID' $script:stage
    Assert-DirectProductE2E ([int]$serverMetadata.schema_version -eq 1 -and
        [string]$serverMetadata.kind -ceq 'termlink.server-direct-acceptance-metadata' -and
        [string]$serverMetadata.environment -ceq 'acceptance' -and
        [string]$serverMetadata.origin -ceq $origin -and
        [string]$serverMetadata.build_sha256 -match '^[a-f0-9]{64}$' -and
        $serverMetadata.direct_transport_enabled -is [bool] -and [bool]$serverMetadata.direct_transport_enabled -and
        $serverMetadata.direct_routing_permitted -is [bool] -and [bool]$serverMetadata.direct_routing_permitted -and
        (Test-ExactSetContains @($serverMetadata.capabilities) $script:requiredCapabilities)) `
        'SERVER_DIRECT_ACCEPTANCE_GATE_CLOSED' $script:stage
    $evidence.server.build_sha256 = [string]$serverMetadata.build_sha256
    $evidence.server.acceptance_environment = $true
    $evidence.server.direct_transport_enabled = $true
    $evidence.server.direct_routing_permitted = $true
    $evidence.server.capabilities = @($serverMetadata.capabilities | ForEach-Object { [string]$_ } | Sort-Object)
    Add-PassedCheck 'server_direct_acceptance_gate_open'

    $script:stage = 'validate_fault_adapter'
    $faultContract = Read-JsonFile $faultContractPath 'FAULT_ADAPTER_CONTRACT_INVALID'
    Assert-RequiredProperties $faultContract @(
        'schema_version', 'kind', 'adapter_relative_path', 'adapter_sha256',
        'process_scoped', 'cleanup_guaranteed', 'lease_scoped', 'isolated_channels', 'supported_actions',
        'adapter_protocol', 'control_pipe_name', 'peer_targeting'
    ) 'FAULT_ADAPTER_CONTRACT_INVALID' $script:stage
    Assert-RequiredProperties $faultContract.peer_targeting @(
        'actions', 'required_fields', 'uuid_format', 'exact_peer_and_session_required',
        'excluded_from_evidence'
    ) 'FAULT_ADAPTER_CONTRACT_INVALID' $script:stage
    $adapterPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $faultContractPath) ([string]$faultContract.adapter_relative_path)))
    Assert-DirectProductE2E (Test-Path -LiteralPath $adapterPath -PathType Leaf) `
        'FAULT_ADAPTER_UNAVAILABLE' $script:stage
    $requiredActions = @($matrix.scenarios.required_fault_actions | ForEach-Object { $_ } | Select-Object -Unique)
    Assert-DirectProductE2E ([int]$faultContract.schema_version -eq 1 -and
        [string]$faultContract.kind -ceq 'termlink.direct-fault-adapter-contract' -and
        [string]$faultContract.adapter_relative_path -notmatch '(^|[\\/])\.\.([\\/]|$)' -and
        [string]$faultContract.adapter_sha256 -ceq (Get-Sha256 $adapterPath) -and
        [string]$faultContract.adapter_protocol -ceq 'bounded_json_file_to_length_prefixed_named_pipe_v1' -and
        [string]$faultContract.control_pipe_name -match '^[A-Za-z0-9._-]{1,180}$' -and
        $faultContract.process_scoped -is [bool] -and [bool]$faultContract.process_scoped -and
        $faultContract.cleanup_guaranteed -is [bool] -and [bool]$faultContract.cleanup_guaranteed -and
        $faultContract.lease_scoped -is [bool] -and [bool]$faultContract.lease_scoped -and
        (Test-ExactSetContains @($faultContract.isolated_channels) $script:requiredFaultChannels) -and
        (Test-ExactSetContains @($faultContract.supported_actions) $requiredActions) -and
        (Test-ExactSetContains @($faultContract.peer_targeting.actions) @(
            'interrupt_one_peer_direct_quic', 'restore_one_peer_direct_quic'
        )) -and
        (Test-ExactSetContains @($faultContract.peer_targeting.required_fields) @(
            'target_peer_device_id', 'target_session_id'
        )) -and
        [string]$faultContract.peer_targeting.uuid_format -ceq 'uuid_d_non_empty' -and
        [bool]$faultContract.peer_targeting.exact_peer_and_session_required -and
        [bool]$faultContract.peer_targeting.excluded_from_evidence) `
        'FAULT_ADAPTER_CONTRACT_INVALID' $script:stage
    $evidence.fault_adapter.ready = $true
    $evidence.fault_adapter.process_scoped = $true
    $evidence.fault_adapter.cleanup_guaranteed = $true
    $evidence.fault_adapter.isolated_channels = @($faultContract.isolated_channels | ForEach-Object { [string]$_ } | Sort-Object)
    $evidence.fault_adapter.supported_actions = @($faultContract.supported_actions | ForEach-Object { [string]$_ } | Sort-Object)
    $faultProbe = Invoke-JsonFileDriver $adapterPath ([string]$faultContract.control_pipe_name) `
        (New-FaultRequest 'preflight' 'probe') $driverWorkingDirectory 65536
    Assert-DirectProductE2E ([bool]$faultProbe.ok -and
        [bool]$faultProbe.process_scoped -and [bool]$faultProbe.cleanup_guaranteed) `
        'FAULT_ADAPTER_PLATFORM_UNAVAILABLE' $script:stage
    Add-PassedCheck 'fault_adapter_isolated_and_recoverable'

    if (-not [string]::IsNullOrWhiteSpace($WindowsLabControlContractPath)) {
        $script:stage = 'validate_windows_lab_control'
        $windowsLabContractPath = Resolve-RequiredFile $WindowsLabControlContractPath `
            'WINDOWS_LAB_CONTROL_CONTRACT_UNAVAILABLE' $script:stage
        $windowsLabContract = Read-JsonFile $windowsLabContractPath 'WINDOWS_LAB_CONTROL_CONTRACT_INVALID'
        Assert-RequiredProperties $windowsLabContract @(
            'schema_version', 'kind', 'driver_relative_path', 'driver_sha256', 'driver_protocol',
            'control_pipe_name', 'process_scoped', 'cleanup_guaranteed', 'supported_operations',
            'selector', 'arbitrary_command_input'
        ) 'WINDOWS_LAB_CONTROL_CONTRACT_INVALID' $script:stage
        $windowsLabDriverPath = [IO.Path]::GetFullPath((Join-Path `
            (Split-Path -Parent $windowsLabContractPath) ([string]$windowsLabContract.driver_relative_path)))
        Assert-DirectProductE2E ([int]$windowsLabContract.schema_version -eq 1 -and
            [string]$windowsLabContract.kind -ceq 'termlink.windows-lab-control-contract' -and
            [string]$windowsLabContract.driver_relative_path -notmatch '(^|[\\/])\.\.([\\/]|$)' -and
            (Test-Path -LiteralPath $windowsLabDriverPath -PathType Leaf) -and
            [string]$windowsLabContract.driver_sha256 -ceq (Get-Sha256 $windowsLabDriverPath) -and
            [string]$windowsLabContract.driver_protocol -ceq `
                'bounded_json_file_to_length_prefixed_named_pipe_v1' -and
            [string]$windowsLabContract.control_pipe_name -match '^[A-Za-z0-9._-]{1,180}$' -and
            $windowsLabContract.process_scoped -is [bool] -and [bool]$windowsLabContract.process_scoped -and
            $windowsLabContract.cleanup_guaranteed -is [bool] -and [bool]$windowsLabContract.cleanup_guaranteed -and
            $windowsLabContract.arbitrary_command_input -is [bool] -and
            -not [bool]$windowsLabContract.arbitrary_command_input -and
            (Test-ExactSetContains @($windowsLabContract.supported_operations) @(
                'probe', 'start_long_output', 'resize_window', 'stop_long_output', 'cleanup_all'
            ))) 'WINDOWS_LAB_CONTROL_CONTRACT_INVALID' $script:stage
        $windowsLabPipe = [string]$windowsLabContract.control_pipe_name
        $windowsLabProbe = Invoke-WindowsLabControlOperation $windowsLabDriverPath $windowsLabPipe `
            $driverWorkingDirectory 'preflight' 'probe'
        if ([bool]$windowsLabProbe.ok) {
            $windowsLabReady = $true
            Add-PassedCheck 'windows_lab_control_ready'
        }
    }

    $script:stage = 'probe_android_devices'
    $primaryProbe = Invoke-AndroidProbe $adb $PrimaryAndroidSerial $androidContract $apk
    $evidence.devices[0].adapter_ready = $true
    $evidence.devices[0].hook_ready = $true
    if (-not [string]::IsNullOrEmpty($SecondaryAndroidSerial)) {
        try {
            $secondaryProbe = Invoke-AndroidProbe $adb $SecondaryAndroidSerial $androidContract $apk
            $evidence.devices[1].adapter_ready = $true
            $evidence.devices[1].hook_ready = $true
            Add-PassedCheck 'two_android_hooks_ready'
        }
        catch {
            $script:failureCode = $null
            $script:failureStage = $null
        }
    }
    Add-PassedCheck 'primary_android_hook_ready'

    $script:stage = 'probe_windows_diagnostics'
    $windowsProbe = Get-WindowsObservation $windowsDriverPath ([string]$windowsContract.control_pipe_name) `
        'preflight' $driverWorkingDirectory 'probe'
    Assert-DirectProductE2E ([bool]$windowsProbe.ok) 'WINDOWS_DIAGNOSTICS_UNAVAILABLE' $script:stage
    Add-PassedCheck 'windows_live_diagnostics_ready'

    if ($Mode -ceq 'FullMatrix') {
        $script:stage = 'execute_matrix'
        Invoke-DirectProductMatrix $matrix $evidence $adb $androidContract $windowsDriverPath `
            ([string]$windowsContract.control_pipe_name) $adapterPath ([string]$faultContract.control_pipe_name) `
            $driverWorkingDirectory $windowsLabDriverPath $windowsLabPipe $windowsLabReady
        $evidence.real_network_test_executed = @($evidence.scenarios | Where-Object executed).Count -gt 0
        $failedScenarios = @($evidence.scenarios | Where-Object status -ceq 'failed')
        $incompleteScenarios = @($evidence.scenarios | Where-Object status -in @('blocked', 'skipped', 'not_run'))
        if ($failedScenarios.Count -gt 0) {
            $evidence.outcome = 'failed'
            $evidence.failure = [pscustomobject][ordered]@{ code = 'PRODUCT_E2E_SCENARIO_FAILED'; stage = 'execute_matrix' }
        } elseif ($incompleteScenarios.Count -gt 0) {
            $evidence.outcome = 'product_e2e_incomplete'
        } else {
            $evidence.outcome = 'product_e2e_passed'
            Add-PassedCheck 'full_matrix_executed_and_passed'
        }
        $evidence.completed_utc = [DateTime]::UtcNow.ToString('o')
        $script:stage = 'write_evidence'
        Write-Evidence $evidence $evidencePath
        [pscustomobject][ordered]@{
            outcome = $evidence.outcome
            real_network_test_executed = $evidence.real_network_test_executed
            executed_scenarios = @($evidence.scenarios | Where-Object executed).Count
            passed_scenarios = @($evidence.scenarios | Where-Object status -ceq 'passed').Count
            skipped_scenarios = @($evidence.scenarios | Where-Object status -ceq 'skipped').Count
            blocked_scenarios = @($evidence.scenarios | Where-Object status -ceq 'blocked').Count
            failed_scenarios = $failedScenarios.Count
            evidence_path = $evidencePath
        }
        return
    }

    $evidence.outcome = 'preflight_passed'
    $evidence.completed_utc = [DateTime]::UtcNow.ToString('o')
    $script:stage = 'write_evidence'
    Write-Evidence $evidence $evidencePath
    [pscustomobject][ordered]@{
        outcome = 'preflight_passed'
        real_network_test_executed = $false
        evidence_path = $evidencePath
    }
}
catch {
    if ([string]::IsNullOrWhiteSpace($script:failureCode)) {
        $script:failureCode = 'UNEXPECTED_PREFLIGHT_FAILURE'
        $script:failureStage = $script:stage
    }
    $evidence.outcome = 'failed'
    $evidence.real_network_test_executed = @($evidence.scenarios | Where-Object executed).Count -gt 0
    $evidence.completed_utc = [DateTime]::UtcNow.ToString('o')
    $evidence.failure = [pscustomobject][ordered]@{
        code = $script:failureCode
        stage = $script:failureStage
    }
    try { Write-Evidence $evidence $evidencePath }
    catch {
        if ($script:failureCode -ceq 'EVIDENCE_SCHEMA_REJECTED') { throw }
    }
    throw [InvalidOperationException]::new("$($script:failureCode) at $($script:failureStage)")
}
finally {
    if ($windowsLabReady -and -not [string]::IsNullOrEmpty($windowsLabDriverPath)) {
        try {
            Invoke-WindowsLabControlOperation $windowsLabDriverPath $windowsLabPipe `
                $driverWorkingDirectory 'all_scenarios' 'cleanup_all' | Out-Null
        } catch {}
    }
    if ($null -ne (Get-Variable -Name adapterPath -ErrorAction SilentlyContinue) -and
        $null -ne (Get-Variable -Name faultContract -ErrorAction SilentlyContinue)) {
        try {
            Invoke-RestoreAllFaults $adapterPath ([string]$faultContract.control_pipe_name) `
                $driverWorkingDirectory 'all_scenarios' | Out-Null
        } catch {}
    }
    if (Test-Path -LiteralPath $driverWorkingDirectory -PathType Container) {
        Get-ChildItem -LiteralPath $driverWorkingDirectory -File -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
