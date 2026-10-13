[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runner = Join-Path $PSScriptRoot 'Invoke-RestoreProductE2E.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0 -or $null -eq $ast) {
    throw ('RESTORE_RUNNER_PARSE_FAILED: ' + (($errors | ForEach-Object Message) -join '; '))
}
$source = [IO.File]::ReadAllText($runner)
foreach ($required in @('close_view', 'reopen_selected', 'reselect_armed_session',
    'force-stop',
    'delta_request_count', 'delta_install_count', 'base_sha256', 'target_sha256',
    'delta_fallback_reason', 'wire_chunk_ack_verified = $false',
    'wal_recovery_verified = $false', '[string]$PreClosedBaselinePath',
    'Assert-PreClosedBaseline', 'Read-PreClosedBaseline',
    'pre_closed_baseline_used', 'pre-closed-reopen',
    '[long]$ExpectedFullSnapshotSequence', 'Assert-FullSnapshotFallback',
    'full_snapshot_fallback_verified_base_retirement_unverified')) {
    if (-not $source.Contains($required)) { throw "RESTORE_RUNNER_PROOF_MISSING: $required" }
}
$requireDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Require-Restore'
}, $true)
$fieldDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-RestoreField'
}, $true)
$preClosedDefinition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Assert-PreClosedBaseline'
}, $true)
if ($null -eq $requireDefinition -or $null -eq $fieldDefinition -or $null -eq $preClosedDefinition) {
    throw 'RESTORE_PRE_CLOSED_ASSERTION_MISSING'
}
. ([ScriptBlock]::Create($requireDefinition.Extent.Text))
. ([ScriptBlock]::Create($fieldDefinition.Extent.Text))
. ([ScriptBlock]::Create($preClosedDefinition.Extent.Text))
$preRunId = 'restore-preclosed-test'
$preSessionId = [guid]::NewGuid()
$preEnvelope = [pscustomobject]@{
    schema_version = 1
    run_id = $preRunId
    session_id = $preSessionId.ToString('D')
    baseline = [pscustomobject]@{
        session_id = $preSessionId.ToString('D')
        live_ack = 12
        restore_diagnostics = [pscustomobject]@{ attempt = 1 }
    }
    closed = [pscustomobject]@{
        session_id = $preSessionId.ToString('D')
        view_closed = $true
        session_running = $true
    }
}
$preBaseline = Assert-PreClosedBaseline $preEnvelope $preRunId $preSessionId
if ([long]$preBaseline.live_ack -ne 12) { throw 'RESTORE_PRE_CLOSED_POSITIVE_FAILED' }
foreach ($bad in @(
    @{ name = 'run-id'; mutate = { param($value) $value.run_id = 'wrong-run' } },
    @{ name = 'session-id'; mutate = { param($value) $value.session_id = ([guid]::NewGuid()).ToString('D') } },
    @{ name = 'closed-view'; mutate = { param($value) $value.closed.view_closed = $false } },
    @{ name = 'closed-session'; mutate = { param($value) $value.closed.session_id = ([guid]::NewGuid()).ToString('D') } }
)) {
    $candidate = $preEnvelope | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    & $bad.mutate $candidate
    $rejected = $false
    try { Assert-PreClosedBaseline $candidate $preRunId $preSessionId | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "RESTORE_PRE_CLOSED_NEGATIVE_FAILED: $($bad.name)" }
}
if (-not $source.Contains("if ([string]::IsNullOrWhiteSpace(`$PreClosedBaselinePath))")) {
    throw 'RESTORE_OLD_CLOSE_PATH_NOT_PRESERVED'
}
$powershell = (Get-Process -Id $PID).Path
$result = & $powershell -NoProfile -File $runner -SelfTest 2>&1
if ($LASTEXITCODE -ne 0 -or @($result).Count -ne 1 -or
    [string](@($result)[0]) -cne 'Restore E2E assertion self-test passed; no ADB or host was started.') {
    throw ('RESTORE_RUNNER_SELF_TEST_FAILED: ' + (@($result) -join ' | '))
}
$baseArguments = @('-NoProfile', '-File', $runner, '-Mode', 'Fallback',
    '-RunId', 'restore-parameter-test', '-ExpectedSessionId', $preSessionId.ToString('D'),
    '-ExpectedFallbackReason', 'NEEDS_FULL_SNAPSHOT', '-AdbPath', $runner)
$hashA = 'a' * 64
$hashB = 'b' * 64
foreach ($case in @(
    @{ name = 'missing-base'; arguments = @('-ExpectedTargetSha256', $hashB, '-ExpectedFullSnapshotSequence', '16');
        errorCode = 'FULL_SNAPSHOT_EXPECTATIONS_REQUIRED' },
    @{ name = 'missing-target'; arguments = @('-ExpectedBaseSha256', $hashA, '-ExpectedFullSnapshotSequence', '16');
        errorCode = 'FULL_SNAPSHOT_EXPECTATIONS_REQUIRED' },
    @{ name = 'missing-sequence'; arguments = @('-ExpectedBaseSha256', $hashA, '-ExpectedTargetSha256', $hashB);
        errorCode = 'FULL_SNAPSHOT_EXPECTATIONS_REQUIRED' },
    @{ name = 'unsupported-reason'; arguments = @('-ExpectedBaseSha256', $hashA,
            '-ExpectedTargetSha256', $hashB, '-ExpectedFullSnapshotSequence', '16');
        reason = 'FAILED'; errorCode = 'FULL_SNAPSHOT_REASON_UNSUPPORTED' }
)) {
    $arguments = $baseArguments + $case.arguments
    if ($case.ContainsKey('reason')) {
        $arguments[[Array]::IndexOf($arguments, 'NEEDS_FULL_SNAPSHOT')] = $case.reason
    }
    $failure = & $powershell @arguments 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or -not $failure.Contains($case.errorCode)) {
        throw "RESTORE_FALLBACK_PARAMETER_GUARD_FAILED: $($case.name): $failure"
    }
}
'Restore product E2E parser, contract guards, and negative assertion fixtures passed.'
