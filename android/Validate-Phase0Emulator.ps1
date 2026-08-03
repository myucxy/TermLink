[CmdletBinding()]
param(
    [string]$DeviceSerial = 'emulator-5554',
    [string]$AdbPath = 'D:\Dev\Env\Android\Sdk\platform-tools\adb.exe',
    [string]$ApkPath = (Join-Path $PSScriptRoot 'app\build\outputs\apk\debug\app-debug.apk'),
    [ValidateRange(5, 60)]
    [int]$CaseTimeoutSeconds = 20,
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\docs\validation\phase0-android-emulator.json')
)

$ErrorActionPreference = 'Stop'
$packageName = 'dev.termlink.harness'
$activityName = "$packageName/.MainActivity"
$caseExtra = 'dev.termlink.harness.extra.CASE_ID'
$autoReplayExtra = 'dev.termlink.harness.extra.AUTO_REPLAY'
$deviceDump = '/sdcard/termlink-phase0-ui.xml'
$goldenRoot = Join-Path $PSScriptRoot '..\testdata\terminal-golden-streams'

function Invoke-Adb {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & $AdbPath -s $DeviceSerial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "adb $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    @($output)
}

if (-not (Test-Path -LiteralPath $AdbPath -PathType Leaf)) {
    throw "adb not found: $AdbPath"
}
$resolvedApk = (Resolve-Path -LiteralPath $ApkPath).Path
$null = Invoke-Adb -Arguments @('get-state')
$sdk = [int]((Invoke-Adb -Arguments @('shell', 'getprop', 'ro.build.version.sdk')) -join '').Trim()
if ($sdk -lt 26) { throw "Harness requires API 26 or newer; device reports API $sdk." }

$installOutput = Invoke-Adb -Arguments @('install', '-r', $resolvedApk)
if (($installOutput -join "`n") -notmatch 'Success') {
    throw "APK install did not report success: $($installOutput -join [Environment]::NewLine)"
}

$results = foreach ($directory in Get-ChildItem -LiteralPath $goldenRoot -Directory | Sort-Object Name) {
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $directory.FullName 'manifest.json') |
        ConvertFrom-Json
    $expectations = Get-Content -Raw -LiteralPath (Join-Path $directory.FullName 'expectations.json') |
        ConvertFrom-Json
    $caseName = [string]$manifest.case_name
    $details = '{0} events · {1} bytes · {2}×{3} · exit {4}' -f `
        $manifest.statistics.event_count,
        $manifest.statistics.output_bytes,
        $expectations.final_size.columns,
        $expectations.final_size.rows,
        $expectations.exit_code

    $null = Invoke-Adb -Arguments @('logcat', '-c')
    $null = Invoke-Adb -Arguments @('shell', 'am', 'force-stop', $packageName)
    $startOutput = Invoke-Adb -Arguments @(
        'shell', 'am', 'start', '-W', '-n', $activityName,
        '--es', $caseExtra, $caseName,
        '--ez', $autoReplayExtra, 'true'
    )
    if (($startOutput -join "`n") -notmatch 'Status:\s+ok') {
        throw "Activity start failed for ${caseName}: $($startOutput -join [Environment]::NewLine)"
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($CaseTimeoutSeconds)
    $ui = ''
    do {
        & $AdbPath -s $DeviceSerial shell uiautomator dump $deviceDump 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $ui = (& $AdbPath -s $DeviceSerial shell cat $deviceDump 2>&1) -join "`n"
            if ($ui.Contains('text="Verified"') -or $ui.Contains('text="Failed"')) { break }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    if (-not $ui.Contains('text="Verified"')) {
        throw "Case '$caseName' did not reach Verified. Last UI dump: $ui"
    }
    if (-not $ui.Contains(('text="{0}"' -f $caseName)) -or
        -not $ui.Contains(('text="{0}"' -f $details))) {
        throw "Case '$caseName' reported unexpected metadata. Expected: $details"
    }

    $logcat = Invoke-Adb -Arguments @('logcat', '-d', '-v', 'brief')
    $fatal = @($logcat | Select-String -Pattern 'FATAL EXCEPTION|ANR in dev\.termlink\.harness')
    if ($fatal.Count -gt 0) {
        throw "Case '$caseName' produced a crash or ANR: $($fatal -join [Environment]::NewLine)"
    }

    [ordered]@{
        case_name = $caseName
        status = 'verified'
        event_count = [int]$manifest.statistics.event_count
        output_bytes = [int]$manifest.statistics.output_bytes
        columns = [int]$expectations.final_size.columns
        rows = [int]$expectations.final_size.rows
        exit_code = [int]$expectations.exit_code
    }
}

$evidence = [ordered]@{
    format = 'termlink.phase0-android-emulator-validation'
    format_version = 1
    generated_at = [DateTimeOffset]::UtcNow.ToString('O')
    device = [ordered]@{
        serial = $DeviceSerial
        sdk = $sdk
        model = ((Invoke-Adb -Arguments @('shell', 'getprop', 'ro.product.model')) -join '').Trim()
        abi = ((Invoke-Adb -Arguments @('shell', 'getprop', 'ro.product.cpu.abi')) -join '').Trim()
    }
    apk_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedApk).Hash.ToLowerInvariant()
    summary = [ordered]@{
        case_count = @($results).Count
        verified_count = @($results | Where-Object status -EQ 'verified').Count
        crash_or_anr_count = 0
    }
    cases = @($results)
}

$destination = [IO.Path]::GetFullPath($EvidencePath)
[IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
[IO.File]::WriteAllText(
    $destination,
    ($evidence | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$evidence | ConvertTo-Json -Depth 8
