[CmdletBinding()]
param([Parameter(Mandatory)][string]$SessionHostDirectory)

$ErrorActionPreference = 'Stop'
$verify = Join-Path $PSScriptRoot '../scripts/Test-ConPtyRuntime.ps1'
$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$work = [IO.Path]::GetFullPath((Join-Path $tempPrefix ('TermLink-ConPty-' + [guid]::NewGuid().ToString('N'))))
if (-not $work.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ConPTY test directory escaped the temporary root.'
}

function Test-Backend([string]$Choice, [string]$Expected, [string]$Failure) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $work 'TermLink.SessionHost.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    [void]$start.Environment.Remove('TERMLINK_CONPTY_BACKEND')
    if ($Choice) { $start.Environment['TERMLINK_CONPTY_BACKEND'] = $Choice }
    foreach ($arg in @('--command-base64', [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('cmd.exe /d /c exit 0')),
        '--diagnostics', '--timeout-ms', '10000')) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { throw 'Backend probe hung.' }
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($Failure) {
            if ($process.ExitCode -eq 0 -or -not $errorText.Contains($Failure)) {
                throw "Backend '$Choice' did not reject a broken configuration: $errorText"
            }
        } elseif ($process.ExitCode -ne 0 -or -not $errorText.Contains("__CONPTY_BACKEND__:$Expected")) {
            throw "Backend '$Choice' did not select '$Expected': $errorText"
        }
        [void]$stdout.GetAwaiter().GetResult()
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Assert-PackageRejected {
    try { & $verify -Directory $work }
    catch { return }
    throw 'Packaging verification accepted an incomplete or altered ConPTY runtime.'
}

try {
    [IO.Directory]::CreateDirectory($work) | Out-Null
    Get-ChildItem -LiteralPath $SessionHostDirectory | Copy-Item -Destination $work -Recurse
    & $verify -Directory $work
    Test-Backend -Expected bundled
    Test-Backend -Choice bundled -Expected bundled
    Test-Backend -Choice system -Expected system
    Test-Backend -Choice invalid -Failure 'must be bundled or system'

    $dll = Join-Path $work 'conpty.dll'
    Move-Item -LiteralPath $dll -Destination ($dll + '.saved')
    Test-Backend -Failure 'runtime is incomplete'
    Assert-PackageRejected
    # Legacy layouts remain usable only with both vendor pieces absent.
    foreach ($arch in @('x64', 'arm64')) {
        Move-Item -LiteralPath (Join-Path $work "$arch/OpenConsole.exe") -Destination (Join-Path $work "$arch/OpenConsole.saved")
    }
    Test-Backend -Expected system
    Test-Backend -Choice bundled -Failure 'runtime is incomplete'
    Assert-PackageRejected
    foreach ($arch in @('x64', 'arm64')) {
        Move-Item -LiteralPath (Join-Path $work "$arch/OpenConsole.saved") -Destination (Join-Path $work "$arch/OpenConsole.exe")
    }
    Move-Item -LiteralPath ($dll + '.saved') -Destination $dll
    [IO.File]::AppendAllText($dll, 'tamper')
    Assert-PackageRejected
    Write-Output 'ConPTY package integrity and backend selection validation passed.'
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
