// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TermLink.Agent.Terminal;

/// <summary>Account-bound durable tombstones fence snapshot restore before any file deletion.</summary>
internal sealed class PortableTerminalDeletionStore : IDisposable
{
    private const int MaximumRecords = 16384;
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private readonly string directory, path;
    private readonly byte[] key, aad;
    private Dictionary<Guid, Record> records = new();
    internal sealed record Record(Guid SessionId, Guid OperationId);
    private sealed record Stored(int Version, Record[] Records);

    internal PortableTerminalDeletionStore(string directory, byte[] key)
    {
        this.directory = Path.GetFullPath(directory);
        ValidateDirectory(this.directory);
        path = Path.Combine(this.directory, "deleted-sessions-v1.bin");
        this.key = key.ToArray();
        aad = Encoding.UTF8.GetBytes("TERMLINK-MAC-CLI-DELETIONS-V1:" + this.directory);
        Load();
    }

    internal IReadOnlyList<Record> Records => records.Values.ToArray();
    internal bool Contains(Guid id) => records.ContainsKey(id);

    internal void Mark(Guid id, Guid operationId)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (records.ContainsKey(id)) { FlushDirectory(directory); return; }
        if (id == Guid.Empty || records.Count >= MaximumRecords) throw new IOException("TERMINAL_DELETION_LIMIT");
        var updated = new Dictionary<Guid, Record>(records) { [id] = new(id, operationId) };
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Stored(1, updated.Values.OrderBy(x => x.SessionId).ToArray()));
        try
        {
            var blob = new byte[28 + plain.Length];
            RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(blob.AsSpan(0, 12), plain, blob.AsSpan(28), blob.AsSpan(12, 16), aad);
            ValidateDirectory(directory); ValidateFile(path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.WriteThrough }))
                { stream.Write(blob); stream.Flush(flushToDisk: true); }
                File.Move(temporary, path, overwrite: true);
                records = updated;
                FlushDirectory(directory);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal void RemoveStateFile(Guid id)
    {
        ValidateDirectory(directory);
        var state = Path.Combine(directory, id.ToString("N") + ".state");
        ValidateFile(state);
        File.Delete(state);
        FlushDirectory(directory);
    }

    private void Load()
    {
        ValidateFile(path);
        if (!File.Exists(path)) return;
        var length = new FileInfo(path).Length;
        if (length is < 28 or > MaximumFileBytes) throw new IOException("TERMINAL_DELETION_INVALID");
        var blob = File.ReadAllBytes(path); var plain = new byte[blob.Length - 28];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, aad);
            var stored = JsonSerializer.Deserialize<Stored>(plain) ?? throw new IOException("TERMINAL_DELETION_INVALID");
            if (stored.Version != 1 || stored.Records is null || stored.Records.Length > MaximumRecords ||
                stored.Records.Any(x => x is null || x.SessionId == Guid.Empty) ||
                stored.Records.Select(x => x.SessionId).Distinct().Count() != stored.Records.Length ||
                stored.Records.Where(x => x.OperationId != Guid.Empty).Select(x => x.OperationId).Distinct().Count() != stored.Records.Count(x => x.OperationId != Guid.Empty))
                throw new IOException("TERMINAL_DELETION_INVALID");
            records = stored.Records.ToDictionary(x => x.SessionId);
        }
        catch (JsonException error) { throw new IOException("TERMINAL_DELETION_INVALID", error); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void ValidateDirectory(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current) && current != Path.GetPathRoot(current); current = Path.GetDirectoryName(current))
        {
            // macOS system aliases are the only accepted link aliases.
            if (current is "/tmp" or "/var" or "/etc") continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("TERMINAL_STATE_LINK_REJECTED");
        }
    }
    private static void ValidateFile(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Exists && (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("TERMINAL_STATE_LINK_REJECTED");
    }
    private static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var fd = Open(path, 0x100000 | 0x100); // Darwin O_RDONLY | O_DIRECTORY | O_NOFOLLOW.
        if (fd < 0) throw new IOException("TERMINAL_DELETION_SYNC_FAILED");
        try { if (Fsync(fd) != 0) throw new IOException("TERMINAL_DELETION_SYNC_FAILED"); }
        finally { Close(fd); }
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int descriptor);
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
