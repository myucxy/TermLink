using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using TermLink.Desktop;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Presentation;

namespace TermLink.Desktop.HeadlessTests;

public static class Program
{
    [STAThread]
    public static async Task Main()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        var identity = new PcAgentIdentity(
            Guid.Parse("f77bd947-015a-49b5-9f93-df8397983209"),
            "headless-pc",
            PcPlatform.Windows,
            PcArchitecture.X64,
            "Windows headless",
            "0.2.0-preview.1");
        var sessions = Enumerable.Range(0, 100)
            .Select(index => new ManagedSessionSnapshot(
                Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                $"session-{index:000}",
                "pwsh",
                ManagedSessionState.Connected,
                index % 3,
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                (ulong)index))
            .ToArray();
        var devices = Enumerable.Range(0, 100)
            .Select(index => new AccountDeviceSnapshot(
                Guid.Parse($"10000000-0000-0000-0000-{index + 1:000000000000}"),
                $"android-{index:000}",
                "ANDROID",
                index % 2 == 0 ? "CONTROL" : "VIEW",
                index % 3 != 0))
            .ToArray();
        var terminalCommands = new[]
        {
            new TerminalCommandCapability("pwsh", "PowerShell 7", "pwsh.exe", Array.Empty<string>(), true),
            new TerminalCommandCapability("custom-existing", "Existing", "cmd.exe", new[] { "/k" }, false),
        };
        var snapshot = new AgentStatusSnapshot(
            1,
            DateTimeOffset.UnixEpoch,
            identity,
            AgentConnectionState.Connected,
            true,
            "headless",
            "https://example.invalid",
            true,
            null,
            null,
            new HashSet<string> { PcCapabilityIds.RealtimeTerminalV1 },
            terminalCommands,
            sessions,
            devices);
        var client = new StaticAgentClient(snapshot);
        var desktopOperations = new FixtureDesktopOperations();
        await using var stateStore = new AgentStateStore(client, TimeSpan.FromMinutes(1));
        var telemetry = new CapturingNavigationTelemetry();
        var viewModel = new MainWindowViewModel(
            stateStore,
            client,
            new ImmediateUiDispatcher(),
            new PlatformDescriptor(PcPlatform.Windows, PcArchitecture.X64, "Windows headless", "win-x64", false),
            telemetry,
            desktopOperations);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await stateStore.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var connectionRoot = RequireControl<Control>(window, "ConnectionPageRoot");
        var sessionsRoot = RequireControl<Grid>(window, "SessionsPageRoot");
        var settingsRoot = RequireControl<Grid>(window, "SettingsPageRoot");
        var devicesRoot = RequireControl<Control>(window, "DevicesPageRoot");
        var accountRoot = RequireControl<Control>(window, "AccountPageRoot");
        var diagnosticsRoot = RequireControl<Control>(window, "DiagnosticsPageRoot");
        var integrationRoot = RequireControl<Control>(window, "IntegrationSettingsRoot");
        var aboutRoot = RequireControl<Control>(window, "AboutSettingsRoot");
        var sessionList = RequireControl<ListBox>(window, "SessionList");
        var commandList = RequireControl<ListBox>(window, "CommandList");
        var devicesContentRoot = RequireControl<StackPanel>(window, "DevicesContentRoot");
        var settingsContentScroll = RequireControl<ScrollViewer>(window, "SettingsContentScroll");
        var settingsContentRoot = RequireControl<Grid>(window, "SettingsContentRoot");
        Require(viewModel.Sessions.Sessions.Count == 100, "The 100-session snapshot was not applied.");
        Require(viewModel.Devices.Devices.Count == 100, "The 100-device snapshot was not applied.");
        Require(viewModel.Settings.TerminalCommands.Count == 2, "Terminal commands were not applied.");
        Require(viewModel.SessionCountText == PresentationText.SessionCount(100),
            "The localized session-count display text was not composed by the presentation layer.");
        Require(viewModel.Sessions.Items[0].DetailText.Contains(" · ", StringComparison.Ordinal)
                && viewModel.Devices.Items[0].DetailText.Contains(" · ", StringComparison.Ordinal),
            "Theme-safe session and device detail text was not composed by the presentation layer.");
        Require(viewModel.CurrentPage.Key == "devices" && viewModel.Pages.Count == 3,
            "The Android-aligned three-destination desktop shell was not selected by default.");
        Require(devicesContentRoot.Margin.Right >= 16 &&
                settingsContentRoot.Margin.Right >= 16 &&
                !settingsContentScroll.AllowAutoHide &&
                !ScrollViewer.GetAllowAutoHide(sessionList),
            "Compact-window scrollbars can still auto-hide over edge-aligned content.");

