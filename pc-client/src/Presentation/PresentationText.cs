using System.Globalization;
using TermLink.Pc.Contracts;
using TermLink.Platform;

namespace TermLink.Presentation;

/// <summary>
/// Startup-culture strings shared by the portable view model and Avalonia XAML.
/// Chinese and English are deliberately complete for the P4 Windows Preview;
/// additional cultures can replace this class with generated resources later.
/// </summary>
public static class PresentationText
{
    private static bool IsChinese =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static string Pick(string chinese, string english) => IsChinese ? chinese : english;

    public static string ControlCenter => Pick("你的终端", "Your terminal");
    public static string Console => Pick("TermLink 桌面端", "TermLink Desktop");
    public static string DeviceOverview => Pick("设备", "Device");
    public static string ThisDevice => Pick("这台终端", "This terminal");
    public static string ThisDeviceDetail => Pick("管理本机 Agent、远程访问和活动会话。", "Manage this Agent, remote access, and active sessions.");
    public static string Connection => Pick("连接服务器", "Connection");
    public static string Sessions => Pick("会话", "Sessions");
    public static string Devices => Pick("设备", "Devices");
    public static string Account => Pick("账户", "Account");
    public static string Settings => Pick("设置", "Settings");
    public static string Diagnostics => Pick("诊断", "Diagnostics");
    public static string Refresh => Pick("刷新", "Refresh");
    public static string Refreshing => Pick("正在刷新状态…", "Refreshing status…");
    public static string RemoteAccess => Pick("允许远程访问", "Allow remote access");
    public static string RemoteAccessDetail => Pick("停止远程访问不会关闭本地终端。", "Stopping remote access does not close local terminals.");
    public static string Enable => Pick("启用", "Enable");
    public static string Stop => Pick("停止", "Stop");
    public static string RetryNow => Pick("立即重连", "Retry now");
    public static string RemoteAccessOn => Pick("远程访问已开启", "Remote access on");
    public static string RemoteAccessOff => Pick("远程访问已关闭", "Remote access off");
    public static string ActiveSessions => Pick("活动会话", "Active sessions");
    public static string OnlineDevices => Pick("在线设备", "Online devices");
    public static string SignedInDevices => Pick("已登录设备", "Signed-in devices");
    public static string AgentVersion => Pick("Agent 版本", "Agent version");
    public static string NoSessions => Pick("还没有受管会话", "No managed sessions yet");
    public static string NoSessionsDetail => Pick("从 Android 端打开本机终端后，会话会安全地显示在这里。", "Sessions appear here after a terminal is opened from Android.");
    public static string NoDevices => Pick("没有其他已登录设备", "No other signed-in devices");
    public static string NoDevicesDetail => Pick("使用同一账户登录 Android 后，设备会显示在这里。", "Devices appear here after Android signs in with the same account.");
    public static string StopSession => Pick("停止远程访问", "Stop remote access");
    public static string GrantControl => Pick("授予控制", "Grant control");
    public static string Revoke => Pick("撤销", "Revoke");
    public static string DeviceOnline => Pick("在线", "Online");
    public static string DeviceOffline => Pick("离线", "Offline");
    public static string TrustedDevice => Pick("可信设备", "Trusted device");
    public static string AndroidDevice => Pick("Android 设备", "Android device");
    public static string WindowsDevice => Pick("Windows PC", "Windows PC");
    public static string LinuxDevice => Pick("Linux PC", "Linux PC");
    public static string MacDevice => Pick("Mac", "Mac");
    public static string UnknownDevice => Pick("设备", "Device");
    public static string AccountPrivacy => Pick(
        "登录和注册在服务端托管网页中完成；客户端不接收密码、邀请码或验证码。",
        "Sign-in and registration use a server-hosted page; the client never receives passwords, invitation codes, or verification codes.");
    public static string PcName => Pick("PC 名称", "PC name");
    public static string Username => Pick("用户名", "Username");
    public static string Nickname => Pick("昵称", "Nickname");
    public static string RegistrationEmail => Pick("邮箱（注册）", "Email (registration)");
    public static string Password => Pick("密码", "Password");
    public static string ConfirmPassword => Pick("确认密码（注册）", "Confirm password (registration)");
    public static string Login => Pick("登录", "Sign in");
    public static string Register => Pick("注册", "Register");
    public static string SignOut => Pick("退出登录", "Sign out");
    public static string SignedIn => Pick("已登录", "Signed in");
    public static string AccountSignedOut => Pick("未登录", "Signed out");
    public static string AuthenticationExpired => Pick("登录已失效", "Sign-in expired");
    public static string AuthenticationExpiredDetail => Pick(
        "账号凭据已过期或被撤销，请重新登录。重新登录后 Agent 会自动恢复连接。",
        "The account credential expired or was revoked. Sign in again and the Agent will reconnect automatically.");
    public static string SignInAgain => Pick("立即重新登录", "Sign in again now");
    public static string SignInDetail => Pick("登录同一账户后，Android 可以发现并访问这台终端。", "Sign in with the same account so Android can discover this terminal.");
    public static string ServerUrl => Pick("服务端地址", "Server URL");
    public static string ServerUrlDetail => Pick(
        "所有账户、会话和文件请求都会使用此 HTTPS 地址。",
        "All account, session, and file requests use this HTTPS address.");
    public static string ServerUrlHint => Pick("https://example.com:8910", "https://example.com:8910");
    public static string ServerUrlRequired => Pick("请输入服务端地址。", "Enter a server URL.");
    public static string ServerUrlUnchanged => Pick("服务端地址未更改。", "The server URL is unchanged.");
    public static string ServerUrlUpdated => Pick("服务端地址已更新，请重新登录账户。", "The server URL was updated. Sign in to the account again.");
    public static string ConfirmServerChangeTitle => Pick("切换服务端地址？", "Change server URL?");
    public static string ConfirmServerChangeDetail => Pick(
        "当前账户凭据和远程同步会被清除，本地会话保持运行；切换后需要重新登录。",
        "Current account credentials and remote synchronization will be cleared. Local sessions stay open; sign in again after switching.");
    public static string BrowserAccountAuthorization => Pick("使用网页登录或注册", "Sign in or register on the web");
    public static string BrowserAccountAuthorizationDetail => Pick(
        "TermLink 将打开系统浏览器并通过安全回调完成授权。注册策略、邀请码和邮箱验证码均由服务端页面处理。",
        "TermLink opens your system browser and completes authorization through a secure callback. Registration policy, invitation codes, and email verification stay on the server-hosted page.");
    public static string BrowserAuthorizationStarted => Pick("正在等待网页授权", "Waiting for web authorization");
    public static string BrowserAuthorizationStartedDetail => Pick(
        "正在连接身份服务；连接成功后浏览器将自动打开，请在 5 分钟内完成登录或注册。",
        "Connecting to the identity service. Your browser will open automatically; complete sign-in or registration within 5 minutes.");
    public static string BrowserAuthorizationSucceeded => Pick(
        "网页授权成功，Agent 正在重新建立连接。",
        "Web authorization succeeded; the Agent is reconnecting.");
    public static string BrowserAuthorizationInProgressDetail => Pick(
        "已有网页授权正在进行，TermLink 已重新打开授权页面，请在页面中继续。",
        "A web authorization is already in progress. TermLink reopened the authorization page so you can continue.");
    public static string BrowserCallbackListenerFailedDetail => Pick(
        "无法启动本机授权回调监听。请检查 Windows 网络权限后重试；本地会话不会受到影响。",
        "The local authorization callback listener could not be started. Check Windows network permissions and try again; local sessions are unaffected.");
    public static string BrowserOpenFailedDetail => Pick(
        "Windows 无法打开默认浏览器。请在系统设置中为 HTTP 和 HTTPS 选择默认浏览器后重试。",
        "Windows could not open the default browser. Choose a default browser for HTTP and HTTPS in Windows Settings, then try again.");
    public static string BrowserAuthorizationTimeoutDetail => Pick(
        "网页授权已超时。请重新点击登录，并在 5 分钟内完成登录或注册。",
        "Web authorization timed out. Start sign-in again and complete it within 5 minutes.");
    public static string BrowserAuthorizationRejectedDetail => Pick(
        "网页授权被取消或返回内容无效，请重新发起登录。",
        "Web authorization was cancelled or returned an invalid response. Start sign-in again.");
    public static string BrowserAuthorizationFailedDetail => Pick(
        "Agent 未能启动网页授权。请检查默认浏览器和 Windows 网络权限后重试；本地会话不会受到影响。",
        "The Agent could not start web authorization. Check the default browser and Windows network permissions, then try again; local sessions are unaffected.");
    public static string IdentityServiceTimeoutDetail => Pick(
        "身份服务响应超时。请检查服务器连接后重试。",
        "The identity service timed out. Check the server connection and try again.");
    public static string TokenExchangeFailedDetail => Pick(
        "网页已返回，但登录凭据交换没有完成。请重新登录；若问题持续，请检查身份服务配置。",
        "The browser returned, but the credential exchange did not complete. Sign in again; if it persists, check the identity service configuration.");
    public static string SignInInputRequired => Pick("请输入登录信息", "Enter sign-in details");
    public static string LoginMode => Pick("登录", "Sign in");
    public static string RegisterMode => Pick("创建账户", "Create account");
    public static string Platform => Pick("平台", "Platform");
    public static string AvailableCommands => Pick("可用命令", "Available commands");
    public static string CommandsDetail => Pick("这些名称会显示在 Android 文件页面的“打开终端”菜单中。", "These names appear in Android's Open terminal menu.");
    public static string CommandDisplayName => Pick("手机端显示名称", "Mobile display name");
    public static string CommandExecutable => Pick("可执行文件或绝对路径", "Executable or absolute path");
    public static string CommandArguments => Pick("参数组合", "Arguments");
    public static string Add => Pick("添加", "Add");
    public static string Save => Pick("保存", "Save");
    public static string AddCustomCommand => Pick("添加自定义命令", "Add custom command");
    public static string Edit => Pick("编辑", "Edit");
    public static string Cancel => Pick("取消", "Cancel");
    public static string DiscardChanges => Pick("放弃更改", "Discard changes");
    public static string UnsavedChanges => Pick("命令配置有尚未保存的更改", "Command settings have unsaved changes");
    public static string AutomaticCommand => Pick("自动检测", "Auto-detected");
    public static string CustomCommand => Pick("自定义", "Custom");
    public static string DesktopAndUpdates => Pick("桌面更新", "Desktop updates");
    public static string UpdateSafetyDetail => Pick(
        "更新由保留的已签名组件执行；关闭控制 UI 不会关闭 Agent。",
        "Updates run through the retained signed component; closing the control UI does not stop the Agent.");
    public static string RepairTerminal => Pick("修复 Terminal 集成", "Repair Terminal integration");
    public static string Configure => Pick("配置", "Configure");
    public static string CheckUpdates => Pick("检查更新", "Check for updates");
    public static string StartUpdate => Pick("开始更新", "Start update");
    public static string AccountLoading => Pick("正在读取账户…", "Loading account…");
    public static string Delete => Pick("删除", "Delete");
    public static string General => Pick("常规", "General");
    public static string Commands => Pick("命令", "Commands");
    public static string Integration => Pick("集成", "Integration");
    public static string About => Pick("关于", "About");
    public static string Author => Pick("作者", "Author");
    public static string ApplicationVersion => Pick("桌面端版本", "Desktop version");
    public static string AboutDetail => Pick("TermLink 桌面控制端和更新信息。", "TermLink desktop control UI and update information.");
    public static string AppShipReleaseNotes => Pick("AppShip 版本说明", "AppShip release notes");
    public static string UpdateNotChecked => Pick("正在等待 AppShip 版本信息。", "Waiting for AppShip version information.");
    public static string CheckingUpdates => Pick("正在检查 AppShip 更新…", "Checking AppShip for updates…");
    public static string Appearance => Pick("外观", "Appearance");
    public static string AppearanceDetail => Pick("与 Android 使用同一套品牌颜色。", "Uses the same brand colors as Android.");
    public static string FollowSystem => Pick("跟随系统", "System");
    public static string LightTheme => Pick("浅色", "Light");
    public static string DarkTheme => Pick("深色", "Dark");
    public static string TerminalIntegrationDetail => Pick(
        "配置 PowerShell 启动参数，并验证或修复 TermLink 管理的 Terminal Profile。",
        "Configure PowerShell startup flags and verify or repair TermLink-managed Terminal profiles.");
    public static string TerminalIntegrationOptions => Pick(
        "Windows Terminal 配置",
        "Windows Terminal configuration");
    public static string TerminalIntegrationOptionsDetail => Pick(
        "这些选项只修改 TermLink 自己的 PowerShell Profile。默认添加 -NoLogo，不添加 -NoProfile。",
        "These options only change TermLink's PowerShell profiles. By default, -NoLogo is added and -NoProfile is not.");
    public static string AddNoLogo => Pick("添加 -NoLogo", "Add -NoLogo");
    public static string AddNoLogoDetail => Pick(
        "隐藏 PowerShell 启动横幅；默认开启。",
        "Hide the PowerShell startup banner; enabled by default.");
    public static string AddNoProfile => Pick("添加 -NoProfile", "Add -NoProfile");
    public static string AddNoProfileDetail => Pick(
        "不加载 $PROFILE。开启后 Oh My Posh、别名、模块和用户启动脚本可能不会初始化。",
        "Do not load $PROFILE. Oh My Posh, aliases, modules, and user startup scripts may not initialize.");
    public static string PackageManagedTerminalIntegration => Pick(
        "当前 Profile 来自签名 MSIX 包，只能查看这些默认值；更改需随应用更新发布。",
        "These profiles come from the signed MSIX package. Defaults are read-only and changes require an app update.");
    public static string DiagnosticsDetail => Pick("仅在排查连接或兼容问题时需要这些信息。", "Use these details when troubleshooting connectivity or compatibility.");
    public static string Revision => Pick("状态修订号", "State revision");
    public static string ObservedAt => Pick("采样时间", "Observed at");
    public static string Capabilities => Pick("能力", "Capabilities");
    public static string LastError => Pick("最后错误", "Last error");
    public static string AdvancedDiagnostics => Pick("高级诊断", "Advanced diagnostics");
    public static string OpenLogs => Pick("打开日志目录", "Open log directory");
    public static string DiagnosticsDirectoryOpened => Pick("已打开诊断日志目录。", "The diagnostics log directory is open.");
    public static string ShellOpenFailedDetail => Pick(
        "Windows 无法打开请求的页面或目录。请检查默认应用和资源管理器后重试。",
        "Windows could not open the requested page or directory. Check default apps and File Explorer, then try again.");
    public static string RefreshStatus => Pick("刷新状态", "Refresh status");
    public static string OpenTermLink => Pick("打开 TermLink", "Open TermLink");
    public static string ExitControlUi => Pick("退出控制 UI（Agent 继续运行）", "Exit control UI (Agent keeps running)");
    public static string ExitTermLinkCompletely => Pick("完全退出 TermLink", "Exit TermLink completely");
    public static string ConnectingAgent => Pick("正在连接 Agent…", "Connecting to Agent…");
    public static string Processing => Pick("正在处理…", "Working…");
    public static string OperationCompleted => Pick("操作已完成。", "Operation completed.");
    public static string OperationFailed => Pick("操作失败：", "Operation failed: ");
    public static string OperationFailedTitle => Pick("操作没有完成", "The operation did not complete");
    public static string InvalidRequestDetail => Pick("当前状态不允许执行该操作。请刷新状态后重试。", "The action is not valid in the current state. Refresh and try again.");
    public static string AccessDeniedDetail => Pick("当前账户或系统权限不足，无法完成该操作。", "The current account or system permission is insufficient.");
    public static string TimeoutDetail => Pick("操作等待超时，请检查 Agent 和服务器连接后重试。", "The operation timed out. Check the Agent and server connection, then retry.");
    public static string ConnectionFailedDetail => Pick("暂时无法连接 Agent 或服务器，请稍后重试。", "The Agent or server is temporarily unavailable.");
    public static string MachineIdentityUnavailableDetail => Pick(
        "服务器版本不支持稳定机器标识。请先升级服务端，然后重试登录。",
        "The Server does not support stable machine identity. Upgrade the Server, then sign in again.");
    public static string UnexpectedFailureDetail => Pick("发生未预期错误。可在诊断页面查看原始错误信息。", "An unexpected error occurred. Open Diagnostics for the original detail.");
    public static string AgentMissingDetail => Pick(
        "未找到后台 Agent 程序，请重新安装或修复 TermLink。",
        "The background Agent is missing. Reinstall or repair TermLink.");
    public static string AgentStartFailedDetail => Pick(
        "后台 Agent 无法启动，请查看诊断日志后重试。",
        "The background Agent could not start. Check Diagnostics and try again.");
    public static string AgentStartingTimeoutDetail => Pick(
        "已有后台 Agent 正在启动，但未能及时就绪；TermLink 没有重复创建 Agent。请稍后重试。",
        "The existing background Agent is still starting and did not become ready in time. TermLink did not create a duplicate Agent. Try again shortly.");
    public static string AgentRestartTimeoutDetail => Pick(
        "旧的后台 Agent 尚未完成退出；为避免重复进程，TermLink 已取消本次重启。请稍后重试。",
        "The previous background Agent has not finished stopping. TermLink cancelled this restart to avoid a duplicate process. Try again shortly.");
    public static string AgentExitedDetail => Pick(
        "后台 Agent 启动后意外退出，请查看诊断日志后重试。",
        "The background Agent exited unexpectedly during startup. Check Diagnostics and try again.");
    public static string AgentExitingDetail => Pick(
        "TermLink 正在完全退出，当前操作已取消。",
        "TermLink is exiting completely, so the current action was cancelled.");
    public static string AgentProcessesStillRunningDetail => Pick(
        "部分 Agent 或命令行会话仍在运行，请稍后重试完全退出。",
        "Some Agent or command-line session processes are still running. Try the full exit again shortly.");
    public static string RemoteAccessUpdated => Pick("远程访问设置已更新。", "Remote access was updated.");
    public static string RetryRequested => Pick("已请求立即重连。", "An immediate reconnect was requested.");

