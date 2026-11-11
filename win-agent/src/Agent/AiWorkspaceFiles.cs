// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TermLink.Agent;

// The Mac provider walks with O_NOFOLLOW and keeps operations relative to an
// opened directory. Enabling a capability requires the packaged native exports.
internal static class AiWorkspaceFiles
{
    internal static bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() && MacAvailable.Value;
    private static readonly Lazy<bool> MacAvailable = new(() => {
        if (!NativeLibrary.TryLoad("termlink-files", typeof(AiWorkspaceFiles).Assembly, null, out var library)) return false;
        try { return new[] { "termlink_files_open", "termlink_files_open_child", "termlink_files_private_directory", "termlink_files_write_child", "termlink_files_remove_child", "termlink_files_rename_child" }.All(name => NativeLibrary.TryGetExport(library, name, out _)); }
        finally { NativeLibrary.Free(library); }
    });
    internal static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsMacOS()) foreach (var alias in new[] { "/tmp", "/var", "/etc" })
            if (full == alias || full.StartsWith(alias + "/", StringComparison.Ordinal)) return "/private" + full;
        return full;
    }
    internal static void ValidatePath(string path)
    {
        if (!Available) throw new AiOperationException("AI_FILE_ACCESS_UNAVAILABLE");
        var full = Canonical(path);
        if (OperatingSystem.IsMacOS()) { using var opened = OpenHandle(full, false); return; }
        var root = Path.GetPathRoot(full); var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar)) {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AiOperationException("ATTACHMENT_PATH_UNSAFE");
        }
    }
    internal static FileStream Read(string path)
    {
        if (OperatingSystem.IsMacOS()) return new FileStream(OpenHandle(Canonical(path), true), FileAccess.Read);
        ValidatePath(path); return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    internal static FileStream Write(string path, bool create)
    {
        if (!OperatingSystem.IsMacOS()) { ValidatePath(path); return new FileStream(path, create ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        using var parent = OpenHandle(Canonical(Path.GetDirectoryName(path)), false, true);
        return new FileStream(Handle(Native.Write(parent.DangerousGetHandle().ToInt32(), Path.GetFileName(path), create ? 1 : 0)), FileAccess.ReadWrite);
    }
    internal static void CreateAttachmentDirectory(string workspace, string id)
    {
        if (!OperatingSystem.IsMacOS()) { var path = Path.Combine(workspace, ".termlink", "attachments", id); ValidatePath(path); Directory.CreateDirectory(path); return; }
        using var root = OpenHandle(Canonical(workspace), false, true);
        using var state = Handle(Native.Directory(root.DangerousGetHandle().ToInt32(), ".termlink"));
        using var attachments = Handle(Native.Directory(state.DangerousGetHandle().ToInt32(), "attachments"));
        using var session = Handle(Native.Directory(attachments.DangerousGetHandle().ToInt32(), id));
    }
    internal static void Delete(string path)
    {
        if (!OperatingSystem.IsMacOS()) { ValidatePath(path); File.Delete(path); return; }
        try {
            using var parent = OpenHandle(Canonical(Path.GetDirectoryName(path)), false, true);
            var result = Native.Delete(parent.DangerousGetHandle().ToInt32(), Path.GetFileName(path));
            if (result != -2) Require(result);
        } catch (AiOperationException error) when (error.Code == "ARTIFACT_NOT_FOUND") { }
    }
    internal static void Complete(string source, string destination)
    {
        if (!OperatingSystem.IsMacOS()) { ValidatePath(source); ValidatePath(destination); File.Move(source, destination); return; }
        if (Path.GetDirectoryName(source) != Path.GetDirectoryName(destination)) throw new AiOperationException("ATTACHMENT_PATH_UNSAFE");
        using var parent = OpenHandle(Canonical(Path.GetDirectoryName(source)), false, true);
        Require(Native.Rename(parent.DangerousGetHandle().ToInt32(), Path.GetFileName(source), Path.GetFileName(destination)));
    }
    internal static byte[] ReadBytes(string path)
    {
        using var file = Read(path);
        if (file.Length > 20 * 1024 * 1024) throw new AiOperationException("ATTACHMENT_LIMIT");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    private static SafeFileHandle OpenHandle(string path, bool regular, bool directory = false)
    {
        var fd = Native.Open(path, out var type, out _, out _); var handle = Handle(fd);
        if (regular && type != 2 || directory && type != 1) { handle.Dispose(); throw new AiOperationException("ATTACHMENT_PATH_UNSAFE"); }
        return handle;
    }
    private static SafeFileHandle Handle(int fd) { Require(fd); return new SafeFileHandle((IntPtr)fd, true); }
    private static void Require(int result) { if (result < 0) throw new AiOperationException(result == -2 ? "ARTIFACT_NOT_FOUND" : "ATTACHMENT_PATH_UNSAFE"); }
    private static class Native
    {
        private const string Library = "termlink-files";
        [DllImport(Library, EntryPoint="termlink_files_open")] internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out int type, out long size, out long modified);
        [DllImport(Library, EntryPoint="termlink_files_private_directory")] internal static extern int Directory(int parent,[MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, EntryPoint="termlink_files_write_child")] internal static extern int Write(int parent,[MarshalAs(UnmanagedType.LPUTF8Str)] string name,int create);
        [DllImport(Library, EntryPoint="termlink_files_remove_child")] internal static extern int Delete(int parent,[MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library, EntryPoint="termlink_files_rename_child")] internal static extern int Rename(int parent,[MarshalAs(UnmanagedType.LPUTF8Str)] string source,[MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    }
}
