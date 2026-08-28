using System;
using System.Buffers.Binary;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation
{
    /// <summary>
    /// Managed-only validation for Host Effect Frame V1. Program can call Run after the native
    /// export is integrated; these checks intentionally require no native DLL entry point.
    /// </summary>
    internal static class HostEffectFrameV1Validation
    {
        private const int HeaderSize = 64;
        private const int RecordSize = 40;
        private const uint Crc32CPolynomial = 0x82f6_3b78;

        internal static void Run()
        {
            ValidateCanonicalFrame();
            ValidateEmptyFrame();
            ValidateHeaderAndCrcRejections();
            ValidateRecordAndLayoutRejections();
            ValidateOrderRejections();
            ValidatePtyReplyRejections();
        }

        internal static byte[] CreateCanonicalFrame()
        {
            return Build(
                new RecordValue(5, 2, 0, Bytes("\x1b[?1;2c")),
                new RecordValue(5, 3, 0, Bytes("\x1b[>0;0;0c")),
                new RecordValue(5, 4, 0, Bytes("\x1b[0n")),
                new RecordValue(5, 5, 0, Bytes("\x1b[?0n")),
                new RecordValue(5, 6, 0, Bytes("\x1b[1;512R")),
                new RecordValue(6, 1, 0, Bytes("\x1b[?512;1R")));
        }

        private static void ValidateCanonicalFrame()
        {
            var source = CreateCanonicalFrame();
            var decoded = NativeTerminalHostEffectFrameV1Decoder.Decode(
                (ReadOnlySpan<byte>)source);
            Require(decoded.Records.Count == 6,
                "The canonical host effect record count changed.");
            var first = decoded.Records[0];
            Require(first.Kind == NativeTerminalHostEffectKindV1.PtyReply &&
                first.SourceSequence == 5 && first.SourceOffsetEnd == 2 &&
                first.Ordinal == 0 && BytesEqual(first.Payload, 0x1b, 0x5b, 0x3f, 0x31, 0x3b, 0x32, 0x63),
                "The first canonical PTY reply was decoded incorrectly.");
            Require(BytesEqual(decoded.Records[1].Payload, Bytes("\x1b[>0;0;0c")) &&
                BytesEqual(decoded.Records[2].Payload, Bytes("\x1b[0n")) &&
                BytesEqual(decoded.Records[3].Payload, Bytes("\x1b[?0n")) &&
                BytesEqual(decoded.Records[4].Payload, Bytes("\x1b[1;512R")) &&
                BytesEqual(decoded.Records[5].Payload, Bytes("\x1b[?512;1R")),
                "One of the canonical PTY reply forms was decoded incorrectly.");
            Require(decoded.Records[4].SourceOffsetEnd == 6 &&
                decoded.Records[5].SourceSequence == 6,
                "Canonical source ordering metadata was decoded incorrectly.");

            source[PayloadOffset(source)] ^= 0xff;
            Require(BytesEqual(first.Payload, 0x1b, 0x5b, 0x3f, 0x31, 0x3b, 0x32, 0x63),
                "Mutating source bytes changed an owned host effect payload.");
            Require(!(first.Payload is byte[]),
                "The host effect DTO exposed its mutable owned byte array.");
            Require(NativeTerminalHostEffectFrameV1.FormatVersion == 1 &&
                NativeTerminalHostEffectFrameV1Decoder.MaximumFrameBytes == 65_710_862,
                "Host Effect Frame V1 public constants changed.");
        }

        private static void ValidateEmptyFrame()
        {
            var empty = Build();
            Require(empty.Length == HeaderSize && PayloadOffset(empty) == HeaderSize,
                "The empty host effect fixture is not canonical.");
            var decoded = NativeTerminalHostEffectFrameV1Decoder.Decode(empty);
            Require(decoded.Records.Count == 0,
                "A canonical empty host effect frame gained records.");

            var trailing = new byte[HeaderSize + 1];
            empty.CopyTo(trailing, 0);
            PutUInt32(trailing, 20, trailing.Length);
            RefreshCrc(trailing);
            Reject(trailing);
        }

        private static void ValidateHeaderAndCrcRejections()
        {
            Throws<ArgumentNullException>(
                () => NativeTerminalHostEffectFrameV1Decoder.Decode((byte[])null),
                "A null host effect frame was accepted.");
            Reject(new byte[HeaderSize - 1]);
            Reject(new byte[NativeTerminalHostEffectFrameV1Decoder.MaximumFrameBytes + 1]);

            Reject(MutateAndRefresh(CreateCanonicalFrame(), 0, (byte)'X'));
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 5, (byte)'2'));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 8, 2));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 10, 1));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 12, HeaderSize + 8));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 14, RecordSize + 8));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), 20, uint.MaxValue));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 24, HeaderSize + 8));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 40, 0));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 40, 3));
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 48, 1));
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 55, 1));

            var badHeaderCrc = CreateCanonicalFrame();
            badHeaderCrc[40] ^= 1;
            Reject(badHeaderCrc);

            var badPayloadCrc = CreateCanonicalFrame();
            badPayloadCrc[PayloadOffset(badPayloadCrc)] ^= 1;
            Reject(badPayloadCrc);
        }

        private static void ValidateRecordAndLayoutRejections()
        {
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), 16, uint.MaxValue));
            Reject(PutUInt64AndRefresh(
                CreateCanonicalFrame(),
                32,
                checked((ulong)(PayloadOffset(CreateCanonicalFrame()) + 1))));

            var first = HeaderSize;
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first, 0));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first, 2));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 4, 1));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 16, 0));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 16, 1024 * 1024 + 1));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 36, 1));

            var second = HeaderSize + RecordSize;
            var secondPayloadOffset = checked((int)ReadUInt64(
                CreateCanonicalFrame(),
                second + 24));
            Reject(PutUInt64AndRefresh(
                CreateCanonicalFrame(),
                second + 24,
                checked((ulong)(secondPayloadOffset + 1))));
            Reject(PutUInt64AndRefresh(
                CreateCanonicalFrame(),
                second + 24,
                checked((ulong)(secondPayloadOffset - 1))));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), first + 24, ulong.MaxValue));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 32, 0));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 32, uint.MaxValue));

            var trailing = CreateCanonicalFrame();
            Array.Resize(ref trailing, trailing.Length + 1);
            PutUInt32(trailing, 20, trailing.Length);
            RefreshCrc(trailing);
            Reject(trailing);
        }

        private static void ValidateOrderRejections()
        {
            var second = HeaderSize + RecordSize;
            var third = HeaderSize + 2 * RecordSize;

            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), second + 16, 2));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), second + 20, 1));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), second + 20, 2));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), HeaderSize + 20, 1));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), third + 20, 1));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), third + 16, 1));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), third + 8, 4));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), second + 8, 4));
        }

        private static void ValidatePtyReplyRejections()
        {
            Reject(Build(new RecordValue(1, 1, 0, new byte[] { 0xff })));
            Reject(Build(new RecordValue(1, 1, 0, Array.Empty<byte>())));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[?1;2C"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[?1;3c"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[>0;0;1c"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[00n"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[?00n"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[0;1R"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[01;1R"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[1;01R"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[513;1R"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[1;513R"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[?512;512Rx"))));
            Reject(Build(new RecordValue(1, 1, 0, Bytes("\x1b[?1234;1R"))));
        }

        private static byte[] Build(params RecordValue[] records)
        {
            var payloadBytes = 0;
            foreach (var record in records)
                payloadBytes = checked(payloadBytes + record.Payload.Length);
            var payloadOffset = checked(HeaderSize + RecordSize * records.Length);
            var frame = new byte[checked(payloadOffset + payloadBytes)];

            Encoding.ASCII.GetBytes("TLHEF1\0\0").CopyTo(frame, 0);
            PutUInt16(frame, 8, 1);
            PutUInt16(frame, 10, 0);
            PutUInt16(frame, 12, HeaderSize);
            PutUInt16(frame, 14, RecordSize);
            PutUInt32(frame, 16, records.Length);
            PutUInt32(frame, 20, frame.Length);
            PutUInt64(frame, 24, HeaderSize);
            PutUInt64(frame, 32, checked((ulong)payloadOffset));
            PutUInt64(frame, 40, 1);

            var dataOffset = payloadOffset;
            for (var index = 0; index < records.Length; index++)
            {
                var record = records[index];
                var offset = HeaderSize + index * RecordSize;
                PutUInt32(frame, offset, 1);
                PutUInt64(frame, offset + 8, record.SourceSequence);
                PutUInt32(frame, offset + 16, record.SourceOffsetEnd);
                PutUInt32(frame, offset + 20, record.Ordinal);
                PutUInt64(frame, offset + 24, checked((ulong)dataOffset));
                PutUInt32(frame, offset + 32, record.Payload.Length);
                record.Payload.CopyTo(frame, dataOffset);
                dataOffset = checked(dataOffset + record.Payload.Length);
            }

            RefreshCrc(frame);
            return frame;
        }

        private static int PayloadOffset(byte[] frame)
        {
            return checked((int)ReadUInt64(frame, 32));
        }

        private static byte[] MutateAndRefresh(byte[] frame, int offset, byte value)
        {
            var result = (byte[])frame.Clone();
            result[offset] = value;
            RefreshCrc(result);
            return result;
        }

        private static byte[] PutUInt16AndRefresh(byte[] frame, int offset, int value)
        {
            var result = (byte[])frame.Clone();
            PutUInt16(result, offset, value);
            RefreshCrc(result);
            return result;
        }

        private static byte[] PutUInt32AndRefresh(byte[] frame, int offset, uint value)
        {
            var result = (byte[])frame.Clone();
            PutUInt32(result, offset, value);
            RefreshCrc(result);
            return result;
        }

        private static byte[] PutUInt64AndRefresh(byte[] frame, int offset, ulong value)
        {
            var result = (byte[])frame.Clone();
            PutUInt64(result, offset, value);
            RefreshCrc(result);
            return result;
        }

        private static void RefreshCrc(byte[] bytes)
        {
            PutUInt32(bytes, 56, Crc32C(bytes.AsSpan(HeaderSize)));
            PutUInt32(bytes, 60, Crc32C(bytes.AsSpan(0, 60)));
        }

        private static uint Crc32C(ReadOnlySpan<byte> bytes)
        {
            var crc = uint.MaxValue;
            for (var index = 0; index < bytes.Length; index++)
            {
                crc ^= bytes[index];
                for (var bit = 0; bit < 8; bit++)
                {
                    var mask = unchecked(0U - (crc & 1U));
                    crc = (crc >> 1) ^ (Crc32CPolynomial & mask);
                }
            }
            return ~crc;
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        }

        private static ulong ReadUInt64(byte[] bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
        }

        private static void PutUInt16(byte[] bytes, int offset, int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(offset, 2),
                checked((ushort)value));
        }

        private static void PutUInt32(byte[] bytes, int offset, int value)
        {
            PutUInt32(bytes, offset, checked((uint)value));
        }

        private static void PutUInt32(byte[] bytes, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        }

        private static void PutUInt64(byte[] bytes, int offset, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
        }

        private static bool BytesEqual(
            System.Collections.Generic.IReadOnlyList<byte> actual,
            params byte[] expected)
        {
            if (actual.Count != expected.Length) return false;
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index]) return false;
            }
            return true;
        }

        private static byte[] Bytes(string value)
        {
            return Encoding.ASCII.GetBytes(value);
        }

        private static void Reject(byte[] bytes)
        {
            Throws<NativeTerminalHostEffectFrameV1DecodeException>(
                () => NativeTerminalHostEffectFrameV1Decoder.Decode(bytes),
                "A malformed Host Effect Frame V1 payload was accepted.");
        }

        private static void Throws<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private readonly struct RecordValue
        {
            internal RecordValue(
                ulong sourceSequence,
                uint sourceOffsetEnd,
                uint ordinal,
                byte[] payload)
            {
                SourceSequence = sourceSequence;
                SourceOffsetEnd = sourceOffsetEnd;
                Ordinal = ordinal;
                Payload = payload;
            }

            internal ulong SourceSequence { get; }
            internal uint SourceOffsetEnd { get; }
            internal uint Ordinal { get; }
            internal byte[] Payload { get; }
        }
    }
}
