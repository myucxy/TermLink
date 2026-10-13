[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ $_.IsAbsoluteUri -and $_.Scheme -eq 'https' -and
        $_.AbsolutePath -eq '/' -and -not $_.UserInfo -and -not $_.Query -and -not $_.Fragment })]
    [Uri]$AcceptanceOrigin,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9._-]{1,200}$')][string]$AgentControlPipe,
    [Parameter(Mandatory)][string]$SessionHostPath,
    [Parameter(Mandatory)][string]$NativeDisplayPath,
    [Parameter(Mandatory)][string]$ProtocolReplyChildPath,
    [ValidatePattern('^emulator-[0-9]{4}$')][string]$FirstSerial = 'emulator-5554',
    [ValidatePattern('^emulator-[0-9]{4}$')][string]$SecondSerial = 'emulator-5556',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')][string]$AndroidPackageName = 'dev.termlink.harness',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$RunId = ('theme-authority-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' +
        [guid]::NewGuid().ToString('N').Substring(0, 8))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Assert-TerminalSemanticProbe.ps1')
. (Join-Path $PSScriptRoot 'Assert-TerminalReplyProbe.ps1')

function Require-Theme([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw [InvalidOperationException]::new($Code) }
}

function Get-ThemeField($Object, [string]$Name, [string]$Code) {
    $property = if ($null -eq $Object) { $null } else { $Object.PSObject.Properties[$Name] }
    Require-Theme ($null -ne $property) $Code
    return $property.Value
}

function Assert-RecipientPair($First, $Second, [string]$ExpectedSession,
    [bool]$RequireLiveMarker = $false) {
    foreach ($item in @($First, $Second)) {
        Require-Theme ([string](Get-ThemeField $item 'session_id' 'RECIPIENT_SESSION_MISSING') -ceq
            $ExpectedSession) 'RECIPIENT_SESSION_MISMATCH'
        Require-Theme ((Get-ThemeField $item 'realtime_subscriber_ready' 'RECIPIENT_NOT_READY') -eq $true) `
            'RECIPIENT_NOT_READY'
        if ($RequireLiveMarker) {
            Require-Theme ((Get-ThemeField $item 'live_marker_visible' 'RECIPIENT_MARKER_MISSING') -eq $true) `
                'RECIPIENT_MARKER_MISSING'
        }
        $id = [string](Get-ThemeField $item 'android_device_id' 'RECIPIENT_ID_UNAVAILABLE')
        $parsedId = [guid]::Empty
        Require-Theme ([guid]::TryParse($id, [ref]$parsedId) -and
            $parsedId -ne [guid]::Empty) 'RECIPIENT_ID_INVALID'
        Require-Theme ([int](Get-ThemeField $item 'terminal_theme_scheme' 'RECIPIENT_THEME_UNAVAILABLE') -in
            @(1, 2)) 'RECIPIENT_THEME_INVALID'
    }
    Require-Theme ([string]$First.android_device_id -cne [string]$Second.android_device_id) `
        'RECIPIENTS_NOT_INDEPENDENT'
    Require-Theme ([int]$First.terminal_theme_scheme -ne [int]$Second.terminal_theme_scheme) `
        'RECIPIENT_THEMES_NOT_DISTINCT'
}

function Assert-RecipientStable($First, $Second, [string]$FirstId, [string]$SecondId,
    [int]$FirstTheme, [int]$SecondTheme) {
    Require-Theme ([string]$First.android_device_id -ceq $FirstId -and
        [string]$Second.android_device_id -ceq $SecondId) 'RECIPIENT_ID_CHANGED'
    Require-Theme ([int]$First.terminal_theme_scheme -eq $FirstTheme -and
        [int]$Second.terminal_theme_scheme -eq $SecondTheme) 'RECIPIENT_THEME_CHANGED'
}

