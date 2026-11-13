// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TermLink.Agent.AI;

/// <summary>Creates private runtime directories without following directory links.</summary>
internal static class PortableAiRuntimeDirectory
{
    private const int DirectoryFlags = 0x100000 | 0x100 | 0x1000000;
    private const uint PrivateMode = 0x1c0;

    internal static string Ensure(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0')) throw new IOException("AI_DIRECTORY_INVALID");
        path = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS())
        {
            var cursor = Path.GetPathRoot(path);
            foreach (var part in path[cursor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, part);
                if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("AI_DIRECTORY_LINK_REJECTED");
            }
            if (!Directory.Exists(path)) {
                if (OperatingSystem.IsLinux()) Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else Directory.CreateDirectory(path);
            }
            if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    throw new IOException("AI_DIRECTORY_NOT_PRIVATE");
            }
            return path;
        }
        var physical = path;
        foreach (var alias in new[] { "/tmp", "/var", "/etc" })
            if (physical == alias || physical.StartsWith(alias + "/", StringComparison.Ordinal)) { physical = "/private" + physical; break; }
        var parts = physical.Split('/', StringSplitOptions.RemoveEmptyEntries);
        using var root = Open("/", DirectoryFlags);
        if (root.IsInvalid) throw new IOException("AI_DIRECTORY_INVALID");
        SafeFileHandle parent = root;
        try
        {
            for (var index = 0; index < parts.Length; index++)
            {
                var child = OpenAt(parent, parts[index], DirectoryFlags);
                if (child.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError(); child.Dispose();
                    if (error != 2) throw new IOException("AI_DIRECTORY_LINK_REJECTED");
                    if (MkdirAt(parent, parts[index], PrivateMode) != 0 && Marshal.GetLastPInvokeError() != 17)
                        throw new IOException("AI_DIRECTORY_LINK_REJECTED");
                    child = OpenAt(parent, parts[index], DirectoryFlags);
                }
                if (child.IsInvalid) { child.Dispose(); throw new IOException("AI_DIRECTORY_LINK_REJECTED"); }
                if (parent != root) parent.Dispose();
                parent = child;
            }
            if (Stat(parent, out var info) != 0 || info.Owner != GetUser() || (info.Mode & 0x3f) != 0 || !NoAcl(parent))
                throw new IOException("AI_DIRECTORY_NOT_PRIVATE");
            return path;
        }
        finally { if (parent != root) parent.Dispose(); }
    }

    private static bool NoAcl(SafeFileHandle handle)
    {
        var attrs = new Attributes { BitmapCount = 5, Common = 0x00400000 }; // ATTR_CMN_EXTENDED_SECURITY
        var data = new byte[8210];
        if (GetAttributes(handle, ref attrs, data, (nuint)data.Length, 0) != 0) return false;
        var length = BitConverter.ToUInt32(data, 0);
        var offset = 4L + BitConverter.ToInt32(data, 4);
        var bytes = BitConverter.ToUInt32(data, 8);
        if (length < 12 || length > data.Length) return false;
        if (bytes == 0) return true;
        // kauth_filesec has magic, two GUIDs, then ACL count/flags.
        if (offset < 12 || bytes < 44 || offset + bytes > length) return false;
        var count = BitConverter.ToUInt32(data, (int)offset + 36);
        return BitConverter.ToUInt32(data, (int)offset) == 0x012cc16d && count is 0 or uint.MaxValue;
    }
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct FileInfo { [FieldOffset(4)] internal ushort Mode; [FieldOffset(16)] internal uint Owner; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes { internal ushort BitmapCount, Reserved; internal uint Common, Volume, Directory, File, Fork; }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true)] private static extern SafeFileHandle Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "openat", SetLastError = true)] private static extern SafeFileHandle OpenAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkdirAt(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat")] private static extern int Stat(SafeFileHandle handle, out FileInfo info);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")] private static extern uint GetUser();
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fgetattrlist")] private static extern int GetAttributes(SafeFileHandle handle, ref Attributes attrs, [Out] byte[] data, nuint length, uint flags);
}
