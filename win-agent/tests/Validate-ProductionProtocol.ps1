[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$candidate = $DotNetPath
if ($DotNetPath -eq 'dotnet') {
    $temporarySdk = Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'
    $sdks = & dotnet --list-sdks 2>$null
    if (($LASTEXITCODE -ne 0 -or -not $sdks) -and (Test-Path -LiteralPath $temporarySdk -PathType Leaf)) {
        $candidate = $temporarySdk
    }
}
& $candidate run --project (Join-Path $PSScriptRoot 'ProtocolValidation\TermLink.Protocol.Validation.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Production Protocol validation failed with exit code $LASTEXITCODE." }
