using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLink.Protocol;

namespace TermLink.Agent
{
    internal sealed class ProductionAgentConfig
    {
        public string ServerUrl { get; set; }
        public Guid PcId { get; set; }
        public string PcName { get; set; }
        public string AccountUsername { get; set; }
        public string AccountEmail { get; set; }
        public string AgentVersion { get; set; }
        public long KeyEpoch { get; set; }
        public string OIDCIssuer { get; set; }
        public string OIDCClientId { get; set; }

        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLink", "production-config.json"); }
        }

        public static ProductionAgentConfig TryLoad()
        {
            if (!File.Exists(DefaultPath)) return null;
            var config = JsonSerializer.Deserialize<ProductionAgentConfig>(File.ReadAllText(DefaultPath, Encoding.UTF8));
            if (config == null || !Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                config.PcId == Guid.Empty || string.IsNullOrWhiteSpace(config.PcName) || string.IsNullOrWhiteSpace(config.AgentVersion) || config.KeyEpoch < 1)
            {
                throw new InvalidDataException("Production Agent configuration is invalid.");
            }
            config.ServerUrl = config.ServerUrl.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(config.OIDCIssuer) || !string.IsNullOrWhiteSpace(config.OIDCClientId))
            {
                if (!Uri.TryCreate(config.OIDCIssuer, UriKind.Absolute, out var issuer) ||
                    issuer.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(config.OIDCClientId))
                {
                    throw new InvalidDataException("Production Agent OIDC configuration is invalid.");
                }
                config.OIDCIssuer = config.OIDCIssuer.TrimEnd('/');
            }
            return config;
        }

        public static void Save(ProductionAgentConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttps ||
                !Uri.TryCreate(config.OIDCIssuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps ||
                config.PcId == Guid.Empty || string.IsNullOrWhiteSpace(config.PcName) ||
                string.IsNullOrWhiteSpace(config.AgentVersion) || config.KeyEpoch < 1 ||
                string.IsNullOrWhiteSpace(config.OIDCClientId))
            {
                throw new ArgumentException("Production Agent configuration is incomplete.", nameof(config));
            }
            config.ServerUrl = config.ServerUrl.TrimEnd('/');
            config.OIDCIssuer = config.OIDCIssuer.TrimEnd('/');
            var bytes = JsonSerializer.SerializeToUtf8Bytes(config);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath));
                InputDeduplicator.AtomicWrite(DefaultPath, bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal sealed class ProductionCredentials : IDisposable
    {
        private const string EntropyText = "TermLink Windows production credentials V1";
        public Guid DeviceId { get; private set; }
        public DeviceIdentity Identity { get; private set; }
        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public DateTime AccessTokenExpiresAtUtc { get; private set; }
        private readonly object tokenSync = new object();

        private sealed class Stored
        {
            public Guid DeviceId { get; set; }
            public string PrivateMaterial { get; set; }
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public long AccessTokenExpiresAtUnixSeconds { get; set; }
        }

        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TermLink", "production-credentials.dpapi"); }
        }

        public static ProductionCredentials TryLoad()
        {
            if (!File.Exists(DefaultPath)) return null;
            byte[] protectedBytes = null;
            byte[] entropy = null;
            byte[] plain = null;
            try
            {
                protectedBytes = File.ReadAllBytes(DefaultPath);
                entropy = Encoding.UTF8.GetBytes(EntropyText);
                plain = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
                var stored = JsonSerializer.Deserialize<Stored>(plain);
                if (stored == null || stored.DeviceId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(stored.PrivateMaterial) ||
                    string.IsNullOrWhiteSpace(stored.AccessToken))
                {
                    throw new InvalidDataException("Production credentials are incomplete.");
                }

                byte[] privateMaterial;
                try
                {
                    privateMaterial = Convert.FromBase64String(stored.PrivateMaterial);
                }
                catch (FormatException error)
                {
                    throw new InvalidDataException("Production credentials are invalid.", error);
                }
                try
                {
                    if (privateMaterial.Length != 64)
                    {
                        throw new InvalidDataException("Production credentials are incomplete.");
                    }
                    var signing = new byte[32];
                    var exchange = new byte[32];
                    try
                    {
                        Buffer.BlockCopy(privateMaterial, 0, signing, 0, 32);
                        Buffer.BlockCopy(privateMaterial, 32, exchange, 0, 32);
                        return new ProductionCredentials
                        {
                            DeviceId = stored.DeviceId,
                            Identity = new DeviceIdentity(signing, exchange),
                            AccessToken = stored.AccessToken,
                            RefreshToken = stored.RefreshToken,
                            AccessTokenExpiresAtUtc = string.IsNullOrWhiteSpace(stored.RefreshToken)
                                ? DateTime.MaxValue
                                : DateTimeOffset.FromUnixTimeSeconds(stored.AccessTokenExpiresAtUnixSeconds).UtcDateTime
                        };
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(signing);
                        CryptographicOperations.ZeroMemory(exchange);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(privateMaterial); }
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
                if (entropy != null) CryptographicOperations.ZeroMemory(entropy);
                if (protectedBytes != null) CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        // These claims are display-only account labels. They never participate
        // in device authorization or Relay authentication decisions.
        internal static (string Username, string Email) ReadAccountClaims(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return (null, null);
            try
            {
                var parts = accessToken.Split('.');
                if (parts.Length != 3) return (null, null);
                var encoded = parts[1].Replace('-', '+').Replace('_', '/');
                encoded += new string('=', (4 - encoded.Length % 4) % 4);
                var payload = Convert.FromBase64String(encoded);
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var username = root.TryGetProperty("preferred_username", out var preferred) ? preferred.GetString() : null;
                if (string.IsNullOrWhiteSpace(username) && root.TryGetProperty("username", out var rawUsername)) username = rawUsername.GetString();
                var email = root.TryGetProperty("email", out var emailValue) ? emailValue.GetString() : null;
                return (NormalizeClaim(username), NormalizeClaim(email));
            }
            catch (Exception error) when (error is FormatException || error is JsonException || error is ArgumentException || error is InvalidOperationException)
            {
                return (null, null);
            }
        }

        private static string NormalizeClaim(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length > 256 ? value.Substring(0, 256) : value;
        }

        public static void Save(Guid deviceId, DeviceIdentity identity, string accessToken)
        {
            SaveCore(deviceId, identity, accessToken, null, DateTime.MaxValue);
        }

        public static void SaveWithRefreshToken(
            Guid deviceId,
            DeviceIdentity identity,
            string accessToken,
            string refreshToken,
            DateTime accessTokenExpiresAtUtc)
        {
            SaveCore(deviceId, identity, accessToken, refreshToken, accessTokenExpiresAtUtc);
        }

        private static void SaveCore(
            Guid deviceId,
            DeviceIdentity identity,
            string accessToken,
            string refreshToken,
            DateTime accessTokenExpiresAtUtc)
        {
            if (deviceId == Guid.Empty || identity == null || string.IsNullOrWhiteSpace(accessToken) ||
                (!string.IsNullOrWhiteSpace(refreshToken) &&
                    (accessTokenExpiresAtUtc.Kind != DateTimeKind.Utc || accessTokenExpiresAtUtc <= DateTime.UnixEpoch)))
            {
                throw new ArgumentException("Credentials are incomplete.");
            }
            var material = identity.ExportPrivateMaterial();
            try
            {
                var stored = new Stored
                {
                    DeviceId = deviceId,
                    PrivateMaterial = Convert.ToBase64String(material),
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    AccessTokenExpiresAtUnixSeconds = string.IsNullOrWhiteSpace(refreshToken)
                        ? 0
                        : new DateTimeOffset(accessTokenExpiresAtUtc).ToUnixTimeSeconds()
                };
                var plain = JsonSerializer.SerializeToUtf8Bytes(stored);
                try
                {
                    var entropy = Encoding.UTF8.GetBytes(EntropyText);
                    try
                    {
                        var protectedBytes = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath));
                            InputDeduplicator.AtomicWrite(DefaultPath, protectedBytes);
                        }
                        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
                    }
                    finally { CryptographicOperations.ZeroMemory(entropy); }
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }

        public bool CanRefresh
        {
            get { lock (tokenSync) return !string.IsNullOrWhiteSpace(RefreshToken); }
        }

        public bool ShouldRefresh(DateTime utcNow, TimeSpan skew)
        {
            lock (tokenSync)
            {
                return !string.IsNullOrWhiteSpace(RefreshToken) && AccessTokenExpiresAtUtc <= utcNow.Add(skew);
            }
        }

        public string GetAccessToken()
        {
            lock (tokenSync) return AccessToken;
        }

        public string GetRefreshToken()
        {
            lock (tokenSync) return RefreshToken;
        }

        public void UpdateTokens(string accessToken, string refreshToken, DateTime accessTokenExpiresAtUtc)
        {
            if (string.IsNullOrWhiteSpace(refreshToken)) refreshToken = GetRefreshToken();
            SaveWithRefreshToken(DeviceId, Identity, accessToken, refreshToken, accessTokenExpiresAtUtc);
            lock (tokenSync)
            {
                AccessToken = accessToken;
                RefreshToken = refreshToken;
                AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
            }
        }

        public void Dispose()
        {
            if (Identity != null) { Identity.Dispose(); Identity = null; }
            lock (tokenSync)
            {
                AccessToken = null;
                RefreshToken = null;
                AccessTokenExpiresAtUtc = DateTime.MinValue;
            }
        }
    }
}
