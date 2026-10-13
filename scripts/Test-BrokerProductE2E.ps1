[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runner = Join-Path $PSScriptRoot 'Invoke-BrokerProductE2E.ps1'
$source = Get-Content -LiteralPath $runner -Raw
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'BROKER_RUNNER_PARSE_FAILED' }
foreach ($phase in @('theme-switch-arm', 'theme-switch-read', 'theme-switch-same',
    'theme-switch-unsubscribe', 'theme-switch-restore', 'theme-switch-final')) {
    if (-not $source.Contains("'$phase'")) { throw 'BROKER_REAL_THEME_PHASE_MISSING' }
}
if (-not $source.Contains("'--select-owned-display-theme'") -or
    -not $source.Contains('[string]$sessionHost.Id, $resolvedHost, $resolvedDisplay') -or
    -not $source.Contains("('scheme' + `$selectedTheme)")) { throw 'BROKER_REAL_THEME_DRIVER_ARGUMENT_INVALID' }
if (-not $source.Contains('-driver.json') -or
    -not $source.Contains('-ExpectedTheme $baselineTheme')) { throw 'BROKER_REAL_THEME_DRIVER_MISSING' }
$audioGuard = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains("throw 'AUDIO_RECORDER_REQUIRES_EFFECTS_AND_EXECUTABLE'")
}, $true)
if ($null -eq $audioGuard -or -not $source.Contains('-AudioRecorderPath $AudioRecorderPath')) {
    throw 'BROKER_AUDIO_RECORDER_INTEGRATION_MISSING'
}
$vectorGuard = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains("throw 'DIAGNOSTIC_VECTOR_REQUIRES_INDEPENDENT_RUN'")
}, $true)
if ($null -eq $vectorGuard -or
    -not $source.Contains("ValidateSet('unsupported', 'cursor-restore', 'tab-restore')") -or
    -not $source.Contains('-DiagnosticVector $DiagnosticVector')) {
    throw 'BROKER_C08_DIAGNOSTIC_VECTOR_INTEGRATION_MISSING'
}
$DiagnosticVector = 'unsupported'; $DiagnosticFailureAcceptance = $false
. ([ScriptBlock]::Create($vectorGuard.Extent.Text))
foreach ($vector in @('cursor-restore', 'tab-restore')) {
    $DiagnosticVector = $vector; $DiagnosticFailureAcceptance = $true
    . ([ScriptBlock]::Create($vectorGuard.Extent.Text))
    $DiagnosticFailureAcceptance = $false
    $rejected = $false
    try { . ([ScriptBlock]::Create($vectorGuard.Extent.Text)) } catch {
        if ($_.Exception.Message -cne 'DIAGNOSTIC_VECTOR_REQUIRES_INDEPENDENT_RUN') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'BROKER_C08_VECTOR_OUTSIDE_INDEPENDENT_RUN_ACCEPTED' }
}
foreach ($guard in @('-OwnedSessionHost $sessionHost', '-ExpectedHostPath $resolvedHost',
    '-StopOwnedSessionHost $stopOwnedHost', 'OWNED_HOST_IDENTITY_MISMATCH',
    'exited_at_timestamp = [Diagnostics.Stopwatch]::GetTimestamp()', '.GetNewClosure()')) {
    if (-not $source.Contains($guard)) { throw 'BROKER_OWNED_HOST_EXIT_AUDIO_INTEGRATION_MISSING' }
}
$AudioRecorderPath = (Get-Process -Id $PID).Path
$EffectsAcceptance = $true
. ([ScriptBlock]::Create($audioGuard.Extent.Text))
foreach ($case in @(
    @{ path = $AudioRecorderPath; effects = $false },
    @{ path = 'relative.exe'; effects = $true },
    @{ path = Join-Path ([IO.Path]::GetTempPath()) ('missing-' + [guid]::NewGuid() + '.exe'); effects = $true }
)) {
    $AudioRecorderPath = $case.path
    $EffectsAcceptance = $case.effects
    $rejected = $false
    try { . ([ScriptBlock]::Create($audioGuard.Extent.Text)) } catch {
        if ($_.Exception.Message -cne 'AUDIO_RECORDER_REQUIRES_EFFECTS_AND_EXECUTABLE') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'BROKER_AUDIO_RECORDER_INVALID_CONTEXT_ACCEPTED' }
}
if (-not $source.Contains('[switch]$BrokerWalFaultAcceptance') -or
    -not $source.Contains('BROKER_WAL_HISTORY_ROOT_REQUIRED') -or
    -not $source.Contains('Invoke-BrokerWalFaultProductE2E.ps1') -or
    -not $source.Contains('BROKER_WAL_FAULT_NOT_PASSED')) { throw 'BROKER_WAL_FAULT_INTEGRATION_MISSING' }
