[CmdletBinding()]
param(
    [ValidateSet('Start', 'Status', 'Restart', 'Interrupt', 'Stop')]
    [string]$Action = 'Status',
    [ValidateRange(100, 50000)]
    [int]$LineCount = 800,
    [ValidateRange(1024, 49152)]
    [int]$LineBytes = 32768,
    [ValidateRange(0, 100)]
    [int]$LineDelayMilliseconds = 75,
    [ValidateRange(60, 86400)]
    [int]$HoldSeconds = 1800,
    [string]$StatePath = (Join-Path ([IO.Path]::GetTempPath()) 'TermLink-Production-Gap-Fixture.json'),
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agent = Join-Path $projectRoot 'src\Agent\bin\Release\net8.0-windows\TermLink.Agent.exe'
$tray = Join-Path $projectRoot 'src\Tray\bin\Release\net8.0-windows\TermLink.exe'
$sessionHost = Join-Path $projectRoot 'src\SessionHost\bin\Release\net8.0-windows\TermLink.SessionHost.exe'
$pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
$productionRoot = Join-Path ([Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)) 'TermLink'

function Read-FixtureState {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        throw "Production Gap fixture state is unavailable: $StatePath"
    }
    Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json
}

function Test-FixtureProcess {
    param([int]$ProcessId, [string]$ExpectedName)
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    return $null -ne $process -and $process.ProcessName -eq $ExpectedName
}

function Get-FixtureSnapshot {
    param($State)
    $statusPath = Join-Path $State.TestRoot 'status.json'
    Remove-Item -LiteralPath $statusPath -Force -ErrorAction SilentlyContinue
    & $tray --control-pipe $State.ControlPipe --buffer-root $State.BufferRoot `
        --status-json $statusPath | Out-Null
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
        throw 'Production Gap fixture status was unavailable.'
    }
    Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
}

function Get-RetainedRange {
    param([string]$BufferRoot, [Guid]$SessionId)
    $sessionDirectory = Join-Path $BufferRoot $SessionId.ToString('N')
    $sequences = @(Get-ChildItem -LiteralPath $sessionDirectory -Filter '*.chunk' -File `
        -ErrorAction SilentlyContinue | ForEach-Object {
            [uint64]::Parse([IO.Path]::GetFileNameWithoutExtension($_.Name))
        } | Sort-Object)
    if ($sequences.Count -eq 0) {
        return [pscustomobject]@{ Earliest = [uint64]0; Latest = [uint64]0; Count = 0 }
    }
    [pscustomobject]@{
        Earliest = [uint64]$sequences[0]
        Latest = [uint64]$sequences[-1]
        Count = $sequences.Count
    }
}

function Write-SafeStatus {
    param($State)
    $snapshot = Get-FixtureSnapshot $State
    $sessionId = [Guid]$State.SessionId
    $range = Get-RetainedRange $State.BufferRoot $sessionId
    $localSession = @($snapshot.Summaries | Where-Object {
        $_.SessionId -eq $sessionId.ToString('D')
    } | Select-Object -First 1)
    $localLifecycle = if ($localSession.Count -eq 1) {
        [string]$localSession[0].Lifecycle
    } else {
        ''
    }
    $productionTracked = @($snapshot.Production.Sessions | Where-Object {
        $_.SessionId -eq $sessionId.ToString('D')
    }).Count -eq 1
    [pscustomobject]@{
        StatePath = [IO.Path]::GetFullPath($StatePath)
        SessionId = $sessionId.ToString('D')
        AgentPid = [int]$State.AgentPid
        AgentRunning = Test-FixtureProcess ([int]$State.AgentPid) 'TermLink.Agent'
        HostPid = [int]$State.HostPid
        HostRunning = Test-FixtureProcess ([int]$State.HostPid) 'TermLink.SessionHost'
        RelayState = [string]$snapshot.Production.RelayState
        LocalSessionCount = @($snapshot.Summaries).Count
        LocalLifecycle = $localLifecycle
        ProductionTracked = $productionTracked
        EarliestRetainedSequence = $range.Earliest
        LatestRetainedSequence = $range.Latest
        RetainedFrameCount = $range.Count
    } | Format-List
}

