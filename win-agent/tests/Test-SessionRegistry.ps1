[CmdletBinding()]
param(
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase1-session-registry.json')
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-ResultFields {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Prefix)
    $line = @($Text -split "`r?`n" | Where-Object { $_.StartsWith($Prefix) }) | Select-Object -Last 1
    if (-not $line) { throw "Result line '$Prefix' was not captured: $Text" }
    $fields = [ordered]@{}
    foreach ($part in $line.Substring($Prefix.Length).Split(';')) {
        $pair = $part.Split([char[]]@('='), 2)
        if ($pair.Count -eq 2) { $fields[$pair[0]] = $pair[1] }
    }
    $fields
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler not found: $compiler"
}

$agentExecutable = & (Join-Path $PSScriptRoot '..\src\Agent\Build-Agent.ps1')
$hostExecutable = & (Join-Path $PSScriptRoot '..\src\SessionHost\Build-SessionHost.ps1')
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Registry-' + [Guid]::NewGuid().ToString('N'))
$testExecutable = Join-Path $temporaryDirectory 'SessionRegistryValidation.exe'
$bufferRoot = Join-Path $temporaryDirectory 'buffers'
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
[IO.Directory]::CreateDirectory($bufferRoot) | Out-Null

try {
    & $compiler `
        /nologo `
        /target:exe `
        /platform:x64 `
        /optimize+ `
        /r:System.Core.dll `
        /r:System.Security.dll `
        /main:TermLink.Agent.RegistryValidationProgram `
        "/out:$testExecutable" `
        (Join-Path $PSScriptRoot '..\src\Common\LocalIpc.cs') `
        (Join-Path $PSScriptRoot '..\src\AgentControl\AgentControl.cs') `
        (Join-Path $PSScriptRoot '..\src\Agent\CurrentUserPipe.cs') `
        (Join-Path $PSScriptRoot '..\src\Agent\SessionRegistrationStore.cs') `
        (Join-Path $PSScriptRoot '..\src\Agent\SessionRegistry.cs') `
        (Join-Path $PSScriptRoot '..\src\Agent\SessionAgent.cs') `
        (Join-Path $PSScriptRoot 'SessionRegistryValidation.cs')
    if ($LASTEXITCODE -ne 0) { throw "Session Registry validation compilation failed with exit code $LASTEXITCODE." }

    $output = & $testExecutable $agentExecutable $hostExecutable $bufferRoot 2>&1
    $testExit = $LASTEXITCODE
    $text = $output -join "`n"
    Assert-True ($testExit -eq 0) "Session Registry validation failed with exit code ${testExit}: $text"
    $fields = Get-ResultFields -Text $text -Prefix '__REGISTRY_TEST_RESULT__:'
    Assert-True ([int]$fields.multi_sessions -eq 3) 'Multi-session Registry coverage did not run.'
    Assert-True ($fields.summary_redacted -eq 'true') 'Summary redaction did not pass.'
    Assert-True ($fields.single_stop_tombstoned -eq 'true') 'Single-session stop tombstone did not block re-registration.'
    Assert-True ($fields.remote_policy_persisted -eq 'true') 'Remote-access policy did not survive Agent restart.'
    Assert-True ($fields.policy_corruption_fail_closed -eq 'true') 'Damaged remote-access policy did not fail closed.'
    Assert-True ($fields.auto_registration_stop_stable -eq 'true') 'Auto-register loop restored an explicitly stopped Session.'
    Assert-True ([int]$fields.concurrent_stop_winners -eq 1) 'Concurrent stop was not idempotent.'
    Assert-True ($fields.abnormal_reclaimed -eq 'true') 'Abnormal Host was not reclaimed.'
    Assert-True ($fields.lost_host_reclaimed -eq 'true') 'Disconnected live Host was not reclaimed.'
    Assert-True ([int]$fields.stop_all_sessions -eq 2) 'Global remote stop did not cover both sessions.'
    Assert-True ($fields.registration_blocked -eq 'true') 'Global remote stop did not block new registration.'
    Assert-True ([int]$fields.registry_close_sessions -eq 2) 'Registry close coverage did not run.'
    Assert-True ([int]$fields.local_shells_survived -eq 7) 'Local Shell survival checks did not all pass.'
    Assert-True ($fields.stop_timeout_stable -eq 'true') 'Single-session stop timeout result was not stable.'
    Assert-True ($fields.batch_timeout_reported -eq 'true') 'Batch timeout coverage did not pass.'
    Assert-True ($fields.shutdown_best_effort -eq 'true') 'Shutdown best-effort cleanup did not pass.'
    Assert-True ($fields.restart_timeout_reported -eq 'true') 'Controlled restart timeout coverage did not pass.'
    Assert-True ($fields.completed_buffer_recovered -eq 'true') 'Completed buffer restart recovery did not pass.'
    Assert-True ($fields.live_interrupted_committed -eq 'true') 'Live Host loss did not commit Interrupted.'
    Assert-True ($fields.external_registry_entry -eq 'true') 'External Agent Registry entry was not tested.'

    $evidence = [ordered]@{
        schema = 'termlink.phase1-session-registry-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = 'local Win-Agent Registry and current-user control IPC; not production Relay or protocol'
        passed = $true
        registry_process_model = 'one long-lived Registry with one isolated SessionAgent worker per Host'
        local_control_protocol = 'current-user Registry Pipe V3; not production Protobuf'
        simultaneous_sessions = [int]$fields.multi_sessions
        summaries_exclude_terminal_body_pipe_token_and_buffer_path = [bool]::Parse($fields.summary_redacted)
        single_remote_stop_preserved_local_shell = [bool]::Parse($fields.single_stop_local_alive)
        single_remote_stop_blocked_host_reregistration = [bool]::Parse($fields.single_stop_tombstoned)
        remote_access_policy_survived_agent_restart = [bool]::Parse($fields.remote_policy_persisted)
        damaged_remote_access_policy_failed_closed = [bool]::Parse($fields.policy_corruption_fail_closed)
        stopped_auto_registration_remained_stopped = [bool]::Parse($fields.auto_registration_stop_stable)
        concurrent_stop_successes = [int]$fields.concurrent_stop_winners
        abnormal_host_reclaimed = [bool]::Parse($fields.abnormal_reclaimed)
        disconnected_live_host_reclaimed = [bool]::Parse($fields.lost_host_reclaimed)
        stop_all_session_count = [int]$fields.stop_all_sessions
        stop_all_blocked_new_registration = [bool]::Parse($fields.registration_blocked)
        registry_shutdown_session_count = [int]$fields.registry_close_sessions
        local_shells_survived_remote_stop_or_registry_shutdown = [int]$fields.local_shells_survived
        single_stop_timeout_code = 'STOP_TIMEOUT'
        batch_stop_timeout_code = 'REMOTE_ACCESS_STOP_TIMEOUT'
        shutdown_timeout_code = 'REGISTRY_SHUTDOWN_WITH_TIMEOUTS'
        restart_timeout_code = 'AGENT_RESTART_WITH_TIMEOUTS'
        completed_buffer_restart_recovery = [bool]::Parse($fields.completed_buffer_recovered)
        live_host_loss_committed_interrupted = [bool]::Parse($fields.live_interrupted_committed)
        shutdown_best_effort_cleanup = [bool]::Parse($fields.shutdown_best_effort)
        external_agent_registry_entry = [bool]::Parse($fields.external_registry_entry)
        host_agent_authentication = 'session ID + 32-byte token + claimed PID + OS peer PID + Registry expected PID'
        buffer_compatibility = 'existing DPAPI CurrentUser event chunks and input_id dedupe retained per session'
    }
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    Write-Host "Phase 1 Session Registry validation passed. Evidence: $resolvedEvidence"
}
finally {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
    if ($resolvedTemporary.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemporary).StartsWith('TermLink-Registry-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force -ErrorAction SilentlyContinue
    }
}
