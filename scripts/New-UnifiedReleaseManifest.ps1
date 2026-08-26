#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$BuildNumber,
    [Parameter(Mandatory)][ValidateSet('preview', 'stable')][string]$Track,
    [Parameter(Mandatory)][ValidateSet('development', 'production')][string]$ReleaseMode,
    [Parameter(Mandatory)][string]$ArtifactInputPath,
    [string]$OutputPath,
    [string]$PromotionSourceManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $repositoryRoot 'release\UnifiedRelease.psm1') -Force
$mappingsPath = Join-Path $repositoryRoot 'release\appship-applications.json'
$releaseDirectory = Join-Path $repositoryRoot "artifacts\release\$Version-$BuildNumber"
[IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = Join-Path $releaseDirectory 'unified-artifact-manifest.json'
}
else {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
}
$releasePrefix = [IO.Path]::GetFullPath($releaseDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutput.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The unified manifest must remain in its exact artifacts/release/version-build directory.'
}
$resolvedInput = [IO.Path]::GetFullPath($ArtifactInputPath)
if (-not (Test-Path -LiteralPath $resolvedInput -PathType Leaf)) { throw "Artifact input not found: $resolvedInput" }
$inputs = @(Get-Content -LiteralPath $resolvedInput -Raw | ConvertFrom-Json)
if ($inputs.Count -eq 0) { throw 'At least one artifact input is required.' }
$ids = @{}
$tuples = @{}
$artifacts = @(
    foreach ($input in $inputs) {
        $artifact = Assert-TermLinkUnifiedArtifact -Artifact $input -Track $Track `
            -RepositoryRoot $repositoryRoot -MappingsPath $mappingsPath
        if ($ids.ContainsKey($artifact.id)) { throw "Duplicate artifact ID: $($artifact.id)" }
        $ids[$artifact.id] = $true
        $tuple = "$($artifact.platform):$($artifact.file_kind):$($artifact.architecture)"
        if ($tuples.ContainsKey($tuple)) { throw "Duplicate AppShip platform tuple: $tuple" }
        $tuples[$tuple] = $true
        $artifact
    }
)

$promotion = $null
if ($Track -ceq 'stable') {
    if ([string]::IsNullOrWhiteSpace($PromotionSourceManifestPath)) {
        throw 'Stable creation requires the exact qualified Preview manifest as promotion evidence.'
    }
    $previewPath = [IO.Path]::GetFullPath($PromotionSourceManifestPath)
    if (-not (Test-Path -LiteralPath $previewPath -PathType Leaf)) { throw "Preview promotion manifest not found: $previewPath" }
    $preview = Get-Content -LiteralPath $previewPath -Raw | ConvertFrom-Json
    if ([int]$preview.schema_version -ne 2 -or [string]$preview.track -cne 'preview' -or
        [string]$preview.version -cne $Version -or [int]$preview.build_number -ne $BuildNumber) {
        throw 'Stable promotion source is not the same version/build Preview manifest.'
    }
    foreach ($artifact in $artifacts) {
        $sourceMatches = @($preview.artifacts | Where-Object {
            [string]$_.id -ceq $artifact.id -and [string]$_.sha256 -ceq $artifact.sha256
        })
        if ($sourceMatches.Count -ne 1) { throw "Stable bytes differ from Preview artifact '$($artifact.id)'." }
    }
    $promotion = [pscustomobject][ordered]@{
        source_track = 'preview'
        source_manifest_sha256 = Get-TermLinkSha256 -Path $previewPath
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($PromotionSourceManifestPath)) {
    throw 'PromotionSourceManifestPath is only valid for the stable track.'
}

$revision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'A clean Git source revision is required.' }
$dirty = @(& git -C $repositoryRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'A unified release manifest must be generated from a clean worktree.' }

$manifest = [pscustomobject][ordered]@{
    '$schema' = '../../../release/unified-artifact-manifest.schema.json'
    schema_version = 2
    product = 'TermLink'
    version = $Version
    build_number = $BuildNumber
    track = $Track
    release_mode = $ReleaseMode
    source_revision = $revision
    generated_at_utc = [DateTimeOffset]::UtcNow.ToString('o')
    promotion = $promotion
    artifacts = @($artifacts | Sort-Object platform, architecture, file_kind)
}
$temporary = $resolvedOutput + '.new'
try {
    [IO.File]::WriteAllText($temporary, ($manifest | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $resolvedOutput -Force
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
[pscustomobject][ordered]@{
    manifest_path = $resolvedOutput
    manifest_sha256 = Get-TermLinkSha256 -Path $resolvedOutput
    artifacts = $artifacts.Count
    track = $Track
} | ConvertTo-Json -Compress
