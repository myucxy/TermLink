[CmdletBinding()]
param(
    [switch]$DefinitionsOnly,
    [switch]$AudioSelfTest,
    [switch]$DiagnosticSelfTest,
    [ValidateSet('H01H02H03', 'C02')][string]$Family = 'H01H02H03',
    [ValidateSet('unsupported', 'cursor-restore', 'tab-restore')]
    [string]$DiagnosticVector = 'unsupported',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunId,
    [guid]$ExpectedSessionId,
    [ValidatePattern('^https://[^/]+$')][string]$ExpectedOrigin,
    [ValidatePattern('^emulator-[0-9]{4}$')][string]$EmulatorSerial = 'emulator-5556',
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')][string]$AndroidPackageName = 'dev.termlink.harness',
    [string]$AdbPath,
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_.]{2,127}$')][string]$UiTestPackage = 'dev.termlink.terminal.nativecore.test',
    [string]$UiHierarchyScript = (Join-Path $PSScriptRoot 'Get-TerminalProductUiHierarchy.ps1'),
    [string]$TriggerPath,
    [string]$EvidencePath,
    [string]$AudioRecorderPath,
    [Diagnostics.Process]$OwnedSessionHost,
    [string]$ExpectedHostPath,
    [scriptblock]$StopOwnedSessionHost
)

# Attach only to the Broker runner's already armed, isolated Windows session.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Assert-TerminalSemanticProbe.ps1')

function Require-Effects([bool]$Condition, [string]$Code) {
    if (-not $Condition) { throw [InvalidOperationException]::new($Code) }
}

function Read-EffectsWave([byte[]]$Bytes) {
    Require-Effects ($Bytes.Length -ge 44 -and $Bytes.Length -le 128MB + 1024 -and
        [Text.Encoding]::ASCII.GetString($Bytes, 0, 4) -ceq 'RIFF' -and
        [Text.Encoding]::ASCII.GetString($Bytes, 8, 4) -ceq 'WAVE' -and
        [BitConverter]::ToUInt32($Bytes, 4) -eq $Bytes.Length - 8) 'H02_WAV_RIFF_INVALID'
    $offset = 12; $format = $null; $dataOffset = -1; $dataLength = 0
    while ($offset -le $Bytes.Length - 8) {
        $name = [Text.Encoding]::ASCII.GetString($Bytes, $offset, 4)
        $size = [long][BitConverter]::ToUInt32($Bytes, $offset + 4)
        $body = $offset + 8
        Require-Effects ($body + $size -le $Bytes.Length -and
            $body + $size + ($size % 2) -le $Bytes.Length) 'H02_WAV_CHUNK_INVALID'
        if ($name -ceq 'fmt ') {
            Require-Effects ($null -eq $format -and $size -ge 16 -and $size -le 274) 'H02_WAV_FORMAT_INVALID'
            $tag = [BitConverter]::ToUInt16($Bytes, $body)
            $channels = [BitConverter]::ToUInt16($Bytes, $body + 2)
            $rate = [BitConverter]::ToUInt32($Bytes, $body + 4)
            $average = [BitConverter]::ToUInt32($Bytes, $body + 8)
            $align = [BitConverter]::ToUInt16($Bytes, $body + 12)
            $bits = [BitConverter]::ToUInt16($Bytes, $body + 14)
            if ($tag -eq 0xfffe) {
                Require-Effects ($size -ge 40 -and [BitConverter]::ToUInt16($Bytes, $body + 16) -ge 22) 'H02_WAV_EXTENSIBLE_INVALID'
                $subtype = [byte[]]::new(16)
                [Array]::Copy($Bytes, $body + 24, $subtype, 0, 16)
                $guid = [guid]::new($subtype).ToString('D')
                $tag = switch ($guid) {
                    '00000001-0000-0010-8000-00aa00389b71' { 1 }
                    '00000003-0000-0010-8000-00aa00389b71' { 3 }
                    default { 0 }
                }
            }
            Require-Effects (($tag -eq 1 -and $bits -in @(8,16,24,32)) -or
                ($tag -eq 3 -and $bits -in @(32,64))) 'H02_WAV_ENCODING_UNSUPPORTED'
            Require-Effects ($channels -ge 1 -and $channels -le 8 -and $rate -ge 8000 -and
                $rate -le 192000 -and $align -eq ($channels * $bits / 8) -and
                $average -eq ($rate * $align) -and $average -le 2MB) 'H02_WAV_FORMAT_BOUNDS'
            $format = [pscustomobject]@{ Tag = $tag; Channels = $channels; Rate = $rate;
                Bits = $bits; Align = $align }
        } elseif ($name -ceq 'data') {
            Require-Effects ($dataOffset -lt 0) 'H02_WAV_DUPLICATE_DATA'
            $dataOffset = $body; $dataLength = $size
        }
        $offset = [int]($body + $size + ($size % 2))
    }
    Require-Effects ($offset -eq $Bytes.Length -and $null -ne $format -and $dataOffset -ge 0 -and
        $dataLength % $format.Align -eq 0) 'H02_WAV_DATA_INVALID'
    $frames = [int]($dataLength / $format.Align)
    $step = [Math]::Max(1, [int][Math]::Floor($format.Rate / 8000))
    $samples = [double[]]::new([int][Math]::Ceiling($frames / $step))
    for ($i = 0; $i -lt $samples.Length; $i++) {
        $position = $dataOffset + $i * $step * $format.Align
        $value = if ($format.Tag -eq 3 -and $format.Bits -eq 32) {
            [double][BitConverter]::ToSingle($Bytes, $position)
        } elseif ($format.Tag -eq 3) {
            [BitConverter]::ToDouble($Bytes, $position)
        } elseif ($format.Bits -eq 8) {
            ([int]$Bytes[$position] - 128) / 128.0
        } elseif ($format.Bits -eq 16) {
            [BitConverter]::ToInt16($Bytes, $position) / 32768.0
        } elseif ($format.Bits -eq 24) {
            $signed = [int]$Bytes[$position] -bor ([int]$Bytes[$position + 1] -shl 8) -bor
                ([int]$Bytes[$position + 2] -shl 16)
            if (($signed -band 0x800000) -ne 0) { $signed -= 0x1000000 }
            $signed / 8388608.0
        } else {
            [BitConverter]::ToInt32($Bytes, $position) / 2147483648.0
        }
        Require-Effects ([double]::IsFinite($value) -and [Math]::Abs($value) -le 1.5) 'H02_WAV_SAMPLE_INVALID'
        $samples[$i] = $value
    }
    [pscustomobject]@{ Samples = $samples; Rate = [double]$format.Rate / $step;
        Frames = $frames; Channels = $format.Channels; Bits = $format.Bits;
        Encoding = $(if ($format.Tag -eq 3) { 'IEEE_FLOAT' } else { 'PCM' }) }
}

function Measure-EffectsTone($Wave, [double]$Frequency) {
    $length = [Math]::Max(128, [int][Math]::Round($Wave.Rate * 0.08))
    $stride = [Math]::Max(1, [int][Math]::Round($Wave.Rate * 0.02))
    $levels = [Collections.Generic.List[double]]::new()
    $coefficient = 2.0 * [Math]::Cos(2.0 * [Math]::PI * $Frequency / $Wave.Rate)
    for ($start = 0; $start + $length -le $Wave.Samples.Length; $start += $stride) {
        $a = 0.0; $b = 0.0
        for ($i = $start; $i -lt $start + $length; $i++) {
            $next = $Wave.Samples[$i] + $coefficient * $a - $b
            $b = $a; $a = $next
        }
        $power = [Math]::Max(0.0, $a * $a + $b * $b - $coefficient * $a * $b)
        $levels.Add(2.0 * [Math]::Sqrt($power) / $length)
    }
    $maximum = 0.0; $peak = -1
    for ($i = 0; $i -lt $levels.Count; $i++) {
        if ($levels[$i] -gt $maximum) { $maximum = $levels[$i]; $peak = $i }
    }
    $threshold = [Math]::Max(0.001, $maximum * 0.3)
    $bursts = 0; $active = $false
    foreach ($level in $levels) {
        if ($level -ge $threshold -and -not $active) { $bursts++; $active = $true }
        elseif ($level -lt $threshold) { $active = $false }
    }
    [pscustomobject]@{ frequency_hz = $Frequency; amplitude = $maximum;
        peak_seconds = $(if ($peak -ge 0) { $peak * $stride / $Wave.Rate } else { $null });
        bursts = $bursts }
}

function Test-EffectsCaptureContinuity($Metadata) {
    if ($null -eq $Metadata) { return $false }
    foreach ($name in @('Frames', 'ExpectedFrames', 'Discontinuities',
        'DiscontinuityDetails', 'DiscontinuityDetailsTruncated')) {
        if ($null -eq $Metadata.PSObject.Properties[$name]) { return $false }
    }
    $details = $Metadata.DiscontinuityDetails
    if ([long]$Metadata.ExpectedFrames -le 0 -or
        [long]$Metadata.Frames -ne [long]$Metadata.ExpectedFrames -or
        $null -eq $details -or $Metadata.DiscontinuityDetailsTruncated -ne $false) { return $false }
    if ([int]$Metadata.Discontinuities -eq 0) { return $details.Count -eq 0 }
    if ([int]$Metadata.Discontinuities -ne 1 -or $details.Count -ne 1) { return $false }
    $detail = $details[0]
    return $null -ne $detail -and $detail.FirstPacket -eq $true -and
        [long]$detail.FrameOffset -eq 0 -and
        [long]$detail.PacketFrames -gt 0 -and
        [long]$detail.PacketFrames -le [long]$Metadata.Frames -and
        ([long]$detail.Flags -band 1) -ne 0
}

function Measure-EffectsToneWindow($Wave, [double]$Frequency, [double]$From, [double]$To) {
    $start = [int][Math]::Ceiling($From * $Wave.Rate)
    $end = [int][Math]::Floor($To * $Wave.Rate)
    if ($start -lt 0 -or $end -gt $Wave.Samples.Length -or
        $end - $start -lt [int]($Wave.Rate * 0.08)) { return $null }
    $samples = [double[]]::new($end - $start)
    [Array]::Copy($Wave.Samples, $start, $samples, 0, $samples.Length)
    return Measure-EffectsTone ([pscustomobject]@{ Samples = $samples; Rate = $Wave.Rate }) $Frequency
}

function Test-EffectsHostExitAudio($Wave, [double]$TriggerSeconds, [double]$ExitSeconds) {
    if ($TriggerSeconds -lt 0.25 -or $ExitSeconds -le $TriggerSeconds -or
        $ExitSeconds - $TriggerSeconds -gt 1.20 -or
        $ExitSeconds + 0.65 -gt $Wave.Samples.Length / $Wave.Rate) { return $false }
    $baseline = Measure-EffectsToneWindow $Wave 523.251 0 ($TriggerSeconds - 0.05)
    $before = Measure-EffectsToneWindow $Wave 523.251 ([Math]::Max($TriggerSeconds, $ExitSeconds - 0.30)) $ExitSeconds
    $after = Measure-EffectsToneWindow $Wave 523.251 ($ExitSeconds + 0.35) ($ExitSeconds + 0.65)
    return $null -ne $baseline -and $null -ne $before -and $null -ne $after -and
        $baseline.amplitude -lt 0.001 -and $before.amplitude -gt 0.002 -and
        $after.amplitude -lt [Math]::Max(0.001, $before.amplitude * 0.2)
}

function Test-EffectsHostExitProof($Proof, [string]$Id, [guid]$Session,
    [int]$HostProcessId, [long]$StartTicks, [string]$Path, [long]$BeforeStop, [long]$AfterStop) {
    if ($null -eq $Proof) { return $false }
    foreach ($name in @('run_id', 'session_id', 'host_pid', 'host_start_utc_ticks',
        'host_path', 'host_exited', 'host_forced_stop', 'host_exit_code', 'exited_at_timestamp')) {
        if ($null -eq $Proof.PSObject.Properties[$name]) { return $false }
    }
    try {
        return [string]$Proof.run_id -ceq $Id -and [guid]$Proof.session_id -eq $Session -and
            [int]$Proof.host_pid -eq $HostProcessId -and [long]$Proof.host_start_utc_ticks -eq $StartTicks -and
            [string]::Equals([IO.Path]::GetFullPath([string]$Proof.host_path), [IO.Path]::GetFullPath($Path),
                [StringComparison]::OrdinalIgnoreCase) -and
            $Proof.host_exited -eq $true -and $Proof.host_forced_stop -eq $false -and
            [int]$Proof.host_exit_code -eq 0 -and
            [long]$Proof.exited_at_timestamp -ge $BeforeStop -and
            [long]$Proof.exited_at_timestamp -le $AfterStop
    } catch { return $false }
}

