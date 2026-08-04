[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EnvFile,
    [string[]]$ComposeFile = @(
        (Join-Path $PSScriptRoot '..\compose\core.compose.yml'),
        (Join-Path $PSScriptRoot '..\compose\history.compose.yml'),
        (Join-Path $PSScriptRoot '..\compose\observability.compose.yml')
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TermLinkBackup.Common.psm1') -Force

function Assert-Production {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-PropertyValue {
    param([object]$Object, [string]$Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-NetworkNames {
    param([object]$Service)
    $networks = Get-PropertyValue $Service 'networks'
    if ($null -eq $networks) { return @() }
    if ($networks -is [Collections.IDictionary] -or $networks -is [Management.Automation.PSCustomObject]) {
        return @($networks.PSObject.Properties.Name | Sort-Object)
    }
    return @($networks | ForEach-Object { [string]$_ } | Sort-Object)
}

function Get-StringLeaf {
    param([object]$Value, [string]$Path = '$')
    if ($null -eq $Value) { return }
    if ($Value -is [string]) {
        [pscustomobject]@{ Path = $Path; Value = $Value }
        return
    }
    if ($Value -is [Collections.IDictionary]) {
        foreach ($key in $Value.Keys) { Get-StringLeaf -Value $Value[$key] -Path "$Path.$key" }
        return
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Get-StringLeaf -Value $item -Path "$Path[$index]"
            $index++
        }
        return
    }
    if ($Value.GetType().IsValueType) { return }
    foreach ($property in $Value.PSObject.Properties) {
        Get-StringLeaf -Value $property.Value -Path "$Path.$($property.Name)"
    }
}

function Assert-ProductionHttpsUri {
    param([string]$Value, [string]$Name)
    $uri = $null
    $isUri = [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)
    $parsedIP = $null
    $isIP = $isUri -and [Net.IPAddress]::TryParse($uri.Host, [ref]$parsedIP)
    $valid = $isUri -and
        $uri.Scheme -eq 'https' -and -not [string]::IsNullOrWhiteSpace($uri.Host) -and
        -not $isIP -and
        $uri.Host -notmatch '(?i)(^localhost$|\.(localhost|invalid|test|example)$|(^|\.)example\.(com|net|org)$)'
    Assert-Production $valid "$Name must be a production HTTPS DNS URL, not a loopback, IP, or reserved test domain."
    return $uri
}

$envPath = [IO.Path]::GetFullPath($EnvFile)
Assert-Production (Test-Path -LiteralPath $envPath -PathType Leaf) "Environment file not found: $envPath"
$composeFiles = @(Resolve-TermLinkComposeFiles -ComposeFile $ComposeFile)
$arguments = @('compose', '--env-file', $envPath, '--project-name', 'termlink-policy')
foreach ($file in $composeFiles) { $arguments += @('-f', $file) }
$arguments += @('--profile', 'edge')
$arguments += @('config', '--format', 'json')
$rendered = Invoke-TermLinkDocker -Arguments $arguments
try {
    $config = ($rendered.Output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
}
catch {
    throw "Rendered Compose configuration was not valid JSON: $($_.Exception.Message)"
}

$services = @($config.services.PSObject.Properties)
$requiredServices = @('alertmanager', 'blackbox', 'edge', 'grafana', 'migrate', 'minio', 'postgres', 'prometheus', 'server', 'valkey')
$actualServices = @($services.Name | Sort-Object)
Assert-Production (@(Compare-Object $requiredServices $actualServices).Count -eq 0) `
    "Production Compose services differ from the reviewed full stack: $($actualServices -join ', ')"
$approvedImages = @{
    edge = 'caddy:2.10.0-alpine@sha256:ae4458638da8e1a91aafffb231c5f8778e964bca650c8a8cb23a7e8ac557aa3c'
    postgres = 'postgres:17.5-alpine3.22@sha256:6567bca8d7bc8c82c5922425a0baee57be8402df92bae5eacad5f01ae9544daa'
    valkey = 'valkey/valkey:8.1.1-alpine@sha256:6a57d58c0a37cf7acc3045ac0fd6dd91be339774106ab4d9ca088013a096a99f'
    minio = 'minio/minio:RELEASE.2025-04-22T22-12-26Z@sha256:a1ea29fa28355559ef137d71fc570e508a214ec84ff8083e39bc5428980b015e'
    prometheus = 'prom/prometheus:v2.55.1@sha256:2659f4c2ebb718e7695cb9b25ffa7d6be64db013daba13e05c875451cf51b0d3'
    blackbox = 'prom/blackbox-exporter:v0.25.0@sha256:b04a9fef4fa086a02fc7fcd8dcdbc4b7b35cc30cdee860fdc6a19dd8b208d63e'
    alertmanager = 'prom/alertmanager:v0.27.0@sha256:e13b6ed5cb929eeaee733479dce55e10eb3bc2e9c4586c705a4e8da41e5eacf5'
    grafana = 'grafana/grafana:11.2.0@sha256:408afb9726de5122b00a2576763a8a57a3c86d5b0eff5305bc994ceb3eb96c3f'
}
$expectedNetworks = @{
    edge = @('private', 'public'); migrate = @('private'); server = @('egress', 'private')
    postgres = @('private'); valkey = @('private'); minio = @('private')
    prometheus = @('private'); blackbox = @('private'); alertmanager = @('egress', 'private'); grafana = @('private')
}
$allowedEnvironment = @{
    edge = @('TERMLINK_ACME_CA', 'TERMLINK_ACME_EMAIL', 'TERMLINK_HISTORY_PUBLIC_HOST', 'TERMLINK_PUBLIC_HOST')
    migrate = @('TERMLINK_ANDROID_OIDC_CLIENT_ID', 'TERMLINK_ANDROID_OIDC_REDIRECT_URI', 'TERMLINK_DATABASE_URL_FILE',
        'TERMLINK_HISTORY_ENABLED', 'TERMLINK_LISTEN_ADDR', 'TERMLINK_LOG_LEVEL', 'TERMLINK_OIDC_CLIENT_ID',
        'TERMLINK_OIDC_CLIENT_SECRET_FILE', 'TERMLINK_OIDC_ISSUER', 'TERMLINK_PUBLIC_URL', 'TERMLINK_REDIS_URL_FILE',
        'TERMLINK_S3_ENDPOINT', 'TERMLINK_TOKEN_SIGNING_KEY_FILE')
    server = @('TERMLINK_ANDROID_OIDC_CLIENT_ID', 'TERMLINK_ANDROID_OIDC_REDIRECT_URI', 'TERMLINK_DATABASE_URL_FILE',
        'TERMLINK_HISTORY_ENABLED', 'TERMLINK_LISTEN_ADDR', 'TERMLINK_LOG_LEVEL', 'TERMLINK_METRICS_LISTEN_ADDR',
        'TERMLINK_OIDC_CLIENT_ID', 'TERMLINK_OIDC_CLIENT_SECRET_FILE', 'TERMLINK_OIDC_ISSUER', 'TERMLINK_PUBLIC_URL',
        'TERMLINK_REDIS_URL_FILE', 'TERMLINK_S3_ACCESS_KEY_FILE', 'TERMLINK_S3_BUCKET', 'TERMLINK_S3_ENDPOINT',
        'TERMLINK_S3_PUBLIC_ENDPOINT', 'TERMLINK_S3_REGION', 'TERMLINK_S3_SECRET_KEY_FILE', 'TERMLINK_TOKEN_SIGNING_KEY_FILE')
    postgres = @('POSTGRES_DB', 'POSTGRES_PASSWORD_FILE', 'POSTGRES_USER')
    valkey = @()
    minio = @('MINIO_ROOT_PASSWORD_FILE', 'MINIO_ROOT_USER_FILE')
    prometheus = @(); blackbox = @(); alertmanager = @()
    grafana = @('GF_AUTH_ANONYMOUS_ENABLED', 'GF_PATHS_DATA', 'GF_PATHS_LOGS', 'GF_PATHS_PLUGINS',
        'GF_PATHS_PROVISIONING', 'GF_SECURITY_ADMIN_PASSWORD__FILE', 'GF_SECURITY_ADMIN_USER', 'GF_USERS_ALLOW_SIGN_UP')
}
$expectedCommands = @{
    edge = 'caddy|run|--config|/etc/caddy/Caddyfile|--adapter|caddyfile'; migrate = 'migrate'; server = ''
    postgres = ''; valkey = 'valkey-server|--appendonly|yes'; minio = 'server|/data|--console-address|:9001'
    prometheus = '--config.file=/etc/prometheus/prometheus.yml|--storage.tsdb.path=/prometheus'
    blackbox = '--config.file=/etc/blackbox/blackbox.yml'
    alertmanager = '--config.file=/etc/alertmanager/alertmanager.yml|--storage.path=/alertmanager'; grafana = ''
}
$expectedHealthchecks = @{
    edge = 'CMD-SHELL|wget -q -O - http://127.0.0.1:8081/readyz >/dev/null'
    migrate = ''; server = 'CMD|/termlink-server|healthcheck'
    postgres = 'CMD-SHELL|pg_isready -U $${POSTGRES_USER} -d $${POSTGRES_DB}'
    valkey = 'CMD|valkey-cli|ping'; minio = 'CMD|mc|ready|local'
    prometheus = 'CMD|wget|-q|-O|/dev/null|http://127.0.0.1:9090/-/ready'
    blackbox = 'CMD|/bin/blackbox_exporter|--version'
    alertmanager = 'CMD|wget|-q|-O|/dev/null|http://127.0.0.1:9093/-/ready'
    grafana = 'CMD|wget|-q|-O|/dev/null|http://127.0.0.1:3000/api/health'
}
$expectedBindSources = @{
    edge = @{ '/etc/caddy/Caddyfile' = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Caddyfile')) }
    prometheus = @{
        '/etc/prometheus/prometheus.yml' = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\observability\prometheus\prometheus.yml'))
        '/etc/prometheus/rules' = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\observability\prometheus\rules'))
    }
    blackbox = @{ '/etc/blackbox/blackbox.yml' = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\observability\blackbox\blackbox.yml')) }
    alertmanager = @{ '/etc/alertmanager/alertmanager.yml' = $null }
    grafana = @{
        '/etc/grafana/provisioning' = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\observability\grafana\provisioning'))
        '/var/lib/grafana/dashboards' = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\observability\grafana\dashboards'))
    }
}
$allowedNamedVolumeTargets = @{
    edge = @('/config', '/data'); migrate = @(); server = @(); postgres = @('/var/lib/postgresql/data')
    valkey = @('/data'); minio = @('/data'); prometheus = @('/prometheus'); blackbox = @()
    alertmanager = @('/alertmanager'); grafana = @('/var/lib/grafana')
}
$readOnlyServices = @('alertmanager', 'blackbox', 'edge', 'grafana', 'migrate', 'prometheus', 'server')
foreach ($serviceProperty in $services) {
    $name = $serviceProperty.Name
    $service = $serviceProperty.Value
    Assert-Production ($null -eq (Get-PropertyValue $service 'build')) `
        "Service '$name' must not contain a local build definition in production."
    Assert-Production (-not [bool](Get-PropertyValue $service 'privileged')) "Service '$name' cannot be privileged."
    foreach ($propertyName in @('cap_add', 'devices', 'device_cgroup_rules', 'group_add', 'volumes_from', 'extra_hosts', 'dns', 'dns_search', 'sysctls')) {
        Assert-Production (@((Get-PropertyValue $service $propertyName) | Where-Object { $null -ne $_ }).Count -eq 0) `
            "Service '$name' cannot set '$propertyName'."
    }
    foreach ($propertyName in @('pid', 'ipc', 'network_mode', 'userns_mode', 'uts', 'cgroup', 'cgroup_parent', 'runtime')) {
        Assert-Production ([string](Get-PropertyValue $service $propertyName) -eq '') `
            "Service '$name' cannot override '$propertyName'."
    }
    Assert-Production ($null -eq (Get-PropertyValue $service 'entrypoint')) `
        "Service '$name' cannot override the reviewed image entrypoint."
    $securityOptions = @((Get-PropertyValue $service 'security_opt') | Where-Object { $null -ne $_ })
    Assert-Production (@($securityOptions | Where-Object { [string]$_ -cne 'no-new-privileges:true' }).Count -eq 0) `
        "Service '$name' has an unapproved security option."
    if ($readOnlyServices -contains $name) {
        Assert-Production ([bool](Get-PropertyValue $service 'read_only')) "Service '$name' must use a read-only root filesystem."
    }

    $command = @((Get-PropertyValue $service 'command') | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    $commandText = $command -join '|'
    if ($name -eq 'prometheus') {
        Assert-Production ($command.Count -eq 3 -and
            ($command[0..1] -join '|') -ceq $expectedCommands[$name] -and
            $command[2] -match '^--storage\.tsdb\.retention\.time=(?:[1-9][0-9]*[dhm])$') `
            "Service '$name' command differs from the reviewed command."
    }
    else {
        Assert-Production ($commandText -ceq $expectedCommands[$name]) `
            "Service '$name' command differs from the reviewed command."
    }
    $healthcheck = Get-PropertyValue $service 'healthcheck'
    $healthTest = if ($null -eq $healthcheck) { '' } else {
        @((Get-PropertyValue $healthcheck 'test') | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ }) -join '|'
    }
    Assert-Production ($healthTest -ceq $expectedHealthchecks[$name]) `
        "Service '$name' healthcheck differs from the reviewed command."
    $image = [string](Get-PropertyValue $service 'image')
    if ($name -in @('server', 'migrate')) {
        Assert-Production ($image -match '^ghcr\.io/myucxy/termlink-server:[A-Za-z0-9_.-]+@sha256:[0-9a-f]{64}$') `
            "Service '$name' must use the approved TermLink GHCR repository and immutable digest: $image"
        Assert-Production ($image -notmatch ':latest@' -and $image -notmatch '@sha256:0{64}$') `
            "Service '$name' cannot use a latest tag or the all-zero placeholder digest."
    }
    else {
        Assert-Production ($image -ceq $approvedImages[$name]) `
            "Service '$name' image differs from the reviewed immutable reference: $image"
    }
    $networks = @(Get-NetworkNames $service)
    Assert-Production (@(Compare-Object $expectedNetworks[$name] $networks).Count -eq 0) `
        "Service '$name' has unapproved network membership: $($networks -join ', ')"

    foreach ($volume in @((Get-PropertyValue $service 'volumes') | Where-Object { $null -ne $_ })) {
        $type = [string](Get-PropertyValue $volume 'type')
        $source = [string](Get-PropertyValue $volume 'source')
        $target = [string](Get-PropertyValue $volume 'target')
        if ($type -eq 'volume') {
            Assert-Production ($allowedNamedVolumeTargets[$name] -contains $target) `
                "Service '$name' has an unapproved named volume target: $target"
        }
        elseif ($type -eq 'bind') {
            $serviceBindSources = $expectedBindSources[$name]
            Assert-Production ($null -ne $serviceBindSources -and $serviceBindSources.ContainsKey($target)) `
                "Service '$name' has an unapproved bind mount target: $target"
            $expectedSource = $serviceBindSources[$target]
            Assert-Production ([bool](Get-PropertyValue $volume 'read_only')) `
                "Service '$name' bind mount must be read-only: $target"
            $resolvedSource = [IO.Path]::GetFullPath($source)
            if ($null -ne $expectedSource) {
                Assert-Production ($resolvedSource.Equals($expectedSource, [StringComparison]::OrdinalIgnoreCase)) `
                    "Service '$name' bind mount source differs from the reviewed path: $target"
            }
            Assert-Production (Test-Path -LiteralPath $resolvedSource) `
                "Service '$name' bind mount source does not exist: $resolvedSource"
            Assert-TermLinkPathHasNoReparsePoint -Path $resolvedSource -Root ([IO.Path]::GetPathRoot($resolvedSource)) | Out-Null
        }
        else { throw "Service '$name' uses an unapproved mount type: $type" }
    }

    $ports = @((Get-PropertyValue $service 'ports'))
    foreach ($port in $ports) {
        if ($null -eq $port) { continue }
        $hostIP = [string](Get-PropertyValue $port 'host_ip')
        $target = [int](Get-PropertyValue $port 'target')
        $published = [int](Get-PropertyValue $port 'published')
        switch ($name) {
            'edge' {
                Assert-Production ($target -eq 8443 -and $published -eq 443) `
                    'Edge may publish only host port 443 to container port 8443.'
            }
            { $_ -in @('grafana', 'alertmanager') } {
                Assert-Production ($hostIP -in @('127.0.0.1', '::1')) `
                    "Service '$name' management port must bind loopback."
            }
            default { throw "Service '$name' unexpectedly publishes a host port." }
        }
    }

    $environment = Get-PropertyValue $service 'environment'
    if ($null -ne $environment) {
        foreach ($entry in $environment.PSObject.Properties) {
            Assert-Production ($allowedEnvironment[$name] -contains $entry.Name) `
                "Service '$name' has unapproved environment variable '$($entry.Name)'."
            if ($entry.Name -match '(?i)(password|secret|token|database_url|redis_url|s3_access_key|s3_secret_key)' -and
                $entry.Name -notmatch '(?i)(_FILE|__FILE)$') {
                throw "Service '$name' places secret-like configuration in environment variable '$($entry.Name)'."
            }
            if ($entry.Name -match '(?i)(_FILE|__FILE)$') {
                Assert-Production ([string]$entry.Value -match '^/run/secrets/[a-z0-9][a-z0-9_-]{0,62}$') `
                    "Service '$name' secret-file variable '$($entry.Name)' must reference /run/secrets."
            }
        }
    }
}

$server = Get-PropertyValue $config.services 'server'
Assert-Production ($null -ne $server) 'Server service is required.'
Assert-Production ([bool](Get-PropertyValue $server 'read_only')) 'Server root filesystem must be read-only.'
Assert-Production ([string](Get-PropertyValue $server 'user') -eq '65532:65532') 'Server must run as UID/GID 65532.'
$serverCaps = @((Get-PropertyValue $server 'cap_drop'))
Assert-Production ($serverCaps -contains 'ALL') 'Server must drop all Linux capabilities.'
$serverSecurity = @((Get-PropertyValue $server 'security_opt'))
Assert-Production ($serverSecurity -contains 'no-new-privileges:true') 'Server must set no-new-privileges.'
$serverEnvironment = Get-PropertyValue $server 'environment'
$publicUri = Assert-ProductionHttpsUri -Value ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_PUBLIC_URL')) -Name 'TERMLINK_PUBLIC_URL'
Assert-ProductionHttpsUri -Value ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_OIDC_ISSUER')) -Name 'TERMLINK_OIDC_ISSUER' | Out-Null
if ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_HISTORY_ENABLED') -eq 'true') {
    Assert-ProductionHttpsUri -Value ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_S3_PUBLIC_ENDPOINT')) `
        -Name 'TERMLINK_S3_PUBLIC_ENDPOINT' | Out-Null
}

