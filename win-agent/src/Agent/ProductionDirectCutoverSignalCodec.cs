using System;
using System.IO;
using System.Security.Cryptography;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    internal enum ProductionDirectCutoverSignalTemporaryBufferKind
    {
        ParserInput,
        ProposalPayload,
        ProposalCanonical,
        AcceptHash,
        AcceptCanonical,
        CommitHash,
        CommitCanonical,
        FallbackCanonical
    }

    internal sealed class ProductionDirectCutoverAcceptSignal
    {
        internal Guid BarrierId { get; init; }
        internal byte[] BarrierSha256 { get; init; } = Array.Empty<byte>();
        internal bool Accepted { get; init; }
        internal string ReasonCode { get; init; } = string.Empty;
    }

    internal sealed class ProductionDirectCutoverCommitSignal
    {
        internal Guid BarrierId { get; init; }
        internal byte[] BarrierSha256 { get; init; } = Array.Empty<byte>();
    }

    internal sealed class ProductionDirectFallbackSignal
    {
        internal Guid BarrierId { get; init; }
        internal string ReasonCode { get; init; } = string.Empty;
        internal ulong LastReceivedDirectFrameSequence { get; init; }
    }

    /// <summary>
    /// Exact canonical codec for the encrypted signalling plaintext carried by
    /// transport signal types 6-9. It rejects unknown fields, alternate field
    /// ordering, duplicate singular fields and owner/direction mismatches.
    /// </summary>
    internal static class ProductionDirectCutoverSignalCodec
    {
        internal const int MaximumPlaintextBytes = 8 * 1024;

        // Validation-only shallow-reference seam. Production ownership never leaves this codec;
        // tests retain the reference solely to verify the subsequent zeroization/transfer.
        internal static Action<ProductionDirectCutoverSignalTemporaryBufferKind, byte[]>
            TemporaryBufferObserverForValidation { get; set; }

        internal static byte[] CreateProposal(ProductionDirectCutoverProposal value)
        {
            if (value?.WireBytes?.Length is not (> 0))
                throw new ArgumentException("A canonical cutover proposal is required.",
                    nameof(value));
            var proposal = DirectCutoverProposalV1.Parser.ParseFrom(value.WireBytes);
            return new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                CutoverProposal = proposal
            }.ToByteArray();
        }

        internal static bool TryParseProposal(
            ReadOnlySpan<byte> plaintext,
            Guid connectionId,
            ulong pathEpoch,
            Direction direction,
            out ProductionDirectCutoverProposal value)
        {
            value = null;
            if (!TryParsePayload(plaintext, out var payload) ||
                payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.CutoverProposal)
                return false;
            var proposalBytes = payload.CutoverProposal.ToByteArray();
            ProductionDirectCutoverProposal candidate = null;
            byte[] canonical = null;
            try
            {
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalPayload,
                    proposalBytes);
                if (!ProductionDirectCutoverCodec.TryParseProposal(
                        proposalBytes, connectionId, pathEpoch, direction, out candidate))
                    return false;
                canonical = CreateProposal(candidate);
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverSignalTemporaryBufferKind.ProposalCanonical,
                    canonical);
                if (!plaintext.SequenceEqual(canonical)) return false;
                value = candidate;
                candidate = null;
                return true;
            }
            finally
            {
                if (candidate != null) ClearProposal(candidate);
                if (canonical != null) CryptographicOperations.ZeroMemory(canonical);
                CryptographicOperations.ZeroMemory(proposalBytes);
            }
        }

        internal static byte[] CreateAccept(
            Guid barrierId,
            byte[] barrierSha256,
            bool accepted,
            string reasonCode)
        {
            ValidateBarrier(barrierId, barrierSha256);
            reasonCode = NormalizeReason(reasonCode);
            if (accepted != string.IsNullOrEmpty(reasonCode))
                throw new ArgumentException(
                    "Accepted cutover signals require an empty reason and rejections require one.");
            return new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                CutoverAccept = new DirectCutoverAcceptV1
                {
                    BarrierId = ByteString.CopyFrom(GuidBytes(barrierId)),
                    BarrierSha256 = ByteString.CopyFrom(barrierSha256),
                    Result = accepted ? DirectPathResultV1.Accepted : DirectPathResultV1.Rejected,
                    ReasonCode = reasonCode
                }
            }.ToByteArray();
        }

        internal static bool TryParseAccept(
            ReadOnlySpan<byte> plaintext,
            out ProductionDirectCutoverAcceptSignal value)
        {
            value = null;
            if (!TryParsePayload(plaintext, out var payload) ||
                payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.CutoverAccept)
                return false;
            var signal = payload.CutoverAccept;
            if (!TryBarrier(signal.BarrierId, signal.BarrierSha256, out var barrierId) ||
                signal.Result is not DirectPathResultV1.Accepted and not DirectPathResultV1.Rejected)
                return false;
            var accepted = signal.Result == DirectPathResultV1.Accepted;
            var reason = NormalizeReason(signal.ReasonCode);
            if (reason != signal.ReasonCode || accepted != string.IsNullOrEmpty(reason)) return false;
            var hash = signal.BarrierSha256.ToByteArray();
            byte[] canonical = null;
            try
            {
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptHash,
                    hash);
                canonical = CreateAccept(barrierId, hash, accepted, reason);
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverSignalTemporaryBufferKind.AcceptCanonical,
                    canonical);
                if (!plaintext.SequenceEqual(canonical)) return false;
                value = new ProductionDirectCutoverAcceptSignal
                {
                    BarrierId = barrierId,
                    BarrierSha256 = hash,
                    Accepted = accepted,
                    ReasonCode = reason
                };
                hash = null;
                return true;
            }
            finally
            {
                if (hash != null) CryptographicOperations.ZeroMemory(hash);
                if (canonical != null) CryptographicOperations.ZeroMemory(canonical);
            }
        }

        internal static byte[] CreateCommit(Guid barrierId, byte[] barrierSha256)
        {
            ValidateBarrier(barrierId, barrierSha256);
            return new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                CutoverCommit = new DirectCutoverCommitV1
                {
                    BarrierId = ByteString.CopyFrom(GuidBytes(barrierId)),
                    BarrierSha256 = ByteString.CopyFrom(barrierSha256)
                }
            }.ToByteArray();
        }

        internal static bool TryParseCommit(
            ReadOnlySpan<byte> plaintext,
            out ProductionDirectCutoverCommitSignal value)
        {
            value = null;
            if (!TryParsePayload(plaintext, out var payload) ||
                payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.CutoverCommit)
                return false;
            var signal = payload.CutoverCommit;
            if (!TryBarrier(signal.BarrierId, signal.BarrierSha256, out var barrierId)) return false;
            var hash = signal.BarrierSha256.ToByteArray();
            byte[] canonical = null;
            try
            {
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitHash,
                    hash);
                canonical = CreateCommit(barrierId, hash);
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverSignalTemporaryBufferKind.CommitCanonical,
                    canonical);
                if (!plaintext.SequenceEqual(canonical)) return false;
                value = new ProductionDirectCutoverCommitSignal
                {
                    BarrierId = barrierId,
                    BarrierSha256 = hash
                };
                hash = null;
                return true;
            }
            finally
            {
                if (hash != null) CryptographicOperations.ZeroMemory(hash);
                if (canonical != null) CryptographicOperations.ZeroMemory(canonical);
            }
        }

        internal static byte[] CreateFallback(
            Guid barrierId,
            string reasonCode,
            ulong lastReceivedDirectFrameSequence)
        {
            if (barrierId == Guid.Empty)
                throw new ArgumentException("A fallback barrier is required.", nameof(barrierId));
            reasonCode = NormalizeReason(reasonCode);
            if (string.IsNullOrEmpty(reasonCode))
                throw new ArgumentException("A fallback reason is required.", nameof(reasonCode));
            return new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                Fallback = new DirectFallbackV1
                {
                    BarrierId = ByteString.CopyFrom(GuidBytes(barrierId)),
                    ReasonCode = reasonCode,
                    LastReceivedDirectFrameSeq = lastReceivedDirectFrameSequence
                }
            }.ToByteArray();
        }

        internal static bool TryParseFallback(
            ReadOnlySpan<byte> plaintext,
            out ProductionDirectFallbackSignal value)
        {
            value = null;
            if (!TryParsePayload(plaintext, out var payload) ||
                payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.Fallback ||
                payload.Fallback.BarrierId.Length != 16 ||
                IsAllZero(payload.Fallback.BarrierId.Span)) return false;
            var reason = NormalizeReason(payload.Fallback.ReasonCode);
            if (string.IsNullOrEmpty(reason) || reason != payload.Fallback.ReasonCode) return false;
            var barrier = GuidFromBytes(payload.Fallback.BarrierId.Span);
            byte[] canonical = null;
            try
            {
                canonical = CreateFallback(
                    barrier, reason, payload.Fallback.LastReceivedDirectFrameSeq);
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverSignalTemporaryBufferKind.FallbackCanonical,
                    canonical);
                if (!plaintext.SequenceEqual(canonical)) return false;
                value = new ProductionDirectFallbackSignal
                {
                    BarrierId = barrier,
                    ReasonCode = reason,
                    LastReceivedDirectFrameSequence = payload.Fallback.LastReceivedDirectFrameSeq
                };
                return true;
            }
            finally
            {
                if (canonical != null) CryptographicOperations.ZeroMemory(canonical);
            }
        }

        internal static byte[] CreateNominationAck(ProductionDirectCommitRequestV4 nomination)
        {
            ValidateNomination(nomination);
            return new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationAckV2 = new DirectNominationAckV2
                {
                    NominationId = ByteString.CopyFrom(GuidBytes(nomination.NominationId)),
                    CheckRoundId = ByteString.CopyFrom(GuidBytes(nomination.CheckRoundId)),
                    WinningPair = ProductionDirectMultipairPlanner.EncodePair(
                        nomination.WinningPair),
                    Result = DirectPathResultV1.Accepted,
                    ReasonCode = string.Empty
                }
            }.ToByteArray();
        }

        internal static byte[] CreateNominationResult(
            ProductionDirectCommitRequestV4 nomination,
            ReadOnlySpan<byte> directPathBinding)
        {
            ValidateNomination(nomination);
            if (directPathBinding.Length != 32 || IsAllZero(directPathBinding))
                throw new ArgumentException("A non-zero Direct path binding is required.",
                    nameof(directPathBinding));
            return new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationResultV2 = new DirectNominationResultV2
                {
                    NominationId = ByteString.CopyFrom(GuidBytes(nomination.NominationId)),
                    CheckRoundId = ByteString.CopyFrom(GuidBytes(nomination.CheckRoundId)),
                    WinningPair = ProductionDirectMultipairPlanner.EncodePair(
                        nomination.WinningPair),
                    Result = DirectPathResultV1.Accepted,
                    ReasonCode = string.Empty,
                    DirectPathBinding = ByteString.CopyFrom(directPathBinding)
                }
            }.ToByteArray();
        }

        internal static bool TryParseNominationResult(
            ReadOnlySpan<byte> plaintext,
            ProductionDirectCommitRequestV4 expected,
            out byte[] directPathBinding)
        {
            directPathBinding = null;
            if (expected == null || !TryParsePayload(plaintext, out var payload) ||
                payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.NominationResultV2)
                return false;
            var value = payload.NominationResultV2;
            if (value.NominationId.Length != 16 || value.CheckRoundId.Length != 16 ||
                value.DirectPathBinding.Length != 32 || IsAllZero(value.DirectPathBinding.Span) ||
                value.Result != DirectPathResultV1.Accepted ||
                !string.IsNullOrEmpty(value.ReasonCode) ||
                !value.NominationId.Span.SequenceEqual(GuidBytes(expected.NominationId)) ||
                !value.CheckRoundId.Span.SequenceEqual(GuidBytes(expected.CheckRoundId)) ||
                !ProductionDirectMultipairPlanner.PairIdsEqual(
                    value.WinningPair, expected.WinningPair)) return false;
            var binding = value.DirectPathBinding.ToByteArray();
            byte[] canonical = null;
            try
            {
                canonical = CreateNominationResult(expected, binding);
                if (!plaintext.SequenceEqual(canonical)) return false;
                directPathBinding = binding;
                binding = null;
                return true;
            }
            finally
            {
                if (binding != null) CryptographicOperations.ZeroMemory(binding);
                if (canonical != null) CryptographicOperations.ZeroMemory(canonical);
            }
        }

        private static bool TryParsePayload(
            ReadOnlySpan<byte> plaintext,
            out TransportSignalPayloadV1 payload)
        {
            payload = null;
            if (plaintext.Length is < 1 or > MaximumPlaintextBytes) return false;
            var parserInput = plaintext.ToArray();
            try
            {
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverSignalTemporaryBufferKind.ParserInput,
                    parserInput);
                payload = TransportSignalPayloadV1.Parser.ParseFrom(parserInput);
            }
            catch (Exception error) when (error is InvalidProtocolBufferException or
                InvalidDataException or ArgumentException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(parserInput);
            }
            return payload.FormatVersion == 1;
        }

        private static void ObserveTemporaryBuffer(
            ProductionDirectCutoverSignalTemporaryBufferKind kind,
            byte[] value) => TemporaryBufferObserverForValidation?.Invoke(kind, value);

        private static void ClearProposal(ProductionDirectCutoverProposal value)
        {
            if (value.DirectPathBinding != null)
                CryptographicOperations.ZeroMemory(value.DirectPathBinding);
            if (value.BarrierSha256 != null)
                CryptographicOperations.ZeroMemory(value.BarrierSha256);
            if (value.WireBytes != null)
                CryptographicOperations.ZeroMemory(value.WireBytes);
        }

        private static void ValidateBarrier(Guid barrierId, byte[] barrierSha256)
        {
            if (barrierId == Guid.Empty || barrierSha256?.Length != 32 ||
                IsAllZero(barrierSha256))
                throw new ArgumentException("The cutover barrier is invalid.");
        }

        private static void ValidateNomination(ProductionDirectCommitRequestV4 value)
        {
            if (value == null || value.NominationId == Guid.Empty ||
                value.CheckRoundId == Guid.Empty || value.WinningPair?.Initiator == null ||
                value.WinningPair.Responder == null)
                throw new ArgumentException("The promoted nomination is invalid.", nameof(value));
        }

        private static bool TryBarrier(
            ByteString barrier,
            ByteString hash,
            out Guid barrierId)
        {
            barrierId = Guid.Empty;
            if (barrier?.Length != 16 || hash?.Length != 32 ||
                IsAllZero(barrier.Span) || IsAllZero(hash.Span)) return false;
            barrierId = GuidFromBytes(barrier.Span);
            return barrierId != Guid.Empty;
        }

        private static string NormalizeReason(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= 128 ? value : value[..128];
        }

        private static bool IsAllZero(ReadOnlySpan<byte> value)
        {
            byte aggregate = 0;
            foreach (var item in value) aggregate |= item;
            return aggregate == 0;
        }

        private static byte[] GuidBytes(Guid value) =>
            Convert.FromHexString(value.ToString("N"));

        private static Guid GuidFromBytes(ReadOnlySpan<byte> value) =>
            Guid.ParseExact(Convert.ToHexString(value), "N");
    }
}
