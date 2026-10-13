using System;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;

internal static class PageProtocolValidation
{
    internal static void Run()
    {
        using (var resized = NativeTerminalCoreV2.Create(80, 24))
        {
            var effects = resized.ApplyOutputWithHostEffectsV2(1, Encoding.ASCII.GetBytes("\x1b[96$|\x1b[25*|\x1b[18t"));
            Require(effects.Records.Count == 3 && effects.Records[0].Kind == NativeTerminalHostEffectKindV2.ResizeRequest &&
                effects.Records[1].Kind == NativeTerminalHostEffectKindV2.ResizeRequest &&
                effects.Records[2].Payload.SequenceEqual(Encoding.ASCII.GetBytes("\x1b[8;24;80t")), "Resize requests changed geometry before host confirmation.");
            resized.ApplyResizeWithHostEffectsV2(2, 96, 25);
            var reply = resized.ApplyOutputWithHostEffectsV2(3, Encoding.ASCII.GetBytes("\x1b[18t"));
            Require(reply.Records.Single().Payload.SequenceEqual(Encoding.ASCII.GetBytes("\x1b[8;25;96t")), "Confirmed size did not reach native reports.");
        }
        using var terminal = NativeTerminalCoreV2.Create(12, 4);
        terminal.ApplyOutput(1, Encoding.ASCII.GetBytes("ONE\u001b[2 P\u001b[HTWO\u001b[?64l\u001b[1 P\u001b[HHIDDEN"));
        using var checkpoint = terminal.CaptureCheckpoint();
        var frame = NativeTerminalRenderFrameV4Decoder.Decode(checkpoint.ExportRenderFrameV4());
        Require(frame.Graphemes[frame.RenderRows[0].Cells[0].GraphemeId - 1].Text == "T" && !frame.Cursor.Visible,
            "Hidden-page output changed the visible page or cursor.");
        using var copy = NativeTerminalCoreV2.ImportSnapshot(checkpoint.ExportSnapshot());
        var replies = copy.ApplyOutputWithHostEffectsV2(2, Encoding.ASCII.GetBytes("\u001b[\"v\u001b[?6n\u001b[1$w"));
        Require(replies.Records.Count == 3 && replies.Records[0].Payload.SequenceEqual(Encoding.ASCII.GetBytes("\u001b[4;12;1;1;2\"w")) &&
            replies.Records[1].Payload.SequenceEqual(Encoding.ASCII.GetBytes("\u001b[?1;7;1R")), "Page report identity lost in bindings.");
        copy.ApplyOutput(3, Encoding.ASCII.GetBytes("\u001b[?64h"));
        using var visible = copy.CaptureCheckpoint();
        frame = NativeTerminalRenderFrameV4Decoder.Decode(visible.ExportRenderFrameV4());
        Require(frame.Graphemes[frame.RenderRows[0].Cells[0].GraphemeId - 1].Text == "H" && frame.Cursor.Visible,
            "Page coupling did not present the active page.");
        using var mixed = NativeTerminalCoreV2.Create(12, 4);
        mixed.ApplyOutput(1, Encoding.ASCII.GetBytes("\u001bP5;0;1!z1B5B39\u001b\\\u001b[5*z"));
        try { mixed.ApplyOutput(2, Encoding.ASCII.GetBytes("99m")); throw new InvalidOperationException("Mixed-source unsupported command accepted."); }
        catch (NativeTerminalException error) {
            Require(error.Diagnostic?.Contains("expanded_source=mixed\n") == true &&
                error.Diagnostic.Contains("expanded_origins=macro,5,0,3;output,2,0,3\n"), "Mixed macro/output coordinates lost.");
        }
        Console.WriteLine("Page DLL: visible/active separation, hidden cursor, checkpoint, reports and mixed diagnostic passed.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
