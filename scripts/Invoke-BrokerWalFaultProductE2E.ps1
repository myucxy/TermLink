[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunId,
    [guid]$ExpectedSessionId,
    [uri]$ExpectedOrigin,
    [string]$HistoryRoot,
    [string]$TriggerPath,
    [string]$EvidencePath,
    [ValidatePattern('^emulator-[0-9]{4}$')][string]$EmulatorSerial = 'emulator-5556',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')][string]$AndroidPackageName = 'dev.termlink.harness',
    [string]$AdbPath,
    [switch]$SelfTest
)

# Attach to a live Broker fixture only. A directory at the exact next WAL path
# makes its atomic File.Move fail without changing production code or existing WAL.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-WalFault([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw [InvalidOperationException]::new($Code) }
}

function Assert-Arm($Arm, [string]$Id, [guid]$Session) {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    Require-WalFault ($Arm.armed -eq $true -and [int]$Arm.schema_version -eq 1 -and
        [string]$Arm.operation -ceq 'open_session' -and [string]$Arm.run_id -ceq $Id -and
        [guid]$Arm.session_id -eq $Session -and [guid]$Arm.request_id -ne [guid]::Empty -and
        $Session.ToString('D') -notin @($Arm.baseline_session_ids) -and
        $now -ge [long]$Arm.armed_at_epoch_millis -and
        $now - [long]$Arm.armed_at_epoch_millis -le 1800000) 'ARM_MISMATCH'
}

function Assert-Baseline($State, [guid]$Session, [long]$WalSequence, [long[]]$WalFiles) {
    $script:evidence.baseline = [ordered]@{
        session_id = [string]$State.session_id; expected_session_id = $Session.ToString('D');
        realtime_subscriber_ready = [bool]$State.realtime_subscriber_ready;
        live_marker_visible = [bool]$State.live_marker_visible;
        broker_capture_count = [int]$State.broker_capture_count;
        broker_capture_last_sequence = [long]$State.broker_capture_last_sequence;
        live_ack = [long]$State.live_ack; wal_last_sequence = [long]$WalSequence
    }
    $script:evidence.wal_files = @($WalFiles)
    Require-WalFault ([guid]$State.session_id -eq $Session) 'BROKER_FIXTURE_SESSION_MISMATCH'
    Require-WalFault ($State.realtime_subscriber_ready -eq $true) 'BROKER_FIXTURE_NOT_SUBSCRIBED'
    Require-WalFault ($State.live_marker_visible -eq $true) 'BROKER_LIVE_MARKER_NOT_VISIBLE'
    Require-WalFault ([int]$State.broker_capture_count -eq 0) 'BROKER_CAPTURE_ALREADY_ACTIVE'
    Require-WalFault ([long]$State.broker_capture_last_sequence -eq 0) 'BROKER_CAPTURE_SEQUENCE_ACTIVE'
    Require-WalFault ([long]$State.live_ack -ge $WalSequence) 'BROKER_ACK_BEHIND_WAL'
}

function Assert-Recovered($State, [guid]$Session, [long]$SourceSequence, [long]$BeforeAck) {
    Require-WalFault ([guid]$State.session_id -eq $Session -and
        $State.realtime_subscriber_ready -eq $true -and
        $State.live_marker_visible -eq $true -and
        [int]$State.broker_capture_count -eq 1 -and
        [long]$State.broker_capture_last_sequence -eq $SourceSequence -and
        [long]$State.live_ack -gt $BeforeAck -and
        [long]$State.live_ack -ge $SourceSequence) 'BROKER_SOURCE_NOT_CORRELATED'
}

