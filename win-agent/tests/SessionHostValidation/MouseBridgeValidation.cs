using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TermLink.LocalIpc;
using TermLink.SessionHost;

internal static class MouseBridgeValidation
{
    // A disposable record-reading child, never the user's shell or Codex session.
    internal static void Child()
    {
        var input = GetStdHandle(-10);
        var output = GetStdHandle(-11);
        GetConsoleMode(input, out var im);
        GetConsoleMode(output, out var om);
        try
        {
            SetConsoleMode(output, om | 4);
            SetConsoleMode(input, (im & ~(1U | 2U | 4U | 64U | 512U)) | 8 | 16 | 128);
            // Leave a terminal-query expectation open while a physical Escape and
            // a mouse event arrive. Classification must preserve their order.
            Console.Write("\u001b[?1003;1006h\u001b[6n__MOUSE_READY__");
            var timer = Stopwatch.StartNew();
            var records = new Record[64];
            int wheel = 0, keys = 0, presses = 0, releases = 0, moves = 0;
            uint buttons = 0;
            var order = new StringBuilder();
            while (timer.ElapsedMilliseconds < 8000 && keys < 4)
            {
                if (GetNumberOfConsoleInputEvents(input, out var count) && count > 0 &&
                    ReadConsoleInput(input, records, Math.Min(count, 64), out var read))
                {
                    for (var i = 0; i < read; i++)
                    {
                        var r = records[i];
                        if (r.Type == 1 && r.KeyDown != 0)
                        {
                            if (r.Character == 'q') { keys++; order.Append('K'); }
                            if (r.Character == '\u001b') order.Append('E');
                        }
                        if (r.Type != 2) continue;
                        if ((r.Mouse.EventFlags & 0x0c) != 0) { wheel++; order.Append('W'); }
                        else if ((r.Mouse.EventFlags & 1) != 0)
                        { moves++; if ((r.Mouse.ButtonState & 7) != 0) order.Append('D'); }
                        else if ((r.Mouse.ButtonState & 7) != buttons)
                        {
                            if ((r.Mouse.ButtonState & 7) == 0) { releases++; order.Append('R'); }
                            else { presses++; order.Append('P'); }
                            buttons = r.Mouse.ButtonState & 7;
                        }
                    }
                }
                else Thread.Sleep(5);
            }
            Console.Write($"\u001b[?1003;1006l__MOUSE_RESULT__:{wheel},{keys},{presses},{releases},{moves}__END____ORDER__:{order}__");
        }
        finally { SetConsoleMode(input, im); SetConsoleMode(output, om); }
    }

    internal static void Run()
    {
        foreach (var mode in new[] { "direct", "standalone", "managed" }) RunScenario(mode);
        Console.WriteLine("Mouse bridge: direct, standalone and managed nested ConPTY wheel/click/drag parity passed.");
    }

    // Explicit opt-in: starts only a new CLI, never an existing user session.
    internal static void RunClaudeStartup(string powerShellPath, string claudePath)
    {
        if (!File.Exists(powerShellPath) || !File.Exists(claudePath))
            throw new FileNotFoundException("Provide installed PowerShell and Claude paths.");
        RunScenario("managed", powerShellPath, claudePath);
    }