    public static string UpdateStatus(DesktopUpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var version = UpdateVersion(status.VersionName, status.BuildNumber);
        return status.Decision switch
        {
            DesktopUpdateDecision.NoUpdate => Pick($"当前已是最新版本 · {version}", $"Up to date · {version}"),
            DesktopUpdateDecision.Optional => Pick($"发现可选更新 · {version}", $"Optional update available · {version}"),
            DesktopUpdateDecision.Required => Pick($"发现必要更新 · {version}", $"Required update available · {version}"),
            DesktopUpdateDecision.NotConfigured => Pick("AppShip 更新服务未配置。", "AppShip update service is not configured."),
            _ => Pick("AppShip 更新检查失败。", "AppShip update check failed."),
        };
    }

    public static string UpdateReleaseNotesTitle(string? versionName, long? buildNumber) =>
        Pick($"{UpdateVersion(versionName, buildNumber)} · 更新内容", $"{UpdateVersion(versionName, buildNumber)} · What's new");

    public static string AvailableUpdate(DesktopUpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var version = UpdateVersion(status.VersionName, status.BuildNumber);
        return Pick($"发现新版本 · {version}", $"New version available · {version}");
    }

    public static string UpdateCheckFailed(string detail) =>
        Pick($"AppShip 更新检查失败 · {detail}", $"AppShip update check failed · {detail}");

