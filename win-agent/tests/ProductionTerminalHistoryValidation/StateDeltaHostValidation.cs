using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

internal static class StateDeltaHostValidation
{
    private static readonly Guid Pc = Guid.Parse("b89de40e-8e3d-4e2d-bb36-616161616161");
    private static readonly Guid Phone = Guid.Parse("84bcd7f1-1b37-4693-a508-626262626262");

    internal static void Run(string fixturePath = null)
    {
        RunProfile(TerminalSemanticProfile.Graphics, fixturePath);
        if (fixturePath == null) RunProfile(TerminalSemanticProfile.GraphicsUnicode16, null);
    }

    private static void RunProfile(TerminalSemanticProfile profile, string fixturePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "termlink-state-delta-" + Guid.NewGuid().ToString("N"));
        var session = Guid.Parse("b14fbf55-4b1b-4a89-a9ee-636363636363");
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var contextId = DeltaCrypto.DeriveContextId(session, Pc, profile.StateContract);
        var nativeProfile = profile == TerminalSemanticProfile.Graphics
            ? NativeTerminalDeltaProfile.LegacyV1 : NativeTerminalDeltaProfile.Unicode16V1;
        try
        {
            var store = new ProductionTerminalHistoryStore(root, Pc,
                _ => profile);
            using var producer = new ProductionTerminalHistoryProducer(store, Pc,
                terminalCoreShadowEnabled: false, terminalCoreSnapshotAuthorityEnabled: true,
                terminalCoreShadowObserver: null);
            var text = new StringBuilder();
            if (profile == TerminalSemanticProfile.GraphicsUnicode16) text.Append("😀");
            for (var row = 0; row < 300; row++)
            {
                for (var column = 0; column < 72; column++)
                    text.Append((char)('!' + (row * 37 + column * 13) % 90));
                text.Append("\r\n");
            }
            producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 1,
                Encoding.UTF8.GetBytes(text.ToString())));
            Require(producer.ProcessSession(session).SnapshotEligible, "Delta base fixture is ineligible.");
            using (var noPrevious = store.ReadDeltaSnapshotPair(session, 0))
                Require(noPrevious?.Current?.SnapshotSequence == 1 && noPrevious.Previous == null,
                    "First graphics snapshot unexpectedly had a Delta base.");
            producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.LocalInputObserved, session, 2,
                LocalPayloadCodec.LocalInputEpoch(7)));
            producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 3,
                Encoding.ASCII.GetBytes("Z")));
            Require(producer.ProcessSession(session).SnapshotEligible, "Delta target fixture is ineligible.");
            Require(store.ReadDeltaSnapshotPair(session, 1) == null,
                "A stale exact target S must not choose the current snapshot.");
            using var pair = store.ReadDeltaSnapshotPair(session, 0);
            using var exact = store.ReadDeltaSnapshotPair(session, 3);
            Require(pair?.Current?.SnapshotSequence == 3 && pair.Previous?.SnapshotSequence == 1 &&
                exact?.Current?.SnapshotSequence == 3 && exact.Previous?.SnapshotSequence == 1 &&
                pair.Current.ObservedLocalInputEpoch == 7 && pair.Previous.ObservedLocalInputEpoch == 0,
                "Latest and exact target selection must read the same durable pair.");
            var request = new HostHistoryRequestPayload
            {
                RequestId = ByteString.CopyFrom(Convert.FromHexString(Guid.NewGuid().ToString("N"))),
                Kind = HostHistoryRequestKindV1.StateDelta,
                SnapshotSeq = 0,
                StateDelta = new StateDeltaRequestV1
                {
                    ContextId = ByteString.CopyFrom(contextId),
                    Profile = (uint)nativeProfile,
                    BaseNextSourceSequence = pair.Previous.SnapshotSequence + 1,
                    BaseSnapshotBytes = (ulong)pair.Previous.Plaintext.Length,
                    BaseSnapshotSha256 = ByteString.CopyFrom(SHA256.HashData(pair.Previous.Plaintext))
                }
            };
            request.SubscriberCapabilities.Add("state_delta_v1");
            var recipient = new ProductionHistoryRecipient
            { DeviceId = Phone, PairwiseKey = key, KeyEpoch = 3 };
            var service = new ProductionStateDeltaService(store, Pc);
            using var selected = service.Select(session, request, recipient, CancellationToken.None);
            Require(selected.Status == HostHistoryResponseStatusV1.Ok &&
                selected.Object?.Kind == HostHistoryObjectKindV1.StateDelta &&
                selected.Object.SealedObject.Length <= DeltaCrypto.MaximumObjectBytes &&
                selected.Object.SealedObject.Length < pair.Current.Plaintext.Length &&
                selected.StateDelta?.TargetNextSourceSequence == 4 &&
                selected.StateDelta.Profile == (uint)nativeProfile &&
                selected.Object.KeyId == DeltaCrypto.KeyId(Phone, 3, profile.StateContract),
                "Host must seal one useful, bounded Delta for the selected target.");
            var metadata = selected.StateDelta;
            var response = ProductionHostHistoryResponseBuilder.Build(request.RequestId, selected,
                0, 1, SHA256.HashData(selected.Object.SealedObject), 0,
                selected.Object.SealedObject.Length, request.Kind, requestedSnapshotSequence: 0);
            Require(response.SnapshotSeq == 3 && response.Columns == 0 && response.Rows == 0 &&
                response.InitialTranscriptBeforeLineId == 0 && response.StateDelta.Equals(metadata),
                "Delta outer response must carry exact S and repeat metadata without legacy geometry.");
            if (fixturePath != null)
            {
                var fixture = new
                {
                    session_id = session.ToString("D"), pc_device_id = Pc.ToString("D"),
                    recipient_device_id = Phone.ToString("D"), key_epoch = 3,
                    pairwise_key = Convert.ToHexString(key).ToLowerInvariant(),
                    context_id = Convert.ToHexString(contextId).ToLowerInvariant(), profile = metadata.Profile,
                    base_snapshot_sequence = pair.Previous.SnapshotSequence,
                    base_observed_local_input_epoch = pair.Previous.ObservedLocalInputEpoch,
                    target_next_sequence = metadata.TargetNextSourceSequence,
                    target_observed_local_input_epoch = pair.Current.ObservedLocalInputEpoch,
                    target_initial_transcript_before_line_id = pair.Current.InitialTranscriptBeforeLineId,
                    base_snapshot = Convert.ToHexString(pair.Previous.Plaintext).ToLowerInvariant(),
                    target_snapshot = Convert.ToHexString(pair.Current.Plaintext).ToLowerInvariant(),
                    base_sha256 = Convert.ToHexString(SHA256.HashData(pair.Previous.Plaintext)).ToLowerInvariant(),
                    target_sha256 = Convert.ToHexString(SHA256.HashData(pair.Current.Plaintext)).ToLowerInvariant(),
                    sealed_object = Convert.ToHexString(selected.Object.SealedObject).ToLowerInvariant()
                };
                File.WriteAllText(fixturePath, JsonSerializer.Serialize(fixture,
                    new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            }
            try
            {
                ProductionHostHistoryResponseBuilder.Build(request.RequestId, selected,
                    0, 2, SHA256.HashData(selected.Object.SealedObject), 0,
                    selected.Object.SealedObject.Length, request.Kind);
                throw new InvalidOperationException("Delta accepted an inaccurate chunk count.");
            }
            catch (ArgumentException) { }
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(request.RequestId,
                selected, 1, 1, SHA256.HashData(selected.Object.SealedObject), 0,
                selected.Object.SealedObject.Length, request.Kind),
                "An out-of-range Delta chunk index was accepted.");
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(request.RequestId,
                selected, 0, 1, SHA256.HashData(selected.Object.SealedObject), 1,
                selected.Object.SealedObject.Length - 1, request.Kind),
                "An out-of-order Delta chunk offset was accepted.");
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(request.RequestId,
                selected, 0, 1, new byte[31], 0,
                selected.Object.SealedObject.Length, request.Kind),
                "A malformed Delta object digest was accepted.");
            var opened = DeltaCrypto.Open(key, selected.Object.SealedObject, session, Pc, Phone, 3,
                profile.StateContract, contextId, metadata.Profile,
                metadata.BaseNextSourceSequence, metadata.TargetNextSourceSequence,
                metadata.BaseSnapshotBytes, metadata.TargetSnapshotBytes,
                metadata.BaseSnapshotSha256.ToByteArray(), metadata.TargetSnapshotSha256.ToByteArray(),
                pair.Previous.ObservedLocalInputEpoch);
            try
            {
                Require(opened.TargetObservedLocalInputEpoch == pair.Current.ObservedLocalInputEpoch &&
                    opened.TargetInitialTranscriptBeforeLineId == pair.Current.InitialTranscriptBeforeLineId,
                    "Delta AEAD header lost target product metadata.");
                using var baseTerminal = NativeTerminalCoreV2.ImportGraphicsSnapshot(pair.Previous.Plaintext);
                using var checkpoint = baseTerminal.CaptureCheckpoint();
                var context = new NativeTerminalDeltaContext(contextId, nativeProfile);
                var result = checkpoint.ApplyDelta(context, opened.Delta,
                    (ulong)DeltaCrypto.MaximumPlaintextBytes);
                using var restored = result.Terminal;
                using var restoredCheckpoint = restored?.CaptureCheckpoint();
                Require(result.Outcome == NativeTerminalDeltaOutcome.Applied &&
                    restoredCheckpoint?.ExportGraphicsSnapshot().SequenceEqual(pair.Current.Plaintext) == true,
                    "Host Delta must reconstruct the target canonical graphics snapshot.");
            }
            finally { CryptographicOperations.ZeroMemory(opened.Delta); }
            var otherRecipient = new ProductionHistoryRecipient
            { DeviceId = Guid.NewGuid(), PairwiseKey = key, KeyEpoch = 3 };
            using (var other = service.Select(session, request, otherRecipient, CancellationToken.None))
            {
                Require(other.Status == HostHistoryResponseStatusV1.Ok &&
                    !other.Object.SealedObject.SequenceEqual(selected.Object.SealedObject),
                    "A Delta ciphertext must be bound independently to each recipient.");
                try
                {
                    var wrong = DeltaCrypto.Open(key, other.Object.SealedObject, session, Pc, Phone, 3,
                        profile.StateContract, contextId, metadata.Profile,
                        metadata.BaseNextSourceSequence, metadata.TargetNextSourceSequence,
                        metadata.BaseSnapshotBytes, metadata.TargetSnapshotBytes,
                        metadata.BaseSnapshotSha256.ToByteArray(),
                        metadata.TargetSnapshotSha256.ToByteArray(),
                        pair.Previous.ObservedLocalInputEpoch);
                    CryptographicOperations.ZeroMemory(wrong.Delta);
                    throw new InvalidOperationException("Another recipient opened a Delta ciphertext.");
                }
                catch (InvalidDataException) { }
            }
            var wrongBase = request.Clone();
            wrongBase.StateDelta.BaseSnapshotSha256 = ByteString.CopyFrom(new byte[32]);
            using (var fallback = service.Select(session, wrongBase, recipient, CancellationToken.None))
                Require(fallback.Status == HostHistoryResponseStatusV1.Rejected &&
                    fallback.ErrorCode == "NEEDS_FULL_SNAPSHOT" && fallback.Object == null,
                    "A wrong base must explicitly request a full snapshot.");
            var wrongBaseSequence = request.Clone();
            wrongBaseSequence.StateDelta.BaseNextSourceSequence++;
            RequireNeedsFull(service, session, wrongBaseSequence, recipient,
                "A different base sequence selected the retained snapshot.");
            var wrongBaseLength = request.Clone();
            wrongBaseLength.StateDelta.BaseSnapshotBytes++;
            RequireNeedsFull(service, session, wrongBaseLength, recipient,
                "A different base length selected the retained snapshot.");
            var wrongContext = request.Clone();
            wrongContext.StateDelta.ContextId = ByteString.CopyFrom(new byte[32]);
            RequireNeedsFull(service, session, wrongContext, recipient,
                "A different context selected the retained snapshot.");
            var otherState = profile == TerminalSemanticProfile.Graphics
                ? TerminalSemanticProfile.GraphicsUnicode16 : TerminalSemanticProfile.Graphics;
            var otherContext = DeltaCrypto.DeriveContextId(session, Pc, otherState.StateContract);
            try
            {
                var wrongState = request.Clone();
                wrongState.StateDelta.ContextId = ByteString.CopyFrom(otherContext);
                RequireNeedsFull(service, session, wrongState, recipient,
                    "Another state contract selected this profile's Delta base.");
            }
            finally { CryptographicOperations.ZeroMemory(otherContext); }
            var wrongProfile = request.Clone();
            wrongProfile.StateDelta.Profile = profile == TerminalSemanticProfile.Graphics
                ? (uint)NativeTerminalDeltaProfile.Unicode16V1 : (uint)NativeTerminalDeltaProfile.LegacyV1;
            RequireNeedsFull(service, session, wrongProfile, recipient,
                "A different Delta profile selected the retained snapshot.");
            var wrongTarget = request.Clone();
            wrongTarget.SnapshotSeq = 1;
            using (var fallback = service.Select(session, wrongTarget, recipient, CancellationToken.None))
                Require(fallback.ErrorCode == "NEEDS_FULL_SNAPSHOT" && fallback.Object == null,
                    "A stale requested S must not silently select the latest snapshot.");
            var damagedCiphertext = (byte[])selected.Object.SealedObject.Clone();
            try
            {
                damagedCiphertext[^1] ^= 1;
                try
                {
                    var damaged = DeltaCrypto.Open(key, damagedCiphertext, session, Pc, Phone, 3,
                        profile.StateContract, contextId, metadata.Profile,
                        metadata.BaseNextSourceSequence, metadata.TargetNextSourceSequence,
                        metadata.BaseSnapshotBytes, metadata.TargetSnapshotBytes,
                        metadata.BaseSnapshotSha256.ToByteArray(), metadata.TargetSnapshotSha256.ToByteArray(),
                        pair.Previous.ObservedLocalInputEpoch);
                    CryptographicOperations.ZeroMemory(damaged.Delta);
                    throw new InvalidOperationException("A damaged Delta ciphertext was accepted.");
                }
                catch (CryptographicException) { }
            }
            finally { CryptographicOperations.ZeroMemory(damagedCiphertext); }
            producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 4,
                Encoding.ASCII.GetBytes("next")));
            Require(producer.ProcessSession(session).SnapshotEligible,
                "A newer Delta target fixture was ineligible.");
            using (var latest = store.ReadDeltaSnapshotPair(session, 0))
                Require(latest?.Current?.SnapshotSequence == 4 &&
                    latest.Previous?.SnapshotSequence == 3,
                    "A newer snapshot did not evict the older Delta base.");
            Require(store.ReadDeltaSnapshotPair(session, 3) == null,
                "A stale target selected a newer Delta baseline.");
            RequireNeedsFull(service, session, request, recipient,
                "An evicted base was reused after the next snapshot.");
            var restartedStore = new ProductionTerminalHistoryStore(root, Pc);
            using (var recovered = restartedStore.ReadDeltaSnapshotPair(session, 0))
                Require(recovered?.Current?.SnapshotSequence == 4 &&
                    recovered.Previous?.SnapshotSequence == 3,
                    "Restart lost the exact current and previous Delta snapshots.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { using var ignored = service.Select(session, request, recipient, cancelled.Token); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("A cancelled Delta request reached snapshot selection.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(contextId);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            var resolvedRoot = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolvedRoot) != temporaryRoot.TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(resolvedRoot).StartsWith("termlink-state-delta-", StringComparison.Ordinal))
                throw new InvalidOperationException("State Delta test cleanup path escaped its temporary root.");
            if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, true);
        }
    }

    private static void RequireNeedsFull(ProductionStateDeltaService service, Guid session,
        HostHistoryRequestPayload request, ProductionHistoryRecipient recipient, string message)
    {
        using var rejected = service.Select(session, request, recipient, CancellationToken.None);
        Require(rejected.Status == HostHistoryResponseStatusV1.Rejected &&
            rejected.ErrorCode == "NEEDS_FULL_SNAPSHOT" && rejected.Object == null, message);
    }

    private static void ExpectArgument(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
