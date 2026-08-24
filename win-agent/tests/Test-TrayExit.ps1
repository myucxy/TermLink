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
    if ($startedAgentIds.Count -ne 0) {
        throw "Embedded runtime unexpectedly started TermLink.Agent process IDs: $($startedAgentIds -join ', ')."
    }

    $embeddedHost = $controllerType.GetField(
        'embeddedHost',
        [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::NonPublic).GetValue($controller)
    if (-not $embeddedHost.IsRunning) { throw 'Embedded runtime is not hosted in the current UI process.' }

    $controlAssembly = [Reflection.Assembly]::LoadFrom((Join-Path ([IO.Path]::GetDirectoryName($trayDll)) 'TermLink.AgentControl.dll'))
    $requestType = $controlAssembly.GetType('TermLink.Agent.RegistryRequest', $true)
    $commandType = $controlAssembly.GetType('TermLink.Agent.RegistryCommand', $true)
    $request = [Activator]::CreateInstance($requestType)
    $request.Command = [Enum]::Parse($commandType, 'List')
    $diagnostic = $diagnosticsType.GetMethod('Send').Invoke($null, @($options, $request, 5000))
    if (-not $diagnostic.Success) { throw 'External diagnostic pipe could not query the embedded runtime.' }

    $retry = $controllerType.GetMethod('RetryProductionConnection').Invoke($controller, @())
    if ($retry.Success -or $retry.Code -ne 'PRODUCTION_NOT_CONFIGURED') {
        throw 'Immediate reconnect did not dispatch directly to the unconfigured embedded runtime.'
    }

    $controllerType.GetMethod('ShutdownAndWait').Invoke($controller, @())
    Start-Sleep -Milliseconds 250
    if ($embeddedHost.IsRunning) { throw 'Tray exit shutdown left the embedded runtime running.' }
    Write-Output 'Tray embedded runtime, external diagnostics, immediate retry, and exit validation passed.'
}
finally {
    if ($null -ne $controller) { $controller.Dispose() }
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT', $originalAccountRoot)
    [Environment]::SetEnvironmentVariable('TERMLINK_ACCEPTANCE_CA_FILE', $originalAcceptanceCA)
    Remove-Item -LiteralPath $bufferRoot -Recurse -Force -ErrorAction SilentlyContinue
}
