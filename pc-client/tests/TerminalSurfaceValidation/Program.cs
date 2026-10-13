using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TermLink.Desktop;
using TermLink.TerminalCore;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--loopback-format-selfcheck") {
            WindowsLoopbackRecorder.SelfCheck();
            Console.WriteLine("WASAPI loopback WAV format self-check passed.");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--terminal-audio-probe-selfcheck") {
            WindowsTerminalAudioValidation.SelfCheck();
            Console.WriteLine("Terminal audio probe frequency self-check passed; no hardware was tested.");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--record-loopback") {
            if (!int.TryParse(args[2], out var seconds) || seconds is < 2 or > 60)
                throw new ArgumentException("Loopback duration must be 2..60 seconds.");
            try {
                Console.WriteLine(JsonSerializer.Serialize(WindowsLoopbackRecorder.Record(args[1], seconds)));
                return 0;
            } catch (Exception error) {
                Console.Error.WriteLine("LOOPBACK_UNAVAILABLE: " + error.Message);
                return 2;
            }
        }
        if (args.Length == 2 && args[0] == "--terminal-audio-probe")
            return WindowsTerminalAudioValidation.Run(args[1]);
        if (args.Length != 1 && !(args.Length == 3 && args[1] == "--fixture-root"))
            throw new ArgumentException("Provide an output PNG path and optional --fixture-root directory.");
        NativeInputQueueValidation.Run();
        NativeDisplayGraphicsValidation.Run(args.Length == 3 ? args[2] : null);
        TerminalAlertsValidation.Run();
        ClipboardAuthorizationValidation.Run();
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().SetupWithoutStarting();
        ClipboardPromptValidation.Run();
        ThemeVariantValidation.Run();
        GraphicsSurfaceValidation.Run(args.Length == 3 ? args[2] : null, Path.ChangeExtension(args[0], ".graphics.png"));
        using var terminal = NativeTerminalCoreV2.Create(24, 7, 1, 20,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);
        const string family = "👨\u200d👩\u200d👧\u200d👦";
        terminal.ApplyOutput(1, Encoding.UTF8.GetBytes("\u001b[?25lWWWW\u001b[2;1H\u001b#6WWWW\u001b[3;1H\u001b#3ABCD\u001b[4;1H\u001b#4ABCD\u001b[5;1H中 🇨🇳 " + family + "\u001b[6;1H\u001b[4:3;58:2::255:64:32mstyled"));
        using var checkpoint = terminal.CaptureCheckpoint();
        var frame = NativeTerminalRenderFrameV4Decoder.Decode(checkpoint.ExportRenderFrameV4());
        var surface = new NativeTerminalSurface();
        surface.Present(frame);
        var size = new Size(800, 330);
        var window = new Window { Width = size.Width, Height = size.Height, Content = surface };
        window.Show();
        surface.Measure(size); surface.Arrange(new Rect(size));
        Dispatcher.UIThread.RunJobs();

        Require(surface.CellAt(new Point(surface.CellWidth * 3.1, surface.CellHeight * 1.2)) == (1, 1), "scaled row hit test diverged");
        Require(surface.CellAt(new Point(surface.CellWidth * 3.1, surface.CellHeight * 0.2)) == (0, 3), "normal row hit test changed");
        surface.Select((1, 0), (1, 11));
        Require(surface.SelectedText() == "WWWW", "scaled row copied hidden cells");
        surface.Select((4, 0), (4, 23));
        Require(surface.SelectedText() == "中 🇨🇳 " + family, "selection split a grapheme");
        surface.ClearSelection();
        using var bitmap = new RenderTargetBitmap(new PixelSize(800, 330), new Vector(96, 96));
        bitmap.Render(surface);
        bitmap.Save(args[0], PngBitmapEncoderOptions.Default);
        var mouse = new List<NativeTerminalMouseEvent>(); var history = new List<int>();
        surface.MouseRequested += mouse.Add; surface.HistoryScrollRequested += history.Add;
        surface.ScrollWheel(.5, default, KeyModifiers.None);
        Require(history.Count == 0, "Partial wheel notch was emitted too early");
        surface.ScrollWheel(.5, default, KeyModifiers.None);
        Require(history.SequenceEqual(new[] { 3 }) && mouse.Count == 0, "Ordinary scroll did not use history");
        void Feed(ulong sequence, string output) {
            terminal.ApplyOutput(sequence, Encoding.UTF8.GetBytes(output));
            using var value = terminal.CaptureCheckpoint(); surface.Present(NativeTerminalRenderFrameV4Decoder.Decode(value.ExportRenderFrameV4()));
        }
        Feed(2, "\x1b[?1003;1006h");
        surface.ScrollWheel(-1, new Point(30, 40), KeyModifiers.None);
        Require(mouse.Count == 1 && Encoding.ASCII.GetString(terminal.EncodeMouse(mouse[0])).StartsWith("\x1b[<65;"), "Mouse wheel was not routed to the negotiated encoder");
        surface.ScrollWheel(1, default, KeyModifiers.Shift);
        Require(mouse.Count == 1 && history.Count == 2, "Shift override duplicated mouse and history input");
        surface.ViewingHistory = true; surface.ScrollWheel(1, default, KeyModifiers.None);
        Require(mouse.Count == 1 && history.Count == 3, "History view sent input to live application");
        surface.ViewingHistory = false;
        Feed(3, "\x1b[?1003l\x1b[?1049;1007h");
        surface.ScrollWheel(1, default, KeyModifiers.None);
        Require(mouse.Count == 2 && Encoding.ASCII.GetString(terminal.EncodeMouse(mouse[1])) == "\x1b[A", "Alternate scroll did not use core direction encoding");
        Feed(4, "\x1bc\x1b[?25l\x1b]17;#123456\x1b\\\x1b]4;2;#abcdef;3;#2468ac\x1b\\\x1b[1;2;3,|");
        surface.Select((0, 0), (0, 0));
        using var colors = new RenderTargetBitmap(new PixelSize(800, 330), new Vector(96, 96));
        colors.Render(surface);
        var pixel = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try {
            colors.CopyPixels(new PixelRect(2, 2, 1, 1), pixel, 4, 4);
            Require(System.Runtime.InteropServices.Marshal.ReadInt32(pixel) == unchecked((int)0xff123456), "OSC 17 did not change the actual selection pixel");
            colors.CopyPixels(new PixelRect(400, 300, 1, 1), pixel, 4, 4);
            Require(System.Runtime.InteropServices.Marshal.ReadInt32(pixel) == unchecked((int)0xff2468ac), "DECAC background did not reach the actual bitmap");
        } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pixel); }
        colors.Save(Path.ChangeExtension(args[0], ".colors.png"), PngBitmapEncoderOptions.Default);
        window.Close();
        Console.WriteLine("Native frame surface: row scaling, hit testing, cluster selection, mouse/history/alternate scroll routing and actual Skia bitmap rendering passed.");
        return 0;
    }

    private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
}
