[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [string]$ManifestPath,
    [ValidateSet('Production', 'Development')]
    [string]$ExpectedReleaseMode = 'Production',
    [string]$AndroidSdkPath,
    [string]$ExpectedAndroidSignerSha256,
    [string]$ExpectedWindowsSignerSha1,
    [switch]$RequireWindowsTimestamp
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'ReleaseVerification.psm1') -Force

function Assert-Release {
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

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    Assert-Release ($exitCode -eq 0) "$Description failed with exit code $exitCode."
    return @($output | ForEach-Object { [string]$_ })
}

function Resolve-AndroidBuildTools {
    param([Parameter(Mandatory)][string]$SdkPath)

    $resolvedSdk = [IO.Path]::GetFullPath($SdkPath)
    Assert-Release (Test-Path -LiteralPath $resolvedSdk -PathType Container) `
        "AndroidSdkPath not found: $resolvedSdk"
    $buildToolsRoot = Join-Path $resolvedSdk 'build-tools'
    Assert-Release (Test-Path -LiteralPath $buildToolsRoot -PathType Container) `
        "Android SDK build-tools directory not found: $buildToolsRoot"
    $candidates = @(Get-ChildItem -LiteralPath $buildToolsRoot -Directory | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'aapt2.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'apksigner.bat') -PathType Leaf)
    } | Sort-Object {
        try { [Version]$_.Name }
        catch { [Version]'0.0' }
    } -Descending)
    Assert-Release ($candidates.Count -gt 0) `
        "No Android build-tools installation contains both aapt2.exe and apksigner.bat under: $buildToolsRoot"
    return $candidates[0]
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
Assert-Release (Test-StrictChildPath -Root (Join-Path $repositoryRoot 'artifacts\release') -Candidate $releaseRoot) `
    'ReleaseDirectory must be a child of the repository artifacts/release directory.'
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $resolvedManifestPath = Join-Path $releaseRoot 'artifact-manifest.json'
}
else {
    $manifestCandidate = $ManifestPath
    if (-not [IO.Path]::IsPathRooted($manifestCandidate)) {
        $manifestCandidate = Join-Path $repositoryRoot $manifestCandidate
    }
    $resolvedManifestPath = [IO.Path]::GetFullPath($manifestCandidate)
}
Assert-Release (Test-StrictChildPath -Root $releaseRoot -Candidate $resolvedManifestPath) `
    'ManifestPath must be a file inside ReleaseDirectory.'
Assert-Release (Test-Path -LiteralPath $resolvedManifestPath -PathType Leaf) `
    "Artifact manifest not found: $resolvedManifestPath"

$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
$expectedMode = $ExpectedReleaseMode.ToLowerInvariant()
Assert-Release ([int]$manifest.schema_version -eq 1) 'Artifact manifest schema_version must be 1.'
Assert-Release ([string]$manifest.version -match '^\d+\.\d+\.\d+$') `
    'Artifact manifest version must contain three numeric components.'
Assert-Release ([int]$manifest.build_number -ge 1 -and [int]$manifest.build_number -le 65535) `
    'Artifact manifest build_number is outside 1..65535.'
Assert-Release ([string]$manifest.release_mode -ceq $expectedMode) `
    "Artifact manifest release_mode is '$($manifest.release_mode)', expected '$expectedMode'."
$artifactRecords = @($manifest.artifacts)
Assert-Release ($artifactRecords.Count -gt 0) 'Artifact manifest must contain at least one artifact.'
if ($expectedMode -ceq 'production') {
    $unsupportedProductionRecords = @($artifactRecords | Where-Object { $_.type -in @('aab', 'msix') })
    Assert-Release ($unsupportedProductionRecords.Count -eq 0) `
        'Production verification rejects AAB/MSIX until complete post-build identity, version, signer continuity, and timestamp checks are implemented.'
}

