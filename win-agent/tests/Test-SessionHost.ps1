[CmdletBinding()]
param(
    [ValidateRange(1, 200)]
    [int]$LeakIterations = 20,
    [ValidateRange(1, 300)]
    [int]$SoakSeconds = 10,
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase0-session-host.json')
)

$ErrorActionPreference = 'Stop'
$sessionHostRoot = Join-Path $PSScriptRoot '..\src\SessionHost'
$hostExecutable = & (Join-Path $sessionHostRoot 'Build-SessionHost.ps1')
$profileFragmentPath = & (Join-Path $sessionHostRoot 'New-WindowsTerminalProfileFragment.ps1') `
    -SessionHostPath $hostExecutable
$profileFragment = Get-Content -Raw -LiteralPath $profileFragmentPath | ConvertFrom-Json
$profileNames = @($profileFragment.profiles | ForEach-Object name)
if ($profileNames.Count -lt 3 -or
    $profileNames -notcontains 'TermLink CMD' -or
    $profileNames -notcontains 'TermLink Windows PowerShell' -or
    $profileNames -notcontains 'TermLink WSL') {
    throw "Windows Terminal profile fragment is incomplete: $($profileNames -join ', ')."
}
foreach ($profile in $profileFragment.profiles) {
    if ($profile.commandline -notmatch '--command-base64 ([A-Za-z0-9+/]+={0,2})$') {
        throw "Profile '$($profile.name)' does not use the encoded Session Host boundary."
    }
    $childCommand = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    if ([string]::IsNullOrWhiteSpace($childCommand)) {
        throw "Profile '$($profile.name)' has an empty child command."
    }
}
$probeSource = Join-Path $PSScriptRoot '..\..\prototypes\conpty-probe\ConPtyNative.cs'
if (-not ('TermLink.Validation.ConPtyProbe' -as [type])) {
    Add-Type -Path $probeSource
}

function New-EncodedChildCommand {
    param(
        [Parameter(Mandatory)][string]$Script,
        [string]$ShellExecutable = 'powershell.exe'
    )

    $encodedScript = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    "`"$ShellExecutable`" -NoLogo -NoProfile -NonInteractive -EncodedCommand $encodedScript"
}

