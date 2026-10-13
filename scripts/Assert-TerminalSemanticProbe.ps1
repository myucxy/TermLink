# Assertions over TerminalSemanticProbe.summarize() JSON. This file never starts a session.
function Test-TerminalSemanticMarker {
    param([Parameter(Mandatory)][object]$Probe,
        [Parameter(Mandatory)][ValidateLength(1, 60)][string]$Marker)
    $hashes = @($Marker.ToCharArray() | ForEach-Object {
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes([string]$_))).ToLowerInvariant()
    })
    foreach ($row in @($Probe.screen_rows)) {
        $cells = @($row.cells)
        for ($start = 0; $start -le $cells.Count - $hashes.Count; $start++) {
            $matches = $true
            for ($index = 0; $index -lt $hashes.Count; $index++) {
                $cell = $cells[$start + $index]
                if ($cell.wide_continuation -or $cell.display_width -ne 1 -or
                    $cell.column -ne $start + $index -or $cell.grapheme_sha256 -cne $hashes[$index]) {
                    $matches = $false
                    break
                }
            }
            if ($matches) { return $true }
        }
    }
    return $false
}

function Assert-TerminalRestoredSemanticProbe {
    param([Parameter(Mandatory)][object]$Before, [Parameter(Mandatory)][object]$After)
    # Source/repaint revisions can advance while a new renderer installs identical state.
    foreach ($field in @('format_version', 'width_profile', 'columns', 'rows', 'observed_columns',
        'observed_rows', 'active_screen', 'reverse_video', 'title_sha256', 'cursor', 'screen_rows',
        'renditions', 'colors', 'image_count', 'glyph_count', 'images', 'glyphs', 'resources')) {
        $old = $Before.PSObject.Properties[$field]
        $new = $After.PSObject.Properties[$field]
        if ($null -eq $old -or $null -eq $new -or
            ($old.Value | ConvertTo-Json -Depth 12 -Compress) -cne
            ($new.Value | ConvertTo-Json -Depth 12 -Compress)) {
            throw "SNAPSHOT_RESTORE_SEMANTIC_$($field.ToUpperInvariant())"
        }
    }
}

