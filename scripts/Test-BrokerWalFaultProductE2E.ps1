[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runner = Join-Path $PSScriptRoot 'Invoke-BrokerWalFaultProductE2E.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($null -eq $ast -or $errors.Count -ne 0) {
    throw ('BROKER_WAL_FAULT_PARSE_FAILED: ' + (($errors | ForEach-Object Message) -join '; '))
}
$source = [IO.File]::ReadAllText($runner)
foreach ($required in @('Assert-Arm', 'Assert-Baseline', 'Assert-Recovered',
    'broker-send.trigger', 'WAL_BLOCKER_LOST', 'BROKER_SIDECAR_NOT_OBSERVED',
    'BROKER_WAL_FAULT_NOT_PROVEN', 'BROKER_RETRY_NOT_CORRELATED',
    '[IO.Directory]::Delete($blockedWalPath, $false)',
    'broker_capture_last_sequence -eq $next', 'ack_during_fault',
    'GetOwner([Security.Principal.SecurityIdentifier])', 'GetAccessRules($true, $true',
    'FOREIGN_WRITE_ACL_ACCEPTED', 'Assert-LocalDirectory $runRoot',
    'Assert-LocalDirectory $history', 'Assert-LocalDirectory $captureRoot',
    'Read-HookResponseForensics', 'Save-HookFailureLogcat',
    'first_retry_failure_code', 'retry_inspect_failures', 'HOOK_RESPONSE_TIMEOUT')) {
    if (-not $source.Contains($required)) { throw "BROKER_WAL_FAULT_GUARD_MISSING: $required" }
}
if ($source.Contains('Remove-Item -Recurse') -or $source.Contains('Directory]::Delete($history')) {
    throw 'BROKER_WAL_FAULT_UNSAFE_CLEANUP'
}
if ($source.Contains('$State.session_running')) {
    throw 'BROKER_WAL_FAULT_UNSUPPORTED_INSPECT_FIELD'
}
if (-not $source.Contains('BROKER_RETRY_WAL_MISSING') -or
    -not $source.Contains('MISSING_COMMITTED_WAL_ACCEPTED')) {
    throw 'BROKER_WAL_FAULT_DURABILITY_GUARD_MISSING'
}
$powershell = (Get-Process -Id $PID).Path
$result = & $powershell -NoProfile -File $runner -SelfTest 2>&1
if ($LASTEXITCODE -ne 0 -or @($result).Count -ne 1 -or
    [string](@($result)[0]) -cne 'Broker WAL fault assertion self-test passed; no ADB or host was started.') {
    throw ('BROKER_WAL_FAULT_SELF_TEST_FAILED: ' + (@($result) -join ' | '))
}
'Broker WAL fault parser, guard, and negative assertion fixtures passed.'
