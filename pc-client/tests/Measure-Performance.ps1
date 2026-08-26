[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet',
    [ValidateRange(5, 300)]
    [int]$DurationSeconds = 20,
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\pc-avalonia-p4-performance.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$desktopDll = Join-Path $repositoryRoot 'pc-client\src\Desktop\bin\Release\net8.0\TermLink.dll'
$agentExecutable = Join-Path $repositoryRoot 'win-agent\src\Agent\bin\Release\net8.0-windows\TermLink.Agent.exe'
$dotnetExecutable = if (Test-Path -LiteralPath $DotNetPath -PathType Leaf) {
    [IO.Path]::GetFullPath($DotNetPath)
} else {
    $dotnetCommand = Get-Command $DotNetPath -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $dotnetCommand) { throw ".NET executable not found: $DotNetPath" }
    $dotnetCommand.Source
}
Assert-True (Test-Path -LiteralPath $desktopDll -PathType Leaf) "Desktop build not found: $desktopDll"
Assert-True (Test-Path -LiteralPath $agentExecutable -PathType Leaf) "Agent build not found: $agentExecutable"

$sampleRoot = Join-Path ([IO.Path]::GetTempPath()) ('termlink-p4-performance-' + [guid]::NewGuid().ToString('N'))
$bufferRoot = Join-Path $sampleRoot 'agent'
$localAppDataRoot = Join-Path $sampleRoot 'local-app-data'
$acceptanceMarker = Join-Path $sampleRoot 'acceptance-root.cer'
[IO.Directory]::CreateDirectory($bufferRoot) | Out-Null
[IO.Directory]::CreateDirectory($localAppDataRoot) | Out-Null
$originalAccountRoot = [Environment]::GetEnvironmentVariable('TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT')
$originalAcceptanceCA = [Environment]::GetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE')
$desktopProcess = $null
$agentProcess = $null

try {
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT', $localAppDataRoot)
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE', $acceptanceMarker)
    $pipeName = 'termlink-p4-performance-' + [guid]::NewGuid().ToString('N')
    $desktopProcess = Start-Process -FilePath $dotnetExecutable `
        -ArgumentList @($desktopDll, '--preview-fixture', '--start-hidden') `
        -WindowStyle Hidden -PassThru
    $agentProcess = Start-Process -FilePath $agentExecutable `
        -ArgumentList @('--registry', '--control-pipe', $pipeName, '--buffer-root', $bufferRoot) `
        -WindowStyle Hidden -PassThru

    Start-Sleep -Seconds 5
    foreach ($process in @($desktopProcess, $agentProcess)) {
        Assert-True (-not $process.HasExited) `
            "Performance measurement process exited early with code $($process.ExitCode)."
        $process.Refresh()
    }

    $desktopCpuAtStart = $desktopProcess.TotalProcessorTime
    $agentCpuAtStart = $agentProcess.TotalProcessorTime
    $sample = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $DurationSeconds
    $sample.Stop()
    $desktopProcess.Refresh()
    $agentProcess.Refresh()

    $desktopCpu = (($desktopProcess.TotalProcessorTime - $desktopCpuAtStart).TotalSeconds /
        $sample.Elapsed.TotalSeconds) * 100
    $agentCpu = (($agentProcess.TotalProcessorTime - $agentCpuAtStart).TotalSeconds /
        $sample.Elapsed.TotalSeconds) * 100
    $desktopMemory = $desktopProcess.PrivateMemorySize64 / 1MB
    $agentMemory = $agentProcess.PrivateMemorySize64 / 1MB
    $evidence = [ordered]@{
        schema = 'termlink.pc-avalonia-p4-performance'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = 'isolated Windows idle sample; Avalonia Desktop uses preview fixture and Agent uses an isolated unconfigured registry'
        passed = ($desktopCpu -le 0.5 -and $desktopMemory -le 120 -and ($desktopMemory + $agentMemory) -le 200)
        hardware = [ordered]@{
            operating_system = (Get-CimInstance Win32_OperatingSystem).Caption
            processor = (Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
            logical_processors = [Environment]::ProcessorCount
        }
        sample = [ordered]@{
            warmup_seconds = 5
            duration_seconds = [Math]::Round($sample.Elapsed.TotalSeconds, 3)
            diagnostics_enabled = $false
        }
        desktop = [ordered]@{
            single_core_cpu_percent = [Math]::Round($desktopCpu, 3)
            private_memory_mib = [Math]::Round($desktopMemory, 2)
            threads = $desktopProcess.Threads.Count
            handles = $desktopProcess.HandleCount
        }
        agent = [ordered]@{
            single_core_cpu_percent = [Math]::Round($agentCpu, 3)
            private_memory_mib = [Math]::Round($agentMemory, 2)
            threads = $agentProcess.Threads.Count
            handles = $agentProcess.HandleCount
        }
        total_private_memory_mib = [Math]::Round($desktopMemory + $agentMemory, 2)
        gates = [ordered]@{
            desktop_single_core_cpu_percent_max = 0.5
            desktop_private_memory_mib_max = 120
            desktop_plus_agent_private_memory_mib_max = 200
        }
    }
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    Assert-True $evidence.passed 'The P4 idle performance sample exceeded a release gate.'
    Write-Output "P4 Windows idle performance validation passed. Evidence: $resolvedEvidence"
}
finally {
    foreach ($process in @($desktopProcess, $agentProcess)) {
        if ($null -eq $process) { continue }
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
                $process.WaitForExit()
            }
        }
        finally {
            $process.Dispose()
        }
    }
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT', $originalAccountRoot)
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE', $originalAcceptanceCA)
    $resolvedSampleRoot = [IO.Path]::GetFullPath($sampleRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedSampleRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedSampleRoot).StartsWith(
            'termlink-p4-performance-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedSampleRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
