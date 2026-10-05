[CmdletBinding()]
param(
    [string]$ApplicationRoot = (Join-Path $PSScriptRoot '..'),
    [ValidateSet('Windows', 'Android', 'All')][string]$Platform = 'All',
    [switch]$RequireClean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$application = [IO.Path]::GetFullPath($ApplicationRoot)
$lock = Get-Content -LiteralPath (Join-Path $application 'terminal-core.lock.json') -Raw | ConvertFrom-Json
if ($lock.schema_version -ne 2 -or $lock.revision -cnotmatch '^[0-9a-f]{40}$' -or $lock.source_dirty -isnot [bool]) {
    throw 'Invalid terminal-core binary lock.'
}
if ($RequireClean) {
    if ($lock.source_dirty) { throw 'Production requires binaries built from a clean core revision.' }
    $state = @(& git -C $application status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $state.Count -ne 0) { throw 'Application repository is not clean.' }
}
$expected = @()
if ($Platform -in @('All', 'Windows')) { $expected += 'native/terminal-core/windows-x64/termlink_terminal_ffi.dll' }
if ($Platform -in @('All', 'Android')) {
    $expected += @('arm64-v8a', 'armeabi-v7a', 'x86', 'x86_64') | ForEach-Object { "native/terminal-core/android/$_/libtermlink_terminal_jni.so" }
}
foreach ($relative in $expected) {
    $records = @($lock.artifacts | Where-Object { $_.path -ceq $relative })
    if ($records.Count -ne 1) { throw "Missing or duplicate binary lock entry: $relative" }
    $record = $records[0]
    $path = Join-Path $application $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing prebuilt library: $relative. Obtain the pinned DLL/SO package from the core maintainer." }
    if ($record.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        (Get-Item -LiteralPath $path).Length -ne $record.size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.sha256) {
        throw "Native binary SHA-256/size mismatch: $relative"
    }
}
[pscustomobject]@{ revision = $lock.revision; source_dirty = $lock.source_dirty; verified_libraries = $expected.Count }
