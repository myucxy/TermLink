[CmdletBinding()]
param(
    [string]$DotNetPath = 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $PSScriptRoot 'ProductionTerminalHistoryValidation\TermLink.ProductionTerminalHistory.Validation.csproj'

if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
    throw "Pinned .NET SDK is unavailable: $DotNetPath"
}

& $DotNetPath run --project $project --configuration Release -- `
    2>&1 | Write-Output
if ($LASTEXITCODE -ne 0) {
    throw 'Production terminal history validation failed.'
}
