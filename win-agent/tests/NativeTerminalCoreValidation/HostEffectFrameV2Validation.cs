using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation
{
    /// <summary>
    /// Managed-only validation for Host Effect Frame V2. Program can call Run after the V2
    /// native apply surface is integrated; these checks intentionally require no native symbol.
    /// </summary>
    internal static class HostEffectFrameV2Validation
    {
        private const int HeaderSize = 64;
        private const int RecordSize = 40;
        private const uint Crc32CPolynomial = 0x82f6_3b78;

        internal static void Run()
        {
            ValidateCanonicalFrameAndOwnership();
            ValidateEmptyFrameAndPublishedLimits();
            ValidateHeaderCrcAndLayoutRejections();
            ValidateOrdinalRejections();
            ValidateReplyGrammar();
            ValidateReplyLengthBoundary();
            ValidateResizeRequests();
            ValidatePlatformActions();
        }

        private static void ValidateResizeRequests()
        {
            var payload = new byte[8]; PutUInt32(payload, 0, 132);
            var frame = Build(new RecordValue(7, 12, 0, payload));
            PutUInt64(frame, 40, 3); PutUInt32(frame, 64, 2); RefreshCrc(frame);
            var decoded = NativeTerminalHostEffectFrameV2Decoder.Decode(frame);
            Require(decoded.Records[0].Kind == NativeTerminalHostEffectKindV2.ResizeRequest, "Resize request was interpreted as PTY input.");
            Reject(PutUInt64AndRefresh(frame, 40, 1));
            Reject(PutUInt32AndRefresh(frame, PayloadOffset(frame), 513));
            Reject(PutUInt32AndRefresh(frame, PayloadOffset(frame), 0));
            Reject(PutUInt32AndRefresh(frame, PayloadOffset(frame) + 4, 24));
            Reject(PutUInt32AndRefresh(frame, 64, 3));
        }

        private static void ValidatePlatformActions()
        {
            var frame = Build(new RecordValue(7, 12, 0, new byte[] { 2, 7, 255, 3, 1, 0, 25 }));
            PutUInt64(frame, 40, 5); PutUInt32(frame, 64, 3); RefreshCrc(frame);
            var decoded = NativeTerminalHostEffectFrameV2Decoder.Decode(frame);
            Require(decoded.Records[0].Kind == NativeTerminalHostEffectKindV2.PlatformAction, "Sound became PTY input.");
            var sound = NativeTerminalPlatformAction.Decode(System.Linq.Enumerable.ToArray(decoded.Records[0].Payload));
            Require(sound.Volume == 7 && sound.Duration32nds == 255 && sound.Notes.Count == 3 && sound.Notes[1] == 0, "Sound grammar changed.");
            Reject(PutUInt64AndRefresh(frame, 40, 1));
            Reject(PutUInt32AndRefresh(frame, PayloadOffset(frame), 0));
            var bad = (byte[])frame.Clone(); bad[PayloadOffset(bad) + 4] = 26; RefreshCrc(bad); Reject(bad);
        }

        internal static byte[] CreateCanonicalFrame()
        {
            return Build(
                new RecordValue(5, 2, 0, Bytes("\x1b[?1;2c")),
                new RecordValue(5, 2, 1, Bytes("\x1b]4;1;rgb:ffff/0000/1212\x1b\\")),
                new RecordValue(5, 3, 0, Bytes("\x1b[?31u")),
                new RecordValue(5, 4, 0, Bytes("\x1b[?25;1$y")),
                new RecordValue(
                    5,
                    5,
                    0,
                    Bytes("\x1bP1$r0;1;2;3;4:5;5;7;8;9;53;38;5;255;48;2;1;2;3m\x1b\\")),
                new RecordValue(
                    5,
                    6,
                    0,
                    Bytes("\x1bP1+r436f=323536;524742=382f382f38\x1b\\")),
                new RecordValue(6, 1, 0, Bytes("\x1b]10;rgb:1212/3434/5656\x1b\\")));
        }

        private static void ValidateCanonicalFrameAndOwnership()
        {
            var source = CreateCanonicalFrame();
            var decoded = NativeTerminalHostEffectFrameV2Decoder.Decode(
                (ReadOnlySpan<byte>)source);
            Require(decoded.Records.Count == 7,
                "The canonical Host Effect Frame V2 record count changed.");
            Require(decoded.Records[0].Kind == NativeTerminalHostEffectKindV2.PtyReply &&
                decoded.Records[0].SourceSequence == 5 &&
                decoded.Records[0].SourceOffsetEnd == 2 &&
                decoded.Records[0].Ordinal == 0,
                "The first canonical V2 record metadata was decoded incorrectly.");
            Require(decoded.Records[1].SourceSequence == 5 &&
                decoded.Records[1].SourceOffsetEnd == 2 &&
                decoded.Records[1].Ordinal == 1,
                "A repeated V2 source key lost its contiguous ordinal.");
            Require(BytesEqual(decoded.Records[4].Payload,
                    Bytes("\x1bP1$r0;1;2;3;4:5;5;7;8;9;53;38;5;255;48;2;1;2;3m\x1b\\")) &&
                BytesEqual(decoded.Records[5].Payload,
                    Bytes("\x1bP1+r436f=323536;524742=382f382f38\x1b\\")),
                "A canonical extended V2 reply was decoded incorrectly.");

            var originalRawMagic = decoded.RawBytes[0];
            var originalPayloadByte = decoded.Records[0].Payload[0];
            source[0] ^= 0xff;
            source[PayloadOffset(source)] ^= 0xff;
            Require(decoded.RawBytes[0] == originalRawMagic &&
                decoded.Records[0].Payload[0] == originalPayloadByte,
                "Mutating source bytes changed owned V2 frame data.");
            Require(!(decoded.RawBytes is byte[]) && !(decoded.Records[0].Payload is byte[]),
                "The V2 DTO exposed a mutable owned byte array.");
        }

        private static void ValidateEmptyFrameAndPublishedLimits()
        {
            var empty = Build();
            var decoded = NativeTerminalHostEffectFrameV2Decoder.Decode(empty);
            Require(empty.Length == HeaderSize && decoded.Records.Count == 0 &&
                decoded.RawBytes.Count == HeaderSize,
                "A canonical empty Host Effect Frame V2 was not preserved.");
            Require(NativeTerminalHostEffectFrameV2.FormatVersion == 2 &&
                NativeTerminalHostEffectFrameV2Decoder.MaximumFrameBytes == 123_033_008 &&
                NativeTerminalHostEffectFrameV2Decoder.MaximumEffects == 1_398_102 &&
                NativeTerminalHostEffectFrameV2Decoder.MaximumPtyReplyBytes == 65_536 &&
                NativeTerminalHostEffectFrameV2Decoder.MaximumAggregatePayloadBytes == 67_108_864,
                "Host Effect Frame V2 public limits changed.");

            Throws<ArgumentNullException>(
                () => NativeTerminalHostEffectFrameV2Decoder.Decode((byte[])null),
                "A null Host Effect Frame V2 was accepted.");
            Reject(new byte[HeaderSize - 1]);
            Reject(new byte[NativeTerminalHostEffectFrameV2Decoder.MaximumFrameBytes + 1]);
        }

        private static void ValidateHeaderCrcAndLayoutRejections()
        {
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 0, (byte)'X'));
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 5, (byte)'1'));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 8, 1));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 10, 1));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 12, HeaderSize + 8));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 14, RecordSize + 8));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), 16, 1_398_103));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), 20, uint.MaxValue));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 24, HeaderSize + 8));
            Reject(PutUInt64AndRefresh(
                CreateCanonicalFrame(),
                32,
                checked((ulong)(PayloadOffset(CreateCanonicalFrame()) + 1))));
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

            var first = HeaderSize;
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first, 0));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first, 2));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 4, 1));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 16, 0));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 16, 1024 * 1024 + 1));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), first + 24, ulong.MaxValue));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 32, 0));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 32, uint.MaxValue));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), first + 36, 1));

            var second = HeaderSize + RecordSize;
            var secondPayload = ReadUInt64(CreateCanonicalFrame(), second + 24);
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), second + 24, secondPayload - 1));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), second + 24, secondPayload + 1));

            var trailing = CreateCanonicalFrame();
            Array.Resize(ref trailing, trailing.Length + 1);
            PutUInt32(trailing, 20, trailing.Length);
            RefreshCrc(trailing);
            Reject(trailing);
        }

        private static void ValidateOrdinalRejections()
        {
            Reject(Build(new RecordValue(1, 1, 1, Bytes("\x1b[0n"))));
            Reject(Build(
                new RecordValue(1, 1, 0, Bytes("\x1b[0n")),
                new RecordValue(1, 1, 0, Bytes("\x1b[?0n"))));
            Reject(Build(
                new RecordValue(1, 1, 0, Bytes("\x1b[0n")),
                new RecordValue(1, 1, 2, Bytes("\x1b[?0n"))));
            Reject(Build(
                new RecordValue(1, 1, 0, Bytes("\x1b[0n")),
                new RecordValue(1, 2, 1, Bytes("\x1b[?0n"))));
            Reject(Build(
                new RecordValue(2, 1, 0, Bytes("\x1b[0n")),
                new RecordValue(1, 2, 0, Bytes("\x1b[?0n"))));
            Reject(Build(
                new RecordValue(1, 2, 0, Bytes("\x1b[0n")),
                new RecordValue(1, 1, 0, Bytes("\x1b[?0n"))));
        }

        private static void ValidateReplyGrammar()
        {
            foreach (var reply in new[] { "\u001b/Z", "\u001bP1!uB\u001b\\", "\u001bP0!u%5\u001b\\", "\u0090" + "1!uB\u009c", "\u009b" + "0n" })
            {
                var payload = Encoding.Latin1.GetBytes(reply);
                var result = NativeTerminalHostEffectFrameV2Decoder.Decode(Build(new RecordValue(1, 1, 0, payload)));
                Require(BytesEqual(result.Records[0].Payload, payload), "Traditional reply bytes were rewritten");
            }
            foreach (var reply in new[] { "\u001bP1!u<\u001b\\", "\u001bP0!uBAD\u001b\\", "\u001bP01!uB\u001b\\", "\u0090" + "1!uB\u001b\\", "\u0090" + "1!uB", "\u009b" + "31m" })
                Reject(Build(new RecordValue(1, 1, 0, Encoding.Latin1.GetBytes(reply))));
            foreach (var reply in new[]
            {
                "\x1b[?1;2c",
                "\x1b[>0;0;0c",
                "\x1b[0n",
                "\x1b[?0n",
                "\x1b[1;512R",
                "\x1b[?512;1R",
                "\x1b[?0u",
                "\x1b[?31u",
                "\x1b[8;1;512t",
                "\x1b[8;512;1t",
            "\x1b[6;20;10t",
            "\x1b[4;524288;524288t",
                "\x1b[4;2$y",
                "\x1b[?9999;0$y",
                "\x1bP0$r\x1b\\",
                "\x1bP1$r0m\x1b\\",
                "\x1bP1$r1;512r\x1b\\",
                "\x1bP1$r1;512s\x1b\\",
                "\x1bP1$r6 q\x1b\\",
                "\x1bP1$r2\"q\x1b\\",
                "\x1bP1$r2*x\x1b\\",
                "\x1bP>|TermLink(1)\x1b\\",
                "\u001bP!|00000000\u001b\\",
                "\u001bP1$u1;1;1;@;@;@;0;2;L;BBAA\u001b\\",
                "\u001bP2$u1/9/512\u001b\\", "\u001bP2$u\u001b\\", "\u001bP2$s\u001b\\",
                "\u001bP2$s0;2;100;0;0/255;1;360;50;100\u001b\\",
                "\u001b[2;1;1;128;128;1;0x",
                "\u001b[3;1;1;128;128;1;0x",
                "\u001b[?13n", "\u001b[?23n", "\u001b[?27;0;0;5n",
                "\u001b[?53n", "\u001b[?57;0n", "\u001b[?70n", "\u001b[?83n",
                "\u001b[24;80;1;1;1\"w", "\u001bP42!~FE9D\u001b\\",
                "\x1bP0+r\x1b\\",
                "\x1bP1+r436f=323536;524742=382f382f38\x1b\\",
                "\x1b]4;255;rgb:ffff/0000/1212\x1b\\",
                "\x1b]10;rgb:ffff/0000/1212\x1b\\",
                "\x1b]11;rgb:ffff/0000/1212\x1b\\",
                "\x1b]12;rgb:ffff/0000/1212\x1b\\"
            })
            {
                _ = NativeTerminalHostEffectFrameV2Decoder.Decode(
                    Build(new RecordValue(1, 1, 0, Bytes(reply))));
            }

            foreach (var reply in new[]
            {
                "\x1b[31m",
                "\u001bP!|12345678\u001b\\", "\u001b[?99n",
                "\u001bP1$u1;1;1;@;@;@;0;2;L;BBZZ\u001b\\",
                "\u001bP2$u9/9\u001b\\", "\u001bP2$u513\u001b\\", "\u001bP2$u9/\u001b\\",
                "\u001bP2$s0;2;101;0;0\u001b\\", "\u001bP2$s0;2;1;0;0/0;2;1;0;0\u001b\\",
                "\u001b[0;80;1;1;1\"w", "\u001b[24;513;1;1;1\"w",
                "\u001bP00!~1234\u001b\\", "\u001bP1!~ffff\u001b\\",
                "\u001bP1!~FFFFX\u001b\\", "\u001bP65535!~FFFF\u001b\\",
                "\x1b[8;0;1t",
                "\x1b[8;1;513t",
                "\x1b[8;01;1t",
                "\x1b[8;1;1;1t",
                "\x1b[6;1025;10t",
                "\x1b[?32u",
                "\x1b[01;1R",
                "\x1b[?025;1$y",
                "\x1b[?25;5$y",
                "\x1bP1$rarbitrary\x1b\\",
                "\x1bP>|TermLink(01)\x1b\\",
                "\x1bP>|Other(1)\x1b\\",
                "\x1bP1$r0;2;1m\x1b\\",
                "\x1bP1+r436F=323536\x1b\\",
                "\x1bP1+r436f=323536;\x1b\\",
                "\x1bP1+r436f=323536=30\x1b\\",
                "\x1bP0+r544e\x1b\\",
                "\x1b]4;01;rgb:ffff/0000/1212\x1b\\",
                "\x1b]4;1;rgb:1234/0000/1212\x1b\\",
                "\x1b]10;rgb:FFFF/0000/1212\x1b\\",
                "\x1b]10;rgb:ffff/0000/1212\x07",
                "\x1b]10;rgb:ffff/0000/1212\u009c"
            })
            {
                Reject(
                    Build(new RecordValue(1, 1, 0, Bytes(reply))),
                    "A malformed V2 reply was accepted: " + Convert.ToHexString(Bytes(reply)));
            }

            var c1Terminated = Bytes("\x1b]10;rgb:ffff/0000/1212");
            Array.Resize(ref c1Terminated, c1Terminated.Length + 1);
            c1Terminated[c1Terminated.Length - 1] = 0x9c;
            Reject(Build(new RecordValue(1, 1, 0, c1Terminated)));

            var pairs = new string[257];
            Array.Fill(pairs, "41=42");
            Reject(Build(new RecordValue(
                1,
                1,
                0,
                Bytes("\x1bP1+r" + string.Join(";", pairs) + "\x1b\\"))));
        }

        private static void ValidateReplyLengthBoundary()
        {
            var maximum = new byte[NativeTerminalHostEffectFrameV2Decoder.MaximumPtyReplyBytes];
            Bytes("\x1bP1+r41=").CopyTo(maximum, 0);
            Array.Fill(maximum, (byte)'0', 8, maximum.Length - 10);
            maximum[maximum.Length - 2] = 0x1b;
            maximum[maximum.Length - 1] = 0x5c;
            _ = NativeTerminalHostEffectFrameV2Decoder.Decode(
                Build(new RecordValue(1, 1, 0, maximum)));

            var oversized = new byte[maximum.Length + 2];
            maximum.AsSpan(0, maximum.Length - 2).CopyTo(oversized);
            oversized[oversized.Length - 4] = (byte)'0';
            oversized[oversized.Length - 3] = (byte)'0';
            oversized[oversized.Length - 2] = 0x1b;
            oversized[oversized.Length - 1] = 0x5c;
            Reject(Build(new RecordValue(1, 1, 0, oversized)));
        }

        private static byte[] Build(params RecordValue[] records)
        {
            var payloadBytes = 0;
            foreach (var record in records)
                payloadBytes = checked(payloadBytes + record.Payload.Length);
            var payloadOffset = checked(HeaderSize + RecordSize * records.Length);
            var frame = new byte[checked(payloadOffset + payloadBytes)];

            Encoding.ASCII.GetBytes("TLHEF2\0\0").CopyTo(frame, 0);
            PutUInt16(frame, 8, 2);
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

        private static bool BytesEqual(IReadOnlyList<byte> actual, byte[] expected)
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
            Reject(bytes, "A malformed Host Effect Frame V2 was accepted.");
        }

        private static void Reject(byte[] bytes, string message)
        {
            Throws<NativeTerminalHostEffectFrameV2DecodeException>(
                () => NativeTerminalHostEffectFrameV2Decoder.Decode(bytes),
                message);
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
