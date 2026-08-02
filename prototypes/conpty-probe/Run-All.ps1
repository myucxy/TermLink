$ErrorActionPreference = 'Stop'

foreach ($shell in @('cmd', 'powershell', 'pwsh', 'wsl')) {
    "`n===== $shell ====="
    & (Join-Path $PSScriptRoot 'ConPtyProbe.ps1') -Shell $shell
}

'All available ConPTY probe cases passed.'
