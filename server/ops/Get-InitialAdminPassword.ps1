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
        throw 'The initial admin password was already claimed. Use Reset-InitialAdminPassword.ps1 to issue a new temporary password.'
    }
    if (-not (Test-Path -LiteralPath $paths.InitialAdminPassword -PathType Leaf)) {
        throw 'The managed initial admin secret is unavailable.'
    }
    $temporaryPassword = [IO.File]::ReadAllText($paths.InitialAdminPassword)
    if ([string]::IsNullOrWhiteSpace($temporaryPassword) -or $temporaryPassword.Length -lt 24) {
        throw 'The managed initial admin secret is invalid.'
    }

    # Claim first. If the process stops after this point, retrieval fails closed
    # and the operator must explicitly reset instead of receiving a credential
    # that was never installed in the identity provider.
    [IO.File]::WriteAllText($paths.InitialAdminClaimed, [DateTimeOffset]::UtcNow.ToString('O'), [Text.UTF8Encoding]::new($false))
    # Rotate the host-side bootstrap input before disclosing the original so a
    # normal restart cannot reveal it again. The replacement is not installed
    # as an identity credential.
    [IO.File]::WriteAllText($paths.InitialAdminPassword, (New-RandomText 32), [Text.UTF8Encoding]::new($false))
    Write-Output $temporaryPassword
}
finally {
    $temporaryPassword = $null
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}
