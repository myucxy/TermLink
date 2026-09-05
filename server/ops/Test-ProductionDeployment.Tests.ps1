[CmdletBinding()]
param(
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $PSScriptRoot 'Test-ProductionDeployment.ps1'
}

function Set-TestSecretPermissions {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][bool]$Directory)

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        $mode = if ($Directory) {
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
        } else {
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
        }
        [IO.File]::SetUnixFileMode($Path, $mode)
        return
    }

    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    if ($Directory) {
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        foreach ($sid in @($currentUser, $system, $administrators)) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow)
            [void]$security.AddAccessRule($rule)
        }
    } else {
        $security = [Security.AccessControl.FileSecurity]::new()
        foreach ($sid in @($currentUser, $system, $administrators)) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.AccessControlType]::Allow)
            [void]$security.AddAccessRule($rule)
        }
    }
    $security.SetAccessRuleProtection($true, $false)
    Set-Acl -LiteralPath $Path -AclObject $security
}

function ConvertTo-ComposeHostPath {
    param([Parameter(Mandatory)][string]$Path)
    return [IO.Path]::GetFullPath($Path).Replace('\', '/')
}

function Test-ComposeConfig {
    param(
        [Parameter(Mandatory)][string]$EnvironmentFile,
        [Parameter(Mandatory)][string[]]$Files,
        [bool]$IncludeEdge = $false
    )

    $arguments = @('compose', '--env-file', $EnvironmentFile, '--project-name', 'termlink-matrix-test')
    foreach ($file in $Files) { $arguments += @('-f', $file) }
    if ($IncludeEdge) { $arguments += @('--profile', 'edge') }
    $arguments += @('config', '--quiet')
    $output = @(& docker @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Compose matrix render failed: $($output -join ' ')"
    }
}

function Get-ComposeConfig {
    param(
        [Parameter(Mandatory)][string]$EnvironmentFile,
        [Parameter(Mandatory)][string[]]$Files
    )

    $arguments = @('compose', '--env-file', $EnvironmentFile, '--project-name', 'termlink-topology-test')
    foreach ($file in $Files) { $arguments += @('-f', $file) }
    $arguments += @('config', '--format', 'json')
    $output = @(& docker @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Compose topology render failed: $($output -join ' ')"
    }
    return (($output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100)
}

function Assert-ComposeConfigRejected {
    param(
        [Parameter(Mandatory)][string]$EnvironmentFile,
        [Parameter(Mandatory)][string[]]$Files,
        [Parameter(Mandatory)][string]$ExpectedPattern
    )

    $arguments = @('compose', '--env-file', $EnvironmentFile, '--project-name', 'termlink-negative-test')
    foreach ($file in $Files) { $arguments += @('-f', $file) }
    $arguments += @('config', '--quiet')
    $output = @(& docker @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0 -or ($output -join ' ') -notmatch $ExpectedPattern) {
        throw "Compose did not fail closed with '$ExpectedPattern': exit=$exitCode output=$($output -join ' ')"
    }
}

function New-FilteredEnvironmentFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string[]]$RemovePrefixes
    )

    $result = foreach ($line in [IO.File]::ReadAllLines($Source)) {
        $remove = $false
        foreach ($prefix in $RemovePrefixes) {
            if ($line.StartsWith($prefix, [StringComparison]::Ordinal)) {
                $remove = $true
                break
            }
        }
        if (-not $remove) { $line }
    }
    [IO.File]::WriteAllLines($Destination, @($result), [Text.UTF8Encoding]::new($false))
    return $Destination
}

function Assert-PolicyRejected {
    param(
        [Parameter(Mandatory)][string]$Policy,
        [Parameter(Mandatory)][string]$EnvironmentFile,
        [Parameter(Mandatory)][string[]]$Files,
        [Parameter(Mandatory)][string]$ExpectedPattern,
        [string[]]$ForbiddenText = @(),
        [bool]$IncludeEdge = $false
    )

    $rejected = $false
    try {
        & $Policy -EnvFile $EnvironmentFile -ComposeFile $Files -IncludeEdge:$IncludeEdge | Out-Null
    }
    catch {
        $message = $_.Exception.Message
        $rejected = $message -match $ExpectedPattern
        foreach ($forbidden in $ForbiddenText) {
            if (-not [string]::IsNullOrEmpty($forbidden) -and
                $message.Contains($forbidden, [StringComparison]::Ordinal)) {
                throw 'Production policy error disclosed account deletion key material.'
            }
        }
    }
    if (-not $rejected) {
        throw "Production policy did not reject configuration with '$ExpectedPattern'."
    }
}

