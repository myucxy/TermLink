Set-StrictMode -Version Latest

function Get-TermLinkAcceptancePaths {
    $serverRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $secretRoot = Join-Path $serverRoot 'secrets\acceptance'
    [pscustomobject]@{
        ServerRoot = $serverRoot
        ComposeFile = Join-Path $serverRoot 'compose\acceptance.compose.yml'
        SecretRoot = $secretRoot
        Marker = Join-Path $secretRoot '.managed-by-termlink-production-acceptance'
        AdminPassword = Join-Path $secretRoot 'keycloak-admin-password'
        InitialAdminPassword = Join-Path $secretRoot 'admin-initial-password'
        InitialAdminClaimed = Join-Path $secretRoot '.admin-initial-password-claimed'
        InitialAdminReady = Join-Path $secretRoot '.admin-initial-password-ready'
        InitialAdminLock = Join-Path $secretRoot '.admin-initial-password.lock'
        UserPassword = Join-Path $secretRoot 'acceptance-user-password'
        PostgresPassword = Join-Path $secretRoot 'postgres-password'
        OidcClientSecret = Join-Path $secretRoot 'oidc-client-secret'
        DatabaseUrl = Join-Path $secretRoot 'database-url'
        RedisUrl = Join-Path $secretRoot 'redis-url'
        TokenSigningKey = Join-Path $secretRoot 'token-signing-key'
        RootCa = Join-Path $secretRoot 'acceptance-root-ca.crt'
        ProbeIdentity = Join-Path $secretRoot 'production-probe-pc.json'
    }
}

function Set-TermLinkAcceptanceComposeEnvironment {
    param([Parameter(Mandatory)]$Paths)

    $env:TERMLINK_ACCEPTANCE_KEYCLOAK_ADMIN_PASSWORD_FILE = $Paths.AdminPassword
    $env:TERMLINK_ACCEPTANCE_ADMIN_INITIAL_PASSWORD_FILE = $Paths.InitialAdminPassword
    $env:TERMLINK_ACCEPTANCE_USER_PASSWORD_FILE = $Paths.UserPassword
    $env:TERMLINK_ACCEPTANCE_POSTGRES_PASSWORD_FILE = $Paths.PostgresPassword
    $env:TERMLINK_ACCEPTANCE_OIDC_CLIENT_SECRET_FILE = $Paths.OidcClientSecret
    $env:TERMLINK_ACCEPTANCE_DATABASE_URL_FILE = $Paths.DatabaseUrl
    $env:TERMLINK_ACCEPTANCE_REDIS_URL_FILE = $Paths.RedisUrl
    $env:TERMLINK_ACCEPTANCE_TOKEN_SIGNING_KEY_FILE = $Paths.TokenSigningKey
}

Export-ModuleMember -Function Get-TermLinkAcceptancePaths, Set-TermLinkAcceptanceComposeEnvironment
