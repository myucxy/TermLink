[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateRange(1, 65535)]
    [int]$BuildNumber,

    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Condition {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Resolve-CommandPath {
    param([Parameter(Mandatory)][string]$Name)
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    Assert-Condition ($null -ne $command) "$Name is not installed on the hosted runner."
    return [IO.Path]::GetFullPath($command.Source)
}

function Resolve-AndroidSdk {
    $candidate = [Environment]::GetEnvironmentVariable('ANDROID_SDK_ROOT', 'Process')
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [Environment]::GetEnvironmentVariable('ANDROID_HOME', 'Process')
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = 'C:\Android\android-sdk' }
    $resolved = [IO.Path]::GetFullPath($candidate)
    Assert-Condition (Test-Path -LiteralPath $resolved -PathType Container) `
        "Android SDK was not found: $resolved"
    return $resolved
}

function Find-SdkManager {
    param([Parameter(Mandatory)][string]$SdkRoot)
    $manager = @(Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'cmdline-tools') `
        -Filter 'sdkmanager.bat' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending)
    Assert-Condition ($manager.Count -gt 0) "Android sdkmanager.bat was not found under $SdkRoot."
    return $manager[0].FullName
}

function Get-CMakeAndNinja {
    param([Parameter(Mandatory)][string]$SdkRoot)
    $configuredCMake = [Environment]::GetEnvironmentVariable('TERMLINK_CMAKE', 'Process')
    $configuredNinja = [Environment]::GetEnvironmentVariable('TERMLINK_NINJA', 'Process')
    if (-not [string]::IsNullOrWhiteSpace($configuredCMake) -and
        -not [string]::IsNullOrWhiteSpace($configuredNinja) -and
        (Test-Path -LiteralPath $configuredCMake -PathType Leaf) -and
        (Test-Path -LiteralPath $configuredNinja -PathType Leaf)) {
        return [pscustomobject]@{ CMake = [IO.Path]::GetFullPath($configuredCMake); Ninja = [IO.Path]::GetFullPath($configuredNinja) }
    }
    $tools = @(Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'cmake') -Directory `
        -ErrorAction SilentlyContinue | ForEach-Object {
            $cmake = Join-Path $_.FullName 'bin\cmake.exe'
            $ninja = Join-Path $_.FullName 'bin\ninja.exe'
            if ((Test-Path -LiteralPath $cmake -PathType Leaf) -and
                (Test-Path -LiteralPath $ninja -PathType Leaf)) {
                [pscustomobject]@{ CMake = $cmake; Ninja = $ninja }
            }
        } | Sort-Object CMake -Descending)
    if ($tools.Count -gt 0) { return $tools[0] }
    $cmake = Get-Command cmake.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $ninja = Get-Command ninja.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    Assert-Condition ($null -ne $cmake -and $null -ne $ninja) `
        "Android CMake/Ninja were not found under $SdkRoot or on PATH."
    return [pscustomobject]@{ CMake = $cmake.Source; Ninja = $ninja.Source }
}

function Ensure-AndroidPackages {
    param([Parameter(Mandatory)][string]$SdkRoot)
    $needs = [Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath (Join-Path $SdkRoot 'platforms\android-36\android.jar') -PathType Leaf)) {
        $needs.Add('platforms;android-36')
    }
    $buildTools = @(Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'build-tools') -Directory `
        -ErrorAction SilentlyContinue | Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'aapt2.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'apksigner.bat') -PathType Leaf)
        })
    if ($buildTools.Count -eq 0) { $needs.Add('build-tools;35.0.0') }
    if (-not (Test-Path -LiteralPath (Join-Path $SdkRoot 'ndk\27.0.12077973') -PathType Container)) {
        $needs.Add('ndk;27.0.12077973')
    }
    $configuredPair = [Environment]::GetEnvironmentVariable('TERMLINK_CMAKE', 'Process'),
        [Environment]::GetEnvironmentVariable('TERMLINK_NINJA', 'Process')
    $configuredTools = -not [string]::IsNullOrWhiteSpace([string]$configuredPair[0]) -and
        -not [string]::IsNullOrWhiteSpace([string]$configuredPair[1]) -and
        (Test-Path -LiteralPath $configuredPair[0] -PathType Leaf) -and
        (Test-Path -LiteralPath $configuredPair[1] -PathType Leaf)
    $cmakePair = @(Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'cmake') -Directory `
        -ErrorAction SilentlyContinue | Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\cmake.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\ninja.exe') -PathType Leaf)
        })
    $cmakeOnPath = Get-Command cmake.exe -CommandType Application -ErrorAction SilentlyContinue
    $ninjaOnPath = Get-Command ninja.exe -CommandType Application -ErrorAction SilentlyContinue
    if (-not $configuredTools -and $cmakePair.Count -eq 0 -and ($null -eq $cmakeOnPath -or $null -eq $ninjaOnPath)) {
        $needs.Add('cmake;3.22.1')
    }
    if ($needs.Count -eq 0) { return }
    $sdkmanager = Find-SdkManager -SdkRoot $SdkRoot
    Write-Host "Installing missing Android SDK packages: $($needs -join ', ')"
    $licenseInput = 1..100 | ForEach-Object { 'y' }
    $licenseInput | & $sdkmanager "--sdk_root=$SdkRoot" @needs
    Assert-Condition ($LASTEXITCODE -eq 0) 'Android SDK package installation failed.'
}

