using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TermLink.UpdateCore
{
    public enum RemoteUpdateTransactionState
    {
        Prepared,
        SetupRunning,
        AwaitingNewAgent,
        RollingBack,
        AwaitingRollbackAgent,
        Succeeded,
        RolledBack,
        RollbackFailed
    }

    public sealed class RemoteUpdateTransactionRecord
    {
        public int SchemaVersion { get; set; } = 1;
        public Guid UpdateId { get; set; }
        public Guid PcId { get; set; }
        public RemoteUpdateTransactionState State { get; set; }
        public string CurrentVersion { get; set; }
        public long CurrentBuild { get; set; }
        public string TargetVersion { get; set; }
        public long TargetBuild { get; set; }
        public string SetupPath { get; set; }
        public long ArtifactSizeBytes { get; set; }
        public string ArtifactSha256 { get; set; }
        public string InstallRoot { get; set; }
        public string BackupPath { get; set; }
        public string ExpectedSignerFingerprint { get; set; }
        public int PreviousProcessId { get; set; }
        public DateTimeOffset HealthDeadlineUtc { get; set; }
        public string LastErrorCode { get; set; }
        public string LastErrorDetail { get; set; }
        public bool FailureReported { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    public sealed class RemoteUpdateReceipt
    {
        public int SchemaVersion { get; set; } = 1;
        public Guid UpdateId { get; set; }
        public string Outcome { get; set; }
        public string ObservedAgentVersion { get; set; }
        public long ObservedAgentBuild { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
    }

    public static class RemoteUpdateTransactionStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermLink",
            "remote-updates");

        public static string CreateTransactionRoot(Guid updateId)
        {
            if (updateId == Guid.Empty) throw new ArgumentException("Update ID is required.", nameof(updateId));
            var root = Path.Combine(DefaultRoot, "transaction-" + updateId.ToString("N"));
            Directory.CreateDirectory(root);
            return Path.GetFullPath(root);
        }

        public static string JournalPath(string transactionRoot) =>
            Path.Combine(RequireTransactionRoot(transactionRoot), "remote-update.json");

        public static string ReceiptPath(string transactionRoot) =>
            Path.Combine(RequireTransactionRoot(transactionRoot), "agent-receipt.json");

        public static string RequireBackupPath(string installRoot, string backupPath)
        {
            if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(backupPath))
                throw new InvalidDataException("The remote update backup path is incomplete.");
            var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Directory.GetParent(root)?.FullName;
            var backup = Path.GetFullPath(backupPath).TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(parent) ||
                !string.Equals(Directory.GetParent(backup)?.FullName, parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(backup).StartsWith(".termlink-backup-", StringComparison.Ordinal))
                throw new InvalidDataException("The remote update backup is outside the authorized install parent.");
            return backup;
        }

        public static void Save(string path, RemoteUpdateTransactionRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            var destination = RequireJournalDestination(path);
            ValidateRecord(record, Path.GetDirectoryName(destination));
            record.UpdatedAtUtc = DateTimeOffset.UtcNow;
            WriteAtomic(destination, JsonSerializer.Serialize(record, Options));
        }

        public static RemoteUpdateTransactionRecord Load(string path)
        {
            var fullPath = RequireJournalPath(path);
            var record = JsonSerializer.Deserialize<RemoteUpdateTransactionRecord>(File.ReadAllBytes(fullPath), Options);
            if (record == null) throw new InvalidDataException("The remote update transaction is empty.");
            ValidateRecord(record, Path.GetDirectoryName(fullPath));
            return record;
        }

        public static void SaveReceipt(string transactionRoot, RemoteUpdateReceipt receipt)
        {
            var root = RequireTransactionRoot(transactionRoot);
            ValidateReceipt(root, receipt);
            receipt.CreatedAtUtc = DateTimeOffset.UtcNow;
            WriteAtomic(ReceiptPath(root), JsonSerializer.Serialize(receipt, Options));
        }

        public static RemoteUpdateReceipt TryLoadReceipt(string transactionRoot)
        {
            var path = ReceiptPath(transactionRoot);
            if (!File.Exists(path)) return null;
            var receipt = JsonSerializer.Deserialize<RemoteUpdateReceipt>(File.ReadAllBytes(path), Options);
            ValidateReceipt(Path.GetDirectoryName(path), receipt);
            return receipt;
        }

        public static string FindActiveJournal(Guid pcId)
        {
            if (pcId == Guid.Empty || !Directory.Exists(DefaultRoot)) return null;
            string selectedPath = null;
            var selectedUpdatedAt = DateTimeOffset.MinValue;
            foreach (var directory in Directory.EnumerateDirectories(DefaultRoot, "transaction-*"))
            {
                var path = Path.Combine(directory, "remote-update.json");
                if (!File.Exists(path)) continue;
                try
                {
                    var record = Load(path);
                    var active = record.State != RemoteUpdateTransactionState.Succeeded &&
                        record.State != RemoteUpdateTransactionState.RolledBack &&
                        (record.State != RemoteUpdateTransactionState.RollbackFailed || !record.FailureReported);
                    if (record.PcId == pcId && active &&
                        (selectedPath == null || record.UpdatedAtUtc > selectedUpdatedAt ||
                         (record.UpdatedAtUtc == selectedUpdatedAt &&
                          string.CompareOrdinal(path, selectedPath) > 0)))
                    {
                        selectedPath = path;
                        selectedUpdatedAt = record.UpdatedAtUtc;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
                catch (JsonException) { }
            }
            return selectedPath;
        }

        public static string RequireTransactionRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Transaction root is required.");
            var root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            var expectedParent = Path.GetFullPath(DefaultRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("transaction-", StringComparison.Ordinal))
                throw new InvalidDataException("The remote update transaction is outside the managed update root.");
            return root;
        }

        private static string RequireJournalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
                throw new FileNotFoundException("The remote update transaction was not found.", value);
            var fullPath = Path.GetFullPath(value);
            var root = RequireTransactionRoot(Path.GetDirectoryName(fullPath));
            if (!string.Equals(fullPath, Path.Combine(root, "remote-update.json"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The remote update journal filename is invalid.");
            return fullPath;
        }

        private static string RequireJournalDestination(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("The remote update transaction path is required.");
            var fullPath = Path.GetFullPath(value);
            var root = RequireTransactionRoot(Path.GetDirectoryName(fullPath));
            if (!string.Equals(fullPath, Path.Combine(root, "remote-update.json"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The remote update journal filename is invalid.");
            return fullPath;
        }

        private static void ValidateRecord(RemoteUpdateTransactionRecord record, string transactionRoot)
        {
            if (record.SchemaVersion != 1 || record.UpdateId == Guid.Empty || record.PcId == Guid.Empty ||
                !Enum.IsDefined(typeof(RemoteUpdateTransactionState), record.State) ||
                string.IsNullOrWhiteSpace(record.CurrentVersion) || string.IsNullOrWhiteSpace(record.TargetVersion) ||
                record.CurrentBuild < 0 || record.TargetBuild <= record.CurrentBuild ||
                string.IsNullOrWhiteSpace(record.SetupPath) ||
                record.ArtifactSizeBytes < 1 || !WindowsUpdateSecurity.IsHex(
                    WindowsUpdateSecurity.NormalizeHex(record.ArtifactSha256), 64) ||
                string.IsNullOrWhiteSpace(record.InstallRoot) || record.PreviousProcessId < 1 ||
                record.HealthDeadlineUtc == default)
                throw new InvalidDataException("The remote update transaction is invalid or incomplete.");
            if (!WindowsUpdateSecurity.IsHex(
                WindowsUpdateSecurity.NormalizeHex(record.ExpectedSignerFingerprint), 40))
                throw new InvalidDataException("The remote update signer fingerprint is invalid.");
            var root = RequireTransactionRoot(transactionRoot);
            if (!string.Equals(
                    Path.GetFullPath(record.SetupPath),
                    Path.Combine(root, "TermLink-Setup.exe"),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The verified Setup path is outside its remote update transaction.");
            if (!string.Equals(
                    Path.GetFileName(root),
                    "transaction-" + record.UpdateId.ToString("N"),
                    StringComparison.Ordinal))
                throw new InvalidDataException("The transaction directory does not match the update ID.");
            if (record.State == RemoteUpdateTransactionState.Prepared &&
                !File.Exists(Path.GetFullPath(record.SetupPath)))
                throw new FileNotFoundException("The verified Setup executable is missing.", record.SetupPath);
            var installRoot = Path.GetFullPath(record.InstallRoot);
            var installMayBeInTransit = record.State == RemoteUpdateTransactionState.SetupRunning ||
                record.State == RemoteUpdateTransactionState.RollingBack ||
                record.State == RemoteUpdateTransactionState.RollbackFailed;
            if (!installMayBeInTransit &&
                (!File.Exists(Path.Combine(installRoot, "TermLink.Agent.dll")) ||
                    !File.Exists(Path.Combine(installRoot, "TermLink.Agent.exe"))))
                throw new FileNotFoundException("The authorized Agent install root is incomplete.", record.InstallRoot);
            if (!string.IsNullOrWhiteSpace(record.BackupPath))
                RequireBackupPath(installRoot, record.BackupPath);
        }

        private static void ValidateReceipt(string transactionRoot, RemoteUpdateReceipt receipt)
        {
            if (receipt == null || receipt.SchemaVersion != 1 || receipt.UpdateId == Guid.Empty ||
                (receipt.Outcome != "SUCCEEDED" && receipt.Outcome != "ROLLED_BACK") ||
                string.IsNullOrWhiteSpace(receipt.ObservedAgentVersion) || receipt.ObservedAgentBuild < 0)
                throw new InvalidDataException("The remote update receipt is invalid.");
            var record = Load(JournalPath(transactionRoot));
            var expectedBuild = receipt.Outcome == "SUCCEEDED" ? record.TargetBuild : record.CurrentBuild;
            if (receipt.UpdateId != record.UpdateId || receipt.ObservedAgentBuild != expectedBuild)
                throw new InvalidDataException("The remote update receipt does not match its transaction build.");
        }

        private static void WriteAtomic(string path, string json)
        {
            var destination = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("The update state path has no directory.");
            Directory.CreateDirectory(directory);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, json + Environment.NewLine, new UTF8Encoding(false));
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
