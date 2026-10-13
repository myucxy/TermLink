using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using System.Windows.Automation;
using TermLink.LocalIpc;
using TermLink.SessionHost;

internal static class NativeWindowValidation
{
    internal static void Child(string themeRestoreSignalName = null)
    {
        var output = GetStdHandle(-11); GetConsoleMode(output, out var mode); SetConsoleMode(output, mode | 4);
        Console.Write("\x1b]2;TermLink Native Probe\x1b\\\x1b[2J\x1b[HNative ConPTY window\r\n\x1b#6Double width\r\n\x1b#3HEIGHT\r\n\x1b#4HEIGHT\r\n\x1b#5Input: ");
        Console.Out.Flush();
        var line = Console.ReadLine();
        Console.Write("\r\n__NATIVE_ECHO__:" + line + ":END\r\n"); Console.Out.Flush();
        var input = GetStdHandle(-10);
        Require(SetConsoleMode(input, 0), "Could not read terminal reports as raw input.");
        EventWaitHandle themeRestoreSignal = null;
        if (!string.IsNullOrWhiteSpace(themeRestoreSignalName))
        {
            try { themeRestoreSignal = EventWaitHandle.OpenExisting(themeRestoreSignalName); }
            catch (Exception error)
            {
                throw new InvalidOperationException("Could not open the isolated theme restore signal.", error);
            }
        }
        using (themeRestoreSignal)
        {
        Console.Write("\x1b[14t\x1b[16t\x1b[18t\x1b[19t"); Console.Out.Flush();
        using var stream = Console.OpenStandardInput();
        var replies = new StringBuilder(); var completed = 0;
        while (replies.Length < 256 && completed < 4) {
            var value = stream.ReadByte(); if (value < 0) break;
            replies.Append((char)value); if (value == 't') completed++;
        }
        var reports = Regex.Matches(replies.ToString(), "\u001b\\[(4|6|8|9);([0-9]+);([0-9]+)t");
        Require(reports.Count == 4, "Selected-host queries did not reach the real child.");
        var values = new System.Collections.Generic.Dictionary<int, (int Height, int Width)>();
        foreach (Match report in reports) values.Add(int.Parse(report.Groups[1].Value),
            (int.Parse(report.Groups[2].Value), int.Parse(report.Groups[3].Value)));
        Require(values[4].Height == values[6].Height * values[8].Height &&
            values[4].Width == values[6].Width * values[8].Width && values[9].Height > 0 && values[9].Width > 0,
            "Actual PTY geometry reports are inconsistent.");
        Console.Write("\r\n__GEOMETRY_CHILD_OK__\r\n"); Console.Out.Flush();
        Console.Write("\x1b[?996n"); Console.Out.Flush();
        var themeReply = ReadReply(input, 'n');
        Require(themeReply is "\x1b[?997;1n" or "\x1b[?997;2n", "Real child did not receive the selected host theme.");
        var originalScheme = themeReply[7];
        Console.Write("\x1b[?2031$p"); Console.Out.Flush();
        Require(ReadReply(input, 'y') == "\x1b[?2031;2$y", "Theme subscription was enabled before the child opted in.");
        Console.Write("\x1b[?2031h"); Console.Out.Flush();
        Require(ReadReply(input, 'n') == themeReply, "Subscribing did not immediately report the selected host theme.");
        Console.Write("\r\n__THEME_SUBSCRIBED__:" + originalScheme + ";\r\n"); Console.Out.Flush();
        var switched = ReadReply(input, 'n');
        Require(switched == "\x1b[?997;" + (originalScheme == '1' ? '2' : '1') + "n",
            "A real window theme change did not produce one ordered PTY report.");
        Console.Write("\r\n__THEME_SWITCHED__\r\n"); Console.Out.Flush();
        RequireNoReply(input, 1000, "Repeating the same host theme produced another PTY reply.");
        Console.Write("\x1b[?2031$p"); Console.Out.Flush();
        Require(ReadReply(input, 'y') == "\x1b[?2031;1$y", "Theme subscription was lost after switching.");
        Console.Write("\x1b[?2031l\x1b[?2031$p"); Console.Out.Flush();
        Require(ReadReply(input, 'y') == "\x1b[?2031;2$y", "Theme subscription did not turn off.");
        Console.Write("\r\n__THEME_UNSUBSCRIBED__\r\n"); Console.Out.Flush();
        if (themeRestoreSignal != null)
        {
            var restoreDeadline = Stopwatch.StartNew();
            var restored = false;
            while (!restored && restoreDeadline.ElapsedMilliseconds < 6000)
            {
                restored = themeRestoreSignal.WaitOne(100);
                if (!restored)
                    RequireNoReply(input, 100, "An unsubscribed child received an unsolicited theme reply before the restore signal.");
            }
            Require(restored, "The parent did not confirm the restored HostTheme frame after the unsubscribe quiet interval.");
        }
        else
        {
            RequireNoReply(input, 1000, "An unsubscribed child received an unsolicited theme reply.");
        }
        Console.Write("\x1b[?996n"); Console.Out.Flush();
        Require(ReadReply(input, 'n') == themeReply, "Unsubscribed theme query did not see the restored window theme.");
        Console.Write("\r\n__THEME_CHILD_OK__\r\n"); Console.Out.Flush();
        Console.Write("\x1b[96$|\x1b[25*|"); Console.Out.Flush();
        var resizeDeadline = Stopwatch.StartNew();
        var resizeConfirmed = false;
        while (resizeDeadline.ElapsedMilliseconds < 6000)
        {
            Console.Write("\x1b[18t"); Console.Out.Flush();
            var reply = new StringBuilder();
            while (reply.Length < 64) { var value = stream.ReadByte(); if (value < 0) break; reply.Append((char)value); if (value == 't') break; }
            if (reply.ToString() == "\x1b[8;25;96t") { resizeConfirmed = true; break; }
            Thread.Sleep(30);
        }
        Require(resizeConfirmed, "Program resize did not reach the physical host and ordered core state.");
        Console.Write("\x1b[1$|\r\n__RESIZE_CHILD_OK__\r\n"); Console.Out.Flush();
        Thread.Sleep(500);
        }
    }

