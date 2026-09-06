[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EnvFile,
    [string[]]$ComposeFile = @(
        (Join-Path $PSScriptRoot '..\compose\core.compose.yml')
    ),
    [bool]$IncludeEdge = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TermLinkBackup.Common.psm1') -Force

function Assert-Production {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-SecretPathPermissions {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$Directory,
        [Parameter(Mandatory)][string]$DisplayName
    )

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        $mode = [IO.File]::GetUnixFileMode($Path)
        $forbidden = [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor
            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor
            [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        Assert-Production (($mode -band $forbidden) -eq 0) `
            "$DisplayName is group- or world-accessible (mode $mode)."
        if ($Directory) {
            Assert-Production (($mode -band [IO.UnixFileMode]::UserExecute) -ne 0) `
                "$DisplayName directory is not traversable by its owner."
        }
        return
    }

    $allowedSids = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',
        'S-1-5-32-544'
    )
    $acl = Get-Acl -LiteralPath $Path
    Assert-Production $acl.AreAccessRulesProtected `
        "$DisplayName retains inherited Windows permissions."
    foreach ($rule in @($acl.Access)) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        Assert-Production (-not $rule.IsInherited) "$DisplayName retains an inherited Windows ACE."
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) {
            Assert-Production ($allowedSids -contains $sid) `
                "$DisplayName grants access to an unreviewed Windows identity."
        }
    }
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

function Get-SecretMountSignatures {
    param([object]$Service)
    $result = @()
    foreach ($secret in @((Get-PropertyValue $Service 'secrets') | Where-Object { $null -ne $_ })) {
        $source = [string](Get-PropertyValue $secret 'source')
        $target = [string](Get-PropertyValue $secret 'target')
        $uid = [string](Get-PropertyValue $secret 'uid')
        $gid = [string](Get-PropertyValue $secret 'gid')
        $mode = [string](Get-PropertyValue $secret 'mode')
        $result += "$source`:$target`:$uid`:$gid`:$mode"
    }
    return @($result | Sort-Object)
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

function Assert-ProductionStunServers {
    param([string]$Value)

    Assert-Production ($Value.Length -ge 1 -and $Value.Length -le 512) `
        'TERMLINK_DIRECT_STUN_SERVERS must contain one or two bounded STUN endpoints.'
    $endpoints = @($Value.Split(',', [StringSplitOptions]::None))
    Assert-Production ($endpoints.Count -ge 1 -and $endpoints.Count -le 2) `
        'TERMLINK_DIRECT_STUN_SERVERS must contain one or two endpoints.'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($endpoint in $endpoints) {
        $match = [regex]::Match($endpoint,
            '^stun:(?<host>[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?):(?<port>[1-9][0-9]{0,4})$')
        Assert-Production $match.Success `
            'TERMLINK_DIRECT_STUN_SERVERS endpoints must use stun:host:port with a production DNS host.'
        $hostName = $match.Groups['host'].Value
        $port = 0
        Assert-Production ([int]::TryParse($match.Groups['port'].Value, [ref]$port) -and $port -le 65535) `
            'TERMLINK_DIRECT_STUN_SERVERS contains an invalid UDP port.'
        Assert-Production ($hostName.Contains('.') -and
            $hostName -notmatch '(?i)(^localhost$|\.(localhost|invalid|test|example)$|(^|\.)example\.(com|net|org)$)') `
            'TERMLINK_DIRECT_STUN_SERVERS must use production DNS hosts, not local or reserved names.'
        Assert-Production ($seen.Add("$hostName`:$port")) `
            'TERMLINK_DIRECT_STUN_SERVERS contains a duplicate endpoint.'
    }
}

function Convert-ProductionSizeToBytes {
    param([string]$Value, [string]$Name)
    $match = [regex]::Match($Value, '^(?<count>[1-9][0-9]*)(?<unit>[kKmMgG])(?:[bB])?$')
    Assert-Production $match.Success "$Name must use a positive k, m, or g byte-size value."
    $multiplier = switch ($match.Groups['unit'].Value.ToLowerInvariant()) {
        'k' { 1KB }
        'm' { 1MB }
        'g' { 1GB }
    }
    $count = 0L
    Assert-Production ([long]::TryParse($match.Groups['count'].Value, [ref]$count) -and
        $count -le [long]::MaxValue / [long]$multiplier) "$Name exceeds the supported byte-size range."
    return $count * [long]$multiplier
}

function Test-AccountDeletionAES256KeyEncoding {
    param([object]$Value)

    if ($Value -isnot [string]) { return $false }
    $encoded = [string]$Value
    $validEncoding = $encoded -cmatch '^[A-Za-z0-9+/]{43}=$' -or
        $encoded -cmatch '^[A-Za-z0-9_-]{43}=?$'
    if (-not $validEncoding) { return $false }
    $normalized = $encoded.Replace('-', '+').Replace('_', '/')
    while (($normalized.Length % 4) -ne 0) { $normalized += '=' }
    $bytes = $null
    try { $bytes = [Convert]::FromBase64String($normalized) } catch { return $false }
    try { return $bytes.Length -eq 32 }
    finally {
        if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
        $encoded = $null
        $normalized = $null
    }
}

function Assert-AccountDeletionIdentityKeyMaterial {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][int]$CurrentVersion
    )

    if (Test-AccountDeletionAES256KeyEncoding -Value $Value) { return }
    Assert-Production ($Value.StartsWith('[', [StringComparison]::Ordinal)) `
        'Account deletion identity key must be one AES-256 key or a bounded versioned keyring.'
    $parsed = $null
    try { $parsed = @($Value | ConvertFrom-Json -Depth 5) }
    catch {
        throw 'Account deletion identity keyring must be valid strict JSON.'
    }
    Assert-Production ($parsed.Count -ge 1 -and $parsed.Count -le 32) `
        'Account deletion identity keyring must contain between 1 and 32 entries.'
    $versions = [Collections.Generic.HashSet[int]]::new()
    $currentVersionFound = $false
    foreach ($entry in $parsed) {
        Assert-Production ($null -ne $entry -and $entry -is [Management.Automation.PSCustomObject]) `
            'Account deletion identity keyring entries must be JSON objects.'
        $propertyNames = @($entry.PSObject.Properties.Name | Sort-Object)
        Assert-Production (@(Compare-Object @('key', 'version') $propertyNames).Count -eq 0) `
            'Account deletion identity keyring entries must contain only version and key.'
        $versionValue = Get-PropertyValue $entry 'version'
        Assert-Production ($versionValue -is [int] -or $versionValue -is [long]) `
            'Account deletion identity keyring version must be an integer.'
        $version = [long]$versionValue
        Assert-Production ($version -ge 1 -and $version -le 32767) `
            'Account deletion identity keyring version is outside the supported range.'
        Assert-Production ($versions.Add([int]$version)) `
            'Account deletion identity keyring contains a duplicate version.'
        Assert-Production (Test-AccountDeletionAES256KeyEncoding -Value (Get-PropertyValue $entry 'key')) `
            'Account deletion identity keyring contains a key that is not exactly 32 decoded bytes.'
        if ($version -eq $CurrentVersion) { $currentVersionFound = $true }
    }
    Assert-Production $currentVersionFound `
        'Account deletion identity keyring does not contain the configured current version.'
}

$envPath = [IO.Path]::GetFullPath($EnvFile)
Assert-Production (Test-Path -LiteralPath $envPath -PathType Leaf) "Environment file not found: $envPath"
$composeFiles = @(Resolve-TermLinkComposeFiles -ComposeFile $ComposeFile)
$singleNodeProductionPath = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\deploy\single-node-production\compose.yml'))
if ($composeFiles -contains $singleNodeProductionPath) {
    Assert-Production ($composeFiles.Count -eq 1) `
        'The bundled single-node deployment cannot be combined with legacy production overlays.'
    & (Join-Path $PSScriptRoot 'Test-SingleNodeProductionDeployment.ps1') `
        -EnvFile $envPath -ComposeFile $singleNodeProductionPath -IncludeEdge:$IncludeEdge
    return
}
$coreComposePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compose\core.compose.yml'))
$observabilityComposePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compose\observability.compose.yml'))
$externalServicesComposePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compose\external-services.compose.yml'))
$registrationRecoveryComposePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compose\registration-recovery.compose.yml'))
$humanVerificationComposePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compose\human-verification.compose.yml'))
$accountDeletionComposePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compose\account-deletion.compose.yml'))
$directTransportComposePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compose\direct-transport.compose.yml'))
$reviewedComposePaths = @(
    $coreComposePath,
    $observabilityComposePath,
    $externalServicesComposePath,
    $registrationRecoveryComposePath,
    $humanVerificationComposePath,
    $accountDeletionComposePath,
    $directTransportComposePath
)
foreach ($composeFile in $composeFiles) {
    Assert-Production ($reviewedComposePaths -contains $composeFile) `
        "Compose file is not part of the reviewed production matrix: $composeFile"
}
Assert-Production (@($composeFiles | Sort-Object -Unique).Count -eq $composeFiles.Count) `
    'Production Compose files must not be supplied more than once.'
$observabilityOverlayRequested = $composeFiles -contains $observabilityComposePath
$externalServicesOverlayRequested = $composeFiles -contains $externalServicesComposePath
$registrationRecoveryOverlayRequested = $composeFiles -contains $registrationRecoveryComposePath
$humanVerificationOverlayRequested = $composeFiles -contains $humanVerificationComposePath
$accountDeletionOverlayRequested = $composeFiles -contains $accountDeletionComposePath
$directTransportOverlayRequested = $composeFiles -contains $directTransportComposePath
Assert-Production ($composeFiles -contains $coreComposePath) `
    'Production validation requires the reviewed core.compose.yml foundation.'
