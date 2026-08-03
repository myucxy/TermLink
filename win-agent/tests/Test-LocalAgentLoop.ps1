[CmdletBinding()]
param(
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase1-local-agent.json')
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-ResultFields {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Prefix
    )

    $line = @($Text -split "`r?`n" | Where-Object { $_.StartsWith($Prefix) }) | Select-Object -Last 1
    if (-not $line) { throw "Result line '$Prefix' was not captured: $Text" }
    $fields = [ordered]@{}
    foreach ($part in $line.Substring($Prefix.Length).Split(';')) {
        $pair = $part.Split([char[]]@('='), 2)
        if ($pair.Count -eq 2) { $fields[$pair[0]] = $pair[1] }
    }
    $fields
}

function Quote-Argument {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value.Contains('"')) { throw 'Test arguments must not contain quotes.' }
    '"' + $Value + '"'
}

$agentExecutable = & (Join-Path $PSScriptRoot '..\src\Agent\Build-Agent.ps1')
$hostExecutable = & (Join-Path $PSScriptRoot '..\src\SessionHost\Build-SessionHost.ps1')
$probeSource = Join-Path $PSScriptRoot '..\..\prototypes\conpty-probe\ConPtyNative.cs'
if (-not ('TermLink.Validation.ConPtyProbe' -as [type])) {
    Add-Type -Path $probeSource
}

$sessionId = [Guid]::NewGuid()
$inputId = [Guid]::NewGuid()
$pipeName = 'termlink-phase1-' + $sessionId.ToString('N')
$tokenBytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($tokenBytes)
$token = [Convert]::ToBase64String($tokenBytes)
$inputText = 'phase1-agent-input'
$inputBytes = [Text.Encoding]::UTF8.GetBytes($inputText + "`r")
$inputBase64 = [Convert]::ToBase64String($inputBytes)
$bufferDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Phase1-' + $sessionId.ToString('N'))
[IO.Directory]::CreateDirectory($bufferDirectory) | Out-Null

