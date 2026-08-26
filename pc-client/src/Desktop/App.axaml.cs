using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Presentation;
#if WINDOWS
using TermLink.Platform.Windows;
#endif

namespace TermLink.Desktop;

public sealed partial class App : Application
{
    private MainWindowViewModel? mainWindowViewModel;
    private IAsyncDisposable? agentClientLifetime;
    private MainWindow? mainWindow;
    private TrayIcon? trayIcon;
    private IClassicDesktopStyleApplicationLifetime? desktopLifetime;
    private readonly ControlUiWindowLifetime windowLifetime = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktopLifetime = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var descriptor = CreatePlatformDescriptor();
            IAgentClient client;
            IDesktopOperations? desktopOperations = null;
#if WINDOWS
            if (!Program.UsePreviewFixture)
            {
                var windowsClient = new WindowsAgentClient(WindowsAgentClientOptions.CreateDefault(descriptor));
                client = windowsClient;
                agentClientLifetime = windowsClient;
                desktopOperations = WindowsDesktopOperations.CreateDefault();
            }
            else
#endif
            {
                client = new PreviewAgentClient(descriptor, Program.UsePreviewFixture);
            }
            var stateStore = new AgentStateStore(client);
            mainWindowViewModel = new MainWindowViewModel(
                stateStore,
                client,
                new AvaloniaUiDispatcher(),
                descriptor,
                new TraceNavigationTelemetry(),
                desktopOperations);
            mainWindow = new MainWindow { DataContext = mainWindowViewModel };
            if (Program.StartHidden) mainWindow.Opened += (_, _) => mainWindow.Hide();
            mainWindow.Closing += OnMainWindowClosing;
            desktop.MainWindow = mainWindow;
            trayIcon = CreateTrayIcon();
            desktop.Exit += OnExit;
            mainWindowViewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        trayIcon?.Dispose();
        trayIcon = null;
        if (mainWindow is not null)
        {
            mainWindow.Closing -= OnMainWindowClosing;
            mainWindow = null;
        }
        if (mainWindowViewModel is not null)
        {
            mainWindowViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            mainWindowViewModel = null;
        }
        agentClientLifetime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        agentClientLifetime = null;
        desktopLifetime = null;
    }

    private TrayIcon CreateTrayIcon()
    {
        var iconUri = new Uri("avares://TermLink/Assets/TermLink.ico");
        using var iconStream = AssetLoader.Open(iconUri);
        var menu = new NativeMenu();
        var openItem = new NativeMenuItem { Header = PresentationText.OpenTermLink };
        openItem.Click += (_, _) => ShowMainWindow();
        var updateItem = new NativeMenuItem { Header = PresentationText.CheckUpdates };
        updateItem.Click += (_, _) => mainWindowViewModel?.CheckForUpdatesCommand.Execute(null);
        var diagnosticsItem = new NativeMenuItem { Header = PresentationText.Diagnostics };
        diagnosticsItem.Click += (_, _) =>
        {
            mainWindowViewModel?.Navigate("diagnostics");
            ShowMainWindow();
        };
        var exitItem = new NativeMenuItem { Header = PresentationText.ExitControlUi };
        exitItem.Click += (_, _) => ExitControlUi();
        menu.Items.Add(openItem);
        menu.Items.Add(updateItem);
        menu.Items.Add(diagnosticsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        var result = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "TermLink PC",
            Menu = menu,
            IsVisible = true,
        };
        result.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { result });
        return result;
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (mainWindow is not null) windowLifetime.HandleClosing(mainWindow, e);
    }

    private void ShowMainWindow()
    {
        if (mainWindow is null) return;
        if (!mainWindow.IsVisible) mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized)
            mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private void ExitControlUi()
    {
        windowLifetime.RequestExit();
        trayIcon?.Dispose();
        trayIcon = null;
        desktopLifetime?.Shutdown();
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

/// <summary>
/// Separates closing the control window from stopping the independent Agent.
/// The behavior is deliberately testable without a native tray provider.
/// </summary>
public sealed class ControlUiWindowLifetime
{
    public bool ExitRequested { get; private set; }

    public void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window)
            throw new ArgumentException("A Window sender is required.", nameof(sender));
        ArgumentNullException.ThrowIfNull(e);
        if (ExitRequested) return;
        e.Cancel = true;
        window.Hide();
    }

    public void RequestExit() => ExitRequested = true;
}