$arguments = @('compose', '--env-file', $envPath, '--project-name', 'termlink-policy')
foreach ($file in $composeFiles) { $arguments += @('-f', $file) }
if ($IncludeEdge) { $arguments += @('--profile', 'edge') }
$arguments += @('config', '--format', 'json')
$rendered = Invoke-TermLinkDocker -Arguments $arguments
try {
    $config = ($rendered.Output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 100
}
catch {
    throw "Rendered Compose configuration was not valid JSON: $($_.Exception.Message)"
}

$services = @($config.services.PSObject.Properties)
$requiredServices = @('migrate', 'postgres', 'server', 'valkey')
if ($IncludeEdge) { $requiredServices += 'edge' }
if ($observabilityOverlayRequested) {
    $requiredServices += @('alertmanager', 'blackbox', 'grafana', 'prometheus')
}
if ($directTransportOverlayRequested) { $requiredServices += 'direct-valkey' }
$requiredServices = @($requiredServices | Sort-Object)
$actualServices = @($services.Name | Sort-Object)
Assert-Production (@(Compare-Object $requiredServices $actualServices).Count -eq 0) `
    "Production Compose services differ from the reviewed core/overlay selection: $($actualServices -join ', ')"
$approvedImages = @{
    edge = 'caddy:2.10.0-alpine@sha256:ae4458638da8e1a91aafffb231c5f8778e964bca650c8a8cb23a7e8ac557aa3c'
    postgres = 'postgres:17.5-alpine3.22@sha256:6567bca8d7bc8c82c5922425a0baee57be8402df92bae5eacad5f01ae9544daa'
    valkey = 'valkey/valkey:8.1.1-alpine@sha256:6a57d58c0a37cf7acc3045ac0fd6dd91be339774106ab4d9ca088013a096a99f'
    'direct-valkey' = 'valkey/valkey:8.1.1-alpine@sha256:6a57d58c0a37cf7acc3045ac0fd6dd91be339774106ab4d9ca088013a096a99f'
    prometheus = 'prom/prometheus:v2.55.1@sha256:2659f4c2ebb718e7695cb9b25ffa7d6be64db013daba13e05c875451cf51b0d3'
    blackbox = 'prom/blackbox-exporter:v0.25.0@sha256:b04a9fef4fa086a02fc7fcd8dcdbc4b7b35cc30cdee860fdc6a19dd8b208d63e'
    alertmanager = 'prom/alertmanager:v0.27.0@sha256:e13b6ed5cb929eeaee733479dce55e10eb3bc2e9c4586c705a4e8da41e5eacf5'
    grafana = 'grafana/grafana:11.2.0@sha256:408afb9726de5122b00a2576763a8a57a3c86d5b0eff5305bc994ceb3eb96c3f'
}
$expectedNetworks = @{
    edge = @('frontend', 'public'); migrate = @('backend'); server = @('backend', 'egress', 'frontend')
    postgres = @('backend'); valkey = @('backend'); 'direct-valkey' = @('direct-state')
    # Metrics and readiness probes use frontend to reach the application's
    # internal listeners.  Alertmanager also needs egress for configured
    # receivers; no observability service needs backend access.
    prometheus = @('frontend'); blackbox = @('frontend'); alertmanager = @('egress', 'frontend'); grafana = @('frontend')
}
if ($directTransportOverlayRequested) {
    $expectedNetworks.server = @($expectedNetworks.server + 'direct-state' | Sort-Object -Unique)
}
$connectionEnvironment = @(
    'TERMLINK_POSTGRES_CONNECT_TIMEOUT', 'TERMLINK_POSTGRES_HEALTH_CHECK_PERIOD',
    'TERMLINK_POSTGRES_MAX_CONN_IDLE_TIME', 'TERMLINK_POSTGRES_MAX_CONN_LIFETIME',
    'TERMLINK_POSTGRES_MAX_CONNS', 'TERMLINK_POSTGRES_MIN_CONNS',
    'TERMLINK_REDIS_DIAL_TIMEOUT', 'TERMLINK_REDIS_MIN_IDLE_CONNS',
    'TERMLINK_REDIS_POOL_SIZE', 'TERMLINK_REDIS_POOL_TIMEOUT',
    'TERMLINK_REDIS_READ_TIMEOUT', 'TERMLINK_REDIS_WRITE_TIMEOUT'
)
$allowedEnvironment = @{
    edge = @('TERMLINK_ACME_CA', 'TERMLINK_ACME_EMAIL', 'TERMLINK_PUBLIC_HOST')
    migrate = @('TERMLINK_ACCOUNT_DELETION_ENABLED', 'TERMLINK_ANDROID_OIDC_CLIENT_ID', 'TERMLINK_ANDROID_OIDC_REDIRECT_URI',
        'TERMLINK_DATABASE_URL_FILE', 'TERMLINK_LISTEN_ADDR',
        'TERMLINK_LOG_LEVEL', 'TERMLINK_OIDC_ALLOWED_PARTIES', 'TERMLINK_OIDC_CLIENT_ID', 'TERMLINK_OIDC_CLIENT_SECRET_FILE',
        'TERMLINK_OIDC_ISSUER', 'TERMLINK_PUBLIC_URL', 'TERMLINK_REDIS_URL_FILE',
        'TERMLINK_REGISTRATION_RECOVERY_ENABLED', 'TERMLINK_TOKEN_SIGNING_KEY_FILE',
        'TERMLINK_WINDOWS_OIDC_CLIENT_ID', 'TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES', 'TERMLINK_WINDOWS_OIDC_REDIRECT_URI',
        'TERMLINK_WEB_OIDC_CLIENT_ID', 'TERMLINK_WEB_OIDC_REDIRECT_URI', 'TERMLINK_WEB_STEP_UP_OIDC_CLIENT_ID')
    # The server receives the same public OIDC settings as migrate, plus its
    # metrics and optional feature settings. Secret values must remain
    # file-backed; the validation below enforces /run/secrets paths.
    server = @('TERMLINK_ACCOUNT_DELETION_ENABLED', 'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE',
        'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION', 'TERMLINK_ANDROID_OIDC_CLIENT_ID', 'TERMLINK_ANDROID_OIDC_REDIRECT_URI',
        'TERMLINK_APPSHIP_ALLOWED_PRIVATE_IPS', 'TERMLINK_APPSHIP_BASE_URL',
        'TERMLINK_DATABASE_URL_FILE', 'TERMLINK_DIRECT_REDIS_URL_FILE', 'TERMLINK_DIRECT_STUN_SERVERS',
        'TERMLINK_DIRECT_TRANSPORT_ENABLED', 'TERMLINK_LISTEN_ADDR',
        'TERMLINK_HUMAN_VERIFICATION_PROVIDER', 'TERMLINK_HUMAN_VERIFICATION_SECRET_FILE', 'TERMLINK_HUMAN_VERIFICATION_SITE_KEY',
        'TERMLINK_KEYCLOAK_ADMIN_BASE_URL', 'TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID', 'TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE',
        'TERMLINK_LOG_LEVEL', 'TERMLINK_METRICS_LISTEN_ADDR', 'TERMLINK_OIDC_ALLOWED_PARTIES', 'TERMLINK_OIDC_CLIENT_ID',
        'TERMLINK_OIDC_CLIENT_SECRET_FILE', 'TERMLINK_OIDC_ISSUER', 'TERMLINK_PUBLIC_URL',
        'TERMLINK_REDIS_URL_FILE', 'TERMLINK_REGISTRATION_RECOVERY_ENABLED',
        'TERMLINK_TOKEN_SIGNING_KEY_FILE',
        'TERMLINK_WINDOWS_OIDC_CLIENT_ID', 'TERMLINK_WINDOWS_REGISTRATION_OIDC_PARTIES', 'TERMLINK_WINDOWS_OIDC_REDIRECT_URI',
        'TERMLINK_WEB_OIDC_CLIENT_ID', 'TERMLINK_WEB_OIDC_REDIRECT_URI', 'TERMLINK_WEB_STEP_UP_OIDC_CLIENT_ID')
    postgres = @('POSTGRES_DB', 'POSTGRES_PASSWORD_FILE', 'POSTGRES_USER')
    valkey = @('TERMLINK_VALKEY_MAXMEMORY')
    'direct-valkey' = @('TERMLINK_DIRECT_VALKEY_MAXMEMORY')
    prometheus = @(); blackbox = @(); alertmanager = @()
    grafana = @('GF_AUTH_ANONYMOUS_ENABLED', 'GF_PATHS_DATA', 'GF_PATHS_LOGS', 'GF_PATHS_PLUGINS',
        'GF_PATHS_PROVISIONING', 'GF_SECURITY_ADMIN_PASSWORD__FILE', 'GF_SECURITY_ADMIN_USER', 'GF_USERS_ALLOW_SIGN_UP')
}
$allowedEnvironment.migrate = @($allowedEnvironment.migrate + $connectionEnvironment | Sort-Object -Unique)
$allowedEnvironment.server = @($allowedEnvironment.server + $connectionEnvironment | Sort-Object -Unique)
# Keep credential-bearing environment names explicit per service.  The
# rendered allow-list above contains public settings (including OIDC client
# IDs/redirect URIs) as well as file-backed credentials.  This second list is
# a regression guard against accidentally granting a secret to the migration
# job or introducing a new secret-like variable without a role review.
$allowedSecretEnvironment = @{
    edge = @()
    migrate = @('TERMLINK_DATABASE_URL_FILE', 'TERMLINK_OIDC_CLIENT_SECRET_FILE',
        'TERMLINK_REDIS_URL_FILE', 'TERMLINK_TOKEN_SIGNING_KEY_FILE')
    server = @('TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE', 'TERMLINK_DATABASE_URL_FILE', 'TERMLINK_HUMAN_VERIFICATION_SECRET_FILE', 'TERMLINK_OIDC_CLIENT_SECRET_FILE',
        'TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE', 'TERMLINK_DIRECT_REDIS_URL_FILE', 'TERMLINK_REDIS_URL_FILE',
        'TERMLINK_TOKEN_SIGNING_KEY_FILE')
    postgres = @('POSTGRES_PASSWORD_FILE')
    valkey = @()
    'direct-valkey' = @()
    prometheus = @(); blackbox = @(); alertmanager = @()
    grafana = @('GF_SECURITY_ADMIN_PASSWORD__FILE')
}
$credentialNamePattern = '(?i)(password|secret|token|identity_key_file|database_url|redis_url)'
$nonCredentialSecretEnvironment = @()
foreach ($serviceName in $allowedEnvironment.Keys) {
    $declaredSecrets = @($allowedSecretEnvironment[$serviceName] | Sort-Object)
    $secretLikeNames = @($allowedEnvironment[$serviceName] |
        Where-Object {
            $_ -match $credentialNamePattern -and $nonCredentialSecretEnvironment -cnotcontains $_
        } |
        Sort-Object)
    Assert-Production (@(Compare-Object $declaredSecrets $secretLikeNames).Count -eq 0) `
        "Service '$serviceName' secret environment allow-list is not explicit or minimal."
}
$expectedCommands = @{
    edge = 'caddy|run|--config|/etc/caddy/Caddyfile|--adapter|caddyfile'; migrate = 'migrate'; server = ''
    postgres = ''; valkey = '/bin/sh|-ec'; 'direct-valkey' = '/bin/sh|-ec'
    prometheus = '--config.file=/etc/prometheus/prometheus.yml|--storage.tsdb.path=/prometheus'
    blackbox = '--config.file=/etc/blackbox/blackbox.yml'
    alertmanager = '--config.file=/etc/alertmanager/alertmanager.yml|--storage.path=/alertmanager'; grafana = ''
}
$expectedHealthchecks = @{
    edge = 'CMD-SHELL|wget -q -O - http://127.0.0.1:8081/readyz >/dev/null'
    migrate = ''; server = 'CMD|/termlink-server|healthcheck'
    postgres = 'CMD-SHELL|pg_isready -U $${POSTGRES_USER} -d $${POSTGRES_DB}'
    valkey = 'CMD-SHELL|REDISCLI_AUTH="$$(cat /run/secrets/valkey_password)" valkey-cli --user default ping | grep -qx PONG'
    'direct-valkey' = 'CMD-SHELL|REDISCLI_AUTH="$$(cat /run/secrets/direct_valkey_password)" valkey-cli --user termlink-direct ping | grep -qx PONG'
    prometheus = 'CMD|wget|-q|-O|/dev/null|http://127.0.0.1:9090/-/ready'
    blackbox = 'CMD|/bin/blackbox_exporter|--version'
    alertmanager = 'CMD|wget|-q|-O|/dev/null|http://127.0.0.1:9093/-/ready'
    grafana = 'CMD|wget|-q|-O|/dev/null|http://127.0.0.1:3000/api/health'
}
$expectedValkeyScriptLines = @(
    'password="$$(cat /run/secrets/valkey_password)"',
    'test -n "$$password"',
    'case "$$password" in *[!A-Za-z0-9_-]*) exit 1;; esac',
    'maxmemory="$${TERMLINK_VALKEY_MAXMEMORY}"',
    'case "$$maxmemory" in ''''|*[!0-9kKmMgGtTbB]*) exit 1;; esac',
    'umask 077',
    'printf ''user default on >%s ~* &* +@all\n'' "$$password" > /run/valkey/users.acl',
    'unset password',
    'exec valkey-server --appendonly yes --aclfile /run/valkey/users.acl \',
    '--maxmemory "$$maxmemory" --maxmemory-policy noeviction'
)
$expectedDirectValkeyScriptLines = @(
    'password="$$(cat /run/secrets/direct_valkey_password)"',
    'test -n "$$password"',
    'case "$$password" in *[!A-Za-z0-9_-]*) exit 1;; esac',
    'maxmemory="$${TERMLINK_DIRECT_VALKEY_MAXMEMORY}"',
    'case "$$maxmemory" in ''''|*[!0-9kKmMgGtTbB]*) exit 1;; esac',
    'umask 077',
    'printf ''user default off\nuser termlink-direct on >%s ~termlink:direct:v1:* ~proof:* &termlink:direct:v1:mailbox:* +hello +ping +client|setinfo +get +set +del +exists +incr +psetex +pexpire +pexpireat +hget +hset +hexists +hincrby +zadd +zcard +zrem +zremrangebyscore +time +publish +subscribe +unsubscribe +eval +evalsha\n'' "$$password" > /run/valkey/users.acl',
    'unset password',
    'exec valkey-server --appendonly yes --aclfile /run/valkey/users.acl \',
    '--maxmemory "$$maxmemory" --maxmemory-policy noeviction'
)
$expectedSecretMounts = @{
    edge = @()
    migrate = @(
        'database_url:database_url:65532:65532:0400',
        'oidc_client_secret:oidc_client_secret:65532:65532:0400',
        'redis_url:redis_url:65532:65532:0400',
        'token_signing_key:token_signing_key:65532:65532:0400')
    server = @(
        'database_url:database_url:65532:65532:0400',
        'oidc_client_secret:oidc_client_secret:65532:65532:0400',
        'redis_url:redis_url:65532:65532:0400',
        'token_signing_key:token_signing_key:65532:65532:0400')
    postgres = @('postgres_password:postgres_password:::0400')
    valkey = @('valkey_password:valkey_password:999:1000:0400')
    'direct-valkey' = @('direct_valkey_password:direct_valkey_password:999:1000:0400')
    prometheus = @(); blackbox = @(); alertmanager = @()
    grafana = @('grafana_admin_password:grafana_admin_password:472:472:0400')
}
if ($registrationRecoveryOverlayRequested) {
    $expectedSecretMounts.server = @($expectedSecretMounts.server +
        'keycloak_admin_client_secret:keycloak_admin_client_secret:65532:65532:0400' | Sort-Object)
}
if ($humanVerificationOverlayRequested) {
    $expectedSecretMounts.server = @($expectedSecretMounts.server +
        'human_verification_secret:human_verification_secret:65532:65532:0400' | Sort-Object)
}
if ($accountDeletionOverlayRequested) {
    $expectedSecretMounts.server = @($expectedSecretMounts.server +
        'account_deletion_identity_key:account_deletion_identity_key:65532:65532:0400' | Sort-Object)
    if (-not $registrationRecoveryOverlayRequested) {
        $expectedSecretMounts.server = @($expectedSecretMounts.server +
            'keycloak_admin_client_secret:keycloak_admin_client_secret:65532:65532:0400' | Sort-Object)
    }
}
if ($directTransportOverlayRequested) {
    $expectedSecretMounts.server = @($expectedSecretMounts.server +
        'direct_redis_url:direct_redis_url:65532:65532:0400' | Sort-Object)
}
$edgeCaddyfilePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\compose\external-services.Caddyfile'))
$expectedBindSources = @{
    edge = @{ '/etc/caddy/Caddyfile' = $edgeCaddyfilePath }
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
    valkey = @('/data'); 'direct-valkey' = @('/data'); prometheus = @('/prometheus'); blackbox = @()
    alertmanager = @('/alertmanager'); grafana = @('/var/lib/grafana')
}
$readOnlyServices = @('alertmanager', 'blackbox', 'direct-valkey', 'edge', 'grafana', 'migrate', 'prometheus', 'server', 'valkey')
$expectedUsers = @{
    migrate = '65532:65532'
    server = '65532:65532'
    valkey = '999:1000'
    'direct-valkey' = '999:1000'
}
$expectedTmpfs = @{
    edge = @('/tmp:rw,noexec,nosuid,size=16m')
    migrate = @('/tmp:rw,noexec,nosuid,size=16m')
    server = @('/tmp:rw,noexec,nosuid,size=16m')
    valkey = @('/run/valkey:rw,noexec,nosuid,size=1m,uid=999,gid=1000,mode=0700')
    'direct-valkey' = @('/run/valkey:rw,noexec,nosuid,size=1m,uid=999,gid=1000,mode=0700')
    prometheus = @('/tmp:rw,noexec,nosuid,size=16m')
    blackbox = @('/tmp:rw,noexec,nosuid,size=8m')
    alertmanager = @('/tmp:rw,noexec,nosuid,size=16m')
    grafana = @('/tmp:rw,noexec,nosuid,size=32m')
}
$resourcePolicyServices = @(
    'alertmanager', 'blackbox', 'edge', 'grafana', 'migrate',
    'postgres', 'prometheus', 'server', 'valkey', 'direct-valkey'
)
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
        $capDrop = @((Get-PropertyValue $service 'cap_drop') | Where-Object { $null -ne $_ })
        Assert-Production ($capDrop.Count -eq 1 -and [string]$capDrop[0] -ceq 'ALL') `
            "Service '$name' must drop every Linux capability."
        Assert-Production ($securityOptions.Count -eq 1 -and [string]$securityOptions[0] -ceq 'no-new-privileges:true') `
            "Service '$name' must set only no-new-privileges."
    }
    elseif ($null -ne (Get-PropertyValue $service 'read_only')) {
        Assert-Production (-not [bool](Get-PropertyValue $service 'read_only')) `
            "Stateful service '$name' must not override the reviewed root filesystem mode."
    }
    if ($expectedUsers.ContainsKey($name)) {
        Assert-Production ([string](Get-PropertyValue $service 'user') -ceq $expectedUsers[$name]) `
            "Service '$name' must run with the reviewed UID/GID $($expectedUsers[$name])."
    }
    if ($expectedTmpfs.ContainsKey($name)) {
        $actualTmpfs = @((Get-PropertyValue $service 'tmpfs') | ForEach-Object { [string]$_ } | Sort-Object)
        Assert-Production (@(Compare-Object $expectedTmpfs[$name] $actualTmpfs).Count -eq 0) `
            "Service '$name' tmpfs configuration differs from the reviewed least-privilege contract."
    }
    if ($resourcePolicyServices -contains $name) {
        $memoryLimit = 0L
        $memoryReservation = 0L
        $cpuLimit = 0.0
        $pidLimit = 0
        Assert-Production ([long]::TryParse([string](Get-PropertyValue $service 'mem_limit'), [ref]$memoryLimit) -and
            $memoryLimit -ge 32MB -and $memoryLimit -le 64GB) `
            "Service '$name' must have a bounded 32 MiB to 64 GiB memory limit."
        Assert-Production ([long]::TryParse([string](Get-PropertyValue $service 'mem_reservation'), [ref]$memoryReservation) -and
            $memoryReservation -ge 16MB -and $memoryReservation -le $memoryLimit) `
            "Service '$name' must have a positive memory reservation no greater than its limit."
        Assert-Production ([double]::TryParse([string](Get-PropertyValue $service 'cpus'),
            [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$cpuLimit) -and
            $cpuLimit -ge 0.05 -and $cpuLimit -le 16.0) `
            "Service '$name' must have a bounded 0.05 to 16 CPU limit."
        Assert-Production ([int]::TryParse([string](Get-PropertyValue $service 'pids_limit'), [ref]$pidLimit) -and
            $pidLimit -ge 32 -and $pidLimit -le 4096) `
            "Service '$name' must have a bounded 32 to 4096 PID limit."

        $logging = Get-PropertyValue $service 'logging'
        $loggingOptions = Get-PropertyValue $logging 'options'
        $maxLogFiles = 0
        Assert-Production ([string](Get-PropertyValue $logging 'driver') -ceq 'json-file') `
            "Service '$name' must use the reviewed rotating json-file log driver."
        $maxLogBytes = Convert-ProductionSizeToBytes `
            -Value ([string](Get-PropertyValue $loggingOptions 'max-size')) -Name "Service '$name' log max-size"
        Assert-Production ($maxLogBytes -ge 1MB -and $maxLogBytes -le 1GB) `
            "Service '$name' log max-size must remain between 1 MiB and 1 GiB."
        Assert-Production ([int]::TryParse([string](Get-PropertyValue $loggingOptions 'max-file'), [ref]$maxLogFiles) -and
            $maxLogFiles -ge 1 -and $maxLogFiles -le 20) `
            "Service '$name' log max-file must remain between 1 and 20."
        if ($name -eq 'postgres') {
            $sharedMemory = 0L
            Assert-Production ([long]::TryParse([string](Get-PropertyValue $service 'shm_size'), [ref]$sharedMemory) -and
                $sharedMemory -ge 16MB -and $sharedMemory -le $memoryLimit) `
                "Service 'postgres' shared memory must remain between 16 MiB and its memory limit."
        }
        elseif ($name -in @('valkey', 'direct-valkey')) {
            $valkeyEnvironment = Get-PropertyValue $service 'environment'
            $maxMemoryVariable = if ($name -ceq 'direct-valkey') {
                'TERMLINK_DIRECT_VALKEY_MAXMEMORY'
            } else { 'TERMLINK_VALKEY_MAXMEMORY' }
            $valkeyMaxMemory = Convert-ProductionSizeToBytes `
                -Value ([string](Get-PropertyValue $valkeyEnvironment $maxMemoryVariable)) `
                -Name $maxMemoryVariable
            Assert-Production ($valkeyMaxMemory -ge 16MB -and $valkeyMaxMemory -le $memoryLimit) `
                "$maxMemoryVariable must remain between 16 MiB and the Valkey container memory limit."
        }
    }

    $command = @((Get-PropertyValue $service 'command') | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
    $commandText = $command -join '|'
    if ($name -in @('valkey', 'direct-valkey')) {
        $actualScriptLines = if ($command.Count -eq 3) {
            @($command[2] -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        } else { @() }
        $expectedScriptLines = if ($name -ceq 'direct-valkey') {
            $expectedDirectValkeyScriptLines
        } else { $expectedValkeyScriptLines }
        Assert-Production ($command.Count -eq 3 -and $command[0] -ceq '/bin/sh' -and $command[1] -ceq '-ec' -and
            @(Compare-Object $expectedScriptLines $actualScriptLines).Count -eq 0) `
            "Service '$name' command differs from the reviewed ACL bootstrap."
    }
    elseif ($name -eq 'prometheus') {
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
        Assert-Production ($image -match '^docker\.io/myucxy/termlink:[A-Za-z0-9_.-]+@sha256:[0-9a-f]{64}$') `
            "Service '$name' must use the approved TermLink Docker Hub repository and immutable digest: $image"
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

    $secretMounts = @(Get-SecretMountSignatures $service)
    Assert-Production (@(Compare-Object $expectedSecretMounts[$name] $secretMounts).Count -eq 0) `
        "Service '$name' secret mounts differ from the reviewed least-privilege mapping: $($secretMounts -join ', ')"

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
            if ($name -eq 'alertmanager' -and $target -eq '/etc/alertmanager/alertmanager.yml') {
                $bundledPlaceholder = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\observability\alertmanager\alertmanager.yml'))
                Assert-Production (Test-Path -LiteralPath $resolvedSource -PathType Leaf) `
                    'Alertmanager production configuration must be a regular file.'
                Assert-Production (-not $resolvedSource.Equals($bundledPlaceholder, [StringComparison]::OrdinalIgnoreCase)) `
                    'Observability requires an explicit reviewed Alertmanager receiver, not the bundled no-delivery placeholder.'
                Assert-SecretPathPermissions -Path $resolvedSource -Directory $false `
                    -DisplayName 'Alertmanager production configuration'
            }
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
            if ($entry.Name -match '(?i)(password|secret|token|identity_key_file|database_url|redis_url)' -and
                $nonCredentialSecretEnvironment -cnotcontains $entry.Name -and
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
$valkeyService = Get-PropertyValue $config.services 'valkey'
Assert-Production ([bool](Get-PropertyValue $valkeyService 'read_only')) 'Valkey root filesystem must be read-only.'
Assert-Production ([string](Get-PropertyValue $valkeyService 'user') -ceq '999:1000') 'Valkey must run as its fixed non-root UID/GID.'
$valkeyCaps = @((Get-PropertyValue $valkeyService 'cap_drop'))
Assert-Production ($valkeyCaps -contains 'ALL') 'Valkey must drop all Linux capabilities.'
$valkeySecurity = @((Get-PropertyValue $valkeyService 'security_opt'))
Assert-Production ($valkeySecurity -contains 'no-new-privileges:true') 'Valkey must set no-new-privileges.'
$directValkeyService = Get-PropertyValue $config.services 'direct-valkey'
if ($directTransportOverlayRequested) {
    Assert-Production ($null -ne $directValkeyService) 'The Direct overlay must provide its independent Valkey service.'
    Assert-Production ([bool](Get-PropertyValue $directValkeyService 'read_only')) `
        'Direct Valkey root filesystem must be read-only.'
    Assert-Production ([string](Get-PropertyValue $directValkeyService 'user') -ceq '999:1000') `
        'Direct Valkey must run as its fixed non-root UID/GID.'
    Assert-Production ([string](Get-PropertyValue $directValkeyService 'restart') -ceq 'unless-stopped') `
        'Direct Valkey must use the reviewed restart policy.'
    Assert-Production ($null -eq (Get-PropertyValue $directValkeyService 'container_name') -and
        $null -eq (Get-PropertyValue $directValkeyService 'hostname')) `
        'Direct Valkey must remain addressable by its shared Compose service name for multi-replica servers.'
    Assert-Production ($null -eq (Get-PropertyValue $directValkeyService 'depends_on')) `
        'Direct Valkey must not depend on the Relay data plane.'
    $serverDependencies = Get-PropertyValue $server 'depends_on'
    Assert-Production ($null -eq (Get-PropertyValue $serverDependencies 'direct-valkey')) `
        'Direct Valkey health must never gate Relay server startup.'
} else {
    Assert-Production ($null -eq $directValkeyService) `
        'Direct Valkey must appear only when direct-transport.compose.yml is selected.'
}
$serverEnvironment = Get-PropertyValue $server 'environment'
$publicUri = Assert-ProductionHttpsUri -Value ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_PUBLIC_URL')) -Name 'TERMLINK_PUBLIC_URL'
$oidcIssuerUri = Assert-ProductionHttpsUri -Value ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_OIDC_ISSUER')) -Name 'TERMLINK_OIDC_ISSUER'
$migration = Get-PropertyValue $config.services 'migrate'
$migrationEnvironment = Get-PropertyValue $migration 'environment'
Assert-Production ([string](Get-PropertyValue $migrationEnvironment 'TERMLINK_REGISTRATION_RECOVERY_ENABLED') -ceq 'false') `
    'The migration job must never enable registration recovery.'
Assert-Production ([string](Get-PropertyValue $migrationEnvironment 'TERMLINK_ACCOUNT_DELETION_ENABLED') -ceq 'false') `
    'The migration job must never enable account deletion processing.'
foreach ($name in @(
    'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE', 'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION',
    'TERMLINK_HUMAN_VERIFICATION_PROVIDER', 'TERMLINK_HUMAN_VERIFICATION_SECRET_FILE',
    'TERMLINK_HUMAN_VERIFICATION_SITE_KEY',
    'TERMLINK_KEYCLOAK_ADMIN_BASE_URL', 'TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID',
    'TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE',
    'TERMLINK_DIRECT_REDIS_URL_FILE', 'TERMLINK_DIRECT_STUN_SERVERS', 'TERMLINK_DIRECT_TRANSPORT_ENABLED'
)) {
    Assert-Production ($null -eq (Get-PropertyValue $migrationEnvironment $name)) `
        "The migration job must not receive recovery authority '$name'."
}
$directTransportEnabled = [string](Get-PropertyValue $serverEnvironment 'TERMLINK_DIRECT_TRANSPORT_ENABLED') -ceq 'true'
Assert-Production ($directTransportEnabled -eq $directTransportOverlayRequested) `
    'TERMLINK_DIRECT_TRANSPORT_ENABLED must exactly match direct-transport.compose.yml.'
if ($directTransportEnabled) {
    Assert-Production ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_DIRECT_REDIS_URL_FILE') -ceq
        '/run/secrets/direct_redis_url') `
        'Direct transport must use only its reviewed file-backed independent Valkey URL.'
    Assert-ProductionStunServers -Value ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_DIRECT_STUN_SERVERS'))
} else {
    foreach ($name in @(
        'TERMLINK_DIRECT_REDIS_URL_FILE', 'TERMLINK_DIRECT_STUN_SERVERS', 'TERMLINK_DIRECT_TRANSPORT_ENABLED'
    )) {
        Assert-Production ($null -eq (Get-PropertyValue $serverEnvironment $name)) `
            "Core deployment unexpectedly includes Direct transport setting '$name'."
    }
}
$recoveryEnabled = [string](Get-PropertyValue $serverEnvironment 'TERMLINK_REGISTRATION_RECOVERY_ENABLED') -ceq 'true'
Assert-Production ($recoveryEnabled -eq $registrationRecoveryOverlayRequested) `
    'TERMLINK_REGISTRATION_RECOVERY_ENABLED must exactly match registration-recovery.compose.yml.'
$accountDeletionEnabled = [string](Get-PropertyValue $serverEnvironment 'TERMLINK_ACCOUNT_DELETION_ENABLED') -ceq 'true'
Assert-Production ($accountDeletionEnabled -eq $accountDeletionOverlayRequested) `
    'TERMLINK_ACCOUNT_DELETION_ENABLED must exactly match account-deletion.compose.yml.'
