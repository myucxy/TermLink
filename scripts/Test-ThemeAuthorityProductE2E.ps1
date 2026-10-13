[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runner = Join-Path $PSScriptRoot 'Invoke-ThemeAuthorityProductE2E.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'THEME_AUTHORITY_RUNNER_PARSE_FAILED' }
$reservedHostUses = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.VariablePath.UserPath -ieq 'Host'
}, $true))
if ($reservedHostUses.Count -ne 0) { throw 'THEME_AUTHORITY_RESERVED_HOST_VARIABLE_USED' }
$ownedHostInitializers = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -ceq 'ownedSessionHost' -and
        $node.Right.Extent.Text -ceq '$null'
}, $true))
if ($ownedHostInitializers.Count -ne 1) { throw 'THEME_AUTHORITY_OWNED_HOST_INITIALIZATION_MISSING' }
. ([ScriptBlock]::Create($ownedHostInitializers[0].Extent.Text))
if ($null -ne $ownedSessionHost) { throw 'THEME_AUTHORITY_OWNED_HOST_INITIALIZATION_FAILED' }
$source = Get-Content -LiteralPath $runner -Raw
foreach ($required in @('open_session', 'Assert-RecipientPair $first $second $sessionId $true',
    'SECOND_RELAY_NOT_LOST', 'SECOND_RELAY_NOT_RECOVERED',
    "@('shell', 'cmd', 'connectivity', 'airplane-mode', 'enable')",
    "@('shell', 'cmd', 'connectivity', 'airplane-mode', 'disable')",
    '& $child --protocol-reply-relay-child', "'--select-owned-display-theme'",
    'Assert-TerminalReplyProbe -Phase $phase', 'Assert-RecipientPair $first $second $sessionId',
    'Save-Inspect', 'host_forced_stop', 'HOST_EXIT_NOT_EXPECTED',
    '$probe = $null', '$probe = Invoke-ProbeWithStartupRetry $serial')) {
    if (-not $source.Contains($required)) { throw "THEME_AUTHORITY_RUNNER_MISSING: $required" }
}
foreach ($name in @('Require-Theme', 'Get-ThemeField', 'Assert-RecipientPair',
    'Assert-RecipientStable', 'Assert-RelayRecovery', 'Start-Hook', 'Invoke-Hook',
    'Invoke-ProbeWithStartupRetry')) {
    $nodes = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq $name
    }, $true))
    if ($nodes.Count -ne 1) { throw "THEME_AUTHORITY_FUNCTION_MISSING: $name" }
    . ([ScriptBlock]::Create($nodes[0].Extent.Text))
}

function New-Inspect([string]$Id, [int]$Theme, [bool]$Ready = $true,
    [long]$Generation = 1, [long]$Lost = 0, [long]$Ack = 10) {
    return [pscustomobject]@{
        session_id = 'f4fa7e50-b8eb-4194-849c-9ed5b6461390'
        android_device_id = $Id
        terminal_theme_scheme = $Theme
        realtime_subscriber_ready = $Ready
        live_marker_visible = $true
        realtime_connected_generation = $Generation
        realtime_network_lost_count = $Lost
        live_ack = $Ack
    }
}

function Require-Failure([scriptblock]$Check, [string]$Expected) {
    try { & $Check }
    catch {
        if ($_.Exception.Message -cne $Expected) { throw }
        return
    }
    throw "THEME_AUTHORITY_NEGATIVE_ACCEPTED: $Expected"
}

$session = 'f4fa7e50-b8eb-4194-849c-9ed5b6461390'
$firstId = 'b5d600d9-a067-4ef4-9b3f-28ab3e1f2d03'
$secondId = '7b1b80ca-38e0-495d-a938-fbb984e20110'
$first = New-Inspect $firstId 1
$second = New-Inspect $secondId 2
Assert-RecipientPair $first $second $session $true
Require-Failure { Assert-RecipientPair $first (New-Inspect $firstId 2) $session } `
    'RECIPIENTS_NOT_INDEPENDENT'
Require-Failure { Assert-RecipientPair $first (New-Inspect $secondId 1) $session } `
    'RECIPIENT_THEMES_NOT_DISTINCT'