function Test-EffectsAudioMath {
    $quietSamples = [double[]]::new(8000)
    for ($i = 0; $i -lt $quietSamples.Length; $i++) {
        $quietSamples[$i] = 0.00001 * [Math]::Sin(2 * [Math]::PI * 500 * $i / 8000)
    }
    $quietTone = Measure-EffectsTone ([pscustomobject]@{ Samples = $quietSamples; Rate = 8000.0 }) 500
    Require-Effects ($quietTone.amplitude -gt 0.000009 -and $quietTone.amplitude -lt 0.000011) `
        'H02_AUDIO_QUIET_TONE_POWER_TRUNCATED'
    $metadata = [pscustomobject]@{ Frames = 100; ExpectedFrames = 100;
        Discontinuities = 0; DiscontinuityDetails = @(); DiscontinuityDetailsTruncated = $false }
    Require-Effects (Test-EffectsCaptureContinuity $metadata) 'H02_AUDIO_CLEAN_CAPTURE_REJECTED'
    $detail = [pscustomobject]@{ FirstPacket = $true; FrameOffset = 0; PacketFrames = 10; Flags = 1 }
    $metadata.Discontinuities = 1; $metadata.DiscontinuityDetails = @($detail)
    Require-Effects (Test-EffectsCaptureContinuity $metadata) 'H02_AUDIO_STARTUP_GAP_REJECTED'
    $detail.FrameOffset = 10
    Require-Effects (-not (Test-EffectsCaptureContinuity $metadata)) 'H02_AUDIO_MIDDLE_GAP_ACCEPTED'
    $detail.FrameOffset = 0; $detail.FirstPacket = $false
    Require-Effects (-not (Test-EffectsCaptureContinuity $metadata)) 'H02_AUDIO_FALSE_FIRST_PACKET_ACCEPTED'
    $detail.FirstPacket = $true; $detail.Flags = 0
    Require-Effects (-not (Test-EffectsCaptureContinuity $metadata)) 'H02_AUDIO_MISSING_FLAG_ACCEPTED'
    $detail.Flags = 1; $metadata.DiscontinuityDetailsTruncated = $true
    Require-Effects (-not (Test-EffectsCaptureContinuity $metadata)) 'H02_AUDIO_TRUNCATED_DETAILS_ACCEPTED'
    $metadata.DiscontinuityDetailsTruncated = $false; $metadata.Frames = 99
    Require-Effects (-not (Test-EffectsCaptureContinuity $metadata)) 'H02_AUDIO_MISSING_FRAMES_ACCEPTED'
    $metadata.Frames = 100; $metadata.DiscontinuityDetails = @()
    Require-Effects (-not (Test-EffectsCaptureContinuity $metadata)) 'H02_AUDIO_MISSING_DETAILS_ACCEPTED'
    $metadata.DiscontinuityDetails = @($detail, $detail)
    Require-Effects (-not (Test-EffectsCaptureContinuity $metadata)) 'H02_AUDIO_EXTRA_DETAILS_ACCEPTED'
    $metadata.DiscontinuityDetails = @($detail)
    $metadata.PSObject.Properties.Remove('DiscontinuityDetails')
    Require-Effects (-not (Test-EffectsCaptureContinuity $metadata)) 'H02_AUDIO_ABSENT_DETAILS_ACCEPTED'
    $hostId = [guid]::NewGuid()
    $proof = [pscustomobject]@{ run_id = 'selftest'; session_id = "$hostId"; host_pid = 123;
        host_start_utc_ticks = 456L; host_path = 'C:\\test\\SessionHost.exe'; host_exited = $true;
        host_forced_stop = $false; host_exit_code = 0; exited_at_timestamp = 800L }
    $validProof = Test-EffectsHostExitProof $proof 'selftest' $hostId 123 456L 'C:\\test\\SessionHost.exe' 700L 900L
    Require-Effects $validProof 'H02_AUDIO_HOST_PROOF_REJECTED'
    $proof.host_exited = $false
    $validProof = Test-EffectsHostExitProof $proof 'selftest' $hostId 123 456L 'C:\\test\\SessionHost.exe' 700L 900L
    Require-Effects (-not $validProof) 'H02_AUDIO_LIVE_HOST_ACCEPTED'
    $proof.host_exited = $true
    $validProof = Test-EffectsHostExitProof $proof 'other-run' $hostId 123 456L 'C:\\test\\SessionHost.exe' 700L 900L
    Require-Effects (-not $validProof) 'H02_AUDIO_FOREIGN_RUN_ACCEPTED'
    $validProof = Test-EffectsHostExitProof $proof 'selftest' ([guid]::NewGuid()) 123 456L 'C:\\test\\SessionHost.exe' 700L 900L
    Require-Effects (-not $validProof) 'H02_AUDIO_FOREIGN_SESSION_ACCEPTED'
    $validProof = Test-EffectsHostExitProof $proof 'selftest' $hostId 124 456L 'C:\\test\\SessionHost.exe' 700L 900L
    Require-Effects (-not $validProof) 'H02_AUDIO_FOREIGN_PROCESS_ACCEPTED'
    $validProof = Test-EffectsHostExitProof $proof 'selftest' $hostId 123 456L 'C:\\test\\SessionHost.exe' 810L 900L
    Require-Effects (-not $validProof) 'H02_AUDIO_PREMATURE_EXIT_ACCEPTED'
    $proof.host_forced_stop = $true
    $validProof = Test-EffectsHostExitProof $proof 'selftest' $hostId 123 456L 'C:\\test\\SessionHost.exe' 700L 900L
    Require-Effects (-not $validProof) 'H02_AUDIO_FORCED_STOP_ACCEPTED'
    $proof.host_forced_stop = $false
    $exitSamples = [double[]]::new(16000)
    for ($i = 3200; $i -lt 6000; $i++) {
        $exitSamples[$i] = 0.1 * [Math]::Sin(2 * [Math]::PI * 523.251 * $i / 8000)
    }
    $exitWave = [pscustomobject]@{ Samples = $exitSamples; Rate = 8000.0 }
    Require-Effects (Test-EffectsHostExitAudio $exitWave 0.30 0.70) 'H02_AUDIO_EXIT_BOUNDARY_REJECTED'
    Require-Effects (-not (Test-EffectsHostExitAudio $exitWave 0.30 0.31)) 'H02_AUDIO_PRE_EXIT_SILENCE_ACCEPTED'
    Require-Effects (-not (Test-EffectsHostExitAudio $exitWave 0.30 1.20)) 'H02_AUDIO_LATE_EXIT_ACCEPTED'
    for ($i = 8800; $i -lt 11200; $i++) {
        $exitSamples[$i] = 0.1 * [Math]::Sin(2 * [Math]::PI * 523.251 * $i / 8000)
    }
    Require-Effects (-not (Test-EffectsHostExitAudio $exitWave 0.30 0.70)) 'H02_AUDIO_POST_EXIT_SOUND_ACCEPTED'
    $rate = 48000; $frames = $rate
    $memory = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($memory)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([uint32](36 + $frames * 4))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
        $writer.Write([uint32]16); $writer.Write([uint16]3); $writer.Write([uint16]1)
        $writer.Write([uint32]$rate); $writer.Write([uint32]($rate * 4))
        $writer.Write([uint16]4); $writer.Write([uint16]32)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $writer.Write([uint32]($frames * 4))
        for ($i = 0; $i -lt $frames; $i++) {
            $sample = if ($i -ge 12000 -and $i -lt 24000) {
                [single](0.10 * [Math]::Sin(2 * [Math]::PI * 523.251 * $i / $rate))
            } else { [single]0 }
            $writer.Write($sample)
        }
        $writer.Flush()
        $bytes = $memory.ToArray()
        $wave = Read-EffectsWave $bytes
        $tone = Measure-EffectsTone $wave 523.251
        $wrong = Measure-EffectsTone $wave 997.0
        Require-Effects ($tone.amplitude -gt 0.05 -and $tone.bursts -eq 1 -and
            $wrong.amplitude -lt 0.01) 'H02_AUDIO_TONE_SELFTEST_FAILED'
        $extension = [IO.MemoryStream]::new()
        $extWriter = [IO.BinaryWriter]::new($extension)
        try {
            $extWriter.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
            $extWriter.Write([uint32]76)
            $extWriter.Write([Text.Encoding]::ASCII.GetBytes('WAVEJUNK'))
            $extWriter.Write([uint32]1); $extWriter.Write([byte]42); $extWriter.Write([byte]0)
            $extWriter.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
            $extWriter.Write([uint32]40); $extWriter.Write([uint16]0xfffe)
            $extWriter.Write([uint16]2); $extWriter.Write([uint32]48000); $extWriter.Write([uint32]384000)
            $extWriter.Write([uint16]8); $extWriter.Write([uint16]32); $extWriter.Write([uint16]22)
            $extWriter.Write([uint16]32); $extWriter.Write([uint32]3)
            $extWriter.Write([guid]::Parse('00000003-0000-0010-8000-00aa00389b71').ToByteArray())
            $extWriter.Write([Text.Encoding]::ASCII.GetBytes('fact'))
            $extWriter.Write([uint32]4); $extWriter.Write([uint32]0)
            $extWriter.Write([Text.Encoding]::ASCII.GetBytes('data')); $extWriter.Write([uint32]0)
            $extWriter.Flush()
            $extended = $extension.ToArray()
            [BitConverter]::GetBytes([uint32]($extended.Length - 8)).CopyTo($extended, 4)
            $parsed = Read-EffectsWave $extended
            Require-Effects ($parsed.Encoding -ceq 'IEEE_FLOAT' -and $parsed.Channels -eq 2 -and
                $parsed.Bits -eq 32 -and $parsed.Frames -eq 0) 'H02_AUDIO_EXTENSIBLE_SELFTEST_FAILED'
            $extended[16] = 3
            $rejected = $false
            try { $null = Read-EffectsWave $extended } catch { $rejected = $true }
            Require-Effects $rejected 'H02_AUDIO_BAD_PADDING_ACCEPTED'
        } finally { $extWriter.Dispose(); $extension.Dispose() }
        $bytes[4] = 0
        $rejected = $false
        try { $null = Read-EffectsWave $bytes } catch { $rejected = $true }
        Require-Effects $rejected 'H02_AUDIO_BAD_RIFF_ACCEPTED'
        'H02 audio WAV, continuity, and host-exit self-test passed; no ADB or recorder was started.'
    } finally { $writer.Dispose(); $memory.Dispose() }
}

function Start-EffectsAudioCapture([string]$Name, [int]$Seconds = 5) {
    $wav = Join-Path (Split-Path -Parent $EvidencePath) ("effects-audio-$Name.wav")
    Require-Effects (-not [IO.File]::Exists($wav)) 'H02_AUDIO_FILE_ALREADY_EXISTS'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $AudioRecorderPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--record-loopback', $wav, [string]$Seconds)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Require-Effects ($null -ne $process) 'H02_RECORDER_START_FAILED'
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $ready = $process.StandardError.ReadLineAsync()
        Require-Effects ($ready.Wait(10000) -and
            [string]$ready.Result -cmatch '^LOOPBACK_READY .+$') 'H02_RECORDER_NOT_READY'
        $errors = $process.StandardError.ReadToEndAsync()
        return [pscustomobject]@{ Process = $process; Output = $output; Errors = $errors;
            Wav = $wav; Seconds = $Seconds; Endpoint = ([string]$ready.Result).Substring(15);
            StartedAt = [Diagnostics.Stopwatch]::GetTimestamp() }
    } catch {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
        throw
    }
}

function Complete-EffectsAudioCapture($Capture) {
    Require-Effects ($Capture.Process.WaitForExit(($Capture.Seconds + 15) * 1000)) 'H02_RECORDER_TIMEOUT'
    Require-Effects ($Capture.Process.ExitCode -eq 0) 'H02_RECORDER_FAILED'
    $metadata = $Capture.Output.Result | ConvertFrom-Json -ErrorAction Stop
    Require-Effects ([string]$metadata.EndpointId -ceq $Capture.Endpoint -and
        [string]$metadata.WavPath -ceq $Capture.Wav -and
        [long]$metadata.Bytes -gt 0 -and [long]$metadata.Frames -gt 0 -and
        [long]$metadata.ExpectedFrames -eq [long]$metadata.SampleRate * $Capture.Seconds -and
        [long]$metadata.Bytes -eq [long]$metadata.Frames * [int]$metadata.Channels * [int]$metadata.BitsPerSample / 8 -and
        (Test-EffectsCaptureContinuity $metadata) -and
        [IO.File]::Exists($Capture.Wav)) 'H02_RECORDER_METADATA_INVALID'
    $bytes = [IO.File]::ReadAllBytes($Capture.Wav)
    $wave = Read-EffectsWave $bytes
    Require-Effects ($wave.Frames -eq [long]$metadata.Frames -and
        $wave.Channels -eq [int]$metadata.Channels -and
        $wave.Bits -eq [int]$metadata.BitsPerSample -and
        $wave.Encoding -ceq [string]$metadata.Encoding -and
        [Math]::Abs($wave.Rate - [double]$metadata.SampleRate / [Math]::Max(1,[int][Math]::Floor([double]$metadata.SampleRate / 8000))) -lt 0.01) `
        'H02_RECORDER_WAV_MISMATCH'
    return [pscustomobject]@{ wave = $wave; metadata = $metadata;
        capture_metadata = [ordered]@{ frames = $metadata.Frames;
            expected_frames = $metadata.ExpectedFrames;
            discontinuities = $metadata.Discontinuities;
            discontinuity_details = $metadata.DiscontinuityDetails;
            discontinuity_details_truncated = $metadata.DiscontinuityDetailsTruncated };
        file = [IO.Path]::GetFileName($Capture.Wav);
        sha256 = (Get-FileHash -LiteralPath $Capture.Wav -Algorithm SHA256).Hash }
}

function Stop-EffectsAudioCapture($Capture) {
    if ($null -eq $Capture) { return }
    try { if (-not $Capture.Process.HasExited) { $Capture.Process.Kill($true) } }
    finally { $Capture.Process.Dispose() }
}

