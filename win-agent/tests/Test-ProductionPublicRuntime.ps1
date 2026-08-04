[CmdletBinding()]
param(
    [ValidateRange(5, 180)][int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agent = Join-Path $projectRoot 'src\Agent\bin\Release\net8.0-windows\TermLink.Agent.exe'
$tray = Join-Path $projectRoot 'src\Tray\bin\Release\net8.0-windows\TermLink.Tray.exe'
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$productionRoot = Join-Path $localAppData 'TermLink'
$configPath = Join-Path $productionRoot 'production-config.json'
$credentialsPath = Join-Path $productionRoot 'production-credentials.dpapi'

foreach ($required in @($agent, $tray, $configPath, $credentialsPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required production runtime file is unavailable: $required"
    }
}

$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "TermLink-Public-Runtime-$testId"
$bufferRoot = Join-Path $testRoot 'buffer'
$statusPath = Join-Path $testRoot 'status.json'
$controlPipe = "termlink-public-runtime-$testId"
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTestRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a test directory outside the temporary root: $resolvedTestRoot"
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
$agentProcess = $null
$snapshot = $null
try {
    $agentProcess = Start-Process -FilePath $agent -ArgumentList @(
        '--registry', '--control-pipe', $controlPipe,
        '--buffer-root', $bufferRoot
    ) -PassThru -WindowStyle Hidden

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        & $tray --control-pipe $controlPipe --buffer-root $bufferRoot `
            --agent-exe $agent --status-json $statusPath
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
            $snapshot = Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
            if ($snapshot.Production.RelayState -eq 2 -and
                $snapshot.Production.AccountLinked -and
                $snapshot.Production.Devices.Count -ge 1) {
                break
            }
        }
        if ($agentProcess.HasExited) {
            throw "Production Registry exited before connecting (exit code $($agentProcess.ExitCode))."
        }
        Start-Sleep -Milliseconds 250
    }

    if ($null -eq $snapshot) {
        throw 'Production Registry status was unavailable.'
    }

    [pscustomobject]@{
        Configured         = [bool]$snapshot.Production.Configured
        AccountLinked      = [bool]$snapshot.Production.AccountLinked
        RelayState         = [string]$snapshot.Production.RelayState
        DeviceCount        = [int]$snapshot.Production.Devices.Count
        SessionCount       = [int]$snapshot.Production.Sessions.Count
        LastErrorCode      = [string]$snapshot.Production.LastErrorCode
        LastConnectedAtUtc = $snapshot.Production.LastConnectedAtUtc
    } | Format-List

    if ($snapshot.Production.RelayState -ne 2 -or
        -not $snapshot.Production.AccountLinked -or
        $snapshot.Production.Devices.Count -lt 1) {
        throw 'Production runtime did not reach Connected with a registered device.'
    }

    Write-Output 'Production public Runtime registration and Realtime validation passed.'
}
finally {
    try {
        & $tray --control-pipe $controlPipe --buffer-root $bufferRoot `
            --agent-exe $agent --shutdown-agent | Out-Null
    }
    catch {
    }
    if ($null -ne $agentProcess) {
        try {
            if (-not $agentProcess.WaitForExit(10000)) { $agentProcess.Kill($true) }
        }
        catch {
        }
        $agentProcess.Dispose()
    }
    if (Test-Path -LiteralPath $resolvedTestRoot -PathType Container) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
