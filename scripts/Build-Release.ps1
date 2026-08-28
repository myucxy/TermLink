[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateRange(1, 65535)]
    [int]$BuildNumber,

    [ValidateSet('Production', 'Development')]
    [string]$ReleaseMode = 'Production',

    [switch]$ServerImage,
    [switch]$WindowsSetup,
    [switch]$WindowsMsix,
    [switch]$AndroidApk,
    [switch]$AndroidAab,

    [switch]$Plan,
    [switch]$DryRun,
    [switch]$Publish,
    [switch]$Force,

    [string]$OutputDirectory,
    [string]$DockerPath = 'docker',
    [string]$CosignPath = 'cosign',
    [string]$ServerSigningKeyPath,
    [Security.SecureString]$ServerSigningKeyPassword,

    [string]$DotNetPath = 'dotnet',
    [string]$CargoPath,
    [string]$WindowsCertificatePath,
    [Security.SecureString]$WindowsCertificatePassword,
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$WindowsCertificateThumbprint,
    [string]$WindowsTimestampUrl,
    [switch]$AllowHttpWindowsTimestamp,
    [ValidateSet('stable', 'preview')]
    [string]$WindowsUpdateChannel = 'stable',
    [string]$WindowsPackageIdentityName = 'TermLink.Terminal',
    [string]$WindowsPublisher,
    [string]$WindowsPublisherDisplayName = 'TermLink',
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$ExpectedWindowsSignerSha1,

    [string]$AndroidSdkPath,
    [string]$AndroidKeystorePath,
    [Security.SecureString]$AndroidKeystorePassword,
    [string]$AndroidKeyAlias,
    [Security.SecureString]$AndroidKeyPassword,
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedAndroidSignerSha256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition {
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

function Get-RepoRelativePath {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$Path
    )

    $separator = [IO.Path]::DirectorySeparatorChar
    $rootPrefix = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + $separator
    $fullPath = [IO.Path]::GetFullPath($Path)
    Assert-Condition ($fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) `
        "Path is outside the repository: $fullPath"
    return $fullPath.Substring($rootPrefix.Length).Replace('\', '/')
}

function Resolve-RequiredFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$BasePath
    )

    $candidate = $Path
    if (-not [IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $BasePath $candidate
    }
    $resolved = [IO.Path]::GetFullPath($candidate)
    Assert-Condition (Test-Path -LiteralPath $resolved -PathType Leaf) "$Name not found: $resolved"
    return $resolved
}

function Resolve-Executable {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Name)

    if ([IO.Path]::IsPathRooted($Path) -or $Path.Contains([IO.Path]::DirectorySeparatorChar) -or
        $Path.Contains([IO.Path]::AltDirectorySeparatorChar)) {
        $resolved = [IO.Path]::GetFullPath($Path)
        Assert-Condition (Test-Path -LiteralPath $resolved -PathType Leaf) "$Name executable not found: $resolved"
        return $resolved
    }

    $command = Get-Command $Path -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    Assert-Condition ($null -ne $command) "$Name executable not found: $Path"
    return $command.Source
}

function ConvertFrom-SecureValue {
    param([Security.SecureString]$Value)
    if ($null -eq $Value) { return $null }
    return [Net.NetworkCredential]::new('', $Value).Password
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return ([BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
}

function Initialize-ArtifactTarget {
    param([Parameter(Mandatory)][string]$Path, [switch]$AllowOverwrite)

    if (Test-Path -LiteralPath $Path) {
        Assert-Condition ($AllowOverwrite -and (Test-Path -LiteralPath $Path -PathType Leaf)) `
            "Artifact already exists (use -Force to replace files): $Path"
        Remove-Item -LiteralPath $Path -Force
    }
}