function Assert-TerminalSemanticProbe {
    param(
        [Parameter(Mandatory)][string] $Phase,
        [Parameter(Mandatory)][object] $Probe
    )

    $phaseCode = [regex]::Replace($Phase.ToUpperInvariant(), '[^A-Z0-9]+', '_').Trim('_')
    if ($phaseCode.Length -gt 16) { $phaseCode = $phaseCode.Substring(0, 16) }
    if ($phaseCode.Length -eq 0) { $phaseCode = 'UNKNOWN' }

    function Value($item, [string] $name) {
        $nameCode = $name.ToUpperInvariant()
        if ($null -eq $item) { throw "SEMANTIC_${phaseCode}_MISSING_$nameCode" }
        if ($item -is [System.Collections.IDictionary]) {
            if (-not $item.Contains($name)) { throw "SEMANTIC_${phaseCode}_MISSING_$nameCode" }
            return $item[$name]
        }
        $property = $item.PSObject.Properties[$name]
        if ($null -eq $property) { throw "SEMANTIC_${phaseCode}_MISSING_$nameCode" }
        return $property.Value
    }
    function Require([bool] $condition, [string] $code) {
        if (-not $condition) { throw "SEMANTIC_${phaseCode}_$code" }
    }
    function Digest([string] $text) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    function Row([int] $index) {
        $rows = @(Value $Probe 'screen_rows')
        Require ($index -lt $rows.Count) 'ROW_OUTSIDE_PROBE'
        return $rows[$index]
    }
    function Cell([int] $rowIndex, [int] $columnIndex) {
        $cells = @(Value (Row $rowIndex) 'cells')
        Require ($columnIndex -lt $cells.Count) 'COLUMN_OUTSIDE_PROBE'
        $cell = $cells[$columnIndex]
        Require ([int](Value $cell 'column') -eq $columnIndex) 'CELL_ORDER'
        return $cell
    }
    function Glyph([int] $rowIndex, [int] $columnIndex, [string] $text, [int] $width) {
        $cell = Cell $rowIndex $columnIndex
        Require (-not [bool](Value $cell 'wide_continuation')) 'UNEXPECTED_CONTINUATION'
        Require ([int](Value $cell 'display_width') -eq $width) 'WIDTH'
        Require ([string](Value $cell 'grapheme_sha256') -ceq (Digest $text)) 'GLYPH'
        if ($width -eq 2) {
            Require ([bool](Value (Cell $rowIndex ($columnIndex + 1)) 'wide_continuation')) 'MISSING_CONTINUATION'
        }
        return $cell
    }

    Require ([int](Value $Probe 'format_version') -eq 5) 'FORMAT_VERSION'
    Require ([int](Value $Probe 'observed_rows') -ge 6) 'ROWS'
    Require ([int](Value $Probe 'observed_columns') -ge 20) 'COLUMNS'
    $phaseName = switch ($Phase.ToUpperInvariant()) {
        'C03' { 'unicode' }; 'C04' { 'line' }; 'C06' { 'sgr' }; 'C09' { 'pages' }
        'C10' { 'charset' }; 'C11' { 'macro' }; 'C12' { 'drcs' }; 'C13' { 'sixel' }
        'C14' { 'colors' }; default { $Phase.ToLowerInvariant() }
    }
    switch ($phaseName) {
        'unicode' {
            Require ([string](Value $Probe 'width_profile') -ceq 'UNICODE_16') 'WIDTH_PROFILE'
            $expected = @(
                @(0, '中', 2), @(2, '文', 2), @(4, ' ', 1),
                @(5, '🇨🇳', 2), @(7, ' ', 1), @(8, "e$([char]0x0301)", 1),
                @(9, ' ', 1), @(10, "👩$([char]0x200d)💻", 2),
                @(12, ' ', 1), @(13, "क्$([char]0x200d)ष", 1)
            )
            foreach ($entry in $expected) { $null = Glyph 3 $entry[0] $entry[1] $entry[2] }
        }
        'line' {
            foreach ($entry in @(@(3, 'DOUBLE_WIDTH', 'WIDE'), @(4, 'DOUBLE_HEIGHT_TOP', 'TOP'), @(5, 'DOUBLE_HEIGHT_BOTTOM', 'BOTTOM'))) {
                Require ([string](Value (Row $entry[0]) 'line_rendition') -ceq $entry[1]) 'LINE_RENDITION'
                for ($i = 0; $i -lt $entry[2].Length; $i++) {
                    $null = Glyph $entry[0] $i ([string]$entry[2][$i]) 1
                }
            }
        }
        'sgr' {
            for ($i = 0; $i -lt 5; $i++) {
                $cell = Glyph 3 $i ([string]'STACK'[$i]) 1
                $id = [int](Value $cell 'rendition_id')
                $renditions = @(Value $Probe 'renditions')
                $rendition = @($renditions | Where-Object { [int](Value $_ 'id') -eq $id })
                Require ($rendition.Count -eq 1) 'RENDITION_MISSING'
                Require ([bool](Value $rendition[0] 'faint')) 'FAINT'
                Require ([string](Value $rendition[0] 'underline_style') -ceq 'CURLY') 'CURLY'
                $foreground = Value $rendition[0] 'foreground'
                Require ([string](Value $foreground 'kind') -ceq 'indexed' -and
                    [int](Value $foreground 'index') -eq 1) 'RED_FOREGROUND'
            }
        }
        'pages' {
            # The vector writes row 4 so the row 1/2 live markers cannot replace it.
            for ($i = 0; $i -lt 6; $i++) { $null = Glyph 3 $i ([string]'HIDDEN'[$i]) 1 }
        }
        'charset' {
            # DOCS Latin-1 mode + DECAUPSS B + G1 96-character designation + SO maps 0x21 to U+0104.
            Require ([string](Value $Probe 'width_profile') -ceq 'UNICODE_16') 'WIDTH_PROFILE'
            $null = Glyph 3 0 ([string][char]0x0104) 1
            $null = Glyph 3 1 ' ' 1
        }
        'macro' {
            $null = Glyph 3 0 'O' 1
            $null = Glyph 3 1 'K' 1
        }
        'drcs' {
            $glyphs = @(Value $Probe 'glyphs')
            $resources = @(Value $Probe 'resources')
            Require ([int](Value $Probe 'glyph_count') -gt 0 -and $glyphs.Count -gt 0) 'GLYPH_COUNT'
            foreach ($glyph in $glyphs) {
                $resource = @($resources | Where-Object { [string](Value $_ 'id') -ceq [string](Value $glyph 'resource_id') })
                Require ($resource.Count -eq 1) 'GLYPH_RESOURCE'
                Require ([string](Value $resource[0] 'format') -ceq 'A8') 'ALPHA_FORMAT'
                $width = [int](Value $resource[0] 'width')
                $height = [int](Value $resource[0] 'height')
                Require ($width -gt 0 -and $height -gt 0 -and
                    [int](Value $resource[0] 'pixel_bytes') -eq $width * $height) 'ALPHA_PIXELS'
                Require ([string](Value $resource[0] 'pixels_sha256') -cmatch '^[0-9a-f]{64}$') 'ALPHA_DIGEST'
            }
        }
        'sixel' {
            $images = @(Value $Probe 'images')
            Require ([int](Value $Probe 'image_count') -eq 1 -and $images.Count -eq 1) 'IMAGE_COUNT'
            $image = $images[0]
            Require ([string](Value $image 'protocol') -ceq 'SIXEL') 'PROTOCOL'
            $resource = @((Value $Probe 'resources') | Where-Object {
                [string](Value $_ 'id') -ceq [string](Value $image 'resource_id')
            })
            Require ($resource.Count -eq 1) 'IMAGE_RESOURCE'
            Require ([string](Value $resource[0] 'format') -ceq 'RGBA8888' -and
                [int](Value $resource[0] 'width') -eq 1 -and
                [int](Value $resource[0] 'height') -eq 6 -and
                [int](Value $resource[0] 'pixel_bytes') -eq 24) 'IMAGE_DIMENSIONS'
            $pixels = [byte[]]::new(24)
            for ($i = 0; $i -lt 24; $i += 4) { $pixels[$i] = 255; $pixels[$i + 3] = 255 }
            $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pixels)).ToLowerInvariant()
            Require ([string](Value $resource[0] 'pixels_sha256') -ceq $expectedHash) 'RED_PIXELS'
        }
        'colors' {
            $colors = Value $Probe 'colors'
            Require ([int](Value $colors 'selection_background_rgb') -eq 0x123456) 'SELECTION_BACKGROUND'
            Require ([int](Value $colors 'foreground_alias') -eq 2 -and
                [int](Value $colors 'background_alias') -eq 3) 'ALIASES'
            Require ($null -eq (Value $colors 'frame_foreground_alias') -and
                $null -eq (Value $colors 'frame_background_alias')) 'FRAME_ALIASES'
            Require ([string](Value $Probe 'title_sha256') -ceq (Digest 'TermLink smoke')) 'TITLE'
        }
        default { throw "SEMANTIC_UNKNOWN_PHASE_$phaseCode" }
    }
}
