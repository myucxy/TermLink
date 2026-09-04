[CmdletBinding()]
param(
    [string[]]$ComposeFile = @(
        (Join-Path $PSScriptRoot '..\compose\core.compose.yml'),
        (Join-Path $PSScriptRoot '..\compose\history.compose.yml')
    ),
    [ValidatePattern('^[a-z0-9][a-z0-9_-]{2,62}$')]
    [string]$ProjectName = 'termlink',
    [Parameter(Mandatory)]
    [string]$BackupPath,
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
    [switch]$ConfirmDestructiveRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TermLinkBackup.Common.psm1') -Force

if (-not $ConfirmDestructiveRestore) {
    throw 'Restore requires explicit destructive confirmation.'
}
if ($Database -in @('postgres', 'template0', 'template1')) {
    throw 'Restore refuses to replace a PostgreSQL maintenance database.'
}
Assert-TermLinkBucketName -Bucket $Bucket
$backupRoot = Assert-TermLinkExternalPath -Path $BackupPath -MustExist
Assert-TermLinkAuthenticationKeySeparation -KeyPath $ManifestAuthenticationKeyFile -BackupPath $backupRoot
$manifestPath = Join-Path $backupRoot 'manifest.json'
$manifestAuthenticationPath = Join-Path $backupRoot 'manifest.hmac'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Backup manifest not found: $manifestPath"
}
if (-not (Test-Path -LiteralPath $manifestAuthenticationPath -PathType Leaf)) {
    throw "Backup manifest authentication tag not found: $manifestAuthenticationPath"
}
$manifestFile = Get-Item -LiteralPath $manifestPath -Force
if (($manifestFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Backup manifest cannot be a reparse point.'
}
$manifestAuthenticationKey = Read-TermLinkAuthenticationKey -Path $ManifestAuthenticationKeyFile
Assert-TermLinkPathHasNoReparsePoint -Path $manifestAuthenticationPath -Root $backupRoot | Out-Null
$expectedManifestTagText = (Get-Content -Raw -LiteralPath $manifestAuthenticationPath).Trim()
if ($expectedManifestTagText -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Backup manifest authentication tag is malformed.'
}
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
$expectedManifestTag = [Convert]::FromHexString($expectedManifestTagText)
$actualManifestTag = Get-TermLinkHmacSha256 -Data $manifestBytes -Key $manifestAuthenticationKey
if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($expectedManifestTag, $actualManifestTag)) {
    throw 'Backup manifest authentication failed.'
}

try {
    $manifestJson = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes)
    $manifest = $manifestJson | ConvertFrom-Json -Depth 10
}
catch {
    throw "Backup manifest is not valid JSON: $($_.Exception.Message)"
}
if ($manifest.schema -cne 'termlink.backup-manifest' -or [int]$manifest.schema_version -ne 1) {
    throw 'Unsupported backup manifest schema.'
}
if ($manifest.project_name -cne $ProjectName -or $manifest.database -cne $Database -or $manifest.bucket -cne $Bucket) {
    throw 'Backup manifest target does not match the requested project, database, and bucket.'
}
if ($manifest.database_format -cne 'postgresql-custom') {
    throw 'Backup database format is not supported.'
}

$entries = @($manifest.files)
if ($entries.Count -lt 1) { throw 'Backup manifest has no payload files.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$validatedPaths = @{}
foreach ($entry in $entries) {
    $relative = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or
        [IO.Path]::IsPathRooted($relative) -or $relative -match '[\x00-\x1f]' -or
        ($relative -cne 'database.dump' -and -not $relative.StartsWith('history/', [StringComparison]::Ordinal))) {
        throw "Unsafe backup manifest path: $relative"
    }
    $segments = @($relative.Split('/'))
    if ($segments.Count -eq 0 -or @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw "Unsafe backup manifest path segments: $relative"
    }
    if (-not $seen.Add($relative)) { throw "Duplicate backup manifest path: $relative" }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $backupRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
    $prefix = $backupRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Backup payload is missing or outside the backup root: $relative"
    }
    Assert-TermLinkPathHasNoReparsePoint -Path $fullPath -Root $backupRoot | Out-Null
    $file = Get-Item -LiteralPath $fullPath -Force
    if ([int64]$entry.size_bytes -ne $file.Length) {
        throw "Backup payload size mismatch: $relative"
    }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$entry.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $actualHash -cne [string]$entry.sha256) {
        throw "Backup payload SHA-256 mismatch: $relative"
    }
    $validatedPaths[$relative] = $fullPath
}
if (-not $validatedPaths.ContainsKey('database.dump')) {
    throw 'Backup manifest does not contain database.dump.'
}
$historyRoot = Join-Path $backupRoot 'history'
if (-not (Test-Path -LiteralPath $historyRoot -PathType Container)) {
    throw 'Backup history directory is missing.'
}
Assert-TermLinkPathHasNoReparsePoint -Path $historyRoot -Root $backupRoot | Out-Null
$actualPayloads = @(Get-ChildItem -LiteralPath $backupRoot -Recurse -File |
    Where-Object { $_.FullName -notin @($manifestPath, $manifestAuthenticationPath) })