function New-HostCommand {
    param(
        [Parameter(Mandatory)][string]$ChildCommand,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds
    )

    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ChildCommand))
    "`"$hostExecutable`" --command-base64 $encodedCommand --timeout-ms $TimeoutMilliseconds --diagnostics"
}

function Invoke-HostedPipeProcess {
    param(
        [Parameter(Mandatory)][string]$Script,
        [string]$InputText = '',
        [int]$InputDelayMilliseconds = 0,
        [int]$HostTimeoutMilliseconds = 5000,
        [int]$WaitMilliseconds = 10000,
        [string]$ShellExecutable = 'powershell.exe'
    )

    $childCommand = New-EncodedChildCommand -Script $Script -ShellExecutable $ShellExecutable
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($childCommand))
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $hostExecutable
    $startInfo.Arguments = "--command-base64 $encodedCommand --timeout-ms $HostTimeoutMilliseconds --diagnostics"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::Start($startInfo)
    $outputTask = $null
    $errorTask = $null
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if ($InputDelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $InputDelayMilliseconds }
        if ($InputText.Length -gt 0) {
            $process.StandardInput.Write($InputText)
            $process.StandardInput.Flush()
        }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit($WaitMilliseconds)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Session Host did not stop within $WaitMilliseconds ms."
        }
        [pscustomobject]@{
            Output = $outputTask.GetAwaiter().GetResult() + $errorTask.GetAwaiter().GetResult()
            ExitCode = $process.ExitCode
        }
    }
    finally {
        if ($outputTask -and $outputTask.IsCompleted) { $outputTask.Dispose() }
        if ($errorTask -and $errorTask.IsCompleted) { $errorTask.Dispose() }
        $process.StandardInput.Dispose()
        $process.StandardOutput.Dispose()
        $process.StandardError.Dispose()
        $process.Dispose()
    }
}

function Get-CurrentProcessCounts {
    $process = [Diagnostics.Process]::GetCurrentProcess()
    try {
        [pscustomobject]@{
            Handles = $process.HandleCount
            Threads = $process.Threads.Count
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-HostedProbe {
    param(
        [Parameter(Mandatory)][string]$Script,
        [string]$InputText = '',
        [Parameter(Mandatory)][string]$ExpectedOutput,
        [Parameter(Mandatory)][string]$ExpectedInput,
        [Parameter(Mandatory)][int]$ExpectedExitCode,
        [int]$HostTimeoutMilliseconds = 5000,
        [int]$ProbeTimeoutMilliseconds = 10000
    )

    $childCommand = New-EncodedChildCommand -Script $Script
    $hostCommand = New-HostCommand `
        -ChildCommand $childCommand `
        -TimeoutMilliseconds $HostTimeoutMilliseconds
    if ($InputText.Length -gt 0) {
        $inputStep = [TermLink.Validation.ConPtyInputStep]@{
            DelayBeforeMilliseconds = 700
            Text = $InputText
        }
        return [TermLink.Validation.ConPtyProbe]::RunScripted(
            $hostCommand,
            [TermLink.Validation.ConPtyInputStep[]]@($inputStep),
            $ExpectedOutput,
            $ExpectedInput,
            $ExpectedExitCode,
            $ProbeTimeoutMilliseconds)
    }

    [TermLink.Validation.ConPtyProbe]::Run(
        $hostCommand,
        '',
        $ExpectedOutput,
        $ExpectedInput,
        $ExpectedExitCode,
        $ProbeTimeoutMilliseconds)
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-HostDiagnostics {
    param([Parameter(Mandatory)][object]$Result, [Parameter(Mandatory)][string]$Reason)

    $match = [regex]::Match(
        $Result.Output,
        '__SESSION_HOST_RESULT__:reason=([^;]+);handles_start=(\d+);handles_end=(\d+)')
    Assert-True $match.Success 'Session Host lifecycle diagnostics were not captured.'
    Assert-True ($match.Groups[1].Value -eq $Reason) `
        "Expected Session Host reason '$Reason', got '$($match.Groups[1].Value)'."
    $start = [int]$match.Groups[2].Value
    $end = [int]$match.Groups[3].Value
    Assert-True ($end -le $start) "Session Host owned handles did not return to baseline: $start -> $end."
    [ordered]@{ reason = $Reason; handles_start = $start; handles_end = $end }
}

function Get-InnerPid {
    param([Parameter(Mandatory)][object]$Result)

    $match = [regex]::Match($Result.Output, '__INNER_PID__:(\d+)')
    if (-not $match.Success) { throw 'Inner Shell PID marker was not captured.' }
    [int]$match.Groups[1].Value
}

function Assert-ProcessStops {
    param([Parameter(Mandatory)][int]$ProcessId)

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Inner process $ProcessId survived Session Host termination."
}

$normalScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::WriteLine('__INNER_STARTED__')
Start-Sleep -Milliseconds 400
[Console]::WriteLine("__INNER_SIZE__:$([Console]::WindowWidth)x$([Console]::WindowHeight)")
$value = [Console]::ReadLine()
[Console]::WriteLine("__INNER_INPUT__:$value")
exit 17
'@
$normal = Invoke-HostedProbe `
    -Script $normalScript `
    -InputText "phase0-input`r" `
    -ExpectedOutput '__INNER_STARTED__' `
    -ExpectedInput '__INNER_INPUT__:phase0-input' `
    -ExpectedExitCode 17
Assert-True $normal.Passed `
    ("Normal double-ConPTY Session Host path failed: exit=$($normal.ExitCode), " +
        "timed_out=$($normal.TimedOut), output=$($normal.Output.Replace([string][char]27, '<ESC>'))")
Assert-True $normal.Output.Contains('__INNER_SIZE__:100x30') 'Outer resize was not propagated to the inner ConPTY.'
$normalEvidence = Assert-HostDiagnostics -Result $normal -Reason 'exited'

$unicodeScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::TreatControlCAsInput = $true
[Console]::WriteLine('__UNICODE_READY__')
$builder = [Text.StringBuilder]::new()
while ($true) {
    $key = [Console]::ReadKey($true)
    if ($key.Key -eq [ConsoleKey]::Enter) { break }
    $null = $builder.Append($key.KeyChar)
}
[Console]::WriteLine("__UNICODE_INPUT__:$builder")
exit 17
'@
$unicodeInput = 'phase0-input 中文终端 🙂'
$pwshExecutable = (Get-Command pwsh.exe -ErrorAction Stop).Source
$unicode = Invoke-HostedPipeProcess `
    -Script $unicodeScript `
    -InputText ($unicodeInput + "`r") `
    -InputDelayMilliseconds 500 `
    -ShellExecutable $pwshExecutable
