[CmdletBinding()]
param(
    [switch]$Reset,
    [string]$LanAddress,
    [string]$LuckyBindAddress = '0.0.0.0',
    [ValidateRange(1, 65535)][int]$LuckyPort = 8080,
    [switch]$SingleNode,
    [switch]$EmbeddedIdentity,
    [switch]$EnableDirectTransportShadow,
    [switch]$EnableDirectDataPlane,
    [ValidateNotNullOrEmpty()]
    [string]$DirectStunServers = 'stun:stun.cloudflare.com:3478,stun:stun.l.google.com:19302',
    [ValidatePattern('^docker\.io/myucxy/termlink:[A-Za-z0-9_.-]+@sha256:[0-9a-f]{64}$')]
    [string]$ServerImage,
    [ValidatePattern('^(?:docker\.io/)?myucxy/termlink:[0-9]+\.[0-9]+\.[0-9]+-local-production$')]
    [string]$LocalServerImage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ProductionAcceptance.Common.psm1') -Force

function Set-AcceptanceEndpointEnvironment {
    param(
        [string]$Address,
        [string]$PublicUpstreamBindAddress,
        [ValidateRange(1, 65535)][int]$PublicUpstreamPort
    )

    if ([string]::IsNullOrWhiteSpace($Address)) {
        $parsedBindAddress = $null
        if (-not [Net.IPAddress]::TryParse($PublicUpstreamBindAddress.Trim(), [ref]$parsedBindAddress) -or
            $parsedBindAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
            throw 'LuckyBindAddress must be an IPv4 address assigned to this host, or 0.0.0.0.'
        }
        $normalizedBindAddress = $parsedBindAddress.ToString()
        if ($normalizedBindAddress -ne '0.0.0.0') {
            $assigned = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object {
                $_.IPAddress -eq $normalizedBindAddress
            })
            if ($assigned.Count -eq 0) {
                throw "LuckyBindAddress '$normalizedBindAddress' is not assigned to this host."
            }
        }
        $env:TERMLINK_ACCEPTANCE_PUBLIC_ORIGIN = 'https://termlink.kget.cc:8910'
        $env:TERMLINK_ACCEPTANCE_PUBLIC_HOST = 'termlink.kget.cc'
        $env:TERMLINK_ACCEPTANCE_PUBLIC_PORT = '8910'
        $env:TERMLINK_ACCEPTANCE_PUBLIC_SCHEME = 'https'
        $env:TERMLINK_ACCEPTANCE_EDGE_BIND_IP = '127.0.0.1'
        $env:TERMLINK_ACCEPTANCE_LUCKY_BIND_IP = $normalizedBindAddress
        $env:TERMLINK_ACCEPTANCE_LUCKY_PORT = $PublicUpstreamPort.ToString([Globalization.CultureInfo]::InvariantCulture)
        return [pscustomobject]@{
            Root = $env:TERMLINK_ACCEPTANCE_PUBLIC_ORIGIN
            PublicOrigin = $env:TERMLINK_ACCEPTANCE_PUBLIC_ORIGIN
        }
    }

    $parsedAddress = $null
    if (-not [Net.IPAddress]::TryParse($Address.Trim(), [ref]$parsedAddress) -or
        $parsedAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'LanAddress must be an IPv4 address assigned to this host.'
    }
    $normalizedAddress = $parsedAddress.ToString()
    $assigned = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object {
        $_.IPAddress -eq $normalizedAddress
    })
    if ($assigned.Count -eq 0) {
        throw "LanAddress '$normalizedAddress' is not assigned to this host."
    }

    $publicHost = "termlink.$normalizedAddress.sslip.io"
    $env:TERMLINK_ACCEPTANCE_PUBLIC_ORIGIN = "https://${publicHost}:9443"
    $env:TERMLINK_ACCEPTANCE_PUBLIC_HOST = $publicHost
    $env:TERMLINK_ACCEPTANCE_PUBLIC_PORT = '9443'
    $env:TERMLINK_ACCEPTANCE_PUBLIC_SCHEME = 'https'
    $env:TERMLINK_ACCEPTANCE_EDGE_BIND_IP = '0.0.0.0'
    $parsedBindAddress = $null
    if (-not [Net.IPAddress]::TryParse($PublicUpstreamBindAddress.Trim(), [ref]$parsedBindAddress) -or
        $parsedBindAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'LuckyBindAddress must be an IPv4 address assigned to this host, or 0.0.0.0.'
    }
    $normalizedBindAddress = $parsedBindAddress.ToString()
    if ($normalizedBindAddress -ne '0.0.0.0') {
        $assignedBindAddress = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object {
            $_.IPAddress -eq $normalizedBindAddress
        })
        if ($assignedBindAddress.Count -eq 0) {
            throw "LuckyBindAddress '$normalizedBindAddress' is not assigned to this host."
        }
    }
    # Keep the compatibility Lucky listener configurable on the LAN path too.
    # Public reverse proxies may already target this listener, so silently
    # forcing loopback would break a healthy stack.
    $env:TERMLINK_ACCEPTANCE_LUCKY_BIND_IP = $normalizedBindAddress
    $env:TERMLINK_ACCEPTANCE_LUCKY_PORT = $PublicUpstreamPort.ToString([Globalization.CultureInfo]::InvariantCulture)
    return [pscustomobject]@{
        Root = $env:TERMLINK_ACCEPTANCE_PUBLIC_ORIGIN
        PublicOrigin = $env:TERMLINK_ACCEPTANCE_PUBLIC_ORIGIN
    }
}

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

