[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern(
        '^docker\.io/myucxy/termlink:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}@sha256:[0-9a-f]{64}$')]
    [string]$ServerImage,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourcePath = Join-Path $PSScriptRoot 'termlink-deploy'
$resolvedSource = [IO.Path]::GetFullPath($sourcePath)
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
if ($resolvedOutput -ceq $resolvedSource) {
    throw 'OutputPath cannot overwrite the source installer template.'
}
if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
    throw "Output already exists; use -Force to replace it: $resolvedOutput"
}

$marker = '__TERMLINK_BUNDLED_IMAGE__'
$source = [IO.File]::ReadAllText($resolvedSource)
$matches = [regex]::Matches($source, [regex]::Escape($marker))
if ($matches.Count -ne 1) {
    throw "The installer template must contain exactly one bundled-image marker; found $($matches.Count)."
}
$installer = $source.Replace($marker, $ServerImage)
if ($installer.Contains($marker, [StringComparison]::Ordinal)) {
    throw 'The generated installer still contains the bundled-image marker.'
}

$parent = Split-Path -Parent $resolvedOutput
[IO.Directory]::CreateDirectory($parent) | Out-Null
$temporary = "$resolvedOutput.tmp.$PID"
try {
    [IO.File]::WriteAllText($temporary, $installer.Replace("`r`n", "`n"),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $resolvedOutput -Force:$Force
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

$digest = (Get-FileHash -LiteralPath $resolvedOutput -Algorithm SHA256).Hash.ToLowerInvariant()
[pscustomobject]@{
    Path = $resolvedOutput
    ServerImage = $ServerImage
    Sha256 = $digest
}
