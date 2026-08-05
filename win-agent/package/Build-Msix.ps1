[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0.0',
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$PackageIdentityName = 'TermLink.Terminal',
    [string]$Publisher = 'CN=TermLink Development',
    [string]$PublisherDisplayName = 'TermLink',
    [Parameter(Mandatory)]
    [string]$CertificatePath,
    [Parameter(Mandatory)]
    [Security.SecureString]$CertificatePassword,
    [string]$TimestampUrl,
    [switch]$Development,
    [string]$DotNetPath = (Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\TermLink-$Version-x64.msix")
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Find-WindowsSdkTool {
    param([Parameter(Mandatory)][string]$Name)
    $binRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidates = @(Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $parsed = $null
            if ([Version]::TryParse($_.Name, [ref]$parsed)) {
                $path = Join-Path $_.FullName "x64\$Name"
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    [pscustomobject]@{ Version = $parsed; Path = $path }
                }
            }
        } | Sort-Object Version -Descending)
    if ($candidates.Count -eq 0) { throw "Windows SDK tool not found: $Name" }
    return $candidates[0].Path
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$Quiet
    )
    $output = @(& $FilePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if (-not $Quiet -or $exitCode -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
    }
    if ($exitCode -ne 0) {
        throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $exitCode."
    }
}

function Confirm-EmbeddedSignature {
    param(
        [Parameter(Mandatory)][string]$SignToolPath,
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowDevelopmentRoot
    )
    $output = @(& $SignToolPath verify /pa /all $Path 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        $output | ForEach-Object { Write-Host $_ }
        return
    }
    $text = $output -join "`n"
    if ($AllowDevelopmentRoot -and $exitCode -eq 1 -and
        $text.Contains('terminated in a root') -and
        $text.Contains('not trusted by the trust provider') -and
        -not $text.Contains('hash of the file does not match') -and
        -not $text.Contains('No signature found')) {
        Write-Host 'Development signature is cryptographically intact; its ephemeral root is intentionally not system-trusted.'
        return
    }
    $output | ForEach-Object { Write-Host $_ }
    throw "Embedded signature verification failed for '$Path'."
}

$versionParts = @($Version.Split('.') | ForEach-Object { [int]$_ })
Assert-True ($versionParts.Count -eq 4 -and @($versionParts | Where-Object { $_ -gt 65535 }).Count -eq 0) `
    'MSIX version parts must each be between 0 and 65535.'
Assert-True (-not [string]::IsNullOrWhiteSpace($Publisher)) 'Publisher is required.'
Assert-True (-not [string]::IsNullOrWhiteSpace($PublisherDisplayName)) 'Publisher display name is required.'
Assert-True (Test-Path -LiteralPath $DotNetPath -PathType Leaf) ".NET SDK not found: $DotNetPath"
Assert-True (Test-Path -LiteralPath $CertificatePath -PathType Leaf) `
    "Signing certificate not found: $CertificatePath"
if (-not $Development) {
    Assert-True (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) `
        'A trusted RFC 3161 TimestampUrl is required for non-development packages.'
}
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $timestampUri = [Uri]$TimestampUrl
    Assert-True ($timestampUri.IsAbsoluteUri -and $timestampUri.Scheme -eq 'https') `
        'TimestampUrl must be an absolute HTTPS URL.'
}