if ($actualPayloads.Count -ne $entries.Count) {
    throw 'Backup contains payload files that are not covered by the manifest.'
}
$historyCount = @($entries | Where-Object { ([string]$_.path).StartsWith('history/', [StringComparison]::Ordinal) }).Count
if ([int]$manifest.history_object_count -ne $historyCount) {
    throw 'Backup history object count does not match the manifest.'
}

$composeFiles = @(Resolve-TermLinkComposeFiles -ComposeFile $ComposeFile)
$compose = New-TermLinkComposeArguments -ComposeFile $composeFiles -ProjectName $ProjectName
$operationId = [guid]::NewGuid().ToString('N')
$remoteRoot = "/tmp/termlink-restore-$operationId"
$minioConfigDir = "$remoteRoot/mc"
$postgresContainer = $null
$minioContainer = $null
$stoppedServices = @()
$restoreSucceeded = $false

try {
    $stoppedServices = @(Stop-TermLinkServices -ComposeArguments $compose -Candidates @('lucky-upstream', 'server'))
    $postgresContainer = Get-TermLinkContainerId -ComposeArguments $compose -Service $PostgresService
    $minioContainer = Get-TermLinkContainerId -ComposeArguments $compose -Service $MinioService
	Initialize-TermLinkMinioAlias -ContainerId $minioContainer -ConfigDir $minioConfigDir

    Invoke-TermLinkDocker -Arguments @('exec', $postgresContainer, 'mkdir', '-p', $remoteRoot) | Out-Null
    Invoke-TermLinkDocker -Arguments @('cp', $validatedPaths['database.dump'], "${postgresContainer}:$remoteRoot/database.dump") | Out-Null
    $databaseEntry = @($entries | Where-Object { [string]$_.path -ceq 'database.dump' })
    if ($databaseEntry.Count -ne 1 -or
        (Get-TermLinkContainerFileSha256 -ContainerId $postgresContainer -Path "$remoteRoot/database.dump") -cne
            [string]$databaseEntry[0].sha256) {
        throw 'Staged PostgreSQL dump SHA-256 does not match the authenticated manifest.'
    }
    Invoke-TermLinkDocker -Arguments @('exec', $minioContainer, 'mkdir', '-p', "$remoteRoot/history") | Out-Null
    foreach ($entry in @($entries | Where-Object { ([string]$_.path).StartsWith('history/', [StringComparison]::Ordinal) })) {
        $relative = [string]$entry.path
        $historyRelative = $relative.Substring('history/'.Length)
        $remotePath = "$remoteRoot/history/$historyRelative"
        $separator = $remotePath.LastIndexOf('/')
        $remoteParent = $remotePath.Substring(0, $separator)
        Invoke-TermLinkDocker -Arguments @('exec', $minioContainer, 'mkdir', '-p', $remoteParent) | Out-Null
        Invoke-TermLinkDocker -Arguments @('cp', $validatedPaths[$relative], "${minioContainer}:$remotePath") | Out-Null
        if ((Get-TermLinkContainerFileSha256 -ContainerId $minioContainer -Path $remotePath) -cne [string]$entry.sha256) {
            throw "Staged MinIO payload SHA-256 does not match the authenticated manifest: $relative"
        }
    }

    Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer, 'dropdb', '--username', $DatabaseUser,
        '--maintenance-db', 'postgres', '--if-exists', '--force', $Database
    ) | Out-Null
    Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer, 'createdb', '--username', $DatabaseUser,
        '--maintenance-db', 'postgres', '--owner', $DatabaseUser, $Database
    ) | Out-Null
    Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer, 'pg_restore', '--username', $DatabaseUser,
        '--dbname', $Database, '--no-owner', '--no-privileges', '--exit-on-error',
        "$remoteRoot/database.dump"
    ) | Out-Null

    Invoke-TermLinkMinioClient -ContainerId $minioContainer -ConfigDir $minioConfigDir `
        -Arguments @('mb', '--ignore-existing', "termlinkbackup/$Bucket") | Out-Null
    Invoke-TermLinkMinioClient -ContainerId $minioContainer -ConfigDir $minioConfigDir `
        -Arguments @('mirror', '--overwrite', '--remove', "$remoteRoot/history", "termlinkbackup/$Bucket") | Out-Null

    $restoreSucceeded = $true
    Write-Host "TermLink restore completed from: $backupRoot"
}
finally {
    $remoteCleanupFailed = $false
    if ($null -ne $postgresContainer) {
        try { Remove-TermLinkContainerPath -ContainerId $postgresContainer -Path $remoteRoot }
        catch { $remoteCleanupFailed = $true; Write-Warning 'PostgreSQL restore staging cleanup failed.' }
    }
    if ($null -ne $minioContainer) {
        try { Remove-TermLinkContainerPath -ContainerId $minioContainer -Path $remoteRoot }
        catch { $remoteCleanupFailed = $true; Write-Warning 'MinIO restore staging cleanup failed.' }
    }
    if ($restoreSucceeded -and $stoppedServices.Count -gt 0) {
        Start-TermLinkServices -ComposeArguments $compose -Services $stoppedServices
    }
    elseif (-not $restoreSucceeded -and $stoppedServices.Count -gt 0) {
        Write-Warning "Restore failed; quiesced services remain stopped: $($stoppedServices -join ', ')"
    }
    if ($restoreSucceeded -and $remoteCleanupFailed) {
        throw 'Restore completed, but plaintext container staging cleanup failed.'
    }
}