        viewModel.Navigate("sessions");
        Dispatcher.UIThread.RunJobs();
        Require(sessionList.ItemsPanelRoot is VirtualizingStackPanel, "The session list is not virtualized.");
        viewModel.Navigate("settings");
        viewModel.SelectSettingsSection("commands");
        Dispatcher.UIThread.RunJobs();
        Require(commandList.ItemsPanelRoot is VirtualizingStackPanel, "The command list is not virtualized.");

        viewModel.OpenTerminalCommandEditorCommand.Execute(null);
        await DrainCommandAsync(viewModel.OpenTerminalCommandEditorCommand);
        Require(viewModel.Settings.CommandEditorOpen, "The custom-command editor did not open explicitly.");
        Require(viewModel.Settings.NewRunAsAdministrator,
            "New terminal commands did not default to administrator launch.");
        viewModel.Settings.NewCommandName = "Custom shell";
        viewModel.Settings.NewExecutable = "custom-shell.exe";
        viewModel.Settings.NewArguments = "--login";
        viewModel.AddTerminalCommandCommand.Execute(null);
        await DrainCommandAsync(viewModel.AddTerminalCommandCommand);
        Require(viewModel.Settings.TerminalCommands.Count == 3, "A manual command was not added.");
        Require(viewModel.Settings.HasUnsavedCommands && !viewModel.Settings.CommandEditorOpen,
            "A custom command did not enter the explicit unsaved state.");
        var added = viewModel.Settings.TerminalCommands.Single(command => command.DisplayName == "Custom shell");
        viewModel.RemoveTerminalCommandCommand.Execute(added);
        await DrainCommandAsync(viewModel.RemoveTerminalCommandCommand);
        Require(viewModel.Settings.TerminalCommands.Count == 2, "A manual command was not removed.");
        var detected = viewModel.Settings.TerminalCommands[0];
        viewModel.OpenTerminalCommandEditorCommand.Execute(detected);
        await DrainCommandAsync(viewModel.OpenTerminalCommandEditorCommand);
        Require(viewModel.Settings.CommandEditorOpen && viewModel.Settings.EditingCommandId == detected.Id,
            "An auto-detected command was not editable.");
        viewModel.Settings.NewCommandName = "PowerShell edited";
        viewModel.AddTerminalCommandCommand.Execute(null);
        await DrainCommandAsync(viewModel.AddTerminalCommandCommand);
        detected = viewModel.Settings.TerminalCommands.Single(command => command.Id == detected.Id);
        Require(detected.DisplayName == "PowerShell edited",
            "Editing an auto-detected command did not update its display name.");
        viewModel.RemoveTerminalCommandCommand.Execute(detected);
        await DrainCommandAsync(viewModel.RemoveTerminalCommandCommand);
        Require(viewModel.Settings.TerminalCommands.Count == 1, "An auto-detected command was not removable.");
        viewModel.SaveTerminalCommandsCommand.Execute(null);
        await DrainCommandAsync(viewModel.SaveTerminalCommandsCommand);
        Require(client.SavedTerminalCommands.Any(command =>
                command.Id == "pwsh" && command.Detected && !command.Enabled),
            "Deleting an auto-detected command did not persist a disabled override.");

