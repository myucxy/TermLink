[CmdletBinding()]
param(
    [string]$CargoPath,
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-PowerShellSyntax {
    param([Parameter(Mandatory)][string]$Path)

    $tokens = $null
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) `
        ("PowerShell parser errors in '$Path':`n" + (($errors | ForEach-Object { $_.Message }) -join "`n"))
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $output = @(& $Executable @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

$agentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $agentRoot '..'))
$nativeBuilder = Join-Path $agentRoot 'scripts\Build-DirectCoreNative.ps1'
$setupBuilder = Join-Path $agentRoot 'package\Build-Setup.ps1'
$msixBuilder = Join-Path $agentRoot 'package\Build-Msix.ps1'
$agentProject = Join-Path $agentRoot 'src\Agent\TermLink.Agent.csproj'

foreach ($script in @($nativeBuilder, $setupBuilder, $msixBuilder)) {
    Assert-True (Test-Path -LiteralPath $script -PathType Leaf) "Required script is missing: $script"
    Assert-PowerShellSyntax -Path $script
}

$agentProjectSource = Get-Content -LiteralPath $agentProject -Raw
Assert-True ($agentProjectSource.Contains(
        'network-core\target\x86_64-pc-windows-msvc\release\termlink_direct_ffi.dll')) `
    'Agent does not use the explicit Windows target network-core artifact.'
Assert-True (-not $agentProjectSource.Contains(
        'network-core\target\release\termlink_direct_ffi.dll')) `
    'Agent still contains the ambiguous non-target network-core fallback.'
Assert-True ($agentProjectSource.Contains('ValidateTermLinkDirectNativeLibrary')) `
    'Agent has no fail-closed Release native-library gate.'
foreach ($packager in @($setupBuilder, $msixBuilder)) {
    $source = Get-Content -LiteralPath $packager -Raw
    Assert-True ($source.Contains('Build-DirectCoreNative.ps1')) `
        "Packager does not invoke the pinned Direct native builder: $packager"
    Assert-True ($source.Contains('termlink_direct_ffi.dll')) `
        "Packager does not require the Direct native DLL: $packager"
    Assert-True ($source.Contains('differs from the native artifact built for this release')) `
        "Packager does not compare the Direct DLL hash with this build: $packager"
}

$dotNetPathWasExplicit = $PSBoundParameters.ContainsKey('DotNetPath')
. (Join-Path $agentRoot 'scripts\Resolve-DotNetSdk.ps1')
$dotnet = Resolve-TermLinkDotNetSdk -ProjectRoot $agentRoot `
    -DotNetPath $DotNetPath -DotNetPathWasExplicit:$dotNetPathWasExplicit

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-DirectNativePackaging-' + [guid]::NewGuid().ToString('N'))
$stagedNative = Join-Path $workRoot 'staged-native'
$releaseOutput = Join-Path $workRoot 'release-output'
$acceptanceOutput = Join-Path $workRoot 'acceptance-output'
$dotnetArtifacts = Join-Path $workRoot 'dotnet-artifacts'
[IO.Directory]::CreateDirectory($stagedNative) | Out-Null
try {
    $nativeBuildOutput = @(& $nativeBuilder -CargoPath $CargoPath -OutputDirectory $stagedNative)
    $nativeBuild = @($nativeBuildOutput | Where-Object {
        $_ -is [psobject] -and $_.PSObject.Properties['NativeLibrary'] -and
        $_.PSObject.Properties['Sha256']
    } | Select-Object -Last 1)
    Assert-True ($nativeBuild.Count -eq 1) 'Direct native builder returned no artifact evidence.'
    $nativeBuild = $nativeBuild[0]

    $tripleTargetNative = Join-Path $repositoryRoot `
        'network-core\target\x86_64-pc-windows-msvc\release\termlink_direct_ffi.dll'
    $stagedNativePath = Join-Path $stagedNative 'termlink_direct_ffi.dll'
    Assert-True (Test-Path -LiteralPath $tripleTargetNative -PathType Leaf) `
        'Pinned triple-target Direct DLL was not produced.'
    Assert-True (Test-Path -LiteralPath $stagedNativePath -PathType Leaf) `
        'Direct native builder did not stage its output.'
    $freshHash = Get-Sha256Hex -Path $tripleTargetNative
    Assert-True ([string]::Equals($freshHash, [string]$nativeBuild.Sha256, [StringComparison]::Ordinal)) `
        'Direct native build evidence differs from the triple-target artifact.'
    Assert-True ([string]::Equals($freshHash, (Get-Sha256Hex -Path $stagedNativePath), [StringComparison]::Ordinal)) `
        'Staged Direct DLL differs from the freshly built triple-target artifact.'

    Invoke-DotNet -Executable $dotnet -Arguments @(
        'build', $agentProject,
        '--configuration', 'Release',
        '--framework', 'net8.0-windows',
        '--output', $releaseOutput,
        '--artifacts-path', $dotnetArtifacts,
        '--nologo',
        '-p:RestoreLockedMode=true',
        '-p:PackageVersion=1.0.0')
    $releaseNative = Join-Path $releaseOutput 'termlink_direct_ffi.dll'
    Assert-True (Test-Path -LiteralPath $releaseNative -PathType Leaf) `
        'Agent Release output omitted the Direct native DLL.'
    Assert-True ([string]::Equals($freshHash, (Get-Sha256Hex -Path $releaseNative), [StringComparison]::Ordinal)) `
        'Agent Release output contains a stale Direct native DLL.'

    Invoke-DotNet -Executable $dotnet -Arguments @(
        'build', $agentProject,
        '--configuration', 'DirectAcceptance',
        '--framework', 'net8.0-windows',
        '--output', $acceptanceOutput,
        '--artifacts-path', $dotnetArtifacts,
        '--nologo',
        '-p:RestoreLockedMode=true',
        '-p:PackageVersion=1.0.0',
        '-p:TermLinkDirectDataPlaneAcceptance=true')
    $acceptanceNative = Join-Path $acceptanceOutput 'termlink_direct_ffi.dll'
    Assert-True (Test-Path -LiteralPath $acceptanceNative -PathType Leaf) `
        'Agent DirectAcceptance output omitted the Direct native DLL.'
    Assert-True ([string]::Equals($freshHash, (Get-Sha256Hex -Path $acceptanceNative), [StringComparison]::Ordinal)) `
        'Agent DirectAcceptance output contains a stale Direct native DLL.'

    [pscustomobject][ordered]@{
        native_sha256 = $freshHash
        cargo_path = [string]$nativeBuild.CargoPath
        toolchain = [string]$nativeBuild.Toolchain
        target = [string]$nativeBuild.Target
        release_copy_verified = $true
        direct_acceptance_copy_verified = $true
    }
}
finally {
    $resolvedWork = [IO.Path]::GetFullPath($workRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + `
        [IO.Path]::DirectorySeparatorChar
    if ($resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedWork).StartsWith(
            'TermLink-DirectNativePackaging-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
    }
}
