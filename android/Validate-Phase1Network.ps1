[CmdletBinding()]
param(
    [string]$JavaHome = 'D:\Dev\Env\Jdk\jdk-17.0.17',
    [string]$AndroidSdk = 'D:\Dev\Env\Android\Sdk',
    [string]$GradleUserHome = 'D:\Dev\Env\gradle\user-home',
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\docs\validation\phase1-android-network.json'),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$env:JAVA_HOME = [IO.Path]::GetFullPath($JavaHome)
$env:ANDROID_HOME = [IO.Path]::GetFullPath($AndroidSdk)
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:GRADLE_USER_HOME = [IO.Path]::GetFullPath($GradleUserHome)

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'gradlew.bat') :core-network:testDebugUnitTest --no-daemon
    if ($LASTEXITCODE -ne 0) { throw "core-network tests failed with exit code $LASTEXITCODE." }
}

$resultDirectory = Join-Path $PSScriptRoot 'core-network\build\test-results\testDebugUnitTest'
$resultFiles = @(Get-ChildItem -LiteralPath $resultDirectory -Filter 'TEST-*.xml' -File)
if ($resultFiles.Count -eq 0) { throw "No core-network test results found in $resultDirectory." }

$tests = 0
$failures = 0
$errors = 0
foreach ($file in $resultFiles) {
    [xml]$result = Get-Content -Raw -LiteralPath $file.FullName
    $tests += [int]$result.testsuite.tests
    $failures += [int]$result.testsuite.failures
    $errors += [int]$result.testsuite.errors
}
if ($tests -lt 5 -or $failures -ne 0 -or $errors -ne 0) {
    throw "core-network validation failed: tests=$tests failures=$failures errors=$errors."
}

$sourceFiles = @(
    'core-network\src\main\kotlin\dev\termlink\network\testrelay\Phase1TestProtocol.kt',
    'core-network\src\main\kotlin\dev\termlink\network\testrelay\Phase1TestSessionClient.kt',
    'core-network\src\test\kotlin\dev\termlink\network\testrelay\Phase1TestSessionClientTest.kt'
)
$hashes = [ordered]@{}
foreach ($relativePath in $sourceFiles) {
    $hash = Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $relativePath) -Algorithm SHA256
    $hashes[$relativePath.Replace('\', '/')] = $hash.Hash.ToLowerInvariant()
}

$evidence = [ordered]@{
    schema = 'termlink.phase1-android-test-network-validation'
    version = 1
    generated_at_utc = [DateTime]::UtcNow.ToString('o')
    classification = 'temporary Phase 1 test transport; not production Protobuf, Relay, Crypto, or Snapshot'
    passed = $true
    tests = $tests
    failures = $failures
    errors = $errors
    covered = @(
        'Phase 1 Magic keeps the bidirectional protocol isolated from LAN Replay V1',
        'raw output is applied before cumulative ACK',
        'disconnect resumes from the highest locally applied sequence without duplicate feed',
        'input submission completes only after the matching input_id is accepted',
        'control lease grant and release bind to RemoteTerminalSession state',
        'explicit History Gap preserves the last contiguous ACK',
        'the test endpoint never reuses the read-only LAN Replay path'
    )
    source_sha256 = $hashes
}
$resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
[IO.File]::WriteAllText(
    $resolvedEvidence,
    ($evidence | ConvertTo-Json -Depth 6) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
Write-Host "Phase 1 Android test network validation passed: $tests tests. Evidence: $resolvedEvidence"