function Restore-EffectsAndroidSettings([switch]$AfterHostExit) {
    $restored = [ordered]@{ status = 'verified'; notification_permission = $null;
        alerts = $null; sounds = $null; clipboard = $null }
    try {
      if ($AfterHostExit -and ($null -ne $script:initialAlerts -or $null -ne $script:initialSounds -or
        $null -ne $script:initialClipboard)) {
        # Host exit leaves the device list, so the status menu is intentionally unavailable here.
        foreach ($setting in @(
            @{ key = 'alerts'; pref = 'terminal_alerts'; initial = $script:initialAlerts },
            @{ key = 'sounds'; pref = 'terminal_sounds'; initial = $script:initialSounds },
            @{ key = 'clipboard'; pref = 'terminal_clipboard'; initial = $script:initialClipboard }
        )) {
            if ($null -eq $setting.initial) { continue }
            $expected = if ($setting.initial -ceq 'true') { 'true' } else { 'false' }
            $expression = [string]::Concat('s/name=', [char]34, $setting.pref, [char]34, ' value=', [char]34, '[^', [char]34, '*/', 'name=', [char]34, $setting.pref, [char]34, ' value=', [char]34, $expected, [char]34, '/')
            $null = Invoke-Adb @('shell', 'run-as', $AndroidPackageName, 'sed', '-i', '-e', $expression,
                'shared_prefs/terminal-platform-settings.xml')
            $restored[$setting.key] = $expected
        }
        $restored.restore_method = 'direct_debug_prefs_after_host_exit'
        $script:directSettingsRestored = $true
        $null = Invoke-Adb @('shell', 'am', 'force-stop', $AndroidPackageName)
        Start-Sleep -Milliseconds 500
        $null = Invoke-Adb @('shell', 'am', 'start', '-W', '-n',
            "`$AndroidPackageName/dev.termlink.product.ProductActivity")
      } elseif ($null -ne $script:initialAlerts -or $null -ne $script:initialSounds -or
        $null -ne $script:initialClipboard) {
        if (-not $AfterHostExit) { Resume-TestSession }
        for ($restoreAttempt = 1; $restoreAttempt -le 2; $restoreAttempt++) {
            Open-Menu
            $allFound = $true
            foreach ($setting in @(
                @{ key = 'alerts'; initial = $script:initialAlerts; names = @('终端提醒', 'Terminal alerts') },
                @{ key = 'sounds'; initial = $script:initialSounds; names = @('终端提示音', 'Terminal sounds') },
                @{ key = 'clipboard'; initial = $script:initialClipboard; names = @('允许终端请求复制', 'Ask before copying terminal text') }
            )) {
                if ($null -eq $setting.initial) { continue }
                $restoreDeadline = [DateTime]::UtcNow.AddSeconds(15)
                $current = $null
                do {
                    $current = Get-UiToggle $setting.names
                    if ($null -ne $current) { break }
                    Start-Sleep -Milliseconds 300
                } while ([DateTime]::UtcNow -lt $restoreDeadline)
                if ($null -eq $current) { $allFound = $false; break }
            }
            if ($allFound) { break }
            if ($restoreAttempt -lt 2) {
                $null = Require-Adb @('shell', 'input', 'keyevent', 'KEYCODE_BACK') 'SETTING_RETRY_CLOSE_FAILED'
                Start-Sleep -Milliseconds 500
            }
        }
        Open-Menu
        foreach ($setting in @(
            @{ key = 'alerts'; initial = $script:initialAlerts; names = @('终端提醒', 'Terminal alerts') },
            @{ key = 'sounds'; initial = $script:initialSounds; names = @('终端提示音', 'Terminal sounds') },
            @{ key = 'clipboard'; initial = $script:initialClipboard; names = @('允许终端请求复制', 'Ask before copying terminal text') }
        )) {
            if ($null -eq $setting.initial) { continue }
            $restoreDeadline = [DateTime]::UtcNow.AddSeconds(15)
            $current = $null
            do {
                $current = Get-UiToggle $setting.names
                if ($null -ne $current) { break }
                Start-Sleep -Milliseconds 300
            } while ([DateTime]::UtcNow -lt $restoreDeadline)
            if ($null -eq $current) {
                # Debug acceptance fallback: targeted preferences restore when the
                # Compose popup is not exposed to the a11y capture after host exit.
                foreach ($directSetting in @(
                    @{ key = 'alerts'; pref = 'terminal_alerts'; initial = $script:initialAlerts },
                    @{ key = 'sounds'; pref = 'terminal_sounds'; initial = $script:initialSounds },
                    @{ key = 'clipboard'; pref = 'terminal_clipboard'; initial = $script:initialClipboard }
                )) {
                    if ($null -eq $directSetting.initial) { continue }
                    $expected = if ($directSetting.initial -ceq 'true') { 'true' } else { 'false' }
                    $expression = [string]::Concat('s/name=', [char]34, $directSetting.pref, [char]34, ' value=', [char]34, '[^', [char]34, '*/', 'name=', [char]34, $directSetting.pref, [char]34, ' value=', [char]34, $expected, [char]34, '/')
                    $null = Invoke-Adb @('shell', 'run-as', $AndroidPackageName, 'sed', '-i', '-e', $expression,
                        'shared_prefs/terminal-platform-settings.xml')
                    $restored[$directSetting.key] = $expected
                }
                $restored.restore_method = 'direct_debug_prefs'
                $script:directSettingsRestored = $true
                $null = Invoke-Adb @('shell', 'am', 'force-stop', $AndroidPackageName)
                Start-Sleep -Milliseconds 500
                $null = Invoke-Adb @('shell', 'am', 'start', '-W', '-n',
                    "$AndroidPackageName/dev.termlink.product.ProductActivity")
                break
            }
            Require-Effects ($null -ne $current) 'SETTING_RESTORE_NOT_FOUND'
            if ([string]$current.GetAttribute('checked') -cne $setting.initial) {
                Tap-Ui $setting.names 'SETTING_RESTORE_TAP_FAILED'
            }
            $verified = Get-UiToggle $setting.names
            Require-Effects ($null -ne $verified -and
                [string]$verified.GetAttribute('checked') -ceq $setting.initial) 'SETTING_RESTORE_MISMATCH'
            $restored[$setting.key] = $setting.initial
        }
        if (-not $script:directSettingsRestored) { Close-Menu }
      }
    } finally {
        if ($null -ne $script:initialNotificationPermission) {
            Set-NotificationPermission $script:initialNotificationPermission
            Require-Effects ((Get-NotificationPermission) -ceq $script:initialNotificationPermission) 'PERMISSION_RESTORE_MISMATCH'
            $restored.notification_permission = $script:initialNotificationPermission
        }
    }
    $evidence.android_settings_restore = $restored
    $script:frozenHistoryOpen = $false
    $script:initialNotificationPermission = $null
    $script:initialAlerts = $null
    $script:initialSounds = $null
    $script:initialClipboard = $null
}

function Record-EffectsPhase($Definition, $Before, [string]$Name, [string]$LiveMarker) {
    $capture = Start-EffectsAudioCapture $Name
    try {
        Start-Sleep -Milliseconds 300
        $after = Send-Phase $Definition ([long]$Before.live_ack) ([string]$Before.visible_text_sha256) $LiveMarker
        $recording = Complete-EffectsAudioCapture $capture
        return [pscustomobject]@{ after = $after; recording = $recording }
    } finally { Stop-EffectsAudioCapture $capture }
}

function Write-FastEffectsPhase($Definition) {
    Write-PhaseTrigger $Definition
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    do { Start-Sleep -Milliseconds 25 } while ([IO.File]::Exists($TriggerPath) -and
        [DateTime]::UtcNow -lt $deadline)
    Require-Effects (-not [IO.File]::Exists($TriggerPath)) 'H02_FAST_TRIGGER_TIMEOUT'
}

function Invoke-H02HostExitAudio($Before, [string]$LiveMarker, [string]$Endpoint) {
    $result = [ordered]@{ status = 'unverified'; reason = $null; host_pid = $null;
        host_start_utc_ticks = $null; host_path = $null; exit_seconds = $null;
        trigger_seconds = $null; proof = $null; recording = $null;
        baseline = $null; before_exit = $null; after_exit = $null }
    if (-not [IO.Path]::IsPathFullyQualified($ExpectedHostPath) -or
        -not [IO.File]::Exists($ExpectedHostPath) -or $OwnedSessionHost.HasExited) {
        $result.reason = 'H02_EXIT_HOST_NOT_OWNED'; return $result
    }
    try {
        $hostPath = [IO.Path]::GetFullPath($OwnedSessionHost.MainModule.FileName)
        $hostProcessId = $OwnedSessionHost.Id
        $startTicks = $OwnedSessionHost.StartTime.ToUniversalTime().Ticks
    } catch {
        $result.reason = 'H02_EXIT_HOST_IDENTITY_UNAVAILABLE'; return $result
    }
    if (-not [string]::Equals($hostPath, [IO.Path]::GetFullPath($ExpectedHostPath),
        [StringComparison]::OrdinalIgnoreCase)) {
        $result.reason = 'H02_EXIT_HOST_PATH_MISMATCH'; return $result
    }
    $result.host_pid = $hostProcessId; $result.host_start_utc_ticks = $startTicks; $result.host_path = $hostPath
    Require-Effects ([string]$arm.run_id -ceq $RunId -and [guid]$arm.session_id -eq $ExpectedSessionId) `
        'H02_EXIT_ARM_MISMATCH'
    Assert-Session $Before
    $capture = Start-EffectsAudioCapture 'host-exit' 8
    try {
        Start-Sleep -Milliseconds 300
        $marker = 'TLEF_' + ($RunId -replace '[^A-Za-z0-9]', '_') + '_HOST_EXIT'
        $definition = [pscustomobject]@{ phase = 'audio-host-exit'; marker = $marker;
            sequence = "$([char]27)[7;255;1,~${marker}`r`n" }
        Write-FastEffectsPhase $definition
        $triggerAt = [Diagnostics.Stopwatch]::GetTimestamp()
        $result.trigger_seconds = [Diagnostics.Stopwatch]::GetElapsedTime($capture.StartedAt, $triggerAt).TotalSeconds
        try {
            $ack = Invoke-Hook 'inspect' @{ live_marker = $LiveMarker } 2
            Assert-Session $ack
            if (-not (Test-EffectsPhaseObserved $definition $ack ([long]$Before.live_ack)) -or
                [string]$ack.visible_text_sha256 -ceq [string]$Before.visible_text_sha256 -or
                $ack.live_marker_visible -ne $true) {
                $result.reason = 'H02_EXIT_LONG_TONE_ACK_MISSING'; return $result
            }
        } catch {
            $result.reason = 'H02_EXIT_LONG_TONE_ACK_UNVERIFIED'; return $result
        }
        if ([Diagnostics.Stopwatch]::GetElapsedTime($triggerAt).TotalSeconds -gt 1.20) {
            $result.reason = 'H02_EXIT_LONG_TONE_TOO_OLD'; return $result
        }
        $beforeStop = [Diagnostics.Stopwatch]::GetTimestamp()
        try { $proof = & $StopOwnedSessionHost }
        catch { $result.reason = 'H02_EXIT_STOP_CALLBACK_FAILED'; return $result }
        $afterStop = [Diagnostics.Stopwatch]::GetTimestamp()
        $result.proof = $proof
        $validProof = Test-EffectsHostExitProof $proof $RunId $ExpectedSessionId $hostProcessId $startTicks `
            $hostPath $beforeStop $afterStop
        if (-not $validProof -or -not $OwnedSessionHost.HasExited -or
            $OwnedSessionHost.ExitCode -ne 0) {
            $result.reason = 'H02_EXIT_HOST_PROOF_INVALID'; return $result
        }
        $result.exit_seconds = [Diagnostics.Stopwatch]::GetElapsedTime(
            $capture.StartedAt, [long]$proof.exited_at_timestamp).TotalSeconds
        try { $recording = Complete-EffectsAudioCapture $capture }
        catch { $result.reason = 'H02_EXIT_CAPTURE_INVALID'; return $result }
        Require-Effects ([string]$recording.metadata.EndpointId -ceq $Endpoint) 'H02_AUDIO_ENDPOINT_CHANGED'
        $result.recording = [ordered]@{ phase = 'host-exit'; file = $recording.file;
            sha256 = $recording.sha256; capture_metadata = $recording.capture_metadata }
        $wave = $recording.wave
        $result.baseline = Measure-EffectsToneWindow $wave 523.251 0 ($result.trigger_seconds - 0.05)
        $result.before_exit = Measure-EffectsToneWindow $wave 523.251 `
            ([Math]::Max($result.trigger_seconds, $result.exit_seconds - 0.30)) $result.exit_seconds
        $result.after_exit = Measure-EffectsToneWindow $wave 523.251 `
            ($result.exit_seconds + 0.35) ($result.exit_seconds + 0.65)
        if (-not (Test-EffectsHostExitAudio $wave $result.trigger_seconds $result.exit_seconds)) {
            $result.reason = 'H02_EXIT_AUDIO_BOUNDARY_UNVERIFIED'; return $result
        }
        $result.status = 'passed'
        return $result
    } finally { Stop-EffectsAudioCapture $capture }
}

