[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EnvFile,
    [Parameter(Mandatory)][string]$ComposeFile,
    [bool]$IncludeEdge = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TermLinkBackup.Common.psm1') -Force

function Assert-SingleNode {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-Value {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-NetworkNames {
    param([object]$Service)
    $networks = Get-Value $Service 'networks'
    if ($null -eq $networks) { return @() }
    if ($networks -is [Collections.IDictionary] -or
        $networks -is [Management.Automation.PSCustomObject]) {
        return @($networks.PSObject.Properties.Name | Sort-Object)
    }
    return @($networks | ForEach-Object { [string]$_ } | Sort-Object)
}

function Get-EnvironmentMap {
    param([string]$Path)
    $result = @{}
    foreach ($rawLine in [IO.File]::ReadAllLines($Path)) {
        $line = $rawLine.Trim()
        if ($line -eq '' -or $line.StartsWith('#', [StringComparison]::Ordinal)) { continue }
        $separator = $line.IndexOf('=')
        Assert-SingleNode ($separator -gt 0) "Invalid environment line: $rawLine"
        $name = $line.Substring(0, $separator)
        Assert-SingleNode ($name -cmatch '^[A-Z][A-Z0-9_]*$') "Invalid environment name: $name"
        Assert-SingleNode (-not $result.ContainsKey($name)) "Duplicate environment name: $name"
        $result[$name] = $line.Substring($separator + 1)
    }
    return $result
}

function Assert-PrivatePath {
    param([string]$Path, [bool]$Directory, [string]$DisplayName)
    Assert-SingleNode (Test-Path -LiteralPath $Path -PathType $(if ($Directory) { 'Container' } else { 'Leaf' })) `
        "$DisplayName does not exist: $Path"
    $item = Get-Item -LiteralPath $Path -Force
    Assert-SingleNode (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        "$DisplayName cannot be a reparse point."
    Assert-TermLinkPathHasNoReparsePoint -Path $Path -Root ([IO.Path]::GetPathRoot($Path)) | Out-Null
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        $mode = [IO.File]::GetUnixFileMode($Path)
        $forbidden = [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor
            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor
            [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        Assert-SingleNode (($mode -band $forbidden) -eq 0) "$DisplayName is group- or world-accessible."
        if ($Directory) {
            Assert-SingleNode (($mode -band [IO.UnixFileMode]::UserExecute) -ne 0) `
                "$DisplayName is not owner-traversable."
        }
        return
    }
    $allowedSids = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',
        'S-1-5-32-544'
    )
    $acl = Get-Acl -LiteralPath $Path
    foreach ($rule in @($acl.Access)) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) {
            Assert-SingleNode ($allowedSids -contains $sid) `
                "$DisplayName grants access to an unreviewed Windows identity."
        }
    }
}

function Assert-ExactSet {
    param([string[]]$Expected, [string[]]$Actual, [string]$Message)
    $expectedValues = @($Expected | Sort-Object)
    $actualValues = @($Actual | Sort-Object)
    $different = if ($expectedValues.Count -eq 0 -and $actualValues.Count -eq 0) {
        $false
    }
    elseif ($expectedValues.Count -eq 0 -or $actualValues.Count -eq 0) {
        $true
    }
    else {
        @(Compare-Object $expectedValues $actualValues).Count -ne 0
    }
    Assert-SingleNode (-not $different) "$Message Actual: $($Actual -join ', ')"
}

$envPath = [IO.Path]::GetFullPath($EnvFile)
$composePath = [IO.Path]::GetFullPath($ComposeFile)
$reviewedPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\deploy\single-node-production\compose.yml'))
Assert-SingleNode ($composePath -ceq $reviewedPath) 'Single-node validation accepts only the reviewed deployment Compose file.'
Assert-PrivatePath -Path $envPath -Directory $false -DisplayName 'Deployment environment file'
$deploymentEnvironment = Get-EnvironmentMap -Path $envPath

$expectedEnvironmentNames = @(
    'COMPOSE_PROJECT_NAME', 'TERMLINK_ADMIN_EMAIL', 'TERMLINK_BACKUP_RECIPIENT',
    'TERMLINK_CONFIG_VERSION', 'TERMLINK_HTTP_BIND_IP', 'TERMLINK_HTTP_PORT',
    'TERMLINK_HTTPS_BIND_IP', 'TERMLINK_HTTPS_PORT', 'TERMLINK_IMAGE',
    'TERMLINK_PUBLIC_HOST', 'TERMLINK_PUBLIC_URL', 'TERMLINK_RUNTIME_DIR',
    'TERMLINK_SECRETS_DIR', 'TERMLINK_TLS_MODE'
)
Assert-ExactSet -Expected $expectedEnvironmentNames -Actual @($deploymentEnvironment.Keys) `
    -Message 'Deployment environment keys differ from the reviewed non-secret set.'
Assert-SingleNode ($deploymentEnvironment.TERMLINK_CONFIG_VERSION -ceq '1') 'Unsupported deployment environment version.'
Assert-SingleNode ($deploymentEnvironment.COMPOSE_PROJECT_NAME -ceq 'termlink') 'Production project name must be termlink.'
Assert-SingleNode ($deploymentEnvironment.TERMLINK_HTTP_BIND_IP -ceq '127.0.0.1') `
    'Server HTTP must bind only to loopback.'
Assert-SingleNode ($deploymentEnvironment.TERMLINK_HTTPS_BIND_IP -ceq '0.0.0.0') `
    'Managed HTTPS must use the reviewed public bind address.'
Assert-SingleNode ($deploymentEnvironment.TERMLINK_TLS_MODE -in @('managed', 'external')) `
    'TLS mode must be managed or external.'

$publicUri = $null
Assert-SingleNode ([Uri]::TryCreate($deploymentEnvironment.TERMLINK_PUBLIC_URL, [UriKind]::Absolute, [ref]$publicUri) -and
    $publicUri.Scheme -ceq 'https' -and $publicUri.UserInfo -eq '' -and $publicUri.Query -eq '' -and
    $publicUri.Fragment -eq '' -and $publicUri.AbsolutePath -ceq '/') 'Public URL must be an origin-only HTTPS URL.'
Assert-SingleNode ($publicUri.DnsSafeHost -ceq $deploymentEnvironment.TERMLINK_PUBLIC_HOST) `
    'Public URL host must match TERMLINK_PUBLIC_HOST.'
Assert-SingleNode ($publicUri.Host -notmatch '(?i)(^localhost$|\.(localhost|invalid|test|example)$|(^|\.)example\.(com|net|org)$)') `
    'Public URL cannot use a reserved test domain.'
Assert-SingleNode ($deploymentEnvironment.TERMLINK_ADMIN_EMAIL -match '^[^\s@]+@[^\s@]+\.[^\s@]+$') `
    'Administrator e-mail is invalid.'
Assert-SingleNode ($deploymentEnvironment.TERMLINK_IMAGE -cmatch
    '^docker\.io/myucxy/termlink:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}@sha256:[0-9a-f]{64}$') `
    'Server image must be an immutable docker.io/myucxy/termlink tag and digest.'
$unexpectedSecretNames = @($deploymentEnvironment.Keys | Where-Object {
    $_ -ne 'TERMLINK_SECRETS_DIR' -and $_ -match '(?i)PASSWORD|SECRET|TOKEN|DATABASE_URL'
})
Assert-SingleNode ($unexpectedSecretNames.Count -eq 0) `
    'Deployment environment must not declare secret-bearing values.'

$runtimePath = [IO.Path]::GetFullPath($deploymentEnvironment.TERMLINK_RUNTIME_DIR)
$secretsPath = [IO.Path]::GetFullPath($deploymentEnvironment.TERMLINK_SECRETS_DIR)
Assert-PrivatePath -Path $runtimePath -Directory $true -DisplayName 'Runtime directory'
Assert-PrivatePath -Path $secretsPath -Directory $true -DisplayName 'Secret directory'

$arguments = @('compose', '--env-file', $envPath, '--project-name', 'termlink-single-node-policy', '-f', $composePath)
if ($IncludeEdge) { $arguments += @('--profile', 'managed-tls') }
$arguments += @('config', '--format', 'json')
$renderedResult = Invoke-TermLinkDocker -Arguments $arguments
$renderedText = $renderedResult.Output -join [Environment]::NewLine
try { $config = $renderedText | ConvertFrom-Json -Depth 100 }
catch { throw "Rendered single-node Compose is invalid JSON: $($_.Exception.Message)" }

$expectedServices = @('keycloak', 'keycloak-init', 'postgres', 'secrets-init', 'server')
if ($IncludeEdge) { $expectedServices += 'edge' }
$actualServices = @($config.services.PSObject.Properties.Name)
Assert-ExactSet -Expected $expectedServices -Actual $actualServices `
    -Message 'Single-node production services differ from the reviewed topology.'
foreach ($retired in @('alertmanager', 'grafana', 'migrate', 'minio', 'prometheus', 'valkey', 'direct-valkey')) {
    Assert-SingleNode ($null -eq (Get-Value $config.services $retired)) "Retired service is present: $retired"
}

$approvedImages = @{
    'secrets-init' = 'caddy:2.10.0-alpine@sha256:ae4458638da8e1a91aafffb231c5f8778e964bca650c8a8cb23a7e8ac557aa3c'
    postgres = 'postgres:17.5-alpine3.22@sha256:6567bca8d7bc8c82c5922425a0baee57be8402df92bae5eacad5f01ae9544daa'
    keycloak = 'termlink-keycloak:26.3.2-theme-v18'
    'keycloak-init' = 'termlink-keycloak:26.3.2-theme-v18'
    server = $deploymentEnvironment.TERMLINK_IMAGE
    edge = 'caddy:2.10.0-alpine@sha256:ae4458638da8e1a91aafffb231c5f8778e964bca650c8a8cb23a7e8ac557aa3c'
}
foreach ($serviceName in $actualServices) {
    $service = Get-Value $config.services $serviceName
    Assert-SingleNode ([string](Get-Value $service 'image') -ceq $approvedImages[$serviceName]) `
        "Service '$serviceName' does not use its reviewed image."
    Assert-SingleNode (-not [bool](Get-Value $service 'privileged')) "Service '$serviceName' cannot be privileged."
    foreach ($propertyName in @('container_name', 'devices', 'ipc', 'pid')) {
        Assert-SingleNode ($null -eq (Get-Value $service $propertyName)) `
            "Service '$serviceName' cannot set $propertyName."
    }
    if ($serviceName -ne 'postgres') {
        $security = @((Get-Value $service 'security_opt'))
        Assert-SingleNode ($security -contains 'no-new-privileges:true') `
            "Service '$serviceName' must set no-new-privileges."
        $capDrop = @((Get-Value $service 'cap_drop'))
        Assert-SingleNode ($capDrop -contains 'ALL') "Service '$serviceName' must drop all capabilities."
        Assert-SingleNode ([bool](Get-Value $service 'read_only')) `
            "Service '$serviceName' must have a read-only root filesystem."
    }
}

$keycloakBuild = Get-Value (Get-Value $config.services 'keycloak') 'build'
Assert-SingleNode ($null -ne $keycloakBuild) 'Keycloak must build the TermLink theme image locally.'
Assert-SingleNode ([string](Get-Value (Get-Value $keycloakBuild 'args') 'KEYCLOAK_BASE_IMAGE') -ceq
    'quay.io/keycloak/keycloak:26.3.2@sha256:98fab020a3a490aba0978f237e2a06cd0ea42bf149c6cf10f11c0aaf27728ff2') `
    'Keycloak base image is not pinned to the reviewed digest.'
Assert-SingleNode ($null -eq (Get-Value (Get-Value $config.services 'keycloak-init') 'build')) `
    'Only the Keycloak runtime service may own the shared image build.'

$expectedNetworks = @{
    'secrets-init' = @()
    postgres = @('database')
    keycloak = @('database', 'egress', 'identity')
    'keycloak-init' = @('identity')
    server = @('database', 'egress', 'frontend', 'identity')
    edge = @('frontend', 'public')
}
foreach ($serviceName in $actualServices) {
    Assert-ExactSet -Expected $expectedNetworks[$serviceName] -Actual @(Get-NetworkNames (Get-Value $config.services $serviceName)) `
        -Message "Service '$serviceName' network membership differs from the reviewed topology."
}
foreach ($networkName in @('database', 'frontend', 'identity')) {
    Assert-SingleNode ([bool](Get-Value (Get-Value $config.networks $networkName) 'internal')) `
        "Network '$networkName' must remain internal."
}
Assert-SingleNode (-not [bool](Get-Value (Get-Value $config.networks 'egress') 'internal')) `
    'Egress network cannot be internal.'

$server = Get-Value $config.services 'server'
$serverEnvironment = Get-Value $server 'environment'
Assert-SingleNode ([string](Get-Value $serverEnvironment 'TERMLINK_RUNTIME_MODE') -ceq 'single-node') `
    'Server must use single-node runtime mode.'
Assert-SingleNode ([string](Get-Value $serverEnvironment 'TERMLINK_OIDC_BACKCHANNEL_ISSUER') -ceq
    'http://keycloak:8080/realms/termlink') 'Server must use the internal Keycloak backchannel issuer.'
Assert-SingleNode ([string](Get-Value $serverEnvironment 'TERMLINK_DIRECT_TRANSPORT_ENABLED') -ceq 'true') `
    'Single-node Direct/NAT signalling must be enabled.'
foreach ($name in @('TERMLINK_REDIS_URL_FILE', 'TERMLINK_DIRECT_REDIS_URL_FILE')) {
    Assert-SingleNode ($null -eq (Get-Value $serverEnvironment $name)) `
        "Single-node Server cannot receive $name."
}
Assert-SingleNode ($null -eq (Get-Value (Get-Value $server 'deploy') 'replicas') -or
    [int](Get-Value (Get-Value $server 'deploy') 'replicas') -eq 1) `
    'Single-node Server cannot declare more than one replica.'
$serverPorts = @((Get-Value $server 'ports') | Where-Object { $null -ne $_ })
Assert-SingleNode ($serverPorts.Count -eq 1 -and
    [string](Get-Value $serverPorts[0] 'host_ip') -ceq '127.0.0.1' -and
    [int](Get-Value $serverPorts[0] 'target') -eq 8080) 'Server may expose only its loopback HTTP origin.'
foreach ($serviceName in @('postgres', 'keycloak', 'keycloak-init', 'secrets-init')) {
    $servicePorts = @((Get-Value (Get-Value $config.services $serviceName) 'ports') |
        Where-Object { $null -ne $_ })
    Assert-SingleNode ($servicePorts.Count -eq 0) `
        "Service '$serviceName' cannot expose host ports."
}

if ($IncludeEdge) {
    $edge = Get-Value $config.services 'edge'
    $edgePorts = @((Get-Value $edge 'ports') | Where-Object { $null -ne $_ })
    Assert-SingleNode ($edgePorts.Count -eq 1 -and [int](Get-Value $edgePorts[0] 'target') -eq 8443) `
        'Managed edge must expose only its HTTPS listener.'
    $edgeEnvironment = Get-Value $edge 'environment'
    Assert-SingleNode ([string](Get-Value $edgeEnvironment 'TERMLINK_ACME_CA') -ceq
        'https://acme-v02.api.letsencrypt.org/directory') 'Managed TLS must use production Let''s Encrypt.'
    Assert-SingleNode ([string](Get-Value $edgeEnvironment 'TERMLINK_PUBLIC_HOST') -ceq
        $deploymentEnvironment.TERMLINK_PUBLIC_HOST) 'Managed edge host does not match the public URL.'
}

$secretNames = @(
    'postgres-password', 'keycloak-db-password', 'keycloak-bootstrap-admin-password',
    'initial-admin-password', 'keycloak-admin-client-secret', 'oidc-client-secret',
    'database-url', 'token-signing-key', 'account-deletion-identity-key'
)
$secretValues = @{}
foreach ($name in $secretNames) {
    $path = Join-Path $secretsPath $name
    Assert-PrivatePath -Path $path -Directory $false -DisplayName "Secret '$name'"
    $value = [IO.File]::ReadAllText($path).Trim()
    Assert-SingleNode ($value.Length -ge 32) "Secret '$name' is too short."
    Assert-SingleNode (-not $renderedText.Contains($value, [StringComparison]::Ordinal)) `
        "Rendered Compose exposes secret '$name'."
    $secretValues[$name] = $value
}
foreach ($name in @(
    'postgres-password', 'keycloak-db-password', 'keycloak-bootstrap-admin-password',
    'initial-admin-password', 'keycloak-admin-client-secret', 'oidc-client-secret', 'token-signing-key'
)) {
    Assert-SingleNode ($secretValues[$name] -cmatch '^[A-Za-z0-9_-]{32,128}$') `
        "Secret '$name' is not URL-safe random material."
}
Assert-SingleNode ($secretValues['account-deletion-identity-key'] -cmatch '^[A-Za-z0-9_-]{43}$') `
    'Account deletion identity key must encode exactly 32 bytes.'
Assert-SingleNode ($secretValues['database-url'] -ceq
    "postgres://termlink:$($secretValues['postgres-password'])@postgres:5432/termlink?sslmode=disable") `
    'Database URL does not match the file-backed PostgreSQL password.'

$realmPath = Join-Path $runtimePath 'keycloak-realm.json'
$profilePath = Join-Path $runtimePath 'keycloak-user-profile.json'
$reconcilePath = Join-Path $runtimePath 'keycloak-reconcile.sh'
$caddyPath = Join-Path $runtimePath 'Caddyfile'
foreach ($path in @($realmPath, $profilePath, $reconcilePath, $caddyPath)) {
    Assert-SingleNode (Test-Path -LiteralPath $path -PathType Leaf) "Runtime asset is missing: $path"
}
$realm = Get-Content -LiteralPath $realmPath -Raw | ConvertFrom-Json -Depth 100
$clientIDs = @($realm.clients | ForEach-Object { [string]$_.clientId })
Assert-ExactSet -Expected @(
    'termlink-android', 'termlink-registration-recovery', 'termlink-server',
    'termlink-web', 'termlink-web-step-up', 'termlink-windows'
) -Actual $clientIDs -Message 'Production realm client set is unexpected.'
$ordinaryUsers = @($realm.users | Where-Object { [string]$_.serviceAccountClientId -eq '' })
Assert-SingleNode ($ordinaryUsers.Count -eq 0) 'Production realm import cannot contain ordinary users.'
$realmText = [IO.File]::ReadAllText($realmPath)
$reconcileText = [IO.File]::ReadAllText($reconcilePath)
Assert-SingleNode ($realmText -notmatch '(?i)acceptance-user|acceptance-probe|termlink-acceptance-cli') `
    'Production realm import contains an acceptance identity or client.'
Assert-SingleNode ($reconcileText -notmatch '(?m)^\s*for username in acceptance') `
    'Production reconcile script cannot create acceptance users.'
$caddyText = [IO.File]::ReadAllText($caddyPath)
Assert-SingleNode ($caddyText -match '(?m)^\s*@internal path /metrics /metrics/\* /debug /debug/\*\s*$' -and
    $caddyText -match '(?m)^\s*respond @internal 404\s*$') 'Managed edge must deny metrics and debug routes.'
Assert-SingleNode ($caddyText -notmatch '(?i)minio|termlink-history|X-Amz-') `
    'Managed edge contains retired object-storage routes.'

Write-Host 'TermLink single-node production deployment policy validation passed.'
