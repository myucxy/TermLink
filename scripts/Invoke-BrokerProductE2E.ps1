[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ $_.IsAbsoluteUri -and $_.Scheme -eq 'https' -and $_.AbsolutePath -eq '/' -and
        -not $_.UserInfo -and -not $_.Query -and -not $_.Fragment })]
    [Uri]$AcceptanceOrigin,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,200}$')][string]$AgentControlPipe,
    [Parameter(Mandatory)][string]$SessionHostPath,
    [Parameter(Mandatory)][string]$NativeDisplayPath,
    [ValidatePattern('^emulator-[0-9]{4}$')][string]$EmulatorSerial = 'emulator-5556',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')][string]$AndroidPackageName = 'dev.termlink.harness',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$RunId = ('broker-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' +
        [Guid]::NewGuid().ToString('N').Substring(0, 8)),
    [switch]$ProtocolSmoke,
    [switch]$ProtocolAcceptance,
    [string]$ProtocolReplyChildPath,
    [string]$KittyTransferAcceptanceChildPath,
    [switch]$SnapshotRestoreAcceptance,
    [switch]$ExtendedProtocolAcceptance,
    [switch]$WheelAcceptance,
    [switch]$EffectsAcceptance,
    [string]$AudioRecorderPath,
    [switch]$DiagnosticFailureAcceptance,
    [ValidateSet('unsupported', 'cursor-restore', 'tab-restore')]
    [string]$DiagnosticVector = 'unsupported',
    [switch]$BrokerWalFaultAcceptance,
    [string]$HistoryRoot,
    [switch]$DeltaRestoreAcceptance,
    [switch]$DeltaRetiredBaseAcceptance,
    [string]$DeltaHistoryRoot,
    [guid]$DeltaPcDeviceId,
    [string]$DeltaSnapshotInspectorPath,
    [ValidateRange(0, 2147483647)][int]$DeltaAgentPid = 0,
    [switch]$ImeViewportRegression
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$deltaAcceptance = [bool]($DeltaRestoreAcceptance -or $DeltaRetiredBaseAcceptance)
if ($DeltaRestoreAcceptance -and $DeltaRetiredBaseAcceptance) {
    throw 'DELTA_ACCEPTANCE_MODES_MUTUALLY_EXCLUSIVE'
}
$protocolReplies = -not [string]::IsNullOrWhiteSpace($ProtocolReplyChildPath)
if ($WheelAcceptance -and -not $protocolReplies) { throw 'WHEEL_ACCEPTANCE_REQUIRES_REPLY_CHILD' }
if (-not [string]::IsNullOrWhiteSpace($AudioRecorderPath) -and
    (-not $EffectsAcceptance -or -not [IO.Path]::IsPathFullyQualified($AudioRecorderPath) -or
        -not (Test-Path -LiteralPath $AudioRecorderPath -PathType Leaf) -or
        [IO.Path]::GetExtension($AudioRecorderPath) -ine '.exe')) {
    throw 'AUDIO_RECORDER_REQUIRES_EFFECTS_AND_EXECUTABLE'
}
$kittyTransfers = -not [string]::IsNullOrWhiteSpace($KittyTransferAcceptanceChildPath)
if ($DiagnosticFailureAcceptance -and ($EffectsAcceptance -or $ProtocolAcceptance -or
    $ProtocolSmoke -or $protocolReplies -or $kittyTransfers -or $SnapshotRestoreAcceptance -or
    $ExtendedProtocolAcceptance -or $WheelAcceptance -or $BrokerWalFaultAcceptance -or
    $deltaAcceptance)) { throw 'DIAGNOSTIC_ACCEPTANCE_REQUIRES_INDEPENDENT_RUN' }
if ($DiagnosticVector -cne 'unsupported' -and -not $DiagnosticFailureAcceptance) {
    throw 'DIAGNOSTIC_VECTOR_REQUIRES_INDEPENDENT_RUN'
}
if ($deltaAcceptance -and ($ProtocolSmoke -or $ProtocolAcceptance -or $protocolReplies -or
    $kittyTransfers -or $SnapshotRestoreAcceptance -or $ExtendedProtocolAcceptance -or
    $WheelAcceptance -or $EffectsAcceptance -or $DiagnosticFailureAcceptance -or
    $BrokerWalFaultAcceptance -or $ImeViewportRegression)) {
    throw 'DELTA_RESTORE_REQUIRES_INDEPENDENT_RUN'
}
if ($BrokerWalFaultAcceptance -and ([string]::IsNullOrWhiteSpace($HistoryRoot) -or
    -not [IO.Path]::IsPathFullyQualified($HistoryRoot))) { throw 'BROKER_WAL_HISTORY_ROOT_REQUIRED' }
if ($deltaAcceptance -and ([string]::IsNullOrWhiteSpace($DeltaHistoryRoot) -or
    -not [IO.Path]::IsPathFullyQualified($DeltaHistoryRoot) -or
    -not (Test-Path -LiteralPath $DeltaHistoryRoot -PathType Container))) {
    throw 'DELTA_HISTORY_ROOT_REQUIRED'
}
if ($deltaAcceptance -and ($DeltaPcDeviceId -eq [guid]::Empty)) {
    throw 'DELTA_PC_DEVICE_ID_REQUIRED'
}
if ($deltaAcceptance -and $DeltaAgentPid -le 0) { throw 'DELTA_AGENT_PID_REQUIRED' }
if ($deltaAcceptance -and ([string]::IsNullOrWhiteSpace($DeltaSnapshotInspectorPath) -or
    -not [IO.Path]::IsPathFullyQualified($DeltaSnapshotInspectorPath) -or
    -not (Test-Path -LiteralPath $DeltaSnapshotInspectorPath -PathType Leaf) -or
    [IO.Path]::GetExtension($DeltaSnapshotInspectorPath) -ine '.exe')) {
    throw 'DELTA_SNAPSHOT_INSPECTOR_REQUIRED'
}
if ($SnapshotRestoreAcceptance -or $ExtendedProtocolAcceptance) { $ProtocolAcceptance = $true }
if ($protocolReplies) { $ProtocolAcceptance = $true }
if ($ProtocolAcceptance) {
    $ProtocolSmoke = $true
    . (Join-Path $PSScriptRoot 'Assert-TerminalSemanticProbe.ps1')
}
if ($protocolReplies) { . (Join-Path $PSScriptRoot 'Assert-TerminalReplyProbe.ps1') }
if ($WheelAcceptance) { . (Join-Path $PSScriptRoot 'Assert-TerminalWheelProbe.ps1') }
if ($ExtendedProtocolAcceptance) { . (Join-Path $PSScriptRoot 'Assert-TerminalExtendedProbe.ps1') }
if ($DeltaRestoreAcceptance) { . (Join-Path $PSScriptRoot 'Assert-HostHistoryPeerAck.ps1') }
if ($kittyTransfers -and -not $ProtocolAcceptance) { . (Join-Path $PSScriptRoot 'Assert-TerminalSemanticProbe.ps1') }

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts\broker-product-e2e\' + $RunId)))
if (Test-Path -LiteralPath $runRoot) { throw "Broker Relay E2E run ID already exists: $RunId" }
if ($BrokerWalFaultAcceptance) {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security.SetOwner($currentUser)
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @($currentUser, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,
            [Security.AccessControl.FileSystemRights]::FullControl, $inheritance,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($runRoot), $security)
} else {
    $null = New-Item -ItemType Directory -Path $runRoot
}
$responsePath = 'no_backup/tail-first-e2e/response.json'
$armPath = 'no_backup/tail-first-e2e/session-arm.json'
$action = 'dev.termlink.product.debug.TAIL_FIRST_E2E'
$receiver = 'dev.termlink.product.debug.TailFirstE2EReceiver'
$origin = $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority)
$imagePath = Join-Path $runRoot 'broker-image.rgba'
$deltaImagePath = Join-Path $runRoot 'delta-image.rgba'
$sendTriggerPath = Join-Path $runRoot 'broker-send.trigger'
$protocolTriggerPath = Join-Path $runRoot 'protocol-smoke.trigger'
$replyLaunchTriggerPath = Join-Path $runRoot 'broker-replies.trigger'
$replyTriggerPath = Join-Path $runRoot 'protocol-replies.trigger'
$kittyLaunchTriggerPath = Join-Path $runRoot 'broker-kitty.trigger'
$kittyTriggerPath = Join-Path $runRoot 'kitty-relay.trigger'
$extendedTriggerPath = Join-Path $runRoot 'protocol-extended.trigger'
$deltaTriggerPath = Join-Path $runRoot 'delta-output.trigger'
$exitTriggerPath = Join-Path $runRoot 'broker-exit.trigger'
$evidencePath = Join-Path $runRoot 'evidence.json'
$sessionHost = $null
$displayProcess = $null
$displayStartTime = $null
$stopOwnedHost = $null
$hostOutput = $null
$hostError = $null
$script:lastHookRead = $null
$script:deltaInspectorFailureCount = 0
$stage = 'preflight'
$evidence = [ordered]@{
    schema_version = 1
    run_id = $RunId
    fixture = 'synthetic-powershell7-kitty-file'
    origin = $origin
    emulator = $EmulatorSerial
    status = 'running'
    stage = $stage
    session_id = $null
    baseline_ack = 0
    broker_capture_count = 0
    broker_source_sequence = 0
    final_ack = 0
    magenta_pixels_before = 0
    magenta_pixels_after = 0
    ime_viewport_regression = [bool]$ImeViewportRegression
    ime_visible_before_broker = $false
    ime_visible_after_broker = $false
    host_stopped = $false
    host_forced_stop = $false
    host_exit_code = $null
    host_stdout_tail = $null
    host_stderr_tail = $null
    failure_code = $null
    failure_type = $null
    failure_location = $null
    hook_read = $null
}
if ($ProtocolSmoke) { $evidence.protocol_smoke = @() }
if ($protocolReplies) { $evidence.protocol_replies = @() }
if ($kittyTransfers) { $evidence.kitty_transfers = @() }
if ($ExtendedProtocolAcceptance) { $evidence.protocol_extended = @() }
if ($WheelAcceptance) { $evidence.wheel = [ordered]@{ status = 'not_started' } }
if ($EffectsAcceptance -or $DiagnosticFailureAcceptance) {
    $evidence.effects = [ordered]@{ status = 'not_started';
        diagnostic_vector = $(if ($DiagnosticFailureAcceptance) { $DiagnosticVector } else { $null }) }
}
if ($deltaAcceptance) {
    $evidence.delta_restore = [ordered]@{ status = 'not_started'; base_sequence = $null;
        target_sequence = $null; base_sha256 = $null; target_sha256 = $null;
        android_checkpoint_sequence = $null; android_checkpoint_sha256 = $null;
        android_full_snapshot_install_count = 0;
        restore_evidence = 'delta-restore.json'; retired_base_acceptance = [bool]$DeltaRetiredBaseAcceptance }
}

function Invoke-SnapshotSemanticRestore($Before, [string]$Label) {
    $closed = Invoke-Hook 'close_view' @{} 70
    Require-E2E ([string]$closed.session_id -ceq $evidence.session_id -and
        $closed.view_closed -eq $true -and $closed.session_running -eq $true) 'SNAPSHOT_VIEW_NOT_CLOSED'
    $reopened = Invoke-Hook 'reopen_selected' @{} 180
    Require-E2E ([string]$reopened.session_id -ceq $evidence.session_id -and
        $reopened.realtime_subscriber_ready -eq $true) 'SNAPSHOT_VIEW_NOT_REOPENED'
    $after = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
    Require-E2E ([long]$after.live_ack -ge [long]$Before.live_ack) 'SNAPSHOT_ACK_REGRESSED'
    Assert-TerminalRestoredSemanticProbe -Before $Before.terminal_semantics -After $after.terminal_semantics
    [IO.File]::WriteAllText((Join-Path $runRoot ('restored-' + $Label + '.json')),
        ($after | ConvertTo-Json -Depth 12) + "`n")
    $null = Save-Screenshot ('restored-' + $Label + '.png')
    return $after
}

function Require-E2E([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw [InvalidOperationException]::new($Code) }
}

function Assert-HostCompletion([bool]$Stopped, [Nullable[int]]$ExitCode, [bool]$ForcedStop,
    [bool]$ExpectedDiagnosticFailure) {
    Require-E2E ($Stopped -and -not $ForcedStop -and $null -ne $ExitCode -and
        $ExitCode -eq $(if ($ExpectedDiagnosticFailure) { 125 } else { 0 })) 'HOST_EXIT_NOT_EXPECTED'
}

function Find-OwnedDisplay([Diagnostics.Process]$Owner, [string]$ExpectedPath) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Owner.Id)" |
            Where-Object { $_.ExecutablePath -and
                [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $ExpectedPath,
                    [StringComparison]::OrdinalIgnoreCase) })
        Require-E2E ($children.Count -le 1) 'DISPLAY_IDENTITY_AMBIGUOUS'
        if ($children.Count -eq 1) {
            try {
                $display = [Diagnostics.Process]::GetProcessById([int]$children[0].ProcessId)
                if (-not $display.HasExited -and $display.StartTime -ge $Owner.StartTime) { return $display }
                $display.Dispose()
            } catch [ArgumentException] { }
        }
        if ($Owner.HasExited) { return $null }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'OWNED_DISPLAY_NOT_READY'
}

function Test-OwnedDisplayIdentity($Display, $Identity, [int]$OwnerId,
    [string]$ExpectedPath, [DateTime]$ExpectedStartTime) {
    return ($null -ne $Display -and $null -ne $Identity -and -not $Display.HasExited -and
        $Display.StartTime -eq $ExpectedStartTime -and
        [int]$Identity.ProcessId -eq $Display.Id -and
        [int]$Identity.ParentProcessId -eq $OwnerId -and $Identity.ExecutablePath -and
        [string]::Equals([IO.Path]::GetFullPath([string]$Identity.ExecutablePath), $ExpectedPath,
            [StringComparison]::OrdinalIgnoreCase))
}