$resolvedArtifactPaths = @{}
$hashResults = @(
    foreach ($artifact in $artifactRecords) {
        $recordPath = [string]$artifact.path
        Assert-Release ($recordPath -match '^artifacts/release/') `
            "Artifact manifest path is not repository release-relative: $recordPath"
        $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ($recordPath.Replace('/', '\'))))
        Assert-Release (Test-StrictChildPath -Root $releaseRoot -Candidate $path) `
            "Artifact path escaped ReleaseDirectory: $recordPath"
        Assert-Release (-not $resolvedArtifactPaths.ContainsKey($recordPath)) `
            "Artifact manifest contains a duplicate path: $recordPath"
        Assert-Release (Test-Path -LiteralPath $path -PathType Leaf) "Artifact not found: $path"
        Assert-Release ([string]$artifact.version -ceq [string]$manifest.version) `
            "Artifact version does not match the release manifest: $recordPath"
        $file = Get-Item -LiteralPath $path
        $sha256 = Get-Sha256Hex -Path $path
        Assert-Release ($file.Length -eq [int64]$artifact.size) "Artifact size mismatch: $path"
        Assert-Release ($sha256 -ceq [string]$artifact.sha256) "Artifact SHA-256 mismatch: $path"
        $resolvedArtifactPaths[$recordPath] = $path
        [pscustomobject][ordered]@{
            name = $file.Name
            size = [int64]$file.Length
            sha256 = $sha256
            type = [string]$artifact.type
        }
    }
)

$androidResult = $null
$apkRecords = @($artifactRecords | Where-Object { $_.type -eq 'apk' })
Assert-Release ($apkRecords.Count -le 1) 'Release must not contain more than one APK.'
if ($apkRecords.Count -eq 1) {
    Assert-Release (-not [string]::IsNullOrWhiteSpace($ExpectedAndroidSignerSha256)) `
        'ExpectedAndroidSignerSha256 is required to verify an APK.'
    Assert-Release (-not [string]::IsNullOrWhiteSpace($AndroidSdkPath)) `
        'AndroidSdkPath is required to verify an APK.'

    $apkPath = $resolvedArtifactPaths[[string]$apkRecords[0].path]
    $buildTools = Resolve-AndroidBuildTools -SdkPath $AndroidSdkPath
    $aapt2 = Join-Path $buildTools.FullName 'aapt2.exe'
    $apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
    $badgingOutput = Invoke-NativeCapture -FilePath $aapt2 -Arguments @('dump', 'badging', $apkPath) `
        -Description 'APK metadata inspection'
    $badging = $badgingOutput -join "`n"
    $packageMatch = [regex]::Match($badging, "package: name='([^']+)' versionCode='([^']+)' versionName='([^']+)'")
    Assert-Release $packageMatch.Success 'APK package metadata is missing.'
    Assert-Release ($packageMatch.Groups[1].Value -ceq 'dev.termlink.harness') `
        'APK applicationId does not match dev.termlink.harness.'
    Assert-Release ($packageMatch.Groups[3].Value -ceq [string]$manifest.version) `
        'APK versionName does not match the release manifest.'
    Assert-Release ($packageMatch.Groups[2].Value -ceq [string]$manifest.build_number) `
        'APK versionCode does not match the release manifest build number.'

    $signatureOutput = Invoke-NativeCapture -FilePath $apksigner `
        -Arguments @('verify', '--verbose', '--print-certs', $apkPath) `
        -Description 'APK signature verification'
    $signerEvidence = Assert-TermLinkAndroidSignerOutput -Output $signatureOutput `
        -ExpectedSha256 $ExpectedAndroidSignerSha256
    $androidResult = [pscustomobject][ordered]@{
        application_id = $packageMatch.Groups[1].Value
        version_name = $packageMatch.Groups[3].Value
        version_code = [int]$packageMatch.Groups[2].Value
        signer_sha256 = $signerEvidence.signer_sha256
        verified_schemes = @($signerEvidence.verified_schemes)
    }
}

