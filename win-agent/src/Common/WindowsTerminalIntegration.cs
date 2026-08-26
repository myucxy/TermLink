#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TermLink.WindowsIntegration
{
    internal sealed class WindowsTerminalIntegrationResult
    {
        internal WindowsTerminalIntegrationResult(
            string fragmentPath,
            string sha256,
            int profileCount,
            bool packageManaged = false)
        {
            FragmentPath = fragmentPath;
            Sha256 = sha256;
            ProfileCount = profileCount;
            PackageManaged = packageManaged;
        }

        public string FragmentPath { get; }
        public string Sha256 { get; }
        public int ProfileCount { get; }
        public bool PackageManaged { get; }
    }

    /// <summary>
    /// Owns only TermLink's dedicated Windows Terminal Fragment. Rebuilding it
    /// never opens settings.json and never changes profiles from other apps.
    /// The same generator is linked into Setup and Tray so a moved installation
    /// cannot leave different command lines depending on which repair path ran.
    /// </summary>
    internal static class WindowsTerminalIntegration
    {
        internal const string FragmentFileName = "termlink.json";
        internal const string InstallMarkerName = ".termlink-install.json";
        private const int ErrorInsufficientBuffer = 122;
        private const int AppModelErrorNoPackage = 15700;

        internal static string DefaultFragmentPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows Terminal", "Fragments", "TermLink", FragmentFileName);

        internal static WindowsTerminalIntegrationResult Reconfigure(string installRoot)
        {
            var root = Path.GetFullPath(installRoot);
            return ReconfigureForProcess(
                root,
                DefaultFragmentPath,
                IsCurrentProcessPackaged(),
                Path.Combine(root, "Terminal", "Fragments", FragmentFileName));
        }

        internal static WindowsTerminalIntegrationResult ReconfigureForProcess(
            string installRoot,
            string looseFragmentPath,
            bool packageManaged,
            string packagedFragmentPath)
        {
            return packageManaged
                ? InspectPackagedFragment(packagedFragmentPath)
                : Reconfigure(installRoot, looseFragmentPath);
        }

        /// <summary>
        /// Full-trust applications retain package identity when launched from an
        /// MSIX. In that mode Windows Terminal discovers the immutable Fragment
        /// declared by AppxManifest.xml; creating a second loose Fragment would
        /// expose duplicate profiles with the same GUIDs.
        /// </summary>
        internal static bool IsCurrentProcessPackaged()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            uint length = 0;
            try
            {
                var status = GetCurrentPackageFullName(ref length, null);
                if (status == AppModelErrorNoPackage) return false;
                if (status != ErrorInsufficientBuffer || length == 0) return false;

                var packageName = new StringBuilder(checked((int)length));
                status = GetCurrentPackageFullName(ref length, packageName);
                return status == 0 && packageName.Length > 0;
            }
            catch (Exception error) when (
                error is DllNotFoundException ||
                error is EntryPointNotFoundException ||
                error is BadImageFormatException ||
                error is OverflowException)
            {
                return false;
            }
        }

        internal static WindowsTerminalIntegrationResult InspectPackagedFragment(
            string packagedFragmentPath)
        {
            if (string.IsNullOrWhiteSpace(packagedFragmentPath))
                throw new ArgumentException(
                    "Packaged Windows Terminal Fragment path is required.",
                    nameof(packagedFragmentPath));

            var path = Path.GetFullPath(packagedFragmentPath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "The MSIX package does not contain its Windows Terminal Fragment.",
                    path);
            }

            int profileCount;
            using (var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
            {
                if (!document.RootElement.TryGetProperty("profiles", out var profiles) ||
                    profiles.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException(
                        "The packaged Windows Terminal Fragment has no profiles array.");
                }
                profileCount = profiles.GetArrayLength();
            }
            if (profileCount == 0)
            {
                throw new InvalidDataException(
                    "The packaged Windows Terminal Fragment contains no profiles.");
            }

            return new WindowsTerminalIntegrationResult(
                path,
                Sha256(path),
                profileCount,
                packageManaged: true);
        }

        internal static WindowsTerminalIntegrationResult Reconfigure(
            string installRoot,
            string fragmentPath)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
                throw new ArgumentException("TermLink install directory is required.", nameof(installRoot));
            if (string.IsNullOrWhiteSpace(fragmentPath))
                throw new ArgumentException("Windows Terminal Fragment path is required.", nameof(fragmentPath));

            var root = Path.GetFullPath(installRoot);
            var path = Path.GetFullPath(fragmentPath);
            var sessionHost = Path.Combine(root, "TermLink.SessionHost.exe");
            if (!File.Exists(sessionHost))
            {
                throw new FileNotFoundException(
                    "TermLink.SessionHost.exe was not found in the current installation directory.",
                    sessionHost);
            }

            var profiles = new List<Dictionary<string, object>>
            {
                CreateProfile("{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}", "TermLink CMD", "cmd.exe", "command-prompt"),
                CreateProfile("{b2025574-4cc8-5264-b577-7c6532eff95a}", "TermLink Windows PowerShell", "powershell.exe -NoLogo -NoProfile", "windows-powershell")
            };
            if (FindExecutableOnPath("pwsh.exe") != null)
            {
                profiles.Add(CreateProfile("{7cf2d180-c8be-5b08-99a0-268bd2dc1d6f}", "TermLink PowerShell 7", "pwsh.exe -NoLogo -NoProfile", "powershell-7"));
            }
            if (FindExecutableOnPath("wsl.exe") != null)
            {
                profiles.Add(CreateProfile("{f5652f1d-a6da-5199-b595-3a1255ce04b4}", "TermLink WSL", "wsl.exe", "wsl"));
            }

            foreach (var profile in profiles)
            {
                var childCommand = (string)profile["__child"];
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(childCommand));
                profile["commandline"] = Quote(sessionHost) + " --command-base64 " + encoded +
                    " --managed-shell " + profile["__shell"];
                profile.Remove("__child");
                profile.Remove("__shell");
            }

            var json = JsonSerializer.Serialize(
                new Dictionary<string, object> { ["profiles"] = profiles },
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Windows Terminal Fragment has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(
                directory,
                ".termlink-fragment-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                var sha256 = Sha256(temporaryPath);
                // The dedicated TermLink Fragment is intentionally replaced even
                // when an obsolete marker or old install path can no longer prove
                // ownership. No other Terminal file is in this replacement scope.
                File.Move(temporaryPath, path, overwrite: true);
                return new WindowsTerminalIntegrationResult(path, sha256, profiles.Count);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        /// <summary>
        /// Keeps uninstall ownership valid after a user-triggered Tray repair.
        /// Missing or unrelated markers are left untouched; the Fragment repair
        /// itself remains successful and can still be repeated later.
        /// </summary>
        internal static bool TryRefreshInstallMarker(
            string installRoot,
            WindowsTerminalIntegrationResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.PackageManaged) return false;
            try
            {
                var root = Path.GetFullPath(installRoot);
                var markerPath = Path.Combine(root, InstallMarkerName);
                if (!File.Exists(markerPath)) return false;
                var marker = JsonNode.Parse(File.ReadAllText(markerPath, Encoding.UTF8)) as JsonObject;
                if (marker == null ||
                    !string.Equals(marker["Product"]?.GetValue<string>(), "TermLink", StringComparison.Ordinal) ||
                    !PathsEqual(marker["InstallRoot"]?.GetValue<string>(), root)) return false;

                marker["TerminalFragmentPath"] = result.FragmentPath;
                marker["TerminalFragmentSha256"] = result.Sha256;
                marker["TerminalIntegrationStatus"] = "configured";
                marker["TerminalIntegrationDiagnostic"] = null;
                var temporaryPath = markerPath + ".terminal-repair-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(
                        temporaryPath,
                        marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                        new UTF8Encoding(false));
                    File.Move(temporaryPath, markerPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                return true;
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is SecurityException ||
                error is JsonException ||
                error is InvalidOperationException ||
                error is ArgumentException ||
                error is NotSupportedException)
            {
                return false;
            }
        }

        private static Dictionary<string, object> CreateProfile(
            string guid,
            string name,
            string childCommand,
            string shell)
        {
            return new Dictionary<string, object>
            {
                ["guid"] = guid,
                ["name"] = name,
                ["commandline"] = string.Empty,
                ["startingDirectory"] = "%USERPROFILE%",
                // TermLink owns the whole SessionHost command line for this
                // profile.  When Android terminates that exact Host, Windows
                // Terminal must remove its tab even if the child shell was
                // force-ended and returned a non-zero status.  This setting is
                // scoped to TermLink profiles and never changes user profiles.
                ["closeOnExit"] = "always",
                ["hidden"] = false,
                ["__child"] = childCommand,
                ["__shell"] = shell
            };
        }

        private static string FindExecutableOnPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var item in path.Split(Path.PathSeparator).Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                try
                {
                    var candidate = Path.Combine(item.Trim().Trim('"'), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception error) when (
                    error is ArgumentException ||
                    error is NotSupportedException ||
                    error is PathTooLongException)
                {
                    // A malformed PATH entry does not invalidate other entries.
                }
            }
            return null;
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(
            ref uint packageFullNameLength,
            StringBuilder packageFullName);
    }
}
#nullable restore
