[CmdletBinding()]
param(
    [string]$PowerShellPath = 'pwsh'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-UiSelfTest {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Write-Utf8 {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory)][string]$GetScript,
        [Parameter(Mandatory)][string]$MockAdb,
        [Parameter(Mandatory)][string]$Shell,
        [Parameter(Mandatory)][string]$TargetPackage,
        [Parameter(Mandatory)][string]$ExpectedCode
    )
    $result = & $Shell -NoProfile -File $GetScript `
        -DeviceSerial 'mock-serial' -TargetPackage $TargetPackage `
        -TestPackage 'dev.termlink.terminal.nativecore.test' `
        -AdbPath $MockAdb -PowerShellPath $Shell 2>&1
    Assert-UiSelfTest ($LASTEXITCODE -ne 0) "expected failure did not occur: $ExpectedCode"
    Assert-UiSelfTest ((@($result) -join ' ').Contains($ExpectedCode)) `
        "failure code missing: $ExpectedCode; output=$(@($result) -join ' ')"
}

$getScript = Join-Path $PSScriptRoot 'Get-TerminalProductUiHierarchy.ps1'
Assert-UiSelfTest (Test-Path -LiteralPath $getScript -PathType Leaf) 'UI_GET_SCRIPT_MISSING'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($getScript, [ref]$tokens, [ref]$errors)
Assert-UiSelfTest ($null -ne $ast -and $errors.Count -eq 0) 'UI_GET_SCRIPT_PARSE_FAILED'