function Get-AndroidBuildTools {
    param([Parameter(Mandatory)][string]$SdkRoot)
    $items = @(Get-ChildItem -LiteralPath (Join-Path $SdkRoot 'build-tools') -Directory `
        -ErrorAction SilentlyContinue | Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'aapt2.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'apksigner.bat') -PathType Leaf)
        } | Sort-Object Name -Descending)
    Assert-Condition ($items.Count -gt 0) 'Android build-tools with aapt2 and apksigner are required.'
    return $items[0].FullName
}

function Resolve-RustToolchain {
    $rustup = Resolve-CommandPath -Name 'rustup'
    $toolchain = '1.97.1-x86_64-pc-windows-msvc'
    $installed = @(& $rustup toolchain list | Where-Object {
        ([string]$_).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0] -eq $toolchain
    })
    if ($installed.Count -eq 0) {
        Write-Host "Installing Rust toolchain $toolchain."
        & $rustup toolchain install $toolchain --profile minimal --no-self-update | Out-Host
        Assert-Condition ($LASTEXITCODE -eq 0) "Rust toolchain installation failed: $toolchain"
    }
    $targets = @('aarch64-linux-android', 'armv7-linux-androideabi',
        'i686-linux-android', 'x86_64-linux-android')
    $missing = @($targets | Where-Object {
        $name = $_
        -not @(& $rustup target list --toolchain $toolchain |
            Where-Object { ([string]$_) -match ('^' + [regex]::Escape($name) + '\s+\(installed\)$') })
    })
    if ($missing.Count -gt 0) {
        & $rustup target add --toolchain $toolchain @missing | Out-Host
        Assert-Condition ($LASTEXITCODE -eq 0) "Rust Android target installation failed: $($missing -join ', ')"
    }
    $cargo = (& $rustup which cargo --toolchain $toolchain).Trim()
    Assert-Condition (Test-Path -LiteralPath $cargo -PathType Leaf) `
        "Pinned Rust cargo was not resolved: $cargo"
    return [pscustomobject]@{ Cargo = $cargo }
}

function Get-RequiredSecret {
    param([Parameter(Mandatory)][string]$Name)
    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($value)) "GitHub secret '$Name' is missing."
    return $value
}

