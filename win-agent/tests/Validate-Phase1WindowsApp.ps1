[CmdletBinding()]
param(
    [string]$DotNetPath = (Join-Path $env:TEMP 'termlink-dotnet-sdk-8.0.303\dotnet.exe'),
    [string]$EvidencePath = (Join-Path $PSScriptRoot '..\..\docs\validation\phase1-windows-app.json'),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-ResultFields {
    param([Parameter(Mandatory)][string]$Text)
    $prefix = '__RECOVERY_TEST_RESULT__:'
    $line = @($Text -split "`r?`n" | Where-Object { $_.StartsWith($prefix) }) | Select-Object -Last 1
    if (-not $line) { throw "Recovery result line was not captured: $Text" }
    $fields = [ordered]@{}
    foreach ($part in $line.Substring($prefix.Length).Split(';')) {
        $pair = $part.Split([char[]]@('='), 2)
        if ($pair.Count -eq 2) { $fields[$pair[0]] = $pair[1] }
    }
    $fields
}

if (-not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) {
    throw "A .NET 8 SDK is required. SDK executable not found: $DotNetPath"
}
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot '..\Build-Windows.ps1') -DotNetPath $DotNetPath
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "C# compiler not found: $compiler"
}

$configuration = 'Release'
$framework = 'net8.0-windows'
$agentExecutable = Join-Path $PSScriptRoot "..\src\Agent\bin\$configuration\$framework\TermLink.Agent.exe"
$trayExecutable = Join-Path $PSScriptRoot "..\src\Tray\bin\$configuration\$framework\TermLink.exe"
$hostExecutable = Join-Path $PSScriptRoot "..\src\SessionHost\bin\$configuration\$framework\TermLink.SessionHost.exe"
foreach ($executable in @($agentExecutable, $trayExecutable, $hostExecutable)) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Build output not found: $executable" }
}

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('TermLink-WindowsApp-' + [Guid]::NewGuid().ToString('N'))
$validationExecutable = Join-Path $temporaryDirectory 'SessionRecoveryValidation.exe'
$bufferRoot = Join-Path $temporaryDirectory 'agent-data'
$isolatedLocalAppDataRoot = Join-Path $temporaryDirectory 'isolated-local-app-data'
$isolationMarkerPath = Join-Path $temporaryDirectory 'production-isolation-marker.cer'
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
[IO.Directory]::CreateDirectory($bufferRoot) | Out-Null
[IO.Directory]::CreateDirectory($isolatedLocalAppDataRoot) | Out-Null