    internal static void Run(string desktopExecutable) => RunCore(desktopExecutable, false);

    internal static void RunProtocolReply(string desktopExecutable) => RunCore(desktopExecutable, true);

    private static void RunCore(string desktopExecutable, bool protocolReplies)
    {
        if (!File.Exists(desktopExecutable)) throw new FileNotFoundException(desktopExecutable);
        if (protocolReplies && !Path.IsPathFullyQualified(desktopExecutable))
            throw new ArgumentException("Protocol reply window probe requires an absolute Desktop executable path.");
        var session = Guid.NewGuid(); var token = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var pipeName = "termlink-native-window-test-" + session.ToString("N");
        var protocolDirectory = protocolReplies
            ? Path.Combine(Path.GetTempPath(), "termlink-protocol-window-" + session.ToString("N"))
            : null;
        if (protocolDirectory != null) Directory.CreateDirectory(protocolDirectory);
        EventWaitHandle themeRestoreSignal = null;
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var validationExecutable = Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.Validation.exe");
        var child = protocolReplies
            ? "\"" + validationExecutable + "\" --protocol-reply-relay-child \"" + protocolDirectory + "\""
            : CreateNativeWindowChildCommand(validationExecutable, session, out themeRestoreSignal);
        var command = "\"" + Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.exe") + "\" --command-base64 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(child)) +
            " --timeout-ms 40000 --diagnostics --agent-pipe " + pipeName + " --session-id " + session + " --handshake-token " + Convert.ToBase64String(token) + " --native-display \"" + Path.GetFullPath(desktopExecutable) + "\"";
        using var outer = new ConPtySessionHost(SessionHostOptions.Parse(new[] { "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(command)) }));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ConPtySessionHost); var coord = type.GetNestedType("COORD", flags)!;
        var size = Activator.CreateInstance(coord)!; coord.GetField("X")!.SetValue(size, (short)80); coord.GetField("Y")!.SetValue(size, (short)25);
        type.GetMethod("CreateInnerPseudoConsole", flags)!.Invoke(outer, new[] { size });
        var outputHandle = (IntPtr)type.GetField("terminalOutput", flags)!.GetValue(outer)!;
        using var output = new FileStream(new SafeFileHandle(outputHandle, false), FileAccess.Read);
        var diagnostics = new StringBuilder(); var journal = new StringBuilder();
        var writeSync = new object(); ulong commandSequence = 0;
        var resizeResults = new ConcurrentDictionary<Guid, LocalResizeResult>();
        var resizes = new ConcurrentQueue<(short Columns, short Rows)>();
        var protocolResults = new ConcurrentQueue<LocalHostResizeResult>();
        var geometries = new ConcurrentQueue<TermLink.TerminalCore.NativeTerminalHostGeometryV1>();
        var themes = new ConcurrentQueue<(ulong Sequence, byte Scheme)>();
        var themeAcks = new ConcurrentQueue<ulong>();
        var reader = Task.Run(() => { var buffer = new byte[4096]; try { int count; while ((count = output.Read(buffer, 0, buffer.Length)) > 0) lock (diagnostics) diagnostics.Append(Encoding.UTF8.GetString(buffer, 0, count)); } catch (IOException) { } });
        var agent = Task.Run(async () => {
            using var timeout = new CancellationTokenSource(10000); await pipe.WaitForConnectionAsync(timeout.Token);
            var hello = LocalFrameCodec.Read(pipe); LocalPayloadCodec.ParseTerminalHello(hello.Payload, token);
            LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.AgentHello, session, 0, LocalPayloadCodec.TerminalHello(Environment.ProcessId, token)));
            LocalFrame frame;
            while ((frame = LocalFrameCodec.Read(pipe)) != null) {
                if (frame.Type == LocalFrameType.Capabilities) continue;
                if (frame.Type == LocalFrameType.ResizeResult) {
                    var result = LocalPayloadCodec.ParseResizeResult(frame.Payload); resizeResults[result.Id] = result; continue;
                }
                if (frame.Type == LocalFrameType.Resize) {
                    LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows); resizes.Enqueue((columns, rows));
                }
                if (frame.Type == LocalFrameType.HostGeometry)
                    geometries.Enqueue(TermLink.TerminalCore.NativeTerminalHostGeometryV1.Decode(frame.Payload));
                if (frame.Type == LocalFrameType.HostTheme)
                    themes.Enqueue((frame.Sequence, TermLink.TerminalCore.NativeTerminalHostThemeV1.Decode(frame.Payload).Encode()[4]));
                if (frame.Type == LocalFrameType.HostResizeResult)
                {
                    var result = LocalPayloadCodec.ParseHostResizeResult(frame.Payload);
                    Require(result.SourceSequence < frame.Sequence, "Resize result was not ordered after its source request.");
                    protocolResults.Enqueue(result);
                }
                if (frame.Type == LocalFrameType.Output) lock (journal) journal.Append(Encoding.UTF8.GetString(frame.Payload));
                lock (writeSync) LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.Ack, session, frame.Sequence, Array.Empty<byte>()));
                if (frame.Type == LocalFrameType.HostTheme) themeAcks.Enqueue(frame.Sequence);
            }
        });
        Process display = null;
        try
        {
            type.GetMethod("StartChildSuspended", flags)!.Invoke(outer, null);
            var pid = 0;
            Require(SpinWait.SpinUntil(() => { lock (diagnostics) { var match = Regex.Match(diagnostics.ToString(), "__NATIVE_DISPLAY_PID__:(\\d+);"); if (match.Success) pid = int.Parse(match.Groups[1].Value); return pid > 0 || agent.IsFaulted; } }, 12000) && pid > 0, "Display did not launch: " + diagnostics);
            display = Process.GetProcessById(pid);
            Require(SpinWait.SpinUntil(() => { display.Refresh(); return display.MainWindowHandle != IntPtr.Zero || display.HasExited; }, 8000) && !display.HasExited, "Display window did not appear.");
            Thread.Sleep(1500);
            Require(resizes.TryPeek(out _), "Native grid did not publish dimensions.");
            Require(themes.TryPeek(out _), "Native display did not publish its actual theme.");
            Require(geometries.TryPeek(out var measured) && measured.CellWidth > 0 && measured.CellHeight > 0 &&
                measured.DisplayWidth > 0 && measured.DisplayHeight > 0 && measured.WindowWidth > 0 && measured.WindowHeight > 0,
                "Selected native display did not journal actual cell, screen and window geometry.");
            if (protocolReplies)
            {
                RunProtocolReplyStages(protocolDirectory!, journal, geometries, resizes, measured);
                return;
            }
            var before = System.Linq.Enumerable.Last(resizes);
            LocalResizeResult Resize(short columns, short rows, bool restore = false, ulong epoch = 0, bool expired = false) {
                var request = new LocalResizeRequest { Id = Guid.NewGuid(), Columns = columns, Rows = rows, Restore = restore, LocalInputEpoch = epoch,
                    ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expired ? -1000 : 6000) };
                lock (writeSync) LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.ResizeRequest, session, ++commandSequence, LocalPayloadCodec.RemoteResize(request)));
                Require(SpinWait.SpinUntil(() => resizeResults.ContainsKey(request.Id), 6000), "Native fit result timed out: " + diagnostics);
                return resizeResults[request.Id];
            }
            var fit = Resize(45, 20);
            Require(fit.Error == LocalResizeError.OK && fit.Columns == 45 && fit.Rows == 20, "Native phone fit failed: " + fit.Error);
            Require(System.Linq.Enumerable.Contains(resizes, ((short)45, (short)20)), "Native phone fit was not journaled.");
            Require(SpinWait.SpinUntil(() => System.Linq.Enumerable.Any(geometries, g => g.WindowWidth != measured.WindowWidth), 3000),
                "Native phone fit did not refresh physical window geometry.");
            var restored = Resize(0, 0, restore: true);
            Require(restored.Error == LocalResizeError.OK && (restored.Columns, restored.Rows) == before, "Native desktop restore failed: " + restored.Error);
            Require(Resize(0, 0, restore: true).Error == LocalResizeError.NO_DESKTOP_SIZE, "Native desktop restore did not consume saved dimensions.");
            Require(Resize(45, 20, epoch: 1).Error == LocalResizeError.LOCAL_INPUT_PRIORITY, "Native fit ignored input epoch.");
            Require(Resize(45, 20, expired: true).Error == LocalResizeError.RESIZE_EXPIRED, "Native fit ignored expiration.");
            Require(Resize(512, 512).Error == LocalResizeError.WINDOW_RESIZE_UNSUPPORTED, "Native fit claimed impossible screen dimensions.");
            Require(Resize(0, 0, restore: true).Error == LocalResizeError.NO_DESKTOP_SIZE, "Refused fit retained a restore point.");
            var narrowFit = Resize(32, 20);
            Require(narrowFit.Error == LocalResizeError.OK && narrowFit.Columns == 32 && narrowFit.Rows == 20,
                "The bounded frozen-history fixture cannot use a real 32-column native window.");
            var narrowRestored = Resize(0, 0, restore: true);
            Require(narrowRestored.Error == LocalResizeError.OK &&
                (narrowRestored.Columns, narrowRestored.Rows) == before,
                "The narrow frozen-history fixture did not restore the desktop grid.");
            var window = display.MainWindowHandle;
            // Target only this isolated test window; do not send global keyboard input.
            foreach (var character in "abc123") Send(window, 0x102, character, 1);
            Send(window, 0x100, 13, 0x001c0001);
            Send(window, 0x101, 13, unchecked((int)0xc01c0001));
            Require(SpinWait.SpinUntil(() => { lock (journal) return journal.ToString().Contains("__NATIVE_ECHO__:abc123:END", StringComparison.Ordinal); }, 8000), "Native window input/output failed; journal=" + journal + "; host=" + diagnostics);
            Require(SpinWait.SpinUntil(() => { lock (journal) return journal.ToString().Contains("__GEOMETRY_CHILD_OK__", StringComparison.Ordinal); }, 8000), "Real child geometry query/reply failed; journal=" + journal + "; host=" + diagnostics);
            var baselineScheme = 0;
            Require(SpinWait.SpinUntil(() => { lock (journal) {
                var match = Regex.Match(journal.ToString(), "__THEME_SUBSCRIBED__:(1|2);");
                if (match.Success) baselineScheme = int.Parse(match.Groups[1].Value);
                return baselineScheme != 0;
            } }, 8000), "Real child theme subscription failed; journal=" + journal + "; host=" + diagnostics);
            var themeBefore = themes.ToArray();
            Require(themeBefore.Length > 0 && themeBefore[^1].Scheme == baselineScheme,
                "The child theme query disagreed with the actual native host theme event.");
            var oppositeScheme = baselineScheme == 1 ? 2 : 1;
            string ThemeEvents() => string.Join(",", themes.ToArray().Select(item => $"{item.Sequence}:{item.Scheme}"));
            var themeBoundsPath = Path.Combine(Path.GetTempPath(), "termlink-native-theme-" + session.ToString("N") + ".txt");
            File.WriteAllText(themeBoundsPath, "Isolated native display PID=" + display.Id + Environment.NewLine);
            SelectWindowTheme(window, display.Id, oppositeScheme, themeBoundsPath);
            var themeSwitched = SpinWait.SpinUntil(() => { lock (journal) return journal.ToString().Contains("__THEME_SWITCHED__", StringComparison.Ordinal); }, 8000);
            if (!themeSwitched)
            {
                // A single ExchangeAsync is bounded at 10 seconds.  The first
                // eight-second assertion can therefore expire while the
                // desktop is still completing that exchange.  Keep the
                // assertion strict, but give the in-flight request a bounded
                // four-second observation window before declaring the probe
                // failed.  This distinguishes a delayed HostTheme frame from
                // a stopped display or agent pipe without hiding a failure.
                var delayedThemeWait = Stopwatch.StartNew();
                while (delayedThemeWait.ElapsedMilliseconds < 4000 && !themeSwitched)
                {
                    try
                    {
                        display.Refresh();
                        if (display.HasExited) break;
                    }
                    catch (InvalidOperationException) { break; }
                    lock (journal) themeSwitched = journal.ToString().Contains("__THEME_SWITCHED__", StringComparison.Ordinal);
                    if (!themeSwitched) Thread.Sleep(100);
                }
                string DisplayState()
                {
                    try
                    {
                        display.Refresh();
                        return "display_exited=" + display.HasExited +
                            (display.HasExited ? ";display_exit_code=" + display.ExitCode : "");
                    }
                    catch (Exception error)
                    {
                        return "display_state_error=" + error.GetType().Name + ":" +
                            error.Message.Replace('\r', ' ').Replace('\n', ' ');
                    }
                }
                var agentState = agent.IsFaulted
                    ? "agent_faulted=true;agent_error=" + (agent.Exception?.GetBaseException().Message ?? "unknown").Replace('\r', ' ').Replace('\n', ' ')
                    : "agent_faulted=false;agent_completed=" + agent.IsCompleted;
                File.AppendAllText(themeBoundsPath, "delayed_theme_wait_ms=" + delayedThemeWait.ElapsedMilliseconds + ";" +
                    DisplayState() + ";" + agentState + Environment.NewLine);
            }
            if (!themeSwitched) DumpAutomationWindowState(window, themeBoundsPath);
            Require(themeSwitched,
                "Real child did not receive the subscribed theme change; bounds=" + themeBoundsPath + "; host_theme_count=" + themes.Count +
                "; host_themes=" + ThemeEvents() + "; journal=" + journal + "; host=" + diagnostics);
            // Act while the child is measuring the quiet interval after its first report.
            SelectWindowTheme(window, display.Id, oppositeScheme, themeBoundsPath);
            Require(SpinWait.SpinUntil(() => themes.Count == themeBefore.Length + 1, 2000),
                "The native display did not publish exactly one changed theme.");
            var changedThemes = themes.ToArray();
            Require(changedThemes[^1].Scheme == oppositeScheme && changedThemes[^1].Sequence > themeBefore[^1].Sequence,
                "Changed theme was not ordered after the original native host theme.");
            Require(SpinWait.SpinUntil(() => { lock (journal) return journal.ToString().Contains("__THEME_UNSUBSCRIBED__", StringComparison.Ordinal); }, 8000),
                "Repeated theme emitted an extra report or subscription could not be disabled; journal=" + journal + "; host=" + diagnostics);
            Require(themes.Count == themeBefore.Length + 1, "Repeating an unchanged window theme republished HostTheme.");
            SelectWindowTheme(window, display.Id, baselineScheme, themeBoundsPath);
            Require(SpinWait.SpinUntil(() => {
                var final = themes.ToArray();
                return final.Length == themeBefore.Length + 2 &&
                    final[^1].Scheme == baselineScheme && final[^1].Sequence > changedThemes[^1].Sequence &&
                    themeAcks.Contains(final[^1].Sequence);
            }, 5000), "Restored HostTheme was not durably acknowledged before the child query; host_theme_count=" + themes.Count +
                ";host_themes=" + ThemeEvents() + ";acked=" + string.Join(",", themeAcks));
            if (themeRestoreSignal != null) themeRestoreSignal.Set();
            Require(SpinWait.SpinUntil(() => { lock (journal) return journal.ToString().Contains("__THEME_CHILD_OK__", StringComparison.Ordinal); }, 8000),
                "Unsubscribed theme change or final query failed; journal=" + journal + "; host=" + diagnostics);
            var finalThemes = themes.ToArray();
            Require(finalThemes.Length == themeBefore.Length + 2 && finalThemes[^1].Scheme == baselineScheme &&
                finalThemes[^1].Sequence > changedThemes[^1].Sequence,
                "Restored native window theme was not published exactly once and in order.");
            Require(SpinWait.SpinUntil(() => { lock (journal) return journal.ToString().Contains("__RESIZE_CHILD_OK__", StringComparison.Ordinal); }, 8000), "Real child resize query/reply failed; journal=" + journal + "; host=" + diagnostics);
            Require(SpinWait.SpinUntil(() => protocolResults.Count >= 3, 4000), "Host resize results were not durably journaled.");
            var results = protocolResults.ToArray();
            Require(results[0].Error == LocalResizeError.OK && results[0].ActualColumns == 96 &&
                results[1].Error == LocalResizeError.OK && results[1].ActualColumns == 96 && results[1].ActualRows == 25 &&
                results[2].Error == LocalResizeError.WINDOW_RESIZE_UNSUPPORTED && results[2].RequestedColumns == 1,
                "Program requests lost ordering, unchanged axes, or accurate rejection results.");
            Require(SpinWait.SpinUntil(() => { lock (diagnostics) return diagnostics.ToString().Contains("authority_failed=false;input_failure=false;output_disconnected=false", StringComparison.Ordinal); }, 5000), "Native host did not close cleanly: " + diagnostics);
            Console.WriteLine("Native window: real child → bundled ConPTY → authority → Avalonia; measured geometry, selected-window theme query/subscribe/change/unsubscribe, text/Enter, phone fit/restore, ordered program column/row resizing and refusal results passed.");
        }
        finally
        {
            if (display != null) { if (!display.HasExited) { display.CloseMainWindow(); if (!display.WaitForExit(3000)) display.Kill(); } display.Dispose(); }
            themeRestoreSignal?.Dispose();
            type.GetMethod("TerminateProcessTree", flags)!.Invoke(outer, new object[] { (uint)0 }); outer.Dispose(); pipe.Dispose();
            try { Task.WhenAll(reader, agent).Wait(3000); } catch (AggregateException) { }
        }
    }

    private static string CreateNativeWindowChildCommand(string validationExecutable, Guid session,
        out EventWaitHandle restoreSignal)
    {
        var name = "TermLink-NativeThemeRestore-" + session.ToString("N");
        restoreSignal = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        return "\"" + validationExecutable + "\" --native-window-child \"" + name + "\"";
    }

    private static void RunProtocolReplyStages(
        string workingDirectory,
        StringBuilder journal,
        ConcurrentQueue<TermLink.TerminalCore.NativeTerminalHostGeometryV1> geometries,
        ConcurrentQueue<(short Columns, short Rows)> resizes,
        TermLink.TerminalCore.NativeTerminalHostGeometryV1 measured)
    {
        WaitJournalMarker(journal, "__PROTOCOL_REPLY_RELAY_READY__", 10000);
        var phases = new[] { "queries", "state" };
        foreach (var phase in phases)
        {
            var trigger = Path.Combine(workingDirectory, "protocol-replies.trigger");
            File.WriteAllText(trigger, phase, new UTF8Encoding(false));
            WaitJournalMarker(journal, "__PROTOCOL_REPLY_RELAY_" + phase.ToUpperInvariant() + "_DONE__", 15000);
            var result = Path.Combine(workingDirectory, "protocol-replies-" + phase + ".json");
            var resultLength = File.Exists(result) ? new FileInfo(result).Length : 0;
            Require(resultLength > 0 && resultLength <= 256 * 1024,
                "Protocol reply stage did not produce a bounded raw result: " + phase);
        }
        var grid = resizes.ToArray().LastOrDefault();
        Require(grid.Columns > 0 && grid.Rows > 0, "Protocol reply authority did not publish terminal grid dimensions.");
        var evidencePath = Path.Combine(workingDirectory, "protocol-window-geometry.json");
        var evidence = new
        {
            schema_version = 1,
            status = "passed",
            terminal_columns = grid.Columns,
            terminal_rows = grid.Rows,
            host_geometry = new
            {
                cell_width = measured.CellWidth,
                cell_height = measured.CellHeight,
                display_width = measured.DisplayWidth,
                display_height = measured.DisplayHeight,
                window_width = measured.WindowWidth,
                window_height = measured.WindowHeight
            },
            geometry_updates = geometries.Count,
            queries = "protocol-replies-queries.json",
            state = "protocol-replies-state.json",
            host_exit_code = (int?)null,
            cleanup_expected_exit_code = 0
        };
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence), Encoding.UTF8);
        Console.WriteLine("__PROTOCOL_REPLY_WINDOW_EVIDENCE__:" + evidencePath);
        File.WriteAllText(Path.Combine(workingDirectory, "protocol-replies.trigger"), "exit", new UTF8Encoding(false));
    }

    private static void WaitJournalMarker(StringBuilder journal, string marker, int timeoutMilliseconds)
    {
        Require(SpinWait.SpinUntil(() =>
        {
            lock (journal) return journal.ToString().Contains(marker, StringComparison.Ordinal);
        }, timeoutMilliseconds), "Protocol reply marker timed out: " + marker);
    }

    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string ReadReply(IntPtr input, char last)
    {
        var reply = new StringBuilder();
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 8000 && reply.Length < 32)
        {
            var wait = WaitForSingleObject(input, 50);
            if (wait == 258) continue;
            Require(wait == 0, "Waiting for a real child theme reply failed.");
            var byteBuffer = new byte[1];
            Require(ReadFile(input, byteBuffer, 1, out var read, IntPtr.Zero) && read == 1,
                "Reading a real child theme reply failed.");
            reply.Append((char)byteBuffer[0]);
            if (byteBuffer[0] == last) return reply.ToString();
        }
        throw new InvalidOperationException("Real child theme reply timed out or exceeded its byte limit: " + reply);
    }

    private static void RequireNoReply(IntPtr input, uint milliseconds, string error)
    {
        Require(Marshal.SizeOf<ConsoleInputRecord>() == 20, "Unexpected Windows console input record layout.");
        var deadline = Stopwatch.StartNew();
        var unexpected = new StringBuilder();
        while (deadline.ElapsedMilliseconds < milliseconds)
        {
            var remaining = (uint)(milliseconds - deadline.ElapsedMilliseconds);
            var wait = WaitForSingleObject(input, remaining);
            if (wait == 258) break;
            Require(wait == 0, error + " Wait result: " + wait);
            // A console input handle can signal for mouse/focus or key-up records.
            // Only actual character input is a PTY reply.
            Require(ReadConsoleInput(input, out var record, 1, out var read) && read == 1,
                "Could not inspect real ConPTY input while checking theme silence.");
            if (record.EventType == 1 && record.KeyEvent.KeyDown != 0 && record.KeyEvent.Character != 0)
                unexpected.Append((char)record.KeyEvent.Character);
        }
        Require(unexpected.Length == 0, error + " Received UTF-16 code units: " +
            string.Join(" ", unexpected.ToString().Select(character => ((int)character).ToString("X4"))));
    }
    private static void SelectWindowTheme(IntPtr window, int expectedPid, int scheme, string boundsPath)
    {
        // Keep Win32 client, screen and hit-test coordinates in one physical-pixel space.
        // The validation executable itself may otherwise be DPI-unaware.
        var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Require(previousDpiContext != IntPtr.Zero, "Could not scope the native theme test to per-monitor DPI awareness.");
        try { SelectWindowThemeDpiAware(window, expectedPid, scheme, boundsPath); }
        finally {
            Require(SetThreadDpiAwarenessContext(previousDpiContext) != IntPtr.Zero,
                "Could not restore the native theme test thread DPI context.");
        }
    }

    internal static void SelectOwnedDisplayTheme(string hostPidText, string hostExecutable,
        string desktopExecutable, string schemeText, string diagnosticPath)
    {
        Require(int.TryParse(hostPidText, out var hostPid) && hostPid > 0, "Invalid selected SessionHost PID.");
        Require(Path.IsPathFullyQualified(diagnosticPath) && Directory.Exists(Path.GetDirectoryName(diagnosticPath)),
            "Selected theme diagnostic path must have an existing absolute parent directory.");
        Require(schemeText is "scheme1" or "scheme2", "Selected theme must be scheme1 or scheme2.");
        var owned = VerifyOwnedDisplay(hostPid, hostExecutable, desktopExecutable);
        SelectWindowTheme(owned.Window, owned.DisplayPid, schemeText == "scheme1" ? 1 : 2, diagnosticPath);
    }

    internal readonly record struct OwnedDisplayIdentity(int HostPid, string HostPath, long HostStartUtcTicks,
        int DisplayPid, string DisplayPath, long DisplayStartUtcTicks, IntPtr Window);

    internal static OwnedDisplayIdentity VerifyOwnedDisplay(int hostPid, string hostExecutable,
        string desktopExecutable)
    {
        Require(hostPid > 0, "Invalid selected SessionHost PID.");
        Require(Path.IsPathFullyQualified(hostExecutable) && File.Exists(hostExecutable),
            "Selected SessionHost executable must be an existing absolute file.");
        Require(Path.IsPathFullyQualified(desktopExecutable) && File.Exists(desktopExecutable),
            "Selected Desktop executable must be an existing absolute file.");
        var expectedHost = Path.GetFullPath(hostExecutable);
        Require(string.Equals(Path.GetFileName(expectedHost), "TermLink.SessionHost.exe",
            StringComparison.OrdinalIgnoreCase), "Selected host path is not a SessionHost executable.");
        var expectedDesktop = Path.GetFullPath(desktopExecutable);
        Require(string.Equals(Path.GetFileName(expectedDesktop), "TermLink.exe", StringComparison.OrdinalIgnoreCase),
            "Selected display path is not the TermLink Desktop executable.");
        using var host = Process.GetProcessById(hostPid);
        Require(!host.HasExited && string.Equals(Path.GetFullPath(host.MainModule!.FileName), expectedHost,
            StringComparison.OrdinalIgnoreCase), "PID does not identify the requested SessionHost executable.");
        var children = DirectChildren(hostPid).Where(child => string.Equals(
            child.Executable, Path.GetFileName(expectedDesktop), StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(children.Length == 1, "SessionHost must own exactly one direct Desktop child.");
        using var display = Process.GetProcessById(children[0].Pid);
        Require(!display.HasExited && string.Equals(Path.GetFullPath(display.MainModule!.FileName), expectedDesktop,
            StringComparison.OrdinalIgnoreCase), "SessionHost Desktop child path does not match the requested executable.");
        Require(!host.HasExited && !display.HasExited && display.StartTime >= host.StartTime,
            "Selected Desktop is not a live child started by this SessionHost.");
        display.Refresh();
        var window = display.MainWindowHandle;
        Require(window != IntPtr.Zero && GetWindowThreadProcessId(window, out var windowPid) != 0 &&
            windowPid == display.Id, "Selected Desktop HWND does not belong to its verified child process.");
        return new OwnedDisplayIdentity(hostPid, expectedHost, host.StartTime.ToUniversalTime().Ticks,
            display.Id, expectedDesktop, display.StartTime.ToUniversalTime().Ticks, window);
    }

    private static (int Pid, string Executable)[] DirectChildren(int parentPid)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        Require(snapshot != new IntPtr(-1), "Could not enumerate the selected SessionHost's direct children.");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            var children = new System.Collections.Generic.List<(int Pid, string Executable)>();
            if (Process32First(snapshot, ref entry))
            {
                do { if (entry.ParentProcessId == (uint)parentPid) children.Add(((int)entry.ProcessId, entry.Executable)); }
                while (Process32Next(snapshot, ref entry));
            }
            return children.ToArray();
        }
        finally { CloseHandle(snapshot); }
    }

    private static void SelectWindowThemeDpiAware(IntPtr window, int expectedPid, int scheme, string boundsPath)
    {
        Require(GetWindowThreadProcessId(window, out var pid) != 0 && pid == expectedPid,
            "Theme test did not target its isolated native display process.");
        Require(scheme is 1 or 2, "Invalid selected-window theme test scheme.");
        var dpi = GetDpiForWindow(window);
        Require(dpi is >= 96 and <= 768, "Invalid isolated native display DPI.");
        Require(GetClientRect(window, out var client),
            "Could not measure isolated native display client coordinates.");
        // Keep this coarse size guard, but never infer control positions from the
        // production layout. Avalonia may change wrapping, font metrics, or menu
        // visibility while the native window is being measured. The actual Button
        // rectangles below come from the Windows UI Automation tree.
        var scale = dpi / 96.0;
        Require(client.Right - client.Left >= (int)Math.Ceiling(180 * scale) &&
            client.Bottom - client.Top >= (int)Math.Ceiling(24 * scale),
            "Isolated native display is too small for the theme controls.");
        Require(GetWindowRect(window, out var initialRect), "Could not measure isolated native display bounds.");
        File.AppendAllText(boundsPath, $"scheme={scheme};client={client.Right - client.Left}x{client.Bottom - client.Top};" +
            $"dpi={dpi};window={initialRect.Left},{initialRect.Top},{initialRect.Right},{initialRect.Bottom};" +
            $"foreground_before={GetForegroundWindow()}{Environment.NewLine}");
        var previousWindow = GetForegroundWindow();
        Require(GetCursorPos(out var previousCursor), "Could not preserve the cursor before native theme UI input.");
        var wasTopmost = (GetWindowLong(window, -20) & 0x00000008) != 0;
        void Click(AutomationButtonBounds button, bool requireForeground)
        {
            var point = button.Center;
            var hitWindow = WindowFromPoint(point);
            var hitThread = GetWindowThreadProcessId(hitWindow, out var hitPid);
            GetWindowRect(hitWindow, out var hitRect);
            File.AppendAllText(boundsPath, $"target={window};click={point.X},{point.Y};hit={hitWindow};" +
                $"hit_pid={hitPid};hit_bounds={hitRect.Left},{hitRect.Top},{hitRect.Right},{hitRect.Bottom};" +
                $"foreground={GetForegroundWindow()};uia_label={button.Name};uia_bounds={button.Left},{button.Top},{button.Right},{button.Bottom};" +
                $"uia_offscreen={button.IsOffscreen};uia_control_type={button.ControlTypeId}{Environment.NewLine}");
            Require(hitThread != 0 && hitPid == expectedPid,
                "Native theme click would leave the isolated display process; bounds=" + boundsPath);
            if (requireForeground)
                Require(GetForegroundWindow() == window, "Isolated native display lost foreground before theme click.");
            GetWindowRect(window, out var rect);
            File.AppendAllText(boundsPath, $"scheme={scheme};client={client.Right - client.Left}x{client.Bottom - client.Top};" +
                $"dpi={dpi};window={rect.Left},{rect.Top},{rect.Right},{rect.Bottom};click={point.X},{point.Y};" +
                $"hit_pid={hitPid};foreground={expectedPid};uia_label={button.Name};uia_bounds={button.Left},{button.Top},{button.Right},{button.Bottom}{Environment.NewLine}");
            Require(SetCursorPos(point.X, point.Y), "Could not point to isolated native theme control.");
            Thread.Sleep(50);
            var clicks = new[] {
                new MouseInputEvent { Type = 0, Mouse = new MouseInput { Flags = 0x0002 } },
                new MouseInputEvent { Type = 0, Mouse = new MouseInput { Flags = 0x0004 } }
            };
            Require(SendInput(1, clicks[..1], Marshal.SizeOf<MouseInputEvent>()) == 1,
                "Windows did not deliver the real mouse-down to the isolated native display.");
            Thread.Sleep(35); // Keep down/up observable to Avalonia instead of collapsing them into one batch.
            var postMoveWindow = WindowFromPoint(point);
            Require(postMoveWindow == hitWindow && GetWindowThreadProcessId(postMoveWindow, out var postMovePid) != 0 &&
                postMovePid == expectedPid, "The isolated native display lost the verified hit target before mouse-up.");
            Require(SendInput(1, clicks[1..], Marshal.SizeOf<MouseInputEvent>()) == 1,
                "Windows did not deliver the real mouse-up to the isolated native display.");
            Thread.Sleep(120); // Allow the target UI thread to consume both events before restoring focus/cursor.
        }
        try
        {
            if (!wasTopmost)
                Require(SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0013),
                    "Could not temporarily raise the isolated native display for real UI input.");
            File.AppendAllText(boundsPath, $"temporary_topmost=true;original_topmost={wasTopmost}{Environment.NewLine}");
            SetForegroundWindow(window);
            var themeButton = FindStableAutomationButton(window, "主题", boundsPath);
            if (GetForegroundWindow() != window)
            {
                // Windows may reject SetForegroundWindow for an unfocused test process.
                // Activate the verified theme button itself; this is still a real input
                // event and cannot accidentally target another process.
                BringWindowToTop(window);
                Click(themeButton, false);
                Require(SpinWait.SpinUntil(() => GetForegroundWindow() == window, 2000),
                    "Could not foreground the isolated native display for real mouse input.");
            }
            else
            {
                Click(themeButton, true);
            }
            Thread.Sleep(100);
            var choiceLabel = scheme == 1 ? "深色" : "浅色";
            var choice = FindStableAutomationButton(window, choiceLabel, boundsPath);
            Click(choice, true);
            Require(WaitForAutomationButtonGone(window, choiceLabel, boundsPath, 2000),
                "The selected theme button remained visible after the real click; the menu did not close; bounds=" + boundsPath);
        }
        finally
        {
            var topmostRestored = wasTopmost || SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
            SetCursorPos(previousCursor.X, previousCursor.Y);
            if (previousWindow != IntPtr.Zero && previousWindow != window) SetForegroundWindow(previousWindow);
            File.AppendAllText(boundsPath, $"topmost_restored={topmostRestored};" +
                $"foreground_after={GetForegroundWindow()}{Environment.NewLine}");
            Require(topmostRestored, "Could not restore the isolated native display's original topmost state.");
        }
    }

    private readonly struct AutomationButtonBounds
    {
        internal AutomationButtonBounds(string name, int left, int top, int right, int bottom,
            bool isOffscreen, int controlTypeId)
        {
            Name = name; Left = left; Top = top; Right = right; Bottom = bottom;
            IsOffscreen = isOffscreen; ControlTypeId = controlTypeId;
        }

        internal string Name { get; }
        internal int Left { get; }
        internal int Top { get; }
        internal int Right { get; }
        internal int Bottom { get; }
        internal bool IsOffscreen { get; }
        internal int ControlTypeId { get; }
        internal WindowPoint Center => new() {
            X = Left + Math.Max(1, Right - Left) / 2,
            Y = Top + Math.Max(1, Bottom - Top) / 2,
        };
    }

    private static AutomationButtonBounds FindStableAutomationButton(IntPtr window, string label, string boundsPath)
    {
        var first = FindAutomationButton(window, label, boundsPath);
        Thread.Sleep(50);
        var second = FindAutomationButton(window, label, boundsPath);
        Require(first.Left == second.Left && first.Top == second.Top && first.Right == second.Right && first.Bottom == second.Bottom,
            $"UI Automation bounds for '{label}' were not stable before SendInput: first={first.Left},{first.Top},{first.Right},{first.Bottom};" +
            $"second={second.Left},{second.Top},{second.Right},{second.Bottom}; bounds={boundsPath}");
        File.AppendAllText(boundsPath, $"uia_stable_label={label};uia_stable_bounds={second.Left},{second.Top},{second.Right},{second.Bottom}{Environment.NewLine}");
        return second;
    }

    private static bool WaitForAutomationButtonGone(IntPtr window, string label, string boundsPath, int timeoutMilliseconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMilliseconds)
        {
            try
            {
                FindAutomationButton(window, label, boundsPath, Math.Min(750, timeoutMilliseconds));
            }
            catch (InvalidOperationException error) when (error.Message.Contains("matches=0;", StringComparison.Ordinal))
            {
                File.AppendAllText(boundsPath, $"uia_gone_label={label};elapsed_ms={deadline.ElapsedMilliseconds}{Environment.NewLine}");
                return true;
            }
            catch (InvalidOperationException error) when (error.Message.Contains("did not return the requested theme control bounds", StringComparison.Ordinal))
            {
                File.AppendAllText(boundsPath, $"uia_query_timeout_label={label};elapsed_ms={deadline.ElapsedMilliseconds}{Environment.NewLine}");
            }
            Thread.Sleep(50);
        }
        File.AppendAllText(boundsPath, $"uia_gone_timeout_label={label};elapsed_ms={deadline.ElapsedMilliseconds}{Environment.NewLine}");
        return false;
    }

    private static void DumpAutomationWindowState(IntPtr window, string boundsPath)
    {
        try
        {
            var task = Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(window);
                var entries = new System.Collections.Generic.List<string>();
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    var current = element.Current;
                    if (string.IsNullOrWhiteSpace(current.Name)) continue;
                    var rect = current.BoundingRectangle;
                    var name = current.Name.Replace('\r', ' ').Replace('\n', ' ');
                    if (name.Length > 512) name = name[..512];
                    entries.Add($"type={current.ControlType.Id};offscreen={current.IsOffscreen};bounds=" +
                        $"{Math.Round(rect.Left)},{Math.Round(rect.Top)},{Math.Round(rect.Right)},{Math.Round(rect.Bottom)};name={name}");
                    if (entries.Count >= 256) break;
                }
                return string.Join('|', entries);
            });
            var state = task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            File.AppendAllText(boundsPath, "uia_window_state=" + state + Environment.NewLine);
        }
        catch (Exception error)
        {
            File.AppendAllText(boundsPath, "uia_window_state_error=" + error.GetType().Name + ":" +
                error.Message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
        }
    }

    private static AutomationButtonBounds FindAutomationButton(IntPtr window, string label, string boundsPath,
        int timeoutMilliseconds = 3000)
    {
        // UI Automation providers can block while a desktop process is repainting.
        // Keep this diagnostic query independently bounded; a timeout is a failed
        // probe, never a reason to fall back to guessed coordinates.
        var task = Task.Run(() =>
        {
            var root = AutomationElement.FromHandle(window);
            Require(root != null, "Windows UI Automation could not create an element for the isolated display HWND.");
            var matches = new System.Collections.Generic.List<AutomationButtonBounds>();
            var labels = new System.Collections.Generic.List<string>();
            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var current = element.Current;
                var rect = current.BoundingRectangle;
                if (current.ControlType.Id != ControlType.Button.Id || string.IsNullOrWhiteSpace(current.Name) ||
                    current.IsOffscreen || rect.IsEmpty || rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;
                var button = new AutomationButtonBounds(current.Name, (int)Math.Round(rect.Left),
                    (int)Math.Round(rect.Top), (int)Math.Round(rect.Right), (int)Math.Round(rect.Bottom),
                    current.IsOffscreen, current.ControlType.Id);
                labels.Add($"{button.Name}={button.Left},{button.Top},{button.Right},{button.Bottom}");
                if (string.Equals(button.Name, label, StringComparison.Ordinal)) matches.Add(button);
            }
            File.AppendAllText(boundsPath, $"uia_query_label={label};uia_visible_buttons={string.Join('|', labels)}{Environment.NewLine}");
            Require(matches.Count == 1,
                $"Windows UI Automation did not identify exactly one visible Button named '{label}'; matches={matches.Count}; buttons={string.Join(" | ", labels)}; bounds={boundsPath}");
            var result = matches[0];
            File.AppendAllText(boundsPath, $"uia_selected_label={result.Name};uia_selected_bounds={result.Left},{result.Top},{result.Right},{result.Bottom};" +
                $"uia_selected_offscreen={result.IsOffscreen};uia_selected_control_type={result.ControlTypeId}{Environment.NewLine}");
            return result;
        });
        try
        {
            return task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Windows UI Automation did not return the requested theme control bounds within {timeoutMilliseconds} ms.");
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(IntPtr handle, [Out] byte[] buffer, int bytesToRead,
        out int bytesRead, IntPtr overlapped);
    private static void Send(IntPtr window, uint message, int wparam, int lparam) =>
        Require(SendMessageTimeout(window, message, new IntPtr(wparam), new IntPtr(lparam), 2, 2000, out _) != IntPtr.Zero, "Test window stopped responding.");
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct ConsoleKeyEvent {
        public int KeyDown; public ushort RepeatCount, VirtualKey, VirtualScan, Character; public uint ControlState;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ConsoleInputRecord {
        public ushort EventType, Padding; public ConsoleKeyEvent KeyEvent;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInputEvent { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry {
        public uint Size, Usage, ProcessId; public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId; public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out WindowRect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out WindowPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(WindowPoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] MouseInputEvent[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)] private static extern bool ReadConsoleInput(
        IntPtr input, out ConsoleInputRecord record, uint length, out uint read);
}
