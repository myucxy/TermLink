[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^docker\.io/myucxy/termlink$')]
    [string]$Image,
    [Parameter(Mandatory)]
    [ValidatePattern('^sha256:[0-9a-f]{64}$')]
    [string]$Digest,
    [Parameter(Mandatory)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Tag,
    [ValidatePattern('^https://token\.actions\.githubusercontent\.com$')]
    [string]$CertificateIssuer = 'https://token.actions.githubusercontent.com',
    [string]$CosignPath = 'cosign'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$cosign = Get-Command $CosignPath -CommandType Application -ErrorAction Stop
$reference = "$Image@$Digest"
$certificateIdentity = "https://github.com/myucxy/TermLink/.github/workflows/release.yml@refs/tags/$Tag"
$common = @(
    '--certificate-identity', $CertificateIdentity,
    '--certificate-oidc-issuer', $CertificateIssuer,
    $reference
)

& $cosign.Source verify @common
if ($LASTEXITCODE -ne 0) { throw 'Cosign image signature verification failed.' }

foreach ($type in @('slsaprovenance', 'spdxjson', 'cyclonedx')) {
    & $cosign.Source verify-attestation --type $type @common
    if ($LASTEXITCODE -ne 0) { throw "Cosign $type attestation verification failed." }
}

Write-Host "Verified immutable TermLink image release: $reference"
