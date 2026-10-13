[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runner = Join-Path $PSScriptRoot 'Invoke-ShellIntegrationProductE2E.ps1'
$runnerSource = [IO.File]::ReadAllText($runner)
if ($runnerSource.Contains('uiautomator') -or
    -not $runnerSource.Contains('-File $UiHierarchyScript') -or
    -not $runnerSource.Contains('$UiTestPackage')) {
    throw 'SHELL_UI_DUMP_MUST_USE_INTERACTIVE_WINDOW_CAPTURE'
}
if ($runnerSource.Contains('host_stdout_tail') -or $runnerSource.Contains('host_stderr_tail')) {
    throw 'SHELL_E2E_RAW_HOST_OUTPUT_FORBIDDEN'
}
if ($runnerSource.Contains('$evidence.failure_reason') -or
    $runnerSource.Contains('$evidence.failure_inner_reason')) {
    throw 'SHELL_E2E_RAW_EXCEPTION_EVIDENCE_FORBIDDEN'
}
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($null -eq $ast -or $errors.Count -ne 0) {
    throw ('SHELL_E2E_PARSE_FAILED: ' + (($errors | ForEach-Object Message) -join '; '))
}

foreach ($name in @('Require-Shell', 'Assert-ShellControl', 'Assert-CompletedShellCommand',
        'Test-RetryableShellObservation', 'Test-OwnedDisplayIdentity')) {
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $false)
    if ($null -eq $function) { throw 'SHELL_CONTROL_GUARD_MISSING' }
    . ([scriptblock]::Create($function.Extent.Text))
}
$runnerFunctions = @{}
foreach ($name in @('Assert-AgentRelay', 'Invoke-Program', 'Stop-OwnedDisplay')) {
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $false)
    if ($null -eq $function) { throw 'SHELL_CLEANUP_GUARD_MISSING' }
    $runnerFunctions[$name] = $function.Extent.Text
}
if ($runnerSource.Contains('function Start-LegacyProgram') -or
    $runnerFunctions['Assert-AgentRelay'].Contains('Start-Sleep') -or
    $runnerFunctions['Assert-AgentRelay'].Contains('$attempt')) {
    throw 'SHELL_REDUNDANT_RETRY_FOUND'
}
if (-not $runnerFunctions['Invoke-Program'].Contains('finally') -or
    -not $runnerFunctions['Invoke-Program'].Contains('Kill($true)') -or
    -not $runnerFunctions['Invoke-Program'].Contains('Dispose()')) {
    throw 'SHELL_PROCESS_CLEANUP_GUARD_MISSING'
}
if ($runnerFunctions['Stop-OwnedDisplay'].IndexOf('Test-OwnedDisplayIdentity') -lt 0 -or
    $runnerFunctions['Stop-OwnedDisplay'].IndexOf('CloseMainWindow') -lt
        $runnerFunctions['Stop-OwnedDisplay'].IndexOf('Test-OwnedDisplayIdentity') -or
    -not $runnerFunctions['Stop-OwnedDisplay'].Contains('Kill($true)')) {
    throw 'SHELL_DISPLAY_CLEANUP_GUARD_MISSING'
}
$session = [guid]::NewGuid()
Assert-ShellControl ([pscustomobject]@{ session_id = "$session"; realtime_subscriber_ready = $true;
    realtime_remote_state = 'CONTROLLING' }) $session
foreach ($state in @('READ_ONLY', 'LIVE', 'RECONNECTING')) {
    try {
        Assert-ShellControl ([pscustomobject]@{ session_id = "$session"; realtime_subscriber_ready = $true;
            realtime_remote_state = $state }) $session
        throw 'SHELL_CONTROL_GUARD_ACCEPTED_NONCONTROLLING'
    } catch {
        if ($_.Exception.Message -cne 'ANDROID_CONTROL_NOT_READY') { throw }
    }
}
try {
    Assert-ShellControl ([pscustomobject]@{ session_id = "$session"; realtime_subscriber_ready = $false;
        realtime_remote_state = 'CONTROLLING' }) $session
    throw 'SHELL_CONTROL_GUARD_ACCEPTED_UNREADY'
} catch {
    if ($_.Exception.Message -cne 'ANDROID_CONTROL_NOT_READY') { throw }
}
$cwdHash = 'a' * 64
$point = [pscustomobject]@{ sequence = 1; line_id = 0; cell_offset = 0; page = 1; alternate = $false }
$command = [pscustomobject]@{ id = 2; phase = 3; exit_code = 0; cwd_path_sha256 = $cwdHash;
    prompt = $point; input = $point; output = $point; end = $point }
