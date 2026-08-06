[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [Alias('CertificatePfxPath', 'SigningCertificatePath')]
    [string]$PfxPath,
    [Parameter(Mandatory)]
    [Alias('PasswordPath', 'CertificatePasswordPath')]
    [string]$PasswordFilePath,
    [Alias('CertificatePath', 'PublisherCertificatePath', 'CerPath', 'CertificateCerPath')]
    [string]$PublicCertificatePath,
    [Parameter(Mandatory)]
    [string]$Publisher,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$PackageIdentityName,
    [string]$TimestampUrl,
    [string]$DotNetPath = (Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'),
    [Alias('Development')]
    [switch]$DevelopmentTrust,
    [switch]$ConfirmDevelopmentTrust,
    [switch]$ConfirmLifecycleMutation,
    [switch]$Preflight,
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\item7-windows-msix-lifecycle.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ConfirmLifecycleMutation) { $ConfirmPreference = 'None' }

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Normalize-Thumbprint {
    param([AllowNull()][string]$Value)
    if ($null -eq $Value) { return '' }
    return ($Value -replace '\s', '').ToUpperInvariant()
}

function Get-RawBytesHash {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    try { return (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
    finally { $stream.Dispose() }
}

function Get-PlainTextPassword {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    Assert-True (Test-Path -LiteralPath $resolved -PathType Leaf) 'The certificate password file was not found.'
    $value = [IO.File]::ReadAllText($resolved)
    $value = $value.TrimEnd([char]13, [char]10)
    Assert-True (-not [string]::IsNullOrWhiteSpace($value)) 'The certificate password file is empty.'
    return $value
}

function Find-WindowsSdkTool {
    param([Parameter(Mandatory)][string]$Name)
    $programFilesX86 = ${env:ProgramFiles(x86)}
    Assert-True (-not [string]::IsNullOrWhiteSpace($programFilesX86)) 'Program Files (x86) is unavailable.'
    $binRoot = Join-Path $programFilesX86 'Windows Kits\10\bin'
    $candidates = @(Get-ChildItem -LiteralPath $binRoot -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $parsed = $null
            if ([Version]::TryParse($_.Name, [ref]$parsed)) {
                $candidate = Join-Path $_.FullName "x64\$Name"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    [pscustomobject]@{ Version = $parsed; Path = $candidate }
                }
            }
        } | Sort-Object Version -Descending)
    if ($candidates.Count -eq 0) { throw "Windows SDK tool not found: $Name" }
    return $candidates[0].Path
}

function Invoke-ExternalTool {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$Quiet
    )
    $output = @(& $FilePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if (-not $Quiet -or $exitCode -ne 0) {
        $output | ForEach-Object { Write-Verbose ([string]$_) }
    }
    if ($exitCode -ne 0) {
        throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $exitCode."
    }
    return $output
}

function Get-RegisteredPackages {
    param([Parameter(Mandatory)][string]$IdentityName)
    $packages = @()
    try {
        $packages += @(Get-AppxPackage -AllUsers -ErrorAction Stop)
    }
    catch {
        throw 'Unable to inspect all-user AppX packages for collision safety.'
    }
    try {
        $packages += @(Get-AppxPackage -ErrorAction Stop)
    }
    catch {
        throw 'Unable to inspect current-user AppX packages for collision safety.'
    }
    return @($packages | Where-Object {
        $null -ne $_ -and
        (([string]$_.Name -eq $IdentityName) -or
            ([string]$_.PackageFamilyName -like "$IdentityName`_*"))
    } | Sort-Object PackageFullName -Unique)
}

function Assert-NoPackageCollision {
    param(
        [Parameter(Mandatory)][string]$IdentityName,
        [Parameter(Mandatory)][string]$ExpectedPublisher
    )
    $registered = @(Get-RegisteredPackages -IdentityName $IdentityName)
    if ($registered.Count -gt 0) {
        $details = @($registered | ForEach-Object {
            $name = [string]$_.Name
            $publisher = [string]$_.Publisher
            if ([string]::IsNullOrWhiteSpace($publisher)) { $publisher = 'unknown publisher' }
            "$name ($publisher)"
        }) -join ', '
        throw "Refusing to continue: PackageIdentityName '$IdentityName' or its package family is already registered: $details"
    }

    # A package with the same publisher and family prefix can be affected by a
    # publisher-scoped deployment even when its Name property is unavailable.
    $publisherPackages = @()
    try {
        $publisherPackages = @(Get-AppxPackage -AllUsers -ErrorAction Stop |
            Where-Object { [string]$_.Publisher -eq $ExpectedPublisher })
    }
    catch {
        throw 'Unable to inspect same-publisher AppX packages for collision safety.'
    }
    foreach ($package in $publisherPackages) {
        $family = [string]$package.PackageFamilyName
        $name = [string]$package.Name
        if ($name -eq $IdentityName -or $family -like "$IdentityName`_*") {
            throw "Refusing to continue: an existing package with publisher '$ExpectedPublisher' would share the target package family."
        }
    }
}

function Get-PlatformState {
    if ($PSVersionTable.PSEdition -ne 'Desktop') {
        throw "Run this AppX lifecycle test with Windows PowerShell 5.1: $env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
    }
    $appxCommands = @(
        'Get-AppxPackage',
        'Add-AppxPackage',
        'Remove-AppxPackage',
        'Invoke-CommandInDesktopPackage'
    )
    foreach ($command in $appxCommands) {
        Assert-True ($null -ne (Get-Command -Name $command -ErrorAction SilentlyContinue)) `
            "Required AppX command is unavailable: $command"
    }
    $osVersion = [Environment]::OSVersion.Version
    Assert-True ($osVersion.Major -ge 10) 'Windows 10 or newer is required for MSIX lifecycle validation.'
    $developerMode = $false
    try {
        $unlock = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
            -ErrorAction Stop
        $developerMode = ([int]$unlock.AllowDevelopmentWithoutDevLicense -eq 1) -or
            ([int]$unlock.AllowAllTrustedApps -eq 1)
    }
    catch {
        $developerMode = $false
    }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    return [pscustomobject]@{
        OsVersion = $osVersion.ToString()
        DeveloperModeEnabled = $developerMode
        IsElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
}

function Assert-Certificate {
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [Parameter(Mandatory)][string]$ExpectedPublisher
    )
    Assert-True $Certificate.HasPrivateKey 'The PFX certificate does not contain a private key.'
    Assert-True ([string]::Equals($Certificate.Subject, $ExpectedPublisher, [StringComparison]::Ordinal)) `
        "Certificate subject does not exactly match Publisher '$ExpectedPublisher'."
    $now = [DateTime]::UtcNow
    Assert-True ($Certificate.NotBefore.ToUniversalTime() -le $now -and
        $Certificate.NotAfter.ToUniversalTime() -gt $now) 'The signing certificate is not currently valid.'
    $allowsCodeSigning = $false
    foreach ($extension in @($Certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })) {
        foreach ($usage in ([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$extension).EnhancedKeyUsages) {
            if ($usage.Value -eq '1.3.6.1.5.5.7.3.3') { $allowsCodeSigning = $true }
        }
    }
    Assert-True $allowsCodeSigning 'The signing certificate EKU does not allow code signing.'
}

function Assert-PublicCertificate {
    param(
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$PrivateCertificate,
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$PublicCertificate,
        [Parameter(Mandatory)][string]$ExpectedPublisher
    )
    Assert-True (-not $PublicCertificate.HasPrivateKey) 'The public CER must not contain a private key.'
    Assert-True ([string]::Equals($PublicCertificate.Subject, $ExpectedPublisher, [StringComparison]::Ordinal)) `
        'The public certificate subject does not match Publisher.'
    Assert-True ((Normalize-Thumbprint $PublicCertificate.Thumbprint) -eq (Normalize-Thumbprint $PrivateCertificate.Thumbprint)) `
        'The public certificate does not exactly match the PFX certificate.'
}

function Get-PackageIdentityFromArchive {
    param([Parameter(Mandatory)][string]$PackagePath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = @($archive.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
        Assert-True ($entry.Count -eq 1) 'The MSIX must contain exactly one AppxManifest.xml.'
        $stream = $entry[0].Open()
        try {
            $readerSettings = [Xml.XmlReaderSettings]::new()
            $readerSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $readerSettings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($stream, $readerSettings)
            try {
                $manifest = [Xml.XmlDocument]::new()
                $manifest.XmlResolver = $null
                $manifest.Load($reader)
            }
            finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaces)
    Assert-True ($null -ne $identity) 'The MSIX Identity element is missing.'
    return [pscustomobject]@{
        Name = $identity.GetAttribute('Name')
        Publisher = $identity.GetAttribute('Publisher')
        Version = $identity.GetAttribute('Version')
    }
}

function Assert-PackageSignature {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$ExpectedThumbprint,
        [Parameter(Mandatory)][bool]$Development
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
    $status = [string]$signature.Status
    Assert-True ($null -ne $signature.SignerCertificate) 'The MSIX has no signer certificate.'
    Assert-True ((Normalize-Thumbprint $signature.SignerCertificate.Thumbprint) -eq (Normalize-Thumbprint $ExpectedThumbprint)) `
        'The MSIX signer does not match the requested certificate.'
    if ($Development) {
        Assert-True ($status -notin @('NotSigned', 'HashMismatch', 'NotSupported')) `
            "Development MSIX Authenticode verification failed: $status"
    }
    else {
        Assert-True ($status -eq 'Valid') `
            "Production MSIX Authenticode verification requires a trusted Valid chain; status was $status."
    }
    return $status
}

function Convert-ToLegacyTerminalPackage {
    param(
        [Parameter(Mandatory)][string]$SourcePackagePath,
        [Parameter(Mandatory)][string]$DestinationPackagePath,
        [Parameter(Mandatory)][string]$WorkRoot,
        [Parameter(Mandatory)][string]$CertificatePath,
        [Parameter(Mandatory)][Security.SecureString]$CertificatePassword,
        [AllowEmptyString()][string]$TimestampUrl
    )

    $resolvedWorkRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\') + '\'
    $legacyLayout = [IO.Path]::GetFullPath((Join-Path $WorkRoot 'legacy-layout'))
    Assert-True ($legacyLayout.StartsWith($resolvedWorkRoot, [StringComparison]::OrdinalIgnoreCase)) `
        'Legacy package layout escaped the lifecycle work directory.'

    $makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
    $signTool = Find-WindowsSdkTool -Name 'signtool.exe'
    Invoke-ExternalTool -FilePath $makeAppx -Arguments @(
        'unpack', '/p', [IO.Path]::GetFullPath($SourcePackagePath), '/d', $legacyLayout, '/o'
    ) -Quiet | Out-Null

    $manifestPath = Join-Path $legacyLayout 'AppxManifest.xml'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) `
        'The unpacked migration fixture is missing AppxManifest.xml.'
    $manifest = [Xml.XmlDocument]::new()
    $manifest.PreserveWhitespace = $true
    $manifest.XmlResolver = $null
    $manifest.Load($manifestPath)
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
    $extensions = @($manifest.SelectNodes(
        "/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension[@Category='windows.appExecutionAlias' or @Category='windows.appExtension']",
        $namespaces))
    Assert-True ($extensions.Count -eq 2) `
        'The current package did not contain both terminal integration declarations before migration fixture conversion.'
    foreach ($extension in $extensions) {
        [void]$extension.ParentNode.RemoveChild($extension)
    }
    $ignorable = @($manifest.DocumentElement.GetAttribute('IgnorableNamespaces').Split(' ') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne 'uap3' })
    $manifest.DocumentElement.SetAttribute('IgnorableNamespaces', ($ignorable -join ' '))
    $manifest.DocumentElement.RemoveAttribute('uap3', 'http://www.w3.org/2000/xmlns/')
    $xmlSettings = [Xml.XmlWriterSettings]::new()
    $xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $xmlSettings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($manifestPath, $xmlSettings)
    try { $manifest.Save($writer) }
    finally { $writer.Dispose() }

    $legacyTerminalRoot = [IO.Path]::GetFullPath((Join-Path $legacyLayout 'Terminal'))
    Assert-True ($legacyTerminalRoot.StartsWith(($legacyLayout.TrimEnd('\') + '\'),
            [StringComparison]::OrdinalIgnoreCase)) 'Legacy Terminal fixture path escaped its package layout.'
    if (Test-Path -LiteralPath $legacyTerminalRoot) {
        Remove-Item -LiteralPath $legacyTerminalRoot -Recurse -Force -ErrorAction Stop
    }

    Invoke-ExternalTool -FilePath $makeAppx -Arguments @(
        'pack', '/d', $legacyLayout, '/p', [IO.Path]::GetFullPath($DestinationPackagePath), '/o', '/h', 'SHA256'
    ) -Quiet | Out-Null
    $plainPassword = [Net.NetworkCredential]::new('', $CertificatePassword).Password
    try {
        $signArguments = @(
            'sign', '/fd', 'SHA256', '/f', [IO.Path]::GetFullPath($CertificatePath), '/p', $plainPassword
        )
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
        }
        Invoke-ExternalTool -FilePath $signTool -Arguments ($signArguments + @(
            [IO.Path]::GetFullPath($DestinationPackagePath)
        )) -Quiet | Out-Null
    }
    finally { $plainPassword = $null }
    Assert-True (Test-Path -LiteralPath $DestinationPackagePath -PathType Leaf) `
        'The signed legacy migration fixture was not created.'
    return [pscustomobject]@{
        PackagePath = [IO.Path]::GetFullPath($DestinationPackagePath)
        Sha256 = (Get-FileHash -LiteralPath $DestinationPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        TerminalIntegrationPresent = $false
    }
}

function Get-TerminalUserFragmentFingerprint {
    $root = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        'Microsoft\Windows Terminal\Fragments\TermLink'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        return [pscustomobject]@{ Exists = $false; FileCount = 0; Fingerprint = $null }
    }
    $resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $records = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $resolvedFile = [IO.Path]::GetFullPath($_.FullName)
        Assert-True ($resolvedFile.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) `
            'A user fragment file escaped the expected TermLink fragment directory.'
        [ordered]@{
            relative_path = $resolvedFile.Substring($resolvedRoot.Length).Replace('\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $resolvedFile -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $serialized = $records | ConvertTo-Json -Depth 4 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes([string]$serialized)
    return [pscustomobject]@{
        Exists = $true
        FileCount = $records.Count
        Fingerprint = (Get-RawBytesHash -Bytes $bytes).ToLowerInvariant()
    }
}

function Get-InstalledPackage {
    param([Parameter(Mandatory)][string]$IdentityName, [Parameter(Mandatory)][string]$ExpectedPublisher)
    $packages = @(Get-AppxPackage -Name $IdentityName -ErrorAction SilentlyContinue |
        Where-Object { [string]$_.Publisher -eq $ExpectedPublisher } |
        Sort-Object Version -Descending)
    if ($packages.Count -eq 0) { return $null }
    return $packages[0]
}

function Assert-InstalledVersion {
    param(
        [Parameter(Mandatory)][string]$IdentityName,
        [Parameter(Mandatory)][string]$ExpectedPublisher,
        [Parameter(Mandatory)][string]$ExpectedVersion
    )
    $package = Get-InstalledPackage -IdentityName $IdentityName -ExpectedPublisher $ExpectedPublisher
    Assert-True ($null -ne $package) "The expected package '$IdentityName' is not installed."
    Assert-True ([string]$package.Version -eq $ExpectedVersion) `
        "Installed package version '$($package.Version)' does not match expected '$ExpectedVersion'."
    return $package
}

function Assert-StartupDeclaration {
    param([Parameter(Mandatory)]$Package)
    $manifestPath = Join-Path $Package.InstallLocation 'AppxManifest.xml'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'Installed AppX manifest is missing.'
    $manifest = [Xml.XmlDocument]::new()
    $manifest.XmlResolver = $null
    $manifest.Load($manifestPath)
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
    $startup = $manifest.SelectSingleNode(
        "/f:Package/f:Applications/f:Application/f:Extensions/desktop:Extension[@Category='windows.startupTask']/desktop:StartupTask",
        $namespaces)
    Assert-True ($null -ne $startup) 'Installed package does not declare a desktop startup task.'
    Assert-True ($startup.GetAttribute('TaskId') -eq 'TermLinkTrayStartup') 'Startup task ID is incorrect.'
    Assert-True ($startup.GetAttribute('Enabled') -eq 'true') 'Startup task is not enabled by default.'
    Assert-True ($startup.ParentNode.GetAttribute('Executable') -eq 'TermLink.Tray.exe') `
        'Startup task does not launch the installed Tray executable.'
    return $true
}

function Assert-TerminalIntegration {
    param(
        [Parameter(Mandatory)]$Package,
        [switch]$RequireRegisteredAlias
    )
    $manifestPath = Join-Path $Package.InstallLocation 'AppxManifest.xml'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'Installed AppX manifest is missing.'
    $manifest = [Xml.XmlDocument]::new()
    $manifest.XmlResolver = $null
    $manifest.Load($manifestPath)
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
    $namespaces.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')

    $aliasExtension = $manifest.SelectSingleNode(
        "/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension[@Category='windows.appExecutionAlias']",
        $namespaces)
    Assert-True ($null -ne $aliasExtension -and
        $aliasExtension.GetAttribute('Executable') -eq 'TermLink.SessionHost.exe') `
        'Installed package does not map its execution alias to SessionHost.'
    $executionAlias = $aliasExtension.SelectSingleNode(
        'uap3:AppExecutionAlias/desktop:ExecutionAlias', $namespaces)
    Assert-True ($null -ne $executionAlias -and
        $executionAlias.GetAttribute('Alias') -eq 'termlink-session.exe') `
        'Installed package does not register the stable SessionHost alias.'

    $terminalExtension = $manifest.SelectSingleNode(
        "/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension[@Category='windows.appExtension']/uap3:AppExtension[@Name='com.microsoft.windows.terminal.settings']",
        $namespaces)
    Assert-True ($null -ne $terminalExtension -and
        $terminalExtension.GetAttribute('Id') -eq 'TermLink' -and
        $terminalExtension.GetAttribute('PublicFolder') -eq 'Terminal') `
        'Installed package does not expose the TermLink Windows Terminal settings extension.'

    $fragmentPath = Join-Path $Package.InstallLocation 'Terminal\Fragments\termlink.json'
    Assert-True (Test-Path -LiteralPath $fragmentPath -PathType Leaf) `
        'Installed package is missing its Windows Terminal profile fragment.'
    $fragment = Get-Content -Raw -LiteralPath $fragmentPath | ConvertFrom-Json
    $profiles = @($fragment.profiles)
    $expectedGuids = @(
        '{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}',
        '{b2025574-4cc8-5264-b577-7c6532eff95a}',
        '{7cf2d180-c8be-5b08-99a0-268bd2dc1d6f}',
        '{f5652f1d-a6da-5199-b595-3a1255ce04b4}'
    )
    Assert-True ($profiles.Count -eq $expectedGuids.Count) `
        'Installed package has an unexpected Windows Terminal profile count.'
    foreach ($profile in $profiles) {
        Assert-True ($expectedGuids -ccontains [string]$profile.guid) `
            "Installed package contains an unexpected Windows Terminal profile: $($profile.guid)"
        Assert-True ([string]$profile.commandline -cmatch '^termlink-session\.exe ') `
            "Installed Windows Terminal profile bypasses the SessionHost alias: $($profile.guid)"
        Assert-True (-not ([string]$profile.commandline).Contains(':\')) `
            "Installed Windows Terminal profile contains a machine-specific path: $($profile.guid)"
    }

    $aliasPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        'Microsoft\WindowsApps\termlink-session.exe'
    if ($RequireRegisteredAlias) {
        Assert-True (Test-Path -LiteralPath $aliasPath -PathType Leaf) `
            'Windows did not register the packaged SessionHost execution alias.'
    }
    return [pscustomobject]@{
        Alias = 'termlink-session.exe'
        AliasRegistered = (Test-Path -LiteralPath $aliasPath -PathType Leaf)
        ProfileCount = $profiles.Count
        ProfileGuids = $expectedGuids
        FragmentPath = $fragmentPath
    }
}

function Assert-LegacyTerminalIntegrationAbsent {
    param([Parameter(Mandatory)]$Package)

    $manifestPath = Join-Path $Package.InstallLocation 'AppxManifest.xml'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'Installed legacy AppX manifest is missing.'
    $manifest = [Xml.XmlDocument]::new()
    $manifest.XmlResolver = $null
    $manifest.Load($manifestPath)
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
    $terminalDeclarations = @($manifest.SelectNodes(
        "/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension[@Category='windows.appExecutionAlias' or @Category='windows.appExtension']",
        $namespaces))
    Assert-True ($terminalDeclarations.Count -eq 0) `
        'The legacy migration fixture unexpectedly contains terminal integration declarations.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $Package.InstallLocation 'Terminal') -PathType Container)) `
        'The legacy migration fixture unexpectedly contains packaged terminal profiles.'
    $aliasPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        'Microsoft\WindowsApps\termlink-session.exe'
    Assert-True (-not (Test-Path -LiteralPath $aliasPath -PathType Leaf)) `
        'The legacy migration fixture unexpectedly registered the SessionHost alias.'
    return [pscustomobject]@{
        AliasRegistered = $false
        ProfileCount = 0
        TerminalAppExtensionDeclared = $false
    }
}

function Find-PackagedProcesses {
    param([Parameter(Mandatory)][string]$InstallLocation)
    $root = [IO.Path]::GetFullPath($InstallLocation).TrimEnd('\') + '\'
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) }
        catch { $false }
    })
}

function Stop-TrackedProcesses {
    param([Parameter(Mandatory)][hashtable]$TrackedProcesses)
    foreach ($key in @($TrackedProcesses.Keys)) {
        $processId = [int]$key
        $expectedPath = [string]$TrackedProcesses[$key]
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        try {
            if ([string]::Equals([IO.Path]::GetFullPath($process.Path), [IO.Path]::GetFullPath($expectedPath),
                    [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
}

function Invoke-InPackageContext {
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $payload = [ordered]@{
        command = $Command
        arguments = @($Arguments)
    } | ConvertTo-Json -Compress
    $payloadBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    $commandScript = @"
`$ErrorActionPreference = 'Stop'
`$payloadJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$payloadBase64'))
`$payload = `$payloadJson | ConvertFrom-Json
`$output = @(& ([string]`$payload.command) @([string[]]`$payload.arguments) 2>&1)
`$exitCode = `$LASTEXITCODE
`$output
if (`$exitCode -ne 0) { throw "Packaged command failed with exit code `$exitCode." }
"@
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($commandScript))
    return @(Invoke-CommandInDesktopPackage `
        -PackageFamilyName $Package.PackageFamilyName `
        -AppId 'TermLinkTray' `
        -Command (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Args "-NoLogo -NoProfile -NonInteractive -EncodedCommand $encodedCommand" `
        -ErrorAction Stop)
}

function Invoke-TrayHealth {
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)][hashtable]$TrackedProcesses,
        [Parameter(Mandatory)][string]$StatusPath
    )
    $trayPath = Join-Path $Package.InstallLocation 'TermLink.Tray.exe'
    Assert-True (Test-Path -LiteralPath $trayPath -PathType Leaf) 'Installed Tray executable is missing.'
    $appUserModelID = "shell:AppsFolder\$($Package.PackageFamilyName)!TermLinkTray"
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList $appUserModelID | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 150
        $running = @(Find-PackagedProcesses -InstallLocation $Package.InstallLocation |
            Where-Object {
                try { [string]::Equals($_.Path, $trayPath, [StringComparison]::OrdinalIgnoreCase) }
                catch { $false }
            })
    } while ($running.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    Assert-True ($running.Count -ge 1) 'Installed Tray did not start.'
    foreach ($process in $running) { $TrackedProcesses[[string]$process.Id] = $trayPath }

    [void](Invoke-InPackageContext -Package $Package -Command $trayPath -Arguments @('--status-json', $StatusPath))
    $statusDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $StatusPath -PathType Leaf) -and
        [DateTime]::UtcNow -lt $statusDeadline) {
        Start-Sleep -Milliseconds 200
    }
    Assert-True (Test-Path -LiteralPath $StatusPath -PathType Leaf) 'Tray status output was not written.'
    $status = Get-Content -Raw -LiteralPath $StatusPath | ConvertFrom-Json
    Assert-True ([bool]$status.Success) 'Installed Tray reported an unhealthy Agent.'
    if ($status.PSObject.Properties.Name -contains 'RemoteAccessEnabled') {
        Assert-True ([bool]$status.RemoteAccessEnabled) 'Installed Agent did not enable remote access.'
    }
    $agentPath = Join-Path $Package.InstallLocation 'TermLink.Agent.exe'
    foreach ($process in @(Find-PackagedProcesses -InstallLocation $Package.InstallLocation)) {
        try { $TrackedProcesses[[string]$process.Id] = [string]$process.Path } catch { }
    }
    return [pscustomobject]@{
        TrayStarted = $true
        StatusSucceeded = [bool]$status.Success
        RemoteAccessEnabled = if ($status.PSObject.Properties.Name -contains 'RemoteAccessEnabled') {
            [bool]$status.RemoteAccessEnabled
        }
        else { $null }
        AgentPathPresent = (Test-Path -LiteralPath $agentPath -PathType Leaf)
    }
}

