using Avalonia.Controls;
using Avalonia.Threading;
using TermLink.Desktop;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.MacOS;
using TermLink.Presentation;

namespace TermLink.Desktop.HeadlessTests;

internal static class MacUsabilityValidation
{
    public static async Task ValidateAsync()
    {
        var client = new MacClient();
        await using var store = new AgentStateStore(client, TimeSpan.FromMilliseconds(250));
        var runner = new OpenRunner();
        var operations = new MacDesktopOperations(MacPlatformOptions.CreateDefault(), runner);
        await using var model = new MainWindowViewModel(store, client, new ImmediateUiDispatcher(),
            new PlatformDescriptor(PcPlatform.MacOS, PcArchitecture.Arm64, "macOS validation", "osx-arm64", false),
            desktopOperations: operations, updateCheckInterval: TimeSpan.FromMilliseconds(50));
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await store.RefreshAsync();
            Require(model.CanEnableRemoteAccess, "A signed-in Mac with disabled Relay could not enable it while offline.");
            Require(!model.CanRetryConnection, "Disabled Relay offered an ineffective reconnect action.");
            Require(!model.SupportsTerminalCommandEditing && !model.OpenTerminalCommandEditorCommand.CanExecute(null),
                "Mac exposed a command editor whose configuration cannot be used.");
            model.OpenTerminalCommandEditorCommand.Execute(null);
            Require(!model.Settings.CommandEditorOpen, "Disabled command editor still opened.");
            model.Navigate("settings");
            model.SelectSettingsSection("integration");
            Dispatcher.UIThread.RunJobs();
            Require(window.FindControl<Border>("WindowsTerminalIntegrationCard")?.IsVisible == false,
                "Windows Terminal card remained visible on macOS.");
            Require(window.FindControl<Border>("MacTerminalIntegrationCard")?.IsVisible == true,
                "macOS Terminal card was absent.");
            Require(!model.OpenTerminalIntegrationSettingsCommand.CanExecute(null) && !model.RepairTerminalIntegrationCommand.CanExecute(null),
                "Mac could execute a Windows integration command.");
            model.PerformPlatformActionCommand.Execute("OpenTerminal");
            await DrainAsync(model.PerformPlatformActionCommand);
            Require(runner.Calls == 1, "Mac integration did not reach its native operation.");
            model.SelectSettingsSection("agents");
            await DrainAsync(model.DetectAiAgentsCommand);
            Require(model.Settings.AiAgents.Count == 1 && model.Settings.AiAgents[0].Status == "detected" &&
                model.Settings.AiAgents[0].Detail.Contains("远程 AI 尚未接通"), "Mac detection silently returned empty or advertised remote AI.");
            Require(!model.Sessions.AiStatus.Contains("Windows", StringComparison.Ordinal), "Mac session status still required a Windows Agent upgrade.");
            model.SelectSettingsSection("about");
            Dispatcher.UIThread.RunJobs();
            Require(!model.CheckForUpdatesCommand.CanExecute(null) && window.FindControl<Button>("AboutUpdateActionButton")?.IsVisible == false,
                "Mac could run the Windows updater.");
            var tray = DesktopTrayMenu.Create(() => { }, () => throw new Exception("Unexpected update"), () => { }, () => { }, null, model.SupportsAutomaticUpdates);
            Require(!tray.Items.OfType<NativeMenuItem>().Any(item => Equals(item.Header, PresentationText.CheckUpdates)),
                "macOS tray exposed an unsupported update command.");

            // A recoverable IPC failure must publish unavailable state and then
            // restore the same revision, without requiring an application restart.
            client.FailNext = true;
            model.Start();
            await UntilAsync(() => model.AgentUnavailable);
            await UntilAsync(() => !model.AgentUnavailable && model.IsAccountLinked);
            Require(model.CanEnableRemoteAccess, "Mac did not recover from a transient polling failure.");
            Console.WriteLine("PASS macOS UI: native integration, detection, offline Relay enable, update/edit isolation and polling recovery");
        }
        finally { window.Close(); }
    }

    private static async Task DrainAsync(AsyncCommand command) => await UntilAsync(() => !command.IsRunning);

    public static async Task ValidateUpdaterAsync()
    {
        var client = new MacClient();
        await using var store = new AgentStateStore(client);
        var operations = new UpdateOperations();
        await using var model = new MainWindowViewModel(store, client, new AvaloniaUiDispatcher(),
            new PlatformDescriptor(PcPlatform.MacOS, PcArchitecture.Arm64, "macOS validation", "osx-arm64", true),
            desktopOperations: operations);
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await store.RefreshAsync();
            var restarts = 0;
            model.UpdateInstalled += (_, _) =>
            {
                Require(!model.IsInstallingUpdate && !model.UpdateProgressVisible,
                    "Restart was requested before the install overlay and exit guard were released.");
                restarts++;
            };
            Require(model.CheckForUpdatesCommand.CanExecute(null), "Configured Mac updates remained disabled.");
            model.CheckForUpdatesCommand.Execute(null);
            await UntilAsync(() => model.ConfirmationVisible);
            model.ConfirmPendingActionCommand.Execute(null);
            await UntilAsync(() => model.ConfirmationVisible && operations.Ready);
            Require(model.ConfirmationDetail.Contains(PresentationText.MacInstallerAuthorizationDetail, StringComparison.Ordinal),
                "The Mac installation confirmation did not explain system authorization and task interruption.");
            model.ConfirmPendingActionCommand.Execute(null);
            await UntilAsync(() => model.IsInstallingUpdate);
            Require(model.UpdateProgressVisible && window.FindControl<Button>("CancelUpdateDownloadButton")?.IsVisible == false,
                "System installation still offered download cancellation.");
            operations.Installed.TrySetResult();
            await DrainAsync(model.CheckForUpdatesCommand);
            Require(restarts == 1 && !model.IsInstallingUpdate && !model.UpdateProgressVisible,
                "Successful Mac installation did not request one clean restart.");
            // Simulate a late report from a completed operation. It cannot
            // re-enable the install guard or block the relaunch path.
            operations.Progress!.Report(new(1, 1, Installing: true));
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
            Require(!model.IsInstallingUpdate && !model.UpdateProgressVisible, "Late progress restored a completed install overlay.");
            Console.WriteLine("PASS macOS update UI: two confirmations, system-install overlay, ordered restart and late-progress rejection (fixture)");
        }
        finally { window.Close(); }
    }

    private sealed class UpdateOperations : IDesktopOperations
    {
        public bool SupportsWindowsTerminalIntegration => false;
        public bool SupportsMacIntegration => true;
        public bool Ready { get; private set; }
        public IProgress<DesktopUpdateProgress>? Progress { get; private set; }
        public TaskCompletionSource Installed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<DesktopUpdateStatus> CheckForUpdatesAsync(bool install,
            Func<DesktopUpdateStatus, CancellationToken, ValueTask<bool>>? confirm,
            IProgress<DesktopUpdateProgress>? progress, CancellationToken token)
        {
            var status = new DesktopUpdateStatus(DesktopUpdateDecision.Optional, "0.3.17", 147, "Fixture", null);
            if (!install || confirm is null || !await confirm(status, token)) return status;
            progress?.Report(new(1, 1, Verifying: true));
            Ready = true;
            if (!await confirm(status with { ReadyToInstall = true, UsesSystemInstaller = true }, token)) return status;
            Progress = progress;
            progress?.Report(new(1, 1, Installing: true));
            await Installed.Task.WaitAsync(token);
            return status with { ApplicationRestartRequired = true };
        }
        public ValueTask<string> OpenDiagnosticsDirectoryAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<DesktopTerminalIntegrationSettings> GetTerminalIntegrationSettingsAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<string> ConfigureTerminalIntegrationAsync(bool logo, bool profile, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<string> RepairTerminalIntegrationAsync(CancellationToken token) => throw new NotSupportedException();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= end) throw new TimeoutException("macOS UI validation timed out.");
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class OpenRunner : IMacCommandRunner
    {
        public int Calls { get; private set; }
        public ValueTask<MacProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new MacProcessResult(0, "", ""));
        }
    }

    private sealed class MacClient : IAgentClient
    {
        public bool FailNext { get; set; }
        public bool SupportsAiAgentDetection => true;
        public bool SupportsTerminalCommandEditing => false;
        public bool SupportsRemoteSessions => false;
        public bool SupportsBrowserAuthorizationCancellation => true;
        public ValueTask<IReadOnlyList<AiAgentCapability>> DetectAiAgentsAsync(CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<AiAgentCapability>>([new("codex", "Codex", "/test/codex", "1.0", "detected", "local_cli")]);
        public ValueTask<AgentStatusSnapshot> GetStatusAsync(bool forceRefresh, CancellationToken token)
        {
            if (FailNext) { FailNext = false; throw new IOException("Simulated IPC outage"); }
            return ValueTask.FromResult(new AgentStatusSnapshot(1, DateTimeOffset.UtcNow,
                new PcAgentIdentity(Guid.Parse("a87a387b-3730-4cba-9f25-c5251d34c028"), "Mac", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS", "0.3.16.146"),
                AgentConnectionState.Offline, true, "user", "Mac user", "https://example.invalid", false, null, null,
                new HashSet<string>(), [], [], []));
        }
        public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask RetryConnectionAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopSessionAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask GrantDeviceAsync(Guid id, string permission, CancellationToken token) => throw new NotSupportedException();
        public ValueTask RevokeDeviceAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask BeginAccountAuthorizationAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public ValueTask LoginAsync(string user, string password, string name, CancellationToken token) => throw new NotSupportedException();
        public ValueTask RegisterAsync(string user, string nickname, string email, string password, string confirmation, string name, CancellationToken token) => throw new NotSupportedException();
        public ValueTask SignOutAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask SaveTerminalCommandsAsync(IReadOnlyList<TerminalCommandCapability> commands, CancellationToken token) => throw new NotSupportedException();
    }
}
