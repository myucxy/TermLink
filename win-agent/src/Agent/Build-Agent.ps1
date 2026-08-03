[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler not found: $compiler"
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$executable = Join-Path $output 'TermLink.Agent.exe'
& $compiler `
    /nologo `
    /target:exe `
    /platform:x64 `
    /optimize+ `
    /r:System.Core.dll `
    /r:System.Security.dll `
    "/out:$executable" `
    (Join-Path $PSScriptRoot '..\Common\LocalIpc.cs') `
    (Join-Path $PSScriptRoot '..\AgentControl\AgentControl.cs') `
    (Join-Path $PSScriptRoot 'CurrentUserPipe.cs') `
    (Join-Path $PSScriptRoot 'SessionRegistrationStore.cs') `
    (Join-Path $PSScriptRoot 'SessionRegistry.cs') `
    (Join-Path $PSScriptRoot 'SessionAgent.cs')
if ($LASTEXITCODE -ne 0) {
    throw "Agent compilation failed with exit code $LASTEXITCODE."
}

$executable
