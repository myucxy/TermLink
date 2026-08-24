[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptPath = Join-Path $repositoryRoot 'scripts\Build-Release.ps1'
$schemaPath = Join-Path $PSScriptRoot 'artifact-manifest.schema.json'
$verificationScriptPath = Join-Path $PSScriptRoot 'Verify-LocalRelease.ps1'
$verificationModulePath = Join-Path $PSScriptRoot 'ReleaseVerification.psm1'
$appShipWrapperPath = Join-Path $repositoryRoot 'scripts\Publish-AppShipProduction.ps1'
$setupBuilderPath = Join-Path $repositoryRoot 'win-agent\package\Build-Setup.ps1'
$msixBuilderPath = Join-Path $repositoryRoot 'win-agent\package\Build-Msix.ps1'

$tokens = $null
$parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) `
    ("Build-Release.ps1 parser errors:`n" + (($parseErrors | ForEach-Object { $_.Message }) -join "`n"))
$null = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
$verificationTokens = $null
$verificationParseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile(
    $verificationScriptPath,
    [ref]$verificationTokens,
    [ref]$verificationParseErrors)
Assert-True ($verificationParseErrors.Count -eq 0) `
    ("Verify-LocalRelease.ps1 parser errors:`n" + (($verificationParseErrors | ForEach-Object { $_.Message }) -join "`n"))
$moduleTokens = $null
$moduleParseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile(
    $verificationModulePath,
    [ref]$moduleTokens,
    [ref]$moduleParseErrors)
Assert-True ($moduleParseErrors.Count -eq 0) `
    ("ReleaseVerification.psm1 parser errors:`n" + (($moduleParseErrors | ForEach-Object { $_.Message }) -join "`n"))
$wrapperTokens = $null
$wrapperParseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile(
    $appShipWrapperPath,
    [ref]$wrapperTokens,
    [ref]$wrapperParseErrors)
Assert-True ($wrapperParseErrors.Count -eq 0) `
    ("Publish-AppShipProduction.ps1 parser errors:`n" + (($wrapperParseErrors | ForEach-Object { $_.Message }) -join "`n"))
Import-Module $verificationModulePath -Force
$releaseScriptSource = Get-Content -LiteralPath $scriptPath -Raw
$appShipWrapperSource = Get-Content -LiteralPath $appShipWrapperPath -Raw
$setupBuilderSource = Get-Content -LiteralPath $setupBuilderPath -Raw
$msixBuilderSource = Get-Content -LiteralPath $msixBuilderPath -Raw
Assert-True ($releaseScriptSource.Contains('status --porcelain --untracked-files=normal')) `
    'Server source identity does not inspect the worktree status.'
Assert-True ($releaseScriptSource.Contains('$serverSourceRevision-dirty')) `
    'Server source identity does not distinguish dirty worktrees from clean HEAD.'
Assert-True ($releaseScriptSource.Contains('$finalArtifactRecords')) `
    'Release manifest does not perform a final post-build artifact digest pass.'
Assert-True ($releaseScriptSource.Contains('$pendingManifestPath')) `
    'Production release verification does not stage the manifest before promotion.'
Assert-True ($releaseScriptSource.Contains('ExpectedAndroidSignerSha256')) `
    'Production release orchestration has no Android signer continuity input.'
Assert-True ($releaseScriptSource.Contains('ExpectedWindowsSignerSha1')) `
    'Production release orchestration has no Windows signer continuity input.'
Assert-True ($releaseScriptSource.Contains('AllowHttpWindowsTimestamp')) `
    'Production release orchestration has no explicit HTTP RFC 3161 opt-in.'
Assert-True ($setupBuilderSource.Contains('AllowHttpTimestamp')) `
    'Windows Setup builder has no explicit HTTP RFC 3161 opt-in.'
Assert-True ($msixBuilderSource.Contains('AllowHttpTimestamp')) `
    'Windows MSIX builder has no explicit HTTP RFC 3161 opt-in.'
Assert-True ($releaseScriptSource.Contains("[string]::IsNullOrEmpty(`$timestampUri.UserInfo)")) `
    'Production release orchestration does not reject timestamp URLs containing userinfo.'
Assert-True ($releaseScriptSource.Contains("'ANDROID_SDK_ROOT', 'ANDROID_HOME'")) `
    'AndroidSdkPath is not propagated to the Gradle process environment.'
Assert-True ($appShipWrapperSource.Contains('& $verificationScript @verificationParameters')) `
    'AppShip production wrapper does not rerun the standalone Production verifier.'
