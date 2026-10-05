[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$verify = Join-Path $PSScriptRoot 'Verify-TerminalCoreCheckout.ps1'
$scratch = Join-Path $PSScriptRoot ("../artifacts/workspace-layout-tests/" + [Guid]::NewGuid().ToString('N'))
$app = [IO.Path]::GetFullPath((Join-Path $scratch 'termlink'))
$core = [IO.Path]::GetFullPath((Join-Path $scratch 'terminal-core'))
$utf8 = [Text.UTF8Encoding]::new($false)

function Git-Checked {
    param([string]$Root, [string[]]$Arguments)
    $output = @(& git -C $Root @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git operation failed: $Arguments" }
    return $output
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Expected)
    try { $null = & $Action }
    catch {
        if ($_.Exception.Message -like "*$Expected*") { return }
        throw
    }
    throw "Expected rejection: $Expected"
}

foreach ($root in @($app, $core)) {
    $null = New-Item -ItemType Directory -Force -Path $root
    $null = Git-Checked $root @('init', '-q')
    $null = Git-Checked $root @('config', 'user.name', 'Workspace fixture')
    $null = Git-Checked $root @('config', 'user.email', 'workspace-fixture@example.invalid')
}
[IO.File]::WriteAllText((Join-Path $core 'Cargo.toml'), '# test fixture', $utf8)
$null = Git-Checked $core @('add', 'Cargo.toml')
$null = Git-Checked $core @('-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture core')
$revision = (Git-Checked $core @('rev-parse', 'HEAD')).Trim()
$lockPath = Join-Path $app 'terminal-core.lock.json'
$lock = @{ schema_version = 1; repository = 'fixture'; revision = $revision } | ConvertTo-Json
[IO.File]::WriteAllText($lockPath, $lock, $utf8)
$null = Git-Checked $app @('add', 'terminal-core.lock.json')
$null = Git-Checked $app @('-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture application')
$result = & $verify -ApplicationRoot $app -RequireClean
if ($result.terminal_core.revision -ne $revision -or $result.terminal_core.dirty) { throw 'Clean checkout not reported correctly' }

[IO.File]::AppendAllText((Join-Path $core 'Cargo.toml'), ' changed', $utf8)
$result = & $verify -ApplicationRoot $app
if (-not $result.terminal_core.dirty) { throw 'Dirty development checkout was hidden' }
Assert-Rejected { & $verify -ApplicationRoot $app -RequireClean } 'core repository has uncommitted changes'
[IO.File]::WriteAllText((Join-Path $core 'Cargo.toml'), '# test fixture', $utf8)

[IO.File]::WriteAllText($lockPath, $lock.Replace($revision, ('0' * 40)), $utf8)
Assert-Rejected { & $verify -ApplicationRoot $app } 'checkout mismatch'
[IO.File]::WriteAllText($lockPath, $lock, $utf8)

[IO.File]::WriteAllText((Join-Path $app 'untracked.txt'), 'test', $utf8)
Assert-Rejected { & $verify -ApplicationRoot $app -RequireClean } 'application repository has uncommitted changes'
$null = Git-Checked $app @('add', 'untracked.txt')
$null = Git-Checked $app @('-c', 'commit.gpgsign=false', 'commit', '-qm', 'record test file')
$null = & $verify -ApplicationRoot $app -RequireClean

$missingApp = Join-Path $scratch 'missing/termlink'
$null = New-Item -ItemType Directory -Force -Path $missingApp
[IO.File]::WriteAllText((Join-Path $missingApp 'terminal-core.lock.json'), $lock, $utf8)
Assert-Rejected { & $verify -ApplicationRoot $missingApp } 'Clone fixture beside'

[pscustomobject]@{ Passed = 6; FixtureDirectory = $scratch }
