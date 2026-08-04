[CmdletBinding()]
param(
    [switch]$RemoveVolumes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ProductionAcceptance.Common.psm1') -Force

$paths = Get-TermLinkAcceptancePaths
if (-not (Test-Path -LiteralPath $paths.Marker -PathType Leaf)) {
    throw 'The managed TermLink production acceptance directory does not exist.'
}
Set-TermLinkAcceptanceComposeEnvironment -Paths $paths
$arguments = @('compose', '-f', $paths.ComposeFile, 'down', '--remove-orphans')
if ($RemoveVolumes) { $arguments += '--volumes' }
& docker @arguments
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the production acceptance stack.' }
Write-Output 'TermLink production acceptance stack stopped.'