function New-ArtifactRecord {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Platform,
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][string]$ArtifactVersion,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$ArtifactRoot
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    Assert-Condition (Test-StrictChildPath -Root $ArtifactRoot -Candidate $resolved) `
        "Artifact path escaped the release output directory: $resolved"
    Assert-Condition (Test-Path -LiteralPath $resolved -PathType Leaf) "Expected artifact was not produced: $resolved"
    $file = Get-Item -LiteralPath $resolved
    return [pscustomobject][ordered]@{
        path = Get-RepoRelativePath -RepositoryRoot $RepositoryRoot -Path $resolved
        size = [int64]$file.Length
        sha256 = Get-Sha256Hex -Path $resolved
        platform = $Platform
        type = $Type
        version = $ArtifactVersion
    }
}

function Get-AndroidSigningConfiguration {
    param(
        [string]$KeystorePath,
        [Security.SecureString]$KeystorePassword,
        [string]$KeyAlias,
        [Security.SecureString]$KeyPassword,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [switch]$Required
    )

    $storePathValue = $KeystorePath
    if ([string]::IsNullOrWhiteSpace($storePathValue)) {
        $storePathValue = [Environment]::GetEnvironmentVariable('TERMLINK_RELEASE_STORE_FILE', 'Process')
    }
    $storePasswordValue = $KeystorePassword
    if ($null -eq $storePasswordValue) {
        $plainStorePassword = [Environment]::GetEnvironmentVariable('TERMLINK_RELEASE_STORE_PASSWORD', 'Process')
        if (-not [string]::IsNullOrWhiteSpace($plainStorePassword)) {
            $storePasswordValue = ConvertTo-SecureString $plainStorePassword -AsPlainText -Force
            $plainStorePassword = $null
        }
    }
    $keyAliasValue = $KeyAlias
    if ([string]::IsNullOrWhiteSpace($keyAliasValue)) {
        $keyAliasValue = [Environment]::GetEnvironmentVariable('TERMLINK_RELEASE_KEY_ALIAS', 'Process')
    }
    $keyPasswordValue = $KeyPassword
    if ($null -eq $keyPasswordValue) {
        $plainKeyPassword = [Environment]::GetEnvironmentVariable('TERMLINK_RELEASE_KEY_PASSWORD', 'Process')
        if (-not [string]::IsNullOrWhiteSpace($plainKeyPassword)) {
            $keyPasswordValue = ConvertTo-SecureString $plainKeyPassword -AsPlainText -Force
            $plainKeyPassword = $null
        }
    }

    $hasAnyValue = (-not [string]::IsNullOrWhiteSpace($storePathValue)) -or
        ($null -ne $storePasswordValue) -or (-not [string]::IsNullOrWhiteSpace($keyAliasValue)) -or
        ($null -ne $keyPasswordValue)
    if ($Required -or $hasAnyValue) {
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($storePathValue)) `
            'Android release keystore is required for this build.'
        Assert-Condition ($null -ne $storePasswordValue) `
            'Android release keystore password is required for this build.'
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($keyAliasValue)) `
            'Android release key alias is required for this build.'
        $resolvedStore = Resolve-RequiredFile -Path $storePathValue -Name 'Android release keystore' `
            -BasePath $RepositoryRoot
        if ($null -eq $keyPasswordValue) { $keyPasswordValue = $storePasswordValue }
        return [pscustomobject]@{
            Enabled = $true
            StorePath = $resolvedStore
            StorePassword = $storePasswordValue
            KeyAlias = $keyAliasValue
            KeyPassword = $keyPasswordValue
        }
    }

    return [pscustomobject]@{ Enabled = $false }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
$releaseVerifierPath = Join-Path $repositoryRoot 'release\Verify-LocalRelease.ps1'
$releaseVerificationModulePath = Join-Path $repositoryRoot 'release\ReleaseVerification.psm1'
$selectedTargets = @($ServerImage, $WindowsSetup, $WindowsMsix, $AndroidApk, $AndroidAab |
    Where-Object { $_.IsPresent })
Assert-Condition ($selectedTargets.Count -gt 0) `
    'Select at least one target: -ServerImage, -WindowsSetup, -WindowsMsix, -AndroidApk, or -AndroidAab.'
Assert-Condition (-not ($Plan -and $DryRun)) '-Plan and -DryRun are mutually exclusive.'
if ($Publish) {
    throw 'Publishing is intentionally disabled. -Publish never uploads; use the reviewed external release process separately.'
}

$versionParts = @($Version.Split('.') | ForEach-Object { [int]$_ })
Assert-Condition (@($versionParts | Where-Object { $_ -gt 65535 }).Count -eq 0) `
    'Version components must each be between 0 and 65535 for the shared Windows version.'
$windowsVersion = "$Version.$BuildNumber"
$releaseId = "$Version-$BuildNumber"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $resolvedOutputDirectory = Join-Path $releaseArtifactsRoot $releaseId
}
else {
    $outputCandidate = $OutputDirectory
    if (-not [IO.Path]::IsPathRooted($outputCandidate)) {
        $outputCandidate = Join-Path $repositoryRoot $outputCandidate
    }
    $resolvedOutputDirectory = [IO.Path]::GetFullPath($outputCandidate)
}
Assert-Condition (Test-StrictChildPath -Root $releaseArtifactsRoot -Candidate $resolvedOutputDirectory) `
    "OutputDirectory must be a child of '$releaseArtifactsRoot'."

