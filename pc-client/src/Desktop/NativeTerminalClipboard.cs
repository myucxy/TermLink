using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TermLink.NativeDisplay;

namespace TermLink.Desktop;

// UI thread only. The terminal never obtains an OS clipboard handle or a read operation.
internal sealed class NativeTerminalClipboard : IDisposable
{
    private readonly Window owner;
    private readonly Func<bool> enabled;
    private readonly Action<string> report;
    private readonly Action<IntPtr, string> write;
    private Semaphore? slot;
    private Window? prompt;
    private Guid id;
    private byte phase;
    private long began, nextPoll;
    private string? text;
    private bool waitingForClick;

    internal NativeTerminalClipboard(Window owner, Func<bool> enabled, Action<string> report,
        Action<IntPtr, string>? write = null)
    { this.owner = owner; this.enabled = enabled; this.report = report; this.write = write ?? WriteClipboard; }

    internal void Offer(string value)
    {
        var reason = !OperatingSystem.IsWindows() ? "platform" :
            id != Guid.Empty ? "busy" :
            !owner.IsActive ? "inactive" :
            !enabled() ? "disabled" : "offered";
        try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLink", "clipboard-ui-debug.log"), $"{DateTime.UtcNow:O} offer={reason} length={value.Length}\n"); } catch { }
        if (reason != "offered") return;
        var candidate = new Semaphore(1, 1, "Local\\TermLink.TerminalClipboard.v1");
        try { if (!candidate.WaitOne(0)) { candidate.Dispose(); return; } }
        catch { candidate.Dispose(); return; }
        slot = candidate; id = Guid.NewGuid(); text = value; phase = 0; began = Environment.TickCount64; nextPoll = 0;
    }

    internal byte[]? NextQuery()
    {
        if (id == Guid.Empty) return null;
        var now = Environment.TickCount64;
        if (phase != 2 && (!enabled() || now - began >= 30000 || !(prompt?.IsActive ?? owner.IsActive))) Cancel();
        if (waitingForClick || now < nextPoll) return null;
        nextPoll = now + 100;
        var payload = new byte[17]; payload[0] = phase; id.TryWriteBytes(payload.AsSpan(1));
        return payload;
    }

    internal void Receive(byte[] request, byte[] response)
    {
        NativeDisplayProtocol.RequireSize(response, 1);
        if (response[0] > 2) throw new IOException("无效的终端剪贴板授权响应。");
        if (new Guid(request.AsSpan(1)) != id || request[0] != phase) return;
        if (phase == 2) { Reset(); return; }
        if (!enabled() || Environment.TickCount64 - began >= 30000 || !(prompt?.IsActive ?? owner.IsActive)) { Cancel(); return; }
        if (response[0] == 1) return;
        if (response[0] == 0) {
            report("未复制：无法确认本机控制状态，或手机正在控制该会话。"); Reset(); return;
        }
        if (phase == 0) ShowPrompt();
        else {
            try { write(prompt!.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, text!); }
            catch { report("复制失败：系统剪贴板暂不可用。"); }
            finally { Reset(); }
        }
    }

    private void ShowPrompt()
    {
        waitingForClick = true;
        var dialog = new Window { Title = "终端请求复制", Width = 420, MaxHeight = 400,
            SizeToContent = SizeToContent.Height, CanResize = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var copy = new Button { Content = "仅复制本次" };
        var cancel = new Button { Content = "取消" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel); buttons.Children.Add(copy);
        var content = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"复制 {text!.Length} 个字符到本机剪贴板？", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = Preview(text), MaxHeight = 220, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons); dialog.Content = content; prompt = dialog;
        copy.Click += (_, _) => { if (!dialog.IsActive) return; waitingForClick = false; phase = 1; copy.IsEnabled = false; nextPoll = 0; };
        cancel.Click += (_, _) => Cancel();
        dialog.Closed += (_, _) => { if (ReferenceEquals(prompt, dialog)) Cancel(); };
        dialog.Deactivated += (_, _) => { if (ReferenceEquals(prompt, dialog)) Cancel(); };
        _ = dialog.ShowDialog(owner);
    }

    internal static string Preview(string value)
    {
        var result = new StringBuilder();
        foreach (var c in value.Take(240)) {
            if (c == '\n') result.Append(c);
            else if (c == '\t') result.Append("\\t");
            else if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) result.Append($"\\u{(int)c:X4}");
            else result.Append(c);
        }
        if (value.Length > 240) result.Append('…');
        return result.ToString();
    }

    private void Cancel() { phase = 2; waitingForClick = false; nextPoll = 0; ClosePrompt(); }
    private void ClosePrompt() { var previous = prompt; prompt = null; previous?.Close(); }
    private void Reset() {
        id = Guid.Empty; text = null; waitingForClick = false; ClosePrompt();
        slot?.Release(); slot?.Dispose(); slot = null;
    }
    public void Dispose() => Reset();

    private static void WriteClipboard(IntPtr owner, string text)
    {
        if (owner == IntPtr.Zero || text.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(text) > 16384)
            throw new InvalidOperationException("Invalid terminal clipboard text.");
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var memory = GlobalAlloc(0x42, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        try {
            var pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) throw new IOException();
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); } finally { GlobalUnlock(memory); }
            if (!OpenClipboard(owner)) throw new IOException();
            try {
                if (!EmptyClipboard() || SetClipboardData(13, memory) == IntPtr.Zero) throw new IOException();
                memory = IntPtr.Zero; // ownership transferred to Windows
            } finally { CloseClipboard(); }
        } finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
