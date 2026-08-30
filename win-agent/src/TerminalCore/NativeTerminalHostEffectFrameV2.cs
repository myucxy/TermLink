using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TermLink.TerminalCore
{
    /// <summary>
    /// A deterministic rejection of malformed or unsupported TermLink Host Effect Frame V2 data.
    /// </summary>
    public sealed class NativeTerminalHostEffectFrameV2DecodeException : ArgumentException
    {
        internal NativeTerminalHostEffectFrameV2DecodeException(string message)
            : base("host effect frame v2: " + message)
        {
        }
    }

    public enum NativeTerminalHostEffectKindV2
    {
        PtyReply = 1
    }

    /// <summary>
    /// One ordered Host Effect Frame V2 record. Payload owns its immutable byte copy.
    /// </summary>
    public readonly struct NativeTerminalHostEffectRecordV2
    {
        private readonly IReadOnlyList<byte> payload;

        internal NativeTerminalHostEffectRecordV2(
            NativeTerminalHostEffectKindV2 kind,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            uint ordinal,
            byte[] payload)
        {
            Kind = kind;
            SourceSequence = sourceSequence;
            SourceOffsetEnd = sourceOffsetEnd;
            Ordinal = ordinal;
            this.payload = Array.AsReadOnly(
                payload ?? throw new ArgumentNullException(nameof(payload)));
        }

        public NativeTerminalHostEffectKindV2 Kind { get; }
        public ulong SourceSequence { get; }
        public uint SourceOffsetEnd { get; }
        public uint Ordinal { get; }

        /// <summary>
        /// An owned, read-only copy of the exact bytes to write to the authoritative PTY.
        /// </summary>
        public IReadOnlyList<byte> Payload => payload;
    }

    /// <summary>
    /// Immutable ordered PTY replies decoded from one canonical Host Effect Frame V2.
    /// </summary>
    public sealed class NativeTerminalHostEffectFrameV2
    {
        private readonly IReadOnlyList<byte> rawBytes;

        internal NativeTerminalHostEffectFrameV2(
            NativeTerminalHostEffectRecordV2[] records,
            byte[] rawBytes)
        {
            Records = Array.AsReadOnly(
                records ?? throw new ArgumentNullException(nameof(records)));
            this.rawBytes = Array.AsReadOnly(
                rawBytes ?? throw new ArgumentNullException(nameof(rawBytes)));
        }

        public const int FormatVersion = 2;
        public IReadOnlyList<NativeTerminalHostEffectRecordV2> Records { get; }

        /// <summary>
        /// An owned, read-only copy of the complete validated wire frame.
        /// </summary>
        public IReadOnlyList<byte> RawBytes => rawBytes;
    }

    /// <summary>
    /// Strict, allocation-bounded decoder for canonical TermLink Host Effect Frame V2 data.
    /// </summary>
    public static class NativeTerminalHostEffectFrameV2Decoder
    {
        public const int MaximumFrameBytes = 123_033_008;
        public const int MaximumEffects = 1_398_102;
        public const int MaximumPtyReplyBytes = 64 * 1024;
        public const int MaximumAggregatePayloadBytes = 64 * 1024 * 1024;

        private const int HeaderSize = 64;
        private const int RecordSize = 40;
        private const ulong RecordsOffset = HeaderSize;
        private const int HeaderCrcEnd = 60;
        private const ushort MajorVersion = 2;
        private const ushort MinorVersion = 0;
        private const ulong RequiredFeatures = 1;
        private const uint MaximumSourceOffset = 1024 * 1024;
        private const uint PtyReplyKind = 1;
        private const uint Crc32CPolynomial = 0x82f6_3b78;

        private static readonly byte[] Magic =
        {
            (byte)'T', (byte)'L', (byte)'H', (byte)'E',
            (byte)'F', (byte)'2', 0, 0
        };

        private static readonly byte[] PrimaryDeviceAttributesReply =
        {
            0x1b, 0x5b, 0x3f, 0x31, 0x3b, 0x32, 0x63
        };

        private static readonly byte[] SecondaryDeviceAttributesReply =
        {
            0x1b, 0x5b, 0x3e, 0x30, 0x3b, 0x30, 0x3b, 0x30, 0x63
        };

        private static readonly byte[] DeviceStatusReply = { 0x1b, 0x5b, 0x30, 0x6e };
        private static readonly byte[] PrivateDeviceStatusReply =
        {
            0x1b, 0x5b, 0x3f, 0x30, 0x6e
        };

        private static readonly byte[] XtVersionReply =
        {
            0x1b, 0x50, 0x3e, 0x7c,
            0x54, 0x65, 0x72, 0x6d, 0x4c, 0x69, 0x6e, 0x6b,
            0x28, 0x31, 0x29, 0x1b, 0x5c
        };

        public static NativeTerminalHostEffectFrameV2 Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Decode(bytes.AsSpan());
        }

        public static NativeTerminalHostEffectFrameV2 Decode(ReadOnlySpan<byte> bytes)
        {
            try
            {
                return new Decoder(bytes).Decode();
            }
            catch (NativeTerminalHostEffectFrameV2DecodeException)
            {
                throw;
            }
            catch (OverflowException)
            {
                throw Error("integer overflow");
            }
            catch (ArgumentOutOfRangeException)
            {
                throw Error("truncated data");
            }
        }

        private static NativeTerminalHostEffectFrameV2DecodeException Error(string message)
        {
            return new NativeTerminalHostEffectFrameV2DecodeException(message);
        }

        private ref struct Decoder
        {
            private readonly ReadOnlySpan<byte> bytes;

            internal Decoder(ReadOnlySpan<byte> bytes)
            {
                this.bytes = bytes;
            }

            internal NativeTerminalHostEffectFrameV2 Decode()
            {
                if (bytes.Length < HeaderSize) throw Error("header is truncated");
                if (bytes.Length > MaximumFrameBytes) throw Error("frame exceeds the hard limit");
                if (!bytes.Slice(0, Magic.Length).SequenceEqual(Magic)) throw Error("bad magic");

                ExpectUInt16(8, MajorVersion, "major version");
                ExpectUInt16(10, MinorVersion, "minor version");
                ExpectUInt16(12, HeaderSize, "header size");
                ExpectUInt16(14, RecordSize, "record size");

                var recordCountValue = ReadUInt32(16, "record count");
                if (recordCountValue > MaximumEffects)
                    throw Error("record count exceeds the hard limit");
                var totalLength = ReadUInt32(20, "total length");
                if (totalLength != (uint)bytes.Length)
                    throw Error("total length does not match input");
                ExpectUInt64(24, RecordsOffset, "records offset");

                var recordBytes = Multiply(recordCountValue, RecordSize, "record bytes");
                var expectedPayloadOffset = Add(RecordsOffset, recordBytes, "payload offset");
                var payloadOffset = ReadUInt64(32, "payload offset");
                if (payloadOffset != expectedPayloadOffset)
                    throw Error("payload offset is not canonical");
                if (expectedPayloadOffset > totalLength)
                    throw Error("record table is truncated");
                var aggregatePayloadBytes = (ulong)totalLength - expectedPayloadOffset;
                if (aggregatePayloadBytes > MaximumAggregatePayloadBytes)
                    throw Error("aggregate PTY reply payload exceeds the hard limit");

                ExpectUInt64(40, RequiredFeatures, "required features");
                ExpectUInt64(48, 0, "header reserved field");

                var payloadCrc = ReadUInt32(56, "payload CRC32C");
                var headerCrc = ReadUInt32(60, "header CRC32C");
                if (Crc32C(bytes.Slice(0, HeaderCrcEnd)) != headerCrc)
                    throw Error("header CRC32C mismatch");
                if (Crc32C(bytes.Slice(HeaderSize)) != payloadCrc)
                    throw Error("payload CRC32C mismatch");

                var recordCount = ToInt(recordCountValue, "record count");
                var records = new NativeTerminalHostEffectRecordV2[recordCount];
                ulong expectedDataOffset = payloadOffset;
                ulong previousSequence = 0;
                uint previousSourceOffset = 0;
                uint previousOrdinal = 0;
                var hasPrevious = false;

                for (var index = 0; index < recordCount; index++)
                {
                    var recordOffset = ToInt(
                        Add(
                            RecordsOffset,
                            Multiply((ulong)index, RecordSize, "record offset"),
                            "record offset"),
                        "record offset");
                    ExpectUInt32(recordOffset, PtyReplyKind, "host effect kind");
                    ExpectUInt32(recordOffset + 4, 0, "record flags");
                    var sequence = ReadUInt64(recordOffset + 8, "source sequence");
                    var sourceOffset = ReadUInt32(recordOffset + 16, "source offset end");
                    if (sourceOffset < 1 || sourceOffset > MaximumSourceOffset)
                        throw Error("source offset end is out of range");
                    var ordinal = ReadUInt32(recordOffset + 20, "ordinal");
                    ValidateOrder(
                        hasPrevious,
                        previousSequence,
                        previousSourceOffset,
                        previousOrdinal,
                        sequence,
                        sourceOffset,
                        ordinal);

                    var dataOffset = ReadUInt64(recordOffset + 24, "record payload offset");
                    if (dataOffset != expectedDataOffset)
                        throw Error("record payloads are not contiguous");
                    var dataLength = ReadUInt32(recordOffset + 32, "record payload length");
                    if (dataLength == 0) throw Error("record payload is empty");
                    if (dataLength > MaximumPtyReplyBytes)
                        throw Error("PTY reply exceeds 65536 bytes");
                    ExpectUInt32(recordOffset + 36, 0, "record reserved field");
                    var dataEnd = Add(dataOffset, dataLength, "record payload end");
                    if (dataEnd > totalLength) throw Error("record payload is truncated");
                    var payload = bytes.Slice(
                        ToInt(dataOffset, "record payload offset"),
                        ToInt(dataLength, "record payload length"));
                    ValidatePtyReply(payload);
                    records[index] = new NativeTerminalHostEffectRecordV2(
                        NativeTerminalHostEffectKindV2.PtyReply,
                        sequence,
                        sourceOffset,
                        ordinal,
                        payload.ToArray());

                    expectedDataOffset = dataEnd;
                    previousSequence = sequence;
                    previousSourceOffset = sourceOffset;
                    previousOrdinal = ordinal;
                    hasPrevious = true;
                }

                if (expectedDataOffset != totalLength)
                    throw Error("frame has trailing or unreferenced payload bytes");
                return new NativeTerminalHostEffectFrameV2(records, bytes.ToArray());
            }

            private static void ValidateOrder(
                bool hasPrevious,
                ulong previousSequence,
                uint previousSourceOffset,
                uint previousOrdinal,
                ulong sequence,
                uint sourceOffset,
                uint ordinal)
            {
                if (!hasPrevious)
                {
                    if (ordinal != 0) throw Error("the first source key ordinal must be zero");
                    return;
                }

                if (sequence < previousSequence ||
                    (sequence == previousSequence && sourceOffset < previousSourceOffset))
                {
                    throw Error("records regress in source-key order");
                }

                if (sequence == previousSequence && sourceOffset == previousSourceOffset)
                {
                    if (previousOrdinal == uint.MaxValue || ordinal != previousOrdinal + 1)
                        throw Error("same-key ordinals are not contiguous");
                    return;
                }

                if (ordinal != 0) throw Error("a new source key ordinal must be zero");
            }

            private static void ValidatePtyReply(ReadOnlySpan<byte> payload)
            {
                if (IsCanonicalV1Reply(payload) ||
                    IsCanonicalDecrpm(payload) ||
                    IsCanonicalDecrqss(payload) ||
                    payload.SequenceEqual(XtVersionReply) ||
                    IsCanonicalXtgettcap(payload) ||
                    IsCanonicalOscColor(payload))
                {
                    return;
                }
                throw Error("PTY reply is not a supported canonical response");
            }

            private static bool IsCanonicalV1Reply(ReadOnlySpan<byte> payload)
            {
                if (payload.SequenceEqual(PrimaryDeviceAttributesReply) ||
                    payload.SequenceEqual(SecondaryDeviceAttributesReply) ||
                    payload.SequenceEqual(DeviceStatusReply) ||
                    payload.SequenceEqual(PrivateDeviceStatusReply))
                {
                    return true;
                }

                if (payload.Length >= 5 && payload[0] == 0x1b && payload[1] == 0x5b &&
                    payload[2] == 0x3f && payload[payload.Length - 1] == 0x75)
                {
                    return TryReadCanonicalUInt32(
                        payload.Slice(3, payload.Length - 4),
                        out var flags) && flags <= 31;
                }

                if (payload.Length < 6 || payload[0] != 0x1b || payload[1] != 0x5b ||
                    payload[payload.Length - 1] != 0x52)
                {
                    return false;
                }
                var body = payload.Slice(2, payload.Length - 3);
                if (!body.IsEmpty && body[0] == 0x3f) body = body.Slice(1);
                var separator = body.IndexOf((byte)0x3b);
                return separator >= 0 && body.Slice(separator + 1).IndexOf((byte)0x3b) < 0 &&
                    TryReadCoordinate(body.Slice(0, separator), out _) &&
                    TryReadCoordinate(body.Slice(separator + 1), out _);
            }

            private static bool IsCanonicalDecrpm(ReadOnlySpan<byte> payload)
            {
                if (payload.Length < 7 || payload[0] != 0x1b || payload[1] != 0x5b ||
                    payload[payload.Length - 2] != 0x24 || payload[payload.Length - 1] != 0x79)
                {
                    return false;
                }
                var body = payload.Slice(2, payload.Length - 4);
                if (!body.IsEmpty && body[0] == 0x3f) body = body.Slice(1);
                var separator = body.IndexOf((byte)0x3b);
                return separator >= 0 && body.Slice(separator + 1).Length == 1 &&
                    TryReadCanonicalUInt32(body.Slice(0, separator), out _) &&
                    body[separator + 1] >= 0x30 && body[separator + 1] <= 0x34;
            }

            private static bool IsCanonicalDecrqss(ReadOnlySpan<byte> payload)
            {
                if (!TryUnwrapDcs(payload, out var body)) return false;
                if (body.Length == 3 && body[0] == 0x30 && body[1] == 0x24 && body[2] == 0x72)
                    return true;
                if (body.Length < 4 || body[0] != 0x31 || body[1] != 0x24 || body[2] != 0x72)
                    return false;
                var status = body.Slice(3);
                return IsCanonicalSgrStatus(status) ||
                    IsCanonicalMarginStatus(status, 0x72) ||
                    IsCanonicalMarginStatus(status, 0x73) ||
                    (status.Length == 3 && status[0] >= 0x31 && status[0] <= 0x36 &&
                        status[1] == 0x20 && status[2] == 0x71) ||
                    (status.Length == 3 && status[0] >= 0x31 && status[0] <= 0x32 &&
                        ((status[1] == 0x22 && status[2] == 0x71) ||
                            (status[1] == 0x2a && status[2] == 0x78)));
            }

            private static bool IsCanonicalSgrStatus(ReadOnlySpan<byte> status)
            {
                if (status.Length < 2 || status[status.Length - 1] != 0x6d || status[0] != 0x30)
                    return false;
                var parameters = status.Slice(0, status.Length - 1);
                if (parameters.Length == 1) return true;
                if (parameters[1] != 0x3b) return false;

                var position = 2;
                var lastRank = 0;
                while (position < parameters.Length)
                {
                    if (!TryTakeField(parameters, ref position, out var field)) return false;
                    int rank;
                    bool valid;
                    if (IsSingleDigit(field, 1)) { rank = 1; valid = true; }
                    else if (IsSingleDigit(field, 2)) { rank = 2; valid = true; }
                    else if (IsSingleDigit(field, 3)) { rank = 3; valid = true; }
                    else if (field.Length == 3 && field[0] == 0x34 && field[1] == 0x3a &&
                        field[2] >= 0x31 && field[2] <= 0x35)
                    {
                        rank = 4;
                        valid = true;
                    }
                    else if (IsSingleDigit(field, 5)) { rank = 5; valid = true; }
                    else if (IsSingleDigit(field, 7)) { rank = 6; valid = true; }
                    else if (IsSingleDigit(field, 8)) { rank = 7; valid = true; }
                    else if (IsSingleDigit(field, 9)) { rank = 8; valid = true; }
                    else if (field.Length == 2 && field[0] == 0x35 && field[1] == 0x33)
                    {
                        rank = 9;
                        valid = true;
                    }
                    else if (IsColorSelector(field, 38))
                    {
                        rank = 10;
                        valid = ConsumeCanonicalSgrColor(parameters, ref position);
                    }
                    else if (IsColorSelector(field, 48))
                    {
                        rank = 11;
                        valid = ConsumeCanonicalSgrColor(parameters, ref position);
                    }
                    else if (IsColorSelector(field, 58))
                    {
                        rank = 12;
                        valid = ConsumeCanonicalSgrColor(parameters, ref position);
                    }
                    else
                    {
                        return false;
                    }
                    if (!valid || rank <= lastRank) return false;
                    lastRank = rank;
                }
                return lastRank != 0;
            }

            private static bool ConsumeCanonicalSgrColor(
                ReadOnlySpan<byte> parameters,
                ref int position)
            {
                if (!TryTakeField(parameters, ref position, out var mode)) return false;
                if (IsSingleDigit(mode, 5))
                {
                    return TryTakeField(parameters, ref position, out var index) &&
                        TryReadCanonicalUInt32(index, out var value) && value <= 255;
                }
                if (!IsSingleDigit(mode, 2)) return false;
                for (var component = 0; component < 3; component++)
                {
                    if (!TryTakeField(parameters, ref position, out var field) ||
                        !TryReadCanonicalUInt32(field, out var value) || value > 255)
                    {
                        return false;
                    }
                }
                return true;
            }

            private static bool IsCanonicalMarginStatus(ReadOnlySpan<byte> status, byte finalByte)
            {
                if (status.Length < 4 || status[status.Length - 1] != finalByte) return false;
                var body = status.Slice(0, status.Length - 1);
                var separator = body.IndexOf((byte)0x3b);
                return separator >= 0 && body.Slice(separator + 1).IndexOf((byte)0x3b) < 0 &&
                    TryReadCoordinate(body.Slice(0, separator), out _) &&
                    TryReadCoordinate(body.Slice(separator + 1), out _);
            }

            private static bool IsCanonicalXtgettcap(ReadOnlySpan<byte> payload)
            {
                if (!TryUnwrapDcs(payload, out var body)) return false;
                if (body.Length == 3 && body[0] == 0x30 && body[1] == 0x2b && body[2] == 0x72)
                    return true;
                if (body.Length < 7 || body[0] != 0x31 || body[1] != 0x2b || body[2] != 0x72)
                    return false;

                var pairs = body.Slice(3);
                var position = 0;
                var count = 0;
                while (position < pairs.Length)
                {
                    if (!TryTakeField(pairs, ref position, out var pair)) return false;
                    count++;
                    if (count > 256) return false;
                    var separator = pair.IndexOf((byte)0x3d);
                    if (separator < 0 || pair.Slice(separator + 1).IndexOf((byte)0x3d) >= 0 ||
                        !IsCanonicalLowerHex(pair.Slice(0, separator), 64) ||
                        !IsCanonicalLowerHex(pair.Slice(separator + 1), int.MaxValue))
                    {
                        return false;
                    }
                }
                return count != 0;
            }

            private static bool IsCanonicalOscColor(ReadOnlySpan<byte> payload)
            {
                if (payload.Length < 8 || payload[0] != 0x1b || payload[1] != 0x5d ||
                    payload[payload.Length - 2] != 0x1b || payload[payload.Length - 1] != 0x5c)
                {
                    return false;
                }
                var body = payload.Slice(2, payload.Length - 4);
                if (body.Length > 4 && body[0] == 0x34 && body[1] == 0x3b)
                {
                    var rest = body.Slice(2);
                    var separator = rest.IndexOf((byte)0x3b);
                    return separator >= 0 && rest.Slice(separator + 1).IndexOf((byte)0x3b) < 0 &&
                        TryReadCanonicalUInt32(rest.Slice(0, separator), out var index) &&
                        index <= 255 && IsCanonicalRgb(rest.Slice(separator + 1));
                }
                if (body.Length > 3 && body[0] == 0x31 &&
                    body[1] >= 0x30 && body[1] <= 0x32 && body[2] == 0x3b)
                {
                    return IsCanonicalRgb(body.Slice(3));
                }
                return false;
            }

            private static bool IsCanonicalRgb(ReadOnlySpan<byte> color)
            {
                if (color.Length != 18 || color[0] != 0x72 || color[1] != 0x67 ||
                    color[2] != 0x62 || color[3] != 0x3a || color[8] != 0x2f ||
                    color[13] != 0x2f)
                {
                    return false;
                }
                return IsCanonicalRepeatedByteHex(color.Slice(4, 4)) &&
                    IsCanonicalRepeatedByteHex(color.Slice(9, 4)) &&
                    IsCanonicalRepeatedByteHex(color.Slice(14, 4));
            }

            private static bool IsCanonicalRepeatedByteHex(ReadOnlySpan<byte> component)
            {
                return component.Length == 4 && component[0] == component[2] &&
                    component[1] == component[3] && IsCanonicalLowerHex(component, 4);
            }

            private static bool IsCanonicalLowerHex(ReadOnlySpan<byte> value, int maximumLength)
            {
                if (value.IsEmpty || value.Length > maximumLength || value.Length % 2 != 0)
                    return false;
                foreach (var digit in value)
                {
                    if (!((digit >= 0x30 && digit <= 0x39) ||
                        (digit >= 0x61 && digit <= 0x66)))
                    {
                        return false;
                    }
                }
                return true;
            }

            private static bool TryUnwrapDcs(
                ReadOnlySpan<byte> payload,
                out ReadOnlySpan<byte> body)
            {
                if (payload.Length >= 6 && payload[0] == 0x1b && payload[1] == 0x50 &&
                    payload[payload.Length - 2] == 0x1b && payload[payload.Length - 1] == 0x5c)
                {
                    body = payload.Slice(2, payload.Length - 4);
                    return true;
                }
                body = default;
                return false;
            }

            private static bool TryTakeField(
                ReadOnlySpan<byte> bytes,
                ref int position,
                out ReadOnlySpan<byte> field)
            {
                if (position < 0 || position >= bytes.Length)
                {
                    field = default;
                    return false;
                }
                var separator = bytes.Slice(position).IndexOf((byte)0x3b);
                if (separator < 0)
                {
                    field = bytes.Slice(position);
                    position = bytes.Length;
                }
                else
                {
                    if (separator == bytes.Length - position - 1)
                    {
                        field = default;
                        return false;
                    }
                    field = bytes.Slice(position, separator);
                    position += separator + 1;
                }
                return !field.IsEmpty;
            }

            private static bool TryReadCoordinate(ReadOnlySpan<byte> field, out uint value)
            {
                return TryReadCanonicalUInt32(field, out value) && value >= 1 && value <= 512;
            }

            private static bool TryReadCanonicalUInt32(
                ReadOnlySpan<byte> field,
                out uint value)
            {
                value = 0;
                if (field.IsEmpty || (field.Length > 1 && field[0] == 0x30)) return false;
                foreach (var digit in field)
                {
                    if (digit < 0x30 || digit > 0x39) return false;
                    var numeric = (uint)(digit - 0x30);
                    if (value > (uint.MaxValue - numeric) / 10) return false;
                    value = value * 10 + numeric;
                }
                return true;
            }

            private static bool IsSingleDigit(ReadOnlySpan<byte> field, int value)
            {
                return field.Length == 1 && field[0] == 0x30 + value;
            }

            private static bool IsColorSelector(ReadOnlySpan<byte> field, int value)
            {
                return field.Length == 2 && field[0] == 0x30 + value / 10 &&
                    field[1] == 0x30 + value % 10;
            }

            private ushort ReadUInt16(int offset, string field)
            {
                EnsureRange(offset, 2, field);
                return BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
            }

            private uint ReadUInt32(int offset, string field)
            {
                EnsureRange(offset, 4, field);
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            }

            private ulong ReadUInt64(int offset, string field)
            {
                EnsureRange(offset, 8, field);
                return BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
            }

            private void ExpectUInt16(int offset, int expected, string field)
            {
                if (ReadUInt16(offset, field) != expected)
                    throw Error(field + " is unsupported");
            }

            private void ExpectUInt32(int offset, uint expected, string field)
            {
                if (ReadUInt32(offset, field) != expected)
                    throw Error(field + " is invalid");
            }

            private void ExpectUInt64(int offset, ulong expected, string field)
            {
                if (ReadUInt64(offset, field) != expected)
                    throw Error(field + " is invalid");
            }

            private void EnsureRange(int offset, int length, string field)
            {
                if (offset < 0 || length < 0 ||
                    (ulong)offset + (ulong)length > (ulong)bytes.Length)
                {
                    throw Error(field + " is truncated");
                }
            }

            private static uint Crc32C(ReadOnlySpan<byte> data)
            {
                var crc = uint.MaxValue;
                for (var index = 0; index < data.Length; index++)
                {
                    crc ^= data[index];
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var mask = unchecked(0U - (crc & 1U));
                        crc = (crc >> 1) ^ (Crc32CPolynomial & mask);
                    }
                }
                return ~crc;
            }

            private static ulong Add(ulong left, ulong right, string field)
            {
                try
                {
                    return checked(left + right);
                }
                catch (OverflowException)
                {
                    throw Error(field + " overflows");
                }
            }

            private static ulong Multiply(ulong left, ulong right, string field)
            {
                try
                {
                    return checked(left * right);
                }
                catch (OverflowException)
                {
                    throw Error(field + " overflows");
                }
            }

            private static int ToInt(ulong value, string field)
            {
                if (value > int.MaxValue) throw Error(field + " exceeds signed 32-bit range");
                return (int)value;
            }
        }
    }
}
