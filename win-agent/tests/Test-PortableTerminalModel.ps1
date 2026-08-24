[CmdletBinding()]
param([string]$DotNetPath = 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe')

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..'))
$project = Join-Path $PSScriptRoot 'PortableTerminalModelValidation\TermLink.PortableTerminalModel.Validation.csproj'

if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
    throw "Pinned .NET SDK executable was not found: $DotNetPath"
}
$sdkVersion = (& $DotNetPath --version | Select-Object -First 1).Trim()
if ($sdkVersion -ne '8.0.303') {
    throw "Portable terminal validation requires .NET SDK 8.0.303; found $sdkVersion."
}

& $DotNetPath run --project $project --configuration Release -- --repo-root $repositoryRoot
if ($LASTEXITCODE -ne 0) {
    throw "Portable terminal model validation failed with exit code $LASTEXITCODE."
}

$git = Get-Command git -ErrorAction Stop
& $git.Source -C $repositoryRoot diff --check -- `
    'win-agent/src/Agent/PortableTerminalModelV1.cs' `
    'win-agent/tests/PortableTerminalModelValidation' `
    'win-agent/tests/Test-PortableTerminalModel.ps1'
if ($LASTEXITCODE -ne 0) {
    throw "Portable terminal model diff check failed with exit code $LASTEXITCODE."
}
