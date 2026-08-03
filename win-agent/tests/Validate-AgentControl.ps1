[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

& $DotNetPath run --project `
    (Join-Path $PSScriptRoot 'AgentControlValidation\TermLink.AgentControl.Validation.csproj') `
    --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Agent control validation failed with exit code $LASTEXITCODE."
}
