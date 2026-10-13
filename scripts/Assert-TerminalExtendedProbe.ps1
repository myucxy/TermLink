# Small, observable continuations of the real Broker semantic probe. The caller
# writes each decoded sequence to the same ConPTY child, then supplies Android's
# terminal_semantics from inspect. reset_before means send RIS before this vector.
# shortcut: fail-closed malformed vectors need isolated runs, never this continuous session.
function Get-TerminalExtendedPhaseDefinitions {
    param([ValidateRange(20, 512)][int]$Columns = 20)
    $esc = [string][char]27
    $cropColumn = [int][Math]::Floor($Columns / 2)
    $marker = 'TLX_C05_SOFT'
    function Phase([string]$name, [bool]$reset, [string]$sequence, [string]$expectedMarker) {
        [pscustomobject]@{
            phase = $name
            reset_before = $reset
            sequence_b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($sequence))
            expected_marker = $expectedMarker
        }
    }
    @(
        (Phase 'c05-soft-pre' $true (
            $esc + '[1;1H' + $marker + $esc + '[4;1HKEEP' +
            $esc + '[5;1H' + $esc + '[2;4:3;31mR' +
            $esc + '[?69h' + $esc + '[3;18s' + $esc + '[2;5r' +
            $esc + '[?6h' + $esc + '[4h' + $esc + '[?7l' +
            $esc + '[2;11HQ' + $esc + '[2;11H' + $esc + '7'
        ) $marker)
        (Phase 'c05-soft-reset' $false ($esc + '[!p') $marker)
        (Phase 'c05-soft-write' $false 'Z' $marker)
        (Phase 'c05-hindex-back' $true (
            $esc + '[1;1HTLX_C05_BACK' + $esc + '[4;1Habcdefgh' +
            $esc + '[5;1H12345678' + $esc + '[?69h' + $esc + '[3;6s' +
            $esc + '[4;5r' + $esc + '[4;3H' + $esc + '6'
        ) 'TLX_C05_BACK')
        (Phase 'c05-hindex-forward' $true (
            $esc + '[1;1HTLX_C05_FORWARD' + $esc + '[4;1Habcdefgh' +
            $esc + '[5;1H12345678' + $esc + '[?69h' + $esc + '[3;6s' +
            $esc + '[4;5r' + $esc + '[4;6H' + $esc + '9'
        ) 'TLX_C05_FORWARD')
        (Phase 'c05-tabs-default' $true (
            $esc + '[1;1HTLX_C05_TABS' + $esc + '[3g' +
            $esc + '[4;4H' + $esc + 'H' + $esc + '[?5W' + [char]13 + [char]9 + [char]9
        ) 'TLX_C05_TABS')
        (Phase 'c05-line-single' $true (
            $esc + '[1;1HTLX_C05_LINE' + $esc + '[4;1H' + $esc + '#6WIDE' + $esc + '#5'
        ) 'TLX_C05_LINE')
        (Phase 'c05-alignment' $true (
            $esc + '#8' + $esc + '7' + $esc + '[1;1HTLX_C05_ALIGN' + $esc + '8'
        ) 'TLX_C05_ALIGN')
        (Phase 'c03-split-pre' $true (
            $esc + '[1;1HTLX_C03_SPLIT' + $esc + '[4;1H' + [char]0x0301 +
            'Z' + $esc + '[4;4H©'
        ) 'TLX_C03_SPLIT')
        (Phase 'c03-split-join' $false ([char]0xfe0f + 'X') 'TLX_C03_SPLIT')
        (Phase 'c04-crop-restore' $true (
            $esc + '[1;1HTLX_C04_CROP' + $esc + '[4;1HABCDEFGHIJ' +
            $esc + '[4;' + ($cropColumn + 1) + 'HKL' + $esc + '#6' + $esc + '#5'
        ) 'TLX_C04_CROP')
        (Phase 'c04-scroll' $true (
            $esc + '[1;1HTLX_C04_SCROLL' + $esc + '[3;5r' +
            $esc + '[4;1H' + $esc + '#6WIDE' + $esc + '[5;1H' + [char]10
        ) 'TLX_C04_SCROLL')
        (Phase 'c06-stack' $true (
            $esc + '[1;1HTLX_C06_STACK' + $esc + '[4;1H' +
            $esc + '[38;2;1;2;3;58;2;4;5;6;4:3mA' + $esc + '[#{' +
            $esc + '[38;2;7;8;9;58;2;10;11;12;4:4mB' + $esc + '[#{' +
            $esc + '[0mC' + $esc + '[#}D' + $esc + '[#}E'
        ) 'TLX_C06_STACK')
        (Phase 'c09-page-three' $true (
            $esc + '[1;1HTLX_C09_PAGE' + $esc + '[4;1HONE' +
            $esc + '[3 P' + $esc + '[1;1HTLX_C09_PAGE' + $esc + '[4;1HTHREE'
        ) 'TLX_C09_PAGE')
        (Phase 'c09-page-one' $false ($esc + '[2 R') 'TLX_C09_PAGE')
        (Phase 'c10-utf8' $true (
            $esc + '[1;1HTLX_C10_UTF8' + $esc + '[4;1Hé中'
        ) 'TLX_C10_UTF8')
        (Phase 'c10-legacy-return' $false (
            $esc + '[4;1H' + $esc + '%@' + $esc + 'P1!uB' + $esc + '\' +
            $esc + '-<' + [char]14 + '!' + [char]15 + $esc + '%Gé'
        ) 'TLX_C10_UTF8')
        (Phase 'c11-replace-delete' $true (
            $esc + '[1;1HTLX_C11_MACRO' + $esc + '[4;1H' +
            $esc + 'P0!zOLD' + $esc + '\' + $esc + '[0*z' +
            $esc + '[5;1H' + $esc + 'P0!zNEW' + $esc + '\' + $esc + '[0*z' +
            $esc + '[6;1H' + $esc + 'P0!z' + $esc + '\' + $esc + '[0*zX'
        ) 'TLX_C11_MACRO')
        (Phase 'c12-overwrite' $true (
            $esc + '[1;1HTLX_C12_DRCS' + $esc + '[4;1H' +
            $esc + 'P1;1;0;5;1;2;8;0{ @~~~~~' + $esc + '\' + $esc + '( @!' +
            $esc + '[4;3H' + $esc + 'P1;1;0;5;1;2;8;0{ @?????' + $esc + '\' + $esc + '( @!'
        ) 'TLX_C12_DRCS')
        (Phase 'c12-erase' $false ($esc + '[4;1H' + $esc + '[2K') 'TLX_C12_DRCS')
        (Phase 'c13-two-colors' $true (
            $esc + '[1;1HTLX_C13_SIXEL' + $esc + '[4;1H' +
            $esc + 'P0;1;0q"1;1;2;6#1;2;100;0;0~#2;2;0;100;0~' + $esc + '\'
        ) 'TLX_C13_SIXEL')
        (Phase 'c13-cancel' $true (
            $esc + '[1;1HTLX_C13_CANCEL' + $esc + '[4;1H' +
            $esc + 'P0;1;0q#1;2;100;0;0~' + [char]24 + 'CANCEL'
        ) 'TLX_C13_CANCEL')
        (Phase 'c14-colors-set' $true (
            $esc + '[1;1HTLX_C14_COLOR' + $esc + ']0;TermLink extended' + $esc + '\' +
            $esc + ']17;#123456' + $esc + '\' + $esc + '[1;2;3,|'
        ) 'TLX_C14_COLOR')
        (Phase 'c14-colors-reset' $false (
            $esc + ']117' + $esc + '\' + $esc + '[1;256;257,|'
        ) 'TLX_C14_COLOR')
    )
}

function Assert-TerminalExtendedProbe {
    param(
        [Parameter(Mandatory)][object]$Definition,
        [Parameter(Mandatory)][object]$Probe,
        [object]$PreviousProbe
    )
    $phase = [string]$Definition.phase
    $phaseCode = $phase.ToUpperInvariant().Replace('-', '_')
    function Need([bool]$condition, [string]$code) {
        if (-not $condition) { throw "EXTENDED_${phaseCode}_$code" }
    }
    function Get($item, [string]$name) {
        $property = if ($item -is [Collections.IDictionary]) {
            if ($item.Contains($name)) { $item[$name] } else { $null }
        } else { $item.PSObject.Properties[$name]?.Value }
        if ($null -eq $property) { throw "EXTENDED_${phaseCode}_MISSING_$($name.ToUpperInvariant())" }
        return $property
    }
    function Nullable($item, [string]$name) {
        if ($item -is [Collections.IDictionary]) {
            Need ($item.Contains($name)) "MISSING_$name"
            return $item[$name]
        }
        $property = $item.PSObject.Properties[$name]
        Need ($null -ne $property) "MISSING_$name"
        return $property.Value
    }
    function Digest([string]$text) {
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
    }
    function Row($source, [int]$index) {
        $rows = @(Get $source 'screen_rows')
        Need ($index -ge 0 -and $index -lt $rows.Count) 'ROW_RANGE'
        return $rows[$index]
    }
    function Cell($source, [int]$row, [int]$column) {
        $cells = @(Get (Row $source $row) 'cells')
        Need ($column -ge 0 -and $column -lt $cells.Count) 'CELL_RANGE'
        $cell = $cells[$column]
        Need ([int](Get $cell 'column') -eq $column) 'CELL_ORDER'
        return $cell
    }
    function Glyph($source, [int]$row, [int]$column, [string]$character) {
        $cell = Cell $source $row $column
        Need (-not [bool](Get $cell 'wide_continuation')) 'CONTINUATION'
        Need ([int](Get $cell 'display_width') -eq 1) 'WIDTH'
        Need ([string](Get $cell 'grapheme_sha256') -ceq (Digest $character)) 'GLYPH'
        return $cell
    }
    function WideGlyph($source, [int]$row, [int]$column, [string]$character) {
        $cell = Cell $source $row $column
        Need (-not [bool](Get $cell 'wide_continuation') -and
            [int](Get $cell 'display_width') -eq 2 -and
            [string](Get $cell 'grapheme_sha256') -ceq (Digest $character)) 'WIDE_GLYPH'
        Need ([bool](Get (Cell $source $row ($column + 1)) 'wide_continuation')) 'WIDE_CONTINUATION'
        return $cell
    }
    function TextAt($source, [int]$row, [int]$column, [string]$value) {
        for ($i = 0; $i -lt $value.Length; $i++) {
            $null = Glyph $source $row ($column + $i) ([string]$value[$i])
        }
    }
    function Cursor($source) {
        $cursor = Get $source 'cursor'
        return @([int](Get $cursor 'row'), [int](Get $cursor 'column'))
    }
    function CellState($source, [int]$row, [int]$column) {
        $cell = Cell $source $row $column
        $fields = @('grapheme_sha256', 'display_width', 'wide_continuation', 'rendition_id', 'protected')
        return ($fields | ForEach-Object { [string](Get $cell $_) }) -join '|'
    }
    function SameObservedCells($left, $right, [int]$exceptRow = -1, [int]$exceptColumn = -1) {
        $leftRows = @(Get $left 'screen_rows')
        $rightRows = @(Get $right 'screen_rows')
        Need ($leftRows.Count -eq $rightRows.Count) 'ROW_COUNT_CHANGED'
        for ($row = 0; $row -lt $leftRows.Count; $row++) {
            foreach ($field in @('line_rendition', 'meaningful_end', 'soft_wrapped')) {
                Need ([string](Get $leftRows[$row] $field) -ceq [string](Get $rightRows[$row] $field)) 'ROW_METADATA_CHANGED'
            }
            $leftCells = @(Get $leftRows[$row] 'cells')
            $rightCells = @(Get $rightRows[$row] 'cells')
            Need ($leftCells.Count -eq $rightCells.Count) 'COLUMN_COUNT_CHANGED'
            for ($column = 0; $column -lt $leftCells.Count; $column++) {
                if ($row -eq $exceptRow -and $column -eq $exceptColumn) { continue }
                Need ((CellState $left $row $column) -ceq (CellState $right $row $column)) 'CELL_CHANGED'
            }
        }
    }
    function Rendition($source, $cell) {
        $id = [int](Get $cell 'rendition_id')
        $matching = @((Get $source 'renditions') | Where-Object { [int](Get $_ 'id') -eq $id })
        Need ($matching.Count -eq 1) 'RENDITION_MISSING'
        return $matching[0]
    }

    Need ([int](Get $Probe 'format_version') -eq 5) 'FORMAT_VERSION'
    Need ([int](Get $Probe 'observed_rows') -ge 6 -and
        [int](Get $Probe 'observed_columns') -ge 20) 'PROBE_GEOMETRY'
    Need ([string]$Definition.expected_marker -ne '') 'MARKER_UNSPECIFIED'
    TextAt $Probe 0 0 ([string]$Definition.expected_marker)
    switch ($phase) {
        'c05-soft-pre' {
            TextAt $Probe 3 0 'KEEP'
            $red = Rendition $Probe (Glyph $Probe 4 0 'R')
            Need ([bool](Get $red 'faint') -and [string](Get $red 'underline_style') -ceq 'CURLY') 'SETUP_STYLE'
            $foreground = Get $red 'foreground'
            Need ([string](Get $foreground 'kind') -ceq 'indexed' -and
                [int](Get $foreground 'index') -eq 1) 'SETUP_COLOR'
            $position = Cursor $Probe
            $null = Glyph $Probe $position[0] $position[1] 'Q'
        }
        'c05-soft-reset' {
            Need ($null -ne $PreviousProbe) 'PREVIOUS_REQUIRED'
            SameObservedCells $PreviousProbe $Probe
            $before = Cursor $PreviousProbe
            $after = Cursor $Probe
            Need ($before[0] -eq $after[0] -and $before[1] -eq $after[1]) 'CURSOR_MOVED'
            $null = Glyph $Probe $after[0] $after[1] 'Q'
            TextAt $Probe 3 0 'KEEP'
        }
        'c05-soft-write' {
            Need ($null -ne $PreviousProbe) 'PREVIOUS_REQUIRED'
            $position = Cursor $PreviousProbe
            $null = Glyph $PreviousProbe $position[0] $position[1] 'Q'
            SameObservedCells $PreviousProbe $Probe $position[0] $position[1]
            $cell = Glyph $Probe $position[0] $position[1] 'Z'
            $rendition = Rendition $Probe $cell
            foreach ($field in @('bold', 'faint', 'italic', 'blink', 'inverse', 'conceal', 'strikethrough', 'overline')) {
                Need (-not [bool](Get $rendition $field)) 'RESET_RENDITION'
            }
            Need ([string](Get $rendition 'underline_style') -ceq 'NONE') 'RESET_UNDERLINE'
            foreach ($field in @('foreground', 'background', 'underline_color')) {
                Need ([string](Get (Get $rendition $field) 'kind') -ceq 'default') 'RESET_COLOR'
            }
            Need (-not [bool](Get $cell 'protected')) 'RESET_PROTECTION'
            $after = Cursor $Probe
            Need ($after[0] -eq $position[0] -and $after[1] -eq $position[1] + 1) 'WRITE_CURSOR'
        }
        'c05-hindex-back' {
            TextAt $Probe 3 0 'ab cdegh'
            TextAt $Probe 4 0 '12 34578'
            $position = Cursor $Probe
            Need ($position[0] -eq 3 -and $position[1] -eq 2) 'BACK_CURSOR'
        }
        'c05-hindex-forward' {
            TextAt $Probe 3 0 'abdef gh'
            TextAt $Probe 4 0 '12456 78'
            $position = Cursor $Probe
            Need ($position[0] -eq 3 -and $position[1] -eq 5) 'FORWARD_CURSOR'
        }
        'c05-tabs-default' {
            $position = Cursor $Probe
            Need ($position[0] -eq 3 -and $position[1] -eq 16) 'DEFAULT_TAB_CURSOR'
        }
        'c05-line-single' {
            TextAt $Probe 3 0 'WIDE'
            Need ([string](Get (Row $Probe 3) 'line_rendition') -ceq 'SINGLE_WIDTH') 'LINE_NOT_SINGLE'
        }
        'c05-alignment' {
            $position = Cursor $Probe
            Need ($position[0] -eq 0 -and $position[1] -eq 0) 'ALIGNMENT_CURSOR'
            $markerLength = ([string]$Definition.expected_marker).Length
            for ($row = 0; $row -lt 6; $row++) {
                Need ([string](Get (Row $Probe $row) 'line_rendition') -ceq 'SINGLE_WIDTH') 'ALIGNMENT_LINE'
                for ($column = $(if ($row -eq 0) { $markerLength } else { 0 }); $column -lt 20; $column++) {
                    $null = Glyph $Probe $row $column 'E'
                }
            }
        }
        'c03-split-pre' {
            Need ([string](Get $Probe 'width_profile') -ceq 'UNICODE_16') 'WIDTH_PROFILE'
            $null = Glyph $Probe 3 0 ([string][char]0x0301)
            $null = Glyph $Probe 3 1 'Z'
            $null = Glyph $Probe 3 3 '©'
            $null = Glyph $Probe 3 4 ' '
        }
        'c03-split-join' {
            Need ($null -ne $PreviousProbe) 'PREVIOUS_REQUIRED'
            $null = Glyph $PreviousProbe 3 3 '©'
            $null = WideGlyph $Probe 3 3 "©$([char]0xfe0f)"
            $null = Glyph $Probe 3 5 'X'
            $null = Glyph $Probe 3 0 ([string][char]0x0301)
            $null = Glyph $Probe 3 1 'Z'
        }
        'c04-crop-restore' {
            Need ([string](Get (Row $Probe 3) 'line_rendition') -ceq 'SINGLE_WIDTH') 'LINE_RENDITION'
            TextAt $Probe 3 0 'ABCDEFGHIJ'
            $cropColumn = [int][Math]::Floor([int](Get $Probe 'columns') / 2)
            $null = Glyph $Probe 3 $cropColumn ' '
            $null = Glyph $Probe 3 ($cropColumn + 1) ' '
        }
        'c04-scroll' {
            Need ([string](Get (Row $Probe 2) 'line_rendition') -ceq 'DOUBLE_WIDTH') 'SCROLLED_RENDITION'
            TextAt $Probe 2 0 'WIDE'
            Need ([string](Get (Row $Probe 3) 'line_rendition') -ceq 'SINGLE_WIDTH') 'VACATED_RENDITION'
        }
        'c06-stack' {
            $checks = @(
                @('A', 0x010203, 0x040506, 'CURLY'),
                @('B', 0x070809, 0x0a0b0c, 'DOTTED'),
                @('D', 0x070809, 0x0a0b0c, 'DOTTED'),
                @('E', 0x010203, 0x040506, 'CURLY')
            )
            foreach ($entry in $checks) {
                $column = switch ($entry[0]) { 'A' { 0 }; 'B' { 1 }; 'D' { 3 }; 'E' { 4 } }
                $rendition = Rendition $Probe (Glyph $Probe 3 $column $entry[0])
                Need ([string](Get $rendition 'underline_style') -ceq $entry[3]) 'UNDERLINE_STYLE'
                foreach ($field in @('foreground', 'underline_color')) {
                    $color = Get $rendition $field
                    $expected = if ($field -ceq 'foreground') { $entry[1] } else { $entry[2] }
                    Need ([string](Get $color 'kind') -ceq 'rgb' -and
                        [int](Get $color 'rgb') -eq $expected) "${field}_RGB"
                }
            }
            $default = Rendition $Probe (Glyph $Probe 3 2 'C')
            Need ([string](Get $default 'underline_style') -ceq 'NONE' -and
                [string](Get (Get $default 'foreground') 'kind') -ceq 'default' -and
                [string](Get (Get $default 'underline_color') 'kind') -ceq 'default') 'RESET_STYLE'
        }
        'c09-page-three' {
            TextAt $Probe 3 0 'THREE'
            Need ($null -eq $PreviousProbe -or [string](Get $Probe 'active_screen') -ceq
                [string](Get $PreviousProbe 'active_screen')) 'SCREEN_CHANGED'
        }
        'c09-page-one' {
            Need ($null -ne $PreviousProbe) 'PREVIOUS_REQUIRED'
            TextAt $PreviousProbe 3 0 'THREE'
            TextAt $Probe 3 0 'ONE'
        }
        'c10-utf8' {
            Need ([string](Get $Probe 'width_profile') -ceq 'UNICODE_16') 'WIDTH_PROFILE'
            $null = Glyph $Probe 3 0 'é'
            $null = WideGlyph $Probe 3 1 '中'
        }
        'c10-legacy-return' {
            Need ($null -ne $PreviousProbe) 'PREVIOUS_REQUIRED'
            $null = WideGlyph $PreviousProbe 3 1 '中'
            $null = Glyph $Probe 3 0 ([string][char]0x0104)
            $null = Glyph $Probe 3 1 'é'
        }
        'c11-replace-delete' {
            TextAt $Probe 3 0 'OLD'
            TextAt $Probe 4 0 'NEW'
            $null = Glyph $Probe 5 0 'X'
            $null = Glyph $Probe 5 1 ' '
        }
        'c12-overwrite' {
            $glyphs = @(Get $Probe 'glyphs')
            $resources = @(Get $Probe 'resources')
            Need ([int](Get $Probe 'glyph_count') -eq 2 -and $glyphs.Count -eq 2) 'GLYPH_COUNT'
            $ids = @()
            foreach ($column in @(0, 2)) {
                $draw = @($glyphs | Where-Object {
                    [int](Get $_ 'row') -eq 3 -and [int](Get $_ 'column') -eq $column
                })
                Need ($draw.Count -eq 1) 'GLYPH_POSITION'
                $id = [string](Get $draw[0] 'resource_id')
                $resource = @($resources | Where-Object { [string](Get $_ 'id') -ceq $id })
                Need ($resource.Count -eq 1 -and [string](Get $resource[0] 'format') -ceq 'A8') 'GLYPH_RESOURCE'
                Need ([int](Get $resource[0] 'pixel_bytes') -eq
                    [int](Get $resource[0] 'width') * [int](Get $resource[0] 'height')) 'GLYPH_DIMENSIONS'
                $ids += $id
            }
            Need ($ids[0] -cne $ids[1]) 'OVERWRITE_REUSED_RESOURCE'
            $old = @($resources | Where-Object { [string](Get $_ 'id') -ceq $ids[0] })[0]
            $new = @($resources | Where-Object { [string](Get $_ 'id') -ceq $ids[1] })[0]
            Need ([string](Get $old 'pixels_sha256') -cne [string](Get $new 'pixels_sha256')) 'OVERWRITE_SAME_PIXELS'
        }
        'c12-erase' {
            Need ($null -ne $PreviousProbe) 'PREVIOUS_REQUIRED'
            Need ([int](Get $PreviousProbe 'glyph_count') -eq 2) 'PREVIOUS_GLYPHS'
            Need ([int](Get $Probe 'glyph_count') -eq 0 -and
                @(Nullable $Probe 'glyphs').Count -eq 0 -and @(Nullable $Probe 'resources').Count -eq 0) 'ERASE_RETAINED_GLYPH'
        }
        'c13-two-colors' {
            Need ([int](Get $Probe 'image_count') -eq 1) 'IMAGE_COUNT'
            $images = @(Get $Probe 'images')
            Need ($images.Count -eq 1 -and [string](Get $images[0] 'protocol') -ceq 'SIXEL') 'IMAGE_PROTOCOL'
            $id = [string](Get $images[0] 'resource_id')
            $resource = @((Get $Probe 'resources') | Where-Object { [string](Get $_ 'id') -ceq $id })
            Need ($resource.Count -eq 1 -and [string](Get $resource[0] 'format') -ceq 'RGBA8888' -and
                [int](Get $resource[0] 'width') -eq 2 -and [int](Get $resource[0] 'height') -eq 6 -and
                [int](Get $resource[0] 'pixel_bytes') -eq 48) 'IMAGE_DIMENSIONS'
            $pixels = [byte[]]::new(48)
            for ($row = 0; $row -lt 6; $row++) {
                $at = $row * 8
                $pixels[$at] = 255; $pixels[$at + 3] = 255
                $pixels[$at + 5] = 255; $pixels[$at + 7] = 255
            }
            Need ([string](Get $resource[0] 'pixels_sha256') -ceq
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pixels)).ToLowerInvariant()) 'IMAGE_PIXELS'
        }
        'c13-cancel' {
            TextAt $Probe 3 0 'CANCEL'
            Need ([int](Get $Probe 'image_count') -eq 0 -and @(Nullable $Probe 'images').Count -eq 0 -and
                @(Nullable $Probe 'resources').Count -eq 0) 'CANCEL_PUBLISHED_IMAGE'
        }
        'c14-colors-set' {
            Need ([string](Get $Probe 'title_sha256') -ceq (Digest 'TermLink extended')) 'TITLE'
            $colors = Get $Probe 'colors'
            Need ([int](Get $colors 'selection_background_rgb') -eq 0x123456 -and
                [int](Get $colors 'foreground_alias') -eq 2 -and
                [int](Get $colors 'background_alias') -eq 3) 'COLORS'
        }
        'c14-colors-reset' {
            Need ($null -ne $PreviousProbe) 'PREVIOUS_REQUIRED'
            Need ([int](Get (Get $PreviousProbe 'colors') 'selection_background_rgb') -eq 0x123456) 'PREVIOUS_COLOR'
            Need ([string](Get $Probe 'title_sha256') -ceq (Digest 'TermLink extended')) 'TITLE'
            $colors = Get $Probe 'colors'
            Need ($null -eq (Nullable $colors 'selection_background_rgb') -and
                $null -eq (Nullable $colors 'foreground_alias') -and
                $null -eq (Nullable $colors 'background_alias')) 'COLORS_NOT_RESET'
        }
        default { throw "EXTENDED_UNKNOWN_PHASE_$phase" }
    }
}
