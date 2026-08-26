using Avalonia;

namespace TermLink.Desktop;

public static class Program
{
    public static bool UsePreviewFixture { get; private set; }
    public static bool StartHidden { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        UsePreviewFixture = args.Contains("--preview-fixture", StringComparer.Ordinal);
        StartHidden = args.Contains("--start-hidden", StringComparer.Ordinal);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