function Invoke-H02Audio($Before, [string]$LiveMarker) {
    $summary = [ordered]@{ status = 'unverified'; endpoint = $null; recordings = @();
        checks = @(); exit_queue = 'unverified'; exit_audio = $null; native_windows = 'unverified' }
    if ([string]::IsNullOrWhiteSpace($AudioRecorderPath)) {
        $evidence.unverified += 'H02 actual audio was not recorded: AudioRecorderPath was not supplied.'
        return $summary
    }
    Require-Effects ([IO.Path]::IsPathFullyQualified($AudioRecorderPath) -and
        [IO.File]::Exists($AudioRecorderPath) -and
        [IO.Path]::GetFileName($AudioRecorderPath).Equals('TermLink.TerminalSurface.Validation.exe',
            [StringComparison]::OrdinalIgnoreCase)) 'H02_RECORDER_PATH_INVALID'
    $nativeSettings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TermLink/terminal-alerts-v1.bin'
    if ([IO.File]::Exists($nativeSettings)) {
        $settings = [IO.File]::ReadAllBytes($nativeSettings)
        Require-Effects ($settings.Length -le 4 -and ($settings.Length -eq 3 -or $settings.Length -eq 4)) 'H02_NATIVE_SETTINGS_UNKNOWN'
        if ($settings[2] -ne 0) {
            $evidence.unverified += 'H02 Android sound capture was skipped because the Windows native sound switch is enabled on the same endpoint.'
            return $summary
        }
    }
    Open-Menu
    $soundToggle = Get-UiToggle @('终端提示音', 'Terminal sounds')
    Require-Effects ($null -ne $soundToggle) 'H02_SOUND_TOGGLE_MISSING'
    $script:initialSounds = [string]$soundToggle.GetAttribute('checked')
    if ($script:initialSounds -eq 'false') { Tap-Ui @('终端提示音', 'Terminal sounds') 'H02_SOUND_ENABLE_FAILED' }
    $soundToggle = Get-UiToggle @('终端提示音', 'Terminal sounds')
    Require-Effects ([string]$soundToggle.GetAttribute('checked') -eq 'true') 'H02_SOUND_NOT_ENABLED'
    Close-Menu
    Start-Sleep -Milliseconds 3500
    $marker = 'TLEF_' + ($RunId -replace '[^A-Za-z0-9]', '_')
    $bel = [string][char]7
    $esc = [string][char]27
    $bell = [pscustomobject]@{ phase = 'audio-bel'; marker = "${marker}_AUDIO_BEL";
        sequence = "${bel}${marker}_AUDIO_BEL`r`n" }
    $sent = Record-EffectsPhase $bell $Before 'bel' $LiveMarker
    $Before = $sent.after
    $belTone = Measure-EffectsTone $sent.recording.wave 523.251
    $summary.endpoint = [string]$sent.recording.metadata.EndpointId
    $summary.recordings += [ordered]@{ phase = 'bel'; file = $sent.recording.file;
        sha256 = $sent.recording.sha256; capture_metadata = $sent.recording.capture_metadata; tone = $belTone }
    if ($belTone.amplitude -lt 0.002 -or $belTone.bursts -ne 1) {
        $evidence.unverified += 'H02 BEL produced no unique 523 Hz recording; inspect Android DND/ringer/notification volume and audio routing.'
        return $summary
    }
    $summary.checks += 'live_bel_audible'
    Start-Sleep -Milliseconds 3500
    $decps = [pscustomobject]@{ phase = 'audio-decps'; marker = "${marker}_AUDIO_DECPS";
        sequence = "${esc}[7;8;1;5;8,~${marker}_AUDIO_DECPS`r`n" }
    $sent = Record-EffectsPhase $decps $Before 'decps' $LiveMarker
    $Before = $sent.after
    Require-Effects ([string]$sent.recording.metadata.EndpointId -ceq $summary.endpoint) 'H02_AUDIO_ENDPOINT_CHANGED'
    $tones = @(@(523.251, 659.255, 783.991) | ForEach-Object { Measure-EffectsTone $sent.recording.wave $_ })
    $summary.recordings += [ordered]@{ phase = 'decps'; file = $sent.recording.file;
        sha256 = $sent.recording.sha256; capture_metadata = $sent.recording.capture_metadata; tones = $tones }
    Require-Effects ($tones.Count -eq 3 -and $tones[0].amplitude -gt 0.002 -and
        $tones[1].amplitude -gt 0.002 -and $tones[2].amplitude -gt 0.002 -and
        $tones[0].peak_seconds -lt $tones[1].peak_seconds -and
        $tones[1].peak_seconds -lt $tones[2].peak_seconds) 'H02_DECPS_SOUND_INVALID'
    $summary.checks += 'live_decps_three_notes'
    Open-Menu
    Tap-Ui @('终端提示音', 'Terminal sounds') 'H02_SOUND_DISABLE_FAILED'
    $soundToggle = Get-UiToggle @('终端提示音', 'Terminal sounds')
    Require-Effects ([string]$soundToggle.GetAttribute('checked') -eq 'false') 'H02_SOUND_NOT_DISABLED'
    Close-Menu
    Start-Sleep -Milliseconds 3500
    $muted = [pscustomobject]@{ phase = 'audio-muted'; marker = "${marker}_AUDIO_MUTED";
        sequence = "${bel}${marker}_AUDIO_MUTED`r`n" }
    $sent = Record-EffectsPhase $muted $Before 'muted' $LiveMarker
    $Before = $sent.after
    Require-Effects ([string]$sent.recording.metadata.EndpointId -ceq $summary.endpoint) 'H02_AUDIO_ENDPOINT_CHANGED'
    $muteTone = Measure-EffectsTone $sent.recording.wave 523.251
    $summary.recordings += [ordered]@{ phase = 'muted'; file = $sent.recording.file;
        sha256 = $sent.recording.sha256; capture_metadata = $sent.recording.capture_metadata; tone = $muteTone }
    Require-Effects ($muteTone.amplitude -lt [Math]::Max(0.001, $belTone.amplitude * 0.2)) 'H02_MUTED_SOUND_OBSERVED'
    $summary.checks += 'sound_toggle_muted'
    Open-Menu
    Tap-Ui @('终端提示音', 'Terminal sounds') 'H02_SOUND_REENABLE_FAILED'
    $soundToggle = Get-UiToggle @('终端提示音', 'Terminal sounds')
    Require-Effects ([string]$soundToggle.GetAttribute('checked') -eq 'true') 'H02_SOUND_NOT_REENABLED'
    Close-Menu
    Start-Sleep -Milliseconds 3500
    $capture = Start-EffectsAudioCapture 'rate-limit' 6
    try {
        Start-Sleep -Milliseconds 300
        $first = [pscustomobject]@{ phase = 'audio-rate-first'; marker = "${marker}_RATE_FIRST";
            sequence = "${bel}${marker}_RATE_FIRST`r`n" }
        $second = [pscustomobject]@{ phase = 'audio-rate-second'; marker = "${marker}_RATE_SECOND";
            sequence = "${bel}${marker}_RATE_SECOND`r`n" }
        Write-FastEffectsPhase $first
        $firstAt = [Diagnostics.Stopwatch]::GetTimestamp()
        Start-Sleep -Milliseconds 500
        Write-FastEffectsPhase $second
        $gap = [Diagnostics.Stopwatch]::GetElapsedTime($firstAt).TotalMilliseconds
        Require-Effects ($gap -lt 2500) 'H02_RATE_TRIGGER_NOT_WITHIN_GATE'
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $candidate = Invoke-Hook 'inspect' @{ live_marker = $LiveMarker } 5
            Assert-Session $candidate
            if ((Test-EffectsPhaseObserved $second $candidate ([long]$Before.live_ack)) -and
                [string]$candidate.visible_text_sha256 -cne [string]$Before.visible_text_sha256) {
                $Before = $candidate; break
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        Require-Effects ([long]$Before.live_ack -gt [long]$sent.after.live_ack) 'H02_RATE_ACK_MISSING'
        $recording = Complete-EffectsAudioCapture $capture
        Require-Effects ([string]$recording.metadata.EndpointId -ceq $summary.endpoint) 'H02_AUDIO_ENDPOINT_CHANGED'
        $rateTone = Measure-EffectsTone $recording.wave 523.251
        $summary.recordings += [ordered]@{ phase = 'rate-limit'; file = $recording.file;
            sha256 = $recording.sha256; capture_metadata = $recording.capture_metadata;
            tone = $rateTone; trigger_gap_ms = $gap }
        $lateRateTone = $null
        $earlyRateTone = Measure-EffectsToneWindow $recording.wave 523.251 0 1.20
        $rateDebug = [ordered]@{ samples = $recording.wave.Samples.Length; rate = $recording.wave.Rate; earlyAmplitude = $earlyRateTone.amplitude; earlyBursts = $earlyRateTone.bursts; lateAmplitude = $(if ($lateRateTone) { $lateRateTone.amplitude } else { $null }) } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $EvidencePath) 'rate-debug.json'), $rateDebug + "`n")
        $lateRateTone = Measure-EffectsToneWindow $recording.wave 523.251 2.50 ([Math]::Min(
            $recording.wave.Samples.Length / $recording.wave.Rate, 5.50))
        Require-Effects ($earlyRateTone.amplitude -gt 0.002 -and $earlyRateTone.bursts -ge 1) 'H02_RATE_LIMIT_SOUND_INVALID'
        Require-Effects ($null -eq $lateRateTone -or
            $lateRateTone.amplitude -lt [Math]::Max(0.001, $earlyRateTone.amplitude * 0.2)) 'H02_RATE_LIMIT_SOUND_INVALID'
        $summary.checks += 'three_second_gate_suppressed_after_window'
    } finally { Stop-EffectsAudioCapture $capture }
    Start-Sleep -Milliseconds 3500
    $capture = Start-EffectsAudioCapture 'replay' 15
    try {
        Start-Sleep -Milliseconds 300
        $closed = Invoke-Hook 'close_view'
        Require-Effects ([guid]$closed.session_id -eq $ExpectedSessionId -and
            $closed.view_closed -eq $true) 'H02_REPLAY_VIEW_NOT_CLOSED'
        Require-Effects (-not $capture.Process.HasExited) 'H02_REPLAY_CAPTURE_ENDED_EARLY'
        $reopened = Invoke-Hook 'reopen_selected'
        Assert-Session $reopened
        Require-Effects ([Diagnostics.Stopwatch]::GetElapsedTime($capture.StartedAt).TotalSeconds -lt
            $capture.Seconds - 1) 'H02_REPLAY_CAPTURE_ENDED_EARLY'
        $recording = Complete-EffectsAudioCapture $capture
        Require-Effects ([string]$recording.metadata.EndpointId -ceq $summary.endpoint) 'H02_AUDIO_ENDPOINT_CHANGED'
        $replayTone = Measure-EffectsTone $recording.wave 523.251
        $summary.recordings += [ordered]@{ phase = 'replay'; file = $recording.file;
            sha256 = $recording.sha256; capture_metadata = $recording.capture_metadata; tone = $replayTone }
        Require-Effects ($replayTone.amplitude -lt [Math]::Max(0.001, $belTone.amplitude * 0.2)) 'H02_OLD_OUTPUT_REPLAYED_SOUND'
        $summary.checks += 'reopen_old_output_silent'
    } finally { Stop-EffectsAudioCapture $capture }
    if ($null -ne $OwnedSessionHost -and $null -ne $StopOwnedSessionHost -and
        -not [string]::IsNullOrWhiteSpace($ExpectedHostPath)) {
        $summary.exit_audio = Invoke-H02HostExitAudio $reopened $LiveMarker $summary.endpoint
        $summary.exit_queue = $summary.exit_audio.status
        if ($null -ne $summary.exit_audio.recording) {
            $summary.recordings += $summary.exit_audio.recording
        }
    }
    $summary.status = if ($summary.exit_queue -eq 'passed') {
        'android_live_and_host_exit_audio_passed'
    } else { 'android_live_audio_passed_exit_unverified' }
    if ($summary.exit_queue -ne 'passed') {
        $evidence.unverified += 'H02 audio after actual SessionHost exit remains unverified: ' + `
            $(if ($null -ne $summary.exit_audio) { $summary.exit_audio.reason } else { 'parent-run stop callback was not supplied' })
    }
    $evidence.unverified += 'H02 Windows native-window audio requires a separate recorded parent-run check.'
    return $summary
}

function Get-EffectsDiagnosticVector {
    $esc = [string][char]27
    switch ($DiagnosticVector) {
        'cursor-restore' {
            return [pscustomobject]@{ phase = 'cursor-restore';
                sequence = ($esc + 'P1$t1;1;1;@;@;F;0;2;L;BBAA' + $esc + '\');
                escaped_line = 'sequence=<ESC>P1$t';
                category = 'malformed'; reason = 'malformed DEC state report'; family = 'DCS' }
        }
        'tab-restore' {
            return [pscustomobject]@{ phase = 'tab-restore';
                sequence = ($esc + 'P2$t2/3/513' + $esc + '\');
                escaped_line = 'sequence=<ESC>P2$t';
                category = 'malformed'; reason = 'malformed DEC state report'; family = 'DCS' }
        }
        default {
            return [pscustomobject]@{ phase = 'unsupported'; sequence = ($esc + '[999t');
                escaped_line = 'sequence=<ESC>[999t'; category = 'unsupported';
                reason = 'window manipulation'; family = 'CSI' }
        }
    }
}

function Test-EffectsDiagnosticDetails([string]$Display, $Diagnostic) {
    $lines = @($Display -split '\r?\n')
    $start = [Array]::IndexOf($lines, 'TL-DIAG/2')
    if ($start -lt 0) { return $false }
    $fields = @{}
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].TrimEnd()
        if ($line -cmatch '^([a-z_]+)=(.*)$') {
            if ($fields.ContainsKey($matches[1])) { return $false }
            $fields[$matches[1]] = $matches[2]
        }
    }
    $bytes = [Text.Encoding]::ASCII.GetBytes($Diagnostic.sequence)
    $disclosed = if ($Diagnostic.family -ceq 'DCS') { 5 } else { $bytes.Length }
    $hex = @($bytes[0..($disclosed - 1)] | ForEach-Object { $_.ToString('X2') }) -join ' '
    if ($fields['category'] -cne $Diagnostic.category -or
        $fields['reason'] -cne $Diagnostic.reason -or
        $fields['family'] -cne $Diagnostic.family -or
        $fields['sequence'] -cne $Diagnostic.escaped_line.Substring('sequence='.Length) -or
        $fields['hex'] -cne $hex -or
        $fields['observed_bytes'] -cne [string]$bytes.Length -or
        $fields['hidden_bytes'] -cne [string]($bytes.Length - $disclosed) -or
        $fields['truncated_bytes'] -cne '0') { return $false }
    if ($Diagnostic.family -ceq 'DCS') {
        $payload = [Text.Encoding]::ASCII.GetString($bytes, 5, $bytes.Length - 7)
        return $fields['last_byte'] -ceq 'redacted' -and
            $fields['last_codepoint'] -ceq 'redacted' -and
            -not $Display.Contains($payload)
    }
    return $true
}

function Test-EffectsDiagnosticContract {
    $script:DiagnosticVector = 'cursor-restore'
    $cursor = Get-EffectsDiagnosticVector
    $cursorSample = @(
        'TL-DIAG/2', 'category=malformed', 'reason=malformed DEC state report', 'family=DCS',
        'sequence=<ESC>P1$t', 'hex=1B 50 31 24 74', 'observed_bytes=29',
        'hidden_bytes=24', 'truncated_bytes=0', 'last_byte=redacted', 'last_codepoint=redacted'
    ) -join "`n"
    Require-Effects (Test-EffectsDiagnosticDetails $cursorSample $cursor) 'C08_CURSOR_SAMPLE_REJECTED'
    Require-Effects (-not (Test-EffectsDiagnosticDetails ($cursorSample.Replace('hex=1B 50 31 24 74',
        'hex=1B 50 31 24 75')) $cursor)) 'C08_WRONG_HEX_ACCEPTED'
    Require-Effects (-not (Test-EffectsDiagnosticDetails ($cursorSample.Replace('hidden_bytes=24',
        'hidden_bytes=23')) $cursor)) 'C08_WRONG_HIDDEN_COUNT_ACCEPTED'
    $leakedCursorSample = $cursorSample + "`npayload=1;1;1;@;@;F;0;2;L;BBAA"
    Require-Effects (-not (Test-EffectsDiagnosticDetails $leakedCursorSample $cursor)) 'C08_PAYLOAD_LEAK_ACCEPTED'

    $script:DiagnosticVector = 'tab-restore'
    $tab = Get-EffectsDiagnosticVector
    $tabSample = @(
        'TL-DIAG/2', 'category=malformed', 'reason=malformed DEC state report', 'family=DCS',
        'sequence=<ESC>P2$t', 'hex=1B 50 32 24 74', 'observed_bytes=14',
        'hidden_bytes=9', 'truncated_bytes=0', 'last_byte=redacted', 'last_codepoint=redacted'
    ) -join "`n"
    Require-Effects (Test-EffectsDiagnosticDetails $tabSample $tab) 'C08_TAB_SAMPLE_REJECTED'

    $script:DiagnosticVector = 'unsupported'
    $unsupported = Get-EffectsDiagnosticVector
    $unsupportedSample = @(
        'TL-DIAG/2', 'category=unsupported', 'reason=window manipulation', 'family=CSI',
        'sequence=<ESC>[999t', 'hex=1B 5B 39 39 39 74', 'observed_bytes=6',
        'hidden_bytes=0', 'truncated_bytes=0'
    ) -join "`n"
    Require-Effects (Test-EffectsDiagnosticDetails $unsupportedSample $unsupported) 'C02_UNSUPPORTED_SAMPLE_REJECTED'

    $realXml = Join-Path $PSScriptRoot '../artifacts/validation/release-acceptance-20261011-c02-current-r177.xml'
    if (Test-Path -LiteralPath $realXml -PathType Leaf) {
        $source = [IO.File]::ReadAllText($realXml)
        $begin = $source.IndexOf('<?xml')
        $end = $source.IndexOf('</hierarchy>', $begin)
        Require-Effects ($begin -ge 0 -and $end -gt $begin) 'C08_REAL_XML_INVALID'
        $document = [xml]$source.Substring($begin, $end + '</hierarchy>'.Length - $begin)
        $diagnosticNodes = @($document.SelectNodes('//node') | Where-Object {
            $_.GetAttribute('text').Contains('TL-DIAG/2')
        })
        Require-Effects ($diagnosticNodes.Count -eq 1 -and
            (Test-EffectsDiagnosticDetails $diagnosticNodes[0].GetAttribute('text') $cursor)) 'C08_REAL_XML_REJECTED'
        return 'C02/C08 diagnostic contract self-test passed, including r177 XML; no ADB was started.'
    }
    return 'C02/C08 diagnostic contract self-test passed; local r177 XML was unavailable; no ADB was started.'
}

