using System;
using System.IO;
using TermLink.UpdateCore;

namespace TermLink.UpdateCore.Validation
{
    internal static class Program
    {
        public static int Main()
        {
            var updateId = Guid.NewGuid();
            var root = RemoteUpdateTransactionStore.CreateTransactionRoot(updateId);
            try
            {
                ValidateBuildIdentity();
                ValidateTransactionAndReceipt(updateId, root);
                ValidatePathBoundary(root);
                Console.WriteLine("UpdateCore validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                var managedRoot = Path.GetFullPath(RemoteUpdateTransactionStore.DefaultRoot)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var resolved = Path.GetFullPath(root);
                if (resolved.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(resolved), "transaction-" + updateId.ToString("N"), StringComparison.Ordinal) &&
                    Directory.Exists(resolved))
                    Directory.Delete(resolved, recursive: true);
            }
        }

        private static void ValidateBuildIdentity()
        {
            Assert(WindowsUpdateVersion.GetBuild(new Version(0, 1, 31, 37)) == 37,
                "four-part versions must use the revision as AppShip build");
            Assert(WindowsUpdateVersion.GetBuild(new Version(0, 1, 31)) == 31,
                "three-part versions must fall back to the build component");
            Assert(WindowsUpdateVersion.TryGetBuild("0.1.31.37", out var build) && build == 37,
                "string build parsing failed");
            Assert(!WindowsUpdateVersion.TryGetBuild("0.1.latest", out _),
                "invalid version text was accepted");
        }

        private static void ValidateTransactionAndReceipt(Guid updateId, string root)
        {
            var setupPath = Path.Combine(root, "TermLink-Setup.exe");
            File.WriteAllBytes(setupPath, new byte[] { 1 });
            var installRoot = Path.Combine(root, "installed");
            Directory.CreateDirectory(installRoot);
            File.WriteAllBytes(Path.Combine(installRoot, "TermLink.Agent.dll"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(installRoot, "TermLink.Agent.exe"), new byte[] { 1 });
            var journalPath = RemoteUpdateTransactionStore.JournalPath(root);
            var record = new RemoteUpdateTransactionRecord
            {
                UpdateId = updateId,
                PcId = Guid.NewGuid(),
                State = RemoteUpdateTransactionState.Prepared,
                CurrentVersion = "0.1.31.37",
                CurrentBuild = 37,
                TargetVersion = "0.1.32",
                TargetBuild = 38,
                SetupPath = setupPath,
                ArtifactSizeBytes = 1,
                ArtifactSha256 = new string('a', 64),
                InstallRoot = installRoot,
                ExpectedSignerFingerprint = new string('b', 40),
                PreviousProcessId = 1,
                HealthDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1)
            };
            RemoteUpdateTransactionStore.Save(journalPath, record);
            var loaded = RemoteUpdateTransactionStore.Load(journalPath);
            Assert(loaded.CurrentBuild == 37 && loaded.TargetBuild == 38,
                "transaction build identity did not round-trip");

            RemoteUpdateTransactionStore.SaveReceipt(root, new RemoteUpdateReceipt
            {
                UpdateId = updateId,
                Outcome = "SUCCEEDED",
                ObservedAgentVersion = "0.1.32.38",
                ObservedAgentBuild = 38
            });
            var receipt = RemoteUpdateTransactionStore.TryLoadReceipt(root);
            Assert(receipt != null && receipt.ObservedAgentBuild == 38,
                "valid build receipt did not round-trip");

            AssertThrows<InvalidDataException>(() => RemoteUpdateTransactionStore.SaveReceipt(root,
                new RemoteUpdateReceipt
                {
                    UpdateId = updateId,
                    Outcome = "SUCCEEDED",
                    ObservedAgentVersion = "0.1.31.37",
                    ObservedAgentBuild = 37
                }), "mismatched target build receipt was accepted");

            ValidateActiveJournalSelection(record);
        }

        private static void ValidateActiveJournalSelection(RemoteUpdateTransactionRecord first)
        {
            var secondId = Guid.NewGuid();
            var secondRoot = RemoteUpdateTransactionStore.CreateTransactionRoot(secondId);
            try
            {
                var secondSetup = Path.Combine(secondRoot, "TermLink-Setup.exe");
                File.WriteAllBytes(secondSetup, new byte[] { 2 });
                var secondInstall = Path.Combine(secondRoot, "installed");
                Directory.CreateDirectory(secondInstall);
                File.WriteAllBytes(Path.Combine(secondInstall, "TermLink.Agent.dll"), new byte[] { 2 });
                File.WriteAllBytes(Path.Combine(secondInstall, "TermLink.Agent.exe"), new byte[] { 2 });
                var second = new RemoteUpdateTransactionRecord
                {
                    UpdateId = secondId,
                    PcId = first.PcId,
                    State = RemoteUpdateTransactionState.Prepared,
                    CurrentVersion = first.CurrentVersion,
                    CurrentBuild = first.CurrentBuild,
                    TargetVersion = first.TargetVersion,
                    TargetBuild = first.TargetBuild,
                    SetupPath = secondSetup,
                    ArtifactSizeBytes = 1,
                    ArtifactSha256 = first.ArtifactSha256,
                    InstallRoot = secondInstall,
                    ExpectedSignerFingerprint = first.ExpectedSignerFingerprint,
                    PreviousProcessId = 1,
                    HealthDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1)
                };
                var secondJournal = RemoteUpdateTransactionStore.JournalPath(secondRoot);
                RemoteUpdateTransactionStore.Save(secondJournal, second);
                Assert(string.Equals(
                        RemoteUpdateTransactionStore.FindActiveJournal(first.PcId),
                        secondJournal,
                        StringComparison.OrdinalIgnoreCase),
                    "the newest active transaction journal was not selected");

                second.State = RemoteUpdateTransactionState.RollbackFailed;
                second.FailureReported = true;
                File.Delete(Path.Combine(secondInstall, "TermLink.Agent.dll"));
                File.Delete(secondSetup);
                RemoteUpdateTransactionStore.Save(secondJournal, second);
                Assert(RemoteUpdateTransactionStore.Load(secondJournal).State ==
                        RemoteUpdateTransactionState.RollbackFailed,
                    "a rollback failure with an in-transit install root could not be recovered");
                Assert(!string.Equals(
                        RemoteUpdateTransactionStore.FindActiveJournal(first.PcId),
                        secondJournal,
                        StringComparison.OrdinalIgnoreCase),
                    "a reported rollback failure continued to block newer jobs");
            }
            finally
            {
                if (Directory.Exists(secondRoot)) Directory.Delete(secondRoot, recursive: true);
            }
        }

        private static void ValidatePathBoundary(string root)
        {
            var outside = Path.Combine(Path.GetTempPath(), "remote-update.json");
            AssertThrows<InvalidDataException>(() => RemoteUpdateTransactionStore.RequireTransactionRoot(
                Path.GetDirectoryName(outside)), "path outside managed update root was accepted");

            var loaded = RemoteUpdateTransactionStore.Load(RemoteUpdateTransactionStore.JournalPath(root));
            AssertThrows<InvalidDataException>(() => RemoteUpdateTransactionStore.Save(
                Path.Combine(root, "other.json"), loaded), "non-journal transaction filename was accepted");
            AssertThrows<InvalidDataException>(() => RemoteUpdateTransactionStore.RequireBackupPath(
                loaded.InstallRoot,
                Path.Combine(root, "nested", ".termlink-backup-outside")),
                "backup outside the install parent was accepted");
        }

        private static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
