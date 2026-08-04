[CmdletBinding()]
param(
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ProductionAcceptance.Common.psm1') -Force

function New-RandomText {
    param([ValidateRange(16, 128)][int]$ByteCount)
    $bytes = [byte[]]::new($ByteCount)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    try {
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    finally {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
    }
}

function Write-SecretFile {
    param([string]$Path, [string]$Value)
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return }
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

$paths = Get-TermLinkAcceptancePaths
if ($Reset -and (Test-Path -LiteralPath $paths.SecretRoot -PathType Container)) {
    if (-not (Test-Path -LiteralPath $paths.Marker -PathType Leaf)) {
        throw "Refusing to reset an unmanaged directory: $($paths.SecretRoot)"
    }
    Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
    & docker compose -f $paths.ComposeFile down --volumes --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'Could not reset the production acceptance stack.' }
    foreach ($file in @(
        $paths.AdminPassword, $paths.UserPassword, $paths.PostgresPassword,
        $paths.OidcClientSecret, $paths.DatabaseUrl, $paths.RedisUrl,
        $paths.TokenSigningKey, $paths.RootCa, $paths.Marker
    )) {
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            Remove-Item -LiteralPath $file -Force
        }
    }
    Remove-Item -LiteralPath $paths.SecretRoot -Force
}

if (Test-Path -LiteralPath $paths.SecretRoot -PathType Container) {
    if (-not (Test-Path -LiteralPath $paths.Marker -PathType Leaf)) {
        throw "Refusing to use an unmanaged directory: $($paths.SecretRoot)"
    }
}
else {
    New-Item -ItemType Directory -Path $paths.SecretRoot | Out-Null
    [IO.File]::WriteAllText($paths.Marker, 'TermLink production acceptance secrets', [Text.UTF8Encoding]::new($false))
}

Write-SecretFile $paths.AdminPassword (New-RandomText 32)
Write-SecretFile $paths.UserPassword (New-RandomText 32)
$postgresPassword = if (Test-Path -LiteralPath $paths.PostgresPassword -PathType Leaf) {
    [IO.File]::ReadAllText($paths.PostgresPassword)
} else {
    $generated = New-RandomText 32
    Write-SecretFile $paths.PostgresPassword $generated
    $generated
}
try {
    Write-SecretFile $paths.DatabaseUrl "postgres://termlink:$postgresPassword@postgres:5432/termlink?sslmode=disable"
}
finally {
    $postgresPassword = $null
}
Write-SecretFile $paths.RedisUrl 'redis://valkey:6379/0'
Write-SecretFile $paths.OidcClientSecret (New-RandomText 32)
Write-SecretFile $paths.TokenSigningKey (New-RandomText 48)

Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
$env:TERMLINK_ACCEPTANCE_KEYCLOAK_IMAGE = & (Join-Path $PSScriptRoot 'Import-KeycloakAcceptanceImage.ps1')
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:TERMLINK_ACCEPTANCE_KEYCLOAK_IMAGE)) {
    throw 'Could not prepare the pinned Keycloak acceptance image.'
}
& docker compose -f $paths.ComposeFile up --build --detach --wait --wait-timeout 300
if ($LASTEXITCODE -ne 0) { throw 'The production acceptance stack did not become ready.' }

if (Test-Path -LiteralPath $paths.RootCa -PathType Leaf) {
    Remove-Item -LiteralPath $paths.RootCa -Force
}
& docker compose -f $paths.ComposeFile cp edge:/data/caddy/pki/authorities/local/root.crt $paths.RootCa
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $paths.RootCa -PathType Leaf)) {
    throw 'Could not export the disposable acceptance CA certificate.'
}

$env:TERMLINK_ACCEPTANCE_ROOT_URL = 'https://api.termlink.localhost:9443'
$env:TERMLINK_ACCEPTANCE_OIDC_ISSUER = 'https://id.termlink.localhost:9443/realms/termlink'
$env:TERMLINK_ACCEPTANCE_CA_FILE = $paths.RootCa
$go = if (Test-Path -LiteralPath 'D:\Dev\Env\Go\bin\go.exe' -PathType Leaf) {
    'D:\Dev\Env\Go\bin\go.exe'
} else {
    'go'
}
Push-Location $paths.ServerRoot
try {
    & $go run ./cmd/production-e2e-probe
    if ($LASTEXITCODE -ne 0) { throw 'The production OIDC/TLS/API/Realtime probe failed.' }
}
finally {
    Pop-Location
}

Write-Output 'TermLink production acceptance stack is running.'
Write-Output 'API: https://api.termlink.localhost:9443'
Write-Output 'OIDC: https://id.termlink.localhost:9443/realms/termlink'
Write-Output "CA certificate: $($paths.RootCa)"