$privateNetwork = Get-PropertyValue $config.networks 'private'
Assert-Production ($null -ne $privateNetwork -and [bool](Get-PropertyValue $privateNetwork 'internal')) `
    'The database/Valkey private network must remain internal.'
$edgeService = Get-PropertyValue $config.services 'edge'
$edgeEnvironment = Get-PropertyValue $edgeService 'environment'
$acmeCA = [string](Get-PropertyValue $edgeEnvironment 'TERMLINK_ACME_CA')
$acmeEmail = [string](Get-PropertyValue $edgeEnvironment 'TERMLINK_ACME_EMAIL')
Assert-Production ($acmeEmail -match '^[^\s@]+@[^\s@]+\.[^\s@]+$') `
    'Production edge requires a syntactically valid ACME contact email.'
Assert-Production ($acmeCA -eq 'https://acme-v02.api.letsencrypt.org/directory') `
    'Production edge must use the configured production ACME CA.'
Assert-Production ([string](Get-PropertyValue $edgeEnvironment 'TERMLINK_PUBLIC_HOST') -ceq $publicUri.DnsSafeHost) `
    'TERMLINK_PUBLIC_HOST must exactly match the public URL host.'
$historyPublicHost = [string](Get-PropertyValue $edgeEnvironment 'TERMLINK_HISTORY_PUBLIC_HOST')
Assert-ProductionHttpsUri -Value "https://$historyPublicHost" -Name 'TERMLINK_HISTORY_PUBLIC_HOST' | Out-Null