if (-not $source.Contains('[switch]$DeltaRestoreAcceptance') -or
    -not $source.Contains('[switch]$DeltaRetiredBaseAcceptance') -or
    -not $source.Contains('DELTA_ACCEPTANCE_MODES_MUTUALLY_EXCLUSIVE') -or
    -not $source.Contains('[string]$DeltaHistoryRoot') -or
    -not $source.Contains('[guid]$DeltaPcDeviceId') -or
    -not $source.Contains('[string]$DeltaSnapshotInspectorPath') -or
    -not $source.Contains('DELTA_RESTORE_REQUIRES_INDEPENDENT_RUN') -or
    -not $source.Contains('--inspect-fixture-snapshots') -or
    -not $source.Contains('Invoke-RestoreProductE2E.ps1') -or
    -not $source.Contains('delta-output.trigger') -or
    -not $source.Contains('DELTA_MULTI_CHUNK_NOT_REACHED') -or
    -not $source.Contains('Assert-DeltaRestoreResult') -or
    -not $source.Contains('DELTA_AGENT_PID_REQUIRED') -or
    -not $source.Contains('DELTA_AGENT_IDENTITY_MISMATCH') -or
    -not $source.Contains("'--diagnostics'") -or
    -not $source.Contains("`$stderr.Trim() -cne 'snapshot_changed'") -or
    -not $source.Contains('DELTA_INSPECTOR_SNAPSHOT_CHANGED') -or
    -not $source.Contains('Assert-DeltaAndroidCheckpointSeed') -or
    -not $source.Contains('DELTA_ANDROID_FULL_SNAPSHOT_NOT_SEEDED') -or
    -not $source.Contains('DELTA_BASE_NOT_ANDROID_CHECKPOINT') -or
    -not $source.Contains('android_checkpoint_sha256') -or
    -not $source.Contains('full_snapshot_install_count') -or
    -not $source.Contains('DELTA_NOT_INSTALLED') -or
    -not $source.Contains('full_snapshot_fallback_verified_base_retirement_unverified') -or
    -not $source.Contains('Assert-HostHistoryPeerAck -LogPath $peerAckLog') -or
    -not $source.Contains("'PEER_ACK_LOG_READ_TRANSIENT'" ) -or
    -not $source.Contains('$evidence.delta_restore.windows_peer_ack = $peerAck') -or
    -not $source.Contains('delta-preclosed.json') -or
    -not $source.Contains('-PreClosedBaselinePath $preClosedBaselinePath')) {
    throw 'BROKER_DELTA_RESTORE_INTEGRATION_MISSING'
}
if (-not $source.Contains("foreach (`$trigger in @(`$kittyLaunchTriggerPath, `$kittyTriggerPath, `$extendedTriggerPath, `$deltaTriggerPath)")) {
    throw 'BROKER_DELTA_TRIGGER_CLEANUP_MISSING'
}
$deltaStart = $source.IndexOf("        `$stage = 'delta-seed-alignment'")
$deltaEnd = $source.IndexOf("    `$stage = 'broker-relay'", $deltaStart)
if ($deltaStart -lt 0 -or $deltaEnd -le $deltaStart) { throw 'BROKER_DELTA_BLOCK_MISSING' }
$deltaBlock = $source.Substring($deltaStart, $deltaEnd - $deltaStart)
if (-not $source.Contains('function Invoke-DeltaSeedViewCycle') -or
    -not $source.Contains('function Close-DeltaSeedView') -or
    -not $source.Contains("for (`$seedAttempt = 1; `$seedAttempt -le 3; `$seedAttempt++)") -or
    -not $deltaBlock.Contains('Invoke-DeltaSeedViewCycle $seedAttempt $seedViewClosed') -or
    -not $deltaBlock.Contains('Assert-DeltaAndroidCheckpointSeed $seedCycle.android $seedCycle.windows') -or
    -not $deltaBlock.Contains('Close-DeltaSeedView') -or
    -not $deltaBlock.Contains('DELTA_ANDROID_SEED_ALIGNMENT_FAILED') -or
    -not $source.Contains('delta-seed-inspector-') -or
    -not $deltaBlock.Contains('seed_alignment') -or
    [regex]::Matches($deltaBlock, [regex]::Escape("Invoke-Hook 'close_view'")).Count -ne 0) {
    throw 'BROKER_DELTA_SEED_ALIGNMENT_MISSING_OR_DUPLICATED'
}
$seedInspectorSave = $source.IndexOf('[IO.File]::WriteAllText($windowsPath')
$seedIdentityAssert = $source.IndexOf('Assert-DeltaAndroidCheckpointSeed $seedCycle.android $seedCycle.windows')
if ($seedInspectorSave -lt 0 -or $seedIdentityAssert -le $seedInspectorSave) {
    throw 'BROKER_DELTA_SEED_INSPECTOR_NOT_SAVED_BEFORE_ASSERT'
}
if (-not $deltaBlock.Contains("[byte[]]::new(512 * 512 * 4)") -or
    -not $deltaBlock.Contains("'_Ga=T,t=f,f=32,s=512,v=512,S=1048576,i=78,q=2,c=4,r=2;'") -or
    -not $deltaBlock.Contains('fixture_resource_sha256') -or
    $deltaBlock.Contains('$lineCount = 2000') -or
    -not $source.Contains('Remove-Item -LiteralPath $deltaImagePath -Force')) {
    throw 'BROKER_DELTA_ATOMIC_RESOURCE_FIXTURE_MISSING'
}

$privateRun = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Extent.Text.Contains('[IO.FileSystemAclExtensions]::Create')
}, $true)
if ($null -eq $privateRun) { throw 'BROKER_WAL_PRIVATE_RUN_MISSING' }
$BrokerWalFaultAcceptance = $true
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('termlink-broker-run-acl-' + [guid]::NewGuid().ToString('N'))
try {
    . ([ScriptBlock]::Create($privateRun.Extent.Text))
    $acl = Get-Acl -LiteralPath $runRoot
    if (-not $acl.AreAccessRulesProtected -or
        -not $acl.GetOwner([Security.Principal.SecurityIdentifier]).Equals($currentUser)) {
        throw 'BROKER_WAL_PRIVATE_RUN_INVALID'
    }
    $trusted = @($currentUser.Value, 'S-1-5-18', 'S-1-5-32-544')
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IdentityReference.Value -notin $trusted -or $rule.IsInherited) {
            throw 'BROKER_WAL_PRIVATE_RUN_FOREIGN_ACCESS'
        }
    }
} finally {
    if ([IO.Directory]::Exists($runRoot)) { [IO.Directory]::Delete($runRoot, $false) }
}

