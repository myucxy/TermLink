using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using TermLink.Common;

namespace TermLink.ProductionTerminalHistory.Validation
{
    internal static class PrivateHistoryDirectoryValidation
    {
        private static readonly FileSystemRights Mutation = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        internal static void Run()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var currentSid = identity.User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var temp = Path.GetFullPath(Path.GetTempPath());
            var sandbox = Path.Combine(temp, "termlink-private-history-" + Guid.NewGuid().ToString("N"));
            var storage = Path.Combine(sandbox, "storage");
            var historyParent = Path.Combine(storage, "Data", "History");
            var history = Path.Combine(historyParent, "machine");
            var legacy = Path.Combine(storage, "production-history", "legacy-pc");
            var outside = Path.Combine(sandbox, "outside");
            var link = Path.Combine(storage, "linked-history");
            var hardLink = Path.Combine(history, "linked-capture.bin");
            Require(IsChildOf(sandbox, temp), "Test directory escaped the process TEMP root.");
            Directory.CreateDirectory(sandbox);
            try
            {
                AddInheritedUsersWrite(sandbox);
                Directory.CreateDirectory(legacy);
                Directory.CreateDirectory(outside);
                var session = Path.Combine(legacy, "session");
                var captures = Path.Combine(legacy, "broker-captures", "capture-session");
                var currentOwned = Path.Combine(legacy, "owned-by-user");
                var adminOwned = Path.Combine(legacy, "owned-by-admins");
                Directory.CreateDirectory(session);
                Directory.CreateDirectory(captures);
                Directory.CreateDirectory(currentOwned);
                Directory.CreateDirectory(adminOwned);
                SetOwner(currentOwned, currentSid);
                SetOwner(adminOwned, administratorsSid);
                var wal = Path.Combine(session, "history.wal.dpapi");
                var capture = Path.Combine(captures, "image.capture");
                File.WriteAllBytes(wal, [0, 1, 2, 0, 255]);
                File.WriteAllBytes(capture, [9, 8, 7, 6]);
                var walHash = Hash(wal);
                var captureHash = Hash(capture);
                Require(HasForeignMutation(legacy), "Wide inherited ACL fixture was not created.");

                PrivateHistoryDirectory.Prepare(storage, historyParent);
                Directory.Move(legacy, history);
                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(OwnerOf(Path.Combine(history, "owned-by-user")).Equals(currentSid) &&
                    OwnerOf(Path.Combine(history, "owned-by-admins")).Equals(administratorsSid),
                    "Preparation changed a trusted Windows owner.");
                Require(Hash(Path.Combine(history, "session", "history.wal.dpapi")) == walHash &&
                    Hash(Path.Combine(history, "broker-captures", "capture-session", "image.capture")) == captureHash,
                    "Preparing migrated history changed existing bytes.");
                Require(HasForeignMutation(sandbox), "Preparing storage changed ACL outside storageRoot.");

                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(Hash(Path.Combine(history, "session", "history.wal.dpapi")) == walHash &&
                    Hash(Path.Combine(history, "broker-captures", "capture-session", "image.capture")) == captureHash,
                    "Repeated preparation changed existing bytes.");

                var longDirectory = Path.Combine(history, "broker-captures",
                    new string('s', 96), new string('f', 48));
                var longCapture = Path.Combine(longDirectory, "capture.bin");
                Require(longCapture.Length > 260, "Long-path fixture did not exceed the Win32 legacy path limit.");
                Directory.CreateDirectory(longDirectory);
                File.WriteAllBytes(longCapture, [7, 0, 255, 8, 6]);
                var longHash = Hash(longCapture);
                Require(File.Exists(longCapture), "Long-path capture fixture was not created.");
                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(Hash(longCapture) == longHash, "Securing the long-path capture changed its bytes.");
                PrivateHistoryDirectory.Prepare(storage, history);
                ValidatePrivateTree(storage, history);
                Require(Hash(longCapture) == longHash, "Repeated long-path preparation changed its bytes.");

                var outsideMarker = Path.Combine(outside, "unchanged.bin");
                File.WriteAllBytes(outsideMarker, [4, 3, 2, 1]);
                var outsideHash = Hash(outsideMarker);
                ExpectRejected(() => PrivateHistoryDirectory.Prepare(storage, outside),
                    "History path outside storageRoot was accepted.");
                Require(Hash(outsideMarker) == outsideHash && HasForeignMutation(outside),
                    "Rejected outside path changed data or ACL beyond storageRoot.");
                try { Directory.CreateSymbolicLink(link, outside); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    throw new InvalidOperationException("The Windows test environment cannot create the required directory symlink fixture.", error);
                }
                ExpectRejected(() => PrivateHistoryDirectory.Prepare(storage, link),
                    "Reparse-point history path was accepted.");
                Require(Hash(outsideMarker) == outsideHash,
                    "Rejected reparse-point path changed its target.");
                Require(CreateHardLink(hardLink, outsideMarker, IntPtr.Zero),
                    "The Windows test environment cannot create the required hard-link fixture (" +
                    Marshal.GetLastWin32Error() + ").");
                ExpectRejected(() => PrivateHistoryDirectory.Prepare(storage, history),
                    "Hard-linked history file was accepted.");
                Require(Hash(outsideMarker) == outsideHash,
                    "Rejected hard link changed its target.");
                Console.WriteLine("Private history directory: inherited ACL, migration, long-path idempotence, path, reparse and hard-link guards passed.");
            }
            finally
            {
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(link);
                if (File.Exists(hardLink)) File.Delete(hardLink);
                Require(IsChildOf(sandbox, temp) && Path.GetFileName(sandbox).StartsWith(
                    "termlink-private-history-", StringComparison.Ordinal),
                    "Refusing to delete a directory outside the test TEMP root.");
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
            }
        }

