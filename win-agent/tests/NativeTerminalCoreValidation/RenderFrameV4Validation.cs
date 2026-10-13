using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;

internal static class RenderFrameV4Validation
{
    internal static void Run()
    {
        const string family = "👨\u200d👩\u200d👧\u200d👦";
        var source = Encoding.UTF8.GetBytes("🇨🇳" + family + "\u001b[4:3;58:2::1:2:3m!");
        for (var split = 0; split <= source.Length; split++)
        {
            using var terminal = NativeTerminalCoreV2.Create(16, 4, 1, 20,
                NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);
            terminal.ApplyOutput(1, source.AsSpan(0, split));
            terminal.ApplyOutput(2, source.AsSpan(split));
            using var checkpoint = terminal.CaptureCheckpoint();
            var bytes = checkpoint.ExportRenderFrameV4();
            var frame = NativeTerminalRenderFrameV4Decoder.Decode(bytes);
            Require(frame.WidthProfile == NativeTerminalWidthProfile.Unicode16 && frame.Cursor.Column == 5,
                "Unicode width/cursor mismatch");
            Require(frame.Graphemes.Any(g => g.Text == family), "Family cluster lost");
            Require(frame.Renditions.Any(r => r.UnderlineStyle == NativeTerminalRenderUnderlineStyleV3.Curly),
                "V4 lost extended rendition state");
            try { checkpoint.ExportRenderFrameV3(); throw new Exception("V3 accepted profile 2"); }
            catch (NativeTerminalException error) { Require(error.Status == NativeTerminalStatus.RenderFrameVersionRequired, "Wrong render rejection"); }
            using var restored = NativeTerminalCoreV2.ImportSnapshot(checkpoint.ExportSnapshot());
            terminal.ApplyOutput(3, Encoding.UTF8.GetBytes("👍🏽"));
            restored.ApplyOutput(3, Encoding.UTF8.GetBytes("👍🏽"));
            using var actual = terminal.CaptureCheckpoint();
            using var expected = restored.CaptureCheckpoint();
            Require(actual.ExportRenderFrameV4().SequenceEqual(expected.ExportRenderFrameV4()), "Restored frame diverged");
            try { NativeTerminalRenderFrameV3Decoder.Decode(bytes); throw new Exception("V3 decoder accepted V4"); }
            catch (NativeTerminalRenderFrameV3DecodeException) { }
            foreach (var width in new uint[] { 0, 1, 3 }) {
                var invalid = bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(60), width);
                BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(92), Crc(invalid.AsSpan(0, 92)));
                Reject(invalid);
            }
            Reject(bytes.AsSpan(0, bytes.Length - 1).ToArray());
            var corrupt = bytes.ToArray(); corrupt[^1] ^= 1; Reject(corrupt);
            var detached = NativeTerminalRenderFrameV4Decoder.Decode(bytes);
            Array.Clear(bytes); Require(detached.Graphemes.Any(g => g.Text == family), "Frame retained caller-owned bytes");
        }
        using var legacy = NativeTerminalCoreV2.Create(8, 4);
        using var frozen = legacy.CaptureCheckpoint();
        Require(NativeTerminalRenderFrameV4Decoder.Decode(frozen.ExportRenderFrameV4()).WidthProfile == NativeTerminalWidthProfile.LegacyV1,
            "Legacy width profile changed");
        legacy.ApplyOutput(1, Encoding.UTF8.GetBytes("\x1b]17;#123456\x1b\\\x1b[1;2;3,|\x1b[2;4;5,|"));
        using var colored = legacy.CaptureCheckpoint();
        var colorBytes = colored.ExportRenderFrameV4();
        var colors = NativeTerminalRenderFrameV4Decoder.Decode(colorBytes).Palette;
        Require(colors.SelectionBackgroundRgb == 0x123456 && colors.ForegroundAlias == 2 && colors.BackgroundAlias == 3 &&
            colors.FrameForegroundAlias == 4 && colors.FrameBackgroundAlias == 5, "Extended colors were dropped");
        using var colorCopy = NativeTerminalCoreV2.ImportSnapshot(colored.ExportSnapshot());
        using var restoredColor = colorCopy.CaptureCheckpoint();
        Require(colorBytes.SequenceEqual(restoredColor.ExportRenderFrameV4()), "Color assignments were not restored");
        var paletteOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(colorBytes.AsSpan(96 + 2 * 24 + 8));
        foreach (var invalidAlias in new uint[] { 256, 258, 0x01000000 }) {
            var malformed = colorBytes.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(paletteOffset + 260 * 4), invalidAlias);
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(88), Crc(malformed.AsSpan(96)));
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(92), Crc(malformed.AsSpan(0, 92)));
            Reject(malformed);
        }
        var scaledText = Encoding.UTF8.GetBytes("\u001b#6ABCD\u001b[2;1H\u001b#3TOP\u001b[3;1H\u001b#4BOTTOM");
        for (var split = 0; split <= scaledText.Length; split++) {
            using var scaled = NativeTerminalCoreV2.Create(9, 4, 1, 20);
            scaled.ApplyOutput(1, scaledText.AsSpan(0, split)); scaled.ApplyOutput(2, scaledText.AsSpan(split));
            using var checkpoint = scaled.CaptureCheckpoint();
            var frame = NativeTerminalRenderFrameV4Decoder.Decode(checkpoint.ExportRenderFrameV4());
            Require(frame.RenderRows[0].LineRendition == NativeTerminalLineRendition.DoubleWidth &&
                frame.RenderRows[1].LineRendition == NativeTerminalLineRendition.DoubleHeightTop &&
                frame.RenderRows[2].LineRendition == NativeTerminalLineRendition.DoubleHeightBottom, "DEC line rendition was lost");
            using var restored = NativeTerminalCoreV2.ImportSnapshot(checkpoint.ExportSnapshot());
            using var copy = restored.CaptureCheckpoint();
            Require(checkpoint.ExportRenderFrameV4().SequenceEqual(copy.ExportRenderFrameV4()), "Scaled line restore diverged");
            try { checkpoint.ExportRenderFrameV3(); throw new Exception("V3 accepted scaled lines"); }
            catch (NativeTerminalException error) { Require(error.Status == NativeTerminalStatus.RenderFrameVersionRequired, "Wrong scaled-line version rejection"); }
        }
    }

    private static void Reject(byte[] bytes)
    {
        try { NativeTerminalRenderFrameV4Decoder.Decode(bytes); }
        catch (NativeTerminalRenderFrameV4DecodeException) { return; }
        throw new Exception("V4 accepted malformed frame");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82f63b78U); }
        return ~crc;
    }
}
