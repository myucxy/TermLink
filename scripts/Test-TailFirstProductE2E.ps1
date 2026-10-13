[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-FailureMessage {
    param([Parameter(Mandatory)][scriptblock]$Action)
    try { & $Action | Out-Null }
    catch { return $_.Exception.Message }
    throw 'Expected the action to fail.'
}

function Start-FakeAgentStatusServer {
    param(
        [Parameter(Mandatory)][string]$AgentControlAssembly,
        [Parameter(Mandatory)][string]$PipeName,
        [Parameter(Mandatory)][string]$ServerOrigin,
        [bool]$RemoteAccessEnabled = $true,
        [bool]$AccountLinked = $true,
        [ValidateSet('Connected', 'Connecting', 'Reconnecting', 'Failed', 'Stopped', 'NotConfigured')]
        [string]$RelayState = 'Connected',
        [bool]$TerminalSnapshotEnabled = $true,
        [bool]$Success = $true
    )
    $quotedAssembly = $AgentControlAssembly.Replace("'", "''")
    $quotedPipe = $PipeName.Replace("'", "''")
    $quotedOrigin = $ServerOrigin.Replace("'", "''")
    $serverScript = @"
`$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::LoadFrom('$quotedAssembly') | Out-Null
`$server = [IO.Pipes.NamedPipeServerStream]::new(
    '$quotedPipe',
    [IO.Pipes.PipeDirection]::InOut,
    1,
    [IO.Pipes.PipeTransmissionMode]::Byte,
    [IO.Pipes.PipeOptions]::None)
try {
    `$server.WaitForConnection()
    `$request = [TermLink.Agent.RegistryControlCodec]::ReadRequest(`$server)
    `$secret = [TermLink.Agent.RegistryControlSecretStore]::LoadDefault()
    try {
        if (-not [TermLink.Agent.RegistryControlAuthentication]::Verify(`$request, `$secret, [DateTime]::UtcNow)) {
            throw 'Agent request authentication failed.'
        }
        if (`$request.Command -ne [TermLink.Agent.RegistryCommand]::RefreshProductionStatus) {
            throw 'Unexpected Agent command.'
        }
        `$production = [TermLink.Agent.ProductionStatus]::new()
        `$production.Configured = [bool]::Parse('$($AccountLinked.ToString().ToLowerInvariant())')
        `$production.AccountLinked = [bool]::Parse('$($AccountLinked.ToString().ToLowerInvariant())')
        `$production.ServerOrigin = '$quotedOrigin'
        `$production.TerminalSnapshotEnabled = [bool]::Parse('$($TerminalSnapshotEnabled.ToString().ToLowerInvariant())')
        `$production.RelayState = [TermLink.Agent.ProductionRelayState]::$RelayState
        `$response = [TermLink.Agent.RegistryResponse]::new()
        `$response.Success = [bool]::Parse('$($Success.ToString().ToLowerInvariant())')
        `$response.RemoteAccessEnabled = [bool]::Parse('$($RemoteAccessEnabled.ToString().ToLowerInvariant())')
        `$response.Production = `$production
        [TermLink.Agent.RegistryControlAuthentication]::PrepareResponse(`$response, `$request, `$secret)
        [TermLink.Agent.RegistryControlCodec]::WriteResponse(`$server, `$response, `$request.ProtocolVersion)
    }
    finally {
        if (`$null -ne `$secret) { [Array]::Clear(`$secret, 0, `$secret.Length) }
    }
}
finally {
    `$server.Dispose()
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($serverScript))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $PSHOME 'pwsh.exe'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    Assert-True ($null -ne $process) 'The fake Agent status server could not start.'
    return $process
}

function Stop-FakeAgentStatusServer {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.WaitForExit(10000)) { $Process.Kill($true); $Process.WaitForExit(5000) | Out-Null }
        Assert-True ($Process.ExitCode -eq 0) `
            ('The fake Agent status server failed: ' + $Process.StandardError.ReadToEnd())
    }
    finally { $Process.Dispose() }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptPath = Join-Path $PSScriptRoot 'Invoke-TailFirstProductE2E.ps1'
$source = Get-Content -Raw -LiteralPath $scriptPath
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) `
    ("Invoke-TailFirstProductE2E.ps1 parser errors:`n" + (($parseErrors | ForEach-Object { $_.Message }) -join "`n"))

$frozenTransitionFunction = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Test-FrozenPageTransition'
}, $true))
Assert-True ($frozenTransitionFunction.Count -eq 1) `
    'The runner must use exactly one Frozen PAGE identity and anchor validator.'
Invoke-Expression $frozenTransitionFunction[0].Extent.Text
$frozenBegin = [pscustomobject]@{
    frozen_active = $true; frozen_render_ready = $true; frozen_capture_id = 81L
    frozen_revision = 42L; frozen_snapshot_sequence_s = 41L; frozen_page_count = 1
    frozen_first_line_id = 501L; frozen_next_before_line_id = 500L; frozen_has_more_before = $true
    frozen_head_truncated = $false
}
$frozenPage = [pscustomobject]@{
    frozen_active = $true; frozen_render_ready = $true; frozen_capture_id = 81L
    frozen_revision = 42L; frozen_snapshot_sequence_s = 41L; frozen_page_count = 2
    frozen_first_line_id = 301L; frozen_next_before_line_id = 300L; frozen_has_more_before = $true
    frozen_head_truncated = $false
}
$seenFrozenAnchors = [Collections.Generic.HashSet[uint64]]::new()
$null = $seenFrozenAnchors.Add(500)
Assert-True (Test-FrozenPageTransition $frozenBegin $frozenBegin $frozenPage $seenFrozenAnchors) `
    'A valid rendered PAGE on the same capture was rejected.'
Assert-True (-not (Test-FrozenPageTransition $frozenBegin $frozenBegin $frozenPage $seenFrozenAnchors)) `
    'A repeated Frozen anchor was accepted.'
$frozenFinalPage = $frozenPage | ConvertTo-Json | ConvertFrom-Json
$frozenFinalPage.frozen_first_line_id = 101L
$frozenFinalPage.frozen_next_before_line_id = 0L
$frozenFinalPage.frozen_has_more_before = $false
$frozenFinalPage.frozen_head_truncated = $true
Assert-True (Test-FrozenPageTransition $frozenBegin $frozenPage $frozenFinalPage $seenFrozenAnchors) `
    'The oldest rendered PAGE with no next cursor was rejected.'
foreach ($change in @(
    @{ frozen_capture_id = 82L }, @{ frozen_revision = 43L },
    @{ frozen_snapshot_sequence_s = 42L }, @{ frozen_render_ready = $false },
    @{ frozen_head_truncated = 'false' }, @{ frozen_next_before_line_id = 0L },
    @{ frozen_first_line_id = 301L }, @{ frozen_page_count = 1 }
)) {
    $invalidPage = $frozenPage | ConvertTo-Json | ConvertFrom-Json
    $invalidPage.frozen_next_before_line_id = 200L
    foreach ($key in $change.Keys) { $invalidPage.$key = $change[$key] }
    Assert-True (-not (Test-FrozenPageTransition $frozenBegin $frozenPage $invalidPage $seenFrozenAnchors)) `
        "The Frozen PAGE validator accepted invalid $(@($change.Keys)[0])."
}
Assert-True ($source.Contains('[switch]$FrozenLifecycleAcceptance') -and
    $source.Contains('$markerCount = if ($FrozenLifecycleAcceptance) {') -and
    $source.Contains('1400') -and $source.Contains("'900000'") -and
    $source.Contains('$columns = __FROZEN_COLUMNS_SOURCE__') -and
    $source.Contains("Replace('__FROZEN_COLUMNS_SOURCE__', '[Console]::WindowWidth')") -and
    $source.Contains('[Console]::Write([char]27 +') -and
    $source.Contains('$resizeClock.Elapsed.TotalSeconds -lt 15') -and
    $source.Contains('$padding = ''X'' * ($columns * 20)') -and
    $source.Contains("'FROZEN_FIXTURE_RESIZE_FAILED'") -and
    $source.Contains("'frozen-fixture-child-observed-32-columns'") -and
    $source.Contains("'frozen-fixture-android-observed-32-columns'") -and
    $source.Contains("'frozen-fixture-width-marker-visible'") -and
    $source.Contains("'TLTF32_'") -and
    $source.Contains('[Convert]::ToHexString($liveMarkerDigest, 0, 4).ToLowerInvariant()') -and
    $source.Contains('$openSessionArguments = @{') -and
    $source.Contains('$openSessionArguments.frozen_lifecycle_acceptance = ''true''') -and
    $source.Contains("'SnapshotTranscript' -and") -and
    $source.Contains('-not $ExpectFrozenUnsupported')) `
    'Frozen lifecycle acceptance does not gate its oversized fixture and extended SessionHost timeout.'
Assert-True ($source.Contains('$responseResult = Invoke-NativeCapture $script:adbPath') -and
    $source.Contains("'Debug hook open_session failure response'") -and
    $source.Contains('$script:hookFailureCode = $stableFailureCode') -and
    $source.Contains('Debug hook operation ''open_session'' reported failure_code ''$stableFailureCode''.' )) `
    'Wait-SessionArm does not fail fast with the matching open_session failure response code.'
$frozenTerminalParameter = @($ast.ParamBlock.Parameters | Where-Object {
    $_.Name.VariablePath.UserPath -eq 'FrozenTerminalState'
})
Assert-True ($frozenTerminalParameter.Count -eq 1) `
    'The runner does not declare exactly one FrozenTerminalState parameter.'
$frozenTerminalValidateSet = @($frozenTerminalParameter[0].Attributes | Where-Object {
    $_.TypeName.Name -eq 'ValidateSet'
})
Assert-True ($frozenTerminalValidateSet.Count -eq 1 -and
    @($frozenTerminalValidateSet[0].PositionalArguments | ForEach-Object { $_.SafeGetValue() }) -ccontains 'terminal_state_2_22_v1' -and
    @($frozenTerminalValidateSet[0].PositionalArguments | ForEach-Object { $_.SafeGetValue() }) -ccontains 'terminal_state_2_22_v2') `
    'FrozenTerminalState does not expose exactly the v1 and v2 state contracts.'
Assert-True ($frozenTerminalParameter[0].DefaultValue.Extent.Text -eq "'terminal_state_2_22_v2'") `
    'FrozenTerminalState does not default to terminal_state_2_22_v2.'
Assert-True ($source.Contains('$FrozenGraphicsMode -or -not $PSBoundParameters.ContainsKey(''FrozenTerminalState'')') -and
    $source.Contains('''--terminal-state'', $FrozenTerminalState')) `
    'FrozenTerminalState is not restricted to graphics mode or forwarded explicitly.'
foreach ($proof in @(
    "'frozen-history-page-after-reconnect'", "'frozen-reconnect-page-same-capture'",
    "'frozen-oldest-boundary-reached'", "'frozen-oldest-head-truncated'",
    "'frozen-ttl-first-page-before-deadline'", "'frozen-ttl-page-does-not-renew-lease'",
    "'frozen-expired-slot-reusable'", "'FROZEN_RECONNECT_PAGE_INVALID'",
    "'STALE_CAPTURE'", 'Test-FrozenPageTransition $begin $afterLivePage $reconnectedPage',
    '[Diagnostics.Stopwatch]::StartNew()', 'Wait-FrozenCaptureAge $ttlClock 240',
    'Wait-FrozenCaptureAge $ttlClock 305', '$ttlFirstPageCompletedSeconds -lt 295',
    '$frozenPageCalls -lt 256', '[Math]::Min(1000'
)) {
    Assert-True ($source.Contains($proof)) "The Frozen lifecycle runner lacks $proof."
}

$literalValues = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.StringConstantExpressionAst]
}, $true) | ForEach-Object { $_.Value })

