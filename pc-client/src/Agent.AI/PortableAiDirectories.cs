// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TermLink.Agent;
using TermLink.Agent.Connection;
using TermLink.Agent.Files;
using TermLink.UpdateCore;

namespace TermLink.Agent.AI;

/// <summary>Private CLI workspaces and the existing no-follow POSIX listing contract.</summary>
internal sealed class PortableAiDirectories : IDisposable
{
    private const int ReadDirectory = 0x100000 | 0x100 | 0x1000000; // macOS O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
    private const uint PrivateMode = 0x1c0; // 0700
    private readonly string accountDirectory;
    private readonly Lazy<PosixRemoteFileHost> files;
    internal string WorkspaceRoot { get; }

    internal PortableAiDirectories(string accountDirectory)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        this.accountDirectory = Path.GetFullPath(accountDirectory);
        WorkspaceRoot = AiWorkspaceStorage.Under(this.accountDirectory);
        using var account = OpenDirectory(this.accountDirectory);
        using var root = OpenPrivateChild(account, "AiWorkspaces", create: true);
        files = new(() => new PosixRemoteFileHost(Path.Combine(this.accountDirectory, "ai-directory-cache")));
    }

    internal async Task<DirectoryListingResult> ListAsync(string path, long offset, bool directoriesOnly, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var page = await files.Value.ListAsync(path, offset, "", token).ConfigureAwait(false);
            return MapPage(page, directoriesOnly);
        }
        catch (RelayFileException)
        {
            // Never return native errors or local path details to the phone.
            throw new FileOperationException("DIRECTORY_BROWSE_FAILED", "The requested directory could not be listed.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new FileOperationException("DIRECTORY_BROWSE_FAILED", "The requested directory could not be listed.");
        }
    }

    internal static DirectoryListingResult MapPage(JsonElement page, bool directoriesOnly) => new()
    {
        CurrentPath = page.GetProperty("current_path").GetString(),
        ParentPath = page.GetProperty("parent_path").GetString(),
        Entries = page.GetProperty("entries").EnumerateArray()
            .Where(entry => !directoriesOnly || entry.GetProperty("kind").GetString() == "directory")
            .Select(entry => new FileOperationEntry {
                Name = entry.GetProperty("name").GetString(), Path = entry.GetProperty("path").GetString(),
                Kind = entry.GetProperty("kind").GetString(),
                SizeBytes = entry.GetProperty("size_bytes").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("size_bytes").GetInt64(),
                ModifiedAt = entry.GetProperty("modified_at").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("modified_at").GetDateTimeOffset(),
                Attributes = entry.GetProperty("attributes").EnumerateArray().Select(value => value.GetString()).ToArray()
            }).ToArray(),
        // The offset belongs to the unfiltered native page. Empty directory-only
        // pages must retain it so files cannot hide subsequent directories.
        NextOffset = page.GetProperty("next_offset").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next_offset").GetInt64()
    };

    internal string CreateCliWorkspace()
    {
        try
        {
            using var account = OpenDirectory(accountDirectory);
            using var root = OpenPrivateChild(account, "AiWorkspaces", create: true);
            var name = "cli-" + Guid.NewGuid().ToString("N");
            // mkdirat creates only a new child of the already-open root; a
            // pre-existing name, symlink or replaced ancestor is never followed.
            if (MkdirAt(root, name, PrivateMode) != 0) throw new IOException();
            using var created = OpenPrivateChild(root, name, create: false);
            return Path.Combine(WorkspaceRoot, name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AiOperationException("AI_WORKSPACE_CREATE_FAILED");
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOf('\0') >= 0) throw new IOException();
        // These Apple system aliases are accepted by the shared POSIX file host.
        if (path == "/var" || path.StartsWith("/var/", StringComparison.Ordinal) ||
            path == "/tmp" || path.StartsWith("/tmp/", StringComparison.Ordinal) ||
            path == "/etc" || path.StartsWith("/etc/", StringComparison.Ordinal)) path = "/private" + path;
        var directory = Open("/", ReadDirectory);
        try
        {
            if (directory.IsInvalid) throw new IOException();
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenAt(directory, part, ReadDirectory);
                if (next.IsInvalid) { next.Dispose(); throw new IOException(); }
                directory.Dispose(); directory = next;
            }
            return directory;
        }
        catch { directory.Dispose(); throw; }
    }

    private static SafeFileHandle OpenPrivateChild(SafeFileHandle parent, string name, bool create)
    {
        // EEXIST is safe only after opening the existing child with O_NOFOLLOW.
        if (create && MkdirAt(parent, name, PrivateMode) != 0 && Marshal.GetLastPInvokeError() != 17) throw new IOException();
        var directory = OpenAt(parent, name, ReadDirectory);
        if (directory.IsInvalid || Chmod(directory, PrivateMode) != 0) { directory.Dispose(); throw new IOException(); }
        return directory;
    }

    public void Dispose()
    {
        if (files.IsValueCreated) files.Value.DisposeAsync().GetAwaiter().GetResult();
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern SafeFileHandle Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "openat", SetLastError = true)]
    private static extern SafeFileHandle OpenAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Chmod(SafeFileHandle directory, uint mode);
}