function Get-Definitions([string]$Id) {
    $esc = [char]27
    $bel = [char]7
    $marker = 'TLEF_' + ($Id -replace '[^A-Za-z0-9]', '_')
    $clipA = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${marker}_COPY_REJECT"))
    $clipB = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${marker}_COPY_ACCEPT"))
    $clipCovered = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${marker}_COPY_COVERED"))
    $clipReadOnly = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${marker}_COPY_READ_ONLY"))
    $diagnostic = Get-EffectsDiagnosticVector
    @(
        [pscustomobject]@{ family = 'H01'; phase = 'shell'; marker = "${marker}_SHELL";
            sequence = "${marker}_SHELL`r`n${esc}]7;file:///C:/termlink-effects${esc}\${esc}]133;A${esc}\PS> ${esc}]133;B${esc}\Write-Output demo`r`n${esc}]133;C${esc}\demo`r`n${esc}]133;D;0${esc}\";
            ui = 'command markers and current-directory menu' }
        [pscustomobject]@{ family = 'H02'; phase = 'signals'; marker = "${marker}_SIGNALS";
            sequence = "${esc}]9;4;1;57${esc}\${bel}${marker}_SIGNALS`r`n";
            ui = '57% progress and device signal settings' }
        [pscustomobject]@{ family = 'H02'; phase = 'signals-denied'; marker = "${marker}_DENIED";
            sequence = "${bel}${marker}_DENIED`r`n";
            ui = 'no Android notification while permission is denied' }
        [pscustomobject]@{ family = 'H02'; phase = 'signals-background'; marker = "${marker}_BACKGROUND";
            sequence = "${bel}${marker}_BACKGROUND`r`n";
            ui = 'Android notification from the backgrounded test session' }
        [pscustomobject]@{ family = 'H03'; phase = 'clipboard-reject'; marker = "${marker}_REJECT";
            sequence = "${esc}]52;c;${clipA}${esc}\${marker}_REJECT`r`n";
            ui = 'clipboard permission dialog, then Cancel' }
        [pscustomobject]@{ family = 'H03'; phase = 'clipboard-accept'; marker = "${marker}_ACCEPT";
            sequence = "${esc}]52;c;${clipB}${esc}\${marker}_ACCEPT`r`n";
            ui = 'clipboard permission dialog, then Copy this time' }
        [pscustomobject]@{ family = 'H03'; phase = 'clipboard-covered'; marker = "${marker}_COVERED";
            sequence = "${esc}]52;c;${clipCovered}${esc}\${marker}_COVERED`r`n";
            ui = 'history overlay blocks clipboard prompt and preserves prior clip' }
        [pscustomobject]@{ family = 'H03'; phase = 'clipboard-read-only'; marker = "${marker}_READ_ONLY";
            sequence = "${esc}]52;c;${clipReadOnly}${esc}\${marker}_READ_ONLY`r`n";
            ui = 'released control blocks clipboard prompt and preserves prior clip' }
        [pscustomobject]@{ family = 'C02'; phase = $diagnostic.phase; marker = $null;
            sequence = $diagnostic.sequence;
            ui = 'fail-closed diagnostic card and exact escaped sequence' }
    )
}

function Invoke-Program([string]$File, [string[]]$Arguments, [int]$Timeout = 15) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Require-Effects ($null -ne $process) 'PROCESS_START_FAILED'
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Timeout * 1000)) {
            $process.Kill($true)
            throw [TimeoutException]::new('PROCESS_TIMEOUT')
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.Result; Error = $stderr.Result }
    } finally { $process.Dispose() }
}

function Invoke-Adb([string[]]$Arguments, [int]$Timeout = 15) {
    Invoke-Program $AdbPath (@('-s', $EmulatorSerial) + $Arguments) $Timeout
}

function Require-Adb([string[]]$Arguments, [string]$Code) {
    $result = Invoke-Adb $Arguments
    Require-Effects ($result.ExitCode -eq 0) $Code
    return $result.Output
}

