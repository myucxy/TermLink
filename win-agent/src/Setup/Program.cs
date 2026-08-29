using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using TermLink.Agent;
using TermLink.UpdateCore;
using TermLink.WindowsIntegration;

namespace TermLink.Setup
{
    internal static class Program
    {
        internal const string PayloadResourceName = "TermLink.Setup.Payload.zip";
        internal const string ProductId = "TermLink";
        internal const string RunValueName = "TermLink";
        internal const string AgentRunValueName = "TermLink.Agent";

        [STAThread]
        public static int Main(string[] args)
        {
            System.Windows.Forms.Application.SetHighDpiMode(
                System.Windows.Forms.HighDpiMode.PerMonitorV2);
            try
            {
                var invokedAsUninstaller = IsUninstallerExecutable();
                if (invokedAsUninstaller && args.Length == 0)
                {
                    DetachFromInheritedWorkingDirectory();
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
                        var delegatedRoot = string.IsNullOrWhiteSpace(options.InstallDirectory)
                            ? null
                            : Path.GetFullPath(options.InstallDirectory);
                        DetachFromInheritedWorkingDirectory();
                        return Installer.LaunchInstalledUninstaller(
                            delegatedRoot,
                            options.PurgeKnownRuntimeData);
                    }
                    // Resolve caller-supplied relative paths before detaching
                    // from an inherited install-root working directory.
                    var root = string.IsNullOrWhiteSpace(options.InstallDirectory)
                        ? Installer.DiscoverPreviousInstallDirectory()
                        : Path.GetFullPath(options.InstallDirectory);
                    var cleanupSelfPath = string.IsNullOrWhiteSpace(options.CleanupSelfPath)
                        ? null
                        : Path.GetFullPath(options.CleanupSelfPath);
                    DetachFromInheritedWorkingDirectory();
                    Installer.Uninstall(root, options.PurgeKnownRuntimeData);
                    Installer.ScheduleSelfDelete(cleanupSelfPath);
                    return 0;
                }

                if (!string.IsNullOrWhiteSpace(options.InstallDirectory))
                {
                    var installDirectory = Path.GetFullPath(options.InstallDirectory);
                    DetachFromInheritedWorkingDirectory();
                    if (!string.IsNullOrWhiteSpace(options.RemoteUpdateTransaction))
                    {
                        return RunRemoteUpdateInstall(options, installDirectory);
                    }
                    Installer.Install(installDirectory, options.CreateStartup, options.TerminateRunningProcesses);
                    if (!options.NoLaunch && !Installer.TryLaunchTray(installDirectory))
                    {
                        Console.Error.WriteLine(SetupText.Pick(
                            "安装已完成，但 TermLink 主程序未通过本地控制管道健康检查；请从开始菜单重试启动 TermLink。",
                            "Installation completed, but the TermLink app did not pass its local control-pipe health check. Try starting TermLink again from the Start menu."));
                        return 2;
                    }
                    return 0;
                }

                DetachFromInheritedWorkingDirectory();
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
                    Console.Error.WriteLine(SetupText.PublicError(error));
                    Console.Error.WriteLine(SetupText.DiagnosticRecorded());
                    return 1;
                }

                try
                {
                    System.Windows.Forms.MessageBox.Show(
                        SetupText.PublicError(error) + Environment.NewLine + Environment.NewLine +
                        SetupText.DiagnosticRecorded(),
                        SetupText.Pick("TermLink 安装程序", "TermLink Setup"),
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                }
                catch
                {
                    Console.Error.WriteLine(SetupText.PublicError(error));
                    Console.Error.WriteLine(SetupText.DiagnosticRecorded());
                }
                return 1;
            }
        }

