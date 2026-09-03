using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using TermLink.Pc.Contracts;
using TermLink.Platform;

namespace TermLink.Presentation;

public abstract class PageViewModel(string key, string title) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
}

public readonly record struct NavigationMeasurement(
    string FromPage,
    string ToPage,
    TimeSpan Duration,
    DateTimeOffset ObservedAt);

public interface INavigationTelemetry
{
    void Record(NavigationMeasurement measurement);
}

public enum AppearancePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public interface IAppearanceController
{
    AppearancePreference Current { get; }
    void Apply(AppearancePreference preference);
}

public enum UiNoticeSeverity
{
    None = 0,
    Info = 1,
    Success = 2,
    Warning = 3,
    Error = 4,
}

public sealed class ConnectionPageViewModel : ObservableObject
{
    private string summary = PresentationText.ConnectingAgent;
    private string detail = string.Empty;
    private bool remoteAccessEnabled;
    private AgentConnectionState state = AgentConnectionState.Connecting;

    public string Summary { get => summary; internal set => SetProperty(ref summary, value); }
    public string Detail { get => detail; internal set => SetProperty(ref detail, value); }
    public bool RemoteAccessEnabled { get => remoteAccessEnabled; internal set => SetProperty(ref remoteAccessEnabled, value); }
    public AgentConnectionState State { get => state; internal set => SetProperty(ref state, value); }
}

public sealed record SessionListItemViewModel(
    Guid SessionId,
    string Title,
    string ShellType,
    string StateText,
    string ViewerText,
    string LastActivityText,
    bool IsHealthy,
    bool IsWarning,
    bool IsError,
    bool CanStop)
{
    public string DetailText => $"{ShellType} · {LastActivityText}";
}

public sealed class SessionsPageViewModel : PageViewModel
{
    public SessionsPageViewModel() : base("sessions", PresentationText.Sessions) { }
    public ObservableCollection<ManagedSessionSnapshot> Sessions { get; } = new();
    public ObservableCollection<SessionListItemViewModel> Items { get; } = new();
}

public sealed record DeviceListItemViewModel(
    Guid DeviceId,
    string Name,
    string DeviceType,
    string TrustState,
    string OnlineState,
    bool Online)
{
    public string DetailText => $"{DeviceType} · {TrustState}";
}

public sealed class DevicesPageViewModel : PageViewModel
{
    public DevicesPageViewModel() : base("devices", PresentationText.Devices) { }
    public ObservableCollection<AccountDeviceSnapshot> Devices { get; } = new();
    public ObservableCollection<DeviceListItemViewModel> Items { get; } = new();
}

public sealed class AccountPageViewModel : ObservableObject
{
    private string pcName = Environment.MachineName;
    private bool accountLinked;
    private string currentUsername = string.Empty;
    private string currentNickname = string.Empty;

    public string PcName { get => pcName; set => SetProperty(ref pcName, value); }
    public bool AccountLinked { get => accountLinked; internal set => SetProperty(ref accountLinked, value); }
    public string CurrentUsername
    {
        get => currentUsername;
        internal set
        {
            if (!SetProperty(ref currentUsername, value)) return;
            RaiseAccountIdentityProperties();
        }
    }
    public string CurrentNickname
    {
        get => currentNickname;
        internal set
        {
            if (!SetProperty(ref currentNickname, value)) return;
            RaiseAccountIdentityProperties();
        }
    }
    public string AccountDisplayName => FirstNonEmpty(CurrentNickname, CurrentUsername, PresentationText.Account);
    public string AccountUsernameDisplay => string.IsNullOrWhiteSpace(CurrentUsername) ? string.Empty : $"@{CurrentUsername.Trim()}";
    public string AccountAvatarInitial
    {
        get
        {
            var source = FirstNonEmpty(CurrentNickname, CurrentUsername, PresentationText.Account);
            var initial = StringInfo.GetNextTextElement(source.Trim());
            return initial.Length == 1 ? initial.ToUpperInvariant() : initial;
        }
    }
    private void RaiseAccountIdentityProperties()
    {
        RaisePropertyChanged(nameof(AccountDisplayName));
        RaisePropertyChanged(nameof(AccountUsernameDisplay));
        RaisePropertyChanged(nameof(AccountAvatarInitial));
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value)).Trim();
}

public sealed class SettingsPageViewModel : PageViewModel
{
    private string platform = string.Empty;
    private string version = string.Empty;
    private string newCommandName = string.Empty;
    private string newExecutable = string.Empty;
    private string newArguments = string.Empty;
    private string? editingCommandId;
    private bool commandEditorOpen;
    private bool hasUnsavedCommands;
    private string releaseNotes = string.Empty;
    private string releaseNotesTitle = PresentationText.AppShipReleaseNotes;
    private string updateStatusText = PresentationText.UpdateNotChecked;
    private bool terminalIntegrationEditorOpen;
    private bool terminalAddNoLogo = true;
    private bool terminalAddNoProfile;
    private bool canConfigureTerminalIntegration = true;

    public SettingsPageViewModel() : base("settings", PresentationText.Settings) { }
    public string Platform { get => platform; internal set => SetProperty(ref platform, value); }
    public string Version { get => version; internal set => SetProperty(ref version, value); }
    public string NewCommandName { get => newCommandName; set => SetProperty(ref newCommandName, value); }
    public string NewExecutable { get => newExecutable; set => SetProperty(ref newExecutable, value); }
    public string NewArguments { get => newArguments; set => SetProperty(ref newArguments, value); }
    public string ApplicationVersion { get; internal set; } = "1.0.0.0";
    public string Author { get; internal set; } = "清风惹尘丶";
    public string ReleaseNotes
    {
        get => releaseNotes;
        internal set
        {
            if (SetProperty(ref releaseNotes, value)) RaisePropertyChanged(nameof(HasReleaseNotes));
        }
    }
    public string ReleaseNotesTitle { get => releaseNotesTitle; internal set => SetProperty(ref releaseNotesTitle, value); }
    public string UpdateStatusText { get => updateStatusText; internal set => SetProperty(ref updateStatusText, value); }
    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);
    public bool TerminalIntegrationEditorOpen
    {
        get => terminalIntegrationEditorOpen;
        internal set => SetProperty(ref terminalIntegrationEditorOpen, value);
    }
    public bool TerminalAddNoLogo
    {
        get => terminalAddNoLogo;
        set => SetProperty(ref terminalAddNoLogo, value);
    }
    public bool TerminalAddNoProfile
    {
        get => terminalAddNoProfile;
        set => SetProperty(ref terminalAddNoProfile, value);
    }
    public bool CanConfigureTerminalIntegration
    {
        get => canConfigureTerminalIntegration;
        internal set => SetProperty(ref canConfigureTerminalIntegration, value);
    }
    public string? EditingCommandId { get => editingCommandId; internal set => SetProperty(ref editingCommandId, value); }
    public bool CommandEditorOpen { get => commandEditorOpen; internal set => SetProperty(ref commandEditorOpen, value); }
    public bool HasUnsavedCommands { get => hasUnsavedCommands; internal set => SetProperty(ref hasUnsavedCommands, value); }
    public ObservableCollection<TerminalCommandCapability> TerminalCommands { get; } = new();
    internal List<TerminalCommandCapability> RemovedTerminalCommands { get; } = new();
}

