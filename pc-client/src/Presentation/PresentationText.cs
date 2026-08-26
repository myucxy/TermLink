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

    public static string ControlCenter => Pick("PC 控制中心", "PC Control Center");
    public static string Console => Pick("PC 控制台", "PC Console");
    public static string Connection => Pick("连接服务器", "Connection");
    public static string Sessions => Pick("会话", "Sessions");
    public static string Devices => Pick("设备", "Devices");
    public static string Account => Pick("账户", "Account");
    public static string Settings => Pick("设置", "Settings");
    public static string Diagnostics => Pick("诊断", "Diagnostics");
    public static string Refresh => Pick("刷新", "Refresh");
    public static string RemoteAccess => Pick("允许远程访问", "Allow remote access");
    public static string RemoteAccessDetail => Pick("停止远程访问不会关闭本地终端。", "Stopping remote access does not close local terminals.");
    public static string Enable => Pick("启用", "Enable");
    public static string Stop => Pick("停止", "Stop");
    public static string RetryNow => Pick("立即重连", "Retry now");
    public static string StopSession => Pick("停止远程访问", "Stop remote access");
    public static string GrantControl => Pick("授予控制", "Grant control");
    public static string Revoke => Pick("撤销", "Revoke");
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
    public static string Platform => Pick("平台", "Platform");
    public static string AvailableCommands => Pick("可用命令", "Available commands");
    public static string CommandDisplayName => Pick("手机端显示名称", "Mobile display name");
    public static string CommandExecutable => Pick("可执行文件或绝对路径", "Executable or absolute path");
    public static string CommandArguments => Pick("参数组合", "Arguments");
    public static string Add => Pick("添加", "Add");
    public static string Save => Pick("保存", "Save");
    public static string DesktopAndUpdates => Pick("桌面与更新", "Desktop and updates");
    public static string UpdateSafetyDetail => Pick(
        "更新由保留的已签名组件执行；关闭控制 UI 不会关闭 Agent。",
        "Updates run through the retained signed component; closing the control UI does not stop the Agent.");
    public static string RepairTerminal => Pick("修复 Terminal 集成", "Repair Terminal integration");
    public static string CheckUpdates => Pick("检查更新", "Check for updates");
    public static string Delete => Pick("删除", "Delete");
    public static string Revision => Pick("状态修订号", "State revision");
    public static string ObservedAt => Pick("采样时间", "Observed at");
    public static string Capabilities => Pick("能力", "Capabilities");
    public static string LastError => Pick("最后错误", "Last error");
    public static string OpenLogs => Pick("打开日志目录", "Open log directory");
    public static string RefreshStatus => Pick("刷新状态", "Refresh status");
    public static string OpenTermLink => Pick("打开 TermLink", "Open TermLink");
    public static string ExitControlUi => Pick("退出控制 UI（Agent 继续运行）", "Exit control UI (Agent keeps running)");
    public static string ConnectingAgent => Pick("正在连接 Agent…", "Connecting to Agent…");
    public static string Processing => Pick("正在处理…", "Working…");
    public static string OperationCompleted => Pick("操作已完成。", "Operation completed.");
    public static string OperationFailed => Pick("操作失败：", "Operation failed: ");
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
    public static string UnsupportedDesktopOperation => Pick("当前平台不支持此桌面操作。", "This desktop operation is unsupported on the current platform.");
    public static string CurrentAccount => Pick("当前账户：", "Current account: ");
    public static string SessionCountSuffix => Pick(" 个会话", " sessions");
    public static string AutoDetected => Pick("自动检测：", "Auto-detected: ");
}
