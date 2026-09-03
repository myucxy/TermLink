using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TermLink.Agent
{
    /// <summary>
    /// Derives a non-secret, account-scoped machine correlation value. The raw
    /// Windows MachineGuid never leaves this process and is never persisted by
    /// TermLink. Authentication continues to use DeviceIdentity keys.
    /// </summary>
    internal static class ProductionMachineIdentity
    {
        private const string DerivationDomain = "TERMLINK-WINDOWS-MACHINE-ID-V1";
        private const string PcIdDomain = "TERMLINK-WINDOWS-PC-ID-V1";

        internal static string DeriveForCurrentMachine(string oidcIssuer, string accountKey)
        {
            return Derive(ReadWindowsMachineGuid(), oidcIssuer, accountKey);
        }

        internal static string Derive(string machineGuid, string oidcIssuer, string accountKey)
        {
            if (!Guid.TryParse((machineGuid ?? string.Empty).Trim(), out var parsedMachineGuid) ||
                parsedMachineGuid == Guid.Empty)
            {
                throw new InvalidOperationException("The Windows machine identity is unavailable.");
            }
            var issuer = ProductionCredentials.NormalizeIssuer(oidcIssuer);
            var normalizedAccountKey = ProductionCredentials.NormalizeAccountKey(accountKey);
            if (issuer == null || normalizedAccountKey == null)
                throw new ArgumentException("A stable OIDC issuer and account binding are required.");

            var input = Encoding.UTF8.GetBytes(string.Join(
                "\n",
                DerivationDomain,
                parsedMachineGuid.ToString("D").ToLowerInvariant(),
                issuer,
                normalizedAccountKey));
            try
            {
                var digest = SHA256.HashData(input);
                try { return Base64Url(digest); }
                finally { CryptographicOperations.ZeroMemory(digest); }
            }
            finally { CryptographicOperations.ZeroMemory(input); }
        }

        internal static Guid DeriveInitialPcId(string machineId)
        {
            if (!IsValid(machineId)) throw new ArgumentException("The machine ID is invalid.", nameof(machineId));
            var input = Encoding.UTF8.GetBytes(PcIdDomain + "\n" + machineId);
            try
            {
                var digest = SHA256.HashData(input);
                try
                {
                    var bytes = new byte[16];
                    Buffer.BlockCopy(digest, 0, bytes, 0, bytes.Length);
                    // Mark the deterministic identifier as an RFC 4122 variant,
                    // name-based UUID. It is a database locator, never a key.
                    bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
                    bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
                    return new Guid(bytes);
                }
                finally { CryptographicOperations.ZeroMemory(digest); }
            }
            finally { CryptographicOperations.ZeroMemory(input); }
        }

        internal static bool IsValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 43) return false;
            try
            {
                var bytes = Base64UrlDecode(value);
                try { return bytes.Length == 32 && string.Equals(Base64Url(bytes), value, StringComparison.Ordinal); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string ReadWindowsMachineGuid()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
                    var value = key?.GetValue("MachineGuid", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (Guid.TryParse((value ?? string.Empty).Trim(), out var parsed) && parsed != Guid.Empty)
                        return parsed.ToString("D");
                }
                catch (Exception error) when (error is UnauthorizedAccessException || error is System.IO.IOException ||
                    error is System.Security.SecurityException)
                {
                    // Try the alternate registry view. The final failure is
                    // explicit so enrollment cannot silently rotate identity.
                }
            }
            throw new InvalidOperationException("The Windows MachineGuid could not be read.");
        }

        private static string Base64Url(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string value)
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            return Convert.FromBase64String(encoded);
        }
    }
}
