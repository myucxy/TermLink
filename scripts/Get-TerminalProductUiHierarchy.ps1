[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9._:-]{1,128}$')]
    [string]$DeviceSerial = 'emulator-5556',

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$')]
    [string]$TargetPackage,

    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$')]
    [string]$TestPackage = 'dev.termlink.terminal.nativecore.test',

    [string]$AdbPath = 'adb',
    [string]$PowerShellPath = 'pwsh',
    [string]$TestClass = 'dev.termlink.terminal.nativecore.TerminalProductUiDumpInstrumentedTest',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')]
    [string]$TestMethod = 'dumpTargetUiHierarchy',
    [string]$RelativePath = 'files/terminal-ui-hierarchy.xml',

    [ValidateRange(1024, 16777216)]
    [int]$MaxBytes = 1048576,
    [ValidateRange(1, 5000)]
    [int]$MaxNodes = 5000,
    [ValidateRange(1000, 120000)]
    [int]$AdbTimeoutMilliseconds = 30000,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-UiCapture {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Code)
    if (-not $Condition) { throw $Code }
}

Assert-UiCapture ($TestPackage -ceq 'dev.termlink.terminal.nativecore.test') 'UI_TEST_PACKAGE_UNSUPPORTED'
Assert-UiCapture ($RelativePath -ceq 'files/terminal-ui-hierarchy.xml') 'UI_CAPTURE_PATH_UNSUPPORTED'
Assert-UiCapture ($TestClass -ceq 'dev.termlink.terminal.nativecore.TerminalProductUiDumpInstrumentedTest') 'UI_TEST_CLASS_UNSUPPORTED'

function Get-ProgramInvocation {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $extension = [IO.Path]::GetExtension($FileName).ToLowerInvariant()
    if ($extension -eq '.ps1') {
        return [pscustomobject]@{
            FileName = $PowerShellPath
            # `--` keeps mock scripts from interpreting adb's `-s` as their
            # own PowerShell parameter; adb.exe never takes this branch.
            Arguments = @('-NoProfile', '-File', $FileName, '--') + $Arguments
        }
    }
    if ($extension -in @('.cmd', '.bat')) {
        return [pscustomobject]@{
            FileName = $env:ComSpec
            Arguments = @('/d', '/s', '/c', $FileName) + $Arguments
        }
    }
    return [pscustomobject]@{ FileName = $FileName; Arguments = $Arguments }
}

function Invoke-ProgramBytes {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds
    )
    $invocation = Get-ProgramInvocation -FileName $FileName -Arguments $Arguments
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $invocation.FileName
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $invocation.Arguments) { [void]$start.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        [void]($started = $process.Start())
        Assert-UiCapture $started 'UI_ADB_START_FAILED'
        $stdout = [IO.MemoryStream]::new()
        $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try { $process.Kill($true) } catch { }
            throw 'UI_ADB_TIMEOUT'
        }
        $null = $copyTask.GetAwaiter().GetResult()
        $stderr = $errorTask.GetAwaiter().GetResult()
        $bytes = $stdout.ToArray()
        $stdout.Dispose()
        Assert-UiCapture ($process.ExitCode -eq 0) 'UI_ADB_COMMAND_FAILED'
        return $bytes
    }
    finally {
        if ($started -and -not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
        $process.Dispose()
    }
}

