using System;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;

namespace TermLink.Agent
{
    internal static class ProductionTls
    {
        private const string AcceptanceCAEnvironment = "TERMLINK_ACCEPTANCE_CA_FILE";
        private const string AcceptanceHostSuffix = ".127.0.0.1.sslip.io";

        public static HttpClient CreateHttpClient(string serverUrl)
        {
            var handler = new HttpClientHandler();
            var root = LoadAcceptanceRoot(serverUrl);
            if (root != null)
            {
                handler.UseProxy = false;
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

        private static X509Certificate2 LoadAcceptanceRoot(string serverUrl)
        {
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var server) ||
                !server.Host.EndsWith(AcceptanceHostSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var path = Environment.GetEnvironmentVariable(AcceptanceCAEnvironment);
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new InvalidDataException("The TermLink acceptance CA file is unavailable.");
            return new X509Certificate2(path);
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
