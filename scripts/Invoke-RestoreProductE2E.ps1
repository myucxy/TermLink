[CmdletBinding()]
param(
    [ValidateSet('Delta', 'Fallback', 'Wal')][string]$Mode = 'Delta',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunId,
    [guid]$ExpectedSessionId,
    [ValidatePattern('^emulator-[0-9]{4}$')][string]$EmulatorSerial = 'emulator-5556',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')][string]$AndroidPackageName = 'dev.termlink.harness',
    [string]$AdbPath,
    [string]$AdvanceTriggerPath,
    [string]$PreClosedBaselinePath,
    [ValidateRange(0, 120)][int]$SettleSeconds = 5,
    [ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedBaseSha256,
    [ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedTargetSha256,
    [ValidateRange(1, [long]::MaxValue)][long]$ExpectedFullSnapshotSequence,
    [ValidateSet('BASE_MISSING', 'BASE_PROFILE_MISMATCH', 'UNAVAILABLE', 'NEEDS_FULL_SNAPSHOT',
        'RETIRED_BASE', 'CANCELLED', 'FAILED')][string]$ExpectedFallbackReason,
    [string]$EvidencePath,
    [switch]$SelfTest
)

# Attach to an already armed Debug open_session whose Windows host remains alive.
# Delta hashes must come from independent Windows evidence, not Android's inspect response.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-Restore([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw [InvalidOperationException]::new($Code) }
}

function Get-RestoreField($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { throw [InvalidOperationException]::new("MISSING_$($Name.ToUpperInvariant())") }
    return $property.Value
}

function Assert-RestoreIdentity($Baseline, $Current, [guid]$SessionId) {
    Require-Restore ([guid](Get-RestoreField $Baseline 'session_id') -eq $SessionId) 'BASELINE_SESSION_MISMATCH'
    Require-Restore ([guid](Get-RestoreField $Current 'session_id') -eq $SessionId) 'REOPEN_SESSION_MISMATCH'
    Require-Restore ((Get-RestoreField $Current 'realtime_subscriber_ready') -eq $true) 'SUBSCRIBER_NOT_READY'
    Require-Restore ([long](Get-RestoreField $Current 'live_ack') -ge
        [long](Get-RestoreField $Baseline 'live_ack')) 'DURABLE_ACK_REGRESSED'
    $beforeAttempt = 0L
    if ($null -ne $Baseline.PSObject.Properties['restore_diagnostics']) {
        $beforeAttempt = [long](Get-RestoreField $Baseline.restore_diagnostics 'attempt')
    }
    $diagnostics = Get-RestoreField $Current 'restore_diagnostics'
    Require-Restore ([long](Get-RestoreField $diagnostics 'attempt') -gt $beforeAttempt) 'RESTORE_ATTEMPT_NOT_NEW'
    return $diagnostics
}

function Assert-DeltaRestore($Diagnostics, [string]$BaseHash, [string]$TargetHash, $Baseline, $Current) {
    Require-Restore ($BaseHash -cmatch '^[0-9a-f]{64}$' -and
        $TargetHash -cmatch '^[0-9a-f]{64}$' -and $BaseHash -cne $TargetHash) 'INDEPENDENT_HASHES_REQUIRED'
    Require-Restore ([int](Get-RestoreField $Diagnostics 'delta_request_count') -eq 1) 'DELTA_NOT_REQUESTED'
    Require-Restore ([int](Get-RestoreField $Diagnostics 'delta_install_count') -eq 1) 'DELTA_NOT_INSTALLED'
    Require-Restore ([int](Get-RestoreField $Diagnostics 'full_snapshot_install_count') -eq 0) 'DELTA_INSTALLED_FULL_SNAPSHOT'
    Require-Restore ($null -eq $Diagnostics.PSObject.Properties['delta_fallback_reason']) 'DELTA_FELL_BACK'
    Require-Restore ([string](Get-RestoreField $Diagnostics 'base_sha256') -ceq $BaseHash) 'BASE_HASH_MISMATCH'
    Require-Restore ([string](Get-RestoreField $Diagnostics 'target_sha256') -ceq $TargetHash) 'TARGET_HASH_MISMATCH'
    Require-Restore ([long](Get-RestoreField $Diagnostics 'base_snapshot_sequence') -gt 0 -and
        [long](Get-RestoreField $Diagnostics 'target_snapshot_sequence') -gt
        [long](Get-RestoreField $Diagnostics 'base_snapshot_sequence')) 'DELTA_TARGET_NOT_NEWER'
    $expectedChunks = [int](Get-RestoreField $Diagnostics 'delta_expected_chunk_count')
    Require-Restore ($expectedChunks -ge 1 -and $expectedChunks -le 16) 'DELTA_OBJECT_EMPTY'
    $requestId = [string](Get-RestoreField $Diagnostics 'delta_request_id')
    Require-Restore ([guid]$requestId -ne [guid]::Empty -and
        ([guid]$requestId).ToString() -ceq $requestId) 'DELTA_TRANSFER_ID_INVALID'
    $accepted = [int](Get-RestoreField $Diagnostics 'delta_chunks_accepted')
    $duplicates = [int](Get-RestoreField $Diagnostics 'delta_duplicate_chunks')
    $sent = [int](Get-RestoreField $Diagnostics 'delta_chunk_acks_sent')
    Require-Restore ($accepted -eq [int]$Diagnostics.delta_expected_chunk_count -and
        $duplicates -ge 0 -and $sent -eq $accepted + $duplicates) 'DELTA_TRANSFER_INCOMPLETE'
    Require-Restore ([long](Get-RestoreField $Baseline 'snapshot_sequence_s') -eq
        [long]$Diagnostics.base_snapshot_sequence) 'DELTA_BASE_SEQUENCE_MISMATCH'
    Require-Restore ([long](Get-RestoreField $Current 'snapshot_sequence_s') -ge
        [long]$Diagnostics.target_snapshot_sequence) 'DELTA_TARGET_NOT_VISIBLE'
}

function Assert-FallbackRestore($Diagnostics, [string]$Reason) {
    Require-Restore ([string](Get-RestoreField $Diagnostics 'delta_fallback_reason') -ceq $Reason) 'FALLBACK_REASON_MISMATCH'
    Require-Restore ([int](Get-RestoreField $Diagnostics 'delta_install_count') -eq 0) 'FALLBACK_INSTALLED_DELTA'
}

function Assert-FullSnapshotFallback($Baseline, $Current, $Diagnostics,
    [string]$BaseHash, [string]$TargetHash, [long]$TargetSequence) {
    Require-Restore ([int](Get-RestoreField $Diagnostics 'delta_request_count') -ge 1) 'DELTA_NOT_REQUESTED'
    Require-Restore ([string](Get-RestoreField $Diagnostics 'base_sha256') -ceq $BaseHash) 'BASE_HASH_MISMATCH'
    Require-Restore ([int](Get-RestoreField $Diagnostics 'full_snapshot_install_count') -eq 1) `
        'FULL_SNAPSHOT_NOT_INSTALLED_ONCE'
    Require-Restore ([long](Get-RestoreField $Diagnostics 'full_snapshot_sequence') -eq $TargetSequence) `
        'FULL_SNAPSHOT_SEQUENCE_MISMATCH'
    Require-Restore ([string](Get-RestoreField $Diagnostics 'full_snapshot_sha256') -ceq $TargetHash) `
        'FULL_SNAPSHOT_HASH_MISMATCH'
    Require-Restore ($TargetSequence -gt [long](Get-RestoreField $Baseline 'snapshot_sequence_s')) `
        'FULL_SNAPSHOT_TARGET_NOT_NEWER'
    Require-Restore ([long](Get-RestoreField $Current 'snapshot_sequence_s') -ge $TargetSequence -and
        [long](Get-RestoreField $Current 'snapshot_sequence_s') -ge
        [long](Get-RestoreField $Baseline 'snapshot_sequence_s')) 'FULL_SNAPSHOT_SEQUENCE_REGRESSED'
    Require-Restore ([int](Get-RestoreField $Current 'snapshot_count') -ge 1) 'FULL_SNAPSHOT_NOT_VISIBLE'
}

function Assert-PreClosedBaseline($Envelope, [string]$ExpectedRunId, [guid]$SessionId) {
    Require-Restore ($null -ne $Envelope) 'PRE_CLOSED_EVIDENCE_INVALID'
    Require-Restore ([string](Get-RestoreField $Envelope 'run_id') -ceq $ExpectedRunId) `
        'PRE_CLOSED_RUN_ID_MISMATCH'
    Require-Restore ([guid](Get-RestoreField $Envelope 'session_id') -eq $SessionId) `
        'PRE_CLOSED_SESSION_ID_MISMATCH'
    $baseline = Get-RestoreField $Envelope 'baseline'
    $closed = Get-RestoreField $Envelope 'closed'
    Require-Restore ($null -ne $baseline -and $null -ne $closed) 'PRE_CLOSED_EVIDENCE_INVALID'
    Require-Restore ([guid](Get-RestoreField $baseline 'session_id') -eq $SessionId -and
        [long](Get-RestoreField $baseline 'live_ack') -ge 0) 'PRE_CLOSED_BASELINE_INVALID'
    Require-Restore ([guid](Get-RestoreField $closed 'session_id') -eq $SessionId -and
        (Get-RestoreField $closed 'view_closed') -eq $true -and
        (Get-RestoreField $closed 'session_running') -eq $true) 'PRE_CLOSED_VIEW_INVALID'
    return $baseline
}

function Invoke-RestoreProgram([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 15) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Require-Restore ($null -ne $process) 'ADB_START_FAILED'
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw [TimeoutException]::new('ADB_TIMEOUT')
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.Result; Error = $errorOutput.Result }
    } finally { $process.Dispose() }
}

