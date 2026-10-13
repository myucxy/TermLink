using System;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;

internal static class TraditionalProtocolValidation
{
    internal static void Run()
    {
        ValidateMacros();
        var prefix = Encoding.ASCII.GetBytes("\u001b%@\u001bP1!uB\u001b\\\u001b-<\u000e!\u001b G");
        for (var split = 0; split <= prefix.Length; split++)
        {
            using var terminal = NativeTerminalCoreV2.Create(20, 6);
            terminal.ApplyOutput(1, prefix.AsSpan(0, split));
            terminal.ApplyOutput(2, prefix.AsSpan(split));
            using var checkpoint = terminal.CaptureCheckpoint();
            var frame = NativeTerminalRenderFrameV4Decoder.Decode(checkpoint.ExportRenderFrameV4());
            Require(frame.Graphemes[frame.RenderRows[0].Cells[0].GraphemeId - 1].Text == "Ą", "UPSS glyph differs");
            using var copy = NativeTerminalCoreV2.ImportSnapshot(checkpoint.ExportSnapshot());
            Require(copy.EncodeKey(NativeTerminalKey.ArrowUp).SequenceEqual(new byte[] { 0x9b, 0x41 }), "C1 input lost after restore");
            var replies = copy.ApplyOutputWithHostEffectsV2(3, Encoding.ASCII.GetBytes("\u001b[&u\u001b[5n"));
            Require(replies.Records.Count == 2 && replies.Records[0].Payload.SequenceEqual(new byte[] { 0x90, 0x31, 0x21, 0x75, 0x42, 0x9c }), "UPSS reply changed on restore");
            Require(replies.Records[1].Payload.SequenceEqual(new byte[] { 0x9b, 0x30, 0x6e }), "C1 DSR reply differs");
            var vt52 = copy.ApplyOutputWithHostEffectsV2(4, Encoding.ASCII.GetBytes("\u000f\u001b%G\u001b F\u001b[?2l\u001bHV\u001bZ"));
            Require(vt52.Records.Single().Payload.SequenceEqual(Encoding.ASCII.GetBytes("\u001b/Z")), "VT52 identity missing");
            using var saved = copy.CaptureCheckpoint();
            using var legacy = NativeTerminalCoreV2.ImportSnapshot(saved.ExportSnapshot());
            Require(legacy.EncodeKey(NativeTerminalKey.ArrowUp).SequenceEqual(new byte[] { 0x1b, 0x41 }), "VT52 input lost after restore");
            legacy.ApplyOutput(5, Encoding.ASCII.GetBytes("\u001b<"));
            Require(legacy.EncodeKey(NativeTerminalKey.ArrowUp).SequenceEqual(new byte[] { 0x1b, 0x5b, 0x41 }), "ANSI re-entry failed");
        }
        using var invalid = NativeTerminalCoreV2.Create(20, 6);
        try { invalid.ApplyOutput(1, Encoding.ASCII.GetBytes("\u001bP0002!uB\u001b\\")); throw new Exception("Invalid UPSS size accepted"); }
        catch (NativeTerminalException error) { Require(error.Diagnostic?.Contains("rejected_parameter=0002\n") == true, "DCS parameter spelling lost"); }
        var oversized = new byte[65537]; oversized[0] = 0x1b; oversized[1] = 0x50; oversized[^1] = 0x9c;
        Require(NativeTerminalTraditionalProtocol.NormalizeC1Reply(oversized, true) == null, "Oversized mixed response accepted");
        Require(NativeTerminalTraditionalProtocol.NormalizeC1Reply(Encoding.UTF8.GetBytes("Лܐݝ")) == null, "UTF8 text classified as a response");
        Console.WriteLine("Traditional protocol DLL, checkpoint, C1 and VT52 validation passed.");
    }
    private static void ValidateMacros()
    {
        using var terminal = NativeTerminalCoreV2.Create(20, 6);
        terminal.ApplyOutput(1, Encoding.ASCII.GetBytes("\u001bP1;0;1!z1B5B356E1B5B356E4F4B\u001b\\"));
        using var checkpoint = terminal.CaptureCheckpoint();
        using var copy = NativeTerminalCoreV2.ImportSnapshot(checkpoint.ExportSnapshot());
        var reply = copy.ApplyOutputWithHostEffectsV2(2, Encoding.ASCII.GetBytes("\u001b[1*z\u001b[?62n\u001b[?63;42n"));
        Require(reply.Records.Count == 4 && reply.Records[0].Ordinal == 0 && reply.Records[1].Ordinal == 1 &&
            reply.Records[0].SourceOffsetEnd == reply.Records[1].SourceOffsetEnd, "Macro query source ordering lost");
        Require(reply.Records[2].Payload.SequenceEqual(Encoding.ASCII.GetBytes("\u001b[16383*{")), "Macro storage report missing");
        using var rendered = copy.CaptureCheckpoint();
        var frame = NativeTerminalRenderFrameV4Decoder.Decode(rendered.ExportRenderFrameV4());
        Require(frame.Graphemes[frame.RenderRows[0].Cells[0].GraphemeId - 1].Text == "O", "Macro output not rendered");
        copy.ApplyOutput(3, Encoding.ASCII.GetBytes("\u001bP0;0;1!z1B5B302A7A\u001b\\"));
        try { copy.ApplyOutputWithHostEffectsV2(4, Encoding.ASCII.GetBytes("\u001b[0*z")); throw new Exception("Recursive macro accepted"); }
        catch (NativeTerminalException error) {
            Require(error.Status == NativeTerminalStatus.LimitExceeded && error.Diagnostic?.Contains("DECINVM nesting exceeds 16 invocations") == true, "Macro resource limit lost");
        }
        copy.ApplyOutput(4, Encoding.ASCII.GetBytes("LIVE"));
        copy.ApplyOutput(5, Encoding.ASCII.GetBytes("\u001bP5;0;1!z1B5B3939396D\u001b\\"));
        try { copy.ApplyOutput(6, Encoding.ASCII.GetBytes("\u001b[5*z")); throw new Exception("Unknown macro SGR accepted"); }
        catch (NativeTerminalException error) {
            Require(error.Diagnostic?.StartsWith("TL-DIAG/3\n") == true && error.Diagnostic.Contains("expanded_sequence=<ESC>[999m\n") &&
                error.Diagnostic.Contains("expanded_start=5:0\nexpanded_end=5:6\n"), "Macro expansion coordinates lost");
        }
        Console.WriteLine("Macro DLL: restore, query ordering, storage report and recursion rollback passed.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
