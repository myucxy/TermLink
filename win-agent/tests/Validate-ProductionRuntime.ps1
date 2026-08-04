[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet'
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

if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    throw "Refusing to overwrite an existing production config: $configPath"
}
if (Test-Path -LiteralPath $credentialsPath -PathType Leaf) {
    throw "Refusing to overwrite existing production credentials: $credentialsPath"
}

function Get-InternalMethod {
    param([Type]$Type, [string]$Name)
    $Type.GetMethod($Name, [Reflection.BindingFlags]'Static,Instance,Public,NonPublic')
}

$service = $null
$identity = $null
$agentProcess = $null
try {
    $resolver = [ResolveEventHandler]{
        param($sender, $eventArgs)
        $name = ([Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
        $candidate = Join-Path $agentOutput $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [Reflection.Assembly]::LoadFrom($candidate)
        }
        return $null
    }
    [AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
    $assembly = [Reflection.Assembly]::LoadFrom($agentAssembly)
    $protocol = [Reflection.Assembly]::LoadFrom($protocolAssembly)
    $runtimeType = $assembly.GetType('TermLink.Agent.ProductionAgentRuntime', $true)
    $tryCreate = Get-InternalMethod $runtimeType 'TryCreate'

    $pendingSessionsField = $runtimeType.GetField('pendingSessions', [Reflection.BindingFlags]'Instance,NonPublic')
    $eventSignalField = $runtimeType.GetField('eventSignal', [Reflection.BindingFlags]'Instance,NonPublic')
    $socketSendGateField = $runtimeType.GetField('socketSendGate', [Reflection.BindingFlags]'Instance,NonPublic')
    if ($null -eq $pendingSessionsField -or $null -eq $eventSignalField -or $null -eq $socketSendGateField) {
        throw 'Production runtime replay/backpressure fields are missing.'
    }
    if ($null -ne $runtimeType.GetField('events', [Reflection.BindingFlags]'Instance,NonPublic')) {
        throw 'Production runtime still exposes the lossy DropWrite event channel.'
    }
    if ($null -eq (Get-InternalMethod $runtimeType 'RememberSubscriber')) {
        throw 'Production runtime does not retain authenticated Realtime subscribers.'
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
    if ($null -ne $resolver) { [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver) }
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
