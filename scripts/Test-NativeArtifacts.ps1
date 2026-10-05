$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$verify = Join-Path $PSScriptRoot 'Verify-NativeArtifacts.ps1'
$scratch = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ("../artifacts/native-artifact-tests/" + [guid]::NewGuid().ToString('N'))))
$null = New-Item -ItemType Directory -Force -Path $scratch
$paths = @('native/terminal-core/windows-x64/termlink_terminal_ffi.dll')
$paths += @('arm64-v8a','armeabi-v7a','x86','x86_64') | ForEach-Object { "native/terminal-core/android/$_/libtermlink_terminal_jni.so" }
$records = foreach ($path in $paths) {
    $file = Join-Path $scratch $path
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file)
    [IO.File]::WriteAllBytes($file, [byte[]]@(1,2,3))
    @{ path=$path; size=3; sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$lock = @{schema_version=2; revision=('a'*40); source_dirty=$false; artifacts=@($records)}
$lockPath = Join-Path $scratch 'terminal-core.lock.json'
function Write-Lock { $lock | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $lockPath -Encoding utf8 }
function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    try { $null = & $Action } catch {
        if ($_.Exception.Message -like "*$Expected*") { return }
        throw
    }
    throw "Expected rejection: $Expected"
}
Write-Lock
$result = & $verify -ApplicationRoot $scratch
if ($result.verified_libraries -ne 5) { throw 'Binary-only checkout verification failed' }
[IO.File]::WriteAllBytes((Join-Path $scratch $paths[0]), [byte[]]@(3,2,1))
Assert-Rejected { & $verify -ApplicationRoot $scratch -Platform Windows } 'SHA-256/size mismatch'
$null = & $verify -ApplicationRoot $scratch -Platform Android
[IO.File]::WriteAllBytes((Join-Path $scratch $paths[0]), [byte[]]@(1,2,3))
$lock.artifacts += $lock.artifacts[0]
Write-Lock
Assert-Rejected { & $verify -ApplicationRoot $scratch } 'duplicate binary lock entry'
$lock.artifacts = @($records)
$lock.artifacts[0].path = '../outside.dll'
Write-Lock
Assert-Rejected { & $verify -ApplicationRoot $scratch } 'Missing or duplicate'
$lock.artifacts[0].path = $paths[0]
$lock.source_dirty = $true
Write-Lock
Assert-Rejected { & $verify -ApplicationRoot $scratch -RequireClean } 'clean core revision'
$lock.source_dirty = $false
Write-Lock
Move-Item -LiteralPath (Join-Path $scratch $paths[0]) -Destination (Join-Path $scratch 'missing.dll')
Assert-Rejected { & $verify -ApplicationRoot $scratch -Platform Windows } 'Missing prebuilt library'
[pscustomobject]@{ Passed=7; NoCoreCheckoutRequired=$true; Evidence=$scratch }
