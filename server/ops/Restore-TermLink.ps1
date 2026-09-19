<#
.SYNOPSIS
    DEPRECATED for the current single-node deployment: this toolchain targets the
    retired multi-container stack (compose/core.compose.yml with Caddy/Valkey).

    For the current production form use the encrypted backup built into the
    single-node deployment tooling instead:
        server/deploy/single-node-production/termlink-deploy backup [options]
    (age-encrypted archive, SHA-256 manifest, guarded restore path).
    See server/deploy/single-node-production/README.md, section "backup".
#>
[CmdletBinding()]
param(
    [string[]]$ComposeFile = @((Join-Path $PSScriptRoot '..\compose\core.compose.yml')),
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
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$')]
    [string]$PostgresService = 'postgres',
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
if ($manifest.schema -cne 'termlink.backup-manifest' -or [int]$manifest.schema_version -ne 2) {
    throw 'Unsupported backup manifest schema.'
}
if ($manifest.project_name -cne $ProjectName -or $manifest.database -cne $Database) {
    throw 'Backup manifest target does not match the requested project and database.'
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
        $relative -cne 'database.dump') {
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
$actualPayloads = @(Get-ChildItem -LiteralPath $backupRoot -Recurse -File |
    Where-Object { $_.FullName -notin @($manifestPath, $manifestAuthenticationPath) })
if ($actualPayloads.Count -ne $entries.Count) {
    throw 'Backup contains payload files that are not covered by the manifest.'
}
$composeFiles = @(Resolve-TermLinkComposeFiles -ComposeFile $ComposeFile)
$compose = New-TermLinkComposeArguments -ComposeFile $composeFiles -ProjectName $ProjectName
$operationId = [guid]::NewGuid().ToString('N')
$remoteRoot = "/tmp/termlink-restore-$operationId"
$postgresContainer = $null
$stoppedServices = @()
$restoreSucceeded = $false

try {
    $stoppedServices = @(Stop-TermLinkServices -ComposeArguments $compose -Candidates @('lucky-upstream', 'server'))
    $postgresContainer = Get-TermLinkContainerId -ComposeArguments $compose -Service $PostgresService
    Invoke-TermLinkDocker -Arguments @('exec', $postgresContainer, 'mkdir', '-p', $remoteRoot) | Out-Null
    Invoke-TermLinkDocker -Arguments @('cp', $validatedPaths['database.dump'], "${postgresContainer}:$remoteRoot/database.dump") | Out-Null
    $databaseEntry = @($entries | Where-Object { [string]$_.path -ceq 'database.dump' })
    if ($databaseEntry.Count -ne 1 -or
        (Get-TermLinkContainerFileSha256 -ContainerId $postgresContainer -Path "$remoteRoot/database.dump") -cne
            [string]$databaseEntry[0].sha256) {
        throw 'Staged PostgreSQL dump SHA-256 does not match the authenticated manifest.'
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

    $restoreSucceeded = $true
    Write-Host "TermLink restore completed from: $backupRoot"
}
finally {
    $remoteCleanupFailed = $false
    if ($null -ne $postgresContainer) {
        try { Remove-TermLinkContainerPath -ContainerId $postgresContainer -Path $remoteRoot }
        catch { $remoteCleanupFailed = $true; Write-Warning 'PostgreSQL restore staging cleanup failed.' }
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