$plainPassword = [Net.NetworkCredential]::new('', $CertificatePassword).Password
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    [IO.Path]::GetFullPath($CertificatePath),
    $plainPassword,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    Assert-True $certificate.HasPrivateKey 'The signing certificate does not contain a private key.'
    Assert-True ([string]::Equals($certificate.Subject, $Publisher, [StringComparison]::Ordinal)) `
        "Manifest publisher '$Publisher' does not match certificate subject '$($certificate.Subject)'."
    $now = [DateTime]::UtcNow
    Assert-True ($certificate.NotBefore.ToUniversalTime() -le $now -and
        $certificate.NotAfter.ToUniversalTime() -gt $now) 'The signing certificate is not currently valid.'
    $eku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    $allowsCodeSigning = $false
    foreach ($extension in $eku) {
        foreach ($usage in ([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$extension).EnhancedKeyUsages) {
            if ($usage.Value -eq '1.3.6.1.5.5.7.3.3') { $allowsCodeSigning = $true }
        }
    }
    Assert-True $allowsCodeSigning 'The signing certificate is not valid for code signing.'

    $makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
    $signTool = Find-WindowsSdkTool -Name 'signtool.exe'
    $workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Msix-' + [guid]::NewGuid().ToString('N'))
    $layout = Join-Path $workRoot 'layout'
    $temporaryPackage = Join-Path $workRoot 'TermLink.msix'
    [IO.Directory]::CreateDirectory($layout) | Out-Null
    try {
        $projects = @(
            'src\Agent\TermLink.Agent.csproj',
            'src\SessionHost\TermLink.SessionHost.csproj',
            'src\Tray\TermLink.Tray.csproj'
        )
        foreach ($project in $projects) {
            Invoke-Tool -FilePath $DotNetPath -Arguments @(
                'publish', (Join-Path $PSScriptRoot "..\$project"),
                '--configuration', 'Release',
                '--runtime', 'win-x64',
                '--self-contained', 'true',
                '--output', $layout,
                '--nologo',
                '-p:DebugSymbols=false',
                '-p:DebugType=None',
                '-p:PublishSingleFile=false',
                "-p:Version=$Version"
            )
        }
        $updaterPublish = Join-Path $workRoot 'updater-publish'
        Invoke-Tool -FilePath $DotNetPath -Arguments @(
            'publish', (Join-Path $PSScriptRoot '..\src\Updater\TermLink.Updater.csproj'),
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--output', $updaterPublish,
            '--nologo',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true',
            "-p:Version=$Version"
        )
        Copy-Item -LiteralPath (Join-Path $updaterPublish 'TermLink.Updater.exe') `
            -Destination (Join-Path $layout 'TermLink.Updater.exe')
        foreach ($name in @(
            'TermLink.Agent.exe',
            'TermLink.SessionHost.exe',
            'TermLink.Tray.exe',
            'TermLink.Updater.exe'
        )) {
            Assert-True (Test-Path -LiteralPath (Join-Path $layout $name) -PathType Leaf) `
                "Published executable is missing: $name"
        }
        Assert-True (Test-Path -LiteralPath (Join-Path $layout 'coreclr.dll') -PathType Leaf) `
            'The package layout is not self-contained.'
        [IO.File]::WriteAllBytes(
            (Join-Path $layout 'TermLink.UpdatePublisher.cer'),
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))

        $manifest = [Xml.XmlDocument]::new()
        $manifest.PreserveWhitespace = $true
        $manifest.Load((Join-Path $PSScriptRoot 'AppxManifest.xml'))
        $namespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
        $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespace)
        $identity.SetAttribute('Name', $PackageIdentityName)
        $identity.SetAttribute('Publisher', $Publisher)
        $identity.SetAttribute('Version', $Version)
        $manifest.SelectSingleNode('/f:Package/f:Properties/f:PublisherDisplayName', $namespace).InnerText = `
            $PublisherDisplayName
        $manifestPath = Join-Path $layout 'AppxManifest.xml'
        $xmlSettings = [Xml.XmlWriterSettings]::new()
        $xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
        $xmlSettings.Indent = $true
        $writer = [Xml.XmlWriter]::Create($manifestPath, $xmlSettings)
        try { $manifest.Save($writer) } finally { $writer.Dispose() }

        $assetRoot = Join-Path $layout 'Assets'
        [IO.Directory]::CreateDirectory($assetRoot) | Out-Null
        $brandAssetRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\assets\brand\windows'))
        foreach ($assetName in @('Square44x44Logo.png', 'Square150x150Logo.png', 'StoreLogo.png')) {
            $source = Join-Path $brandAssetRoot $assetName
            Assert-True (Test-Path -LiteralPath $source -PathType Leaf) "Brand asset is missing: $source"
            Copy-Item -LiteralPath $source -Destination (Join-Path $assetRoot $assetName)
        }

        $signArguments = @('sign', '/fd', 'SHA256', '/f', [IO.Path]::GetFullPath($CertificatePath), '/p', $plainPassword)
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
        }
        $ownedBinaries = @(Get-ChildItem -LiteralPath $layout -File |
            Where-Object { $_.Name -match '^TermLink\..+\.(exe|dll)$' } |
            Sort-Object Name)
        Assert-True ($ownedBinaries.Count -ge 4) 'No TermLink binaries were found to sign.'
        foreach ($binary in $ownedBinaries) {
            Invoke-Tool -FilePath $signTool -Arguments ($signArguments + @($binary.FullName))
            Confirm-EmbeddedSignature -SignToolPath $signTool -Path $binary.FullName `
                -AllowDevelopmentRoot:$Development
        }

        Invoke-Tool -FilePath $makeAppx -Arguments @('pack', '/d', $layout, '/p', $temporaryPackage, '/o', '/h', 'SHA256') `
            -Quiet
        Invoke-Tool -FilePath $signTool -Arguments ($signArguments + @($temporaryPackage))
        Confirm-EmbeddedSignature -SignToolPath $signTool -Path $temporaryPackage `
            -AllowDevelopmentRoot:$Development

        $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
        Move-Item -LiteralPath $temporaryPackage -Destination $resolvedOutput -Force
        [pscustomobject]@{
            PackagePath = $resolvedOutput
            PackageIdentityName = $PackageIdentityName
            Version = $Version
            Publisher = $Publisher
            CertificateThumbprint = $certificate.Thumbprint
            SignedTermLinkBinaries = $ownedBinaries.Count
            Sha256 = (Get-FileHash -LiteralPath $resolvedOutput -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    finally {
        $resolvedWork = [IO.Path]::GetFullPath($workRoot)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedWork).StartsWith('TermLink-Msix-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    $certificate.Dispose()
    $plainPassword = $null
}
