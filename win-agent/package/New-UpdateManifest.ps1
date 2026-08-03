[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [Parameter(Mandatory)]
    [string]$CertificatePath,
    [Parameter(Mandatory)]
    [Security.SecureString]$CertificatePassword,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [Parameter(Mandatory)]
    [string]$MinimumCurrentVersion,
    [ValidateRange(1, 31)]
    [int]$ValidityDays = 7,
    [switch]$Development,
    [string]$ManifestPath,
    [string]$SignaturePath,
    [string]$PublisherCertificateOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
$resolvedCertificate = [IO.Path]::GetFullPath($CertificatePath)
Assert-True (Test-Path -LiteralPath $resolvedPackage -PathType Leaf) "MSIX package not found: $resolvedPackage"
Assert-True (Test-Path -LiteralPath $resolvedCertificate -PathType Leaf) `
    "Signing certificate not found: $resolvedCertificate"

$versionParts = @($MinimumCurrentVersion.Split('.') | ForEach-Object { [int]$_ })
Assert-True ($versionParts.Count -eq 4 -and @($versionParts | Where-Object { $_ -gt 65535 }).Count -eq 0) `
    'MinimumCurrentVersion parts must each be between 0 and 65535.'

$plainPassword = [Net.NetworkCredential]::new('', $CertificatePassword).Password
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $resolvedCertificate,
    $plainPassword,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    Assert-True $certificate.HasPrivateKey 'The signing certificate does not contain a private key.'
    $signature = Get-AuthenticodeSignature -LiteralPath $resolvedPackage
    $acceptedStatuses = if ($Development) {
        @(
            [Management.Automation.SignatureStatus]::Valid,
            [Management.Automation.SignatureStatus]::NotTrusted,
            [Management.Automation.SignatureStatus]::UnknownError
        )
    } else {
        @([Management.Automation.SignatureStatus]::Valid)
    }
    Assert-True ($acceptedStatuses -contains $signature.Status) `
        "MSIX Authenticode signature is not acceptable: $($signature.Status)"
    Assert-True ($null -ne $signature.SignerCertificate -and
        $signature.SignerCertificate.Thumbprint -eq $certificate.Thumbprint) `
        'The MSIX signer does not match the update manifest certificate.'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackage)
    try {
        $manifestEntries = @($archive.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
        Assert-True ($manifestEntries.Count -eq 1) 'The MSIX must contain exactly one AppxManifest.xml.'
        $stream = $manifestEntries[0].Open()
        try {
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            try {
                $manifest = [Xml.XmlDocument]::new()
                $manifest.XmlResolver = $null
                $manifest.Load($reader)
            } finally {
                $reader.Dispose()
            }
        } finally {
            $stream.Dispose()
        }
    } finally {
        $archive.Dispose()
    }

    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaces)
    Assert-True ($null -ne $identity) 'The MSIX Identity element is missing.'
    $packageIdentity = $identity.GetAttribute('Name')
    $publisher = $identity.GetAttribute('Publisher')
    $version = $identity.GetAttribute('Version')
    Assert-True ([string]::Equals($publisher, $certificate.Subject, [StringComparison]::Ordinal)) `
        'The MSIX Publisher does not match the signing certificate subject.'

    $now = [DateTimeOffset]::UtcNow
    $payload = [ordered]@{
        schema = 'termlink.windows-update-manifest'
        schema_version = 1
        package_identity = $packageIdentity
        publisher = $publisher
        version = $version
        minimum_current_version = $MinimumCurrentVersion
        package_file = [IO.Path]::GetFileName($resolvedPackage)
        sha256 = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
        created_at_utc = $now.ToString('o')
        expires_at_utc = $now.AddDays($ValidityDays).ToString('o')
    }
    $json = $payload | ConvertTo-Json -Depth 3
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        $ManifestPath = [IO.Path]::ChangeExtension($resolvedPackage, '.update.json')
    }
    $resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
    if ([string]::IsNullOrWhiteSpace($SignaturePath)) {
        $SignaturePath = $resolvedManifest + '.sig'
    }
    $resolvedSignature = [IO.Path]::GetFullPath($SignaturePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedManifest)) | Out-Null
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedSignature)) | Out-Null
    [IO.File]::WriteAllText($resolvedManifest, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

    $manifestBytes = [IO.File]::ReadAllBytes($resolvedManifest)
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    try {
        Assert-True ($null -ne $rsa) 'The signing certificate does not contain an RSA private key.'
        $signatureBytes = $rsa.SignData(
            $manifestBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        [IO.File]::WriteAllBytes($resolvedSignature, $signatureBytes)
    } finally {
        if ($null -ne $rsa) { $rsa.Dispose() }
    }

    $resolvedPublicCertificate = $null
    if (-not [string]::IsNullOrWhiteSpace($PublisherCertificateOutput)) {
        $resolvedPublicCertificate = [IO.Path]::GetFullPath($PublisherCertificateOutput)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedPublicCertificate)) | Out-Null
        [IO.File]::WriteAllBytes(
            $resolvedPublicCertificate,
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }

    [pscustomobject]@{
        ManifestPath = $resolvedManifest
        SignaturePath = $resolvedSignature
        PackagePath = $resolvedPackage
        PackageIdentity = $packageIdentity
        Version = $version
        MinimumCurrentVersion = $MinimumCurrentVersion
        Publisher = $publisher
        CertificateThumbprint = $certificate.Thumbprint
        PublisherCertificatePath = $resolvedPublicCertificate
        Sha256 = $payload.sha256
    }
}
finally {
    $certificate.Dispose()
    $plainPassword = $null
}