$secretValues = @()
foreach ($secretProperty in @($config.secrets.PSObject.Properties)) {
    $file = [string](Get-PropertyValue $secretProperty.Value 'file')
    Assert-Production ($file -ne '' -and (Test-Path -LiteralPath $file -PathType Leaf)) `
        "Compose secret '$($secretProperty.Name)' does not resolve to an existing file."
    $item = Get-Item -LiteralPath $file -Force
    Assert-Production (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        "Compose secret '$($secretProperty.Name)' cannot be a reparse point."
    Assert-TermLinkPathHasNoReparsePoint -Path $file -Root ([IO.Path]::GetPathRoot($file)) | Out-Null
    $value = ([Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($file))).Trim()
    Assert-Production ($value.Length -ge 12) `
        "Compose secret '$($secretProperty.Name)' must contain at least 12 non-whitespace characters."
    $secretValues += [pscustomobject]@{ Name = $secretProperty.Name; Value = $value }
}

$stringLeaves = @(Get-StringLeaf -Value $config)
foreach ($secret in $secretValues) {
    foreach ($leaf in $stringLeaves) {
        if ($leaf.Value.Contains($secret.Value, [StringComparison]::Ordinal)) {
            throw "Rendered Compose configuration exposes declared secret '$($secret.Name)' at $($leaf.Path)."
        }
    }
}

Write-Host 'TermLink production Compose policy validation passed.'
