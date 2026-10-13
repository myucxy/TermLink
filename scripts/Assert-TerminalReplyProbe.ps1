# Assertions for bytes read by the isolated ConPTY child from its real stdin.
function Assert-TerminalReplyProbe {
    param(
        [Parameter(Mandatory)][ValidateSet('geometry', 'queries', 'state', 'theme',
            'theme-switch-arm', 'theme-switch-read', 'theme-switch-same',
            'theme-switch-unsubscribe', 'theme-switch-restore', 'theme-switch-final')][string]$Phase,
        [Parameter(Mandatory)][object]$Probe,
        [Parameter(Mandatory)][object]$Inspect,
        [ValidateRange(0, 2)][int]$ExpectedTheme = 0
    )

    $phaseCode = $Phase.ToUpperInvariant().Replace('-', '_')
    function Require([bool]$condition, [string]$code) {
        if (-not $condition) { throw "REPLY_${phaseCode}_$code" }
    }
    function Value($item, [string]$name) {
        $property = if ($item -is [Collections.IDictionary]) {
            if ($item.Contains($name)) { $item[$name] } else { $null }
        } else { $item.PSObject.Properties[$name]?.Value }
        if ($null -eq $property) { throw "REPLY_${phaseCode}_MISSING_$($name.ToUpperInvariant())" }
        return $property
    }
    function Decode([string]$text) {
        Require ($text -cmatch '^(?:[0-9A-F]{2})*$') 'INVALID_HEX'
        return [Text.Encoding]::Latin1.GetString([Convert]::FromHexString($text))
    }
    function Sent($record) {
        try { return [Text.Encoding]::Latin1.GetString([Convert]::FromBase64String([string](Value $record 'sent_b64'))) }
        catch { throw "REPLY_${phaseCode}_INVALID_BASE64" }
    }
    function Reports([string]$text) {
        $pattern = [regex]::Escape($esc + 'P') + '([12])\$u[^\x1b]*' + [regex]::Escape($st)
        $found = [regex]::Matches($text, $pattern)
        Require ($found.Count -eq 2 -and ($found.Value -join '') -ceq $text -and
            $found[0].Groups[1].Value -ceq '1' -and $found[1].Groups[1].Value -ceq '2') 'STATE_REPORTS'
        return @($found[0].Value, $found[1].Value)
    }

    $esc = [string][char]27
    $st = $esc + '\'
    Require ([string](Value $Probe 'phase') -ceq $Phase) 'PHASE'
    Require (-not [bool](Value $Probe 'timed_out') -and -not [bool](Value $Probe 'truncated')) 'BOUNDED_CAPTURE'
    Require ($null -eq $Probe.error) 'CHILD_ERROR'
    $records = @(Value $Probe 'records')
    $stages = @(switch ($Phase) {
        'geometry' { @('geometry') }
        'queries' { @('queries', 'fixed', 'repeat', 'split-head', 'split-tail') }
        'state' { @('before', 'mutate', 'changed', 'bad-query-quiet',
            'cancelled-restore-quiet', 'after-rejection', 'restore-and-query') }
        'theme' { @('query', 'status-before', 'subscribe', 'subscribe-again',
            'status-on', 'unsubscribe', 'status-after', 'ris-and-query', 'final-quiet') }
        'theme-switch-arm' { @('query', 'subscribe', 'extra-quiet') }
        'theme-switch-read' { @('changed', 'extra-quiet') }
        'theme-switch-same' { @('same-color-quiet') }
        'theme-switch-unsubscribe' { @('unsubscribe', 'status-after') }
        'theme-switch-restore' { @('restored-quiet') }
        'theme-switch-final' { @('final-query', 'extra-quiet') }
    })
    Require ($records.Count -eq $stages.Count) 'RECORD_COUNT'
    $sentParts = [Collections.Generic.List[string]]::new()
    $receivedParts = [Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $records.Count; $index++) {
        $record = $records[$index]
        Require ([string](Value $record 'stage') -ceq $stages[$index]) 'RECORD_STAGE'
        Require (-not [bool](Value $record 'timed_out') -and -not [bool](Value $record 'truncated')) 'RECORD_BOUNDED'
        Require ([string]$record.unexpected_code_units_hex -ceq '') 'UNEXPECTED_CONSOLE_CHARACTERS'
        $sentParts.Add((Sent $record))
        $hex = [string](Value $record 'received_hex')
        $receivedParts.Add($hex)
        $chunks = @($record.received_chunks_hex)
        Require (($chunks -join '') -ceq $hex) 'READ_CHUNKS'
    }
    $sent = $sentParts -join ''
    $receivedHex = $receivedParts -join ''
    Require ($sent -ceq [Text.Encoding]::Latin1.GetString(
        [Convert]::FromBase64String([string](Value $Probe 'sent_b64')))) 'SENT_AGGREGATE'
    Require ($receivedHex -ceq [string](Value $Probe 'received_hex')) 'RECEIVED_AGGREGATE'
    Require (($receivedHex.Length / 2) -le 32768) 'RECEIVED_LIMIT'

    $geometryDetailUnavailable = $false
    $themeScheme = $null
    switch ($Phase) {
        'geometry' {
            $expected = $esc + '[14t' + $esc + '[14;2t' + $esc + '[15t' +
                $esc + '[16t' + $esc + '[18t' + $esc + '[19t'
            Require ($sent -ceq $expected) 'QUERY_BYTES'
            $reply = Decode $receivedHex
            $found = [regex]::Matches($reply, [regex]::Escape($esc + '[') +
                '([45689]);([1-9][0-9]{0,5});([1-9][0-9]{0,5})t')
            Require ($found.Count -ge 1 -and ($found.Value -join '') -ceq $reply) 'REPLY_BYTES'
            $expectedKinds = @(4, 4, 5, 6, 8, 9)
            $kindIndex = -1
            $values = @{}
            foreach ($match in $found) {
                $kind = [int]::Parse($match.Groups[1].Value)
                do { $kindIndex++ } while ($kindIndex -lt $expectedKinds.Count -and $expectedKinds[$kindIndex] -ne $kind)
                Require ($kindIndex -lt $expectedKinds.Count) 'REPLY_ORDER_OR_SIZE'
                $height = [int]::Parse($match.Groups[2].Value)
                $width = [int]::Parse($match.Groups[3].Value)
                $maximum = if ($kind -eq 4) { 524288 } elseif ($kind -eq 6) { 1024 } elseif ($kind -eq 8) { 512 } else { 32768 }
                Require ($height -le $maximum -and $width -le $maximum) 'REPLY_ORDER_OR_SIZE'
                if (-not $values.ContainsKey($kind)) { $values[$kind] = @() }
                $values[$kind] += ,@($height, $width)
            }
            $rows = [int](Value $Inspect 'terminal_rows')
            $columns = [int](Value $Inspect 'terminal_columns')
            Require ($values.ContainsKey(8) -and $values[8].Count -eq 1 -and
                $values[8][0][0] -eq $rows -and $values[8][0][1] -eq $columns) 'ANDROID_GRID'
            $cellKnown = $values.ContainsKey(6)
            $displayKnown = $values.ContainsKey(5)
            $windowReplies = if ($values.ContainsKey(4)) { $values[4].Count } else { 0 }
            Require ($windowReplies -ge [int]$cellKnown -and $windowReplies -le 1 + [int]$cellKnown) 'CELL_PIXEL_GRID'
            if ($cellKnown) {
                Require ($values[4][0][0] -eq $rows * $values[6][0][0] -and
                    $values[4][0][1] -eq $columns * $values[6][0][1]) 'CELL_PIXEL_GRID'
            }
            $expectedDisplayCells = if ($cellKnown -and $displayKnown) {
                @([int][Math]::Floor($values[5][0][0] / $values[6][0][0]),
                    [int][Math]::Floor($values[5][0][1] / $values[6][0][1]))
            } else { @(0, 0) }
            $displayCellsKnown = $expectedDisplayCells[0] -gt 0 -and $expectedDisplayCells[1] -gt 0
            Require ($values.ContainsKey(9) -eq $displayCellsKnown) 'DISPLAY_CELL_GRID'
            if ($displayCellsKnown) {
                Require ($values[9][0][0] -eq $expectedDisplayCells[0] -and
                    $values[9][0][1] -eq $expectedDisplayCells[1]) 'DISPLAY_CELL_GRID'
            }
            $geometry = $Inspect.PSObject.Properties['host_geometry']?.Value
            $geometryDetailUnavailable = $null -eq $geometry
            if (-not $geometryDetailUnavailable) {
                $cellWidth = [int](Value $geometry 'cell_width')
                $cellHeight = [int](Value $geometry 'cell_height')
                $displayWidth = [int](Value $geometry 'display_width')
                $displayHeight = [int](Value $geometry 'display_height')
                $windowWidth = [int](Value $geometry 'window_width')
                $windowHeight = [int](Value $geometry 'window_height')
                $expectedReplies = [Collections.Generic.List[string]]::new()
                if ($cellWidth -gt 0 -and $cellHeight -gt 0) {
                    $expectedReplies.Add($esc + "[4;$($rows * $cellHeight);$($columns * $cellWidth)t")
                }
                if ($windowWidth -gt 0 -and $windowHeight -gt 0) {
                    $expectedReplies.Add($esc + "[4;$windowHeight;$windowWidth" + 't')
                }
                if ($displayWidth -gt 0 -and $displayHeight -gt 0) {
                    $expectedReplies.Add($esc + "[5;$displayHeight;$displayWidth" + 't')
                }
                if ($cellWidth -gt 0 -and $cellHeight -gt 0) {
                    $expectedReplies.Add($esc + "[6;$cellHeight;$cellWidth" + 't')
                }
                $expectedReplies.Add($esc + "[8;$rows;$columns" + 't')
                if ($cellWidth -gt 0 -and $cellHeight -gt 0 -and
                    $displayWidth -ge $cellWidth -and $displayHeight -ge $cellHeight) {
                    $displayRows = [int][Math]::Floor($displayHeight / $cellHeight)
                    $displayColumns = [int][Math]::Floor($displayWidth / $cellWidth)
                    $expectedReplies.Add($esc + "[9;$displayRows;$displayColumns" + 't')
                }
                Require ($reply -ceq ($expectedReplies -join '')) 'ANDROID_HOST_GEOMETRY'
            }
        }
        'queries' {
            $expected = $esc + '[1;1HAB' + $esc + '[=c' + $esc + '[?15n' +
                $esc + '["v' + $esc + '[42;1;1;1;1;2*y'
            Require ($sentParts[0] -ceq $expected) 'QUERY_BYTES'
            $reply = Decode $receivedParts[0]
            $da3 = $esc + 'P!|00000000' + $st
            $printer = $esc + '[?13n'
            $checksum = $esc + 'P42!~FE9D' + $st
            $display = [regex]::Match($reply, [regex]::Escape($esc + '[') + '([0-9]+);([0-9]+);1;1;1"w')
            Require ($display.Success -and $reply -ceq ($da3 + $printer + $display.Value + $checksum)) 'REPLY_BYTES'
            Require ([int]$display.Groups[1].Value -eq [int](Value $Inspect 'terminal_rows') -and
                [int]$display.Groups[2].Value -eq [int](Value $Inspect 'terminal_columns')) 'DISPLAY_RANGE'
            $fixedQueries = $esc + '[x' + $esc + '[1x' + $esc + '[?25n' + $esc + '[?26n' +
                $esc + '[?55n' + $esc + '[?56n' + $esc + '[?75n'
            $fixedReplies = $esc + '[2;1;1;128;128;1;0x' + $esc + '[3;1;1;128;128;1;0x' +
                $esc + '[?23n' + $esc + '[?27;0;0;5n' + $esc + '[?53n' +
                $esc + '[?57;0n' + $esc + '[?70n'
            Require ($sentParts[1] -ceq $fixedQueries -and
                (Decode $receivedParts[1]) -ceq $fixedReplies) 'FIXED_QUERY_REPLY'
            $da3 = $esc + 'P!|00000000' + $st
            Require ($sentParts[2] -ceq ($esc + '[=c' + $esc + '[=c') -and
                (Decode $receivedParts[2]) -ceq ($da3 + $da3)) 'REPEATED_QUERY_REPLY'
            Require ($sentParts[3] -ceq ($esc + '[?26') -and $receivedParts[3].Length -eq 0 -and
                $sentParts[4] -ceq 'n' -and
                (Decode $receivedParts[4]) -ceq ($esc + '[?27;0;0;5n')) 'SPLIT_QUERY_REPLY'
        }
        'state' {
            $expectedQueries = $esc + '[1$w' + $esc + '[2$w'
            Require ($sentParts[0] -ceq $expectedQueries -and
                $sentParts[1] -ceq ($esc + '[2;3H' + $esc + '[1m' + $esc + '[3g')) 'QUERY_BYTES'
            $before = Reports (Decode $receivedParts[0])
            Require ($receivedParts[1].Length -eq 0) 'MUTATION_REPLY'
            Require ($sentParts[2] -ceq $expectedQueries) 'CHANGED_QUERY_BYTES'
            $changed = Reports (Decode $receivedParts[2])
            $cursorPrefix = [regex]::Escape($esc + 'P1$u') + '2;3;[1-9][0-9]*;A;'
            Require ($changed[0] -cmatch ('^' + $cursorPrefix) -and
                $changed[0] -cne $before[0] -and
                $changed[1] -ceq ($esc + 'P2$u' + $st) -and
                $changed[1] -cne $before[1]) 'STATE_MUTATION'
            Require ($sentParts[3] -ceq ($esc + '[2;3$w') -and
                $sentParts[4] -ceq ($esc + 'P2$t1/2/513' + [string][char]24) -and
                $receivedParts[3].Length -eq 0 -and $receivedParts[4].Length -eq 0) 'REJECTED_INPUT'
            Require ($sentParts[5] -ceq $expectedQueries) 'REJECTION_QUERY_BYTES'
            $afterRejection = Reports (Decode $receivedParts[5])
            Require ($afterRejection[0] -ceq $changed[0] -and
                $afterRejection[1] -ceq $changed[1]) 'REJECTION_ATOMICITY'
            $cursorRestore = $before[0].Substring(0, 4) + 't' + $before[0].Substring(5)
            $tabRestore = $before[1].Substring(0, 4) + 't' + $before[1].Substring(5)
            Require ($sentParts[6] -ceq ($cursorRestore + $tabRestore + $expectedQueries)) 'RESTORE_BYTES'
            $after = Reports (Decode $receivedParts[6])
            Require ($after[0] -ceq $before[0] -and $after[1] -ceq $before[1]) 'RESTORE_ROUNDTRIP'
        }
        'theme' {
            $expectedSent = @(
                ($esc + '[?996n'), ($esc + '[?2031$p'), ($esc + '[?2031h'),
                ($esc + '[?2031h'), ($esc + '[?2031$p'), ($esc + '[?2031l'),
                ($esc + '[?2031$p'), ($esc + 'c' + $esc + '[?996n' + $esc + '[?2031$p'), ''
            )
            for ($index = 0; $index -lt $expectedSent.Count; $index++) {
                Require ($sentParts[$index] -ceq $expectedSent[$index]) 'QUERY_BYTES'
            }
            $theme = Decode $receivedParts[0]
            Require ($theme -cmatch ('^' + [regex]::Escape($esc + '[?997;') + '[12]n$')) 'THEME_REPLY'
            Require ((Decode $receivedParts[1]) -ceq ($esc + '[?2031;2$y')) 'MODE_BEFORE'
            Require ((Decode $receivedParts[2]) -ceq $theme) 'SUBSCRIBE_REPLY'
            Require ($receivedParts[3].Length -eq 0) 'DUPLICATE_SUBSCRIPTION_REPLY'
            Require ((Decode $receivedParts[4]) -ceq ($esc + '[?2031;1$y')) 'MODE_ON'
            Require ($receivedParts[5].Length -eq 0) 'UNSUBSCRIBE_REPLY'
            Require ((Decode $receivedParts[6]) -ceq ($esc + '[?2031;2$y')) 'MODE_OFF'
            Require ((Decode $receivedParts[7]) -ceq ($theme + $esc + '[?2031;2$y')) 'RIS_PRESERVES_THEME'
            Require ($receivedParts[8].Length -eq 0) 'EXTRA_REPLY'
        }
        'theme-switch-arm' {
            Require ($sentParts[0] -ceq ($esc + '[?996n') -and
                $sentParts[1] -ceq ($esc + '[?2031h') -and $sentParts[2].Length -eq 0) 'QUERY_BYTES'
            $theme = Decode $receivedParts[0]
            Require ($theme -cmatch ('^' + [regex]::Escape($esc + '[?997;') + '([12])n$')) 'THEME_REPLY'
            Require ((Decode $receivedParts[1]) -ceq $theme) 'SUBSCRIBE_REPLY'
            Require ($receivedParts[2].Length -eq 0) 'EXTRA_REPLY'
            $themeScheme = [int]$Matches[1]
            if ($ExpectedTheme -ne 0) { Require ($themeScheme -eq $ExpectedTheme) 'BASELINE_SCHEME' }
        }
        'theme-switch-read' {
            Require ($ExpectedTheme -in @(1, 2)) 'EXPECTED_THEME_REQUIRED'
            Require ($sent.Length -eq 0) 'QUERY_BYTES'
            $opposite = if ($ExpectedTheme -eq 1) { 2 } else { 1 }
            Require ((Decode $receivedParts[0]) -ceq ($esc + '[?997;' + $opposite + 'n')) 'CHANGED_REPLY'
            Require ($receivedParts[1].Length -eq 0) 'EXTRA_REPLY'
        }
        'theme-switch-same' {
            Require ($sent.Length -eq 0) 'QUERY_BYTES'
            Require ($receivedHex.Length -eq 0) 'SAME_COLOR_REPLY'
        }
        'theme-switch-unsubscribe' {
            Require ($sentParts[0] -ceq ($esc + '[?2031l') -and
                $sentParts[1] -ceq ($esc + '[?2031$p')) 'QUERY_BYTES'
            Require ($receivedParts[0].Length -eq 0) 'UNSUBSCRIBE_REPLY'
            Require ((Decode $receivedParts[1]) -ceq ($esc + '[?2031;2$y')) 'MODE_OFF'
        }
        'theme-switch-restore' {
            Require ($sent.Length -eq 0) 'QUERY_BYTES'
            Require ($receivedHex.Length -eq 0) 'UNSUBSCRIBED_REPLY'
        }
        'theme-switch-final' {
            Require ($ExpectedTheme -in @(1, 2)) 'EXPECTED_THEME_REQUIRED'
            Require ($sentParts[0] -ceq ($esc + '[?996n') -and $sentParts[1].Length -eq 0) 'QUERY_BYTES'
            Require ((Decode $receivedParts[0]) -ceq ($esc + '[?997;' + $ExpectedTheme + 'n')) 'RESTORED_REPLY'
            Require ($receivedParts[1].Length -eq 0) 'EXTRA_REPLY'
        }
    }
    return [pscustomobject]@{
        phase = $Phase
        received_bytes = [int]($receivedHex.Length / 2)
        geometry_detail_unavailable = $geometryDetailUnavailable
        theme_scheme = $themeScheme
        byte_assertions = 'passed'
    }
}
