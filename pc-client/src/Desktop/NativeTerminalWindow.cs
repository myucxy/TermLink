using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using TermLink.NativeDisplay;
using TermLink.TerminalCore;

namespace TermLink.Desktop;

/// <summary>Opt-in native terminal window, attached to one existing SessionHost authority.</summary>
public sealed class NativeTerminalWindow : Window
{
    private readonly NativeTerminalSurface surface = new();
    private readonly NativeTerminalDisplayFrameDecoder frames = new();
    private readonly Border terminalBorder = new() { BorderThickness = new Thickness(1) };
    private readonly Border terminalInnerBorder = new() { BorderThickness = new Thickness(1) };
    private readonly TextBlock status = new() { IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button shellCommands = new() { Content = "命令定位", Padding = new Thickness(7, 1), FontSize = 11 };
    private readonly Button resumeInput = new() { Content = "检查后继续输入", Padding = new Thickness(7, 1), FontSize = 11, IsVisible = false };
    private readonly TextBlock signalStatus = new() { FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly NativeTerminalAlerts alerts = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLink"));
    private readonly NativeTerminalClipboard terminalClipboard;
    private long noticeUntil;
    private readonly CancellationTokenSource lifetime = new();
    private readonly NativeTerminalInputQueue requests = new();
    private readonly string pipeName;
    private readonly int hostPid;
    private uint scrollback;
    private long scrollGeneration;
    private long localGestureGeneration;
    private long reliableInputRejectCount;
    private bool reliableInputLatched;
    private bool started;

    public NativeTerminalWindow(string pipeName, int hostPid)
    {
        if (!pipeName.StartsWith("termlink-display-", StringComparison.Ordinal) || pipeName.Length != 49 || hostPid <= 0)
            throw new ArgumentException("Invalid native terminal attachment.");
        this.pipeName = pipeName; this.hostPid = hostPid;
        terminalClipboard = new NativeTerminalClipboard(this, () => alerts.ClipboardEnabled,
            message => { status.Text = message; status.IsVisible = true; });
        Title = "TermLink · CLI"; Width = 1000; Height = 650; MinWidth = 240; MinHeight = 160;
        terminalInnerBorder.Child = surface; terminalBorder.Child = terminalInnerBorder;
        var directory = new Button { Content = "当前目录", Width = 70, Padding = new Thickness(7, 1), FontSize = 11 };
        directory.Click += (_, _) => Queue(NativeDisplayProtocol.ShellQuery, [0]);
        shellCommands.Width = 70;
        shellCommands.Click += (_, _) => Queue(NativeDisplayProtocol.ShellQuery, [1]);
        resumeInput.Click += (_, _) => ResumeReliableInput();
        resumeInput.Margin = new Thickness(0, 0, 4, 0);
        var menu = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, MinHeight = 24 };
        directory.Margin = new Thickness(0, 0, 4, 0);
        shellCommands.Margin = new Thickness(0, 0, 4, 0);
        menu.Children.Add(directory); menu.Children.Add(shellCommands); menu.Children.Add(resumeInput);
        var alertMenu = new Button { Content = "提醒", Width = 46, Padding = new Thickness(7, 1), FontSize = 11 };
        alertMenu.Click += (_, _) => {
            var settings = alerts.Settings();
            var notify = new CheckBox { Content = "本机终端提醒", IsChecked = settings.Alerts, FontSize = 12, MinHeight = 32 };
            var audio = new CheckBox { Content = "本机终端提示音", IsChecked = settings.Sounds, FontSize = 12, MinHeight = 32 };
            var copy = new CheckBox { Content = "允许终端请求复制（逐次确认）", IsChecked = alerts.ClipboardEnabled, FontSize = 12, MinHeight = 32 };
            void Save() {
                if (!alerts.Set(notify.IsChecked == true, audio.IsChecked == true, copy.IsChecked == true)) {
                    status.Text = "提醒设置保存失败，提醒和声音保持关闭。"; status.IsVisible = true;
                }
            }
            notify.IsCheckedChanged += (_, _) => Save(); audio.IsCheckedChanged += (_, _) => Save();
            copy.IsCheckedChanged += (_, _) => Save();
            var content = new StackPanel { Margin = new Thickness(10, 4) }; content.Children.Add(notify); content.Children.Add(audio); content.Children.Add(copy);
            var flyout = new Flyout { Content = content }; flyout.ShowAt(alertMenu);
        };
        alertMenu.Margin = new Thickness(0, 0, 4, 0);
        menu.Children.Add(alertMenu);
        var themeMenu = new Button { Content = "主题", Width = 46, Padding = new Thickness(7, 1), FontSize = 11 };
        themeMenu.Margin = new Thickness(0, 0, 4, 0);
        var themeChoices = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4, IsVisible = false, Margin = new Thickness(0, 0, 4, 0) };
        themeMenu.Click += (_, _) => themeChoices.IsVisible = !themeChoices.IsVisible;
        foreach (var (label, width, variant) in new (string, double, Avalonia.Styling.ThemeVariant)[] {
            ("跟随系统", 65, Avalonia.Styling.ThemeVariant.Default),
            ("浅色", 44, Avalonia.Styling.ThemeVariant.Light),
            ("深色", 44, Avalonia.Styling.ThemeVariant.Dark),
        }) {
            var choice = new Button { Content = label, Width = width, Padding = new Thickness(4, 1), FontSize = 11 };
            choice.Click += (_, _) => { RequestedThemeVariant = variant; themeChoices.IsVisible = false; };
            themeChoices.Children.Add(choice);
        }
        menu.Children.Add(themeMenu); menu.Children.Add(themeChoices); menu.Children.Add(signalStatus);
        SizeChanged += (_, _) => {
            var narrow = ClientSize.Width < 420;
            directory.Width = shellCommands.Width = narrow ? 60 : 70;
            alertMenu.Width = themeMenu.Width = narrow ? 40 : 46;
        };
        var panel = new DockPanel(); DockPanel.SetDock(menu, Dock.Top); panel.Children.Add(menu);
        DockPanel.SetDock(status, Dock.Top); panel.Children.Add(status); panel.Children.Add(terminalBorder); Content = panel;
        surface.TextRequested += text => { GoLive(); Queue(NativeDisplayProtocol.Text, NativeDisplayProtocol.Utf8.GetBytes(text)); };
        surface.KeyRequested += SendKey;
        surface.MouseRequested += SendMouse;
        surface.HistoryScrollRequested += rows => {
            scrollback = (uint)Math.Clamp((long)scrollback + rows, 0, 10_000); Interlocked.Increment(ref scrollGeneration);
            surface.ViewingHistory = scrollback != 0;
        };
        Opened += (_, _) => { surface.Focus(); if (!started) { started = true; _ = RunAsync(); } };
        Closed += (_, _) => { lifetime.Cancel(); requests.Complete(); alerts.Dispose(); terminalClipboard.Dispose(); };
        Activated += (_, _) => Queue(NativeDisplayProtocol.Focus, [1]);
        Deactivated += (_, _) => Queue(NativeDisplayProtocol.Focus, [0]);
        KeyDown += OnWindowKey;
    }

