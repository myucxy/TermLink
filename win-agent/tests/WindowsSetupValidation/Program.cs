using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.WindowsIntegration;
using TermLink.UpdateCore;

namespace TermLink.Setup.Validation
{
    internal static class ValidationProgram
    {
        [STAThread]
        public static int Main()
        {
            var temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "TermLink-WindowsSetupValidation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ValidateBilingualSetupText();
                ValidateArguments();
                ValidateLegacyInstallOwnership(temporaryRoot);
                ValidateCrossDirectoryUpgradeIsRejected(temporaryRoot);
                ValidateInstalledLaunchVersionIdentity(temporaryRoot);
                ValidateIndependentAgentStartupCommands(temporaryRoot);
                ValidateLaunchHealthIsProcessBound();
                ValidateLaunchHealthIsInstallRootBound(temporaryRoot);
                ValidateUpgradeTerminationConsent();
                ValidateUpgradeTerminationPrompt();
                ValidateServiceImagePathParsing();
                ValidateRunningProcessDiscoveryAndTermination(temporaryRoot);
                ValidateTransientDirectoryLockRetry(temporaryRoot);
                ValidatePermanentDirectoryLockDiagnostics(temporaryRoot);
                ValidateDirectoryMoveCancellation(temporaryRoot);
                ValidateInstallCommitGate();
                ValidateCancelButtonBehavior();
                ValidateFailedUpgradeRestoresPreviousRoot(temporaryRoot);
                ValidateAiWorkspacePreservation(temporaryRoot);
                ValidateOldWindowsIsRejected();
                ValidateMissingTerminalIsOptional(temporaryRoot);
                ValidateTerminalIntegrationFailureIsPublicSafe(temporaryRoot);
                ValidateArchitectureAndConPty();
                ValidateCurrentMachineProbe();
                ValidateStrictFragmentOwnership(temporaryRoot);
                ValidateTerminalRepairOverwritesObsoleteTermLinkFragment(temporaryRoot);
                ValidatePackagedTerminalIntegrationDoesNotWriteLooseFragment(temporaryRoot);
                ValidateRuntimePurgeBoundary(temporaryRoot);
                Console.WriteLine("Windows setup prerequisite/ownership validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                var resolved = Path.GetFullPath(temporaryRoot);
                var temp = Path.GetFullPath(Path.GetTempPath());
                if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith(
                        "TermLink-WindowsSetupValidation-",
                        StringComparison.Ordinal))
                {
                    Directory.Delete(resolved, recursive: true);
                }
            }
        }

        private static void ValidateBilingualSetupText()
        {
            var chineseUsage = WithUiCulture("zh-CN", () =>
            {
                Assert(SetupText.IsChinese, "Simplified Chinese UI culture was not recognized.");
                return SetupArguments.Usage;
            });
            Assert(chineseUsage.Contains("打开安装向导", StringComparison.Ordinal) &&
                chineseUsage.Contains("显示帮助", StringComparison.Ordinal),
                "The Chinese Setup help text is incomplete.");

            var englishUsage = WithUiCulture("en-US", () =>
            {
                Assert(!SetupText.IsChinese, "English UI culture was incorrectly treated as Chinese.");
                return SetupArguments.Usage;
            });
            Assert(englishUsage.Contains("Open the setup wizard", StringComparison.Ordinal) &&
                englishUsage.Contains("Show help", StringComparison.Ordinal),
                "The English Setup help text is incomplete.");

            var chineseError = WithUiCulture("zh-CN", () => AssertThrows<ArgumentException>(
                () => SetupArguments.Parse(new[] { "--unknown" }),
                "The Chinese invalid-argument path did not fail closed.").Message);
            var englishError = WithUiCulture("en-US", () => AssertThrows<ArgumentException>(
                () => SetupArguments.Parse(new[] { "--unknown" }),
                "The English invalid-argument path did not fail closed.").Message);
            Assert(chineseError.Contains("未知参数", StringComparison.Ordinal) &&
                englishError.Contains("Unknown argument", StringComparison.Ordinal),
                "Setup argument errors are not localized in both supported languages.");

            const string privateDiagnostic = "C:\\Users\\private\\must-not-leak.txt: backend detail";
            var chinesePublicError = WithUiCulture("zh-CN", () =>
                SetupText.PublicError(new InvalidOperationException(privateDiagnostic)));
            var englishPublicError = WithUiCulture("en-US", () =>
                SetupText.PublicError(new InvalidOperationException(privateDiagnostic)));
            Assert(chinesePublicError.Contains("安装操作失败", StringComparison.Ordinal) &&
                englishPublicError.Contains("setup operation failed", StringComparison.OrdinalIgnoreCase),
                "Generic Setup failures are not localized in both supported languages.");
            Assert(!chinesePublicError.Contains(privateDiagnostic, StringComparison.Ordinal) &&
                !englishPublicError.Contains(privateDiagnostic, StringComparison.Ordinal),
                "Setup exposed a raw exception or private path to the user.");

            var codedError = WithUiCulture("en-US", () =>
                SetupText.PublicError(new InvalidOperationException("SETUP_INSTALL_FAILED")));
            Assert(codedError.Contains("[SETUP_INSTALL_FAILED]", StringComparison.Ordinal),
                "Setup discarded a safe stable error code.");

            var fileError = WithUiCulture("zh-CN", () =>
                SetupText.PublicError(new IOException(privateDiagnostic)));
            Assert(fileError.Contains("文件操作失败", StringComparison.Ordinal) &&
                !fileError.Contains(privateDiagnostic, StringComparison.Ordinal),
                "Setup file errors are not safely localized.");

            var chineseDiagnostic = WithUiCulture("zh-CN", SetupText.DiagnosticRecorded);
            var englishDiagnostic = WithUiCulture("en-US", SetupText.DiagnosticRecorded);
            Assert(chineseDiagnostic.Contains("诊断", StringComparison.Ordinal) &&
                englishDiagnostic.Contains("diagnostics", StringComparison.OrdinalIgnoreCase) &&
                !chineseDiagnostic.Contains("\\", StringComparison.Ordinal) &&
                !englishDiagnostic.Contains("\\", StringComparison.Ordinal),
                "Setup diagnostic guidance is not bilingual or exposes a filesystem path.");
        }

