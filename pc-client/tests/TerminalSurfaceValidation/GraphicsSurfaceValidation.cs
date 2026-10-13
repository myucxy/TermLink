using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TermLink.Desktop;
using TermLink.TerminalCore;

internal static class GraphicsSurfaceValidation
{
    private static int assertions;
    private sealed record Resource(ulong Id, uint Width, uint Height, byte Format, byte[] Pixels);

    internal static void Run(string? fixtureRoot, string outputPng)
    {
        var surface = new NativeTerminalSurface();
        var size = new Size(1000, 800);
        var window = new Window { Width = size.Width, Height = size.Height, Content = surface };
        window.Show();
        surface.Measure(size); surface.Arrange(new Rect(size));
        Dispatcher.UIThread.RunJobs();
        try
        {
            if (fixtureRoot is not null) RustFixtures(surface, fixtureRoot, outputPng);
            LayersAndAlpha(surface);
            GlyphStylesAndScaling(surface);
            CacheLifetime(surface);
        }
        finally { window.Close(); }
        Require(surface.CachedBitmapBytes == 0, "detached surface retained bitmaps");
        Console.WriteLine($"Graphics surface: {assertions} actual Skia pixel/contract assertions passed; Rust fixtures {(fixtureRoot is null ? "not requested" : "verified")}.");
    }

    private static void RustFixtures(NativeTerminalSurface surface, string root, string outputPng)
    {
        var expected = new Dictionary<string, (int Columns, int Rows, int Images, int Glyphs, int Resources)>
        {
            ["empty"] = (12, 4, 0, 0, 0), ["kitty-crop-scrolled"] = (12, 4, 1, 0, 1),
            ["sixel-region-clip"] = (12, 4, 1, 0, 1), ["drcs-live"] = (80, 24, 1, 4, 3),
            ["drcs-scrolled-live"] = (80, 24, 1, 0, 1), ["drcs-history-frame-000"] = (80, 1, 1, 4, 3),
            ["drcs-history-frame-001"] = (80, 1, 1, 0, 1), ["drcs-frozen-viewport"] = (80, 24, 1, 4, 3),
        };
        foreach (var (name, counts) in expected)
        {
            var frame = NativeTerminalRenderFrameV5Decoder.Decode(File.ReadAllBytes(Path.Combine(root, name + ".v5.bin")));
            Require((frame.Columns, frame.Rows, frame.Images.Count, frame.Glyphs.Count, frame.Resources.Entries.Count) == counts,
                "Rust fixture dimensions/counts " + name);
            surface.Present(frame);
            using var bitmap = Render(surface);
            if (name == "kitty-crop-scrolled")
            {
                var image = frame.Images[0];
                Require(image.Row == -1 && image.Z == -7 && image.Layout.Source.X == 1 && image.Layout.Source.Width == 1,
                    "Rust crop/origin/z");
                Require(frame.Resources.Entries[0].Pixels.SequenceEqual(Convert.FromHexString("ff0000ff00ff00ff0000ffffffffff80")), "Rust straight RGBA payload");
                Pixel(bitmap, surface, 1.5, .2, 0x00ff00, "Rust cropped green image above viewport origin");
                Pixel(bitmap, surface, 1.5, .85, Blend(0xffffff, 0x445566, 128), "Rust half-alpha white image");
            }
            if (name == "sixel-region-clip")
            {
                Pixel(bitmap, surface, .2, 1.5, 0xff0000, "Rust SIXEL visible clip");
                Pixel(bitmap, surface, .2, .5, 0x445566, "Rust SIXEL clip excludes top row");
                Pixel(bitmap, surface, .8, 1.5, 0x445566, "Rust SIXEL destination half-cell width");
            }
            if (name is "drcs-live" or "drcs-history-frame-000" or "drcs-frozen-viewport")
            {
                foreach (var glyph in frame.Glyphs)
                {
                    Require(frame.Resources.TryGet(glyph.ResourceId, out var resource), "Rust glyph resource");
                    for (var y = 0; y < glyph.SourceHeight; y++)
                        for (var x = 0; x < glyph.SourceWidth; x++)
                            Require(resource.Pixels[(int)(y * resource.Width + glyph.SourceX + x)] ==
                                (glyph.ResourceId == 2 ? (y < 6 ? (byte)255 : (byte)0) : (y == 1 ? (byte)255 : (byte)0)), "Rust atlas mask");
                }
                Pixel(bitmap, surface, 2.5, .2, 0x112233, "Rust DRCS A foreground");
                Pixel(bitmap, surface, 2.5, .7, 0x445566, "Rust DRCS A transparent lower mask");
                Pixel(bitmap, surface, 3.5, .25, 0x445566, "Rust replaced atlas B differs from retained A");
                if (name == "drcs-live")
                {
                    Require(surface.CachedBitmapBytes == 16 + 15360 * 4 * 2, "bitmap budget charges expanded A8 masks");
                    bitmap.Save(outputPng, PngBitmapEncoderOptions.Default);
                }
            }
        }
    }

