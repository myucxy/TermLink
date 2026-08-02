[CmdletBinding()]
param(
    [ValidateSet('cmd', 'powershell', 'pwsh', 'wsl')]
    [string]$Shell = 'powershell'
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
        $commandLine = "`"$pwshPath`" -NoLogo -NoProfile"
        $inputText = @(
            "Write-Output '__OUTPUT_CAPTURE_OK__'"
            "`$termlinkValue = 'android-input' + '-simulation'"
            "Write-Output ('__INPUT_CAPTURED__:' + `$termlinkValue)"
            'exit 17'
        ) -join "`r"
    }
    'wsl' {
        $commandLine = 'wsl.exe --exec bash --noprofile --norc'
        $inputText = @(
            "printf '__OUTPUT_CAPTURE_OK__\n'"
            "termlink_value='android-input''-simulation'"
            'printf ''__INPUT_CAPTURED__:%s\n'' "$termlink_value"'
            'exit 17'
        ) -join "`r"
    }
}

$inputText += "`r"
$result = [TermLink.Validation.ConPtyProbe]::Run(
    $commandLine,
    $inputText,
    $expectedOutput,
    $expectedInput,
    $expectedExitCode,
    15000)

[PSCustomObject]@{
    Shell                  = $Shell
    Passed                 = $result.Passed
    OutputCaptured         = $result.OutputCaptured
    InputRoundTripCaptured = $result.InputRoundTripCaptured
    ResizeSucceeded        = $result.ResizeSucceeded
    ExitCodeCaptured       = $result.ExitCodeCaptured
    ExitCode               = $result.ExitCode
    ResizeHResult          = ('0x{0:X8}' -f $result.ResizeHResult)
} | Format-List

'--- Captured ConPTY output (control characters escaped) ---'
$result.Output.Replace([string][char]27, '<ESC>')

if (-not $result.Passed) {
    throw "ConPTY probe failed for Shell '$Shell'."
}
