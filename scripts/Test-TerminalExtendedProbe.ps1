$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Assert-TerminalExtendedProbe.ps1')

function Hash([string]$text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
}
function New-Probe {
    $rows = @()
    for ($row = 0; $row -lt 6; $row++) {
        $cells = @()
        for ($column = 0; $column -lt 20; $column++) {
            $cells += @{ column = $column; grapheme_sha256 = (Hash ' '); display_width = 1;
                rendition_id = 0; wide_continuation = $false; protected = $false }
        }
        $rows += @{ row = $row; line_rendition = 'SINGLE_WIDTH'; meaningful_end = 20;
            soft_wrapped = $false; cells = $cells }
    }
    return @{
        format_version = 5; columns = 20; observed_rows = 6; observed_columns = 20
        width_profile = 'UNICODE_16'; active_screen = 'MAIN'; title_sha256 = Hash ''
        cursor = @{ row = 0; column = 0 }
        screen_rows = $rows
        colors = @{ selection_background_rgb = $null; foreground_alias = $null; background_alias = $null }
        image_count = 0; images = @(); glyph_count = 0; glyphs = @(); resources = @()
        renditions = @(
            @{ id = 0; foreground = @{ kind = 'default' }; background = @{ kind = 'default' };
                underline_color = @{ kind = 'default' }; underline_style = 'NONE'; bold = $false;
                faint = $false; italic = $false; blink = $false; inverse = $false;
                conceal = $false; strikethrough = $false; overline = $false },
            @{ id = 1; foreground = @{ kind = 'indexed'; index = 1 }; background = @{ kind = 'default' };
                underline_color = @{ kind = 'default' }; underline_style = 'CURLY'; bold = $false;
                faint = $true; italic = $false; blink = $false; inverse = $false;
                conceal = $false; strikethrough = $false; overline = $false }
        )
    }
}
function Put-Text($probe, [int]$row, [int]$column, [string]$text, [int]$rendition = 0) {
    for ($offset = 0; $offset -lt $text.Length; $offset++) {
        $probe.screen_rows[$row].cells[$column + $offset] = @{
            column = $column + $offset
            grapheme_sha256 = Hash ([string]$text[$offset])
            display_width = 1; rendition_id = $rendition
            wide_continuation = $false; protected = $false
        }
    }
}
function Put-Glyph($probe, [int]$row, [int]$column, [string]$text, [int]$width = 1,
    [int]$rendition = 0) {
    $probe.screen_rows[$row].cells[$column] = @{
        column = $column; grapheme_sha256 = Hash $text; display_width = $width
        rendition_id = $rendition; wide_continuation = $false; protected = $false
    }
    if ($width -eq 2) {
        $probe.screen_rows[$row].cells[$column + 1] = @{
            column = $column + 1; rendition_id = $rendition
            wide_continuation = $true; protected = $false
        }
    }
}
function Copy-Probe($probe) { $probe | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable }
function Expect-Rejection($definition, $probe, $previous = $null) {
    $failed = $false
    try { Assert-TerminalExtendedProbe -Definition $definition -Probe $probe -PreviousProbe $previous }
    catch { $failed = $_.Exception.Message -like 'EXTENDED_*' }
    if (-not $failed) { throw "Expected rejection of $($definition.phase) mutation" }
}

$definitions = @(Get-TerminalExtendedPhaseDefinitions)
$expectedNames = @('c05-soft-pre', 'c05-soft-reset', 'c05-soft-write',
    'c05-hindex-back', 'c05-hindex-forward', 'c05-tabs-default',
    'c05-line-single', 'c05-alignment',
    'c03-split-pre', 'c03-split-join', 'c04-crop-restore', 'c04-scroll',
    'c06-stack', 'c09-page-three', 'c09-page-one', 'c10-utf8', 'c10-legacy-return',
    'c11-replace-delete', 'c12-overwrite', 'c12-erase', 'c13-two-colors',
    'c13-cancel', 'c14-colors-set', 'c14-colors-reset')
