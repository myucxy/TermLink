using System;
using System.Linq;
using System.Security.Cryptography;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.ProductionTerminalHistory.Validation
{
    internal static class FrozenHistoryWireValidation
    {
        public static void Run()
        {
            Require((int)HostHistoryRequestKindV1.LatestSnapshot == 1 &&
                (int)HostHistoryRequestKindV1.TranscriptPage == 2 &&
                (int)HostHistoryRequestKindV1.EventRange == 3 &&
                (int)HostHistoryRequestKindV1.FrozenBegin == 4 &&
                (int)HostHistoryRequestKindV1.FrozenPage == 5 &&
                (int)HostHistoryRequestKindV1.FrozenRelease == 6,
                "Host history request kind wire values changed.");
            Require((int)HostHistoryObjectKindV1.Snapshot == 1 &&
                (int)HostHistoryObjectKindV1.Transcript == 2 &&
                (int)HostHistoryObjectKindV1.EventChunk == 3 &&
                (int)HostHistoryObjectKindV1.FrozenPage == 4,
                "Host history object kind wire values changed.");
            Require(HostHistoryRequestPayload.Descriptor.FindFieldByName("frozen_history").FieldNumber == 9 &&
                HostHistoryResponsePayload.Descriptor.FindFieldByName("frozen_history").FieldNumber == 26,
                "Frozen history field numbers changed.");
            Require(FrozenHistoryRequest.Descriptor.FindFieldByName("token").FieldNumber == 1 &&
                FrozenHistoryRequest.Descriptor.FindFieldByName("capture_id").FieldNumber == 2 &&
                FrozenHistoryRequest.Descriptor.FindFieldByName("capture_revision").FieldNumber == 3 &&
                FrozenHistoryRequest.Descriptor.FindFieldByName("max_lines").FieldNumber == 4 &&
                FrozenHistoryRequest.Descriptor.FindFieldByName("max_cells").FieldNumber == 5 &&
                FrozenHistoryRequest.Descriptor.FindFieldByName("max_utf8_bytes").FieldNumber == 6 &&
                FrozenHistoryRequest.Descriptor.FindFieldByName("page_format_version").FieldNumber == 7 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("token").FieldNumber == 1 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("capture_id").FieldNumber == 2 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("capture_revision").FieldNumber == 3 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("lease_remaining_ms").FieldNumber == 4 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("state_contract").FieldNumber == 5 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("head_truncated").FieldNumber == 6 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("required_cells").FieldNumber == 7 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("required_utf8_bytes").FieldNumber == 8 &&
                FrozenHistoryMetadata.Descriptor.FindFieldByName("snapshot_seq").FieldNumber == 9,
                "Frozen history nested field numbers changed.");
            var containerFields = new[]
            {
                "container_version", "session_id", "pc_device_id", "recipient_device_id",
                "key_epoch", "key_id", "token", "capture_id", "capture_revision",
                "snapshot_seq", "request_before_line_id", "max_lines", "max_cells",
                "max_utf8_bytes", "page_format_version", "plaintext_bytes", "nonce",
                "ciphertext", "first_line_id", "last_line_id", "has_more_before",
                "next_before_line_id"
            };
            for (var index = 0; index < containerFields.Length; index++)
                Require(FrozenHistoryCiphertextV1.Descriptor.FindFieldByName(containerFields[index])
                    .FieldNumber == index + 1,
                    "Frozen history ciphertext container field numbers changed.");
            const string containerBoundaryVector = "0801B00107";
            Require(Convert.ToHexString(new FrozenHistoryCiphertextV1
                {
                    ContainerVersion = 1,
                    NextBeforeLineId = 7
                }.ToByteArray()) == containerBoundaryVector,
                "Frozen history ciphertext container boundary vector changed.");

            const string oldRequest = "10011807";
            const string oldResponse = "100118012807";
            Require(Convert.ToHexString(HostHistoryRequestPayload.Parser.ParseFrom(
                Convert.FromHexString(oldRequest)).ToByteArray()) == oldRequest &&
                Convert.ToHexString(HostHistoryResponsePayload.Parser.ParseFrom(
                Convert.FromHexString(oldResponse)).ToByteArray()) == oldResponse,
                "Old Host History wire vectors changed.");
            var begin = new HostHistoryRequestPayload
            {
                Kind = HostHistoryRequestKindV1.FrozenBegin,
                SnapshotSeq = 7,
                BeforeLineId = ulong.MaxValue,
                FrozenHistory = new FrozenHistoryRequest
                {
                    MaxLines = 1, MaxCells = 2, MaxUtf8Bytes = 3, PageFormatVersion = 1
                }
            };
            const string beginVector = "1004180720FFFFFFFFFFFFFFFFFF014A082001280230033801";
            Require(Convert.ToHexString(begin.ToByteArray()) == beginVector &&
                HostHistoryRequestPayload.Parser.ParseFrom(Convert.FromHexString(beginVector))
                    .BeforeLineId == ulong.MaxValue,
                "Frozen BEGIN sentinel/vector changed.");
            Require(LegacyRequestCanSkipFrozenFields(begin.ToByteArray()),
                "A legacy protobuf reader cannot skip the appended frozen field.");

            var metadata = new FrozenHistoryMetadata
            {
                Token = ByteString.CopyFrom(Enumerable.Repeat((byte)0xA5, 32).ToArray()),
                CaptureId = 5,
                CaptureRevision = 8,
                SnapshotSeq = 7,
                LeaseRemainingMs = 300000,
                StateContract = "terminal_core_graphics_v1",
                HeadTruncated = true
            };
            using var selection = new ProductionHostHistorySelection
            {
                Status = HostHistoryResponseStatusV1.Ok,
                Object = new ProductionHostHistoryObject
                {
                    Kind = HostHistoryObjectKindV1.FrozenPage,
                    ObjectId = Guid.NewGuid(),
                    SnapshotSequence = 7,
                    RequestBeforeLineId = ulong.MaxValue,
                    FormatName = "TLHPV2",
                    FormatVersion = 2,
                    SealedObject = new byte[] { 1, 2, 3, 4 }
                }
            };
            var digest = SHA256.HashData(selection.Object.SealedObject);
            var requestId = ByteString.CopyFrom(new byte[16]);
            var first = ProductionHostHistoryResponseBuilder.Build(requestId, selection, 0, 2,
                digest, 0, 2, HostHistoryRequestKindV1.FrozenBegin, metadata);
            var second = ProductionHostHistoryResponseBuilder.Build(requestId, selection, 1, 2,
                digest, 2, 2, HostHistoryRequestKindV1.FrozenBegin, metadata);
            metadata.LeaseRemainingMs = 1;
            Require(first.FrozenHistory.Equals(second.FrozenHistory) &&
                first.FrozenHistory.LeaseRemainingMs == 300000 &&
                first.SnapshotSeq == 7 && first.RequestBeforeLineId == ulong.MaxValue &&
                first.ChunkIndex == 0 && second.ChunkIndex == 1 &&
                first.ObjectSha256.Equals(second.ObjectSha256) &&
                HostHistoryResponsePayload.Parser.ParseFrom(first.ToByteArray()).FrozenHistory
                    .Token.Length == 32,
                "Frozen metadata changed across chunks or escaped the protobuf response.");
            Require(LegacyResponseCanSkipFrozenFields(first.ToByteArray()),
                "A legacy protobuf reader cannot skip the appended response field.");

            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, selection,
                0, 2, digest, 0, 2), "Frozen page accepted without capture metadata.");
            metadata.CaptureRevision = 9;
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, selection,
                0, 2, digest, 0, 2, HostHistoryRequestKindV1.FrozenBegin, metadata),
                "Frozen page accepted a revision unrelated to snapshot S.");
            metadata.CaptureRevision = 8;
            metadata.Token = ByteString.CopyFrom(new byte[31]);
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, selection,
                0, 2, digest, 0, 2, HostHistoryRequestKindV1.FrozenBegin, metadata),
                "Frozen page accepted a short token.");
            metadata.Token = first.FrozenHistory.Token;
            metadata.SnapshotSeq = 8;
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, selection,
                0, 2, digest, 0, 2, HostHistoryRequestKindV1.FrozenBegin, metadata),
                "Frozen page accepted metadata from a different snapshot S.");
            metadata.SnapshotSeq = 7;
            selection.Object.SnapshotSequence = 0;
            metadata.CaptureRevision = 1;
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, selection,
                0, 2, digest, 0, 2, HostHistoryRequestKindV1.FrozenBegin, metadata),
                "Frozen page accepted snapshot S=0.");
            selection.Object.SnapshotSequence = 7;
            metadata.CaptureRevision = 8;

            using var release = new ProductionHostHistorySelection { Status = HostHistoryResponseStatusV1.Ok };
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, release,
                0, 0, Array.Empty<byte>(), 0, 0),
                "Legacy empty OK was accidentally enabled.");
            var released = ProductionHostHistoryResponseBuilder.Build(requestId, release,
                0, 0, Array.Empty<byte>(), 0, 0, HostHistoryRequestKindV1.FrozenRelease, metadata);
            Require(released.Kind == HostHistoryObjectKindV1.Unspecified &&
                released.ObjectId.IsEmpty && released.ObjectChunk.IsEmpty &&
                released.ChunkCount == 0 && released.FrozenHistory.Token.Length == 32,
                "Frozen RELEASE did not produce its explicit empty OK response.");
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, selection,
                0, 2, digest, 0, 2, HostHistoryRequestKindV1.FrozenRelease, metadata),
                "Frozen RELEASE returned an object.");

            using var changed = new ProductionHostHistorySelection
            {
                Status = HostHistoryResponseStatusV1.Rejected,
                ErrorCode = "ANCHOR_CHANGED", AvailableSnapshotSequence = 9
            };
            var available = ProductionHostHistoryResponseBuilder.Build(requestId, changed,
                0, 0, Array.Empty<byte>(), 0, 0,
                HostHistoryRequestKindV1.FrozenBegin, null, requestedSnapshotSequence: 7);
            Require(available.SnapshotSeq == 9 && available.ErrorCode == "ANCHOR_CHANGED" &&
                available.ObjectChunk.IsEmpty && available.ChunkCount == 0,
                "Only an obsolete BEGIN may carry the newer available snapshot S.");
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, changed,
                0, 0, Array.Empty<byte>(), 0, 0,
                HostHistoryRequestKindV1.FrozenBegin, null, requestedSnapshotSequence: 9),
                "ANCHOR_CHANGED leaked an S that was not newer than requested.");
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, changed,
                0, 0, Array.Empty<byte>(), 0, 0,
                HostHistoryRequestKindV1.FrozenPage, null, requestedSnapshotSequence: 7),
                "PAGE leaked the available snapshot S.");
            changed.ErrorCode = "CAPTURE_LIMIT";
            ExpectArgument(() => ProductionHostHistoryResponseBuilder.Build(requestId, changed,
                0, 0, Array.Empty<byte>(), 0, 0,
                HostHistoryRequestKindV1.FrozenBegin, null, requestedSnapshotSequence: 7),
                "An unrelated error leaked the available snapshot S.");
            using var ordinaryError = new ProductionHostHistorySelection
            { Status = HostHistoryResponseStatusV1.Rejected, ErrorCode = "CAPTURE_LIMIT" };
            var ordinary = ProductionHostHistoryResponseBuilder.Build(requestId, ordinaryError,
                0, 0, Array.Empty<byte>(), 0, 0,
                HostHistoryRequestKindV1.FrozenBegin, null, requestedSnapshotSequence: 7);
            Require(ordinary.SnapshotSeq == 0,
                "An unrelated empty error response must keep snapshot S zero.");
        }

        private static bool LegacyRequestCanSkipFrozenFields(byte[] wire)
        {
            using var input = new CodedInputStream(wire);
            var kind = 0;
            while (true)
            {
                var tag = input.ReadTag();
                if (tag == 0) break;
                switch (tag)
                {
                    case 16: kind = input.ReadEnum(); break;
                    case 24: input.ReadUInt64(); break;
                    case 32: input.ReadUInt64(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return kind == 4;
        }

        private static bool LegacyResponseCanSkipFrozenFields(byte[] wire)
        {
            using var input = new CodedInputStream(wire);
            var status = 0;
            while (true)
            {
                var tag = input.ReadTag();
                if (tag == 0) break;
                if (tag == 16) status = input.ReadEnum();
                else input.SkipLastField();
            }
            return status == 1;
        }

        private static void ExpectArgument(Action action, string message)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
