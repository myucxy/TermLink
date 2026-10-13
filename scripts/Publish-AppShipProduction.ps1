[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][ValidateSet('AndroidApk', 'WindowsSetup')][string]$Target,
    [Parameter(Mandatory)][string]$BaseURL,
    [PSCredential]$Credential,
    [string]$PublishScriptPath,
    [string]$StatePath,
    [string]$Title,
    [AllowEmptyString()][string]$ReleaseNotes = '',
    [ValidateSet('stable', 'preview')]
    [string]$Channel = 'stable',
    [string]$AndroidSdkPath,
    [ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedAndroidSignerSha256,
    [ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$ExpectedWindowsSignerSha1,
    [switch]$SignatureApproved,
    [string]$SignatureReviewNote,
    [switch]$Resume,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-PublishCondition {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Test-StrictChildPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )

    $separator = [IO.Path]::DirectorySeparatorChar
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + $separator
    $fullCandidate = [IO.Path]::GetFullPath($Candidate)
    return $fullCandidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try { $hashBytes = $sha256.ComputeHash($stream) }
        finally { $sha256.Dispose() }
    }
    finally { $stream.Dispose() }
    return ([BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
$appShipStateRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.appship'))
$verificationScript = Join-Path $repositoryRoot 'release\Verify-LocalRelease.ps1'
$verificationModule = Join-Path $repositoryRoot 'release\ReleaseVerification.psm1'
Assert-PublishCondition (Test-Path -LiteralPath $verificationScript -PathType Leaf) `
    "Production verifier not found: $verificationScript"
Assert-PublishCondition (Test-Path -LiteralPath $verificationModule -PathType Leaf) `
    "Production verification module not found: $verificationModule"
Import-Module $verificationModule -Force

$manifestCandidate = $ManifestPath
if (-not [IO.Path]::IsPathRooted($manifestCandidate)) {
    $manifestCandidate = Join-Path $repositoryRoot $manifestCandidate
}
$resolvedManifestPath = [IO.Path]::GetFullPath($manifestCandidate)
Assert-PublishCondition (Test-StrictChildPath -Root $releaseRoot -Candidate $resolvedManifestPath) `
    'ManifestPath must be inside the repository artifacts/release directory.'
Assert-PublishCondition ([IO.Path]::GetFileName($resolvedManifestPath) -ceq 'artifact-manifest.json') `
    'Only a promoted artifact-manifest.json can be published; pending or renamed manifests are rejected.'
Assert-PublishCondition (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf) `
    "Production artifact manifest not found: $resolvedManifestPath"

$manifestSha256 = Get-Sha256Hex -Path $resolvedManifestPath
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
Assert-PublishCondition ([int]$manifest.schema_version -eq 1) 'Artifact manifest schema_version must be 1.'
Assert-PublishCondition ([string]$manifest.release_mode -ceq 'production') `
    'AppShip publishing requires a production artifact manifest; development artifacts are never publishable.'
$version = [string]$manifest.version
$buildNumber = [int]$manifest.build_number
Assert-PublishCondition ($version -match '^\d+\.\d+\.\d+$') 'Artifact manifest version is invalid.'
Assert-PublishCondition ($buildNumber -ge 1 -and $buildNumber -le 65535) 'Artifact manifest build number is invalid.'

$expectedReleaseDirectory = [IO.Path]::GetFullPath((Join-Path $releaseRoot "$version-$buildNumber"))
$actualReleaseDirectory = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($resolvedManifestPath))
Assert-PublishCondition ([string]::Equals(
        $actualReleaseDirectory,
        $expectedReleaseDirectory,
        [StringComparison]::OrdinalIgnoreCase)) `
    "Production manifest must be stored in artifacts/release/$version-$buildNumber."

$artifactRecords = @($manifest.artifacts)
Assert-PublishCondition ($artifactRecords.Count -gt 0) 'Artifact manifest contains no artifacts.'
$unsupportedProductionRecords = @($artifactRecords | Where-Object { $_.type -eq 'msix' })
Assert-PublishCondition ($unsupportedProductionRecords.Count -eq 0) `
    'Production AppShip publishing rejects MSIX until complete post-build verification is implemented.'
$unexpectedRecordTypes = @($artifactRecords | Where-Object { $_.type -notin @('apk', 'aab', 'setup') })
Assert-PublishCondition ($unexpectedRecordTypes.Count -eq 0) `
    'This AppShip production wrapper accepts client manifests containing APK, AAB, and/or Windows Setup only.'
$apkRecords = @($artifactRecords | Where-Object { $_.type -ceq 'apk' })
$setupRecords = @($artifactRecords | Where-Object { $_.type -ceq 'setup' })
Assert-PublishCondition ($apkRecords.Count -le 1) `
    'Production manifest must not contain more than one APK.'
Assert-PublishCondition ($setupRecords.Count -le 1) `
    'Production manifest must not contain more than one Windows Setup.'

$targetType = if ($Target -eq 'AndroidApk') { 'apk' } else { 'setup' }
$targetRecords = @($artifactRecords | Where-Object { $_.type -ceq $targetType })
Assert-PublishCondition ($targetRecords.Count -eq 1) `
    "Production manifest must contain exactly one $targetType artifact for target $Target."
$targetRecord = $targetRecords[0]

$expectedFileName = if ($Target -eq 'AndroidApk') {
    "TermLink-Android-$version-$buildNumber.apk"
}
else {
    "TermLink-Setup-$version.$buildNumber-x64.exe"
}
$expectedRelativePath = "artifacts/release/$version-$buildNumber/$expectedFileName"
Assert-PublishCondition ([string]$targetRecord.path -ceq $expectedRelativePath) `
    "AppShip target path must exactly match the production convention: $expectedRelativePath"
$expectedPlatform = if ($Target -eq 'AndroidApk') { 'android' } else { 'windows-x64' }
Assert-PublishCondition ([string]$targetRecord.platform -ceq $expectedPlatform) `
    "AppShip target platform must be '$expectedPlatform'."
Assert-PublishCondition ([string]$targetRecord.version -ceq $version) `
    'AppShip target version does not match the production manifest.'

$artifactPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot (([string]$targetRecord.path).Replace('/', '\'))))
Assert-PublishCondition (Test-StrictChildPath -Root $actualReleaseDirectory -Candidate $artifactPath) `
    'AppShip target escaped the production release directory.'
Assert-PublishCondition (Test-Path -LiteralPath $artifactPath -PathType Leaf) `
    "AppShip target artifact not found: $artifactPath"
$artifactFile = Get-Item -LiteralPath $artifactPath
$artifactSha256 = Get-Sha256Hex -Path $artifactPath
Assert-PublishCondition ($artifactFile.Length -eq [int64]$targetRecord.size) `
    'AppShip target size does not match the production manifest.'
Assert-PublishCondition ($artifactSha256 -ceq [string]$targetRecord.sha256) `
    'AppShip target SHA-256 does not match the production manifest.'

$hasApk = $apkRecords.Count -eq 1
$hasSetup = $setupRecords.Count -eq 1
if ($hasApk) {
    Assert-PublishCondition (-not [string]::IsNullOrWhiteSpace($AndroidSdkPath)) `
        'AndroidSdkPath is required to re-verify a production manifest containing an APK.'
    Assert-PublishCondition (-not [string]::IsNullOrWhiteSpace($ExpectedAndroidSignerSha256)) `
        'ExpectedAndroidSignerSha256 is required to re-verify a production manifest containing an APK.'
}
if ($hasSetup) {
    Assert-PublishCondition (-not [string]::IsNullOrWhiteSpace($ExpectedWindowsSignerSha1)) `
        'ExpectedWindowsSignerSha1 is required to re-verify a production manifest containing Windows Setup.'
}

$baseUri = $null
$validBaseUrl = [Uri]::TryCreate($BaseURL, [UriKind]::Absolute, [ref]$baseUri) -and
    $baseUri.Scheme -eq 'https' -and [string]::IsNullOrWhiteSpace($baseUri.UserInfo) -and
    [string]::IsNullOrWhiteSpace($baseUri.Query) -and [string]::IsNullOrWhiteSpace($baseUri.Fragment) -and
    ($baseUri.AbsolutePath -eq '/' -or [string]::IsNullOrWhiteSpace($baseUri.AbsolutePath))
Assert-PublishCondition $validBaseUrl 'Production AppShip BaseURL must be an absolute HTTPS URL without credentials.'

$applicationSlug = if ($Target -eq 'AndroidApk') {
    if ($Channel -eq 'preview') { 'termlink-android-preview' } else { 'termlink-android' }
}
else {
    if ($Channel -eq 'preview') { 'termlink-win64-preview' } else { 'termlink-win64' }
}
$appShipChannel = if ($Channel -eq 'preview') { 'beta' } else { 'stable' }
$createApplicationIfMissing = $Channel -eq 'preview'
$previewApplicationName = if ($Target -eq 'AndroidApk') {
    'TermLink Android Preview'
} else {
    'TermLink Windows Preview'
}
$previewApplicationSummary = if ($Target -eq 'AndroidApk') {
    'Private non-stable TermLink Android preview releases.'
} else {
    'Private non-stable TermLink Windows preview releases.'
}
$platform = if ($Target -eq 'AndroidApk') { 'android' } else { 'windows' }
$fileKind = if ($Target -eq 'AndroidApk') { 'apk' } else { 'exe' }
$architecture = if ($Target -eq 'AndroidApk') { 'universal' } else { 'x64' }
$signerFingerprint = if ($Target -eq 'AndroidApk') {
    ConvertTo-TermLinkNormalizedFingerprint -Value $ExpectedAndroidSignerSha256 -Length 64 `
        -Description 'Android signer certificate SHA-256 fingerprint'
}
else {
    ConvertTo-TermLinkNormalizedFingerprint -Value $ExpectedWindowsSignerSha1 -Length 40 `
        -Description 'Windows Setup Authenticode signer SHA-1 fingerprint'
}

$expectedStatePath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot `
    ".appship\$applicationSlug-$Channel-$version-build$buildNumber-signature-approved.json"))
if ([string]::IsNullOrWhiteSpace($StatePath)) {
    $resolvedStatePath = $expectedStatePath
}
else {
    $stateCandidate = $StatePath
    if (-not [IO.Path]::IsPathRooted($stateCandidate)) { $stateCandidate = Join-Path $repositoryRoot $stateCandidate }
    $resolvedStatePath = [IO.Path]::GetFullPath($stateCandidate)
    Assert-PublishCondition (Test-StrictChildPath -Root $appShipStateRoot -Candidate $resolvedStatePath) `
        'StatePath must remain inside the repository .appship directory.'
    Assert-PublishCondition ([string]::Equals(
            $resolvedStatePath,
            $expectedStatePath,
            [StringComparison]::OrdinalIgnoreCase)) `
        "StatePath must be unique to this application/channel/version: $expectedStatePath"
}

$effectiveTitle = if ([string]::IsNullOrWhiteSpace($Title)) {
    "TermLink $Target $version"
}
else { $Title.Trim() }

$publishPlan = [pscustomobject][ordered]@{
    schema_version = 1
    operation = if ($Plan) { 'plan' } else { 'publish' }
    manifest = $resolvedManifestPath
    release_mode = 'production'
    verification_on_publish = 'Verify-LocalRelease.ps1'
    full_verification_performed = $false
    application_slug = $applicationSlug
    artifact_path = $artifactPath
    artifact_sha256 = $artifactSha256
    platform = $platform
    file_kind = $fileKind
    architecture = $architecture
    release_track = $Channel
    channel = $appShipChannel
    create_application_if_missing = $createApplicationIfMissing
    version_name = $version
    build_number = $buildNumber
    state_path = $resolvedStatePath
    resume = [bool]$Resume
    upload = -not [bool]$Plan
}
if ($Plan) {
    $publishPlan | ConvertTo-Json -Depth 6
    return
}

Assert-PublishCondition ($PSVersionTable.PSVersion -ge [Version]'7.2') `
    'Actual AppShip publishing requires PowerShell 7.2 or newer; use -Plan for PowerShell 5.1 validation.'
Assert-PublishCondition ($null -ne $Credential) `
    'A protected AppShip PSCredential is required; passwords in command arguments or files are not accepted.'
Assert-PublishCondition $SignatureApproved `
    'SignatureApproved is required after an authorized reviewer verifies the exact artifact and fingerprint.'
Assert-PublishCondition (-not [string]::IsNullOrWhiteSpace($SignatureReviewNote)) `
    'SignatureReviewNote is required for the AppShip signature-review record.'
Assert-PublishCondition (-not [string]::IsNullOrWhiteSpace($PublishScriptPath)) `
    'PublishScriptPath must point to the reviewed appship-publish client.'
$publishScriptCandidate = $PublishScriptPath
if (-not [IO.Path]::IsPathRooted($publishScriptCandidate)) {
    $publishScriptCandidate = Join-Path $repositoryRoot $publishScriptCandidate
}
$resolvedPublishScript = [IO.Path]::GetFullPath($publishScriptCandidate)
Assert-PublishCondition (Test-Path -LiteralPath $resolvedPublishScript -PathType Leaf) `
    "Reviewed AppShip publish client not found: $resolvedPublishScript"
$publishScriptSource = Get-Content -LiteralPath $resolvedPublishScript -Raw
$requiredClientMarkers = @(
    '#Requires -Version 7.2',
    'schemaVersion = ''appship.publish-state/v1''',
    'Idempotency-Key',
    'SignatureDecision'
)
foreach ($marker in $requiredClientMarkers) {
    Assert-PublishCondition ($publishScriptSource.Contains($marker)) `
        'PublishScriptPath does not identify the reviewed resumable appship-publish client.'
}