        private static void ValidateArguments()
        {
            var ordinary = SetupArguments.Parse(new[] { "--uninstall", "C:\\TermLink" });
            Assert(!ordinary.PurgeKnownRuntimeData,
                "Uninstall must preserve runtime data unless purge is explicitly requested.");

            var purge = SetupArguments.Parse(new[]
            {
                "--uninstall", "C:\\TermLink", "--purge-known-runtime-data", "--quiet"
            });
            Assert(purge.Uninstall && purge.PurgeKnownRuntimeData,
                "The explicit runtime-data purge option was not parsed.");

            AssertThrows<ArgumentException>(
                () => SetupArguments.Parse(new[] { "--purge-known-runtime-data" }),
                "Runtime-data purge must be rejected outside uninstall mode.");

            var terminate = SetupArguments.Parse(new[]
            {
                "--install-dir", "C:\\TermLink", "--terminate-running", "--quiet"
            });
            Assert(terminate.TerminateRunningProcesses,
                "The explicit running-process termination option was not parsed.");
            AssertThrows<ArgumentException>(
                () => SetupArguments.Parse(new[] { "--terminate-running" }),
                "Running-process termination must be rejected without an install directory.");

            var noLaunch = SetupArguments.Parse(new[]
            {
                "--install-dir", "C:\\TermLink", "--no-launch", "--quiet"
            });
            Assert(noLaunch.NoLaunch,
                "The explicit no-launch validation option was not parsed.");
            AssertThrows<ArgumentException>(
                () => SetupArguments.Parse(new[] { "--no-launch" }),
                "No-launch must be rejected without an install directory.");
        }

