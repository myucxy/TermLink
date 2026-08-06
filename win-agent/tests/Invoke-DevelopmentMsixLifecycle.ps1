[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$PackageIdentityName = 'TermLink.Terminal.InstallAudit',
    [string]$DotNetPath = (Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'),
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\item7-windows-msix-lifecycle.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function New-RandomText {
    param([ValidateRange(16, 128)][int]$ByteCount)
    $bytes = [byte[]]::new($ByteCount)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes)
    }
    finally {
        $rng.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

$workRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'TermLink-InstallAudit-Credential-' + [guid]::NewGuid().ToString('N'))
$certificate = $null
$rsa = $null
$password = $null
try {
    [IO.Directory]::CreateDirectory($workRoot) | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($sid)
    $acl.SetAccessRuleProtection($true, $false)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $sid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $workRoot -AclObject $acl

    $publisher = 'CN=TermLink Development Install Audit'
    $password = New-RandomText -ByteCount 32
    $rsa = [Security.Cryptography.RSA]::Create()
    $rsa.KeySize = 3072
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        [Security.Cryptography.X509Certificates.X500DistinguishedName]::new($publisher),
        $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
            $false, $false, 0, $true))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
            $true))
    $enhancedUsages = [Security.Cryptography.OidCollection]::new()
    [void]$enhancedUsages.Add(
        [Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
            $enhancedUsages, $false))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
            $request.PublicKey, $false))
    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddMinutes(-5),
        [DateTimeOffset]::UtcNow.AddDays(2))

    $pfxPath = Join-Path $workRoot 'install-audit.pfx'
    $passwordPath = Join-Path $workRoot 'install-audit-password.txt'
    [IO.File]::WriteAllBytes(
        $pfxPath,
        $certificate.Export(
            [Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
            $password))
    [IO.File]::WriteAllText(
        $passwordPath,
        $password,
        [Text.UTF8Encoding]::new($false))

    $ps51 = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $ps51 -PathType Leaf)) {
        throw 'Windows PowerShell 5.1 is required for AppX lifecycle validation.'
    }
    $lifecycleScript = Join-Path $PSScriptRoot 'Validate-TrustedMsixLifecycle.ps1'
    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-File', [IO.Path]::GetFullPath($lifecycleScript),
        '-PfxPath', $pfxPath,
        '-PasswordFilePath', $passwordPath,
        '-Publisher', $publisher,
        '-PackageIdentityName', $PackageIdentityName,
        '-DotNetPath', [IO.Path]::GetFullPath($DotNetPath),
        '-DevelopmentTrust',
        '-ConfirmDevelopmentTrust',
        '-ConfirmLifecycleMutation',
        '-EvidencePath', [IO.Path]::GetFullPath($EvidencePath)
    )
    & $ps51 @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Trusted MSIX lifecycle failed with exit code $LASTEXITCODE."
    }
}
finally {
    $password = $null
    if ($null -ne $certificate) { $certificate.Dispose() }
    if ($null -ne $rsa) { $rsa.Dispose() }
    if (Test-Path -LiteralPath $workRoot -PathType Container) {
        $resolvedRoot = [IO.Path]::GetFullPath($workRoot)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedRoot).StartsWith(
                'TermLink-InstallAudit-Credential-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        }
    }
}