$start = $source.IndexOf("`$childScript = @'")
if ($start -lt 0) { throw 'BROKER_CHILD_MISSING' }
$start = $source.IndexOf("`n", $start) + 1
$end = $source.IndexOf("`n'@.Replace", $start)
if ($end -lt $start) { throw 'BROKER_CHILD_END_MISSING' }
$child = $source.Substring($start, $end - $start)
if (-not $child.Contains('i=77,q=2,c=4,r=2')) { throw 'BROKER_IMAGE_MUST_SUPPRESS_UNREAD_REPLY' }
$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseInput($child, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -ne 0) { throw 'BROKER_CHILD_PARSE_FAILED' }
if (-not $source.Contains('[string]$ProtocolReplyChildPath') -or
    -not $source.Contains('Assert-TerminalReplyProbe -Phase $phase -Probe $replyProbe -Inspect $current') -or
    -not $source.Contains("if (`$fixtureReplies) { [IO.File]::WriteAllText(`$fixtureReplyTrigger, 'exit') }") -or
    -not $child.Contains("[IO.File]::Exists('broker-replies.trigger')") -or
    -not $child.Contains('& $replyChild --protocol-reply-relay-child') -or
    -not $child.Contains('__REPLY_CHILD_PATH_BASE64__') -or
    $child.Contains('Invoke-Expression')) {
    throw 'BROKER_REPLY_CHILD_PATH_UNSAFE_OR_MISSING'
}
if (-not $child.Contains("[IO.File]::Exists('delta-output.trigger')") -or
    -not $child.Contains('[Convert]::FromBase64String($encoded)') -or
    -not $child.Contains('DELTA_OUTPUT_TRIGGER_INVALID') -or
    -not $source.Contains('Write-ProductTrigger $deltaTriggerPath') -or
    -not $child.Contains('if (__DELTA_SEED_MODE__)') -or
    -not $source.Contains("Replace('__DELTA_SEED_MODE__', `$(if (`$deltaAcceptance) { '`$true' } else { '`$false' }))")) {
    throw 'BROKER_DELTA_CHILD_TRIGGER_MISSING_OR_UNSAFE'
}
$seedSelfCheckRoot = Join-Path ([IO.Path]::GetTempPath()) ('termlink-broker-seed-selfcheck-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $seedSelfCheckRoot -Force | Out-Null
try {
    foreach ($seedMode in @($true, $false)) {
        $seedLiteral = if ($seedMode) { '$true' } else { '$false' }
        $generatedChild = $child.Replace('__MARKER_STEM__', 'TLTF_SELFTEST').
            Replace('__SMOKE_PREFIX__', 'TLPS_SELFTEST').
            Replace('__LIVE_MARKER__', 'TLTF_LIVE_SELFTEST').
            Replace('__DELTA_SEED_MODE__', $seedLiteral).
            Replace('__KITTY_CHILD_PATH_BASE64__', '').
            Replace('__REPLY_CHILD_PATH_BASE64__', '')
        $generatedPath = Join-Path $seedSelfCheckRoot ('broker-fixture-' + $seedMode.ToString().ToLowerInvariant() + '.ps1')
        [IO.File]::WriteAllText($generatedPath, $generatedChild, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $seedSelfCheckRoot 'broker-exit.trigger'), '')
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = Join-Path $PSHOME 'pwsh.exe'
        $startInfo.WorkingDirectory = $seedSelfCheckRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $generatedPath)) {
            $startInfo.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) { throw 'BROKER_DELTA_SEED_SELFTEST_START_FAILED' }
        try {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(10000)) {
                $process.Kill($true)
                throw 'BROKER_DELTA_SEED_SELFTEST_TIMEOUT'
            }
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) {
                throw ('BROKER_DELTA_SEED_SELFTEST_FAILED: ' + $stderr.Trim())
            }
            $lines = @($stdout -split "`r?`n" | Where-Object { $_ -ne '' })
            $expectedCount = if ($seedMode) { 2 } else { 5200 }
            if ($lines.Count -ne $expectedCount -or
                $lines[0] -cne 'TLTF_SELFTEST_0001' -or
                $lines[$lines.Count - 1] -cne 'TLTF_SELFTEST_5200') {
                throw 'BROKER_DELTA_SEED_SELFTEST_OUTPUT_INVALID'
            }
        } finally {
            $process.Dispose()
        }
    }
} finally {
    if ([IO.Directory]::Exists($seedSelfCheckRoot)) { [IO.Directory]::Delete($seedSelfCheckRoot, $true) }
}
$replyChildSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\win-agent\tests\SessionHostValidation\ProtocolReplyRelayChild.cs') -Raw
if (-not $replyChildSource.Contains('idle.Elapsed < TimeSpan.FromMinutes(2)')) {
    throw 'BROKER_REPLY_CHILD_IDLE_DEADLINE_TOO_SHORT'
}
if (-not $source.Contains('Assert-TerminalRestoredSemanticProbe') -or
    -not $source.Contains('Get-TerminalExtendedPhaseDefinitions') -or
    -not $child.Contains("[IO.File]::Exists('protocol-extended.trigger')") -or
    -not $child.Contains('& $kittyChild --kitty-relay-child') -or
    -not $source.Contains('KITTY_DURABLE_REPLAY_NOT_VISIBLE') -or
    -not $source.Contains('KITTY_TEMP_NOT_DELETED') -or
    -not $source.Contains('KITTY_TRANSFER_PIXELS_CHANGED') -or
    $source.Contains("'native_frame_semantics_and_visual'")) {
    throw 'BROKER_EXTENDED_RESTORE_OR_TRANSFER_ACCEPTANCE_MISSING'
}
if (-not $source.Contains('$phasePrevious = $previous') -or
    -not $source.Contains('$phasePrevious.terminal_semantics') -or
    -not $source.Contains("('extended-' + `$definition.phase)") -or
    -not $source.Contains("'restored-extended-' + `$definition.phase + '.json'") -or
    -not $source.Contains("'restored-extended-' + `$definition.phase + '.png'") -or
    -not $source.Contains('snapshot_restore = $false')) {
    throw 'BROKER_EXTENDED_SNAPSHOT_RESTORE_COVERAGE_MISSING'
}

$broker = $source.IndexOf("[IO.File]::WriteAllText(`$sendTriggerPath, '')")
$image = $source.IndexOf("'NATIVE_IMAGE_NOT_VISIBLE'", $broker)
$optional = $source.IndexOf("    if (`$ProtocolSmoke) {", $image)
if ($broker -lt 0 -or $image -le $broker -or $optional -le $image -or
    -not $source.Contains("[switch]`$ProtocolSmoke") -or
    -not $source.Contains('BROKER_FETCH_OR_ACK_TIMEOUT') -or
    -not $source.Contains('Invoke-ProbeWithStartupRetry') -or
    -not $source.Contains("BRIDGE_STARTUP_TIMEOUT") -or
    -not $source.Contains("[string](`$result.Output ?? '')") -or
    -not $source.Contains('failure_type') -or
    -not $source.Contains('failure_location') -or
    -not $source.Contains("'transport_and_visual_smoke'")) {
    throw 'BROKER_DEFAULT_PATH_CHANGED'
}

