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
            Array.Empty<TerminalCommandCapability>(),
            sessions,
            Array.Empty<AccountDeviceSnapshot>());
        var client = new StaticAgentClient(snapshot);
        await using var stateStore = new AgentStateStore(client, TimeSpan.FromMinutes(1));
        var telemetry = new CapturingNavigationTelemetry();
        var viewModel = new MainWindowViewModel(
            stateStore,
            new ImmediateUiDispatcher(),
            new PlatformDescriptor(PcPlatform.Windows, PcArchitecture.X64, "Windows headless", "win-x64", false),
            telemetry);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await stateStore.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var connectionRoot = RequireControl<Grid>(window, "ConnectionPageRoot");
        var sessionsRoot = RequireControl<Grid>(window, "SessionsPageRoot");
        var settingsRoot = RequireControl<Grid>(window, "SettingsPageRoot");
        var sessionList = RequireControl<ListBox>(window, "SessionList");
        Require(viewModel.Sessions.Sessions.Count == 100, "The 100-session snapshot was not applied.");
        Require(sessionList.ItemsPanelRoot is VirtualizingStackPanel, "The session list is not virtualized.");

        var durations = new List<double>(300);
        var keys = new[] { "connection", "settings", "sessions" };
        for (var index = 0; index < 300; index++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            viewModel.Navigate(keys[index % keys.Length]);
            Dispatcher.UIThread.RunJobs();
            durations.Add(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }

        Require(ReferenceEquals(connectionRoot, RequireControl<Grid>(window, "ConnectionPageRoot")),
            "The connection page was reconstructed during navigation.");
        Require(ReferenceEquals(sessionsRoot, RequireControl<Grid>(window, "SessionsPageRoot")),
            "The sessions page was reconstructed during navigation.");
        Require(ReferenceEquals(settingsRoot, RequireControl<Grid>(window, "SettingsPageRoot")),
            "The settings page was reconstructed during navigation.");
        var p95 = Percentile(durations, 0.95);
        Require(p95 <= 100, $"Headless navigation P95 exceeded 100 ms: {p95:F3} ms.");
        Require(telemetry.Measurements.Count >= 299, "Navigation telemetry did not capture page transitions.");

        Console.WriteLine(
            $"Avalonia headless shell passed: cached pages, virtualized 100-session list, navigation P95={p95:F3} ms.");
        window.Close();
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
        public ValueTask<AgentStatusSnapshot> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);

        public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask RetryConnectionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class CapturingNavigationTelemetry : INavigationTelemetry
    {
        public List<NavigationMeasurement> Measurements { get; } = new();
        public void Record(NavigationMeasurement measurement) => Measurements.Add(measurement);
    }
}
