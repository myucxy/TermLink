using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TermLink.UpdateCore
{
    public static class WindowsUpdateSecurity
    {
        public const uint TrustSuccess = 0x00000000;
        public const uint CertificateUntrustedRoot = 0x800B0109;
        public const uint CertificateChaining = 0x800B010A;
        public const uint TrustBadDigest = 0x80096010;
        public const uint TrustNoSignature = 0x800B0100;
        private const string PublisherCertificateFileName = "TermLink.UpdatePublisher.cer";
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

        public static void ValidateIntegrity(string path, long expectedSize, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("The downloaded update package is missing.", path);
            var normalizedHash = NormalizeHex(expectedSha256);
            if (expectedSize <= 0 || !IsHex(normalizedHash, 64))
                throw new InvalidDataException("The expected update size or SHA-256 is invalid.");
            if (new FileInfo(path).Length != expectedSize)
                throw new InvalidDataException("The downloaded update size does not match the fixed metadata.");
            byte[] actual;
            using (var stream = File.OpenRead(path)) actual = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(normalizedHash)))
                throw new CryptographicException("The downloaded update SHA-256 does not match the fixed metadata.");
        }

        public static string ValidateAuthenticode(
            string path,
            string expectedThumbprint,
            string reportedThumbprint)
        {
            var status = VerifyEmbeddedSignature(path);
            if (!IsAcceptableAuthenticodeStatus(status))
            {
                throw new CryptographicException(
                    "The update Authenticode signature failed validation (0x" + status.ToString("X8") + ").");
            }
            var actual = ReadSignerThumbprint(path);
            ValidateSignerIdentity(actual, expectedThumbprint, reportedThumbprint);
            return actual;
        }

        public static bool IsAcceptableAuthenticodeStatus(uint status) =>
            status == TrustSuccess || status == CertificateUntrustedRoot || status == CertificateChaining;

        public static void ValidateSignerIdentity(
            string actualThumbprint,
            string expectedThumbprint,
            string reportedThumbprint)
        {
            var actual = NormalizeThumbprint(actualThumbprint, "downloaded Authenticode signer");
            var expected = NormalizeThumbprint(expectedThumbprint, "installed/configured update signer");
            if (!FixedHexEquals(actual, expected))
                throw new CryptographicException("The update signer does not match the installed or configured TermLink publisher.");
            if (!string.IsNullOrWhiteSpace(reportedThumbprint))
            {
                var reported = NormalizeThumbprint(reportedThumbprint, "server-pinned signer fingerprint");
                if (!FixedHexEquals(actual, reported))
                    throw new CryptographicException("The update signer does not match the server-pinned signer fingerprint.");
            }
        }

        public static string ResolveExpectedSignerThumbprint(string applicationDirectory = null)
        {
            var configuredThumbprint = Environment.GetEnvironmentVariable("TERMLINK_UPDATE_SIGNER_THUMBPRINT");
            var configuredCertificate = Environment.GetEnvironmentVariable("TERMLINK_UPDATE_PUBLISHER_CERTIFICATE");
            string expected = null;
            if (!string.IsNullOrWhiteSpace(configuredThumbprint))
                expected = NormalizeThumbprint(configuredThumbprint, "configured update signer");
            if (!string.IsNullOrWhiteSpace(configuredCertificate))
            {
                var certificateThumbprint = ReadCertificateThumbprint(configuredCertificate, true);
                if (expected != null) ValidateSignerIdentity(certificateThumbprint, expected, null);
                expected = certificateThumbprint;
            }
            if (expected != null) return expected;

            var root = string.IsNullOrWhiteSpace(applicationDirectory)
                ? AppContext.BaseDirectory
                : Path.GetFullPath(applicationDirectory);
            var bundledCertificate = Path.Combine(root, PublisherCertificateFileName);
            if (File.Exists(bundledCertificate))
            {
                expected = ReadCertificateThumbprint(bundledCertificate, true);
                var installedTray = Path.Combine(root, "TermLink.exe");
                var installedProcessPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(installedProcessPath) && File.Exists(installedProcessPath) &&
                    string.Equals(
                        Path.GetFullPath(installedProcessPath),
                        Path.GetFullPath(installedTray),
                        StringComparison.OrdinalIgnoreCase))
                {
                    var status = VerifyEmbeddedSignature(installedProcessPath);
                    if (!IsAcceptableAuthenticodeStatus(status))
                        throw new CryptographicException(
                            "The installed executable signature cannot establish the bundled update publisher identity.");
                    ValidateSignerIdentity(ReadSignerThumbprint(installedProcessPath), expected, null);
                }
                return expected;
            }

            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                var status = VerifyEmbeddedSignature(processPath);
                if (!IsAcceptableAuthenticodeStatus(status))
                    throw new CryptographicException("The installed executable signature cannot establish an update publisher identity.");
                return NormalizeThumbprint(ReadSignerThumbprint(processPath), "installed executable signer");
            }
            throw new CryptographicException("No installed or configured TermLink update publisher identity is available.");
        }

        public static string ReadCertificateThumbprint(string path, bool required)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                if (required) throw new FileNotFoundException("The update publisher certificate was not found.", path);
                return null;
            }
            using (var certificate = new X509Certificate2(Path.GetFullPath(path)))
                return NormalizeThumbprint(certificate.Thumbprint, "update publisher certificate thumbprint");
        }

        public static uint VerifyEmbeddedSignature(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("The Authenticode file was not found.", path);
            var pathPointer = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
            var filePointer = IntPtr.Zero;
            var dataPointer = IntPtr.Zero;
            try
            {
                var file = new WinTrustFileInfo
                {
                    StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                    FilePath = pathPointer
                };
                filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(file, filePointer, false);
                var data = new WinTrustData
                {
                    StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                    UiChoice = 2,
                    UnionChoice = 1,
                    File = filePointer,
                    ProviderFlags = 0x00001000
                };
                dataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
                Marshal.StructureToPtr(data, dataPointer, false);
                return unchecked((uint)WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPointer));
            }
            finally
            {
                if (dataPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(dataPointer);
                if (filePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(filePointer);
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        public static string ReadSignerThumbprint(string path)
        {
            try
            {
                using (var certificate = X509Certificate.CreateFromSignedFile(Path.GetFullPath(path)))
                using (var certificate2 = new X509Certificate2(certificate.GetRawCertData()))
                    return NormalizeThumbprint(certificate2.Thumbprint, "Authenticode signer thumbprint");
            }
            catch (CryptographicException error)
            {
                throw new CryptographicException("The update EXE has no readable Authenticode signer.", error);
            }
        }

        public static string NormalizeThumbprint(string value, string description)
        {
            var normalized = NormalizeHex(value);
            if (!IsHex(normalized, 40))
                throw new CryptographicException("The " + description + " is not a SHA-1 certificate thumbprint.");
            return normalized;
        }

        public static string NormalizeHex(string value)
        {
            if (value == null) return string.Empty;
            var characters = value.ToLowerInvariant().ToCharArray();
            var result = new char[characters.Length];
            var count = 0;
            foreach (var character in characters)
            {
                if (character == ':' || character == '-' || char.IsWhiteSpace(character)) continue;
                result[count++] = character;
            }
            return new string(result, 0, count);
        }

        public static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (var character in value)
            {
                if ((character < '0' || character > '9') && (character < 'a' || character > 'f')) return false;
            }
            return true;
        }

        private static bool FixedHexEquals(string left, string right) =>
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            IntPtr trustData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructureSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructureSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
        }
    }
}
