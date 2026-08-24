[CmdletBinding()]
param(
    [string]$AgentAssembly = (Join-Path $PSScriptRoot '..\src\Agent\bin\Release\net8.0-windows\TermLink.Agent.dll')
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

$sourcePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Agent\ProductionAgentRuntime.cs'))
$resolvedAssembly = [IO.Path]::GetFullPath($AgentAssembly)
if (-not (Test-Path -LiteralPath $resolvedAssembly -PathType Leaf)) {
    throw "Release Agent assembly is unavailable: $resolvedAssembly"
}
if ((Get-Item -LiteralPath $resolvedAssembly).LastWriteTimeUtc -lt
    (Get-Item -LiteralPath $sourcePath).LastWriteTimeUtc) {
    throw "Release Agent assembly is older than ProductionAgentRuntime.cs; build TermLink.Agent in Release first."
}

$source = Get-Content -Raw -LiteralPath $sourcePath
Assert-True (-not $source.Contains('active PC grant', [StringComparison]::OrdinalIgnoreCase)) `
    'Production subscriber rejection still requires an active PC grant.'

$assemblyDirectory = [IO.Path]::GetDirectoryName($resolvedAssembly)
foreach ($dependency in @(
    'BouncyCastle.Cryptography.dll',
    'Google.Protobuf.dll',
    'System.Security.Cryptography.ProtectedData.dll',
    'TermLink.Protocol.dll',
    'TermLink.AgentControl.dll'
)) {
    $dependencyPath = Join-Path $assemblyDirectory $dependency
    if (Test-Path -LiteralPath $dependencyPath -PathType Leaf) {
        [void][Reflection.Assembly]::LoadFrom($dependencyPath)
    }
}

$assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
$runtimeType = $assembly.GetType('TermLink.Agent.ProductionAgentRuntime', $true)
$protocolAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $assemblyDirectory 'TermLink.Protocol.dll'))
$exchangeType = $protocolAssembly.GetType('TermLink.Protocol.DeviceExchangeKeyPair', $true)
$flags = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$parseRecipients = $runtimeType.GetMethod('ParseAuthorizedRecipientKeys', $flags)
$createSubscriber = $runtimeType.GetMethod('CreateAuthorizedSubscriber', $flags)
$retireSubscriber = $runtimeType.GetMethod('RetireSubscriber', $flags)
Assert-True ($null -ne $parseRecipients) 'Authorized-recipient parser was not found.'
Assert-True ($null -ne $createSubscriber) 'Authorized-recipient subscriber factory was not found.'

$exchangeConstructor = $exchangeType.GetConstructor([Type[]]@([byte[]]))
Assert-True ($null -ne $exchangeConstructor) 'Device exchange-key constructor was not found.'
$localPrivateKey = [byte[]]::new(32)
$remotePrivateKey = [byte[]]::new(32)
for ($index = 0; $index -lt 32; $index++) {
    $localPrivateKey[$index] = [byte]($index + 1)
    $remotePrivateKey[$index] = [byte](255 - $index)
}
$localConstructorArguments = [object[]]::new(1)
$localConstructorArguments[0] = $localPrivateKey
$remoteConstructorArguments = [object[]]::new(1)
$remoteConstructorArguments[0] = $remotePrivateKey
$localExchange = $exchangeConstructor.Invoke($localConstructorArguments)
$remoteExchange = $exchangeConstructor.Invoke($remoteConstructorArguments)
$document = $null
$subscriber = $null
try {
    $pcDeviceId = [Guid]'11111111-1111-4111-8111-111111111111'
    $androidDeviceId = [Guid]'22222222-2222-4222-8222-222222222222'
    $keyEpoch = [long]7
    $snapshot = [ordered]@{
        pc_id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
        key_epoch = $keyEpoch
        items = @(
            [ordered]@{
                device_id = $androidDeviceId.ToString('D')
                device_type = 'ANDROID'
                device_name = 'Same-account Android'
                exchange_public_key = [Convert]::ToBase64String([byte[]]$remoteExchange.PublicKey)
                key_id = 'same-account-android-key'
                authorization_source = 'ACCOUNT'
            },
            [ordered]@{
                device_id = '33333333-3333-4333-8333-333333333333'
                device_type = 'WINDOWS_PC'
                exchange_public_key = [Convert]::ToBase64String([byte[]]$localExchange.PublicKey)
            }
        )
    }
    $json = $snapshot | ConvertTo-Json -Depth 5 -Compress
    $document = [Text.Json.JsonDocument]::Parse(
        [string]$json,
        [Text.Json.JsonDocumentOptions]::new())
    $parseArguments = [object[]]::new(3)
    $parseArguments[0] = $document.RootElement
    $parseArguments[1] = $keyEpoch
    $parseArguments[2] = [long]0
    $authorizedKeys = Invoke-Reflected $parseRecipients $null $parseArguments

    Assert-True ($authorizedKeys.Count -eq 1) `
        'The account authorization snapshot did not retain exactly one Android recipient.'
    Assert-True ([long]$parseArguments[2] -eq $keyEpoch) `
        'The account authorization snapshot did not preserve the authoritative key epoch.'
    Assert-True ($authorizedKeys.ContainsKey($androidDeviceId)) `
        'The same-account Android recipient was not accepted without a permission/grant field.'

    $staleEpochArguments = [object[]]::new(3)
    $staleEpochArguments[0] = $document.RootElement
    $staleEpochArguments[1] = $keyEpoch + 1
    $staleEpochArguments[2] = [long]0
    $staleEpochRejected = $false
    try {
        [void](Invoke-Reflected $parseRecipients $null $staleEpochArguments)
    }
    catch [IO.InvalidDataException] {
        $staleEpochRejected = $true
    }
    Assert-True $staleEpochRejected 'A stale recipient key epoch was accepted.'

    $indexArguments = [object[]]::new(1)
    $indexArguments[0] = $androidDeviceId
    $authorizedPublicKey = [byte[]]$authorizedKeys.GetType().GetProperty('Item').GetValue(
        $authorizedKeys,
        $indexArguments)

    $subscriberArguments = [object[]]::new(5)
    $subscriberArguments[0] = $localExchange
    $subscriberArguments[1] = $pcDeviceId
    $subscriberArguments[2] = $androidDeviceId
    $subscriberArguments[3] = $authorizedPublicKey
    $subscriberArguments[4] = $keyEpoch
    $subscriber = Invoke-Reflected $createSubscriber $null $subscriberArguments
    $subscriberType = $subscriber.GetType()
    $instanceFields = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Instance
    $subscriberDeviceId = [Guid]$subscriberType.GetField('DeviceId', $instanceFields).GetValue($subscriber)
    $subscriberEpoch = [long]$subscriberType.GetField('KeyEpoch', $instanceFields).GetValue($subscriber)
    $subscriberExchangeKey = [byte[]]$subscriberType.GetField('ExchangePublicKey', $instanceFields).GetValue($subscriber)
    $subscriberPairwiseKey = [byte[]]$subscriberType.GetField('PairwiseKey', $instanceFields).GetValue($subscriber)
    Assert-True ($subscriberDeviceId -eq $androidDeviceId) 'Subscriber identity changed while consuming the authorized list.'
    Assert-True ($subscriberEpoch -eq $keyEpoch) 'Subscriber key epoch changed while consuming the authorized list.'
    Assert-True ($subscriberExchangeKey.Length -eq 32) 'Subscriber exchange-key validation was weakened.'
    Assert-True ($subscriberPairwiseKey.Length -eq 32) 'Subscriber pairwise key was not derived.'

    $sameDeviceArguments = [object[]]::new(5)
    [Array]::Copy($subscriberArguments, $sameDeviceArguments, 5)
    $sameDeviceArguments[2] = $pcDeviceId
    $sameDeviceRejected = $false
    try {
        [void](Invoke-Reflected $createSubscriber $null $sameDeviceArguments)
    }
    catch [IO.InvalidDataException] {
        $sameDeviceRejected = $true
    }
    Assert-True $sameDeviceRejected 'PC/recipient identity separation was not enforced.'

    [pscustomobject]@{
        SameAccountRecipientParsed = $true
        ExplicitGrantRequired = $false
        SubscriberCreated = $true
        KeyEpoch = $subscriberEpoch
        ExchangeKeyBytes = $subscriberExchangeKey.Length
        PairwiseKeyBytes = $subscriberPairwiseKey.Length
        StaleKeyEpochRejected = $staleEpochRejected
        PcRecipientIdentitySeparated = $sameDeviceRejected
    } | Format-List
    Write-Output 'Same-account production recipient parsing and subscriber creation validation passed.'
}
finally {
    if ($null -ne $subscriber -and $null -ne $retireSubscriber) {
        $retireArguments = [object[]]::new(1)
        $retireArguments[0] = $subscriber
        [void](Invoke-Reflected $retireSubscriber $null $retireArguments)
    }
    if ($null -ne $document) { $document.Dispose() }
    if ($null -ne $remoteExchange) { $remoteExchange.Dispose() }
    if ($null -ne $localExchange) { $localExchange.Dispose() }
    [Array]::Clear($remotePrivateKey, 0, $remotePrivateKey.Length)
    [Array]::Clear($localPrivateKey, 0, $localPrivateKey.Length)
}
