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
using TermLink.LocalIpc;

namespace TermLink.Agent
{
    internal sealed class FileTransferArtifact : IAsyncDisposable
    {
        private readonly Stream stream;
        private readonly string temporaryPath;
        private readonly bool preserveTemporaryPath;
        private int opened;
        private int disposed;

        internal FileTransferArtifact(
            string filename,
            bool directoryZip,
            long sizeBytes,
            byte[] sha256,
            Stream stream,
            string temporaryPath,
            bool preserveTemporaryPath = false)
        {
            Filename = filename;
            DirectoryZip = directoryZip;
            SizeBytes = sizeBytes;
            Sha256 = sha256;
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.temporaryPath = temporaryPath;
            this.preserveTemporaryPath = preserveTemporaryPath;
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
            if (string.IsNullOrEmpty(temporaryPath) || preserveTemporaryPath) return;
            try { File.Delete(temporaryPath); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            try
            {
                var parent = Path.GetDirectoryName(temporaryPath);
                if (!string.IsNullOrEmpty(parent)) Directory.Delete(parent, false);
            }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
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
        internal const string CacheDirectoryName = "FileTransfers";
        internal static readonly TimeSpan OrphanRetention = TimeSpan.FromHours(26);
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
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TermLink",
                    "Cache",
                    CacheDirectoryName))
        {
            CleanupExpired(DateTime.UtcNow - OrphanRetention);
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
            if (Directory.Exists(path))
                return await PrepareDirectoryAsync(path, Guid.NewGuid(), false, token).ConfigureAwait(false);
            return await PrepareFileAsync(path, token).ConfigureAwait(false);
        }

        internal async Task<FileTransferArtifact> PrepareAsync(
            string requestedPath,
            Guid transferId,
            CancellationToken token)
        {
            if (transferId == Guid.Empty)
                throw new ArgumentException("A transfer ID is required.", nameof(transferId));
            var path = paths.NormalizeExistingFileOrDirectory(requestedPath);
            if (Directory.Exists(path))
                return await PrepareDirectoryAsync(path, transferId, true, token).ConfigureAwait(false);
            return await PrepareFileAsync(path, token).ConfigureAwait(false);
        }

        internal string TransferDirectory(Guid transferId)
        {
            if (transferId == Guid.Empty)
                throw new ArgumentException("A transfer ID is required.", nameof(transferId));
            var candidate = Path.GetFullPath(Path.Combine(temporaryRoot, transferId.ToString("N")));
            EnsureWithinCache(candidate);
            return candidate;
        }

        internal void Cleanup(Guid transferId)
        {
            var directory = TransferDirectory(transferId);
            if (!Directory.Exists(directory)) return;
            EnsureOrdinaryDirectory(directory);
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    (!string.Equals(entry.Name, "payload.zip", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(entry.Name, "payload.zip.tmp", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        "File-transfer cache contains an unexpected or unsafe entry.");
                }
                entry.Delete();
            }
            Directory.Delete(directory, false);
        }

