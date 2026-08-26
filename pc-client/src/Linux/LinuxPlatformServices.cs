using System.Diagnostics;
using System.Runtime.InteropServices;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;

namespace TermLink.Platform.Linux;

public sealed record LinuxPlatformOptions(
    string AgentExecutable,
    string IpcEndpoint,
    string DataDirectory,
    string ConfigurationDirectory,
    string AppImagePath,
    string UpdateKeyringPath)
{
    public static LinuxPlatformOptions CreateDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data)) data = Path.Combine(home, ".local", "share");
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config)) config = Path.Combine(home, ".config");
        var root = Path.Combine(data, "TermLink");
        return new LinuxPlatformOptions(
            Path.Combine(AppContext.BaseDirectory, "TermLink.Agent"),
            UnixDomainSocketTransport.DefaultEndpoint(),
            root,
            config,
            Environment.GetEnvironmentVariable("APPIMAGE") ?? Path.Combine(AppContext.BaseDirectory, "TermLink.AppImage"),
            Path.Combine(AppContext.BaseDirectory, "trust", "linux-update.gpg"));
    }
}

public sealed class LinuxPlatformServices : IPlatformServices, IAsyncDisposable
{
    private readonly PosixTerminalSessionHost terminalHost;

    public LinuxPlatformServices(LinuxPlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux platform services require Linux.");
        Descriptor = new PlatformDescriptor(
            PcPlatform.Linux,
            RuntimeInformation.OSArchitecture == Architecture.Arm64 ? PcArchitecture.Arm64 : PcArchitecture.X64,
            RuntimeInformation.OSDescription,
            RuntimeInformation.RuntimeIdentifier,
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE")));
        LocalIpc = new UnixDomainSocketTransport();
        terminalHost = new PosixTerminalSessionHost();
        TerminalHost = terminalHost;
        FileSystem = new PosixFileSystemOperations();
        TerminalCommands = new PosixTerminalCommandDiscovery();
        SecretStore = new LinuxSecretStore(new LinuxSecretStoreOptions("dev.termlink.agent", "kdewallet", "TermLink"));
        var arguments = new[] { "--agent", "--ipc", options.IpcEndpoint };
        Autostart = new LinuxAutostartManager(new LinuxAutostartOptions(
            options.AgentExecutable,
            arguments,
            options.ConfigurationDirectory));
        Updates = new LinuxAppImageUpdateInstaller(
            new LinuxAppImageUpdateOptions(options.AppImagePath, Path.Combine(options.DataDirectory, "updates"), arguments),
            new GpgAppImageSignatureVerifier(options.UpdateKeyringPath));
        Processes = new LinuxProcessSupervisor(options.AgentExecutable, options.IpcEndpoint, Path.Combine(options.DataDirectory, "agent.pid"));
        Notifications = new LinuxNotification();
    }

    public PlatformDescriptor Descriptor { get; }
    public ISecretStore SecretStore { get; }
    public ILocalIpcTransport LocalIpc { get; }
    public ITerminalSessionHost TerminalHost { get; }
    public IFileSystemOperations FileSystem { get; }
    public ITerminalCommandDiscovery TerminalCommands { get; }
    public IAutostartManager Autostart { get; }
    public IUpdateInstaller Updates { get; }
    public IProcessSupervisor Processes { get; }
    public IPlatformNotification Notifications { get; }

    public ValueTask DisposeAsync() => terminalHost.DisposeAsync();
}

public sealed class LinuxProcessSupervisor(string agentExecutable, string ipcEndpoint, string pidPath) : IProcessSupervisor
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask EnsureAgentRunningAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsAgentRunningAsync(cancellationToken).ConfigureAwait(false)) return;
            if (!File.Exists(agentExecutable)) throw new FileNotFoundException("The Linux Agent executable is missing.", agentExecutable);
            var start = new ProcessStartInfo
            {
                FileName = agentExecutable,
                WorkingDirectory = Path.GetDirectoryName(agentExecutable),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--agent");
            start.ArgumentList.Add("--ipc");
            start.ArgumentList.Add(ipcEndpoint);
            var process = Process.Start(start) ?? throw new InvalidOperationException("The Linux Agent did not start.");
            var directory = Path.GetDirectoryName(pidPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(pidPath + ".new", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(pidPath + ".new", pidPath, true);
        }
        finally { gate.Release(); }
    }

    public ValueTask<bool> IsAgentRunningAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(pidPath) || !int.TryParse(File.ReadAllText(pidPath), out var id) || id <= 0)
            return ValueTask.FromResult(false);
        try
        {
            using var process = Process.GetProcessById(id);
            if (process.HasExited) return ValueTask.FromResult(false);
            var procExecutable = new FileInfo($"/proc/{id}/exe").ResolveLinkTarget(true)?.FullName;
            return ValueTask.FromResult(string.Equals(
                Path.GetFullPath(agentExecutable), procExecutable, StringComparison.Ordinal));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(false);
        }
    }
}

public sealed class LinuxNotification : IPlatformNotification
{
    public ValueTask ShowAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = ExecutableLocator.Find("notify-send");
        if (executable is null) return ValueTask.CompletedTask;
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(title);
        start.ArgumentList.Add(message);
        _ = Process.Start(start);
        return ValueTask.CompletedTask;
    }
}
