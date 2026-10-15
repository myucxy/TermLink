[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$AvdName,

    [Parameter(Mandatory)]
    [ValidateScript({
        $_.IsAbsoluteUri -and $_.Scheme -eq 'https' -and $_.AbsolutePath -eq '/' -and
        [string]::IsNullOrEmpty($_.UserInfo) -and -not $_.Query -and -not $_.Fragment
    })]
    [Uri]$AcceptanceOrigin,

    [string]$AndroidApkPath = 'android\app\build\outputs\apk\debug\app-debug.apk',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')]
    [string]$AndroidPackageName = 'dev.termlink.harness',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.$]{2,255}$')]
    [string]$AndroidActivity = 'dev.termlink.product.ProductActivity',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.$]{2,255}$')]
    [string]$HookReceiverClass = 'dev.termlink.product.debug.TailFirstE2EReceiver',

    [string]$SessionHostPath = 'win-agent\src\SessionHost\bin\Release\net8.0-windows\TermLink.SessionHost.exe',
    [string]$NativeDisplayPath,
    [switch]$FrozenGraphicsMode,
    [ValidateSet('terminal_state_2_22_v1', 'terminal_state_2_22_v2')]
    [string]$FrozenTerminalState = 'terminal_state_2_22_v2',
    [switch]$ExpectFrozenUnsupported,
    [switch]$FrozenLifecycleAcceptance,
    [ValidatePattern('^[^\\/]{1,200}$')]
    [string]$AgentControlPipe,

    # The 5200-line fixture has 26 transcript pages: the initial partial tail
    # page plus 25 authenticated continuations. The final continuation is first
    # attempted offline, then retried online so failure retention and the full
    # unbounded chain are both proven in one run.
    [ValidateSet(25)]
    [int]$HistoryPageCount = 25,
    [ValidateSet(200)]
    [int]$HistoryPageSize = 200,
    [ValidateRange(5554, 5682)]
    [ValidateScript({ ($_ % 2) -eq 0 })]
    [int]$EmulatorPort = 5554,
    [ValidateRange(30, 600)]
    [int]$BootTimeoutSeconds = 180,
    [ValidateRange(30, 600)]
    [int]$ObservationTimeoutSeconds = 300,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$ArtifactRunId = ('run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)),
    [ValidateSet('ManagedAcceptanceCompose', 'ExternalHttps')]
    [string]$TargetMode = 'ManagedAcceptanceCompose',
    [ValidateSet('SnapshotTranscript', 'StreamedChunkPrefix')]
    [string]$HistoryBootstrapMode = 'SnapshotTranscript',
    [string]$DockerPath = 'docker',
    [switch]$PreflightOnly,
    [switch]$KeepEmulator,
    [switch]$ShowEmulator
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:failureCode = $null
$script:failurePublicMessage = $null
$script:hookFailureCode = $null
$script:stage = 'initialize'
$script:emulatorProcess = $null
$script:emulatorOutputTask = $null
$script:emulatorErrorTask = $null
$script:emulatorOwned = $false
$script:sessionProcess = $null
$script:sessionOutputTask = $null
$script:sessionErrorTask = $null
$script:airplaneModeEnabled = $false
$script:adbPath = $null
$script:frozenCaptureActive = $false
$script:openSessionRequest = $null
$script:liveMarker = $null

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\tail-first-e2e'))
$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $artifactRoot $ArtifactRunId))
$evidencePath = Join-Path $artifactDirectory 'evidence.json'
$frozenFixtureStatusPath = Join-Path $artifactDirectory 'frozen-fixture-columns.txt'
$hookContractEntry = 'assets/termlink-tail-first-e2e-contract-v1.json'
$hookAction = 'dev.termlink.product.debug.TAIL_FIRST_E2E'
$hookResponsePath = 'no_backup/tail-first-e2e/response.json'
$hookSessionArmPath = 'no_backup/tail-first-e2e/session-arm.json'
$requiredHookOperations = @(
    'probe', 'open_session', 'inspect', 'page_up', 'open_history', 'close_history', 'show_ime'
)
if ($FrozenGraphicsMode) {
    $requiredHookOperations += @('begin_frozen_history', 'page_frozen_history', 'release_frozen_history')
}
$snapshotMarkerCount = 5200
$snapshotTranscriptPageCount = 26
$snapshotTailFirstBeforeLineId = [uint64]5177
$streamedPrefixMarkerCount = 5200
$streamedPrefixMinimumTail = [uint64]4097
$markerCount = if ($FrozenLifecycleAcceptance) {
    1400
} elseif ($HistoryBootstrapMode -ceq 'StreamedChunkPrefix') {
    $streamedPrefixMarkerCount
} else {
    $snapshotMarkerCount
}
$minimumOpenTail = if ($HistoryBootstrapMode -ceq 'StreamedChunkPrefix') {
    $streamedPrefixMinimumTail
} else {
    [uint64]0
}
# Opening history preserves the 24 marker rows already visible in the live
# viewport (5177..5200) and requests only the older prefix. Generate the entire
# immutable 26-page chain. Transcript pages are sealed forward from line 1,
# making the newest page the expected 176-row partial page (5001..5176).
$expectedTranscriptPages = [Collections.Generic.List[object]]::new()
$requestBeforeLineId = $snapshotTailFirstBeforeLineId
while ($requestBeforeLineId -gt 1) {
    $lastLineId = $requestBeforeLineId - 1
    $firstLineId = [uint64]($lastLineId - (($lastLineId - 1) % [uint64]$HistoryPageSize))
    $expectedTranscriptPages.Add([pscustomobject]@{
        request_before = $requestBeforeLineId
        first = $firstLineId
        last = $lastLineId
        has_more = $firstLineId -gt 1
    })
    $requestBeforeLineId = $firstLineId
}
if ($expectedTranscriptPages.Count -ne $snapshotTranscriptPageCount -or
    $HistoryPageCount -ne $snapshotTranscriptPageCount - 1) {
    throw 'Snapshot/Transcript fixture page geometry is inconsistent.'
}
$tlH115BoundaryLineIds = [uint64[]]@(4095, 4096, 4097, 5200)
$serial = "emulator-$EmulatorPort"

function Stop-TailFirstE2E {
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Z0-9_]+$')][string]$Code,
        [Parameter(Mandatory)][string]$PublicMessage
    )
    $script:failureCode = $Code
    $script:failurePublicMessage = $PublicMessage
    throw [InvalidOperationException]::new("${Code}: $PublicMessage")
}

function Assert-E2E {
    param(
        [bool]$Condition,
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) { Stop-TailFirstE2E -Code $Code -PublicMessage $Message }
}

function Test-RelaySubscriptionRecovered {
    param([Parameter(Mandatory)]$Before, [Parameter(Mandatory)]$Current)
    try {
        # Android's public Internet validation is independent of the authenticated TermLink Relay.
        return [guid]$Before.session_id -ne [guid]::Empty -and
            $Current.session_id -ceq $Before.session_id -and
            $Before.realtime_connected_generation -gt 0 -and
            $Current.realtime_connected_generation -gt $Before.realtime_connected_generation -and
            $Current.realtime_socket_state -ceq 'CONNECTED' -and
            $Current.realtime_transport_state -ceq 'CONNECTED' -and
            $Current.realtime_subscriber_ready -is [bool] -and $Current.realtime_subscriber_ready
    }
    catch { return $false }
}

function Test-FrozenPageTransition {
    param(
        [Parameter(Mandatory)]$Reference,
        [Parameter(Mandatory)]$Previous,
        [Parameter(Mandatory)]$Current,
        [Parameter(Mandatory)][Collections.Generic.HashSet[uint64]]$SeenAnchors
    )
    $fields = @('frozen_active', 'frozen_render_ready', 'frozen_capture_id',
        'frozen_revision', 'frozen_snapshot_sequence_s', 'frozen_page_count',
        'frozen_first_line_id', 'frozen_next_before_line_id',
        'frozen_has_more_before', 'frozen_head_truncated')
    if (@($fields | Where-Object { $_ -notin $Current.PSObject.Properties.Name }).Count -gt 0) {
        return $false
    }
    if ($Current.frozen_active -isnot [bool] -or $Current.frozen_render_ready -isnot [bool] -or
        $Current.frozen_has_more_before -isnot [bool] -or $Current.frozen_head_truncated -isnot [bool] -or
        -not $Current.frozen_active -or -not $Current.frozen_render_ready) {
        return $false
    }
    try {
        $anchor = [uint64]$Current.frozen_next_before_line_id
        $previousAnchor = [uint64]$Previous.frozen_next_before_line_id
        $firstLine = [uint64]$Current.frozen_first_line_id
        $previousFirstLine = [uint64]$Previous.frozen_first_line_id
        $capture = [uint64]$Current.frozen_capture_id
        $revision = [uint64]$Current.frozen_revision
        $snapshot = [uint64]$Current.frozen_snapshot_sequence_s
        $pageCount = [int]$Current.frozen_page_count
        if ($previousAnchor -eq 0 -or -not $SeenAnchors.Contains($previousAnchor) -or
            $firstLine -eq 0 -or $firstLine -eq $previousFirstLine -or
            $capture -eq 0 -or $capture -ne [uint64]$Reference.frozen_capture_id -or
            $revision -ne [uint64]$Reference.frozen_revision -or
            $snapshot -ne [uint64]$Reference.frozen_snapshot_sequence_s -or $pageCount -ne 2) {
            return $false
        }
        if ($Current.frozen_has_more_before) {
            if ($anchor -eq 0 -or $anchor -eq $previousAnchor -or $SeenAnchors.Contains($anchor)) {
                return $false
            }
            return $SeenAnchors.Add($anchor)
        }
        return $anchor -eq 0
    }
    catch { return $false }
}

function Wait-FrozenCaptureAge {
    param(
        [Parameter(Mandatory)][Diagnostics.Stopwatch]$Clock,
        [Parameter(Mandatory)][int]$TargetSeconds,
        [Parameter(Mandatory)][string]$Phase
    )
    $nextReport = $Clock.Elapsed.TotalSeconds
    while ($Clock.Elapsed.TotalSeconds -lt $TargetSeconds) {
        $remaining = $TargetSeconds - $Clock.Elapsed.TotalSeconds
        if ($Clock.Elapsed.TotalSeconds -ge $nextReport) {
            Write-Host ("{0}: {1:N0}s remaining" -f $Phase, [Math]::Ceiling($remaining))
            $nextReport = $Clock.Elapsed.TotalSeconds + 30
        }
        Start-Sleep -Milliseconds ([int][Math]::Min(1000, [Math]::Max(1, $remaining * 1000)))
    }
}

function Test-StrictChildPath {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Candidate)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return [IO.Path]::GetFullPath($Candidate).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-RepositoryFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Code, [Parameter(Mandatory)][string]$Label)
    $candidate = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repositoryRoot $Path }
    $resolved = [IO.Path]::GetFullPath($candidate)
    Assert-E2E (Test-Path -LiteralPath $resolved -PathType Leaf) $Code "$Label is unavailable."
    return $resolved
}

function Resolve-Application {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Code, [Parameter(Mandatory)][string]$Label)
    if ([IO.Path]::IsPathRooted($Path) -or $Path.Contains('\') -or $Path.Contains('/')) {
        $resolved = [IO.Path]::GetFullPath($Path)
        Assert-E2E (Test-Path -LiteralPath $resolved -PathType Leaf) $Code "$Label is unavailable."
        return $resolved
    }
    $command = Get-Command $Path -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    Assert-E2E ($null -ne $command) $Code "$Label is unavailable."
    return [string]$command.Source
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 60
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
    if ($null -eq $process) { return [pscustomobject]@{ ExitCode = -1; StdOut = ''; StdErr = '' } }
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
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
    finally {
        $process.Dispose()
    }
}

function Invoke-RequiredNative {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string]$Message,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 60
    )
    $result = Invoke-NativeCapture -FilePath $FilePath -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds
    Assert-E2E ($result.ExitCode -eq 0) $Code $Message
    return [string]$result.StdOut
}

function Get-ManifestTool {
    param([Parameter(Mandatory)][string]$Id)
    $ensureDevEnv = 'D:\Dev\Scripts\Ensure-DevEnv.ps1'
    Assert-E2E (Test-Path -LiteralPath $ensureDevEnv -PathType Leaf) 'DEV_ENV_MANIFEST_UNAVAILABLE' `
        'D:\Dev tool inspection is unavailable.'
    $raw = @(& $ensureDevEnv -Tool $Id -Json 2>$null) -join "`n"
    Assert-E2E ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($raw)) `
        'DEV_ENV_TOOL_UNAVAILABLE' "Required D:\Dev tool '$Id' is unavailable."
    try { $tool = $raw | ConvertFrom-Json -ErrorAction Stop }
    catch { Stop-TailFirstE2E 'DEV_ENV_TOOL_INVALID' "D:\Dev returned invalid metadata for '$Id'." }
    Assert-E2E ([string]$tool.Status -eq 'installed' -and (Test-Path -LiteralPath ([string]$tool.Path) -PathType Leaf)) `
        'DEV_ENV_TOOL_UNAVAILABLE' "Required D:\Dev tool '$Id' is unavailable."
    return [pscustomobject][ordered]@{
        id = $Id
        path = [IO.Path]::GetFullPath([string]$tool.Path)
        version = [string]$tool.Version
    }
}

function Get-WindowsAgentPreflight {
    param(
        [Parameter(Mandatory)][string]$AgentControlAssembly,
        [string]$RequestedPipeName,
        [Parameter(Mandatory)][string]$ExpectedOrigin,
        [Parameter(Mandatory)][bool]$ExpectedTerminalSnapshotEnabled
    )
    try {
        $assembly = [Reflection.Assembly]::LoadFrom($AgentControlAssembly)
        $controlPipeType = $assembly.GetType('TermLink.Agent.TermLinkControlPipe', $true)
        $requestType = $assembly.GetType('TermLink.Agent.RegistryRequest', $true)
        $commandType = $assembly.GetType('TermLink.Agent.RegistryCommand', $true)
        $clientType = $assembly.GetType('TermLink.Agent.RegistryControlClient', $true)
    }
    catch {
        Stop-TailFirstE2E 'AGENT_CONTROL_ASSEMBLY_INVALID' `
            'The AgentControl assembly beside SessionHost could not be loaded.'
    }

    $usesDefaultPipe = [string]::IsNullOrWhiteSpace($RequestedPipeName)
    try {
        $resolvedPipeName = if ($usesDefaultPipe) {
            [string]$controlPipeType.GetMethod('DefaultName').Invoke($null, @())
        } else {
            [string]$RequestedPipeName
        }
        Assert-E2E (-not [string]::IsNullOrWhiteSpace($resolvedPipeName) -and $resolvedPipeName.Length -le 200 -and
            $resolvedPipeName.IndexOfAny([char[]]@('\', '/')) -lt 0) 'WINDOWS_AGENT_PIPE_INVALID' `
            'The selected Windows Agent control pipe name is invalid.'

        $request = [Activator]::CreateInstance($requestType)
        $request.Command = [Enum]::Parse($commandType, 'RefreshProductionStatus')
        $sendMethod = @($clientType.GetMethods() | Where-Object {
            $_.Name -eq 'Send' -and $_.GetParameters().Count -eq 3
        })
        Assert-E2E ($sendMethod.Count -eq 1) 'AGENT_CONTROL_ASSEMBLY_INVALID' `
            'The AgentControl assembly lacks the expected status client.'
        $response = $sendMethod[0].Invoke($null, [object[]]@($resolvedPipeName, $request, 10000))
    }
    catch {
        if ($script:failureCode) { throw }
        Stop-TailFirstE2E 'WINDOWS_AGENT_PIPE_UNAVAILABLE' `
            'The Windows Agent control pipe is unavailable or did not answer RefreshProductionStatus.'
    }

    Assert-JsonProperties $response @('Success', 'RemoteAccessEnabled', 'Production') `
        'Windows Agent RefreshProductionStatus response' 'WINDOWS_AGENT_STATUS_INVALID'
    Assert-E2E ($response.Success -is [bool] -and $response.RemoteAccessEnabled -is [bool]) `
        'WINDOWS_AGENT_STATUS_INVALID' 'The Windows Agent returned invalid readiness booleans.'
    Assert-E2E ([bool]$response.Success) 'WINDOWS_AGENT_STATUS_REFRESH_FAILED' `
        'The Windows Agent could not refresh its production status.'
    Assert-E2E ([bool]$response.RemoteAccessEnabled) 'WINDOWS_AGENT_REMOTE_ACCESS_DISABLED' `
        'The Windows Agent does not have remote access enabled.'

    $production = $response.Production
    Assert-E2E ($null -ne $production) 'WINDOWS_AGENT_STATUS_INVALID' `
        'The Windows Agent omitted its production status.'
    Assert-JsonProperties $production @(
        'Configured', 'AccountLinked', 'ServerOrigin', 'TerminalSnapshotEnabled', 'RelayState'
    ) 'Windows Agent production status' 'WINDOWS_AGENT_STATUS_INVALID'
    Assert-E2E ($production.Configured -is [bool] -and $production.AccountLinked -is [bool] -and
        $production.TerminalSnapshotEnabled -is [bool]) 'WINDOWS_AGENT_STATUS_INVALID' `
        'The Windows Agent returned invalid production readiness booleans.'
    Assert-E2E ([bool]$production.Configured -and [bool]$production.AccountLinked) `
        'WINDOWS_AGENT_ACCOUNT_NOT_LINKED' 'The Windows Agent is not linked to an acceptance account.'
    $relayState = [string]$production.RelayState
    Assert-E2E ($relayState -ceq 'Connected') 'WINDOWS_AGENT_RELAY_NOT_CONNECTED' `
        'The Windows Agent production relay is not connected.'
    Assert-E2E ([string]$production.ServerOrigin -ceq $ExpectedOrigin) 'WINDOWS_AGENT_ORIGIN_MISMATCH' `
        'The Windows Agent is linked to a different server origin.'
    $snapshotMatchesMode = [bool]$production.TerminalSnapshotEnabled -eq $ExpectedTerminalSnapshotEnabled
    $snapshotFailureCode = if ($ExpectedTerminalSnapshotEnabled) {
        'WINDOWS_AGENT_SNAPSHOT_DISABLED'
    } else {
        'WINDOWS_AGENT_SNAPSHOT_ENABLED'
    }
    $snapshotFailureMessage = if ($ExpectedTerminalSnapshotEnabled) {
        'The Windows Agent terminal snapshot producer is disabled.'
    } else {
        'The Windows Agent terminal snapshot producer must be disabled for streamed Chunk-prefix acceptance.'
    }
    Assert-E2E $snapshotMatchesMode $snapshotFailureCode $snapshotFailureMessage

    return [pscustomobject][ordered]@{
        assembly = [IO.Path]::GetFileName($AgentControlAssembly)
        pipe_selection = if ($usesDefaultPipe) { 'sid-default' } else { 'explicit' }
        connected = $true
        status_refreshed = $true
        remote_access_enabled = $true
        configured = $true
        account_linked = $true
        relay_state = $relayState
        server_origin = [string]$production.ServerOrigin
        terminal_snapshot_enabled = [bool]$production.TerminalSnapshotEnabled
    }
}

function Get-ApkHookContract {
    param([Parameter(Mandatory)][string]$ApkPath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try { $archive = [IO.Compression.ZipFile]::OpenRead($ApkPath) }
    catch { Stop-TailFirstE2E 'ANDROID_APK_INVALID' 'The Android APK is not a readable ZIP archive.' }
    try {
        $entry = $archive.GetEntry($hookContractEntry)
        if ($null -eq $entry) {
            Stop-TailFirstE2E 'E2E_HOOK_CONTRACT_MISSING' `
                "The Debug APK lacks $hookContractEntry. Required contract: explicit broadcast '$hookAction', receiver '$HookReceiverClass', response '$hookResponsePath', operations [$($requiredHookOperations -join ', ')]."
        }
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.UTF8Encoding]::new($false), $true)
        try { $raw = $reader.ReadToEnd() } finally { $reader.Dispose() }
        try { $contract = $raw | ConvertFrom-Json -ErrorAction Stop }
        catch { Stop-TailFirstE2E 'E2E_HOOK_CONTRACT_INVALID' 'The APK tail-first E2E contract is invalid JSON.' }
    }
    finally { $archive.Dispose() }

    Assert-JsonProperties $contract @(
        'schema_version', 'transport', 'package_name', 'receiver_class',
        'broadcast_action', 'response_relative_path', 'server_origin',
        'terminal_snapshot_runtime', 'operations'
    ) 'APK tail-first E2E contract' 'E2E_HOOK_CONTRACT_INVALID'
    $valid = [int]$contract.schema_version -eq 1 -and
        [string]$contract.transport -eq 'explicit_broadcast_run_as_file' -and
        [string]$contract.package_name -eq $AndroidPackageName -and
        [string]$contract.receiver_class -eq $HookReceiverClass -and
        [string]$contract.broadcast_action -eq $hookAction -and
        [string]$contract.response_relative_path -eq $hookResponsePath
    Assert-E2E $valid 'E2E_HOOK_CONTRACT_INVALID' 'The APK tail-first E2E contract does not match the runner contract.'
    $expectedOrigin = $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
    Assert-E2E ([string]$contract.server_origin -ceq $expectedOrigin) 'ANDROID_APK_ORIGIN_MISMATCH' `
        'The Android Debug APK was not built for the requested acceptance origin.'
    Assert-E2E ($contract.terminal_snapshot_runtime -is [bool]) 'E2E_HOOK_CONTRACT_INVALID' `
        'The APK tail-first E2E contract terminal_snapshot_runtime field is not a JSON boolean.'
    Assert-E2E ([bool]$contract.terminal_snapshot_runtime) 'ANDROID_APK_SNAPSHOT_RUNTIME_DISABLED' `
        'The Android Debug APK was built without terminal snapshot runtime enabled.'
    $operations = @($contract.operations | ForEach-Object { [string]$_ })
    foreach ($operation in $requiredHookOperations) {
        Assert-E2E ($operations -ccontains $operation) 'E2E_HOOK_CONTRACT_INVALID' `
            "The APK tail-first E2E contract lacks operation '$operation'."
    }
    return [pscustomobject][ordered]@{
        schema_version = 1
        transport = 'explicit_broadcast_run_as_file'
        entry = $hookContractEntry
        server_origin = [string]$contract.server_origin
        terminal_snapshot_runtime = [bool]$contract.terminal_snapshot_runtime
        operations = $requiredHookOperations
    }
}