function Read-ValidatedHierarchy {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    Assert-UiCapture ($Bytes.Length -gt 0) 'UI_HIERARCHY_EMPTY'
    Assert-UiCapture ($Bytes.Length -le $MaxBytes) 'UI_HIERARCHY_TOO_LARGE'
    $text = [Text.Encoding]::UTF8.GetString($Bytes).TrimStart([char]0xFEFF)
    Assert-UiCapture (-not [string]::IsNullOrWhiteSpace($text)) 'UI_HIERARCHY_EMPTY'

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = [int64]$MaxBytes * 2
    $document = [Xml.XmlDocument]::new()
    $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($text), $settings)
    try { $document.Load($reader) }
    catch { throw 'UI_HIERARCHY_XML_INVALID' }
    finally { $reader.Dispose() }

    $root = $document.DocumentElement
    Assert-UiCapture ($null -ne $root -and $root.LocalName -ceq 'hierarchy') 'UI_HIERARCHY_ROOT_INVALID'
    $nodes = @($document.SelectNodes('//node'))
    Assert-UiCapture ($nodes.Count -gt 0 -and $nodes.Count -le $MaxNodes) 'UI_HIERARCHY_NODE_COUNT_INVALID'
    $elements = @($document.SelectNodes('//*[self::node or self::window]'))
    $targetElements = 0
    foreach ($element in $elements) {
        $package = $element.GetAttribute('package')
        Assert-UiCapture (-not [string]::IsNullOrWhiteSpace($package)) 'UI_HIERARCHY_PACKAGE_MISSING'
        Assert-UiCapture ($package -ceq $TargetPackage) 'UI_HIERARCHY_TARGET_MISMATCH'
        $targetElements++
    }
    Assert-UiCapture ($targetElements -gt 0) 'UI_HIERARCHY_TARGET_EMPTY'
    foreach ($node in $nodes) {
        foreach ($name in @('package', 'class', 'text', 'content-desc', 'bounds', 'clickable', 'enabled',
                'checkable', 'checked', 'focused', 'password', 'selected')) {
            Assert-UiCapture ($node.HasAttribute($name)) "UI_HIERARCHY_ATTRIBUTE_MISSING_$($name.Replace('-', '_'))"
        }
        Assert-UiCapture ($node.GetAttribute('package') -ceq $TargetPackage) 'UI_HIERARCHY_NODE_TARGET_MISMATCH'
        Assert-UiCapture ($node.GetAttribute('bounds') -match '^\[-?\d+,-?\d+\]\[-?\d+,-?\d+\]$') 'UI_HIERARCHY_BOUNDS_INVALID'
        foreach ($name in @('clickable', 'enabled', 'checkable', 'checked', 'focused', 'password', 'selected')) {
            Assert-UiCapture ($node.GetAttribute($name) -match '^(true|false)$') 'UI_HIERARCHY_STATE_INVALID'
        }
        if ($node.GetAttribute('password') -ceq 'true') {
            Assert-UiCapture ([string]::IsNullOrEmpty($node.GetAttribute('text'))) 'UI_HIERARCHY_SENSITIVE_TEXT'
        }
    }
    return $text
}

function Assert-InstrumentationSucceeded {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $text = [Text.Encoding]::UTF8.GetString($Bytes)
    Assert-UiCapture (-not [string]::IsNullOrWhiteSpace($text)) 'UI_INSTRUMENTATION_OUTPUT_EMPTY'
    # am instrument reports Activity.RESULT_OK (-1) for a successful run.
    Assert-UiCapture ($text -match '(?m)^INSTRUMENTATION_CODE:\s*-1\s*$') 'UI_INSTRUMENTATION_CODE_FAILED'
    Assert-UiCapture ($text -match '(?m)\bOK\s*\(\s*1\s+test\s*\)') 'UI_INSTRUMENTATION_RESULT_NOT_OK'
    foreach ($failure in @('INSTRUMENTATION_FAILED', 'FAILURES', 'shortMsg=', 'Error=')) {
        Assert-UiCapture (-not $text.Contains($failure)) 'UI_INSTRUMENTATION_RESULT_FAILED'
    }
}

$classMethod = "$TestClass#$TestMethod"
$runnerComponent = "$TestPackage/androidx.test.runner.AndroidJUnitRunner"
$clearArgs = @('-s', $DeviceSerial, 'shell', 'run-as', $TestPackage, 'rm', '-f', $RelativePath)
$null = Invoke-ProgramBytes -FileName $AdbPath -Arguments $clearArgs -TimeoutMilliseconds $AdbTimeoutMilliseconds
$instrumentationArgs = @(
    '-s', $DeviceSerial, 'shell', 'am', 'instrument', '-w', '-r',
    '-e', 'class', $classMethod,
    '-e', 'target_package', $TargetPackage,
    $runnerComponent
)
$instrumentation = Invoke-ProgramBytes -FileName $AdbPath -Arguments $instrumentationArgs -TimeoutMilliseconds $AdbTimeoutMilliseconds
Assert-InstrumentationSucceeded -Bytes $instrumentation
$captureArgs = @('-s', $DeviceSerial, 'exec-out', 'run-as', $TestPackage, 'cat', $RelativePath)
$capture = Invoke-ProgramBytes -FileName $AdbPath -Arguments $captureArgs -TimeoutMilliseconds $AdbTimeoutMilliseconds
$xml = Read-ValidatedHierarchy -Bytes $capture
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    if (-not [string]::IsNullOrWhiteSpace($parent)) { $null = New-Item -ItemType Directory -Force -Path $parent }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $xml, [Text.UTF8Encoding]::new($false))
}
$xml
