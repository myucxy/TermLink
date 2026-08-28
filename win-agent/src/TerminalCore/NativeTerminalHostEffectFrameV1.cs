using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TermLink.TerminalCore
{
    /// <summary>
    /// A deterministic rejection of malformed or unsupported TermLink Host Effect Frame V1 data.
    /// </summary>
    public sealed class NativeTerminalHostEffectFrameV1DecodeException : ArgumentException
    {
        internal NativeTerminalHostEffectFrameV1DecodeException(string message)
            : base("host effect frame v1: " + message)
        {
        }
    }

    public enum NativeTerminalHostEffectKindV1
    {
        PtyReply = 1
    }

    /// <summary>
    /// One ordered host effect. Payload is the exact, non-empty byte sequence to write to the PTY.
    /// </summary>
    public readonly struct NativeTerminalHostEffectRecordV1
    {
        private readonly IReadOnlyList<byte> payload;

        internal NativeTerminalHostEffectRecordV1(
            NativeTerminalHostEffectKindV1 kind,
            ulong sourceSequence,
            uint sourceOffsetEnd,
            uint ordinal,
            byte[] payload)
        {
            Kind = kind;
            SourceSequence = sourceSequence;
            SourceOffsetEnd = sourceOffsetEnd;
            Ordinal = ordinal;
            this.payload = Array.AsReadOnly(payload ?? throw new ArgumentNullException(nameof(payload)));
        }

        public NativeTerminalHostEffectKindV1 Kind { get; }
        public ulong SourceSequence { get; }
        public uint SourceOffsetEnd { get; }
        public uint Ordinal { get; }

        /// <summary>
        /// An owned, read-only copy of the raw PTY reply bytes. No text encoding is implied.
        /// </summary>
        public IReadOnlyList<byte> Payload => payload;
    }

    /// <summary>
    /// Immutable ordered PTY replies emitted while applying terminal output.
    /// </summary>
    public sealed class NativeTerminalHostEffectFrameV1
    {
        internal NativeTerminalHostEffectFrameV1(NativeTerminalHostEffectRecordV1[] records)
        {
            Records = Array.AsReadOnly(
                records ?? throw new ArgumentNullException(nameof(records)));
        }

        public const int FormatVersion = 1;
        public IReadOnlyList<NativeTerminalHostEffectRecordV1> Records { get; }
    }

    /// <summary>
    /// Strict, allocation-bounded decoder for canonical TermLink Host Effect Frame V1 data.
    /// </summary>
    public static class NativeTerminalHostEffectFrameV1Decoder
    {
        public const int MaximumFrameBytes = 65_710_862;

        private const int HeaderSize = 64;
        private const int RecordSize = 40;
        private const ulong RecordsOffset = HeaderSize;
        private const int HeaderCrcEnd = 60;
        private const ushort MajorVersion = 1;
        private const ushort MinorVersion = 0;
        private const ulong RequiredFeatures = 1;
        private const uint MaximumSourceOffset = 1024 * 1024;
        private const int MaximumPtyReplyBytes = 11;
        private const uint PtyReplyKind = 1;
        private const uint Crc32CPolynomial = 0x82f6_3b78;

        private static readonly byte[] Magic =
        {
            (byte)'T', (byte)'L', (byte)'H', (byte)'E',
            (byte)'F', (byte)'1', 0, 0
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

        public static NativeTerminalHostEffectFrameV1 Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Decode(bytes.AsSpan());
        }

        public static NativeTerminalHostEffectFrameV1 Decode(ReadOnlySpan<byte> bytes)
        {
            try
            {
                return new Decoder(bytes).Decode();
            }
            catch (NativeTerminalHostEffectFrameV1DecodeException)
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

        private static NativeTerminalHostEffectFrameV1DecodeException Error(string message)
        {
            return new NativeTerminalHostEffectFrameV1DecodeException(message);
        }

        private ref struct Decoder
        {
            private readonly ReadOnlySpan<byte> bytes;

            internal Decoder(ReadOnlySpan<byte> bytes)
            {
                this.bytes = bytes;
            }

            internal NativeTerminalHostEffectFrameV1 Decode()
            {
                if (bytes.Length < HeaderSize) throw Error("header is truncated");
                if (bytes.Length > MaximumFrameBytes) throw Error("frame exceeds the hard limit");
                if (!bytes.Slice(0, Magic.Length).SequenceEqual(Magic)) throw Error("bad magic");

                ExpectUInt16(8, MajorVersion, "major version");
                ExpectUInt16(10, MinorVersion, "minor version");
                ExpectUInt16(12, HeaderSize, "header size");
                ExpectUInt16(14, RecordSize, "record size");

                var recordCountValue = ReadUInt32(16, "record count");
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

                ExpectUInt64(40, RequiredFeatures, "required features");
                ExpectUInt64(48, 0, "header reserved field");

                var payloadCrc = ReadUInt32(56, "payload CRC32C");
                var headerCrc = ReadUInt32(60, "header CRC32C");
                if (Crc32C(bytes.Slice(0, HeaderCrcEnd)) != headerCrc)
                    throw Error("header CRC32C mismatch");
                if (Crc32C(bytes.Slice(HeaderSize)) != payloadCrc)
                    throw Error("payload CRC32C mismatch");

                var recordCount = ToInt(recordCountValue, "record count");
                var records = new NativeTerminalHostEffectRecordV1[recordCount];
                ulong expectedDataOffset = payloadOffset;
                ulong previousSequence = 0;
                uint previousSourceOffset = 0;
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
                        sequence,
                        sourceOffset,
                        ordinal);

                    var dataOffset = ReadUInt64(recordOffset + 24, "record payload offset");
                    if (dataOffset != expectedDataOffset)
                        throw Error("record payloads are not contiguous");
                    var dataLength = ReadUInt32(recordOffset + 32, "record payload length");
                    if (dataLength == 0) throw Error("record payload is empty");
                    ExpectUInt32(recordOffset + 36, 0, "record reserved field");
                    var dataEnd = Add(dataOffset, dataLength, "record payload end");
                    if (dataEnd > totalLength) throw Error("record payload is truncated");
                    var payload = bytes.Slice(
                        ToInt(dataOffset, "record payload offset"),
                        ToInt(dataLength, "record payload length"));
                    ValidatePtyReply(payload);
                    records[index] = new NativeTerminalHostEffectRecordV1(
                        NativeTerminalHostEffectKindV1.PtyReply,
                        sequence,
                        sourceOffset,
                        ordinal,
                        payload.ToArray());

                    expectedDataOffset = dataEnd;
                    previousSequence = sequence;
                    previousSourceOffset = sourceOffset;
                    hasPrevious = true;
                }

                if (expectedDataOffset != totalLength)
                    throw Error("frame has trailing or unreferenced payload bytes");
                return new NativeTerminalHostEffectFrameV1(records);
            }

            private static void ValidateOrder(
                bool hasPrevious,
                ulong previousSequence,
                uint previousSourceOffset,
                ulong sequence,
                uint sourceOffset,
                uint ordinal)
            {
                if (ordinal != 0) throw Error("V1 ordinal must be zero");
                if (!hasPrevious) return;

                if (sequence < previousSequence ||
                    (sequence == previousSequence && sourceOffset <= previousSourceOffset))
                {
                    throw Error("records are not strictly ordered by source key");
                }
            }

            private static void ValidatePtyReply(ReadOnlySpan<byte> payload)
            {
                if (payload.Length > MaximumPtyReplyBytes)
                    throw Error("PTY reply exceeds 11 bytes");
                if (payload.SequenceEqual(PrimaryDeviceAttributesReply) ||
                    payload.SequenceEqual(SecondaryDeviceAttributesReply) ||
                    payload.SequenceEqual(DeviceStatusReply) ||
                    payload.SequenceEqual(PrivateDeviceStatusReply))
                {
                    return;
                }

                if (payload.Length < 6 || payload[0] != 0x1b || payload[1] != 0x5b ||
                    payload[payload.Length - 1] != 0x52)
                {
                    throw Error("PTY reply is not a supported canonical response");
                }

                var index = 2;
                if (payload[index] == 0x3f) index++;
                if (!TryReadCoordinate(payload, ref index, out var row) ||
                    index >= payload.Length - 1 || payload[index] != 0x3b)
                {
                    throw Error("PTY cursor reply row is invalid");
                }
                index++;
                if (!TryReadCoordinate(payload, ref index, out var column) ||
                    index != payload.Length - 1 || row > 512 || column > 512)
                {
                    throw Error("PTY cursor reply column is invalid");
                }
            }

            private static bool TryReadCoordinate(
                ReadOnlySpan<byte> payload,
                ref int index,
                out uint value)
            {
                value = 0;
                if (index >= payload.Length - 1 || payload[index] < 0x31 || payload[index] > 0x39)
                    return false;
                do
                {
                    value = value * 10 + (uint)(payload[index] - 0x30);
                    index++;
                }
                while (index < payload.Length - 1 &&
                    payload[index] >= 0x30 && payload[index] <= 0x39);
                return true;
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
