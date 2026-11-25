// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using TermLink.Agent.Connection;
using TermLink.Agent.Terminal;
using TermLink.Platform;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

internal static class FailedReaderValidation
{
    public static async Task RunAsync(string root)
    {
        foreach (var persisted in new[] { false, true })
        foreach (var partial in new[] { false, true })
            await CheckAsync(root, persisted, partial, false);
        await CheckAsync(root, false, true, true);
        await CheckAsync(root, true, true, true);
        await CheckAsync(root, false, true, false, 1);
        await CheckAsync(root, true, true, false, 1);
        await BlockedParserStopAsync(root);
        Console.WriteLine("PASS failed reader: unknown mode and partial tail retain original native diagnostic, last committed snapshot, unchanged published events and interrupted lifecycle without an invented exit code");
        Console.WriteLine("PASS failed reader cleanup: persistent/nonpersistent PTYs stop once then disposal succeeds immediately; failed PTY stop retains its native core/key until actual retry success");
    }

    private static async Task CheckAsync(string root, bool persisted, bool partial, bool stopFails, int? initialStopFailures = null)
    {
        var pty = new ParserPty { FailStops = initialStopFailures ?? (stopFails ? 2 : 0) };
        var directory = persisted ? Path.Combine(root, "failed-" + Guid.NewGuid().ToString("N")) : null;
        var key = persisted ? RandomNumberGenerator.GetBytes(32) : null;
        var host = new PortableTerminalHost(pty, [new("sh", "Owned parser fixture", "/bin/sh", [], true, false)], directory, key);
        var id = await host.StartAsync("sh", root, Guid.NewGuid(), CancellationToken.None);
        var session = Session(host, id);
        var reader = (Task)Get(session, "Reader")!;
        var core = (NativeTerminal)Get(session, "Core")!;
        pty.Output.Writer.TryWrite("SAFE_FRAME"u8.ToArray());
        await Until(async () => (await host.CaptureAsync(id, CancellationToken.None)).Sequence == 2);
        var checkpoint = await host.CaptureAsync(id, CancellationToken.None);
        var published = (await host.ReadAsync(id, 0, CancellationToken.None))!.Select(item => item with { Payload = item.Payload.ToArray() }).ToArray();
        var stateFile = directory is null ? null : Path.Combine(directory, id.ToString("N") + ".state");
        var storedHash = stateFile is null ? null : SHA256.HashData(await File.ReadAllBytesAsync(stateFile));
        if (partial)
        {
            pty.Output.Writer.TryWrite("\x1b[?"u8.ToArray());
            await Until(() => Task.FromResult(host.Sessions.Single().Sequence == 3));
            Require((await host.ReadAsync(id, checkpoint.Sequence, CancellationToken.None))!.Count == 0 &&
                (await host.ReadHistoryAsync(id, checkpoint.Sequence, CancellationToken.None))!.Count == 0,
                "The tail selected for neutral projection was exposed before failure.");
        }
        pty.Output.Writer.TryWrite(partial ? "9999h"u8.ToArray() : "\x1b[?9999h"u8.ToArray());
        await Until(() => Task.FromResult(reader.IsCompleted));
        var failure = Get(session, "ReaderFailure") as NativeTerminalException;
        Require(failure?.Status == NativeTerminalStatus.Unsupported && failure.Diagnostic?.Contains("rejected_parameter=9999\n", StringComparison.Ordinal) == true,
            "The first native failure/diagnostic was replaced by a cleanup FailedClosed exception.");
        Require((core.GetPresentationInfo().Flags & NativeTerminalPresentationFlags.FailedClosed) != 0,
            "The native core was incorrectly recovered after the unsupported mode.");
        await ExpectAsync<NativeTerminalException>(() => Task.Run(() => core.CaptureCheckpoint()));
        var frozen = await host.CaptureAsync(id, CancellationToken.None);
        Require(frozen.Sequence == checkpoint.Sequence && frozen.Bytes.SequenceEqual(checkpoint.Bytes), "Failed core published a new snapshot.");
        var all = (await host.ReadHistoryAsync(id, 0, CancellationToken.None))!;
        foreach (var item in published)
        {
            var current = all.Single(value => value.Sequence == item.Sequence);
            Require(current.Type == item.Type && current.Payload.SequenceEqual(item.Payload), "A previously committed/published event was changed.");
        }
        var tail = (await host.ReadAsync(id, checkpoint.Sequence, CancellationToken.None))!;
        Require(tail.Count == (partial ? 2 : 1) && tail[0].Sequence == checkpoint.Sequence + 1 &&
            tail.Zip(tail.Skip(1), (a, b) => b.Sequence == a.Sequence + 1).All(value => value), "Failure exit has a gap or rolled back its source sequence.");
        if (partial) Require(tail[0].Type == MessageType.SessionUpsert && tail[0].Payload.Length == 0 && !tail[0].RequiresSnapshot,
            "Uncommitted partial output was exposed instead of freezing the prior valid frame.");
        var ended = tail[^1]; var payload = SessionUpsertPayload.Parser.ParseFrom(ended.Payload);
        Require(ended.Type == MessageType.SessionEnded && payload.Lifecycle == "INTERRUPTED" && !payload.HasExitCode &&
            payload.HasTerminalProtocolDiagnostic && payload.TerminalProtocolDiagnostic == failure!.Diagnostic && payload.TerminalProtocolDiagnostic.Length <= 8192 &&
            payload.TerminalProtocolDiagnostic.All(c => c == '\n' || c is >= (char)32 and <= (char)126) && host.Sessions.Single().State == "INTERRUPTED",
            "The failed parser was not visible as an interrupted SessionEnded with bounded escaped native evidence and no invented exit code.");
        if (stateFile is not null) Require(storedHash!.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(stateFile))), "Failed native state was persisted as a valid checkpoint.");
        Require(await host.RecoverAsync(id, Guid.NewGuid(), checkpoint.Sequence, CancellationToken.None) is null, "Failed core attempted a native recovery advance.");
        await ExpectAsync<IOException>(() => host.RecordInputResultAsync(id, Guid.NewGuid(), new InputResultPayload(), CancellationToken.None));
        Require(host.Sessions.Single().Sequence == ended.Sequence, "Failed native input receipt changed the authoritative source sequence.");
        await CheckProjectionAndSealingAsync(host, id, checkpoint.Sequence, ended);

        if (stopFails)
        {
            Require(reader.IsFaulted && pty.Open, "Fixture did not exercise an actually faulted reader plus an open PTY.");
            await ExpectAsync<IOException>(() => host.DisposeAsync().AsTask());
            Require(pty.Open && !(bool)Get(core, "disposed")! && host.Sessions.Count == 1 &&
                (key is null || ((byte[])Get(host, "stateKey")!).Any(b => b != 0)), "Failed PTY stop lost the native owner or retry key.");
            await host.DisposeAsync();
            Require(pty.Stops == 3, "Faulted reader prevented the actual PTY retry.");
        }
        else if (initialStopFailures == 1)
        {
            Require(reader.IsFaulted && pty.Open, "Fixture did not leave a faulted reader after its first stop failure.");
            await host.DisposeAsync();
            Require(pty.Stops == 2, "Historical reader failure prevented disposal from succeeding after the actual PTY retry.");
        }
        else
        {
            Require(!reader.IsFaulted && !pty.Open && pty.Stops == 1, "Parser error did not stop its PTY exactly once.");
            await host.StopAsync(id, CancellationToken.None);
            await host.DisposeAsync();
            Require(pty.Stops == 1, "Historical parser failure required a second successful PTY stop.");
        }
        await host.DisposeAsync();
        Require(!pty.Open && host.Sessions.Count == 0 && (bool)Get(core, "disposed")!, "Completed failure cleanup did not release the native core.");
        if (key is not null) { Require(((byte[])Get(host, "stateKey")!).All(b => b == 0), "Completed failure cleanup retained its state key."); CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task BlockedParserStopAsync(string root)
    {
        var pty = new ParserPty { StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var host = new PortableTerminalHost(pty, [new("sh", "Owned slow-stop fixture", "/bin/sh", [], true, false)]);
        var id = await host.StartAsync("sh", root, Guid.NewGuid(), CancellationToken.None);
        var session = Session(host, id); var core = (NativeTerminal)Get(session, "Core")!;
        pty.Output.Writer.TryWrite("SAFE"u8.ToArray());
        await Until(async () => (await host.CaptureAsync(id, CancellationToken.None)).Sequence == 2);
        pty.Output.Writer.TryWrite("\x1b[?"u8.ToArray());
        await Until(() => Task.FromResult(host.Sessions.Single().Sequence == 3));
        pty.Output.Writer.TryWrite("9999h"u8.ToArray());
        await pty.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var clock = (Task)Get(host, "clock")!;
        await Task.Delay(350);
        Require(!clock.IsCompleted && !clock.IsFaulted &&
            (core.GetPresentationInfo().Flags & NativeTerminalPresentationFlags.FailedClosed) != 0,
            "The clock tried to advance a failed core while its PTY was still stopping.");
        var input = new InputEventPayload { InputId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), Input = ByteString.CopyFromUtf8("must-not-write"),
            LeaseId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), LeaseVersion = 1, InputSeq = 1 };
        Require(await host.InputAsync(id, Guid.NewGuid(), input, () => Task.FromResult(true), CancellationToken.None) == "SESSION_ENDED",
            "A failed reader allowed new PTY input while stop was pending.");
        Require(await host.RecoverAsync(id, Guid.NewGuid(), 0, CancellationToken.None) is null, "Pending failed reader attempted native recovery.");
        await ExpectAsync<IOException>(() => host.RecordInputResultAsync(id, Guid.NewGuid(), new InputResultPayload(), CancellationToken.None));
        var cleanup = host.DisposeAsync().AsTask();
        await Task.Delay(30);
        Require(!cleanup.IsCompleted && pty.Open, "Disposal claimed completion before the blocked PTY stop finished.");
        pty.StopGate.SetResult();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(3));
        Require(!pty.Open && pty.Stops == 1 && host.Sessions.Count == 0 && (bool)Get(core, "disposed")!,
            "First disposal did not release the stopped failed core.");
        Console.WriteLine("PASS failed reader slow stop: clock skips native FailedClosed, input stays fenced, disposal waits and succeeds once the actual PTY stop completes");
    }

    private static async Task CheckProjectionAndSealingAsync(PortableTerminalHost host, Guid id, ulong after, RelayTerminalEvent ended)
    {
        var planeType = typeof(RelayConnectionService).GetNestedType("TerminalPlane", BindingFlags.NonPublic)!;
        var request = new HostHistoryRequestPayload { AfterSeq = after, ThroughSeq = ended.Sequence };
        var selection = (Task)planeType.GetMethod("SelectEventRangeAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [host, id, Guid.NewGuid(), request, CancellationToken.None])!;
        await selection;
        var result = selection.GetType().GetProperty("Result")!.GetValue(selection)!;
        var events = (IEnumerable<HistoryEventV1>)result.GetType().GetProperty("Events")!.GetValue(result)!;
        var history = SessionUpsertPayload.Parser.ParseFrom(events.Last().Payload);
        Require(!history.HasTerminalProtocolDiagnostic && !history.HasExitCode && history.Lifecycle == "INTERRUPTED",
            "Native diagnostic escaped the recipient-specific SessionEnded contract into a history object.");
        Require(SessionUpsertPayload.Parser.ParseFrom((await host.ReadHistoryAsync(id, after, CancellationToken.None))!.Last().Payload).HasTerminalProtocolDiagnostic,
            "History projection mutated the live source event.");

        var deviceId = Guid.NewGuid(); var recipient = Guid.NewGuid();
        var retainedType = typeof(RelayConnectionService).GetNestedType("RetainedIdentity", BindingFlags.NonPublic)!;
        var retained = Activator.CreateInstance(retainedType, [deviceId, Guid.NewGuid(), "owned", "unused", 1L])!;
        var accountType = typeof(RelayConnectionService).GetNestedType("Account", BindingFlags.NonPublic)!;
        var account = Activator.CreateInstance(accountType, ["https://unused.invalid", "https://unused.invalid", "owned", "owned", "", "", "owned",
            retained, "unused", "unused", DateTimeOffset.UtcNow.AddHours(1), true])!;
        byte[]? wire = null;
        Func<byte[], WebSocketMessageType, Task> send = (bytes, _) => { wire = bytes; return Task.CompletedTask; };
        using var device = DeviceIdentity.Generate();
        var plane = planeType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke([null, account, device, host, send, CancellationToken.None]);
        var pairwise = RandomNumberGenerator.GetBytes(32); var keyId = Encoding.ASCII.GetBytes("epoch-00000001");
        var sendEnvelope = planeType.GetMethod("SendEnvelopeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)sendEnvelope.Invoke(plane, [recipient, id, pairwise, keyId, MessageType.SessionEnded, ended.Payload, ended.Sequence])!;
        var envelope = Envelope.Parser.ParseFrom(wire);
        var sessionKey = SessionCrypto.DeriveSessionKey(pairwise, id, CryptoDirection.PcToAndroid, keyId);
        try
        {
            Require(envelope.MessageType == MessageType.SessionEnded && envelope.RecipientDeviceId.Span.SequenceEqual(Convert.FromHexString(recipient.ToString("N"))) &&
                !Encoding.UTF8.GetString(wire!).Contains("TL-DIAG/", StringComparison.Ordinal), "Diagnostic was not in a recipient-sealed SessionEnded envelope.");
            var clear = SessionCrypto.Decrypt(sessionKey, envelope.Nonce.ToByteArray(), SessionCrypto.AssociatedData(envelope), envelope.Ciphertext.ToByteArray());
            Require(clear.SequenceEqual(ended.Payload), "Recipient could not recover the unchanged native diagnostic.");
            var original = wire!.ToArray();
            await (Task)sendEnvelope.Invoke(plane, [recipient, id, pairwise, keyId, MessageType.SessionEnded, ended.Payload, ended.Sequence])!;
            Require(wire!.SequenceEqual(original), "Same-recipient diagnostic replay changed ciphertext.");
        }
        finally { CryptographicOperations.ZeroMemory(sessionKey); CryptographicOperations.ZeroMemory(pairwise); await ((IAsyncDisposable)plane).DisposeAsync(); }
    }

    private static object Session(object host, Guid id)
    {
        var pair = ((IEnumerable)Get(host, "sessions")!).Cast<object>().Single(item => (Guid)item.GetType().GetProperty("Key")!.GetValue(item)! == id);
        return pair.GetType().GetProperty("Value")!.GetValue(pair)!;
    }
    private static object? Get(object owner, string name) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(owner);
    private static async Task Until(Func<Task<bool>> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!await condition()) await Task.Delay(10, timeout.Token); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

    private sealed class ParserPty : ITerminalSessionHost
    {
        public readonly Channel<byte[]> Output = Channel.CreateUnbounded<byte[]>();
        public bool Open = true; public int Stops, FailStops;
        public TaskCompletionSource? StopGate;
        public readonly TaskCompletionSource StopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken token) => ValueTask.FromResult(new TerminalLaunchResult(Guid.NewGuid(), 0, "sh"));
        public async ValueTask<int> ReadOutputAsync(Guid id, Memory<byte> buffer, CancellationToken token)
        { try { var data = await Output.Reader.ReadAsync(token); data.CopyTo(buffer); return data.Length; } catch (ChannelClosedException) { return 0; } }
        public async ValueTask StopAsync(Guid id, CancellationToken token)
        { Stops++; StopStarted.TrySetResult(); if (StopGate is { } gate) await gate.Task; if (FailStops-- > 0) throw new IOException("Owned PTY stop failure"); Open = false; Output.Writer.TryComplete(); }
        public ValueTask ResizeAsync(Guid id, int c, int r, CancellationToken t) => throw new NotSupportedException();
        public ValueTask SendInputAsync(Guid id, ReadOnlyMemory<byte> b, CancellationToken t) => throw new NotSupportedException();
        public ValueTask SignalAsync(Guid id, TerminalSignal s, CancellationToken t) => throw new NotSupportedException();
        public ValueTask<int> WaitForExitAsync(Guid id, CancellationToken t) => throw new NotSupportedException();
    }
}
