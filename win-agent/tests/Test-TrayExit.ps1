[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$trayDll = Join-Path $PSScriptRoot '..\src\Tray\bin\Release\net8.0-windows\TermLink.Tray.dll'
$agentExecutable = Join-Path $PSScriptRoot '..\src\Agent\bin\Release\net8.0-windows\TermLink.Agent.exe'
if (-not (Test-Path -LiteralPath $trayDll -PathType Leaf)) { throw "Tray assembly not found: $trayDll" }
if (-not (Test-Path -LiteralPath $agentExecutable -PathType Leaf)) { throw "Agent executable not found: $agentExecutable" }

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $trayDll).Path)
$optionsType = $assembly.GetType('TermLink.Tray.TrayOptions', $true)
$controllerType = $assembly.GetType('TermLink.Tray.AgentController', $true)
$parse = $optionsType.GetMethod('Parse', [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static)
$pipeName = 'termlink-exit-validation-' + [guid]::NewGuid().ToString('N')
$bufferRoot = Join-Path ([IO.Path]::GetTempPath()) ('termlink-exit-validation-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($bufferRoot) | Out-Null

try {
    $parseArguments = New-Object object[] 1
    $parseArguments[0] = [string[]]@(
        '--agent-exe', (Resolve-Path -LiteralPath $agentExecutable).Path,
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
    $response = $controllerType.GetMethod('EnsureAgent').Invoke($controller, @())
    if (-not $response.Success) { throw 'Agent did not start for tray exit validation.' }

    $expectedPath = (Resolve-Path -LiteralPath $agentExecutable).Path
    $started = @(Get-Process -Name 'TermLink.Agent' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $expectedPath })
    if ($started.Count -ne 1) { throw "Expected one validation Agent, found $($started.Count)." }

    $controllerType.GetMethod('ShutdownAndWait').Invoke($controller, @())
    Start-Sleep -Milliseconds 250
    $remaining = @(Get-Process -Name 'TermLink.Agent' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $expectedPath })
    if ($remaining.Count -ne 0) { throw 'Tray exit shutdown left the validation Agent running.' }
    Write-Output 'Tray exit shutdown validation passed.'
}
finally {
    Remove-Item -LiteralPath $bufferRoot -Recurse -Force -ErrorAction SilentlyContinue
}
