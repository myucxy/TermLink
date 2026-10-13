using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TermLink.Common;

internal sealed class KittyCaptureQuotaExceededException : IOException
{
    internal KittyCaptureQuotaExceededException() : base("Kitty capture store quota exceeded.") { }
}

// The caller supplies a trusted, existing, private directory. No terminal path reaches this store.
internal sealed class KittyCaptureStore : IDisposable
{
    internal const int MaxObjectBytes = 32 * 1024 * 1024;
    internal const long MaxSessionBytes = 64L * 1024 * 1024;
    internal const long MaxGlobalBytes = 256L * 1024 * 1024;
    internal const int MaxSessionObjects = 256;
    internal const int MaxGlobalObjects = 1024;
    private const int MaxStoredBytes = MaxObjectBytes + 64 * 1024;
    private const int HeaderBytes = 88;
    private const uint FileReadData = 1;
    private const uint FileWriteData = 2;
    private const uint FileReadAttributes = 0x80;
    private const uint FileListDirectory = 1;
    private const uint Delete = 0x10000;
    private const uint Synchronize = 0x100000;
    private const uint ShareReadWrite = 3;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint FileDirectoryFile = 1;
    private const uint FileNonDirectoryFile = 0x40;
    private const uint FileSynchronousIoNonalert = 0x20;
    private const uint FileOpenReparsePoint = 0x200000;
    private const uint ObjCaseInsensitive = 0x40;
    private const uint ObjDontReparse = 0x1000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint StatusNoMoreFiles = 0x80000006;
    private const uint StatusNameNotFound = 0xC0000034;
    private const uint StatusPathNotFound = 0xC000003A;
    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("TermLink Kitty durable capture v1\0");
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TLKC0001");

    private readonly SafeFileHandle root;
    private readonly Mutex gate;
    private readonly Guid session;

