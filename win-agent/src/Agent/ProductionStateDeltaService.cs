using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

namespace TermLink.Agent;

// Selects one bounded, recipient-bound Delta from two durable exact snapshots.
// No Delta is retained or sent until Host History has accepted the request.
internal sealed class ProductionStateDeltaService
{
    private const int MaximumCombinedSnapshotBytes = 64 * 1024 * 1024;
    private readonly ProductionTerminalHistoryStore store;
    private readonly Guid pcDeviceId;

    internal ProductionStateDeltaService(ProductionTerminalHistoryStore store, Guid pcDeviceId)
    { this.store = store; this.pcDeviceId = pcDeviceId; }

    internal ProductionHostHistorySelection Select(Guid sessionId, HostHistoryRequestPayload request,
        ProductionHistoryRecipient recipient, CancellationToken cancellationToken)
    {
        if (request.StateDelta == null || request.RequestId.Length != 16 ||
            request.BeforeLineId != 0 || request.AfterSeq != 0 || request.ThroughSeq != 0 ||
            request.MaxEvents != 0 || request.FrozenHistory != null ||
            recipient.DeviceId == Guid.Empty || recipient.DeviceId == pcDeviceId ||
            recipient.PairwiseKey?.Length != 32 || recipient.KeyEpoch < 1)
            return Reject("INVALID_REQUEST");
        if (!request.SubscriberCapabilities.Contains("state_delta_v1"))
            return Reject("NEEDS_FULL_SNAPSHOT");
        var input = request.StateDelta;
        if (input.ContextId.Length != 32 || input.BaseSnapshotSha256.Length != 32 ||
            input.Profile is < 1 or > 4 || input.BaseNextSourceSequence == 0 ||
            input.BaseSnapshotBytes == 0) return Reject("INVALID_REQUEST");
        cancellationToken.ThrowIfCancellationRequested();
        var profile = store.GetSessionProfile(sessionId);
        if (!profile.IsGraphics || !NativeTerminalDelta.SupportsV1())
            return Reject("NEEDS_FULL_SNAPSHOT");
        var nativeProfile = profile == TerminalSemanticProfile.Graphics
            ? NativeTerminalDeltaProfile.LegacyV1
            : profile == TerminalSemanticProfile.GraphicsUnicode16
                ? NativeTerminalDeltaProfile.Unicode16V1
                : throw new TerminalHistoryProfileException(TerminalSessionProfile.Mismatch);
        var contextId = DeltaCrypto.DeriveContextId(sessionId, pcDeviceId, profile.StateContract);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(contextId, input.ContextId.Span) ||
                input.Profile != (uint)nativeProfile)
                return Reject("NEEDS_FULL_SNAPSHOT");
            using var pair = store.ReadDeltaSnapshotPair(sessionId, request.SnapshotSeq);
            if (pair?.Current == null || pair.Previous == null)
                return Reject("NEEDS_FULL_SNAPSHOT");
            var current = pair.Current;
            var previous = pair.Previous;
            if ((long)current.Plaintext.Length + previous.Plaintext.Length > MaximumCombinedSnapshotBytes)
                return Reject("NEEDS_FULL_SNAPSHOT");
            if (current.ObservedLocalInputEpoch < previous.ObservedLocalInputEpoch)
                throw new InvalidDataException("Delta target local input epoch precedes its base.");
            if (current.SnapshotSequence == ulong.MaxValue ||
                previous.FormatName != SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatName ||
                previous.FormatVersion != SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatVersion ||
                previous.SnapshotSequence >= current.SnapshotSequence ||
                input.BaseNextSourceSequence != previous.SnapshotSequence + 1 ||
                input.BaseSnapshotBytes != (ulong)previous.Plaintext.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(previous.Plaintext), input.BaseSnapshotSha256.Span))
                return Reject("NEEDS_FULL_SNAPSHOT");
            cancellationToken.ThrowIfCancellationRequested();
            var context = new NativeTerminalDeltaContext(contextId, nativeProfile);
            using var baseTerminal = NativeTerminalCoreV2.ImportGraphicsSnapshot(previous.Plaintext);
            using var targetTerminal = NativeTerminalCoreV2.ImportGraphicsSnapshot(current.Plaintext);
            using var baseCheckpoint = baseTerminal.CaptureCheckpoint();
            using var targetCheckpoint = targetTerminal.CaptureCheckpoint();
            var baseState = baseCheckpoint.GetDeltaState(context);
            var targetState = targetCheckpoint.GetDeltaState(context);
            if (baseState.NextSourceSequence != previous.SnapshotSequence + 1 ||
                targetState.NextSourceSequence != current.SnapshotSequence + 1 ||
                baseState.SnapshotBytes != (ulong)previous.Plaintext.Length ||
                targetState.SnapshotBytes != (ulong)current.Plaintext.Length ||
                !CryptographicOperations.FixedTimeEquals(baseState.Sha256,
                    input.BaseSnapshotSha256.Span) ||
                !CryptographicOperations.FixedTimeEquals(targetState.Sha256,
                    SHA256.HashData(current.Plaintext)))
                throw new InvalidDataException("Durable Delta snapshots disagree with native checkpoint identity.");
            byte[] delta = null;
            byte[] sealedObject = null;
            try
            {
                delta = baseCheckpoint.ExportDeltaTo(targetCheckpoint, context,
                    (ulong)DeltaCrypto.MaximumPlaintextBytes);
                cancellationToken.ThrowIfCancellationRequested();
                if (delta.Length + DeltaCrypto.HeaderBytes + 16 >= current.Plaintext.Length)
                    return Reject("NEEDS_FULL_SNAPSHOT");
                sealedObject = DeltaCrypto.Seal(recipient.PairwiseKey, sessionId, pcDeviceId,
                    recipient.DeviceId, recipient.KeyEpoch, profile.StateContract, contextId,
                    input.Profile, baseState.NextSourceSequence, targetState.NextSourceSequence,
                    baseState.SnapshotBytes, targetState.SnapshotBytes,
                    baseState.Sha256, targetState.Sha256, current.ObservedLocalInputEpoch,
                    current.InitialTranscriptBeforeLineId, delta);
                cancellationToken.ThrowIfCancellationRequested();
                if (sealedObject.Length > DeltaCrypto.MaximumObjectBytes)
                    return Reject("NEEDS_FULL_SNAPSHOT");
                var metadata = new StateDeltaMetadataV1
                {
                    ContextId = ByteString.CopyFrom(contextId), Profile = input.Profile,
                    BaseNextSourceSequence = baseState.NextSourceSequence,
                    TargetNextSourceSequence = targetState.NextSourceSequence,
                    BaseSnapshotBytes = baseState.SnapshotBytes,
                    TargetSnapshotBytes = targetState.SnapshotBytes,
                    BaseSnapshotSha256 = ByteString.CopyFrom(baseState.Sha256),
                    TargetSnapshotSha256 = ByteString.CopyFrom(targetState.Sha256)
                };
                var result = new ProductionHostHistorySelection
                {
                    Status = HostHistoryResponseStatusV1.Ok, StateDelta = metadata,
                    Object = new ProductionHostHistoryObject
                    {
                        Kind = HostHistoryObjectKindV1.StateDelta,
                        ObjectId = Guid.NewGuid(), SnapshotSequence = current.SnapshotSequence,
                        KeyId = DeltaCrypto.KeyId(recipient.DeviceId, recipient.KeyEpoch, profile.StateContract),
                        FormatName = DeltaCrypto.FormatName,
                        FormatVersion = (int)DeltaCrypto.FormatVersion,
                        SealedObject = sealedObject
                    }
                };
                sealedObject = null;
                return result;
            }
            finally
            {
                if (delta != null) CryptographicOperations.ZeroMemory(delta);
                if (sealedObject != null) CryptographicOperations.ZeroMemory(sealedObject);
            }
        }
        catch (NativeTerminalException error) when (error.Status == NativeTerminalStatus.NeedsFullSnapshot ||
            error.Status == NativeTerminalStatus.LimitExceeded ||
            error.Status == NativeTerminalStatus.IncompatibleDelta)
        { return Reject("NEEDS_FULL_SNAPSHOT"); }
        catch (NativeTerminalException)
        { return Reject("INVALID_HISTORY_STATE"); }
        catch (NativeTerminalAbiException)
        { return Reject("INVALID_HISTORY_STATE"); }
        finally { CryptographicOperations.ZeroMemory(contextId); }
    }

    private static ProductionHostHistorySelection Reject(string code) => new()
    { Status = HostHistoryResponseStatusV1.Rejected, ErrorCode = code };
}
