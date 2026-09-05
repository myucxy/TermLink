[CmdletBinding()]
param(
    [string]$ComposeFile = (Join-Path $PSScriptRoot '..\compose\integration.compose.yml')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'TermLinkBackup.Common.psm1') -Force

function Assert-Drill {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$id = [guid]::NewGuid().ToString('N')
$projectName = "termlink-drill-$($id.Substring(0, 12))"
$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "termlink-item7-drill-$id"))
$expectedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Assert-Drill ($tempRoot.StartsWith($expectedTempRoot, [StringComparison]::OrdinalIgnoreCase)) 'Unsafe drill temp path.'
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null

$composeFilePath = [IO.Path]::GetFullPath($ComposeFile)
$compose = New-TermLinkComposeArguments -ComposeFile @($composeFilePath) -ProjectName $projectName
$backupPath = Join-Path $tempRoot 'backup'
$manifestAuthenticationKeyPath = Join-Path $tempRoot 'manifest-authentication.key'
[IO.File]::WriteAllBytes($manifestAuthenticationKeyPath, [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$wrongManifestAuthenticationKeyPath = Join-Path $tempRoot 'wrong-manifest-authentication.key'
[IO.File]::WriteAllBytes($wrongManifestAuthenticationKeyPath, [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$postgresContainer = $null

try {
    Invoke-TermLinkDocker -Arguments ($compose + @('up', '-d', '--wait', 'postgres')) | Out-Null
    $postgresContainer = Get-TermLinkContainerId -ComposeArguments $compose -Service 'postgres'

    Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer, 'psql', '--username', 'termlink', '--dbname', 'termlink',
        '--set', 'ON_ERROR_STOP=1', '--command',
        "CREATE TABLE drill_seed(id integer PRIMARY KEY, value text NOT NULL); INSERT INTO drill_seed VALUES (1, 'before-backup');"
    ) | Out-Null
    & (Join-Path $PSScriptRoot 'Backup-TermLink.ps1') `
        -ComposeFile @($composeFilePath) `
        -ProjectName $projectName `
        -Destination $backupPath `
        -ManifestAuthenticationKeyFile $manifestAuthenticationKeyPath `
        -DestinationEncryptionConfirmed

    Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer, 'psql', '--username', 'termlink', '--dbname', 'termlink',
        '--set', 'ON_ERROR_STOP=1', '--command',
        "DROP TABLE drill_seed; CREATE TABLE after_backup(value text NOT NULL); INSERT INTO after_backup VALUES ('must-disappear');"
    ) | Out-Null
    $wrongKeyRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'Restore-TermLink.ps1') `
            -ComposeFile @($composeFilePath) `
            -ProjectName $projectName `
            -BackupPath $backupPath `
            -ManifestAuthenticationKeyFile $wrongManifestAuthenticationKeyPath `
            -ConfirmDestructiveRestore
    }
    catch {
        $wrongKeyRejected = $_.Exception.Message.Contains('manifest authentication failed')
    }
    Assert-Drill $wrongKeyRejected 'Restore did not reject an incorrect manifest authentication key.'
    $preRestoreProbe = Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer, 'psql', '--username', 'termlink', '--dbname', 'termlink',
        '--tuples-only', '--no-align', '--command', "SELECT value FROM after_backup;"
    )
    Assert-Drill ((($preRestoreProbe.Output -join '').Trim()) -eq 'must-disappear') `
        'Manifest authentication failure mutated the database.'

    $dumpPath = Join-Path $backupPath 'database.dump'
    $originalDump = [IO.File]::ReadAllBytes($dumpPath)
    $tamperedDump = [byte[]]::new($originalDump.Length + 1)
    [Array]::Copy($originalDump, $tamperedDump, $originalDump.Length)
    $tamperedDump[$tamperedDump.Length - 1] = 0x7f
    [IO.File]::WriteAllBytes($dumpPath, $tamperedDump)
    $tamperRejected = $false
	$tamperError = ''
    try {
        & (Join-Path $PSScriptRoot 'Restore-TermLink.ps1') `
            -ComposeFile @($composeFilePath) `
            -ProjectName $projectName `
            -BackupPath $backupPath `
            -ManifestAuthenticationKeyFile $manifestAuthenticationKeyPath `
            -ConfirmDestructiveRestore
    }
    catch {
		$tamperError = $_.Exception.Message
		$tamperRejected = $tamperError.Contains('SHA-256 mismatch') -or $tamperError.Contains('size mismatch')
    }
    finally {
        [IO.File]::WriteAllBytes($dumpPath, $originalDump)
    }
    Assert-Drill $tamperRejected "Tampered backup was not rejected by manifest validation: $tamperError"
    $mutationProbe = Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer, 'psql', '--username', 'termlink', '--dbname', 'termlink',
        '--tuples-only', '--no-align', '--command', "SELECT value FROM after_backup;"
    )
    Assert-Drill ((($mutationProbe.Output -join '').Trim()) -eq 'must-disappear') `
        'Tamper rejection mutated the database.'

    & (Join-Path $PSScriptRoot 'Restore-TermLink.ps1') `
        -ComposeFile @($composeFilePath) `
        -ProjectName $projectName `
        -BackupPath $backupPath `
        -ManifestAuthenticationKeyFile $manifestAuthenticationKeyPath `
        -ConfirmDestructiveRestore

    $databaseProbe = Invoke-TermLinkDocker -Arguments @(
        'exec', $postgresContainer, 'psql', '--username', 'termlink', '--dbname', 'termlink',
        '--tuples-only', '--no-align', '--command',
        "SELECT value || ':' || (to_regclass('public.after_backup') IS NULL)::text FROM drill_seed WHERE id=1;"
    )
    Assert-Drill ((($databaseProbe.Output -join '').Trim()) -eq 'before-backup:true') `
        'Restored PostgreSQL state did not exactly match the backup.'

    Write-Host 'TermLink PostgreSQL backup and destructive restore drill passed.'
}
finally {
    Invoke-TermLinkDocker -Arguments ($compose + @('down', '-v', '--remove-orphans')) -AllowFailure | Out-Null
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $resolvedCleanup = [IO.Path]::GetFullPath($tempRoot)
        Assert-Drill ($resolvedCleanup.StartsWith($expectedTempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            ([IO.Path]::GetFileName($resolvedCleanup)).StartsWith('termlink-item7-drill-', [StringComparison]::Ordinal)) `
            'Refusing unsafe drill cleanup target.'
        [IO.Directory]::Delete($resolvedCleanup, $true)
    }
}
