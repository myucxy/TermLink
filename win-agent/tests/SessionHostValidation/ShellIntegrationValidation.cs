#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TermLink.Agent;
using TermLink.SessionHost;
using TermLink.TerminalCore;

internal static class ShellIntegrationValidation
{
    internal static void Run(string executable)
    {
        var raw = "\"" + executable + "\" -NoLogo -NoProfile";
        Require(PowerShellShellIntegration.Prepare(raw + " -Command 1", ManagedShellType.PowerShell7) == raw + " -Command 1", "Custom invocation changed.");
        var prepared = PowerShellShellIntegration.Prepare(raw, ManagedShellType.PowerShell7);
        Require(prepared.Contains("-NoExit -EncodedCommand ", StringComparison.Ordinal), "Interactive hook missing.");
        var directory = Path.GetFullPath(Path.Combine("artifacts/validation", "shell-live-" + Guid.NewGuid().ToString("N"), "中文 空格"));
        Directory.CreateDirectory(directory);
        using var host = new ConPtySessionHost(SessionHostOptions.Parse(new[] { "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(prepared)),
            "--working-directory-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(directory)) }));
        var type = typeof(ConPtySessionHost); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var coord = type.GetNestedType("COORD", flags)!; var size = Activator.CreateInstance(coord)!;
        coord.GetField("X")!.SetValue(size, (short)100); coord.GetField("Y")!.SetValue(size, (short)24);
        type.GetMethod("CreateInnerPseudoConsole", flags)!.Invoke(host, new[] { size });
        using var output = new FileStream(new SafeFileHandle((IntPtr)type.GetField("terminalOutput", flags)!.GetValue(host)!, false), FileAccess.Read);
        using var input = new FileStream(new SafeFileHandle((IntPtr)type.GetField("terminalInput", flags)!.GetValue(host)!, false), FileAccess.Write);
        using var terminal = NativeTerminalCoreV2.Create(100, 24, 1, 1000);
        var sync = new object(); ulong sequence = 1; NativeTerminalShellState? state = null; Exception? failure = null;
        var reader = Task.Run(() => {
            var buffer = new byte[4096];
            try {
                int read;
                while ((read = output.Read(buffer, 0, buffer.Length)) > 0) lock (sync) {
                    var effects = terminal.ApplyOutputWithHostEffectsV2(sequence++, buffer.AsSpan(0, read));
                    foreach (var reply in effects.Records.Where(r => r.Kind == NativeTerminalHostEffectKindV2.PtyReply)) {
                        var bytes = reply.Payload.ToArray(); input.Write(bytes); input.Flush();
                    }
                    if (terminal.TryCaptureCheckpoint(out var checkpoint, out _)) using (checkpoint) { state = checkpoint.ShellState(); }
                }
            } catch (IOException) { }
            catch (Exception error) { lock (sync) failure = error; }
        });
        void Wait(Func<NativeTerminalShellState, bool> condition, string stage) {
            if (!SpinWait.SpinUntil(() => { lock (sync) return failure != null || state != null && condition(state); }, 15000))
                throw new InvalidOperationException("PowerShell shell integration timed out: " + stage);
            lock (sync) if (failure != null) throw new InvalidOperationException("PowerShell integration failed: " + stage, failure);
        }
        void Send(string text) { lock (sync) { var bytes = Encoding.UTF8.GetBytes(text + "\r"); input.Write(bytes); input.Flush(); } }
        try {
            type.GetMethod("StartChildSuspended", flags)!.Invoke(host, null);
            Wait(s => s.Commands.LastOrDefault()?.Phase == 1, "initial prompt");
            lock (sync) Require(NativeTerminalShellState.LocalWindowsDirectory(state!.CwdUri) == directory, "Chinese/space cwd mismatch.");
            Send("Write-Output '__TL_SHELL_OK__'");
            Wait(s => s.Commands.Any(c => c.Phase == 3 && c.ExitCode == 0) && s.Commands.Last().Phase == 1, "successful command");
            Send("cmd /c exit 7");
            Wait(s => s.Commands.Any(c => c.Phase == 3 && c.ExitCode == 1) && s.Commands.Last().Phase == 1, "failed command");
            Send("Write-Error '__TL_EXPECTED_FAILURE__'");
            Wait(s => s.Commands.Count(c => c.Phase == 3 && c.ExitCode == 1) >= 2 && s.Commands.Last().Phase == 1, "cmdlet failure after native failure");
            Send("Set-Location ..");
            Wait(s => NativeTerminalShellState.LocalWindowsDirectory(s.CwdUri) == Directory.GetParent(directory)!.FullName && s.Commands.Last().Phase == 1, "changed cwd");
            lock (sync) Require(state!.Commands.Any(c => c.Input != null && c.Output != null && c.End != null), "Missing actual PSReadLine phase boundaries.");
            Console.WriteLine("Real PowerShell + ConPTY: cwd UTF-8/spaces, prompt/input/output/end, success/failure and directory change passed.");
        }
        finally {
            type.GetMethod("TerminateProcessTree", flags)!.Invoke(host, new object[] { (uint)0 });
            host.Dispose();
            if (!reader.Wait(5000)) throw new InvalidOperationException("Shell probe output failed to drain.");
        }
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