        internal void CleanupExpired(DateTime notModifiedSinceUtc)
        {
            if (notModifiedSinceUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Cache cleanup cutoff must be UTC.", nameof(notModifiedSinceUtc));
            if (!Directory.Exists(temporaryRoot)) return;
            EnsureOrdinaryDirectory(temporaryRoot);
            foreach (var candidate in new DirectoryInfo(temporaryRoot).EnumerateDirectories())
            {
                if (!Guid.TryParseExact(candidate.Name, "N", out var transferId) ||
                    (candidate.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                DateTime newestWrite;
                try
                {
                    newestWrite = candidate.EnumerateFileSystemInfos()
                        .Select(entry => entry.LastWriteTimeUtc)
                        .Append(candidate.LastWriteTimeUtc)
                        .Max();
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    continue;
                }
                if (newestWrite > notModifiedSinceUtc) continue;
                try { Cleanup(transferId); }
                catch (Exception error) when (
                    error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)
                {
                    // An unexpected or concurrently active entry is never
                    // recursively removed by orphan collection.
                }
            }
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

        private async Task<FileTransferArtifact> PrepareDirectoryAsync(
            string path,
            Guid transferId,
            bool preserveForResume,
            CancellationToken token)
        {
            var rootAttributes = File.GetAttributes(path);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
                throw new FileOperationException(
                    "REPARSE_DIRECTORY_UNSUPPORTED",
                    "A reparse-point directory cannot be downloaded as an archive.");

            var transferDirectory = TransferDirectory(transferId);
            Directory.CreateDirectory(transferDirectory);
            EnsureOrdinaryDirectory(transferDirectory);
            var temporaryPath = Path.Combine(transferDirectory, "payload.zip");
            var stagingPath = Path.Combine(transferDirectory, "payload.zip.tmp");
            try
            {
                if (!File.Exists(temporaryPath))
                {
                    try { File.Delete(stagingPath); }
                    catch (FileNotFoundException) { }
                    await WriteDirectoryZipAsync(path, stagingPath, token).ConfigureAwait(false);
                    File.Move(stagingPath, temporaryPath, false);
                }
                EnsureOrdinaryFile(temporaryPath);
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
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
                        temporaryPath,
                        preserveForResume);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                try { File.Delete(stagingPath); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (!preserveForResume)
                {
                    try { Cleanup(transferId); }
                    catch (DirectoryNotFoundException) { }
                }
                throw;
            }
        }

        private void EnsureWithinCache(string candidate)
        {
            var root = temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("File-transfer cache path escaped its configured root.");
        }

        private static void EnsureOrdinaryDirectory(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "File-transfer cache directory is not an ordinary directory.");
            }
        }

        private static void EnsureOrdinaryFile(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException(
                    "File-transfer cache payload is not an ordinary file.");
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
        internal const string EnhancedPowerShell7CommandId = "powershell-7-enhanced";
        internal const string WslShellType = "wsl";
        internal const string CustomShellType = "custom";
        internal const int DefaultRegistrationTimeoutMilliseconds = 30000;
        // The always-present TermLink CMD profile owns closeOnExit; the explicit command below replaces its shell.
        internal const string WindowsTerminalHostProfileId = "{cb3ce4a8-86da-5aa1-a77d-abe62f8b55b2}";
        private readonly WindowsPathService paths;
        private readonly IProcessStarter processes;
        private readonly Func<string> windowsTerminalResolver;
        private readonly string sessionHostPath;
        private readonly string commandPromptPath;
        private readonly string windowsPowerShellPath;
        private readonly string powerShell7Path;
        private readonly string wslPath;
        private readonly string registryControlPipeName;
        private readonly string nativeDisplayExecutable;

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
                    "wsl.exe"),
                null,
                Path.Combine(AppContext.BaseDirectory, "TermLink.exe"))
        {
        }

        internal ManagedTerminalLauncher(string registryControlPipeName, string nativeDisplayExecutable = null)
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
                    "wsl.exe"),
                registryControlPipeName,
                nativeDisplayExecutable ?? Path.Combine(AppContext.BaseDirectory, "TermLink.exe"))
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
            string wslPath = null,
            string registryControlPipeName = null,
            string nativeDisplayExecutable = null)
        {
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.processes = processes ?? throw new ArgumentNullException(nameof(processes));
            this.windowsTerminalResolver = windowsTerminalResolver ?? throw new ArgumentNullException(nameof(windowsTerminalResolver));
            this.sessionHostPath = Path.GetFullPath(sessionHostPath ?? throw new ArgumentNullException(nameof(sessionHostPath)));
            this.commandPromptPath = Path.GetFullPath(commandPromptPath ?? throw new ArgumentNullException(nameof(commandPromptPath)));
            this.windowsPowerShellPath = Path.GetFullPath(windowsPowerShellPath ?? throw new ArgumentNullException(nameof(windowsPowerShellPath)));
            this.powerShell7Path = NormalizeOptionalExecutablePath(powerShell7Path);
            this.wslPath = NormalizeOptionalExecutablePath(wslPath);
            this.registryControlPipeName = string.IsNullOrWhiteSpace(registryControlPipeName)
                ? TermLinkControlPipe.DefaultName()
                : registryControlPipeName.Trim();
            this.nativeDisplayExecutable = nativeDisplayExecutable == null ? null : Path.GetFullPath(nativeDisplayExecutable);
            if (this.registryControlPipeName.Length > 200 ||
                this.registryControlPipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new ArgumentException("registry control pipe name is invalid.", nameof(registryControlPipeName));
            }
        }

        internal Guid Launch(string shellType, string workingDirectory, Guid? requestedSessionId = null)
        {
            var directory = paths.NormalizeExistingDirectory(workingDirectory);
            var profile = ResolveCommand(shellType);
            var enhanced = string.Equals(profile.Id, EnhancedPowerShell7CommandId, StringComparison.Ordinal);
            if (!File.Exists(sessionHostPath))
                throw new FileOperationException("SESSION_HOST_NOT_FOUND", "TermLink.SessionHost.exe is not installed.");
            if (!File.Exists(profile.ExecutablePath))
                throw new FileOperationException("SHELL_NOT_FOUND", "The requested Windows shell is not installed.");
            if (enhanced && (nativeDisplayExecutable == null || !File.Exists(nativeDisplayExecutable)))
                throw new FileOperationException("NATIVE_DISPLAY_NOT_FOUND", "TermLink.exe is not installed for the enhanced terminal.");

            var sessionId = requestedSessionId.GetValueOrDefault(Guid.NewGuid());
            if (sessionId == Guid.Empty)
                throw new FileOperationException("SESSION_ID_INVALID", "A non-empty managed session ID is required.");
            var arguments = BuildSessionHostArgumentsForProfile(
                profile,
                directory,
                sessionId,
                registryControlPipeName,
                enhanced ? nativeDisplayExecutable : null);
            if (enhanced)
            {
                var nativeStart = new ProcessStartInfo
                {
                    FileName = sessionHostPath,
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                SanitizeTerminalEnvironment(nativeStart);
                foreach (var argument in arguments) nativeStart.ArgumentList.Add(argument);
                try
                {
                    if (processes.TryStart(nativeStart)) return sessionId;
                    throw new FileOperationException("TERMINAL_LAUNCH_FAILED", "The enhanced terminal process could not be started.");
                }
                catch (FileOperationException) { throw; }
                catch (Exception error) when (
                    error is IOException || error is UnauthorizedAccessException ||
                    error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
                {
                    throw new FileOperationException("TERMINAL_LAUNCH_FAILED", "The enhanced terminal process could not be started.", error);
                }
            }
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
                SanitizeTerminalEnvironment(terminalStart);
                terminalStart.ArgumentList.Add("-w");
                terminalStart.ArgumentList.Add("new");
                terminalStart.ArgumentList.Add("new-tab");
                terminalStart.ArgumentList.Add("--profile");
                terminalStart.ArgumentList.Add(WindowsTerminalHostProfileId);
                terminalStart.ArgumentList.Add("--title");
                terminalStart.ArgumentList.Add(profile.DisplayName);
                terminalStart.ArgumentList.Add("--elevate");
                terminalStart.ArgumentList.Add("-d");
                terminalStart.ArgumentList.Add(directory);
                terminalStart.ArgumentList.Add(sessionHostPath);
                foreach (var argument in arguments) terminalStart.ArgumentList.Add(argument);
                try
                {
                    var started = processes.TryStart(terminalStart);
                    if (started) return sessionId;
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
                UseShellExecute = true,
                Verb = "runas"
            };
            foreach (var argument in arguments) directStart.ArgumentList.Add(argument);
            try
            {
                var started = processes.TryStart(directStart);
                if (!started)
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

        internal string NormalizeWorkingDirectory(string workingDirectory)
        {
            TermLink.UpdateCore.AiWorkspaceStorage.EnsureCliDirectory(Path.GetDirectoryName(sessionHostPath), workingDirectory);
            return paths.NormalizeExistingDirectory(workingDirectory);
        }

        /// <summary>
        /// The desktop process may itself have been started from a non-interactive
        /// PowerShell host that advertises TERM=dumb and NO_COLOR. Those values
        /// describe that parent host, not the ConPTY created for a managed TermLink
        /// session. Do not let them disable color in Windows Terminal or Oh My Posh.
        /// </summary>
        internal static void SanitizeTerminalEnvironment(ProcessStartInfo startInfo)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
            startInfo.Environment.Remove("NO_COLOR");
            if (startInfo.Environment.TryGetValue("TERM", out var term) &&
                string.Equals(term?.Trim(), "dumb", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove("TERM");
            }
            if (startInfo.Environment.TryGetValue("COLORTERM", out var colorTerm) &&
                string.IsNullOrWhiteSpace(colorTerm))
            {
                startInfo.Environment.Remove("COLORTERM");
            }
        }

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
            return new ManagedTerminalLauncher().AdvertisedCommands();
        }

        internal TerminalCommandCapability[] AdvertisedCommands() =>
            AvailableProfiles()
                .Select(profile => new TerminalCommandCapability
                {
                    Id = profile.Id,
                    DisplayName = profile.DisplayName,
                    ShellType = profile.ManagedShell,
                    Source = profile.Source
                })
                .ToArray();

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
                    ? " -NoLogo"
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
            Guid sessionId,
            string registryControlPipeName = null,
            string nativeDisplayExecutable = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            ValidateManagedShellType(profile.ManagedShell);
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            var commandLine = BuildCommandLine(profile);
            var arguments = new List<string>
            {
                "--command-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(commandLine)),
                "--managed-shell", profile.ManagedShell,
                "--managed-session-id", sessionId.ToString("D"),
                "--working-directory-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory)),
                "--registration-timeout-ms", DefaultRegistrationTimeoutMilliseconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrWhiteSpace(registryControlPipeName))
            {
                var normalizedPipe = registryControlPipeName.Trim();
                if (normalizedPipe.Length > 200 || normalizedPipe.IndexOfAny(new[] { '\\', '/' }) >= 0)
                    throw new ArgumentException("registry control pipe name is invalid.", nameof(registryControlPipeName));
                arguments.Add("--registry-pipe");
                arguments.Add(normalizedPipe);
            }
            if (nativeDisplayExecutable != null)
            {
                var executable = Path.GetFullPath(nativeDisplayExecutable);
                if (!File.Exists(executable)) throw new ArgumentException("Native display executable does not exist.", nameof(nativeDisplayExecutable));
                arguments.Add("--native-display");
                arguments.Add(executable);
                arguments.Add("--terminal-state");
                arguments.Add(TerminalSemanticProfile.GraphicsUnicode16.StateContract);
                arguments.Add("--broker-captures");
            }
            return arguments.ToArray();
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
            string.Equals(shellType, EnhancedPowerShell7CommandId, StringComparison.Ordinal) ||
            string.Equals(shellType, WslShellType, StringComparison.Ordinal);

        private static void ValidateManagedShellType(string shellType)
        {
            if (string.Equals(shellType, CustomShellType, StringComparison.Ordinal)) return;
            ValidateShellType(shellType);
        }

        private TerminalCommandProfile ResolveCommand(string commandId)
        {
            if (commandId == EnhancedPowerShell7CommandId &&
                (nativeDisplayExecutable == null || !File.Exists(nativeDisplayExecutable)))
                throw new FileOperationException("NATIVE_DISPLAY_NOT_FOUND", "TermLink.exe is not installed for the enhanced terminal.");
            var profile = AvailableProfiles().FirstOrDefault(item =>
                string.Equals(item.Id, commandId, StringComparison.Ordinal));
            if (profile != null) return profile;
            throw new FileOperationException(
                "SHELL_NOT_ALLOWED",
                "Only terminal commands currently advertised by this PC are supported.");
        }

        internal IReadOnlyList<TerminalCommandProfile> AvailableProfiles()
        {
            if (!File.Exists(sessionHostPath)) return Array.Empty<TerminalCommandProfile>();
            var configured = TerminalCommandProfileStore.LoadConfiguredProfiles();
            var overrides = configured
                .Where(profile => string.Equals(profile.Source, "detected", StringComparison.Ordinal))
                .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
            var result = new List<TerminalCommandProfile>(24);
            AddBuiltIn(result, overrides, CommandPromptShellType, "Command Prompt", commandPromptPath, string.Empty);
            AddBuiltIn(result, overrides, WindowsPowerShellShellType, "Windows PowerShell", windowsPowerShellPath, "-NoLogo");
            AddBuiltIn(result, overrides, PowerShell7ShellType, "PowerShell 7", powerShell7Path, "-NoLogo");
            AddBuiltIn(result, overrides, WslShellType, "WSL", wslPath, string.Empty);
            foreach (var profile in configured.Where(profile =>
                string.Equals(profile.Source, "manual", StringComparison.Ordinal)))
            {
                if (profile.Enabled && File.Exists(profile.ExecutablePath)) result.Add(profile);
            }
            var powerShell7 = result.FirstOrDefault(profile => profile.Id == PowerShell7ShellType);
            if (result.Count < 24 && powerShell7 != null && nativeDisplayExecutable != null &&
                File.Exists(nativeDisplayExecutable))
                AddBuiltIn(result, overrides, EnhancedPowerShell7CommandId,
                    "PowerShell 7（增强终端）", powerShell7.ExecutablePath, powerShell7.Arguments,
                    PowerShell7ShellType);
            return result;
        }

        private static void AddBuiltIn(
            ICollection<TerminalCommandProfile> result,
            IReadOnlyDictionary<string, TerminalCommandProfile> overrides,
            string id,
            string displayName,
            string executablePath,
            string arguments,
            string managedShell = null)
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
                ManagedShell = managedShell ?? id,
                Source = "detected",
                Enabled = true,
                RunAsAdministrator = id != EnhancedPowerShell7CommandId
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
