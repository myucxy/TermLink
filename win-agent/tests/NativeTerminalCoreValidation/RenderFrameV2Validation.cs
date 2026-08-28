using System;
using System.Buffers.Binary;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation
{
    /// <summary>
    /// Managed-only validation for Render Frame V2. Program can call Run once the V2 native
    /// export is integrated; these checks intentionally require no native DLL entry point.
    /// </summary>
    internal static class RenderFrameV2Validation
    {
        private const int HeaderSize = 96;
        private const int DirectoryOffset = 96;
        private const int DirectoryEntrySize = 24;
        private const int SectionCount = 6;
        private const uint Crc32CPolynomial = 0x82f6_3b78;

        internal static void Run()
        {
            ValidateCanonicalFrames();
            ValidateVersionIsolation();
            ValidateStrictHeader();
            ValidateSharedPayloadStrictness();
        }

        internal static byte[] CreateCanonicalFrame(
            NativeTerminalRenderCursorShapeV2 cursorShape =
                NativeTerminalRenderCursorShapeV2.Block,
            bool cursorBlinking = false)
        {
            var grapheme = new byte[9];
            PutUInt32(grapheme, 0, 8);
            PutUInt16(grapheme, 4, 1);
            grapheme[6] = 1;
            grapheme[8] = (byte)'A';

            var rendition = new byte[16];
            var palette = new byte[259 * 4];
            for (var index = 0; index < 259; index++)
                PutUInt32(palette, index * 4, uint.MaxValue);

            var rows = new byte[8 + 8 + 12];
            PutUInt32(rows, 0, 8);
            PutUInt32(rows, 4, 12);
            PutUInt32(rows, 8, 1);
            PutUInt32(rows, 16, 1);

            var title = Encoding.UTF8.GetBytes("v2");
            return Assemble(
                cursorShape,
                cursorBlinking,
                new[]
                {
                    new SectionBytes(1, 8, grapheme),
                    new SectionBytes(1, 16, rendition),
                    new SectionBytes(259, 4, palette),
                    new SectionBytes(0, 16, Array.Empty<byte>()),
                    new SectionBytes(1, 0, rows),
                    new SectionBytes(title.Length, 0, title)
                });
        }

        private static void ValidateCanonicalFrames()
        {
            foreach (var shape in new[]
            {
                NativeTerminalRenderCursorShapeV2.Block,
                NativeTerminalRenderCursorShapeV2.Underline,
                NativeTerminalRenderCursorShapeV2.Bar
            })
            {
                foreach (var blinking in new[] { false, true })
                {
                    var source = CreateCanonicalFrame(shape, blinking);
                    var decoded = NativeTerminalRenderFrameV2Decoder.Decode(
                        (ReadOnlySpan<byte>)source);
                    Require(decoded.PresentationRevision == 7 &&
                        decoded.NextSourceSequence == 11,
                        "V2 revision metadata changed during shared-payload decoding.");
                    Require(decoded.Columns == 1 && decoded.Rows == 1 &&
                        decoded.ActiveScreen == NativeTerminalRenderScreenV1.Alternate &&
                        decoded.ReverseVideo,
                        "V2 common screen metadata was decoded incorrectly.");
                    Require(decoded.Cursor.Row == 0 && decoded.Cursor.Column == 0 &&
                        decoded.Cursor.Visible && decoded.Cursor.WrapPending &&
                        decoded.Cursor.Shape == shape &&
                        decoded.Cursor.Blinking == blinking,
                        "V2 cursor presentation state was decoded incorrectly.");
                    Require(decoded.Graphemes.Count == 1 &&
                        decoded.Graphemes[0].Text == "A" &&
                        decoded.Renditions.Count == 1 &&
                        decoded.RenderRows.Count == 1 &&
                        decoded.RenderRows[0].Cells.Count == 1 &&
                        decoded.Title == "v2",
                        "V2 shared payload was decoded incorrectly.");

                    source[SectionOffset(source, 6)] = (byte)'X';
                    Require(decoded.Title == "v2",
                        "Mutating source bytes changed an immutable V2 DTO.");
                }
            }

            Require(NativeTerminalRenderFrameV2.FormatVersion == 2 &&
                NativeTerminalRenderFrameV2Decoder.MaximumFrameBytes == 16 * 1024 * 1024,
                "V2 public format constants changed.");
        }

        private static void ValidateVersionIsolation()
        {
            var v2 = CreateCanonicalFrame();
            Throws<NativeTerminalRenderFrameV1DecodeException>(
                () => NativeTerminalRenderFrameV1Decoder.Decode(v2),
                "The V1 decoder accepted a V2 frame.");

            var v1 = (byte[])v2.Clone();
            Encoding.ASCII.GetBytes("TLRFRM1\0").CopyTo(v1, 0);
            PutUInt16(v1, 8, 1);
            Array.Clear(v1, 68, 4);
            PutUInt64(v1, 72, 0x1f);
            RefreshCrc(v1);
            _ = NativeTerminalRenderFrameV1Decoder.Decode(v1);
            Reject(v1);
        }

        private static void ValidateStrictHeader()
        {
            Throws<ArgumentNullException>(
                () => NativeTerminalRenderFrameV2Decoder.Decode((byte[])null),
                "A null V2 frame was accepted.");
            Reject(new byte[HeaderSize - 1]);
            Reject(new byte[NativeTerminalRenderFrameV2Decoder.MaximumFrameBytes + 1]);

            Reject(MutateAndRefresh(CreateCanonicalFrame(), 6, (byte)'1'));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 8, 1));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 10, 1));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 12, HeaderSize + 8));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 14, DirectoryEntrySize + 8));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 16, SectionCount - 1));
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 68, 3));
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 69, 2));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 70, 1));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 72, 0x1f));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 72, 0x7f));

            var badHeaderCrc = (byte[])CreateCanonicalFrame().Clone();
            badHeaderCrc[24] ^= 1;
            Reject(badHeaderCrc);

            var badPayloadCrc = (byte[])CreateCanonicalFrame().Clone();
            badPayloadCrc[badPayloadCrc.Length - 1] ^= 1;
            Reject(badPayloadCrc);
        }

        private static void ValidateSharedPayloadStrictness()
        {
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 18, 4));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), 20, uint.MaxValue));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), 60, 2));
            Reject(PutUInt32AndRefresh(CreateCanonicalFrame(), 64, DirectoryOffset + 8));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 80, 1));
            Reject(PutUInt16AndRefresh(
                CreateCanonicalFrame(),
                DirectoryOffset + 4,
                2));
            Reject(PutUInt32AndRefresh(
                CreateCanonicalFrame(),
                DirectoryOffset + DirectoryEntrySize + 8,
                248));

            var invalidUtf8 = CreateCanonicalFrame();
            invalidUtf8[SectionOffset(invalidUtf8, 1) + 8] = 0xff;
            RefreshCrc(invalidUtf8);
            Reject(invalidUtf8);
        }

        private static byte[] Assemble(
            NativeTerminalRenderCursorShapeV2 cursorShape,
            bool cursorBlinking,
            SectionBytes[] sections)
        {
            Require(sections.Length == SectionCount, "The V2 fixture needs exactly six sections.");
            var offsets = new int[sections.Length];
            var total = HeaderSize + SectionCount * DirectoryEntrySize;
            for (var index = 0; index < sections.Length; index++)
            {
                total = Align8(total);
                offsets[index] = total;
                total = Align8(checked(total + sections[index].Bytes.Length));
            }

            var frame = new byte[total];
            Encoding.ASCII.GetBytes("TLRFRM2\0").CopyTo(frame, 0);
            PutUInt16(frame, 8, 2);
            PutUInt16(frame, 10, 0);
            PutUInt16(frame, 12, HeaderSize);
            PutUInt16(frame, 14, DirectoryEntrySize);
            PutUInt16(frame, 16, SectionCount);
            PutUInt16(frame, 18, 3);
            PutUInt32(frame, 20, frame.Length);
            PutUInt64(frame, 24, 7);
            PutUInt64(frame, 32, 11);
            PutUInt32(frame, 40, 1);
            PutUInt32(frame, 44, 1);
            PutUInt32(frame, 48, 0);
            PutUInt32(frame, 52, 0);
            PutUInt32(frame, 56, 3);
            PutUInt32(frame, 60, 1);
            PutUInt32(frame, 64, DirectoryOffset);
            frame[68] = checked((byte)cursorShape);
            frame[69] = cursorBlinking ? (byte)1 : (byte)0;
            PutUInt64(frame, 72, 0x3f);

            for (var index = 0; index < sections.Length; index++)
            {
                var directory = DirectoryOffset + index * DirectoryEntrySize;
                PutUInt32(frame, directory, index + 1);
                PutUInt16(frame, directory + 4, 1);
                PutUInt16(frame, directory + 6, 1);
                PutUInt32(frame, directory + 8, offsets[index]);
                PutUInt32(frame, directory + 12, sections[index].Bytes.Length);
                PutUInt32(frame, directory + 16, sections[index].Count);
                PutUInt32(frame, directory + 20, sections[index].RecordSize);
                sections[index].Bytes.CopyTo(frame, offsets[index]);
            }

            RefreshCrc(frame);
            return frame;
        }

        private static int SectionOffset(byte[] frame, int tag)
        {
            return checked((int)ReadUInt32(
                frame,
                DirectoryOffset + (tag - 1) * DirectoryEntrySize + 8));
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
            PutUInt32(bytes, 88, Crc32C(bytes.AsSpan(HeaderSize)));
            PutUInt32(bytes, 92, Crc32C(bytes.AsSpan(0, 92)));
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

        private static int Align8(int value)
        {
            return checked(value + 7) & ~7;
        }

        private static void Reject(byte[] bytes)
        {
            Throws<NativeTerminalRenderFrameV2DecodeException>(
                () => NativeTerminalRenderFrameV2Decoder.Decode(bytes),
                "A malformed Render Frame V2 payload was accepted.");
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

        private readonly struct SectionBytes
        {
            internal SectionBytes(int count, int recordSize, byte[] bytes)
            {
                Count = count;
                RecordSize = recordSize;
                Bytes = bytes;
            }

            internal int Count { get; }
            internal int RecordSize { get; }
            internal byte[] Bytes { get; }
        }
    }
}