    private static string UpdateVersion(string? versionName, long? buildNumber)
    {
        var version = string.IsNullOrWhiteSpace(versionName) ? "-" : $"v{versionName.Trim()}";
        return buildNumber.HasValue
            ? Pick($"{version}（构建 {buildNumber.Value}）", $"{version} (build {buildNumber.Value})")
            : version;
    }
    public static string SessionStopped => Pick("会话的远程访问已停止。", "Remote access to the session was stopped.");
    public static string DeviceGranted => Pick("设备控制权限已更新。", "Device control permission was updated.");
    public static string DeviceRevoked => Pick("设备授权已撤销。", "Device authorization was revoked.");
    public static string LoginSucceeded => Pick("登录成功，Agent 正在重新建立连接。", "Signed in; the Agent is reconnecting.");
    public static string RegistrationSucceeded => Pick("注册成功，Agent 正在重新建立连接。", "Registered; the Agent is reconnecting.");
    public static string SignedOut => Pick("已退出此 PC 上的 TermLink 账户。", "Signed out of the TermLink account on this PC.");
    public static string CommandsSaved => Pick("命令配置已保存并同步。", "Command settings were saved and synchronized.");
    public static string CommandRequired => Pick("命令名称和可执行文件不能为空。", "Command name and executable are required.");
    public static string CommandAdded => Pick("已添加本地命令；保存后生效。", "Local command added; save to apply it.");
    public static string CommandUpdated => Pick("命令已更新；保存后生效。", "Command updated; save to apply it.");
    public static string CommandRemoved => Pick("命令已移除；保存后生效。", "Command removed; save to apply it.");
    public static string UnsupportedDesktopOperation => Pick("当前平台不支持此桌面操作。", "This desktop operation is unsupported on the current platform.");
    public static string CurrentAccount => Pick("当前账户：", "Current account: ");
    public static string SessionCount(int count) => Pick($"{count} 个会话", count == 1 ? "1 session" : $"{count} sessions");
    public static string DeviceHeaderSummary(string remoteAccessStatus, int activeSessionCount) => Pick(
        $"{remoteAccessStatus} · {activeSessionCount} 个活动会话",
        $"{remoteAccessStatus} · {activeSessionCount} active {(activeSessionCount == 1 ? "session" : "sessions")}");
    public static string AutoDetected => Pick("自动检测：", "Auto-detected: ");
    public static string SessionStarting => Pick("正在建立", "Starting");
    public static string SessionConnected => Pick("运行中", "Running");
    public static string SessionDisconnected => Pick("已断开", "Disconnected");
    public static string SessionEnded => Pick("已结束", "Ended");
    public static string SessionInterrupted => Pick("已中断", "Interrupted");
    public static string SessionFaulted => Pick("异常", "Faulted");
    public static string Viewers(int count) => Pick($"{count} 个查看者", $"{count} viewers");
    public static string LastActive(string value) => Pick($"最后活动 {value}", $"Last active {value}");
    public static string SessionCarrier(SessionDataPlaneCarrier carrier) =>
        SessionDataPlaneCarrierContract.Normalize(carrier) switch
        {
            SessionDataPlaneCarrier.Negotiating => Pick("协商中", "Negotiating"),
            SessionDataPlaneCarrier.LanDirect => Pick("局域网直连", "LAN direct"),
            SessionDataPlaneCarrier.NatDirect => Pick("NAT 直连", "NAT direct"),
            _ => Pick("服务器中转", "Relay"),
        };
    public static string RelayNotConfigured => Pick("服务器未连接", "Server disconnected");
    public static string RelayConnecting => Pick("正在连接服务器", "Connecting to server");
    public static string RelayConnected => Pick("服务器已连接", "Server connected");
    public static string RelayReconnecting => Pick("正在重连服务器", "Reconnecting to server");
    public static string RelaySignInRequired => Pick("需要重新登录", "Sign-in required");
    public static string RelayOffline => Pick("服务器已断开", "Server disconnected");
    public static string RelayFaulted => Pick("服务器连接失败", "Server connection failed");
    public static string ConfirmAction => Pick("确认操作", "Confirm action");
    public static string ConfirmStopRemoteTitle => Pick("停止远程访问？", "Stop remote access?");
    public static string ConfirmStopRemoteDetail => Pick("Android 将无法新建或控制会话，但本地终端不会关闭。", "Android will no longer create or control sessions. Local terminals stay open.");
    public static string ConfirmStopSessionTitle => Pick("停止此会话的远程访问？", "Stop remote access to this session?");
    public static string ConfirmStopSessionDetail(string title) => Pick($"“{title}”将从远程会话列表中关闭，本地 Shell 生命周期按 Agent 策略处理。", $"Remote access to “{title}” will stop. The Agent retains ownership of the local shell lifecycle.");
    public static string ConfirmRevokeDeviceTitle => Pick("撤销此设备？", "Revoke this device?");
    public static string ConfirmRevokeDeviceDetail(string name) => Pick($"“{name}”需要重新登录才能再次访问这台终端。", $"“{name}” must sign in again before accessing this terminal.");
    public static string ConfirmSignOutTitle => Pick("退出 TermLink 账户？", "Sign out of TermLink?");
    public static string ConfirmSignOutDetail => Pick(
        "此 PC 将停止向当前账户同步；本地终端保持运行，登录其他账户后会继续同步。",
        "This PC will stop syncing to the current account. Local terminals stay open and resume syncing after another account signs in.");
    public static string ConfirmFullExitTitle => Pick("完全退出 TermLink？", "Exit TermLink completely?");
    public static string ConfirmFullExitDetail => Pick(
        "这将停止后台 Agent、断开所有远程连接，并结束 TermLink 管理的命令行会话。未保存的会话工作可能丢失；本机登录信息会保留。",
        "This stops the background Agent, disconnects every remote connection, and ends command-line sessions managed by TermLink. Unsaved session work may be lost; your local sign-in is retained.");
}
