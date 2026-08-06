using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Tray
{
    internal enum WindowsUpdateDecision
    {
        NotConfigured,
        NoUpdate,
        Optional,
        Required,
        Error
    }

    internal sealed class WindowsUpdateResult
    {
        public WindowsUpdateDecision Decision { get; set; }
        public string VersionName { get; set; }
        public long? BuildNumber { get; set; }
        public string ReleaseNotes { get; set; }
        public string DownloadUrl { get; set; }
        public string Message { get; set; }
    }

    internal static class AppShipUpdateClient
    {
        internal const string ApplicationSlug = "termlink-win64";
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        internal static string ConfiguredBaseUrl()
        {
            var configured = Environment.GetEnvironmentVariable("TERMLINK_APPSHIP_BASE_URL");
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = Environment.GetEnvironmentVariable("APPSHIP_BASE_URL");
            }
            return (configured ?? string.Empty)
                .Trim()
                .TrimEnd('/');
        }

        internal static async Task<WindowsUpdateResult> CheckAsync(
            string baseUrl,
            Version currentVersion,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return new WindowsUpdateResult
                {
                    Decision = WindowsUpdateDecision.NotConfigured,
                    Message = "Update service is not configured."
                };
            }
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) ||
                !string.Equals(root.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return new WindowsUpdateResult
                {
                    Decision = WindowsUpdateDecision.Error,
                    Message = "The update service must use HTTPS."
                };
            }

            var build = currentVersion == null
                ? 0
                : currentVersion.Build > 0
                    ? currentVersion.Build
                    : currentVersion.Revision > 0
                        ? currentVersion.Revision
                        : 1;
            var endpoint = baseUrl + "/api/v1/apps/" + ApplicationSlug +
                "/updates?platform=windows&arch=x64&channel=stable&build=" + build;
            try
            {
                using (var response = await Client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                    {
                        return new WindowsUpdateResult
                        {
                            Decision = WindowsUpdateDecision.NoUpdate,
                            Message = "You are up to date."
                        };
                    }
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return new WindowsUpdateResult
                        {
                            Decision = WindowsUpdateDecision.Error,
                            Message = "Update service returned HTTP " + (int)response.StatusCode + "."
                        };
                    }
                    return Parse(body);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                return new WindowsUpdateResult
                {
                    Decision = WindowsUpdateDecision.Error,
                    Message = error.Message.Length > 160 ? error.Message.Substring(0, 160) : error.Message
                };
            }
        }

        private static WindowsUpdateResult Parse(string body)
        {
            try
            {
                using (var document = JsonDocument.Parse(body ?? string.Empty))
                {
                    var root = document.RootElement;
                    var release = ObjectAt(root, "release") ?? ObjectAt(root, "update") ?? ObjectAt(root, "latest") ?? root;
                    var artifact = ObjectAt(release, "artifact") ?? ObjectAt(root, "artifact");
                    var rawDecision = StringAt(root, "decision", "status", "result");
                    var version = StringAt(release, "versionName", "version_name", "version");
                    var buildText = StringAt(release, "buildNumber", "build_number", "build");
                    long build;
                    long? buildNumber = long.TryParse(buildText, out build) ? build : (long?)null;
                    var downloadUrl = StringAt(artifact ?? release, "downloadUrl", "download_url", "url");
                    if (!string.IsNullOrWhiteSpace(downloadUrl) &&
                        (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var download) ||
                         !string.Equals(download.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                    {
                        downloadUrl = null;
                    }
                    return new WindowsUpdateResult
                    {
                        Decision = DecisionAt(rawDecision, version, buildNumber, downloadUrl),
                        VersionName = version,
                        BuildNumber = buildNumber,
                        ReleaseNotes = StringAt(release, "releaseNotes", "release_notes", "notes"),
                        DownloadUrl = downloadUrl,
                        Message = StringAt(root, "message", "detail")
                    };
                }
            }
            catch (JsonException)
            {
                return new WindowsUpdateResult
                {
                    Decision = WindowsUpdateDecision.Error,
                    Message = "Update service returned invalid JSON."
                };
            }
        }

        private static WindowsUpdateDecision DecisionAt(string value, string version, long? build, string download)
        {
            switch ((value ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "REQUIRED":
                case "MANDATORY":
                case "FORCE": return WindowsUpdateDecision.Required;
                case "OPTIONAL":
                case "AVAILABLE":
                case "UPDATE_AVAILABLE": return WindowsUpdateDecision.Optional;
                case "NO_UPDATE":
                case "NONE":
                case "CURRENT":
                case "UP_TO_DATE":
                case "NOT_AVAILABLE": return WindowsUpdateDecision.NoUpdate;
                default: return version != null || build.HasValue || download != null
                    ? WindowsUpdateDecision.Optional
                    : WindowsUpdateDecision.NoUpdate;
            }
        }

        private static JsonElement? ObjectAt(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.Object) return null;
            return value;
        }

        private static string StringAt(JsonElement parent, params string[] names)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!parent.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                if (value.ValueKind == JsonValueKind.Number) return value.ToString();
            }
            return null;
        }
    }
}