function Assert-RelayRecovery($FirstBefore, $FirstDuring, $FirstAfter,
    $SecondBefore, $SecondDuring, $SecondAfter) {
    Require-Theme ($SecondDuring.realtime_subscriber_ready -eq $false -and
        [long]$SecondDuring.realtime_network_lost_count -gt
            [long]$SecondBefore.realtime_network_lost_count) 'SECOND_RELAY_NOT_LOST'
    Require-Theme ($FirstDuring.realtime_subscriber_ready -eq $true -and
        [long]$FirstDuring.realtime_connected_generation -eq
            [long]$FirstBefore.realtime_connected_generation) 'FIRST_RELAY_INTERRUPTED'
    Require-Theme ($SecondAfter.realtime_subscriber_ready -eq $true -and
        [long]$SecondAfter.realtime_connected_generation -gt
            [long]$SecondBefore.realtime_connected_generation -and
        [long]$SecondAfter.live_ack -ge [long]$SecondBefore.live_ack) 'SECOND_RELAY_NOT_RECOVERED'
    Require-Theme ($FirstAfter.realtime_subscriber_ready -eq $true -and
        [long]$FirstAfter.realtime_connected_generation -eq
            [long]$FirstBefore.realtime_connected_generation -and
        [long]$FirstAfter.live_ack -ge [long]$FirstBefore.live_ack) 'FIRST_RELAY_NOT_STABLE'
}

function Invoke-Program([string]$Path, [string[]]$Arguments, [int]$TimeoutSeconds = 20) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Path
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Require-Theme ($null -ne $process) 'PROCESS_START_FAILED'
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw [TimeoutException]::new('PROCESS_TIMEOUT')
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.Result; Error = $stderr.Result }
    } finally { $process.Dispose() }
}

function Invoke-Adb([string]$Serial, [string[]]$Arguments, [int]$TimeoutSeconds = 20) {
    return Invoke-Program $script:adbPath (@('-s', $Serial) + $Arguments) $TimeoutSeconds
}

function Require-Adb([string]$Serial, [string[]]$Arguments, [string]$Code,
    [int]$TimeoutSeconds = 20) {
    $result = Invoke-Adb $Serial $Arguments $TimeoutSeconds
    Require-Theme ($result.ExitCode -eq 0) $Code
    return $result.Output
}

function Start-Hook([string]$Serial, [string]$Operation, [hashtable]$Arguments = @{}) {
    $id = [guid]::NewGuid().ToString('N')
    $null = Require-Adb $Serial @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $responsePath) `
        'HOOK_RESET_FAILED'
    if ($Operation -ceq 'open_session') {
        $null = Require-Adb $Serial @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $armPath) `
            'HOOK_ARM_RESET_FAILED'
    }
    $broadcastArguments = @('shell', 'am', 'broadcast', '-a', $action, '-n',
        "$AndroidPackageName/$receiver", '--es', 'e2e_operation', $Operation,
        '--es', 'request_id', $id, '--es', 'run_id', $RunId)
    foreach ($key in @($Arguments.Keys | Sort-Object)) {
        $broadcastArguments += @('--es', $key, [string]$Arguments[$key])
    }
    $null = Require-Adb $Serial $broadcastArguments 'HOOK_BROADCAST_FAILED'
    return $id
}

