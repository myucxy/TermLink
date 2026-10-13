[CmdletBinding()]
param([Parameter(Mandatory)][string]$ValidationPath)

$ErrorActionPreference = 'Stop'
$windowsEvidencePath = 'native-window-effects-fixture/evidence.json'
. (Join-Path $PSScriptRoot 'Invoke-TerminalEffectsProductE2E.ps1') -DefinitionsOnly -RunId 'windows-effects-selftest' | Out-Null
if ($windowsEvidencePath -cne 'native-window-effects-fixture/evidence.json' -or
    $null -eq (Get-Command Read-EffectsWave -ErrorAction SilentlyContinue)) {
    throw 'H02_AUDIO_IMPORT_CHANGED_WINDOWS_EVIDENCE_PATH'
}
& (Join-Path $PSScriptRoot 'Invoke-WindowsTerminalEffectsProductE2E.ps1') `
    -SelfTest -ValidationPath $ValidationPath
