using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

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
        internal const int MaximumQueryBytes = 512;
        internal const int MaximumQueryCodePoints = 128;
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

        internal DirectoryListingResult List(string path, long offset, string query = null, bool directoriesOnly = false)
        {
            if (offset < 0)
                throw new FileOperationException("OFFSET_INVALID", "The directory offset cannot be negative.");
            var normalizedQuery = NormalizeQuery(query);
            return string.IsNullOrWhiteSpace(path)
                ? ListRoots(offset, normalizedQuery)
                : ListDirectory(paths.NormalizeExistingDirectory(path), offset, normalizedQuery, directoriesOnly);
        }

        private DirectoryListingResult ListRoots(long offset, string query)
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
                    .Where(entry => MatchesQuery(entry.Name, query))
                    .ToArray();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new FileOperationException("ROOT_LIST_FAILED", "Windows drive roots could not be enumerated.", error);
            }
            return Page(string.Empty, null, roots, offset);
        }

        private DirectoryListingResult ListDirectory(string path, long offset, string query, bool directoriesOnly)
        {
            FileOperationEntry[] entries;
            try
            {
                entries = new DirectoryInfo(path)
                    .EnumerateFileSystemInfos()
                    .Where(value => !directoriesOnly || (value.Attributes & FileAttributes.Directory) != 0)
                    .Where(value => MatchesQuery(value.Name, query))
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

        private static string NormalizeQuery(string query)
        {
            if (string.IsNullOrEmpty(query)) return string.Empty;
            if (query.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(query) > MaximumQueryBytes ||
                query.EnumerateRunes().Count() > MaximumQueryCodePoints)
            {
                throw new FileOperationException("QUERY_INVALID", "The directory search query is invalid.");
            }
            return query;
        }

        private static bool MatchesQuery(string name, string query) =>
            query.Length == 0 || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

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

}