function Wait-HookFile([string]$Serial, [string]$Path, [string]$RequestId, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $result = Invoke-Adb $Serial @('exec-out', 'run-as', $AndroidPackageName, 'cat', $Path) 10
        $body = [string]($result.Output ?? '')
        if ($result.ExitCode -eq 0 -and $body.Length -gt 0) {
            Require-Theme ($body.Length -le 1MB) 'HOOK_RESPONSE_TOO_LARGE'
            try { $parsed = $body | ConvertFrom-Json -ErrorAction Stop }
            catch {
                if ($body.TrimStart().StartsWith('{')) { throw 'HOOK_RESPONSE_INVALID' }
                Start-Sleep -Milliseconds 250
                continue
            }
            if ([string]$parsed.request_id -ceq $RequestId) { return $parsed }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('HOOK_RESPONSE_TIMEOUT')
}

function Invoke-Hook([string]$Serial, [string]$Operation, [hashtable]$Arguments = @{},
    [int]$Seconds = 30) {
    $id = Start-Hook $Serial $Operation $Arguments
    $result = Wait-HookFile $Serial $responsePath $id $Seconds
    Require-Theme ([string]$result.operation -ceq $Operation) 'HOOK_OPERATION_MISMATCH'
    if ($result.ok -ne $true) {
        $code = [string]$result.failure_code
        if ($code -cmatch '^[A-Z0-9_]{1,64}$') { throw [InvalidOperationException]::new($code) }
        throw [InvalidOperationException]::new('HOOK_OPERATION_FAILED')
    }
    return $result
}

function Invoke-ProbeWithStartupRetry([string]$Serial, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { return Invoke-Hook $Serial 'probe' }
        catch {
            if ($_.Exception.Message -cne 'BRIDGE_UNAVAILABLE') { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('BRIDGE_STARTUP_TIMEOUT')
}

function Wait-Marker([string]$Serial, [string]$Marker, [long]$AfterAck, [string]$SessionId,
    [int]$Seconds = 45) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $item = Invoke-Hook $Serial 'inspect' @{ live_marker = $liveMarker }
        if ([string]$item.session_id -ceq $SessionId -and [long]$item.live_ack -gt $AfterAck -and
            (Test-TerminalSemanticMarker $item.terminal_semantics $Marker)) { return $item }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('REPLY_MARKER_TIMEOUT')
}

function Wait-Relay([bool]$Ready, [string]$Serial, [int]$Seconds = 35) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $item = Invoke-Hook $Serial 'inspect' @{ live_marker = $liveMarker }
        if ([bool]$item.realtime_subscriber_ready -eq $Ready) { return $item }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('RELAY_STATE_TIMEOUT')
}

function Write-Trigger([string]$Path, [string]$Value) {
    Require-Theme (-not [IO.File]::Exists($Path)) 'TRIGGER_ALREADY_PENDING'
    $pending = $Path + '.pending'
    [IO.File]::WriteAllText($pending, $Value)
    [IO.File]::Move($pending, $Path)
}

function Save-Inspect([string]$Name, $Response) {
    $path = Join-Path $runRoot ($Name + '.json')
    [IO.File]::WriteAllText($path, ($Response | ConvertTo-Json -Depth 12) + "`n")
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts\theme-authority-product-e2e\' + $RunId)))
Require-Theme (-not (Test-Path -LiteralPath $runRoot)) 'RUN_ID_ALREADY_EXISTS'
$null = New-Item -ItemType Directory -Path $runRoot
$evidencePath = Join-Path $runRoot 'evidence.json'
$responsePath = 'no_backup/tail-first-e2e/response.json'
$armPath = 'no_backup/tail-first-e2e/session-arm.json'
$action = 'dev.termlink.product.debug.TAIL_FIRST_E2E'
$receiver = 'dev.termlink.product.debug.TailFirstE2EReceiver'
$replyTrigger = Join-Path $runRoot 'protocol-replies.trigger'
$exitTrigger = Join-Path $runRoot 'theme-host-exit.trigger'
$startTrigger = Join-Path $runRoot 'theme-replies-start.trigger'
$ownedSessionHost = $null
$hostStartTicks = 0L
$airplaneChanged = $false
$stage = 'preflight'
$evidence = [ordered]@{ schema_version = 1; run_id = $RunId; status = 'running'; stage = $stage;
    first_serial = $FirstSerial; second_serial = $SecondSerial; session_id = $null;
    first_recipient_id = $null; second_recipient_id = $null; first_theme = $null; second_theme = $null;
    relay_recovery = $null; phases = @(); host_pid = $null; host_stopped = $false;
    host_forced_stop = $false; host_exit_code = $null; failure_code = $null;
    failure_type = $null; failure_line = 0 }

try {
    Require-Theme ($FirstSerial -cne $SecondSerial) 'EMULATOR_SERIALS_NOT_DISTINCT'
    $manifest = Get-Content -LiteralPath 'D:\Dev\DEV_ENV_MANIFEST.json' -Raw | ConvertFrom-Json
    $sdk = @($manifest.tools | Where-Object { $_.id -eq 'android-sdk' })
    Require-Theme ($sdk.Count -eq 1) 'ANDROID_SDK_MANIFEST_MISSING'
    $script:adbPath = [IO.Path]::GetFullPath((Join-Path $manifest.roots.root $sdk[0].probes[0].relativePath))
    $resolvedHost = [IO.Path]::GetFullPath($SessionHostPath)
    $resolvedDisplay = [IO.Path]::GetFullPath($NativeDisplayPath)
    $resolvedChild = [IO.Path]::GetFullPath($ProtocolReplyChildPath)
    foreach ($path in @($script:adbPath, $resolvedHost, $resolvedDisplay, $resolvedChild)) {
        Require-Theme (Test-Path -LiteralPath $path -PathType Leaf) 'REQUIRED_BINARY_MISSING'
    }
    foreach ($serial in @($FirstSerial, $SecondSerial)) {
        Require-Theme ((Require-Adb $serial @('get-state') 'EMULATOR_NOT_RUNNING').Trim() -ceq 'device') `
            'EMULATOR_NOT_RUNNING'
    }
    $controlAssembly = Join-Path (Split-Path -Parent $resolvedHost) 'TermLink.AgentControl.dll'
    Require-Theme (Test-Path -LiteralPath $controlAssembly -PathType Leaf) 'AGENT_CONTROL_ASSEMBLY_MISSING'
    $assembly = [Reflection.Assembly]::LoadFrom($controlAssembly)
    $requestType = $assembly.GetType('TermLink.Agent.RegistryRequest', $true)
    $commandType = $assembly.GetType('TermLink.Agent.RegistryCommand', $true)
    $clientType = $assembly.GetType('TermLink.Agent.RegistryControlClient', $true)
    $request = [Activator]::CreateInstance($requestType)
    $request.Command = [Enum]::Parse($commandType, 'RefreshProductionStatus')
    $send = @($clientType.GetMethods() | Where-Object { $_.Name -eq 'Send' -and $_.GetParameters().Count -eq 3 })
    Require-Theme ($send.Count -eq 1) 'AGENT_CONTROL_API_INVALID'
    $agent = $send[0].Invoke($null, [object[]]@($AgentControlPipe, $request, 10000))
    Require-Theme ($agent.Success -and $agent.RemoteAccessEnabled -and $agent.Production.Configured -and
        $agent.Production.AccountLinked -and [string]$agent.Production.RelayState -ceq 'Connected' -and
        [string]$agent.Production.ServerOrigin -ceq $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority)) `
        'AGENT_RELAY_NOT_READY'
    foreach ($serial in @($FirstSerial, $SecondSerial)) {
        $null = Require-Adb $serial @('shell', 'am', 'start', '-n',
            "$AndroidPackageName/dev.termlink.product.ProductActivity") 'ANDROID_ACTIVITY_UNAVAILABLE'
        $probe = $null
        $probe = Invoke-ProbeWithStartupRetry $serial
        Require-Theme ($probe.account_ready -eq $true -and
            [string]$probe.server_origin -ceq $AcceptanceOrigin.GetLeftPart([UriPartial]::Authority) -and
            @($probe.capabilities) -contains 'inspect') 'ANDROID_ORIGIN_OR_ACCOUNT_MISMATCH'
    }

    $stage = 'arm-both'
    $stem = 'TLTF_' + $RunId.Replace('-', '_').Replace('.', '_')
    $liveHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($RunId))
    $liveMarker = 'TLTF_LIVE_' + [Convert]::ToHexString($liveHash, 0, 8).ToLowerInvariant()
    $arms = @{}
    foreach ($serial in @($FirstSerial, $SecondSerial)) {
        $id = Start-Hook $serial 'open_session' @{ first_marker = "${stem}_0001";
            last_marker = "${stem}_5200"; marker_count = '5200'; minimum_tail = '0';
            history_bootstrap_mode = 'SnapshotTranscript' }
        $arms[$serial] = Wait-HookFile $serial $armPath $id 15
        Require-Theme ($arms[$serial].armed -eq $true) 'SESSION_ARM_FAILED'
    }

    $stage = 'host-start'
    $childEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($resolvedChild))
    $fixturePath = Join-Path $runRoot 'theme-fixture.ps1'
    $fixture = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
1..5200 | ForEach-Object { [Console]::WriteLine(('__MARKER_STEM___{0:D4}' -f $_)) }
[Console]::WriteLine('__LIVE_MARKER__')
[Console]::Out.Flush()
while (-not [IO.File]::Exists('theme-replies-start.trigger')) {
    if ([IO.File]::Exists('theme-host-exit.trigger')) { exit 0 }
    Start-Sleep -Milliseconds 100
}
[IO.File]::Delete('theme-replies-start.trigger')
$child = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__REPLY_CHILD_BASE64__'))
& $child --protocol-reply-relay-child ([IO.Directory]::GetCurrentDirectory())
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
while (-not [IO.File]::Exists('theme-host-exit.trigger')) { Start-Sleep -Milliseconds 100 }
'@.Replace('__MARKER_STEM__', $stem).Replace('__LIVE_MARKER__', $liveMarker).
        Replace('__REPLY_CHILD_BASE64__', $childEncoded)
    [IO.File]::WriteAllText($fixturePath, $fixture, [Text.UTF8Encoding]::new($false))
    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    $command = '"{0}" -NoLogo -NoProfile -NonInteractive -File "{1}"' -f $pwsh, $fixturePath
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $resolvedHost
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--command-base64',
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($command)),
        '--managed-shell', 'powershell-7', '--registry-pipe', $AgentControlPipe,
        '--working-directory-base64', [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($runRoot)),
        '--native-display', $resolvedDisplay, '--terminal-state', 'terminal_state_2_22_v2',
        '--broker-captures', '--diagnostics', '--timeout-ms', '1800000')) { $start.ArgumentList.Add($argument) }
    $ownedSessionHost = [Diagnostics.Process]::Start($start)
    Require-Theme ($null -ne $ownedSessionHost) 'SESSION_HOST_START_FAILED'
    $evidence.host_pid = $ownedSessionHost.Id
    $hostStartTicks = $ownedSessionHost.StartTime.ToUniversalTime().Ticks
    $hostStdout = $ownedSessionHost.StandardOutput.ReadToEndAsync()
    $hostStderr = $ownedSessionHost.StandardError.ReadToEndAsync()

    $stage = 'open-both'
    $opened = @{}
    foreach ($serial in @($FirstSerial, $SecondSerial)) {
        $item = Wait-HookFile $serial $responsePath $arms[$serial].request_id 180
        Save-Inspect ('open-' + $serial) $item
        Require-Theme ($item.ok -eq $true -and $item.last_marker_visible -eq $true -and
            $item.realtime_subscriber_ready -eq $true -and
            @($arms[$serial].baseline_session_ids) -cnotcontains [string]$item.session_id) `
            'SESSION_OPEN_FAILED'
        $opened[$serial] = $item
    }
    Require-Theme ([string]$opened[$FirstSerial].session_id -ceq
        [string]$opened[$SecondSerial].session_id) 'RECIPIENT_SESSION_MISMATCH'
    $sessionId = [string]$opened[$FirstSerial].session_id
    $evidence.session_id = $sessionId
    $first = Invoke-Hook $FirstSerial 'inspect' @{ live_marker = $liveMarker }
    $second = Invoke-Hook $SecondSerial 'inspect' @{ live_marker = $liveMarker }
    Assert-RecipientPair $first $second $sessionId $true
    Save-Inspect 'first-before' $first
    Save-Inspect 'second-before' $second
    $evidence.first_recipient_id = [string]$first.android_device_id
    $evidence.second_recipient_id = [string]$second.android_device_id
    $evidence.first_theme = [int]$first.terminal_theme_scheme
    $evidence.second_theme = [int]$second.terminal_theme_scheme

    $stage = 'relay-recovery'
    $airplane = (Require-Adb $SecondSerial @('shell', 'settings', 'get', 'global', 'airplane_mode_on') `
        'AIRPLANE_STATE_UNAVAILABLE').Trim()
    Require-Theme ($airplane -ceq '0') 'SECOND_EMULATOR_ALREADY_OFFLINE'
    $null = Require-Adb $SecondSerial @('shell', 'cmd', 'connectivity', 'airplane-mode', 'enable') `
        'SECOND_RELAY_INTERRUPT_FAILED'
    $airplaneChanged = $true
    $secondDuring = Wait-Relay $false $SecondSerial
    $firstDuring = Invoke-Hook $FirstSerial 'inspect' @{ live_marker = $liveMarker }
    Save-Inspect 'first-during' $firstDuring
    Save-Inspect 'second-during' $secondDuring
    $null = Require-Adb $SecondSerial @('shell', 'cmd', 'connectivity', 'airplane-mode', 'disable') `
        'SECOND_RELAY_RESTORE_FAILED'
    $airplaneChanged = $false
    $secondAfter = Wait-Relay $true $SecondSerial 60
    $firstAfter = Invoke-Hook $FirstSerial 'inspect' @{ live_marker = $liveMarker }
    Assert-RelayRecovery $first $firstDuring $firstAfter $second $secondDuring $secondAfter
    Assert-RecipientPair $firstAfter $secondAfter $sessionId $true
    Assert-RecipientStable $firstAfter $secondAfter $evidence.first_recipient_id `
        $evidence.second_recipient_id $evidence.first_theme $evidence.second_theme
    Save-Inspect 'first-after' $firstAfter
    Save-Inspect 'second-after' $secondAfter
    $evidence.relay_recovery = [ordered]@{
        interrupted_serial = $SecondSerial
        first_connected_generation_before = [long]$first.realtime_connected_generation
        first_connected_generation_after = [long]$firstAfter.realtime_connected_generation
        second_connected_generation_before = [long]$second.realtime_connected_generation
        second_connected_generation_after = [long]$secondAfter.realtime_connected_generation
        second_network_lost_before = [long]$second.realtime_network_lost_count
        second_network_lost_during = [long]$secondDuring.realtime_network_lost_count
        status = 'passed'
    }

    $stage = 'conpty-theme-replies'
    Write-Trigger $startTrigger ''
    $first = Wait-Marker $FirstSerial '__PROTOCOL_REPLY_RELAY_READY__' ([long]$firstAfter.live_ack) $sessionId
    $second = Wait-Marker $SecondSerial '__PROTOCOL_REPLY_RELAY_READY__' ([long]$secondAfter.live_ack) $sessionId
    $baselineTheme = 0
    foreach ($phase in @('theme-switch-arm', 'theme-switch-read', 'theme-switch-same',
        'theme-switch-unsubscribe', 'theme-switch-restore', 'theme-switch-final')) {
        Require-Theme (-not $ownedSessionHost.HasExited) 'THEME_SWITCH_HOST_EXITED'
        if ($phase -in @('theme-switch-read', 'theme-switch-same', 'theme-switch-restore')) {
            $selected = if ($phase -ceq 'theme-switch-restore') { $baselineTheme } else { 3 - $baselineTheme }
            $driver = Invoke-Program $resolvedChild @('--select-owned-display-theme', [string]$ownedSessionHost.Id,
                $resolvedHost, $resolvedDisplay, ('scheme' + $selected),
                (Join-Path $runRoot ($phase + '-window.txt'))) 30
            [IO.File]::WriteAllText((Join-Path $runRoot ($phase + '-driver.json')),
                ([ordered]@{ exit_code = $driver.ExitCode; stdout = $driver.Output;
                    stderr = $driver.Error } | ConvertTo-Json) + "`n")
            Require-Theme ($driver.ExitCode -eq 0) 'THEME_SWITCH_REAL_UI_FAILED'
        }
        Write-Trigger $replyTrigger $phase
        $marker = '__PROTOCOL_REPLY_RELAY_' + $phase.ToUpperInvariant() + '_DONE__'
        $first = Wait-Marker $FirstSerial $marker ([long]$first.live_ack) $sessionId
        $second = Wait-Marker $SecondSerial $marker ([long]$second.live_ack) $sessionId
        Assert-RecipientPair $first $second $sessionId
        Assert-RecipientStable $first $second $evidence.first_recipient_id `
            $evidence.second_recipient_id $evidence.first_theme $evidence.second_theme
        Save-Inspect ('first-' + $phase) $first
        Save-Inspect ('second-' + $phase) $second
        $probePath = Join-Path $runRoot ('protocol-replies-' + $phase + '.json')
        Require-Theme (Test-Path -LiteralPath $probePath -PathType Leaf) 'REPLY_RESULT_MISSING'
        Require-Theme ((Get-Item -LiteralPath $probePath).Length -le 256KB) 'REPLY_RESULT_TOO_LARGE'
        $probe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
        $assertion = Assert-TerminalReplyProbe -Phase $phase -Probe $probe -Inspect $first `
            -ExpectedTheme $baselineTheme
        if ($phase -ceq 'theme-switch-arm') { $baselineTheme = [int]$assertion.theme_scheme }
        $evidence.phases += [ordered]@{ phase = $phase; status = 'byte_asserted';
            first_ack = [long]$first.live_ack; second_ack = [long]$second.live_ack;
            received_bytes = $assertion.received_bytes; raw_probe = (Split-Path -Leaf $probePath) }
    }
    Write-Trigger $replyTrigger 'exit'
    $evidence.status = 'passed'
    $stage = 'complete'
} catch {
    $evidence.status = 'failed'
    $evidence.failure_code = if ($_.Exception.Message -cmatch '^[A-Z0-9_]{1,64}$') {
        $_.Exception.Message
    } else { 'UNEXPECTED_FAILURE' }
    $evidence.failure_type = $_.Exception.GetType().FullName
    if ($null -ne $_.InvocationInfo) { $evidence.failure_line = [int]$_.InvocationInfo.ScriptLineNumber }
} finally {
    if ($airplaneChanged) {
        try { $null = Require-Adb $SecondSerial @('shell', 'cmd', 'connectivity', 'airplane-mode',
            'disable') 'SECOND_RELAY_RESTORE_FAILED' }
        catch { $evidence.status = 'failed'; $evidence.failure_code = 'SECOND_RELAY_RESTORE_FAILED' }
    }
    if ($null -ne $ownedSessionHost) {
        try {
            if (-not $ownedSessionHost.HasExited) {
                Require-Theme ($ownedSessionHost.StartTime.ToUniversalTime().Ticks -eq $hostStartTicks -and
                    [string]::Equals($ownedSessionHost.MainModule.FileName, $resolvedHost,
                        [StringComparison]::OrdinalIgnoreCase)) 'OWNED_HOST_IDENTITY_MISMATCH'
                if (-not [IO.File]::Exists($replyTrigger)) { Write-Trigger $replyTrigger 'exit' }
                Write-Trigger $exitTrigger ''
                $ownedSessionHost.StandardInput.Close()
                if (-not $ownedSessionHost.WaitForExit(10000)) {
                    $evidence.host_forced_stop = $true
                    $ownedSessionHost.Kill($true)
                    $null = $ownedSessionHost.WaitForExit(5000)
                }
            }
            $evidence.host_stopped = $ownedSessionHost.HasExited
            if ($ownedSessionHost.HasExited) { $evidence.host_exit_code = $ownedSessionHost.ExitCode }
        } catch { $evidence.status = 'failed'; $evidence.failure_code = 'HOST_EXIT_NOT_EXPECTED' }
        $ownedSessionHost.Dispose()
    }
    if ($evidence.status -ceq 'passed' -and (-not $evidence.host_stopped -or
        $evidence.host_forced_stop -or $evidence.host_exit_code -ne 0)) {
        $evidence.status = 'failed'
        $evidence.failure_code = 'HOST_EXIT_NOT_EXPECTED'
    }
    $evidence.stage = $stage
    [IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 8) + "`n")
}
if ($evidence.status -cne 'passed') {
    throw "Theme authority E2E failed ($($evidence.failure_code)); evidence: $evidencePath"
}
Write-Output "Theme authority E2E passed: $evidencePath"
