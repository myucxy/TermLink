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
if (-not (Test-Path -LiteralPath $paths.InitialAdminPassword -PathType Leaf)) {
    $env:TERMLINK_ACCEPTANCE_ADMIN_INITIAL_PASSWORD_FILE = $paths.AdminPassword
}
$composeFiles = @($paths.ComposeFile)
$includeDirectCleanup = $RemoveVolumes -or
    (Test-Path -LiteralPath $paths.DirectValkeyPassword -PathType Leaf) -or
    (Test-Path -LiteralPath $paths.DirectRedisUrl -PathType Leaf)
if ($includeDirectCleanup) {
    Set-TermLinkAcceptanceDirectComposeEnvironment `
        -Paths $paths `
        -STUNServers 'stun:stun.cloudflare.com:3478,stun:stun.l.google.com:19302'
    foreach ($directComposeFile in @(
        (Join-Path $paths.ServerRoot 'compose\direct-transport.compose.yml'),
        (Join-Path $paths.ServerRoot 'compose\acceptance.direct-transport.compose.yml')
    )) {
        if (-not (Test-Path -LiteralPath $directComposeFile -PathType Leaf)) {
            throw "Direct transport Compose cleanup overlay not found: $directComposeFile"
        }
        $composeFiles += $directComposeFile
    }
}
$arguments = @('compose')
$arguments += @($composeFiles | ForEach-Object { '-f'; $_ })
$arguments += @('down', '--remove-orphans')
if ($RemoveVolumes) { $arguments += '--volumes' }
& docker @arguments
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the production acceptance stack.' }
Write-Output 'TermLink production acceptance stack stopped.'
