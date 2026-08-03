[CmdletBinding()]
param(
    [ValidateSet('cmd', 'powershell', 'pwsh', 'wsl')]
    [string]$Shell = 'powershell',

    [ValidateSet(
        'smoke',
        'ansi-unicode',
        'terminal-features',
        'vim',
        'less',
        'fzf',
        'htop'
    )]
    [string]$Scenario = 'smoke',

    [string]$CaptureRoot,

    [ValidatePattern('^[a-z0-9][a-z0-9._-]*$')]
    [string]$CaseName,

    [switch]$OverwriteCapture
)

$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot 'ConPtyNative.cs'

if (-not ('TermLink.Validation.ConPtyProbe' -as [type])) {
    Add-Type -Path $sourcePath
}

$inputValue = 'android-input-simulation'
$expectedOutput = '__OUTPUT_CAPTURE_OK__'
$expectedInput = "__INPUT_CAPTURED__:$inputValue"
$expectedExitCode = 17
$additionalExpectedOutput = @()
$temporaryScenarioScript = $null
$inputSteps = $null

if ($Scenario -in @('ansi-unicode', 'terminal-features') -and $Shell -ne 'pwsh') {
    throw "Scenario '$Scenario' requires -Shell pwsh."
}
if ($Scenario -in @('vim', 'less', 'fzf', 'htop') -and $Shell -ne 'wsl') {
    throw "Scenario '$Scenario' requires -Shell wsl."
}

