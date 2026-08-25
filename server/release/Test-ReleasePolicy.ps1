[CmdletBinding()]
param(
    [string]$WorkflowPath = (Join-Path $PSScriptRoot '..\..\.github\workflows\release.yml'),
    [string]$DockerfilePath = (Join-Path $PSScriptRoot '..\Dockerfile'),
    [string]$VerifierPath = (Join-Path $PSScriptRoot 'Verify-ImageRelease.ps1'),
    [string]$DeploymentPolicyPath = (Join-Path $PSScriptRoot '..\ops\Test-ProductionDeployment.ps1'),
    [string]$AcceptanceStartPath = (Join-Path $PSScriptRoot '..\ops\Start-ProductionAcceptance.ps1'),
    [string]$AcceptanceOverlayPath = (Join-Path $PSScriptRoot '..\compose\acceptance.release-image.compose.yml')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Policy {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Remove-YamlComment {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Line)
    $singleQuoted = $false
    $doubleQuoted = $false
    for ($index = 0; $index -lt $Line.Length; $index++) {
        $character = $Line[$index]
        if ($character -eq "'" -and -not $doubleQuoted) {
            $singleQuoted = -not $singleQuoted
        }
        elseif ($character -eq '"' -and -not $singleQuoted -and
            ($index -eq 0 -or $Line[$index - 1] -ne '\')) {
            $doubleQuoted = -not $doubleQuoted
        }
        elseif ($character -eq '#' -and -not $singleQuoted -and -not $doubleQuoted) {
            return $Line.Substring(0, $index).TrimEnd()
        }
    }
    return $Line.TrimEnd()
}

$workflow = Get-Content -Raw -LiteralPath $WorkflowPath
$dockerfile = Get-Content -Raw -LiteralPath $DockerfilePath
$verifier = Get-Content -Raw -LiteralPath $VerifierPath
$deploymentPolicy = Get-Content -Raw -LiteralPath $DeploymentPolicyPath
$acceptanceStart = Get-Content -Raw -LiteralPath $AcceptanceStartPath
$acceptanceOverlay = Get-Content -Raw -LiteralPath $AcceptanceOverlayPath
$dockerfileFrontend = '# syntax=docker/dockerfile:1.7@sha256:a57df69d0ea827fb7266491f2813635de6f17269be881f696fbfdf2d83dda33e'
$activeWorkflow = ((($workflow -split '\r?\n') | ForEach-Object { Remove-YamlComment $_ }) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine
$activeDockerfile = ((($dockerfile -split '\r?\n') |
    Where-Object { -not $_.TrimStart().StartsWith('#', [StringComparison]::Ordinal) }) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine

Assert-Policy ($activeWorkflow -notmatch '(?im)^\s*image:\s*[^\s]+:latest\s*$') `
    'Release workflow contains a latest image reference.'
Assert-Policy ($activeWorkflow -match '(?m)^\s*IMAGE:\s*docker\.io/myucxy/termlink\s*$') `
    'Release workflow does not target the reviewed Docker Hub repository.'
Assert-Policy ($activeWorkflow -notmatch 'ghcr\.io|secrets\.GITHUB_TOKEN|github\.actor') `
    'Release workflow retains an unreviewed GHCR login or image reference.'
foreach ($requiredPattern in @(
    '(?m)^\s*registry:\s*docker\.io\s*$',
    '(?m)^\s*username:\s*\$\{\{\s*secrets\.DOCKERHUB_USERNAME\s*\}\}\s*$',
    '(?m)^\s*password:\s*\$\{\{\s*secrets\.DOCKERHUB_TOKEN\s*\}\}\s*$'
)) {
    Assert-Policy ($activeWorkflow -match $requiredPattern) `
        "Release workflow is missing Docker Hub credential binding: $requiredPattern"
}
Assert-Policy ($dockerfile.StartsWith($dockerfileFrontend, [StringComparison]::Ordinal)) `
    'Dockerfile frontend is not pinned to the reviewed immutable digest.'
$baseImages = @([regex]::Matches($activeDockerfile, '(?im)^FROM\s+(?:--platform=[^\s]+\s+)?([^\s]+)'))
Assert-Policy ($baseImages.Count -ge 2) 'Dockerfile build and runtime stages were not found.'
foreach ($baseImage in $baseImages) {
    Assert-Policy ($baseImage.Groups[1].Value -match '@sha256:[0-9a-f]{64}$') `
        "Dockerfile base image is not digest-pinned: $($baseImage.Groups[1].Value)"
}
$actionReferences = @([regex]::Matches($activeWorkflow, '(?im)^\s*(?:-\s*)?uses:\s*([^\s]+)\s*$'))
Assert-Policy ($actionReferences.Count -ge 8) 'Expected pinned release actions were not found.'
$approvedActions = @(
    'actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683',
    'docker/setup-qemu-action@29109295f81e9208d7d86ff1c6c12d2833863392',
    'docker/setup-buildx-action@e468171a9de216ec08956ac3ada2f0791b6bd435',
    'docker/login-action@74a5d142397b4f367a81961eba4e8cd7edddf772',
    'docker/metadata-action@c1e51972afc2121e065aed6d45c65596fe445f3f',
    'docker/build-push-action@263435318d21b8e681c14492fe198d362a7d2c83',
    'sigstore/cosign-installer@d58896d6a1865668819e1d91763c7751a165e159',
    'anchore/sbom-action@df80a981bc6edbc4e220a492d3cbe9f5547a6e75',
    'actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02'
)
foreach ($reference in $actionReferences) {
    $action = $reference.Groups[1].Value
    Assert-Policy ($action -match '@[0-9a-f]{40}$') `
        "Action reference is not pinned to a commit: $($reference.Value.Trim())"
    Assert-Policy ($approvedActions -contains $action) `
        "Release workflow uses an action that has not been reviewed: $action"
}
foreach ($action in $approvedActions) {
    Assert-Policy (@($actionReferences | Where-Object { $_.Groups[1].Value -eq $action }).Count -gt 0) `
        "Release workflow is missing reviewed action: $action"
}

foreach ($requiredPattern in @(
    '(?m)^\s*platforms:\s*linux/amd64,linux/arm64\s*$',
    '(?m)^\s*provenance:\s*mode=max\s*$',
    '(?m)^\s*sbom:\s*true\s*$',
    '(?m)^\s*(?:run:\s*)?cosign sign --yes\s+',
    '(?m)^\s*docker buildx imagetools inspect\s+',
    '(?m)^\s*cosign attest --yes --type slsaprovenance\s+',
    '(?m)^\s*cosign attest --yes --type spdxjson\s+',
    '(?m)^\s*cosign verify --certificate-identity\s+',
    '(?m)^\s*cosign verify-attestation --type slsaprovenance\s+',
    '(?m)^\s*cosign verify-attestation --type cyclonedx\s+',
    '(?m)^\s*flavor:\s*latest=false\s*$'
)) {
    Assert-Policy ($activeWorkflow -match $requiredPattern) `
        "Release workflow is missing required policy pattern: $requiredPattern"
}

foreach ($requiredPattern in @(
    '(?m)^FROM --platform=\$BUILDPLATFORM\s+',
    '(?m)^ARG TARGETOS\s*$',
    '(?m)^ARG TARGETARCH\s*$',
    '(?m)^RUN CGO_ENABLED=0 GOOS=\$\{TARGETOS\} GOARCH=\$\{TARGETARCH\} go build[ \t]*\\[ \t]*\r?$',
    '(?m)^\s*org\.opencontainers\.image\.source=',
    '(?m)^\s*org\.opencontainers\.image\.revision=',
    '(?m)^\s*org\.opencontainers\.image\.version=',
    '(?m)^USER 65532:65532\s*$'
)) {
    Assert-Policy ($activeDockerfile -match $requiredPattern) `
        "Dockerfile is missing release invariant: $requiredPattern"
}

Assert-Policy ($activeDockerfile -notmatch '(?m)^ARG TARGET(?:OS|ARCH)=') `
    'Dockerfile must not override BuildKit automatic target-platform arguments.'

$immutableDockerHubPattern = '^docker\.io/myucxy/termlink:[A-Za-z0-9_.-]+@sha256:[0-9a-f]{64}$'
Assert-Policy ($verifier.Contains('^docker\.io/myucxy/termlink$')) `
    'Post-release signature verifier does not pin the reviewed Docker Hub repository.'
Assert-Policy ($deploymentPolicy.Contains($immutableDockerHubPattern)) `
    'Production deployment policy does not require the immutable Docker Hub image.'
Assert-Policy ($acceptanceStart.Contains($immutableDockerHubPattern) -and
    $acceptanceStart.Contains('org.opencontainers.image.revision') -and
    $acceptanceStart.Contains('org.opencontainers.image.source')) `
    'Acceptance deployment does not validate immutable image identity and OCI source metadata.'
Assert-Policy ($acceptanceOverlay.Contains('TERMLINK_ACCEPTANCE_SERVER_IMAGE') -and
    $acceptanceOverlay.Contains('build: !reset null')) `
    'Acceptance image overlay can rebuild or substitute the published server image.'

Write-Host 'TermLink release workflow and Dockerfile policy validation passed.'
