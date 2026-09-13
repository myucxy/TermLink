using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    internal enum ProductionDirectCutoverTemporaryBufferKind
    {
        CreateWire,
        ParseOwnedInput,
        ParseSessionId,
        ParseCanonical,
        DigestInput,
        ResultDirectPathBinding,
        ResultBarrierSha256,
        ResultWireBytes
    }

    internal sealed class ProductionDirectSessionCutoverBarrier
    {
        public Guid SessionId { get; init; }
        public ulong FirstDirectSequence { get; init; }
    }

    internal sealed class ProductionDirectCutoverProposal
    {
        public Guid BarrierId { get; init; }
        public Direction Direction { get; init; }
        public byte[] DirectPathBinding { get; init; } = Array.Empty<byte>();
        public byte[] BarrierSha256 { get; init; } = Array.Empty<byte>();
        public uint DuplicateGraceMilliseconds { get; init; }
        public IReadOnlyList<ProductionDirectSessionCutoverBarrier> SessionBarriers { get; init; } =
            Array.Empty<ProductionDirectSessionCutoverBarrier>();
        public byte[] WireBytes { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Canonical protobuf and digest boundary for the independently committed
    /// Direct directions. It owns no socket or Relay state. Received bytes
    /// must round-trip byte-identically so unknown fields, duplicate singular
    /// fields, non-minimal varints, and non-canonical barrier order fail closed.
    /// </summary>
    internal static class ProductionDirectCutoverCodec
    {
        private static readonly byte[] DigestDomain =
            Encoding.ASCII.GetBytes("TERMLINK-DIRECT-CUTOVER-V1\0");
        internal const int MaximumSessionBarriers = 32;
        internal const uint MaximumDuplicateGraceMilliseconds = 5_000;
        internal const int MaximumProposalBytes = 4 * 1024;

        // Validation-only shallow-reference seam. The callback observes storage but never
        // receives ownership; every non-transferred buffer is still cleared by this codec.
        internal static Action<ProductionDirectCutoverTemporaryBufferKind, byte[]>
            TemporaryBufferObserverForValidation { get; set; }

        internal static ProductionDirectCutoverProposal CreateProposal(
            Guid connectionId,
            ulong pathEpoch,
            Direction direction,
            Guid barrierId,
            ReadOnlySpan<byte> directPathBinding,
            IEnumerable<ProductionDirectSessionCutoverBarrier> sessionBarriers,
            uint duplicateGraceMilliseconds)
        {
            ValidateOwner(connectionId, pathEpoch, direction);
            if (barrierId == Guid.Empty)
                throw new ArgumentException("A non-empty cutover barrier ID is required.",
                    nameof(barrierId));
            if (directPathBinding.Length != 32 || IsAllZero(directPathBinding))
                throw new ArgumentException("A non-zero 32-byte Direct path binding is required.",
                    nameof(directPathBinding));
            var canonical = CanonicalizeBarriers(sessionBarriers);
            if (duplicateGraceMilliseconds > MaximumDuplicateGraceMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(duplicateGraceMilliseconds));

            var message = BuildMessage(
                direction,
                barrierId,
                directPathBinding,
                canonical,
                duplicateGraceMilliseconds);
            var wire = message.ToByteArray();
            try
            {
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverTemporaryBufferKind.CreateWire,
                    wire);
                if (wire.Length is < 1 or > MaximumProposalBytes)
                    throw new InvalidDataException("The cutover proposal exceeds its wire limit.");
                return Result(
                    connectionId,
                    pathEpoch,
                    direction,
                    barrierId,
                    directPathBinding,
                    canonical,
                    duplicateGraceMilliseconds,
                    wire);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wire);
            }
        }

        internal static bool TryParseProposal(
            ReadOnlySpan<byte> exactWireBytes,
            Guid connectionId,
            ulong pathEpoch,
            Direction expectedDirection,
            out ProductionDirectCutoverProposal proposal)
        {
            proposal = null;
            byte[] owned = null;
            byte[] previousSessionId = null;
            byte[] currentSessionId = null;
            byte[] canonical = null;
            try
            {
                ValidateOwner(connectionId, pathEpoch, expectedDirection);
                if (exactWireBytes.Length is < 1 or > MaximumProposalBytes) return false;
                owned = exactWireBytes.ToArray();
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverTemporaryBufferKind.ParseOwnedInput,
                    owned);
                DirectCutoverProposalV1 message;
                try { message = DirectCutoverProposalV1.Parser.ParseFrom(owned); }
                catch (InvalidProtocolBufferException) { return false; }

                if (message.BarrierId.Length != 16 || IsAllZero(message.BarrierId.Span) ||
                    message.DirectPathBinding.Length != 32 ||
                    IsAllZero(message.DirectPathBinding.Span) ||
                    message.SessionBarriers.Count is < 1 or > MaximumSessionBarriers ||
                    message.DuplicateGraceMs > MaximumDuplicateGraceMilliseconds)
                {
                    return false;
                }

                var barriers = new List<ProductionDirectSessionCutoverBarrier>(
                    message.SessionBarriers.Count);
                foreach (var value in message.SessionBarriers)
                {
                    if (value == null || value.SessionId.Length != 16 ||
                        IsAllZero(value.SessionId.Span)) return false;
                    var pcToAndroid = value.HasPcToAndroidFirstDirectSeq;
                    var androidToPc = value.HasAndroidToPcFirstDirectSeq;
                    var directionMatches = expectedDirection switch
                    {
                        Direction.PcToAndroid => pcToAndroid && !androidToPc,
                        Direction.AndroidToPc => androidToPc && !pcToAndroid,
                        _ => false
                    };
                    if (!directionMatches) return false;
                    var firstSequence = pcToAndroid
                        ? value.PcToAndroidFirstDirectSeq
                        : value.AndroidToPcFirstDirectSeq;
                    if (firstSequence is 0 or > long.MaxValue) return false;
                    currentSessionId = value.SessionId.ToByteArray();
                    ObserveTemporaryBuffer(
                        ProductionDirectCutoverTemporaryBufferKind.ParseSessionId,
                        currentSessionId);
                    if (previousSessionId != null &&
                        CompareUnsigned(previousSessionId, currentSessionId) >= 0) return false;
                    barriers.Add(new ProductionDirectSessionCutoverBarrier
                    {
                        SessionId = GuidFromBytes(currentSessionId),
                        FirstDirectSequence = firstSequence
                    });
                    if (previousSessionId != null)
                        CryptographicOperations.ZeroMemory(previousSessionId);
                    previousSessionId = currentSessionId;
                    currentSessionId = null;
                }

                var barrierId = GuidFromBytes(message.BarrierId.Span);
                canonical = BuildMessage(
                    expectedDirection,
                    barrierId,
                    message.DirectPathBinding.Span,
                    barriers,
                    message.DuplicateGraceMs).ToByteArray();
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverTemporaryBufferKind.ParseCanonical,
                    canonical);
                if (!exactWireBytes.SequenceEqual(canonical)) return false;
                proposal = Result(
                    connectionId,
                    pathEpoch,
                    expectedDirection,
                    barrierId,
                    message.DirectPathBinding.Span,
                    barriers,
                    message.DuplicateGraceMs,
                    canonical);
                return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or
                OverflowException)
            {
                proposal = null;
                return false;
            }
            finally
            {
                if (currentSessionId != null)
                    CryptographicOperations.ZeroMemory(currentSessionId);
                if (previousSessionId != null)
                    CryptographicOperations.ZeroMemory(previousSessionId);
                if (canonical != null)
                    CryptographicOperations.ZeroMemory(canonical);
                if (owned != null)
                    CryptographicOperations.ZeroMemory(owned);
            }
        }

        internal static byte[] ComputeBarrierSha256(
            Guid connectionId,
            ulong pathEpoch,
            ReadOnlySpan<byte> deterministicProposal)
        {
            if (connectionId == Guid.Empty)
                throw new ArgumentException("A non-empty Direct connection ID is required.",
                    nameof(connectionId));
            if (pathEpoch is 0 or > long.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(pathEpoch));
            if (deterministicProposal.Length is < 1 or > MaximumProposalBytes)
                throw new ArgumentOutOfRangeException(nameof(deterministicProposal));

            var input = new byte[checked(
                DigestDomain.Length + 16 + sizeof(ulong) + deterministicProposal.Length)];
            try
            {
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverTemporaryBufferKind.DigestInput,
                    input);
                var offset = 0;
                DigestDomain.CopyTo(input, offset);
                offset += DigestDomain.Length;
                GuidBytes(connectionId).CopyTo(input, offset);
                offset += 16;
                BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan(offset, sizeof(ulong)), pathEpoch);
                offset += sizeof(ulong);
                deterministicProposal.CopyTo(input.AsSpan(offset));
                return SHA256.HashData(input);
            }
            finally { CryptographicOperations.ZeroMemory(input); }
        }

        private static ProductionDirectCutoverProposal Result(
            Guid connectionId,
            ulong pathEpoch,
            Direction direction,
            Guid barrierId,
            ReadOnlySpan<byte> directPathBinding,
            IReadOnlyList<ProductionDirectSessionCutoverBarrier> barriers,
            uint duplicateGraceMilliseconds,
            byte[] wire)
        {
            byte[] ownedBinding = null;
            byte[] barrierSha256 = null;
            byte[] ownedWire = null;
            try
            {
                ownedBinding = directPathBinding.ToArray();
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverTemporaryBufferKind.ResultDirectPathBinding,
                    ownedBinding);
                barrierSha256 = ComputeBarrierSha256(connectionId, pathEpoch, wire);
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverTemporaryBufferKind.ResultBarrierSha256,
                    barrierSha256);
                ownedWire = wire.ToArray();
                ObserveTemporaryBuffer(
                    ProductionDirectCutoverTemporaryBufferKind.ResultWireBytes,
                    ownedWire);
                var result = new ProductionDirectCutoverProposal
                {
                    BarrierId = barrierId,
                    Direction = direction,
                    DirectPathBinding = ownedBinding,
                    BarrierSha256 = barrierSha256,
                    DuplicateGraceMilliseconds = duplicateGraceMilliseconds,
                    SessionBarriers = barriers.Select(value =>
                        new ProductionDirectSessionCutoverBarrier
                        {
                            SessionId = value.SessionId,
                            FirstDirectSequence = value.FirstDirectSequence
                        }).ToArray(),
                    WireBytes = ownedWire
                };
                ownedBinding = null;
                barrierSha256 = null;
                ownedWire = null;
                return result;
            }
            finally
            {
                if (ownedBinding != null)
                    CryptographicOperations.ZeroMemory(ownedBinding);
                if (barrierSha256 != null)
                    CryptographicOperations.ZeroMemory(barrierSha256);
                if (ownedWire != null)
                    CryptographicOperations.ZeroMemory(ownedWire);
            }
        }

        private static DirectCutoverProposalV1 BuildMessage(
            Direction direction,
            Guid barrierId,
            ReadOnlySpan<byte> directPathBinding,
            IReadOnlyList<ProductionDirectSessionCutoverBarrier> sessionBarriers,
            uint duplicateGraceMilliseconds)
        {
            var result = new DirectCutoverProposalV1
            {
                BarrierId = ByteString.CopyFrom(GuidBytes(barrierId)),
                DirectPathBinding = ByteString.CopyFrom(directPathBinding),
                DuplicateGraceMs = duplicateGraceMilliseconds
            };
            foreach (var value in sessionBarriers)
            {
                var barrier = new SessionCutoverBarrierV1
                {
                    SessionId = ByteString.CopyFrom(GuidBytes(value.SessionId))
                };
                if (direction == Direction.PcToAndroid)
                    barrier.PcToAndroidFirstDirectSeq = value.FirstDirectSequence;
                else
                    barrier.AndroidToPcFirstDirectSeq = value.FirstDirectSequence;
                result.SessionBarriers.Add(barrier);
            }
            return result;
        }

        private static IReadOnlyList<ProductionDirectSessionCutoverBarrier> CanonicalizeBarriers(
            IEnumerable<ProductionDirectSessionCutoverBarrier> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var result = values
                .Select(value => value ?? throw new ArgumentException(
                    "A cutover barrier cannot be null.", nameof(values)))
                .Select(value => new ProductionDirectSessionCutoverBarrier
                {
                    SessionId = value.SessionId,
                    FirstDirectSequence = value.FirstDirectSequence
                })
                .OrderBy(value => Convert.ToHexString(GuidBytes(value.SessionId)),
                    StringComparer.Ordinal)
                .ToArray();
            if (result.Length is < 1 or > MaximumSessionBarriers ||
                result.Any(value => value.SessionId == Guid.Empty ||
                    value.FirstDirectSequence is 0 or > long.MaxValue) ||
                result.Select(value => value.SessionId).Distinct().Count() != result.Length)
            {
                throw new ArgumentException("Cutover session barriers are invalid.", nameof(values));
            }
            return result;
        }

        private static void ValidateOwner(
            Guid connectionId,
            ulong pathEpoch,
            Direction direction)
        {
            if (connectionId == Guid.Empty)
                throw new ArgumentException("A non-empty Direct connection ID is required.",
                    nameof(connectionId));
            if (pathEpoch is 0 or > long.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(pathEpoch));
            if (direction is not Direction.PcToAndroid and not Direction.AndroidToPc)
                throw new ArgumentOutOfRangeException(nameof(direction));
        }

        private static int CompareUnsigned(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                var compared = left[index].CompareTo(right[index]);
                if (compared != 0) return compared;
            }
            return left.Length.CompareTo(right.Length);
        }

        private static bool IsAllZero(ReadOnlySpan<byte> value)
        {
            byte aggregate = 0;
            foreach (var item in value) aggregate |= item;
            return aggregate == 0;
        }

        private static byte[] GuidBytes(Guid value) =>
            Convert.FromHexString(value.ToString("N"));

        private static Guid GuidFromBytes(ReadOnlySpan<byte> value)
        {
            if (value.Length != 16)
                throw new InvalidDataException("A Direct UUID must contain 16 bytes.");
            return Guid.ParseExact(Convert.ToHexString(value), "N");
        }

        private static void ObserveTemporaryBuffer(
            ProductionDirectCutoverTemporaryBufferKind kind,
            byte[] value) => TemporaryBufferObserverForValidation?.Invoke(kind, value);
    }
}