    private static void LayersAndAlpha(NativeTerminalSurface surface)
    {
        var text = Text("\x1b[2;1H\x1b[48;2;0;255;0m ");
        var red = new Resource(1, 1, 1, 1, new byte[] { 255, 0, 0, 255 });
        var low = Image(1, 1, z: int.MinValue, width: 2, height: 3);
        surface.Present(Frame(text, new[] { red }, new[] { low }));
        using (var bitmap = Render(surface))
        {
            Pixel(bitmap, surface, .5, .5, 0xff0000, "very low z over default background");
            Pixel(bitmap, surface, .5, 1.5, 0x00ff00, "nondefault background covers very low z");
        }
        var threshold = Image(1, 1, z: -1073741824, width: 2, height: 3);
        surface.Present(Frame(text, new[] { red }, new[] { threshold }));
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, 1.5, 0xff0000, "threshold image above cell background");

        var alpha = new Resource(1, 3, 1, 1, new byte[] { 255, 0, 0, 0, 255, 0, 0, 128, 255, 0, 0, 255 });
        var strip = Image(1, 1, sourceWidth: 3, width: 3, height: 1);
        surface.Present(Frame(Text(""), new[] { alpha }, new[] { strip }));
        using (var bitmap = Render(surface))
        {
            Pixel(bitmap, surface, .5, .5, 0x000000, "zero alpha ignores nonzero RGB");
            Pixel(bitmap, surface, 1.5, .5, 0x800000, "straight RGBA alpha is premultiplied once");
            Pixel(bitmap, surface, 2.5, .5, 0xff0000, "opaque RGBA");
        }

