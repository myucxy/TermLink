using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;
using TermLink.UpdateCore;

namespace TermLink.Updater
{
    internal sealed class RemoteSetupTransactionRunner
    {
        private const string RecoveryValueName = "TermLinkRemoteUpdateRecovery";
        private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ReceiptPollInterval = TimeSpan.FromSeconds(2);

        public int Run(string journalPath)
        {
            journalPath = Path.GetFullPath(journalPath);
            var transactionRoot = RemoteUpdateTransactionStore.RequireTransactionRoot(
                Path.GetDirectoryName(journalPath));
            var record = RemoteUpdateTransactionStore.Load(journalPath);
            if (record.State == RemoteUpdateTransactionState.Prepared) ValidateSetup(record);
            RegisterRecovery(journalPath);

            switch (record.State)
            {
                case RemoteUpdateTransactionState.Prepared:
                    return ApplyTarget(journalPath, transactionRoot, record);
                case RemoteUpdateTransactionState.SetupRunning:
                    return RecoverInterruptedSetup(journalPath, transactionRoot, record);
                case RemoteUpdateTransactionState.AwaitingNewAgent:
                    return VerifyTargetOrRollback(journalPath, transactionRoot, record);
                case RemoteUpdateTransactionState.RollingBack:
                    return RollBack(journalPath, transactionRoot, record);
                case RemoteUpdateTransactionState.AwaitingRollbackAgent:
                    return VerifyRollback(journalPath, transactionRoot, record);
                case RemoteUpdateTransactionState.Succeeded:
                case RemoteUpdateTransactionState.RolledBack:
                    ClearRecovery();
                    return record.State == RemoteUpdateTransactionState.Succeeded ? 0 : 20;
                case RemoteUpdateTransactionState.RollbackFailed:
                    return 30;
                default:
                    throw new InvalidDataException("The remote update transaction state is unsupported.");
            }
        }

        private static int ApplyTarget(
            string journalPath,
            string transactionRoot,
            RemoteUpdateTransactionRecord record)
        {
            record.State = RemoteUpdateTransactionState.SetupRunning;
            record.LastErrorCode = null;
            record.LastErrorDetail = null;
            RemoteUpdateTransactionStore.Save(journalPath, record);
            StopProcess(record.PreviousProcessId, record.InstallRoot);
            var exitCode = LaunchSetup(record, journalPath);
            record = RemoteUpdateTransactionStore.Load(journalPath);
            if (record.State == RemoteUpdateTransactionState.SetupRunning)
                return RecoverInterruptedSetup(journalPath, transactionRoot, record);
            if (exitCode != 0 || record.State != RemoteUpdateTransactionState.AwaitingNewAgent)
            {
                record.LastErrorCode = "SETUP_PROCESS_FAILED";
                record.LastErrorDetail = "Setup exited with code " + exitCode + ".";
                record.State = RemoteUpdateTransactionState.RollingBack;
                RemoteUpdateTransactionStore.Save(journalPath, record);
                return RollBack(journalPath, transactionRoot, record);
            }
            return VerifyTargetOrRollback(journalPath, transactionRoot, record);
        }

        private static int RecoverInterruptedSetup(
            string journalPath,
            string transactionRoot,
            RemoteUpdateTransactionRecord record)
        {
            if (InstalledBuild(record.InstallRoot) == record.TargetBuild)
            {
                if (string.IsNullOrWhiteSpace(record.BackupPath))
                    record.BackupPath = FindBackupForBuild(record.InstallRoot, record.CurrentBuild);
                if (!string.IsNullOrWhiteSpace(record.BackupPath) && Directory.Exists(record.BackupPath))
                {
                    record.State = RemoteUpdateTransactionState.AwaitingNewAgent;
                    RemoteUpdateTransactionStore.Save(journalPath, record);
                    return VerifyTargetOrRollback(journalPath, transactionRoot, record);
                }
            }
            record.LastErrorCode = "SETUP_INTERRUPTED";
            record.LastErrorDetail = "Setup did not durably reach the new-Agent verification phase.";
            record.State = RemoteUpdateTransactionState.RollingBack;
            RemoteUpdateTransactionStore.Save(journalPath, record);
            return RollBack(journalPath, transactionRoot, record);
        }

