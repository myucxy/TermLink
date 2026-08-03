using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TermLink.Updater
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                var options = UpdateOptions.Parse(args);
                switch (options.Command)
                {
                    case "verify":
                        var verified = Verify(options, string.Empty);
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            verified.Manifest.PackageIdentity,
                            verified.Manifest.Version,
                            verified.Manifest.Sha256,
                            verified.CertificateThumbprint
                        }));
                        return 0;
                    case "launch":
                        return Launch(options);
                    case "execute":
                        return new UpdateTransactionRunner(new PowerShellMsixDeployment()).Run(
                            options.Required("transaction"));
                    default:
                        throw new ArgumentException("Unknown updater command: " + options.Command);
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
                return 125;
            }
        }

        private static int Launch(UpdateOptions options)
        {
            var currentVersion = UpdatePackageVerifier.ParseFourPartVersion(
                options.Required("current-version"),
                "current-version");
            var target = Verify(options, "target-");
            var rollback = Verify(options, "rollback-");
            UpdatePackageVerifier.ValidateUpgrade(target, rollback, currentVersion);

            var cacheRoot = Path.GetFullPath(options.ValueOrDefault(
                "cache-root",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TermLink",
                    "updates")));
            Directory.CreateDirectory(cacheRoot);
            var transactionName = "transaction-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" +
                Guid.NewGuid().ToString("N");
            var temporaryRoot = Path.Combine(cacheRoot, "." + transactionName + ".tmp");
            var transactionRoot = Path.Combine(cacheRoot, transactionName);
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                var targetRoot = Path.Combine(temporaryRoot, "target");
                var rollbackRoot = Path.Combine(temporaryRoot, "rollback");
                Directory.CreateDirectory(targetRoot);
                Directory.CreateDirectory(rollbackRoot);
                var stagedCertificate = Copy(target.CertificatePath, Path.Combine(temporaryRoot, "publisher.cer"));
                var stagedTarget = Stage(target, targetRoot);
                var stagedRollback = Stage(rollback, rollbackRoot);
                var runnerSource = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(runnerSource) || !File.Exists(runnerSource))
                {
                    throw new InvalidOperationException("The updater executable path is unavailable.");
                }
                Copy(runnerSource, Path.Combine(temporaryRoot, "TermLink.Updater.exe"));

                Directory.Move(temporaryRoot, transactionRoot);
                temporaryRoot = null;
                var journalPath = Path.Combine(transactionRoot, "transaction.json");
                var record = new UpdateTransactionRecord
                {
                    State = UpdateTransactionState.Prepared,
                    CurrentVersion = currentVersion.ToString(4),
                    TargetManifestPath = Path.Combine(transactionRoot, "target", Path.GetFileName(stagedTarget.ManifestPath)),
                    TargetSignaturePath = Path.Combine(transactionRoot, "target", Path.GetFileName(stagedTarget.SignaturePath)),
                    TargetPackagePath = Path.Combine(transactionRoot, "target", Path.GetFileName(stagedTarget.PackagePath)),
                    RollbackManifestPath = Path.Combine(transactionRoot, "rollback", Path.GetFileName(stagedRollback.ManifestPath)),
                    RollbackSignaturePath = Path.Combine(transactionRoot, "rollback", Path.GetFileName(stagedRollback.SignaturePath)),
                    RollbackPackagePath = Path.Combine(transactionRoot, "rollback", Path.GetFileName(stagedRollback.PackagePath)),
                    PublisherCertificatePath = Path.Combine(transactionRoot, Path.GetFileName(stagedCertificate)),
                    AllowUntrustedPublisher = options.HasSwitch("allow-untrusted-publisher")
                };
                UpdateJournal.Save(journalPath, record);

                // Revalidate the immutable inputs from their final staged paths before executing externally.
                var stagedTargetVerified = UpdatePackageVerifier.Verify(
                    record.TargetManifestPath,
                    record.TargetSignaturePath,
                    record.TargetPackagePath,
                    record.PublisherCertificatePath,
                    record.AllowUntrustedPublisher,
                    DateTimeOffset.UtcNow);
                var stagedRollbackVerified = UpdatePackageVerifier.Verify(
                    record.RollbackManifestPath,
                    record.RollbackSignaturePath,
                    record.RollbackPackagePath,
                    record.PublisherCertificatePath,
                    record.AllowUntrustedPublisher,
                    DateTimeOffset.UtcNow);
                UpdatePackageVerifier.ValidateUpgrade(stagedTargetVerified, stagedRollbackVerified, currentVersion);

                var runner = Path.Combine(transactionRoot, "TermLink.Updater.exe");
                var start = new ProcessStartInfo
                {
                    FileName = runner,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = transactionRoot
                };
                start.ArgumentList.Add("execute");
                start.ArgumentList.Add("--transaction");
                start.ArgumentList.Add(journalPath);
                var process = Process.Start(start);
                if (process == null) throw new InvalidOperationException("The external update runner could not start.");
                process.Dispose();
                Console.WriteLine(journalPath);
                return 0;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryRoot) && Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
        }

        private static VerifiedUpdatePackage Verify(UpdateOptions options, string prefix)
        {
            return UpdatePackageVerifier.Verify(
                options.Required(prefix + "manifest"),
                options.Required(prefix + "signature"),
                options.Required(prefix + "package"),
                options.ValueOrDefault(
                    "publisher-certificate",
                    Path.Combine(AppContext.BaseDirectory, "TermLink.UpdatePublisher.cer")),
                options.HasSwitch("allow-untrusted-publisher"),
                DateTimeOffset.UtcNow);
        }

        private static StagedPaths Stage(VerifiedUpdatePackage package, string root)
        {
            return new StagedPaths(
                Copy(package.ManifestPath, Path.Combine(root, "manifest.json")),
                Copy(package.SignaturePath, Path.Combine(root, "manifest.sig")),
                Copy(package.PackagePath, Path.Combine(root, Path.GetFileName(package.PackagePath))));
        }

        private static string Copy(string source, string destination)
        {
            File.Copy(Path.GetFullPath(source), destination, false);
            return Path.GetFullPath(destination);
        }

        private sealed class StagedPaths
        {
            public StagedPaths(string manifestPath, string signaturePath, string packagePath)
            {
                ManifestPath = manifestPath;
                SignaturePath = signaturePath;
                PackagePath = packagePath;
            }

            public string ManifestPath { get; }
            public string SignaturePath { get; }
            public string PackagePath { get; }
        }
    }

    internal sealed class UpdateOptions
    {
        private readonly Dictionary<string, string> values;
        private readonly HashSet<string> switches;

        private UpdateOptions(string command, Dictionary<string, string> values, HashSet<string> switches)
        {
            Command = command;
            this.values = values;
            this.switches = switches;
        }

        public string Command { get; }

        public static UpdateOptions Parse(string[] args)
        {
            if (args == null || args.Length == 0) throw new ArgumentException("An updater command is required.");
            var command = args[0];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var switches = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index++)
            {
                var token = args[index];
                if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2)
                {
                    throw new ArgumentException("Invalid updater option: " + token);
                }
                var name = token.Substring(2);
                if (string.Equals(name, "allow-untrusted-publisher", StringComparison.Ordinal))
                {
                    if (!switches.Add(name)) throw new ArgumentException("Duplicate updater option: " + token);
                    continue;
                }
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    throw new ArgumentException(token + " requires a value.");
                }
                if (!values.TryAdd(name, args[index])) throw new ArgumentException("Duplicate updater option: " + token);
            }
            return new UpdateOptions(command, values, switches);
        }

        public string Required(string name)
        {
            string value;
            if (!values.TryGetValue(name, out value)) throw new ArgumentException("--" + name + " is required.");
            return value;
        }

        public string ValueOrDefault(string name, string defaultValue)
        {
            string value;
            return values.TryGetValue(name, out value) ? value : defaultValue;
        }

        public bool HasSwitch(string name)
        {
            return switches.Contains(name);
        }
    }
}
