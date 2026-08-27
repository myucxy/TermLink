using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TermLink.Agent;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Presentation;

namespace TermLink.Platform.Windows;

public sealed record WindowsAgentClientOptions(
    string ControlPipeName,
    string BufferRoot,
    string AgentExecutablePath,
    PlatformDescriptor Platform)
{
    public static WindowsAgentClientOptions CreateDefault(PlatformDescriptor platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new WindowsAgentClientOptions(
            TermLinkControlPipe.DefaultName(),
            Path.Combine(localData, "TermLink", "Agent"),
            Path.Combine(AppContext.BaseDirectory, "TermLink.Agent.exe"),
            platform);
    }
}

public sealed class WindowsAgentClient : IAgentClient, IAsyncDisposable
{
    private readonly WindowsAgentClientOptions options;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object revisionSync = new();
    private byte[]? lastFingerprint;
    private long revision;
    private int fullShutdownRequested;
    private bool disposed;

    public WindowsAgentClient(WindowsAgentClientOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ControlPipeName))
            throw new ArgumentException("The Agent control pipe is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.BufferRoot) || string.IsNullOrWhiteSpace(options.AgentExecutablePath))
            throw new ArgumentException("The Agent paths are required.", nameof(options));
    }

    public async ValueTask<AgentStatusSnapshot> GetStatusAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            await EnsureAgentAsync(cancellationToken).ConfigureAwait(false);
            if (forceRefresh)
            {
                var refresh = await SendAsync(
                    new RegistryRequest { Command = RegistryCommand.RefreshProductionStatus },
                    30_000,
                    cancellationToken).ConfigureAwait(false);
                if (!refresh.Success && refresh.Code != "PRODUCTION_NOT_CONFIGURED" &&
                    refresh.Code != "ACCOUNT_REAUTH_REQUIRED")
                {
                    throw new InvalidOperationException(refresh.Code ?? "PRODUCTION_REFRESH_FAILED");
                }
            }
            var response = await SendAsync(
                new RegistryRequest { Command = RegistryCommand.List },
                15_000,
                cancellationToken).ConfigureAwait(false);
            if (!response.Success) throw new InvalidOperationException(response.Code ?? "AGENT_STATUS_FAILED");
            return WithRevision(Map(response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return WithRevision(AgentStatusSnapshot.Disconnected(
                CreateIdentity(null),
                ClassifyError(error),
                error.Message));
        }
    }

    public async ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken)
    {
        var command = enabled ? RegistryCommand.EnableRemoteAccess : RegistryCommand.StopAllRemoteAccess;
        await SendRequiredAsync(new RegistryRequest { Command = command }, 20_000, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RetryConnectionAsync(CancellationToken cancellationToken) =>
        await SendRequiredAsync(
            new RegistryRequest { Command = RegistryCommand.RetryProductionConnection },
            20_000,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask StopSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session is required.", nameof(sessionId));
        await SendRequiredAsync(
            new RegistryRequest { Command = RegistryCommand.StopSession, SessionId = sessionId },
            20_000,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask GrantDeviceAsync(
        Guid deviceId,
        string permission,
        CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("A device is required.", nameof(deviceId));
        permission = ProductionDeviceGrantContract.NormalizePermission(permission);
        await SendRequiredAsync(new RegistryRequest
        {
            Command = RegistryCommand.GrantDevice,
            DeviceId = deviceId,
            GrantPermission = permission
        }, 30_000, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("A device is required.", nameof(deviceId));
        await SendRequiredAsync(new RegistryRequest
        {
            Command = RegistryCommand.RevokeDevice,
            DeviceId = deviceId
        }, 30_000, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask LoginAsync(
        string username,
        string password,
        string pcName,
        CancellationToken cancellationToken)
    {
        await SendRequiredAsync(new RegistryRequest
        {
            Command = RegistryCommand.LoginAccount,
            AccountUsername = username,
            AccountPassword = password,
            PcName = pcName
        }, 60_000, cancellationToken).ConfigureAwait(false);
        await RestartAfterMutationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RegisterAsync(
        string username,
        string email,
        string password,
        string passwordConfirmation,
        string pcName,
        CancellationToken cancellationToken)
    {
        await SendRequiredAsync(new RegistryRequest
        {
            Command = RegistryCommand.RegisterAccount,
            AccountUsername = username,
            AccountEmail = email,
            AccountPassword = password,
            AccountPasswordConfirmation = passwordConfirmation,
            PcName = pcName
        }, 60_000, cancellationToken).ConfigureAwait(false);
        await RestartAfterMutationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SignOutAsync(CancellationToken cancellationToken)
    {
        await SendRequiredAsync(
            new RegistryRequest { Command = RegistryCommand.SignOutAccount },
            30_000,
            cancellationToken).ConfigureAwait(false);
        await RestartAfterMutationAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the authenticated Agent and every Agent/SessionHost process owned by
    /// this installation. Once entered, status polling cannot restart the Agent.
    /// A failed shutdown restores normal client operation so the UI can report and
    /// retry the failure without silently losing its control surface.
    /// </summary>
    public async ValueTask ShutdownCompletelyAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref fullShutdownRequested, 1) != 0) return;

        var entered = false;
        try
        {
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            try
            {
                var response = await SendAsync(
                    new RegistryRequest { Command = RegistryCommand.Shutdown },
                    20_000,
                    cancellationToken).ConfigureAwait(false);
                if (!response.Success)
                    throw new InvalidOperationException(response.Code ?? "AGENT_SHUTDOWN_FAILED");
            }
            catch (Exception error) when (IsConnectionFailure(error))
            {
                // No authenticated Agent is serving this installation. The
                // path-scoped sweep below still closes any orphan SessionHost.
            }

            await WaitForPipeToStopAsync(DateTime.UtcNow.AddSeconds(20), cancellationToken)
                .ConfigureAwait(false);
            await WaitForOwnedProcessesAsync(DateTime.UtcNow.AddSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            KillOwnedProcesses();
            await WaitForOwnedProcessesAsync(DateTime.UtcNow.AddSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            if (HasOwnedProcesses())
                throw new InvalidOperationException("TermLink Agent or SessionHost processes are still running.");
        }
        catch
        {
            Volatile.Write(ref fullShutdownRequested, 0);
            throw;
        }
        finally
        {
            if (entered) lifecycle.Release();
        }
    }

    public async ValueTask SaveTerminalCommandsAsync(
        IReadOnlyList<TerminalCommandCapability> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var configured = commands
            .Select(command => new ProductionTerminalCommandSummary
            {
                Id = command.Id,
                DisplayName = command.DisplayName,
                Executable = command.Executable,
                Arguments = command.Arguments.Count == 0 ? string.Empty : command.Arguments[0],
                ShellType = command.Detected ? command.Id : "custom",
                Detected = command.Detected,
                Enabled = command.Enabled,
                RunAsAdministrator = command.RunAsAdministrator
            })
            .ToList();
        await SendRequiredAsync(new RegistryRequest
        {
            Command = RegistryCommand.SaveTerminalCommands,
            TerminalCommands = configured
        }, 30_000, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RegistryResponse> SendRequiredAsync(
        RegistryRequest request,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await EnsureAgentAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAsync(request, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        if (!response.Success) throw new InvalidOperationException(response.Code ?? "AGENT_COMMAND_FAILED");
        return response;
    }

    private async Task EnsureAgentAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref fullShutdownRequested) != 0)
            throw new InvalidOperationException("TermLink is exiting completely; the Agent will not be restarted.");
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref fullShutdownRequested) != 0)
                throw new InvalidOperationException("TermLink is exiting completely; the Agent will not be restarted.");
            if (await TryListAsync(750, cancellationToken).ConfigureAwait(false) is not null) return;
            var agentPath = Path.GetFullPath(options.AgentExecutablePath);
            if (!File.Exists(agentPath))
                throw new FileNotFoundException("The independent TermLink Agent executable is missing.", agentPath);
            Directory.CreateDirectory(Path.GetFullPath(options.BufferRoot));
            var start = new ProcessStartInfo
            {
                FileName = agentPath,
                WorkingDirectory = Path.GetDirectoryName(agentPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--registry");
            start.ArgumentList.Add("--control-pipe");
            start.ArgumentList.Add(options.ControlPipeName);
            start.ArgumentList.Add("--buffer-root");
            start.ArgumentList.Add(Path.GetFullPath(options.BufferRoot));
            using var process = Process.Start(start) ??
                throw new InvalidOperationException("The independent TermLink Agent did not start.");
            var deadline = DateTime.UtcNow.AddSeconds(65);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryListAsync(1000, cancellationToken).ConfigureAwait(false) is not null) return;
                if (process.HasExited)
                    throw new InvalidOperationException($"The independent TermLink Agent exited with code {process.ExitCode}.");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("The independent TermLink Agent did not become ready within 65 seconds.");
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task RestartAfterMutationAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryListAsync(250, cancellationToken).ConfigureAwait(false) is null) break;
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }
        await EnsureAgentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RegistryResponse?> TryListAsync(
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(
                new RegistryRequest { Command = RegistryCommand.List },
                timeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            return response.Success ? response : null;
        }
        catch (Exception error) when (IsConnectionFailure(error))
        {
            return null;
        }
    }

    private Task<RegistryResponse> SendAsync(
        RegistryRequest request,
        int timeoutMilliseconds,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => RegistryControlClient.Send(
                options.ControlPipeName,
                request,
                timeoutMilliseconds),
            cancellationToken);

    private async Task WaitForPipeToStopAsync(DateTime deadlineUtc, CancellationToken cancellationToken)
    {
        while (DateTime.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryListAsync(250, cancellationToken).ConfigureAwait(false) is null) return;
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("The independent TermLink Agent did not release its control pipe.");
    }

    private async Task WaitForOwnedProcessesAsync(DateTime deadlineUtc, CancellationToken cancellationToken)
    {
        while (DateTime.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = GetOwnedProcesses();
            try
            {
                if (processes.Count == 0) return;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private void KillOwnedProcesses()
    {
        foreach (var process in GetOwnedProcesses())
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and termination.
                }
                catch (Win32Exception)
                {
                    // The final ownership check reports a precise failure without
                    // broadening termination to unrelated TermLink installations.
                }
            }
        }
    }

    private List<Process> GetOwnedProcesses()
    {
        var result = new List<Process>();
        var installRoot = Path.GetDirectoryName(Path.GetFullPath(options.AgentExecutablePath));
        if (string.IsNullOrWhiteSpace(installRoot)) return result;
        var installPrefix = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        foreach (var name in new[] { "TermLink.Agent", "TermLink.SessionHost" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) &&
                        Path.GetFullPath(path).StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(process);
                        continue;
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
                process.Dispose();
            }
        }
        return result;
    }

    private bool HasOwnedProcesses()
    {
        var processes = GetOwnedProcesses();
        try
        {
            return processes.Count != 0;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private AgentStatusSnapshot Map(RegistryResponse response)
    {
        var production = response.Production ?? new ProductionStatus();
        var productionSessions = (production.Sessions ?? Array.Empty<ProductionSessionSummary>())
            .ToDictionary(session => session.SessionId);
        var sessions = (response.Summaries ?? Array.Empty<SessionSummary>())
            .Select(session =>
            {
                productionSessions.TryGetValue(session.SessionId, out var remote);
                return new ManagedSessionSnapshot(
                    session.SessionId,
                    $"{session.ShellType} · {session.SessionId.ToString("N")[..8]}",
                    session.ShellType.ToString(),
                    MapSessionState(session.Lifecycle),
                    remote?.ViewerCount ?? 0,
                    new DateTimeOffset(DateTime.SpecifyKind(session.LastActivityUtc, DateTimeKind.Utc)),
                    session.LastSequence);
            })
            .OrderByDescending(session => session.LastActivityAt)
            .ToArray();
        var devices = (production.Devices ?? Array.Empty<ProductionDeviceSummary>())
            .Where(device => device.DeviceId != Guid.Empty)
            .Select(device => new AccountDeviceSnapshot(
                device.DeviceId,
                device.Name ?? string.Empty,
                device.Type ?? string.Empty,
                device.TrustState ?? string.Empty,
                false))
            .ToArray();
        var commands = (production.TerminalCommands ?? Array.Empty<ProductionTerminalCommandSummary>())
            .Where(command => !string.IsNullOrWhiteSpace(command.Id))
            .Select(command => new TerminalCommandCapability(
                command.Id,
                command.DisplayName ?? command.Id,
                command.Executable ?? string.Empty,
                string.IsNullOrWhiteSpace(command.Arguments)
                    ? Array.Empty<string>()
                    : new[] { command.Arguments },
                command.Detected,
                command.RunAsAdministrator,
                command.Enabled))
            .Where(command => command.Enabled)
            .ToArray();
        var capabilities = new HashSet<string> { PcCapabilityIds.RealtimeTerminalV1 };
        if (production.TerminalSnapshotEnabled) capabilities.Add(PcCapabilityIds.TerminalSnapshotV1);
        if (commands.Length > 0) capabilities.Add(PcCapabilityIds.TerminalCommandsV1);
        if (production.Configured) capabilities.Add(PcCapabilityIds.FileOperationsV1);
        capabilities.Add(PcCapabilityIds.RemoteAgentUpdateV1);
        var accountName = string.Join(
            " · ",
            new[] { production.AccountUsername, production.AccountEmail }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new AgentStatusSnapshot(
            0,
            DateTimeOffset.UtcNow,
            CreateIdentity(production),
            MapConnectionState(production),
            production.AccountLinked,
            accountName,
            production.ServerOrigin ?? string.Empty,
            response.RemoteAccessEnabled,
            string.IsNullOrWhiteSpace(production.LastErrorCode) ? null : production.LastErrorCode,
            ConnectionDetail(production),
            capabilities,
            commands,
            sessions,
            devices);
    }

    private PcAgentIdentity CreateIdentity(ProductionStatus? production)
    {
        var pcId = production?.PcId ?? Guid.Empty;
        if (pcId == Guid.Empty)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(options.ControlPipeName));
            pcId = new Guid(hash.AsSpan(0, 16));
        }
        var version = production?.AgentVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            try { version = FileVersionInfo.GetVersionInfo(options.AgentExecutablePath).ProductVersion; }
            catch (FileNotFoundException) { }
        }
        if (string.IsNullOrWhiteSpace(version)) version = "unknown";
        return new PcAgentIdentity(
            pcId,
            string.IsNullOrWhiteSpace(production?.PcName) ? Environment.MachineName : production.PcName,
            options.Platform.Platform,
            options.Platform.Architecture,
            options.Platform.PlatformVersion,
            version);
    }

    private AgentStatusSnapshot WithRevision(AgentStatusSnapshot snapshot)
    {
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(FingerprintText(snapshot)));
        lock (revisionSync)
        {
            if (lastFingerprint is null || !CryptographicOperations.FixedTimeEquals(lastFingerprint, fingerprint))
            {
                if (revision == long.MaxValue) revision = 1;
                else revision++;
                lastFingerprint = fingerprint;
            }
            return snapshot with { Revision = revision };
        }
    }

    private static string FingerprintText(AgentStatusSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append(snapshot.Identity).Append('|').Append(snapshot.ConnectionState).Append('|')
            .Append(snapshot.AccountLinked).Append('|').Append(snapshot.AccountDisplayName).Append('|')
            .Append(snapshot.ServerOrigin).Append('|').Append(snapshot.RemoteAccessEnabled).Append('|')
            .Append(snapshot.LastErrorCode).Append('|').Append(snapshot.LastErrorDetail);
        foreach (var session in snapshot.Sessions) builder.Append('|').Append(session);
        foreach (var device in snapshot.Devices) builder.Append('|').Append(device);
        foreach (var command in snapshot.TerminalCommands) builder.Append('|').Append(command);
        return builder.ToString();
    }

    private static AgentConnectionState MapConnectionState(ProductionStatus status) => status.RelayState switch
    {
        ProductionRelayState.Connected => AgentConnectionState.Connected,
        ProductionRelayState.Connecting => AgentConnectionState.Connecting,
        ProductionRelayState.Reconnecting => AgentConnectionState.Reconnecting,
        ProductionRelayState.Failed when status.LastErrorCode == "ACCOUNT_REAUTH_REQUIRED" =>
            AgentConnectionState.ReauthenticationRequired,
        ProductionRelayState.Failed => AgentConnectionState.Faulted,
        ProductionRelayState.NotConfigured => AgentConnectionState.NotConfigured,
        _ => AgentConnectionState.Offline
    };

    private static ManagedSessionState MapSessionState(SessionAgentLifecycle state) => state switch
    {
        SessionAgentLifecycle.Starting => ManagedSessionState.Starting,
        SessionAgentLifecycle.Connected => ManagedSessionState.Connected,
        SessionAgentLifecycle.Disconnected => ManagedSessionState.Disconnected,
        SessionAgentLifecycle.Ended => ManagedSessionState.Ended,
        SessionAgentLifecycle.RemoteStopped or SessionAgentLifecycle.HostLost => ManagedSessionState.Interrupted,
        _ => ManagedSessionState.Faulted
    };

    private static string? ConnectionDetail(ProductionStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.LastErrorCode)) return null;
        return $"Relay state: {status.RelayState}";
    }

    private static bool IsConnectionFailure(Exception error) =>
        error is IOException or TimeoutException or Win32Exception;

    private static string ClassifyError(Exception error) => error switch
    {
        FileNotFoundException => "PC-AGENT-MISSING",
        UnauthorizedAccessException => "PC-AGENT-ACCESS-DENIED",
        CryptographicException => "PC-AGENT-CREDENTIAL-ERROR",
        InvalidDataException => "PC-AGENT-IPC-AUTHENTICATION",
        TimeoutException => "PC-AGENT-TIMEOUT",
        IOException => "PC-AGENT-IPC-UNAVAILABLE",
        _ => "PC-AGENT-UNAVAILABLE"
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public ValueTask DisposeAsync()
    {
        disposed = true;
        lifecycle.Dispose();
        if (lastFingerprint is not null)
        {
            CryptographicOperations.ZeroMemory(lastFingerprint);
            lastFingerprint = null;
        }
        return ValueTask.CompletedTask;
    }
}
