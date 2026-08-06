using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                var options = SetupArguments.Parse(args);
                if (options.Help)
                {
                    Console.WriteLine(SetupArguments.Usage);
                    return 0;
                }

                if (options.Uninstall)
                {
                    var root = options.InstallDirectory ?? Installer.DefaultInstallDirectory;
                    Installer.Uninstall(root);
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
                try
                {
                    File.AppendAllText(
                        Path.Combine(Path.GetTempPath(), "TermLink-Setup-error.log"),
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
                        error.Message,
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
    }

    internal static class Installer
    {
        private const string MarkerName = ".termlink-install.json";
        private const string FragmentFileName = "termlink.json";
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
                if (createStartup) ConfigureStartup(root, marker);
                else RemoveStartupIfOwned(previous);
                WriteMarker(root, marker);

                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                Console.WriteLine("TermLink 已安装到: " + root);
                Console.WriteLine("Windows Terminal 配置: " + marker.TerminalFragmentPath);
            }
            catch
            {
                if (marker != null)
                {
                    RemoveTerminalFragmentIfOwned(marker);
                    RemoveStartupIfOwned(marker);
                }
                if (Directory.Exists(root)) Directory.Delete(root, true);
                if (Directory.Exists(backup)) Directory.Move(backup, root);
                throw;
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
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
            Directory.Delete(root, true);
            Console.WriteLine("TermLink 已卸载: " + root);
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
            var path = Path.Combine(root, MarkerName);
            if (!File.Exists(path)) return null;
            try
            {
                var marker = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path, Encoding.UTF8));
                if (marker == null || !string.Equals(marker.Product, Program.ProductId, StringComparison.Ordinal)) return null;
                return marker;
            }
            catch (JsonException)
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
            File.WriteAllText(path, json, new UTF8Encoding(false));
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

        private static void RemoveTerminalFragmentIfOwned(InstallMarker marker)
        {
            if (marker == null || string.IsNullOrWhiteSpace(marker.TerminalFragmentPath)) return;
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
