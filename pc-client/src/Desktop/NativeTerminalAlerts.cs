using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TermLink.NativeDisplay;
using TermLink.TerminalCore;

namespace TermLink.Desktop;

internal sealed class NativeTerminalAlerts : IDisposable
{
    private const int PlaybackFailureTimeoutMs = 5_000;
    private const uint WaveMapper = 0xffffffff, CallbackEvent = 0x00050000, HeaderDone = 1;
    private const int StillPlaying = 33;
    private static readonly List<RetainedWaveOut> retained = new();
    private static readonly AudioFaultGate processAudioGate = new();
    private readonly string settingsPath;
    private readonly TerminalActionReceipts receipts;
    private readonly AudioFaultGate audioGate;
    private readonly CancellationTokenSource lifetime = new();
    private long lastNotice = -3000, lastSound = -3000;
    private bool failedSettings;
    private Task sound = Task.CompletedTask;

    internal NativeTerminalAlerts(string root, AudioFaultGate? testAudioGate = null)
    {
        settingsPath = Path.Combine(root, "terminal-alerts-v1.bin");
        receipts = new TerminalActionReceipts(Path.Combine(root, "terminal-action-receipts-v1"));
        audioGate = testAudioGate ?? processAudioGate;
    }

    internal (bool Alerts, bool Sounds) Settings() {
        try {
            if (failedSettings || !File.Exists(settingsPath)) return default;
            var data = File.ReadAllBytes(settingsPath);
            return ((data.Length == 3 && data[0] == 1) || (data.Length == 4 && data[0] == 2 && data[3] <= 1)) &&
                data[1] <= 1 && data[2] <= 1 ? (data[1] == 1, data[2] == 1) : default;
        } catch { return default; }
    }

    internal bool ClipboardEnabled {
        get { try {
            if (failedSettings || !File.Exists(settingsPath)) return false;
            var data = File.ReadAllBytes(settingsPath);
            return data.Length == 4 && data[0] == 2 && data[1] <= 1 && data[2] <= 1 && data[3] == 1;
        } catch { return false; } }
    }