    internal KittyCaptureStore(string trustedAbsoluteRoot, Guid session)
    {
        if (session == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(session));
        if (string.IsNullOrWhiteSpace(trustedAbsoluteRoot) || !Path.IsPathFullyQualified(trustedAbsoluteRoot))
            throw new ArgumentException("A trusted absolute store root is required.", nameof(trustedAbsoluteRoot));
        var rootPath = Path.GetFullPath(trustedAbsoluteRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (rootPath.Length < 4 || !char.IsLetter(rootPath[0]) || rootPath[1] != ':' ||
            rootPath[2] != '\\' || rootPath.IndexOf(':', 2) >= 0 ||
            rootPath.Split('\\').Skip(1).Any(part => part.EndsWith(' ') || part.EndsWith('.')))
            throw new ArgumentException("The store root must be a local drive directory.", nameof(trustedAbsoluteRoot));
        this.session = session;
        root = OpenAbsoluteRoot(rootPath);
        try
        {
            var info = Inspect(root, directory: true);
            gate = new Mutex(false, "Local\\TermLink.KittyCapture." +
                info.VolumeSerial.ToString("X8") + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8"));
        }
        catch { root.Dispose(); throw; }
    }

    internal CaptureReceipt Put(Guid objectId, ulong sourceSequence, ReadOnlySpan<byte> canonicalBytes)
    {
        if (objectId == Guid.Empty || sourceSequence == 0 || canonicalBytes.Length > MaxObjectBytes)
            throw new ArgumentException("Capture identity or size is invalid.");
        var hash = SHA256.HashData(canonicalBytes);
        var receipt = new CaptureReceipt(session, sourceSequence, objectId, canonicalBytes.Length, hash);
        EnterGate();
        try
        {
            var directory = OpenSession(create: false, allowMissing: true);
            try
            {
                var destination = objectId.ToString("N") + ".capture";
                if (directory != null)
                {
                    using var existing = OpenChild(directory, destination,
                        FileReadData | FileReadAttributes | Synchronize,
                        FileOpen, directory: false, allowMissing: true);
                    if (existing != null)
                    {
                        var prior = ReadOpened(existing, receipt);
                        try
                        {
                            if (prior.AsSpan().SequenceEqual(canonicalBytes)) return receipt;
                            throw new InvalidDataException("A different Kitty capture already owns this object ID.");
                        }
                        finally { CryptographicOperations.ZeroMemory(prior); }
                    }
                }
                var usage = CountAll();
                var minimumBytes = HeaderBytes + canonicalBytes.Length;
                if (usage.GlobalObjects >= MaxGlobalObjects || usage.SessionObjects >= MaxSessionObjects ||
                    usage.GlobalBytes > MaxGlobalBytes - minimumBytes ||
                    usage.SessionBytes > MaxSessionBytes - minimumBytes)
                    throw new KittyCaptureQuotaExceededException();
                var plaintext = new byte[HeaderBytes + canonicalBytes.Length];
                byte[] encrypted = null;
                try
                {
                    Magic.CopyTo(plaintext, 0);
                    session.TryWriteBytes(plaintext.AsSpan(8, 16));
                    objectId.TryWriteBytes(plaintext.AsSpan(24, 16));
                    BinaryPrimitives.WriteUInt64LittleEndian(plaintext.AsSpan(40, 8), sourceSequence);
                    BinaryPrimitives.WriteInt32LittleEndian(plaintext.AsSpan(48, 4), canonicalBytes.Length);
                    hash.CopyTo(plaintext, 56);
                    canonicalBytes.CopyTo(plaintext.AsSpan(HeaderBytes));
                    if (!CryptographicOperations.FixedTimeEquals(
                            SHA256.HashData(plaintext.AsSpan(HeaderBytes)), hash))
                        throw new InvalidDataException("Kitty capture source changed during Put.");
                    encrypted = ProtectedData.Protect(plaintext, Entropy(receipt), DataProtectionScope.CurrentUser);
                    if (encrypted.Length > MaxStoredBytes ||
                        usage.GlobalBytes > MaxGlobalBytes - encrypted.Length ||
                        usage.SessionBytes > MaxSessionBytes - encrypted.Length)
                        throw new KittyCaptureQuotaExceededException();
                    directory ??= OpenSession(create: true);
                    var pendingName = objectId.ToString("N") + "." + Guid.NewGuid().ToString("N") + ".pending";
                    using var pending = OpenChild(directory, pendingName,
                        FileWriteData | FileReadAttributes | Delete | Synchronize,
                        FileCreate, directory: false, allowMissing: false);
                    // Failed writes remain charged as pending until explicit session cleanup.
                    RandomAccess.Write(pending, encrypted, 0);
                    RandomAccess.FlushToDisk(pending);
                    RenameWithinDirectory(pending, directory, destination);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                    if (encrypted != null) CryptographicOperations.ZeroMemory(encrypted);
                }
            }
            finally { directory?.Dispose(); }
        }
        finally { gate.ReleaseMutex(); }
        return receipt;
    }

    internal byte[] ReadExact(Guid expectedSession, ulong eventSequence, Guid objectId,
        int expectedLength, ReadOnlySpan<byte> expectedSha256)
    {
        if (expectedSession != session || eventSequence == 0 || objectId == Guid.Empty ||
            expectedLength < 0 || expectedLength > MaxObjectBytes || expectedSha256.Length != 32)
            throw new InvalidDataException("Kitty capture identity is invalid.");
        var receipt = new CaptureReceipt(session, eventSequence, objectId, expectedLength, expectedSha256.ToArray());
        try
        {
            using var directory = OpenSession(create: false);
            using var file = OpenChild(directory, objectId.ToString("N") + ".capture",
                FileReadData | FileReadAttributes | Synchronize, FileOpen, directory: false, allowMissing: false);
            return ReadOpened(file, receipt);
        }
        finally { CryptographicOperations.ZeroMemory(receipt.Sha256); }
    }

    internal void DeleteSession(Guid expectedSession)
    {
        if (expectedSession != session) throw new InvalidDataException("Kitty capture session is invalid.");
        WithGate(() =>
        {
            using var directory = OpenSession(create: false, allowMissing: true, forDelete: true);
            if (directory == null) return;
            var names = new List<string>();
            foreach (var name in Names(directory, MaxSessionObjects + 1))
            {
                if (!ValidObjectName(name)) throw new InvalidDataException("Unknown Kitty store entry.");
                names.Add(name);
            }
            foreach (var name in names)
            {
                using var file = OpenChild(directory, name,
                    FileReadAttributes | Delete | Synchronize, FileOpen, directory: false, allowMissing: false);
                Inspect(file, directory: false);
                if (!SetDisposition(file)) throw new IOException("Kitty capture cleanup failed.");
            }
            if (!SetDisposition(directory)) throw new IOException("Kitty capture directory cleanup failed.");
        });
    }

    private byte[] ReadOpened(SafeFileHandle file, CaptureReceipt expected)
    {
        var info = Inspect(file, directory: false);
        var size = ((long)info.SizeHigh << 32) | info.SizeLow;
        if (size <= 0 || size > MaxStoredBytes) throw new InvalidDataException("Kitty capture stored length is invalid.");
        var encrypted = new byte[(int)size];
        byte[] plain = null;
        try
        {
            var read = 0;
            while (read < encrypted.Length)
            {
                var next = RandomAccess.Read(file, encrypted.AsSpan(read), read);
                if (next == 0) throw new InvalidDataException("Kitty capture stored file was truncated.");
                read += next;
            }
            if (!SameFileState(info, Inspect(file, directory: false)))
                throw new InvalidDataException("Kitty capture changed during read.");
            try { plain = ProtectedData.Unprotect(encrypted, Entropy(expected), DataProtectionScope.CurrentUser); }
            catch (CryptographicException error) { throw new InvalidDataException("Kitty capture authentication failed.", error); }
            if (plain.Length != HeaderBytes + expected.Length ||
                !plain.AsSpan(0, 8).SequenceEqual(Magic) ||
                new Guid(plain.AsSpan(8, 16)) != expected.Session ||
                new Guid(plain.AsSpan(24, 16)) != expected.ObjectId ||
                BinaryPrimitives.ReadUInt64LittleEndian(plain.AsSpan(40, 8)) != expected.SourceSequence ||
                BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(48, 4)) != expected.Length ||
                !plain.AsSpan(52, 4).SequenceEqual(new byte[4]) ||
                !CryptographicOperations.FixedTimeEquals(plain.AsSpan(56, 32), expected.Sha256) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(plain.AsSpan(HeaderBytes)), expected.Sha256))
                throw new InvalidDataException("Kitty capture content or identity is invalid.");
            return plain.AsSpan(HeaderBytes).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    private (long GlobalBytes, int GlobalObjects, long SessionBytes, int SessionObjects) CountAll()
    {
        long globalBytes = 0, sessionBytes = 0;
        var globalObjects = 0;
        var sessionObjects = 0;
        foreach (var sessionName in Names(root, MaxGlobalObjects + 1))
        {
            if (!Guid.TryParseExact(sessionName, "N", out var listedSession) || listedSession == Guid.Empty)
                throw new InvalidDataException("Unknown Kitty store directory.");
            using var directory = OpenChild(root, sessionName,
                FileListDirectory | FileReadAttributes | Synchronize, FileOpen, directory: true, allowMissing: false);
            foreach (var name in Names(directory, MaxGlobalObjects + 1))
            {
                if (!ValidObjectName(name)) throw new InvalidDataException("Unknown Kitty store entry.");
                using var file = OpenChild(directory, name,
                    FileReadData | FileReadAttributes | Synchronize, FileOpen, directory: false, allowMissing: false);
                var info = Inspect(file, directory: false);
                var bytes = ((long)info.SizeHigh << 32) | info.SizeLow;
                if (bytes > MaxStoredBytes) throw new InvalidDataException("Kitty capture stored size is invalid.");
                globalBytes = checked(globalBytes + bytes);
                globalObjects++;
                if (listedSession == session) { sessionBytes = checked(sessionBytes + bytes); sessionObjects++; }
                if (globalBytes > MaxGlobalBytes || globalObjects > MaxGlobalObjects ||
                    sessionBytes > MaxSessionBytes || sessionObjects > MaxSessionObjects)
                    throw new IOException("Kitty capture store quota exceeded.");
            }
        }
        return (globalBytes, globalObjects, sessionBytes, sessionObjects);
    }

    private static byte[] Entropy(CaptureReceipt receipt)
    {
        var entropy = new byte[Purpose.Length + 16 + 16 + 8];
        Purpose.CopyTo(entropy, 0);
        receipt.Session.TryWriteBytes(entropy.AsSpan(Purpose.Length, 16));
        receipt.ObjectId.TryWriteBytes(entropy.AsSpan(Purpose.Length + 16, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(entropy.AsSpan(Purpose.Length + 32), receipt.SourceSequence);
        return entropy;
    }

    private void WithGate(Action action)
    {
        EnterGate();
        try { action(); }
        finally { gate.ReleaseMutex(); }
    }

    private void EnterGate()
    {
        bool held;
        try { held = gate.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("Kitty capture store is busy.");
    }

    private SafeFileHandle OpenSession(bool create, bool allowMissing = false, bool forDelete = false) =>
        OpenChild(root, session.ToString("N"),
            FileListDirectory | FileReadAttributes | Synchronize | (forDelete ? Delete : 0),
            create ? FileOpenIf : FileOpen, directory: true, allowMissing);

    private static SafeFileHandle OpenAbsoluteRoot(string path)
    {
        // The absolute root is trusted startup configuration; all untrusted descendants use Nt relative opens.
        var handle = CreateFile(path, FileListDirectory | FileReadAttributes | Synchronize,
            ShareReadWrite, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("Kitty capture store root cannot be opened.");
        }
        try { Inspect(handle, directory: true); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, uint access,
        uint disposition, bool directory, bool allowMissing)
    {
        var nameBytes = checked(name.Length * 2);
        if (nameBytes > ushort.MaxValue - 2) throw new InvalidDataException("Kitty store name is too long.");
        var namePointer = Marshal.StringToHGlobalUni(name);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var added = false;
        try
        {
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = (ushort)nameBytes,
                MaximumLength = (ushort)(nameBytes + 2),
                Buffer = namePointer
            }, unicodePointer, false);
            if (parent != null) parent.DangerousAddRef(ref added);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent?.DangerousGetHandle() ?? IntPtr.Zero,
                ObjectName = unicodePointer,
                Attributes = ObjCaseInsensitive | ObjDontReparse
            };
            var status = unchecked((uint)NtCreateFile(out var handle, access,
                ref attributes, out _, IntPtr.Zero, 0,
                ShareReadWrite, disposition,
                (directory ? FileDirectoryFile : FileNonDirectoryFile) |
                FileSynchronousIoNonalert | FileOpenReparsePoint, IntPtr.Zero, 0));
            if (allowMissing && (status == StatusNameNotFound || status == StatusPathNotFound))
            {
                handle?.Dispose();
                return null;
            }
            if ((status & 0x80000000) != 0 || handle == null || handle.IsInvalid)
            {
                handle?.Dispose();
                throw new IOException("Kitty capture store entry cannot be opened.");
            }
            try { Inspect(handle, directory); return handle; }
            catch { handle.Dispose(); throw; }
        }
        finally
        {
            if (added) parent.DangerousRelease();
            Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(namePointer);
        }
    }

    private static FileInfoByHandle Inspect(SafeFileHandle handle, bool directory)
    {
        if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) ||
            ((info.Attributes & FileAttributeDirectory) != 0) != directory ||
            (info.Attributes & FileAttributeReparsePoint) != 0 ||
            (!directory && info.Links != 1))
            throw new InvalidDataException("Kitty store entry is not an ordinary file or directory.");
        return info;
    }

    private static bool SameFileState(FileInfoByHandle a, FileInfoByHandle b) =>
        a.VolumeSerial == b.VolumeSerial && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow &&
        a.SizeHigh == b.SizeHigh && a.SizeLow == b.SizeLow &&
        a.LastWriteTime.High == b.LastWriteTime.High && a.LastWriteTime.Low == b.LastWriteTime.Low;

    private static IEnumerable<string> Names(SafeFileHandle directory, int maximum)
    {
        var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            var first = true;
            var count = 0;
            while (true)
            {
                var status = unchecked((uint)NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, out var result, buffer, 65536, 12, true, IntPtr.Zero, first));
                first = false;
                if (status == StatusNoMoreFiles) yield break;
                if ((status & 0x80000000) != 0 || result.Information.ToInt64() < 12)
                    throw new InvalidDataException("Kitty store directory enumeration failed.");
                var length = Marshal.ReadInt32(buffer, 8);
                if (length < 0 || (length & 1) != 0 || length > result.Information.ToInt64() - 12)
                    throw new InvalidDataException("Kitty store directory entry is malformed.");
                var name = Marshal.PtrToStringUni(IntPtr.Add(buffer, 12), length / 2);
                if (name is "." or "..") continue;
                if (++count > maximum) throw new IOException("Kitty capture store entry count exceeded.");
                yield return name;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool ValidObjectName(string name)
    {
        if (name.Length == 40 && name.EndsWith(".capture", StringComparison.Ordinal))
            return Guid.TryParseExact(name[..32], "N", out var id) && id != Guid.Empty;
        if (name.Length == 73 && name[32] == '.' && name.EndsWith(".pending", StringComparison.Ordinal))
            return Guid.TryParseExact(name[..32], "N", out var id) && id != Guid.Empty &&
                Guid.TryParseExact(name.Substring(33, 32), "N", out var nonce) && nonce != Guid.Empty;
        return false;
    }

    private static void RenameWithinDirectory(SafeFileHandle source, SafeFileHandle directory, string target)
    {
        var bytes = Encoding.Unicode.GetBytes(target);
        var buffer = Marshal.AllocHGlobal(24 + bytes.Length);
        try
        {
            Span<byte> zero = stackalloc byte[24];
            zero.Clear();
            Marshal.Copy(zero.ToArray(), 0, buffer, 24);
            Marshal.WriteIntPtr(buffer, 8, directory.DangerousGetHandle());
            Marshal.WriteInt32(buffer, 16, bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, 20), bytes.Length);
            var status = NtSetInformationFile(source, out _, buffer, 20 + bytes.Length, 10);
            if (status < 0) throw new IOException("Kitty capture atomic commit failed.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool SetDisposition(SafeFileHandle handle)
    {
        var deletion = new FileDispositionInfo { DeleteFile = 1 };
        return SetFileInformationByHandle(handle, 4, ref deletion, 1);
    }

    public void Dispose() { gate.Dispose(); root.Dispose(); }

    internal sealed record CaptureReceipt(Guid Session, ulong SourceSequence, Guid ObjectId, int Length, byte[] Sha256);

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { internal ushort Length, MaximumLength; internal IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes
    {
        internal int Length;
        internal IntPtr RootDirectory, ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { internal IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoByHandle
    {
        internal uint Attributes;
        internal FileTime CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDispositionInfo { internal byte DeleteFile; }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryDirectoryFile(SafeFileHandle directory, IntPtr eventHandle, IntPtr apcRoutine,
        IntPtr apcContext, out IoStatusBlock ioStatus, IntPtr buffer, uint length,
        uint informationClass, bool returnSingleEntry, IntPtr fileName, bool restartScan);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus,
        IntPtr buffer, int length, int informationClass);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoByHandle info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDispositionInfo info, uint length);
}
