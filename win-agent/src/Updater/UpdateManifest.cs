using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace TermLink.Updater
{
    public sealed class UpdateManifest
    {
        public const string CurrentSchema = "termlink.windows-update-manifest";
        public const int CurrentSchemaVersion = 1;

        public string Schema { get; set; }
        public int SchemaVersion { get; set; }
        public string PackageIdentity { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string MinimumCurrentVersion { get; set; }
        public string PackageFile { get; set; }
        public string Sha256 { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    public sealed class VerifiedUpdatePackage
    {
        public VerifiedUpdatePackage(
            UpdateManifest manifest,
            string manifestPath,
            string signaturePath,
            string packagePath,
            string certificatePath,
            string certificateThumbprint)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            ManifestPath = Path.GetFullPath(manifestPath);
            SignaturePath = Path.GetFullPath(signaturePath);
            PackagePath = Path.GetFullPath(packagePath);
            CertificatePath = Path.GetFullPath(certificatePath);
            CertificateThumbprint = certificateThumbprint ?? throw new ArgumentNullException(nameof(certificateThumbprint));
        }

        public UpdateManifest Manifest { get; }
        public string ManifestPath { get; }
        public string SignaturePath { get; }
        public string PackagePath { get; }
        public string CertificatePath { get; }
        public string CertificateThumbprint { get; }
        public Version PackageVersion => Version.Parse(Manifest.Version);
    }

    public static class UpdatePackageVerifier
    {
        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> ManifestProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "schema",
            "schema_version",
            "package_identity",
            "publisher",
            "version",
            "minimum_current_version",
            "package_file",
            "sha256",
            "created_at_utc",
            "expires_at_utc"
        };

        public static VerifiedUpdatePackage Verify(
            string manifestPath,
            string signaturePath,
            string packagePath,
            string publisherCertificatePath,
            bool allowUntrustedPublisher,
            DateTimeOffset nowUtc)
        {
            RequireFile(manifestPath, "update manifest");
            RequireFile(signaturePath, "update manifest signature");
            RequireFile(packagePath, "MSIX package");
            RequireFile(publisherCertificatePath, "publisher certificate");

            var manifestBytes = File.ReadAllBytes(manifestPath);
            var signatureBytes = File.ReadAllBytes(signaturePath);
            using (var certificate = new X509Certificate2(Path.GetFullPath(publisherCertificatePath)))
            {
                VerifyCertificate(certificate, nowUtc);
                using (var rsa = certificate.GetRSAPublicKey())
                {
                    if (rsa == null || !rsa.VerifyData(
                        manifestBytes,
                        signatureBytes,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1))
                    {
                        throw new CryptographicException("The update manifest signature is invalid.");
                    }
                }

                var manifest = ParseManifest(manifestBytes, nowUtc);
                if (!string.Equals(manifest.Publisher, certificate.Subject, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The update publisher does not match the pinned certificate subject.");
                }
                if (!string.Equals(manifest.PackageFile, Path.GetFileName(packagePath), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The update package file name does not match the manifest.");
                }

                var packageHash = ComputeSha256(packagePath);
                if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(packageHash),
                    Encoding.ASCII.GetBytes(manifest.Sha256)))
                {
                    throw new InvalidDataException("The MSIX SHA-256 does not match the update manifest.");
                }

                var signature = AuthenticodeInspector.Inspect(packagePath);
                if (signature.Status != AuthenticodeStatus.Valid &&
                    !(allowUntrustedPublisher &&
                        (signature.Status == AuthenticodeStatus.NotTrusted ||
                            signature.Status == AuthenticodeStatus.UnknownError)))
                {
                    throw new CryptographicException(
                        "The MSIX Authenticode signature is not acceptable: " + signature.Status + ".");
                }
                if (string.IsNullOrWhiteSpace(signature.Thumbprint) ||
                    !string.Equals(
                        NormalizeThumbprint(signature.Thumbprint),
                        NormalizeThumbprint(certificate.Thumbprint),
                        StringComparison.Ordinal))
                {
                    throw new CryptographicException("The MSIX signer does not match the pinned publisher certificate.");
                }

                var identity = ReadPackageIdentity(packagePath);
                if (!string.Equals(identity.Name, manifest.PackageIdentity, StringComparison.Ordinal) ||
                    !string.Equals(identity.Publisher, manifest.Publisher, StringComparison.Ordinal) ||
                    !string.Equals(identity.Version, manifest.Version, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The MSIX identity does not match the signed update manifest.");
                }

                return new VerifiedUpdatePackage(
                    manifest,
                    manifestPath,
                    signaturePath,
                    packagePath,
                    publisherCertificatePath,
                    NormalizeThumbprint(certificate.Thumbprint));
            }
        }

        public static void ValidateUpgrade(VerifiedUpdatePackage target, VerifiedUpdatePackage rollback, Version currentVersion)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (rollback == null) throw new ArgumentNullException(nameof(rollback));
            if (currentVersion == null) throw new ArgumentNullException(nameof(currentVersion));
            if (!string.Equals(target.Manifest.PackageIdentity, rollback.Manifest.PackageIdentity, StringComparison.Ordinal) ||
                !string.Equals(target.Manifest.Publisher, rollback.Manifest.Publisher, StringComparison.Ordinal) ||
                !string.Equals(target.CertificateThumbprint, rollback.CertificateThumbprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Target and rollback packages do not have the same trusted identity.");
            }
            if (rollback.PackageVersion != currentVersion)
            {
                throw new InvalidDataException("The rollback package is not the currently installed version.");
            }
            if (target.PackageVersion <= currentVersion)
            {
                throw new InvalidDataException("The target update version must be newer than the installed version.");
            }
            var minimum = ParseFourPartVersion(target.Manifest.MinimumCurrentVersion, "minimum_current_version");
            if (currentVersion < minimum)
            {
                throw new InvalidDataException("The installed version is older than the update's minimum supported version.");
            }
        }

        private static UpdateManifest ParseManifest(byte[] encoded, DateTimeOffset nowUtc)
        {
            using (var document = JsonDocument.Parse(encoded, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("The update manifest root must be an object.");
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!ManifestProperties.Contains(property.Name) || !seen.Add(property.Name))
                    {
                        throw new InvalidDataException("The update manifest contains an unknown or duplicate property.");
                    }
                }
                foreach (var required in ManifestProperties)
                {
                    if (!seen.Contains(required))
                    {
                        throw new InvalidDataException("The update manifest is missing property: " + required + ".");
                    }
                }

                var root = document.RootElement;
                var manifest = new UpdateManifest
                {
                    Schema = RequiredString(root, "schema", 100),
                    SchemaVersion = RequiredInt32(root, "schema_version"),
                    PackageIdentity = RequiredString(root, "package_identity", 100),
                    Publisher = RequiredString(root, "publisher", 500),
                    Version = RequiredString(root, "version", 30),
                    MinimumCurrentVersion = RequiredString(root, "minimum_current_version", 30),
                    PackageFile = RequiredString(root, "package_file", 255),
                    Sha256 = RequiredString(root, "sha256", 64),
                    CreatedAtUtc = RequiredUtcTimestamp(root, "created_at_utc"),
                    ExpiresAtUtc = RequiredUtcTimestamp(root, "expires_at_utc")
                };

                if (!string.Equals(manifest.Schema, UpdateManifest.CurrentSchema, StringComparison.Ordinal) ||
                    manifest.SchemaVersion != UpdateManifest.CurrentSchemaVersion)
                {
                    throw new InvalidDataException("The update manifest schema is unsupported.");
                }
                ParseFourPartVersion(manifest.Version, "version");
                ParseFourPartVersion(manifest.MinimumCurrentVersion, "minimum_current_version");
                if (!Sha256Pattern.IsMatch(manifest.Sha256))
                {
                    throw new InvalidDataException("The update manifest SHA-256 must be lowercase hexadecimal.");
                }
                if (!string.Equals(manifest.PackageFile, Path.GetFileName(manifest.PackageFile), StringComparison.Ordinal) ||
                    !manifest.PackageFile.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The update manifest package_file must be a simple MSIX file name.");
                }
                if (manifest.CreatedAtUtc > nowUtc.AddMinutes(5) || manifest.ExpiresAtUtc <= nowUtc ||
                    manifest.ExpiresAtUtc <= manifest.CreatedAtUtc ||
                    manifest.ExpiresAtUtc - manifest.CreatedAtUtc > TimeSpan.FromDays(31))
                {
                    throw new InvalidDataException("The update manifest validity window is not acceptable.");
                }
                return manifest;
            }
        }

        private static PackageIdentity ReadPackageIdentity(string packagePath)
        {
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var entry = archive.Entries.SingleOrDefault(item =>
                    string.Equals(item.FullName, "AppxManifest.xml", StringComparison.Ordinal));
                if (entry == null) throw new InvalidDataException("The MSIX does not contain AppxManifest.xml.");
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 2 * 1024 * 1024
                };
                using (var stream = entry.Open())
                using (var reader = XmlReader.Create(stream, settings))
                {
                    var document = new XmlDocument { XmlResolver = null };
                    document.Load(reader);
                    var namespaces = new XmlNamespaceManager(document.NameTable);
                    namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
                    var node = document.SelectSingleNode("/f:Package/f:Identity", namespaces) as XmlElement;
                    if (node == null) throw new InvalidDataException("The MSIX Identity element is missing.");
                    return new PackageIdentity(
                        node.GetAttribute("Name"),
                        node.GetAttribute("Publisher"),
                        node.GetAttribute("Version"));
                }
            }
        }

        private static void VerifyCertificate(X509Certificate2 certificate, DateTimeOffset nowUtc)
        {
            if (nowUtc.UtcDateTime < certificate.NotBefore.ToUniversalTime() ||
                nowUtc.UtcDateTime >= certificate.NotAfter.ToUniversalTime())
            {
                throw new CryptographicException("The pinned update publisher certificate is not currently valid.");
            }
            var allowsCodeSigning = false;
            foreach (var extension in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            {
                foreach (var usage in extension.EnhancedKeyUsages)
                {
                    if (string.Equals(usage.Value, "1.3.6.1.5.5.7.3.3", StringComparison.Ordinal))
                    {
                        allowsCodeSigning = true;
                    }
                }
            }
            if (!allowsCodeSigning)
            {
                throw new CryptographicException("The pinned update publisher certificate is not valid for code signing.");
            }
        }

        private static string RequiredString(JsonElement root, string name, int maximumLength)
        {
            var value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException(name + " must be a string.");
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength)
            {
                throw new InvalidDataException(name + " is empty or too long.");
            }
            return text;
        }

        private static int RequiredInt32(JsonElement root, string name)
        {
            var value = root.GetProperty(name);
            int result;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out result))
            {
                throw new InvalidDataException(name + " must be an integer.");
            }
            return result;
        }

        private static DateTimeOffset RequiredUtcTimestamp(JsonElement root, string name)
        {
            var text = RequiredString(root, name, 50);
            DateTimeOffset value;
            if (!DateTimeOffset.TryParseExact(
                    text,
                    "o",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out value) || value.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException(name + " must be an ISO 8601 UTC timestamp.");
            }
            return value;
        }

        public static Version ParseFourPartVersion(string value, string fieldName)
        {
            Version parsed;
            var parts = value == null ? Array.Empty<string>() : value.Split('.');
            if (parts.Length != 4 || !Version.TryParse(value, out parsed) ||
                parts.Any(part => part.Length == 0 || part.Any(character => !char.IsDigit(character))) ||
                parsed.Major > 65535 || parsed.Minor > 65535 || parsed.Build > 65535 || parsed.Revision > 65535)
            {
                throw new InvalidDataException(fieldName + " must be a four-part MSIX version.");
            }
            return parsed;
        }

        public static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
        }

        private static string NormalizeThumbprint(string value)
        {
            return value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }

        private static void RequireFile(string path, string description)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("The " + description + " was not found.", path);
            }
        }

        private sealed class PackageIdentity
        {
            public PackageIdentity(string name, string publisher, string version)
            {
                Name = name;
                Publisher = publisher;
                Version = version;
            }

            public string Name { get; }
            public string Publisher { get; }
            public string Version { get; }
        }
    }

    internal enum AuthenticodeStatus
    {
        Valid = 0,
        UnknownError = 1,
        NotSigned = 2,
        HashMismatch = 3,
        NotTrusted = 4,
        NotSupportedFileFormat = 5,
        Incompatible = 6
    }

    internal sealed class AuthenticodeResult
    {
        public AuthenticodeStatus Status { get; set; }
        public string Thumbprint { get; set; }
    }

    internal static class AuthenticodeInspector
    {
        private const string Script =
            "$ErrorActionPreference='Stop';" +
            "$s=Get-AuthenticodeSignature -FilePath $env:TERMLINK_UPDATE_PACKAGE;" +
            "$r=[ordered]@{status=[int]$s.Status;thumbprint=$null};" +
            "if($null -ne $s.SignerCertificate){$r.thumbprint=$s.SignerCertificate.Thumbprint};" +
            "$r|ConvertTo-Json -Compress";

        public static AuthenticodeResult Inspect(string packagePath)
        {
            var powerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powerShell)) throw new PlatformNotSupportedException("Windows PowerShell is unavailable.");
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
            var start = new ProcessStartInfo
            {
                FileName = powerShell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(encodedScript);
            start.Environment.Remove("PSModulePath");
            start.Environment["TERMLINK_UPDATE_PACKAGE"] = Path.GetFullPath(packagePath);
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Authenticode inspection could not start.");
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(30000))
                {
                    process.Kill(true);
                    throw new TimeoutException("Authenticode inspection timed out.");
                }
                if (process.ExitCode != 0)
                {
                    throw new CryptographicException("Authenticode inspection failed: " + Limit(error, 1000));
                }
                using (var document = JsonDocument.Parse(output))
                {
                    return new AuthenticodeResult
                    {
                        Status = (AuthenticodeStatus)document.RootElement.GetProperty("status").GetInt32(),
                        Thumbprint = document.RootElement.GetProperty("thumbprint").ValueKind == JsonValueKind.String
                            ? document.RootElement.GetProperty("thumbprint").GetString()
                            : null
                    };
                }
            }
        }

        private static string Limit(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value)) return "no error details";
            return value.Length <= maximumLength ? value.Trim() : value.Substring(0, maximumLength).Trim();
        }
    }
}