try {
    & $compiler `
        /nologo `
        /target:exe `
        /platform:x64 `
        /optimize+ `
        /r:System.Core.dll `
        "/out:$validationExecutable" `
        (Join-Path $PSScriptRoot '..\src\AgentControl\AgentControl.cs') `
        (Join-Path $PSScriptRoot 'SessionRecoveryValidation.cs')
    if ($LASTEXITCODE -ne 0) {
        throw "Session recovery validation compilation failed with exit code $LASTEXITCODE."
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $validationExecutable
    $validationArguments = @(
        $agentExecutable,
        $trayExecutable,
        $hostExecutable,
        $bufferRoot,
        $isolatedLocalAppDataRoot,
        $isolationMarkerPath
    ) | ForEach-Object { '"' + ([string]$_).Replace('"', '\"') + '"' }
    $start.Arguments = $validationArguments -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    # ProductionAccountStorage accepts this root only with the paired CA marker.
    # Both are process-scoped and inherited by the explicitly configured Agent
    # and Tray children; the caller's real LocalAppData and account files are
    # never moved, deleted, or opened by this validation.
    $start.EnvironmentVariables['LOCALAPPDATA'] = $isolatedLocalAppDataRoot
    $start.EnvironmentVariables['TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT'] = $isolatedLocalAppDataRoot
    $start.EnvironmentVariables['TERMLINK_ACCEPTANCE_CA_FILE'] = $isolationMarkerPath
    $validationProcess = [Diagnostics.Process]::Start($start)
    if ($null -eq $validationProcess) { throw 'Session recovery validation process could not be started.' }
    try {
        $outputTask = $validationProcess.StandardOutput.ReadToEndAsync()
        $errorTask = $validationProcess.StandardError.ReadToEndAsync()
        $validationProcess.WaitForExit()
        $testExit = $validationProcess.ExitCode
        $text = $outputTask.GetAwaiter().GetResult() + $errorTask.GetAwaiter().GetResult()
    }
    finally {
        $validationProcess.Dispose()
    }
    Assert-True ($testExit -eq 0) "Session recovery validation failed with exit code ${testExit}: $text"
    $fields = Get-ResultFields -Text $text
    Assert-True ([int]$fields.sessions -eq 20) '20-session pressure coverage did not run.'
    Assert-True ([int]$fields.recovered -eq 20) 'Agent restart did not recover all 20 sessions.'
    Assert-True ([int]$fields.controlled_recovered -eq 20) 'Controlled Agent restart did not recover all 20 sessions.'
    Assert-True ($fields.registration_times_preserved -eq 'true') 'Registration timestamps were not preserved.'
    Assert-True ($fields.dpapi_index_plaintext -eq 'false') 'Registration index exposed plaintext routing secrets.'
    Assert-True ([int]$fields.tray_status_sessions -eq 20) 'Tray did not read all 20 summaries.'
    Assert-True ([int]$fields.local_shells_survived -eq 20) 'A local shell did not survive remote stop.'
    Assert-True ($fields.terminal_index_retained -eq 'true') 'Remote stop did not retain the encrypted terminal-recovery index.'
    Assert-True ($fields.isolated_local_app_data -eq 'true') 'Validation did not use its isolated LocalAppData root.'
    Assert-True ($fields.production_runtime_absent -eq 'true') 'A production runtime was created during local recovery validation.'
    Assert-True ($fields.production_configured -eq 'false') 'Local recovery validation observed a production account.'

    $sourceFiles = @(
        'src\AgentControl\AgentControl.cs',
        'src\Agent\CurrentUserPipe.cs',
        'src\Agent\SessionRegistrationStore.cs',
        'src\Agent\SessionRegistry.cs',
        'src\Agent\TermLink.Agent.csproj',
        'src\SessionHost\TermLink.SessionHost.csproj',
        'src\Tray\Program.cs',
        'src\Tray\TrayApplicationContext.cs',
        'src\Tray\TermLink.Tray.csproj',
        'tests\SessionRecoveryValidation.cs'
    )
    $hashes = [ordered]@{}
    foreach ($relativePath in $sourceFiles) {
        $source = Join-Path $PSScriptRoot "..\$relativePath"
        $hashes[$relativePath.Replace('\', '/')] =
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $sdkVersion = (& $DotNetPath --version).Trim()
    $evidence = [ordered]@{
        schema = 'termlink.phase1-windows-app-validation'
        version = 1
        generated_at_utc = [DateTime]::UtcNow.ToString('o')
        classification = 'current-user .NET 8 Agent/Tray and local recovery; not production Relay, installer, or E2EE'
        passed = $true
        dotnet_sdk = $sdkVersion
        target_framework = 'net8.0-windows'
        warnings = 0
        errors = 0
        real_conpty_session_hosts = [int]$fields.sessions
        sessions_recovered_after_forced_agent_restart = [int]$fields.recovered
        sessions_recovered_after_controlled_agent_restart = [int]$fields.controlled_recovered
        registration_times_preserved = [bool]::Parse($fields.registration_times_preserved)
        registration_index = 'DPAPI CurrentUser, atomic replace, PID plus process-start validation'
        registration_index_exposed_plaintext = [bool]::Parse($fields.dpapi_index_plaintext)
        tray_summary_sessions = [int]$fields.tray_status_sessions
        local_shells_survived_remote_stop = [int]$fields.local_shells_survived
        remote_stop_retained_terminal_recovery_index = [bool]::Parse($fields.terminal_index_retained)
        isolated_local_app_data = [bool]::Parse($fields.isolated_local_app_data)
        production_runtime_absent = [bool]::Parse($fields.production_runtime_absent)
        production_configured = [bool]::Parse($fields.production_configured)
        tray_boundary = 'current-user control Pipe V3 and redacted SessionSummary/ProductionStatus only'
        source_sha256 = $hashes
    }
    $resolvedEvidence = [IO.Path]::GetFullPath($EvidencePath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEvidence)) | Out-Null
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resolvedEvidence -Encoding utf8
    Write-Host "Phase 1 .NET 8 Agent/Tray recovery validation passed. Evidence: $resolvedEvidence"
}
finally {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory)
    if ($resolvedTemporary.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemporary).StartsWith('TermLink-WindowsApp-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force -ErrorAction SilentlyContinue
    }
}
