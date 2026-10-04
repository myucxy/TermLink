using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

        Require(MainWindowViewModel.DefaultUpdateCheckInterval == TimeSpan.FromMinutes(30),
            "The default update-check interval is not 30 minutes.");

        var identity = new PcAgentIdentity(
            Guid.Parse("f77bd947-015a-49b5-9f93-df8397983209"),
            "headless-pc-with-a-name-that-must-not-expand-the-sidebar",
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
            "headless-user-with-a-very-long-account-name",
            "清风惹尘丶的超长昵称用于验证侧栏省略",
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
            desktopOperations,
            noticeAutoDismissDelay: TimeSpan.FromMilliseconds(250));
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
        var noticeOverlay = RequireControl<Grid>(window, "NoticeOverlay");
        var currentPageTitle = RequireControl<TextBlock>(window, "CurrentPageTitleText");
        var currentPageSubtitle = RequireControl<TextBlock>(window, "CurrentPageSubtitleText");
        var serverStatusPill = RequireControl<Border>(window, "ServerStatusPill");
        var serverStatusDot = RequireControl<Border>(window, "ServerStatusDot");
        var sidebarComputerCard = RequireControl<Border>(window, "SidebarComputerCard");
        var sidebarAccountButton = RequireControl<Button>(window, "SidebarAccountButton");
        var sidebarLoginButton = RequireControl<Button>(window, "SidebarLoginButton");
        var sidebarUpdateButton = RequireControl<Button>(window, "SidebarUpdateButton");
        var sidebarComputerName = RequireControl<TextBlock>(window, "SidebarComputerNameText");
        var sidebarNickname = RequireControl<TextBlock>(window, "SidebarNicknameText");
        var sidebarUsername = RequireControl<TextBlock>(window, "SidebarUsernameText");
        var sidebarAvatarInitial = RequireControl<TextBlock>(window, "SidebarAvatarInitialText");
        var aboutUpdateAction = RequireControl<Button>(window, "AboutUpdateActionButton");
        var confirmationOverlay = RequireControl<Control>(window, "ConfirmationOverlay");
        var updateProgressOverlay = RequireControl<Control>(window, "UpdateProgressOverlay");
        var cancelUpdateDownload = RequireControl<Button>(window, "CancelUpdateDownloadButton");
        var configureTerminalIntegration = RequireControl<Button>(window, "ConfigureTerminalIntegrationButton");
        var terminalIntegrationEditor = RequireControl<Control>(window, "TerminalIntegrationEditor");
        var terminalAddNoLogo = RequireControl<CheckBox>(window, "TerminalAddNoLogoCheckBox");
        var terminalAddNoProfile = RequireControl<CheckBox>(window, "TerminalAddNoProfileCheckBox");
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
        Require(viewModel.IsConnected && !viewModel.IsConnectionBusy && !viewModel.IsServerDisconnected &&
                viewModel.StatusText == PresentationText.RelayConnected &&
                serverStatusPill.Classes.Contains("positive") &&
                serverStatusDot.Classes.Contains("online") &&
                !serverStatusDot.Classes.Contains("warning") &&
                !serverStatusDot.Classes.Contains("error"),
            "The top-right server indicator did not show green only for a connected server.");
        Require(currentPageTitle.Text == PresentationText.Devices &&
                currentPageSubtitle.Text == PresentationText.DeviceHeaderSummary(
                    viewModel.RemoteAccessStatusText,
                    viewModel.ActiveSessionCount) &&
                !devicesContentRoot.GetLogicalDescendants().OfType<TextBlock>()
                    .Any(text => text.Text == identity.Name ||
                                 (text.Text?.Contains("Relay", StringComparison.OrdinalIgnoreCase) ?? false)),
            "The device page did not move its summary into the shared header or still repeated the PC name/Relay state.");
        Require(sidebarAccountButton.IsVisible && !sidebarLoginButton.IsVisible &&
                sidebarNickname.Text == snapshot.AccountNickname &&
                sidebarUsername.Text == $"@{snapshot.AccountUsername}" &&
                sidebarAvatarInitial.Text == "清",
            "The sidebar did not show the signed-in nickname, username, and Unicode avatar initial.");
        Require(sidebarComputerName.TextTrimming.ToString() == "CharacterEllipsis" &&
                sidebarNickname.TextTrimming.ToString() == "CharacterEllipsis" &&
                sidebarUsername.TextTrimming.ToString() == "CharacterEllipsis" &&
                sidebarComputerName.TextWrapping.ToString() == "NoWrap",
            "Long sidebar identity labels are not constrained to one ellipsized line.");
        var navigationButtons = window.GetLogicalDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("nav"))
            .ToArray();
        var computerNameOrigin = sidebarComputerName.TranslatePoint(new Point(0, 0), window);
        var nicknameOrigin = sidebarNickname.TranslatePoint(new Point(0, 0), window);
        Require(navigationButtons.Length == 3 &&
                navigationButtons.All(button =>
                    button.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Stretch &&
                    Math.Abs(button.Bounds.Width - sidebarComputerCard.Bounds.Width) < 0.5),
            "The primary navigation selection area does not fill the available sidebar width.");
        Require(sidebarAccountButton.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Stretch &&
                Math.Abs(sidebarComputerCard.Bounds.Width - sidebarAccountButton.Bounds.Width) < 0.5 &&
                computerNameOrigin.HasValue && nicknameOrigin.HasValue &&
                Math.Abs(computerNameOrigin.Value.X - nicknameOrigin.Value.X) < 0.5,
            "The sidebar computer and account cards do not share the same width and text baseline. " +
            $"computerWidth={sidebarComputerCard.Bounds.Width}, " +
            $"accountWidth={sidebarAccountButton.Bounds.Width}, " +
            $"computerX={computerNameOrigin?.X}, nicknameX={nicknameOrigin?.X}, " +
            $"accountAlignment={sidebarAccountButton.HorizontalAlignment}");
        Require(noticeOverlay.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right &&
                noticeOverlay.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Bottom &&
                Grid.GetRow(noticeOverlay) == 1 && noticeOverlay.GetValue(Panel.ZIndexProperty) > 0,
            "Notices are not rendered as a bottom-right overlay above page content.");
        Require(devicesContentRoot.Margin.Right >= 16 &&
                settingsContentRoot.Margin.Right >= 16 &&
                !settingsContentScroll.AllowAutoHide &&
                !ScrollViewer.GetAllowAutoHide(sessionList),
            "Compact-window scrollbars can still auto-hide over edge-aligned content.");

        viewModel.Navigate("sessions");
        Dispatcher.UIThread.RunJobs();
        Require(currentPageTitle.Text == PresentationText.Sessions &&
                currentPageSubtitle.Text == viewModel.SessionCountText,
            "The session page did not move its count into the shared header.");
        Require(sessionList.ItemsPanelRoot is VirtualizingStackPanel, "The session list is not virtualized.");
        viewModel.Navigate("settings");
        viewModel.SelectSettingsSection("agents");
        await DrainCommandAsync(viewModel.DetectAiAgentsCommand);
        Dispatcher.UIThread.RunJobs();
        Require(viewModel.IsAgentsSettingsSelected && viewModel.Settings.AiAgents.Single().Id == "zcode", "AI settings did not discover and display ZCode.");
        Require(viewModel.CurrentPageSubtitle == "AI Agent", "AI settings header still shows General.");
        viewModel.DetectAiAgentsCommand.Execute(null);
        await DrainCommandAsync(viewModel.DetectAiAgentsCommand);
        Require(viewModel.Settings.AiAgents.Count == 1, "AI rescan duplicated entries.");
        viewModel.Settings.ZcodeRemoteUrl = "https://zcode.z.ai/remote/v4?pair=fixture-only";
        viewModel.SaveZcodeRemoteCommand.Execute(null);
        await DrainCommandAsync(viewModel.SaveZcodeRemoteCommand);
        Require(client.RemoteConfigured && viewModel.Settings.ZcodeRemoteUrl.Length == 0 &&
                viewModel.Settings.AiAgents.Single().Strategy == "desktop_remote", "Saving remote config did not clear the input and refresh the strategy.");
        viewModel.ClearZcodeRemoteCommand.Execute(null);
        await DrainCommandAsync(viewModel.ClearZcodeRemoteCommand);
        Require(!client.RemoteConfigured && viewModel.Settings.AiAgents.Single().Strategy == "native", "Clearing config did not restore native strategy.");
        viewModel.SelectSettingsSection("commands");
        Dispatcher.UIThread.RunJobs();
        Require(currentPageTitle.Text == PresentationText.Settings &&
                currentPageSubtitle.Text == PresentationText.Commands,
            "The settings page did not move its selected section into the shared header.");
        Require(commandList.ItemsPanelRoot is VirtualizingStackPanel, "The command list is not virtualized.");

        viewModel.OpenTerminalCommandEditorCommand.Execute(null);
        await DrainCommandAsync(viewModel.OpenTerminalCommandEditorCommand);
        Require(viewModel.Settings.CommandEditorOpen, "The custom-command editor did not open explicitly.");
        viewModel.Settings.NewCommandName = "Custom shell";
        viewModel.Settings.NewExecutable = "custom-shell.exe";
        viewModel.Settings.NewArguments = "--login";
        viewModel.AddTerminalCommandCommand.Execute(null);
        await DrainCommandAsync(viewModel.AddTerminalCommandCommand);
        Require(viewModel.Settings.TerminalCommands.Count == 3, "A manual command was not added.");
        Require(viewModel.Settings.HasUnsavedCommands && !viewModel.Settings.CommandEditorOpen,
            "A custom command did not enter the explicit unsaved state.");
        var added = viewModel.Settings.TerminalCommands.Single(command => command.DisplayName == "Custom shell");
        Require(added.RunAsAdministrator,
            "A custom command did not enforce administrator launch after the UI option was removed.");
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
        desktopOperations.Status = new DesktopUpdateStatus(
            DesktopUpdateDecision.Optional,
            "0.3.0",
            52,
            "New version fixture",
            "fixture update available");
        viewModel.CheckForUpdatesCommand.Execute(null);
        await WaitForAsync(() => viewModel.ConfirmationVisible,
            "An available update did not open the main-window confirmation overlay.");
        Dispatcher.UIThread.RunJobs();
        Require(confirmationOverlay.IsVisible && viewModel.HasAvailableUpdate && sidebarUpdateButton.IsVisible &&
                viewModel.UpdateActionText == PresentationText.StartUpdate &&
                Equals(aboutUpdateAction.Content, PresentationText.StartUpdate),
            "An available update was not confirmed inside the main window.");
        viewModel.ConfirmPendingActionCommand.Execute(null);
        await DrainCommandAsync(viewModel.ConfirmPendingActionCommand);
        await WaitForAsync(
            () => desktopOperations.InstallRequested && viewModel.UpdateProgressVisible &&
                  viewModel.UpdateProgressPercent == 50,
            "A confirmed update did not expose live download progress.");
        Require(updateProgressOverlay.IsVisible && cancelUpdateDownload.IsVisible &&
                viewModel.UpdateProgressText.Contains("50%", StringComparison.Ordinal) &&
                viewModel.UpdateProgressText.Contains("5.0 MB", StringComparison.Ordinal),
            "The update progress overlay did not render progress and cancellation inside the main window.");
        cancelUpdateDownload.Command!.Execute(null);
        await DrainCommandAsync(viewModel.CancelUpdateDownloadCommand);
        await DrainCommandAsync(viewModel.CheckForUpdatesCommand);
        Require(desktopOperations.InstallCanceled && !viewModel.UpdateProgressVisible,
            "Cancelling the main-window update progress did not cancel and close the download.");
        desktopOperations.CompleteDownload = true;
        viewModel.CheckForUpdatesCommand.Execute(null);
        await WaitForAsync(() => viewModel.ConfirmationVisible, "Download confirmation missing.");
        viewModel.ConfirmPendingActionCommand.Execute(null);
        await DrainCommandAsync(viewModel.ConfirmPendingActionCommand);
        await WaitForAsync(() => viewModel.ConfirmationVisible && desktopOperations.VerifiedConfirmationRequested,
            "A verified download did not request separate installation confirmation.");
        Require(!viewModel.UpdateProgressVisible && viewModel.ConfirmationDetail.Contains("AI", StringComparison.Ordinal),
            "Install confirmation overlapped download progress or omitted active tasks.");
        viewModel.CancelPendingActionCommand.Execute(null);
        await DrainCommandAsync(viewModel.CancelPendingActionCommand);
        await DrainCommandAsync(viewModel.CheckForUpdatesCommand);
        Require(!desktopOperations.InstallerStarted && viewModel.Settings.UpdateStatusText.Contains("verified", StringComparison.OrdinalIgnoreCase),
            "Install later launched the installer or lost verified status.");
        viewModel.Navigate("devices");
        viewModel.OpenAboutUpdatesCommand.Execute(null);
        await DrainCommandAsync(viewModel.OpenAboutUpdatesCommand);
        Require(viewModel.IsSettingsPageSelected && viewModel.IsAboutSettingsSelected && aboutRoot.IsVisible,
            "The sidebar update entry did not navigate directly to About updates.");
        viewModel.SelectSettingsSection("integration");
        Dispatcher.UIThread.RunJobs();
        Require(viewModel.IsIntegrationSettingsSelected && integrationRoot.IsVisible,
            "The update action was not separated from the integration section.");
        configureTerminalIntegration.Command!.Execute(null);
        await DrainCommandAsync(viewModel.OpenTerminalIntegrationSettingsCommand);
        Dispatcher.UIThread.RunJobs();
        Require(terminalIntegrationEditor.IsVisible && terminalAddNoLogo.IsChecked == true &&
                terminalAddNoProfile.IsChecked == false,
            "The Terminal integration editor did not expose the requested parameter defaults.");
        viewModel.Settings.TerminalAddNoLogo = false;
        viewModel.Settings.TerminalAddNoProfile = true;
        viewModel.SaveTerminalIntegrationSettingsCommand.Execute(null);
        await DrainCommandAsync(viewModel.SaveTerminalIntegrationSettingsCommand);
        Dispatcher.UIThread.RunJobs();
        Require(!terminalIntegrationEditor.IsVisible &&
                desktopOperations.ConfiguredTerminalSettings == (false, true),
            "The Terminal integration editor did not save the two independent parameter switches. " +
            $"visible={terminalIntegrationEditor.IsVisible}, " +
            $"saved={desktopOperations.ConfiguredTerminalSettings}");

        viewModel.Navigate("sessions");
        var firstSessionId = viewModel.Sessions.Items[0].SessionId;
        viewModel.StopSessionCommand.Execute(firstSessionId);
        await WaitForAsync(() => viewModel.ConfirmationVisible, "A destructive session action did not request confirmation.");
        Require(!client.StopSessionCalled, "The session was stopped before confirmation.");
        Dispatcher.UIThread.RunJobs();
        Require(RequireControl<Button>(window, "CancelConfirmationButton").IsFocused,
            "Confirmation did not focus Cancel.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await DrainCommandAsync(viewModel.CancelPendingActionCommand);
        await DrainCommandAsync(viewModel.StopSessionCommand);
        Require(!client.StopSessionCalled, "Cancelling the confirmation still stopped the session.");

        desktopOperations.FailUpdateChecks = true;
        viewModel.CheckForUpdatesCommand.Execute(null);
        await DrainCommandAsync(viewModel.CheckForUpdatesCommand);
        Require(viewModel.OperationMessage.Contains(PresentationText.UnexpectedFailureDetail, StringComparison.Ordinal) &&
                !viewModel.Settings.HasReleaseNotes &&
                viewModel.Settings.UpdateStatusText.Contains("AppShip", StringComparison.Ordinal),
            "An AppShip failure was not localized or isolated from previously loaded release notes.");
        viewModel.OpenDiagnosticsDirectoryCommand.Execute(null);
        await DrainCommandAsync(viewModel.OpenDiagnosticsDirectoryCommand);
        Require(viewModel.NoticeCode == "ACCESS_DENIED" && viewModel.NoticeDetail == PresentationText.AccessDeniedDetail,
            "A path-access failure was not classified as ACCESS_DENIED.");
        await viewModel.ReportFailureAsync(new InvalidOperationException("PRODUCTION_REQUEST_FAILED"));
        Require(viewModel.NoticeCode == "PRODUCTION_REQUEST_FAILED" &&
                viewModel.NoticeDetail == PresentationText.ConnectionFailedDetail,
            "An unavailable production identity service was not shown as a connection failure.");
        await viewModel.ReportFailureAsync(new System.Security.Cryptography.CryptographicException("signature fixture"));
        Require(viewModel.NoticeCode == "UPDATE_VERIFICATION_FAILED" &&
                viewModel.NoticeDetail == PresentationText.UpdateVerificationFailedDetail,
            "A failed update signature check was not shown as an update verification failure.");
        await viewModel.ReportFailureAsync(new InvalidOperationException("无法启动已验证的 TermLink 安装程序。"));
        Require(viewModel.NoticeCode == "UPDATE_INSTALLER_LAUNCH_FAILED" &&
                viewModel.NoticeDetail == PresentationText.UpdateInstallerLaunchFailedDetail,
            "A failed installer launch was not shown as an installer launch failure.");
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            await viewModel.ReportFailureAsync(new AgentClientOperationException(
                "PC_AGENT_EXITED",
                "TermLink Agent failed: raw English diagnostic",
                125));
            Require(viewModel.NoticeCode == "PC_AGENT_EXITED" &&
                    viewModel.NoticeDetail == PresentationText.AgentExitedDetail &&
                    !viewModel.NoticeDetail.Contains("raw English", StringComparison.Ordinal),
                "A Chinese Agent failure exposed the raw English diagnostic.");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            await viewModel.ReportFailureAsync(new AgentClientOperationException("PC_AGENT_RESTART_TIMEOUT"));
            Require(viewModel.NoticeCode == "PC_AGENT_RESTART_TIMEOUT" &&
                    viewModel.NoticeDetail == PresentationText.AgentRestartTimeoutDetail,
                "The English Agent restart failure was not localized.");
            await viewModel.ReportFailureAsync(new AgentClientOperationException("ACCOUNT_CALLBACK_LISTENER_FAILED"));
            Require(viewModel.NoticeCode == "ACCOUNT_CALLBACK_LISTENER_FAILED" &&
                    viewModel.NoticeDetail == PresentationText.BrowserCallbackListenerFailedDetail,
                "A browser callback listener failure was not localized.");
            await viewModel.ReportFailureAsync(new AgentClientOperationException("ACCOUNT_BROWSER_OPEN_FAILED"));
            Require(viewModel.NoticeCode == "ACCOUNT_BROWSER_OPEN_FAILED" &&
                    viewModel.NoticeDetail == PresentationText.BrowserOpenFailedDetail,
                "A browser launch failure was not localized.");
            await viewModel.ReportFailureAsync(new AgentClientOperationException("ACCOUNT_AUTHORIZATION_FAILED"));
            Require(viewModel.NoticeCode == "ACCOUNT_AUTHORIZATION_FAILED" &&
                    viewModel.NoticeDetail == PresentationText.BrowserAuthorizationFailedDetail,
                "A legacy Agent browser-authorization failure was not localized.");
            await viewModel.ReportFailureAsync(new AgentClientOperationException("ACCOUNT_MACHINE_IDENTITY_UNAVAILABLE"));
            Require(viewModel.NoticeCode == "ACCOUNT_MACHINE_IDENTITY_UNAVAILABLE" &&
                    viewModel.NoticeDetail == PresentationText.MachineIdentityUnavailableDetail,
                "A stable-machine Server compatibility failure was not localized.");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
        Thread.Sleep(150);
        await viewModel.ReportFailureAsync(new TimeoutException("notice timer reset fixture"));
        Thread.Sleep(150);
        Dispatcher.UIThread.RunJobs();
        Require(viewModel.HasNotice && viewModel.NoticeCode == "TIMEOUT",
            "A newer notice did not reset the five-second auto-dismiss countdown.");
        Thread.Sleep(350); Dispatcher.UIThread.RunJobs();
        Require(viewModel.HasNotice && viewModel.NoticeCode == "TIMEOUT", "An actionable failure disappeared automatically.");

        client.CurrentSnapshot = AgentStatusSnapshot.Disconnected(identity, "PC-AGENT-TIMEOUT", "fixture") with { Revision = 100 };
        await stateStore.RefreshAsync(); Dispatcher.UIThread.RunJobs();
        Require(viewModel.AgentUnavailable && !viewModel.IsAccountSignedOut && !viewModel.HasNoSessions &&
            viewModel.Account.CurrentUsername == snapshot.AccountUsername && viewModel.Sessions.Items.All(s => !s.CanStop),
            "Unavailable IPC was misrepresented as sign-out/empty sessions or left destructive controls enabled.");
        client.CurrentSnapshot = snapshot with { Revision = 101 };
        await stateStore.RefreshAsync(); Dispatcher.UIThread.RunJobs();
        Require(!viewModel.AgentUnavailable && viewModel.Account.PcName == identity.Name, "Recovery did not restore the real registered identity.");
        viewModel.Sessions.Search = "session-099";
        Require(viewModel.Sessions.VisibleItems.Count == 1, "Session search did not filter real titles.");
        viewModel.Sessions.Search = "";

        var reauthenticationSnapshot = snapshot with
        {
            Revision = 2,
            ConnectionState = AgentConnectionState.ReauthenticationRequired,
            LastErrorCode = "ACCOUNT_REAUTH_REQUIRED",
            LastErrorDetail = "Authentication expired",
        };
        var reauthenticationClient = new StaticAgentClient(reauthenticationSnapshot);
        var reauthenticationStore = new AgentStateStore(reauthenticationClient, TimeSpan.FromMinutes(1));
        await using var reauthenticationViewModel = new MainWindowViewModel(
            reauthenticationStore,
            reauthenticationClient,
            new ImmediateUiDispatcher(),
            new PlatformDescriptor(PcPlatform.Windows, PcArchitecture.X64, "Windows headless", "win-x64", false));
        var reauthenticationWindow = new MainWindow { DataContext = reauthenticationViewModel };
        reauthenticationWindow.Show();
        await reauthenticationStore.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        reauthenticationViewModel.Navigate("settings");
        reauthenticationViewModel.SelectSettingsSection("account");
        Dispatcher.UIThread.RunJobs();
        Require(reauthenticationViewModel.Account.AccountLinked &&
                reauthenticationViewModel.RequiresReauthentication &&
                !reauthenticationViewModel.IsAccountLinked &&
                reauthenticationViewModel.IsAccountSignedOut &&
                !reauthenticationViewModel.CanRetryConnection &&
                !reauthenticationViewModel.CanDisableRemoteAccess,
            "Expired Windows credentials still appeared as a usable signed-in account.");
        var accountSignInCard = RequireControl<Control>(reauthenticationWindow, "AccountSignInCard");
        var browserAuthorizationButton = RequireControl<Button>(reauthenticationWindow, "BrowserAccountAuthorizationButton");
        Require(!RequireControl<Control>(reauthenticationWindow, "LinkedAccountCard").IsVisible &&
                accountSignInCard.IsVisible &&
                !RequireControl<Button>(reauthenticationWindow, "SidebarAccountButton").IsVisible &&
                RequireControl<Button>(reauthenticationWindow, "SidebarLoginButton").IsVisible &&
                reauthenticationViewModel.AccountActionText == PresentationText.SignInAgain &&
                reauthenticationViewModel.AccountStateTitle == PresentationText.AuthenticationExpired,
            "The account page did not replace Sign out with the web reauthorization entry.");
        Require(!accountSignInCard.GetLogicalDescendants().OfType<TextBox>().Any() &&
                browserAuthorizationButton.IsVisible &&
                Equals(browserAuthorizationButton.Content, PresentationText.BrowserAccountAuthorization),
            "The account page still exposed native credential fields or omitted the web authorization action.");
        browserAuthorizationButton.Command!.Execute(null);
        await DrainCommandAsync(reauthenticationViewModel.BeginAccountAuthorizationCommand);
        Require(reauthenticationClient.BeginAccountAuthorizationCalled &&
                reauthenticationClient.AuthorizedPcName == reauthenticationViewModel.Account.PcName,
            "The web authorization action did not send the current PC name to the Agent.");
        reauthenticationViewModel.Navigate("devices");
        reauthenticationViewModel.OpenAccountSettingsCommand.Execute(null);
        await DrainCommandAsync(reauthenticationViewModel.OpenAccountSettingsCommand);
        Require(reauthenticationViewModel.IsSettingsPageSelected &&
                reauthenticationViewModel.IsAccountSettingsSelected,
            "The sign-in-expired action did not navigate directly to web account authorization.");
        reauthenticationWindow.Close();

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

        var periodicOperations = new FixtureDesktopOperations();
        var periodicClient = new StaticAgentClient(snapshot);
        var periodicStore = new AgentStateStore(periodicClient, TimeSpan.FromMinutes(1));
        var periodicViewModel = new MainWindowViewModel(
            periodicStore,
            periodicClient,
            new ImmediateUiDispatcher(),
            new PlatformDescriptor(PcPlatform.Windows, PcArchitecture.X64, "Windows headless", "win-x64", false),
            desktopOperations: periodicOperations,
            updateCheckInterval: TimeSpan.FromMilliseconds(20));
        try
        {
            periodicViewModel.Start();
            Require(
                SpinWait.SpinUntil(() => periodicOperations.CheckCount >= 2, TimeSpan.FromSeconds(5)),
                "The lifecycle update monitor did not perform periodic silent checks.");
        }
        finally
        {
            periodicViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            periodicStore.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

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

    private static Task WaitForAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
        Require(condition(), message);
        return Task.CompletedTask;
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
        public AgentStatusSnapshot CurrentSnapshot { get; set; } = snapshot;
        public bool StopSessionCalled { get; private set; }
        public bool BeginAccountAuthorizationCalled { get; private set; }
        public string AuthorizedPcName { get; private set; } = string.Empty;
        public IReadOnlyList<TerminalCommandCapability> SavedTerminalCommands { get; private set; } = Array.Empty<TerminalCommandCapability>();
        public bool SupportsZcodeRemoteConfiguration => true;
        public bool RemoteConfigured { get; private set; }
        public ValueTask ConfigureZcodeRemoteAsync(string url, CancellationToken cancellationToken) {
            RemoteConfigured = url.Length > 0;
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<AiAgentCapability>> DetectAiAgentsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AiAgentCapability>>(new[] { new AiAgentCapability("zcode", "ZCode", "D:\\ZCode\\zcode.exe", "1", "available", RemoteConfigured ? "desktop_remote" : "native") });
        public ValueTask<AgentStatusSnapshot> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CurrentSnapshot);

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
        public ValueTask BeginAccountAuthorizationAsync(string pcName, CancellationToken cancellationToken)
        {
            BeginAccountAuthorizationCalled = true;
            AuthorizedPcName = pcName;
            return ValueTask.CompletedTask;
        }
        public ValueTask LoginAsync(string username, string password, string pcName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RegisterAsync(string username, string nickname, string email, string password, string passwordConfirmation, string pcName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
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
        public bool InstallRequested { get; private set; }
        public bool InstallCanceled { get; private set; }
        public bool CompleteDownload { get; set; }
        public bool VerifiedConfirmationRequested { get; private set; }
        public bool InstallerStarted { get; private set; }
        private int checkCount;
        public int CheckCount => Volatile.Read(ref checkCount);
        public (bool AddNoLogo, bool AddNoProfile)? ConfiguredTerminalSettings { get; private set; }
        public DesktopUpdateStatus Status { get; set; } = new(
            DesktopUpdateDecision.NoUpdate,
            "0.2.4",
            45,
            "AppShip release fixture",
            "fixture is current");

        public async ValueTask<DesktopUpdateStatus> CheckForUpdatesAsync(
            bool install,
            Func<DesktopUpdateStatus, CancellationToken, ValueTask<bool>>? confirm,
            IProgress<DesktopUpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref checkCount);
            if (FailUpdateChecks) throw new InvalidOperationException("operation fixture");
            if (!install || Status.Decision is not (DesktopUpdateDecision.Optional or DesktopUpdateDecision.Required))
                return Status;
            if (confirm is null || !await confirm(Status, cancellationToken)) return Status;
            InstallRequested = true;
            progress?.Report(new DesktopUpdateProgress(5 * 1024 * 1024, 10 * 1024 * 1024));
            if (CompleteDownload) {
                progress?.Report(new DesktopUpdateProgress(10 * 1024 * 1024, 10 * 1024 * 1024, true));
                var ready = Status with { ReadyToInstall = true, Message = "Downloaded and verified" };
                VerifiedConfirmationRequested = true;
                InstallerStarted = await confirm(ready, cancellationToken);
                return ready;
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                InstallCanceled = true;
                throw;
            }
            return Status;
        }
        public ValueTask<string> OpenDiagnosticsDirectoryAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<string>(new UnauthorizedAccessException("Access to path is denied"));
        public ValueTask<DesktopTerminalIntegrationSettings> GetTerminalIntegrationSettingsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DesktopTerminalIntegrationSettings(
                AddNoLogo: true,
                AddNoProfile: false,
                CanConfigure: true));
        public ValueTask<string> ConfigureTerminalIntegrationAsync(
            bool addNoLogo,
            bool addNoProfile,
            CancellationToken cancellationToken)
        {
            ConfiguredTerminalSettings = (addNoLogo, addNoProfile);
            return ValueTask.FromResult("configured");
        }
        public ValueTask<string> RepairTerminalIntegrationAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult("repaired");
    }
}
