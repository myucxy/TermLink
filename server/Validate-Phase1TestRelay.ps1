[CmdletBinding()]
param(
    [string]$GoPath = 'D:\Dev\Env\Go\bin\go.exe',
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\docs\validation\phase1-test-relay.json')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $GoPath -PathType Leaf)) { throw "Go executable not found: $GoPath" }

Push-Location $PSScriptRoot
try {
    & $GoPath test ./...
    if ($LASTEXITCODE -ne 0) { throw "go test failed with exit code $LASTEXITCODE." }
    & $GoPath vet ./...
    if ($LASTEXITCODE -ne 0) { throw "go vet failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
}

$sourceFiles = @(
    'internal\phase1test\protocol.go',
    'internal\phase1test\fixture.go',
    'internal\phase1test\handler.go',
    'internal\phase1test\phase1test_test.go'
)
$hashes = [ordered]@{}
foreach ($relativePath in $sourceFiles) {
    $hash = Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $relativePath) -Algorithm SHA256
    $hashes[$relativePath.Replace('\', '/')] = $hash.Hash.ToLowerInvariant()
}

$evidence = [ordered]@{
    schema = 'termlink.phase1-test-relay-validation'
    version = 1
    generated_at_utc = [DateTime]::UtcNow.ToString('o')
    classification = 'temporary plaintext test Relay over fixed non-sensitive fixtures; not production Relay or Protobuf'
    passed = $true
    covered = @(
        'isolated binary Magic and strict frame bounds',
        'cumulative ACK and suffix-only resume',
        'one-time disconnect recovery',
        'matching input_id acceptance and test control lease',
        'slow-consumer FlowControl followed by bounded disconnect',
        'explicit History Gap range',
        'Bearer token and non-browser Origin boundary',
        'LAN Replay V1 tests remain unchanged and passing'
    )
    source_sha256 = $hashes
}
$resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
[IO.File]::WriteAllText(
    $resolvedEvidence,
    ($evidence | ConvertTo-Json -Depth 6) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
Write-Host "Phase 1 test Relay validation passed. Evidence: $resolvedEvidence"