        private static int VerifyTargetOrRollback(
            string journalPath,
            string transactionRoot,
            RemoteUpdateTransactionRecord record)
        {
            StartTray(record.InstallRoot);
            var receipt = WaitForReceipt(transactionRoot, record.UpdateId, "SUCCEEDED", record.HealthDeadlineUtc);
            if (receipt != null && receipt.Outcome == "SUCCEEDED" &&
                receipt.ObservedAgentBuild == record.TargetBuild)
            {
                var backupPath = RemoteUpdateTransactionStore.RequireBackupPath(
                    record.InstallRoot,
                    record.BackupPath);
                TryDeleteDirectory(backupPath);
                if (!string.IsNullOrWhiteSpace(record.BackupPath) && Directory.Exists(record.BackupPath))
                    throw new IOException("The previous installation backup could not be committed.");
                record.State = RemoteUpdateTransactionState.Succeeded;
                record.LastErrorCode = null;
                record.LastErrorDetail = null;
                RemoteUpdateTransactionStore.Save(journalPath, record);
                ClearRecovery();
                return 0;
            }
            record.LastErrorCode = "NEW_AGENT_CONNECTION_TIMEOUT";
            record.LastErrorDetail = "The new Agent did not confirm Relay and Server connectivity before the deadline.";
            record.State = RemoteUpdateTransactionState.RollingBack;
            RemoteUpdateTransactionStore.Save(journalPath, record);
            return RollBack(journalPath, transactionRoot, record);
        }

        private static int RollBack(
            string journalPath,
            string transactionRoot,
            RemoteUpdateTransactionRecord record)
        {
            record.State = RemoteUpdateTransactionState.RollingBack;
            RemoteUpdateTransactionStore.Save(journalPath, record);
            try
            {
                StopOwnedProcesses(record.InstallRoot);
                if (!string.IsNullOrWhiteSpace(record.BackupPath) && Directory.Exists(record.BackupPath))
                {
                    var backupPath = RemoteUpdateTransactionStore.RequireBackupPath(
                        record.InstallRoot,
                        record.BackupPath);
                    var quarantine = Path.Combine(
                        transactionRoot,
                        "failed-target-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                    if (Directory.Exists(record.InstallRoot)) Directory.Move(record.InstallRoot, quarantine);
                    Directory.Move(backupPath, record.InstallRoot);
                }
                if (InstalledBuild(record.InstallRoot) != record.CurrentBuild)
                    throw new InvalidDataException("The restored installation build does not match the rollback target.");
                record.State = RemoteUpdateTransactionState.AwaitingRollbackAgent;
                record.HealthDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(2);
                RemoteUpdateTransactionStore.Save(journalPath, record);
                StartTray(record.InstallRoot);
                return VerifyRollback(journalPath, transactionRoot, record);
            }
            catch (Exception error)
            {
                record.State = RemoteUpdateTransactionState.RollbackFailed;
                record.LastErrorCode = "ROLLBACK_FAILED";
                record.LastErrorDetail = SafeError(error);
                RemoteUpdateTransactionStore.Save(journalPath, record);
                return 30;
            }
        }

        private static int VerifyRollback(
            string journalPath,
            string transactionRoot,
            RemoteUpdateTransactionRecord record)
        {
            StartTray(record.InstallRoot);
            var receipt = WaitForReceipt(transactionRoot, record.UpdateId, "ROLLED_BACK", record.HealthDeadlineUtc);
            if (receipt != null && receipt.Outcome == "ROLLED_BACK" &&
                receipt.ObservedAgentBuild == record.CurrentBuild)
            {
                record.State = RemoteUpdateTransactionState.RolledBack;
                RemoteUpdateTransactionStore.Save(journalPath, record);
                ClearRecovery();
                return 20;
            }
            record.State = RemoteUpdateTransactionState.RollbackFailed;
            record.LastErrorCode = "ROLLBACK_CONNECTION_TIMEOUT";
            record.LastErrorDetail = "The restored Agent started but did not confirm Server connectivity before the deadline.";
            RemoteUpdateTransactionStore.Save(journalPath, record);
            return 30;
        }

        private static void ValidateSetup(RemoteUpdateTransactionRecord record)
        {
            WindowsUpdateSecurity.ValidateIntegrity(
                record.SetupPath,
                record.ArtifactSizeBytes,
                record.ArtifactSha256);
            WindowsUpdateSecurity.ValidateAuthenticode(
                record.SetupPath,
                WindowsUpdateSecurity.ResolveExpectedSignerThumbprint(record.InstallRoot),
                record.ExpectedSignerFingerprint);
        }

        private static int LaunchSetup(RemoteUpdateTransactionRecord record, string journalPath)
        {
            var start = new ProcessStartInfo
            {
                FileName = record.SetupPath,
                WorkingDirectory = Path.GetDirectoryName(record.SetupPath) ?? Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--install-dir");
            start.ArgumentList.Add(record.InstallRoot);
            start.ArgumentList.Add("--terminate-running");
            start.ArgumentList.Add("--no-launch");
            start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add("--remote-update-transaction");
            start.ArgumentList.Add(journalPath);
            using var process = Process.Start(start) ??
                throw new InvalidOperationException("The verified Setup process could not start.");
            process.WaitForExit();
            return process.ExitCode;
        }

        private static void RegisterRecovery(string journalPath)
        {
            var runner = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(runner) || !File.Exists(runner))
                throw new InvalidOperationException("The external Updater executable path is unavailable.");
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true) ??
                throw new UnauthorizedAccessException("The current-user recovery startup key is unavailable.");
            key.SetValue(
                RecoveryValueName,
                Quote(runner) + " remote-resume --transaction " + Quote(journalPath),
                RegistryValueKind.String);
        }

