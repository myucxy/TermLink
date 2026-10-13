[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runner = Join-Path $PSScriptRoot 'Invoke-TerminalEffectsProductE2E.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($null -eq $ast -or $errors.Count -ne 0) {
    throw ('EFFECTS_RUNNER_PARSE_FAILED: ' + (($errors | ForEach-Object Message) -join '; '))
}

$source = [IO.File]::ReadAllText($runner)
if (-not $source.Contains('-File $UiHierarchyScript') -or
    -not $source.Contains('$UiTestPackage')) {
    throw 'EFFECTS_UI_DUMP_MUST_USE_INTERACTIVE_WINDOW_CAPTURE'
}
if (-not ($source.Contains('uiautomator') -and $source.Contains('termlink-ui-fallback.xml'))) {
    throw 'EFFECTS_UI_FALLBACK_CAPTURE_MISSING'
}
foreach ($guard in @('session-arm.json', 'ARM_MISMATCH', 'SERVER_OR_ACCOUNT_MISMATCH',
    'SESSION_ID_MISMATCH', 'SUBSCRIBER_NOT_READY', 'PHASE_ACK_TIMEOUT',
    'screencap', 'ui_observed', 'unverified', 'LOOPBACK_READY',
    'H02_AUDIO_ENDPOINT_CHANGED', 'H02_MUTED_SOUND_OBSERVED', 'H02_RATE_LIMIT_SOUND_INVALID',
    'H02_OLD_OUTPUT_REPLAYED_SOUND', 'H02_RECORDER_PATH_INVALID',
    'Test-EffectsHostExitAudio', 'StopOwnedSessionHost', 'H02_EXIT_HOST_PROOF_INVALID',
    'android_settings_restore',
    'H02 audio after actual SessionHost exit')) {
    if (-not $source.Contains($guard)) { throw "EFFECTS_PROOF_MISSING: $guard" }
}

$shell = (Get-Process -Id $PID).Path
$audio = & $shell -NoProfile -File $runner -AudioSelfTest 2>&1
if ($LASTEXITCODE -ne 0 -or @($audio).Count -ne 1 -or
    [string](@($audio)[0]) -cne 'H02 audio WAV, continuity, and host-exit self-test passed; no ADB or recorder was started.') {
    throw ('H02_AUDIO_SELFTEST_FAILED: ' + (@($audio) -join ' | '))
}
$diagnosticTest = & $shell -NoProfile -File $runner -DiagnosticSelfTest 2>&1
$realXml = Join-Path $PSScriptRoot '../artifacts/validation/release-acceptance-20261011-c02-current-r177.xml'
if ($LASTEXITCODE -ne 0 -or @($diagnosticTest).Count -ne 1 -or
    -not [string](@($diagnosticTest)[0]).StartsWith('C02/C08 diagnostic contract self-test passed;') -or
    ((Test-Path -LiteralPath $realXml -PathType Leaf) -and
        -not [string](@($diagnosticTest)[0]).Contains('including r177 XML'))) {
    throw ('C02_C08_DIAGNOSTIC_SELFTEST_FAILED: ' + (@($diagnosticTest) -join ' | '))
}
$result = & $shell -NoProfile -File $runner -DefinitionsOnly -RunId 'selftest-effects' 2>&1
if ($LASTEXITCODE -ne 0) { throw ('EFFECTS_DEFINITIONS_FAILED: ' + (@($result) -join ' | ')) }
$definitions = @((@($result) -join "`n") | ConvertFrom-Json)
$expected = @('shell', 'signals', 'signals-denied', 'signals-background',
    'clipboard-reject', 'clipboard-accept', 'clipboard-covered', 'clipboard-read-only', 'unsupported')
