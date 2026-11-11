using System.Runtime.InteropServices;
using TermLink.Pc.Contracts;

namespace TermLink.Platform.MacOS;

public sealed record MacAuthorizationObservation(int Keychain, int Notifications, int Accessibility, int DataDirectory);

/// <summary>Checks this UI process only. Unknown states never grant a permission.</summary>
public sealed class MacAuthorizationProbe
{
    // A timed-out UNUserNotificationCenter callback may still be executing its
    // block. Keep the code mapped for the process lifetime, never dlclose it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IntPtr>> Libraries = new(StringComparer.Ordinal);
    private readonly string directory;
    private readonly Func<MacAuthorizationObservation> read;

    public MacAuthorizationProbe(string dataDirectory, Func<MacAuthorizationObservation>? read = null, string? libraryPath = null)
    {
        directory = Path.GetFullPath(dataDirectory);
        this.read = read ?? (() => ReadNative(directory, libraryPath ?? Path.Combine(AppContext.BaseDirectory, "libtermlink-authorization.dylib")));
    }

    public async ValueTask<IReadOnlyList<CapabilityCheck>> DetectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MacAuthorizationObservation observation;
        try { observation = await Task.Run(read, cancellationToken).WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException or TimeoutException)
        { observation = new(-1, -1, -1, -1); }
        cancellationToken.ThrowIfCancellationRequested();
        return Describe(observation);
    }

    public static IReadOnlyList<CapabilityCheck> Describe(MacAuthorizationObservation value) => new CapabilityCheck[]
    {
        new("mac_keychain", "macOS 授权", "钥匙串", value.Keychain == 1 ? CapabilityCheckStatus.Limited : value.Keychain == 0 ? CapabilityCheckStatus.NeedsAuthorization : CapabilityCheckStatus.Unknown,
            value.Keychain == 1 ? "默认钥匙串已解锁；这不代表所有 TermLink 项目 ACL 已授权，实际登录读写仍可能出现系统确认。" : value.Keychain == 0 ? "默认钥匙串已锁定，请在钥匙串访问中解锁。检测不会读取凭据或弹出授权请求。" : "无法确认钥匙串锁定状态；检测未读取任何钥匙串项目。", "OpenKeychainAccess"),
        new("mac_notifications", "macOS 授权", "通知权限", value.Notifications == 2 ? CapabilityCheckStatus.Available : value.Notifications is 3 or 4 ? CapabilityCheckStatus.Limited : value.Notifications is 0 or 1 ? CapabilityCheckStatus.NeedsAuthorization : CapabilityCheckStatus.Unknown,
            value.Notifications == 2 ? "当前控制界面的通知授权可用；不代表后台通知功能已接入或真机推送已配置。" : value.Notifications is 3 or 4 ? "当前通知授权为临时或静默范围，展示方式受系统限制；不代表完整通知授权或后台功能已接入。" : value.Notifications == 1 ? "macOS 已关闭当前控制界面的通知权限，可在系统设置中调整。" : value.Notifications == 0 ? "当前应用尚未获得通知授权；检测只读状态，不主动申请。" : "无法确认当前应用的通知权限；请从已签名应用包运行后重新检测。", "OpenNotificationSettings"),
        new("mac_file_access", "macOS 授权", "应用数据目录访问", value.DataDirectory == 1 ? CapabilityCheckStatus.Available : value.DataDirectory == 0 ? CapabilityCheckStatus.NeedsAuthorization : CapabilityCheckStatus.Unknown,
            value.DataDirectory == 1 ? "当前进程可访问自己的数据目录；不代表可读取桌面、文稿、外接盘或完整磁盘。" : "应用数据目录未创建或访问尚未确认；实际目录授权需按操作验证。", "OpenFilesPrivacySettings"),
        new("mac_full_disk", "macOS 授权", "文件与文件夹 / 完全磁盘访问", CapabilityCheckStatus.Unknown,
            "macOS 没有可用于可靠确认所有目录权限的统一检查。本次不访问受保护目录、不读取 TCC 数据库；仅在实际需要的目录操作时确认权限。", "OpenFilesPrivacySettings"),
        new("mac_accessibility", "macOS 授权", "辅助功能", CapabilityCheckStatus.NotRequired,
            value.Accessibility == 1 ? "当前控制界面已有辅助功能授权；TermLink Relay、CLI 探测与原生 AI 协议不依赖它。" : "TermLink Relay、CLI 探测与原生 AI 协议无需辅助功能授权，不需要为能力检测开启它。"),
        new("mac_automation", "macOS 授权", "自动化 / 屏幕录制", CapabilityCheckStatus.NotRequired,
            "当前协议链路不依赖模拟键鼠、读取其他应用窗口或屏幕录制；本次不发送 Apple Events，也不申请这些权限。"),
        new("mac_install_authorization", "macOS 授权", "安装更新的管理员授权", CapabilityCheckStatus.Unknown,
            "安装授权由 macOS 在执行已校验的安装包时按次决定，不能通过静态检测提前确认；当前检测不请求管理员权限。"),
    };

    private static MacAuthorizationObservation ReadNative(string directory, string libraryPath)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(libraryPath)) return new(-1, -1, -1, -1);
        var library = Libraries.GetOrAdd(Path.GetFullPath(libraryPath), path => new Lazy<IntPtr>(() => NativeLibrary.Load(path))).Value;
        var keychain = Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(library, "tl_macos_keychain_status"));
        var notifications = Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(library, "tl_macos_notification_status"));
        var accessibility = Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(library, "tl_macos_accessibility_status"));
        var access = Marshal.GetDelegateForFunctionPointer<PathQuery>(NativeLibrary.GetExport(library, "tl_macos_directory_access"));
        return new(keychain(), notifications(), accessibility(), access(directory));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Query();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PathQuery([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
}
