[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [int]$ClientCount = 32
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $projectRoot "src\Tray\bin\$Configuration\net8.0-windows"
$agent = Join-Path $projectRoot "src\Agent\bin\$Configuration\net8.0-windows\TermLink.Agent.exe"
$main = Join-Path $output 'TermLink.exe'
if (-not (Test-Path -LiteralPath $agent -PathType Leaf) -or
    -not (Test-Path -LiteralPath $main -PathType Leaf)) {
    throw 'Build the Windows solution before running the control-pipe concurrency test.'
}

$suffix = [guid]::NewGuid().ToString('N')
$pipe = "termlink-concurrency-$suffix"
$buffer = Join-Path $env:TEMP "termlink-concurrency-$suffix"
$server = 'https://termlink.kget.cc:8910'
$agentProcess = Start-Process -FilePath $agent -ArgumentList @(
    '--registry', '--control-pipe', $pipe, '--buffer-root', $buffer
) -PassThru -WindowStyle Hidden

try {
    $ready = $false
    foreach ($attempt in 1..30) {
        $probe = Join-Path $env:TEMP "termlink-ready-$suffix.json"
        $process = Start-Process -FilePath $main -ArgumentList @(
            '--control-pipe', $pipe,
            '--buffer-root', $buffer,
            '--server-url', $server,
            '--status-json', $probe
        ) -PassThru -WindowStyle Hidden -Wait
        if ($process.ExitCode -eq 0 -and (Test-Path -LiteralPath $probe)) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'Agent did not become ready.' }

    $clients = foreach ($index in 1..$ClientCount) {
        $status = Join-Path $env:TEMP "termlink-concurrent-$suffix-$index.json"
        [pscustomobject]@{
            Status = $status
            Process = Start-Process -FilePath $main -ArgumentList @(
                '--control-pipe', $pipe,
                '--buffer-root', $buffer,
                '--server-url', $server,
                '--status-json', $status
            ) -PassThru -WindowStyle Hidden
        }
    }
    foreach ($client in $clients) { $client.Process.WaitForExit() }
    $failed = @($clients | Where-Object {
        $_.Process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $_.Status)
    })
    if ($failed.Count -ne 0) {
        throw "$($failed.Count) concurrent control clients failed."
    }

    $shutdown = Start-Process -FilePath $main -ArgumentList @(
        '--control-pipe', $pipe,
        '--buffer-root', $buffer,
        '--server-url', $server,
        '--shutdown-agent'
    ) -PassThru -WindowStyle Hidden -Wait
    if ($shutdown.ExitCode -ne 0) { throw 'Shutdown request failed.' }
    if (-not $agentProcess.WaitForExit(10000)) { throw 'Agent did not exit.' }

    [pscustomobject]@{
        ConcurrentClients = $clients.Count
        Failures = $failed.Count
        AgentExitCode = $agentProcess.ExitCode
        ShutdownExitCode = $shutdown.ExitCode
    }
}
finally {
    if (-not $agentProcess.HasExited) {
        $agentProcess.Kill($true)
        $agentProcess.WaitForExit()
    }
}