function Write-SecretFile {
    param([string]$Path, [string]$Value, [switch]$Force)
    if (-not $Force -and (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Wait-ProductionAcceptanceStack {
    param(
        [Parameter(Mandatory)][string[]]$ComposeFile,
        [string[]]$AdditionalHealthyServices = @(),
        [string[]]$AdditionalCompletedServices = @(),
        [ValidateRange(30, 900)][int]$TimeoutSeconds = 300
    )

    $composeFileArguments = @($ComposeFile | ForEach-Object { '-f'; $_ })

    $healthyServices = @(if ($EmbeddedIdentity) {
        'postgres'; 'server'; $AdditionalHealthyServices
    }
    else {
        'lucky-upstream'; 'postgres'; 'valkey'; 'server'; 'keycloak'; $AdditionalHealthyServices
    })
    if ($SingleNode -and -not $EmbeddedIdentity) {
        $healthyServices = @($healthyServices | Where-Object { $_ -notin @('lucky-upstream', 'valkey') })
    }
    $runningServices = @()
    $completedServices = @(if ($EmbeddedIdentity) {
        'runtime-secrets-init'; $AdditionalCompletedServices
    }
    else {
        'runtime-secrets-init'; 'keycloak-init'; 'server-secrets-init'; 'migrate'; $AdditionalCompletedServices
    })
    if ($SingleNode -and -not $EmbeddedIdentity) {
        $completedServices = @($completedServices | Where-Object { $_ -ne 'migrate' })
    }
    $expectedServices = @($healthyServices + $runningServices + $completedServices)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $lastState = @{}

    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $rows = @(& docker compose @composeFileArguments ps --all --format json)
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not inspect the production acceptance stack.'
        }

        $lastState = @{}
        foreach ($row in $rows) {
            if ([string]::IsNullOrWhiteSpace([string]$row)) { continue }
            try {
                $items = @($row | ConvertFrom-Json -ErrorAction Stop)
            }
            catch {
                throw 'Docker Compose returned an invalid service status payload.'
            }
            foreach ($item in $items) {
                $lastState[[string]$item.Service] = $item
            }
        }

        $failed = @($expectedServices | Where-Object {
            $item = $lastState[$_]
            $null -ne $item -and [string]$item.State -eq 'exited' -and [int]$item.ExitCode -ne 0
        })
        if ($failed.Count -ne 0) {
            throw "Production acceptance services failed: $($failed -join ', ')."
        }

        $healthyReady = @($healthyServices | Where-Object {
            $item = $lastState[$_]
            $null -ne $item -and [string]$item.State -eq 'running' -and [string]$item.Health -eq 'healthy'
        }).Count -eq $healthyServices.Count
        $runningReady = @($runningServices | Where-Object {
            $item = $lastState[$_]
            $null -ne $item -and [string]$item.State -eq 'running'
        }).Count -eq $runningServices.Count
        $completedReady = @($completedServices | Where-Object {
            $item = $lastState[$_]
            $null -ne $item -and [string]$item.State -eq 'exited' -and [int]$item.ExitCode -eq 0
        }).Count -eq $completedServices.Count

        if ($healthyReady -and $runningReady -and $completedReady) {
            return
        }
        Start-Sleep -Seconds 2
    }

    $summary = @($expectedServices | ForEach-Object {
        $item = $lastState[$_]
        if ($null -eq $item) {
            "$_=missing"
        }
        else {
            $health = if ([string]::IsNullOrWhiteSpace([string]$item.Health)) { 'none' } else { [string]$item.Health }
            "$_=$($item.State)/$health/exit-$($item.ExitCode)"
        }
    }) -join ', '
    throw "The production acceptance stack did not become ready within $TimeoutSeconds seconds: $summary."
}

function Wait-PublicAcceptanceEndpoint {
    param(
        [Parameter(Mandatory)][ValidatePattern('^https://')][string]$Uri,
        [ValidateRange(10, 120)][int]$TimeoutSeconds = 60
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        try {
            $response = Invoke-WebRequest `
                -Uri $Uri `
                -Method Get `
                -MaximumRedirection 0 `
                -TimeoutSec 10 `
                -UseBasicParsing
            if ([int]$response.StatusCode -eq 200) { return }
        }
        catch {
            # External ingress may briefly retain the stopped upstream while
            # the single-node server takes ownership of the same host port.
        }
        Start-Sleep -Seconds 2
    }

    throw "The public acceptance endpoint did not become ready within $TimeoutSeconds seconds."
}

if (-not [string]::IsNullOrWhiteSpace($ServerImage) -and
    -not [string]::IsNullOrWhiteSpace($LocalServerImage)) {
    throw '只能指定 ServerImage 或 LocalServerImage 其中一个。 / Specify only one of ServerImage or LocalServerImage.'
}
if ($EmbeddedIdentity -and -not $SingleNode) {
    throw '内置身份验收必须使用单机模式。 / Embedded identity acceptance requires single-node mode.'
}
if ($EnableDirectDataPlane -and -not ($EnableDirectTransportShadow -or $SingleNode)) {
    throw 'Direct 数据面验收需要先启用 Direct transport。 / Direct data-plane acceptance requires Direct transport.'
}
$directRoutingPermitted = if ($EnableDirectDataPlane) { 'true' } else { 'false' }
# Override inherited process values in both acceptance topologies. The data
# plane can only be enabled by this invocation's explicit switch; a stale
# shell environment must never turn routing on implicitly.
$env:TERMLINK_DIRECT_ROUTING_PERMITTED = $directRoutingPermitted
$env:TERMLINK_SINGLE_NODE_DIRECT_ROUTING_PERMITTED = $directRoutingPermitted
$endpoints = Set-AcceptanceEndpointEnvironment `
    -Address $LanAddress `
    -PublicUpstreamBindAddress $LuckyBindAddress `
    -PublicUpstreamPort $LuckyPort
$paths = Get-TermLinkAcceptancePaths
# Compose resolves every file-backed secret path while it loads the project,
# including for a build-only command. Export only these non-secret path values
# before the optional metadata prebuild; the files themselves are still
# created and permission-hardened later, after the Reset safety checks.
Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
$composeFiles = @()
$singleNodeComposeFile = Join-Path $paths.ServerRoot 'compose\acceptance.single-node.compose.yml'
$embeddedIdentityComposeFile = Join-Path $paths.ServerRoot 'compose\acceptance.embedded-identity.compose.yml'
$directTransportComposeFile = Join-Path $paths.ServerRoot 'compose\direct-transport.compose.yml'
$acceptanceDirectTransportComposeFile = Join-Path $paths.ServerRoot 'compose\acceptance.direct-transport.compose.yml'
$additionalHealthyServices = @()
$additionalCompletedServices = @()
$localEdgeEnabled = -not [string]::IsNullOrWhiteSpace($LanAddress)
if ($SingleNode -and $localEdgeEnabled) {
    throw '单机模式由外部入口终止 TLS，不能同时启用内置 LAN Caddy。 / Single-node mode requires external TLS ingress and cannot use the LAN Caddy listener.'
}
if ($EmbeddedIdentity) {
    if (-not (Test-Path -LiteralPath $embeddedIdentityComposeFile -PathType Leaf)) {
        throw "Embedded identity acceptance Compose file not found: $embeddedIdentityComposeFile"
    }
    $composeFiles += $embeddedIdentityComposeFile
}
else {
    $composeFiles += $paths.ComposeFile
}
if ($SingleNode -and -not $EmbeddedIdentity) {
    if (-not (Test-Path -LiteralPath $singleNodeComposeFile -PathType Leaf)) {
        throw "Single-node acceptance Compose overlay not found: $singleNodeComposeFile"
    }
    $env:TERMLINK_SINGLE_NODE_BIND_IP = $LuckyBindAddress
    $env:TERMLINK_SINGLE_NODE_PORT = $LuckyPort.ToString([Globalization.CultureInfo]::InvariantCulture)
    # Direct signalling is process-local in this topology and has no external
    # dependency. It remains shadow-only; Relay is still the authoritative
    # terminal transport and is never gated by Direct health.
    $env:TERMLINK_SINGLE_NODE_DIRECT_TRANSPORT_ENABLED = 'true'
    $env:TERMLINK_SINGLE_NODE_DIRECT_STUN_SERVERS = $DirectStunServers
    $composeFiles += $singleNodeComposeFile
}
if ($localEdgeEnabled) {
    $localEdgeComposeFile = Join-Path $paths.ServerRoot 'compose\acceptance.local-edge.compose.yml'
    if (-not (Test-Path -LiteralPath $localEdgeComposeFile -PathType Leaf)) {
        throw "Local TLS listener Compose overlay not found: $localEdgeComposeFile"
    }
    $composeFiles += $localEdgeComposeFile
}
if ($EnableDirectTransportShadow -and -not $SingleNode) {
    if (-not (Test-Path -LiteralPath $directTransportComposeFile -PathType Leaf)) {
        throw "Direct transport Compose overlay not found: $directTransportComposeFile"
    }
    if (-not (Test-Path -LiteralPath $acceptanceDirectTransportComposeFile -PathType Leaf)) {
        throw "Acceptance Direct transport Compose overlay not found: $acceptanceDirectTransportComposeFile"
    }
    Set-TermLinkAcceptanceDirectComposeEnvironment -Paths $paths -STUNServers $DirectStunServers
    $composeFiles += $directTransportComposeFile
    $composeFiles += $acceptanceDirectTransportComposeFile
    $additionalHealthyServices += 'direct-valkey'
    $additionalCompletedServices += 'direct-valkey-secrets-init'
}
$selectedServerImage = if (-not [string]::IsNullOrWhiteSpace($ServerImage)) {
    $ServerImage
}
else {
    $LocalServerImage
}
if (-not [string]::IsNullOrWhiteSpace($selectedServerImage)) {
    $releaseImageComposeFile = if ($EmbeddedIdentity) {
        Join-Path $paths.ServerRoot 'compose\acceptance.embedded-identity.release-image.compose.yml'
    }
    else {
        Join-Path $paths.ServerRoot 'compose\acceptance.release-image.compose.yml'
    }
    if (-not (Test-Path -LiteralPath $releaseImageComposeFile -PathType Leaf)) {
        throw "Published-image Compose overlay not found: $releaseImageComposeFile"
    }
    $env:TERMLINK_ACCEPTANCE_SERVER_IMAGE = $selectedServerImage
    if (-not [string]::IsNullOrWhiteSpace($ServerImage)) {
        & docker pull $ServerImage
        if ($LASTEXITCODE -ne 0) { throw 'Could not pull the immutable production server image.' }
    }
    else {
        $localPlatform = (& docker image inspect $LocalServerImage --format '{{.Os}}/{{.Architecture}}').Trim()
        if ($LASTEXITCODE -ne 0 -or $localPlatform -cne 'linux/amd64') {
            throw '本地生产镜像必须已存在且平台为 linux/amd64。 / The local production image must exist and target linux/amd64.'
        }
    }
    $imageMetadataJson = & docker image inspect $selectedServerImage --format '{{json .Config.Labels}}'
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($imageMetadataJson)) {
        throw '无法检查选定的生产服务端镜像。 / Could not inspect the selected production server image.'
    }
    $imageMetadata = $imageMetadataJson | ConvertFrom-Json
    if ([string]$imageMetadata.'org.opencontainers.image.revision' -notmatch '^[0-9a-f]{40}$' -or
        [string]$imageMetadata.'org.opencontainers.image.version' -notmatch '^v?[0-9]+\.[0-9]+\.[0-9]+(?:\+[0-9]+)?$' -or
        [string]$imageMetadata.'org.opencontainers.image.created' -eq 'unknown' -or
        [string]$imageMetadata.'org.opencontainers.image.source' -cne 'https://github.com/myucxy/TermLink') {
        throw '选定的服务端镜像 OCI 源元数据缺失或不一致。 / Selected server image OCI source metadata is missing or inconsistent.'
    }
    if (-not [string]::IsNullOrWhiteSpace($LocalServerImage)) {
        $localTagVersion = [regex]::Match(
            $LocalServerImage,
            '(?:^|/)myucxy/termlink:(?<version>[0-9]+\.[0-9]+\.[0-9]+)-local-production$'
        ).Groups['version'].Value
        if ([string]$imageMetadata.'org.opencontainers.image.version' -cne $localTagVersion) {
            throw '本地生产镜像标签与 OCI 版本不一致。 / The local production tag does not match its OCI version.'
        }
    }
    $composeFiles += $releaseImageComposeFile
}
if ($EnableDirectDataPlane) {
    $metadataComposeFile = Join-Path $paths.ServerRoot 'compose\acceptance.direct-product-e2e-metadata.compose.yml'
    if (-not (Test-Path -LiteralPath $metadataComposeFile -PathType Leaf)) {
        throw "Direct product acceptance metadata Compose overlay not found: $metadataComposeFile"
    }

    # A source acceptance run first produces the exact image that will be
    # started, then removes its build declaration through the existing
    # release-image overlay. This makes the injected identifier the content
    # address of the running image rather than a hash of a mutable workspace.
    if ([string]::IsNullOrWhiteSpace($selectedServerImage)) {
        $prebuildComposeArguments = @($composeFiles | ForEach-Object { '-f'; $_ })
        & docker compose @prebuildComposeArguments build server
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not build the server image for Direct product acceptance metadata.'
        }
        $selectedServerImage = 'termlink-production-acceptance-server:local'
        $env:TERMLINK_ACCEPTANCE_SERVER_IMAGE = $selectedServerImage
        $releaseImageComposeFile = if ($EmbeddedIdentity) {
            Join-Path $paths.ServerRoot 'compose\acceptance.embedded-identity.release-image.compose.yml'
        }
        else {
            Join-Path $paths.ServerRoot 'compose\acceptance.release-image.compose.yml'
        }
        if (-not (Test-Path -LiteralPath $releaseImageComposeFile -PathType Leaf)) {
            throw "Published-image Compose overlay not found: $releaseImageComposeFile"
        }
        $composeFiles += $releaseImageComposeFile
    }

    $imageContentID = (& docker image inspect $selectedServerImage --format '{{.Id}}').Trim()
    if ($LASTEXITCODE -ne 0 -or $imageContentID -cnotmatch '^sha256:[0-9a-f]{64}$') {
        throw 'The Direct product acceptance server image lacks an exact content identifier.'
    }
    $env:TERMLINK_DIRECT_PRODUCT_E2E_BUILD_SHA256 = $imageContentID.Substring(7)
    $composeFiles += $metadataComposeFile
}
$composeFileArguments = @($composeFiles | ForEach-Object { '-f'; $_ })
if ($Reset -and (Test-Path -LiteralPath $paths.SecretRoot -PathType Container)) {
    if (-not (Test-Path -LiteralPath $paths.Marker -PathType Leaf)) {
        throw "Refusing to reset an unmanaged directory: $($paths.SecretRoot)"
    }
    Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
    if (-not (Test-Path -LiteralPath $paths.InitialAdminPassword -PathType Leaf)) {
        $env:TERMLINK_ACCEPTANCE_ADMIN_INITIAL_PASSWORD_FILE = $paths.AdminPassword
    }
    if ($EmbeddedIdentity) {
        & docker compose -f $embeddedIdentityComposeFile down --volumes --remove-orphans
    }
    else {
        & docker compose @composeFileArguments down --volumes --remove-orphans
    }
    if ($LASTEXITCODE -ne 0) { throw 'Could not reset the production acceptance stack.' }
    $composeProject = 'termlink-production-acceptance'
    $projectPrefix = "$composeProject`_"
    $remainingContainers = @(& docker ps -aq --filter "label=com.docker.compose.project=$composeProject")
    if ($LASTEXITCODE -ne 0 -or $remainingContainers.Count -ne 0) {
        throw 'Refusing to remove project storage while Compose containers remain.'
    }
    $retiredVolumes = @(& docker volume ls --filter "label=com.docker.compose.project=$composeProject" --format '{{.Name}}')
    foreach ($volume in $retiredVolumes) {
        if ([string]::IsNullOrWhiteSpace($volume) -or -not $volume.StartsWith($projectPrefix, [StringComparison]::Ordinal)) {
            throw "Refusing to remove an unexpected Docker volume: $volume"
        }
        & docker volume rm $volume | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not remove retired project volume '$volume'." }
    }
    $retiredNetworks = @(& docker network ls --filter "label=com.docker.compose.project=$composeProject" --format '{{.Name}}')
    foreach ($network in $retiredNetworks) {
        if ([string]::IsNullOrWhiteSpace($network) -or -not $network.StartsWith($projectPrefix, [StringComparison]::Ordinal)) {
            throw "Refusing to remove an unexpected Docker network: $network"
        }
        & docker network rm $network | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not remove retired project network '$network'." }
    }
    $productionPolicyEnvironment = Join-Path $paths.SecretRoot 'production-policy.env'
    foreach ($file in @(
        $paths.AdminPassword, $paths.InitialAdminPassword, $paths.InitialAdminClaimed,
        $paths.InitialAdminReady, $paths.InitialAdminLock, $paths.UserPassword, $paths.PostgresPassword,
        $paths.ValkeyPassword, $paths.OidcClientSecret, $paths.DatabaseUrl, $paths.RedisUrl,
        $paths.DirectValkeyPassword, $paths.DirectRedisUrl,
        $paths.LegacyS3AccessKey, $paths.LegacyS3SecretKey,
        $paths.TokenSigningKey, $paths.RootCa, $paths.ProbeIdentity,
        $productionPolicyEnvironment
    )) {
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            Remove-Item -LiteralPath $file -Force
        }
    }
    $unexpectedEntries = @(Get-ChildItem -LiteralPath $paths.SecretRoot -Force | Where-Object {
        $_.FullName -cne $paths.Marker
    })
    if ($unexpectedEntries.Count -ne 0) {
        throw 'Refusing to remove a managed acceptance directory containing unexpected entries.'
    }
    Remove-Item -LiteralPath $paths.Marker -Force
    Remove-Item -LiteralPath $paths.SecretRoot -Force
}

if (Test-Path -LiteralPath $paths.SecretRoot -PathType Container) {
    if (-not (Test-Path -LiteralPath $paths.Marker -PathType Leaf)) {
        throw "Refusing to use an unmanaged directory: $($paths.SecretRoot)"
    }
}
else {
    New-Item -ItemType Directory -Path $paths.SecretRoot | Out-Null
    [IO.File]::WriteAllText($paths.Marker, 'TermLink production acceptance secrets', [Text.UTF8Encoding]::new($false))
}
Protect-TermLinkAcceptanceSecretTree -SecretRoot $paths.SecretRoot

Write-SecretFile $paths.InitialAdminPassword (New-RandomText 18)
if (-not $EmbeddedIdentity) {
    Write-SecretFile $paths.AdminPassword (New-RandomText 32)
    Write-SecretFile $paths.UserPassword (New-RandomText 32)
}
$postgresPassword = if (Test-Path -LiteralPath $paths.PostgresPassword -PathType Leaf) {
    [IO.File]::ReadAllText($paths.PostgresPassword)
} else {
    $generated = New-RandomText 32
    Write-SecretFile $paths.PostgresPassword $generated
    $generated
}
try {
    Write-SecretFile $paths.DatabaseUrl "postgres://termlink:$postgresPassword@postgres:5432/termlink?sslmode=disable"
}
finally {
    $postgresPassword = $null
}
if (-not $EmbeddedIdentity) {
    $valkeyPassword = if (Test-Path -LiteralPath $paths.ValkeyPassword -PathType Leaf) {
        [IO.File]::ReadAllText($paths.ValkeyPassword).Trim()
    } else {
        $generated = New-RandomText 32
        Write-SecretFile $paths.ValkeyPassword $generated
        $generated
    }
    if ($valkeyPassword -notmatch '^[A-Za-z0-9_-]{16,128}$') {
        $valkeyPassword = $null
        throw 'The managed acceptance Valkey password file is empty or has an invalid format.'
    }
    try {
        Write-SecretFile $paths.RedisUrl "redis://default:$valkeyPassword@valkey:6379/0" -Force
    }
    finally {
        $valkeyPassword = $null
    }
}
if ($EnableDirectTransportShadow -and -not $SingleNode) {
    $directValkeyPassword = if (Test-Path -LiteralPath $paths.DirectValkeyPassword -PathType Leaf) {
        [IO.File]::ReadAllText($paths.DirectValkeyPassword).Trim()
    } else {
        $generated = New-RandomText 32
        Write-SecretFile $paths.DirectValkeyPassword $generated
        $generated
    }
    if ($directValkeyPassword -notmatch '^[A-Za-z0-9_-]{16,128}$') {
        $directValkeyPassword = $null
        throw 'The managed acceptance Direct Valkey password file is empty or has an invalid format.'
    }
    try {
        Write-SecretFile $paths.DirectRedisUrl "redis://termlink-direct:$directValkeyPassword@direct-valkey:6379/0" -Force
    }
    finally {
        $directValkeyPassword = $null
    }
}
if (-not $EmbeddedIdentity) {
    Write-SecretFile $paths.OidcClientSecret (New-RandomText 32)
}
Write-SecretFile $paths.TokenSigningKey (New-RandomText 48)
Protect-TermLinkAcceptanceSecretTree -SecretRoot $paths.SecretRoot

Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
$initializerServices = @(if ($EmbeddedIdentity) { 'runtime-secrets-init' } else { 'runtime-secrets-init'; 'server-secrets-init' })
if (-not $EmbeddedIdentity) {
    $env:TERMLINK_ACCEPTANCE_KEYCLOAK_IMAGE = & (Join-Path $PSScriptRoot 'Import-KeycloakAcceptanceImage.ps1')
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($env:TERMLINK_ACCEPTANCE_KEYCLOAK_IMAGE)) {
        throw 'Could not prepare the pinned Keycloak acceptance image.'
    }
    # Keep the public Lucky upstream closed while the idempotent initializer
    # reconciles the realm. This prevents a fresh or partially configured realm
    # from exposing registration before the managed admin identity is verified.
    & docker compose @composeFileArguments stop lucky-upstream
    if ($LASTEXITCODE -ne 0) { throw 'Could not close the public acceptance upstream before identity initialization.' }
    $initializerServices += 'keycloak-init'
}
# Secret staging jobs must rerun so rotations reach their service-private
# volumes. The external provider additionally reruns its realm reconciler.
& docker compose @composeFileArguments rm --stop --force @initializerServices
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the production initializer services.' }
if ($EnableDirectTransportShadow -and -not $SingleNode) {
    & docker compose @composeFileArguments rm --stop --force direct-valkey-secrets-init
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the Direct Valkey secret initializer.' }
    # The Direct lane is deliberately not a server startup dependency. Prewarm
    # its isolated state only for this explicit acceptance run so the server can
    # enable shadow signaling without weakening Relay startup semantics.
    & docker compose @composeFileArguments up --detach --wait direct-valkey
    if ($LASTEXITCODE -ne 0) { throw 'Could not prewarm the Direct transport acceptance state.' }
}
# Recreate/build the project once. A second `compose up --wait` can restart the
# completed initializer jobs and race Compose's own container replacement on
# Docker Desktop, leaving the waiter bound to a removed container ID. Poll the
# single stable container set instead and require every initializer and health
# check to reach its production-ready terminal state.
& docker compose @composeFileArguments up --build --detach --remove-orphans
if ($LASTEXITCODE -ne 0) { throw 'Could not build or recreate the production acceptance stack.' }
Wait-ProductionAcceptanceStack -ComposeFile $composeFiles `
    -AdditionalHealthyServices $additionalHealthyServices `
    -AdditionalCompletedServices $additionalCompletedServices -TimeoutSeconds 300

