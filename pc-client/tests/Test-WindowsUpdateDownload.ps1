#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallationRoot,
    [Parameter(Mandatory)][string]$UpdateJsonPath,
    [string]$AssemblyDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[string]$installation = (Resolve-Path -LiteralPath $InstallationRoot).Path
if (-not $AssemblyDirectory) { $AssemblyDirectory = $installation }
[string]$work = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-UpdateDownload-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
try {
    foreach ($name in @('TermLink.Pc.Contracts', 'TermLink.Platform.Abstractions',
        'TermLink.Presentation', 'TermLink.UpdateCore', 'TermLink.AgentControl', 'TermLink.Platform.Windows')) {
        [Reflection.Assembly]::LoadFrom((Join-Path $AssemblyDirectory "$name.dll")) | Out-Null
    }
    $type = [TermLink.Platform.Windows.WindowsDesktopOperations]
    $flags = [Reflection.BindingFlags]'NonPublic,Static'
    $candidate = $type.GetMethod('ParseAppShipUpdate', $flags).Invoke(
        $null, [object[]]@([IO.File]::ReadAllText((Resolve-Path -LiteralPath $UpdateJsonPath).Path), [long]0))
    $operations = [Activator]::CreateInstance($type, [object[]]@($installation, $work))
    $download = $type.GetMethod('DownloadAndVerifyUpdateAsync', [Reflection.BindingFlags]'NonPublic,Instance')
    $task = $download.Invoke($operations, [object[]]@($candidate, $null, [Threading.CancellationToken]::None))
    $path = $task.GetAwaiter().GetResult()
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Verified download was not promoted.' }
    [TermLink.UpdateCore.WindowsUpdateSecurity]::ValidateIntegrity($path, $candidate.SizeBytes, $candidate.Sha256)
    $signer = [TermLink.UpdateCore.WindowsUpdateSecurity]::ResolveExpectedSignerThumbprint($installation)
    [TermLink.UpdateCore.WindowsUpdateSecurity]::ValidateAuthenticode($path, $signer, $candidate.SignerSha1, $installation) | Out-Null
    # An exclusive reopen also proves no download handle survives verification/promotion.
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $stream.Dispose()
    if (@(Get-ChildItem -LiteralPath (Split-Path $path) -Force -Filter '.download-*.tmp').Count) {
        throw 'Partial download was left behind.'
    }
    $cachedTask = $download.Invoke($operations, [object[]]@($candidate, $null, [Threading.CancellationToken]::None))
    if ($cachedTask.GetAwaiter().GetResult() -ne $path) { throw 'Verified cache path changed.' }
    # Only the isolated temporary copy is modified. The installer is never launched.
    [IO.File]::AppendAllText($path, 'changed-after-review')
    $verify = $type.GetMethod('VerifyCachedUpdateAsync', [Reflection.BindingFlags]'NonPublic,Instance')
    $rejected = $false
    try { $verify.Invoke($operations, [object[]]@($path, $candidate, [Threading.CancellationToken]::None)).GetAwaiter().GetResult() }
    catch { if ($_.Exception.ToString() -notmatch 'CryptographicException') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Installer changed after review was accepted.' }
    "Windows update download, integrity, Authenticode and file promotion passed: $($candidate.Status.VersionName).$($candidate.Status.BuildNumber)"
}
finally {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedWork = [IO.Path]::GetFullPath($work)
    if (-not $resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedWork) -notlike 'TermLink-UpdateDownload-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
}