$manifestPath = Join-Path $resolvedOutputDirectory 'artifact-manifest.json'
$pendingManifestPath = "$manifestPath.pending"
$serverArtifactPath = Join-Path $resolvedOutputDirectory "TermLink-Server-$releaseId.oci.tar"
$serverSignaturePath = "$serverArtifactPath.sig"
$setupArtifactPath = Join-Path $resolvedOutputDirectory "TermLink-Setup-$windowsVersion-x64.exe"
$msixArtifactPath = Join-Path $resolvedOutputDirectory "TermLink-$windowsVersion-x64.msix"
$apkArtifactPath = Join-Path $resolvedOutputDirectory "TermLink-Android-$releaseId.apk"
$aabArtifactPath = Join-Path $resolvedOutputDirectory "TermLink-Android-$releaseId.aab"
$isProduction = $ReleaseMode -eq 'Production'
$isDevelopment = -not $isProduction
if ($isProduction) {
    Assert-Condition (-not $WindowsMsix) `
        'Production WindowsMsix is disabled until MSIX identity, version, signer continuity, and RFC 3161 timestamp receive complete post-build verification.'
    Assert-Condition (-not $AndroidAab) `
        'Production AndroidAab is disabled until AAB application identity, version, and signer continuity receive complete post-build verification.'
}
if ($isProduction -and -not ($Plan -or $DryRun)) {
    Assert-Condition (Test-Path -LiteralPath $releaseVerifierPath -PathType Leaf) `
        "Production release verifier not found: $releaseVerifierPath"
    Assert-Condition (Test-Path -LiteralPath $releaseVerificationModulePath -PathType Leaf) `
        "Production release verification module not found: $releaseVerificationModulePath"
}
$windowsSetupSigningArguments = if (-not [string]::IsNullOrWhiteSpace($WindowsCertificateThumbprint)) {
    @('-CertificateThumbprint', '<WindowsCertificateThumbprint>')
}
else {
    @('-CertificatePath', '<WindowsCertificatePath>', '-CertificatePassword', '<secure>')
}
$windowsSetupSigningRequirement = if (-not [string]::IsNullOrWhiteSpace($WindowsCertificateThumbprint)) {
    'code-signing certificate with private key in CurrentUser/My'
}
else {
    'code-signing PFX and password'
}

$steps = New-Object 'System.Collections.Generic.List[object]'
$stepNumber = 0
if ($ServerImage) {
    $stepNumber++
    $serverOutputs = @((Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $serverArtifactPath))
    $serverRequirements = @('docker with buildx')
    if ($isProduction) {
        $serverOutputs += Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $serverSignaturePath
        $serverRequirements += @('cosign', 'ServerSigningKeyPath')
    }
    $steps.Add([pscustomobject][ordered]@{
        order = $stepNumber
        id = 'server-image'
        platform = 'linux/amd64,linux/arm64'
        type = 'oci-image'
        working_directory = '.'
        command = 'docker'
        arguments = @(
            'buildx', 'build', '--platform', 'linux/amd64,linux/arm64', '--file', 'server/Dockerfile',
            '--tag', "termlink-server:$Version-build.$BuildNumber", '--build-arg', "VERSION=$Version+$BuildNumber",
            '--build-arg', 'REVISION=<git-sha>', '--build-arg', 'CREATED=<git-commit-time>',
            '--output', "type=oci,dest=$($serverOutputs[0])", '.'
        )
        requirements = $serverRequirements
        outputs = $serverOutputs
        publish = $false
    })
}
if ($WindowsSetup) {
    $stepNumber++
    $steps.Add([pscustomobject][ordered]@{
        order = $stepNumber
        id = 'windows-setup'
        platform = 'windows-x64'
        type = 'setup'
        working_directory = '.'
        command = 'win-agent/package/Build-Setup.ps1'
        arguments = @('-Version', $windowsVersion, '-UpdateChannel', $WindowsUpdateChannel,
            '-CargoPath', '<CargoPath>') + $windowsSetupSigningArguments +
            @('-OutputPath', (Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $setupArtifactPath))
        requirements = if ($isProduction) {
            @('.NET 8 SDK', 'Rust 1.97.1 with x86_64-pc-windows-msvc target', 'Windows SDK signing tools',
                "production $windowsSetupSigningRequirement", 'RFC 3161 timestamp URL (HTTP requires explicit opt-in)',
                'explicit expected Authenticode signer SHA-1 continuity fingerprint')
        } else {
            @('.NET 8 SDK', 'Rust 1.97.1 with x86_64-pc-windows-msvc target', 'Windows SDK signing tools',
                "development $windowsSetupSigningRequirement")
        }
        outputs = @((Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $setupArtifactPath))
        publish = $false
    })
}
if ($WindowsMsix) {
    $stepNumber++
    $steps.Add([pscustomobject][ordered]@{
        order = $stepNumber
        id = 'windows-msix'
        platform = 'windows-x64'
        type = 'msix'
        working_directory = '.'
        command = 'win-agent/package/Build-Msix.ps1'
        arguments = @('-Version', $windowsVersion, '-Publisher', '<WindowsPublisher>',
            '-CertificatePath', '<WindowsCertificatePath>', '-CertificatePassword', '<secure>',
            '-CargoPath', '<CargoPath>',
            '-OutputPath', (Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $msixArtifactPath))
        requirements = if ($isProduction) {
            @('.NET 8 SDK', 'Rust 1.97.1 with x86_64-pc-windows-msvc target',
                'Windows SDK packaging/signing tools', 'production code-signing PFX and password',
                'matching publisher subject', 'RFC 3161 timestamp URL (HTTP requires explicit opt-in)')
        } else {
            @('.NET 8 SDK', 'Rust 1.97.1 with x86_64-pc-windows-msvc target',
                'Windows SDK packaging/signing tools', 'development code-signing PFX and password')
        }
        outputs = @((Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $msixArtifactPath))
        publish = $false
    })
}
if ($AndroidApk -or $AndroidAab) {
    $stepNumber++
    $androidTasks = @()
    $androidOutputs = @()
    $androidTypes = @()
    if ($AndroidApk) {
        $androidTasks += ':app:assembleRelease'
        $androidOutputs += Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $apkArtifactPath
        $androidTypes += 'apk'
    }
    if ($AndroidAab) {
        $androidTasks += ':app:bundleRelease'
        $androidOutputs += Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $aabArtifactPath
        $androidTypes += 'aab'
    }
    $androidArguments = @('--no-daemon', '--console=plain', '--project-dir', 'android') + $androidTasks +
        @("-PtermlinkVersionCode=$BuildNumber", "-PtermlinkVersionName=$Version")
    $androidRequirements = @('JDK 17', 'Android SDK', 'Gradle wrapper')
    if ($isProduction) {
        $androidRequirements += @('release keystore', 'keystore password', 'key alias')
        if ($AndroidApk) {
            $androidRequirements += 'explicit expected APK signer certificate SHA-256 continuity fingerprint'
        }
    }
    $steps.Add([pscustomobject][ordered]@{
        order = $stepNumber
        id = 'android-release'
        platform = 'android'
        type = ($androidTypes -join ',')
        working_directory = '.'
        command = 'android/gradlew.bat'
        arguments = $androidArguments
        requirements = $androidRequirements
        outputs = $androidOutputs
        publish = $false
    })
}

