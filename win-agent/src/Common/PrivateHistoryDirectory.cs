using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TermLink.Common;

internal static class PrivateHistoryDirectory
{
    private const uint OwnerInformation = 1;
    private const uint DaclInformation = 4;
    private const uint ProtectedDaclInformation = 0x80000000;
    private const uint ReadControl = 0x20000;
    private const uint WriteDacl = 0x40000;
    private const uint ReadAttributes = 0x80;
    private const uint ShareReadWrite = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;

    internal static void Prepare(string storageRoot, string historyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyDirectory);
        if (!Path.IsPathFullyQualified(storageRoot) || !Path.IsPathFullyQualified(historyDirectory))
            throw new InvalidDataException("Private history paths must be absolute.");
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));
        var history = Path.TrimEndingDirectorySeparator(Path.GetFullPath(historyDirectory));
        if (boundary.Length < 4 || boundary[1] != ':' || boundary[2] != '\\' ||
            !history.StartsWith(boundary + '\\', StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("History must be inside the TermLink storage directory.");
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw new InvalidDataException("Current Windows identity is unavailable.");
        var held = new List<SafeFileHandle>();
        try
        {
            var path = boundary;
            held.Add(ProtectDirectory(path, current));
            foreach (var segment in Path.GetRelativePath(boundary, history).Split(Path.DirectorySeparatorChar))
            {
                path = Path.Combine(path, segment);
                held.Add(ProtectDirectory(path, current));
            }
            ProtectContents(history, current, 0);
        }
        finally
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
        }
    }

    private static SafeFileHandle ProtectDirectory(string path, SecurityIdentifier current)
    {
        Directory.CreateDirectory(path);
        var handle = OpenAndProtect(path, current, out var attributes);
        if ((attributes & FileAttributes.Directory) != 0) return handle;
        handle.Dispose();
        throw new InvalidDataException("Private history ancestor is not a directory.");
    }

    private static void ProtectContents(string directory, SecurityIdentifier current, int depth)
    {
        if (depth >= 32) throw new InvalidDataException("Private history directory depth exceeds its limit.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            using var handle = OpenAndProtect(path, current, out var attributes);
            if ((attributes & FileAttributes.Directory) != 0) ProtectContents(path, current, depth + 1);
        }
    }

    private static SafeFileHandle OpenAndProtect(string path, SecurityIdentifier current, out FileAttributes attributes)
    {
        // Retain the object without DELETE sharing while inspecting and changing its DACL.
        var handle = CreateFile(@"\\?\" + path, ReadControl | WriteDacl | ReadAttributes, ShareReadWrite,
            IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Private history entry cannot be secured.");
        }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = (FileAttributes)info.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0 && info.Links != 1)
                throw new InvalidDataException("Private history entry is redirected or hard linked.");
            var result = GetSecurityInfo(handle, 1, OwnerInformation | DaclInformation,
                out var owner, out _, out _, out _, out var descriptor);
            if (result != 0) throw new Win32Exception((int)result);
            try
            {
                var ownerSid = new SecurityIdentifier(owner);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                if (!current.Equals(ownerSid) && !system.Equals(ownerSid) && !administrators.Equals(ownerSid))
                    throw new InvalidDataException("Private history entry has an untrusted owner.");
                var inheritance = (attributes & FileAttributes.Directory) != 0 ? "OICI" : "";
                var security = new RawSecurityDescriptor($"D:P(A;{inheritance};FA;;;{current.Value})(A;{inheritance};FA;;;SY)(A;{inheritance};FA;;;BA)");
                var acl = security.DiscretionaryAcl;
                var bytes = new byte[acl.BinaryLength];
                acl.GetBinaryForm(bytes, 0);
                result = SetSecurityInfo(handle, 1, DaclInformation | ProtectedDaclInformation,
                    IntPtr.Zero, IntPtr.Zero, bytes, IntPtr.Zero);
                if (result != 0) throw new Win32Exception((int)result, "Private history permissions cannot be protected.");
            }
            finally { LocalFree(descriptor); }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { internal uint Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        internal uint Attributes;
        internal FileTime Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo information);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, uint type, uint information,
        IntPtr owner, IntPtr group, byte[] dacl, IntPtr sacl);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
