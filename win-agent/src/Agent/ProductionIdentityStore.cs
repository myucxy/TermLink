using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TermLink.Protocol;

namespace TermLink.Agent
{
    internal sealed class ProductionRetainedIdentity : IDisposable
    {
        public Guid DeviceId { get; init; }
        public Guid PcId { get; init; }
        public string PcName { get; init; }
        public long KeyEpoch { get; init; }
        public DeviceIdentity Identity { get; init; }

        public void Dispose() => Identity?.Dispose();
    }

    /// <summary>
    /// DPAPI-protected, token-free identity retention. Signing and exchange
    /// keys remain account + machine scoped while OAuth tokens are still
    /// removed immediately at sign-out.
    /// </summary>
    internal static class ProductionIdentityStore
    {
        private const int SchemaVersion = 1;
        private const int MaximumRecords = 32;
        private const string EntropyText = "TermLink Windows retained identities V1";

        private sealed class Document
        {
            public int Version { get; set; } = SchemaVersion;
            public List<Record> Identities { get; set; } = new List<Record>();
        }

        private sealed class Record
        {
            public string AccountKey { get; set; }
            public string OIDCIssuer { get; set; }
            public string MachineId { get; set; }
            public Guid DeviceId { get; set; }
            public Guid PcId { get; set; }
            public string PcName { get; set; }
            public long KeyEpoch { get; set; }
            public string PrivateMaterial { get; set; }
            public long UpdatedAtUnixSeconds { get; set; }
        }

        internal static ProductionRetainedIdentity TryLoad(
            string accountKey,
            string oidcIssuer,
            string machineId)
        {
            try { return TryLoadCore(accountKey, oidcIssuer, machineId); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is CryptographicException || error is InvalidDataException || error is JsonException)
            {
                // Retention is a recovery optimization, not an authentication
                // prerequisite. Enrollment can safely fall back to the
                // account-scoped Server resolve and rotate the device key.
                return null;
            }
        }

        private static ProductionRetainedIdentity TryLoadCore(
            string accountKey,
            string oidcIssuer,
            string machineId)
        {
            accountKey = ProductionCredentials.NormalizeAccountKey(accountKey);
            oidcIssuer = ProductionCredentials.NormalizeIssuer(oidcIssuer);
            if (accountKey == null || oidcIssuer == null || !ProductionMachineIdentity.IsValid(machineId))
                return null;
            var document = LoadDocument();
            var record = document.Identities
                .Where(value => RecordKeyMatches(value, accountKey, oidcIssuer, machineId))
                .OrderByDescending(value => value.UpdatedAtUnixSeconds)
                .FirstOrDefault();
            if (record == null) return null;
            ValidateRecord(record);
            byte[] material;
            try { material = Convert.FromBase64String(record.PrivateMaterial); }
            catch (FormatException error) { throw new InvalidDataException("A retained TermLink identity is invalid.", error); }
            try
            {
                if (material.Length != 64) throw new InvalidDataException("A retained TermLink identity is incomplete.");
                var signing = new byte[32];
                var exchange = new byte[32];
                try
                {
                    Buffer.BlockCopy(material, 0, signing, 0, 32);
                    Buffer.BlockCopy(material, 32, exchange, 0, 32);
                    return new ProductionRetainedIdentity
                    {
                        DeviceId = record.DeviceId,
                        PcId = record.PcId,
                        PcName = record.PcName,
                        KeyEpoch = record.KeyEpoch,
                        Identity = new DeviceIdentity(signing, exchange)
                    };
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(signing);
                    CryptographicOperations.ZeroMemory(exchange);
                }
            }
            finally { CryptographicOperations.ZeroMemory(material); }
        }

        internal static void Save(ProductionAgentConfig config, ProductionCredentials credentials)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (credentials == null) throw new ArgumentNullException(nameof(credentials));
            Save(config, credentials.DeviceId, credentials.Identity, credentials.AccountKey);
        }

        internal static bool TrySave(ProductionAgentConfig config, ProductionCredentials credentials)
        {
            if (config == null || credentials == null) return false;
            return TrySave(config, credentials.DeviceId, credentials.Identity, credentials.AccountKey);
        }

