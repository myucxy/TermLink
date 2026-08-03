[CmdletBinding()]
param(
    [string]$DotNetPath = (Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'),
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase1-windows-updater.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Updater {
    param([string[]]$Arguments, [int]$ExpectedExitCode = 0)
    $output = @(& $script:updaterExe @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $ExpectedExitCode) {
        throw "Updater exit code $exitCode, expected ${ExpectedExitCode}: $($output -join [Environment]::NewLine)"
    }
    return $output
}

Assert-True (Test-Path -LiteralPath $DotNetPath -PathType Leaf) ".NET SDK not found: $DotNetPath"
$identityName = 'TermLink.Terminal.UpdateValidation'
$currentVersion = '0.1.0.0'
$targetVersion = '0.2.0.0'
$publisher = 'CN=TermLink Development'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-UpdaterValidation-' + [guid]::NewGuid().ToString('N'))
$currentPackage = Join-Path $workRoot "TermLink-$currentVersion-x64.msix"
$targetPackage = Join-Path $workRoot "TermLink-$targetVersion-x64.msix"
$pfxPath = Join-Path $workRoot 'development-signing.pfx'
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
$securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
$rsa = [Security.Cryptography.RSA]::Create(3072)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    [Security.Cryptography.X509Certificates.X500DistinguishedName]::new($publisher),
    $rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256,
    [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$request.CertificateExtensions.Add(
    [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$request.CertificateExtensions.Add(
    [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
        $true))
$enhancedUsages = [Security.Cryptography.OidCollection]::new()
[void]$enhancedUsages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
$request.CertificateExtensions.Add(
    [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($enhancedUsages, $false))
$request.CertificateExtensions.Add(
    [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(2))
[IO.File]::WriteAllBytes(
    $pfxPath,
    $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))

try {
    $currentBuild = & (Join-Path $PSScriptRoot '..\package\Build-Msix.ps1') `
        -Version $currentVersion `
        -PackageIdentityName $identityName `
        -Publisher $publisher `
        -PublisherDisplayName 'TermLink Development' `
        -CertificatePath $pfxPath `
        -CertificatePassword $securePassword `
        -Development `
        -DotNetPath $DotNetPath `
        -OutputPath $currentPackage
    $targetBuild = & (Join-Path $PSScriptRoot '..\package\Build-Msix.ps1') `
        -Version $targetVersion `
        -PackageIdentityName $identityName `
        -Publisher $publisher `
        -PublisherDisplayName 'TermLink Development' `
        -CertificatePath $pfxPath `
        -CertificatePassword $securePassword `
        -Development `
        -DotNetPath $DotNetPath `
        -OutputPath $targetPackage
    Assert-True ($null -ne $currentBuild -and $null -ne $targetBuild) 'MSIX builds did not return metadata.'
    Write-Output 'STEP=packages_built'

    $currentManifest = & (Join-Path $PSScriptRoot '..\package\New-UpdateManifest.ps1') `
        -PackagePath $currentPackage `
        -CertificatePath $pfxPath `
        -CertificatePassword $securePassword `
        -MinimumCurrentVersion $currentVersion `
        -Development
    $targetManifest = & (Join-Path $PSScriptRoot '..\package\New-UpdateManifest.ps1') `
        -PackagePath $targetPackage `
        -CertificatePath $pfxPath `
        -CertificatePassword $securePassword `
        -MinimumCurrentVersion $currentVersion `
        -Development
    Write-Output 'STEP=manifests_signed'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packagedUpdaterRoot = Join-Path $workRoot 'packaged-updater'
    [IO.Directory]::CreateDirectory($packagedUpdaterRoot) | Out-Null
    $script:updaterExe = Join-Path $packagedUpdaterRoot 'TermLink.Updater.exe'
    $packagedCertificate = Join-Path $packagedUpdaterRoot 'TermLink.UpdatePublisher.cer'
    $archive = [IO.Compression.ZipFile]::OpenRead($targetPackage)
    try {
        foreach ($entryName in @('TermLink.Updater.exe', 'TermLink.UpdatePublisher.cer')) {
            $entry = $archive.GetEntry($entryName)
            Assert-True ($null -ne $entry) "Packaged update payload is missing: $entryName"
            $destination = Join-Path $packagedUpdaterRoot $entryName
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
        }
    } finally {
        $archive.Dispose()
    }
    $packagedCertificateValue = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $packagedCertificate)
    try {
        Assert-True ($packagedCertificateValue.Thumbprint -eq $certificate.Thumbprint) `
            'The packaged update publisher certificate does not match the package signer.'
    } finally {
        $packagedCertificateValue.Dispose()
    }
    Write-Output 'STEP=packaged_updater_extracted'

    $targetVerify = Invoke-Updater -Arguments @(
        'verify',
        '--manifest', $targetManifest.ManifestPath,
        '--signature', $targetManifest.SignaturePath,
        '--package', $targetPackage,
        '--publisher-certificate', $packagedCertificate,
        '--allow-untrusted-publisher'
    )
    $currentVerify = Invoke-Updater -Arguments @(
        'verify',
        '--manifest', $currentManifest.ManifestPath,
        '--signature', $currentManifest.SignaturePath,
        '--package', $currentPackage,
        '--publisher-certificate', $packagedCertificate,
        '--allow-untrusted-publisher'
    )
    Assert-True ((($targetVerify | Select-Object -Last 1) | ConvertFrom-Json).Version -eq $targetVersion) `
        'Target verification returned the wrong version.'
    Assert-True ((($currentVerify | Select-Object -Last 1) | ConvertFrom-Json).Version -eq $currentVersion) `
        'Rollback verification returned the wrong version.'
    Write-Output 'STEP=bundles_verified'

    $tamperedManifest = Join-Path $workRoot 'tampered.update.json'
    [IO.File]::Copy($targetManifest.ManifestPath, $tamperedManifest)
    [IO.File]::AppendAllText($tamperedManifest, ' ', [Text.UTF8Encoding]::new($false))
    [void](Invoke-Updater -Arguments @(
        'verify',
        '--manifest', $tamperedManifest,
        '--signature', $targetManifest.SignaturePath,
        '--package', $targetPackage,
        '--publisher-certificate', $packagedCertificate,
        '--allow-untrusted-publisher'
    ) -ExpectedExitCode 125)

    $tamperedRoot = Join-Path $workRoot 'tampered-package'
    [IO.Directory]::CreateDirectory($tamperedRoot) | Out-Null
    $tamperedPackage = Join-Path $tamperedRoot ([IO.Path]::GetFileName($targetPackage))
    [IO.File]::Copy($targetPackage, $tamperedPackage)
    $tamperStream = [IO.File]::Open($tamperedPackage, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $tamperStream.WriteByte(0) } finally { $tamperStream.Dispose() }
    [void](Invoke-Updater -Arguments @(
        'verify',
        '--manifest', $targetManifest.ManifestPath,
        '--signature', $targetManifest.SignaturePath,
        '--package', $tamperedPackage,
        '--publisher-certificate', $packagedCertificate,
        '--allow-untrusted-publisher'
    ) -ExpectedExitCode 125)
    Write-Output 'STEP=tamper_rejected'

    $validationProject = Join-Path $PSScriptRoot 'UpdaterValidation\TermLink.Updater.Validation.csproj'
    & $DotNetPath build $validationProject --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Updater state validation build failed with exit code $LASTEXITCODE." }
    $validationDll = Join-Path $PSScriptRoot `
        'UpdaterValidation\bin\Release\net8.0-windows\TermLink.Updater.Validation.dll'
    $stateOutput = @(& $DotNetPath $validationDll `
        --target-manifest $targetManifest.ManifestPath `
        --target-signature $targetManifest.SignaturePath `
        --target-package $targetPackage `
        --target-version $targetVersion `
        --rollback-manifest $currentManifest.ManifestPath `
        --rollback-signature $currentManifest.SignaturePath `
        --rollback-package $currentPackage `
        --current-version $currentVersion `
        --certificate $packagedCertificate 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Updater state validation failed: $($stateOutput -join [Environment]::NewLine)"
    }
    $stateResult = $stateOutput[-1] | ConvertFrom-Json
    Assert-True $stateResult.passed 'Updater state validation did not pass.'
    Assert-True ($stateResult.cases.Count -eq 5) 'Updater state validation did not run all cases.'
    Write-Output 'STEP=rollback_state_machine_verified'

    $sourceFiles = @(
        'package\Build-Msix.ps1',
        'package\New-UpdateManifest.ps1',
        'src\Updater\Program.cs',
        'src\Updater\UpdateManifest.cs',
        'src\Updater\UpdateTransaction.cs',
        'src\Updater\PowerShellMsixDeployment.cs',
        'tests\Validate-WindowsUpdater.ps1',
        'tests\UpdaterValidation\Program.cs'
    )
    $sourceHashes = [ordered]@{}
    foreach ($relativePath in $sourceFiles) {
        $sourceHashes[$relativePath.Replace('\', '/')] =
            (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "..\$relativePath") -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $evidence = [ordered]@{
        schema = 'termlink.phase1-windows-updater-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = 'development-signed MSIX/update manifests and simulated deployment failures; no package registration or production trust claim'
        passed = $true
        package_identity = $identityName
        current_version = $currentVersion
        target_version = $targetVersion
        publisher_thumbprint = $certificate.Thumbprint
        manifest_signature = 'RSA PKCS#1 v1.5 SHA-256 over exact UTF-8 manifest bytes'
        target_msix_sha256 = $targetBuild.Sha256
        rollback_msix_sha256 = $currentBuild.Sha256
        manifest_tamper_rejected = $true
        package_tamper_rejected = $true
        packaged_single_file_updater_executed = $true
        packaged_publisher_certificate_matched = $true
        deployment_state_cases = $stateResult.cases
        real_add_appx_package = 'not executed: production-trusted certificate is unavailable'
        system_certificate_store_unchanged = $true
        source_sha256 = $sourceHashes
    }
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    Write-Output "Windows updater validation passed. Evidence: $resolvedEvidence"
}
finally {
    $certificate.Dispose()
    $rsa.Dispose()
    $password = $null
    $resolvedWork = [IO.Path]::GetFullPath($workRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedWork).StartsWith(
            'TermLink-UpdaterValidation-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
    }
}