function Assert-JsonProperties {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string[]]$Names,
        [Parameter(Mandatory)][string]$Context,
        [ValidatePattern('^[A-Z0-9_]+$')][string]$Code = 'E2E_HOOK_RESPONSE_INVALID'
    )
    $available = @($Value.PSObject.Properties.Name)
    foreach ($name in $Names) {
        Assert-E2E ($available -ccontains $name) $Code `
            "$Context lacks required field '$name'."
    }
}

function Test-JsonInteger {
    param($Value, [decimal]$Maximum)
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool]) { return $false }
    try { $number = [decimal]$Value } catch { return $false }
    return $number -ge 0 -and $number -le $Maximum -and [decimal]::Truncate($number) -eq $number
}

function Get-AcceptanceStackState {
    param([Parameter(Mandatory)][string]$Docker)
    $composeFile = Join-Path $repositoryRoot 'server\compose\acceptance.compose.yml'
    $acceptanceMarker = Join-Path $repositoryRoot 'server\secrets\acceptance\.managed-by-termlink-production-acceptance'
    $acceptanceCa = Join-Path $repositoryRoot 'server\secrets\acceptance\acceptance-root-ca.crt'
    Assert-E2E (Test-Path -LiteralPath $composeFile -PathType Leaf) 'ACCEPTANCE_COMPOSE_MISSING' `
        'The production acceptance Compose file is unavailable.'
    Assert-E2E (Test-Path -LiteralPath $acceptanceMarker -PathType Leaf) 'ACCEPTANCE_STACK_UNMANAGED' `
        'The existing production acceptance stack has no managed marker.'
    Assert-E2E (Test-Path -LiteralPath $acceptanceCa -PathType Leaf) 'ACCEPTANCE_CA_MISSING' `
        'The existing production acceptance stack has no exported public CA.'
    $secretRoot = Split-Path -Parent $acceptanceMarker
    $composePaths = [ordered]@{
        TERMLINK_ACCEPTANCE_KEYCLOAK_ADMIN_PASSWORD_FILE = (Join-Path $secretRoot 'keycloak-admin-password')
        TERMLINK_ACCEPTANCE_ADMIN_INITIAL_PASSWORD_FILE = (Join-Path $secretRoot 'admin-initial-password')
        TERMLINK_ACCEPTANCE_USER_PASSWORD_FILE = (Join-Path $secretRoot 'acceptance-user-password')
        TERMLINK_ACCEPTANCE_POSTGRES_PASSWORD_FILE = (Join-Path $secretRoot 'postgres-password')
        TERMLINK_ACCEPTANCE_VALKEY_PASSWORD_FILE = (Join-Path $secretRoot 'valkey-password')
        TERMLINK_ACCEPTANCE_OIDC_CLIENT_SECRET_FILE = (Join-Path $secretRoot 'oidc-client-secret')
        TERMLINK_ACCEPTANCE_DATABASE_URL_FILE = (Join-Path $secretRoot 'database-url')
        TERMLINK_ACCEPTANCE_REDIS_URL_FILE = (Join-Path $secretRoot 'redis-url')
        TERMLINK_ACCEPTANCE_TOKEN_SIGNING_KEY_FILE = (Join-Path $secretRoot 'token-signing-key')
    }
    $savedEnvironment = @{}
    try {
        foreach ($name in $composePaths.Keys) {
            $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $composePaths[$name], 'Process')
        }
        $raw = Invoke-RequiredNative $Docker @('compose', '-f', $composeFile, 'ps', '--all', '--format', 'json') `
            'ACCEPTANCE_STACK_INSPECTION_FAILED' 'Could not inspect the existing production acceptance stack.'
    }
    finally {
        foreach ($name in $composePaths.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
    $byService = @{}
    foreach ($line in @($raw -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $items = @($line | ConvertFrom-Json -ErrorAction Stop) }
        catch { Stop-TailFirstE2E 'ACCEPTANCE_STACK_STATUS_INVALID' 'Docker returned invalid acceptance service status.' }
        foreach ($item in $items) { $byService[[string]$item.Service] = $item }
    }
    $healthy = @('lucky-upstream', 'postgres', 'valkey', 'server')
    $running = @('keycloak')
    $completed = @('runtime-secrets-init', 'oidc-ca-ready', 'keycloak-init', 'server-secrets-init', 'migrate')
    foreach ($name in $healthy) {
        $item = $byService[$name]
        Assert-E2E ($null -ne $item -and [string]$item.State -eq 'running' -and [string]$item.Health -eq 'healthy') `
            'ACCEPTANCE_STACK_NOT_HEALTHY' "Acceptance service '$name' is not healthy."
    }
    foreach ($name in $running) {
        $item = $byService[$name]
        Assert-E2E ($null -ne $item -and [string]$item.State -eq 'running') `
            'ACCEPTANCE_STACK_NOT_HEALTHY' "Acceptance service '$name' is not running."
    }
    foreach ($name in $completed) {
        $item = $byService[$name]
        Assert-E2E ($null -ne $item -and [string]$item.State -eq 'exited' -and [int]$item.ExitCode -eq 0) `
            'ACCEPTANCE_STACK_NOT_HEALTHY' "Acceptance initializer '$name' did not complete successfully."
    }
    return @($healthy + $running + $completed | ForEach-Object {
        $item = $byService[$_]
        [pscustomobject][ordered]@{
            service = $_
            state = [string]$item.State
            health = if ([string]::IsNullOrWhiteSpace([string]$item.Health)) { $null } else { [string]$item.Health }
            exit_code = [int]$item.ExitCode
        }
    })
}

function New-ExternalEndpointEvidence {
    param(
        [Parameter(Mandatory)][Uri]$Uri,
        [Parameter(Mandatory)][ValidateSet('readyz', 'client_config', 'oidc_discovery')][string]$Name,
        [Parameter(Mandatory)]$Evidence
    )
    $entry = [pscustomobject][ordered]@{
        name = $Name
        path = $Uri.AbsolutePath
        http_status = $null
        result = 'requesting'
    }
    $Evidence.endpoints += $entry
    return $entry
}

function Assert-ExternalEndpointStatus {
    param(
        [Parameter(Mandatory)][ValidateRange(100, 599)][int]$StatusCode,
        [Parameter(Mandatory)][ValidateSet('readyz', 'client_config', 'oidc_discovery')][string]$Name,
        [Parameter(Mandatory)][ValidatePattern('^EXTERNAL_[A-Z0-9_]+$')][string]$CodePrefix,
        [Parameter(Mandatory)]$EvidenceEntry
    )
    $EvidenceEntry.http_status = $StatusCode
    if ($StatusCode -ge 300 -and $StatusCode -le 399) {
        $EvidenceEntry.result = 'redirect_rejected'
        Stop-TailFirstE2E "${CodePrefix}_REDIRECT_FORBIDDEN" `
            "The external $Name endpoint returned a redirect; redirects are not allowed."
    }
    if ($StatusCode -ne 200) {
        $EvidenceEntry.result = 'http_error'
        Stop-TailFirstE2E "${CodePrefix}_HTTP_STATUS" `
            "The external $Name endpoint did not return HTTP 200."
    }
}

function Invoke-ExternalJsonEndpoint {
    param(
        [Parameter(Mandatory)][Uri]$Uri,
        [Parameter(Mandatory)][ValidateSet('readyz', 'client_config', 'oidc_discovery')][string]$Name,
        [Parameter(Mandatory)][ValidatePattern('^EXTERNAL_[A-Z0-9_]+$')][string]$CodePrefix,
        [Parameter(Mandatory)]$Evidence,
        [ValidateRange(1, 60)][int]$TimeoutSeconds = 15,
        [ValidateRange(1024, 1048576)][int]$MaximumBytes = 65536
    )
    $endpointEvidence = New-ExternalEndpointEvidence $Uri $Name $Evidence

    $handler = $null
    $client = $null
    $request = $null
    $cancellation = $null
    $response = $null
    try {
        Add-Type -AssemblyName System.Net.Http
        $handler = [Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseCookies = $false
        $handler.AutomaticDecompression = [Net.DecompressionMethods]::GZip -bor [Net.DecompressionMethods]::Deflate
        $handler.MaxResponseHeadersLength = 16
        $client = [Net.Http.HttpClient]::new($handler, $true)
        $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
        $request.Headers.Accept.ParseAdd('application/json')
        $cancellation = [Threading.CancellationTokenSource]::new()
        $cancellation.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds))
        $response = $client.SendAsync(
            $request,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $cancellation.Token
        ).GetAwaiter().GetResult()
        $statusCode = [int]$response.StatusCode
        Assert-ExternalEndpointStatus $statusCode $Name $CodePrefix $endpointEvidence
        $contentLength = $response.Content.Headers.ContentLength
        if ($null -ne $contentLength -and [long]$contentLength -gt $MaximumBytes) {
            $endpointEvidence.result = 'response_too_large'
            Stop-TailFirstE2E "${CodePrefix}_RESPONSE_TOO_LARGE" `
                "The external $Name endpoint response exceeded the allowed size."
        }

        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $memory = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(8192)
            $total = 0
            while (($read = $stream.ReadAsync(
                $buffer,
                0,
                $buffer.Length,
                $cancellation.Token
            ).GetAwaiter().GetResult()) -gt 0) {
                $total += $read
                if ($total -gt $MaximumBytes) {
                    $endpointEvidence.result = 'response_too_large'
                    Stop-TailFirstE2E "${CodePrefix}_RESPONSE_TOO_LARGE" `
                        "The external $Name endpoint response exceeded the allowed size."
                }
                $memory.Write($buffer, 0, $read)
            }
            try {
                $utf8 = [Text.UTF8Encoding]::new($false, $true)
                $raw = $utf8.GetString($memory.ToArray())
                $document = $raw | ConvertFrom-Json -ErrorAction Stop
            }
            catch {
                if ($script:failureCode) { throw }
                $endpointEvidence.result = 'invalid_json'
                Stop-TailFirstE2E "${CodePrefix}_JSON_INVALID" `
                    "The external $Name endpoint did not return valid UTF-8 JSON."
            }
        }
        finally {
            $memory.Dispose()
            $stream.Dispose()
        }
        $endpointEvidence.result = 'http_200_json'
        return [pscustomobject][ordered]@{ document = $document; evidence = $endpointEvidence }
    }
    catch {
        if ($script:failureCode) { throw }
        $endpointEvidence.result = 'request_failed'
        Stop-TailFirstE2E "${CodePrefix}_REQUEST_FAILED" `
            "The external $Name endpoint could not be read within the preflight limits."
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $cancellation) { $cancellation.Dispose() }
        if ($null -ne $request) { $request.Dispose() }
        if ($null -ne $client) { $client.Dispose() }
        elseif ($null -ne $handler) { $handler.Dispose() }
    }
}

function Get-ExternalHttpsPreflight {
    param(
        [Parameter(Mandatory)][Uri]$Origin,
        [Parameter(Mandatory)]$Evidence
    )
    $expectedOrigin = $Origin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
    $expectedIssuer = "$expectedOrigin/realms/termlink"
    $Evidence.server_origin = $expectedOrigin
    $Evidence.expected_oidc_issuer = $expectedIssuer

    $readyResult = Invoke-ExternalJsonEndpoint ([Uri]"$expectedOrigin/readyz") 'readyz' `
        'EXTERNAL_READYZ' $Evidence
    Assert-JsonProperties $readyResult.document @('status') 'External readiness response' `
        'EXTERNAL_READYZ_INVALID'
    if (-not ([string]$readyResult.document.status -ceq 'ready')) {
        $readyResult.evidence.result = 'status_mismatch'
        Stop-TailFirstE2E 'EXTERNAL_READYZ_NOT_READY' `
            'The external readiness endpoint did not report status ready.'
    }
    $readyResult.evidence.result = 'ready'

    $configResult = Invoke-ExternalJsonEndpoint ([Uri]"$expectedOrigin/v1/client-config") 'client_config' `
        'EXTERNAL_CLIENT_CONFIG' $Evidence
    $config = $configResult.document
    Assert-JsonProperties $config @(
        'oidc_issuer', 'oidc_client_id', 'redirect_uri', 'windows_oidc_client_id',
        'windows_redirect_uri', 'web_oidc_client_id', 'web_redirect_uri', 'protocol_versions'
    ) 'External client configuration' 'EXTERNAL_CLIENT_CONFIG_INVALID'
    foreach ($field in @('oidc_client_id', 'redirect_uri', 'windows_oidc_client_id',
        'windows_redirect_uri', 'web_oidc_client_id')) {
        Assert-E2E (-not [string]::IsNullOrWhiteSpace([string]$config.$field)) `
            'EXTERNAL_CLIENT_CONFIG_INVALID' 'The external client configuration is incomplete.'
    }
    $supportsProtocolV1 = $false
    foreach ($version in @($config.protocol_versions)) {
        if ((Test-JsonInteger $version ([int]::MaxValue)) -and [int]$version -eq 1) {
            $supportsProtocolV1 = $true
        }
    }
    Assert-E2E $supportsProtocolV1 'EXTERNAL_CLIENT_CONFIG_INVALID' `
        'The external client configuration does not advertise protocol version 1.'
    if (-not ([string]$config.oidc_issuer -ceq $expectedIssuer)) {
        $configResult.evidence.result = 'issuer_mismatch'
        Stop-TailFirstE2E 'EXTERNAL_CLIENT_CONFIG_ISSUER_MISMATCH' `
            'The external client configuration OIDC issuer does not match the acceptance origin.'
    }
    if (-not ([string]$config.web_redirect_uri -ceq "$expectedOrigin/auth/callback")) {
        $configResult.evidence.result = 'origin_mismatch'
        Stop-TailFirstE2E 'EXTERNAL_CLIENT_CONFIG_ORIGIN_MISMATCH' `
            'The external client configuration public web origin does not match the acceptance origin.'
    }
    $configResult.evidence.result = 'origins_matched'

    $discoveryResult = Invoke-ExternalJsonEndpoint `
        ([Uri]"$expectedIssuer/.well-known/openid-configuration") 'oidc_discovery' `
        'EXTERNAL_OIDC_DISCOVERY' $Evidence
    Assert-JsonProperties $discoveryResult.document @('issuer') 'External OIDC discovery response' `
        'EXTERNAL_OIDC_DISCOVERY_INVALID'
    if (-not ([string]$discoveryResult.document.issuer -ceq $expectedIssuer)) {
        $discoveryResult.evidence.result = 'issuer_mismatch'
        Stop-TailFirstE2E 'EXTERNAL_OIDC_ISSUER_MISMATCH' `
            'The external OIDC discovery issuer does not match the expected issuer.'
    }
    $discoveryResult.evidence.result = 'issuer_matched'
    return $Evidence
}

function Assert-NoSensitiveFields {
    param($Value)
    if ($null -eq $Value) { return }
    if ($Value -is [string] -or $Value.GetType().IsPrimitive) { return }
    if ($Value -is [Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            Assert-E2E ([string]$key -notmatch '(?i)(token|password|secret|authorization|cookie|private|credential|key_material)') `
                'E2E_HOOK_SENSITIVE_FIELD' 'The Debug hook returned a forbidden sensitive field.'
            Assert-NoSensitiveFields $Value[$key]
        }
        return
    }
    if ($Value -is [Collections.IEnumerable]) {
        foreach ($item in $Value) { Assert-NoSensitiveFields $item }
        return
    }
    foreach ($property in $Value.PSObject.Properties) {
        Assert-E2E ($property.Name -notmatch '(?i)(token|password|secret|authorization|cookie|private|credential|key_material)') `
            'E2E_HOOK_SENSITIVE_FIELD' 'The Debug hook returned a forbidden sensitive field.'
        Assert-NoSensitiveFields $property.Value
    }
}

function Invoke-Adb {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$Code, [Parameter(Mandatory)][string]$Message, [int]$TimeoutSeconds = 60)
    return Invoke-RequiredNative $script:adbPath (@('-s', $serial) + $Arguments) $Code $Message $TimeoutSeconds
}

function Start-HookOperation {
    param(
        [Parameter(Mandatory)][ValidateSet('probe', 'open_session', 'inspect', 'page_up', 'open_history', 'close_history', 'show_ime', 'begin_frozen_history', 'page_frozen_history', 'release_frozen_history')][string]$Operation,
        [hashtable]$Arguments = @{}
    )
    $requestId = [Guid]::NewGuid().ToString('N')
    $null = Invoke-Adb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $hookResponsePath) `
        'E2E_HOOK_RUNTIME_UNAVAILABLE' 'The installed app is not a debuggable APK with the tail-first hook.'
    if ($Operation -ceq 'open_session') {
        $null = Invoke-Adb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $hookSessionArmPath) `
            'E2E_HOOK_RUNTIME_UNAVAILABLE' 'The installed app cannot reset the tail-first session-arm evidence.'
    }
    $broadcast = @(
        'shell', 'am', 'broadcast', '--async', '-a', $hookAction,
        '-n', "$AndroidPackageName/$HookReceiverClass",
        '--es', 'e2e_operation', $Operation,
        '--es', 'request_id', $requestId,
        '--es', 'run_id', $ArtifactRunId
    )
    foreach ($key in @($Arguments.Keys | Sort-Object)) {
        $broadcast += @('--es', [string]$key, [string]$Arguments[$key])
    }
    $null = Invoke-Adb $broadcast 'E2E_HOOK_BROADCAST_FAILED' "Debug hook operation '$Operation' could not be requested."

    return [pscustomobject][ordered]@{
        operation = $Operation
        request_id = $requestId
    }
}