function New-EnvironmentOverrideFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][hashtable]$Overrides
    )

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $lines = foreach ($line in [IO.File]::ReadAllLines($Source)) {
        $separator = $line.IndexOf('=')
        $name = if ($separator -gt 0) { $line.Substring(0, $separator) } else { '' }
        if ($name -ne '' -and $Overrides.ContainsKey($name)) {
            [void]$seen.Add($name)
            "$name=$($Overrides[$name])"
        } else { $line }
    }
    foreach ($name in @($Overrides.Keys | Sort-Object)) {
        if (-not $seen.Contains([string]$name)) { "$name=$($Overrides[$name])" }
    }
    [IO.File]::WriteAllLines($Destination, @($lines), [Text.UTF8Encoding]::new($false))
    return $Destination
}

function New-ProtectedTestFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Value
    )

    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
    Set-TestSecretPermissions -Path $Path -Directory $false
    return $Path
}

function Test-ProductionDockerfileContract {
    param([Parameter(Mandatory)][string]$ServerRoot)

    $dockerfile = [IO.File]::ReadAllText((Join-Path $ServerRoot 'Dockerfile'))
    foreach ($required in @(
        'ARG SOURCE_DATE_EPOCH=1',
        'ENV GOTOOLCHAIN=local',
        'go mod download && go mod verify',
        '-mod=readonly',
        '-buildvcs=false',
        'test -n "${SOURCE_DATE_EPOCH}"',
        'touch -d "@${SOURCE_DATE_EPOCH}" /out/termlink-server'
    )) {
        if (-not $dockerfile.Contains($required, [StringComparison]::Ordinal)) {
            throw "Production Dockerfile omitted reproducibility contract: $required"
        }
    }
    $ignore = [IO.File]::ReadAllText((Join-Path $ServerRoot 'Dockerfile.dockerignore'))
    foreach ($required in @('**', '!server/go.mod', '!server/go.sum', '!server/cmd/**', '!server/internal/**', '!server/migrations/**')) {
        if (-not $ignore.Contains($required, [StringComparison]::Ordinal)) {
            throw "Production Docker context allow-list omitted: $required"
        }
    }
    foreach ($forbidden in @('!android/', '!win-agent/', '!pc-client/', '!artifacts/', '!.git/')) {
        if ($ignore.Contains($forbidden, [StringComparison]::Ordinal)) {
            throw "Production Docker context unexpectedly includes: $forbidden"
        }
    }
}

if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker CLI is required for production deployment policy tests.'
}

$policy = [IO.Path]::GetFullPath($PolicyPath)
$serverRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$composeRoot = Join-Path $serverRoot 'compose'
Test-ProductionDockerfileContract -ServerRoot $serverRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("termlink-production-policy-{0}" -f [guid]::NewGuid().ToString('N'))
$secretRoot = Join-Path $testRoot 'secrets'
[void][IO.Directory]::CreateDirectory($testRoot)
[void][IO.Directory]::CreateDirectory($secretRoot)
$identityKeyBytes = $null
$identityKey = $null
$rotatedIdentityKeyBytes = $null
$rotatedIdentityKey = $null
$thirdIdentityKeyBytes = $null
$thirdIdentityKey = $null

