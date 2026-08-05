[CmdletBinding()]
param()

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

$paths = Get-TermLinkAcceptancePaths
$lockStream = $null
try {
    try {
        $lockStream = [IO.File]::Open(
            $paths.InitialAdminLock,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None
        )
    }
    catch [IO.IOException] {
        throw 'Another initial admin password operation is already running.'
    }
    if (-not (Test-Path -LiteralPath $paths.Marker -PathType Leaf)) {
        throw 'The managed production acceptance deployment is unavailable.'
    }

    # Block retrieval before changing either the host-side input or Keycloak.
    # Any interruption therefore requires an explicit retry of this reset.
    [IO.File]::WriteAllText($paths.InitialAdminClaimed, 'reset-in-progress', [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $paths.InitialAdminReady -PathType Leaf) {
        Remove-Item -LiteralPath $paths.InitialAdminReady -Force
    }
    [IO.File]::WriteAllText($paths.InitialAdminPassword, (New-RandomText 18), [Text.UTF8Encoding]::new($false))
    Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
    $env:TERMLINK_ACCEPTANCE_KEYCLOAK_IMAGE = & (Join-Path $PSScriptRoot 'Import-KeycloakAcceptanceImage.ps1')
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:TERMLINK_ACCEPTANCE_KEYCLOAK_IMAGE)) {
        throw 'Could not prepare the pinned Keycloak image.'
    }

$resetCommand = @'
admin_password="$(cat /run/secrets/keycloak_admin_password)"
temporary_password="$(cat /run/secrets/admin_initial_password)"
/opt/keycloak/bin/kcadm.sh config credentials \
  --config /tmp/kcadm.config \
  --server http://keycloak:8080 \
  --realm master \
  --user acceptance-admin \
  --password "$admin_password" >/dev/null 2>&1
admin_user_id=$(/opt/keycloak/bin/kcadm.sh get users \
  --config /tmp/kcadm.config \
  -r termlink -q exact=true -q username=admin \
  --fields id --format csv --noquotes | tail -n 1)
test -n "$admin_user_id"
/opt/keycloak/bin/kcadm.sh set-password \
  --config /tmp/kcadm.config \
  -r termlink --userid "$admin_user_id" \
  --new-password "$temporary_password" --temporary >/dev/null
'@

    & docker compose -f $paths.ComposeFile run --rm --no-deps --entrypoint /bin/bash keycloak-init -ec $resetCommand
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not issue a new initial admin temporary password.'
    }

    [IO.File]::WriteAllText($paths.InitialAdminReady, 'TermLink admin temporary password reset completed', [Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $paths.InitialAdminClaimed -Force
    Write-Output 'A new temporary password is ready. Retrieve it once with Get-InitialAdminPassword.ps1.'
}
finally {
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}
