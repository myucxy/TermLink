[CmdletBinding()]
param(
    [string]$ServerImage = 'myucxy/termlink:0.2.37-local-production',
    [switch]$KeepOnFailure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Docker {
    param([string[]]$Arguments, [switch]$AllowFailure)
    $output = @(& docker @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "docker failed ($exitCode): $($output -join ' ')"
    }
    [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function New-UrlSafeSecret {
    param([int]$ByteCount = 32)
    $bytes = [byte[]]::new($ByteCount)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    try { return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Wait-HttpReady {
    param([string]$Uri, [int]$Seconds = 60)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { return $response }
        }
        catch { Start-Sleep -Milliseconds 500 }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "HTTP endpoint did not become ready: $Uri"
}

$packagePath = $PSScriptRoot
$serverPath = [IO.Path]::GetFullPath((Join-Path $packagePath '..\..'))
$project = "termlink-deploy-e2e-$PID"
if ($project -notmatch '^termlink-deploy-e2e-[0-9]+$') { throw 'Unsafe E2E project name.' }
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $project))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $fixtureRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'E2E fixture escaped the operating-system temp directory.'
}
$runtimePath = Join-Path $fixtureRoot 'runtime'
$secretPath = Join-Path $fixtureRoot 'secrets'
$envPath = Join-Path $fixtureRoot 'config.env'
$httpPort = Get-FreeTcpPort
$publicHost = 'deploy-e2e.termlink.local'
$publicURL = "https://$publicHost"
$composePath = Join-Path $runtimePath 'compose.yml'
$composeArguments = @('compose', '--project-name', $project, '--env-file', $envPath, '-f', $composePath)
$started = $false
$succeeded = $false

try {
    Invoke-Docker -Arguments @('image', 'inspect', $ServerImage) | Out-Null
    New-Item -ItemType Directory -Path $runtimePath, $secretPath -Force | Out-Null
    foreach ($name in @(
        'compose.yml', 'Caddyfile', 'Keycloak.Dockerfile', 'keycloak-realm.json',
        'keycloak-user-profile.json', 'keycloak-reconcile.sh', 'postgres-init.sh'
    )) {
        Copy-Item -LiteralPath (Join-Path $packagePath $name) -Destination (Join-Path $runtimePath $name)
    }
    Copy-Item -LiteralPath (Join-Path $serverPath 'acceptance\keycloak-theme\termlink') `
        -Destination (Join-Path $runtimePath 'keycloak-theme') -Recurse
    foreach ($name in @('identity.css', 'termlink-app-icon.svg', 'termlink-favicon.svg')) {
        Copy-Item -LiteralPath (Join-Path $serverPath "internal\webportal\assets\$name") `
            -Destination (Join-Path $runtimePath $name)
    }

    $postgresPassword = New-UrlSafeSecret
    $secrets = [ordered]@{
        'postgres-password' = $postgresPassword
        'keycloak-db-password' = (New-UrlSafeSecret)
        'keycloak-bootstrap-admin-password' = (New-UrlSafeSecret)
        'initial-admin-password' = (New-UrlSafeSecret -ByteCount 24)
        'keycloak-admin-client-secret' = (New-UrlSafeSecret)
        'oidc-client-secret' = (New-UrlSafeSecret)
        'database-url' = "postgres://termlink:${postgresPassword}@postgres:5432/termlink?sslmode=disable"
        'token-signing-key' = (New-UrlSafeSecret -ByteCount 48)
        'account-deletion-identity-key' = (New-UrlSafeSecret)
    }
    foreach ($entry in $secrets.GetEnumerator()) {
        [IO.File]::WriteAllText((Join-Path $secretPath $entry.Key), $entry.Value,
            [Text.UTF8Encoding]::new($false))
    }

    $runtimeComposePath = $runtimePath.Replace('\', '/')
    $secretComposePath = $secretPath.Replace('\', '/')
    $keycloakImage = "$project-keycloak:local"
    [IO.File]::WriteAllLines($envPath, @(
        'TERMLINK_CONFIG_VERSION=1'
        "COMPOSE_PROJECT_NAME=$project"
        "TERMLINK_IMAGE=$ServerImage"
        "TERMLINK_KEYCLOAK_IMAGE=$keycloakImage"
        "TERMLINK_RUNTIME_DIR=$runtimeComposePath"
        "TERMLINK_SECRETS_DIR=$secretComposePath"
        "TERMLINK_PUBLIC_HOST=$publicHost"
        "TERMLINK_PUBLIC_URL=$publicURL"
        'TERMLINK_ADMIN_EMAIL=admin@termlink.local'
        'TERMLINK_TLS_MODE=external'
        'TERMLINK_HTTP_BIND_IP=127.0.0.1'
        "TERMLINK_HTTP_PORT=$httpPort"
        'TERMLINK_HTTPS_BIND_IP=127.0.0.1'
        'TERMLINK_HTTPS_PORT=443'
        'TERMLINK_BACKUP_RECIPIENT='
    ), [Text.UTF8Encoding]::new($false))

    Invoke-Docker -Arguments ($composeArguments + @('config', '--quiet')) | Out-Null
    $started = $true
    Invoke-Docker -Arguments ($composeArguments + @(
        'up', '-d', '--build', '--wait', '--wait-timeout', '420', '--scale', 'server=1'
    )) | Out-Null

    $running = (Invoke-Docker -Arguments ($composeArguments + @('ps', '--services', '--filter', 'status=running'))).Output
    foreach ($service in @('postgres', 'keycloak', 'server')) {
        if (@($running | Where-Object { $_.ToString().Trim() -ceq $service }).Count -ne 1) {
            throw "E2E service is not running exactly once: $service"
        }
    }
    if ($running -contains 'valkey' -or $running -contains 'minio' -or $running -contains 'migrate') {
        throw 'E2E topology contains a retired service.'
    }

    Wait-HttpReady -Uri "http://127.0.0.1:$httpPort/healthz" | Out-Null
    Wait-HttpReady -Uri "http://127.0.0.1:$httpPort/readyz" | Out-Null
    $discovery = Wait-HttpReady -Uri "http://127.0.0.1:$httpPort/realms/termlink/.well-known/openid-configuration"
    if (-not $discovery.Content.Contains("$publicURL/realms/termlink", [StringComparison]::Ordinal)) {
        throw 'OIDC discovery did not preserve the configured public issuer.'
    }
    $registration = Invoke-WebRequest -Uri "http://127.0.0.1:$httpPort/register" -UseBasicParsing -TimeoutSec 10
    if ($registration.StatusCode -ne 200) { throw 'Registration page is unavailable.' }

    $migration = (Invoke-Docker -Arguments ($composeArguments + @(
        'exec', '-T', 'postgres', 'psql', '-U', 'termlink', '-d', 'termlink',
        '-Atqc', 'SELECT MAX(version) FROM schema_migrations'
    ))).Output[-1].ToString().Trim()
    if ($migration -ne '42') { throw "Unexpected migration version: $migration" }
    foreach ($database in @('termlink', 'keycloak')) {
        $exists = (Invoke-Docker -Arguments ($composeArguments + @(
            'exec', '-T', 'postgres', 'psql', '-U', 'termlink', '-d', 'postgres',
            '-Atqc', "SELECT 1 FROM pg_database WHERE datname='$database'"
        ))).Output[-1].ToString().Trim()
        if ($exists -ne '1') { throw "Database is missing: $database" }
    }

    $identityCheck = @'
password="$(cat /run/secrets/keycloak_bootstrap_admin_password)"
/opt/keycloak/bin/kcadm.sh config credentials --config /tmp/e2e-kcadm.config \
  --server http://127.0.0.1:8080 --realm master \
  --user termlink-bootstrap --password "$password" >/dev/null
/opt/keycloak/bin/kcadm.sh get users --config /tmp/e2e-kcadm.config \
  -r termlink -q exact=true -q username=admin --fields username --format csv --noquotes | grep -Fxq admin
/opt/keycloak/bin/kcadm.sh get clients --config /tmp/e2e-kcadm.config \
  -r termlink -q clientId=termlink-registration-recovery --fields id --format csv --noquotes | grep -q .
rm -f /tmp/e2e-kcadm.config
'@
    Invoke-Docker -Arguments ($composeArguments + @(
        'exec', '-T', 'keycloak', '/bin/bash', '-ec', $identityCheck
    )) | Out-Null

    Invoke-Docker -Arguments ($composeArguments + @('restart', 'server')) | Out-Null
    Wait-HttpReady -Uri "http://127.0.0.1:$httpPort/readyz" -Seconds 90 | Out-Null

    Invoke-Docker -Arguments ($composeArguments + @('rm', '-f', 'keycloak-init', 'secrets-init')) -AllowFailure | Out-Null
    Invoke-Docker -Arguments ($composeArguments + @(
        'up', '-d', '--wait', '--wait-timeout', '180', 'secrets-init', 'keycloak-init', 'server'
    )) | Out-Null
    Wait-HttpReady -Uri "http://127.0.0.1:$httpPort/readyz" -Seconds 90 | Out-Null
    $succeeded = $true
    Write-Host "TermLink deployment E2E passed (migration $migration, port $httpPort)."
}
finally {
    if ($started -and ($succeeded -or -not $KeepOnFailure)) {
        Invoke-Docker -Arguments ($composeArguments + @('down', '--volumes', '--remove-orphans')) -AllowFailure | Out-Null
    }
    if (($succeeded -or -not $KeepOnFailure) -and (Test-Path -LiteralPath $fixtureRoot)) {
        $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
        if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove E2E files outside the operating-system temp directory.'
        }
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
    if (-not $succeeded -and $KeepOnFailure) {
        Write-Warning "E2E resources retained for diagnosis: project=$project fixture=$fixtureRoot"
    }
}
