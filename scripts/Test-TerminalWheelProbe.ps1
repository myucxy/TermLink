[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Assert-TerminalWheelProbe.ps1')

$esc = [string][char]27
$inspect = [pscustomobject]@{ terminal_rows = 24; terminal_columns = 80 }
function Probe([string]$raw) {
    $hex = [Convert]::ToHexString([Text.Encoding]::Latin1.GetBytes($raw))
    return [pscustomobject]@{
        phase = 'mouse-read'; sent_b64 = ''; received_hex = $hex
        records = @([pscustomobject]@{
            stage = 'wheel'; sent_b64 = ''; received_hex = $hex
            received_chunks_hex = @($hex.Substring(0, 4), $hex.Substring(4, 8), $hex.Substring(12))
            timed_out = $false; truncated = $false
        })
        timed_out = $false; truncated = $false; error = $null
    }
}
function ExpectFailure([scriptblock]$run, [string]$code) {
    try { $null = & $run; throw "EXPECTED_FAILURE_MISSING_$code" }
    catch { if ($_.Exception.Message -cne $code) { throw } }
}

$up = $esc + '[<64;79;4M'
$down = $esc + '[<65;79;4M'
$valid = Probe ($up + $down)
if ((Assert-TerminalWheelProbe $valid $inspect).byte_assertions -cne 'passed') {
    throw 'WHEEL_VALID_VECTOR_FAILED'
}
if ((Assert-TerminalWheelProbe ($valid | ConvertTo-Json -Depth 5 | ConvertFrom-Json) $inspect).
    byte_assertions -cne 'passed') { throw 'WHEEL_JSON_VECTOR_FAILED' }
ExpectFailure { Assert-TerminalWheelProbe (Probe ($up + $up)) $inspect } 'WHEEL_DIRECTION'
ExpectFailure { Assert-TerminalWheelProbe (Probe ($down + $up)) $inspect } 'WHEEL_DIRECTION'
ExpectFailure { Assert-TerminalWheelProbe (Probe ($esc + '[<64;81;4M' + $down)) $inspect } 'WHEEL_COORDINATE'
ExpectFailure { Assert-TerminalWheelProbe (Probe ($up + $esc + '[<65;79;25M')) $inspect } 'WHEEL_COORDINATE'
ExpectFailure { Assert-TerminalWheelProbe (Probe ($esc + '[<68;79;4M' + $down)) $inspect } 'WHEEL_REPORT_BYTES'
ExpectFailure { Assert-TerminalWheelProbe (Probe ($up + $down + 'X')) $inspect } 'WHEEL_REPORT_BYTES'
$badChunks = Probe ($up + $down)
$badChunks.records[0].received_chunks_hex[1] = '00000000'
ExpectFailure { Assert-TerminalWheelProbe $badChunks $inspect } 'WHEEL_READ_CHUNKS'
$timedOut = Probe ($up + $down)
$timedOut.timed_out = $true
ExpectFailure { Assert-TerminalWheelProbe $timedOut $inspect } 'WHEEL_BOUNDED_CAPTURE'

'Wheel probe accepted split reads and rejected duplicate, reverse, malformed and extra input.'
