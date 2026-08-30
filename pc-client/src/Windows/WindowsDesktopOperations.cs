using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TermLink.WindowsIntegration;

namespace TermLink.Platform.Windows;

/// <summary>
/// Windows shell integration for the Avalonia control UI. The independent
/// Agent remains the owner of Relay and terminal state; these actions only
/// open a user-visible OS surface or rebuild TermLink's own Terminal fragment.
/// </summary>
public sealed class WindowsDesktopOperations : IDesktopOperations
{
    private const long MaximumUpdateResultBytes = 64 * 1024;
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

    public async ValueTask<DesktopUpdateStatus> CheckForUpdatesAsync(
        bool interactive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rollbackUi = Path.Combine(installationRoot, "TermLink.Legacy.exe");
        if (!File.Exists(rollbackUi))
        {
            throw new FileNotFoundException(
                "Preview 更新组件尚未安装；请使用完整的 TermLink Preview 安装包。",
                rollbackUi);
        }

        var resultDirectory = Path.Combine(localApplicationData, "TermLink", "UpdateChecks");
        Directory.CreateDirectory(resultDirectory);
        var resultPath = Path.Combine(resultDirectory, $"desktop-{Guid.NewGuid():N}.json");
        var start = new ProcessStartInfo
        {
            FileName = rollbackUi,
            UseShellExecute = false,
            WorkingDirectory = installationRoot,
        };
        start.ArgumentList.Add("--check-update");
        if (!interactive) start.ArgumentList.Add("--check-update-silent");
        start.ArgumentList.Add("--check-update-result-json");
        start.ArgumentList.Add(resultPath);
        try
        {
            using var process = Process.Start(start) ??
                throw new InvalidOperationException("无法启动更新检查组件。");
            if (interactive)
            {
                // The user-visible signed updater owns its lifecycle once it is
                // shown. Never translate an Avalonia command cancellation into
                // terminating a Setup process the user has already accepted.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryStopUpdateCheck(process);
                    throw;
                }
            }
            if (!File.Exists(resultPath))
            {
                throw new InvalidDataException(
                    $"更新检查组件未返回结构化结果（退出码 {process.ExitCode}）。");
            }
            var info = new FileInfo(resultPath);
            if (info.Length <= 0 || info.Length > MaximumUpdateResultBytes)
                throw new InvalidDataException("更新检查组件返回的结果大小无效。");
            var json = await File.ReadAllTextAsync(resultPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return ParseUpdateStatus(json);
        }
        finally
        {
            TryDeleteResult(resultPath);
        }
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

    public ValueTask<DesktopTerminalIntegrationSettings> GetTerminalIntegrationSettingsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packageManaged = WindowsTerminalIntegration.IsCurrentProcessPackaged();
        var fragmentPath = packageManaged
            ? Path.Combine(installationRoot, "Terminal", "Fragments", WindowsTerminalIntegration.FragmentFileName)
            : WindowsTerminalIntegration.DefaultFragmentPath;
        var settings = WindowsTerminalIntegration.ReadPowerShellSettings(fragmentPath);
        return ValueTask.FromResult(new DesktopTerminalIntegrationSettings(
            settings.AddNoLogo,
            settings.AddNoProfile,
            CanConfigure: !packageManaged));
    }

    public ValueTask<string> ConfigureTerminalIntegrationAsync(
        bool addNoLogo,
        bool addNoProfile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WindowsTerminalIntegration.IsCurrentProcessPackaged())
        {
            throw new NotSupportedException(
                "MSIX 安装的 Windows Terminal Profile 由签名包管理，当前版本不能在运行时修改参数。");
        }
        var result = WindowsTerminalIntegration.Reconfigure(
            installationRoot,
            WindowsTerminalIntegration.DefaultFragmentPath,
            addNoLogo,
            addNoProfile);
        WindowsTerminalIntegration.TryRefreshInstallMarker(installationRoot, result);
        return ValueTask.FromResult("Windows Terminal 启动参数已保存。");
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

    internal static DesktopUpdateStatus ParseUpdateStatus(string json)
    {
        UpdateResultDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<UpdateResultDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("更新检查组件返回了无效 JSON。", error);
        }
        if (document is null || document.SchemaVersion != 1 ||
            !Enum.TryParse<DesktopUpdateDecision>(document.Decision, true, out var decision))
        {
            throw new InvalidDataException("更新检查组件返回了不支持的结果格式。");
        }
        if (document.BuildNumber is < 0)
            throw new InvalidDataException("更新检查组件返回了无效构建号。");
        return new DesktopUpdateStatus(
            decision,
            NormalizeField(document.VersionName),
            document.BuildNumber,
            NormalizeField(document.ReleaseNotes),
            NormalizeField(document.Message));
    }

    private static string? NormalizeField(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryStopUpdateCheck(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void TryDeleteResult(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class UpdateResultDocument
    {
        public int SchemaVersion { get; set; }
        public string? Decision { get; set; }
        public string? VersionName { get; set; }
        public long? BuildNumber { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? Message { get; set; }
    }
}