        private static void ClearRecovery()
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue(RecoveryValueName, false);
        }

        private static RemoteUpdateReceipt WaitForReceipt(
            string transactionRoot,
            Guid updateId,
            string expectedOutcome,
            DateTimeOffset deadline)
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                var receipt = RemoteUpdateTransactionStore.TryLoadReceipt(transactionRoot);
                if (receipt != null && receipt.UpdateId == updateId && receipt.Outcome == expectedOutcome) return receipt;
                Thread.Sleep(ReceiptPollInterval);
            }
            return null;
        }

        private static void StopProcess(int processId, string installRoot)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var path = process.MainModule?.FileName;
                if (!IsUnder(path, installRoot))
                    throw new InvalidDataException("The previous process is not owned by the authorized install root.");
                try { process.CloseMainWindow(); } catch (InvalidOperationException) { }
                if (!process.WaitForExit((int)ProcessStopTimeout.TotalMilliseconds))
                {
                    process.Kill(true);
                    process.WaitForExit((int)ProcessStopTimeout.TotalMilliseconds);
                }
            }
            catch (ArgumentException) { }
        }

        private static void StopOwnedProcesses(string installRoot)
        {
            foreach (var process in Process.GetProcessesByName("TermLink"))
            {
                using (process)
                {
                    try
                    {
                        if (!IsUnder(process.MainModule?.FileName, installRoot)) continue;
                        process.Kill(true);
                        process.WaitForExit((int)ProcessStopTimeout.TotalMilliseconds);
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }

        private static void StartTray(string installRoot)
        {
            var path = Path.Combine(Path.GetFullPath(installRoot), "TermLink.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("The installed TermLink Tray is missing.", path);
            var alreadyRunning = Process.GetProcessesByName("TermLink").Any(process =>
            {
                using (process)
                {
                    try { return IsUnder(process.MainModule?.FileName, installRoot); }
                    catch (InvalidOperationException) { return false; }
                }
            });
            if (alreadyRunning) return;
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = installRoot,
                UseShellExecute = true
            });
            if (started == null) throw new InvalidOperationException("The installed TermLink Tray could not start.");
        }

        private static long InstalledBuild(string installRoot)
        {
            var path = Path.Combine(Path.GetFullPath(installRoot), "TermLink.Agent.dll");
            if (!File.Exists(path)) return -1;
            var version = AssemblyName.GetAssemblyName(path).Version;
            return version == null ? -1 : WindowsUpdateVersion.GetBuild(version);
        }

        private static string FindBackupForBuild(string installRoot, long currentBuild)
        {
            var parent = Directory.GetParent(Path.GetFullPath(installRoot))?.FullName;
            if (string.IsNullOrWhiteSpace(parent)) return null;
            var candidates = Directory.GetDirectories(parent, ".termlink-backup-*")
                .Where(candidate => InstalledBuild(candidate) == currentBuild)
                .ToArray();
            return candidates.Length == 1 ? Path.GetFullPath(candidates[0]) : null;
        }

        private static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            Directory.Delete(Path.GetFullPath(path), true);
        }

        private static string SafeError(Exception error)
        {
            var value = error.GetType().Name + ": " + error.Message;
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 1000 ? value : value.Substring(0, 1000);
        }
    }
}