        internal static void AddInheritedUsersWrite(string path)
        {
            var security = new DirectoryInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }

        private static void SetOwner(string path, SecurityIdentifier owner)
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl();
            security.SetOwner(owner);
            directory.SetAccessControl(security);
        }

        private static SecurityIdentifier OwnerOf(string path) =>
            new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ??
            throw new InvalidOperationException("Windows owner SID is unavailable.");

        internal static void ValidatePrivateTree(string storage, string history)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var current = identity.User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var allowed = new[] { current, system, administrators };
            var path = history;
            while (true)
            {
                CheckSecurity(new DirectoryInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner), allowed, path);
                if (string.Equals(path, storage, StringComparison.OrdinalIgnoreCase)) break;
                path = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("History path escaped storageRoot.");
            }
            foreach (var directory in Directory.EnumerateDirectories(history, "*", SearchOption.AllDirectories))
                CheckSecurity(new DirectoryInfo(directory).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner), allowed, directory);
            foreach (var file in Directory.EnumerateFiles(history, "*", SearchOption.AllDirectories))
                CheckSecurity(new FileInfo(file).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner), allowed, file);
        }

        private static void CheckSecurity(FileSystemSecurity security, SecurityIdentifier[] allowed, string path)
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            Require(owner != null && allowed.Any(value => value.Equals(owner)), "Untrusted owner: " + path);
            Require(security.AreAccessRulesProtected, "ACL inheritance remains enabled: " + path);
            Require(!security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule =>
                    rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & Mutation) != 0 &&
                    !allowed.Any(value => value.Equals(rule.IdentityReference))),
                "Another identity retains mutation rights: " + path);
        }

        private static bool HasForeignMutation(string path)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var current = identity.User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            return !current.Equals(users) && new DirectoryInfo(path).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule =>
                    rule.AccessControlType == AccessControlType.Allow &&
                    users.Equals(rule.IdentityReference) && (rule.FileSystemRights & Mutation) != 0);
        }

        private static void ExpectRejected(Action action, string message)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return; }
            throw new InvalidOperationException(message);
        }

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        private static bool IsChildOf(string child, string parent)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
            return relative != "." && !Path.IsPathRooted(relative) &&
                relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
    }
}