        private static int RunRemoteUpdateInstall(SetupArguments options, string installDirectory)
        {
            var journalPath = Path.GetFullPath(options.RemoteUpdateTransaction);
            var transaction = RemoteUpdateTransactionStore.Load(journalPath);
            if (transaction.State != RemoteUpdateTransactionState.SetupRunning ||
                !string.Equals(Path.GetFullPath(transaction.InstallRoot), installDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(SetupText.Pick(
                    "远程更新事务未授权此安装目录。",
                    "The remote update transaction does not authorize this install root."));
            var setupVersion = typeof(Program).Assembly.GetName().Version ??
                throw new InvalidDataException(SetupText.Pick(
                    "无法读取安装程序程序集版本。",
                    "The Setup assembly version is unavailable."));
            if (WindowsUpdateVersion.GetBuild(setupVersion) != transaction.TargetBuild)
                throw new InvalidDataException(SetupText.Pick(
                    "远程更新事务目标与当前安装程序构建不匹配。",
                    "The remote update transaction target does not match this Setup build."));
            try
            {
                var outcome = Installer.Install(
                    installDirectory,
                    options.CreateStartup,
                    terminateRunningProcesses: true,
                    preserveBackupForExternalCommit: true);
                if (string.IsNullOrWhiteSpace(outcome.PreservedBackupPath) ||
                    !Directory.Exists(outcome.PreservedBackupPath))
                    throw new InvalidDataException(SetupText.Pick(
                        "远程更新未保留上一版本的安装备份。",
                        "The remote update did not preserve the previous installation backup."));
                transaction.BackupPath = outcome.PreservedBackupPath;
                transaction.State = RemoteUpdateTransactionState.AwaitingNewAgent;
                transaction.HealthDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(3);
                transaction.LastErrorCode = null;
                transaction.LastErrorDetail = null;
                RemoteUpdateTransactionStore.Save(journalPath, transaction);
                return 0;
            }
            catch (Exception error)
            {
                transaction.LastErrorCode = "SETUP_INSTALL_FAILED";
                transaction.LastErrorDetail = Installer.SafeDiagnostic(error);
                RemoteUpdateTransactionStore.Save(journalPath, transaction);
                throw;
            }
        }

        private static bool IsUninstallerExecutable()
        {
            var processPath = Environment.ProcessPath;
            return !string.IsNullOrWhiteSpace(processPath) &&
                string.Equals(Path.GetFileName(processPath), "TermLink.Uninstall.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static void DetachFromInheritedWorkingDirectory()
        {
            // An in-app update is commonly launched by TermLink.exe while that
            // process has the install root as its current directory. Inheriting
            // it can give Setup its own handle to the directory it must rename.
            // The installed uninstaller has the same self-lock risk.
            Directory.SetCurrentDirectory(Path.GetFullPath(Path.GetTempPath()));
        }
    }

    internal sealed class SetupArguments
    {
        internal static string Usage => SetupText.Pick(
            "TermLink 安装程序\n" +
            "  TermLink-Setup.exe                         打开安装向导\n" +
            "  TermLink-Setup.exe --install-dir <目录>     静默安装\n" +
            "  TermLink-Setup.exe --install-dir <目录> --no-startup\n" +
            "  TermLink-Setup.exe --install-dir <目录> --no-launch\n" +
            "  TermLink-Setup.exe --install-dir <目录> --terminate-running\n" +
            "  TermLink-Setup.exe --install-dir <目录> --terminate-running --no-launch --remote-update-transaction <journal>\n" +
            "  TermLink-Setup.exe --uninstall [<目录>]     卸载\n" +
            "  TermLink-Setup.exe --uninstall [<目录>] --purge-known-runtime-data\n" +
            "  TermLink-Setup.exe --help                   显示帮助",
            "TermLink Setup\n" +
            "  TermLink-Setup.exe                         Open the setup wizard\n" +
            "  TermLink-Setup.exe --install-dir <folder>  Install silently\n" +
            "  TermLink-Setup.exe --install-dir <folder> --no-startup\n" +
            "  TermLink-Setup.exe --install-dir <folder> --no-launch\n" +
            "  TermLink-Setup.exe --install-dir <folder> --terminate-running\n" +
            "  TermLink-Setup.exe --install-dir <folder> --terminate-running --no-launch --remote-update-transaction <journal>\n" +
            "  TermLink-Setup.exe --uninstall [<folder>]  Uninstall\n" +
            "  TermLink-Setup.exe --uninstall [<folder>] --purge-known-runtime-data\n" +
            "  TermLink-Setup.exe --help                   Show help");

        public string InstallDirectory { get; private set; }
        public bool Uninstall { get; private set; }
        public bool CreateStartup { get; private set; } = true;
        public bool PurgeKnownRuntimeData { get; private set; }
        public bool TerminateRunningProcesses { get; private set; }
        public bool NoLaunch { get; private set; }
        public bool Help { get; private set; }
        public string CleanupSelfPath { get; private set; }
        public string RemoteUpdateTransaction { get; private set; }

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
                    case "--purge-known-runtime-data":
                        result.PurgeKnownRuntimeData = true;
                        break;
                    case "--terminate-running":
                        result.TerminateRunningProcesses = true;
                        break;
                    case "--no-launch":
                        result.NoLaunch = true;
                        break;
                    case "--quiet":
                        break;
                    case "--cleanup-self":
                        result.CleanupSelfPath = Required(args, ref index, "--cleanup-self");
                        break;
                    case "--remote-update-transaction":
                        result.RemoteUpdateTransaction = Required(args, ref index, "--remote-update-transaction");
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        result.Help = true;
                        break;
                    default:
                        throw new ArgumentException(SetupText.Pick(
                            "未知参数: ",
                            "Unknown argument: ") + args[index]);
                }
            }

            if (result.Uninstall && args.Any(value => string.Equals(value, "--install-dir", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(SetupText.Pick(
                    "--uninstall 不能和 --install-dir 同时使用。",
                    "--uninstall cannot be used with --install-dir."));
            }
            if (result.PurgeKnownRuntimeData && !result.Uninstall)
            {
                throw new ArgumentException(SetupText.Pick(
                    "--purge-known-runtime-data 只能和 --uninstall 一起使用。",
                    "--purge-known-runtime-data can only be used with --uninstall."));
            }
            if (result.TerminateRunningProcesses &&
                (result.Uninstall || string.IsNullOrWhiteSpace(result.InstallDirectory)))
            {
                throw new ArgumentException(SetupText.Pick(
                    "--terminate-running 只能和 --install-dir 一起使用。",
                    "--terminate-running can only be used with --install-dir."));
            }
            if (result.NoLaunch &&
                (result.Uninstall || string.IsNullOrWhiteSpace(result.InstallDirectory)))
            {
                throw new ArgumentException(SetupText.Pick(
                    "--no-launch 只能和 --install-dir 一起使用。",
                    "--no-launch can only be used with --install-dir."));
            }
            if (!string.IsNullOrWhiteSpace(result.RemoteUpdateTransaction) &&
                (result.Uninstall || string.IsNullOrWhiteSpace(result.InstallDirectory) ||
                 !result.TerminateRunningProcesses || !result.NoLaunch))
            {
                throw new ArgumentException(SetupText.Pick(
                    "--remote-update-transaction 需要同时指定 --install-dir、--terminate-running 和 --no-launch。",
                    "--remote-update-transaction requires --install-dir, --terminate-running and --no-launch."));
            }
            return result;
        }

        private static string Required(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException(option + SetupText.Pick(
                    " 需要一个目录参数。",
                    " requires a folder argument."));
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
        public string TerminalIntegrationStatus { get; set; }
        public string TerminalIntegrationDiagnostic { get; set; }
        public string StartupCommand { get; set; }
        public string AgentStartupCommand { get; set; }
        public string DesktopShortcutPath { get; set; }
        public string DesktopShortcutSha256 { get; set; }
        public string StartMenuShortcutPath { get; set; }
        public string StartMenuShortcutSha256 { get; set; }
        public string UninstallerPath { get; set; }
        public string UninstallerSha256 { get; set; }
    }

    internal sealed class SetupPrerequisiteResult
    {
        internal SetupPrerequisiteResult(
            IReadOnlyList<string> errors,
            IReadOnlyList<string> warnings,
            bool windowsTerminalInstalled,
            string architectureDescription)
        {
            Errors = errors;
            Warnings = warnings;
            WindowsTerminalInstalled = windowsTerminalInstalled;
            ArchitectureDescription = architectureDescription;
        }

        public IReadOnlyList<string> Errors { get; }
        public IReadOnlyList<string> Warnings { get; }
        public bool WindowsTerminalInstalled { get; }
        public string ArchitectureDescription { get; }
        public bool Supported => Errors.Count == 0;
    }

    internal static class SetupPrerequisites
    {
        internal const int MinimumWindowsBuild = 17763;

        internal static SetupPrerequisiteResult Evaluate(
            bool isWindows,
            int windowsBuild,
            bool is64BitOperatingSystem,
            Architecture processArchitecture,
            bool createPseudoConsoleAvailable,
            bool windowsTerminalInstalled)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            if (!isWindows)
            {
                errors.Add(SetupText.Pick(
                    "TermLink Windows Agent 只能安装在 Windows 上。",
                    "TermLink Windows Agent can only be installed on Windows."));
            }
            else if (windowsBuild < MinimumWindowsBuild)
            {
                errors.Add(SetupText.Pick(
                    "Windows 版本过旧（当前 build " + windowsBuild +
                    "）。TermLink 需要 Windows 10 1809 / Server 2019 (build 17763) 或更高版本。",
                    "This Windows version is too old (current build " + windowsBuild +
                    "). TermLink requires Windows 10 1809 / Server 2019 (build 17763) or later."));
            }

            if (!is64BitOperatingSystem || processArchitecture != Architecture.X64)
            {
                errors.Add(SetupText.Pick(
                    "当前环境无法运行 TermLink x64 组件。" +
                    "需要 64 位 Windows 且安装程序必须以 x64 进程运行；当前进程架构为 " +
                    processArchitecture + "。",
                    "This environment cannot run the TermLink x64 components. " +
                    "A 64-bit Windows installation and an x64 Setup process are required. The current process architecture is " +
                    processArchitecture + "."));
            }

            if (!createPseudoConsoleAvailable)
            {
                errors.Add(SetupText.Pick(
                    "系统 kernel32.dll 未提供 CreatePseudoConsole (ConPTY)。" +
                    "请安装受支持的 Windows 更新后重试。",
                    "System kernel32.dll does not provide CreatePseudoConsole (ConPTY). " +
                    "Install a supported Windows update and try again."));
            }

            if (!windowsTerminalInstalled)
            {
                warnings.Add(SetupText.Pick(
                    "未检测到 Windows Terminal。TermLink Agent 仍可安装；" +
                    "安装程序不会自动安装、替换或修改 Windows Terminal。",
                    "Windows Terminal was not detected. TermLink Agent can still be installed. " +
                    "Setup will not install, replace, or modify Windows Terminal."));
            }

            return new SetupPrerequisiteResult(
                errors,
                warnings,
                windowsTerminalInstalled,
                "OS=" + RuntimeInformation.OSArchitecture + ", Process=" + processArchitecture);
        }

        internal static SetupPrerequisiteResult EvaluateCurrent()
        {
            return Evaluate(
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                Environment.OSVersion.Version.Build,
                Environment.Is64BitOperatingSystem,
                RuntimeInformation.ProcessArchitecture,
                HasCreatePseudoConsoleExport(),
                IsWindowsTerminalInstalled());
        }

        internal static SetupPrerequisiteResult EnsureSupported()
        {
            var result = EvaluateCurrent();
            if (!result.Supported)
            {
                throw new PlatformNotSupportedException(
                    SetupText.Pick(
                        "TermLink 安装前置检查未通过：",
                        "TermLink setup prerequisites were not met:") + Environment.NewLine +
                    string.Join(Environment.NewLine, result.Errors) + Environment.NewLine +
                    SetupText.Pick("架构详情: ", "Architecture details: ") + result.ArchitectureDescription);
            }
            return result;
        }

        private static bool HasCreatePseudoConsoleExport()
        {
            IntPtr module = IntPtr.Zero;
            try
            {
                if (!NativeLibrary.TryLoad("kernel32.dll", out module)) return false;
                return NativeLibrary.TryGetExport(module, "CreatePseudoConsole", out _);
            }
            catch (Exception error) when (
                error is DllNotFoundException ||
                error is EntryPointNotFoundException ||
                error is BadImageFormatException ||
                error is NotSupportedException)
            {
                return false;
            }
            finally
            {
                if (module != IntPtr.Zero) NativeLibrary.Free(module);
            }
        }

        private static bool IsWindowsTerminalInstalled()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe"),
                Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe"),
                Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe")
            };
            return candidates.Any(path => File.Exists(path) || Directory.Exists(path)) ||
                Installer.FindExecutableOnPath("wt.exe") != null;
        }
    }

    internal sealed class InstallOutcome
    {
        internal InstallOutcome(string terminalStatus, string terminalMessage, string terminalDiagnosticLogPath)
        {
            TerminalStatus = terminalStatus;
            TerminalMessage = terminalMessage;
            TerminalDiagnosticLogPath = terminalDiagnosticLogPath;
        }

        public string TerminalStatus { get; }
        public string TerminalMessage { get; }
        public string TerminalDiagnosticLogPath { get; }
        public string PreservedBackupPath { get; internal set; }
        public bool TerminalConfigured =>
            string.Equals(TerminalStatus, "configured", StringComparison.Ordinal) ||
            string.Equals(TerminalStatus, "configured-terminal-not-installed", StringComparison.Ordinal);
    }

    internal sealed class RunningTermLinkProcess
    {
        internal RunningTermLinkProcess(int processId, string name, string executablePath)
        {
            ProcessId = processId;
            Name = name ?? string.Empty;
            ExecutablePath = executablePath ?? string.Empty;
        }

        public int ProcessId { get; }
        public string Name { get; }
        public string ExecutablePath { get; }
    }

    internal sealed class RunningTermLinkService
    {
        internal RunningTermLinkService(string name, string displayName, string executablePath)
        {
            Name = name ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            ExecutablePath = executablePath ?? string.Empty;
        }

        public string Name { get; }
        public string DisplayName { get; }
        public string ExecutablePath { get; }
    }

    internal sealed class RunningTermLinkProcessesException : InvalidOperationException
    {
        internal RunningTermLinkProcessesException(IReadOnlyList<RunningTermLinkProcess> processes)
            : base(
                SetupText.Pick(
                    "检测到正在运行的 TermLink 程序，未获得终止授权，升级已取消：",
                    "Running TermLink processes were detected, but permission to stop them was not granted. The upgrade was cancelled:") +
                Environment.NewLine + Installer.FormatRunningProcessList(processes) +
                Environment.NewLine +
                SetupText.Pick(
                    "请使用安装向导确认终止，或在无人值守安装时显式传入 --terminate-running。",
                    "Confirm termination in the setup wizard, or explicitly pass --terminate-running for unattended installation."))
        {
        }
    }

