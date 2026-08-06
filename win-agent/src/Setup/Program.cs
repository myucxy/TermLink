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
        public string DesktopShortcutPath { get; set; }
        public string DesktopShortcutSha256 { get; set; }
        public string StartMenuShortcutPath { get; set; }
        public string StartMenuShortcutSha256 { get; set; }
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
                WriteMarker(root, marker);

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
