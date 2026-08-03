using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TermLink.Updater
{
    public enum UpdateTransactionState
    {
        Prepared,
        ApplyingTarget,
        TargetApplied,
        RollingBack,
        Succeeded,
        RolledBack,
        DeploymentFailed,
        RollbackFailed
    }

    public sealed class UpdateTransactionRecord
    {
        public int SchemaVersion { get; set; } = 1;
        public UpdateTransactionState State { get; set; }
        public string CurrentVersion { get; set; }
        public string TargetManifestPath { get; set; }
        public string TargetSignaturePath { get; set; }
        public string TargetPackagePath { get; set; }
        public string RollbackManifestPath { get; set; }
        public string RollbackSignaturePath { get; set; }
        public string RollbackPackagePath { get; set; }
        public string PublisherCertificatePath { get; set; }
        public bool AllowUntrustedPublisher { get; set; }
        public string LastError { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    public interface IUpdateDeployment
    {
        void Install(VerifiedUpdatePackage package, bool allowDowngrade);
        bool CheckHealth(VerifiedUpdatePackage package);
        void StartTray(VerifiedUpdatePackage package);
    }

    public static class UpdateJournal
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static UpdateTransactionRecord Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("The update transaction journal was not found.", path);
            }
            var record = JsonSerializer.Deserialize<UpdateTransactionRecord>(File.ReadAllBytes(path), Options);
            if (record == null || record.SchemaVersion != 1 || !Enum.IsDefined(typeof(UpdateTransactionState), record.State))
            {
                throw new InvalidDataException("The update transaction journal is invalid or unsupported.");
            }
            RequirePath(record.CurrentVersion, "current version");
            RequirePath(record.TargetManifestPath, "target manifest");
            RequirePath(record.TargetSignaturePath, "target signature");
            RequirePath(record.TargetPackagePath, "target package");
            RequirePath(record.RollbackManifestPath, "rollback manifest");
            RequirePath(record.RollbackSignaturePath, "rollback signature");
            RequirePath(record.RollbackPackagePath, "rollback package");
            RequirePath(record.PublisherCertificatePath, "publisher certificate");
            return record;
        }

        public static void Save(string path, UpdateTransactionRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            var destination = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(directory)) throw new InvalidDataException("The journal path has no directory.");
            Directory.CreateDirectory(directory);
            record.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(record, Options) + Environment.NewLine,
                    new UTF8Encoding(false));
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void RequirePath(string value, string description)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("The update journal " + description + " is missing.");
            }
        }
    }

    public sealed class UpdateTransactionRunner
    {
        private readonly IUpdateDeployment deployment;
        private readonly Func<DateTimeOffset> utcNow;

        public UpdateTransactionRunner(IUpdateDeployment deployment)
            : this(deployment, () => DateTimeOffset.UtcNow)
        {
        }

        public UpdateTransactionRunner(IUpdateDeployment deployment, Func<DateTimeOffset> utcNow)
        {
            this.deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
            this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        public int Run(string journalPath)
        {
            var record = UpdateJournal.Load(journalPath);
            var currentVersion = UpdatePackageVerifier.ParseFourPartVersion(record.CurrentVersion, "current version");
            var target = Verify(record, true);
            var rollback = Verify(record, false);
            UpdatePackageVerifier.ValidateUpgrade(target, rollback, currentVersion);

            while (true)
            {
                switch (record.State)
                {
                    case UpdateTransactionState.Prepared:
                    case UpdateTransactionState.ApplyingTarget:
                        record.State = UpdateTransactionState.ApplyingTarget;
                        record.LastError = null;
                        UpdateJournal.Save(journalPath, record);
                        try
                        {
                            deployment.Install(target, false);
                            record.State = UpdateTransactionState.TargetApplied;
                            UpdateJournal.Save(journalPath, record);
                        }
                        catch (Exception error)
                        {
                            record.State = UpdateTransactionState.DeploymentFailed;
                            record.LastError = SafeError(error);
                            UpdateJournal.Save(journalPath, record);
                            TryStartTray(rollback);
                            return 10;
                        }
                        break;

                    case UpdateTransactionState.TargetApplied:
                        try
                        {
                            if (deployment.CheckHealth(target))
                            {
                                record.State = UpdateTransactionState.Succeeded;
                                record.LastError = null;
                                UpdateJournal.Save(journalPath, record);
                                TryStartTray(target);
                                return 0;
                            }
                            record.LastError = "The updated package failed its health check.";
                        }
                        catch (Exception error)
                        {
                            record.LastError = SafeError(error);
                        }
                        record.State = UpdateTransactionState.RollingBack;
                        UpdateJournal.Save(journalPath, record);
                        break;

                    case UpdateTransactionState.RollingBack:
                        try
                        {
                            deployment.Install(rollback, true);
                            if (!deployment.CheckHealth(rollback))
                            {
                                throw new InvalidOperationException("The rollback package failed its health check.");
                            }
                            record.State = UpdateTransactionState.RolledBack;
                            UpdateJournal.Save(journalPath, record);
                            TryStartTray(rollback);
                            return 20;
                        }
                        catch (Exception error)
                        {
                            record.State = UpdateTransactionState.RollbackFailed;
                            record.LastError = SafeError(error);
                            UpdateJournal.Save(journalPath, record);
                            return 30;
                        }

                    case UpdateTransactionState.Succeeded:
                        return 0;
                    case UpdateTransactionState.RolledBack:
                        return 20;
                    case UpdateTransactionState.DeploymentFailed:
                        return 10;
                    case UpdateTransactionState.RollbackFailed:
                        return 30;
                    default:
                        throw new InvalidDataException("The update transaction state is unsupported.");
                }
            }
        }

        private VerifiedUpdatePackage Verify(UpdateTransactionRecord record, bool target)
        {
            return UpdatePackageVerifier.Verify(
                target ? record.TargetManifestPath : record.RollbackManifestPath,
                target ? record.TargetSignaturePath : record.RollbackSignaturePath,
                target ? record.TargetPackagePath : record.RollbackPackagePath,
                record.PublisherCertificatePath,
                record.AllowUntrustedPublisher,
                utcNow());
        }

        private void TryStartTray(VerifiedUpdatePackage package)
        {
            try
            {
                deployment.StartTray(package);
            }
            catch
            {
                // Deployment outcome is already durable; startup will retry at the next user login.
            }
        }

        private static string SafeError(Exception error)
        {
            var text = error.GetType().Name + ": " + error.Message;
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 1000 ? text : text.Substring(0, 1000);
        }
    }
}