$functionEnd = $child.IndexOf("`nif (__DELTA_SEED_MODE__)")
if ($functionEnd -lt 0) { throw 'BROKER_MARKER_STREAM_MISSING' }
$functionText = $child.Substring(0, $functionEnd).
    Replace('__SMOKE_PREFIX__', 'TLPS_TEST').
    Replace('__LIVE_MARKER__', 'TLTF_LIVE_TEST')
. ([ScriptBlock]::Create($functionText))

$esc = [char]27
$cases = @(
    @{ name = 'unicode'; group = 'C03'; vector = '中文 🇨🇳' },
    @{ name = 'line'; group = 'C04'; vector = $esc + '#6WIDE' },
    @{ name = 'sgr'; group = 'C06'; vector = $esc + '[#{' },
    @{ name = 'pages'; group = 'C09'; vector = $esc + '[2 P' },
    @{ name = 'charset'; group = 'C10'; vector = $esc + 'P1!uB' },
    @{ name = 'macro'; group = 'C11'; vector = $esc + 'P1;0;1!z4F4B' },
    @{ name = 'drcs'; group = 'C12'; vector = $esc + 'P1;1;0;5;1;2;8;0{ @~~~~~' },
    @{ name = 'sixel'; group = 'C13'; vector = $esc + 'P0;1;0q"' },
    @{ name = 'colors'; group = 'C14'; vector = $esc + ']17;#123456' }
)
$originalOutput = [Console]::Out
$writer = [IO.StringWriter]::new()
try {
    [Console]::SetOut($writer)
    foreach ($case in $cases) {
        if (-not $source.Contains("@{ name = '$($case.name)'; group = '$($case.group)' }")) {
            throw "BROKER_PHASE_MAPPING_MISSING: $($case.name)"
        }
        $null = $writer.GetStringBuilder().Clear()
        Write-ProtocolSmoke $case.name
        $output = $writer.ToString()
        if (-not $output.StartsWith($esc + 'c' + $esc + '[2J' + $esc + '[H') -or
            -not $output.Contains($case.vector) -or
            -not $output.Contains('TLTF_LIVE_TEST') -or
            -not $output.Contains('TLPS_TEST_' + $case.name.ToUpperInvariant())) {
            throw "BROKER_PHASE_VECTOR_INVALID: $($case.name)"
        }
    }
    $null = $writer.GetStringBuilder().Clear()
    Write-ProtocolSmoke 'drcs-baseline'
    $baseline = $writer.ToString()
    if (-not $baseline.Contains('TLPS_TEST_DRCS') -or $baseline.Contains($esc + 'P1;1;0;5;1;2;8;0{')) {
        throw 'BROKER_DRCS_BASELINE_INVALID'
    }
} finally {
    [Console]::SetOut($originalOutput)
    $writer.Dispose()
}

$pixelFunction = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Count-NewDrcsPixels'
}, $true)
if ($null -eq $pixelFunction -or -not $source.Contains('DRCS_GLYPH_NOT_VISIBLE')) {
    throw 'BROKER_DRCS_PIXEL_ASSERTION_MISSING'
}
. ([ScriptBlock]::Create($pixelFunction.Extent.Text))
$sixelPixelFunction = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Count-NewSixelPixels'
}, $true)
if ($null -eq $sixelPixelFunction -or -not $source.Contains('SIXEL_IMAGE_NOT_VISIBLE')) {
    throw 'BROKER_SIXEL_PIXEL_ASSERTION_MISSING'
}
. ([ScriptBlock]::Create($sixelPixelFunction.Extent.Text))
function Require-E2E([bool]$Condition, [string]$Code) { if (-not $Condition) { throw $Code } }
$AndroidPackageName = 'dev.termlink.harness.recovery'
foreach ($name in @('Get-DeltaProperty', 'Assert-DeltaSnapshotTransition',
        'Assert-DeltaAndroidCheckpointSeed', 'Assert-DeltaSnapshotStable',
        'Assert-BaseRetired', 'Assert-DeltaRestoreResult', 'New-DeltaPreClosedBaseline', 'Test-OwnedDisplayIdentity')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "BROKER_FUNCTION_MISSING: $name" }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$displayStart = [DateTime]::UtcNow
$displayPath = Join-Path ([IO.Path]::GetTempPath()) 'TermLink.NativeDisplay.exe'
$fakeDisplay = [pscustomobject]@{ Id = 42; HasExited = $false; StartTime = $displayStart }
$fakeIdentity = [pscustomobject]@{ ProcessId = 42; ParentProcessId = 41; ExecutablePath = $displayPath }
if (-not (Test-OwnedDisplayIdentity $fakeDisplay $fakeIdentity 41 $displayPath $displayStart)) {
    throw 'BROKER_DISPLAY_IDENTITY_POSITIVE_FAILED'
}
foreach ($mutation in @(
    { param($d, $i) $i.ProcessId = 43 },
    { param($d, $i) $i.ParentProcessId = 40 },
    { param($d, $i) $i.ExecutablePath = Join-Path ([IO.Path]::GetTempPath()) 'other.exe' },
    { param($d, $i) $d.StartTime = $displayStart.AddSeconds(1) },
    { param($d, $i) $d.HasExited = $true }
)) {
    $candidateDisplay = $fakeDisplay.PSObject.Copy()
    $candidateIdentity = $fakeIdentity.PSObject.Copy()
    & $mutation $candidateDisplay $candidateIdentity
    if (Test-OwnedDisplayIdentity $candidateDisplay $candidateIdentity 41 $displayPath $displayStart) {
        throw 'BROKER_DISPLAY_IDENTITY_NEGATIVE_FAILED'
    }
}
$stopDisplayDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-OwnedDisplay'
}, $true).Extent.Text
if ([regex]::Matches($stopDisplayDefinition, 'Test-OwnedDisplayIdentity').Count -ne 2 -or
    $stopDisplayDefinition.IndexOf('CloseMainWindow()') -gt $stopDisplayDefinition.IndexOf('$Display.Kill()') -or
    -not $source.Contains('if ($evidence.host_stopped -and $null -ne $displayProcess)')) {
    throw 'BROKER_DISPLAY_CLEANUP_ORDER_INVALID'
}
$deltaSessionId = [guid]::NewGuid()
$deltaBaseHash = 'a' * 64
$deltaTargetHash = 'b' * 64
function Assert-BrokerDeltaReject([scriptblock]$Action, [string]$Name) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw "BROKER_DELTA_NEGATIVE_FAILED: $Name" }
}
$deltaBase = [pscustomobject]@{
    current = [pscustomobject]@{ sequence = 12; sha256 = $deltaBaseHash; length = 4096 }
    previous_exists = $false
    previous = $null
}
$deltaTarget = [pscustomobject]@{
    current = [pscustomobject]@{ sequence = 15; sha256 = $deltaTargetHash; length = 8192 }
    previous_exists = $true
    previous = [pscustomobject]@{ sequence = 12; sha256 = $deltaBaseHash; length = 4096 }
}
Assert-DeltaSnapshotTransition $deltaBase $deltaTarget
Assert-DeltaSnapshotStable $deltaTarget ($deltaTarget | ConvertTo-Json | ConvertFrom-Json)
$deltaAndroid = [pscustomobject]@{
    snapshot_sequence_s = 12
    restore_diagnostics = [pscustomobject]@{
        attempt = 1
        delta_install_count = 0
        full_snapshot_install_count = 1
        full_snapshot_sequence = 12
        full_snapshot_sha256 = $deltaBaseHash
    }
}
$deltaSeed = Assert-DeltaAndroidCheckpointSeed $deltaAndroid $deltaBase
if ($deltaSeed.full_snapshot_install_count -ne 1 -or $deltaSeed.sequence -ne 12 -or
    $deltaSeed.sha256 -cne $deltaBaseHash) { throw 'BROKER_DELTA_ANDROID_SEED_POSITIVE_FAILED' }
