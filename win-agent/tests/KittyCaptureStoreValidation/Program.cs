using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TermLink.Common;

if (args.Length == 4 && args[0] == "--child-put")
{
    try
    {
        using var child = new KittyCaptureStore(args[1], Guid.Parse(args[2]));
        child.Put(Guid.Parse(args[3]), 14, "x"u8);
        return;
    }
    catch (IOException) { Environment.ExitCode = 2; return; }
}

var root = Path.Combine(Path.GetTempPath(), "termlink-kitty-store-" + Guid.NewGuid().ToString("N"));
var outside = Path.Combine(Path.GetTempPath(), "termlink-kitty-outside-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Directory.CreateDirectory(outside);
var session = Guid.NewGuid();
var objectId = Guid.NewGuid();
try
{
    KittyCaptureStore.CaptureReceipt receipt;
    using (var store = new KittyCaptureStore(root, session))
    {
        receipt = store.Put(objectId, 12, "pixels"u8);
        Check(store.ReadExact(session, 12, objectId, receipt.Length, receipt.Sha256)
            .AsSpan().SequenceEqual("pixels"u8), "Roundtrip failed.");
        var repeated = store.Put(objectId, 12, "pixels"u8);
        Check(repeated.Length == receipt.Length && repeated.Sha256.SequenceEqual(receipt.Sha256),
            "Idempotent Put changed identity.");
        Throws<InvalidDataException>(() => store.Put(objectId, 12, "different"u8), "Conflict overwritten.");
        Throws<InvalidDataException>(() => store.ReadExact(Guid.NewGuid(), 12, objectId,
            receipt.Length, receipt.Sha256), "Cross-session read accepted.");
        Throws<InvalidDataException>(() => store.ReadExact(session, 13, objectId,
            receipt.Length, receipt.Sha256), "Wrong event sequence accepted.");
        Throws<InvalidDataException>(() => store.ReadExact(session, 12, objectId,
            receipt.Length, new byte[32]), "Wrong digest accepted.");
    }
    using (var restart = new KittyCaptureStore(root, session))
    {
        Check(restart.ReadExact(session, 12, objectId, receipt.Length, receipt.Sha256)
            .AsSpan().SequenceEqual("pixels"u8), "Restart lost capture.");
        var file = Path.Combine(root, session.ToString("N"), objectId.ToString("N") + ".capture");
        var encrypted = File.ReadAllBytes(file);
        encrypted[^1] ^= 1;
        File.WriteAllBytes(file, encrypted);
        Throws<InvalidDataException>(() => restart.ReadExact(session, 12, objectId,
            receipt.Length, receipt.Sha256), "Tampered capture accepted.");
        Throws<InvalidDataException>(() => restart.Put(objectId, 12, "pixels"u8),
            "Tampered capture overwritten.");
        File.WriteAllBytes(file, new byte[KittyCaptureStore.MaxObjectBytes + 65537]);
        Throws<InvalidDataException>(() => restart.ReadExact(session, 12, objectId,
            receipt.Length, receipt.Sha256), "Oversized stored file allocated/read.");
        File.WriteAllBytes(file, encrypted[..(encrypted.Length - 1)]);
        Throws<InvalidDataException>(() => restart.ReadExact(session, 12, objectId,
            receipt.Length, receipt.Sha256), "Truncated capture accepted.");
        File.Delete(file);

        // A crash can leave a partial pending file. It still consumes quota until explicit cleanup.
        var sessionDirectory = Path.Combine(root, session.ToString("N"));
        for (var i = 0; i < 255; i++)
            File.WriteAllBytes(Path.Combine(sessionDirectory,
                Guid.NewGuid().ToString("N") + "." + Guid.NewGuid().ToString("N") + ".pending"), []);
        var races = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using (var first = StartChild(root, session, races[0]))
        using (var second = StartChild(root.ToUpperInvariant(), session, races[1]))
        {
            var finished = first.WaitForExit(30000) && second.WaitForExit(30000);
            if (!finished)
            {
                if (!first.HasExited) first.Kill(entireProcessTree: true);
                if (!second.HasExited) second.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Cross-process quota race timed out.");
            }
            Check((first.ExitCode == 0 && second.ExitCode == 2) ||
                (first.ExitCode == 2 && second.ExitCode == 0),
                "Cross-process session quota admission was not atomic.");
        }
        Throws<IOException>(() => restart.Put(Guid.NewGuid(), 15, "x"u8), "Session count quota exceeded.");
        restart.DeleteSession(session);
        Check(!Directory.Exists(sessionDirectory), "Explicit session cleanup failed.");

        using var other = new KittyCaptureStore(root, Guid.NewGuid());
        var retained = other.Put(Guid.NewGuid(), 16, "kept"u8);
        restart.DeleteSession(session);
        Check(other.ReadExact(retained.Session, retained.SourceSequence, retained.ObjectId,
            retained.Length, retained.Sha256).AsSpan().SequenceEqual("kept"u8),
            "Session cleanup removed another session.");
        other.DeleteSession(retained.Session);
    }

    var byteSession = Guid.NewGuid();
    var byteDirectory = Path.Combine(root, byteSession.ToString("N"));
    Directory.CreateDirectory(byteDirectory);
    for (var i = 0; i < 2; i++)
    {
        var pending = Path.Combine(byteDirectory,
            Guid.NewGuid().ToString("N") + "." + Guid.NewGuid().ToString("N") + ".pending");
        using var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.SetLength(32L * 1024 * 1024);
    }
    using (var full = new KittyCaptureStore(root, byteSession))
    {
        Throws<IOException>(() => full.Put(Guid.NewGuid(), 18, "x"u8), "Session byte quota exceeded.");
        full.DeleteSession(byteSession);
    }

    var globalSessions = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
    foreach (var id in globalSessions)
    {
        var directory = Path.Combine(root, id.ToString("N"));
        Directory.CreateDirectory(directory);
        for (var i = 0; i < 256; i++)
            File.WriteAllBytes(Path.Combine(directory,
                Guid.NewGuid().ToString("N") + "." + Guid.NewGuid().ToString("N") + ".pending"), []);
    }
    using (var global = new KittyCaptureStore(root, Guid.NewGuid()))
        Throws<IOException>(() => global.Put(Guid.NewGuid(), 19, "x"u8), "Global count quota exceeded.");
    foreach (var id in globalSessions)
        using (var cleanup = new KittyCaptureStore(root, id)) cleanup.DeleteSession(id);

    var foreignSession = Guid.NewGuid();
    var foreignDirectory = Path.Combine(root, foreignSession.ToString("N"));
    var symlinkCreated = true;
    try { Directory.CreateSymbolicLink(foreignDirectory, outside); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        symlinkCreated = false;
        Console.WriteLine("Kitty capture store symlink check skipped: test user cannot create symlinks.");
    }
    try
    {
        if (symlinkCreated)
        {
            using var foreign = new KittyCaptureStore(root, foreignSession);
            ThrowsStoreRejection(() => foreign.Put(Guid.NewGuid(), 17, "x"u8),
                "Reparse session directory accepted.");
        }
    }
    finally
    {
        if (symlinkCreated)
        {
            Check((File.GetAttributes(foreignDirectory) & FileAttributes.ReparsePoint) != 0,
                "Test link changed before cleanup.");
            Directory.Delete(foreignDirectory);
        }
    }
    Console.WriteLine("Kitty capture store: restart, authenticated read, tamper, conflict, quota race, pending crash, reparse and cleanup passed.");
}
finally
{
    DeleteTestDirectory(root, "termlink-kitty-store-");
    DeleteTestDirectory(outside, "termlink-kitty-outside-");
}

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static Process StartChild(string root, Guid session, Guid objectId)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path is unavailable.");
    var info = new ProcessStartInfo(executable) { UseShellExecute = false };
    if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("--child-put");
    info.ArgumentList.Add(root);
    info.ArgumentList.Add(session.ToString("D"));
    info.ArgumentList.Add(objectId.ToString("D"));
    return Process.Start(info) ?? throw new InvalidOperationException("Test child process did not start.");
}

static void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

static void ThrowsStoreRejection(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
    throw new InvalidOperationException(message);
}

static void DeleteTestDirectory(string path, string prefix)
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
