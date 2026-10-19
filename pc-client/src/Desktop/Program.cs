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
#if WINDOWS
        if (args.Length == 1 && args[0] == "--shutdown-agent") {
            try {
                using var owned = System.Diagnostics.Process.GetProcessesByName("TermLink.Agent").FirstOrDefault(p => {
                    try { return string.Equals(p.MainModule?.FileName, System.IO.Path.Combine(AppContext.BaseDirectory, "TermLink.Agent.exe"), StringComparison.OrdinalIgnoreCase); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { p.Dispose(); return false; }
                });
                if (owned == null) { Environment.ExitCode = 1; return; }
                var response = TermLink.Agent.RegistryControlClient.Send(TermLink.Agent.TermLinkControlPipe.DefaultName(),
                    new TermLink.Agent.RegistryRequest { Command = TermLink.Agent.RegistryCommand.RestartAgent }, 10000, owned.Id);
                Environment.ExitCode = response.Success ? 0 : 1;
            } catch (Exception e) when (e is System.IO.IOException or TimeoutException or UnauthorizedAccessException) { Environment.ExitCode = 1; }
            return;
        }
#endif
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
#if WINDOWS
        // An older Updater starts the new desktop without --start-hidden.
        StartHidden |= TermLink.UpdateCore.RemoteUpdateTransactionStore.IsVerifyingInstallation(AppContext.BaseDirectory);
#endif
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
