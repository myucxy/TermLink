using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using TermLink.Desktop;
using TermLink.NativeDisplay;
using TermLink.TerminalCore;

internal static class WindowsTerminalAudioValidation
{
    private const int CaptureSeconds = 14;
    private const double BellHz = 523.251, MidHz = 659.255, HighHz = 783.991;

    internal static void SelfCheck()
    {
        Wave.SelfCheck();
        Capture.LaunchArgumentSelfCheck();
        DiscontinuitySelfCheck();
    }

    internal static int Run(string outputRoot)
    {
        if (!Path.IsPathFullyQualified(outputRoot))
            throw new ArgumentException("Provide an absolute output root.", nameof(outputRoot));
        var root = Path.Combine(Path.GetFullPath(outputRoot), "terminal-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            var result = Probe(root);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", root, result.WavPath,
                result.EndpointId, result.Discontinuities, result.DiscontinuityDetails }));
            return 0;
        } catch (ProbeUnverifiedException error) {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "unverified", root, reason = error.Message }));
            return 2;
        } catch (Exception error) {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", root, reason = error.ToString() }));
            return 1;
        }
    }

    private static WindowsLoopbackRecorder.Result Probe(string root)
    {
        var id = Guid.NewGuid();
        using var alerts = new NativeTerminalAlerts(root);
        Require(alerts.Settings() == (false, false) && alerts.Set(true, true),
            "Isolated alert settings could not be enabled.");
        NativeDisplaySignals Signal(ulong sequence, byte[] payload) => new(id,
            new NativeTerminalHostSignals(0, 0), new[] { new NativeDisplayAction(sequence, 1, 0, payload) });
        var bell = Signal(1, new byte[] { 1, 0, 0, 0 });
        var throttled = Signal(2, new byte[] { 1, 0, 0, 0 });
        if (!MayNotify(out var state)) {
            alerts.Offer(bell, IntPtr.Zero, true);
            throw new ProbeUnverifiedException("Windows notification state " + state +
                " blocks production Play; no audio result can be claimed.");
        }

        using var capture = Capture.Start(Path.Combine(root, "production-offer.wav"), CaptureSeconds);
        var clock = Stopwatch.StartNew();
        WaitUntil(clock, .45);
        Require(alerts.Offer(bell, IntPtr.Zero, true), "Enabled BEL did not produce its visual notice.");
        WaitUntil(clock, .80);
        Require(!alerts.Offer(bell, IntPtr.Zero, true), "Duplicate durable BEL receipt replayed.");
        alerts.Offer(throttled, IntPtr.Zero, true); // Fresh receipt inside the 3-second audio gate.
        WaitUntil(clock, 3.85);
        alerts.Offer(Signal(3, new byte[] { 2, 7, 3, 3, 1, 5, 8 }), IntPtr.Zero, true);
        WaitUntil(clock, 7.20);
        Require(!alerts.Offer(throttled, IntPtr.Zero, true), "Throttled receipt replayed after 3 seconds.");
        Require(alerts.Set(true, false), "Isolated sound setting could not be disabled.");
        WaitUntil(clock, 7.70);
        alerts.Offer(Signal(4, new byte[] { 2, 7, 3, 1, 1 }), IntPtr.Zero, true);
        WaitUntil(clock, 10.40);
        Require(alerts.Set(true, true), "Isolated sound setting could not be restored.");
        var longNotes = new byte[34];
        longNotes[0] = 2; longNotes[1] = 7; longNotes[2] = 255; longNotes[3] = 30;
        Array.Fill(longNotes, (byte)1, 4, 30);
        alerts.Offer(Signal(5, longNotes), IntPtr.Zero, true);
        WaitUntil(clock, 10.75);
        alerts.Dispose();
        WaitUntil(clock, 11.20);
        Require(!alerts.Offer(Signal(6, new byte[] { 1, 0, 0, 0 }), IntPtr.Zero, true),
            "Disposed alerts offered another visual effect.");

        var metadata = capture.Complete();
        if (!CaptureHasOnlyStartupDiscontinuity(metadata))
            throw new ProbeUnverifiedException("WASAPI capture has an unverified gap: discontinuities=" +
                metadata.Discontinuities + " details=" + metadata.DiscontinuityDetails?.Count +
                " truncated=" + metadata.DiscontinuityDetailsTruncated +
                " frames=" + metadata.Frames + "/" + metadata.ExpectedFrames + ".");
        var wave = Wave.Read(metadata.WavPath);
        Require(wave.Frames == metadata.Frames && wave.Rate == metadata.SampleRate &&
            wave.Channels == metadata.Channels, "WASAPI metadata disagrees with its WAV file.");
        var baseline = MaxTone(wave, 0, .30);
        if (baseline > .003)
            throw new ProbeUnverifiedException("Default render endpoint was already playing audio during baseline.");
        // waveOut's first device start was recorded at .822 s (the prior endpoint at .673 s).
        // The 1.10 s bound contains either complete 93.75 ms BEL without hiding a later replay.
        var first = wave.Peak(BellHz, .25, 1.10);
        if (first.Amplitude < Math.Max(.0015, baseline * 4))
            throw new ProbeUnverifiedException("Production BEL was not audible in WASAPI loopback; " +
                "check system-sound volume, endpoint, notification policy or competing playback.");
        Require(first.Amplitude > wave.Peak(MidHz, .25, 1.10).Amplitude * 1.6,
            "BEL did not have a dominant 523 Hz component.");
        Require(FullBell(wave, out var bellBurst),
            "BEL must be one complete 75–130 ms burst: count=" + bellBurst.Bursts +
            " spanMs=" + bellBurst.SpanMs + " audibleMs=" + bellBurst.AudibleMs + ".");
        Quiet(wave, 1.10, 3.50, first.Amplitude, baseline, "duplicate receipt, 3-second gate or queue");

        var low = wave.Peak(BellHz, 3.65, 4.50);
        var mid = wave.Peak(MidHz, 3.65, 4.50);
        var high = wave.Peak(HighHz, 3.65, 4.50);
        Require(low.Amplitude > .0015 && mid.Amplitude > .0015 && high.Amplitude > .0015 &&
            low.Time + .045 < mid.Time && mid.Time + .045 < high.Time &&
            high.Time - low.Time < .35,
            "Production DECPS tones were absent or not ordered 523/659/784 Hz.");
        Require(wave.Tone(BellHz, low.Time) > wave.Tone(MidHz, low.Time) * 1.4 &&
            wave.Tone(MidHz, mid.Time) > wave.Tone(HighHz, mid.Time) * 1.4 &&
            wave.Tone(HighHz, high.Time) > wave.Tone(BellHz, high.Time) * 1.4,
            "DECPS tone frequencies were not distinct at their recorded peaks.");
        Quiet(wave, 4.55, 7.10, low.Amplitude, baseline, "DECPS playback queued or repeated");
        Quiet(wave, 7.30, 10.10, first.Amplitude, baseline, "disabled sound or consumed receipt replayed");

        var beforeDispose = wave.Peak(BellHz, 10.30, 10.95);
        Require(beforeDispose.Amplitude > .0015,
            "Long production sound did not start before Dispose.");
        Quiet(wave, 11.30, 13.60, beforeDispose.Amplitude, baseline,
            "Dispose did not stop sound or queued a later offer");
        if (!MayNotify(out state))
            throw new ProbeUnverifiedException("Windows notification policy changed during capture: " + state);
        return metadata;
    }

    private static void Quiet(Wave wave, double from, double to, double reference,
        double baseline, string caseName)
    {
        Require(MaxTone(wave, from, to) < Math.Max(.0015, Math.Max(baseline * 2, reference * .20)),
            "Unexpected recorded tone during " + caseName + ".");
    }

    private static double MaxTone(Wave wave, double from, double to) =>
        Math.Max(wave.Peak(BellHz, from, to).Amplitude,
            Math.Max(wave.Peak(MidHz, from, to).Amplitude, wave.Peak(HighHz, from, to).Amplitude));

    private static bool FullBell(Wave wave, out (int Bursts, double SpanMs, double AudibleMs) burst)
    {
        var amplitude = wave.Peak(BellHz, .25, 1.10).Amplitude;
        burst = wave.SoundBursts(.25, 1.10, Math.Max(.0001, amplitude * .05));
        return amplitude >= .0015 && burst.Bursts == 1 &&
            burst.SpanMs is >= 75 and <= 130 && burst.AudibleMs >= 71;
    }

    private static bool CaptureHasOnlyStartupDiscontinuity(WindowsLoopbackRecorder.Result result)
    {
        if (result.ExpectedFrames <= 0 || result.Frames != result.ExpectedFrames ||
            result.DiscontinuityDetailsTruncated || result.DiscontinuityDetails == null) return false;
        if (result.Discontinuities == 0) return result.DiscontinuityDetails.Count == 0;
        if (result.Discontinuities != 1 || result.DiscontinuityDetails.Count != 1) return false;
        var detail = result.DiscontinuityDetails[0];
        return detail.FirstPacket && detail.FrameOffset == 0 &&
            detail.PacketFrames > 0 && detail.PacketFrames <= result.Frames &&
            (detail.Flags & 1) != 0;
    }

    private static void DiscontinuitySelfCheck()
    {
        var clean = new WindowsLoopbackRecorder.Result("probe.wav", "endpoint", "IEEE_FLOAT",
            2, 48000, 32, 100, 100, 800, 0, [], false);
        var first = new WindowsLoopbackRecorder.DiscontinuityDetail(0, 10, 1, 1, 1, true);
        var startup = clean with { Discontinuities = 1, DiscontinuityDetails = [first] };
        Require(CaptureHasOnlyStartupDiscontinuity(clean) &&
            CaptureHasOnlyStartupDiscontinuity(startup) &&
            !CaptureHasOnlyStartupDiscontinuity(startup with { DiscontinuityDetails = [first with { FirstPacket = false }] }) &&
            !CaptureHasOnlyStartupDiscontinuity(startup with { DiscontinuityDetails = [first with { FrameOffset = 10 }] }) &&
            !CaptureHasOnlyStartupDiscontinuity(startup with { DiscontinuityDetails = [first with { Flags = 0 }] }) &&
            !CaptureHasOnlyStartupDiscontinuity(startup with { DiscontinuityDetails = [] }) &&
            !CaptureHasOnlyStartupDiscontinuity(startup with { DiscontinuityDetailsTruncated = true }) &&
            !CaptureHasOnlyStartupDiscontinuity(startup with { Frames = 99 }),
            "WASAPI discontinuity classification accepted a missing or middle-stream gap.");
    }

    private static void WaitUntil(Stopwatch clock, double seconds)
    {
        while (clock.Elapsed.TotalSeconds < seconds)
            Thread.Sleep(Math.Min(20, Math.Max(1, (int)((seconds - clock.Elapsed.TotalSeconds) * 1000))));
    }

    private static bool MayNotify(out int state)
    {
        state = -1;
        try { return SHQueryUserNotificationState(out state) == 0 && state == 5; }
        catch { return false; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private sealed class ProbeUnverifiedException(string message) : Exception(message) { }

    private sealed class Capture : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> output;
        private readonly Task<string> errors;
        private readonly string wavPath;
        private readonly string endpoint;
        private readonly int seconds;

        private Capture(Process process, Task<string> output, Task<string> errors,
            string wavPath, string endpoint, int seconds)
        {
            this.process = process; this.output = output; this.errors = errors;
            this.wavPath = wavPath; this.endpoint = endpoint; this.seconds = seconds;
        }

        internal static Capture Start(string wavPath, int seconds)
        {
            var executable = Environment.ProcessPath ??
                throw new ProbeUnverifiedException("Current executable path is unavailable.");
            var launch = new ProcessStartInfo(executable) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var command = Environment.GetCommandLineArgs()[0];
            var managedEntry = ManagedEntryArgument(executable, command);
            if (managedEntry != null) launch.ArgumentList.Add(managedEntry);
            launch.ArgumentList.Add("--record-loopback");
            launch.ArgumentList.Add(wavPath);
            launch.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Process process;
            try { process = Process.Start(launch) ??
                throw new ProbeUnverifiedException("WASAPI child recorder could not start."); }
            catch (System.ComponentModel.Win32Exception error) {
                throw new ProbeUnverifiedException("WASAPI child recorder could not start: " + error.Message);
            }
            try {
                var output = process.StandardOutput.ReadToEndAsync();
                var ready = process.StandardError.ReadLineAsync();
                if (!ready.Wait(TimeSpan.FromSeconds(10)) ||
                    ready.Result is not string line || !line.StartsWith("LOOPBACK_READY ", StringComparison.Ordinal))
                    throw new ProbeUnverifiedException("WASAPI recorder was not ready: " +
                        (ready.IsCompleted ? ready.Result : "timeout"));
                return new Capture(process, output, process.StandardError.ReadToEndAsync(),
                    wavPath, line[15..], seconds);
            } catch {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        private static string? ManagedEntryArgument(string executable, string command)
        {
            if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!command.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new ProbeUnverifiedException("dotnet launcher has no managed DLL entry point.");
            return command;
        }

        internal static void LaunchArgumentSelfCheck()
        {
            const string dll = @"C:\probe\TermLink.TerminalSurface.Validation.dll";
            Require(ManagedEntryArgument(@"C:\probe\TermLink.TerminalSurface.Validation.exe", dll) == null &&
                ManagedEntryArgument(@"C:\probe\dotnet.exe", dll) == dll,
                "Apphost/dotnet child launch arguments diverged.");
            try { _ = ManagedEntryArgument(@"C:\probe\dotnet.exe", @"C:\probe\Validation.exe"); }
            catch (ProbeUnverifiedException) { return; }
            throw new InvalidOperationException("dotnet launch accepted a non-DLL entry point.");
        }

        internal WindowsLoopbackRecorder.Result Complete()
        {
            if (!process.WaitForExit((seconds + 10) * 1000))
                throw new ProbeUnverifiedException("WASAPI recorder timed out.");
            var recorderOutput = output.Result;
            var recorderErrors = errors.Result;
            var root = Path.GetDirectoryName(wavPath)!;
            if (recorderOutput.Length > 8192 || recorderErrors.Length > 8192)
                throw new ProbeUnverifiedException("WASAPI recorder metadata or log exceeded 8192 characters.");
            File.WriteAllText(Path.Combine(root, "recorder.json"), recorderOutput);
            File.WriteAllText(Path.Combine(root, "recorder.log"),
                "LOOPBACK_READY " + endpoint + Environment.NewLine + recorderErrors);
            if (process.ExitCode != 0)
                throw new ProbeUnverifiedException("WASAPI recorder failed: " + recorderErrors);
            var metadata = JsonSerializer.Deserialize<WindowsLoopbackRecorder.Result>(recorderOutput)
                ?? throw new InvalidDataException("WASAPI recorder returned no metadata.");
            if (metadata.EndpointId != endpoint || metadata.WavPath != wavPath ||
                metadata.Bytes <= 0 || !File.Exists(wavPath))
                throw new InvalidDataException("WASAPI recorder metadata is inconsistent.");
            return metadata;
        }

        public void Dispose()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }

    private sealed class Wave
    {
        private readonly double[] samples;
        internal long Frames { get; }
        internal int Channels { get; }
        internal uint Rate { get; }
        private readonly double analysisRate;

        private Wave(double[] samples, long frames, int channels, uint rate, double analysisRate)
        {
            this.samples = samples; Frames = frames; Channels = channels;
            Rate = rate; this.analysisRate = analysisRate;
        }

        internal static void SelfCheck()
        {
            const int rate = 8000;
            var samples = new double[rate];
            var frequencies = new[] { BellHz, MidHz, HighHz };
            for (var note = 0; note < frequencies.Length; note++)
                for (var i = 800 + note * 800; i < 1360 + note * 800; i++)
                    samples[i] = .08 * Math.Sin(2 * Math.PI * frequencies[note] * i / rate);
            var wave = new Wave(samples, rate, 1, rate, rate);
            var low = wave.Peak(BellHz, .08, .45);
            var mid = wave.Peak(MidHz, .08, .45);
            var high = wave.Peak(HighHz, .08, .45);
            Require(low.Amplitude > .05 && mid.Amplitude > .05 && high.Amplitude > .05 &&
                low.Time < mid.Time && mid.Time < high.Time &&
                wave.Peak(BellHz, .50, .90).Amplitude < .001,
                "Frequency analysis did not distinguish ordered tones and silence.");

            static Wave Bells(params (double Start, double Duration)[] intervals) {
                const int sampleRate = 8000;
                var data = new double[sampleRate * 2];
                foreach (var (start, duration) in intervals) {
                    var first = (int)(start * sampleRate);
                    var count = (int)(duration * sampleRate);
                    for (var i = 0; i < count; i++)
                        data[first + i] = .08 * Math.Sin(2 * Math.PI * BellHz * i / sampleRate);
                }
                return new Wave(data, data.Length, 1, sampleRate, sampleRate);
            }
            Require(FullBell(Bells((.673, .09375)), out _) &&
                FullBell(Bells((.822, .09375)), out _) &&
                !FullBell(Bells((.673, .00525)), out _) &&
                !FullBell(Bells((.673, .09375), (.94, .09375)), out _) &&
                !FullBell(Bells((.673, .19)), out _) &&
                !FullBell(Bells(), out _),
                "2 ms BEL burst oracle missed startup delay, truncated, duplicate, extended or silent audio.");
        }

        internal (int Bursts, double SpanMs, double AudibleMs) SoundBursts(double from, double to, double threshold)
        {
            // At 8 kHz analysis rate a 2 ms RMS bucket contains 16 samples. Adjacent audible
            // buckets may have at most two silent buckets (4 ms) before becoming another burst.
            var bucket = Math.Max(1, (int)Math.Round(analysisRate * .002));
            var first = Math.Max(0, (int)(from * analysisRate));
            var limit = Math.Min(samples.Length, (int)(to * analysisRate));
            var bursts = 0; var firstActive = -1; var lastActive = -1;
            var previousActive = -1; var activeBuckets = 0;
            for (int start = first, index = 0; start + bucket <= limit; start += bucket, index++) {
                var sum = 0.0;
                for (var i = start; i < start + bucket; i++) sum += samples[i] * samples[i];
                if (Math.Sqrt(sum / bucket) < threshold) continue;
                if (previousActive < 0 || index - previousActive > 3) bursts++;
                if (firstActive < 0) firstActive = index;
                lastActive = previousActive = index;
                activeBuckets++;
            }
            var milliseconds = bucket * 1000.0 / analysisRate;
            return (bursts, firstActive < 0 ? 0 : (lastActive - firstActive + 1) * milliseconds,
                activeBuckets * milliseconds);
        }

        internal static Wave Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8)
                throw new InvalidDataException("Recorded WAV has an invalid RIFF header.");
            var offset = 12; var dataOffset = -1; var dataSize = 0;
            var tag = 0; var channels = 0; var bits = 0; var align = 0; uint rate = 0;
            while (offset <= bytes.Length - 8) {
                var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
                var body = offset + 8;
                if ((long)body + size + (size & 1) > bytes.Length)
                    throw new InvalidDataException("Recorded WAV has an invalid chunk length.");
                if (bytes.AsSpan(offset, 4).SequenceEqual("fmt "u8)) {
                    if (tag != 0 || size < 16) throw new InvalidDataException("Recorded WAV format is invalid.");
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2));
                    rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 4));
                    align = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 12));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14));
                    if (tag == 0xfffe && size >= 40) {
                        var subtype = new Guid(bytes.AsSpan(body + 24, 16));
                        tag = subtype == new Guid("00000003-0000-0010-8000-00AA00389B71") ? 3 :
                            subtype == new Guid("00000001-0000-0010-8000-00AA00389B71") ? 1 : 0;
                    }
                } else if (bytes.AsSpan(offset, 4).SequenceEqual("data"u8)) {
                    if (dataOffset != -1) throw new InvalidDataException("Recorded WAV repeats the data chunk.");
                    dataOffset = body; dataSize = size;
                }
                offset = body + size + (size & 1);
            }
            if (offset != bytes.Length || dataOffset < 0 || channels is < 1 or > 8 ||
                rate is < 8000 or > 192000 || bits is not (8 or 16 or 24 or 32 or 64) ||
                tag is not (1 or 3) || (tag == 3 && bits is not (32 or 64)) ||
                (tag == 1 && bits == 64) || align != channels * bits / 8 ||
                dataSize % align != 0)
                throw new InvalidDataException("Recorded WAV mix format is unsupported.");
            var frames = dataSize / align;
            var step = Math.Max(1, (int)(rate / 8000));
            var samples = new double[(frames + step - 1) / step];
            for (var i = 0; i < samples.Length; i++) {
                var at = dataOffset + i * step * align;
                samples[i] = tag == 3 ? bits == 32 ? BitConverter.ToSingle(bytes, at) : BitConverter.ToDouble(bytes, at) :
                    bits == 8 ? (bytes[at] - 128) / 128.0 :
                    bits == 16 ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at)) / 32768.0 :
                    bits == 24 ? Sign24(bytes, at) / 8388608.0 :
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)) / 2147483648.0;
                if (!double.IsFinite(samples[i]) || Math.Abs(samples[i]) > 1.5)
                    throw new InvalidDataException("Recorded WAV contains an invalid audio sample.");
            }
            return new Wave(samples, frames, channels, rate, (double)rate / step);
        }

        private static int Sign24(byte[] data, int offset)
        {
            var value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
            return (value & 0x800000) != 0 ? value - 0x1000000 : value;
        }

        internal (double Amplitude, double Time) Peak(double frequency, double from, double to)
        {
            var length = Math.Max(128, (int)(analysisRate * .065));
            var first = Math.Max(0, (int)(from * analysisRate));
            var last = Math.Min(samples.Length - length, (int)(to * analysisRate));
            var stride = Math.Max(1, (int)(analysisRate * .01));
            var best = (Amplitude: 0.0, Time: from);
            for (var start = first; start <= last; start += stride) {
                var amplitude = ToneAt(frequency, start, length);
                if (amplitude > best.Amplitude)
                    best = (amplitude, (start + length / 2.0) / analysisRate);
            }
            return best;
        }

        internal double Tone(double frequency, double time)
        {
            var length = Math.Max(128, (int)(analysisRate * .065));
            var start = Math.Clamp((int)(time * analysisRate) - length / 2, 0, samples.Length - length);
            return ToneAt(frequency, start, length);
        }

        private double ToneAt(double frequency, int start, int length)
        {
            var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / analysisRate);
            var a = 0.0; var b = 0.0;
            for (var i = start; i < start + length; i++) {
                var next = samples[i] + coefficient * a - b;
                b = a; a = next;
            }
            return 2 * Math.Sqrt(Math.Max(0, a * a + b * b - coefficient * a * b)) / length;
        }
    }
}
