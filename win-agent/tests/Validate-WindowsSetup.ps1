[CmdletBinding()]
param(
    [string]$DotNetPath = (Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'),
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase1-windows-setup.json'),
    [string]$OutputSetupPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-Setup {
    param(
        [Parameter(Mandatory)][string]$SetupPath,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $SetupPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Assert-True ($null -ne $process) 'The setup process did not start.'
    try {
        Assert-True $process.WaitForExit(120000) 'The setup process timed out.'
        Assert-True ($process.ExitCode -eq 0) `
            "The setup process failed with exit code $($process.ExitCode)."
    }
    finally {
        $process.Dispose()
    }
}

Assert-True (Test-Path -LiteralPath $DotNetPath -PathType Leaf) ".NET SDK not found: $DotNetPath"
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-SetupValidation-' + [guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $workRoot 'Installed TermLink'
$setupPath = if ([string]::IsNullOrWhiteSpace($OutputSetupPath)) {
    Join-Path $workRoot 'TermLink-Setup.exe'
} else {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputSetupPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
    $resolvedOutput
}
$pfxPath = Join-Path $workRoot 'development-signing.pfx'
$fragmentPath = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows Terminal\Fragments\TermLink\termlink.json'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$fragmentExisted = Test-Path -LiteralPath $fragmentPath -PathType Leaf
$fragmentBytes = if ($fragmentExisted) { [IO.File]::ReadAllBytes($fragmentPath) } else { $null }
$runKey = Get-ItemProperty -LiteralPath $runKeyPath -ErrorAction SilentlyContinue
$runExisted = $null -ne $runKey -and $null -ne $runKey.PSObject.Properties['TermLink']
$runValue = if ($runExisted) { [string]$runKey.TermLink } else { $null }
$installed = $false
$rsa = $null
$certificate = $null
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

try {
    $passwordText = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $password = ConvertTo-SecureString $passwordText -AsPlainText -Force
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        [Security.Cryptography.X509Certificates.X500DistinguishedName]::new('CN=TermLink Setup Validation'),
        $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $usages = [Security.Cryptography.OidCollection]::new()
    [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3', 'Code Signing'))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $false))
    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(2))
    [IO.File]::WriteAllBytes(
        $pfxPath,
        $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $passwordText))

    $buildOutput = @(& (Join-Path $PSScriptRoot '..\package\Build-Setup.ps1') `
        -Version '0.1.0.0' `
        -CertificatePath $pfxPath `
        -CertificatePassword $password `
        -Development `
        -DotNetPath $DotNetPath `
        -OutputPath $setupPath)
    # Build-Setup emits tool progress for interactive callers. Select the final
    # summary object instead of relying on pipeline cardinality.
    $result = @($buildOutput | Where-Object {
        $_ -is [psobject] -and $_.PSObject.Properties['SetupPath'] -and
        $_.PSObject.Properties['SelfContained'] -and $_.PSObject.Properties['EmbeddedPayload']
    } | Select-Object -Last 1)
    Assert-True (Test-Path -LiteralPath $setupPath -PathType Leaf) 'The single-file setup executable was not created.'
    Assert-True ($result.Count -eq 1 -and [bool]$result[0].SelfContained -and [bool]$result[0].EmbeddedPayload) `
        'The setup build did not report a self-contained embedded payload.'

    Invoke-Setup -SetupPath $setupPath -Arguments @(
        '--install-dir', $installRoot, '--no-startup', '--quiet')
    $installed = $true
    foreach ($name in @(
        'TermLink.Agent.exe',
        'TermLink.SessionHost.exe',
        'TermLink.Tray.exe',
        'TermLink.Updater.exe',
        'TermLink.UpdatePublisher.cer',
        'coreclr.dll',
        '.termlink-install.json'
    )) {
        Assert-True (Test-Path -LiteralPath (Join-Path $installRoot $name) -PathType Leaf) `
            "Installed setup payload is missing: $name"
    }

    Assert-True (Test-Path -LiteralPath $fragmentPath -PathType Leaf) `
        'The setup did not create the Windows Terminal fragment.'
    $fragment = Get-Content -Raw -LiteralPath $fragmentPath | ConvertFrom-Json
    $profiles = @($fragment.profiles)
    Assert-True ($profiles.Count -ge 2 -and $profiles.Count -le 4) `
        'The setup created an unexpected Windows Terminal profile count.'
    $expectedHostPrefix = '"' + (Join-Path $installRoot 'TermLink.SessionHost.exe') + '" '
    $mandatoryGuids = @(
        '{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}',
        '{b2025574-4cc8-5264-b577-7c6532eff95a}'
    )
    foreach ($guid in $mandatoryGuids) {
        Assert-True (@($profiles | Where-Object { [string]$_.guid -ceq $guid }).Count -eq 1) `
            "A mandatory Terminal profile is missing: $guid"
    }
    foreach ($profile in $profiles) {
        Assert-True ($profile.commandline.StartsWith(
                $expectedHostPrefix, [StringComparison]::OrdinalIgnoreCase)) `
            "Terminal profile does not use the selected installation directory: $($profile.guid)"
    }
    $afterRun = Get-ItemProperty -LiteralPath $runKeyPath -ErrorAction SilentlyContinue
    $afterRunValue = if ($null -ne $afterRun -and $null -ne $afterRun.PSObject.Properties['TermLink']) {
        [string]$afterRun.TermLink
    } else { $null }
    Assert-True ([string]::Equals($afterRunValue, $runValue, [StringComparison]::Ordinal)) `
        '--no-startup changed the current-user startup registration.'

    Invoke-Setup -SetupPath $setupPath -Arguments @(
        '--install-dir', $installRoot, '--no-startup', '--quiet')
    Invoke-Setup -SetupPath $setupPath -Arguments @('--uninstall', $installRoot, '--quiet')
    $installed = $false
    Assert-True (-not (Test-Path -LiteralPath $installRoot)) 'The setup uninstall left the install directory behind.'
    Assert-True (-not (Test-Path -LiteralPath $fragmentPath)) 'The setup uninstall left its generated Terminal fragment behind.'

    $evidence = [ordered]@{
        schema = 'termlink.phase1-windows-setup-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        passed = $true
        setup_version = '0.1.0.0'
        architecture = 'win-x64'
        single_file_setup = $true
        self_contained_payload = $true
        embedded_payload = $true
        selected_install_directory_applied = $true
        terminal_profile_count = $profiles.Count
        terminal_fragment_owned_cleanup = $true
        startup_unchanged_under_no_startup = $true
        upgrade_in_place = $true
        uninstall = $true
        setup_sha256 = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
    [IO.File]::WriteAllText(
        $resolvedEvidence,
        ($evidence | ConvertTo-Json -Depth 5) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Write-Output "Single-file Windows setup validation passed. Evidence: $resolvedEvidence"
}
finally {
    if ($installed -and (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        try { Invoke-Setup -SetupPath $setupPath -Arguments @('--uninstall', $installRoot, '--quiet') }
        catch { Write-Warning "Validation cleanup could not run setup uninstall: $($_.Exception.Message)" }
    }
    if ($fragmentExisted) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fragmentPath)) | Out-Null
        [IO.File]::WriteAllBytes($fragmentPath, $fragmentBytes)
    }
    elseif (Test-Path -LiteralPath $fragmentPath -PathType Leaf) {
        Remove-Item -LiteralPath $fragmentPath -Force
    }
    $currentRun = Get-ItemProperty -LiteralPath $runKeyPath -ErrorAction SilentlyContinue
    $currentRunValue = if ($null -ne $currentRun -and $null -ne $currentRun.PSObject.Properties['TermLink']) {
        [string]$currentRun.TermLink
    } else { $null }
    if ($runExisted -and -not [string]::Equals($currentRunValue, $runValue, [StringComparison]::Ordinal)) {
        New-ItemProperty -LiteralPath $runKeyPath -Name 'TermLink' -Value $runValue -PropertyType String -Force | Out-Null
    }
    elseif (-not $runExisted -and $null -ne $currentRunValue) {
        Remove-ItemProperty -LiteralPath $runKeyPath -Name 'TermLink' -Force
    }
    if ($null -ne $certificate) { $certificate.Dispose() }
    if ($null -ne $rsa) { $rsa.Dispose() }
    $resolvedWork = [IO.Path]::GetFullPath($workRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedWork).StartsWith('TermLink-SetupValidation-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
    }
}