Assert-True ($appShipWrapperSource.Contains('$finalManifestSha256 -ceq $manifestSha256')) `
    'AppShip production wrapper does not reject a manifest changed during verification.'
Assert-True ($appShipWrapperSource.Contains('$verificationParameters.RequireWindowsTimestamp = $true')) `
    'AppShip production wrapper does not require Windows timestamp verification.'
Assert-True ($appShipWrapperSource.Contains("SignatureDecision = 'approved'")) `
    'AppShip production wrapper does not bind the reviewed artifact to signature approval.'
Assert-True ($appShipWrapperSource.Contains('[IO.FileShare]::None')) `
    'AppShip production wrapper does not serialize publishing to one application/channel/version state.'

$androidSignerSha256 = 'f374794c07dbc34299d6c141cba4eee034849efc625a031eb893dc5748df021b'
$androidSignatureFixture = @(
    'Verifies',
    'Verified using v1 scheme (JAR signing): false',
    'Verified using v2 scheme (APK Signature Scheme v2): true',
    "Signer #1 certificate SHA-256 digest: $androidSignerSha256",
    'Number of signers: 1'
)
$androidEvidence = Assert-TermLinkAndroidSignerOutput -Output $androidSignatureFixture `
    -ExpectedSha256 $androidSignerSha256
Assert-True ($androidEvidence.signer_sha256 -ceq $androidSignerSha256) `
    'The correct Android continuity fingerprint was not accepted.'
Assert-True (@($androidEvidence.verified_schemes) -contains 2) `
    'The Android verifier did not retain APK Signature Scheme v2 evidence.'
$wrongAndroidRejected = $false
try {
    $null = Assert-TermLinkAndroidSignerOutput -Output $androidSignatureFixture -ExpectedSha256 ('0' * 64)
}
catch {
    $wrongAndroidRejected = $_.Exception.Message.Contains('continuity fingerprint')
}
Assert-True $wrongAndroidRejected 'An incorrect Android signer continuity fingerprint was accepted.'
$v1OnlyRejected = $false
try {
    $null = Assert-TermLinkAndroidSignerOutput -Output @(
        'Verified using v1 scheme (JAR signing): true',
        "Signer #1 certificate SHA-256 digest: $androidSignerSha256"
    ) -ExpectedSha256 $androidSignerSha256
}
catch {
    $v1OnlyRejected = $_.Exception.Message.Contains('v2 or newer')
}
Assert-True $v1OnlyRejected 'An Android APK with only v1 signature evidence was accepted.'
$duplicateSignerRejected = $false
try {
    $null = Assert-TermLinkAndroidSignerOutput -Output @(
        'Verified using v2 scheme (APK Signature Scheme v2): true',
        "Signer #1 certificate SHA-256 digest: $androidSignerSha256",
        "Signer #2 certificate SHA-256 digest: $androidSignerSha256",
        'Number of signers: 2'
    ) -ExpectedSha256 $androidSignerSha256
}
catch {
    $duplicateSignerRejected = $_.Exception.Message.Contains('exactly one signer')
}
Assert-True $duplicateSignerRejected `
    'An APK with two signers using the same certificate fingerprint was accepted.'
$missingSignerCountRejected = $false
try {
    $null = Assert-TermLinkAndroidSignerOutput -Output @(
        'Verified using v2 scheme (APK Signature Scheme v2): true',
        "Signer #1 certificate SHA-256 digest: $androidSignerSha256"
    ) -ExpectedSha256 $androidSignerSha256
}
catch {
    $missingSignerCountRejected = $_.Exception.Message.Contains('missing or ambiguous')
}
Assert-True $missingSignerCountRejected 'APK signer count evidence was optional instead of fail-closed.'

$windowsSignerSha1 = '91ae60bc06d866628b8eb2c62f32bde9dab56466'
$acceptedWindowsSigner = Assert-TermLinkWindowsSignatureEvidence -Status 'Valid' `
    -ActualSha1 $windowsSignerSha1.ToUpperInvariant() -ExpectedSha1 $windowsSignerSha1
Assert-True ($acceptedWindowsSigner -ceq $windowsSignerSha1) `
    'The correct Windows signer continuity fingerprint was not accepted.'
