Set-StrictMode -Version Latest

function Set-TermLinkSecretPathPermissions {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$Directory
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Cannot protect missing secret path '$Path'."
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        $mode = if ($Directory) {
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
        }
        else {
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
        }
        [IO.File]::SetUnixFileMode($Path, $mode)
        return
    }

    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) {
        [void]$acl.RemoveAccessRuleSpecific($rule)
    }
    $identities = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )
    foreach ($identity in $identities) {
        $accessRule = if ($Directory) {
            [Security.AccessControl.FileSystemAccessRule]::new(
                $identity,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
                    [Security.AccessControl.InheritanceFlags]::ObjectInherit,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow
            )
        }
        else {
            [Security.AccessControl.FileSystemAccessRule]::new(
                $identity,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.AccessControlType]::Allow
            )
        }
        [void]$acl.AddAccessRule($accessRule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Protect-TermLinkAcceptanceSecretTree {
    param([Parameter(Mandatory)][string]$SecretRoot)

    if (-not (Test-Path -LiteralPath $SecretRoot -PathType Container)) {
        throw "Acceptance secret directory '$SecretRoot' does not exist."
    }
    $rootItem = Get-Item -LiteralPath $SecretRoot -Force
    if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to protect reparse-point secret directory '$SecretRoot'."
    }
    Set-TermLinkSecretPathPermissions -Path $SecretRoot -Directory $true
    foreach ($item in @(Get-ChildItem -LiteralPath $SecretRoot -Force -File)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to use reparse-point secret file '$($item.FullName)'."
        }
        Set-TermLinkSecretPathPermissions -Path $item.FullName -Directory $false
    }
}

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
        ValkeyPassword = Join-Path $secretRoot 'valkey-password'
        OidcClientSecret = Join-Path $secretRoot 'oidc-client-secret'
        DatabaseUrl = Join-Path $secretRoot 'database-url'
        RedisUrl = Join-Path $secretRoot 'redis-url'
        DirectValkeyPassword = Join-Path $secretRoot 'direct-valkey-password'
        DirectRedisUrl = Join-Path $secretRoot 'direct-redis-url'
        TokenSigningKey = Join-Path $secretRoot 'token-signing-key'
        RootCa = Join-Path $secretRoot 'acceptance-root-ca.crt'
        ProbeIdentity = Join-Path $secretRoot 'production-probe-pc.json'
    }
}

function Set-TermLinkAcceptanceDirectComposeEnvironment {
    param(
        [Parameter(Mandatory)]$Paths,
        [Parameter(Mandatory)][string]$STUNServers
    )

    $env:TERMLINK_DIRECT_REDIS_URL_FILE = $Paths.DirectRedisUrl
    $env:TERMLINK_DIRECT_VALKEY_PASSWORD_FILE = $Paths.DirectValkeyPassword
    $env:TERMLINK_DIRECT_STUN_SERVERS = $STUNServers
}

function Set-TermLinkAcceptanceComposeEnvironment {
    param([Parameter(Mandatory)]$Paths)

    $env:TERMLINK_ACCEPTANCE_KEYCLOAK_ADMIN_PASSWORD_FILE = $Paths.AdminPassword
    $env:TERMLINK_ACCEPTANCE_ADMIN_INITIAL_PASSWORD_FILE = $Paths.InitialAdminPassword
    $env:TERMLINK_ACCEPTANCE_USER_PASSWORD_FILE = $Paths.UserPassword
    $env:TERMLINK_ACCEPTANCE_POSTGRES_PASSWORD_FILE = $Paths.PostgresPassword
    $env:TERMLINK_ACCEPTANCE_VALKEY_PASSWORD_FILE = $Paths.ValkeyPassword
    $env:TERMLINK_ACCEPTANCE_OIDC_CLIENT_SECRET_FILE = $Paths.OidcClientSecret
    $env:TERMLINK_ACCEPTANCE_DATABASE_URL_FILE = $Paths.DatabaseUrl
    $env:TERMLINK_ACCEPTANCE_REDIS_URL_FILE = $Paths.RedisUrl
    $env:TERMLINK_ACCEPTANCE_TOKEN_SIGNING_KEY_FILE = $Paths.TokenSigningKey
}

Export-ModuleMember -Function Get-TermLinkAcceptancePaths, Set-TermLinkAcceptanceComposeEnvironment, Set-TermLinkAcceptanceDirectComposeEnvironment, Protect-TermLinkAcceptanceSecretTree
