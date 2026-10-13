using System;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;

internal static class HostGeometryValidation
{
    internal static void Run()
    {
        ValidateTheme();
        using var terminal = NativeTerminalCoreV2.Create(80, 24);
        var geometry = new NativeTerminalHostGeometryV1(9, 18, 1920, 1080, 744, 476);
        if (NativeTerminalHostGeometryV1.Decode(geometry.Encode()) != geometry) throw new Exception("Geometry encoding mismatch.");
        var replies = terminal.ApplyBatchWithHostEffectsV2(
            NativeTerminalBatchEvent.HostGeometry(1, geometry),
            NativeTerminalBatchEvent.Output(2, Encoding.ASCII.GetBytes("\u001b[14t\u001b[14;2t\u001b[15t\u001b[16t\u001b[18t\u001b[19t")));
        var expected = new[] { "\u001b[4;432;720t", "\u001b[4;476;744t", "\u001b[5;1080;1920t",
            "\u001b[6;18;9t", "\u001b[8;24;80t", "\u001b[9;60;213t" };
        if (!replies.Records.Select(r => Encoding.ASCII.GetString(r.Payload.ToArray())).SequenceEqual(expected))
            throw new Exception("Geometry replies mismatch.");
        using var checkpoint = terminal.CaptureCheckpoint();
        using var restored = NativeTerminalCoreV2.ImportSnapshot(checkpoint.ExportSnapshot());
        var resized = restored.ApplyBatchWithHostEffectsV2(NativeTerminalBatchEvent.Resize(3,40,10),
            NativeTerminalBatchEvent.Output(4, Encoding.ASCII.GetBytes("\u001b[14t")));
        if (Encoding.ASCII.GetString(resized.Records.Single().Payload.ToArray()) != "\u001b[4;180;360t")
            throw new Exception("Restored geometry did not track resize.");
        // Both marshalling paths must publish the same event kind, without changing source order.
        restored.ApplyBatch(NativeTerminalBatchEvent.HostGeometry(5, default));
        if (restored.ApplyOutputWithHostEffectsV2(6, Encoding.ASCII.GetBytes("\u001b[16t")).Records.Count != 0)
            throw new Exception("Unavailable cell geometry was guessed.");
        Console.WriteLine("Host geometry DLL: ordered replies, exact wire codec, resize, restore and withdrawal passed.");
    }

    private static void ValidateTheme()
    {
        using var terminal = NativeTerminalCoreV2.Create(80, 24);
        var palette = Enumerable.Repeat(0xffffffU, 259).ToArray();
        palette[257] = 0;
        var dark = NativeTerminalHostThemeV1.Create(true, palette);
        palette[257] = 0xffffff;
        var light = NativeTerminalHostThemeV1.Create(false, palette);
        var replies = terminal.ApplyBatchWithHostEffectsV2(
            NativeTerminalBatchEvent.HostTheme(1, dark),
            NativeTerminalBatchEvent.Output(2, Encoding.ASCII.GetBytes("\u001b[?2031h")),
            NativeTerminalBatchEvent.HostTheme(3, light));
        if (!replies.Records.Select(r => Encoding.ASCII.GetString(r.Payload.ToArray()))
            .SequenceEqual(new[] { "\u001b[?997;1n", "\u001b[?997;2n" })) throw new Exception("Theme replies mismatch.");
        using var checkpoint = terminal.CaptureCheckpoint();
        var frame = NativeTerminalRenderFrameV4Decoder.Decode(checkpoint.ExportRenderFrameV4());
        if (frame.Palette.DefaultBackgroundRgb != 0xffffff) throw new Exception("Selected theme was not rendered.");
        using var restored = NativeTerminalCoreV2.ImportSnapshot(checkpoint.ExportSnapshot());
        var restoredReply = restored.ApplyOutputWithHostEffectsV2(4, "\u001b[?996n"u8.ToArray());
        if (Encoding.ASCII.GetString(restoredReply.Records.Single().Payload.ToArray()) != "\u001b[?997;2n")
            throw new Exception("Restored theme query mismatch.");
        Console.WriteLine("Host theme DLL: ordered reports, palette rendering and snapshot restore passed.");
    }
}