if ($definitions.Count -ne $expected.Count -or
    (@($definitions.phase) -join ',') -cne ($expected -join ',')) {
    throw 'EFFECTS_DEFINITIONS_MISMATCH'
}
foreach ($definition in $definitions) {
    if ($definition.PSObject.Properties.Name -join ',' -cne
        'family,phase,expected_marker,reset_before,sequence_b64,expected_ui') {
        throw 'EFFECTS_TRIGGER_FIELDS_INVALID'
    }
    if ($definition.reset_before -ne $false) { throw 'EFFECTS_RESET_NOT_ALLOWED' }
    $sequence = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($definition.sequence_b64))
    if ($definition.phase -ne 'unsupported' -and
        (-not $definition.expected_marker -or -not $sequence.Contains($definition.expected_marker))) {
        throw 'EFFECTS_MARKER_NOT_IN_PAYLOAD'
    }
    if ($sequence.Length -gt 4096) { throw 'EFFECTS_PAYLOAD_TOO_LARGE' }
}
if (-not ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($definitions[8].sequence_b64))).Contains("$([char]27)[999t")) {
    throw 'EFFECTS_UNSUPPORTED_SAMPLE_MISSING'
}
foreach ($vector in @(
    @{ name = 'cursor-restore'; hex = '1B50312474313B313B313B403B403B463B303B323B4C3B424241411B5C' },
    @{ name = 'tab-restore'; hex = '1B50322474322F332F3531331B5C' }
)) {
    $output = & $shell -NoProfile -File $runner -DefinitionsOnly -Family C02 `
        -DiagnosticVector $vector.name -RunId 'selftest-effects' 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('EFFECTS_C08_DEFINITIONS_FAILED: ' + (@($output) -join ' | ')) }
    $selected = @((@($output) -join "`n") | ConvertFrom-Json)
    $bytes = [Convert]::FromBase64String($selected[8].sequence_b64)
    if ($selected.Count -ne 9 -or $selected[8].phase -cne $vector.name -or
        $null -ne $selected[8].expected_marker -or
        [Convert]::ToHexString($bytes) -cne $vector.hex -or
        (@($selected[0..7].phase) -join ',') -cne (@($definitions[0..7].phase) -join ',')) {
        throw 'EFFECTS_C08_VECTOR_BYTES_MISMATCH'
    }
}
foreach ($vector in @('cursor-restore', 'tab-restore')) {
    $rejected = & $shell -NoProfile -File $runner -DefinitionsOnly -DiagnosticVector $vector 2>&1
    if ($LASTEXITCODE -eq 0 -or
        -not ((@($rejected) -join ' ').Contains('DIAGNOSTIC_VECTOR_REQUIRES_C02'))) {
        throw 'EFFECTS_C08_OUTSIDE_DIAGNOSTIC_RUN_ACCEPTED'
    }
}
foreach ($guard in @('C08_DIAGNOSTIC_DETAILS_MISMATCH',
    'Test-EffectsDiagnosticDetails $display $diagnostic',
    'C08_WRONG_HEX_ACCEPTED', 'C08_WRONG_HIDDEN_COUNT_ACCEPTED',
    'C08_PAYLOAD_LEAK_ACCEPTED', 'C08_REAL_XML_REJECTED')) {
    if (-not $source.Contains($guard)) { throw 'EFFECTS_C08_UI_CLASSIFICATION_GUARD_MISSING' }
}
$readOnlySequence = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($definitions[7].sequence_b64))
if (-not $readOnlySequence.Contains("$([char]27)]52;c;") -or
    $readOnlySequence -ceq [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($definitions[5].sequence_b64))) {
    throw 'EFFECTS_READ_ONLY_FIXTURE_INVALID'
}

