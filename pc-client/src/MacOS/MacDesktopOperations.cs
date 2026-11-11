using System.Runtime.InteropServices;
using TermLink.Platform;
using TermLink.Pc.Contracts;

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
    public bool SupportsAuthorizationDetection => true;

    public async ValueTask<IReadOnlyList<CapabilityCheck>> DetectAuthorizationsAsync(CancellationToken token)
    {
        var checks = (await new MacAuthorizationProbe(options.DataDirectory).DetectAsync(token).ConfigureAwait(false)).ToList();
        var plist = Path.Combine(options.LaunchAgentsDirectory, MacPlatformOptions.AgentServiceName + ".plist");
        checks.Add(new("mac_login_items", "macOS 集成", "登录项与后台运行", File.Exists(plist) ? CapabilityCheckStatus.Limited : CapabilityCheckStatus.Unknown,
            File.Exists(plist) ? "已存在本应用的 launchd 配置；是否被系统允许后台运行仍需在登录项中确认。" : "未发现本应用的自动启动配置。当前后台可随手动启动运行，系统登录项许可需单独确认。", "OpenLoginItems"));
        checks.Add(new("mac_updates", "更新", "应用内 Mac 包更新", SupportsAutomaticUpdates ? CapabilityCheckStatus.Available : CapabilityCheckStatus.NotIntegrated,
            SupportsAutomaticUpdates ? "此安装已配置可信更新渠道；下载、签名和公证验证后仍需 macOS 按次授权安装。" : "当前安装未启用可信自动更新渠道，DEV 使用新开发包手动更新。"));
        checks.Add(new("mac_notifications_feature", "macOS 集成", "后台 AI 状态提醒", CapabilityCheckStatus.NotIntegrated,
            "系统通知权限与产品通知功能独立。当前桌面尚未接入服务端后台事件上报。"));
        return checks;
    }

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
            case DesktopPlatformAction.OpenNotificationSettings:
                await OpenAsync(["x-apple.systempreferences:com.apple.Notifications-Settings.extension"], cancellationToken).ConfigureAwait(false);
                return "已打开 macOS 通知设置；调整后请重新检测。";
            case DesktopPlatformAction.OpenFilesPrivacySettings:
                await OpenAsync(["x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders"], cancellationToken).ConfigureAwait(false);
                return "已打开文件与文件夹隐私设置；只为实际需要的目录授权。";
            case DesktopPlatformAction.OpenKeychainAccess:
                await OpenAsync(["-b", "com.apple.keychainaccess"], cancellationToken).ConfigureAwait(false);
                return "已打开钥匙串访问；本次未读取凭据或修改权限。";
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
            $"Remote terminal/files: not integrated\nAI: ZCode native chain only; model authentication requires validation\n";
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
