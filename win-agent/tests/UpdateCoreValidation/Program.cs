using System;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using TermLink.UpdateCore;

namespace TermLink.UpdateCore.Validation
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            var updateId = Guid.NewGuid();
            var root = RemoteUpdateTransactionStore.CreateTransactionRoot(updateId);
            try
            {
                ValidateBuildIdentity();
                ValidateUpdateSecurity(root);
                if (args.Length > 0) ValidateSignedUpdate(root, args[0]);
                ValidateTransactionAndReceipt(updateId, root);
                ValidatePathBoundary(root);
                ValidateCliWorkspace(root);
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

        private static void ValidateUpdateSecurity(string root)
        {
            foreach (var status in new[] { WindowsUpdateSecurity.TrustSuccess,
                WindowsUpdateSecurity.CertificateUntrustedRoot, WindowsUpdateSecurity.CertificateChaining })
            {
                Assert(WindowsUpdateSecurity.IsAcceptableAuthenticodeStatus(status),
                    "pinned updates must not require system certificate trust");
                Assert(WindowsUpdateSecurity.IsAcceptableAuthenticodeStatus(status, strict: true) ==
                    (status == WindowsUpdateSecurity.TrustSuccess), "explicit strict policy must still require system trust");
            }
            foreach (var status in new[] { WindowsUpdateSecurity.TrustBadDigest,
                WindowsUpdateSecurity.TrustNoSignature, WindowsUpdateSecurity.CertRevoked,
                WindowsUpdateSecurity.CertRevocationFailed, 0x800B0101u, uint.MaxValue })
                Assert(!WindowsUpdateSecurity.IsAcceptableAuthenticodeStatus(status),
                    "signature failures must not be accepted as certificate trust failures");

            var signer = new string('a', 40);
            WindowsUpdateSecurity.ValidateSignerIdentity(signer.ToUpperInvariant(), signer, signer);
            AssertThrows<CryptographicException>(() => WindowsUpdateSecurity.ValidateSignerIdentity(
                new string('b', 40), signer, signer), "a different publisher was accepted");
            AssertThrows<CryptographicException>(() => WindowsUpdateSecurity.ValidateSignerIdentity(
                signer, signer, new string('b', 40)), "a mismatched server signer was accepted");
            AssertThrows<CryptographicException>(() => WindowsUpdateSecurity.ValidateSignerIdentity(
                signer, null, signer), "an update without a pinned publisher was accepted");

            var unsigned = Path.Combine(root, "unsigned-update.exe");
            File.WriteAllBytes(unsigned, new byte[] { 1, 2, 3 });
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(unsigned)));
            WindowsUpdateSecurity.ValidateIntegrity(unsigned, 3, hash);
            AssertThrows<CryptographicException>(() => WindowsUpdateSecurity.ValidateIntegrity(
                unsigned, 3, new string('0', 64)), "an update with the wrong hash was accepted");
            AssertThrows<InvalidDataException>(() => WindowsUpdateSecurity.ValidateIntegrity(
                unsigned, 4, hash), "a truncated update was accepted");

            var originalPolicy = Environment.GetEnvironmentVariable("TERMLINK_UPDATE_SIGNATURE_POLICY");
            var originalSigner = Environment.GetEnvironmentVariable("TERMLINK_UPDATE_SIGNER_THUMBPRINT");
            try
            {
                Environment.SetEnvironmentVariable("TERMLINK_UPDATE_SIGNER_THUMBPRINT", signer);
                foreach (var policy in new[] { null, "auto", "compat", "strict" })
                {
                    Environment.SetEnvironmentVariable("TERMLINK_UPDATE_SIGNATURE_POLICY", policy);
                    try { WindowsUpdateSecurity.ValidateAuthenticode(unsigned, signer, signer, root); }
                    catch (CryptographicException error)
                    {
                        Assert(error.Message.Contains("policy=" + (policy == "strict" ? "strict" : "compat")),
                            "default policy unexpectedly requires system trust for an explicitly pinned publisher");
                        continue;
                    }
                    throw new InvalidOperationException("an unsigned update was accepted");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("TERMLINK_UPDATE_SIGNATURE_POLICY", originalPolicy);
                Environment.SetEnvironmentVariable("TERMLINK_UPDATE_SIGNER_THUMBPRINT", originalSigner);
            }
        }

        private static void ValidateSignedUpdate(string root, string fixture)
        {
            var signer = WindowsUpdateSecurity.ReadSignerThumbprint(fixture);
            var originalPolicy = Environment.GetEnvironmentVariable("TERMLINK_UPDATE_SIGNATURE_POLICY");
            try
            {
                Environment.SetEnvironmentVariable("TERMLINK_UPDATE_SIGNATURE_POLICY", null);
                Assert(WindowsUpdateSecurity.ValidateAuthenticode(fixture, signer, signer, root) == signer,
                    "the signed update fixture was rejected");
                AssertThrows<CryptographicException>(() => WindowsUpdateSecurity.ValidateAuthenticode(
                    fixture, new string('0', 40), signer, root), "a signed update with the wrong publisher was accepted");
                AssertThrows<CryptographicException>(() => WindowsUpdateSecurity.ValidateAuthenticode(
                    fixture, signer, new string('0', 40), root), "a signed update with the wrong server signer was accepted");

                var tampered = Path.Combine(root, "tampered-update.exe");
                File.Copy(fixture, tampered);
                using (var stream = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
                    var section = reader.PEHeaders.SectionHeaders[0];
                    Assert(section.SizeOfRawData > 0, "signed fixture has no section data to tamper");
                    stream.Position = section.PointerToRawData;
                    var original = stream.ReadByte();
                    stream.Position--;
                    stream.WriteByte((byte)(original ^ 1));
                }
                Assert(WindowsUpdateSecurity.ReadSignerThumbprint(tampered) == signer,
                    "tampering must preserve the certificate to test signature verification");
                AssertThrows<CryptographicException>(() => WindowsUpdateSecurity.ValidateAuthenticode(
                    tampered, signer, signer, root), "a tampered update with the correct certificate was accepted");
                Console.WriteLine("Signed update and tampered-signature validation passed.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TERMLINK_UPDATE_SIGNATURE_POLICY", originalPolicy);
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

        private static void ValidateCliWorkspace(string root)
        {
            var install = Path.Combine(root, "install");
            var workspace = AiWorkspaceStorage.Under(install);
            var leaf = "cli_20261004_120000_123_" + Guid.NewGuid().ToString("N");
            var target = Path.Combine(workspace, leaf);
            AiWorkspaceStorage.EnsureCliDirectory(install, target);
            Assert(Directory.Exists(target), "managed CLI directory must be created");
            File.WriteAllText(Path.Combine(target, "keep.txt"), "retain on retry");
            AiWorkspaceStorage.EnsureCliDirectory(install, target);
            Assert(File.ReadAllText(Path.Combine(target, "keep.txt")) == "retain on retry", "retry must preserve CLI contents");
            foreach (var rejected in new[] { Path.Combine(root, leaf), Path.Combine(workspace, "arbitrary"), Path.Combine(workspace, "nested", leaf) })
            {
                AiWorkspaceStorage.EnsureCliDirectory(install, rejected);
                Assert(!Directory.Exists(rejected), "remote launch cannot create outside the reserved namespace");
            }
            var moved = Path.Combine(root, "new-install"); Directory.CreateDirectory(moved);
            AiWorkspaceStorage.MoveBetweenInstallRoots(install, moved);
            Assert(File.Exists(Path.Combine(AiWorkspaceStorage.Under(moved), leaf, "keep.txt")), "upgrade must preserve CLI workspaces with AI workspaces");
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
