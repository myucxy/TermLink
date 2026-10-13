using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Common;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

internal static class BrokerCaptureRestoreValidation
{
    internal static void Run()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.Combine(tempRoot, "termlink-broker-restore-" + Guid.NewGuid().ToString("N"));
        var pc = Guid.NewGuid();
        var phone = Guid.NewGuid();
        var session = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        byte[] canonical = null;
        byte[] hash = null;
        try
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "broker-captures-v1.json")));
            canonical = Convert.FromHexString(fixture.RootElement.GetProperty("encoded_hex").GetString());
            hash = SHA256.HashData(canonical);
            var store = new ProductionTerminalHistoryStore(root, pc,
                _ => TerminalSemanticProfile.Graphics);
            store.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 1,
                Encoding.ASCII.GetBytes("first")));
            using (var captures = new KittyCaptureStore(store.BrokerCaptureRootPath, session))
            {
                var receipt = captures.Put(objectId, 2, canonical);
                CryptographicOperations.ZeroMemory(receipt.Sha256);
            }
            using var reference = NativeTerminalBrokerOutputReferenceV1.External(2,
                Encoding.ASCII.GetBytes("x"), objectId, canonical.Length, hash);
            var brokerFrame = new LocalFrame(LocalFrameType.BrokerOutput, session, 2,
                reference.Encode());
            var blockedWalPath = Path.Combine(root, session.ToString("N"), "wal",
                2UL.ToString("D20") + ".wal.dpapi");
            Directory.CreateDirectory(blockedWalPath);
            var observed = 0;
            store.FrameObserved += _ => observed++;
            var failed = false;
            try { new ProductionBrokerCaptureService(store, pc).PersistAndVerifyPublishedFrame(brokerFrame); }
            catch (IOException) { failed = true; }
            Require(failed && observed == 0 && Directory.Exists(blockedWalPath) &&
                    !File.Exists(blockedWalPath),
                "BrokerOutput was published before its WAL write completed.");
            var request = new HostHistoryRequestPayload
            {
                RequestId = ByteString.CopyFrom(Convert.FromHexString(Guid.NewGuid().ToString("N"))),
                Kind = HostHistoryRequestKindV1.BrokerCapture,
                BrokerCapture = new BrokerCaptureSelectionV1
                {
                    ObjectId = ByteString.CopyFrom(Convert.FromHexString(objectId.ToString("N"))),
                    SourceEventSeq = 2,
                    CanonicalBytes = (uint)canonical.Length,
                    CanonicalSha256 = ByteString.CopyFrom(hash)
                }
            };
            request.SubscriberCapabilities.Add("terminal_broker_captures_v1");
            var recipient = new ProductionHistoryRecipient
            { DeviceId = phone, PairwiseKey = key, KeyEpoch = 3 };
            var restartedStore = new ProductionTerminalHistoryStore(root, pc);
            var restartedService = new ProductionBrokerCaptureService(restartedStore, pc);
            using (var missing = restartedService.Select(session, request, recipient, CancellationToken.None))
                Require(missing.ErrorCode == "BROKER_CAPTURE_MISSING" && missing.Object == null,
                    "A sidecar without a durable WAL reference was selected after restart.");
            Directory.Delete(blockedWalPath, recursive: false);
            restartedService.PersistAndVerifyPublishedFrame(brokerFrame);
            var recoveredStore = new ProductionTerminalHistoryStore(root, pc);
            var recoveredService = new ProductionBrokerCaptureService(recoveredStore, pc);
            using (var recovered = recoveredService.Select(session, request, recipient, CancellationToken.None))
            {
                Require(recovered.Status == HostHistoryResponseStatusV1.Ok &&
                    recovered.Object?.Kind == HostHistoryObjectKindV1.BrokerCapture,
                    "A durable BrokerOutput was not selectable after restart.");
                var opened = BrokerCaptureCrypto.Open(key, recovered.Object.SealedObject,
                    session, pc, phone, objectId, 2, 3, (uint)canonical.Length, hash);
                try { Require(opened.AsSpan().SequenceEqual(canonical),
                    "Restarted Broker capture differs from its durable sidecar."); }
                finally { CryptographicOperations.ZeroMemory(opened); }
            }
            var third = new LocalFrame(LocalFrameType.Output, session, 3,
                Encoding.ASCII.GetBytes("third"));
            recoveredStore.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 4,
                Encoding.ASCII.GetBytes("fourth")));
            var incomplete = recoveredStore.ReadContiguousFrames(session, 2, 4, 2, 4096,
                out var gap);
            Require(gap && incomplete.Count == 0,
                "Out-of-order WAL frames were replayed across a sequence gap.");
            recoveredStore.AppendCommittedFrame(third);
            recoveredStore.AppendCommittedFrame(third);
            var conflict = false;
            try
            {
                recoveredStore.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 3,
                    Encoding.ASCII.GetBytes("different")));
            }
            catch (InvalidDataException) { conflict = true; }
            Require(conflict, "A duplicate WAL sequence accepted different bytes.");
            var finalStore = new ProductionTerminalHistoryStore(root, pc);
            var replay = finalStore.ReadContiguousFrames(session, 0, 4, 4, 4096,
                out gap);
            try { Require(!gap && replay.Count == 4 &&
                replay.Select(value => value.Sequence).SequenceEqual(new ulong[] { 1, 2, 3, 4 }),
                "Restarted WAL replay lost or reordered frames."); }
            finally
            {
                foreach (var frame in replay) CryptographicOperations.ZeroMemory(frame.Payload);
            }
            var walPath = Path.Combine(root, session.ToString("N"), "wal",
                4UL.ToString("D20") + ".wal.dpapi");
            var bytes = File.ReadAllBytes(walPath);
            bytes[^1] ^= 1;
            File.WriteAllBytes(walPath, bytes);
            CryptographicOperations.ZeroMemory(bytes);
            var corruptRejected = false;
            try { finalStore.ReadContiguousFrames(session, 3, 4, 1, 4096, out _); }
            catch (InvalidDataException) { corruptRejected = true; }
            Require(corruptRejected, "A corrupted WAL frame was replayed after restart.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (canonical != null) CryptographicOperations.ZeroMemory(canonical);
            if (hash != null) CryptographicOperations.ZeroMemory(hash);
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), tempRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("termlink-broker-restore-", StringComparison.Ordinal))
                throw new InvalidOperationException("Broker restore test cleanup escaped TEMP.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
