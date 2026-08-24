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

function ConvertTo-Base64Url {
    param([byte[]]$Bytes)
    return [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function New-TestAccessToken {
    param(
        [string]$Issuer,
        [string]$Subject,
        [string]$Username
    )
    $header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"none"}'))
    $payloadJson = [ordered]@{
        iss = $Issuer
        sub = $Subject
        preferred_username = $Username
        email = "$Username@example.test"
    } | ConvertTo-Json -Compress
    $payload = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($payloadJson))
    return "$header.$payload.test-signature"
}

function Get-StaticMethod {
    param(
        [Type]$Type,
        [string]$Name,
        [Type[]]$Parameters
    )
    $flags = [Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
    $method = $Type.GetMethod($Name, $flags, $null, $Parameters, $null)
    if ($null -eq $method) { throw "Method was not found: $($Type.FullName).$Name" }
    return $method
}

function Assert-ThrowsInvalidOperation {
    param([scriptblock]$Action, [string]$Message)
    $matched = $false
    try {
        & $Action
    }
    catch {
        $error = $_.Exception
        while ($null -ne $error) {
            if ($error -is [InvalidOperationException]) {
                $matched = $true
                break
            }
            $error = $error.InnerException
        }
    }
    Assert-True $matched $Message
}

$resolvedAssembly = [IO.Path]::GetFullPath($AgentAssembly)
if (-not (Test-Path -LiteralPath $resolvedAssembly -PathType Leaf)) {
    throw "Agent assembly not found: $resolvedAssembly"
}
$assembly = [Reflection.Assembly]::LoadFrom($resolvedAssembly)
$configurationType = $assembly.GetType('TermLink.Agent.ProductionAgentConfig', $true)
$credentialsType = $assembly.GetType('TermLink.Agent.ProductionCredentials', $true)
$enrollmentType = $assembly.GetType('TermLink.Agent.ProductionEnrollment', $true)
$storageType = $assembly.GetType('TermLink.Agent.ProductionAccountStorage', $true)
$readAccountKey = Get-StaticMethod $credentialsType 'ReadAccountKey' ([Type[]]@([string], [string]))
$canReuse = Get-StaticMethod $credentialsType 'CanReuseIdentity' ([Type[]]@([string], [string]))
$selectIdentity = Get-StaticMethod $enrollmentType 'SelectIdentity' ([Type[]]@(
    [string], [Guid], [string], [Guid], [string]))
$forgetResolvedRoot = Get-StaticMethod $storageType 'ForgetLocalAccount' ([Type[]]@())
$configPathProperty = $configurationType.GetProperty('DefaultPath', [Reflection.BindingFlags]'Static, Public, NonPublic')
$credentialsPathProperty = $credentialsType.GetProperty('DefaultPath', [Reflection.BindingFlags]'Static, Public, NonPublic')
Assert-True ($null -ne $configPathProperty) 'Production configuration path property was not found.'
Assert-True ($null -ne $credentialsPathProperty) 'Production credentials path property was not found.'

$issuerA = 'https://issuer-a.example.test/oidc'
$issuerB = 'https://issuer-b.example.test/oidc'
$accountAToken = New-TestAccessToken $issuerA 'subject-a' 'same-visible-name'
$accountARenamedToken = New-TestAccessToken ($issuerA + '/') 'subject-a' 'renamed-user'
$accountBToken = New-TestAccessToken $issuerA 'subject-b' 'same-visible-name'
$otherIssuerToken = New-TestAccessToken $issuerB 'subject-a' 'same-visible-name'

$keyA = [string]$readAccountKey.Invoke($null, [object[]]@($accountAToken, $issuerA))
$keyARenamed = [string]$readAccountKey.Invoke($null, [object[]]@($accountARenamedToken, $issuerA))
$keyB = [string]$readAccountKey.Invoke($null, [object[]]@($accountBToken, $issuerA))
$otherIssuerKey = [string]$readAccountKey.Invoke($null, [object[]]@($otherIssuerToken, $issuerB))
$issuerMismatch = $readAccountKey.Invoke($null, [object[]]@($otherIssuerToken, $issuerA))
Assert-True (-not [string]::IsNullOrWhiteSpace($keyA)) 'Issuer+subject did not produce an account key.'
Assert-True ($keyA -eq $keyARenamed) 'Changing username/email changed the stable account key.'
Assert-True ($keyA -ne $keyB) 'Different subjects reused one account key.'
Assert-True ($keyA -ne $otherIssuerKey) 'Different issuers reused one account key.'
Assert-True ($null -eq $issuerMismatch) 'A token from an unexpected issuer was accepted.'
Assert-True ([bool]$canReuse.Invoke($null, [object[]]@($keyA, $keyARenamed))) 'The same account was not reusable.'
Assert-True (-not [bool]$canReuse.Invoke($null, [object[]]@($keyA, $keyB))) 'A different account was reusable.'

$deviceA = [Guid]::Parse('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')
$pcA = [Guid]::Parse('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb')
$sameSelection = $selectIdentity.Invoke($null, [object[]]@($keyA, $deviceA, $keyA, $pcA, $keyARenamed))
Assert-True $sameSelection.ReuseDeviceIdentity 'Same-account re-login did not reuse the device identity.'
Assert-True $sameSelection.ReusePcIdentity 'Same-account re-login did not reuse the PC identity.'
Assert-True ($sameSelection.DeviceId -eq $deviceA) 'Same-account re-login changed DeviceId.'
Assert-True ($sameSelection.PcId -eq $pcA) 'Same-account re-login changed PcId.'

$crossSelection = $selectIdentity.Invoke($null, [object[]]@($keyA, $deviceA, $keyA, $pcA, $keyB))
Assert-True (-not $crossSelection.ReuseDeviceIdentity) 'Cross-account login reused the device identity.'
Assert-True (-not $crossSelection.ReusePcIdentity) 'Cross-account login reused the PC identity.'
Assert-True ($crossSelection.DeviceId -ne [Guid]::Empty -and $crossSelection.DeviceId -ne $deviceA) 'Cross-account login did not rotate DeviceId.'
Assert-True ($crossSelection.PcId -ne [Guid]::Empty -and $crossSelection.PcId -ne $pcA) 'Cross-account login did not rotate PcId.'

$acceptanceRootEnvironment = 'TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT'
$acceptanceCAEnvironment = 'TERMLINK_ACCEPTANCE_CA_FILE'
$originalAcceptanceRoot = [Environment]::GetEnvironmentVariable($acceptanceRootEnvironment)
$originalAcceptanceCA = [Environment]::GetEnvironmentVariable($acceptanceCAEnvironment)
$testRoot = Join-Path 'D:\Dev\Temp' ('termlink-account-session-' + [Guid]::NewGuid().ToString('N'))
$outsideRoot = $testRoot + '-outside'
$termLinkRoot = Join-Path $testRoot 'TermLink'
[IO.Directory]::CreateDirectory($termLinkRoot) | Out-Null
$caPath = Join-Path $testRoot 'acceptance-ca.pem'
$configPath = Join-Path $termLinkRoot 'production-config.json'
$credentialsPath = Join-Path $termLinkRoot 'production-credentials.dpapi'
$unrelatedPath = Join-Path $termLinkRoot 'tray-preferences.json'
$unrelatedDirectory = Join-Path $termLinkRoot 'Agent'
$outsideTermLinkRoot = Join-Path $outsideRoot 'TermLink'
$outsideConfigPath = Join-Path $outsideTermLinkRoot 'production-config.json'
$outsideCredentialsPath = Join-Path $outsideTermLinkRoot 'production-credentials.dpapi'
try {
    [Environment]::SetEnvironmentVariable($acceptanceRootEnvironment, $null)
    [Environment]::SetEnvironmentVariable($acceptanceCAEnvironment, $null)
    $defaultRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $defaultConfigPath = [string]$configPathProperty.GetValue($null)
    $defaultCredentialsPath = [string]$credentialsPathProperty.GetValue($null)
    Assert-True ($defaultConfigPath -eq (Join-Path $defaultRoot 'TermLink\production-config.json')) 'The default production configuration path changed.'
    Assert-True ($defaultCredentialsPath -eq (Join-Path $defaultRoot 'TermLink\production-credentials.dpapi')) 'The default production credentials path changed.'

    [IO.File]::WriteAllText($caPath, 'acceptance test CA marker')
    [Environment]::SetEnvironmentVariable($acceptanceCAEnvironment, $caPath)
    [Environment]::SetEnvironmentVariable($acceptanceRootEnvironment, $testRoot)
    Assert-True ([string]$configPathProperty.GetValue($null) -eq $configPath) 'The acceptance configuration path did not use the isolated root.'
    Assert-True ([string]$credentialsPathProperty.GetValue($null) -eq $credentialsPath) 'The acceptance credentials path did not use the isolated root.'

    [Environment]::SetEnvironmentVariable($acceptanceRootEnvironment, 'relative-account-root')
    Assert-ThrowsInvalidOperation { $configPathProperty.GetValue($null) | Out-Null } 'A relative acceptance account root was accepted.'

    [Environment]::SetEnvironmentVariable($acceptanceRootEnvironment, [IO.Path]::GetPathRoot($testRoot))
    Assert-ThrowsInvalidOperation { $credentialsPathProperty.GetValue($null) | Out-Null } 'A drive-root acceptance account root was accepted.'

    [Environment]::SetEnvironmentVariable($acceptanceRootEnvironment, $testRoot)
    [Environment]::SetEnvironmentVariable($acceptanceCAEnvironment, $null)
    Assert-ThrowsInvalidOperation { $configPathProperty.GetValue($null) | Out-Null } 'An acceptance account root without an acceptance CA was accepted.'

    [Environment]::SetEnvironmentVariable($acceptanceCAEnvironment, $caPath)
    [IO.File]::WriteAllText($configPath, 'config')
    [IO.File]::WriteAllText($credentialsPath, 'credentials')
    [IO.File]::WriteAllText($unrelatedPath, 'keep')
    [IO.Directory]::CreateDirectory($unrelatedDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $unrelatedDirectory 'keep.txt'), 'keep')
    [IO.Directory]::CreateDirectory($outsideTermLinkRoot) | Out-Null
    [IO.File]::WriteAllText($outsideConfigPath, 'outside config')
    [IO.File]::WriteAllText($outsideCredentialsPath, 'outside credentials')
    $forgetResolvedRoot.Invoke($null, [object[]]@()) | Out-Null
    Assert-True (-not (Test-Path -LiteralPath $configPath)) 'Sign-out retained production-config.json.'
    Assert-True (-not (Test-Path -LiteralPath $credentialsPath)) 'Sign-out retained production-credentials.dpapi.'
    Assert-True (Test-Path -LiteralPath $unrelatedPath -PathType Leaf) 'Sign-out deleted unrelated TermLink data.'
    Assert-True (Test-Path -LiteralPath (Join-Path $unrelatedDirectory 'keep.txt') -PathType Leaf) 'Sign-out recursively deleted Agent data.'
    Assert-True (Test-Path -LiteralPath $outsideConfigPath -PathType Leaf) 'Sign-out deleted configuration outside the isolated root.'
    Assert-True (Test-Path -LiteralPath $outsideCredentialsPath -PathType Leaf) 'Sign-out deleted credentials outside the isolated root.'
}
finally {
    [Environment]::SetEnvironmentVariable($acceptanceRootEnvironment, $originalAcceptanceRoot)
    [Environment]::SetEnvironmentVariable($acceptanceCAEnvironment, $originalAcceptanceCA)
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $outsideRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$configurationSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\Agent\ProductionConfiguration.cs') -Raw
$programSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\Tray\Program.cs') -Raw
$traySource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\src\Tray\TrayApplicationContext.cs') -Raw
Assert-True ($configurationSource.Contains('ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser)')) 'Credential persistence no longer uses DPAPI CurrentUser.'
Assert-True ($configurationSource.Contains('TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT')) 'Acceptance account root override is missing.'
Assert-True ($configurationSource.Contains('TERMLINK_ACCEPTANCE_CA_FILE')) 'Acceptance account root is not gated by the acceptance CA.'
Assert-True (-not [regex]::IsMatch($configurationSource, 'class Stored[\s\S]*?public\s+string\s+Password\s*\{')) 'A password field was added to persisted credentials.'
Assert-True ($configurationSource.Contains('File.Delete(path)')) 'Account clearing does not delete exact known files.'
Assert-True (-not $configurationSource.Contains('Directory.Delete(termLinkRoot')) 'Account clearing recursively deletes the TermLink directory.'

$signOutIndex = $programSource.IndexOf('public RegistryResponse ForgetLocalAccountAndRestart()', [StringComparison]::Ordinal)
$shutdownIndex = $programSource.IndexOf('ShutdownAndWaitCore();', $signOutIndex, [StringComparison]::Ordinal)
$forgetIndex = $programSource.IndexOf('ProductionEnrollment.ForgetLocalAccount();', $shutdownIndex, [StringComparison]::Ordinal)
$restartIndex = $programSource.IndexOf('response = EnsureAgentCore();', $forgetIndex, [StringComparison]::Ordinal)
Assert-True ($signOutIndex -ge 0 -and $shutdownIndex -gt $signOutIndex -and $forgetIndex -gt $shutdownIndex -and $restartIndex -gt $forgetIndex) 'Sign-out does not stop, clear, and restart the Agent in order.'
Assert-True ($programSource.Contains('response.Production.Configured || response.Production.AccountLinked')) 'Sign-out does not verify that the restarted Agent is logged out.'

$exitStart = $traySource.IndexOf('private async void ExitTray()', [StringComparison]::Ordinal)
$exitEnd = $traySource.IndexOf('protected override void Dispose', $exitStart, [StringComparison]::Ordinal)
Assert-True ($exitStart -ge 0 -and $exitEnd -gt $exitStart) 'ExitTray implementation was not found.'
$exitBody = $traySource.Substring($exitStart, $exitEnd - $exitStart)
Assert-True ($exitBody.Contains('You will remain signed in.')) 'Exit tray does not tell the user that login is retained.'
Assert-True (-not $exitBody.Contains('ForgetLocalAccount')) 'Exit tray incorrectly clears the local account.'

Write-Output 'Account session identity, rotation, sign-out clearing, and exit-tray retention validation passed.'
