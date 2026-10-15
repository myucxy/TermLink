using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Protocol;

namespace TermLink.Agent
{
    internal sealed class ProductionEnrollmentOptions
    {
        public string ServerUrl { get; private set; }
        public string PcName { get; private set; }

        public static bool IsEnrollment(string[] args)
        {
            foreach (var argument in args)
            {
                if (string.Equals(argument, "--link-account", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static ProductionEnrollmentOptions Parse(string[] args)
        {
            string serverUrl = null;
            string pcName = Environment.MachineName;
            var link = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--link-account": link = true; break;
                    case "--server-url": serverUrl = Required(args, ref index, "--server-url"); break;
                    case "--pc-name": pcName = Required(args, ref index, "--pc-name"); break;
                    default: throw new ArgumentException("Unknown account-link argument: " + args[index]);
                }
            }
            if (!link || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) ||
                !ProductionServerOriginPolicy.IsAllowedOrigin(server) || !string.IsNullOrEmpty(server.Query) ||
                !string.IsNullOrEmpty(server.Fragment))
            {
                throw new ArgumentException("--server-url must be a TermLink origin.");
            }
            if (string.IsNullOrWhiteSpace(pcName) || pcName.Length > 128)
            {
                throw new ArgumentException("--pc-name is invalid.");
            }
            return new ProductionEnrollmentOptions
            {
                ServerUrl = serverUrl.TrimEnd('/'),
                PcName = pcName.Trim()
            };
        }

        private static string Required(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException(option + " requires a value.");
            return args[index];
        }
    }

    internal sealed class ProductionIdentitySelection
    {
        public bool ReuseDeviceIdentity { get; set; }
        public bool ReusePcIdentity { get; set; }
        public Guid DeviceId { get; set; }
        public Guid PcId { get; set; }
    }

    internal sealed class ProductionEnrollmentException : Exception
    {
        public ProductionEnrollmentException(string code, string message, Exception innerException = null)
            : base(message, innerException)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("An enrollment error code is required.", nameof(code));
            Code = code;
        }

        public string Code { get; }
    }

    public static class ProductionEnrollment
    {
        private const string AuthorizationScopes = "openid profile email offline_access";
        private const string BrowserCallbackTemplate = "http://127.0.0.1/oauth2/callback";
        public const string PublicServerUrl = "https://termlink.kget.cc:8910";
        private static readonly TimeSpan AuthorizationCallbackTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan AuthorizationCallbackConnectionTimeout = TimeSpan.FromSeconds(5);
        private const int MaximumAuthorizationCallbackConnections = 64;
        private static readonly object BrowserAuthorizationSync = new object();
        private static BrowserAuthorizationAttempt activeBrowserAuthorization;

        internal sealed class BrowserAuthorizationAttempt
        {
            public ProductionEnrollmentOptions Options { get; set; }
            public string AuthorizationUrl;
            public Task Completion { get; set; }
            public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
            public bool Committing { get; private set; }
            internal void Cancel() {
                lock (BrowserAuthorizationSync) {
                    if (Committing) throw new ProductionEnrollmentException("ACCOUNT_AUTHORIZATION_COMMITTING", "Account authorization is being saved.");
                    Cancellation.Cancel();
                }
            }
            internal void BeginCommit() {
                lock (BrowserAuthorizationSync) { Cancellation.Token.ThrowIfCancellationRequested(); Committing = true; }
            }
        }

        internal static void CancelBrowserAuthorization()
        {
            lock (BrowserAuthorizationSync)
            {
                var attempt = activeBrowserAuthorization;
                if (attempt == null || attempt.Completion == null || attempt.Completion.IsCompleted)
                    throw new ProductionEnrollmentException("ACCOUNT_AUTHORIZATION_NOT_WAITING", "No browser authorization is waiting.");
                attempt.Cancel();
            }
        }

        internal static void ReopenBrowserAuthorization()
        {
            lock (BrowserAuthorizationSync)
            {
                var attempt = activeBrowserAuthorization;
                if (attempt == null || attempt.Completion == null || attempt.Completion.IsCompleted ||
                    attempt.Cancellation.IsCancellationRequested || attempt.Committing ||
                    string.IsNullOrWhiteSpace(attempt.AuthorizationUrl))
                    throw new ProductionEnrollmentException("ACCOUNT_AUTHORIZATION_NOT_WAITING", "The authorization page is not ready.");
                OpenSystemBrowser(attempt.AuthorizationUrl);
            }
        }

        private sealed class ClientConfiguration
        {
            [JsonPropertyName("oidc_issuer")]
            public string OIDCIssuer { get; set; }

            [JsonPropertyName("windows_oidc_client_id")]
            public string ClientId { get; set; }

            [JsonPropertyName("windows_redirect_uri")]
            public string RedirectUri { get; set; }
        }

        private sealed class OIDCConfiguration
        {
            [JsonPropertyName("authorization_endpoint")]
            public string AuthorizationEndpoint { get; set; }

            [JsonPropertyName("token_endpoint")]
            public string TokenEndpoint { get; set; }
        }

