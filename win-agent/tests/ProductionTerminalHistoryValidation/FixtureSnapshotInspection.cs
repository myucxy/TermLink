using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Common;
using TermLink.LocalIpc;

namespace TermLink.ProductionTerminalHistory.Validation;

/// <summary>Reads one copied DPAPI fixture; never constructs a Store over the source directory.</summary>
internal static class FixtureSnapshotInspection
{
    private const string FixturePrefix = "termlink-snapshot-inspection-";
    private static readonly string[] Names =
    {
        "terminal-profile.dpapi", "checkpoint.dpapi", "outbox.dpapi", "previous-snapshot.dpapi"
    };

    private sealed class SnapshotChangedException : Exception { }
    private sealed class PrivateEntryException(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }
    private sealed record FileStamp(string Name, long Length, string Sha256);

    internal static int Run(string historyRoot, Guid pcDeviceId, Guid sessionId,
        TextWriter standardOutput = null, TextWriter standardError = null)
    {
        standardOutput ??= Console.Out;
        standardError ??= Console.Error;
        string fixtureRoot = null;
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string output = null;
        string failure = null;
        var exitCode = 0;
        try
        {
            if (!Path.IsPathFullyQualified(historyRoot) || pcDeviceId == Guid.Empty || sessionId == Guid.Empty)
                throw new InvalidDataException("Invalid inspection identity.");
            var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(historyRoot));
            var sourceSession = Path.Combine(sourceRoot, sessionId.ToString("N"));
            if (!Directory.Exists(sourceRoot) || !Directory.Exists(sourceSession) ||
                !string.Equals(Path.GetDirectoryName(sourceSession), sourceRoot, StringComparison.OrdinalIgnoreCase))
                throw new DirectoryNotFoundException("The requested private session is absent.");
            RequirePrivate(new DirectoryInfo(sourceRoot), requireProtected: true);
            RequirePrivate(new DirectoryInfo(sourceSession));
            var before = Capture(sourceSession);
            if (!before.ContainsKey("terminal-profile.dpapi") || !before.ContainsKey("outbox.dpapi"))
                throw new InvalidDataException("The requested session has no snapshot profile or outbox.");

            Directory.CreateDirectory(tempRoot);
            if ((new DirectoryInfo(tempRoot).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The process temp root is redirected.");
            var candidateRoot = Path.Combine(tempRoot, FixturePrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(candidateRoot);
            fixtureRoot = candidateRoot;
            ProtectFixtureRoot(new DirectoryInfo(fixtureRoot));
            var fixtureSession = Path.Combine(fixtureRoot, sessionId.ToString("N"));
            Directory.CreateDirectory(fixtureSession);
            try
            {
                foreach (var name in before.Keys)
                    File.Copy(Path.Combine(sourceSession, name), Path.Combine(fixtureSession, name), overwrite: false);
            }
            catch (IOException)
            {
                VerifySame(before, Capture(sourceSession));
                throw;
            }
            PrivateHistoryDirectory.Prepare(fixtureRoot, fixtureSession);
            VerifySame(before, Capture(sourceSession));
            VerifySame(before, Capture(fixtureSession));

            // Store construction may complete interrupted deletes; it only sees the isolated copy.
            var store = new ProductionTerminalHistoryStore(fixtureRoot, pcDeviceId);
            using (var pair = store.ReadDeltaSnapshotPair(sessionId, 0))
            {
                if (pair?.Current == null) throw new InvalidDataException("No Graphics snapshot is available.");
                output = JsonSerializer.Serialize(new
                {
                    current = Describe(pair.Current),
                    previous_exists = pair.Previous != null,
                    previous = pair.Previous == null ? null : Describe(pair.Previous)
                });
            }
            VerifySame(before, Capture(sourceSession));
        }
        catch (SnapshotChangedException)
        {
            failure = "snapshot_changed";
            exitCode = 3;
        }
        catch (PrivateEntryException error)
        {
            failure = "inspect_failed:" + error.Reason;
            exitCode = 1;
        }
        catch (DirectoryNotFoundException)
        {
            failure = "inspect_failed:source_session_absent";
            exitCode = 1;
        }
        catch (Exception error)
        {
            failure = "inspect_failed:" + error.GetType().Name;
            exitCode = 1;
        }
        finally
        {
            if (fixtureRoot != null)
            {
                try { RemoveOwnedFixture(fixtureRoot, sessionId, tempRoot); }
                catch
                {
                    failure = "cleanup_failed";
                    exitCode = 4;
                }
            }
        }
        if (failure != null) standardError.WriteLine(failure);
        else standardOutput.WriteLine(output);
        return exitCode;
    }

    internal static int SelfCheck()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.Combine(tempRoot, "termlink-snapshot-selfcheck-" + Guid.NewGuid().ToString("N"));
        var pcDeviceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var passed = false;
        try
        {
            Directory.CreateDirectory(root);
            ProtectFixtureRoot(new DirectoryInfo(root));
            var store = new ProductionTerminalHistoryStore(root, pcDeviceId,
                _ => TerminalSemanticProfile.Graphics);
            using (var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId,
                terminalCoreShadowEnabled: false, terminalCoreSnapshotAuthorityEnabled: true,
                terminalCoreShadowObserver: null))
            {
                var output = new StringBuilder();
                for (var row = 0; row < 300; row++) output.Append('X', 72).Append("\r\n");
                producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, sessionId, 1,
                    Encoding.ASCII.GetBytes(output.ToString())));
                if (!producer.ProcessSession(sessionId).SnapshotEligible)
                    throw new InvalidOperationException("The first self-check snapshot was ineligible.");
                producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.LocalInputObserved, sessionId, 2,
                    LocalPayloadCodec.LocalInputEpoch(7)));
                producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, sessionId, 3,
                    Encoding.ASCII.GetBytes("Z")));
                if (!producer.ProcessSession(sessionId).SnapshotEligible)
                    throw new InvalidOperationException("The second self-check snapshot was ineligible.");
            }
            var sessionDirectory = Path.Combine(root, sessionId.ToString("N"));
            PrivateHistoryDirectory.Prepare(root, sessionDirectory);
            // A session created after startup inherits the protected root instead of protecting each new entry.
            var inheritedSession = new DirectoryInfo(sessionDirectory);
            var sessionAcl = inheritedSession.GetAccessControl();
            sessionAcl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            inheritedSession.SetAccessControl(sessionAcl);
            foreach (var name in Names)
            {
                var file = new FileInfo(Path.Combine(sessionDirectory, name));
                var fileAcl = file.GetAccessControl();
                fileAcl.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                file.SetAccessControl(fileAcl);
            }
            if (inheritedSession.GetAccessControl().AreAccessRulesProtected ||
                Names.Any(name => new FileInfo(Path.Combine(sessionDirectory, name)).GetAccessControl().AreAccessRulesProtected))
                throw new InvalidOperationException("The inherited ACL fixture was not created.");
            var before = Capture(sessionDirectory);
            if (before.Count != Names.Length)
                throw new InvalidOperationException("The self-check source fixture is incomplete.");
            using var expected = store.ReadDeltaSnapshotPair(sessionId, 0);
            if (expected?.Current == null || expected.Previous == null)
                throw new InvalidOperationException("The self-check snapshot pair is incomplete.");
            using var capturedOutput = new StringWriter();
            using var capturedError = new StringWriter();
            if (Run(root, pcDeviceId, sessionId, capturedOutput, capturedError) != 0 ||
                capturedError.GetStringBuilder().Length != 0)
                throw new InvalidOperationException("The self-check inspection failed.");
            using var result = JsonDocument.Parse(capturedOutput.ToString());
            var value = result.RootElement;
            if (!value.GetProperty("previous_exists").GetBoolean() ||
                !Matches(value.GetProperty("current"), expected.Current) ||
                !Matches(value.GetProperty("previous"), expected.Previous))
                throw new InvalidOperationException("The self-check metadata differs from the source snapshots.");
            VerifySame(before, Capture(sessionDirectory));
            if (Run(Path.Combine(root, "missing"), pcDeviceId, sessionId,
                    new StringWriter(), new StringWriter()) == 0 ||
                Run(root, pcDeviceId, Guid.Empty, new StringWriter(), new StringWriter()) == 0)
                throw new InvalidOperationException("The self-check accepted an invalid identity.");
            void ExpectAclFailure(string source, Guid target, string reason)
            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                var code = Run(source, pcDeviceId, target, output, error);
                if (code != 1 ||
                    output.GetStringBuilder().Length != 0 ||
                    error.ToString().Trim() != "inspect_failed:" + reason)
                {
                    Console.Error.WriteLine($"snapshot_inspector_acl_selfcheck:expected={reason};exit={code};actual={error.ToString().Trim()}");
                    throw new InvalidOperationException("The self-check accepted an unsafe ACL or lost its safe reason.");
                }
            }
            var unprotectedRoot = Path.Combine(root, "unprotected-root");
            Directory.CreateDirectory(Path.Combine(unprotectedRoot, sessionId.ToString("N")));
            ExpectAclFailure(unprotectedRoot, sessionId, "private_root_inheritance_enabled");
            var wideRoot = Path.Combine(root, "wide-root");
            Directory.CreateDirectory(Path.Combine(wideRoot, sessionId.ToString("N")));
            ProtectFixtureRoot(new DirectoryInfo(wideRoot));
            AddForeignAllow(new DirectoryInfo(wideRoot));
            ExpectAclFailure(wideRoot, sessionId, "private_acl_foreign_allow");
            var unsafeSessionId = Guid.NewGuid();
            var unsafeSession = new DirectoryInfo(Path.Combine(root, unsafeSessionId.ToString("N")));
            unsafeSession.Create();
            AddForeignAllow(unsafeSession);
            ExpectAclFailure(root, unsafeSessionId, "private_acl_foreign_allow");
            var unsafeFile = new FileInfo(Path.Combine(sessionDirectory, Names[0]));
            var originalFileAcl = unsafeFile.GetAccessControl().GetSecurityDescriptorBinaryForm();
            var unsafeFileAcl = unsafeFile.GetAccessControl();
            unsafeFileAcl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            unsafeFile.SetAccessControl(unsafeFileAcl);
            try { ExpectAclFailure(root, sessionId, "private_acl_foreign_allow"); }
            finally
            {
                // An unchanged FileSecurity instance has no modified sections to persist.
                var restored = new FileSecurity();
                restored.SetSecurityDescriptorBinaryForm(originalFileAcl);
                unsafeFile.SetAccessControl(restored);
            }
            VerifySame(before, Capture(sessionDirectory));
            passed = true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("snapshot_inspector_selfcheck_error:" +
                (error is PrivateEntryException entry ? entry.Reason : error.GetType().Name));
            passed = false;
        }
        finally
        {
            try
            {
                if (!string.Equals(Path.GetDirectoryName(root), tempRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFileName(root).StartsWith("termlink-snapshot-selfcheck-", StringComparison.Ordinal) ||
                    (Directory.Exists(root) &&
                     ((new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0 ||
                      new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
                          .Any(entry => (entry.Attributes & FileAttributes.ReparsePoint) != 0))))
                    throw new InvalidOperationException("The self-check root is not owned.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch { passed = false; }
        }
        Console.WriteLine(passed ? "snapshot_inspector_selfcheck:pass" : "snapshot_inspector_selfcheck:fail");
        return passed ? 0 : 1;
    }

    private static bool Matches(JsonElement description, ProductionTerminalHistoryArtifact artifact) =>
        description.GetProperty("sequence").GetUInt64() == artifact.SnapshotSequence &&
        description.GetProperty("sha256").GetString() ==
            Convert.ToHexString(SHA256.HashData(artifact.Plaintext)).ToLowerInvariant() &&
        description.GetProperty("length").GetInt32() == artifact.Plaintext.Length;

    private static object Describe(ProductionTerminalHistoryArtifact artifact) => new
    {
        sequence = artifact.SnapshotSequence,
        sha256 = Convert.ToHexString(SHA256.HashData(artifact.Plaintext)).ToLowerInvariant(),
        length = artifact.Plaintext.Length
    };

    private static Dictionary<string, FileStamp> Capture(string directory)
    {
        var result = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                var info = new FileInfo(path);
                RequirePrivate(info);
                var length = info.Length;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                info.Refresh();
                if (!info.Exists || info.Length != length) throw new SnapshotChangedException();
                result.Add(name, new FileStamp(name, length, hash));
            }
            catch (IOException) { throw new SnapshotChangedException(); }
        }
        return result;
    }

    private static void VerifySame(Dictionary<string, FileStamp> expected,
        Dictionary<string, FileStamp> observed)
    {
        if (expected.Count != observed.Count || expected.Any(item =>
            !observed.TryGetValue(item.Key, out var current) || current != item.Value))
            throw new SnapshotChangedException();
    }

    private static void RequirePrivate(FileSystemInfo entry, bool requireProtected = false)
    {
        entry.Refresh();
        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget != null)
            throw new PrivateEntryException("private_entry_redirected");
        FileSystemSecurity security = entry switch
        {
            DirectoryInfo directory => directory.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner),
            FileInfo file => file.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner),
            _ => throw new InvalidDataException("Invalid private history entry.")
        };
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw new InvalidDataException("Windows identity is unavailable.");
        var allowed = new[] { current,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !allowed.Any(value => value.Equals(owner)))
            throw new PrivateEntryException("private_owner_untrusted");
        if (requireProtected && !security.AreAccessRulesProtected)
            throw new PrivateEntryException("private_root_inheritance_enabled");
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl == null ||
            security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                !allowed.Any(value => value.Equals(rule.IdentityReference))))
            throw new PrivateEntryException("private_acl_foreign_allow");
    }

    private static void AddForeignAllow(DirectoryInfo directory)
    {
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static void ProtectFixtureRoot(DirectoryInfo directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw new InvalidDataException("Windows identity is unavailable.");
        var security = directory.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var principal in new[] { current,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static void RemoveOwnedFixture(string fixtureRoot, Guid sessionId, string tempRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureRoot));
        if (!string.Equals(Path.GetDirectoryName(full), tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith(FixturePrefix, StringComparison.Ordinal) ||
            !Directory.Exists(full) || (new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to remove an unowned fixture directory.");
        var sessionDirectory = Path.Combine(full, sessionId.ToString("N"));
        if (!string.Equals(Path.GetDirectoryName(sessionDirectory), full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture session escaped its root.");
        if (Directory.Exists(sessionDirectory))
        {
            if ((new DirectoryInfo(sessionDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixture session was redirected.");
            foreach (var name in Names)
            {
                var path = Path.Combine(sessionDirectory, name);
                if (!File.Exists(path)) continue;
                if ((new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Fixture file was redirected.");
                File.Delete(path);
            }
            Directory.Delete(sessionDirectory, recursive: false);
        }
        var brokerDirectory = Path.Combine(full, "broker-captures");
        if (Directory.Exists(brokerDirectory))
        {
            if ((new DirectoryInfo(brokerDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixture broker directory was redirected.");
            Directory.Delete(brokerDirectory, recursive: false);
        }
        Directory.Delete(full, recursive: false);
    }
}
