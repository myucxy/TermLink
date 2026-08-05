[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet',
    [switch]$PreserveExistingProductionFiles
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agentProject = Join-Path $projectRoot 'src\Agent\TermLink.Agent.csproj'
$agentAssembly = Join-Path $projectRoot 'src\Agent\bin\Release\net8.0-windows\TermLink.Agent.dll'
$protocolAssembly = Join-Path $projectRoot 'src\Protocol\bin\Release\net8.0\TermLink.Protocol.dll'
$agentOutput = Split-Path -Parent $agentAssembly
$build = & $DotNetPath build $agentProject --configuration Release --no-restore 2>&1
if ($LASTEXITCODE -ne 0) { throw "Agent build failed: $($build -join "`n")" }

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$termLinkDirectory = Join-Path $localAppData 'TermLink'
$configPath = Join-Path $termLinkDirectory 'production-config.json'
$credentialsPath = Join-Path $termLinkDirectory 'production-credentials.dpapi'
$bufferRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Production-Lifecycle-' + [Guid]::NewGuid().ToString('N'))
$probeBufferRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Production-Probe-' + [Guid]::NewGuid().ToString('N'))
$replayBufferDirectory = [string](Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Production-Replay-' + [Guid]::NewGuid().ToString('N')))

if ((Test-Path -LiteralPath $configPath -PathType Leaf) -or
    (Test-Path -LiteralPath $credentialsPath -PathType Leaf)) {
    if (-not $PreserveExistingProductionFiles) {
        throw "Refusing to overwrite existing production files under: $termLinkDirectory"
    }
    $runningTermLinkProcesses = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like 'TermLink*' })
    if ($runningTermLinkProcesses.Count -gt 0) {
        throw 'Stop all TermLink processes before preserving production files for validation.'
    }

    $backupSuffix = '.validation-backup-' + [Guid]::NewGuid().ToString('N')
    $configBackupPath = $null
    $credentialsBackupPath = $null
    $childOutput = @()
    $childExitCode = -1
    try {
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            $configBackupPath = $configPath + $backupSuffix
            Move-Item -LiteralPath $configPath -Destination $configBackupPath
        }
        if (Test-Path -LiteralPath $credentialsPath -PathType Leaf) {
            $credentialsBackupPath = $credentialsPath + $backupSuffix
            Move-Item -LiteralPath $credentialsPath -Destination $credentialsBackupPath
        }

        $powerShellPath = (Get-Process -Id $PID).Path
        $childOutput = & $powerShellPath -NoProfile -File $PSCommandPath -DotNetPath $DotNetPath 2>&1
        $childExitCode = $LASTEXITCODE
    }
    finally {
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            Remove-Item -LiteralPath $configPath -Force
        }
        if (Test-Path -LiteralPath $credentialsPath -PathType Leaf) {
            Remove-Item -LiteralPath $credentialsPath -Force
        }
        if ($null -ne $configBackupPath -and (Test-Path -LiteralPath $configBackupPath -PathType Leaf)) {
            Move-Item -LiteralPath $configBackupPath -Destination $configPath
        }
        if ($null -ne $credentialsBackupPath -and (Test-Path -LiteralPath $credentialsBackupPath -PathType Leaf)) {
            Move-Item -LiteralPath $credentialsBackupPath -Destination $credentialsPath
        }
    }

    $childOutput | Write-Output
    if ($childExitCode -ne 0) {
        throw "Isolated production runtime validation failed with exit code $childExitCode."
    }
    return
}

if ($null -eq ('TermLinkProductionValidationAssemblyResolver' -as [Type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;

public static class TermLinkProductionValidationAssemblyResolver
{
    private static string searchPath;
    private static bool attached;

    public static void Attach(string path)
    {
        searchPath = path;
        if (!attached)
        {
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            attached = true;
        }
    }

    public static void Detach()
    {
        if (attached)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
            attached = false;
        }
        searchPath = null;
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        try
        {
            var requested = new AssemblyName(args.Name);
            if (requested.Name is null || requested.Name.EndsWith(".resources", StringComparison.Ordinal))
            {
                return null;
            }

            var root = searchPath;
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var candidate = Path.Combine(root, requested.Name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }
        catch
        {
            return null;
        }
    }
}
'@
}

function Get-InternalMethod {
    param([Type]$Type, [string]$Name)
    $Type.GetMethod($Name, [Reflection.BindingFlags]'Static,Instance,Public,NonPublic')
}

$service = $null
$identity = $null
$agentProcess = $null
try {
    [TermLinkProductionValidationAssemblyResolver]::Attach($agentOutput)
    $assembly = [Reflection.Assembly]::LoadFrom($agentAssembly)
    $protocol = [Reflection.Assembly]::LoadFrom($protocolAssembly)
    $runtimeType = $assembly.GetType('TermLink.Agent.ProductionAgentRuntime', $true)
    $tryCreate = Get-InternalMethod $runtimeType 'TryCreate'

    $pendingSessionsField = $runtimeType.GetField('pendingSessions', [Reflection.BindingFlags]'Instance,NonPublic')
    $eventSignalField = $runtimeType.GetField('eventSignal', [Reflection.BindingFlags]'Instance,NonPublic')
    $historySignalField = $runtimeType.GetField('historySignal', [Reflection.BindingFlags]'Instance,NonPublic')
    $historyWorkerField = $runtimeType.GetField('historyWorker', [Reflection.BindingFlags]'Instance,NonPublic')
    $socketSendGateField = $runtimeType.GetField('socketSendGate', [Reflection.BindingFlags]'Instance,NonPublic')
    if ($null -eq $pendingSessionsField -or $null -eq $eventSignalField -or
        $null -eq $historySignalField -or $null -eq $historyWorkerField -or
        $null -eq $socketSendGateField) {
        throw 'Production runtime replay/backpressure fields are missing.'
    }
    if ($null -ne $runtimeType.GetField('events', [Reflection.BindingFlags]'Instance,NonPublic')) {
        throw 'Production runtime still exposes the lossy DropWrite event channel.'
    }
    if ($null -eq (Get-InternalMethod $runtimeType 'RememberSubscriber')) {
        throw 'Production runtime does not retain authenticated Realtime subscribers.'
    }
    foreach ($subscriberMethod in @(
        'RemoveSubscriber', 'PruneSubscribers', 'TryAdvanceSubscriberAck',
        'RetireSubscriber', 'RefreshAuthorizedRecipientsAsync')) {
        if ($null -eq (Get-InternalMethod $runtimeType $subscriberMethod)) {
            throw "Production runtime subscriber lifecycle method is missing: $subscriberMethod"
        }
    }
    $subscriberType = $runtimeType.GetNestedType('Subscriber', [Reflection.BindingFlags]'NonPublic')
    if ($null -eq $subscriberType) { throw 'Production runtime Subscriber state is unavailable.' }
    $subscriber = [Activator]::CreateInstance($subscriberType, $true)
    $subscriberType.GetField('KeyEpoch').SetValue($subscriber, [int64]3)
    $subscriberType.GetField('HighestAck').SetValue($subscriber, [uint64]2)
    $subscriberType.GetField('LastSentSequence').SetValue($subscriber, [uint64]5)
    $subscriberType.GetField('LastActivityUtc').SetValue($subscriber, [DateTime]::UtcNow)
    [byte[]]$subscriberExchange = [byte[]](1..32)
    [byte[]]$subscriberPairwise = [byte[]](33..64)
    $subscriberType.GetField('ExchangePublicKey').SetValue($subscriber, $subscriberExchange)
    $subscriberType.GetField('PairwiseKey').SetValue($subscriber, $subscriberPairwise)
    $advanceAck = Get-InternalMethod $runtimeType 'TryAdvanceSubscriberAck'
    if ($advanceAck.Invoke($null, [object[]]@($subscriber, [uint64]1)) -or
        $advanceAck.Invoke($null, [object[]]@($subscriber, [uint64]6)) -or
        -not $advanceAck.Invoke($null, [object[]]@($subscriber, [uint64]5)) -or
        $advanceAck.Invoke($null, [object[]]@($subscriber, [uint64]4))) {
        throw 'Production runtime accepted a regressive or out-of-range cumulative ACK.'
    }
    $retireSubscriber = Get-InternalMethod $runtimeType 'RetireSubscriber'
    $retireSubscriber.Invoke($null, [object[]]@($subscriber)) | Out-Null
    if (-not [bool]$subscriberType.GetField('Removed').GetValue($subscriber) -or
        $null -ne $subscriberType.GetField('ExchangePublicKey').GetValue($subscriber) -or
        $null -ne $subscriberType.GetField('PairwiseKey').GetValue($subscriber) -or
        @($subscriberExchange | Where-Object { $_ -ne 0 }).Count -ne 0 -or
        @($subscriberPairwise | Where-Object { $_ -ne 0 }).Count -ne 0) {
        throw 'Production runtime did not atomically retire and zeroize Subscriber keys.'
    }
    if ($null -eq (Get-InternalMethod $runtimeType 'AdvancePublishedSequence')) {
        throw 'Production runtime does not preserve a monotonic replay publication cursor.'
    }
    if ($null -eq (Get-InternalMethod $runtimeType 'QueueRegisteredSessionsForReplay')) {
        throw 'Production runtime does not queue recovered Registry sessions for publication.'
    }
    $localInputIdempotencyKey = Get-InternalMethod $runtimeType 'LocalInputLeaseIdempotencyKey'
    if ($null -eq $localInputIdempotencyKey) {
        throw 'Production runtime does not derive a stable local-input lease operation key.'
    }
    $localInputSession = [Guid]::NewGuid()
    $localInputKey = [string]$localInputIdempotencyKey.Invoke(
        $null, [object[]]@($localInputSession, [uint64]42))
    $replayedLocalInputKey = [string]$localInputIdempotencyKey.Invoke(
        $null, [object[]]@($localInputSession, [uint64]42))
    $nextLocalInputKey = [string]$localInputIdempotencyKey.Invoke(
        $null, [object[]]@($localInputSession, [uint64]43))
    if ($localInputKey -ne $replayedLocalInputKey -or
        $localInputKey -eq $nextLocalInputKey -or
        $localInputKey.Length -lt 8 -or $localInputKey.Length -gt 128) {
        throw 'Local-input lease operation keys are not stable, unique, and server-compatible.'
    }
    $mapMessageType = Get-InternalMethod $runtimeType 'MapMessageType'
    if ($null -eq $mapMessageType) {
        throw 'Production runtime lifecycle message mapping is unavailable.'
    }
    $newEnvelopeNonce = Get-InternalMethod $runtimeType 'NewEnvelopeNonce'
    if ($null -eq $newEnvelopeNonce) {
        throw 'Production runtime does not expose replay-safe Envelope nonce generation.'
    }
    $nonces = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt 256; $index++) {
        [byte[]]$nonce = $newEnvelopeNonce.Invoke($null, [object[]]@())
        if ($nonce.Length -ne 12 -or -not $nonces.Add([Convert]::ToHexString($nonce))) {
            throw 'Production runtime generated an invalid or repeated Envelope nonce.'
        }
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($nonce)
    }

    # Exercise the DPAPI buffer reader with deterministic retention loss. This
    # proves resume reads are ordered and that an expired cursor is observable
    # as an earliest-retained sequence for the runtime Gap path.
    New-Item -ItemType Directory -Path $replayBufferDirectory -Force | Out-Null
    $bufferType = $assembly.GetType('TermLink.Agent.EncryptedEventBuffer', $true)
    $bufferConstructor = $bufferType.GetConstructor(
        [Reflection.BindingFlags]'Instance,Public,NonPublic', $null,
        [Type[]]@([string], [Guid], [long]), $null)
    $replaySessionId = [Guid]::NewGuid()
    $buffer = $bufferConstructor.Invoke([object[]]@($replayBufferDirectory, $replaySessionId, [int64]1024))
    $localFrameType = $assembly.GetType('TermLink.LocalIpc.LocalFrame', $true)
    $localFrameKindType = $assembly.GetType('TermLink.LocalIpc.LocalFrameType', $true)
    $frameConstructor = $localFrameType.GetConstructor(
        [Reflection.BindingFlags]'Instance,Public,NonPublic', $null,
        [Type[]]@($localFrameKindType, [Guid], [ulong], [byte[]]), $null)
    $outputKind = [Enum]::Parse($localFrameKindType, 'Output')
    $lifecycleKind = [Enum]::Parse($localFrameKindType, 'Lifecycle')
    $append = Get-InternalMethod $bufferType 'Append'
    for ($sequence = 1; $sequence -le 3; $sequence++) {
        [byte[]]$payload = [byte[]]::new(600)
        [Array]::Fill($payload, [byte]$sequence)
        $frame = $frameConstructor.Invoke([object[]]@(
            $outputKind, $replaySessionId, [uint64]$sequence, $payload))
        $append.Invoke($buffer, [object[]]@($frame)) | Out-Null
    }
    $read = Get-InternalMethod $bufferType 'ReadFrom'
    $readArguments = New-Object object[] 3
    $readArguments[0] = [uint64]0
    $readArguments[1] = [uint64]0
    $readArguments[2] = [uint64]0
    $replayed = $read.Invoke($buffer, $readArguments)
    $earliestRetained = [uint64]$readArguments[1]
    $latestRetained = [uint64]$readArguments[2]
    if ($latestRetained -ne 3 -or $earliestRetained -le 1 -or $replayed.Count -ge 3) {
        throw "Durable replay retention contract failed: earliest=$earliestRetained latest=$latestRetained frames=$($replayed.Count)."
    }
    $buffer.Dispose()
    $buffer = $null

    $localPayloadCodec = $assembly.GetType('TermLink.LocalIpc.LocalPayloadCodec', $true)
    $localLifecycleType = $assembly.GetType('TermLink.LocalIpc.LocalLifecycleState', $true)
    $lifecyclePayload = Get-InternalMethod $localPayloadCodec 'Lifecycle'
    $interruptedState = [Enum]::Parse($localLifecycleType, 'Interrupted')
    [byte[]]$interruptedPayload = $lifecyclePayload.Invoke($null, [object[]]@($interruptedState, 0))
    $interruptedFrame = $frameConstructor.Invoke([object[]]@(
        $lifecycleKind, [Guid]::NewGuid(), [uint64]3, $interruptedPayload))
    $interruptedMessageType = $mapMessageType.Invoke($null, [object[]]@($interruptedFrame))
    if ($interruptedMessageType.ToString() -ne 'SessionInterrupted') {
        throw "Production runtime mapped Interrupted to $interruptedMessageType instead of SessionInterrupted."
    }
    $buildPayload = Get-InternalMethod $runtimeType 'BuildPayload'
    $endedState = [Enum]::Parse($localLifecycleType, 'Ended')
    [byte[]]$endedPayload = $lifecyclePayload.Invoke($null, [object[]]@($endedState, 17))
    $endedFrame = $frameConstructor.Invoke([object[]]@(
        $lifecycleKind, [Guid]::NewGuid(), [uint64]4, $endedPayload))
    $productionEnded = $buildPayload.Invoke($null, [object[]]@($endedFrame))
    if (-not $productionEnded.HasExitCode -or $productionEnded.ExitCode -ne 17) {
        throw 'Production E2EE lifecycle payload dropped the real Shell exit code.'
    }
    [byte[]]$successfulPayload = $lifecyclePayload.Invoke($null, [object[]]@($endedState, 0))
    $successfulFrame = $frameConstructor.Invoke([object[]]@(
        $lifecycleKind, [Guid]::NewGuid(), [uint64]5, $successfulPayload))
    $productionSuccess = $buildPayload.Invoke($null, [object[]]@($successfulFrame))
    if (-not $productionSuccess.HasExitCode -or $productionSuccess.ExitCode -ne 0) {
        throw 'Production E2EE lifecycle payload confused a real zero exit with an unknown result.'
    }
    $productionInterrupted = $buildPayload.Invoke($null, [object[]]@($interruptedFrame))
    if ($productionInterrupted.HasExitCode) {
        throw 'Production E2EE lifecycle payload attached an exit code before Session end.'
    }

    $withoutConfig = $tryCreate.Invoke($null, [object[]]@())
    if ($null -ne $withoutConfig) {
        throw 'Production runtime was enabled without production configuration or credentials.'
    }

    New-Item -ItemType Directory -Path $termLinkDirectory -Force | Out-Null
    [ordered]@{ ServerUrl = 'http://invalid-production-config' } |
        ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
    $invalidRejected = $false
    try {
        $tryCreate.Invoke($null, [object[]]@()) | Out-Null
    }
    catch {
        $invalidRejected = $true
    }
    finally {
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            Remove-Item -LiteralPath $configPath -Force
        }
    }
    if (-not $invalidRejected) { throw 'Malformed production configuration was silently accepted.' }

    [ordered]@{
        ServerUrl = 'https://127.0.0.1:1'
        PcId = [Guid]::NewGuid()
        PcName = 'Production Runtime Validation'
        AgentVersion = 'validation'
        KeyEpoch = 1
    } | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8

    $identityType = $protocol.GetType('TermLink.Protocol.DeviceIdentity', $true)
    $identity = $identityType.GetMethod('Generate', [Reflection.BindingFlags]'Static,Public').Invoke($null, @())
    $credentialsType = $assembly.GetType('TermLink.Agent.ProductionCredentials', $true)
    $save = Get-InternalMethod $credentialsType 'Save'
    $save.Invoke($null, [object[]]@([Guid]::NewGuid(), $identity, 'production-runtime-validation-token')) | Out-Null
    $identity.Dispose()
    $identity = $null

    $optionsType = $assembly.GetType('TermLink.Agent.RegistryServiceOptions', $true)
    $parse = Get-InternalMethod $optionsType 'Parse'
    $controlPipe = 'termlink-production-lifecycle-' + [Guid]::NewGuid().ToString('N')
    $parseArguments = New-Object object[] 1
    $parseArguments[0] = [string[]]@(
        '--registry', '--control-pipe', $controlPipe,
        '--buffer-root', $bufferRoot)
    $options = $parse.Invoke($null, $parseArguments)

    $serviceType = $assembly.GetType('TermLink.Agent.SessionRegistryService', $true)
    $serviceConstructor = $serviceType.GetConstructor(
        [Reflection.BindingFlags]'Instance,Public,NonPublic', $null,
        [Type[]]@($optionsType), $null)
    $service = $serviceConstructor.Invoke([object[]]@($options))

    $runtime = $serviceType.GetField('productionRuntime', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($service)
    if ($null -eq $runtime) { throw 'SessionRegistryService did not create a production runtime.' }
    if ($runtime.GetType().GetField('socketSendGate', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($runtime).CurrentCount -ne 1) {
        throw 'Production runtime WebSocket send gate was not initialized.'
    }
    $pairingChallengeField = $runtime.GetType().GetField(
        'pairingChallenge', [Reflection.BindingFlags]'Instance,NonPublic')
    $snapshotStatus = Get-InternalMethod $runtime.GetType() 'SnapshotStatus'
    $pairingChallengeType = $pairingChallengeField.FieldType
    $pairingChallenge = [Activator]::CreateInstance($pairingChallengeType)
    $pairingChallengeType.GetProperty('TargetFingerprint').SetValue($pairingChallenge, 'validation-fingerprint')
    $pairingChallengeType.GetProperty('Permission').SetValue($pairingChallenge, 'CONTROL')
    $pairingChallengeField.SetValue($runtime, $pairingChallenge)
    $pairingSnapshot = $snapshotStatus.Invoke($runtime, [object[]]@()).PairingChallenge
    if ($null -eq $pairingSnapshot -or
        $pairingSnapshot.TargetFingerprint -ne 'validation-fingerprint' -or
        $pairingSnapshot.Permission -ne 'CONTROL') {
        throw 'Production status snapshot dropped pairing authorization metadata.'
    }
    $pairingChallengeField.SetValue($runtime, $null)
    $httpProofMethod = $runtime.GetType().GetMethods([Reflection.BindingFlags]'Instance,NonPublic') |
        Where-Object {
            $_.Name -eq 'AddProofHeaders' -and
            $_.GetParameters().Count -eq 4 -and
            $_.GetParameters()[0].ParameterType.FullName -eq 'System.Net.Http.Headers.HttpRequestHeaders'
        }
    if ($null -eq $httpProofMethod) { throw 'Production runtime HTTP proof method is unavailable.' }
    $proofRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, 'https://example.invalid/v1/devices')
    try {
        $proofRequest.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'single-token')
        $httpProofMethod.Invoke($runtime, [object[]]@(
            $proofRequest.Headers,
            [Net.Http.HttpMethod]::Get,
            '/v1/devices',
            [byte[]]::new(0))) | Out-Null
        $authorizationValues = @($proofRequest.Headers.GetValues('Authorization'))
        if ($authorizationValues.Count -ne 1 -or $authorizationValues[0] -ne 'Bearer single-token') {
            throw 'Production runtime duplicated or replaced the HTTP Authorization header.'
        }
        foreach ($headerName in @(
            'X-TermLink-Device', 'X-TermLink-Timestamp',
            'X-TermLink-Nonce', 'X-TermLink-Signature')) {
            if (-not $proofRequest.Headers.Contains($headerName)) {
                throw "Production runtime omitted proof header $headerName."
            }
        }
    }
    finally {
        $proofRequest.Dispose()
    }
    $clearSubscribers = Get-InternalMethod $runtime.GetType() 'ClearSubscribers'
    $clearSubscribers.Invoke($runtime, [object[]]@()) | Out-Null
    $rememberSubscriber = Get-InternalMethod $runtime.GetType() 'RememberSubscriber'
    $subscriberSessionId = [Guid]::NewGuid()
    $subscriberDeviceId = [Guid]::NewGuid()
    $retainedSubscriber = [Activator]::CreateInstance($subscriberType, $true)
    $subscriberType.GetField('DeviceId').SetValue($retainedSubscriber, $subscriberDeviceId)
    $subscriberType.GetField('KeyEpoch').SetValue($retainedSubscriber, [int64]1)
    $subscriberType.GetField('LastActivityUtc').SetValue($retainedSubscriber, [DateTime]::UtcNow)
    $subscriberType.GetField('ExchangePublicKey').SetValue($retainedSubscriber, [byte[]](1..32))
    $subscriberType.GetField('PairwiseKey').SetValue($retainedSubscriber, [byte[]](33..64))
    $firstRemembered = $rememberSubscriber.Invoke(
        $runtime, [object[]]@($subscriberSessionId, $retainedSubscriber))
    $secondRemembered = $rememberSubscriber.Invoke(
        $runtime, [object[]]@($subscriberSessionId, $retainedSubscriber))
    if (-not [Object]::ReferenceEquals($firstRemembered, $secondRemembered) -or
        [bool]$subscriberType.GetField('Removed').GetValue($retainedSubscriber) -or
        $null -eq $subscriberType.GetField('PairwiseKey').GetValue($retainedSubscriber)) {
        throw 'Repeated SUBSCRIBE retired the active Subscriber and zeroized its live key.'
    }
    $clearSubscribers.Invoke($runtime, [object[]]@()) | Out-Null
    $runtimeRegistry = $runtime.GetType().GetField('registry', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($runtime)
    $serviceRegistry = $serviceType.GetField('registry', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($service)
    if (-not [Object]::ReferenceEquals($runtimeRegistry, $serviceRegistry)) {
        throw 'Production runtime was not attached to the SessionRegistry owned by the service.'
    }
    if ($null -eq (Get-InternalMethod $serviceRegistry.GetType() 'TrySendResize')) {
        throw 'Production runtime Registry does not expose remote resize dispatch.'
    }

    $service.Dispose()
    $service = $null
    $disposed = [Convert]::ToInt32($runtime.GetType().GetField('disposed', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($runtime))
    if ($disposed -ne 1) { throw 'SessionRegistryService did not dispose the production runtime.' }

    $agentExecutable = Join-Path $agentOutput 'TermLink.Agent.exe'
    $probePipe = 'termlink-production-failure-' + [Guid]::NewGuid().ToString('N')
    $agentProcess = Start-Process -FilePath $agentExecutable -ArgumentList @(
        '--registry', '--control-pipe', $probePipe,
        '--buffer-root', $probeBufferRoot
    ) -PassThru -WindowStyle Hidden
    $probeProject = Join-Path $projectRoot 'tests\AgentControlValidation\TermLink.AgentControl.Validation.csproj'
    $probeOutput = & $DotNetPath run --project $probeProject --configuration Release --no-restore -- `
        --probe-production-failure $probePipe 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Production refresh failure probe failed: $($probeOutput -join "`n")"
    }
    if (-not $agentProcess.WaitForExit(10000) -or $agentProcess.ExitCode -ne 0) {
        throw 'Production failure probe Agent did not exit cleanly.'
    }
    $agentProcess.Dispose()
    $agentProcess = $null
    $probeOutput | Write-Output
    Write-Output 'Production runtime no-config and lifecycle validation passed.'
}
finally {
    [TermLinkProductionValidationAssemblyResolver]::Detach()
    if ($null -ne $identity) { $identity.Dispose() }
    if ($null -ne $service) { try { $service.Dispose() } catch { } }
    if ($null -ne $agentProcess) {
        try { if (-not $agentProcess.HasExited) { $agentProcess.Kill($true) } } catch { }
        try { $agentProcess.Dispose() } catch { }
    }
    if ($null -ne $buffer) { try { $buffer.Dispose() } catch { } }
    if (Test-Path -LiteralPath $configPath -PathType Leaf) { Remove-Item -LiteralPath $configPath -Force }
    if (Test-Path -LiteralPath $credentialsPath -PathType Leaf) { Remove-Item -LiteralPath $credentialsPath -Force }
    if (Test-Path -LiteralPath $bufferRoot -PathType Container) { Remove-Item -LiteralPath $bufferRoot -Recurse -Force }
    if (Test-Path -LiteralPath $probeBufferRoot -PathType Container) { Remove-Item -LiteralPath $probeBufferRoot -Recurse -Force }
    if (Test-Path -LiteralPath $replayBufferDirectory -PathType Container) { Remove-Item -LiteralPath $replayBufferDirectory -Recurse -Force }
}