$humanVerificationProvider = [string](Get-PropertyValue $serverEnvironment 'TERMLINK_HUMAN_VERIFICATION_PROVIDER')
$humanVerificationEnabled = $humanVerificationProvider -ceq 'turnstile'
Assert-Production ($humanVerificationEnabled -eq $humanVerificationOverlayRequested) `
    'TERMLINK_HUMAN_VERIFICATION_PROVIDER must exactly match human-verification.compose.yml.'
if ($humanVerificationEnabled) {
    $humanVerificationSiteKey = [string](Get-PropertyValue $serverEnvironment 'TERMLINK_HUMAN_VERIFICATION_SITE_KEY')
    Assert-Production ($humanVerificationSiteKey -cmatch '^\S{1,256}$') `
        'Turnstile requires a bounded non-empty public site key.'
    Assert-Production ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_HUMAN_VERIFICATION_SECRET_FILE') -ceq
        '/run/secrets/human_verification_secret') `
        'Turnstile must use only its reviewed file-backed secret.'
} else {
    foreach ($name in @(
        'TERMLINK_HUMAN_VERIFICATION_PROVIDER', 'TERMLINK_HUMAN_VERIFICATION_SECRET_FILE',
        'TERMLINK_HUMAN_VERIFICATION_SITE_KEY'
    )) {
        Assert-Production ($null -eq (Get-PropertyValue $serverEnvironment $name)) `
            "Core deployment unexpectedly includes human verification setting '$name'."
    }
}
$keycloakAdminRequested = $recoveryEnabled -or $accountDeletionEnabled
if ($keycloakAdminRequested) {
    Assert-Production ($oidcIssuerUri.AbsolutePath -match '^/realms/[^/]+/?$' -and
        $oidcIssuerUri.Query -eq '' -and $oidcIssuerUri.Fragment -eq '' -and $oidcIssuerUri.UserInfo -eq '') `
        'Keycloak administration requires a realm issuer URL ending in /realms/<realm>.'
    $recoveryClientID = [string](Get-PropertyValue $serverEnvironment 'TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID')
    Assert-Production ($recoveryClientID -cmatch '^[A-Za-z0-9._-]{1,128}$') `
        'Keycloak administration requires a bounded service-account client ID.'
    Assert-Production ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE') -ceq
        '/run/secrets/keycloak_admin_client_secret') `
        'Keycloak administration must use only its reviewed file-backed client secret.'
    $adminBaseURL = [string](Get-PropertyValue $serverEnvironment 'TERMLINK_KEYCLOAK_ADMIN_BASE_URL')
    if ($adminBaseURL -ne '') {
        $adminBaseUri = Assert-ProductionHttpsUri -Value $adminBaseURL -Name 'TERMLINK_KEYCLOAK_ADMIN_BASE_URL'
        Assert-Production ($adminBaseUri.Query -eq '' -and $adminBaseUri.Fragment -eq '' -and $adminBaseUri.UserInfo -eq '') `
            'TERMLINK_KEYCLOAK_ADMIN_BASE_URL must not contain user info, query, or fragment data.'
    }
} else {
    foreach ($name in @(
        'TERMLINK_KEYCLOAK_ADMIN_BASE_URL', 'TERMLINK_KEYCLOAK_ADMIN_CLIENT_ID',
        'TERMLINK_KEYCLOAK_ADMIN_CLIENT_SECRET_FILE'
    )) {
        Assert-Production ($null -eq (Get-PropertyValue $serverEnvironment $name)) `
            "Core deployment unexpectedly includes Keycloak administration setting '$name'."
    }
}
if ($accountDeletionEnabled) {
    Assert-Production ([string](Get-PropertyValue $serverEnvironment 'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE') -ceq
        '/run/secrets/account_deletion_identity_key') `
        'Account deletion must use only its reviewed file-backed identity key.'
    $identityKeyVersion = 0
    Assert-Production ([int]::TryParse(
        [string](Get-PropertyValue $serverEnvironment 'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION'),
        [ref]$identityKeyVersion) -and $identityKeyVersion -ge 1) `
        'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION must be a positive integer.'
} else {
    foreach ($name in @(
        'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_FILE', 'TERMLINK_ACCOUNT_DELETION_IDENTITY_KEY_VERSION'
    )) {
        Assert-Production ($null -eq (Get-PropertyValue $serverEnvironment $name)) `
            "Core deployment unexpectedly includes account-deletion setting '$name'."
    }
}
foreach ($name in @(
    'TERMLINK_HISTORY_ENABLED', 'TERMLINK_S3_ACCESS_KEY_FILE', 'TERMLINK_S3_BUCKET',
    'TERMLINK_S3_ENDPOINT', 'TERMLINK_S3_PUBLIC_ENDPOINT', 'TERMLINK_S3_REGION', 'TERMLINK_S3_SECRET_KEY_FILE'
)) {
    Assert-Production ($null -eq (Get-PropertyValue $serverEnvironment $name)) `
        "Object-storage setting '$name' must not be present after server-side storage retirement."
}
$metricsAddress = [string](Get-PropertyValue $serverEnvironment 'TERMLINK_METRICS_LISTEN_ADDR')
if ($observabilityOverlayRequested) {
    Assert-Production ($metricsAddress -ceq ':9090') `
        'The observability overlay must expose metrics only on the reviewed internal listener :9090.'
} else {
    Assert-Production ($metricsAddress -eq '') `
        'Core deployments must not enable a metrics listener without the observability overlay.'
}
$frontendNetwork = Get-PropertyValue $config.networks 'frontend'
Assert-Production ($null -ne $frontendNetwork -and [bool](Get-PropertyValue $frontendNetwork 'internal')) `
    'The edge/application frontend network must remain internal.'
$backendNetwork = Get-PropertyValue $config.networks 'backend'
Assert-Production ($null -ne $backendNetwork -and [bool](Get-PropertyValue $backendNetwork 'internal')) `
    'The PostgreSQL/Valkey backend network must remain internal.'
$expectedNetworkNames = @('backend', 'egress', 'frontend')
if ($IncludeEdge) { $expectedNetworkNames += 'public' }
if ($directTransportOverlayRequested) { $expectedNetworkNames += 'direct-state' }
$actualNetworkNames = @($config.networks.PSObject.Properties.Name | Sort-Object)
Assert-Production (@(Compare-Object ($expectedNetworkNames | Sort-Object) $actualNetworkNames).Count -eq 0) `
    "Production Compose networks differ from the selected reviewed topology: $($actualNetworkNames -join ', ')"
$directStateNetwork = Get-PropertyValue $config.networks 'direct-state'
Assert-Production ($null -eq (Get-PropertyValue $config.networks 'object') -and
    $null -eq (Get-PropertyValue $config.networks 'object-control')) `
    'Production deployments must not retain retired object-store networks.'
if ($directTransportOverlayRequested) {
    Assert-Production ($null -ne $directStateNetwork -and [bool](Get-PropertyValue $directStateNetwork 'internal')) `
        'The server/Direct-Valkey state network must remain internal.'
} else {
    Assert-Production ($null -eq $directStateNetwork) `
        'Core deployments must not retain the optional Direct state network.'
}
Assert-Production ($null -eq (Get-PropertyValue $config.networks 'private')) `
    'The legacy private network name must not remain in production Compose.'
$edgeService = Get-PropertyValue $config.services 'edge'
if ($IncludeEdge) {
    Assert-Production ($null -ne $edgeService) 'The requested edge profile did not render the edge service.'
    $edgeEnvironment = Get-PropertyValue $edgeService 'environment'
    $acmeCA = [string](Get-PropertyValue $edgeEnvironment 'TERMLINK_ACME_CA')
    $acmeEmail = [string](Get-PropertyValue $edgeEnvironment 'TERMLINK_ACME_EMAIL')
    Assert-Production ($acmeEmail -match '^[^\s@]+@[^\s@]+\.[^\s@]+$') `
        'Production edge requires a syntactically valid ACME contact email.'
    Assert-Production ($acmeCA -eq 'https://acme-v02.api.letsencrypt.org/directory') `
        'Production edge must use the configured production ACME CA.'
    Assert-Production ([string](Get-PropertyValue $edgeEnvironment 'TERMLINK_PUBLIC_HOST') -ceq $publicUri.DnsSafeHost) `
        'TERMLINK_PUBLIC_HOST must exactly match the public URL host.'
    Assert-Production ($null -eq (Get-PropertyValue $edgeEnvironment 'TERMLINK_HISTORY_PUBLIC_HOST')) `
        'The edge must not expose the retired history object host.'
}

$caddyfilePath = $edgeCaddyfilePath
$caddyfileText = [IO.File]::ReadAllText($caddyfilePath)
Assert-Production ($caddyfileText -match '(?m)^\s*@internal path /metrics /metrics/\* /debug /debug/\*\s*$' -and
    $caddyfileText -match '(?m)^\s*respond @internal 404\s*$') `
    'The application edge must keep metrics and debug routes private.'
Assert-Production ($caddyfileText -match '(?m)^\s*Strict-Transport-Security "max-age=31536000; includeSubDomains"\s*$') `
    'The application edge must retain the reviewed HSTS policy.'
Assert-Production ($caddyfileText -match '(?ms)fields\s*\{.*?request delete.*?url delete.*?uri delete.*?query delete.*?\}') `
    'Caddy runtime logs must remove request URLs and query credentials.'
Assert-Production ($caddyfileText -notmatch '(?i)minio|termlink-history|X-Amz-') `
    'The application edge must not retain object-storage routes or signing parameters.'
$acceptanceProxy = Join-Path $PSScriptRoot '..\acceptance\LuckyUpstream.Caddyfile'
$acceptanceProxyText = [IO.File]::ReadAllText([IO.Path]::GetFullPath($acceptanceProxy))
Assert-Production ($acceptanceProxyText -notmatch '(?i)minio|termlink-history|X-Amz-') `
    "Acceptance proxy '$acceptanceProxy' must not retain object-storage routes."
Assert-Production ($acceptanceProxyText -match '(?ms)@serverMutation not method GET HEAD OPTIONS.*?forward_auth @serverMutation server:8080\s*\{\s*uri /readyz\s*lb_try_duration 30s\s*lb_try_interval 250ms') `
    "Acceptance proxy '$acceptanceProxy' must gate mutations through a body-safe readiness request."
Assert-Production ($acceptanceProxyText -match '(?ms)@serverRead method GET HEAD OPTIONS.*?reverse_proxy @serverRead server:8080\s*\{\s*lb_try_duration 30s\s*lb_try_interval 250ms') `
    "Acceptance proxy '$acceptanceProxy' must safely retry read-only requests during Server bootstrap."
Assert-Production ($acceptanceProxyText -match '(?ms)request body untouched.*?reverse_proxy server:8080\s*\{(?!.*?lb_try_duration).*?header_up X-Forwarded-Proto') `
    "Acceptance proxy '$acceptanceProxy' must not replay mutating requests after readiness succeeds."

# This is the key boundary of the production topology: the public edge may
# reach PostgreSQL and Valkey only through the application.
$edgeNetworks = if ($IncludeEdge) { @(Get-NetworkNames $edgeService) } else { @() }
if ($IncludeEdge) {
    $edgeDataServices = @('postgres', 'valkey')
    if ($directTransportOverlayRequested) { $edgeDataServices += 'direct-valkey' }
    foreach ($dataServiceName in $edgeDataServices) {
        $dataService = Get-PropertyValue $config.services $dataServiceName
        $sharedNetworks = @((Get-NetworkNames $dataService) | Where-Object { $edgeNetworks -contains $_ })
        Assert-Production ($sharedNetworks.Count -eq 0) `
            "Edge must not share a Docker network with data service '$dataServiceName': $($sharedNetworks -join ', ')"
    }
    Assert-Production ($edgeNetworks -notcontains 'backend') 'Edge must never join the backend network.'
}
Assert-Production ($null -eq (Get-PropertyValue $config.services 'minio')) `
    'MinIO must not be present after server-side object storage retirement.'

$expectedSecretNames = @(
    'database_url', 'oidc_client_secret', 'postgres_password', 'redis_url',
    'token_signing_key', 'valkey_password'
)
if ($observabilityOverlayRequested) { $expectedSecretNames += 'grafana_admin_password' }
if ($registrationRecoveryOverlayRequested -or $accountDeletionOverlayRequested) {
    $expectedSecretNames += 'keycloak_admin_client_secret'
}
if ($humanVerificationOverlayRequested) { $expectedSecretNames += 'human_verification_secret' }
if ($accountDeletionOverlayRequested) { $expectedSecretNames += 'account_deletion_identity_key' }
if ($directTransportOverlayRequested) {
    $expectedSecretNames += @('direct_redis_url', 'direct_valkey_password')
}
$actualSecretNames = @($config.secrets.PSObject.Properties.Name | Sort-Object)
Assert-Production (@(Compare-Object ($expectedSecretNames | Sort-Object) $actualSecretNames).Count -eq 0) `
    "Production Compose secrets differ from the selected reviewed topology: $($actualSecretNames -join ', ')"

$secretValues = @()
$checkedSecretDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($secretProperty in @($config.secrets.PSObject.Properties)) {
    $file = [string](Get-PropertyValue $secretProperty.Value 'file')
    Assert-Production ($file -ne '' -and (Test-Path -LiteralPath $file -PathType Leaf)) `
        "Compose secret '$($secretProperty.Name)' does not resolve to an existing file."
    $item = Get-Item -LiteralPath $file -Force
    Assert-Production (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        "Compose secret '$($secretProperty.Name)' cannot be a reparse point."
    Assert-TermLinkPathHasNoReparsePoint -Path $file -Root ([IO.Path]::GetPathRoot($file)) | Out-Null
    $secretDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($file))
    if ($checkedSecretDirectories.Add($secretDirectory)) {
        Assert-SecretPathPermissions -Path $secretDirectory -Directory $true `
            -DisplayName "Compose secret directory '$secretDirectory'"
    }
    Assert-SecretPathPermissions -Path $file -Directory $false `
        -DisplayName "Compose secret '$($secretProperty.Name)'"
    $value = ([Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($file))).Trim()
    Assert-Production ($value.Length -ge 12) `
        "Compose secret '$($secretProperty.Name)' must contain at least 12 non-whitespace characters."
    $secretValues += [pscustomobject]@{ Name = $secretProperty.Name; Value = $value }
}

if ($accountDeletionOverlayRequested) {
    $identityKeySecret = @($secretValues | Where-Object { $_.Name -ceq 'account_deletion_identity_key' })
    Assert-Production ($identityKeySecret.Count -eq 1) `
        'Account deletion requires exactly one identity encryption key secret.'
    Assert-AccountDeletionIdentityKeyMaterial -Value ([string]$identityKeySecret[0].Value) `
        -CurrentVersion $identityKeyVersion
}

$valkeyPasswordSecret = @($secretValues | Where-Object { $_.Name -ceq 'valkey_password' })
$redisUrlSecret = @($secretValues | Where-Object { $_.Name -ceq 'redis_url' })
Assert-Production ($valkeyPasswordSecret.Count -eq 1 -and $redisUrlSecret.Count -eq 1) `
    'Production Compose must declare exactly one Valkey password and Redis URL secret.'
$valkeyPassword = [string]$valkeyPasswordSecret[0].Value
Assert-Production ($valkeyPassword -cmatch '^[A-Za-z0-9_-]{16,128}$') `
    'Valkey password must be a 16-128 character URL-safe random value.'
$redisUri = $null
$redisUriValid = [Uri]::TryCreate([string]$redisUrlSecret[0].Value, [UriKind]::Absolute, [ref]$redisUri)
Assert-Production ($redisUriValid -and $redisUri.Scheme -ceq 'redis' -and $redisUri.Host -ceq 'valkey' -and
    $redisUri.Port -eq 6379 -and $redisUri.AbsolutePath -ceq '/0' -and $redisUri.Query -eq '' -and
    $redisUri.Fragment -eq '' -and $redisUri.UserInfo -ceq "default:$valkeyPassword") `
    'Redis URL must target the internal Valkey service and exactly match its file-backed ACL password.'
$valkeyPassword = $null

if ($directTransportOverlayRequested) {
    $directValkeyPasswordSecret = @($secretValues | Where-Object { $_.Name -ceq 'direct_valkey_password' })
    $directRedisUrlSecret = @($secretValues | Where-Object { $_.Name -ceq 'direct_redis_url' })
    Assert-Production ($directValkeyPasswordSecret.Count -eq 1 -and $directRedisUrlSecret.Count -eq 1) `
        'Direct transport must declare exactly one independent Valkey password and URL secret.'
    $directValkeyPassword = [string]$directValkeyPasswordSecret[0].Value
    Assert-Production ($directValkeyPassword -cmatch '^[A-Za-z0-9_-]{16,128}$') `
        'Direct Valkey password must be a 16-128 character URL-safe random value.'
    Assert-Production ($directValkeyPassword -cne [string]$valkeyPasswordSecret[0].Value) `
        'Direct and Relay Valkey must not reuse the same ACL password.'
    $directRedisUri = $null
    $directRedisUriValid = [Uri]::TryCreate(
        [string]$directRedisUrlSecret[0].Value, [UriKind]::Absolute, [ref]$directRedisUri)
    Assert-Production ($directRedisUriValid -and $directRedisUri.Scheme -ceq 'redis' -and
        $directRedisUri.Host -ceq 'direct-valkey' -and $directRedisUri.Port -eq 6379 -and
        $directRedisUri.AbsolutePath -ceq '/0' -and $directRedisUri.Query -eq '' -and
        $directRedisUri.Fragment -eq '' -and
        $directRedisUri.UserInfo -ceq "termlink-direct:$directValkeyPassword") `
        'Direct Redis URL must target the one shared Direct Valkey service and exactly match its bounded ACL user.'
    $directValkeyPassword = $null
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
