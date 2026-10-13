using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TermLink.TerminalCore
{
    public enum NativeTerminalGraphicsProtocol : uint { Sixel = 1, Kitty = 2 }
    public readonly record struct NativeTerminalGraphicsRatio(ulong Numerator, ulong Denominator);
    public readonly record struct NativeTerminalGraphicsSourceRectangle(uint X, uint Y, uint Width, uint Height);
    public readonly record struct NativeTerminalGraphicsClip(uint Left, uint Top, uint Right, uint Bottom);
    public readonly record struct NativeTerminalImageLayoutV5(
        NativeTerminalGraphicsSourceRectangle Source,
        NativeTerminalGraphicsRatio X, NativeTerminalGraphicsRatio Y,
        NativeTerminalGraphicsRatio Width, NativeTerminalGraphicsRatio Height,
        uint AllocatedColumns, uint AllocatedRows);
    public readonly record struct NativeTerminalImageDrawV5(
        ulong PlacementId, ulong ResourceId, int Column, int Row, int Z, uint ImageId,
        NativeTerminalImageLayoutV5 Layout, NativeTerminalGraphicsProtocol Protocol, NativeTerminalGraphicsClip Clip);
    public readonly record struct NativeTerminalGlyphDrawV5(
        uint Column, uint Row, ulong ResourceId, uint SourceX, uint SourceWidth, uint SourceHeight);

    public sealed class NativeTerminalRenderFrameV5DecodeException : ArgumentException
    {
        internal NativeTerminalRenderFrameV5DecodeException(string detail, Exception inner = null)
            : base("render frame v5: " + detail, inner) { }
    }

    public sealed class NativeTerminalRenderFrameV5
    {
        internal NativeTerminalRenderFrameV5(ulong revision, NativeTerminalRenderFrameV4 text,
            NativeTerminalGraphicsResources resources, NativeTerminalImageDrawV5[] images, NativeTerminalGlyphDrawV5[] glyphs)
        {
            Revision = revision;
            Text = text;
            Resources = resources;
            Images = Array.AsReadOnly(images);
            Glyphs = Array.AsReadOnly(glyphs);
        }

        public const int FormatVersion = 5;
        public ulong Revision { get; }
        public int Columns => Text.Columns;
        public int Rows => Text.Rows;
        public NativeTerminalRenderFrameV4 Text { get; }
        public NativeTerminalGraphicsResources Resources { get; }
        public IReadOnlyList<NativeTerminalImageDrawV5> Images { get; }
        public IReadOnlyList<NativeTerminalGlyphDrawV5> Glyphs { get; }
    }

    public static class NativeTerminalRenderFrameV5Decoder
    {
        public const int MaximumFrameBytes = 64 * 1024 * 1024;
        public const int MaximumImages = 4096;
        public const int MaximumPlacementCells = 4096;
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static NativeTerminalRenderFrameV5 Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Decode(bytes.AsSpan());
        }

        public static NativeTerminalRenderFrameV5 Decode(ReadOnlySpan<byte> bytes)
        {
            try { return DecodeFrame(bytes); }
            catch (NativeTerminalGraphicsResourcesDecodeException error) { throw Error("invalid resources", error); }
            catch (NativeTerminalRenderFrameV4DecodeException error) { throw Error("invalid V4 text", error); }
        }

        private static NativeTerminalRenderFrameV5 DecodeFrame(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 64 || bytes.Length > MaximumFrameBytes ||
                !bytes.Slice(0, 12).SequenceEqual("TLRFRM5\0\x05\0\x40\0"u8))
                throw Error("invalid header or frame size");
            var revision = U64(bytes, 16);
            var columns = U32(bytes, 24);
            var rows = U32(bytes, 28);
            var textLength = U32(bytes, 32);
            var resourceLength = U32(bytes, 36);
            var imageCount = U32(bytes, 40);
            var glyphCount = U32(bytes, 44);
            if (columns == 0 || columns > 512 || rows == 0 || rows > 512 ||
                textLength < 240 || textLength > NativeTerminalRenderFrameV4Decoder.MaximumFrameBytes ||
                resourceLength < 48 || resourceLength > NativeTerminalGraphicsResourcesDecoder.MaximumFrameBytes ||
                imageCount > MaximumImages || glyphCount > (ulong)columns * rows ||
                64UL + textLength + resourceLength + imageCount * 144UL + glyphCount * 32UL != (ulong)bytes.Length ||
                U32(bytes, 12) != bytes.Length || U32(bytes, 48) != 7 || U64(bytes, 56) != 0 ||
                U32(bytes, 52) != Crc32C(bytes.Slice(0, 52), bytes.Slice(56)))
                throw Error("invalid header fields, lengths or CRC32C");

            var textBytes = bytes.Slice(64, (int)textLength);
            if (U64(textBytes, 24) != revision || U64(textBytes, 32) != revision ||
                U32(textBytes, 40) != columns || U32(textBytes, 44) != rows)
                throw Error("V4 text revision or dimensions differ");
            var directory = NativeTerminalGraphicsResourcesDecoder.Inspect(bytes.Slice(64 + (int)textLength, (int)resourceLength));
            var images = new NativeTerminalImageDrawV5[(int)imageCount];
            var glyphs = new NativeTerminalGlyphDrawV5[(int)glyphCount];
            var placements = new HashSet<ulong>();
            var referenced = new HashSet<ulong>();
            var offset = 64 + (int)textLength + (int)resourceLength;
            for (var index = 0; index < images.Length; index++, offset += 144)
            {
                var draw = bytes.Slice(offset, 144);
                var source = new NativeTerminalGraphicsSourceRectangle(U32(draw, 32), U32(draw, 36), U32(draw, 40), U32(draw, 44));
                var layout = new NativeTerminalImageLayoutV5(source, Ratio(draw, 48), Ratio(draw, 64), Ratio(draw, 80), Ratio(draw, 96), U32(draw, 112), U32(draw, 116));
                var image = new NativeTerminalImageDrawV5(U64(draw, 0), U64(draw, 8), I32(draw, 16), I32(draw, 20), I32(draw, 24),
                    U32(draw, 28), layout, (NativeTerminalGraphicsProtocol)U32(draw, 120),
                    new NativeTerminalGraphicsClip(U32(draw, 128), U32(draw, 132), U32(draw, 136), U32(draw, 140)));
                if (U32(draw, 124) != 0 || image.PlacementId == 0 || !placements.Add(image.PlacementId) ||
                    (index > 0 && Compare(images[index - 1], image) >= 0) ||
                    (image.Protocol != NativeTerminalGraphicsProtocol.Sixel && image.Protocol != NativeTerminalGraphicsProtocol.Kitty) ||
                    (image.Protocol == NativeTerminalGraphicsProtocol.Sixel && image.ImageId != 0))
                    throw Error("invalid image identity, ordering or protocol");
                ValidateImage(image, columns, rows);
                if (!directory.TryGet(image.ResourceId, out var resource) || resource.Format != NativeTerminalGraphicsImageFormat.Rgba8888 ||
                    !Contains(resource, source.X, source.Y, source.Width, source.Height))
                    throw Error("invalid image resource or source rectangle");
                referenced.Add(image.ResourceId);
                images[index] = image;
            }
            ulong previousCell = 0;
            for (var index = 0; index < glyphs.Length; index++, offset += 32)
            {
                var draw = bytes.Slice(offset, 32);
                var glyph = new NativeTerminalGlyphDrawV5(U32(draw, 0), U32(draw, 4), U64(draw, 8), U32(draw, 16), U32(draw, 20), U32(draw, 24));
                var cell = ((ulong)glyph.Row << 32) | glyph.Column;
                if (U32(draw, 28) != 0 || glyph.Column >= columns || glyph.Row >= rows || (index > 0 && cell <= previousCell) ||
                    !directory.TryGet(glyph.ResourceId, out var resource) || resource.Format != NativeTerminalGraphicsImageFormat.Alpha8 ||
                    !Contains(resource, glyph.SourceX, 0, glyph.SourceWidth, glyph.SourceHeight))
                    throw Error("invalid glyph cell, ordering or source resource");
                referenced.Add(glyph.ResourceId);
                previousCell = cell;
                glyphs[index] = glyph;
            }
            foreach (var resource in directory.Entries)
                if (!referenced.Contains(resource.Id)) throw Error("unreferenced graphics resource");

            var text = NativeTerminalRenderFrameV4Decoder.Decode(textBytes);
            foreach (var glyph in glyphs)
            {
                var row = text.RenderRows[(int)glyph.Row];
                var effectiveColumns = row.LineRendition == NativeTerminalLineRendition.SingleWidth ? text.Columns : Math.Max(1, text.Columns / 2);
                var cell = row.Cells[(int)glyph.Column];
                if (glyph.Column >= effectiveColumns || cell.WideContinuation || text.Graphemes[cell.GraphemeId - 1].DisplayWidth != 1)
                    throw Error("glyph must address an available narrow text cell");
            }
            return new NativeTerminalRenderFrameV5(revision, text, directory.Materialize(), images, glyphs);
        }

        private static void ValidateImage(NativeTerminalImageDrawV5 image, uint columns, uint rows)
        {
            var layout = image.Layout;
            var clip = image.Clip;
            if (image.Column < -MaximumPlacementCells || image.Column > MaximumPlacementCells || image.Column >= columns ||
                image.Row < -MaximumPlacementCells || image.Row > MaximumPlacementCells || image.Row >= rows ||
                layout.AllocatedColumns == 0 || layout.AllocatedColumns > MaximumPlacementCells ||
                layout.AllocatedRows == 0 || layout.AllocatedRows > MaximumPlacementCells ||
                clip.Left >= clip.Right || clip.Right > layout.AllocatedColumns || clip.Top >= clip.Bottom || clip.Bottom > layout.AllocatedRows ||
                (long)image.Column + clip.Right <= 0 || (long)image.Column + clip.Left >= columns ||
                (long)image.Row + clip.Bottom <= 0 || (long)image.Row + clip.Top >= rows)
                throw Error("image origin, allocation or clip is outside viewport");
            ValidateRatio(layout.X, false);
            ValidateRatio(layout.Y, false);
            ValidateRatio(layout.Width, true);
            ValidateRatio(layout.Height, true);
            if (!ExtentFits(layout.X, layout.Width, layout.AllocatedColumns) || !ExtentFits(layout.Y, layout.Height, layout.AllocatedRows))
                throw Error("image rectangle exceeds its allocation");
        }

        private static void ValidateRatio(NativeTerminalGraphicsRatio ratio, bool positive)
        {
            if (ratio.Numerator > 1UL << 48 || ratio.Denominator == 0 || ratio.Denominator > 1UL << 32 ||
                (positive && ratio.Numerator == 0)) throw Error("invalid image ratio bounds");
            var a = ratio.Numerator;
            var b = ratio.Denominator;
            while (b != 0) { var remainder = a % b; a = b; b = remainder; }
            if (a != 1) throw Error("image ratio is not canonical");
        }

        private static bool ExtentFits(NativeTerminalGraphicsRatio start, NativeTerminalGraphicsRatio length, uint limit) =>
            (UInt128)start.Numerator * length.Denominator + (UInt128)length.Numerator * start.Denominator <=
            (UInt128)limit * start.Denominator * length.Denominator;

        private static bool Contains(NativeTerminalGraphicsResourceMetadata resource, uint x, uint y, uint width, uint height) =>
            width != 0 && height != 0 && x < resource.Width && y < resource.Height && width <= resource.Width - x && height <= resource.Height - y;

        private static int Compare(NativeTerminalImageDrawV5 first, NativeTerminalImageDrawV5 second)
        {
            var order = first.Z.CompareTo(second.Z);
            if (order == 0) order = first.ImageId.CompareTo(second.ImageId);
            return order == 0 ? first.PlacementId.CompareTo(second.PlacementId) : order;
        }

        private static NativeTerminalGraphicsRatio Ratio(ReadOnlySpan<byte> bytes, int offset) => new NativeTerminalGraphicsRatio(U64(bytes, offset), U64(bytes, offset + 8));
        private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        private static int I32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
        private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
        private static NativeTerminalRenderFrameV5DecodeException Error(string detail, Exception inner = null) => new NativeTerminalRenderFrameV5DecodeException(detail, inner);

        private static uint Crc32C(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = uint.MaxValue;
            foreach (var value in first) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
            foreach (var value in second) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
            return ~crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var crc = index;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82f63b78U);
                table[index] = crc;
            }
            return table;
        }
    }
}