function Invoke-TerminalAliasHealth {
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)][string]$StatusRoot
    )
    $aliasPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        'Microsoft\WindowsApps\termlink-session.exe'
    Assert-True (Test-Path -LiteralPath $aliasPath -PathType Leaf) `
        'The installed SessionHost execution alias is unavailable for a runtime probe.'

    $expectedHostPath = Join-Path $Package.InstallLocation 'TermLink.SessionHost.exe'
    Assert-True (Test-Path -LiteralPath $expectedHostPath -PathType Leaf) `
        'The installed SessionHost executable is missing.'
    $trayPath = Join-Path $Package.InstallLocation 'TermLink.Tray.exe'
    Assert-True (Test-Path -LiteralPath $trayPath -PathType Leaf) `
        'The installed Tray executable is missing.'

    $probeId = [guid]::NewGuid().ToString('N')
    $controlPipeName = "termlink-msix-alias-$probeId"
    $bufferRoot = Join-Path $StatusRoot "alias-buffer-$probeId"
    [IO.Directory]::CreateDirectory($bufferRoot) | Out-Null
    $bootstrapStatusPath = Join-Path $StatusRoot "alias-bootstrap-$probeId.json"
    [void](Invoke-InPackageContext -Package $Package -Command $trayPath -Arguments @(
        '--control-pipe', $controlPipeName,
        '--buffer-root', $bufferRoot,
        '--status-json', $bootstrapStatusPath))
    $bootstrapDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $bootstrapStatusPath -PathType Leaf) -and
        [DateTime]::UtcNow -lt $bootstrapDeadline) {
        Start-Sleep -Milliseconds 200
    }
    Assert-True (Test-Path -LiteralPath $bootstrapStatusPath -PathType Leaf) `
        'The isolated packaged Agent did not write its bootstrap status.'
    $bootstrapStatus = Get-Content -Raw -LiteralPath $bootstrapStatusPath | ConvertFrom-Json
    Assert-True ([bool]$bootstrapStatus.Success -and [bool]$bootstrapStatus.RemoteAccessEnabled) `
        'The isolated packaged Agent was not healthy before the SessionHost alias probe.'

    # Use a bounded child command and redirected standard handles so this probe
    # exercises the same packaged SessionHost alias without opening or closing
    # any of the user's Windows Terminal windows.
    $marker = '__TERMLINK_MSIX_ALIAS_RUNTIME__'
    $childCommand = "cmd.exe /d /q /c `"echo $marker& set /p TERMLINK_ALIAS_PROBE=& exit /b 23`""
    $encodedChildCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($childCommand))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $aliasPath
    $start.Arguments = "--command-base64 $encodedChildCommand --managed-shell command-prompt --registry-pipe $controlPipeName --timeout-ms 30000 --diagnostics"
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $session = @()
    $actualHostPath = $null
    $standardOutput = ''
    $standardError = ''
    try {
        Assert-True $process.Start() 'The installed SessionHost execution alias did not start.'
        $actualHostPath = [Diagnostics.Process]::GetProcessById($process.Id).MainModule.FileName
        Assert-True ([string]::Equals(
            [IO.Path]::GetFullPath($actualHostPath),
            [IO.Path]::GetFullPath($expectedHostPath),
            [StringComparison]::OrdinalIgnoreCase)) `
            'The execution alias did not resolve to the installed SessionHost executable.'

        $registrationDeadline = [DateTime]::UtcNow.AddSeconds(10)
        $attempt = 0
        do {
            $attempt++
            $statusPath = Join-Path $StatusRoot ("alias-status-{0}.json" -f $attempt)
            Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue
            [void](Invoke-InPackageContext -Package $Package -Command $trayPath `
                -Arguments @(
                    '--control-pipe', $controlPipeName,
                    '--buffer-root', $bufferRoot,
                    '--status-json', $statusPath))
            $statusWriteDeadline = [DateTime]::UtcNow.AddSeconds(5)
            while (-not (Test-Path -LiteralPath $statusPath -PathType Leaf) -and
                -not $process.HasExited -and [DateTime]::UtcNow -lt $statusWriteDeadline) {
                Start-Sleep -Milliseconds 100
            }
            if (Test-Path -LiteralPath $statusPath -PathType Leaf) {
                $status = Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
                Assert-True ([bool]$status.Success) `
                    'The packaged Agent became unhealthy during the SessionHost alias probe.'
                $session = @($status.Summaries | Where-Object {
                    [int]$_.HostProcessId -eq $process.Id
                } | Select-Object -First 1)
            }
            if ($session.Count -eq 0) { Start-Sleep -Milliseconds 150 }
        } while ($session.Count -eq 0 -and -not $process.HasExited -and
            [DateTime]::UtcNow -lt $registrationDeadline)
        if ($session.Count -ne 1) {
            if (-not $process.HasExited) {
                try {
                    $process.StandardInput.WriteLine('abort')
                    $process.StandardInput.Flush()
                    $process.StandardInput.Close()
                    [void]$process.WaitForExit(5000)
                }
                catch { }
            }
            if ($process.HasExited) {
                $standardError = $process.StandardError.ReadToEnd()
            }
            $registryDiagnostics = @($standardError -split "`r?`n" | Where-Object {
                $_.StartsWith('__AGENT_REGISTRY_RESULT__:', [StringComparison]::Ordinal)
            } | Select-Object -Unique)
            $detail = " SessionHost exit=" + (
                $(if ($process.HasExited) { $process.ExitCode } else { 'running' })) +
                "; registry=" + $(if ($registryDiagnostics.Count -gt 0) {
                    $registryDiagnostics -join ','
                }
                else { 'no diagnostic' }) + '.'
            throw "The packaged SessionHost alias did not register its managed Shell with the Agent.$detail"
        }

        $process.StandardInput.WriteLine('continue')
        $process.StandardInput.Flush()
        $process.StandardInput.Close()
        Assert-True $process.WaitForExit(20000) `
            'The bounded SessionHost alias runtime probe did not exit naturally.'
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        Assert-True ($process.ExitCode -eq 23) `
            "The installed SessionHost alias returned unexpected exit code $($process.ExitCode)."
        Assert-True ($standardOutput.Contains($marker)) `
            'The installed SessionHost alias did not bridge the child Shell output.'

        return [pscustomobject]@{
            AliasStarted = $true
            HostPathMatchesPackage = $true
            SessionRegistered = $true
            SessionId = [string]$session[0].SessionId
            ShellType = [string]$session[0].ShellType
            OutputMarkerObserved = $true
            ExitCode = $process.ExitCode
            OpenedWindowsTerminalWindow = $false
        }
    }
    finally {
        if ($null -ne $process) {
            try {
                if (-not $process.HasExited) { $process.Kill() }
            }
            catch { }
            $process.Dispose()
        }
        try {
            [void](Invoke-InPackageContext -Package $Package -Command $trayPath -Arguments @(
                '--control-pipe', $controlPipeName,
                '--buffer-root', $bufferRoot,
                '--shutdown-agent'))
        }
        catch { }
        $standardOutput = $null
        $standardError = $null
    }
}

