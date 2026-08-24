[CmdletBinding()]
param([string]$DotNetPath = 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe')

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
    throw "Pinned .NET SDK is unavailable: $DotNetPath"
}
if ((& $DotNetPath --version | Select-Object -First 1).Trim() -ne '8.0.303') {
    throw 'Production PC heartbeat validation requires .NET SDK 8.0.303.'
}

$project = Join-Path $PSScriptRoot 'ProductionPcHeartbeatValidation\TermLink.ProductionPcHeartbeat.Validation.csproj'
& $DotNetPath run --project $project --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Production PC heartbeat validation failed with exit code $LASTEXITCODE."
}
