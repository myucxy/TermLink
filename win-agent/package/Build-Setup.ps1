[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0.0',
    [string]$CertificatePath,
    [Security.SecureString]$CertificatePassword,
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$CertificateThumbprint,
    [string]$TimestampUrl,
    [switch]$AllowHttpTimestamp,
    [switch]$Development,
    [ValidateSet('stable', 'preview')]
    [string]$UpdateChannel = 'stable',
    [string]$DotNetPath,
    [string]$CargoPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts\TermLink-Setup-$Version.exe")
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$updateApplicationSlug = if ($UpdateChannel -eq 'preview') { 'termlink-win64-preview' } else { 'termlink-win64' }
$appShipUpdateChannel = if ($UpdateChannel -eq 'preview') { 'beta' } else { 'stable' }

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
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
        [Parameter(Mandatory)][string]$Path
    )
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $SignToolPath verify /pa /all $Path 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -eq 0) { return }
    if ($Development -and $exitCode -eq 1) {
        $text = $output -join "`n"
        if ($text.Contains('terminated in a root') -and
            $text.Contains('not trusted by the trust provider') -and
            -not $text.Contains('hash of the file does not match') -and
            -not $text.Contains('No signature found')) {
            Write-Host "Development signature is cryptographically intact; its root is intentionally not system-trusted."
            return
        }
    }
    $output | ForEach-Object { Write-Host $_ }
    throw "Embedded signature verification failed for '$Path'."
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotNetPathWasExplicit = $PSBoundParameters.ContainsKey('DotNetPath')
. (Join-Path $projectRoot 'scripts\Resolve-DotNetSdk.ps1')
$candidate = Resolve-TermLinkDotNetSdk -ProjectRoot $projectRoot `
    -DotNetPath $DotNetPath -DotNetPathWasExplicit:$dotNetPathWasExplicit
$usesCertificateStore = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
$usesCertificateFile = -not [string]::IsNullOrWhiteSpace($CertificatePath) -or $null -ne $CertificatePassword
Assert-True ($usesCertificateStore -xor $usesCertificateFile) `
    'Specify exactly one signing source: CertificateThumbprint, or CertificatePath with CertificatePassword.'