$inspection = [pscustomobject]@{
    shell_semantics = [pscustomobject]@{ cwd_path_sha256 = $cwdHash; command_count = 1;
        next_command_id = 3; commands = @($command) }
    terminal_semantics = [pscustomobject]@{ next_source_sequence = 2 }
}
if ((Assert-CompletedShellCommand $inspection $cwdHash 1 0).id -ne 2) {
    throw 'SHELL_ADJACENT_COMMAND_REJECTED'
}
$command.id = 3
try {
    $null = Assert-CompletedShellCommand $inspection $cwdHash 1 0
    throw 'SHELL_SKIPPED_COMMAND_ACCEPTED'
} catch {
    if ($_.Exception.Message -cne 'SHELL_COMMAND_ID_NOT_ADJACENT') { throw }
}
if (-not (Test-RetryableShellObservation 'SHELL_COMMAND_ANCHOR_MISSING') -or
    (Test-RetryableShellObservation 'SHELL_COMMAND_ID_NOT_ADJACENT') -or
    (Test-RetryableShellObservation 'SHELL_COMMAND_CWD_OR_ID_INVALID')) {
    throw 'SHELL_OBSERVATION_RETRY_GUARD_FAILED'
}
$displayStart = [DateTime]::UtcNow
$display = [pscustomobject]@{ HasExited = $false; StartTime = $displayStart }
$ownedIdentity = [pscustomobject]@{ ParentProcessId = 1234; ExecutablePath = 'C:\runner\display.exe' }
if (-not (Test-OwnedDisplayIdentity $display $ownedIdentity 1234 'C:\runner\display.exe' $displayStart)) {
    throw 'SHELL_DISPLAY_IDENTITY_GUARD_FAILED'
}
foreach ($invalidIdentity in @(
    [pscustomobject]@{ ParentProcessId = 4321; ExecutablePath = 'C:\runner\display.exe' },
    [pscustomobject]@{ ParentProcessId = 1234; ExecutablePath = 'C:\other\display.exe' }
)) {
    if (Test-OwnedDisplayIdentity $display $invalidIdentity 1234 'C:\runner\display.exe' $displayStart) {
        throw 'SHELL_DISPLAY_IDENTITY_NEGATIVE_ACCEPTED'
    }
}

$shell = (Get-Process -Id $PID).Path
$output = & $shell -NoProfile -File $runner -DefinitionsOnly -RunId 'shell-selftest' 2>&1
if ($LASTEXITCODE -ne 0) { throw ('SHELL_E2E_PLAN_FAILED: ' + (@($output) -join ' | ')) }
$plan = (@($output) -join [Environment]::NewLine) | ConvertFrom-Json
$windowCheck = & $shell -NoProfile -File $runner -WindowInputSelfCheck 2>&1
if ($LASTEXITCODE -ne 0 -or -not ((@($windowCheck) -join ' ').Contains('Window input ownership self-check passed.'))) {
    throw ('SHELL_E2E_WINDOW_GUARD_FAILED: ' + (@($windowCheck) -join ' | '))
}
$canonicalHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes('c:\work\中文 空格'))).ToLowerInvariant()
if ($plan.first_marker -cne 'TLTF_shell_selftest_0001' -or
    $plan.last_marker -cne 'TLTF_shell_selftest_5200' -or
    $plan.live_marker -cnotmatch '^TLTF_LIVE_[a-f0-9]{16}$' -or
    $plan.generator_command -cnotmatch '1\.\.5200' -or
    -not $plan.generator_command.Contains($plan.live_marker) -or
    $plan.generator_command.Contains([char]27) -or
    $plan.location_filler_command -cne '1..100|%{"H01_LOCATION_{0:D3}"-f $_}' -or
    $plan.shell_arguments -cne '-NoLogo -NoProfile' -or
    $plan.managed_shell -cne 'powershell-7' -or
    (@($plan.android_commands) -join ',') -cne 'pwd,throw' -or
    $plan.directory_change_command -cne 'cd ..' -or
    $plan.working_directory_leaf -cne '中文 空格' -or
    $plan.canonical_path_example_sha256 -cne $canonicalHash) {
    throw 'SHELL_E2E_PLAN_INVALID'
}

# Guards must fail before creating a run directory or touching ADB.
$negative = & $shell -NoProfile -File $runner -RunId 'shell-selftest' 2>&1
if ($LASTEXITCODE -eq 0 -or -not ((@($negative) -join ' ').Contains('RUN_CONTEXT_REQUIRED'))) {
    throw 'SHELL_E2E_CONTEXT_GUARD_FAILED'
}
$negativeOrigin = & $shell -NoProfile -File $runner -RunId 'shell-selftest' -AcceptanceOrigin 'http://example.invalid/' -AgentControlPipe 'test-pipe' -SessionHostPath $shell -NativeDisplayPath $shell 2>&1
if ($LASTEXITCODE -eq 0 -or -not ((@($negativeOrigin) -join ' ').Contains('RUN_CONTEXT_REQUIRED'))) {
    throw 'SHELL_E2E_ORIGIN_GUARD_FAILED'
}
'Real shell integration E2E parser, isolated plan, window input guard, and no-ADB negative guards passed.'
