[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'build\TermLink.WindowsTerminal.json'),
    [string]$SessionHostPath,
    [string]$WslDistribution = 'Ubuntu-24.04'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SessionHostPath)) {
    $SessionHostPath = & (Join-Path $PSScriptRoot 'Build-SessionHost.ps1')
}
$resolvedHost = (Resolve-Path -LiteralPath $SessionHostPath).Path

function New-ProfileCommandLine {
    param(
        [Parameter(Mandatory)][string]$ChildCommand,
        [Parameter(Mandatory)][string]$ManagedShell
    )

    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ChildCommand))
    '"{0}" --command-base64 {1} --managed-shell {2}' -f $resolvedHost, $encoded, $ManagedShell
}

$profiles = @(
    [ordered]@{
        guid = '{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}'
        name = 'TermLink CMD'
        commandline = New-ProfileCommandLine -ChildCommand 'cmd.exe' -ManagedShell 'command-prompt'
        startingDirectory = '%USERPROFILE%'
        hidden = $false
    }
    [ordered]@{
        guid = '{b2025574-4cc8-5264-b577-7c6532eff95a}'
        name = 'TermLink Windows PowerShell'
        commandline = New-ProfileCommandLine -ChildCommand 'powershell.exe -NoLogo' -ManagedShell 'windows-powershell'
        startingDirectory = '%USERPROFILE%'
        hidden = $false
    }
)

$pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
if ($pwsh) {
    $profiles += [ordered]@{
        guid = '{7cf2d180-c8be-5b08-99a0-268bd2dc1d6f}'
        name = 'TermLink PowerShell 7'
        commandline = New-ProfileCommandLine -ChildCommand ('"{0}" -NoLogo' -f $pwsh.Source) -ManagedShell 'powershell-7'
        startingDirectory = '%USERPROFILE%'
        hidden = $false
    }
}

if (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
    $profiles += [ordered]@{
        guid = '{f5652f1d-a6da-5199-b595-3a1255ce04b4}'
        name = 'TermLink WSL'
        commandline = New-ProfileCommandLine -ChildCommand (
            'wsl.exe -d "{0}"' -f $WslDistribution) -ManagedShell 'wsl'
        startingDirectory = '%USERPROFILE%'
        hidden = $false
    }
}

$document = [ordered]@{
    termlinkAddNoLogo = $true
    termlinkAddNoProfile = $false
    profiles = $profiles
}
foreach ($profile in $profiles) { $profile['closeOnExit'] = 'always' }
$destination = [IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $destination
[IO.Directory]::CreateDirectory($directory) | Out-Null
[IO.File]::WriteAllText(
    $destination,
    ($document | ConvertTo-Json -Depth 6) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

$destination
