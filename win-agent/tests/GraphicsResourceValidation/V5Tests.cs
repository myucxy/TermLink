using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using TermLink.TerminalCore;
using static TermLink.GraphicsResource.Validation.Program;

namespace TermLink.GraphicsResource.Validation;

internal static class V5Tests
{
    private static int assertions;

    internal static void Run()
    {
        ValidFrames();
        InvalidHeadersAndText();
        InvalidImages();
        RatiosAndClips();
        InvalidGlyphsAndClosure();
        RejectsBeforePixelCopies();
        Console.WriteLine("PASS Render V5 graphics: " + assertions + " assertions");
    }

    private static void ValidFrames()
    {
        var bytes = Sample();
        var frame = NativeTerminalRenderFrameV5Decoder.Decode(bytes);
        Require(frame.Revision == 7 && frame.Columns == 4 && frame.Rows == 2 && frame.Text.NextSourceSequence == 7,
            "outer and text projection");
        Require(frame.Images.Count == 1 && frame.Glyphs.Count == 1 && frame.Resources.Entries.Count == 2, "images and glyphs");
        Require(frame.Images[0].Layout.Source.Width == 1 && frame.Images[0].Layout.Width.Numerator == 1 &&
            frame.Images[0].Clip.Right == 2 && frame.Images[0].Protocol == NativeTerminalGraphicsProtocol.Kitty,
            "image layout and clip preserved");
        Require(frame.Glyphs[0].SourceX == 1 && frame.Glyphs[0].SourceWidth == 2 && frame.Glyphs[0].ResourceId == 2,
            "glyph crop preserved");
        Array.Clear(bytes);
        Require(frame.Resources.Entries[0].Pixels.SequenceEqual(new byte[] { 1, 2, 3, 4 }) && frame.Text.Graphemes[0].Text == " ",
            "frame detached from wire bytes");
        try { ((IList<NativeTerminalImageDrawV5>)frame.Images)[0] = default; throw new Exception("mutable image list"); }
        catch (NotSupportedException) { assertions++; }
        try { ((IList<NativeTerminalGlyphDrawV5>)frame.Glyphs)[0] = default; throw new Exception("mutable glyph list"); }
        catch (NotSupportedException) { assertions++; }

        Require(NativeTerminalRenderFrameV5Decoder.Decode(Frame(Text(), Encode(1), Array.Empty<byte[]>(), Array.Empty<byte[]>())).Resources.PixelBytes == 0,
            "text-only V5");
        var image = Image();
        Write32(image, 16, unchecked((uint)-1));
        Write32(image, 20, unchecked((uint)-1));
        Write32(image, 24, unchecked((uint)int.MinValue));
        Write32(image, 28, 0);
        Write32(image, 120, 1);
        frame = NativeTerminalRenderFrameV5Decoder.Decode(Frame(Text(), RgbaResources(), new[] { image }, Array.Empty<byte[]>()));
        Require(frame.Images[0].Column == -1 && frame.Images[0].Row == -1 && frame.Images[0].Z == int.MinValue &&
            frame.Images[0].Protocol == NativeTerminalGraphicsProtocol.Sixel, "negative origin and SIXEL accepted");
        Require(NativeTerminalRenderFrameV5Decoder.Decode(Frame(Text(), AlphaResources(), Array.Empty<byte[]>(), new[] { Glyph() })).Glyphs.Count == 1,
            "A8-only glyph frame");
        frame = NativeTerminalRenderFrameV5Decoder.Decode(Frame(Text(scaled: true), AlphaResources(), Array.Empty<byte[]>(), new[] { Glyph(1) }));
        Require(frame.Text.RenderRows[0].LineRendition == NativeTerminalLineRendition.DoubleWidth, "glyph in available scaled-row cell");
        var maximumImages = new byte[NativeTerminalRenderFrameV5Decoder.MaximumImages][];
        for (var index = 0; index < maximumImages.Length; index++) maximumImages[index] = Image((ulong)index + 1);
        Require(NativeTerminalRenderFrameV5Decoder.Decode(Frame(Text(), RgbaResources(), maximumImages, Array.Empty<byte[]>())).Images.Count == 4096,
            "4096 image draws accepted");
        var everyCell = new byte[8][];
        for (var index = 0; index < everyCell.Length; index++) everyCell[index] = Glyph((uint)index % 4, (uint)index / 4);
        Require(NativeTerminalRenderFrameV5Decoder.Decode(Frame(Text(), AlphaResources(), Array.Empty<byte[]>(), everyCell)).Glyphs.Count == 8,
            "glyph count equal to cell count accepted");
    }