        viewModel.SelectSettingsSection("about");
        Dispatcher.UIThread.RunJobs();
        Require(viewModel.IsAboutSettingsSelected && aboutRoot.IsVisible &&
                viewModel.Settings.Author == "清风惹尘丶" &&
                !viewModel.Settings.HasReleaseNotes,
            "The About section did not expose the author or started with hard-coded release notes.");
        viewModel.CheckForUpdatesCommand.Execute(null);
        await DrainCommandAsync(viewModel.CheckForUpdatesCommand);
        Require(viewModel.Settings.HasReleaseNotes &&
                viewModel.Settings.ReleaseNotes == "AppShip release fixture" &&
                viewModel.Settings.ReleaseNotesTitle.Contains("45", StringComparison.Ordinal),
            "The About section did not render the structured AppShip release notes fixture.");
        viewModel.SelectSettingsSection("integration");
        Dispatcher.UIThread.RunJobs();
        Require(viewModel.IsIntegrationSettingsSelected && integrationRoot.IsVisible,
            "The update action was not separated from the integration section.");

        viewModel.Navigate("sessions");
        var firstSessionId = viewModel.Sessions.Items[0].SessionId;
        viewModel.StopSessionCommand.Execute(firstSessionId);
        await WaitForAsync(() => viewModel.ConfirmationVisible, "A destructive session action did not request confirmation.");
        Require(!client.StopSessionCalled, "The session was stopped before confirmation.");
        viewModel.CancelPendingActionCommand.Execute(null);
        await DrainCommandAsync(viewModel.CancelPendingActionCommand);
        await DrainCommandAsync(viewModel.StopSessionCommand);
        Require(!client.StopSessionCalled, "Cancelling the confirmation still stopped the session.");

        desktopOperations.FailUpdateChecks = true;
        viewModel.CheckForUpdatesCommand.Execute(null);
        await DrainCommandAsync(viewModel.CheckForUpdatesCommand);
        Require(viewModel.OperationMessage.Contains("operation fixture", StringComparison.Ordinal) &&
                !viewModel.Settings.HasReleaseNotes &&
                viewModel.Settings.UpdateStatusText.Contains("AppShip", StringComparison.Ordinal),
            "An AppShip failure was not isolated from previously loaded release notes.");
        viewModel.OpenDiagnosticsDirectoryCommand.Execute(null);
        await DrainCommandAsync(viewModel.OpenDiagnosticsDirectoryCommand);
        Require(viewModel.NoticeCode == "ACCESS_DENIED" && viewModel.NoticeDetail == PresentationText.AccessDeniedDetail,
            "A path-access failure was not classified as ACCESS_DENIED.");

        var durations = new List<double>(300);
        var keys = new[] { "connection", "settings", "sessions" };
        for (var index = 0; index < 300; index++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            viewModel.Navigate(keys[index % keys.Length]);
            Dispatcher.UIThread.RunJobs();
            durations.Add(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }

        Require(ReferenceEquals(connectionRoot, RequireControl<Control>(window, "ConnectionPageRoot")),
            "The connection page was reconstructed during navigation.");
        Require(ReferenceEquals(sessionsRoot, RequireControl<Grid>(window, "SessionsPageRoot")),
            "The sessions page was reconstructed during navigation.");
        Require(ReferenceEquals(settingsRoot, RequireControl<Grid>(window, "SettingsPageRoot")),
            "The settings page was reconstructed during navigation.");
        Require(ReferenceEquals(devicesRoot, RequireControl<Control>(window, "DevicesPageRoot")),
            "The devices page was reconstructed during navigation.");
        Require(ReferenceEquals(accountRoot, RequireControl<Control>(window, "AccountPageRoot")),
            "The account page was reconstructed during navigation.");
        Require(ReferenceEquals(diagnosticsRoot, RequireControl<Control>(window, "DiagnosticsPageRoot")),
            "The diagnostics page was reconstructed during navigation.");
        var p95 = Percentile(durations, 0.95);
        Require(p95 <= 100, $"Headless navigation P95 exceeded 100 ms: {p95:F3} ms.");
        Require(telemetry.Measurements.Count >= 299, "Navigation telemetry did not capture page transitions.");

        var controlLifetime = new ControlUiWindowLifetime();
        var closeProbe = new Window();
        closeProbe.Closing += controlLifetime.HandleClosing;
        closeProbe.Show();
        closeProbe.Close();
        Dispatcher.UIThread.RunJobs();
        Require(!closeProbe.IsVisible && !controlLifetime.ExitRequested,
            "Closing the control window did not hide it while keeping the UI lifetime active.");
        closeProbe.Show();
        controlLifetime.RequestExit();
        closeProbe.Close();
        Dispatcher.UIThread.RunJobs();
        Require(!closeProbe.IsVisible && controlLifetime.ExitRequested,
            "Explicit control-UI exit did not allow the window to close.");

        VerifySingleInstanceActivation();

        Console.WriteLine(
            $"Avalonia headless shell passed: cached pages, virtualized 100-session list, About/integration, commands, single-instance activation, tray-window lifecycle and failure isolation, navigation P95={p95:F3} ms.");
        window.Close();
    }