$wrongWindowsRejected = $false
try {
    $null = Assert-TermLinkWindowsSignatureEvidence -Status 'Valid' `
        -ActualSha1 $windowsSignerSha1 -ExpectedSha1 ('0' * 40)
}
catch {
    $wrongWindowsRejected = $_.Exception.Message.Contains('continuity fingerprint')
}
Assert-True $wrongWindowsRejected 'An incorrect Windows signer continuity fingerprint was accepted.'
$invalidWindowsStatusRejected = $false
try {
    $null = Assert-TermLinkWindowsSignatureEvidence -Status 'NotTrusted' `
        -ActualSha1 $windowsSignerSha1 -ExpectedSha1 $windowsSignerSha1
}
catch {
    $invalidWindowsStatusRejected = $_.Exception.Message.Contains("expected 'Valid'")
}
Assert-True $invalidWindowsStatusRejected 'A non-valid Windows Authenticode status was accepted.'
$windowsVersionEvidence = Assert-TermLinkWindowsVersionEvidence `
    -ProductVersion '0.1.20.26+commit' -FileVersion '0.1.20.26' -ExpectedVersion '0.1.20.26'
Assert-True ($windowsVersionEvidence.file_version -ceq '0.1.20.26') `
    'Correct Windows ProductVersion/FileVersion evidence was not accepted.'
$wrongFileVersionRejected = $false
try {
    $null = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion '0.1.20.26+commit' -FileVersion '0.1.20.25' -ExpectedVersion '0.1.20.26'
}
catch { $wrongFileVersionRejected = $_.Exception.Message.Contains('FileVersion') }
Assert-True $wrongFileVersionRejected 'An incorrect Windows Setup FileVersion was accepted.'
$wrongProductVersionRejected = $false
try {
    $null = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion '0.1.20.25+commit' -FileVersion '0.1.20.26' -ExpectedVersion '0.1.20.26'
}
catch { $wrongProductVersionRejected = $_.Exception.Message.Contains('ProductVersion') }
Assert-True $wrongProductVersionRejected 'An incorrect Windows Setup ProductVersion was accepted.'
$extraNumericProductVersionRejected = $false
try {
    $null = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion '0.1.20.26.27' -FileVersion '0.1.20.26' -ExpectedVersion '0.1.20.26'
}
catch { $extraNumericProductVersionRejected = $_.Exception.Message.Contains('ProductVersion') }
Assert-True $extraNumericProductVersionRejected `
    'A Windows Setup ProductVersion with an extra numeric component was accepted.'
$malformedProductMetadataRejected = $false
try {
    $null = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion '0.1.20.26+' -FileVersion '0.1.20.26' -ExpectedVersion '0.1.20.26'
}
catch { $malformedProductMetadataRejected = $_.Exception.Message.Contains('ProductVersion') }
Assert-True $malformedProductMetadataRejected `
    'A Windows Setup ProductVersion with malformed build metadata was accepted.'

$rfc3161Oid = '1.3.6.1.4.1.311.3.3.1'
$legacyCounterSignatureOid = '1.2.840.113549.1.9.6'
$timestampEvidence = Assert-TermLinkRfc3161TimestampEvidence `
    -UnsignedAttributeOids @($rfc3161Oid) -HasTimeStamperCertificate $true
Assert-True ($timestampEvidence.timestamp_oid -ceq $rfc3161Oid) `
    'Valid RFC 3161 timestamp-token evidence was not accepted.'
