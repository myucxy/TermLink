[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$HostPath,
    [string]$DisplayPath,
    [string]$AgentControlPipe,
    [string]$AudioRecorderPath,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunId,
    [string]$TestArtifactRoot,
    [string]$ValidationPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require-WindowsEffects([bool]$Value, [string]$Code) {
    if (-not $Value) { throw [InvalidOperationException]::new($Code) }
}

function Assert-WindowsEffectsRecording($Record, [string]$Directory) {
    $file = Join-Path $Directory ([string]$Record.file)
    $metadataFile = Join-Path $Directory ([string]$Record.metadata)
    Require-WindowsEffects ([IO.File]::Exists($file) -and [IO.File]::Exists($metadataFile) -and
        [string]::Equals([IO.Path]::GetFullPath($file),
            [IO.Path]::GetFullPath((Join-Path $Directory ([IO.Path]::GetFileName($file)))),
            [StringComparison]::OrdinalIgnoreCase)) 'H02_RECORDING_PATH_INVALID'
    $metadata = Get-Content -LiteralPath $metadataFile -Raw | ConvertFrom-Json
    Require-WindowsEffects ([string]$metadata.EndpointId -ceq [string]$Record.endpoint -and
        [string]$metadata.WavPath -ceq $file -and
        [long]$metadata.Bytes -gt 0 -and [long]$metadata.Frames -gt 0 -and
        [long]$metadata.ExpectedFrames -eq [long]$metadata.SampleRate * 5 -and
        (Test-EffectsCaptureContinuity $metadata)) 'H02_RECORDER_METADATA_INVALID'
    $bytes = [IO.File]::ReadAllBytes($file)
    $sha = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    Require-WindowsEffects ([string]::Equals($sha, [string]$Record.sha256,
        [StringComparison]::OrdinalIgnoreCase)) 'H02_RECORDING_HASH_MISMATCH'
    $wave = Read-EffectsWave $bytes
    Require-WindowsEffects ($wave.Frames -eq [long]$metadata.Frames -and
        $wave.Channels -eq [int]$metadata.Channels -and
        $wave.Bits -eq [int]$metadata.BitsPerSample -and
        $wave.Encoding -ceq [string]$metadata.Encoding) 'H02_RECORDER_WAV_MISMATCH'
    [pscustomobject]@{ wave = $wave; metadata = $metadata; sha256 = $sha; file = $Record.file }
}

if ([string]::IsNullOrWhiteSpace($ValidationPath) -and -not [string]::IsNullOrWhiteSpace($HostPath)) {
    $ValidationPath = Join-Path (Split-Path -Parent $HostPath) 'TermLink.SessionHost.Validation.exe'
}
if ($SelfTest) {
    Require-WindowsEffects ([IO.Path]::IsPathFullyQualified($ValidationPath) -and
        [IO.File]::Exists($ValidationPath)) 'H02_VALIDATION_BINARY_MISSING'
    & $ValidationPath --native-window-effects-selftest
    Require-WindowsEffects ($LASTEXITCODE -eq 0) 'H02_PROBE_SELFTEST_FAILED'
    & (Join-Path $PSScriptRoot 'Invoke-TerminalEffectsProductE2E.ps1') -AudioSelfTest
    'Windows native effects self-test passed.'
    return
}

Require-WindowsEffects (-not [string]::IsNullOrWhiteSpace($RunId) -and
    $AgentControlPipe -cmatch '^[A-Za-z0-9._-]{1,200}$' -and
    [IO.Path]::IsPathFullyQualified($HostPath) -and [IO.File]::Exists($HostPath) -and
    [IO.Path]::IsPathFullyQualified($DisplayPath) -and [IO.File]::Exists($DisplayPath) -and
    [IO.Path]::IsPathFullyQualified($AudioRecorderPath) -and [IO.File]::Exists($AudioRecorderPath) -and
    [IO.Path]::IsPathFullyQualified($ValidationPath) -and [IO.File]::Exists($ValidationPath) -and
    [IO.Path]::IsPathFullyQualified($TestArtifactRoot) -and
    [IO.Directory]::Exists($TestArtifactRoot)) 'H02_INPUT_INVALID'
$runDirectory = Join-Path $TestArtifactRoot ('native-window-effects-' + $RunId)
$windowsEvidencePath = Join-Path $runDirectory 'evidence.json'
Require-WindowsEffects (-not [IO.Directory]::Exists($runDirectory)) 'H02_ARTIFACT_DIRECTORY_EXISTS'
& $ValidationPath --native-window-effects-probe $HostPath $DisplayPath $AgentControlPipe `
    $AudioRecorderPath $RunId $TestArtifactRoot
$probeExit = $LASTEXITCODE
Require-WindowsEffects ([IO.File]::Exists($windowsEvidencePath)) 'H02_PROBE_EVIDENCE_MISSING'
$evidence = Get-Content -LiteralPath $windowsEvidencePath -Raw | ConvertFrom-Json
if ($probeExit -ne 0 -or [string]$evidence.status -cne 'audio_pending_analysis') {
    throw [InvalidOperationException]::new('H02_NATIVE_PROBE_FAILED')
}

try {
    . (Join-Path $PSScriptRoot 'Invoke-TerminalEffectsProductE2E.ps1') -DefinitionsOnly -RunId $RunId |
        Out-Null
    $records = @($evidence.recordings)
    Require-WindowsEffects ($records.Count -eq 3 -and
        @($records.phase | Sort-Object -Unique).Count -eq 3) 'H02_RECORDING_PHASES_INVALID'
    $byPhase = @{}
    foreach ($record in $records) {
        Require-WindowsEffects ($record.phase -in @('bell-muted', 'bell-audible', 'decps-audible')) `
            'H02_RECORDING_PHASE_INVALID'
        $byPhase[[string]$record.phase] = Assert-WindowsEffectsRecording $record $runDirectory
    }
    Require-WindowsEffects (@($records.endpoint | Sort-Object -Unique).Count -eq 1) `
        'H02_AUDIO_ENDPOINT_CHANGED'
    $muted = Measure-EffectsTone $byPhase['bell-muted'].wave 523.251
    $audible = Measure-EffectsTone $byPhase['bell-audible'].wave 523.251
    $decps = @(@(523.251, 659.255, 783.991) | ForEach-Object {
        Measure-EffectsTone $byPhase['decps-audible'].wave $_
    })
    Require-WindowsEffects ($audible.amplitude -gt 0.002 -and $audible.bursts -eq 1) `
        'H02_REAL_BEL_SOUND_MISSING'
    Require-WindowsEffects ($muted.amplitude -lt [Math]::Max(0.001, $audible.amplitude * 0.2)) `
        'H02_MUTED_BEL_AUDIBLE'
    Require-WindowsEffects ($decps.Count -eq 3 -and $decps[0].amplitude -gt 0.002 -and
        $decps[1].amplitude -gt 0.002 -and $decps[2].amplitude -gt 0.002 -and
        $decps[0].peak_seconds -lt $decps[1].peak_seconds -and
        $decps[1].peak_seconds -lt $decps[2].peak_seconds) 'H02_REAL_DECPS_SOUND_INVALID'
    $evidence | Add-Member -NotePropertyName audio_analysis -NotePropertyValue ([ordered]@{
        endpoint = [string]$records[0].endpoint; muted_bel = $muted;
        audible_bel = $audible; decps = $decps
    }) -Force
    $evidence.status = 'passed'
} catch {
    $evidence.status = 'failed'
    $evidence | Add-Member -NotePropertyName audio_analysis_failure -NotePropertyValue ([string]$_.Exception.Message) -Force
    throw
} finally {
    [IO.File]::WriteAllText($windowsEvidencePath, (($evidence | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
}
$windowsEvidencePath
