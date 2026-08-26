using TermLink.Platform;

namespace TermLink.Platform.Unix;

public sealed class PosixPathPolicy
{
    private readonly string[] allowedRoots;

    public PosixPathPolicy(IEnumerable<string>? allowedRoots = null)
    {
        var roots = (allowedRoots ?? [Path.DirectorySeparatorChar.ToString()])
            .Select(NormalizeRoot)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0) throw new ArgumentException("At least one file root is required.", nameof(allowedRoots));
        this.allowedRoots = roots;
    }

    public string ResolveExisting(string path, bool requireDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathRooted(path))
            throw new InvalidDataException("An absolute POSIX path is required.");
        var fullPath = Path.GetFullPath(path);
        FileSystemInfo info = requireDirectory ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("The requested path does not exist.", fullPath);
        var resolved = info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
        resolved = Path.GetFullPath(resolved);
        if (!allowedRoots.Any(root => IsWithinRoot(root, resolved)))
            throw new UnauthorizedAccessException("The requested path is outside the configured file roots.");
        return resolved;
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.IndexOf('\0') >= 0 || !Path.IsPathRooted(root))
            throw new ArgumentException("File roots must be absolute POSIX paths.", nameof(root));
        var path = Path.GetFullPath(root);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        return new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        if (root == Path.DirectorySeparatorChar.ToString()) return candidate.StartsWith(root, StringComparison.Ordinal);
        return string.Equals(root, candidate, StringComparison.Ordinal) ||
            candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }
}

public sealed class PosixFileSystemOperations : IFileSystemOperations
{
    private readonly PosixPathPolicy policy;

    public PosixFileSystemOperations(PosixPathPolicy? policy = null) =>
        this.policy = policy ?? new PosixPathPolicy();

    public ValueTask<IReadOnlyList<FileSystemEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = policy.ResolveExisting(path, true);
        var entries = new List<FileSystemEntry>();
        foreach (var entry in new DirectoryInfo(resolved).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                var size = isDirectory ? 0 : ((FileInfo)entry).Length;
                entries.Add(new FileSystemEntry(
                    entry.Name,
                    entry.FullName,
                    isDirectory,
                    size,
                    entry.LastWriteTimeUtc));
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // A concurrent rename is not a failure of the whole directory listing.
            }
        }
        return ValueTask.FromResult<IReadOnlyList<FileSystemEntry>>(entries
            .OrderByDescending(value => value.IsDirectory)
            .ThenBy(value => value.Name, StringComparer.Ordinal)
            .ToArray());
    }

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = policy.ResolveExisting(path, false);
        Stream stream = new FileStream(
            resolved,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }
}