$legacyTimestampRejected = $false
try {
    $null = Assert-TermLinkRfc3161TimestampEvidence `
        -UnsignedAttributeOids @($legacyCounterSignatureOid) -HasTimeStamperCertificate $true
}
catch { $legacyTimestampRejected = $_.Exception.Message.Contains('legacy Authenticode countersignature') }
Assert-True $legacyTimestampRejected 'A legacy Authenticode countersignature was accepted as RFC 3161.'
$missingTimestampOidRejected = $false
try {
    $null = Assert-TermLinkRfc3161TimestampEvidence `
        -UnsignedAttributeOids @() -HasTimeStamperCertificate $true
}
catch { $missingTimestampOidRejected = $_.Exception.Message.Contains('exactly one RFC 3161') }
Assert-True $missingTimestampOidRejected `
    'A timestamp certificate without an RFC 3161 timestamp-token OID was accepted.'
$unvalidatedTimestampRejected = $false
try {
    $null = Assert-TermLinkRfc3161TimestampEvidence `
        -UnsignedAttributeOids @($rfc3161Oid) -HasTimeStamperCertificate $false
}
catch { $unvalidatedTimestampRejected = $_.Exception.Message.Contains('did not validate') }
Assert-True $unvalidatedTimestampRejected `
    'An RFC 3161 attribute without a Windows-validated timestamper certificate was accepted.'

$probeOutput = Join-Path $repositoryRoot 'artifacts\release\plan-determinism-probe'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    "The side-effect probe path must not exist before the test: $probeOutput"

$commonArguments = @{
    Version = '12.34.56'
    BuildNumber = 321
    ServerImage = $true
    WindowsSetup = $true
    WindowsMsix = $true
    AndroidApk = $true
    AndroidAab = $true
    ReleaseMode = 'Development'
    OutputDirectory = $probeOutput
}
$planOne = (@(& $scriptPath @commonArguments -Plan) -join "`n")
$planTwo = (@(& $scriptPath @commonArguments -Plan) -join "`n")
Assert-True ($planOne -ceq $planTwo) 'Repeated -Plan output is not deterministic.'

$dryRunOne = (@(& $scriptPath @commonArguments -DryRun) -join "`n")
$dryRunTwo = (@(& $scriptPath @commonArguments -DryRun) -join "`n")
Assert-True ($dryRunOne -ceq $dryRunTwo) 'Repeated -DryRun output is not deterministic.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) '-Plan or -DryRun created an output path.'

$planObject = $planOne | ConvertFrom-Json
Assert-True ($planObject.version -ceq '12.34.56') 'The plan did not preserve the shared version.'
Assert-True ($planObject.build_number -eq 321) 'The plan did not preserve the shared build number.'
Assert-True ($planObject.windows_version -ceq '12.34.56.321') 'The Windows version mapping is incorrect.'
Assert-True ($planObject.release_mode -ceq 'development') 'The multi-target plan mode is incorrect.'
Assert-True ($planObject.publish -eq $false) 'The plan unexpectedly enables publishing.'
Assert-True (@($planObject.steps).Count -eq 4) 'The expected target steps were not planned.'

$serverStep = @($planObject.steps | Where-Object { $_.id -eq 'server-image' })[0]
$serverArguments = @($serverStep.arguments)
Assert-True ($serverArguments -contains '--output') 'The server plan does not export a local OCI artifact.'
Assert-True (-not ($serverArguments -contains '--push')) 'The server plan contains a push argument.'
Assert-True ($serverStep.publish -eq $false) 'The server step unexpectedly enables publishing.'

$androidStep = @($planObject.steps | Where-Object { $_.id -eq 'android-release' })[0]
Assert-True (@($androidStep.arguments) -contains '--project-dir') `
    'The Android plan does not anchor Gradle to the Android project directory.'
Assert-True (@($androidStep.arguments) -contains '-PtermlinkVersionCode=321') `
    'The Android build number is not sourced from the shared input.'
Assert-True (@($androidStep.arguments) -contains '-PtermlinkVersionName=12.34.56') `
    'The Android version is not sourced from the shared input.'

$publishRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -ServerImage -Plan -Publish
}
catch {
    $publishRejected = $_.Exception.Message.Contains('Publishing is intentionally disabled')
}
Assert-True $publishRejected '-Publish was not rejected explicitly.'

$productionMsixRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -WindowsMsix -Plan
}
catch {
    $productionMsixRejected = $_.Exception.Message.Contains('Production WindowsMsix is disabled')
}
Assert-True $productionMsixRejected `
    'Production MSIX was accepted without complete post-build identity/signature/timestamp verification.'
$productionAabRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -AndroidAab -Plan
}
catch {
    $productionAabRejected = $_.Exception.Message.Contains('Production AndroidAab is disabled')
}
Assert-True $productionAabRejected `
    'Production AAB was accepted without complete post-build identity/version/signer verification.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    'Unsupported production target preflight created an output path.'

$signingRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -WindowsSetup -OutputDirectory $probeOutput
}
catch {
    $signingRejected = $_.Exception.Message.Contains('Specify exactly one Windows signing source')
}
Assert-True $signingRejected 'Production execution did not fail closed when signing input was absent.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    'Fail-closed production preflight created an output path.'

$storeSigningPlan = & $scriptPath -Version '1.2.3' -BuildNumber 4 -WindowsSetup -Plan `
    -WindowsCertificateThumbprint ('A' * 40) | ConvertFrom-Json