function Write-Base64File {
    param([Parameter(Mandatory)][string]$Base64, [Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Name)
    try { $bytes = [Convert]::FromBase64String($Base64) }
    catch { throw "GitHub secret '$Name' is not valid base64." }
    [IO.File]::WriteAllBytes($Path, $bytes)
    $bytes = $null
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkRoot = Resolve-AndroidSdk
$dotnetCandidate = [Environment]::GetEnvironmentVariable('TERMLINK_DOTNET_PATH', 'Process')
if ([string]::IsNullOrWhiteSpace($dotnetCandidate)) {
    $dotnet = Resolve-CommandPath -Name 'dotnet'
}
else {
    $dotnet = [IO.Path]::GetFullPath($dotnetCandidate)
    Assert-Condition (Test-Path -LiteralPath $dotnet -PathType Leaf) "Configured .NET SDK was not found: $dotnet"
}
$dotnetVersionOutput = @(& $dotnet '--version' 2>&1)
$dotnetVersion = ($dotnetVersionOutput -join ' ').Trim()
Assert-Condition ($LASTEXITCODE -eq 0) "The configured .NET SDK could not run: $dotnet"
Assert-Condition ($dotnetVersion -ceq '8.0.303') `
    ".NET SDK 8.0.303 is required; the runner selected $dotnetVersion. Use setup-dotnet with DOTNET_INSTALL_DIR and pass the isolated executable."
Ensure-AndroidPackages -SdkRoot $sdkRoot
$cmake = Get-CMakeAndNinja -SdkRoot $sdkRoot
$buildTools = Get-AndroidBuildTools -SdkRoot $sdkRoot
$ndkRoot = Join-Path $sdkRoot 'ndk\27.0.12077973'
$readelf = Join-Path $ndkRoot 'toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-readelf.exe'
Assert-Condition (Test-Path -LiteralPath $readelf -PathType Leaf) "Android llvm-readelf was not found: $readelf"
$rust = Resolve-RustToolchain
& $rust.Cargo fetch --manifest-path (Join-Path $repositoryRoot 'network-core\Cargo.toml') --locked
Assert-Condition ($LASTEXITCODE -eq 0) 'Rust dependency fetch failed.'

Write-Host "Android SDK: $sdkRoot"
Write-Host "Android build tools: $buildTools"
Write-Host "Android CMake: $($cmake.CMake)"
Write-Host "Android Ninja: $($cmake.Ninja)"
Write-Host "Rust Cargo: $($rust.Cargo)"
Write-Host "dotnet: $dotnet"
if ($PrepareOnly) {
    Write-Host 'GitHub Windows runner preparation passed; signing secrets were not read.'
    return
}

$androidStoreB64 = Get-RequiredSecret -Name 'GH_CLIENT_ANDROID_KEYSTORE_B64'
$androidStorePasswordText = Get-RequiredSecret -Name 'GH_CLIENT_ANDROID_PASSWORD'
$windowsPfxB64 = Get-RequiredSecret -Name 'GH_CLIENT_WINDOWS_PFX_B64'
$windowsPfxPasswordText = Get-RequiredSecret -Name 'GH_CLIENT_WINDOWS_PASSWORD'
$androidAlias = 'nuanya-release'
foreach ($name in 'GH_CLIENT_ANDROID_KEYSTORE_B64', 'GH_CLIENT_ANDROID_PASSWORD', 'GH_CLIENT_WINDOWS_PFX_B64', 'GH_CLIENT_WINDOWS_PASSWORD') {
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}

$secretRoot = Join-Path ([IO.Path]::GetTempPath()) ('termlink-client-signing-' + [guid]::NewGuid().ToString('N'))
$androidStorePath = Join-Path $secretRoot 'android-release.jks'
$windowsPfxPath = Join-Path $secretRoot 'windows-release.pfx'
$windowsCertPath = Join-Path $secretRoot 'windows-release.cer'
$androidPassword = $null
$windowsPassword = $null
$rootThumbprint = $null
$rootImported = $false
$savedEnvironment = @{}
$environmentNames = @(
    'ANDROID_SDK_ROOT', 'ANDROID_HOME', 'TERMLINK_CMAKE', 'TERMLINK_NINJA',
    'TERMLINK_CARGO', 'TERMLINK_LLVM_READELF', 'TERMLINK_RELEASE_STORE_FILE',
    'TERMLINK_RELEASE_STORE_PASSWORD', 'TERMLINK_RELEASE_KEY_ALIAS',
    'TERMLINK_RELEASE_KEY_PASSWORD')
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    [IO.Directory]::CreateDirectory($secretRoot) | Out-Null
    Write-Base64File -Base64 $androidStoreB64 -Path $androidStorePath -Name 'GH_CLIENT_ANDROID_KEYSTORE_B64'
    Write-Base64File -Base64 $windowsPfxB64 -Path $windowsPfxPath -Name 'GH_CLIENT_WINDOWS_PFX_B64'
    $androidPassword = ConvertTo-SecureString -String $androidStorePasswordText -AsPlainText -Force
    $windowsPassword = ConvertTo-SecureString -String $windowsPfxPasswordText -AsPlainText -Force

    $policy = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release\github-release-policy.json') -Raw | ConvertFrom-Json
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $windowsPfxPath, $windowsPassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        Assert-Condition $certificate.HasPrivateKey 'Windows PFX does not contain a private key.'
        $rootThumbprint = $certificate.Thumbprint.ToUpperInvariant()
        Assert-Condition ($rootThumbprint -ieq [string]$policy.windows_signer_sha1) `
            'Windows PFX does not match the pinned signer fingerprint.'
        [IO.File]::WriteAllBytes($windowsCertPath,
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    }
    finally { $certificate.Dispose() }
    Write-Host 'Installing pinned public Windows signing certificate on the disposable runner.'
    if (@(Get-ChildItem -LiteralPath "Cert:\LocalMachine\Root\$rootThumbprint" -ErrorAction SilentlyContinue).Count -eq 0) {
        & certutil.exe -addstore -f Root $windowsCertPath *> $null
        Assert-Condition ($LASTEXITCODE -eq 0) 'Pinned Windows signer trust setup failed.'
        $rootImported = $true
    }

    [Environment]::SetEnvironmentVariable('ANDROID_SDK_ROOT', $sdkRoot, 'Process')
    [Environment]::SetEnvironmentVariable('ANDROID_HOME', $sdkRoot, 'Process')
    [Environment]::SetEnvironmentVariable('TERMLINK_CMAKE', $cmake.CMake, 'Process')
    [Environment]::SetEnvironmentVariable('TERMLINK_NINJA', $cmake.Ninja, 'Process')
    [Environment]::SetEnvironmentVariable('TERMLINK_CARGO', $rust.Cargo, 'Process')
    [Environment]::SetEnvironmentVariable('TERMLINK_LLVM_READELF', $readelf, 'Process')

    $outputDirectory = Join-Path $repositoryRoot "artifacts\release\$Version-$BuildNumber"
    $timestampUrl = [Environment]::GetEnvironmentVariable('TERMLINK_WINDOWS_TIMESTAMP_URL', 'Process')
    if ([string]::IsNullOrWhiteSpace($timestampUrl)) { $timestampUrl = 'http://timestamp.acs.microsoft.com' }
    $build = @{
        Version = $Version; BuildNumber = $BuildNumber; ReleaseMode = 'Production'
        WindowsSetup = $true; AndroidApk = $true; OutputDirectory = $outputDirectory
        DotNetPath = $dotnet; CargoPath = $rust.Cargo
        WindowsCertificatePath = $windowsPfxPath; WindowsCertificatePassword = $windowsPassword
        WindowsTimestampUrl = $timestampUrl; AllowHttpWindowsTimestamp = $true
        ExpectedWindowsSignerSha1 = [string]$policy.windows_signer_sha1
        AndroidSdkPath = $sdkRoot; AndroidKeystorePath = $androidStorePath
        AndroidKeystorePassword = $androidPassword; AndroidKeyAlias = $androidAlias
        ExpectedAndroidSignerSha256 = [string]$policy.android_signer_sha256
    }
    Write-Host 'Building and signing the Windows Setup and Android APK.'
    & (Join-Path $repositoryRoot 'scripts\Build-Release.ps1') @build | Out-Host
    Assert-Condition ($LASTEXITCODE -eq 0) 'Production Android/Windows build failed.'

    $manifestPath = Join-Path $outputDirectory 'artifact-manifest.json'
    Assert-Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'Build did not produce artifact-manifest.json.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-Condition ([string]$manifest.version -ceq $Version -and [int]$manifest.build_number -eq $BuildNumber) `
        'Build manifest version/build does not match the requested release.'
    & (Join-Path $repositoryRoot 'release\Verify-LocalRelease.ps1') `
        -ReleaseDirectory $outputDirectory -ManifestPath $manifestPath `
        -RequireSourceIdentity -ExpectedReleaseMode Production -AndroidSdkPath $sdkRoot `
        -ExpectedAndroidSignerSha256 ([string]$policy.android_signer_sha256) `
        -ExpectedWindowsSignerSha1 ([string]$policy.windows_signer_sha1) -RequireWindowsTimestamp | Out-Host
    Assert-Condition ($LASTEXITCODE -eq 0) 'Independent client release verification failed.'
    [ordered]@{
        schema_version = 1; verified = $true
        source_revision = [string]$manifest.source_revision
        core_revision = [string]$manifest.core_revision
        version = [string]$manifest.version
        build_number = [int]$manifest.build_number
        manifest_sha256 = Get-Sha256Hex -Path $manifestPath
        verified_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputDirectory 'ci-verification.json') -Encoding utf8
    Write-Host "Verified client batch: $outputDirectory"
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    if ($rootImported -and $rootThumbprint) {
        Remove-Item -LiteralPath "Cert:\LocalMachine\Root\$rootThumbprint" -Force -ErrorAction SilentlyContinue
    }
    if ($androidPassword) { $androidPassword.Dispose() }
    if ($windowsPassword) { $windowsPassword.Dispose() }
    if (Test-Path -LiteralPath $secretRoot -PathType Container) {
        Remove-Item -LiteralPath $secretRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    $androidStoreB64 = $null; $windowsPfxB64 = $null
    $androidStorePasswordText = $null; $windowsPfxPasswordText = $null
}