function Stop-OwnedDisplay([Diagnostics.Process]$Owner, [Diagnostics.Process]$Display,
    [string]$ExpectedPath, [DateTime]$ExpectedStartTime) {
    Require-E2E ($null -ne $Owner -and $Owner.HasExited) 'HOST_MUST_EXIT_BEFORE_DISPLAY_CLEANUP'
    if ($null -eq $Display) { return $true }
    $Display.Refresh()
    if ($Display.HasExited) { return $true }
    $identity = Get-CimInstance Win32_Process -Filter "ProcessId = $($Display.Id)"
    if (-not (Test-OwnedDisplayIdentity $Display $identity $Owner.Id $ExpectedPath $ExpectedStartTime)) {
        return $false
    }
    $null = $Display.CloseMainWindow()
    if ($Display.WaitForExit(5000)) { return $true }
    $Display.Refresh()
    if ($Display.HasExited) { return $true }
    $identity = Get-CimInstance Win32_Process -Filter "ProcessId = $($Display.Id)"
    if (-not (Test-OwnedDisplayIdentity $Display $identity $Owner.Id $ExpectedPath $ExpectedStartTime)) {
        return $false
    }
    $Display.Kill()
    return $Display.WaitForExit(5000)
}

function Write-ProductTrigger([string]$Path, [string]$Value) {
    $pending = $Path + '.pending'
    Require-E2E (-not [IO.File]::Exists($Path) -and -not [IO.File]::Exists($pending)) 'TRIGGER_ALREADY_PENDING'
    [IO.File]::WriteAllText($pending, $Value)
    [IO.File]::Move($pending, $Path)
}

function Get-DeltaProperty($Object, [string]$Name, [string]$MissingCode) {
    $property = if ($null -eq $Object) { $null } else { $Object.PSObject.Properties[$Name] }
    Require-E2E ($null -ne $property) $MissingCode
    return $property.Value
}

function Assert-DeltaSnapshotTransition($Base, $Candidate) {
    $baseCurrent = Get-DeltaProperty $Base 'current' 'DELTA_BASE_IDENTITY_MISMATCH'
    $candidatePrevious = Get-DeltaProperty $Candidate 'previous' 'DELTA_BASE_IDENTITY_MISMATCH'
    Require-E2E ((Get-DeltaProperty $Candidate 'previous_exists' 'DELTA_BASE_IDENTITY_MISMATCH') -eq $true) `
        'DELTA_BASE_IDENTITY_MISMATCH'
    $baseSequence = [long](Get-DeltaProperty $baseCurrent 'sequence' 'DELTA_BASE_IDENTITY_MISMATCH')
    $baseHash = [string](Get-DeltaProperty $baseCurrent 'sha256' 'DELTA_BASE_IDENTITY_MISMATCH')
    $previousSequence = [long](Get-DeltaProperty $candidatePrevious 'sequence' 'DELTA_BASE_IDENTITY_MISMATCH')
    $previousHash = [string](Get-DeltaProperty $candidatePrevious 'sha256' 'DELTA_BASE_IDENTITY_MISMATCH')
    Require-E2E ($baseSequence -ge 0 -and $baseHash -cmatch '^[0-9a-f]{64}$' -and
        $previousSequence -eq $baseSequence -and $previousHash -ceq $baseHash) 'DELTA_BASE_IDENTITY_MISMATCH'
    $targetCurrent = Get-DeltaProperty $Candidate 'current' 'DELTA_TARGET_IDENTITY_MISMATCH'
    $targetSequence = [long](Get-DeltaProperty $targetCurrent 'sequence' 'DELTA_TARGET_IDENTITY_MISMATCH')
    $targetHash = [string](Get-DeltaProperty $targetCurrent 'sha256' 'DELTA_TARGET_IDENTITY_MISMATCH')
    Require-E2E ($targetSequence -gt $baseSequence -and $targetHash -cmatch '^[0-9a-f]{64}$' -and
        $targetHash -cne $baseHash) 'DELTA_TARGET_IDENTITY_MISMATCH'
}

function Assert-DeltaAndroidCheckpointSeed($AndroidState, $WindowsBase) {
    $diagnostics = Get-DeltaProperty $AndroidState 'restore_diagnostics' `
        'DELTA_ANDROID_RESTORE_DIAGNOSTICS_MISSING'
    $androidSequence = [long](Get-DeltaProperty $AndroidState 'snapshot_sequence_s' `
        'DELTA_ANDROID_CHECKPOINT_SEQUENCE_MISSING')
    $baseCurrent = Get-DeltaProperty $WindowsBase 'current' 'DELTA_BASE_NOT_ANDROID_CHECKPOINT'
    $baseSequence = [long](Get-DeltaProperty $baseCurrent 'sequence' 'DELTA_BASE_NOT_ANDROID_CHECKPOINT')
    $baseHash = [string](Get-DeltaProperty $baseCurrent 'sha256' 'DELTA_BASE_NOT_ANDROID_CHECKPOINT')
    $fullInstallCount = [int](Get-DeltaProperty $diagnostics 'full_snapshot_install_count' `
        'DELTA_ANDROID_FULL_SNAPSHOT_COUNT_MISSING')
    $deltaInstallCount = [int](Get-DeltaProperty $diagnostics 'delta_install_count' `
        'DELTA_ANDROID_DELTA_COUNT_MISSING')
    if ($fullInstallCount -eq 0) {
        $deltaChunks = [int](Get-DeltaProperty $diagnostics 'delta_chunks_accepted' `
            'DELTA_ANDROID_DELTA_COUNT_MISSING')
        $expectedChunks = [int](Get-DeltaProperty $diagnostics 'delta_expected_chunk_count' `
            'DELTA_ANDROID_DELTA_COUNT_MISSING')
        $duplicates = [int](Get-DeltaProperty $diagnostics 'delta_duplicate_chunks' `
            'DELTA_ANDROID_DELTA_COUNT_MISSING')
        $acks = [int](Get-DeltaProperty $diagnostics 'delta_chunk_acks_sent' `
            'DELTA_ANDROID_DELTA_COUNT_MISSING')
        $requests = [int](Get-DeltaProperty $diagnostics 'delta_request_count' `
            'DELTA_ANDROID_DELTA_COUNT_MISSING')
        $requestId = [string](Get-DeltaProperty $diagnostics 'delta_request_id' `
            'DELTA_ANDROID_SEED_IDENTITY_MISSING')
        $seedBaseSequence = [long](Get-DeltaProperty $diagnostics 'base_snapshot_sequence' `
            'DELTA_ANDROID_SEED_IDENTITY_MISSING')
        $seedBaseHash = [string](Get-DeltaProperty $diagnostics 'base_sha256' `
            'DELTA_ANDROID_SEED_IDENTITY_MISSING')
        $targetSequence = [long](Get-DeltaProperty $diagnostics 'target_snapshot_sequence' `
            'DELTA_ANDROID_SEED_IDENTITY_MISSING')
        $targetHash = [string](Get-DeltaProperty $diagnostics 'target_sha256' `
            'DELTA_ANDROID_SEED_IDENTITY_MISSING')
        $previousExists = (Get-DeltaProperty $WindowsBase 'previous_exists' `
            'DELTA_BASE_NOT_ANDROID_CHECKPOINT') -eq $true
        $windowsPrevious = Get-DeltaProperty $WindowsBase 'previous' 'DELTA_BASE_NOT_ANDROID_CHECKPOINT'
        $windowsPreviousSequence = if ($previousExists) {
            [long](Get-DeltaProperty $windowsPrevious 'sequence' 'DELTA_BASE_NOT_ANDROID_CHECKPOINT')
        } else { 0 }
        $windowsPreviousHash = if ($previousExists) {
            [string](Get-DeltaProperty $windowsPrevious 'sha256' 'DELTA_BASE_NOT_ANDROID_CHECKPOINT')
        } else { '' }
        Require-E2E ($requests -eq 1 -and $deltaInstallCount -eq 1 -and
            $null -eq $diagnostics.PSObject.Properties['delta_fallback_reason'] -and
            $expectedChunks -ge 1 -and $expectedChunks -le 16 -and
            $deltaChunks -eq $expectedChunks -and $duplicates -ge 0 -and
            $acks -eq $deltaChunks + $duplicates) 'DELTA_SEED_TRANSFER_INCOMPLETE'
        Require-E2E ([guid]$requestId -ne [guid]::Empty -and
            ([guid]$requestId).ToString('D') -ceq $requestId) 'DELTA_SEED_REQUEST_INVALID'
        Require-E2E ($previousExists -and $seedBaseSequence -gt 0 -and
            $targetSequence -gt $seedBaseSequence -and
            $seedBaseHash -cmatch '^[0-9a-f]{64}$' -and
            $targetHash -cmatch '^[0-9a-f]{64}$' -and $targetHash -cne $seedBaseHash -and
            $seedBaseSequence -eq $windowsPreviousSequence -and
            $seedBaseHash -ceq $windowsPreviousHash -and
            $targetSequence -eq $baseSequence -and $targetHash -ceq $baseHash -and
            $androidSequence -eq $targetSequence) 'DELTA_SEED_IDENTITY_MISMATCH'
        return [pscustomobject]@{
            seed_kind = 'delta'
            full_snapshot_install_count = $fullInstallCount
            delta_install_count = $deltaInstallCount
            sequence = $targetSequence
            sha256 = $targetHash
        }
    }
    Require-E2E ($fullInstallCount -eq 1 -and $deltaInstallCount -eq 0) 'DELTA_ANDROID_FULL_SNAPSHOT_NOT_SEEDED'
    $fullSequence = [long](Get-DeltaProperty $diagnostics 'full_snapshot_sequence' `
        'DELTA_ANDROID_CHECKPOINT_SEQUENCE_MISSING')
    $fullHash = [string](Get-DeltaProperty $diagnostics 'full_snapshot_sha256' `
        'DELTA_ANDROID_CHECKPOINT_HASH_MISSING')
    Require-E2E ($androidSequence -gt 0 -and $fullSequence -eq $androidSequence -and
        $fullHash -cmatch '^[0-9a-f]{64}$') 'DELTA_ANDROID_CHECKPOINT_IDENTITY_MISMATCH'
    Require-E2E ($baseSequence -eq $fullSequence -and $baseHash -cmatch '^[0-9a-f]{64}$' -and
        $baseHash -ceq $fullHash) 'DELTA_BASE_NOT_ANDROID_CHECKPOINT'
    return [pscustomobject]@{
        seed_kind = 'full_snapshot'
        full_snapshot_install_count = $fullInstallCount
        delta_install_count = $deltaInstallCount
        sequence = $fullSequence
        sha256 = $fullHash
    }
}

function New-DeltaPreClosedBaseline($State) {
    $diagnostics = Get-DeltaProperty $State 'restore_diagnostics' 'DELTA_ANDROID_RESTORE_DIAGNOSTICS_MISSING'
    $copy = [ordered]@{ attempt = [long](Get-DeltaProperty $diagnostics 'attempt' 'DELTA_RESTORE_ATTEMPT_MISSING') }
    foreach ($name in @('full_snapshot_install_count', 'delta_install_count', 'delta_chunks_accepted',
        'base_snapshot_sequence', 'base_sha256', 'target_snapshot_sequence', 'target_sha256',
        'full_snapshot_sequence', 'full_snapshot_sha256')) {
        $property = $diagnostics.PSObject.Properties[$name]
        if ($null -ne $property) { $copy[$name] = $property.Value }
    }
    return [ordered]@{
        session_id = [string](Get-DeltaProperty $State 'session_id' 'DELTA_SESSION_ID_MISMATCH')
        live_ack = [long](Get-DeltaProperty $State 'live_ack' 'DELTA_LIVE_ACK_MISSING')
        snapshot_sequence_s = [long](Get-DeltaProperty $State 'snapshot_sequence_s' 'DELTA_ANDROID_CHECKPOINT_SEQUENCE_MISSING')
        restore_diagnostics = $copy
    }
}

function Assert-DeltaSnapshotStable($First, $Second) {
    $firstCurrent = Get-DeltaProperty $First 'current' 'DELTA_PAIR_NOT_STABLE'
    $secondCurrent = Get-DeltaProperty $Second 'current' 'DELTA_PAIR_NOT_STABLE'
    Require-E2E ([long](Get-DeltaProperty $firstCurrent 'sequence' 'DELTA_PAIR_NOT_STABLE') -eq
        [long](Get-DeltaProperty $secondCurrent 'sequence' 'DELTA_PAIR_NOT_STABLE') -and
        [string](Get-DeltaProperty $firstCurrent 'sha256' 'DELTA_PAIR_NOT_STABLE') -ceq
        [string](Get-DeltaProperty $secondCurrent 'sha256' 'DELTA_PAIR_NOT_STABLE')) 'DELTA_PAIR_NOT_STABLE'
}