    private static void RunScenario(string mode, string powerShellPath = null, string claudePath = null)
    {
        var session = Guid.NewGuid();
        var token = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var name = "termlink-mouse-test-" + session.ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var child = powerShellPath == null
            ? "\"" + Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.Validation.exe") + "\" --mouse-probe-child"
            : "\"" + powerShellPath + "\" -NoLogo -NoProfile";
        var command = mode == "direct" ? child : "\"" + Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.exe") +
            "\" --command-base64 " + Encode(child) + " --timeout-ms " + (powerShellPath == null ? "15000" : "45000") + " --diagnostics";
        if (mode == "managed") command += " --agent-pipe " + name + " --session-id " + session + " --handshake-token " + Convert.ToBase64String(token);
        using var outer = new ConPtySessionHost(SessionHostOptions.Parse(new[] { "--command-base64", Encode(command) }));
        var type = typeof(ConPtySessionHost);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var coord = type.GetNestedType("COORD", BindingFlags.NonPublic)!;
        var size = Activator.CreateInstance(coord)!;
        coord.GetField("X")!.SetValue(size, (short)80);
        coord.GetField("Y")!.SetValue(size, (short)24);
        type.GetMethod("CreateInnerPseudoConsole", flags)!.Invoke(outer, new[] { size });
        IntPtr Handle(string field) => (IntPtr)type.GetField(field, flags)!.GetValue(outer)!;
        using var output = new FileStream(new SafeFileHandle(Handle("terminalOutput"), false), FileAccess.Read);
        using var input = new FileStream(new SafeFileHandle(Handle("terminalInput"), false), FileAccess.Write);
        var text = new StringBuilder();
        var reader = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try { int count; while ((count = output.Read(buffer, 0, buffer.Length)) > 0)
                lock (text) text.Append(Encoding.UTF8.GetString(buffer, 0, count)); }
            catch (IOException) { }
        });
        var localEpochs = 0;
        var agent = mode == "managed" ? Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(10000);
            await pipe.WaitForConnectionAsync(timeout.Token);
            var hello = LocalFrameCodec.Read(pipe);
            LocalPayloadCodec.ParseTerminalHello(hello.Payload, token);
            LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.AgentHello, session, 0,
                LocalPayloadCodec.TerminalHello(Environment.ProcessId, token)));
            LocalFrame frame;
            while ((frame = LocalFrameCodec.Read(pipe)) != null)
            {
                if (frame.Type == LocalFrameType.Capabilities) continue;
                if (frame.Type == LocalFrameType.LocalInputObserved) Interlocked.Increment(ref localEpochs);
                LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.Ack, session, frame.Sequence, Array.Empty<byte>()));
            }
        }) : Task.CompletedTask;
        try
        {
            type.GetMethod("StartChildSuspended", flags)!.Invoke(outer, null);
            bool Contains(string marker) { lock (text) return text.ToString().Contains(marker, StringComparison.Ordinal); }
            void Wait(string marker)
            {
                if (!SpinWait.SpinUntil(() => Contains(marker) || agent.IsFaulted, 12000) || agent.IsFaulted)
                    throw new Exception(powerShellPath == null
                        ? mode + " mouse probe failed: " + text
                        : "Claude startup probe timed out or its test bridge failed.", agent.Exception);
            }
            if (powerShellPath != null)
            {
                void Send(string value) { input.Write(Encoding.UTF8.GetBytes(value)); input.Flush(); }
                Wait("PS ");
                Send("& '" + claudePath.Replace("'", "''") + "'\r");
                Wait("\u001b[16t");
                Thread.Sleep(1500);
                var cliProcess = type.GetField("processInfo", flags)!.GetValue(outer)!;
                var cliHandle = (IntPtr)cliProcess.GetType().GetField("hProcess")!.GetValue(cliProcess)!;
                if (WaitForSingleObject(cliHandle, 0) == 0)
                    throw new Exception("Claude geometry query terminated the test shell.");
                Send("\u0003");
                Thread.Sleep(300);
                Send("\u0003");
                Thread.Sleep(500);
                Send("Write-Output ('__CLAUDE_' + 'SHELL_ALIVE__')\r");
                Wait("__CLAUDE_SHELL_ALIVE__");
                Send("exit 0\r");
                Wait(";authority_failed=false;input_failure=false;output_disconnected=false");
                if (WaitForSingleObject(cliHandle, 5000) != 0 || !GetExitCodeProcess(cliHandle, out var cliExit) || cliExit != 0)
                    throw new Exception("Claude test shell did not exit cleanly.");
                Console.WriteLine("Claude startup: real CSI 16t observed, authority survived, shell accepted input after interrupt, exit 0.");
                return;
            }
            Wait("__MOUSE_READY__");
            // Hover must be forwarded but cannot invalidate Android's control lease.
            input.Write(Encoding.ASCII.GetBytes("\u001b[<35;5;5M")); input.Flush();
            Thread.Sleep(100);
            if (mode == "managed" && Volatile.Read(ref localEpochs) != 0) throw new Exception("hover reclaimed control");
            for (var i = 0; i < 4; i++)
            {
                input.Write(Encoding.ASCII.GetBytes("\u001b[27;1;27;1;0;1_\u001b[<64;5;5M\u001b[<65;5;5M\u001b[<0;5;5M\u001b[<32;6;6M\u001b[<0;6;6mq"));
                input.Flush();
                Thread.Sleep(40);
            }
            Wait("__END__");
            if (!Contains("__MOUSE_RESULT__:8,4,4,4,5__END__")) throw new Exception(mode + " mouse parity failed: " + text);
            Wait("__ORDER__:EWWPDRKEWWPDRKEWWPDRKEWWPDRK__");
            if (mode == "managed" && Volatile.Read(ref localEpochs) < 20) throw new Exception("mouse actions did not reclaim local control");
            if (mode != "direct") Wait(";authority_failed=false;input_failure=false;output_disconnected=false");
            var process = type.GetField("processInfo", flags)!.GetValue(outer)!;
            var handle = (IntPtr)process.GetType().GetField("hProcess")!.GetValue(process)!;
            if (WaitForSingleObject(handle, 5000) != 0 || !GetExitCodeProcess(handle, out var exit) || exit != 0)
                throw new Exception(mode + " probe did not exit cleanly: " + text);
            Console.WriteLine(mode + ": 8 wheels, 4 keys, 4 presses, 4 releases, 5 moves; Escape/mouse order preserved.");
        }
        finally
        {
            type.GetMethod("TerminateProcessTree", flags)!.Invoke(outer, new object[] { (uint)0 });
            outer.Dispose();
            pipe.Dispose();
            try { if (!Task.WhenAll(reader, agent).Wait(2000)) throw new Exception("mouse fixture did not stop"); }
            catch (AggregateException error) when (error.InnerExceptions.All(e =>
                e is IOException || e is ObjectDisposedException || e is OperationCanceledException)) { }
        }
    }

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    [StructLayout(LayoutKind.Explicit, Size = 20, CharSet = CharSet.Unicode)] private struct Record
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(14)] public char Character;
        [FieldOffset(4)] public WindowsConsoleMouseRecord Mouse;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll")] private static extern bool GetNumberOfConsoleInputEvents(IntPtr h, out uint count);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW")] private static extern bool ReadConsoleInput(IntPtr h, [Out] Record[] records, uint count, out uint read);
}
