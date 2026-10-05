[CmdletBinding()]
param(
    [string]$CargoPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-TermLinkCargo {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = [IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "CargoPath was not found: $resolved"
        }
        return $resolved
    }

    $managed = 'D:\Dev\Env\Rust\cargo\bin\cargo.exe'
    if (Test-Path -LiteralPath $managed -PathType Leaf) { return $managed }
    $command = Get-Command cargo.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) { $command = Get-Command cargo -ErrorAction SilentlyContinue }
    if ($null -eq $command) {
        throw 'Rust cargo is required to build terminal-core. Pass -CargoPath or install the pinned toolchain under D:\Dev.'
    }
    return [IO.Path]::GetFullPath($command.Source)
}

$agentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $agentRoot '..'))
$null = & (Join-Path $repositoryRoot 'scripts\Verify-TerminalCoreCheckout.ps1')
$coreRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '..\terminal-core'))
$manifest = Join-Path $coreRoot 'Cargo.toml'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw "Clone the private terminal-core repository next to the TermLink application repository: $manifest"
}

$cargo = Resolve-TermLinkCargo -ExplicitPath $CargoPath
Push-Location $coreRoot
try {
    & $cargo '+1.97.1' build `
        --manifest-path $manifest `
        -p termlink-terminal-ffi `
        --release `
        --locked `
        --offline `
        --target x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) {
        throw "terminal-core native build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$nativePath = Join-Path $coreRoot `
    'target\x86_64-pc-windows-msvc\release\termlink_terminal_ffi.dll'
if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
    throw "terminal-core native output was not produced: $nativePath"
}

$publishedPath = $nativePath
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $publishedPath = Join-Path $destination 'termlink_terminal_ffi.dll'
    Copy-Item -LiteralPath $nativePath -Destination $publishedPath -Force
}

[pscustomobject]@{
    CargoPath = $cargo
    Toolchain = '1.97.1'
    Target = 'x86_64-pc-windows-msvc'
    NativeLibrary = [IO.Path]::GetFullPath($publishedPath)
    Sha256 = (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