    private static async Task DrainCommandAsync(AsyncCommand command)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (command.IsRunning && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
        Dispatcher.UIThread.RunJobs();
        Require(!command.IsRunning, "An async UI command did not complete.");
    }

    private static async Task WaitForAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
        Dispatcher.UIThread.RunJobs();
        Require(condition(), message);
    }

    private static void VerifySingleInstanceActivation()
    {
        var scope = "headless-" + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceCoordinator(scope);
        Require(primary.TryAcquire(), "The first desktop instance did not acquire ownership.");
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(() => activated.TrySetResult());
        var secondaryResult = Task.Run(async () =>
        {
            using var secondary = new SingleInstanceCoordinator(scope);
            return !secondary.TryAcquire() &&
                await secondary.SignalPrimaryAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }).GetAwaiter().GetResult();
        Require(secondaryResult, "A second desktop instance did not signal the primary instance.");
        activated.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private static T RequireControl<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Control '{name}' was not found.");

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StaticAgentClient(AgentStatusSnapshot snapshot) : IAgentClient
    {
        public bool StopSessionCalled { get; private set; }
        public IReadOnlyList<TerminalCommandCapability> SavedTerminalCommands { get; private set; } = Array.Empty<TerminalCommandCapability>();
        public ValueTask<AgentStatusSnapshot> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);

        public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask RetryConnectionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask StopSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            StopSessionCalled = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask GrantDeviceAsync(Guid deviceId, string permission, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask LoginAsync(string username, string password, string pcName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RegisterAsync(string username, string email, string password, string passwordConfirmation, string pcName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SignOutAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SaveTerminalCommandsAsync(IReadOnlyList<TerminalCommandCapability> commands, CancellationToken cancellationToken)
        {
            SavedTerminalCommands = commands.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingNavigationTelemetry : INavigationTelemetry
    {
        public List<NavigationMeasurement> Measurements { get; } = new();
        public void Record(NavigationMeasurement measurement) => Measurements.Add(measurement);
    }

    private sealed class FixtureDesktopOperations : IDesktopOperations
    {
        public bool FailUpdateChecks { get; set; }

        public ValueTask<DesktopUpdateStatus> CheckForUpdatesAsync(
            bool interactive,
            CancellationToken cancellationToken) => FailUpdateChecks
                ? ValueTask.FromException<DesktopUpdateStatus>(new InvalidOperationException("operation fixture"))
                : ValueTask.FromResult(new DesktopUpdateStatus(
                    DesktopUpdateDecision.NoUpdate,
                    "0.2.4",
                    45,
                    "AppShip release fixture",
                    "fixture is current"));
        public ValueTask<string> OpenDiagnosticsDirectoryAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<string>(new UnauthorizedAccessException("Access to path is denied"));
        public ValueTask<string> RepairTerminalIntegrationAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult("repaired");
    }
}
