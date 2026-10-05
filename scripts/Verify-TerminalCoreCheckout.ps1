[CmdletBinding()]
param(
    [string]$ApplicationRoot = (Join-Path $PSScriptRoot '..'),
    [switch]$RequireClean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Git {
    param([string]$Root, [string[]]$Arguments)
    $output = @(& git -C $Root @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Git failed in $Root." }
    return $output
}

$application = [IO.Path]::GetFullPath($ApplicationRoot).TrimEnd('\', '/')
$core = [IO.Path]::GetFullPath((Join-Path $application '..\terminal-core'))
$lock = Get-Content -LiteralPath (Join-Path $application 'terminal-core.lock.json') -Raw | ConvertFrom-Json
if ($lock.schema_version -ne 1 -or $lock.revision -cnotmatch '^[0-9a-f]{40}$') {
    throw 'Invalid terminal-core revision lock.'
}
if (-not (Test-Path -LiteralPath (Join-Path $core '.git')) -or
    -not (Test-Path -LiteralPath (Join-Path $core 'Cargo.toml'))) {
    throw "Clone $($lock.repository) beside the application repository: $core"
}

$states = @{}
foreach ($entry in @(@('application', $application), @('core', $core))) {
    $name, $root = $entry
    $top = [IO.Path]::GetFullPath((Read-Git $root @('rev-parse', '--show-toplevel'))).TrimEnd('\', '/')
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($root, $top)) {
        throw "$name must be an independent repository root: $root"
    }
    $revision = (Read-Git $root @('rev-parse', 'HEAD')).Trim()
    $dirty = @(Read-Git $root @('status', '--porcelain', '--untracked-files=normal')).Count -ne 0
    if ($RequireClean -and $dirty) { throw "$name repository has uncommitted changes." }
    $states[$name] = [pscustomobject]@{ revision = $revision; dirty = $dirty }
}
if ($states.core.revision -cne $lock.revision) {
    throw "terminal-core checkout mismatch: expected $($lock.revision), found $($states.core.revision)."
}
[pscustomobject]@{
    application = $states.application
    terminal_core = $states.core
    terminal_core_repository = $lock.repository
}