    internal bool Set(bool alerts, bool sounds, bool? clipboard = null) {
        string? temporary = null;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
                file.Write(new byte[] { 2, (byte)(alerts ? 1 : 0), (byte)(sounds ? 1 : 0), (byte)((clipboard ?? ClipboardEnabled) ? 1 : 0) }); file.Flush(true);
            }
            File.Move(temporary, settingsPath, true); temporary = null; failedSettings = false;
            return true;
        } catch { failedSettings = true; return false; }
        finally { if (temporary != null) try { File.Delete(temporary); } catch { } }
    }

    // Returns a visual hint; optional audio never runs on the UI or transport thread.
    internal bool Offer(NativeDisplaySignals signals, IntPtr window, bool foreground, Action<string>? clipboardRequested = null) {
        var actions = receipts.Claim(signals);
        if (lifetime.IsCancellationRequested || actions.Count == 0) return false;
        var settings = Settings();
        var decoded = actions.Select(a => NativeTerminalPlatformAction.Decode(a.Payload)).ToArray();
        // Receipts are already durable even when this device refuses or drops the request.
        if (foreground && ClipboardEnabled) {
            var copy = decoded.FirstOrDefault(a => a.Kind == 3);
            if (copy != null) clipboardRequested?.Invoke(copy.Text!);
        }
        var bell = decoded.Any(a => a.Kind == 1);
        var now = Environment.TickCount64;
        var notice = settings.Alerts && bell && now - lastNotice >= 3000;
        if (notice) {
            lastNotice = now;
            if (!foreground && window != IntPtr.Zero && MayNotify()) {
                var flash = new Flash { Size = (uint)Marshal.SizeOf<Flash>(), Window = window, Flags = 3, Count = 3 };
                try { FlashWindowEx(ref flash); } catch { }
            }
        }
        var notes = decoded.FirstOrDefault(a => a.Kind == 2) ?? (bell ? new NativeTerminalPlatformAction(2, 3, 3, new byte[] { 1 }) : null);
        if (notes != null && notes.Volume > 0 && notes.Duration32nds > 0 && notes.Notes.Any(n => n != 0) &&
            settings.Sounds && audioGate.Available && sound.IsCompleted &&
            now - lastSound >= 3000 && MayNotify()) {
            lastSound = now;
            sound = Task.Run(() => Play(notes));
        }
        return notice;
    }

    private static bool MayNotify() {
        try { return OperatingSystem.IsWindows() && SHQueryUserNotificationState(out var state) == 0 && state == 5; }
        catch { return false; }
    }

    private void Play(NativeTerminalPlatformAction action) {
        // Serialize all native windows on this desktop; a terminal cannot build up a playback queue.
        using var mutex = new Mutex(false, "Local\\TermLink.TerminalAudio.v1");
        var acquired = false;
        try {
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            // A different window may have faulted while this worker waited for the mutex.
            if (!acquired || !audioGate.Available || lifetime.IsCancellationRequested ||
                !Settings().Sounds || !MayNotify()) return;
            var wave = Wave(action); if (wave.Length == 44) return;
            _ = TryPlayWave(wave, () => !lifetime.IsCancellationRequested && Settings().Sounds && MayNotify(),
                PlaybackFailureTimeoutMs, new NativeWaveOutApi());
        } catch { /* Device policy and audio failures cannot terminate a CLI session. */ }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    internal sealed class AudioFaultGate {
        private int faulted;
        internal bool Available => Volatile.Read(ref faulted) == 0;
        internal void Fault() => Interlocked.Exchange(ref faulted, 1);
    }

    internal bool UsesProcessAudioGate => ReferenceEquals(audioGate, processAudioGate);

    internal bool TryPlayWave(byte[] wave, Func<bool> allowed, int timeoutMs, IWaveOutApi api,
        int cleanupWaitMs = 2_500) {
        if (!audioGate.Available) return false;
        try {
            if (PlayWave(wave, allowed, timeoutMs, api, cleanupWaitMs)) return true;
        } catch { /* Treat an unexpected native ownership result as process-wide failure. */ }
        audioGate.Fault();
        return false;
    }

    internal enum PlaybackEnd { Completed, Cancelled, TimedOut }

    internal static PlaybackEnd MonitorPlayback(WaitHandle completed, Func<bool> done, Func<bool> allowed, int timeoutMs) {
        var elapsed = Stopwatch.StartNew();
        while (true) {
            if (done()) return PlaybackEnd.Completed;
            bool keepPlaying;
            try { keepPlaying = allowed(); } catch { keepPlaying = false; }
            if (!keepPlaying) return PlaybackEnd.Cancelled;
            if (elapsed.ElapsedMilliseconds >= timeoutMs) return PlaybackEnd.TimedOut;
            completed.WaitOne(20);
        }
    }

    // False means native ownership could not be confirmed; the caller must stop offering audio.
    internal static bool PlayWave(byte[] wave, Func<bool> allowed, int timeoutMs, IWaveOutApi api,
        int cleanupWaitMs = 2_500) {
        if (wave.Length <= 44 || (wave.Length - 44) % 2 != 0) throw new ArgumentException("Invalid PCM wave.", nameof(wave));
        var completed = new AutoResetEvent(false);
        var pinned = default(GCHandle);
        var headerPointer = IntPtr.Zero;
        var device = IntPtr.Zero;
        var opened = false; var prepared = false; var uncertain = false;
        var prepareAttempted = false;
        try {
            var format = new WaveFormatEx { FormatTag = 1, Channels = 1, SamplesPerSecond = 16_000,
                AverageBytesPerSecond = 32_000, BlockAlign = 2, BitsPerSample = 16 };
            if (api.Open(out device, ref format, completed.SafeWaitHandle.DangerousGetHandle()) == 0) {
                opened = true;
                pinned = GCHandle.Alloc(wave, GCHandleType.Pinned);
                headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(new WaveHeader { Data = IntPtr.Add(pinned.AddrOfPinnedObject(), 44),
                    BufferLength = (uint)(wave.Length - 44) }, headerPointer, false);
                prepareAttempted = true;
                if (api.Prepare(device, headerPointer) == 0) {
                    prepared = true;
                    if (api.Write(device, headerPointer) == 0) {
                        var end = MonitorPlayback(completed, () => HeaderIsDone(headerPointer), allowed, timeoutMs);
                        if (end != PlaybackEnd.Completed) _ = api.Reset(device);
                    }
                }
            }
        } catch {
            // An exception during native ownership transfer leaves its outcome unknown.
            uncertain = prepareAttempted || (device != IntPtr.Zero && !opened);
        } finally {
            if (opened && !uncertain) {
                if (prepared) {
                    var cleanup = Stopwatch.StartNew();
                    while (true) {
                        int result;
                        try { result = api.Unprepare(device, headerPointer); }
                        catch { uncertain = true; break; }
                        if (result == 0) break;
                        if (result != StillPlaying || cleanup.ElapsedMilliseconds >= cleanupWaitMs) {
                            uncertain = true; break;
                        }
                        completed.WaitOne(20);
                    }
                }
                if (!uncertain) {
                    try { uncertain = api.Close(device) != 0; }
                    catch { uncertain = true; }
                }
            }
            if (uncertain) {
                // shortcut: a broken driver can retain one pinned buffer, header, event and device per process.
                // Releasing them before buffer ownership is confirmed risks native use-after-free.
                lock (retained) retained.Add(new RetainedWaveOut(pinned, headerPointer, completed, device));
            } else {
                if (headerPointer != IntPtr.Zero) Marshal.FreeHGlobal(headerPointer);
                if (pinned.IsAllocated) pinned.Free();
                completed.Dispose();
            }
        }
        return !uncertain;
    }

    private static bool HeaderIsDone(IntPtr header) =>
        (Marshal.PtrToStructure<WaveHeader>(header).Flags & HeaderDone) != 0;

    internal interface IWaveOutApi {
        int Open(out IntPtr device, ref WaveFormatEx format, IntPtr completed);
        int Prepare(IntPtr device, IntPtr header);
        int Write(IntPtr device, IntPtr header);
        int Reset(IntPtr device);
        int Unprepare(IntPtr device, IntPtr header);
        int Close(IntPtr device);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    private sealed record RetainedWaveOut(GCHandle Pinned, IntPtr Header, AutoResetEvent Completed, IntPtr Device);

    internal static int RetainedWaveOutCount { get { lock (retained) return retained.Count; } }

    private sealed class NativeWaveOutApi : IWaveOutApi {
        public int Open(out IntPtr device, ref WaveFormatEx format, IntPtr completed) =>
            waveOutOpen(out device, WaveMapper, ref format, completed, IntPtr.Zero, CallbackEvent);
        public int Prepare(IntPtr device, IntPtr header) => waveOutPrepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>());
        public int Write(IntPtr device, IntPtr header) => waveOutWrite(device, header, (uint)Marshal.SizeOf<WaveHeader>());
        public int Reset(IntPtr device) => waveOutReset(device);
        public int Unprepare(IntPtr device, IntPtr header) => waveOutUnprepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>());
        public int Close(IntPtr device) => waveOutClose(device);
    }

    internal static byte[] Wave(NativeTerminalPlatformAction sound) {
        const int rate = 16000;
        if (sound.Kind != 2 || sound.Volume > 7 || sound.Notes.Count is < 1 or > 30 || sound.Notes.Any(n => n > 25))
            throw new ArgumentException("Invalid terminal sound.");
        var perNote = rate * sound.Duration32nds / 32;
        var samples = Math.Min(rate * 2, perNote * sound.Notes.Count);
        var wave = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(wave); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), samples * 2);
        for (var i = 0; i < samples; i++) {
            var note = sound.Notes[i / perNote]; var position = i % perNote;
            var length = Math.Min(perNote, samples - i + position);
            var envelope = Math.Min(1, Math.Min(position / 80.0, (length - position) / 80.0));
            var frequency = note == 0 ? 0 : 440 * Math.Pow(2, (71 + note - 69) / 12.0);
            var value = (short)(Math.Sin(2 * Math.PI * frequency * position / rate) * 5000 * sound.Volume / 7 * envelope);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + i * 2), value);
        }
        return wave;
    }

    public void Dispose() { lifetime.Cancel(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct Flash { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref Flash flash);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr device, uint deviceId,
        ref WaveFormatEx format, IntPtr completed, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr device);
    // shortcut: a native driver call can block indefinitely. The mutex remains owned by the
    // background audio worker; the UI/transport thread never waits or forcibly frees its memory.
}
