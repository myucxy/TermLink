[CmdletBinding()]
param(
    [string]$JavaHome = 'D:\Dev\Env\Jdk\jdk-17.0.17',
    [string]$AndroidSdk = 'D:\Dev\Env\Android\Sdk',
    [string]$GradleUserHome = 'D:\Dev\Env\gradle\user-home',
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\docs\validation\phase1-android-session.json'),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$env:JAVA_HOME = [IO.Path]::GetFullPath($JavaHome)
$env:ANDROID_HOME = [IO.Path]::GetFullPath($AndroidSdk)
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:GRADLE_USER_HOME = [IO.Path]::GetFullPath($GradleUserHome)

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'gradlew.bat') :core-session:testDebugUnitTest --no-daemon
    if ($LASTEXITCODE -ne 0) { throw "core-session tests failed with exit code $LASTEXITCODE." }
}

$resultDirectory = Join-Path $PSScriptRoot 'core-session\build\test-results\testDebugUnitTest'
$resultFiles = @(Get-ChildItem -LiteralPath $resultDirectory -Filter 'TEST-*.xml' -File)
if ($resultFiles.Count -eq 0) { throw "No core-session test results found in $resultDirectory." }

$tests = 0
$failures = 0
$errors = 0
foreach ($file in $resultFiles) {
    [xml]$result = Get-Content -Raw -LiteralPath $file.FullName
    $tests += [int]$result.testsuite.tests
    $failures += [int]$result.testsuite.failures
    $errors += [int]$result.testsuite.errors
}
if ($tests -lt 11 -or $failures -ne 0 -or $errors -ne 0) {
    throw "core-session validation failed: tests=$tests failures=$failures errors=$errors."
}

$sourceFiles = @(
    'core-session\src\main\kotlin\dev\termlink\session\RemoteSessionModels.kt',
    'core-session\src\main\kotlin\dev\termlink\session\RemoteTerminalSession.kt',
    'core-session\src\test\kotlin\dev\termlink\session\RemoteTerminalSessionTest.kt'
)
$hashes = [ordered]@{}
foreach ($relativePath in $sourceFiles) {
    $hash = Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $relativePath) -Algorithm SHA256
    $hashes[$relativePath.Replace('\', '/')] = $hash.Hash.ToLowerInvariant()
}

$evidence = [ordered]@{
    schema = 'termlink.phase1-android-session-validation'
    version = 1
    generated_at_utc = [DateTime]::UtcNow.ToString('o')
    classification = 'client session state machine; production Relay and Snapshot formats remain unfrozen'
    passed = $true
    tests = $tests
    failures = $failures
    errors = $errors
    resize_debounce_milliseconds = '100..200'
    covered = @(
        'feed completes before cumulative ACK advances',
        'duplicate output is discarded',
        'implicit and explicit gaps do not advance ACK',
        'reconnect resumes from the highest locally applied sequence',
        'bounded raw replay starts at sequence 1 and clears the engine',
        'mid-stream reset is rejected until a portable Snapshot format is frozen',
        'input requires an unexpired control lease and a unique input_id',
        'failed input is not retried automatically',
        'terminal size events are distinct and debounced',
        'exited sessions reject input and further output',
        'an Ended lifecycle remains Exited when its final ACK transport fails'
    )
    source_sha256 = $hashes
}
$resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
$evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
Write-Host "Phase 1 Android session validation passed: $tests tests. Evidence: $resolvedEvidence"
