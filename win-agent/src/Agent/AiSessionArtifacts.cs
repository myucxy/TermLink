using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TermLink.Agent;

internal sealed partial class AiAgentSession
{
    private static readonly HashSet<string> ArtifactSkipDirectories = new(StringComparer.OrdinalIgnoreCase) { ".git", ".termlink", "node_modules", "bin", "obj", ".gradle", ".venv" };

    internal object Artifacts(string query)
    {
        if (query == null || query.Length > 200) throw new AiOperationException("INVALID_SEARCH");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        CheckAttachmentPath(root);
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0));
        var files = new List<object>(); var visited = 0; var scanned = 0; var truncatedFiles = false;
        while (queue.Count > 0 && visited++ < 128 && files.Count < 200 && scanned < 4096) {
            var current = queue.Dequeue();
            try {
                CheckAttachmentPath(current.Path);
                foreach (var path in Directory.EnumerateFileSystemEntries(current.Path)) {
                    if (++scanned > 4096 || files.Count >= 200) { truncatedFiles = true; break; }
                    var attr = File.GetAttributes(path);
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attr & FileAttributes.Directory) != 0) {
                        if (!ArtifactSkipDirectories.Contains(Path.GetFileName(path))) {
                            if (current.Depth < 3 && queue.Count < 128) queue.Enqueue((path, current.Depth + 1));
                            else truncatedFiles = true;
                        }
                    } else {
                        var relative = Path.GetRelativePath(root, path);
                        if (query.Length > 0 && !relative.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        using var input = OpenArtifact(relative);
                        var modified = File.GetLastWriteTimeUtc(input.SafeFileHandle);
                        files.Add(new { id = relative.Replace('\\', '/'), name = Path.GetFileName(path), size = input.Length,
                            mime = ArtifactMime(Path.GetExtension(path)), version = ArtifactVersion(input), modifiedAtMs = new DateTimeOffset(modified).ToUnixTimeMilliseconds() });
                    }
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or AiOperationException) { truncatedFiles = true; }
        }
        return new { files = files.ToArray(), truncated = truncatedFiles || queue.Count > 0, scope = "workspace" };
    }

    internal object ReadArtifact(string id, long offset, string expectedVersion)
    {
        if (offset < 0) throw new AiOperationException("INVALID_OFFSET");
        using var input = OpenArtifact(id);
        var version = ArtifactVersion(input);
        if (!string.IsNullOrEmpty(expectedVersion) && version != expectedVersion) throw new AiOperationException("ARTIFACT_CHANGED");
        if (input.Length > 50L * 1024 * 1024) throw new AiOperationException("ARTIFACT_TOO_LARGE");
        if (offset > input.Length) throw new AiOperationException("INVALID_OFFSET");
        input.Position = offset;
        var bytes = new byte[(int)Math.Min(64 * 1024, input.Length - offset)]; input.ReadExactly(bytes);
        return new { id, name = Path.GetFileName(id), mime = ArtifactMime(Path.GetExtension(id)), version, size = input.Length, offset,
            nextOffset = offset + bytes.Length, data = Convert.ToBase64String(bytes) };
    }

    private FileStream OpenArtifact(string id)
    {
        if (id == null || id.Length is < 1 or > 2048 || id.Any(char.IsControl) || Path.IsPathRooted(id) || id.Contains(':'))
            throw new AiOperationException("INVALID_ARTIFACT");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, id));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ArtifactSkipDirectories.Contains))
            throw new AiOperationException("ARTIFACT_OUTSIDE_WORKSPACE");
        CheckAttachmentPath(path);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Validate the opened handle as well as the requested path: a directory could have
        // been replaced with a junction between validation and FileStream.Open.
        try {
            var finalPath = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(input.SafeFileHandle, finalPath, (uint)finalPath.Capacity, 0);
            if (length == 0 || length >= finalPath.Capacity || !finalPath.ToString().StartsWith(@"\\?\" + root, StringComparison.OrdinalIgnoreCase) ||
                finalPath.ToString()[(4 + root.Length)..].Split('\\', '/').Any(ArtifactSkipDirectories.Contains))
                throw new AiOperationException("ARTIFACT_OUTSIDE_WORKSPACE");
            return input;
        } catch { input.Dispose(); throw; }
    }

    private static string ArtifactVersion(FileStream file) => file.Length + ":" + File.GetLastWriteTimeUtc(file.SafeFileHandle).Ticks;
    private static string ArtifactMime(string extension) => extension.ToLowerInvariant() switch {
        ".html" or ".htm" => "text/html", ".md" or ".markdown" => "text/markdown", ".svg" => "image/svg+xml",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        ".pdf" => "application/pdf", ".json" => "application/json", ".txt" or ".log" or ".css" or ".js" or ".ts" or ".tsx" or ".jsx" or ".cs" or ".py" or ".go" or ".rs" or ".xml" or ".yaml" or ".yml" => "text/plain",
        _ => "application/octet-stream"
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint count, uint flags);
}
