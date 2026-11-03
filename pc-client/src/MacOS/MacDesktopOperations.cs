using System.Runtime.InteropServices;
using TermLink.Platform;

namespace TermLink.Platform.MacOS;

/// <summary>Native macOS surfaces for the control UI; never starts a shell command string.</summary>
public sealed class MacDesktopOperations : IDesktopOperations
{
    private readonly MacPlatformOptions options;
    private readonly IMacCommandRunner runner;
    private readonly string applicationDirectory;
    private MacUpdateService? updates;

    public void ConfigureUpdates(MacUpdateService service) => updates = service ?? throw new ArgumentNullException(nameof(service));

    public MacDesktopOperations(MacPlatformOptions options, IMacCommandRunner? runner = null, string? applicationDirectory = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.runner = runner ?? new MacCommandRunner();
        this.applicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
    }

    public bool SupportsAutomaticUpdates => updates is not null;
    public bool SupportsWindowsTerminalIntegration => false;
    public bool SupportsMacIntegration => true;

    public ValueTask<DesktopUpdateStatus> CheckForUpdatesAsync(bool install,
        Func<DesktopUpdateStatus, CancellationToken, ValueTask<bool>>? confirm,
        IProgress<DesktopUpdateProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (updates is not null) return updates.CheckAsync(install, confirm, progress, cancellationToken);
        return ValueTask.FromResult(new DesktopUpdateStatus(DesktopUpdateDecision.NotConfigured,
            null, null, null, "macOS updates require a separately downloaded application package."));
    }

    public async ValueTask<string> PerformPlatformActionAsync(DesktopPlatformAction action, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case DesktopPlatformAction.OpenTerminal:
                await OpenAsync(["-b", "com.apple.Terminal"], cancellationToken).ConfigureAwait(false);
                return "已打开 macOS 终端。";
            case DesktopPlatformAction.OpenApplicationDirectory:
                var bundle = FindApplicationBundle(applicationDirectory);
                await OpenAsync(bundle is null ? ["--", applicationDirectory] : ["-R", "--", bundle], cancellationToken).ConfigureAwait(false);
                return "已在 Finder 中打开应用位置。";
            case DesktopPlatformAction.OpenLoginItems:
                await OpenAsync(["x-apple.systempreferences:com.apple.LoginItems-Settings.extension"], cancellationToken).ConfigureAwait(false);
                return "已打开系统登录项设置。";
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    public async ValueTask<string> OpenSessionDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("工作目录不可用。");
        await OpenAsync(["--", Path.GetFullPath(directory)], cancellationToken).ConfigureAwait(false);
        return "已在 Finder 中打开工作目录。";
    }

    public async ValueTask<string> OpenAgentHelpAsync(string agentId, CancellationToken cancellationToken)
    {
        var url = agentId switch
        {
            "codex" => "https://developers.openai.com/codex/cli/",
            "claude" => "https://code.claude.com/docs/en/setup",
            "opencode" => "https://opencode.ai/docs/",
            "grok" => "https://grok.com/",
            "zcode" => "https://zcode.z.ai/",
            _ => throw new ArgumentException("Unsupported Agent.", nameof(agentId)),
        };
        await OpenAsync([url], cancellationToken).ConfigureAwait(false);
        return "已打开官方说明。";
    }

    public async ValueTask<string> OpenDiagnosticsDirectoryAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(options.DataDirectory, "logs");
        Directory.CreateDirectory(directory);
        // A useful local context file, deliberately excluding credentials,
        // account names, environment variables and authorization callback URLs.
        var context = $"TermLink macOS diagnostics\nObserved: {DateTimeOffset.Now:O}\n" +
            $"OS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.OSArchitecture}\n" +
            $"Channel: {(MacPlatformOptions.IsDevelopmentBuild ? "DEV" : "Production")}\n" +
            $"Agent executable present: {File.Exists(options.AgentExecutable)}\n" +
            $"Remote terminal/files/AI: not connected in this build\n";
        await File.WriteAllTextAsync(Path.Combine(directory, "desktop-context.txt"), context, cancellationToken).ConfigureAwait(false);
        await OpenAsync(["--", directory], cancellationToken).ConfigureAwait(false);
        return "已打开本机诊断目录。";
    }

    public ValueTask<DesktopTerminalIntegrationSettings> GetTerminalIntegrationSettingsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DesktopTerminalIntegrationSettings(false, false, false));
    public ValueTask<string> ConfigureTerminalIntegrationAsync(bool addNoLogo, bool addNoProfile, CancellationToken cancellationToken) =>
        ValueTask.FromException<string>(new PlatformNotSupportedException());
    public ValueTask<string> RepairTerminalIntegrationAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<string>(new PlatformNotSupportedException());

    private async ValueTask OpenAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await runner.RunAsync("/usr/bin/open", arguments, deadline.Token).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException("PC_SHELL_OPEN_FAILED");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PC_SHELL_OPEN_FAILED");
        }
    }

    private static string? FindApplicationBundle(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
        return null;
    }
}
