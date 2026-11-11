using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TermLink.Platform;

namespace TermLink.Platform.MacOS;

/// <summary>AppShip's macOS PKG channel, with a locally pinned publisher and explicit installation approval.</summary>
public sealed class MacUpdateService
{
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(30) };
    private readonly MacUpdateOptions options;
    private readonly string directory;
    private readonly IUpdateInstaller installer;
    private readonly HttpClient http;
    private readonly Func<CancellationToken, ValueTask> verifyCurrent;
    private readonly Func<CancellationToken, ValueTask> prepareInstall;
    private readonly Func<ValueTask> recoverInstall;
    private readonly SemaphoreSlim gate = new(1, 1);

    public MacUpdateService(MacUpdateOptions options, string directory, IUpdateInstaller installer,
        Func<CancellationToken, ValueTask> verifyCurrent, Func<CancellationToken, ValueTask> prepareInstall,
        Func<ValueTask> recoverInstall, HttpClient? http = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.directory = Path.GetFullPath(directory);
        this.installer = installer;
        this.verifyCurrent = verifyCurrent;
        this.prepareInstall = prepareInstall;
        this.recoverInstall = recoverInstall;
        this.http = http ?? DefaultClient;
        Https(options.ServiceUri.AbsoluteUri);
        if (options.ServiceUri.Query.Length != 0 || options.ServiceUri.Fragment.Length != 0 ||
            !Regex.IsMatch(options.ApplicationSlug, "^[a-z0-9-]{1,100}$") || options.Channel is not ("stable" or "beta") ||
            options.Architecture is not ("arm64" or "x64") || options.CurrentBuild is < 1 or > 65535 ||
            MacUpdateTrust.TeamId(options.TrustedTeamId) != options.TrustedTeamId)
            throw new ArgumentException("Invalid Mac update configuration.", nameof(options));
    }

    public async ValueTask<DesktopUpdateStatus> CheckAsync(bool install,
        Func<DesktopUpdateStatus, CancellationToken, ValueTask<bool>>? confirm,
        IProgress<DesktopUpdateProgress>? progress, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await verifyCurrent(token).ConfigureAwait(false);
            var endpoint = new Uri(options.ServiceUri.AbsoluteUri.TrimEnd('/') +
                $"/api/v1/apps/{options.ApplicationSlug}/updates?platform=macos&arch={options.Architecture}&channel={options.Channel}&build={options.CurrentBuild}");
            (DesktopUpdateStatus Status, UpdatePackage? Package) candidate;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await GetAsync(endpoint, false, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new(DesktopUpdateDecision.NotConfigured, null, null, null, "尚未发布此 Mac 渠道的安装包。");
                response.EnsureSuccessStatusCode();
                candidate = Parse(await ReadManifestAsync(response, deadline.Token).ConfigureAwait(false));
            }
            if (candidate.Package is null || !install || confirm is null ||
                !await confirm(candidate.Status, token).ConfigureAwait(false)) return candidate.Status;
            var path = await DownloadAsync(candidate.Package, progress, token).ConfigureAwait(false);
            var ready = candidate.Status with { ReadyToInstall = true, Message = "已下载并验证 Mac 安装包，可稍后安装。", UsesSystemInstaller = true };
            if (!await confirm(ready, token).ConfigureAwait(false)) return ready;
            // Verify again after the approval dialog, before stopping any sessions.
            await installer.VerifyAndStageAsync(candidate.Package, path, token).ConfigureAwait(false);
            var prepared = false;
            try
            {
                progress?.Report(new(candidate.Package.Size, candidate.Package.Size, Installing: true));
                prepared = true;
                await prepareInstall(token).ConfigureAwait(false);
                await installer.LaunchAsync(candidate.Package, path, token).ConfigureAwait(false);
                return candidate.Status with { Message = "Mac 更新已安装，正在重新启动。", ApplicationRestartRequired = true };
            }
            catch
            {
                if (prepared) await recoverInstall().ConfigureAwait(false);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private (DesktopUpdateStatus Status, UpdatePackage? Package) Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        var root = document.RootElement;
        var status = String(root, "status");
        if (status is "no_update" or "current") return (new(DesktopUpdateDecision.NoUpdate, null, null, null, null), null);
        var decision = status switch { "optional" => DesktopUpdateDecision.Optional, "required" => DesktopUpdateDecision.Required,
            _ => throw new InvalidDataException("Unknown Mac update status.") };
        var latest = root.GetProperty("latest");
        var version = String(latest, "versionName");
        var build = latest.GetProperty("buildNumber").GetInt64();
        if (!Regex.IsMatch(version, "^[0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5}$") || build <= options.CurrentBuild || build > 65535)
            throw new InvalidDataException("The Mac update must have a newer build number.");
        var artifact = root.GetProperty("artifact");
        foreach (var (key, expected) in new[] { ("platform", "macos"), ("architecture", options.Architecture), ("fileKind", "pkg"), ("status", "ready") })
            if (String(artifact, key) != expected) throw new InvalidDataException("Incorrect Mac update artifact: " + key);
        if (artifact.TryGetProperty("downloadEnabled", out var enabled) && enabled.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Mac update downloads are disabled.");
        var size = artifact.GetProperty("sizeBytes").GetInt64();
        if (size <= 0 || size > MaximumPackageBytes || (artifact.TryGetProperty("expectedSizeBytes", out var expectedSize) && expectedSize.GetInt64() != size))
            throw new InvalidDataException("Invalid Mac update size.");
        var sha = String(artifact, "sha256");
        if (!Regex.IsMatch(sha, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Invalid Mac update SHA-256.");
        if (MacUpdateTrust.TeamId(String(artifact, "signerFingerprint")) != options.TrustedTeamId)
            throw new CryptographicException("The update publisher differs from the installed application's publisher.");
        if (!String(artifact, "originalFilename").EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mac automatic updates require PKG.");
        var uri = Https(String(artifact, "downloadUrl"));
        var notes = String(latest, "releaseNotes");
        if (notes.Length > 32 * 1024) throw new InvalidDataException("Update release notes are too long.");
        return (new(decision, version, build, notes, null), new(version, build, uri, size, sha, options.TrustedTeamId, "pkg"));
    }

    private async Task<string> DownloadAsync(UpdatePackage package, IProgress<DesktopUpdateProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, $"TermLink-{package.BuildNumber}-{package.Sha256.ToLowerInvariant()}.pkg");
        if (File.Exists(path))
        {
            try { await installer.VerifyAndStageAsync(package, path, token).ConfigureAwait(false); return path; }
            catch (Exception error) when (error is IOException or CryptographicException) { File.Delete(path); }
        }
        var temporary = Path.Combine(directory, ".download-" + Guid.NewGuid().ToString("N") + ".pkg");
        try
        {
            using var response = await GetAsync(package.DownloadUri, true, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != package.Size)
                throw new InvalidDataException("Mac package Content-Length differs from its manifest.");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long received = 0;
                int count;
                progress?.Report(new(0, package.Size));
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > package.Size) throw new InvalidDataException("Mac package exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    progress?.Report(new(received, package.Size));
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                if (received != package.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(package.Sha256)))
                    throw new CryptographicException("Mac update download failed its integrity check.");
            }
            progress?.Report(new(package.Size, package.Size, true));
            await installer.VerifyAndStageAsync(package, temporary, token).ConfigureAwait(false);
            File.Move(temporary, path);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, bool allowRedirects, CancellationToken token)
    {
        for (var count = 0; ; count++)
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            try
            {
                if (!allowRedirects || count >= 3 || response.Headers.Location is null) throw new HttpRequestException("Invalid Mac update redirect.");
                uri = Https(new Uri(uri, response.Headers.Location).AbsoluteUri);
            }
            finally { response.Dispose(); }
        }
    }

    private static async Task<string> ReadManifestAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int limit = 64 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Mac update response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Mac update response is too large.");
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static Uri Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 ? uri : throw new InvalidDataException("Mac updates require credential-free HTTPS URLs.");
}