Require-Failure { Assert-RecipientPair $first $second ([guid]::NewGuid().ToString()) } `
    'RECIPIENT_SESSION_MISMATCH'
$missingId = New-Inspect $secondId 2
$missingId.PSObject.Properties.Remove('android_device_id')
Require-Failure { Assert-RecipientPair $first $missingId $session } 'RECIPIENT_ID_UNAVAILABLE'
$missingTheme = New-Inspect $secondId 2
$missingTheme.PSObject.Properties.Remove('terminal_theme_scheme')
Require-Failure { Assert-RecipientPair $first $missingTheme $session } 'RECIPIENT_THEME_UNAVAILABLE'
Assert-RecipientStable $first $second $firstId $secondId 1 2
Require-Failure { Assert-RecipientStable $first $second $secondId $firstId 1 2 } `
    'RECIPIENT_ID_CHANGED'
Require-Failure { Assert-RecipientStable $first $second $firstId $secondId 2 1 } `
    'RECIPIENT_THEME_CHANGED'

$firstDuring = New-Inspect $firstId 1 $true 1 0 11
$firstAfter = New-Inspect $firstId 1 $true 1 0 12
$secondDuring = New-Inspect $secondId 2 $false 1 1 10
$secondAfter = New-Inspect $secondId 2 $true 2 1 12
Assert-RelayRecovery $first $firstDuring $firstAfter $second $secondDuring $secondAfter
Require-Failure {
    Assert-RelayRecovery $first $firstDuring $firstAfter $second `
        (New-Inspect $secondId 2 $true 1 0 10) $secondAfter
} 'SECOND_RELAY_NOT_LOST'
Require-Failure {
    Assert-RelayRecovery $first $firstDuring $firstAfter $second $secondDuring `
        (New-Inspect $secondId 2 $true 1 1 12)
} 'SECOND_RELAY_NOT_RECOVERED'
Require-Failure {
    Assert-RelayRecovery $first (New-Inspect $firstId 1 $true 2 0 11) `
        $firstAfter $second $secondDuring $secondAfter
} 'FIRST_RELAY_INTERRUPTED'

$AndroidPackageName = 'dev.termlink.harness'
$responsePath = 'test-response.json'
$armPath = 'test-arm.json'
$action = 'dev.termlink.product.debug.TAIL_FIRST_E2E'
$receiver = 'dev.termlink.product.debug.TailFirstE2EReceiver'
$RunId = 'test-theme-run'
$script:adbCalls = @()
function Require-Adb([string]$Serial, [string[]]$Arguments, [string]$Code,
    [int]$TimeoutSeconds = 20) {
    $script:adbCalls += [pscustomobject]@{ serial = $Serial; args = @($Arguments); code = $Code }
    return ''
}
$hookId = Start-Hook 'emulator-5554' 'probe' @{ marker = 'expected-marker' }
if ($hookId -notmatch '^[0-9a-f]{32}$' -or $script:adbCalls.Count -ne 2 -or
    $script:adbCalls[0].code -cne 'HOOK_RESET_FAILED' -or
    $script:adbCalls[1].code -cne 'HOOK_BROADCAST_FAILED' -or
    ($script:adbCalls[1].args -join '|') -cnotmatch '\|--es\|marker\|expected-marker$') {
    throw 'THEME_AUTHORITY_HOOK_ARGUMENTS_NOT_PRESERVED'
}

$responsePath = 'test-response.json'
$script:mockResponse = $null
function Start-Hook([string]$Serial, [string]$Operation, [hashtable]$Arguments = @{}) { return 'test-request' }
function Wait-HookFile([string]$Serial, [string]$Path, [string]$RequestId, [int]$Seconds) {
    return $script:mockResponse
}
$script:mockResponse = [pscustomobject]@{ operation = 'probe'; ok = $false;
    failure_code = 'ACCOUNT_UNAUTHENTICATED' }
Require-Failure { Invoke-Hook 'emulator-5554' 'probe' } 'ACCOUNT_UNAUTHENTICATED'
$script:mockResponse.failure_code = 'bad freeform error'
Require-Failure { Invoke-Hook 'emulator-5554' 'probe' } 'HOOK_OPERATION_FAILED'
$script:mockResponse.operation = 'inspect'
Require-Failure { Invoke-Hook 'emulator-5554' 'probe' } 'HOOK_OPERATION_MISMATCH'

$script:attempts = 0
function Invoke-Hook([string]$Serial, [string]$Operation, [hashtable]$Arguments = @{},
    [int]$Seconds = 30) {
    $script:attempts++
    throw [InvalidOperationException]::new('ACCOUNT_UNAUTHENTICATED')
}
Require-Failure { Invoke-ProbeWithStartupRetry 'emulator-5556' 1 } 'ACCOUNT_UNAUTHENTICATED'
if ($script:attempts -ne 1) { throw 'NON_STARTUP_HOOK_FAILURE_RETRIED' }

Write-Output 'Theme authority runner parse, integration, and fail-closed checks passed.'
