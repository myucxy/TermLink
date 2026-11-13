using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Agent.Connection;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

namespace TermLink.Agent.Terminal;

/// <summary>Mac PTY and native core authority. Network/UI reconnects do not own these processes.</summary>
public sealed partial class PortableTerminalHost : IRelayTerminalHost
{
    private readonly ITerminalSessionHost pty;
    private readonly ConcurrentDictionary<Guid, Session> sessions = new();
    private readonly Dictionary<Guid, Guid> launches = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task clock;
    private int disposed;
    private readonly string? stateDirectory;
    private readonly byte[]? stateKey;
    private readonly PortableTerminalDeletionStore? deletions;
    private static readonly object nativeSync = new();
    private static bool loaded;

    public static void LoadNative(string directory)
    {
        lock (nativeSync)
        {
            if (loaded) return;
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            var path = Path.Combine(directory, "libtermlink_terminal_ffi.dylib");
            // The application packaging verifier pins this binary to the macOS lock.
            var handle = NativeLibrary.Load(path);
            NativeLibrary.SetDllImportResolver(typeof(NativeTerminalCoreV2).Assembly,
                (name, assembly, paths) => name == "termlink_terminal_ffi.dll" ? handle : IntPtr.Zero);
            NativeTerminalCoreV2.ValidateAbi();
            using var smoke = NativeTerminalCoreV2.Create(80, 24);
            loaded = true;
        }
    }

