using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Text.Json;
using Microsoft.Win32;
using TermLink.UpdateCore;
using TermLink.Updater;

namespace TermLink.Updater.Validation
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            if (args.SequenceEqual(new[] { "--start-hidden" }) &&
                Environment.GetEnvironmentVariable("TERMLINK_TEST_ROLLBACK_JOURNAL") is { } receiptJournal)
            {
                var receiptRecord = RemoteUpdateTransactionStore.Load(receiptJournal);
                RemoteUpdateTransactionStore.SaveReceipt(Path.GetDirectoryName(receiptJournal), new RemoteUpdateReceipt
                {
                    UpdateId = receiptRecord.UpdateId, Outcome = "ROLLED_BACK",
                    ObservedAgentVersion = receiptRecord.CurrentVersion, ObservedAgentBuild = receiptRecord.CurrentBuild
                });
                return 0;
            }
            if (args.Length == 2 && args[0] == "--remote-rollback-test") return ValidateRemoteRollback(args[1]);
            if (args.Length == 2 && args[0] == "--remote-child") { File.WriteAllText(args[1], Environment.ProcessId.ToString()); Thread.Sleep(Timeout.Infinite); return 0; }
            if (args.Length == 2 && args[0] == "--remote-parent") {
                using var child=Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"DescendantProbe.exe")) {
                    Arguments="--remote-child \""+args[1]+"\"",UseShellExecute=false,CreateNoWindow=true });
                Thread.Sleep(Timeout.Infinite); return 0;
            }
            if (args.Length == 1 && args[0] == "--remote-stop-tree-test") return ValidateRemoteProcessStop();
            try
            {
                var options = Options.Parse(args);
                var root = Path.Combine(Path.GetTempPath(), "TermLink-UpdaterState-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    var results = new List<object>
                    {
                        RunCase(root, "success", options, UpdateTransactionState.Prepared, new FakeDeployment(), 0,
                            UpdateTransactionState.Succeeded, options.TargetVersion),
                        RunCase(root, "deployment_failure", options, UpdateTransactionState.Prepared,
                            new FakeDeployment { FailTargetDeployment = true }, 10,
                            UpdateTransactionState.DeploymentFailed, options.CurrentVersion),
                        RunCase(root, "health_failure_rollback", options, UpdateTransactionState.Prepared,
                            new FakeDeployment { FailTargetHealth = true }, 20,
                            UpdateTransactionState.RolledBack, options.CurrentVersion),
                        RunCase(root, "rollback_failure", options, UpdateTransactionState.Prepared,
                            new FakeDeployment { FailTargetHealth = true, FailRollbackDeployment = true }, 30,
                            UpdateTransactionState.RollbackFailed, options.TargetVersion),
                        RunCase(root, "resume_rollback", options, UpdateTransactionState.RollingBack,
                            new FakeDeployment { InstalledVersion = options.TargetVersion }, 20,
                            UpdateTransactionState.RolledBack, options.CurrentVersion)
                    };
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        passed = true,
                        cases = results
                    }));
                    return 0;
                }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
        }

        private static int ValidateRemoteRollback(string installParent)
        {
            var id = Guid.NewGuid();
            var root = Path.Combine(Path.GetFullPath(installParent), "TermLink-RollbackValidation-" + id.ToString("N"));
            var install = Path.Combine(root, "installed");
            var backup = Path.Combine(root, ".termlink-backup-" + id.ToString("N"));
            var transaction = RemoteUpdateTransactionStore.CreateTransactionRoot(id);
            var journal = RemoteUpdateTransactionStore.JournalPath(transaction);
            using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            var previousRecovery = runKey.GetValue("TermLinkRemoteUpdateRecovery");
            var previousMarker = Environment.GetEnvironmentVariable("TERMLINK_TEST_ROLLBACK_JOURNAL");
            try
            {
                Directory.CreateDirectory(install);
                Directory.CreateDirectory(backup);
                foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                    File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));
                File.Copy(Environment.ProcessPath, Path.Combine(backup, "TermLink.exe"));
                File.Copy(Environment.ProcessPath, Path.Combine(backup, "TermLink.Agent.exe"));
                var assemblyPath = typeof(Program).Assembly.Location;
                File.Copy(assemblyPath, Path.Combine(backup, "TermLink.Agent.dll"));
                File.WriteAllText(Path.Combine(install, "TermLink.Agent.exe"), "failed target");
                File.WriteAllText(Path.Combine(install, "TermLink.Agent.dll"), "failed target");
                var build = WindowsUpdateVersion.GetBuild(System.Reflection.AssemblyName.GetAssemblyName(assemblyPath).Version);
                RemoteUpdateTransactionStore.Save(journal, new RemoteUpdateTransactionRecord
                {
                    UpdateId = id, PcId = Guid.NewGuid(), State = RemoteUpdateTransactionState.RollingBack,
                    InstallRoot = install, BackupPath = backup, CurrentVersion = "fixture", CurrentBuild = build,
                    TargetVersion = "fixture-target", TargetBuild = build + 1, PreviousProcessId = Environment.ProcessId,
                    SetupPath = Path.Combine(transaction, "TermLink-Setup.exe"), ArtifactSizeBytes = 1,
                    ArtifactSha256 = new string('a', 64), ExpectedSignerFingerprint = new string('b', 40),
                    HealthDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1)
                });
                Environment.SetEnvironmentVariable("TERMLINK_TEST_ROLLBACK_JOURNAL", journal);
                var code = new RemoteSetupTransactionRunner().Run(journal);
                var final = RemoteUpdateTransactionStore.Load(journal);
                Assert(code == 20 && final.State == RemoteUpdateTransactionState.RolledBack,
                    "Remote rollback did not durably restore and verify the previous build.");
                Assert(Directory.Exists(RemoteSetupTransactionRunner.FailedTargetPath(install, id)) && !Directory.Exists(backup),
                    "Remote rollback did not quarantine the failed target beside the installation.");
                Console.WriteLine("PASS real remote rollback: journal on " + Path.GetPathRoot(transaction) +
                    ", installation on " + Path.GetPathRoot(install) + "; failed target preserved and previous build receipt verified.");
                return 0;
            }
            finally
            {
                Environment.SetEnvironmentVariable("TERMLINK_TEST_ROLLBACK_JOURNAL", previousMarker);
                if (previousRecovery == null) runKey.DeleteValue("TermLinkRemoteUpdateRecovery", false);
                else runKey.SetValue("TermLinkRemoteUpdateRecovery", previousRecovery);
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.Delete(transaction, true);
            }
        }

        private static int ValidateRemoteProcessStop()
        {
            var root=Path.Combine(Path.GetTempPath(),"TermLink-UpdaterParent-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Process parent=null, child=null;
            try {
                var install=Path.Combine(root,"installed");
                var quarantine=RemoteSetupTransactionRunner.FailedTargetPath(install,Guid.NewGuid());
                if(Path.GetDirectoryName(quarantine)!=Path.GetDirectoryName(install)) throw new Exception("Failed target would be moved across volumes during rollback.");
                foreach(var file in Directory.EnumerateFiles(AppContext.BaseDirectory)) File.Copy(file,Path.Combine(root,Path.GetFileName(file)));
                File.Copy(Environment.ProcessPath,Path.Combine(root,"TermLink.exe"));
                File.Copy(Environment.ProcessPath,Path.Combine(root,"DescendantProbe.exe"));
                var marker=Path.Combine(root,"child.pid");
                parent=Process.Start(new ProcessStartInfo(Path.Combine(root,"TermLink.exe")) {Arguments="--remote-parent \""+marker+"\"",UseShellExecute=false,CreateNoWindow=true});
                var deadline=DateTime.UtcNow.AddSeconds(10);
                while(!File.Exists(marker)&&DateTime.UtcNow<deadline) Thread.Sleep(50);
                child=Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
                RemoteSetupTransactionRunner.StopOwnedProcesses(root);
                if(!parent.WaitForExit(5000)||child.HasExited) throw new Exception("Stopping the old UI killed its external updater descendant.");
                Console.WriteLine("PASS remote updater survives old UI termination; only the owned UI process stopped.");
                return 0;
            } finally {
                if(child!=null){if(!child.HasExited)child.Kill();child.WaitForExit(5000);child.Dispose();}
                if(parent!=null){if(!parent.HasExited)parent.Kill();parent.WaitForExit(5000);parent.Dispose();}
                Directory.Delete(root,true);
            }
        }

        private static object RunCase(
            string root,
            string name,
            Options options,
            UpdateTransactionState initialState,
            FakeDeployment deployment,
            int expectedExitCode,
            UpdateTransactionState expectedState,
            string expectedInstalledVersion)
        {
            if (string.IsNullOrEmpty(deployment.InstalledVersion)) deployment.InstalledVersion = options.CurrentVersion;
            deployment.TargetVersion = options.TargetVersion;
            var caseRoot = Path.Combine(root, name);
            Directory.CreateDirectory(caseRoot);
            var journalPath = Path.Combine(caseRoot, "transaction.json");
            UpdateJournal.Save(journalPath, new UpdateTransactionRecord
            {
                State = initialState,
                CurrentVersion = options.CurrentVersion,
                TargetManifestPath = options.TargetManifest,
                TargetSignaturePath = options.TargetSignature,
                TargetPackagePath = options.TargetPackage,
                RollbackManifestPath = options.RollbackManifest,
                RollbackSignaturePath = options.RollbackSignature,
                RollbackPackagePath = options.RollbackPackage,
                PublisherCertificatePath = options.Certificate,
                AllowUntrustedPublisher = true
            });
            var exitCode = new UpdateTransactionRunner(deployment).Run(journalPath);
            var record = UpdateJournal.Load(journalPath);
            Assert(exitCode == expectedExitCode, name + " returned the wrong exit code.");
            Assert(record.State == expectedState, name + " persisted the wrong terminal state.");
            Assert(
                string.Equals(deployment.InstalledVersion, expectedInstalledVersion, StringComparison.Ordinal),
                name + " left the wrong version installed.");
            if (expectedState == UpdateTransactionState.Succeeded || expectedState == UpdateTransactionState.RolledBack)
            {
                Assert(deployment.StartedTrayVersions.Count == 1, name + " did not restart exactly one Tray.");
            }
            if (name == "resume_rollback")
            {
                Assert(deployment.InstallCalls.SequenceEqual(new[] { options.CurrentVersion + ":rollback" }),
                    "resume_rollback repeated the target deployment.");
            }
            return new
            {
                name,
                exit_code = exitCode,
                state = record.State.ToString(),
                installed_version = deployment.InstalledVersion,
                install_calls = deployment.InstallCalls,
                tray_starts = deployment.StartedTrayVersions.Count
            };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    internal sealed class FakeDeployment : IUpdateDeployment
    {
        public bool FailTargetDeployment { get; set; }
        public bool FailTargetHealth { get; set; }
        public bool FailRollbackDeployment { get; set; }
        public string InstalledVersion { get; set; }
        public string TargetVersion { get; set; }
        public List<string> InstallCalls { get; } = new List<string>();
        public List<string> StartedTrayVersions { get; } = new List<string>();

        public void Install(VerifiedUpdatePackage package, bool allowDowngrade)
        {
            InstallCalls.Add(package.Manifest.Version + (allowDowngrade ? ":rollback" : ":target"));
            if (!allowDowngrade && FailTargetDeployment) throw new IOException("injected target deployment failure");
            if (allowDowngrade && FailRollbackDeployment) throw new IOException("injected rollback deployment failure");
            InstalledVersion = package.Manifest.Version;
        }

        public bool CheckHealth(VerifiedUpdatePackage package)
        {
            return string.Equals(InstalledVersion, package.Manifest.Version, StringComparison.Ordinal) &&
                !(FailTargetHealth && string.Equals(package.Manifest.Version, TargetVersion, StringComparison.Ordinal));
        }

        public void StartTray(VerifiedUpdatePackage package)
        {
            StartedTrayVersions.Add(package.Manifest.Version);
        }
    }

    internal sealed class Options
    {
        public string TargetManifest { get; private set; }
        public string TargetSignature { get; private set; }
        public string TargetPackage { get; private set; }
        public string TargetVersion { get; private set; }
        public string RollbackManifest { get; private set; }
        public string RollbackSignature { get; private set; }
        public string RollbackPackage { get; private set; }
        public string CurrentVersion { get; private set; }
        public string Certificate { get; private set; }

        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Validation arguments must be --name value pairs.");
                }
                values.Add(args[index].Substring(2), args[index + 1]);
            }
            return new Options
            {
                TargetManifest = Required(values, "target-manifest"),
                TargetSignature = Required(values, "target-signature"),
                TargetPackage = Required(values, "target-package"),
                TargetVersion = Required(values, "target-version"),
                RollbackManifest = Required(values, "rollback-manifest"),
                RollbackSignature = Required(values, "rollback-signature"),
                RollbackPackage = Required(values, "rollback-package"),
                CurrentVersion = Required(values, "current-version"),
                Certificate = Required(values, "certificate")
            };
        }

        private static string Required(IDictionary<string, string> values, string name)
        {
            string value;
            if (!values.TryGetValue(name, out value)) throw new ArgumentException("--" + name + " is required.");
            return value;
        }
    }
}
