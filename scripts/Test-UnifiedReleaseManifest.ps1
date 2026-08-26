#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [switch]$RequireStable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $repositoryRoot 'release\UnifiedRelease.psm1') -Force
$resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\release')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedManifest.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $resolvedManifest -PathType Leaf)) {
    throw 'The unified manifest must be an existing file inside artifacts/release.'
}
$manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
if ([int]$manifest.schema_version -ne 2 -or [string]$manifest.product -cne 'TermLink' -or
    [string]$manifest.version -notmatch '^\d+\.\d+\.\d+$' -or [int]$manifest.build_number -notin 1..65535 -or
    [string]$manifest.track -notin @('preview', 'stable') -or
    [string]$manifest.release_mode -notin @('development', 'production') -or
    [string]$manifest.source_revision -notmatch '^[0-9a-f]{40}$') {
    throw 'The unified manifest header is invalid.'
}
if ($RequireStable -and [string]$manifest.track -cne 'stable') { throw 'A stable manifest is required.' }
$expectedDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\release\$($manifest.version)-$($manifest.build_number)"))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($resolvedManifest), $expectedDirectory,
        [StringComparison]::OrdinalIgnoreCase)) { throw 'The unified manifest version/build directory is inconsistent.' }
$tuples = @{}
$results = @(
    foreach ($artifact in @($manifest.artifacts)) {
        $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ([string]$artifact.path).Replace('/', [IO.Path]::DirectorySeparatorChar)))
        if (-not $path.StartsWith($expectedDirectory + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Artifact escaped the release directory or is missing: $($artifact.path)"
        }
        $file = Get-Item -LiteralPath $path
        $hash = Get-TermLinkSha256 -Path $path
        if ($file.Length -ne [int64]$artifact.size -or $hash -cne [string]$artifact.sha256) {
            throw "Artifact bytes do not match the manifest: $($artifact.id)"
        }
        $tuple = "$($artifact.platform):$($artifact.file_kind):$($artifact.architecture)"
        if ($tuples.ContainsKey($tuple)) { throw "Duplicate AppShip tuple: $tuple" }
        $tuples[$tuple] = $true
        $blockers = @($artifact.qualification.blockers)
        $qualified = [bool]$artifact.signature.verified -and [bool]$artifact.updater.rollback_verified -and
            [bool]$artifact.qualification.build_verified -and [bool]$artifact.qualification.platform_e2e_verified -and
            [bool]$artifact.qualification.health_verified -and $blockers.Count -eq 0
        if ([string]$manifest.track -ceq 'stable' -and -not $qualified) {
            throw "Stable artifact is not qualified: $($artifact.id)"
        }
        if ([string]$manifest.track -ceq 'preview' -and
            (-not [bool]$artifact.signature.verified -or -not [bool]$artifact.qualification.build_verified)) {
            throw "Preview artifact lacks build/signature verification: $($artifact.id)"
        }
        [pscustomobject][ordered]@{ id = [string]$artifact.id; sha256 = $hash; qualified_for_stable = $qualified }
    }
)
if ($results.Count -eq 0) { throw 'The unified manifest contains no artifacts.' }
if ([string]$manifest.track -ceq 'stable') {
    if ($null -eq $manifest.promotion -or [string]$manifest.promotion.source_track -cne 'preview' -or
        [string]$manifest.promotion.source_manifest_sha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'Stable promotion provenance is missing.'
    }
}
[pscustomobject][ordered]@{
    verified = $true
    manifest_sha256 = Get-TermLinkSha256 -Path $resolvedManifest
    track = [string]$manifest.track
    artifacts = $results
} | ConvertTo-Json -Depth 6 -Compress
