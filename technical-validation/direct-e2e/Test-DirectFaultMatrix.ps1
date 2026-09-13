[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSCommandPath
$dotnet = 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Pinned .NET SDK is missing: $dotnet"
}

$project = Join-Path $projectRoot 'TermLink.DirectE2E.Model.csproj'

& $dotnet build $project --configuration Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Direct fault matrix build failed with exit code $LASTEXITCODE."
}

& $dotnet run --project $project --configuration Release --no-build --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Direct fault matrix failed with exit code $LASTEXITCODE."
}
