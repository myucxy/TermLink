[CmdletBinding()]
param(
  [switch]$RequireInfrastructure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$requiredEnvironment = @(
  'TERMLINK_INTEGRATION_DATABASE_URL',
  'TERMLINK_INTEGRATION_REDIS_URL'
)
$missingEnvironment = @($requiredEnvironment | Where-Object {
  [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
})
if ($RequireInfrastructure -and $missingEnvironment.Count -gt 0) {
  throw "Missing integration environment: $($missingEnvironment -join ', ')"
}

$ensureDevEnv = 'D:\Dev\Scripts\Ensure-DevEnv.ps1'
if (Test-Path -LiteralPath $ensureDevEnv -PathType Leaf) {
  $probeJson = & $ensureDevEnv -Tool go -Json
  if ($LASTEXITCODE -ne 0) {
    throw 'The canonical Go toolchain is unavailable.'
  }
  $goProbe = $probeJson | ConvertFrom-Json
  $goPath = [string]$goProbe.Path
}
else {
  $goCommand = Get-Command go -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if ($null -eq $goCommand) {
    throw 'The Go toolchain is unavailable.'
  }
  $goPath = [string]$goCommand.Source
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $repositoryRoot 'server')
try {
  & $goPath test ./internal/api -run '^TestSameAccountDirectUseE2EGate$' -count=1 -v
  $testExitCode = $LASTEXITCODE
}
finally {
  Pop-Location
}
exit $testExitCode