if ($definitions.Count -ne $expectedNames.Count -or
    (@($definitions.phase) -join ',') -cne ($expectedNames -join ',')) {
    throw 'Extended phase list changed unexpectedly'
}
for ($index = 0; $index -lt $definitions.Count; $index++) {
    $definition = $definitions[$index]
    $bytes = [Convert]::FromBase64String([string]$definition.sequence_b64)
    if ($bytes.Length -eq 0 -or $bytes.Length -gt 512) { throw "Bad vector length: $($definition.phase)" }
    if ([Text.Encoding]::UTF8.GetString($bytes) -notmatch [regex]::Escape([string]$definition.expected_marker) -and
        $definition.reset_before) {
        throw "Missing vector marker: $($definition.phase)"
    }
    if ($index -lt 8 -and (($index -eq 0 -or $index -ge 3) -ne [bool]$definition.reset_before)) {
        throw "Wrong reset boundary: $($definition.phase)"
    }
    if ($index -ge 8 -and $definition.phase -notmatch '^c(?:03|04|06|09|10|11|12|13|14)-') {
        throw "Extended phase has no acceptance group: $($definition.phase)"
    }
}
$esc = [string][char]27
$vectors = @($definitions | ForEach-Object { [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String([string]$_.sequence_b64)) })
foreach ($pair in @(
    @(1, $esc + '[!p'), @(3, $esc + '6'), @(4, $esc + '9'),
    @(5, $esc + '[?5W'), @(6, $esc + '#5'), @(7, $esc + '#8')
)) {
    if (-not $vectors[[int]$pair[0]].Contains([string]$pair[1])) {
        throw "Missing core protocol vector $($pair[0])"
    }
}

$pre = New-Probe
Put-Text $pre 0 0 $definitions[0].expected_marker
Put-Text $pre 3 0 'KEEP'
Put-Text $pre 4 0 'R' 1
Put-Text $pre 2 12 'Q' 1
$pre.cursor.row = 2; $pre.cursor.column = 12
Assert-TerminalExtendedProbe $definitions[0] $pre
$badPre = Copy-Probe $pre
Put-Text $badPre 4 0 'X' 1
Expect-Rejection $definitions[0] $badPre

$reset = Copy-Probe $pre
Assert-TerminalExtendedProbe $definitions[1] $reset $pre
Expect-Rejection $definitions[1] $reset
$badReset = Copy-Probe $reset
$badReset.cursor.column = 11
Expect-Rejection $definitions[1] $badReset $pre
$badResetCell = Copy-Probe $reset
Put-Text $badResetCell 3 1 'X'
Expect-Rejection $definitions[1] $badResetCell $pre

$write = Copy-Probe $reset
Put-Text $write 2 12 'Z'
$write.cursor.column = 13
Assert-TerminalExtendedProbe $definitions[2] $write $reset
Expect-Rejection $definitions[2] $write
$badWrite = Copy-Probe $write
$badWrite.screen_rows[2].cells[12].rendition_id = 1
Expect-Rejection $definitions[2] $badWrite $reset
$badInsert = Copy-Probe $write
Put-Text $badInsert 2 13 'Q' 1
Expect-Rejection $definitions[2] $badInsert $reset

$back = New-Probe
Put-Text $back 0 0 $definitions[3].expected_marker
Put-Text $back 3 0 'ab cdegh'
Put-Text $back 4 0 '12 34578'
$back.cursor.row = 3; $back.cursor.column = 2
Assert-TerminalExtendedProbe $definitions[3] $back
$badBack = Copy-Probe $back
Put-Text $badBack 4 3 'X'
Expect-Rejection $definitions[3] $badBack

$forward = New-Probe
Put-Text $forward 0 0 $definitions[4].expected_marker
Put-Text $forward 3 0 'abdef gh'
Put-Text $forward 4 0 '12456 78'
$forward.cursor.row = 3; $forward.cursor.column = 5
Assert-TerminalExtendedProbe $definitions[4] $forward
$badForward = Copy-Probe $forward
$badForward.cursor.column = 6
Expect-Rejection $definitions[4] $badForward

$tabs = New-Probe
Put-Text $tabs 0 0 $definitions[5].expected_marker
$tabs.cursor.row = 3; $tabs.cursor.column = 16
Assert-TerminalExtendedProbe $definitions[5] $tabs
$badTabs = Copy-Probe $tabs
$badTabs.cursor.column = 3
Expect-Rejection $definitions[5] $badTabs

$line = New-Probe
Put-Text $line 0 0 $definitions[6].expected_marker
Put-Text $line 3 0 'WIDE'
Assert-TerminalExtendedProbe $definitions[6] $line
$badLine = Copy-Probe $line
$badLine.screen_rows[3].line_rendition = 'DOUBLE_WIDTH'
Expect-Rejection $definitions[6] $badLine

$align = New-Probe
for ($row = 0; $row -lt 6; $row++) { Put-Text $align $row 0 ('E' * 20) }
Put-Text $align 0 0 $definitions[7].expected_marker
Assert-TerminalExtendedProbe $definitions[7] $align
$badAlign = Copy-Probe $align
Put-Text $badAlign 5 19 'X'
Expect-Rejection $definitions[7] $badAlign