function Assert-BaseRetired($Base, $First, $Candidate, $Confirm) {
    Assert-DeltaSnapshotStable $Candidate $Confirm
    $baseCurrent = Get-DeltaProperty $Base 'current' 'DELTA_BASE_IDENTITY_MISMATCH'
    $firstCurrent = Get-DeltaProperty $First 'current' 'DELTA_FIRST_TARGET_INVALID'
    $candidateCurrent = Get-DeltaProperty $Candidate 'current' 'DELTA_RETIRED_TARGET_INVALID'
    $previousExists = (Get-DeltaProperty $Candidate 'previous_exists' 'DELTA_BASE_NOT_RETIRED') -eq $true
    Require-E2E ($previousExists -eq
        ((Get-DeltaProperty $Confirm 'previous_exists' 'DELTA_PAIR_NOT_STABLE') -eq $true)) `
        'DELTA_PAIR_NOT_STABLE'
    $candidatePrevious = Get-DeltaProperty $Candidate 'previous' 'DELTA_BASE_NOT_RETIRED'
    $confirmPrevious = Get-DeltaProperty $Confirm 'previous' 'DELTA_PAIR_NOT_STABLE'
    if ($previousExists) {
        Require-E2E ($null -ne $candidatePrevious -and $null -ne $confirmPrevious -and
            [long](Get-DeltaProperty $candidatePrevious 'sequence' 'DELTA_BASE_NOT_RETIRED') -eq
            [long](Get-DeltaProperty $confirmPrevious 'sequence' 'DELTA_PAIR_NOT_STABLE') -and
            [string](Get-DeltaProperty $candidatePrevious 'sha256' 'DELTA_BASE_NOT_RETIRED') -ceq
            [string](Get-DeltaProperty $confirmPrevious 'sha256' 'DELTA_PAIR_NOT_STABLE')) 'DELTA_PAIR_NOT_STABLE'
    } else {
        Require-E2E ($null -eq $candidatePrevious -and $null -eq $confirmPrevious) 'DELTA_PAIR_NOT_STABLE'
    }
    $baseSequence = [long](Get-DeltaProperty $baseCurrent 'sequence' 'DELTA_BASE_IDENTITY_MISMATCH')
    $baseHash = [string](Get-DeltaProperty $baseCurrent 'sha256' 'DELTA_BASE_IDENTITY_MISMATCH')
    $firstSequence = [long](Get-DeltaProperty $firstCurrent 'sequence' 'DELTA_FIRST_TARGET_INVALID')
    $firstHash = [string](Get-DeltaProperty $firstCurrent 'sha256' 'DELTA_FIRST_TARGET_INVALID')
    $targetSequence = [long](Get-DeltaProperty $candidateCurrent 'sequence' 'DELTA_RETIRED_TARGET_INVALID')
    $targetHash = [string](Get-DeltaProperty $candidateCurrent 'sha256' 'DELTA_RETIRED_TARGET_INVALID')
    Require-E2E ($baseSequence -ge 0 -and $baseHash -cmatch '^[0-9a-f]{64}$' -and
        $firstSequence -gt $baseSequence -and $firstHash -cmatch '^[0-9a-f]{64}$' -and
        $firstHash -cne $baseHash) 'DELTA_FIRST_TARGET_INVALID'
    Require-E2E ($targetSequence -gt $firstSequence -and $targetHash -cmatch '^[0-9a-f]{64}$' -and
        $targetHash -cne $firstHash -and $targetHash -cne $baseHash) 'DELTA_BASE_NOT_RETIRED'
    if ($previousExists) {
        $previousSequence = [long](Get-DeltaProperty $candidatePrevious 'sequence' 'DELTA_BASE_NOT_RETIRED')
        $previousHash = [string](Get-DeltaProperty $candidatePrevious 'sha256' 'DELTA_BASE_NOT_RETIRED')
        Require-E2E ($previousSequence -gt $baseSequence -and $previousSequence -lt $targetSequence -and
            $previousHash -cmatch '^[0-9a-f]{64}$' -and $previousHash -cne $baseHash) `
            'DELTA_BASE_NOT_RETIRED'
    }
}

function Assert-DeltaRestoreResult($Result, [guid]$ExpectedSessionId, $Base, $Target) {
    Require-E2E ([guid](Get-DeltaProperty $Result 'session_id' 'DELTA_SESSION_ID_MISMATCH') -eq
        $ExpectedSessionId) 'DELTA_SESSION_ID_MISMATCH'
    Require-E2E ([string](Get-DeltaProperty $Result 'status' 'DELTA_RESTORE_STATUS_INVALID') -ceq
        'delta_and_chunk_ack_send_verified_peer_ack_unverified') 'DELTA_RESTORE_STATUS_INVALID'
    $expectedChunks = [int](Get-DeltaProperty $Result 'delta_expected_chunk_count' 'DELTA_MULTI_CHUNK_NOT_REACHED')
    $installCount = [int](Get-DeltaProperty $Result 'delta_install_count' 'DELTA_NOT_INSTALLED')
    Require-E2E ($installCount -eq 1) 'DELTA_NOT_INSTALLED'
    Require-E2E ($expectedChunks -ge 2 -and $expectedChunks -le 16) 'DELTA_MULTI_CHUNK_NOT_REACHED'
    $accepted = [int](Get-DeltaProperty $Result 'delta_chunks_accepted' 'DELTA_TRANSFER_INCOMPLETE')
    $duplicates = [int](Get-DeltaProperty $Result 'delta_duplicate_chunks' 'DELTA_TRANSFER_INCOMPLETE')
    $acks = [int](Get-DeltaProperty $Result 'delta_chunk_acks_sent' 'DELTA_TRANSFER_INCOMPLETE')
    Require-E2E ($accepted -eq $expectedChunks -and $duplicates -ge 0 -and
        $acks -eq $accepted + $duplicates) 'DELTA_TRANSFER_INCOMPLETE'
    Require-E2E ([long](Get-DeltaProperty $Result 'base_snapshot_sequence' 'DELTA_BASE_IDENTITY_MISMATCH') -eq
        [long]$Base.current.sequence -and
        [string](Get-DeltaProperty $Result 'base_sha256' 'DELTA_BASE_IDENTITY_MISMATCH') -ceq
        [string]$Base.current.sha256) 'DELTA_BASE_IDENTITY_MISMATCH'
    Require-E2E ([long](Get-DeltaProperty $Result 'target_snapshot_sequence' 'DELTA_TARGET_IDENTITY_MISMATCH') -eq
        [long]$Target.current.sequence -and
        [string](Get-DeltaProperty $Result 'target_sha256' 'DELTA_TARGET_IDENTITY_MISMATCH') -ceq
        [string]$Target.current.sha256) 'DELTA_TARGET_IDENTITY_MISMATCH'
}

function Find-WheelControls([xml]$Hierarchy, [bool]$Required) {
    $points = @{}
    foreach ($entry in @(@('up', '鼠标滚轮向上', 'Mouse wheel up'), @('down', '鼠标滚轮向下', 'Mouse wheel down'))) {
        $nodes = @($Hierarchy.SelectNodes('//node') | Where-Object {
            $_.GetAttribute('package') -ceq $AndroidPackageName -and
            $_.GetAttribute('content-desc') -cin @($entry[1], $entry[2]) -and
            $_.GetAttribute('enabled') -ceq 'true'
        })
        Require-E2E ($nodes.Count -le 1 -and ($nodes.Count -eq 1 -or -not $Required)) 'WHEEL_CONTROL_NOT_UNIQUE_OR_VISIBLE'
        if ($nodes.Count -eq 0) { continue }
        Require-E2E ($nodes[0].GetAttribute('bounds') -cmatch '^\[([0-9]+),([0-9]+)\]\[([0-9]+),([0-9]+)\]$') 'WHEEL_CONTROL_BOUNDS'
        $left = [int]$Matches[1]; $top = [int]$Matches[2]; $right = [int]$Matches[3]; $bottom = [int]$Matches[4]
        Require-E2E ($right -gt $left -and $bottom -gt $top) 'WHEEL_CONTROL_EMPTY'
        $points[$entry[0]] = @([int](($left + $right) / 2), [int](($top + $bottom) / 2))
    }
    return $points
}

function Read-WheelControls([bool]$Required) {
    $null = Require-Adb @('shell', 'uiautomator', 'dump', '/sdcard/termlink-wheel-e2e.xml') 'WHEEL_UI_DUMP_FAILED'
    $xml = Require-Adb @('exec-out', 'cat', '/sdcard/termlink-wheel-e2e.xml') 'WHEEL_UI_READ_FAILED'
    Require-E2E ($xml.Length -le 1MB) 'WHEEL_UI_TOO_LARGE'
    return Find-WheelControls ([xml]$xml) $Required
}

function Invoke-Program([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 20) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Require-E2E ($null -ne $process) 'PROCESS_START_FAILED'
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw [TimeoutException]::new('PROCESS_TIMEOUT')
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.Result; Error = $errorOutput.Result }
    } finally { $process.Dispose() }
}

function Invoke-DeltaSnapshotInspector {
    $sessionId = [guid]$evidence.session_id
    for ($retry = 0; $retry -lt 4; $retry++) {
        $result = Invoke-Program $DeltaSnapshotInspectorPath @(
            '--inspect-fixture-snapshots', $DeltaHistoryRoot, $DeltaPcDeviceId.ToString('D'),
            $sessionId.ToString('D')) 30
        if ($result.ExitCode -eq 0) {
            $output = ([string]($result.Output ?? '')).Trim()
            Require-E2E ($output.Length -gt 0 -and $output.Length -le 1MB) 'DELTA_INSPECTOR_JSON_INVALID'
            try { return $output | ConvertFrom-Json -ErrorAction Stop }
            catch { throw [InvalidOperationException]::new('DELTA_INSPECTOR_JSON_INVALID') }
        }
        $script:deltaInspectorFailureCount++
        $stderr = [string]($result.Error ?? '')
        $diagnostic = [ordered]@{ exit_code = $result.ExitCode;
            stderr_length = $stderr.Length;
            stderr_excerpt = $stderr.Substring(0, [Math]::Min($stderr.Length, 4096));
            stdout_length = ([string]($result.Output ?? '')).Length;
            stdout_excerpt = ([string]($result.Output ?? '')).Substring(0,
                [Math]::Min(([string]($result.Output ?? '')).Length, 4096)) }
        [IO.File]::WriteAllText((Join-Path $runRoot ('delta-inspector-failure-{0:D3}.json' -f
            $script:deltaInspectorFailureCount)), ($diagnostic | ConvertTo-Json) + "`n")
        if ($result.ExitCode -ne 3 -or $stderr.Trim() -cne 'snapshot_changed') {
            throw 'DELTA_INSPECTOR_FAILED'
        }
        if ($retry -eq 3) { throw 'DELTA_INSPECTOR_SNAPSHOT_CHANGED' }
        Start-Sleep -Milliseconds 150
    }
}

function Invoke-DeltaSeedViewCycle([int]$Attempt, [bool]$ViewAlreadyClosed) {
    Require-E2E (-not $sessionHost.HasExited) 'DELTA_SEED_HOST_NOT_RUNNING'
    $closed = $null
    if (-not $ViewAlreadyClosed) {
        $closed = Invoke-Hook 'close_view' @{} 70
        Require-E2E ([string]$closed.session_id -ceq $evidence.session_id -and
            $closed.view_closed -eq $true -and $closed.session_running -eq $true) 'DELTA_SEED_VIEW_NOT_CLOSED'
    }
    $reopened = Invoke-Hook 'reopen_selected' @{} 180
    Require-E2E ([string]$reopened.session_id -ceq $evidence.session_id -and
        $reopened.realtime_subscriber_ready -eq $true) 'DELTA_SEED_VIEW_NOT_REOPENED'
    $android = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
    Require-E2E ([string]$android.session_id -ceq $evidence.session_id -and
        $android.realtime_subscriber_ready -eq $true) 'DELTA_SEED_INSPECT_INVALID'
    $windows = Invoke-DeltaSnapshotInspector
    $windowsPath = Join-Path $runRoot ('delta-seed-inspector-{0:D2}.json' -f $Attempt)
    [IO.File]::WriteAllText($windowsPath, ($windows | ConvertTo-Json -Depth 12) + "`n")
    Require-E2E ((Get-Item -LiteralPath $windowsPath).Length -le 256KB) 'DELTA_SEED_INSPECTOR_TOO_LARGE'
    $androidPath = Join-Path $runRoot ('delta-seed-android-{0:D2}.json' -f $Attempt)
    [IO.File]::WriteAllText($androidPath, ($android | ConvertTo-Json -Depth 12) + "`n")
    Require-E2E ((Get-Item -LiteralPath $androidPath).Length -le 256KB) 'DELTA_SEED_ANDROID_EVIDENCE_TOO_LARGE'
    return [pscustomobject]@{
        attempt = $Attempt
        view_was_already_closed = $ViewAlreadyClosed
        closed = $closed
        reopened = $reopened
        android = $android
        windows = $windows
        windows_path = [IO.Path]::GetFileName($windowsPath)
        android_path = [IO.Path]::GetFileName($androidPath)
    }
}

function Close-DeltaSeedView {
    $closed = Invoke-Hook 'close_view' @{} 70
    Require-E2E ([string]$closed.session_id -ceq $evidence.session_id -and
        $closed.view_closed -eq $true -and $closed.session_running -eq $true) 'DELTA_SEED_VIEW_NOT_CLOSED'
    return $closed
}

function Invoke-Adb([string[]]$Arguments, [int]$TimeoutSeconds = 20) {
    Invoke-Program $script:adbPath (@('-s', $EmulatorSerial) + $Arguments) $TimeoutSeconds
}

function Require-Adb([string[]]$Arguments, [string]$Code, [int]$TimeoutSeconds = 20) {
    $result = Invoke-Adb $Arguments $TimeoutSeconds
    Require-E2E ($result.ExitCode -eq 0) $Code
    return $result.Output
}

function Get-SafeHookReadEvidence([string]$Output, [int]$ExitCode, [bool]$JsonParsed, [string]$ParseErrorType = $null) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Output ?? '')
    $prefixLength = [Math]::Min($bytes.Length, 64)
    $prefix = if ($prefixLength -eq 0) { [byte[]]::new(0) } else { [byte[]]$bytes[0..($prefixLength - 1)] }
    return [ordered]@{
        exit_code = $ExitCode
        output_length = $bytes.Length
        output_prefix_hex = [Convert]::ToHexString($prefix).ToLowerInvariant()
        json_parsed = $JsonParsed
        parse_error_type = $ParseErrorType
    }
}

function Start-Hook([string]$Operation, [hashtable]$Arguments = @{}) {
    $requestId = [Guid]::NewGuid().ToString('N')
    $null = Require-Adb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $responsePath) 'HOOK_RESET_FAILED'
    if ($Operation -eq 'open_session') {
        $null = Require-Adb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $armPath) 'HOOK_ARM_RESET_FAILED'
    }
    $broadcast = @('shell', 'am', 'broadcast', '-a', $action, '-n', "$AndroidPackageName/$receiver",
        '--es', 'e2e_operation', $Operation, '--es', 'request_id', $requestId, '--es', 'run_id', $RunId)
    foreach ($key in @($Arguments.Keys | Sort-Object)) { $broadcast += @('--es', $key, [string]$Arguments[$key]) }
    $null = Require-Adb $broadcast 'HOOK_BROADCAST_FAILED'
    return $requestId
}

