using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TermLink.SessionHost;

internal sealed class KittyFileCaptureLimitException : IOException
{
    internal KittyFileCaptureLimitException() : base("Kitty file capture exceeds the per-file limit.") { }
}

// The trusted Host owns root authorization and passes only a session-owned directory handle.
internal sealed class KittyFileBroker : IDisposable
{
    internal const int MaxCaptureBytes = 16 * 1024 * 1024;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileTypeDisk = 1;
    private const uint FileReadData = 1;
    private const uint FileReadAttributes = 0x80;
    private const uint Delete = 0x10000;
    private const uint Synchronize = 0x100000;
    private const uint FileShareRead = 1;
    private const uint FileOpen = 1;
    private const uint FileNonDirectoryFile = 0x40;
    private const uint FileSynchronousIoNonalert = 0x20;
    private const uint FileOpenReparsePoint = 0x200000;
    private const uint ObjCaseInsensitive = 0x40;
    private const uint ObjDontReparse = 0x1000;
    private const uint DuplicateSameAccess = 2;

    private readonly SafeFileHandle root;
    private readonly uint volume;

    internal KittyFileBroker(SafeFileHandle sessionOwnedRoot)
    {
        ArgumentNullException.ThrowIfNull(sessionOwnedRoot);
        if (sessionOwnedRoot.IsInvalid || sessionOwnedRoot.IsClosed)
            throw new ArgumentException("An open session-owned directory handle is required.", nameof(sessionOwnedRoot));
        var added = false;
        try
        {
            sessionOwnedRoot.DangerousAddRef(ref added);
            var process = GetCurrentProcess();
            if (!DuplicateHandle(process, sessionOwnedRoot.DangerousGetHandle(), process,
                    out var duplicate, 0, false, DuplicateSameAccess))
                throw new IOException("The session root cannot be duplicated.");
            root = duplicate;
        }
        finally { if (added) sessionOwnedRoot.DangerousRelease(); }

        try
        {
            var info = Inspect(root);
            if ((info.Attributes & FileAttributeDirectory) == 0 ||
                (info.Attributes & FileAttributeReparsePoint) != 0)
                throw new ArgumentException("The session root must be a normal directory handle.", nameof(sessionOwnedRoot));
            volume = info.VolumeSerial;
        }
        catch { root.Dispose(); throw; }
    }

    internal CapturedFile Capture(string relativePath, ulong offset, ulong? length, bool temporary)
    {
        ValidateRelativePath(relativePath);
        if (root.IsClosed) throw new ObjectDisposedException(nameof(KittyFileBroker));
        var pathBytes = checked(relativePath.Length * sizeof(char));
        var nameBuffer = Marshal.StringToHGlobalUni(relativePath);
        try
        {
            var name = new UnicodeString
            {
                Length = checked((ushort)pathBytes),
                MaximumLength = checked((ushort)(pathBytes + sizeof(char))),
                Buffer = nameBuffer
            };
            var namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(name, namePointer, false);
                var added = false;
                try
                {
                    root.DangerousAddRef(ref added);
                    var attributes = new ObjectAttributes
                    {
                        Length = Marshal.SizeOf<ObjectAttributes>(),
                        RootDirectory = root.DangerousGetHandle(),
                        ObjectName = namePointer,
                        Attributes = ObjCaseInsensitive | ObjDontReparse
                    };
                    var status = NtCreateFile(out var file,
                        FileReadData | FileReadAttributes | Synchronize | (temporary ? Delete : 0),
                        ref attributes, out _, IntPtr.Zero, 0, FileShareRead, FileOpen,
                        FileNonDirectoryFile | FileSynchronousIoNonalert | FileOpenReparsePoint,
                        IntPtr.Zero, 0);
                    if (status < 0)
                    {
                        file?.Dispose();
                        throw new IOException("The requested file is unavailable or outside the session root.");
                    }
                    if (file == null || file.IsInvalid)
                    {
                        file?.Dispose();
                        throw new IOException("The requested file could not be opened.");
                    }
                    using (file)
                    {
                        var before = Inspect(file);
                        if ((before.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
                            before.Links != 1 || before.VolumeSerial != volume)
                            throw new IOException("The requested file is not an authorized regular file.");

                        var size = ((ulong)before.SizeHigh << 32) | before.SizeLow;
                        if (offset > size || offset > long.MaxValue ||
                            (length.HasValue && length.Value > size - offset))
                            throw new IOException("The requested file range is unavailable.");
                        var count = length ?? size - offset;
                        if (count > MaxCaptureBytes) throw new KittyFileCaptureLimitException();
                        var bytes = new byte[(int)count];
                        var read = 0;
                        while (read < bytes.Length)
                        {
                            var next = RandomAccess.Read(file, bytes.AsSpan(read), checked((long)(offset + (ulong)read)));
                            if (next == 0) throw new IOException("The requested file changed during capture.");
                            read += next;
                        }
                        if (!SameFileState(before, Inspect(file)))
                            throw new IOException("The requested file changed during capture.");
                        // The capture holds the same handle until the Host reports durable application.
                        return new CapturedFile(bytes, temporary ? Duplicate(file) : null, before);
                    }
                }
                finally { if (added) root.DangerousRelease(); }
            }
            finally { Marshal.FreeHGlobal(namePointer); }
        }
        finally { Marshal.FreeHGlobal(nameBuffer); }
    }

