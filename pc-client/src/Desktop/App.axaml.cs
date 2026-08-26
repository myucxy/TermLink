using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Presentation;

namespace TermLink.Desktop;

public sealed partial class App : Application
{
    private MainWindowViewModel? mainWindowViewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var descriptor = CreatePlatformDescriptor();
            var client = new PreviewAgentClient(descriptor, Program.UsePreviewFixture);
            var stateStore = new AgentStateStore(client);
            mainWindowViewModel = new MainWindowViewModel(
                stateStore,
                new AvaloniaUiDispatcher(),
                descriptor,
                new TraceNavigationTelemetry());
            desktop.MainWindow = new MainWindow { DataContext = mainWindowViewModel };
            desktop.Exit += OnExit;
            mainWindowViewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (mainWindowViewModel is null) return;
        mainWindowViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        mainWindowViewModel = null;
    }

    private static PlatformDescriptor CreatePlatformDescriptor()
    {
        var platform = OperatingSystem.IsWindows()
            ? PcPlatform.Windows
            : OperatingSystem.IsLinux()
                ? PcPlatform.Linux
                : OperatingSystem.IsMacOS()
                    ? PcPlatform.MacOS
                    : PcPlatform.Unknown;
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => PcArchitecture.X64,
            Architecture.Arm64 => PcArchitecture.Arm64,
            _ => PcArchitecture.Unknown,
        };
        return new PlatformDescriptor(
            platform,
            architecture,
            RuntimeInformation.OSDescription,
            RuntimeInformation.RuntimeIdentifier,
            false);
    }
}
