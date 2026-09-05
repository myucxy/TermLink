[CmdletBinding()]
param(
    [string[]]$ComposeFile = @((Join-Path $PSScriptRoot '..\compose\core.compose.yml')),
    [ValidatePattern('^[a-z0-9][a-z0-9_-]{2,62}$')]
    [string]$ProjectName = 'termlink',
    [Parameter(Mandatory)]
    [string]$Destination,
    [Parameter(Mandatory)]
    [string]$ManifestAuthenticationKeyFile,
    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_-]{0,62}$')]
    [string]$Database = 'termlink',
    [ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_-]{0,62}$')]
    [string]$DatabaseUser = 'termlink',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$')]
    [string]$PostgresService = 'postgres',
    [Parameter(Mandatory)]
    [switch]$DestinationEncryptionConfirmed
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TermLinkBackup.Common.psm1') -Force

if (-not $DestinationEncryptionConfirmed) {
    throw 'Production backups require an explicitly confirmed encrypted-at-rest destination.'
}
$composeFiles = @(Resolve-TermLinkComposeFiles -ComposeFile $ComposeFile)
$compose = New-TermLinkComposeArguments -ComposeFile $composeFiles -ProjectName $ProjectName
$destinationPath = Assert-TermLinkExternalPath -Path $Destination
$manifestAuthenticationKey = Read-TermLinkAuthenticationKey -Path $ManifestAuthenticationKeyFile
Assert-TermLinkAuthenticationKeySeparation -KeyPath $ManifestAuthenticationKeyFile -BackupPath $destinationPath
if (Test-Path -LiteralPath $destinationPath) {
    throw "Backup destination already exists: $destinationPath"
}

$operationId = [guid]::NewGuid().ToString('N')
$remoteRoot = "/tmp/termlink-backup-$operationId"
$databaseDump = Join-Path $destinationPath 'database.dump'
$manifestPath = Join-Path $destinationPath 'manifest.json'
$manifestAuthenticationPath = Join-Path $destinationPath 'manifest.hmac'
$postgresContainer = $null
$stoppedServices = @()
$completed = $false

[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
try {
    $stoppedServices = @(Stop-TermLinkServices -ComposeArguments $compose -Candidates @('lucky-upstream', 'server'))
    $postgresContainer = Get-TermLinkContainerId -ComposeArguments $compose -Service $PostgresService
    Invoke-TermLinkDocker -Arguments @('exec', $postgresContainer, 'mkdir', '-p', $remoteRoot) | Out-Null
    Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer,
        'pg_dump', '--username', $DatabaseUser, '--dbname', $Database,
        '--format=custom', '--no-owner', '--no-privileges',
        '--file', "$remoteRoot/database.dump"
    ) | Out-Null
    Invoke-TermLinkDocker -Arguments @('cp', "${postgresContainer}:$remoteRoot/database.dump", $databaseDump) | Out-Null

    $payloadFiles = @(Get-ChildItem -LiteralPath $destinationPath -Recurse -File | Sort-Object FullName)
    $fileEntries = foreach ($file in $payloadFiles) {
        $relative = [IO.Path]::GetRelativePath($destinationPath, $file.FullName).Replace('\', '/')
        [ordered]@{
            path = $relative
            size_bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest = [ordered]@{
        schema = 'termlink.backup-manifest'
        schema_version = 2
        created_at_utc = [DateTimeOffset]::UtcNow.ToString('o')
        classification = 'PostgreSQL account and coordination metadata; encrypted-at-rest destination required'
        project_name = $ProjectName
        database = $Database
        database_format = 'postgresql-custom'
        valkey = [ordered]@{
            included = $false
            reason = 'ephemeral presence, nonce, lease, and quota state is reconstructed after restart'
        }
        quiesced_services = @($stoppedServices)
        files = @($fileEntries)
    }
    [IO.File]::WriteAllText(
        $manifestPath,
        (($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    $manifestTag = Get-TermLinkHmacSha256 -Data ([IO.File]::ReadAllBytes($manifestPath)) -Key $manifestAuthenticationKey
    [IO.File]::WriteAllText(
        $manifestAuthenticationPath,
        ([Convert]::ToHexString($manifestTag).ToLowerInvariant() + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    $completed = $true
    Write-Host "TermLink backup completed: $destinationPath"
}
finally {
    $remoteCleanupFailed = $false
    if ($null -ne $postgresContainer) {
        try { Remove-TermLinkContainerPath -ContainerId $postgresContainer -Path $remoteRoot }
        catch { $remoteCleanupFailed = $true; Write-Warning 'PostgreSQL backup staging cleanup failed.' }
    }
    $localCleanupFailed = $false
    if (-not $completed -and (Test-Path -LiteralPath $destinationPath -PathType Container)) {
        try { [IO.Directory]::Delete($destinationPath, $true) }
        catch { $localCleanupFailed = $true; Write-Warning 'Incomplete local backup cleanup failed.' }
    }
    if ($stoppedServices.Count -gt 0) {
        Start-TermLinkServices -ComposeArguments $compose -Services $stoppedServices
    }
    if ($completed -and $remoteCleanupFailed) {
        throw 'Backup payload was created, but plaintext container staging cleanup failed.'
    }
    if (-not $completed -and $localCleanupFailed) {
        Write-Warning 'Incomplete plaintext backup output remains and requires operator cleanup.'
    }
}