$storeSigningStep = @($storeSigningPlan.steps | Where-Object { $_.id -eq 'windows-setup' })[0]
Assert-True (@($storeSigningStep.arguments) -contains '-CertificateThumbprint') `
    'The Windows setup plan did not represent certificate-store signing.'
Assert-True (-not (@($storeSigningStep.arguments) -contains '-CertificatePassword')) `
    'The certificate-store signing plan still requested a PFX password.'

$serverSigningRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -ServerImage -OutputDirectory $probeOutput
}
catch {
    $serverSigningRejected = $_.Exception.Message.Contains('ServerSigningKeyPath is required')
}
Assert-True $serverSigningRejected 'The production server image did not fail closed without a signing key.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    'Server signing preflight created an output path.'

$androidEnvironmentNames = @(
    'TERMLINK_RELEASE_STORE_FILE', 'TERMLINK_RELEASE_STORE_PASSWORD',
    'TERMLINK_RELEASE_KEY_ALIAS', 'TERMLINK_RELEASE_KEY_PASSWORD'
)
$savedAndroidEnvironment = @{}
$androidSigningRejected = $false
try {
    foreach ($name in $androidEnvironmentNames) {
        $savedAndroidEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    try {
        $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -AndroidApk -OutputDirectory $probeOutput
    }
    catch {
        $androidSigningRejected = $_.Exception.Message.Contains('Android release keystore is required')
    }
}
finally {
    foreach ($name in $androidEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedAndroidEnvironment[$name], 'Process')
    }
}
Assert-True $androidSigningRejected 'The production Android build did not fail closed without a keystore.'
Assert-True (-not (Test-Path -LiteralPath $probeOutput)) `
    'Android signing preflight created an output path.'

$strictPathRejected = $false
try {
    $null = & $scriptPath -Version '1.2.3' -BuildNumber 4 -ServerImage -Plan `
        -OutputDirectory (Join-Path $repositoryRoot 'release\outside-artifacts')
}
catch {
    $strictPathRejected = $_.Exception.Message.Contains('OutputDirectory must be a child')
}
Assert-True $strictPathRejected 'An output path outside artifacts/release was accepted.'