Assert-True ($unicode.ExitCode -eq 17) `
    "UTF-8 Agent-pipe input returned $($unicode.ExitCode), expected 17."
Assert-True $unicode.Output.Contains("__UNICODE_INPUT__:$unicodeInput") `
    "UTF-8 Agent-pipe input was corrupted: $($unicode.Output.Replace([string][char]27, '<ESC>'))"
$unicodeEvidence = Assert-HostDiagnostics -Result $unicode -Reason 'exited'

$keyInputScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::TreatControlCAsInput = $true
[Console]::WriteLine('__KEY_INPUT_READY__')
for ($index = 1; $index -le 3; $index++) {
    $key = [Console]::ReadKey($true)
    [Console]::WriteLine("__KEY_$($index)__:$($key.Key):$([int]$key.KeyChar):$($key.Modifiers)")
}
exit 41
'@
$keyInput = Invoke-HostedProbe `
    -Script $keyInputScript `
    -InputText (([string][char]3) + ([char]27) + 'b' + ([char]27) + '[A') `
    -ExpectedOutput '__KEY_INPUT_READY__' `
    -ExpectedInput '__KEY_3__:UpArrow:0:0' `
    -ExpectedExitCode 41
Assert-True $keyInput.Passed `
    "Control-key bridge failed: $($keyInput.Output.Replace([string][char]27, '<ESC>'))"
Assert-True $keyInput.Output.Contains('__KEY_1__:C:3:Control') 'Ctrl+C was not bridged as terminal input.'
Assert-True $keyInput.Output.Contains('__KEY_2__:B:98:Alt') 'Alt+B was not bridged as terminal input.'
$keyInputEvidence = Assert-HostDiagnostics -Result $keyInput -Reason 'exited'

$eofScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::WriteLine('__EOF_READY__')
$null = [Console]::In.ReadToEnd()
[Console]::WriteLine('__INNER_EOF__')
exit 23
'@
$eof = Invoke-HostedPipeProcess `
    -Script $eofScript `
    -InputText (([string][char]26) + "`r") `
    -InputDelayMilliseconds 500
$eofOutput = $eof.Output
Assert-True ($eof.ExitCode -eq 23) `
    "Input-pipe disconnect returned $($eof.ExitCode), expected 23. Output: $eofOutput"
Assert-True $eofOutput.Contains('__EOF_READY__') 'Inner EOF readiness marker was not captured.'
Assert-True $eofOutput.Contains('__INNER_EOF__') 'Input-pipe disconnect did not reach the inner Shell as EOF.'
$eofEvidence = Assert-HostDiagnostics -Result $eof -Reason 'exited'

$timeoutScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::WriteLine('__TIMEOUT_READY__')
[Console]::WriteLine("__INNER_PID__:$PID")
Start-Sleep -Seconds 60
'@
$timeout = Invoke-HostedProbe `
    -Script $timeoutScript `
    -ExpectedOutput '__TIMEOUT_READY__' `
    -ExpectedInput '__TIMEOUT_READY__' `
    -ExpectedExitCode 124 `
    -HostTimeoutMilliseconds 600 `
    -ProbeTimeoutMilliseconds 5000
Assert-True $timeout.Passed 'Session Host timeout path failed.'
$timeoutPid = Get-InnerPid -Result $timeout
Assert-ProcessStops -ProcessId $timeoutPid
$timeoutEvidence = Assert-HostDiagnostics -Result $timeout -Reason 'timeout'

$forcedCommand = New-HostCommand `
    -ChildCommand (New-EncodedChildCommand -Script $timeoutScript) `
    -TimeoutMilliseconds 60000
$forced = [TermLink.Validation.ConPtyProbe]::Run(
    $forcedCommand,
    '',
    '__TIMEOUT_READY__',
    '__TIMEOUT_READY__',
    124,
    900)