$releasePlan = [pscustomobject][ordered]@{
    schema_version = 1
    operation = if ($DryRun) { 'dry-run' } else { 'plan' }
    version = $Version
    build_number = $BuildNumber
    windows_version = $windowsVersion
    release_mode = $ReleaseMode.ToLowerInvariant()
    output_directory = Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $resolvedOutputDirectory
    publish = $false
    steps = $steps.ToArray()
    manifest = Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $manifestPath
}

if ($Plan -or $DryRun) {
    $releasePlan | ConvertTo-Json -Depth 8
    return
}

# Validate every prerequisite before creating output or starting any build. Production
# therefore cannot leave a partially built release merely because signing input was absent.
$resolvedDocker = $null
$resolvedCosign = $null
$resolvedServerKey = $null
$resolvedDotNet = $null
$resolvedCargo = $null
$resolvedWindowsCertificate = $null
$resolvedWindowsCertificateThumbprint = $null
$resolvedWindowsPublisher = $WindowsPublisher
$androidSigning = $null
$resolvedAndroidSdk = $null

if ($ServerImage) {
    if ($isProduction) {
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($ServerSigningKeyPath)) `
            'ServerSigningKeyPath is required for a production server image.'
        $resolvedServerKey = Resolve-RequiredFile -Path $ServerSigningKeyPath -Name 'Server signing key' `
            -BasePath $repositoryRoot
        $resolvedCosign = Resolve-Executable -Path $CosignPath -Name 'Cosign'
    }
    $resolvedDocker = Resolve-Executable -Path $DockerPath -Name 'Docker'
}
if ($WindowsSetup -or $WindowsMsix) {
    $usesWindowsCertificateFile = -not [string]::IsNullOrWhiteSpace($WindowsCertificatePath) -or
        $null -ne $WindowsCertificatePassword
    $usesWindowsCertificateStore = -not [string]::IsNullOrWhiteSpace($WindowsCertificateThumbprint)
    Assert-Condition ($usesWindowsCertificateFile -xor $usesWindowsCertificateStore) `
        'Specify exactly one Windows signing source: WindowsCertificateThumbprint, or WindowsCertificatePath with WindowsCertificatePassword.'
    if ($usesWindowsCertificateFile) {
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($WindowsCertificatePath)) `
            'WindowsCertificatePath is required when signing from a PFX file.'
        Assert-Condition ($null -ne $WindowsCertificatePassword) `
            'WindowsCertificatePassword is required when signing from a PFX file.'
        $resolvedWindowsCertificate = Resolve-RequiredFile -Path $WindowsCertificatePath `
            -Name 'Windows signing certificate' -BasePath $repositoryRoot
    }
    else {
        Assert-Condition (-not $WindowsMsix) `
            'WindowsCertificateThumbprint currently supports WindowsSetup only; WindowsMsix still requires a PFX file.'
        $certificateStorePath = "Cert:\CurrentUser\My\$WindowsCertificateThumbprint"
        Assert-Condition (Test-Path -LiteralPath $certificateStorePath -PathType Leaf) `
            "Windows signing certificate was not found in CurrentUser/My: $WindowsCertificateThumbprint"
        $resolvedWindowsCertificateThumbprint = $WindowsCertificateThumbprint.ToUpperInvariant()
    }
    $resolvedDotNet = Resolve-Executable -Path $DotNetPath -Name '.NET SDK'
    $cargoCandidate = if ([string]::IsNullOrWhiteSpace($CargoPath)) {
        $managedCargo = 'D:\Dev\Env\Rust\cargo\bin\cargo.exe'
        if (Test-Path -LiteralPath $managedCargo -PathType Leaf) { $managedCargo } else { 'cargo' }
    }
    else { $CargoPath }
    $resolvedCargo = Resolve-Executable -Path $cargoCandidate -Name 'Cargo 1.97.1'
    if ($isProduction) {
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($WindowsTimestampUrl)) `
            'WindowsTimestampUrl is required for production Windows artifacts.'
        $timestampUri = $null
        $validTimestamp = [Uri]::TryCreate($WindowsTimestampUrl, [UriKind]::Absolute, [ref]$timestampUri)
        if ($validTimestamp) {
            $validTimestamp = -not [string]::IsNullOrWhiteSpace($timestampUri.Host) -and
                [string]::IsNullOrEmpty($timestampUri.UserInfo) -and
                ($timestampUri.Scheme -eq 'https' -or
                    ($timestampUri.Scheme -eq 'http' -and $AllowHttpWindowsTimestamp))
        }
        Assert-Condition $validTimestamp `
            'WindowsTimestampUrl must be absolute HTTPS; HTTP additionally requires -AllowHttpWindowsTimestamp.'
        if ($WindowsSetup) {
            Assert-Condition (-not [string]::IsNullOrWhiteSpace($ExpectedWindowsSignerSha1)) `
                'ExpectedWindowsSignerSha1 is required for a production Windows Setup.'
        }
    }
}
if ($WindowsMsix) {
    if ([string]::IsNullOrWhiteSpace($resolvedWindowsPublisher) -and $isDevelopment) {
        $resolvedWindowsPublisher = 'CN=TermLink Development'
    }
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($resolvedWindowsPublisher)) `
        'WindowsPublisher is required for a production MSIX and must match the certificate subject.'
}
if ($AndroidApk -or $AndroidAab) {
    $androidSigning = Get-AndroidSigningConfiguration -KeystorePath $AndroidKeystorePath `
        -KeystorePassword $AndroidKeystorePassword -KeyAlias $AndroidKeyAlias `
        -KeyPassword $AndroidKeyPassword -RepositoryRoot $repositoryRoot -Required:$isProduction
    $gradleWrapper = Join-Path $repositoryRoot 'android\gradlew.bat'
    Assert-Condition (Test-Path -LiteralPath $gradleWrapper -PathType Leaf) "Gradle wrapper not found: $gradleWrapper"
    $androidSdkValue = $AndroidSdkPath
    if ([string]::IsNullOrWhiteSpace($androidSdkValue)) {
        $androidSdkValue = [Environment]::GetEnvironmentVariable('ANDROID_SDK_ROOT', 'Process')
    }
    if ([string]::IsNullOrWhiteSpace($androidSdkValue)) {
        $androidSdkValue = [Environment]::GetEnvironmentVariable('ANDROID_HOME', 'Process')
    }
    if (-not [string]::IsNullOrWhiteSpace($androidSdkValue)) {
        $resolvedAndroidSdk = [IO.Path]::GetFullPath($androidSdkValue)
        Assert-Condition (Test-Path -LiteralPath $resolvedAndroidSdk -PathType Container) `
            "Android SDK directory not found: $resolvedAndroidSdk"
    }
    if ($isProduction -and $AndroidApk) {
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($ExpectedAndroidSignerSha256)) `
            'ExpectedAndroidSignerSha256 is required for a production APK.'
        Assert-Condition ($null -ne $resolvedAndroidSdk) `
            'AndroidSdkPath, ANDROID_SDK_ROOT, or ANDROID_HOME is required to verify a production APK.'
        $androidBuildToolsRoot = Join-Path $resolvedAndroidSdk 'build-tools'
        $androidVerifierTools = @(Get-ChildItem -LiteralPath $androidBuildToolsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                (Test-Path -LiteralPath (Join-Path $_.FullName 'aapt2.exe') -PathType Leaf) -and
                (Test-Path -LiteralPath (Join-Path $_.FullName 'apksigner.bat') -PathType Leaf)
            })
        Assert-Condition ($androidVerifierTools.Count -gt 0) `
            "Android SDK has no build-tools installation containing aapt2.exe and apksigner.bat: $androidBuildToolsRoot"
    }
}