switch ($Shell) {
    'cmd' {
        $commandLine = "$env:ComSpec /D /Q"
        $inputText = @(
            'echo __OUTPUT_CAPTURE_OK__'
            'set /p "termlink_value=__INPUT_PROMPT__"'
            $inputValue
            'echo __INPUT_CAPTURED__:%termlink_value%'
            'exit /b 17'
        ) -join "`r"
    }
    'powershell' {
        $commandLine = 'powershell.exe -NoLogo -NoProfile'
        $inputText = @(
            "Write-Output '__OUTPUT_CAPTURE_OK__'"
            "`$termlinkValue = 'android-input' + '-simulation'"
            "Write-Output ('__INPUT_CAPTURED__:' + `$termlinkValue)"
            'exit 17'
        ) -join "`r"
    }
    'pwsh' {
        $pwshPath = (Get-Command pwsh.exe -ErrorAction Stop).Source
        if ($Scenario -eq 'terminal-features') {
            $temporaryScenarioScript = Join-Path `
                ([System.IO.Path]::GetTempPath()) `
                "termlink-phase0-terminal-features-$PID.ps1"
            $scenarioScript = @'
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$esc = [char]27
$bell = [char]7
$st = "$esc\"

[Console]::Write("__OUTPUT_CAPTURE_OK__`r`n")
[Console]::Write("${esc}[1;2;3;4;5;7;8;9mSGR${esc}[0m__SGR_ATTRIBUTES__`r`n")
Start-Sleep -Milliseconds 120
[Console]::Write("${esc}[31m__ANSI_16_RED__${esc}[0m ")
[Console]::Write("${esc}[38;5;201m__ANSI_256__${esc}[0m ")
[Console]::Write("${esc}[38;2;12;200;144m__TRUECOLOR__${esc}[0m`r`n")

# Palette/default foreground/default background/cursor color controls.
[Console]::Write("${esc}]4;1;rgb:ff/33/44${st}")
[Console]::Write("${esc}]10;rgb:e8/ed/f2${st}")
[Console]::Write("${esc}]11;rgb:0b/0e/12${st}")
[Console]::Write("${esc}]12;rgb:f3/c9/69${st}")
[Console]::Write("__OSC_COLORS__`r`n")

[Console]::Write("${esc}]0;TermLink phase 0${bell}")
[Console]::Write("${esc}]8;;https://example.invalid/phase0${st}__OSC8_LINK__${esc}]8;;${st}`r`n")
[Console]::Write("${esc}]52;c;VEVSTUxJTktfVEVTVF9PTkxZ${bell}")
[Console]::Write("__OSC52_RECOVERED__`r`n")

# Exercise scroll regions and cursor save/restore without relying on line parsing.
[Console]::Write("${esc}[4;12r${esc}7${esc}[8;20H__SCROLL_CURSOR__${esc}8${esc}[r`r`n")

# Keep mode transitions in separate ConPTY output chunks for draft checkpoint capture.
[Console]::Write("${esc}[?1h${esc}=${esc}[?1000h${esc}[?1002h${esc}[?1003h${esc}[?1006h${esc}[?1004h${esc}[?2004h")
[Console]::Write("__MODES_ENABLED__")
Start-Sleep -Milliseconds 120
[Console]::Write("${esc}[?1l${esc}>${esc}[?1000l${esc}[?1002l${esc}[?1003l${esc}[?1006l${esc}[?1004l${esc}[?2004l`r`n")

[Console]::Write("${esc}[?1049h${esc}[2J${esc}[H__ALT_ACTIVE__`r`n中文终端 🙂 e`u{0301}")
Start-Sleep -Milliseconds 120
[Console]::Write("${esc}[?1049l__ALT_RECOVERED__`r`n")

# Overlong OSC and CSI payloads must be bounded and the parser must recover.
$longOsc = 'A' * 20000
[Console]::Write("${esc}]0;${longOsc}${bell}__LONG_OSC_RECOVERED__`r`n")
$longCsi = '9;' * 10000
[Console]::Write("${esc}[${longCsi}0m__LONG_CSI_RECOVERED__`r`n")
[Console]::Write("__INPUT_CAPTURED__:android-input-simulation`r`n")
exit 17
'@
            [System.IO.File]::WriteAllText(
                $temporaryScenarioScript,
                $scenarioScript,
                [System.Text.UTF8Encoding]::new($false))
            $commandLine = "`"$pwshPath`" -NoLogo -NoProfile -NonInteractive -File `"$temporaryScenarioScript`""
            $inputText = ''
            $additionalExpectedOutput = @(
                '__SGR_ATTRIBUTES__'
                '__ANSI_16_RED__'
                '__ANSI_256__'
                '__TRUECOLOR__'
                '__OSC_COLORS__'
                '__OSC8_LINK__'
                '__OSC52_RECOVERED__'
                '__SCROLL_CURSOR__'
                '__MODES_ENABLED__'
                '__ALT_ACTIVE__'
                '__ALT_RECOVERED__'
                '__LONG_OSC_RECOVERED__'
                '__LONG_CSI_RECOVERED__'
            )
        }
        elseif ($Scenario -eq 'ansi-unicode') {
            $commandLine = "`"$pwshPath`" -NoLogo -NoProfile -NonInteractive"
            $inputText = @(
                '[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)'
                '$esc = [char]27'
                '[Console]::Write("__OUTPUT_CAPTURE_OK__`r`n")'
                '[Console]::Write("${esc}[31m__ANSI_16_RED__${esc}[0m`r`n")'
                '[Console]::Write("${esc}[38;5;201m__ANSI_256__${esc}[0m`r`n")'
                '[Console]::Write("${esc}[38;2;12;200;144m__TRUECOLOR__${esc}[0m`r`n")'
                '[Console]::Write("__CJK__:中文终端`r`n")'
                '[Console]::Write("__EMOJI__:🙂👩‍💻`r`n")'
                '[Console]::Write("__COMBINING__:e`u{0301}`r`n")'
                '[Console]::Write("${esc}[?1049h__ALT_SCREEN__${esc}[?1049l")'
                '[Console]::Write("${esc}[?2004h${esc}[?2004l")'
                '[Console]::Write("__INPUT_CAPTURED__:android-input-simulation`r`n")'
                'exit 17'
            ) -join "`r"
            $additionalExpectedOutput = @(
                '__ANSI_16_RED__'
                '__ANSI_256__'
                '__TRUECOLOR__'
                '__CJK__:中文终端'
                '__EMOJI__:🙂👩‍💻'
                '__COMBINING__:'
                '__ALT_SCREEN__'
            )
        }
        else {
            $commandLine = "`"$pwshPath`" -NoLogo -NoProfile"
            $inputText = @(
                "Write-Output '__OUTPUT_CAPTURE_OK__'"
                "`$termlinkValue = 'android-input' + '-simulation'"
                "Write-Output ('__INPUT_CAPTURED__:' + `$termlinkValue)"
                'exit 17'
            ) -join "`r"
        }
    }
    'wsl' {
        $commandLine = 'wsl.exe -d Ubuntu-24.04 -u root --exec env TERM=xterm-256color LANG=C.UTF-8 bash --noprofile --norc'
        switch ($Scenario) {
            'vim' {
                $inputSteps = @(
                    [TermLink.Validation.ConPtyInputStep]@{
                        Text = "printf 'alpha\nbeta\n中文终端 🙂\n' > /tmp/termlink-vim.txt`r" +
                            "vim -Nu NONE -n -i NONE /tmp/termlink-vim.txt`r"
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 700
                        Text = "Go__VIM_EDIT_OK__`e:wq`r"
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 250
                        Text = "printf '__OUTPUT_CAPTURE_OK__\n__INPUT_CAPTURED__:android-input-simulation\n__VIM_EXITED__\n'`r" +
                            "exit 17`r"
                    }
                )
                $inputText = ''
                $additionalExpectedOutput = @('__VIM_EDIT_OK__', '__VIM_EXITED__')
            }
            'less' {
                $inputSteps = @(
                    [TermLink.Validation.ConPtyInputStep]@{
                        Text = "seq 1 120 | sed '60s/.*/__LESS_ACTIVE__ 中文终端/' > /tmp/termlink-less.txt`r" +
                            "less -R /tmp/termlink-less.txt`r"
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 600
                        Text = 'G'
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 250
                        Text = 'q'
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 250
                        Text = "printf '__OUTPUT_CAPTURE_OK__\n__INPUT_CAPTURED__:android-input-simulation\n__LESS_EXITED__\n'`r" +
                            "exit 17`r"
                    }
                )
                $inputText = ''
                $additionalExpectedOutput = @('__LESS_ACTIVE__', '__LESS_EXITED__')
            }
            'fzf' {
                $inputSteps = @(
                    [TermLink.Validation.ConPtyInputStep]@{
                        Text = "fzf_bin=`$(command -v fzf || printf /tmp/termlink-phase0-tools/fzf/usr/bin/fzf)`r" +
                            "printf 'alpha\nbeta-target\ngamma\n中文终端\n' | `"`$fzf_bin`" --no-mouse --border --prompt='TermLink> ' `r"
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 700
                        Text = 'be'
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 250
                        Text = "`r"
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 250
                        Text = "printf '__OUTPUT_CAPTURE_OK__\n__INPUT_CAPTURED__:android-input-simulation\n__FZF_EXITED__\n'`r" +
                            "exit 17`r"
                    }
                )
                $inputText = ''
                $additionalExpectedOutput = @('TermLink>', 'beta-target', '__FZF_EXITED__')
            }
            'htop' {
                $inputSteps = @(
                    [TermLink.Validation.ConPtyInputStep]@{
                        Text = "htop_bin=`$(command -v htop || printf /tmp/termlink-phase0-tools/htop/usr/bin/htop)`r" +
                            "LD_LIBRARY_PATH=/tmp/termlink-phase0-tools/lib/usr/lib/x86_64-linux-gnu `"`$htop_bin`" -C -d 1 -p `$$`r"
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 1000
                        Text = 'q'
                    }
                    [TermLink.Validation.ConPtyInputStep]@{
                        DelayBeforeMilliseconds = 250
                        Text = "printf '__OUTPUT_CAPTURE_OK__\n__INPUT_CAPTURED__:android-input-simulation\n__HTOP_EXITED__\n'`r" +
                            "exit 17`r"
                    }
                )
                $inputText = ''
                $additionalExpectedOutput = @('CPU', 'Mem', '__HTOP_EXITED__')
            }
            default {
                $inputText = @(
                    "printf '__OUTPUT_CAPTURE_OK__\n'"
                    "termlink_value='android-input''-simulation'"
                    'printf ''__INPUT_CAPTURED__:%s\n'' "$termlink_value"'
                    'exit 17'
                ) -join "`r"
            }
        }
    }
}

