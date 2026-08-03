using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TermLink.Updater;

namespace TermLink.Updater.Validation
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
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
