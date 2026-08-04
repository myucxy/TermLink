using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    internal static class ProductionEnrollment
    {
        private const string AuthorizationScopes = "openid offline_access";

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

        public static int Run(ProductionEnrollmentOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            RunAsync(options).GetAwaiter().GetResult();
            Console.WriteLine("TermLink account linked.");
            return 0;
        }

        private static async Task RunAsync(ProductionEnrollmentOptions options)
        {
            using var http = ProductionTls.CreateHttpClient(options.ServerUrl);
            http.Timeout = TimeSpan.FromSeconds(20);
            var client = await GetJsonAsync<ClientConfiguration>(
                http,
                options.ServerUrl + "/v1/client-config",
                CancellationToken.None).ConfigureAwait(false);
            ValidateClientConfiguration(client);
            var discovery = await GetJsonAsync<OIDCConfiguration>(
                http,
                client.OIDCIssuer.TrimEnd('/') + "/.well-known/openid-configuration",
                CancellationToken.None).ConfigureAwait(false);
            if (!IsHttps(discovery.AuthorizationEndpoint) || !IsHttps(discovery.TokenEndpoint))
                throw new InvalidDataException("OIDC discovery endpoints are invalid.");

            var redirect = new Uri(client.RedirectUri);
            using var listener = new TcpListener(IPAddress.Loopback, redirect.Port);
            listener.Start(1);

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
            using var browser = Process.Start(new ProcessStartInfo
            {
                FileName = authorizationUrl,
                UseShellExecute = true
            });
            if (browser == null) throw new InvalidOperationException("The system browser could not be opened.");

            string code;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (var connection = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false))
            {
                code = await ReadAuthorizationResponseAsync(
                    connection,
                    redirect.AbsolutePath,
                    state,
                    timeout.Token).ConfigureAwait(false);
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
            using var tokenResponse = await http.SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!tokenResponse.IsSuccessStatusCode)
                throw new InvalidOperationException("OIDC authorization code exchange failed.");
            var tokens = await ReadJsonAsync<TokenResponse>(tokenResponse, CancellationToken.None).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken) || tokens.ExpiresIn < 30)
                throw new InvalidDataException("OIDC token response is incomplete.");

            var existingConfig = ProductionAgentConfig.TryLoad();
            if (existingConfig != null && !string.Equals(
                existingConfig.ServerUrl,
                options.ServerUrl,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The existing TermLink device is linked to a different server.");
            }

            var existingCredentials = ProductionCredentials.TryLoad();
            DeviceIdentity identity = null;
            try
            {
                identity = existingCredentials == null ? DeviceIdentity.Generate() : existingCredentials.Identity;
                var deviceId = existingCredentials == null ? Guid.NewGuid() : existingCredentials.DeviceId;
                var expiresAt = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn);
                ProductionCredentials.SaveWithRefreshToken(
                    deviceId,
                    identity,
                    tokens.AccessToken,
                    tokens.RefreshToken,
                    expiresAt);
                ProductionAgentConfig.Save(new ProductionAgentConfig
                {
                    ServerUrl = options.ServerUrl,
                    PcId = existingConfig == null ? Guid.NewGuid() : existingConfig.PcId,
                    PcName = existingConfig == null ? options.PcName : existingConfig.PcName,
                    AgentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                    KeyEpoch = existingConfig == null ? 1 : existingConfig.KeyEpoch,
                    OIDCIssuer = client.OIDCIssuer,
                    OIDCClientId = client.ClientId
                });
            }
            finally
            {
                if (existingCredentials != null) existingCredentials.Dispose();
                else if (identity != null) identity.Dispose();
                verifier = null;
                state = null;
                code = null;
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
            if (!success) throw new InvalidOperationException("OIDC authorization response was rejected.");
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
