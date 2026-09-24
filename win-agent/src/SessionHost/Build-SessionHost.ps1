[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($output) | Out-Null
$executable = Join-Path $output 'TermLink.SessionHost.exe'

$dotnet = $env:TERMLINK_DOTNET
if ([string]::IsNullOrWhiteSpace($dotnet)) {
    $pinned = 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe'
    $dotnet = if (Test-Path -LiteralPath $pinned -PathType Leaf) {
        $pinned
    }
    else {
        (Get-Command dotnet -ErrorAction Stop).Source
    }
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Pinned .NET SDK host not found: $dotnet"
}

& $dotnet publish `
    (Join-Path $PSScriptRoot 'TermLink.SessionHost.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --no-self-contained `
    --output $output `
    --disable-build-servers |
    Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Session Host publish failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Session Host publish did not create $executable."
}
foreach ($dependency in @('TermLink.TerminalCore.dll', 'termlink_terminal_ffi.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $dependency) -PathType Leaf)) {
        throw "Session Host publish omitted production terminal-core dependency: $dependency"
    }
}

& (Join-Path $PSScriptRoot '../../scripts/Test-ConPtyRuntime.ps1') -Directory $output
$executable
