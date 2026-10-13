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

internal static class BrokerCaptureHostValidation
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "termlink-broker-host-" + Guid.NewGuid().ToString("N"));
        var pc = Guid.NewGuid();
        var phone = Guid.NewGuid();
        var session = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        try
        {
            var fixture = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "broker-captures-v1.json")));
            using (fixture)
            {
                var canonical = Convert.FromHexString(fixture.RootElement.GetProperty("encoded_hex").GetString());
                var hash = SHA256.HashData(canonical);
                try
                {
                    var store = new ProductionTerminalHistoryStore(root, pc,
                        _ => TerminalSemanticProfile.Graphics);
                    var captureRoot = Path.Combine(root, "broker-captures");
                    Directory.CreateDirectory(captureRoot);
                    using var captureStore = new KittyCaptureStore(captureRoot, session);
                    captureStore.Put(objectId, 2, canonical);
                    using var reference = NativeTerminalBrokerOutputReferenceV1.External(
                        2, Encoding.UTF8.GetBytes("x"), objectId, canonical.Length, hash);
                    var encodedReference = reference.Encode();
                    var brokerFrame = new LocalFrame(LocalFrameType.BrokerOutput,
                        session, 2, encodedReference);
                    var historyEvent = ProductionAgentRuntime.BuildHistoryEvent(brokerFrame);
                    var projected = BrokerOutputPayload.Parser.ParseFrom(historyEvent.Payload);
                    Require(historyEvent.Seq == 2 && historyEvent.MessageType == MessageType.BrokerOutput &&
                        projected.ReferenceV1.Span.SequenceEqual(encodedReference),
                        "The exact BrokerOutput reference must survive history event mapping.");
                    var service = new ProductionBrokerCaptureService(store, pc);
                    service.PersistAndVerifyPublishedFrame(brokerFrame);
                    var persisted = store.ReadContiguousFrames(session, 1, 2, 1,
                        128 * 1024, out var gapDetected);
                    Require(!gapDetected && persisted.Count == 1 &&
                        persisted[0].Payload.AsSpan().SequenceEqual(brokerFrame.Payload),
                        "The exact BrokerOutput WAL reference was not durable before publication.");
                    CryptographicOperations.ZeroMemory(persisted[0].Payload);
                    var recipient = new ProductionHistoryRecipient
                    { DeviceId = phone, KeyEpoch = 3, PairwiseKey = key };
                    var request = new HostHistoryRequestPayload
                    {
                        RequestId = ByteString.CopyFrom(Convert.FromHexString(Guid.NewGuid().ToString("N"))),
                        Kind = HostHistoryRequestKindV1.BrokerCapture,
                        BrokerCapture = new BrokerCaptureSelectionV1
                        {
                            ObjectId = ByteString.CopyFrom(Convert.FromHexString(objectId.ToString("N"))),
                            SourceEventSeq = 2, CanonicalBytes = (uint)canonical.Length,
                            CanonicalSha256 = ByteString.CopyFrom(hash)
                        }
                    };
                    request.SubscriberCapabilities.Add("terminal_broker_captures_v1");
                    using var selected = service.Select(session, request, recipient, CancellationToken.None);
                    Require(selected.Status == HostHistoryResponseStatusV1.Ok &&
                        selected.Object?.Kind == HostHistoryObjectKindV1.BrokerCapture &&
                        selected.Object.ObjectId == objectId && selected.Object.StartSequence == 2 &&
                        selected.Object.EndSequence == 2 && selected.StateDelta == null &&
                        selected.BrokerCapture?.CanonicalBytes == canonical.Length,
                        "Broker capture selection lost WAL-authorized identity.");
                    var opened = BrokerCaptureCrypto.Open(key, selected.Object.SealedObject,
                        session, pc, phone, objectId, 2, 3, (uint)canonical.Length, hash);
                    try { Require(opened.AsSpan().SequenceEqual(canonical),
                        "The recipient cannot open the exact captured object."); }
                    finally { CryptographicOperations.ZeroMemory(opened); }
                    var digest = SHA256.HashData(selected.Object.SealedObject);
                    var response = ProductionHostHistoryResponseBuilder.Build(request.RequestId,
                        selected, 0, 1, digest, 0, selected.Object.SealedObject.Length, request.Kind);
                    Require(response.SnapshotSeq == 0 && response.Columns == 0 && response.Rows == 0 &&
                        response.InitialTranscriptBeforeLineId == 0 && response.StartSeq == 2 &&
                        response.EndSeq == 2 && response.BrokerCapture.Equals(request.BrokerCapture),
                        "Broker capture response metadata or no-snapshot contract changed.");
                    var forged = request.Clone();
                    forged.BrokerCapture.ObjectId = ByteString.CopyFrom(
                        Convert.FromHexString(Guid.NewGuid().ToString("N")));
                    using (var rejected = service.Select(session, forged, recipient, CancellationToken.None))
                        Require(rejected.ErrorCode == "BROKER_CAPTURE_MISSING" && rejected.Object == null,
                            "A UUID absent from the exact WAL reference was selectable.");
                    var wrongSequence = request.Clone();
                    wrongSequence.BrokerCapture.SourceEventSeq = 3;
                    using (var rejected = service.Select(session, wrongSequence, recipient, CancellationToken.None))
                        Require(rejected.ErrorCode == "BROKER_CAPTURE_MISSING",
                            "An unrelated event sequence selected a capture.");
                    var wrongHash = request.Clone();
                    wrongHash.BrokerCapture.CanonicalSha256 = ByteString.CopyFrom(new byte[32]);
                    using (var rejected = service.Select(session, wrongHash, recipient, CancellationToken.None))
                        Require(rejected.ErrorCode == "BROKER_CAPTURE_MISSING",
                            "A digest absent from the exact WAL reference was selectable.");
                    var oldPeer = request.Clone();
                    oldPeer.SubscriberCapabilities.Clear();
                    using (var rejected = service.Select(session, oldPeer, recipient, CancellationToken.None))
                        Require(rejected.ErrorCode == "UNSUPPORTED_FORMAT",
                            "A peer without Broker capture capability was accepted.");
                    var oversizedObject = new byte[BrokerCaptureCrypto.MaximumObjectBytes];
                    try
                    {
                        using var bound = new ProductionHostHistorySelection
                        {
                            Status = HostHistoryResponseStatusV1.Ok,
                            BrokerCapture = new BrokerCaptureSelectionV1
                            {
                                ObjectId = request.BrokerCapture.ObjectId,
                                SourceEventSeq = 2,
                                CanonicalBytes = BrokerCaptureCrypto.MaximumCanonicalBytes,
                                CanonicalSha256 = ByteString.CopyFrom(hash)
                            },
                            Object = new ProductionHostHistoryObject
                            {
                                Kind = HostHistoryObjectKindV1.BrokerCapture,
                                ObjectId = objectId, StartSequence = 2, EndSequence = 2,
                                FormatName = "TERMLINK_BROKER_CAPTURE_V1", FormatVersion = 1,
                                SealedObject = oversizedObject
                            }
                        };
                        var chunks = ProductionHostHistoryResponseBuilder.Build(request.RequestId,
                            bound, 64, 65, SHA256.HashData(oversizedObject), 64 * 512 * 1024,
                            208, request.Kind);
                        Require(chunks.ChunkCount == 65 && chunks.ObjectChunk.Length == 208,
                            "The 65th capture chunk lost its exact final length.");
                        ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(request.RequestId,
                            bound, 64, 65, SHA256.HashData(oversizedObject), 64 * 512 * 1024,
                            207, request.Kind));
                    }
                    finally { CryptographicOperations.ZeroMemory(oversizedObject); }
                    var capturePath = Path.Combine(captureRoot, session.ToString("N"),
                        objectId.ToString("N") + ".capture");
                    var stored = File.ReadAllBytes(capturePath);
                    stored[^1] ^= 1;
                    File.WriteAllBytes(capturePath, stored);
                    try
                    {
                        service.PersistAndVerifyPublishedFrame(brokerFrame);
                        throw new InvalidOperationException("A damaged capture was published live.");
                    }
                    catch (InvalidDataException) { }
                    using (var invalid = service.Select(session, request, recipient, CancellationToken.None))
                        Require(invalid.ErrorCode == "INVALID_BROKER_CAPTURE" && invalid.Object == null,
                            "A damaged capture must produce an explicit integrity error.");
                    File.Delete(capturePath);
                    try
                    {
                        service.PersistAndVerifyPublishedFrame(brokerFrame);
                        throw new InvalidOperationException("A missing capture was accepted for history reconciliation.");
                    }
                    catch (IOException) { }
                    using (var missingCapture = service.Select(session, request, recipient, CancellationToken.None))
                        Require(missingCapture.ErrorCode == "BROKER_CAPTURE_MISSING" && missingCapture.Object == null,
                            "A missing sidecar was selectable through its durable WAL reference.");
                    store.DeleteSession(session);
                    service.DeleteSession(session);
                    try
                    {
                        service.PersistAndVerifyPublishedFrame(brokerFrame);
                        throw new InvalidOperationException("Deleted BrokerOutput history accepted a late append.");
                    }
                    catch (InvalidOperationException error) when (
                        error.Message != "Deleted BrokerOutput history accepted a late append.") { }
                    Require(!Directory.Exists(Path.Combine(root, session.ToString("N"))),
                        "A late BrokerOutput append recreated deleted session history.");
                    CryptographicOperations.ZeroMemory(encodedReference);
                    using (var missing = service.Select(session, request, recipient, CancellationToken.None))
                        Require(missing.ErrorCode == "BROKER_CAPTURE_MISSING" && missing.Object == null,
                            "Deleting captured objects did not produce an explicit missing-object error.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(canonical);
                    CryptographicOperations.ZeroMemory(hash);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var resolvedRoot = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolvedRoot), temporaryRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedRoot).StartsWith("termlink-broker-host-", StringComparison.Ordinal))
                throw new InvalidOperationException("Broker host test cleanup path escaped its temporary root.");
            if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, true);
        }
    }

    private static void ExpectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("An invalid Broker capture chunk passed validation.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
