using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TermLink.LocalIpc;
using TermLink.SessionHost;

// A disposable outer ConPTY acts as the renderer; the child is the real SessionHost
// with its own real inner ConPTY. No installed client or user session is touched.
internal static class WindowResizeValidation
{
    public static void Run()
    {
        var request = new LocalResizeRequest { Id = Guid.NewGuid(), Columns = 45, Rows = 28,
            LocalInputEpoch = 7, ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10000 };
        var decoded = LocalPayloadCodec.ParseRemoteResize(LocalPayloadCodec.RemoteResize(request));
        Require(decoded.Id == request.Id && decoded.Columns == 45 && decoded.Rows == 28 &&
            decoded.LocalInputEpoch == 7 && !decoded.Restore, "resize IPC round trip");
        request.Columns = 1;
        try { LocalPayloadCodec.RemoteResize(request); throw new Exception("invalid size accepted"); }
        catch (InvalidDataException) { }
        var invalid = new byte[21];
        try { LocalPayloadCodec.ParseResizeResult(invalid); throw new Exception("empty result accepted"); }
        catch (InvalidDataException) { }

        var session = Guid.NewGuid();
        var token = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var name = "termlink-resize-test-" + session.ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var hostPath = Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.exe");
        var child = "powershell.exe -NoLogo -NoProfile -Command \"Start-Sleep -Seconds 40\"";
        var command = "\"" + hostPath + "\" --command-base64 " + Encode(child) +
            " --agent-pipe " + name + " --session-id " + session + " --handshake-token " +
            Convert.ToBase64String(token) + " --timeout-ms 35000 --diagnostics";
        using var outer = new ConPtySessionHost(SessionHostOptions.Parse(new[] { "--command-base64", Encode(command) }));
        var type = typeof(ConPtySessionHost);
        var coord = type.GetNestedType("COORD", BindingFlags.NonPublic);
        var size = Activator.CreateInstance(coord);
        coord.GetField("X").SetValue(size, (short)100);
        coord.GetField("Y").SetValue(size, (short)35);
        type.GetMethod("CreateInnerPseudoConsole", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(outer, new[] { size });
        IntPtr Handle(string field) => (IntPtr)type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(outer);
        using var output = new FileStream(new SafeFileHandle(Handle("terminalOutput"), false), FileAccess.Read);
        var acceptResize = true;
        var transcript = new StringBuilder();
        var requestedSizes = new ConcurrentQueue<string>();
        var reader = Task.Run(() => {
            var buffer = new byte[4096];
            var pending = "";
            try {
                int count;
                while ((count = output.Read(buffer, 0, buffer.Length)) > 0) {
                    var text = Encoding.UTF8.GetString(buffer, 0, count);
                    lock (transcript) transcript.Append(text);
                    pending += text;
                    var matches = Regex.Matches(pending, "\x1b\\[8;([0-9]+);([0-9]+)t");
                    foreach (Match match in matches) {
                        requestedSizes.Enqueue(match.Value);
                        // ConPTY optimistically resizes on CSI 8. Windows Terminal
                        // sends ResetSize when the pane/window cannot honor it.
                        var accepted = Volatile.Read(ref acceptResize);
                        Require(PseudoConsoleApi.Resize(Handle("pseudoConsole"), new PseudoConsoleApi.Size {
                            X = accepted ? short.Parse(match.Groups[2].Value) : (short)100,
                            Y = accepted ? short.Parse(match.Groups[1].Value) : (short)35 }) == 0,
                            "outer ConPTY resize/reset");
                    }
                    if (matches.Count > 0) {
                        var last = matches[matches.Count - 1];
                        pending = pending.Substring(last.Index + last.Length);
                    }
                    if (pending.Length > 128) pending = pending.Substring(pending.Length - 128);
                }
            } catch (IOException) { } catch (ObjectDisposedException) { }
        });
        type.GetMethod("StartChildSuspended", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(outer, null);
        try {
            using var timeout = new CancellationTokenSource(10000);
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            var hello = LocalFrameCodec.Read(pipe);
            Require(hello.Type == LocalFrameType.HostHello && hello.SessionId == session, "host handshake");
            LocalPayloadCodec.ParseHello(hello.Payload, token);
            LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.AgentHello, session, 0,
                LocalPayloadCodec.Hello(Environment.ProcessId, token)));
            var sync = new object();
            ulong sequence = 0;
            void Send(LocalFrameType kind, byte[] payload) {
                lock (sync) LocalFrameCodec.Write(pipe, new LocalFrame(kind, session, ++sequence, payload));
            }
            var results = new ConcurrentDictionary<Guid, LocalResizeResult>();
            var resizes = new ConcurrentQueue<(short, short)>();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => {
                try {
                    LocalFrame frame;
                    while ((frame = LocalFrameCodec.Read(pipe)) != null) {
                        if (frame.Type == LocalFrameType.Capabilities) {
                            Require(LocalPayloadCodec.ParseCapabilities(frame.Payload) && (frame.Payload[1] & 2) != 0, "resize capability");
                            continue;
                        }
                        if (frame.Type == LocalFrameType.ResizeResult) {
                            Require(frame.Sequence == 0, "control reply must not consume journal sequence");
                            var result = LocalPayloadCodec.ParseResizeResult(frame.Payload);
                            results[result.Id] = result;
                            continue;
                        }
                        if (frame.Type == LocalFrameType.Resize) {
                            LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                            resizes.Enqueue((columns, rows));
                        }
                        lock (sync) LocalFrameCodec.Write(pipe,
                            new LocalFrame(LocalFrameType.Ack, session, frame.Sequence, Array.Empty<byte>()));
                        if (frame.Type == LocalFrameType.Lifecycle) ready.TrySetResult();
                    }
                } catch (Exception error) { ready.TrySetException(error); }
            });
            ready.Task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            LocalResizeResult Resize(short columns, short rows, bool restore = false, ulong epoch = 0, bool expired = false) {
                var request = new LocalResizeRequest { Id = Guid.NewGuid(), Columns = columns, Rows = rows,
                    Restore = restore, LocalInputEpoch = epoch,
                    ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expired ? -1000 : 10000) };
                Send(LocalFrameType.ResizeRequest, LocalPayloadCodec.RemoteResize(request));
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 7000) {
                    if (results.TryRemove(request.Id, out var result)) return result;
                    Thread.Sleep(20);
                }
                throw new Exception("resize reply timed out; " + transcript);
            }
            var fit = Resize(45, 28);
            Require(fit.Error == LocalResizeError.OK && fit.Columns == 45 && fit.Rows == 28,
                "phone fit failed: " + fit.Error + "; " + transcript);
            Require(SpinWait.SpinUntil(() => resizes.Contains(((short)45, (short)28)), 1000), "fit not journaled");
            var restored = Resize(0, 0, restore: true);
            Require(restored.Error == LocalResizeError.OK && restored.Columns == 100 && restored.Rows == 35, "desktop restore");
            Require(Resize(0, 0, restore: true).Error == LocalResizeError.NO_DESKTOP_SIZE, "restore must consume its saved size");
            var count = requestedSizes.Count;
            Require(Resize(45, 28, epoch: 1).Error == LocalResizeError.LOCAL_INPUT_PRIORITY, "local priority");
            Require(Resize(45, 28, expired: true).Error == LocalResizeError.RESIZE_EXPIRED, "deadline");
            Require(requestedSizes.Count == count, "rejected request changed outer window");
            Volatile.Write(ref acceptResize, false);
            var refused = Resize(45, 28);
            Require(refused.Error == LocalResizeError.WINDOW_RESIZE_UNSUPPORTED, "refused window request claimed success: " + refused.Error);
            Require(Resize(0, 0, restore: true).Error == LocalResizeError.NO_DESKTOP_SIZE, "failed resize saved a restore point");
            Volatile.Write(ref acceptResize, true);
            Require(Resize(45, 28).Error == LocalResizeError.OK, "second fit");
            Require(PseudoConsoleApi.Resize(Handle("pseudoConsole"), new PseudoConsoleApi.Size { X = 90, Y = 32 }) == 0,
                "local desktop resize");
            Require(SpinWait.SpinUntil(() => resizes.Contains(((short)90, (short)32)), 2000), "local resize not journaled");
            Require(Resize(0, 0, restore: true).Error == LocalResizeError.NO_DESKTOP_SIZE, "local resize must supersede restore point");
            Console.WriteLine("Window resize: real nested ConPTY fit, restore, local priority, expiry and refusal passed.");
        } finally {
            // Terminate only the disposable job we created, while continuing to drain output.
            type.GetMethod("TerminateProcessTree", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(outer, new object[] { (uint)0 });
        }
    }
    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
