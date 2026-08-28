#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$DotNetPath = (Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'),
    [string]$CargoPath = 'D:\Dev\Env\Rust\cargo\bin\cargo.exe',
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase1-windows-setup.json'),
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.2.0',
    [ValidateSet('stable', 'preview')]
    [string]$UpdateChannel = 'stable',
    [string]$OutputSetupPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

Assert-True (Test-Path -LiteralPath $CargoPath -PathType Leaf) "Cargo executable not found: $CargoPath"

function Invoke-Setup {
    param(
        [Parameter(Mandatory)][string]$SetupPath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$ExpectedExitCode = 0,
        [string]$WorkingDirectory
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $SetupPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $start.WorkingDirectory = $WorkingDirectory
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Assert-True ($null -ne $process) 'The setup process did not start.'
    try {
        Assert-True $process.WaitForExit(120000) 'The setup process timed out.'
        Assert-True ($process.ExitCode -eq $ExpectedExitCode) `
            "The setup process returned exit code $($process.ExitCode), expected $ExpectedExitCode."
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
$installStateKeyPath = 'HKCU:\Software\TermLink'
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)) 'Programs\TermLink'
$desktopShortcutsBefore = @(Get-ChildItem -LiteralPath $desktopDirectory -Filter 'TermLink*.lnk' -File -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty FullName)
$startMenuShortcutsBefore = @(if (Test-Path -LiteralPath $startMenuDirectory) {
    Get-ChildItem -LiteralPath $startMenuDirectory -Filter 'TermLink*.lnk' -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
})
$fragmentExisted = Test-Path -LiteralPath $fragmentPath -PathType Leaf
$fragmentBytes = if ($fragmentExisted) { [IO.File]::ReadAllBytes($fragmentPath) } else { $null }
$runKey = Get-ItemProperty -LiteralPath $runKeyPath -ErrorAction SilentlyContinue
$runExisted = $null -ne $runKey -and $null -ne $runKey.PSObject.Properties['TermLink']
$runValue = if ($runExisted) { [string]$runKey.TermLink } else { $null }
$agentRunExisted = $null -ne $runKey -and $null -ne $runKey.PSObject.Properties['TermLink.Agent']
$agentRunValue = if ($agentRunExisted) { [string]$runKey.'TermLink.Agent' } else { $null }
$installState = Get-ItemProperty -LiteralPath $installStateKeyPath -ErrorAction SilentlyContinue
$installRootValueExisted = $null -ne $installState -and
    $null -ne $installState.PSObject.Properties['InstallRoot']
$installRootValueBefore = if ($installRootValueExisted) { [string]$installState.InstallRoot } else { $null }
$installed = $false
$relocationProbe = $null
$rsa = $null
$certificate = $null
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

try {
    # Isolate the validation from an existing user-owned TermLink fragment.
    # The exact original bytes are restored in finally even when validation
    # fails, while the test installation gets a clean ownership boundary.
    if ($fragmentExisted) {
        Remove-Item -LiteralPath $fragmentPath -Force
    }
    # The real setup validation intentionally installs into a temporary root.
    # Hide only the saved current-user root while the test runs so the
    # uninstall-first relocation guard does not mistake this isolated lifecycle
    # for a user-requested move. The exact value is restored in finally.
    if ($installRootValueExisted) {
        Remove-ItemProperty -LiteralPath $installStateKeyPath -Name 'InstallRoot' -Force
    }

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
        -Version $Version `
        -CertificatePath $pfxPath `
        -CertificatePassword $password `
        -Development `
        -UpdateChannel $UpdateChannel `
        -DotNetPath $DotNetPath `
        -CargoPath $CargoPath `
        -OutputPath $setupPath)
    # Build-Setup emits tool progress for interactive callers. Select the final
    # summary object instead of relying on pipeline cardinality.
    $result = @($buildOutput | Where-Object {
        $_ -is [psobject] -and $_.PSObject.Properties['SetupPath'] -and
        $_.PSObject.Properties['SelfContained'] -and $_.PSObject.Properties['EmbeddedPayload'] -and
        $_.PSObject.Properties['SignedPayloadBinaries']
    } | Select-Object -Last 1)
    Assert-True (Test-Path -LiteralPath $setupPath -PathType Leaf) 'The single-file setup executable was not created.'
    Assert-True ($result.Count -eq 1 -and [bool]$result[0].SelfContained -and [bool]$result[0].EmbeddedPayload) `
        'The setup build did not report a self-contained embedded payload.'

    Invoke-Setup -SetupPath $setupPath -Arguments @(
        '--install-dir', $installRoot, '--no-startup', '--no-launch', '--quiet')
    $installed = $true
    foreach ($name in @(
        'TermLink.Agent.exe',
        'TermLink.SessionHost.exe',
        'TermLink.exe',
        'TermLink.Legacy.exe',
        'TermLink.Updater.exe',
        'TermLink.Agent.dll',
        'TermLink.TerminalCore.dll',
        'termlink_terminal_ffi.dll',
        'TermLink.UpdatePublisher.cer',
        'TermLink.Uninstall.exe',
        'coreclr.dll',
        '.termlink-install.json'
    )) {
        Assert-True (Test-Path -LiteralPath (Join-Path $installRoot $name) -PathType Leaf) `
            "Installed setup payload is missing: $name"
    }
    $expectedProductExecutables = @(
        'TermLink.Agent.exe',
        'TermLink.exe',
        'TermLink.Legacy.exe',
        'TermLink.SessionHost.exe',
        'TermLink.Updater.exe'
    )
    $installedProductExecutables = @(Get-ChildItem -LiteralPath $installRoot -File -Filter 'TermLink*.exe' |
        Where-Object { $_.Name -cne 'TermLink.Uninstall.exe' } |
        Select-Object -ExpandProperty Name)
    Assert-True ($installedProductExecutables.Count -eq $expectedProductExecutables.Count -and
        @($installedProductExecutables | Where-Object { $expectedProductExecutables -cnotcontains $_ }).Count -eq 0) `
        "Installed setup contains an unexpected product executable: $($installedProductExecutables -join ', ')"
    $signedPayloadBinaries = @(Get-ChildItem -LiteralPath $installRoot -File |
        Where-Object {
            ($_.Name -match '^TermLink(?:\..+)?\.(exe|dll)$' -or
                $_.Name -ceq 'termlink_terminal_ffi.dll') -and
            $_.Name -cne 'TermLink.Uninstall.exe'
        } |
        Sort-Object Name)
    Assert-True ($signedPayloadBinaries.Count -eq [int]$result[0].SignedPayloadBinaries) `
        'Installed signed payload binary count differs from the setup build.'
    $acceptedSignatureStatuses = @(
        [Management.Automation.SignatureStatus]::Valid,
        [Management.Automation.SignatureStatus]::UnknownError,
        [Management.Automation.SignatureStatus]::NotTrusted
    )
    foreach ($binary in $signedPayloadBinaries) {
        $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
        Assert-True ($null -ne $signature.SignerCertificate -and
            [string]::Equals($signature.SignerCertificate.Thumbprint, $certificate.Thumbprint,
                [StringComparison]::OrdinalIgnoreCase) -and
            $acceptedSignatureStatuses -contains $signature.Status) `
            "Installed payload binary signature is invalid: $($binary.Name) ($($signature.Status))"
    }
    $trayAssemblyPath = Join-Path $installRoot 'TermLink.dll'
    Assert-True (Test-Path -LiteralPath $trayAssemblyPath -PathType Leaf) `
        'Installed setup payload is missing: TermLink.dll'
    $trayAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($trayAssemblyPath).Version.ToString()
    Assert-True ([string]::Equals($trayAssemblyVersion, $Version, [StringComparison]::Ordinal)) `
        "Installed Tray assembly version is '$trayAssemblyVersion', expected '$Version'."
    $updateConfigPath = Join-Path $workRoot 'update-config.json'
    Invoke-Setup -SetupPath (Join-Path $installRoot 'TermLink.Legacy.exe') -Arguments @(
        '--update-config-json', $updateConfigPath)
    $updateConfig = Get-Content -LiteralPath $updateConfigPath -Raw | ConvertFrom-Json
    $expectedUpdateSlug = if ($UpdateChannel -eq 'preview') { 'termlink-win64-preview' } else { 'termlink-win64' }
    $expectedAppShipChannel = if ($UpdateChannel -eq 'preview') { 'beta' } else { 'stable' }
    Assert-True ([string]$updateConfig.application_slug -ceq $expectedUpdateSlug -and
        [string]$updateConfig.channel -ceq $expectedAppShipChannel) `
        'The installed rollback/update surface is not bound to the requested AppShip application and channel.'

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
    $desktopShortcuts = @(Get-ChildItem -LiteralPath $desktopDirectory -Filter 'TermLink*.lnk' -File |
        Where-Object { $_.FullName -notin $desktopShortcutsBefore })
    $startMenuShortcuts = @(Get-ChildItem -LiteralPath $startMenuDirectory -Filter 'TermLink*.lnk' -File |
        Where-Object { $_.FullName -notin $startMenuShortcutsBefore })
    Assert-True ($desktopShortcuts.Count -eq 1) 'The setup did not create exactly one desktop shortcut.'
    Assert-True ($startMenuShortcuts.Count -eq 1) 'The setup did not create exactly one Start menu shortcut.'
    $shortcutShell = New-Object -ComObject WScript.Shell
    foreach ($shortcut in @($desktopShortcuts[0], $startMenuShortcuts[0])) {
        $shortcutTarget = $shortcutShell.CreateShortcut($shortcut.FullName).TargetPath
        Assert-True ([string]::Equals($shortcutTarget, (Join-Path $installRoot 'TermLink.exe'),
                [StringComparison]::OrdinalIgnoreCase)) `
            "Shortcut target is incorrect: $($shortcut.FullName)"
    }
    $afterRun = Get-ItemProperty -LiteralPath $runKeyPath -ErrorAction SilentlyContinue
    $afterRunValue = if ($null -ne $afterRun -and $null -ne $afterRun.PSObject.Properties['TermLink']) {
        [string]$afterRun.TermLink
    } else { $null }
    Assert-True ([string]::Equals($afterRunValue, $runValue, [StringComparison]::Ordinal)) `
        '--no-startup changed the current-user startup registration.'
    $afterAgentRunValue = if ($null -ne $afterRun -and
        $null -ne $afterRun.PSObject.Properties['TermLink.Agent']) {
        [string]$afterRun.'TermLink.Agent'
    } else { $null }
    Assert-True ([string]::Equals($afterAgentRunValue, $agentRunValue, [StringComparison]::Ordinal)) `
        '--no-startup changed the independent Agent startup registration.'
    $afterInstallState = Get-ItemProperty -LiteralPath $installStateKeyPath -ErrorAction SilentlyContinue
    $afterInstallRoot = if ($null -ne $afterInstallState -and
        $null -ne $afterInstallState.PSObject.Properties['InstallRoot']) {
        [string]$afterInstallState.InstallRoot
    } else { $null }
    Assert-True ([string]::Equals($afterInstallRoot, $installRoot, [StringComparison]::OrdinalIgnoreCase)) `
        'The setup did not remember the selected installation directory.'

    # Exercise the real executable's relocation guard while a process from A is
    # running. B must not be created, the old process must not be stopped, and
    # the old installed bytes must remain unchanged. --no-launch keeps this
    # lifecycle independent from any user-owned Tray already running.
    $relocationRoot = Join-Path $workRoot 'Relocated TermLink'
    $oldMarkerPath = Join-Path $installRoot '.termlink-install.json'
    $oldTrayAssemblyPath = Join-Path $installRoot 'TermLink.dll'
    $oldMarkerHash = (Get-FileHash -LiteralPath $oldMarkerPath -Algorithm SHA256).Hash
    $oldTrayAssemblyHash = (Get-FileHash -LiteralPath $oldTrayAssemblyPath -Algorithm SHA256).Hash
    $relocationProbePath = Join-Path $installRoot 'TermLink.RelocationProbe.exe'
    Copy-Item -LiteralPath (Join-Path ([Environment]::SystemDirectory) 'ping.exe') `
        -Destination $relocationProbePath
    $relocationProbe = Start-Process -FilePath $relocationProbePath `
        -ArgumentList @('127.0.0.1', '-t') -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 250
    Assert-True (-not $relocationProbe.HasExited) `
        'The relocation running-process probe did not remain active.'
    Invoke-Setup -SetupPath $setupPath -Arguments @(
        '--install-dir', $relocationRoot, '--no-startup', '--no-launch', '--quiet') `
        -ExpectedExitCode 1 -WorkingDirectory $installRoot
    Assert-True (-not $relocationProbe.HasExited) `
        'The rejected relocation stopped a process from the existing installation.'
    Assert-True (-not (Test-Path -LiteralPath $relocationRoot)) `
        'The rejected relocation created the new installation directory.'
    Assert-True (
        (Get-FileHash -LiteralPath $oldMarkerPath -Algorithm SHA256).Hash -ceq $oldMarkerHash -and
        (Get-FileHash -LiteralPath $oldTrayAssemblyPath -Algorithm SHA256).Hash -ceq $oldTrayAssemblyHash) `
        'The rejected relocation changed the existing installation.'
    $relocationProbe.Kill($true)
    Assert-True $relocationProbe.WaitForExit(5000) `
        'The validation relocation probe did not stop during test cleanup.'
    $relocationProbe.Dispose()
    $relocationProbe = $null
    Remove-Item -LiteralPath $relocationProbePath -Force

    # Force the real upgrade path to encounter an obsolete TermLink-owned
    # Fragment. This must be replaced as a whole even though the existing
    # install marker no longer owns the stale bytes.
    $staleHostPath = 'C:\Obsolete TermLink\TermLink.SessionHost.exe'
    $staleExtraProfileGuid = '{8f3dff3b-9401-52e8-999a-49066cfefea0}'
    $staleFragment = [ordered]@{
        stale_top_level = 'must be removed by the upgrade'
        profiles = @(
            [ordered]@{
                guid = $mandatoryGuids[0]
                name = 'Stale TermLink CMD'
                commandline = '"' + $staleHostPath + '" --stale'
                startingDirectory = 'C:\Obsolete TermLink'
                hidden = $true
                stale_profile_field = 'must be removed by the upgrade'
            },
            [ordered]@{
                guid = $staleExtraProfileGuid
                name = 'Stale Extra Profile'
                commandline = 'cmd.exe /k echo stale'
                startingDirectory = '%USERPROFILE%'
                hidden = $false
            }
        )
    }
    [IO.File]::WriteAllText(
        $fragmentPath,
        ($staleFragment | ConvertTo-Json -Depth 6) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    Invoke-Setup -SetupPath $setupPath -Arguments @(
        '--install-dir', $installRoot, '--no-startup', '--no-launch', '--terminate-running', '--quiet') `
        -WorkingDirectory $installRoot

    $upgradedFragmentText = [IO.File]::ReadAllText($fragmentPath, [Text.Encoding]::UTF8)
    Assert-True (-not $upgradedFragmentText.Contains($staleHostPath, [StringComparison]::OrdinalIgnoreCase)) `
        'The upgrade retained the obsolete SessionHost path.'
    Assert-True (-not $upgradedFragmentText.Contains('stale_top_level', [StringComparison]::Ordinal) -and
        -not $upgradedFragmentText.Contains('stale_profile_field', [StringComparison]::Ordinal) -and
        -not $upgradedFragmentText.Contains($staleExtraProfileGuid, [StringComparison]::OrdinalIgnoreCase)) `
        'The upgrade merged with the stale Fragment instead of replacing it completely.'
    $upgradedFragment = $upgradedFragmentText | ConvertFrom-Json
    $upgradedTopLevelProperties = @($upgradedFragment.PSObject.Properties.Name)
    Assert-True ($upgradedTopLevelProperties.Count -eq 1 -and
        $upgradedTopLevelProperties[0] -ceq 'profiles') `
        "The upgraded Fragment contains unexpected top-level fields: $($upgradedTopLevelProperties -join ', ')"
    $upgradedProfiles = @($upgradedFragment.profiles)
    Assert-True ($upgradedProfiles.Count -eq $profiles.Count) `
        'The upgrade retained an extra stale Terminal profile or removed a generated profile.'
    $allowedProfileProperties = @(
        'guid',
        'name',
        'commandline',
        'startingDirectory',
        'closeOnExit',
        'hidden')
    foreach ($profile in $upgradedProfiles) {
        $unexpectedProperties = @($profile.PSObject.Properties.Name |
            Where-Object { $_ -cnotin $allowedProfileProperties })
        Assert-True ($unexpectedProperties.Count -eq 0) `
            "The upgraded profile contains stale fields: $($unexpectedProperties -join ', ')"
        Assert-True ($profile.commandline.StartsWith(
                $expectedHostPrefix, [StringComparison]::OrdinalIgnoreCase)) `
            "Upgraded Terminal profile does not use the current SessionHost: $($profile.guid)"
        Assert-True ([string]$profile.closeOnExit -ceq 'always') `
            "Upgraded Terminal profile no longer closes its tab after a remote session stop: $($profile.guid)"
    }
    $upgradedMarker = Get-Content -Raw -LiteralPath (Join-Path $installRoot '.termlink-install.json') |
        ConvertFrom-Json
    $upgradedFragmentHash = (Get-FileHash -LiteralPath $fragmentPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True ([string]::Equals(
            [string]$upgradedMarker.TerminalFragmentPath,
            [IO.Path]::GetFullPath($fragmentPath),
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals(
            [string]$upgradedMarker.TerminalFragmentSha256,
            $upgradedFragmentHash,
            [StringComparison]::OrdinalIgnoreCase)) `
        'The upgrade marker does not own the completely replaced Terminal Fragment.'
    # Exercise the legacy-upgrade path and the installed uninstaller: remove
    # the new registry hint, then let the in-place uninstaller delegate through
    # a temporary copy so it can delete its own installation directory safely.
    Remove-ItemProperty -LiteralPath $installStateKeyPath -Name 'InstallRoot' -Force -ErrorAction SilentlyContinue
    $uninstallerPath = Join-Path $installRoot 'TermLink.Uninstall.exe'
    $uninstallProcess = Start-Process -FilePath $uninstallerPath -PassThru -WindowStyle Hidden `
        -WorkingDirectory $installRoot
    Assert-True $uninstallProcess.WaitForExit(30000) 'The installed uninstaller did not return.'
    $uninstallProcess.Dispose()
    $uninstallDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while ((Test-Path -LiteralPath $installRoot) -and [DateTime]::UtcNow -lt $uninstallDeadline) {
        Start-Sleep -Milliseconds 250
    }
    $installed = $false
    Assert-True (-not (Test-Path -LiteralPath $installRoot)) 'The installed uninstaller left the install directory behind.'
    Assert-True (-not (Test-Path -LiteralPath $fragmentPath)) 'The setup uninstall left its generated Terminal fragment behind.'
    foreach ($shortcut in @($desktopShortcuts[0], $startMenuShortcuts[0])) {
        Assert-True (-not (Test-Path -LiteralPath $shortcut.FullName)) `
            "The setup uninstall left its generated shortcut behind: $($shortcut.FullName)"
    }

    $evidence = [ordered]@{
        schema = 'termlink.phase1-windows-setup-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        passed = $true
        setup_version = $Version
        architecture = 'win-x64'
        single_file_setup = $true
        self_contained_payload = $true
        embedded_payload = $true
        selected_install_directory_applied = $true
        tray_assembly_version = $trayAssemblyVersion
        release_track = $UpdateChannel
        update_channel = $expectedAppShipChannel
        update_application_slug = $expectedUpdateSlug
        terminal_profile_count = $profiles.Count
        terminal_fragment_stale_upgrade_overwrite = $true
        terminal_fragment_owned_cleanup = $true
        startup_unchanged_under_no_startup = $true
        upgrade_in_place = $true
        inherited_install_working_directory_detached = $true
        cross_directory_upgrade_rejected = $true
        relocation_rejection_preserved_running_install = $true
        previous_install_directory_auto_discovered = $true
        installed_uninstaller = $true
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
    if ($null -ne $relocationProbe) {
        try {
            if (-not $relocationProbe.HasExited) {
                $relocationProbe.Kill($true)
                [void]$relocationProbe.WaitForExit(5000)
            }
        }
        catch { }
        finally { $relocationProbe.Dispose() }
    }
    if ($installed -and (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        try { Invoke-Setup -SetupPath $setupPath -Arguments @('--uninstall', $installRoot, '--quiet') }
        catch { Write-Warning "Validation cleanup could not run setup uninstall: $($_.Exception.Message)" }
    }
    if ($fragmentExisted) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fragmentPath)) | Out-Null
        [IO.File]::WriteAllBytes($fragmentPath, $fragmentBytes)
        $restoredFragmentBytes = [IO.File]::ReadAllBytes($fragmentPath)
        Assert-True (
            $restoredFragmentBytes.Length -eq $fragmentBytes.Length -and
            [Convert]::ToBase64String($restoredFragmentBytes) -ceq [Convert]::ToBase64String($fragmentBytes)) `
            'Validation cleanup did not restore the original user Terminal Fragment byte-for-byte.'
    }
    else {
        if (Test-Path -LiteralPath $fragmentPath -PathType Leaf) {
            Remove-Item -LiteralPath $fragmentPath -Force
        }
        Assert-True (-not (Test-Path -LiteralPath $fragmentPath -PathType Leaf)) `
            'Validation cleanup left its generated Terminal Fragment behind.'
    }
    $shortcutShell = New-Object -ComObject WScript.Shell
    foreach ($directory in @($desktopDirectory, $startMenuDirectory)) {
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        foreach ($shortcut in @(Get-ChildItem -LiteralPath $directory -Filter 'TermLink*.lnk' -File -ErrorAction SilentlyContinue)) {
            try {
                $target = $shortcutShell.CreateShortcut($shortcut.FullName).TargetPath
                if ([string]$target -like ((Join-Path $installRoot '*'))) {
                    Remove-Item -LiteralPath $shortcut.FullName -Force -ErrorAction SilentlyContinue
                }
            } catch { }
        }
    }
    if ((Test-Path -LiteralPath $startMenuDirectory -PathType Container) -and
        -not (Get-ChildItem -LiteralPath $startMenuDirectory -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $startMenuDirectory -Force -ErrorAction SilentlyContinue
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
    $currentAgentRunValue = if ($null -ne $currentRun -and
        $null -ne $currentRun.PSObject.Properties['TermLink.Agent']) {
        [string]$currentRun.'TermLink.Agent'
    } else { $null }
    if ($agentRunExisted -and -not [string]::Equals(
            $currentAgentRunValue,
            $agentRunValue,
            [StringComparison]::Ordinal)) {
        New-ItemProperty -LiteralPath $runKeyPath -Name 'TermLink.Agent' -Value $agentRunValue `
            -PropertyType String -Force | Out-Null
    }
    elseif (-not $agentRunExisted -and $null -ne $currentAgentRunValue) {
        Remove-ItemProperty -LiteralPath $runKeyPath -Name 'TermLink.Agent' -Force
    }
    $currentInstallState = Get-ItemProperty -LiteralPath $installStateKeyPath -ErrorAction SilentlyContinue
    $currentInstallRoot = if ($null -ne $currentInstallState -and
        $null -ne $currentInstallState.PSObject.Properties['InstallRoot']) {
        [string]$currentInstallState.InstallRoot
    } else { $null }
    if ($installRootValueExisted) {
        if (-not [string]::Equals($currentInstallRoot, $installRootValueBefore,
                [StringComparison]::OrdinalIgnoreCase)) {
            New-Item -Path $installStateKeyPath -Force | Out-Null
            New-ItemProperty -LiteralPath $installStateKeyPath -Name 'InstallRoot' -Value $installRootValueBefore -PropertyType String -Force | Out-Null
        }
    }
    elseif ($null -ne $currentInstallRoot) {
        Remove-ItemProperty -LiteralPath $installStateKeyPath -Name 'InstallRoot' -Force -ErrorAction SilentlyContinue
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