function Stop-PackagedAgent {
    param([Parameter(Mandatory)]$Package)
    $trayPath = Join-Path $Package.InstallLocation 'TermLink.Tray.exe'
    if (-not (Test-Path -LiteralPath $trayPath -PathType Leaf)) { return }
    try {
        [void](Invoke-InPackageContext -Package $Package -Command $trayPath -Arguments @('--shutdown-agent'))
    }
    catch { Write-Verbose 'Packaged Agent shutdown could not be requested.' }
}

function Invoke-AppxInstall {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [switch]$AllowDowngrade
    )
    $arguments = @{
        Path = $PackagePath
        ForceApplicationShutdown = $true
        ErrorAction = 'Stop'
    }
    if ($AllowDowngrade) { $arguments.ForceUpdateFromAnyVersion = $true }
    Add-AppxPackage @arguments
}

function Get-TransactionPath {
    param([Parameter(Mandatory)][object[]]$Output)
    $line = @($Output | ForEach-Object { [string]$_ } |
        Where-Object { $_ -match '(?i)transaction-[^\\/]+[\\/]transaction\.json$' } |
        Select-Object -Last 1)
    Assert-True ($line.Count -eq 1) 'Packaged updater did not return a transaction journal path.'
    return $line[0].Trim()
}

function Invoke-PackagedUpdater {
    param(
        [Parameter(Mandatory)]$Package,
        [Parameter(Mandatory)][string]$CurrentVersion,
        [Parameter(Mandatory)]$TargetManifest,
        [Parameter(Mandatory)]$RollbackManifest,
        [Parameter(Mandatory)][string]$TargetPackage,
        [Parameter(Mandatory)][string]$RollbackPackage,
        [Parameter(Mandatory)][string]$PublisherCertificatePath,
        [Parameter(Mandatory)][string]$CacheRoot,
        [Parameter(Mandatory)][bool]$Development
    )
    $updater = Join-Path $Package.InstallLocation 'TermLink.Updater.exe'
    Assert-True (Test-Path -LiteralPath $updater -PathType Leaf) 'Packaged updater executable is missing.'
    $arguments = @(
        'launch',
        '--current-version', $CurrentVersion,
        '--target-manifest', $TargetManifest.ManifestPath,
        '--target-signature', $TargetManifest.SignaturePath,
        '--target-package', $TargetPackage,
        '--rollback-manifest', $RollbackManifest.ManifestPath,
        '--rollback-signature', $RollbackManifest.SignaturePath,
        '--rollback-package', $RollbackPackage,
        '--publisher-certificate', $PublisherCertificatePath,
        '--cache-root', $CacheRoot
    )
    if ($Development) { $arguments += '--allow-untrusted-publisher' }
    $output = @(Invoke-InPackageContext -Package $Package -Command $updater -Arguments $arguments)
    $journalPath = $null
    try { $journalPath = Get-TransactionPath -Output $output } catch { }
    $launchDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($null -eq $journalPath -and [DateTime]::UtcNow -lt $launchDeadline) {
        $candidate = @(Get-ChildItem -LiteralPath $CacheRoot -Filter 'transaction.json' -File -Recurse `
            -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
        if ($candidate.Count -eq 1) { $journalPath = $candidate[0].FullName }
        else { Start-Sleep -Milliseconds 200 }
    }
    Assert-True (-not [string]::IsNullOrWhiteSpace($journalPath)) `
        'Packaged updater did not create a transaction journal.'
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        Start-Sleep -Milliseconds 500
        $journal = $null
        if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
            try { $journal = Get-Content -Raw -LiteralPath $journalPath | ConvertFrom-Json } catch { }
        }
        if ($null -ne $journal) {
            $state = [string]$journal.State
            if ($state -in @('4', 'Succeeded')) {
                $installed = Get-InstalledPackage -IdentityName $Package.Name -ExpectedPublisher $Package.Publisher
                Assert-True ($null -ne $installed -and [string]$installed.Version -eq [string]$TargetManifest.Version) `
                    'Updater journal succeeded but the target package version is not installed.'
                return [pscustomobject]@{ Journal = $journal; Installed = $installed; State = 'Succeeded' }
            }
            if ($state -in @('5', '6', '7', 'RolledBack', 'DeploymentFailed', 'RollbackFailed')) {
                throw "Packaged updater ended in terminal state $state."
            }
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    $state = if ($null -ne $journal) { [string]$journal.State } else { 'unknown' }
    throw "Packaged updater did not install target version; journal state was $state."
}

function Write-Evidence {
    param([Parameter(Mandatory)][hashtable]$Evidence, [Parameter(Mandatory)][string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $directory = [IO.Path]::GetDirectoryName($resolved)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = "$resolved.tmp-$([guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::WriteAllText(
            $temporary,
            ($Evidence | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $resolved -Force
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

$workRoot = $null
$securePassword = $null
$passwordText = $null
$certificate = $null
$publicCertificate = $null
$derivedCertificatePath = $null
$importedCertificate = $null
$importedCertificateThumbprint = $null
$importedCertificateRawHash = $null
$trackedProcesses = @{}
$installedByRun = $false
$packageMutationStarted = $false
$upgradedByRun = $false
$downgradedByRun = $false
$uninstalledByRun = $false
$cleanupErrors = @()
$primaryFailure = $null
$pendingEvidence = $null
$cleanupPackageAbsent = $false
$cleanupCertificateAbsent = (-not [bool]$DevelopmentTrust)
$cleanupTrackedProcessesAbsent = $false
$cleanupTemporaryWorkspaceAbsent = $false
$preflightOnly = $Preflight -or [bool]$WhatIfPreference
$development = [bool]$DevelopmentTrust
$currentVersion = '0.1.0.0'
$targetVersion = '0.2.0.0'
$healthChecks = [ordered]@{
    initial = $false
    upgraded = $false
    downgraded = $false
}
$currentSignatureStatus = $null
$targetSignatureStatus = $null
$terminalAliasPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
    'Microsoft\WindowsApps\termlink-session.exe'
$initialTerminal = $null
$upgradedTerminal = $null
$downgradedTerminal = $null
$initialAliasRuntime = $null
$upgradedAliasRuntime = $null
$downgradedAliasRuntime = $null
$userFragmentBefore = Get-TerminalUserFragmentFingerprint
$userFragmentAfter = $null

try {
    Assert-True (-not [string]::IsNullOrWhiteSpace($Publisher)) 'Publisher is required.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($PackageIdentityName)) 'PackageIdentityName is required.'
    Assert-True ($PackageIdentityName -notmatch '[^A-Za-z0-9.-]') 'PackageIdentityName contains unsupported characters.'
    Assert-True (Test-Path -LiteralPath $PfxPath -PathType Leaf) 'The PFX path was not found.'
    Assert-True (Test-Path -LiteralPath $DotNetPath -PathType Leaf) ".NET SDK executable was not found: $DotNetPath"
    Assert-True (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\package\Build-Msix.ps1') -PathType Leaf) `
        'The MSIX build script is missing.'
    Assert-True (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\package\New-UpdateManifest.ps1') -PathType Leaf) `
        'The update manifest script is missing.'
    if ($development) {
        Assert-True (-not [string]::IsNullOrWhiteSpace($Publisher)) 'Development Publisher is required.'
        if (-not $preflightOnly) {
            Assert-True $ConfirmDevelopmentTrust `
                'Development trust requires the explicit -ConfirmDevelopmentTrust switch.'
        }
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $timestampUri = [Uri]$TimestampUrl
            Assert-True ($timestampUri.IsAbsoluteUri -and $timestampUri.Scheme -eq 'https') `
                'TimestampUrl must be an absolute HTTPS URL when supplied.'
        }
    }
    else {
        Assert-True (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) `
            'Production validation requires an RFC 3161 HTTPS TimestampUrl.'
        $timestampUri = [Uri]$TimestampUrl
        Assert-True ($timestampUri.IsAbsoluteUri -and $timestampUri.Scheme -eq 'https') `
            'TimestampUrl must be an absolute HTTPS URL in production mode.'
        Assert-True (-not $ConfirmDevelopmentTrust) `
            '-ConfirmDevelopmentTrust is only valid with -DevelopmentTrust.'
    }

    $platform = Get-PlatformState
    $passwordText = Get-PlainTextPassword -Path $PasswordFilePath
    $securePassword = ConvertTo-SecureString -String $passwordText -AsPlainText -Force
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        [IO.Path]::GetFullPath($PfxPath),
        [Net.NetworkCredential]::new('', $passwordText).Password,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $passwordText = $null
    Assert-Certificate -Certificate $certificate -ExpectedPublisher $Publisher

    if (-not [string]::IsNullOrWhiteSpace($PublicCertificatePath)) {
        Assert-True (Test-Path -LiteralPath $PublicCertificatePath -PathType Leaf) 'The public certificate path was not found.'
        $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [IO.Path]::GetFullPath($PublicCertificatePath))
        Assert-PublicCertificate -PrivateCertificate $certificate -PublicCertificate $publicCertificate `
            -ExpectedPublisher $Publisher
    }

    # This check is deliberately before certificate import, package build, or
    # Add-AppxPackage so an existing user's registration is never touched.
    Assert-NoPackageCollision -IdentityName $PackageIdentityName -ExpectedPublisher $Publisher
    Assert-True (-not (Test-Path -LiteralPath $terminalAliasPath -PathType Leaf)) `
        "Refusing to replace an existing Windows app execution alias: $terminalAliasPath"

    if ($preflightOnly) {
        [pscustomobject]@{
            Preflight = $true
            DevelopmentTrust = $development
            DevelopmentTrustConfirmation = [bool]$ConfirmDevelopmentTrust
            Publisher = $Publisher
            PackageIdentityName = $PackageIdentityName
            CertificateThumbprint = (Normalize-Thumbprint $certificate.Thumbprint)
            CertificateValidNow = $true
            CertificateCodeSigningEku = $true
            Platform = $platform.OsVersion
            DeveloperModeEnabled = $platform.DeveloperModeEnabled
            IsElevated = $platform.IsElevated
            PackageCollision = $false
            ExecutionAliasCollision = $false
            AppxStateMutated = $false
            CertificateStoreMutated = $false
            DevelopmentTrustStore = if ($development) { 'LocalMachine/TrustedPeople' } else { $null }
        }
        return
    }

    # Invalidate any older success evidence before the first build, trust-store
    # import, or AppX mutation.  If this run is interrupted or cleanup fails,
    # callers cannot mistake a previous successful JSON file for this run.
    Write-Evidence -Evidence ([ordered]@{
        schema = 'termlink.windows-msix-lifecycle-validation'
        version = 2
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = 'validation in progress or failed before cleanup proof'
        passed = $false
        package_identity = $PackageIdentityName
        publisher = $Publisher
    }) -Path $EvidencePath

    $workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-MsixLifecycle-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($workRoot) | Out-Null
    $currentModernPackage = Join-Path $workRoot "TermLink-$currentVersion-current-x64.msix"
    $currentPackage = Join-Path $workRoot "TermLink-$currentVersion-legacy-x64.msix"
    $targetPackage = Join-Path $workRoot "TermLink-$targetVersion-x64.msix"
    $derivedCertificatePath = Join-Path $workRoot 'publisher.cer'
    [IO.File]::WriteAllBytes(
        $derivedCertificatePath,
        $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))

    $buildScript = Join-Path $PSScriptRoot '..\package\Build-Msix.ps1'
    $manifestScript = Join-Path $PSScriptRoot '..\package\New-UpdateManifest.ps1'
    $buildArguments = @{
        Publisher = $Publisher
        PublisherDisplayName = 'TermLink'
        CertificatePath = [IO.Path]::GetFullPath($PfxPath)
        CertificatePassword = $securePassword
        Development = $development
        TimestampUrl = $TimestampUrl
        DotNetPath = [IO.Path]::GetFullPath($DotNetPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $buildArguments.TimestampUrl = $TimestampUrl
    }
    $buildArguments.Version = $currentVersion
    $buildArguments.PackageIdentityName = $PackageIdentityName
    $buildArguments.OutputPath = $currentModernPackage
    $currentBuildOutput = @(& $buildScript @buildArguments)
    $currentBuild = @($currentBuildOutput | Where-Object { $null -ne $_.PSObject.Properties['Sha256'] } | Select-Object -Last 1)
    Assert-True ($currentBuild.Count -eq 1) 'The 0.1.0.0 MSIX build did not return metadata.'
    $legacyBuild = Convert-ToLegacyTerminalPackage `
        -SourcePackagePath $currentModernPackage `
        -DestinationPackagePath $currentPackage `
        -WorkRoot $workRoot `
        -CertificatePath $PfxPath `
        -CertificatePassword $securePassword `
        -TimestampUrl $TimestampUrl
    $buildArguments.Version = $targetVersion
    $buildArguments.OutputPath = $targetPackage
    $targetBuildOutput = @(& $buildScript @buildArguments)
    $targetBuild = @($targetBuildOutput | Where-Object { $null -ne $_.PSObject.Properties['Sha256'] } | Select-Object -Last 1)
    Assert-True ($targetBuild.Count -eq 1) 'The 0.2.0.0 MSIX build did not return metadata.'

    foreach ($packagePath in @($currentPackage, $targetPackage)) {
        Assert-True (Test-Path -LiteralPath $packagePath -PathType Leaf) 'The MSIX build output is missing.'
        $identity = Get-PackageIdentityFromArchive -PackagePath $packagePath
        Assert-True ($identity.Name -eq $PackageIdentityName -and $identity.Publisher -eq $Publisher) `
            'The built package identity or publisher does not exactly match the requested values.'
        $expectedPackageVersion = if ($packagePath -eq $currentPackage) { $currentVersion } else { $targetVersion }
        Assert-True ($identity.Version -eq $expectedPackageVersion) 'The built package version does not match the requested lifecycle phase.'
        $signatureStatus = Assert-PackageSignature -PackagePath $packagePath `
            -ExpectedThumbprint $certificate.Thumbprint -Development $development
        if ($packagePath -eq $currentPackage) { $currentSignatureStatus = $signatureStatus }
        else { $targetSignatureStatus = $signatureStatus }
    }

    if ($development) {
        $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
        try {
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $existing = @($store.Certificates | Where-Object {
                (Normalize-Thumbprint $_.Thumbprint) -eq (Normalize-Thumbprint $certificate.Thumbprint)
            })
            if ($existing.Count -eq 0) {
                if ($PSCmdlet.ShouldProcess('LocalMachine TrustedPeople', 'Import the exact development publisher certificate')) {
                    $importedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                        $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
                    $importedCertificateThumbprint = Normalize-Thumbprint $importedCertificate.Thumbprint
                    $importedCertificateRawHash = Get-RawBytesHash -Bytes $importedCertificate.RawData
                    $store.Add($importedCertificate)
                }
                else { throw 'Development certificate import was not confirmed.' }
            }
        }
        finally { $store.Dispose() }
    }

    $currentManifest = & $manifestScript `
        -PackagePath $currentPackage `
        -CertificatePath $PfxPath `
        -CertificatePassword $securePassword `
        -MinimumCurrentVersion $currentVersion `
        -Development:$development `
        -PublisherCertificateOutput $derivedCertificatePath
    $targetManifest = & $manifestScript `
        -PackagePath $targetPackage `
        -CertificatePath $PfxPath `
        -CertificatePassword $securePassword `
        -MinimumCurrentVersion $currentVersion `
        -Development:$development `
        -PublisherCertificateOutput $derivedCertificatePath
    Assert-True (Test-Path -LiteralPath $currentManifest.ManifestPath -PathType Leaf) 'Current update manifest was not created.'
    Assert-True (Test-Path -LiteralPath $targetManifest.ManifestPath -PathType Leaf) 'Target update manifest was not created.'

    if (-not $PSCmdlet.ShouldProcess("AppX package $PackageIdentityName", 'Register version 0.1.0.0')) { return }
    $packageMutationStarted = $true
    Invoke-AppxInstall -PackagePath $currentPackage
    $installedByRun = $true
    $installed = Assert-InstalledVersion -IdentityName $PackageIdentityName -ExpectedPublisher $Publisher -ExpectedVersion $currentVersion
    $initialStartup = Assert-StartupDeclaration -Package $installed
    $initialTerminal = Assert-LegacyTerminalIntegrationAbsent -Package $installed
    $initialStatus = Invoke-TrayHealth -Package $installed -TrackedProcesses $trackedProcesses `
        -StatusPath (Join-Path $workRoot 'initial-status.json')
    $healthChecks.initial = $initialStatus.StatusSucceeded

    $cacheRoot = Join-Path $workRoot 'updater-cache'
    [IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
    $targetResult = Invoke-PackagedUpdater -Package $installed -CurrentVersion $currentVersion `
        -TargetManifest $targetManifest -RollbackManifest $currentManifest `
        -TargetPackage $targetPackage -RollbackPackage $currentPackage `
        -PublisherCertificatePath $derivedCertificatePath -CacheRoot $cacheRoot -Development $development
    Assert-True ($null -ne $targetResult.Journal -and [string]$targetResult.State -eq 'Succeeded') `
        'Packaged updater did not complete in the Succeeded state.'
    $upgraded = Assert-InstalledVersion -IdentityName $PackageIdentityName -ExpectedPublisher $Publisher -ExpectedVersion $targetVersion
    $upgradedByRun = $true
    $upgradedStartup = Assert-StartupDeclaration -Package $upgraded
    $upgradedTerminal = Assert-TerminalIntegration -Package $upgraded -RequireRegisteredAlias
    Assert-True (-not $initialTerminal.AliasRegistered -and $upgradedTerminal.AliasRegistered) `
        'The legacy-to-unified upgrade did not add the SessionHost execution alias.'
    Assert-True ($initialTerminal.ProfileCount -eq 0 -and $upgradedTerminal.ProfileCount -eq 4) `
        'The legacy-to-unified upgrade did not add all packaged Windows Terminal profiles.'
    $upgradedStatus = Invoke-TrayHealth -Package $upgraded -TrackedProcesses $trackedProcesses `
        -StatusPath (Join-Path $workRoot 'upgraded-status.json')
    $healthChecks.upgraded = $upgradedStatus.StatusSucceeded
    $upgradedAliasRuntime = Invoke-TerminalAliasHealth -Package $upgraded -StatusRoot $workRoot

    Stop-PackagedAgent -Package $upgraded
    if (-not $PSCmdlet.ShouldProcess("AppX package $PackageIdentityName", 'Downgrade to version 0.1.0.0 with ForceUpdateFromAnyVersion')) { return }
    Invoke-AppxInstall -PackagePath $currentPackage -AllowDowngrade
    $downgraded = Assert-InstalledVersion -IdentityName $PackageIdentityName -ExpectedPublisher $Publisher -ExpectedVersion $currentVersion
    $downgradedByRun = $true
    $downgradedStartup = Assert-StartupDeclaration -Package $downgraded
    $downgradedTerminal = Assert-LegacyTerminalIntegrationAbsent -Package $downgraded
    $downgradedStatus = Invoke-TrayHealth -Package $downgraded -TrackedProcesses $trackedProcesses `
        -StatusPath (Join-Path $workRoot 'downgraded-status.json')
    $healthChecks.downgraded = $downgradedStatus.StatusSucceeded

    Stop-PackagedAgent -Package $downgraded
    Stop-TrackedProcesses -TrackedProcesses $trackedProcesses
    if (-not $PSCmdlet.ShouldProcess("AppX package $PackageIdentityName", 'Remove the package installed by this validation run')) { return }
    $toRemove = @(Get-AppxPackage -Name $PackageIdentityName -ErrorAction SilentlyContinue |
        Where-Object { [string]$_.Publisher -eq $Publisher })
    Assert-True ($toRemove.Count -gt 0) 'Refusing to uninstall: no currently installed package was recorded as owned by this run.'
    foreach ($package in $toRemove) { Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop }
    $uninstalledByRun = $true
    Assert-True (@(Get-AppxPackage -Name $PackageIdentityName -ErrorAction SilentlyContinue).Count -eq 0) `
        'The validation package is still registered after Remove-AppxPackage.'
    Assert-True (-not (Test-Path -LiteralPath $terminalAliasPath -PathType Leaf)) `
        'The SessionHost execution alias remains after package removal.'
    $userFragmentAfter = Get-TerminalUserFragmentFingerprint
    Assert-True ($userFragmentBefore.Exists -eq $userFragmentAfter.Exists -and
        $userFragmentBefore.FileCount -eq $userFragmentAfter.FileCount -and
        [string]$userFragmentBefore.Fingerprint -eq [string]$userFragmentAfter.Fingerprint) `
        'The lifecycle validation changed the user-owned Windows Terminal Fragment directory.'

    $sourceFiles = @(
        'package\AppxManifest.xml',
        'package\Build-Msix.ps1',
        'package\Terminal\Fragments\termlink.json',
        'package\New-UpdateManifest.ps1',
        'src\AgentControl\AgentControl.cs',
        'src\SessionHost\SessionHost.cs',
        'src\Updater\Program.cs',
        'src\Updater\UpdateManifest.cs',
        'src\Updater\UpdateTransaction.cs',
        'src\Updater\PowerShellMsixDeployment.cs',
        'src\Tray\Program.cs',
        'tests\Invoke-DevelopmentMsixLifecycle.ps1',
        'tests\Validate-TrustedMsixLifecycle.ps1'
    )
    $sourceHashes = [ordered]@{}
    foreach ($relativePath in $sourceFiles) {
        $sourcePath = Join-Path $PSScriptRoot "..\$relativePath"
        Assert-True (Test-Path -LiteralPath $sourcePath -PathType Leaf) "Validation source is missing: $relativePath"
        $sourceHashes[$relativePath.Replace('\', '/')] =
            (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $trustStatus = if ($development) {
        "development Authenticode status: $currentSignatureStatus/$targetSignatureStatus"
    }
    else { "current=$currentSignatureStatus; target=$targetSignatureStatus; trusted chain required" }
    $classification = if ($development) {
        'development-only; exact publisher certificate may be imported into LocalMachine TrustedPeople after explicit confirmation'
    }
    else { 'production; Authenticode Valid trusted chain required and no certificate trust import performed' }
    $pendingEvidence = [ordered]@{
        schema = 'termlink.windows-msix-lifecycle-validation'
        version = 2
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = $classification
        passed = $false
        package_identity = $PackageIdentityName
        publisher = $Publisher
        current_version = $currentVersion
        target_version = $targetVersion
        trust_chain_status = $trustStatus
        certificate_thumbprint = (Normalize-Thumbprint $certificate.Thumbprint)
        development_trust_imported = ($null -ne $importedCertificateThumbprint)
        installed = $installedByRun
        upgraded = $upgradedByRun
        downgraded = $downgradedByRun
        updater_state = [string]$targetResult.State
        rollback_bundle_verified = ($targetResult.Journal -ne $null)
        direct_downgrade_verified = ($downgraded.Version.ToString() -eq $currentVersion)
        uninstalled = $uninstalledByRun
        startup_checks = [ordered]@{
            initial_tray_started = $initialStatus.TrayStarted
            upgraded_tray_started = $upgradedStatus.TrayStarted
            downgraded_tray_started = $downgradedStatus.TrayStarted
            initial_declared = [bool]$initialStartup
            upgraded_declared = [bool]$upgradedStartup
            downgraded_declared = [bool]$downgradedStartup
            startup_task_enabled = $true
        }
        terminal_checks = [ordered]@{
            execution_alias = 'termlink-session.exe'
            initial_alias_registered = $initialTerminal.AliasRegistered
            upgraded_alias_registered = $upgradedTerminal.AliasRegistered
            downgraded_alias_registered = $downgradedTerminal.AliasRegistered
            alias_removed_on_uninstall = $true
            profile_count = $upgradedTerminal.ProfileCount
            profile_guids = $upgradedTerminal.ProfileGuids
            legacy_to_unified_upgrade_verified = (
                -not $initialTerminal.AliasRegistered -and
                $initialTerminal.ProfileCount -eq 0 -and
                $upgradedTerminal.AliasRegistered -and
                $upgradedTerminal.ProfileCount -eq 4)
            unified_to_legacy_downgrade_cleanup_verified = (
                -not $downgradedTerminal.AliasRegistered -and
                $downgradedTerminal.ProfileCount -eq 0)
            user_fragment_present = $userFragmentBefore.Exists
            user_fragment_file_count = $userFragmentBefore.FileCount
            user_fragment_untouched = $true
            visible_profile_coexistence_verified = $false
            alias_runtime_initial = $initialAliasRuntime
            alias_runtime_upgraded = $upgradedAliasRuntime
            alias_runtime_downgraded = $downgradedAliasRuntime
            user_terminal_windows_opened_or_closed = $false
        }
        health_checks = $healthChecks
        target_msix_sha256 = $targetBuild[0].Sha256
        current_msix_sha256 = $legacyBuild.Sha256
        cleanup_checks = [ordered]@{
            package_absent = $false
            validation_owned_development_certificate_absent = $false
            tracked_processes_absent = $false
            temporary_workspace_absent = $false
        }
        source_sha256 = $sourceHashes
    }
}
catch {
    $primaryFailure = $_
}
finally {
    Stop-TrackedProcesses -TrackedProcesses $trackedProcesses
    if ($packageMutationStarted) {
        try {
            $remaining = @(Get-AppxPackage -Name $PackageIdentityName -ErrorAction SilentlyContinue |
                Where-Object { [string]$_.Publisher -eq $Publisher })
            foreach ($package in $remaining) {
                Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
            }
            $remainingAfterCleanup = @(Get-AppxPackage -Name $PackageIdentityName -ErrorAction Stop |
                Where-Object { [string]$_.Publisher -eq $Publisher })
            if ($remainingAfterCleanup.Count -ne 0) {
                throw 'The validation-owned package remains registered after cleanup.'
            }
            $cleanupPackageAbsent = $true
        }
        catch { $cleanupErrors += 'Owned package cleanup failed or could not be verified.' }
    }
    else { $cleanupPackageAbsent = $true }
    if ($null -ne $importedCertificateThumbprint) {
        try {
            $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            try {
                $matches = @($store.Certificates | Where-Object {
                    (Normalize-Thumbprint $_.Thumbprint) -eq $importedCertificateThumbprint -and
                    ((Get-RawBytesHash -Bytes $_.RawData) -eq $importedCertificateRawHash)
                })
                foreach ($matchToRemove in $matches) { $store.Remove($matchToRemove) }
            }
            finally { $store.Dispose() }
            $verifyStore = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
            try {
                $verifyStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
                $remainingCertificates = @($verifyStore.Certificates | Where-Object {
                    (Normalize-Thumbprint $_.Thumbprint) -eq $importedCertificateThumbprint -and
                    ((Get-RawBytesHash -Bytes $_.RawData) -eq $importedCertificateRawHash)
                })
                if ($remainingCertificates.Count -ne 0) {
                    throw 'The exact development publisher certificate remains trusted after cleanup.'
                }
                $cleanupCertificateAbsent = $true
            }
            finally { $verifyStore.Dispose() }
        }
        catch { $cleanupErrors += 'Exact development certificate cleanup failed or could not be verified.' }
    }
    else { $cleanupCertificateAbsent = $true }

    $ownedProcessesRemaining = @()
    foreach ($key in @($trackedProcesses.Keys)) {
        $processId = [int]$key
        $expectedPath = [string]$trackedProcesses[$key]
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        try {
            if ([string]::Equals([IO.Path]::GetFullPath($process.Path), [IO.Path]::GetFullPath($expectedPath),
                    [StringComparison]::OrdinalIgnoreCase)) {
                $ownedProcessesRemaining += $processId
            }
        }
        catch { }
    }
    if ($ownedProcessesRemaining.Count -eq 0) { $cleanupTrackedProcessesAbsent = $true }
    else { $cleanupErrors += 'Validation-owned process cleanup failed or could not be verified.' }

    if ($null -ne $importedCertificate) { $importedCertificate.Dispose() }
    if ($null -ne $publicCertificate) { $publicCertificate.Dispose() }
    if ($null -ne $certificate) { $certificate.Dispose() }
    if ($null -ne $securePassword) { $securePassword.Dispose() }
    $passwordText = $null
    if ($null -ne $workRoot) {
        $resolvedWork = [IO.Path]::GetFullPath($workRoot)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedWork).StartsWith('TermLink-MsixLifecycle-', [StringComparison]::Ordinal)) {
            try {
                Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction Stop
                if (Test-Path -LiteralPath $resolvedWork) {
                    throw 'The lifecycle temporary workspace still exists after cleanup.'
                }
                $cleanupTemporaryWorkspaceAbsent = $true
            }
            catch { $cleanupErrors += 'Lifecycle temporary workspace cleanup failed or could not be verified.' }
        }
        else { $cleanupErrors += 'Lifecycle temporary workspace failed its cleanup boundary check.' }
    }
    else { $cleanupTemporaryWorkspaceAbsent = $true }
}

if ($cleanupErrors.Count -gt 0) {
    throw ('MSIX lifecycle validation cleanup failed: ' + ($cleanupErrors -join ' '))
}
if ($null -ne $primaryFailure) { throw $primaryFailure }
Assert-True ($null -ne $pendingEvidence) 'The lifecycle validation completed without pending evidence.'
$pendingEvidence.cleanup_checks.package_absent = $cleanupPackageAbsent
$pendingEvidence.cleanup_checks.validation_owned_development_certificate_absent = $cleanupCertificateAbsent
$pendingEvidence.cleanup_checks.tracked_processes_absent = $cleanupTrackedProcessesAbsent
$pendingEvidence.cleanup_checks.temporary_workspace_absent = $cleanupTemporaryWorkspaceAbsent
Assert-True ($cleanupPackageAbsent -and $cleanupCertificateAbsent -and
    $cleanupTrackedProcessesAbsent -and $cleanupTemporaryWorkspaceAbsent) `
    'The lifecycle validation cleanup proof is incomplete.'
$pendingEvidence.passed = $true
Write-Evidence -Evidence $pendingEvidence -Path $EvidencePath
Write-Output "Trusted MSIX lifecycle validation passed. Evidence: $([IO.Path]::GetFullPath($EvidencePath))"
