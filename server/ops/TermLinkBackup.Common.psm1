Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-TermLinkDocker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )
    $output = @(& docker @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $summary = ($output -join ' ').Trim()
        if ($summary.Length -gt 1200) { $summary = $summary.Substring(0, 1200) }
        throw "docker failed with exit code ${exitCode}: $summary"
    }
    [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Resolve-TermLinkComposeFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$ComposeFile)
    $resolved = foreach ($file in $ComposeFile) {
        $path = [IO.Path]::GetFullPath($file)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Compose file not found: $path"
        }
        $path
    }
    return @($resolved)
}

function New-TermLinkComposeArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$ComposeFile,
        [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9_-]{2,62}$')][string]$ProjectName
    )
    $arguments = @('compose', '--project-name', $ProjectName)
    foreach ($file in $ComposeFile) { $arguments += @('-f', $file) }
    return $arguments
}

function Get-TermLinkContainerId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$ComposeArguments,
        [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,62}$')][string]$Service
    )
    $result = Invoke-TermLinkDocker -Arguments ($ComposeArguments + @('ps', '-q', $Service))
    $ids = @($result.Output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -ne '' })
    if ($ids.Count -ne 1 -or $ids[0] -notmatch '^[0-9a-f]{12,64}$') {
        throw "Expected exactly one running container for service '$Service'."
    }
    return $ids[0]
}

function Get-TermLinkRunningServices {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$ComposeArguments)
    $result = Invoke-TermLinkDocker -Arguments ($ComposeArguments + @('ps', '--status', 'running', '--services'))
    return @($result.Output | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -ne '' })
}

function Stop-TermLinkServices {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$ComposeArguments,
        [Parameter(Mandatory)][string[]]$Candidates
    )
    $running = @(Get-TermLinkRunningServices -ComposeArguments $ComposeArguments)
    $stopped = @()
    foreach ($service in $Candidates) {
        if ($running -contains $service) {
            Invoke-TermLinkDocker -Arguments ($ComposeArguments + @('stop', $service)) | Out-Null
            $stopped += $service
        }
    }
    return $stopped
}

function Start-TermLinkServices {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$ComposeArguments,
        [Parameter(Mandatory)][string[]]$Services
    )
    [array]::Reverse($Services)
    foreach ($service in $Services) {
        Invoke-TermLinkDocker -Arguments ($ComposeArguments + @('up', '-d', $service)) | Out-Null
    }
}

function Get-TermLinkRepositoryRoot {
    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
}

function Assert-TermLinkExternalPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$MustExist
    )
    $resolved = [IO.Path]::GetFullPath($Path)
    $repositoryRoot = (Get-TermLinkRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $repositoryPrefix = $repositoryRoot + [IO.Path]::DirectorySeparatorChar
    if ($resolved.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $resolved.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Backup payloads must be stored outside the source repository.'
    }
    if ($MustExist -and -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Directory not found: $resolved"
    }
    $probe = if (Test-Path -LiteralPath $resolved) { $resolved } else { [IO.Path]::GetDirectoryName($resolved) }
    while (-not [string]::IsNullOrWhiteSpace($probe) -and -not (Test-Path -LiteralPath $probe)) {
        $probe = [IO.Path]::GetDirectoryName($probe)
    }
    while (-not [string]::IsNullOrWhiteSpace($probe)) {
        $item = Get-Item -LiteralPath $probe -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse-point backup path is not allowed: $probe"
        }
        $parent = [IO.Path]::GetDirectoryName($probe)
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $probe) { break }
        $probe = $parent
    }
    return $resolved
}

function Assert-TermLinkPathHasNoReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )
    $resolved = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetFullPath($Root)
    $volumeRoot = [IO.Path]::GetPathRoot($rootPath)
    if (-not $rootPath.Equals($volumeRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $rootPath = $rootPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
    $prefix = if ($rootPath.EndsWith([IO.Path]::DirectorySeparatorChar)) {
        $rootPath
    }
    else { $rootPath + [IO.Path]::DirectorySeparatorChar }
    if (-not $resolved.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -and
        -not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the expected root: $resolved"
    }
    $probe = $resolved
    while ($true) {
        $item = Get-Item -LiteralPath $probe -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Path contains a reparse point: $probe"
        }
        if ($probe.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) { break }
        $probe = [IO.Path]::GetDirectoryName($probe)
        if ([string]::IsNullOrWhiteSpace($probe)) {
            throw "Path ancestry did not reach the expected root: $resolved"
        }
    }
    return $resolved
}

function Read-TermLinkAuthenticationKey {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Authentication key file not found: $resolved"
    }
    Assert-TermLinkPathHasNoReparsePoint -Path $resolved -Root ([IO.Path]::GetPathRoot($resolved)) | Out-Null
    $key = [IO.File]::ReadAllBytes($resolved)
    if ($key.Length -lt 32) {
        throw 'Backup manifest authentication key must contain at least 32 bytes.'
    }
    return ,([byte[]]$key)
}