function Assert-LocalDirectory([string]$Path, [string]$Code) {
    Require-WalFault ([IO.Directory]::Exists($Path)) $Code
    $item = Get-Item -LiteralPath $Path -Force
    Require-WalFault ($item.PSIsContainer -and
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) $Code
    $acl = Get-Acl -LiteralPath $Path
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new(
        $acl.GetSecurityDescriptorBinaryForm(), 0)
    Require-WalFault ($null -ne $descriptor.DiscretionaryAcl) $Code
    $current = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $system = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $administrators = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    # Match the trusted owners used by PrivateHistoryDirectory.
    $trusted = @($current, $system, $administrators)
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    Require-WalFault ($null -ne $current -and $null -ne $owner -and
        @($trusted | Where-Object { $_.Equals($owner) }).Count -gt 0) $Code
    $mutation = [int]([Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership)
    foreach ($rule in $acl.GetAccessRules($true, $true,
            [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            (([int]$rule.FileSystemRights -band $mutation) -ne 0) -and
            @($trusted | Where-Object { $_.Equals($rule.IdentityReference) }).Count -eq 0) {
            throw [InvalidOperationException]::new($Code)
        }
    }
}

function Get-WalSequences([string]$Directory) {
    Assert-LocalDirectory $Directory 'WAL_DIRECTORY_UNSAFE'
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    $sequences = [Collections.Generic.List[long]]::new()
    foreach ($entry in $entries) {
        $match = [regex]::Match($entry.Name, '^([0-9]{20})\.wal\.dpapi$',
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        Require-WalFault (-not $entry.PSIsContainer -and
            (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) -and
            $match.Success) 'WAL_NOT_QUIET'
        $sequence = [ulong]::Parse($match.Groups[1].Value, [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture)
        Require-WalFault ($sequence -gt 0 -and $sequence -lt [long]::MaxValue) 'WAL_SEQUENCE_INVALID'
        $sequences.Add([long]$sequence)
    }
    Require-WalFault ($sequences.Count -gt 0 -and
        @($sequences | Sort-Object -Unique).Count -eq $sequences.Count) 'WAL_BASELINE_MISSING'
    return @($sequences | Sort-Object)
}

function Get-CaptureCount([string]$CaptureRoot, [string]$SessionDirectory,
    [switch]$AllowPending) {
    Assert-LocalDirectory $CaptureRoot 'CAPTURE_ROOT_UNSAFE'
    if (-not [IO.Directory]::Exists($SessionDirectory)) { return 0 }
    Assert-LocalDirectory $SessionDirectory 'CAPTURE_DIRECTORY_UNSAFE'
    $entries = @(Get-ChildItem -LiteralPath $SessionDirectory -Force)
    $captures = 0
    foreach ($entry in $entries) {
        Require-WalFault (-not $entry.PSIsContainer -and
            (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) 'CAPTURE_ENTRY_UNSAFE'
        if ($entry.Name -cmatch '^[0-9a-f]{32}\.capture$') {
            Require-WalFault ($entry.Length -gt 0) 'CAPTURE_ENTRY_UNSAFE'
            $captures++
            continue
        }
        Require-WalFault ($AllowPending -and
            $entry.Name -cmatch '^[0-9a-f]{32}\.[0-9a-f]{32}\.pending$') 'CAPTURE_ENTRY_UNSAFE'
    }
    return $captures
}

function Invoke-Program([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 15) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Require-WalFault ($null -ne $process) 'ADB_START_FAILED'
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw [TimeoutException]::new('ADB_TIMEOUT')
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.Result }
    } finally { $process.Dispose() }
}

function Require-Adb([string[]]$Arguments, [string]$Code) {
    $result = Invoke-Program $AdbPath (@('-s', $EmulatorSerial) + $Arguments)
    Require-WalFault ($result.ExitCode -eq 0) $Code
    return $result.Output
}

function Invoke-Hook([string]$Operation) {
    $responsePath = 'no_backup/tail-first-e2e/response.json'
    $requestId = [guid]::NewGuid().ToString('N')
    $null = Require-Adb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $responsePath) 'HOOK_RESET_FAILED'
    $broadcast = @('shell', 'am', 'broadcast', '-a',
        'dev.termlink.product.debug.TAIL_FIRST_E2E', '-n',
        "$AndroidPackageName/dev.termlink.product.debug.TailFirstE2EReceiver",
        '--es', 'e2e_operation', $Operation, '--es', 'request_id', $requestId,
        '--es', 'run_id', $RunId)
    if ($Operation -ceq 'inspect') { $broadcast += @('--es', 'live_marker', $script:liveMarker) }
    $null = Require-Adb $broadcast 'HOOK_BROADCAST_FAILED'
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $read = Invoke-Program $AdbPath @('-s', $EmulatorSerial, 'exec-out', 'run-as',
            $AndroidPackageName, 'cat', $responsePath)
        if ($read.ExitCode -eq 0 -and $read.Output.Length -gt 0) {
            Require-WalFault ($read.Output.Length -le 1MB) 'HOOK_RESPONSE_TOO_LARGE'
            try { $response = $read.Output | ConvertFrom-Json -ErrorAction Stop }
            catch { $response = $null }
            if ($null -ne $response -and [string]$response.request_id -ceq $requestId) {
                Require-WalFault ([string]$response.operation -ceq $Operation -and
                    $response.ok -eq $true) 'HOOK_RESPONSE_FAILED'
                return $response
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('HOOK_RESPONSE_TIMEOUT')
}

$script:evidence = [ordered]@{}
if ($SelfTest) {
    $id = [guid]::NewGuid()
    $arm = [pscustomobject]@{ armed = $true; schema_version = 1; operation = 'open_session';
        run_id = 'broker-selftest'; session_id = "$id"; request_id = "$(New-Guid)";
        baseline_session_ids = @(); armed_at_epoch_millis = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
    Assert-Arm $arm 'broker-selftest' $id
    $baseline = [pscustomobject]@{ session_id = "$id";
        realtime_subscriber_ready = $true; live_marker_visible = $true; broker_capture_count = 0;
        broker_capture_last_sequence = 0; live_ack = 12 }
    Assert-Baseline $baseline $id 12 @(12)
    $recovered = [pscustomobject]@{ session_id = "$id";
        realtime_subscriber_ready = $true; live_marker_visible = $true; broker_capture_count = 1;
        broker_capture_last_sequence = 13; live_ack = 13 }
    Assert-Recovered $recovered $id 13 12
    $arm.baseline_session_ids = @("$id")
    $rejected = $false
    try { Assert-Arm $arm 'broker-selftest' $id } catch { $rejected = $true }
    Require-WalFault $rejected 'BASELINE_SESSION_ACCEPTED'
    $recovered.broker_capture_last_sequence = 14
    $rejected = $false
    try { Assert-Recovered $recovered $id 13 12 } catch { $rejected = $true }
    Require-WalFault $rejected 'UNRELATED_BROKER_SOURCE_ACCEPTED'
    $recovered.broker_capture_last_sequence = 13
    $recovered.PSObject.Properties.Remove('live_marker_visible')
    $rejected = $false
    try { Assert-Recovered $recovered $id 13 12 } catch { $rejected = $true }
    Require-WalFault $rejected 'MISSING_LIVE_MARKER_ACCEPTED'
    $baseline.PSObject.Properties.Remove('realtime_subscriber_ready')
    $rejected = $false
    try { Assert-Baseline $baseline $id 12 @(12) } catch { $rejected = $true }
    Require-WalFault $rejected 'MISSING_SUBSCRIBER_ACCEPTED'
    $selfRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(),
        'termlink-broker-wal-acl-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($selfRoot) | Out-Null
        $acl = Get-Acl -LiteralPath $selfRoot
        $acl.SetAccessRuleProtection($true, $false)
        $current = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $system = [Security.Principal.SecurityIdentifier]::new(
            [Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
        $administrators = [Security.Principal.SecurityIdentifier]::new(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        foreach ($sid in @($current, $system, $administrators)) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.InheritanceFlags]::ContainerInherit,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $selfRoot -AclObject $acl
        Assert-LocalDirectory $selfRoot 'SELFTEST_PRIVATE_DIRECTORY_REJECTED'
        $users = [Security.Principal.SecurityIdentifier]::new(
            [Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,
            [Security.AccessControl.FileSystemRights]::Modify,
            [Security.AccessControl.AccessControlType]::Allow))
        Set-Acl -LiteralPath $selfRoot -AclObject $acl
        $rejected = $false
        try { Assert-LocalDirectory $selfRoot 'FOREIGN_WRITE_ACL_ACCEPTED' }
        catch { $rejected = $true }
        Require-WalFault $rejected 'FOREIGN_WRITE_ACL_ACCEPTED'
    } finally {
        Require-WalFault ([string]::Equals([IO.Path]::GetDirectoryName($selfRoot),
            [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()),
            [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($selfRoot).StartsWith('termlink-broker-wal-acl-',
                [StringComparison]::Ordinal)) 'SELFTEST_CLEANUP_SCOPE_INVALID'
        if ([IO.Directory]::Exists($selfRoot)) { [IO.Directory]::Delete($selfRoot, $false) }
    }
    'Broker WAL fault assertion self-test passed; no ADB or host was started.'
    return
}

Require-WalFault (-not [string]::IsNullOrWhiteSpace($RunId) -and
    $ExpectedSessionId -ne [guid]::Empty -and $null -ne $ExpectedOrigin -and
    $ExpectedOrigin.IsAbsoluteUri -and $ExpectedOrigin.Scheme -ceq 'https' -and
    $ExpectedOrigin.AbsolutePath -ceq '/' -and -not $ExpectedOrigin.UserInfo -and
    -not $ExpectedOrigin.Query -and -not $ExpectedOrigin.Fragment) 'RUN_CONTEXT_REQUIRED'
Require-WalFault (-not [string]::IsNullOrWhiteSpace($AdbPath) -and
    [IO.File]::Exists($AdbPath)) 'ADB_PATH_REQUIRED'
Require-WalFault (-not [string]::IsNullOrWhiteSpace($HistoryRoot) -and
    [IO.Path]::IsPathFullyQualified($HistoryRoot) -and
    -not [string]::IsNullOrWhiteSpace($TriggerPath) -and
    [IO.Path]::IsPathFullyQualified($TriggerPath) -and
    -not [string]::IsNullOrWhiteSpace($EvidencePath) -and
    [IO.Path]::IsPathFullyQualified($EvidencePath)) 'PATH_CONTEXT_REQUIRED'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts\broker-product-e2e\' + $RunId)))
$trigger = [IO.Path]::GetFullPath($TriggerPath)
$evidenceFile = [IO.Path]::GetFullPath($EvidencePath)
Require-WalFault ([string]::Equals([IO.Path]::GetDirectoryName($trigger), $runRoot,
        [StringComparison]::OrdinalIgnoreCase) -and
    [IO.Path]::GetFileName($trigger) -ceq 'broker-send.trigger' -and
    [string]::Equals([IO.Path]::GetDirectoryName($evidenceFile), $runRoot,
        [StringComparison]::OrdinalIgnoreCase) -and
    [IO.Path]::GetExtension($evidenceFile) -ceq '.json') 'EVIDENCE_SCOPE_INVALID'
Assert-LocalDirectory $runRoot 'RUN_DIRECTORY_UNSAFE'
Require-WalFault (-not [IO.File]::Exists($trigger) -and
    -not [IO.File]::Exists($trigger + '.pending') -and
    -not [IO.File]::Exists($evidenceFile)) 'RUN_FILE_ALREADY_EXISTS'
$history = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($HistoryRoot))
Require-WalFault ($history -notmatch '[\[\]*?]') 'HISTORY_PATH_WILDCARD_UNSAFE'
$sessionDirectory = [IO.Path]::Combine($history, $ExpectedSessionId.ToString('N'))
$walDirectory = [IO.Path]::Combine($sessionDirectory, 'wal')
$captureRoot = [IO.Path]::Combine($history, 'broker-captures')
$captureDirectory = [IO.Path]::Combine($captureRoot, $ExpectedSessionId.ToString('N'))
$blockedWalPath = $null
$createdWalDirectory = $false
$createdPending = $false
$createdTrigger = $false
$failureCode = $null
$cleanupCode = $null
$evidence = [ordered]@{ schema_version = 1; run_id = $RunId;
    session_id = $ExpectedSessionId.ToString('D'); origin = $ExpectedOrigin.GetLeftPart([UriPartial]::Authority);
    status = 'running'; stage = 'preflight'; wal_sequence_before = $null; blocked_sequence = $null;
    ack_before = $null; ack_during_fault = $null; ack_after = $null;
    sidecars_before = $null; sidecars_during_fault = $null; broker_resolved_count = $null;
    broker_source_sequence = $null; failure_code = $null; cleanup_code = $null }
$liveHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($RunId))
$script:liveMarker = 'TLTF_LIVE_' + [Convert]::ToHexString($liveHash, 0, 8).ToLowerInvariant()
try {
    Assert-LocalDirectory $history 'HISTORY_ROOT_UNSAFE'
    Assert-LocalDirectory $sessionDirectory 'SESSION_DIRECTORY_UNSAFE'
    Assert-LocalDirectory $walDirectory 'WAL_DIRECTORY_UNSAFE'
    Assert-LocalDirectory $captureRoot 'CAPTURE_ROOT_UNSAFE'
    if ([IO.Directory]::Exists($captureDirectory)) {
        Assert-LocalDirectory $captureDirectory 'CAPTURE_DIRECTORY_UNSAFE'
    }
    $armText = Require-Adb @('exec-out', 'run-as', $AndroidPackageName, 'cat',
        'no_backup/tail-first-e2e/session-arm.json') 'ARM_READ_FAILED'
    Require-WalFault ($armText.Length -gt 0 -and $armText.Length -le 131072) 'ARM_SIZE_INVALID'
    Assert-Arm ($armText | ConvertFrom-Json -ErrorAction Stop) $RunId $ExpectedSessionId
    $probe = Invoke-Hook 'probe'
    Require-WalFault ($probe.account_ready -eq $true -and
        [string]$probe.server_origin -ceq $evidence.origin) 'SERVER_OR_ACCOUNT_MISMATCH'
    $before = $null
    $sequences = @()
    $last = 0L
    $lastAck = -1L
    $stable = 0
    $stableDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $sequences = @(Get-WalSequences $walDirectory)
        $before = Invoke-Hook 'inspect' @{ live_marker = $script:liveMarker }
        $candidateLast = [long]$sequences[-1]
        $candidateAck = [long]$before.live_ack
        if ($sequences.Count -gt 0 -and $candidateLast -eq $last -and
            $candidateAck -ge $candidateLast -and $candidateAck -eq $lastAck -and
            [int]$before.broker_capture_count -eq 0) {
            $stable++
        } else {
            $stable = 0
        }
        $last = $candidateLast
        $lastAck = $candidateAck
        if ($stable -lt 2) { Start-Sleep -Seconds 1 }
    } while ($stable -lt 2 -and [DateTime]::UtcNow -lt $stableDeadline)
    Require-WalFault ($stable -ge 2) 'BROKER_FIXTURE_NOT_IDLE'
    Assert-Baseline $before $ExpectedSessionId $last $sequences
    $evidence.ack_before = [long]$before.live_ack
    $evidence.wal_sequence_before = $last
    $evidence.sidecars_before = Get-CaptureCount $captureRoot $captureDirectory
    Require-WalFault ($evidence.sidecars_before -eq 0) 'BROKER_IMAGE_ALREADY_CAPTURED'
    $next = [long]($last + 1)
    $blockedWalPath = [IO.Path]::Combine($walDirectory, ('{0:D20}.wal.dpapi' -f $next))
    Require-WalFault (-not [IO.File]::Exists($blockedWalPath) -and
        -not [IO.Directory]::Exists($blockedWalPath)) 'NEXT_WAL_ALREADY_EXISTS'
    $null = New-Item -ItemType Directory -Path $blockedWalPath -ErrorAction Stop
    $createdWalDirectory = $true
    Assert-LocalDirectory $blockedWalPath 'BLOCKER_DIRECTORY_UNSAFE'
    Require-WalFault (@([IO.Directory]::EnumerateFileSystemEntries($blockedWalPath)).Count -eq 0) `
        'BLOCKER_DIRECTORY_NOT_EMPTY'
    $evidence.blocked_sequence = $next
    $evidence.stage = 'blocked-broker-send'
    [IO.File]::WriteAllText($trigger + '.pending', '', [Text.UTF8Encoding]::new($false))
    $createdPending = $true
    [IO.File]::Move($trigger + '.pending', $trigger)
    $createdPending = $false
    $createdTrigger = $true
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $sidecars = 0
    do {
        Require-WalFault ([IO.Directory]::Exists($blockedWalPath) -and
            -not [IO.File]::Exists($blockedWalPath)) 'WAL_BLOCKER_LOST'
        $sidecars = Get-CaptureCount $captureRoot $captureDirectory -AllowPending
        if ($sidecars -gt 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    Require-WalFault ($sidecars -eq 1 -and -not [IO.File]::Exists($trigger)) `
        'BROKER_SIDECAR_NOT_OBSERVED'
    $createdTrigger = $false
    $during = Invoke-Hook 'inspect' @{ live_marker = $script:liveMarker }
    Require-WalFault ([guid]$during.session_id -eq $ExpectedSessionId -and
        [long]$during.live_ack -eq $evidence.ack_before -and
        [int]$during.broker_capture_count -eq 0 -and
        [IO.Directory]::Exists($blockedWalPath) -and -not [IO.File]::Exists($blockedWalPath)) `
        'BROKER_WAL_FAULT_NOT_PROVEN'
    $evidence.ack_during_fault = [long]$during.live_ack
    $evidence.sidecars_during_fault = $sidecars
    Require-WalFault ([string]::Equals([IO.Path]::GetDirectoryName($blockedWalPath),
        $walDirectory, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($blockedWalPath) -ceq ('{0:D20}.wal.dpapi' -f $next)) `
        'BLOCKER_SCOPE_INVALID'
    Assert-LocalDirectory $history 'HISTORY_ROOT_UNSAFE'
    Assert-LocalDirectory $sessionDirectory 'SESSION_DIRECTORY_UNSAFE'
    Assert-LocalDirectory $walDirectory 'WAL_DIRECTORY_UNSAFE'
    Assert-LocalDirectory $blockedWalPath 'BLOCKER_DIRECTORY_UNSAFE'
    [IO.Directory]::Delete($blockedWalPath, $false)
    $createdWalDirectory = $false
    $evidence.stage = 'waiting-for-retry'
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $after = $null
    do {
        if ([IO.File]::Exists($blockedWalPath)) {
            $candidate = Invoke-Hook 'inspect' @{ live_marker = $script:liveMarker }
            if ([guid]$candidate.session_id -eq $ExpectedSessionId -and
                [int]$candidate.broker_capture_count -eq 1 -and
                [long]$candidate.broker_capture_last_sequence -eq $next -and
                [long]$candidate.live_ack -gt $evidence.ack_before -and
                [long]$candidate.live_ack -ge $next) {
                $after = $candidate
                break
            }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    Require-WalFault ($null -ne $after) 'BROKER_RETRY_NOT_CORRELATED'
    Assert-Recovered $after $ExpectedSessionId $next $evidence.ack_before
    Require-WalFault ((Get-CaptureCount $captureRoot $captureDirectory) -eq 1) `
        'BROKER_SIDECAR_COUNT_CHANGED'
    $evidence.ack_after = [long]$after.live_ack
    $evidence.broker_resolved_count = [int]$after.broker_capture_count
    $evidence.broker_source_sequence = [long]$after.broker_capture_last_sequence
    $evidence.status = 'passed'
    $evidence.stage = 'complete'
} catch {
    $code = [string]$_.Exception.Message
    $failureCode = if ($code -cmatch '^[A-Z][A-Z0-9_]{2,80}$') { $code } else { 'BROKER_WAL_UNEXPECTED' }
    $evidence.failure_code = $failureCode
    $evidence.status = 'failed'
} finally {
    if ($createdWalDirectory -and $null -ne $blockedWalPath -and
        [IO.Directory]::Exists($blockedWalPath)) {
        try {
            Require-WalFault ([string]::Equals([IO.Path]::GetDirectoryName($blockedWalPath),
                $walDirectory, [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($blockedWalPath) -ceq ('{0:D20}.wal.dpapi' -f $next)) `
                'BLOCKER_SCOPE_INVALID'
            Assert-LocalDirectory $history 'HISTORY_ROOT_UNSAFE'
            Assert-LocalDirectory $sessionDirectory 'SESSION_DIRECTORY_UNSAFE'
            Assert-LocalDirectory $walDirectory 'WAL_DIRECTORY_UNSAFE'
            Assert-LocalDirectory $blockedWalPath 'BLOCKER_DIRECTORY_UNSAFE'
            Require-WalFault (@([IO.Directory]::EnumerateFileSystemEntries($blockedWalPath)).Count -eq 0) `
                'BLOCKER_DIRECTORY_NOT_EMPTY'
            [IO.Directory]::Delete($blockedWalPath, $false)
        } catch { $cleanupCode = 'BLOCKER_CLEANUP_FAILED' }
    }
    foreach ($ownedFile in @(@{ Path = $trigger + '.pending'; Owned = $createdPending },
            @{ Path = $trigger; Owned = $createdTrigger })) {
        if ($ownedFile.Owned -and [IO.File]::Exists($ownedFile.Path)) {
            try {
                Assert-LocalDirectory $runRoot 'RUN_DIRECTORY_UNSAFE'
                $item = Get-Item -LiteralPath $ownedFile.Path -Force
                Require-WalFault ((($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) `
                    'TRIGGER_UNSAFE'
                [IO.File]::Delete($ownedFile.Path)
            } catch { $cleanupCode = 'TRIGGER_CLEANUP_FAILED' }
        }
    }
    if ($null -ne $cleanupCode) {
        $evidence.cleanup_code = $cleanupCode
        $evidence.status = 'failed'
    }
    Assert-LocalDirectory $runRoot 'RUN_DIRECTORY_UNSAFE'
    [IO.File]::WriteAllText($evidenceFile,
        (($evidence | ConvertTo-Json -Depth 4) + "`n"), [Text.UTF8Encoding]::new($false))
}
if ($evidence.status -ne 'passed') {
    throw [InvalidOperationException]::new('BROKER_WAL_FAULT_FAILED:' + ($failureCode ?? $cleanupCode))
}
'Broker WAL fault product E2E passed.'
