[CmdletBinding()]
param(
    [string]$BashPath = 'D:\Dev\Env\Git\bin\bash.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Test {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Set-PrivateFixtureAcl {
    param([string]$Path)
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { return }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    foreach ($identity in @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'),
        [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    )) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance, $propagation, [Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

$packagePath = $PSScriptRoot
$serverPath = [IO.Path]::GetFullPath((Join-Path $packagePath '..\..'))
$policyPath = Join-Path $serverPath 'ops\Test-SingleNodeProductionDeployment.ps1'

Assert-Test (Test-Path -LiteralPath $BashPath -PathType Leaf) "Bash not found: $BashPath"
foreach ($retiredAsset in @('compose.yml', 'Caddyfile')) {
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $packagePath $retiredAsset))) `
        "The deployment package still requires the retired sidecar asset: $retiredAsset"
}
foreach ($scriptName in @('termlink-deploy')) {
    & $BashPath -n (Join-Path $packagePath $scriptName)
    Assert-Test ($LASTEXITCODE -eq 0) "Bash syntax validation failed: $scriptName"
}
$help = & $BashPath (Join-Path $packagePath 'termlink-deploy') --help
$helpText = $help -join "`n"
Assert-Test ($LASTEXITCODE -eq 0 -and $helpText.Contains('termlink-deploy install') -and
    $helpText.Contains('termlink-deploy upgrade') -and $helpText.Contains('termlink-deploy doctor')) `
    'Deployment help did not render the command contract.'

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixturePath = Join-Path $tempRoot ("termlink-single-node-policy-" + [Guid]::NewGuid().ToString('N'))
$fixturePath = [IO.Path]::GetFullPath($fixturePath)
Assert-Test ($fixturePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) `
    'Disposable fixture escaped the operating-system temp directory.'

try {
    $runtimePath = Join-Path $fixturePath 'runtime'
    $secretPath = Join-Path $fixturePath 'secrets'
    New-Item -ItemType Directory -Path $fixturePath -Force | Out-Null
    Set-PrivateFixtureAcl -Path $fixturePath
    New-Item -ItemType Directory -Path $runtimePath, $secretPath -Force | Out-Null
    & $BashPath (Join-Path $packagePath 'termlink-deploy') render-assets --output $runtimePath
    Assert-Test ($LASTEXITCODE -eq 0) 'The single-file installer could not render its runtime assets.'
    foreach ($name in @('compose.yml')) {
        Assert-Test (Test-Path -LiteralPath (Join-Path $runtimePath $name) -PathType Leaf) `
            "The single-file installer did not render $name."
    }
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $runtimePath 'Caddyfile'))) `
        'The single-file installer still rendered a Caddy configuration.'

    $postgresPassword = 'Postgres_Test_7Pz4mN8vQ2xK6cR9sT5yH3jF'
    $secrets = [ordered]@{
        'postgres-password' = $postgresPassword
        'initial-admin-password' = 'Initial_Admin_Test_8pQ4mN7vR2xK6cD9'
        'database-url' = "postgres://termlink:${postgresPassword}@postgres:5432/termlink?sslmode=disable"
        'token-signing-key' = 'Token_Signing_Test_7pQ3mN8vR2xK6cD9sH4jF5yT1wB'
        'account-deletion-identity-key' = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
    }
    foreach ($entry in $secrets.GetEnumerator()) {
        [IO.File]::WriteAllText((Join-Path $secretPath $entry.Key), $entry.Value,
            [Text.UTF8Encoding]::new($false))
    }

    $runtimeComposePath = $runtimePath.Replace('\', '/')
    $secretComposePath = $secretPath.Replace('\', '/')
    $image = 'docker.io/myucxy/termlink:v0.2.38@sha256:' + ('a' * 64)
    $envPath = Join-Path $fixturePath 'config.env'
    $environment = @(
        'TERMLINK_CONFIG_VERSION=2'
        'COMPOSE_PROJECT_NAME=termlink'
        "TERMLINK_IMAGE=$image"
        "TERMLINK_RUNTIME_DIR=$runtimeComposePath"
        "TERMLINK_SECRETS_DIR=$secretComposePath"
        'TERMLINK_PUBLIC_HOST=deploy.termlink.example.cn'
        'TERMLINK_PUBLIC_URL=https://deploy.termlink.example.cn:18443'
        'TERMLINK_PUBLIC_PORT=18443'
        'TERMLINK_HTTP_BIND_IP=127.0.0.1'
        'TERMLINK_HTTP_PORT=18080'
        'TERMLINK_BACKUP_RECIPIENT='
    )
    [IO.File]::WriteAllLines($envPath, $environment, [Text.UTF8Encoding]::new($false))

    & $policyPath -EnvFile $envPath -ComposeFile (Join-Path $runtimePath 'compose.yml')
    Assert-Test ($LASTEXITCODE -eq 0) 'Single-node production policy validation failed.'

    $releaseInstaller = Join-Path $fixturePath 'termlink-deploy-release'
    & (Join-Path $packagePath 'Build-Installer.ps1') -ServerImage $image -OutputPath $releaseInstaller
    Assert-Test ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $releaseInstaller -PathType Leaf)) `
        'The release installer builder did not create a single-file artifact.'
    $releaseText = [IO.File]::ReadAllText($releaseInstaller)
    Assert-Test (-not $releaseText.Contains('__TERMLINK_BUNDLED_IMAGE__', [StringComparison]::Ordinal) -and
        $releaseText.Contains("readonly BUNDLED_IMAGE='$image'", [StringComparison]::Ordinal)) `
        'The release installer did not pin the requested immutable Server image.'
    & $BashPath -n $releaseInstaller
    Assert-Test ($LASTEXITCODE -eq 0) 'The generated release installer has invalid Bash syntax.'
}
finally {
    if (Test-Path -LiteralPath $fixturePath) {
        $resolvedFixture = [IO.Path]::GetFullPath($fixturePath)
        Assert-Test ($resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) `
            'Refusing to remove a fixture outside the operating-system temp directory.'
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}

Write-Host 'TermLink single-node deployment package tests passed.'
