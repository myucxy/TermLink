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
        public string AgentVersion { get; set; }
        public long KeyEpoch { get; set; }

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
            return config;
        }
    }

    internal sealed class ProductionCredentials : IDisposable
    {
        private const string EntropyText = "TermLink Windows production credentials V1";
        public Guid DeviceId { get; private set; }
        public DeviceIdentity Identity { get; private set; }
        public string AccessToken { get; private set; }

        private sealed class Stored
        {
            public Guid DeviceId { get; set; }
            public string PrivateMaterial { get; set; }
            public string AccessToken { get; set; }
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
                            AccessToken = stored.AccessToken
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

        public static void Save(Guid deviceId, DeviceIdentity identity, string accessToken)
        {
            if (deviceId == Guid.Empty || identity == null || string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("Credentials are incomplete.");
            var material = identity.ExportPrivateMaterial();
            try
            {
                var stored = new Stored { DeviceId = deviceId, PrivateMaterial = Convert.ToBase64String(material), AccessToken = accessToken };
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

        public void Dispose()
        {
            if (Identity != null) { Identity.Dispose(); Identity = null; }
            AccessToken = null;
        }
    }
}
