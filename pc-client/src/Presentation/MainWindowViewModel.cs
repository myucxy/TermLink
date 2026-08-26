using System.Collections.ObjectModel;
using System.Diagnostics;
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

public sealed class ConnectionPageViewModel : PageViewModel
{
    private string summary = PresentationText.ConnectingAgent;
    private string detail = string.Empty;
    private bool remoteAccessEnabled;

    public ConnectionPageViewModel() : base("connection", PresentationText.Connection) { }

    public string Summary { get => summary; internal set => SetProperty(ref summary, value); }
    public string Detail { get => detail; internal set => SetProperty(ref detail, value); }
    public bool RemoteAccessEnabled { get => remoteAccessEnabled; internal set => SetProperty(ref remoteAccessEnabled, value); }
}

public sealed class SessionsPageViewModel : PageViewModel
{
    public SessionsPageViewModel() : base("sessions", PresentationText.Sessions) { }
    public ObservableCollection<ManagedSessionSnapshot> Sessions { get; } = new();
}

public sealed class DevicesPageViewModel : PageViewModel
{
    public DevicesPageViewModel() : base("devices", PresentationText.Devices) { }
    public ObservableCollection<AccountDeviceSnapshot> Devices { get; } = new();
}

public sealed class AccountPageViewModel : PageViewModel
{
    private string username = string.Empty;
    private string email = string.Empty;
    private string password = string.Empty;
    private string passwordConfirmation = string.Empty;
    private string pcName = Environment.MachineName;
    private bool accountLinked;
    private string accountDisplayName = string.Empty;

    public AccountPageViewModel() : base("account", PresentationText.Account) { }
    public string Username { get => username; set => SetProperty(ref username, value); }
    public string Email { get => email; set => SetProperty(ref email, value); }
    public string Password { get => password; set => SetProperty(ref password, value); }
    public string PasswordConfirmation { get => passwordConfirmation; set => SetProperty(ref passwordConfirmation, value); }
    public string PcName { get => pcName; set => SetProperty(ref pcName, value); }
    public bool AccountLinked { get => accountLinked; internal set => SetProperty(ref accountLinked, value); }
    public string AccountDisplayName { get => accountDisplayName; internal set => SetProperty(ref accountDisplayName, value); }
}

public sealed class SettingsPageViewModel : PageViewModel
{
    private string platform = string.Empty;
    private string version = string.Empty;
    private string newCommandName = string.Empty;
    private string newExecutable = string.Empty;
    private string newArguments = string.Empty;

    public SettingsPageViewModel() : base("settings", PresentationText.Settings) { }
    public string Platform { get => platform; internal set => SetProperty(ref platform, value); }
    public string Version { get => version; internal set => SetProperty(ref version, value); }
    public string NewCommandName { get => newCommandName; set => SetProperty(ref newCommandName, value); }
    public string NewExecutable { get => newExecutable; set => SetProperty(ref newExecutable, value); }
    public string NewArguments { get => newArguments; set => SetProperty(ref newArguments, value); }
    public ObservableCollection<TerminalCommandCapability> TerminalCommands { get; } = new();
}

public sealed class DiagnosticsPageViewModel : PageViewModel
{
    private string pcId = "-";
    private string revision = "-";
    private string observedAt = "-";
    private string serverOrigin = "-";
    private string capabilities = "-";
    private string lastError = "-";