$frozenLiveInput = [regex]::Match($source,
    '(?s)\$script:stage = ''frozen-release-live-flow''.*?(?=\$deadline =)').Value
Assert-True (-not [string]::IsNullOrEmpty($frozenLiveInput) -and
    -not $frozenLiveInput.Contains('StandardInput') -and
    $frozenLiveInput.Contains("Invoke-HookOperation 'show_ime'") -and
    $frozenLiveInput.Contains('''input'', ''text'', $liveMarker') -and
    $frozenLiveInput.Contains("'input', 'keyevent', 'KEYCODE_ENTER'")) `
    'Frozen native-display mode must submit the live marker through the Android keyboard, not unread host stdin.'
Assert-True ($source.Contains('Start-ThreadJob -ArgumentList $frozenOutputTrigger') -and
    $source.Contains('[Console]::WriteLine($marker)') -and
    $source.Contains("'frozen-live-output-ack-advanced'") -and
    $source.Contains("'frozen-live-output-changed-live-text'") -and
    $source.Contains("'frozen-live-output-retains-capture'") -and
    $source.Contains("'frozen-live-output-retains-revision'")) `
    'Frozen concurrent output must originate in the owned Windows ConPTY and preserve its capture.'
Assert-True ($source.Contains("'frozen-relay-disconnected'") -and
    $source.Contains("'frozen-relay-successor-subscribed'") -and
    $source.Contains("'frozen-reconnect-new-capture'") -and
    $source.Contains("'frozen-rebrowse-release-clears-overlay'")) `
    'Frozen Relay reconnect must release and reopen a new capture.'
Assert-True ($source.Contains("'old-host-reject-clears-frozen-state'") -and
    $source.Contains("'old-host-reject-live-continues'") -and
    $source.Contains("'FROZEN_UNSUPPORTED_CLOSED_LIVE_CLIENT'")) `
    'Old Host refusal must release the frozen state and leave the live Relay client usable.'

$targetParameter = @($ast.ParamBlock.Parameters | Where-Object {
    $_.Name.VariablePath.UserPath -eq 'TargetMode'
})
Assert-True ($targetParameter.Count -eq 1) 'The runner does not declare exactly one TargetMode parameter.'
Assert-True ($targetParameter[0].DefaultValue.Extent.Text -eq "'ManagedAcceptanceCompose'") `
    'TargetMode does not default to ManagedAcceptanceCompose.'
$targetModeAttribute = @($targetParameter[0].Attributes | Where-Object {
    $_.TypeName.Name -eq 'ValidateSet'
})
Assert-True ($targetModeAttribute.Count -eq 1) 'TargetMode does not have a ValidateSet.'
$targetModeValues = @($targetModeAttribute[0].PositionalArguments | ForEach-Object {
    $_.SafeGetValue()
})
Assert-True ($targetModeValues.Count -eq 2 -and
    $targetModeValues -ccontains 'ManagedAcceptanceCompose' -and
    $targetModeValues -ccontains 'ExternalHttps') 'TargetMode does not expose exactly the two supported modes.'

$observationTimeoutParameter = @($ast.ParamBlock.Parameters | Where-Object {
    $_.Name.VariablePath.UserPath -eq 'ObservationTimeoutSeconds'
})
Assert-True ($observationTimeoutParameter.Count -eq 1) `
    'The runner does not declare exactly one ObservationTimeoutSeconds parameter.'
$observationTimeoutDefault = [int]$observationTimeoutParameter[0].DefaultValue.SafeGetValue()
Assert-True ($observationTimeoutDefault -ge 300) `
    'The runner observation timeout is too short for the 5200-event streamed-prefix fixture.'

Assert-True ($source.Contains("Get-ManifestTool 'android-sdk'")) 'The runner does not select ADB through D:\Dev.'
Assert-True ($source.Contains("Get-ManifestTool 'dotnet'")) 'The runner does not preflight the D:\Dev .NET SDK.'
Assert-True ($source.Contains("Get-ManifestTool 'go'")) 'The runner does not preflight the D:\Dev Go toolchain.'
Assert-True ($source.Contains('$snapshotMarkerCount = 5200')) `
    'The Snapshot/Transcript runner does not cross the TL-H115 4096-line boundary.'
Assert-True ($source.Contains('$snapshotTranscriptPageCount = 26')) `
    'The Snapshot/Transcript runner does not require the complete 26-page chain.'
Assert-True ($source.Contains('$snapshotTailFirstBeforeLineId = [uint64]5177')) `
    'The Snapshot/Transcript runner does not pin the exact 24-row live tail.'
Assert-True ($source.Contains('$tlH115BoundaryLineIds = [uint64[]]@(4095, 4096, 4097, 5200)')) `
    'The Snapshot/Transcript runner does not name all TL-H115 boundary lines.'
Assert-True ($source.Contains('$streamedPrefixMarkerCount = 5200')) `
    'The streamed Chunk-prefix runner does not emit enough independently flushed events.'
Assert-True ($source.Contains('$streamedPrefixMinimumTail = [uint64]4097')) `
    'The streamed Chunk-prefix runner does not gate open beyond the 4096-event window.'
Assert-True ($source.Contains("'NATIVE_FULL_PREFIX'")) `
    'The streamed Chunk-prefix runner does not require native terminal-core authority.'
foreach ($streamedProof in @(
    "'streamed-prefix-ack-exceeds-4096'",
    "'streamed-prefix-bootstrap-source'",
    "'streamed-prefix-first-marker-visible'",
    "'streamed-prefix-last-marker-visible'",
    "'streamed-prefix-live-ack-advanced'",
    "'streamed-prefix-live-marker-visible'"
)) {
    Assert-True ($source.Contains($streamedProof)) `
        "The streamed Chunk-prefix scenario lacks proof $streamedProof."
}
foreach ($rangeProof in @(
    '$requestBeforeLineId = $snapshotTailFirstBeforeLineId',
    '$lastLineId = $requestBeforeLineId - 1',
    '$lastLineId - (($lastLineId - 1) % [uint64]$HistoryPageSize)',
    '$requestBeforeLineId = $firstLineId'
)) {
    Assert-True ($source.Contains($rangeProof)) "The runner lacks generated transcript geometry '$rangeProof'."
}
Assert-True ($source.Contains("'artifacts\tail-first-e2e'")) 'The runner does not anchor state below artifacts/tail-first-e2e.'
Assert-True ($source.Contains("'-no-snapshot-load'")) 'The runner does not require a cold emulator boot.'
Assert-True ($source.Contains("'-no-snapshot-save'")) 'The runner may persist an acceptance emulator snapshot.'
Assert-True ($source.Contains('[switch]$ShowEmulator')) 'The runner lacks the ShowEmulator parameter.'
Assert-True ($source.Contains("if (-not `$ShowEmulator) { `$emulatorArguments += '-no-window' }")) `
    'The runner does not keep headless mode conditional on ShowEmulator.'
Assert-True ($source.Contains("'shell', 'am', 'broadcast', '--async', '-a', `$hookAction")) `
    'The runner blocks on the open_session broadcast before SessionHost can start.'
Assert-True ($source.Contains('$invalidJsonSeen = $true')) `
    'The asynchronous hook reader does not tolerate an in-progress AtomicFile write.'
Assert-True ($source.Contains('$script:hookFailureCode = $stableFailureCode')) `
    'The runner does not retain a validated Debug hook failure_code for evidence.'
Assert-True ($source.Contains("reported failure_code '`$stableFailureCode'.")) `
    'The runner public error does not expose the validated stable Debug hook failure_code.'
Assert-True ($source.Contains('hook_failure_code = $script:hookFailureCode')) `
    'The runner failure evidence does not expose the validated stable Debug hook failure_code.'
Assert-True ($source.Contains('E2E_HOOK_CONTRACT_MISSING')) 'The missing Debug hook does not have an explicit failure code.'
Assert-True ($source.Contains("'TermLink.AgentControl.dll'")) 'The runner does not load AgentControl beside SessionHost.'
Assert-True ($source.Contains("'RefreshProductionStatus'")) 'The runner does not request refreshed Agent production status.'
Assert-True ($source.Contains("'TermLink.Agent.TermLinkControlPipe'")) 'The runner does not resolve the SID-default Agent pipe.'
foreach ($readinessField in @(
    'RemoteAccessEnabled', 'AccountLinked', 'RelayState', 'ServerOrigin', 'TerminalSnapshotEnabled'
)) {
    Assert-True ($source.Contains($readinessField)) "The runner does not validate Agent field '$readinessField'."
}
$agentPreflightIndex = $source.IndexOf("`$script:stage = 'preflight-windows-agent'", [StringComparison]::Ordinal)
$sessionStartIndex = $source.IndexOf('Diagnostics.Process]::Start($sessionStart)', [StringComparison]::Ordinal)
Assert-True ($agentPreflightIndex -ge 0 -and $agentPreflightIndex -lt $sessionStartIndex) `
    'The Agent preflight does not run before SessionHost starts.'
$sessionArmIndex = $source.IndexOf('$sessionArm = Wait-SessionArm $openSessionRequest', [StringComparison]::Ordinal)
$openResponseIndex = $source.IndexOf('$initial = Wait-HookOperation $openSessionRequest', [StringComparison]::Ordinal)
Assert-True ($sessionArmIndex -ge 0 -and $sessionArmIndex -lt $sessionStartIndex -and
    $sessionStartIndex -lt $openResponseIndex) `
    'SessionHost does not start strictly after Android arms the existing-session baseline.'
foreach ($coldStateProof in @(
    'databases/termlink-history.db',
    'databases/termlink-history.db-shm',
    'databases/termlink-history.db-wal',
    'shared_prefs/termlink_product_preferences.xml',
    'oidc_session_preserved = $true',
    'device_identity_preserved = $true'
)) {
    Assert-True ($source.Contains($coldStateProof)) "The selective Android cold-state reset lacks '$coldStateProof'."
}
Assert-True ($source.Contains('raw exception text was suppressed')) 'Unexpected errors are not fail-closed for secret output.'
Assert-True (-not $source.Contains('Start-ProductionAcceptance.ps1')) 'The runner can start the acceptance stack.'
Assert-True (-not $source.Contains('Stop-ProductionAcceptance.ps1')) 'The runner can stop the acceptance stack.'
Assert-True ($source -notmatch "(?im)^.*'compose'.*'(?:up|down|start|stop|restart|rm|kill)'.*$") `
    'The runner contains a Compose lifecycle action.'
Assert-True ($literalValues -ccontains 'ps') 'The runner does not inspect Compose status with ps.'
Assert-True ($source -notmatch '(?i)Get-Content[^\r\n]+server\\secrets') `
    'The runner reads an acceptance secret file.'
Assert-True ($source.Contains("if (`$TargetMode -ceq 'ManagedAcceptanceCompose')")) `
    'The runner does not explicitly isolate the managed Compose preflight.'
Assert-True ($source.Contains("`$null = Get-ExternalHttpsPreflight `$AcceptanceOrigin `$externalEvidence")) `
    'The runner does not route ExternalHttps through the public HTTPS preflight.'
Assert-True ($source.Contains('AllowAutoRedirect = $false')) `
    'The external preflight does not reject redirects.'
Assert-True ($source.Contains('UseCookies = $false')) `
    'The external preflight may attach or retain cookies.'
Assert-True ($source.Contains('[Net.Http.HttpCompletionOption]::ResponseHeadersRead')) `
    'The external preflight does not bound response processing from headers onward.'

$stateObservationFunction = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-StateObservation'
}, $true))
Assert-True ($stateObservationFunction.Count -eq 1) `
    'The runner does not declare exactly one state-observation validator.'
$stateObservationSource = $stateObservationFunction[0].Extent.Text
foreach ($gridField in @('terminal_columns', 'terminal_rows')) {
    Assert-True ($stateObservationSource.Contains("'$gridField'")) `
        "State observations do not require terminal grid field '$gridField'."
}
Assert-True ($stateObservationSource.Contains(
    "foreach (`$field in @('terminal_columns', 'terminal_rows'))")) `
    'Terminal grid dimensions are not validated as positive bounded integers.'

$initialHistoryStart = $source.IndexOf(
    "`$overlay = Invoke-HookOperation 'open_history'", [StringComparison]::Ordinal)
$historyPagingStart = $source.IndexOf(
    "`$script:stage = 'history-page-up'", [StringComparison]::Ordinal)
Assert-True ($initialHistoryStart -ge 0 -and $historyPagingStart -gt $initialHistoryStart) `
    'The initial history-load assertion block is missing or misplaced.'
$initialHistorySource = $source.Substring(
    $initialHistoryStart, $historyPagingStart - $initialHistoryStart)
foreach ($initialAssertion in @(
    "'next_before_line_id'",
    "'history-initial-load-single-page'",
    "'history-initial-load-bounded-rows'",
    "'history-initial-load-exact-row-count'",
    "'history-initial-load-transcript-count'",
    "'history-initial-load-snapshot-cache-matches-rebase'",
    "'history-initial-load-viewport-changed'",
    "'history-initial-load-snapshot-rebase-covered-by-ack'",
    "'history-initial-load-live-ack-not-regressed'",
    "'history-initial-request-cursor'",
    "'history-initial-first-line'",
    "'history-initial-last-line'",
    "'history-initial-has-more'",
    "'history-initial-next-cursor'"
)) {
    Assert-True ($initialHistorySource.Contains($initialAssertion)) `
        "The initial history load lacks required proof '$initialAssertion'."
}
Assert-True ($initialHistorySource.Contains(
    '$overlayObservation.snapshot_sequence_s -le $overlayObservation.live_ack'
)) 'The initial Snapshot rebase is not fenced by its same-response durable ACK.'
Assert-True ($initialHistorySource.Contains('$fixedAck = $overlayObservation.live_ack')) `
    'Continuation pages are not pinned to the ACK sampled with the rebased Snapshot.'
Assert-True ($initialHistorySource.Contains(
    '$overlayObservation.snapshot_sequence_s -ge $initialSnapshot')) `
    'The initial history load does not reject a regressing Snapshot rebase.'
Assert-True ($initialHistorySource.Contains(
    '$overlayObservation.live_ack -ge $fixedAck')) `
    'The initial history load permits the durable live ACK to regress.'
Assert-True ($initialHistorySource.Contains(
    '$fixedSnapshot = $overlayObservation.snapshot_sequence_s')) `
    'Transcript continuation pages are not fixed to the authenticated rebased Snapshot.'

$paginationFunction = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-PaginationObservation'
}, $true))
Assert-True ($paginationFunction.Count -eq 1) `
    'The runner does not declare exactly one pagination assertion helper.'
$paginationSource = $paginationFunction[0].Extent.Text
foreach ($paginationProof in @(
    'transcript-count-advanced-once',
    'snapshot-count-fixed',
    'chunk-count-fixed',
    'grid-columns-fixed',
    'grid-rows-fixed',
    'viewport-not-duplicate',
    'exact-row-count',
    'previous-page-had-cursor',
    'request-follows-previous-cursor',
    'request-cursor',
    'first-line',
    'last-line',
    'has-more',
    'next-cursor'
)) {
    Assert-True ($paginationSource.Contains($paginationProof)) `
        "Pagination does not record required proof '$paginationProof'."
}
Assert-True ($paginationSource.Contains('$Previous.transcript_count + 1')) `
    'Pagination does not require the transcript cache count to advance exactly once.'
Assert-True ($paginationSource.Contains(
    '$observation.snapshot_count -eq $Previous.snapshot_count')) `
    'Pagination does not keep the authenticated Snapshot cache fixed after the initial rebase.'
Assert-True ($paginationSource.Contains(
    '$SeenViewportSignatures.Add($observation.viewport_signature)')) `
    'Pagination does not reject a duplicate rendered viewport.'

$failureRetentionStart = $source.IndexOf(
    "`$script:stage = 'history-failure-retention'", [StringComparison]::Ordinal)
$liveFlowStart = $source.IndexOf(
    "`$script:stage = 'history-overlay-live-flow'", [StringComparison]::Ordinal)
Assert-True ($failureRetentionStart -ge 0 -and $liveFlowStart -gt $failureRetentionStart) `
    'The controlled history-failure stage is missing or misplaced.'
$failureRetentionSource = $source.Substring(
    $failureRetentionStart, $liveFlowStart - $failureRetentionStart)
foreach ($failureProof in @(
    'Set-AndroidAirplaneMode $true',
    '-AcceptFailure',
    "'offline-page-load-failed-closed'",
    "'offline-page-load-network-failure'",
    "'offline-probe-has-unloaded-page'",
    "'offline-probe-request-cursor'",
    "expected_result = 'transcript_load_failed'",
    "'history-offline-baseline'",
    "'offline-baseline-observed-network-loss'",
    "'offline-baseline-history-objects'",
    "realtime_remote_state -in @('READ_ONLY', 'RECONNECTING')",
    '$offlineStableViewportSamples -ge 2',
    "'failure-retains-history-objects'",
    "'failure-retains-viewport'",
    "'failure-retains-first-visible-line-id'",
    "'failure-retains-visible-text-hash'",
    "'failure-retains-transcript-top-row'",
    "'failure-retains-grid-columns'",
    "'failure-retains-grid-rows'",
    "'02a-failed-page-retained.png'",
    'Set-AndroidAirplaneMode $false',
    "'history-network-recovery'",
    '[DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)',
    "'online-retry-observed-network-recovery'",
    "'online-retry-retains-history-objects'",
    "realtime_socket_state -ceq 'CONNECTED'",
    "realtime_transport_state -ceq 'CONNECTED'",
    "realtime_remote_state -ceq 'CONTROLLING'"
)) {
    Assert-True ($failureRetentionSource.Contains($failureProof)) `
        "The controlled history failure lacks required proof '$failureProof'."
}
Assert-True ($failureRetentionSource.Contains("[string]`$failedPage.failure_code -ceq 'TRANSCRIPT_LOAD_FAILED'")) `
    'The offline probe accepts a generic OPERATION_FAILED response.'
Assert-True ($failureRetentionSource.Contains(
    '$retainedObservation.viewport_signature -ceq $offlineBaselineObservation.viewport_signature')) `
    'Failure retention is not measured from the immediate post-network-transition viewport.'
$finalRetryIndex = $source.IndexOf("`$script:stage = 'history-final-page-retry'", [StringComparison]::Ordinal)
Assert-True ($finalRetryIndex -gt $failureRetentionStart -and $finalRetryIndex -lt $liveFlowStart) `
    'The final transcript page is not retried online before the live-flow stage.'
foreach ($recoveryProof in @(
    "expected_result = 'success'",
    "'history-complete-all-distinct-pages'",
    "'02b-complete-history.png'"
)) {
    Assert-True ($source.Substring($finalRetryIndex, $liveFlowStart - $finalRetryIndex).Contains($recoveryProof)) `
        "The final-page recovery lacks required proof '$recoveryProof'."
}

$receiverPath = Join-Path $repositoryRoot `
    'android\app\src\debug\kotlin\dev\termlink\product\debug\TailFirstE2EReceiver.kt'
$receiverSource = Get-Content -Raw -LiteralPath $receiverPath
foreach ($receiverProof in @(
    'CoroutineStart.UNDISPATCHED',
    'writeSessionArmAtomically',
    'EXPECTED_PAGE_RESULT_TRANSCRIPT_LOAD_FAILED',
    'FAILURE_TRANSCRIPT_LOAD_FAILED',
    'tailFirstLiveMarker(runId)',
    'next_before_line_id'
)) {
    Assert-True ($receiverSource.Contains($receiverProof)) `
        "The Debug receiver lacks required tail-first proof '$receiverProof'."
}
Assert-True ($source.Contains('[Security.Cryptography.SHA256]::HashData(') -and
    $source.Contains("`$liveMarker = 'TLTF_LIVE_'")) `
    'The live marker is not a short run-unique value that remains contiguous below 80 columns.'
Assert-True (-not $source.Contains('"${markerStem}_LIVE_AFTER_OVERLAY"')) `
    'The runner still uses an ArtifactRunId-sized live marker that ConPTY may hard-wrap.'
$fallbackRestoreIndex = $source.IndexOf(
    'if ($script:airplaneModeEnabled -and $null -ne $script:adbPath)',
    [StringComparison]::Ordinal)
$keepEmulatorIndex = $source.IndexOf(
    'if ($script:emulatorOwned -and -not $KeepEmulator)',
    [StringComparison]::Ordinal)
Assert-True ($fallbackRestoreIndex -ge 0 -and $keepEmulatorIndex -gt $fallbackRestoreIndex) `
    'Android network restoration is not attempted before KeepEmulator cleanup is applied.'
$fallbackRestoreSource = $source.Substring(
    $fallbackRestoreIndex, $keepEmulatorIndex - $fallbackRestoreIndex)
Assert-True ($fallbackRestoreSource.Contains("'airplane_mode_on'")) `
    'Fallback network cleanup does not verify that airplane mode is disabled.'
Assert-True ($fallbackRestoreSource.Contains('android_network_restored = $true')) `
    'Fallback network cleanup does not record verified restoration.'

foreach ($imeProof in @(
    "'ime-layout-grid-columns-fixed'",
    "'ime-layout-grid-rows-fixed'",
    "'ime-layout-viewport-projection-fixed'",
    "'terminal-extends-to-bottom-bar'",
    '$terminalBarGap -in 0, 1'
)) {
    Assert-True ($source.Contains($imeProof)) `
        "The IME layout stage lacks required proof '$imeProof'."
}

$managedFunction = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-AcceptanceStackState'
}, $true))[0]
$externalFunction = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-ExternalHttpsPreflight'
}, $true))[0]
Assert-True ($null -ne $managedFunction -and $null -ne $externalFunction) `
    'The managed or external target preflight function is missing.'
Assert-True ($managedFunction.Extent.Text.Contains(
    "@('compose', '-f', `$composeFile, 'ps', '--all', '--format', 'json')")) `
    'The managed target no longer performs its read-only Compose ps inspection.'
foreach ($managedOnlyText in @('server\compose\acceptance.compose.yml',
    'server\secrets\acceptance\.managed-by-termlink-production-acceptance',
    'server\secrets\acceptance\acceptance-root-ca.crt')) {
    $occurrences = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
            $node.Value -eq $managedOnlyText
    }, $true))
    Assert-True ($occurrences.Count -eq 1 -and
        $occurrences[0].Extent.StartOffset -ge $managedFunction.Extent.StartOffset -and
        $occurrences[0].Extent.EndOffset -le $managedFunction.Extent.EndOffset) `
        "Managed-only path '$managedOnlyText' is evaluated outside the managed preflight."
}
Assert-True ($externalFunction.Extent.Text -notmatch '(?i)docker|acceptanceMarker|acceptanceCa|server\\secrets') `
    'The ExternalHttps preflight depends on Docker, the managed marker, or the local acceptance CA.'

# Import only the pure external contract validator and its assertion helpers.
# The HTTP transport is replaced below so these checks perform no public access.
foreach ($functionName in @('Stop-TailFirstE2E', 'Assert-E2E', 'Assert-JsonProperties',
    'Test-JsonInteger', 'New-ExternalEndpointEvidence', 'Assert-ExternalEndpointStatus',
    'Get-ExternalHttpsPreflight')) {
    $definition = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
    }, $true))
    Assert-True ($definition.Count -eq 1) "Runner function '$functionName' is missing or duplicated."
    Invoke-Expression $definition[0].Extent.Text
}

function Invoke-ExternalJsonEndpoint {
    param(
        [Parameter(Mandatory)][Uri]$Uri,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$CodePrefix,
        [Parameter(Mandatory)]$Evidence
    )
    $entry = New-ExternalEndpointEvidence $Uri $Name $Evidence
    $fixture = $script:externalResponses[$Uri.AbsolutePath]
    if ($null -eq $fixture) {
        $entry.result = 'request_failed'
        Stop-TailFirstE2E "${CodePrefix}_REQUEST_FAILED" 'The fixture endpoint is unavailable.'
    }
    Assert-ExternalEndpointStatus ([int]$fixture.Status) $Name $CodePrefix $entry
    $entry.result = 'http_200_json'
    return [pscustomobject][ordered]@{ document = $fixture.Document; evidence = $entry }
}

function New-ExternalEvidence {
    return [pscustomobject][ordered]@{
        server_origin = $null
        expected_oidc_issuer = $null
        endpoints = @()
    }
}

function Set-ValidExternalResponses {
    param([Parameter(Mandatory)][Uri]$Origin)
    $root = $Origin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
    $issuer = "$root/realms/termlink"
    $script:externalResponses = @{
        '/readyz' = [pscustomobject]@{ Status = 200; Document = [pscustomobject]@{ status = 'ready' } }
        '/v1/client-config' = [pscustomobject]@{
            Status = 200
            Document = [pscustomobject][ordered]@{
                oidc_issuer = $issuer
                oidc_client_id = 'termlink-android'
                redirect_uri = 'dev.termlink:/oauth2redirect'
                windows_oidc_client_id = 'termlink-windows'
                windows_redirect_uri = 'http://127.0.0.1/oauth2/callback'
                web_oidc_client_id = 'termlink-web'
                web_redirect_uri = "$root/auth/callback"
                protocol_versions = @(1)
            }
        }
        '/realms/termlink/.well-known/openid-configuration' = [pscustomobject]@{
            Status = 200
            Document = [pscustomobject]@{ issuer = $issuer }
        }
    }
}

$externalOrigin = [Uri]'https://public.example.test:9443'
Set-ValidExternalResponses $externalOrigin
$script:failureCode = $null
$script:failurePublicMessage = $null
$externalEvidence = New-ExternalEvidence
$null = Get-ExternalHttpsPreflight $externalOrigin $externalEvidence
Assert-True ($externalEvidence.endpoints.Count -eq 3 -and
    @($externalEvidence.endpoints | Where-Object { $_.http_status -eq 200 }).Count -eq 3) `
    'A valid external preflight did not record all three HTTP 200 endpoint summaries.'
Assert-True ([string]$externalEvidence.endpoints[0].result -eq 'ready' -and
    [string]$externalEvidence.endpoints[1].result -eq 'origins_matched' -and
    [string]$externalEvidence.endpoints[2].result -eq 'issuer_matched') `
    'A valid external preflight did not record its semantic status summaries.'

Set-ValidExternalResponses $externalOrigin
$script:externalResponses['/readyz'] = [pscustomobject]@{
    Status = 502
    Document = $null
    Body = 'password=must-not-appear; Cookie: must-not-appear; Authorization: Bearer must-not-appear'
}
$script:failureCode = $null
$script:failurePublicMessage = $null
$externalEvidence = New-ExternalEvidence
$message = Get-FailureMessage { Get-ExternalHttpsPreflight $externalOrigin $externalEvidence }
Assert-True ($message -match 'EXTERNAL_READYZ_HTTP_STATUS') `
    'External readiness did not fail closed on HTTP 502 with its stable code.'
$externalJson = $externalEvidence | ConvertTo-Json -Depth 6
Assert-True ($externalJson -notmatch '(?i)password|cookie|authorization|bearer|must-not-appear') `
    'External HTTP failure evidence contains a response body or sensitive headers.'

Set-ValidExternalResponses $externalOrigin
$script:externalResponses['/v1/client-config'].Document.web_redirect_uri = `
    'https://wrong.example.test:9443/auth/callback'
$script:failureCode = $null
$script:failurePublicMessage = $null
$message = Get-FailureMessage {
    Get-ExternalHttpsPreflight $externalOrigin (New-ExternalEvidence)
}
Assert-True ($message -match 'EXTERNAL_CLIENT_CONFIG_ORIGIN_MISMATCH') `
    'External client configuration did not fail closed on a public-origin mismatch.'

Set-ValidExternalResponses $externalOrigin
$script:externalResponses['/v1/client-config'].Document.oidc_issuer = `
    'https://wrong.example.test:9443/realms/termlink'
$script:failureCode = $null
$script:failurePublicMessage = $null
$message = Get-FailureMessage {
    Get-ExternalHttpsPreflight $externalOrigin (New-ExternalEvidence)
}
Assert-True ($message -match 'EXTERNAL_CLIENT_CONFIG_ISSUER_MISMATCH') `
    'External client configuration did not fail closed on an OIDC issuer mismatch.'

Set-ValidExternalResponses $externalOrigin
$script:externalResponses['/realms/termlink/.well-known/openid-configuration'].Document.issuer = `
    'https://wrong.example.test:9443/realms/termlink'
$script:failureCode = $null
$script:failurePublicMessage = $null
$message = Get-FailureMessage {
    Get-ExternalHttpsPreflight $externalOrigin (New-ExternalEvidence)
}
Assert-True ($message -match 'EXTERNAL_OIDC_ISSUER_MISMATCH') `
    'External OIDC discovery did not fail closed on an issuer mismatch.'

$common = @{
    AvdName = 'TailFirst_Static_AVD'
    AcceptanceOrigin = [Uri]'https://termlink.192.0.2.10.sslip.io:9443'
    AndroidApkPath = (Join-Path $repositoryRoot 'does-not-need-to-exist.apk')
    SessionHostPath = (Join-Path $PSHOME 'pwsh.exe')
    DockerPath = (Join-Path $PSHOME 'pwsh.exe')
    TargetMode = 'ManagedAcceptanceCompose'
    PreflightOnly = $true
}

$message = Get-FailureMessage { & $scriptPath @common -HistoryPageSize 201 }
Assert-True ($message -match 'HistoryPageSize') 'HistoryPageSize accepted a value greater than 200.'
$message = Get-FailureMessage { & $scriptPath @common -HistoryPageSize 199 }
Assert-True ($message -match 'HistoryPageSize') 'HistoryPageSize accepted a value other than the fixed 200-row contract.'
$message = Get-FailureMessage { & $scriptPath @common -HistoryPageCount 24 }
Assert-True ($message -match 'HistoryPageCount') 'HistoryPageCount accepted an incomplete 5200-line chain.'
$message = Get-FailureMessage { & $scriptPath @common -HistoryPageCount 26 }
Assert-True ($message -match 'HistoryPageCount') 'HistoryPageCount accepted an exhausted-cursor offline probe.'
$message = Get-FailureMessage { & $scriptPath @common -EmulatorPort 5555 }
Assert-True ($message -match 'EmulatorPort') 'The runner accepted an odd emulator console port.'
$message = Get-FailureMessage { & $scriptPath @common -AcceptanceOrigin ([Uri]'http://termlink.example.test') }
Assert-True ($message -match 'AcceptanceOrigin') 'The runner accepted a non-HTTPS acceptance origin.'
$message = Get-FailureMessage { & $scriptPath @common -AcceptanceOrigin ([Uri]'https://user@termlink.example.test') }
Assert-True ($message -match 'AcceptanceOrigin') 'The runner accepted an origin containing user information.'
$message = Get-FailureMessage { & $scriptPath @common -ArtifactRunId '..\escape' }
Assert-True ($message -match 'ArtifactRunId') 'ArtifactRunId accepted path traversal.'
$message = Get-FailureMessage { & $scriptPath @common -TargetMode 'UnknownTarget' }
Assert-True ($message -match 'TargetMode') 'The runner accepted an unknown target mode.'
$message = Get-FailureMessage { & $scriptPath @common -HistoryBootstrapMode 'UnknownBootstrap' }
Assert-True ($message -match 'HistoryBootstrapMode') 'The runner accepted an unknown history bootstrap mode.'
$message = Get-FailureMessage { & $scriptPath @common -FrozenTerminalState 'terminal_state_2_22_v3' }
Assert-True ($message -match 'FrozenTerminalState') 'The runner accepted an unknown terminal state contract.'
$message = Get-FailureMessage { & $scriptPath @common -FrozenTerminalState 'terminal_state_2_22_v1' }
Assert-True ($message -match 'E2E_INPUT_INVALID') `
    'The runner accepted an explicit FrozenTerminalState without FrozenGraphicsMode.'
$message = Get-FailureMessage {
    & $scriptPath @common -FrozenGraphicsMode -NativeDisplayPath (Join-Path $PSHOME 'pwsh.exe') `
        -FrozenTerminalState 'terminal_state_2_22_v1'
}
Assert-True ($message -match 'ANDROID_APK_MISSING') `
    'The runner did not accept an explicit v1 terminal state in FrozenGraphicsMode.'

# Exercise the missing-hook failure before Docker/ADB touches a device. Both the
# fake APK and the runner evidence live under repository artifacts.
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $repositoryRoot "artifacts\tail-first-e2e-static-tests\$testId"
$runId = "static-missing-hook-$testId"
$runDirectory = Join-Path $repositoryRoot "artifacts\tail-first-e2e\$runId"
$fakeApk = Join-Path $testRoot 'missing-hook.apk'
$legacyApk = Join-Path $testRoot 'legacy-hook-contract.apk'
$validApk = Join-Path $testRoot 'valid-agent-contract.apk'
$fixtureHostDirectory = Join-Path $repositoryRoot 'win-agent\src\SessionHost\bin\Release\net8.0-windows'
$fixtureSessionHost = Join-Path $fixtureHostDirectory 'TermLink.SessionHost.exe'
$fixtureAgentControl = Join-Path $fixtureHostDirectory 'TermLink.AgentControl.dll'
$agentRunDirectories = [Collections.Generic.List[string]]::new()
$artifactsPrefix = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\') + '\'
foreach ($candidate in @($testRoot, $runDirectory)) {
    Assert-True ([IO.Path]::GetFullPath($candidate).StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) `
        'Static-test cleanup target escaped repository artifacts.'
}

[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($fakeApk, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry('AndroidManifest.xml')
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write('<manifest />') } finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }

    $failureArguments = $common.Clone()
    $failureArguments.AndroidApkPath = $fakeApk
    $failureArguments.ArtifactRunId = $runId
    $message = Get-FailureMessage { & $scriptPath @failureArguments }
    Assert-True ($message -match 'E2E_HOOK_CONTRACT_MISSING') `
        'An APK without the Debug hook did not fail with E2E_HOOK_CONTRACT_MISSING.'
    $evidencePath = Join-Path $runDirectory 'evidence.json'
    Assert-True (Test-Path -LiteralPath $evidencePath -PathType Leaf) `
        'The missing-hook preflight did not write structured evidence.'
    $evidence = Get-Content -Raw -LiteralPath $evidencePath | ConvertFrom-Json
    Assert-True ([string]$evidence.status -eq 'failed') 'Missing-hook evidence was not marked failed.'
    Assert-True ([string]$evidence.inputs.target_mode -eq 'ManagedAcceptanceCompose') `
        'Managed evidence did not record the selected target mode.'
    Assert-True ([string]$evidence.failure.code -eq 'E2E_HOOK_CONTRACT_MISSING') `
        'Missing-hook evidence used the wrong failure code.'
    Assert-True ([string]$evidence.failure.stage -eq 'preflight-hook-contract') `
        'Missing-hook evidence used the wrong failure stage.'
    Assert-True ((Get-Content -Raw -LiteralPath $evidencePath) -notmatch '(?i)bearer\s+|access_token|refresh_token|password\s*[:=]') `
        'Missing-hook evidence contains a credential-shaped value.'

    Assert-True (Test-Path -LiteralPath $fixtureSessionHost -PathType Leaf) `
        'Build TermLink.SessionHost.exe in Release before running the Agent pipe policy checks.'
    Assert-True (Test-Path -LiteralPath $fixtureAgentControl -PathType Leaf) `
        'Build TermLink.AgentControl.dll beside SessionHost before running the Agent pipe policy checks.'

    $contract = [ordered]@{
        schema_version = 1
        transport = 'explicit_broadcast_run_as_file'
        package_name = 'dev.termlink.harness'
        receiver_class = 'dev.termlink.product.debug.TailFirstE2EReceiver'
        broadcast_action = 'dev.termlink.product.debug.TAIL_FIRST_E2E'
        response_relative_path = 'no_backup/tail-first-e2e/response.json'
        server_origin = [string]$common.AcceptanceOrigin.GetLeftPart([UriPartial]::Authority)
        terminal_snapshot_runtime = $true
        operations = @('probe', 'open_session', 'inspect', 'page_up', 'open_history', 'close_history', 'show_ime')
    }
    foreach ($apk in @($legacyApk, $validApk)) {
        if ($apk -eq $validApk) {
            $contract.operations += @('begin_frozen_history', 'page_frozen_history', 'release_frozen_history')
        }
        $archive = [IO.Compression.ZipFile]::Open($apk, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $entry = $archive.CreateEntry('assets/termlink-tail-first-e2e-contract-v1.json')
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write(($contract | ConvertTo-Json -Depth 5)) } finally { $writer.Dispose() }
        }
        finally { $archive.Dispose() }
    }

    $legacyRunId = "static-frozen-legacy-hook-$testId"
    $legacyRunDirectory = Join-Path $repositoryRoot "artifacts\tail-first-e2e\$legacyRunId"
    $agentRunDirectories.Add($legacyRunDirectory)
    $legacyArguments = $common.Clone()
    $legacyArguments.AndroidApkPath = $legacyApk
    $legacyArguments.NativeDisplayPath = Join-Path $PSHOME 'pwsh.exe'
    $legacyArguments.FrozenGraphicsMode = $true
    $legacyArguments.ArtifactRunId = $legacyRunId
    $message = Get-FailureMessage { & $scriptPath @legacyArguments }
    Assert-True ($message -match "lacks operation 'begin_frozen_history'") `
        'Frozen Graphics preflight accepted an APK without frozen history operations.'
    $legacyEvidence = Get-Content -Raw -LiteralPath (Join-Path $legacyRunDirectory 'evidence.json') | ConvertFrom-Json
    Assert-True ([string]$legacyEvidence.failure.stage -eq 'preflight-hook-contract') `
        'Frozen Graphics hook failure was not caught before Agent/AVD preflight.'

    $legacyNormalRunId = "static-normal-legacy-hook-$testId"
    $agentRunDirectories.Add((Join-Path $repositoryRoot "artifacts\tail-first-e2e\$legacyNormalRunId"))
    $legacyNormalPipe = "termlink-tail-first-legacy-$testId"
    $legacyNormalServer = Start-FakeAgentStatusServer $fixtureAgentControl $legacyNormalPipe ([string]$contract.server_origin)
    try {
        $legacyNormalArguments = $common.Clone()
        $legacyNormalArguments.AndroidApkPath = $legacyApk
        $legacyNormalArguments.SessionHostPath = $fixtureSessionHost
        $legacyNormalArguments.AgentControlPipe = $legacyNormalPipe
        $legacyNormalArguments.DockerPath = 'termlink-intentionally-missing-docker'
        $legacyNormalArguments.ArtifactRunId = $legacyNormalRunId
        $message = Get-FailureMessage { & $scriptPath @legacyNormalArguments }
        Assert-True ($message -match 'DOCKER_UNAVAILABLE') `
            'Normal mode rejected an APK with only the original hook operations.'
    }
    finally { Stop-FakeAgentStatusServer $legacyNormalServer }

    $positiveRunId = "static-agent-ready-$testId"
    $positiveRunDirectory = Join-Path $repositoryRoot "artifacts\tail-first-e2e\$positiveRunId"
    $agentRunDirectories.Add($positiveRunDirectory)
    Assert-True ([IO.Path]::GetFullPath($positiveRunDirectory).StartsWith(
        $artifactsPrefix,
        [StringComparison]::OrdinalIgnoreCase)) 'Agent-ready cleanup target escaped repository artifacts.'
    $positivePipe = "termlink-tail-first-positive-$testId"
    $positiveServer = Start-FakeAgentStatusServer $fixtureAgentControl $positivePipe ([string]$contract.server_origin)
    try {
        $positiveArguments = $common.Clone()
        $positiveArguments.AndroidApkPath = $validApk
        $positiveArguments.SessionHostPath = $fixtureSessionHost
        $positiveArguments.AgentControlPipe = $positivePipe
        $positiveArguments.DockerPath = 'termlink-intentionally-missing-docker'
        $positiveArguments.ArtifactRunId = $positiveRunId
        $message = Get-FailureMessage { & $scriptPath @positiveArguments }
        Assert-True ($message -match 'DOCKER_UNAVAILABLE') `
            'A ready Agent did not advance the runner to the Docker preflight boundary.'
        $positiveEvidencePath = Join-Path $positiveRunDirectory 'evidence.json'
        $positiveEvidence = Get-Content -Raw -LiteralPath $positiveEvidencePath | ConvertFrom-Json
        Assert-True ([bool]$positiveEvidence.preflight.windows_agent.connected) `
            'Ready-Agent evidence did not record a successful pipe connection.'
        Assert-True ([string]$positiveEvidence.preflight.windows_agent.relay_state -eq 'Connected') `
            'Ready-Agent evidence did not record the connected relay state.'
        Assert-True ([bool]$positiveEvidence.preflight.windows_agent.terminal_snapshot_enabled) `
            'Ready-Agent evidence did not record the snapshot producer gate.'
        Assert-True ((Get-Content -Raw -LiteralPath $positiveEvidencePath) -notmatch `
            '(?i)account_username|account_email|control_pipe_name|bearer\s+|access_token|refresh_token|password\s*[:=]') `
            'Ready-Agent evidence contains a sensitive or identity-bearing field.'
    }
    finally { Stop-FakeAgentStatusServer $positiveServer }

    $streamedRunId = "static-agent-streamed-$testId"
    $streamedRunDirectory = Join-Path $repositoryRoot "artifacts\tail-first-e2e\$streamedRunId"
    $agentRunDirectories.Add($streamedRunDirectory)
    Assert-True ([IO.Path]::GetFullPath($streamedRunDirectory).StartsWith(
        $artifactsPrefix,
        [StringComparison]::OrdinalIgnoreCase)) 'Streamed-Agent cleanup target escaped repository artifacts.'
    $streamedPipe = "termlink-tail-first-streamed-$testId"
    $streamedServer = Start-FakeAgentStatusServer `
        $fixtureAgentControl `
        $streamedPipe `
        ([string]$contract.server_origin) `
        -TerminalSnapshotEnabled $false
    try {
        $streamedArguments = $common.Clone()
        $streamedArguments.AndroidApkPath = $validApk
        $streamedArguments.SessionHostPath = $fixtureSessionHost
        $streamedArguments.AgentControlPipe = $streamedPipe
        $streamedArguments.DockerPath = 'termlink-intentionally-missing-docker'
        $streamedArguments.ArtifactRunId = $streamedRunId
        $streamedArguments.HistoryBootstrapMode = 'StreamedChunkPrefix'
        $message = Get-FailureMessage { & $scriptPath @streamedArguments }
        Assert-True ($message -match 'DOCKER_UNAVAILABLE') `
            'A Snapshot-disabled Agent did not advance streamed Chunk-prefix mode to the Docker preflight boundary.'
        $streamedEvidencePath = Join-Path $streamedRunDirectory 'evidence.json'
        $streamedEvidence = Get-Content -Raw -LiteralPath $streamedEvidencePath | ConvertFrom-Json
        Assert-True (-not [bool]$streamedEvidence.preflight.windows_agent.terminal_snapshot_enabled) `
            'Streamed-Agent evidence did not record the disabled Snapshot producer.'
        Assert-True ([string]$streamedEvidence.inputs.history_bootstrap_mode -eq 'StreamedChunkPrefix') `
            'Streamed-Agent evidence did not record the selected bootstrap mode.'
    }
    finally { Stop-FakeAgentStatusServer $streamedServer }

    $negativeCases = @(
        [pscustomobject]@{ Name = 'remote-disabled'; Expected = 'WINDOWS_AGENT_REMOTE_ACCESS_DISABLED'; Options = @{ RemoteAccessEnabled = $false } }
        [pscustomobject]@{ Name = 'account-unlinked'; Expected = 'WINDOWS_AGENT_ACCOUNT_NOT_LINKED'; Options = @{ AccountLinked = $false } }
        [pscustomobject]@{ Name = 'relay-connecting'; Expected = 'WINDOWS_AGENT_RELAY_NOT_CONNECTED'; Options = @{ RelayState = 'Connecting' } }
        [pscustomobject]@{ Name = 'origin-mismatch'; Expected = 'WINDOWS_AGENT_ORIGIN_MISMATCH'; Options = @{ ServerOrigin = 'https://wrong.example.test:9443' } }
        [pscustomobject]@{ Name = 'snapshot-disabled'; Expected = 'WINDOWS_AGENT_SNAPSHOT_DISABLED'; Options = @{ TerminalSnapshotEnabled = $false } }
    )
    foreach ($case in $negativeCases) {
        $caseRunId = "static-agent-$($case.Name)-$testId"
        $caseRunDirectory = Join-Path $repositoryRoot "artifacts\tail-first-e2e\$caseRunId"
        $agentRunDirectories.Add($caseRunDirectory)
        Assert-True ([IO.Path]::GetFullPath($caseRunDirectory).StartsWith(
            $artifactsPrefix,
            [StringComparison]::OrdinalIgnoreCase)) 'Negative Agent cleanup target escaped repository artifacts.'
        $casePipe = "termlink-tail-first-$($case.Name)-$testId"
        $serverArguments = @{
            AgentControlAssembly = $fixtureAgentControl
            PipeName = $casePipe
            ServerOrigin = [string]$contract.server_origin
        }
        foreach ($name in $case.Options.Keys) { $serverArguments[$name] = $case.Options[$name] }
        $caseServer = Start-FakeAgentStatusServer @serverArguments
        try {
            $caseArguments = $common.Clone()
            $caseArguments.AndroidApkPath = $validApk
            $caseArguments.SessionHostPath = $fixtureSessionHost
            $caseArguments.AgentControlPipe = $casePipe
            $caseArguments.ArtifactRunId = $caseRunId
            $message = Get-FailureMessage { & $scriptPath @caseArguments }
            Assert-True ($message -match [regex]::Escape([string]$case.Expected)) `
                "Agent case '$($case.Name)' did not fail with $($case.Expected)."
            $caseEvidence = Get-Content -Raw -LiteralPath (Join-Path $caseRunDirectory 'evidence.json') | ConvertFrom-Json
            Assert-True ([string]$caseEvidence.failure.code -eq [string]$case.Expected) `
                "Agent case '$($case.Name)' wrote the wrong evidence failure code."
            Assert-True ([string]$caseEvidence.failure.stage -eq 'preflight-windows-agent') `
                "Agent case '$($case.Name)' wrote the wrong evidence failure stage."
        }
        finally { Stop-FakeAgentStatusServer $caseServer }
    }

    # Isolated policy checks: all ADB/aapt results below are in-memory fixtures.
    foreach ($functionName in @('Get-AvdStartMode', 'Get-CandidateApkIdentity',
        'Assert-InstalledCandidateApk')) {
        $definition = @($ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
        }, $true))
        Assert-True ($definition.Count -eq 1) "Runner function '$functionName' is missing or duplicated."
        Invoke-Expression $definition[0].Extent.Text
    }
    $script:serial = 'emulator-5556'
    $script:AvdName = 'TermLinkReleaseUpgrade_API36'
    $script:AndroidPackageName = 'dev.termlink.harness.recovery'
    $script:FrozenLifecycleAcceptance = $true
    $script:adbPath = 'fixture-adb'
    $script:avdBySerial = @{ 'emulator-5556' = $script:AvdName }
    function Get-EmulatorAvdName { param([string]$DeviceSerial) return $script:avdBySerial[$DeviceSerial] }
    $deviceList = "List of devices attached`nemulator-5556`tdevice`n"
    Assert-True ((Get-AvdStartMode $deviceList) -ceq 'warm_existing') `
        'Frozen lifecycle did not reuse the matching connected AVD.'
    $script:avdBySerial['emulator-5556'] = 'Another_AVD'
    Assert-True ((Get-FailureMessage { Get-AvdStartMode $deviceList }) -match 'AVD_IDENTITY_MISMATCH') `
        'Frozen lifecycle accepted a running AVD with the wrong name.'
    $script:avdBySerial['emulator-5556'] = $script:AvdName
    Assert-True ((Get-FailureMessage {
        Get-AvdStartMode "List of devices attached`nemulator-5556`toffline`n"
    }) -match 'AVD_NOT_READY') 'Frozen lifecycle accepted an offline selected serial.'
    Assert-True ((Get-AvdStartMode 'List of devices attached') -ceq 'owned_preserved_data') `
        'Frozen lifecycle without a running AVD did not select the data-preserving owned path.'
    $script:FrozenLifecycleAcceptance = $false
    Assert-True ((Get-FailureMessage { Get-AvdStartMode $deviceList }) -match 'AVD_NOT_COLD') `
        'Normal cold-first mode started accepting an already running emulator.'
    $script:FrozenLifecycleAcceptance = $true

    $fakeBuildTools = Join-Path $testRoot 'sdk\build-tools\36.1.0'
    [IO.Directory]::CreateDirectory($fakeBuildTools) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $fakeBuildTools 'aapt.exe'), [byte[]]@(0))
    function Invoke-RequiredNative {
        param([string]$FilePath, [string[]]$Arguments, [string]$Code, [string]$Message)
        return "package: name='dev.termlink.harness.recovery' versionCode='132' versionName='0.3.3' platformBuildVersionName='16'"
    }
    $candidate = Get-CandidateApkIdentity $validApk (Join-Path $testRoot 'sdk')
    Assert-True ($candidate.package -ceq $script:AndroidPackageName -and
        $candidate.version_code -ceq '132' -and $candidate.version_name -ceq '0.3.3' -and
        $candidate.sha256 -ceq (Get-FileHash -LiteralPath $validApk -Algorithm SHA256).Hash.ToLowerInvariant()) `
        'Candidate APK identity/hash was not derived from the APK and aapt badging.'
    $script:mockInstalledHash = $candidate.sha256
    $script:mockInstalledVersion = '0.3.3'
    $script:installedQueries = [Collections.Generic.List[string]]::new()
    function Invoke-NativeCapture {
        param([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds)
        $command = $Arguments -join ' '
        $script:installedQueries.Add($command)
        $output = if ($command -match ' shell pm path ') {
            'package:/data/app/example/base.apk'
        } elseif ($command -match ' shell sha256sum ') {
            "$($script:mockInstalledHash)  /data/app/example/base.apk"
        } elseif ($command -match ' shell dumpsys package ') {
            "Packages:`n  Package [dev.termlink.harness.recovery]`n    versionCode=132 minSdk=26`n    versionName=$($script:mockInstalledVersion)`n"
        } else { throw "Unexpected fixture ADB query: $command" }
        return [pscustomobject]@{ ExitCode = 0; StdOut = $output; StdErr = '' }
    }
    $null = Assert-InstalledCandidateApk $candidate
    Assert-True ($script:installedQueries.Count -eq 3 -and
        -not (($script:installedQueries -join ' ') -match ' install |force-stop| run-as | rm ')) `
        'Warm existing APK validation performed an unexpected state-changing ADB command.'
    $script:mockInstalledHash = '0' * 64
    Assert-True ((Get-FailureMessage { Assert-InstalledCandidateApk $candidate }) -match
        'ANDROID_INSTALLED_APK_HASH_MISMATCH') 'Warm lifecycle accepted the wrong installed APK hash.'
    $script:mockInstalledHash = $candidate.sha256
    $script:mockInstalledVersion = '0.3.2'
    Assert-True ((Get-FailureMessage { Assert-InstalledCandidateApk $candidate }) -match
        'ANDROID_INSTALLED_APK_VERSION_MISMATCH') 'Warm lifecycle accepted the wrong installed version.'
    Assert-True ($source.Contains("if (`$avdStartMode -cne 'warm_existing')") -and
        $source.Contains('android_state_reset -NotePropertyValue $false') -and
        $source.Contains("if (`$FrozenLifecycleAcceptance) {") -and
        $source.Contains("'shell', 'am', 'force-stop', `$AndroidPackageName")) `
        'Frozen preservation or normal cold-first behavior is missing from the runner.'

    $identityFunction = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Test-FailureSessionIdentity'
    }, $true))
    Assert-True ($identityFunction.Count -eq 1) 'The failed-open session identity guard is missing.'
    Invoke-Expression $identityFunction[0].Extent.Text
    $baselineId = '11111111-1111-4111-8111-111111111111'
    $newId = '22222222-2222-4222-8222-222222222222'
    $otherId = '33333333-3333-4333-8333-333333333333'
    $arm = [pscustomobject]@{ baseline_session_ids = @($baselineId); selected_session_id = $null }
    Assert-True ((Test-FailureSessionIdentity $newId $arm) -and
        -not (Test-FailureSessionIdentity $baselineId $arm) -and
        -not (Test-FailureSessionIdentity 'not-a-uuid' $arm)) `
        'Failed-open evidence can select an old or invalid session.'
    $arm.selected_session_id = $newId
    Assert-True ((Test-FailureSessionIdentity $newId $arm) -and
        -not (Test-FailureSessionIdentity $otherId $arm)) `
        'Failed-open evidence can switch away from the selected session.'
    Assert-True ($source.Contains("`$script:stage -ceq 'tail-first-open'") -and
        $source.Contains('-not $script:sessionProcess.HasExited') -and
        $source.Contains('Save-FrozenOpenFailureEvidence $script:openSessionRequest') -and
        $source.Contains("Save-Screenshot 'failed-open.png'")) `
        'Failed-open evidence is not gated by the live owned Host and armed session.'
    $failureCapture = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Save-FrozenOpenFailureEvidence'
    }, $true))
    Assert-True ($failureCapture.Count -eq 1 -and
        $failureCapture[0].Extent.Text.Contains('AddSeconds(60)') -and
        @([regex]::Matches($failureCapture[0].Extent.Text,
            '\$script:sessionProcess\.HasExited')).Count -ge 2) `
        'Failed-open inspect lacks the 60-second bound or live Host checks before screenshot.'

    $frozenTemplates = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
            $node.Value.Contains("`$stem = '__MARKER_STEM__'") -and
            $node.Value.Contains('__FROZEN_COLUMNS_SOURCE__')
    }, $true))
    Assert-True ($frozenTemplates.Count -eq 1) 'The Frozen child fixture template is missing or ambiguous.'
    $statusPath = Join-Path $testRoot 'frozen-child-columns.txt'
    $child = $frozenTemplates[0].Value.
        Replace('__MARKER_STEM__', 'TLTF32_TEST').
        Replace('__FROZEN_LIFECYCLE__', '$true').
        Replace('__FROZEN_COLUMNS_SOURCE__', '32').
        Replace('__FIXTURE_STATUS_BASE64__', [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($statusPath))).
        Replace('__FROZEN_WIDTH_MARKER__', 'TLF_COL32_TEST').
        Replace('__MARKER_COUNT__', '1400')
    $childStart = [Diagnostics.ProcessStartInfo]::new()
    $childStart.FileName = Join-Path $PSHOME 'pwsh.exe'
    $childStart.UseShellExecute = $false
    $childStart.CreateNoWindow = $true
    $childStart.RedirectStandardInput = $true
    $childStart.RedirectStandardOutput = $true
    $childStart.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand',
        [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child)))) {
        $childStart.ArgumentList.Add($argument)
    }
    $childProcess = [Diagnostics.Process]::Start($childStart)
    Assert-True ($null -ne $childProcess) 'Could not start the isolated Frozen child fixture.'
    try {
        $childOutput = $childProcess.StandardOutput.ReadToEndAsync()
        $childError = $childProcess.StandardError.ReadToEndAsync()
        $childProcess.StandardInput.WriteLine('__TERMLINK_TAIL_FIRST_EXIT__')
        $childProcess.StandardInput.Close()
        Assert-True ($childProcess.WaitForExit(30000) -and $childProcess.ExitCode -eq 0) `
            'The isolated Frozen child fixture did not complete.'
        $outputLines = @($childOutput.GetAwaiter().GetResult() -split '\r?\n')
        $markerLines = @($outputLines | Where-Object { $_ -match 'TLTF32_TEST_\d{4}' })
        Assert-True ($markerLines.Count -eq 1400) `
            'The actual Frozen child emitted a different marker count than 1400.'
        Assert-True (@($markerLines[0..1398] | Where-Object { $_ -notmatch 'X{640}$' }).Count -eq 0) `
            'An earlier Frozen marker lost its 20-row history padding.'
        Assert-True ($markerLines[1399] -ceq 'TLTF32_TEST_1400' -and
            $outputLines[1400] -ceq 'TLF_COL32_TEST') `
            'The final Frozen marker or following width marker is not a visible unpadded line.'
        Assert-True ((Get-Content -LiteralPath $statusPath -Raw).Trim() -ceq '32') `
            'The isolated Frozen child did not record the mocked 32-column terminal width.'
        Assert-True ([string]::IsNullOrWhiteSpace($childError.GetAwaiter().GetResult())) `
            'The isolated Frozen child wrote an error.'
    }
    finally {
        if (-not $childProcess.HasExited) { $childProcess.Kill($true) }
        $childProcess.Dispose()
    }
}
finally {
    foreach ($candidate in @($testRoot, $runDirectory) + @($agentRunDirectories)) {
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            Remove-Item -LiteralPath $candidate -Recurse -Force
        }
    }
}

Write-Output 'Tail-first product E2E parser, policy, parameter, and fail-closed checks passed.'
