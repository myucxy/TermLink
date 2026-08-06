using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TermLink.Setup
{
    internal static class Program
    {
        internal const string PayloadResourceName = "TermLink.Setup.Payload.zip";
        internal const string ProductId = "TermLink";
        internal const string RunValueName = "TermLink";

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                var invokedAsUninstaller = IsUninstallerExecutable();
                if (invokedAsUninstaller && args.Length == 0)
                {
                    return Installer.LaunchInstalledUninstaller();
                }
                var options = SetupArguments.Parse(args);
                if (options.Help)
                {
                    Console.WriteLine(SetupArguments.Usage);
                    return 0;
                }

                if (options.Uninstall)
                {
                    if (invokedAsUninstaller && string.IsNullOrWhiteSpace(options.CleanupSelfPath))
                    {
                        return Installer.LaunchInstalledUninstaller(options.InstallDirectory);
                    }
                    var root = options.InstallDirectory ?? Installer.DiscoverPreviousInstallDirectory();
                    Installer.Uninstall(root);
                    Installer.ScheduleSelfDelete(options.CleanupSelfPath);
                    return 0;
                }

                if (!string.IsNullOrWhiteSpace(options.InstallDirectory))
                {
                    Installer.Install(options.InstallDirectory, options.CreateStartup);
                    return 0;
                }

                System.Windows.Forms.Application.SetHighDpiMode(
                    System.Windows.Forms.HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                System.Windows.Forms.Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception error)
            {
                var errorLogPath = Path.Combine(Path.GetTempPath(), "TermLink-Setup-error.log");
                try
                {
                    File.AppendAllText(
                        errorLogPath,
                        DateTime.UtcNow.ToString("O") + Environment.NewLine + error + Environment.NewLine);
                }
                catch
                {
                    // Error reporting must not mask the original installer failure.
                }
                if (args.Any(value => string.Equals(value, "--quiet", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Error.WriteLine(error.Message);
                    return 1;
                }

                try
                {
                    System.Windows.Forms.MessageBox.Show(
                        error.Message + Environment.NewLine + Environment.NewLine +
                        "详细日志: " + errorLogPath,
                        "TermLink 安装程序",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                }
                catch
                {
                    Console.Error.WriteLine(error.Message);
                }
                return 1;
            }
        }

        private static bool IsUninstallerExecutable()
        {
            var processPath = Environment.ProcessPath;
            return !string.IsNullOrWhiteSpace(processPath) &&
                string.Equals(Path.GetFileName(processPath), "TermLink.Uninstall.exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class SetupArguments
    {
        internal const string Usage =
            "TermLink Setup\n" +
            "  TermLink-Setup.exe                         打开安装向导\n" +
            "  TermLink-Setup.exe --install-dir <目录>     静默安装\n" +
            "  TermLink-Setup.exe --install-dir <目录> --no-startup\n" +
            "  TermLink-Setup.exe --uninstall [<目录>]     卸载\n" +
            "  TermLink-Setup.exe --help                   显示帮助";

        public string InstallDirectory { get; private set; }
        public bool Uninstall { get; private set; }
        public bool CreateStartup { get; private set; } = true;
        public bool Help { get; private set; }
        public string CleanupSelfPath { get; private set; }

        public static SetupArguments Parse(string[] args)
        {
            var result = new SetupArguments();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "--install-dir":
                        result.InstallDirectory = Required(args, ref index, "--install-dir");
                        break;
                    case "--uninstall":
                        result.Uninstall = true;
                        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            result.InstallDirectory = args[++index];
                        }
                        break;
                    case "--no-startup":
                        result.CreateStartup = false;
                        break;
                    case "--quiet":
                        break;
                    case "--cleanup-self":
                        result.CleanupSelfPath = Required(args, ref index, "--cleanup-self");
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        result.Help = true;
                        break;
                    default:
                        throw new ArgumentException("未知参数: " + args[index]);
                }
            }

            if (result.Uninstall && args.Any(value => string.Equals(value, "--install-dir", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("--uninstall 不能和 --install-dir 同时使用。");
            }
            return result;
        }

        private static string Required(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException(option + " 需要一个目录参数。");
            }
            return args[index];
        }
    }

    internal sealed class InstallMarker
    {
        public string Product { get; set; }
        public string Version { get; set; }
        public string InstallRoot { get; set; }
        public string InstalledUtc { get; set; }
        public string TerminalFragmentPath { get; set; }
        public string TerminalFragmentSha256 { get; set; }
        public string StartupCommand { get; set; }
        public string DesktopShortcutPath { get; set; }
        public string DesktopShortcutSha256 { get; set; }
        public string StartMenuShortcutPath { get; set; }
        public string StartMenuShortcutSha256 { get; set; }
        public string UninstallerPath { get; set; }
        public string UninstallerSha256 { get; set; }
    }

    internal static class Installer
    {
        private const string MarkerName = ".termlink-install.json";
        private const string FragmentFileName = "termlink.json";
        private const string UninstallerFileName = "TermLink.Uninstall.exe";
        private const string InstallStateSubKey = @"Software\TermLink";
        private const string InstallRootValueName = "InstallRoot";
        private static readonly string[] RequiredPayloadFiles =
        {
            "TermLink.Agent.exe",
            "TermLink.Tray.exe"
        };
        private static readonly string[] OwnedProcessNames =
        {
            "TermLink.Agent",
            "TermLink.SessionHost",
            "TermLink.Tray",
            "TermLink.Updater"
        };

        internal static string DefaultInstallDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "TermLink");
            }
        }

        /// <summary>
        /// Finds the last valid TermLink installation for the interactive setup
        /// wizard (and for an uninstall without an explicit directory). The
        /// registry value is only a hint; the marker and payload are validated
        /// before a path is returned, so a stale or tampered value is ignored.
        /// </summary>
        internal static string DiscoverPreviousInstallDirectory()
        {
            var candidates = new List<string>();
            AddCandidate(candidates, ReadRegisteredInstallDirectory());
            // Older installers did not persist the registry hint, but they did
            // write the Windows Terminal fragment. Its command line contains
            // the absolute SessionHost path and therefore lets upgrades recover
            // a custom directory without scanning an entire drive.
            AddCandidate(candidates, ReadFragmentInstallDirectory());
            AddCandidate(candidates, DefaultInstallDirectory);

            foreach (var parent in CommonInstallParents())
            {
                try
                {
                    if (!Directory.Exists(parent)) continue;
                    foreach (var directory in Directory.EnumerateDirectories(parent, "TermLink", SearchOption.TopDirectoryOnly))
                    {
                        AddCandidate(candidates, directory);
                    }
                }
                catch (Exception error) when (
                    error is UnauthorizedAccessException ||
                    error is DirectoryNotFoundException ||
                    error is IOException)
                {
                    // Discovery is best effort. A protected or disappearing
                    // parent must never prevent a first-time installation.
                }
            }

            foreach (var candidate in candidates)
            {
                if (ReadMarkerIfPresent(candidate) != null) return candidate;
            }
            return DefaultInstallDirectory;
        }

        private static string ReadFragmentInstallDirectory()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows Terminal", "Fragments", "TermLink", FragmentFileName);
            if (!File.Exists(path)) return null;
            try
            {
                using (var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
                {
                    if (!document.RootElement.TryGetProperty("profiles", out var profiles) ||
                        profiles.ValueKind != JsonValueKind.Array) return null;
                    foreach (var profile in profiles.EnumerateArray())
                    {
                        if (!profile.TryGetProperty("commandline", out var commandline) ||
                            commandline.ValueKind != JsonValueKind.String) continue;
                        var value = commandline.GetString();
                        const string executableName = "TermLink.SessionHost.exe";
                        var executableIndex = value.IndexOf(executableName, StringComparison.OrdinalIgnoreCase);
                        if (executableIndex < 0) continue;
                        var quoteIndex = value.LastIndexOf('"', executableIndex);
                        var start = quoteIndex >= 0 ? quoteIndex + 1 :
                            value.LastIndexOf(' ', executableIndex) + 1;
                        if (start < 0 || start >= executableIndex) continue;
                        var executablePath = value.Substring(start, executableIndex - start + executableName.Length);
                        return Directory.GetParent(Path.GetFullPath(executablePath))?.FullName;
                    }
                }
            }
            catch (Exception error) when (
                error is JsonException ||
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is ArgumentException ||
                error is NotSupportedException)
            {
                return null;
            }
            return null;
        }

        internal static bool IsValidInstallDirectory(string root)
        {
            return ReadMarkerIfPresent(root) != null;
        }

        private static IEnumerable<string> CommonInstallParents()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            return roots
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddCandidate(ICollection<string> candidates, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            try
            {
                var normalized = NormalizeInstallRoot(candidate);
                if (!candidates.Any(value => string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(normalized);
                }
            }
            catch (Exception error) when (
                error is ArgumentException ||
                error is NotSupportedException ||
                error is IOException)
            {
                // Ignore malformed or dangerous paths from external state.
            }
        }

        private static string ReadRegisteredInstallDirectory()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(InstallStateSubKey, writable: false))
                {
                    return key?.GetValue(InstallRootValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                }
            }
            catch (Exception error) when (
                error is SecurityException ||
                error is UnauthorizedAccessException ||
                error is IOException)
            {
                return null;
            }
        }

        private static void RememberInstallDirectory(string root)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(InstallStateSubKey, writable: true))
                {
                    key?.SetValue(InstallRootValueName, root, RegistryValueKind.String);
                }
            }
            catch (Exception error) when (
                error is SecurityException ||
                error is UnauthorizedAccessException ||
                error is IOException)
            {
                // Registry persistence is an optimization. The marker remains
                // authoritative and the install must still succeed if HKCU is
                // restricted by enterprise policy.
            }
        }

        private static void ForgetInstallDirectory(string root)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(InstallStateSubKey, writable: true))
                {
                    var remembered = key?.GetValue(InstallRootValueName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (string.Equals(remembered, root, StringComparison.OrdinalIgnoreCase))
                    {
                        key.DeleteValue(InstallRootValueName, throwOnMissingValue: false);
                    }
                }
            }
            catch (Exception error) when (
                error is SecurityException ||
                error is UnauthorizedAccessException ||
                error is IOException)
            {
                // A stale hint is harmless; a future discovery will validate the
                // marker before using it.
            }
        }

        internal static void Install(string installDirectory, bool createStartup)
        {
            var root = NormalizeInstallRoot(installDirectory);
            var parent = Directory.GetParent(root).FullName;
            Directory.CreateDirectory(parent);
            var previous = ReadMarkerIfPresent(root);
            ValidateExistingDirectory(root, previous);
            StopOwnedProcesses(root);

            var staging = Path.Combine(parent, ".termlink-install-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(parent, ".termlink-backup-" + Guid.NewGuid().ToString("N"));
            InstallMarker marker = null;
            var installSucceeded = false;
            try
            {
                Directory.CreateDirectory(staging);
                ExtractPayload(staging);

                if (Directory.Exists(root)) Directory.Move(root, backup);
                Directory.Move(staging, root);

                marker = new InstallMarker
                {
                    Product = Program.ProductId,
                    Version = typeof(Installer).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                    InstallRoot = root,
                    InstalledUtc = DateTime.UtcNow.ToString("O")
                };
                ConfigureTerminal(root, marker, previous);
                if (createStartup)
                {
                    try
                    {
                        ConfigureStartup(root, marker);
                    }
                    catch (Exception startupError) when (
                        startupError is UnauthorizedAccessException ||
                        startupError is System.Security.SecurityException)
                    {
                        // HKCU\...\Run can be locked down by enterprise policy. The
                        // application is still usable without auto-start; do not make
                        // the whole install fail for an optional convenience feature.
                        marker.StartupCommand = null;
                        Console.Error.WriteLine("登录启动项未配置（当前用户注册表权限不足）。");
                    }
                }
                else
                {
                    TryRemoveStartupIfOwned(previous);
                }
                ConfigureShortcuts(root, marker, previous);
                ConfigureUninstaller(root, marker);
                WriteMarker(root, marker);
                RememberInstallDirectory(root);

                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                installSucceeded = true;
                Console.WriteLine("TermLink 已安装到: " + root);
                Console.WriteLine("Windows Terminal 配置: " + marker.TerminalFragmentPath);
            }
            catch
            {
                if (marker != null)
                {
                    // Cleanup must never replace the original installer error. In
                    // particular, the marker is intentionally incomplete while a
                    // fragment or startup value is being written.
                    TryRemoveTerminalFragmentIfOwned(marker);
                    TryRemoveStartupIfOwned(marker);
                    TryRemoveShortcuts(marker);
                    TryRemoveUninstallerIfOwned(marker);
                }
                TryDeleteDirectory(root);
                TryRestoreDirectory(backup, root);
                throw;
            }
            finally
            {
                TryDeleteDirectory(staging);
                if (installSucceeded) TryDeleteDirectory(backup);
            }
        }

        internal static void Uninstall(string installDirectory)
        {
            var root = NormalizeInstallRoot(installDirectory);
            var marker = ReadMarkerIfPresent(root);
            if (marker == null || !string.Equals(marker.Product, Program.ProductId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("指定目录不是 TermLink 安装目录，已拒绝删除: " + root);
            }

            StopOwnedProcesses(root);
            RemoveTerminalFragmentIfOwned(marker);
            RemoveStartupIfOwned(marker);
            RemoveShortcuts(marker);
            Directory.Delete(root, true);
            ForgetInstallDirectory(root);
            Console.WriteLine("TermLink 已卸载: " + root);
        }

        internal static int LaunchInstalledUninstaller(string installDirectory = null)
        {
            var source = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                throw new InvalidOperationException("卸载程序路径不可用。");
            }

            var root = NormalizeInstallRoot(installDirectory ?? Path.GetDirectoryName(source));
            var temporaryCopy = Path.Combine(
                Path.GetTempPath(), "TermLink-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(source, temporaryCopy, overwrite: false);
            var start = new ProcessStartInfo
            {
                FileName = temporaryCopy,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--uninstall");
            start.ArgumentList.Add(root);
            start.ArgumentList.Add("--cleanup-self");
            start.ArgumentList.Add(temporaryCopy);
            start.ArgumentList.Add("--quiet");
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("卸载程序无法启动清理进程。");
            }
            return 0;
        }

        internal static void ScheduleSelfDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var command = "/d /c timeout /t 2 /nobreak >nul & del /f /q \"" +
                    path.Replace("\"", "\"\"") + "\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = command,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                })?.Dispose();
            }
            catch
            {
                // The temporary copy is harmless if the best-effort cleanup
                // process cannot be started.
            }
        }

        private static string NormalizeInstallRoot(string installDirectory)
        {
            var root = Path.GetFullPath(string.IsNullOrWhiteSpace(installDirectory)
                ? DefaultInstallDirectory
                : installDirectory.Trim());
            var directoryRoot = Path.GetPathRoot(root);
            if (string.Equals(root.TrimEnd(Path.DirectorySeparatorChar),
                    directoryRoot.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("不能把磁盘根目录作为 TermLink 安装目录。");
            }
            return root.TrimEnd(Path.DirectorySeparatorChar);
        }

        private static void ValidateExistingDirectory(string root, InstallMarker previous)
        {
            if (!Directory.Exists(root)) return;
            if (previous != null) return;
            if (Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new InvalidOperationException(
                    "安装目录已存在且不是 TermLink 目录，为避免覆盖用户文件，请选择空目录或已有 TermLink 目录: " + root);
            }
        }

        private static InstallMarker ReadMarkerIfPresent(string root)
        {
            try
            {
                var normalizedRoot = NormalizeInstallRoot(root);
                var path = Path.Combine(normalizedRoot, MarkerName);
                if (!File.Exists(path) || !Directory.Exists(normalizedRoot)) return null;
                var marker = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path, Encoding.UTF8));
                if (marker == null || !string.Equals(marker.Product, Program.ProductId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(marker.InstallRoot)) return null;
                var declaredRoot = NormalizeInstallRoot(marker.InstallRoot);
                if (!string.Equals(declaredRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return null;
                foreach (var fileName in RequiredPayloadFiles)
                {
                    if (!File.Exists(Path.Combine(normalizedRoot, fileName))) return null;
                }
                return marker;
            }
            catch (Exception error) when (
                error is ArgumentException ||
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is JsonException ||
                error is NotSupportedException)
            {
                return null;
            }
        }

        private static void WriteMarker(string root, InstallMarker marker)
        {
            var path = Path.Combine(root, MarkerName);
            File.WriteAllText(path,
                JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                new UTF8Encoding(false));
        }

        private static void ExtractPayload(string destination)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(Program.PayloadResourceName))
            {
                if (stream == null) throw new InvalidOperationException("安装器没有内嵌 payload。");
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    foreach (var entry in archive.Entries)
                    {
                        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        var target = Path.GetFullPath(Path.Combine(destination, relative));
                        var prefix = destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("安装器 payload 包含越界路径: " + entry.FullName);
                        }
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open())
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                        }
                    }
                }
            }
        }

        private static void StopOwnedProcesses(string root)
        {
            var tray = Path.Combine(root, "TermLink.Tray.exe");
            if (File.Exists(tray))
            {
                try
                {
                    using (var shutdown = Process.Start(new ProcessStartInfo
                    {
                        FileName = tray,
                        Arguments = "--shutdown-agent",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }))
                    {
                        shutdown?.WaitForExit(10000);
                    }
                }
                catch
                {
                    // The process sweep below remains scoped to this install root.
                }
            }

            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var name in OwnedProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId) continue;
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) &&
                            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            process.Kill(true);
                            process.WaitForExit(5000);
                        }
                    }
                    catch
                    {
                        // A process that cannot be inspected is never killed by this installer.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
        }

        private static void ConfigureTerminal(string root, InstallMarker marker, InstallMarker previous)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var directory = Path.Combine(localAppData, "Microsoft", "Windows Terminal", "Fragments", "TermLink");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FragmentFileName);
            if (File.Exists(path))
            {
                var ownsExisting = previous != null &&
                    !string.IsNullOrWhiteSpace(previous.TerminalFragmentPath) &&
                    string.Equals(Path.GetFullPath(previous.TerminalFragmentPath),
                        Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Sha256(path), previous.TerminalFragmentSha256, StringComparison.OrdinalIgnoreCase);
                ownsExisting = ownsExisting || IsRecognizedTermLinkFragment(path);
                if (!ownsExisting)
                {
                    throw new InvalidOperationException(
                        "TermLink Terminal Fragment 已存在但不属于本安装器，为避免覆盖用户配置，请先处理: " + path);
                }
            }
            var sessionHost = Path.Combine(root, "TermLink.SessionHost.exe");
            var profiles = new List<Dictionary<string, object>>
            {
                CreateProfile("{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}", "TermLink CMD", "cmd.exe", "command-prompt"),
                CreateProfile("{b2025574-4cc8-5264-b577-7c6532eff95a}", "TermLink Windows PowerShell", "powershell.exe -NoLogo -NoProfile", "windows-powershell")
            };
            if (FindOnPath("pwsh.exe") != null)
            {
                profiles.Add(CreateProfile("{7cf2d180-c8be-5b08-99a0-268bd2dc1d6f}", "TermLink PowerShell 7", "pwsh.exe -NoLogo -NoProfile", "powershell-7"));
            }
            if (FindOnPath("wsl.exe") != null)
            {
                profiles.Add(CreateProfile("{f5652f1d-a6da-5199-b595-3a1255ce04b4}", "TermLink WSL", "wsl.exe", "wsl"));
            }

            foreach (var profile in profiles)
            {
                // CreateProfile temporarily stores the child command in the private marker below.
                var childCommand = (string)profile["__child"];
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(childCommand));
                profile["commandline"] = Quote(sessionHost) + " --command-base64 " + encoded + " --managed-shell " + profile["__shell"];
                profile.Remove("__child");
                profile.Remove("__shell");
            }

            var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["profiles"] = profiles },
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            try
            {
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch (UnauthorizedAccessException error)
            {
                throw new InvalidOperationException(
                    "无法写入 Windows Terminal Fragment：" + path +
                    "。请确认当前用户对该目录有写入权限，并关闭占用该文件的编辑器后重试。",
                    error);
            }
            marker.TerminalFragmentPath = path;
            marker.TerminalFragmentSha256 = Sha256(path);
        }

        private static Dictionary<string, object> CreateProfile(string guid, string name, string childCommand, string shell)
        {
            return new Dictionary<string, object>
            {
                ["guid"] = guid,
                ["name"] = name,
                ["commandline"] = string.Empty,
                ["startingDirectory"] = "%USERPROFILE%",
                ["hidden"] = false,
                ["__child"] = childCommand,
                ["__shell"] = shell
            };
        }

        private static bool IsRecognizedTermLinkFragment(string path)
        {
            try
            {
                using (var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
                {
                    if (!document.RootElement.TryGetProperty("profiles", out var profiles) ||
                        profiles.ValueKind != JsonValueKind.Array || profiles.GetArrayLength() == 0)
                    {
                        return false;
                    }
                    var knownGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}",
                        "{b2025574-4cc8-5264-b577-7c6532eff95a}",
                        "{7cf2d180-c8be-5b08-99a0-268bd2dc1d6f}",
                        "{f5652f1d-a6da-5199-b595-3a1255ce04b4}"
                    };
                    foreach (var profile in profiles.EnumerateArray())
                    {
                        if (!profile.TryGetProperty("guid", out var guid) ||
                            !knownGuids.Contains(guid.GetString() ?? string.Empty)) return false;
                    }
                    return true;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string FindOnPath(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void ConfigureStartup(string root, InstallMarker marker)
        {
            var tray = Path.Combine(root, "TermLink.Tray.exe");
            var command = Quote(tray);
            using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            {
                key.SetValue(Program.RunValueName, command, RegistryValueKind.String);
            }
            marker.StartupCommand = command;
        }

        private static void RemoveStartupIfOwned(InstallMarker marker)
        {
            if (marker == null || string.IsNullOrWhiteSpace(marker.StartupCommand)) return;
            using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
            {
                var value = key?.GetValue(Program.RunValueName) as string;
                if (string.Equals(value, marker.StartupCommand, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(Program.RunValueName, false);
                }
            }
        }

        private static void ConfigureShortcuts(string root, InstallMarker marker, InstallMarker previous)
        {
            var tray = Path.Combine(root, "TermLink.Tray.exe");
            var desktopDefault = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "TermLink.lnk");
            var startMenuDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "TermLink");
            var startMenuDefault = Path.Combine(startMenuDirectory, "TermLink.lnk");

            marker.DesktopShortcutPath = ResolveShortcutPath(
                desktopDefault, previous?.DesktopShortcutPath, previous?.DesktopShortcutSha256);
            marker.StartMenuShortcutPath = ResolveShortcutPath(
                startMenuDefault, previous?.StartMenuShortcutPath, previous?.StartMenuShortcutSha256);

            CreateShortcut(marker.DesktopShortcutPath, tray, root);
            marker.DesktopShortcutSha256 = Sha256(marker.DesktopShortcutPath);
            CreateShortcut(marker.StartMenuShortcutPath, tray, root);
            marker.StartMenuShortcutSha256 = Sha256(marker.StartMenuShortcutPath);
        }

        private static void ConfigureUninstaller(string root, InstallMarker marker)
        {
            var source = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
            var sourceName = Path.GetFileName(source);
            if (!sourceName.StartsWith("TermLink-Setup-", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sourceName, "TermLink.Setup.exe", StringComparison.OrdinalIgnoreCase))
            {
                // A developer invoking the installer through `dotnet` has no
                // distributable setup EXE to copy. Official single-file builds
                // always take one of the two names above.
                return;
            }

            var destination = Path.Combine(root, UninstallerFileName);
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                return;
            File.Copy(source, destination, overwrite: true);
            marker.UninstallerPath = destination;
            marker.UninstallerSha256 = Sha256(destination);
        }

        private static string ResolveShortcutPath(string defaultPath, string previousPath, string previousSha256)
        {
            if (!string.IsNullOrWhiteSpace(previousPath) &&
                !string.IsNullOrWhiteSpace(previousSha256) &&
                File.Exists(previousPath) &&
                string.Equals(Sha256(previousPath), previousSha256, StringComparison.OrdinalIgnoreCase))
            {
                return previousPath;
            }

            if (!File.Exists(defaultPath)) return defaultPath;
            var directory = Path.GetDirectoryName(defaultPath);
            var stem = Path.GetFileNameWithoutExtension(defaultPath);
            var extension = Path.GetExtension(defaultPath);
            for (var suffix = 2; suffix <= 100; suffix++)
            {
                var candidate = Path.Combine(directory, stem + " (" + suffix + ")" + extension);
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException("无法为 TermLink 创建快捷方式：同名文件过多。请清理旧的 TermLink 快捷方式后重试。");
        }

        private static void CreateShortcut(string path, string target, string workingDirectory)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            object shell = null;
            object shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    throw new PlatformNotSupportedException("当前 Windows 环境缺少快捷方式创建组件 WScript.Shell。");
                }
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { path });
                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty,
                    null, shortcut, new object[] { target });
                shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty,
                    null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty,
                    null, shortcut, new object[] { "TermLink Windows Agent" });
                shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty,
                    null, shortcut, new object[] { target + ",0" });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod,
                    null, shortcut, null);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new InvalidOperationException(
                    "无法创建 TermLink 快捷方式：" + path + "。请确认当前用户对该目录有写入权限。",
                    error);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void RemoveShortcuts(InstallMarker marker)
        {
            RemoveShortcutIfOwned(marker?.DesktopShortcutPath, marker?.DesktopShortcutSha256);
            RemoveShortcutIfOwned(marker?.StartMenuShortcutPath, marker?.StartMenuShortcutSha256);
            var startMenuDirectory = marker?.StartMenuShortcutPath == null
                ? null
                : Path.GetDirectoryName(marker.StartMenuShortcutPath);
            if (!string.IsNullOrWhiteSpace(startMenuDirectory) &&
                Directory.Exists(startMenuDirectory) &&
                !Directory.EnumerateFileSystemEntries(startMenuDirectory).Any())
            {
                Directory.Delete(startMenuDirectory);
            }
        }

        private static void RemoveShortcutIfOwned(string path, string sha256)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha256) || !File.Exists(path)) return;
            if (string.Equals(Sha256(path), sha256, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }

        private static void TryRemoveTerminalFragmentIfOwned(InstallMarker marker)
        {
            try
            {
                RemoveTerminalFragmentIfOwned(marker);
            }
            catch
            {
                // Preserve the original install error. The diagnostic log contains
                // the original stack and is the source of truth for cleanup issues.
            }
        }

        private static void TryRemoveStartupIfOwned(InstallMarker marker)
        {
            try
            {
                RemoveStartupIfOwned(marker);
            }
            catch
            {
                // Preserve the original install error; startup cleanup is best effort.
            }
        }

        private static void TryRemoveShortcuts(InstallMarker marker)
        {
            try
            {
                RemoveShortcuts(marker);
            }
            catch
            {
                // Preserve the original install error; owned shortcuts can be retried on uninstall.
            }
        }

        private static void TryRemoveUninstallerIfOwned(InstallMarker marker)
        {
            try
            {
                if (marker == null || string.IsNullOrWhiteSpace(marker.UninstallerPath) ||
                    string.IsNullOrWhiteSpace(marker.UninstallerSha256) ||
                    !File.Exists(marker.UninstallerPath)) return;
                if (string.Equals(Sha256(marker.UninstallerPath), marker.UninstallerSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(marker.UninstallerPath);
                }
            }
            catch
            {
                // Preserve the original install error; the incomplete root is
                // already scheduled for cleanup by the surrounding rollback.
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Preserve the original install error; an interrupted cleanup can
                // be retried safely by the next installation attempt.
            }
        }

        private static void TryRestoreDirectory(string backup, string root)
        {
            try
            {
                if (Directory.Exists(backup) && !Directory.Exists(root))
                {
                    Directory.Move(backup, root);
                }
            }
            catch
            {
                // Preserve the original install error. The backup remains visible
                // for manual recovery instead of being deleted implicitly.
            }
        }

        private static void RemoveTerminalFragmentIfOwned(InstallMarker marker)
        {
            if (marker == null || string.IsNullOrWhiteSpace(marker.TerminalFragmentPath) ||
                string.IsNullOrWhiteSpace(marker.TerminalFragmentSha256)) return;
            var expectedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows Terminal", "Fragments", "TermLink", FragmentFileName);
            if (!string.Equals(
                    Path.GetFullPath(marker.TerminalFragmentPath ?? string.Empty),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(expectedPath)) return;
            if (string.Equals(Sha256(expectedPath), marker.TerminalFragmentSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(expectedPath);
                var directory = Path.GetDirectoryName(expectedPath);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
        }
    }
}
