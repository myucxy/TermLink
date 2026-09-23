using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLink.Presentation;
using TermLink.UpdateCore;
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
    private const long MaximumUpdateResponseBytes = 256 * 1024;
    private const long MaximumUpdateArtifactBytes = 2L * 1024 * 1024 * 1024;
    private const string PublicAppShipBaseUrl = "https://appship.kget.cc:8910";
    private static readonly HttpClient UpdateClient = new(
        new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
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
        bool install,
        Func<DesktopUpdateStatus, CancellationToken, ValueTask<bool>>? confirm,
        IProgress<DesktopUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var update = await FetchUpdateAsync(cancellationToken).ConfigureAwait(false);
        var status = update.Status;
        if (!install || update.DownloadUrl is null ||
            status.Decision is not (DesktopUpdateDecision.Optional or DesktopUpdateDecision.Required))
            return status;
        if (confirm is null || !await confirm(status, cancellationToken).ConfigureAwait(false))
            return status;

        var setup = await DownloadAndVerifyUpdateAsync(update, progress, cancellationToken).ConfigureAwait(false);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = setup,
            WorkingDirectory = Path.GetDirectoryName(setup)!,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("无法启动已验证的 TermLink 安装程序。");
        return status with { Message = "已打开已验证的安装程序。" };
    }

    internal static UpdateCandidate ParseAppShipUpdate(string json, long currentBuild)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("更新服务返回格式无效。");
        var rawStatus = String(root, "status")?.ToLowerInvariant();
        var latest = Object(root, "latest");
        var version = latest.HasValue ? String(latest.Value, "versionName") : null;
        var build = latest.HasValue ? Integer(latest.Value, "buildNumber") : null;
        var notes = latest.HasValue ? String(latest.Value, "releaseNotes") : null;
        if (rawStatus is "no_update" or "current")
        {
            return new UpdateCandidate(
                new DesktopUpdateStatus(DesktopUpdateDecision.NoUpdate, version, build, notes, "current"));
        }
        var decision = rawStatus switch
        {
            "optional" => DesktopUpdateDecision.Optional,
            "required" => DesktopUpdateDecision.Required,
            _ => throw new InvalidDataException("更新服务返回了未知状态。"),
        };
        if (!build.HasValue || build.Value <= currentBuild)
            throw new InvalidDataException("更新服务返回的 build 不是严格升级。");
        var artifact = Object(root, "artifact") ?? throw new InvalidDataException("更新服务未返回安装包。");
        RequireEqual(artifact, "platform", "windows");
        RequireEqual(artifact, "fileKind", "exe");
        RequireEqual(artifact, "architecture", "x64");
        RequireEqual(artifact, "status", "ready");
        if (artifact.TryGetProperty("downloadEnabled", out var enabled) && enabled.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("更新安装包不可下载。");
        var size = Integer(artifact, "sizeBytes");
        var expectedSize = Integer(artifact, "expectedSizeBytes");
        if (!size.HasValue || size <= 0 || size > MaximumUpdateArtifactBytes ||
            (expectedSize.HasValue && expectedSize != size))
            throw new InvalidDataException("更新安装包大小无效。");
        var sha256 = NormalizeHex(String(artifact, "sha256"));
        var signer = NormalizeHex(String(artifact, "signerFingerprint"));
        if (!IsHex(sha256, 64) || !IsHex(signer, 40))
            throw new InvalidDataException("更新安装包哈希或签名指纹无效。");
        var download = HttpsUri(String(artifact, "downloadUrl"));
        var name = String(artifact, "originalFilename");
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetExtension(name), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新安装包不是 Windows EXE。");
        return new UpdateCandidate(
            new DesktopUpdateStatus(decision, version, build, notes, null),
            download.AbsoluteUri, size.Value, sha256!, signer!);
    }

    private async Task<UpdateCandidate> FetchUpdateAsync(CancellationToken cancellationToken)
    {
        var endpoint = new Uri(HttpsUri(ConfiguredAppShipBaseUrl()),
            $"api/v1/apps/{Metadata("TermLinkUpdateApplicationSlug", "termlink-win64")}/updates" +
            $"?platform=windows&arch=x64&channel={Metadata("TermLinkUpdateChannel", "stable")}" +
            $"&build={CurrentBuild()}");
        using var response = await SendWithRedirectsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"更新服务返回 HTTP {(int)response.StatusCode}。");
        var json = Encoding.UTF8.GetString(await ReadBoundedAsync(
            response, MaximumUpdateResponseBytes, cancellationToken).ConfigureAwait(false));
        return ParseAppShipUpdate(json, CurrentBuild());
    }

    private async Task<string> DownloadAndVerifyUpdateAsync(
        UpdateCandidate update,
        IProgress<DesktopUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(localApplicationData, "TermLink", "Updates");
        Directory.CreateDirectory(root);
        var finalPath = Path.Combine(root,
            $"TermLink-Setup-{update.Status.VersionName}-{update.Status.BuildNumber}-x64.exe");
        var temporaryPath = Path.Combine(root, $".download-{Guid.NewGuid():N}.tmp");
        try
        {
            progress?.Report(new DesktopUpdateProgress(0, update.SizeBytes));
            using var response = await SendWithRedirectsAsync(HttpsUri(update.DownloadUrl), cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength.HasValue &&
                response.Content.Headers.ContentLength.Value != update.SizeBytes)
                throw new InvalidDataException("下载长度与 AppShip 元数据不一致。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long received = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                received = checked(received + count);
                if (received > update.SizeBytes) throw new InvalidDataException("下载超过 AppShip 声明大小。");
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                progress?.Report(new DesktopUpdateProgress(received, update.SizeBytes));
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (received != update.SizeBytes || !CryptographicOperations.FixedTimeEquals(
                    hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                throw new CryptographicException("下载内容与 AppShip SHA-256 不一致。");
            var expectedSigner = WindowsUpdateSecurity.ResolveExpectedSignerThumbprint(installationRoot);
            WindowsUpdateSecurity.ValidateAuthenticode(
                temporaryPath, expectedSigner, update.SignerSha1, installationRoot);
            File.Move(temporaryPath, finalPath, true);
            return finalPath;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<HttpResponseMessage> SendWithRedirectsAsync(Uri initial, CancellationToken token)
    {
        var current = initial;
        for (var redirects = 0; ; redirects++)
        {
            var response = await UpdateClient.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            try
            {
                if (redirects >= 3 || response.Headers.Location is null)
                    throw new HttpRequestException("更新请求重定向无效或过多。");
                current = HttpsUri(response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location.AbsoluteUri
                    : new Uri(current, response.Headers.Location).AbsoluteUri);
            }
            finally { response.Dispose(); }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response, long maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum)
            throw new InvalidDataException("更新服务响应过大。");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("更新服务响应过大。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static long CurrentBuild()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version?.Revision >= 0 ? version.Revision : version?.Build ?? 0;
    }

    private static string ConfiguredAppShipBaseUrl() =>
        (Environment.GetEnvironmentVariable("TERMLINK_APPSHIP_BASE_URL") ?? PublicAppShipBaseUrl).TrimEnd('/') + "/";

    private static string Metadata(string key, string fallback) =>
        typeof(WindowsDesktopOperations).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => string.Equals(value.Key, key, StringComparison.Ordinal))?.Value ?? fallback;

    private static JsonElement? Object(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
    private static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Integer(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
    private static void RequireEqual(JsonElement parent, string name, string expected)
    {
        if (!string.Equals(String(parent, name), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"更新安装包字段 {name} 无效。");
    }
    private static Uri HttpsUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("更新地址必须是无内嵌凭据的 HTTPS URL。");
        return uri;
    }
    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is ':' or '-') continue;
            normalized.Append(char.ToLowerInvariant(character));
        }
        return normalized.ToString();
    }
    private static bool IsHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);

    internal sealed record UpdateCandidate(
        DesktopUpdateStatus Status,
        string? DownloadUrl = null,
        long SizeBytes = 0,
        string Sha256 = "",
        string SignerSha1 = "");

    public ValueTask<string> OpenDiagnosticsDirectoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(localApplicationData, "TermLink", "logs");
        Directory.CreateDirectory(directory);
        OpenShellTarget(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
        return ValueTask.FromResult(PresentationText.DiagnosticsDirectoryOpened);
    }

    internal static void OpenShellTarget(
        ProcessStartInfo startInfo,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        try
        {
            // ShellExecute commonly returns no process handle after handing a
            // directory or URL to an existing Explorer/browser instance. That
            // is a successful dispatch and must not be reported as a failure.
            using var process = (startProcess ?? Process.Start)(startInfo);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new AgentClientOperationException("PC_SHELL_OPEN_FAILED", error.Message, innerException: error);
        }
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