    public PortableTerminalHost(ITerminalSessionHost pty, IReadOnlyList<TerminalCommandCapability> commands, string? stateDirectory = null, byte[]? stateKey = null)
    {
        this.pty = pty;
        UpdateCommands(commands);
        NativeTerminalCoreV2.ValidateAbi();
        if ((stateDirectory is null) != (stateKey is null) || (stateKey is not null && stateKey.Length != 32)) throw new ArgumentException("Invalid terminal state store");
        this.stateDirectory = stateDirectory;
        this.stateKey = stateKey?.ToArray();
        if (stateDirectory is not null)
        {
            Directory.CreateDirectory(stateDirectory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            deletions = new PortableTerminalDeletionStore(stateDirectory, this.stateKey!);
            foreach (var deleted in deletions.Records)
                if (deleted.OperationId != Guid.Empty) launches.Add(deleted.OperationId, deleted.SessionId);
            Restore();
        }
        clock = AdvanceClockAsync();
    }
    private IReadOnlyList<TerminalCommandCapability> commands = Array.Empty<TerminalCommandCapability>();
    public IReadOnlyList<TerminalCommandCapability> Commands => Volatile.Read(ref commands);
    public void UpdateCommands(IReadOnlyList<TerminalCommandCapability> values) => Volatile.Write(ref commands,
        values.Where(x =>
        {
            try { return x.Enabled && Path.IsPathFullyQualified(x.Executable) && File.Exists(x.Executable) &&
                (OperatingSystem.IsWindows() || (File.GetUnixFileMode(x.Executable) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }).ToArray());
    public IReadOnlyList<RelayTerminalSession> Sessions => sessions.Values.Select(s => s.View).OrderBy(s => s.CreatedAt).ToArray();

    public async Task<Guid> StartAsync(string command, string directory, Guid operationId, CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (operationId != Guid.Empty && launches.TryGetValue(operationId, out var prior)) return prior;
            if (sessions.Count >= 64) throw new InvalidOperationException("TERMINAL_SESSION_LIMIT");
            var profile = Commands.SingleOrDefault(c => c.Id == command) ?? throw new InvalidDataException("TERMINAL_COMMAND_UNAVAILABLE");
            if (directory.Length > 4096 || directory.Contains('\0') || !Path.IsPathFullyQualified(directory))
                throw new InvalidDataException("TERMINAL_DIRECTORY_INVALID");
            var core = NativeTerminalCoreV2.Create(100, 30, 1, 10000);
            TerminalLaunchResult launch;
            try { launch = await pty.LaunchAsync(new(command, directory, 100, 30, new Dictionary<string, string>()), token).ConfigureAwait(false); }
            catch { core.Dispose(); throw; }
            var shell = profile.Detected ? command : "custom";
            var session = new Session(launch.SessionId, shell, core) { OperationId = operationId, PtyAttached = true };
            session.View = session.View with { Title = profile.DisplayName };
            // Seed sequence one even for a shell which has not produced a prompt.
            core.ApplyAdvance(1, 0);
            session.View = session.View with { Sequence = 1 };
            // Android can attach a newly-created terminal from sequence zero.
            // The seed advance must exist in the replay log as well as the core.
            var seed = new SessionUpsertPayload { ShellType = shell, Lifecycle = "RUNNING", Columns = 100, Rows = 30 }.ToByteArray();
            session.Events.Enqueue(new(1, MessageType.SessionStarted, seed));
            session.EventBytes = seed.Length;
            sessions[launch.SessionId] = session;
            try { Persist(session); }
            catch
            {
                sessions.TryRemove(launch.SessionId, out _);
                await pty.StopAsync(launch.SessionId, CancellationToken.None).ConfigureAwait(false);
                core.Dispose(); throw;
            }
            if (operationId != Guid.Empty) launches.Add(operationId, launch.SessionId);
            session.Reader = ReadPtyAsync(session);
            return launch.SessionId;
        }
        finally { lifecycle.Release(); }
    }

    public async Task<RelayTerminalCheckpoint> CaptureAsync(Guid id, CancellationToken token)
    {
        var s = Get(id);
        await s.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (s.Deleted) throw new KeyNotFoundException("TERMINAL_SESSION_NOT_FOUND");
            var checkpoint = s.Checkpoint ?? throw new IOException("TERMINAL_CHECKPOINT_UNAVAILABLE");
            return checkpoint with { Bytes = checkpoint.Bytes.ToArray() };
        }
        finally { s.Gate.Release(); }
    }

    public async Task<IReadOnlyList<RelayTerminalEvent>?> ReadAsync(Guid id, ulong after, CancellationToken token)
    {
        var s = Get(id);
        await s.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (s.Deleted) throw new KeyNotFoundException("TERMINAL_SESSION_NOT_FOUND");
            var committed = s.Checkpoint?.Sequence ?? 0;
            if (after > committed || (after < committed && (s.Events.Count == 0 || after + 1 < s.Events.Peek().Sequence))) return null;
            var events = s.Events.Where(e => e.Sequence > after && e.Sequence <= committed).Take(64).ToArray();
            // Intermediate UTF-8/escape/DEC2026 states cannot be checkpointed by
            // mobile cores. Skip that range only with a newer committed snapshot.
            if (after < s.SnapshotRequiredThrough && committed >= s.SnapshotRequiredThrough) return null;
            if (events.Any(e => e.RequiresSnapshot)) return null;
            return events;
        }
        finally { s.Gate.Release(); }
    }

    public async Task<IReadOnlyList<RelayTerminalEvent>?> ReadHistoryAsync(Guid id, ulong after, CancellationToken token)
    {
        var s = Get(id);
        await s.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (s.Deleted) throw new KeyNotFoundException("TERMINAL_SESSION_NOT_FOUND");
            var committed = s.Checkpoint?.Sequence ?? 0;
            if (after > committed || (after < committed && (s.Events.Count == 0 || after + 1 < s.Events.Peek().Sequence))) return null;
            if (after < s.SnapshotRequiredThrough && committed >= s.SnapshotRequiredThrough) return null;
            // An incomplete UTF-8/VT token is not missing data: replay both
            // retained source fragments, withholding presentation until ready.
            // Transport Gap markers only advanced our core and are not parser
            // corruption. Expose them as the equivalent neutral core advance.
            return s.Events.Where(e => e.Sequence > after && e.Sequence <= committed).Take(64)
                .Select(e => e.Type == MessageType.Gap
                    ? new RelayTerminalEvent(e.Sequence, MessageType.SessionUpsert, new SessionUpsertPayload().ToByteArray())
                    : e with { RequiresSnapshot = false }).ToArray();
        }
        finally { s.Gate.Release(); }
    }

