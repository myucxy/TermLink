[CmdletBinding()]
param(
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase1-local-agent-gap.json')
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Quote-Argument {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value.Contains('"')) { throw 'Test arguments must not contain quotes.' }
    '"' + $Value + '"'
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

function Start-CapturedProcess {
    param([Parameter(Mandatory)][string]$FileName, [Parameter(Mandatory)][string]$Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::Start($start)
    [pscustomobject]@{
        Process = $process
        OutputTask = $process.StandardOutput.ReadToEndAsync()
        ErrorTask = $process.StandardError.ReadToEndAsync()
    }
}

$agentExecutable = & (Join-Path $PSScriptRoot '..\src\Agent\Build-Agent.ps1')
$hostExecutable = & (Join-Path $PSScriptRoot '..\src\SessionHost\Build-SessionHost.ps1')
$sessionId = [Guid]::NewGuid()
$pipeName = 'termlink-phase1-gap-' + $sessionId.ToString('N')
$tokenBytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($tokenBytes)
$token = [Convert]::ToBase64String($tokenBytes)
$bufferDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-Phase1-Gap-' + $sessionId.ToString('N'))
[IO.Directory]::CreateDirectory($bufferDirectory) | Out-Null
$hostCapture = $null
$agentCapture = $null

try {
    $innerScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::Write('X' * (256 * 1024))
[Console]::WriteLine('__PHASE1_GAP_OUTPUT_END__')
Start-Sleep -Seconds 5
exit 23
'@
    $encodedScript = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($innerScript))
    $childCommand = 'powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ' + $encodedScript
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($childCommand))
    $hostArguments = @(
        '--command-base64', $encodedCommand,
        '--timeout-ms', '15000',
        '--agent-pipe', (Quote-Argument $pipeName),
        '--session-id', $sessionId.ToString('D'),
        '--handshake-token', (Quote-Argument $token),
        '--test-agent-buffer-bytes', (64 * 1024),
        '--diagnostics'
    ) -join ' '
    $hostCapture = Start-CapturedProcess -FileName $hostExecutable -Arguments $hostArguments

    Start-Sleep -Seconds 2
    Assert-True (-not $hostCapture.Process.HasExited) 'Session Host exited before the delayed Agent could connect.'

    $agentArguments = @(
        '--pipe-name', (Quote-Argument $pipeName),
        '--session-id', $sessionId.ToString('D'),
        '--handshake-token', (Quote-Argument $token),
        '--buffer-directory', (Quote-Argument $bufferDirectory),
        '--wait-ms', '60000',
        '--diagnostics'
    ) -join ' '
    $agentCapture = Start-CapturedProcess -FileName $agentExecutable -Arguments $agentArguments

    Assert-True ($hostCapture.Process.WaitForExit(60000)) 'Session Host did not stop in the history-gap scenario.'
    Assert-True ($agentCapture.Process.WaitForExit(30000)) 'Session Agent did not stop in the history-gap scenario.'
    $hostOutput = $hostCapture.OutputTask.GetAwaiter().GetResult()
    $hostError = $hostCapture.ErrorTask.GetAwaiter().GetResult()
    $agentOutput = $agentCapture.OutputTask.GetAwaiter().GetResult()
    $agentError = $agentCapture.ErrorTask.GetAwaiter().GetResult()

    Assert-True ($hostCapture.Process.ExitCode -eq 23) `
        "Session Host returned $($hostCapture.Process.ExitCode), expected 23: $hostError"
    Assert-True ($agentCapture.Process.ExitCode -eq 23) `
        "Session Agent returned $($agentCapture.Process.ExitCode), expected 23: $agentError"
    Assert-True ($hostOutput.Length -gt (200 * 1024)) `
        "The bounded-backlog output producer was truncated at $($hostOutput.Length) characters."
    Assert-True ([string]::IsNullOrWhiteSpace($agentError)) "Session Agent wrote an error: $agentError"

    $agentFields = Get-ResultFields -Text $agentOutput -Prefix '__AGENT_RESULT__:'
    $hostFields = Get-ResultFields -Text $hostError -Prefix '__SESSION_HOST_RESULT__:'
    Assert-True ([uint64]$hostFields.agent_ack -eq [uint64]$agentFields.last_seq) `
        'Session Host did not receive the final ACK after the history gap.'

    $bufferOutput = & $agentExecutable `
        --session-id $sessionId.ToString('D') `
        --buffer-directory $bufferDirectory `
        --inspect-buffer 2>&1
    Assert-True ($LASTEXITCODE -eq 0) "Encrypted Buffer inspection failed: $bufferOutput"
    $bufferFields = Get-ResultFields -Text ($bufferOutput -join "`n") -Prefix '__BUFFER_RESULT__:'
    Assert-True ([int]$bufferFields.gap_frames -ge 1) 'A bounded Host backlog did not produce a persisted gap declaration.'
    Assert-True ([int]$bufferFields.input_frames -eq 0) 'Input frames entered the gap-scenario Buffer.'
    Assert-True ([uint64]$bufferFields.first_seq -gt 1) 'Gap scenario unexpectedly retained the beginning of the stream.'
    Assert-True ([int64]$bufferFields.output_bytes -lt (256 * 1024)) `
        'Gap scenario did not discard output beyond the bounded Host backlog.'

    $evidence = [ordered]@{
        schema = 'termlink.phase1-local-agent-gap-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = 'local Host-Agent bounded backlog only; not production backpressure or Snapshot'
        session_id = $sessionId.ToString('D')
        passed = $true
        production_buffer_limit_bytes = 4 * 1024 * 1024
        diagnostic_buffer_limit_bytes = 64 * 1024
        produced_output_bytes = 256 * 1024
        first_persisted_sequence = [uint64]$bufferFields.first_seq
        last_persisted_sequence = [uint64]$bufferFields.last_seq
        persisted_frames = [int]$bufferFields.frames
        persisted_gap_frames = [int]$bufferFields.gap_frames
        persisted_output_bytes = [int64]$bufferFields.output_bytes
        input_frames_persisted = [int]$bufferFields.input_frames
        final_ack = [uint64]$hostFields.agent_ack
        lifecycle_exit_code = [int]$agentFields.exit
    }
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    Write-Host "Phase 1 bounded history-gap path passed. Evidence: $resolvedEvidence"
}
finally {
    foreach ($capture in @($agentCapture, $hostCapture)) {
        if ($capture -and -not $capture.Process.HasExited) {
            $capture.Process.Kill()
            $capture.Process.WaitForExit()
        }
        if ($capture) { $capture.Process.Dispose() }
    }
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedBuffer = [IO.Path]::GetFullPath($bufferDirectory)
    if ($resolvedBuffer.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedBuffer).StartsWith('TermLink-Phase1-Gap-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedBuffer -Recurse -Force -ErrorAction SilentlyContinue
    }
}
