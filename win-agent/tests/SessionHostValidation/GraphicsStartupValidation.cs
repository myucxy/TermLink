using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.LocalIpc;
using TermLink.NativeDisplay;
using TermLink.SessionHost;
using TermLink.TerminalCore;

internal static class GraphicsStartupValidation
{
    private const string ModeVariable = "TERMLINK_STARTUP_PROBE_MODE";
    private const string PathVariable = "TERMLINK_STARTUP_PROBE_PATH";
    private const string ChildMarker = "__GRAPHICS_FIRST_OUTPUT__";
    private static string ProbePath => Environment.GetEnvironmentVariable(PathVariable)!;
    private static string Mode => Environment.GetEnvironmentVariable(ModeVariable)!;
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.Validation.exe");

    internal static void Child()
    {
        File.WriteAllText(ProbePath + ".child", "started");
        var output = GetStdHandle(-11);
        Require(GetConsoleMode(output, out var mode) && SetConsoleMode(output, mode | 4), "Child VT output unavailable.");
        Console.Write(Mode == "broker-unsupported" ? "\x1b[999t" :
            Mode == "sixel" ? "\x1bP0;1;0q\"1;1;1;6#1;2;100;0;0#1~\x1b\\" :
            "\x1b_Ga=T,f=32,s=1,v=1,i=1,c=1,r=1,q=2;AQIDBA==\x1b\\");
        if (Mode != "broker-unsupported") Console.Write(ChildMarker);
        Console.Out.Flush();
        Thread.Sleep(Mode is "input-bridge-fault" or "broker-unsupported" ? 10000 : 1000);
        if (Mode == "child-nonzero") Environment.ExitCode = 42;
    }

