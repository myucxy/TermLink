using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal sealed class ProductionHostHistoryObject : IDisposable
    {
        public HostHistoryObjectKindV1 Kind { get; set; }
        public Guid ObjectId { get; set; }
        public ulong SnapshotSequence { get; set; }
        public int Columns { get; set; }
        public int Rows { get; set; }
        public ulong InitialTranscriptBeforeLineId { get; set; }
        public ulong RequestBeforeLineId { get; set; }
        public ulong FirstLineId { get; set; }
        public ulong LastLineId { get; set; }
        public bool HasMoreBefore { get; set; }
        public ulong NextBeforeLineId { get; set; }
        public ulong StartSequence { get; set; }
        public ulong EndSequence { get; set; }
        public bool HasMoreEvents { get; set; }
        public string KeyId { get; set; }
        public string FormatName { get; set; }
        public int FormatVersion { get; set; }
        public byte[] SealedObject { get; set; }

        public void Dispose()
        {
            if (SealedObject != null) CryptographicOperations.ZeroMemory(SealedObject);
            SealedObject = null;
        }
    }

    internal sealed class ProductionHostHistorySelection : IDisposable
    {
        public HostHistoryResponseStatusV1 Status { get; set; }
        public string ErrorCode { get; set; }
        public ProductionHostHistoryObject Object { get; set; }

        public void Dispose()
        {
            Object?.Dispose();
            Object = null;
        }
    }

    internal sealed class ProductionHostHistoryService
    {
        internal const int DefaultMaximumEvents = 512;
        internal const int MaximumEvents = 4096;
        internal const int MaximumSealedObjectBytes = 64 * 1024 * 1024;
        private const int MaximumDecodedEventPageBytes = MaximumSealedObjectBytes / 2;
        private readonly ProductionTerminalHistoryStore store;
        private readonly ProductionTerminalHistoryProducer producer;
        private readonly Guid pcDeviceId;
        private readonly Func<Guid, bool> isDestroyed;

        public ProductionHostHistoryService(
            ProductionTerminalHistoryStore store,
            ProductionTerminalHistoryProducer producer,
            Guid pcDeviceId,
            Func<Guid, bool> isDestroyed)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.producer = producer ?? throw new ArgumentNullException(nameof(producer));
            if (pcDeviceId == Guid.Empty) throw new ArgumentException("PC device ID is required.", nameof(pcDeviceId));
            this.pcDeviceId = pcDeviceId;
            this.isDestroyed = isDestroyed ?? throw new ArgumentNullException(nameof(isDestroyed));
        }

        public ProductionHostHistorySelection Select(
            Guid sessionId,
            HostHistoryRequestPayload request,
            ProductionHistoryRecipient recipient)
        {
            if (sessionId == Guid.Empty || request == null || recipient == null)
                return Rejected("INVALID_REQUEST");
            if (isDestroyed(sessionId)) return NotFound("SESSION_HISTORY_DESTROYED");
            try
            {
                return request.Kind switch
                {
                    HostHistoryRequestKindV1.LatestSnapshot => SelectSnapshot(sessionId, recipient),
                    HostHistoryRequestKindV1.TranscriptPage => SelectTranscript(sessionId, request, recipient),
                    HostHistoryRequestKindV1.EventRange => SelectEvents(sessionId, request, recipient),
                    _ => Rejected("INVALID_HISTORY_KIND")
                };
            }
            catch (InvalidDataException)
            {
                return Rejected("INVALID_HISTORY_STATE");
            }
            catch (ArgumentException)
            {
                return Rejected("INVALID_REQUEST");
            }
        }

        private ProductionHostHistorySelection SelectSnapshot(
            Guid sessionId,
            ProductionHistoryRecipient recipient)
        {
            var prepared = producer.PrepareLatestSnapshot(sessionId, recipient);
            if (prepared == null) return NotReady("SNAPSHOT_NOT_READY");
            return PreparedArtifact(prepared, HostHistoryObjectKindV1.Snapshot, 0);
        }

        private ProductionHostHistorySelection SelectTranscript(
            Guid sessionId,
            HostHistoryRequestPayload request,
            ProductionHistoryRecipient recipient)
        {
            if (request.SnapshotSeq == 0 || request.BeforeLineId == 0)
                return Rejected("INVALID_TRANSCRIPT_CURSOR");
            var prepared = producer.PrepareTranscriptPage(
                sessionId,
                recipient,
                request.SnapshotSeq,
                request.BeforeLineId);
            if (prepared == null) return NotFound("TRANSCRIPT_PAGE_NOT_FOUND");
            return PreparedArtifact(
                prepared,
                HostHistoryObjectKindV1.Transcript,
                request.BeforeLineId);
        }

        private ProductionHostHistorySelection SelectEvents(
            Guid sessionId,
            HostHistoryRequestPayload request,
            ProductionHistoryRecipient recipient)
        {
            if (request.ThroughSeq == 0 || request.ThroughSeq <= request.AfterSeq)
                return Rejected("INVALID_EVENT_RANGE");
            var maximumEvents = request.MaxEvents == 0
                ? DefaultMaximumEvents
                : checked((int)Math.Min(request.MaxEvents, (uint)MaximumEvents));
            var frames = store.ReadContiguousFrames(
                sessionId,
                request.AfterSeq,
                request.ThroughSeq,
                maximumEvents,
                MaximumDecodedEventPageBytes,
                out var gapDetected);
            if (gapDetected) return Gap("EVENT_RANGE_GAP");
            if (frames.Count == 0) return NotReady("EVENT_RANGE_NOT_READY");
            var expected = checked(request.AfterSeq + 1);

            var events = new List<HistoryEventV1>(Math.Min(maximumEvents, frames.Count));
            var calculatedBytes = 0;
            foreach (var frame in frames)
            {
                if (frame.Sequence != expected) break;
                var historyEvent = ProductionAgentRuntime.BuildHistoryEvent(frame);
                if (historyEvent == null) break;
                var eventBytes = historyEvent.CalculateSize();
                if (events.Count > 0 &&
                    (events.Count >= maximumEvents || calculatedBytes + eventBytes > MaximumSealedObjectBytes / 2))
                {
                    break;
                }
                events.Add(historyEvent);
                calculatedBytes = checked(calculatedBytes + eventBytes);
                expected = checked(expected + 1);
            }
            if (events.Count == 0) return NotFound("EVENT_RANGE_EMPTY");

            var plaintext = new HistoryChunkV1 { Events = { events } }.ToByteArray();
            var plaintextDigest = SHA256.HashData(plaintext);
            byte[] sealedObject;
            try
            {
                sealedObject = store.GetOrCreateSealedChunk(
                    sessionId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    events[0].Seq,
                    events[^1].Seq,
                    plaintextDigest,
                    () => HistoryCrypto.Seal(
                        recipient.PairwiseKey,
                        sessionId,
                        pcDeviceId,
                        recipient.DeviceId,
                        recipient.KeyEpoch,
                        events));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(plaintextDigest);
            }
            if (sealedObject.Length > MaximumSealedObjectBytes)
            {
                CryptographicOperations.ZeroMemory(sealedObject);
                return Rejected("EVENT_RANGE_TOO_LARGE");
            }
            var start = events[0].Seq;
            var end = events[^1].Seq;
            return Ready(new ProductionHostHistoryObject
            {
                Kind = HostHistoryObjectKindV1.EventChunk,
                ObjectId = StableObjectId("event", sessionId, start, end, recipient.DeviceId, recipient.KeyEpoch),
                StartSequence = start,
                EndSequence = end,
                HasMoreEvents = end < request.ThroughSeq,
                KeyId = HistoryCrypto.KeyId(recipient.DeviceId, recipient.KeyEpoch),
                FormatName = "BOUNDED_EVENT_REPLAY",
                FormatVersion = 1,
                SealedObject = sealedObject
            });
        }

        private static ProductionHostHistorySelection PreparedArtifact(
            ProductionTerminalHistoryPreparedUpload prepared,
            HostHistoryObjectKindV1 kind,
            ulong requestBeforeLineId)
        {
            var artifact = prepared.Artifact;
            return Ready(new ProductionHostHistoryObject
            {
                Kind = kind,
                ObjectId = StableObjectId(
                    artifact.ArtifactId,
                    prepared.SessionId,
                    artifact.SnapshotSequence,
                    artifact.FirstLineId,
                    prepared.RecipientDeviceId,
                    prepared.KeyEpoch),
                SnapshotSequence = artifact.SnapshotSequence,
                Columns = artifact.Columns,
                Rows = artifact.Rows,
                InitialTranscriptBeforeLineId = artifact.InitialTranscriptBeforeLineId,
                RequestBeforeLineId = requestBeforeLineId,
                FirstLineId = artifact.FirstLineId,
                LastLineId = artifact.LastLineId,
                HasMoreBefore = artifact.HasMoreBefore,
                NextBeforeLineId = artifact.NextBeforeLineId,
                KeyId = kind == HostHistoryObjectKindV1.Snapshot
                    ? SnapshotKeyId(artifact, prepared.RecipientDeviceId, prepared.KeyEpoch)
                    : SnapshotCrypto.TranscriptKeyId(prepared.RecipientDeviceId, prepared.KeyEpoch),
                FormatName = artifact.FormatName,
                FormatVersion = artifact.FormatVersion,
                SealedObject = prepared.SealedObject
            });
        }

        private static string SnapshotKeyId(
            ProductionTerminalHistoryArtifact artifact,
            Guid recipientDeviceId,
            long keyEpoch)
        {
            return artifact.FormatName == SnapshotCrypto.TerminalCoreSnapshotFormatName
                ? SnapshotCrypto.TerminalCoreSnapshotKeyId(recipientDeviceId, keyEpoch)
                : SnapshotCrypto.SnapshotKeyId(recipientDeviceId, keyEpoch);
        }

        private static Guid StableObjectId(
            string kind,
            Guid sessionId,
            ulong first,
            ulong last,
            Guid recipientDeviceId,
            long keyEpoch)
        {
            var input = Encoding.UTF8.GetBytes(
                "TermLink host history V1\n" + kind + "\n" + sessionId.ToString("N") + "\n" +
                first.ToString(CultureInfo.InvariantCulture) + "\n" +
                last.ToString(CultureInfo.InvariantCulture) + "\n" +
                recipientDeviceId.ToString("N") + "\n" +
                keyEpoch.ToString(CultureInfo.InvariantCulture));
            try
            {
                var digest = SHA256.HashData(input);
                try { return Guid.ParseExact(Convert.ToHexString(digest, 0, 16), "N"); }
                finally { CryptographicOperations.ZeroMemory(digest); }
            }
            finally { CryptographicOperations.ZeroMemory(input); }
        }

        private static ProductionHostHistorySelection Ready(ProductionHostHistoryObject value) =>
            new ProductionHostHistorySelection
            {
                Status = HostHistoryResponseStatusV1.Ok,
                Object = value
            };

        private static ProductionHostHistorySelection NotReady(string code) =>
            new ProductionHostHistorySelection
            {
                Status = HostHistoryResponseStatusV1.NotReady,
                ErrorCode = code
            };

        private static ProductionHostHistorySelection NotFound(string code) =>
            new ProductionHostHistorySelection
            {
                Status = HostHistoryResponseStatusV1.NotFound,
                ErrorCode = code
            };

        private static ProductionHostHistorySelection Gap(string code) =>
            new ProductionHostHistorySelection
            {
                Status = HostHistoryResponseStatusV1.Gap,
                ErrorCode = code
            };

        private static ProductionHostHistorySelection Rejected(string code) =>
            new ProductionHostHistorySelection
            {
                Status = HostHistoryResponseStatusV1.Rejected,
                ErrorCode = code
            };
    }
}