$badDeltaSeedCount = $deltaAndroid | ConvertTo-Json | ConvertFrom-Json
$badDeltaSeedCount.restore_diagnostics.full_snapshot_install_count = 0
Assert-BrokerDeltaReject { Assert-DeltaAndroidCheckpointSeed $badDeltaSeedCount $deltaBase } `
    'android-full-snapshot-count'
$badDeltaSeedPair = $deltaAndroid | ConvertTo-Json | ConvertFrom-Json
$badDeltaSeedPair.restore_diagnostics.full_snapshot_sha256 = 'c' * 64
Assert-BrokerDeltaReject { Assert-DeltaAndroidCheckpointSeed $badDeltaSeedPair $deltaBase } `
    'android-full-snapshot-hash'
$badDeltaSeedWindows = $deltaBase | ConvertTo-Json | ConvertFrom-Json
$badDeltaSeedWindows.current.sha256 = 'c' * 64
Assert-BrokerDeltaReject { Assert-DeltaAndroidCheckpointSeed $deltaAndroid $badDeltaSeedWindows } `
    'android-full-snapshot-windows-pair'
$retiredTarget = [pscustomobject]@{
    current = [pscustomobject]@{ sequence = 18; sha256 = ('c' * 64); length = 12288 }
    previous_exists = $true
    previous = [pscustomobject]@{ sequence = 15; sha256 = $deltaTargetHash; length = 8192 }
}
Assert-BaseRetired $deltaBase $deltaTarget $retiredTarget ($retiredTarget | ConvertTo-Json | ConvertFrom-Json)
$retiredWithoutPrevious = $retiredTarget | ConvertTo-Json | ConvertFrom-Json
$retiredWithoutPrevious.previous_exists = $false
$retiredWithoutPrevious.previous = $null
Assert-BaseRetired $deltaBase $deltaTarget $retiredWithoutPrevious `
    ($retiredWithoutPrevious | ConvertTo-Json | ConvertFrom-Json)
