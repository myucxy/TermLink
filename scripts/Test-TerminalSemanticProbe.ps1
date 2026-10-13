$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assert-TerminalSemanticProbe.ps1')

function Hash([string] $text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
}
function New-Probe {
    $rows = @()
    for ($row = 0; $row -lt 6; $row++) {
        $cells = @()
        for ($column = 0; $column -lt 20; $column++) {
            $cells += @{ column = $column; grapheme_sha256 = (Hash ' '); display_width = 1;
                rendition_id = 0; wide_continuation = $false }
        }
        $rows += @{ row = $row; line_rendition = 'SINGLE_WIDTH'; cells = $cells }
    }
    return @{
        format_version = 5; width_profile = 'UNICODE_16'; observed_rows = 6; observed_columns = 20
        screen_rows = $rows; renditions = @(@{ id = 0; faint = $false; underline_style = 'NONE';
                foreground = @{ kind = 'default' } })
        glyph_count = 0; glyphs = @(); resources = @(); image_count = 0; images = @()
        colors = @{ selection_background_rgb = $null; foreground_alias = $null;
            background_alias = $null; frame_foreground_alias = $null; frame_background_alias = $null }
        title_sha256 = (Hash '')
    }
}
function Put-Glyph($probe, [int] $row, [int] $column, [string] $text, [int] $width = 1, [int] $rendition = 0) {
    $probe.screen_rows[$row].cells[$column] = @{ column = $column; grapheme_sha256 = (Hash $text);
        display_width = $width; rendition_id = $rendition; wide_continuation = $false }
    if ($width -eq 2) {
        $probe.screen_rows[$row].cells[$column + 1] = @{ column = ($column + 1);
            rendition_id = $rendition; wide_continuation = $true }
    }
}
function Put-Text($probe, [int] $row, [string] $text, [int] $rendition = 0) {
    for ($i = 0; $i -lt $text.Length; $i++) {
        Put-Glyph $probe $row $i ([string]$text[$i]) 1 $rendition
    }
}
function Expect-Fail([string] $phase, $probe) {
    $failed = $false
    try { Assert-TerminalSemanticProbe $phase $probe } catch { $failed = $true }
    if (-not $failed) { throw "Expected $phase to reject altered frame data" }
}

$markerProbe = New-Probe
Put-Text $markerProbe 3 'TLX_C05_SOFT'
if (-not (Test-TerminalSemanticMarker $markerProbe 'TLX_C05_SOFT') -or
    (Test-TerminalSemanticMarker $markerProbe 'TLX_C05_BACK')) { throw 'MARKER_MATCH_FAILED' }
$markerProbe.screen_rows[3].cells[4].column = 5
if (Test-TerminalSemanticMarker $markerProbe 'TLX_C05_SOFT') { throw 'MARKER_ACCEPTED_NONCONTIGUOUS_COLUMNS' }
function Expect-Code([string] $phase, $probe, [string] $expected) {
    try { Assert-TerminalSemanticProbe $phase $probe }
    catch {
        if ($_.Exception.Message -ceq $expected) { return }
        throw "Expected $expected, got $($_.Exception.Message)"
    }
    throw "Expected $expected, but $phase passed"
}

$unicode = New-Probe
foreach ($entry in @(
    @(0, '中', 2), @(2, '文', 2), @(4, ' ', 1), @(5, '🇨🇳', 2), @(7, ' ', 1),
    @(8, "e$([char]0x0301)", 1), @(9, ' ', 1), @(10, "👩$([char]0x200d)💻", 2),
    @(12, ' ', 1), @(13, "क्$([char]0x200d)ष", 1)
)) { Put-Glyph $unicode 3 $entry[0] $entry[1] $entry[2] }
Assert-TerminalSemanticProbe unicode $unicode
$unicode.width_profile = 'LEGACY_V1'
Expect-Code unicode $unicode 'SEMANTIC_UNICODE_WIDTH_PROFILE'
$unicode.width_profile = 'UNICODE_16'
$missingWidth = New-Probe
$missingWidth.Remove('width_profile')
Expect-Code unicode $missingWidth 'SEMANTIC_UNICODE_MISSING_WIDTH_PROFILE'
Expect-Code 'future/odd?' (New-Probe) 'SEMANTIC_UNKNOWN_PHASE_FUTURE_ODD'
Expect-Code ('a' * 80) (New-Probe) ('SEMANTIC_UNKNOWN_PHASE_' + ('A' * 16))
$unicode.screen_rows[3].cells[11].wide_continuation = $false
Expect-Fail unicode $unicode

$line = New-Probe
$line.screen_rows[3].line_rendition = 'DOUBLE_WIDTH'; Put-Text $line 3 'WIDE'
$line.screen_rows[4].line_rendition = 'DOUBLE_HEIGHT_TOP'; Put-Text $line 4 'TOP'
$line.screen_rows[5].line_rendition = 'DOUBLE_HEIGHT_BOTTOM'; Put-Text $line 5 'BOTTOM'
Assert-TerminalSemanticProbe line $line
$line.screen_rows[5].line_rendition = 'SINGLE_WIDTH'
Expect-Fail line $line

