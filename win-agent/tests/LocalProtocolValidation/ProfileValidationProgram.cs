using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal static class ProfileValidationProgram
    {
        private static int checks;

        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--graphics-profile-only")
                return GraphicsProfileValidationProgram.Main(args.Skip(1).ToArray());
            var result = RegistryValidationProgram.Main(args);
            if (result != 0 || args.Length != 2 || args[0] != "--profile-only") return result;
            var root = Path.Combine(args[1], "persistence-" + Guid.NewGuid().ToString("N"));
            ClipboardTransportValidation.Run(root);
            ClipboardLeaseValidation.Run();
            ValidateCurrentSession(root);
            ValidateHandshakeCannotRestampLegacy(root);
            foreach (var kind in new[] { "missing", "mismatch", "invalid", "owner", "session", "oversize" })
            {
                ValidateRejectedJournal(root, kind);
                ValidateRejectedProjection(root, kind);
            }
            Console.WriteLine("Persistent terminal profile: " + checks + " assertions passed (DPAPI, restart, incompatible recovery, file preservation).");
            return 0;
        }

        private static void ValidateCurrentSession(string root)
        {
            var session = Guid.NewGuid();
            var directory = Path.Combine(root, "current");
            using (var buffer = new EncryptedEventBuffer(directory, session, 1024 * 1024))
            {
                Require(!buffer.HasBoundProfile, "Empty buffer claimed an authenticated producer");
                Reject(TerminalSessionProfile.Missing, () => buffer.Append(Output(session, 1)));
                Require(!File.Exists(Path.Combine(directory, TerminalSessionProfile.FileName)), "Unaccepted output stamped a profile");
                buffer.BindAuthenticatedProfile();
                buffer.Append(Output(session, 1));
            }
            var marker = File.ReadAllBytes(Path.Combine(directory, TerminalSessionProfile.FileName));
            using (var restored = new EncryptedEventBuffer(directory, session, 1024 * 1024))
            {
                restored.BindAuthenticatedProfile();
                restored.Append(Output(session, 2));
                Require(restored.ReadFrom(0, out _, out _).Count == 2, "Compatible restart lost journal output");
                Require(File.ReadAllBytes(Path.Combine(directory, TerminalSessionProfile.FileName)).SequenceEqual(marker), "Reconnect rewrote immutable profile");
            }
            var device = Guid.NewGuid();
            var projectionRoot = Path.Combine(root, "current-projection");
            var store = new ProductionTerminalHistoryStore(projectionRoot, device);
            store.AppendCommittedFrame(Output(session, 1));
            store = new ProductionTerminalHistoryStore(projectionRoot, device);
            Require(store.TerminalCompatibilityError(session) == string.Empty, "Compatible projection rejected after restart");
            Require(store.ReadFrames(session).Count == 1, "Compatible projection replay failed");
            var emptySession = Guid.NewGuid();
            store.ReadFrames(emptySession);
            Require(!File.Exists(Path.Combine(projectionRoot, emptySession.ToString("N"), TerminalSessionProfile.FileName)), "Empty read invented a producer");
            store.IsInactiveRetentionExpired(emptySession, false, DateTimeOffset.UtcNow);
            store.AppendCommittedFrame(Output(emptySession, 1));
            Require(store.ReadFrames(emptySession).Count == 1, "Retention metadata prevented first authenticated output");
        }

        private static void ValidateRejectedJournal(string root, string kind)
        {
            var session = Guid.NewGuid();
            var directory = Path.Combine(root, "journal-" + kind);
            using (var buffer = new EncryptedEventBuffer(directory, session, 1024 * 1024))
            {
                buffer.BindAuthenticatedProfile();
                buffer.Append(Output(session, 1));
            }
            ChangeProfile(directory, session, session.ToByteArray(), kind);
            var before = Fingerprint(directory);
            var code = ErrorCode(kind);
            // An incompatible journal is not trimmed even when the new retention budget is smaller.
            using (var restored = new EncryptedEventBuffer(directory, session, 1))
            {
                Require(restored.TerminalCompatibilityError == code, "Wrong journal error: " + kind);
                Require(!restored.HasBoundProfile, "Incompatible journal became bound");
                Reject(code, () => restored.BindAuthenticatedProfile());
                Reject(code, () => restored.Append(Output(session, 2)));
                Reject(code, () => restored.ReadFrom(0, out _, out _));
            }
            var registration = new SessionRegistration(session, "profile-test", new byte[32],
                Process.GetCurrentProcess().Id, ManagedShellType.Custom);
            var recovered = new RecoveredBufferedSession(registration, directory, 1);
            try
            {
                Require(recovered.TerminalCompatibilityError == code, "Completed session lost profile error");
                Require(recovered.EnsureTerminalFrame() == null, "Recovery appended a current lifecycle to old history");
                Reject(code, () => recovered.ReadBufferedEvents(0, out _, out _));
            }
            finally { recovered.Dispose(); }
            Require(Fingerprint(directory) == before, "Incompatible journal was mutated: " + kind);
        }

        private static void ValidateRejectedProjection(string root, string kind)
        {
            var session = Guid.NewGuid();
            var device = Guid.NewGuid();
            var projectionRoot = Path.Combine(root, "projection-" + kind);
            var directory = Path.Combine(projectionRoot, session.ToString("N"));
            var store = new ProductionTerminalHistoryStore(projectionRoot, device);
            store.AppendCommittedFrame(Output(session, 1));
            store.SaveCheckpoint(new ProductionTerminalHistoryCheckpoint
            {
                SessionId = session, LastSequence = 1, AuthorityVersion = 2,
                SnapshotEligible = false, IneligibilityCode = "TEST_FIXTURE"
            });
            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(
                "TermLink production terminal history terminal-profile V1\n" + device.ToString("N") + "\n" + session.ToString("N")));
            ChangeProfile(directory, session, entropy, kind);
            var before = Fingerprint(directory);
            store = new ProductionTerminalHistoryStore(projectionRoot, device);
            var code = ErrorCode(kind);
            Require(store.TerminalCompatibilityError(session) == code, "Wrong projection error: " + kind);
            Reject(code, () => store.ReadFrames(session));
            Reject(code, () => store.ReadContiguousFrames(session, 0, 2, 10, 4096, out _));
            Reject(code, () => store.AppendCommittedFrame(Output(session, 2)));
            Reject(code, () => store.LoadCheckpoint(session));
            using (var producer = new ProductionTerminalHistoryProducer(store, device))
            {
                var service = new ProductionHostHistoryService(store, producer, device, _ => false);
                foreach (var requestKind in new[] { HostHistoryRequestKindV1.LatestSnapshot, HostHistoryRequestKindV1.EventRange, HostHistoryRequestKindV1.TranscriptPage })
                {
                    using var selected = service.Select(session, new HostHistoryRequestPayload { Kind = requestKind }, new ProductionHistoryRecipient());
                    Require(selected.Status == HostHistoryResponseStatusV1.Rejected && selected.ErrorCode == code,
                        "History RPC did not preserve rejection category: " + requestKind);
                }
            }
            Require(Fingerprint(directory) == before, "Rejected projection was mutated: " + kind);
        }

        private static void ValidateHandshakeCannotRestampLegacy(string root)
        {
            var session = Guid.NewGuid();
            var directory = Path.Combine(root, "legacy-handshake");
            using (var buffer = new EncryptedEventBuffer(directory, session, 1024 * 1024))
            {
                buffer.BindAuthenticatedProfile();
                buffer.Append(Output(session, 1));
            }
            File.Delete(Path.Combine(directory, TerminalSessionProfile.FileName));
            var before = Fingerprint(directory);
            var token = RandomNumberGenerator.GetBytes(32);
            var pipe = "termlink-persisted-profile-" + session.ToString("N");
            var options = AgentOptions.CreateManaged(pipe, session, token, directory, Process.GetCurrentProcess().Id, 10000, 1024 * 1024);
            using (var agent = new SessionAgent(options))
            {
                var worker = Task.Run((Action)agent.RunManaged);
                try
                {
                    using var host = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None);
                    host.Connect(5000);
                    LocalFrameCodec.Write(host, new LocalFrame(LocalFrameType.HostHello, session, 0,
                        LocalPayloadCodec.TerminalHello(Process.GetCurrentProcess().Id, token)));
                    var reply = Task.Run(() => LocalFrameCodec.Read(host));
                    Require(reply.Wait(5000) && reply.Result == null, "Matching host bypassed old journal admission");
                    Require(agent.TerminalCompatibilityError == TerminalSessionProfile.Missing, "Old journal failure was not exposed");
                    Require(agent.Snapshot.LastSequence == 1 && agent.Snapshot.Lifecycle != SessionAgentLifecycle.Connected,
                        "Old journal changed sequence or entered Connected");
                }
                finally { agent.RequestRemoteStop(true); Require(worker.Wait(5000), "Profile test worker did not stop"); }
            }
            Require(Fingerprint(directory) == before, "Current handshake or stop restamped legacy history");
        }

        private static void ChangeProfile(string directory, Guid session, byte[] entropy, string kind)
        {
            var path = Path.Combine(directory, TerminalSessionProfile.FileName);
            if (kind == "missing") { File.Delete(path); return; }
            if (kind == "invalid") { File.WriteAllBytes(path, new byte[] { 1, 2, 3 }); return; }
            if (kind == "oversize") { File.WriteAllBytes(path, new byte[4097]); return; }
            var bytes = TerminalSessionProfile.Encode(kind == "session" ? Guid.NewGuid() : session);
            if (kind == "mismatch") bytes[bytes.Length - 1] = (byte)'9';
            if (kind == "owner") entropy = Guid.NewGuid().ToByteArray();
            File.WriteAllBytes(path, ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser));
        }

        private static string ErrorCode(string kind) => kind == "missing" ? TerminalSessionProfile.Missing :
            kind == "mismatch" ? TerminalSessionProfile.Mismatch : TerminalSessionProfile.Invalid;

        private static LocalFrame Output(Guid session, ulong sequence) =>
            new LocalFrame(LocalFrameType.Output, session, sequence, Encoding.UTF8.GetBytes("synthetic-profile-output\r\n"));

        private static string Fingerprint(string directory) => string.Join("\n", Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).Select(path =>
                Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

        private static void Reject(string code, Action action)
        {
            try { action(); }
            catch (TerminalHistoryProfileException error) { Require(error.Code == code, "Wrong rejection code: " + error.Code); return; }
            throw new Exception("Expected rejection: " + code);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
    }
}