$inputText += "`r"
$runWorkingDirectory = $env:SystemRoot
$previousProcessWorkingDirectory = [Environment]::CurrentDirectory
Push-Location -LiteralPath $runWorkingDirectory
try {
    [Environment]::CurrentDirectory = $runWorkingDirectory
    if ($inputSteps) {
        $result = [TermLink.Validation.ConPtyProbe]::RunScripted(
            $commandLine,
            [TermLink.Validation.ConPtyInputStep[]]$inputSteps,
            $expectedOutput,
            $expectedInput,
            $expectedExitCode,
            15000)
    }
    else {
        $result = [TermLink.Validation.ConPtyProbe]::Run(
            $commandLine,
            $inputText,
            $expectedOutput,
            $expectedInput,
            $expectedExitCode,
            15000)
    }
}
finally {
    [Environment]::CurrentDirectory = $previousProcessWorkingDirectory
    Pop-Location
    if ($temporaryScenarioScript) {
        Remove-Item -LiteralPath $temporaryScenarioScript -Force -ErrorAction SilentlyContinue
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [string[]]$Value
    )

    [System.IO.File]::WriteAllLines(
        $LiteralPath,
        $Value,
        [System.Text.UTF8Encoding]::new($false))
}

function Write-GoldenStreamBundle {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [object]$ProbeResult,

        [switch]$AllowOverwrite
    )

    $bundlePath = Join-Path ([System.IO.Path]::GetFullPath($Root)) $Name
    if (Test-Path -LiteralPath $bundlePath) {
        if (-not $AllowOverwrite) {
            throw "Golden Stream bundle already exists: $bundlePath"
        }

        $existingManifestPath = Join-Path $bundlePath 'manifest.json'
        if (-not (Test-Path -LiteralPath $existingManifestPath -PathType Leaf)) {
            throw "Refusing to overwrite an unrecognized directory: $bundlePath"
        }
        $existingManifest = Get-Content -Raw -LiteralPath $existingManifestPath | ConvertFrom-Json
        if ($existingManifest.format -ne 'termlink.golden-stream' -or
            $existingManifest.format_version -ne 1 -or
            $existingManifest.case_name -ne $Name) {
            throw "Refusing to overwrite an incompatible Golden Stream: $bundlePath"
        }
    }

    [System.IO.Directory]::CreateDirectory($bundlePath) | Out-Null
    $outputPath = Join-Path $bundlePath 'output.bin'
    $eventsPath = Join-Path $bundlePath 'events.jsonl'
    $expectationsPath = Join-Path $bundlePath 'expectations.json'
    $manifestPath = Join-Path $bundlePath 'manifest.json'

    [System.IO.File]::WriteAllBytes($outputPath, $ProbeResult.OutputBytes)

    $eventLines = foreach ($event in $ProbeResult.Events) {
        $record = [ordered]@{
            seq        = $event.Sequence
            elapsed_us = $event.ElapsedMicroseconds
            type       = $event.Type
        }

        switch ($event.Type) {
            'output' {
                $record.data_offset = $event.OutputOffset
                $record.data_length = $event.OutputLength
            }
            'resize' {
                $record.columns = $event.Columns
                $record.rows = $event.Rows
                $record.reason = $event.Reason
            }
            'lifecycle' {
                $record.state = $event.State
                if ($null -ne $event.ExitCode) {
                    $record.exit_code = $event.ExitCode
                }
            }
            default {
                throw "Unsupported capture event type: $($event.Type)"
            }
        }

        $record | ConvertTo-Json -Compress
    }
    Write-Utf8NoBom -LiteralPath $eventsPath -Value $eventLines

    $expectations = [ordered]@{
        exit_code            = $expectedExitCode
        final_size           = [ordered]@{ columns = 100; rows = 30 }
        output_contains_utf8 = @($expectedOutput, $expectedInput) + $additionalExpectedOutput
        normalized_grid      = $null
        reference_screenshot = $null
    }
    Write-Utf8NoBom `
        -LiteralPath $expectationsPath `
        -Value ($expectations | ConvertTo-Json -Depth 5)

    $outputEventCount = @($ProbeResult.Events | Where-Object Type -EQ 'output').Count
    $manifest = [ordered]@{
        format         = 'termlink.golden-stream'
        format_version = 1
        capture_id     = [Guid]::NewGuid().ToString('D')
        case_name      = $Name
        created_at     = [DateTimeOffset]::UtcNow.ToString('O')
        clock          = 'monotonic-microseconds'
        source         = [ordered]@{
            platform          = 'windows-conpty'
            shell             = $Shell
            command_line      = $ProbeResult.CommandLine
            working_directory = $runWorkingDirectory
        }
        initial_size   = [ordered]@{ columns = 80; rows = 25 }
        files          = [ordered]@{
            events       = 'events.jsonl'
            output       = 'output.bin'
            expectations = 'expectations.json'
        }
        statistics     = [ordered]@{
            event_count        = @($ProbeResult.Events).Count
            output_event_count = $outputEventCount
            output_bytes       = $ProbeResult.OutputBytes.Length
        }
        sha256         = [ordered]@{
            events       = (Get-FileHash -Algorithm SHA256 -LiteralPath $eventsPath).Hash.ToLowerInvariant()
            output       = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath).Hash.ToLowerInvariant()
            expectations = (Get-FileHash -Algorithm SHA256 -LiteralPath $expectationsPath).Hash.ToLowerInvariant()
        }
    }
    Write-Utf8NoBom -LiteralPath $manifestPath -Value ($manifest | ConvertTo-Json -Depth 8)

    return $bundlePath
}

[PSCustomObject]@{
    Shell                  = $Shell
    Scenario               = $Scenario
    Passed                 = $result.Passed
    OutputCaptured         = $result.OutputCaptured
    InputRoundTripCaptured = $result.InputRoundTripCaptured
    ResizeSucceeded        = $result.ResizeSucceeded
    ExitCodeCaptured       = $result.ExitCodeCaptured
    ExitCode               = $result.ExitCode
    TimedOut               = $result.TimedOut
    TerminationReason      = $result.TerminationReason
    ResizeHResult          = ('0x{0:X8}' -f $result.ResizeHResult)
} | Format-List

'--- Captured ConPTY output (control characters escaped) ---'
$result.Output.Replace([string][char]27, '<ESC>')

if (-not $result.Passed) {
    throw "ConPTY probe failed for Shell '$Shell'."
}

if ($CaptureRoot) {
    if (-not $CaseName) {
        throw '-CaseName is required when -CaptureRoot is specified.'
    }

    $capturePath = Write-GoldenStreamBundle `
        -Root $CaptureRoot `
        -Name $CaseName `
        -ProbeResult $result `
        -AllowOverwrite:$OverwriteCapture
    "Golden Stream written to: $capturePath"
}
