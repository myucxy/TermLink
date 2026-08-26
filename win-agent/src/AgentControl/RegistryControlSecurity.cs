using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TermLink.Agent
{
    public static class RegistryControlAuthentication
    {
        public const int SecretBytes = 32;
        public const int NonceBytes = 16;
        public const int TagBytes = 32;
        public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

        public static void Prepare(RegistryRequest request, byte[] secret, DateTime utcNow)
        {
            if (request == null) throw new ArgumentNullException("request");
            ValidateSecret(secret);
            if (utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Authentication time must be UTC.", "utcNow");
            request.AuthenticationTimestampUnixSeconds = new DateTimeOffset(utcNow).ToUnixTimeSeconds();
            request.AuthenticationNonce = new byte[NonceBytes];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(request.AuthenticationNonce);
            request.AuthenticationTag = ComputeTag(request, secret);
        }

        public static bool Verify(RegistryRequest request, byte[] secret, DateTime utcNow)
        {
            if (request == null) return false;
            ValidateSecret(secret);
            if (utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Authentication time must be UTC.", "utcNow");
            if (request.AuthenticationNonce == null || request.AuthenticationNonce.Length != NonceBytes ||
                request.AuthenticationTag == null || request.AuthenticationTag.Length != TagBytes ||
                request.AuthenticationTimestampUnixSeconds <= 0)
            {
                return false;
            }
            DateTime requestUtc;
            try
            {
                requestUtc = new DateTime(
                    checked(621355968000000000L +
                        request.AuthenticationTimestampUnixSeconds * TimeSpan.TicksPerSecond),
                    DateTimeKind.Utc);
            }
            catch (Exception error)
            {
                if (!(error is ArgumentOutOfRangeException) && !(error is OverflowException)) throw;
                return false;
            }
            if ((utcNow - requestUtc).Duration() > MaximumClockSkew) return false;
            var expected = ComputeTag(request, secret);
            try
            {
                return FixedTimeEquals(expected, request.AuthenticationTag);
            }
            finally
            {
                Array.Clear(expected, 0, expected.Length);
            }
        }

        public static void PrepareResponse(
            RegistryResponse response,
            RegistryRequest request,
            byte[] secret)
        {
            if (response == null) throw new ArgumentNullException("response");
            if (request == null || request.AuthenticationNonce == null ||
                request.AuthenticationNonce.Length != NonceBytes)
            {
                throw new ArgumentException("An authenticated Registry request is required.", "request");
            }
            ValidateSecret(secret);
            response.AuthenticationNonce = (byte[])request.AuthenticationNonce.Clone();
            response.AuthenticationTag = ComputeResponseTag(
                response,
                response.AuthenticationNonce,
                secret);
        }

        public static bool VerifyResponse(
            RegistryResponse response,
            RegistryRequest request,
            byte[] secret)
        {
            if (response == null || request == null ||
                response.AuthenticationNonce == null || response.AuthenticationNonce.Length != NonceBytes ||
                response.AuthenticationTag == null || response.AuthenticationTag.Length != TagBytes ||
                request.AuthenticationNonce == null || request.AuthenticationNonce.Length != NonceBytes ||
                !FixedTimeEquals(response.AuthenticationNonce, request.AuthenticationNonce))
            {
                return false;
            }
            ValidateSecret(secret);
            var expected = ComputeResponseTag(
                response,
                response.AuthenticationNonce,
                secret);
            try
            {
                return FixedTimeEquals(expected, response.AuthenticationTag);
            }
            finally
            {
                Array.Clear(expected, 0, expected.Length);
            }
        }

        private static byte[] ComputeTag(RegistryRequest request, byte[] secret)
        {
            var payload = RegistryControlCodec.AuthenticationPayload(request);
            try
            {
                using (var hmac = new HMACSHA256(secret)) return hmac.ComputeHash(payload);
            }
            finally
            {
                Array.Clear(payload, 0, payload.Length);
            }
        }

        private static byte[] ComputeResponseTag(
            RegistryResponse response,
            byte[] nonce,
            byte[] secret)
        {
            var domain = Encoding.ASCII.GetBytes("TermLink Registry Control Response V7\0");
            var responsePayload = RegistryControlCodec.ResponseAuthenticationPayload(response, nonce);
            var payload = new byte[domain.Length + responsePayload.Length];
            Buffer.BlockCopy(domain, 0, payload, 0, domain.Length);
            Buffer.BlockCopy(responsePayload, 0, payload, domain.Length, responsePayload.Length);
            try
            {
                using (var hmac = new HMACSHA256(secret)) return hmac.ComputeHash(payload);
            }
            finally
            {
                Array.Clear(domain, 0, domain.Length);
                Array.Clear(responsePayload, 0, responsePayload.Length);
                Array.Clear(payload, 0, payload.Length);
            }
        }

        private static void ValidateSecret(byte[] secret)
        {
            if (secret == null || secret.Length != SecretBytes)
                throw new ArgumentException("Registry control secret must contain 32 bytes.", "secret");
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }

    public sealed class RegistryControlReplayWindow
    {
        private const int MaximumEntries = 2048;
        private readonly object sync = new object();
        private readonly Dictionary<string, DateTime> observed =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public bool TryAccept(RegistryRequest request, DateTime utcNow)
        {
            if (request == null || request.AuthenticationNonce == null ||
                request.AuthenticationNonce.Length != RegistryControlAuthentication.NonceBytes)
            {
                return false;
            }
            if (utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Replay time must be UTC.", "utcNow");
            var key = Convert.ToBase64String(request.AuthenticationNonce);
            lock (sync)
            {
                var threshold = utcNow - RegistryControlAuthentication.MaximumClockSkew;
                if (observed.Count >= MaximumEntries)
                {
                    var expired = new List<string>();
                    foreach (var item in observed)
                    {
                        if (item.Value < threshold) expired.Add(item.Key);
                    }
                    foreach (var expiredKey in expired) observed.Remove(expiredKey);
                    if (observed.Count >= MaximumEntries) return false;
                }
                if (observed.ContainsKey(key)) return false;
                observed.Add(key, utcNow);
                return true;
            }
        }
    }

    public static class RegistryControlSecretStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TLCS");
        private const byte FormatVersion = 1;
        private const int MaximumProtectedBytes = 4096;

        public static string DefaultPath()
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var acceptanceRoot = Environment.GetEnvironmentVariable(
                "TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(acceptanceRoot))
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE")))
                    throw new InvalidOperationException(
                        "TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT requires TERMLINK_ACCEPTANCE_CA_FILE.");
                if (!Path.IsPathRooted(acceptanceRoot))
                    throw new InvalidOperationException(
                        "TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT must be an absolute path.");
                localData = Path.GetFullPath(acceptanceRoot);
            }
            return Path.Combine(
                localData,
                "TermLink",
                "Agent",
                "control.secret");
        }

        public static byte[] GetOrCreateDefault()
        {
            return GetOrCreate(DefaultPath());
        }

        public static byte[] LoadDefault()
        {
            return Load(DefaultPath());
        }

        public static byte[] GetOrCreate(string path)
        {
            path = NormalizePath(path);
            if (File.Exists(path)) return Load(path);
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Control secret directory is unavailable.");
            Directory.CreateDirectory(directory);
            var secret = new byte[RegistryControlAuthentication.SecretBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
            var protectedBytes = Protect(secret);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(protectedBytes.Length);
                    writer.Write(protectedBytes);
                    writer.Flush();
                    stream.Flush(true);
                }
                try
                {
                    File.Move(temporary, path);
                    return secret;
                }
                catch (IOException)
                {
                    if (!File.Exists(path)) throw;
                    Array.Clear(secret, 0, secret.Length);
                    return Load(path);
                }
            }
            catch
            {
                Array.Clear(secret, 0, secret.Length);
                throw;
            }
            finally
            {
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static byte[] Load(string path)
        {
            path = NormalizePath(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length ||
                    !FixedTimeEquals(magic, Magic) ||
                    reader.ReadByte() != FormatVersion)
                {
                    throw new InvalidDataException("Registry control secret header is invalid.");
                }
                var length = reader.ReadInt32();
                if (length <= 0 || length > MaximumProtectedBytes)
                    throw new InvalidDataException("Registry control secret length is invalid.");
                var protectedBytes = reader.ReadBytes(length);
                if (protectedBytes.Length != length || stream.Position != stream.Length)
                    throw new InvalidDataException("Registry control secret file is truncated or has trailing bytes.");
                try
                {
                    var secret = Unprotect(protectedBytes);
                    if (secret.Length != RegistryControlAuthentication.SecretBytes)
                    {
                        Array.Clear(secret, 0, secret.Length);
                        throw new InvalidDataException("Registry control secret payload length is invalid.");
                    }
                    return secret;
                }
                finally
                {
                    Array.Clear(protectedBytes, 0, protectedBytes.Length);
                }
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Control secret path is required.", "path");
            return Path.GetFullPath(path);
        }

        private static byte[] Protect(byte[] clear)
        {
            return InvokeCryptProtect(clear, true);
        }

        private static byte[] Unprotect(byte[] protectedBytes)
        {
            return InvokeCryptProtect(protectedBytes, false);
        }

        private static byte[] InvokeCryptProtect(byte[] input, bool protect)
        {
            var inputBlob = new DataBlob();
            var outputBlob = new DataBlob();
            try
            {
                inputBlob.Size = input.Length;
                inputBlob.Data = Marshal.AllocHGlobal(input.Length);
                Marshal.Copy(input, 0, inputBlob.Data, input.Length);
                var success = protect
                    ? CryptProtectData(
                        ref inputBlob,
                        null,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        1,
                        out outputBlob)
                    : CryptUnprotectData(
                        ref inputBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        1,
                        out outputBlob);
                if (!success)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var result = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (inputBlob.Data != IntPtr.Zero)
                {
                    for (var index = 0; index < inputBlob.Size; index++)
                        Marshal.WriteByte(inputBlob.Data, index, 0);
                    Marshal.FreeHGlobal(inputBlob.Data);
                }
                if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }
}