    public DiagnosticsPageViewModel() : base("diagnostics", PresentationText.Diagnostics) { }
    public string PcId { get => pcId; internal set => SetProperty(ref pcId, value); }
    public string Revision { get => revision; internal set => SetProperty(ref revision, value); }
    public string ObservedAt { get => observedAt; internal set => SetProperty(ref observedAt, value); }
    public string ServerOrigin { get => serverOrigin; internal set => SetProperty(ref serverOrigin, value); }
    public string Capabilities { get => capabilities; internal set => SetProperty(ref capabilities, value); }
    public string LastError { get => lastError; internal set => SetProperty(ref lastError, value); }
}

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AgentStateStore stateStore;
    private readonly IAgentClient client;
    private readonly IDesktopOperations? desktopOperations;
    private readonly IUiDispatcher dispatcher;
    private readonly INavigationTelemetry? navigationTelemetry;
    private readonly Dictionary<string, PageViewModel> pages;
    private PageViewModel currentPage;
    private string agentName = "TermLink";
    private string statusText = PresentationText.Pick("正在连接", "Connecting");
    private string operationMessage = string.Empty;

    public MainWindowViewModel(
        AgentStateStore stateStore,
        IAgentClient client,
        IUiDispatcher dispatcher,
        PlatformDescriptor descriptor,
        INavigationTelemetry? navigationTelemetry = null,
        IDesktopOperations? desktopOperations = null)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.navigationTelemetry = navigationTelemetry;
        this.desktopOperations = desktopOperations;
        ArgumentNullException.ThrowIfNull(descriptor);
        Connection = new ConnectionPageViewModel();
        Sessions = new SessionsPageViewModel();
        Devices = new DevicesPageViewModel();
        Account = new AccountPageViewModel();
        Diagnostics = new DiagnosticsPageViewModel();
        Settings = new SettingsPageViewModel
        {
            Platform = $"{descriptor.Platform} · {descriptor.Architecture} · {descriptor.PlatformVersion}",
        };
        pages = new PageViewModel[] { Connection, Sessions, Devices, Account, Settings, Diagnostics }
            .ToDictionary(page => page.Key, StringComparer.Ordinal);
        currentPage = Sessions;
        NavigateCommand = new AsyncCommand((parameter, _) =>
        {
            Navigate(parameter as string);
            return Task.CompletedTask;
        });
        RefreshCommand = new AsyncCommand((_, cancellationToken) => stateStore.RefreshAsync(true, cancellationToken));
        SetRemoteAccessCommand = new AsyncCommand((parameter, cancellationToken) =>
            MutateAsync(
                token => client.SetRemoteAccessAsync(
                    parameter is bool enabled || bool.TryParse(parameter as string, out enabled) && enabled,
                    token),
                PresentationText.RemoteAccessUpdated,
                cancellationToken));
        RetryConnectionCommand = new AsyncCommand((_, cancellationToken) =>
            MutateAsync(client.RetryConnectionAsync, PresentationText.RetryRequested, cancellationToken));
        StopSessionCommand = new AsyncCommand((parameter, cancellationToken) =>
            parameter is Guid sessionId
                ? MutateAsync(token => client.StopSessionAsync(sessionId, token), PresentationText.SessionStopped, cancellationToken)
                : Task.CompletedTask);
        GrantDeviceCommand = new AsyncCommand((parameter, cancellationToken) =>
            parameter is Guid deviceId
                ? MutateAsync(token => client.GrantDeviceAsync(deviceId, "CONTROL", token), PresentationText.DeviceGranted, cancellationToken)
                : Task.CompletedTask);
        RevokeDeviceCommand = new AsyncCommand((parameter, cancellationToken) =>
            parameter is Guid deviceId
                ? MutateAsync(token => client.RevokeDeviceAsync(deviceId, token), PresentationText.DeviceRevoked, cancellationToken)
                : Task.CompletedTask);
        LoginCommand = new AsyncCommand((_, cancellationToken) =>
            MutateAsync(
                token => client.LoginAsync(Account.Username, Account.Password, Account.PcName, token),
                PresentationText.LoginSucceeded,
                cancellationToken,
                clearPasswords: true));
        RegisterCommand = new AsyncCommand((_, cancellationToken) =>
            MutateAsync(
                token => client.RegisterAsync(
                    Account.Username,
                    Account.Email,
                    Account.Password,
                    Account.PasswordConfirmation,
                    Account.PcName,
                    token),
                PresentationText.RegistrationSucceeded,
                cancellationToken,
                clearPasswords: true));
        SignOutCommand = new AsyncCommand((_, cancellationToken) =>
            MutateAsync(client.SignOutAsync, PresentationText.SignedOut, cancellationToken, clearPasswords: true));
        AddTerminalCommandCommand = new AsyncCommand((_, _) =>
        {
            AddTerminalCommand();
            return Task.CompletedTask;
        });
        RemoveTerminalCommandCommand = new AsyncCommand((parameter, _) =>
        {
            if (parameter is TerminalCommandCapability command && !command.Detected)
                Settings.TerminalCommands.Remove(command);
            return Task.CompletedTask;
        });
        SaveTerminalCommandsCommand = new AsyncCommand((_, cancellationToken) =>
            MutateAsync(
                token => client.SaveTerminalCommandsAsync(
                    Settings.TerminalCommands.Where(command => !command.Detected).ToArray(),
                    token),
                PresentationText.CommandsSaved,
                cancellationToken));
        CheckForUpdatesCommand = new AsyncCommand((_, cancellationToken) =>
            RunDesktopOperationAsync(
                token => RequireDesktopOperations().CheckForUpdatesAsync(token),
                cancellationToken));
        OpenDiagnosticsDirectoryCommand = new AsyncCommand((_, cancellationToken) =>
            RunDesktopOperationAsync(
                token => RequireDesktopOperations().OpenDiagnosticsDirectoryAsync(token),
                cancellationToken));
        RepairTerminalIntegrationCommand = new AsyncCommand((_, cancellationToken) =>
            RunDesktopOperationAsync(
                token => RequireDesktopOperations().RepairTerminalIntegrationAsync(token),
                cancellationToken));
        stateStore.Changed += StateChanged;
    }

    public ConnectionPageViewModel Connection { get; }
    public SessionsPageViewModel Sessions { get; }
    public DevicesPageViewModel Devices { get; }
    public AccountPageViewModel Account { get; }
    public SettingsPageViewModel Settings { get; }
    public DiagnosticsPageViewModel Diagnostics { get; }
    public AsyncCommand NavigateCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SetRemoteAccessCommand { get; }
    public AsyncCommand RetryConnectionCommand { get; }
    public AsyncCommand StopSessionCommand { get; }
    public AsyncCommand GrantDeviceCommand { get; }
    public AsyncCommand RevokeDeviceCommand { get; }
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand RegisterCommand { get; }
    public AsyncCommand SignOutCommand { get; }
    public AsyncCommand AddTerminalCommandCommand { get; }
    public AsyncCommand RemoveTerminalCommandCommand { get; }
    public AsyncCommand SaveTerminalCommandsCommand { get; }
    public AsyncCommand CheckForUpdatesCommand { get; }
    public AsyncCommand OpenDiagnosticsDirectoryCommand { get; }
    public AsyncCommand RepairTerminalIntegrationCommand { get; }
    public IReadOnlyCollection<PageViewModel> Pages => pages.Values;
    public PageViewModel CurrentPage { get => currentPage; private set => SetProperty(ref currentPage, value); }
    public bool IsConnectionPageSelected => ReferenceEquals(CurrentPage, Connection);
    public bool IsSessionsPageSelected => ReferenceEquals(CurrentPage, Sessions);
    public bool IsDevicesPageSelected => ReferenceEquals(CurrentPage, Devices);
    public bool IsAccountPageSelected => ReferenceEquals(CurrentPage, Account);
    public bool IsSettingsPageSelected => ReferenceEquals(CurrentPage, Settings);
    public bool IsDiagnosticsPageSelected => ReferenceEquals(CurrentPage, Diagnostics);
    public string AgentName { get => agentName; private set => SetProperty(ref agentName, value); }
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }
    public string OperationMessage { get => operationMessage; private set => SetProperty(ref operationMessage, value); }

    public void Start()
    {
        stateStore.Start();
    }

    public void Navigate(string? key)
    {
        if (key is null || !pages.TryGetValue(key, out var page) || ReferenceEquals(page, CurrentPage)) return;
        var fromPage = CurrentPage.Key;
        var startedAt = Stopwatch.GetTimestamp();
        CurrentPage = page;
        RaisePropertyChanged(nameof(IsConnectionPageSelected));
        RaisePropertyChanged(nameof(IsSessionsPageSelected));
        RaisePropertyChanged(nameof(IsDevicesPageSelected));
        RaisePropertyChanged(nameof(IsAccountPageSelected));
        RaisePropertyChanged(nameof(IsSettingsPageSelected));
        RaisePropertyChanged(nameof(IsDiagnosticsPageSelected));
        navigationTelemetry?.Record(new NavigationMeasurement(
            fromPage,
            page.Key,
            Stopwatch.GetElapsedTime(startedAt),
            DateTimeOffset.UtcNow));
    }

    private void StateChanged(object? sender, AgentStatusSnapshot snapshot)
    {
        _ = dispatcher.InvokeAsync(() => Apply(snapshot));
    }

    private void Apply(AgentStatusSnapshot snapshot)
    {
        AgentName = snapshot.Identity.Name;
        StatusText = snapshot.ConnectionState switch
        {
            AgentConnectionState.Connected => PresentationText.Pick("Relay 已连接", "Relay connected"),
            AgentConnectionState.Reconnecting => PresentationText.Pick("正在重连", "Reconnecting"),
            AgentConnectionState.ReauthenticationRequired => PresentationText.Pick("需要重新登录", "Sign-in required"),
            AgentConnectionState.Faulted => PresentationText.Pick("Agent 异常", "Agent fault"),
            _ => snapshot.ConnectionState.ToString(),
        };
        Connection.Summary = StatusText;
        Connection.Detail = string.IsNullOrWhiteSpace(snapshot.LastErrorDetail)
            ? snapshot.ServerOrigin
            : $"{snapshot.LastErrorCode}: {snapshot.LastErrorDetail}";
        Connection.RemoteAccessEnabled = snapshot.RemoteAccessEnabled;
        Settings.Version = snapshot.Identity.AgentVersion;
        Account.AccountLinked = snapshot.AccountLinked;
        Account.AccountDisplayName = snapshot.AccountDisplayName;
        ObservableCollectionDiff.Apply(Sessions.Sessions, snapshot.Sessions, item => item.SessionId);
        ObservableCollectionDiff.Apply(Devices.Devices, snapshot.Devices, item => item.DeviceId);
        ObservableCollectionDiff.Apply(Settings.TerminalCommands, snapshot.TerminalCommands, item => item.Id);
        Diagnostics.PcId = snapshot.Identity.PcId.ToString("D");
        Diagnostics.Revision = snapshot.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Diagnostics.ObservedAt = snapshot.ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        Diagnostics.ServerOrigin = string.IsNullOrWhiteSpace(snapshot.ServerOrigin) ? "-" : snapshot.ServerOrigin;
        Diagnostics.Capabilities = snapshot.Capabilities.Count == 0
            ? "-"
            : string.Join(", ", snapshot.Capabilities.Order(StringComparer.Ordinal));
        Diagnostics.LastError = string.IsNullOrWhiteSpace(snapshot.LastErrorCode)
            ? PresentationText.Pick("无", "None")
            : snapshot.LastErrorCode + ": " + snapshot.LastErrorDetail;
    }

    private async Task RunDesktopOperationAsync(
        Func<CancellationToken, ValueTask<string>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            OperationMessage = PresentationText.Processing;
            var message = await operation(cancellationToken).ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () => OperationMessage = string.IsNullOrWhiteSpace(message) ? PresentationText.OperationCompleted : message,
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await dispatcher.InvokeAsync(
                () => OperationMessage = PresentationText.OperationFailed + error.Message,
                CancellationToken.None);
        }
    }

    private IDesktopOperations RequireDesktopOperations() =>
        desktopOperations ?? throw new PlatformNotSupportedException(PresentationText.UnsupportedDesktopOperation);

    private async Task MutateAsync(
        Func<CancellationToken, ValueTask> mutation,
        string successMessage,
        CancellationToken cancellationToken,
        bool clearPasswords = false)
    {
        try
        {
            OperationMessage = PresentationText.Processing;
            await mutation(cancellationToken).ConfigureAwait(false);
            if (clearPasswords)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    Account.Password = string.Empty;
                    Account.PasswordConfirmation = string.Empty;
                }, cancellationToken);
            }
            await stateStore.RefreshAsync(true, cancellationToken).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => OperationMessage = successMessage, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await dispatcher.InvokeAsync(
                () => OperationMessage = PresentationText.OperationFailed + error.Message,
                CancellationToken.None);
        }
    }

    private void AddTerminalCommand()
    {
        var name = Settings.NewCommandName.Trim();
        var executable = Settings.NewExecutable.Trim();
        if (name.Length == 0 || executable.Length == 0)
        {
            OperationMessage = PresentationText.CommandRequired;
            return;
        }
        Settings.TerminalCommands.Add(new TerminalCommandCapability(
            "custom-" + Guid.NewGuid().ToString("N"),
            name,
            executable,
            string.IsNullOrWhiteSpace(Settings.NewArguments)
                ? Array.Empty<string>()
                : new[] { Settings.NewArguments.Trim() },
            false));
        Settings.NewCommandName = string.Empty;
        Settings.NewExecutable = string.Empty;
        Settings.NewArguments = string.Empty;
        OperationMessage = PresentationText.CommandAdded;
    }

    public async ValueTask DisposeAsync()
    {
        stateStore.Changed -= StateChanged;
        await stateStore.DisposeAsync().ConfigureAwait(false);
    }
}