$manifestProbeName = 'manifest-verification-probe-' + [guid]::NewGuid().ToString('N')
$manifestProbeRoot = Join-Path $repositoryRoot "artifacts\release\$manifestProbeName"
[IO.Directory]::CreateDirectory($manifestProbeRoot) | Out-Null
try {
    $probeArtifactPath = Join-Path $manifestProbeRoot 'probe.sig'
    [IO.File]::WriteAllText($probeArtifactPath, 'release-verification-probe', (New-Object Text.UTF8Encoding -ArgumentList $false))
    $probeFile = Get-Item -LiteralPath $probeArtifactPath
    $probeHash = (Get-FileHash -LiteralPath $probeArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $probeManifest = [pscustomobject][ordered]@{
        schema_version = 1
        version = '12.34.56'
        build_number = 321
        release_mode = 'production'
        artifacts = @(
            [pscustomobject][ordered]@{
                path = "artifacts/release/$manifestProbeName/probe.sig"
                size = [int64]$probeFile.Length
                sha256 = $probeHash
                platform = 'generic'
                type = 'signature'
                version = '12.34.56'
            }
        )
    }
    $probeManifestPath = Join-Path $manifestProbeRoot 'artifact-manifest.json'
    [IO.File]::WriteAllText(
        $probeManifestPath,
        (($probeManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $verificationResult = @(& $verificationScriptPath -ReleaseDirectory $manifestProbeRoot `
        -ExpectedReleaseMode Production) -join "`n" | ConvertFrom-Json
    Assert-True ($verificationResult.verified -eq $true -and $verificationResult.release_mode -ceq 'production') `
        'A valid production artifact manifest did not pass the release verifier.'

    $probeManifest.artifacts[0].type = 'aab'
    [IO.File]::WriteAllText(
        $probeManifestPath,
        (($probeManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $unsupportedProductionManifestRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $manifestProbeRoot `
            -ExpectedReleaseMode Production
    }
    catch {
        $unsupportedProductionManifestRejected = $_.Exception.Message.Contains('rejects AAB/MSIX')
    }
    Assert-True $unsupportedProductionManifestRejected `
        'The standalone Production verifier accepted an incompletely verified AAB/MSIX record.'
    $probeManifest.artifacts[0].type = 'signature'

    $probeManifest.release_mode = 'development'
    [IO.File]::WriteAllText(
        $probeManifestPath,
        (($probeManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $developmentManifestRejected = $false
    try {
        $null = & $verificationScriptPath -ReleaseDirectory $manifestProbeRoot -ExpectedReleaseMode Production
    }
    catch {
        $developmentManifestRejected = $_.Exception.Message.Contains("expected 'production'")
    }
    Assert-True $developmentManifestRejected `
        'A development artifact manifest was accepted by the production release verifier.'
}
finally {
    $releaseArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
    $resolvedProbeRoot = [IO.Path]::GetFullPath($manifestProbeRoot)
    $probePrefix = $releaseArtifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedProbeRoot.StartsWith($probePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedProbeRoot).StartsWith('manifest-verification-probe-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedProbeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$appShipProbeVersion = '98.76.54'
$appShipProbeBuild = Get-Random -Minimum 40000 -Maximum 65000
$appShipProbeRoot = Join-Path $repositoryRoot `
    "artifacts\release\$appShipProbeVersion-$appShipProbeBuild"
while (Test-Path -LiteralPath $appShipProbeRoot) {
    $appShipProbeBuild = Get-Random -Minimum 40000 -Maximum 65000
    $appShipProbeRoot = Join-Path $repositoryRoot `
        "artifacts\release\$appShipProbeVersion-$appShipProbeBuild"
}
[IO.Directory]::CreateDirectory($appShipProbeRoot) | Out-Null
try {
    $appShipApkName = "TermLink-Android-$appShipProbeVersion-$appShipProbeBuild.apk"
    $appShipApkPath = Join-Path $appShipProbeRoot $appShipApkName
    [IO.File]::WriteAllText(
        $appShipApkPath,
        'appship-production-gate-probe',
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $appShipApk = Get-Item -LiteralPath $appShipApkPath
    $appShipApkHash = (Get-FileHash -LiteralPath $appShipApkPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $appShipSetupName = "TermLink-Setup-$appShipProbeVersion.$appShipProbeBuild-x64.exe"
    $appShipSetupPath = Join-Path $appShipProbeRoot $appShipSetupName
    [IO.File]::WriteAllText(
        $appShipSetupPath,
        'appship-windows-production-gate-probe',
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $appShipSetup = Get-Item -LiteralPath $appShipSetupPath
    $appShipSetupHash = (Get-FileHash -LiteralPath $appShipSetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $appShipManifest = [pscustomobject][ordered]@{
        schema_version = 1
        version = $appShipProbeVersion
        build_number = $appShipProbeBuild
        release_mode = 'production'
        artifacts = @(
            [pscustomobject][ordered]@{
                path = "artifacts/release/$appShipProbeVersion-$appShipProbeBuild/$appShipApkName"
                size = [int64]$appShipApk.Length
                sha256 = $appShipApkHash
                platform = 'android'
                type = 'apk'
                version = $appShipProbeVersion
            },
            [pscustomobject][ordered]@{
                path = "artifacts/release/$appShipProbeVersion-$appShipProbeBuild/$appShipSetupName"
                size = [int64]$appShipSetup.Length
                sha256 = $appShipSetupHash
                platform = 'windows-x64'
                type = 'setup'
                version = $appShipProbeVersion
            }
        )
    }
    $appShipManifestPath = Join-Path $appShipProbeRoot 'artifact-manifest.json'
    [IO.File]::WriteAllText(
        $appShipManifestPath,
        (($appShipManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $appShipPlanJson = @(& $appShipWrapperPath `
        -ManifestPath $appShipManifestPath `
        -Target AndroidApk `
        -BaseURL 'https://appship.example.test:8910' `
        -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
        -ExpectedAndroidSignerSha256 $androidSignerSha256 `
        -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
        -Plan) -join "`n"
    $appShipPlan = $appShipPlanJson | ConvertFrom-Json
    Assert-True ($appShipPlan.release_mode -ceq 'production' -and $appShipPlan.upload -eq $false) `
        'AppShip production wrapper plan did not preserve the production/no-upload boundary.'
    Assert-True ($appShipPlan.application_slug -ceq 'termlink-android' -and
        $appShipPlan.platform -ceq 'android' -and $appShipPlan.file_kind -ceq 'apk' -and
        $appShipPlan.architecture -ceq 'universal') `
        'AppShip Android target mapping is incorrect.'
    Assert-True ([string]$appShipPlan.artifact_path -ceq [IO.Path]::GetFullPath($appShipApkPath)) `
        'AppShip wrapper did not select the exact manifest artifact path.'
    Assert-True ([string]$appShipPlan.artifact_sha256 -ceq $appShipApkHash) `
        'AppShip wrapper did not retain the exact manifest artifact hash.'
    Assert-True ([IO.Path]::GetFileName([string]$appShipPlan.state_path) -ceq
        "termlink-android-stable-$appShipProbeVersion-build$appShipProbeBuild-signature-approved.json") `
        'AppShip wrapper state path is not unique to application/channel/version.'

    $appShipWindowsPlanJson = @(& $appShipWrapperPath `
        -ManifestPath $appShipManifestPath `
        -Target WindowsSetup `
        -BaseURL 'https://appship.example.test:8910' `
        -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
        -ExpectedAndroidSignerSha256 $androidSignerSha256 `
        -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
        -Plan) -join "`n"
    $appShipWindowsPlan = $appShipWindowsPlanJson | ConvertFrom-Json
    Assert-True ($appShipWindowsPlan.application_slug -ceq 'termlink-win64' -and
        $appShipWindowsPlan.platform -ceq 'windows' -and $appShipWindowsPlan.file_kind -ceq 'exe' -and
        $appShipWindowsPlan.architecture -ceq 'x64') `
        'AppShip Windows target mapping is incorrect.'
    Assert-True ([string]$appShipWindowsPlan.artifact_path -ceq [IO.Path]::GetFullPath($appShipSetupPath) -and
        [string]$appShipWindowsPlan.artifact_sha256 -ceq $appShipSetupHash) `
        'AppShip Windows wrapper did not select the exact manifest path/hash.'
    Assert-True ([IO.Path]::GetFileName([string]$appShipWindowsPlan.state_path) -ceq
        "termlink-win64-stable-$appShipProbeVersion-build$appShipProbeBuild-signature-approved.json") `
        'AppShip Windows state path is not unique to application/channel/version.'

    $appShipManifest.release_mode = 'development'
    [IO.File]::WriteAllText(
        $appShipManifestPath,
        (($appShipManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $developmentPublishRejected = $false
    try {
        $null = & $appShipWrapperPath `
            -ManifestPath $appShipManifestPath `
            -Target AndroidApk `
            -BaseURL 'https://appship.example.test:8910' `
            -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
            -ExpectedAndroidSignerSha256 $androidSignerSha256 `
            -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
            -Plan
    }
    catch { $developmentPublishRejected = $_.Exception.Message.Contains('development artifacts are never publishable') }
    Assert-True $developmentPublishRejected `
        'AppShip wrapper accepted a development artifact manifest.'

    $appShipManifest.release_mode = 'production'
    $appShipManifest.artifacts[0].sha256 = '0' * 64
    [IO.File]::WriteAllText(
        $appShipManifestPath,
        (($appShipManifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine),
        (New-Object Text.UTF8Encoding -ArgumentList $false))
    $wrongPublishHashRejected = $false
    try {
        $null = & $appShipWrapperPath `
            -ManifestPath $appShipManifestPath `
            -Target AndroidApk `
            -BaseURL 'https://appship.example.test:8910' `
            -AndroidSdkPath 'D:\AndroidSdkPlanProbe' `
            -ExpectedAndroidSignerSha256 $androidSignerSha256 `
            -ExpectedWindowsSignerSha1 $windowsSignerSha1 `
            -Plan
    }
    catch { $wrongPublishHashRejected = $_.Exception.Message.Contains('SHA-256') }
    Assert-True $wrongPublishHashRejected `
        'AppShip wrapper accepted artifact bytes that did not match the production manifest.'
}
finally {
    $releaseArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
    $resolvedProbeRoot = [IO.Path]::GetFullPath($appShipProbeRoot)
    $probePrefix = $releaseArtifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedProbeRoot.StartsWith($probePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedProbeRoot).StartsWith(
            "$appShipProbeVersion-",
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedProbeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Build-Release parser, plan, signer-count, version, RFC3161, production-manifest, AppShip-gate, and strict-path checks passed.'
