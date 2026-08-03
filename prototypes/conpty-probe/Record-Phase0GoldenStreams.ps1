[CmdletBinding()]
param(
    [string]$CaptureRoot = (Join-Path $PSScriptRoot '..\..\testdata\terminal-golden-streams'),
    [string]$WslDistribution = 'Ubuntu-24.04',
    [switch]$SkipWslToolPreparation,
    [switch]$OverwriteCapture
)

$ErrorActionPreference = 'Stop'
$capturePath = [IO.Path]::GetFullPath($CaptureRoot)
[IO.Directory]::CreateDirectory($capturePath) | Out-Null

if (-not $SkipWslToolPreparation) {
    $prepareScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Prepare-Phase0WslTools.sh'))
    $wslPrepareScript = (& wsl.exe -d $WslDistribution -u root --exec wslpath -a $prepareScript).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslPrepareScript)) {
        throw "Unable to convert WSL tool script path: $prepareScript"
    }
    & wsl.exe -d $WslDistribution -u root --exec bash $wslPrepareScript
    if ($LASTEXITCODE -ne 0) { throw 'Phase 0 WSL tool preparation failed.' }
}

$cases = @(
    [ordered]@{ shell = 'cmd'; scenario = 'smoke'; name = 'smoke-cmd-v1' }
    [ordered]@{ shell = 'powershell'; scenario = 'smoke'; name = 'smoke-powershell-v1' }
    [ordered]@{ shell = 'pwsh'; scenario = 'smoke'; name = 'smoke-pwsh-v1' }
    [ordered]@{ shell = 'wsl'; scenario = 'smoke'; name = 'smoke-wsl-v1' }
    [ordered]@{ shell = 'pwsh'; scenario = 'ansi-unicode'; name = 'ansi-unicode-pwsh-v1' }
    [ordered]@{ shell = 'pwsh'; scenario = 'terminal-features'; name = 'terminal-features-pwsh-v1' }
    [ordered]@{ shell = 'wsl'; scenario = 'vim'; name = 'tui-vim-wsl-v1' }
    [ordered]@{ shell = 'wsl'; scenario = 'less'; name = 'tui-less-wsl-v1' }
    [ordered]@{ shell = 'wsl'; scenario = 'fzf'; name = 'tui-fzf-wsl-v1' }
    [ordered]@{ shell = 'wsl'; scenario = 'htop'; name = 'tui-htop-wsl-v1' }
)

$probe = Join-Path $PSScriptRoot 'ConPtyProbe.ps1'
$validator = Join-Path $PSScriptRoot 'Test-GoldenStream.ps1'
$results = foreach ($case in $cases) {
    & $probe `
        -Shell $case.shell `
        -Scenario $case.scenario `
        -CaptureRoot $capturePath `
        -CaseName $case.name `
        -OverwriteCapture:$OverwriteCapture | Out-Host
    & $validator -BundlePath (Join-Path $capturePath $case.name) | Out-Host
    [pscustomobject]@{
        CaseName = $case.name
        Shell = $case.shell
        Scenario = $case.scenario
        Valid = $true
    }
}

$results | Format-Table -AutoSize