function Wait-HookFile([string]$RelativePath, [string]$RequestId, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $result = Invoke-Adb @('exec-out', 'run-as', $AndroidPackageName, 'cat', $RelativePath) 10
        $output = [string]($result.Output ?? '')
        $script:lastHookRead = Get-SafeHookReadEvidence $output $result.ExitCode $false
        if ($result.ExitCode -eq 0 -and $output.Length -gt 0) {
            Require-E2E ($output.Length -le 1MB) 'HOOK_RESPONSE_TOO_LARGE'
            $trimmed = $output.TrimStart([char]0xFEFF, [char]0x20, [char]0x09, [char]0x0D, [char]0x0A)
            try {
                $body = $output | ConvertFrom-Json -ErrorAction Stop
            } catch {
                $script:lastHookRead = Get-SafeHookReadEvidence $output $result.ExitCode $false $_.Exception.GetType().FullName
                # `run-as cat` can report a not-yet-created file on stdout while the
                # Activity is starting. Product responses are always JSON objects;
                # malformed JSON objects fail explicitly instead of becoming readiness.
                if ($trimmed.StartsWith('{')) { throw [InvalidOperationException]::new('HOOK_RESPONSE_INVALID') }
                Start-Sleep -Milliseconds 250
                continue
            }
            $script:lastHookRead = Get-SafeHookReadEvidence $output $result.ExitCode $true
            if ($null -ne $body -and $body.PSObject.Properties.Name -contains 'request_id' -and
                [string]$body.request_id -ceq $RequestId) { return $body }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('HOOK_RESPONSE_TIMEOUT')
}

function Invoke-ProbeWithStartupRetry([int]$TimeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { return Invoke-Hook 'probe' }
        catch {
            if ($_.Exception.Message -cne 'BRIDGE_UNAVAILABLE') { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('BRIDGE_STARTUP_TIMEOUT')
}

function Wait-ReplyMarker([string]$Marker, [long]$AfterAck, [string]$ExpectedSessionId) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $candidate = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
        if ([string]$candidate.session_id -ceq $ExpectedSessionId -and
            [long]$candidate.live_ack -gt $AfterAck -and
            (Test-TerminalSemanticMarker $candidate.terminal_semantics $Marker)) { return $candidate }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('REPLY_MARKER_TIMEOUT')
}

function Require-HookResponse($Response, [string]$Operation, [string]$FallbackCode) {
    $response = $Response
    if ($response.ok -ne $true) {
        $failureCode = [string]$response.failure_code
        if ($failureCode -cmatch '^[A-Z0-9_]{1,64}$') {
            throw [InvalidOperationException]::new($failureCode)
        }
        throw [InvalidOperationException]::new($FallbackCode)
    }
    Require-E2E ([string]$response.operation -ceq $Operation) $FallbackCode
}

function Invoke-Hook([string]$Operation, [hashtable]$Arguments = @{}, [int]$TimeoutSeconds = 30) {
    $requestId = Start-Hook $Operation $Arguments
    $response = Wait-HookFile $responsePath $requestId $TimeoutSeconds
    Require-HookResponse $response $Operation 'HOOK_OPERATION_FAILED'
    return $response
}

function Save-Screenshot([string]$Name) {
    $path = Join-Path $runRoot $Name
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:adbPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-s', $EmulatorSerial, 'exec-out', 'screencap', '-p')) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    Require-E2E ($null -ne $process) 'SCREENSHOT_START_FAILED'
    try {
        $file = [IO.File]::Create($path)
        try { $process.StandardOutput.BaseStream.CopyTo($file) } finally { $file.Dispose() }
        Require-E2E ($process.WaitForExit(20000) -and $process.ExitCode -eq 0) 'SCREENSHOT_FAILED'
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
    return $path
}

function Count-MagentaPixels([string]$Path) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new($Path)
    try {
        $count = 0
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.R -eq 255 -and $pixel.G -eq 0 -and $pixel.B -eq 255) { $count++ }
            }
        }
        return $count
    } finally { $bitmap.Dispose() }
}

function Count-NewDrcsPixels([string]$BeforePath, [string]$AfterPath) {
    Add-Type -AssemblyName System.Drawing
    $before = [Drawing.Bitmap]::new($BeforePath)
    $after = [Drawing.Bitmap]::new($AfterPath)
    try {
        Require-E2E ($before.Width -eq $after.Width -and $before.Height -eq $after.Height) 'DRCS_SCREENSHOT_SIZE_CHANGED'
        # Row 4 and the first six cells are inside this portrait terminal region; the unchanged markers are outside it.
        $left = 0
        $right = [int][Math]::Floor($before.Width / 4)
        $top = [int][Math]::Floor($before.Height * 0.15)
        $bottom = [int][Math]::Floor($before.Height * 0.5)
        $count = 0
        for ($y = $top; $y -lt $bottom; $y++) {
            for ($x = $left; $x -lt $right; $x++) {
                $old = $before.GetPixel($x, $y)
                $new = $after.GetPixel($x, $y)
                if ($old.R -ge 200 -and $old.G -ge 200 -and $old.B -ge 200 -and
                    $new.R -le 120 -and $new.G -le 120 -and $new.B -le 120) { $count++ }
            }
        }
        return [pscustomobject]@{ Count = $count; Region = @($left, $top, $right, $bottom) }
    } finally { $after.Dispose(); $before.Dispose() }
}

function Count-NewSixelPixels([string]$BeforePath, [string]$AfterPath) {
    Add-Type -AssemblyName System.Drawing
    $before = [Drawing.Bitmap]::new($BeforePath)
    $after = [Drawing.Bitmap]::new($AfterPath)
    try {
        Require-E2E ($before.Width -eq $after.Width -and $before.Height -eq $after.Height) 'SIXEL_SCREENSHOT_SIZE_CHANGED'
        $region = @(0, [int]($before.Height * 0.15), [int]($before.Width / 4), [int]($before.Height * 0.5))
        $count = 0
        for ($y = $region[1]; $y -lt $region[3]; $y++) {
            for ($x = $region[0]; $x -lt $region[2]; $x++) {
                $old = $before.GetPixel($x, $y)
                $new = $after.GetPixel($x, $y)
                if ($new.R -ge 200 -and $new.G -le 50 -and $new.B -le 50 -and
                    -not ($old.R -ge 200 -and $old.G -le 50 -and $old.B -le 50)) { $count++ }
            }
        }
        return [pscustomobject]@{ Count = $count; Region = $region }
    } finally { $after.Dispose(); $before.Dispose() }
}

function Assert-AgentRelay([string]$AssemblyPath) {
    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $requestType = $assembly.GetType('TermLink.Agent.RegistryRequest', $true)
    $commandType = $assembly.GetType('TermLink.Agent.RegistryCommand', $true)
    $clientType = $assembly.GetType('TermLink.Agent.RegistryControlClient', $true)
    $request = [Activator]::CreateInstance($requestType)
    $request.Command = [Enum]::Parse($commandType, 'RefreshProductionStatus')
    $send = @($clientType.GetMethods() | Where-Object {
        $_.Name -eq 'Send' -and $_.GetParameters().Count -eq 3
    })
    Require-E2E ($send.Count -eq 1) 'AGENT_CONTROL_API_INVALID'
    $response = $send[0].Invoke($null, [object[]]@($AgentControlPipe, $request, 10000))
    Require-E2E ($response.Success -and $response.RemoteAccessEnabled -and
        $response.Production.Configured -and $response.Production.AccountLinked -and
        [string]$response.Production.RelayState -ceq 'Connected' -and
        [string]$response.Production.ServerOrigin -ceq $origin) 'AGENT_RELAY_NOT_READY'
}