if ($EnableDirectDataPlane) {
    $serverContainerID = (& docker compose @composeFileArguments ps -q server).Trim()
    $runningImageContentID = if ([string]::IsNullOrWhiteSpace($serverContainerID)) {
        ''
    }
    else {
        (& docker container inspect $serverContainerID --format '{{.Image}}').Trim()
    }
    if ($LASTEXITCODE -ne 0 -or
        $runningImageContentID -cne "sha256:$($env:TERMLINK_DIRECT_PRODUCT_E2E_BUILD_SHA256)") {
        throw 'The running server image does not match its Direct product acceptance metadata digest.'
    }
}

function Assert-RuntimeSecretMetadata {
    param(
        [Parameter(Mandatory)][string]$Service,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected
    )
    $actual = (& docker compose @composeFileArguments exec -T $Service stat -c '%a:%u:%g' $Path).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -cne $Expected) {
        throw "Service '$Service' secret '$Path' has metadata '$actual'; expected '$Expected'."
    }
}

if (-not $EmbeddedIdentity) {
    Assert-RuntimeSecretMetadata -Service keycloak -Path /run/secrets/keycloak_admin_password -Expected '400:1000:0'
}
Assert-RuntimeSecretMetadata -Service postgres -Path /run/secrets/postgres_password -Expected '400:70:70'
if (-not $SingleNode) {
    Assert-RuntimeSecretMetadata -Service valkey -Path /run/secrets/valkey_password -Expected '400:999:1000'
}
if ($EnableDirectTransportShadow -and -not $SingleNode) {
    Assert-RuntimeSecretMetadata `
        -Service direct-valkey `
        -Path /run/secrets/direct_valkey_password `
        -Expected '400:999:1000'
}

if ($localEdgeEnabled -and -not $SingleNode) {
    if (Test-Path -LiteralPath $paths.RootCa -PathType Leaf) {
        Remove-Item -LiteralPath $paths.RootCa -Force
    }
    & docker compose @composeFileArguments cp lucky-upstream:/data/caddy/pki/authorities/local/root.crt $paths.RootCa
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $paths.RootCa -PathType Leaf)) {
        throw 'Could not export the disposable acceptance CA certificate.'
    }
}
elseif (Test-Path -LiteralPath $paths.RootCa -PathType Leaf) {
    Remove-Item -LiteralPath $paths.RootCa -Force
}

$env:TERMLINK_ACCEPTANCE_ROOT_URL = $endpoints.Root
$env:TERMLINK_ACCEPTANCE_OIDC_ISSUER = "$($endpoints.PublicOrigin)/realms/termlink"
if ($localEdgeEnabled) {
    $env:TERMLINK_ACCEPTANCE_CA_FILE = $paths.RootCa
}
else {
    Remove-Item Env:TERMLINK_ACCEPTANCE_CA_FILE -ErrorAction SilentlyContinue
}
if ($SingleNode) {
    Wait-PublicAcceptanceEndpoint -Uri "$($endpoints.Root)/healthz" -TimeoutSeconds 60
}
$claimedEmbeddedAdmin = $EmbeddedIdentity -and
    (Test-Path -LiteralPath $paths.InitialAdminClaimed -PathType Leaf)
if ($claimedEmbeddedAdmin) {
    # The one-time bootstrap password is deliberately destroyed when claimed.
    # Never reset a real administrator merely to rerun an acceptance probe.
    Wait-PublicAcceptanceEndpoint -Uri "$($endpoints.Root)/readyz" -TimeoutSeconds 60
    Wait-PublicAcceptanceEndpoint `
        -Uri "$($env:TERMLINK_ACCEPTANCE_OIDC_ISSUER)/.well-known/openid-configuration" `
        -TimeoutSeconds 60
    Write-Output '已领取管理员密码；升级已验证健康状态与 OIDC，跳过需要密码的认证探针。 / Admin password was already claimed; health and OIDC were verified, so the credentialed probe was skipped.'
}
else {
    $env:TERMLINK_ACCEPTANCE_USER_NAME = if ($EmbeddedIdentity) { 'admin' } else { 'acceptance-probe' }
    $env:TERMLINK_ACCEPTANCE_USER_PASSWORD_FILE = if ($EmbeddedIdentity) { $paths.InitialAdminPassword } else { $paths.UserPassword }
    $env:TERMLINK_ACCEPTANCE_PC_OIDC_CLIENT_ID = if ($EmbeddedIdentity) { 'termlink-windows' } else { 'termlink-acceptance-cli' }
    $env:TERMLINK_ACCEPTANCE_PROBE_IDENTITY_FILE = $paths.ProbeIdentity
    $env:TERMLINK_ACCEPTANCE_REQUIRE_DIRECT_TRANSPORT = if ($EnableDirectTransportShadow -or $SingleNode) { '1' } else { '0' }
    $env:TERMLINK_ACCEPTANCE_EXPECT_DIRECT_ROUTING = if ($EnableDirectDataPlane) { '1' } else { '0' }
    $go = if (Test-Path -LiteralPath 'D:\Dev\Env\Go\bin\go.exe' -PathType Leaf) {
        'D:\Dev\Env\Go\bin\go.exe'
    } else {
        'go'
    }
    Push-Location $paths.ServerRoot
    try {
        & $go run ./cmd/production-e2e-probe
        if ($LASTEXITCODE -ne 0) { throw 'The production OIDC/TLS/API/Realtime probe failed.' }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path -LiteralPath $paths.InitialAdminReady -PathType Leaf) -and
    -not (Test-Path -LiteralPath $paths.InitialAdminClaimed -PathType Leaf)) {
    [IO.File]::WriteAllText($paths.InitialAdminReady, 'TermLink admin identity bootstrap completed', [Text.UTF8Encoding]::new($false))
}
Protect-TermLinkAcceptanceSecretTree -SecretRoot $paths.SecretRoot

# Keep completed jobs available for diagnostics until every probe succeeds,
# then remove only their stopped containers. Named data, secret volumes, and
# images remain untouched.
$oneShotServices = @(if ($EmbeddedIdentity) { 'runtime-secrets-init' } else { 'runtime-secrets-init'; 'server-secrets-init' })
if (-not $EmbeddedIdentity) { $oneShotServices += 'keycloak-init' }
if (-not $SingleNode) { $oneShotServices += 'migrate' }
if ($EnableDirectTransportShadow -and -not $SingleNode) {
    $oneShotServices += 'direct-valkey-secrets-init'
}
& docker compose @composeFileArguments rm --force @oneShotServices
if ($LASTEXITCODE -ne 0) { throw 'Could not remove completed production acceptance jobs.' }
if ($SingleNode -and -not $EmbeddedIdentity) {
    # These services remain declared behind an inactive profile so Compose
    # does not classify their old containers as orphans. Remove the containers
    # only after the single-node probe succeeds; their named volumes and images
    # remain available for rollback.
    $retiredDistributedServices = @('lucky-upstream', 'valkey', 'migrate')
    $cleanupComposeFileArguments = @($composeFileArguments)
    & docker compose @cleanupComposeFileArguments rm --stop --force @retiredDistributedServices
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not remove retired distributed-runtime containers.'
    }
}

Write-Output 'TermLink production acceptance stack is running.'
Write-Output "API: $($endpoints.Root)"
Write-Output "Public API/OIDC: $($endpoints.PublicOrigin)"
if ($localEdgeEnabled) {
    Write-Output "CA certificate: $($paths.RootCa)"
}
if (-not (Test-Path -LiteralPath $paths.InitialAdminClaimed -PathType Leaf)) {
    Write-Output 'Initial admin is ready. Retrieve its password once with server\ops\Get-InitialAdminPassword.ps1.'
}
