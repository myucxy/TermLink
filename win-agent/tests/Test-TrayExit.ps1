[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$trayDll = Join-Path $PSScriptRoot '..\src\Tray\bin\Release\net8.0-windows\TermLink.dll'
if (-not (Test-Path -LiteralPath $trayDll -PathType Leaf)) { throw "Tray assembly not found: $trayDll" }

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $trayDll).Path)
$optionsType = $assembly.GetType('TermLink.Tray.TrayOptions', $true)
$controllerType = $assembly.GetType('TermLink.Tray.AgentController', $true)
$diagnosticsType = $assembly.GetType('TermLink.Tray.ExternalAgentDiagnostics', $true)
$parse = $optionsType.GetMethod('Parse', [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static)
$pipeName = 'termlink-exit-validation-' + [guid]::NewGuid().ToString('N')
$bufferRoot = Join-Path ([IO.Path]::GetTempPath()) ('termlink-exit-validation-' + [guid]::NewGuid().ToString('N'))
$accountRoot = Join-Path $bufferRoot 'local-app-data'
$acceptanceMarker = Join-Path $bufferRoot 'acceptance-root.cer'
[IO.Directory]::CreateDirectory($bufferRoot) | Out-Null
$originalAccountRoot = [Environment]::GetEnvironmentVariable('TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT')
$originalAcceptanceCA = [Environment]::GetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE')
$beforeAgentIds = @(Get-Process -Name 'TermLink.Agent' -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty Id)
$controller = $null
$startedAgentIds = @()

try {
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT', $accountRoot)
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE', $acceptanceMarker)
    $parseArguments = New-Object object[] 1
    $parseArguments[0] = [string[]]@(
        '--buffer-root', $bufferRoot,
        '--control-pipe', $pipeName
    )
    $options = $parse.Invoke($null, $parseArguments)
    $controller = [Activator]::CreateInstance(
        $controllerType,
        [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic,
        $null,
        @($options),
        $null)
    $interactiveStart = [Diagnostics.Stopwatch]::StartNew()
    $controllerType.GetMethod('StartInteractive').Invoke($controller, @())
    $interactiveStart.Stop()
    if ($interactiveStart.Elapsed -gt [TimeSpan]::FromSeconds(2)) {
        throw "Interactive startup blocked the UI for $($interactiveStart.Elapsed)."
    }
    $response = $controllerType.GetMethod('EnsureAgent').Invoke($controller, @())
    if (-not $response.Success) { throw 'Agent did not start for tray exit validation.' }

    $afterAgentIds = @(Get-Process -Name 'TermLink.Agent' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Id)
    $startedAgentIds = @($afterAgentIds | Where-Object { $_ -notin $beforeAgentIds })
    if ($startedAgentIds.Count -ne 1) {
        throw "Independent runtime did not start exactly one TermLink.Agent process: $($startedAgentIds -join ', ')."
    }

    $controlAssembly = [Reflection.Assembly]::LoadFrom((Join-Path ([IO.Path]::GetDirectoryName($trayDll)) 'TermLink.AgentControl.dll'))
    $requestType = $controlAssembly.GetType('TermLink.Agent.RegistryRequest', $true)
    $commandType = $controlAssembly.GetType('TermLink.Agent.RegistryCommand', $true)
    $request = [Activator]::CreateInstance($requestType)
    $request.Command = [Enum]::Parse($commandType, 'List')
    $diagnostic = $diagnosticsType.GetMethod('Send').Invoke($null, @($options, $request, 5000))
    if (-not $diagnostic.Success) { throw 'External diagnostic pipe could not query the independent runtime.' }

    $retry = $controllerType.GetMethod('RetryProductionConnection').Invoke($controller, @())
    if ($retry.Success -or $retry.Code -ne 'PRODUCTION_NOT_CONFIGURED') {
        throw 'Immediate reconnect did not dispatch to the unconfigured independent runtime.'
    }

    # Closing only the UI supervisor must not terminate Relay/Agent ownership.
    $controller.Dispose()
    $controller = $null
    Start-Sleep -Milliseconds 250
    $survivors = @(Get-Process -Id $startedAgentIds -ErrorAction SilentlyContinue)
    if ($survivors.Count -ne 1) { throw 'Disposing the UI supervisor terminated the independent Agent.' }
    foreach ($survivor in $survivors) { $survivor.Dispose() }
    $request = [Activator]::CreateInstance($requestType)
    $request.Command = [Enum]::Parse($commandType, 'List')
    $diagnostic = $diagnosticsType.GetMethod('Send').Invoke($null, @($options, $request, 5000))
    if (-not $diagnostic.Success) { throw 'The Agent control pipe stopped when the UI supervisor was disposed.' }

    # A replacement UI attaches to the same Agent rather than creating a second owner.
    $controller = [Activator]::CreateInstance(
        $controllerType,
        [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic,
        $null,
        @($options),
        $null)
    $reopened = $controllerType.GetMethod('EnsureAgent').Invoke($controller, @())
    if (-not $reopened.Success) { throw 'A replacement UI could not attach to the independent Agent.' }
    $afterReopenIds = @(Get-Process -Name 'TermLink.Agent' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Id)
    $additionalAgentIds = @($afterReopenIds | Where-Object { $_ -notin $beforeAgentIds -and $_ -notin $startedAgentIds })
    if ($additionalAgentIds.Count -ne 0) {
        throw "A replacement UI started duplicate Agent process IDs: $($additionalAgentIds -join ', ')."
    }
    $controllerType.GetMethod('ShutdownAndWait').Invoke($controller, @())
    Start-Sleep -Milliseconds 250
    if (@(Get-Process -Id $startedAgentIds -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'Explicit Agent shutdown left the independent runtime running.'
    }
    Write-Output 'Tray independent Agent, authenticated diagnostics, UI restart survival, and explicit shutdown validation passed.'
}
finally {
    if ($null -ne $controller) { $controller.Dispose() }
    foreach ($processId in $startedAgentIds) {
        $owned = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -ne $owned) {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            $owned.Dispose()
        }
    }
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT', $originalAccountRoot)
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE', $originalAcceptanceCA)
    Remove-Item -LiteralPath $bufferRoot -Recurse -Force -ErrorAction SilentlyContinue
}