    private static void InvalidHeadersAndText()
    {
        var valid = Sample();
        try { NativeTerminalRenderFrameV5Decoder.Decode((byte[])null); throw new Exception("null accepted"); }
        catch (ArgumentNullException) { assertions++; }
        for (var size = 0; size < 64; size++) Reject(valid.AsSpan(0, size).ToArray(), "short header");
        foreach (var size in new[] { 64, 65, 64 + (int)U32(valid, 32) - 1, ImageOffset(valid) - 1, ImageOffset(valid) + 143, valid.Length - 1 })
        {
            var bytes = valid.AsSpan(0, size).ToArray();
            Write32(bytes, 12, (uint)size);
            Seal(bytes);
            Reject(bytes, "truncated sections");
        }
        foreach (var offset in new[] { 0, 7, 8, 10, 12, 56, 60, 63 })
            Mutate(valid, bytes => bytes[offset] ^= 1, "header field " + offset);
        foreach (var offset in new[] { 24, 28 })
            foreach (var value in new[] { 0U, 513U, uint.MaxValue }) Mutate(valid, bytes => Write32(bytes, offset, value), "dimensions");
        foreach (var offset in new[] { 32, 36, 40, 44 })
            Mutate(valid, bytes => Write32(bytes, offset, uint.MaxValue), "section length/count overflow");
        Mutate(valid, bytes => Write32(bytes, 32, 239), "text minimum length");
        Mutate(valid, bytes => Write32(bytes, 36, 47), "resource minimum length");
        Mutate(valid, bytes => Write32(bytes, 40, 4097), "image count limit");
        Mutate(valid, bytes => Write32(bytes, 44, 9), "glyph count exceeds cells");
        foreach (var features in new[] { 0U, 1U, 3U, 6U, 15U, uint.MaxValue })
            Mutate(valid, bytes => Write32(bytes, 48, features), "unknown or missing required features");
        var corrupt = (byte[])valid.Clone();
        corrupt[52] ^= 1;
        Reject(corrupt, "outer CRC");
        var trailing = new byte[valid.Length + 1];
        valid.CopyTo(trailing, 0);
        Write32(trailing, 12, (uint)trailing.Length);
        Seal(trailing);
        Reject(trailing, "trailing bytes");
        Reject(new byte[NativeTerminalRenderFrameV5Decoder.MaximumFrameBytes + 1], "frame byte limit");
        Mutate(valid, bytes => bytes[64] ^= 1, "embedded text magic");
        Mutate(valid, bytes => bytes[64 + 88] ^= 1, "embedded text CRC");
        foreach (var offset in new[] { 24, 32, 40, 44 })
            Mutate(valid, bytes => { bytes[64 + offset] ^= 1; SealText(bytes.AsSpan(64, (int)U32(bytes, 32))); }, "embedded text identity");
        Mutate(valid, bytes => bytes[64 + (int)U32(bytes, 32) + 40] ^= 1, "embedded resource CRC");
        try { NativeTerminalRenderFrameV4Decoder.Decode(valid); throw new Exception("V4 accepted V5"); }
        catch (NativeTerminalRenderFrameV4DecodeException) { assertions++; }
    }