$previous = @{}
foreach ($definition in @($definitions | Select-Object -Skip 8)) {
    $probe = New-Probe
    Put-Text $probe 0 0 $definition.expected_marker
    $before = $null
    switch ($definition.phase) {
        'c03-split-pre' {
            Put-Glyph $probe 3 0 ([string][char]0x0301)
            Put-Text $probe 3 1 'Z'
            Put-Glyph $probe 3 3 '©'
            $badField = { param($item) Put-Glyph $item 3 0 "Z$([char]0x0301)" }
            $previous['c03'] = $probe
        }
        'c03-split-join' {
            $before = $previous['c03']
            Put-Glyph $probe 3 0 ([string][char]0x0301)
            Put-Text $probe 3 1 'Z'
            Put-Glyph $probe 3 3 "©$([char]0xfe0f)" 2
            Put-Text $probe 3 5 'X'
            $badField = { param($item) Put-Text $item 3 5 'Y' }
        }
        'c04-crop-restore' {
            Put-Text $probe 3 0 'ABCDEFGHIJ'
            $badField = { param($item) Put-Text $item 3 10 'K' }
        }
        'c04-scroll' {
            $probe.screen_rows[2].line_rendition = 'DOUBLE_WIDTH'
            Put-Text $probe 2 0 'WIDE'
            $badField = { param($item) $item.screen_rows[2].line_rendition = 'SINGLE_WIDTH' }
        }
        'c06-stack' {
            $probe.renditions += @{
                id = 2; foreground = @{ kind = 'rgb'; rgb = 0x010203 }
                background = @{ kind = 'default' }; underline_color = @{ kind = 'rgb'; rgb = 0x040506 }
                underline_style = 'CURLY'; bold = $false; faint = $false; italic = $false
                blink = $false; inverse = $false; conceal = $false; strikethrough = $false; overline = $false
            }
            $probe.renditions += @{
                id = 3; foreground = @{ kind = 'rgb'; rgb = 0x070809 }
                background = @{ kind = 'default' }; underline_color = @{ kind = 'rgb'; rgb = 0x0a0b0c }
                underline_style = 'DOTTED'; bold = $false; faint = $false; italic = $false
                blink = $false; inverse = $false; conceal = $false; strikethrough = $false; overline = $false
            }
            Put-Text $probe 3 0 'A' 2
            Put-Text $probe 3 1 'B' 3
            Put-Text $probe 3 2 'C'
            Put-Text $probe 3 3 'D' 3
            Put-Text $probe 3 4 'E' 2
            $badField = { param($item) $item.renditions[3].underline_color.rgb = 0x0a0b0d }
        }
        'c09-page-three' {
            Put-Text $probe 3 0 'THREE'
            $badField = { param($item) Put-Text $item 3 0 'WRONG' }
            $previous['c09'] = $probe
        }
        'c09-page-one' {
            $before = $previous['c09']
            Put-Text $probe 3 0 'ONE'
            $badField = { param($item) Put-Text $item 3 0 'TWO' }
        }
        'c10-utf8' {
            Put-Glyph $probe 3 0 'é'
            Put-Glyph $probe 3 1 '中' 2
            $badField = { param($item) Put-Glyph $item 3 0 'Ã' }
            $previous['c10'] = $probe
        }
        'c10-legacy-return' {
            $before = $previous['c10']
            Put-Glyph $probe 3 0 ([string][char]0x0104)
            Put-Glyph $probe 3 1 'é'
            $badField = { param($item) Put-Glyph $item 3 1 'Ã' }
        }
        'c11-replace-delete' {
            Put-Text $probe 3 0 'OLD'
            Put-Text $probe 4 0 'NEW'
            Put-Text $probe 5 0 'X'
            $badField = { param($item) Put-Text $item 4 0 'OLD' }
        }
        'c12-overwrite' {
            $probe.glyph_count = 2
            $probe.glyphs = @(@{ row = 3; column = 0; resource_id = '1' },
                @{ row = 3; column = 2; resource_id = '2' })
            $probe.resources = @(@{ id = '1'; format = 'A8'; width = 5; height = 8;
                    pixel_bytes = 40; pixels_sha256 = 'a' * 64 },
                @{ id = '2'; format = 'A8'; width = 5; height = 8;
                    pixel_bytes = 40; pixels_sha256 = 'b' * 64 })
            $badField = { param($item) $item.resources[1].pixels_sha256 = 'a' * 64 }
            $previous['c12'] = $probe
        }
        'c12-erase' {
            $before = $previous['c12']
            $badField = { param($item) $item.glyph_count = 1; $item.glyphs = @(@{ row = 3; column = 0; resource_id = '1' }) }
        }
        'c13-two-colors' {
            $probe.image_count = 1
            $probe.images = @(@{ protocol = 'SIXEL'; resource_id = '7' })
            $pixels = [byte[]]::new(48)
            for ($row = 0; $row -lt 6; $row++) {
                $at = $row * 8
                $pixels[$at] = 255; $pixels[$at + 3] = 255
                $pixels[$at + 5] = 255; $pixels[$at + 7] = 255
            }
            $probe.resources = @(@{ id = '7'; format = 'RGBA8888'; width = 2; height = 6;
                pixel_bytes = 48; pixels_sha256 = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($pixels)).ToLowerInvariant() })
            $badField = { param($item) $item.resources[0].pixels_sha256 = '0' * 64 }
        }
        'c13-cancel' {
            Put-Text $probe 3 0 'CANCEL'
            $badField = { param($item) $item.image_count = 1; $item.images = @(@{ protocol = 'SIXEL'; resource_id = '7' }) }
        }
        'c14-colors-set' {
            $probe.title_sha256 = Hash 'TermLink extended'
            $probe.colors.selection_background_rgb = 0x123456
            $probe.colors.foreground_alias = 2
            $probe.colors.background_alias = 3
            $badField = { param($item) $item.colors.foreground_alias = 4 }
            $previous['c14'] = $probe
        }
        'c14-colors-reset' {
            $before = $previous['c14']
            $probe.title_sha256 = Hash 'TermLink extended'
            $badField = { param($item) $item.colors.selection_background_rgb = 0x123456 }
        }
        default { throw "Unrecognized extended test phase $($definition.phase)" }
    }
    Assert-TerminalExtendedProbe $definition $probe $before
    $bad = Copy-Probe $probe
    & $badField $bad
    Expect-Rejection $definition $bad $before
    switch ($definition.phase) {
        'c03-split-join' {
            $bad = Copy-Probe $probe
            Put-Glyph $bad 3 3 '©' 2
            Expect-Rejection $definition $bad $before
        }
        'c04-crop-restore' {
            $bad = Copy-Probe $probe
            $bad.screen_rows[3].line_rendition = 'DOUBLE_WIDTH'
            Expect-Rejection $definition $bad $before
        }
        'c06-stack' {
            $bad = Copy-Probe $probe
            Put-Text $bad 3 4 'F' 2
            Expect-Rejection $definition $bad $before
            $bad = Copy-Probe $probe
            $bad.renditions[2].foreground.rgb = 0x010204
            Expect-Rejection $definition $bad $before
        }
        'c11-replace-delete' {
            $bad = Copy-Probe $probe
            Put-Text $bad 5 1 'O'
            Expect-Rejection $definition $bad $before
        }
        'c12-overwrite' {
            $bad = Copy-Probe $probe
            $bad.glyphs[1].resource_id = '1'
            Expect-Rejection $definition $bad $before
        }
        'c13-two-colors' {
            $bad = Copy-Probe $probe
            $bad.resources[0].width = 1
            Expect-Rejection $definition $bad $before
        }
        'c14-colors-set' {
            $bad = Copy-Probe $probe
            $bad.title_sha256 = Hash 'Wrong title'
            Expect-Rejection $definition $bad $before
        }
        'c14-colors-reset' {
            $bad = Copy-Probe $probe
            $bad.title_sha256 = Hash 'Wrong title'
            Expect-Rejection $definition $bad $before
        }
    }
}

