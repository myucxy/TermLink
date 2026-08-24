[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler not found: $compiler"
}

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($output) | Out-Null
$executable = Join-Path $output 'TermLink.SessionHost.exe'
& $compiler `
    /nologo `
    /target:exe `
    /platform:x64 `
    /optimize+ `
    "/out:$executable" `
    (Join-Path $PSScriptRoot '..\AgentControl\AgentControl.cs') `
    (Join-Path $PSScriptRoot '..\Common\LocalIpc.cs') `
    (Join-Path $PSScriptRoot 'TerminalTitleTracker.cs') `
    (Join-Path $PSScriptRoot 'SessionHost.cs')
if ($LASTEXITCODE -ne 0) {
    throw "Session Host compilation failed with exit code $LASTEXITCODE."
}

$executable