        internal static bool TrySave(
            ProductionAgentConfig config,
            Guid deviceId,
            DeviceIdentity identity,
            string credentialAccountKey)
        {
            try
            {
                Save(config, deviceId, identity, credentialAccountKey);
                return true;
            }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is UnauthorizedAccessException ||
                error is CryptographicException || error is InvalidDataException || error is JsonException)
            {
                return false;
            }
        }

        internal static void Save(
            ProductionAgentConfig config,
            Guid deviceId,
            DeviceIdentity identity,
            string credentialAccountKey)
        {
            if (config == null || deviceId == Guid.Empty || identity == null || config.PcId == Guid.Empty ||
                string.IsNullOrWhiteSpace(config.PcName) || config.KeyEpoch < 1 ||
                !ProductionCredentials.CanReuseIdentity(config.AccountKey, credentialAccountKey) ||
                ProductionCredentials.NormalizeIssuer(config.OIDCIssuer) == null ||
                !ProductionMachineIdentity.IsValid(config.MachineId))
            {
                throw new ArgumentException("The retained TermLink identity is incomplete.");
            }

            var document = LoadDocumentForSave();
            document.Identities.RemoveAll(value => RecordKeyMatches(
                value, config.AccountKey, config.OIDCIssuer, config.MachineId));
            var material = identity.ExportPrivateMaterial();
            try
            {
                document.Identities.Add(new Record
                {
                    AccountKey = config.AccountKey,
                    OIDCIssuer = ProductionCredentials.NormalizeIssuer(config.OIDCIssuer),
                    MachineId = config.MachineId,
                    DeviceId = deviceId,
                    PcId = config.PcId,
                    PcName = config.PcName,
                    KeyEpoch = config.KeyEpoch,
                    PrivateMaterial = Convert.ToBase64String(material),
                    UpdatedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
                document.Identities = document.Identities
                    .OrderByDescending(value => value.UpdatedAtUnixSeconds)
                    .Take(MaximumRecords)
                    .ToList();
                SaveDocument(document);
            }
            finally { CryptographicOperations.ZeroMemory(material); }
        }

        private static bool RecordKeyMatches(Record value, string accountKey, string issuer, string machineId) =>
            value != null &&
            ProductionCredentials.CanReuseIdentity(value.AccountKey, accountKey) &&
            string.Equals(
                ProductionCredentials.NormalizeIssuer(value.OIDCIssuer),
                ProductionCredentials.NormalizeIssuer(issuer),
                StringComparison.Ordinal) &&
            string.Equals(value.MachineId, machineId, StringComparison.Ordinal);

        private static void ValidateRecord(Record value)
        {
            if (value.DeviceId == Guid.Empty || value.PcId == Guid.Empty ||
                string.IsNullOrWhiteSpace(value.PcName) || value.PcName.Length > 128 || value.KeyEpoch < 1 ||
                string.IsNullOrWhiteSpace(value.PrivateMaterial))
            {
                throw new InvalidDataException("A retained TermLink identity is incomplete.");
            }
        }

        private static Document LoadDocument()
        {
            var path = ProductionAccountStorage.IdentityStorePath;
            if (!File.Exists(path)) return new Document();
            byte[] protectedBytes = null;
            byte[] entropy = null;
            byte[] plain = null;
            try
            {
                protectedBytes = File.ReadAllBytes(path);
                entropy = Encoding.UTF8.GetBytes(EntropyText);
                plain = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
                var document = JsonSerializer.Deserialize<Document>(plain);
                if (document == null || document.Version != SchemaVersion || document.Identities == null ||
                    document.Identities.Count > MaximumRecords)
                    throw new InvalidDataException("The retained TermLink identity store is invalid.");
                return document;
            }
            finally
            {
                if (plain != null) CryptographicOperations.ZeroMemory(plain);
                if (entropy != null) CryptographicOperations.ZeroMemory(entropy);
                if (protectedBytes != null) CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        private static Document LoadDocumentForSave()
        {
            try { return LoadDocument(); }
            catch (Exception error) when (error is CryptographicException ||
                error is InvalidDataException || error is JsonException)
            {
                // A corrupt or foreign-user DPAPI blob cannot be recovered.
                // Replace it atomically with the newly authenticated identity;
                // OAuth tokens are never stored in this file.
                return new Document();
            }
        }

        private static void SaveDocument(Document document)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(document);
            try
            {
                var entropy = Encoding.UTF8.GetBytes(EntropyText);
                try
                {
                    var protectedBytes = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
                    try
                    {
                        var path = ProductionAccountStorage.IdentityStorePath;
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        InputDeduplicator.AtomicWrite(path, protectedBytes);
                    }
                    finally { CryptographicOperations.ZeroMemory(protectedBytes); }
                }
                finally { CryptographicOperations.ZeroMemory(entropy); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }
}
