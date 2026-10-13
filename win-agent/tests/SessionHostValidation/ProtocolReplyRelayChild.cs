using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

internal static class ProtocolReplyRelayChild
{
    private const int MaxReceivedBytes = 32 * 1024;
    private const int PhaseDeadlineMilliseconds = 3000;
    private const string TriggerName = "protocol-replies.trigger";
    private const string MouseArm = "\u001bc\x1b[?1049h\x1b[?1000h\x1b[?1006h\x1b[4;1H__WHEEL_MOUSE_READY__";
    private const string MouseDisarm = "\x1b[?1000l\x1b[?1006l\x1b[?1007l\x1b[?1049l";

    internal static void Child(string workingDirectory)
    {
        workingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        if (Marshal.SizeOf<ConsoleInputRecord>() != 20)
            throw new InvalidOperationException("Console INPUT_RECORD ABI size is not 20 bytes.");
        var input = GetStdHandle(-10);
        var output = GetStdHandle(-11);
        if (!GetConsoleMode(input, out var inputMode) ||
            !GetConsoleMode(output, out var outputMode) ||
            !SetConsoleMode(input, (inputMode & ~(1U | 2U | 4U)) | 512U) ||
            !SetConsoleMode(output, outputMode | 4U) ||
            !SetConsoleCP(65001) || !SetConsoleOutputCP(65001))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Protocol reply child requires a VT ConPTY.");

        var trigger = Path.Combine(workingDirectory, TriggerName);
        using var stdout = Console.OpenStandardOutput();
        Write(stdout, "\x1b[2J\x1b[2;1H__PROTOCOL_REPLY_RELAY_READY__");
        var idle = Stopwatch.StartNew();
        var wheelState = 0;
        var themeSwitchState = 0;
        try
        {
            // Android inspect can use its full hook timeout between phase triggers.
            while (idle.Elapsed < TimeSpan.FromMinutes(2))
            {
                if (!File.Exists(trigger)) { Thread.Sleep(25); continue; }
                string phase;
                try { phase = File.ReadAllText(trigger, Encoding.UTF8).Trim().ToLowerInvariant(); }
                catch (IOException) { Thread.Sleep(25); continue; }
                if (phase.Length == 0) { Thread.Sleep(25); continue; }
                File.Delete(trigger);
                if (phase == "exit") break;
                if (phase is not ("geometry" or "queries" or "state" or "theme" or
                    "theme-switch-arm" or "theme-switch-read" or "theme-switch-same" or
                    "theme-switch-unsubscribe" or "theme-switch-restore" or "theme-switch-final" or
                    "mouse-arm" or "mouse-read" or "nonmouse-arm"))
                    throw new InvalidDataException("Unknown protocol reply phase: " + phase);
                if (wheelState != 0 && phase is not ("mouse-read" or "nonmouse-arm") ||
                    phase == "mouse-arm" && wheelState != 0 ||
                    phase == "mouse-read" && wheelState != 1 ||
                    phase == "nonmouse-arm" && wheelState != 2)
                    throw new InvalidDataException("Wheel phase is out of order: " + phase);
                var expectedThemeSwitchPhase = themeSwitchState switch {
                    1 => "theme-switch-read", 2 => "theme-switch-same", 3 => "theme-switch-unsubscribe",
                    4 => "theme-switch-restore", 5 => "theme-switch-final", _ => null
                };
                if (phase.StartsWith("theme-switch-", StringComparison.Ordinal) &&
                    phase != (expectedThemeSwitchPhase ?? "theme-switch-arm") ||
                    expectedThemeSwitchPhase != null && phase != expectedThemeSwitchPhase)
                    throw new InvalidDataException("Theme switch phase is out of order: " + phase);
                idle.Restart();
                var records = new List<StageRecord>();
                string error = null;
                switch (phase)
                {
                    case "geometry":
                        records.Add(SendAndCapture(input, stdout, trigger, "geometry",
                            "\x1b[14t\x1b[14;2t\x1b[15t\x1b[16t\x1b[18t\x1b[19t",
                            Stopwatch.StartNew(), MaxReceivedBytes, null));
                        break;
                    case "queries":
                        RunQueries(input, stdout, trigger, records);
                        break;
                    case "state":
                        RunState(input, stdout, trigger, records, out error);
                        break;
                    case "theme":
                        RunTheme(input, stdout, trigger, records);
                        break;
                    case "theme-switch-arm":
                        records.Add(SendAndCapture(input, stdout, trigger, "query", "\x1b[?996n",
                            Stopwatch.StartNew(), MaxReceivedBytes, IsThemeReply));
                        records.Add(SendAndCapture(input, stdout, trigger, "subscribe", "\x1b[?2031h",
                            Stopwatch.StartNew(), MaxReceivedBytes - records[0].Received.Length, IsThemeReply));
                        records.Add(SendAndCaptureQuiet(input, stdout, trigger, "extra-quiet", "",
                            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length), 1000));
                        themeSwitchState = 1;
                        break;
                    case "theme-switch-read":
                        records.Add(SendAndCapture(input, stdout, trigger, "changed", "",
                            Stopwatch.StartNew(), MaxReceivedBytes, IsThemeReply));
                        records.Add(SendAndCaptureQuiet(input, stdout, trigger, "extra-quiet", "",
                            Stopwatch.StartNew(), MaxReceivedBytes - records[0].Received.Length, 1000));
                        themeSwitchState = 2;
                        break;
                    case "theme-switch-same":
                        records.Add(SendAndCaptureQuiet(input, stdout, trigger, "same-color-quiet", "",
                            Stopwatch.StartNew(), MaxReceivedBytes, 1000));
                        themeSwitchState = 3;
                        break;
                    case "theme-switch-unsubscribe":
                        records.Add(SendAndCaptureQuiet(input, stdout, trigger, "unsubscribe", "\x1b[?2031l",
                            Stopwatch.StartNew(), MaxReceivedBytes, 1000));
                        records.Add(SendAndCapture(input, stdout, trigger, "status-after", "\x1b[?2031$p",
                            Stopwatch.StartNew(), MaxReceivedBytes - records[0].Received.Length,
                            bytes => IsExact(bytes, "\x1b[?2031;2$y")));
                        themeSwitchState = 4;
                        break;
                    case "theme-switch-restore":
                        records.Add(SendAndCaptureQuiet(input, stdout, trigger, "restored-quiet", "",
                            Stopwatch.StartNew(), MaxReceivedBytes, 1000));
                        themeSwitchState = 5;
                        break;
                    case "theme-switch-final":
                        records.Add(SendAndCapture(input, stdout, trigger, "final-query", "\x1b[?996n",
                            Stopwatch.StartNew(), MaxReceivedBytes, IsThemeReply));
                        records.Add(SendAndCaptureQuiet(input, stdout, trigger, "extra-quiet", "",
                            Stopwatch.StartNew(), MaxReceivedBytes - records[0].Received.Length, 1000));
                        themeSwitchState = 6;
                        break;
                    case "mouse-arm":
                        Write(stdout, MouseArm);
                        records.Add(new StageRecord("arm", Encoding.ASCII.GetBytes(MouseArm),
                            Array.Empty<byte>(), Array.Empty<string>(), false, false));
                        wheelState = 1;
                        break;
                    case "mouse-read":
                        records.Add(CaptureWheel(input, trigger));
                        wheelState = 2;
                        break;
                    case "nonmouse-arm":
                        var disarm = MouseDisarm + "\x1b[4;1H__WHEEL_NONMOUSE_READY__";
                        Write(stdout, disarm);
                        records.Add(new StageRecord("disarm", Encoding.ASCII.GetBytes(disarm),
                            Array.Empty<byte>(), Array.Empty<string>(), false, false));
                        wheelState = 0;
                        break;
                }
                var result = new
                {
                    phase,
                    sent_b64 = Convert.ToBase64String(records.SelectMany(item => item.Sent).ToArray()),
                    received_hex = Convert.ToHexString(records.SelectMany(item => item.Received).ToArray()),
                    records = records.Select(item => new
                    {
                        stage = item.Stage,
                        sent_b64 = Convert.ToBase64String(item.Sent),
                        received_hex = Convert.ToHexString(item.Received),
                        received_chunks_hex = item.Chunks,
                        unexpected_code_units_hex = item.UnexpectedCodeUnitsHex,
                        timed_out = item.TimedOut,
                        truncated = item.Truncated
                    }).ToArray(),
                    timed_out = records.Any(item => item.TimedOut),
                    truncated = records.Any(item => item.Truncated),
                    error
                };
                File.WriteAllText(Path.Combine(workingDirectory, "protocol-replies-" + phase + ".json"),
                    JsonSerializer.Serialize(result), Encoding.UTF8);
                if (phase == "mouse-read") Write(stdout, "\x1b[5;1H__WHEEL_MOUSE_READ_DONE__");
                else if (phase is not ("mouse-arm" or "nonmouse-arm"))
                    Write(stdout, "\x1b[3;1H__PROTOCOL_REPLY_RELAY_" + phase.ToUpperInvariant() + "_DONE__");
                idle.Restart();
            }
        }
        catch (OperationCanceledException) when (ExitRequested(trigger)) { }
        finally
        {
            if (wheelState != 0)
            {
                try { Write(stdout, MouseDisarm); }
                catch (IOException) { }
            }
            SetConsoleMode(input, inputMode);
            SetConsoleMode(output, outputMode);
        }
    }

    private static StageRecord CaptureWheel(IntPtr input, string trigger)
    {
        using var received = new MemoryStream();
        var chunks = new List<string>();
        var buffer = new byte[4096];
        var elapsed = Stopwatch.StartNew();
        var drainFrom = -1L;
        var drained = false;
        while (received.Length < MaxReceivedBytes)
        {
            if (ExitRequested(trigger)) throw new OperationCanceledException();
            var now = elapsed.ElapsedMilliseconds;
            if (drainFrom >= 0 && now - drainFrom >= 500) { drained = true; break; }
            if (now >= 10000) break;
            var remaining = Math.Min(50L, 10000 - now);
            if (drainFrom >= 0) remaining = Math.Min(remaining, 500 - (now - drainFrom));
            var wait = WaitForSingleObject(input, (uint)Math.Max(1, remaining));
            if (wait == 258) continue;
            if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Wait for wheel input failed.");
            var readSize = Math.Min(buffer.Length, MaxReceivedBytes - (int)received.Length);
            if (!ReadFile(input, buffer, readSize, out var read, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read wheel input failed.");
            if (read == 0) break;
            received.Write(buffer, 0, read);
            chunks.Add(Convert.ToHexString(buffer, 0, read));
            if (drainFrom < 0 && CountWheelReports(received.ToArray()) >= 2)
                drainFrom = elapsed.ElapsedMilliseconds;
        }
        return new StageRecord("wheel", Array.Empty<byte>(), received.ToArray(), chunks.ToArray(),
            !drained, received.Length == MaxReceivedBytes);
    }

    private static int CountWheelReports(byte[] bytes) => Regex.Matches(
        Encoding.ASCII.GetString(bytes), @"\x1B\[<[0-9]+;[0-9]+;[0-9]+[Mm]").Count;

    private static void RunQueries(IntPtr input, Stream output, string trigger, List<StageRecord> records)
    {
        // The checksum is fixed; the display-size report in this first burst remains dynamic.
        records.Add(SendAndCapture(input, output, trigger, "queries",
            "\x1b[1;1HAB\x1b[=c\x1b[?15n\x1b[\"v\x1b[42;1;1;1;1;2*y",
            Stopwatch.StartNew(), MaxReceivedBytes, null));
        const string fixedReplies = "\x1b[2;1;1;128;128;1;0x\x1b[3;1;1;128;128;1;0x" +
            "\x1b[?23n\x1b[?27;0;0;5n\x1b[?53n\x1b[?57;0n\x1b[?70n";
        records.Add(SendAndCapture(input, output, trigger, "fixed",
            "\x1b[x\x1b[1x\x1b[?25n\x1b[?26n\x1b[?55n\x1b[?56n\x1b[?75n",
            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length),
            bytes => IsExact(bytes, fixedReplies)));
        const string da3 = "\x1bP!|00000000\x1b\\";
        records.Add(SendAndCapture(input, output, trigger, "repeat",
            "\x1b[=c\x1b[=c", Stopwatch.StartNew(),
            MaxReceivedBytes - records.Sum(item => item.Received.Length),
            bytes => IsExact(bytes, da3 + da3)));
        records.Add(SendAndCaptureQuiet(input, output, trigger, "split-head", "\x1b[?26",
            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length)));
        records.Add(SendAndCapture(input, output, trigger, "split-tail", "n",
            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length),
            bytes => IsExact(bytes, "\x1b[?27;0;0;5n")));
    }

    private static void RunState(
        IntPtr input, Stream output, string trigger, List<StageRecord> records, out string error)
    {
        error = null;
        const string queries = "\x1b[1$w\x1b[2$w";
        var before = SendAndCapture(input, output, trigger, "before", queries,
            Stopwatch.StartNew(), MaxReceivedBytes,
            bytes => Report(bytes, "1$u") != null && Report(bytes, "2$u") != null);
        records.Add(before);
        var cursorReport = Report(before.Received, "1$u");
        var tabReport = Report(before.Received, "2$u");
        if (cursorReport == null || tabReport == null)
        {
            error = "state_report_missing";
            return;
        }
        var changed = Encoding.ASCII.GetBytes("\x1b[2;3H\x1b[1m\x1b[3g");
        Write(output, changed);
        records.Add(new StageRecord("mutate", changed, Array.Empty<byte>(), Array.Empty<string>(), false, false));
        records.Add(SendAndCapture(input, output, trigger, "changed", queries,
            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length),
            bytes => Report(bytes, "1$u") != null && Report(bytes, "2$u") != null));
        records.Add(SendAndCaptureQuiet(input, output, trigger, "bad-query-quiet", "\x1b[2;3$w",
            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length)));
        records.Add(SendAndCaptureQuiet(input, output, trigger, "cancelled-restore-quiet", "\x1bP2$t1/2/513\x18",
            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length)));
        records.Add(SendAndCapture(input, output, trigger, "after-rejection", queries,
            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length),
            bytes => Report(bytes, "1$u") != null && Report(bytes, "2$u") != null));
        var cursorRestore = (byte[])cursorReport.Clone();
        cursorRestore[4] = (byte)'t'; // DCS 1$u -> DCS 1$t, preserving the measured report payload.
        var tabRestore = (byte[])tabReport.Clone();
        tabRestore[4] = (byte)'t';
        var sent = cursorRestore.Concat(tabRestore).Concat(Encoding.ASCII.GetBytes(queries)).ToArray();
        var after = SendAndCapture(input, output, trigger, "restore-and-query", sent,
            Stopwatch.StartNew(), MaxReceivedBytes - records.Sum(item => item.Received.Length),
            bytes => Report(bytes, "1$u") != null && Report(bytes, "2$u") != null);
        records.Add(after);
        if (Report(after.Received, "1$u") == null || Report(after.Received, "2$u") == null)
            error = "restored_state_report_missing";
    }

    private static void RunTheme(IntPtr input, Stream output, string trigger, List<StageRecord> records)
    {
        // This fixture selects a real native display, which publishes the current host theme.
        // Theme changes themselves require a separate display-side driver.
        var deadline = Stopwatch.StartNew();
        var received = 0;
        void Reply(string stage, string sent, Func<byte[], bool> complete)
        {
            var record = SendAndCapture(input, output, trigger, stage, sent,
                deadline, MaxReceivedBytes - received, complete);
            records.Add(record);
            received += record.Received.Length;
        }
        void Quiet(string stage, string sent)
        {
            var record = SendAndCaptureQuiet(input, output, trigger, stage, sent,
                deadline, MaxReceivedBytes - received);
            records.Add(record);
            received += record.Received.Length;
        }

        Reply("query", "\x1b[?996n", IsThemeReply);
        Reply("status-before", "\x1b[?2031$p", bytes => IsExact(bytes, "\x1b[?2031;2$y"));
        Reply("subscribe", "\x1b[?2031h", IsThemeReply);
        Quiet("subscribe-again", "\x1b[?2031h");
        Reply("status-on", "\x1b[?2031$p", bytes => IsExact(bytes, "\x1b[?2031;1$y"));
        Quiet("unsubscribe", "\x1b[?2031l");
        Reply("status-after", "\x1b[?2031$p", bytes => IsExact(bytes, "\x1b[?2031;2$y"));
        Reply("ris-and-query", "\u001bc\x1b[?996n\x1b[?2031$p", bytes =>
            Regex.IsMatch(Encoding.ASCII.GetString(bytes), @"^\x1B\[\?997;[12]n\x1B\[\?2031;2\$y$"));
        Quiet("final-quiet", "");
    }

    private static bool IsExact(byte[] bytes, string expected) =>
        bytes.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(expected));

    private static bool IsThemeReply(byte[] bytes) =>
        Regex.IsMatch(Encoding.ASCII.GetString(bytes), @"^\x1B\[\?997;[12]n$");

    private static StageRecord SendAndCaptureQuiet(
        IntPtr input, Stream output, string trigger, string stage, string sent,
        Stopwatch deadline, int capacity, int quietMilliseconds = 250)
    {
        var sentBytes = Encoding.ASCII.GetBytes(sent);
        Write(output, sentBytes);
        var unexpected = new StringBuilder();
        var quiet = Stopwatch.StartNew();
        while (quiet.ElapsedMilliseconds < quietMilliseconds &&
            deadline.ElapsedMilliseconds < PhaseDeadlineMilliseconds && unexpected.Length / 4 < capacity)
        {
            if (ExitRequested(trigger)) throw new OperationCanceledException();
            var remaining = Math.Min(quietMilliseconds - (int)quiet.ElapsedMilliseconds,
                PhaseDeadlineMilliseconds - (int)deadline.ElapsedMilliseconds);
            var wait = WaitForSingleObject(input, (uint)Math.Min(50, remaining));
            if (wait == 258) continue;
            if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Wait for ConPTY theme reply failed.");
            // ReadFile can block on non-character mouse/focus events even after the handle signals.
            if (!ReadConsoleInput(input, out var record, 1, out var read) || read != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read ConPTY theme input record failed.");
            if (record.EventType == 1 && record.KeyEvent.KeyDown != 0 && record.KeyEvent.Character != 0)
                for (var repeat = 0; repeat < Math.Max(1, (int)record.KeyEvent.RepeatCount) &&
                    unexpected.Length / 4 < capacity; repeat++)
                    unexpected.Append(((int)record.KeyEvent.Character).ToString("X4"));
        }
        return new StageRecord(stage, sentBytes, Array.Empty<byte>(), Array.Empty<string>(),
            quiet.ElapsedMilliseconds < quietMilliseconds, unexpected.Length / 4 >= capacity,
            unexpected.ToString());
    }

    private static StageRecord SendAndCapture(
        IntPtr input, Stream output, string trigger, string stage, string sent,
        Stopwatch deadline, int capacity, Func<byte[], bool> complete) =>
        SendAndCapture(input, output, trigger, stage, Encoding.ASCII.GetBytes(sent), deadline, capacity, complete);

    private static StageRecord SendAndCapture(
        IntPtr input, Stream output, string trigger, string stage, byte[] sent,
        Stopwatch deadline, int capacity, Func<byte[], bool> complete)
    {
        Write(output, sent);
        using var received = new MemoryStream();
        var chunks = new List<string>();
        var buffer = new byte[4096];
        while (deadline.ElapsedMilliseconds < PhaseDeadlineMilliseconds && received.Length < capacity)
        {
            if (ExitRequested(trigger)) throw new OperationCanceledException();
            var remaining = PhaseDeadlineMilliseconds - (int)deadline.ElapsedMilliseconds;
            var wait = WaitForSingleObject(input, (uint)Math.Min(50, remaining));
            if (wait == 258) continue;
            if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Wait for ConPTY reply failed.");
            if (!PeekConsoleInput(input, out var next, 1, out var peeked))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Peek ConPTY reply input failed.");
            if (peeked == 0) continue;
            if (next.EventType != 1 || next.KeyEvent.KeyDown == 0 || next.KeyEvent.Character == 0)
            {
                if (!ReadConsoleInput(input, out _, 1, out var discarded) || discarded != 1)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Discard non-character ConPTY input failed.");
                continue;
            }
            var readSize = Math.Min(buffer.Length, capacity - (int)received.Length);
            if (!ReadFile(input, buffer, readSize, out var read, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read ConPTY reply failed.");
            if (read == 0) break;
            received.Write(buffer, 0, read);
            chunks.Add(Convert.ToHexString(buffer, 0, read));
            if (complete != null && complete(received.ToArray())) break;
        }
        var bytes = received.ToArray();
        return new StageRecord(stage, sent, bytes, chunks.ToArray(),
            complete == null ? bytes.Length == 0 : !complete(bytes), bytes.Length == capacity);
    }

    private static byte[] Report(byte[] bytes, string selector)
    {
        var prefix = Encoding.ASCII.GetBytes("\x1bP" + selector);
        for (var start = 0; start <= bytes.Length - prefix.Length; start++)
        {
            if (!bytes.AsSpan(start, prefix.Length).SequenceEqual(prefix)) continue;
            for (var end = start + prefix.Length; end + 1 < bytes.Length; end++)
                if (bytes[end] == 0x1b && bytes[end + 1] == (byte)'\\')
                    return bytes.AsSpan(start, end + 2 - start).ToArray();
        }
        return null;
    }

    private static bool ExitRequested(string trigger)
    {
        if (!File.Exists(trigger)) return false;
        try { return File.ReadAllText(trigger, Encoding.UTF8).Trim().Equals("exit", StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
    }

    private static void Write(Stream output, string value) => Write(output, Encoding.ASCII.GetBytes(value));

    private static void Write(Stream output, byte[] bytes)
    {
        output.Write(bytes);
        output.Flush();
    }

    private sealed record StageRecord(
        string Stage, byte[] Sent, byte[] Received, string[] Chunks, bool TimedOut, bool Truncated,
        string UnexpectedCodeUnitsHex = "");

    [StructLayout(LayoutKind.Sequential)] private struct ConsoleKeyEvent {
        public int KeyDown; public ushort RepeatCount, VirtualKey, VirtualScan, Character; public uint ControlState;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ConsoleInputRecord {
        public ushort EventType, Padding; public ConsoleKeyEvent KeyEvent;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCP(uint codePage);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleOutputCP(uint codePage);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(
        IntPtr handle, [Out] byte[] buffer, int bytesToRead, out int bytesRead, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)] private static extern bool ReadConsoleInput(
        IntPtr input, out ConsoleInputRecord record, uint length, out uint read);
    [DllImport("kernel32.dll", EntryPoint = "PeekConsoleInputW", SetLastError = true)] private static extern bool PeekConsoleInput(
        IntPtr input, out ConsoleInputRecord record, uint length, out uint read);
}