$root = Join-Path ([IO.Path]::GetTempPath()) "termlink-ui-hierarchy-selftest-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Force -Path $root
$mockXml = Join-Path $root 'capture.xml'
$mockAdb = Join-Path $root 'mock-adb.ps1'
$shell = (Get-Process -Id $PID).Path
$targetPackage = 'dev.termlink.harness'
$testPackage = 'dev.termlink.terminal.nativecore.test'
$cleanupSafe = $false
try {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $relativeRoot = [IO.Path]::GetRelativePath($tempRoot, $resolvedRoot)
    Assert-UiSelfTest ($relativeRoot -match '^termlink-ui-hierarchy-selftest-[0-9a-f]{32}$') 'UI_SELFTEST_CLEANUP_PATH_UNSAFE'
    $cleanupSafe = $true

    $mockSource = @'
$arguments = @($args | Where-Object { $_ -ne '--' })
$xmlPath = [Environment]::GetEnvironmentVariable('TERMLINK_UI_MOCK_XML')
$expectedTarget = [Environment]::GetEnvironmentVariable('TERMLINK_UI_MOCK_TARGET')
$expectedComponent = [Environment]::GetEnvironmentVariable('TERMLINK_UI_MOCK_COMPONENT')
if ($arguments -contains 'shell' -and $arguments -contains 'rm') { exit 0 }
if ($arguments -contains 'shell') {
    $targetIndex = [Array]::IndexOf($arguments, 'target_package')
    $classIndex = [Array]::IndexOf($arguments, 'class')
    $component = $arguments[$arguments.Length - 1]
    if ($targetIndex -lt 0 -or $arguments[$targetIndex + 1] -cne $expectedTarget -or
        $classIndex -lt 0 -or $component -cne $expectedComponent) { exit 21 }
    Write-Output 'INSTRUMENTATION_STATUS: id=AndroidJUnitRunner'
    Write-Output 'INSTRUMENTATION_STATUS_CODE: 0'
    Write-Output 'INSTRUMENTATION_RESULT: stream='
    Write-Output 'OK (1 test)'
    Write-Output 'INSTRUMENTATION_CODE: -1'
    exit 0
}
if ($arguments -contains 'exec-out') {
    [Console]::Write([IO.File]::ReadAllText($xmlPath))
    exit 0
}
exit 22
'@
    Write-Utf8 -Path $mockAdb -Text $mockSource
    $env:TERMLINK_UI_MOCK_XML = $mockXml
    $env:TERMLINK_UI_MOCK_TARGET = $targetPackage
    $env:TERMLINK_UI_MOCK_COMPONENT = "$testPackage/androidx.test.runner.AndroidJUnitRunner"

    $validXml = @"
<?xml version="1.0" encoding="utf-8"?>
<hierarchy rotation="0"><window package="$targetPackage" bounds="[0,0][1080,2400]"><node package="$targetPackage" class="android.view.View" text="" content-desc="menu" bounds="[12,24][320,180]" clickable="true" enabled="true" checkable="true" checked="true" focused="false" password="false" selected="false" /><node package="$targetPackage" class="android.widget.EditText" text="" content-desc="password" bounds="[12,200][320,260]" clickable="true" enabled="true" checkable="false" checked="false" focused="true" password="true" selected="false" /></window></hierarchy>
"@
    Write-Utf8 -Path $mockXml -Text $validXml.Trim()
    $valid = & $shell -NoProfile -File $getScript `
        -DeviceSerial 'mock-serial' -TargetPackage $targetPackage `
        -TestPackage $testPackage -AdbPath $mockAdb -PowerShellPath $shell 2>&1
    Assert-UiSelfTest ($LASTEXITCODE -eq 0) "valid mock ADB failed: $(@($valid) -join ' ')"
    $validText = @($valid) -join "`n"
    Assert-UiSelfTest ($validText.Contains('<hierarchy') -and $validText.Contains(('package="' + $targetPackage + '"')) -and
        $validText.Contains('checkable="true" checked="true"') -and $validText.Contains('password="true"') -and
        -not $validText.Contains('do-not-leak')) 'valid hierarchy was not returned with safe state fields'
    Write-Host 'PASS valid mock instrumentation and hierarchy parser'

    $wrongXml = $validXml.Replace($targetPackage, 'dev.other.app')
    Write-Utf8 -Path $mockXml -Text $wrongXml.Trim()
    Invoke-ExpectedFailure -GetScript $getScript -MockAdb $mockAdb -Shell $shell `
        -TargetPackage $targetPackage -ExpectedCode 'UI_HIERARCHY_TARGET_MISMATCH'
    Write-Host 'PASS wrong target package rejected'

    Write-Utf8 -Path $mockXml -Text '<hierarchy><window package="dev.termlink.harness"><node></hierarchy>'
    Invoke-ExpectedFailure -GetScript $getScript -MockAdb $mockAdb -Shell $shell `
        -TargetPackage $targetPackage -ExpectedCode 'UI_HIERARCHY_XML_INVALID'
    Write-Host 'PASS malformed hierarchy rejected'

    $oversized = '<hierarchy><window package="' + $targetPackage + '"><node package="' + $targetPackage + '" text="' + ('x' * 1100000) + '" content-desc="" bounds="[0,0][1,1]" clickable="false" enabled="true" /></window></hierarchy>'
    Write-Utf8 -Path $mockXml -Text $oversized
    Invoke-ExpectedFailure -GetScript $getScript -MockAdb $mockAdb -Shell $shell `
        -TargetPackage $targetPackage -ExpectedCode 'UI_HIERARCHY_TOO_LARGE'
    Write-Host 'PASS oversized hierarchy rejected'

    $sensitiveXml = $validXml.Replace('class="android.widget.EditText" text=""', 'class="android.widget.EditText" text="do-not-leak"')
    Write-Utf8 -Path $mockXml -Text $sensitiveXml.Trim()
    Invoke-ExpectedFailure -GetScript $getScript -MockAdb $mockAdb -Shell $shell `
        -TargetPackage $targetPackage -ExpectedCode 'UI_HIERARCHY_SENSITIVE_TEXT'
    Write-Host 'PASS password text rejected'
}
finally {
    Remove-Item Env:TERMLINK_UI_MOCK_XML -ErrorAction SilentlyContinue
    Remove-Item Env:TERMLINK_UI_MOCK_TARGET -ErrorAction SilentlyContinue
    Remove-Item Env:TERMLINK_UI_MOCK_COMPONENT -ErrorAction SilentlyContinue
    if ($cleanupSafe -and (Test-Path -LiteralPath $root)) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

'Terminal product UI hierarchy mock self-test passed; no ADB was started.'