        var mask = new Resource(2, 1, 1, 2, new byte[] { 255 });
        var glyphText = Text("\ufffd");
        surface.Present(Frame(glyphText, new[] { red, mask }, new[] { Image(1, 1, z: -1) }, new[] { Glyph(2) }));
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, .5, 0xffffff, "glyph paints over negative image");
        surface.Present(Frame(glyphText, new[] { red, mask }, new[] { Image(1, 1, z: 0) }, new[] { Glyph(2) }));
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, .5, 0xff0000, "nonnegative image paints over glyph");
        surface.Select((0, 0), (0, 0));
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, .5, 0xffffff, "selected glyph remains readable above images");
        surface.ClearSelection();
    }

    private static void GlyphStylesAndScaling(NativeTerminalSurface surface)
    {
        var mask = new Resource(1, 1, 1, 2, new byte[] { 255 });
        var styled = Text("\x1b[38;2;240;32;16m\ufffd\x1b[7m\ufffd\x1b[27;8m\ufffd\x1b[28;5m\ufffd");
        surface.Present(Frame(styled, new[] { mask }, Array.Empty<byte[]>(), new[] { Glyph(1, 0), Glyph(1, 1), Glyph(1, 2), Glyph(1, 3) }));
        using (var bitmap = Render(surface))
        {
            Pixel(bitmap, surface, .5, .5, 0xf02010, "A8 takes SGR foreground");
            Pixel(bitmap, surface, 1.5, .5, 0x000000, "A8 takes inverse foreground");
            Pixel(bitmap, surface, 2.5, .5, 0x000000, "A8 conceal");
            Pixel(bitmap, surface, 3.5, .5, 0xf02010, "A8 visible blink phase");
        }
        Require(surface.CachedBitmapBytes == 4, "different SGR colors reuse one atlas bitmap");
        surface.SetBlinkPhase(false);
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, 3.5, .5, 0x000000, "A8 hidden blink phase");
        surface.SetBlinkPhase(true);

        var half = new Resource(1, 1, 2, 2, new byte[] { 255, 0 });
        var scaled = Text("\x1b#6\ufffd\x1b[2;1H\x1b#3\ufffd\x1b[3;1H\x1b#4\ufffd");
        surface.Present(Frame(scaled, new[] { half }, Array.Empty<byte[]>(), new[] { Glyph(1, 0, 0, height: 2), Glyph(1, 0, 1, height: 2), Glyph(1, 0, 2, height: 2) }));
        using (var bitmap = Render(surface))
        {
            Pixel(bitmap, surface, 1.5, .2, 0xffffff, "double-width glyph spans two physical columns");
            Pixel(bitmap, surface, 1.5, .8, 0x000000, "double-width glyph keeps mask transparency");
            Pixel(bitmap, surface, 1.5, 1.8, 0xffffff, "double-height top uses top mask half");
            Pixel(bitmap, surface, 1.5, 2.2, 0x000000, "double-height bottom uses bottom mask half");
            Pixel(bitmap, surface, 2.5, 1.5, 0x000000, "scaled glyph clips to its cell");
        }
        Require(surface.CellAt(new Point(surface.CellWidth * 1.5, surface.CellHeight * .2)) == (0, 0), "graphics preserve input row mapping");

        var invisible = new Resource(1, 1, 1, 2, new byte[] { 0 });
        surface.Present(Frame(Text("\ufffd"), new[] { invisible }, Array.Empty<byte[]>(), new[] { Glyph(1) }));
        using (var bitmap = Render(surface))
            for (var y = 2; y < (int)surface.CellHeight - 2; y++)
                for (var x = 1; x < (int)surface.CellWidth - 1; x++)
                    Require(Read(bitmap, x, y) == 0x000000, "transparent A8 also drew the fallback character");
        var translucent = new Resource(1, 1, 1, 2, new byte[] { 128 });
        surface.Present(Frame(Text("\x1b[38;2;255;0;0m\ufffd"), new[] { translucent }, Array.Empty<byte[]>(), new[] { Glyph(1) }));
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, .5, 0x800000, "A8 mask applies alpha once");
    }

    private static void CacheLifetime(NativeTerminalSurface surface)
    {
        var text = Text("");
        var first = Frame(text, new[] { new Resource(1, 1, 1, 1, new byte[] { 255, 0, 0, 255 }) }, new[] { Image(1, 1) });
        var identical = Frame(text, new[] { new Resource(1, 1, 1, 1, new byte[] { 255, 0, 0, 255 }) }, new[] { Image(1, 1) });
        var second = Frame(text, new[] { new Resource(1, 1, 1, 1, new byte[] { 0, 0, 255, 255 }) }, new[] { Image(1, 1) });
        surface.Present(first);
        using var frozen = Render(surface);
        Require(surface.CachedBitmapBytes == 4, "bitmap cost counted");
        var cache = (Dictionary<NativeTerminalGraphicsResource, WriteableBitmap>)typeof(NativeTerminalSurface)
            .GetField("bitmapCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(surface)!;
        var firstBitmap = cache[first.Resources.Entries[0]];
        Require(!ReferenceEquals(first.Resources.Entries[0], identical.Resources.Entries[0]), "frames have independently decoded resources");
        surface.Present(identical);
        Require(surface.CachedBitmapBytes == 4 && cache.Count == 1 && ReferenceEquals(firstBitmap, cache[identical.Resources.Entries[0]]),
            "identical independently decoded resource retains bitmap with current resource reference");
        Require(!cache.ContainsKey(first.Resources.Entries[0]), "cache releases previous frame resource reference");
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, .5, 0xff0000, "retained bitmap paints unchanged pixels");
        Require(ReferenceEquals(firstBitmap, cache[identical.Resources.Entries[0]]), "drawing retained resource allocates no replacement bitmap");
        surface.Present(second);
        Require(surface.CachedBitmapBytes == 0 && cache.Count == 0, "changed resource content releases old bitmap references");
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, .5, 0x0000ff, "same ID from another frame does not reuse old pixels");
        Require(!ReferenceEquals(firstBitmap, cache[second.Resources.Entries[0]]), "same ID with changed content creates a replacement bitmap");
        Pixel(frozen, surface, .5, .5, 0xff0000, "frozen rendered pixels survive cache disposal");
        surface.Present(Frame(text, Array.Empty<Resource>(), Array.Empty<byte[]>()));
        Require(surface.CachedBitmapBytes == 0 && cache.Count == 0, "resource absent from next frame is evicted");
        surface.Present(second);
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, .5, 0x0000ff, "evicted resource can be rendered again");
        surface.Present(NativeTerminalRenderFrameV4Decoder.Decode(text));
        Require(surface.GraphicsFrame is null && surface.CachedBitmapBytes == 0, "V4 clears graphics and cache");
        using (var bitmap = Render(surface)) Pixel(bitmap, surface, .5, .5, 0x000000, "V4 removes old image");
        surface.Present(first);
        using var restored = Render(surface);
        Pixel(restored, surface, .5, .5, 0xff0000, "previous immutable frame can be presented again");
    }

    private static byte[] Text(string output)
    {
        using var terminal = NativeTerminalCoreV2.Create(6, 3, 1, 20, NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);
        terminal.ApplyOutput(1, Encoding.UTF8.GetBytes("\x1b[?25l\x1b]10;#ffffff\x1b\\\x1b]11;#000000\x1b\\" + output));
        using var checkpoint = terminal.CaptureCheckpoint();
        return checkpoint.ExportRenderFrameV4();
    }

    private static NativeTerminalRenderFrameV5 Frame(byte[] text, Resource[] resources, byte[][] images, byte[][]? glyphs = null)
    {
        glyphs ??= Array.Empty<byte[]>();
        var resourceLength = 48 + resources.Sum(resource => 32 + ((resource.Pixels.Length + 3) & ~3));
        var payload = new byte[resourceLength];
        "TLGRS1\0\0\x01\0\x30\0"u8.CopyTo(payload);
        W32(payload, 12, (uint)payload.Length); W64(payload, 16, resources.Length == 0 ? 1 : resources.Max(resource => resource.Id) + 1);
        W32(payload, 24, (uint)resources.Length); W64(payload, 32, (ulong)resources.Sum(resource => resource.Pixels.Length));
        var offset = 48;
        foreach (var resource in resources)
        {
            W64(payload, offset, resource.Id); W32(payload, offset + 8, resource.Width); W32(payload, offset + 12, resource.Height);
            payload[offset + 16] = resource.Format; W32(payload, offset + 20, (uint)resource.Pixels.Length); W32(payload, offset + 24, Crc(resource.Pixels));
            resource.Pixels.CopyTo(payload, offset + 32); offset += 32 + ((resource.Pixels.Length + 3) & ~3);
        }
        W32(payload, 40, Crc(payload.AsSpan(0, 40), payload.AsSpan(44)));
        var bytes = new byte[64 + text.Length + payload.Length + images.Length * 144 + glyphs.Length * 32];
        "TLRFRM5\0\x05\0\x40\0"u8.CopyTo(bytes); W32(bytes, 12, (uint)bytes.Length); W64(bytes, 16, U64(text, 24));
        W32(bytes, 24, U32(text, 40)); W32(bytes, 28, U32(text, 44)); W32(bytes, 32, (uint)text.Length); W32(bytes, 36, (uint)payload.Length);
        W32(bytes, 40, (uint)images.Length); W32(bytes, 44, (uint)glyphs.Length); W32(bytes, 48, 7);
        text.CopyTo(bytes, 64); payload.CopyTo(bytes, 64 + text.Length); offset = 64 + text.Length + payload.Length;
        foreach (var image in images) { image.CopyTo(bytes, offset); offset += 144; }
        foreach (var glyph in glyphs) { glyph.CopyTo(bytes, offset); offset += 32; }
        W32(bytes, 52, Crc(bytes.AsSpan(0, 52), bytes.AsSpan(56)));
        return NativeTerminalRenderFrameV5Decoder.Decode(bytes);
    }

    private static byte[] Image(ulong placement, ulong resource, int z = 0, uint sourceWidth = 1, uint width = 1, uint height = 1)
    {
        var bytes = new byte[144]; W64(bytes, 0, placement); W64(bytes, 8, resource); W32(bytes, 24, unchecked((uint)z));
        W32(bytes, 40, sourceWidth); W32(bytes, 44, 1);
        foreach (var offset in new[] { 56, 72, 88, 104 }) W64(bytes, offset, 1);
        W64(bytes, 80, width); W64(bytes, 96, height); W32(bytes, 112, width); W32(bytes, 116, height);
        W32(bytes, 120, 2); W32(bytes, 136, width); W32(bytes, 140, height); return bytes;
    }
    private static byte[] Glyph(ulong resource, uint column = 0, uint row = 0, uint height = 1)
    {
        var bytes = new byte[32]; W32(bytes, 0, column); W32(bytes, 4, row); W64(bytes, 8, resource); W32(bytes, 20, 1); W32(bytes, 24, height); return bytes;
    }
    private static RenderTargetBitmap Render(NativeTerminalSurface surface)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize((int)surface.Bounds.Width, (int)surface.Bounds.Height), new Vector(96, 96));
        bitmap.Render(surface); return bitmap;
    }
    private static void Pixel(RenderTargetBitmap bitmap, NativeTerminalSurface surface, double column, double row, int expected, string message)
    {
        var actual = Read(bitmap, (int)(column * surface.CellWidth), (int)(row * surface.CellHeight));
        Require(actual == expected, $"{message}: expected {expected:x6}, got {actual:x6}");
    }
    private static int Read(RenderTargetBitmap bitmap, int x, int y)
    {
        var pixel = Marshal.AllocHGlobal(4);
        try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), pixel, 4, 4); return Marshal.ReadInt32(pixel) & 0xffffff; }
        finally { Marshal.FreeHGlobal(pixel); }
    }
    private static int Blend(int foreground, int background, int alpha)
    {
        static int Channel(int foreground, int background, int alpha) => (foreground * alpha + 127) / 255 + (background * (255 - alpha) + 127) / 255;
        return (Channel(foreground >> 16 & 255, background >> 16 & 255, alpha) << 16) |
            (Channel(foreground >> 8 & 255, background >> 8 & 255, alpha) << 8) | Channel(foreground & 255, background & 255, alpha);
    }
    private static uint Crc(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
    {
        var crc = uint.MaxValue;
        foreach (var value in first) crc = CrcByte(crc, value);
        foreach (var value in second) crc = CrcByte(crc, value);
        return ~crc;
    }
    private static uint CrcByte(uint crc, byte value)
    {
        if (Sse42.IsSupported) return Sse42.Crc32(crc, value);
        crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82f63b78U); return crc;
    }
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void W64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
}
