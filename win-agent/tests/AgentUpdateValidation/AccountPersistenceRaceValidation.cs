using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.Protocol;

namespace TermLink.AgentUpdate.Validation
{
    internal static class AccountPersistenceRaceValidation
    {
        internal static void Run()
        {
            const string issuer = "https://identity.example.test";
            var accountKey = "oidc-sha256:" + new string('c', 64);
            var machineId = ProductionMachineIdentity.Derive(
                "00112233-4455-6677-8899-aabbccddeeff", issuer, accountKey);
            var root = Path.Combine(Path.GetTempPath(), "TermLink-account-race-" + Guid.NewGuid().ToString("N"));
            var previousRoot = Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT");
            var previousMarker = Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE");
            Directory.CreateDirectory(root);
            try
            {
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT", root);
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE", Path.Combine(root, "acceptance.cer"));
                using var identity = DeviceIdentity.Generate();
                var deviceId = Guid.NewGuid();
                var pcId = Guid.NewGuid();
                var oldConfig = Config(pcId, "Old PC", "old-name", 4, accountKey, issuer, machineId);
                ProductionAgentConfig.Save(oldConfig);
                ProductionCredentials.SaveWithRefreshToken(deviceId, identity, accountKey,
                    "old-access", "old-refresh", DateTime.UtcNow.AddHours(1));
                using var oldCredentials = ProductionCredentials.TryLoad();
                Check(oldCredentials.LoginGeneration != Guid.Empty,
                    "a new login did not create a credential generation");

                var currentConfig = Config(pcId, "Renamed PC", "new-name", 5, accountKey, issuer, machineId);
                ProductionCredentials.SaveWithRefreshToken(deviceId, identity, accountKey,
                    "new-access", "old-refresh", DateTime.UtcNow.AddHours(1));
                ProductionAgentConfig.Save(currentConfig);
                ProductionIdentityStore.Save(currentConfig, deviceId, identity, accountKey);
                using (var current = ProductionCredentials.TryLoad())
                    Check(current.LoginGeneration != Guid.Empty &&
                        current.LoginGeneration != oldCredentials.LoginGeneration,
                        "same-token login reused the retired login generation");

                oldCredentials.UpdateTokens("late-access", "late-refresh", DateTime.UtcNow.AddHours(1));
                using (var persisted = ProductionCredentials.TryLoad())
                    Check(persisted.GetAccessToken() == "new-access" &&
                        persisted.GetRefreshToken() == "old-refresh",
                        "an old refresh response overwrote a newer login on the same device");

                Retain(oldConfig, oldCredentials);
                using (var retained = ProductionIdentityStore.TryLoad(accountKey, issuer, machineId))
                    Check(retained != null && retained.PcName == "Renamed PC" && retained.KeyEpoch == 5,
                        "an old runtime overwrote the retained identity after a newer login");

                oldConfig.KeyEpoch = 9;
                Check(!ProductionAgentRuntime.SaveCurrentKeyEpoch(oldConfig, oldCredentials),
                    "an old runtime advanced the key epoch after a newer login");
                CheckCurrentConfig(5, "Renamed PC", "new-name");

                var refreshedGeneration = Guid.Empty;
                using (var refreshCredentials = ProductionCredentials.TryLoad())
                {
                    refreshedGeneration = refreshCredentials.LoginGeneration;
                    refreshCredentials.UpdateTokens("refreshed-access", "refreshed-refresh",
                        DateTime.UtcNow.AddHours(1));
                }
                using (var persisted = ProductionCredentials.TryLoad())
                    Check(persisted.GetAccessToken() == "refreshed-access" &&
                        persisted.GetRefreshToken() == "refreshed-refresh" &&
                        persisted.LoginGeneration == refreshedGeneration,
                        "the current login could not persist its refreshed tokens");

                var nextDeviceId = Guid.NewGuid();
                using var nextIdentity = DeviceIdentity.Generate();
                var nextConfig = Config(Guid.NewGuid(), "Replacement PC", "replacement-name", 2,
                    accountKey, issuer, machineId);
                ProductionCredentials.SaveWithRefreshToken(nextDeviceId, nextIdentity, accountKey,
                    "replacement-access", "replacement-refresh", DateTime.UtcNow.AddHours(1));
                ProductionAgentConfig.Save(nextConfig);
                ProductionIdentityStore.Save(nextConfig, nextDeviceId, nextIdentity, accountKey);
                Check(!ProductionAgentRuntime.SaveCurrentKeyEpoch(oldConfig, oldCredentials),
                    "an old device advanced the replacement device's key epoch");
                Retain(oldConfig, oldCredentials);
                using (var retained = ProductionIdentityStore.TryLoad(accountKey, issuer, machineId))
                    Check(retained != null && retained.DeviceId == nextDeviceId &&
                        retained.PcId == nextConfig.PcId && retained.PcName == "Replacement PC",
                        "an old device replaced the new retained identity");
                CheckCurrentConfig(2, "Replacement PC", "replacement-name");

                using var epochCredentials = ProductionCredentials.TryLoad();
                nextConfig.KeyEpoch = 3;
                Check(ProductionAgentRuntime.SaveCurrentKeyEpoch(nextConfig, epochCredentials),
                    "the current device could not advance its key epoch");
                CheckCurrentConfig(3, "Replacement PC", "replacement-name");

                using var logoutStarted = new ManualResetEventSlim();
                Task logout;
                lock (ProductionAccountStorage.WriteSync)
                {
                    logout = Task.Run(() =>
                    {
                        logoutStarted.Set();
                        ProductionAccountStorage.ForgetLocalAccount(root);
                    });
                    Check(logoutStarted.Wait(TimeSpan.FromSeconds(5)), "logout worker did not start");
                    Check(!logout.Wait(TimeSpan.FromMilliseconds(200)),
                        "logout deleted account files without waiting for an in-flight credential write");
                }
                logout.GetAwaiter().GetResult();
                epochCredentials.UpdateTokens("post-logout-access", "post-logout-refresh",
                    DateTime.UtcNow.AddHours(1));
                Check(!File.Exists(ProductionCredentials.DefaultPath) &&
                    !File.Exists(ProductionAgentConfig.DefaultPath),
                    "an in-flight refresh restored account files after logout");

                ProductionCredentials.SaveWithRefreshToken(deviceId, identity, accountKey,
                    "legacy-access", "legacy-refresh", DateTime.UtcNow.AddHours(1));
                RemoveLoginGenerationFromStoredCredentials();
                using var legacyCredentials = ProductionCredentials.TryLoad();
                Check(legacyCredentials.LoginGeneration == Guid.Empty,
                    "a pre-generation credential file could not be read");
                legacyCredentials.UpdateTokens("legacy-refreshed-access", "legacy-refreshed-token",
                    DateTime.UtcNow.AddHours(1));
                using (var legacyRefreshed = ProductionCredentials.TryLoad())
                    Check(legacyRefreshed.LoginGeneration == Guid.Empty &&
                        legacyRefreshed.GetRefreshToken() == "legacy-refreshed-token",
                        "a pre-generation credential file could not refresh");

                var damagedCredentials = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(ProductionCredentials.DefaultPath, damagedCredentials);
                Retain(oldConfig, oldCredentials);
                Check(File.ReadAllBytes(ProductionCredentials.DefaultPath).AsSpan().SequenceEqual(damagedCredentials),
                    "best-effort retention changed damaged credentials instead of leaving recovery to login");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT", previousRoot);
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE", previousMarker);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static ProductionAgentConfig Config(Guid pcId, string pcName, string username,
            long epoch, string accountKey, string issuer, string machineId) => new ProductionAgentConfig
        {
            ServerUrl = "https://server.example.test",
            PcId = pcId,
            PcName = pcName,
            AccountKey = accountKey,
            AccountUsername = username,
            AgentVersion = "test",
            KeyEpoch = epoch,
            OIDCIssuer = issuer,
            OIDCClientId = "termlink-windows",
            MachineId = machineId
        };

        private static void Retain(ProductionAgentConfig config, ProductionCredentials credentials) =>
            typeof(ProductionAgentRuntime)
                .GetMethod("TryRetainIdentity", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { config, credentials });

        private static void RemoveLoginGenerationFromStoredCredentials()
        {
            var path = ProductionCredentials.DefaultPath;
            var entropy = Encoding.UTF8.GetBytes("TermLink Windows production credentials V1");
            var protectedBytes = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
            try
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(plain);
                Check(fields.Remove("LoginGeneration"), "stored credentials omitted the login generation");
                var legacy = JsonSerializer.SerializeToUtf8Bytes(fields);
                try
                {
                    var rewritten = ProtectedData.Protect(legacy, entropy, DataProtectionScope.CurrentUser);
                    try { File.WriteAllBytes(path, rewritten); }
                    finally { CryptographicOperations.ZeroMemory(rewritten); }
                }
                finally { CryptographicOperations.ZeroMemory(legacy); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
                CryptographicOperations.ZeroMemory(protectedBytes);
                CryptographicOperations.ZeroMemory(entropy);
            }
        }

        private static void CheckCurrentConfig(long epoch, string pcName, string username)
        {
            var current = ProductionAgentConfig.TryLoad();
            Check(current != null && current.KeyEpoch == epoch && current.PcName == pcName &&
                current.AccountUsername == username,
                "stale persistence changed the current account configuration");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
