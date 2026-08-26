using System.Diagnostics;
using System.Runtime.InteropServices;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;

namespace TermLink.Platform.MacOS;

public sealed record MacPlatformOptions(
    string AgentExecutable,
    string IpcEndpoint,
    string DataDirectory,
    string LaunchAgentsDirectory,
    uint UserId)
{
    public static MacPlatformOptions CreateDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new MacPlatformOptions(
            Path.Combine(AppContext.BaseDirectory, "TermLink.Agent"),
            UnixDomainSocketTransport.DefaultEndpoint(),
            Path.Combine(home, "Library", "Application Support", "TermLink"),
            Path.Combine(home, "Library", "LaunchAgents"),
            GetEffectiveUserId());
    }

    [DllImport("libc", EntryPoint = "geteuid", ExactSpelling = true)]
    private static extern uint GetEffectiveUserId();
}

public sealed class MacPlatformServices : IPlatformServices, IAsyncDisposable
{
    private readonly PosixTerminalSessionHost terminalHost;

    public MacPlatformServices(MacPlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS platform services require macOS.");
        Descriptor = new PlatformDescriptor(
            PcPlatform.MacOS,
            RuntimeInformation.OSArchitecture == Architecture.Arm64 ? PcArchitecture.Arm64 : PcArchitecture.X64,
            RuntimeInformation.OSDescription,
            RuntimeInformation.RuntimeIdentifier,
            true);
        SecretStore = new MacKeychainSecretStore("dev.termlink.agent");
        LocalIpc = new UnixDomainSocketTransport();
        terminalHost = new PosixTerminalSessionHost();
        TerminalHost = terminalHost;
        FileSystem = new PosixFileSystemOperations();
        TerminalCommands = new PosixTerminalCommandDiscovery();
        var arguments = new[] { "--agent", "--ipc", options.IpcEndpoint };
        Autostart = new MacLaunchdAutostartManager(new MacLaunchdOptions(
            "dev.termlink.agent",
            options.AgentExecutable,
            arguments,
            options.LaunchAgentsDirectory,
            options.UserId));
        Updates = new MacPackageUpdateInstaller(Path.Combine(options.DataDirectory, "updates"));
        Processes = new MacProcessSupervisor(options.AgentExecutable, options.IpcEndpoint, Path.Combine(options.DataDirectory, "agent.pid"));
        Notifications = new MacNotification();
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

public sealed class MacProcessSupervisor(string agentExecutable, string ipcEndpoint, string pidPath) : IProcessSupervisor
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask EnsureAgentRunningAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsAgentRunningAsync(cancellationToken).ConfigureAwait(false)) return;
            if (!File.Exists(agentExecutable)) throw new FileNotFoundException("The macOS Agent executable is missing.", agentExecutable);
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
            var process = Process.Start(start) ?? throw new InvalidOperationException("The macOS Agent did not start.");
            Directory.CreateDirectory(Path.GetDirectoryName(pidPath)!);
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
            return ValueTask.FromResult(
                string.Equals(Path.GetFullPath(agentExecutable), process.MainModule?.FileName, StringComparison.Ordinal));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(false);
        }
    }
}

public sealed class MacNotification : IPlatformNotification
{
    public ValueTask ShowAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS notifications require macOS.");
        static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var script = $"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"";
        var start = new ProcessStartInfo { FileName = "/usr/bin/osascript", UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        _ = Process.Start(start);
        return ValueTask.CompletedTask;
    }
}
