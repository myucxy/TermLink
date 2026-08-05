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
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
    finally {
        $rng.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Write-SecretFile {
    param([string]$Path, [string]$Value, [switch]$Force)
    if (-not $Force -and (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

$paths = Get-TermLinkAcceptancePaths
if ($Reset -and (Test-Path -LiteralPath $paths.SecretRoot -PathType Container)) {
    if (-not (Test-Path -LiteralPath $paths.Marker -PathType Leaf)) {
        throw "Refusing to reset an unmanaged directory: $($paths.SecretRoot)"
    }
    Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
    if (-not (Test-Path -LiteralPath $paths.InitialAdminPassword -PathType Leaf)) {
        $env:TERMLINK_ACCEPTANCE_ADMIN_INITIAL_PASSWORD_FILE = $paths.AdminPassword
    }
    & docker compose -f $paths.ComposeFile down --volumes --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'Could not reset the production acceptance stack.' }
    $productionPolicyEnvironment = Join-Path $paths.SecretRoot 'production-policy.env'
    foreach ($file in @(
        $paths.AdminPassword, $paths.InitialAdminPassword, $paths.InitialAdminClaimed,
        $paths.InitialAdminReady, $paths.InitialAdminLock, $paths.UserPassword, $paths.PostgresPassword,
        $paths.ValkeyPassword, $paths.OidcClientSecret, $paths.DatabaseUrl, $paths.RedisUrl,
        $paths.S3AccessKey, $paths.S3SecretKey,
        $paths.TokenSigningKey, $paths.RootCa, $paths.ProbeIdentity,
        $productionPolicyEnvironment
    )) {
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            Remove-Item -LiteralPath $file -Force
        }
    }
    $unexpectedEntries = @(Get-ChildItem -LiteralPath $paths.SecretRoot -Force | Where-Object {
        $_.FullName -cne $paths.Marker
    })
    if ($unexpectedEntries.Count -ne 0) {
        throw 'Refusing to remove a managed acceptance directory containing unexpected entries.'
    }
    Remove-Item -LiteralPath $paths.Marker -Force
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
Protect-TermLinkAcceptanceSecretTree -SecretRoot $paths.SecretRoot

Write-SecretFile $paths.AdminPassword (New-RandomText 32)
Write-SecretFile $paths.InitialAdminPassword (New-RandomText 18)
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
$valkeyPassword = if (Test-Path -LiteralPath $paths.ValkeyPassword -PathType Leaf) {
    [IO.File]::ReadAllText($paths.ValkeyPassword).Trim()
} else {
    $generated = New-RandomText 32
    Write-SecretFile $paths.ValkeyPassword $generated
    $generated
}
if ($valkeyPassword -notmatch '^[A-Za-z0-9_-]{16,128}$') {
    $valkeyPassword = $null
    throw 'The managed acceptance Valkey password file is empty or has an invalid format.'
}
try {
    Write-SecretFile $paths.RedisUrl "redis://default:$valkeyPassword@valkey:6379/0" -Force
}
finally {
    $valkeyPassword = $null
}
Write-SecretFile $paths.OidcClientSecret (New-RandomText 32)
Write-SecretFile $paths.TokenSigningKey (New-RandomText 48)
Write-SecretFile $paths.S3AccessKey (New-RandomText 24)
Write-SecretFile $paths.S3SecretKey (New-RandomText 48)
Protect-TermLinkAcceptanceSecretTree -SecretRoot $paths.SecretRoot

Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
$env:TERMLINK_ACCEPTANCE_KEYCLOAK_IMAGE = & (Join-Path $PSScriptRoot 'Import-KeycloakAcceptanceImage.ps1')
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:TERMLINK_ACCEPTANCE_KEYCLOAK_IMAGE)) {
    throw 'Could not prepare the pinned Keycloak acceptance image.'
}
# Keep the public Lucky upstream closed while the idempotent initializer
# reconciles the realm. This prevents a fresh or partially configured realm
# from exposing registration before the managed admin identity is verified.
& docker compose -f $paths.ComposeFile stop lucky-upstream
if ($LASTEXITCODE -ne 0) { throw 'Could not close the public acceptance upstream before identity initialization.' }
# Realm imports only affect an empty Keycloak data volume, so retaining an
# exited init container would otherwise leave registration and client policy
# changes unapplied on an existing deployment. Secret staging jobs must also
# rerun so rotations reach their service-private volumes.
& docker compose -f $paths.ComposeFile rm --stop --force runtime-secrets-init server-secrets-init keycloak-init
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the Keycloak policy initializer.' }
# Recreate/build the project first, then wait from a stable container set. Combining
# --build/--detach with --wait can race Compose's container replacement and leave the
# waiter holding an old container id ("No such container").
& docker compose -f $paths.ComposeFile up --build --detach
if ($LASTEXITCODE -ne 0) { throw 'Could not build or recreate the production acceptance stack.' }
& docker compose -f $paths.ComposeFile up --no-build --no-recreate --detach --wait --wait-timeout 300
if ($LASTEXITCODE -ne 0) { throw 'The production acceptance stack did not become ready.' }

function Assert-RuntimeSecretMetadata {
    param(
        [Parameter(Mandatory)][string]$Service,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected
    )
    $actual = (& docker compose -f $paths.ComposeFile exec -T $Service stat -c '%a:%u:%g' $Path).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -cne $Expected) {
        throw "Service '$Service' secret '$Path' has metadata '$actual'; expected '$Expected'."
    }
}