$allArtifactTargets = @($manifestPath, $pendingManifestPath)
if ($ServerImage) {
    $allArtifactTargets += $serverArtifactPath
    if ($isProduction) { $allArtifactTargets += $serverSignaturePath }
}
if ($WindowsSetup) { $allArtifactTargets += $setupArtifactPath }
if ($WindowsMsix) { $allArtifactTargets += $msixArtifactPath }
if ($AndroidApk) { $allArtifactTargets += $apkArtifactPath }
if ($AndroidAab) { $allArtifactTargets += $aabArtifactPath }

# Resolve source identity before creating the release output directory. Otherwise a
# previously clean tree would become dirty merely because this build created its own
# untracked artifact directory, and a genuinely dirty source tree would be mislabeled
# as the clean HEAD commit in the OCI image metadata.
$serverSourceRevision = $null
$serverSourceCreated = $null
if ($ServerImage) {
    $gitPath = Resolve-Executable -Path 'git' -Name 'Git'
    $serverSourceRevision = (& $gitPath -C $repositoryRoot rev-parse HEAD).Trim()
    Assert-Condition ($LASTEXITCODE -eq 0 -and $serverSourceRevision -match '^[0-9a-fA-F]{40}$') `
        'Unable to resolve the source revision for the server image.'
    $sourceStatus = @(& $gitPath -C $repositoryRoot status --porcelain --untracked-files=normal)
    Assert-Condition ($LASTEXITCODE -eq 0) 'Unable to resolve the source worktree status for the server image.'
    if ($sourceStatus.Count -gt 0) {
        $serverSourceRevision = "$serverSourceRevision-dirty"
    }
    $serverSourceCreated = (& $gitPath -C $repositoryRoot show -s '--format=%cI' HEAD).Trim()
    Assert-Condition ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($serverSourceCreated)) `
        'Unable to resolve the source commit time for the server image.'
}