$verificationParameters = @{
    ReleaseDirectory = $actualReleaseDirectory
    ManifestPath = $resolvedManifestPath
    ExpectedReleaseMode = 'Production'
    RequireSourceIdentity = $true
}
if ($hasApk) {
    $verificationParameters.AndroidSdkPath = $AndroidSdkPath
    $verificationParameters.ExpectedAndroidSignerSha256 = $ExpectedAndroidSignerSha256
}
if ($hasSetup) {
    $verificationParameters.ExpectedWindowsSignerSha1 = $ExpectedWindowsSignerSha1
    $verificationParameters.RequireWindowsTimestamp = $true
}
$verificationJson = @(& $verificationScript @verificationParameters) -join "`n"
$verificationResult = $verificationJson | ConvertFrom-Json
Assert-PublishCondition ($verificationResult.verified -eq $true -and
        [string]$verificationResult.release_mode -ceq 'production' -and
        [string]$verificationResult.version -ceq $version -and
        [int]$verificationResult.build_number -eq $buildNumber) `
    'Production verifier returned inconsistent release evidence.'

# Recheck both the promoted manifest and selected bytes after the full verifier and
# immediately before handing the exact artifact path to the reviewed AppShip client.
$finalManifestSha256 = Get-Sha256Hex -Path $resolvedManifestPath
Assert-PublishCondition ($finalManifestSha256 -ceq $manifestSha256) `
    'Production manifest changed during AppShip verification.'
