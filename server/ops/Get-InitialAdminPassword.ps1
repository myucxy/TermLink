[CmdletBinding()]
param()

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

$paths = Get-TermLinkAcceptancePaths
$lockStream = $null
$temporaryPassword = $null
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
    if (-not (Test-Path -LiteralPath $paths.InitialAdminReady -PathType Leaf)) {
        throw 'The initial admin is not ready. Complete Start-ProductionAcceptance.ps1 first.'
    }
    if (Test-Path -LiteralPath $paths.InitialAdminClaimed -PathType Leaf) {
        throw 'The initial admin password was already claimed. Use Reset-InitialAdminPassword.ps1 to issue a new password.'
    }
    if (-not (Test-Path -LiteralPath $paths.InitialAdminPassword -PathType Leaf)) {
        throw 'The managed initial admin secret is unavailable.'
    }
    $temporaryPassword = [IO.File]::ReadAllText($paths.InitialAdminPassword)
    if ([string]::IsNullOrWhiteSpace($temporaryPassword) -or $temporaryPassword.Length -lt 24) {
        throw 'The managed initial admin secret is invalid.'
    }

    # Claim first. If the process stops after this point, retrieval fails closed
    # and the operator must explicitly reset the credential.
    [IO.File]::WriteAllText($paths.InitialAdminClaimed, [DateTimeOffset]::UtcNow.ToString('O'), [Text.UTF8Encoding]::new($false))
    # Rotate both the host input and the service-private bootstrap file before
    # disclosing the original. The embedded account retains only its Argon2id
    # digest; the replacement value is never installed as a credential.
    [IO.File]::WriteAllText($paths.InitialAdminPassword, (New-RandomText 32), [Text.UTF8Encoding]::new($false))
    Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
    $embeddedCompose = Join-Path $paths.ServerRoot 'compose\acceptance.embedded-identity.compose.yml'
    & docker compose -f $embeddedCompose run --rm --no-deps runtime-secrets-init
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not scrub the staged initial administrator secret.'
    }
    Write-Output $temporaryPassword
}
finally {
    $temporaryPassword = $null
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}