        internal static string NormalizeServerOrigin(string value)
        {
            // Plain HTTP is accepted for loopback/private-network origins so a
            // local development server can be exercised end to end.
            if (!Uri.TryCreate(value, UriKind.Absolute, out var server) ||
                !ProductionServerOriginPolicy.IsAllowedOrigin(server) ||
                string.IsNullOrEmpty(server.Query) == false ||
                string.IsNullOrEmpty(server.Fragment) == false ||
                !string.IsNullOrEmpty(server.UserInfo) ||
                (!string.IsNullOrEmpty(server.AbsolutePath) && server.AbsolutePath != "/"))
            {
                throw new ArgumentException("The TermLink server origin must be an HTTPS origin.");
            }
            return new UriBuilder(server.Scheme, server.IdnHost, server.IsDefaultPort ? -1 : server.Port)
                .Uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).TrimEnd('/');
        }

        internal static string ConfiguredServerUrl() => ProductionAccountStorage.LoadServerUrl();

        public static void ConfigureServer(string serverUrl) =>
            ProductionAccountStorage.SaveServerUrl(NormalizeServerOrigin(serverUrl));

        private sealed class TokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; }

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; }

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }
        }

        private sealed class MachineResolutionResponse
        {
            [JsonPropertyName("found")]
            public bool Found { get; set; }

            [JsonPropertyName("pc_id")]
            public string PcId { get; set; }

            [JsonPropertyName("pc_name")]
            public string PcName { get; set; }

            [JsonPropertyName("key_epoch")]
            public long KeyEpoch { get; set; }
        }

        internal static int Run(ProductionEnrollmentOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            LinkAccount(options);
            Console.WriteLine("TermLink account linked.");
            return 0;
        }

        /// <summary>
        /// Runs the browser-based OIDC flow inside the interactive TermLink
        /// process. The browser remains external, but no standalone Agent
        /// process is created for enrollment.
        /// </summary>
        public static void LinkAccount(string serverUrl, string pcName)
        {
            var options = ProductionEnrollmentOptions.Parse(new[]
            {
                "--link-account",
                "--server-url", serverUrl,
                "--pc-name", pcName
            });
            LinkAccount(options);
        }

        internal static bool TryContinueActiveBrowserAuthorization(string serverUrl)
        {
            BrowserAuthorizationAttempt attempt;
            lock (BrowserAuthorizationSync)
            {
                attempt = activeBrowserAuthorization;
                if (attempt == null || attempt.Completion == null || attempt.Completion.IsCompleted ||
                    !string.Equals(attempt.Options.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var authorizationUrl = Volatile.Read(ref attempt.AuthorizationUrl);
            if (!string.IsNullOrWhiteSpace(authorizationUrl)) OpenSystemBrowser(authorizationUrl);
            try
            {
                attempt.Completion.GetAwaiter().GetResult();
                return true;
            }
            finally
            {
                ClearCompletedBrowserAuthorization(attempt);
            }
        }

        private static void LinkAccount(ProductionEnrollmentOptions options)
        {
            BrowserAuthorizationAttempt attempt;
            var reopenExistingAttempt = false;
            lock (BrowserAuthorizationSync)
            {
                attempt = activeBrowserAuthorization;
                if (attempt != null && attempt.Completion != null && !attempt.Completion.IsCompleted)
                {
                    if (!string.Equals(attempt.Options.ServerUrl, options.ServerUrl, StringComparison.OrdinalIgnoreCase))
                        throw new ProductionEnrollmentException(
                            "ACCOUNT_AUTHORIZATION_IN_PROGRESS",
                            "A browser authorization attempt for another server is already in progress.");
                    reopenExistingAttempt = true;
                }
                else
                {
                    attempt = new BrowserAuthorizationAttempt { Options = options };
                    activeBrowserAuthorization = attempt;
                    attempt.Completion = RunAsync(options, attempt);
                }
            }

            if (reopenExistingAttempt)
            {
                var authorizationUrl = Volatile.Read(ref attempt.AuthorizationUrl);
                if (!string.IsNullOrWhiteSpace(authorizationUrl)) OpenSystemBrowser(authorizationUrl);
            }

            try
            {
                attempt.Completion.GetAwaiter().GetResult();
            }
            finally
            {
                ClearCompletedBrowserAuthorization(attempt);
            }
        }

        private static void ClearCompletedBrowserAuthorization(BrowserAuthorizationAttempt attempt)
        {
            if (attempt == null || attempt.Completion == null || !attempt.Completion.IsCompleted) return;
            lock (BrowserAuthorizationSync)
            {
                if (ReferenceEquals(activeBrowserAuthorization, attempt)) {
                    activeBrowserAuthorization = null; attempt.Cancellation.Dispose();
                }
            }
        }

        public static void LoginDirect(string serverUrl, string pcName, string username, string password)
        {
            RunDirectAsync(serverUrl, pcName, username, null, null, password, false).GetAwaiter().GetResult();
        }

        public static void RegisterDirect(string serverUrl, string pcName, string username, string email, string password, string confirm)
        {
            RegisterDirect(serverUrl, pcName, username, username, email, password, confirm);
        }

        public static void RegisterDirect(
            string serverUrl,
            string pcName,
            string username,
            string nickname,
            string email,
            string password,
            string confirm)
        {
            RunDirectAsync(serverUrl, pcName, username, nickname, email, password, true, confirm).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Forgets only the local production account session. The caller must
        /// first deactivate the current account runtime so in-memory credentials
        /// cannot continue a Relay session or race with file removal. Local
        /// terminal/session workers may remain alive for the next account.
        /// </summary>
        public static void ForgetLocalAccount()
        {
            ProductionAccountStorage.ForgetLocalAccount();
        }

        internal static ProductionIdentitySelection SelectIdentity(
            string credentialAccountKey,
            Guid existingDeviceId,
            string configAccountKey,
            Guid existingPcId,
            string incomingAccountKey)
        {
            var accountMatches = existingDeviceId != Guid.Empty &&
                ProductionCredentials.CanReuseIdentity(credentialAccountKey, incomingAccountKey) &&
                (string.IsNullOrWhiteSpace(configAccountKey) ||
                    ProductionCredentials.CanReuseIdentity(configAccountKey, incomingAccountKey));
            var pcMatches = accountMatches && existingPcId != Guid.Empty;
            return new ProductionIdentitySelection
            {
                ReuseDeviceIdentity = accountMatches,
                ReusePcIdentity = pcMatches,
                DeviceId = accountMatches ? existingDeviceId : Guid.NewGuid(),
                PcId = pcMatches ? existingPcId : Guid.NewGuid()
            };
        }

        private static async Task RunDirectAsync(
            string serverUrl,
            string pcName,
            string username,
            string nickname,
            string email,
            string password,
            bool register,
            string confirm = null)
        {
            serverUrl = NormalizeServerOrigin(serverUrl);
            if (string.IsNullOrWhiteSpace(username) || username.Length > 128 || string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Account credentials are invalid.");
            using var http = ProductionTls.CreateHttpClient(serverUrl.TrimEnd('/'));
            http.Timeout = TimeSpan.FromSeconds(20);
            var client = await GetJsonAsync<ClientConfiguration>(http, serverUrl.TrimEnd('/') + "/v1/client-config", CancellationToken.None).ConfigureAwait(false);
            ValidateClientConfigurationForDirect(client);
            if (register)
            {
                if (string.IsNullOrWhiteSpace(nickname) || string.IsNullOrWhiteSpace(email) ||
                    string.IsNullOrWhiteSpace(confirm) || password != confirm)
                    throw new ArgumentException("Registration fields are invalid.");
                using var registration = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + "/v1/auth/register")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        username = username.Trim(), nickname = nickname.Trim(), email = email.Trim(),
                        password, password_confirm = confirm
                    }), Encoding.UTF8, "application/json")
                };
                using var registrationResponse = await http.SendAsync(registration, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (!registrationResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException("TermLink account registration was rejected.");
            }
            var discovery = await GetJsonAsync<OIDCConfiguration>(http, client.OIDCIssuer.TrimEnd('/') + "/.well-known/openid-configuration", CancellationToken.None).ConfigureAwait(false);
            if (!IsHttps(discovery.TokenEndpoint))
                throw new InvalidDataException("OIDC token endpoint must be HTTPS or a local HTTP origin.");
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password", ["client_id"] = client.ClientId,
                    ["scope"] = AuthorizationScopes, ["username"] = username.Trim(), ["password"] = password
                })
            };
            using var tokenResponse = await http.SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!tokenResponse.IsSuccessStatusCode)
                throw new InvalidOperationException("TermLink account login was rejected.");
            var tokens = await ReadJsonAsync<TokenResponse>(tokenResponse, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken) || tokens.ExpiresIn < 30)
                throw new InvalidDataException("OIDC token response is incomplete.");
            await PersistTokensAsync(serverUrl.TrimEnd('/'), pcName, client, tokens, http).ConfigureAwait(false);
        }

        private static void ValidateClientConfigurationForDirect(ClientConfiguration client)
        {
            if (client == null || !IsHttps(client.OIDCIssuer) || string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.RedirectUri))
                throw new InvalidDataException("TermLink client configuration is invalid.");
        }

        private static async Task PersistTokensAsync(
            string serverUrl,
            string pcName,
            ClientConfiguration client,
            TokenResponse tokens,
            HttpClient http)
        {
            var existingConfig = ProductionAgentConfig.TryLoad();
            using var existingCredentials = ProductionCredentials.TryLoad();
            var account = ProductionCredentials.ReadAccountClaims(tokens.AccessToken);
            var accountKey = ProductionCredentials.ReadAccountKey(tokens.AccessToken, client.OIDCIssuer);
            if (string.IsNullOrWhiteSpace(accountKey))
                throw new InvalidDataException("The OIDC access token does not identify a stable issuer and subject.");
            var machineId = ProductionMachineIdentity.DeriveForCurrentMachine(client.OIDCIssuer, accountKey);
            var selection = SelectIdentity(
                existingCredentials?.AccountKey,
                existingCredentials == null ? Guid.Empty : existingCredentials.DeviceId,
                existingConfig?.AccountKey,
                existingConfig == null ? Guid.Empty : existingConfig.PcId,
                accountKey);
            var existingIssuerMatches = existingConfig == null ||
                string.Equals(
                    ProductionCredentials.NormalizeIssuer(existingConfig.OIDCIssuer),
                    ProductionCredentials.NormalizeIssuer(client.OIDCIssuer),
                    StringComparison.Ordinal);
            if (existingConfig != null &&
                !string.Equals(existingConfig.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase) &&
                (!selection.ReusePcIdentity || !existingIssuerMatches))
            {
                throw new InvalidOperationException("The existing TermLink device is linked to a different identity service.");
            }

            using var retained = selection.ReusePcIdentity
                ? null
                : ProductionIdentityStore.TryLoad(accountKey, client.OIDCIssuer, machineId);
            var reuseRetainedDevice = !selection.ReuseDeviceIdentity && retained != null;
            var reuseRetainedPc = !selection.ReusePcIdentity && retained != null;
            var revoked = false;
            if (selection.ReuseDeviceIdentity)
            {
                revoked = await IsDeviceRevokedAsync(
                    http, serverUrl, tokens.AccessToken, selection.DeviceId,
                    existingCredentials.Identity, CancellationToken.None).ConfigureAwait(false);
            }
            else if (reuseRetainedDevice)
            {
                revoked = await IsDeviceRevokedAsync(
                    http, serverUrl, tokens.AccessToken, retained.DeviceId,
                    retained.Identity, CancellationToken.None).ConfigureAwait(false);
            }
            if (revoked)
            {
                reuseRetainedDevice = false;
                reuseRetainedPc = false;
            }
            var reuseExistingDevice = selection.ReuseDeviceIdentity && !revoked;
            var reuseExistingPc = selection.ReusePcIdentity && !revoked;
            MachineResolutionResponse recovered = null;
            if (!reuseExistingPc && !reuseRetainedPc)
            {
                recovered = await ResolveMachineAsync(
                    serverUrl,
                    machineId,
                    tokens.AccessToken,
                    http).ConfigureAwait(false);
            }
            if (revoked && recovered == null)
                throw new ProductionEnrollmentException(
                    "ACCOUNT_MACHINE_IDENTITY_UNAVAILABLE",
                    "The revoked device's PC could not be resolved safely.");

            var deviceId = reuseExistingDevice
                ? selection.DeviceId
                : reuseRetainedDevice ? retained.DeviceId : Guid.NewGuid();
            var resolvedPcId = reuseExistingPc
                ? selection.PcId
                : reuseRetainedPc ? retained.PcId
                : recovered != null ? Guid.Parse(recovered.PcId)
                : ProductionMachineIdentity.DeriveInitialPcId(machineId);
            var resolvedPcName = reuseExistingPc && existingConfig != null
                ? existingConfig.PcName
                : reuseRetainedPc ? retained.PcName
                : recovered != null && !string.IsNullOrWhiteSpace(recovered.PcName)
                    ? NormalizePcName(recovered.PcName)
                    : NormalizePcName(pcName);
            var resolvedKeyEpoch = reuseExistingPc && existingConfig != null
                ? existingConfig.KeyEpoch
                : reuseRetainedPc ? retained.KeyEpoch
                : recovered != null ? recovered.KeyEpoch
                : 1;
            DeviceIdentity identity = null;
            var ownsIdentity = false;
            try
            {
                if (reuseExistingDevice)
                    identity = existingCredentials.Identity;
                else if (reuseRetainedDevice)
                    identity = retained.Identity;
                else
                {
                    identity = DeviceIdentity.Generate();
                    ownsIdentity = true;
                }
                var config = new ProductionAgentConfig
                {
                    ServerUrl = serverUrl,
                    PcId = resolvedPcId,
                    PcName = resolvedPcName,
                    AccountKey = accountKey,
                    AccountUsername = string.IsNullOrWhiteSpace(account.Username) && reuseExistingPc ? existingConfig?.AccountUsername : account.Username,
                    AccountNickname = string.IsNullOrWhiteSpace(account.Nickname) && reuseExistingPc ? existingConfig?.AccountNickname : account.Nickname,
                    AccountEmail = string.IsNullOrWhiteSpace(account.Email) && reuseExistingPc ? existingConfig?.AccountEmail : account.Email,
                    AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    KeyEpoch = resolvedKeyEpoch,
                    OIDCIssuer = client.OIDCIssuer,
                    OIDCClientId = client.ClientId,
                    MachineId = machineId
                };
                lock (ProductionAccountStorage.WriteSync)
                {
                    ProductionCredentials.SaveWithRefreshToken(
                        deviceId,
                        identity,
                        accountKey,
                        tokens.AccessToken,
                        tokens.RefreshToken,
                        DateTime.UtcNow.AddSeconds(tokens.ExpiresIn));
                    ProductionAgentConfig.Save(config);
                    ProductionAccountStorage.SaveServerUrl(serverUrl);
                    ProductionIdentityStore.TrySave(config, deviceId, identity, accountKey);
                }
            }
            finally
            {
                if (ownsIdentity) identity?.Dispose();
            }
        }

        internal static async Task<bool> IsDeviceRevokedAsync(
            HttpClient http,
            string serverUrl,
            string accessToken,
            Guid deviceId,
            DeviceIdentity identity,
            CancellationToken token)
        {
            if (http == null || string.IsNullOrWhiteSpace(serverUrl) ||
                string.IsNullOrWhiteSpace(accessToken) || deviceId == Guid.Empty || identity == null)
                throw new ArgumentException("The retained device status request is incomplete.");
            const string path = "/v1/devices/self";
            using var request = new HttpRequestMessage(HttpMethod.Get, serverUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var nonceBytes = RandomNumberGenerator.GetBytes(16);
            var nonce = Base64Url(nonceBytes);
            var bodyHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
            var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", "GET", path, timestamp, nonce, bodyHash);
            request.Headers.TryAddWithoutValidation("X-TermLink-Device", deviceId.ToString("D"));
            request.Headers.TryAddWithoutValidation("X-TermLink-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-TermLink-Nonce", nonce);
            request.Headers.TryAddWithoutValidation("X-TermLink-Signature", Base64Url(identity.Sign(Encoding.UTF8.GetBytes(canonical))));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK) return false;
            var (revoked, detail) = await ReadDeviceStatusRejectionAsync(response, token).ConfigureAwait(false);
            if (revoked) return true;
            throw new ProductionEnrollmentException(
                "ACCOUNT_DEVICE_STATUS_UNAVAILABLE",
                "The existing TermLink device status could not be verified safely." + detail);
        }

        private static async Task<(bool Revoked, string Detail)> ReadDeviceStatusRejectionAsync(
            HttpResponseMessage response, CancellationToken token)
        {
            // Non-200 device-status rejections are read exactly once with a
            // bounded forward-only window (no seek, no second read) so
            // chunked or non-seekable streams behave the same. The single
            // parse drives both the revocation decision and the sanitized
            // http_status/server_code/server_message_key diagnostics; raw
            // bodies and credentials are never retained or logged.
            var detail = " http_status=" + ((int)response.StatusCode);
            if (response.Content == null) return (false, detail);
            string serverCode = null;
            string serverMessageKey = null;
            var truncated = false;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var bytes = new byte[4097];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                if (count > 4096)
                {
                    truncated = true;
                }
                else
                {
                    using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    {
                        if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                            serverCode = code.GetString();
                        if (error.TryGetProperty("message_key", out var key) && key.ValueKind == JsonValueKind.String)
                            serverMessageKey = key.GetString();
                    }
                }
            }
            catch (JsonException) { }
            var revoked = response.StatusCode == HttpStatusCode.Unauthorized &&
                string.Equals(serverCode, "DEVICE_REVOKED", StringComparison.Ordinal);
            if (serverCode != null) detail += " server_code=" + SanitizeExternalToken(serverCode);
            if (serverMessageKey != null) detail += " server_message_key=" + SanitizeExternalToken(serverMessageKey);
            if (truncated) detail += " server_body=truncated";
            return (revoked, detail);
        }

        private static string SanitizeExternalToken(string value)
        {
            // error.code/message_key are server-controlled external input:
            // strip control characters (including CR/LF) and bound the length
            // before they can reach exception messages or log fields.
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (!char.IsControl(character)) builder.Append(character);
            }
            var text = builder.ToString();
            return text.Length > 96 ? text[..96] : text;
        }

        private static async Task<MachineResolutionResponse> ResolveMachineAsync(
            string serverUrl,
            string machineId,
            string accessToken,
            HttpClient http)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl + "/v1/pcs/resolve-machine")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { machine_id = machineId }),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProductionEnrollmentException(
                    "ACCOUNT_MACHINE_IDENTITY_UNAVAILABLE",
                    "The TermLink Server must support stable machine identity before this PC can be linked safely.");
            var result = await ReadJsonAsync<MachineResolutionResponse>(response, CancellationToken.None).ConfigureAwait(false);
            if (!result.Found) return null;
            if (!Guid.TryParse(result.PcId, out var pcId) || pcId == Guid.Empty ||
                string.IsNullOrWhiteSpace(result.PcName) || result.PcName.Length > 128 || result.KeyEpoch < 1)
                throw new InvalidDataException("The TermLink machine identity response is invalid.");
            return result;
        }

        private static string NormalizePcName(string pcName)
        {
            pcName = (pcName ?? Environment.MachineName).Trim();
            if (pcName.Length < 1 || pcName.Length > 128) return Environment.MachineName;
            return pcName;
        }

        private static async Task RunAsync(
            ProductionEnrollmentOptions options,
            BrowserAuthorizationAttempt attempt)
        {
            try { await RunBrowserAuthorizationAsync(options, attempt).ConfigureAwait(false); }
            catch (OperationCanceledException) when (attempt.Cancellation.IsCancellationRequested)
            {
                throw new ProductionEnrollmentException("ACCOUNT_AUTHORIZATION_CANCELLED", "Browser authorization was cancelled.");
            }
        }

        private static async Task RunBrowserAuthorizationAsync(
            ProductionEnrollmentOptions options,
            BrowserAuthorizationAttempt attempt)
        {
            var cancellation = attempt.Cancellation.Token;
            using var http = ProductionTls.CreateHttpClient(options.ServerUrl);
            http.Timeout = TimeSpan.FromSeconds(20);
            ClientConfiguration client;
            try
            {
                client = await GetJsonAsync<ClientConfiguration>(
                    http,
                    options.ServerUrl + "/v1/client-config",
                    cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
            {
                throw new ProductionEnrollmentException(
                    "ACCOUNT_IDENTITY_SERVICE_TIMEOUT",
                    "The TermLink client configuration request timed out.",
                    error);
            }
            ValidateClientConfiguration(client);
            OIDCConfiguration discovery;
            try
            {
                discovery = await GetJsonAsync<OIDCConfiguration>(
                    http,
                    client.OIDCIssuer.TrimEnd('/') + "/.well-known/openid-configuration",
                    cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
            {
                throw new ProductionEnrollmentException(
                    "ACCOUNT_IDENTITY_SERVICE_TIMEOUT",
                    "The OIDC discovery request timed out.",
                    error);
            }
            if (!IsHttps(discovery.AuthorizationEndpoint) || !IsHttps(discovery.TokenEndpoint))
                throw new InvalidDataException("OIDC discovery endpoints are invalid.");

            var redirectTemplate = new Uri(client.RedirectUri);
            using var listener = StartCallbackListener(redirectTemplate, out var redirect);
            var redirectUri = redirect.AbsoluteUri;

            var verifierBytes = new byte[32];
            var stateBytes = new byte[32];
            RandomNumberGenerator.Fill(verifierBytes);
            RandomNumberGenerator.Fill(stateBytes);
            var verifier = Base64Url(verifierBytes);
            var state = Base64Url(stateBytes);
            CryptographicOperations.ZeroMemory(verifierBytes);
            CryptographicOperations.ZeroMemory(stateBytes);
            var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
            var challenge = Base64Url(challengeBytes);
            CryptographicOperations.ZeroMemory(challengeBytes);

            var authorizationUrl = discovery.AuthorizationEndpoint + "?" + await FormAsync(new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = client.ClientId,
                ["redirect_uri"] = redirectUri,
                ["scope"] = AuthorizationScopes,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
            }).ConfigureAwait(false);
            lock (BrowserAuthorizationSync)
            {
                cancellation.ThrowIfCancellationRequested();
                Volatile.Write(ref attempt.AuthorizationUrl, authorizationUrl);
                OpenSystemBrowser(authorizationUrl);
            }

            string code;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(AuthorizationCallbackTimeout);
                try
                {
                    code = await ReceiveAuthorizationCodeAsync(
                        listener,
                        redirect,
                        state,
                        timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
                {
                    throw new ProductionEnrollmentException(
                        "ACCOUNT_AUTHORIZATION_TIMEOUT",
                        "Browser authorization did not return before the callback timeout.",
                        error);
                }
            }
            listener.Stop();

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = client.ClientId,
                    ["redirect_uri"] = redirectUri,
                    ["code"] = code,
                    ["code_verifier"] = verifier
                })
            };
            HttpResponseMessage tokenResponse;
            try
            {
                tokenResponse = await http.SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
            {
                throw new ProductionEnrollmentException(
                    "ACCOUNT_TOKEN_EXCHANGE_TIMEOUT",
                    "The OIDC authorization code exchange timed out.",
                    error);
            }
            using (tokenResponse)
            {
                if (!tokenResponse.IsSuccessStatusCode)
                    throw new ProductionEnrollmentException(
                        "ACCOUNT_TOKEN_EXCHANGE_FAILED",
                        "The OIDC authorization code exchange failed.");
                var tokens = await ReadJsonAsync<TokenResponse>(tokenResponse, cancellation).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken) || tokens.ExpiresIn < 30)
                    throw new InvalidDataException("OIDC token response is incomplete.");

                try
                {
                    attempt.BeginCommit();
                    await PersistTokensAsync(options.ServerUrl, options.PcName, client, tokens, http).ConfigureAwait(false);
                }
                finally
                {
                    verifier = null;
                    state = null;
                    code = null;
                }
            }
        }

        private static void ValidateClientConfiguration(ClientConfiguration client)
        {
            if (client == null || !IsHttps(client.OIDCIssuer) || string.IsNullOrWhiteSpace(client.ClientId) ||
                !Uri.TryCreate(client.RedirectUri, UriKind.Absolute, out var redirect) ||
                !IsValidBrowserCallbackTemplate(redirect))
            {
                throw new InvalidDataException("Windows OIDC client configuration is invalid.");
            }
        }

        private static bool IsHttps(string value)
        {
            // Local development relaxation: allow plain HTTP for loopback and
            // private-network origins so a local Docker server works without
            // a TLS edge.
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme == Uri.UriSchemeHttps) return true;
                if (uri.Scheme == Uri.UriSchemeHttp && ProductionServerOriginPolicy.IsPrivateOrLoopbackHost(uri.Host)) return true;
            }
            return false;
        }

        internal static ProcessStartInfo CreateBrowserStartInfo(string authorizationUrl)
        {
            if (!IsHttps(authorizationUrl))
                throw new ArgumentException("The browser authorization URL must use HTTPS.", nameof(authorizationUrl));
            return new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true,
                Verb = "open",
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
        }

        internal static ProcessStartInfo CreateBrowserExecutableStartInfo(
            string executablePath,
            string authorizationUrl)
        {
            if (!IsHttps(authorizationUrl))
                throw new ArgumentException("The browser authorization URL must use HTTPS.", nameof(authorizationUrl));
            if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) ||
                !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The browser executable path is invalid.", nameof(executablePath));
            }
            var start = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(executablePath),
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
            start.ArgumentList.Add(authorizationUrl);
            return start;
        }

        private static void OpenSystemBrowser(string authorizationUrl)
        {
            Exception launchError = null;
            try
            {
                using var browser = Process.Start(CreateBrowserStartInfo(authorizationUrl));
                return;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                launchError = error;
            }

            foreach (var executablePath in BrowserFallbackExecutablePaths())
            {
                if (!File.Exists(executablePath)) continue;
                try
                {
                    using var browser = Process.Start(
                        CreateBrowserExecutableStartInfo(executablePath, authorizationUrl));
                    return;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    launchError = error;
                }
            }

            throw new ProductionEnrollmentException(
                "ACCOUNT_BROWSER_OPEN_FAILED",
                "The system browser could not be opened.",
                launchError);
        }

        private static IEnumerable<string> BrowserFallbackExecutablePaths()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new[]
            {
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localApplicationData, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe")
            }.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        internal static TcpListener StartCallbackListener(Uri redirectTemplate, out Uri redirect)
        {
            if (redirectTemplate == null) throw new ArgumentNullException(nameof(redirectTemplate));
            if (!IsValidBrowserCallbackTemplate(redirectTemplate))
            {
                throw new ArgumentException("The browser callback URI template is invalid.", nameof(redirectTemplate));
            }

            redirect = null;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Server.ExclusiveAddressUse = true;
            try
            {
                // Browsers, endpoint protection, and local network inspection
                // can establish a speculative loopback connection before the
                // actual OAuth redirect arrives. Keep enough backlog for the
                // authenticated callback instead of letting one probe consume
                // the only pending connection.
                listener.Start(8);
                var endpoint = listener.LocalEndpoint as IPEndPoint;
                if (endpoint == null || !IPAddress.Loopback.Equals(endpoint.Address) || endpoint.Port < 1)
                    throw new InvalidOperationException("Windows did not assign a valid loopback callback endpoint.");
                redirect = new UriBuilder(redirectTemplate) { Port = endpoint.Port }.Uri;
                return listener;
            }
            catch (Exception error) when (error is SocketException or InvalidOperationException)
            {
                listener.Stop();
                throw new ProductionEnrollmentException(
                    "ACCOUNT_CALLBACK_LISTENER_FAILED",
                    "The local browser callback listener could not be started.",
                    error);
            }
            catch
            {
                listener.Stop();
                throw;
            }
        }

        private static bool IsValidBrowserCallbackTemplate(Uri redirect)
        {
            return redirect != null &&
                string.Equals(redirect.OriginalString, BrowserCallbackTemplate, StringComparison.Ordinal) &&
                redirect.Scheme == Uri.UriSchemeHttp && redirect.Host == "127.0.0.1" && redirect.IsDefaultPort &&
                redirect.AbsolutePath == "/oauth2/callback" && string.IsNullOrEmpty(redirect.UserInfo) &&
                string.IsNullOrEmpty(redirect.Query) && string.IsNullOrEmpty(redirect.Fragment);
        }

        private static async Task<T> GetJsonAsync<T>(HttpClient http, string url, CancellationToken token)
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("TermLink discovery request failed.");
            return await ReadJsonAsync<T>(response, token).ConfigureAwait(false);
        }

        private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken token)
        {
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token).ConfigureAwait(false);
            if (result == null) throw new InvalidDataException("JSON response is empty.");
            return result;
        }

        private static async Task<string> ReadAuthorizationResponseAsync(
            TcpClient connection,
            Uri expectedRedirect,
            string expectedState,
            CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (expectedRedirect == null) throw new ArgumentNullException(nameof(expectedRedirect));
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            var requestLine = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > 8192)
                throw new InvalidDataException("OIDC callback request is invalid.");
            string host = null;
            for (var count = 0; count < 100; count++)
            {
                var header = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (header == null || header.Length == 0) break;
                if (header.Length > 8192 || count == 99) throw new InvalidDataException("OIDC callback headers are invalid.");
                var separator = header.IndexOf(':');
                if (separator > 0 && string.Equals(header.Substring(0, separator).Trim(), "Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (host != null) throw new InvalidDataException("OIDC callback contains duplicate Host headers.");
                    host = header.Substring(separator + 1).Trim();
                }
            }
            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || parts[0] != "GET" ||
                (parts[2] != "HTTP/1.1" && parts[2] != "HTTP/1.0"))
                throw new InvalidDataException("OIDC callback method is invalid.");
            if (parts[2] == "HTTP/1.1" && !string.Equals(host, expectedRedirect.Authority, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OIDC callback Host is invalid.");

            Uri target;
            if (parts[1].StartsWith("/", StringComparison.Ordinal))
            {
                target = new Uri(expectedRedirect.GetLeftPart(UriPartial.Authority) + parts[1]);
            }
            else if (!Uri.TryCreate(parts[1], UriKind.Absolute, out target))
            {
                throw new InvalidDataException("OIDC callback target is invalid.");
            }
            var query = ParseQuery(target.Query);
            string code = null;
            var targetMatches = target.Scheme == Uri.UriSchemeHttp && target.Host == "127.0.0.1" &&
                target.Port == expectedRedirect.Port && target.AbsolutePath == expectedRedirect.AbsolutePath &&
                string.IsNullOrEmpty(target.UserInfo) && string.IsNullOrEmpty(target.Fragment);
            var stateMatches = query.TryGetValue("state", out var actualState) &&
                FixedTimeTextEquals(actualState, expectedState);
            var success = targetMatches && stateMatches &&
                query.TryGetValue("code", out code) &&
                !string.IsNullOrWhiteSpace(code) && !query.ContainsKey("error");
            if (success)
            {
                await TryWriteAuthorizationResponseAsync(stream, true, token).ConfigureAwait(false);
                return code;
            }

            await TryWriteAuthorizationResponseAsync(stream, false, token).ConfigureAwait(false);
            if (targetMatches && stateMatches && query.ContainsKey("error"))
                throw new ProductionEnrollmentException(
                    "ACCOUNT_AUTHORIZATION_REJECTED",
                    "The OIDC authorization response was rejected.");
            return null;
        }

        internal static async Task<string> ReceiveAuthorizationCodeAsync(
            TcpListener listener,
            Uri expectedRedirect,
            string expectedState,
            CancellationToken token)
        {
            if (listener == null) throw new ArgumentNullException(nameof(listener));
            if (expectedRedirect == null) throw new ArgumentNullException(nameof(expectedRedirect));
            if (string.IsNullOrWhiteSpace(expectedState)) throw new ArgumentException("OIDC callback state is required.", nameof(expectedState));

            for (var attempt = 0; attempt < MaximumAuthorizationCallbackConnections; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var connection = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectionTimeout.CancelAfter(AuthorizationCallbackConnectionTimeout);
                try
                {
                    var code = await ReadAuthorizationResponseAsync(
                        connection,
                        expectedRedirect,
                        expectedState,
                        connectionTimeout.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(code)) return code;
                }
                catch (ProductionEnrollmentException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // A speculative connection sent no complete HTTP request.
                    // Close it and keep waiting for the real browser redirect.
                }
                catch (Exception error) when (
                    error is IOException or SocketException or InvalidDataException or UriFormatException)
                {
                    // Invalid loopback traffic is untrusted local noise. It must
                    // never terminate the authorization attempt before the
                    // state-bound OAuth callback arrives.
                }
            }

            throw new ProductionEnrollmentException(
                "ACCOUNT_AUTHORIZATION_REJECTED",
                "Too many invalid local browser callback connections were received.");
        }

        private static async Task TryWriteAuthorizationResponseAsync(
            Stream stream,
            bool success,
            CancellationToken token)
        {
            var html = Encoding.UTF8.GetBytes(success
                ? "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>TermLink 授权完成 / Authorization complete</title><body><h1>授权已返回 TermLink</h1><p>可以关闭此页面并返回 Windows 客户端。 / You can close this page and return to the Windows client.</p></body></html>"
                : "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>TermLink 授权未完成 / Authorization incomplete</title><body><h1>此回调无效</h1><p>TermLink 将继续等待有效的网页登录回调。 / TermLink is still waiting for a valid web authorization callback.</p></body></html>");
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + (success ? "200 OK" : "400 Bad Request") + "\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Content-Security-Policy: default-src 'none'\r\n" +
                "Referrer-Policy: no-referrer\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Connection: close\r\n" +
                "Content-Length: " + html.Length + "\r\n\r\n");
            try
            {
                await stream.WriteAsync(headers, token).ConfigureAwait(false);
                await stream.WriteAsync(html, token).ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The authorization code is already authenticated by its
                // loopback endpoint and state. A browser closing the socket
                // before it reads the friendly HTML must not discard that code.
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
                if (!result.TryAdd(name, value)) throw new InvalidDataException("OIDC callback query contains duplicate fields.");
            }
            return result;
        }

        private static bool FixedTimeTextEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
            var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
            try
            {
                return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }

        private static async Task<string> FormAsync(Dictionary<string, string> values)
        {
            using var form = new FormUrlEncodedContent(values);
            return await form.ReadAsStringAsync().ConfigureAwait(false);
        }

        private static string Base64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