Assert-True $forced.TimedOut 'Outer force-termination scenario did not reach the Probe timeout.'
$forcedPid = Get-InnerPid -Result $forced
Assert-ProcessStops -ProcessId $forcedPid

$rapidResizeScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::WriteLine('__RESIZE_READY__')
$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $size = "$([Console]::WindowWidth)x$([Console]::WindowHeight)"
    if ($size -eq '132x43') {
        [Console]::WriteLine('__RAPID_RESIZE_OK__:132x43')
        exit 29
    }
    Start-Sleep -Milliseconds 25
} while ([DateTime]::UtcNow -lt $deadline)
[Console]::WriteLine("__RAPID_RESIZE_FAILED__:$size")
exit 30
'@
$rapidResizeCommand = New-HostCommand `
    -ChildCommand (New-EncodedChildCommand -Script $rapidResizeScript) `
    -TimeoutMilliseconds 7000
$rapidResizeSteps = [TermLink.Validation.ConPtyInputStep[]]@(
    [TermLink.Validation.ConPtyInputStep]@{
        DelayBeforeMilliseconds = 150
        Columns = 120
        Rows = 40
        ResizeReason = 'rapid-1'
    }
    [TermLink.Validation.ConPtyInputStep]@{
        DelayBeforeMilliseconds = 50
        Columns = 60
        Rows = 20
        ResizeReason = 'rapid-2'
    }
    [TermLink.Validation.ConPtyInputStep]@{
        DelayBeforeMilliseconds = 50
        Columns = 132
        Rows = 43
        ResizeReason = 'rapid-final'
    }
)
$rapidResize = [TermLink.Validation.ConPtyProbe]::RunScripted(
    $rapidResizeCommand,
    $rapidResizeSteps,
    '__RESIZE_READY__',
    '__RAPID_RESIZE_OK__:132x43',
    29,
    10000)
Assert-True $rapidResize.Passed `
    "Rapid resize propagation failed: $($rapidResize.Output.Replace([string][char]27, '<ESC>'))"
$rapidResizeEvents = @($rapidResize.Events | Where-Object Type -EQ 'resize')
Assert-True ($rapidResizeEvents.Count -ge 5) 'Scripted resize events were not all captured.'

$throughputBytes = 2 * 1024 * 1024
$throughputScript = @"
[Console]::OutputEncoding = [Text.UTF8Encoding]::new(`$false)
[Console]::Write(('Z' * $throughputBytes))
[Console]::WriteLine('__THROUGHPUT_OK__')
exit 31
"@
$throughputTimer = [Diagnostics.Stopwatch]::StartNew()
$throughput = Invoke-HostedPipeProcess `
    -Script $throughputScript `
    -HostTimeoutMilliseconds 15000 `
    -WaitMilliseconds 20000
$throughputTimer.Stop()
$throughputCount = [regex]::Matches($throughput.Output, 'Z').Count
Assert-True ($throughput.ExitCode -eq 31) `
    "High-throughput child returned $($throughput.ExitCode), expected 31."
Assert-True $throughput.Output.Contains('__THROUGHPUT_OK__') 'High-throughput completion marker was lost.'
Assert-True ($throughputCount -eq $throughputBytes) `
    "High-throughput output was truncated: $throughputCount of $throughputBytes payload bytes."

$soakScript = @"
[Console]::OutputEncoding = [Text.UTF8Encoding]::new(`$false)
[Console]::WriteLine('__SOAK_STARTED__')
for (`$index = 0; `$index -lt $SoakSeconds; `$index++) {
    Start-Sleep -Seconds 1
    [Console]::WriteLine("__SOAK_TICK__:`$index")
}
[Console]::WriteLine('__SOAK_OK__')
exit 37
"@
$soakTimer = [Diagnostics.Stopwatch]::StartNew()
$soak = Invoke-HostedPipeProcess `
    -Script $soakScript `
    -HostTimeoutMilliseconds (($SoakSeconds + 5) * 1000) `
    -WaitMilliseconds (($SoakSeconds + 10) * 1000)
$soakTimer.Stop()
Assert-True ($soak.ExitCode -eq 37 -and $soak.Output.Contains('__SOAK_OK__')) `
    "Session Host soak run failed: exit=$($soak.ExitCode)."
