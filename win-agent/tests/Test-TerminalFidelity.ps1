[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SessionHostPath,
    [switch]$Managed,
    [string]$EvidencePath = (Join-Path $PSScriptRoot '../../artifacts/validation/terminal-fidelity.json')
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $SessionHostPath).Path
$child = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$e = [char]27
[Console]::Write("${e}[0mNORMAL ${e}[2mFAINT${e}[22m NORMAL ${e}[1mBOLD${e}[22m NORMAL`r`n")
[Console]::Write("${e}[38;2;111;112;113mRGB_GREY${e}[0m ${e}[48;2;62;65;70m RGB_BG ${e}[0m`r`n")
[Console]::Write("${e}[3mITALIC${e}[0m ${e}[4mUNDERLINE${e}[0m`r`n")
# Use native console key records, as Codex does on Windows; no fake theme environment.
$watch = [Diagnostics.Stopwatch]::StartNew()
[Console]::Write("${e}]10;?${e}\${e}]11;?${e}\")
$got = [Text.StringBuilder]::new()
while ($watch.ElapsedMilliseconds -lt 250) {
    if ([Console]::KeyAvailable) { [void]$got.Append([Console]::ReadKey($true).KeyChar) }
    else { Start-Sleep -Milliseconds 1 }
    if ($got.ToString().Contains("${e}]11;rgb:002b/0036/0040${e}\")) { break }
}
$ms = $watch.ElapsedMilliseconds
$reply = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($got.ToString()))
[Console]::Write("__PALETTE__:${ms}:${reply}__END__`r`n")
[Console]::Write('__INPUT_READY__')
$line = [Text.StringBuilder]::new()
$watch.Restart()
while ($watch.ElapsedMilliseconds -lt 2000) {
    if (-not [Console]::KeyAvailable) { Start-Sleep -Milliseconds 1; continue }
    $character = [Console]::ReadKey($true).KeyChar
    if ($character -eq [char]13) { break }
    [void]$line.Append($character)
}
$inputBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line.ToString()))
[Console]::Write("`r`n__INPUT_B64__:${inputBase64}__END__`r`n")
Start-Sleep -Milliseconds 200
exit 0
'@
$command = 'powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand ' +
    [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child))
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($command))
$results = foreach ($backend in @('system', 'bundled')) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($arg in @('--command-base64', $encoded, '--diagnostics', '--timeout-ms', '5000')) {
        $start.ArgumentList.Add($arg)
    }
    if ($Managed) {
        foreach ($arg in @('--managed-shell', 'windows-powershell', '--registration-timeout-ms', '3000')) {
            $start.ArgumentList.Add($arg)
        }
    }
    $start.Environment['TERMLINK_CONPTY_BACKEND'] = $backend
    $process = [Diagnostics.Process]::Start($start)
    try {
        $errorTask = $process.StandardError.ReadToEndAsync()
        $output = [Text.StringBuilder]::new()
        $buffer = [char[]]::new(1)
        $read = $process.StandardOutput.ReadAsync($buffer, 0, 1)
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        # A deterministic outer renderer: reply only to the two observed OSC queries.
        while ($deadline.ElapsedMilliseconds -lt 10000) {
            if (-not $read.Wait(30)) { continue }
            if ($read.Result -eq 0) { break }
            [void]$output.Append($buffer[0])
            if ($output.ToString().EndsWith('__INPUT_READY__', [StringComparison]::Ordinal)) {
                $process.StandardInput.Write("1中`r")
                $process.StandardInput.Flush()
            }
            foreach ($slot in @('10', '11')) {
                # Culture comparisons can ignore ESC and match again at the next
                # sequence's opening byte, injecting a duplicate reply as input.
                if ($output.ToString().EndsWith("$([char]27)]${slot};?$([char]27)\", [StringComparison]::Ordinal)) {
                    $color = if ($slot -eq '10') { 'eeee/eeee/eeee' } else { '002b/0036/0040' }
                    $process.StandardInput.Write("$([char]27)]${slot};rgb:${color}$([char]27)\")
                    $process.StandardInput.Flush()
                }
            }
            $read = $process.StandardOutput.ReadAsync($buffer, 0, 1)
        }
        if (-not $process.WaitForExit(2000)) { throw 'Fidelity probe did not exit.' }
        $raw = $output.ToString()
        $errors = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or -not $errors.Contains("__CONPTY_BACKEND__:$backend")) {
            throw "Fidelity probe failed: $errors"
        }
        if ($Managed -and ($errors -notmatch 'authority_failed=false' -or
            $errors -notmatch 'durable_ack=[1-9]\d*')) { throw "Authority did not consume the output: $errors" }
        $palette = [regex]::Match($raw, '__PALETTE__:(\d+):([A-Za-z0-9+/=]*)__END__')
        if (-not $palette.Success) { throw 'Palette probe result is missing.' }
        $reply = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($palette.Groups[2].Value))
        [pscustomobject]@{
            backend = $backend
            managed = [bool]$Managed
            faint_preserved = $raw.Contains("$([char]27)[2mFAINT")
            rgb_foreground_preserved = $raw.Contains("$([char]27)[38;2;111;112;113m")
            rgb_background_preserved = $raw.Contains("$([char]27)[48;2;62;65;70m")
            palette_round_trip = $reply.Contains("$([char]27)]10;rgb:eeee/eeee/eeee$([char]27)\") -and
                $reply.Contains("$([char]27)]11;rgb:002b/0036/0040$([char]27)\")
            palette_elapsed_ms = [int]$palette.Groups[1].Value
            input_preserved = $raw.Contains('__INPUT_B64__:MeS4rQ==__END__')
            output_escaped = $raw.Replace([string][char]27, '<ESC>')
        }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
$results | Select-Object -ExcludeProperty output_escaped | Format-Table -AutoSize
$bundled = $results | Where-Object backend -eq 'bundled'
if (-not ($bundled.faint_preserved -and $bundled.rgb_foreground_preserved -and
    $bundled.rgb_background_preserved -and $bundled.palette_round_trip -and $bundled.input_preserved)) {
    throw "Bundled ConPTY lost terminal fidelity; see $EvidencePath"
}
