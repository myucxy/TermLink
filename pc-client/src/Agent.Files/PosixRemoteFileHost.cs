using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TermLink.Agent.Connection;

namespace TermLink.Agent.Files;

/// <summary>Handle-anchored POSIX files. Directory ZIPs never follow child links.</summary>
public sealed class PosixRemoteFileHost : IRelayFileHost
{
    public const long MaximumTransferBytes = 5L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 100000, MaximumDepth = 64;
    private readonly string cache;
    private readonly long maximumBytes;
    private readonly SemaphoreSlim operations = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public PosixRemoteFileHost(string cacheDirectory, long maximumBytes = MaximumTransferBytes)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (!Path.IsPathFullyQualified(cacheDirectory) || maximumBytes is < 1 or > MaximumTransferBytes) throw new ArgumentException();
        cache = Normalize(cacheDirectory); this.maximumBytes = maximumBytes;
        Directory.CreateDirectory(cache);
        using var anchored = Open(cache); if (anchored.Type != 1) throw new RelayFileException("CACHE_INVALID");
        File.SetUnixFileMode(cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var directory in Directory.EnumerateDirectories(cache))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-26))
                SafeDelete(directory);
    }

    public static void ValidateNative()
    {
        using var root = Open("/");
        if (root.Type != 1) throw new RelayFileException("FILE_NATIVE_UNAVAILABLE");
    }

    public Task<JsonElement> ListAsync(string path, long offset, string query, CancellationToken token) => Task.Run(() =>
    {
        if (offset < 0 || query.Length > 512 || Utf8.GetByteCount(query) > 512 || query.EnumerateRunes().Count() > 128 || query.Contains('\0')) throw new RelayFileException("QUERY_INVALID");
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "/Volumes", "/" }.Distinct(StringComparer.Ordinal)
                .Select(p => new Entry(p == "/" ? "/" : Path.GetFileName(p), p, "directory", null, null, [])).Where(e => Matches(e.Name, query)).ToArray();
            return Page("", null, roots, offset);
        }
        path = Normalize(path);
        using var directory = Open(path);
        if (directory.Type != 1) throw new RelayFileException("DIRECTORY_REQUIRED");
        var entries = ReadDirectory(directory, token).Where(e => e.Type is 1 or 2).Select(e => new Entry(e.Name, Path.Combine(path, e.Name),
            e.Type == 1 ? "directory" : e.Type == 2 ? "file" : "other", e.Type == 2 ? e.Size : null,
            Time(e.Modified), e.Type == 3 ? ["symlink"] : e.Type == 4 ? ["special"] : e.Name.StartsWith('.') ? ["hidden"] : []))
            .Where(e => Matches(e.Name, query)).OrderBy(e => e.Kind == "directory" ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
        return Page(path, path == "/" ? null : Path.GetDirectoryName(path), entries, offset);
    }, token);

    private static JsonElement Page(string path, string? parent, Entry[] entries, long offset)
    {
        var page = entries.Skip((int)Math.Min(int.MaxValue, offset)).Take(500).ToList();
        while (true)
        {
            long? next = offset + page.Count < entries.Length ? offset + page.Count : null;
            var result = JsonSerializer.SerializeToElement(new { current_path = path, parent_path = parent, entries = page, next_offset = next }, Json);
            if (Utf8.GetByteCount(result.GetRawText()) <= 900 * 1024) return result;
            if (page.Count <= 1) throw new RelayFileException("LIST_RESULT_TOO_LARGE");
            page.RemoveAt(page.Count - 1);
        }
    }

    public async Task<RelayFileArtifact> PrepareAsync(Guid operationId, string path, CancellationToken token)
    {
        if (operationId == Guid.Empty) throw new ArgumentException();
        path = Normalize(path);
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(cache, operationId.ToString("N"));
            var metadataPath = Path.Combine(directory, "metadata.json");
            var payload = Path.Combine(directory, "payload");
            if (Directory.Exists(directory))
            {
                using var existing = Open(directory);
                if (File.Exists(metadataPath) && File.Exists(payload))
                {
                    using var meta = Open(metadataPath);
                    if (meta.Type != 2 || meta.Size > 8192) throw new RelayFileException("CACHE_INVALID");
                    using var metaStream = meta.Stream();
                    var stored = await JsonSerializer.DeserializeAsync<Metadata>(metaStream, Json, token).ConfigureAwait(false) ?? throw new RelayFileException("CACHE_INVALID");
                    if (stored.Operation != operationId || stored.Source != path || stored.Size is < 0 || stored.Size > maximumBytes || stored.Sha256.Length != 32) throw new RelayFileException("CACHE_SOURCE_CHANGED");
                    using var data = Open(payload);
                    if (data.Type != 2 || data.Size != stored.Size) throw new RelayFileException("CACHE_INVALID");
                    var stream = data.Stream();
                    try
                    {
                        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(hash, stored.Sha256)) throw new RelayFileException("CACHE_INVALID");
                        stream.Position = 0;
                        return new Artifact(stored, stream);
                    }
                    catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
                }
                SafeDelete(directory);
            }
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (path == cache || path.StartsWith(cache + "/", StringComparison.Ordinal)) throw new RelayFileException("INTERNAL_CACHE_UNSUPPORTED");
            using var source = Open(path);
            using var excludedCache = Open(cache);
            if (Same(source, excludedCache)) throw new RelayFileException("INTERNAL_CACHE_UNSUPPORTED");
            if (source.Size < 0 || source.Type == 2 && source.Size > maximumBytes) throw new RelayFileException("FILE_TOO_LARGE");
            var name = source.Type == 1 ? (Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } n ? n : "root") + ".zip" : Path.GetFileName(path);
            if (name.Length is < 1 or > 255 || name.Any(char.IsControl)) throw new RelayFileException("FILENAME_INVALID");
            var staging = Path.Combine(directory, "payload.tmp");
            try
            {
                using (var output = CreatePrivate(staging))
                {
                    var bounded = new LimitedStream(output, maximumBytes);
                    if (source.Type == 1)
                    {
                        using var zip = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8);
                        var budget = new Budget(maximumBytes);
                        await ZipDirectoryAsync(zip, source, excludedCache, "", 0, budget, token).ConfigureAwait(false);
                    }
                    else
                    {
                        using var input = source.Stream();
                        await input.CopyToAsync(bounded, 128 * 1024, token).ConfigureAwait(false);
                    }
                    output.Flush(flushToDisk: true);
                }
                File.Move(staging, payload);
                using var data = Open(payload);
                var content = data.Stream();
                try
                {
                    var hash = await SHA256.HashDataAsync(content, token).ConfigureAwait(false); content.Position = 0;
                    var metadata = new Metadata(operationId, path, name, source.Type == 1, content.Length, hash);
                    var metaStaging = metadataPath + ".tmp";
                    using (var stream = CreatePrivate(metaStaging)) { await JsonSerializer.SerializeAsync(stream, metadata, Json, token).ConfigureAwait(false); stream.Flush(true); }
                    File.Move(metaStaging, metadataPath);
                    return new Artifact(metadata, content);
                }
                catch { await content.DisposeAsync().ConfigureAwait(false); throw; }
            }
            catch { SafeDelete(directory); throw; }
        }
        finally { operations.Release(); }
    }

    private async Task ZipDirectoryAsync(ZipArchive zip, Handle directory, Handle excludedCache, string prefix, int depth, Budget budget, CancellationToken token)
    {
        if (depth >= MaximumDepth) throw new RelayFileException("ARCHIVE_DEPTH_LIMIT");
        foreach (var entry in ReadDirectory(directory, token))
        {
            token.ThrowIfCancellationRequested();
            if (++budget.Entries > MaximumEntries) throw new RelayFileException("ARCHIVE_ENTRY_LIMIT");
            // A link or device inside a directory is never traversed or read.
            if (entry.Type is not (1 or 2)) continue;
            if (entry.Name.Contains('\\') || entry.Name.Any(char.IsControl)) throw new RelayFileException("ARCHIVE_NAME_INVALID");
            using var child = OpenChild(directory, entry.Name);
            if (child.Type != entry.Type) throw new RelayFileException("SOURCE_CHANGED");
            // Compare directory identity, not just a path: macOS volume aliases
            // must never include this transfer cache in its own growing ZIP.
            if (child.Type == 1 && Same(child, excludedCache)) continue;
            var name = prefix + entry.Name;
            var target = zip.CreateEntry(child.Type == 1 ? name + "/" : name, CompressionLevel.Fastest);
            target.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if (child.Type == 1) await ZipDirectoryAsync(zip, child, excludedCache, name + "/", depth + 1, budget, token).ConfigureAwait(false);
            else
            {
                if (child.Size < 0 || child.Size > budget.Remaining) throw new RelayFileException("FILE_TOO_LARGE");
                using var input = child.Stream(); using var output = target.Open();
                await input.CopyToAsync(new LimitedStream(output, budget.Remaining), 128 * 1024, token).ConfigureAwait(false);
                budget.Remaining -= input.Length;
            }
        }
    }

    public async Task CleanupAsync(Guid operationId, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try { SafeDelete(Path.Combine(cache, operationId.ToString("N"))); }
        finally { operations.Release(); }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static bool Matches(string value, string query) => query.Length == 0 || value.Contains(query, StringComparison.OrdinalIgnoreCase);
    private static DateTimeOffset? Time(long value) { try { return DateTimeOffset.FromUnixTimeSeconds(value); } catch (ArgumentOutOfRangeException) { return null; } }
    private static FileStream CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite, Options = FileOptions.WriteThrough });
    }
    private static void SafeDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        using var dir = Open(path);
        foreach (var entry in ReadDirectory(dir, CancellationToken.None))
        {
            var child = Path.Combine(path, entry.Name);
            if (entry.Type == 1) SafeDelete(child); else File.Delete(child);
        }
        Directory.Delete(path);
    }
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Utf8.GetByteCount(path) > 4096 || path.Contains('\0') || !Path.IsPathFullyQualified(path)) throw new RelayFileException("PATH_INVALID");
        path = Path.GetFullPath(path);
        // Fixed macOS system aliases. User-controlled symlinks remain rejected.
        foreach (var alias in new[] { "/var", "/tmp", "/etc" })
            if (path == alias || path.StartsWith(alias + "/", StringComparison.Ordinal)) return "/private" + path;
        return path;
    }
    private static Handle Open(string path)
    {
        var fd = Native.Open(Normalize(path), out var type, out var size, out var modified);
        return new Handle(Check(fd), type, size, modified);
    }
    private static Handle OpenChild(Handle parent, string name)
    {
        var fd = Native.OpenChild(parent.Fd, name, out var type, out var size, out var modified);
        return new Handle(Check(fd), type, size, modified);
    }
    private static bool Same(Handle left, Handle right) => Check(Native.Same(left.Fd, right.Fd)) == 1;
    private static int Check(int result)
    {
        if (result >= 0) return result;
        throw new RelayFileException(-result is 1 or 13 ? "ACCESS_DENIED" : -result is 2 or 20 ? "PATH_NOT_FOUND" : -result is 40 or 62 ? "SYMLINK_UNSUPPORTED" : "FILE_ACCESS_FAILED");
    }
    private static IEnumerable<NativeEntry> ReadDirectory(Handle directory, CancellationToken token)
    {
        if (directory.Type != 1) throw new RelayFileException("DIRECTORY_REQUIRED");
        var iterator = Native.Begin(directory.Fd); if (iterator == IntPtr.Zero) throw new RelayFileException("ACCESS_DENIED");
        try
        {
            var name = new byte[1024]; var count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var result = Native.Next(iterator, name, (nuint)name.Length, out var type, out var size, out var modified);
                if (result == 0) yield break;
                Check(result);
                if (++count > MaximumEntries) throw new RelayFileException("DIRECTORY_ENTRY_LIMIT");
                var length = Array.IndexOf(name, (byte)0);
                string text;
                try { text = Utf8.GetString(name, 0, length); }
                catch (DecoderFallbackException) { throw new RelayFileException("FILENAME_ENCODING_UNSUPPORTED"); }
                if (text.Length == 0 || text.Any(char.IsControl)) continue;
                yield return new(text, type, size, modified);
            }
        }
        finally { Native.End(iterator); }
    }
    private sealed record Entry(string Name, string Path, string Kind, long? SizeBytes, DateTimeOffset? ModifiedAt, string[] Attributes);
    private sealed record NativeEntry(string Name, int Type, long Size, long Modified);
    private sealed record Metadata(Guid Operation, string Source, string Filename, bool DirectoryZip, long Size, byte[] Sha256);
    private sealed class Budget(long bytes) { public long Remaining = bytes; public int Entries; }
    private sealed class Artifact(Metadata metadata, FileStream content) : RelayFileArtifact
    {
        public override string Filename => metadata.Filename;
        public override bool DirectoryZip => metadata.DirectoryZip;
        public override long SizeBytes => metadata.Size;
        public override byte[] Sha256 => metadata.Sha256;
        public override Stream Content => content;
        public override ValueTask DisposeAsync() => content.DisposeAsync();
    }
    private sealed class Handle(int fd, int type, long size, long modified) : IDisposable
    {
        public int Fd { get; } = fd; public int Type { get; } = type; public long Size { get; } = size; public long Modified { get; } = modified;
        private bool transferred;
        public FileStream Stream() { if (Type != 2 || transferred) throw new RelayFileException("FILE_REQUIRED"); transferred = true; return new(new SafeFileHandle((IntPtr)Fd, ownsHandle: true), FileAccess.Read); }
        public void Dispose() { if (!transferred) Native.Close(Fd); }
    }
    private sealed class LimitedStream(Stream inner, long maximum) : Stream
    {
        private long written;
        private void Count(int count) { if (count < 0 || count > maximum - written) throw new RelayFileException("FILE_TOO_LARGE"); written += count; }
        public override void Write(byte[] b, int o, int c) { Count(c); inner.Write(b, o, c); }
        public override void Write(ReadOnlySpan<byte> b) { Count(b.Length); inner.Write(b); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken token = default) { Count(b.Length); await inner.WriteAsync(b, token); }
        public override Task WriteAsync(byte[] b,int o,int c,CancellationToken t) => WriteAsync(b.AsMemory(o,c),t).AsTask();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override int Read(byte[] b,int o,int c) => throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException(); public override void SetLength(long l)=>throw new NotSupportedException();
    }
    private static class Native
    {
        private const string Library = "termlink-files";
        [DllImport(Library, EntryPoint="termlink_files_same")] public static extern int Same(int left,int right);
        [DllImport(Library, EntryPoint="termlink_files_open")] public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out int type,out long size,out long modified);
        [DllImport(Library, EntryPoint="termlink_files_open_child")] public static extern int OpenChild(int parent,[MarshalAs(UnmanagedType.LPUTF8Str)]string name,out int type,out long size,out long modified);
        [DllImport(Library, EntryPoint="termlink_files_list_begin")] public static extern IntPtr Begin(int fd);
        [DllImport(Library, EntryPoint="termlink_files_list_next")] public static extern int Next(IntPtr iterator,byte[] name,nuint capacity,out int type,out long size,out long modified);
        [DllImport(Library, EntryPoint="termlink_files_list_end")] public static extern void End(IntPtr iterator);
        [DllImport(Library, EntryPoint="termlink_files_close")] public static extern int Close(int fd);
    }
}
