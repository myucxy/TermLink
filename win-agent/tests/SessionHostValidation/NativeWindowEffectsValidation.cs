#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class NativeWindowEffectsValidation
{
    private static readonly string[] Phases =
        ["clipboard-cancel", "clipboard-accept", "clipboard-background", "bell-muted", "bell-audible", "decps-audible", "exit"];

    internal static void SelfTest()
    {
        foreach (var phase in Phases) Require(ParsePhase(phase + "\n") == phase, "Phase parser rejected a known phase.");
        foreach (var bad in new[] { "", "unknown\n", "bell-muted\nsecret", "../exit\n" })
            RequireThrows(() => ParsePhase(bad), "Phase parser accepted an unsafe trigger.");
        var attempts = 0;
        var busyReports = 0;
        Require(ClipboardRetry(() => {
            if (++attempts == 1) throw new COMException("clipboard busy", unchecked((int)0x800401D0));
            return 7;
        }, () => busyReports++) == 7 && attempts == 2 && busyReports == 1,
            "Transient clipboard-open failure was not reported and retried exactly once.");
        attempts = 0;
        try
        {
            ClipboardRetry<int>(() => { attempts++; throw new COMException("other COM error", unchecked((int)0x80004005)); });
            throw new InvalidOperationException("Other COM errors were retried or ignored.");
        }
        catch (COMException error) when (error.HResult == unchecked((int)0x80004005))
        { Require(attempts == 1, "Other COM errors were retried."); }
        Exception? pumpFailure = null;
        var pumpThread = new Thread(() =>
        {
            try
            {
                var dispatched = false;
                Dispatcher.CurrentDispatcher.BeginInvoke((Action)(() => dispatched = true));
                Require(ClipboardRetry(() => {
                    if (!dispatched) throw new COMException("clipboard busy", unchecked((int)0x800401D0));
                    return true;
                }), "Clipboard retry did not dispatch pending STA work.");
            }
            catch (Exception error) { pumpFailure = error; }
        });
        pumpThread.SetApartmentState(ApartmentState.STA);
        pumpThread.Start();
        Require(pumpThread.Join(4_000) && pumpFailure == null, "Clipboard retry did not pump its STA message queue.");
        ClipboardSnapshot.SelfTest();
    }

    private static string ParsePhase(string request)
    {
        var phase = request.TrimEnd('\r', '\n');
        Require(Phases.Contains(phase) && request == phase + "\n", "Unknown or malformed effects phase.");
        return phase;
    }

    internal static void Child(string runDirectory)
    {
        Require(Path.IsPathFullyQualified(runDirectory) && Directory.Exists(runDirectory), "Effects child directory is invalid.");
        File.WriteAllText(Path.Combine(runDirectory, "child-ready.txt"), Environment.ProcessId.ToString(), Encoding.ASCII);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMinutes(3))
        {
            var trigger = Path.Combine(runDirectory, "phase.trigger");
            if (!File.Exists(trigger)) { Thread.Sleep(30); continue; }
            var phase = ParsePhase(File.ReadAllText(trigger, Encoding.ASCII));
            File.Delete(trigger);
            if (phase == "exit") return;
            var marker = "__NATIVE_EFFECTS_" + phase.Replace('-', '_').ToUpperInvariant() + "__";
            var text = phase switch
            {
                "clipboard-cancel" or "clipboard-accept" or "clipboard-background" =>
                    "\x1b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        "TermLink " + phase + " " + Path.GetFileName(runDirectory))) + "\a",
                "bell-muted" or "bell-audible" => "\a",
                "decps-audible" => "\x1b[7;8;1;5;8,~",
                _ => throw new InvalidOperationException("Unsupported effects phase."),
            };
            Console.Write(text + marker + "\r\n");
            Console.Out.Flush();
            File.WriteAllText(Path.Combine(runDirectory, phase + ".ack.json"), JsonSerializer.Serialize(new
            {
                phase, child_pid = Environment.ProcessId, output_flushed_at = Stopwatch.GetTimestamp(), marker,
            }), Encoding.UTF8);
        }
        throw new TimeoutException("Effects child did not receive an exit trigger.");
    }

    internal static void Run(string hostPath, string displayPath, string agentControlPipe,
        string audioRecorderPath, string runId, string artifactRoot)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { RunSta(hostPath, displayPath, agentControlPipe, audioRecorderPath, runId, artifactRoot); }
            catch (Exception failure) { error = failure; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("Native window effects acceptance failed.", error);
    }

    private static void RunSta(string hostPath, string displayPath, string agentControlPipe,
        string audioRecorderPath, string runId, string artifactRoot)
    {
        Require(OperatingSystem.IsWindows(), "Native effects require Windows.");
        Require(Path.IsPathFullyQualified(hostPath) && File.Exists(hostPath) &&
            Path.GetFileName(hostPath).Equals("TermLink.SessionHost.exe", StringComparison.OrdinalIgnoreCase),
            "SessionHost path is invalid.");
        Require(Path.IsPathFullyQualified(displayPath) && File.Exists(displayPath) &&
            Path.GetFileName(displayPath).Equals("TermLink.exe", StringComparison.OrdinalIgnoreCase),
            "Native display path is invalid.");
        Require(Path.IsPathFullyQualified(audioRecorderPath) && File.Exists(audioRecorderPath) &&
            Path.GetFileName(audioRecorderPath).Equals("TermLink.TerminalSurface.Validation.exe", StringComparison.OrdinalIgnoreCase),
            "Audio recorder path is invalid.");
        Require(agentControlPipe.Length is >= 1 and <= 200 && agentControlPipe.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'),
            "Agent control pipe is invalid.");
        Require(runId.Length is >= 1 and <= 64 && runId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'),
            "Run ID is invalid.");
        Require(Path.IsPathFullyQualified(artifactRoot) && Directory.Exists(artifactRoot), "Artifact root is invalid.");
        var runDirectory = Path.Combine(artifactRoot, "native-window-effects-" + runId);
        Require(!Directory.Exists(runDirectory), "Effects artifact directory already exists.");
        Directory.CreateDirectory(runDirectory);
        var evidencePath = Path.Combine(runDirectory, "evidence.json");
        var evidence = new Dictionary<string, object?> { ["run_id"] = runId, ["status"] = "failed",
            ["checks"] = new List<string>(), ["recordings"] = new List<object>(), ["screenshots"] = new List<object>() };
        var checks = (List<string>)evidence["checks"]!;
        Process? host = null;
        long? hostStartUtcTicks = null;
        NativeWindowValidation.OwnedDisplayIdentity? ownedDisplay = null;
        ClipboardSnapshot? originalClipboard = null;
        bool clipboardCaptured = false, originalSettingsExists = false;
        byte[]? originalSettings = null, lastTestSettings = null;
        var allowedSettings = new List<byte[]?>();
        var allowedClipboard = new List<string>();
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermLink", "terminal-alerts-v1.bin");
        var initialForeground = GetForegroundWindow();
        string? failure = null, cleanupFailure = null;
        var clipboardStage = "preflight";
        var clipboardStages = new List<string>();
        evidence["clipboard_stages"] = clipboardStages;
        var cleaningUp = false;
        void SetClipboardStage(string stage) { clipboardStage = stage; clipboardStages.Add(stage); }
        void NoteClipboardBusy()
        {
            var key = cleaningUp ? "clipboard_cleanup_first_busy" : "clipboard_first_busy";
            if (!evidence.ContainsKey(key)) evidence[key] = new { stage = clipboardStage, windows = ClipboardWindows() };
        }
        try
        {
            originalSettingsExists = File.Exists(settingsPath);
            if (originalSettingsExists) originalSettings = File.ReadAllBytes(settingsPath);
            lastTestSettings = originalSettings?.ToArray();
            allowedSettings.Add(lastTestSettings);
            SetClipboardStage("capture");
            originalClipboard = ClipboardSnapshot.Capture(NoteClipboardBusy);
            clipboardCaptured = true;
            var sentinel = "TermLink effects sentinel " + runId;
            allowedClipboard.Add(sentinel);
            SetClipboardStage("sentinel-set");
            ClipboardRetry(() => Clipboard.SetText(sentinel), NoteClipboardBusy);
            SetClipboardStage("sentinel-read");
            Require(ClipboardRetry<string>(() => Clipboard.GetText(), NoteClipboardBusy) == sentinel, "Clipboard sentinel was not installed.");
            SetClipboardStage("runtime");
            host = StartHost(hostPath, displayPath, agentControlPipe, runDirectory);
            hostStartUtcTicks = host.StartTime.ToUniversalTime().Ticks;
            var owned = WaitOwnedDisplay(host, hostPath, displayPath);
            ownedDisplay = owned;
            Require(owned.HostStartUtcTicks == hostStartUtcTicks,
                "Selected SessionHost changed identity after launch.");
            evidence["owner"] = new { owned.HostPid, owned.HostPath, owned.HostStartUtcTicks,
                owned.DisplayPid, owned.DisplayPath, owned.DisplayStartUtcTicks,
                hwnd = owned.Window.ToInt64() };
            Require(File.Exists(Path.Combine(runDirectory, "child-ready.txt")) ||
                SpinWait.SpinUntil(() => File.Exists(Path.Combine(runDirectory, "child-ready.txt")), 10_000),
                "Real ConPTY child did not start.");
            EnsureSetting(owned, settingsPath, 1, true, allowedSettings, lastTestSettings, out lastTestSettings);
            EnsureSetting(owned, settingsPath, 2, false, allowedSettings, lastTestSettings, out lastTestSettings);
            EnsureSetting(owned, settingsPath, 3, true, allowedSettings, lastTestSettings, out lastTestSettings);
            EnsureMainForeground(owned, "clipboard-cancel");
            checks.Add("owned_window_settings_enabled");
            Capture(owned.Window, owned.DisplayPid, Path.Combine(runDirectory, "settings.png"),
                (List<object>)evidence["screenshots"]!);

            Trigger(runDirectory, "clipboard-cancel");
            var prompt = WaitPrompt(owned.Window, owned.DisplayPid, true, "clipboard-cancel");
            Capture(prompt, owned.DisplayPid, Path.Combine(runDirectory, "clipboard-cancel.png"),
                (List<object>)evidence["screenshots"]!);
            ClickOwned(prompt, owned.DisplayPid, "取消");
            Require(SpinWait.SpinUntil(() => !PromptExists(owned.Window, owned.DisplayPid), 3_000), "Cancelled clipboard prompt stayed open.");
            SetClipboardStage("cancel-read");
            Require(ClipboardRetry<string>(() => Clipboard.GetText(), NoteClipboardBusy) == sentinel, "Cancel changed the system clipboard.");
            SetClipboardStage("runtime");
            // New clipboard requests must outlast the authorization gate's three-second flood limit.
            Thread.Sleep(3_100);
            checks.Add("clipboard_cancel_preserved_sentinel");

            EnsureMainForeground(owned, "clipboard-accept");
            Trigger(runDirectory, "clipboard-accept");
            prompt = WaitPrompt(owned.Window, owned.DisplayPid, true, "clipboard-accept");
            var accepted = "TermLink clipboard-accept " + Path.GetFileName(runDirectory);
            allowedClipboard.Add(accepted);
            ClickOwned(prompt, owned.DisplayPid, "仅复制本次");
            SetClipboardStage("accept-read");
            Require(SpinWait.SpinUntil(() => ClipboardRetry<string>(() => Clipboard.GetText(), NoteClipboardBusy) == accepted, 5_000),
                "One-time clipboard authorization did not copy exact child text.");
            SetClipboardStage("runtime");
            Require(SpinWait.SpinUntil(() => !PromptExists(owned.Window, owned.DisplayPid), 3_000),
                "One-time clipboard prompt stayed open after copying.");
            checks.Add("clipboard_once_exact_text");

            Thread.Sleep(3_100);
            ShowWindow(owned.Window, 6);
            Require(SpinWait.SpinUntil(() => IsIconic(owned.Window), 2_000), "Owned display did not enter background.");
            Trigger(runDirectory, "clipboard-background");
            Require(WaitPrompt(owned.Window, owned.DisplayPid, false, "clipboard-background") == IntPtr.Zero,
                "Background clipboard request displayed a prompt.");
            ShowWindow(owned.Window, 9);
            BringWindowToTop(owned.Window);
            SetForegroundWindow(owned.Window);
            EnsureMainForeground(owned, "clipboard-background-return");
            Require(WaitPrompt(owned.Window, owned.DisplayPid, false, "clipboard-background-return") == IntPtr.Zero,
                "Background clipboard request resurfaced after activation.");
            SetClipboardStage("background-read");
            Require(ClipboardRetry<string>(() => Clipboard.GetText(), NoteClipboardBusy) == accepted, "Background clipboard request changed clipboard text.");
            SetClipboardStage("runtime");
            checks.Add("background_clipboard_dropped_without_replay");

            EnsureSetting(owned, settingsPath, 2, false, allowedSettings, lastTestSettings, out lastTestSettings);
            RecordPhase(owned, runDirectory, audioRecorderPath, "bell-muted", evidence);
            Thread.Sleep(3_300);
            EnsureSetting(owned, settingsPath, 2, true, allowedSettings, lastTestSettings, out lastTestSettings);
            RecordPhase(owned, runDirectory, audioRecorderPath, "bell-audible", evidence);
            Thread.Sleep(3_300);
            RecordPhase(owned, runDirectory, audioRecorderPath, "decps-audible", evidence);
            checks.Add("recorded_real_bel_decps_and_muted_output");
            evidence["status"] = "audio_pending_analysis";
        }
        catch (Exception caught)
        {
            failure = caught.GetType().Name + ": " + caught.Message;
            evidence["failure_stage"] = clipboardStage;
        }
        finally
        {
            cleaningUp = true;
            try
            {
                if (host != null) StopOwnedHost(host, hostPath, hostStartUtcTicks, runDirectory);
            }
            catch (Exception caught) { cleanupFailure = "host: " + caught.Message; }
            try
            {
                if (ownedDisplay.HasValue) CloseOwnedDisplay(ownedDisplay.Value);
            }
            catch (Exception caught) { cleanupFailure = AddCleanupFailure(cleanupFailure, "display: " + caught.Message); }
            try
            {
                var current = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
                Require(allowedSettings.Any(candidate => BytesEqual(current, candidate)),
                    "Native settings changed outside this test; refusing to overwrite them.");
                if (originalSettingsExists) File.WriteAllBytes(settingsPath, originalSettings!);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
            catch (Exception caught) { cleanupFailure = AddCleanupFailure(cleanupFailure, "settings: " + caught.Message); }
            try
            {
                if (clipboardCaptured)
                {
                    SetClipboardStage("restore");
                    Require(ClipboardRetry<bool>(() => Clipboard.ContainsText() && allowedClipboard.Contains(Clipboard.GetText()), NoteClipboardBusy),
                        "Clipboard changed outside this test; refusing to overwrite it.");
                    if (originalClipboard!.Data == null) ClipboardRetry(() => Clipboard.Clear(), NoteClipboardBusy);
                    else
                    {
                        originalClipboard.PrepareRestore();
                        ClipboardRetry(() => Clipboard.SetDataObject(originalClipboard.Data, true), NoteClipboardBusy);
                    }
                    originalClipboard.VerifyRestored(NoteClipboardBusy);
                    checks.Add("clipboard_original_restored");
                }
            }
            catch (Exception caught)
            {
                cleanupFailure = AddCleanupFailure(cleanupFailure, "clipboard: " + caught.Message);
                evidence["clipboard_cleanup_failure_stage"] = clipboardStage;
            }
            finally { originalClipboard?.Dispose(); }
            if (initialForeground != IntPtr.Zero && IsWindow(initialForeground)) SetForegroundWindow(initialForeground);
            if (failure != null) evidence["failure"] = failure;
            if (cleanupFailure != null) evidence["cleanup_failure"] = cleanupFailure;
            if (failure != null || cleanupFailure != null) evidence["status"] = "failed";
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            host?.Dispose();
        }
        Require(failure == null && cleanupFailure == null, "Native effects failed; see " + evidencePath);
    }

    private static Process StartHost(string hostPath, string displayPath, string agentControlPipe, string runDirectory)
    {
        var validation = Environment.ProcessPath!;
        var child = "\"" + validation + "\" --native-window-effects-child \"" + runDirectory + "\"";
        var info = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(child)),
            "--managed-shell", "custom", "--registry-pipe", agentControlPipe, "--working-directory-base64",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(runDirectory)), "--native-display", displayPath,
            "--terminal-state", "terminal_state_2_22_v2", "--diagnostics", "--timeout-ms", "180000" })
            info.ArgumentList.Add(argument);
        var host = Process.Start(info) ?? throw new InvalidOperationException("SessionHost could not start.");
        _ = host.StandardOutput.ReadToEndAsync();
        _ = host.StandardError.ReadToEndAsync();
        return host;
    }

    private static NativeWindowValidation.OwnedDisplayIdentity WaitOwnedDisplay(Process host,
        string hostPath, string displayPath)
    {
        var until = Stopwatch.StartNew();
        Exception? last = null;
        while (until.ElapsedMilliseconds < 15_000 && !host.HasExited)
        {
            try { return NativeWindowValidation.VerifyOwnedDisplay(host.Id, hostPath, displayPath); }
            catch (Exception error) { last = error; Thread.Sleep(100); }
        }
        throw new InvalidOperationException("Owned native window did not appear.", last);
    }

    private static void Trigger(string directory, string phase)
    {
        ParsePhase(phase + "\n");
        var ack = Path.Combine(directory, phase + ".ack.json");
        Require(!File.Exists(ack), "Effects phase ran twice.");
        var temp = Path.Combine(directory, phase + ".tmp");
        File.WriteAllText(temp, phase + "\n", Encoding.ASCII);
        File.Move(temp, Path.Combine(directory, "phase.trigger"));
        Require(SpinWait.SpinUntil(() => File.Exists(ack), 10_000), "Real ConPTY child did not flush " + phase);
        using var result = JsonDocument.Parse(File.ReadAllText(ack));
        Require(result.RootElement.GetProperty("phase").GetString() == phase &&
            result.RootElement.GetProperty("child_pid").GetInt32() > 0, "Child phase acknowledgement is invalid.");
    }

    private static void RecordPhase(NativeWindowValidation.OwnedDisplayIdentity owned, string directory,
        string recorderPath, string phase, IDictionary<string, object?> evidence)
    {
        Require(GetForegroundWindow() == owned.Window, "Native display was not foreground for sound phase.");
        var wav = Path.Combine(directory, phase + ".wav");
        var info = new ProcessStartInfo(recorderPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--record-loopback", wav, "5" }) info.ArgumentList.Add(argument);
        using var recorder = Process.Start(info) ?? throw new InvalidOperationException("Loopback recorder did not start.");
        var output = recorder.StandardOutput.ReadToEndAsync();
        try
        {
            var ready = recorder.StandardError.ReadLineAsync();
            Require(ready.Wait(10_000) && ready.Result?.StartsWith("LOOPBACK_READY ", StringComparison.Ordinal) == true,
                "Loopback recorder was not ready.");
            var endpoint = ready.Result![15..];
            var errors = recorder.StandardError.ReadToEndAsync();
            Thread.Sleep(350);
            var triggerAt = Stopwatch.GetTimestamp();
            Trigger(directory, phase);
            Require(recorder.WaitForExit(20_000) && recorder.ExitCode == 0, "Loopback recorder failed: " + phase);
            var metadata = output.Result;
            using var parsed = JsonDocument.Parse(metadata);
            Require(parsed.RootElement.GetProperty("EndpointId").GetString() == endpoint &&
                parsed.RootElement.GetProperty("WavPath").GetString() == wav && File.Exists(wav),
                "Loopback metadata disagreed with recording.");
            File.WriteAllText(Path.Combine(directory, phase + ".metadata.json"), metadata, Encoding.UTF8);
            ((List<object>)evidence["recordings"]!).Add(new { phase,
                file = Path.GetFileName(wav), metadata = phase + ".metadata.json",
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(wav))), endpoint,
                trigger_at = triggerAt });
            _ = errors.Result;
        }
        catch
        {
            if (!recorder.HasExited) recorder.Kill(true);
            throw;
        }
    }

    private static void StopOwnedHost(Process host, string expectedPath, long? expectedStartUtcTicks, string directory)
    {
        if (host.HasExited) { Require(host.ExitCode == 0, "Owned SessionHost exited with an error."); return; }
        void VerifyHostIdentity() => Require(expectedStartUtcTicks.HasValue &&
            host.StartTime.ToUniversalTime().Ticks == expectedStartUtcTicks.Value &&
            string.Equals(Path.GetFullPath(host.MainModule!.FileName), Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase), "SessionHost identity changed before cleanup.");
        VerifyHostIdentity();
        var exit = Path.Combine(directory, "phase.trigger");
        if (!File.Exists(exit)) File.WriteAllText(exit, "exit\n", Encoding.UTF8);
        host.StandardInput.Close();
        if (!host.WaitForExit(10_000))
        {
            VerifyHostIdentity();
            host.Kill(true);
            host.WaitForExit(5_000);
            throw new TimeoutException("Owned SessionHost required forced cleanup.");
        }
        Require(host.ExitCode == 0, "Owned SessionHost returned a nonzero exit code.");
    }

    private static void CloseOwnedDisplay(NativeWindowValidation.OwnedDisplayIdentity owned)
    {
        Process display;
        try { display = Process.GetProcessById(owned.DisplayPid); }
        catch (ArgumentException) { return; }
        using (display)
        {
            if (display.HasExited) return;
            display.Refresh();
            Require(display.StartTime.ToUniversalTime().Ticks == owned.DisplayStartUtcTicks &&
                string.Equals(Path.GetFullPath(display.MainModule!.FileName), owned.DisplayPath,
                    StringComparison.OrdinalIgnoreCase) && display.MainWindowHandle == owned.Window &&
                IsWindow(owned.Window) && GetWindowThreadProcessId(owned.Window, out var windowPid) != 0 &&
                windowPid == owned.DisplayPid, "Refusing to close a native display whose identity changed.");
            string? modalFailure = null;
            try { CloseOwnedClipboardModal(owned); }
            catch (Exception error) { modalFailure = error.Message; }
            if (!display.CloseMainWindow())
                throw new InvalidOperationException("Owned native display rejected close request; " +
                    (modalFailure ?? OwnedWindowSummary(owned.DisplayPid)));
            Require(display.WaitForExit(5_000), "Owned native display did not exit after close request.");
            Require(modalFailure == null, "Owned clipboard modal cleanup failed: " + modalFailure);
        }
    }

    private static void CloseOwnedClipboardModal(NativeWindowValidation.OwnedDisplayIdentity owned)
    {
        var handle = FindPrompt(owned.Window, owned.DisplayPid);
        if (handle != IntPtr.Zero)
        {
            Require(PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero),
                "Owned clipboard modal rejected WM_CLOSE.");
            Require(SpinWait.SpinUntil(() => !IsWindow(handle), 3_000),
                "Owned clipboard modal did not close in time.");
        }
    }

    private static bool BytesEqual(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);
    private static string AddCleanupFailure(string? prior, string next) => prior == null ? next : prior + "; " + next;
    private static object ClipboardWindows()
    {
        static object Describe(IntPtr window) => new
        {
            hwnd = window.ToInt64(),
            pid = window != IntPtr.Zero && GetWindowThreadProcessId(window, out var pid) != 0 ? (int?)pid : null,
        };
        return new { open = Describe(GetOpenClipboardWindow()), owner = Describe(GetClipboardOwner()) };
    }

    private static T ClipboardRetry<T>(Func<T> action, Action? onFirstBusy = null)
    {
        var deadline = Stopwatch.StartNew();
        var reportedBusy = false;
        while (true)
        {
            try { return action(); }
            catch (COMException error) when (error.HResult == unchecked((int)0x800401D0))
            {
                if (!reportedBusy) { onFirstBusy?.Invoke(); reportedBusy = true; }
                if (deadline.ElapsedMilliseconds >= 3_000) throw;
                PumpClipboardSta();
            }
        }
    }

    private static void PumpClipboardSta()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) { Thread.Sleep(50); return; }
        // The fixture owns OLE clipboard data, so a writer's WM_DESTROYCLIPBOARD must run during retries.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
    private static void ClipboardRetry(Action action, Action? onFirstBusy = null) =>
        ClipboardRetry(() => { action(); return true; }, onFirstBusy);

    private sealed class ClipboardSnapshot : IDisposable
    {
        private const long MaxCopiedBytes = 64L * 1024 * 1024;
        private readonly Dictionary<string, object> values;
        private readonly List<MemoryStream> streams;
        internal System.Windows.IDataObject? Data { get; }

        private ClipboardSnapshot(System.Windows.IDataObject? data, Dictionary<string, object> values,
            List<MemoryStream> streams)
        { Data = data; this.values = values; this.streams = streams; }

        internal static void SelfTest()
        {
            using var source = new MemoryStream([1, 2, 3]);
            var copies = new List<MemoryStream>();
            var copy = (MemoryStream)CopyValue(source, "selftest", copies);
            source.Position = 0;
            source.WriteByte(9);
            Require(copy.ToArray().AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }),
                "Clipboard stream snapshot retained a live source reference.");
            foreach (var stream in copies) stream.Dispose();
        }

        internal static ClipboardSnapshot Capture(Action? onFirstBusy = null)
        {
            var source = ClipboardRetry<System.Windows.IDataObject?>(() => Clipboard.GetDataObject(), onFirstBusy);
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            var streams = new List<MemoryStream>();
            if (source == null) return new ClipboardSnapshot(null, values, streams);
            try
            {
                var formats = ClipboardRetry<string[]>(() => source.GetFormats(false), onFirstBusy);
                Require(formats.Distinct(StringComparer.Ordinal).Count() == formats.Length,
                    "Clipboard advertised duplicate formats.");
                if (formats.Length == 0) return new ClipboardSnapshot(null, values, streams);
                var data = new System.Windows.DataObject();
                foreach (var format in formats)
                {
                    Require(!string.IsNullOrWhiteSpace(format), "Clipboard advertised an empty format.");
                    var value = ClipboardRetry<object?>(() => source.GetData(format, false), onFirstBusy);
                    Require(value != null, "Clipboard format could not be materialized: " + format);
                    var copy = CopyValue(value, format, streams);
                    data.SetData(format, copy, false);
                    values.Add(format, copy);
                }
                Require(data.GetFormats(false).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                    formats.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal),
                    "Clipboard formats could not be represented independently.");
                return new ClipboardSnapshot(data, values, streams);
            }
            catch
            {
                foreach (var stream in streams) stream.Dispose();
                throw;
            }
        }

        private static object CopyValue(object value, string format, List<MemoryStream> streams)
        {
            switch (value)
            {
                case string text: return text;
                case string[] paths: return paths.ToArray();
                case byte[] bytes when bytes.LongLength <= MaxCopiedBytes: return bytes.ToArray();
                case Stream source when source.CanSeek && source.Length <= MaxCopiedBytes:
                    var position = source.Position;
                    try
                    {
                        source.Position = 0;
                        var copy = new MemoryStream();
                        var buffer = new byte[8192];
                        int count;
                        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            Require(copy.Length + count <= MaxCopiedBytes,
                                "Clipboard stream exceeded the safe snapshot limit: " + format);
                            copy.Write(buffer, 0, count);
                        }
                        copy.Position = 0;
                        streams.Add(copy);
                        return copy;
                    }
                    finally { source.Position = position; }
                case BitmapSource bitmap when BitmapByteCount(bitmap) <= MaxCopiedBytes:
                    var image = new WriteableBitmap(bitmap);
                    image.Freeze();
                    return image;
                default:
                    throw new InvalidOperationException("Clipboard format cannot be copied safely: " + format +
                        " (" + value.GetType().FullName + ").");
            }
        }

        private static long BitmapByteCount(BitmapSource bitmap) =>
            ((long)bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8 * bitmap.PixelHeight;

        internal void PrepareRestore()
        {
            foreach (var stream in streams) stream.Position = 0;
        }

        internal void VerifyRestored(Action? onFirstBusy = null)
        {
            using var actual = Capture(onFirstBusy);
            Require(values.Count == actual.values.Count &&
                values.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                    actual.values.Keys.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal),
                "Restored clipboard formats differ from the original.");
            foreach (var (format, expected) in values)
                Require(EqualValue(expected, actual.values[format]),
                    "Restored clipboard format differs from the original: " + format);
        }

        private static bool EqualValue(object expected, object actual) => (expected, actual) switch
        {
            (string a, string b) => a == b,
            (string[] a, string[] b) => a.SequenceEqual(b, StringComparer.Ordinal),
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            (MemoryStream a, MemoryStream b) => a.ToArray().AsSpan().SequenceEqual(b.ToArray()),
            (BitmapSource a, BitmapSource b) => a.PixelWidth == b.PixelWidth &&
                a.PixelHeight == b.PixelHeight && a.Format == b.Format && a.DpiX == b.DpiX &&
                a.DpiY == b.DpiY && (a.Palette?.Colors?.ToArray() ?? Array.Empty<System.Windows.Media.Color>())
                    .SequenceEqual(b.Palette?.Colors?.ToArray() ?? Array.Empty<System.Windows.Media.Color>()) &&
                BitmapPixels(a).AsSpan().SequenceEqual(BitmapPixels(b)),
            _ => false,
        };

        private static byte[] BitmapPixels(BitmapSource bitmap)
        {
            var stride = checked((int)(((long)bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8));
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        public void Dispose()
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void RequireThrows(Action action, string message)
    { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException(message); }

    private static bool[] ReadSettings(string path)
    {
        if (!File.Exists(path)) return [false, false, false, false];
        var bytes = File.ReadAllBytes(path);
        Require((bytes.Length == 3 || bytes.Length == 4) &&
            ((bytes[0] == 1 && bytes.Length == 3) || (bytes[0] == 2 && bytes.Length == 4)) &&
            bytes.Skip(1).All(b => b <= 1), "Native settings format is invalid.");
        return [false, bytes[1] == 1, bytes[2] == 1, bytes.Length == 4 && bytes[3] == 1];
    }

    private static void EnsureSetting(NativeWindowValidation.OwnedDisplayIdentity owned, string path,
        int field, bool desired, List<byte[]?> allowed, byte[]? prior, out byte[]? updated)
    {
        var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Require(BytesEqual(current, prior), "Native settings changed outside this test.");
        var flags = ReadSettings(path);
        if (flags[field] != desired)
        {
            flags[field] = desired;
            allowed.Add([2, (byte)(flags[1] ? 1 : 0), (byte)(flags[2] ? 1 : 0), (byte)(flags[3] ? 1 : 0)]);
            ClickOwned(owned.Window, owned.DisplayPid, "提醒");
            ClickOwned(owned.Window, owned.DisplayPid, field switch {
                1 => "本机终端提醒", 2 => "本机终端提示音", 3 => "允许终端请求复制（逐次确认）", _ => throw new ArgumentOutOfRangeException(nameof(field)) });
            Require(SpinWait.SpinUntil(() => ReadSettings(path)[field] == desired, 3_000),
                "Native setting did not persist.");
            ClickOwnedWindowCenter(owned.Window, owned.DisplayPid, "dismiss settings");
        }
        updated = File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static void EnsureMainForeground(NativeWindowValidation.OwnedDisplayIdentity owned, string phase)
    {
        Require(IsWindow(owned.Window) && GetWindowThreadProcessId(owned.Window, out var pid) != 0 &&
            pid == owned.DisplayPid, "Owned main window changed before " + phase);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetForegroundWindow() == owned.Window && !AlertFlyoutVisible(owned.Window, owned.DisplayPid)) return;
            ClickOwnedWindowCenter(owned.Window, owned.DisplayPid, "focus main before " + phase);
            Thread.Sleep(120);
        }
        if (GetForegroundWindow() != owned.Window || AlertFlyoutVisible(owned.Window, owned.DisplayPid))
            throw new InvalidOperationException("Owned main window or alert Flyout is not ready for " + phase +
                "; " + OwnedWindowSummary(owned.DisplayPid));
    }

    private static bool AlertFlyoutVisible(IntPtr mainWindow, int displayPid)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, displayPid));
        foreach (AutomationElement root in windows)
        {
            var handle = new IntPtr(root.Current.NativeWindowHandle);
            if (handle != mainWindow && GetWindow(handle, 4) != mainWindow) continue;
            foreach (var label in new[] { "本机终端提醒", "本机终端提示音", "允许终端请求复制（逐次确认）" })
            {
                var matches = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, label));
                foreach (AutomationElement match in matches)
                    if (!match.Current.IsOffscreen) return true;
            }
        }
        return false;
    }

    private static IntPtr WaitPrompt(IntPtr mainWindow, int displayPid, bool required, string phase)
    {
        IntPtr found = IntPtr.Zero;
        var appeared = SpinWait.SpinUntil(() => { found = FindPrompt(mainWindow, displayPid); return found != IntPtr.Zero; },
            required ? 8_000 : 2_000);
        if (required && !appeared)
            throw new InvalidOperationException("Expected owned clipboard prompt did not appear for " + phase +
                "; " + OwnedWindowSummary(displayPid));
        return found;
    }

    private static bool PromptExists(IntPtr mainWindow, int displayPid) => FindPrompt(mainWindow, displayPid) != IntPtr.Zero;

    private static IntPtr FindPrompt(IntPtr mainWindow, int displayPid)
    {
        Require(IsWindow(mainWindow) && GetWindowThreadProcessId(mainWindow, out var mainPid) != 0 &&
            mainPid == displayPid, "Owned main window changed while finding clipboard prompt.");
        var candidates = new List<IntPtr>();
        Require(EnumWindows((handle, _) => {
            if (handle == mainWindow || GetWindow(handle, 4) != mainWindow || !IsWindowVisible(handle) ||
                GetWindowThreadProcessId(handle, out var pid) == 0 || pid != displayPid) return true;
            var title = new StringBuilder(128);
            if (GetWindowText(handle, title, title.Capacity) > 0 && title.ToString() == "终端请求复制")
                candidates.Add(handle);
            return true;
        }, IntPtr.Zero), "Could not enumerate owned native display windows for clipboard prompt.");
        Require(candidates.Count <= 1, "More than one owned clipboard prompt appeared.");
        return candidates.Count == 0 ? IntPtr.Zero : candidates[0];
    }

    private static string OwnedWindowSummary(int displayPid)
    {
        try
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, displayPid));
            var result = new List<string>();
            foreach (AutomationElement window in windows.Cast<AutomationElement>().Take(8))
            {
                var current = window.Current;
                var handle = new IntPtr(current.NativeWindowHandle);
                if (handle == IntPtr.Zero || GetWindowThreadProcessId(handle, out var pid) == 0 || pid != displayPid)
                    continue;
                var name = new string((current.Name ?? "").Where(c => !char.IsControl(c)).Take(80).ToArray());
                bool Has(string label) => window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, label)).Count > 0;
                result.Add("hwnd=" + handle.ToInt64() + ",name=" + name +
                    ",enabled=" + current.IsEnabled + ",offscreen=" + current.IsOffscreen +
                    ",cancel=" + Has("取消") + ",copy_once=" + Has("仅复制本次") +
                    ",alerts=" + Has("提醒"));
            }
            return "foreground=" + GetForegroundWindow().ToInt64() + "; owned_windows=[" +
                string.Join(" | ", result) + "]";
        }
        catch (Exception error) { return "owned_window_summary_error=" + error.GetType().Name; }
    }

    private static void ClickOwned(IntPtr root, int displayPid, string label)
    {
        var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Require(previousDpiContext != IntPtr.Zero, "Could not use physical UIA bounds: " + label);
        try { ClickOwnedDpiAware(root, displayPid, label); }
        finally { Require(SetThreadDpiAwarenessContext(previousDpiContext) != IntPtr.Zero,
            "Could not restore DPI context: " + label); }
    }

    private static void ClickOwnedDpiAware(IntPtr root, int displayPid, string label)
    {
        Require(GetWindowThreadProcessId(root, out var ownerPid) != 0 && ownerPid == displayPid,
            "UI root does not belong to the isolated native display.");
        var element = AutomationElement.FromHandle(root) ?? throw new InvalidOperationException("Owned UI root vanished.");
        var named = new PropertyCondition(AutomationElement.NameProperty, label);
        var matches = element.FindAll(TreeScope.Descendants, named).Cast<AutomationElement>().ToList();
        if (matches.Count == 0)
        {
            var ownedWindows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, displayPid));
            foreach (AutomationElement ownedWindow in ownedWindows)
                matches.AddRange(ownedWindow.FindAll(TreeScope.Descendants, named).Cast<AutomationElement>());
        }
        Require(matches.Count == 1 && matches[0].Current.ProcessId == displayPid && matches[0].Current.IsEnabled,
            "Expected owned control is missing: " + label);
        var rectangle = matches[0].Current.BoundingRectangle;
        Require(!rectangle.IsEmpty && rectangle.Width >= 8 && rectangle.Height >= 8,
            "Owned control has no usable UIA bounds: " + label);
        var x = (int)(rectangle.Left + rectangle.Width / 2);
        var y = (int)(rectangle.Top + rectangle.Height / 2);
        ClickOwnedPoint(root, displayPid, x, y, label);
    }

    private static void ClickOwnedWindowCenter(IntPtr root, int displayPid, string label)
    {
        var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Require(previousDpiContext != IntPtr.Zero, "Could not use physical window bounds: " + label);
        try
        {
            Require(GetWindowRect(root, out var frame), "Owned display bounds are unavailable.");
            ClickOwnedPoint(root, displayPid, frame.Left + (frame.Right - frame.Left) / 2,
                frame.Top + (frame.Bottom - frame.Top) / 2, label);
        }
        finally { Require(SetThreadDpiAwarenessContext(previousDpiContext) != IntPtr.Zero,
            "Could not restore DPI context: " + label); }
    }

    private static void ClickOwnedPoint(IntPtr root, int displayPid, int x, int y, string label)
    {
        var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Require(previousDpiContext != IntPtr.Zero, "Could not use physical display pixels: " + label);
        try
        {
            Require(GetWindowThreadProcessId(root, out var rootPid) != 0 && rootPid == displayPid,
                "UI root changed ownership: " + label);
            Require(GetCursorPos(out var before), "Could not preserve cursor: " + label);
            var wasTopmost = (GetWindowLong(root, -20) & 0x00000008) != 0;
            var raised = false;
            try
            {
                if (GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid) == 0 || foregroundPid != displayPid)
                {
                    Require(SetWindowPos(root, new IntPtr(-1), 0, 0, 0, 0, 0x0013),
                        "Could not raise owned UI root: " + label);
                    raised = !wasTopmost;
                    BringWindowToTop(root);
                    SetForegroundWindow(root);
                }
                var hit = WindowFromPoint(new PointI(x, y));
                Require(SetCursorPos(x, y) && GetWindowThreadProcessId(hit, out var hitPid) != 0 &&
                    hitPid == displayPid, "UIA control hit point is outside the owned display: " + label);
                var foregroundWasOwned = GetWindowThreadProcessId(GetForegroundWindow(), out var focusedPid) != 0 &&
                    focusedPid == displayPid;
                var input = new[] { new Input { Type = 0, Mouse = new MouseInput { Flags = 2 } },
                    new Input { Type = 0, Mouse = new MouseInput { Flags = 4 } } };
                Thread.Sleep(50);
                Require(SendInput(1, input[..1], Marshal.SizeOf<Input>()) == 1,
                    "Owned UI mouse-down failed: " + label);
                Thread.Sleep(35);
                Require(WindowFromPoint(new PointI(x, y)) == hit &&
                    GetWindowThreadProcessId(hit, out hitPid) != 0 && hitPid == displayPid,
                    "Owned UI target changed before mouse-up: " + label);
                Require(SendInput(1, input[1..], Marshal.SizeOf<Input>()) == 1,
                    "Owned UI mouse-up failed: " + label);
                Thread.Sleep(120);
                Require(SpinWait.SpinUntil(() => GetWindowThreadProcessId(GetForegroundWindow(), out var currentPid) != 0 &&
                    currentPid == displayPid, 2_000),
                    "Owned UI root could not gain focus after verified click: " + label +
                    "; foreground_was_owned=" + foregroundWasOwned);
            }
            finally
            {
                var restoredTopmost = !raised || !IsWindow(root) ||
                    SetWindowPos(root, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
                SetCursorPos(before.X, before.Y);
                Require(restoredTopmost, "Could not restore owned UI topmost state: " + label);
            }
        }
        finally { Require(SetThreadDpiAwarenessContext(previousDpiContext) != IntPtr.Zero,
            "Could not restore DPI context: " + label); }
    }

    private static void Capture(IntPtr window, int displayPid, string file, List<object> evidence)
    {
        var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Require(previousDpiContext != IntPtr.Zero, "Could not use physical screen pixels for screenshot.");
        try
        {
            Require(IsWindow(window) && GetWindowThreadProcessId(window, out var pid) != 0 && pid == displayPid &&
                SpinWait.SpinUntil(() => GetForegroundWindow() == window, 2_000),
                "Owned screenshot window is not foreground.");
            Require(GetWindowRect(window, out var bounds), "Owned screenshot window has no bounds.");
            var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
            Require(width is >= 100 and <= 4096 && height is >= 100 and <= 4096,
                "Owned screenshot window size is invalid.");
            Require(GetWindowThreadProcessId(WindowFromPoint(new PointI(bounds.Left + width / 2,
                bounds.Top + height / 2)), out var hitPid) != 0 && hitPid == displayPid,
                "Owned screenshot center is hidden by another process.");
            var source = GetDC(IntPtr.Zero);
            Require(source != IntPtr.Zero, "Desktop screenshot source is unavailable.");
            IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, selected = IntPtr.Zero;
            try
            {
                memory = CreateCompatibleDC(source); bitmap = CreateCompatibleBitmap(source, width, height);
                Require(memory != IntPtr.Zero && bitmap != IntPtr.Zero,
                    "Owned screenshot capture could not allocate a bitmap.");
                selected = SelectObject(memory, bitmap);
                Require(BitBlt(memory, 0, 0, width, height, source, bounds.Left, bounds.Top, 0x00CC0020),
                    "Visible desktop screenshot capture failed.");
                var frame = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
                var stride = checked(width * 3);
                var bytes = new byte[checked(stride * height)];
                pixels.CopyPixels(bytes, stride, 0);
                var visiblePixels = 0;
                for (var offset = 0; offset < bytes.Length; offset += 3)
                    if (Math.Max(bytes[offset], Math.Max(bytes[offset + 1], bytes[offset + 2])) > 20)
                        visiblePixels++;
                Require(visiblePixels >= 100, "Visible desktop screenshot is black or empty.");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
                using (var stream = File.Create(file)) encoder.Save(stream);
                evidence.Add(new { file = Path.GetFileName(file),
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), visible_pixels = visiblePixels });
            }
            finally
            {
                if (selected != IntPtr.Zero) SelectObject(memory, selected);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, source);
            }
        }
        finally { Require(SetThreadDpiAwarenessContext(previousDpiContext) != IntPtr.Zero,
            "Could not restore DPI context after screenshot."); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointI
    { public int X, Y; public PointI(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public int Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y, Data, Flags, Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointI point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PointI point);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RectI rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint mode);
}
