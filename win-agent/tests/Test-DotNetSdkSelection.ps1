[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Matches {
    param([Parameter(Mandatory)][string]$Actual, [Parameter(Mandatory)][string]$Pattern)
    if ($Actual -notmatch $Pattern) {
        throw "Expected '$Actual' to match '$Pattern'."
    }
}

function Get-FailureMessage {
    param([Parameter(Mandatory)][scriptblock]$Action)
    try {
        & $Action | Out-Null
    }
    catch {
        return $_.Exception.Message
    }
    throw 'Expected the script to fail.'
}

$agentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolverScript = Join-Path $agentRoot 'scripts\Resolve-DotNetSdk.ps1'
$buildWindows = Join-Path $agentRoot 'Build-Windows.ps1'
$buildSetup = Join-Path $agentRoot 'package\Build-Setup.ps1'
$buildMsix = Join-Path $agentRoot 'package\Build-Msix.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-DotNetSelection-' + [guid]::NewGuid().ToString('N'))
$originalTemp = $env:TEMP
$originalPath = $env:PATH

[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    . $resolverScript

    $emptyPathRoot = Join-Path $testRoot 'empty-path'
    $pathShimRoot = Join-Path $testRoot 'path-shim'
    $temporarySdkRoot = Join-Path $testRoot 'termlink-dotnet-sdk-8.0.303'
    $manifestRoot = Join-Path $testRoot 'manifest-root'
    $manifestSdkRoot = Join-Path $manifestRoot 'Env\dotnet-sdk-8.0.303'
    $explicitSdkRoot = Join-Path $testRoot 'explicit-sdk'
    foreach ($directory in @($emptyPathRoot, $pathShimRoot, $temporarySdkRoot, $manifestSdkRoot, $explicitSdkRoot)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $invalidPathShim = Join-Path $pathShimRoot 'dotnet.cmd'
    [IO.File]::WriteAllText($invalidPathShim, "@exit /b 87`r`n", [Text.Encoding]::ASCII)
    # This is intentionally an executable that is not dotnet. Automatic SDK
    # resolution must never consult the old TEMP convention.
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\where.exe') `
        -Destination (Join-Path $temporarySdkRoot 'dotnet.exe')

    $fakeDotNetBody = @'
@echo off
if "%~1"=="--list-sdks" (
  echo 8.0.303 [C:\fake\sdk]
  exit /b 0
)
if "%~1"=="--version" (
  echo 8.0.303
  exit /b 0
)
if "%~1"=="build" exit /b 91
exit /b 92
'@
    $manifestDotNet = Join-Path $manifestSdkRoot 'dotnet.cmd'
    $explicitDotNet = Join-Path $explicitSdkRoot 'dotnet.cmd'
    [IO.File]::WriteAllText($manifestDotNet, $fakeDotNetBody, [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($explicitDotNet, $fakeDotNetBody, [Text.Encoding]::ASCII)

    $manifestPath = Join-Path $testRoot 'DEV_ENV_MANIFEST.json'
    $manifest = [ordered]@{
        schemaVersion = 1
        roots = [ordered]@{ root = $manifestRoot }
        tools = @([ordered]@{
            id = 'dotnet'
            version = '8.0.303'
            probes = @([ordered]@{
                relativePath = 'Env\dotnet-sdk-8.0.303\dotnet.cmd'
                arguments = @('--version')
            })
        })
    }
    [IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))

    $inspectionMarker = Join-Path $testRoot 'ensure-invoked.txt'
    $escapedSdk = $manifestDotNet.Replace("'", "''")
    $escapedMarker = $inspectionMarker.Replace("'", "''")
    $ensureScript = Join-Path $testRoot 'Ensure-DevEnv.ps1'
    $ensureBody = @"
param([string[]]`$Tool, [string]`$ManifestPath, [switch]`$Json)
[IO.File]::WriteAllText('$escapedMarker', 'invoked')
[pscustomobject]@{
    Id = 'dotnet'
    Status = 'installed'
    Path = '$escapedSdk'
    Version = '8.0.303'
} | ConvertTo-Json
exit 0
"@
    [IO.File]::WriteAllText($ensureScript, $ensureBody, [Text.UTF8Encoding]::new($false))

    $env:TEMP = $testRoot
    $env:PATH = $pathShimRoot

    $selected = Resolve-TermLinkDotNetSdk -ProjectRoot $agentRoot `
        -ManifestPath $manifestPath -EnsureScriptPath $ensureScript
    Assert-True ($selected -ceq $manifestDotNet) 'The manifest SDK was not selected before PATH.'
    Assert-True (Test-Path -LiteralPath $inspectionMarker -PathType Leaf) `
        'Ensure-DevEnv.ps1 was not invoked for the manifest SDK.'

    Remove-Item -LiteralPath $inspectionMarker -Force
    $selected = Resolve-TermLinkDotNetSdk -ProjectRoot $agentRoot `
        -DotNetPath $explicitDotNet -DotNetPathWasExplicit `
        -ManifestPath $manifestPath -EnsureScriptPath $ensureScript
    Assert-True ($selected -ceq $explicitDotNet) 'Explicit -DotNetPath did not take precedence.'
    Assert-True (-not (Test-Path -LiteralPath $inspectionMarker)) `
        'Manifest inspection ran even though -DotNetPath was explicit.'

    $message = Get-FailureMessage {
        Resolve-TermLinkDotNetSdk -ProjectRoot $agentRoot `
            -DotNetPath $invalidPathShim -DotNetPathWasExplicit `
            -ManifestPath $manifestPath -EnsureScriptPath $ensureScript
    }
    Assert-Matches $message 'Explicit -DotNetPath'

    $missingManifest = Join-Path $testRoot 'ci-without-dev-manifest.json'
    $env:PATH = "$explicitSdkRoot;$pathShimRoot"
    $selected = Resolve-TermLinkDotNetSdk -ProjectRoot $agentRoot `
        -ManifestPath $missingManifest -EnsureScriptPath (Join-Path $testRoot 'missing-ensure.ps1')
    Assert-True ($selected -ceq 'dotnet') 'CI fallback did not select dotnet from PATH.'

    $env:PATH = $emptyPathRoot
    $message = Get-FailureMessage {
        Resolve-TermLinkDotNetSdk -ProjectRoot $agentRoot `
            -ManifestPath $missingManifest -EnsureScriptPath (Join-Path $testRoot 'missing-ensure.ps1')
    }
    Assert-Matches $message 'PATH fallback is invalid'
    Assert-Matches $message 'could not execute.+--list-sdks'

    $env:PATH = $originalPath
    $message = Get-FailureMessage { & $buildWindows -DotNetPath $explicitDotNet }
    Assert-Matches $message '^Windows solution build failed with exit code 91\.$'

    $password = ConvertTo-SecureString 'test-only' -AsPlainText -Force
    $missingCertificate = Join-Path $testRoot 'missing.pfx'
    foreach ($packageScript in @($buildSetup, $buildMsix)) {
        $message = Get-FailureMessage {
            & $packageScript -DotNetPath $explicitDotNet `
                -CertificatePath $missingCertificate -CertificatePassword $password
        }
        Assert-Matches $message '^Signing certificate not found:'
    }

    foreach ($consumer in @($buildWindows, $buildSetup, $buildMsix)) {
        $consumerText = Get-Content -Raw -LiteralPath $consumer
        Assert-True ($consumerText.Contains('scripts\Resolve-DotNetSdk.ps1')) `
            "SDK resolver is not wired into $consumer."
        Assert-True (-not $consumerText.Contains('termlink-dotnet-sdk-8.0.303')) `
            "Legacy TEMP SDK fallback remains in $consumer."
    }

    [pscustomobject]@{
        Passed = $true
        ManifestSelectedBeforePath = $true
        CorruptTemporarySdkIgnored = $true
        ExplicitPathHasPriority = $true
        CiPathFallback = $true
        ConsumersShareResolver = 3
        SelectedSdk = '8.0.303'
    }
}
finally {
    $env:TEMP = $originalTemp
    $env:PATH = $originalPath
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