    internal static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 16000 ||
            path[0] == '\\' || path.Contains('/') || path.Contains(':') ||
            path.IndexOfAny(new[] { '*', '?', '<', '>', '|', '"' }) >= 0)
            throw new ArgumentException("A plain relative file path is required.", nameof(path));
        foreach (var character in path)
            if (char.IsControl(character))
                throw new ArgumentException("A plain relative file path is required.", nameof(path));
        foreach (var part in path.Split('\\'))
        {
            if (part.Length == 0 || part == "." || part == ".." ||
                part.EndsWith(' ') || part.EndsWith('.'))
                throw new ArgumentException("A plain relative file path is required.", nameof(path));
        }
    }

    private static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        var process = GetCurrentProcess();
        if (!DuplicateHandle(process, source.DangerousGetHandle(), process,
                out var copy, 0, false, DuplicateSameAccess))
            throw new IOException("The captured file handle cannot be retained.");
        return copy;
    }

    private static FileInfoByHandle Inspect(SafeFileHandle handle)
    {
        if (GetFileType(handle) != FileTypeDisk || !GetFileInformationByHandle(handle, out var info))
            throw new IOException("A normal disk file handle is required.");
        return info;
    }

    private static bool SameFileState(FileInfoByHandle a, FileInfoByHandle b) =>
        a.Attributes == b.Attributes && a.VolumeSerial == b.VolumeSerial &&
        a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow &&
        a.SizeHigh == b.SizeHigh && a.SizeLow == b.SizeLow && a.Links == b.Links &&
        a.LastWriteTime.High == b.LastWriteTime.High && a.LastWriteTime.Low == b.LastWriteTime.Low;

    public void Dispose() => root.Dispose();

    internal sealed class CapturedFile : IDisposable
    {
        private readonly byte[] bytes;
        private readonly SafeFileHandle temporaryHandle;
        private readonly FileInfoByHandle identity;
        internal ReadOnlyMemory<byte> Bytes { get; }

        internal CapturedFile(byte[] bytes, SafeFileHandle temporaryHandle, FileInfoByHandle identity)
        {
            this.bytes = bytes;
            Bytes = bytes;
            this.temporaryHandle = temporaryHandle;
            this.identity = identity;
        }

        // Call only after the matching resource transaction is durable and applied.
        internal bool DeleteAfterDurableApply()
        {
            if (temporaryHandle == null || temporaryHandle.IsClosed) return false;
            FileInfoByHandle current;
            try { current = Inspect(temporaryHandle); }
            catch (IOException) { return false; }
            if (!SameFileState(identity, current) || current.Links != 1 ||
                (current.Attributes & FileAttributeReparsePoint) != 0)
                return false;
            var disposition = new FileDispositionInfo { DeleteFile = 1 };
            return SetFileInformationByHandle(temporaryHandle, 4, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>());
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(bytes);
            temporaryHandle?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length; internal ushort MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    {
        internal int Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { internal IntPtr Status; internal IntPtr Information; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileTime { internal uint Low; internal uint High; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileInfoByHandle
    {
        internal uint Attributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerial;
        internal uint SizeHigh;
        internal uint SizeLow;
        internal uint Links;
        internal uint IndexHigh;
        internal uint IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDispositionInfo { internal byte DeleteFile; }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint desiredAccess,
        ref ObjectAttributes attributes, out IoStatusBlock ioStatus, IntPtr allocationSize,
        uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions,
        IntPtr eaBuffer, uint eaLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoByHandle info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int fileInformationClass,
        ref FileDispositionInfo info, uint bufferSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out SafeFileHandle targetHandle, uint desiredAccess,
        bool inheritHandle, uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
}