foreach ($target in $allArtifactTargets) {
    Initialize-ArtifactTarget -Path $target -AllowOverwrite:$Force
}
[IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

$artifactRecords = New-Object 'System.Collections.Generic.List[object]'
if ($ServerImage) {
    $dockerArguments = @(
        'buildx', 'build', '--platform', 'linux/amd64,linux/arm64',
        '--file', (Join-Path $repositoryRoot 'server\Dockerfile'),
        '--tag', "termlink-server:$Version-build.$BuildNumber",
        '--build-arg', "VERSION=$Version+$BuildNumber",
        '--build-arg', "REVISION=$serverSourceRevision",
        '--build-arg', "CREATED=$serverSourceCreated",
        '--output', "type=oci,dest=$serverArtifactPath",
        $repositoryRoot
    )
    Invoke-CheckedNative -FilePath $resolvedDocker -Arguments $dockerArguments -Description 'Server OCI image build'
    $artifactRecords.Add((New-ArtifactRecord -Path $serverArtifactPath -Platform 'linux/amd64,linux/arm64' `
        -Type 'oci-image' -ArtifactVersion $Version -RepositoryRoot $repositoryRoot `
        -ArtifactRoot $resolvedOutputDirectory))

    if ($isProduction) {
        $previousCosignPassword = [Environment]::GetEnvironmentVariable('COSIGN_PASSWORD', 'Process')
        $plainServerKeyPassword = $null
        try {
            if ($null -ne $ServerSigningKeyPassword) {
                $plainServerKeyPassword = ConvertFrom-SecureValue -Value $ServerSigningKeyPassword
                [Environment]::SetEnvironmentVariable('COSIGN_PASSWORD', $plainServerKeyPassword, 'Process')
            }
            Invoke-CheckedNative -FilePath $resolvedCosign -Arguments @(
                'sign-blob', '--yes', '--key', $resolvedServerKey,
                '--output-signature', $serverSignaturePath, $serverArtifactPath
            ) -Description 'Server OCI artifact signing'
        }
        finally {
            [Environment]::SetEnvironmentVariable('COSIGN_PASSWORD', $previousCosignPassword, 'Process')
            $plainServerKeyPassword = $null
        }
        $artifactRecords.Add((New-ArtifactRecord -Path $serverSignaturePath -Platform 'linux/amd64,linux/arm64' `
            -Type 'signature' -ArtifactVersion $Version -RepositoryRoot $repositoryRoot `
            -ArtifactRoot $resolvedOutputDirectory))
    }
}

if ($WindowsSetup) {
    $setupParameters = @{
        Version = $windowsVersion
        Development = $isDevelopment
        DotNetPath = $resolvedDotNet
        CargoPath = $resolvedCargo
        OutputPath = $setupArtifactPath
        UpdateChannel = $WindowsUpdateChannel
    }
    if (-not [string]::IsNullOrWhiteSpace($resolvedWindowsCertificateThumbprint)) {
        $setupParameters.CertificateThumbprint = $resolvedWindowsCertificateThumbprint
    }
    else {
        $setupParameters.CertificatePath = $resolvedWindowsCertificate
        $setupParameters.CertificatePassword = $WindowsCertificatePassword
    }
    if (-not [string]::IsNullOrWhiteSpace($WindowsTimestampUrl)) {
        $setupParameters.TimestampUrl = $WindowsTimestampUrl
    }
    if ($AllowHttpWindowsTimestamp) {
        $setupParameters.AllowHttpTimestamp = $true
    }
    $null = & (Join-Path $repositoryRoot 'win-agent\package\Build-Setup.ps1') @setupParameters
    $artifactRecords.Add((New-ArtifactRecord -Path $setupArtifactPath -Platform 'windows-x64' `
        -Type 'setup' -ArtifactVersion $Version -RepositoryRoot $repositoryRoot `
        -ArtifactRoot $resolvedOutputDirectory))
}

if ($WindowsMsix) {
    $msixParameters = @{
        Version = $windowsVersion
        PackageIdentityName = $WindowsPackageIdentityName
        Publisher = $resolvedWindowsPublisher
        PublisherDisplayName = $WindowsPublisherDisplayName
        CertificatePath = $resolvedWindowsCertificate
        CertificatePassword = $WindowsCertificatePassword
        Development = $isDevelopment
        DotNetPath = $resolvedDotNet
        CargoPath = $resolvedCargo
        OutputPath = $msixArtifactPath
    }
    if (-not [string]::IsNullOrWhiteSpace($WindowsTimestampUrl)) {
        $msixParameters.TimestampUrl = $WindowsTimestampUrl
    }
    if ($AllowHttpWindowsTimestamp) {
        $msixParameters.AllowHttpTimestamp = $true
    }
    $null = & (Join-Path $repositoryRoot 'win-agent\package\Build-Msix.ps1') @msixParameters
    $artifactRecords.Add((New-ArtifactRecord -Path $msixArtifactPath -Platform 'windows-x64' `
        -Type 'msix' -ArtifactVersion $Version -RepositoryRoot $repositoryRoot `
        -ArtifactRoot $resolvedOutputDirectory))
}

if ($AndroidApk -or $AndroidAab) {
    $androidSigningEnvironmentNames = @(
        'TERMLINK_RELEASE_STORE_FILE', 'TERMLINK_RELEASE_STORE_PASSWORD',
        'TERMLINK_RELEASE_KEY_ALIAS', 'TERMLINK_RELEASE_KEY_PASSWORD'
    )
    $androidSdkEnvironmentNames = @('ANDROID_SDK_ROOT', 'ANDROID_HOME')
    $environmentNames = @($androidSigningEnvironmentNames + $androidSdkEnvironmentNames)
    $savedEnvironment = @{}
    foreach ($name in $environmentNames) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    $plainAndroidStorePassword = $null
    $plainAndroidKeyPassword = $null
    try {
        if ($null -ne $resolvedAndroidSdk) {
            [Environment]::SetEnvironmentVariable('ANDROID_SDK_ROOT', $resolvedAndroidSdk, 'Process')
            [Environment]::SetEnvironmentVariable('ANDROID_HOME', $resolvedAndroidSdk, 'Process')
        }
        if ($androidSigning.Enabled) {
            $plainAndroidStorePassword = ConvertFrom-SecureValue -Value $androidSigning.StorePassword
            $plainAndroidKeyPassword = ConvertFrom-SecureValue -Value $androidSigning.KeyPassword
            [Environment]::SetEnvironmentVariable('TERMLINK_RELEASE_STORE_FILE', $androidSigning.StorePath, 'Process')
            [Environment]::SetEnvironmentVariable('TERMLINK_RELEASE_STORE_PASSWORD', $plainAndroidStorePassword, 'Process')
            [Environment]::SetEnvironmentVariable('TERMLINK_RELEASE_KEY_ALIAS', $androidSigning.KeyAlias, 'Process')
            [Environment]::SetEnvironmentVariable('TERMLINK_RELEASE_KEY_PASSWORD', $plainAndroidKeyPassword, 'Process')
        }
        else {
            foreach ($name in $androidSigningEnvironmentNames) {
                [Environment]::SetEnvironmentVariable($name, $null, 'Process')
            }
        }

        $gradleArguments = @('--no-daemon', '--console=plain', '--project-dir', (Join-Path $repositoryRoot 'android'))
        if ($AndroidApk) { $gradleArguments += ':app:assembleRelease' }
        if ($AndroidAab) { $gradleArguments += ':app:bundleRelease' }
        $gradleArguments += @(
            "-PtermlinkVersionCode=$BuildNumber",
            "-PtermlinkVersionName=$Version"
        )
        Invoke-CheckedNative -FilePath (Join-Path $repositoryRoot 'android\gradlew.bat') `
            -Arguments $gradleArguments -Description 'Android release build'

        if ($AndroidApk) {
            $apkSourceName = if ($androidSigning.Enabled) { 'app-release.apk' } else { 'app-release-unsigned.apk' }
            $apkSource = Join-Path $repositoryRoot "android\app\build\outputs\apk\release\$apkSourceName"
            Assert-Condition (Test-Path -LiteralPath $apkSource -PathType Leaf) `
                "Expected Android APK was not produced: $apkSource"
            Copy-Item -LiteralPath $apkSource -Destination $apkArtifactPath
            $artifactRecords.Add((New-ArtifactRecord -Path $apkArtifactPath -Platform 'android' `
                -Type 'apk' -ArtifactVersion $Version -RepositoryRoot $repositoryRoot `
                -ArtifactRoot $resolvedOutputDirectory))
        }
        if ($AndroidAab) {
            $aabSource = Join-Path $repositoryRoot 'android\app\build\outputs\bundle\release\app-release.aab'
            Assert-Condition (Test-Path -LiteralPath $aabSource -PathType Leaf) `
                "Expected Android AAB was not produced: $aabSource"
            Copy-Item -LiteralPath $aabSource -Destination $aabArtifactPath
            $artifactRecords.Add((New-ArtifactRecord -Path $aabArtifactPath -Platform 'android' `
                -Type 'aab' -ArtifactVersion $Version -RepositoryRoot $repositoryRoot `
                -ArtifactRoot $resolvedOutputDirectory))
        }
    }
    finally {
        foreach ($name in $environmentNames) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
        $plainAndroidStorePassword = $null
        $plainAndroidKeyPassword = $null
    }
}

# Re-read and hash every artifact only after every platform build has finished. In
# particular, a BuildKit OCI exporter can return control while Windows is still
# making the completed tar visible through its filesystem cache. Deferring the final
# digest pass prevents a manifest from recording an early, transient file digest.
$finalArtifactRecords = @(
    foreach ($record in $artifactRecords) {
        New-ArtifactRecord -Path (Join-Path $repositoryRoot $record.path) `
            -Platform $record.platform -Type $record.type -ArtifactVersion $record.version `
            -RepositoryRoot $repositoryRoot -ArtifactRoot $resolvedOutputDirectory
    }
)
$sortedRecords = @($finalArtifactRecords | Sort-Object platform, type, path)
$manifest = [pscustomobject][ordered]@{
    schema_version = 1
    version = $Version
    build_number = $BuildNumber
    release_mode = $ReleaseMode.ToLowerInvariant()
    artifacts = $sortedRecords
}
$manifestJson = $manifest | ConvertTo-Json -Depth 6
if ($isProduction) {
    try {
        [IO.File]::WriteAllText(
            $pendingManifestPath,
            $manifestJson + [Environment]::NewLine,
            (New-Object Text.UTF8Encoding -ArgumentList $false))
        $verificationParameters = @{
            ReleaseDirectory = $resolvedOutputDirectory
            ManifestPath = $pendingManifestPath
            ExpectedReleaseMode = 'Production'
        }
        if ($AndroidApk) {
            $verificationParameters.AndroidSdkPath = $resolvedAndroidSdk
            $verificationParameters.ExpectedAndroidSignerSha256 = $ExpectedAndroidSignerSha256
        }
        if ($WindowsSetup) {
            $verificationParameters.ExpectedWindowsSignerSha1 = $ExpectedWindowsSignerSha1
            $verificationParameters.RequireWindowsTimestamp = $true
        }
        $null = & $releaseVerifierPath @verificationParameters
        Move-Item -LiteralPath $pendingManifestPath -Destination $manifestPath
    }
    finally {
        if (Test-Path -LiteralPath $pendingManifestPath -PathType Leaf) {
            Remove-Item -LiteralPath $pendingManifestPath -Force -ErrorAction SilentlyContinue
        }
    }
}
else {
    [IO.File]::WriteAllText(
        $manifestPath,
        $manifestJson + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding -ArgumentList $false))
}

[pscustomobject]@{
    ManifestPath = $manifestPath
    ArtifactCount = $sortedRecords.Count
    Published = $false
}
