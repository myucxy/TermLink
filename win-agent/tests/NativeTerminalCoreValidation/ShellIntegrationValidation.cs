using System;
using System.IO;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;
internal static class ShellIntegrationValidation
{
    internal static void Run()
    {
        using var t = NativeTerminalCoreV2.Create(12, 3, 1, 100);
        t.ApplyOutput(1, Encoding.UTF8.GetBytes("\u001b]7;file:///C:/work/%E4%B8%AD%20test\u001b\\\u001b]133;A\u001b\\PS> \u001b]133;B\u001b\\echo hello\r\n\u001b]133;C\u001b\\hello\r\n\u001b]133;D;7\u001b\\more\r\nmore\r\n"));
        using var checkpoint = t.CaptureCheckpoint();
        var state = checkpoint.ShellState(); var bytes = checkpoint.ExportShellStateV1();
        Require(state.Commands.Count == 1 && state.Commands[0].Phase == 3 && state.Commands[0].ExitCode == 7, "Command lifecycle lost.");
        Require(NativeTerminalShellState.LocalWindowsDirectory(state.CwdUri) == "C:\\work\\中 test", "UTF-8 URI decoding changed.");
        using var history = t.CaptureHistory();
        Require(history.ResolveAnchor(state.Commands[0].Prompt.LineId, state.Commands[0].Prompt.CellOffset) > 0, "Command anchor missing.");
        for (var length = 0; length < bytes.Length; length++) {
            try { NativeTerminalShellState.Decode(bytes.Take(length).ToArray(), checkpoint.Info.NextSourceSequence); throw new Exception("Truncated state accepted."); }
            catch (InvalidDataException) { }
        }
        foreach (var uri in new[] {"file://remote/C:/secret", "file:////server/share", "file:///C:/a%5Cb", "file:///C:/a%00", "file:///C:/a%FF", "file:///C:/a%3Ab", "file:///C:/../Windows", "file:///C:/a.", "https://example.test/a"})
            Require(NativeTerminalShellState.LocalWindowsDirectory(uri) == null, "Unsafe/nonlocal directory accepted.");
        t.ApplyOutput(2, Encoding.ASCII.GetBytes("\u001b[3J\u001bc"));
        Require(checkpoint.ExportShellStateV1().SequenceEqual(bytes), "Checkpoint shell state changed after reset.");
        using var cleared = t.CaptureCheckpoint();
        Require(cleared.ShellState().Commands.Count == 0 && cleared.ShellState().NextCommandId == 2, "Reset reused command IDs.");
        var actions = t.ApplyOutputWithHostEffectsV2(3, Encoding.ASCII.GetBytes("\a\u001b[7;32;1;0;25,~\u001b]9;4;2;75\u001b\\"));
        Require(actions.Records.Count == 2 && actions.Records.All(r => r.Kind == NativeTerminalHostEffectKindV2.PlatformAction), "Platform actions not typed.");
        Require(t.HostSignals() == new NativeTerminalHostSignals(2,75), "Progress missing from ABI.");
        var committed = t.ExportShellStateV1();
        t.ApplyOutput(4, Encoding.ASCII.GetBytes("\u001b[3"));
        Require(!t.TryCaptureCheckpoint(out var pending, out var pendingStatus) &&
            pendingStatus == NativeTerminalStatus.SnapshotNotEligible, "Partial CSI unexpectedly became snapshot eligible.");
        pending?.Dispose();
        Require(t.ExportShellStateV1().SequenceEqual(committed) && t.ShellState().Commands.Count == 0,
            "Partial output made committed Shell state unavailable or changed it.");
        t.ApplyOutput(5, Encoding.ASCII.GetBytes("1m\u001b]7;file:///C:/uncommitted"));
        Require(t.ExportShellStateV1().SequenceEqual(committed), "An incomplete directory marker was committed.");
        t.ApplyOutput(6, Encoding.ASCII.GetBytes("\u001b\\"));
        Require(t.ShellState().CwdUri == "file:///C:/uncommitted", "A completed directory marker was lost.");
        Console.WriteLine("Shell state DLL, frozen anchors, bounds and path validation passed.");
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
