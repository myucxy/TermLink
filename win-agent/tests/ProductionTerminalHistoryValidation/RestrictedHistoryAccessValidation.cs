using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using TermLink.Common;

namespace TermLink.ProductionTerminalHistory.Validation;

internal static class RestrictedHistoryAccessValidation
{
    private const int AccessDenied = 5;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x80;

    internal static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Windows user SID is unavailable.");
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (identity.Groups?.Cast<IdentityReference>().Any(group => group.Equals(users)) != true)
        {
            Console.WriteLine("SKIP restricted history access: current token has no BuiltinUsers group.");
            return;
        }

        var temp = Path.GetFullPath(Path.GetTempPath());
        var sandbox = Path.Combine(temp, "termlink-restricted-history-" + Guid.NewGuid().ToString("N"));
        var storage = Path.Combine(sandbox, "storage");
        var history = Path.Combine(storage, "history");
        var preserved = Path.Combine(history, "existing.bin");
        Require(IsChildOf(sandbox, temp), "Test directory escaped the process TEMP root.");
        Directory.CreateDirectory(sandbox);
        try
        {
            var directory = new DirectoryInfo(sandbox);
            var acl = directory.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            Directory.CreateDirectory(history);
            File.WriteAllBytes(preserved, [0, 1, 2, 0, 255]);
            var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(preserved)));

            if (!TryRestrictedToken(identity.AccessToken, user, administrators, out var restricted, out var reason))
            {
                Console.WriteLine("SKIP restricted history access: " + reason);
                return;
            }
            using (restricted)
            {
                int before;
                var enteredImpersonation = false;
                try
                {
                    before = WindowsIdentity.RunImpersonated(restricted,
                        () =>
                        {
                            enteredImpersonation = true;
                            return CreateAndWrite(Path.Combine(history, "before.bin"));
                        });
                }
                catch (Exception error) when (error is PlatformNotSupportedException or NotSupportedException or
                    SecurityException or Win32Exception)
                {
                    if (enteredImpersonation) throw;
                    Console.WriteLine("SKIP restricted history access: impersonation unavailable (" +
                        error.GetType().Name + ": " + error.Message + ").");
                    return;
                }
                Require(before == 0, "Restricted token could not write before ACL preparation; Win32 error " + before + ".");
                RequireOriginalIdentity(user);

                PrivateHistoryDirectory.Prepare(storage, history);
                var after = WindowsIdentity.RunImpersonated(restricted,
                    () => CreateAndWrite(Path.Combine(history, "after.bin")));
                Require(after == AccessDenied,
                    "Restricted token was not denied after ACL preparation; Win32 error " + after + ".");
                RequireOriginalIdentity(user);
            }
            File.WriteAllBytes(Path.Combine(history, "owner-after.bin"), [3]);
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(preserved))) == originalHash,
                "ACL preparation changed the existing history file.");
            Console.WriteLine("Restricted history access: real CreateFileW allowed before and denied after ACL preparation; owner write and history bytes preserved.");
        }
        finally
        {
            Require(IsChildOf(sandbox, temp) && Path.GetFileName(sandbox).StartsWith(
                "termlink-restricted-history-", StringComparison.Ordinal),
                "Refusing to delete a directory outside the test TEMP root.");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    private static bool TryRestrictedToken(SafeAccessTokenHandle original, SecurityIdentifier user,
        SecurityIdentifier administrators, out SafeAccessTokenHandle restricted, out string reason)
    {
        var userSid = IntPtr.Zero;
        var adminSid = IntPtr.Zero;
        try
        {
            userSid = Marshal.AllocHGlobal(user.BinaryLength);
            adminSid = Marshal.AllocHGlobal(administrators.BinaryLength);
            var userBytes = new byte[user.BinaryLength];
            var adminBytes = new byte[administrators.BinaryLength];
            user.GetBinaryForm(userBytes, 0);
            administrators.GetBinaryForm(adminBytes, 0);
            Marshal.Copy(userBytes, 0, userSid, userBytes.Length);
            Marshal.Copy(adminBytes, 0, adminSid, adminBytes.Length);
            var disabled = new[] { new SidAndAttributes(userSid), new SidAndAttributes(adminSid) };
            try
            {
                if (CreateRestrictedToken(original, 1, (uint)disabled.Length, disabled,
                    0, IntPtr.Zero, 0, IntPtr.Zero, out restricted))
                {
                    reason = "";
                    return true;
                }
                var error = Marshal.GetLastWin32Error();
                restricted?.Dispose();
                if (error is not (5 or 50 or 120 or 1314))
                    throw new Win32Exception(error, "CreateRestrictedToken failed unexpectedly.");
                reason = "CreateRestrictedToken failed with Win32 error " + error + ".";
            }
            catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException or
                PlatformNotSupportedException)
            {
                reason = "CreateRestrictedToken unavailable (" + error.GetType().Name + ").";
            }
            restricted = null!;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(adminSid);
            Marshal.FreeHGlobal(userSid);
        }
    }

    private static int CreateAndWrite(string path)
    {
        var handle = CreateFileW(path, GenericWrite, 0, IntPtr.Zero, CreateNew,
            FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return error == 0 ? -1 : error;
        }
        using var stream = new FileStream(handle, FileAccess.Write);
        stream.WriteByte(0x5A);
        return 0;
    }

    private static void RequireOriginalIdentity(SecurityIdentifier original)
    {
        using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);
        Require(impersonated == null, "The thread is still impersonating a Windows token.");
        using var restored = WindowsIdentity.GetCurrent();
        Require(restored.User?.Equals(original) == true,
            "Impersonation did not restore the original Windows identity.");
    }

    private static bool IsChildOf(string child, string parent)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributes
    {
        internal readonly IntPtr Sid;
        internal readonly uint Attributes;

        internal SidAndAttributes(IntPtr sid) => Sid = sid;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags,
        uint disableCount, [In] SidAndAttributes[] disabled, uint deletePrivilegeCount,
        IntPtr deletePrivileges, uint restrictCount, IntPtr restrictSids,
        out SafeAccessTokenHandle restricted);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