Assert-True ($soakTimer.ElapsedMilliseconds -ge ($SoakSeconds * 1000 - 250)) `
    'Session Host soak child exited before the requested duration.'

$loopScript = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::WriteLine('__LOOP_OK__')
exit 0
'@
$parentProbeSource = Join-Path $PSScriptRoot 'SessionHostParentProbe.cs'
$parentProbeExecutable = Join-Path $sessionHostRoot 'build\TermLink.ParentProbe.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Core.dll `
    "/out:$parentProbeExecutable" $parentProbeSource
if ($LASTEXITCODE -ne 0) { throw "Parent probe compilation failed with exit code $LASTEXITCODE." }
$loopChildCommand = New-EncodedChildCommand -Script $loopScript
$loopEncodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($loopChildCommand))
$parentProbeOutput = & $parentProbeExecutable $hostExecutable $loopEncodedCommand $LeakIterations 2>&1
$parentProbeExit = $LASTEXITCODE
$parentProbeMatch = [regex]::Match(
    ($parentProbeOutput -join "`n"),
    '__PARENT_PROBE__:before=(\d+);after=(\d+);delta=(-?\d+);samples=([0-9,]+)')
Assert-True $parentProbeMatch.Success "Parent probe result was not captured: $parentProbeOutput"
$parentBeforeHandles = [int]$parentProbeMatch.Groups[1].Value
$parentAfterHandles = [int]$parentProbeMatch.Groups[2].Value
$parentHandleDelta = [int]$parentProbeMatch.Groups[3].Value
Assert-True ($parentProbeExit -eq 0 -and $parentHandleDelta -le 8) `
    ("Session Host parent probe grew across $LeakIterations runs: " +
        "$parentBeforeHandles -> $parentAfterHandles; " +
        "samples: $($parentProbeMatch.Groups[4].Value).")

$evidence = [ordered]@{
    format = 'termlink.phase0-session-host-validation'
    format_version = 1
    generated_at = [DateTimeOffset]::UtcNow.ToString('O')
    executable_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $hostExecutable).Hash.ToLowerInvariant()
    environment = [ordered]@{
        os_version = [Environment]::OSVersion.Version.ToString()
        process_architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    windows_terminal_profile = [ordered]@{
        fragment_file = [IO.Path]::GetFileName($profileFragmentPath)
        profile_count = $profileNames.Count
        profiles = $profileNames
        encoded_commands_valid = $true
        outer_conpty_bridge = $normal.Passed
    }
    normal_exit = $normalEvidence
    unicode_input = [ordered]@{
        path = 'agent-stdin-pipe-to-pwsh7-inner-conpty'
        cjk = $true
        emoji = $true
        round_trip = $true
        lifecycle = $unicodeEvidence
    }
    control_input = [ordered]@{
        ctrl_c = $true
        alt_b = $true
        up_arrow = $true
        lifecycle = $keyInputEvidence
    }
    input_disconnect = $eofEvidence
    timeout = $timeoutEvidence
    force_termination = [ordered]@{
        probe_timed_out = $forced.TimedOut
        inner_pid_stopped = $forcedPid
    }
    resize = [ordered]@{ columns = 100; rows = 30; propagated = $true }
    rapid_resize = [ordered]@{
        sequence = @('120x40', '60x20', '132x43')
        captured_resize_events = $rapidResizeEvents.Count
        final_propagated = $true
    }
    throughput = [ordered]@{
        payload_bytes = $throughputBytes
        captured_payload_bytes = $throughputCount
        elapsed_ms = $throughputTimer.ElapsedMilliseconds
    }
    soak = [ordered]@{
        requested_seconds = $SoakSeconds
        elapsed_ms = $soakTimer.ElapsedMilliseconds
        completed = $true
    }
    leak_loop = [ordered]@{
        iterations = $LeakIterations
        parent_handles_before = $parentBeforeHandles
        parent_handles_after = $parentAfterHandles
        delta = $parentHandleDelta
        probe = 'isolated-csharp-process-launcher'
    }
}
$evidenceFile = [System.IO.Path]::GetFullPath($EvidencePath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($evidenceFile)) | Out-Null
[System.IO.File]::WriteAllText(
    $evidenceFile,
    ($evidence | ConvertTo-Json -Depth 8) + "`n",
    [Text.UTF8Encoding]::new($false))

$evidence | ConvertTo-Json -Depth 8
