using System;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal static class ProductionHostHistoryResponseBuilder
    {
        public static HostHistoryResponsePayload Build(
            ByteString requestId,
            ProductionHostHistorySelection selection,
            int chunkIndex,
            int chunkCount,
            byte[] objectDigest,
            int objectOffset,
            int objectCount,
            HostHistoryRequestKindV1 requestKind = HostHistoryRequestKindV1.Unspecified,
            FrozenHistoryMetadata frozenHistory = null,
            ulong requestedSnapshotSequence = 0)
        {
            if (requestId == null || requestId.Length != 16)
                throw new ArgumentException("Host History request ID must contain 16 bytes.", nameof(requestId));
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            if (objectDigest == null) throw new ArgumentNullException(nameof(objectDigest));

            var historyObject = selection.Object;
            var frozenPage = requestKind == HostHistoryRequestKindV1.FrozenBegin ||
                requestKind == HostHistoryRequestKindV1.FrozenPage;
            var frozenRelease = requestKind == HostHistoryRequestKindV1.FrozenRelease;
            var stateDelta = requestKind == HostHistoryRequestKindV1.StateDelta;
            var delta = selection.StateDelta;
            var brokerRequest = requestKind == HostHistoryRequestKindV1.BrokerCapture;
            var broker = selection.BrokerCapture;
            var availableSnapshotSequence = selection.AvailableSnapshotSequence;
            if (broker != null &&
                (!brokerRequest || selection.Status != HostHistoryResponseStatusV1.Ok ||
                 historyObject?.Kind != HostHistoryObjectKindV1.BrokerCapture ||
                 broker.ObjectId.Length != 16 || broker.SourceEventSeq is 0 or >= long.MaxValue ||
                 broker.CanonicalBytes is < 136 or > BrokerCaptureCrypto.MaximumCanonicalBytes ||
                 broker.CanonicalSha256.Length != 32 ||
                 historyObject.ObjectId == Guid.Empty ||
                 historyObject.ObjectId != Guid.ParseExact(
                     Convert.ToHexString(broker.ObjectId.Span), "N") ||
                 historyObject.StartSequence != broker.SourceEventSeq ||
                 historyObject.EndSequence != broker.SourceEventSeq ||
                 historyObject.SnapshotSequence != 0 || historyObject.Columns != 0 ||
                 historyObject.Rows != 0 || historyObject.InitialTranscriptBeforeLineId != 0 ||
                 historyObject.RequestBeforeLineId != 0 || historyObject.FirstLineId != 0 ||
                 historyObject.LastLineId != 0 || historyObject.HasMoreBefore ||
                 historyObject.NextBeforeLineId != 0 || historyObject.HasMoreEvents))
                throw new ArgumentException("Broker capture response selection is invalid.");
            if (brokerRequest && selection.Status == HostHistoryResponseStatusV1.Ok &&
                (broker == null || historyObject?.Kind != HostHistoryObjectKindV1.BrokerCapture))
                throw new ArgumentException("A successful Broker capture response needs its object and metadata.");
            if (historyObject?.Kind == HostHistoryObjectKindV1.BrokerCapture &&
                (!brokerRequest || broker == null || historyObject.SealedObject == null ||
                 historyObject.SealedObject.Length !=
                     (long)BrokerCaptureCrypto.HeaderBytes + broker.CanonicalBytes + 16 ||
                 historyObject.SealedObject.Length > BrokerCaptureCrypto.MaximumObjectBytes ||
                 historyObject.FormatName != "TERMLINK_BROKER_CAPTURE_V1" ||
                 historyObject.FormatVersion != 1 ||
                 chunkCount is < 1 or > 65 ||
                 chunkCount != (historyObject.SealedObject.Length + 512 * 1024 - 1) / (512 * 1024) ||
                 chunkIndex < 0 || chunkIndex >= chunkCount || objectOffset < 0 ||
                 objectOffset != chunkIndex * 512 * 1024 ||
                 objectCount != Math.Min(512 * 1024,
                     historyObject.SealedObject.Length - objectOffset)))
                throw new ArgumentException("Broker capture object bounds or format are invalid.");
            if (delta != null &&
                (!stateDelta || selection.Status != HostHistoryResponseStatusV1.Ok ||
                 historyObject?.Kind != HostHistoryObjectKindV1.StateDelta ||
                 delta.ContextId.Length != 32 || delta.Profile is < 1 or > 4 ||
                 delta.BaseSnapshotSha256.Length != 32 ||
                 delta.TargetSnapshotSha256.Length != 32 ||
                 delta.BaseNextSourceSequence == 0 ||
                 delta.TargetNextSourceSequence <= delta.BaseNextSourceSequence ||
                 delta.BaseSnapshotBytes == 0 || delta.TargetSnapshotBytes == 0 ||
                 historyObject.SnapshotSequence == ulong.MaxValue ||
                 delta.TargetNextSourceSequence != historyObject.SnapshotSequence + 1 ||
                 (requestedSnapshotSequence != 0 &&
                  requestedSnapshotSequence != historyObject.SnapshotSequence)))
                throw new ArgumentException("State Delta response metadata is invalid.");
            if (stateDelta && selection.Status == HostHistoryResponseStatusV1.Ok &&
                (delta == null || historyObject?.Kind != HostHistoryObjectKindV1.StateDelta))
                throw new ArgumentException("A successful State Delta response needs its object and metadata.");
            if (historyObject?.Kind == HostHistoryObjectKindV1.StateDelta &&
                (!stateDelta || delta == null || historyObject.SealedObject == null ||
                 selection.Status != HostHistoryResponseStatusV1.Ok ||
                 historyObject.ObjectId == Guid.Empty ||
                 historyObject.SealedObject.Length < DeltaCrypto.HeaderBytes + 256 + 16 ||
                 historyObject.SealedObject.Length > DeltaCrypto.MaximumObjectBytes ||
                 historyObject.FormatName != DeltaCrypto.FormatName ||
                 historyObject.FormatVersion != DeltaCrypto.FormatVersion ||
                 historyObject.Columns != 0 || historyObject.Rows != 0 ||
                 historyObject.InitialTranscriptBeforeLineId != 0 ||
                 chunkCount is < 1 or > 16 ||
                 chunkCount != (historyObject.SealedObject.Length + 512 * 1024 - 1) / (512 * 1024) ||
                 chunkIndex < 0 || chunkIndex >= chunkCount || objectOffset < 0 ||
                 objectOffset != chunkIndex * 512 * 1024 ||
                 objectCount != Math.Min(512 * 1024,
                     historyObject.SealedObject.Length - objectOffset)))
                throw new ArgumentException("State Delta object bounds or format are invalid.");
            if (availableSnapshotSequence != 0 &&
                (requestKind != HostHistoryRequestKindV1.FrozenBegin ||
                 selection.Status != HostHistoryResponseStatusV1.Rejected ||
                 selection.ErrorCode != "ANCHOR_CHANGED" || historyObject != null ||
                 frozenHistory != null || requestedSnapshotSequence == 0 ||
                 availableSnapshotSequence <= requestedSnapshotSequence))
                throw new ArgumentException("An available snapshot is only valid for an older frozen BEGIN anchor.");
            if ((frozenPage || frozenRelease) && selection.Status == HostHistoryResponseStatusV1.Ok &&
                frozenHistory == null)
                throw new ArgumentException("A successful frozen history response requires capture metadata.");
            if (frozenHistory != null &&
                (!frozenPage && !frozenRelease || frozenHistory.Token.Length != 32 ||
                 frozenHistory.CaptureId == 0 || frozenHistory.CaptureRevision == 0 ||
                 frozenHistory.SnapshotSeq == 0 ||
                 string.IsNullOrWhiteSpace(frozenHistory.StateContract)))
                throw new ArgumentException("Frozen history capture metadata is invalid.");
            if (frozenRelease && historyObject != null)
                throw new ArgumentException("Frozen history RELEASE cannot return an object.");
            if (historyObject?.Kind == HostHistoryObjectKindV1.FrozenPage)
            {
                if (!frozenPage || frozenHistory == null || selection.Status != HostHistoryResponseStatusV1.Ok ||
                    historyObject.RequestBeforeLineId == 0 ||
                    historyObject.SnapshotSequence == 0 ||
                    historyObject.SnapshotSequence == ulong.MaxValue ||
                    frozenHistory.SnapshotSeq != historyObject.SnapshotSequence ||
                    frozenHistory.CaptureRevision != historyObject.SnapshotSequence + 1 ||
                    (requestKind == HostHistoryRequestKindV1.FrozenBegin &&
                     historyObject.RequestBeforeLineId != ulong.MaxValue))
                    throw new ArgumentException("Frozen history page selection is invalid.");
            }
            else if (frozenPage && selection.Status == HostHistoryResponseStatusV1.Ok)
                throw new ArgumentException("Frozen history BEGIN/PAGE must return a frozen page object.");
            if (historyObject == null)
            {
                if (selection.Status == HostHistoryResponseStatusV1.Ok && !frozenRelease)
                    throw new ArgumentException("Only frozen history RELEASE may return an empty OK response.");
                // Error/empty responses still occupy one transport Envelope, but
                // the protocol declares that they contain zero object chunks.
                if (chunkIndex != 0 || chunkCount != 0 || objectDigest.Length != 0 ||
                    objectOffset != 0 || objectCount != 0)
                {
                    throw new ArgumentException("An empty Host History response cannot declare object chunks.");
                }
            }
            else
            {
                if (historyObject.SealedObject == null || chunkCount < 1 ||
                    chunkIndex < 0 || chunkIndex >= chunkCount ||
                    objectDigest.Length != 32 || objectOffset < 0 || objectCount < 0 ||
                    objectOffset > historyObject.SealedObject.Length - objectCount)
                {
                    throw new ArgumentException("Host History object chunk metadata is invalid.");
                }
            }

            var response = new HostHistoryResponsePayload
            {
                RequestId = requestId,
                Status = selection.Status,
                Kind = historyObject?.Kind ?? HostHistoryObjectKindV1.Unspecified,
                ObjectId = historyObject == null
                    ? ByteString.Empty
                    : ByteString.CopyFrom(GuidBytes(historyObject.ObjectId)),
                SnapshotSeq = historyObject?.SnapshotSequence ?? availableSnapshotSequence,
                Columns = checked((uint)(historyObject?.Columns ?? 0)),
                Rows = checked((uint)(historyObject?.Rows ?? 0)),
                InitialTranscriptBeforeLineId = historyObject?.InitialTranscriptBeforeLineId ?? 0,
                RequestBeforeLineId = historyObject?.RequestBeforeLineId ?? 0,
                FirstLineId = historyObject?.FirstLineId ?? 0,
                LastLineId = historyObject?.LastLineId ?? 0,
                HasMoreBefore = historyObject?.HasMoreBefore ?? false,
                NextBeforeLineId = historyObject?.NextBeforeLineId ?? 0,
                StartSeq = historyObject?.StartSequence ?? 0,
                EndSeq = historyObject?.EndSequence ?? 0,
                HasMoreEvents = historyObject?.HasMoreEvents ?? false,
                KeyId = historyObject?.KeyId ?? string.Empty,
                FormatName = historyObject?.FormatName ?? string.Empty,
                FormatVersion = checked((uint)(historyObject?.FormatVersion ?? 0)),
                ObjectSha256 = ByteString.CopyFrom(objectDigest),
                ObjectBytes = checked((ulong)(historyObject?.SealedObject?.LongLength ?? 0)),
                ChunkIndex = checked((uint)chunkIndex),
                ChunkCount = checked((uint)chunkCount),
                ErrorCode = selection.ErrorCode ?? string.Empty,
                FrozenHistory = frozenHistory?.Clone(),
                StateDelta = delta?.Clone(),
                BrokerCapture = broker?.Clone()
            };
            if (objectCount > 0)
            {
                response.ObjectChunk = ByteString.CopyFrom(
                    historyObject.SealedObject,
                    objectOffset,
                    objectCount);
            }
            return response;
        }

        private static byte[] GuidBytes(Guid value) =>
            Convert.FromHexString(value.ToString("N"));
    }
}