function Assert-TermLinkAuthenticationKeySeparation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$KeyPath,
        [Parameter(Mandatory)][string]$BackupPath
    )
    $key = [IO.Path]::GetFullPath($KeyPath)
    $backup = [IO.Path]::GetFullPath($BackupPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $repository = (Get-TermLinkRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    foreach ($forbidden in @($backup, $repository)) {
        $prefix = $forbidden + [IO.Path]::DirectorySeparatorChar
        if ($key.Equals($forbidden, [StringComparison]::OrdinalIgnoreCase) -or
            $key.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Backup manifest authentication key must be stored outside both the backup and source repository.'
        }
    }
}

function Get-TermLinkHmacSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][byte[]]$Data,
        [Parameter(Mandatory)][byte[]]$Key
    )
    $algorithm = [Security.Cryptography.HMACSHA256]::new($Key)
    try { return ,([byte[]]$algorithm.ComputeHash($Data)) }
    finally { $algorithm.Dispose() }
}

function Get-TermLinkContainerFileSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerId,
        [Parameter(Mandatory)][ValidatePattern('^/tmp/termlink-(backup|restore)-[0-9a-f]{32}/.+$')][string]$Path
    )
    $result = Invoke-TermLinkDocker -Arguments @('exec', $ContainerId, 'sha256sum', '--', $Path)
    $output = ($result.Output -join [Environment]::NewLine).Trim()
    if ($output -notmatch '^([0-9a-fA-F]{64})\s') {
        throw "Container did not return a valid SHA-256 for staged restore payload: $Path"
    }
    return $Matches[1].ToLowerInvariant()
}

function Assert-TermLinkBucketName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Bucket)
    if ($Bucket -notmatch '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$' -or $Bucket.Contains('..')) {
        throw "Invalid S3 bucket name: $Bucket"
    }
}

function Initialize-TermLinkMinioAlias {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerId,
        [Parameter(Mandatory)][ValidatePattern('^/tmp/termlink-(backup|restore)-[0-9a-f]{32}/mc$')][string]$ConfigDir
    )
    $script = @'
set -eu
user="${MINIO_ROOT_USER:-}"
password="${MINIO_ROOT_PASSWORD:-}"
if [ -z "$user" ] && [ -n "${MINIO_ROOT_USER_FILE:-}" ]; then user="$(cat "$MINIO_ROOT_USER_FILE")"; fi
if [ -z "$password" ] && [ -n "${MINIO_ROOT_PASSWORD_FILE:-}" ]; then password="$(cat "$MINIO_ROOT_PASSWORD_FILE")"; fi
test -n "$user"
test -n "$password"
mc --config-dir "$1" alias set termlinkbackup http://127.0.0.1:9000 "$user" "$password" >/dev/null
unset user password
'@
    Invoke-TermLinkDocker -Arguments @('exec', $ContainerId, 'sh', '-c', $script, 'termlink-minio-alias', $ConfigDir) | Out-Null
}

function Invoke-TermLinkMinioClient {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerId,
        [Parameter(Mandatory)][ValidatePattern('^/tmp/termlink-(backup|restore)-[0-9a-f]{32}/mc$')][string]$ConfigDir,
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )
    return Invoke-TermLinkDocker -Arguments (@('exec', $ContainerId, 'mc', '--config-dir', $ConfigDir) + $Arguments) `
        -AllowFailure:$AllowFailure
}

function Remove-TermLinkContainerPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerId,
        [Parameter(Mandatory)][ValidatePattern('^/tmp/termlink-(backup|restore)-[0-9a-f]{32}(/.*)?$')][string]$Path
    )
    Invoke-TermLinkDocker -Arguments @('exec', $ContainerId, 'rm', '-rf', $Path) | Out-Null
}

Export-ModuleMember -Function @(
    'Invoke-TermLinkDocker',
    'Resolve-TermLinkComposeFiles',
    'New-TermLinkComposeArguments',
    'Get-TermLinkContainerId',
    'Get-TermLinkRunningServices',
    'Stop-TermLinkServices',
    'Start-TermLinkServices',
    'Assert-TermLinkExternalPath',
    'Assert-TermLinkPathHasNoReparsePoint',
    'Read-TermLinkAuthenticationKey',
    'Assert-TermLinkAuthenticationKeySeparation',
    'Get-TermLinkHmacSha256',
    'Get-TermLinkContainerFileSha256',
    'Assert-TermLinkBucketName',
    'Initialize-TermLinkMinioAlias',
    'Invoke-TermLinkMinioClient',
    'Remove-TermLinkContainerPath'
)
