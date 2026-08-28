using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation
{
    /// <summary>
    /// Pure-managed validation for the strict Render Frame V1 decoder.
    /// Program may call Run without loading or invoking the native terminal DLL.
    /// </summary>
    internal static class RenderFrameV1Validation
    {
        private const int HeaderSize = 96;
        private const int DirectoryOffset = 96;
        private const int DirectoryEntrySize = 24;
        private const int SectionCount = 6;
        private const uint Crc32CPolynomial = 0x82f6_3b78;

        internal static void Run()
        {
            ValidateCanonicalMixedContent();
            ValidateCrcDomainsAndBounds();
            ValidateReservedFieldsAndUnknownBits();
            ValidateDirectoryLayoutAndPadding();
            ValidateUtf8ColorsAndTables();
            ValidateReferencesAndWideCells();
            ValidateCheckedWireIntegers();
        }

        private static void ValidateCanonicalMixedContent()
        {
            var decoded = NativeTerminalRenderFrameV1Decoder.Decode(new FrameBuilder().Build());
            Require(decoded.PresentationRevision == 7, "Presentation revision diverged.");
            Require(decoded.NextSourceSequence == 11, "Next source sequence diverged.");
            Require(decoded.Columns == 3 && decoded.Rows == 2, "Frame size diverged.");
            Require(decoded.ActiveScreen == NativeTerminalRenderScreenV1.Alternate,
                "Active screen diverged.");
            Require(decoded.ReverseVideo, "Global reverse-video flag was lost.");
            Require(decoded.Cursor.Row == 1 && decoded.Cursor.Column == 2 &&
                decoded.Cursor.Visible && decoded.Cursor.WrapPending,
                "Cursor projection diverged.");
            Require(decoded.Graphemes.Select(value => value.Text).SequenceEqual(new[] { "A", "界" }),
                "Grapheme text diverged.");
            Require(decoded.Graphemes.Select(value => value.DisplayWidth).SequenceEqual(new[] { 1, 2 }),
                "Grapheme widths diverged.");
            Require(decoded.Renditions[0].Foreground.Kind == NativeTerminalRenderColorKindV1.Default,
                "Default foreground diverged.");
            Require(decoded.Renditions[1].Foreground.Kind == NativeTerminalRenderColorKindV1.Indexed &&
                decoded.Renditions[1].Foreground.Value == 3,
                "Indexed foreground diverged.");
            Require(decoded.Renditions[1].Background.Kind == NativeTerminalRenderColorKindV1.Rgb &&
                decoded.Renditions[1].Background.Value == 0x112233,
                "RGB background diverged.");
            Require(decoded.Renditions[1].Bold, "Bold rendition flag was lost.");
            Require(decoded.Renditions[1].Conceal, "Conceal rendition flag was lost.");
            Require(decoded.Renditions[1].Strikethrough,
                "Strikethrough rendition flag was lost.");
            Require(decoded.Palette.ColorsRgb[0] == null &&
                decoded.Palette.ColorsRgb[1] == 0x010203 &&
                decoded.Palette.DefaultForegroundRgb == 0xffffff &&
                decoded.Palette.DefaultBackgroundRgb == 0,
                "Palette projection diverged.");
            Require(decoded.Hyperlinks.Count == 1 &&
                decoded.Hyperlinks[0].Reference == 5 &&
                decoded.Hyperlinks[0].Uri == "https://example.test/界",
                "Hyperlink projection diverged.");
            Require(decoded.RenderRows[0].MeaningfulEnd == 3 &&
                decoded.RenderRows[0].SoftWrapped,
                "Row projection diverged.");
            Require(decoded.RenderRows[0].Cells[1].IsProtected &&
                decoded.RenderRows[0].Cells[1].HyperlinkReference == 5 &&
                decoded.RenderRows[0].Cells[2].WideContinuation,
                "Cell projection diverged.");
            Require(decoded.Title == "mixed 界", "Title projection diverged.");

            Throws<NotSupportedException>(
                () => ((IList<NativeTerminalRenderRowV1>)decoded.RenderRows).Clear(),
                "Frame rows were mutable.");
            Throws<NotSupportedException>(
                () => ((IList<int?>)decoded.Palette.ColorsRgb)[0] = 1,
                "Palette values were mutable.");
        }

        private static void ValidateCrcDomainsAndBounds()
        {
            Reject(MutateWithoutCrc(new FrameBuilder().Build(), 0, (byte)'X'));
            Reject(MutateWithoutCrc(new FrameBuilder().Build(), 24, 8));

            var payload = new FrameBuilder().Build();
            Reject(MutateWithoutCrc(payload, SectionOffset(payload, 6), (byte)'X'));

            Throws<ArgumentNullException>(
                () => NativeTerminalRenderFrameV1Decoder.Decode((byte[])null),
                "A null frame was accepted.");
            Reject(Array.Empty<byte>());
            Reject(new byte[NativeTerminalRenderFrameV1Decoder.MaximumFrameBytes + 1]);
        }

        private static void ValidateReservedFieldsAndUnknownBits()
        {
            Reject(MutateAndRefresh(new FrameBuilder().Build(), 68, 1));
            Reject(PutUInt32AndRefresh(new FrameBuilder().Build(), 56, 4));

            var frameFlags = new FrameBuilder().Build();
            PutUInt16(frameFlags, 18, 1 << 2);
            RefreshCrc(frameFlags);
            Reject(frameFlags);

            var maliciousFrameFlags = new FrameBuilder().Build();
            PutUInt16(maliciousFrameFlags, 18, 0x8003);
            RefreshCrc(maliciousFrameFlags);
            Reject(maliciousFrameFlags);

            var grapheme = new FrameBuilder().Build();
            Reject(MutateAndRefresh(grapheme, SectionOffset(grapheme, 1) + 7, 1));

            var rendition = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(rendition, SectionOffset(rendition, 2) + 12, 1));

            var renditionFlags = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                renditionFlags,
                SectionOffset(renditionFlags, 2) + 16,
                1U << 7));

            var maliciousRenditionFlags = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                maliciousRenditionFlags,
                SectionOffset(maliciousRenditionFlags, 2) + 16,
                0x8000_0061));

            var hyperlink = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(hyperlink, SectionOffset(hyperlink, 4) + 12, 1));

            var rows = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(rows, FirstCellOffset(rows) + 8, 1U << 13));

            var rowFlags = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(rowFlags, SectionOffset(rowFlags, 5) + 12, 2));
        }

        private static void ValidateDirectoryLayoutAndPadding()
        {
            var tag = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(tag, DirectoryOffset, 2));

            var version = new FrameBuilder().Build();
            var versionCopy = (byte[])version.Clone();
            PutUInt16(versionCopy, DirectoryOffset + 4, 2);
            RefreshCrc(versionCopy);
            Reject(versionCopy);

            var flags = new FrameBuilder().Build();
            var flagsCopy = (byte[])flags.Clone();
            PutUInt16(flagsCopy, DirectoryOffset + 6, 0);
            RefreshCrc(flagsCopy);
            Reject(flagsCopy);

            var overlap = new FrameBuilder().Build();
            var firstOffset = ReadUInt32(overlap, DirectoryOffset + 8);
            Reject(PutUInt32AndRefresh(
                overlap,
                DirectoryOffset + DirectoryEntrySize + 8,
                firstOffset));

            var gap = new FrameBuilder().Build();
            var secondOffsetField = DirectoryOffset + DirectoryEntrySize + 8;
            Reject(PutUInt32AndRefresh(
                gap,
                secondOffsetField,
                checked(ReadUInt32(gap, secondOffsetField) + 8)));

            var padded = new FrameBuilder().Build();
            var padding = checked(SectionOffset(padded, 1) + SectionLength(padded, 1));
            Require(padding < Align8(padding), "The fixed frame had no grapheme padding.");
            Reject(MutateAndRefresh(padded, padding, 1));

            var wrongPaletteCount = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                wrongPaletteCount,
                DirectoryOffset + 2 * DirectoryEntrySize + 16,
                258));
        }

        private static void ValidateUtf8ColorsAndTables()
        {
            var invalidGrapheme = new FrameBuilder().Build();
            Reject(MutateAndRefresh(
                invalidGrapheme,
                SectionOffset(invalidGrapheme, 1) + 2 * 8,
                0xff));

            var invalidLink = new FrameBuilder().Build();
            Reject(MutateAndRefresh(
                invalidLink,
                SectionOffset(invalidLink, 4) + 16,
                0xff));

            var invalidTitle = new FrameBuilder().Build();
            Reject(MutateAndRefresh(
                invalidTitle,
                SectionOffset(invalidTitle, 6),
                0xff));

            var badColorTag = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                badColorTag,
                SectionOffset(badColorTag, 2) + 16 + 4,
                0x0300_0000));

            var badDefaultPayload = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                badDefaultPayload,
                SectionOffset(badDefaultPayload, 2) + 4,
                1));

            var badPalette = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                badPalette,
                SectionOffset(badPalette, 3),
                0x0100_0000));

            var renditionOrder = new FrameBuilder().Build();
            var renditionSection = SectionOffset(renditionOrder, 2);
            for (var index = 0; index < 16; index++)
            {
                var temporary = renditionOrder[renditionSection + index];
                renditionOrder[renditionSection + index] = renditionOrder[renditionSection + 16 + index];
                renditionOrder[renditionSection + 16 + index] = temporary;
            }
            RefreshCrc(renditionOrder);
            Reject(renditionOrder);

            var duplicateLinkReference = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                duplicateLinkReference,
                SectionOffset(duplicateLinkReference, 4),
                0));
        }

        private static void ValidateReferencesAndWideCells()
        {
            var danglingGrapheme = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(danglingGrapheme, FirstCellOffset(danglingGrapheme), 3));

            var danglingRendition = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                danglingRendition,
                FirstCellOffset(danglingRendition) + 4,
                2));

            var danglingLink = new FrameBuilder().Build();
            Reject(PutUInt32AndRefresh(
                danglingLink,
                FirstCellOffset(danglingLink) + 8,
                6U << 2));

            var unusedGrapheme = new FrameBuilder().Build();
            var unusedGraphemeCells = FirstCellOffset(unusedGrapheme);
            foreach (var index in new[] { 1, 2, 3, 4 })
            {
                var cell = unusedGraphemeCells + index * 12;
                PutUInt32(unusedGrapheme, cell, 1);
                PutUInt32(unusedGrapheme, cell + 8, ReadUInt32(unusedGrapheme, cell + 8) & ~1U);
            }
            RefreshCrc(unusedGrapheme);
            Reject(unusedGrapheme);

            var unusedRendition = new FrameBuilder().Build();
            var unusedRenditionCells = FirstCellOffset(unusedRendition);
            for (var index = 0; index < 6; index++)
                PutUInt32(unusedRendition, unusedRenditionCells + index * 12 + 4, 1);
            RefreshCrc(unusedRendition);
            Reject(unusedRendition);

            var unusedLink = new FrameBuilder().Build();
            var firstCell = FirstCellOffset(unusedLink);
            for (var index = 0; index < 6; index++)
            {
                var metadataOffset = firstCell + index * 12 + 8;
                var metadata = ReadUInt32(unusedLink, metadataOffset);
                PutUInt32(unusedLink, metadataOffset, metadata & 3U);
            }
            RefreshCrc(unusedLink);
            Reject(unusedLink);

            var missingContinuation = new FrameBuilder().Build();
            var continuation = FirstCellOffset(missingContinuation) + 2 * 12;
            Reject(PutUInt32AndRefresh(missingContinuation, continuation, 1));

            var mismatchedStyle = new FrameBuilder().Build();
            continuation = FirstCellOffset(mismatchedStyle) + 2 * 12;
            Reject(PutUInt32AndRefresh(mismatchedStyle, continuation + 4, 0));

            var standalone = new FrameBuilder().Build();
            firstCell = FirstCellOffset(standalone);
            PutUInt32(standalone, firstCell, 0);
            PutUInt32(standalone, firstCell + 8, 1);
            RefreshCrc(standalone);
            Reject(standalone);
        }

        private static void ValidateCheckedWireIntegers()
        {
            Reject(PutUInt32AndRefresh(new FrameBuilder().Build(), 20, uint.MaxValue));
            Reject(PutUInt32AndRefresh(
                new FrameBuilder().Build(),
                DirectoryOffset + 12,
                uint.MaxValue));
            Reject(PutUInt32AndRefresh(new FrameBuilder().Build(), 40, uint.MaxValue));
        }

        private static void Reject(byte[] bytes)
        {
            Throws<NativeTerminalRenderFrameV1DecodeException>(
                () => NativeTerminalRenderFrameV1Decoder.Decode(bytes),
                "A malformed Render Frame V1 payload was accepted.");
        }

        private static int FirstCellOffset(byte[] frame)
        {
            return checked(SectionOffset(frame, 5) + 8 + 2 * 8);
        }

        private static int SectionOffset(byte[] frame, int tag)
        {
            return checked((int)ReadUInt32(
                frame,
                DirectoryOffset + (tag - 1) * DirectoryEntrySize + 8));
        }

        private static int SectionLength(byte[] frame, int tag)
        {
            return checked((int)ReadUInt32(
                frame,
                DirectoryOffset + (tag - 1) * DirectoryEntrySize + 12));
        }

        private static byte[] MutateWithoutCrc(byte[] frame, int offset, byte value)
        {
            var result = (byte[])frame.Clone();
            result[offset] = value;
            return result;
        }

        private static byte[] MutateAndRefresh(byte[] frame, int offset, byte value)
        {
            var result = (byte[])frame.Clone();
            result[offset] = value;
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

        private static ushort ReadUInt16(byte[] bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        }

        private static void PutUInt16(byte[] bytes, int offset, int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)value));
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

        private sealed class FrameBuilder
        {
            internal byte[] Build()
            {
                var graphemes = BuildGraphemes();
                var renditions = BuildRenditions();
                var palette = BuildPalette();
                var hyperlinks = BuildHyperlinks();
                var rows = BuildRows();
                var title = Encoding.UTF8.GetBytes("mixed 界");
                return Assemble(new[]
                {
                    new SectionBytes(2, 8, graphemes),
                    new SectionBytes(2, 16, renditions),
                    new SectionBytes(259, 4, palette),
                    new SectionBytes(1, 16, hyperlinks),
                    new SectionBytes(6, 0, rows),
                    new SectionBytes(title.Length, 0, title)
                });
            }

            private static byte[] BuildGraphemes()
            {
                var first = Encoding.UTF8.GetBytes("A");
                var second = Encoding.UTF8.GetBytes("界");
                var result = new byte[2 * 8 + first.Length + second.Length];
                var stringOffset = 16;
                PutUInt32(result, 0, stringOffset);
                PutUInt16(result, 4, first.Length);
                result[6] = 1;
                stringOffset += first.Length;
                PutUInt32(result, 8, stringOffset);
                PutUInt16(result, 12, second.Length);
                result[14] = 2;
                first.CopyTo(result, 16);
                second.CopyTo(result, 16 + first.Length);
                return result;
            }

            private static byte[] BuildRenditions()
            {
                var result = new byte[32];
                PutUInt32(result, 16, 0x61);
                PutUInt32(result, 20, 0x0100_0003);
                PutUInt32(result, 24, 0x0211_2233);
                return result;
            }

            private static byte[] BuildPalette()
            {
                var result = new byte[259 * 4];
                for (var index = 0; index < 259; index++)
                    PutUInt32(result, index * 4, uint.MaxValue);
                PutUInt32(result, 4, 0x010203);
                PutUInt32(result, 256 * 4, 0x00ffffff);
                PutUInt32(result, 257 * 4, 0);
                return result;
            }

            private static byte[] BuildHyperlinks()
            {
                var uri = Encoding.UTF8.GetBytes("https://example.test/界");
                var result = new byte[16 + uri.Length];
                PutUInt32(result, 0, 5);
                PutUInt32(result, 4, 16);
                PutUInt32(result, 8, uri.Length);
                uri.CopyTo(result, 16);
                return result;
            }

            private static byte[] BuildRows()
            {
                var result = new byte[8 + 2 * 8 + 6 * 12];
                PutUInt32(result, 0, 8);
                PutUInt32(result, 4, 12);
                PutUInt32(result, 8, 3);
                PutUInt32(result, 12, 1);
                PutUInt32(result, 16, 1);
                PutUInt32(result, 20, 0);
                var offset = 24;
                PutCell(result, ref offset, 1, 0, 0);
                PutCell(result, ref offset, 2, 1, (5 << 2) | 2);
                PutCell(result, ref offset, 0, 1, (5 << 2) | 2 | 1);
                PutCell(result, ref offset, 2, 1, (5 << 2) | 2);
                PutCell(result, ref offset, 0, 1, (5 << 2) | 2 | 1);
                PutCell(result, ref offset, 1, 0, 0);
                return result;
            }

            private static void PutCell(
                byte[] bytes,
                ref int offset,
                int grapheme,
                int rendition,
                int metadata)
            {
                PutUInt32(bytes, offset, grapheme);
                PutUInt32(bytes, offset + 4, rendition);
                PutUInt32(bytes, offset + 8, metadata);
                offset = checked(offset + 12);
            }

            private static byte[] Assemble(SectionBytes[] sections)
            {
                var offsets = new int[sections.Length];
                var total = HeaderSize + SectionCount * DirectoryEntrySize;
                for (var index = 0; index < sections.Length; index++)
                {
                    total = Align8(total);
                    offsets[index] = total;
                    total = Align8(checked(total + sections[index].Bytes.Length));
                }

                var frame = new byte[total];
                Encoding.ASCII.GetBytes("TLRFRM1\0").CopyTo(frame, 0);
                PutUInt16(frame, 8, 1);
                PutUInt16(frame, 10, 0);
                PutUInt16(frame, 12, HeaderSize);
                PutUInt16(frame, 14, DirectoryEntrySize);
                PutUInt16(frame, 16, SectionCount);
                PutUInt16(frame, 18, 3);
                PutUInt32(frame, 20, frame.Length);
                PutUInt64(frame, 24, 7);
                PutUInt64(frame, 32, 11);
                PutUInt32(frame, 40, 3);
                PutUInt32(frame, 44, 2);
                PutUInt32(frame, 48, 1);
                PutUInt32(frame, 52, 2);
                PutUInt32(frame, 56, 3);
                PutUInt32(frame, 60, 1);
                PutUInt32(frame, 64, DirectoryOffset);
                PutUInt64(frame, 72, 0x1f);

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