        private static void ValidateLegacyInstallOwnership(string temporaryRoot)
        {
            var root = Path.Combine(temporaryRoot, "legacy-install-ownership", "TermLink");
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "TermLink.exe"), new byte[] { 0x4d, 0x5a });
            File.WriteAllBytes(Path.Combine(root, "TermLink.SessionHost.exe"), new byte[] { 0x4d, 0x5a });
            var marker = new InstallMarker
            {
                Product = global::TermLink.Setup.Program.ProductId,
                Version = "0.1.32.38",
                InstallRoot = root,
                InstalledUtc = DateTime.UtcNow.ToString("O")
            };
            File.WriteAllText(
                Path.Combine(root, ".termlink-install.json"),
                System.Text.Json.JsonSerializer.Serialize(marker),
                new UTF8Encoding(false));

            Assert(Installer.IsValidInstallDirectory(root),
                "A marker-owned pre-P3 installation without TermLink.Agent.exe was rejected for in-place upgrade.");
            File.Delete(Path.Combine(root, "TermLink.SessionHost.exe"));
            Assert(!Installer.IsValidInstallDirectory(root),
                "A marker without the legacy SessionHost ownership anchor was accepted.");
        }

        private static void ValidateUpgradeTerminationConsent()
        {
            var running = new[]
            {
                new RunningTermLinkProcess(1234, "TermLink.Agent", "C:\\TermLink\\TermLink.Agent.exe"),
                new RunningTermLinkProcess(5678, "TermLink", "C:\\TermLink\\TermLink.exe")
            };
            var error = AssertThrows<RunningTermLinkProcessesException>(
                () => Installer.ValidateTerminationConsent(running, terminateRunningProcesses: false),
                "An upgrade without termination consent did not fail closed.");
            Assert(error.Message.Contains("PID 1234", StringComparison.Ordinal) &&
                error.Message.Contains("--terminate-running", StringComparison.Ordinal),
                "The running-process diagnostic is incomplete.");
            Installer.ValidateTerminationConsent(running, terminateRunningProcesses: true);
            var summary = Installer.FormatRunningProcessList(running);
            Assert(summary.Contains("TermLink.Agent", StringComparison.Ordinal) &&
                summary.Contains("PID 5678", StringComparison.Ordinal),
                "The interactive upgrade prompt summary is incomplete.");
        }

        private static void ValidateUpgradeTerminationPrompt()
        {
            var running = new[]
            {
                new RunningTermLinkProcess(
                    2468,
                    "TermLink.SessionHost",
                    "C:\\TermLink\\TermLink.SessionHost.exe")
            };
            var chinesePrompt = WithUiCulture("zh-CN", () =>
                Installer.FormatUpgradeTerminationPrompt(
                    running,
                    Array.Empty<RunningTermLinkService>()));
            Assert(chinesePrompt.Contains("所有由 TermLink SessionHost 承载的命令行会话", StringComparison.Ordinal) &&
                chinesePrompt.Contains("PID 2468", StringComparison.Ordinal) &&
                chinesePrompt.Contains("关闭后的会话无法恢复", StringComparison.Ordinal),
                "The Chinese upgrade prompt does not explicitly disclose terminal-session termination.");

            var englishPrompt = WithUiCulture("en-US", () =>
                Installer.FormatUpgradeTerminationPrompt(
                    running,
                    Array.Empty<RunningTermLinkService>()));
            Assert(englishPrompt.Contains("all command-line sessions hosted by TermLink SessionHost", StringComparison.Ordinal) &&
                englishPrompt.Contains("PID 2468", StringComparison.Ordinal) &&
                englishPrompt.Contains("Closed sessions cannot be restored", StringComparison.Ordinal),
                "The English upgrade prompt does not explicitly disclose terminal-session termination.");

            var chineseQuietPrompt = WithUiCulture("zh-CN", () =>
                Installer.FormatUpgradeTerminationPrompt(
                    Array.Empty<RunningTermLinkProcess>(),
                    Array.Empty<RunningTermLinkService>()));
            var englishQuietPrompt = WithUiCulture("en-US", () =>
                Installer.FormatUpgradeTerminationPrompt(
                    Array.Empty<RunningTermLinkProcess>(),
                    Array.Empty<RunningTermLinkService>()));
            Assert(chineseQuietPrompt.Contains("提交安装前仍会再次检查", StringComparison.Ordinal) &&
                englishQuietPrompt.Contains("check again before committing", StringComparison.Ordinal),
                "An idle upgrade no longer warns in both languages that a final process sweep will run before commit.");
        }

        private static void ValidateCrossDirectoryUpgradeIsRejected(string temporaryRoot)
        {
            var activeRoot = Path.Combine(temporaryRoot, "relocation", "A", "TermLink");
            var requestedRoot = Path.Combine(temporaryRoot, "relocation", "B", "TermLink");
            Directory.CreateDirectory(activeRoot);
            var sentinel = Path.Combine(activeRoot, "existing-install.bin");
            var sentinelBytes = new byte[] { 0x54, 0x4c, 0x01, 0x7f };
            File.WriteAllBytes(sentinel, sentinelBytes);

            var error = AssertThrows<InvalidOperationException>(
                () => Installer.ValidateInstallDirectorySelection(requestedRoot, activeRoot),
                "A cross-directory upgrade was accepted.");
            Assert(error.Message.Contains(activeRoot, StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains(requestedRoot, StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains(SetupText.Pick("先", "first"), StringComparison.OrdinalIgnoreCase),
                "The relocation rejection did not identify both roots or the uninstall-first action.");
            Assert(File.ReadAllBytes(sentinel).SequenceEqual(sentinelBytes),
                "Rejecting a cross-directory upgrade changed the active installation.");
            Assert(!Directory.Exists(Path.GetDirectoryName(requestedRoot)),
                "Rejecting a cross-directory upgrade created the requested parent directory.");

            Installer.ValidateInstallDirectorySelection(activeRoot, activeRoot.ToUpperInvariant());
        }

        private static void ValidateLaunchHealthIsProcessBound()
        {
            var authenticationSecret = Enumerable.Range(0, RegistryControlAuthentication.SecretBytes)
                .Select(value => (byte)(value + 11))
                .ToArray();
            var matchingPipeName = "termlink-setup-health-match-" + Guid.NewGuid().ToString("N");
            using (var matchingServer = new NamedPipeServerStream(
                matchingPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None))
            {
                var serverTask = Task.Run(() =>
                {
                    matchingServer.WaitForConnection();
                    var request = RegistryControlCodec.ReadRequest(matchingServer, out var requestVersion);
                    Assert(request.Command == RegistryCommand.List,
                        "The setup launch probe sent an unexpected health command.");
                    var response = new RegistryResponse { Success = true, Code = "healthy" };
                    RegistryControlAuthentication.PrepareResponse(
                        response,
                        request,
                        authenticationSecret);
                    RegistryControlCodec.WriteResponse(matchingServer, response, requestVersion);
                });
                var response = Installer.SendHealthRequestToLaunchedTray(
                    matchingPipeName,
                    Environment.ProcessId,
                    5000,
                    authenticationSecret);
                Assert(response != null && response.Success,
                    "The launch probe rejected the control pipe owned by the launched process.");
                Assert(serverTask.Wait(5000),
                    "The matching launch-health server did not finish.");
            }

            var foreignPipeName = "termlink-setup-health-foreign-" + Guid.NewGuid().ToString("N");
            using (var foreignServer = new NamedPipeServerStream(
                foreignPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None))
            {
                using var connectionObserved = new ManualResetEventSlim(false);
                var serverTask = Task.Run(() =>
                {
                    try { foreignServer.WaitForConnection(); }
                    catch (IOException)
                    {
                        // The deliberately rejected client can close before the
                        // server thread returns from WaitForConnection.
                    }
                    finally { connectionObserved.Set(); }
                });
                var response = Installer.SendHealthRequestToLaunchedTray(
                    foreignPipeName,
                    Environment.ProcessId == int.MaxValue ? int.MaxValue - 1 : Environment.ProcessId + 1,
                    5000,
                    authenticationSecret);
                Assert(response == null,
                    "A healthy global pipe owned by another process was accepted for the new launch.");
                Assert(connectionObserved.Wait(5000) && serverTask.Wait(5000),
                    "The foreign launch-health server did not observe the probe connection.");
            }
            Array.Clear(authenticationSecret, 0, authenticationSecret.Length);
        }

        private static void ValidateLaunchHealthIsInstallRootBound(string temporaryRoot)
        {
            var root = Path.Combine(temporaryRoot, "launch-health", "TermLink");
            var otherRoot = Path.Combine(temporaryRoot, "launch-health", "OtherTermLink");
            Assert(Installer.IsOwnedLaunchHealthServerPath(
                    Path.Combine(root, "TermLink.exe"),
                    root),
                "The installed Avalonia control process was rejected as a launch-health server.");
            Assert(Installer.IsOwnedLaunchHealthServerPath(
                    Path.Combine(root, "TermLink.Agent.exe"),
                    root),
                "The installed Agent process was rejected as a launch-health server.");
            Assert(!Installer.IsOwnedLaunchHealthServerPath(
                    Path.Combine(root, "TermLink.SessionHost.exe"),
                    root),
                "SessionHost was accepted as an installer launch-health server.");
            Assert(!Installer.IsOwnedLaunchHealthServerPath(
                    Path.Combine(root, "nested", "TermLink.Agent.exe"),
                    root),
                "A nested executable was accepted as the installed Agent.");
            Assert(!Installer.IsOwnedLaunchHealthServerPath(
                    Path.Combine(otherRoot, "TermLink.Agent.exe"),
                    root),
                "An Agent from another installation was accepted by the launch check.");
            Assert(Installer.LaunchHealthTimeout >= TimeSpan.FromSeconds(75),
                "Setup does not leave enough cold-start headroom after WindowsAgentClient's Agent deadline.");
        }

        private static void ValidateInstalledLaunchVersionIdentity(string temporaryRoot)
        {
            var root = Path.Combine(temporaryRoot, "launch-version-identity", "TermLink");
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "TermLink.exe"), new byte[] { 0x4d, 0x5a });
            File.WriteAllBytes(Path.Combine(root, "TermLink.Agent.exe"), new byte[] { 0x4d, 0x5a });
            File.WriteAllBytes(Path.Combine(root, "TermLink.SessionHost.exe"), new byte[] { 0x4d, 0x5a });
            var validationAssembly = typeof(ValidationProgram).Assembly.Location;
            File.Copy(validationAssembly, Path.Combine(root, "TermLink.dll"), overwrite: false);
            var assemblyVersion = AssemblyName.GetAssemblyName(validationAssembly).Version!.ToString();
            var markerPath = Path.Combine(root, ".termlink-install.json");
            var marker = new InstallMarker
            {
                Product = global::TermLink.Setup.Program.ProductId,
                Version = assemblyVersion,
                InstallRoot = root,
                InstalledUtc = DateTime.UtcNow.ToString("O")
            };
            File.WriteAllText(
                markerPath,
                System.Text.Json.JsonSerializer.Serialize(marker),
                new UTF8Encoding(false));
            Assert(Installer.IsInstalledLaunchIdentityValid(root),
                "A matching marker/Tray assembly version was rejected before launch.");

            marker.Version = "9.9.9.9";
            File.WriteAllText(
                markerPath,
                System.Text.Json.JsonSerializer.Serialize(marker),
                new UTF8Encoding(false));
            Assert(!Installer.IsInstalledLaunchIdentityValid(root),
                "A marker/Tray assembly version mismatch was accepted for launch.");
        }

        private static void ValidateIndependentAgentStartupCommands(string temporaryRoot)
        {
            var root = Path.Combine(temporaryRoot, "startup commands", "TermLink");
            var localData = Path.Combine(temporaryRoot, "startup local data");
            var commands = Installer.CreateStartupCommands(root, localData);
            Assert(commands.Tray == "\"" + Path.Combine(root, "TermLink.exe") + "\"",
                "The UI startup command changed unexpectedly.");
            Assert(commands.Agent.StartsWith(
                    "\"" + Path.Combine(root, "TermLink.Agent.exe") +
                        "\" --registry --control-pipe termlink-agent-control-v2-",
                    StringComparison.Ordinal) &&
                commands.Agent.EndsWith(
                    " --buffer-root \"" + Path.Combine(localData, "TermLink", "Agent") + "\"",
                    StringComparison.Ordinal),
                "The independent Agent startup command is incomplete or not account-scoped.");
        }

        private static void ValidateServiceImagePathParsing()
        {
            Assert(string.Equals(
                    Installer.ParseServiceExecutablePath(
                        "\"C:\\Program Files\\TermLink\\TermLink.exe\" --service"),
                    "C:\\Program Files\\TermLink\\TermLink.exe",
                    StringComparison.OrdinalIgnoreCase),
                "A quoted Windows service ImagePath was not parsed safely.");
            Assert(string.Equals(
                    Installer.ParseServiceExecutablePath(
                        "C:\\TermLink\\TermLink.Agent.exe --service"),
                    "C:\\TermLink\\TermLink.Agent.exe",
                    StringComparison.OrdinalIgnoreCase),
                "An unquoted Windows service ImagePath was not parsed safely.");
            Assert(Installer.ParseServiceExecutablePath("cmd.exe /c arbitrary") == null,
                "A non-TermLink service command was treated as an executable path.");
        }

        private static void ValidateRunningProcessDiscoveryAndTermination(string temporaryRoot)
        {
            var installRoot = Path.Combine(temporaryRoot, "running-process-probe");
            var foreignRoot = Path.Combine(temporaryRoot, "foreign-process-probe");
            Directory.CreateDirectory(installRoot);
            Directory.CreateDirectory(foreignRoot);
            var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
            var names = new[]
            {
                "TermLink.Agent.exe",
                "TermLink.exe",
                "TermLink.SessionHost.exe",
                "TermLink.Tray.exe",
                "TermLink.Updater.exe",
                "Renamed-Legacy-Helper.exe"
            };
            var owned = new List<Process>();
            Process foreign = null;
            Process nonOwnedDescendant = null;
            try
            {
                foreach (var name in names)
                {
                    var executable = Path.Combine(installRoot, name);
                    File.Copy(source, executable, overwrite: false);
                    var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = "127.0.0.1 -t",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    Assert(process != null, "A running-process validation helper did not start: " + name);
                    owned.Add(process!);
                }

                // Reproduce the in-app update topology: an executable from the
                // TermLink install root owns a child that runs outside that root.
                // The downloaded Setup is such a child in production and must
                // survive while the old TermLink process is being stopped.
                var descendantScript = Path.Combine(temporaryRoot, "update-descendant.cmd");
                var descendantExecutable = Path.Combine(foreignRoot, "TermLink.UpdateSetupProbe.exe");
                File.Copy(source, descendantExecutable, overwrite: false);
                File.WriteAllText(
                    descendantScript,
                    "@echo off\r\n\"" + descendantExecutable + "\" 127.0.0.1 -t\r\n",
                    Encoding.ASCII);
                var parentExecutable = Path.Combine(installRoot, "TermLink.UpdateParent.exe");
                File.Copy(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    parentExecutable,
                    overwrite: false);
                var updateParentStart = new ProcessStartInfo
                {
                    FileName = parentExecutable,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                updateParentStart.ArgumentList.Add("/d");
                updateParentStart.ArgumentList.Add("/c");
                updateParentStart.ArgumentList.Add(descendantScript);
                var updateParent = Process.Start(updateParentStart);
                Assert(updateParent != null, "The in-app update parent validation helper did not start.");
                owned.Add(updateParent!);
                var descendantDeadline = DateTime.UtcNow.AddSeconds(10);
                while (nonOwnedDescendant == null && DateTime.UtcNow < descendantDeadline)
                {
                    foreach (var candidate in Process.GetProcesses())
                    {
                        var candidatePath = Installer.TryGetProcessImagePath(candidate.Id);
                        if (string.Equals(
                                candidatePath,
                                descendantExecutable,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            nonOwnedDescendant = candidate;
                            break;
                        }
                        candidate.Dispose();
                    }
                    if (nonOwnedDescendant == null) Thread.Sleep(50);
                }
                Assert(nonOwnedDescendant != null,
                    "The non-owned update descendant did not start.");

                var foreignExecutable = Path.Combine(foreignRoot, "TermLink.exe");
                File.Copy(source, foreignExecutable, overwrite: false);
                foreign = Process.Start(new ProcessStartInfo
                {
                    FileName = foreignExecutable,
                    Arguments = "127.0.0.1 -t",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Assert(foreign != null, "The foreign-path process validation helper did not start.");

                var deadline = DateTime.UtcNow.AddSeconds(5);
                IReadOnlyList<RunningTermLinkProcess> running;
                do
                {
                    running = Installer.GetRunningOwnedProcesses(installRoot);
                    if (running.Count == names.Length + 1) break;
                    Thread.Sleep(50);
                } while (DateTime.UtcNow < deadline);

                Assert(owned.All(process => running.Any(value => value.ProcessId == process.Id)),
                    "The installer did not discover every TermLink-owned executable type.");
                Assert(running.All(value => value.ProcessId != foreign!.Id),
                    "The installer claimed a same-named executable outside the install root.");
                Assert(Installer.IsOwnedTrayRunning(installRoot),
                    "The cancellation recovery probe did not recognize an already-running owned Tray.");
                AssertThrows<RunningTermLinkProcessesException>(
                    () => Installer.EnsureOwnedProcessesStopped(
                        installRoot,
                        terminateRunningProcesses: false),
                    "The installer silently terminated a process without consent.");
                Assert(owned.All(process => !process.HasExited),
                    "The installer terminated an owned process after consent was denied.");
                Installer.EnsureOwnedProcessesStopped(
                    installRoot,
                    terminateRunningProcesses: true);
                Assert(owned.All(process => process.WaitForExit(5000)),
                    "The installer did not terminate every authorized blocking process.");
                Assert(!Installer.IsOwnedTrayRunning(installRoot),
                    "The cancellation recovery probe still reported the stopped Tray as running.");
                Assert(nonOwnedDescendant != null && !nonOwnedDescendant.HasExited,
                    "Stopping the old TermLink process also terminated the downloaded installer descendant.");
                Assert(!foreign.HasExited,
                    "The installer terminated a same-named process outside the install root.");
            }
            finally
            {
                foreach (var process in owned)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                    process.Dispose();
                }
                if (foreign != null)
                {
                    if (!foreign.HasExited)
                    {
                        foreign.Kill(entireProcessTree: true);
                        foreign.WaitForExit(5000);
                    }
                    foreign.Dispose();
                }
                if (nonOwnedDescendant != null)
                {
                    if (!nonOwnedDescendant.HasExited)
                    {
                        nonOwnedDescendant.Kill(entireProcessTree: true);
                        nonOwnedDescendant.WaitForExit(5000);
                    }
                    nonOwnedDescendant.Dispose();
                }
            }
        }

        private static void ValidateTransientDirectoryLockRetry(string temporaryRoot)
        {
            var parent = Path.Combine(temporaryRoot, "transient-directory-lock");
            var source = Path.Combine(parent, "source");
            var destination = Path.Combine(parent, "destination");
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "payload.bin");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });

            FileStream locked = null;
            Thread release = null;
            try
            {
                locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                release = new Thread(() =>
                {
                    Thread.Sleep(350);
                    locked.Dispose();
                });
                release.Start();

                Installer.MoveDirectoryWithRetry(
                    source,
                    destination,
                    "Transient lock validation",
                    TimeSpan.FromSeconds(5));
                Assert(Directory.Exists(destination) && File.Exists(Path.Combine(destination, "payload.bin")),
                    "The installer did not recover after a transient directory lock was released.");
            }
            finally
            {
                release?.Join(5000);
                locked?.Dispose();
            }
        }

        private static void ValidateDirectoryMoveCancellation(string temporaryRoot)
        {
            var parent = Path.Combine(temporaryRoot, "directory-move-cancellation");
            var source = Path.Combine(parent, "source");
            var destination = Path.Combine(parent, "destination");
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "payload.bin");
            File.WriteAllBytes(file, new byte[] { 5, 6, 7, 8 });

            using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(200);
            AssertThrows<OperationCanceledException>(
                () => Installer.MoveDirectoryWithRetry(
                    source,
                    destination,
                    "Cancellation validation",
                    TimeSpan.FromSeconds(5),
                    cancellation.Token),
                "A cancelled directory move continued retrying instead of stopping safely.");
            Assert(Directory.Exists(source) && !Directory.Exists(destination),
                "A cancelled directory move partially activated its destination.");
        }

        private static void ValidatePermanentDirectoryLockDiagnostics(string temporaryRoot)
        {
            var parent = Path.Combine(temporaryRoot, "permanent-directory-lock");
            var source = Path.Combine(parent, "source");
            var destination = Path.Combine(parent, "destination");
            Directory.CreateDirectory(source);
            var file = Path.Combine(source, "payload.bin");
            File.WriteAllBytes(file, new byte[] { 9, 8, 7, 6 });

            using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var blocked = Installer.FindDeleteBlockedPaths(source);
            Assert(blocked.Any(value => value.Contains("payload.bin", StringComparison.OrdinalIgnoreCase)),
                "The installer did not identify the file whose handle denied directory rename/delete access.");
            var error = AssertThrows<IOException>(
                () => Installer.MoveDirectoryWithRetry(
                    source,
                    destination,
                    "Permanent lock validation",
                    TimeSpan.FromMilliseconds(100)),
                "A permanently locked directory unexpectedly moved.");
            Assert(error.Message.Contains("payload.bin", StringComparison.OrdinalIgnoreCase) &&
                error.Message.Contains("Win32 32", StringComparison.Ordinal),
                "The permanent-lock diagnostic omitted the blocked path or sharing-violation code.");
        }

        private static void ValidateInstallCommitGate()
        {
            var cancelledFirst = new InstallCommitGate();
            Assert(cancelledFirst.TryRequestCancellation(),
                "The install commit gate rejected its first cancellation request.");
            Assert(!cancelledFirst.TryBeginCommit() &&
                cancelledFirst.State == InstallCommitGateState.CancellationRequested,
                "A commit started after cancellation had already won the gate.");

            var committedFirst = new InstallCommitGate();
            Assert(committedFirst.TryBeginCommit(),
                "The install commit gate rejected its first commit request.");
            Assert(!committedFirst.TryRequestCancellation() &&
                committedFirst.State == InstallCommitGateState.CommitStarted,
                "A cancellation request was accepted after commit had started.");

            for (var attempt = 0; attempt < 200; attempt++)
            {
                var raced = new InstallCommitGate();
                using var start = new ManualResetEventSlim(false);
                var cancellationWon = false;
                var commitWon = false;
                var cancelTask = Task.Run(() =>
                {
                    start.Wait();
                    cancellationWon = raced.TryRequestCancellation();
                });
                var commitTask = Task.Run(() =>
                {
                    start.Wait();
                    commitWon = raced.TryBeginCommit();
                });
                start.Set();
                Task.WaitAll(cancelTask, commitTask);
                Assert(cancellationWon ^ commitWon,
                    "Cancellation and commit did not resolve to exactly one atomic winner.");
            }
        }

        private static void ValidateCancelButtonBehavior()
        {
            using (var form = new SetupForm())
            using (var cancellation = new CancellationTokenSource())
            {
                var gate = new InstallCommitGate();
                SetPrivateField(form, "installCancellation", cancellation);
                SetPrivateField(form, "installCommitGate", gate);
                var cancelButton = form.Controls
                    .OfType<System.Windows.Forms.Button>()
                    .Single(button => button.Text == "取消");
                Assert(cancelButton.DialogResult == System.Windows.Forms.DialogResult.None,
                    "The active install Cancel button would close the form before rollback completed.");
                RaiseButtonClick(cancelButton);
                Assert(cancellation.IsCancellationRequested,
                    "The active install Cancel button did not signal cancellation.");
                Assert(gate.State == InstallCommitGateState.CancellationRequested,
                    "The active install Cancel button did not win the cancellation gate.");
                Assert(!form.IsDisposed,
                    "The Cancel button closed the form before background rollback completed.");
            }

            using (var form = new SetupForm())
            using (var cancellation = new CancellationTokenSource())
            {
                var gate = new InstallCommitGate();
                Assert(gate.TryBeginCommit(), "Unable to arrange the committed-install UI test.");
                SetPrivateField(form, "installCancellation", cancellation);
                SetPrivateField(form, "installCommitGate", gate);
                var cancelButton = form.Controls
                    .OfType<System.Windows.Forms.Button>()
                    .Single(button => button.Text == "取消");
                RaiseButtonClick(cancelButton);
                Assert(!cancellation.IsCancellationRequested,
                    "The Cancel button claimed to cancel after the commit boundary.");
                Assert((bool)GetPrivateField(form, "closeWhenFinished"),
                    "A post-commit close request was not deferred until startup validation finished.");

                SetPrivateField(form, "committedInstallIsFinishing", true);
                var closing = new System.Windows.Forms.FormClosingEventArgs(
                    System.Windows.Forms.CloseReason.UserClosing,
                    cancel: false);
                typeof(SetupForm).GetMethod(
                    "SetupFormClosing",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(
                        form,
                        new object[] { form, closing });
                Assert(closing.Cancel,
                    "Closing the post-commit form was not deferred during startup validation.");
            }
        }

        private static void SetPrivateField(object instance, string name, object value)
        {
            typeof(SetupForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(instance, value);
        }

        private static void RaiseButtonClick(System.Windows.Forms.Button button)
        {
            typeof(System.Windows.Forms.Button).GetMethod(
                "OnClick",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(
                    button,
                    new object[] { EventArgs.Empty });
        }

        private static object GetPrivateField(object instance, string name)
        {
            return typeof(SetupForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(instance);
        }

        private static void ValidateFailedUpgradeRestoresPreviousRoot(string temporaryRoot)
        {
            var parent = Path.Combine(temporaryRoot, "rollback-probe");
            var root = Path.Combine(parent, "TermLink");
            var backup = Path.Combine(parent, ".termlink-backup-test");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(root, "new.txt"), "failed new version", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(backup, "old.txt"), "working old version", new UTF8Encoding(false));
            Directory.CreateDirectory(AiWorkspaceStorage.Under(root));
            File.WriteAllText(Path.Combine(AiWorkspaceStorage.Under(root), "project.txt"), "user work");

            Installer.RestorePreviousInstallRootAfterFailure(backup, root, parent);

            Assert(File.Exists(Path.Combine(root, "old.txt")),
                "A failed upgrade did not restore the previous root to its original path.");
            Assert(!File.Exists(Path.Combine(root, "new.txt")),
                "The failed new root remained active after previous-version restoration.");
            Assert(!Directory.Exists(backup),
                "The previous-version backup was not consumed by restoration.");
            Assert(File.ReadAllText(Path.Combine(AiWorkspaceStorage.Under(root), "project.txt")) == "user work", "Rollback lost AI files.");
        }

        private static void ValidateAiWorkspacePreservation(string temporaryRoot)
        {
            var oldRoot = Path.Combine(temporaryRoot, "workspace-old");
            var nextRoot = Path.Combine(temporaryRoot, "workspace-next");
            Directory.CreateDirectory(AiWorkspaceStorage.Under(oldRoot));
            Directory.CreateDirectory(nextRoot);
            File.WriteAllText(Path.Combine(AiWorkspaceStorage.Under(oldRoot), "work.txt"), "before upgrade");
            AiWorkspaceStorage.MoveBetweenInstallRoots(oldRoot, nextRoot);
            Assert(!Directory.Exists(AiWorkspaceStorage.Under(oldRoot)), "Workspace left in disposable backup.");
            File.AppendAllText(Path.Combine(AiWorkspaceStorage.Under(nextRoot), "work.txt"), " + new work");
            AiWorkspaceStorage.MoveBetweenInstallRoots(nextRoot, oldRoot);
            Assert(File.ReadAllText(Path.Combine(AiWorkspaceStorage.Under(oldRoot), "work.txt")) == "before upgrade + new work", "Remote rollback discarded new work.");
            Directory.CreateDirectory(AiWorkspaceStorage.Under(nextRoot));
            try { AiWorkspaceStorage.MoveBetweenInstallRoots(oldRoot, nextRoot); throw new Exception("Conflicting workspaces overwritten."); }
            catch (IOException) { Assert(File.Exists(Path.Combine(AiWorkspaceStorage.Under(oldRoot), "work.txt")), "Conflict lost original files."); }
            var preserved = AiWorkspaceStorage.PreserveBeforeUninstall(oldRoot);
            Assert(File.Exists(Path.Combine(preserved, "work.txt")) && !Directory.Exists(AiWorkspaceStorage.Under(oldRoot)), "Uninstall preservation failed.");
        }

        private static void ValidateOldWindowsIsRejected()
        {
            var result = SetupPrerequisites.Evaluate(
                isWindows: true,
                windowsBuild: SetupPrerequisites.MinimumWindowsBuild - 1,
                is64BitOperatingSystem: true,
                processArchitecture: Architecture.X64,
                createPseudoConsoleAvailable: true,
                windowsTerminalInstalled: true);
            Assert(!result.Supported, "A pre-1809 Windows build was accepted.");
            Assert(result.Errors.Any(value => value.Contains("17763", StringComparison.Ordinal)),
                "The old-Windows diagnostic does not state the minimum build.");
        }

        private static void ValidateMissingTerminalIsOptional(string temporaryRoot)
        {
            var result = SetupPrerequisites.Evaluate(
                isWindows: true,
                windowsBuild: SetupPrerequisites.MinimumWindowsBuild,
                is64BitOperatingSystem: true,
                processArchitecture: Architecture.X64,
                createPseudoConsoleAvailable: true,
                windowsTerminalInstalled: false);
            Assert(result.Supported, "Missing Windows Terminal incorrectly blocked Agent installation.");
            Assert(!result.WindowsTerminalInstalled && result.Warnings.Count == 1,
                "Missing Windows Terminal did not produce one explicit optional-frontend warning.");
            Assert(result.Warnings[0].Contains(
                    SetupText.Pick("不会自动安装、替换或修改", "will not install, replace, or modify"),
                    StringComparison.OrdinalIgnoreCase),
                "Missing Terminal warning does not promise the non-replacement boundary.");

            var marker = new InstallMarker();
            var installRoot = Path.Combine(temporaryRoot, "no-terminal-install");
            Directory.CreateDirectory(installRoot);
            File.WriteAllBytes(Path.Combine(installRoot, "TermLink.SessionHost.exe"), new byte[] { 0x4d, 0x5a });
            var fragmentPath = Path.Combine(
                temporaryRoot, "no-terminal-local-app-data", "Fragments", "TermLink", "termlink.json");
            var outcome = Installer.ConfigureTerminalSafely(
                installRoot,
                marker,
                previous: null,
                windowsTerminalInstalled: false,
                terminalFragmentPath: fragmentPath);
            Assert(outcome.TerminalConfigured &&
                string.Equals(outcome.TerminalStatus, "configured-terminal-not-installed", StringComparison.Ordinal),
                "Missing Windows Terminal did not preconfigure the owned fragment for a future install.");
            Assert(string.Equals(marker.TerminalFragmentPath, Path.GetFullPath(fragmentPath), StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fragmentPath) &&
                string.Equals(marker.TerminalFragmentSha256, Sha256(fragmentPath), StringComparison.Ordinal),
                "Missing Windows Terminal did not persist exact fragment ownership state.");
            using (var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(fragmentPath, Encoding.UTF8)))
            {
                var expectedHost = Path.Combine(installRoot, "TermLink.SessionHost.exe");
                Assert(document.RootElement.GetProperty("profiles").EnumerateArray().All(profile =>
                        profile.GetProperty("commandline").GetString()!
                            .Contains(expectedHost, StringComparison.OrdinalIgnoreCase)),
                    "The preconfigured fragment does not point every profile at the new SessionHost path.");
            }
            Assert(outcome.TerminalMessage.Contains(
                    SetupText.Pick("没有安装、替换或修改 Terminal 本体", "did not install, replace, or modify Windows Terminal itself"),
                    StringComparison.OrdinalIgnoreCase),
                "The no-Terminal outcome lost the non-replacement boundary.");
        }

        private static void ValidateTerminalIntegrationFailureIsPublicSafe(string temporaryRoot)
        {
            var installRoot = Path.Combine(temporaryRoot, "terminal-failure-install");
            Directory.CreateDirectory(installRoot);
            File.WriteAllBytes(Path.Combine(installRoot, "TermLink.SessionHost.exe"), new byte[] { 0x4d, 0x5a });
            var privateFragmentPath = Path.Combine(
                temporaryRoot, "private-user-path", "Fragments", "TermLink", "termlink.json");
            Directory.CreateDirectory(privateFragmentPath);

            var marker = new InstallMarker();
            var outcome = WithUiCulture("en-US", () => Installer.ConfigureTerminalSafely(
                installRoot,
                marker,
                previous: null,
                windowsTerminalInstalled: true,
                terminalFragmentPath: privateFragmentPath));

            Assert(!outcome.TerminalConfigured &&
                outcome.TerminalMessage.Contains("optional Windows Terminal profile", StringComparison.OrdinalIgnoreCase),
                "An optional Terminal integration failure did not produce a public English explanation.");
            Assert(!outcome.TerminalMessage.Contains(privateFragmentPath, StringComparison.OrdinalIgnoreCase) &&
                !outcome.TerminalMessage.Contains("private-user-path", StringComparison.OrdinalIgnoreCase),
                "The optional Terminal integration failure exposed a private path.");
        }

        private static void ValidateArchitectureAndConPty()
        {
            var wrongArchitecture = SetupPrerequisites.Evaluate(
                true, SetupPrerequisites.MinimumWindowsBuild, true, Architecture.X86, true, true);
            Assert(!wrongArchitecture.Supported &&
                wrongArchitecture.Errors.Any(value => value.Contains("x64", StringComparison.OrdinalIgnoreCase)),
                "A non-x64 setup process was accepted.");

            var missingConPty = SetupPrerequisites.Evaluate(
                true, SetupPrerequisites.MinimumWindowsBuild, true, Architecture.X64, false, true);
            Assert(!missingConPty.Supported &&
                missingConPty.Errors.Any(value => value.Contains("CreatePseudoConsole", StringComparison.Ordinal)),
                "A system without the ConPTY export was accepted.");
        }

        private static void ValidateCurrentMachineProbe()
        {
            var current = SetupPrerequisites.EvaluateCurrent();
            Assert(current.Supported,
                "The current Windows test host failed prerequisite probing: " +
                string.Join(" ", current.Errors));
        }

        private static void ValidateStrictFragmentOwnership(string temporaryRoot)
        {
            var path = Path.Combine(temporaryRoot, "foreign-known-guid.json");
            File.WriteAllText(
                path,
                "{\"profiles\":[{\"guid\":\"{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}\",\"name\":\"User modified\"}]}\n",
                new UTF8Encoding(false));
            Assert(!Installer.IsTerminalFragmentOwnedByMarker(path, marker: null),
                "A foreign fragment was claimed solely because it used a known GUID.");

            var marker = new InstallMarker
            {
                TerminalFragmentPath = path,
                TerminalFragmentSha256 = Sha256(path)
            };
            Assert(Installer.IsTerminalFragmentOwnedByMarker(path, marker),
                "An exact marker path/hash match was not recognized as installer-owned.");

            File.AppendAllText(path, " ", new UTF8Encoding(false));
            Assert(!Installer.IsTerminalFragmentOwnedByMarker(path, marker),
                "A user-modified known-GUID fragment was still claimed by the installer.");
        }

        private static void ValidateTerminalRepairOverwritesObsoleteTermLinkFragment(string temporaryRoot)
        {
            var installRoot = Path.Combine(temporaryRoot, "moved-install");
            Directory.CreateDirectory(installRoot);
            File.WriteAllBytes(Path.Combine(installRoot, "TermLink.SessionHost.exe"), new byte[] { 0x4d, 0x5a });
            var fragmentPath = Path.Combine(temporaryRoot, "terminal", "TermLink", "termlink.json");
            Directory.CreateDirectory(Path.GetDirectoryName(fragmentPath)!);
            File.WriteAllText(
                fragmentPath,
                "{\"profiles\":[{\"guid\":\"{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}\",\"commandline\":\"C:\\\\Old\\\\TermLink.SessionHost.exe\"}]}\n",
                new UTF8Encoding(false));

            var result = WindowsTerminalIntegration.Reconfigure(installRoot, fragmentPath);
            var repaired = File.ReadAllText(fragmentPath, Encoding.UTF8);
            Assert(!repaired.Contains("C:\\Old", StringComparison.OrdinalIgnoreCase),
                "Terminal repair retained the obsolete install directory.");
            using (var document = System.Text.Json.JsonDocument.Parse(repaired))
            {
                Assert(document.RootElement.GetProperty("termlinkAddNoLogo").GetBoolean() &&
                        !document.RootElement.GetProperty("termlinkAddNoProfile").GetBoolean(),
                    "Terminal repair did not migrate an old Fragment to the new PowerShell defaults.");
                var expectedHost = Path.Combine(installRoot, "TermLink.SessionHost.exe");
                var profiles = document.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
                Assert(profiles.Length == result.ProfileCount && profiles.All(profile =>
                        profile.GetProperty("commandline").GetString()!
                            .Contains(expectedHost, StringComparison.OrdinalIgnoreCase)),
                    "Terminal repair did not point every profile at the current SessionHost.");
                Assert(profiles.All(profile =>
                        string.Equals(
                            profile.GetProperty("closeOnExit").GetString(),
                            "always",
                            StringComparison.Ordinal)),
                    "Terminal repair did not configure TermLink tabs to close with their SessionHost.");
            }
            var defaults = WindowsTerminalIntegration.ReadPowerShellSettings(fragmentPath);
            Assert(defaults.AddNoLogo && !defaults.AddNoProfile,
                "Terminal integration defaults did not add -NoLogo while omitting -NoProfile.");

            result = WindowsTerminalIntegration.Reconfigure(
                installRoot,
                fragmentPath,
                addNoLogo: false,
                addNoProfile: true);
            var configured = WindowsTerminalIntegration.ReadPowerShellSettings(fragmentPath);
            Assert(!configured.AddNoLogo && configured.AddNoProfile,
                "Terminal integration did not persist the two independent PowerShell switches.");
            var configuredCommand = DecodeTerminalChildCommand(
                fragmentPath,
                "TermLink Windows PowerShell");
            Assert(!configuredCommand.Contains("-NoLogo", StringComparison.Ordinal) &&
                    configuredCommand.Contains("-NoProfile", StringComparison.Ordinal),
                "Terminal integration switches did not change the generated PowerShell command.");
            result = WindowsTerminalIntegration.Reconfigure(installRoot, fragmentPath);
            var preserved = WindowsTerminalIntegration.ReadPowerShellSettings(fragmentPath);
            Assert(!preserved.AddNoLogo && preserved.AddNoProfile,
                "Terminal repair did not preserve the configured PowerShell switches.");

            result = WindowsTerminalIntegration.Reconfigure(
                installRoot,
                fragmentPath,
                addNoLogo: true,
                addNoProfile: false);
            var defaultCommand = DecodeTerminalChildCommand(
                fragmentPath,
                "TermLink Windows PowerShell");
            Assert(defaultCommand.Contains("-NoLogo", StringComparison.Ordinal) &&
                    !defaultCommand.Contains("-NoProfile", StringComparison.Ordinal),
                "Terminal integration did not restore the requested default PowerShell command.");
            Assert(result.ProfileCount >= 2 && string.Equals(result.Sha256, Sha256(fragmentPath), StringComparison.Ordinal),
                "Terminal repair did not return the rebuilt Fragment ownership metadata.");

            var markerPath = Path.Combine(installRoot, WindowsTerminalIntegration.InstallMarkerName);
            File.WriteAllText(
                markerPath,
                "{\"Product\":\"TermLink\",\"InstallRoot\":" +
                System.Text.Json.JsonSerializer.Serialize(installRoot) + "}\n",
                new UTF8Encoding(false));
            Assert(WindowsTerminalIntegration.TryRefreshInstallMarker(installRoot, result),
                "Terminal repair did not refresh the valid install marker.");
            var markerText = File.ReadAllText(markerPath, Encoding.UTF8);
            Assert(markerText.Contains(result.Sha256, StringComparison.Ordinal),
                "The refreshed install marker does not own the repaired Fragment hash.");
        }

        private static void ValidatePackagedTerminalIntegrationDoesNotWriteLooseFragment(string temporaryRoot)
        {
            var root = Path.Combine(temporaryRoot, "packaged-terminal-integration");
            var packageFragment = Path.Combine(root, "package", "Terminal", "Fragments", "termlink.json");
            var looseFragment = Path.Combine(root, "local", "Fragments", "TermLink", "termlink.json");
            Directory.CreateDirectory(Path.GetDirectoryName(packageFragment)!);
            Directory.CreateDirectory(Path.GetDirectoryName(looseFragment)!);
            File.WriteAllText(
                packageFragment,
                "{\"profiles\":[{\"guid\":\"{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}\"}]}\n",
                new UTF8Encoding(false));
            var sentinel = "user-owned-loose-fragment\n";
            File.WriteAllText(looseFragment, sentinel, new UTF8Encoding(false));

            var result = WindowsTerminalIntegration.ReconfigureForProcess(
                Path.Combine(root, "package"),
                looseFragment,
                packageManaged: true,
                packageFragment);
            Assert(result.PackageManaged && result.ProfileCount == 1,
                "The packaged Terminal integration was not identified as package-managed.");
            Assert(string.Equals(result.FragmentPath, Path.GetFullPath(packageFragment), StringComparison.OrdinalIgnoreCase),
                "The packaged Terminal integration did not report its immutable package Fragment.");
            Assert(string.Equals(File.ReadAllText(looseFragment, Encoding.UTF8), sentinel, StringComparison.Ordinal),
                "Inspecting an MSIX Terminal Fragment modified a loose Fragment.");
            Assert(!WindowsTerminalIntegration.TryRefreshInstallMarker(root, result),
                "A package-managed Fragment unexpectedly claimed loose installer marker ownership.");
        }

        private static string DecodeTerminalChildCommand(string fragmentPath, string profileName)
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(fragmentPath, Encoding.UTF8));
            var profile = document.RootElement.GetProperty("profiles").EnumerateArray().Single(item =>
                string.Equals(item.GetProperty("name").GetString(), profileName, StringComparison.Ordinal));
            var commandLine = profile.GetProperty("commandline").GetString()!;
            const string prefix = "--command-base64 ";
            var start = commandLine.IndexOf(prefix, StringComparison.Ordinal);
            Assert(start >= 0, "Terminal Fragment command omitted --command-base64.");
            start += prefix.Length;
            var end = commandLine.IndexOf(' ', start);
            if (end < 0) end = commandLine.Length;
            return Encoding.UTF8.GetString(Convert.FromBase64String(commandLine[start..end]));
        }

        private static void ValidateRuntimePurgeBoundary(string temporaryRoot)
        {
            var localData = Path.Combine(temporaryRoot, "LocalAppData");
            var known = Installer.GetKnownRuntimeDataFilePaths(localData);
            foreach (var path in known)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "owned", new UTF8Encoding(false));
            }

            var unrelated = Path.Combine(localData, "TermLink", "Agent", "sessions", "keep.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
            File.WriteAllText(unrelated, "user/runtime buffer retained", new UTF8Encoding(false));
            var deleted = Installer.PurgeKnownRuntimeDataFiles(localData);

            Assert(deleted.Count == known.Count, "Not all allowlisted runtime files were purged.");
            Assert(known.All(path => !File.Exists(path)), "An allowlisted runtime file survived purge.");
            Assert(File.Exists(unrelated),
                "Runtime purge recursively removed a file outside the explicit allowlist.");
        }

        private static T WithUiCulture<T>(string cultureName, Func<T> action)
        {
            var previous = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                return action();
            }
            finally
            {
                CultureInfo.CurrentUICulture = previous;
            }
        }

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static T AssertThrows<T>(Action action, string message) where T : Exception
        {
            try
            {
                action();
            }
            catch (T error)
            {
                return error;
            }
            throw new InvalidOperationException(message);
        }
    }
}
