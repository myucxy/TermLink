using System;
using System.IO;

namespace TermLink.UpdateCore;

// User-created files are never part of an installer payload or disposable backup.
public static class AiWorkspaceStorage
{
    public const string DirectoryName = "AiWorkspaces";
    public static string Under(string installRoot) => Path.Combine(Path.GetFullPath(installRoot), DirectoryName);

    // Only this reserved direct-child namespace may be created by remote CLI launch.
    // Existing/custom directories still pass through the normal file-operation validator.
    public static void EnsureCliDirectory(string installRoot, string requestedPath)
    {
        var root = Under(installRoot);
        var path = Path.GetFullPath(requestedPath);
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\Acli_[0-9]{8}_[0-9]{6}_[0-9]{3}_[a-f0-9]{32}\z")) return;
        // Reject links at every existing ancestor, including the installation directory.
        for (var parent = new DirectoryInfo(path); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("CLI workspace must not pass through a directory link.");
        Directory.CreateDirectory(path); // Idempotent for an operation replay.
    }

    public static void MoveBetweenInstallRoots(string sourceRoot, string destinationRoot)
    {
        var source = Under(sourceRoot);
        if (!Directory.Exists(source)) return;
        var destination = Under(destinationRoot);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("AI workspace folders conflict; both copies have been preserved.");
        // Move the directory entry without following any user-created links inside it.
        Directory.Move(source, destination);
    }

    public static string PreserveBeforeUninstall(string installRoot)
    {
        var source = Under(installRoot);
        if (!Directory.Exists(source)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var destination = Path.Combine(Directory.GetParent(root).FullName,
            Path.GetFileName(root) + "-AiWorkspaces-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(source, destination);
        return destination;
    }
}
