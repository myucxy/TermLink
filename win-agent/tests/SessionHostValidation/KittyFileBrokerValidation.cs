using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TermLink.SessionHost;

internal static class KittyFileBrokerValidation
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "termlink-kitty-broker-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "termlink-kitty-outside-" + Guid.NewGuid().ToString("N"));
        var symlink = Path.Combine(directory, "symlink.bin");
        var redirectedDirectory = Path.Combine(directory, "redir");
        var symlinkCreated = false;
        var redirectedDirectoryCreated = false;
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(directory, "image.bin"), "0123456789");
            File.WriteAllText(Path.Combine(outside, "secret.bin"), "secret");
            using var root = OpenDirectory(directory);
            using var broker = new KittyFileBroker(root);
            root.Dispose(); // The broker retains its own root handle.

            using (var capture = broker.Capture("image.bin", 2, 4, false))
                Check(System.Text.Encoding.ASCII.GetString(capture.Bytes.Span) == "2345", "Range capture failed.");
            using (var capture = broker.Capture("image.bin", 8, null, false))
                Check(System.Text.Encoding.ASCII.GetString(capture.Bytes.Span) == "89", "EOF capture failed.");
            using (var capture = broker.Capture("image.bin", 10, 0, false))
                Check(capture.Bytes.Length == 0, "Empty EOF range failed.");

            foreach (var path in new[] { "..\\secret.bin", ".\\image.bin", "C:\\secret.bin",
                         "\\\\server\\share\\secret.bin", "\\?\\C:\\secret.bin", "image.bin:stream",
                         "image.bin/child", "sub\\..\\image.bin", "image.bin\\", "image.bin\0tail" })
                Throws<ArgumentException>(() => broker.Capture(path, 0, null, false), "Unsafe path accepted.");
            Throws<IOException>(() => broker.Capture("image.bin", ulong.MaxValue, 1, false), "Offset overflow accepted.");
            Throws<IOException>(() => broker.Capture("image.bin", 9, 2, false), "Past-EOF range accepted.");
            Throws<IOException>(() => broker.Capture("missing.bin", 0, null, false), "Missing file accepted.");

            File.WriteAllBytes(Path.Combine(directory, "large.bin"), new byte[KittyFileBroker.MaxCaptureBytes + 1]);
            Throws<IOException>(() => broker.Capture("large.bin", 0, null, false), "Oversized capture accepted.");
            using (var capture = broker.Capture("large.bin", 1, KittyFileBroker.MaxCaptureBytes, false))
                Check(capture.Bytes.Length == KittyFileBroker.MaxCaptureBytes, "Exact capture limit failed.");

            Check(CreateHardLink(Path.Combine(directory, "link.bin"), Path.Combine(outside, "secret.bin"), IntPtr.Zero),
                "Hard-link test setup failed.");
            Throws<IOException>(() => broker.Capture("link.bin", 0, null, false), "Hard link accepted.");

            var canCreateSymlink = true;
            try
            {
                File.CreateSymbolicLink(symlink, Path.Combine(outside, "secret.bin"));
                symlinkCreated = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                canCreateSymlink = false;
                Console.WriteLine("Kitty broker symlink check skipped: test user cannot create symlinks.");
            }
            if (canCreateSymlink)
            {
                Throws<IOException>(() => broker.Capture("symlink.bin", 0, null, false), "File reparse accepted.");
                Directory.CreateSymbolicLink(redirectedDirectory, outside);
                redirectedDirectoryCreated = true;
                Throws<IOException>(() => broker.Capture("redir\\secret.bin", 0, null, false), "Intermediate reparse accepted.");
                using var redirectedRoot = OpenDirectory(redirectedDirectory);
                Throws<ArgumentException>(() => new KittyFileBroker(redirectedRoot), "Reparse root accepted.");
            }

            File.WriteAllText(Path.Combine(directory, "temp.bin"), "capture");
            using (var capture = broker.Capture("temp.bin", 0, null, true))
            {
                ThrowsAccessFailure(() => File.WriteAllText(Path.Combine(directory, "temp.bin"), "replace"),
                    "Captured temporary file remained writable.");
                ThrowsAccessFailure(() => File.Move(Path.Combine(directory, "temp.bin"), Path.Combine(directory, "moved.bin")),
                    "Captured temporary file could be renamed.");
                Check(File.Exists(Path.Combine(directory, "temp.bin")), "Temporary file was deleted before commit.");
                Check(capture.DeleteAfterDurableApply(), "Same-handle temporary deletion failed.");
            }
            Check(!File.Exists(Path.Combine(directory, "temp.bin")), "Temporary file survived disposition close.");

            File.WriteAllText(Path.Combine(directory, "retained.bin"), "capture");
            using (broker.Capture("retained.bin", 0, null, true)) { }
            Check(File.Exists(Path.Combine(directory, "retained.bin")), "Uncommitted temporary file was deleted.");
            Console.WriteLine("Kitty file broker: bounded range, escape/reparse/hard-link denial, and same-handle retention/delete passed.");
        }
        finally
        {
            // Remove links directly before recursion; never recurse through a reparse target.
            if (redirectedDirectoryCreated)
            {
                Check((File.GetAttributes(redirectedDirectory) & FileAttributes.ReparsePoint) != 0,
                    "Test directory link changed before cleanup.");
                Directory.Delete(redirectedDirectory);
            }
            if (symlinkCreated)
            {
                Check((File.GetAttributes(symlink) & FileAttributes.ReparsePoint) != 0,
                    "Test file link changed before cleanup.");
                File.Delete(symlink);
            }
            DeleteTestDirectory(directory, "termlink-kitty-broker-");
            DeleteTestDirectory(outside, "termlink-kitty-outside-");
        }
    }

    private static void DeleteTestDirectory(string path, string prefix)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to clean an unexpected test directory.");
        Directory.Delete(full, recursive: true);
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFile(path, 0x80000000, 1 | 2 | 4, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Test root handle could not be opened.");
        return handle;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void ThrowsAccessFailure(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(message);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
