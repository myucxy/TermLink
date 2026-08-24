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
        // A non-display, one-way account binding derived from the OIDC issuer
        // and subject. Usernames and email addresses are mutable labels and
        // must never decide whether a device identity can be reused.
        public string AccountKey { get; set; }
        public string AccountUsername { get; set; }
        public string AccountEmail { get; set; }
        public string AgentVersion { get; set; }
        public long KeyEpoch { get; set; }
        public string OIDCIssuer { get; set; }
        public string OIDCClientId { get; set; }

        public static string DefaultPath
        {
            get { return ProductionAccountStorage.ConfigPath; }
        }

        public static ProductionAgentConfig TryLoad()
        {
            var path = DefaultPath;
            if (!File.Exists(path)) return null;
            var config = JsonSerializer.Deserialize<ProductionAgentConfig>(File.ReadAllText(path, Encoding.UTF8));
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
                var path = DefaultPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                InputDeduplicator.AtomicWrite(path, bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal sealed class ProductionCredentials : IDisposable
    {
        private const string EntropyText = "TermLink Windows production credentials V1";
        public Guid DeviceId { get; private set; }
        public DeviceIdentity Identity { get; private set; }
        public string AccountKey { get; private set; }
        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public DateTime AccessTokenExpiresAtUtc { get; private set; }
        private readonly object tokenSync = new object();

        private sealed class Stored
        {
            public Guid DeviceId { get; set; }
            public string PrivateMaterial { get; set; }
            public string AccountKey { get; set; }
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public long AccessTokenExpiresAtUnixSeconds { get; set; }
        }

        public static string DefaultPath
        {
            get { return ProductionAccountStorage.CredentialsPath; }
        }

        public static ProductionCredentials TryLoad()
        {
            var path = DefaultPath;
            if (!File.Exists(path)) return null;
            byte[] protectedBytes = null;
            byte[] entropy = null;
            byte[] plain = null;
            try
            {
                protectedBytes = File.ReadAllBytes(path);
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
                var tokenAccountKey = ReadAccountKey(stored.AccessToken, null);
                var storedAccountKey = NormalizeAccountKey(stored.AccountKey);
                if (!string.IsNullOrWhiteSpace(storedAccountKey) &&
                    !string.IsNullOrWhiteSpace(tokenAccountKey) &&
                    !string.Equals(storedAccountKey, tokenAccountKey, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Production credentials have an inconsistent account binding.");
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
                            AccountKey = storedAccountKey ?? tokenAccountKey,
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

        /// <summary>
        /// Returns a stable, non-display account key bound to the OIDC issuer
        /// and subject. The key intentionally excludes username and email.
        /// </summary>
        internal static string ReadAccountKey(string accessToken, string expectedIssuer)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            try
            {
                var parts = accessToken.Split('.');
                if (parts.Length != 3) return null;
                var encoded = parts[1].Replace('-', '+').Replace('_', '/');
                encoded += new string('=', (4 - encoded.Length % 4) % 4);
                var payload = Convert.FromBase64String(encoded);
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (!root.TryGetProperty("iss", out var issuerValue) ||
                    !root.TryGetProperty("sub", out var subjectValue))
                {
                    return null;
                }
                var issuer = NormalizeIssuer(issuerValue.GetString());
                var subject = NormalizeSubject(subjectValue.GetString());
                if (issuer == null || subject == null) return null;
                if (!string.IsNullOrWhiteSpace(expectedIssuer) &&
                    !string.Equals(issuer, NormalizeIssuer(expectedIssuer), StringComparison.Ordinal))
                {
                    return null;
                }

                var binding = Encoding.UTF8.GetBytes(issuer + "\n" + subject);
                try
                {
                    var digest = SHA256.HashData(binding);
                    try { return "oidc-sha256:" + Convert.ToHexString(digest).ToLowerInvariant(); }
                    finally { CryptographicOperations.ZeroMemory(digest); }
                }
                finally { CryptographicOperations.ZeroMemory(binding); }
            }
            catch (Exception error) when (error is FormatException || error is JsonException ||
                error is ArgumentException || error is InvalidOperationException || error is UriFormatException)
            {
                return null;
            }
        }

        internal static bool CanReuseIdentity(string existingAccountKey, string incomingAccountKey)
        {
            existingAccountKey = NormalizeAccountKey(existingAccountKey);
            incomingAccountKey = NormalizeAccountKey(incomingAccountKey);
            return existingAccountKey != null && incomingAccountKey != null &&
                string.Equals(existingAccountKey, incomingAccountKey, StringComparison.Ordinal);
        }

        private static string NormalizeIssuer(string value)
        {
            value = (value ?? string.Empty).Trim().TrimEnd('/');
            if (!Uri.TryCreate(value, UriKind.Absolute, out var issuer) ||
                issuer.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(issuer.Fragment) ||
                !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.UserInfo))
            {
                return null;
            }
            return issuer.AbsoluteUri.TrimEnd('/');
        }

        private static string NormalizeSubject(string value)
        {
            value = value ?? string.Empty;
            if (value.Length < 1 || value.Length > 1024) return null;
            foreach (var character in value)
            {
                if (char.IsControl(character)) return null;
            }
            return value;
        }

        private static string NormalizeAccountKey(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length != 76 || !value.StartsWith("oidc-sha256:", StringComparison.Ordinal)) return null;
            for (var index = "oidc-sha256:".Length; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return null;
            }
            return value;
        }

        private static string NormalizeClaim(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length > 256 ? value.Substring(0, 256) : value;
        }

        public static void Save(Guid deviceId, DeviceIdentity identity, string accessToken)
        {
            SaveCore(deviceId, identity, null, accessToken, null, DateTime.MaxValue);
        }

        public static void SaveWithRefreshToken(
            Guid deviceId,
            DeviceIdentity identity,
            string accessToken,
            string refreshToken,
            DateTime accessTokenExpiresAtUtc)
        {
            SaveCore(
                deviceId,
                identity,
                ReadAccountKey(accessToken, null),
                accessToken,
                refreshToken,
                accessTokenExpiresAtUtc);
        }

        internal static void SaveWithRefreshToken(
            Guid deviceId,
            DeviceIdentity identity,
            string accountKey,
            string accessToken,
            string refreshToken,
            DateTime accessTokenExpiresAtUtc)
        {
            SaveCore(deviceId, identity, accountKey, accessToken, refreshToken, accessTokenExpiresAtUtc);
        }

        private static void SaveCore(
            Guid deviceId,
            DeviceIdentity identity,
            string accountKey,
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
            accountKey = NormalizeAccountKey(accountKey);
            var material = identity.ExportPrivateMaterial();
            try
            {
                var stored = new Stored
                {
                    DeviceId = deviceId,
                    PrivateMaterial = Convert.ToBase64String(material),
                    AccountKey = accountKey,
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
                            var path = DefaultPath;
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            InputDeduplicator.AtomicWrite(path, protectedBytes);
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
            var incomingAccountKey = ReadAccountKey(accessToken, null);
            if (AccountKey != null && incomingAccountKey != null &&
                !CanReuseIdentity(AccountKey, incomingAccountKey))
            {
                throw new InvalidDataException("Refreshed credentials changed the OIDC account subject.");
            }
            var resolvedAccountKey = AccountKey ?? incomingAccountKey;
            SaveCore(DeviceId, Identity, resolvedAccountKey, accessToken, refreshToken, accessTokenExpiresAtUtc);
            lock (tokenSync)
            {
                AccountKey = resolvedAccountKey;
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

    internal static class ProductionAccountStorage
    {
        private const string AcceptanceCAEnvironment = "TERMLINK_ACCEPTANCE_CA_FILE";
        private const string AcceptanceRootEnvironment = "TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT";
        private const string ConfigFileName = "production-config.json";
        private const string CredentialsFileName = "production-credentials.dpapi";

        internal static string ConfigPath
        {
            get { return GetKnownFilePath(ResolveLocalApplicationDataRoot(), ConfigFileName); }
        }

        internal static string CredentialsPath
        {
            get { return GetKnownFilePath(ResolveLocalApplicationDataRoot(), CredentialsFileName); }
        }

        internal static void ForgetLocalAccount()
        {
            ForgetLocalAccount(ResolveLocalApplicationDataRoot());
        }

        internal static string ResolveLocalApplicationDataRoot()
        {
            var acceptanceRoot = Environment.GetEnvironmentVariable(AcceptanceRootEnvironment);
            if (string.IsNullOrWhiteSpace(acceptanceRoot))
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AcceptanceCAEnvironment)))
            {
                throw new InvalidOperationException(
                    AcceptanceRootEnvironment + " is allowed only when " + AcceptanceCAEnvironment + " is also set.");
            }

            try
            {
                if (!Path.IsPathFullyQualified(acceptanceRoot))
                    throw new InvalidOperationException(AcceptanceRootEnvironment + " must be an absolute path.");
                var fullPath = Path.GetFullPath(acceptanceRoot);
                var pathRoot = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(pathRoot) ||
                    string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(AcceptanceRootEnvironment + " cannot be a drive or share root.");
                }
                return fullPath;
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
            {
                throw new InvalidOperationException(AcceptanceRootEnvironment + " is not a valid absolute path.", error);
            }
        }

        // The root parameter makes the path contract independently testable.
        // Every invocation still resolves only <root>\TermLink and the two exact
        // known files; directories and unrelated TermLink data are never removed.
        internal static void ForgetLocalAccount(string localApplicationDataRoot)
        {
            if (string.IsNullOrWhiteSpace(localApplicationDataRoot))
                throw new InvalidOperationException("The local application data directory is unavailable.");
            var localRoot = Path.GetFullPath(localApplicationDataRoot);
            var termLinkRoot = Path.GetFullPath(Path.Combine(localRoot, "TermLink"));
            if (!string.Equals(
                Path.GetRelativePath(localRoot, termLinkRoot),
                "TermLink",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The TermLink account data path is outside LocalApplicationData.");
            if (Directory.Exists(termLinkRoot) &&
                (File.GetAttributes(termLinkRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The TermLink account data directory cannot be a reparse point.");
            }

            Exception firstError = null;
            foreach (var fileName in new[] { CredentialsFileName, ConfigFileName })
            {
                var path = Path.GetFullPath(Path.Combine(termLinkRoot, fileName));
                if (!string.Equals(Path.GetDirectoryName(path), termLinkRoot, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A TermLink account file path escaped its known location.");
                }
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    if (firstError == null) firstError = error;
                }
            }
            if (firstError != null)
                throw new IOException("One or more local TermLink account files could not be removed.", firstError);
        }

        private static string GetKnownFilePath(string localApplicationDataRoot, string fileName)
        {
            var termLinkRoot = Path.GetFullPath(Path.Combine(localApplicationDataRoot, "TermLink"));
            var path = Path.GetFullPath(Path.Combine(termLinkRoot, fileName));
            if (!string.Equals(Path.GetDirectoryName(path), termLinkRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A TermLink account file path escaped its known location.");
            }
            return path;
        }

    }
}
