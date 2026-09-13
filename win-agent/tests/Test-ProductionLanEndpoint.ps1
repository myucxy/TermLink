[CmdletBinding()]
param(
    [string]$AgentAssembly = (Join-Path $PSScriptRoot '..\src\Agent\bin\Release\net8.0-windows\TermLink.Agent.dll'),
    [string]$TrayAssembly = (Join-Path $PSScriptRoot '..\src\Tray\bin\Release\net8.0-windows\TermLink.dll')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Reflected {
    param(
        [Parameter(Mandatory)][Reflection.MethodInfo]$Method,
        [AllowNull()][object]$Instance,
        [Parameter(Mandatory)][object[]]$Arguments
    )
    try {
        $result = $Method.Invoke($Instance, $Arguments)
        return ,$result
    }
    catch [Reflection.TargetInvocationException] {
        if ($null -ne $_.Exception.InnerException) { throw $_.Exception.InnerException }
        throw
    }
}

function Parse-TrayOptions {
    param([Parameter(Mandatory)][Reflection.MethodInfo]$Parse, [string[]]$Arguments)
    $invokeArguments = [object[]]::new(1)
    $invokeArguments[0] = [string[]]$Arguments
    Invoke-Reflected $Parse $null $invokeArguments
}

$agentPath = [IO.Path]::GetFullPath($AgentAssembly)
$trayPath = [IO.Path]::GetFullPath($TrayAssembly)
$productionTlsSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Agent\ProductionTls.cs'))
$productionRuntimeSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Agent\ProductionAgentRuntime.cs'))
$sessionRegistrySource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Agent\SessionRegistry.cs'))
$trayProgramSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Tray\Program.cs'))
$trayApplicationSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Tray\TrayApplicationContext.cs'))
foreach ($required in @(
    $agentPath,
    $trayPath,
    $productionTlsSource,
    $productionRuntimeSource,
    $sessionRegistrySource,
    $trayProgramSource,
    $trayApplicationSource
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required LAN endpoint validation file is unavailable: $required"
    }
}
if ((Get-Item -LiteralPath $agentPath).LastWriteTimeUtc -lt (Get-Item -LiteralPath $productionTlsSource).LastWriteTimeUtc) {
    throw 'Agent assembly is older than ProductionTls.cs; build TermLink.Agent in Release first.'
}
$trayTimestamp = (Get-Item -LiteralPath $trayPath).LastWriteTimeUtc
if ($trayTimestamp -lt (Get-Item -LiteralPath $trayProgramSource).LastWriteTimeUtc -or
    $trayTimestamp -lt (Get-Item -LiteralPath $trayApplicationSource).LastWriteTimeUtc) {
    throw 'Tray assembly is older than its endpoint sources; build TermLink.Tray in Release first.'
}

$agentDirectory = [IO.Path]::GetDirectoryName($agentPath)
$trayDirectory = [IO.Path]::GetDirectoryName($trayPath)
$protocolPath = Join-Path $agentDirectory 'TermLink.Protocol.dll'
foreach ($dependency in @(
    (Join-Path $agentDirectory 'BouncyCastle.Cryptography.dll'),
    (Join-Path $agentDirectory 'Google.Protobuf.dll'),
    (Join-Path $agentDirectory 'System.Security.Cryptography.ProtectedData.dll'),
    (Join-Path $agentDirectory 'TermLink.Protocol.dll'),
    (Join-Path $agentDirectory 'TermLink.AgentControl.dll')
)) {
    if (Test-Path -LiteralPath $dependency -PathType Leaf) {
        [void][Reflection.Assembly]::LoadFrom($dependency)
    }
}
$agent = [Reflection.Assembly]::LoadFrom($agentPath)
$protocol = [Reflection.Assembly]::LoadFrom($protocolPath)
$tray = [Reflection.Assembly]::LoadFrom($trayPath)

$publicServerUrl = 'https://termlink.kget.cc:8910'
$lanServerUrl = 'https://termlink.192.168.9.28.sslip.io:9443'
$staticPrivate = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
$instancePrivate = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic
$productionTlsType = $agent.GetType('TermLink.Agent.ProductionTls', $true)
$isAcceptanceHost = $productionTlsType.GetMethod('IsAcceptanceHost', $staticPrivate)
$shouldBypassSystemProxy = $productionTlsType.GetMethod('ShouldBypassSystemProxy', $staticPrivate)
$loadAcceptanceRoot = $productionTlsType.GetMethod('LoadAcceptanceRoot', $staticPrivate)
$validateWithRoot = $productionTlsType.GetMethod('ValidateWithRoot', $staticPrivate)
$keepAliveTimeoutField = $productionTlsType.GetField('WebSocketKeepAliveTimeout', $staticPrivate)
$configureWebSocket = $productionTlsType.GetMethod(
    'ConfigureWebSocket',
    [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public)
Assert-True ($null -ne $isAcceptanceHost) 'Acceptance-host classifier was not found.'
Assert-True ($null -ne $shouldBypassSystemProxy) 'Direct Relay host classifier was not found.'
Assert-True ($null -ne $loadAcceptanceRoot) 'Acceptance root loader was not found.'
Assert-True ($null -ne $validateWithRoot) 'Acceptance certificate validator was not found.'
Assert-True ($null -ne $keepAliveTimeoutField -and
    [TimeSpan]$keepAliveTimeoutField.GetValue($null) -eq [TimeSpan]::FromSeconds(10)) `
    'WebSocket keepalive timeout configuration is not 10 seconds.'
Assert-True ($null -ne $configureWebSocket) 'WebSocket TLS configurator was not found.'

Assert-True ([bool](Invoke-Reflected $shouldBypassSystemProxy $null ([object[]]@($publicServerUrl)))) `
    'The fixed production Relay origin did not bypass the system proxy.'
Assert-True ([bool](Invoke-Reflected $shouldBypassSystemProxy $null ([object[]]@($lanServerUrl)))) `
    'The scoped sslip.io acceptance origin did not bypass the system proxy.'
Assert-True (-not [bool](Invoke-Reflected $shouldBypassSystemProxy $null ([object[]]@('https://example.test')))) `
    'An unrelated HTTPS origin unexpectedly bypassed the system proxy.'

$publicWebSocket = [Net.WebSockets.ClientWebSocket]::new()
try {
    $publicWebSocket.Options.Proxy = [Net.WebProxy]::new('http://127.0.0.1:9')
    [void](Invoke-Reflected $configureWebSocket $null ([object[]]@($publicWebSocket.Options, $publicServerUrl)))
    Assert-True ($null -eq $publicWebSocket.Options.Proxy) 'Production WebSocket retained the system proxy.'
    Assert-True ($null -eq $publicWebSocket.Options.RemoteCertificateValidationCallback) `
        'Production WebSocket installed the disposable acceptance CA validator.'
    Assert-True ($publicWebSocket.Options.KeepAliveInterval -eq [TimeSpan]::FromSeconds(15)) `
        'Production WebSocket keepalive interval is incorrect.'
    $runtimeKeepAliveTimeout = $publicWebSocket.Options.GetType().GetProperty('KeepAliveTimeout')
    if ($null -ne $runtimeKeepAliveTimeout) {
        Assert-True ([TimeSpan]$runtimeKeepAliveTimeout.GetValue($publicWebSocket.Options) -eq
            [TimeSpan]::FromSeconds(10)) 'Production WebSocket keepalive timeout is incorrect.'
    }
}
finally {
    $publicWebSocket.Dispose()
}

foreach ($hostName in @(
    'api.127.0.0.1.sslip.io',
    'termlink.10.0.0.1.sslip.io',
    'termlink.172.16.0.1.sslip.io',
    'termlink.172.31.255.254.sslip.io',
    'termlink.192.168.9.28.sslip.io'
)) {
    $allowed = [bool](Invoke-Reflected $isAcceptanceHost $null ([object[]]@($hostName)))
    Assert-True $allowed "Acceptance CA host was unexpectedly rejected: $hostName"
}
foreach ($hostName in @(
    '127.0.0.1.sslip.io',
    '192.168.9.28',
    'api.192.168.9.28.sslip.io',
    'evil.termlink.192.168.9.28.sslip.io',
    'termlink.172.15.255.255.sslip.io',
    'termlink.172.32.0.1.sslip.io',
    'termlink.192.169.0.1.sslip.io',
    'termlink.8.8.8.8.sslip.io',
    'termlink.192.168.009.028.sslip.io',
    'termlink.kget.cc'
)) {
    $allowed = [bool](Invoke-Reflected $isAcceptanceHost $null ([object[]]@($hostName)))
    Assert-True (-not $allowed) "Acceptance CA escaped its host scope: $hostName"
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Lan-Endpoint-' + [Guid]::NewGuid().ToString('N'))
$resolvedTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$resolvedTemporaryDirectory = [IO.Path]::GetFullPath($temporaryDirectory)
if (-not $resolvedTemporaryDirectory.StartsWith($resolvedTemporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a test directory outside the temporary root: $resolvedTemporaryDirectory"
}
[IO.Directory]::CreateDirectory($resolvedTemporaryDirectory) | Out-Null
$caPath = Join-Path $resolvedTemporaryDirectory 'acceptance-root.cer'
$originalCA = [Environment]::GetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE')
$rootKey = $null
$root = $null
$loadedRoot = $null
try {
    $rootKey = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=TermLink LAN endpoint test root',
        $rootKey,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,
            $true))
    $root = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddMinutes(-1),
        [DateTimeOffset]::UtcNow.AddHours(1))
    [IO.File]::WriteAllBytes(
        $caPath,
        $root.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE', $null)
    $withoutExplicitRoot = Invoke-Reflected $loadAcceptanceRoot $null ([object[]]@($lanServerUrl))
    Assert-True ($null -eq $withoutExplicitRoot) 'The LAN endpoint enabled custom trust without an explicit CA file.'
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE', $caPath)

    $loadedRoot = Invoke-Reflected $loadAcceptanceRoot $null ([object[]]@($lanServerUrl))
    Assert-True ($null -ne $loadedRoot) 'The explicit LAN acceptance CA was not loaded.'
    $loadedRoot.Dispose()
    $loadedRoot = $null
    $webSocket = [Net.WebSockets.ClientWebSocket]::new()
    try {
        $webSocket.Options.Proxy = [Net.WebProxy]::new('http://127.0.0.1:9')
        [void](Invoke-Reflected $configureWebSocket $null ([object[]]@($webSocket.Options, $lanServerUrl)))
        Assert-True ($null -eq $webSocket.Options.Proxy) 'LAN acceptance WebSocket retained the system proxy.'
        Assert-True ($null -ne $webSocket.Options.RemoteCertificateValidationCallback) `
            'LAN acceptance WebSocket did not install the scoped CA validator.'
        Assert-True ($webSocket.Options.KeepAliveInterval -eq [TimeSpan]::FromSeconds(15)) `
            'LAN acceptance WebSocket keepalive interval is incorrect.'
        $runtimeKeepAliveTimeout = $webSocket.Options.GetType().GetProperty('KeepAliveTimeout')
        if ($null -ne $runtimeKeepAliveTimeout) {
            Assert-True ([TimeSpan]$runtimeKeepAliveTimeout.GetValue($webSocket.Options) -eq
                [TimeSpan]::FromSeconds(10)) 'LAN acceptance WebSocket keepalive timeout is incorrect.'
        }
    }
    finally {
        $webSocket.Dispose()
    }
    $publicRoot = Invoke-Reflected $loadAcceptanceRoot $null ([object[]]@($publicServerUrl))
    Assert-True ($null -eq $publicRoot) 'The disposable acceptance CA was applied to the public Release origin.'
    $directIpRoot = Invoke-Reflected $loadAcceptanceRoot $null ([object[]]@('https://192.168.9.28:9443'))
    Assert-True ($null -eq $directIpRoot) 'The acceptance CA was applied to a direct LAN IP outside the scoped sslip.io host.'

    $runtimeType = $agent.GetType('TermLink.Agent.ProductionAgentRuntime', $true)
    $handshakeTimeoutField = $runtimeType.GetField('RealtimeHandshakeTimeout', $staticPrivate)
    Assert-True ($null -ne $handshakeTimeoutField -and
        [TimeSpan]$handshakeTimeoutField.GetValue($null) -eq [TimeSpan]::FromSeconds(15)) `
        'Realtime connection/welcome timeout is not 15 seconds.'
    $connectionFailureCode = $runtimeType.GetMethod('ConnectionFailureCode', $staticPrivate)
    Assert-True ($null -ne $connectionFailureCode) 'Realtime connection failure classifier was not found.'
    Assert-True ((Invoke-Reflected $connectionFailureCode $null ([object[]]@(
        [Net.Sockets.SocketException]::new([int][Net.Sockets.SocketError]::ConnectionRefused)))) -eq
        'CONNECTION_REFUSED') 'Connection-refused failure reason changed.'
    Assert-True ((Invoke-Reflected $connectionFailureCode $null ([object[]]@(
        [TimeoutException]::new('timeout')))) -eq 'CONNECTION_TIMEOUT') `
        'Connection-timeout failure reason changed.'
    Assert-True ((Invoke-Reflected $connectionFailureCode $null ([object[]]@(
        [Security.Authentication.AuthenticationException]::new('tls')))) -eq
        'TLS_CERTIFICATE_FAILED') 'TLS failure reason changed.'
    $tryParseTerminate = $runtimeType.GetMethod('TryParseSessionTerminateRequest', $staticPrivate)
    Assert-True ($null -ne $tryParseTerminate) 'Session terminate request validator was not found.'
    $terminatePayloadType = $protocol.GetType(
        'TermLink.Protocol.Realtime.V1.SessionTerminateRequestPayload',
        $true)
    $terminateRequestId = [Guid]::NewGuid()
    $terminatePayload = [Activator]::CreateInstance($terminatePayloadType)
    $terminatePayload.RequestId = [Google.Protobuf.ByteString]::CopyFrom(
        [Convert]::FromHexString($terminateRequestId.ToString('N')))
    $terminatePayload.ReasonCode = 'USER_REQUESTED'
    $terminateBytes = [Google.Protobuf.MessageExtensions]::ToByteArray($terminatePayload)
    $terminateArguments = [object[]]@($terminateBytes, [Guid]::Empty)
    Assert-True ([bool](Invoke-Reflected $tryParseTerminate $null $terminateArguments)) `
        'Valid Session terminate request was rejected.'
    Assert-True ($terminateArguments[1] -eq $terminateRequestId) `
        'Session terminate request ID changed during validation.'
    $terminatePayload.ReasonCode = 'REMOTE_STOP'
    $invalidReasonBytes = [Google.Protobuf.MessageExtensions]::ToByteArray($terminatePayload)
    $invalidReasonArguments = [object[]]@($invalidReasonBytes, [Guid]::Empty)
    Assert-True (-not [bool](Invoke-Reflected $tryParseTerminate $null $invalidReasonArguments)) `
        'Session terminate request accepted a non-USER_REQUESTED reason.'
    $terminatePayload.ReasonCode = 'USER_REQUESTED'
    $terminatePayload.RequestId = [Google.Protobuf.ByteString]::CopyFrom([byte[]]::new(15))
    $invalidIdBytes = [Google.Protobuf.MessageExtensions]::ToByteArray($terminatePayload)
    $invalidIdArguments = [object[]]@($invalidIdBytes, [Guid]::Empty)
    Assert-True (-not [bool](Invoke-Reflected $tryParseTerminate $null $invalidIdArguments)) `
        'Session terminate request accepted a non-16-byte request ID.'

    $validationArguments = [object[]]::new(4)
    $validationArguments[0] = $root
    $validationArguments[1] = $root
    $validationArguments[2] = $null
    $validationArguments[3] = [Net.Security.SslPolicyErrors]::RemoteCertificateNameMismatch
    $mismatchResult = [bool]$validateWithRoot.Invoke($null, $validationArguments)
    Assert-True (-not $mismatchResult) 'Acceptance CA validation ignored a TLS hostname/SAN mismatch.'

    foreach ($invalidOrigin in @(
        'http://termlink.192.168.9.28.sslip.io:9443',
        'https://termlink.192.168.9.28.sslip.io:9443/api',
        'https://termlink.192.168.9.28.sslip.io:9443?mode=e2e',
        'https://user@termlink.192.168.9.28.sslip.io:9443',
        'https://termlink.192.168.9.28.sslip.io:9443/#fragment'
    )) {
        $rejected = $false
        try {
            $candidate = Invoke-Reflected $loadAcceptanceRoot $null ([object[]]@($invalidOrigin))
            if ($null -ne $candidate) { $candidate.Dispose() }
        }
        catch [ArgumentException] {
            $rejected = $true
        }
        Assert-True $rejected "Invalid server origin was accepted: $invalidOrigin"
    }

    $trayOptionsType = $tray.GetType('TermLink.Tray.TrayOptions', $true)
    $parseTrayOptions = $trayOptionsType.GetMethod('Parse', [Reflection.BindingFlags]'Static,Public')
    $defaultOptions = Parse-TrayOptions $parseTrayOptions ([string[]]@())
    Assert-True ($defaultOptions.ServerUrl -eq $publicServerUrl) 'Tray Release default server origin changed.'
    Assert-True ($null -eq $trayOptionsType.GetProperty('AgentExecutable')) `
        'Tray still models a standalone Agent executable dependency.'
    $legacyAgentOptionRejected = $false
    try {
        [void](Parse-TrayOptions $parseTrayOptions ([string[]]@('--agent-exe', 'TermLink.Agent.exe')))
    }
    catch [ArgumentException] {
        $legacyAgentOptionRejected = $true
    }
    Assert-True $legacyAgentOptionRejected 'Tray still accepts the obsolete --agent-exe product option.'
    $lanOptions = Parse-TrayOptions $parseTrayOptions ([string[]]@('--server-url', ($lanServerUrl + '/')))
    Assert-True ($lanOptions.ServerUrl -eq $lanServerUrl) 'Tray did not normalize the LAN HTTPS origin.'

    foreach ($invalidOrigin in @(
        'http://termlink.192.168.9.28.sslip.io:9443',
        'https://termlink.192.168.9.28.sslip.io:9443/api',
        'https://termlink.192.168.9.28.sslip.io:9443?mode=e2e',
        'https://user@termlink.192.168.9.28.sslip.io:9443'
    )) {
        $rejected = $false
        try {
            [void](Parse-TrayOptions $parseTrayOptions ([string[]]@('--server-url', $invalidOrigin)))
        }
        catch [ArgumentException] {
            $rejected = $true
        }
        Assert-True $rejected "Tray accepted an invalid server origin: $invalidOrigin"
    }

    $controllerType = $tray.GetType('TermLink.Tray.AgentController', $true)
    $controller = [Activator]::CreateInstance(
        $controllerType,
        [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic,
        $null,
        [object[]]@($lanOptions),
        $null)
    $mainWindow = $null
    try {
        Assert-True ($controller.ServerUrl -eq $lanServerUrl) 'AgentController did not retain the Tray LAN origin.'
        Assert-True ($null -eq $controllerType.GetMethod('CreateAccountLinkStartInfo', $instancePrivate)) `
            'AgentController still creates a standalone Agent enrollment process.'
        $linkCompatibility = $controllerType.GetMethod(
            'LinkAccount',
            [Type[]]@([string], [string]))
        Assert-True ($null -ne $linkCompatibility) 'AgentController removed its existing LinkAccount(serverUrl, pcName) entry point.'
        $mismatchedLinkRejected = $false
        try {
            $compatibilityArguments = [object[]]::new(2)
            $compatibilityArguments[0] = $publicServerUrl
            $compatibilityArguments[1] = 'LAN E2E PC'
            [void](Invoke-Reflected $linkCompatibility $controller $compatibilityArguments)
        }
        catch [ArgumentException] {
            $mismatchedLinkRejected = $true
        }
        Assert-True $mismatchedLinkRejected 'AgentController accepted a LinkAccount server different from the Tray origin.'

        $trayProductVersionType = $tray.GetType('TermLink.Tray.TrayProductVersion', $true)
        $fullVersion = [string]$trayProductVersionType.GetProperty(
            'Full',
            [Reflection.BindingFlags]'Static,Public').GetValue($null)
        $versionParts = @($fullVersion.Split('.'))
        Assert-True ($versionParts.Count -eq 4 -and
            @($versionParts | Where-Object { $_ -notmatch '^\d+$' }).Count -eq 0) `
            'Tray display version is not a complete four-part numeric version.'

        $mainWindowType = $tray.GetType('TermLink.Tray.MainWindowForm', $true)
        $mainWindow = [Activator]::CreateInstance(
            $mainWindowType,
            [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic,
            $null,
            [object[]]@($controller),
            $null)
        Assert-True ($mainWindow.Text -eq ('TermLink v' + $fullVersion)) `
            'Main window title does not contain the complete four-part version.'
        [void](Invoke-Reflected $mainWindowType.GetMethod('Navigate') $mainWindow ([object[]]@('connection')))
        $retryButton = $mainWindowType.GetField('connectionRetryButton', $instancePrivate).GetValue($mainWindow)
        Assert-True ($null -ne $retryButton -and
            ($retryButton.AccessibleName.Contains('Retry', [StringComparison]::OrdinalIgnoreCase) -or
                $retryButton.AccessibleName.Contains('重试', [StringComparison]::Ordinal))) `
            'Connection page does not expose an immediate-retry action.'

        $controlAssembly = [AppDomain]::CurrentDomain.GetAssemblies() |
            Where-Object { $_.GetName().Name -eq 'TermLink.AgentControl' } |
            Select-Object -First 1
        $responseType = $controlAssembly.GetType('TermLink.Agent.RegistryResponse', $true)
        $productionStatusType = $controlAssembly.GetType('TermLink.Agent.ProductionStatus', $true)
        $testResponse = [Activator]::CreateInstance($responseType)
        $testResponse.Success = $true
        $testResponse.RemoteAccessEnabled = $true
        $testResponse.Summaries = [Collections.Generic.List[TermLink.Agent.SessionSummary]]::new()
        $testStatus = [Activator]::CreateInstance($productionStatusType)
        $testStatus.Configured = $true
        $testStatus.AccountLinked = $true
        $testStatus.RelayState = [TermLink.Agent.ProductionRelayState]::Reconnecting
        $testStatus.LastErrorCode = 'CONNECTION_REFUSED'
        $testResponse.Production = $testStatus
        [void](Invoke-Reflected $mainWindowType.GetMethod('Apply') $mainWindow ([object[]]@($testResponse)))
        $diagnostics = $mainWindowType.GetField('diagnosticsSummaryLabel', $instancePrivate).GetValue($mainWindow)
        Assert-True ($diagnostics.Text.Contains('[CONNECTION_REFUSED]', [StringComparison]::Ordinal)) `
            'Connection diagnostics do not display the classified failure reason.'
        Assert-True $retryButton.Enabled 'Immediate retry is disabled for a reconnecting linked account.'

        $testStatus.RelayState = [TermLink.Agent.ProductionRelayState]::Failed
        $testStatus.LastErrorCode = 'ACCOUNT_REAUTH_REQUIRED'
        [void](Invoke-Reflected $mainWindowType.GetMethod('Apply') $mainWindow ([object[]]@($testResponse)))
        $identityState = $mainWindowType.GetField('identityState', $instancePrivate).GetValue($mainWindow)
        $accountIdentity = $mainWindowType.GetField('accountIdentityLabel', $instancePrivate).GetValue($mainWindow)
        $activitySummary = $mainWindowType.GetField('activitySummaryLabel', $instancePrivate).GetValue($mainWindow)
        $reauthText = [string]::Join(' ', @(
            $identityState.Text,
            $accountIdentity.Text,
            $activitySummary.Text,
            $retryButton.Text,
            $retryButton.AccessibleName))
        Assert-True ($reauthText.Contains('失效', [StringComparison]::Ordinal) -or
            $reauthText.Contains('sign-in expired', [StringComparison]::OrdinalIgnoreCase)) `
            'Expired credentials are still presented as a generic disconnected Relay.'
        Assert-True ($retryButton.Text.Contains('登录', [StringComparison]::Ordinal) -or
            $retryButton.Text.Contains('sign in', [StringComparison]::OrdinalIgnoreCase)) `
            'The connection action does not change from retry to sign-in after authentication expires.'
        Assert-True $retryButton.Enabled 'The sign-in-again action is disabled after authentication expires.'
        Assert-True ($diagnostics.Text.Contains('[ACCOUNT_REAUTH_REQUIRED]', [StringComparison]::Ordinal)) `
            'Authentication expiry diagnostics lost the stable reason code.'
    }
    finally {
        if ($null -ne $mainWindow) { $mainWindow.Dispose() }
        $controller.Dispose()
    }

    $trayApplicationText = Get-Content -Raw -LiteralPath $trayApplicationSource
    Assert-True ($trayApplicationText.Contains(
        'controller.LoginAccount(dialog.Username, dialog.Password, dialog.PcName)',
        [StringComparison]::Ordinal)) 'Tray login is not routed through Agent Control.'
    Assert-True ($trayApplicationText.Contains(
        'controller.RegisterAccount(dialog.Username, dialog.Email, dialog.Password, dialog.Confirm, dialog.PcName)',
        [StringComparison]::Ordinal)) 'Tray registration is not routed through Agent Control.'
    $productionRuntimeText = Get-Content -Raw -LiteralPath $productionRuntimeSource
    $sessionRegistryText = Get-Content -Raw -LiteralPath $sessionRegistrySource
    Assert-True ($sessionRegistryText.Contains(
        'ProductionEnrollment.LoginDirect(',
        [StringComparison]::Ordinal) -and
        $sessionRegistryText.Contains(
            'ProductionEnrollment.ConfiguredServerUrl()',
            [StringComparison]::Ordinal)) `
        'Agent login does not use the configured Server URL.'
    Assert-True ($sessionRegistryText.Contains(
        'ProductionEnrollment.RegisterDirect(',
        [StringComparison]::Ordinal) -and
        $sessionRegistryText.Contains(
            'ProductionEnrollment.ConfiguredServerUrl()',
            [StringComparison]::Ordinal)) `
        'Agent registration does not use the configured Server URL.'
    Assert-True ($productionRuntimeText.Contains(
        'connectionSupervisor.WaitForRetryOrResumeAsync(',
        [StringComparison]::Ordinal) -and
        $sessionRegistryText.Contains(
            'retryRuntime.RequestReconnect();',
            [StringComparison]::Ordinal)) `
        'Immediate reconnect no longer interrupts the bounded retry delay.'

    $accountLinkType = $tray.GetType('TermLink.Tray.AccountLinkForm', $true)
    $accountRegisterType = $tray.GetType('TermLink.Tray.AccountRegisterForm', $true)
    $linkForm = [Activator]::CreateInstance($accountLinkType, [object[]]@($lanServerUrl))
    $registerForm = [Activator]::CreateInstance($accountRegisterType, [object[]]@($lanServerUrl))
    $defaultLinkForm = [Activator]::CreateInstance($accountLinkType)
    try {
        Assert-True ($linkForm.ServerUrl -eq $lanServerUrl) 'Login dialog does not display the selected LAN origin.'
        Assert-True ($registerForm.ServerUrl -eq $lanServerUrl) 'Registration dialog does not display the selected LAN origin.'
        Assert-True ($defaultLinkForm.ServerUrl -eq $publicServerUrl) 'Default login dialog no longer displays the Release origin.'
        $lanLoginService = @($linkForm.Controls | Where-Object { $_ -is [Windows.Forms.TextBox] -and $_.Text -eq $lanServerUrl })
        $lanRegisterService = @($registerForm.Controls | Where-Object { $_ -is [Windows.Forms.TextBox] -and $_.Text -eq $lanServerUrl })
        Assert-True ($lanLoginService.Count -eq 1 -and $lanLoginService[0].ReadOnly) 'Login dialog LAN service field is incorrect.'
        Assert-True ($lanRegisterService.Count -eq 1 -and $lanRegisterService[0].ReadOnly) 'Registration dialog LAN service field is incorrect.'
    }
    finally {
        $defaultLinkForm.Dispose()
        $registerForm.Dispose()
        $linkForm.Dispose()
    }

    [pscustomobject]@{
        ReleaseDefault = $defaultOptions.ServerUrl
        LanOrigin = $lanOptions.ServerUrl
        ScopedAcceptanceCA = $true
        ExplicitCARequired = $true
        LanWebSocketProxyDisabled = $true
        ProductionWebSocketProxyDisabled = $true
        WebSocketKeepAliveSeconds = 15
        WebSocketKeepAliveTimeoutSeconds = 10
        SessionTerminatePayloadValidated = $true
        RealtimeHandshakeTimeoutSeconds = 15
        ConnectionFailureReasons = $true
        ImmediateReconnectCovered = $true
        FourPartWindowVersion = $fullVersion
        SingleProcessEnrollment = $true
        HostnameMismatchRejected = $true
        InvalidOriginsRejected = $true
        LinkAccountOriginPassed = $true
        LoginAndRegistrationUseConfiguredAgentUrl = $true
        DialogsDisplayOverride = $true
    } | Format-List
    Write-Output 'Production LAN HTTPS endpoint and Tray propagation validation passed.'
}
finally {
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE', $originalCA)
    if ($null -ne $loadedRoot) { $loadedRoot.Dispose() }
    if ($null -ne $root) { $root.Dispose() }
    if ($null -ne $rootKey) { $rootKey.Dispose() }
    if (Test-Path -LiteralPath $resolvedTemporaryDirectory -PathType Container) {
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }
}
