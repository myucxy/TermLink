using System.Diagnostics;
using TermLink.WindowsIntegration;

namespace TermLink.Platform.Windows;

/// <summary>
/// Windows shell integration for the Avalonia control UI. The independent
/// Agent remains the owner of Relay and terminal state; these actions only
/// open a user-visible OS surface or rebuild TermLink's own Terminal fragment.
/// </summary>
public sealed class WindowsDesktopOperations : IDesktopOperations
{
    private readonly string installationRoot;
    private readonly string localApplicationData;

    public WindowsDesktopOperations(string installationRoot, string? localApplicationData = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows desktop operations require Windows.");
        this.installationRoot = NormalizeDirectory(installationRoot, nameof(installationRoot));
        this.localApplicationData = NormalizeDirectory(
            localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            nameof(localApplicationData));
    }

    public static WindowsDesktopOperations CreateDefault() => new(AppContext.BaseDirectory);

    public ValueTask<string> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rollbackUi = Path.Combine(installationRoot, "TermLink.Legacy.exe");
        if (!File.Exists(rollbackUi))
        {
            throw new FileNotFoundException(
                "Preview 更新组件尚未安装；请使用完整的 TermLink Preview 安装包。",
                rollbackUi);
        }

        var start = new ProcessStartInfo
        {
            FileName = rollbackUi,
            UseShellExecute = false,
            WorkingDirectory = installationRoot,
        };
        start.ArgumentList.Add("--check-update");
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("无法启动更新检查组件。");
        return ValueTask.FromResult("已打开安全更新检查；安装前仍会验证签名和 SHA-256。");
    }

    public ValueTask<string> OpenDiagnosticsDirectoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(localApplicationData, "TermLink", "logs");
        Directory.CreateDirectory(directory);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("无法打开诊断日志目录。");
        return ValueTask.FromResult("已打开诊断日志目录。");
    }

    public ValueTask<string> RepairTerminalIntegrationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = WindowsTerminalIntegration.Reconfigure(installationRoot);
        var markerUpdated = WindowsTerminalIntegration.TryRefreshInstallMarker(installationRoot, result);
        var ownership = markerUpdated || result.PackageManaged
            ? string.Empty
            : " 安装标记不可用，但配置文件已安全重建。";
        return ValueTask.FromResult(
            $"已验证或重建 {result.ProfileCount} 个 Windows Terminal Profile。{ownership}");
    }

    private static string NormalizeDirectory(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Directory is required.", parameterName);
        return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
    }
}
