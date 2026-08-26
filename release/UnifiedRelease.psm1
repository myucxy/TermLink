Set-StrictMode -Version Latest

function Get-TermLinkSha256 {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $hash = [Security.Cryptography.SHA256]::HashData($stream)
        return [Convert]::ToHexString($hash).ToLowerInvariant()
    }
    finally { $stream.Dispose() }
}

function Get-TermLinkAppShipMapping {
    param(
        [Parameter(Mandatory)][string]$Platform,
        [Parameter(Mandatory)][string]$Architecture,
        [Parameter(Mandatory)][ValidateSet('preview', 'stable')][string]$Track,
        [Parameter(Mandatory)][string]$MappingsPath
    )
    $configuration = Get-Content -LiteralPath $MappingsPath -Raw | ConvertFrom-Json
    if ([int]$configuration.schema_version -ne 1) { throw 'AppShip application mapping schema is unsupported.' }
    $matches = @($configuration.mappings | Where-Object {
        [string]$_.platform -ceq $Platform -and [string]$_.architecture -ceq $Architecture -and
        [string]$_.track -ceq $Track
    })
    if ($matches.Count -ne 1) {
        throw "Exactly one AppShip mapping is required for $Platform/$Architecture/$Track."
    }
    return $matches[0]
}

function Assert-TermLinkUnifiedArtifact {
    param(
        [Parameter(Mandatory)]$Artifact,
        [Parameter(Mandatory)][ValidateSet('preview', 'stable')][string]$Track,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$MappingsPath
    )
    $required = @('id', 'path', 'platform', 'architecture', 'file_kind', 'minimum_os',
        'signature_policy', 'signature_identity', 'signature_verified', 'signature_evidence',
        'updater_strategy', 'rollback_verified', 'build_verified', 'platform_e2e_verified',
        'health_verified', 'blockers')
    foreach ($name in $required) {
        if ($null -eq $Artifact.PSObject.Properties[$name]) { throw "Artifact input is missing '$name'." }
    }
    if ([string]$Artifact.path -notmatch '^artifacts/release/') { throw 'Artifact path must be repository release-relative.' }
    $repositoryPrefix = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$Artifact.path).Replace('/', [IO.Path]::DirectorySeparatorChar)))
    if (-not $path.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Artifact path is unavailable or outside the repository: $path" }

    $platformKinds = @{
        windows = @('exe'); android = @('apk'); linux = @('appimage'); macos = @('pkg', 'dmg')
    }
    if (-not $platformKinds.ContainsKey([string]$Artifact.platform) -or
        [string]$Artifact.file_kind -notin $platformKinds[[string]$Artifact.platform]) {
        throw 'Artifact platform/file-kind pair is invalid.'
    }
    $signaturePolicies = @{
        windows = 'authenticode-rfc3161'; android = 'android-apk-v2';
        linux = 'openpgp-detached'; macos = 'apple-developer-id-notarized'
    }
    if ([string]$Artifact.signature_policy -cne $signaturePolicies[[string]$Artifact.platform]) {
        throw 'Artifact signature policy does not match its platform.'
    }
    $mapping = Get-TermLinkAppShipMapping -Platform ([string]$Artifact.platform) `
        -Architecture ([string]$Artifact.architecture) -Track $Track -MappingsPath $MappingsPath
    $blockers = @($Artifact.blockers | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
    $qualified = [bool]$Artifact.build_verified -and [bool]$Artifact.signature_verified -and
        [bool]$Artifact.rollback_verified -and [bool]$Artifact.platform_e2e_verified -and
        [bool]$Artifact.health_verified -and $blockers.Count -eq 0
    if ($Track -ceq 'stable' -and -not $qualified) {
        throw "Stable artifact '$($Artifact.id)' is not fully qualified."
    }
    if ($Track -ceq 'preview' -and (-not [bool]$Artifact.build_verified -or -not [bool]$Artifact.signature_verified)) {
        throw "Preview artifact '$($Artifact.id)' lacks verified build or signature evidence."
    }
    $file = Get-Item -LiteralPath $path
    return [pscustomobject][ordered]@{
        id = [string]$Artifact.id
        path = [string]$Artifact.path
        platform = [string]$Artifact.platform
        architecture = [string]$Artifact.architecture
        file_kind = [string]$Artifact.file_kind
        size = [int64]$file.Length
        sha256 = Get-TermLinkSha256 -Path $path
        minimum_os = [string]$Artifact.minimum_os
        signature = [pscustomobject][ordered]@{
            policy = [string]$Artifact.signature_policy
            identity = [string]$Artifact.signature_identity
            verified = [bool]$Artifact.signature_verified
            evidence = [string]$Artifact.signature_evidence
        }
        updater = [pscustomobject][ordered]@{
            strategy = [string]$Artifact.updater_strategy
            rollback_verified = [bool]$Artifact.rollback_verified
        }
        qualification = [pscustomobject][ordered]@{
            build_verified = [bool]$Artifact.build_verified
            platform_e2e_verified = [bool]$Artifact.platform_e2e_verified
            health_verified = [bool]$Artifact.health_verified
            blockers = $blockers
        }
        appship = [pscustomobject][ordered]@{
            application_slug = [string]$mapping.application_slug
            channel = [string]$mapping.appship_channel
        }
    }
}

Export-ModuleMember -Function @('Get-TermLinkSha256', 'Get-TermLinkAppShipMapping', 'Assert-TermLinkUnifiedArtifact')