$windowsResult = $null
$setupRecords = @($artifactRecords | Where-Object { $_.type -eq 'setup' })
Assert-Release ($setupRecords.Count -le 1) 'Release must not contain more than one Windows Setup executable.'
if ($setupRecords.Count -eq 1) {
    Assert-Release (-not [string]::IsNullOrWhiteSpace($ExpectedWindowsSignerSha1)) `
        'ExpectedWindowsSignerSha1 is required to verify a Windows Setup.'
    $setupPath = $resolvedArtifactPaths[[string]$setupRecords[0].path]
    $setupFile = Get-Item -LiteralPath $setupPath
    $setupSignature = Get-AuthenticodeSignature -LiteralPath $setupPath
    Assert-Release ($null -ne $setupSignature.SignerCertificate) 'Windows Setup has no Authenticode signer.'
    $signerSha1 = Assert-TermLinkWindowsSignatureEvidence -Status ([string]$setupSignature.Status) `
        -ActualSha1 ([string]$setupSignature.SignerCertificate.Thumbprint) `
        -ExpectedSha1 $ExpectedWindowsSignerSha1
    $expectedWindowsVersion = "$($manifest.version).$($manifest.build_number)"
    $versionEvidence = Assert-TermLinkWindowsVersionEvidence `
        -ProductVersion ([string]$setupFile.VersionInfo.ProductVersion) `
        -FileVersion ([string]$setupFile.VersionInfo.FileVersion) `
        -ExpectedVersion $expectedWindowsVersion
    $timestampEvidence = $null
    if ($RequireWindowsTimestamp) {
        $peEvidence = Get-TermLinkPeAuthenticodeEvidence -Path $setupPath
        $timestampEvidence = Assert-TermLinkRfc3161TimestampEvidence `
            -UnsignedAttributeOids @($peEvidence.unsigned_attribute_oids) `
            -HasTimeStamperCertificate ($null -ne $setupSignature.TimeStamperCertificate)
    }
    $windowsResult = [pscustomobject][ordered]@{
        product_version = $versionEvidence.product_version
        file_version = $versionEvidence.file_version
        signature_status = [string]$setupSignature.Status
        signer_sha1 = $signerSha1
        signer_subject = $setupSignature.SignerCertificate.Subject
        timestamped = $null -ne $timestampEvidence
        timestamp_oid = if ($null -eq $timestampEvidence) { $null } else { $timestampEvidence.timestamp_oid }
    }
}