# Missing run context must stop before invoking ADB or writing a trigger.
$negative = & $shell -NoProfile -File $runner -Family C02 2>&1
if ($LASTEXITCODE -eq 0 -or -not ((@($negative) -join ' ').Contains('RUN_CONTEXT_REQUIRED'))) {
    throw 'EFFECTS_RUN_CONTEXT_GUARD_FAILED'
}
$outOfScope = & $shell -NoProfile -File $runner -RunId 'selftest-effects' -ExpectedSessionId ([guid]::NewGuid()) `
    -ExpectedOrigin 'https://example.invalid' -AdbPath $shell `
    -TriggerPath (Join-Path $env:TEMP 'effects.trigger') `
    -EvidencePath (Join-Path $env:TEMP 'effects.json') 2>&1
if ($LASTEXITCODE -eq 0 -or -not ((@($outOfScope) -join ' ').Contains('EVIDENCE_SCOPE_INVALID'))) {
    throw 'EFFECTS_WORKSPACE_SCOPE_GUARD_FAILED'
}
# Exercise the actual runner functions with device boundaries replaced, without loading its main program.
& {
    foreach ($function in $ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Parent -is [Management.Automation.Language.NamedBlockAst]
    }, $false)) { . ([scriptblock]::Create($function.Extent.Text)) }
    . (Join-Path $PSScriptRoot 'Assert-TerminalSemanticProbe.ps1')
    function Check([bool]$Condition, [string]$Code) { if (-not $Condition) { throw $Code } }
    function Reject([scriptblock]$Action, [string]$Code) {
        try { & $Action } catch {
            if ($_.Exception.Message -ceq $Code) { return }
            throw
        }
        throw "EXPECTED_REJECTION_$Code"
    }
    function Start-Sleep { param($Milliseconds, $Seconds) }
    function Invoke-Adb { throw 'UNMOCKED_ADB' }
    $AndroidPackageName = 'dev.termlink.harness'
    $RunId = 'selftest-effects'
    $ExpectedSessionId = [guid]'5c757234-b498-40ce-ae0f-0f24e291326c'
    $ExpectedOrigin = 'https://example.invalid'
    $DiagnosticVector = 'unsupported'
    $script:mockAdbCalls = [Collections.Generic.List[string]]::new()
    function Require-Adb($Arguments, $Code) { $script:mockAdbCalls.Add($Arguments -join ' ') }
    $script:mockUi = [xml]@'
<hierarchy><window><node package="dev.termlink.harness" clickable="true" enabled="true" bounds="[0,0][100,50]" content-desc="关闭本地标签页"/>
<node package="dev.termlink.harness" clickable="true" enabled="true" bounds="[100,100][200,200]"><node text="关闭" checked="false" checkable="false"/></node>
<node package="dev.termlink.harness" clickable="true" enabled="true" bounds="[0,200][200,250]" checked="true" checkable="true"><node text="终端提醒" checked="false" checkable="false"/></node>
</window></hierarchy>
'@
    function Get-UiNodes { $script:mockUi.SelectNodes('//node') }
    Tap-Ui @('关闭', 'Close') 'CLOSE_MISSING'
    Check ($script:mockAdbCalls.Count -eq 1 -and $script:mockAdbCalls[0] -ceq 'shell input tap 150 150') 'CLOSE_SELECTED_LOCAL_TAB'
    $toggle = Get-UiToggle @('终端提醒')
    Check ($null -ne $toggle -and $toggle.GetAttribute('checked') -ceq 'true') 'TOGGLE_LABEL_CHECKED_USED'
    $toggle.RemoveAttribute('checked')
    Check ($null -eq (Get-UiToggle @('终端提醒'))) 'MISSING_CHECKED_ACCEPTED'
    $toggle.SetAttribute('checked', 'true')
    $button = (Find-UiNode @('关闭')).ParentNode
    $button.SetAttribute('enabled', 'false')
    Reject { Tap-Ui @('关闭') 'CLOSE_MISSING' } 'UI_TARGET_NOT_ACTIONABLE'
    Check ($script:mockAdbCalls.Count -eq 1) 'DISABLED_BUTTON_TAPPED'
    $button.SetAttribute('enabled', 'true')
    $button.SetAttribute('package', 'other.application')
    Reject { Tap-Ui @('关闭') 'CLOSE_MISSING' } 'UI_TARGET_NOT_ACTIONABLE'
    $script:mockUi = [xml]'<hierarchy><node text="关闭本地标签页"/></hierarchy>'
    Check ($null -eq (Find-UiNode @('关闭'))) 'CLOSE_SUBSTRING_MATCHED'
    Reject { Close-Menu } 'STATUS_MENU_NOT_OPEN'
    Check ($script:mockAdbCalls.Count -eq 1) 'BACK_SENT_WITHOUT_MENU'

    $script:mockPermission = 'denied'
    function Get-NotificationPermission { $script:mockPermission }
    Set-NotificationPermission 'denied'
    Check ($script:mockAdbCalls.Count -eq 1) 'REDUNDANT_REVOKE_KILLS_APP'
    Set-NotificationPermission 'granted'
    Check ($script:mockAdbCalls.Count -eq 2 -and $script:mockAdbCalls[1] -ceq
        'shell pm grant dev.termlink.harness android.permission.POST_NOTIFICATIONS') 'PERMISSION_GRANT_NOT_ISSUED'

    $script:mockHookCalls = [Collections.Generic.List[string]]::new()
    $script:mockInspectQueue = [Collections.Generic.Queue[object]]::new()
    $script:mockProbe = [pscustomobject]@{ account_ready = $true; server_origin = $ExpectedOrigin }
    $script:mockReady = [pscustomobject]@{ session_id = "$ExpectedSessionId"; realtime_subscriber_ready = $true; live_ack = 12L }
    function Invoke-Hook($Operation, $Arguments = @{}, $TimeoutSeconds = 30) {
        $script:mockHookCalls.Add($Operation)
        switch ($Operation) {
            'probe' { return $script:mockProbe }
            'inspect' {
                if ($script:mockInspectQueue.Count -eq 0) { throw 'UNEXPECTED_INSPECT' }
                $next = $script:mockInspectQueue.Dequeue()
                if ($next -is [string]) {
                    $script:lastHookResponse = [pscustomobject]@{ failure_code = $next }
                    throw 'HOOK_FAILED'
                }
                return $next
            }
            'reselect_armed_session' { return $script:mockReady }
            default { throw 'UNEXPECTED_HOOK' }
        }
    }
    $script:mockInspectQueue.Enqueue($script:mockReady)
    Resume-TestSession 12
    Check (($script:mockHookCalls -join ',') -ceq 'probe,inspect') 'READY_SESSION_REOPENED'
    Check ($script:mockAdbCalls[2] -ceq ('shell am start -W -n dev.termlink.harness/dev.termlink.product.ProductActivity ' +
        '-a android.intent.action.MAIN -c android.intent.category.LAUNCHER --activity-single-top')) 'NONDETERMINISTIC_LAUNCH'
    $script:mockHookCalls.Clear()
    $script:mockInspectQueue.Enqueue('SESSION_UNAVAILABLE')
    Resume-TestSession 12
    Check (($script:mockHookCalls -join ',') -ceq 'probe,inspect,reselect_armed_session') 'ARMED_SESSION_NOT_RESTORED'
    $script:mockInspectQueue.Enqueue('SESSION_ARM_UNAVAILABLE')
    Reject { Resume-TestSession 12 } 'HOOK_FAILED'
    $script:mockInspectQueue.Enqueue([pscustomobject]@{ session_id = [guid]::NewGuid(); realtime_subscriber_ready = $true; live_ack = 12L })
    Reject { Resume-TestSession 12 } 'SESSION_ID_MISMATCH'
    $script:mockHookCalls.Clear()
    $script:mockProbe.server_origin = 'https://other.invalid'
    Reject { Resume-TestSession 12 } 'SERVER_OR_ACCOUNT_MISMATCH'
    Check (($script:mockHookCalls -join ',') -ceq 'probe') 'FOREIGN_ACCOUNT_RESELECTED'
    $script:mockProbe.server_origin = $ExpectedOrigin
    $script:mockInspectQueue.Enqueue([pscustomobject]@{ session_id = "$ExpectedSessionId"; realtime_subscriber_ready = $false; live_ack = 11L })
    $script:mockInspectQueue.Enqueue($script:mockReady)
    Resume-TestSession 12
    Check ($script:mockInspectQueue.Count -eq 0) 'RESUME_RETURNED_BEFORE_ACK_READY'

    function ResponseFor([string]$Text, [long]$Ack = 13) {
        $column = 0
        $cells = @($Text.ToCharArray() | ForEach-Object {
            [pscustomobject]@{ column = $column++; wide_continuation = $false; display_width = 1;
                grapheme_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                    [Text.Encoding]::UTF8.GetBytes([string]$_))).ToLowerInvariant() }
        })
        return [pscustomobject]@{ session_id = "$ExpectedSessionId"; realtime_subscriber_ready = $true;
            live_ack = $Ack; visible_text_sha256 = 'changed'; live_marker_visible = $true;
            history_overlay = $false;
            frozen_history = [pscustomobject]@{ active = $false; page_count = 0 };
            frozen_active = $false;
            frozen_render_ready = $false; terminal_semantics = [pscustomobject]@{
                screen_rows = @([pscustomobject]@{ cells = $cells }) } }
    }
    $phase = @(Get-Definitions $RunId)[1]
    $marker = Get-EffectsPhaseMarker $phase
    $rightPhase = ResponseFor $marker
    $wrongPhase = ResponseFor 'TLEF_unrelated'
    Check (-not (Test-EffectsPhaseObserved $phase $wrongPhase 12)) 'UNRELATED_ACK_HASH_ACCEPTED'
    Check (-not (Test-EffectsPhaseObserved $phase (ResponseFor $marker 12) 12)) 'OLD_PHASE_ACK_ACCEPTED'
    Check (Test-EffectsPhaseObserved $phase $rightPhase 12) 'CURRENT_PHASE_REJECTED'
    $TriggerPath = Join-Path $env:TEMP ('effects-selftest-' + [guid]::NewGuid().ToString('N') + '.trigger')
    try {
        Write-PhaseTrigger $phase
        $trigger = [IO.File]::ReadAllText($TriggerPath) | ConvertFrom-Json
        $sequence = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($trigger.sequence_b64))
        Check ($trigger.reset_before -eq $false -and $sequence.StartsWith($phase.sequence) -and
            $sequence.Contains($marker + ' ' + (Get-LiveMarker $RunId)) -and
            ($marker + ' ' + (Get-LiveMarker $RunId)).Length -le 60) 'PHASE_PROOF_SEQUENCE_INVALID'
        Reject { Write-PhaseTrigger $phase } 'TRIGGER_ALREADY_EXISTS'
    } finally { if ([IO.File]::Exists($TriggerPath)) { [IO.File]::Delete($TriggerPath) } }
    function Write-PhaseTrigger($Definition) { }
    $script:mockInspectQueue.Enqueue($wrongPhase)
    $script:mockInspectQueue.Enqueue($rightPhase)
    $received = Send-Phase $phase 12 'old' (Get-LiveMarker $RunId)
    Check ($received -eq $rightPhase -and $script:mockInspectQueue.Count -eq 0) 'SEND_PHASE_HASH_ONLY_ACCEPTED'
    $rightPhase.history_overlay = $false
            $rightPhase.frozen_history = [pscustomobject]@{ active = $true; page_count = 1 }
    
    $rightPhase.visible_text_sha256 = 'overlay'
    $script:mockInspectQueue.Enqueue($rightPhase)
    $covered = Send-CoveredPhase $phase 12 'overlay' (Get-LiveMarker $RunId)
    Check ($covered.frozen_history.active -eq $true -and $covered.frozen_history.page_count -eq 1) 'COVERED_FROZEN_VIEW_NOT_READY'
    $script:resumeAck = -1L
    function Resume-TestSession([long]$MinimumAck = 0) { $script:resumeAck = $MinimumAck }
    $rightPhase.history_overlay = $false
    $rightPhase.visible_text_sha256 = 'changed'
    $background = ResponseFor $marker
    $background | Add-Member -NotePropertyName terminal_fixture_notification_present -NotePropertyValue $true
    $script:mockInspectQueue.Enqueue($background)
    $script:mockInspectQueue.Enqueue($wrongPhase)
    $script:mockInspectQueue.Enqueue($rightPhase)
    $resumed = Send-BackgroundPhase $phase 12 'old' (Get-LiveMarker $RunId)
    Check ($resumed -eq $rightPhase -and $script:mockInspectQueue.Count -eq 0 -and
        $script:resumeAck -eq 12 -and $script:backgroundNotificationSeen -eq $true) 'BACKGROUND_CAUSALITY_OR_ACK_LOST'
    $background.session_id = [guid]::NewGuid()
    $script:mockInspectQueue.Enqueue($background)
    Reject { Send-BackgroundPhase $phase 12 'old' (Get-LiveMarker $RunId) } 'SESSION_ID_MISMATCH'
    $script:initialAlerts = 'false'
    $script:initialSounds = $null
    $script:initialClipboard = $null
    $script:initialNotificationPermission = 'denied'
    $script:mockPermission = 'granted'
    function Open-Menu { throw 'MOCK_MENU_UNAVAILABLE' }
    function Set-NotificationPermission($State) { $script:mockPermission = $State }
    Reject { Restore-EffectsAndroidSettings } 'MOCK_MENU_UNAVAILABLE'
    Check ($script:mockPermission -ceq 'denied') 'PERMISSION_NOT_RESTORED_AFTER_MENU_FAILURE'
}
'Effects product E2E parser, definitions, navigation, permission, resume, phase-causality mocks, and no-ADB guards passed.'
