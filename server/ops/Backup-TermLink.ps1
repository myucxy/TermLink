[CmdletBinding()]
param(
    [string[]]$ComposeFile = @(
        (Join-Path $PSScriptRoot '..\compose\core.compose.yml'),
        (Join-Path $PSScriptRoot '..\compose\history.compose.yml')
    ),
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
    [string]$Bucket = 'termlink-history',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$')]
    [string]$PostgresService = 'postgres',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$')]
    [string]$MinioService = 'minio',
    [Parameter(Mandatory)]
    [switch]$DestinationEncryptionConfirmed
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TermLinkBackup.Common.psm1') -Force

if (-not $DestinationEncryptionConfirmed) {
    throw 'Production backups require an explicitly confirmed encrypted-at-rest destination.'
}
Assert-TermLinkBucketName -Bucket $Bucket
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
$minioConfigDir = "$remoteRoot/mc"
$databaseDump = Join-Path $destinationPath 'database.dump'
$historyRoot = Join-Path $destinationPath 'history'
$manifestPath = Join-Path $destinationPath 'manifest.json'
$manifestAuthenticationPath = Join-Path $destinationPath 'manifest.hmac'
$postgresContainer = $null
$minioContainer = $null
$stoppedServices = @()
$completed = $false

[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
try {
    $stoppedServices = @(Stop-TermLinkServices -ComposeArguments $compose -Candidates @('edge', 'server'))
    $postgresContainer = Get-TermLinkContainerId -ComposeArguments $compose -Service $PostgresService
    $minioContainer = Get-TermLinkContainerId -ComposeArguments $compose -Service $MinioService
	Initialize-TermLinkMinioAlias -ContainerId $minioContainer -ConfigDir $minioConfigDir

    Invoke-TermLinkDocker -Arguments @('exec', $postgresContainer, 'mkdir', '-p', $remoteRoot) | Out-Null
    Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer,
        'pg_dump', '--username', $DatabaseUser, '--dbname', $Database,
        '--format=custom', '--no-owner', '--no-privileges',
        '--file', "$remoteRoot/database.dump"
    ) | Out-Null
    Invoke-TermLinkDocker -Arguments @('cp', "${postgresContainer}:$remoteRoot/database.dump", $databaseDump) | Out-Null

    Invoke-TermLinkDocker -Arguments @('exec', $minioContainer, 'mkdir', '-p', "$remoteRoot/history") | Out-Null
    Invoke-TermLinkMinioClient -ContainerId $minioContainer -ConfigDir $minioConfigDir `
        -Arguments @('stat', "termlinkbackup/$Bucket") | Out-Null
    Invoke-TermLinkMinioClient -ContainerId $minioContainer -ConfigDir $minioConfigDir `
        -Arguments @('mirror', '--overwrite', "termlinkbackup/$Bucket", "$remoteRoot/history") | Out-Null
    Invoke-TermLinkDocker -Arguments @('cp', "${minioContainer}:$remoteRoot/history", $historyRoot) | Out-Null

    $payloadFiles = @(Get-ChildItem -LiteralPath $destinationPath -Recurse -File | Sort-Object FullName)
    $fileEntries = foreach ($file in $payloadFiles) {
        $relative = [IO.Path]::GetRelativePath($destinationPath, $file.FullName).Replace('\', '/')
        [ordered]@{
            path = $relative
            size_bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $historyObjectCount = @($fileEntries | Where-Object { $_.path.StartsWith('history/', [StringComparison]::Ordinal) }).Count
    $manifest = [ordered]@{
        schema = 'termlink.backup-manifest'
        schema_version = 1
        created_at_utc = [DateTimeOffset]::UtcNow.ToString('o')
        classification = 'PostgreSQL metadata plus client-E2EE MinIO ciphertext; encrypted-at-rest destination required'
        project_name = $ProjectName
        database = $Database
        database_format = 'postgresql-custom'
        bucket = $Bucket
        history_object_count = $historyObjectCount
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
    if ($null -ne $minioContainer) {
        try { Remove-TermLinkContainerPath -ContainerId $minioContainer -Path $remoteRoot }
        catch { $remoteCleanupFailed = $true; Write-Warning 'MinIO backup staging cleanup failed.' }
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
