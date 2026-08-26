using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    internal sealed class FileOperationException : Exception
    {
        internal FileOperationException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        internal FileOperationException(string code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code;
        }

        internal string Code { get; }
    }

    internal sealed class FileOperationEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; }
        public long? SizeBytes { get; set; }
        public DateTimeOffset? ModifiedAt { get; set; }
        public string[] Attributes { get; set; }
    }

    internal sealed class DirectoryListingResult
    {
        public string CurrentPath { get; set; }
        public string ParentPath { get; set; }
        public IReadOnlyList<FileOperationEntry> Entries { get; set; }
        public long? NextOffset { get; set; }
    }

    internal sealed class WindowsPathService
    {
        internal string NormalizeExistingDirectory(string path)
        {
            var normalized = NormalizeAbsolutePath(path);
            if (!Directory.Exists(normalized))
                throw new FileOperationException("DIRECTORY_NOT_FOUND", "The requested directory does not exist.");
            return normalized;
        }

        internal string NormalizeExistingFileOrDirectory(string path)
        {
            var normalized = NormalizeAbsolutePath(path);
            if (!File.Exists(normalized) && !Directory.Exists(normalized))
                throw new FileOperationException("PATH_NOT_FOUND", "The requested file or directory does not exist.");
            return normalized;
        }

        internal string NormalizeAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new FileOperationException("PATH_REQUIRED", "A Windows path is required.");
            if (path.Length > 4096 || path.IndexOf('\0') >= 0)
                throw new FileOperationException("PATH_INVALID", "The requested path is invalid.");
            if (!Path.IsPathFullyQualified(path))
                throw new FileOperationException("PATH_NOT_ABSOLUTE", "The requested path must be absolute.");
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                path.StartsWith("\\\\.\\", StringComparison.Ordinal))
            {
                throw new FileOperationException("PATH_NAMESPACE_UNSUPPORTED", "Windows device paths are not supported.");
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception error) when (
                error is ArgumentException ||
                error is NotSupportedException ||
                error is PathTooLongException)
            {
                throw new FileOperationException("PATH_INVALID", "The requested path is invalid.", error);
            }
        }
    }

    internal sealed class DirectoryListingService
    {
        internal const int MaximumPageSize = 500;
        // The server accepts at most 1 MiB for the LIST result itself. Keep a
        // meaningful envelope below that boundary so completion framing and
        // future small contract additions cannot turn a valid page into a 400.
        internal const int MaximumSerializedResultBytes = 900 * 1024;
        private static readonly JsonSerializerOptions ListingJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        private readonly WindowsPathService paths;
        private readonly Func<IEnumerable<string>> rootProvider;

        internal DirectoryListingService()
            : this(new WindowsPathService(), () => Directory.GetLogicalDrives())
        {
        }

        internal DirectoryListingService(
            WindowsPathService paths,
            Func<IEnumerable<string>> rootProvider)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
        }

        internal DirectoryListingResult List(string path, long offset)
        {
            if (offset < 0)
                throw new FileOperationException("OFFSET_INVALID", "The directory offset cannot be negative.");
            return string.IsNullOrWhiteSpace(path)
                ? ListRoots(offset)
                : ListDirectory(paths.NormalizeExistingDirectory(path), offset);
        }

        private DirectoryListingResult ListRoots(long offset)
        {
            IEnumerable<FileOperationEntry> roots;
            try
            {
                roots = rootProvider()
                    .Select(NormalizeRoot)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.InvariantCultureIgnoreCase)
                    .ThenBy(value => value, StringComparer.InvariantCulture)
                    .Select(value => new FileOperationEntry
                    {
                        Name = RootName(value),
                        Path = value,
                        Kind = "directory",
                        SizeBytes = null,
                        ModifiedAt = null,
                        Attributes = Array.Empty<string>()
                    })
                    .ToArray();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new FileOperationException("ROOT_LIST_FAILED", "Windows drive roots could not be enumerated.", error);
            }
            return Page(string.Empty, null, roots, offset);
        }

        private DirectoryListingResult ListDirectory(string path, long offset)
        {
            FileOperationEntry[] entries;
            try
            {
                entries = new DirectoryInfo(path)
                    .EnumerateFileSystemInfos()
                    .Select(CreateEntry)
                    .OrderBy(entry => entry.Kind == "directory" ? 0 : 1)
                    .ThenBy(entry => entry.Name, StringComparer.InvariantCultureIgnoreCase)
                    .ThenBy(entry => entry.Name, StringComparer.InvariantCulture)
                    .ToArray();
            }
            catch (DirectoryNotFoundException error)
            {
                throw new FileOperationException("DIRECTORY_NOT_FOUND", "The requested directory no longer exists.", error);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new FileOperationException("ACCESS_DENIED", "Access to the requested directory was denied.", error);
            }
            catch (IOException error)
            {
                throw new FileOperationException("DIRECTORY_LIST_FAILED", "The requested directory could not be enumerated.", error);
            }
            return Page(path, ParentOf(path), entries, offset);
        }

        internal static DirectoryListingResult Page(
            string currentPath,
            string parentPath,
            IEnumerable<FileOperationEntry> entries,
            long offset)
        {
            var available = entries.SkipLong(offset).Take(MaximumPageSize + 1).ToArray();
            var page = new List<FileOperationEntry>(Math.Min(MaximumPageSize, available.Length));
            var maximumEntries = Math.Min(MaximumPageSize, available.Length);
            for (var index = 0; index < maximumEntries; index++)
            {
                page.Add(available[index]);
                var candidate = CreatePageResult(currentPath, parentPath, page, offset, page.Count < available.Length);
                if (SerializedSize(candidate) <= MaximumSerializedResultBytes) continue;
                page.RemoveAt(page.Count - 1);
                break;
            }

            if (page.Count == 0 && available.Length != 0)
            {
                throw new FileOperationException(
                    "LIST_ENTRY_TOO_LARGE",
                    "A directory entry cannot fit within the file-operation result limit.");
            }
            var result = CreatePageResult(currentPath, parentPath, page, offset, page.Count < available.Length);
            if (SerializedSize(result) > MaximumSerializedResultBytes)
            {
                throw new FileOperationException(
                    "LIST_RESULT_TOO_LARGE",
                    "The directory page cannot fit within the file-operation result limit.");
            }
            return result;
        }

        internal static int SerializedSize(DirectoryListingResult result) =>
            JsonSerializer.SerializeToUtf8Bytes(result, ListingJsonOptions).Length;

        private static DirectoryListingResult CreatePageResult(
            string currentPath,
            string parentPath,
            IReadOnlyList<FileOperationEntry> entries,
            long offset,
            bool hasMore) => new DirectoryListingResult
        {
            CurrentPath = currentPath,
            ParentPath = parentPath,
            Entries = entries.ToArray(),
            NextOffset = hasMore ? checked(offset + entries.Count) : null
        };

        private static FileOperationEntry CreateEntry(FileSystemInfo value)
        {
            var attributes = value.Attributes;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            return new FileOperationEntry
            {
                Name = value.Name,
                Path = value.FullName,
                Kind = isDirectory ? "directory" : "file",
                SizeBytes = isDirectory ? null : ((FileInfo)value).Length,
                ModifiedAt = new DateTimeOffset(value.LastWriteTimeUtc, TimeSpan.Zero),
                Attributes = AttributeNames(attributes)
            };
        }

        private static string NormalizeRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw new IOException("A logical drive provider returned an invalid root.");
            return Path.GetPathRoot(Path.GetFullPath(value));
        }

        private static string RootName(string root)
        {
            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(trimmed) ? root : trimmed;
        }

        private static string ParentOf(string path)
        {
            var root = Path.GetPathRoot(path);
            if (string.Equals(
                path.TrimEnd(Path.DirectorySeparatorChar),
                root?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return Directory.GetParent(path)?.FullName;
        }

        private static string[] AttributeNames(FileAttributes attributes)
        {
            var values = new List<string>();
            Add(FileAttributes.ReadOnly, "read-only");
            Add(FileAttributes.Hidden, "hidden");
            Add(FileAttributes.System, "system");
            Add(FileAttributes.Archive, "archive");
            Add(FileAttributes.ReparsePoint, "reparse-point");
            Add(FileAttributes.Compressed, "compressed");
            Add(FileAttributes.Encrypted, "encrypted");
            return values.ToArray();

            void Add(FileAttributes flag, string name)
            {
                if ((attributes & flag) != 0) values.Add(name);
            }
        }
    }

    internal static class EnumerablePagingExtensions
    {
        internal static IEnumerable<T> SkipLong<T>(this IEnumerable<T> source, long count)
        {
            foreach (var item in source)
            {
                if (count > 0)
                {
                    count--;
                    continue;
                }
                yield return item;
            }
        }
    }

    internal sealed class FileTransferArtifact : IAsyncDisposable
    {
        private readonly Stream stream;
        private readonly string temporaryPath;
        private int opened;
        private int disposed;

        internal FileTransferArtifact(
            string filename,
            bool directoryZip,
            long sizeBytes,
            byte[] sha256,
            Stream stream,
            string temporaryPath)
        {
            Filename = filename;
            DirectoryZip = directoryZip;
            SizeBytes = sizeBytes;
            Sha256 = sha256;
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.temporaryPath = temporaryPath;
        }

        internal string Filename { get; }
        internal bool DirectoryZip { get; }
        internal long SizeBytes { get; }
        internal byte[] Sha256 { get; }
        internal string Sha256Base64Url => Convert.ToBase64String(Sha256)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal Stream OpenRead()
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(FileTransferArtifact));
            if (Interlocked.Exchange(ref opened, 1) != 0)
                throw new InvalidOperationException("The transfer stream has already been opened.");
            if (stream.CanSeek) stream.Position = 0;
            return stream;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await stream.DisposeAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(temporaryPath)) return;
            try { File.Delete(temporaryPath); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal sealed class BoundedWriteStream : Stream
    {
        private readonly Stream inner;
        private readonly long maximumLength;
        private readonly bool leaveOpen;

        internal BoundedWriteStream(Stream inner, long maximumLength, bool leaveOpen = false)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (!inner.CanWrite) throw new ArgumentException("The bounded stream must be writable.", nameof(inner));
            if (maximumLength < 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
            this.maximumLength = maximumLength;
            this.leaveOpen = leaveOpen;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set
            {
                if (value < 0 || value > maximumLength) ThrowLimitExceeded();
                inner.Position = value;
            }
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(inner.Position + offset),
                SeekOrigin.End => checked(inner.Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > maximumLength) ThrowLimitExceeded();
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            if (value < 0 || value > maximumLength) ThrowLimitExceeded();
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWriteFits(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWriteFits(buffer.Length);
            inner.Write(buffer);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureWriteFits(count);
            return inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWriteFits(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        private void EnsureWriteFits(int count)
        {
            if (count < 0 || inner.Position > maximumLength - count) ThrowLimitExceeded();
        }

        private static void ThrowLimitExceeded() => throw new FileOperationException(
            "FILE_TOO_LARGE",
            "The requested download exceeds the 5 GiB server limit.");

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    internal sealed class FileTransferPreparer
    {
        internal const long MaximumTransferBytes = 5L * 1024 * 1024 * 1024;
        private static readonly DateTimeOffset MinimumZipTimestamp =
            new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset MaximumZipTimestamp =
            new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        private readonly WindowsPathService paths;
        private readonly string temporaryRoot;
        private readonly long maximumTransferBytes;

        internal FileTransferPreparer()
            : this(
                new WindowsPathService(),
                Path.Combine(Path.GetTempPath(), "TermLink", "file-operations"))
        {
        }

        internal FileTransferPreparer(
            WindowsPathService paths,
            string temporaryRoot,
            long maximumTransferBytes = MaximumTransferBytes)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            if (string.IsNullOrWhiteSpace(temporaryRoot))
                throw new ArgumentException("A temporary artifact root is required.", nameof(temporaryRoot));
            this.temporaryRoot = Path.GetFullPath(temporaryRoot);
            if (maximumTransferBytes < 1 || maximumTransferBytes > MaximumTransferBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumTransferBytes));
            this.maximumTransferBytes = maximumTransferBytes;
        }

        internal async Task<FileTransferArtifact> PrepareAsync(string requestedPath, CancellationToken token)
        {
            var path = paths.NormalizeExistingFileOrDirectory(requestedPath);
            if (Directory.Exists(path)) return await PrepareDirectoryAsync(path, token).ConfigureAwait(false);
            return await PrepareFileAsync(path, token).ConfigureAwait(false);
        }

        private async Task<FileTransferArtifact> PrepareFileAsync(string path, CancellationToken token)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new FileOperationException("ACCESS_DENIED", "Access to the requested file was denied.", error);
            }
            catch (IOException error)
            {
                throw new FileOperationException("FILE_OPEN_FAILED", "The requested file could not be opened.", error);
            }

            try
            {
                ValidateSize(stream.Length);
                var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                stream.Position = 0;
                return new FileTransferArtifact(
                    Path.GetFileName(path),
                    false,
                    stream.Length,
                    hash,
                    stream,
                    null);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private async Task<FileTransferArtifact> PrepareDirectoryAsync(string path, CancellationToken token)
        {
            var rootAttributes = File.GetAttributes(path);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
                throw new FileOperationException(
                    "REPARSE_DIRECTORY_UNSUPPORTED",
                    "A reparse-point directory cannot be downloaded as an archive.");

            Directory.CreateDirectory(temporaryRoot);
            var temporaryPath = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await WriteDirectoryZipAsync(path, temporaryPath, token).ConfigureAwait(false);
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
                try
                {
                    ValidateSize(stream.Length);
                    var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                    stream.Position = 0;
                    return new FileTransferArtifact(
                        DirectoryArchiveName(path),
                        true,
                        stream.Length,
                        hash,
                        stream,
                        temporaryPath);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                try { File.Delete(temporaryPath); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                throw;
            }
        }

        private async Task WriteDirectoryZipAsync(
            string directoryPath,
            string destinationPath,
            CancellationToken token)
        {
            try
            {
                await using var output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var boundedOutput = new BoundedWriteStream(output, maximumTransferBytes, true);
                using var archive = new ZipArchive(boundedOutput, ZipArchiveMode.Create, true, Encoding.UTF8);
                var rootName = ArchiveRootName(directoryPath);
                await AddDirectoryAsync(archive, new DirectoryInfo(directoryPath), rootName + "/", token)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException error)
            {
                throw new FileOperationException("ACCESS_DENIED", "Access to the requested directory was denied.", error);
            }
            catch (IOException error)
            {
                throw new FileOperationException("ZIP_CREATE_FAILED", "The requested directory could not be archived.", error);
            }
        }

        private static async Task AddDirectoryAsync(
            ZipArchive archive,
            DirectoryInfo directory,
            string entryPrefix,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var directoryEntry = archive.CreateEntry(entryPrefix, CompressionLevel.NoCompression);
            directoryEntry.LastWriteTime = ZipTimestamp(directory.LastWriteTimeUtc);

            var children = directory.EnumerateFileSystemInfos()
                .OrderBy(value => (value.Attributes & FileAttributes.Directory) != 0 ? 0 : 1)
                .ThenBy(value => value.Name, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(value => value.Name, StringComparer.InvariantCulture)
                .ToArray();
            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                var childEntryName = entryPrefix + child.Name;
                // Never dereference either directory junctions/symlinks or
                // file symlinks while packaging a user-selected tree.
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((child.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!ShouldTraverseDirectory(child.Attributes)) continue;
                    await AddDirectoryAsync(
                        archive,
                        (DirectoryInfo)child,
                        childEntryName + "/",
                        token).ConfigureAwait(false);
                    continue;
                }

                var entry = archive.CreateEntry(childEntryName, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipTimestamp(child.LastWriteTimeUtc);
                await using var input = new FileStream(
                    child.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var target = entry.Open();
                await input.CopyToAsync(target, 128 * 1024, token).ConfigureAwait(false);
            }
        }

        internal static bool ShouldTraverseDirectory(FileAttributes attributes) =>
            (attributes & FileAttributes.Directory) != 0 &&
            (attributes & FileAttributes.ReparsePoint) == 0;

        private static DateTimeOffset ZipTimestamp(DateTime value)
        {
            var timestamp = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
            if (timestamp < MinimumZipTimestamp) return MinimumZipTimestamp;
            if (timestamp > MaximumZipTimestamp) return MaximumZipTimestamp;
            return timestamp;
        }

        private void ValidateSize(long size)
        {
            if (size > maximumTransferBytes)
                throw new FileOperationException(
                    "FILE_TOO_LARGE",
                    "The requested download exceeds the 5 GiB server limit.");
        }

        private static string DirectoryArchiveName(string path) => ArchiveRootName(path) + ".zip";

        private static string ArchiveRootName(string path)
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(name)) return SanitizeArchiveName(name);
            var root = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(root)) return "directory";
            return SanitizeArchiveName(root.Replace(':', '-'));
        }

        private static string SanitizeArchiveName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
            result = result.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(result)) result = "directory";
            return result.Length <= 240 ? result : result.Substring(0, 240);
        }
    }

    internal interface IProcessStarter
    {
        bool TryStart(ProcessStartInfo startInfo);
    }

    internal sealed class SystemProcessStarter : IProcessStarter
    {
        public bool TryStart(ProcessStartInfo startInfo)
        {
            using var process = Process.Start(startInfo);
            return process != null;
        }
    }

    internal sealed class ManagedTerminalLauncher
    {
        internal const string CommandPromptShellType = "command-prompt";
        internal const string WindowsPowerShellShellType = "windows-powershell";
        internal const string PowerShell7ShellType = "powershell-7";
        internal const string WslShellType = "wsl";
        internal const string CustomShellType = "custom";
        internal const int DefaultRegistrationTimeoutMilliseconds = 30000;
        private readonly WindowsPathService paths;
        private readonly IProcessStarter processes;
        private readonly Func<string> windowsTerminalResolver;
        private readonly string sessionHostPath;
        private readonly string commandPromptPath;
        private readonly string windowsPowerShellPath;
        private readonly string powerShell7Path;
        private readonly string wslPath;

        internal ManagedTerminalLauncher()
            : this(
                new WindowsPathService(),
                new SystemProcessStarter(),
                ResolveWindowsTerminal,
                Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.exe"),
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                ResolvePreferredExecutable(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "PowerShell", "7", "pwsh.exe"),
                    "pwsh.exe"),
                ResolvePreferredExecutable(
                    Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                    "wsl.exe"))
        {
        }

        internal ManagedTerminalLauncher(
            WindowsPathService paths,
            IProcessStarter processes,
            Func<string> windowsTerminalResolver,
            string sessionHostPath,
            string commandPromptPath,
            string windowsPowerShellPath,
            string powerShell7Path = null,
            string wslPath = null)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.processes = processes ?? throw new ArgumentNullException(nameof(processes));
            this.windowsTerminalResolver = windowsTerminalResolver ?? throw new ArgumentNullException(nameof(windowsTerminalResolver));
            this.sessionHostPath = Path.GetFullPath(sessionHostPath ?? throw new ArgumentNullException(nameof(sessionHostPath)));
            this.commandPromptPath = Path.GetFullPath(commandPromptPath ?? throw new ArgumentNullException(nameof(commandPromptPath)));
            this.windowsPowerShellPath = Path.GetFullPath(windowsPowerShellPath ?? throw new ArgumentNullException(nameof(windowsPowerShellPath)));
            this.powerShell7Path = NormalizeOptionalExecutablePath(powerShell7Path);
            this.wslPath = NormalizeOptionalExecutablePath(wslPath);
        }

        internal Guid Launch(string shellType, string workingDirectory, Guid? requestedSessionId = null)
        {
            var directory = paths.NormalizeExistingDirectory(workingDirectory);
            var profile = ResolveCommand(shellType);
            if (!File.Exists(sessionHostPath))
                throw new FileOperationException("SESSION_HOST_NOT_FOUND", "TermLink.SessionHost.exe is not installed.");
            if (!File.Exists(profile.ExecutablePath))
                throw new FileOperationException("SHELL_NOT_FOUND", "The requested Windows shell is not installed.");

            var sessionId = requestedSessionId.GetValueOrDefault(Guid.NewGuid());
            if (sessionId == Guid.Empty)
                throw new FileOperationException("SESSION_ID_INVALID", "A non-empty managed session ID is required.");
            var arguments = BuildSessionHostArgumentsForProfile(profile, directory, sessionId);
            var windowsTerminal = windowsTerminalResolver();
            if (!string.IsNullOrWhiteSpace(windowsTerminal))
            {
                var terminalStart = new ProcessStartInfo
                {
                    FileName = windowsTerminal,
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                terminalStart.ArgumentList.Add("-w");
                terminalStart.ArgumentList.Add("new");
                terminalStart.ArgumentList.Add("new-tab");
                if (profile.RunAsAdministrator) terminalStart.ArgumentList.Add("--elevate");
                terminalStart.ArgumentList.Add("-d");
                terminalStart.ArgumentList.Add(directory);
                terminalStart.ArgumentList.Add(sessionHostPath);
                foreach (var argument in arguments) terminalStart.ArgumentList.Add(argument);
                try
                {
                    if (processes.TryStart(terminalStart)) return sessionId;
                }
                catch (Exception error) when (
                    error is IOException ||
                    error is UnauthorizedAccessException ||
                    error is InvalidOperationException ||
                    error is System.ComponentModel.Win32Exception)
                {
                    // A broken Windows Terminal alias must not prevent the
                    // installed SessionHost from opening a visible console.
                }
            }

            var directStart = new ProcessStartInfo
            {
                FileName = sessionHostPath,
                WorkingDirectory = directory,
                UseShellExecute = true
            };
            if (profile.RunAsAdministrator) directStart.Verb = "runas";
            foreach (var argument in arguments) directStart.ArgumentList.Add(argument);
            try
            {
                if (!processes.TryStart(directStart))
                    throw new FileOperationException("TERMINAL_LAUNCH_FAILED", "The terminal process could not be started.");
                return sessionId;
            }
            catch (FileOperationException) { throw; }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is InvalidOperationException ||
                error is System.ComponentModel.Win32Exception)
            {
                throw new FileOperationException("TERMINAL_LAUNCH_FAILED", "The terminal process could not be started.", error);
            }
        }

        internal string NormalizeWorkingDirectory(string workingDirectory) =>
            paths.NormalizeExistingDirectory(workingDirectory);

        internal string ManagedShellTypeFor(string commandId) => ResolveCommand(commandId).ManagedShell;

        /// <summary>
        /// Returns only managed shells that this installation can actually
        /// launch. SessionHost is part of the capability because a shell
        /// executable alone is not a remotely openable TermLink terminal.
        /// </summary>
        internal static string[] DetectSupportedShellTypes()
        {
            return DetectSupportedShellTypesAtPaths(
                Path.Combine(AppContext.BaseDirectory, "TermLink.SessionHost.exe"),
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                ResolvePreferredExecutable(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "PowerShell", "7", "pwsh.exe"),
                    "pwsh.exe"),
                ResolvePreferredExecutable(
                    Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                    "wsl.exe"));
        }

        internal static TerminalCommandCapability[] DetectTerminalCommandCapabilities()
        {
            var launcher = new ManagedTerminalLauncher();
            return launcher.AvailableProfiles()
                .Select(profile => new TerminalCommandCapability
                {
                    Id = profile.Id,
                    DisplayName = profile.DisplayName,
                    ShellType = profile.ManagedShell,
                    Source = profile.Source
                })
                .ToArray();
        }

        internal static TerminalCommandProfile[] LoadAvailableTerminalCommandProfiles() =>
            new ManagedTerminalLauncher().AvailableProfiles().ToArray();

        internal static string[] DetectSupportedShellTypesAtPaths(
            string sessionHostPath,
            string commandPromptPath,
            string windowsPowerShellPath,
            string powerShell7Path,
            string wslPath)
        {
            if (string.IsNullOrWhiteSpace(sessionHostPath) || !File.Exists(sessionHostPath))
                return Array.Empty<string>();

            var result = new List<string>(4);
            if (!string.IsNullOrWhiteSpace(commandPromptPath) && File.Exists(commandPromptPath))
                result.Add(CommandPromptShellType);
            if (!string.IsNullOrWhiteSpace(windowsPowerShellPath) && File.Exists(windowsPowerShellPath))
                result.Add(WindowsPowerShellShellType);
            if (!string.IsNullOrWhiteSpace(powerShell7Path) && File.Exists(powerShell7Path))
                result.Add(PowerShell7ShellType);
            if (!string.IsNullOrWhiteSpace(wslPath) && File.Exists(wslPath))
                result.Add(WslShellType);
            return result.ToArray();
        }

        internal static string[] BuildSessionHostArguments(
            string shellType,
            string shellPath,
            string workingDirectory,
            Guid sessionId)
        {
            ValidateShellType(shellType);
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            var commandLine = QuoteWindowsArgument(shellPath) +
                (string.Equals(shellType, WindowsPowerShellShellType, StringComparison.Ordinal) ||
                 string.Equals(shellType, PowerShell7ShellType, StringComparison.Ordinal)
                    ? " -NoLogo -NoProfile"
                    : string.Empty);
            return new[]
            {
                "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(commandLine)),
                "--managed-shell", shellType,
                "--managed-session-id", sessionId.ToString("D"),
                "--working-directory-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory)),
                "--registration-timeout-ms", DefaultRegistrationTimeoutMilliseconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        internal static string[] BuildSessionHostArgumentsForProfile(
            TerminalCommandProfile profile,
            string workingDirectory,
            Guid sessionId)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            ValidateManagedShellType(profile.ManagedShell);
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            var commandLine = BuildCommandLine(profile);
            return new[]
            {
                "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(commandLine)),
                "--managed-shell", profile.ManagedShell,
                "--managed-session-id", sessionId.ToString("D"),
                "--working-directory-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory)),
                "--registration-timeout-ms", DefaultRegistrationTimeoutMilliseconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        internal static void ValidateShellType(string shellType)
        {
            if (!string.Equals(shellType, CommandPromptShellType, StringComparison.Ordinal) &&
                !string.Equals(shellType, WindowsPowerShellShellType, StringComparison.Ordinal) &&
                !string.Equals(shellType, PowerShell7ShellType, StringComparison.Ordinal) &&
                !string.Equals(shellType, WslShellType, StringComparison.Ordinal))
            {
                throw new FileOperationException(
                    "SHELL_NOT_ALLOWED",
                    "Only advertised managed shell types are supported.");
            }
        }

        internal static bool IsBuiltInShellType(string shellType) =>
            string.Equals(shellType, CommandPromptShellType, StringComparison.Ordinal) ||
            string.Equals(shellType, WindowsPowerShellShellType, StringComparison.Ordinal) ||
            string.Equals(shellType, PowerShell7ShellType, StringComparison.Ordinal) ||
            string.Equals(shellType, WslShellType, StringComparison.Ordinal);

        private static void ValidateManagedShellType(string shellType)
        {
            if (string.Equals(shellType, CustomShellType, StringComparison.Ordinal)) return;
            ValidateShellType(shellType);
        }

        private TerminalCommandProfile ResolveCommand(string commandId)
        {
            var profile = AvailableProfiles().FirstOrDefault(item =>
                string.Equals(item.Id, commandId, StringComparison.Ordinal));
            if (profile != null) return profile;
            throw new FileOperationException(
                "SHELL_NOT_ALLOWED",
                "Only terminal commands currently advertised by this PC are supported.");
        }

        private IReadOnlyList<TerminalCommandProfile> AvailableProfiles()
        {
            if (!File.Exists(sessionHostPath)) return Array.Empty<TerminalCommandProfile>();
            var configured = TerminalCommandProfileStore.LoadConfiguredProfiles();
            var overrides = configured
                .Where(profile => string.Equals(profile.Source, "detected", StringComparison.Ordinal))
                .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
            var result = new List<TerminalCommandProfile>(24);
            AddBuiltIn(result, overrides, CommandPromptShellType, "Command Prompt", commandPromptPath, string.Empty);
            AddBuiltIn(result, overrides, WindowsPowerShellShellType, "Windows PowerShell", windowsPowerShellPath, "-NoLogo -NoProfile");
            AddBuiltIn(result, overrides, PowerShell7ShellType, "PowerShell 7", powerShell7Path, "-NoLogo -NoProfile");
            AddBuiltIn(result, overrides, WslShellType, "WSL", wslPath, string.Empty);
            foreach (var profile in configured.Where(profile =>
                string.Equals(profile.Source, "manual", StringComparison.Ordinal)))
            {
                if (profile.Enabled && File.Exists(profile.ExecutablePath)) result.Add(profile);
            }
            return result;
        }

        private static void AddBuiltIn(
            ICollection<TerminalCommandProfile> result,
            IReadOnlyDictionary<string, TerminalCommandProfile> overrides,
            string id,
            string displayName,
            string executablePath,
            string arguments)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return;
            if (overrides.TryGetValue(id, out var configured))
            {
                if (configured.Enabled && File.Exists(configured.ExecutablePath)) result.Add(configured);
                return;
            }
            result.Add(new TerminalCommandProfile
            {
                Id = id,
                DisplayName = displayName,
                ExecutablePath = executablePath,
                Arguments = arguments,
                ManagedShell = id,
                Source = "detected",
                Enabled = true,
                RunAsAdministrator = true
            });
        }

        private static string BuildCommandLine(TerminalCommandProfile profile)
        {
            var arguments = string.IsNullOrWhiteSpace(profile.Arguments)
                ? string.Empty
                : " " + profile.Arguments;
            var extension = Path.GetExtension(profile.ExecutablePath);
            if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                var commandPrompt = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrWhiteSpace(commandPrompt))
                    commandPrompt = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                return QuoteWindowsArgument(commandPrompt) + " /D /S /C \"" +
                    QuoteWindowsArgument(profile.ExecutablePath) + arguments + "\"";
            }
            return QuoteWindowsArgument(profile.ExecutablePath) + arguments;
        }

        private static string NormalizeOptionalExecutablePath(string path) =>
            string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

        private static string ResolvePreferredExecutable(string preferredPath, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
                return preferredPath;
            return FindExecutableOnPath(fileName);
        }

        private static string FindExecutableOnPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    var candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (Exception error) when (
                    error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
                {
                    // A malformed PATH entry does not hide executables in later entries.
                }
            }
            return null;
        }

        private static string ResolveWindowsTerminal()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(alias)) return alias;
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    var candidate = Path.Combine(directory.Trim(), "wt.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception error) when (
                    error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
                {
                }
            }
            return null;
        }

        private static string QuoteWindowsArgument(string value) =>
            "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
