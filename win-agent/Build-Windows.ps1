[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$candidate = $DotNetPath
if ($DotNetPath -eq 'dotnet') {
    $temporarySdk = Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'
    $sdks = & dotnet --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $sdks) {
        if (Test-Path -LiteralPath $temporarySdk -PathType Leaf) { $candidate = $temporarySdk }
    }
}
if ($candidate -ne 'dotnet' -and -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
    throw "dotnet SDK executable not found: $candidate"
}

& $candidate build (Join-Path $PSScriptRoot 'TermLink.Windows.sln') `
    --configuration $Configuration `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Windows solution build failed with exit code $LASTEXITCODE." }