try {
    Set-TestSecretPermissions -Path $testRoot -Directory $true
    Set-TestSecretPermissions -Path $secretRoot -Directory $true

    $valkeyPassword = 'Valkey_Test_7Pz4mN8vQ2xK6cR9'
    $directValkeyPassword = 'DirectValkey_Test_8Qm4xN7pR2vK6sL9'
    $identityKeyBytes = [byte[]](0..31)
    $identityKey = [Convert]::ToBase64String($identityKeyBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $secretFixtures = [ordered]@{
        'keycloak-admin-password' = 'Keycloak_Admin_Test_8mP4qV7nR2xK'
        'admin-initial-password' = 'Initial_Admin_Test_6qN2vK9mR4xP'
        'acceptance-user-password' = 'Acceptance_User_Test_7pL3xV8mQ2nR'
        'oidc-client-secret' = 'Oidc_Test_3mJ8pQ6sV2wX9kL5'
        'database-url' = 'postgres://termlink:Db_Test_4xQ8mV2pL7nK@postgres:5432/termlink?sslmode=require'
        'redis-url' = "redis://default:$valkeyPassword@valkey:6379/0"
        'token-signing-key' = 'Token_Test_6vN2qR9mX4pK8sJ3wL7cD5hF1aB0eGzY'
        'postgres-password' = 'Postgres_Test_9qL3vK7mX2pR8sN6'
        'valkey-password' = $valkeyPassword
        'direct-redis-url' = "redis://termlink-direct:$directValkeyPassword@direct-valkey:6379/0"
        'direct-valkey-password' = $directValkeyPassword
        'grafana-admin-password' = 'Grafana_Test_2pV7mQ9xK4nR8sL6'
        'keycloak-admin-client-secret' = 'Keycloak_Test_4nR8vM2qL7pX5sK9'
        'human-verification-secret' = 'Turnstile_Test_5qN9vK3mR8xL2pW7'
        'account-deletion-identity-key' = $identityKey
    }
    foreach ($fixture in $secretFixtures.GetEnumerator()) {
        $fixturePath = Join-Path $secretRoot $fixture.Key
        [IO.File]::WriteAllText($fixturePath, $fixture.Value, [Text.UTF8Encoding]::new($false))
        Set-TestSecretPermissions -Path $fixturePath -Directory $false
    }

    $envFile = Join-Path $testRoot 'production.env'
    $alertmanagerConfig = Join-Path $testRoot 'alertmanager.yml'
    [IO.File]::WriteAllText($alertmanagerConfig, @'
global:
  resolve_timeout: 5m
route:
  receiver: production-operations
receivers:
  - name: production-operations
    webhook_configs:
      - url: https://alerts.termlink.prod/alertmanager
'@, [Text.UTF8Encoding]::new($false))
    Set-TestSecretPermissions -Path $alertmanagerConfig -Directory $false
    $environmentLines = @(
        'COMPOSE_PROJECT_NAME=termlink-policy-test',
        'TERMLINK_IMAGE=docker.io/myucxy/termlink:policy-test@sha256:1111111111111111111111111111111111111111111111111111111111111111',
        'TERMLINK_PUBLIC_URL=https://app.termlink.prod',
        'TERMLINK_PUBLIC_HOST=app.termlink.prod',
        'TERMLINK_ACME_CA=https://acme-v02.api.letsencrypt.org/directory',
        'TERMLINK_ACME_EMAIL=operations@termlink.prod',
        'TERMLINK_OIDC_ISSUER=https://identity.termlink.prod/realms/termlink',
        'TERMLINK_OIDC_CLIENT_ID=termlink-server',
        "TERMLINK_OIDC_CLIENT_SECRET_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'oidc-client-secret'))",
        "TERMLINK_DATABASE_URL_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'database-url'))",
        "TERMLINK_REDIS_URL_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'redis-url'))",
        "TERMLINK_TOKEN_SIGNING_KEY_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'token-signing-key'))",
        "TERMLINK_POSTGRES_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'postgres-password'))",
        "TERMLINK_VALKEY_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'valkey-password'))",
        "TERMLINK_DIRECT_REDIS_URL_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'direct-redis-url'))",
        "TERMLINK_DIRECT_VALKEY_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'direct-valkey-password'))",
        'TERMLINK_DIRECT_STUN_SERVERS=stun:stun.cloudflare.com:3478,stun:stun.l.google.com:19302',
        "TERMLINK_GRAFANA_ADMIN_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'grafana-admin-password'))",
        "TERMLINK_ALERTMANAGER_CONFIG_FILE=$(ConvertTo-ComposeHostPath $alertmanagerConfig)",
        "TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'keycloak-admin-client-secret'))",
        'TERMLINK_HUMAN_VERIFICATION_SITE_KEY=0x4AAAA-production-test-site-key',
        "TERMLINK_HUMAN_VERIFICATION_SECRET_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'human-verification-secret'))",
        "TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'account-deletion-identity-key'))",
        'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION=1',
        "TERMLINK_ACCEPTANCE_KEYCLOAK_ADMIN_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'keycloak-admin-password'))",
        "TERMLINK_ACCEPTANCE_ADMIN_INITIAL_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'admin-initial-password'))",
        "TERMLINK_ACCEPTANCE_USER_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'acceptance-user-password'))",
        "TERMLINK_ACCEPTANCE_POSTGRES_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'postgres-password'))",
        "TERMLINK_ACCEPTANCE_VALKEY_PASSWORD_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'valkey-password'))",
        "TERMLINK_ACCEPTANCE_OIDC_CLIENT_SECRET_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'oidc-client-secret'))",
        "TERMLINK_ACCEPTANCE_DATABASE_URL_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'database-url'))",
        "TERMLINK_ACCEPTANCE_REDIS_URL_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'redis-url'))",
        "TERMLINK_ACCEPTANCE_TOKEN_SIGNING_KEY_FILE=$(ConvertTo-ComposeHostPath (Join-Path $secretRoot 'token-signing-key'))"
    )
    [IO.File]::WriteAllLines($envFile, $environmentLines, [Text.UTF8Encoding]::new($false))

    $core = Join-Path $composeRoot 'core.compose.yml'
    $observability = Join-Path $composeRoot 'observability.compose.yml'
    $externalServices = Join-Path $composeRoot 'external-services.compose.yml'
    $registrationRecovery = Join-Path $composeRoot 'registration-recovery.compose.yml'
    $humanVerification = Join-Path $composeRoot 'human-verification.compose.yml'
    $accountDeletion = Join-Path $composeRoot 'account-deletion.compose.yml'
    $directTransport = Join-Path $composeRoot 'direct-transport.compose.yml'
    $acceptance = Join-Path $composeRoot 'acceptance.compose.yml'
    $acceptanceLocalEdge = Join-Path $composeRoot 'acceptance.local-edge.compose.yml'

    $publicAcceptance = Get-ComposeConfig -EnvironmentFile $envFile -Files @($acceptance)
    $publicAcceptanceServices = @($publicAcceptance.services.PSObject.Properties.Name)
    if ($publicAcceptanceServices -ccontains 'edge' -or
        $publicAcceptanceServices -ccontains 'server-local-edge-ca-init') {
        throw 'Default public acceptance topology unexpectedly includes the local TLS edge.'
    }
    if (@($publicAcceptance.services.server.environment.PSObject.Properties.Name) -cnotcontains 'SSL_CERT_FILE' -or
        @($publicAcceptance.services.'server-secrets-init'.depends_on.PSObject.Properties.Name) -cnotcontains 'lucky-upstream') {
        throw 'Default public server is missing its internal OIDC loopback trust gate.'
    }
    $localAcceptance = Get-ComposeConfig -EnvironmentFile $envFile -Files @($acceptance, $acceptanceLocalEdge)
    $localAcceptanceServices = @($localAcceptance.services.PSObject.Properties.Name)
    if ($localAcceptanceServices -ccontains 'edge' -or
        $localAcceptanceServices -ccontains 'server-local-edge-ca-init') {
        throw 'Private-LAN acceptance unexpectedly restored the retired edge service.'
    }
    $localTLSListeners = @($localAcceptance.services.'lucky-upstream'.ports | Where-Object {
        [int]$_.target -eq 9443 -and [int]$_.published -eq 9443
    })
    if ($localTLSListeners.Count -ne 1) {
        throw 'Private-LAN acceptance did not expose Lucky internal TLS on port 9443.'
    }

    # Render every valid feature selection with and without edge. Account
    # deletion is independent from the retired object-storage subsystem.
    $matrixCount = 0
    for ($flags = 0; $flags -lt 32; $flags++) {
        $files = @($core)
        if (($flags -band 1) -ne 0) { $files += $observability }
        if (($flags -band 2) -ne 0) { $files += $registrationRecovery }
        if (($flags -band 4) -ne 0) { $files += $humanVerification }
        if (($flags -band 8) -ne 0) { $files += $directTransport }
        if (($flags -band 16) -ne 0) { $files += $accountDeletion }
        foreach ($withEdge in @($false, $true)) {
            Test-ComposeConfig -EnvironmentFile $envFile -Files $files -IncludeEdge:$withEdge
            $matrixCount++
        }
    }
    if ($matrixCount -ne 64) {
        throw "Unexpected Compose matrix size: $matrixCount"
    }

    $cases = @(
        @{ Name = 'core with edge'; Files = @($core); Edge = $true },
        @{ Name = 'core without edge'; Files = @($core); Edge = $false },
        @{ Name = 'core plus observability'; Files = @($core, $observability); Edge = $true },
        @{ Name = 'core plus registration recovery'; Files = @($core, $registrationRecovery); Edge = $true },
        @{ Name = 'core plus human verification'; Files = @($core, $humanVerification); Edge = $true },
        @{ Name = 'core plus Direct shared state'; Files = @($core, $directTransport); Edge = $true },
        @{ Name = 'core plus account deletion'; Files = @($core, $accountDeletion); Edge = $true },
        @{ Name = 'core plus both recovery paths'; Files = @($core, $registrationRecovery, $accountDeletion); Edge = $false },
        @{ Name = 'default full stack'; Files = @($core, $observability, $registrationRecovery, $humanVerification, $accountDeletion, $directTransport); Edge = $true },
        @{ Name = 'external OIDC overlay'; Files = @($core, $externalServices); Edge = $true },
        @{ Name = 'external OIDC plus observability'; Files = @($core, $externalServices, $observability); Edge = $true }
    )
    foreach ($case in $cases) {
        Write-Host "Validating $($case.Name)..."
        & $policy -EnvFile $envFile -ComposeFile $case.Files -IncludeEdge:$case.Edge
    }

    # The Direct overlay is one shared state plane for any number of server
    # replicas. Only server receives its network, enable switch, and URL
    # secret; migrate remains on the Relay backend with only Relay secrets.
    $directConfig = Get-ComposeConfig -EnvironmentFile $envFile -Files @($core, $directTransport)
    $directServer = $directConfig.services.server
    $directMigration = $directConfig.services.migrate
    $directState = $directConfig.services.'direct-valkey'
    $serverEnvironmentNames = @($directServer.environment.PSObject.Properties.Name)
    $migrationEnvironmentNames = @($directMigration.environment.PSObject.Properties.Name)
    foreach ($name in @(
        'TERMLINK_DIRECT_TRANSPORT_ENABLED', 'TERMLINK_DIRECT_REDIS_URL_FILE', 'TERMLINK_DIRECT_STUN_SERVERS'
    )) {
        if ($serverEnvironmentNames -cnotcontains $name) {
            throw "Direct server topology omitted $name."
        }
        if ($migrationEnvironmentNames -ccontains $name) {
            throw "Migration unexpectedly received Direct authority $name."
        }
    }
    $serverSecretSources = @($directServer.secrets | ForEach-Object { [string]$_.source })
    $migrationSecretSources = @($directMigration.secrets | ForEach-Object { [string]$_.source })
    if ($serverSecretSources -cnotcontains 'direct_redis_url' -or
        $migrationSecretSources -ccontains 'direct_redis_url' -or
        $migrationSecretSources -ccontains 'direct_valkey_password') {
        throw 'Direct secret topology crossed the server/migration authority boundary.'
    }
    $serverNetworkNames = @($directServer.networks.PSObject.Properties.Name)
    $migrationNetworkNames = @($directMigration.networks.PSObject.Properties.Name)
    $directNetworkNames = @($directState.networks.PSObject.Properties.Name)
    if ($serverNetworkNames -cnotcontains 'direct-state' -or
        $directNetworkNames -cnotcontains 'direct-state' -or
        $migrationNetworkNames -ccontains 'direct-state') {
        throw 'Direct shared-state network is not isolated to server and direct-valkey.'
    }
    if ($directServer.depends_on.PSObject.Properties.Name -ccontains 'direct-valkey') {
        throw 'Direct state health unexpectedly gates Relay server startup.'
    }

    Assert-PolicyRejected -Policy $policy -EnvironmentFile $envFile `
        -Files @($core, $acceptance) -ExpectedPattern 'not part of the reviewed production matrix'

    $noAcmeEnv = New-FilteredEnvironmentFile -Source $envFile -Destination (Join-Path $testRoot 'no-acme.env') `
        -RemovePrefixes @('TERMLINK_ACME_EMAIL=')
    & $policy -EnvFile $noAcmeEnv -ComposeFile @($core) -IncludeEdge:$false | Out-Null
    Assert-PolicyRejected -Policy $policy -EnvironmentFile $noAcmeEnv -Files @($core) `
        -IncludeEdge:$true -ExpectedPattern 'syntactically valid ACME contact email'

    $missingCases = @(
        @{ Name = 'observability receiver config'; Files = @($core, $observability); Remove = @('TERMLINK_ALERTMANAGER_CONFIG_FILE='); Pattern = 'TERMLINK_ALERTMANAGER_CONFIG_FILE' },
        @{ Name = 'observability admin secret'; Files = @($core, $observability); Remove = @('TERMLINK_GRAFANA_ADMIN_PASSWORD_FILE='); Pattern = 'TERMLINK_GRAFANA_ADMIN_PASSWORD_FILE' },
        @{ Name = 'registration recovery secret'; Files = @($core, $registrationRecovery); Remove = @('TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE='); Pattern = 'TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE' },
        @{ Name = 'human verification site key'; Files = @($core, $humanVerification); Remove = @('TERMLINK_HUMAN_VERIFICATION_SITE_KEY='); Pattern = 'TERMLINK_HUMAN_VERIFICATION_SITE_KEY' },
        @{ Name = 'human verification secret'; Files = @($core, $humanVerification); Remove = @('TERMLINK_HUMAN_VERIFICATION_SECRET_FILE='); Pattern = 'TERMLINK_HUMAN_VERIFICATION_SECRET_FILE' },
        @{ Name = 'account deletion identity key'; Files = @($core, $accountDeletion); Remove = @('TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE='); Pattern = 'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE' },
        @{ Name = 'account deletion key version'; Files = @($core, $accountDeletion); Remove = @('TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION='); Pattern = 'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION' },
        @{ Name = 'Direct Redis URL'; Files = @($core, $directTransport); Remove = @('TERMLINK_DIRECT_REDIS_URL_FILE='); Pattern = 'TERMLINK_DIRECT_REDIS_URL_FILE' },
        @{ Name = 'Direct Valkey password'; Files = @($core, $directTransport); Remove = @('TERMLINK_DIRECT_VALKEY_PASSWORD_FILE='); Pattern = 'TERMLINK_DIRECT_VALKEY_PASSWORD_FILE' },
        @{ Name = 'Direct STUN endpoints'; Files = @($core, $directTransport); Remove = @('TERMLINK_DIRECT_STUN_SERVERS='); Pattern = 'TERMLINK_DIRECT_STUN_SERVERS' }
    )
    foreach ($missingCase in $missingCases) {
        $missingEnv = New-FilteredEnvironmentFile -Source $envFile `
            -Destination (Join-Path $testRoot ("missing-{0}.env" -f ($missingCase.Name -replace ' ', '-'))) `
            -RemovePrefixes $missingCase.Remove
        Assert-ComposeConfigRejected -EnvironmentFile $missingEnv -Files $missingCase.Files `
            -ExpectedPattern $missingCase.Pattern
    }

    $wrongDirectUrlPath = New-ProtectedTestFile -Path (Join-Path $secretRoot 'wrong-direct-redis-url') `
        -Value "redis://termlink-direct:$directValkeyPassword@valkey:6379/0"
    $wrongDirectUrlEnv = New-EnvironmentOverrideFile -Source $envFile `
        -Destination (Join-Path $testRoot 'wrong-direct-url.env') `
        -Overrides @{ TERMLINK_DIRECT_REDIS_URL_FILE = (ConvertTo-ComposeHostPath $wrongDirectUrlPath) }
    Assert-PolicyRejected -Policy $policy -EnvironmentFile $wrongDirectUrlEnv `
        -Files @($core, $directTransport) -ExpectedPattern 'one shared Direct Valkey service'

    $reusedDirectUrlPath = New-ProtectedTestFile -Path (Join-Path $secretRoot 'reused-direct-redis-url') `
        -Value "redis://termlink-direct:$valkeyPassword@direct-valkey:6379/0"
    $reusedDirectPasswordEnv = New-EnvironmentOverrideFile -Source $envFile `
        -Destination (Join-Path $testRoot 'reused-direct-password.env') `
        -Overrides @{
            TERMLINK_DIRECT_REDIS_URL_FILE = (ConvertTo-ComposeHostPath $reusedDirectUrlPath)
            TERMLINK_DIRECT_VALKEY_PASSWORD_FILE = (ConvertTo-ComposeHostPath (Join-Path $secretRoot 'valkey-password'))
        }
    Assert-PolicyRejected -Policy $policy -EnvironmentFile $reusedDirectPasswordEnv `
        -Files @($core, $directTransport) -ExpectedPattern 'must not reuse the same ACL password'

    $invalidStunEnv = New-EnvironmentOverrideFile -Source $envFile `
        -Destination (Join-Path $testRoot 'invalid-direct-stun.env') `
        -Overrides @{ TERMLINK_DIRECT_STUN_SERVERS = 'stun:localhost:3478' }
    Assert-PolicyRejected -Policy $policy -EnvironmentFile $invalidStunEnv `
        -Files @($core, $directTransport) -ExpectedPattern 'production DNS hosts'

    $placeholderAlertEnv = Join-Path $testRoot 'placeholder-alertmanager.env'
    $bundledAlertmanagerConfig = ConvertTo-ComposeHostPath (Join-Path $serverRoot 'observability/alertmanager/alertmanager.yml')
    $placeholderAlertLines = [IO.File]::ReadAllLines($envFile) | ForEach-Object {
        if ($_.StartsWith('TERMLINK_ALERTMANAGER_CONFIG_FILE=', [StringComparison]::Ordinal)) {
            "TERMLINK_ALERTMANAGER_CONFIG_FILE=$bundledAlertmanagerConfig"
        } else { $_ }
    }
    [IO.File]::WriteAllLines($placeholderAlertEnv, @($placeholderAlertLines), [Text.UTF8Encoding]::new($false))
    Assert-PolicyRejected -Policy $policy -EnvironmentFile $placeholderAlertEnv `
        -Files @($core, $observability) -ExpectedPattern 'no-delivery placeholder'

    $invalidIdentityKeyPath = Join-Path $secretRoot 'invalid-account-deletion-identity-key'
    [IO.File]::WriteAllText($invalidIdentityKeyPath, 'not-a-32-byte-base64-key', [Text.UTF8Encoding]::new($false))
    Set-TestSecretPermissions -Path $invalidIdentityKeyPath -Directory $false
    $invalidKeyEnv = Join-Path $testRoot 'invalid-account-key.env'
    $invalidKeyLines = [IO.File]::ReadAllLines($envFile) | ForEach-Object {
        if ($_.StartsWith('TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE=', [StringComparison]::Ordinal)) {
            "TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE=$(ConvertTo-ComposeHostPath $invalidIdentityKeyPath)"
        } else { $_ }
    }
    [IO.File]::WriteAllLines($invalidKeyEnv, @($invalidKeyLines), [Text.UTF8Encoding]::new($false))
    Assert-PolicyRejected -Policy $policy -EnvironmentFile $invalidKeyEnv `
        -Files @($core, $accountDeletion) -ExpectedPattern 'one AES-256 key or a bounded versioned keyring' `
        -ForbiddenText @('not-a-32-byte-base64-key')

    $invalidVersionEnv = Join-Path $testRoot 'invalid-account-key-version.env'
    $invalidVersionLines = [IO.File]::ReadAllLines($envFile) | ForEach-Object {
        if ($_.StartsWith('TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION=', [StringComparison]::Ordinal)) {
            'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION=0'
        } else { $_ }
    }
    [IO.File]::WriteAllLines($invalidVersionEnv, @($invalidVersionLines), [Text.UTF8Encoding]::new($false))
    Assert-PolicyRejected -Policy $policy -EnvironmentFile $invalidVersionEnv `
        -Files @($core, $accountDeletion) -ExpectedPattern 'must be a positive integer'

    $rotatedIdentityKeyBytes = [byte[]](32..63)
    $rotatedIdentityKey = [Convert]::ToBase64String($rotatedIdentityKeyBytes)
    $thirdIdentityKeyBytes = [byte[]](64..95)
    $thirdIdentityKey = [Convert]::ToBase64String($thirdIdentityKeyBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $validKeyring = ConvertTo-Json -InputObject @(
        [ordered]@{ version = 1; key = $identityKey },
        [ordered]@{ version = 7; key = $rotatedIdentityKey }
    ) -Compress
    $validKeyringPath = New-ProtectedTestFile -Path (Join-Path $secretRoot 'valid-account-deletion-keyring') -Value $validKeyring
    $validKeyringEnv = New-EnvironmentOverrideFile -Source $envFile -Destination (Join-Path $testRoot 'valid-keyring.env') `
        -Overrides @{
            TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE = (ConvertTo-ComposeHostPath $validKeyringPath)
            TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION = '7'
        }
    & $policy -EnvFile $validKeyringEnv -ComposeFile @($core, $accountDeletion) -IncludeEdge:$false | Out-Null

    $unknownSecret = 'KEYRING_UNKNOWN_FIELD_MUST_NOT_BE_LOGGED'
    $keyringCases = @(
        @{
            Name = 'duplicate-version'
            Current = '1'
            Value = (ConvertTo-Json -InputObject @(
                [ordered]@{ version = 1; key = $identityKey },
                [ordered]@{ version = 1; key = $rotatedIdentityKey }
            ) -Compress)
            Pattern = 'duplicate version'
            Forbidden = @($identityKey, $rotatedIdentityKey)
        },
        @{
            Name = 'unknown-field'
            Current = '1'
            Value = (ConvertTo-Json -InputObject @(
                [ordered]@{ version = 1; key = $identityKey; private = $unknownSecret }
            ) -Compress)
            Pattern = 'only version and key'
            Forbidden = @($identityKey, $unknownSecret)
        },
        @{
            Name = 'missing-current-version'
            Current = '7'
            Value = (ConvertTo-Json -InputObject @(
                [ordered]@{ version = 1; key = $identityKey },
                [ordered]@{ version = 2; key = $thirdIdentityKey }
            ) -Compress)
            Pattern = 'does not contain the configured current version'
            Forbidden = @($identityKey, $thirdIdentityKey)
        },
        @{
            Name = 'over-32-entries'
            Current = '1'
            Value = (ConvertTo-Json -InputObject @(1..33 | ForEach-Object {
                [ordered]@{ version = $_; key = $identityKey }
            }) -Compress)
            Pattern = 'between 1 and 32 entries'
            Forbidden = @($identityKey)
        }
    )
    foreach ($keyringCase in $keyringCases) {
        $keyringPath = New-ProtectedTestFile `
            -Path (Join-Path $secretRoot ("invalid-keyring-{0}" -f $keyringCase.Name)) `
            -Value ([string]$keyringCase.Value)
        $keyringEnv = New-EnvironmentOverrideFile -Source $envFile `
            -Destination (Join-Path $testRoot ("invalid-keyring-{0}.env" -f $keyringCase.Name)) `
            -Overrides @{
                TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE = (ConvertTo-ComposeHostPath $keyringPath)
                TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION = [string]$keyringCase.Current
            }
        Assert-PolicyRejected -Policy $policy -EnvironmentFile $keyringEnv `
            -Files @($core, $accountDeletion) -ExpectedPattern ([string]$keyringCase.Pattern) `
            -ForbiddenText @($keyringCase.Forbidden)
    }

    Write-Host "TermLink production deployment policy matrix passed ($matrixCount Compose selections)."
}
finally {
    if ($null -ne $identityKeyBytes) { [Array]::Clear($identityKeyBytes, 0, $identityKeyBytes.Length) }
    if ($null -ne $rotatedIdentityKeyBytes) { [Array]::Clear($rotatedIdentityKeyBytes, 0, $rotatedIdentityKeyBytes.Length) }
    if ($null -ne $thirdIdentityKeyBytes) { [Array]::Clear($thirdIdentityKeyBytes, 0, $thirdIdentityKeyBytes.Length) }
    $identityKey = $null
    $rotatedIdentityKey = $null
    $thirdIdentityKey = $null
    if (Test-Path -LiteralPath $testRoot) {
        [IO.Directory]::Delete($testRoot, $true)
    }
}
