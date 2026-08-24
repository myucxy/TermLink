#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$GoPath = 'D:\Dev\Env\Go\bin\go.exe',
    [string]$DotNetPath = 'D:\Dev\Env\dotnet-sdk-8.0.303\dotnet.exe',
    [ValidateRange(10, 300)][int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Executable {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found at the pinned development path: $Path"
    }
    return [IO.Path]::GetFullPath($Path)
}
function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Start-CapturedProcess {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][hashtable]$Environment
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FileName
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    foreach ($name in $Environment.Keys) { $start.Environment[$name] = [string]$Environment[$name] }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "Could not start $FileName" }
    return [pscustomobject]@{
        Process = $process
        OutputTask = $process.StandardOutput.ReadToEndAsync()
        ErrorTask = $process.StandardError.ReadToEndAsync()
    }
}

$moduleRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $moduleRoot '..\..'))
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$evidenceRoot = Join-Path $repoRoot "artifacts\technical-validation\e2e\$runId"
$serverOutput = Join-Path $evidenceRoot 'server'
$clientOutput = Join-Path $evidenceRoot 'client'
$resultPath = Join-Path $evidenceRoot 'relay-result.json'
[IO.Directory]::CreateDirectory($serverOutput) | Out-Null
[IO.Directory]::CreateDirectory($clientOutput) | Out-Null

$go = Assert-Executable -Path $GoPath -Label 'Go'
$dotnet = Assert-Executable -Path $DotNetPath -Label '.NET SDK'
$serverExecutable = Join-Path $serverOutput 'termlink-e2e-server.exe'
$clientProject = Join-Path $moduleRoot 'protocol-client\TermLink.TechnicalValidation.E2E.ProtocolClient.csproj'
$clientDll = Join-Path $clientOutput 'TermLink.TechnicalValidation.E2E.ProtocolClient.dll'
$capture = $null

try {
    Push-Location $moduleRoot
    try {
        & $go test ./...
        if ($LASTEXITCODE -ne 0) { throw "Go tests failed with exit code $LASTEXITCODE." }
        & $go build -trimpath -o $serverExecutable ./cmd/termlink-e2e-server
        if ($LASTEXITCODE -ne 0) { throw "Go server build failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
    }

    & $dotnet build $clientProject --configuration Release --output $clientOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw ".NET protocol client build failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $clientDll -PathType Leaf)) { throw 'Built protocol client DLL is missing.' }

    $port = Get-FreeTcpPort
    $capture = Start-CapturedProcess -FileName $serverExecutable -Arguments @('serve') -WorkingDirectory $moduleRoot -Environment @{
        TERMLINK_E2E_ADDR = "127.0.0.1:$port"
        TERMLINK_E2E_TOKEN = 'termlink-e2e-local-token-v1'
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $ready = $false
    do {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port/readyz" -TimeoutSec 2
            $ready = $response.StatusCode -eq 200
        }
        catch {
            $ready = $false
        }
        if ($ready) { break }
        if ($capture.Process.HasExited) { throw 'E2E Relay exited before readiness.' }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'E2E Relay did not become ready.' }

    & $dotnet $clientDll --root "http://127.0.0.1:$port/" --result $resultPath --timeout-seconds $TimeoutSeconds
    if ($LASTEXITCODE -ne 0) { throw "Protocol validation failed with exit code $LASTEXITCODE." }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.status -ne 'passed' -or $result.product_e2e -ne $false) {
        throw 'Protocol validation result did not preserve its passed/non-product-E2E boundary.'
    }
    Write-Output "E2E technical validation passed. Evidence: $resultPath"
}
finally {
    if ($null -ne $capture) {
        try { if (-not $capture.Process.HasExited) { $capture.Process.Kill($true) } } catch { }
        try { [void]$capture.Process.WaitForExit(5000) } catch { }
        try { $capture.OutputTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $evidenceRoot 'server.stdout.log') -Encoding utf8 } catch { }
        try { $capture.ErrorTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $evidenceRoot 'server.stderr.log') -Encoding utf8 } catch { }
        try { $capture.Process.Dispose() } catch { }
    }
}
