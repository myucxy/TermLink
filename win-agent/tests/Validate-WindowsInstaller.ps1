[CmdletBinding()]
param(
    [string]$DotNetPath = (Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'),
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase1-windows-installer.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Find-PackagedProcesses {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$InstallLocation,
        [int[]]$ExcludedIds = @()
    )
    $root = [IO.Path]::GetFullPath($InstallLocation).TrimEnd('\') + '\'
    return @(Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object {
        if ($ExcludedIds -contains $_.Id) { return $false }
        try {
            return $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            return $false
        }
    })
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

function Stop-ValidationProcesses {
    param([int[]]$ProcessIds)
    foreach ($processId in @($ProcessIds | Select-Object -Unique)) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}

Assert-True (Test-Path -LiteralPath $DotNetPath -PathType Leaf) ".NET SDK not found: $DotNetPath"
$identityName = 'TermLink.Terminal.Validation'
$packageVersion = '0.1.0.0'
$publisher = 'CN=TermLink Development'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-InstallerValidation-' + [guid]::NewGuid().ToString('N'))
$packagePath = Join-Path $workRoot 'TermLink-validation.msix'
$pfxPath = Join-Path $workRoot 'development-signing.pfx'
$payloadRoot = Join-Path $workRoot 'payload'
$statusPath = Join-Path $workRoot 'agent-status.json'
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
$validationProcessIds = @()
$installLocation = $null

try {
    $buildResult = & (Join-Path $PSScriptRoot '..\package\Build-Msix.ps1') `
        -Version $packageVersion `
        -PackageIdentityName $identityName `
        -Publisher $publisher `
        -PublisherDisplayName 'TermLink Development' `
        -CertificatePath $pfxPath `
        -CertificatePassword $securePassword `
        -Development `
        -DotNetPath $DotNetPath `
        -OutputPath $packagePath
    Assert-True ($null -ne $buildResult) 'MSIX build did not return package metadata.'
    Assert-True (Test-Path -LiteralPath $packagePath -PathType Leaf) 'Signed MSIX was not created.'
    Write-Output 'STEP=package_built'
    $packageSignature = Get-AuthenticodeSignature -LiteralPath $packagePath
    Assert-True ($null -ne $packageSignature.SignerCertificate -and
        $packageSignature.SignerCertificate.Thumbprint -eq $certificate.Thumbprint -and
        $packageSignature.Status -ne [Management.Automation.SignatureStatus]::HashMismatch -and
        $packageSignature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) `
        "MSIX embedded signature is not valid: $($packageSignature.Status)"

    $makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
    $unpackOutput = @(& $makeAppx unpack /p $packagePath /d $payloadRoot /o 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Signed MSIX could not be unpacked: $($unpackOutput -join [Environment]::NewLine)"
    }
    $installLocation = [IO.Path]::GetFullPath($payloadRoot)
    Write-Output 'STEP=payload_unpacked'

    $installedManifestPath = Join-Path $installLocation 'AppxManifest.xml'
    $installedManifest = [Xml.XmlDocument]::new()
    $installedManifest.Load($installedManifestPath)
    $namespaces = [Xml.XmlNamespaceManager]::new($installedManifest.NameTable)
    $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
    $identity = $installedManifest.SelectSingleNode('/f:Package/f:Identity', $namespaces)
    Assert-True ($identity.GetAttribute('Name') -eq $identityName) 'Packaged identity changed.'
    Assert-True ($identity.GetAttribute('Version') -eq $packageVersion) 'Packaged version changed.'
    $startup = $installedManifest.SelectSingleNode(
        "/f:Package/f:Applications/f:Application/f:Extensions/desktop:Extension[@Category='windows.startupTask']/desktop:StartupTask",
        $namespaces)
    Assert-True ($null -ne $startup) 'Installed package does not declare a desktop startup task.'
    Assert-True ($startup.GetAttribute('TaskId') -eq 'TermLinkTrayStartup') 'Startup task ID changed.'
    Assert-True ($startup.GetAttribute('Enabled') -eq 'true') 'Startup task is not enabled by default.'
    Assert-True ($startup.ParentNode.GetAttribute('Executable') -eq 'TermLink.Tray.exe') `
        'Startup task does not launch the Tray.'
    Assert-True (Test-Path -LiteralPath (Join-Path $installLocation 'coreclr.dll') -PathType Leaf) `
        'Installed package is not self-contained.'
    Assert-True (Test-Path -LiteralPath (Join-Path $installLocation 'TermLink.Updater.exe') -PathType Leaf) `
        'Installed package does not contain the external single-file Updater.'
    $updatePublisherPath = Join-Path $installLocation 'TermLink.UpdatePublisher.cer'
    Assert-True (Test-Path -LiteralPath $updatePublisherPath -PathType Leaf) `
        'Installed package does not contain the pinned update publisher certificate.'
    $updatePublisher = [Security.Cryptography.X509Certificates.X509Certificate2]::new($updatePublisherPath)
    try {
        Assert-True ($updatePublisher.Thumbprint -eq $certificate.Thumbprint) `
            'The packaged update publisher certificate does not match the package signer.'
    } finally {
        $updatePublisher.Dispose()
    }

    $signedBinaries = @(Get-ChildItem -LiteralPath $installLocation -File |
        Where-Object { $_.Name -match '^TermLink\..+\.(exe|dll)$' })
    Assert-True ($signedBinaries.Count -eq $buildResult.SignedTermLinkBinaries) `
        'Installed TermLink binary count differs from the signed build.'
    foreach ($binary in $signedBinaries) {
        $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
        Assert-True ($null -ne $signature.SignerCertificate -and
            $signature.SignerCertificate.Thumbprint -eq $certificate.Thumbprint -and
            $signature.Status -ne [Management.Automation.SignatureStatus]::HashMismatch -and
            $signature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) `
            "Packaged binary signature is invalid: $($binary.Name)"
    }
    Write-Output 'STEP=payload_verified'

    $beforeTrayIds = @(Get-Process -Name 'TermLink.Tray' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $beforeAgentIds = @(Get-Process -Name 'TermLink.Agent' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $installedTray = Join-Path $installLocation 'TermLink.Tray.exe'
    $firstTrayLaunch = Start-Process -FilePath $installedTray -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $trayProcesses = @()
    do {
        Start-Sleep -Milliseconds 100
        $trayProcesses = @(Find-PackagedProcesses -Name 'TermLink.Tray' -InstallLocation $installLocation `
            -ExcludedIds $beforeTrayIds)
    } while ($trayProcesses.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    $validationProcessIds += @($trayProcesses | Select-Object -ExpandProperty Id)
    if ($trayProcesses.Count -ne 1) {
        $firstTrayLaunch.Refresh()
        $exitDetail = if ($firstTrayLaunch.HasExited) { "exit code $($firstTrayLaunch.ExitCode)" } else { 'still running' }
        throw "Packaged Tray did not start exactly once; launcher is $exitDetail."
    }
    Write-Output 'STEP=tray_started'

    $secondTrayLaunch = Start-Process -FilePath $installedTray -PassThru
    Start-Sleep -Seconds 2
    $trayProcesses = @(Find-PackagedProcesses -Name 'TermLink.Tray' -InstallLocation $installLocation `
        -ExcludedIds $beforeTrayIds)
    $validationProcessIds += @($trayProcesses | Select-Object -ExpandProperty Id)
    Assert-True ($trayProcesses.Count -eq 1) 'Tray current-user single-instance protection failed.'
    $secondTrayLaunch.Refresh()
    Assert-True $secondTrayLaunch.HasExited 'The second Tray process did not exit after single-instance detection.'
    Write-Output 'STEP=tray_single_instance'

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $agentProcesses = @()
    do {
        Start-Sleep -Milliseconds 100
        $agentProcesses = @(Find-PackagedProcesses -Name 'TermLink.Agent' -InstallLocation $installLocation `
            -ExcludedIds $beforeAgentIds)
    } while ($agentProcesses.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    $validationProcessIds += @($agentProcesses | Select-Object -ExpandProperty Id)
    Assert-True ($agentProcesses.Count -eq 1) 'Packaged Tray did not start exactly one Agent.'
    Write-Output 'STEP=agent_started'

    $statusProcess = Start-Process -FilePath $installedTray `
        -ArgumentList @('--status-json', $statusPath) -Wait -PassThru
    Assert-True ($statusProcess.ExitCode -eq 0) 'Packaged Tray could not query Agent status.'
    $agentStatus = Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
    Assert-True $agentStatus.Success 'Packaged Agent status was not successful.'
    Assert-True $agentStatus.RemoteAccessEnabled 'Packaged Agent did not enable remote access.'
    Write-Output 'STEP=agent_status'
    $shutdownProcess = Start-Process -FilePath $installedTray `
        -ArgumentList '--shutdown-agent' -Wait -PassThru
    Assert-True ($shutdownProcess.ExitCode -eq 0) 'Packaged Tray could not stop the validation Agent.'
    Start-Sleep -Milliseconds 300
    Stop-ValidationProcesses -ProcessIds $validationProcessIds
    $validationProcessIds = @()

    $sourceFiles = @(
        'package\AppxManifest.xml',
        'package\Build-Msix.ps1',
        'src\Updater\Program.cs',
        'src\Tray\Program.cs',
        'tests\Validate-WindowsInstaller.ps1'
    )
    $sourceHashes = [ordered]@{}
    foreach ($relativePath in $sourceFiles) {
        $sourceHashes[$relativePath.Replace('\', '/')] =
            (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "..\$relativePath") -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $evidence = [ordered]@{
        schema = 'termlink.phase1-windows-installer-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = 'signed development MSIX, unpacked payload, startup declaration, and updater payload; not trusted production install or real update deployment'
        passed = $true
        package_identity = $identityName
        package_version = $packageVersion
        architecture = 'x64'
        self_contained = $true
        signed_msix = $true
        signed_termlink_binaries = [int]$buildResult.SignedTermLinkBinaries
        updater_single_file_payload = $true
        pinned_update_publisher_matches_package_signer = $true
        current_user_install = 'not executed: temporary root trust requires Windows security confirmation'
        unpacked_packaged_tray_launched = $true
        tray_single_instance = $true
        packaged_agent_status = 'AGENT_STATUS'
        startup_task_id = 'TermLinkTrayStartup'
        startup_task_enabled_by_default = $true
        startup_task_executable = 'TermLink.Tray.exe'
        current_user_uninstall = 'not applicable: package was not registered'
        system_certificate_store_unchanged = $true
        msix_sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        source_sha256 = $sourceHashes
    }
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    Write-Output 'STEP=evidence_written'
    Write-Output "Signed MSIX payload/startup validation passed. Evidence: $resolvedEvidence"
}
finally {
    Stop-ValidationProcesses -ProcessIds $validationProcessIds
    if (-not [string]::IsNullOrWhiteSpace($installLocation) -and
        [IO.Directory]::Exists($installLocation)) {
        $remainingValidationProcesses = @(
            Find-PackagedProcesses -Name 'TermLink.Tray' -InstallLocation $installLocation
            Find-PackagedProcesses -Name 'TermLink.Agent' -InstallLocation $installLocation
        )
        Stop-ValidationProcesses -ProcessIds @($remainingValidationProcesses | Select-Object -ExpandProperty Id)
    }
    $certificate.Dispose()
    $rsa.Dispose()
    $password = $null
    $resolvedWork = [IO.Path]::GetFullPath($workRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedWork).StartsWith(
            'TermLink-InstallerValidation-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
    }
}