try {
    $manifest = Get-Content -LiteralPath 'D:\Dev\DEV_ENV_MANIFEST.json' -Raw | ConvertFrom-Json
    $androidSdk = @($manifest.tools | Where-Object { $_.id -eq 'android-sdk' })
    Require-E2E ($androidSdk.Count -eq 1) 'ANDROID_SDK_MANIFEST_MISSING'
    $script:adbPath = [IO.Path]::GetFullPath((Join-Path $manifest.roots.root $androidSdk[0].probes[0].relativePath))
    Require-E2E (Test-Path -LiteralPath $script:adbPath -PathType Leaf) 'ADB_MISSING'
    $resolvedHost = [IO.Path]::GetFullPath($SessionHostPath)
    $resolvedDisplay = [IO.Path]::GetFullPath($NativeDisplayPath)
    Require-E2E ((Test-Path -LiteralPath $resolvedHost -PathType Leaf) -and
        (Test-Path -LiteralPath $resolvedDisplay -PathType Leaf)) 'WINDOWS_BINARY_MISSING'
    $replyChildBase64 = ''
    if ($protocolReplies) {
        $resolvedReplyChild = [IO.Path]::GetFullPath($ProtocolReplyChildPath)
        Require-E2E (Test-Path -LiteralPath $resolvedReplyChild -PathType Leaf) 'REPLY_CHILD_BINARY_MISSING'
        $replyChildBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($resolvedReplyChild))
    }
    $kittyChildBase64 = ''
    if ($kittyTransfers) {
        $resolvedKittyChild = [IO.Path]::GetFullPath($KittyTransferAcceptanceChildPath)
        Require-E2E (Test-Path -LiteralPath $resolvedKittyChild -PathType Leaf) 'KITTY_CHILD_BINARY_MISSING'
        $kittyChildBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($resolvedKittyChild))
    }
    $controlAssembly = Join-Path (Split-Path -Parent $resolvedHost) 'TermLink.AgentControl.dll'
    if ($deltaAcceptance) {
        Require-E2E (-not [string]::IsNullOrWhiteSpace($env:TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT)) 'DELTA_ISOLATED_ROOT_REQUIRED'
        $deltaAgent = [Diagnostics.Process]::GetProcessById($DeltaAgentPid)
        try {
            Require-E2E (-not $deltaAgent.HasExited -and $deltaAgent.MainModule.FileName -ieq
                (Join-Path (Split-Path -Parent $resolvedHost) 'TermLink.Agent.exe')) 'DELTA_AGENT_IDENTITY_MISMATCH'
        } finally { $deltaAgent.Dispose() }
        $peerAckLog = [IO.Path]::GetFullPath((Join-Path $env:TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT `
            'TermLink/logs/TermLink-background-runtime.log'))
    }
    Require-E2E (Test-Path -LiteralPath $controlAssembly -PathType Leaf) 'AGENT_CONTROL_ASSEMBLY_MISSING'
    Require-E2E ((Require-Adb @('get-state') 'EMULATOR_NOT_RUNNING').Trim() -ceq 'device') 'EMULATOR_NOT_RUNNING'
    Assert-AgentRelay $controlAssembly
    $null = Require-Adb @('shell', 'am', 'start', '-n', "$AndroidPackageName/dev.termlink.product.ProductActivity") 'ANDROID_ACTIVITY_UNAVAILABLE'
    $probe = Invoke-ProbeWithStartupRetry
    Require-E2E ($probe.account_ready -eq $true -and [string]$probe.server_origin -ceq $origin -and
        @($probe.capabilities) -contains 'inspect') 'ANDROID_ORIGIN_OR_ACCOUNT_MISMATCH'
    if ($EffectsAcceptance) {
        # A fresh acceptance install otherwise shows the system notification
        # prompt during the native-image phase and hides the broker render.
        # H02 later changes this to denied through the same adb contract.
        $null = Require-Adb @('shell', 'pm', 'grant', $AndroidPackageName,
            'android.permission.POST_NOTIFICATIONS') 'NOTIFICATION_PRESET_FAILED'
    }

    $stage = 'session-arm'
    $markerStem = 'TLTF_' + $RunId.Replace('-', '_').Replace('.', '_')
    $firstMarker = "${markerStem}_0001"
    $lastMarker = "${markerStem}_5200"
    $liveHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($RunId))
    $liveMarker = 'TLTF_LIVE_' + [Convert]::ToHexString($liveHash, 0, 8).ToLowerInvariant()
    $smokeMarkerPrefix = 'TLPS_' + [Convert]::ToHexString($liveHash, 0, 4).ToLowerInvariant()
    $openId = Start-Hook 'open_session' @{
        first_marker = $firstMarker
        last_marker = $lastMarker
        marker_count = '5200'
        minimum_tail = '0'
        history_bootstrap_mode = 'SnapshotTranscript'
    }
    $arm = Wait-HookFile $armPath $openId 15
    Require-E2E ($arm.armed -eq $true -and [string]$arm.operation -ceq 'open_session') 'SESSION_ARM_FAILED'

    $stage = 'windows-pty'
    $pixels = [byte[]]::new(16 * 16 * 4)
    for ($i = 0; $i -lt $pixels.Length; $i += 4) {
        $pixels[$i] = 255; $pixels[$i + 1] = 0; $pixels[$i + 2] = 255; $pixels[$i + 3] = 255
    }
    [IO.File]::WriteAllBytes($imagePath, $pixels)
    $childScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
function Write-ProtocolSmoke([string]$Phase) {
    $esc = [char]27
    $st = $esc + '\'
    $row = $esc + '[4;1H'
    switch ($Phase) {
        'unicode' { $vector = $row + '中文 🇨🇳 é 👩‍💻 क्‍ष' }
        'line' { $vector = $row + $esc + '#6WIDE' + $esc + '[5;1H' + $esc + '#3TOP' +
            $esc + '[6;1H' + $esc + '#4BOTTOM' }
        'sgr' { $vector = $row + $esc + '[2;4:3;31m' + $esc + '[#{' + $esc + '[0m' +
            $esc + '[#qSTACK' + $esc + '[0m' }
        'pages' { $vector = $row + 'ONE' + $esc + '[2 P' + $esc + '[HTWO' +
            $esc + '[?64l' + $esc + '[1 P' + $row + 'HIDDEN' + $esc + '[?64h' }
        'charset' { $vector = $row + $esc + '%@' + $esc + 'P1!uB' + $st +
            $esc + '-<' + [char]14 + '!' + [char]15 + $esc + '%G' }
        'macro' { $vector = $row + $esc + 'P1;0;1!z4F4B' + $st + $esc + '[1*z' }
        'drcs-baseline' { $vector = '' }
        'drcs' { $vector = $row + $esc + 'P1;1;0;5;1;2;8;0{ @~~~~~' + $st +
            $esc + '( @!!!!!!' + $esc + '(B' }
        'sixel' { $vector = $row + $esc + 'P0;1;0q"1;1;1;6#1;2;100;0;0#1~' + $st }
        'colors' { $vector = $row + $esc + ']17;#123456' + $st +
            $esc + '[1;2;3,|' + $esc + ']0;TermLink smoke' + $st +
            $esc + '[35mCOLOR' + $esc + '[0m' }
        default { throw 'UNKNOWN_PROTOCOL_SMOKE_PHASE' }
    }
    $marker = '__SMOKE_PREFIX___' + $(if ($Phase -eq 'drcs-baseline') { 'DRCS' } else { $Phase.ToUpperInvariant() })
    [Console]::Write($esc + 'c' + $esc + '[2J' + $esc + '[H' + $vector +
        $esc + '(B' + [char]15 + $esc + '[0m' + $esc + '[1;1H' + '__LIVE_MARKER__' +
        $esc + '[2;1H' + $marker)
    [Console]::Out.Flush()
}
if (__DELTA_SEED_MODE__) {
    # Delta starts from the authenticated Android full Snapshot. Keep the
    # seed output in one bounded write so the Host cannot retire that base
    # before Android reports the full-snapshot install and checkpoint hash.
    [Console]::Write(('__MARKER_STEM___0001' + [char]13 + [char]10 +
        '__MARKER_STEM___5200' + [char]13 + [char]10))
} else {
    1..5200 | ForEach-Object { [Console]::WriteLine(('__MARKER_STEM___{0:D4}' -f $_)) }
    [Console]::WriteLine('__LIVE_MARKER__')
}
[Console]::Out.Flush()
while ($true) {
    if ([IO.File]::Exists('broker-exit.trigger')) { break }
    if ([IO.File]::Exists('delta-output.trigger')) {
        $encoded = [IO.File]::ReadAllText('delta-output.trigger').Trim()
        [IO.File]::Delete('delta-output.trigger')
        if ([string]::IsNullOrWhiteSpace($encoded)) { throw 'DELTA_OUTPUT_TRIGGER_EMPTY' }
        try {
            $deltaOutput = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded))
        } catch { throw 'DELTA_OUTPUT_TRIGGER_INVALID' }
        [Console]::Write($deltaOutput)
        [Console]::Out.Flush()
    }
    if ([IO.File]::Exists('broker-send.trigger')) {
        [IO.File]::Delete('broker-send.trigger')
        $path = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('broker-image.rgba'))
        # Keep the recovery marker below the two image rows in the same write after clearing the display.
        [Console]::Write(([char]27 + '[2J' + [char]27 + '[H' +
            [char]27 + '_Ga=T,t=f,f=32,s=16,v=16,S=1024,i=77,q=2,c=4,r=2;' + $path + [char]27 + '\' +
            [char]27 + '[4;1H' + '__LIVE_MARKER__'))
        [Console]::Out.Flush()
    }
    if ([IO.File]::Exists('protocol-smoke.trigger')) {
        $phase = [IO.File]::ReadAllText('protocol-smoke.trigger').Trim()
        [IO.File]::Delete('protocol-smoke.trigger')
        Write-ProtocolSmoke $phase
    }
    if ([IO.File]::Exists('protocol-extended.trigger')) {
        $phase = [IO.File]::ReadAllText('protocol-extended.trigger') | ConvertFrom-Json
        [IO.File]::Delete('protocol-extended.trigger')
        [IO.File]::WriteAllText('effects-received.log', $phase.phase)
        if ($phase.reset_before) { [Console]::Write([char]27 + 'c') }
        [Console]::Write([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($phase.sequence_b64)))
        [Console]::Out.Flush()
    }
    if ([IO.File]::Exists('broker-kitty.trigger')) {
        [IO.File]::Delete('broker-kitty.trigger')
        $kittyChild = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__KITTY_CHILD_PATH_BASE64__'))
        if (-not [IO.Path]::IsPathFullyQualified($kittyChild) -or -not [IO.File]::Exists($kittyChild)) {
            throw 'KITTY_CHILD_PATH_INVALID'
        }
        & $kittyChild --kitty-relay-child ([IO.Directory]::GetCurrentDirectory())
        if ($LASTEXITCODE -ne 0) { throw 'KITTY_CHILD_FAILED' }
    }
    if ([IO.File]::Exists('broker-replies.trigger')) {
        [IO.File]::Delete('broker-replies.trigger')
        $replyChild = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String('__REPLY_CHILD_PATH_BASE64__'))
        if (-not [IO.Path]::IsPathFullyQualified($replyChild) -or -not [IO.File]::Exists($replyChild)) {
            throw 'REPLY_CHILD_PATH_INVALID'
        }
        & $replyChild --protocol-reply-relay-child ([IO.Directory]::GetCurrentDirectory())
        if ($LASTEXITCODE -ne 0) { throw 'REPLY_CHILD_FAILED' }
    }
    Start-Sleep -Milliseconds 100
}
'@.Replace('__MARKER_STEM__', $markerStem).
    Replace('__SMOKE_PREFIX__', $smokeMarkerPrefix).
    Replace('__LIVE_MARKER__', $liveMarker).
    Replace('__DELTA_SEED_MODE__', $(if ($deltaAcceptance) { '$true' } else { '$false' })).
    Replace('__KITTY_CHILD_PATH_BASE64__', $kittyChildBase64).
    Replace('__REPLY_CHILD_PATH_BASE64__', $replyChildBase64)
    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    $childScriptPath = Join-Path $runRoot 'broker-fixture.ps1'
    [IO.File]::WriteAllText($childScriptPath, $childScript, [Text.UTF8Encoding]::new($false))
    $childCommand = '"{0}" -NoLogo -NoProfile -NonInteractive -File "{1}"' -f $pwsh, $childScriptPath
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($childCommand))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $resolvedHost
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--command-base64', $encodedCommand, '--managed-shell', 'powershell-7',
        '--registry-pipe', $AgentControlPipe, '--working-directory-base64',
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($runRoot)),
        '--native-display', $resolvedDisplay, '--terminal-state', 'terminal_state_2_22_v2',
        '--broker-captures', '--diagnostics', '--timeout-ms', '1800000')) { $start.ArgumentList.Add($argument) }
    $sessionHost = [Diagnostics.Process]::Start($start)
    Require-E2E ($null -ne $sessionHost) 'SESSION_HOST_START_FAILED'
    $fixtureHost = $sessionHost
    $fixtureHostId = $sessionHost.Id
    $fixtureHostStart = $sessionHost.StartTime.ToUniversalTime().Ticks
    $fixtureHostPath = $resolvedHost
    $fixtureRunId = $RunId
    $fixtureEvidence = $evidence
    $fixtureReplyTrigger = $replyTriggerPath
    $fixtureKittyTrigger = $kittyTriggerPath
    $fixtureExitTrigger = $exitTriggerPath
    $fixtureReplies = $protocolReplies
    $fixtureKitty = $kittyTransfers
    $stopOwnedHost = {
        if ($fixtureHost.Id -ne $fixtureHostId) { throw 'OWNED_HOST_IDENTITY_MISMATCH' }
        if (-not $fixtureHost.HasExited) {
            if ($fixtureHost.StartTime.ToUniversalTime().Ticks -ne $fixtureHostStart -or
                -not [string]::Equals($fixtureHost.MainModule.FileName, $fixtureHostPath,
                    [StringComparison]::OrdinalIgnoreCase)) { throw 'OWNED_HOST_IDENTITY_MISMATCH' }
            if ($fixtureReplies) { [IO.File]::WriteAllText($fixtureReplyTrigger, 'exit') }
            if ($fixtureKitty) { [IO.File]::WriteAllText($fixtureKittyTrigger, 'exit') }
            [IO.File]::WriteAllText($fixtureExitTrigger, '')
            $fixtureHost.StandardInput.Close()
            if (-not $fixtureHost.WaitForExit(10000)) {
                $fixtureEvidence.host_forced_stop = $true
                $fixtureHost.Kill($true)
                $null = $fixtureHost.WaitForExit(5000)
            }
        }
        [pscustomobject]@{
            run_id = $fixtureRunId; session_id = $fixtureEvidence.session_id
            host_pid = $fixtureHostId; host_start_utc_ticks = $fixtureHostStart
            host_path = $fixtureHostPath; host_exited = $fixtureHost.HasExited
            host_forced_stop = $fixtureEvidence.host_forced_stop
            host_exit_code = $(if ($fixtureHost.HasExited) { $fixtureHost.ExitCode } else { $null })
            exited_at_timestamp = [Diagnostics.Stopwatch]::GetTimestamp()
        }
    }.GetNewClosure()
    $hostOutput = $sessionHost.StandardOutput.ReadToEndAsync()
    $hostError = $sessionHost.StandardError.ReadToEndAsync()
    $displayProcess = Find-OwnedDisplay $sessionHost $resolvedDisplay
    if ($null -ne $displayProcess) {
        $displayStartTime = $displayProcess.StartTime
        $evidence.display_pid = $displayProcess.Id
    }

    $stage = 'android-open'
    $opened = Wait-HookFile $responsePath $openId 180
    Require-HookResponse $opened 'open_session' 'SESSION_OPEN_FAILED'
    Require-E2E ($opened.last_marker_visible -eq $true -and
        $opened.realtime_subscriber_ready -eq $true) 'SESSION_OPEN_FAILED'
    Require-E2E (@($arm.baseline_session_ids) -cnotcontains [string]$opened.session_id) 'SESSION_NOT_ISOLATED'
    $evidence.session_id = [string]$opened.session_id
    $before = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
    Require-E2E ([string]$before.session_id -ceq $evidence.session_id -and
        [int]$before.broker_capture_count -eq 0) 'BROKER_BASELINE_INVALID'
    $evidence.baseline_ack = [long]$before.live_ack
    if ($ImeViewportRegression) {
        $stage = 'ime-viewport'
        $ime = Invoke-Hook 'show_ime'
        Require-E2E ([string]$ime.session_id -ceq $evidence.session_id -and
            $ime.ime_visible -eq $true) 'BROKER_IME_NOT_VISIBLE'
        $evidence.ime_visible_before_broker = [bool]$ime.ime_visible
        $null = Save-Screenshot 'ime-before.png'
    }
    $beforeScreenshot = Save-Screenshot 'before.png'
    $evidence.magenta_pixels_before = Count-MagentaPixels $beforeScreenshot

    if ($deltaAcceptance) {
        $stage = 'delta-seed-alignment'
        $seedAlignment = [ordered]@{ status = 'running'; attempts = @(); selected_attempt = $null }
        $evidence.delta_restore.seed_alignment = $seedAlignment
        $seedAligned = $false
        $closed = $null
        $seedViewClosed = $false
        for ($seedAttempt = 1; $seedAttempt -le 3; $seedAttempt++) {
            $stage = 'delta-seed-cycle-' + $seedAttempt
            $seedCycle = Invoke-DeltaSeedViewCycle $seedAttempt $seedViewClosed
            $attemptEvidence = [ordered]@{
                attempt = $seedAttempt
                android_evidence = $seedCycle.android_path
                windows_inspector = $seedCycle.windows_path
                android_snapshot_sequence = if ($null -eq $seedCycle.android.snapshot_sequence_s) {
                    $null
                } else { [long]$seedCycle.android.snapshot_sequence_s }
                status = 'running'
            }
            $seedAlignment.attempts += $attemptEvidence
            try {
                $androidSeed = Assert-DeltaAndroidCheckpointSeed $seedCycle.android $seedCycle.windows
                $attemptEvidence.status = 'matched'
                $attemptEvidence.seed_kind = $androidSeed.seed_kind
                $attemptEvidence.full_snapshot_install_count = $androidSeed.full_snapshot_install_count
                $attemptEvidence.delta_install_count = $androidSeed.delta_install_count
                $attemptEvidence.sequence = $androidSeed.sequence
                $attemptEvidence.sha256 = $androidSeed.sha256
                $seedAligned = $true
            } catch {
                $failureCode = $_.Exception.Message
                $attemptEvidence.status = 'mismatch'
                $attemptEvidence.failure_code = if ($failureCode -cmatch '^[A-Z0-9_]{1,64}$') {
                    $failureCode
                } else { 'DELTA_SEED_ASSERTION_FAILED' }
                $attemptEvidence.failure_type = $_.Exception.GetType().FullName
                if ($failureCode -notin @(
                    'DELTA_ANDROID_RESTORE_DIAGNOSTICS_MISSING',
                    'DELTA_ANDROID_FULL_SNAPSHOT_COUNT_MISSING',
                    'DELTA_ANDROID_FULL_SNAPSHOT_NOT_SEEDED',
                    'DELTA_ANDROID_CHECKPOINT_SEQUENCE_MISSING',
                    'DELTA_ANDROID_CHECKPOINT_HASH_MISSING',
                    'DELTA_ANDROID_CHECKPOINT_IDENTITY_MISMATCH',
                    'DELTA_BASE_NOT_ANDROID_CHECKPOINT')) { throw }
            }
            $closed = Close-DeltaSeedView
            $seedViewClosed = $true
            if ($seedAligned) {
                $before = $seedCycle.android
                $baseSnapshot = $seedCycle.windows
                $baseCurrent = Get-DeltaProperty $baseSnapshot 'current' 'DELTA_BASE_IDENTITY_MISMATCH'
                $baseSequence = [long](Get-DeltaProperty $baseCurrent 'sequence' 'DELTA_BASE_IDENTITY_MISMATCH')
                $baseHash = [string](Get-DeltaProperty $baseCurrent 'sha256' 'DELTA_BASE_IDENTITY_MISMATCH')
                $seedAlignment.status = 'passed'
                $seedAlignment.selected_attempt = $seedAttempt
                $seedAlignment.sequence = $androidSeed.sequence
                $seedAlignment.sha256 = $androidSeed.sha256
                $seedAlignment.seed_kind = $androidSeed.seed_kind
                $seedAlignment.full_snapshot_install_count = $androidSeed.full_snapshot_install_count
                $seedAlignment.delta_install_count = $androidSeed.delta_install_count
                $evidence.baseline_ack = [long]$before.live_ack
                break
            }
        }
        Require-E2E $seedAligned 'DELTA_ANDROID_SEED_ALIGNMENT_FAILED'
        $preClosedBaselinePath = Join-Path $runRoot 'delta-preclosed.json'
        $baselineEvidence = New-DeltaPreClosedBaseline $before
        $preClosedEnvelope = [ordered]@{
            schema_version = 1; run_id = $RunId; session_id = [string]$evidence.session_id
            baseline = $baselineEvidence
            closed = [ordered]@{
                session_id = [string]$closed.session_id
                view_closed = [bool]$closed.view_closed
                session_running = [bool]$closed.session_running
            }
        }
        [IO.File]::WriteAllText($preClosedBaselinePath,
            ($preClosedEnvelope | ConvertTo-Json -Depth 8) + "`n")
        Require-E2E ((Get-Item -LiteralPath $preClosedBaselinePath).Length -le 256KB) `
            'DELTA_PRE_CLOSED_EVIDENCE_TOO_LARGE'
        $evidence.delta_restore.android_checkpoint_sequence = $androidSeed.sequence
        $evidence.delta_restore.android_checkpoint_sha256 = $androidSeed.sha256
        $evidence.delta_restore.android_seed_kind = $androidSeed.seed_kind
        $evidence.delta_restore.android_full_snapshot_install_count = $androidSeed.full_snapshot_install_count
        $evidence.delta_restore.android_delta_install_count = $androidSeed.delta_install_count

        if ($DeltaRetiredBaseAcceptance) {
            $retiredSnapshots = @()
            $retiredPair = $null
            foreach ($phase in @('first', 'second')) {
                $stage = 'delta-retire-trigger-' + $phase
                $retireMarker = "TL_DELTA_RETIRE_$($phase)_$RunId"
                $retireOutput = ($retireMarker + "`r`n") * 32
                Write-ProductTrigger $deltaTriggerPath ([Convert]::ToBase64String(
                    [Text.Encoding]::ASCII.GetBytes($retireOutput)))
                $stage = 'delta-retire-wait-' + $phase
                $prior = if ($phase -ceq 'first') { $baseSnapshot } else { $retiredSnapshots[0] }
                $priorCurrent = Get-DeltaProperty $prior 'current' 'DELTA_BASE_IDENTITY_MISMATCH'
                $priorSequence = [long](Get-DeltaProperty $priorCurrent 'sequence' 'DELTA_BASE_IDENTITY_MISMATCH')
                $priorHash = [string](Get-DeltaProperty $priorCurrent 'sha256' 'DELTA_BASE_IDENTITY_MISMATCH')
                $stableSnapshot = $null
                $stableReads = 0
                $confirmed = $null
                $deadline = [DateTime]::UtcNow.AddSeconds(120)
                do {
                    Start-Sleep -Milliseconds 500
                    try { $candidate = Invoke-DeltaSnapshotInspector }
                    catch {
                        if ($_.Exception.Message -cne 'DELTA_INSPECTOR_SNAPSHOT_CHANGED') { throw }
                        continue
                    }
                    $candidateCurrent = Get-DeltaProperty $candidate 'current' 'DELTA_RETIRED_TARGET_INVALID'
                    $candidateSequence = [long](Get-DeltaProperty $candidateCurrent 'sequence' 'DELTA_RETIRED_TARGET_INVALID')
                    $candidateHash = [string](Get-DeltaProperty $candidateCurrent 'sha256' 'DELTA_RETIRED_TARGET_INVALID')
                    if ($candidateSequence -le $priorSequence -or $candidateHash -ceq $priorHash) { continue }
                    Require-E2E ($candidateHash -cmatch '^[0-9a-f]{64}$') 'DELTA_RETIRED_TARGET_INVALID'
                    if ($null -eq $stableSnapshot) {
                        $stableSnapshot = $candidate
                        $stableReads = 1
                    } else {
                        try {
                            Assert-DeltaSnapshotStable $stableSnapshot $candidate
                            $stableReads++
                        } catch {
                            if ($_.Exception.Message -cne 'DELTA_PAIR_NOT_STABLE') { throw }
                            $stableSnapshot = $candidate
                            $stableReads = 1
                        }
                    }
                    if ($stableReads -ge 2) {
                        if ($phase -ceq 'second') {
                            try { Assert-BaseRetired $baseSnapshot $retiredSnapshots[0] $stableSnapshot $candidate }
                            catch {
                                if ($_.Exception.Message -cnotin @('DELTA_BASE_NOT_RETIRED', 'DELTA_PAIR_NOT_STABLE')) {
                                    throw
                                }
                                if ($_.Exception.Message -ceq 'DELTA_PAIR_NOT_STABLE') {
                                    $stableSnapshot = $candidate
                                    $stableReads = 1
                                }
                                continue
                            }
                            $retiredPair = @($stableSnapshot, $candidate)
                        }
                        $confirmed = $candidate
                        break
                    }
                } while ([DateTime]::UtcNow -lt $deadline)
                Require-E2E ($null -ne $confirmed) 'DELTA_BASE_RETIREMENT_NOT_OBSERVED'
                $retiredSnapshots += $confirmed
            }
            $retiredTarget = $retiredSnapshots[1]
            Require-E2E ($null -ne $retiredPair -and $retiredPair.Count -eq 2) `
                'DELTA_BASE_RETIREMENT_NOT_OBSERVED'
            Assert-BaseRetired $baseSnapshot $retiredSnapshots[0] $retiredPair[0] $retiredPair[1]
            $targetCurrent = Get-DeltaProperty $retiredTarget 'current' 'DELTA_RETIRED_TARGET_INVALID'
            $targetSequence = [long](Get-DeltaProperty $targetCurrent 'sequence' 'DELTA_RETIRED_TARGET_INVALID')
            $targetHash = [string](Get-DeltaProperty $targetCurrent 'sha256' 'DELTA_RETIRED_TARGET_INVALID')
            $evidence.delta_restore.base_sequence = $baseSequence
            $evidence.delta_restore.first_sequence = [long]$retiredSnapshots[0].current.sequence
            $evidence.delta_restore.target_sequence = $targetSequence
            $evidence.delta_restore.base_sha256 = $baseHash
            $evidence.delta_restore.target_sha256 = $targetHash
            $evidence.delta_restore.retired_base_verified = $true
            $restoreEvidencePath = Join-Path $runRoot 'delta-restore.json'
            $stage = 'delta-retire-restore'
            & (Join-Path $PSScriptRoot 'Invoke-RestoreProductE2E.ps1') -Mode Fallback `
                -RunId $RunId -ExpectedSessionId ([guid]$evidence.session_id) `
                -AdbPath $script:adbPath -AndroidPackageName $AndroidPackageName `
                -EmulatorSerial $EmulatorSerial -ExpectedBaseSha256 $baseHash `
                -ExpectedTargetSha256 $targetHash -ExpectedFullSnapshotSequence $targetSequence `
                -ExpectedFallbackReason NEEDS_FULL_SNAPSHOT -EvidencePath $restoreEvidencePath `
                -PreClosedBaselinePath $preClosedBaselinePath -SettleSeconds 0 | Out-Null
            Require-E2E (Test-Path -LiteralPath $restoreEvidencePath -PathType Leaf) 'DELTA_RESTORE_EVIDENCE_MISSING'
            Require-E2E ((Get-Item -LiteralPath $restoreEvidencePath).Length -le 256KB) 'DELTA_RESTORE_EVIDENCE_TOO_LARGE'
            $retiredResult = Get-Content -LiteralPath $restoreEvidencePath -Raw | ConvertFrom-Json -ErrorAction Stop
            Require-E2E ([guid]$retiredResult.session_id -eq [guid]$evidence.session_id -and
                $retiredResult.status -ceq 'full_snapshot_fallback_verified_base_retirement_unverified') `
                'DELTA_RETIRED_RESTORE_STATUS_INVALID'
            $evidence.delta_restore.status = 'passed'
            $evidence.delta_restore.full_snapshot_install_count = [int]$retiredResult.full_snapshot_install_count
            $evidence.delta_restore.full_snapshot_sequence = [long]$retiredResult.full_snapshot_sequence
        }

        if ($DeltaRestoreAcceptance) {
        # A single short Output captures a large local image atomically; streamed text retires the base.
        $deltaImageBytes = [byte[]]::new(512 * 512 * 4)
        try {
            $seed = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($RunId))
            [Random]::new([BitConverter]::ToInt32($seed, 0)).NextBytes($deltaImageBytes)
            for ($pixel = 3; $pixel -lt $deltaImageBytes.Length; $pixel += 4) {
                $deltaImageBytes[$pixel] = 255
            }
            [IO.File]::WriteAllBytes($deltaImagePath, $deltaImageBytes)
            $evidence.delta_restore.fixture_resource_bytes = $deltaImageBytes.Length
            $evidence.delta_restore.fixture_resource_sha256 =
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($deltaImageBytes)).ToLowerInvariant()
        } finally { [Array]::Clear($deltaImageBytes, 0, $deltaImageBytes.Length) }
        $deltaImageReference = [Convert]::ToBase64String(
            [Text.Encoding]::UTF8.GetBytes('delta-image.rgba'))
        $deltaCommand = [char]27 + '[H' + [char]27 +
            '_Ga=T,t=f,f=32,s=512,v=512,S=1048576,i=78,q=2,c=4,r=2;' +
            $deltaImageReference + [char]27 + '\'
        $deltaPayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($deltaCommand))
        $stage = 'delta-output-trigger'
        Write-ProductTrigger $deltaTriggerPath $deltaPayload

        $stage = 'delta-wait-target'
        $targetSnapshot = $null
        $stableSnapshot = $null
        $stableReads = 0
        $deadline = [DateTime]::UtcNow.AddSeconds(120)
        do {
            Start-Sleep -Milliseconds 500
            try { $candidate = Invoke-DeltaSnapshotInspector }
            catch {
                if ($_.Exception.Message -cne 'DELTA_INSPECTOR_SNAPSHOT_CHANGED') { throw }
                continue
            }
            $transitionReady = $true
            try { Assert-DeltaSnapshotTransition $baseSnapshot $candidate }
            catch {
                if ($_.Exception.Message -notin @('DELTA_BASE_IDENTITY_MISMATCH',
                        'DELTA_TARGET_IDENTITY_MISMATCH')) { throw }
                $transitionReady = $false
            }
            if (-not $transitionReady) { continue }
            if ($null -eq $stableSnapshot) {
                $stableSnapshot = $candidate
                $stableReads = 1
            } else {
                try {
                    Assert-DeltaSnapshotStable $stableSnapshot $candidate
                    $stableReads++
                } catch {
                    if ($_.Exception.Message -cne 'DELTA_PAIR_NOT_STABLE') { throw }
                    $stableSnapshot = $candidate
                    $stableReads = 1
                }
            }
            if ($stableReads -ge 2) {
                $targetSnapshot = $candidate
                break
            }
        } while ([DateTime]::UtcNow -lt $deadline)
        Require-E2E ($null -ne $targetSnapshot) 'DELTA_PAIR_NOT_STABLE'
        Assert-DeltaSnapshotTransition $baseSnapshot $targetSnapshot
        $targetCurrent = Get-DeltaProperty $targetSnapshot 'current' 'DELTA_TARGET_IDENTITY_MISMATCH'
        $targetSequence = [long](Get-DeltaProperty $targetCurrent 'sequence' 'DELTA_TARGET_IDENTITY_MISMATCH')
        $targetHash = [string](Get-DeltaProperty $targetCurrent 'sha256' 'DELTA_TARGET_IDENTITY_MISMATCH')
        $evidence.delta_restore.base_sequence = $baseSequence
        $evidence.delta_restore.target_sequence = $targetSequence
        $evidence.delta_restore.base_sha256 = $baseHash
        $evidence.delta_restore.target_sha256 = $targetHash
        $restoreEvidencePath = Join-Path $runRoot 'delta-restore.json'
        $stage = 'delta-restore'
        & (Join-Path $PSScriptRoot 'Invoke-RestoreProductE2E.ps1') -Mode Delta `
            -RunId $RunId -ExpectedSessionId ([guid]$evidence.session_id) `
            -AdbPath $script:adbPath -AndroidPackageName $AndroidPackageName `
            -EmulatorSerial $EmulatorSerial -ExpectedBaseSha256 $baseHash `
            -ExpectedTargetSha256 $targetHash -EvidencePath $restoreEvidencePath `
            -PreClosedBaselinePath $preClosedBaselinePath `
            -SettleSeconds 0 | Out-Null
        Require-E2E (Test-Path -LiteralPath $restoreEvidencePath -PathType Leaf) 'DELTA_RESTORE_EVIDENCE_MISSING'
        Require-E2E ((Get-Item -LiteralPath $restoreEvidencePath).Length -le 256KB) 'DELTA_RESTORE_EVIDENCE_TOO_LARGE'
        $deltaResult = Get-Content -LiteralPath $restoreEvidencePath -Raw | ConvertFrom-Json -ErrorAction Stop
        Require-E2E ([int]$deltaResult.delta_expected_chunk_count -ge 2) 'DELTA_MULTI_CHUNK_NOT_REACHED'
        Assert-DeltaRestoreResult $deltaResult ([guid]$evidence.session_id) $baseSnapshot $targetSnapshot
        $stage = 'delta-windows-peer-ack'
        $peerAckDeadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            try {
                $peerAck = Assert-HostHistoryPeerAck -LogPath $peerAckLog -AgentPid $DeltaAgentPid `
                    -SessionId ([guid]$evidence.session_id) -RequestId ([guid]$deltaResult.delta_request_id) `
                    -ExpectedChunkCount ([int]$deltaResult.delta_expected_chunk_count)
                break
            } catch {
                if ($_.Exception.Message -cnotin @('PEER_ACK_EVIDENCE_INCOMPLETE', 'PEER_ACK_LOG_READ_TRANSIENT') -or
                    [DateTime]::UtcNow -ge $peerAckDeadline) { throw }
                Start-Sleep -Milliseconds 100
            }
        } while ($true)
        $evidence.delta_restore.windows_peer_ack = $peerAck
        $evidence.delta_restore.status = 'passed'
        $evidence.delta_restore.delta_expected_chunk_count = [int]$deltaResult.delta_expected_chunk_count
        $evidence.delta_restore.delta_chunks_accepted = [int]$deltaResult.delta_chunks_accepted
        $evidence.delta_restore.delta_duplicate_chunks = [int]$deltaResult.delta_duplicate_chunks
        $evidence.delta_restore.delta_chunk_acks_sent = [int]$deltaResult.delta_chunk_acks_sent
        }
    }

    $stage = 'broker-relay'
    if ($BrokerWalFaultAcceptance) {
        Require-E2E (-not $sessionHost.HasExited) 'BROKER_WAL_HOST_NOT_RUNNING'
        & (Join-Path $PSScriptRoot 'Invoke-BrokerWalFaultProductE2E.ps1') -RunId $RunId `
            -ExpectedSessionId ([guid]$evidence.session_id) -ExpectedOrigin $AcceptanceOrigin `
            -HistoryRoot $HistoryRoot -TriggerPath $sendTriggerPath `
            -EvidencePath (Join-Path $runRoot 'broker-wal-fault.json') -AdbPath $script:adbPath `
            -EmulatorSerial $EmulatorSerial -AndroidPackageName $AndroidPackageName | Out-Null
        $fault = Get-Content -LiteralPath (Join-Path $runRoot 'broker-wal-fault.json') -Raw | ConvertFrom-Json
        Require-E2E ($fault.status -ceq 'passed') 'BROKER_WAL_FAULT_NOT_PASSED'
        $evidence.broker_wal_fault = [ordered]@{ status = $fault.status; evidence = 'broker-wal-fault.json' }
    } else {
        [IO.File]::WriteAllText($sendTriggerPath, '')
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $after = $null
    do {
        Start-Sleep -Milliseconds 500
        $candidate = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
        if ([string]$candidate.session_id -ceq $evidence.session_id -and
            [int]$candidate.broker_capture_count -eq 1 -and
            [long]$candidate.broker_capture_last_sequence -gt $evidence.baseline_ack -and
            [long]$candidate.live_ack -ge [long]$candidate.broker_capture_last_sequence) {
            $after = $candidate
            break
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    Require-E2E ($null -ne $after) 'BROKER_FETCH_OR_ACK_TIMEOUT'
    $evidence.broker_capture_count = [int]$after.broker_capture_count
    $evidence.broker_source_sequence = [long]$after.broker_capture_last_sequence
    $evidence.final_ack = [long]$after.live_ack

    $stage = 'native-image'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $afterScreenshot = Save-Screenshot 'after.png'
        $evidence.magenta_pixels_after = Count-MagentaPixels $afterScreenshot
        if ($evidence.magenta_pixels_after -gt $evidence.magenta_pixels_before + 100) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    Require-E2E ($evidence.magenta_pixels_after -gt $evidence.magenta_pixels_before + 100) 'NATIVE_IMAGE_NOT_VISIBLE'
    if ($ImeViewportRegression) {
        $ime = Invoke-Hook 'show_ime'
        Require-E2E ([string]$ime.session_id -ceq $evidence.session_id -and
            $ime.ime_visible -eq $true) 'BROKER_IME_LOST_AFTER_IMAGE'
        $evidence.ime_visible_after_broker = [bool]$ime.ime_visible
    }

    if ($ProtocolSmoke) {
        $previous = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
        $smokePhases = @(
            @{ name = 'unicode'; group = 'C03' }, @{ name = 'line'; group = 'C04' },
            @{ name = 'sgr'; group = 'C06' }, @{ name = 'pages'; group = 'C09' },
            @{ name = 'charset'; group = 'C10' }, @{ name = 'macro'; group = 'C11' },
            @{ name = 'drcs'; group = 'C12' }, @{ name = 'sixel'; group = 'C13' },
            @{ name = 'colors'; group = 'C14' }
        )
        for ($index = 0; $index -lt $smokePhases.Count; $index++) {
            $phase = $smokePhases[$index]
            $stage = 'protocol-smoke-' + $phase.name
            $phaseEvidence = [ordered]@{
                group = $phase.group
                phase = $phase.name
                coverage_kind = 'transport_and_visual_smoke'
                marker = $smokeMarkerPrefix + '_' + $phase.name.ToUpperInvariant()
                ack_before = [long]$previous.live_ack
                ack_after = $null
                signature_before = [string]$previous.visible_text_sha256
                signature_after = $null
                screenshot = $null
                status = 'running'
            }
            if ($ProtocolAcceptance) { $phaseEvidence.coverage_kind = 'native_frame_semantics_with_screenshot_artifact' }
            $evidence.protocol_smoke += $phaseEvidence
            $sixelBaselinePath = if ($phase.name -eq 'sixel') { Save-Screenshot 'protocol-sixel-baseline.png' } else { $null }
            $drcsBaseline = $null
            if ($phase.name -eq 'drcs') {
                [IO.File]::WriteAllText($protocolTriggerPath, 'drcs-baseline')
                $deadline = [DateTime]::UtcNow.AddSeconds(45)
                do {
                    Start-Sleep -Milliseconds 500
                    $candidate = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
                    if ([string]$candidate.session_id -ceq $evidence.session_id -and
                        [long]$candidate.live_ack -gt $phaseEvidence.ack_before -and
                        [string]$candidate.visible_text_sha256 -cne $phaseEvidence.signature_before -and
                        [int]$candidate.broker_capture_count -eq 1) {
                        $drcsBaseline = $candidate
                        break
                    }
                } while ([DateTime]::UtcNow -lt $deadline)
                Require-E2E ($null -ne $drcsBaseline) 'DRCS_BASELINE_TIMEOUT'
                $phaseEvidence.drcs_baseline_ack = [long]$drcsBaseline.live_ack
                $phaseEvidence.drcs_baseline_screenshot = 'protocol-07-drcs-baseline.png'
                $null = Save-Screenshot $phaseEvidence.drcs_baseline_screenshot
            }
            [IO.File]::WriteAllText($protocolTriggerPath, $phase.name)
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            $current = $null
            do {
                Start-Sleep -Milliseconds 500
                $candidate = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
                if ([string]$candidate.session_id -ceq $evidence.session_id -and
                    [long]$candidate.live_ack -gt $phaseEvidence.ack_before -and
                    ($phase.name -eq 'drcs' -or
                        [string]$candidate.visible_text_sha256 -cne $phaseEvidence.signature_before) -and
                    ($null -eq $drcsBaseline -or [long]$candidate.live_ack -gt [long]$drcsBaseline.live_ack) -and
                    $candidate.live_marker_visible -eq $true -and
                    [int]$candidate.broker_capture_count -eq 1) {
                    $current = $candidate
                    break
                }
            } while ([DateTime]::UtcNow -lt $deadline)
            Require-E2E ($null -ne $current) 'PROTOCOL_SMOKE_ACK_OR_RENDER_TIMEOUT'
            $phaseEvidence.ack_after = [long]$current.live_ack
            $phaseEvidence.signature_after = [string]$current.visible_text_sha256
            $phaseEvidence.screenshot = 'protocol-{0:D2}-{1}.png' -f ($index + 1), $phase.name
            $screenshot = Save-Screenshot $phaseEvidence.screenshot
            if ($phase.name -eq 'drcs') {
                do {
                    $pixels = Count-NewDrcsPixels (Join-Path $runRoot $phaseEvidence.drcs_baseline_screenshot) $screenshot
                    $phaseEvidence.drcs_foreground_pixels = $pixels.Count
                    $phaseEvidence.drcs_pixel_region = $pixels.Region
                    if ($pixels.Count -ge 100) { break }
                    Start-Sleep -Milliseconds 500
                    $screenshot = Save-Screenshot $phaseEvidence.screenshot
                } while ([DateTime]::UtcNow -lt $deadline)
                Require-E2E ($phaseEvidence.drcs_foreground_pixels -ge 100) 'DRCS_GLYPH_NOT_VISIBLE'
            }
            if ($phase.name -eq 'sixel') {
                do {
                    $pixels = Count-NewSixelPixels $sixelBaselinePath $screenshot
                    $phaseEvidence.sixel_red_pixels = $pixels.Count
                    $phaseEvidence.sixel_pixel_region = $pixels.Region
                    if ($pixels.Count -gt 0) { break }
                    Start-Sleep -Milliseconds 500
                    $screenshot = Save-Screenshot $phaseEvidence.screenshot
                } while ([DateTime]::UtcNow -lt $deadline)
                Require-E2E ($phaseEvidence.sixel_red_pixels -gt 0) 'SIXEL_IMAGE_NOT_VISIBLE'
            }
            if ($ProtocolAcceptance) {
                Require-E2E ($current.PSObject.Properties.Name -contains 'terminal_semantics') 'SEMANTIC_PROBE_MISSING'
                $probeFile = 'semantic-{0:D2}-{1}.json' -f ($index + 1), $phase.name
                [IO.File]::WriteAllText((Join-Path $runRoot $probeFile),
                    ($current.terminal_semantics | ConvertTo-Json -Depth 12) + "`n")
                $phaseEvidence.semantic_probe = $probeFile
                Assert-TerminalSemanticProbe -Phase $phase.name -Probe $current.terminal_semantics
                $phaseEvidence.status = 'semantic_passed'
            } else {
                $phaseEvidence.status = 'observed'
            }
            if ($SnapshotRestoreAcceptance) {
                $stage = 'snapshot-restore-' + $phase.name
                $current = Invoke-SnapshotSemanticRestore $current $phase.name
                Assert-TerminalSemanticProbe -Phase $phase.name -Probe $current.terminal_semantics
                $phaseEvidence.snapshot_restore = 'semantic_equal_after_engine_recreation'
            }
            $evidence.final_ack = [long]$current.live_ack
            $previous = $current
        }
    }
    if ($ExtendedProtocolAcceptance) {
        $previous = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
        foreach ($definition in @(Get-TerminalExtendedPhaseDefinitions -Columns ([int]$previous.terminal_semantics.columns))) {
            $stage = 'protocol-extended-' + $definition.phase
            $phasePrevious = $previous
            Write-ProductTrigger $extendedTriggerPath ($definition | ConvertTo-Json -Compress)
            $current = Wait-ReplyMarker $definition.expected_marker ([long]$phasePrevious.live_ack) $evidence.session_id
            Assert-TerminalExtendedProbe -Definition $definition -Probe $current.terminal_semantics -PreviousProbe $phasePrevious.terminal_semantics
            $probeFile = 'extended-' + $definition.phase + '.json'
            [IO.File]::WriteAllText((Join-Path $runRoot $probeFile), ($current | ConvertTo-Json -Depth 12) + "`n")
            $screenshotFile = 'extended-' + $definition.phase + '.png'
            $null = Save-Screenshot $screenshotFile
            $phaseEvidence = [ordered]@{
                phase = $definition.phase; ack_before = [long]$phasePrevious.live_ack
                ack_after = [long]$current.live_ack; semantic_probe = $probeFile; screenshot = $screenshotFile
                snapshot_restore = $false; status = 'semantic_passed'
            }
            if ($SnapshotRestoreAcceptance) {
                $stage = 'snapshot-restore-extended-' + $definition.phase
                $restored = Invoke-SnapshotSemanticRestore $current ('extended-' + $definition.phase)
                Assert-TerminalExtendedProbe -Definition $definition -Probe $restored.terminal_semantics `
                    -PreviousProbe $phasePrevious.terminal_semantics
                $current = $restored
                $phaseEvidence.snapshot_restore = 'semantic_equal_after_engine_recreation'
                $phaseEvidence.restored_semantic_probe = 'restored-extended-' + $definition.phase + '.json'
                $phaseEvidence.restored_screenshot = 'restored-extended-' + $definition.phase + '.png'
                $phaseEvidence.restore_ack_after = [long]$current.live_ack
            }
            $evidence.protocol_extended += $phaseEvidence
            $previous = $current
            $evidence.final_ack = [long]$current.live_ack
        }
    }
    if ($kittyTransfers) {
        $stage = 'kitty-transfer-start'
        $previous = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
        $captures = [int]$previous.broker_capture_count
        $pixels = [byte[]]::new(1024)
        for ($index = 0; $index -lt $pixels.Length; $index += 4) {
            $pixels[$index] = 255; $pixels[$index + 2] = 255; $pixels[$index + 3] = 255
        }
        $pixelHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pixels)).ToLowerInvariant()
        Write-ProductTrigger $kittyLaunchTriggerPath ''
        foreach ($phase in @('f', 'mutate-f', 't', 's', 'mutate-s')) {
            $stage = 'kitty-transfer-' + $phase
            Write-ProductTrigger $kittyTriggerPath $phase
            $marker = '__KITTY_RELAY_' + $phase.ToUpperInvariant() + '_DONE__'
            $current = Wait-ReplyMarker $marker ([long]$previous.live_ack) $evidence.session_id
            if ($phase -in @('f', 't', 's')) { $captures++ }
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            while ([int]$current.broker_capture_count -lt $captures -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 250
                $current = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
            }
            Require-E2E ([int]$current.broker_capture_count -eq $captures) 'KITTY_TRANSFER_CAPTURE_COUNT'
            $resources = @($current.terminal_semantics.resources | Where-Object {
                $_.format -ceq 'RGBA8888' -and $_.width -eq 16 -and $_.height -eq 16 -and
                $_.pixel_bytes -eq 1024 -and $_.pixels_sha256 -ceq $pixelHash
            })
            Require-E2E ($resources.Count -eq 1 -and [int]$current.terminal_semantics.image_count -eq 1) 'KITTY_TRANSFER_PIXELS_CHANGED'
            $fixture = Get-Content -LiteralPath (Join-Path $runRoot ('kitty-relay-' + $phase + '.json')) -Raw | ConvertFrom-Json
            Require-E2E ($fixture.phase -ceq $phase -and $fixture.payload_sha256 -ceq $pixelHash) 'KITTY_TRANSFER_FIXTURE_RESULT'
            if ($phase -eq 't') {
                Require-E2E ($fixture.temporary_deleted_after_capture -eq $true -and
                    $fixture.temporary_exists -eq $false) 'KITTY_TEMP_NOT_DELETED'
            }
            if ($phase -eq 's') { Require-E2E ($fixture.shared_registration -ceq 'registered') 'KITTY_SHARED_NOT_REGISTERED' }
            $screenshot = $null
            $visiblePixels = 0
            $visualDeadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                $screenshot = Save-Screenshot ('kitty-' + $phase + '.png')
                if ((Get-Item -LiteralPath $screenshot).Length -gt 0) { $visiblePixels = Count-MagentaPixels $screenshot }
                if ($visiblePixels -ge 100) { break }
                Start-Sleep -Milliseconds 500
            } while ([DateTime]::UtcNow -lt $visualDeadline)
            Require-E2E ($visiblePixels -ge 100) 'KITTY_TRANSFER_NOT_VISIBLE'
            if ($phase -in @('mutate-f', 'mutate-s')) {
                $current = Invoke-SnapshotSemanticRestore $current ('kitty-' + $phase)
                $visiblePixels = Count-MagentaPixels (Join-Path $runRoot ('restored-kitty-' + $phase + '.png'))
                Require-E2E ($visiblePixels -ge 100) 'KITTY_DURABLE_REPLAY_NOT_VISIBLE'
            }
            $evidence.kitty_transfers += [ordered]@{
                phase = $phase; ack_before = [long]$previous.live_ack; ack_after = [long]$current.live_ack
                capture_count = $captures; pixel_sha256 = $pixelHash; visible_pixels = $visiblePixels
                snapshot_restore = ($phase -in @('mutate-f', 'mutate-s')); status = 'semantic_and_visual_passed'
            }
            $previous = $current
            $evidence.final_ack = [long]$current.live_ack
        }
        Write-ProductTrigger $kittyTriggerPath 'exit'
    }
    if ($protocolReplies) {
        $stage = 'protocol-reply-start'
        $previous = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
        [IO.File]::WriteAllText($replyLaunchTriggerPath, '')
        $ready = Wait-ReplyMarker '__PROTOCOL_REPLY_RELAY_READY__' ([long]$previous.live_ack) $evidence.session_id
        $previous = $ready
        $baselineTheme = 0
        foreach ($phase in @('geometry', 'queries', 'state', 'theme',
            'theme-switch-arm', 'theme-switch-read', 'theme-switch-same',
            'theme-switch-unsubscribe', 'theme-switch-restore', 'theme-switch-final')) {
            $stage = 'protocol-reply-' + $phase
            $phaseEvidence = [ordered]@{
                phase = $phase
                coverage_kind = 'real_conpty_stdin_reply_relay'
                json = 'protocol-replies-' + $phase + '.json'
                ack_before = [long]$previous.live_ack
                ack_after = $null
                received_bytes = 0
                geometry_detail_unavailable = $false
                status = 'running'
            }
            $evidence.protocol_replies += $phaseEvidence
            $replyFile = Join-Path $runRoot $phaseEvidence.json
            Require-E2E (-not (Test-Path -LiteralPath $replyFile)) 'REPLY_RESULT_ALREADY_EXISTS'
            if ($phase -in @('theme-switch-read', 'theme-switch-same', 'theme-switch-restore')) {
                Require-E2E ($baselineTheme -in @(1, 2) -and -not $sessionHost.HasExited) 'THEME_SWITCH_HOST_NOT_READY'
                $selectedTheme = if ($phase -ceq 'theme-switch-restore') { $baselineTheme } else { 3 - $baselineTheme }
                $themeSelection = Invoke-Program $resolvedReplyChild @('--select-owned-display-theme',
                    [string]$sessionHost.Id, $resolvedHost, $resolvedDisplay, ('scheme' + $selectedTheme),
                    (Join-Path $runRoot ($phase + '-window.txt'))) 30
                [ordered]@{ exit_code = $themeSelection.ExitCode; stdout = $themeSelection.Output;
                    stderr = $themeSelection.Error } | ConvertTo-Json |
                    Set-Content -LiteralPath (Join-Path $runRoot ($phase + '-driver.json'))
                Require-E2E ($themeSelection.ExitCode -eq 0) 'THEME_SWITCH_REAL_UI_FAILED'
            }
            [IO.File]::WriteAllText($replyTriggerPath, $phase)
            $marker = '__PROTOCOL_REPLY_RELAY_' + $phase.ToUpperInvariant() + '_DONE__'
            $current = Wait-ReplyMarker $marker ([long]$previous.live_ack) $evidence.session_id
            Require-E2E (Test-Path -LiteralPath $replyFile -PathType Leaf) 'REPLY_RESULT_MISSING'
            Require-E2E ((Get-Item -LiteralPath $replyFile).Length -le 256KB) 'REPLY_RESULT_TOO_LARGE'
            $replyProbe = Get-Content -LiteralPath $replyFile -Raw | ConvertFrom-Json -ErrorAction Stop
            $assertion = Assert-TerminalReplyProbe -Phase $phase -Probe $replyProbe -Inspect $current `
                -ExpectedTheme $baselineTheme
            if ($phase -ceq 'theme-switch-arm') {
                Require-E2E ($assertion.theme_scheme -in @(1, 2)) 'THEME_BASELINE_INVALID'
                $baselineTheme = [int]$assertion.theme_scheme
            }
            $phaseEvidence.ack_after = [long]$current.live_ack
            $phaseEvidence.received_bytes = $assertion.received_bytes
            $phaseEvidence.geometry_detail_unavailable = $assertion.geometry_detail_unavailable
            $phaseEvidence.status = 'byte_asserted'
            $evidence.final_ack = [long]$current.live_ack
            $previous = $current
        }
        if ($WheelAcceptance) {
            $stage = 'wheel-arm'
            Write-ProductTrigger $replyTriggerPath 'mouse-arm'
            $armed = Wait-ReplyMarker '__WHEEL_MOUSE_READY__' ([long]$previous.live_ack) $evidence.session_id
            $points = Read-WheelControls $true
            $stage = 'wheel-input'
            Write-ProductTrigger $replyTriggerPath 'mouse-read'
            foreach ($direction in @('up', 'down')) {
                $null = Require-Adb @('shell', 'input', 'tap', [string]$points[$direction][0],
                    [string]$points[$direction][1]) 'WHEEL_UI_TAP_FAILED'
            }
            $read = Wait-ReplyMarker '__WHEEL_MOUSE_READ_DONE__' ([long]$armed.live_ack) $evidence.session_id
            $wheelProbe = Get-Content -LiteralPath (Join-Path $runRoot 'protocol-replies-mouse-read.json') -Raw | ConvertFrom-Json
            $assertion = Assert-TerminalWheelProbe -Probe $wheelProbe -Inspect $read
            $stage = 'wheel-disable'
            Write-ProductTrigger $replyTriggerPath 'nonmouse-arm'
            $disabled = Wait-ReplyMarker '__WHEEL_NONMOUSE_READY__' ([long]$read.live_ack) $evidence.session_id
            Require-E2E ((Read-WheelControls $false).Count -eq 0) 'WHEEL_VISIBLE_OUTSIDE_MOUSE_MODE'
            $evidence.wheel = [ordered]@{
                status = 'real_ui_and_conpty_bytes_passed'; ack_before = [long]$armed.live_ack
                ack_after = [long]$disabled.live_ack; received_bytes = $assertion.received_bytes
                up_count = 1; down_count = 1; controls_hidden_after_disable = $true
            }
        }
        [IO.File]::WriteAllText($replyTriggerPath, 'exit')
    }
    if ($EffectsAcceptance -or $DiagnosticFailureAcceptance) {
        $stage = 'terminal-effects'
        $effectsPath = Join-Path $runRoot 'effects-evidence.json'
        & (Join-Path $PSScriptRoot 'Invoke-TerminalEffectsProductE2E.ps1') -RunId $RunId `
            -ExpectedSessionId ([guid]$evidence.session_id) -ExpectedOrigin $origin `
            -AdbPath $script:adbPath -TriggerPath $extendedTriggerPath -EvidencePath $effectsPath `
            -AndroidPackageName $AndroidPackageName -EmulatorSerial $EmulatorSerial `
            -AudioRecorderPath $AudioRecorderPath `
            -OwnedSessionHost $sessionHost -ExpectedHostPath $resolvedHost -StopOwnedSessionHost $stopOwnedHost `
            -Family $(if ($DiagnosticFailureAcceptance) { 'C02' } else { 'H01H02H03' }) `
            -DiagnosticVector $DiagnosticVector | Out-Null
        $effects = Get-Content -LiteralPath $effectsPath -Raw | ConvertFrom-Json
        Require-E2E ($effects.status -in @('passed', 'partial')) 'EFFECTS_ACCEPTANCE_FAILED'
        $evidence.effects = [ordered]@{ status = $effects.status; evidence = 'effects-evidence.json';
            diagnostic_vector = $(if ($DiagnosticFailureAcceptance) { $DiagnosticVector } else { $null }) }
    }
    $evidence.status = if (($EffectsAcceptance -or $DiagnosticFailureAcceptance) -and
        $evidence.effects.status -ne 'passed') { 'partial' } else { 'passed' }
    $stage = 'complete'
} catch {
    $evidence.status = 'failed'
    $evidence.failure_code = if ($_.Exception.Message -cmatch '^[A-Z0-9_]{1,64}$') {
        $_.Exception.Message
    } else { 'UNEXPECTED_FAILURE' }
    $evidence.failure_type = $_.Exception.GetType().FullName
    # InvocationInfo is not populated for every terminating error (for example
    # an exception raised by a native/async callback).  StrictMode must not turn
    # failure evidence into a second failure while dereferencing MyCommand.
    $invocation = $_.InvocationInfo
    $commandName = ''
    $invocationName = ''
    $lineNumber = 0
    if ($null -ne $invocation) {
        if ($null -ne $invocation.MyCommand) { $commandName = [string]$invocation.MyCommand.Name }
        if ($null -ne $invocation.InvocationName) { $invocationName = [string]$invocation.InvocationName }
        if ($null -ne $invocation.ScriptLineNumber) { $lineNumber = [int]$invocation.ScriptLineNumber }
    }
    $evidence.failure_location = [ordered]@{
        command = $commandName
        function = $invocationName
        line = $lineNumber
    }
    $evidence.hook_read = $script:lastHookRead
    if ($stage -ceq 'android-open' -and $null -ne $script:adbPath) {
        try {
            $failedSurface = Invoke-Hook 'inspect' @{ live_marker = $liveMarker } 15
            [IO.File]::WriteAllText((Join-Path $runRoot 'failed-open-inspect.json'),
                ($failedSurface | ConvertTo-Json -Depth 12) + "`n")
            $null = Save-Screenshot 'failed-open.png'
        } catch { $evidence.failure_inspect_error_type = $_.Exception.GetType().FullName }
    }
} finally {
    if ($null -ne $sessionHost) {
        try {
            if (-not $sessionHost.HasExited) { $null = & $stopOwnedHost }
            $evidence.host_stopped = $sessionHost.HasExited
            if ($evidence.host_stopped) {
                $evidence.host_exit_code = $sessionHost.ExitCode
                foreach ($stream in @(@('host_stdout_tail', $hostOutput), @('host_stderr_tail', $hostError))) {
                    $task = $stream[1]
                    try {
                        if ($null -ne $task -and $task.Wait(2000)) {
                            $value = [string]$task.Result
                            $evidence[$stream[0]] = if ($value.Length -gt 4096) {
                                $value.Substring($value.Length - 4096)
                            } else { $value }
                        }
                    } catch { $evidence[$stream[0]] = '<unavailable>' }
                }
            }
        } catch { $evidence.host_stopped = $false }
        if ($evidence.host_stopped -and $null -ne $displayProcess) {
            try {
                Require-E2E (Stop-OwnedDisplay $sessionHost $displayProcess $resolvedDisplay $displayStartTime) `
                    'OWNED_DISPLAY_IDENTITY_CHANGED'
                $evidence.display_stopped = $true
            } catch {
                $evidence.cleanup_error_code = 'OWNED_DISPLAY_CLEANUP_FAILED'
                if ($evidence.status -in @('passed', 'partial')) {
                    $evidence.status = 'failed'
                    $evidence.failure_code = 'OWNED_DISPLAY_CLEANUP_FAILED'
                }
            }
        }
        $sessionHost.Dispose()
    }
    if ($null -ne $displayProcess) { $displayProcess.Dispose() }
    if ($evidence.status -in @('passed', 'partial')) {
        try { Assert-HostCompletion $evidence.host_stopped $evidence.host_exit_code `
            $evidence.host_forced_stop ([bool]$DiagnosticFailureAcceptance) }
        catch { $evidence.status = 'failed'; $evidence.failure_code = 'HOST_EXIT_NOT_EXPECTED' }
    }
    if (Test-Path -LiteralPath $imagePath -PathType Leaf) { Remove-Item -LiteralPath $imagePath -Force }
    if (Test-Path -LiteralPath $deltaImagePath -PathType Leaf) { Remove-Item -LiteralPath $deltaImagePath -Force }
    if (Test-Path -LiteralPath $sendTriggerPath -PathType Leaf) { Remove-Item -LiteralPath $sendTriggerPath -Force }
    if (Test-Path -LiteralPath $protocolTriggerPath -PathType Leaf) { Remove-Item -LiteralPath $protocolTriggerPath -Force }
    if (Test-Path -LiteralPath $replyLaunchTriggerPath -PathType Leaf) { Remove-Item -LiteralPath $replyLaunchTriggerPath -Force }
    if (Test-Path -LiteralPath $replyTriggerPath -PathType Leaf) { Remove-Item -LiteralPath $replyTriggerPath -Force }
    foreach ($trigger in @($kittyLaunchTriggerPath, $kittyTriggerPath, $extendedTriggerPath, $deltaTriggerPath)) {
        foreach ($path in @($trigger, ($trigger + '.pending'))) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        }
    }
    if ($WheelAcceptance -and $null -ne $script:adbPath) {
        $null = Invoke-Adb @('shell', 'rm', '-f', '/sdcard/termlink-wheel-e2e.xml')
    }
    if (Test-Path -LiteralPath $exitTriggerPath -PathType Leaf) { Remove-Item -LiteralPath $exitTriggerPath -Force }
    $evidence.stage = $stage
    [IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 5) + "`n")
}

if ($evidence.status -ne 'passed' -or -not $evidence.host_stopped) {
    throw "Broker Relay E2E failed ($($evidence.failure_code)); evidence: $evidencePath"
}
Write-Output "Broker Relay E2E passed: $evidencePath"