$deltaRestore = [pscustomobject]@{
    session_id = $deltaSessionId.ToString('D')
    status = 'delta_and_chunk_ack_send_verified_peer_ack_unverified'
    base_snapshot_sequence = 12
    base_sha256 = $deltaBaseHash
    target_snapshot_sequence = 15
    target_sha256 = $deltaTargetHash
    delta_install_count = 1
    delta_expected_chunk_count = 2
    delta_chunks_accepted = 2
    delta_duplicate_chunks = 1
    delta_chunk_acks_sent = 3
}
Assert-DeltaRestoreResult $deltaRestore $deltaSessionId $deltaBase $deltaTarget
$badBaseHash = $deltaTarget | ConvertTo-Json | ConvertFrom-Json
$badBaseHash.previous.sha256 = 'c' * 64
Assert-BrokerDeltaReject { Assert-DeltaSnapshotTransition $deltaBase $badBaseHash } 'base-hash-mismatch'
$badPreviousSequence = $deltaTarget | ConvertTo-Json | ConvertFrom-Json
$badPreviousSequence.previous.sequence = 11
Assert-BrokerDeltaReject { Assert-DeltaSnapshotTransition $deltaBase $badPreviousSequence } 'previous-base-mismatch'
$badTargetSequence = $deltaTarget | ConvertTo-Json | ConvertFrom-Json
$badTargetSequence.current.sequence = 12
Assert-BrokerDeltaReject { Assert-DeltaSnapshotTransition $deltaBase $badTargetSequence } 'target-sequence-not-newer'
$unstableTarget = $deltaTarget | ConvertTo-Json | ConvertFrom-Json
$unstableTarget.current.sha256 = 'd' * 64
Assert-BrokerDeltaReject { Assert-DeltaSnapshotStable $deltaTarget $unstableTarget } 'pair-not-stable'
$stillHeld = $retiredTarget | ConvertTo-Json | ConvertFrom-Json
$stillHeld.previous.sequence = 12
$stillHeld.previous.sha256 = $deltaBaseHash
Assert-BrokerDeltaReject {
    Assert-BaseRetired $deltaBase $deltaTarget $stillHeld ($stillHeld | ConvertTo-Json | ConvertFrom-Json)
} 'base-still-previous'
$falseStable = $retiredTarget | ConvertTo-Json | ConvertFrom-Json
$falseStable.previous.sha256 = 'd' * 64
Assert-BrokerDeltaReject { Assert-BaseRetired $deltaBase $deltaTarget $retiredTarget $falseStable } `
    'retired-pair-not-stable'
$badSession = $deltaRestore | ConvertTo-Json | ConvertFrom-Json
$badSession.session_id = ([guid]::NewGuid()).ToString('D')
Assert-BrokerDeltaReject { Assert-DeltaRestoreResult $badSession $deltaSessionId $deltaBase $deltaTarget } 'session-identity-mismatch'
$singleChunk = $deltaRestore | ConvertTo-Json | ConvertFrom-Json
$singleChunk.delta_expected_chunk_count = 1
Assert-BrokerDeltaReject { Assert-DeltaRestoreResult $singleChunk $deltaSessionId $deltaBase $deltaTarget } 'single-chunk'
$badAck = $deltaRestore | ConvertTo-Json | ConvertFrom-Json
$badAck.delta_chunk_acks_sent = 2
Assert-BrokerDeltaReject { Assert-DeltaRestoreResult $badAck $deltaSessionId $deltaBase $deltaTarget } 'ack-count-mismatch'
function Invoke-DeltaSeedAlignmentMock([object[]]$Cycles) {
    $attempts = @()
    $matched = $false
    for ($index = 0; $index -lt [Math]::Min($Cycles.Count, 3); $index++) {
        $cycle = $Cycles[$index]
        $entry = [ordered]@{ attempt = $index + 1; status = 'running' }
        try {
            $seed = Assert-DeltaAndroidCheckpointSeed $cycle.android $cycle.windows
            $entry.status = 'matched'
            $entry.sequence = $seed.sequence
            $entry.sha256 = $seed.sha256
            $entry.full_snapshot_install_count = $seed.full_snapshot_install_count
            $matched = $true
        } catch {
            $entry.status = 'mismatch'
            $entry.failure_code = $_.Exception.Message
        }
        $attempts += $entry
        if ($matched) { break }
    }
    [pscustomobject]@{ matched = $matched; attempts = $attempts }
}
$deltaSeedAndroid = [pscustomobject]@{
    snapshot_sequence_s = 12
    restore_diagnostics = [pscustomobject]@{
        attempt = 1
        delta_install_count = 0
        full_snapshot_install_count = 1
        full_snapshot_sequence = 12
        full_snapshot_sha256 = $deltaBaseHash
    }
}
$deltaSeedWindowsMismatch = [pscustomobject]@{
    current = [pscustomobject]@{ sequence = 12; sha256 = ('c' * 64); length = 4096 }
    previous_exists = $false
    previous = $null
}
$deltaSeedWindowsMatch = [pscustomobject]@{
    current = [pscustomobject]@{ sequence = 12; sha256 = $deltaBaseHash; length = 4096 }
    previous_exists = $false
    previous = $null
}
$deltaSeedMockPassed = Invoke-DeltaSeedAlignmentMock @(
    [pscustomobject]@{ android = $deltaSeedAndroid; windows = $deltaSeedWindowsMismatch },
    [pscustomobject]@{ android = $deltaSeedAndroid; windows = $deltaSeedWindowsMatch })
if (-not $deltaSeedMockPassed.matched -or $deltaSeedMockPassed.attempts.Count -ne 2 -or
    $deltaSeedMockPassed.attempts[0].status -cne 'mismatch' -or
    $deltaSeedMockPassed.attempts[0].failure_code -cne 'DELTA_BASE_NOT_ANDROID_CHECKPOINT' -or
    $deltaSeedMockPassed.attempts[1].status -cne 'matched' -or
    $deltaSeedMockPassed.attempts[1].full_snapshot_install_count -ne 1 -or
    $deltaSeedMockPassed.attempts[1].sequence -ne 12 -or
    $deltaSeedMockPassed.attempts[1].sha256 -cne $deltaBaseHash) {
    throw 'BROKER_DELTA_SEED_ALIGNMENT_MOCK_POSITIVE_FAILED'
}
$deltaSeedMockFailed = Invoke-DeltaSeedAlignmentMock @(
    [pscustomobject]@{ android = $deltaSeedAndroid; windows = $deltaSeedWindowsMismatch },
    [pscustomobject]@{ android = $deltaSeedAndroid; windows = $deltaSeedWindowsMismatch },
    [pscustomobject]@{ android = $deltaSeedAndroid; windows = $deltaSeedWindowsMismatch },
    [pscustomobject]@{ android = $deltaSeedAndroid; windows = $deltaSeedWindowsMatch })
if ($deltaSeedMockFailed.matched -or $deltaSeedMockFailed.attempts.Count -ne 3 -or
    @($deltaSeedMockFailed.attempts | Where-Object { $_.status -cne 'mismatch' }).Count -ne 0) {
    throw 'BROKER_DELTA_SEED_ALIGNMENT_MOCK_BOUNDARY_FAILED'
}
$deltaSeedAndroidDelta = [pscustomobject]@{
    snapshot_sequence_s = 10
    restore_diagnostics = [pscustomobject]@{
        full_snapshot_install_count = 0
        delta_install_count = 1
        delta_request_count = 1
        delta_request_id = [guid]::NewGuid().ToString()
        delta_expected_chunk_count = 1
        delta_duplicate_chunks = 0
        delta_chunk_acks_sent = 1
        delta_chunks_accepted = 1
        base_snapshot_sequence = 8
        base_sha256 = ('c' * 64)
        target_snapshot_sequence = 10
        target_sha256 = $deltaBaseHash
    }
}
$deltaSeedWindowsDeltaMatch = [pscustomobject]@{
    current = [pscustomobject]@{ sequence = 10; sha256 = $deltaBaseHash; length = 8192 }
    previous_exists = $true
    previous = [pscustomobject]@{ sequence = 8; sha256 = ('c' * 64); length = 6912 }
}
$deltaSeedDelta = Assert-DeltaAndroidCheckpointSeed $deltaSeedAndroidDelta $deltaSeedWindowsDeltaMatch
if ($deltaSeedDelta.delta_install_count -ne 1 -or $deltaSeedDelta.full_snapshot_install_count -ne 0 -or
    $deltaSeedDelta.seed_kind -cne 'delta' -or $deltaSeedDelta.sequence -ne 10 -or
    $deltaSeedDelta.sha256 -cne $deltaBaseHash) {
    throw 'BROKER_DELTA_SEED_DELTA_PATH_POSITIVE_FAILED'
}
$deltaSeedWindowsDeltaBad = $deltaSeedWindowsDeltaMatch | ConvertTo-Json | ConvertFrom-Json
$deltaSeedWindowsDeltaBad.previous.sha256 = ('b' * 64)
try {
    $null = Assert-DeltaAndroidCheckpointSeed $deltaSeedAndroidDelta $deltaSeedWindowsDeltaBad
    throw 'BROKER_DELTA_SEED_DELTA_PATH_NEGATIVE_ACCEPTED'
} catch {
    if ($_.Exception.Message -cne 'DELTA_SEED_IDENTITY_MISMATCH') { throw }
}
foreach ($mutation in @(
    { param($a, $w) $a.restore_diagnostics.delta_expected_chunk_count = 2 },
    { param($a, $w) $a.restore_diagnostics.delta_chunks_accepted = 0 },
    { param($a, $w) $a.restore_diagnostics.delta_duplicate_chunks = -1 },
    { param($a, $w) $a.restore_diagnostics.delta_chunk_acks_sent = 0 },
    { param($a, $w) $a.restore_diagnostics.delta_request_count = 0 },
    { param($a, $w) $a.restore_diagnostics.delta_install_count = 2 },
    { param($a, $w) $a.restore_diagnostics.delta_request_id = [guid]::Empty.ToString() },
    { param($a, $w) $a.restore_diagnostics | Add-Member delta_fallback_reason 'NEEDS_FULL_SNAPSHOT' },
    { param($a, $w) $w.previous_exists = $false; $w.previous = $null },
    { param($a, $w) $a.restore_diagnostics.base_snapshot_sequence = 10; $w.previous.sequence = 10 },
    { param($a, $w) $a.restore_diagnostics.base_sha256 = 'c'; $w.previous.sha256 = 'c' },
    { param($a, $w) $a.restore_diagnostics.target_sha256 = 'a'; $w.current.sha256 = 'a' },
    { param($a, $w) $a.restore_diagnostics.base_sha256 = $deltaBaseHash; $w.previous.sha256 = $deltaBaseHash },
    { param($a, $w) $a.snapshot_sequence_s = 9 }
)) {
    $invalidAndroid = $deltaSeedAndroidDelta | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $invalidWindows = $deltaSeedWindowsDeltaMatch | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    & $mutation $invalidAndroid $invalidWindows
    Assert-BrokerDeltaReject { Assert-DeltaAndroidCheckpointSeed $invalidAndroid $invalidWindows } 'invalid-delta-seed'
}
# Android omits optional hashes/sequences on a full restore (r229/r230 shape).
foreach ($state in @($deltaAndroid, $deltaSeedAndroidDelta)) {
    $copy = $state | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $copy | Add-Member session_id $deltaSessionId.ToString()
    $copy | Add-Member live_ack 15
    $copy.restore_diagnostics | Add-Member -Force attempt 2
    $baseline = New-DeltaPreClosedBaseline $copy
    $roundTrip = $baseline | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    if ($roundTrip.restore_diagnostics.attempt -ne 2 -or
        $roundTrip.snapshot_sequence_s -ne $copy.snapshot_sequence_s) { throw 'BROKER_SEED_BASELINE_COPY_FAILED' }
    foreach ($field in @('base_sha256', 'target_sha256', 'target_snapshot_sequence', 'full_snapshot_sha256')) {
        $original = $copy.restore_diagnostics.PSObject.Properties[$field]
        $copied = $roundTrip.restore_diagnostics.PSObject.Properties[$field]
        if (($null -eq $original) -ne ($null -eq $copied) -or
            ($null -ne $original -and $original.Value -cne $copied.Value)) {
            throw 'BROKER_SEED_BASELINE_OPTIONAL_FIELD_CHANGED'
        }
    }
}
foreach ($field in @('base_sha256', 'target_sha256', 'base_snapshot_sequence', 'target_snapshot_sequence')) {
    $invalid = $deltaRestore | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $invalid.$field = if ($field.EndsWith('sha256')) { 'd' * 64 } else { 13 }
    Assert-BrokerDeltaReject { Assert-DeltaRestoreResult $invalid $deltaSessionId $deltaBase $deltaTarget } $field
}
function Invoke-ExtendedSnapshotRestoreMock([bool]$RestoreSucceeds) {
    $definition = [pscustomobject]@{ phase = 'c05-soft-reset' }
    $phasePrevious = [pscustomobject]@{
        live_ack = 10
        terminal_semantics = [pscustomobject]@{ identity = 'baseline' }
    }
    $current = [pscustomobject]@{
        live_ack = 11
        terminal_semantics = [pscustomobject]@{ identity = 'phase' }
    }
    $calls = [Collections.Generic.List[object]]::new()
    $phaseEvidence = [ordered]@{
        snapshot_restore = $false
        status = 'semantic_passed'
    }
    $assert = {
        param($Definition, $Probe, $PreviousProbe)
        $calls.Add([pscustomobject]@{
            phase = $Definition.phase
            probe = $Probe.identity
            previous = $PreviousProbe.identity
        })
        if ($PreviousProbe.identity -cne 'baseline') { throw 'MOCK_PREVIOUS_PROBE_IDENTITY_CHANGED' }
    }.GetNewClosure()
    $restore = {
        param($Before, [string]$Label)
        if ($Before.terminal_semantics.identity -cne 'phase' -or $Label -cne 'extended-c05-soft-reset') {
            throw 'MOCK_RESTORE_BEFORE_IDENTITY_CHANGED'
        }
        if (-not $RestoreSucceeds) { throw 'MOCK_SNAPSHOT_RESTORE_FAILED' }
        [pscustomobject]@{
            live_ack = 12
            terminal_semantics = [pscustomobject]@{ identity = 'restored' }
        }
    }.GetNewClosure()
    $failed = $false
    $evidence = @()
    try {
        & $assert $definition $current.terminal_semantics $phasePrevious.terminal_semantics
        $restored = & $restore $current ('extended-' + $definition.phase)
        & $assert $definition $restored.terminal_semantics $phasePrevious.terminal_semantics
        $phaseEvidence.snapshot_restore = 'semantic_equal_after_engine_recreation'
        $evidence += $phaseEvidence
    } catch {
        $failed = $true
    }
    [pscustomobject]@{
        failed = $failed
        evidence = $evidence
        calls = $calls
    }
}
$extendedRestorePassed = Invoke-ExtendedSnapshotRestoreMock $true
if ($extendedRestorePassed.failed -or $extendedRestorePassed.evidence.Count -ne 1 -or
    $extendedRestorePassed.calls.Count -ne 2 -or
    $extendedRestorePassed.calls[0].probe -cne 'phase' -or
    $extendedRestorePassed.calls[1].probe -cne 'restored' -or
    @($extendedRestorePassed.calls | Where-Object { $_.previous -cne 'baseline' }).Count -ne 0 -or
    $extendedRestorePassed.evidence[0].snapshot_restore -cne 'semantic_equal_after_engine_recreation') {
    throw 'BROKER_EXTENDED_SNAPSHOT_RESTORE_MOCK_POSITIVE_FAILED'
}
$extendedRestoreFailed = Invoke-ExtendedSnapshotRestoreMock $false
if (-not $extendedRestoreFailed.failed -or $extendedRestoreFailed.evidence.Count -ne 0 -or
    $extendedRestoreFailed.calls.Count -ne 1) {
    throw 'BROKER_EXTENDED_SNAPSHOT_RESTORE_MOCK_FAILURE_RECORDED_AS_PASS'
}
foreach ($name in @('Find-WheelControls', 'Assert-HostCompletion')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -eq $definition) { throw "BROKER_FUNCTION_MISSING: $name" }
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$wheelXml = '<hierarchy><node package="dev.termlink.harness.recovery" content-desc="鼠标滚轮向上" enabled="true" bounds="[10,20][30,40]"/><node package="dev.termlink.harness.recovery" content-desc="Mouse wheel down" enabled="true" bounds="[10,50][30,70]"/></hierarchy>'
$points = Find-WheelControls ([xml]$wheelXml) $true
if ($points.Count -ne 2 -or ($points.up -join ',') -cne '20,30' -or
    ($points.down -join ',') -cne '20,60' -or
    (Find-WheelControls ([xml]'<hierarchy/>') $false).Count -ne 0) { throw 'BROKER_WHEEL_UI_POSITIVE_FAILED' }
foreach ($bad in @('<hierarchy/>', $wheelXml.Replace('[10,20][30,40]', 'invalid'),
    $wheelXml.Replace('[10,20][30,40]', '[30,20][10,40]'),
    $wheelXml.Replace('</hierarchy>', '<node package="dev.termlink.harness.recovery" content-desc="鼠标滚轮向上" enabled="true" bounds="[10,20][30,40]"/></hierarchy>'))) {
    $failed = $false
    try { $null = Find-WheelControls ([xml]$bad) $true } catch { $failed = $true }
    if (-not $failed) { throw 'BROKER_WHEEL_UI_NEGATIVE_FAILED' }
}
Assert-HostCompletion $true 0 $false $false
Assert-HostCompletion $true 125 $false $true
foreach ($case in @(@($false, 0, $false, $false), @($true, 125, $false, $false),
    @($true, 0, $true, $false), @($true, 0, $false, $true), @($true, $null, $false, $false))) {
    $failed = $false
    try { Assert-HostCompletion $case[0] $case[1] $case[2] $case[3] } catch { $failed = $true }
    if (-not $failed) { throw 'BROKER_HOST_EXIT_NEGATIVE_FAILED' }
}
if (-not $source.Contains('DIAGNOSTIC_ACCEPTANCE_REQUIRES_INDEPENDENT_RUN') -or
    -not $source.Contains('Invoke-TerminalEffectsProductE2E.ps1') -or
    -not $source.Contains("'partial' } else { 'passed' }")) { throw 'BROKER_EFFECTS_COMPLETION_GUARD_MISSING' }
Add-Type -AssemblyName System.Drawing
$beforePath = [IO.Path]::GetTempFileName()
$afterPath = [IO.Path]::GetTempFileName()
$beforeBitmap = [Drawing.Bitmap]::new(400, 800)
$afterBitmap = [Drawing.Bitmap]::new(400, 800)
try {
    $canvas = [Drawing.Graphics]::FromImage($beforeBitmap)
    try { $canvas.Clear([Drawing.Color]::White) } finally { $canvas.Dispose() }
    $canvas = [Drawing.Graphics]::FromImage($afterBitmap)
    try {
        $canvas.Clear([Drawing.Color]::White)
        $canvas.FillRectangle([Drawing.Brushes]::Black, 20, 160, 11, 11)
        $canvas.FillRectangle([Drawing.Brushes]::Black, 300, 160, 20, 20)
        $canvas.FillRectangle([Drawing.Brushes]::Red, 20, 190, 2, 6)
        $canvas.FillRectangle([Drawing.Brushes]::Red, 300, 190, 5, 6)
    } finally { $canvas.Dispose() }
    $beforeBitmap.Save($beforePath, [Drawing.Imaging.ImageFormat]::Png)
    $afterBitmap.Save($afterPath, [Drawing.Imaging.ImageFormat]::Png)
    if ((Count-NewDrcsPixels $beforePath $beforePath).Count -ne 0 -or
        (Count-NewDrcsPixels $beforePath $afterPath).Count -ne 121) {
        throw 'BROKER_DRCS_PIXEL_ASSERTION_INVALID'
    }
    if ((Count-NewSixelPixels $beforePath $beforePath).Count -ne 0 -or
        (Count-NewSixelPixels $beforePath $afterPath).Count -ne 12) {
        throw 'BROKER_SIXEL_PIXEL_ASSERTION_INVALID'
    }
} finally {
    $beforeBitmap.Dispose()
    $afterBitmap.Dispose()
    Remove-Item -LiteralPath $beforePath, $afterPath -Force
}

'Broker default path, nine fixed protocol vectors, DRCS pixel assertion, and both PowerShell parsers passed.'