    private void GoLive() { scrollback = 0; surface.ViewingHistory = false; Interlocked.Increment(ref scrollGeneration); }

    private void SendMouse(NativeTerminalMouseEvent input) {
        var payload = new byte[20]; payload[0] = (byte)input.Action;
        payload[1] = (byte)(input.Button.HasValue ? (uint)input.Button.Value : input.WheelDirection.HasValue ? (uint)input.WheelDirection.Value : 0);
        payload[2] = (byte)input.ButtonsHeld; payload[3] = (byte)input.Modifiers;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), input.Position.CellColumn);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), input.Position.CellRow);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), input.Position.PixelX);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), input.Position.PixelY);
        Queue(NativeDisplayProtocol.Mouse, payload);
    }

    private void Queue(byte kind, byte[] payload)
    {
        if (lifetime.IsCancellationRequested) return;
        var reliable = kind is NativeDisplayProtocol.ConsoleKey or NativeDisplayProtocol.Text or NativeDisplayProtocol.Paste;
        if (reliable && reliableInputLatched)
        {
            CountReliableInputReject();
            ShowReliableInputBlocked();
            return;
        }
        if (!requests.TryWrite(kind, payload))
        {
            if (reliable)
            {
                reliableInputLatched = true;
                CountReliableInputReject();
                ShowReliableInputBlocked();
            }
            else
            {
                status.Text = "输入未发送：终端暂时繁忙，请重试。"; status.IsVisible = true;
            }
            return;
        }
        if (kind is NativeDisplayProtocol.ConsoleKey or NativeDisplayProtocol.Text or NativeDisplayProtocol.Paste ||
            kind == NativeDisplayProtocol.Mouse && payload.Length >= 3 &&
            (payload[0] != (byte)NativeTerminalMouseAction.Move || payload[2] != 0)) localGestureGeneration++;
    }

    private void CountReliableInputReject()
    {
        if (reliableInputRejectCount < long.MaxValue) reliableInputRejectCount++;
    }

    private void ShowReliableInputBlocked()
    {
        status.Text = $"输入已暂停：已拒绝 {reliableInputRejectCount} 项。请检查命令后点击“检查后继续输入”。";
        status.IsVisible = true;
        resumeInput.IsVisible = true;
    }

    private void ResumeReliableInput()
    {
        reliableInputLatched = false;
        reliableInputRejectCount = 0;
        resumeInput.IsVisible = false;
        status.IsVisible = false;
    }

    private void SendKey(NativeTerminalKey key, NativeTerminalModifiers modifiers)
    {
        ushort vk = key switch {
            NativeTerminalKey.Enter => 13, NativeTerminalKey.Backspace => 8, NativeTerminalKey.Tab => 9,
            NativeTerminalKey.Escape => 27, NativeTerminalKey.Delete => 46, NativeTerminalKey.Insert => 45,
            NativeTerminalKey.Home => 36, NativeTerminalKey.End => 35, NativeTerminalKey.PageUp => 33,
            NativeTerminalKey.PageDown => 34, NativeTerminalKey.ArrowUp => 38, NativeTerminalKey.ArrowDown => 40,
            NativeTerminalKey.ArrowLeft => 37, NativeTerminalKey.ArrowRight => 39,
            >= NativeTerminalKey.F1 and <= NativeTerminalKey.F12 => (ushort)(112 + key - NativeTerminalKey.F1),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        SendVirtualKey(vk, modifiers);
    }

    private void SendVirtualKey(ushort vk, NativeTerminalModifiers modifiers)
    {
        GoLive();
        var payload = new byte[13]; BinaryPrimitives.WriteUInt16LittleEndian(payload, vk); payload[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), (ushort)MapVirtualKey(vk, 0));
        var character = vk switch { 13 => '\r', 8 => '\b', 9 => '\t', 27 => '\x1b', _ => '\0' };
        if (modifiers.HasFlag(NativeTerminalModifiers.Control) && vk is >= 65 and <= 90)
            character = (char)(vk - 64);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), character);
        uint control = 0;
        if (vk is >= 33 and <= 40 or 45 or 46) control |= 0x100;
        if (modifiers.HasFlag(NativeTerminalModifiers.Control)) control |= 8;
        if (modifiers.HasFlag(NativeTerminalModifiers.Alt)) control |= 2;
        if (modifiers.HasFlag(NativeTerminalModifiers.Shift)) control |= 16;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7), control); payload[11] = 1;
        var pair = new byte[26]; payload.CopyTo(pair, 0); payload.CopyTo(pair, 13); pair[19] = 0;
        Queue(NativeDisplayProtocol.ConsoleKey, pair);
    }

    private async void OnWindowKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key is Key.C or Key.V)
        {
            e.Handled = true;
            if (Clipboard is null) return;
            try {
                if (e.Key == Key.C) await Clipboard.SetTextAsync(surface.SelectedText());
                else {
                    var text = await Clipboard.TryGetTextAsync();
                    if (text is not null) { GoLive(); Queue(NativeDisplayProtocol.Paste, NativeDisplayProtocol.Utf8.GetBytes(text)); }
                }
            }
            catch (Exception error) {
                status.Text = "剪贴板操作失败：" + error.Message; status.IsVisible = true;
            }
        }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.M) {
            e.Handled = true; Queue(NativeDisplayProtocol.ShellQuery, [1]);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key >= Key.A && e.Key <= Key.Z)
        {
            e.Handled = true; SendVirtualKey((ushort)(65 + e.Key - Key.A), NativeTerminalModifiers.Control |
                (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? NativeTerminalModifiers.Shift : 0) |
                (e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? NativeTerminalModifiers.Alt : 0));
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15_000, lifetime.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var actualPid) || actualPid != hostPid)
                throw new IOException("终端展示连接的进程身份不匹配。");
            ulong revision = 0; long generation = -1; (int Columns, int Rows) previousSize = default;
            NativeTerminalHostGeometryV1? previousGeometry = null;
            bool? previousDarkTheme = null;
            while (!lifetime.IsCancellationRequested)
            {
                var dark = await Dispatcher.UIThread.InvokeAsync(() =>
                    ActualThemeVariant != Avalonia.Styling.ThemeVariant.Light);
                if (dark != previousDarkTheme)
                {
                    var reply = await ExchangeAsync(pipe, NativeDisplayProtocol.HostTheme,
                        NativeTerminalSurface.HostTheme(dark).Encode()).ConfigureAwait(false);
                    if (reply.Kind != NativeDisplayProtocol.Accepted) throw new IOException("终端未接受主题状态。");
                    previousDarkTheme = dark;
                }
                // Keep rendering/remote fit responsive even during a continuous drag or paste.
                for (var sent = 0; sent < 8 && requests.TryRead(out var request); sent++)
                {
                    var reply = await ExchangeAsync(pipe, request.Kind, request.Payload).ConfigureAwait(false);
                    if (reply.Kind == NativeDisplayProtocol.ShellState && request.Kind == NativeDisplayProtocol.ShellQuery) {
                        if (reply.Payload.Length < 40) throw new IOException("无效的 Shell 状态。");
                        var state = NativeTerminalShellState.Decode(reply.Payload.AsSpan(8).ToArray(), BinaryPrimitives.ReadUInt64LittleEndian(reply.Payload));
                        await Dispatcher.UIThread.InvokeAsync(() => ShowShellState(state, request.Payload[0] == 0));
                        continue;
                    }
                    if (reply.Kind == NativeDisplayProtocol.ShellAnchor && request.Kind == NativeDisplayProtocol.ShellSeek) {
                        NativeDisplayProtocol.RequireSize(reply.Payload, 4);
                        var target = BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload);
                        if (target > 10000) throw new IOException("无效的命令历史位置。");
                        await Dispatcher.UIThread.InvokeAsync(() => { scrollback = target; surface.ViewingHistory = target != 0; Interlocked.Increment(ref scrollGeneration); });
                        continue;
                    }
                    if (reply.Kind == NativeDisplayProtocol.Rejected) await Dispatcher.UIThread.InvokeAsync(() => {
                        var diagnostic = reply.Payload.Length != 0 ? NativeDisplayProtocol.Utf8.GetString(reply.Payload) : null;
                        status.Text = diagnostic?.StartsWith("TL-INPUT-DIAG/1\ndirection=input_encoding\n", StringComparison.Ordinal) == true
                            ? "输入未处理：\n" + diagnostic : "操作未完成：终端状态已变化，请重试。";
                        status.IsVisible = true;
                    });
                    else if (reply.Kind != NativeDisplayProtocol.Accepted) throw new IOException("终端返回了无效的输入确认。");
                }
                var size = await Dispatcher.UIThread.InvokeAsync(() => surface.MeasureGrid(surface.Bounds.Size));
                if (size != previousSize && size.Columns >= 2 && size.Rows >= 2)
                {
                    var dimensions = new byte[4]; BinaryPrimitives.WriteUInt16LittleEndian(dimensions, (ushort)size.Columns);
                    BinaryPrimitives.WriteUInt16LittleEndian(dimensions.AsSpan(2), (ushort)size.Rows);
                    var reply = await ExchangeAsync(pipe, NativeDisplayProtocol.Resize, dimensions).ConfigureAwait(false);
                    if (reply.Kind == NativeDisplayProtocol.Accepted) previousSize = size;
                    else if (reply.Kind != NativeDisplayProtocol.Rejected) throw new IOException("终端返回了无效的尺寸确认。");
                }
                var geometry = await Dispatcher.UIThread.InvokeAsync(MeasureHostGeometry);
                if (geometry != previousGeometry)
                {
                    var reply = await ExchangeAsync(pipe, NativeDisplayProtocol.HostGeometry, geometry.Encode()).ConfigureAwait(false);
                    if (reply.Kind != NativeDisplayProtocol.Accepted) throw new IOException("终端未接受显示尺寸状态。");
                    previousGeometry = geometry;
                }
                var (currentGeneration, offset) = await Dispatcher.UIThread.InvokeAsync(() => (scrollGeneration, scrollback));
                var query = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(query, generation == currentGeneration ? revision : 0);
                BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(8), offset);
                var response = await ExchangeAsync(pipe, NativeDisplayProtocol.Render, query).ConfigureAwait(false);
                if (response.Kind == NativeDisplayProtocol.FitWindow)
                {
                    previousSize = await FitWindowAsync(pipe, response.Payload).ConfigureAwait(false);
                    revision = 0;
                }
                else if (response.Kind is NativeDisplayProtocol.Frame or NativeDisplayProtocol.FrameV5)
                {
                    var decoded = frames.Decode(response.Kind, response.Payload);
                    var frame = decoded.Text;
                    revision = frame.PresentationRevision; generation = currentGeneration;
                    await Dispatcher.UIThread.InvokeAsync(() => {
                        if (currentGeneration != scrollGeneration) return;
                        if (reliableInputLatched) ShowReliableInputBlocked();
                        else status.IsVisible = false;
                        if (decoded.Graphics is null) surface.Present(frame);
                        else surface.Present(decoded.Graphics);
                        var foreground = surface.DefaultColor(frame.Palette.FrameForegroundAlias ?? 256);
                        var background = surface.DefaultColor(frame.Palette.FrameBackgroundAlias ?? 257);
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(0xff000000U | (uint)foreground));
                        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(0xff000000U | (uint)background));
                        terminalBorder.BorderBrush = Foreground;
                        terminalInnerBorder.BorderBrush = Background;
                        Title = string.IsNullOrWhiteSpace(frame.Title) ? "TermLink · CLI" : frame.Title + " · TermLink";
                    });
                }
                else if (response.Kind == NativeDisplayProtocol.HostResizeNotice)
                {
                    if (response.Payload.Length > 1024) throw new System.IO.InvalidDataException("Host resize notice exceeds its bound.");
                    var notice = NativeDisplayProtocol.Utf8.GetString(response.Payload);
                    await Dispatcher.UIThread.InvokeAsync(() => { status.Text = notice; status.IsVisible = true; });
                }
                else if (response.Kind is NativeDisplayProtocol.HistoryExpired or NativeDisplayProtocol.HistoryLimit)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => {
                        status.Text = response.Kind == NativeDisplayProtocol.HistoryExpired
                            ? "本次历史浏览已过期，请回到底部后重新向上滚动。"
                            : "历史内容超出当前内存限额，请回到底部后稍后重试。";
                        status.IsVisible = true;
                    });
                }
                else if (response.Kind != NativeDisplayProtocol.NotReady && response.Kind != NativeDisplayProtocol.Unchanged)
                    throw new IOException("终端返回了不支持的展示消息。");
                var signalsReply = await ExchangeAsync(pipe, NativeDisplayProtocol.SignalsQuery, Array.Empty<byte>()).ConfigureAwait(false);
                if (signalsReply.Kind != NativeDisplayProtocol.SignalsState) throw new IOException("终端返回了无效的提醒状态。");
                var signals = NativeDisplaySignals.Decode(signalsReply.Payload);
                var windowState = await Dispatcher.UIThread.InvokeAsync(() => (Handle: TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, Foreground: IsActive));
                var attention = alerts.Offer(signals, windowState.Handle, windowState.Foreground,
                    text => Dispatcher.UIThread.Post(() => terminalClipboard.Offer(text)));
                await Dispatcher.UIThread.InvokeAsync(() => {
                    if (attention) noticeUntil = Environment.TickCount64 + 3000;
                    var progress = signals.Progress;
                    signalStatus.Text = progress.ProgressState switch {
                        1 => $"{progress.ProgressPercent}%", 2 => $"出错 · {progress.ProgressPercent}%",
                        3 => "进行中", 4 => $"暂停 · {progress.ProgressPercent}%",
                        _ => Environment.TickCount64 < noticeUntil ? "终端请求关注" : "",
                    };
                });
                var clipboardQuery = await Dispatcher.UIThread.InvokeAsync(terminalClipboard.NextQuery);
                if (clipboardQuery != null) {
                    var clipboardReply = await ExchangeAsync(pipe, NativeDisplayProtocol.ClipboardQuery, clipboardQuery).ConfigureAwait(false);
                    if (clipboardReply.Kind != NativeDisplayProtocol.ClipboardState) throw new IOException("终端返回了无效的复制授权。");
                    await Dispatcher.UIThread.InvokeAsync(() => terminalClipboard.Receive(clipboardQuery, clipboardReply.Payload));
                }
                await Task.Delay(33, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { terminalClipboard.Dispose(); status.Text = "终端连接已停止：" + error.Message; status.IsVisible = true; surface.IsEnabled = false; });
        }
    }

    private void ShowShellState(NativeTerminalShellState state, bool directory)
    {
        if (directory) {
            var path = NativeTerminalShellState.LocalWindowsDirectory(state.CwdUri);
            if (path == null || !Directory.Exists(path)) { status.Text = "当前 Shell 尚未提供可打开的本地目录。"; status.IsVisible = true; return; }
            try {
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
                start.ArgumentList.Add(path); System.Diagnostics.Process.Start(start)?.Dispose();
            } catch (Exception error) { status.Text = "打开目录失败：" + error.Message; status.IsVisible = true; }
            return;
        }
        var menu = new ContextMenu { MaxHeight = 400 };
        foreach (var command in state.Commands.Reverse()) {
            var phase = command.Phase switch { 0 or 1 => "等待输入", 2 => "运行中", 3 => command.ExitCode.HasValue ? "退出码 " + command.ExitCode : "已完成", _ => "已中断" };
            var item = new MenuItem { Header = $"#{command.Id} · {phase}", IsEnabled = !command.Prompt.Alternate && command.Prompt.Page == 1 };
            ToolTip.SetTip(item, NativeTerminalShellState.LocalWindowsDirectory(command.CwdUri) ?? command.CwdUri);
            item.Click += (_, _) => { var payload = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(payload, command.Id); Queue(NativeDisplayProtocol.ShellSeek, payload); };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "暂无命令标记", IsEnabled = false });
        shellCommands.ContextMenu = menu; menu.Open(shellCommands);
    }

    private NativeTerminalHostGeometryV1 MeasureHostGeometry()
    {
        // The session uses integer virtual cell pixels, measured once by its selected display.
        // Physical window/screen sizes are never inferred from the grid or an Android renderer.
        static uint Pixel(double value, int maximum) => double.IsFinite(value) && value >= 1 && value <= maximum
            ? (uint)Math.Round(value, MidpointRounding.AwayFromZero) : 0;
        var cellWidth = Pixel(surface.CellWidth * RenderScaling, 1024);
        var cellHeight = Pixel(surface.CellHeight * RenderScaling, 1024);
        if (cellWidth == 0 || cellHeight == 0) cellWidth = cellHeight = 0;
        var screen = Screens.ScreenFromWindow(this);
        var screenWidth = Pixel(screen?.Bounds.Width ?? 0, 32768);
        var screenHeight = Pixel(screen?.Bounds.Height ?? 0, 32768);
        if (screenWidth == 0 || screenHeight == 0) screenWidth = screenHeight = 0;
        uint windowWidth = 0, windowHeight = 0;
        var handle = TryGetPlatformHandle();
        if (handle?.HandleDescriptor == "HWND" && GetWindowRect(handle.Handle, out var rect))
        {
            windowWidth = Pixel((long)rect.Right - rect.Left, 32768);
            windowHeight = Pixel((long)rect.Bottom - rect.Top, 32768);
            if (windowWidth == 0 || windowHeight == 0) windowWidth = windowHeight = 0;
        }
        return new(cellWidth, cellHeight, screenWidth, screenHeight, windowWidth, windowHeight);
    }

    private async Task<(int Columns, int Rows)> FitWindowAsync(Stream pipe, byte[] request)
    {
        NativeDisplayProtocol.RequireSize(request, 28);
        var columns = BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(16));
        var rows = BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(18));
        if (columns is < 2 or > 512 || rows is < 2 or > 512) throw new IOException("终端请求了无效的窗口尺寸。");
        var expires = BinaryPrimitives.ReadInt64LittleEndian(request.AsSpan(20));
        var original = await Dispatcher.UIThread.InvokeAsync(() => (Width, Height, WindowState, localGestureGeneration));
        var eligible = true;
        for (var attempt = 0; attempt < 3 && eligible; attempt++)
        {
            eligible = await Dispatcher.UIThread.InvokeAsync(() => {
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expires || original.localGestureGeneration != localGestureGeneration) return false;
                var width = ClientSize.Width - surface.Bounds.Width + (columns + 0.25) * surface.CellWidth;
                var height = ClientSize.Height - surface.Bounds.Height + (rows + 0.25) * surface.CellHeight;
                var screen = Screens.ScreenFromWindow(this);
                if (screen != null && (width > screen.WorkingArea.Width / screen.Scaling - 32 || height > screen.WorkingArea.Height / screen.Scaling - 64)) return false;
                WindowState = WindowState.Normal; Width = width; Height = height;
                return true;
            });
            await Task.Delay(60, lifetime.Token).ConfigureAwait(false);
            var actual = await Dispatcher.UIThread.InvokeAsync(() => surface.MeasureGrid(surface.Bounds.Size));
            if (actual == (columns, rows)) break;
        }
        var result = await Dispatcher.UIThread.InvokeAsync(() => (Size: surface.MeasureGrid(surface.Bounds.Size), Generation: localGestureGeneration));
        var payload = new byte[21]; request.AsSpan(0, 16).CopyTo(payload);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(16), (short)result.Size.Columns);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(18), (short)result.Size.Rows);
        payload[20] = (byte)(eligible && original.localGestureGeneration == result.Generation && result.Size == (columns, rows) ? 1 : 0);
        var accepted = await ExchangeAsync(pipe, NativeDisplayProtocol.FitResult, payload).ConfigureAwait(false);
        if (accepted.Kind == NativeDisplayProtocol.Accepted && payload[20] == 1) return result.Size;
        if (accepted.Kind is not (NativeDisplayProtocol.Accepted or NativeDisplayProtocol.Rejected)) throw new IOException("终端返回了无效的窗口调整结果。");
        await Dispatcher.UIThread.InvokeAsync(() => { Width = original.Width; Height = original.Height; WindowState = original.WindowState; });
        await Task.Delay(60, lifetime.Token).ConfigureAwait(false);
        return default;
    }

    private async Task<(byte Kind, byte[] Payload)> ExchangeAsync(Stream pipe, byte kind, byte[] payload)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await NativeDisplayProtocol.WriteAsync(pipe, kind, payload, NativeDisplayProtocol.MaximumRequestBytes, deadline.Token).ConfigureAwait(false);
        return await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumGraphicsFrameBytes, deadline.Token).ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);
}

// A display connection belongs to one terminal profile for its entire window lifetime.
internal sealed class NativeTerminalDisplayFrameDecoder
{
    private byte? frameKind;

    internal (NativeTerminalRenderFrameV4 Text, NativeTerminalRenderFrameV5? Graphics) Decode(byte kind, byte[] payload)
    {
        if (kind is not (NativeDisplayProtocol.Frame or NativeDisplayProtocol.FrameV5))
            throw new InvalidDataException("Unsupported native display frame kind.");
        if (frameKind.HasValue && frameKind.Value != kind)
            throw new InvalidDataException("Native display terminal profile changed during the connection.");
        NativeTerminalRenderFrameV5? graphics = null;
        NativeTerminalRenderFrameV4 text;
        if (kind == NativeDisplayProtocol.FrameV5)
        {
            graphics = NativeTerminalRenderFrameV5Decoder.Decode(payload);
            text = graphics.Text;
        }
        else text = NativeTerminalRenderFrameV4Decoder.Decode(payload);
        frameKind = kind;
        return (text, graphics);
    }
}
