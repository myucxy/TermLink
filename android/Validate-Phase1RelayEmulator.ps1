[CmdletBinding()]
param(
    [string]$DeviceSerial = 'emulator-5554',
    [string]$AdbPath = 'D:\Dev\Env\Android\Sdk\platform-tools\adb.exe',
    [string]$ApkPath = (Join-Path $PSScriptRoot 'app\build\outputs\apk\debug\app-debug.apk'),
    [string]$RelayAddress = 'ws://10.0.2.2:8080',
    [string]$TestToken = 'termlink-lan-test',
    [ValidateRange(5, 60)]
    [int]$ScenarioTimeoutSeconds = 20,
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\docs\validation\phase1-android-relay-emulator.json')
)

$ErrorActionPreference = 'Stop'
$packageName = 'dev.termlink.harness'
$activityName = "$packageName/.MainActivity"
$extraPrefix = 'dev.termlink.harness.extra.'
$deviceDump = '/sdcard/termlink-phase1-relay-ui.xml'

function Invoke-Adb {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & $AdbPath -s $DeviceSerial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "adb $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    @($output)
}

function Get-UiDump {
    & $AdbPath -s $DeviceSerial shell uiautomator dump $deviceDump 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return '' }
    ((& $AdbPath -s $DeviceSerial shell cat $deviceDump 2>&1) -join "`n")
}

function Wait-Ui {
    param(
        [Parameter(Mandatory)][scriptblock]$Predicate,
        [Parameter(Mandatory)][string]$Description
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($ScenarioTimeoutSeconds)
    $ui = ''
    do {
        $ui = Get-UiDump
        if (& $Predicate $ui) { return $ui }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description. Last UI dump: $ui"
}

function Invoke-TextButton {
    param(
        [Parameter(Mandatory)][string]$Ui,
        [Parameter(Mandatory)][string]$Text
    )

    $escaped = [Security.SecurityElement]::Escape($Text)
    if ($Ui -notmatch ('text="{0}"[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"' -f [regex]::Escape($escaped))) {
        throw "Button text '$Text' was not found in the UI dump."
    }
    $x = [int](($matches[1] -as [int]) + ($matches[3] -as [int])) / 2
    $y = [int](($matches[2] -as [int]) + ($matches[4] -as [int])) / 2
    $null = Invoke-Adb -Arguments @('shell', 'input', 'tap', "$x", "$y")
}

function Start-Scenario {
    param([Parameter(Mandatory)][string]$Scenario)

    $null = Invoke-Adb -Arguments @('shell', 'am', 'force-stop', $packageName)
    $start = Invoke-Adb -Arguments @(
        'shell', 'am', 'start', '-W', '-n', $activityName,
        '--es', "${extraPrefix}MODE", 'phase1',
        '--es', "${extraPrefix}RELAY_ADDRESS", $RelayAddress,
        '--es', "${extraPrefix}TEST_TOKEN", $TestToken,
        '--es', "${extraPrefix}PHASE1_SCENARIO", $Scenario,
        '--ez', "${extraPrefix}AUTO_PHASE1_CONNECT", 'true'
    )
    if (($start -join "`n") -notmatch 'Status:\s+ok') {
        throw "Activity start failed for ${Scenario}: $($start -join [Environment]::NewLine)"
    }
}

if (-not (Test-Path -LiteralPath $AdbPath -PathType Leaf)) { throw "adb not found: $AdbPath" }
$resolvedApk = (Resolve-Path -LiteralPath $ApkPath).Path
$null = Invoke-Adb -Arguments @('get-state')
$sdk = [int]((Invoke-Adb -Arguments @('shell', 'getprop', 'ro.build.version.sdk')) -join '').Trim()
if ($sdk -lt 26) { throw "Harness requires API 26 or newer; device reports API $sdk." }
$install = Invoke-Adb -Arguments @('install', '-r', $resolvedApk)
if (($install -join "`n") -notmatch 'Success') { throw 'APK install did not report success.' }
$null = Invoke-Adb -Arguments @('logcat', '-c')

Start-Scenario -Scenario 'DISCONNECT_ONCE'
$ui = Wait-Ui -Description 'resumed Read only state at ACK 13' -Predicate {
    param($value)
    $value.Contains('text="Read only"') -and $value.Contains('ACK 13')
}
Invoke-TextButton -Ui $ui -Text '申请控制'
$ui = Wait-Ui -Description 'Controlling state' -Predicate {
    param($value)
    $value.Contains('text="Controlling"') -and $value.Contains('ACK 13')
}
Invoke-TextButton -Ui $ui -Text '发送测试输入'
$ui = Wait-Ui -Description 'accepted test input' -Predicate {
    param($value)
    $value -match 'text="Input accepted ([1-9][0-9]*)"'
}
$null = $ui -match 'text="Input accepted ([1-9][0-9]*)"'
$acceptedInputCount = [int]$matches[1]

Start-Scenario -Scenario 'HISTORY_GAP'
$null = Wait-Ui -Description 'History Gap at missing 5..6' -Predicate {
    param($value)
    $value.Contains('text="History gap"') -and $value.Contains('ACK 4') -and $value.Contains('missing 5..6')
}

Start-Scenario -Scenario 'EXITED'
$null = Wait-Ui -Description 'Exited state at ACK 14' -Predicate {
    param($value)
    $value.Contains('text="Exited"') -and $value.Contains('ACK 14') -and $value.Contains('exit 17')
}

$logcat = Invoke-Adb -Arguments @('logcat', '-d', '-v', 'brief')
$fatal = @($logcat | Select-String -Pattern 'FATAL EXCEPTION|ANR in dev\.termlink\.harness')
if ($fatal.Count -gt 0) {
    throw "Phase 1 Relay validation produced a crash or ANR: $($fatal -join [Environment]::NewLine)"
}

$evidence = [ordered]@{
    schema = 'termlink.phase1-android-relay-emulator-validation'
    version = 1
    generated_at_utc = [DateTime]::UtcNow.ToString('o')
    classification = 'API emulator plus temporary plaintext test Relay; not production Relay, E2EE, or hardware validation'
    passed = $true
    relay_address = $RelayAddress
    device = [ordered]@{
        serial = $DeviceSerial
        sdk = $sdk
        model = ((Invoke-Adb -Arguments @('shell', 'getprop', 'ro.product.model')) -join '').Trim()
        abi = ((Invoke-Adb -Arguments @('shell', 'getprop', 'ro.product.cpu.abi')) -join '').Trim()
    }
    apk_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedApk).Hash.ToLowerInvariant()
    scenarios = @(
        [ordered]@{
            name = 'disconnect-once'
            final_state = 'ReadOnly then Controlling'
            last_acked_sequence = 13
            input_accepted_count = $acceptedInputCount
        },
        [ordered]@{
            name = 'history-gap'
            final_state = 'HistoryGap'
            last_acked_sequence = 4
            missing_start = 5
            missing_end = 6
        },
        [ordered]@{
            name = 'exited'
            final_state = 'Exited'
            last_acked_sequence = 14
            exit_code = 17
        }
    )
    crash_or_anr_count = 0
}
$destination = [IO.Path]::GetFullPath($EvidencePath)
[IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
[IO.File]::WriteAllText(
    $destination,
    ($evidence | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$evidence | ConvertTo-Json -Depth 8