function Invoke-RestoreAdb([string[]]$Arguments, [int]$TimeoutSeconds = 15) {
    Invoke-RestoreProgram $AdbPath (@('-s', $EmulatorSerial) + $Arguments) $TimeoutSeconds
}

function Invoke-RestoreHook([string]$Operation, [hashtable]$Arguments = @{}, [int]$TimeoutSeconds = 180) {
    $requestId = [guid]::NewGuid().ToString('N')
    $responsePath = 'no_backup/tail-first-e2e/response.json'
    $reset = Invoke-RestoreAdb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $responsePath)
    Require-Restore ($reset.ExitCode -eq 0) 'HOOK_RESET_FAILED'
    $broadcast = @('shell', 'am', 'broadcast', '-a', 'dev.termlink.product.debug.TAIL_FIRST_E2E',
        '-n', "$AndroidPackageName/dev.termlink.product.debug.TailFirstE2EReceiver",
        '--es', 'e2e_operation', $Operation, '--es', 'request_id', $requestId,
        '--es', 'run_id', $RunId)
    foreach ($key in @($Arguments.Keys | Sort-Object)) { $broadcast += @('--es', $key, [string]$Arguments[$key]) }
    $sent = Invoke-RestoreAdb $broadcast
    Require-Restore ($sent.ExitCode -eq 0) 'HOOK_BROADCAST_FAILED'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $read = Invoke-RestoreAdb @('exec-out', 'run-as', $AndroidPackageName, 'cat', $responsePath)
        if ($read.ExitCode -eq 0 -and $read.Output.TrimStart().StartsWith('{')) {
            Require-Restore ($read.Output.Length -le 1MB) 'HOOK_RESPONSE_TOO_LARGE'
            $response = $read.Output | ConvertFrom-Json -ErrorAction Stop
            if ([string]$response.request_id -ceq $requestId) {
                Require-Restore ([string]$response.operation -ceq $Operation) 'HOOK_OPERATION_MISMATCH'
                if ($response.ok -ne $true) {
                    $failure = [string]$response.failure_code
                    Require-Restore ($failure -cmatch '^[A-Z0-9_]{1,64}$') 'HOOK_FAILURE_INVALID'
                    throw [InvalidOperationException]::new($failure)
                }
                return $response
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('HOOK_RESPONSE_TIMEOUT')
}

function Read-PreClosedBaseline {
    Require-Restore (-not [string]::IsNullOrWhiteSpace($PreClosedBaselinePath) -and
        [IO.Path]::IsPathFullyQualified($PreClosedBaselinePath) -and
        (Test-Path -LiteralPath $PreClosedBaselinePath -PathType Leaf)) 'PRE_CLOSED_EVIDENCE_REQUIRED'
    $resolved = [IO.Path]::GetFullPath($PreClosedBaselinePath)
    Require-Restore ((Get-Item -LiteralPath $resolved).Length -le 256KB) 'PRE_CLOSED_EVIDENCE_TOO_LARGE'
    try {
        $envelope = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json -ErrorAction Stop
    } catch { throw [InvalidOperationException]::new('PRE_CLOSED_EVIDENCE_INVALID') }
    return Assert-PreClosedBaseline $envelope $RunId $ExpectedSessionId
}

if ($SelfTest) {
    $id = [guid]::NewGuid()
    $hashA = 'a' * 64
    $hashB = 'b' * 64
    $before = [pscustomobject]@{ session_id = "$id"; live_ack = 12; snapshot_sequence_s = 12;
        restore_diagnostics = [pscustomobject]@{ attempt = 1 } }
    $diagnostics = [pscustomobject]@{ attempt = 2; delta_request_count = 1; delta_install_count = 1;
        full_snapshot_install_count = 0;
        base_sha256 = $hashA; target_sha256 = $hashB; base_snapshot_sequence = 12;
        target_snapshot_sequence = 15; delta_expected_chunk_count = 2;
        delta_request_id = [guid]::NewGuid().ToString(); delta_chunks_accepted = 2;
        delta_duplicate_chunks = 1; delta_chunk_acks_sent = 3 }
    $after = [pscustomobject]@{ session_id = "$id"; live_ack = 15; snapshot_sequence_s = 15; realtime_subscriber_ready = $true;
        restore_diagnostics = $diagnostics }
    $actual = Assert-RestoreIdentity $before $after $id
    Assert-DeltaRestore $actual $hashA $hashB $before $after
    $rejected = $false
    try { Assert-DeltaRestore $actual $hashA ('c' * 64) $before $after } catch { $rejected = $true }
    Require-Restore $rejected 'HASH_MISMATCH_NOT_REJECTED'
    $diagnostics.delta_chunk_acks_sent = 2
    $rejected = $false
    try { Assert-DeltaRestore $diagnostics $hashA $hashB $before $after } catch { $rejected = $true }
    Require-Restore $rejected 'MISSING_ACTUAL_ACK_NOT_REJECTED'
    $diagnostics.delta_chunk_acks_sent = 3
    foreach ($mutation in @(
        { param($value) $value.delta_request_count = 2 },
        { param($value) $value.delta_install_count = 2 },
        { param($value) $value.full_snapshot_install_count = 1 },
        { param($value) $value.base_snapshot_sequence = 0 },
        { param($value) $value.base_snapshot_sequence = 13 },
        { param($value) $value.target_snapshot_sequence = 12 },
        { param($value) $value.target_snapshot_sequence = 16 },
        { param($value) $value.delta_expected_chunk_count = 17 },
        { param($value) $value.delta_chunks_accepted = 1 },
        { param($value) $value.delta_duplicate_chunks = -1 },
        { param($value) $value.delta_request_id = [guid]::Empty.ToString() },
        { param($value) $value | Add-Member delta_fallback_reason 'NEEDS_FULL_SNAPSHOT' }
    )) {
        $invalid = $diagnostics | ConvertTo-Json | ConvertFrom-Json
        & $mutation $invalid
        $rejected = $false
        try { Assert-DeltaRestore $invalid $hashA $hashB $before $after } catch { $rejected = $true }
        Require-Restore $rejected 'INVALID_DELTA_RESTORE_NOT_REJECTED'
    }
    $rejected = $false
    $stale = [pscustomobject]@{ session_id = "$id"; live_ack = 15; realtime_subscriber_ready = $true;
        restore_diagnostics = [pscustomobject]@{ attempt = 1 } }
    try { Assert-RestoreIdentity $before $stale $id | Out-Null } catch { $rejected = $true }
    Require-Restore $rejected 'STALE_ATTEMPT_NOT_REJECTED'
    $fallback = [pscustomobject]@{ delta_fallback_reason = 'RETIRED_BASE'; delta_install_count = 0 }
    Assert-FallbackRestore $fallback 'RETIRED_BASE'
    $rejected = $false
    try { Assert-FallbackRestore $fallback 'FAILED' } catch { $rejected = $true }
    Require-Restore $rejected 'FALLBACK_MISMATCH_NOT_REJECTED'
    $fallback | Add-Member -NotePropertyName delta_request_count -NotePropertyValue 1
    $fallback | Add-Member -NotePropertyName base_sha256 -NotePropertyValue $hashA
    $fallback | Add-Member -NotePropertyName full_snapshot_install_count -NotePropertyValue 1
    $fallback | Add-Member -NotePropertyName full_snapshot_sequence -NotePropertyValue 16
    $fallback | Add-Member -NotePropertyName full_snapshot_sha256 -NotePropertyValue $hashB
    $after.snapshot_sequence_s = 16
    $after | Add-Member -NotePropertyName snapshot_count -NotePropertyValue 1
    Assert-FullSnapshotFallback $before $after $fallback $hashA $hashB 16
    foreach ($mutation in @(
        { param($value) $value.delta_request_count = 0 },
        { param($value) $value.base_sha256 = 'c' * 64 },
        { param($value) $value.full_snapshot_install_count = 0 },
        { param($value) $value.full_snapshot_install_count = 2 },
        { param($value) $value.full_snapshot_sequence = 15 },
        { param($value) $value.full_snapshot_sha256 = 'c' * 64 }
    )) {
        $invalid = $fallback | ConvertTo-Json | ConvertFrom-Json
        & $mutation $invalid
        $rejected = $false
        try { Assert-FullSnapshotFallback $before $after $invalid $hashA $hashB 16 } catch { $rejected = $true }
        Require-Restore $rejected 'INVALID_FULL_SNAPSHOT_FALLBACK_NOT_REJECTED'
    }
    $after.snapshot_sequence_s = 11
    $rejected = $false
    try { Assert-FullSnapshotFallback $before $after $fallback $hashA $hashB 16 } catch { $rejected = $true }
    Require-Restore $rejected 'REGRESSED_FULL_SNAPSHOT_NOT_REJECTED'
    $after.snapshot_sequence_s = 16
    $before.snapshot_sequence_s = 16
    $rejected = $false
    try { Assert-FullSnapshotFallback $before $after $fallback $hashA $hashB 16 } catch { $rejected = $true }
    Require-Restore $rejected 'STALE_FULL_SNAPSHOT_TARGET_NOT_REJECTED'
    'Restore E2E assertion self-test passed; no ADB or host was started.'
    return
}

Require-Restore (-not [string]::IsNullOrWhiteSpace($RunId) -and $ExpectedSessionId -ne [guid]::Empty) 'RUN_CONTEXT_REQUIRED'
if (-not [string]::IsNullOrWhiteSpace($PreClosedBaselinePath)) {
    Require-Restore ($Mode -cne 'Wal') 'PRE_CLOSED_BASELINE_UNSUPPORTED'
}
if ($Mode -ceq 'Delta') {
    Require-Restore (-not [string]::IsNullOrWhiteSpace($ExpectedBaseSha256) -and
        -not [string]::IsNullOrWhiteSpace($ExpectedTargetSha256)) 'INDEPENDENT_HASHES_REQUIRED'
} elseif ($Mode -ceq 'Fallback') {
    Require-Restore (-not [string]::IsNullOrWhiteSpace($ExpectedFallbackReason)) 'FALLBACK_REASON_REQUIRED'
}
$verifyFullSnapshot = $Mode -ceq 'Fallback' -and (
    $PSBoundParameters.ContainsKey('ExpectedBaseSha256') -or
    $PSBoundParameters.ContainsKey('ExpectedTargetSha256') -or
    $PSBoundParameters.ContainsKey('ExpectedFullSnapshotSequence'))
if ($verifyFullSnapshot) {
    Require-Restore (-not [string]::IsNullOrWhiteSpace($ExpectedBaseSha256) -and
        -not [string]::IsNullOrWhiteSpace($ExpectedTargetSha256) -and
        $ExpectedFullSnapshotSequence -gt 0) 'FULL_SNAPSHOT_EXPECTATIONS_REQUIRED'
    Require-Restore ($ExpectedFallbackReason -cin @('NEEDS_FULL_SNAPSHOT', 'RETIRED_BASE')) `
        'FULL_SNAPSHOT_REASON_UNSUPPORTED'
}
Require-Restore ($Mode -ceq 'Fallback' -or
    -not $PSBoundParameters.ContainsKey('ExpectedFullSnapshotSequence')) `
    'FULL_SNAPSHOT_SEQUENCE_FALLBACK_ONLY'
Require-Restore (-not [string]::IsNullOrWhiteSpace($AdbPath) -and
    (Test-Path -LiteralPath $AdbPath -PathType Leaf)) 'ADB_PATH_REQUIRED'

$liveHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($RunId))
$liveMarker = 'TLTF_LIVE_' + [Convert]::ToHexString($liveHash, 0, 8).ToLowerInvariant()
$evidence = [ordered]@{
    schema_version = 1; mode = $Mode; run_id = $RunId; session_id = "$ExpectedSessionId"
    status = 'running'; wire_chunk_ack_verified = $false; wal_recovery_verified = $false
    restart_resume_verified = $false; chunk_ack_send_verified = $false
    fault_injection_verified = $false; stage = 'baseline'
    pre_closed_baseline_used = (-not [string]::IsNullOrWhiteSpace($PreClosedBaselinePath))
}
try {
    $baseline = if (-not [string]::IsNullOrWhiteSpace($PreClosedBaselinePath)) {
        $evidence.stage = 'pre-closed-baseline'
        Read-PreClosedBaseline
    } else {
        Invoke-RestoreHook 'inspect' @{ live_marker = $liveMarker }
    }
    Require-Restore ([guid]$baseline.session_id -eq $ExpectedSessionId) 'BASELINE_SESSION_MISMATCH'
    $evidence.baseline_live_ack = [long]$baseline.live_ack
    if ($Mode -ceq 'Wal') {
        $evidence.stage = 'restart-process'
        $stopped = Invoke-RestoreAdb @('shell', 'am', 'force-stop', $AndroidPackageName)
        Require-Restore ($stopped.ExitCode -eq 0) 'ANDROID_FORCE_STOP_FAILED'
        $started = Invoke-RestoreAdb @('shell', 'am', 'start', '-n',
            "$AndroidPackageName/dev.termlink.product.ProductActivity")
        Require-Restore ($started.ExitCode -eq 0) 'ANDROID_RESTART_FAILED'
        $evidence.stage = 'wait-bridge'
        $probeReady = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try {
                $probe = Invoke-RestoreHook 'probe' @{} 10
                if ($probe.account_ready -eq $true) { $probeReady = $true; break }
            } catch {
                if ($_.Exception.Message -cne 'BRIDGE_UNAVAILABLE') { throw }
            }
            Start-Sleep -Milliseconds 500
        }
        Require-Restore $probeReady 'ANDROID_BRIDGE_NOT_READY'
        $evidence.stage = 'reselect-armed-session'
        $reopened = Invoke-RestoreHook 'reselect_armed_session'
        Require-Restore ([guid]$reopened.session_id -eq $ExpectedSessionId) 'RESELECT_SESSION_MISMATCH'
        $current = Invoke-RestoreHook 'inspect' @{ live_marker = $liveMarker }
        Require-Restore ([guid]$current.session_id -eq $ExpectedSessionId -and
            $current.realtime_subscriber_ready -eq $true) 'RESTART_SUBSCRIBER_NOT_READY'
        Require-Restore ([long]$current.live_ack -ge [long]$baseline.live_ack) 'WAL_ACK_REGRESSED'
        Require-Restore ([long]$current.snapshot_sequence_s -ge [long]$baseline.snapshot_sequence_s) `
            'WAL_SNAPSHOT_REGRESSED'
        Require-Restore ([int]$current.snapshot_count -ge 1 -and
            [long](Get-RestoreField $current.restore_diagnostics 'attempt') -gt 0) 'RESTART_HISTORY_NOT_RESTORED'
        Require-Restore ([string]$current.viewport_signature -cmatch '^[A-Za-z0-9:_-]{1,128}$') `
            'RESTART_VIEWPORT_INVALID'
        $evidence.restart_resume_verified = $true
        $evidence.final_live_ack = [long]$current.live_ack
        $evidence.status = 'restart_resume_verified_wal_crash_unverified'
    } else {
        if ([string]::IsNullOrWhiteSpace($PreClosedBaselinePath)) {
            $evidence.stage = 'close-view'
            $closed = Invoke-RestoreHook 'close_view'
            Require-Restore ([guid]$closed.session_id -eq $ExpectedSessionId -and
                $closed.view_closed -eq $true -and $closed.session_running -eq $true) 'VIEW_NOT_CLOSED'
        } else {
            $evidence.stage = 'pre-closed-reopen'
        }
        if (-not [string]::IsNullOrWhiteSpace($AdvanceTriggerPath)) {
            $trigger = [IO.Path]::GetFullPath($AdvanceTriggerPath)
            Require-Restore (-not (Test-Path -LiteralPath $trigger)) 'ADVANCE_TRIGGER_ALREADY_EXISTS'
            [IO.File]::WriteAllText($trigger, $RunId)
        }
        if ($SettleSeconds -gt 0) { Start-Sleep -Seconds $SettleSeconds }
        $evidence.stage = 'reopen-selected'
        $reopened = Invoke-RestoreHook 'reopen_selected'
        $evidence.stage = 'inspect-restore'
        $current = Invoke-RestoreHook 'inspect' @{ live_marker = $liveMarker }
        $diagnostics = Assert-RestoreIdentity $baseline $current $ExpectedSessionId
        Require-Restore ([guid]$reopened.session_id -eq $ExpectedSessionId) 'REOPEN_SESSION_MISMATCH'
        if ($Mode -ceq 'Delta') {
            Assert-DeltaRestore $diagnostics $ExpectedBaseSha256 $ExpectedTargetSha256 $baseline $current
        } else {
            Assert-FallbackRestore $diagnostics $ExpectedFallbackReason
            if ($verifyFullSnapshot) {
                Assert-FullSnapshotFallback $baseline $current $diagnostics $ExpectedBaseSha256 `
                    $ExpectedTargetSha256 $ExpectedFullSnapshotSequence
            }
        }
        $evidence.restore_attempt = [long]$diagnostics.attempt
        $evidence.delta_request_count = [int]$diagnostics.delta_request_count
        $evidence.delta_install_count = [int]$diagnostics.delta_install_count
        $evidence.delta_expected_chunk_count = [int]$diagnostics.delta_expected_chunk_count
        if ($Mode -ceq 'Delta') {
            $evidence.chunk_ack_send_verified = $true
            $evidence.base_snapshot_sequence = [long]$diagnostics.base_snapshot_sequence
            $evidence.base_sha256 = [string]$diagnostics.base_sha256
            $evidence.target_snapshot_sequence = [long]$diagnostics.target_snapshot_sequence
            $evidence.target_sha256 = [string]$diagnostics.target_sha256
            $evidence.full_snapshot_install_count = [int]$diagnostics.full_snapshot_install_count
            $evidence.delta_request_id = $diagnostics.delta_request_id
            $evidence.delta_chunks_accepted = [int]$diagnostics.delta_chunks_accepted
            $evidence.delta_duplicate_chunks = [int]$diagnostics.delta_duplicate_chunks
            $evidence.delta_chunk_acks_sent = [int]$diagnostics.delta_chunk_acks_sent
        }
        $evidence.final_live_ack = [long]$current.live_ack
        if ($verifyFullSnapshot) {
            $evidence.expected_base_sha256 = $ExpectedBaseSha256
            $evidence.expected_target_sha256 = $ExpectedTargetSha256
            $evidence.expected_full_snapshot_sequence = $ExpectedFullSnapshotSequence
            $evidence.fallback_reason = $diagnostics.delta_fallback_reason
            $evidence.base_sha256 = [string]$diagnostics.base_sha256
            $evidence.full_snapshot_install_count = [int]$diagnostics.full_snapshot_install_count
            $evidence.full_snapshot_sequence = [long]$diagnostics.full_snapshot_sequence
            $evidence.full_snapshot_sha256 = [string]$diagnostics.full_snapshot_sha256
            $evidence.final_snapshot_sequence_s = [long]$current.snapshot_sequence_s
        }
        $evidence.status = if ($Mode -ceq 'Delta') { 'delta_and_chunk_ack_send_verified_peer_ack_unverified' } else {
            if ($verifyFullSnapshot) { 'full_snapshot_fallback_verified_base_retirement_unverified' } else {
                'fallback_observed_fault_injection_unverified'
            }
        }
    }
} catch {
    $evidence.status = 'failed'
    $evidence.failure_code = $_.Exception.Message
    throw
} finally {
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $resolved = [IO.Path]::GetFullPath($EvidencePath)
        $directory = [IO.Path]::GetDirectoryName($resolved)
        if (-not [IO.Directory]::Exists($directory)) { $null = [IO.Directory]::CreateDirectory($directory) }
        [IO.File]::WriteAllText($resolved, ($evidence | ConvertTo-Json -Depth 5) + "`n")
    }
}
$evidence | ConvertTo-Json -Depth 5
