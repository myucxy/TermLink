[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Assert-TerminalReplyProbe.ps1')
$esc = [string][char]27
$st = $esc + '\'
$inspect = [pscustomobject]@{
    terminal_rows = 24
    terminal_columns = 80
    host_geometry = [pscustomobject]@{
        cell_width = 9; cell_height = 18
        display_width = 1920; display_height = 1080
        window_width = 744; window_height = 476
    }
}

function Record([string]$stage, [string]$sent, [string]$received) {
    $hex = [Convert]::ToHexString([Text.Encoding]::Latin1.GetBytes($received))
    return [pscustomobject]@{
        stage = $stage
        sent_b64 = [Convert]::ToBase64String([Text.Encoding]::Latin1.GetBytes($sent))
        received_hex = $hex
        received_chunks_hex = if ($hex.Length -eq 0) { @() } else { @($hex.Substring(0, 2), $hex.Substring(2)) }
        unexpected_code_units_hex = ''
        timed_out = $false
        truncated = $false
    }
}
function Probe([string]$phase, [object[]]$records) {
    $sent = ($records | ForEach-Object { [Text.Encoding]::Latin1.GetString([Convert]::FromBase64String($_.sent_b64)) }) -join ''
    return [pscustomobject]@{
        phase = $phase
        sent_b64 = [Convert]::ToBase64String([Text.Encoding]::Latin1.GetBytes($sent))
        received_hex = ($records.received_hex -join '')
        records = $records
        timed_out = $false
        truncated = $false
        error = $null
    }
}
function ExpectFailure([scriptblock]$run, [string]$code) {
    try { $null = & $run; throw "EXPECTED_FAILURE_MISSING_$code" }
    catch { if ($_.Exception.Message -cne $code) { throw } }
}

$geometrySent = $esc + '[14t' + $esc + '[14;2t' + $esc + '[15t' + $esc + '[16t' + $esc + '[18t' + $esc + '[19t'
$geometryReceived = $esc + '[4;432;720t' + $esc + '[4;476;744t' + $esc + '[5;1080;1920t' +
    $esc + '[6;18;9t' + $esc + '[8;24;80t' + $esc + '[9;60;213t'
$geometry = Probe geometry @((Record geometry $geometrySent $geometryReceived))
$result = Assert-TerminalReplyProbe geometry $geometry $inspect
if ($result.byte_assertions -cne 'passed' -or $result.geometry_detail_unavailable) { throw 'GEOMETRY_VECTOR_FAILED' }
$withoutGeometry = [pscustomobject]@{ terminal_rows = 24; terminal_columns = 80 }
if (-not (Assert-TerminalReplyProbe geometry $geometry $withoutGeometry).geometry_detail_unavailable) {
    throw 'GEOMETRY_MISSING_FIELD_NOT_REPORTED'
}
$unknownGeometry = [pscustomobject]@{
    terminal_rows = 24; terminal_columns = 80
    host_geometry = [pscustomobject]@{
        cell_width = 0; cell_height = 0; display_width = 0; display_height = 0
        window_width = 0; window_height = 0
    }
}
$unknownReply = Probe geometry @((Record geometry $geometrySent ($esc + '[8;24;80t')))
if ((Assert-TerminalReplyProbe geometry $unknownReply $unknownGeometry).byte_assertions -cne 'passed') {
    throw 'GEOMETRY_UNKNOWN_HOST_VECTOR_FAILED'
}
ExpectFailure { Assert-TerminalReplyProbe geometry $unknownReply $inspect } 'REPLY_GEOMETRY_ANDROID_HOST_GEOMETRY'
$badGeometry = Probe geometry @((Record geometry $geometrySent ($geometryReceived.Replace('[8;24;80t', '[8;25;80t'))))
ExpectFailure { Assert-TerminalReplyProbe geometry $badGeometry $inspect } 'REPLY_GEOMETRY_ANDROID_GRID'

$querySent = $esc + '[1;1HAB' + $esc + '[=c' + $esc + '[?15n' + $esc + '["v' + $esc + '[42;1;1;1;1;2*y'
$queryReceived = $esc + 'P!|00000000' + $st + $esc + '[?13n' + $esc + '[24;80;1;1;1"w' +
    $esc + 'P42!~FE9D' + $st
$fixedQuerySent = $esc + '[x' + $esc + '[1x' + $esc + '[?25n' + $esc + '[?26n' +
    $esc + '[?55n' + $esc + '[?56n' + $esc + '[?75n'
$fixedQueryReceived = $esc + '[2;1;1;128;128;1;0x' + $esc + '[3;1;1;128;128;1;0x' +
    $esc + '[?23n' + $esc + '[?27;0;0;5n' + $esc + '[?53n' + $esc + '[?57;0n' + $esc + '[?70n'
$da3Reply = $esc + 'P!|00000000' + $st
$queryRecords = @(
    (Record queries $querySent $queryReceived),
    (Record fixed $fixedQuerySent $fixedQueryReceived),
    (Record repeat ($esc + '[=c' + $esc + '[=c') ($da3Reply + $da3Reply)),
    (Record split-head ($esc + '[?26') ''),
    (Record split-tail 'n' ($esc + '[?27;0;0;5n'))
)
$query = Probe queries $queryRecords
if ((Assert-TerminalReplyProbe queries $query $inspect).byte_assertions -cne 'passed') { throw 'QUERY_VECTOR_FAILED' }
$badQuery = Probe queries (@((Record queries $querySent ($queryReceived.Replace('FE9D', '0000')))) +
    @($queryRecords[1..4]))
ExpectFailure { Assert-TerminalReplyProbe queries $badQuery $inspect } 'REPLY_QUERIES_REPLY_BYTES'
$badFixedQuery = Probe queries @($queryRecords[0],
    (Record fixed $fixedQuerySent ($fixedQueryReceived.Replace('[?23n', '[?13n'))),
    $queryRecords[2], $queryRecords[3], $queryRecords[4])
ExpectFailure { Assert-TerminalReplyProbe queries $badFixedQuery $inspect } 'REPLY_QUERIES_FIXED_QUERY_REPLY'
$badRepeatQuery = Probe queries @($queryRecords[0], $queryRecords[1],
    (Record repeat ($esc + '[=c' + $esc + '[=c') $da3Reply), $queryRecords[3], $queryRecords[4])
ExpectFailure { Assert-TerminalReplyProbe queries $badRepeatQuery $inspect } 'REPLY_QUERIES_REPEATED_QUERY_REPLY'
$badSplitQuery = Probe queries @($queryRecords[0], $queryRecords[1], $queryRecords[2],
    (Record split-head ($esc + '[?26') ($esc + '[?27;0;0;5n')), $queryRecords[4])
ExpectFailure { Assert-TerminalReplyProbe queries $badSplitQuery $inspect } 'REPLY_QUERIES_SPLIT_QUERY_REPLY'

$before1 = $esc + 'P1$u1;1;1;@;@;@;0;2;L;BBAA' + $st
$before2 = $esc + 'P2$u9/17' + $st
$changed1 = $esc + 'P1$u2;3;1;A;@;@;0;2;L;BBAA' + $st
$changed2 = $esc + 'P2$u' + $st
$stateQuery = $esc + '[1$w' + $esc + '[2$w'
$restore = $esc + 'P1$t1;1;1;@;@;@;0;2;L;BBAA' + $st
$restoreTabs = $esc + 'P2$t9/17' + $st
$stateRecords = @(
    (Record before $stateQuery ($before1 + $before2)),
    (Record mutate ($esc + '[2;3H' + $esc + '[1m' + $esc + '[3g') ''),
    (Record changed $stateQuery ($changed1 + $changed2)),
    (Record bad-query-quiet ($esc + '[2;3$w') ''),
    (Record cancelled-restore-quiet ($esc + 'P2$t1/2/513' + [string][char]24) ''),
    (Record after-rejection $stateQuery ($changed1 + $changed2)),
    (Record restore-and-query ($restore + $restoreTabs + $stateQuery) ($before1 + $before2))
)
$state = Probe state $stateRecords
if ((Assert-TerminalReplyProbe state $state $inspect).byte_assertions -cne 'passed') { throw 'STATE_VECTOR_FAILED' }
$badState = Probe state @(
    $stateRecords[0], $stateRecords[1], $stateRecords[2], $stateRecords[3],
    $stateRecords[4], $stateRecords[5],
    (Record restore-and-query ($restore + $restoreTabs + $stateQuery) ($before1.Replace('1;1;1', '2;1;1') + $before2))
)
ExpectFailure { Assert-TerminalReplyProbe state $badState $inspect } 'REPLY_STATE_RESTORE_ROUNDTRIP'
$badMutation = Probe state @($stateRecords[0], $stateRecords[1],
    (Record changed $stateQuery ($before1 + $before2)),
    $stateRecords[3], $stateRecords[4], $stateRecords[5], $stateRecords[6])
ExpectFailure { Assert-TerminalReplyProbe state $badMutation $inspect } 'REPLY_STATE_STATE_MUTATION'
$badAtomicState = Probe state @($stateRecords[0], $stateRecords[1], $stateRecords[2],
    $stateRecords[3], $stateRecords[4],
    (Record after-rejection $stateQuery ($changed1 + $before2)), $stateRecords[6])
ExpectFailure { Assert-TerminalReplyProbe state $badAtomicState $inspect } 'REPLY_STATE_REJECTION_ATOMICITY'
$badParameterReply = Probe state @($stateRecords[0], $stateRecords[1], $stateRecords[2],
    (Record bad-query-quiet ($esc + '[2;3$w') $changed2),
    $stateRecords[4], $stateRecords[5], $stateRecords[6])
ExpectFailure { Assert-TerminalReplyProbe state $badParameterReply $inspect } 'REPLY_STATE_REJECTED_INPUT'
$badCancelledInput = Probe state @($stateRecords[0], $stateRecords[1], $stateRecords[2],
    $stateRecords[3], (Record cancelled-restore-quiet ($esc + 'P2$t1/2/513' + $st) ''),
    $stateRecords[5], $stateRecords[6])
ExpectFailure { Assert-TerminalReplyProbe state $badCancelledInput $inspect } 'REPLY_STATE_REJECTED_INPUT'

$themeReply = $esc + '[?997;1n'
$themeStatusOff = $esc + '[?2031;2$y'
$themeRecords = @(
    (Record query ($esc + '[?996n') $themeReply),
    (Record status-before ($esc + '[?2031$p') $themeStatusOff),
    (Record subscribe ($esc + '[?2031h') $themeReply),
    (Record subscribe-again ($esc + '[?2031h') ''),
    (Record status-on ($esc + '[?2031$p') ($esc + '[?2031;1$y')),
    (Record unsubscribe ($esc + '[?2031l') ''),
    (Record status-after ($esc + '[?2031$p') $themeStatusOff),
    (Record ris-and-query ($esc + 'c' + $esc + '[?996n' + $esc + '[?2031$p') ($themeReply + $themeStatusOff)),
    (Record final-quiet '' '')
)
$theme = Probe theme $themeRecords
if ((Assert-TerminalReplyProbe theme $theme $inspect).byte_assertions -cne 'passed') { throw 'THEME_VECTOR_FAILED' }
$lightReply = $esc + '[?997;2n'
$lightRecords = @($themeRecords)
$lightRecords[0] = Record query ($esc + '[?996n') $lightReply
$lightRecords[2] = Record subscribe ($esc + '[?2031h') $lightReply
$lightRecords[7] = Record ris-and-query ($esc + 'c' + $esc + '[?996n' + $esc + '[?2031$p') ($lightReply + $themeStatusOff)
if ((Assert-TerminalReplyProbe theme (Probe theme $lightRecords) $inspect).byte_assertions -cne 'passed') {
    throw 'LIGHT_THEME_VECTOR_FAILED'
}
function ThemeWithReplacement([int]$index, [string]$sent, [string]$received) {
    $changed = @($themeRecords)
    $changed[$index] = Record $themeRecords[$index].stage $sent $received
    return Probe theme $changed
}
ExpectFailure {
    Assert-TerminalReplyProbe theme (ThemeWithReplacement 0 ($esc + '[?995n') $themeReply) $inspect
} 'REPLY_THEME_QUERY_BYTES'
ExpectFailure {
    Assert-TerminalReplyProbe theme (ThemeWithReplacement 0 ($esc + '[?996n') ($themeReply + $themeReply)) $inspect
} 'REPLY_THEME_THEME_REPLY'
ExpectFailure {
    Assert-TerminalReplyProbe theme (ThemeWithReplacement 0 ($esc + '[?996n') '') $inspect
} 'REPLY_THEME_THEME_REPLY'
ExpectFailure {
    Assert-TerminalReplyProbe theme (ThemeWithReplacement 2 ($esc + '[?2031h') ($esc + '[?997;2n')) $inspect
} 'REPLY_THEME_SUBSCRIBE_REPLY'
ExpectFailure {
    Assert-TerminalReplyProbe theme (ThemeWithReplacement 3 ($esc + '[?2031h') $themeReply) $inspect
} 'REPLY_THEME_DUPLICATE_SUBSCRIPTION_REPLY'
ExpectFailure {
    Assert-TerminalReplyProbe theme (ThemeWithReplacement 4 ($esc + '[?2031$p') $themeStatusOff) $inspect
} 'REPLY_THEME_MODE_ON'
ExpectFailure {
    Assert-TerminalReplyProbe theme (ThemeWithReplacement 7 ($esc + 'c' + $esc + '[?996n' + $esc + '[?2031$p') ($themeReply + $esc + '[?2031;1$y')) $inspect
} 'REPLY_THEME_RIS_PRESERVES_THEME'
ExpectFailure {
    Assert-TerminalReplyProbe theme (ThemeWithReplacement 8 '' $themeReply) $inspect
} 'REPLY_THEME_EXTRA_REPLY'
$timedOutTheme = Probe theme $themeRecords
$timedOutTheme.records[3].timed_out = $true
ExpectFailure { Assert-TerminalReplyProbe theme $timedOutTheme $inspect } 'REPLY_THEME_RECORD_BOUNDED'

foreach ($baseline in @(1, 2)) {
    $other = if ($baseline -eq 1) { 2 } else { 1 }
    $baseReply = $esc + '[?997;' + $baseline + 'n'
    $changedReply = $esc + '[?997;' + $other + 'n'
    $vectors = @(
        (Probe 'theme-switch-arm' @(
            (Record query ($esc + '[?996n') $baseReply),
            (Record subscribe ($esc + '[?2031h') $baseReply),
            (Record extra-quiet '' ''))),
        (Probe 'theme-switch-read' @(
            (Record changed '' $changedReply), (Record extra-quiet '' ''))),
        (Probe 'theme-switch-same' @((Record same-color-quiet '' ''))),
        (Probe 'theme-switch-unsubscribe' @(
            (Record unsubscribe ($esc + '[?2031l') ''),
            (Record status-after ($esc + '[?2031$p') $themeStatusOff))),
        (Probe 'theme-switch-restore' @((Record restored-quiet '' ''))),
        (Probe 'theme-switch-final' @(
            (Record final-query ($esc + '[?996n') $baseReply),
            (Record extra-quiet '' '')))
    )
    foreach ($vector in $vectors) {
        $result = Assert-TerminalReplyProbe -Phase $vector.phase -Probe $vector -Inspect $inspect -ExpectedTheme $baseline
        if ($result.byte_assertions -cne 'passed') { throw "THEME_SWITCH_VECTOR_FAILED_$($vector.phase)" }
        if ($vector.phase -eq 'theme-switch-arm' -and $result.theme_scheme -ne $baseline) {
            throw 'THEME_SWITCH_BASELINE_MISMATCH'
        }
    }
    $duplicate = Probe 'theme-switch-read' @(
        (Record changed '' $changedReply), (Record extra-quiet '' $changedReply))
    ExpectFailure { Assert-TerminalReplyProbe 'theme-switch-read' $duplicate $inspect $baseline } 'REPLY_THEME_SWITCH_READ_EXTRA_REPLY'
    $wrongChange = Probe 'theme-switch-read' @(
        (Record changed '' $baseReply), (Record extra-quiet '' ''))
    ExpectFailure { Assert-TerminalReplyProbe 'theme-switch-read' $wrongChange $inspect $baseline } 'REPLY_THEME_SWITCH_READ_CHANGED_REPLY'
    $wrongFinal = Probe 'theme-switch-final' @(
        (Record final-query ($esc + '[?996n') $changedReply), (Record extra-quiet '' ''))
    ExpectFailure { Assert-TerminalReplyProbe 'theme-switch-final' $wrongFinal $inspect $baseline } 'REPLY_THEME_SWITCH_FINAL_RESTORED_REPLY'
    $unexpected = Probe 'theme-switch-same' @((Record same-color-quiet '' ''))
    $unexpected.records[0].unexpected_code_units_hex = '001B'
    ExpectFailure { Assert-TerminalReplyProbe 'theme-switch-same' $unexpected $inspect $baseline } 'REPLY_THEME_SWITCH_SAME_UNEXPECTED_CONSOLE_CHARACTERS'
    $restoreReply = Probe 'theme-switch-restore' @((Record restored-quiet '' $changedReply))
    ExpectFailure { Assert-TerminalReplyProbe 'theme-switch-restore' $restoreReply $inspect $baseline } 'REPLY_THEME_SWITCH_RESTORE_UNSUBSCRIBED_REPLY'
}

'Terminal reply probe geometry, expanded queries, state recovery, theme, theme switch and negative cases passed.'