$wideDefinition = Get-TerminalExtendedPhaseDefinitions -Columns 106 |
    Where-Object phase -eq 'c04-crop-restore'
$wideSequence = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($wideDefinition.sequence_b64))
if (-not $wideSequence.Contains("$([char]27)[4;54HKL")) { throw 'Crop vector ignored the real viewport width' }
$wide = New-Probe
$wide.columns = 106; $wide.observed_columns = 60
foreach ($row in $wide.screen_rows) {
    foreach ($column in 20..59) {
        $row.cells += @{ column = $column; grapheme_sha256 = Hash ' '; display_width = 1;
            rendition_id = 0; wide_continuation = $false; protected = $false }
    }
}
Put-Text $wide 0 0 $wideDefinition.expected_marker
Put-Text $wide 3 0 'ABCDEFGHIJ'
Assert-TerminalExtendedProbe $wideDefinition $wide
Put-Text $wide 3 53 'K'
Expect-Rejection $wideDefinition $wide

$drcsDefinition = $definitions | Where-Object phase -eq 'c12-overwrite'
$drcsSequence = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($drcsDefinition.sequence_b64))
if (-not $drcsSequence.Contains('{ @?????')) { throw 'DRCS overwrite needs a blank glyph definition, not an empty erase request' }

Write-Output 'Terminal extended probe assertions passed.'