$finalFile = Get-Item -LiteralPath $artifactPath
$finalSha256 = Get-Sha256Hex -Path $artifactPath
Assert-PublishCondition ($finalFile.Length -eq [int64]$targetRecord.size -and
        $finalSha256 -ceq [string]$targetRecord.sha256) `
    'AppShip target changed after production verification.'

$publishParameters = @{
    BaseURL = $baseUri.AbsoluteUri.TrimEnd('/')
    Credential = $Credential
    ApplicationSlug = $applicationSlug
    ArtifactPath = $artifactPath
    Platform = $platform
    FileKind = $fileKind
    Architecture = $architecture
    Channel = $appShipChannel
    VersionName = $version
    BuildNumber = [long]$buildNumber
    Title = $effectiveTitle
    ReleaseNotes = $ReleaseNotes
    RequireSignatureReview = $true
    SignatureDecision = 'approved'
    SignerFingerprint = $signerFingerprint
    SignatureReviewNote = $SignatureReviewNote.Trim()
    StatePath = $resolvedStatePath
}
if ($Resume) { $publishParameters.Resume = $true }
if ($createApplicationIfMissing) {
    $publishParameters.CreateApplicationIfMissing = $true
    $publishParameters.ApplicationName = $previewApplicationName
    $publishParameters.ApplicationSummary = $previewApplicationSummary
    $publishParameters.ApplicationDescription = $previewApplicationSummary
    $publishParameters.ApplicationVisibility = 'private'
}

[IO.Directory]::CreateDirectory($appShipStateRoot) | Out-Null
$publishLockPath = "$resolvedStatePath.lock"
$publishLock = $null
try {
    try {
        $publishLock = [IO.File]::Open(
            $publishLockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Another AppShip publish is already using this application/channel/version state: $resolvedStatePath"
    }
    & $resolvedPublishScript @publishParameters
}
finally {
    if ($null -ne $publishLock) { $publishLock.Dispose() }
    Remove-Item -LiteralPath $publishLockPath -Force -ErrorAction SilentlyContinue
}