    public async Task<string?> InputAsync(Guid id, Guid sender, InputEventPayload input, Func<Task<bool>> authority, CancellationToken token)
    {
        var s = Get(id);
        if (input.InputId.Length != 16 || input.InputId.Span.IndexOfAnyExcept((byte)0) < 0 || input.Input.Length is < 1 or > 16384 ||
            input.LeaseId.Length != 16 || input.LeaseVersion < 1 || input.InputSeq == 0 || input.ObservedLocalInputEpoch != 0)
            return "INPUT_INVALID";
        if (input.TypedInput is { } typed && (typed.FormatVersion != 1 || (int)typed.Kind != 4 || !typed.RawBytes.Equals(input.Input))) return "INPUT_UNSUPPORTED";
        var key = sender.ToString("N") + ":" + Convert.ToHexString(input.InputId.Span);
        var hash = SHA256.HashData(input.ToByteArray());
        await s.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!await authority().ConfigureAwait(false)) return "LEASE_INVALID";
            if (s.Receipts.TryGetValue(key, out var receipt)) return CryptographicOperations.FixedTimeEquals(hash, receipt.Hash) ? receipt.Error : "INPUT_ID_CONFLICT";
            if (s.View.State != "RUNNING") return "SESSION_ENDED";
            var fence = sender.ToString("N") + ":" + Convert.ToHexString(input.LeaseId.Span) + ":" + input.LeaseVersion;
            if (s.InputSequences.TryGetValue(fence, out var sequence) && input.InputSeq <= sequence) return "INPUT_SEQUENCE_STALE";
            // Do not evict receipts and silently turn old retries into new writes.
            if (s.Receipts.Count >= 65536 || s.InputSequences.Count >= 1024) return "INPUT_RECEIPT_LIMIT";
            s.InputSequences[fence] = input.InputSeq;
            s.Receipts.Add(key, new(hash, "INPUT_WRITE_UNCERTAIN"));
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                await pty.SendInputAsync(id, input.Input.Memory, deadline.Token).ConfigureAwait(false);
                s.Receipts[key] = new(hash, null);
                return null;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                return "INPUT_WRITE_UNCERTAIN";
            }
        }
        finally { s.Gate.Release(); }
    }

    public async Task<string?> ResizeAsync(Guid id, ResizePayload resize, Func<Task<bool>> authority, CancellationToken token)
    {
        if (resize.Restore) resize = resize.Clone();
        if (resize.Restore) { resize.Columns = 100; resize.Rows = 30; }
        if (resize.Columns is < 2 or > 512 || resize.Rows is < 2 or > 256 || resize.ObservedLocalInputEpoch != 0 ||
            resize.RequestId.Length != 16 || resize.ExpiresAtMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return "RESIZE_INVALID";
        var s = Get(id);
        await s.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!await authority().ConfigureAwait(false)) return "LEASE_INVALID";
            if (s.View.State != "RUNNING") return "SESSION_ENDED";
            if (s.View.Columns == resize.Columns && s.View.Rows == resize.Rows) return null;
            await pty.ResizeAsync(id, (int)resize.Columns, (int)resize.Rows, token).ConfigureAwait(false);
            var seq = s.View.Sequence + 1;
            s.Core.ApplyResize(seq, resize.Columns, resize.Rows);
            s.View = s.View with { Columns = (int)resize.Columns, Rows = (int)resize.Rows };
            // Lease/request fields are not part of the shared ordered resize event.
            Add(s, MessageType.ResizeApplied, new ResizePayload { Columns = resize.Columns, Rows = resize.Rows }.ToByteArray());
            return null;
        }
        finally { s.Gate.Release(); }
    }

    public async Task RecordInputResultAsync(Guid id, Guid recipient, InputResultPayload result, CancellationToken token)
    {
        var s = Get(id);
        await s.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (s.Deleted) throw new KeyNotFoundException("TERMINAL_SESSION_NOT_FOUND");
            s.Core.ApplyAdvance(s.View.Sequence + 1, s.Core.GetPresentationInfo().LogicalTimeMicros);
            Add(s, result.Accepted ? MessageType.InputAccepted : MessageType.InputRejected, result.ToByteArray(), recipient);
        }
        finally { s.Gate.Release(); }
    }
    public async Task<RelayTerminalEvent?> RecoverAsync(Guid id, Guid recipient, ulong after, CancellationToken token)
    {
        var s = Get(id);
        await s.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (s.Deleted) throw new KeyNotFoundException("TERMINAL_SESSION_NOT_FOUND");
            if (s.Checkpoint?.Sequence != s.View.Sequence || after >= s.View.Sequence) return null;
            s.Core.ApplyAdvance(s.View.Sequence + 1, s.Core.GetPresentationInfo().LogicalTimeMicros);
            Add(s, MessageType.Gap, new GapPayload { Start = after + 1, End = s.View.Sequence, ReasonCode = "HISTORY_GAP" }.ToByteArray(), recipient);
            return s.Events.Last();
        }
        finally { s.Gate.Release(); }
    }
    public async Task StopAsync(Guid id, CancellationToken token)
    {
        var s = Get(id);
        await s.StopGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (s.Deleted) return;
            s.Cancellation.Cancel();
            try { await s.Reader.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await pty.StopAsync(id, token).ConfigureAwait(false);
            s.PtyAttached = false;
            await EndAsync(s, "EXITED").ConfigureAwait(false);
        }
        finally { s.StopGate.Release(); }
    }

    private async Task ReadPtyAsync(Session s)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, s.Cancellation.Token);
        var token = linked.Token;
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var count = await pty.ReadOutputAsync(s.View.Id, buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                await s.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var seq = s.View.Sequence + 1;
                    var effects = s.Core.ApplyOutputWithHostEffectsV2(seq, buffer.AsSpan(0, count));
                    Add(s, MessageType.OutputChunk, new OutputChunkPayload { Output = ByteString.CopyFrom(buffer, 0, count), SourceEventSeq = seq,
                        PresentationRevision = 0 }.ToByteArray());
                    foreach (var effect in effects.Records)
                        if (effect.Kind == NativeTerminalHostEffectKindV2.PtyReply)
                            {
                            using var replyDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            replyDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                            await pty.SendInputAsync(s.View.Id, effect.Payload.ToArray(), replyDeadline.Token).ConfigureAwait(false);
                        }
                }
                finally { s.Gate.Release(); }
            }
            await EndAsync(s, "EXITED").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            try { await pty.StopAsync(s.View.Id, CancellationToken.None).ConfigureAwait(false); }
            finally { await EndAsync(s, "INTERRUPTED").ConfigureAwait(false); }
        }
    }

    private async Task EndAsync(Session s, string state)
    {
        await s.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (s.View.State != "RUNNING") return;
            s.Core.ApplyAdvance(s.View.Sequence + 1, s.Core.GetPresentationInfo().LogicalTimeMicros);
            s.View = s.View with { State = state };
            Add(s, state == "EXITED" ? MessageType.SessionEnded : MessageType.SessionInterrupted, new SessionUpsertPayload {
                ShellType = s.View.Shell, Title = s.View.Title, Lifecycle = state, Columns = (uint)s.View.Columns, Rows = (uint)s.View.Rows }.ToByteArray());
        }
        finally { s.Gate.Release(); }
    }

    private void Add(Session s, MessageType type, byte[] payload, Guid? recipient = null)
    {
        s.View = s.View with { Sequence = s.View.Sequence + 1, ActivityAt = DateTimeOffset.UtcNow };
        var eligible = Persist(s);
        s.Events.Enqueue(new(s.View.Sequence, type, payload, !eligible, recipient)); s.EventBytes += payload.Length;
        while (s.Events.Count > 4096 || s.EventBytes > 8 * 1024 * 1024) s.EventBytes -= s.Events.Dequeue().Payload.Length;
    }
    private async Task AdvanceClockAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
        {
            foreach (var s in sessions.Values)
            {
                await s.Gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (s.View.State != "RUNNING" || s.DeferredAt is null || s.DeferredAt > DateTimeOffset.UtcNow.AddMilliseconds(-250)) continue;
                    var info = s.Core.GetPresentationInfo();
                    if (info.SynchronizedOutputDeadlineMicros == 0) continue;
                    s.Core.ApplyAdvance(s.View.Sequence + 1, info.SynchronizedOutputDeadlineMicros);
                    // A native clock expiry has no raw Output equivalent. Recover
                    // via a committed checkpoint instead of fabricating PTY bytes.
                    var payload = new SessionUpsertPayload { ShellType = s.View.Shell, Lifecycle = s.View.State, Columns = (uint)s.View.Columns, Rows = (uint)s.View.Rows }.ToByteArray();
                    Add(s, MessageType.SessionUpsert, payload);
                    var last = s.Events.Last();
                    // All replay spanning this clock event needs the snapshot.
                    s.SnapshotRequiredThrough = last.Sequence;
                }
                finally { s.Gate.Release(); }
            }
        }
    }
    private sealed record StoredState(int Version, RelayTerminalSession Session, Guid OperationId, byte[] Snapshot);
    private byte[] StateAad(Guid id) => Encoding.UTF8.GetBytes("TERMLINK-MAC-CLI-STATE-V1:" + stateDirectory + ":" + id.ToString("N"));
    private bool Persist(Session s)
    {
        if (!s.Core.TryCaptureCheckpoint(out var checkpoint, out _))
        {
            s.DeferredAt ??= DateTimeOffset.UtcNow;
            return false;
        }
        using var capture = checkpoint;
        var raw = capture.ExportSnapshot();
        if (capture.Info.NextSourceSequence != s.View.Sequence + 1) throw new IOException("TERMINAL_SEQUENCE_INVALID");
        if (s.Checkpoint is { } old) CryptographicOperations.ZeroMemory(old.Bytes);
        s.Checkpoint = new(s.View.Sequence, (uint)s.View.Columns, (uint)s.View.Rows, raw.ToArray());
        s.DeferredAt = null;
        if (stateDirectory is null || stateKey is null) { CryptographicOperations.ZeroMemory(raw); return true; }
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        byte[]? plain = null;
        try
        {
            plain = JsonSerializer.SerializeToUtf8Bytes(new StoredState(1, s.View, s.OperationId, raw));
            if (plain.Length > 24 * 1024 * 1024) throw new IOException("TERMINAL_STATE_LIMIT");
            var blob = new byte[12 + 16 + plain.Length];
            RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
            using (var aes = new AesGcm(stateKey, 16)) aes.Encrypt(blob.AsSpan(0, 12), plain, blob.AsSpan(28), blob.AsSpan(12, 16), StateAad(s.View.Id));
            var destination = Path.Combine(stateDirectory, s.View.Id.ToString("N") + ".state");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.WriteThrough }))
                { stream.Write(blob); stream.Flush(flushToDisk: true); }
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(raw); if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
        return true;
    }
    private void Restore()
    {
        foreach (var path in Directory.EnumerateFiles(stateDirectory!, "*.state").Take(64))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) || new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length > 24 * 1024 * 1024 + 28)
                throw new IOException("TERMINAL_STATE_INVALID");
            if (deletions?.Contains(id) == true) { deletions.RemoveStateFile(id); continue; }
            var blob = File.ReadAllBytes(path);
            if (blob.Length < 28) throw new IOException("TERMINAL_STATE_INVALID");
            var plain = new byte[blob.Length - 28];
            try
            {
                using (var aes = new AesGcm(stateKey!, 16)) aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, StateAad(id));
                var stored = JsonSerializer.Deserialize<StoredState>(plain) ?? throw new IOException("TERMINAL_STATE_INVALID");
                if (stored.Version != 1 || stored.Session.Id != id || stored.Session.Columns is < 2 or > 512 || stored.Session.Rows is < 2 or > 512)
                    throw new IOException("TERMINAL_STATE_INVALID");
                var core = NativeTerminalCoreV2.ImportSnapshot(stored.Snapshot);
                CryptographicOperations.ZeroMemory(stored.Snapshot);
                if (core.GetPresentationInfo().NextSourceSequence != stored.Session.Sequence + 1) { core.Dispose(); throw new IOException("TERMINAL_STATE_INVALID"); }
                var session = new Session(id, stored.Session.Shell, core) { View = stored.Session, OperationId = stored.OperationId };
                if (session.View.State == "RUNNING")
                {
                    // A restarted Agent never guesses whether a persisted PID is still its child.
                    core.ApplyAdvance(session.View.Sequence + 1, 0);
                    session.View = session.View with { State = "INTERRUPTED", Sequence = session.View.Sequence + 1 };
                }
                sessions[id] = session;
                if (session.OperationId != Guid.Empty) launches.Add(session.OperationId, id);
                Persist(session);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
    private Session Get(Guid id) => sessions.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException("TERMINAL_SESSION_NOT_FOUND");

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            foreach (var s in sessions.Values) await StopAsync(s.View.Id, CancellationToken.None).ConfigureAwait(false);
            lifetime.Cancel();
            try { await clock.ConfigureAwait(false); } catch (OperationCanceledException) { }
            foreach (var s in sessions.Values) { s.Core.Dispose(); if (s.Checkpoint is { } checkpoint) CryptographicOperations.ZeroMemory(checkpoint.Bytes); }
            deletions?.Dispose();
            if (stateKey is not null) CryptographicOperations.ZeroMemory(stateKey);
        }
        finally { lifecycle.Release(); }
    }
    private sealed record Receipt(byte[] Hash, string? Error);
    private sealed class Session
    {
        public Session(Guid id, string shell, NativeTerminal core)
        {
            Core = core;
            View = new(id, shell, shell, "RUNNING", 100, 30, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
        public RelayTerminalSession View;
        public bool Deleted, PtyAttached;
        public NativeTerminal Core;
        public Guid OperationId;
        public RelayTerminalCheckpoint? Checkpoint;
        public DateTimeOffset? DeferredAt;
        public ulong SnapshotRequiredThrough;
        public readonly SemaphoreSlim Gate = new(1, 1), StopGate = new(1, 1);
        public readonly CancellationTokenSource Cancellation = new();
        public readonly Queue<RelayTerminalEvent> Events = new();
        public readonly Dictionary<string, Receipt> Receipts = new();
        public readonly Dictionary<string, ulong> InputSequences = new();
        public int EventBytes;
        public Task Reader = Task.CompletedTask;
    }
}