if ($usesCertificateFile) {
    Assert-True (-not [string]::IsNullOrWhiteSpace($CertificatePath)) `
        'CertificatePath is required when signing from a PFX file.'
    Assert-True ($null -ne $CertificatePassword) `
        'CertificatePassword is required when signing from a PFX file.'
    Assert-True (Test-Path -LiteralPath $CertificatePath -PathType Leaf) `
        "Signing certificate not found: $CertificatePath"
}
if (-not $Development) {
    Assert-True (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) `
        'A trusted RFC 3161 TimestampUrl is required for non-development installers.'
}
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $timestampUri = $null
    $validTimestamp = [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri)
    if ($validTimestamp) {
        $validTimestamp = -not [string]::IsNullOrWhiteSpace($timestampUri.Host) -and
            [string]::IsNullOrEmpty($timestampUri.UserInfo) -and
            ($timestampUri.Scheme -eq 'https' -or
                ($timestampUri.Scheme -eq 'http' -and $AllowHttpTimestamp))
    }
    Assert-True $validTimestamp `
        'TimestampUrl must be absolute HTTPS; HTTP additionally requires -AllowHttpTimestamp.'
}

$plainPassword = $null
if ($usesCertificateStore) {
    $storePath = "Cert:\CurrentUser\My\$CertificateThumbprint"
    Assert-True (Test-Path -LiteralPath $storePath -PathType Leaf) `
        "Signing certificate was not found in CurrentUser/My: $CertificateThumbprint"
    $certificate = Get-Item -LiteralPath $storePath
}
else {
    $plainPassword = [Net.NetworkCredential]::new('', $CertificatePassword).Password
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        [IO.Path]::GetFullPath($CertificatePath),
        $plainPassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
}
try {
    Assert-True $certificate.HasPrivateKey 'The signing certificate does not contain a private key.'
    $now = [DateTime]::UtcNow
    Assert-True ($certificate.NotBefore.ToUniversalTime() -le $now -and
        $certificate.NotAfter.ToUniversalTime() -gt $now) 'The signing certificate is not currently valid.'
    $allowsCodeSigning = $false
    foreach ($extension in @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })) {
        foreach ($usage in ([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$extension).EnhancedKeyUsages) {
            if ($usage.Value -eq '1.3.6.1.5.5.7.3.3') { $allowsCodeSigning = $true }
        }
    }
    Assert-True $allowsCodeSigning 'The signing certificate is not valid for code signing.'

    $signTool = Find-WindowsSdkTool -Name 'signtool.exe'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Setup-' + [guid]::NewGuid().ToString('N'))
    $layout = Join-Path $workRoot 'payload'
    $legacyPublish = Join-Path $workRoot 'legacy-publish'
    $updaterPublish = Join-Path $workRoot 'updater-publish'
    $setupPublish = Join-Path $workRoot 'setup-publish'
    $dotnetArtifacts = Join-Path $workRoot 'dotnet-artifacts'
    $payloadZip = Join-Path $workRoot 'TermLink.payload.zip'
    [IO.Directory]::CreateDirectory($layout) | Out-Null
    try {
        $null = & (Join-Path $projectRoot 'scripts\Build-TerminalCoreNative.ps1') `
            -CargoPath $CargoPath `
            -OutputDirectory $layout
        $projects = @(
            (Join-Path $PSScriptRoot '..\src\Agent\TermLink.Agent.csproj'),
            (Join-Path $PSScriptRoot '..\src\SessionHost\TermLink.SessionHost.csproj'),
            (Join-Path $PSScriptRoot '..\..\pc-client\src\Desktop\TermLink.Desktop.csproj')
        )
        foreach ($project in $projects) {
            Invoke-Tool -FilePath $candidate -Arguments @(
                'publish', $project,
                '--configuration', 'Release',
                '--runtime', 'win-x64',
                '--framework', 'net8.0-windows',
                '--self-contained', 'true',
                '--output', $layout,
                '--artifacts-path', $dotnetArtifacts,
                '--nologo',
                '-p:RestoreLockedMode=true',
                '-p:PackageVersion=1.0.0',
                '-p:DebugSymbols=false',
                '-p:DebugType=None',
                '-p:PublishSingleFile=false',
                "-p:Version=$Version"
            )
        }
        # Keep the WinForms implementation as an isolated single-file rollback
        # and update surface for one Preview cycle. Its dependency graph must not
        # overwrite Avalonia assemblies in the primary payload.
        Invoke-Tool -FilePath $candidate -Arguments @(
            'publish', (Join-Path $PSScriptRoot '..\src\Tray\TermLink.Tray.csproj'),
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--output', $legacyPublish,
            '--artifacts-path', $dotnetArtifacts,
            '--nologo',
            '-p:RestoreLockedMode=true',
            '-p:PackageVersion=1.0.0',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true',
            "-p:TermLinkUpdateApplicationSlug=$updateApplicationSlug",
            "-p:TermLinkUpdateChannel=$appShipUpdateChannel",
            "-p:Version=$Version"
        )
        Copy-Item -LiteralPath (Join-Path $legacyPublish 'TermLink.exe') `
            -Destination (Join-Path $layout 'TermLink.Legacy.exe')
        Invoke-Tool -FilePath $candidate -Arguments @(
            'publish', (Join-Path $PSScriptRoot '..\src\Updater\TermLink.Updater.csproj'),
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--output', $updaterPublish,
            '--artifacts-path', $dotnetArtifacts,
            '--nologo',
            '-p:RestoreLockedMode=true',
            '-p:PackageVersion=1.0.0',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true',
            "-p:Version=$Version"
        )
        Copy-Item -LiteralPath (Join-Path $updaterPublish 'TermLink.Updater.exe') `
            -Destination (Join-Path $layout 'TermLink.Updater.exe')

        $expectedProductExecutables = @(
            'TermLink.Agent.exe',
            'TermLink.exe',
            'TermLink.Legacy.exe',
            'TermLink.SessionHost.exe',
            'TermLink.Updater.exe'
        )
        foreach ($name in $expectedProductExecutables) {
            Assert-True (Test-Path -LiteralPath (Join-Path $layout $name) -PathType Leaf) `
                "Published executable is missing: $name"
        }
        $publishedProductExecutables = @(Get-ChildItem -LiteralPath $layout -File -Filter 'TermLink*.exe' |
            Select-Object -ExpandProperty Name)
        Assert-True ($publishedProductExecutables.Count -eq $expectedProductExecutables.Count -and
            @($publishedProductExecutables | Where-Object { $expectedProductExecutables -cnotcontains $_ }).Count -eq 0) `
            "The setup contains an unexpected product executable: $($publishedProductExecutables -join ', ')"
        Assert-True (Test-Path -LiteralPath (Join-Path $layout 'TermLink.Agent.dll') -PathType Leaf) `
            'The setup is missing the independent Agent runtime assembly.'
        Assert-True (Test-Path -LiteralPath (Join-Path $layout 'TermLink.TerminalCore.dll') -PathType Leaf) `
            'The setup is missing the Windows terminal-core managed runtime.'
        Assert-True (Test-Path -LiteralPath (Join-Path $layout 'termlink_terminal_ffi.dll') -PathType Leaf) `
            'The setup is missing the Windows terminal-core native runtime.'
        Assert-True (Test-Path -LiteralPath (Join-Path $layout 'coreclr.dll') -PathType Leaf) `
            'The setup payload is not self-contained.'
        [IO.File]::WriteAllBytes(
            (Join-Path $layout 'TermLink.UpdatePublisher.cer'),
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))

        $signArguments = if ($usesCertificateStore) {
            @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, '/s', 'My')
        }
        else {
            @('sign', '/fd', 'SHA256', '/f', [IO.Path]::GetFullPath($CertificatePath), '/p', $plainPassword)
        }
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
        }
        $ownedBinaries = @(Get-ChildItem -LiteralPath $layout -File |
            Where-Object {
                $_.Name -match '^TermLink(?:\..+)?\.(exe|dll)$' -or
                $_.Name -ceq 'termlink_terminal_ffi.dll'
            } | Sort-Object Name)
        Assert-True ($ownedBinaries.Count -ge 4) 'No TermLink binaries were found in the setup payload.'
        foreach ($binary in $ownedBinaries) {
            Invoke-Tool -FilePath $signTool -Arguments ($signArguments + @($binary.FullName))
            Confirm-EmbeddedSignature -SignToolPath $signTool -Path $binary.FullName
        }

        [IO.Compression.ZipFile]::CreateFromDirectory($layout, $payloadZip, [IO.Compression.CompressionLevel]::Optimal, $false)
        Invoke-Tool -FilePath $candidate -Arguments @(
            'publish', (Join-Path $PSScriptRoot '..\src\Setup\TermLink.Setup.csproj'),
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--output', $setupPublish,
            '--artifacts-path', $dotnetArtifacts,
            '--nologo',
            '-p:RestoreLockedMode=true',
            '-p:PackageVersion=1.0.0',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true',
            "-p:Version=$Version",
            "-p:PayloadPath=$payloadZip"
        )
        $setupExecutable = Join-Path $setupPublish 'TermLink.Setup.exe'
        Assert-True (Test-Path -LiteralPath $setupExecutable -PathType Leaf) 'The setup executable was not published.'
        Invoke-Tool -FilePath $signTool -Arguments ($signArguments + @($setupExecutable))
        Confirm-EmbeddedSignature -SignToolPath $signTool -Path $setupExecutable

        $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
        Move-Item -LiteralPath $setupExecutable -Destination $resolvedOutput -Force
        $global:LASTEXITCODE = 0
        [pscustomobject]@{
            SetupPath = $resolvedOutput
            Version = $Version
            Architecture = 'win-x64'
            SelfContained = $true
            EmbeddedPayload = $true
            SignedPayloadBinaries = $ownedBinaries.Count
            Sha256 = Get-Sha256Hex -Path $resolvedOutput
        }
    }
    finally {
        $resolvedWork = [IO.Path]::GetFullPath($workRoot)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedWork).StartsWith('TermLink-Setup-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    $certificate.Dispose()
    $plainPassword = $null
}
