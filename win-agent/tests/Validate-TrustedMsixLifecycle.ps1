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
    [switch]$Preflight,
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\item7-windows-msix-lifecycle.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
    $appxCommands = @(
        'Get-AppxPackage',
        'Add-AppxPackage',
        'Remove-AppxPackage',
        'Invoke-CommandInDesktopPackage'
    )
    if ($PSVersionTable.PSEdition -eq 'Core' -and
        $null -eq (Get-Command -Name 'Get-AppxPackage' -ErrorAction SilentlyContinue)) {
        Import-Module Appx -UseWindowsPowerShell -ErrorAction Stop -WarningAction SilentlyContinue
    }
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
        $Evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8
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
$upgradedByRun = $false
$downgradedByRun = $false
$uninstalledByRun = $false
$cleanupErrors = @()
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
            AppxStateMutated = $false
            CertificateStoreMutated = $false
            DevelopmentTrustStore = if ($development) { 'LocalMachine/TrustedPeople' } else { $null }
        }
        return
    }

    $workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-MsixLifecycle-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($workRoot) | Out-Null
    $currentPackage = Join-Path $workRoot "TermLink-$currentVersion-x64.msix"
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
    $buildArguments.OutputPath = $currentPackage
    $currentBuildOutput = @(& $buildScript @buildArguments)
    $currentBuild = @($currentBuildOutput | Where-Object { $null -ne $_.PSObject.Properties['Sha256'] } | Select-Object -Last 1)
    Assert-True ($currentBuild.Count -eq 1) 'The 0.1.0.0 MSIX build did not return metadata.'
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
                    $store.Add($importedCertificate)
                    $importedCertificateThumbprint = Normalize-Thumbprint $importedCertificate.Thumbprint
                    $importedCertificateRawHash = Get-RawBytesHash -Bytes $importedCertificate.RawData
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
    Invoke-AppxInstall -PackagePath $currentPackage
    $installedByRun = $true
    $installed = Assert-InstalledVersion -IdentityName $PackageIdentityName -ExpectedPublisher $Publisher -ExpectedVersion $currentVersion
    $initialStartup = Assert-StartupDeclaration -Package $installed
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
    $upgradedStatus = Invoke-TrayHealth -Package $upgraded -TrackedProcesses $trackedProcesses `
        -StatusPath (Join-Path $workRoot 'upgraded-status.json')
    $healthChecks.upgraded = $upgradedStatus.StatusSucceeded

    Stop-PackagedAgent -Package $upgraded
    if (-not $PSCmdlet.ShouldProcess("AppX package $PackageIdentityName", 'Downgrade to version 0.1.0.0 with ForceUpdateFromAnyVersion')) { return }
    Invoke-AppxInstall -PackagePath $currentPackage -AllowDowngrade
    $downgraded = Assert-InstalledVersion -IdentityName $PackageIdentityName -ExpectedPublisher $Publisher -ExpectedVersion $currentVersion
    $downgradedByRun = $true
    $downgradedStartup = Assert-StartupDeclaration -Package $downgraded
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

    $sourceFiles = @(
        'package\AppxManifest.xml',
        'package\Build-Msix.ps1',
        'package\New-UpdateManifest.ps1',
        'src\Updater\Program.cs',
        'src\Updater\UpdateManifest.cs',
        'src\Updater\UpdateTransaction.cs',
        'src\Updater\PowerShellMsixDeployment.cs',
        'src\Tray\Program.cs',
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
    $evidence = [ordered]@{
        schema = 'termlink.windows-msix-lifecycle-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = $classification
        passed = $true
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
        health_checks = $healthChecks
        target_msix_sha256 = $targetBuild[0].Sha256
        current_msix_sha256 = $currentBuild[0].Sha256
        source_sha256 = $sourceHashes
    }
    Write-Evidence -Evidence $evidence -Path $EvidencePath
    Write-Output "Trusted MSIX lifecycle validation passed. Evidence: $([IO.Path]::GetFullPath($EvidencePath))"
}
catch {
    throw
}
finally {
    Stop-TrackedProcesses -TrackedProcesses $trackedProcesses
    if ($installedByRun -and -not $uninstalledByRun) {
        try {
            $remaining = @(Get-AppxPackage -Name $PackageIdentityName -ErrorAction SilentlyContinue |
                Where-Object { [string]$_.Publisher -eq $Publisher })
            foreach ($package in $remaining) {
                Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
            }
        }
        catch { $cleanupErrors += 'Owned package cleanup failed.' }
    }
    if ($null -ne $importedCertificateThumbprint) {
        try {
            $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
            $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            try {
                $matches = @($store.Certificates | Where-Object {
                    (Normalize-Thumbprint $_.Thumbprint) -eq $importedCertificateThumbprint -and
                    ((Get-RawBytesHash -Bytes $_.RawData) -eq $importedCertificateRawHash)
                })
                $matchToRemove = $matches | Select-Object -First 1
                if ($null -ne $matchToRemove) { $store.Remove($matchToRemove) }
            }
            finally { $store.Dispose() }
        }
        catch { $cleanupErrors += 'Exact development certificate cleanup failed.' }
    }
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
            Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if ($cleanupErrors.Count -gt 0) {
        Write-Warning ($cleanupErrors -join ' ')
    }
}