Assert-RuntimeSecretMetadata -Service keycloak -Path /run/secrets/keycloak_admin_password -Expected '400:1000:0'
Assert-RuntimeSecretMetadata -Service postgres -Path /run/secrets/postgres_password -Expected '400:70:70'
Assert-RuntimeSecretMetadata -Service valkey -Path /run/secrets/valkey_password -Expected '400:999:1000'
Assert-RuntimeSecretMetadata -Service minio -Path /run/secrets/s3_access_key -Expected '400:0:0'
Assert-RuntimeSecretMetadata -Service minio -Path /run/secrets/s3_secret_key -Expected '400:0:0'

if (Test-Path -LiteralPath $paths.RootCa -PathType Leaf) {
    Remove-Item -LiteralPath $paths.RootCa -Force
}
& docker compose -f $paths.ComposeFile cp edge:/data/caddy/pki/authorities/local/root.crt $paths.RootCa
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $paths.RootCa -PathType Leaf)) {
    throw 'Could not export the disposable acceptance CA certificate.'
}

$env:TERMLINK_ACCEPTANCE_ROOT_URL = 'https://api.127.0.0.1.sslip.io:9443'
$env:TERMLINK_ACCEPTANCE_OIDC_ISSUER = 'https://termlink.kget.cc:8910/realms/termlink'
$env:TERMLINK_ACCEPTANCE_CA_FILE = $paths.RootCa
$env:TERMLINK_ACCEPTANCE_USER_NAME = 'acceptance-probe'
$env:TERMLINK_ACCEPTANCE_PROBE_IDENTITY_FILE = $paths.ProbeIdentity
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

if (-not (Test-Path -LiteralPath $paths.InitialAdminReady -PathType Leaf) -and
    -not (Test-Path -LiteralPath $paths.InitialAdminClaimed -PathType Leaf)) {
    [IO.File]::WriteAllText($paths.InitialAdminReady, 'TermLink admin identity bootstrap completed', [Text.UTF8Encoding]::new($false))
}
Protect-TermLinkAcceptanceSecretTree -SecretRoot $paths.SecretRoot

Write-Output 'TermLink production acceptance stack is running.'
Write-Output 'API: https://api.127.0.0.1.sslip.io:9443'
Write-Output 'Public API/OIDC: https://termlink.kget.cc:8910'
Write-Output "CA certificate: $($paths.RootCa)"
if (-not (Test-Path -LiteralPath $paths.InitialAdminClaimed -PathType Leaf)) {
    Write-Output 'Initial admin is ready. Retrieve its temporary password once with server\ops\Get-InitialAdminPassword.ps1.'
}
