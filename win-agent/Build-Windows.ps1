[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$dotNetPathWasExplicit = $PSBoundParameters.ContainsKey('DotNetPath')
. (Join-Path $PSScriptRoot 'scripts\Resolve-DotNetSdk.ps1')
$candidate = Resolve-TermLinkDotNetSdk -ProjectRoot $PSScriptRoot `
    -DotNetPath $DotNetPath -DotNetPathWasExplicit:$dotNetPathWasExplicit

& $candidate build (Join-Path $PSScriptRoot 'TermLink.Windows.sln') `
    --configuration $Configuration `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Windows solution build failed with exit code $LASTEXITCODE." }

& $candidate run `
    --project (Join-Path $PSScriptRoot 'tests\WindowsSetupValidation\TermLink.WindowsSetup.Validation.csproj') `
    --configuration $Configuration `
    --no-build `
    --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Windows setup validation failed with exit code $LASTEXITCODE."
}