$platformResults = @()
$ociRecords = @($artifactRecords | Where-Object { $_.type -eq 'oci-image' })
Assert-Release ($ociRecords.Count -le 1) 'Release must not contain more than one OCI image archive.'
if ($ociRecords.Count -eq 1) {
    $ociPath = $resolvedArtifactPaths[[string]$ociRecords[0].path]
    $temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'TermLink-Release-Verify'))
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ([guid]::NewGuid().ToString('N'))))
    $temporaryPrefix = $temporaryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    Assert-Release ($temporaryDirectory.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) `
        'Temporary verification directory escaped its root.'
    [IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null

    try {
        $null = Invoke-NativeCapture -FilePath 'tar' -Arguments @('-xf', $ociPath, '-C', $temporaryDirectory) `
            -Description 'OCI archive extraction'
        $index = Get-Content -LiteralPath (Join-Path $temporaryDirectory 'index.json') -Raw | ConvertFrom-Json
        $candidateDescriptors = @(
            foreach ($rootDescriptor in @($index.manifests)) {
                if ($rootDescriptor.mediaType -eq 'application/vnd.oci.image.index.v1+json') {
                    $nestedDigest = ([string]$rootDescriptor.digest).Split(':')[1]
                    $nestedPath = Join-Path $temporaryDirectory "blobs\sha256\$nestedDigest"
                    $nestedIndex = Get-Content -LiteralPath $nestedPath -Raw | ConvertFrom-Json
                    $nestedIndex.manifests
                }
                else { $rootDescriptor }
            }
        )
        $imageDescriptors = @($candidateDescriptors | Where-Object {
            $platformProperty = $_.PSObject.Properties['platform']
            $null -ne $platformProperty -and $null -ne $platformProperty.Value -and
            $platformProperty.Value.os -eq 'linux' -and
            $platformProperty.Value.architecture -in @('amd64', 'arm64')
        })
        Assert-Release ($imageDescriptors.Count -eq 2) `
            'OCI archive must contain linux/amd64 and linux/arm64 images.'

        $platformResults = @(
            foreach ($descriptor in $imageDescriptors) {
                $architecture = [string]$descriptor.platform.architecture
                $manifestDigest = ([string]$descriptor.digest).Split(':')[1]
                $imageManifestPath = Join-Path $temporaryDirectory "blobs\sha256\$manifestDigest"
                $imageManifest = Get-Content -LiteralPath $imageManifestPath -Raw | ConvertFrom-Json
                $configDigest = ([string]$imageManifest.config.digest).Split(':')[1]
                $configPath = Join-Path $temporaryDirectory "blobs\sha256\$configDigest"
                $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
                $labels = $config.config.Labels
                Assert-Release ($labels.'org.opencontainers.image.version' -ceq "$($manifest.version)+$($manifest.build_number)") `
                    "OCI version label mismatch for linux/$architecture."
                Assert-Release ($labels.'org.opencontainers.image.revision' -match '^[0-9a-f]{40}(?:-dirty)?$') `
                    "OCI revision label is invalid for linux/$architecture."

                $lastLayer = @($imageManifest.layers)[-1]
                $layerDigest = ([string]$lastLayer.digest).Split(':')[1]
                $platformRoot = Join-Path $temporaryDirectory $architecture
                [IO.Directory]::CreateDirectory($platformRoot) | Out-Null
                $null = Invoke-NativeCapture -FilePath 'tar' `
                    -Arguments @('-xf', (Join-Path $temporaryDirectory "blobs\sha256\$layerDigest"), '-C', $platformRoot) `
                    -Description "linux/$architecture application layer extraction"
                $binaryPath = Join-Path $platformRoot 'termlink-server'
                Assert-Release (Test-Path -LiteralPath $binaryPath -PathType Leaf) `
                    "Server binary missing from linux/$architecture image."
                $stream = [IO.File]::OpenRead($binaryPath)
                try {
                    $header = New-Object byte[] 20
                    $bytesRead = $stream.Read($header, 0, $header.Length)
                }
                finally { $stream.Dispose() }
                Assert-Release ($bytesRead -eq 20 -and $header[0] -eq 0x7f -and
                    $header[1] -eq 0x45 -and $header[2] -eq 0x4c -and $header[3] -eq 0x46) `
                    "Server binary is not ELF for linux/$architecture."
                $machine = [BitConverter]::ToUInt16($header, 18)
                $expectedMachine = if ($architecture -eq 'amd64') { 62 } else { 183 }
                Assert-Release ($machine -eq $expectedMachine) `
                    "ELF architecture mismatch for linux/$architecture (machine $machine)."
                [pscustomobject][ordered]@{
                    platform = "linux/$architecture"
                    elf_machine = $machine
                    revision = $labels.'org.opencontainers.image.revision'
                    user = $config.config.User
                }
            }
        )
    }
    finally {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
        if ($resolvedTemporary.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

[pscustomobject][ordered]@{
    verified = $true
    version = [string]$manifest.version
    build_number = [int]$manifest.build_number
    release_mode = [string]$manifest.release_mode
    artifacts = $hashResults
    android = $androidResult
    windows = $windowsResult
    server = $platformResults
} | ConvertTo-Json -Depth 8
