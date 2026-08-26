using System.Collections.ObjectModel;
using TermLink.Pc.Contracts;
using TermLink.Platform;

namespace TermLink.Presentation;

public abstract class PageViewModel(string key, string title) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
}

public sealed class ConnectionPageViewModel : PageViewModel
{
    private string summary = "正在连接 Agent…";
    private string detail = string.Empty;
    private bool remoteAccessEnabled;

    public ConnectionPageViewModel() : base("connection", "连接服务器") { }

    public string Summary { get => summary; internal set => SetProperty(ref summary, value); }
    public string Detail { get => detail; internal set => SetProperty(ref detail, value); }
    public bool RemoteAccessEnabled { get => remoteAccessEnabled; internal set => SetProperty(ref remoteAccessEnabled, value); }
}

public sealed class SessionsPageViewModel : PageViewModel
{
    public SessionsPageViewModel() : base("sessions", "会话") { }
    public ObservableCollection<ManagedSessionSnapshot> Sessions { get; } = new();
}

public sealed class SettingsPageViewModel : PageViewModel
{
    private string platform = string.Empty;
    private string version = string.Empty;

    public SettingsPageViewModel() : base("settings", "设置") { }
    public string Platform { get => platform; internal set => SetProperty(ref platform, value); }
    public string Version { get => version; internal set => SetProperty(ref version, value); }
    public ObservableCollection<TerminalCommandCapability> TerminalCommands { get; } = new();
}

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AgentStateStore stateStore;
    private readonly IUiDispatcher dispatcher;
    private readonly Dictionary<string, PageViewModel> pages;
    private PageViewModel currentPage;
    private string agentName = "TermLink";
    private string statusText = "正在连接";

    public MainWindowViewModel(AgentStateStore stateStore, IUiDispatcher dispatcher, PlatformDescriptor descriptor)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(descriptor);
        Connection = new ConnectionPageViewModel();
        Sessions = new SessionsPageViewModel();
        Settings = new SettingsPageViewModel
        {
            Platform = $"{descriptor.Platform} · {descriptor.Architecture} · {descriptor.PlatformVersion}",
        };
        pages = new PageViewModel[] { Connection, Sessions, Settings }
            .ToDictionary(page => page.Key, StringComparer.Ordinal);
        currentPage = Sessions;
        NavigateCommand = new AsyncCommand((parameter, _) =>
        {
            Navigate(parameter as string);
            return Task.CompletedTask;
        });
        RefreshCommand = new AsyncCommand((_, cancellationToken) => stateStore.RefreshAsync(true, cancellationToken));
        stateStore.Changed += StateChanged;
    }

    public ConnectionPageViewModel Connection { get; }
    public SessionsPageViewModel Sessions { get; }
    public SettingsPageViewModel Settings { get; }
    public AsyncCommand NavigateCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public IReadOnlyCollection<PageViewModel> Pages => pages.Values;
    public PageViewModel CurrentPage { get => currentPage; private set => SetProperty(ref currentPage, value); }
    public string AgentName { get => agentName; private set => SetProperty(ref agentName, value); }
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }

    public void Start()
    {
        stateStore.Start();
    }

    public void Navigate(string? key)
    {
        if (key is not null && pages.TryGetValue(key, out var page)) CurrentPage = page;
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
            AgentConnectionState.Connected => "Relay 已连接",
            AgentConnectionState.Reconnecting => "正在重连",
            AgentConnectionState.ReauthenticationRequired => "需要重新登录",
            AgentConnectionState.Faulted => "Agent 异常",
            _ => snapshot.ConnectionState.ToString(),
        };
        Connection.Summary = StatusText;
        Connection.Detail = string.IsNullOrWhiteSpace(snapshot.LastErrorDetail)
            ? snapshot.ServerOrigin
            : $"{snapshot.LastErrorCode}: {snapshot.LastErrorDetail}";
        Connection.RemoteAccessEnabled = snapshot.RemoteAccessEnabled;
        Settings.Version = snapshot.Identity.AgentVersion;
        ObservableCollectionDiff.Apply(Sessions.Sessions, snapshot.Sessions, item => item.SessionId);
        ObservableCollectionDiff.Apply(Settings.TerminalCommands, snapshot.TerminalCommands, item => item.Id);
    }

    public async ValueTask DisposeAsync()
    {
        stateStore.Changed -= StateChanged;
        await stateStore.DisposeAsync().ConfigureAwait(false);
    }
}