if ($Action -eq 'Start') {
    foreach ($required in @(
        $agent,
        $tray,
        $sessionHost,
        (Join-Path $productionRoot 'production-config.json'),
        (Join-Path $productionRoot 'production-credentials.dpapi')
    )) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required production Gap fixture file is unavailable: $required"
        }
    }
    if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
        throw "A Production Gap fixture state already exists: $StatePath"
    }

    $testId = [Guid]::NewGuid().ToString('N')
    $testRoot = Join-Path ([IO.Path]::GetTempPath()) "TermLink-Production-Gap-$testId"
    $bufferRoot = Join-Path $testRoot 'buffer'
    $controlPipe = "termlink-production-gap-$testId"
    $markerPath = Join-Path $testRoot 'output-complete.marker'
    New-Item -ItemType Directory -Path $bufferRoot -Force | Out-Null
    $agentProcess = $null
    $hostProcess = $null
    try {
        $agentProcess = Start-Process -FilePath $agent -ArgumentList @(
            '--registry', '--control-pipe', $controlPipe,
            '--buffer-root', $bufferRoot,
            '--host-disconnect-timeout-ms', '5000',
            '--max-buffer-bytes', (16 * 1024 * 1024).ToString()
        ) -RedirectStandardOutput (Join-Path $testRoot 'agent.stdout') `
          -RedirectStandardError (Join-Path $testRoot 'agent.stderr') `
          -PassThru -WindowStyle Hidden

        $bootstrap = [pscustomobject]@{
            TestRoot = $testRoot
            BufferRoot = $bufferRoot
            ControlPipe = $controlPipe
            AgentPid = $agentProcess.Id
            HostPid = 0
            SessionId = [Guid]::Empty.ToString('D')
        }
        $connectedDeadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            $snapshot = Get-FixtureSnapshot $bootstrap
            if ($snapshot.Production.RelayState -eq 2 -and $snapshot.Production.AccountLinked) {
                break
            }
            if ($agentProcess.HasExited) {
                throw "Production Gap Agent exited with code $($agentProcess.ExitCode)."
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $connectedDeadline)
        if ($snapshot.Production.RelayState -ne 2 -or -not $snapshot.Production.AccountLinked) {
            throw 'Production Gap Agent did not connect to the Relay.'
        }

        $script =
            '[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);' +
            "`$line=('G'*$LineBytes);" +
            "1..$LineCount|%{[Console]::WriteLine(`$line+('{0:D5}'-f `$_));" +
            "Start-Sleep -Milliseconds $LineDelayMilliseconds};" +
            '[IO.File]::WriteAllText(' +
                "'" + $markerPath.Replace("'", "''") + "','complete',[Text.UTF8Encoding]::new(`$false));" +
            '[Console]::WriteLine(''__TERMLINK_GAP_OUTPUT_COMPLETE__'');' +
            "Start-Sleep -Seconds $HoldSeconds"
        $encodedScript = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
        $childCommand = '"{0}" -NoLogo -NoProfile -NonInteractive -EncodedCommand {1}' -f `
            $pwsh, $encodedScript
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($childCommand))
        $hostProcess = Start-Process -FilePath $sessionHost -ArgumentList @(
            '--command-base64', $encodedCommand,
            '--managed-shell', 'powershell-7',
            '--registry-pipe', $controlPipe,
            '--timeout-ms', (($HoldSeconds + 300) * 1000).ToString()
        ) -RedirectStandardOutput (Join-Path $testRoot 'host.stdout') `
          -RedirectStandardError (Join-Path $testRoot 'host.stderr') `
          -PassThru -WindowStyle Hidden

        $sessionDeadline = [DateTime]::UtcNow.AddSeconds(30)
        $session = $null
        do {
            $snapshot = Get-FixtureSnapshot $bootstrap
            $session = @($snapshot.Summaries) | Where-Object {
                $_.HostProcessId -eq $hostProcess.Id
            } | Select-Object -First 1
            if ($null -ne $session -and [uint64]$session.LastSequence -ge 2) { break }
            if ($hostProcess.HasExited) {
                throw "Production Gap Host exited with code $($hostProcess.ExitCode)."
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $sessionDeadline)
        if ($null -eq $session) {
            throw 'Production Gap session did not register.'
        }

        $state = [ordered]@{
            TestRoot = $testRoot
            BufferRoot = $bufferRoot
            ControlPipe = $controlPipe
            AgentPid = $agentProcess.Id
            HostPid = $hostProcess.Id
            SessionId = [string]$session.SessionId
        }
        $state | ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding utf8

        $outputDeadline = [DateTime]::UtcNow.AddMinutes(5)
        while (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -and
            [DateTime]::UtcNow -lt $outputDeadline) {
            if ($hostProcess.HasExited) {
                throw "Production Gap Host exited during output with code $($hostProcess.ExitCode)."
            }
            Start-Sleep -Milliseconds 250
        }
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            throw 'Production Gap output did not complete before the timeout.'
        }

        $retentionDeadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $range = Get-RetainedRange $bufferRoot ([Guid]$session.SessionId)
            if ($range.Earliest -gt 1 -and $range.Latest -ge [uint64]$session.LastSequence) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $retentionDeadline)
        if ($range.Earliest -le 1) {
            throw 'Production Gap fixture did not roll the encrypted event buffer.'
        }
        Write-SafeStatus ([pscustomobject]$state)
    }
    catch {
        if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
            Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        }
        if ($null -ne $agentProcess -and -not $agentProcess.HasExited) {
            Stop-Process -Id $agentProcess.Id -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $testRoot -PathType Container) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
    exit 0
}

$state = Read-FixtureState
if (-not (Test-FixtureProcess ([int]$state.AgentPid) 'TermLink.Agent')) {
    $replacementAgents = @(Get-Process -Name 'TermLink.Agent' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $agent })
    if ($replacementAgents.Count -eq 1) {
        $state.AgentPid = $replacementAgents[0].Id
        $state | ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding utf8
    }
}
if ($Action -eq 'Status') {
    Write-SafeStatus $state
    exit 0
}
if ($Action -eq 'Restart') {
    if (-not (Test-FixtureProcess ([int]$state.AgentPid) 'TermLink.Agent')) {
        throw 'Production Gap Agent is not running.'
    }
    $agentControlAssembly = Join-Path $projectRoot `
        'src\AgentControl\bin\Release\net8.0-windows\TermLink.AgentControl.dll'
    [Reflection.Assembly]::LoadFrom($agentControlAssembly) | Out-Null
    $request = [TermLink.Agent.RegistryRequest]::new()
    $request.Command = [TermLink.Agent.RegistryCommand]::RestartAgent
    $restart = [TermLink.Agent.RegistryControlClient]::Send(
        [string]$state.ControlPipe, $request, 10000)
    if (-not $restart.Success) {
        throw "Production Gap Agent rejected controlled restart: $($restart.Code)"
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Test-FixtureProcess ([int]$state.AgentPid) 'TermLink.Agent') -and
        [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (Test-FixtureProcess ([int]$state.AgentPid) 'TermLink.Agent') {
        throw 'Production Gap Agent did not stop for a controlled restart.'
    }

    $dotnet = Join-Path $productionRoot 'dotnet-sdk\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw "Pinned .NET SDK is unavailable: $dotnet"
    }
    & $dotnet build (Join-Path $projectRoot 'src\Agent\TermLink.Agent.csproj') `
        --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'Production Agent rebuild failed during controlled restart.'
    }
    $agentProcess = Start-Process -FilePath $agent -ArgumentList @(
        '--registry', '--control-pipe', $state.ControlPipe,
        '--buffer-root', $state.BufferRoot,
        '--host-disconnect-timeout-ms', '5000',
        '--max-buffer-bytes', (16 * 1024 * 1024).ToString()
    ) -RedirectStandardOutput (Join-Path $state.TestRoot 'agent-restarted.stdout') `
      -RedirectStandardError (Join-Path $state.TestRoot 'agent-restarted.stderr') `
      -PassThru -WindowStyle Hidden
    $state.AgentPid = $agentProcess.Id
    $state | ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding utf8

    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $snapshot = Get-FixtureSnapshot $state
        $recovered = @($snapshot.Summaries | Where-Object {
            $_.SessionId -eq [string]$state.SessionId -and $_.HostProcessId -eq [int]$state.HostPid
        })
        if ($snapshot.Production.RelayState -eq 2 -and $recovered.Count -eq 1) { break }
        if ($agentProcess.HasExited) {
            throw "Restarted Production Gap Agent exited with code $($agentProcess.ExitCode)."
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($snapshot.Production.RelayState -ne 2 -or $recovered.Count -ne 1) {
        throw 'Production Gap session was not recovered after Agent restart.'
    }
    Write-SafeStatus $state
    exit 0
}
if ($Action -eq 'Interrupt') {
    if (Test-FixtureProcess ([int]$state.HostPid) 'TermLink.SessionHost') {
        Stop-Process -Id ([int]$state.HostPid) -Force
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $snapshot = Get-FixtureSnapshot $state
        $localSession = @($snapshot.Summaries | Where-Object {
            $_.SessionId -eq [string]$state.SessionId
        })
        $productionSession = @($snapshot.Production.Sessions | Where-Object {
            $_.SessionId -eq [string]$state.SessionId
        })
        if ($localSession.Count -eq 0 -and $productionSession.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($localSession.Count -ne 0 -or $productionSession.Count -ne 0) {
        throw 'Production session did not leave the active Registry after Host loss.'
    }
    Write-SafeStatus $state
    exit 0
}

try {
    if (Test-FixtureProcess ([int]$state.AgentPid) 'TermLink.Agent') {
        & $tray --control-pipe $state.ControlPipe --buffer-root $state.BufferRoot `
            --shutdown-agent | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        while ((Test-FixtureProcess ([int]$state.AgentPid) 'TermLink.Agent') -and
            [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
        }
        if (Test-FixtureProcess ([int]$state.AgentPid) 'TermLink.Agent') {
            Stop-Process -Id ([int]$state.AgentPid) -Force
        }
    }
    if (Test-FixtureProcess ([int]$state.HostPid) 'TermLink.SessionHost') {
        Stop-Process -Id ([int]$state.HostPid) -Force
    }
}
finally {
    Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $state.TestRoot -PathType Container)) {
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolvedRoot = [IO.Path]::GetFullPath([string]$state.TestRoot)
        if (-not $resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolvedRoot).StartsWith(
                'TermLink-Production-Gap-', [StringComparison]::Ordinal)) {
            throw "Refusing to remove an unexpected fixture directory: $resolvedRoot"
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
Write-Output 'Production Gap fixture stopped.'