    internal static class Installer
    {
        private const string MarkerName = ".termlink-install.json";
        private const string FragmentFileName = "termlink.json";
        private const string UninstallerFileName = "TermLink.Uninstall.exe";
        // WindowsAgentClient allows the independent Agent 65 seconds after the
        // Avalonia UI has initialized. Leave additional room for a cold UI start.
        internal static readonly TimeSpan LaunchHealthTimeout = TimeSpan.FromSeconds(90);
        private const int LaunchHealthConnectTimeoutMilliseconds = 1000;
        private const string InstallStateSubKey = @"Software\TermLink";
        private const string InstallRootValueName = "InstallRoot";
        private const uint DeleteAccess = 0x00010000;
        private const uint ShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint ProcessQueryLimitedInformation = 0x00001000;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        private static readonly string[] RequiredPayloadFiles =
        {
            "TermLink.exe",
            "TermLink.SessionHost.exe"
        };

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            uint flags,
            StringBuilder executableName,
            ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(
            IntPtr pipe,
            out uint serverProcessId);
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

        /// <summary>
        /// Returns the installation explicitly owned by current-user setup
        /// state. Unlike the broader wizard discovery, this intentionally does
        /// not scan common directories: a valid but abandoned test/backup copy
        /// must not prevent a new installation. Current installers persist the
        /// registry value, while the Terminal Fragment covers legacy installs.
        /// </summary>
        private static string DiscoverAuthoritativeInstallDirectory()
        {
            var candidates = new List<string>();
            AddCandidate(candidates, ReadRegisteredInstallDirectory());
            AddCandidate(candidates, ReadFragmentInstallDirectory());
            foreach (var candidate in candidates)
            {
                if (ReadMarkerIfPresent(candidate) != null) return candidate;
            }
            return null;
        }

        internal static void EnsureInstallDirectoryCompatible(string requestedInstallDirectory)
        {
            ValidateInstallDirectorySelection(
                requestedInstallDirectory,
                DiscoverAuthoritativeInstallDirectory());
        }

        internal static void ValidateInstallDirectorySelection(
            string requestedInstallDirectory,
            string activeInstallDirectory)
        {
            var requestedRoot = NormalizeInstallRoot(requestedInstallDirectory);
            if (string.IsNullOrWhiteSpace(activeInstallDirectory)) return;

            var activeRoot = NormalizeInstallRoot(activeInstallDirectory);
            if (string.Equals(requestedRoot, activeRoot, StringComparison.OrdinalIgnoreCase)) return;

            throw new InvalidOperationException(SetupText.Pick(
                "检测到 TermLink 已安装在：" + activeRoot + Environment.NewLine +
                "安装器不支持直接把现有安装迁移到其他目录，以免旧版本继续运行或新版本被误报为已启动。" +
                Environment.NewLine +
                "如需改用以下目录，请先从原目录运行 TermLink.Uninstall.exe 完成卸载，再重新安装：" +
                Environment.NewLine + requestedRoot,
                "TermLink is already installed at: " + activeRoot + Environment.NewLine +
                "Setup cannot migrate an existing installation directly to another folder because the old version could keep running or the new version could be reported as started incorrectly." +
                Environment.NewLine +
                "To use the following folder, first run TermLink.Uninstall.exe from the existing folder, then install again:" +
                Environment.NewLine + requestedRoot));
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

        internal static InstallOutcome Install(
            string installDirectory,
            bool createStartup,
            bool terminateRunningProcesses = false,
            CancellationToken cancellationToken = default,
            Func<bool> tryBeginCommit = null,
            bool preserveBackupForExternalCommit = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Run the platform checks before creating or moving any directory.
            // This keeps a rejected old/unsupported machine completely unchanged.
            var prerequisites = SetupPrerequisites.EnsureSupported();
            var root = NormalizeInstallRoot(installDirectory);
            // A cross-directory upgrade would leave the authoritative old root
            // installed and its global Tray/Agent identity could make the new
            // executable exit immediately. Reject it before creating even the
            // requested parent directory or stopping any process.
            EnsureInstallDirectoryCompatible(root);
            var parent = Directory.GetParent(root).FullName;
            Directory.CreateDirectory(parent);
            var previous = ReadMarkerIfPresent(root);
            ValidateExistingDirectory(root, previous);

            var staging = Path.Combine(parent, ".termlink-install-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(parent, ".termlink-backup-" + Guid.NewGuid().ToString("N"));
            InstallMarker marker = null;
            var previousTerminalFragment = ReadExistingTerminalFragment();
            var previousStartupCommand = ReadOwnedStartupCommand(previous);
            var previousAgentStartupCommand = ReadOwnedAgentStartupCommand(previous);
            var previousDesktopShortcut = ReadOwnedFile(
                previous?.DesktopShortcutPath,
                previous?.DesktopShortcutSha256);
            var previousStartMenuShortcut = ReadOwnedFile(
                previous?.StartMenuShortcutPath,
                previous?.StartMenuShortcutSha256);
            var installSucceeded = false;
            var oldRootMoved = false;
            var newRootActivated = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(staging);
                ExtractPayload(staging, cancellationToken);

                // Re-check immediately before the first mutation. A process may
                // have started while the payload was being staged.
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOwnedProcessesStopped(
                    root,
                    terminateRunningProcesses,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(root))
                {
                    MoveDirectoryWithRetry(
                        root,
                        backup,
                        SetupText.Pick(
                            "无法将旧版本移入安全备份目录",
                            "Could not move the old version into the safe backup folder"),
                        cancellationToken: cancellationToken);
                    oldRootMoved = true;
                }
                cancellationToken.ThrowIfCancellationRequested();
                MoveDirectoryWithRetry(
                    staging,
                    root,
                    SetupText.Pick(
                        "无法激活新版本安装目录",
                        "Could not activate the new version install folder"),
                    cancellationToken: cancellationToken);
                newRootActivated = true;
                cancellationToken.ThrowIfCancellationRequested();

                marker = new InstallMarker
                {
                    Product = Program.ProductId,
                    Version = typeof(Installer).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                    InstallRoot = root,
                    InstalledUtc = DateTime.UtcNow.ToString("O")
                };
                var terminalOutcome = ConfigureTerminalSafely(
                    root,
                    marker,
                    previous,
                    prerequisites.WindowsTerminalInstalled);
                cancellationToken.ThrowIfCancellationRequested();
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
                        marker.AgentStartupCommand = null;
                        Console.Error.WriteLine(SetupText.Pick(
                            "登录启动项未配置（当前用户注册表权限不足）。",
                            "Startup at sign-in was not configured because the current user lacks registry access."));
                    }
                }
                else
                {
                    TryRemoveStartupIfOwned(previous);
                }
                ConfigureShortcuts(root, marker, previous);
                ConfigureUninstaller(root, marker);
                cancellationToken.ThrowIfCancellationRequested();
                if (tryBeginCommit != null && !tryBeginCommit())
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                WriteMarker(root, marker);
                RememberInstallDirectory(root);

                // The new root is now complete and authoritative. Backup cleanup
                // is post-commit best effort: a locked file in the old backup
                // must never trigger rollback that deletes the working new root.
                installSucceeded = true;
                if (!preserveBackupForExternalCommit) TryDeleteDirectory(backup);
                if (!preserveBackupForExternalCommit && Directory.Exists(backup))
                {
                    Console.Error.WriteLine(
                        SetupText.Pick(
                            "旧版本备份暂时无法清理，已保留以避免数据损坏: ",
                            "The old-version backup could not be cleaned up and was retained to avoid data loss: ") + backup);
                }
                Console.WriteLine(SetupText.Pick("TermLink 已安装到: ", "TermLink was installed to: ") + root);
                if (terminalOutcome.TerminalConfigured)
                {
                    Console.WriteLine(SetupText.Pick(
                        "Windows Terminal 配置: ",
                        "Windows Terminal configuration: ") + marker.TerminalFragmentPath);
                }
                else
                {
                    Console.Error.WriteLine(terminalOutcome.TerminalMessage);
                    if (!string.IsNullOrWhiteSpace(terminalOutcome.TerminalDiagnosticLogPath))
                    {
                        Console.Error.WriteLine(SetupText.DiagnosticRecorded());
                    }
                }
                if (preserveBackupForExternalCommit) terminalOutcome.PreservedBackupPath = backup;
                return terminalOutcome;
            }
            catch (Exception installError)
            {
                if (installSucceeded)
                {
                    // The transaction already committed. Never roll back to a
                    // backup that may have been partially cleaned.
                    throw;
                }
                if (marker != null)
                {
                    // Cleanup must never replace the original installer error. In
                    // particular, the marker is intentionally incomplete while a
                    // fragment or startup value is being written.
                    TryRestoreTerminalFragmentAfterRollback(
                        marker,
                        previousTerminalFragment);
                    TryRemoveStartupIfOwned(marker);
                    TryRemoveShortcuts(marker);
                    TryRemoveUninstallerIfOwned(marker);
                }
                TryRestoreStartupAfterRollback(previousStartupCommand);
                TryRestoreAgentStartupAfterRollback(previousAgentStartupCommand);
                TryRestoreOwnedFileAfterRollback(
                    previous?.DesktopShortcutPath,
                    previousDesktopShortcut);
                TryRestoreOwnedFileAfterRollback(
                    previous?.StartMenuShortcutPath,
                    previousStartMenuShortcut);
                // Never delete `root` unless this invocation successfully moved
                // the new staged payload there. In particular, a lock that makes
                // the old-root move fail must leave the previous install intact.
                if (oldRootMoved)
                {
                    try
                    {
                        RestorePreviousInstallRootAfterFailure(backup, root, parent);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new InvalidOperationException(SetupText.Pick(
                            "升级失败，且无法自动把旧版本恢复到原目录。" +
                            "旧版本备份仍保留在: " + backup + Environment.NewLine +
                            "请不要删除该目录，并根据详细日志手动恢复。",
                            "The upgrade failed and the previous version could not be restored automatically to its original folder. " +
                            "The old-version backup remains at: " + backup + Environment.NewLine +
                            "Do not delete that folder. Use the detailed log to restore it manually."),
                            new AggregateException(installError, rollbackError));
                    }
                }
                else if (newRootActivated)
                {
                    TryDeleteDirectory(root);
                    if (Directory.Exists(root))
                    {
                        throw new InvalidOperationException(SetupText.Pick(
                            "安装已取消，但无法清理尚未提交的新版本目录: " + root +
                            Environment.NewLine +
                            "该目录可能仍被防病毒软件、索引器或文件预览器占用，请关闭占用后手动删除。",
                            "Installation was cancelled, but the uncommitted new-version folder could not be removed: " + root +
                            Environment.NewLine +
                            "Antivirus software, an indexer, or a file previewer may still be using this folder. Close the owner and delete it manually."),
                            installError);
                    }
                }
                throw;
            }
            finally
            {
                TryDeleteDirectory(staging);
                if (installSucceeded && !preserveBackupForExternalCommit) TryDeleteDirectory(backup);
            }
        }

