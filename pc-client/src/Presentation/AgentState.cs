using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TermLink.Pc.Contracts;

namespace TermLink.Presentation;

public interface IAgentClient
{
    bool SupportsBrowserAuthorizationControls => false;
    ValueTask ControlBrowserAuthorizationAsync(bool cancel, CancellationToken cancellationToken) =>
        ValueTask.FromException(new PlatformNotSupportedException("Browser authorization controls are unavailable."));
    bool SupportsZcodeRemoteConfiguration => false;
    ValueTask ConfigureZcodeRemoteAsync(string url, CancellationToken cancellationToken) =>
        ValueTask.FromException(new PlatformNotSupportedException("ZCode remote configuration is unavailable on this platform."));
    ValueTask<IReadOnlyList<AiAgentCapability>> DetectAiAgentsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AiAgentCapability>>(Array.Empty<AiAgentCapability>());
    ValueTask<AgentStatusSnapshot> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken);
    ValueTask ConfigureServerAsync(string serverUrl, CancellationToken cancellationToken) =>
        ValueTask.FromException(new PlatformNotSupportedException("Server URL configuration is unavailable on this platform."));
    ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken);
    ValueTask RetryConnectionAsync(CancellationToken cancellationToken);
    ValueTask StopSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    ValueTask GrantDeviceAsync(Guid deviceId, string permission, CancellationToken cancellationToken);
    ValueTask RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken);
    ValueTask BeginAccountAuthorizationAsync(string pcName, CancellationToken cancellationToken);
    ValueTask LoginAsync(string username, string password, string pcName, CancellationToken cancellationToken);
    ValueTask RegisterAsync(
        string username,
        string nickname,
        string email,
        string password,
        string passwordConfirmation,
        string pcName,
        CancellationToken cancellationToken);
    ValueTask SignOutAsync(CancellationToken cancellationToken);
    ValueTask SaveTerminalCommandsAsync(
        IReadOnlyList<TerminalCommandCapability> commands,
        CancellationToken cancellationToken);
}

public sealed class AgentClientOperationException : InvalidOperationException
{
    public AgentClientOperationException(
        string code,
        string? diagnostic = null,
        int? processExitCode = null,
        Exception? innerException = null)
        : base(string.IsNullOrWhiteSpace(code) ? "PC_AGENT_OPERATION_FAILED" : code.Trim(), innerException)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "PC_AGENT_OPERATION_FAILED" : code.Trim();
        Diagnostic = string.IsNullOrWhiteSpace(diagnostic) ? null : diagnostic.Trim();
        ProcessExitCode = processExitCode;
    }

    public string Code { get; }
    public string? Diagnostic { get; }
    public int? ProcessExitCode { get; }
}

public interface IUiDispatcher
{
    bool CheckAccess();
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return ValueTask.CompletedTask;
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncCommand : ObservableObject, ICommand
{
    private readonly Func<object?, CancellationToken, Task> execute;
    private readonly Predicate<object?>? canExecute;
    private CancellationTokenSource? active;
    private bool isRunning;

    public AsyncCommand(Func<object?, CancellationToken, Task> execute, Predicate<object?>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (!SetProperty(ref isRunning, value)) return;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !IsRunning && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        active?.Dispose();
        active = new CancellationTokenSource();
        IsRunning = true;
        try
        {
            await execute(parameter, active.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested)
        {
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Cancel() => active?.Cancel();
}

public sealed class AgentStateStore : IAsyncDisposable
{
    private readonly IAgentClient client;
    private readonly TimeSpan interval;
    private readonly object synchronization = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? activeRefresh;
    private Task? polling;
    private AgentStatusSnapshot? current;
    private bool disposed;

    public AgentStateStore(IAgentClient client, TimeSpan? interval = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.interval = interval ?? TimeSpan.FromSeconds(3);
        if (this.interval < TimeSpan.FromMilliseconds(250) || this.interval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public AgentStatusSnapshot? Current
    {
        get { lock (synchronization) return current; }
    }

    public event EventHandler<AgentStatusSnapshot>? Changed;

    public Task RefreshAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        lock (synchronization)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (activeRefresh is { IsCompleted: false }) return activeRefresh.WaitAsync(cancellationToken);
            activeRefresh = RefreshCoreAsync(forceRefresh, lifetime.Token);
            return activeRefresh.WaitAsync(cancellationToken);
        }
    }

    public void Start()
    {
        lock (synchronization)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            polling ??= PollAsync(lifetime.Token);
        }
    }

    private async Task RefreshCoreAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var next = await client.GetStatusAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        PcContractValidator.Validate(next);
        var publish = false;
        lock (synchronization)
        {
            if (current is null || current.Revision != next.Revision)
            {
                current = next;
                publish = true;
            }
        }
        if (publish) Changed?.Invoke(this, next);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            await RefreshAsync(false, cancellationToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await RefreshAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? wait;
        lock (synchronization)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            wait = polling;
        }
        if (wait is not null)
        {
            try { await wait.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lifetime.Dispose();
    }
}

public static class ObservableCollectionDiff
{
    public static void Apply<T, TKey>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, TKey> keySelector)
        where TKey : notnull
    {
        var expectedKeys = new HashSet<TKey>(source.Select(keySelector));
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!expectedKeys.Contains(keySelector(target[index]))) target.RemoveAt(index);
        }

        for (var sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
        {
            var value = source[sourceIndex];
            var key = keySelector(value);
            var existingIndex = -1;
            for (var index = sourceIndex; index < target.Count; index++)
            {
                if (EqualityComparer<TKey>.Default.Equals(keySelector(target[index]), key))
                {
                    existingIndex = index;
                    break;
                }
            }
            if (existingIndex < 0) target.Insert(sourceIndex, value);
            else
            {
                if (existingIndex != sourceIndex) target.Move(existingIndex, sourceIndex);
                if (!EqualityComparer<T>.Default.Equals(target[sourceIndex], value)) target[sourceIndex] = value;
            }
        }
    }
}
