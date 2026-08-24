using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace TermLink.Agent
{
    internal static class ProductionTls
    {
        private const string AcceptanceCAEnvironment = "TERMLINK_ACCEPTANCE_CA_FILE";
        private const string AcceptanceHostSuffix = ".127.0.0.1.sslip.io";
        private const string LanAcceptanceHostPrefix = "termlink.";
        private const string SslipHostSuffix = ".sslip.io";
        private const string ProductionRelayHost = "termlink.kget.cc";
        private static readonly TimeSpan WebSocketKeepAliveInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan WebSocketKeepAliveTimeout = TimeSpan.FromSeconds(10);

        public static HttpClient CreateHttpClient(string serverUrl)
        {
            var handler = new HttpClientHandler
            {
                // Metadata, lease, status, and history traffic are independent.
                // A conservative per-origin pool prevents one slow REST request
                // from serializing otherwise unrelated sessions.
                MaxConnectionsPerServer = 16,
                UseCookies = false
            };
            if (ShouldBypassSystemProxy(serverUrl)) handler.UseProxy = false;
            var root = LoadAcceptanceRoot(serverUrl);
            if (root != null)
            {
                handler.ServerCertificateCustomValidationCallback = delegate(
                    HttpRequestMessage request,
                    X509Certificate2 certificate,
                    X509Chain chain,
                    SslPolicyErrors errors)
                {
                    return ValidateWithRoot(root, certificate, chain, errors);
                };
            }
            return new HttpClient(handler, true);
        }

        public static void ConfigureWebSocket(ClientWebSocketOptions options, string serverUrl)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.KeepAliveInterval = WebSocketKeepAliveInterval;
            ConfigureKeepAliveTimeout(options);
            if (ShouldBypassSystemProxy(serverUrl)) options.Proxy = null;
            var root = LoadAcceptanceRoot(serverUrl);
            if (root != null)
            {
                options.RemoteCertificateValidationCallback = delegate(
                    object sender,
                    X509Certificate certificate,
                    X509Chain chain,
                    SslPolicyErrors errors)
                {
                    return ValidateWithRoot(root, certificate, chain, errors);
                };
            }
        }

        private static bool ShouldBypassSystemProxy(string serverUrl)
        {
            var server = ParseServerOrigin(serverUrl);
            // Relay WebSockets must not inherit a machine HTTP proxy that can
            // serve HTTPS pages but reject WSS Upgrade. Scope direct transport
            // to the fixed production Relay and explicit acceptance hosts.
            return string.Equals(
                    server.DnsSafeHost,
                    ProductionRelayHost,
                    StringComparison.OrdinalIgnoreCase) ||
                IsAcceptanceHost(server.DnsSafeHost);
        }

        private static void ConfigureKeepAliveTimeout(ClientWebSocketOptions options)
        {
            // KeepAliveTimeout is exposed by newer runtimes but is absent from
            // the pinned .NET 8 reference surface. Configure it when present;
            // .NET 8 still sends/answers WebSocket control keepalives at the
            // requested interval while the Relay enforces its Pong deadline.
            var property = typeof(ClientWebSocketOptions).GetProperty(
                "KeepAliveTimeout",
                BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite)
                property.SetValue(options, WebSocketKeepAliveTimeout, null);
        }

        private static X509Certificate2 LoadAcceptanceRoot(string serverUrl)
        {
            var server = ParseServerOrigin(serverUrl);
            if (!IsAcceptanceHost(server.DnsSafeHost)) return null;
            var path = Environment.GetEnvironmentVariable(AcceptanceCAEnvironment);
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new InvalidDataException("The TermLink acceptance CA file is unavailable.");
            return new X509Certificate2(path);
        }

        private static Uri ParseServerOrigin(string serverUrl)
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) ||
                server.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(server.Host) ||
                !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Query) ||
                !string.IsNullOrEmpty(server.Fragment) || server.AbsolutePath != "/")
            {
                throw new ArgumentException("The TermLink server URL must be an HTTPS origin.", nameof(serverUrl));
            }
            return server;
        }

        private static bool IsAcceptanceHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (host.Length > AcceptanceHostSuffix.Length &&
                host.EndsWith(AcceptanceHostSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!host.StartsWith(LanAcceptanceHostPrefix, StringComparison.OrdinalIgnoreCase) ||
                !host.EndsWith(SslipHostSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var addressText = host.Substring(
                LanAcceptanceHostPrefix.Length,
                host.Length - LanAcceptanceHostPrefix.Length - SslipHostSuffix.Length);
            var octets = addressText.Split('.');
            if (octets.Length != 4) return false;
            var values = new byte[4];
            for (var index = 0; index < octets.Length; index++)
            {
                var octet = octets[index];
                if (octet.Length == 0 || (octet.Length > 1 && octet[0] == '0')) return false;
                for (var character = 0; character < octet.Length; character++)
                {
                    if (octet[character] < '0' || octet[character] > '9') return false;
                }
                if (!byte.TryParse(octet, out values[index])) return false;
            }
            return values[0] == 10 ||
                (values[0] == 172 && values[1] >= 16 && values[1] <= 31) ||
                (values[0] == 192 && values[1] == 168);
        }

        private static bool ValidateWithRoot(
            X509Certificate2 root,
            X509Certificate certificate,
            X509Chain providedChain,
            SslPolicyErrors errors)
        {
            if (certificate == null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
            using var leaf = new X509Certificate2(certificate);
            using var validation = new X509Chain();
            validation.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            validation.ChainPolicy.CustomTrustStore.Add(root);
            if (providedChain != null)
            {
                for (var index = 1; index < providedChain.ChainElements.Count; index++)
                    validation.ChainPolicy.ExtraStore.Add(providedChain.ChainElements[index].Certificate);
            }
            validation.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            validation.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            return validation.Build(leaf);
        }
    }
}
