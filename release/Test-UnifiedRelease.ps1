#Requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release'))
$version = '99.99.99'
$build = 65535
$testRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot "$version-$build"))
$releasePrefix = $releaseRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $testRoot.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unified release test root escaped artifacts/release.'
}
if (Test-Path -LiteralPath $testRoot) { throw "Unified release test root already exists: $testRoot" }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $artifactPath = Join-Path $testRoot "TermLink-Setup-$version.$build-x64.exe"
    [IO.File]::WriteAllBytes($artifactPath, [Text.Encoding]::UTF8.GetBytes('signed validation fixture'))
    $inputPath = Join-Path $testRoot 'preview-input.json'
    $input = @([pscustomobject][ordered]@{
        id = 'windows-x64-setup'
        path = "artifacts/release/$version-$build/TermLink-Setup-$version.$build-x64.exe"
        platform = 'windows'
        architecture = 'x64'
        file_kind = 'exe'
        minimum_os = 'Windows 10 1809'
        signature_policy = 'authenticode-rfc3161'
        signature_identity = '0123456789abcdef0123456789abcdef01234567'
        signature_verified = $true
        signature_evidence = 'Validation fixture; not a distributable package.'
        updater_strategy = 'windows-setup'
        rollback_verified = $false
        build_verified = $true
        platform_e2e_verified = $false
        health_verified = $false
        blockers = @('fixture intentionally lacks platform E2E')
    })
    [IO.File]::WriteAllText($inputPath, ($input | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $generator = Join-Path $repositoryRoot 'scripts\New-UnifiedReleaseManifest.ps1'
    $verifier = Join-Path $repositoryRoot 'scripts\Test-UnifiedReleaseManifest.ps1'
    $previewOutput = & $generator -Version $version -BuildNumber $build -Track preview `
        -ReleaseMode development -ArtifactInputPath $inputPath
    if ($LASTEXITCODE -ne 0) { throw 'Preview unified manifest generation failed.' }
    $previewResult = $previewOutput | ConvertFrom-Json
    $verification = & $verifier -ManifestPath $previewResult.manifest_path | ConvertFrom-Json
    if (-not [bool]$verification.verified -or [string]$verification.track -cne 'preview') {
        throw 'Preview unified manifest verification failed.'
    }

    $stableRejected = $false
    try {
        & $generator -Version $version -BuildNumber $build -Track stable -ReleaseMode production `
            -ArtifactInputPath $inputPath -PromotionSourceManifestPath $previewResult.manifest_path | Out-Null
    }
    catch { $stableRejected = $true }
    if (-not $stableRejected) { throw 'An unqualified stable artifact was accepted.' }

    [IO.File]::AppendAllText($artifactPath, 'tamper')
    $tamperRejected = $false
    try { & $verifier -ManifestPath $previewResult.manifest_path | Out-Null }
    catch { $tamperRejected = $true }
    if (-not $tamperRejected) { throw 'Artifact tampering was accepted.' }
    Write-Output 'Unified release preview, stable qualification, promotion provenance, and tamper gates passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved -PathType Container)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