    internal static void Host(string[] arguments)
    {
        var options = SessionHostOptions.Parse(arguments);
        if (Mode.StartsWith("broker-", StringComparison.Ordinal)) options.EnableBrokerCapture = true;
        using var host = new ConPtySessionHost(options);
        Task injection = null;
        if (Mode is "dispose" or "resume-failure" or "input-bridge-fault") injection = Task.Run(() => {
            Require(SpinWait.SpinUntil(() => Mode == "input-bridge-fault"
                ? File.Exists(ProbePath + ".child") && File.Exists(ProbePath + ".running")
                : File.Exists(ProbePath + ".display"), 8000),
                "Session did not reach the fault injection point.");
            if (Mode == "dispose") host.Dispose();
            else if (Mode == "input-bridge-fault") {
                typeof(ConPtySessionHost).GetMethod("BridgeInput", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(host, new object[] { IntPtr.Zero, false });
                File.WriteAllText(ProbePath + ".injected", "ready");
            }
            else {
                var info = typeof(ConPtySessionHost).GetField("processInfo", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
                Require(CloseHandle((IntPtr)info.GetType().GetField("hThread")!.GetValue(info)!), "Could not inject ResumeThread failure.");
                info.GetType().GetField("hThread")!.SetValue(info, IntPtr.Zero);
                typeof(ConPtySessionHost).GetField("processInfo", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(host, info);
                File.WriteAllText(ProbePath + ".injected", "ready");
            }
        });
        try { Environment.ExitCode = host.Run(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 125; }
        injection?.GetAwaiter().GetResult();
    }

    internal static async Task Display(string pipeName)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try {
            await pipe.ConnectAsync(deadline.Token);
            File.WriteAllText(ProbePath + ".display", "connected");
            if (Mode == "display-exit") return;
            if (Mode is "timeout" or "disconnect" or "dispose") {
                await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumGraphicsFrameBytes, deadline.Token); return;
            }
            if (Mode == "display-failure") {
                await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.HostGeometry, new byte[1], NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
                await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumGraphicsFrameBytes, deadline.Token); return;
            }
            async Task<byte> Request(byte kind, byte[] bytes) {
                await NativeDisplayProtocol.WriteAsync(pipe, kind, bytes, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
                return (await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumGraphicsFrameBytes, deadline.Token)).Kind;
            }
            // A large queued paste must be rejected promptly, leaving this sequential pipe free for geometry.
            Require(await Request(NativeDisplayProtocol.Paste, Encoding.ASCII.GetBytes(new string('x', 256 * 1024))) == NativeDisplayProtocol.Rejected,
                "Pre-geometry paste entered the suspended child's PTY.");
            var resize = new byte[4]; BinaryPrimitives.WriteUInt16LittleEndian(resize, 80); BinaryPrimitives.WriteUInt16LittleEndian(resize.AsSpan(2), 25);
            Require(await Request(NativeDisplayProtocol.Resize, resize) == NativeDisplayProtocol.Accepted, "Bootstrap resize was rejected.");
            if (Mode == "resume-failure") Require(SpinWait.SpinUntil(() => File.Exists(ProbePath + ".injected"), 3000), "Missing resume fault injection.");
            var geometry = Mode == "zero" ? new NativeTerminalHostGeometryV1(0, 0, 0, 0, 0, 0) :
                new NativeTerminalHostGeometryV1(10, 20, 1920, 1080, 800, 500);
            Require(await Request(NativeDisplayProtocol.HostGeometry, geometry.Encode()) == NativeDisplayProtocol.Accepted, "Geometry was rejected.");
            while (!deadline.IsCancellationRequested) {
                await NativeDisplayProtocol.WriteAsync(pipe, NativeDisplayProtocol.Render, new byte[12], NativeDisplayProtocol.MaximumRequestBytes, deadline.Token);
                var result = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumGraphicsFrameBytes, deadline.Token);
                if (result.Kind == NativeDisplayProtocol.FrameV5 && NativeTerminalRenderFrameV5Decoder.Decode(result.Payload).Images.Count > 0)
                    File.WriteAllText(ProbePath + ".image", "rendered");
                await Task.Delay(25, deadline.Token);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }

    internal static async Task Run()
    {
        foreach (var mode in new[] { "kitty", "sixel", "broker-root", "broker-unsupported", "child-nonzero", "broker-missing-root", "zero", "timeout", "withheld-ack", "local-only", "display-exit", "display-failure", "disconnect", "dispose", "resume-failure", "input-bridge-fault" })
            await Scenario(mode);
        Console.WriteLine("Graphics startup: real Kitty/SIXEL children wait for durable applied geometry; queued inputs and failure cases passed.");
    }

    internal static Task RunInputBridgeFailure() => Scenario("input-bridge-fault");

    private static async Task Scenario(string mode)
    {
        var session = Guid.NewGuid(); var token = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(Path.GetTempPath(), "termlink-startup-" + session.ToString("N"));
        var pipeName = "termlink-startup-" + session.ToString("N");
        var positive = mode is "kitty" or "sixel" or "broker-root";
        var brokerRoot = Path.Combine(path + ".history", "broker-captures");
        if (mode is "broker-root" or "broker-unsupported") {
            Directory.CreateDirectory(brokerRoot);
            foreach (var directory in new[] { Path.GetDirectoryName(brokerRoot)!, brokerRoot }) {
                using var identity = WindowsIdentity.GetCurrent();
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).SetAccessControl(security);
            }
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment[ModeVariable] = mode; start.Environment[PathVariable] = path;
        foreach (var value in new[] { "--graphics-startup-host", "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes("\"" + Executable + "\" --graphics-startup-child")),
            "--timeout-ms", positive || mode is "input-bridge-fault" or "broker-unsupported" or "child-nonzero" ? "10000" : "2000", "--diagnostics", "--agent-pipe", pipeName, "--session-id", session.ToString(),
            "--handshake-token", Convert.ToBase64String(token), "--native-display", Executable, "--terminal-state", TerminalSemanticProfile.Graphics.StateContract,
            "--working-directory-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(AppContext.BaseDirectory)) }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        var diagnostics = process.StandardError.ReadToEndAsync(); var outerOutput = process.StandardOutput.ReadToEndAsync();
        var frames = new List<LocalFrame>(); var inputRejected = false; var heldGeometryAck = false;
        var agent = Task.Run(async () => {
            try {
                await pipe.WaitForConnectionAsync(deadline.Token);
                var hello = LocalFrameCodec.Read(pipe);
                Require(hello.Type == LocalFrameType.HostHello && LocalPayloadCodec.ParseTerminalHello(hello.Payload, token, TerminalSemanticProfile.Graphics) == process.Id,
                    "Graphics startup handshake failed.");
                LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.AgentHello, session, 0,
                    LocalPayloadCodec.TerminalHello(Environment.ProcessId, token, TerminalSemanticProfile.Graphics)));
                Require(LocalFrameCodec.Read(pipe).Type == LocalFrameType.Capabilities, "Startup capabilities missing.");
                if (mode is "broker-root" or "broker-unsupported")
                    LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.BrokerCaptureRoot, session, 0,
                        LocalPayloadCodec.BrokerCaptureRoot(brokerRoot)));
                if (mode == "disconnect") { pipe.Dispose(); return; }
                var inputId = Guid.NewGuid();
                LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.RemoteInputV2, session, 1,
                    LocalPayloadCodec.RemoteInput(inputId, 0, Encoding.ASCII.GetBytes(new string('x', 256 * 1024)))));
                using var journal = new FileStream(path + ".journal", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                while (!deadline.IsCancellationRequested) {
                    var frame = LocalFrameCodec.Read(pipe); if (frame == null) break;
                    frames.Add(frame); LocalFrameCodec.Write(journal, frame); journal.Flush(true);
                    if (frame.Type == LocalFrameType.RemoteInputResult) {
                        Require(LocalPayloadCodec.ParseRemoteInputResult(frame.Payload, out var result) == inputId &&
                            result == LocalRemoteInputResult.SessionInputUnavailable, "Early remote input was not rejected.");
                        inputRejected = true;
                    }
                    if (frame.Type == LocalFrameType.HostGeometry) {
                        Require(frames.Any(f => f.Type == LocalFrameType.Resize && f.Sequence < frame.Sequence), "Geometry preceded initial resize.");
                        await Task.Delay(300, deadline.Token);
                        Require(!File.Exists(path + ".child"), "Child ran before the geometry ACK.");
                        heldGeometryAck = true;
                        if (mode == "withheld-ack") { await Task.Delay(10000, deadline.Token); return; }
                        if (mode == "local-only") {
                            LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.RemoteAccessStopped, session, 2, LocalPayloadCodec.RemoteAccessStopped()));
                            continue;
                        }
                    }
                    LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.Ack, session, frame.Sequence, Array.Empty<byte>()));
                    if (mode == "input-bridge-fault" && frame.Type == LocalFrameType.Lifecycle &&
                        LocalPayloadCodec.ParseLifecycle(frame.Payload, out _) == LocalLifecycleState.Running)
                        File.WriteAllText(path + ".running", "ready");
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
        });
        string evidence = "";
        try {
            await process.WaitForExitAsync(deadline.Token);
            pipe.Dispose(); deadline.Cancel();
            await agent;
            evidence = await diagnostics; await outerOutput;
            if (mode == "broker-unsupported") {
                var ended = frames.FirstOrDefault(f => f.Type == LocalFrameType.Lifecycle &&
                    LocalPayloadCodec.ParseLifecycle(f.Payload, out _) == LocalLifecycleState.Ended);
                Require(process.ExitCode == 125 && ended != null &&
                    LocalPayloadCodec.ParseLifecycle(ended.Payload, out var exitCode) == LocalLifecycleState.Ended &&
                    exitCode == 125 && frames.Any(f => f.Type == LocalFrameType.SessionRemoved) &&
                    evidence.Contains("authority_failed=true", StringComparison.Ordinal),
                    "Broker protocol failure did not end with authoritative code 125: " + evidence);
                var diagnostic = new TerminalFailureStore().ReadAuthorityBody(session);
                Require(diagnostic != null && diagnostic.Contains("sequence=<ESC>[999t\n", StringComparison.Ordinal) &&
                    diagnostic.Contains("hex=1B 5B 39 39 39 74\n", StringComparison.Ordinal),
                    "Broker protocol failure did not retain the exact escaped sequence and bytes.");
            }
            else if (mode == "child-nonzero") {
                var ended = frames.FirstOrDefault(f => f.Type == LocalFrameType.Lifecycle &&
                    LocalPayloadCodec.ParseLifecycle(f.Payload, out _) == LocalLifecycleState.Ended);
                Require(process.ExitCode == 42 && ended != null &&
                    LocalPayloadCodec.ParseLifecycle(ended.Payload, out var exitCode) == LocalLifecycleState.Ended &&
                    exitCode == 42 && !evidence.Contains("authority_failed=true", StringComparison.Ordinal),
                    "An ordinary child exit code was overwritten: " + evidence);
            }
            else if (positive) {
                Require(process.ExitCode == 0 && inputRejected && heldGeometryAck, mode + " startup failed: " + evidence);
                var geometry = frames.First(f => f.Type == LocalFrameType.HostGeometry);
                var output = frames.FirstOrDefault(f => f.Type == LocalFrameType.Output && Encoding.UTF8.GetString(f.Payload).Contains(ChildMarker));
                Require(output != null && geometry.Sequence < output.Sequence && File.Exists(path + ".image"), mode + " first image/output did not follow geometry: " + evidence);
                Require(evidence.Contains("authority_failed=false;") && evidence.Contains("output_disconnected=false"),
                    "Authority failed after resume: " + evidence);
            }
            else if (mode == "input-bridge-fault") {
                var running = frames.FindIndex(f => f.Type == LocalFrameType.Lifecycle &&
                    LocalPayloadCodec.ParseLifecycle(f.Payload, out _) == LocalLifecycleState.Running);
                var ended = frames.FindIndex(f => f.Type == LocalFrameType.Lifecycle &&
                    LocalPayloadCodec.ParseLifecycle(f.Payload, out var exit) == LocalLifecycleState.Ended && exit == 125);
                var removed = frames.FindIndex(f => f.Type == LocalFrameType.SessionRemoved);
                Require(process.ExitCode == 125 && File.Exists(path + ".child") && File.Exists(path + ".injected") &&
                    running >= 0 && ended > running && removed > ended &&
                    evidence.Contains("reason=input-failure") && evidence.Contains("input_failure=true"),
                    "BridgeInput fatal fault did not end and remove its live session: " + evidence);
                LocalPayloadCodec.ParseSessionRemoved(frames[removed].Payload);
                Require(frames[running].Sequence < frames[ended].Sequence &&
                    frames[ended].Sequence < frames[removed].Sequence,
                    "BridgeInput terminal frames were not ordered by sequence.");
                Console.WriteLine($"BridgeInput fatal: Running seq={frames[running].Sequence}, " +
                    $"Ended seq={frames[ended].Sequence}, Removed seq={frames[removed].Sequence}, child exit=125.");
            }
            else {
                var expected = mode switch { "broker-missing-root" => "Authenticated Agent did not provide",
                    "display-exit" or "display-failure" => "Native display",
                    "disconnect" => "Agent disconnected", "local-only" => "Remote access stopped", "dispose" => "Session disposed", "resume-failure" => "ResumeThread failed",
                    _ => "Timed out waiting" };
                Require(process.ExitCode == 125 && !File.Exists(path + ".child") && evidence.Contains("graphics startup failed:") && evidence.Contains(expected),
                    mode + " did not fail closed with the expected diagnostic: " + evidence);
            }
            var match = Regex.Match(evidence, "__GRAPHICS_CHILD_SUSPENDED__:pid=(\\d+);");
            Require(mode == "broker-missing-root" ? !match.Success :
                match.Success && !IsAlive(int.Parse(match.Groups[1].Value)),
                "Startup left a child process alive or started it before broker root: " + evidence);
            Console.WriteLine("Graphics startup " + mode + ": passed");
        }
        finally {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            pipe.Dispose(); deadline.Cancel();
            try { await agent; } catch when (evidence.Length == 0) { }
            evidence = await diagnostics;
            var display = Regex.Match(evidence, "__NATIVE_DISPLAY_PID__:(\\d+);");
            if (display.Success) {
                try { using var ownedDisplay = Process.GetProcessById(int.Parse(display.Groups[1].Value)); if (!ownedDisplay.HasExited) ownedDisplay.Kill(); }
                catch (ArgumentException) { }
            }
            foreach (var suffix in new[] { ".child", ".display", ".running", ".injected", ".image", ".journal" }) File.Delete(path + suffix);
            if (mode == "broker-unsupported") File.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TermLink", "TerminalFailures", session.ToString("N") + ".dpapi"));
            if (Directory.Exists(path + ".history")) Directory.Delete(path + ".history", recursive: true);
        }
    }

    private static bool IsAlive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