public sealed class DiagnosticsPageViewModel : ObservableObject
{
    private string pcId = "-";
    private string revision = "-";
    private string observedAt = "-";
    private string serverOrigin = "-";
    private string capabilities = "-";
    private string lastError = "-";

    public string PcId { get => pcId; internal set => SetProperty(ref pcId, value); }
    public string Revision { get => revision; internal set => SetProperty(ref revision, value); }
    public string ObservedAt { get => observedAt; internal set => SetProperty(ref observedAt, value); }
    public string ServerOrigin { get => serverOrigin; internal set => SetProperty(ref serverOrigin, value); }
    public string Capabilities { get => capabilities; internal set => SetProperty(ref capabilities, value); }
    public string LastError { get => lastError; internal set => SetProperty(ref lastError, value); }
}

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    public static TimeSpan DefaultUpdateCheckInterval => TimeSpan.FromMinutes(30);

    private static readonly string[] SettingsSections = ["general", "account", "commands", "integration", "diagnostics", "about"];
    private readonly AgentStateStore stateStore;
    private readonly IAgentClient client;
    private readonly IDesktopOperations? desktopOperations;
    private readonly IAppearanceController? appearanceController;
    private readonly IUiDispatcher dispatcher;
    private readonly INavigationTelemetry? navigationTelemetry;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly SemaphoreSlim updateCheckLock = new(1, 1);
    private readonly TimeSpan updateCheckInterval;
    private readonly TimeSpan noticeAutoDismissDelay;
    private readonly Dictionary<string, PageViewModel> pages;
    private PageViewModel currentPage;
    private string settingsSection = "general";
    private string agentName = "TermLink";
    private string statusText = PresentationText.Pick("正在连接", "Connecting");
    private string operationMessage = string.Empty;
    private UiNoticeSeverity noticeSeverity;
    private string noticeTitle = string.Empty;
    private string noticeDetail = string.Empty;
    private string noticeCode = string.Empty;
    private long noticeVersion;
    private bool confirmationVisible;
    private string confirmationTitle = string.Empty;
    private string confirmationDetail = string.Empty;
    private TaskCompletionSource<bool>? pendingConfirmation;
    private AppearancePreference appearancePreference;
    private bool accountStateKnown;
    private bool hasAvailableUpdate;
    private string availableUpdateText = string.Empty;
    private Task? updatePolling;

    public MainWindowViewModel(
        AgentStateStore stateStore,
        IAgentClient client,
        IUiDispatcher dispatcher,
        PlatformDescriptor descriptor,
        INavigationTelemetry? navigationTelemetry = null,
        IDesktopOperations? desktopOperations = null,
        IAppearanceController? appearanceController = null,
        string applicationVersion = "1.0.0.0",
        string author = "清风惹尘丶",
        TimeSpan? updateCheckInterval = null,
        TimeSpan? noticeAutoDismissDelay = null)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.navigationTelemetry = navigationTelemetry;
        this.desktopOperations = desktopOperations;
        this.appearanceController = appearanceController;
        this.updateCheckInterval = updateCheckInterval ?? DefaultUpdateCheckInterval;
        if (this.updateCheckInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(updateCheckInterval));
        this.noticeAutoDismissDelay = noticeAutoDismissDelay ?? TimeSpan.FromSeconds(5);
        if (this.noticeAutoDismissDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(noticeAutoDismissDelay));
        appearancePreference = appearanceController?.Current ?? AppearancePreference.System;
        ArgumentNullException.ThrowIfNull(descriptor);

        Connection = new ConnectionPageViewModel();
        Sessions = new SessionsPageViewModel();
        Devices = new DevicesPageViewModel();
        Account = new AccountPageViewModel();
        Diagnostics = new DiagnosticsPageViewModel();
        Settings = new SettingsPageViewModel
        {
            Platform = $"{descriptor.Platform} · {descriptor.Architecture} · {descriptor.PlatformVersion}",
            ApplicationVersion = applicationVersion,
            Author = author,
        };
        pages = new PageViewModel[] { Devices, Sessions, Settings }
            .ToDictionary(page => page.Key, StringComparer.Ordinal);
        currentPage = Devices;

        NavigateCommand = new AsyncCommand((parameter, _) =>
        {
            Navigate(parameter as string);
            return Task.CompletedTask;
        });
        SelectSettingsSectionCommand = new AsyncCommand((parameter, _) =>
        {
            SelectSettingsSection(parameter as string);
            return Task.CompletedTask;
        });
        SetAppearanceCommand = new AsyncCommand((parameter, _) =>
        {
            if (Enum.TryParse<AppearancePreference>(parameter as string, true, out var preference))
                SetAppearance(preference);
            return Task.CompletedTask;
        });
        RefreshCommand = new AsyncCommand((_, cancellationToken) => RefreshAsync(cancellationToken));
        SetRemoteAccessCommand = new AsyncCommand((parameter, cancellationToken) =>
            SetRemoteAccessAsync(ParseBoolean(parameter), cancellationToken));
        RetryConnectionCommand = new AsyncCommand((_, cancellationToken) =>
            MutateAsync(client.RetryConnectionAsync, PresentationText.RetryRequested, cancellationToken));
        OpenAccountSettingsCommand = new AsyncCommand((_, _) =>
        {
            Navigate("settings");
            SelectSettingsSection("account");
            return Task.CompletedTask;
        });
        OpenAboutUpdatesCommand = new AsyncCommand((_, _) =>
        {
            Navigate("settings");
            SelectSettingsSection("about");
            return Task.CompletedTask;
        });
        StopSessionCommand = new AsyncCommand((parameter, cancellationToken) =>
            parameter is Guid sessionId ? StopSessionAsync(sessionId, cancellationToken) : Task.CompletedTask);
        GrantDeviceCommand = new AsyncCommand((parameter, cancellationToken) =>
            parameter is Guid deviceId
                ? MutateAsync(token => client.GrantDeviceAsync(deviceId, "CONTROL", token), PresentationText.DeviceGranted, cancellationToken)
                : Task.CompletedTask);
        RevokeDeviceCommand = new AsyncCommand((parameter, cancellationToken) =>
            parameter is Guid deviceId ? RevokeDeviceAsync(deviceId, cancellationToken) : Task.CompletedTask);
        BeginAccountAuthorizationCommand = new AsyncCommand((_, cancellationToken) =>
            BeginAccountAuthorizationAsync(cancellationToken));
        SignOutCommand = new AsyncCommand((_, cancellationToken) => SignOutAsync(cancellationToken));
        OpenTerminalCommandEditorCommand = new AsyncCommand((parameter, _) =>
        {
            OpenTerminalCommandEditor(parameter as TerminalCommandCapability);
            return Task.CompletedTask;
        });
        AddTerminalCommandCommand = new AsyncCommand((_, _) =>
        {
            CommitTerminalCommandEditor();
            return Task.CompletedTask;
        });
        CancelTerminalCommandEditorCommand = new AsyncCommand((_, _) =>
        {
            CloseTerminalCommandEditor();
            return Task.CompletedTask;
        });
        RemoveTerminalCommandCommand = new AsyncCommand((parameter, _) =>
        {
            RemoveTerminalCommand(parameter as TerminalCommandCapability);
            return Task.CompletedTask;
        });
        SaveTerminalCommandsCommand = new AsyncCommand((_, cancellationToken) => SaveTerminalCommandsAsync(cancellationToken));
        CheckForUpdatesCommand = new AsyncCommand((_, cancellationToken) =>
            CheckForUpdatesAsync(interactive: true, showNotice: true, cancellationToken));
        UpdateActionCommand = new AsyncCommand((_, cancellationToken) =>
            CheckForUpdatesAsync(interactive: true, showNotice: true, cancellationToken));
        OpenDiagnosticsDirectoryCommand = new AsyncCommand((_, cancellationToken) =>
            RunDesktopOperationAsync(token => RequireDesktopOperations().OpenDiagnosticsDirectoryAsync(token), cancellationToken));
        RepairTerminalIntegrationCommand = new AsyncCommand((_, cancellationToken) =>
            RunDesktopOperationAsync(token => RequireDesktopOperations().RepairTerminalIntegrationAsync(token), cancellationToken));
        OpenTerminalIntegrationSettingsCommand = new AsyncCommand((_, cancellationToken) =>
            OpenTerminalIntegrationSettingsAsync(cancellationToken));
        SaveTerminalIntegrationSettingsCommand = new AsyncCommand((_, cancellationToken) =>
            SaveTerminalIntegrationSettingsAsync(cancellationToken));
        CancelTerminalIntegrationSettingsCommand = new AsyncCommand((_, _) =>
        {
            Settings.TerminalIntegrationEditorOpen = false;
            return Task.CompletedTask;
        });
        DismissNoticeCommand = new AsyncCommand((_, _) =>
        {
            ClearNotice();
            return Task.CompletedTask;
        });
        ConfirmPendingActionCommand = new AsyncCommand((_, _) =>
        {
            pendingConfirmation?.TrySetResult(true);
            return Task.CompletedTask;
        });
        CancelPendingActionCommand = new AsyncCommand((_, _) =>
        {
            pendingConfirmation?.TrySetResult(false);
            return Task.CompletedTask;
        });
        stateStore.Changed += StateChanged;
    }

    public ConnectionPageViewModel Connection { get; }
    public SessionsPageViewModel Sessions { get; }
    public DevicesPageViewModel Devices { get; }
    public AccountPageViewModel Account { get; }
    public SettingsPageViewModel Settings { get; }
    public DiagnosticsPageViewModel Diagnostics { get; }
    public AsyncCommand NavigateCommand { get; }
    public AsyncCommand SelectSettingsSectionCommand { get; }
    public AsyncCommand SetAppearanceCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SetRemoteAccessCommand { get; }
    public AsyncCommand RetryConnectionCommand { get; }
    public AsyncCommand OpenAccountSettingsCommand { get; }
    public AsyncCommand OpenAboutUpdatesCommand { get; }
    public AsyncCommand StopSessionCommand { get; }
    public AsyncCommand GrantDeviceCommand { get; }
    public AsyncCommand RevokeDeviceCommand { get; }
    public AsyncCommand BeginAccountAuthorizationCommand { get; }
    public AsyncCommand SignOutCommand { get; }
    public AsyncCommand OpenTerminalCommandEditorCommand { get; }
    public AsyncCommand AddTerminalCommandCommand { get; }
    public AsyncCommand CancelTerminalCommandEditorCommand { get; }
    public AsyncCommand RemoveTerminalCommandCommand { get; }
    public AsyncCommand SaveTerminalCommandsCommand { get; }
    public AsyncCommand CheckForUpdatesCommand { get; }
    public AsyncCommand UpdateActionCommand { get; }
    public AsyncCommand OpenDiagnosticsDirectoryCommand { get; }
    public AsyncCommand RepairTerminalIntegrationCommand { get; }
    public AsyncCommand OpenTerminalIntegrationSettingsCommand { get; }
    public AsyncCommand SaveTerminalIntegrationSettingsCommand { get; }
    public AsyncCommand CancelTerminalIntegrationSettingsCommand { get; }
    public AsyncCommand DismissNoticeCommand { get; }
    public AsyncCommand ConfirmPendingActionCommand { get; }
    public AsyncCommand CancelPendingActionCommand { get; }
    public IReadOnlyCollection<PageViewModel> Pages => pages.Values;
    public PageViewModel CurrentPage { get => currentPage; private set => SetProperty(ref currentPage, value); }
    public bool IsConnectionPageSelected => IsDevicesPageSelected;
    public bool IsDevicesPageSelected => ReferenceEquals(CurrentPage, Devices);
    public bool IsSessionsPageSelected => ReferenceEquals(CurrentPage, Sessions);
    public bool IsSettingsPageSelected => ReferenceEquals(CurrentPage, Settings);
    public bool IsAccountPageSelected => IsSettingsPageSelected && IsAccountSettingsSelected;
    public bool IsDiagnosticsPageSelected => IsSettingsPageSelected && IsDiagnosticsSettingsSelected;
    public string SettingsSection => settingsSection;
    public bool IsGeneralSettingsSelected => settingsSection == "general";
    public bool IsAccountSettingsSelected => settingsSection == "account";
    public bool IsCommandsSettingsSelected => settingsSection == "commands";
    public bool IsIntegrationSettingsSelected => settingsSection == "integration";
    public bool IsDiagnosticsSettingsSelected => settingsSection == "diagnostics";
    public bool IsAboutSettingsSelected => settingsSection == "about";
    public string CurrentSettingsSectionTitle => settingsSection switch
    {
        "account" => PresentationText.Account,
        "commands" => PresentationText.Commands,
        "integration" => PresentationText.Integration,
        "diagnostics" => PresentationText.Diagnostics,
        "about" => PresentationText.About,
        _ => PresentationText.General,
    };
    public string CurrentPageSubtitle => CurrentPage.Key switch
    {
        "devices" => PresentationText.DeviceHeaderSummary(RemoteAccessStatusText, ActiveSessionCount),
        "sessions" => SessionCountText,
        "settings" => CurrentSettingsSectionTitle,
        _ => PresentationText.Console,
    };
    public string AgentName { get => agentName; private set => SetProperty(ref agentName, value); }
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }
    public string OperationMessage { get => operationMessage; private set => SetProperty(ref operationMessage, value); }
    public UiNoticeSeverity NoticeSeverity { get => noticeSeverity; private set => SetProperty(ref noticeSeverity, value); }
    public string NoticeTitle { get => noticeTitle; private set => SetProperty(ref noticeTitle, value); }
    public string NoticeDetail { get => noticeDetail; private set => SetProperty(ref noticeDetail, value); }
    public string NoticeCode { get => noticeCode; private set => SetProperty(ref noticeCode, value); }
    public bool HasNotice => NoticeSeverity != UiNoticeSeverity.None;
    public bool IsInfoNotice => NoticeSeverity == UiNoticeSeverity.Info;
    public bool IsSuccessNotice => NoticeSeverity == UiNoticeSeverity.Success;
    public bool IsWarningNotice => NoticeSeverity == UiNoticeSeverity.Warning;
    public bool IsErrorNotice => NoticeSeverity == UiNoticeSeverity.Error;
    public bool ConfirmationVisible { get => confirmationVisible; private set => SetProperty(ref confirmationVisible, value); }
    public string ConfirmationTitle { get => confirmationTitle; private set => SetProperty(ref confirmationTitle, value); }
    public string ConfirmationDetail { get => confirmationDetail; private set => SetProperty(ref confirmationDetail, value); }
    public AppearancePreference AppearancePreference { get => appearancePreference; private set => SetProperty(ref appearancePreference, value); }
    public bool IsSystemAppearance => AppearancePreference == AppearancePreference.System;
    public bool IsLightAppearance => AppearancePreference == AppearancePreference.Light;
    public bool IsDarkAppearance => AppearancePreference == AppearancePreference.Dark;
    public bool IsConnected => Connection.State == AgentConnectionState.Connected;
    public bool IsConnectionBusy => Connection.State is AgentConnectionState.Connecting or AgentConnectionState.Reconnecting;
    public bool IsServerDisconnected => !IsConnected && !IsConnectionBusy;
    public string ServerConnectionToolTip => string.IsNullOrWhiteSpace(Connection.Detail)
        ? StatusText
        : $"{StatusText} · {Connection.Detail}";
    public bool CanRetryConnection => Connection.State is AgentConnectionState.Offline or AgentConnectionState.Faulted;
    public bool CanEnableRemoteAccess => !Connection.RemoteAccessEnabled && Account.AccountLinked && IsConnected;
    public bool CanDisableRemoteAccess => Connection.RemoteAccessEnabled && !RequiresReauthentication;
    public string RemoteAccessStatusText => Connection.RemoteAccessEnabled ? PresentationText.RemoteAccessOn : PresentationText.RemoteAccessOff;
    public int ActiveSessionCount => Sessions.Sessions.Count(session => session.State is ManagedSessionState.Starting or ManagedSessionState.Connected or ManagedSessionState.Disconnected);
    public string SessionCountText => PresentationText.SessionCount(Sessions.Items.Count);
    public int OnlineDeviceCount => Devices.Devices.Count(device => device.Online);
    public bool HasSessions => Sessions.Items.Count > 0;
    public bool HasNoSessions => !HasSessions;
    public bool HasDevices => Devices.Items.Count > 0;
    public bool HasNoDevices => !HasDevices;
    public bool RequiresReauthentication => Connection.State == AgentConnectionState.ReauthenticationRequired;
    public bool IsAccountStateKnown => accountStateKnown;
    public bool IsAccountStateLoading => !IsAccountStateKnown;
    public bool IsAccountLinked => IsAccountStateKnown && Account.AccountLinked && !RequiresReauthentication;
    public bool IsAccountSignedOut => IsAccountStateKnown && !IsAccountLinked;
    public string AccountStateTitle => RequiresReauthentication
        ? PresentationText.AuthenticationExpired
        : PresentationText.AccountSignedOut;
    public string AccountStateDetail => RequiresReauthentication
        ? PresentationText.AuthenticationExpiredDetail
        : PresentationText.SignInDetail;
    public string AccountActionText => RequiresReauthentication ? PresentationText.SignInAgain : PresentationText.Login;
    public bool HasAvailableUpdate
    {
        get => hasAvailableUpdate;
        private set
        {
            if (!SetProperty(ref hasAvailableUpdate, value)) return;
            RaisePropertyChanged(nameof(UpdateActionText));
        }
    }
    public string AvailableUpdateText { get => availableUpdateText; private set => SetProperty(ref availableUpdateText, value); }
    public string UpdateActionText => HasAvailableUpdate ? PresentationText.StartUpdate : PresentationText.CheckUpdates;

    public void Start()
    {
        stateStore.Start();
        if (desktopOperations is not null)
            updatePolling ??= PollForUpdatesAsync(lifetimeCancellation.Token);
    }

    public void Navigate(string? key)
    {
        if (key is "connection") key = "devices";
        if (key is "account" or "diagnostics")
        {
            SelectSettingsSection(key);
            key = "settings";
        }
        if (key is null || !pages.TryGetValue(key, out var page) || ReferenceEquals(page, CurrentPage)) return;
        var fromPage = CurrentPage.Key;
        var startedAt = Stopwatch.GetTimestamp();
        CurrentPage = page;
        RaiseNavigationProperties();
        navigationTelemetry?.Record(new NavigationMeasurement(
            fromPage,
            page.Key,
            Stopwatch.GetElapsedTime(startedAt),
            DateTimeOffset.UtcNow));
    }

    public void SelectSettingsSection(string? key)
    {
        if (key is null || !SettingsSections.Contains(key, StringComparer.Ordinal) || settingsSection == key) return;
        settingsSection = key;
        RaisePropertyChanged(nameof(SettingsSection));
        RaisePropertyChanged(nameof(IsGeneralSettingsSelected));
        RaisePropertyChanged(nameof(IsAccountSettingsSelected));
        RaisePropertyChanged(nameof(IsCommandsSettingsSelected));
        RaisePropertyChanged(nameof(IsIntegrationSettingsSelected));
        RaisePropertyChanged(nameof(IsDiagnosticsSettingsSelected));
        RaisePropertyChanged(nameof(IsAboutSettingsSelected));
        RaisePropertyChanged(nameof(IsAccountPageSelected));
        RaisePropertyChanged(nameof(IsDiagnosticsPageSelected));
        RaisePropertyChanged(nameof(CurrentSettingsSectionTitle));
        RaisePropertyChanged(nameof(CurrentPageSubtitle));
    }

    private void SetAppearance(AppearancePreference preference)
    {
        appearanceController?.Apply(preference);
        AppearancePreference = preference;
        RaisePropertyChanged(nameof(IsSystemAppearance));
        RaisePropertyChanged(nameof(IsLightAppearance));
        RaisePropertyChanged(nameof(IsDarkAppearance));
    }

    private void StateChanged(object? sender, AgentStatusSnapshot snapshot) =>
        _ = dispatcher.InvokeAsync(() => Apply(snapshot));

    private void Apply(AgentStatusSnapshot snapshot)
    {
        AgentName = snapshot.Identity.Name;
        StatusText = LocalizeConnectionState(snapshot.ConnectionState);
        Connection.State = snapshot.ConnectionState;
        Connection.Summary = StatusText;
        Connection.Detail = string.IsNullOrWhiteSpace(snapshot.LastErrorDetail)
            ? snapshot.ServerOrigin
            : LocalizeFailure(snapshot.LastErrorCode, snapshot.LastErrorDetail).Detail;
        Connection.RemoteAccessEnabled = snapshot.RemoteAccessEnabled;
        Settings.Version = snapshot.Identity.AgentVersion;
        Account.AccountLinked = snapshot.AccountLinked;
        Account.CurrentUsername = snapshot.AccountUsername;
        Account.CurrentNickname = snapshot.AccountNickname;
        accountStateKnown = true;
        if (string.IsNullOrWhiteSpace(Account.PcName)) Account.PcName = snapshot.Identity.Name;
        ObservableCollectionDiff.Apply(Sessions.Sessions, snapshot.Sessions, item => item.SessionId);
        ObservableCollectionDiff.Apply(Devices.Devices, snapshot.Devices, item => item.DeviceId);
        var sessionItems = snapshot.Sessions.Select(CreateSessionItem).ToArray();
        ObservableCollectionDiff.Apply(Sessions.Items, sessionItems, item => item.SessionId);
        var deviceItems = snapshot.Devices.Select(CreateDeviceItem).ToArray();
        ObservableCollectionDiff.Apply(Devices.Items, deviceItems, item => item.DeviceId);
        if (!Settings.HasUnsavedCommands && !Settings.CommandEditorOpen)
            ObservableCollectionDiff.Apply(
                Settings.TerminalCommands,
                snapshot.TerminalCommands.Select(command => command with { RunAsAdministrator = true }).ToArray(),
                item => item.Id);
        Diagnostics.PcId = snapshot.Identity.PcId.ToString("D");
        Diagnostics.Revision = snapshot.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Diagnostics.ObservedAt = snapshot.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        Diagnostics.ServerOrigin = string.IsNullOrWhiteSpace(snapshot.ServerOrigin) ? "-" : snapshot.ServerOrigin;
        Diagnostics.Capabilities = snapshot.Capabilities.Count == 0
            ? "-"
            : string.Join(", ", snapshot.Capabilities.Order(StringComparer.Ordinal));
        Diagnostics.LastError = string.IsNullOrWhiteSpace(snapshot.LastErrorCode)
            ? PresentationText.Pick("无", "None")
            : snapshot.LastErrorCode + ": " +
                LocalizeFailure(snapshot.LastErrorCode, snapshot.LastErrorDetail).Detail;
        RaiseStatusProperties();
        RaiseAccountStateProperties();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            ShowNotice(UiNoticeSeverity.Info, PresentationText.Refreshing, string.Empty);
            await stateStore.RefreshAsync(true, cancellationToken).ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () => ShowNotice(UiNoticeSeverity.Success, PresentationText.OperationCompleted, string.Empty),
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ShowFailureAsync(error);
        }
    }

    private async Task SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled == Connection.RemoteAccessEnabled) return;
        if (!enabled && !await RequestConfirmationAsync(
                PresentationText.ConfirmStopRemoteTitle,
                PresentationText.ConfirmStopRemoteDetail,
                cancellationToken)) return;
        await MutateAsync(
            token => client.SetRemoteAccessAsync(enabled, token),
            PresentationText.RemoteAccessUpdated,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StopSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = Sessions.Items.FirstOrDefault(item => item.SessionId == sessionId);
        if (session is null || !session.CanStop) return;
        if (!await RequestConfirmationAsync(
                PresentationText.ConfirmStopSessionTitle,
                PresentationText.ConfirmStopSessionDetail(session.Title),
                cancellationToken)) return;
        await MutateAsync(
            token => client.StopSessionAsync(sessionId, token),
            PresentationText.SessionStopped,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = Devices.Items.FirstOrDefault(item => item.DeviceId == deviceId);
        if (device is null) return;
        if (!await RequestConfirmationAsync(
                PresentationText.ConfirmRevokeDeviceTitle,
                PresentationText.ConfirmRevokeDeviceDetail(device.Name),
                cancellationToken)) return;
        await MutateAsync(
            token => client.RevokeDeviceAsync(deviceId, token),
            PresentationText.DeviceRevoked,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task BeginAccountAuthorizationAsync(CancellationToken cancellationToken)
    {
        try
        {
            ShowNotice(
                UiNoticeSeverity.Info,
                PresentationText.BrowserAuthorizationStarted,
                PresentationText.BrowserAuthorizationStartedDetail);
            await client.BeginAccountAuthorizationAsync(Account.PcName, cancellationToken).ConfigureAwait(false);
            await stateStore.RefreshAsync(true, cancellationToken).ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () => ShowNotice(
                    UiNoticeSeverity.Success,
                    PresentationText.BrowserAuthorizationSucceeded,
                    string.Empty),
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ShowFailureAsync(error);
        }
    }

    private async Task SignOutAsync(CancellationToken cancellationToken)
    {
        if (!Account.AccountLinked) return;
        if (!await RequestConfirmationAsync(
                PresentationText.ConfirmSignOutTitle,
                PresentationText.ConfirmSignOutDetail,
                cancellationToken)) return;
        await MutateAsync(client.SignOutAsync, PresentationText.SignedOut, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunDesktopOperationAsync(
        Func<CancellationToken, ValueTask<string>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            ShowNotice(UiNoticeSeverity.Info, PresentationText.Processing, string.Empty);
            var message = await operation(cancellationToken).ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () => ShowNotice(
                    UiNoticeSeverity.Success,
                    string.IsNullOrWhiteSpace(message) ? PresentationText.OperationCompleted : message,
                    string.Empty),
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ShowFailureAsync(error);
        }
    }

    private async Task OpenTerminalIntegrationSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await RequireDesktopOperations()
                .GetTerminalIntegrationSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                Settings.TerminalAddNoLogo = settings.AddNoLogo;
                Settings.TerminalAddNoProfile = settings.AddNoProfile;
                Settings.CanConfigureTerminalIntegration = settings.CanConfigure;
                Settings.TerminalIntegrationEditorOpen = true;
            }, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ShowFailureAsync(error);
        }
    }

    private async Task SaveTerminalIntegrationSettingsAsync(CancellationToken cancellationToken)
    {
        if (!Settings.CanConfigureTerminalIntegration) return;
        await RunDesktopOperationAsync(
            token => RequireDesktopOperations().ConfigureTerminalIntegrationAsync(
                Settings.TerminalAddNoLogo,
                Settings.TerminalAddNoProfile,
                token),
            cancellationToken).ConfigureAwait(false);
        if (NoticeSeverity == UiNoticeSeverity.Success)
        {
            await dispatcher.InvokeAsync(
                () => Settings.TerminalIntegrationEditorOpen = false,
                cancellationToken);
        }
    }

    private async Task CheckForUpdatesAsync(
        bool interactive,
        bool showNotice,
        CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await updateCheckLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (showNotice)
            {
                await dispatcher.InvokeAsync(
                    () => ShowNotice(UiNoticeSeverity.Info, PresentationText.CheckingUpdates, string.Empty),
                    cancellationToken);
            }
            var status = await RequireDesktopOperations()
                .CheckForUpdatesAsync(interactive, cancellationToken)
                .ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                ApplyUpdateStatus(status);
                if (!showNotice) return;
                var severity = status.Decision switch
                {
                    DesktopUpdateDecision.Error => UiNoticeSeverity.Error,
                    DesktopUpdateDecision.NotConfigured => UiNoticeSeverity.Warning,
                    _ => UiNoticeSeverity.Success,
                };
                ShowNotice(
                    severity,
                    PresentationText.UpdateStatus(status),
                    string.Empty);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // App shutdown or command cancellation owns this outcome.
        }
        catch (Exception error)
        {
            var failure = LocalizeFailure(ErrorCode(error), error.Message);
            await dispatcher.InvokeAsync(() =>
            {
                ClearUpdateStatus(PresentationText.UpdateCheckFailed(failure.Detail));
                if (showNotice)
                {
                    ShowNotice(
                        UiNoticeSeverity.Error,
                        PresentationText.OperationFailedTitle,
                        failure.Detail,
                        failure.Code);
                }
            }, CancellationToken.None);
        }
        finally
        {
            if (entered) updateCheckLock.Release();
        }
    }

    private async Task PollForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckForUpdatesAsync(
                interactive: false,
                showNotice: false,
                cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(updateCheckInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckForUpdatesAsync(
                    interactive: false,
                    showNotice: false,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ApplyUpdateStatus(DesktopUpdateStatus status)
    {
        Settings.UpdateStatusText = PresentationText.UpdateStatus(status);
        Settings.ReleaseNotes = status.ReleaseNotes?.Trim() ?? string.Empty;
        Settings.ReleaseNotesTitle = PresentationText.UpdateReleaseNotesTitle(
            status.VersionName,
            status.BuildNumber);
        HasAvailableUpdate = status.Decision is DesktopUpdateDecision.Optional or DesktopUpdateDecision.Required;
        AvailableUpdateText = HasAvailableUpdate ? PresentationText.AvailableUpdate(status) : string.Empty;
    }

    private void ClearUpdateStatus(string statusText)
    {
        Settings.UpdateStatusText = statusText;
        Settings.ReleaseNotes = string.Empty;
        Settings.ReleaseNotesTitle = PresentationText.AppShipReleaseNotes;
    }

    private IDesktopOperations RequireDesktopOperations() =>
        desktopOperations ?? throw new PlatformNotSupportedException(PresentationText.UnsupportedDesktopOperation);

    private async Task MutateAsync(
        Func<CancellationToken, ValueTask> mutation,
        string successMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            ShowNotice(UiNoticeSeverity.Info, PresentationText.Processing, string.Empty);
            await mutation(cancellationToken).ConfigureAwait(false);
            await stateStore.RefreshAsync(true, cancellationToken).ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () => ShowNotice(UiNoticeSeverity.Success, successMessage, string.Empty),
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await ShowFailureAsync(error);
        }
    }

    private void OpenTerminalCommandEditor(TerminalCommandCapability? command)
    {
        Settings.EditingCommandId = command?.Id;
        Settings.NewCommandName = command?.DisplayName ?? string.Empty;
        Settings.NewExecutable = command?.Executable ?? string.Empty;
        Settings.NewArguments = command is null ? string.Empty : string.Join(' ', command.Arguments);
        Settings.CommandEditorOpen = true;
    }

    private void CommitTerminalCommandEditor()
    {
        var name = Settings.NewCommandName.Trim();
        var executable = Settings.NewExecutable.Trim();
        if (name.Length == 0 || executable.Length == 0)
        {
            ShowNotice(UiNoticeSeverity.Warning, PresentationText.CommandRequired, PresentationText.CommandsDetail);
            return;
        }
        var arguments = string.IsNullOrWhiteSpace(Settings.NewArguments)
            ? Array.Empty<string>()
            : new[] { Settings.NewArguments.Trim() };
        var editingIndex = Settings.EditingCommandId is null ? -1 : FindCommandIndex(Settings.EditingCommandId);
        if (editingIndex >= 0)
        {
            var existing = Settings.TerminalCommands[editingIndex];
            Settings.TerminalCommands[editingIndex] = existing with
            {
                DisplayName = name,
                Executable = executable,
                Arguments = arguments,
                RunAsAdministrator = true,
            };
            ShowNotice(UiNoticeSeverity.Info, PresentationText.CommandUpdated, string.Empty);
        }
        else
        {
            Settings.TerminalCommands.Add(new TerminalCommandCapability(
                "custom-" + Guid.NewGuid().ToString("N"),
                name,
                executable,
                arguments,
                false,
                true));
            ShowNotice(UiNoticeSeverity.Info, PresentationText.CommandAdded, string.Empty);
        }
        Settings.HasUnsavedCommands = true;
        CloseTerminalCommandEditor();
    }

    private void RemoveTerminalCommand(TerminalCommandCapability? command)
    {
        if (command is null) return;
        Settings.TerminalCommands.Remove(command);
        Settings.RemovedTerminalCommands.RemoveAll(item => item.Id == command.Id);
        Settings.RemovedTerminalCommands.Add(command with { Enabled = false });
        Settings.HasUnsavedCommands = true;
        ShowNotice(UiNoticeSeverity.Info, PresentationText.CommandRemoved, string.Empty);
    }

    private async Task SaveTerminalCommandsAsync(CancellationToken cancellationToken)
    {
        if (!Settings.HasUnsavedCommands) return;
        await MutateAsync(
            token => client.SaveTerminalCommandsAsync(
                Settings.TerminalCommands
                    .Concat(Settings.RemovedTerminalCommands)
                    .ToArray(),
                token),
            PresentationText.CommandsSaved,
            cancellationToken).ConfigureAwait(false);
        if (NoticeSeverity == UiNoticeSeverity.Success)
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    Settings.HasUnsavedCommands = false;
                    Settings.RemovedTerminalCommands.Clear();
                },
                cancellationToken);
        }
    }

    private void CloseTerminalCommandEditor()
    {
        Settings.EditingCommandId = null;
        Settings.NewCommandName = string.Empty;
        Settings.NewExecutable = string.Empty;
        Settings.NewArguments = string.Empty;
        Settings.CommandEditorOpen = false;
    }

    private int FindCommandIndex(string id)
    {
        for (var index = 0; index < Settings.TerminalCommands.Count; index++)
            if (string.Equals(Settings.TerminalCommands[index].Id, id, StringComparison.Ordinal)) return index;
        return -1;
    }

    public async Task<bool> RequestConfirmationAsync(string title, string detail, CancellationToken cancellationToken)
    {
        if (pendingConfirmation is not null) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingConfirmation = completion;
        ConfirmationTitle = title;
        ConfirmationDetail = detail;
        ConfirmationVisible = true;
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            pendingConfirmation = null;
            ConfirmationVisible = false;
            ConfirmationTitle = string.Empty;
            ConfirmationDetail = string.Empty;
        }
    }

    public Task ReportFailureAsync(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ShowFailureAsync(error);
    }

    private async Task ShowFailureAsync(Exception error)
    {
        var failure = LocalizeFailure(ErrorCode(error), error.Message);
        await dispatcher.InvokeAsync(
            () => ShowNotice(UiNoticeSeverity.Error, PresentationText.OperationFailedTitle, failure.Detail, failure.Code),
            CancellationToken.None);
    }

    private void ShowNotice(UiNoticeSeverity severity, string title, string detail, string code = "")
    {
        NoticeSeverity = severity;
        NoticeTitle = title;
        NoticeDetail = detail;
        NoticeCode = code;
        OperationMessage = string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}";
        RaiseNoticeProperties();
        var version = Interlocked.Increment(ref noticeVersion);
        _ = DismissNoticeAfterDelayAsync(version);
    }

    private void ClearNotice()
    {
        Interlocked.Increment(ref noticeVersion);
        NoticeSeverity = UiNoticeSeverity.None;
        NoticeTitle = string.Empty;
        NoticeDetail = string.Empty;
        NoticeCode = string.Empty;
        OperationMessage = string.Empty;
        RaiseNoticeProperties();
    }

    private async Task DismissNoticeAfterDelayAsync(long version)
    {
        try
        {
            await Task.Delay(noticeAutoDismissDelay, lifetimeCancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref noticeVersion) == version) ClearNotice();
            }, lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private void RaiseNoticeProperties()
    {
        RaisePropertyChanged(nameof(HasNotice));
        RaisePropertyChanged(nameof(IsInfoNotice));
        RaisePropertyChanged(nameof(IsSuccessNotice));
        RaisePropertyChanged(nameof(IsWarningNotice));
        RaisePropertyChanged(nameof(IsErrorNotice));
    }

    private void RaiseNavigationProperties()
    {
        RaisePropertyChanged(nameof(IsConnectionPageSelected));
        RaisePropertyChanged(nameof(IsDevicesPageSelected));
        RaisePropertyChanged(nameof(IsSessionsPageSelected));
        RaisePropertyChanged(nameof(IsSettingsPageSelected));
        RaisePropertyChanged(nameof(IsAccountPageSelected));
        RaisePropertyChanged(nameof(IsDiagnosticsPageSelected));
        RaisePropertyChanged(nameof(CurrentPageSubtitle));
    }

    private void RaiseStatusProperties()
    {
        RaisePropertyChanged(nameof(IsConnected));
        RaisePropertyChanged(nameof(IsConnectionBusy));
        RaisePropertyChanged(nameof(IsServerDisconnected));
        RaisePropertyChanged(nameof(ServerConnectionToolTip));
        RaisePropertyChanged(nameof(CanRetryConnection));
        RaisePropertyChanged(nameof(RequiresReauthentication));
        RaisePropertyChanged(nameof(CanEnableRemoteAccess));
        RaisePropertyChanged(nameof(CanDisableRemoteAccess));
        RaisePropertyChanged(nameof(RemoteAccessStatusText));
        RaisePropertyChanged(nameof(ActiveSessionCount));
        RaisePropertyChanged(nameof(SessionCountText));
        RaisePropertyChanged(nameof(OnlineDeviceCount));
        RaisePropertyChanged(nameof(HasSessions));
        RaisePropertyChanged(nameof(HasNoSessions));
        RaisePropertyChanged(nameof(HasDevices));
        RaisePropertyChanged(nameof(HasNoDevices));
        RaisePropertyChanged(nameof(AccountActionText));
        RaisePropertyChanged(nameof(CurrentPageSubtitle));
    }

    private void RaiseAccountStateProperties()
    {
        RaisePropertyChanged(nameof(IsAccountStateKnown));
        RaisePropertyChanged(nameof(IsAccountStateLoading));
        RaisePropertyChanged(nameof(IsAccountLinked));
        RaisePropertyChanged(nameof(IsAccountSignedOut));
        RaisePropertyChanged(nameof(AccountStateTitle));
        RaisePropertyChanged(nameof(AccountStateDetail));
        RaisePropertyChanged(nameof(AccountActionText));
        RaisePropertyChanged(nameof(CanEnableRemoteAccess));
    }

    private static bool ParseBoolean(object? value) =>
        value is bool boolean || bool.TryParse(value as string, out boolean) && boolean;

    private static string LocalizeConnectionState(AgentConnectionState state) => state switch
    {
        AgentConnectionState.NotConfigured => PresentationText.RelayNotConfigured,
        AgentConnectionState.Connecting => PresentationText.RelayConnecting,
        AgentConnectionState.Connected => PresentationText.RelayConnected,
        AgentConnectionState.Reconnecting => PresentationText.RelayReconnecting,
        AgentConnectionState.ReauthenticationRequired => PresentationText.RelaySignInRequired,
        AgentConnectionState.Offline => PresentationText.RelayOffline,
        AgentConnectionState.Faulted => PresentationText.RelayFaulted,
        _ => state.ToString(),
    };

    private static SessionListItemViewModel CreateSessionItem(ManagedSessionSnapshot session)
    {
        var stateText = session.State switch
        {
            ManagedSessionState.Starting => PresentationText.SessionStarting,
            ManagedSessionState.Connected => PresentationText.SessionConnected,
            ManagedSessionState.Disconnected => PresentationText.SessionDisconnected,
            ManagedSessionState.Ended => PresentationText.SessionEnded,
            ManagedSessionState.Interrupted => PresentationText.SessionInterrupted,
            _ => PresentationText.SessionFaulted,
        };
        var local = session.LastActivityAt.ToLocalTime();
        return new SessionListItemViewModel(
            session.SessionId,
            session.Title,
            session.ShellType,
            stateText,
            PresentationText.Viewers(session.ViewerCount),
            PresentationText.LastActive(local.ToString("MM-dd HH:mm")),
            session.State == ManagedSessionState.Connected,
            session.State is ManagedSessionState.Starting or ManagedSessionState.Disconnected,
            session.State is ManagedSessionState.Interrupted or ManagedSessionState.Faulted,
            session.State is not ManagedSessionState.Ended);
    }

    private static DeviceListItemViewModel CreateDeviceItem(AccountDeviceSnapshot device) => new(
        device.DeviceId,
        device.Name,
        LocalizeDeviceType(device.DeviceType),
        string.Equals(device.TrustState, "TRUSTED", StringComparison.OrdinalIgnoreCase)
            ? PresentationText.TrustedDevice
            : device.TrustState.Replace('_', ' '),
        device.Online ? PresentationText.DeviceOnline : PresentationText.DeviceOffline,
        device.Online);

    private static string LocalizeDeviceType(string type) => type.ToUpperInvariant() switch
    {
        "ANDROID" => PresentationText.AndroidDevice,
        "WINDOWS_PC" or "PC_AGENT" => PresentationText.WindowsDevice,
        "LINUX_PC" => PresentationText.LinuxDevice,
        "MACOS_PC" => PresentationText.MacDevice,
        _ => PresentationText.UnknownDevice,
    };

    private static string ErrorCode(Exception error)
    {
        if (error is AgentClientOperationException agentError) return agentError.Code;
        if (error is FileNotFoundException) return "PC_AGENT_MISSING";
        if (error is UnauthorizedAccessException) return "ACCESS_DENIED";
        if (error is TimeoutException) return "TIMEOUT";
        if (error is IOException) return "CONNECTION_FAILED";
        var text = error.Message ?? string.Empty;
        foreach (var code in new[]
        {
            "PRODUCTION_REQUEST_FAILED",
            "PRODUCTION_REQUEST_CANCELLED",
            "OPERATION_TIMEOUT",
            "INVALID_REQUEST",
            "ACCESS_DENIED",
            "UNAUTHORIZED",
            "TIMEOUT",
            "CONNECTION_FAILED",
            "RELAY_DISCONNECTED"
        })
            if (text.Contains(code, StringComparison.OrdinalIgnoreCase)) return code;
        return error.GetType().Name;
    }

    private static (string Code, string Detail) LocalizeFailure(string? code, string? rawDetail)
    {
        code ??= string.Empty;
        var normalized = code.Trim().ToUpperInvariant();
        var detail = normalized switch
        {
            "INVALID_REQUEST" => PresentationText.InvalidRequestDetail,
            "ACCESS_DENIED" or "UNAUTHORIZED" => PresentationText.AccessDeniedDetail,
            "TIMEOUT" or "TIMEOUTEXCEPTION" or "TASKCANCELEDEXCEPTION" or
                "PRODUCTION_REQUEST_CANCELLED" or "OPERATION_TIMEOUT" => PresentationText.TimeoutDetail,
            "CONNECTION_FAILED" or "RELAY_DISCONNECTED" or "IOEXCEPTION" or
                "PRODUCTION_REQUEST_FAILED" => PresentationText.ConnectionFailedDetail,
            "PC_AGENT_MISSING" or "PC-AGENT-MISSING" => PresentationText.AgentMissingDetail,
            "PC_AGENT_START_FAILED" => PresentationText.AgentStartFailedDetail,
            "PC_AGENT_STARTING_TIMEOUT" => PresentationText.AgentStartingTimeoutDetail,
            "PC_AGENT_RESTART_TIMEOUT" => PresentationText.AgentRestartTimeoutDetail,
            "PC_AGENT_EXITED" => PresentationText.AgentExitedDetail,
            "PC_AGENT_EXITING" => PresentationText.AgentExitingDetail,
            "PC_AGENT_PROCESSES_STILL_RUNNING" => PresentationText.AgentProcessesStillRunningDetail,
            "ACCOUNT_AUTHORIZATION_IN_PROGRESS" => PresentationText.BrowserAuthorizationInProgressDetail,
            "ACCOUNT_CALLBACK_LISTENER_FAILED" => PresentationText.BrowserCallbackListenerFailedDetail,
            "ACCOUNT_BROWSER_OPEN_FAILED" => PresentationText.BrowserOpenFailedDetail,
            "ACCOUNT_AUTHORIZATION_TIMEOUT" => PresentationText.BrowserAuthorizationTimeoutDetail,
            "ACCOUNT_AUTHORIZATION_REJECTED" => PresentationText.BrowserAuthorizationRejectedDetail,
            "ACCOUNT_AUTHORIZATION_FAILED" => PresentationText.BrowserAuthorizationFailedDetail,
            "ACCOUNT_IDENTITY_SERVICE_TIMEOUT" => PresentationText.IdentityServiceTimeoutDetail,
            "ACCOUNT_TOKEN_EXCHANGE_TIMEOUT" or "ACCOUNT_TOKEN_EXCHANGE_FAILED" => PresentationText.TokenExchangeFailedDetail,
            "PC_SHELL_OPEN_FAILED" => PresentationText.ShellOpenFailedDetail,
            "PC-AGENT-IPC-UNAVAILABLE" or "PC-AGENT-IPC-AUTHENTICATION" or
                "PC-AGENT-CREDENTIAL-ERROR" or "PC-AGENT-ACCESS-DENIED" or
                "PC-AGENT-TIMEOUT" or "PC-AGENT-UNAVAILABLE" => PresentationText.ConnectionFailedDetail,
            _ => PresentationText.UnexpectedFailureDetail,
        };
        return (string.IsNullOrWhiteSpace(code) ? "UNEXPECTED" : code, detail);
    }

    public async ValueTask DisposeAsync()
    {
        lifetimeCancellation.Cancel();
        pendingConfirmation?.TrySetResult(false);
        stateStore.Changed -= StateChanged;
        if (updatePolling is not null)
        {
            try { await updatePolling.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await stateStore.DisposeAsync().ConfigureAwait(false);
        lifetimeCancellation.Dispose();
    }
}
