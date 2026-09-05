using System;
using Google.Protobuf;
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
            int objectCount)
        {
            if (requestId == null || requestId.Length != 16)
                throw new ArgumentException("Host History request ID must contain 16 bytes.", nameof(requestId));
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            if (objectDigest == null) throw new ArgumentNullException(nameof(objectDigest));

            var historyObject = selection.Object;
            if (historyObject == null)
            {
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
                SnapshotSeq = historyObject?.SnapshotSequence ?? 0,
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
                ErrorCode = selection.ErrorCode ?? string.Empty
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
