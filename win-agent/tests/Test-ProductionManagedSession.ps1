[CmdletBinding()]
param(
    [ValidateRange(10, 180)][int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agent = Join-Path $projectRoot 'src\Agent\bin\Release\net8.0-windows\TermLink.Agent.exe'
$tray = Join-Path $projectRoot 'src\Tray\bin\Release\net8.0-windows\TermLink.Tray.exe'
$sessionHost = Join-Path $projectRoot 'src\SessionHost\bin\Release\net8.0-windows\TermLink.SessionHost.exe'
$pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$productionRoot = Join-Path $localAppData 'TermLink'

foreach ($required in @(
    $agent,
    $tray,
    $sessionHost,
    (Join-Path $productionRoot 'production-config.json'),
    (Join-Path $productionRoot 'production-credentials.dpapi')
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required production managed-session file is unavailable: $required"
    }
}

$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "TermLink-Managed-Session-$testId"
$bufferRoot = Join-Path $testRoot 'buffer'
$statusPath = Join-Path $testRoot 'status.json'
$controlPipe = "termlink-managed-session-$testId"
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
if (-not $resolvedTestRoot.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a test directory outside the temporary root: $resolvedTestRoot"
}

function Get-RuntimeSnapshot {
    if (Test-Path -LiteralPath $statusPath -PathType Leaf) {
        Remove-Item -LiteralPath $statusPath -Force
    }
    & $tray --control-pipe $controlPipe --buffer-root $bufferRoot `
        --agent-exe $agent --status-json $statusPath
    $statusDeadline = [DateTime]::UtcNow.AddSeconds(12)
    while (-not (Test-Path -LiteralPath $statusPath -PathType Leaf) -and
        [DateTime]::UtcNow -lt $statusDeadline) {
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
        throw 'Production managed-session status was unavailable.'
    }
    Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
$hostProcess = $null
$outputTask = $null
$errorTask = $null
$snapshot = $null
$managedSession = $null
$localMarker = '__TERMLINK_MANAGED_LOCAL_INPUT__'
try {
    $script =
        '[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);' +
        '[Console]::InputEncoding=[Text.UTF8Encoding]::new($false);' +
        '[Console]::WriteLine("__TERMLINK_MANAGED_READY__");' +
        '$line=[Console]::ReadLine();' +
        '[Console]::WriteLine("__TERMLINK_MANAGED_LOCAL_INPUT__"+$line);' +
        'exit 0'
    $encodedScript = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $childCommand = '"{0}" -NoLogo -NoProfile -NonInteractive -EncodedCommand {1}' -f $pwsh, $encodedScript
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($childCommand))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $sessionHost
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @(
        '--command-base64', $encodedCommand,
        '--managed-shell', 'powershell-7',
        '--registry-pipe', $controlPipe,
        '--timeout-ms', ([Math]::Max(30000, $TimeoutSeconds * 1000)).ToString()
    )) {
        $start.ArgumentList.Add($argument)
    }
    $hostProcess = [Diagnostics.Process]::Start($start)
    if ($null -eq $hostProcess) { throw 'Managed Session Host could not be started.' }
    $outputTask = $hostProcess.StandardOutput.ReadToEndAsync()
    $errorTask = $hostProcess.StandardError.ReadToEndAsync()

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $snapshot = Get-RuntimeSnapshot
        $localSessions = @($snapshot.Summaries)
        $productionSessions = @($snapshot.Production.Sessions)
        if ($snapshot.Production.RelayState -eq 2 -and
            $snapshot.Production.AccountLinked -and
            $localSessions.Count -eq 1 -and
            $productionSessions.Count -eq 1 -and
            $localSessions[0].SessionId -eq $productionSessions[0].SessionId -and
            [uint64]$localSessions[0].LastSequence -ge 2) {
            $managedSession = $localSessions[0]
            break
        }
        if ($hostProcess.HasExited) {
            throw "Managed Session Host exited before registration (exit code $($hostProcess.ExitCode))."
        }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $managedSession) {
        throw 'Managed PowerShell session did not reach the connected production Runtime.'
    }

    $hostProcess.StandardInput.WriteLine('local-windows-keyboard')
    $hostProcess.StandardInput.Flush()
    $hostProcess.StandardInput.Close()
    if (-not $hostProcess.WaitForExit(15000)) {
        throw 'Managed PowerShell did not exit after local input.'
    }
    $output = $outputTask.GetAwaiter().GetResult()
    $errorOutput = $errorTask.GetAwaiter().GetResult()
    if ($hostProcess.ExitCode -ne 0 -or
        -not $output.Contains($localMarker + 'local-windows-keyboard', [StringComparison]::Ordinal)) {
        throw "Managed PowerShell local-input validation failed (exit $($hostProcess.ExitCode)): $errorOutput"
    }

    $terminalPublishDeadline = [DateTime]::UtcNow.AddSeconds(15)
    $terminalPublished = $false
    do {
        $finalSnapshot = Get-RuntimeSnapshot
        $finalLocalSessions = @($finalSnapshot.Summaries)
        $finalProductionSessions = @($finalSnapshot.Production.Sessions)
        if ($finalLocalSessions.Count -eq 0 -and $finalProductionSessions.Count -eq 0) {
            $terminalPublished = $true
            break
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $terminalPublishDeadline)
    if (-not $terminalPublished) {
        throw 'Managed PowerShell terminal lifecycle was not published before Agent shutdown.'
    }

    [pscustomobject]@{
        AccountLinked = [bool]$snapshot.Production.AccountLinked
        RelayState = [string]$snapshot.Production.RelayState
        SessionId = [string]$managedSession.SessionId
        ShellType = [string]$managedSession.ShellType
        LastSequence = [uint64]$managedSession.LastSequence
        LocalInputCompleted = $true
        TerminalPublished = $terminalPublished
        ExitCode = $hostProcess.ExitCode
    } | Format-List
    Write-Output 'Production managed PowerShell registration and local-priority validation passed.'
}
finally {
    if ($null -ne $hostProcess) {
        try {
            if (-not $hostProcess.HasExited) { $hostProcess.Kill($true) }
            $hostProcess.WaitForExit(5000) | Out-Null
        }
        catch {
        }
        $hostProcess.Dispose()
    }
    try {
        & $tray --control-pipe $controlPipe --buffer-root $bufferRoot `
            --agent-exe $agent --shutdown-agent | Out-Null
        $shutdownDeadline = [DateTime]::UtcNow.AddSeconds(12)
        do {
            $matchingAgents = @(Get-CimInstance Win32_Process | Where-Object {
                $_.Name -eq 'TermLink.Agent.exe' -and $_.CommandLine -like "*$controlPipe*"
            })
            if ($matchingAgents.Count -eq 0) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $shutdownDeadline)
    }
    catch {
    }
    if (Test-Path -LiteralPath $resolvedTestRoot -PathType Container) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
