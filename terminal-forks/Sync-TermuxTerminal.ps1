[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = 'https://github.com/termux/termux-app.git'
$commit = '3df69d1da197dd9bd71a3bafd902dffd720576b4'
$scriptRoot = Split-Path -Parent $PSCommandPath
$patchRoot = Join-Path $scriptRoot 'termux-patches'
$checkedInFork = Join-Path $scriptRoot 'termux-terminal'
$destinationPath = [IO.Path]::GetFullPath($Destination)

if (Test-Path -LiteralPath $destinationPath) {
    throw "Destination already exists; refusing to overwrite it: $destinationPath"
}

$checkout = Join-Path ([IO.Path]::GetTempPath()) ("termlink-termux-upstream-" + [guid]::NewGuid())
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$checkoutPath = [IO.Path]::GetFullPath($checkout)
if (!$checkoutPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary checkout escaped the system temporary directory: $checkoutPath"
}

try {
    git init $checkoutPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'git init failed' }
    git -C $checkoutPath remote add origin $repository
    if ($LASTEXITCODE -ne 0) { throw 'git remote add failed' }
    git -C $checkoutPath fetch --depth 1 origin $commit
    if ($LASTEXITCODE -ne 0) { throw 'git fetch failed' }
    git -C $checkoutPath checkout --detach FETCH_HEAD | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'git checkout failed' }

    $actualCommit = (git -C $checkoutPath rev-parse HEAD).Trim()
    if ($actualCommit -ne $commit) {
        throw "Fetched unexpected commit: $actualCommit"
    }

    $emulatorMain = Join-Path $destinationPath 'terminal-emulator\src\main\java\com\termux\terminal'
    $emulatorTest = Join-Path $destinationPath 'terminal-emulator\src\test\java\com\termux\terminal'
    $viewMain = Join-Path $destinationPath 'terminal-view\src\main'
    New-Item -ItemType Directory -Force -Path $emulatorMain, $emulatorTest, $viewMain | Out-Null

    $emulatorFiles = @(
        'ByteQueue.java',
        'KeyHandler.java',
        'Logger.java',
        'TerminalBuffer.java',
        'TerminalColors.java',
        'TerminalColorScheme.java',
        'TerminalEmulator.java',
        'TerminalOutput.java',
        'TerminalRow.java',
        'TextStyle.java',
        'WcWidth.java'
    )
    foreach ($name in $emulatorFiles) {
        $source = Join-Path $checkoutPath "terminal-emulator\src\main\java\com\termux\terminal\$name"
        Copy-Item -LiteralPath $source -Destination (Join-Path $emulatorMain $name)
    }

    $upstreamTests = Join-Path $checkoutPath 'terminal-emulator\src\test\java\com\termux\terminal'
    Get-ChildItem -LiteralPath $upstreamTests -Filter '*.java' -File |
        Copy-Item -Destination $emulatorTest
    Copy-Item -LiteralPath (Join-Path $checkoutPath 'terminal-emulator\src\main\AndroidManifest.xml') `
        -Destination (Join-Path $destinationPath 'terminal-emulator\src\main\AndroidManifest.xml')

    Copy-Item -LiteralPath (Join-Path $checkoutPath 'terminal-view\src\main\java') `
        -Destination $viewMain -Recurse
    Copy-Item -LiteralPath (Join-Path $checkoutPath 'terminal-view\src\main\res') `
        -Destination $viewMain -Recurse
    Copy-Item -LiteralPath (Join-Path $checkoutPath 'terminal-view\src\main\AndroidManifest.xml') `
        -Destination (Join-Path $viewMain 'AndroidManifest.xml')
    Copy-Item -LiteralPath (Join-Path $checkoutPath 'LICENSE.md') `
        -Destination (Join-Path $destinationPath 'UPSTREAM_LICENSE.txt')

    $patches = @(Get-ChildItem -LiteralPath $patchRoot -Filter '*.patch' -File | Sort-Object Name)
    if ($patches.Count -eq 0) { throw "No TermLink patches found in: $patchRoot" }
    foreach ($patch in $patches) {
        git -C $destinationPath apply --whitespace=nowarn $patch.FullName
        if ($LASTEXITCODE -ne 0) { throw "TermLink patch failed to apply: $($patch.Name)" }
    }

    Copy-Item -LiteralPath (Join-Path $checkedInFork 'terminal-emulator\build.gradle.kts') `
        -Destination (Join-Path $destinationPath 'terminal-emulator\build.gradle.kts')
    Copy-Item -LiteralPath (Join-Path $checkedInFork 'terminal-view\build.gradle.kts') `
        -Destination (Join-Path $destinationPath 'terminal-view\build.gradle.kts')
    Copy-Item -LiteralPath (Join-Path $checkedInFork 'UPSTREAM.md') `
        -Destination (Join-Path $destinationPath 'UPSTREAM.md')

    Write-Output "Prepared pinned Termux terminal fork at $destinationPath"
    Write-Output "Upstream commit: $actualCommit"
} finally {
    if (Test-Path -LiteralPath $checkoutPath) {
        Remove-Item -LiteralPath $checkoutPath -Recurse -Force
    }
}
