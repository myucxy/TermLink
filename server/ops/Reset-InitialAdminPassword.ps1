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

    [IO.File]::WriteAllText($paths.InitialAdminClaimed, 'reset-in-progress', [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $paths.InitialAdminReady -PathType Leaf) {
        Remove-Item -LiteralPath $paths.InitialAdminReady -Force
    }
    [IO.File]::WriteAllText($paths.InitialAdminPassword, (New-RandomText 18), [Text.UTF8Encoding]::new($false))
    Protect-TermLinkAcceptanceSecretTree -SecretRoot $paths.SecretRoot
    Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
    $embeddedCompose = Join-Path $paths.ServerRoot 'compose\acceptance.embedded-identity.compose.yml'

    # Atomically restage the new file, then execute the reset inside the exact
    # running Server container so published and local-image deployments behave
    # identically. No password is placed in process arguments or logs.
    & docker compose -f $embeddedCompose run --rm --no-deps runtime-secrets-init
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not stage the new initial administrator password.'
    }
    & docker compose -f $embeddedCompose exec -T server /termlink-server identity-reset-initial-admin
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not reset the embedded initial administrator password.'
    }

    [IO.File]::WriteAllText($paths.InitialAdminReady, 'TermLink initial administrator password reset completed', [Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $paths.InitialAdminClaimed -Force
    Protect-TermLinkAcceptanceSecretTree -SecretRoot $paths.SecretRoot
    Write-Output 'A new initial administrator password is ready. Retrieve it once with Get-InitialAdminPassword.ps1.'
}
finally {
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}
