[CmdletBinding()]
param(
    [string]$CargoPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$requiredToolchain = '1.97.1'
$targetTriple = 'x86_64-pc-windows-msvc'

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
        throw 'Rust cargo is required to build network-core. Pass -CargoPath or install the pinned toolchain under D:\Dev.'
    }
    return [IO.Path]::GetFullPath($command.Source)
}

function Resolve-PinnedCargoArguments {
    param([Parameter(Mandatory)][string]$Cargo)

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $rustupOutput = @(& $Cargo "+$requiredToolchain" --version 2>&1)
        $rustupExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($rustupExitCode -eq 0 -and ($rustupOutput -join "`n") -match "cargo\s+$([regex]::Escape($requiredToolchain))(?:\s|$)") {
        return ,@("+$requiredToolchain")
    }

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $directOutput = @(& $Cargo --version 2>&1)
        $directExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($directExitCode -ne 0 -or
        -not (($directOutput -join "`n") -match "cargo\s+$([regex]::Escape($requiredToolchain))(?:\s|$)")) {
        throw "CargoPath must resolve to the pinned Rust $requiredToolchain toolchain: $Cargo"
    }
    return ,@()
}

$agentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $agentRoot '..'))
$manifest = Join-Path $repositoryRoot 'network-core\Cargo.toml'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw "network-core manifest was not found: $manifest"
}

$cargo = Resolve-TermLinkCargo -ExplicitPath $CargoPath
$cargoPrefix = @(Resolve-PinnedCargoArguments -Cargo $cargo)
& $cargo @cargoPrefix build `
    --manifest-path $manifest `
    -p termlink-direct-ffi `
    --release `
    --locked `
    --offline `
    --target $targetTriple
if ($LASTEXITCODE -ne 0) {
    throw "network-core native build failed with exit code $LASTEXITCODE."
}

$nativePath = Join-Path $repositoryRoot `
    "network-core\target\$targetTriple\release\termlink_direct_ffi.dll"
if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf)) {
    throw "network-core native output was not produced: $nativePath"
}
$sourceHash = (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant()

$publishedPath = $nativePath
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $publishedPath = Join-Path $destination 'termlink_direct_ffi.dll'
    Copy-Item -LiteralPath $nativePath -Destination $publishedPath -Force
    $publishedHash = (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($sourceHash, $publishedHash, [StringComparison]::Ordinal)) {
        throw 'The copied network-core native library does not match the freshly built source artifact.'
    }
}

[pscustomobject]@{
    CargoPath = $cargo
    Toolchain = $requiredToolchain
    Target = $targetTriple
    NativeLibrary = [IO.Path]::GetFullPath($publishedPath)
    Sha256 = $sourceHash
}