$sgr = New-Probe
$sgr.renditions += @{ id = 1; faint = $true; underline_style = 'CURLY'; foreground = @{ kind = 'indexed'; index = 1 } }
Put-Text $sgr 3 'STACK' 1
Assert-TerminalSemanticProbe sgr $sgr
$sgr.renditions[1].foreground.index = 2
Expect-Fail sgr $sgr

$pages = New-Probe
Put-Text $pages 3 'HIDDEN'
Assert-TerminalSemanticProbe pages $pages
$pages.screen_rows[3].cells[2].grapheme_sha256 = Hash 'X'
Expect-Fail pages $pages

$charset = New-Probe
Put-Glyph $charset 3 0 ([string][char]0x0104)
Assert-TerminalSemanticProbe charset $charset
Assert-TerminalSemanticProbe C10 $charset
$charset.screen_rows[3].cells[0].grapheme_sha256 = Hash 'A'
Expect-Fail charset $charset
$charset.screen_rows[3].cells[0].grapheme_sha256 = Hash ([string][char]0x0104)
$charset.screen_rows[3].cells[1].grapheme_sha256 = Hash 'X'
Expect-Fail charset $charset

$macro = New-Probe
Put-Text $macro 3 'OK'
Assert-TerminalSemanticProbe macro $macro
$macro.screen_rows[3].cells[1].grapheme_sha256 = Hash 'X'
Expect-Fail macro $macro

$drcs = New-Probe
$drcs.glyph_count = 1
$drcs.glyphs = @(@{ row = 3; column = 0; resource_id = '41' })
$drcs.resources = @(@{ id = '41'; format = 'A8'; width = 8; height = 10;
        pixel_bytes = 80; pixels_sha256 = ('a' * 64) })
Assert-TerminalSemanticProbe drcs $drcs
$drcs.resources[0].format = 'RGBA8888'
Expect-Fail drcs $drcs
$drcs.resources[0].format = 'ALPHA8'
Expect-Fail drcs $drcs
$drcs.resources[0].format = 'A8'
$drcs.resources[0].pixel_bytes = 79
Expect-Fail drcs $drcs

$sixel = New-Probe
$pixels = [byte[]]::new(24)
for ($i = 0; $i -lt 24; $i += 4) { $pixels[$i] = 255; $pixels[$i + 3] = 255 }
$sixel.image_count = 1
$sixel.images = @(@{ protocol = 'SIXEL'; resource_id = '42' })
$sixel.resources = @(@{ id = '42'; format = 'RGBA8888'; width = 1; height = 6;
        pixel_bytes = 24; pixels_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pixels)).ToLowerInvariant() })
Assert-TerminalSemanticProbe sixel $sixel
$sixel.resources[0].pixels_sha256 = '0' * 64
Expect-Fail sixel $sixel

$colors = New-Probe
$colors.colors.selection_background_rgb = 0x123456
$colors.colors.foreground_alias = 2
$colors.colors.background_alias = 3
$colors.title_sha256 = Hash 'TermLink smoke'
Assert-TerminalSemanticProbe colors $colors
Assert-TerminalSemanticProbe C14 $colors
$jsonProbe = $colors | ConvertTo-Json -Depth 20 | ConvertFrom-Json
Assert-TerminalSemanticProbe colors $jsonProbe
$colors.colors.frame_foreground_alias = 4
Expect-Fail colors $colors

$restore = New-Probe
$restore.columns = 20; $restore.rows = 6; $restore.active_screen = 'MAIN'; $restore.reverse_video = $false
$restore.cursor = @{ row = 0; column = 0; visible = $true; wrap_pending = $false; shape = 'BLOCK'; blinking = $true }
$restore.presentation_revision = 1; $restore.next_source_sequence = 9
$beforeRestore = $restore | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$afterRestore = $restore | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$afterRestore.presentation_revision = 2; $afterRestore.next_source_sequence = 10
Assert-TerminalRestoredSemanticProbe $beforeRestore $afterRestore
$afterRestore.screen_rows[3].cells[0].grapheme_sha256 = Hash 'X'
$rejected = $false
try { Assert-TerminalRestoredSemanticProbe $beforeRestore $afterRestore }
catch { $rejected = $_.Exception.Message -ceq 'SNAPSHOT_RESTORE_SEMANTIC_SCREEN_ROWS' }
if (-not $rejected) { throw 'Snapshot restore must reject changed content' }
$afterRestore = $restore | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$afterRestore.PSObject.Properties.Remove('resources')
$rejected = $false
try { Assert-TerminalRestoredSemanticProbe $beforeRestore $afterRestore }
catch { $rejected = $_.Exception.Message -ceq 'SNAPSHOT_RESTORE_SEMANTIC_RESOURCES' }
if (-not $rejected) { throw 'Snapshot restore must reject missing resource evidence' }
Write-Output 'Terminal semantic probe assertions passed.'