function Wait-HookOperation {
    param(
        [Parameter(Mandatory)]$Request,
        [switch]$AcceptFailure
    )
    $Operation = [string]$Request.operation
    $requestId = [string]$Request.request_id

    $deadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
    $invalidJsonSeen = $false
    do {
        $result = Invoke-NativeCapture $script:adbPath @('-s', $serial, 'exec-out', 'run-as', $AndroidPackageName, 'cat', $hookResponsePath) 10
        if ($result.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$result.StdOut)) {
            Assert-E2E ([Text.Encoding]::UTF8.GetByteCount([string]$result.StdOut) -le 1048576) `
                'E2E_HOOK_RESPONSE_TOO_LARGE' 'The Debug hook response exceeded 1 MiB.'
            $response = $null
            try { $response = [string]$result.StdOut | ConvertFrom-Json -ErrorAction Stop }
            catch { $invalidJsonSeen = $true }
            if ($null -ne $response) {
                Assert-NoSensitiveFields $response
                Assert-JsonProperties $response @('schema_version', 'request_id', 'operation', 'ok') `
                    "Debug hook operation '$Operation' response"
                if ([string]$response.request_id -eq $requestId) {
                    Assert-E2E ([int]$response.schema_version -eq 1 -and [string]$response.operation -eq $Operation) `
                        'E2E_HOOK_RESPONSE_INVALID' 'The Debug hook response does not match the requested operation.'
                    Assert-E2E ($response.ok -is [bool]) 'E2E_HOOK_RESPONSE_INVALID' `
                        'The Debug hook response ok field is not a JSON boolean.'
                    if (-not [bool]$response.ok) {
                        if ($FrozenLifecycleAcceptance -and $Operation -ceq 'open_session' -and
                            (Test-Path -LiteralPath $frozenFixtureStatusPath -PathType Leaf) -and
                            (Get-Content -LiteralPath $frozenFixtureStatusPath -Raw).StartsWith('FROZEN_FIXTURE_RESIZE_FAILED')) {
                            Stop-TailFirstE2E 'FROZEN_FIXTURE_RESIZE_FAILED' `
                                'The owned ConPTY child did not observe the requested 32-column terminal width within 15 seconds.'
                        }
                        Assert-JsonProperties $response @('failure_code') "Debug hook operation '$Operation' failure"
                        Assert-E2E ([string]$response.failure_code -match '^[A-Z0-9_]{1,64}$') `
                            'E2E_HOOK_RESPONSE_INVALID' "Debug hook operation '$Operation' returned an invalid failure code."
                        if (-not [bool]$AcceptFailure) {
                            $stableFailureCode = [string]$response.failure_code
                            $script:hookFailureCode = $stableFailureCode
                            Stop-TailFirstE2E 'E2E_HOOK_OPERATION_FAILED' `
                                "Debug hook operation '$Operation' reported failure_code '$stableFailureCode'."
                        }
                    }
                    return $response
                }
            }
        }
        if ($FrozenLifecycleAcceptance -and $Operation -ceq 'open_session' -and
            (Test-Path -LiteralPath $frozenFixtureStatusPath -PathType Leaf) -and
            (Get-Content -LiteralPath $frozenFixtureStatusPath -Raw).StartsWith('FROZEN_FIXTURE_RESIZE_FAILED')) {
            Stop-TailFirstE2E 'FROZEN_FIXTURE_RESIZE_FAILED' `
                'The owned ConPTY child did not observe the requested 32-column terminal width within 15 seconds.'
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($invalidJsonSeen) {
        Stop-TailFirstE2E 'E2E_HOOK_RESPONSE_INVALID' 'The Debug hook did not finish a valid JSON response before timeout.'
    }
    Stop-TailFirstE2E 'E2E_HOOK_RESPONSE_TIMEOUT' "Debug hook operation '$Operation' timed out."
}

function Invoke-HookOperation {
    param(
        [Parameter(Mandatory)][ValidateSet('probe', 'open_session', 'inspect', 'page_up', 'open_history', 'close_history', 'show_ime', 'begin_frozen_history', 'page_frozen_history', 'release_frozen_history')][string]$Operation,
        [hashtable]$Arguments = @{},
        [switch]$AcceptFailure
    )
    $request = Start-HookOperation -Operation $Operation -Arguments $Arguments
    return Wait-HookOperation -Request $request -AcceptFailure:$AcceptFailure
}

function Wait-SessionArm {
    param([Parameter(Mandatory)]$Request)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $invalidJsonSeen = $false
    do {
        $result = Invoke-NativeCapture $script:adbPath `
            @('-s', $serial, 'exec-out', 'run-as', $AndroidPackageName, 'cat', $hookSessionArmPath) 10
        if ($result.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$result.StdOut)) {
            Assert-E2E ([Text.Encoding]::UTF8.GetByteCount([string]$result.StdOut) -le 1048576) `
                'E2E_HOOK_RESPONSE_TOO_LARGE' 'The Debug hook session-arm response exceeded 1 MiB.'
            $arm = $null
            try { $arm = [string]$result.StdOut | ConvertFrom-Json -ErrorAction Stop }
            catch { $invalidJsonSeen = $true }
            if ($null -ne $arm) {
                Assert-NoSensitiveFields $arm
                Assert-JsonProperties $arm @(
                    'schema_version', 'request_id', 'operation', 'armed',
                    'armed_at_epoch_millis', 'baseline_session_ids'
                ) 'Debug hook session-arm response'
                if ([string]$arm.request_id -ceq [string]$Request.request_id) {
                    Assert-E2E ([int]$arm.schema_version -eq 1 -and [string]$arm.operation -ceq 'open_session' -and
                        $arm.armed -is [bool] -and [bool]$arm.armed) 'E2E_HOOK_RESPONSE_INVALID' `
                        'The Debug hook session-arm response does not match the pending open_session request.'
                    Assert-E2E ((Test-JsonInteger $arm.armed_at_epoch_millis ([decimal][int64]::MaxValue)) -and
                        [int64]$arm.armed_at_epoch_millis -gt 0) 'E2E_HOOK_RESPONSE_INVALID' `
                        'The Debug hook session-arm timestamp is invalid.'
                    $baselineIds = @($arm.baseline_session_ids | ForEach-Object { [string]$_ })
                    foreach ($id in $baselineIds) {
                        $parsed = [Guid]::Empty
                        Assert-E2E ([Guid]::TryParse($id, [ref]$parsed) -and $parsed -ne [Guid]::Empty) `
                            'E2E_HOOK_RESPONSE_INVALID' 'The Debug hook session-arm baseline contains an invalid session ID.'
                    }
                    Assert-E2E (@($baselineIds | Select-Object -Unique).Count -eq $baselineIds.Count) `
                        'E2E_HOOK_RESPONSE_INVALID' 'The Debug hook session-arm baseline contains a duplicate session ID.'
                    return [pscustomobject][ordered]@{
                        request_id = [string]$arm.request_id
                        armed_at_utc = [DateTimeOffset]::FromUnixTimeMilliseconds(
                            [int64]$arm.armed_at_epoch_millis
                        ).UtcDateTime.ToString('o')
                        arm_received_at_utc = [DateTime]::UtcNow.ToString('o')
                        baseline_session_ids = $baselineIds
                        baseline_session_count = $baselineIds.Count
                    }
                }
            }
        }
        # open_session arms the baseline before SessionHost starts. If Android
        # rejects that exact request, consume its response now instead of
        # waiting for the unrelated session-arm timeout.
        $responseResult = Invoke-NativeCapture $script:adbPath `
            @('-s', $serial, 'exec-out', 'run-as', $AndroidPackageName, 'cat', $hookResponsePath) 10
        if ($responseResult.ExitCode -eq 0 -and
            -not [string]::IsNullOrWhiteSpace([string]$responseResult.StdOut)) {
            Assert-E2E ([Text.Encoding]::UTF8.GetByteCount([string]$responseResult.StdOut) -le 1048576) `
                'E2E_HOOK_RESPONSE_TOO_LARGE' 'The Debug hook response exceeded 1 MiB.'
            $response = $null
            try { $response = [string]$responseResult.StdOut | ConvertFrom-Json -ErrorAction Stop }
            catch { $invalidJsonSeen = $true }
            if ($null -ne $response) {
                Assert-NoSensitiveFields $response
                Assert-JsonProperties $response @('schema_version', 'request_id', 'operation', 'ok') `
                    'Debug hook open_session failure response'
                if ([string]$response.request_id -ceq [string]$Request.request_id) {
                    Assert-E2E ([int]$response.schema_version -eq 1 -and
                        [string]$response.operation -ceq 'open_session' -and
                        $response.ok -is [bool]) 'E2E_HOOK_RESPONSE_INVALID' `
                        'The Debug hook open_session failure response does not match the pending request.'
                    if (-not [bool]$response.ok) {
                        Assert-JsonProperties $response @('failure_code') 'Debug hook open_session failure response'
                        Assert-E2E ([string]$response.failure_code -match '^[A-Z0-9_]{1,64}$') `
                            'E2E_HOOK_RESPONSE_INVALID' 'The Debug hook open_session failure code is invalid.'
                        $stableFailureCode = [string]$response.failure_code
                        $script:hookFailureCode = $stableFailureCode
                        Stop-TailFirstE2E 'E2E_HOOK_OPERATION_FAILED' `
                            "Debug hook operation 'open_session' reported failure_code '$stableFailureCode'."
                    }
                    Stop-TailFirstE2E 'E2E_HOOK_RESPONSE_INVALID' `
                        'The Debug hook completed open_session without arming its session baseline.'
                }
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($invalidJsonSeen) {
        Stop-TailFirstE2E 'E2E_HOOK_RESPONSE_INVALID' `
            'Android did not finish a valid session-arm JSON response before timeout.'
    }
    Stop-TailFirstE2E 'E2E_SESSION_BASELINE_TIMEOUT' `
        'Android did not arm its existing-session baseline before SessionHost startup.'
}

function Set-AndroidAirplaneMode {
    param([Parameter(Mandatory)][bool]$Enabled)
    $verb = if ($Enabled) { 'enable' } else { 'disable' }
    $expected = if ($Enabled) { '1' } else { '0' }
    $null = Invoke-Adb @('shell', 'cmd', 'connectivity', 'airplane-mode', $verb) `
        'ANDROID_NETWORK_INJECTION_FAILED' "The Android network could not be switched to airplane mode '$verb'."
    if ($Enabled) { $script:airplaneModeEnabled = $true }
    $observed = Invoke-Adb @('shell', 'settings', 'get', 'global', 'airplane_mode_on') `
        'ANDROID_NETWORK_INJECTION_FAILED' 'The Android airplane-mode state could not be inspected.'
    Assert-E2E ([string]$observed.Trim() -eq $expected) 'ANDROID_NETWORK_INJECTION_FAILED' `
        "The Android airplane-mode state did not become '$expected'."
    $networkDeadline = [DateTime]::UtcNow.AddSeconds(20)
    $networkTransitioned = $false
    do {
        $connectivity = Invoke-Adb @('shell', 'dumpsys', 'connectivity') `
            'ANDROID_NETWORK_INJECTION_FAILED' 'The Android active network could not be inspected.'
        $activeMatch = [regex]::Match([string]$connectivity, '(?m)^Active default network:\s*([^\s]+)\s*$')
        if ($activeMatch.Success) {
            $activeNetwork = $activeMatch.Groups[1].Value
            $networkTransitioned = if ($Enabled) {
                $activeNetwork -ceq 'none'
            } else {
                $activeNetwork -match '^[0-9]+$'
            }
        }
        if (-not $networkTransitioned) { Start-Sleep -Milliseconds 250 }
    } while (-not $networkTransitioned -and [DateTime]::UtcNow -lt $networkDeadline)
    Assert-E2E $networkTransitioned 'ANDROID_NETWORK_INJECTION_FAILED' `
        "The Android active network did not finish the airplane-mode '$verb' transition."
    if (-not $Enabled) { $script:airplaneModeEnabled = $false }
}

function Add-Assertion {
    param([Parameter(Mandatory)][string]$Id, [Parameter(Mandatory)][string]$Expected, $Actual, [bool]$Passed)
    $script:evidence.assertions += [pscustomobject][ordered]@{
        id = $Id
        expected = $Expected
        actual = $Actual
        passed = $Passed
        observed_at_utc = [DateTime]::UtcNow.ToString('o')
    }
    Assert-E2E $Passed 'ASSERTION_FAILED' "Tail-first assertion '$Id' failed."
}

function Get-StateObservation {
    param([Parameter(Mandatory)][string]$Phase, [Parameter(Mandatory)]$Response)
    Assert-JsonProperties $Response @(
        'session_id', 'snapshot_count', 'transcript_count', 'chunk_count',
        'snapshot_sequence_s', 'live_ack', 'bootstrap_source', 'history_overlay',
        'first_marker_visible', 'last_marker_visible', 'live_marker_visible',
        'viewport_signature', 'first_visible_line_id', 'visible_text_sha256',
        'next_before_line_id', 'transcript_top_row', 'transcript_page_count',
        'transcript_row_count', 'terminal_columns', 'terminal_rows',
        'realtime_socket_phase', 'realtime_socket_state', 'realtime_transport_state',
        'realtime_remote_state', 'realtime_socket_generation', 'realtime_connected_generation',
        'realtime_endpoint_count', 'realtime_reconnect_attempt', 'realtime_subscribe_send_count',
        'realtime_last_subscribe_cursor', 'realtime_flow_control_send_count',
        'realtime_last_received_sequence', 'realtime_last_handled_sequence',
        'realtime_network_available_count', 'realtime_network_lost_count',
        'realtime_network_reconnect_count', 'realtime_transport_collector_active',
        'realtime_reconnect_active', 'realtime_subscriber_ready'
    ) "Debug hook state for '$Phase'"
    $sessionId = [Guid]::Empty
    Assert-E2E ([Guid]::TryParse([string]$Response.session_id, [ref]$sessionId) -and $sessionId -ne [Guid]::Empty) `
        'E2E_HOOK_RESPONSE_INVALID' "Debug hook state for '$Phase' has an invalid session ID."
    foreach ($field in @('snapshot_count', 'transcript_count', 'chunk_count')) {
        Assert-E2E (Test-JsonInteger $Response.$field ([int]::MaxValue)) 'E2E_HOOK_RESPONSE_INVALID' `
            "Debug hook state for '$Phase' has an invalid $field."
    }
    foreach ($field in @('snapshot_sequence_s', 'live_ack')) {
        Assert-E2E (Test-JsonInteger $Response.$field ([decimal][uint64]::MaxValue)) 'E2E_HOOK_RESPONSE_INVALID' `
            "Debug hook state for '$Phase' has an invalid $field."
    }
    foreach ($field in @(
        'realtime_socket_generation', 'realtime_connected_generation', 'realtime_endpoint_count',
        'realtime_reconnect_attempt', 'realtime_subscribe_send_count', 'realtime_last_subscribe_cursor',
        'realtime_flow_control_send_count', 'realtime_last_received_sequence',
        'realtime_last_handled_sequence', 'realtime_network_available_count',
        'realtime_network_lost_count', 'realtime_network_reconnect_count'
    )) {
        Assert-E2E (Test-JsonInteger $Response.$field ([decimal][uint64]::MaxValue)) `
            'E2E_HOOK_RESPONSE_INVALID' "Debug hook state for '$Phase' has an invalid $field."
    }
    foreach ($field in @(
        'bootstrap_source', 'realtime_socket_phase', 'realtime_socket_state',
        'realtime_transport_state', 'realtime_remote_state'
    )) {
        Assert-E2E ([string]$Response.$field -cmatch '^[A-Z_]{2,32}$') `
            'E2E_HOOK_RESPONSE_INVALID' "Debug hook state for '$Phase' has an invalid $field."
    }
    foreach ($field in @('terminal_columns', 'terminal_rows')) {
        Assert-E2E (Test-JsonInteger $Response.$field 65535 -and [int]$Response.$field -ge 1) `
            'E2E_HOOK_RESPONSE_INVALID' "Debug hook state for '$Phase' has an invalid $field."
    }
    foreach ($field in @('history_overlay', 'first_marker_visible', 'last_marker_visible', 'live_marker_visible')) {
        Assert-E2E ($Response.$field -is [bool]) 'E2E_HOOK_RESPONSE_INVALID' `
            "Debug hook state for '$Phase' has a non-boolean $field."
    }
    foreach ($field in @(
        'realtime_transport_collector_active', 'realtime_reconnect_active', 'realtime_subscriber_ready'
    )) {
        Assert-E2E ($Response.$field -is [bool]) 'E2E_HOOK_RESPONSE_INVALID' `
            "Debug hook state for '$Phase' has a non-boolean $field."
    }
    if ($Response.PSObject.Properties['realtime_transport_failure_reason']) {
        Assert-E2E ([string]$Response.realtime_transport_failure_reason -cmatch '^[\x20-\x7e]{1,256}$') `
            'E2E_HOOK_RESPONSE_INVALID' "Debug hook state has an invalid transport failure reason."
    }
    Assert-E2E ([string]$Response.viewport_signature -match '^[A-Za-z0-9:_-]{1,128}$') `
        'E2E_HOOK_RESPONSE_INVALID' "Debug hook state for '$Phase' has an invalid viewport signature."
    Assert-E2E ([string]$Response.visible_text_sha256 -cmatch '^[0-9a-f]{64}$') `
        'E2E_HOOK_RESPONSE_INVALID' "Debug hook state for '$Phase' has an invalid visible-text hash."
    foreach ($field in @(
        'first_visible_line_id', 'next_before_line_id', 'transcript_page_count', 'transcript_row_count'
    )) {
        Assert-E2E (Test-JsonInteger $Response.$field ([decimal][uint64]::MaxValue)) `
            'E2E_HOOK_RESPONSE_INVALID' "Debug hook state for '$Phase' has an invalid $field."
    }
    $transcriptTopRowValid = $false
    if ($null -ne $Response.transcript_top_row -and
        $Response.transcript_top_row -isnot [string] -and
        $Response.transcript_top_row -isnot [bool]) {
        try {
            $transcriptTopRowNumber = [decimal]$Response.transcript_top_row
            $transcriptTopRowValid =
                $transcriptTopRowNumber -ge [int]::MinValue -and
                $transcriptTopRowNumber -le [int]::MaxValue -and
                [decimal]::Truncate($transcriptTopRowNumber) -eq $transcriptTopRowNumber
        }
        catch { $transcriptTopRowValid = $false }
    }
    Assert-E2E $transcriptTopRowValid `
        'E2E_HOOK_RESPONSE_INVALID' "Debug hook state for '$Phase' has an invalid transcript_top_row."
    return [pscustomobject][ordered]@{
        phase = $Phase
        observed_at_utc = [DateTime]::UtcNow.ToString('o')
        session_id = [string]$Response.session_id
        snapshot_count = [int]$Response.snapshot_count
        transcript_count = [int]$Response.transcript_count
        chunk_count = [int]$Response.chunk_count
        snapshot_sequence_s = [uint64]$Response.snapshot_sequence_s
        live_ack = [uint64]$Response.live_ack
        bootstrap_source = [string]$Response.bootstrap_source
        history_overlay = [bool]$Response.history_overlay
        first_marker_visible = [bool]$Response.first_marker_visible
        last_marker_visible = [bool]$Response.last_marker_visible
        live_marker_visible = [bool]$Response.live_marker_visible
        viewport_signature = [string]$Response.viewport_signature
        first_visible_line_id = [uint64]$Response.first_visible_line_id
        visible_text_sha256 = [string]$Response.visible_text_sha256
        next_before_line_id = [uint64]$Response.next_before_line_id
        transcript_top_row = [int]$Response.transcript_top_row
        transcript_page_count = [int]$Response.transcript_page_count
        transcript_row_count = [int]$Response.transcript_row_count
        terminal_columns = [int]$Response.terminal_columns
        terminal_rows = [int]$Response.terminal_rows
        realtime_socket_phase = [string]$Response.realtime_socket_phase
        realtime_socket_state = [string]$Response.realtime_socket_state
        realtime_transport_state = [string]$Response.realtime_transport_state
        realtime_transport_failure_reason = if ($Response.PSObject.Properties['realtime_transport_failure_reason']) { [string]$Response.realtime_transport_failure_reason } else { $null }
        realtime_terminal_accepted_generation = if ($Response.PSObject.Properties['realtime_terminal_accepted_generation']) { [uint64]$Response.realtime_terminal_accepted_generation } else { [uint64]0 }
        realtime_accepted_terminal_profile = if ($Response.PSObject.Properties['realtime_accepted_terminal_profile']) { [string]$Response.realtime_accepted_terminal_profile } else { $null }
        realtime_remote_state = [string]$Response.realtime_remote_state
        realtime_socket_generation = [uint64]$Response.realtime_socket_generation
        realtime_connected_generation = [uint64]$Response.realtime_connected_generation
        realtime_endpoint_count = [uint64]$Response.realtime_endpoint_count
        realtime_reconnect_attempt = [uint64]$Response.realtime_reconnect_attempt
        realtime_subscribe_send_count = [uint64]$Response.realtime_subscribe_send_count
        realtime_last_subscribe_cursor = [uint64]$Response.realtime_last_subscribe_cursor
        realtime_flow_control_send_count = [uint64]$Response.realtime_flow_control_send_count
        realtime_last_received_sequence = [uint64]$Response.realtime_last_received_sequence
        realtime_last_handled_sequence = [uint64]$Response.realtime_last_handled_sequence
        realtime_network_available_count = [uint64]$Response.realtime_network_available_count
        realtime_network_lost_count = [uint64]$Response.realtime_network_lost_count
        realtime_network_reconnect_count = [uint64]$Response.realtime_network_reconnect_count
        realtime_transport_collector_active = [bool]$Response.realtime_transport_collector_active
        realtime_reconnect_active = [bool]$Response.realtime_reconnect_active
        realtime_subscriber_ready = [bool]$Response.realtime_subscriber_ready
    }
}

function Get-PaginationObservation {
    param(
        [Parameter(Mandatory)][int]$PageNumber,
        [Parameter(Mandatory)]$Response,
        [Parameter(Mandatory)]$Previous,
        [Parameter(Mandatory)][string]$ExpectedSessionId,
        [Parameter(Mandatory)][uint64]$ExpectedSnapshot,
        [Parameter(Mandatory)][uint64]$ExpectedAck,
        [Parameter(Mandatory)][int]$ExpectedColumns,
        [Parameter(Mandatory)][int]$ExpectedRows,
        [Parameter(Mandatory)]$ExpectedPage,
        [Parameter(Mandatory)][Collections.Generic.HashSet[string]]$SeenViewportSignatures
    )
    Assert-JsonProperties $Response @(
        'pages_moved', 'rows_loaded', 'request_before_line_id', 'loaded_first_line_id',
        'loaded_last_line_id', 'loaded_has_more_before', 'next_before_line_id'
    ) "Debug hook page-up $PageNumber response"
    Assert-E2E (Test-JsonInteger $Response.pages_moved ([int]::MaxValue)) 'E2E_HOOK_RESPONSE_INVALID' `
        "Debug hook page-up $PageNumber response has invalid pages_moved."
    Assert-E2E (Test-JsonInteger $Response.rows_loaded ([int]::MaxValue)) 'E2E_HOOK_RESPONSE_INVALID' `
        "Debug hook page-up $PageNumber response has invalid rows_loaded."
    foreach ($field in @('request_before_line_id', 'loaded_first_line_id', 'loaded_last_line_id', 'next_before_line_id')) {
        Assert-E2E (Test-JsonInteger $Response.$field ([decimal][uint64]::MaxValue)) 'E2E_HOOK_RESPONSE_INVALID' `
            "Debug hook page-up $PageNumber response has invalid $field."
    }
    Assert-E2E ($Response.loaded_has_more_before -is [bool]) 'E2E_HOOK_RESPONSE_INVALID' `
        "Debug hook page-up $PageNumber response has invalid loaded_has_more_before."
    $observation = Get-StateObservation "page-up-$PageNumber" $Response
    $observation | Add-Member -NotePropertyName pages_moved -NotePropertyValue ([int]$Response.pages_moved)
    $observation | Add-Member -NotePropertyName rows_loaded -NotePropertyValue ([int]$Response.rows_loaded)
    $observation | Add-Member -NotePropertyName request_before_line_id -NotePropertyValue ([uint64]$Response.request_before_line_id)
    $observation | Add-Member -NotePropertyName loaded_first_line_id -NotePropertyValue ([uint64]$Response.loaded_first_line_id)
    $observation | Add-Member -NotePropertyName loaded_last_line_id -NotePropertyValue ([uint64]$Response.loaded_last_line_id)
    $observation | Add-Member -NotePropertyName loaded_has_more_before -NotePropertyValue ([bool]$Response.loaded_has_more_before)
    $observation | Add-Member -NotePropertyName next_before_line_id `
        -NotePropertyValue ([uint64]$Response.next_before_line_id) -Force
    $script:evidence.observations += $observation
    Add-Assertion "page-$PageNumber-session-fixed" $ExpectedSessionId $observation.session_id `
        ($observation.session_id -ceq $ExpectedSessionId)
    Add-Assertion "page-$PageNumber-single-page" '1' $observation.pages_moved ($observation.pages_moved -eq 1)
    Add-Assertion "page-$PageNumber-bounded-rows" "1..$HistoryPageSize" $observation.rows_loaded `
        ($observation.rows_loaded -ge 1 -and $observation.rows_loaded -le $HistoryPageSize)
    $expectedRowCount = [int]($ExpectedPage.last - $ExpectedPage.first + 1)
    Add-Assertion "page-$PageNumber-exact-row-count" ([string]$expectedRowCount) $observation.rows_loaded `
        ($observation.rows_loaded -eq $expectedRowCount)
    Add-Assertion "page-$PageNumber-transcript-count-advanced-once" ([string]($Previous.transcript_count + 1)) `
        $observation.transcript_count ($observation.transcript_count -eq $Previous.transcript_count + 1)
    Add-Assertion "page-$PageNumber-snapshot-count-fixed" ([string]$Previous.snapshot_count) `
        $observation.snapshot_count ($observation.snapshot_count -eq $Previous.snapshot_count)
    Add-Assertion "page-$PageNumber-chunk-count-fixed" '0' $observation.chunk_count `
        ($observation.chunk_count -eq 0)
    Add-Assertion "page-$PageNumber-snapshot-s-fixed" ([string]$ExpectedSnapshot) $observation.snapshot_sequence_s `
        ($observation.snapshot_sequence_s -eq $ExpectedSnapshot)
    Add-Assertion "page-$PageNumber-live-ack-fixed" ([string]$ExpectedAck) $observation.live_ack `
        ($observation.live_ack -eq $ExpectedAck)
    Add-Assertion "page-$PageNumber-grid-columns-fixed" ([string]$ExpectedColumns) $observation.terminal_columns `
        ($observation.terminal_columns -eq $ExpectedColumns)
    Add-Assertion "page-$PageNumber-grid-rows-fixed" ([string]$ExpectedRows) $observation.terminal_rows `
        ($observation.terminal_rows -eq $ExpectedRows)
    Add-Assertion "page-$PageNumber-history-overlay-visible" 'true' $observation.history_overlay `
        $observation.history_overlay
    $uniqueSignature = $SeenViewportSignatures.Add($observation.viewport_signature)
    Add-Assertion "page-$PageNumber-viewport-not-duplicate" 'unique' $observation.viewport_signature $uniqueSignature
    Add-Assertion "page-$PageNumber-previous-page-had-cursor" 'true' $Previous.loaded_has_more_before `
        ([bool]$Previous.loaded_has_more_before)
    Add-Assertion "page-$PageNumber-request-follows-previous-cursor" ([string]$Previous.next_before_line_id) `
        $observation.request_before_line_id ($observation.request_before_line_id -eq $Previous.next_before_line_id)
    Add-Assertion "page-$PageNumber-request-cursor" ([string]$ExpectedPage.request_before) `
        $observation.request_before_line_id ($observation.request_before_line_id -eq $ExpectedPage.request_before)
    Add-Assertion "page-$PageNumber-first-line" ([string]$ExpectedPage.first) `
        $observation.loaded_first_line_id ($observation.loaded_first_line_id -eq $ExpectedPage.first)
    Add-Assertion "page-$PageNumber-last-line" ([string]$ExpectedPage.last) `
        $observation.loaded_last_line_id ($observation.loaded_last_line_id -eq $ExpectedPage.last)
    Add-Assertion "page-$PageNumber-has-more" ([string]$ExpectedPage.has_more) `
        $observation.loaded_has_more_before ($observation.loaded_has_more_before -eq $ExpectedPage.has_more)
    $expectedNext = if ($ExpectedPage.has_more) { $ExpectedPage.first } else { [uint64]0 }
    Add-Assertion "page-$PageNumber-next-cursor" ([string]$expectedNext) `
        $observation.next_before_line_id ($observation.next_before_line_id -eq $expectedNext)
    return $observation
}

function Save-Screenshot {
    param([Parameter(Mandatory)][string]$Name)
    $path = Join-Path $artifactDirectory $Name
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:adbPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-s', $serial, 'exec-out', 'screencap', '-p')) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Assert-E2E ($null -ne $process) 'SCREENSHOT_FAILED' "Could not capture screenshot '$Name'."
    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $process.StandardOutput.BaseStream.CopyTo($stream) } finally { $stream.Dispose() }
        Assert-E2E ($process.WaitForExit(30000) -and $process.ExitCode -eq 0) 'SCREENSHOT_FAILED' `
            "Could not capture screenshot '$Name'."
        $bytes = [IO.File]::ReadAllBytes($path)
        $validPng = $bytes.Length -gt 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and
            $bytes[2] -eq 0x4e -and $bytes[3] -eq 0x47
        Assert-E2E $validPng 'SCREENSHOT_FAILED' "Screenshot '$Name' is not a valid PNG."
    }
    finally { $process.Dispose() }
    return $Name
}

function ConvertTo-Rect {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Label)
    Assert-JsonProperties $Value @('left', 'top', 'right', 'bottom') "Debug hook $Label bounds"
    foreach ($field in @('left', 'top', 'right', 'bottom')) {
        Assert-E2E (Test-JsonInteger $Value.$field ([int]::MaxValue)) 'E2E_LAYOUT_INVALID' `
            "Debug hook returned invalid $Label bounds."
    }
    try {
        $rect = [pscustomobject][ordered]@{
            left = [int]$Value.left; top = [int]$Value.top; right = [int]$Value.right; bottom = [int]$Value.bottom
        }
    }
    catch { Stop-TailFirstE2E 'E2E_LAYOUT_INVALID' "Debug hook returned invalid $Label bounds." }
    Assert-E2E ($rect.left -ge 0 -and $rect.top -ge 0 -and $rect.right -gt $rect.left -and $rect.bottom -gt $rect.top) `
        'E2E_LAYOUT_INVALID' "Debug hook returned invalid $Label bounds."
    return $rect
}

function Get-EmulatorAvdName {
    param([Parameter(Mandatory)][string]$DeviceSerial)
    $result = Invoke-NativeCapture $script:adbPath @('-s', $DeviceSerial, 'emu', 'avd', 'name') 10
    if ($result.ExitCode -ne 0) { return $null }
    return @(([string]$result.StdOut -split '\r?\n') |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne 'OK' } |
        Select-Object -First 1)[0]
}

function Get-AvdStartMode {
    param([Parameter(Mandatory)][string]$DevicesOutput)
    $selected = [regex]::Match($DevicesOutput, "(?m)^$([regex]::Escape($serial))\s+(\S+)\s*`$")
    if ($selected.Success) {
        Assert-E2E $FrozenLifecycleAcceptance 'AVD_NOT_COLD' `
            'The selected emulator port is already in use; a cold start is required.'
        Assert-E2E ($selected.Groups[1].Value -ceq 'device') 'AVD_NOT_READY' `
            'The selected emulator is connected but not ready.'
        Assert-E2E ((Get-EmulatorAvdName $serial) -ceq $AvdName) 'AVD_IDENTITY_MISMATCH' `
            'The running emulator does not match the requested AVD.'
        return 'warm_existing'
    }
    foreach ($runningSerial in @([regex]::Matches($DevicesOutput, '(?m)^(emulator-\d+)\s+device\s*$') |
        ForEach-Object { $_.Groups[1].Value })) {
        $runningAvd = Get-EmulatorAvdName $runningSerial
        Assert-E2E ([string]::IsNullOrWhiteSpace($runningAvd) -or $runningAvd -cne $AvdName) 'AVD_NOT_COLD' `
            'The requested AVD is already running on another serial.'
    }
    return $(if ($FrozenLifecycleAcceptance) { 'owned_preserved_data' } else { 'cold_owned' })
}

function Get-CandidateApkIdentity {
    param([Parameter(Mandatory)][string]$ApkPath, [Parameter(Mandatory)][string]$SdkRoot)
    $buildTools = Join-Path $SdkRoot 'build-tools'
    $aapt = @(Get-ChildItem -LiteralPath $buildTools -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(?:\.\d+)+$' -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'aapt.exe') -PathType Leaf) } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'aapt.exe' })
    Assert-E2E ($aapt.Count -eq 1) 'ANDROID_AAPT_UNAVAILABLE' `
        'Android SDK build-tools with aapt.exe are required to verify the candidate APK.'
    $badging = Invoke-RequiredNative $aapt[0] @('dump', 'badging', $ApkPath) `
        'ANDROID_APK_IDENTITY_INVALID' 'Could not read the candidate APK identity.'
    $match = [regex]::Match($badging, "(?m)^package: name='([^']+)' versionCode='(\d+)' versionName='([^']+)'(?:\s|`$)")
    Assert-E2E $match.Success 'ANDROID_APK_IDENTITY_INVALID' 'The candidate APK package/version is unavailable.'
    Assert-E2E ($match.Groups[1].Value -ceq $AndroidPackageName) 'ANDROID_APK_PACKAGE_MISMATCH' `
        'The candidate APK package differs from AndroidPackageName.'
    return [pscustomobject][ordered]@{
        package = $match.Groups[1].Value
        version_code = $match.Groups[2].Value
        version_name = $match.Groups[3].Value
        sha256 = (Get-FileHash -LiteralPath $ApkPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Test-FailureSessionIdentity {
    param([Parameter(Mandatory)][string]$CandidateId, [Parameter(Mandatory)]$Arm)
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($CandidateId, [ref]$parsed) -or $parsed -eq [Guid]::Empty) { return $false }
    if (-not [string]::IsNullOrWhiteSpace([string]$Arm.selected_session_id)) {
        return $CandidateId -ceq [string]$Arm.selected_session_id
    }
    return $Arm.baseline_session_ids -cnotcontains $CandidateId
}

# Local fixture forensics: schema-screened hook response (<=1 MiB) and filtered logcat.
function Save-HookFailureForensics {
    if ($null -eq $script:adbPath) { return }
    $hookResponseSaved = $false
    $logcatSaved = $false
    try {
        $result = Invoke-NativeCapture $script:adbPath `
            @('-s', $serial, 'exec-out', 'run-as', $AndroidPackageName, 'cat', $hookResponsePath) 10
        if ($result.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$result.StdOut)) {
            Assert-E2E ([Text.Encoding]::UTF8.GetByteCount([string]$result.StdOut) -le 1048576) `
                'E2E_HOOK_RESPONSE_TOO_LARGE' 'The failed Debug hook response exceeded 1 MiB.'
            $candidate = [string]$result.StdOut | ConvertFrom-Json -ErrorAction Stop
            Assert-NoSensitiveFields $candidate
            [IO.File]::WriteAllText((Join-Path $artifactDirectory 'failed-stage-hook-response.json'),
                [string]$result.StdOut, [Text.UTF8Encoding]::new($false))
            $hookResponseSaved = $true
        }
    }
    catch {}
    try {
        $logcat = Invoke-NativeCapture $script:adbPath @('-s', $serial, 'logcat', '-d', '-t', '600',
            '-s', 'TermLinkRealtime', 'TermLinkFrozenHistory', 'TermLinkTailFirst', 'TermLinkTranscript') 15
        if ($logcat.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$logcat.StdOut)) {
            [IO.File]::WriteAllText((Join-Path $artifactDirectory 'failed-stage-logcat.txt'),
                [string]$logcat.StdOut, [Text.UTF8Encoding]::new($false))
            $logcatSaved = $true
        }
    }
    catch {}
    if ($null -ne $script:evidence.failure) {
        $script:evidence.failure | Add-Member -NotePropertyName failed_hook_response_saved `
            -NotePropertyValue $hookResponseSaved
        $script:evidence.failure | Add-Member -NotePropertyName failed_stage_logcat_saved `
            -NotePropertyValue $logcatSaved
    }
}

function Save-FrozenOpenFailureEvidence {
    param([Parameter(Mandatory)]$OpenRequest, [Parameter(Mandatory)]$Arm,
        [Parameter(Mandatory)][string]$Marker)
    $capture = [pscustomobject][ordered]@{
        status = 'no_verified_session'
        hook_response_saved = $false
        inspect_saved = $false
        screenshot_saved = $false
    }
    $script:evidence | Add-Member -NotePropertyName failure_capture -NotePropertyValue $capture
    $openResponse = $null
    try {
        $result = Invoke-NativeCapture $script:adbPath `
            @('-s', $serial, 'exec-out', 'run-as', $AndroidPackageName, 'cat', $hookResponsePath) 10
        if ($result.ExitCode -eq 0 -and
            [Text.Encoding]::UTF8.GetByteCount([string]$result.StdOut) -le 1048576) {
            $candidate = [string]$result.StdOut | ConvertFrom-Json -ErrorAction Stop
            Assert-NoSensitiveFields $candidate
            if ([string]$candidate.request_id -ceq [string]$OpenRequest.request_id -and
                [string]$candidate.operation -ceq 'open_session' -and
                $candidate.ok -is [bool]) {
                $failureCode = if ($candidate.PSObject.Properties['failure_code']) {
                    [string]$candidate.failure_code
                } else { $null }
                $sessionId = if ($candidate.PSObject.Properties['session_id']) {
                    [string]$candidate.session_id
                } else { $null }
                $openResponse = [pscustomobject][ordered]@{
                    request_id = [string]$candidate.request_id
                    operation = 'open_session'
                    ok = [bool]$candidate.ok
                    failure_code = if ($failureCode -match '^[A-Z0-9_]{1,64}$') {
                        $failureCode
                    } else { $null }
                    session_id = if ($sessionId -match '^[0-9a-fA-F-]{36}$') {
                        $sessionId
                    } else { $null }
                }
                [IO.File]::WriteAllText((Join-Path $artifactDirectory 'failed-open-hook-response.json'),
                    ($openResponse | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
                $capture.hook_response_saved = $true
            }
        }
    }
    catch {}

    try {
        $inspectRequest = Start-HookOperation 'inspect' @{ live_marker = $Marker }
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            if ($script:sessionProcess.HasExited) {
                $capture.status = 'host_exited_before_capture'
                break
            }
            $result = Invoke-NativeCapture $script:adbPath `
                @('-s', $serial, 'exec-out', 'run-as', $AndroidPackageName, 'cat', $hookResponsePath) 10
            if ($result.ExitCode -eq 0 -and
                [Text.Encoding]::UTF8.GetByteCount([string]$result.StdOut) -le 1048576) {
                $response = $null
                try { $response = [string]$result.StdOut | ConvertFrom-Json -ErrorAction Stop } catch {}
                if ($null -ne $response -and [string]$response.request_id -ceq [string]$inspectRequest.request_id -and
                    [string]$response.operation -ceq 'inspect' -and $response.ok -is [bool] -and $response.ok) {
                    Assert-NoSensitiveFields $response
                    $observation = Get-StateObservation 'failed-open' $response
                    if (-not (Test-FailureSessionIdentity $observation.session_id $Arm)) { break }
                    if ($null -ne $openResponse -and $null -ne $openResponse.session_id -and
                        $openResponse.session_id -cne $observation.session_id) { break }
                    [IO.File]::WriteAllText((Join-Path $artifactDirectory 'failed-open-inspect.json'),
                        ($observation | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
                    $capture.inspect_saved = $true
                    if ($script:sessionProcess.HasExited) {
                        $capture.status = 'host_exited_before_capture'
                        break
                    }
                    $script:evidence.screenshots += Save-Screenshot 'failed-open.png'
                    $capture.screenshot_saved = $true
                    $capture.status = 'captured_unverified_failure'
                    break
                }
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
    }
    catch {}
}

function Assert-InstalledCandidateApk {
    param([Parameter(Mandatory)]$Candidate)
    $pathResult = Invoke-NativeCapture $script:adbPath @('-s', $serial, 'shell', 'pm', 'path', $AndroidPackageName) 20
    $paths = @(([string]$pathResult.StdOut -split '\r?\n') | Where-Object { $_ -match '^package:' })
    Assert-E2E ($pathResult.ExitCode -eq 0 -and $paths.Count -eq 1 -and
        $paths[0] -match '^package:/\S+/base\.apk\s*$') 'ANDROID_INSTALLED_APK_MISSING' `
        'The selected emulator lacks the expected single-base APK package.'
    $installedPath = $paths[0].Substring('package:'.Length).Trim()
    $hashResult = Invoke-NativeCapture $script:adbPath @('-s', $serial, 'shell', 'sha256sum', $installedPath) 20
    $hash = [regex]::Match([string]$hashResult.StdOut, '(?i)^([0-9a-f]{64})\s+\S+\s*$')
    Assert-E2E ($hashResult.ExitCode -eq 0 -and $hash.Success -and
        $hash.Groups[1].Value -ieq $Candidate.sha256) 'ANDROID_INSTALLED_APK_HASH_MISMATCH' `
        'The installed APK SHA-256 differs from the candidate APK.'
    $dump = Invoke-NativeCapture $script:adbPath @('-s', $serial, 'shell', 'dumpsys', 'package', $AndroidPackageName) 20
    $packageBlock = [regex]::Match([string]$dump.StdOut,
        "(?ms)^\s*Package \[$([regex]::Escape($AndroidPackageName))\].*?(?=^\s*Package \[|\z)")
    $versionCode = [regex]::Match($packageBlock.Value, '(?m)^\s*versionCode=(\d+)\b')
    $versionName = [regex]::Match($packageBlock.Value, '(?m)^\s*versionName=(\S+)\s*$')
    Assert-E2E ($dump.ExitCode -eq 0 -and $packageBlock.Success -and $versionCode.Success -and
        $versionName.Success -and $versionCode.Groups[1].Value -ceq $Candidate.version_code -and
        $versionName.Groups[1].Value -ceq $Candidate.version_name) 'ANDROID_INSTALLED_APK_VERSION_MISMATCH' `
        'The installed APK package/version differs from the candidate APK.'
    return $Candidate
}

function Write-Evidence {
    $script:evidence.finished_at_utc = [DateTime]::UtcNow.ToString('o')
    $json = $script:evidence | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($evidencePath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

Assert-E2E (Test-StrictChildPath $artifactRoot $artifactDirectory) 'ARTIFACT_PATH_INVALID' `
    'ArtifactRunId escaped artifacts/tail-first-e2e.'
Assert-E2E (-not (Test-Path -LiteralPath $artifactDirectory)) 'ARTIFACT_RUN_EXISTS' `
    'The selected tail-first E2E artifact run already exists.'
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
New-Item -ItemType Directory -Path $artifactDirectory -ErrorAction Stop | Out-Null

$script:evidence = [pscustomobject][ordered]@{
    schema_version = 1
    run_id = $ArtifactRunId
    status = 'running'
    started_at_utc = [DateTime]::UtcNow.ToString('o')
    finished_at_utc = $null
    stage = $script:stage
    inputs = [pscustomobject][ordered]@{
        target_mode = $TargetMode
        history_bootstrap_mode = $HistoryBootstrapMode
        avd = $AvdName
        emulator_serial = $serial
        acceptance_origin = $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority)
        android_package = $AndroidPackageName
        marker_count = $markerCount
        minimum_open_tail = $minimumOpenTail
        frozen_terminal_state = if ($FrozenGraphicsMode) { $FrozenTerminalState } else { $null }
        history_page_count = $HistoryPageCount
        history_page_size = $HistoryPageSize
    }
    tools = @()
    preflight = [pscustomobject][ordered]@{}
    observations = @()
    assertions = @()
    screenshots = @()
    cleanup = [pscustomobject][ordered]@{
        owned_emulator_stopped = $false
        owned_session_stopped = $false
        network_failure_injected = $false
        android_network_restored = $false
    }
    failure = $null
}

try {
    $script:stage = 'preflight-inputs'
    $script:evidence.stage = $script:stage
    Assert-E2E (-not $ExpectFrozenUnsupported -or $FrozenGraphicsMode) 'E2E_INPUT_INVALID' `
        'Old Host gating requires FrozenGraphicsMode.'
    Assert-E2E ($FrozenGraphicsMode -or -not $PSBoundParameters.ContainsKey('FrozenTerminalState')) `
        'E2E_INPUT_INVALID' 'FrozenTerminalState is only valid with FrozenGraphicsMode.'
    Assert-E2E (-not $FrozenLifecycleAcceptance -or
        ($FrozenGraphicsMode -and $HistoryBootstrapMode -ceq 'SnapshotTranscript' -and
            -not $ExpectFrozenUnsupported)) 'E2E_INPUT_INVALID' `
        'FrozenLifecycleAcceptance requires FrozenGraphicsMode and SnapshotTranscript without ExpectFrozenUnsupported.'
    $resolvedApk = Resolve-RepositoryFile $AndroidApkPath 'ANDROID_APK_MISSING' 'The Android Debug APK'
    $resolvedSessionHost = Resolve-RepositoryFile $SessionHostPath 'SESSION_HOST_MISSING' 'The Windows SessionHost executable'
    $resolvedNativeDisplay = if ($FrozenGraphicsMode) {
        Assert-E2E (-not [string]::IsNullOrWhiteSpace($NativeDisplayPath)) 'NATIVE_DISPLAY_MISSING' `
            'Graphics acceptance requires NativeDisplayPath.'
        Resolve-RepositoryFile $NativeDisplayPath 'NATIVE_DISPLAY_MISSING' 'The Windows native display executable'
    } else { $null }
    $agentControlAssembly = Join-Path (Split-Path -Parent $resolvedSessionHost) 'TermLink.AgentControl.dll'
    $script:stage = 'preflight-tools'
    $script:evidence.stage = $script:stage
    $adbTool = Get-ManifestTool 'android-sdk'
    $dotnetTool = Get-ManifestTool 'dotnet'
    $goTool = Get-ManifestTool 'go'
    $script:adbPath = $adbTool.path
    $script:evidence.tools = @($adbTool, $dotnetTool, $goTool)
    $sdkRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $script:adbPath) '..'))
    $emulatorPath = Join-Path $sdkRoot 'emulator\emulator.exe'
    Assert-E2E (Test-Path -LiteralPath $emulatorPath -PathType Leaf) 'ANDROID_EMULATOR_UNAVAILABLE' `
        'The emulator paired with the D:\Dev Android SDK is unavailable.'

    $script:stage = 'preflight-hook-contract'
    $script:evidence.stage = $script:stage
    $script:evidence.preflight | Add-Member -NotePropertyName hook_contract -NotePropertyValue (Get-ApkHookContract $resolvedApk)

    $script:stage = 'preflight-windows-agent'
    $script:evidence.stage = $script:stage
    Assert-E2E (Test-Path -LiteralPath $agentControlAssembly -PathType Leaf) 'AGENT_CONTROL_ASSEMBLY_MISSING' `
        'TermLink.AgentControl.dll is unavailable beside the Windows SessionHost executable.'
    $expectedOrigin = $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
    $script:evidence.preflight | Add-Member -NotePropertyName windows_agent -NotePropertyValue `
        (Get-WindowsAgentPreflight `
            $agentControlAssembly `
            $AgentControlPipe `
            $expectedOrigin `
            ($HistoryBootstrapMode -ceq 'SnapshotTranscript'))

    if ($TargetMode -ceq 'ManagedAcceptanceCompose') {
        $script:stage = 'preflight-acceptance-stack'
        $script:evidence.stage = $script:stage
        $resolvedDocker = Resolve-Application $DockerPath 'DOCKER_UNAVAILABLE' 'Docker'
        $script:evidence.preflight | Add-Member -NotePropertyName acceptance_services -NotePropertyValue `
            @(Get-AcceptanceStackState $resolvedDocker)
    }
    else {
        $script:stage = 'preflight-external-https'
        $script:evidence.stage = $script:stage
        $externalEvidence = [pscustomobject][ordered]@{
            server_origin = $null
            expected_oidc_issuer = $null
            endpoints = @()
        }
        $script:evidence.preflight | Add-Member -NotePropertyName external_https -NotePropertyValue $externalEvidence
        $null = Get-ExternalHttpsPreflight $AcceptanceOrigin $externalEvidence
    }

    $script:stage = 'preflight-avd'
    $script:evidence.stage = $script:stage
    $avds = @((Invoke-RequiredNative $emulatorPath @('-list-avds') 'AVD_LIST_FAILED' 'Could not list Android virtual devices.') -split '\r?\n' |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
    Assert-E2E ($avds -ccontains $AvdName) 'AVD_MISSING' 'The requested Android virtual device is unavailable.'
    $devices = Invoke-NativeCapture $script:adbPath @('devices') 30
    Assert-E2E ($devices.ExitCode -eq 0) 'ADB_UNAVAILABLE' 'ADB could not inspect connected devices.'
    $avdStartMode = Get-AvdStartMode ([string]$devices.StdOut)
    $script:evidence.preflight | Add-Member -NotePropertyName avd_start_mode -NotePropertyValue $avdStartMode
    if ($FrozenLifecycleAcceptance) {
        $candidateApk = Get-CandidateApkIdentity $resolvedApk $sdkRoot
        $script:evidence.preflight | Add-Member -NotePropertyName candidate_apk -NotePropertyValue $candidateApk
        $script:evidence.preflight | Add-Member -NotePropertyName android_state_reset -NotePropertyValue $false
        if ($avdStartMode -ceq 'warm_existing') {
            $installedApk = Assert-InstalledCandidateApk $candidateApk
            $script:evidence.preflight | Add-Member -NotePropertyName installed_apk -NotePropertyValue $installedApk
        }
    } else {
        $script:evidence.preflight | Add-Member -NotePropertyName avd_cold_start_ready -NotePropertyValue $true
    }
    $script:evidence.preflight | Add-Member -NotePropertyName session_host -NotePropertyValue ([IO.Path]::GetFileName($resolvedSessionHost))

    if ($PreflightOnly) {
        $script:evidence.status = 'preflight-passed'
        Write-Evidence
        Write-Output "Tail-first product E2E preflight passed. Evidence: $evidencePath"
        return
    }

    $script:stage = if ($avdStartMode -ceq 'warm_existing') { 'android-warm-existing' } else { 'android-cold-start' }
    $script:evidence.stage = $script:stage
    if ($avdStartMode -cne 'warm_existing') {
        $emulatorStart = [Diagnostics.ProcessStartInfo]::new()
        $emulatorStart.FileName = $emulatorPath
        $emulatorStart.UseShellExecute = $false
        $emulatorStart.CreateNoWindow = -not $ShowEmulator
        $emulatorStart.WindowStyle = if ($ShowEmulator) {
            [Diagnostics.ProcessWindowStyle]::Normal
        } else {
            [Diagnostics.ProcessWindowStyle]::Hidden
        }
        $emulatorStart.RedirectStandardOutput = $true
        $emulatorStart.RedirectStandardError = $true
        $emulatorArguments = @('-avd', $AvdName, '-port', [string]$EmulatorPort, '-no-snapshot-load', '-no-snapshot-save', '-no-boot-anim')
        if (-not $ShowEmulator) { $emulatorArguments += '-no-window' }
        foreach ($argument in $emulatorArguments) { $emulatorStart.ArgumentList.Add($argument) }
        $script:emulatorProcess = [Diagnostics.Process]::Start($emulatorStart)
        Assert-E2E ($null -ne $script:emulatorProcess) 'AVD_START_FAILED' 'The requested AVD could not be started.'
        $script:emulatorOwned = $true
        $script:emulatorOutputTask = $script:emulatorProcess.StandardOutput.ReadToEndAsync()
        $script:emulatorErrorTask = $script:emulatorProcess.StandardError.ReadToEndAsync()
        $bootDeadline = [DateTime]::UtcNow.AddSeconds($BootTimeoutSeconds)
        $booted = $false
        do {
            if ($script:emulatorProcess.HasExited) { break }
            $state = Invoke-NativeCapture $script:adbPath @('-s', $serial, 'get-state') 5
            if ($state.ExitCode -eq 0 -and ([string]$state.StdOut).Trim() -eq 'device') {
                $boot = Invoke-NativeCapture $script:adbPath @('-s', $serial, 'shell', 'getprop', 'sys.boot_completed') 5
                if ($boot.ExitCode -eq 0 -and ([string]$boot.StdOut).Trim() -eq '1') { $booted = $true; break }
            }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $bootDeadline)
        Assert-E2E $booted 'AVD_BOOT_TIMEOUT' 'The requested AVD did not complete booting in time.'
    }
    $actualAvd = Get-EmulatorAvdName $serial
    Assert-E2E (-not [string]::IsNullOrWhiteSpace($actualAvd)) 'AVD_IDENTITY_FAILED' 'Could not verify the running AVD.'
    Assert-E2E ($actualAvd -eq $AvdName) 'AVD_IDENTITY_MISMATCH' 'ADB connected to a different AVD than requested.'

    if ($avdStartMode -cne 'warm_existing') {
        $null = Invoke-Adb @('install', '-r', '-t', $resolvedApk) 'ANDROID_INSTALL_FAILED' 'The Debug APK could not be installed.' 180
    }
    if ($FrozenLifecycleAcceptance) {
        $installedApk = Assert-InstalledCandidateApk $candidateApk
        if (-not $script:evidence.preflight.PSObject.Properties['installed_apk']) {
            $script:evidence.preflight | Add-Member -NotePropertyName installed_apk -NotePropertyValue $installedApk
        }
        $script:evidence.preflight | Add-Member -NotePropertyName android_cold_state -NotePropertyValue `
            ([pscustomobject][ordered]@{
                history_database_removed = $false
                display_preferences_removed = $false
                oidc_session_preserved = $true
                device_identity_preserved = $true
            })
    } else {
        $null = Invoke-Adb @('shell', 'am', 'force-stop', $AndroidPackageName) 'ANDROID_COLD_START_FAILED' 'The app could not be force-stopped for a cold launch.'
        $null = Invoke-Adb @(
            'shell', 'run-as', $AndroidPackageName, 'rm', '-f',
            'databases/termlink-history.db',
            'databases/termlink-history.db-shm',
            'databases/termlink-history.db-wal',
            'shared_prefs/termlink_product_preferences.xml'
        ) 'ANDROID_COLD_STATE_RESET_FAILED' `
            'The app history database or display preferences could not be reset before launch.'
        $script:evidence.preflight | Add-Member -NotePropertyName android_cold_state -NotePropertyValue `
            ([pscustomobject][ordered]@{
                history_database_removed = $true
                display_preferences_removed = $true
                oidc_session_preserved = $true
                device_identity_preserved = $true
            })
    }
    $null = Invoke-Adb @('shell', 'am', 'start', '-W', '-n', "$AndroidPackageName/$AndroidActivity") `
        $(if ($FrozenLifecycleAcceptance) { 'ANDROID_START_FAILED' } else { 'ANDROID_COLD_START_FAILED' }) `
        'The app could not be opened.'

    $script:stage = 'runtime-hook-preflight'
    $script:evidence.stage = $script:stage
    $probe = Invoke-HookOperation 'probe'
    Assert-JsonProperties $probe @('capabilities', 'account_ready', 'server_origin') 'Debug hook probe response'
    $capabilities = @($probe.capabilities | ForEach-Object { [string]$_ })
    foreach ($operation in $requiredHookOperations) {
        Assert-E2E ($capabilities -ccontains $operation) 'E2E_HOOK_RUNTIME_INCOMPLETE' `
            "The runtime Debug hook lacks capability '$operation'."
    }
    if ($FrozenGraphicsMode) {
        foreach ($operation in @('begin_frozen_history', 'page_frozen_history', 'release_frozen_history')) {
            Assert-E2E ($capabilities -ccontains $operation) 'E2E_HOOK_RUNTIME_INCOMPLETE' `
                "The runtime Debug hook lacks capability '$operation'."
        }
    }
    Assert-E2E ($probe.account_ready -is [bool]) 'E2E_HOOK_RESPONSE_INVALID' `
        'The Debug hook probe account_ready field is not a JSON boolean.'
    Assert-E2E ([bool]$probe.account_ready) 'ANDROID_ACCOUNT_NOT_READY' `
        'The Android Debug app is not signed in to the acceptance account.'
    Assert-E2E ([string]$probe.server_origin -eq $expectedOrigin) 'ANDROID_ORIGIN_MISMATCH' `
        'The Android Debug app is not configured for the requested acceptance origin.'

    $script:stage = 'windows-managed-session'
    $script:evidence.stage = $script:stage
    $liveMarkerDigest = [Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($ArtifactRunId))
    $markerStem = if ($FrozenLifecycleAcceptance) {
        'TLTF32_' + [Convert]::ToHexString($liveMarkerDigest, 0, 4).ToLowerInvariant()
    } else { 'TLTF_' + $ArtifactRunId.Replace('-', '_').Replace('.', '_') }
    $markers = @(1..$markerCount | ForEach-Object { '{0}_{1:D4}' -f $markerStem, $_ })
    Assert-E2E (@($markers | Select-Object -Unique).Count -eq $markerCount) 'MARKER_GENERATION_FAILED' `
        'The Windows marker set is not unique.'
    $firstMarker = $markers[0]
    $lastMarker = $markers[-1]
    # Keep the live marker unique without letting a long ArtifactRunId exceed the
    # authoritative terminal width. ConPTY is allowed to hard-wrap an echoed
    # input line, which would make a long contiguous-string assertion a false
    # negative even though every output byte reached Android.
    $liveMarker = 'TLTF_LIVE_' +
        [Convert]::ToHexString($liveMarkerDigest, 0, 8).ToLowerInvariant()
    $script:liveMarker = $liveMarker
    $frozenWidthMarker = 'TLF_COL32_' +
        [Convert]::ToHexString($liveMarkerDigest, 0, 4).ToLowerInvariant()
    $frozenLiveMarker = 'TLTF_FROZEN_' +
        [Convert]::ToHexString($liveMarkerDigest, 0, 8).ToLowerInvariant()
    $frozenOutputTrigger = Join-Path $artifactDirectory 'frozen-live-output.trigger'
    $frozenFixtureStatusBase64 = [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes($frozenFixtureStatusPath))
    $childScript = if ($HistoryBootstrapMode -ceq 'StreamedChunkPrefix') {
        @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::WriteLine('__FIRST_MARKER__')
1..__MARKER_COUNT__ | ForEach-Object {
    $eventName = 'TLSP_EVENT_{0:D5}' -f $_
    [Console]::Write("$eventName`r")
    [Console]::Out.Flush()
    Start-Sleep -Milliseconds 17
}
[Console]::WriteLine()
[Console]::WriteLine('__LAST_MARKER__')
while ($true) {
    $line = [Console]::ReadLine()
    if ($null -eq $line -or $line -eq '__TERMLINK_TAIL_FIRST_EXIT__') { break }
    [Console]::WriteLine($line)
}
'@.Replace('__FIRST_MARKER__', $firstMarker).
            Replace('__LAST_MARKER__', $lastMarker).
            Replace('__MARKER_COUNT__', [string]$markerCount)
    } else {
        @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$stem = '__MARKER_STEM__'
$padding = ''
if (__FROZEN_LIFECYCLE__) {
    $fixturePath = [Text.Encoding]::UTF8.GetString(
        [Convert]::FromBase64String('__FIXTURE_STATUS_BASE64__'))
    try {
        [Console]::Write([char]27 + '[32$|')
        [Console]::Out.Flush()
        $resizeClock = [Diagnostics.Stopwatch]::StartNew()
        do {
            $columns = __FROZEN_COLUMNS_SOURCE__
            if ($columns -eq 32) { break }
            Start-Sleep -Milliseconds 100
        } while ($resizeClock.Elapsed.TotalSeconds -lt 15)
        if ($columns -ne 32) { throw 'FROZEN_FIXTURE_RESIZE_FAILED' }
        [IO.File]::WriteAllText($fixturePath, [string]$columns, [Text.UTF8Encoding]::new($false))
        $padding = 'X' * ($columns * 20)
    }
    catch {
        [IO.File]::WriteAllText($fixturePath, 'FROZEN_FIXTURE_RESIZE_FAILED',
            [Text.UTF8Encoding]::new($false))
        throw 'FROZEN_FIXTURE_RESIZE_FAILED'
    }
}
1..__MARKER_COUNT__ | ForEach-Object {
    $marker = '{0}_{1:D4}' -f $stem, $_
    if (-not __FROZEN_LIFECYCLE__ -or $_ -lt __MARKER_COUNT__) { $marker += $padding }
    [Console]::WriteLine($marker)
}
if (__FROZEN_LIFECYCLE__) { [Console]::WriteLine('__FROZEN_WIDTH_MARKER__') }
while ($true) {
    $line = [Console]::ReadLine()
    if ($null -eq $line -or $line -eq '__TERMLINK_TAIL_FIRST_EXIT__') { break }
    [Console]::WriteLine($line)
}
'@.Replace('__MARKER_STEM__', $markerStem).
            Replace('__FROZEN_LIFECYCLE__', $(if ($FrozenLifecycleAcceptance) { '$true' } else { '$false' })).
            Replace('__FROZEN_COLUMNS_SOURCE__', '[Console]::WindowWidth').
            Replace('__FIXTURE_STATUS_BASE64__', $frozenFixtureStatusBase64).
            Replace('__FROZEN_WIDTH_MARKER__', $frozenWidthMarker).
            Replace('__MARKER_COUNT__', [string]$markerCount)
    }
    if ($FrozenGraphicsMode) {
        # Only the runner's owned PowerShell child writes to its ConPTY. The
        # native-display SessionHost does not read its redirected stdin.
        $triggerBase64 = [Convert]::ToBase64String(
            [Text.Encoding]::UTF8.GetBytes($frozenOutputTrigger))
        $frozenOutputJob = @'
$frozenOutputTrigger = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__TRIGGER_BASE64__'))
$null = Start-ThreadJob -ArgumentList $frozenOutputTrigger, '__FROZEN_MARKER__' -ScriptBlock {
    param($trigger, $marker)
    $deadline = [DateTime]::UtcNow.AddMinutes(9)
    while (-not (Test-Path -LiteralPath $trigger) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (Test-Path -LiteralPath $trigger) {
        [Console]::WriteLine($marker)
        [Console]::Out.Flush()
    }
}
'@.Replace('__TRIGGER_BASE64__', $triggerBase64).
            Replace('__FROZEN_MARKER__', $frozenLiveMarker)
        $childScript = $childScript.Replace('while ($true) {',
            $frozenOutputJob + [Environment]::NewLine + 'while ($true) {')
    }
    $encodedChildScript = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childScript))
    $childCommand = '"{0}" -NoLogo -NoProfile -NonInteractive -EncodedCommand {1}' -f (Join-Path $PSHOME 'pwsh.exe'), $encodedChildScript
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($childCommand))
    $sessionStart = [Diagnostics.ProcessStartInfo]::new()
    $sessionStart.FileName = $resolvedSessionHost
    $sessionStart.UseShellExecute = $false
    $sessionStart.CreateNoWindow = $true
    $sessionStart.RedirectStandardInput = $true
    $sessionStart.RedirectStandardOutput = $true
    $sessionStart.RedirectStandardError = $true
    $sessionStart.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $sessionStart.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $sessionStart.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $sessionTimeoutMs = if ($FrozenLifecycleAcceptance) { '900000' } else { '600000' }
    $sessionArguments = @('--command-base64', $encodedCommand, '--managed-shell', 'powershell-7',
        '--timeout-ms', $sessionTimeoutMs)
    if ($FrozenGraphicsMode) {
        $sessionArguments += @('--native-display', $resolvedNativeDisplay,
            '--terminal-state', $FrozenTerminalState)
    }
    if (-not [string]::IsNullOrWhiteSpace($AgentControlPipe)) { $sessionArguments += @('--registry-pipe', $AgentControlPipe) }
    foreach ($argument in $sessionArguments) { $sessionStart.ArgumentList.Add($argument) }

    $script:stage = 'tail-first-session-arm'
    $script:evidence.stage = $script:stage
    $openSessionArguments = @{
        first_marker = $firstMarker
        last_marker = $lastMarker
        marker_count = [string]$markerCount
        minimum_tail = [string]$minimumOpenTail
        history_bootstrap_mode = $HistoryBootstrapMode
    }
    if ($FrozenLifecycleAcceptance) {
        # Frozen readiness depends only on last_marker. The live marker sits
        # at the tail of live output that has frequently scrolled out of the
        # visible viewport by the time the surface settles, so requiring it
        # here turned r398/r407/r408/r419/r421 into SURFACE_TIMEOUT even with
        # transport CONNECTED and subscriber ready. The receiver treats a
        # missing live_marker as optional (frozenLifecycleOpenSessionArguments
        # AreValid accepts both shapes).
        $openSessionArguments.frozen_lifecycle_acceptance = 'true'
    }
    $openSessionRequest = Start-HookOperation 'open_session' $openSessionArguments
    $script:openSessionRequest = $openSessionRequest
    $sessionArm = Wait-SessionArm $openSessionRequest
    $sessionArm | Add-Member -NotePropertyName session_host_started_at_utc -NotePropertyValue ([DateTime]::UtcNow.ToString('o'))
    $sessionArm | Add-Member -NotePropertyName selected_session_id -NotePropertyValue $null
    $sessionArm | Add-Member -NotePropertyName selected_session_observed_at_utc -NotePropertyValue $null
    $script:evidence | Add-Member -NotePropertyName session_arm -NotePropertyValue $sessionArm

    $script:stage = 'windows-managed-session'
    $script:evidence.stage = $script:stage
    $script:sessionProcess = [Diagnostics.Process]::Start($sessionStart)
    Assert-E2E ($null -ne $script:sessionProcess) 'MANAGED_SESSION_START_FAILED' 'The Windows managed ConPTY session could not be started.'
    $script:sessionOutputTask = $script:sessionProcess.StandardOutput.ReadToEndAsync()
    $script:sessionErrorTask = $script:sessionProcess.StandardError.ReadToEndAsync()

    $script:stage = 'tail-first-open'
    $script:evidence.stage = $script:stage
    $initial = Wait-HookOperation $openSessionRequest
    $initialObservation = Get-StateObservation 'initial-open' $initial
    $script:evidence.observations += $initialObservation
    $script:evidence.session_arm.selected_session_id = $initialObservation.session_id
    $script:evidence.session_arm.selected_session_observed_at_utc = [DateTime]::UtcNow.ToString('o')
    Add-Assertion 'selected-session-not-in-armed-baseline' 'new UUID' $initialObservation.session_id `
        ($script:evidence.session_arm.baseline_session_ids -cnotcontains $initialObservation.session_id)
    # Receipt precedes startup on the host; Android's wall clock can be ahead of Windows.
    Add-Assertion 'session-host-started-after-baseline-arm' ">=$($script:evidence.session_arm.arm_received_at_utc)" `
        $script:evidence.session_arm.session_host_started_at_utc `
        ([DateTimeOffset]::Parse($script:evidence.session_arm.session_host_started_at_utc) -ge
            [DateTimeOffset]::Parse($script:evidence.session_arm.arm_received_at_utc))
    if ($FrozenLifecycleAcceptance) {
        Assert-E2E (Test-Path -LiteralPath $frozenFixtureStatusPath -PathType Leaf) `
            'FROZEN_FIXTURE_RESIZE_FAILED' 'The ConPTY child did not record its observed terminal width.'
        $fixtureColumns = (Get-Content -LiteralPath $frozenFixtureStatusPath -Raw).Trim()
        Add-Assertion 'frozen-fixture-child-observed-32-columns' '32' $fixtureColumns `
            ($fixtureColumns -ceq '32')
        $fixtureObservation = Get-StateObservation 'frozen-fixture-width' (
            Invoke-HookOperation 'inspect' @{ live_marker = $frozenWidthMarker })
        Add-Assertion 'frozen-fixture-android-observed-32-columns' '32' `
            $fixtureObservation.terminal_columns ($fixtureObservation.terminal_columns -eq 32)
        Add-Assertion 'frozen-fixture-width-marker-visible' 'true' `
            $fixtureObservation.live_marker_visible $fixtureObservation.live_marker_visible
        $script:evidence.observations += $fixtureObservation
    }
    if ($FrozenGraphicsMode) {
        $script:stage = 'frozen-history-begin'
        $script:evidence.stage = $script:stage
        $begin = Invoke-HookOperation 'begin_frozen_history' -AcceptFailure:$ExpectFrozenUnsupported
        if ($ExpectFrozenUnsupported) {
            Add-Assertion 'old-host-rejects-frozen-history' 'UNSUPPORTED_FORMAT' $begin.failure_code `
                (-not $begin.ok -and $begin.failure_code -ceq 'UNSUPPORTED_FORMAT')
            $oldHostReleased = Invoke-HookOperation 'release_frozen_history'
            Add-Assertion 'old-host-reject-clears-frozen-state' 'false/0' `
                "$($oldHostReleased.frozen_active)/$($oldHostReleased.frozen_page_count)" `
                (-not $oldHostReleased.frozen_active -and $oldHostReleased.frozen_page_count -eq 0)
            $script:evidence.observations += $oldHostReleased
            $script:stage = 'frozen-unsupported-live-flow'
            $script:evidence.stage = $script:stage
            $null = Invoke-HookOperation 'show_ime'
            $null = Invoke-Adb @('shell', 'input', 'text', $liveMarker) `
                'FROZEN_LIVE_INPUT_FAILED' 'The Android keyboard could not enter the live marker after old Host rejection.'
            $null = Invoke-Adb @('shell', 'input', 'keyevent', 'KEYCODE_ENTER') `
                'FROZEN_LIVE_INPUT_FAILED' 'The Android keyboard could not submit the live marker after old Host rejection.'
            $oldHostLiveDeadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
            $oldHostLive = $null
            do {
                $candidate = Get-StateObservation 'frozen-unsupported-live' (
                    Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker })
                if ($candidate.session_id -ceq $initialObservation.session_id -and
                    $candidate.live_ack -gt [uint64]$oldHostReleased.live_ack -and
                    $candidate.live_marker_visible) {
                    $oldHostLive = $candidate
                    break
                }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $oldHostLiveDeadline)
            Assert-E2E ($null -ne $oldHostLive) 'FROZEN_UNSUPPORTED_CLOSED_LIVE_CLIENT' `
                'Windows output did not reach Android after old Host rejected frozen history.'
            Add-Assertion 'old-host-reject-live-continues' 'ACK advanced and marker visible' `
                "$($oldHostLive.live_ack)/$($oldHostLive.live_marker_visible)" `
                ($oldHostLive.live_ack -gt [uint64]$oldHostReleased.live_ack -and
                    $oldHostLive.live_marker_visible)
            $script:evidence.observations += $oldHostLive
            $script:evidence.screenshots += Save-Screenshot 'frozen-unsupported-live-continues.png'
        } else {
            Assert-JsonProperties $begin @('frozen_active', 'frozen_render_ready',
                'frozen_capture_id', 'frozen_revision', 'frozen_snapshot_sequence_s',
                'frozen_page_count', 'frozen_first_line_id', 'frozen_last_line_id',
                'frozen_next_before_line_id', 'frozen_has_more_before', 'live_ack') `
                'Frozen BEGIN product response'
            $script:frozenCaptureActive = [bool]$begin.frozen_active
            Add-Assertion 'frozen-begin-one-page' '1 ready page' $begin.frozen_page_count `
                ($begin.frozen_active -and $begin.frozen_render_ready -and $begin.frozen_page_count -eq 1)
            Add-Assertion 'frozen-begin-has-older-lines' 'true' $begin.frozen_has_more_before `
                ([bool]$begin.frozen_has_more_before)
            Add-Assertion 'frozen-begin-ack-unchanged' $initialObservation.live_ack $begin.live_ack `
                ($begin.live_ack -eq $initialObservation.live_ack)
            Add-Assertion 'frozen-begin-capture-valid' 'positive' $begin.frozen_capture_id `
                ([uint64]$begin.frozen_capture_id -gt 0 -and
                    [uint64]$begin.frozen_revision -eq [uint64]$begin.frozen_snapshot_sequence_s + 1)
            if ($FrozenLifecycleAcceptance) {
                Assert-E2E ($begin.PSObject.Properties.Name -contains 'frozen_head_truncated' -and
                    $begin.frozen_head_truncated -is [bool]) 'FROZEN_HEAD_FLAG_INVALID' `
                    'Frozen BEGIN did not report a JSON boolean head-truncation flag.'
                $seenFrozenAnchors = [Collections.Generic.HashSet[uint64]]::new()
                Assert-E2E ([uint64]$begin.frozen_next_before_line_id -gt 0 -and
                    $seenFrozenAnchors.Add([uint64]$begin.frozen_next_before_line_id)) 'FROZEN_BEGIN_ANCHOR_INVALID' `
                    'Frozen BEGIN did not provide a nonzero first anchor.'
                $frozenPageCalls = 0
            }
            $script:evidence.observations += $begin
            $script:evidence.screenshots += Save-Screenshot 'frozen-01-begin.png'
            $previous = $begin
            for ($pageNumber = 1; $pageNumber -le 2; $pageNumber++) {
                $script:stage = "frozen-history-page-$pageNumber"
                $script:evidence.stage = $script:stage
                $page = Invoke-HookOperation 'page_frozen_history'
                if ($FrozenLifecycleAcceptance) {
                    $frozenPageCalls++
                    Assert-E2E (Test-FrozenPageTransition $begin $previous $page $seenFrozenAnchors) `
                        'FROZEN_PAGE_TRANSITION_INVALID' "Frozen page $pageNumber lost its capture, render, or unique anchor."
                }
                Add-Assertion "frozen-page-$pageNumber-retains-capture" $begin.frozen_capture_id `
                    $page.frozen_capture_id ($page.frozen_capture_id -eq $begin.frozen_capture_id)
                Add-Assertion "frozen-page-$pageNumber-retains-revision" $begin.frozen_revision `
                    $page.frozen_revision ($page.frozen_revision -eq $begin.frozen_revision)
                Add-Assertion "frozen-page-$pageNumber-retains-snapshot" $begin.frozen_snapshot_sequence_s `
                    $page.frozen_snapshot_sequence_s `
                    ($page.frozen_snapshot_sequence_s -eq $begin.frozen_snapshot_sequence_s)
                Add-Assertion "frozen-page-$pageNumber-advances-anchor" 'different nonzero anchor' `
                    $page.frozen_next_before_line_id `
                    ($page.frozen_next_before_line_id -ne $previous.frozen_next_before_line_id -and
                        [uint64]$page.frozen_next_before_line_id -gt 0)
                Add-Assertion "frozen-page-$pageNumber-renders" 'ready two-page window' `
                    $page.frozen_page_count `
                    ($page.frozen_active -and $page.frozen_render_ready -and $page.frozen_page_count -eq 2)
                Add-Assertion "frozen-page-$pageNumber-ack-unchanged" $begin.live_ack $page.live_ack `
                    ($page.live_ack -eq $begin.live_ack)
                $script:evidence.observations += $page
                $script:evidence.screenshots += Save-Screenshot ("frozen-0{0}-page.png" -f ($pageNumber + 1))
                $previous = $page
            }
            $script:stage = 'frozen-history-concurrent-live-output'
            $script:evidence.stage = $script:stage
            $frozenBeforeLive = Get-StateObservation 'frozen-before-live' (
                Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker })
            $script:evidence.observations += $frozenBeforeLive
            [IO.File]::WriteAllText($frozenOutputTrigger, $frozenLiveMarker, [Text.UTF8Encoding]::new($false))
            $frozenLiveDeadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
            $frozenLive = $null
            do {
                $candidate = Get-StateObservation 'frozen-concurrent-live' (
                    Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker })
                if ($candidate.session_id -ceq $initialObservation.session_id -and
                    $candidate.live_ack -gt $frozenBeforeLive.live_ack -and
                    $candidate.visible_text_sha256 -cne $frozenBeforeLive.visible_text_sha256) {
                    $frozenLive = $candidate
                    break
                }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $frozenLiveDeadline)
            Assert-E2E ($null -ne $frozenLive) 'FROZEN_LIVE_ACK_DID_NOT_ADVANCE' `
                'Windows output did not reach Android through Relay while frozen history was open.'
            Add-Assertion 'frozen-live-output-ack-advanced' ">$($frozenBeforeLive.live_ack)" $frozenLive.live_ack `
                ($frozenLive.live_ack -gt $frozenBeforeLive.live_ack)
            Add-Assertion 'frozen-live-output-changed-live-text' 'different live text hash' `
                $frozenLive.visible_text_sha256 `
                ($frozenLive.visible_text_sha256 -cne $frozenBeforeLive.visible_text_sha256)
            $script:evidence.observations += $frozenLive
            $afterLivePage = Invoke-HookOperation 'page_frozen_history'
            if ($FrozenLifecycleAcceptance) {
                $frozenPageCalls++
                Assert-E2E (Test-FrozenPageTransition $begin $previous $afterLivePage $seenFrozenAnchors) `
                    'FROZEN_PAGE_TRANSITION_INVALID' 'The concurrent-output PAGE lost its capture, render, or unique anchor.'
            }
            Add-Assertion 'frozen-live-output-page-still-frozen' 'active and rendered' `
                "$($afterLivePage.frozen_active)/$($afterLivePage.frozen_render_ready)" `
                ($afterLivePage.frozen_active -and $afterLivePage.frozen_render_ready)
            Add-Assertion 'frozen-live-output-retains-capture' $begin.frozen_capture_id `
                $afterLivePage.frozen_capture_id ($afterLivePage.frozen_capture_id -eq $begin.frozen_capture_id)
            Add-Assertion 'frozen-live-output-retains-revision' $begin.frozen_revision `
                $afterLivePage.frozen_revision ($afterLivePage.frozen_revision -eq $begin.frozen_revision)
            Add-Assertion 'frozen-live-output-retains-snapshot' $begin.frozen_snapshot_sequence_s `
                $afterLivePage.frozen_snapshot_sequence_s `
                ($afterLivePage.frozen_snapshot_sequence_s -eq $begin.frozen_snapshot_sequence_s)
            Add-Assertion 'frozen-live-output-continues-page' 'different nonzero anchor' `
                $afterLivePage.frozen_next_before_line_id `
                ($afterLivePage.frozen_next_before_line_id -ne $previous.frozen_next_before_line_id -and
                    [uint64]$afterLivePage.frozen_next_before_line_id -gt 0)
            Add-Assertion 'frozen-live-output-page-keeps-ack' ">=$($frozenLive.live_ack)" `
                $afterLivePage.live_ack ($afterLivePage.live_ack -ge $frozenLive.live_ack)
            $script:evidence.observations += $afterLivePage
            $script:evidence.screenshots += Save-Screenshot 'frozen-04-page-after-live.png'

            $script:stage = 'frozen-history-relay-reconnect'
            $script:evidence.stage = $script:stage
            try {
                Set-AndroidAirplaneMode $true
                $script:evidence.cleanup.network_failure_injected = $true
                $offlineDeadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
                $frozenOffline = $null
                do {
                    $candidate = Get-StateObservation 'frozen-relay-offline' (
                        Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker })
                    if ($candidate.realtime_network_lost_count -gt $frozenLive.realtime_network_lost_count -and
                        $candidate.realtime_connected_generation -eq 0) {
                        $frozenOffline = $candidate
                        break
                    }
                    Start-Sleep -Milliseconds 250
                } while ([DateTime]::UtcNow -lt $offlineDeadline)
                Assert-E2E ($null -ne $frozenOffline) 'FROZEN_RELAY_DISCONNECT_NOT_OBSERVED' `
                    'Android did not observe Relay disconnection while frozen history was open.'
                $script:evidence.observations += $frozenOffline
                Add-Assertion 'frozen-relay-disconnected' 'network loss and generation 0' `
                    "$($frozenOffline.realtime_network_lost_count)/$($frozenOffline.realtime_connected_generation)" `
                    ($frozenOffline.realtime_network_lost_count -gt $frozenLive.realtime_network_lost_count -and
                        $frozenOffline.realtime_connected_generation -eq 0)
            }
            finally {
                if ($script:airplaneModeEnabled) {
                    Set-AndroidAirplaneMode $false
                    $script:evidence.cleanup.android_network_restored = $true
                }
            }
            $onlineDeadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
            $frozenOnline = $null
            do {
                $candidate = Get-StateObservation 'frozen-relay-online' (
                    Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker })
                if (Test-RelaySubscriptionRecovered $frozenLive $candidate) {
                    $frozenOnline = $candidate
                    break
                }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $onlineDeadline)
            Assert-E2E ($null -ne $frozenOnline) 'FROZEN_RELAY_RECONNECT_NOT_OBSERVED' `
                'Android did not restore its Relay subscription while frozen history was open.'
            $script:evidence.observations += $frozenOnline
            Add-Assertion 'frozen-relay-successor-subscribed' '>previous generation and ready' `
                "$($frozenOnline.realtime_connected_generation)/$($frozenOnline.realtime_subscriber_ready)" `
                ($frozenOnline.realtime_connected_generation -gt $frozenLive.realtime_connected_generation -and
                    $frozenOnline.realtime_subscriber_ready)

            if ($FrozenLifecycleAcceptance) {
                $script:stage = 'frozen-history-page-after-reconnect'
                $script:evidence.stage = $script:stage
                $frozenPageCalls++
                $reconnectedPage = Invoke-HookOperation 'page_frozen_history'
                Assert-E2E (Test-FrozenPageTransition $begin $afterLivePage $reconnectedPage $seenFrozenAnchors) `
                    'FROZEN_RECONNECT_PAGE_INVALID' `
                    'The original Frozen capture did not PAGE and render after Relay reconnection.'
                Add-Assertion 'frozen-reconnect-page-same-capture' $begin.frozen_capture_id `
                    $reconnectedPage.frozen_capture_id `
                    ($reconnectedPage.frozen_capture_id -eq $begin.frozen_capture_id)
                $script:evidence.observations += $reconnectedPage
                $previous = $reconnectedPage
                $script:stage = 'frozen-history-oldest-boundary'
                $script:evidence.stage = $script:stage
                while ($previous.frozen_has_more_before -and $frozenPageCalls -lt 256) {
                    $frozenPageCalls++
                    $page = Invoke-HookOperation 'page_frozen_history'
                    Assert-E2E (Test-FrozenPageTransition $begin $previous $page $seenFrozenAnchors) `
                        'FROZEN_PAGE_TRANSITION_INVALID' `
                        "Frozen PAGE $frozenPageCalls lost its capture, render, or unique anchor."
                    $script:evidence.observations += $page
                    $previous = $page
                }
                Add-Assertion 'frozen-oldest-boundary-reached' 'has_more=false within 256 PAGEs' `
                    "$($previous.frozen_has_more_before)/$frozenPageCalls" `
                    ($frozenPageCalls -le 256 -and $previous.frozen_has_more_before -is [bool] -and
                        -not $previous.frozen_has_more_before)
                Add-Assertion 'frozen-oldest-head-truncated' 'true' $previous.frozen_head_truncated `
                    ($previous.frozen_head_truncated -is [bool] -and $previous.frozen_head_truncated)
            }

            $script:stage = 'frozen-history-release'
            $script:evidence.stage = $script:stage
            $released = Invoke-HookOperation 'release_frozen_history'
            $script:frozenCaptureActive = $false
            Add-Assertion 'frozen-release-clears-overlay' 'false/0' `
                "$($released.frozen_active)/$($released.frozen_page_count)" `
                (-not $released.frozen_active -and $released.frozen_page_count -eq 0)
            Add-Assertion 'frozen-release-preserves-ack' ">=$($frozenLive.live_ack)" $released.live_ack `
                ($released.live_ack -ge $frozenLive.live_ack)
            $script:evidence.observations += $released
            $script:evidence.screenshots += Save-Screenshot 'frozen-05-released.png'
            if ($FrozenLifecycleAcceptance) {
                $script:stage = 'frozen-history-ttl-begin'
                $script:evidence.stage = $script:stage
                $ttlBegin = Invoke-HookOperation 'begin_frozen_history'
                $script:frozenCaptureActive = [bool]$ttlBegin.frozen_active
                Add-Assertion 'frozen-ttl-new-capture' 'new ready capture with older pages' `
                    $ttlBegin.frozen_capture_id `
                    ($ttlBegin.frozen_active -and $ttlBegin.frozen_render_ready -and
                        $ttlBegin.frozen_capture_id -ne $begin.frozen_capture_id -and
                        $ttlBegin.frozen_has_more_before)
                $script:evidence.observations += $ttlBegin
                $ttlClock = [Diagnostics.Stopwatch]::StartNew()
                Wait-FrozenCaptureAge $ttlClock 240 'Frozen TTL first PAGE'
                $script:stage = 'frozen-history-ttl-page-before-expiry'
                $script:evidence.stage = $script:stage
                $ttlPage = Invoke-HookOperation 'page_frozen_history'
                $ttlFirstPageCompletedSeconds = $ttlClock.Elapsed.TotalSeconds
                $ttlSeenAnchors = [Collections.Generic.HashSet[uint64]]::new()
                $null = $ttlSeenAnchors.Add([uint64]$ttlBegin.frozen_next_before_line_id)
                Add-Assertion 'frozen-ttl-first-page-before-deadline' '<295 seconds and rendered' `
                    $ttlFirstPageCompletedSeconds `
                    ($ttlFirstPageCompletedSeconds -lt 295 -and
                        (Test-FrozenPageTransition $ttlBegin $ttlBegin $ttlPage $ttlSeenAnchors))
                $script:evidence.observations += $ttlPage
                Wait-FrozenCaptureAge $ttlClock 305 'Frozen TTL stale PAGE'
                $script:stage = 'frozen-history-ttl-stale'
                $script:evidence.stage = $script:stage
                $ttlStale = Invoke-HookOperation 'page_frozen_history' -AcceptFailure
                Add-Assertion 'frozen-ttl-page-does-not-renew-lease' 'STALE_CAPTURE' $ttlStale.failure_code `
                    (-not $ttlStale.ok -and $ttlStale.failure_code -ceq 'STALE_CAPTURE')
                $script:evidence.observations += $ttlStale
                $ttlReleased = Invoke-HookOperation 'release_frozen_history'
                $script:frozenCaptureActive = $false
                Add-Assertion 'frozen-ttl-release-clears-overlay' 'false/0' `
                    "$($ttlReleased.frozen_active)/$($ttlReleased.frozen_page_count)" `
                    (-not $ttlReleased.frozen_active -and $ttlReleased.frozen_page_count -eq 0)
                $script:evidence.observations += $ttlReleased
            }
            $rebrowse = Invoke-HookOperation 'begin_frozen_history'
            Add-Assertion 'frozen-reconnect-new-capture' 'different ready capture' $rebrowse.frozen_capture_id `
                ($rebrowse.frozen_render_ready -and $rebrowse.frozen_capture_id -ne $begin.frozen_capture_id)
            $script:frozenCaptureActive = [bool]$rebrowse.frozen_active
            $script:evidence.observations += $rebrowse
            $script:evidence.screenshots += Save-Screenshot 'frozen-06-rebrowse.png'
            if ($FrozenLifecycleAcceptance) {
                $rebrowsePage = Invoke-HookOperation 'page_frozen_history'
                $rebrowseAnchors = [Collections.Generic.HashSet[uint64]]::new()
                $null = $rebrowseAnchors.Add([uint64]$rebrowse.frozen_next_before_line_id)
                Add-Assertion 'frozen-expired-slot-reusable' 'new capture can PAGE' `
                    $rebrowsePage.frozen_capture_id `
                    (Test-FrozenPageTransition $rebrowse $rebrowse $rebrowsePage $rebrowseAnchors)
                $script:evidence.observations += $rebrowsePage
            }
            $rebrowseReleased = Invoke-HookOperation 'release_frozen_history'
            $script:frozenCaptureActive = $false
            Add-Assertion 'frozen-rebrowse-release-clears-overlay' 'false/0' `
                "$($rebrowseReleased.frozen_active)/$($rebrowseReleased.frozen_page_count)" `
                (-not $rebrowseReleased.frozen_active -and $rebrowseReleased.frozen_page_count -eq 0)
            $script:evidence.observations += $rebrowseReleased
            $script:stage = 'frozen-release-live-flow'
            $script:evidence.stage = $script:stage
            # Native display mode reads its window pipe, so verify the Android input path instead of unused host stdin.
            $null = Invoke-HookOperation 'show_ime'
            $null = Invoke-Adb @('shell', 'input', 'text', $liveMarker) `
                'FROZEN_LIVE_INPUT_FAILED' 'The Android keyboard could not enter the live marker.'
            $null = Invoke-Adb @('shell', 'input', 'keyevent', 'KEYCODE_ENTER') `
                'FROZEN_LIVE_INPUT_FAILED' 'The Android keyboard could not submit the live marker.'
            $deadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
            $liveAfterRelease = $null
            do {
                $candidate = Get-StateObservation 'frozen-release-live' (
                    Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker })
                if ($candidate.session_id -ceq $initialObservation.session_id -and
                    $candidate.live_ack -gt $rebrowseReleased.live_ack -and $candidate.live_marker_visible) {
                    $liveAfterRelease = $candidate
                    break
                }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)
            Assert-E2E ($null -ne $liveAfterRelease) 'FROZEN_RELEASE_CLOSED_LIVE_CLIENT' `
                'Live output and durable ACK did not continue after releasing frozen history.'
            $script:evidence.observations += $liveAfterRelease
            Add-Assertion 'frozen-release-live-output-continues' ">$($rebrowseReleased.live_ack) and visible marker" `
                $liveAfterRelease.live_ack ($liveAfterRelease.live_ack -gt $rebrowseReleased.live_ack -and
                    $liveAfterRelease.live_marker_visible)
            $script:evidence.screenshots += Save-Screenshot 'frozen-07-live-continues.png'
        }
    } else {
    if ($HistoryBootstrapMode -ceq 'StreamedChunkPrefix') {
        Add-Assertion 'streamed-prefix-snapshot-count' '0' $initialObservation.snapshot_count `
            ($initialObservation.snapshot_count -eq 0)
        Add-Assertion 'streamed-prefix-transcript-count' '0' $initialObservation.transcript_count `
            ($initialObservation.transcript_count -eq 0)
        Add-Assertion 'streamed-prefix-chunk-count-positive' '>0' $initialObservation.chunk_count `
            ($initialObservation.chunk_count -gt 0)
        Add-Assertion 'streamed-prefix-snapshot-sequence-zero' '0' $initialObservation.snapshot_sequence_s `
            ($initialObservation.snapshot_sequence_s -eq 0)
        Add-Assertion 'streamed-prefix-bootstrap-source' 'NATIVE_FULL_PREFIX' $initialObservation.bootstrap_source `
            ($initialObservation.bootstrap_source -ceq 'NATIVE_FULL_PREFIX')
        Add-Assertion 'streamed-prefix-ack-exceeds-4096' ">=$minimumOpenTail" $initialObservation.live_ack `
            ($initialObservation.live_ack -ge $minimumOpenTail)
        Add-Assertion 'streamed-prefix-history-overlay-hidden' 'false' $initialObservation.history_overlay `
            (-not $initialObservation.history_overlay)
        Add-Assertion 'streamed-prefix-first-marker-visible' 'true' $initialObservation.first_marker_visible `
            $initialObservation.first_marker_visible
        Add-Assertion 'streamed-prefix-last-marker-visible' 'true' $initialObservation.last_marker_visible `
            $initialObservation.last_marker_visible
        $script:evidence.screenshots += Save-Screenshot '01-streamed-prefix-restored.png'

        $fixedSessionId = $initialObservation.session_id
        $fixedSnapshot = [uint64]0
        $fixedAck = $initialObservation.live_ack
        $fixedColumns = $initialObservation.terminal_columns
        $fixedRows = $initialObservation.terminal_rows
        $script:stage = 'streamed-prefix-live-flow'
        $script:evidence.stage = $script:stage
        $script:sessionProcess.StandardInput.WriteLine($liveMarker)
        $script:sessionProcess.StandardInput.Flush()
        $liveDeadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
        $closedObservation = $null
        $lastLiveCandidate = $null
        do {
            $live = Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker }
            $candidate = Get-StateObservation 'streamed-prefix-live' $live
            $lastLiveCandidate = $candidate
            if (-not $candidate.history_overlay -and $candidate.live_ack -gt $fixedAck -and
                $candidate.live_marker_visible) {
                $closedObservation = $candidate
                break
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $liveDeadline)
        if ($null -eq $closedObservation -and $null -ne $lastLiveCandidate) {
            $lastLiveCandidate.phase = 'streamed-prefix-live-timeout'
            $script:evidence.observations += $lastLiveCandidate
        }
        Assert-E2E ($null -ne $closedObservation) 'LIVE_ACK_DID_NOT_ADVANCE' `
            'Live ACK did not advance after the streamed Chunk-prefix restore.'
        $script:evidence.observations += $closedObservation
        Add-Assertion 'streamed-prefix-live-session-fixed' $fixedSessionId $closedObservation.session_id `
            ($closedObservation.session_id -ceq $fixedSessionId)
        Add-Assertion 'streamed-prefix-live-bootstrap-source-fixed' 'NATIVE_FULL_PREFIX' `
            $closedObservation.bootstrap_source ($closedObservation.bootstrap_source -ceq 'NATIVE_FULL_PREFIX')
        Add-Assertion 'streamed-prefix-live-snapshot-count-zero' '0' $closedObservation.snapshot_count `
            ($closedObservation.snapshot_count -eq 0)
        Add-Assertion 'streamed-prefix-live-transcript-count-zero' '0' $closedObservation.transcript_count `
            ($closedObservation.transcript_count -eq 0)
        Add-Assertion 'streamed-prefix-live-ack-advanced' ">$fixedAck" $closedObservation.live_ack `
            ($closedObservation.live_ack -gt $fixedAck)
        Add-Assertion 'streamed-prefix-live-marker-visible' 'true' $closedObservation.live_marker_visible `
            $closedObservation.live_marker_visible
        Add-Assertion 'streamed-prefix-live-grid-columns-fixed' ([string]$fixedColumns) `
            $closedObservation.terminal_columns ($closedObservation.terminal_columns -eq $fixedColumns)
        Add-Assertion 'streamed-prefix-live-grid-rows-fixed' ([string]$fixedRows) `
            $closedObservation.terminal_rows ($closedObservation.terminal_rows -eq $fixedRows)
        $script:evidence.screenshots += Save-Screenshot '02-streamed-prefix-live.png'
    }
    else {
    Add-Assertion 'initial-snapshot-count' '1' $initialObservation.snapshot_count ($initialObservation.snapshot_count -eq 1)
    Add-Assertion 'initial-transcript-count' '0' $initialObservation.transcript_count ($initialObservation.transcript_count -eq 0)
    Add-Assertion 'initial-chunk-count' '0' $initialObservation.chunk_count ($initialObservation.chunk_count -eq 0)
    Add-Assertion 'initial-snapshot-sequence-s-positive' '>0' $initialObservation.snapshot_sequence_s `
        ($initialObservation.snapshot_sequence_s -gt 0)
    Add-Assertion 'initial-ack-covers-snapshot' ">=$($initialObservation.snapshot_sequence_s)" $initialObservation.live_ack `
        ($initialObservation.live_ack -ge $initialObservation.snapshot_sequence_s)
    Add-Assertion 'initial-history-overlay-hidden' 'false' $initialObservation.history_overlay (-not $initialObservation.history_overlay)
    Add-Assertion 'tail-last-marker-visible' 'true' $initialObservation.last_marker_visible $initialObservation.last_marker_visible
    Add-Assertion 'tl-h115-line-5200-visible' 'true' $initialObservation.last_marker_visible `
        $initialObservation.last_marker_visible
    Add-Assertion 'tail-first-marker-not-visible' 'false' $initialObservation.first_marker_visible (-not $initialObservation.first_marker_visible)
    $script:evidence.screenshots += Save-Screenshot '01-initial-tail.png'

    $fixedSessionId = $initialObservation.session_id
    $initialSnapshot = $initialObservation.snapshot_sequence_s
    $fixedSnapshot = $initialSnapshot
    $fixedAck = $initialObservation.live_ack
    $fixedColumns = $initialObservation.terminal_columns
    $fixedRows = $initialObservation.terminal_rows
    $seenViewportSignatures = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $null = $seenViewportSignatures.Add($initialObservation.viewport_signature)
    $script:stage = 'history-initial-load'
    $script:evidence.stage = $script:stage
    $overlay = Invoke-HookOperation 'open_history' @{ first_marker = $firstMarker; last_marker = $lastMarker }
    Assert-JsonProperties $overlay @(
        'pages_moved', 'rows_loaded', 'request_before_line_id', 'loaded_first_line_id',
        'loaded_last_line_id', 'loaded_has_more_before', 'next_before_line_id'
    ) 'Debug hook initial history response'
    Assert-E2E (Test-JsonInteger $overlay.pages_moved ([int]::MaxValue)) 'E2E_HOOK_RESPONSE_INVALID' `
        'Debug hook initial history response has invalid pages_moved.'
    Assert-E2E (Test-JsonInteger $overlay.rows_loaded ([int]::MaxValue)) 'E2E_HOOK_RESPONSE_INVALID' `
        'Debug hook initial history response has invalid rows_loaded.'
    foreach ($field in @('request_before_line_id', 'loaded_first_line_id', 'loaded_last_line_id', 'next_before_line_id')) {
        Assert-E2E (Test-JsonInteger $overlay.$field ([decimal][uint64]::MaxValue)) 'E2E_HOOK_RESPONSE_INVALID' `
            "Debug hook initial history response has invalid $field."
    }
    Assert-E2E ($overlay.loaded_has_more_before -is [bool]) 'E2E_HOOK_RESPONSE_INVALID' `
        'Debug hook initial history response has invalid loaded_has_more_before.'
    $overlayObservation = Get-StateObservation 'history-initial-load' $overlay
    $overlayObservation | Add-Member -NotePropertyName pages_moved -NotePropertyValue ([int]$overlay.pages_moved)
    $overlayObservation | Add-Member -NotePropertyName rows_loaded -NotePropertyValue ([int]$overlay.rows_loaded)
    $overlayObservation | Add-Member -NotePropertyName request_before_line_id -NotePropertyValue ([uint64]$overlay.request_before_line_id)
    $overlayObservation | Add-Member -NotePropertyName loaded_first_line_id -NotePropertyValue ([uint64]$overlay.loaded_first_line_id)
    $overlayObservation | Add-Member -NotePropertyName loaded_last_line_id -NotePropertyValue ([uint64]$overlay.loaded_last_line_id)
    $overlayObservation | Add-Member -NotePropertyName loaded_has_more_before -NotePropertyValue ([bool]$overlay.loaded_has_more_before)
    $overlayObservation | Add-Member -NotePropertyName next_before_line_id `
        -NotePropertyValue ([uint64]$overlay.next_before_line_id) -Force
    $script:evidence.observations += $overlayObservation
    Add-Assertion 'history-initial-load-session-fixed' $fixedSessionId $overlayObservation.session_id `
        ($overlayObservation.session_id -ceq $fixedSessionId)
    Add-Assertion 'history-overlay-visible' 'true' $overlayObservation.history_overlay $overlayObservation.history_overlay
    Add-Assertion 'history-initial-load-single-page' '1' $overlayObservation.pages_moved `
        ($overlayObservation.pages_moved -eq 1)
    Add-Assertion 'history-initial-load-bounded-rows' "1..$HistoryPageSize" $overlayObservation.rows_loaded `
        ($overlayObservation.rows_loaded -ge 1 -and $overlayObservation.rows_loaded -le $HistoryPageSize)
    Add-Assertion 'history-initial-load-transcript-count' '1' $overlayObservation.transcript_count `
        ($overlayObservation.transcript_count -eq 1)
    $expectedSnapshotCountAfterHistory = $initialObservation.snapshot_count +
        [int]($overlayObservation.snapshot_sequence_s -ne $initialSnapshot)
    Add-Assertion 'history-initial-load-snapshot-cache-matches-rebase' `
        ([string]$expectedSnapshotCountAfterHistory) $overlayObservation.snapshot_count `
        ($overlayObservation.snapshot_count -eq $expectedSnapshotCountAfterHistory)
    Add-Assertion 'history-initial-load-chunk-count-fixed' '0' $overlayObservation.chunk_count `
        ($overlayObservation.chunk_count -eq 0)
    Add-Assertion 'history-initial-load-snapshot-rebase-covered-by-ack' `
        "$initialSnapshot..$($overlayObservation.live_ack)" $overlayObservation.snapshot_sequence_s `
        ($overlayObservation.snapshot_sequence_s -ge $initialSnapshot -and
            $overlayObservation.snapshot_sequence_s -le $overlayObservation.live_ack)
    Add-Assertion 'history-initial-load-live-ack-not-regressed' ">=$fixedAck" $overlayObservation.live_ack `
        ($overlayObservation.live_ack -ge $fixedAck)
    Add-Assertion 'history-initial-load-grid-columns-fixed' ([string]$fixedColumns) $overlayObservation.terminal_columns `
        ($overlayObservation.terminal_columns -eq $fixedColumns)
    Add-Assertion 'history-initial-load-grid-rows-fixed' ([string]$fixedRows) $overlayObservation.terminal_rows `
        ($overlayObservation.terminal_rows -eq $fixedRows)
    Add-Assertion 'history-initial-load-viewport-changed' 'different from live tail' $overlayObservation.viewport_signature `
        ($seenViewportSignatures.Add($overlayObservation.viewport_signature))
    $initialExpectedPage = $expectedTranscriptPages[0]
    $initialExpectedRows = [int]($initialExpectedPage.last - $initialExpectedPage.first + 1)
    Add-Assertion 'history-initial-load-exact-row-count' ([string]$initialExpectedRows) $overlayObservation.rows_loaded `
        ($overlayObservation.rows_loaded -eq $initialExpectedRows)
    Add-Assertion 'history-initial-request-cursor' ([string]$initialExpectedPage.request_before) `
        $overlayObservation.request_before_line_id ($overlayObservation.request_before_line_id -eq $initialExpectedPage.request_before)
    Add-Assertion 'history-initial-first-line' ([string]$initialExpectedPage.first) `
        $overlayObservation.loaded_first_line_id ($overlayObservation.loaded_first_line_id -eq $initialExpectedPage.first)
    Add-Assertion 'history-initial-last-line' ([string]$initialExpectedPage.last) `
        $overlayObservation.loaded_last_line_id ($overlayObservation.loaded_last_line_id -eq $initialExpectedPage.last)
    Add-Assertion 'history-initial-has-more' ([string]$initialExpectedPage.has_more) `
        $overlayObservation.loaded_has_more_before ($overlayObservation.loaded_has_more_before -eq $initialExpectedPage.has_more)
    Add-Assertion 'history-initial-next-cursor' ([string]$initialExpectedPage.first) `
        $overlayObservation.next_before_line_id ($overlayObservation.next_before_line_id -eq $initialExpectedPage.first)

    # A pending Transcript may authenticate a newer Snapshot after the live
    # renderer has durably covered it. Once the first page is loaded, all
    # continuation requests remain fixed to that rebased anchor and the ACK
    # observed in the same atomic bridge response.
    $fixedSnapshot = $overlayObservation.snapshot_sequence_s
    $fixedAck = $overlayObservation.live_ack

    $script:stage = 'history-page-up'
    $script:evidence.stage = $script:stage
    $previousPageObservation = $overlayObservation
    for ($page = 1; $page -lt $HistoryPageCount; $page++) {
        $paged = Invoke-HookOperation 'page_up' @{
            first_marker = $firstMarker
            last_marker = $lastMarker
            page_size = [string]$HistoryPageSize
            page_number = [string]$page
            expected_result = 'success'
        }
        $previousPageObservation = Get-PaginationObservation `
            -PageNumber $page `
            -Response $paged `
            -Previous $previousPageObservation `
            -ExpectedSessionId $fixedSessionId `
            -ExpectedSnapshot $fixedSnapshot `
            -ExpectedAck $fixedAck `
            -ExpectedColumns $fixedColumns `
            -ExpectedRows $fixedRows `
            -ExpectedPage $expectedTranscriptPages[$page] `
            -SeenViewportSignatures $seenViewportSignatures
    }
    $script:evidence.screenshots += Save-Screenshot '02-paged-history.png'

    $script:stage = 'history-failure-retention'
    $script:evidence.stage = $script:stage
    $offlineExpectedPage = $expectedTranscriptPages[$HistoryPageCount]
    Add-Assertion 'offline-probe-has-unloaded-page' 'true' $previousPageObservation.loaded_has_more_before `
        ([bool]$previousPageObservation.loaded_has_more_before)
    Add-Assertion 'offline-probe-request-cursor' ([string]$offlineExpectedPage.request_before) `
        $previousPageObservation.next_before_line_id `
        ($previousPageObservation.next_before_line_id -eq $offlineExpectedPage.request_before)
    Add-Assertion 'offline-probe-before-final-page-count' ([string]$HistoryPageCount) `
        $previousPageObservation.transcript_count `
        ($previousPageObservation.transcript_count -eq $HistoryPageCount)
    try {
        Set-AndroidAirplaneMode $true
        $script:evidence.cleanup.network_failure_injected = $true
        # Android's connectivity transition legitimately changes the session
        # status from CONTROLLING to READ_ONLY and may remeasure the terminal
        # surface. Capture the failure-retention baseline after that transition
        # and immediately before the rejected page request, so the assertion
        # proves the request itself does not move or replace the viewport.
        $offlineBaselineDeadline = [DateTime]::UtcNow.AddSeconds(30)
        $offlineBaselineObservation = $null
        $offlinePreviousViewportSignature = $null
        $offlineStableViewportSamples = 0
        do {
            $offlineBaselineResponse = Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker }
            $offlineBaselineObservation = Get-StateObservation 'history-offline-baseline' $offlineBaselineResponse
            $networkLossSettled =
                $offlineBaselineObservation.realtime_network_lost_count -gt
                    $previousPageObservation.realtime_network_lost_count -and
                $offlineBaselineObservation.realtime_remote_state -in @('READ_ONLY', 'RECONNECTING')
            if ($networkLossSettled) {
                $offlineStableViewportSamples = if (
                    $offlineBaselineObservation.viewport_signature -ceq $offlinePreviousViewportSignature
                ) { $offlineStableViewportSamples + 1 } else { 1 }
                $offlinePreviousViewportSignature = $offlineBaselineObservation.viewport_signature
            }
            else {
                $offlineStableViewportSamples = 0
                $offlinePreviousViewportSignature = $null
            }
            $appObservedNetworkLoss = $networkLossSettled -and $offlineStableViewportSamples -ge 2
            if (-not $appObservedNetworkLoss) { Start-Sleep -Milliseconds 250 }
        } while (-not $appObservedNetworkLoss -and [DateTime]::UtcNow -lt $offlineBaselineDeadline)
        Add-Assertion 'offline-baseline-observed-network-loss' 'true' $appObservedNetworkLoss `
            $appObservedNetworkLoss
        $script:evidence.observations += $offlineBaselineObservation
        Add-Assertion 'offline-baseline-session-fixed' $fixedSessionId $offlineBaselineObservation.session_id `
            ($offlineBaselineObservation.session_id -ceq $fixedSessionId -and
                $offlineBaselineObservation.history_overlay)
        Add-Assertion 'offline-baseline-history-objects' `
            "$($previousPageObservation.snapshot_count)/$($previousPageObservation.transcript_count)/$($previousPageObservation.chunk_count)" `
            "$($offlineBaselineObservation.snapshot_count)/$($offlineBaselineObservation.transcript_count)/$($offlineBaselineObservation.chunk_count)" `
            ($offlineBaselineObservation.snapshot_count -eq $previousPageObservation.snapshot_count -and
                $offlineBaselineObservation.transcript_count -eq $previousPageObservation.transcript_count -and
                $offlineBaselineObservation.chunk_count -eq $previousPageObservation.chunk_count)
        $failedPage = Invoke-HookOperation 'page_up' @{
            first_marker = $firstMarker
            last_marker = $lastMarker
            page_size = [string]$HistoryPageSize
            page_number = [string]$HistoryPageCount
            expected_result = 'transcript_load_failed'
        } -AcceptFailure
        Add-Assertion 'offline-page-load-failed-closed' 'false' $failedPage.ok (-not [bool]$failedPage.ok)
        Add-Assertion 'offline-page-load-network-failure' 'TRANSCRIPT_LOAD_FAILED' $failedPage.failure_code `
            ([string]$failedPage.failure_code -ceq 'TRANSCRIPT_LOAD_FAILED')
        $script:evidence.observations += [pscustomobject][ordered]@{
            phase = 'offline-page-load-failure'
            observed_at_utc = [DateTime]::UtcNow.ToString('o')
            session_id = $fixedSessionId
            request_before_line_id = $previousPageObservation.next_before_line_id
            expected_first_line_id = $offlineExpectedPage.first
            expected_last_line_id = $offlineExpectedPage.last
            failure_code = [string]$failedPage.failure_code
            airplane_mode = $true
        }
        $retained = Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker }
        $retainedObservation = Get-StateObservation 'history-failure-retained' $retained
        $script:evidence.observations += $retainedObservation
        Add-Assertion 'failure-retains-session-page' $fixedSessionId $retainedObservation.session_id `
            ($retainedObservation.session_id -ceq $fixedSessionId -and $retainedObservation.history_overlay)
        Add-Assertion 'failure-retains-history-objects' `
            "$($previousPageObservation.snapshot_count)/$($previousPageObservation.transcript_count)/$($previousPageObservation.chunk_count)" `
            "$($retainedObservation.snapshot_count)/$($retainedObservation.transcript_count)/$($retainedObservation.chunk_count)" `
            ($retainedObservation.snapshot_count -eq $previousPageObservation.snapshot_count -and
                $retainedObservation.transcript_count -eq $previousPageObservation.transcript_count -and
                $retainedObservation.chunk_count -eq $previousPageObservation.chunk_count)
        Add-Assertion 'failure-retains-snapshot-s' ([string]$fixedSnapshot) $retainedObservation.snapshot_sequence_s `
            ($retainedObservation.snapshot_sequence_s -eq $fixedSnapshot)
        Add-Assertion 'failure-retains-live-ack' ([string]$fixedAck) $retainedObservation.live_ack `
            ($retainedObservation.live_ack -eq $fixedAck)
        Add-Assertion 'failure-retains-viewport' $offlineBaselineObservation.viewport_signature `
            $retainedObservation.viewport_signature `
            ($retainedObservation.viewport_signature -ceq $offlineBaselineObservation.viewport_signature)
        Add-Assertion 'failure-retains-first-visible-line-id' `
            ([string]$offlineBaselineObservation.first_visible_line_id) `
            $retainedObservation.first_visible_line_id `
            ($retainedObservation.first_visible_line_id -eq $offlineBaselineObservation.first_visible_line_id)
        Add-Assertion 'failure-retains-visible-text-hash' $offlineBaselineObservation.visible_text_sha256 `
            $retainedObservation.visible_text_sha256 `
            ($retainedObservation.visible_text_sha256 -ceq $offlineBaselineObservation.visible_text_sha256)
        Add-Assertion 'failure-retains-transcript-top-row' `
            ([string]$offlineBaselineObservation.transcript_top_row) $retainedObservation.transcript_top_row `
            ($retainedObservation.transcript_top_row -eq $offlineBaselineObservation.transcript_top_row)
        Add-Assertion 'failure-retains-grid-columns' ([string]$fixedColumns) $retainedObservation.terminal_columns `
            ($retainedObservation.terminal_columns -eq $fixedColumns)
        Add-Assertion 'failure-retains-grid-rows' ([string]$fixedRows) $retainedObservation.terminal_rows `
            ($retainedObservation.terminal_rows -eq $fixedRows)
        $script:evidence.screenshots += Save-Screenshot '02a-failed-page-retained.png'
    }
    finally {
        if ($script:airplaneModeEnabled) {
            Set-AndroidAirplaneMode $false
            $script:evidence.cleanup.android_network_restored = $true
        }
    }

    $script:stage = 'history-network-recovery'
    $script:evidence.stage = $script:stage
    # Wait for the actual Relay successor; public Internet validation is diagnostic only.
    $onlineRecoveryDeadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
    $onlineRecoveryObservation = $null
    $appObservedNetworkRecovery = $false
    do {
        $onlineRecoveryResponse = Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker }
        $onlineRecoveryObservation = Get-StateObservation 'history-online-recovery' $onlineRecoveryResponse
        $appObservedNetworkRecovery =
            (Test-RelaySubscriptionRecovered $previousPageObservation $onlineRecoveryObservation) -and
            $onlineRecoveryObservation.realtime_remote_state -ceq 'CONTROLLING'
        if (-not $appObservedNetworkRecovery) { Start-Sleep -Milliseconds 250 }
    } while (-not $appObservedNetworkRecovery -and [DateTime]::UtcNow -lt $onlineRecoveryDeadline)
    Add-Assertion 'online-retry-observed-network-recovery' 'true' $appObservedNetworkRecovery `
        $appObservedNetworkRecovery
    $script:evidence.observations += $onlineRecoveryObservation
    Add-Assertion 'online-retry-retains-history-objects' `
        "$($previousPageObservation.snapshot_count)/$($previousPageObservation.transcript_count)/$($previousPageObservation.chunk_count)" `
        "$($onlineRecoveryObservation.snapshot_count)/$($onlineRecoveryObservation.transcript_count)/$($onlineRecoveryObservation.chunk_count)" `
        ($onlineRecoveryObservation.snapshot_count -eq $previousPageObservation.snapshot_count -and
            $onlineRecoveryObservation.transcript_count -eq $previousPageObservation.transcript_count -and
            $onlineRecoveryObservation.chunk_count -eq $previousPageObservation.chunk_count)

    $script:stage = 'history-final-page-retry'
    $script:evidence.stage = $script:stage
    $finalPage = Invoke-HookOperation 'page_up' @{
        first_marker = $firstMarker
        last_marker = $lastMarker
        page_size = [string]$HistoryPageSize
        page_number = [string]$HistoryPageCount
        expected_result = 'success'
    }
    $previousPageObservation = Get-PaginationObservation `
        -PageNumber $HistoryPageCount `
        -Response $finalPage `
        -Previous $previousPageObservation `
        -ExpectedSessionId $fixedSessionId `
        -ExpectedSnapshot $fixedSnapshot `
        -ExpectedAck $fixedAck `
        -ExpectedColumns $fixedColumns `
        -ExpectedRows $fixedRows `
        -ExpectedPage $expectedTranscriptPages[$HistoryPageCount] `
        -SeenViewportSignatures $seenViewportSignatures
    Add-Assertion 'history-complete-all-distinct-pages' ([string]$snapshotTranscriptPageCount) `
        $previousPageObservation.transcript_count `
        ($previousPageObservation.transcript_count -eq $snapshotTranscriptPageCount)
    foreach ($boundary in $tlH115BoundaryLineIds | Where-Object { $_ -lt $snapshotMarkerCount }) {
        $boundaryPageIndex = -1
        for ($index = 0; $index -lt $expectedTranscriptPages.Count; $index++) {
            $candidatePage = $expectedTranscriptPages[$index]
            if ($boundary -ge $candidatePage.first -and $boundary -le $candidatePage.last) {
                $boundaryPageIndex = $index
                break
            }
        }
        Add-Assertion "tl-h115-line-$boundary-loaded" 'authenticated transcript page loaded' `
            $boundaryPageIndex `
            ($boundaryPageIndex -ge 0 -and $previousPageObservation.transcript_count -gt $boundaryPageIndex)
    }
    $script:evidence.screenshots += Save-Screenshot '02b-complete-history.png'

    $script:stage = 'history-overlay-live-flow'
    $script:evidence.stage = $script:stage
    $overlayAck = $overlayObservation.live_ack
    $script:sessionProcess.StandardInput.WriteLine($liveMarker)
    $script:sessionProcess.StandardInput.Flush()
    $liveDeadline = [DateTime]::UtcNow.AddSeconds($ObservationTimeoutSeconds)
    $liveObservation = $null
    $lastLiveCandidate = $null
    do {
        $live = Invoke-HookOperation 'inspect' @{ live_marker = $liveMarker }
        $candidate = Get-StateObservation 'history-overlay-live' $live
        $lastLiveCandidate = $candidate
        if ($candidate.history_overlay -and $candidate.live_ack -gt $overlayAck -and $candidate.live_marker_visible) {
            $liveObservation = $candidate
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $liveDeadline)
    if ($null -eq $liveObservation -and $null -ne $lastLiveCandidate) {
        $lastLiveCandidate.phase = 'history-overlay-live-timeout'
        $script:evidence.observations += $lastLiveCandidate
    }
    Assert-E2E ($null -ne $liveObservation) 'LIVE_ACK_DID_NOT_ADVANCE' `
        'Live ACK did not advance while the history overlay was open.'
    $script:evidence.observations += $liveObservation
    Add-Assertion 'history-overlay-live-session-fixed' $fixedSessionId $liveObservation.session_id `
        ($liveObservation.session_id -ceq $fixedSessionId)
    Add-Assertion 'history-overlay-live-snapshot-s-fixed' ([string]$fixedSnapshot) $liveObservation.snapshot_sequence_s `
        ($liveObservation.snapshot_sequence_s -eq $fixedSnapshot)
    Add-Assertion 'history-overlay-live-ack-advanced' ">$overlayAck" $liveObservation.live_ack ($liveObservation.live_ack -gt $overlayAck)
    Add-Assertion 'history-overlay-live-marker-received' 'true' $liveObservation.live_marker_visible $liveObservation.live_marker_visible
    Add-Assertion 'history-overlay-live-grid-columns-fixed' ([string]$fixedColumns) $liveObservation.terminal_columns `
        ($liveObservation.terminal_columns -eq $fixedColumns)
    Add-Assertion 'history-overlay-live-grid-rows-fixed' ([string]$fixedRows) $liveObservation.terminal_rows `
        ($liveObservation.terminal_rows -eq $fixedRows)
    $script:evidence.screenshots += Save-Screenshot '03-history-overlay-live.png'

    $closed = Invoke-HookOperation 'close_history' @{ live_marker = $liveMarker }
    $closedObservation = Get-StateObservation 'history-overlay-closed' $closed
    $script:evidence.observations += $closedObservation
    Add-Assertion 'history-overlay-closed-session-fixed' $fixedSessionId $closedObservation.session_id `
        ($closedObservation.session_id -ceq $fixedSessionId)
    Add-Assertion 'history-overlay-closed' 'false' $closedObservation.history_overlay (-not $closedObservation.history_overlay)
    Add-Assertion 'history-overlay-closed-ack-not-regressed' ">=$($liveObservation.live_ack)" $closedObservation.live_ack `
        ($closedObservation.live_ack -ge $liveObservation.live_ack)
    Add-Assertion 'history-overlay-closed-grid-columns-fixed' ([string]$fixedColumns) $closedObservation.terminal_columns `
        ($closedObservation.terminal_columns -eq $fixedColumns)
    Add-Assertion 'history-overlay-closed-grid-rows-fixed' ([string]$fixedRows) $closedObservation.terminal_rows `
        ($closedObservation.terminal_rows -eq $fixedRows)
    Add-Assertion 'new-live-marker-visible-after-exit' 'true' $closedObservation.live_marker_visible $closedObservation.live_marker_visible
    $script:evidence.screenshots += Save-Screenshot '04-live-tail-restored.png'
    }

    $script:stage = 'ime-layout'
    $script:evidence.stage = $script:stage
    $layout = Invoke-HookOperation 'show_ime'
    Assert-JsonProperties $layout @(
        'ime_visible', 'display_bounds', 'terminal_bounds', 'bottom_bar_bounds', 'ime_bounds'
    ) 'Debug hook IME layout response'
    Assert-E2E ($layout.ime_visible -is [bool]) 'E2E_HOOK_RESPONSE_INVALID' `
        'The Debug hook IME visibility field is not a JSON boolean.'
    Assert-E2E ([bool]$layout.ime_visible) 'IME_NOT_VISIBLE' 'The Android IME did not become visible.'
    $layoutObservation = Get-StateObservation 'ime-layout' $layout
    $display = ConvertTo-Rect $layout.display_bounds 'display'
    $terminal = ConvertTo-Rect $layout.terminal_bounds 'terminal'
    $bottomBar = ConvertTo-Rect $layout.bottom_bar_bounds 'bottom bar'
    $ime = ConvertTo-Rect $layout.ime_bounds 'IME'
    $layoutObservation | Add-Member -NotePropertyName ime_visible -NotePropertyValue $true
    $layoutObservation | Add-Member -NotePropertyName display_bounds -NotePropertyValue $display
    $layoutObservation | Add-Member -NotePropertyName terminal_bounds -NotePropertyValue $terminal
    $layoutObservation | Add-Member -NotePropertyName bottom_bar_bounds -NotePropertyValue $bottomBar
    $layoutObservation | Add-Member -NotePropertyName ime_bounds -NotePropertyValue $ime
    $script:evidence.observations += $layoutObservation
    Add-Assertion 'ime-layout-session-fixed' $fixedSessionId $layoutObservation.session_id `
        ($layoutObservation.session_id -ceq $fixedSessionId)
    Add-Assertion 'ime-layout-snapshot-s-fixed' ([string]$fixedSnapshot) $layoutObservation.snapshot_sequence_s `
        ($layoutObservation.snapshot_sequence_s -eq $fixedSnapshot)
    Add-Assertion 'ime-layout-ack-not-regressed' ">=$($closedObservation.live_ack)" $layoutObservation.live_ack `
        ($layoutObservation.live_ack -ge $closedObservation.live_ack)
    Add-Assertion 'ime-layout-grid-columns-fixed' ([string]$fixedColumns) $layoutObservation.terminal_columns `
        ($layoutObservation.terminal_columns -eq $fixedColumns)
    Add-Assertion 'ime-layout-grid-rows-fixed' ([string]$fixedRows) $layoutObservation.terminal_rows `
        ($layoutObservation.terminal_rows -eq $fixedRows)
    Add-Assertion 'ime-layout-viewport-projection-fixed' $closedObservation.viewport_signature `
        $layoutObservation.viewport_signature `
        ($layoutObservation.viewport_signature -ceq $closedObservation.viewport_signature)
    $allInsideDisplay = $terminal.left -ge $display.left -and $terminal.top -ge $display.top -and
        $terminal.right -le $display.right -and $terminal.bottom -le $display.bottom -and
        $bottomBar.left -ge $display.left -and $bottomBar.top -ge $display.top -and
        $bottomBar.right -le $display.right -and $bottomBar.bottom -le $display.bottom -and
        $ime.left -ge $display.left -and $ime.top -ge $display.top -and
        $ime.right -le $display.right -and $ime.bottom -le $display.bottom
    Add-Assertion 'layout-bounds-inside-display' 'true' $allInsideDisplay $allInsideDisplay
    $terminalClear = $terminal.bottom -le $ime.top
    $bottomBarClear = $bottomBar.bottom -le $ime.top
    $terminalBarClear = $terminal.bottom -le $bottomBar.top
    $terminalBarGap = $bottomBar.top - $terminal.bottom
    Add-Assertion 'terminal-not-obscured-by-ime' 'true' $terminalClear $terminalClear
    Add-Assertion 'bottom-bar-not-obscured-by-ime' 'true' $bottomBarClear $bottomBarClear
    Add-Assertion 'terminal-not-obscured-by-bottom-bar' 'true' $terminalBarClear $terminalBarClear
    Add-Assertion 'terminal-extends-to-bottom-bar' '0..1px' $terminalBarGap ($terminalBarGap -in 0, 1)
    $script:evidence.screenshots += Save-Screenshot '05-ime-layout.png'

    }
    $script:evidence.status = 'passed'
    $script:evidence.stage = 'complete'
}
catch {
    $code = if ([string]::IsNullOrWhiteSpace($script:failureCode)) { 'UNEXPECTED_FAILURE' } else { $script:failureCode }
    $message = if ([string]::IsNullOrWhiteSpace($script:failurePublicMessage)) {
        'An unexpected failure occurred; raw exception text was suppressed to protect acceptance credentials.'
    } else { $script:failurePublicMessage }
    $script:evidence.status = 'failed'
    $script:evidence.failure = [pscustomobject][ordered]@{
        code = $code
        stage = $script:stage
        message = $message
        hook_failure_code = $script:hookFailureCode
    }
    Save-HookFailureForensics
    if ($FrozenLifecycleAcceptance -and $script:stage -ceq 'tail-first-open' -and
        $null -ne $script:openSessionRequest -and $null -ne $script:liveMarker -and
        $null -ne $script:sessionProcess -and -not $script:sessionProcess.HasExited -and
        $script:evidence.PSObject.Properties['session_arm']) {
        Save-FrozenOpenFailureEvidence $script:openSessionRequest $script:evidence.session_arm $script:liveMarker
    }
}
finally {
    if ($script:frozenCaptureActive -and $null -ne $script:adbPath) {
        try { $null = Invoke-HookOperation 'release_frozen_history'; $script:frozenCaptureActive = $false }
        catch {}
    }
    if ($script:airplaneModeEnabled -and $null -ne $script:adbPath) {
        for ($attempt = 1; $attempt -le 3 -and $script:airplaneModeEnabled; $attempt++) {
            try {
                $restoreNetwork = Invoke-NativeCapture $script:adbPath `
                    @('-s', $serial, 'shell', 'cmd', 'connectivity', 'airplane-mode', 'disable') 15
                $restoredState = Invoke-NativeCapture $script:adbPath `
                    @('-s', $serial, 'shell', 'settings', 'get', 'global', 'airplane_mode_on') 15
                if ($restoreNetwork.ExitCode -eq 0 -and $restoredState.ExitCode -eq 0 -and
                    [string]$restoredState.StdOut.Trim() -eq '0') {
                    $script:airplaneModeEnabled = $false
                    $script:evidence.cleanup.android_network_restored = $true
                }
            }
            catch {}
        }
    }
    if ($null -ne $script:sessionProcess) {
        try {
            if (-not $script:sessionProcess.HasExited) {
                $script:sessionProcess.StandardInput.WriteLine('__TERMLINK_TAIL_FIRST_EXIT__')
                $script:sessionProcess.StandardInput.Flush()
                $script:sessionProcess.StandardInput.Close()
                if (-not $script:sessionProcess.WaitForExit(15000)) { $script:sessionProcess.Kill($true) }
            }
            $script:evidence.cleanup.owned_session_stopped = $true
        }
        catch {}
        finally { $script:sessionProcess.Dispose() }
    }
    if ($script:emulatorOwned -and -not $KeepEmulator) {
        try {
            $null = Invoke-NativeCapture $script:adbPath @('-s', $serial, 'emu', 'kill') 15
            if ($null -ne $script:emulatorProcess -and -not $script:emulatorProcess.HasExited) {
                if (-not $script:emulatorProcess.WaitForExit(30000)) { $script:emulatorProcess.Kill($true) }
            }
            $script:evidence.cleanup.owned_emulator_stopped = $true
        }
        catch {}
    }
    if ($null -ne $script:emulatorProcess) { $script:emulatorProcess.Dispose() }
    Write-Evidence
}

if ($script:evidence.status -eq 'failed') {
    throw "$($script:evidence.failure.code): $($script:evidence.failure.message) Evidence: $evidencePath"
}
Write-Output "Tail-first product E2E passed. Evidence: $evidencePath"
