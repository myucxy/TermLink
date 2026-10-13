using Avalonia;

namespace TermLink.Desktop;

public static class Program
{
    private static int pendingActivation;

    public static bool UsePreviewFixture { get; private set; }
    public static bool StartHidden { get; private set; }
    public static (string Pipe, int HostPid)? NativeTerminalAttachment { get; private set; }
    public static event EventHandler? ActivationRequested;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--native-terminal")
        {
            if (!OperatingSystem.IsWindows() || !int.TryParse(args[2], out var hostPid) || hostPid <= 0)
                throw new ArgumentException("Invalid native terminal host.");
            NativeTerminalAttachment = (args[1], hostPid);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }
        UsePreviewFixture = args.Contains("--preview-fixture", StringComparer.Ordinal);
        StartHidden = args.Contains("--start-hidden", StringComparer.Ordinal);
        using var singleInstance = new SingleInstanceCoordinator(
            UsePreviewFixture ? "preview-fixture" : "control-ui");
        if (!singleInstance.TryAcquire())
        {
            _ = singleInstance.SignalPrimaryAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return;
        }
        singleInstance.StartListening(RequestActivation);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static bool ConsumePendingActivation() => Interlocked.Exchange(ref pendingActivation, 0) != 0;

    private static void RequestActivation()
    {
        Interlocked.Exchange(ref pendingActivation, 1);
        ActivationRequested?.Invoke(null, EventArgs.Empty);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
