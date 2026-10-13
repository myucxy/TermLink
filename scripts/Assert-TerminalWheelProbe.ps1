# Checks raw bytes read from the real ConPTY child after the product's wheel controls are tapped.
function Assert-TerminalWheelProbe {
    param(
        [Parameter(Mandatory)][object]$Probe,
        [Parameter(Mandatory)][object]$Inspect
    )

    function Require([bool]$condition, [string]$code) {
        if (-not $condition) { throw "WHEEL_$code" }
    }
    function Field($item, [string]$name) {
        $property = $item.PSObject.Properties[$name]
        if ($null -eq $property) { throw "WHEEL_MISSING_$name" }
        return $property.Value
    }
    function DecodeHex([string]$hex) {
        Require ($hex -cmatch '^(?:[0-9A-F]{2})*$') 'INVALID_HEX'
        return [Text.Encoding]::Latin1.GetString([Convert]::FromHexString($hex))
    }

    Require ([string](Field $Probe 'phase') -ceq 'mouse-read') 'PHASE'
    Require (-not [bool](Field $Probe 'timed_out') -and -not [bool](Field $Probe 'truncated')) 'BOUNDED_CAPTURE'
    Require ($null -eq (Field $Probe 'error')) 'CHILD_ERROR'
    $records = @(Field $Probe 'records')
    Require ($records.Count -eq 1) 'RECORD_COUNT'
    $record = $records[0]
    Require ([string](Field $record 'stage') -ceq 'wheel' -and
        -not [bool](Field $record 'timed_out') -and -not [bool](Field $record 'truncated')) 'RECORD_STAGE'
    Require ([string](Field $Probe 'sent_b64') -ceq '' -and
        [string](Field $record 'sent_b64') -ceq '') 'UNEXPECTED_OUTPUT'
    $hex = [string](Field $record 'received_hex')
    Require ($hex -ceq [string](Field $Probe 'received_hex') -and
        $hex.Length -le 65536) 'RECEIVED_AGGREGATE'
    $chunks = @(Field $record 'received_chunks_hex')
    Require ($chunks.Count -ge 1 -and ($chunks -join '') -ceq $hex) 'READ_CHUNKS'
    foreach ($chunk in $chunks) { $null = DecodeHex ([string]$chunk) }
    $bytes = DecodeHex $hex

    $esc = [string][char]27
    $reports = [regex]::Matches($bytes, [regex]::Escape($esc + '[<') +
        '(64|65);([1-9][0-9]{0,2});([1-9][0-9]{0,2})M')
    Require ($reports.Count -eq 2 -and ($reports.Value -join '') -ceq $bytes) 'REPORT_BYTES'
    Require ($reports[0].Groups[1].Value -ceq '64' -and
        $reports[1].Groups[1].Value -ceq '65') 'DIRECTION'
    $rows = [int](Field $Inspect 'terminal_rows')
    $columns = [int](Field $Inspect 'terminal_columns')
    Require ($rows -gt 0 -and $rows -le 512 -and $columns -gt 0 -and $columns -le 512) 'GRID'
    foreach ($report in $reports) {
        $x = [int]$report.Groups[2].Value
        $y = [int]$report.Groups[3].Value
        Require ($x -le $columns -and $y -le $rows) 'COORDINATE'
    }
    return [pscustomobject]@{
        phase = 'mouse-read'
        received_bytes = [int]($hex.Length / 2)
        byte_assertions = 'passed'
    }
}
