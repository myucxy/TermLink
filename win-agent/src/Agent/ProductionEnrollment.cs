using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
                server.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(server.Query) ||
                !string.IsNullOrEmpty(server.Fragment))
            {
                throw new ArgumentException("--server-url must be an HTTPS TermLink origin.");
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
        public const string PublicServerUrl = "https://termlink.kget.cc:8910";
        private static readonly TimeSpan AuthorizationCallbackTimeout = TimeSpan.FromMinutes(5);
        private static readonly object BrowserAuthorizationSync = new object();
        private static BrowserAuthorizationAttempt activeBrowserAuthorization;

        private sealed class BrowserAuthorizationAttempt
        {
            public ProductionEnrollmentOptions Options { get; set; }
            public string AuthorizationUrl;
            public Task Completion { get; set; }
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

        private sealed class TokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; }

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; }

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }
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
                if (ReferenceEquals(activeBrowserAuthorization, attempt)) activeBrowserAuthorization = null;
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
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
                throw new ArgumentException("The TermLink server origin is invalid.");
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
            if (!IsHttps(discovery.TokenEndpoint)) throw new InvalidDataException("OIDC token endpoint is invalid.");
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
            PersistTokens(serverUrl.TrimEnd('/'), pcName, client, tokens);
        }

        private static void ValidateClientConfigurationForDirect(ClientConfiguration client)
        {
            if (client == null || !IsHttps(client.OIDCIssuer) || string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.RedirectUri))
                throw new InvalidDataException("TermLink client configuration is invalid.");
        }

        private static void PersistTokens(string serverUrl, string pcName, ClientConfiguration client, TokenResponse tokens)
        {
            var existingConfig = ProductionAgentConfig.TryLoad();
            if (existingConfig != null && !string.Equals(existingConfig.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The existing TermLink device is linked to a different server.");
            using var existingCredentials = ProductionCredentials.TryLoad();
            var account = ProductionCredentials.ReadAccountClaims(tokens.AccessToken);
            var accountKey = ProductionCredentials.ReadAccountKey(tokens.AccessToken, client.OIDCIssuer);
            if (string.IsNullOrWhiteSpace(accountKey))
                throw new InvalidDataException("The OIDC access token does not identify a stable issuer and subject.");
            var selection = SelectIdentity(
                existingCredentials?.AccountKey,
                existingCredentials == null ? Guid.Empty : existingCredentials.DeviceId,
                existingConfig?.AccountKey,
                existingConfig == null ? Guid.Empty : existingConfig.PcId,
                accountKey);
            DeviceIdentity identity = null;
            try
            {
                identity = selection.ReuseDeviceIdentity ? existingCredentials.Identity : DeviceIdentity.Generate();
                ProductionCredentials.SaveWithRefreshToken(
                    selection.DeviceId,
                    identity,
                    accountKey,
                    tokens.AccessToken,
                    tokens.RefreshToken,
                    DateTime.UtcNow.AddSeconds(tokens.ExpiresIn));
                ProductionAgentConfig.Save(new ProductionAgentConfig
                {
                    ServerUrl = serverUrl,
                    PcId = selection.PcId,
                    PcName = selection.ReusePcIdentity && existingConfig != null
                        ? existingConfig.PcName
                        : NormalizePcName(pcName),
                    AccountKey = accountKey,
                    AccountUsername = string.IsNullOrWhiteSpace(account.Username) && selection.ReusePcIdentity ? existingConfig?.AccountUsername : account.Username,
                    AccountNickname = string.IsNullOrWhiteSpace(account.Nickname) && selection.ReusePcIdentity ? existingConfig?.AccountNickname : account.Nickname,
                    AccountEmail = string.IsNullOrWhiteSpace(account.Email) && selection.ReusePcIdentity ? existingConfig?.AccountEmail : account.Email,
                    AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    KeyEpoch = selection.ReusePcIdentity && existingConfig != null ? existingConfig.KeyEpoch : 1,
                    OIDCIssuer = client.OIDCIssuer,
                    OIDCClientId = client.ClientId
                });
            }
            finally
            {
                if (!selection.ReuseDeviceIdentity) identity?.Dispose();
            }
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
            using var http = ProductionTls.CreateHttpClient(options.ServerUrl);
            http.Timeout = TimeSpan.FromSeconds(20);
            ClientConfiguration client;
            try
            {
                client = await GetJsonAsync<ClientConfiguration>(
                    http,
                    options.ServerUrl + "/v1/client-config",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
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
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
            {
                throw new ProductionEnrollmentException(
                    "ACCOUNT_IDENTITY_SERVICE_TIMEOUT",
                    "The OIDC discovery request timed out.",
                    error);
            }
            if (!IsHttps(discovery.AuthorizationEndpoint) || !IsHttps(discovery.TokenEndpoint))
                throw new InvalidDataException("OIDC discovery endpoints are invalid.");

            var redirect = new Uri(client.RedirectUri);
            using var listener = StartCallbackListener(redirect);

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
                ["redirect_uri"] = client.RedirectUri,
                ["scope"] = AuthorizationScopes,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
            }).ConfigureAwait(false);
            Volatile.Write(ref attempt.AuthorizationUrl, authorizationUrl);
            OpenSystemBrowser(authorizationUrl);

            string code;
            using (var timeout = new CancellationTokenSource(AuthorizationCallbackTimeout))
            {
                try
                {
                    using var connection = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                    code = await ReadAuthorizationResponseAsync(
                        connection,
                        redirect.AbsolutePath,
                        state,
                        timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
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
                    ["redirect_uri"] = client.RedirectUri,
                    ["code"] = code,
                    ["code_verifier"] = verifier
                })
            };
            HttpResponseMessage tokenResponse;
            try
            {
                tokenResponse = await http.SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
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
                var tokens = await ReadJsonAsync<TokenResponse>(tokenResponse, CancellationToken.None).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken) || tokens.ExpiresIn < 30)
                    throw new InvalidDataException("OIDC token response is incomplete.");

                try
                {
                    PersistTokens(options.ServerUrl, options.PcName, client, tokens);
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
                redirect.Scheme != Uri.UriSchemeHttp || redirect.Host != "127.0.0.1" ||
                redirect.Port < 1024 || redirect.AbsolutePath != "/oauth2/callback" ||
                !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment))
            {
                throw new InvalidDataException("Windows OIDC client configuration is invalid.");
            }
        }

        private static bool IsHttps(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
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

        internal static TcpListener StartCallbackListener(Uri redirect)
        {
            if (redirect == null) throw new ArgumentNullException(nameof(redirect));
            if (redirect.Scheme != Uri.UriSchemeHttp || redirect.Host != "127.0.0.1" || redirect.Port < 1024)
                throw new ArgumentException("The browser callback URI is invalid.", nameof(redirect));
            var listener = new TcpListener(IPAddress.Loopback, redirect.Port);
            listener.Server.ExclusiveAddressUse = true;
            try
            {
                listener.Start(1);
                return listener;
            }
            catch (SocketException error) when (
                error.SocketErrorCode == SocketError.AddressAlreadyInUse ||
                error.SocketErrorCode == SocketError.AccessDenied)
            {
                listener.Stop();
                throw new ProductionEnrollmentException(
                    "ACCOUNT_CALLBACK_PORT_IN_USE",
                    "The local browser callback port is already in use.",
                    error);
            }
            catch
            {
                listener.Stop();
                throw;
            }
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
            string expectedPath,
            string expectedState,
            CancellationToken token)
        {
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            var requestLine = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(requestLine) || requestLine.Length > 8192)
                throw new InvalidDataException("OIDC callback request is invalid.");
            for (var count = 0; count < 100; count++)
            {
                var header = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (header == null || header.Length == 0) break;
                if (header.Length > 8192 || count == 99) throw new InvalidDataException("OIDC callback headers are invalid.");
            }
            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || parts[0] != "GET") throw new InvalidDataException("OIDC callback method is invalid.");
            var target = new Uri("http://127.0.0.1" + parts[1]);
            var query = ParseQuery(target.Query);
            string code = null;
            var success = target.AbsolutePath == expectedPath &&
                query.TryGetValue("state", out var actualState) &&
                FixedTimeTextEquals(actualState, expectedState) &&
                query.TryGetValue("code", out code) &&
                !string.IsNullOrWhiteSpace(code) && !query.ContainsKey("error");
            var html = Encoding.UTF8.GetBytes(success
                ? "<!doctype html><title>TermLink linked</title><p>TermLink authorization returned. You can close this window.</p>"
                : "<!doctype html><title>TermLink error</title><p>TermLink authorization could not be completed.</p>");
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + (success ? "200 OK" : "400 Bad Request") + "\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Content-Security-Policy: default-src 'none'\r\n" +
                "Connection: close\r\n" +
                "Content-Length: " + html.Length + "\r\n\r\n");
            await stream.WriteAsync(headers, token).ConfigureAwait(false);
            await stream.WriteAsync(html, token).ConfigureAwait(false);
            if (!success)
                throw new ProductionEnrollmentException(
                    "ACCOUNT_AUTHORIZATION_REJECTED",
                    "The OIDC authorization response was rejected.");
            return code;
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