$agent = $null
try {
    $agentStart = [Diagnostics.ProcessStartInfo]::new()
    $agentStart.FileName = $agentExecutable
    $agentStart.Arguments = @(
        '--pipe-name', (Quote-Argument $pipeName),
        '--session-id', $sessionId.ToString('D'),
        '--handshake-token', (Quote-Argument $token),
        '--buffer-directory', (Quote-Argument $bufferDirectory),
        '--input-base64', (Quote-Argument $inputBase64),
        '--input-id', $inputId.ToString('D'),
        '--remote-input-v2',
        '--duplicate-input-attempts', '2',
        '--resize-columns', '111',
        '--resize-rows', '37',
        '--wait-ms', '20000',
        '--test-disconnect-after-frames', '2',
        '--diagnostics'
    ) -join ' '
    $agentStart.UseShellExecute = $false
    $agentStart.CreateNoWindow = $true
    $agentStart.RedirectStandardOutput = $true
    $agentStart.RedirectStandardError = $true
    $agentStart.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $agentStart.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $agent = [Diagnostics.Process]::Start($agentStart)
    $agentOutputTask = $agent.StandardOutput.ReadToEndAsync()
    $agentErrorTask = $agent.StandardError.ReadToEndAsync()

    $innerScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::WriteLine('__PHASE1_READY__')
$value = [Console]::ReadLine()
[Console]::WriteLine("__PHASE1_INPUT__:$value")
[Console]::WriteLine('__PHASE1_ENDED__')
exit 17
'@
    $encodedScript = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($innerScript))
    $childCommand = 'powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ' + $encodedScript
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($childCommand))
    $hostCommand = @(
        (Quote-Argument $hostExecutable),
        '--command-base64', $encodedCommand,
        '--timeout-ms', '10000',
        '--agent-pipe', (Quote-Argument $pipeName),
        '--session-id', $sessionId.ToString('D'),
        '--handshake-token', (Quote-Argument $token),
        '--diagnostics'
    ) -join ' '

    $probe = [TermLink.Validation.ConPtyProbe]::Run(
        $hostCommand,
        '',
        '__PHASE1_READY__',
        "__PHASE1_INPUT__:$inputText",
        17,
        15000)
    Assert-True $probe.Passed (
        "Local Agent double-ConPTY path failed: exit=$($probe.ExitCode), " +
        "timed_out=$($probe.TimedOut), output=$($probe.Output.Replace([string][char]27, '<ESC>'))")
    Assert-True $probe.Output.Contains('__PHASE1_ENDED__') 'Inner lifecycle end marker was not captured.'

    if (-not $agent.WaitForExit(10000)) {
        $agent.Kill()
        $agent.WaitForExit()
        throw 'Session Agent did not stop after the inner Shell exited.'
    }
    $agentOutput = $agentOutputTask.GetAwaiter().GetResult()
    $agentError = $agentErrorTask.GetAwaiter().GetResult()
    Assert-True ($agent.ExitCode -eq 17) "Session Agent returned $($agent.ExitCode): $agentError"
    Assert-True ([string]::IsNullOrWhiteSpace($agentError)) "Session Agent wrote an error: $agentError"

    $agentFields = Get-ResultFields -Text $agentOutput -Prefix '__AGENT_RESULT__:'
    Assert-True ([int]$agentFields.inputs_accepted -eq 1) 'Agent did not accept exactly one unique input.'
    Assert-True ([int]$agentFields.inputs_duplicate -eq 1) 'Agent did not reject the duplicate input ID.'
    Assert-True ([int]$agentFields.remote_input_results_accepted -eq 1) `
        'Session Host did not confirm exactly one successful ConPTY write.'
    Assert-True ([int]$agentFields.remote_input_results_rejected -eq 0) `
        'Session Host unexpectedly rejected the remote ConPTY write.'
    Assert-True ([int]$agentFields.resize_applied -ge 1) 'Agent did not observe the requested 111x37 resize.'
    Assert-True ([int]$agentFields.reconnects -ge 1) 'Host-Agent reconnect and retransmit path was not exercised.'
    Assert-True ([int]$agentFields.output_frames -ge 1) 'Agent did not receive terminal output.'
    Assert-True ([int]$agentFields.output_bytes -gt 0) 'Agent terminal output was empty.'
    Assert-True ([int]$agentFields.exit -eq 17) 'Agent lifecycle exit code was not 17.'

    $hostResult = [regex]::Match(
        $probe.Output,
        '__SESSION_HOST_RESULT__:reason=([^;]+);[\s\S]*?agent_ack=(\d+)')
    Assert-True $hostResult.Success 'Session Host diagnostics and final ACK were not captured.'
    $hostReason = $hostResult.Groups[1].Value
    $hostAck = [uint64]$hostResult.Groups[2].Value
    Assert-True ($hostReason -eq 'exited') 'Session Host did not report a normal exit.'
    Assert-True ($hostAck -eq [uint64]$agentFields.last_seq) `
        ("Session Host final ACK mismatch: host=$hostAck, " +
            "agent=$($agentFields.last_seq); agent_output=$agentOutput; " +
            "probe_output=$($probe.Output.Replace([string][char]27, '<ESC>'))")

    $bufferOutput = & $agentExecutable `
        --session-id $sessionId.ToString('D') `
        --buffer-directory $bufferDirectory `
        --inspect-buffer 2>&1
    Assert-True ($LASTEXITCODE -eq 0) "Encrypted Buffer inspection failed: $bufferOutput"
    $bufferFields = Get-ResultFields -Text ($bufferOutput -join "`n") -Prefix '__BUFFER_RESULT__:'
    Assert-True ([int]$bufferFields.input_frames -eq 0) 'Input frames entered the encrypted event Buffer.'
    Assert-True ([int]$bufferFields.gap_frames -eq 0) 'The normal reconnect path unexpectedly created a history gap.'
    Assert-True ([uint64]$bufferFields.last_seq -eq [uint64]$agentFields.last_seq) `
        'Encrypted Buffer did not persist through the final lifecycle frame.'
    Assert-True ($bufferFields.output_sha256 -match '^[0-9a-f]{64}$') 'Buffer output SHA-256 is invalid.'

    $rawInput = [Text.Encoding]::ASCII.GetBytes($inputText)
    foreach ($file in Get-ChildItem -LiteralPath $bufferDirectory -File) {
        $stored = [IO.File]::ReadAllBytes($file.FullName)
        $match = $false
        for ($offset = 0; $offset -le $stored.Length - $rawInput.Length; $offset++) {
            $same = $true
            for ($index = 0; $index -lt $rawInput.Length; $index++) {
                if ($stored[$offset + $index] -ne $rawInput[$index]) { $same = $false; break }
            }
            if ($same) { $match = $true; break }
        }
        Assert-True (-not $match) "Input plaintext was visible in local protected file $($file.Name)."
    }

    $evidence = [ordered]@{
        schema = 'termlink.phase1-local-agent-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = 'local Host-Agent IPC only; not the production Relay protocol'
        session_id = $sessionId.ToString('D')
        passed = $true
        double_conpty = $probe.Passed
        reconnects = [int]$agentFields.reconnects
        last_sequence = [uint64]$agentFields.last_seq
        persisted_frames = [int]$bufferFields.frames
        output_frames = [int]$bufferFields.output_frames
        output_bytes = [int64]$bufferFields.output_bytes
        output_sha256 = $bufferFields.output_sha256
        input_accepted = [int]$agentFields.inputs_accepted
        duplicate_input_rejected = [int]$agentFields.inputs_duplicate
        conpty_input_result_accepted = [int]$agentFields.remote_input_results_accepted
        conpty_input_result_rejected = [int]$agentFields.remote_input_results_rejected
        input_frames_persisted = [int]$bufferFields.input_frames
        resize_111x37_observed = ([int]$agentFields.resize_applied -ge 1)
        lifecycle_exit_code = [int]$agentFields.exit
        final_ack = $hostAck
        buffer_protection = 'Windows DPAPI CurrentUser per event chunk'
    }
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    Write-Host "Phase 1 local Host-Agent loop passed. Evidence: $resolvedEvidence"
}
finally {
    if ($agent) {
        if (-not $agent.HasExited) {
            $agent.Kill()
            $agent.WaitForExit()
        }
        $agent.Dispose()
    }
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedBuffer = [IO.Path]::GetFullPath($bufferDirectory)
    if ($resolvedBuffer.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedBuffer).StartsWith('TermLink-Phase1-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedBuffer -Recurse -Force -ErrorAction SilentlyContinue
    }
}
