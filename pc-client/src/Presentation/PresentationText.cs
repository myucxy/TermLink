using System.Globalization;

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
    public static string ThisDeviceDetail => Pick("管理本机 Agent、远程访问和已登录设备。", "Manage this Agent, remote access, and signed-in devices.");
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
        "登录、注册和退出均由独立 Agent 处理；凭据不会写入 UI 日志。",
        "The independent Agent handles sign-in, registration, and sign-out; credentials are never written to UI logs.");
    public static string PcName => Pick("PC 名称", "PC name");
    public static string Username => Pick("用户名", "Username");
    public static string RegistrationEmail => Pick("邮箱（注册）", "Email (registration)");
    public static string Password => Pick("密码", "Password");
    public static string ConfirmPassword => Pick("确认密码（注册）", "Confirm password (registration)");
    public static string Login => Pick("登录", "Sign in");
    public static string Register => Pick("注册", "Register");
    public static string SignOut => Pick("退出登录", "Sign out");
    public static string SignedIn => Pick("已登录", "Signed in");
    public static string AccountSignedOut => Pick("未登录", "Signed out");
    public static string SignInDetail => Pick("登录同一账户后，Android 可以发现并访问这台终端。", "Sign in with the same account so Android can discover this terminal.");
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
    public static string DesktopAndUpdates => Pick("桌面与更新", "Desktop and updates");
    public static string UpdateSafetyDetail => Pick(
        "更新由保留的已签名组件执行；关闭控制 UI 不会关闭 Agent。",
        "Updates run through the retained signed component; closing the control UI does not stop the Agent.");
    public static string RepairTerminal => Pick("修复 Terminal 集成", "Repair Terminal integration");
    public static string CheckUpdates => Pick("检查更新", "Check for updates");
    public static string Delete => Pick("删除", "Delete");
    public static string General => Pick("常规", "General");
    public static string Commands => Pick("命令", "Commands");
    public static string UpdateAndIntegration => Pick("更新与集成", "Updates and integration");
    public static string Appearance => Pick("外观", "Appearance");
    public static string AppearanceDetail => Pick("与 Android 使用同一套品牌颜色。", "Uses the same brand colors as Android.");
    public static string FollowSystem => Pick("跟随系统", "System");
    public static string LightTheme => Pick("浅色", "Light");
    public static string DarkTheme => Pick("深色", "Dark");
    public static string TerminalIntegrationDetail => Pick("验证并修复 TermLink 管理的 Terminal Profile。", "Verify and repair TermLink-managed Terminal profiles.");
    public static string DiagnosticsDetail => Pick("仅在排查连接或兼容问题时需要这些信息。", "Use these details when troubleshooting connectivity or compatibility.");
    public static string Revision => Pick("状态修订号", "State revision");
    public static string ObservedAt => Pick("采样时间", "Observed at");
    public static string Capabilities => Pick("能力", "Capabilities");
    public static string LastError => Pick("最后错误", "Last error");
    public static string AdvancedDiagnostics => Pick("高级诊断", "Advanced diagnostics");
    public static string OpenLogs => Pick("打开日志目录", "Open log directory");
    public static string RefreshStatus => Pick("刷新状态", "Refresh status");
    public static string OpenTermLink => Pick("打开 TermLink", "Open TermLink");
    public static string ExitControlUi => Pick("退出控制 UI（Agent 继续运行）", "Exit control UI (Agent keeps running)");
    public static string ConnectingAgent => Pick("正在连接 Agent…", "Connecting to Agent…");
    public static string Processing => Pick("正在处理…", "Working…");
    public static string OperationCompleted => Pick("操作已完成。", "Operation completed.");
    public static string OperationFailed => Pick("操作失败：", "Operation failed: ");
    public static string OperationFailedTitle => Pick("操作没有完成", "The operation did not complete");
    public static string InvalidRequestDetail => Pick("当前状态不允许执行该操作。请刷新状态后重试。", "The action is not valid in the current state. Refresh and try again.");
    public static string AccessDeniedDetail => Pick("当前账户或系统权限不足，无法完成该操作。", "The current account or system permission is insufficient.");
    public static string TimeoutDetail => Pick("操作等待超时，请检查 Agent 和服务器连接后重试。", "The operation timed out. Check the Agent and server connection, then retry.");
    public static string ConnectionFailedDetail => Pick("暂时无法连接 Agent 或服务器，请稍后重试。", "The Agent or server is temporarily unavailable.");
    public static string UnexpectedFailureDetail => Pick("发生未预期错误。可在诊断页面查看原始错误信息。", "An unexpected error occurred. Open Diagnostics for the original detail.");
    public static string RemoteAccessUpdated => Pick("远程访问设置已更新。", "Remote access was updated.");
    public static string RetryRequested => Pick("已请求立即重连。", "An immediate reconnect was requested.");
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
    public static string AutoDetected => Pick("自动检测：", "Auto-detected: ");
    public static string SessionStarting => Pick("正在建立", "Starting");
    public static string SessionConnected => Pick("运行中", "Running");
    public static string SessionDisconnected => Pick("已断开", "Disconnected");
    public static string SessionEnded => Pick("已结束", "Ended");
    public static string SessionInterrupted => Pick("已中断", "Interrupted");
    public static string SessionFaulted => Pick("异常", "Faulted");
    public static string Viewers(int count) => Pick($"{count} 个查看者", $"{count} viewers");
    public static string LastActive(string value) => Pick($"最后活动 {value}", $"Last active {value}");
    public static string RelayNotConfigured => Pick("尚未配置", "Not configured");
    public static string RelayConnecting => Pick("正在建立连接", "Connecting");
    public static string RelayConnected => Pick("Relay 已连接", "Relay connected");
    public static string RelayReconnecting => Pick("正在重新连接", "Reconnecting");
    public static string RelaySignInRequired => Pick("需要重新登录", "Sign-in required");
    public static string RelayOffline => Pick("当前离线", "Offline");
    public static string RelayFaulted => Pick("Agent 异常", "Agent fault");
    public static string ConfirmAction => Pick("确认操作", "Confirm action");
    public static string ConfirmStopRemoteTitle => Pick("停止远程访问？", "Stop remote access?");
    public static string ConfirmStopRemoteDetail => Pick("Android 将无法新建或控制会话，但本地终端不会关闭。", "Android will no longer create or control sessions. Local terminals stay open.");
    public static string ConfirmStopSessionTitle => Pick("停止此会话的远程访问？", "Stop remote access to this session?");
    public static string ConfirmStopSessionDetail(string title) => Pick($"“{title}”将从远程会话列表中关闭，本地 Shell 生命周期按 Agent 策略处理。", $"Remote access to “{title}” will stop. The Agent retains ownership of the local shell lifecycle.");
    public static string ConfirmRevokeDeviceTitle => Pick("撤销此设备？", "Revoke this device?");
    public static string ConfirmRevokeDeviceDetail(string name) => Pick($"“{name}”需要重新登录才能再次访问这台终端。", $"“{name}” must sign in again before accessing this terminal.");
    public static string ConfirmSignOutTitle => Pick("退出 TermLink 账户？", "Sign out of TermLink?");
    public static string ConfirmSignOutDetail => Pick("此 PC 将停止向同账户设备发布状态，现有本地终端不会关闭。", "This PC will stop publishing to account devices. Local terminals stay open.");
}