    private static void InvalidImages()
    {
        var valid = Sample();
        foreach (var value in new[] { 0UL, 2UL, ulong.MaxValue })
            ImageMutation(valid, (bytes, offset) => Write64(bytes, offset + 8, value), "image resource format/reference");
        ImageMutation(valid, (bytes, offset) => Write64(bytes, offset, 0), "zero placement");
        foreach (var offset in new[] { 32, 36 })
            ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + offset, 1), "source origin outside resource");
        foreach (var offset in new[] { 40, 44 })
            foreach (var value in new[] { 0U, 2U, uint.MaxValue })
                ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + offset, value), "source size");
        foreach (var value in new[] { 0U, 3U, uint.MaxValue })
            ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + 120, value), "image protocol");
        ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + 120, 1), "SIXEL with Kitty image ID");
        ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + 124, 1), "reserved image field");
        foreach (var offset in new[] { 16, 20 })
            foreach (var value in new[] { unchecked((uint)-4097), 4097U, unchecked((uint)int.MinValue), 512U })
                ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + offset, value), "origin bounds");
        foreach (var offset in new[] { 112, 116 })
            foreach (var value in new[] { 0U, 4097U, uint.MaxValue })
                ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + offset, value), "allocation bounds");

        var first = Image();
        var second = Image(2);
        Write32(second, 24, 1);
        var ordered = Frame(Text(), RgbaResources(), new[] { first, second }, Array.Empty<byte[]>());
        Require(NativeTerminalRenderFrameV5Decoder.Decode(ordered).Images.Count == 2, "ordered placements share a resource");
        Mutate(ordered, bytes => Write64(bytes, ImageOffset(bytes) + 144, 1), "placement identity repeated across z");
        Mutate(ordered, bytes => Write32(bytes, ImageOffset(bytes), 3), "placement alone may increase while z changes", reject: false);
        Mutate(ordered, bytes => Write32(bytes, ImageOffset(bytes) + 144 + 24, unchecked((uint)-1)), "z order");
        var duplicateOrder = Frame(Text(), RgbaResources(), new[] { first, first }, Array.Empty<byte[]>());
        Reject(duplicateOrder, "duplicate draw key");
    }

    private static void RatiosAndClips()
    {
        var valid = Sample();
        foreach (var offset in new[] { 48, 64, 80, 96 })
        {
            ImageMutation(valid, (bytes, draw) => Write64(bytes, draw + offset + 8, 0), "zero denominator");
            ImageMutation(valid, (bytes, draw) => Write64(bytes, draw + offset + 8, (1UL << 32) + 1), "denominator limit");
            ImageMutation(valid, (bytes, draw) => Write64(bytes, draw + offset, (1UL << 48) + 1), "numerator limit");
            ImageMutation(valid, (bytes, draw) => { Write64(bytes, draw + offset, 2); Write64(bytes, draw + offset + 8, 2); }, "noncanonical ratio");
        }
        foreach (var offset in new[] { 48, 64 })
            ImageMutation(valid, (bytes, draw) => Write64(bytes, draw + offset + 8, 2), "noncanonical zero ratio");
        foreach (var offset in new[] { 80, 96 })
            ImageMutation(valid, (bytes, draw) => Write64(bytes, draw + offset, 0), "zero image extent");
        foreach (var offset in new[] { 48, 64 })
            ImageMutation(valid, (bytes, draw) => Write64(bytes, draw + offset, 2), "image extent exceeds allocation");

        const ulong denominator = 1UL << 32;
        var precise = Image();
        Write64(precise, 48, 1);
        Write64(precise, 56, denominator);
        Write64(precise, 80, denominator - 1);
        Write64(precise, 88, denominator);
        Write32(precise, 112, 1);
        Write32(precise, 136, 1);
        var preciseFrame = Frame(Text(), RgbaResources(), new[] { precise }, Array.Empty<byte[]>());
        Require(NativeTerminalRenderFrameV5Decoder.Decode(preciseFrame).Images[0].Layout.X.Denominator == denominator, "exact wide-product boundary accepted");
        ImageMutation(preciseFrame, (bytes, draw) => Write64(bytes, draw + 56, denominator - 1), "extent exceeds boundary by less than double precision");

        foreach (var offset in new[] { 128, 132 })
            ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + offset, 2), "empty clip");
        foreach (var offset in new[] { 136, 140 })
            foreach (var value in new[] { 0U, 3U, uint.MaxValue })
                ImageMutation(valid, (bytes, draw) => Write32(bytes, draw + offset, value), "clip outside allocation");
        ImageMutation(valid, (bytes, draw) => { Write32(bytes, draw + 16, unchecked((uint)-1)); Write32(bytes, draw + 136, 1); }, "actual horizontal clip misses viewport");
        ImageMutation(valid, (bytes, draw) => { Write32(bytes, draw + 20, unchecked((uint)-1)); Write32(bytes, draw + 140, 1); }, "actual vertical clip misses viewport");
        ImageMutation(valid, (bytes, draw) => { Write32(bytes, draw + 16, 3); Write32(bytes, draw + 128, 1); }, "clip begins at right edge");
        ImageMutation(valid, (bytes, draw) => { Write32(bytes, draw + 20, 1); Write32(bytes, draw + 132, 1); }, "clip begins at bottom edge");
    }

    private static void InvalidGlyphsAndClosure()
    {
        var valid = Sample();
        foreach (var resource in new[] { 0UL, 1UL, 3UL, ulong.MaxValue })
            Mutate(valid, bytes => Write64(bytes, bytes.Length - 24, resource), "glyph resource format/reference");
        foreach (var change in new (int Offset, uint Value)[] { (0, 4), (4, 2), (16, 3), (20, 0), (20, 3), (20, uint.MaxValue), (24, 0), (24, 2), (28, 1) })
            Mutate(valid, bytes => Write32(bytes, bytes.Length - 32 + change.Offset, change.Value), "glyph fields");
        Reject(Frame(Text(wide: true), AlphaResources(), Array.Empty<byte[]>(), new[] { Glyph(0) }), "glyph overlays wide lead");
        Reject(Frame(Text(wide: true), AlphaResources(), Array.Empty<byte[]>(), new[] { Glyph(1) }), "glyph overlays continuation");
        Reject(Frame(Text(scaled: true), AlphaResources(), Array.Empty<byte[]>(), new[] { Glyph(2) }), "glyph outside scaled row effective columns");
        var ordered = Frame(Text(), AlphaResources(), Array.Empty<byte[]>(), new[] { Glyph(0), Glyph(1), Glyph(0, 1) });
        Require(NativeTerminalRenderFrameV5Decoder.Decode(ordered).Glyphs.Count == 3, "glyph row/column order");
        Reject(Frame(Text(), AlphaResources(), Array.Empty<byte[]>(), new[] { Glyph(1), Glyph(0) }), "descending glyph cells");
        Reject(Frame(Text(), AlphaResources(), Array.Empty<byte[]>(), new[] { Glyph(0), Glyph(0) }), "duplicate glyph cell");
        Reject(Frame(Text(), Resources(), new[] { Image() }, Array.Empty<byte[]>()), "unreferenced A8 resource");
        Reject(Frame(Text(), Resources(), Array.Empty<byte[]>(), new[] { Glyph() }), "unreferenced RGBA resource");
        Reject(Frame(Text(), Resources(), Array.Empty<byte[]>(), Array.Empty<byte[]>()), "unreferenced resource table");
        Reject(Frame(Text(), Encode(1), new[] { Image() }, Array.Empty<byte[]>()), "missing resource table entry");
    }

    private static void RejectsBeforePixelCopies()
    {
        var large = new byte[NativeTerminalGraphicsResourcesDecoder.MaximumImageBytes];
        var resources = Encode(3, new Entry(1, 2048, 2048, 1, large), new Entry(2, 3, 1, 2, new byte[] { 0, 127, 255 }));
        var bytes = Frame(Text(), resources, new[] { Image() }, new[] { Glyph() });
        Write64(bytes, bytes.Length - 24, 0);
        Seal(bytes);
        RejectWithoutPixels(bytes, "last glyph reference");
        Write64(bytes, bytes.Length - 24, 2);
        Write32(bytes, ImageOffset(bytes) + 124, 1);
        Seal(bytes);
        RejectWithoutPixels(bytes, "late image reserved field");
        var badCell = Frame(Text(wide: true), resources, new[] { Image() }, new[] { Glyph() });
        RejectWithoutPixels(badCell, "late V4 wide-cell check");
    }

    private static void RejectWithoutPixels(byte[] bytes, string name)
    {
        Reject(bytes, name + " warmup");
        var before = GC.GetAllocatedBytesForCurrentThread();
        Reject(bytes, name);
        Require(GC.GetAllocatedBytesForCurrentThread() - before < 65536, name + " copied pixel payload");
    }

    private static byte[] Sample() => Frame(Text(), Resources(), new[] { Image() }, new[] { Glyph() });
    private static byte[] RgbaResources() => Encode(2, new Entry(1, 1, 1, 1, new byte[] { 1, 2, 3, 4 }));
    private static byte[] AlphaResources() => Encode(3, new Entry(2, 3, 1, 2, new byte[] { 0, 127, 255 }));
    private static byte[] Resources() => Encode(3, new Entry(1, 1, 1, 1, new byte[] { 1, 2, 3, 4 }), new Entry(2, 3, 1, 2, new byte[] { 0, 127, 255 }));

    private static byte[] Image(ulong placement = 1)
    {
        var bytes = new byte[144];
        Write64(bytes, 0, placement);
        Write64(bytes, 8, 1);
        Write32(bytes, 28, 1);
        Write32(bytes, 40, 1);
        Write32(bytes, 44, 1);
        foreach (var offset in new[] { 56, 72, 80, 88, 96, 104 }) Write64(bytes, offset, 1);
        foreach (var offset in new[] { 112, 116, 120, 136, 140 }) Write32(bytes, offset, 2);
        return bytes;
    }

    private static byte[] Glyph(uint column = 0, uint row = 0)
    {
        var bytes = new byte[32];
        Write32(bytes, 0, column);
        Write32(bytes, 4, row);
        Write64(bytes, 8, 2);
        Write32(bytes, 16, 1);
        Write32(bytes, 20, 2);
        Write32(bytes, 24, 1);
        return bytes;
    }

    private static byte[] Frame(byte[] text, byte[] resources, byte[][] images, byte[][] glyphs)
    {
        var bytes = new byte[64 + text.Length + resources.Length + images.Length * 144 + glyphs.Length * 32];
        "TLRFRM5\0\x05\0\x40\0"u8.CopyTo(bytes);
        Write32(bytes, 12, (uint)bytes.Length);
        Write64(bytes, 16, 7);
        Write32(bytes, 24, U32(text, 40));
        Write32(bytes, 28, U32(text, 44));
        Write32(bytes, 32, (uint)text.Length);
        Write32(bytes, 36, (uint)resources.Length);
        Write32(bytes, 40, (uint)images.Length);
        Write32(bytes, 44, (uint)glyphs.Length);
        Write32(bytes, 48, 7);
        text.CopyTo(bytes, 64);
        resources.CopyTo(bytes, 64 + text.Length);
        var offset = 64 + text.Length + resources.Length;
        foreach (var image in images) { image.CopyTo(bytes, offset); offset += 144; }
        foreach (var glyph in glyphs) { glyph.CopyTo(bytes, offset); offset += 32; }
        Seal(bytes);
        return bytes;
    }

    private static byte[] Text(bool wide = false, bool scaled = false)
    {
        var columns = wide ? 2 : 4;
        var rows = wide ? 1 : 2;
        var utf8 = wide ? "界"u8.ToArray() : " "u8.ToArray();
        var graphemes = new byte[8 + utf8.Length];
        Write32(graphemes, 0, 8);
        Put16(graphemes, 4, (ushort)utf8.Length);
        graphemes[6] = wide ? (byte)2 : (byte)1;
        utf8.CopyTo(graphemes, 8);
        var palette = new byte[264 * 4];
        Array.Fill(palette, (byte)255);
        var cells = new byte[8 + rows * 8 + rows * columns * 12];
        Write32(cells, 0, 8);
        Write32(cells, 4, 12);
        if (wide) Write32(cells, 8, 2);
        if (scaled) Write32(cells, 12, 2);
        for (var index = 0; index < rows * columns; index++) Write32(cells, 8 + rows * 8 + index * 12, 1);
        if (wide) { Write32(cells, 8 + rows * 8 + 12, 0); Write32(cells, 8 + rows * 8 + 12 + 8, 1); }
        var sections = new[] { graphemes, new byte[16], palette, Array.Empty<byte>(), cells, Array.Empty<byte>() };
        var counts = new[] { 1, 1, 264, 0, columns * rows, 0 };
        var recordSizes = new[] { 8, 16, 4, 16, 0, 0 };
        var length = 240;
        foreach (var section in sections) length += (section.Length + 7) & ~7;
        var bytes = new byte[length];
        "TLRFRM4\0"u8.CopyTo(bytes);
        Put16(bytes, 8, 4);
        Put16(bytes, 12, 96);
        Put16(bytes, 14, 24);
        Put16(bytes, 16, 6);
        Write32(bytes, 20, (uint)length);
        Write64(bytes, 24, 7);
        Write64(bytes, 32, 7);
        Write32(bytes, 40, (uint)columns);
        Write32(bytes, 44, (uint)rows);
        Write32(bytes, 60, 2);
        Write32(bytes, 64, 96);
        Write64(bytes, 72, 0x7ff);
        var offset = 240;
        for (var index = 0; index < sections.Length; index++)
        {
            var directory = 96 + index * 24;
            Write32(bytes, directory, (uint)index + 1);
            Put16(bytes, directory + 4, index == 1 ? (ushort)2 : (ushort)1);
            Put16(bytes, directory + 6, 1);
            Write32(bytes, directory + 8, (uint)offset);
            Write32(bytes, directory + 12, (uint)sections[index].Length);
            Write32(bytes, directory + 16, (uint)counts[index]);
            Write32(bytes, directory + 20, (uint)recordSizes[index]);
            sections[index].CopyTo(bytes, offset);
            offset += (sections[index].Length + 7) & ~7;
        }
        SealText(bytes);
        return bytes;
    }

    private static void ImageMutation(byte[] source, Action<byte[], int> mutate, string name) => Mutate(source, bytes => mutate(bytes, ImageOffset(bytes)), name);
    private static void Mutate(byte[] source, Action<byte[]> mutate, string name, bool reject = true)
    {
        var bytes = (byte[])source.Clone();
        mutate(bytes);
        Seal(bytes);
        if (reject) Reject(bytes, name);
        else { NativeTerminalRenderFrameV5Decoder.Decode(bytes); assertions++; }
    }
    private static void Reject(byte[] bytes, string name)
    {
        try { NativeTerminalRenderFrameV5Decoder.Decode(bytes); }
        catch (NativeTerminalRenderFrameV5DecodeException) { assertions++; return; }
        throw new Exception("accepted malformed V5: " + name);
    }
    private static void Seal(byte[] bytes) => Write32(bytes, 52, Crc(bytes.AsSpan(0, 52), bytes.AsSpan(56)));
    private static void SealText(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(88), Crc(bytes.Slice(96)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(92), Crc(bytes.Slice(0, 92)));
    }
    private static int ImageOffset(byte[] bytes) => 64 + (int)U32(bytes, 32) + (int)U32(bytes, 36);
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
}