        internal static string SafeDiagnostic(Exception error)
        {
            var value = (error?.GetType().Name ?? "Exception") + ": " +
                (error?.Message ?? SetupText.Pick("未知错误", "Unknown error"));
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 1000 ? value : value.Substring(0, 1000);
        }

        internal static void Uninstall(string installDirectory, bool purgeKnownRuntimeData = false)
        {
            var root = NormalizeInstallRoot(installDirectory);
            var marker = ReadMarkerIfPresent(root);
            if (marker == null || !string.Equals(marker.Product, Program.ProductId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(SetupText.Pick(
                    "指定目录不是 TermLink 安装目录，已拒绝删除: ",
                    "The selected folder is not a TermLink installation. Deletion was refused: ") + root);
            }

            EnsureOwnedProcessesStopped(root, terminateRunningProcesses: true);
            RemoveTerminalFragmentIfOwned(marker);
            RemoveStartupIfOwned(marker);
            RemoveShortcuts(marker);
            Directory.Delete(root, true);
            ForgetInstallDirectory(root);
            if (purgeKnownRuntimeData)
            {
                var deleted = PurgeKnownRuntimeDataFiles(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                Console.WriteLine(SetupText.Pick(
                    "已清理明确列入清单的 TermLink 运行数据文件: ",
                    "Deleted the explicitly allow-listed TermLink runtime data files: ") + deleted.Count);
            }
            else
            {
                Console.WriteLine(SetupText.Pick(
                    "TermLink 运行数据已保留；如需删除明确列入清单的数据，请使用 --purge-known-runtime-data。",
                    "TermLink runtime data was retained. Use --purge-known-runtime-data to delete the explicitly allow-listed files."));
            }
            Console.WriteLine(SetupText.Pick("TermLink 已卸载: ", "TermLink was uninstalled from: ") + root);
        }

        internal static int LaunchInstalledUninstaller(
            string installDirectory = null,
            bool purgeKnownRuntimeData = false)
        {
            var source = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                throw new InvalidOperationException(SetupText.Pick(
                    "卸载程序路径不可用。",
                    "The uninstaller path is unavailable."));
            }

            var root = NormalizeInstallRoot(installDirectory ?? Path.GetDirectoryName(source));
            var temporaryCopy = Path.Combine(
                Path.GetTempPath(), "TermLink-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(source, temporaryCopy, overwrite: false);
            var start = new ProcessStartInfo
            {
                FileName = temporaryCopy,
                WorkingDirectory = Path.GetDirectoryName(temporaryCopy),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--uninstall");
            start.ArgumentList.Add(root);
            if (purgeKnownRuntimeData) start.ArgumentList.Add("--purge-known-runtime-data");
            start.ArgumentList.Add("--cleanup-self");
            start.ArgumentList.Add(temporaryCopy);
            start.ArgumentList.Add("--quiet");
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException(SetupText.Pick(
                    "卸载程序无法启动清理进程。",
                    "The uninstaller could not start its cleanup process."));
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
                    WorkingDirectory = Path.GetTempPath(),
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
                throw new ArgumentException(SetupText.Pick(
                    "不能把磁盘根目录作为 TermLink 安装目录。",
                    "A drive root cannot be used as the TermLink install folder."));
            }
            return root.TrimEnd(Path.DirectorySeparatorChar);
        }

        private static void ValidateExistingDirectory(string root, InstallMarker previous)
        {
            if (!Directory.Exists(root)) return;
            if (previous != null) return;
            if (Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new InvalidOperationException(SetupText.Pick(
                    "安装目录已存在且不是 TermLink 目录，为避免覆盖用户文件，请选择空目录或已有 TermLink 目录: ",
                    "The install folder already exists and is not a TermLink folder. Choose an empty folder or an existing TermLink folder to avoid overwriting user files: ") + root);
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

        private static void ExtractPayload(
            string destination,
            CancellationToken cancellationToken)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(Program.PayloadResourceName))
            {
                if (stream == null) throw new InvalidOperationException(SetupText.Pick(
                    "安装器没有内嵌 payload。",
                    "Setup does not contain an embedded payload."));
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        var target = Path.GetFullPath(Path.Combine(destination, relative));
                        var prefix = destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(SetupText.Pick(
                                "安装器 payload 包含越界路径: ",
                                "The setup payload contains a path outside the install folder: ") + entry.FullName);
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
                            var buffer = new byte[128 * 1024];
                            int bytesRead;
                            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                output.Write(buffer, 0, bytesRead);
                            }
                        }
                    }
                }
            }
        }

        internal static IReadOnlyList<RunningTermLinkProcess> GetRunningOwnedProcesses(
            string installDirectory)
        {
            var root = NormalizeInstallRoot(installDirectory);
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var result = new List<RunningTermLinkProcess>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var path = TryGetProcessImagePath(process.Id);
                    if (!string.IsNullOrWhiteSpace(path) &&
                        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new RunningTermLinkProcess(
                            process.Id,
                            process.ProcessName,
                            path));
                    }
                }
                catch
                {
                    // A process that cannot be inspected is not claimed as
                    // TermLink-owned and is never terminated by this installer.
                }
                finally
                {
                    process.Dispose();
                }
            }
            return result.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.ProcessId)
                .ToArray();
        }

        internal static bool IsOwnedTrayRunning(string installDirectory)
        {
            var root = NormalizeInstallRoot(installDirectory);
            var trayPath = Path.Combine(root, "TermLink.exe");
            return GetRunningOwnedProcesses(root).Any(process =>
                string.Equals(
                    Path.GetFullPath(process.ExecutablePath),
                    trayPath,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal static string TryGetProcessImagePath(int processId)
        {
            if (processId <= 0) return null;
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue) return null;
            try
            {
                var capacity = 32768u;
                var path = new StringBuilder((int)capacity);
                if (!QueryFullProcessImageName(handle, 0, path, ref capacity)) return null;
                if (capacity == 0 || path.Length == 0) return null;
                try { return Path.GetFullPath(path.ToString()); }
                catch (Exception error) when (
                    error is ArgumentException ||
                    error is IOException ||
                    error is NotSupportedException) { return null; }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        internal static string FormatRunningProcessList(
            IReadOnlyList<RunningTermLinkProcess> processes)
        {
            if (processes == null || processes.Count == 0)
                return SetupText.Pick("（无）", "(none)");
            return string.Join(
                Environment.NewLine,
                processes.Select(value => "• " + value.Name + " (PID " + value.ProcessId + ")"));
        }

        internal static string FormatUpgradeTerminationPrompt(
            IReadOnlyList<RunningTermLinkProcess> processes,
            IReadOnlyList<RunningTermLinkService> services)
        {
            processes = processes ?? Array.Empty<RunningTermLinkProcess>();
            services = services ?? Array.Empty<RunningTermLinkService>();
            var processSummary = processes.Count == 0
                ? SetupText.Pick(
                    "• 当前未检测到；提交安装前仍会再次检查并终止随后启动的 TermLink 程序。",
                    "• None currently detected. Setup will check again before committing and stop any TermLink processes started later.")
                : FormatRunningProcessList(processes);
            var serviceSummary = services.Count == 0
                ? SetupText.Pick("• 无", "• None")
                : FormatRunningServiceList(services);
            return SetupText.Pick(
                "检测到已有 TermLink 安装。升级将关闭 TermLink 主程序、后台 Agent，以及所有由 TermLink SessionHost 承载的命令行会话。" +
                Environment.NewLine +
                "请先保存终端中未完成的工作；关闭后的会话无法恢复。" +
                Environment.NewLine + Environment.NewLine +
                "当前 TermLink 程序：" + Environment.NewLine + processSummary +
                Environment.NewLine + Environment.NewLine +
                "Windows 服务：" + Environment.NewLine + serviceSummary +
                Environment.NewLine + Environment.NewLine +
                "是否终止这些程序和会话并继续升级？",
                "An existing TermLink installation was found. The upgrade will stop the TermLink app, the background Agent, and all command-line sessions hosted by TermLink SessionHost." +
                Environment.NewLine +
                "Save any unfinished terminal work first. Closed sessions cannot be restored." +
                Environment.NewLine + Environment.NewLine +
                "Current TermLink processes:" + Environment.NewLine + processSummary +
                Environment.NewLine + Environment.NewLine +
                "Windows services:" + Environment.NewLine + serviceSummary +
                Environment.NewLine + Environment.NewLine +
                "Stop these processes and sessions and continue the upgrade?");
        }

        internal static IReadOnlyList<RunningTermLinkService> GetRunningOwnedServices(
            string installDirectory)
        {
            var root = NormalizeInstallRoot(installDirectory);
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var result = new List<RunningTermLinkService>();
            try
            {
                using var services = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services",
                    writable: false);
                if (services == null) return result;
                foreach (var name in services.GetSubKeyNames())
                {
                    using var service = services.OpenSubKey(name, writable: false);
                    var imagePath = service?.GetValue(
                        "ImagePath",
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    var executable = ParseServiceExecutablePath(imagePath);
                    if (string.IsNullOrWhiteSpace(executable) ||
                        !executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        !IsWindowsServiceRunning(name)) continue;
                    var displayName = service.GetValue("DisplayName") as string;
                    result.Add(new RunningTermLinkService(name, displayName, executable));
                }
            }
            catch (Exception error) when (
                error is SecurityException ||
                error is UnauthorizedAccessException ||
                error is IOException)
            {
                // Service discovery is best effort and never broadens ownership.
            }
            return result.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static string FormatRunningServiceList(
            IReadOnlyList<RunningTermLinkService> services)
        {
            if (services == null || services.Count == 0)
                return SetupText.Pick("（无）", "(none)");
            return string.Join(
                Environment.NewLine,
                services.Select(value => "• " +
                    (string.IsNullOrWhiteSpace(value.DisplayName) ? value.Name : value.DisplayName) +
                    " (" + value.Name + ")"));
        }

        internal static string ParseServiceExecutablePath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            var value = Environment.ExpandEnvironmentVariables(imagePath.Trim());
            string executable;
            if (value[0] == '"')
            {
                var end = value.IndexOf('"', 1);
                if (end <= 1) return null;
                executable = value.Substring(1, end - 1);
            }
            else
            {
                var end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end < 0) return null;
                executable = value.Substring(0, end + 4);
            }
            if (executable.StartsWith(@"\??\", StringComparison.Ordinal))
                executable = executable.Substring(4);
            if (!Path.IsPathFullyQualified(executable)) return null;
            try { return Path.GetFullPath(executable); }
            catch (Exception error) when (
                error is ArgumentException ||
                error is NotSupportedException ||
                error is IOException) { return null; }
        }

        private static bool IsWindowsServiceRunning(string name)
        {
            var result = RunServiceControl("query", name, 5000);
            return result.ExitCode == 0 &&
                System.Text.RegularExpressions.Regex.IsMatch(
                    result.Output,
                    @"STATE\s*:\s*4\b",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static (int ExitCode, string Output) RunServiceControl(
            string command,
            string name,
            int timeoutMilliseconds)
        {
            var executable = Path.Combine(Environment.SystemDirectory, "sc.exe");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add(command);
            process.StartInfo.ArgumentList.Add(name);
            if (!process.Start()) return (-1, string.Empty);
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(true); }
                catch { }
                return (-1, output);
            }
            return (process.ExitCode, output);
        }

        internal static void ValidateTerminationConsent(
            IReadOnlyList<RunningTermLinkProcess> processes,
            bool terminateRunningProcesses)
        {
            if (processes != null && processes.Count > 0 && !terminateRunningProcesses)
            {
                throw new RunningTermLinkProcessesException(processes);
            }
        }

        internal static void EnsureOwnedProcessesStopped(
            string root,
            bool terminateRunningProcesses,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var running = GetRunningOwnedProcesses(root);
            var runningServices = GetRunningOwnedServices(root);
            ValidateTerminationConsent(running, terminateRunningProcesses);
            if (runningServices.Count > 0 && !terminateRunningProcesses)
            {
                throw new InvalidOperationException(
                    SetupText.Pick(
                        "检测到正在运行的 TermLink Windows 服务，未获得终止授权，升级已取消：",
                        "Running TermLink Windows services were detected, but permission to stop them was not granted. The upgrade was cancelled:") +
                    Environment.NewLine + FormatRunningServiceList(runningServices));
            }
            if (running.Count == 0 && runningServices.Count == 0) return;

            foreach (var service in runningServices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RunServiceControl("stop", service.Name, 10000);
            }
            var serviceDeadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < serviceDeadline && GetRunningOwnedServices(root).Count > 0)
            {
                if (cancellationToken.WaitHandle.WaitOne(200))
                    cancellationToken.ThrowIfCancellationRequested();
            }
            var remainingServices = GetRunningOwnedServices(root);
            if (remainingServices.Count > 0)
            {
                throw new InvalidOperationException(
                    SetupText.Pick(
                        "无法停止以下 TermLink Windows 服务，升级未修改现有安装：",
                        "The following TermLink Windows services could not be stopped. The existing installation was not changed:") +
                    Environment.NewLine + FormatRunningServiceList(remainingServices));
            }

            var tray = Path.Combine(root, "TermLink.exe");
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
                        if (shutdown != null)
                        {
                            var shutdownDeadline = DateTime.UtcNow.AddSeconds(10);
                            while (!shutdown.WaitForExit(100) && DateTime.UtcNow < shutdownDeadline)
                                cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                }
                catch
                {
                    // The process sweep below remains scoped to this install root.
                }
            }

            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var terminationDiagnostics = new List<string>();
            var processDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < processDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = GetRunningOwnedProcesses(root);
                if (candidates.Count == 0) break;
                foreach (var candidate in candidates)
                {
                    try
                    {
                        using var process = Process.GetProcessById(candidate.ProcessId);
                        if (process.Id == Environment.ProcessId) continue;
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path) &&
                            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            // Setup can itself be a child of TermLink.exe when an
                            // in-app update launches the downloaded installer. A
                            // tree kill of that parent would therefore terminate
                            // this installer before it can replace the old files.
                            // Every executable under the validated install root is
                            // enumerated and stopped separately by this loop, so a
                            // single-process kill is both sufficient and scoped.
                            process.Kill(entireProcessTree: false);
                            var exitDeadline = DateTime.UtcNow.AddSeconds(5);
                            while (!process.WaitForExit(100) && DateTime.UtcNow < exitDeadline)
                                cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    catch (Exception error)
                    {
                        terminationDiagnostics.Add(
                            candidate.Name + " (PID " + candidate.ProcessId + "): " + error.Message);
                    }
                }
                if (cancellationToken.WaitHandle.WaitOne(100))
                    cancellationToken.ThrowIfCancellationRequested();
            }

            var remaining = GetRunningOwnedProcesses(root);
            if (remaining.Count > 0)
            {
                foreach (var candidate in remaining)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    terminationDiagnostics.Add(
                        TryTaskKillOwnedProcess(root, candidate, cancellationToken));
                }
                var taskKillDeadline = DateTime.UtcNow.AddSeconds(5);
                do
                {
                    if (cancellationToken.WaitHandle.WaitOne(100))
                        cancellationToken.ThrowIfCancellationRequested();
                    remaining = GetRunningOwnedProcesses(root);
                } while (remaining.Count > 0 && DateTime.UtcNow < taskKillDeadline);
            }
            if (remaining.Count > 0)
            {
                var distinctDiagnostics = terminationDiagnostics
                    .Distinct(StringComparer.Ordinal)
                    .Take(20)
                    .ToArray();
                var diagnostics = distinctDiagnostics.Length == 0
                    ? string.Empty
                    : Environment.NewLine + SetupText.Pick("终止诊断：", "Termination diagnostics:") + Environment.NewLine +
                        string.Join(
                            Environment.NewLine,
                            distinctDiagnostics.Select(value => "• " + value));
                throw new InvalidOperationException(
                    SetupText.Pick(
                        "无法终止以下 TermLink 程序，升级未修改现有安装：",
                        "The following TermLink processes could not be stopped. The existing installation was not changed:") + Environment.NewLine +
                    FormatRunningProcessList(remaining) + Environment.NewLine +
                    SetupText.Pick(
                        "请在任务管理器中结束这些 PID 后重试；若诊断为拒绝访问，请以管理员身份运行安装器。",
                        "End these PIDs in Task Manager and try again. If diagnostics report access denied, run Setup as administrator.") +
                    diagnostics);
            }
        }

        private static string TryTaskKillOwnedProcess(
            string root,
            RunningTermLinkProcess candidate,
            CancellationToken cancellationToken)
        {
            try
            {
                using var owned = Process.GetProcessById(candidate.ProcessId);
                // Keep the Process object alive until taskkill exits so this
                // verification and termination cannot silently target a PID
                // that was already recycled before the fallback began.
                _ = owned.SafeHandle;
                var path = TryGetProcessImagePath(candidate.ProcessId);
                var prefix = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (string.IsNullOrWhiteSpace(path) ||
                    !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.Name + " (PID " + candidate.ProcessId +
                        SetupText.Pick(
                            "): PID 已变化或不再属于当前安装目录，未调用 taskkill。",
                            "): the PID changed or no longer belongs to the current install folder; taskkill was not invoked.");
                }

                using var taskKill = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                taskKill.StartInfo.ArgumentList.Add("/PID");
                taskKill.StartInfo.ArgumentList.Add(candidate.ProcessId.ToString());
                // Do not use /T here. During an in-app update the downloaded
                // Setup process is a descendant of TermLink.exe; taskkill /T
                // would kill the installer together with the old application.
                // Remaining TermLink-owned descendants are discovered by path
                // and handled individually by the caller's bounded retry loop.
                taskKill.StartInfo.ArgumentList.Add("/F");
                taskKill.Start();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!taskKill.WaitForExit(100) && DateTime.UtcNow < deadline)
                {
                    if (!cancellationToken.IsCancellationRequested) continue;
                    try { taskKill.Kill(true); }
                    catch { }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (!taskKill.HasExited)
                {
                    try { taskKill.Kill(true); }
                    catch { }
                    return candidate.Name + " (PID " + candidate.ProcessId +
                        SetupText.Pick(
                            "): taskkill 等待超时。",
                            "): timed out waiting for taskkill.");
                }
                var output = (taskKill.StandardOutput.ReadToEnd() + " " +
                    taskKill.StandardError.ReadToEnd()).Trim();
                return candidate.Name + " (PID " + candidate.ProcessId +
                    "): taskkill exit " + taskKill.ExitCode +
                    (string.IsNullOrWhiteSpace(output) ? string.Empty : "，" + output);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return candidate.Name + " (PID " + candidate.ProcessId +
                    SetupText.Pick(
                        "): taskkill 兜底失败，",
                        "): taskkill fallback failed: ") + error.Message;
            }
        }

        internal static void MoveDirectoryWithRetry(
            string source,
            string destination,
            string operation,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException(SetupText.Pick("需要源目录。", "Source directory is required."), nameof(source));
            if (string.IsNullOrWhiteSpace(destination))
                throw new ArgumentException(SetupText.Pick("需要目标目录。", "Destination directory is required."), nameof(destination));

            var resolvedSource = Path.GetFullPath(source);
            var resolvedDestination = Path.GetFullPath(destination);
            var wait = timeout ?? TimeSpan.FromSeconds(30);
            if (wait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            var deadline = DateTime.UtcNow + wait;
            IOException lastError = null;
            var delayMilliseconds = 50;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Directory.Move(resolvedSource, resolvedDestination);
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    throw;
                }
                catch (IOException error)
                {
                    lastError = error;
                    if (DateTime.UtcNow >= deadline) break;
                    if (cancellationToken.WaitHandle.WaitOne(delayMilliseconds))
                        cancellationToken.ThrowIfCancellationRequested();
                    delayMilliseconds = Math.Min(delayMilliseconds + 50, 500);
                }
            }

            var blockers = new List<RunningTermLinkProcess>();
            if (Directory.Exists(resolvedSource))
                blockers.AddRange(GetRunningOwnedProcesses(resolvedSource));
            if (Directory.Exists(resolvedDestination))
                blockers.AddRange(GetRunningOwnedProcesses(resolvedDestination));
            var distinctBlockers = blockers
                .GroupBy(value => value.ProcessId)
                .Select(group => group.First())
                .ToArray();
            var details = distinctBlockers.Length == 0
                ? SetupText.Pick(
                    "未发现仍从相关目录运行的进程；可能是防病毒软件、索引器或文件预览器的临时句柄。",
                    "No process still running from the related folders was found. Antivirus software, an indexer, or a file previewer may hold a temporary handle.")
                : SetupText.Pick("仍在占用的进程：", "Processes still using the folder:") +
                    Environment.NewLine + FormatRunningProcessList(distinctBlockers);
            var blockedPaths = Directory.Exists(resolvedSource)
                ? FindDeleteBlockedPaths(resolvedSource, 12)
                : Array.Empty<string>();
            if (blockedPaths.Count > 0)
            {
                details += Environment.NewLine + SetupText.Pick(
                    "无法取得删除/改名访问权的路径：",
                    "Paths without delete/rename access:") +
                    Environment.NewLine + string.Join(
                        Environment.NewLine,
                        blockedPaths.Select(path => "• " + path));
            }
            var win32Error = lastError == null ? 0 : lastError.HResult & 0xFFFF;
            throw new IOException(
                (string.IsNullOrWhiteSpace(operation)
                    ? SetupText.Pick("无法移动安装目录", "Could not move the install folder")
                    : operation) + SetupText.Pick("。", ".") +
                Environment.NewLine + details + Environment.NewLine +
                SetupText.Pick("安装器已等待 ", "Setup waited ") + Math.Ceiling(wait.TotalSeconds) +
                SetupText.Pick(
                    " 秒且没有覆盖现有目录。请关闭相关程序后重试；若安全软件正在扫描，请等待扫描完成。",
                    " seconds without overwriting the existing folder. Close related programs and try again. If security software is scanning, wait for it to finish.") +
                Environment.NewLine + SetupText.Pick("底层错误: Win32 ", "Underlying error: Win32 ") + win32Error +
                " (0x" + win32Error.ToString("X") + SetupText.Pick(")，", "), ") + lastError?.Message,
                lastError);
        }

        internal static IReadOnlyList<string> FindDeleteBlockedPaths(
            string root,
            int maximumResults = 12)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException(SetupText.Pick("需要根目录。", "Root directory is required."), nameof(root));
            if (maximumResults <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumResults));
            var resolvedRoot = Path.GetFullPath(root);
            if (!Directory.Exists(resolvedRoot)) return Array.Empty<string>();

            var blocked = new List<string>();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFiles(
                        resolvedRoot,
                        "*",
                        SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateDirectories(
                        resolvedRoot,
                        "*",
                        SearchOption.AllDirectories))
                    .Concat(new[] { resolvedRoot })
                    .ToArray();
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException)
            {
                return new[]
                {
                    SetupText.Pick("<无法枚举安装目录: ", "<Could not enumerate the install folder: ") +
                    error.Message + ">"
                };
            }

            foreach (var path in entries)
            {
                var isDirectory = Directory.Exists(path);
                var handle = CreateFile(
                    path,
                    DeleteAccess,
                    ShareReadWriteDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    isDirectory ? FileFlagBackupSemantics : FileAttributeNormal,
                    IntPtr.Zero);
                if (handle != InvalidHandleValue)
                {
                    CloseHandle(handle);
                    continue;
                }

                var error = Marshal.GetLastWin32Error();
                if (error != 5 && error != 32 && error != 33) continue;
                var relative = Path.GetRelativePath(resolvedRoot, path);
                blocked.Add(
                    (string.Equals(relative, ".", StringComparison.Ordinal)
                        ? SetupText.Pick("<安装目录本身>", "<install folder itself>")
                        : relative) + " (Win32 " + error + ")");
                if (blocked.Count >= maximumResults) break;
            }
            return blocked;
        }

        internal static InstallOutcome ConfigureTerminalSafely(
            string root,
            InstallMarker marker,
            InstallMarker previous,
            bool windowsTerminalInstalled,
            string terminalFragmentPath = null)
        {
            try
            {
                ConfigureTerminal(root, marker, terminalFragmentPath);
                marker.TerminalIntegrationStatus = windowsTerminalInstalled
                    ? "configured"
                    : "configured-terminal-not-installed";
                marker.TerminalIntegrationDiagnostic = windowsTerminalInstalled
                    ? null
                    : SetupText.Pick(
                        "未检测到 Windows Terminal；TermLink 自有 Profile Fragment 已预先生成。" +
                        "以后安装 Windows Terminal 即可读取该配置，TermLink 没有安装、替换或修改 Terminal 本体。",
                        "Windows Terminal was not detected. TermLink's own profile fragment was generated in advance. " +
                        "Windows Terminal can read it after being installed later. TermLink did not install, replace, or modify Windows Terminal itself.");
                var message = windowsTerminalInstalled
                    ? SetupText.Pick(
                        "Windows Terminal Profile 已配置。",
                        "The Windows Terminal profile was configured.")
                    : marker.TerminalIntegrationDiagnostic;
                return new InstallOutcome(marker.TerminalIntegrationStatus, message, null);
            }
            catch (Exception error) when (
                error is InvalidOperationException ||
                error is UnauthorizedAccessException ||
                error is SecurityException ||
                error is IOException ||
                error is JsonException ||
                error is ArgumentException ||
                error is NotSupportedException)
            {
                var diagnosticPath = WriteTerminalDiagnostic(error);
                var message = SetupText.Pick(
                    "TermLink Agent 已安装，但可选的 Windows Terminal Profile 配置失败。" +
                    "安装程序没有修改 Windows Terminal 的 settings.json，也不会替换现有 Terminal。",
                    "TermLink Agent was installed, but the optional Windows Terminal profile could not be configured. " +
                    "Setup did not modify Windows Terminal settings.json and will not replace an existing Terminal.") +
                    Environment.NewLine + SetupText.PublicError(error);
                marker.TerminalIntegrationStatus = "failed";
                marker.TerminalIntegrationDiagnostic = message;
                CarryForwardTerminalOwnership(marker, previous);
                return new InstallOutcome("failed", message, diagnosticPath);
            }
        }

        private static void CarryForwardTerminalOwnership(
            InstallMarker marker,
            InstallMarker previous)
        {
            if (marker == null || previous == null) return;
            var expectedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows Terminal", "Fragments", "TermLink", FragmentFileName);
            if (!IsTerminalFragmentOwnedByMarker(expectedPath, previous)) return;
            marker.TerminalFragmentPath = expectedPath;
            marker.TerminalFragmentSha256 = previous.TerminalFragmentSha256;
        }

        private static string WriteTerminalDiagnostic(Exception error)
        {
            var path = Path.Combine(Path.GetTempPath(), "TermLink-Setup-terminal.log");
            try
            {
                File.AppendAllText(
                    path,
                    DateTime.UtcNow.ToString("O") + Environment.NewLine + error + Environment.NewLine,
                    new UTF8Encoding(false));
                return path;
            }
            catch
            {
                // The optional integration remains non-fatal when diagnostics
                // cannot be persisted.
                return null;
            }
        }

        private static void ConfigureTerminal(
            string root,
            InstallMarker marker,
            string terminalFragmentPath = null)
        {
            var result = string.IsNullOrWhiteSpace(terminalFragmentPath)
                ? WindowsTerminalIntegration.Reconfigure(root)
                : WindowsTerminalIntegration.Reconfigure(root, terminalFragmentPath);
            marker.TerminalFragmentPath = result.FragmentPath;
            marker.TerminalFragmentSha256 = result.Sha256;
        }

        internal static bool IsTerminalFragmentOwnedByMarker(string path, InstallMarker marker)
        {
            if (marker == null || string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(marker.TerminalFragmentPath) ||
                string.IsNullOrWhiteSpace(marker.TerminalFragmentSha256) ||
                !File.Exists(path)) return false;
            try
            {
                return string.Equals(
                        Path.GetFullPath(marker.TerminalFragmentPath),
                        Path.GetFullPath(path),
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Sha256(path),
                        marker.TerminalFragmentSha256,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is SecurityException ||
                error is ArgumentException ||
                error is NotSupportedException)
            {
                return false;
            }
        }

        internal static bool TryLaunchTray(string installDirectory)
        {
            try
            {
                var root = NormalizeInstallRoot(installDirectory);
                if (!IsInstalledLaunchIdentityValid(root)) return false;
                var tray = Path.Combine(root, "TermLink.exe");
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = tray,
                    WorkingDirectory = root,
                    UseShellExecute = false
                }))
                {
                    if (process == null) return false;
                    var deadline = DateTime.UtcNow.Add(LaunchHealthTimeout);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (process.HasExited)
                        {
                            // Avalonia uses a per-user single-instance coordinator.
                            // A secondary TermLink.exe can legitimately hand activation
                            // to the already-starting primary process and exit. Keep
                            // probing when that primary belongs to this installation.
                            if (!IsOwnedTrayRunning(root)) return false;
                        }
                        else
                        {
                            var launchedPath = TryGetProcessImagePath(process.Id);
                            if (!string.IsNullOrWhiteSpace(launchedPath) &&
                                !string.Equals(
                                    Path.GetFullPath(launchedPath),
                                    Path.GetFullPath(tray),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }
                        }
                        try
                        {
                            var response = SendHealthRequestToOwnedInstallation(
                                TermLinkControlPipe.DefaultName(),
                                root,
                                LaunchHealthConnectTimeoutMilliseconds);
                            if (response != null && response.Success) return true;
                        }
                        catch (Exception error) when (
                            error is IOException ||
                            error is TimeoutException ||
                            error is UnauthorizedAccessException)
                        {
                        }
                        Thread.Sleep(150);
                    }
                    // A process that merely remains alive is not sufficient. The
                    // authenticated control pipe must also be hosted by the exact
                    // TermLink.exe or TermLink.Agent.exe under this install root.
                    return false;
                }
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is InvalidOperationException ||
                error is BadImageFormatException ||
                error is SecurityException ||
                error is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        internal static bool IsInstalledLaunchIdentityValid(string installDirectory)
        {
            try
            {
                var root = NormalizeInstallRoot(installDirectory);
                var tray = Path.Combine(root, "TermLink.exe");
                var trayAssembly = Path.Combine(root, "TermLink.dll");
                var marker = ReadMarkerIfPresent(root);
                if (!File.Exists(tray) || !File.Exists(trayAssembly) || marker == null) return false;
                var installedVersion = AssemblyName.GetAssemblyName(trayAssembly).Version?.ToString();
                return !string.IsNullOrWhiteSpace(installedVersion) &&
                    string.Equals(installedVersion, marker.Version, StringComparison.Ordinal);
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is InvalidOperationException ||
                error is BadImageFormatException ||
                error is SecurityException ||
                error is ArgumentException ||
                error is NotSupportedException)
            {
                return false;
            }
        }

        internal static RegistryResponse SendHealthRequestToLaunchedTray(
            string pipeName,
            int launchedProcessId,
            int timeoutMilliseconds)
        {
            var secret = RegistryControlSecretStore.LoadDefault();
            try
            {
                return SendHealthRequestToLaunchedTray(
                    pipeName,
                    launchedProcessId,
                    timeoutMilliseconds,
                    secret);
            }
            finally
            {
                Array.Clear(secret, 0, secret.Length);
            }
        }

        internal static RegistryResponse SendHealthRequestToLaunchedTray(
            string pipeName,
            int launchedProcessId,
            int timeoutMilliseconds,
            byte[] authenticationSecret)
        {
            return SendAuthenticatedHealthRequest(
                pipeName,
                timeoutMilliseconds,
                authenticationSecret,
                serverProcessId =>
                {
                    if (serverProcessId == (uint)launchedProcessId) return true;
                    var launchedPath = TryGetProcessImagePath(launchedProcessId);
                    var serverPath = serverProcessId > int.MaxValue
                        ? null
                        : TryGetProcessImagePath((int)serverProcessId);
                    return !string.IsNullOrWhiteSpace(launchedPath) &&
                        !string.IsNullOrWhiteSpace(serverPath) &&
                        string.Equals(
                            Path.GetDirectoryName(Path.GetFullPath(launchedPath)),
                            Path.GetDirectoryName(Path.GetFullPath(serverPath)),
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            Path.GetFileName(serverPath),
                            "TermLink.Agent.exe",
                            StringComparison.OrdinalIgnoreCase);
                });
        }

        internal static RegistryResponse SendHealthRequestToOwnedInstallation(
            string pipeName,
            string installDirectory,
            int timeoutMilliseconds)
        {
            var secret = RegistryControlSecretStore.LoadDefault();
            try
            {
                return SendHealthRequestToOwnedInstallation(
                    pipeName,
                    installDirectory,
                    timeoutMilliseconds,
                    secret);
            }
            finally
            {
                Array.Clear(secret, 0, secret.Length);
            }
        }

        internal static RegistryResponse SendHealthRequestToOwnedInstallation(
            string pipeName,
            string installDirectory,
            int timeoutMilliseconds,
            byte[] authenticationSecret)
        {
            var root = NormalizeInstallRoot(installDirectory);
            return SendAuthenticatedHealthRequest(
                pipeName,
                timeoutMilliseconds,
                authenticationSecret,
                serverProcessId =>
                {
                    if (serverProcessId > int.MaxValue) return false;
                    return IsOwnedLaunchHealthServerPath(
                        TryGetProcessImagePath((int)serverProcessId),
                        root);
                });
        }

        internal static bool IsOwnedLaunchHealthServerPath(
            string executablePath,
            string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(executablePath) ||
                string.IsNullOrWhiteSpace(installDirectory)) return false;
            try
            {
                var root = NormalizeInstallRoot(installDirectory);
                var candidate = Path.GetFullPath(executablePath);
                return string.Equals(
                        candidate,
                        Path.Combine(root, "TermLink.exe"),
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        candidate,
                        Path.Combine(root, "TermLink.Agent.exe"),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (
                error is ArgumentException ||
                error is IOException ||
                error is NotSupportedException ||
                error is SecurityException)
            {
                return false;
            }
        }

        private static RegistryResponse SendAuthenticatedHealthRequest(
            string pipeName,
            int timeoutMilliseconds,
            byte[] authenticationSecret,
            Func<uint, bool> isExpectedServer)
        {
            using (var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None))
            {
                pipe.Connect(timeoutMilliseconds);
                if (!GetNamedPipeServerProcessId(
                        pipe.SafePipeHandle.DangerousGetHandle(),
                        out var serverProcessId) ||
                    !isExpectedServer(serverProcessId)) return null;
                var request = new RegistryRequest { Command = RegistryCommand.List };
                RegistryControlAuthentication.Prepare(request, authenticationSecret, DateTime.UtcNow);
                RegistryControlCodec.WriteRequest(pipe, request);
                var response = RegistryControlCodec.ReadResponse(pipe);
                return RegistryControlAuthentication.VerifyResponse(
                    response,
                    request,
                    authenticationSecret)
                    ? response
                    : null;
            }
        }

        internal static string FindExecutableOnPath(string name)
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
            var commands = CreateStartupCommands(
                root,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            {
                key.SetValue(Program.RunValueName, commands.Tray, RegistryValueKind.String);
                key.SetValue(Program.AgentRunValueName, commands.Agent, RegistryValueKind.String);
            }
            marker.StartupCommand = commands.Tray;
            marker.AgentStartupCommand = commands.Agent;
        }

        internal static (string Tray, string Agent) CreateStartupCommands(
            string installRoot,
            string localApplicationData)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
                throw new ArgumentException(SetupText.Pick("需要安装根目录。", "Install root is required."), nameof(installRoot));
            if (string.IsNullOrWhiteSpace(localApplicationData))
                throw new ArgumentException(SetupText.Pick("需要本地应用数据目录。", "Local application data is required."), nameof(localApplicationData));
            var root = Path.GetFullPath(installRoot);
            var tray = Quote(Path.Combine(root, "TermLink.exe"));
            var agent = Quote(Path.Combine(root, "TermLink.Agent.exe")) +
                " --registry --control-pipe " + TermLinkControlPipe.DefaultName() +
                " --buffer-root " + Quote(Path.Combine(
                    Path.GetFullPath(localApplicationData),
                    "TermLink",
                    "Agent"));
            return (tray, agent);
        }

        private static void RemoveStartupIfOwned(InstallMarker marker)
        {
            if (marker == null) return;
            using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
            {
                if (!string.IsNullOrWhiteSpace(marker.StartupCommand) && string.Equals(
                        key?.GetValue(Program.RunValueName) as string,
                        marker.StartupCommand,
                        StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(Program.RunValueName, false);
                }
                if (!string.IsNullOrWhiteSpace(marker.AgentStartupCommand) && string.Equals(
                        key?.GetValue(Program.AgentRunValueName) as string,
                        marker.AgentStartupCommand,
                        StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(Program.AgentRunValueName, false);
                }
            }
        }

        private static string ReadOwnedStartupCommand(InstallMarker marker)
        {
            return ReadOwnedStartupCommand(marker?.StartupCommand, Program.RunValueName);
        }

        private static string ReadOwnedAgentStartupCommand(InstallMarker marker)
        {
            return ReadOwnedStartupCommand(marker?.AgentStartupCommand, Program.AgentRunValueName);
        }

        private static string ReadOwnedStartupCommand(string markerCommand, string valueName)
        {
            if (string.IsNullOrWhiteSpace(markerCommand)) return null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                    writable: false))
                {
                    var current = key?.GetValue(valueName) as string;
                    return string.Equals(
                        current,
                        markerCommand,
                        StringComparison.OrdinalIgnoreCase)
                        ? current
                        : null;
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

        private static void TryRestoreStartupAfterRollback(string previousCommand)
        {
            TryRestoreStartupAfterRollback(previousCommand, Program.RunValueName);
        }

        private static void TryRestoreAgentStartupAfterRollback(string previousCommand)
        {
            TryRestoreStartupAfterRollback(previousCommand, Program.AgentRunValueName);
        }

        private static void TryRestoreStartupAfterRollback(string previousCommand, string valueName)
        {
            if (string.IsNullOrWhiteSpace(previousCommand)) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                    writable: true))
                {
                    var current = key?.GetValue(valueName) as string;
                    if (string.IsNullOrWhiteSpace(current))
                    {
                        key?.SetValue(valueName, previousCommand, RegistryValueKind.String);
                    }
                }
            }
            catch
            {
                // Preserve the original installation failure. A concurrent user
                // value is never overwritten during rollback.
            }
        }

        private static void ConfigureShortcuts(string root, InstallMarker marker, InstallMarker previous)
        {
            var tray = Path.Combine(root, "TermLink.exe");
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
                !string.Equals(sourceName, "TermLink-Setup.exe", StringComparison.OrdinalIgnoreCase) &&
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
            throw new IOException(SetupText.Pick(
                "无法为 TermLink 创建快捷方式：同名文件过多。请清理旧的 TermLink 快捷方式后重试。",
                "A TermLink shortcut could not be created because too many files have the same name. Remove old TermLink shortcuts and try again."));
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
                    throw new PlatformNotSupportedException(SetupText.Pick(
                        "当前 Windows 环境缺少快捷方式创建组件 WScript.Shell。",
                        "This Windows environment does not provide the WScript.Shell shortcut component."));
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
                throw new InvalidOperationException(SetupText.Pick(
                    "无法创建 TermLink 快捷方式：" + path + "。请确认当前用户对该目录有写入权限。",
                    "The TermLink shortcut could not be created: " + path + ". Confirm that the current user can write to this folder."),
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

        private static byte[] ReadOwnedFile(string path, string sha256)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha256) ||
                !File.Exists(path)) return null;
            try
            {
                return string.Equals(Sha256(path), sha256, StringComparison.OrdinalIgnoreCase)
                    ? File.ReadAllBytes(path)
                    : null;
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is SecurityException)
            {
                return null;
            }
        }

        private static void TryRestoreOwnedFileAfterRollback(string path, byte[] content)
        {
            if (string.IsNullOrWhiteSpace(path) || content == null) return;
            try
            {
                // Do not overwrite a file created or modified concurrently after
                // the failed upgrade. Restoration only fills the path removed by
                // this installer's owned-file cleanup.
                if (File.Exists(path)) return;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var temporaryPath = path + ".termlink-rollback-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporaryPath, content);
                    File.Move(temporaryPath, path, overwrite: false);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the original installation failure and any concurrent
                // file. The previous install root still contains its marker.
            }
        }

        private static byte[] ReadExistingTerminalFragment()
        {
            var expectedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows Terminal", "Fragments", "TermLink", FragmentFileName);
            try
            {
                return File.Exists(expectedPath) ? File.ReadAllBytes(expectedPath) : null;
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is SecurityException)
            {
                return null;
            }
        }

        private static void TryRestoreTerminalFragmentAfterRollback(
            InstallMarker installedMarker,
            byte[] previousContent)
        {
            try
            {
                if (installedMarker == null ||
                    string.IsNullOrWhiteSpace(installedMarker.TerminalFragmentPath)) return;
                var path = installedMarker.TerminalFragmentPath;
                // If another process/user edited the new fragment after it was
                // written, rollback no longer owns its current bytes and leaves
                // them untouched.
                if (!IsTerminalFragmentOwnedByMarker(path, installedMarker)) return;
                if (previousContent == null)
                {
                    RemoveTerminalFragmentIfOwned(installedMarker);
                    return;
                }

                var directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                var temporaryPath = Path.Combine(
                    directory,
                    ".termlink-rollback-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporaryPath, previousContent);
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the original installation error. If safe restoration
                // is impossible, the previous/new fragment is left visible for
                // diagnosis instead of broad cleanup.
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

        internal static void RestorePreviousInstallRootAfterFailure(
            string backup,
            string root,
            string parent)
        {
            if (!Directory.Exists(backup))
            {
                throw new DirectoryNotFoundException(SetupText.Pick(
                    "旧版本备份不存在: ",
                    "The old-version backup does not exist: ") + backup);
            }

            string failedRoot = null;
            if (Directory.Exists(root))
            {
                failedRoot = Path.Combine(parent, ".termlink-failed-" + Guid.NewGuid().ToString("N"));
                MoveDirectoryWithRetry(
                    root,
                    failedRoot,
                    SetupText.Pick(
                        "无法隔离失败的新版本目录",
                        "Could not isolate the failed new-version folder"),
                    TimeSpan.FromSeconds(10));
            }

            try
            {
                MoveDirectoryWithRetry(
                    backup,
                    root,
                    SetupText.Pick(
                        "无法恢复旧版本安装目录",
                        "Could not restore the old-version install folder"),
                    TimeSpan.FromSeconds(10));
            }
            catch
            {
                // If restoring the backup itself fails, make the activated new
                // root usable again when possible. The backup remains intact for
                // manual recovery and the caller surfaces both failures.
                if (!string.IsNullOrWhiteSpace(failedRoot) &&
                    Directory.Exists(failedRoot) &&
                    !Directory.Exists(root))
                {
                    try
                    {
                        MoveDirectoryWithRetry(
                            failedRoot,
                            root,
                            SetupText.Pick(
                                "无法重新激活失败的新版本目录",
                                "Could not reactivate the failed new-version folder"),
                            TimeSpan.FromSeconds(5));
                    }
                    catch { }
                }
                throw;
            }

            if (!string.IsNullOrWhiteSpace(failedRoot))
            {
                TryDeleteDirectory(failedRoot);
                if (Directory.Exists(failedRoot))
                {
                    Console.Error.WriteLine(
                        SetupText.Pick(
                            "失败的新版本目录暂时无法清理，旧版本已恢复；保留目录: ",
                            "The failed new-version folder could not be cleaned up. The old version was restored, and this folder was retained: ") +
                        failedRoot);
                }
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
            if (IsTerminalFragmentOwnedByMarker(expectedPath, marker))
            {
                File.Delete(expectedPath);
                var directory = Path.GetDirectoryName(expectedPath);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }

        internal static IReadOnlyList<string> GetKnownRuntimeDataFilePaths(string localApplicationData)
        {
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new ArgumentException(SetupText.Pick(
                    "需要本地应用数据目录。",
                    "Local application data directory is required."), nameof(localApplicationData));
            }
            var root = Path.Combine(Path.GetFullPath(localApplicationData), "TermLink");
            return new[]
            {
                Path.Combine(root, "production-config.json"),
                Path.Combine(root, "production-credentials.dpapi"),
                Path.Combine(root, "Agent", "control.secret"),
                Path.Combine(root, "Agent", "registrations.dpapi"),
                Path.Combine(root, "Agent", "remote-access-policy.dpapi"),
                Path.Combine(root, "Tray", "ui.preferences")
            };
        }

        internal static IReadOnlyList<string> PurgeKnownRuntimeDataFiles(string localApplicationData)
        {
            var knownPaths = GetKnownRuntimeDataFilePaths(localApplicationData);
            var root = Path.GetFullPath(Path.Combine(localApplicationData, "TermLink"));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var deleted = new List<string>();
            foreach (var candidate in knownPaths)
            {
                var path = Path.GetFullPath(candidate);
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(SetupText.Pick(
                        "运行数据清理路径超出 TermLink 专用目录，已拒绝: ",
                        "The runtime-data cleanup path is outside the TermLink-owned folder and was refused: ") + path);
                }
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deleted.Add(path);
            }

            // Remove only these exact directories, and only when already empty.
            // Session directories, update transactions and unknown files are
            // deliberately retained; no recursive enumeration or deletion occurs.
            foreach (var directory in new[]
            {
                Path.Combine(root, "Agent"),
                Path.Combine(root, "Tray"),
                root
            })
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            return deleted;
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
