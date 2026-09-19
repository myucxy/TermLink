using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
        public string ArtifactFileName { get; set; }
        public long? SizeBytes { get; set; }
        public string Sha256 { get; set; }
        public string SignerFingerprint { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Exports only the update fields the Avalonia About page can display. The
    /// signed Legacy component retains artifact URLs, hashes, signer identity,
    /// and all authority to download or launch Setup.
    /// </summary>
    internal static class WindowsUpdateResultExport
    {
        internal static string Serialize(WindowsUpdateResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            return JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    decision = result.Decision.ToString(),
                    versionName = Bounded(result.VersionName, 80),
                    buildNumber = result.BuildNumber,
                    releaseNotes = Bounded(result.ReleaseNotes, 32 * 1024),
                    message = Bounded(result.Message, 1024)
                },
                new JsonSerializerOptions { WriteIndented = true });
        }

        private static string Bounded(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
        }
    }

    /// <summary>
    /// Enforces the HTTPS-only, bounded redirect policy shared by update checks
    /// and artifact downloads. Redirects are handled by application code so an
    /// AppShip response can never silently downgrade a request to HTTP.
    /// </summary>
    internal static class HttpsRedirectPolicy
    {
        internal const int MaximumRedirects = 3;

        internal static bool IsRedirect(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.Moved ||
                statusCode == HttpStatusCode.Redirect ||
                statusCode == HttpStatusCode.RedirectMethod ||
                statusCode == HttpStatusCode.TemporaryRedirect ||
                (int)statusCode == 308;
        }

        internal static Uri ValidateInitial(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                throw new InvalidDataException("The update URL is not absolute.");
            }
            ValidateHttps(uri);
            return uri;
        }

        internal static Uri Resolve(Uri current, Uri location, int redirectsAlreadyFollowed)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (location == null) throw new InvalidDataException("The update redirect has no Location header.");
            if (redirectsAlreadyFollowed >= MaximumRedirects)
            {
                throw new HttpRequestException("The update request exceeded the redirect limit.");
            }
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            ValidateHttps(next);
            return next;
        }

        private static void ValidateHttps(Uri uri)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidDataException("The update service and every redirect must use HTTPS without embedded credentials.");
            }
        }
    }

    internal static class AppShipUpdateClient
    {
        internal static string ApplicationSlug => ReadBuildMetadata(
            "TermLinkUpdateApplicationSlug",
            "termlink-win64");
        internal static string UpdateChannel => ReadBuildMetadata(
            "TermLinkUpdateChannel",
            "stable");
        private const string PublicBaseUrl = "https://appship.kget.cc:8910";
        private const int MaximumResponseBytes = 256 * 1024;
        private const long MaximumArtifactBytes = 2L * 1024 * 1024 * 1024;
        private static readonly HttpClient Client = CreateClient();

        internal static string ConfiguredBaseUrl()
        {
            var configured = Environment.GetEnvironmentVariable("TERMLINK_APPSHIP_BASE_URL");
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = Environment.GetEnvironmentVariable("APPSHIP_BASE_URL");
            }
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = PublicBaseUrl;
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

            Uri root;
            try
            {
                root = HttpsRedirectPolicy.ValidateInitial(baseUrl + "/");
            }
            catch (Exception error) when (error is InvalidDataException || error is UriFormatException)
            {
                return Error(error.Message);
            }

            var build = CurrentBuild(currentVersion);
            var relative = "api/v1/apps/" + ApplicationSlug +
                "/updates?platform=windows&arch=x64&channel=" + UpdateChannel + "&build=" + build;
            var endpoint = new Uri(root, relative);
            try
            {
                using (var response = await SendWithRedirectsAsync(Client, endpoint, cancellationToken).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        return new WindowsUpdateResult
                        {
                            Decision = WindowsUpdateDecision.NoUpdate,
                            Message = "You are up to date."
                        };
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        return Error("Update service returned HTTP " + (int)response.StatusCode + ".");
                    }
                    if (response.Content.Headers.ContentLength.HasValue &&
                        response.Content.Headers.ContentLength.Value > MaximumResponseBytes)
                    {
                        return Error("Update service response is too large.");
                    }
                    var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
                    return Parse(Encoding.UTF8.GetString(bytes), build);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                return Error(Limit(error.Message, 240));
            }
        }

        internal static WindowsUpdateResult ParseForValidation(string body, long currentBuild = 0)
        {
            return Parse(body, currentBuild);
        }

        private static string ReadBuildMetadata(string key, string fallback)
        {
            foreach (var metadata in typeof(AppShipUpdateClient).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(metadata.Key, key, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(metadata.Value))
                {
                    return metadata.Value;
                }
            }
            return fallback;
        }

        internal static async Task<HttpResponseMessage> SendWithRedirectsAsync(
            HttpClient client,
            Uri initial,
            CancellationToken cancellationToken)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (initial == null) throw new ArgumentNullException(nameof(initial));
            HttpsRedirectPolicy.ValidateInitial(initial.AbsoluteUri);
            var current = initial;
            var redirectCount = 0;
            while (true)
            {
                var response = await client.GetAsync(
                    current,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                if (!HttpsRedirectPolicy.IsRedirect(response.StatusCode)) return response;
                try
                {
                    current = HttpsRedirectPolicy.Resolve(
                        current,
                        response.Headers.Location,
                        redirectCount);
                    redirectCount++;
                }
                finally
                {
                    response.Dispose();
                }
            }
        }

        private static WindowsUpdateResult Parse(string body, long currentBuild)
        {
            try
            {
                using (var document = JsonDocument.Parse(body ?? string.Empty))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        return Error("Update service returned an invalid response object.");
                    }
                    var release = ObjectAt(root, "release") ?? ObjectAt(root, "update") ??
                        ObjectAt(root, "latest") ?? root;
                    var artifact = ObjectAt(release, "artifact") ?? ObjectAt(root, "artifact");
                    var rawDecision = StringAt(root, "decision", "status", "result");
                    var version = StringAt(release, "versionName", "version_name", "version");
                    var buildNumber = Int64At(release, "buildNumber", "build_number", "build");
                    var decision = DecisionAt(rawDecision, version, buildNumber, artifact);
                    if ((decision == WindowsUpdateDecision.Optional || decision == WindowsUpdateDecision.Required) &&
                        buildNumber.HasValue && buildNumber.Value <= currentBuild)
                    {
                        decision = WindowsUpdateDecision.NoUpdate;
                    }
                    // Fail closed like the MSIX and Android update paths: a
                    // decision without a build number cannot be proven to be an
                    // upgrade, so it must never reach the verified-launch stage.
                    if ((decision == WindowsUpdateDecision.Optional || decision == WindowsUpdateDecision.Required) &&
                        !buildNumber.HasValue)
                    {
                        return Error("Update service response did not include a build number; refusing to update.");
                    }
                    if (decision == WindowsUpdateDecision.NoUpdate)
                    {
                        return new WindowsUpdateResult
                        {
                            Decision = decision,
                            VersionName = version,
                            BuildNumber = buildNumber,
                            ReleaseNotes = StringAt(release, "releaseNotes", "release_notes", "notes"),
                            Message = StringAt(root, "message", "detail")
                        };
                    }
                    if (decision != WindowsUpdateDecision.Optional && decision != WindowsUpdateDecision.Required)
                    {
                        return new WindowsUpdateResult { Decision = decision, Message = StringAt(root, "message", "detail") };
                    }
                    if (artifact == null)
                    {
                        return Error("AppShip did not provide an update artifact.");
                    }

                    var artifactValue = artifact.Value;
                    var downloadUrl = StringAt(artifactValue, "downloadUrl", "download_url", "url");
                    try
                    {
                        HttpsRedirectPolicy.ValidateInitial(downloadUrl);
                    }
                    catch (Exception)
                    {
                        return Error("AppShip returned a non-HTTPS artifact URL.");
                    }
                    var size = Int64At(artifactValue, "sizeBytes", "size_bytes", "size");
                    var expectedSize = Int64At(artifactValue, "expectedSizeBytes", "expected_size_bytes");
                    if (!size.HasValue || size.Value <= 0 || size.Value > MaximumArtifactBytes ||
                        (expectedSize.HasValue && expectedSize.Value != size.Value))
                    {
                        return Error("AppShip returned an invalid artifact size.");
                    }
                    var sha256 = NormalizeHex(StringAt(artifactValue, "sha256", "sha_256"));
                    if (!IsHex(sha256, 64))
                    {
                        return Error("AppShip did not provide a valid artifact SHA-256.");
                    }
                    var signer = NormalizeHex(StringAt(artifactValue, "signerFingerprint", "signer_fingerprint"));
                    if (!string.IsNullOrEmpty(signer) && !IsHex(signer, 40))
                    {
                        return Error("AppShip returned an invalid signer fingerprint.");
                    }
                    if (!MatchesOptional(artifactValue, "platform", "windows") ||
                        !MatchesOptional(artifactValue, "fileKind", "exe") ||
                        !MatchesOptional(artifactValue, "file_kind", "exe") ||
                        !MatchesArchitecture(artifactValue) ||
                        !MatchesOptional(artifactValue, "status", "ready") ||
                        IsExplicitlyFalse(artifactValue, "downloadEnabled"))
                    {
                        return Error("AppShip artifact metadata is not a ready Windows x64 EXE.");
                    }
                    var originalName = StringAt(artifactValue, "originalFilename", "original_filename", "filename");
                    if (!string.IsNullOrWhiteSpace(originalName) &&
                        !string.Equals(Path.GetExtension(originalName), ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return Error("AppShip artifact is not an EXE installer.");
                    }

                    return new WindowsUpdateResult
                    {
                        Decision = decision,
                        VersionName = version,
                        BuildNumber = buildNumber,
                        ReleaseNotes = StringAt(release, "releaseNotes", "release_notes", "notes"),
                        DownloadUrl = downloadUrl,
                        ArtifactFileName = originalName,
                        SizeBytes = size,
                        Sha256 = sha256,
                        SignerFingerprint = signer,
                        Message = StringAt(root, "message", "detail")
                    };
                }
            }
            catch (JsonException)
            {
                return Error("Update service returned invalid JSON.");
            }
        }

        private static WindowsUpdateDecision DecisionAt(
            string value,
            string version,
            long? build,
            JsonElement? artifact)
        {
            switch ((value ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "REQUIRED":
                case "MANDATORY":
                case "FORCE": return WindowsUpdateDecision.Required;
                case "OPTIONAL":
                case "AVAILABLE":
                case "UPDATE_AVAILABLE": return WindowsUpdateDecision.Optional;
                case "ERROR":
                case "FAILED":
                case "UNAVAILABLE": return WindowsUpdateDecision.Error;
                case "NO_UPDATE":
                case "NONE":
                case "CURRENT":
                case "UP_TO_DATE":
                case "NOT_AVAILABLE": return WindowsUpdateDecision.NoUpdate;
                default: return version != null || build.HasValue || artifact.HasValue
                    ? WindowsUpdateDecision.Optional
                    : WindowsUpdateDecision.NoUpdate;
            }
        }

        internal static long CurrentBuild(Version currentVersion)
        {
            if (currentVersion == null) return 0;
            // Windows setup versions are four-part values such as 0.1.14.18,
            // where the AppShip build number is the fourth component. Prefer
            // Revision whenever it is present; using Build first incorrectly
            // reported build 18 as build 14 and offered an update to itself.
            if (currentVersion.Revision >= 0) return currentVersion.Revision;
            if (currentVersion.Build >= 0) return currentVersion.Build;
            return 0;
        }

        private static HttpClient CreateClient()
        {
            return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        private static async Task<byte[]> ReadBoundedAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int count;
                while ((count = await input.ReadAsync(
                    buffer,
                    0,
                    buffer.Length,
                    cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > MaximumResponseBytes)
                        throw new InvalidDataException("Update service response is too large.");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
        }

        private static WindowsUpdateResult Error(string message)
        {
            return new WindowsUpdateResult
            {
                Decision = WindowsUpdateDecision.Error,
                Message = message
            };
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

        private static long? Int64At(JsonElement parent, params string[] names)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in names)
            {
                if (!parent.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
            }
            return null;
        }

        private static bool MatchesOptional(JsonElement parent, string propertyName, string expected)
        {
            if (!parent.TryGetProperty(propertyName, out var value)) return true;
            return value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesArchitecture(JsonElement parent)
        {
            var architecture = StringAt(parent, "architecture", "arch");
            return string.IsNullOrWhiteSpace(architecture) ||
                string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(architecture, "amd64", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplicitlyFalse(JsonElement parent, string propertyName)
        {
            return parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.False;
        }

        internal static string NormalizeHex(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (!char.IsWhiteSpace(character) && character != ':' && character != '-')
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }
            return builder.ToString();
        }

        internal static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (var character in value)
            {
                if ((character < '0' || character > '9') && (character < 'a' || character > 'f')) return false;
            }
            return true;
        }

        private static string Limit(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value)) return "No error details were provided.";
            value = value.Trim();
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
        }
    }
}
