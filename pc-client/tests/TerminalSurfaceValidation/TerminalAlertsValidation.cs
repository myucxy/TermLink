using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TermLink.Desktop;
using TermLink.NativeDisplay;
using TermLink.TerminalCore;

internal static class TerminalAlertsValidation
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "termlink-alert-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            using var alerts = new NativeTerminalAlerts(root);
            Require(alerts.Settings() == (false, false), "OS effects must default to off.");
            Require(!alerts.ClipboardEnabled, "Clipboard must default to off.");
            var id = Guid.NewGuid();
            NativeDisplaySignals State(ulong sequence) => new(id, new NativeTerminalHostSignals(1, 50),
                new[] { new NativeDisplayAction(sequence, 1, 0, new byte[] { 1,0,0,0 }) });
            Require(!alerts.Offer(State(1), IntPtr.Zero, true), "Default action escaped consent.");
            Require(alerts.Set(true, false), "Cannot save device consent.");
            Require(!alerts.Offer(State(1), IntPtr.Zero, true), "An action denied before consent was replayed.");
            using var replacement = new NativeTerminalAlerts(root);
            Require(replacement.Settings() == (true, false) && !replacement.Offer(State(1), IntPtr.Zero, true), "Replaced consumer lost consent or receipts.");
            Require(replacement.Offer(State(2), IntPtr.Zero, true), "New live bell was not displayed.");
            var wire = State(3).Encode(); var decoded = NativeDisplaySignals.Decode(wire);
            Require(decoded.SessionId == id && decoded.Progress.ProgressPercent == 50 && decoded.Actions.Single().SourceSequence == 3, "Signal codec mismatch.");
            var invalid = wire.Append((byte)0).ToArray();
            try { NativeDisplaySignals.Decode(invalid); throw new Exception("Trailing bytes accepted."); } catch (InvalidDataException) { }
            File.WriteAllBytes(Path.Combine(root, "terminal-action-receipts-v1", id.ToString("N") + ".receipt"), new byte[] { 0 });
            using var corrupted = new NativeTerminalAlerts(root);
            Require(!corrupted.Offer(State(4), IntPtr.Zero, true), "Corrupt persistent receipt allowed a side effect.");
            var blockedRoot = Path.Combine(root, "blocked"); File.WriteAllText(blockedRoot, "not a directory");
            using var blocked = new NativeTerminalAlerts(blockedRoot);
            Require(!blocked.Set(true, true) && !blocked.Offer(State(5), IntPtr.Zero, true), "Storage failure escaped fail-closed policy.");
            var wave = NativeTerminalAlerts.Wave(new NativeTerminalPlatformAction(2, 7, 255, Enumerable.Repeat((byte)25, 30).ToArray()));
            Require(wave.Length == 64044 && BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40)) == 64000, "Audio exceeded two-second budget.");
            var bellWave = NativeTerminalAlerts.Wave(new NativeTerminalPlatformAction(2, 3, 3, new byte[] { 1 }));
            Require(bellWave.Length == 3044, "Short BEL duration changed.");
            WaveOutValidation(bellWave, wave);
            var silent = NativeTerminalAlerts.Wave(new NativeTerminalPlatformAction(2, 7, 32, new byte[] { 0 }));
            Require(silent.Skip(44).All(b => b == 0), "Rest note produced audio.");
            var muted = NativeTerminalAlerts.Wave(new NativeTerminalPlatformAction(2, 0, 32, new byte[] { 1 }));
            Require(muted.Skip(44).All(b => b == 0), "Zero volume produced audio.");
            var empty = NativeTerminalAlerts.Wave(new NativeTerminalPlatformAction(2, 7, 0, new byte[] { 1 }));
            Require(empty.Length == 44, "Zero duration produced audio.");
            var copyId = Guid.NewGuid();
            var value = System.Text.Encoding.UTF8.GetBytes("中文\n"); var payload = new byte[8 + value.Length];
            payload[0] = 3; BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)value.Length); value.CopyTo(payload, 8);
            NativeDisplaySignals Copy(ulong seq) => new(copyId, new NativeTerminalHostSignals(0, 0),
                new[] { new NativeDisplayAction(seq, 8, 0, payload) });
            using var copies = new NativeTerminalAlerts(root); var copied = new List<string>();
            copies.Offer(Copy(1), IntPtr.Zero, true, copied.Add);
            Require(copied.Count == 0 && copies.Set(false, false, true), "Default copy bypassed consent.");
            copies.Offer(Copy(1), IntPtr.Zero, true, copied.Add);
            Require(copied.Count == 0, "Previously refused copy replayed after enabling.");
            copies.Offer(Copy(2), IntPtr.Zero, false, copied.Add);
            copies.Offer(Copy(2), IntPtr.Zero, true, copied.Add);
            Require(copied.Count == 0, "Background copy retried in foreground.");
            copies.Offer(Copy(3), IntPtr.Zero, true, copied.Add);
            Require(copied.SequenceEqual(new[] { "中文\n" }), "Exact clipboard text lost.");
            using var copyReplacement = new NativeTerminalAlerts(root);
            copyReplacement.Offer(Copy(3), IntPtr.Zero, true, copied.Add);
            Require(copyReplacement.ClipboardEnabled && copied.Count == 1, "Clipboard receipt lost across restart.");
            Require(copies.Set(true, false) && copies.ClipboardEnabled, "Changing alerts reset clipboard consent.");
            Console.WriteLine("Terminal alerts: defaults, persistent denied attempts, replacement, corruption, I/O failure, codec and bounded PCM passed.");
        } finally { Directory.Delete(root, true); }
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private static void WaveOutValidation(byte[] bellWave, byte[] longWave)
    {
        Require(Marshal.SizeOf<NativeTerminalAlerts.WaveFormatEx>() == 18 &&
            Marshal.OffsetOf<NativeTerminalAlerts.WaveFormatEx>(nameof(NativeTerminalAlerts.WaveFormatEx.ExtraSize)).ToInt32() == 16,
            "WAVEFORMATEX PCM layout changed.");
        Require(Marshal.SizeOf<NativeTerminalAlerts.WaveHeader>() == (IntPtr.Size == 8 ? 48 : 32) &&
            Marshal.OffsetOf<NativeTerminalAlerts.WaveHeader>(nameof(NativeTerminalAlerts.WaveHeader.Flags)).ToInt32() ==
                (IntPtr.Size == 8 ? 24 : 16), "WAVEHDR native layout changed.");

        var natural = new FakeWaveOutApi { CompleteOnWrite = true };
        Require(NativeTerminalAlerts.PlayWave(bellWave, () => true, 1_000, natural) &&
            natural.Calls.SequenceEqual(new[] { "open", "prepare", "write", "unprepare", "close" }) &&
            natural.Format.FormatTag == 1 && natural.Format.Channels == 1 &&
            natural.Format.SamplesPerSecond == 16_000 && natural.Format.AverageBytesPerSecond == 32_000 &&
            natural.Format.BlockAlign == 2 && natural.Format.BitsPerSample == 16 && natural.Format.ExtraSize == 0 &&
            natural.BufferLength == bellWave.Length - 44,
            "Natural completion reset the device or PCM format changed.");

        var cancelled = new FakeWaveOutApi();
        Require(NativeTerminalAlerts.PlayWave(longWave, () => false, 1_000, cancelled) &&
            cancelled.Calls.SequenceEqual(new[] { "open", "prepare", "write", "reset", "unprepare", "close" }) &&
            cancelled.BufferLength == 64_000, "Cancellation did not reset exactly once and return the two-second buffer.");

        var timedOut = new FakeWaveOutApi();
        var timeout = Stopwatch.StartNew();
        Require(NativeTerminalAlerts.PlayWave(bellWave, () => true, 80, timedOut) &&
            timedOut.Calls.SequenceEqual(new[] { "open", "prepare", "write", "reset", "unprepare", "close" }) &&
            timeout.ElapsedMilliseconds is >= 80 and < 1_000, "Timed out sound did not reset exactly once.");

        foreach (var failure in new[] { "open", "prepare", "write", "reset", "unprepare", "close" }) {
            var api = new FakeWaveOutApi { FailAt = failure };
            var safe = NativeTerminalAlerts.PlayWave(bellWave, () => false, 1_000, api, cleanupWaitMs: 0);
            var expected = failure switch {
                "open" => new[] { "open" },
                "prepare" => new[] { "open", "prepare", "close" },
                "write" => new[] { "open", "prepare", "write", "unprepare", "close" },
                "reset" or "unprepare" => new[] { "open", "prepare", "write", "reset", "unprepare" },
                _ => new[] { "open", "prepare", "write", "reset", "unprepare", "close" }
            };
            Require(api.Calls.SequenceEqual(expected) && safe == (failure is "open" or "prepare" or "write"),
                "waveOut " + failure + " failure released a driver-owned buffer or skipped cleanup.");
        }

        var healthyGate = new NativeTerminalAlerts.AudioFaultGate();
        using (var firstDefault = new NativeTerminalAlerts(Path.GetTempPath()))
        using (var secondDefault = new NativeTerminalAlerts(Path.GetTempPath()))
            Require(firstDefault.UsesProcessAudioGate && secondDefault.UsesProcessAudioGate,
                "Production windows do not share the process audio gate.");
        using (var healthy = new NativeTerminalAlerts(Path.GetTempPath(), healthyGate)) {
            var api = new FakeWaveOutApi { CompleteOnWrite = true };
            Require(healthy.TryPlayWave(bellWave, () => true, 1_000, api) && healthyGate.Available &&
                api.Calls.SequenceEqual(new[] { "open", "prepare", "write", "unprepare", "close" }),
                "Normal playback faulted the shared audio gate.");
        }

        var retainedBefore = NativeTerminalAlerts.RetainedWaveOutCount;
        var uncertainGate = new NativeTerminalAlerts.AudioFaultGate();
        using (var first = new NativeTerminalAlerts(Path.GetTempPath(), uncertainGate)) {
            var api = new FakeWaveOutApi { FailAt = "unprepare" };
            Require(!first.TryPlayWave(bellWave, () => false, 1_000, api, cleanupWaitMs: 0) &&
                !uncertainGate.Available && NativeTerminalAlerts.RetainedWaveOutCount == retainedBefore + 1 &&
                api.Calls.SequenceEqual(new[] { "open", "prepare", "write", "reset", "unprepare" }),
                "Unknown native buffer ownership was freed or did not latch the shared gate.");
        }
        using (var replacement = new NativeTerminalAlerts(Path.GetTempPath(), uncertainGate)) {
            var blocked = new FakeWaveOutApi();
            Require(!replacement.TryPlayWave(bellWave, () => true, 1_000, blocked) && blocked.Calls.Count == 0 &&
                NativeTerminalAlerts.RetainedWaveOutCount == retainedBefore + 1,
                "A new window bypassed the process audio fault latch.");
        }

        var thrownGate = new NativeTerminalAlerts.AudioFaultGate();
        using (var first = new NativeTerminalAlerts(Path.GetTempPath(), thrownGate)) {
            var api = new FakeWaveOutApi { ThrowAt = "prepare" };
            Require(!first.TryPlayWave(bellWave, () => true, 1_000, api) && !thrownGate.Available &&
                NativeTerminalAlerts.RetainedWaveOutCount == retainedBefore + 2 &&
                api.Calls.SequenceEqual(new[] { "open", "prepare" }),
                "Native ownership exception released an unconfirmed buffer or left the gate open.");
        }
        using (var replacement = new NativeTerminalAlerts(Path.GetTempPath(), thrownGate)) {
            var blocked = new FakeWaveOutApi();
            Require(!replacement.TryPlayWave(bellWave, () => true, 1_000, blocked) && blocked.Calls.Count == 0,
                "A new instance bypassed the fault latch after a native exception.");
        }
    }

    private sealed class FakeWaveOutApi : NativeTerminalAlerts.IWaveOutApi
    {
        internal readonly List<string> Calls = new();
        internal string? FailAt;
        internal string? ThrowAt;
        internal bool CompleteOnWrite;
        internal uint BufferLength;
        internal NativeTerminalAlerts.WaveFormatEx Format;
        private IntPtr header;

        public int Open(out IntPtr device, ref NativeTerminalAlerts.WaveFormatEx format, IntPtr completed) {
            Calls.Add("open"); Format = format;
            Require(completed != IntPtr.Zero, "waveOut callback event is missing.");
            device = FailAt == "open" ? IntPtr.Zero : new IntPtr(1);
            return FailAt == "open" ? 1 : 0;
        }

        public int Prepare(IntPtr device, IntPtr value) {
            Calls.Add("prepare"); header = value;
            BufferLength = Marshal.PtrToStructure<NativeTerminalAlerts.WaveHeader>(header).BufferLength;
            if (ThrowAt == "prepare") throw new InvalidOperationException("Simulated native prepare failure.");
            return FailAt == "prepare" ? 1 : 0;
        }

        public int Write(IntPtr device, IntPtr value) {
            Calls.Add("write");
            if (CompleteOnWrite) Complete();
            return FailAt == "write" ? 1 : 0;
        }

        public int Reset(IntPtr device) {
            Calls.Add("reset");
            if (FailAt == "reset") return 1;
            Complete(); return 0;
        }

        public int Unprepare(IntPtr device, IntPtr value) {
            Calls.Add("unprepare");
            return FailAt == "unprepare" ? 1 : FailAt == "reset" ? 33 : 0;
        }

        public int Close(IntPtr device) { Calls.Add("close"); return FailAt == "close" ? 1 : 0; }

        private void Complete() {
            var value = Marshal.PtrToStructure<NativeTerminalAlerts.WaveHeader>(header);
            value.Flags |= 1;
            Marshal.StructureToPtr(value, header, false);
        }
    }
}