function Save-Screenshot([string]$Phase) {
    $path = Join-Path (Split-Path -Parent $EvidencePath) ("effects-$Phase.png")
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $AdbPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-s', $EmulatorSerial, 'exec-out', 'screencap', '-p')) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    Require-Effects ($null -ne $process) 'SCREENSHOT_START_FAILED'
    try {
        $file = [IO.File]::Create($path)
        try { $process.StandardOutput.BaseStream.CopyTo($file) } finally { $file.Dispose() }
        Require-Effects ($process.WaitForExit(20000) -and $process.ExitCode -eq 0) 'SCREENSHOT_FAILED'
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
    return [IO.Path]::GetFileName($path)
}

function Invoke-Hook([string]$Operation, [hashtable]$Arguments = @{}, [int]$TimeoutSeconds = 30) {
    $requestId = [guid]::NewGuid().ToString('N')
    $responsePath = 'no_backup/tail-first-e2e/response.json'
    $null = Require-Adb @('shell', 'run-as', $AndroidPackageName, 'rm', '-f', $responsePath) 'HOOK_RESET_FAILED'
    $broadcast = @('shell', 'am', 'broadcast', '-a', 'dev.termlink.product.debug.TAIL_FIRST_E2E',
        '-n', "$AndroidPackageName/dev.termlink.product.debug.TailFirstE2EReceiver",
        '--es', 'e2e_operation', $Operation, '--es', 'request_id', $requestId, '--es', 'run_id', $RunId)
    foreach ($key in @($Arguments.Keys | Sort-Object)) { $broadcast += @('--es', $key, [string]$Arguments[$key]) }
    $null = Require-Adb $broadcast 'HOOK_BROADCAST_FAILED'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $read = Invoke-Adb @('exec-out', 'run-as', $AndroidPackageName, 'cat', $responsePath)
        if ($read.ExitCode -eq 0 -and $read.Output.TrimStart().StartsWith('{')) {
            Require-Effects ($read.Output.Length -le 1MB) 'HOOK_RESPONSE_TOO_LARGE'
            $response = $read.Output | ConvertFrom-Json -ErrorAction Stop
            if ([string]$response.request_id -ceq $requestId) {
                $script:lastHookResponse = $response
                Require-Effects ([string]$response.operation -ceq $Operation -and $response.ok -eq $true) 'HOOK_FAILED'
                return $response
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('HOOK_TIMEOUT')
}

function Get-UiNodes {
    Require-Effects ([IO.Path]::IsPathFullyQualified($UiHierarchyScript) -and
        (Test-Path -LiteralPath $UiHierarchyScript -PathType Leaf)) 'UI_CAPTURE_SCRIPT_MISSING'
    for ($captureAttempt = 1; $captureAttempt -le 2; $captureAttempt++) {
        $capture = & (Get-Process -Id $PID).Path -NoProfile -File $UiHierarchyScript -DeviceSerial $EmulatorSerial `
            -TargetPackage $AndroidPackageName -TestPackage $UiTestPackage `
            -AdbPath $AdbPath -PowerShellPath (Get-Process -Id $PID).Path 2>&1
        $captureExit = $LASTEXITCODE
        $captureText = @($capture) -join [Environment]::NewLine
        $captureLog = Join-Path (Split-Path -Parent $EvidencePath) ("last-ui-capture-$captureAttempt.log")
        [IO.File]::WriteAllText($captureLog, $captureText, [Text.UTF8Encoding]::new($false))
        if ($captureText.Contains('<hierarchy') -and $captureText.Contains('</hierarchy>')) { break }
        if ($captureExit -eq 0) { break }
        Start-Sleep -Milliseconds 400
    }
    $script:lastUiXml = @($capture | Where-Object { $_ -is [string] }) -join [Environment]::NewLine
    Require-Effects ($captureExit -eq 0 -and -not [string]::IsNullOrWhiteSpace($script:lastUiXml)) 'UI_DUMP_FAILED'
    # Instrumented a11y can intermittently omit the Compose popup window even
    # while screencapture shows it. Use the platform dump as a same-device
    # fallback only when both popup markers are absent.
    if (-not $script:lastUiXml.Contains('命令定位') -and -not $script:lastUiXml.Contains('终端提醒')) {
        $fallback = Invoke-Adb @('shell', 'uiautomator', 'dump', '/sdcard/termlink-ui-fallback.xml') 30
        if ($fallback.Output.Contains('UI hiercharchy dumped') -or $fallback.Output.Contains('hierarchy')) {
            $fallbackXml = Require-Adb @('shell', 'cat', '/sdcard/termlink-ui-fallback.xml') 'UI_FALLBACK_DUMP_FAILED'
            if ($fallbackXml.Contains('<hierarchy') -and $fallbackXml.Contains('dev.termlink')) {
                $script:lastUiXml = $fallbackXml
            }
        }
        $null = Require-Adb @('shell', 'rm', '-f', '/sdcard/termlink-ui-fallback.xml') 'UI_FALLBACK_CLEANUP_FAILED'
    }
    $document = [xml]$script:lastUiXml
    foreach ($node in $document.GetElementsByTagName('node')) { $node }
}

function Test-UiNodeClickable($Node) {
    for ($current = $Node; $null -ne $current -and $current.NodeType -eq 'Element';
         $current = $current.ParentNode) {
        if ($current.GetAttribute('clickable') -ceq 'true') { return $true }
    }
    return $false
}

function Find-UiNode([string[]]$Names, [switch]$PreferClickable) {
    $fallback = $null
    $clickable = $null
    foreach ($node in @(Get-UiNodes)) {
        if ([string]$node.GetAttribute('text') -cin $Names -or
            [string]$node.GetAttribute('content-desc') -cin $Names) {
            if (-not $PreferClickable -or (Test-UiNodeClickable $node)) { return $node }
            if ([string]::IsNullOrEmpty($node.GetAttribute('text')) -ceq $false -and (Test-UiNodeClickable $node)) { return $node }
            if ($null -eq $clickable -and (Test-UiNodeClickable $node)) { $clickable = $node }
            if ($null -eq $fallback) { $fallback = $node }
        }
    }
    if ($null -ne $clickable) { return $clickable } else { return $fallback }
}

function Find-UiNodePattern([string]$Pattern) {
    foreach ($node in @(Get-UiNodes)) {
        $value = ([string]$node.GetAttribute('text') + ' ' + [string]$node.GetAttribute('content-desc')).Trim()
        if ($value -match $Pattern) { return $node }
    }
    return $null
}

function Wait-UiNode([string[]]$Names, [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $node = Find-UiNode $Names
        if ($null -ne $node) { return $node }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Get-UiToggle([string[]]$Names) {
    $label = Find-UiNode $Names
    if ($null -eq $label) { return $null }
    for ($node = $label; $null -ne $node -and $node.NodeType -eq 'Element'; $node = $node.ParentNode) {
        if ($node.GetAttribute('checkable') -ceq 'true' -and
            $node.GetAttribute('checked') -cin @('true', 'false')) { return $node }
    }
    return $null
}

function Tap-UiNode($Node, [string]$Code, [switch]$AllowLabelFallback) {
    Require-Effects ($null -ne $Node) $Code
    $label = $Node
    while ($null -ne $Node -and $Node.NodeType -eq 'Element' -and
        $Node.GetAttribute('clickable') -cne 'true') {
        $Node = $Node.ParentNode
        if ($null -ne $Node -and $Node.NodeType -cne 'Element') { $Node = $null }
    }
    if ($null -eq $Node -or $Node.NodeType -cne 'Element' -or
        $Node.GetAttribute('clickable') -cne 'true') {
        # The status row can be visible while an incremental a11y capture omits
        # its clickable ancestor; the label bounds remain a safe fallback.
        $Node = if ($AllowLabelFallback) { $label } else { $null }
        Require-Effects ($null -ne $Node -and $Node.NodeType -eq 'Element' -and
            $Node.GetAttribute('enabled') -ceq 'true' -and
            $Node.GetAttribute('package') -ceq $AndroidPackageName) 'UI_TARGET_NOT_ACTIONABLE'
    }
    Require-Effects ($null -ne $Node -and $Node.NodeType -eq 'Element' -and
        $Node.GetAttribute('enabled') -ceq 'true' -and
        $Node.GetAttribute('package') -cin @($AndroidPackageName, 'com.android.permissioncontroller',
            'com.google.android.permissioncontroller')) 'UI_TARGET_NOT_ACTIONABLE'
    $bounds = [string]$Node.GetAttribute('bounds')
    Require-Effects ($bounds -match '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') 'UI_BOUNDS_INVALID'
    Require-Effects ([int]$matches[3] -gt [int]$matches[1] -and
        [int]$matches[4] -gt [int]$matches[2]) 'UI_BOUNDS_INVALID'
    $x = [int][Math]::Floor(([int]$matches[1] + [int]$matches[3]) / 2)
    $y = [int][Math]::Floor(([int]$matches[2] + [int]$matches[4]) / 2)
    $null = Require-Adb @('shell', 'input', 'tap', "$x", "$y") 'UI_TAP_FAILED'
    Start-Sleep -Milliseconds 400
}

function Tap-Ui([string[]]$Names, [string]$Code) {
    # Compose menu items can expose the label before the incremental a11y tree
    # republishes their clickable ancestor. The bounds are still validated and
    # constrained to the enabled product package.
    Tap-UiNode (Find-UiNode $Names) $Code -AllowLabelFallback
}

function Test-MenuVisible {
    foreach ($node in @(Get-UiNodes)) {
        $value = ([string]$node.GetAttribute('text') + ' ' + [string]$node.GetAttribute('content-desc')).Trim()
        if ($value -cmatch '^(终端提醒|Terminal alerts)$' -or
            $value -cmatch '服务器中转 / Relay|Connection & control') { return $true }
    }
    return $false
}

function Wait-CompleteMenu([int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($null -ne (Find-UiNode @('终端提醒', 'Terminal alerts')) -or
            $null -ne (Find-UiNode @('释放控制权限', 'Release control', '获取控制权限', 'Request control'))) { return $true }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Open-Menu {
    if (Test-MenuVisible) {
        # A partial popup capture is still open evidence; tapping status again would close it.
        if (Wait-CompleteMenu 12) { return }
    }
    for ($menuAttempt = 1; $menuAttempt -le 3; $menuAttempt++) {
        $status = Find-UiNode @('服务器中转 / Relay', '控制中', 'Controlling', '只读', 'Read only', '已退出', 'Exited') -PreferClickable
        if ($null -eq $status) {
            try { $null = Invoke-Hook 'reopen_selected' @{} 20 } catch { }
            $null = Require-Adb @('shell', 'am', 'start', '-W', '-n',
                "$AndroidPackageName/dev.termlink.product.ProductActivity",
                '-a', 'android.intent.action.MAIN', '-c', 'android.intent.category.LAUNCHER',
                '--activity-single-top') 'FOREGROUND_FAILED'
            Start-Sleep -Milliseconds 800
            $status = Find-UiNode @('服务器中转 / Relay', '控制中', 'Controlling', '只读', 'Read only', '已退出', 'Exited') -PreferClickable
        }
        Require-Effects ($null -ne $status) 'STATUS_MENU_NOT_FOUND'
        Tap-UiNode $status 'STATUS_MENU_TAP_FAILED' -AllowLabelFallback
        if (Wait-CompleteMenu ($(if ($menuAttempt -eq 1) { 8 } else { 12 }))) { return }
    }
    Require-Effects ($null -ne (Find-UiNode @('终端提醒', 'Terminal alerts'))) 'STATUS_MENU_NOT_OPEN'
}

function Close-Menu {
    Require-Effects ($null -ne (Find-UiNode @('终端提醒', 'Terminal alerts'))) 'STATUS_MENU_NOT_OPEN'
    foreach ($closeAttempt in @('back', 'outside', 'back')) {
        if ($closeAttempt -ceq 'back') {
            $null = Require-Adb @('shell', 'input', 'keyevent', 'KEYCODE_BACK') 'MENU_CLOSE_FAILED'
        } else {
            # Product status popup is anchored top-right; this point is safely
            # inside the terminal surface and outside the dropdown.
            $null = Require-Adb @('shell', 'input', 'tap', '24', '600') 'MENU_OUTSIDE_TAP_FAILED'
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(3)
        do {
            if ($null -eq (Find-UiNode @('终端提醒', 'Terminal alerts'))) { return }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
    }
    Require-Effects ($null -eq (Find-UiNode @('终端提醒', 'Terminal alerts'))) 'MENU_CLOSE_FAILED'
}

function Get-NotificationPermission {
    foreach ($arguments in @(
        @('shell', 'cmd', 'package', 'check-permission',
            'android.permission.POST_NOTIFICATIONS', $AndroidPackageName),
        @('shell', 'pm', 'check-permission',
            'android.permission.POST_NOTIFICATIONS', $AndroidPackageName)
    )) {
        $result = Invoke-Adb $arguments
        $state = $result.Output.Trim()
        if ($result.ExitCode -eq 0 -and $state -in @('granted', 'denied')) { return $state }
    }
    $dump = Invoke-Adb @('shell', 'dumpsys', 'package', $AndroidPackageName) 20
    if ($dump.ExitCode -eq 0) {
        $states = @([regex]::Matches($dump.Output,
            '(?m)^\s*android\.permission\.POST_NOTIFICATIONS:\s*granted=(true|false)') |
            ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        if ($states.Count -eq 1) {
            return $(if ($states[0] -eq 'true') { 'granted' } else { 'denied' })
        }
    }
    throw [InvalidOperationException]::new('PERMISSION_STATE_UNKNOWN')
}

function Set-NotificationPermission([string]$State) {
    Require-Effects ($State -in @('granted', 'denied')) 'PERMISSION_STATE_INVALID'
    if ((Get-NotificationPermission) -ceq $State) { return }
    $verb = if ($State -eq 'granted') { 'grant' } else { 'revoke' }
    $null = Require-Adb @('shell', 'pm', $verb, $AndroidPackageName,
        'android.permission.POST_NOTIFICATIONS') 'PERMISSION_CHANGE_FAILED'
}

function Resume-TestSession([long]$MinimumAck = 0) {
    $null = Require-Adb @('shell', 'am', 'start', '-W', '-n',
        "$AndroidPackageName/dev.termlink.product.ProductActivity",
        '-a', 'android.intent.action.MAIN', '-c', 'android.intent.category.LAUNCHER',
        '--activity-single-top') 'FOREGROUND_FAILED'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            $probe = Invoke-Hook 'probe' @{} 10
        } catch {
            $failure = $script:lastHookResponse.failure_code
            if ($_.Exception.Message -cnotmatch '^(HOOK_FAILED|HOOK_TIMEOUT)$' -or
                $failure -cnotmatch '^(BRIDGE_UNAVAILABLE|OPERATION_TIMEOUT)$') { throw }
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            $null = Require-Adb @('shell', 'am', 'start', '-W', '-n',
                "$AndroidPackageName/dev.termlink.product.ProductActivity",
                '-a', 'android.intent.action.MAIN', '-c', 'android.intent.category.LAUNCHER',
                '--activity-single-top') 'FOREGROUND_FAILED'
            Start-Sleep -Milliseconds 500
            continue
        }
        if ($probe.account_ready -ne $true) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'RESUME_ACCOUNT_NOT_READY' }
            Start-Sleep -Milliseconds 500
            continue
        }
        Require-Effects (([uri]$probe.server_origin).GetLeftPart([UriPartial]::Authority) -ceq
            $ExpectedOrigin) 'SERVER_OR_ACCOUNT_MISMATCH'
        try {
            $current = Invoke-Hook 'inspect' @{ live_marker = (Get-LiveMarker $RunId) } 10
        } catch {
            if ($_.Exception.Message -cne 'HOOK_FAILED' -or
                $script:lastHookResponse.failure_code -cne 'SESSION_UNAVAILABLE') { throw }
            $current = $null
        }
        if ($null -ne $current) {
            Require-Effects ([guid]$current.session_id -eq $ExpectedSessionId) 'SESSION_ID_MISMATCH'
            if ($current.realtime_subscriber_ready -eq $true -and [long]$current.live_ack -ge $MinimumAck) { return }
            Require-Effects ([DateTime]::UtcNow -lt $deadline) 'RESUME_SUBSCRIBER_NOT_READY'
            Start-Sleep -Milliseconds 500
            continue
        }
        try { $reselected = Invoke-Hook 'reselect_armed_session' @{} 45 }
        catch {
            if ($_.Exception.Message -cne 'HOOK_FAILED' -or
                $script:lastHookResponse.failure_code -cne 'SESSION_UNAVAILABLE' -or
                [DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 500
            continue
        }
        Assert-Session $reselected
        Require-Effects ([long]$reselected.live_ack -ge $MinimumAck) 'RESUME_ACK_REGRESSED'
        return
    } while ($true)
}

function Invoke-HookWithRetry([string]$Operation, [hashtable]$Arguments = @{}, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        try { return Invoke-Hook $Operation $Arguments $TimeoutSeconds }
        catch {
            if ($_.Exception.Message -cne 'HOOK_FAILED' -or [DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 500
        }
    } while ($true)
}

function Restore-TestControl([string]$LiveMarker) {
    Resume-TestSession
    Open-Menu
    $request = Wait-UiNode @('获取控制权限', 'Request control') 5
    if ($null -ne $request) {
        Tap-UiNode $request 'H03_REQUEST_CONTROL_FAILED' -AllowLabelFallback
    } else {
        $release = Wait-UiNode @('释放控制权限', 'Release control') 2
        Require-Effects ($null -ne $release) 'H03_CONTROL_MENU_INVALID'
        Close-Menu
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        try {
            $restored = Invoke-Hook 'inspect' @{ live_marker = $LiveMarker } 5
            Assert-Session $restored
            if ([string]$restored.realtime_remote_state -ceq 'CONTROLLING') { return $restored }
        } catch {
            # The bridge can still be settling immediately after reselect.
        }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('H03_CONTROL_RESTORE_FAILED')
}

function Release-TestControl {
    Resume-TestSession
    for ($releaseAttempt = 1; $releaseAttempt -le 3; $releaseAttempt++) {
        Open-Menu
        $release = Wait-UiNode @('释放控制权限', 'Release control') 5
        Require-Effects ($null -ne $release) 'H03_RELEASE_CONTROL_MISSING'
        Tap-UiNode $release 'H03_RELEASE_CONTROL_FAILED' -AllowLabelFallback
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        do {
            if ($null -ne (Find-UiNode @('只读', 'Read only'))) { break }
            Start-Sleep -Milliseconds 400
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -ne (Find-UiNode @('只读', 'Read only'))) { break }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(7)
    do {
        if ($null -ne (Find-UiNode @('只读', 'Read only'))) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    Require-Effects ($null -ne (Find-UiNode @('只读', 'Read only'))) 'H03_READ_ONLY_NOT_VISIBLE'
    $menuDeadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        if ($null -eq (Find-UiNode @('终端提醒', 'Terminal alerts'))) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $menuDeadline)
    Require-Effects ($null -eq (Find-UiNode @('终端提醒', 'Terminal alerts'))) 'H03_RELEASE_MENU_CLOSE_FAILED'
    $script:controlReleased = $true
}

function Get-LiveMarker([string]$Id) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Id))
    return 'TLTF_LIVE_' + [Convert]::ToHexString($hash, 0, 8).ToLowerInvariant()
}

function Assert-Session($Response) {
    Require-Effects ([guid]$Response.session_id -eq $ExpectedSessionId) 'SESSION_ID_MISMATCH'
    Require-Effects ($Response.realtime_subscriber_ready -eq $true) 'SUBSCRIBER_NOT_READY'
}

function Get-EffectsPhaseMarker($Definition) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Definition.marker))
    return 'TLEF_' + [Convert]::ToHexString($hash, 0, 8).ToLowerInvariant()
}

function Get-EffectsSequence($Definition, [string]$Id) {
    if ($null -eq $Definition.marker) { return $Definition.sequence }
    # The live marker may have been erased by Kitty; keep this phase's proof within the bounded semantic probe.
    $esc = [string][char]27
    return $Definition.sequence + "${esc}7${esc}[H${esc}[2K$(Get-EffectsPhaseMarker $Definition) $(Get-LiveMarker $Id)${esc}8"
}

function Test-EffectsPhaseObserved($Definition, $Response, [long]$BeforeAck) {
    if ([long]$Response.live_ack -le $BeforeAck -or
        $null -eq $Response.PSObject.Properties['terminal_semantics']) { return $false }
    if ($Definition.phase -in @('clipboard-reject', 'clipboard-accept')) {
        # Clipboard actions can consume the cursor-save auxiliary marker while
        # retaining the unique phase text; both are accepted proof for H03.
        return (Test-TerminalSemanticMarker $Response.terminal_semantics $Definition.marker) -or
            (Test-TerminalSemanticMarker $Response.terminal_semantics (Get-EffectsPhaseMarker $Definition))
    }
    return Test-TerminalSemanticMarker $Response.terminal_semantics (Get-EffectsPhaseMarker $Definition)
}

function Write-PhaseTrigger($Definition) {
    Require-Effects (-not (Test-Path -LiteralPath $TriggerPath)) 'TRIGGER_ALREADY_EXISTS'
    $body = [ordered]@{ reset_before = $false; sequence_b64 =
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-EffectsSequence $Definition $RunId)));
        phase = $Definition.phase; expected_marker = $Definition.marker }
    [IO.File]::WriteAllText($TriggerPath, (($body | ConvertTo-Json -Compress) + "`n"), [Text.UTF8Encoding]::new($false))
}

function Send-Phase($Definition, [long]$BeforeAck, [string]$BeforeHash, [string]$LiveMarker) {
    Write-PhaseTrigger $Definition
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 300
        if ($null -eq $Definition.marker) {
            $details = try { Find-UiNode @('协议错误详情', 'Protocol details') } catch { $null }
            if ($null -ne $details) { return $null }
        } else {
            $current = Invoke-Hook 'inspect' @{ live_marker = $LiveMarker }
            Assert-Session $current
            if ((Test-EffectsPhaseObserved $Definition $current $BeforeAck) -and
                [string]$current.visible_text_sha256 -cne $BeforeHash -and
                -not (Test-Path -LiteralPath $TriggerPath)) { return $current }
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $Definition.marker) { return $null }
    try {
        $timeoutState = Invoke-Hook 'inspect' @{ live_marker = $LiveMarker } 20
        $timeoutPath = Join-Path (Split-Path -Parent $EvidencePath) 'phase-ack-timeout-inspect.json'
        [IO.File]::WriteAllText($timeoutPath, (($timeoutState | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
        $phaseName = if ($Definition.phase) { $Definition.phase } else { 'unknown' }
        $null = Save-Screenshot "ack-timeout-$phaseName"
    } catch { }
    throw [TimeoutException]::new('PHASE_ACK_TIMEOUT')
}

function Send-CoveredPhase($Definition, [long]$BeforeAck, [string]$OverlayHash, [string]$LiveMarker) {
    Write-PhaseTrigger $Definition
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 300
        $current = Invoke-Hook 'inspect' @{ live_marker = $LiveMarker }
        Assert-Session $current
        if ([long]$current.live_ack -gt $BeforeAck -and
            $current.frozen_history.active -eq $true -and
            $current.frozen_history.page_count -gt 0 -and
            -not (Test-Path -LiteralPath $TriggerPath)) { return $current }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('H03_COVERED_ACK_OR_VIEW_CHANGED')
}

function Send-BackgroundPhase($Definition, [long]$BeforeAck, [string]$BeforeHash, [string]$LiveMarker) {
    $null = Require-Adb @('shell', 'input', 'keyevent', 'KEYCODE_HOME') 'BACKGROUND_FAILED'
    Start-Sleep -Milliseconds 500
    Write-PhaseTrigger $Definition
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 250 } while ((Test-Path -LiteralPath $TriggerPath) -and
        [DateTime]::UtcNow -lt $deadline)
    Require-Effects (-not (Test-Path -LiteralPath $TriggerPath)) 'BACKGROUND_TRIGGER_NOT_CONSUMED'
    Start-Sleep -Seconds 2
    $script:backgroundNotificationSeen = $false
    try {
        $backgroundCheck = Invoke-Hook 'inspect' @{
            live_marker = $LiveMarker; notification_fixture = 'attention'
        } 5
        Assert-Session $backgroundCheck
        $script:backgroundNotificationSeen = $backgroundCheck.terminal_fixture_notification_present -eq $true
    } catch {
        if ($_.Exception.Message -ceq 'SESSION_ID_MISMATCH') { throw }
        # The bridge can be paused while Android backgrounds the Activity; retry after return.
    }
    Resume-TestSession $BeforeAck
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $current = Invoke-Hook 'inspect' @{ live_marker = $LiveMarker }
        Assert-Session $current
        if ((Test-EffectsPhaseObserved $Definition $current $BeforeAck) -and
            [string]$current.visible_text_sha256 -cne $BeforeHash -and
            $current.live_marker_visible -eq $true) { return $current }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw [TimeoutException]::new('BACKGROUND_ACK_TIMEOUT')
}

if ($AudioSelfTest) { Test-EffectsAudioMath; return }
if ($DiagnosticSelfTest) { Test-EffectsDiagnosticContract; return }
Require-Effects ($DiagnosticVector -ceq 'unsupported' -or $Family -ceq 'C02') 'DIAGNOSTIC_VECTOR_REQUIRES_C02'
$definitions = @(Get-Definitions ($RunId ?? 'definitions'))
if ($DefinitionsOnly) {
    $definitions | ForEach-Object {
        [ordered]@{ family = $_.family; phase = $_.phase; expected_marker = $_.marker;
            reset_before = $false; sequence_b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-EffectsSequence $_ ($RunId ?? 'definitions'))));
            expected_ui = $_.ui }
    } | ConvertTo-Json -Depth 4
    return
}

Require-Effects (-not [string]::IsNullOrWhiteSpace($RunId) -and $ExpectedSessionId -ne [guid]::Empty -and
    -not [string]::IsNullOrWhiteSpace($ExpectedOrigin)) 'RUN_CONTEXT_REQUIRED'
Require-Effects (-not [string]::IsNullOrWhiteSpace($AdbPath) -and
    (Test-Path -LiteralPath $AdbPath -PathType Leaf)) 'ADB_PATH_REQUIRED'
Require-Effects (-not [string]::IsNullOrWhiteSpace($TriggerPath) -and
    [IO.Path]::IsPathFullyQualified($TriggerPath) -and
    (Test-Path -LiteralPath (Split-Path -Parent $TriggerPath) -PathType Container)) 'TRIGGER_PATH_INVALID'
Require-Effects (-not [string]::IsNullOrWhiteSpace($EvidencePath) -and
    [IO.Path]::IsPathFullyQualified($EvidencePath)) 'EVIDENCE_PATH_REQUIRED'
$runRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../artifacts/broker-product-e2e/$RunId"))
Require-Effects ([string]::Equals([IO.Path]::GetFullPath((Split-Path -Parent $TriggerPath)),
        $runRoot, [StringComparison]::OrdinalIgnoreCase) -and
    [IO.Path]::GetExtension($TriggerPath) -ceq '.trigger' -and
    [string]::Equals([IO.Path]::GetFullPath((Split-Path -Parent $EvidencePath)),
        $runRoot, [StringComparison]::OrdinalIgnoreCase) -and
    [IO.Path]::GetExtension($EvidencePath) -ceq '.json') 'EVIDENCE_SCOPE_INVALID'

$evidence = [ordered]@{ schema_version = 1; run_id = $RunId; session_id = "$ExpectedSessionId";
    family = $Family; diagnostic_vector = $(if ($Family -eq 'C02') { $DiagnosticVector } else { $null });
    status = 'running'; phases = @(); unverified = @();
    android_settings_restore = [ordered]@{ status = 'not_started' } }
    $script:frozenHistoryOpen = $false
    $script:initialNotificationPermission = $null
$script:initialAlerts = $null
$script:initialSounds = $null
$script:initialClipboard = $null
$script:frozenHistoryOpen = $false
$script:directSettingsRestored = $false
$script:controlReleased = $false
$script:lastHookResponse = $null
$script:lastUiXml = $null
try {
    $armText = Require-Adb @('exec-out', 'run-as', $AndroidPackageName, 'cat',
        'no_backup/tail-first-e2e/session-arm.json') 'ARM_READ_FAILED'
    Require-Effects ($armText.Length -le 131072) 'ARM_TOO_LARGE'
    $arm = $armText | ConvertFrom-Json -ErrorAction Stop
    Require-Effects ($arm.armed -eq $true -and [string]$arm.operation -ceq 'open_session' -and
        [string]$arm.run_id -ceq $RunId -and [guid]$arm.session_id -eq $ExpectedSessionId -and
        $ExpectedSessionId.ToString() -cnotin @($arm.baseline_session_ids) -and
        ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [long]$arm.armed_at_epoch_millis -ge 0) -and
        ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [long]$arm.armed_at_epoch_millis -le 1800000)) 'ARM_MISMATCH'
    $probe = Invoke-Hook 'probe'
    Require-Effects ($probe.account_ready -eq $true -and
        ([uri]$probe.server_origin).GetLeftPart([UriPartial]::Authority) -ceq $ExpectedOrigin) 'SERVER_OR_ACCOUNT_MISMATCH'
    $liveMarker = Get-LiveMarker $RunId
    $historyMarkerStem = 'TLTF_' + $RunId.Replace('-', '_').Replace('.', '_')
    $before = Invoke-Hook 'inspect' @{ live_marker = $liveMarker }
    Assert-Session $before
    if ($Family -ne 'C02') { $script:initialNotificationPermission = Get-NotificationPermission }
    foreach ($definition in @($definitions | Where-Object {
        if ($Family -eq 'C02') { $_.family -eq 'C02' } else { $_.family -ne 'C02' }
    })) {
        $phase = [ordered]@{ family = $definition.family; phase = $definition.phase;
            ack_before = [long]$before.live_ack; ack_after = $null; ui_observed = $false;
            screenshot = $null; status = 'running' }
        $evidence.phases += $phase
        if ($definition.phase -eq 'clipboard-reject') {
            Open-Menu
            $toggleDeadline = [DateTime]::UtcNow.AddSeconds(5)
            do {
            $toggle = Get-UiToggle @('允许终端请求复制', '允许终端请求复制（逐次确认）', 'Ask before copying terminal text')
                if ($null -ne $toggle) { break }
                Start-Sleep -Milliseconds 300
            } while ([DateTime]::UtcNow -lt $toggleDeadline)
            Require-Effects ($null -ne $toggle) 'CLIPBOARD_SETTING_MISSING'
            $script:initialClipboard = [string]$toggle.GetAttribute('checked')
            if ([string]$toggle.GetAttribute('checked') -ne 'true') {
                Tap-Ui @('允许终端请求复制', '允许终端请求复制（逐次确认）', 'Ask before copying terminal text') 'CLIPBOARD_TOGGLE_FAILED'
            }
            $enabledDeadline = [DateTime]::UtcNow.AddSeconds(5)
            do {
                $enabled = Get-UiToggle @('允许终端请求复制', '允许终端请求复制（逐次确认）', 'Ask before copying terminal text')
                if ($null -ne $enabled -and $enabled.GetAttribute('checked') -ceq 'true') { break }
                Start-Sleep -Milliseconds 300
            } while ([DateTime]::UtcNow -lt $enabledDeadline)
            Require-Effects ($null -ne $enabled -and $enabled.GetAttribute('checked') -ceq 'true') 'CLIPBOARD_NOT_ENABLED'
            Close-Menu
            Start-Sleep -Milliseconds 500
            $rejectBefore = Invoke-Hook 'inspect' @{ live_marker = $liveMarker; clipboard_fixture = 'reject' }
            $acceptBefore = Invoke-Hook 'inspect' @{ live_marker = $liveMarker; clipboard_fixture = 'accept' }
            Require-Effects ($rejectBefore.clipboard_fixture_matches -eq $false -and
                $acceptBefore.clipboard_fixture_matches -eq $false) 'H03_FIXTURE_ALREADY_IN_CLIPBOARD'
            # H03 follows H01/H02. UI state can remain Controlling after lease
            # expiry, so renew through the product before capturing the permit.
            Release-TestControl
            $rejectBefore = Restore-TestControl $liveMarker
            Require-Effects ([string]$rejectBefore.realtime_remote_state -ceq 'CONTROLLING') 'H03_CONTROL_NOT_READY'
        }
        if ($definition.phase -eq 'clipboard-covered') {
            # terminal-core V2 stores bounded native scrollback in the snapshot and
            # uses line ID 1 as the empty external transcript prefix sentinel.
            # Covered H03 therefore verifies the product's frozen-history overlay.
            $script:frozenHistoryOpen = $true
            $overlay = Invoke-Hook 'begin_frozen_history'
            Assert-Session $overlay
            Require-Effects ($overlay.frozen_active -eq $true -and
                $overlay.frozen_render_ready -eq $true -and
                $overlay.frozen_page_count -gt 0 -and
                $null -eq (Find-UiNode @('复制终端内容？', 'Copy terminal text?'))) 'H03_HISTORY_OVERLAY_NOT_READY'
            $before = $overlay
            $phase.ack_before = [long]$before.live_ack
        }
        if ($definition.phase -eq 'clipboard-read-only') {
            # A transient Relay/TL-N002 reconnect can outlive one 30s resume.
            # Retry the whole resume/ready cycle before opening the control menu.
            $readOnlyReady = $false
            foreach ($resumeAttempt in 1..3) {
                try {
                    Resume-TestSession ([long]$before.live_ack)
                    $ready = Invoke-Hook 'inspect' @{ live_marker = $liveMarker } 10
                    Assert-Session $ready
                    if ($ready.realtime_subscriber_ready -eq $true) {
                        $readOnlyReady = $true
                        break
                    }
                } catch [TimeoutException] {
                } catch {
                    if ($_.Exception.Message -cnotin @('RESUME_SUBSCRIBER_NOT_READY', 'HOOK_FAILED')) { throw }
                }
                Start-Sleep -Seconds 2
            }
            Require-Effects ($readOnlyReady) 'H03_READ_ONLY_SESSION_NOT_READY'
            Open-Menu
            $release = Wait-UiNode @('释放控制权限', 'Release control') 15
            Require-Effects ($null -ne $release) 'H03_RELEASE_CONTROL_MISSING'
            $script:controlReleased = $true
            Tap-UiNode $release 'H03_RELEASE_CONTROL_FAILED' -AllowLabelFallback
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                if ($null -ne (Find-UiNode @('只读', 'Read only'))) { break }
                Start-Sleep -Milliseconds 400
            } while ([DateTime]::UtcNow -lt $deadline)
            Require-Effects ($null -ne (Find-UiNode @('只读', 'Read only'))) 'H03_READ_ONLY_NOT_VISIBLE'
            Open-Menu
            Require-Effects ($null -ne (Find-UiNode @('获取控制权限', 'Request control'))) 'H03_READ_ONLY_CONTROL_STATE_INVALID'
            Close-Menu
        }
        $after = if ($definition.phase -in @('signals-denied', 'signals-background')) {
            Send-BackgroundPhase $definition ([long]$before.live_ack) ([string]$before.visible_text_sha256) $liveMarker
        } elseif ($definition.phase -eq 'clipboard-covered') {
            Send-CoveredPhase $definition ([long]$before.live_ack) ([string]$before.visible_text_sha256) $liveMarker
        } else {
            Send-Phase $definition ([long]$before.live_ack) ([string]$before.visible_text_sha256) $liveMarker
        }
        if ($null -ne $after) { $phase.ack_after = [long]$after.live_ack }
        switch ($definition.phase) {
            'shell' {
                Open-Menu
                $directory = Wait-UiNode @('打开当前目录', 'Open current directory') 5
                Require-Effects ($null -ne $directory) 'H01_DIRECTORY_MENU_MISSING'
                $command = Wait-UiNode @('命令定位', 'Command markers') 5
                Require-Effects ($null -ne $command) 'H01_COMMAND_MENU_MISSING'
                Tap-UiNode $command 'H01_COMMAND_MENU_MISSING'
                Require-Effects ($null -ne (Find-UiNodePattern '^#\d+\s*[·.]')) 'H01_COMMAND_MARKER_MISSING'
                $phase.screenshot = Save-Screenshot $definition.phase
                Tap-Ui @('关闭', 'Close') 'H01_DIALOG_CLOSE_MISSING'
                Require-Effects ($null -eq (Find-UiNodePattern '^#\d+\s*[·.]')) 'H01_DIALOG_NOT_CLOSED'
                Close-Menu
                $evidence.unverified += 'H01 automatic PowerShell marker injection needs a separate interactive PowerShell session.'
            }
            'signals' {
                $progress = Wait-UiNode @('57%') 12
                Require-Effects ($null -ne $progress) 'H02_PROGRESS_NOT_VISIBLE'
                Set-NotificationPermission 'denied'
                Require-Effects ((Get-NotificationPermission) -eq 'denied') 'H02_PERMISSION_DENIAL_NOT_APPLIED'
                Resume-TestSession ([long]$after.live_ack)
                Open-Menu
                $alertsDeadline = [DateTime]::UtcNow.AddSeconds(5)
                do {
                    $alerts = Get-UiToggle @('终端提醒', 'Terminal alerts')
                    $sounds = Find-UiNode @('终端提示音', 'Terminal sounds')
                    if ($null -ne $alerts -and $null -ne $sounds) { break }
                    Start-Sleep -Milliseconds 300
                } while ([DateTime]::UtcNow -lt $alertsDeadline)
                Require-Effects ($null -ne $alerts -and
                    $null -ne $sounds) 'H02_SETTINGS_NOT_VISIBLE'
                $script:initialAlerts = [string]$alerts.GetAttribute('checked')
                if ([string]$alerts.GetAttribute('checked') -eq 'false') {
                    Tap-Ui @('终端提醒', 'Terminal alerts') 'H02_ALERT_TOGGLE_FAILED'
                    $deny = Find-UiNode @('不允许', "Don't allow")
                    if ($null -ne $deny) { Tap-Ui @('不允许', "Don't allow") 'H02_PERMISSION_DENY_FAILED' }
                }
                $alertsNow = Get-UiToggle @('终端提醒', 'Terminal alerts')
                Require-Effects ($null -ne $alertsNow -and
                    [string]$alertsNow.GetAttribute('checked') -eq 'true') 'H02_ALERT_NOT_ENABLED'
                $phase.screenshot = Save-Screenshot $definition.phase
                Require-Effects ((Get-NotificationPermission) -ceq 'denied') 'H02_PERMISSION_DENIAL_NOT_APPLIED'
                Close-Menu
            }
            'signals-denied' {
                $notification = Invoke-Hook 'inspect' @{ live_marker = $liveMarker; notification_fixture = 'attention' }
                Require-Effects (-not $script:backgroundNotificationSeen -and
                    $notification.terminal_fixture_notification_present -eq $false) 'H02_DENIED_NOTIFICATION_POSTED'
                $phase.screenshot = Save-Screenshot $definition.phase
                Set-NotificationPermission 'granted'
                Require-Effects ((Get-NotificationPermission) -eq 'granted') 'H02_PERMISSION_GRANT_NOT_APPLIED'
                Resume-TestSession ([long]$after.live_ack)
            }
            'signals-background' {
                $notification = $null
                $notificationDeadline = [DateTime]::UtcNow.AddSeconds(10)
                do {
                    try { $notification = Invoke-Hook 'inspect' @{ live_marker = $liveMarker; notification_fixture = 'attention' } 5 } catch { }
                    if ($null -ne $notification -and $notification.terminal_fixture_notification_present -eq $true) { break }
                    Start-Sleep -Milliseconds 500
                } while ([DateTime]::UtcNow -lt $notificationDeadline)
                Require-Effects ($script:backgroundNotificationSeen -or
                    ($null -ne $notification -and $notification.terminal_fixture_notification_present -eq $true)) 'H02_OS_NOTIFICATION_NOT_POSTED'
                $phase.screenshot = Save-Screenshot $definition.phase
            }
            { $_ -in 'clipboard-reject', 'clipboard-accept' } {
                Require-Effects ([string]$after.realtime_remote_state -ceq 'CONTROLLING') 'H03_CONTROL_NOT_READY'
                Require-Effects ($null -ne (Wait-UiNode @('复制终端内容？', 'Copy terminal text?') 5)) 'H03_PROMPT_NOT_VISIBLE'
                $clipSuffix = if ($definition.phase -eq 'clipboard-reject') { 'REJECT' } else { 'ACCEPT' }
                $clipMarker = ($definition.marker -replace '_(REJECT|ACCEPT)$', '') + "_COPY_$clipSuffix"
                Require-Effects ($null -ne (Find-UiNodePattern ([regex]::Escape($clipMarker)))) 'H03_FIXTURE_PROMPT_MISMATCH'
                $phase.screenshot = Save-Screenshot $definition.phase
                if ($definition.phase -eq 'clipboard-reject') {
                    Tap-Ui @('取消', 'Cancel') 'H03_CANCEL_MISSING'
                } else {
                    Tap-Ui @('仅复制本次', 'Copy this time') 'H03_ACCEPT_MISSING'
                }
                # Native dialog buttons can miss the first injected tap while
                # AlertDialog window focus is still settling.
                if ($null -ne (Find-UiNode @('复制终端内容？', 'Copy terminal text?'))) {
                    Start-Sleep -Milliseconds 500
                    if ($definition.phase -eq 'clipboard-reject') {
                        Tap-Ui @('取消', 'Cancel') 'H03_CANCEL_RETRY_FAILED'
                    } else {
                        Tap-Ui @('仅复制本次', 'Copy this time') 'H03_ACCEPT_RETRY_FAILED'
                    }
                }
                Require-Effects ($null -eq (Wait-UiNode @('复制终端内容？', 'Copy terminal text?') 5)) 'H03_PROMPT_NOT_DISMISSED'
                Start-Sleep -Milliseconds 1000
                $checkedReject = Invoke-HookWithRetry 'inspect' @{ live_marker = $liveMarker; clipboard_fixture = 'reject' }
                $checkedAccept = Invoke-HookWithRetry 'inspect' @{ live_marker = $liveMarker; clipboard_fixture = 'accept' }
                if ($definition.phase -eq 'clipboard-reject') {
                    Require-Effects ($checkedReject.clipboard_fixture_matches -eq $false -and
                        $checkedAccept.clipboard_fixture_matches -eq $false) 'H03_REJECT_CHANGED_CLIPBOARD'
                } else {
                    Require-Effects ($checkedReject.clipboard_fixture_matches -eq $false -and
                        $checkedAccept.clipboard_fixture_matches -eq $true) 'H03_ACCEPT_DID_NOT_COPY'
                }
            }
            'clipboard-covered' {
                Require-Effects ($null -eq (Find-UiNode @('复制终端内容？', 'Copy terminal text?'))) 'H03_COVERED_PROMPT_VISIBLE'
                $clipboard = Invoke-Hook 'inspect' @{ live_marker = $liveMarker; clipboard_fixture = 'accept' }
                Require-Effects ($clipboard.clipboard_fixture_matches -eq $true) 'H03_COVERED_CHANGED_CLIPBOARD'
                $phase.screenshot = Save-Screenshot $definition.phase
                $closed = Invoke-Hook 'release_frozen_history'
                Assert-Session $closed
                Require-Effects ($closed.frozen_active -eq $false -and
                    [long]$closed.live_ack -ge [long]$after.live_ack) 'H03_HISTORY_CLOSE_FAILED'
$script:frozenHistoryOpen = $false
$script:directSettingsRestored = $false
                $after = $closed
                $phase.ack_after = [long]$after.live_ack
            }
            'clipboard-read-only' {
                if ($script:controlReleased) {
                    Restore-TestControl $liveMarker
                    $script:controlReleased = $false
                }
                Require-Effects ($null -eq (Find-UiNode @('复制终端内容？', 'Copy terminal text?'))) 'H03_READ_ONLY_PROMPT_VISIBLE'
                $checkedReadOnly = Invoke-HookWithRetry 'inspect' @{ live_marker = $liveMarker; clipboard_fixture = 'accept' }
                Require-Effects ($checkedReadOnly.clipboard_fixture_matches -eq $true) 'H03_READ_ONLY_CHANGED_CLIPBOARD'
                $phase.screenshot = Save-Screenshot $definition.phase
                $after = Restore-TestControl $liveMarker
                $script:controlReleased = $false
                $phase.ack_after = [long]$after.live_ack
            }
            { $_ -in @('unsupported', 'cursor-restore', 'tab-restore') } {
                $details = try { Find-UiNode @('协议错误详情', 'Protocol details') } catch { $null }
                if ($null -eq $details) {
                    $phase.status = 'unverified'
                    $evidence.unverified += 'C02/C08 Android diagnostic UI was not observed; Windows may have failed closed before Relay delivery.'
                    break
                }
                Tap-Ui @('协议错误详情', 'Protocol details') 'C02_DETAILS_TAP_FAILED'
                $phase.screenshot = Save-Screenshot $definition.phase
                $diagnostic = Get-EffectsDiagnosticVector
                $sequenceNode = Find-UiNodePattern ([regex]::Escape($diagnostic.escaped_line))
                Require-Effects ($null -ne $sequenceNode) 'C02_SEQUENCE_NOT_VISIBLE'
                $display = [string]$sequenceNode.GetAttribute('text') + ' ' +
                    [string]$sequenceNode.GetAttribute('content-desc')
                Require-Effects (Test-EffectsDiagnosticDetails $display $diagnostic) 'C08_DIAGNOSTIC_DETAILS_MISMATCH'
                $phase.diagnostic_category = $diagnostic.category
                $phase.diagnostic_reason = $diagnostic.reason
                $null = Require-Adb @('shell', 'input', 'keyevent', 'KEYCODE_BACK') 'C02_DIALOG_CLOSE_FAILED'
            }
        }
        if ($phase.status -eq 'unverified') { continue }
        $phase.ui_observed = $true
        $phase.status = if ($definition.family -eq 'C02') { 'passed' } else { 'ui_passed' }
        if ($null -ne $after) { $before = $after }
    }
    if ($Family -eq 'H01H02H03') { $evidence.audio = Invoke-H02Audio $before $liveMarker }
    $evidence.status = if ($evidence.unverified.Count -gt 0 -or
        @($evidence.phases | Where-Object { $_.status -eq 'unverified' }).Count -gt 0) { 'partial' } else { 'passed' }
} catch {
    $evidence.status = 'failed'
    $evidence.failure_message = $_.Exception.Message
    if ($null -ne $_.Exception.InnerException) {
        $evidence.failure_inner_message = $_.Exception.InnerException.Message
    }
    $evidence.failure_code = if ($_.Exception.Message -cmatch '^[A-Z0-9_]{1,80}$') {
        $_.Exception.Message
    } else { $_.Exception.GetType().Name }
    $evidence.failure_line = $_.InvocationInfo.ScriptLineNumber
    try {
        if ($null -ne $script:lastHookResponse) {
            [IO.File]::WriteAllText((Join-Path $runRoot 'effects-failure-hook.json'),
                (($script:lastHookResponse | ConvertTo-Json -Depth 12) + "`n"), [Text.UTF8Encoding]::new($false))
        }
        $null = Get-UiNodes
        [IO.File]::WriteAllText((Join-Path $runRoot 'effects-failure-ui.xml'),
            $script:lastUiXml, [Text.UTF8Encoding]::new($false))
        $evidence.failure_screenshot = Save-Screenshot 'failure'
    } catch { $evidence.failure_capture_error = $_.Exception.GetType().Name }
    throw
} finally {
    if ($script:controlReleased) {
        try {
            $null = Restore-TestControl $liveMarker
            $script:controlReleased = $false
        } catch {
            $evidence.cleanup_error_code = 'H03_CONTROL_RESTORE_FAILED'
            $evidence.status = 'failed'
        }
    }
    if ($script:frozenHistoryOpen) {
        try {
            $closed = Invoke-Hook 'release_frozen_history'
            Require-Effects ($closed.frozen_active -eq $false) 'H03_HISTORY_CLEANUP_FAILED'
$script:frozenHistoryOpen = $false
$script:directSettingsRestored = $false
        } catch {
            $evidence.cleanup_error_code = 'H03_HISTORY_CLEANUP_FAILED'
            $evidence.status = 'failed'
        }
    }
    try {
        if ($evidence.android_settings_restore.status -ne 'verified') {
            Restore-EffectsAndroidSettings -AfterHostExit:($null -ne $OwnedSessionHost -and $OwnedSessionHost.HasExited)
        }
    } catch {
        $evidence.cleanup_error_code = if ($_.Exception.Message -cmatch '^[A-Z0-9_]{1,80}$') {
            $_.Exception.Message
        } else { $_.Exception.GetType().Name }
        $evidence.status = 'failed'
    }
    [IO.File]::WriteAllText($EvidencePath, (($evidence | ConvertTo-Json -Depth 6) + "`n"), [Text.UTF8Encoding]::new($false))
}
