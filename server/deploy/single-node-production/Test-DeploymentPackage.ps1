[CmdletBinding()]
param(
    [string]$BashPath = 'D:\Dev\Env\Git\bin\bash.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Test {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Set-PrivateFixtureAcl {
    param([string]$Path)
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    foreach ($identity in @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

$packagePath = $PSScriptRoot
$serverPath = [IO.Path]::GetFullPath((Join-Path $packagePath '..\..'))
$policyPath = Join-Path $serverPath 'ops\Test-ProductionDeployment.ps1'

Assert-Test (Test-Path -LiteralPath $BashPath -PathType Leaf) "Bash not found: $BashPath"
foreach ($scriptName in @('termlink-deploy', 'keycloak-reconcile.sh', 'postgres-init.sh')) {
    & $BashPath -n (Join-Path $packagePath $scriptName)
    Assert-Test ($LASTEXITCODE -eq 0) "Bash syntax validation failed: $scriptName"
}
$help = & $BashPath (Join-Path $packagePath 'termlink-deploy') --help
$helpText = $help -join "`n"
Assert-Test ($LASTEXITCODE -eq 0 -and $helpText.Contains('termlink-deploy install') -and
    $helpText.Contains('termlink-deploy upgrade') -and $helpText.Contains('termlink-deploy doctor')) `
    'Deployment help did not render the command contract.'

$realm = Get-Content -LiteralPath (Join-Path $packagePath 'keycloak-realm.json') -Raw |
    ConvertFrom-Json -Depth 100
Get-Content -LiteralPath (Join-Path $packagePath 'keycloak-user-profile.json') -Raw |
    ConvertFrom-Json -Depth 100 | Out-Null
$ordinaryUsers = @($realm.users | Where-Object { [string]$_.serviceAccountClientId -eq '' })
Assert-Test ($ordinaryUsers.Count -eq 0) 'Production realm contains an ordinary or acceptance user.'
$clientIDs = @($realm.clients | ForEach-Object { [string]$_.clientId })
Assert-Test ($clientIDs -notcontains 'termlink-acceptance-cli') 'Production realm contains the acceptance client.'

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixturePath = Join-Path $tempRoot ("termlink-single-node-policy-" + [Guid]::NewGuid().ToString('N'))
$fixturePath = [IO.Path]::GetFullPath($fixturePath)
Assert-Test ($fixturePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) `
    'Disposable fixture escaped the operating-system temp directory.'

try {
    $runtimePath = Join-Path $fixturePath 'runtime'
    $secretPath = Join-Path $fixturePath 'secrets'
    New-Item -ItemType Directory -Path $fixturePath -Force | Out-Null
    Set-PrivateFixtureAcl -Path $fixturePath
    New-Item -ItemType Directory -Path $runtimePath, $secretPath -Force | Out-Null
    foreach ($name in @(
        'compose.yml', 'Caddyfile', 'Keycloak.Dockerfile', 'keycloak-realm.json',
        'keycloak-user-profile.json', 'keycloak-reconcile.sh', 'postgres-init.sh'
    )) {
        Copy-Item -LiteralPath (Join-Path $packagePath $name) -Destination (Join-Path $runtimePath $name)
    }

    $postgresPassword = 'Postgres_Test_7Pz4mN8vQ2xK6cR9sT5yH3jF'
    $secrets = [ordered]@{
        'postgres-password' = $postgresPassword
        'keycloak-db-password' = 'Keycloak_DB_Test_4kQ8nD7pM3sV6xR2cH9j'
        'keycloak-bootstrap-admin-password' = 'Bootstrap_Test_5mR8kN2vP7xQ4cD9sH6j'
        'initial-admin-password' = 'Initial_Admin_Test_8pQ4mN7vR2xK6cD9'
        'keycloak-admin-client-secret' = 'Recovery_Test_6vN3pQ8mR2xK7cD9sH4j'
        'oidc-client-secret' = 'OIDC_Test_4mN8pQ2vR7xK6cD9sH3jF5yT'
        'database-url' = "postgres://termlink:${postgresPassword}@postgres:5432/termlink?sslmode=disable"
        'token-signing-key' = 'Token_Signing_Test_7pQ3mN8vR2xK6cD9sH4jF5yT1wB'
        'account-deletion-identity-key' = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
    }
    foreach ($entry in $secrets.GetEnumerator()) {
        [IO.File]::WriteAllText((Join-Path $secretPath $entry.Key), $entry.Value,
            [Text.UTF8Encoding]::new($false))
    }

    $runtimeComposePath = $runtimePath.Replace('\', '/')
    $secretComposePath = $secretPath.Replace('\', '/')
    $image = 'docker.io/myucxy/termlink:v0.2.38@sha256:' + ('a' * 64)
    $envPath = Join-Path $fixturePath 'config.env'
    $environment = @(
        'TERMLINK_CONFIG_VERSION=1'
        'COMPOSE_PROJECT_NAME=termlink'
        "TERMLINK_IMAGE=$image"
        "TERMLINK_RUNTIME_DIR=$runtimeComposePath"
        "TERMLINK_SECRETS_DIR=$secretComposePath"
        'TERMLINK_PUBLIC_HOST=deploy.termlink.example.cn'
        'TERMLINK_PUBLIC_URL=https://deploy.termlink.example.cn'
        'TERMLINK_ADMIN_EMAIL=admin@termlink.example.cn'
        'TERMLINK_TLS_MODE=managed'
        'TERMLINK_HTTP_BIND_IP=127.0.0.1'
        'TERMLINK_HTTP_PORT=18080'
        'TERMLINK_HTTPS_BIND_IP=0.0.0.0'
        'TERMLINK_HTTPS_PORT=18443'
        'TERMLINK_BACKUP_RECIPIENT='
    )
    [IO.File]::WriteAllLines($envPath, $environment, [Text.UTF8Encoding]::new($false))

    & $policyPath -EnvFile $envPath -ComposeFile (Join-Path $packagePath 'compose.yml') -IncludeEdge $true
    Assert-Test ($LASTEXITCODE -eq 0) 'Single-node production policy validation failed.'
}
finally {
    if (Test-Path -LiteralPath $fixturePath) {
        $resolvedFixture = [IO.Path]::GetFullPath($fixturePath)
        Assert-Test ($resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) `
            'Refusing to remove a fixture outside the operating-system temp directory.'
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}

Write-Host 'TermLink single-node deployment package tests passed.'
