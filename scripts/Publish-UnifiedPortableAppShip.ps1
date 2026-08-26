#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ArtifactId,
    [Parameter(Mandatory)][string]$BaseURL,
    [Parameter(Mandatory)][PSCredential]$Credential,
    [Parameter(Mandatory)][string]$PublishScriptPath,
    [Parameter(Mandatory)][string]$SignatureReviewNote,
    [switch]$SignatureApproved,
    [switch]$Resume,
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $repositoryRoot 'release\UnifiedRelease.psm1') -Force
$resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
$verificationScript = Join-Path $repositoryRoot 'scripts\Test-UnifiedReleaseManifest.ps1'
$verification = & $verificationScript -ManifestPath $resolvedManifest | ConvertFrom-Json
if (-not [bool]$verification.verified) { throw 'Unified release verification failed.' }
$manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
$matches = @($manifest.artifacts | Where-Object { [string]$_.id -ceq $ArtifactId })
if ($matches.Count -ne 1) { throw "Exactly one unified artifact must match '$ArtifactId'." }
$artifact = $matches[0]
if ([string]$artifact.platform -notin @('linux', 'macos')) {
    throw 'Windows and Android publishing must keep using Publish-AppShipProduction.ps1 for native signature re-verification.'
}
if (-not [bool]$artifact.qualification.platform_e2e_verified -or
    -not [bool]$artifact.qualification.health_verified -or
    @($artifact.qualification.blockers).Count -ne 0) {
    throw 'Portable artifacts cannot publish until platform E2E, health evidence, and blockers are resolved.'
}
if (-not $SignatureApproved -or [string]::IsNullOrWhiteSpace($SignatureReviewNote)) {
    throw 'An authorized signature review of the exact artifact is required.'
}
$baseUri = $null
if (-not [Uri]::TryCreate($BaseURL, [UriKind]::Absolute, [ref]$baseUri) -or
    $baseUri.Scheme -cne 'https' -or -not [string]::IsNullOrWhiteSpace($baseUri.UserInfo) -or
    -not [string]::IsNullOrWhiteSpace($baseUri.Query) -or -not [string]::IsNullOrWhiteSpace($baseUri.Fragment)) {
    throw 'AppShip BaseURL must be an absolute credential-free HTTPS origin.'
}
$resolvedClient = [IO.Path]::GetFullPath($PublishScriptPath)
if (-not (Test-Path -LiteralPath $resolvedClient -PathType Leaf)) { throw 'The reviewed AppShip client is missing.' }
$clientSource = Get-Content -LiteralPath $resolvedClient -Raw
foreach ($marker in @("schemaVersion = 'appship.publish-state/v1'", 'Idempotency-Key', 'SignatureDecision')) {
    if (-not $clientSource.Contains($marker)) { throw 'PublishScriptPath is not the reviewed resumable AppShip client.' }
}
$artifactPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ([string]$artifact.path).Replace('/', [IO.Path]::DirectorySeparatorChar)))
if ((Get-TermLinkSha256 -Path $artifactPath) -cne [string]$artifact.sha256) { throw 'Artifact changed after unified verification.' }
$stateRoot = Join-Path $repositoryRoot '.appship'
[IO.Directory]::CreateDirectory($stateRoot) | Out-Null
$slug = [string]$artifact.appship.application_slug
$track = [string]$manifest.track
$version = [string]$manifest.version
$build = [int]$manifest.build_number
$statePath = Join-Path $stateRoot "$slug-$track-$version-build$build-signature-approved.json"
$fileKind = [string]$artifact.file_kind
$parameters = @{
    BaseURL = $baseUri.AbsoluteUri.TrimEnd('/')
    Credential = $Credential
    ApplicationSlug = $slug
    ArtifactPath = $artifactPath
    Platform = [string]$artifact.platform
    FileKind = $fileKind
    Architecture = [string]$artifact.architecture
    Channel = [string]$artifact.appship.channel
    VersionName = $version
    BuildNumber = $build
    Title = "TermLink $($artifact.platform) $version"
    ReleaseNotes = "Unified manifest SHA-256: $($verification.manifest_sha256)"
    RequireSignatureReview = $true
    SignatureDecision = 'approved'
    SignerFingerprint = [string]$artifact.signature.identity
    SignatureReviewNote = $SignatureReviewNote.Trim()
    StatePath = $statePath
}
if ($Resume) { $parameters.Resume = $true }
$planResult = [pscustomobject][ordered]@{
    operation = if ($Plan) { 'plan' } else { 'publish' }
    application_slug = $slug
    channel = [string]$artifact.appship.channel
    artifact_id = $ArtifactId
    artifact_sha256 = [string]$artifact.sha256
    state_path = $statePath
    resume = [bool]$Resume
}
if ($Plan) { $planResult | ConvertTo-Json -Compress; return }
& $resolvedClient @parameters
