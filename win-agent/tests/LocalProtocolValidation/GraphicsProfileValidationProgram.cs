using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.TerminalCore;

namespace TermLink.Agent;

internal static class GraphicsProfileValidationProgram
{
    private static int checks;

    public static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "termlink-profile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        BrokerRootCodec();
        EventBufferProfiles(root);
        Unicode16JournalProfile(root);
        StoreProfiles(root);
        BrokerCommitRequiresRuntime(root);
        BrokerRecoveryBeforeLifecycle(root);
        DeferredRegistryRecovery(root);
        QuarantinedRegistryRecovery(root);
        BrokerPersistenceRetryValidation.Run(root);
        RegistryFirstFrame(root);
        Handshake(root, TerminalSemanticProfile.Graphics, null, "graphics", true);
        Handshake(root, TerminalSemanticProfile.GraphicsUnicode16, null, "graphics-unicode16", true);
        Handshake(root, TerminalSemanticProfile.GraphicsUnicode16, TerminalSemanticProfile.Graphics, "width-downgrade", false);
        Handshake(root, TerminalSemanticProfile.Graphics, TerminalSemanticProfile.GraphicsUnicode16, "width-upgrade", false);
        Handshake(root, TerminalSemanticProfile.Legacy, TerminalSemanticProfile.Graphics, "downgrade", false);
        Handshake(root, TerminalSemanticProfile.Graphics, TerminalSemanticProfile.Legacy, "upgrade", false);
        Handshake(root, TerminalSemanticProfile.Graphics, null, "bad-token", false, wrongToken: true);
        Handshake(root, TerminalSemanticProfile.Graphics, null, "bad-pid", false, wrongPid: true);
        if (args.Contains("--native-graphics")) NativeOutboxProfile(root);
        Console.WriteLine("PASS persisted graphics profile: " + checks + " assertions; fixtures: " + root);
        return 0;
    }

    private static void BrokerRootCodec()
    {
        var path = @"C:\TermLink\Data\History\broker-captures";
        Require(LocalPayloadCodec.ParseBrokerCaptureRoot(LocalPayloadCodec.BrokerCaptureRoot(path)) == path,
            "Broker root UTF-8 roundtrip changed the local path.");
        Require(LocalPayloadCodec.ParseBrokerCaptureRoot(LocalPayloadCodec.BrokerCaptureRoot(@"C:\")) == @"C:\",
            "Broker volume root was rejected.");
        foreach (var bad in new[] { @"\\server\share", @"C:\a\..\b", @"C:\a:stream", @"C:\bad. ",
            @"C:\bad?", @"C:\a\\b", @"C:\a\", "C:\\a\tb", "", "relative" })
        {
            try { LocalPayloadCodec.BrokerCaptureRoot(bad); throw new Exception("Broker root accepted " + bad); }
            catch (InvalidDataException) { checks++; }
        }
        try { LocalPayloadCodec.ParseBrokerCaptureRoot(new byte[] { 0xc3, 0x28 });
            throw new Exception("Broker root accepted malformed UTF-8."); }
        catch (InvalidDataException) { checks++; }
        checks++;
    }

    private static void EventBufferProfiles(string root)
    {
        var session = Guid.NewGuid();
        var directory = Path.Combine(root, "journal");
        using (var buffer = new EncryptedEventBuffer(directory, session, 1024 * 1024))
        {
            Require(buffer.BoundProfile == null && !buffer.HasBoundProfile, "empty journal is unbound");
            buffer.BindAuthenticatedProfile(TerminalSemanticProfile.Graphics);
            Require(buffer.BoundProfile == TerminalSemanticProfile.Graphics, "journal caches actual profile");
            buffer.Append(Output(session, 1));
            var before = Fingerprint(directory);
            Reject(TerminalSessionProfile.Mismatch, () => buffer.BindAuthenticatedProfile(TerminalSemanticProfile.Legacy));
            Require(Fingerprint(directory) == before, "cached mismatch preserved journal");
        }
        var persisted = Fingerprint(directory);
        using (var buffer = new EncryptedEventBuffer(directory, session, 1024 * 1024))
        {
            Require(buffer.BoundProfile == TerminalSemanticProfile.Graphics && buffer.HasBoundProfile, "restart reads graphics marker");
            buffer.BindAuthenticatedProfile(TerminalSemanticProfile.Graphics);
            Require(buffer.ReadFrom(0, out _, out _).Count == 1, "graphics journal replay");
            Reject(TerminalSessionProfile.Mismatch, () => buffer.BindAuthenticatedProfile());
        }
        Require(Fingerprint(directory) == persisted, "restart never rewrites profile");
        var legacyDirectory = Path.Combine(root, "unmarked-journal");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "preserve.txt"), "old producer state");
        using var unmarked = new EncryptedEventBuffer(legacyDirectory, Guid.NewGuid(), 1024 * 1024);
        Reject(TerminalSessionProfile.Missing, () => unmarked.BindAuthenticatedProfile(TerminalSemanticProfile.Graphics));
        Require(!File.Exists(Path.Combine(legacyDirectory, TerminalSessionProfile.FileName)), "nonempty journal cannot be stamped");
    }

    private static void Unicode16JournalProfile(string root)
    {
        var session = Guid.NewGuid();
        var directory = Path.Combine(root, "journal-unicode16");
        using (var buffer = new EncryptedEventBuffer(directory, session, 1024 * 1024))
        {
            buffer.BindAuthenticatedProfile(TerminalSemanticProfile.GraphicsUnicode16);
            buffer.Append(Output(session, 1));
        }
        using (var reopened = new EncryptedEventBuffer(directory, session, 1024 * 1024))
        {
            Require(reopened.BoundProfile == TerminalSemanticProfile.GraphicsUnicode16 &&
                reopened.ReadFrom(0, out _, out _).Count == 1,
                "Unicode16 journal did not retain its exact profile and frame.");
            Reject(TerminalSessionProfile.Mismatch,
                () => reopened.BindAuthenticatedProfile(TerminalSemanticProfile.Graphics));
        }
    }

    private static void StoreProfiles(string root)
    {
        var device = Guid.NewGuid();
        var session = Guid.NewGuid();
        var storeRoot = Path.Combine(root, "history");
        TerminalSemanticProfile authenticated = null;
        var store = new ProductionTerminalHistoryStore(storeRoot, device, _ => authenticated);
        Reject(TerminalSessionProfile.Missing, () => store.AppendCommittedFrame(Output(session, 1)));
        Reject(TerminalSessionProfile.Missing, () => store.GetSessionProfile(session));
        Require(!File.Exists(Path.Combine(storeRoot, session.ToString("N"), TerminalSessionProfile.FileName)), "unknown provider creates no marker");
        authenticated = TerminalSemanticProfile.Graphics;
        store.AppendCommittedFrame(Output(session, 1));
        Require(store.GetSessionProfile(session) == TerminalSemanticProfile.Graphics, "history persists authenticated graphics profile");
        var directory = Path.Combine(storeRoot, session.ToString("N"));
        var before = Fingerprint(directory);
        authenticated = TerminalSemanticProfile.Legacy;
        Reject(TerminalSessionProfile.Mismatch, () => store.ReadFrames(session));
        Reject(TerminalSessionProfile.Mismatch, () => store.AppendCommittedFrame(Output(session, 2)));
        Reject(TerminalSessionProfile.Mismatch, () => store.GetSessionProfile(session));
        Require(Fingerprint(directory) == before, "cached provider mismatch preserved history");
        var mismatched = new ProductionTerminalHistoryStore(storeRoot, device, _ => TerminalSemanticProfile.Legacy);
        Reject(TerminalSessionProfile.Mismatch, () => mismatched.GetSessionProfile(session));
        Require(Fingerprint(directory) == before, "restart provider mismatch preserved history");
        var restored = new ProductionTerminalHistoryStore(storeRoot, device, _ => null);
        Require(restored.GetSessionProfile(session) == TerminalSemanticProfile.Graphics && restored.ReadFrames(session).Count == 1,
            "known persisted profile survives unknown live provider");
        var noProvider = new ProductionTerminalHistoryStore(storeRoot, device);
        Require(noProvider.GetSessionProfile(session) == TerminalSemanticProfile.Graphics, "no provider still reads actual marker");
        var legacy = Guid.NewGuid();
        noProvider.AppendCommittedFrame(Output(legacy, 1));
        Require(noProvider.GetSessionProfile(legacy) == TerminalSemanticProfile.Legacy, "no provider legacy test default");
        var orphan = Guid.NewGuid();
        var orphanDirectory = Path.Combine(storeRoot, orphan.ToString("N"));
        Directory.CreateDirectory(orphanDirectory);
        File.WriteAllText(Path.Combine(orphanDirectory, "old-state.bin"), "preserved");
        var orphanBefore = Fingerprint(orphanDirectory);
        Reject(TerminalSessionProfile.Missing, () => new ProductionTerminalHistoryStore(storeRoot, device,
            _ => TerminalSemanticProfile.Graphics).AppendCommittedFrame(Output(orphan, 1)));
        Require(Fingerprint(orphanDirectory) == orphanBefore, "unmarked old history stays untouched");
        Reject(TerminalSessionProfile.Mismatch, () => restored.SaveOutbox(Outbox(session, false, new byte[] { 1 })));
        Reject(TerminalSessionProfile.Mismatch, () => noProvider.SaveOutbox(Outbox(legacy, true, new byte[] { 1 })));
        using var registry = new SessionRegistry(Path.Combine(root, "registry"), 10000, 1024 * 1024, 5);
        Require(registry.GetSessionProfile(Guid.NewGuid()) == null, "unknown registry session has no profile");
    }

    private static void BrokerCommitRequiresRuntime(string root)
    {
        var directory = Path.Combine(root, "no-runtime-registry");
        var options = RegistryServiceOptions.Parse(new[]
        {
            "--registry", "--control-pipe", "termlink-no-runtime-" + Guid.NewGuid().ToString("N"),
            "--buffer-root", directory
        });
        using var registry = new SessionRegistry(directory, 10000, 1024 * 1024, 5);
        using var service = new SessionRegistryService(options, registry);
        var forward = typeof(SessionRegistryService).GetMethod("ForwardCommittedFrame",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("Production commit callback is missing.");
        var commit = (Action<ManagedShellType, LocalFrame>)forward.CreateDelegate(
            typeof(Action<ManagedShellType, LocalFrame>), service);
        var session = Guid.NewGuid();
        commit(default, Output(session, 1));
        checks++;
        try
        {
            commit(default, new LocalFrame(LocalFrameType.BrokerOutput, session, 2, Array.Empty<byte>()));
            throw new Exception("Broker commit without a production runtime must not permit ACK.");
        }
        catch (InvalidOperationException error) when (error.Message == "BROKER_CAPTURE_RUNTIME_UNAVAILABLE")
        {
            checks++;
        }
        commit(default, Output(session, 3));
        checks++;
    }

    private static LocalFrame InlineBroker(Guid session)
    {
        var identity = new byte[96];
        BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(0, 4), NativeTerminalCoreV2.AbiVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(4, 4), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(12, 4), 'f');
        identity[16] = 1;
        identity[24] = 1;
        identity[32] = 1;
        using var record = NativeTerminalBrokerRecordV1.FromStored(identity,
            (uint)NativeTerminalBrokerFailure.Denied, Array.Empty<byte>());
        var canonical = NativeTerminalBrokerCaptureCodecV1.Encode(1, new[] { record });
        using var reference = NativeTerminalBrokerOutputReferenceV1.InlineFailures(1,
            Encoding.ASCII.GetBytes("x"), canonical);
        return new LocalFrame(LocalFrameType.BrokerOutput, session, 1, reference.Encode());
    }

    private static void BrokerRecoveryBeforeLifecycle(string root)
    {
        var session = Guid.NewGuid();
        var directory = Path.Combine(root, "broker-recovery");
        var broker = InlineBroker(session);
        using (var buffer = new EncryptedEventBuffer(directory, session, 1024 * 1024))
        {
            buffer.BindAuthenticatedProfile(TerminalSemanticProfile.Graphics);
            buffer.Append(broker);
        }
        var registration = new SessionRegistration(session, "broker-recovery", new byte[32],
            Environment.ProcessId, ManagedShellType.Custom);
        var recovered = new RecoveredBufferedSession(registration, directory, 1024 * 1024);
        try
        {
            var before = Fingerprint(directory);
            try
            {
                recovered.EnsureTerminalFrame(_ => throw new IOException("simulated missing capture"));
                throw new Exception("Missing capture allowed recovery lifecycle commit.");
            }
            catch (IOException error) when (error.Message == "simulated missing capture") { checks++; }
            Require(Fingerprint(directory) == before, "failed capture recovery preserves every stored byte");
            Require(recovered.TerminalCompatibilityError == "BROKER_CAPTURE_RECOVERY_UNAVAILABLE" &&
                recovered.GetSummary().Lifecycle == SessionAgentLifecycle.Faulted,
                "unavailable capture recovery is explicitly faulted");
            var verified = 0;
            var removal = recovered.EnsureTerminalFrame(frame =>
            {
                Require(frame.Sequence == 1 && frame.Payload.SequenceEqual(broker.Payload),
                    "recovery verifies the exact retained Broker frame");
                Require(Fingerprint(directory) == before, "capture verification precedes synthetic lifecycle");
                verified++;
            });
            Require(verified == 1 && removal?.Sequence == 3, "verified Broker recovery appends terminal markers once");
            Require(recovered.TerminalCompatibilityError.Length == 0, "verified retry clears recovery error");
            var after = Fingerprint(directory);
            recovered.EnsureTerminalFrame(_ => verified++);
            Require(verified == 2 && Fingerprint(directory) == after,
            "repeated recovery revalidates captures without duplicate terminal markers");
        }
        finally { recovered.Dispose(); }
    }

    private static void DeferredRegistryRecovery(string root)
    {
        var session = Guid.NewGuid();
        var directory = Path.Combine(root, "deferred-registry");
        var registration = new SessionRegistration(session, "deferred-recovery", new byte[32],
            Environment.ProcessId, ManagedShellType.Custom, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        new DpapiRegistryRegistrationStore(Path.Combine(directory, "registrations.dpapi")).Upsert(registration);
        using (var buffer = new EncryptedEventBuffer(Path.Combine(directory, session.ToString("N")), session, 1024 * 1024))
        {
            buffer.BindAuthenticatedProfile(TerminalSemanticProfile.Graphics);
            buffer.Append(InlineBroker(session));
        }
        var ready = false;
        var committed = new List<ulong>();
        SessionRegistry registry = null;
        registry = new SessionRegistry(directory, 10000, 1024 * 1024, 5, (_, frame) =>
        {
            Require(ready, "recovery callback waits for the attached production runtime");
            Require(registry.GetSessionProfile(session) == TerminalSemanticProfile.Graphics,
                "recovered profile is visible before capture verification");
            committed.Add(frame.Sequence);
        }, deferRecovery: true);
        using (registry)
        {
            Require(committed.Count == 0 && registry.RecoveredSessionCount == 0,
                "deferred construction never starts retained sessions");
            ready = true;
            registry.RecoverRetainedSessions();
            Require(committed.SequenceEqual(new ulong[] { 1, 3 }) && registry.RecoveredSessionCount == 1,
                "attached recovery verifies capture before the terminal removal");
            registry.RecoverRetainedSessions();
            Require(committed.Count == 2, "recovery starts only once");
        }
    }

    private static void QuarantinedRegistryRecovery(string root)
    {
        var directory = Path.Combine(root, "quarantined-registry");
        var failedSession = Guid.NewGuid();
        var healthySession = Guid.NewGuid();
        var registrations = new DpapiRegistryRegistrationStore(Path.Combine(directory, "registrations.dpapi"));
        foreach (var session in new[] { failedSession, healthySession })
        {
            registrations.Upsert(new SessionRegistration(session, "quarantined-recovery", new byte[32],
                Environment.ProcessId, ManagedShellType.Custom, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
            using var buffer = new EncryptedEventBuffer(Path.Combine(directory, session.ToString("N")), session, 1024 * 1024);
            buffer.BindAuthenticatedProfile(TerminalSemanticProfile.Graphics);
            buffer.Append(session == failedSession ? InlineBroker(session) : Output(session, 1));
        }
        var failedDirectory = Path.Combine(directory, failedSession.ToString("N"));
        var before = Fingerprint(failedDirectory);
        var healthyCommitted = false;
        var captureAvailable = false;
        var recoveredCommits = new List<ulong>();
        using var registry = new SessionRegistry(directory, 10000, 1024 * 1024, 5, (_, frame) =>
        {
            if (frame.SessionId == failedSession)
            {
                if (!captureAvailable) throw new InvalidOperationException("BROKER_CAPTURE_RUNTIME_UNAVAILABLE");
                recoveredCommits.Add(frame.Sequence);
            }
            else healthyCommitted = frame.Type == LocalFrameType.SessionRemoved;
        }, deferRecovery: true);
        registry.RecoverRetainedSessions();
        Require(healthyCommitted, "one missing capture never blocks unrelated retained sessions");
        Require(Fingerprint(failedDirectory) == before, "quarantined recovery never rewrites the unavailable history");
        Require(registry.TerminalCompatibilityError(failedSession) == "BROKER_CAPTURE_RECOVERY_UNAVAILABLE",
            "registry exposes the precise recovery failure");
        Require(registry.ContainsSession(failedSession) && registry.HasSessionBuffer(failedSession) &&
            registry.GetSummaries().All(value => value.SessionId != failedSession),
            "quarantined history remains retained without appearing as an active session");
        registry.RecoverRetainedSessions();
        Require(Fingerprint(failedDirectory) == before, "unavailable retry preserves the quarantined history");
        captureAvailable = true;
        registry.RecoverRetainedSessions();
        Require(registry.TerminalCompatibilityError(failedSession).Length == 0 &&
            recoveredCommits.SequenceEqual(new ulong[] { 1, 3 }),
            "login retries capture verification before completing retained history");
        registry.RecoverRetainedSessions();
        Require(recoveredCommits.Count == 2, "successful login recovery is not repeated");
    }

    private static void Handshake(string root, TerminalSemanticProfile offered, TerminalSemanticProfile persisted,
        string name, bool accepted, bool wrongToken = false, bool wrongPid = false)
    {
        var session = Guid.NewGuid();
        var directory = Path.Combine(root, "handshake-" + name);
        if (persisted != null)
        {
            using var buffer = new EncryptedEventBuffer(directory, session, 1024 * 1024);
            buffer.BindAuthenticatedProfile(persisted);
        }
        Directory.CreateDirectory(directory);
        var before = Fingerprint(directory);
        var token = RandomNumberGenerator.GetBytes(32);
        var sentToken = token.ToArray();
        if (wrongToken) sentToken[0] ^= 1;
        var pipe = "termlink-graphics-profile-" + session.ToString("N");
        var options = AgentOptions.CreateManaged(pipe, session, token, directory, Process.GetCurrentProcess().Id, 10000, 1024 * 1024);
        using var agent = new SessionAgent(options);
        var worker = Task.Run(() =>
        {
            try { agent.RunManaged(); }
            catch (InvalidDataException) when (wrongToken || wrongPid) { }
        });
        try
        {
            using var host = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None);
            host.Connect(5000);
            LocalFrameCodec.Write(host, new LocalFrame(LocalFrameType.HostHello, session, 0,
                LocalPayloadCodec.TerminalHello(Process.GetCurrentProcess().Id + (wrongPid ? 1 : 0), sentToken, offered)));
            var response = Task.Run(() => LocalFrameCodec.Read(host));
            Require(response.Wait(5000), name + " handshake completed");
            if (accepted)
            {
                Require(response.Result?.Type == LocalFrameType.AgentHello, "graphics hello accepted");
                LocalPayloadCodec.ParseTerminalHello(response.Result.Payload, token, out TerminalSemanticProfile echoed);
                Require(echoed == offered && agent.BoundProfile == offered, "authenticated AgentHello echoes graphics profile");
            }
            else
            {
                Require(response.Result == null, name + " rejected");
                Require(agent.BoundProfile == persisted, name + " never changes bound profile");
                Require(Fingerprint(directory) == before, name + " preserves directory");
            }
        }
        finally
        {
            agent.RequestRemoteStop(false);
            Require(worker.Wait(5000), name + " agent stopped");
        }
    }

    private static void RegistryFirstFrame(string root)
    {
        var session = Guid.NewGuid();
        var token = RandomNumberGenerator.GetBytes(32);
        var pipe = "termlink-first-graphics-frame-" + session.ToString("N");
        SessionRegistry registry = null;
        var store = new ProductionTerminalHistoryStore(Path.Combine(root, "first-frame-history"), Guid.NewGuid(),
            id => registry?.GetSessionProfile(id));
        var firstCommit = new TaskCompletionSource<TerminalSemanticProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
        registry = new SessionRegistry(Path.Combine(root, "first-frame-registry"), 10000, 1024 * 1024, 5,
            (_, frame) =>
            {
                try
                {
                    store.AppendCommittedFrame(frame);
                    firstCommit.TrySetResult(store.GetSessionProfile(frame.SessionId));
                }
                catch (Exception error) { firstCommit.TrySetException(error); }
            });
        using (registry)
        using (var host = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None))
        {
            Require(registry.TryRegister(new SessionRegistration(session, pipe, token, Process.GetCurrentProcess().Id,
                ManagedShellType.Custom), out _), "first-frame session registers");
            host.Connect(5000);
            LocalFrameCodec.Write(host, new LocalFrame(LocalFrameType.HostHello, session, 0,
                LocalPayloadCodec.TerminalHello(Process.GetCurrentProcess().Id, token, TerminalSemanticProfile.Graphics)));
            Require(LocalFrameCodec.Read(host)?.Type == LocalFrameType.AgentHello, "first-frame graphics hello");
            LocalFrameCodec.Write(host, Output(session, 1));
            Require(firstCommit.Task.Wait(5000) && firstCommit.Task.Result == TerminalSemanticProfile.Graphics,
                "immediate first frame observes authenticated registry profile");
            Require(store.ReadFrames(session).Count == 1, "first frame committed without profile fallback");
            var concurrentStore = new ProductionTerminalHistoryStore(
                Path.Combine(root, "registry-lock-history"), Guid.NewGuid(), registry.GetSessionProfile);
            Require(registry.DeleteInactiveSession(Guid.NewGuid(), () =>
            {
                // Deletion holds the registry lock while waiting for history. A history writer
                // must be able to read the authenticated profile without waiting for that lock.
                var writer = Task.Run(() =>
                {
                    concurrentStore.AppendCommittedFrame(Output(session, 1));
                    return concurrentStore.GetSessionProfile(session);
                });
                Require(writer.Wait(5000) && writer.Result == TerminalSemanticProfile.Graphics,
                    "history profile lookup does not wait on the registry deletion lock");
            }), "unrelated inactive deletion completes alongside history writes");
        }
    }

    private static void NativeOutboxProfile(string root)
    {
        Require(NativeTerminalCoreV2.SupportsGraphicsV1(), "graphics DLL is available");
        var session = Guid.NewGuid();
        var device = Guid.NewGuid();
        var storeRoot = Path.Combine(root, "native-history");
        var store = new ProductionTerminalHistoryStore(storeRoot, device, _ => TerminalSemanticProfile.Graphics);
        store.AppendCommittedFrame(Output(session, 1));
        using var terminal = NativeTerminalCoreV2.CreateGraphicsV1(8, 3, 1, 1000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.LegacyV1);
        terminal.ApplyOutput(1, Encoding.ASCII.GetBytes("graphics profile"));
        using var checkpoint = terminal.CaptureCheckpoint();
        var snapshot = checkpoint.ExportGraphicsSnapshot();
        var outbox = Outbox(session, true, snapshot);
        store.SaveProduction(new ProductionTerminalHistoryCheckpoint { SessionId = session, LastSequence = 1,
            AuthorityVersion = 2, SnapshotEligible = true, SnapshotPlaintext = snapshot }, outbox);
        var recovered = new ProductionTerminalHistoryStore(storeRoot, device, _ => TerminalSemanticProfile.Graphics);
        Require(recovered.LoadOutbox(session).Artifacts[0].Plaintext.SequenceEqual(snapshot), "graphics outbox roundtrip validates native V5");
        Require(recovered.LoadCheckpoint(session).AuthorityVersion == 2, "native authority generation stays stable");
        var wrongDimensions = Outbox(session, true, snapshot);
        wrongDimensions.Artifacts[0].Columns = 9;
        try { recovered.SaveOutbox(wrongDimensions); throw new Exception("Accepted dimensions mismatch"); }
        catch (InvalidDataException) { checks++; }
    }

    private static ProductionTerminalHistoryOutbox Outbox(Guid session, bool graphics, byte[] snapshot) => new()
    {
        SessionId = session, SnapshotSequence = 1,
        Artifacts = new List<ProductionTerminalHistoryArtifact> { new()
        {
            ArtifactId = "snapshot", Kind = ProductionTerminalHistoryArtifactKind.Snapshot,
            UploadOrder = 0, SnapshotSequence = 1, InitialTranscriptBeforeLineId = 1, Columns = 8, Rows = 3,
            FormatName = graphics ? SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatName : SnapshotCrypto.TerminalCoreSnapshotFormatName,
            FormatVersion = (int)(graphics ? SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatVersion : SnapshotCrypto.TerminalCoreSnapshotFormatVersion),
            Plaintext = snapshot
        } }
    };

    private static LocalFrame Output(Guid session, ulong sequence) =>
        new(LocalFrameType.Output, session, sequence, Encoding.ASCII.GetBytes("sample"));

    private static string Fingerprint(string directory) => string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

    private static void Reject(string code, Action action)
    {
        try { action(); }
        catch (TerminalHistoryProfileException error) { Require(error.Code == code, "rejection code " + code); return; }
        throw new InvalidOperationException("Accepted " + code);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
}
