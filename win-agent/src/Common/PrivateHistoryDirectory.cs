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
        var (boundary, segments, current) = ValidateAndProtectRoot(storageRoot, historyDirectory);
        var held = new List<SafeFileHandle>();
        try
        {
            // 完整 Prepare 保护全链（含目标段）并递归扫描目标内容；
            // 仅 PreparePaths 排除最后一段供迁移前使用。递归必须使用循环
            // 结束后的规范化最终路径：原始输入可能是正斜杠等形式，拼接
            // \\?\ 前缀后无法打开。
            var path = boundary;
            held.Add(ProtectDirectory(path, current));
            foreach (var segment in segments)
            {
                path = Path.Combine(path, segment);
                held.Add(ProtectDirectory(path, current));
            }

            ProtectContents(path, current, 0);
        }
        finally
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
        }
    }

    /// <summary>
    /// 只创建并保护从 storageRoot 到目标历史目录父段的祖先链，不创建、
    /// 不保护、不递归扫描最后一段（目标历史目录本身）。用于运行初始化的
    /// 迁移前阶段：目标目录必须保持不存在，让 legacy 迁移判定正常工作，
    /// 同时避免初始化显式递归扫描兄弟机器的历史。
    /// <paramref name="historyDirectory"/> 必须是最终 historyRoot（如
    /// storage/Data/History/machine）：本方法自行排除末段，调用方传父目录
    /// 会漏检倒数第二段的重解析点，让迁移把历史移出私有根目录。
    /// </summary>
    internal static void PreparePaths(string storageRoot, string historyDirectory)
    {
        var (boundary, segments, current) = ValidateAndProtectRoot(storageRoot, historyDirectory);
        var held = new List<SafeFileHandle>();
        try
        {
            ProtectAncestorChain(boundary, segments, current, held);
        }
        finally
        {
            for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
        }
    }

    private static (string Boundary, string[] Segments, SecurityIdentifier Current)
        ValidateAndProtectRoot(string storageRoot, string historyDirectory)
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
        return (boundary,
            Path.GetRelativePath(boundary, history).Split(Path.DirectorySeparatorChar),
            current);
    }

    private static string ProtectAncestorChain(
        string boundary, string[] segments, SecurityIdentifier current,
        List<SafeFileHandle> held)
    {
        // 打开并保护除最后一段外的全部路径段；最后一段（目标历史目录）
        // 由完整 Prepare 创建、保护并递归扫描内容。
        var path = boundary;
        held.Add(ProtectDirectory(path, current));
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            path = Path.Combine(path, segments[index]);
            held.Add(ProtectDirectory(path, current));
        }

        return path;
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
                out var owner, out _, out var dacl, out _, out var descriptor);
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
                var expectedAcl = security.DiscretionaryAcl;
                var expectedBytes = new byte[expectedAcl.BinaryLength];
                expectedAcl.GetBinaryForm(expectedBytes, 0);
                if (HasProtectedDacl(descriptor, expectedBytes)) return handle;
                result = SetSecurityInfo(handle, 1, DaclInformation | ProtectedDaclInformation,
                    IntPtr.Zero, IntPtr.Zero, expectedBytes, IntPtr.Zero);
                if (result != 0) throw new Win32Exception((int)result, "Private history permissions cannot be protected.");
            }
            finally { LocalFree(descriptor); }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static bool HasProtectedDacl(IntPtr descriptor, byte[] expectedDaclBytes)
    {
        // SetSecurityInfo with a protected DACL propagates the descriptor to
        // the whole subtree and is the dominant startup cost on hosts with
        // large histories. Comparing the stored DACL first keeps the daily
        // pass read-only (resumable across watchdog restarts) and only pays
        // the write on entries that are genuinely unprotected or stale.
        var length = (int)GetSecurityDescriptorLength(descriptor);
        var bytes = new byte[length];
        Marshal.Copy(descriptor, bytes, 0, length);
        var security = new RawSecurityDescriptor(bytes, 0);
        if ((security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            return false;
        var stored = security.DiscretionaryAcl;
        // A NULL DACL (no DACL at all) and an empty DACL are both unprotected:
        // neither matches the expected descriptor, and DiscretionaryAcl is
        // null precisely for the NULL form that used to crash this compare.
        if (stored == null || stored.BinaryLength != expectedDaclBytes.Length)
            return false;
        var storedBytes = new byte[stored.BinaryLength];
        stored.GetBinaryForm(storedBytes, 0);
        return storedBytes.AsSpan().SequenceEqual(expectedDaclBytes);
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
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
