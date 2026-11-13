// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Runtime.Versioning;
using TermLink.Agent;
using TermLink.Agent.AI;
using TermLink.Ai;

internal static class PortableAiDirectoryValidation
{
    private static readonly UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static async Task RunAsync(string root)
    {
        PrivateDirectory(root);
        await DefaultManagerAsync(Path.Combine(root, "default"));
        await ReceiptFencesAsync(Path.Combine(root, "receipts"));
        await CancelledReceiptAsync(Path.Combine(root, "cancelled"));
        await DirectoryAuthorityAsync();
        PageMapping();
        if (OperatingSystem.IsMacOS())
        {
            await RealMacDirectoriesAsync(Path.Combine(root, "Mac space ; $(literal) 中文"));
            SymlinkFences(Path.Combine(root, "links"));
        }
        Console.WriteLine("PASS portable AI directory compatibility complete (local fixtures and no-follow filesystem only; no models or credentials)");
    }

    private static async Task DefaultManagerAsync(string root)
    {
        using var manager = new AiSessionManager(installRoot: root, catalog: EmptyCatalog);
        var owner = Guid.NewGuid();
        var catalog = Parse(await manager.ExecuteAsync(owner, "{\"action\":\"catalog\"}", CancellationToken.None));
        Require(catalog.GetProperty("cliWorkspaceCreation").GetBoolean() == OperatingSystem.IsWindows() &&
            catalog.GetProperty("directoryBrowsing").GetBoolean(), "Default manager changed Windows/default catalog behavior");
        var request = Create(manager);
        var response = await manager.ExecuteAsync(owner, Encode(request), CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            var path = Text(Parse(response), "directory");
            Require(Directory.Exists(path) && Path.GetDirectoryName(path) == Path.Combine(root, "AiWorkspaces") &&
                Path.GetFileName(path).StartsWith("cli_", StringComparison.Ordinal), "Windows default workspace behavior changed");
        }
        else Require(Text(Parse(response), "error") == "CLI_WORKSPACE_UNAVAILABLE", "A manager without a portable factory enabled non-Windows creation");
        Console.WriteLine("PASS unchanged optional-callback default catalog and platform-specific workspace branch");
    }

    private static async Task ReceiptFencesAsync(string root)
    {
        PrivateDirectory(root);
        var count = 0;
        using var manager = new AiSessionManager(installRoot: root, catalog: EmptyCatalog, createCliWorkspace: () =>
        {
            var path = Path.Combine(root, "cli-" + Interlocked.Increment(ref count));
            Directory.CreateDirectory(path); return path;
        });
        var owner = Guid.NewGuid(); var request = Create(manager); var body = Encode(request);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => manager.ExecuteAsync(owner, body, CancellationToken.None)));
        Require(results.All(result => result == results[0]) && count == 1, "A concurrent receipt retry created extra CLI directories");
        var first = Parse(results[0]);
        Require(Text(first, "hostId") == manager.HostId && Text(first, "operationId") == request.OperationId && Directory.Exists(Text(first, "directory")),
            "CLI workspace did not return the original host/operation/path receipt");
        await Error(manager, owner, request with { Directory = root }, "OPERATION_ID_CONFLICT");
        await Error(manager, owner, request with { OperationId = Guid.NewGuid().ToString(), Directory = root }, "INVALID_DIRECTORY_MODE");
        await Error(manager, owner, request with { OperationId = "" }, "INVALID_OPERATION_ID");
        await Error(manager, owner, request with { HostId = Guid.NewGuid().ToString() }, "HOST_CHANGED");
        var denied = Parse(await manager.ExecuteAsync(owner, body, CancellationToken.None, () => false));
        Require(Text(denied, "error") == "AUTHORITY_CHANGED" && count == 1, "An unauthorized caller consumed a receipt or created a directory");
        var other = Parse(await manager.ExecuteAsync(Guid.NewGuid(), body, CancellationToken.None));
        Require(Text(other, "directory") != Text(first, "directory") && count == 2, "Operation receipts were shared between callers");
        manager.Dispose();
        await Error(manager, owner, request, "HOST_CHANGED");
        Console.WriteLine("PASS CLI receipts: concurrent exact-body deduplication, caller isolation, arbitrary path/host/operation/authority rejection");
    }

    private static async Task CancelledReceiptAsync(string root)
    {
        PrivateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var count = 0;
        using var manager = new AiSessionManager(installRoot: root, catalog: EmptyCatalog, createCliWorkspace: () =>
        {
            Interlocked.Increment(ref count); entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            var path = Path.Combine(root, "cli-original"); Directory.CreateDirectory(path); return path;
        });
        var owner = Guid.NewGuid(); var body = Encode(Create(manager));
        using var disconnected = new CancellationTokenSource();
        var first = manager.ExecuteAsync(owner, body, disconnected.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        disconnected.Cancel();
        try { await first; throw new InvalidOperationException("Cancelled caller unexpectedly received a receipt"); }
        catch (OperationCanceledException) { }
        release.Set();
        var recovered = Parse(await manager.ExecuteAsync(owner, body, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Require(count == 1 && Directory.Exists(Text(recovered, "directory")), "A cancelled caller lost the accepted operation or repeated creation");
        var allowed = true;
        var revokedPath = Path.Combine(root, "revoked");
        using var revoked = new AiSessionManager(installRoot: root, catalog: EmptyCatalog, createCliWorkspace: () =>
        {
            Directory.CreateDirectory(revokedPath); allowed = false; return revokedPath;
        });
        var rejected = Parse(await revoked.ExecuteAsync(owner, Encode(Create(revoked)), CancellationToken.None, () => allowed));
        Require(Text(rejected, "error") == "AUTHORITY_CHANGED" && !Directory.Exists(revokedPath), "Revocation during create returned or retained an unused workspace");
        Console.WriteLine("PASS lost caller receipt recovery and authority loss after creation cleanup");
    }

    private static async Task DirectoryAuthorityAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DirectoryListingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var allowed = true;
        using var manager = new AiSessionManager(catalog: EmptyCatalog, listDirectories: (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult(); return response.Task;
        });
        var owner = Guid.NewGuid(); var request = new AiSessionManager.Request("directories", manager.HostId);
        var pending = manager.ExecuteAsync(owner, Encode(request), CancellationToken.None, () => allowed);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        allowed = false;
        response.SetResult(new() { CurrentPath = "/fixture", Entries = [] });
        Require(Text(Parse(await pending), "error") == "AUTHORITY_CHANGED", "Authorization loss during listing exposed its result");
        await Error(manager, owner, request with { HostId = "retired" }, "HOST_CHANGED");
        Require(calls == 1, "A stale host reached the directory provider");

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelling = new AiSessionManager(catalog: EmptyCatalog, listDirectories: async (_, _, _, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return new() { Entries = [] };
        });
        using var cts = new CancellationTokenSource();
        var task = cancelling.ExecuteAsync(owner, Encode(new AiSessionManager.Request("directories", cancelling.HostId)), cts.Token);
        cts.Cancel();
        try { await task; throw new InvalidOperationException("Directory cancellation was ignored"); } catch (OperationCanceledException) { }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS directory callback authorization/host recheck and linked caller cancellation");
    }

    private static void PageMapping()
    {
        var page = JsonSerializer.SerializeToElement(new { current_path = "/fixture", parent_path = "/", next_offset = 500,
            entries = new[] { new { name = "file", path = "/fixture/file", kind = "file", size_bytes = (long?)7,
                modified_at = (DateTimeOffset?)null, attributes = Array.Empty<string>() } } });
        var dirs = PortableAiDirectories.MapPage(page, true);
        var files = PortableAiDirectories.MapPage(page, false);
        Require(dirs.Entries.Count == 0 && dirs.NextOffset == 500 && dirs.CurrentPath == "/fixture" && dirs.ParentPath == "/" &&
            files.Entries.Single().Kind == "file" && files.Entries.Single().SizeBytes == 7, "Filtering consumed/lost the native continuation offset");
        Console.WriteLine("PASS directory-only empty-page continuation and files metadata mapping");
    }

    [SupportedOSPlatform("macos")]
    private static async Task RealMacDirectoriesAsync(string account)
    {
        PrivateDirectory(account);
        var missing = new AiAgentInstallation("codex", "Codex", "", "", "", "codex", "missing");
        using var host = new PortableAiHost(account, () => Task.FromResult(missing), () => Task.FromResult(missing with { Id = "zcode" }));
        var owner = Guid.NewGuid(); var catalog = Parse(await host.ExecuteAsync(owner, "{\"action\":\"catalog\"}", CancellationToken.None, () => true));
        var root = Path.Combine(account, "AiWorkspaces"); var hostId = Text(catalog, "hostId");
        Require(catalog.GetProperty("cliWorkspaceCreation").GetBoolean() && catalog.GetProperty("directoryBrowsing").GetBoolean() &&
            Text(catalog, "defaultDirectory") == root && Text(catalog, "cliWorkspaceRoot") == root && Directory.Exists(root), "Old APK default CLI catalog is incomplete");
        Require(File.GetUnixFileMode(root) == Private, "Mac workspace root is not private");
        var request = new AiSessionManager.Request("create-cli-workspace", hostId, Guid.NewGuid().ToString());
        var body = Encode(request);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.ExecuteAsync(owner, body, CancellationToken.None, () => true)));
        var path = Text(Parse(results[0]), "directory");
        Require(results.All(value => value == results[0]) && Directory.Exists(path) && Path.GetDirectoryName(path) == root &&
            Path.GetFileName(path).StartsWith("cli-", StringComparison.Ordinal) && File.GetUnixFileMode(path) == Private &&
            Directory.EnumerateDirectories(root).Count() == 1, "Mac creation did not remain one private direct child");
        var directory = Path.Combine(root, "folder 空格 ; $(literal)"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(root, "regular-file"), "fixture");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked-folder"), directory);
        var listed = Parse(await host.ExecuteAsync(owner, Encode(new AiSessionManager.Request("directories", hostId, Directory: root)), CancellationToken.None, () => true))
            .GetProperty("directories");
        var entries = listed.GetProperty("entries").EnumerateArray().ToArray();
        Require(entries.Length == 2 && entries.All(e => Text(e, "kind") == "directory") && entries.Any(e => Text(e, "name") == "folder 空格 ; $(literal)"),
            "Mac directory list included a file/link or corrupted a literal POSIX path");
        var all = Parse(await host.ExecuteAsync(owner, Encode(new AiSessionManager.Request("files", hostId, Directory: root)), CancellationToken.None, () => true)).GetProperty("directories");
        Require(all.GetProperty("entries").GetArrayLength() == 3, "Mac file-reference list lost regular entries or followed a link");
        var denied = Parse(await host.ExecuteAsync(owner, Encode(new AiSessionManager.Request("directories", hostId, Directory: Path.Combine(root, "linked-folder"))), CancellationToken.None, () => true));
        Require(Text(denied, "error") == "DIRECTORY_BROWSE_FAILED", "Mac listing followed a symlink directory");
        var roots = Parse(await host.ExecuteAsync(owner, Encode(new AiSessionManager.Request("directories", hostId)), CancellationToken.None, () => true));
        Require(roots.GetProperty("directories").GetProperty("entries").EnumerateArray().All(e => Text(e, "kind") == "directory"), "Empty-path root discovery failed");
        var paging = Path.Combine(account, "paging"); Directory.CreateDirectory(paging);
        for (var i = 0; i < 503; i++) Directory.CreateDirectory(Path.Combine(paging, "dir-" + i.ToString("D4")));
        for (var i = 0; i < 1002; i++) await File.WriteAllTextAsync(Path.Combine(paging, "file-" + i.ToString("D4")), "");
        var observed = new HashSet<string>(StringComparer.Ordinal); var offset = 0L; var pages = 0; var emptyContinuation = false;
        do
        {
            var page = Parse(await host.ExecuteAsync(owner, Encode(new AiSessionManager.Request("directories", hostId, Directory: paging, Offset: offset)),
                CancellationToken.None, () => true)).GetProperty("directories");
            var dirs = page.GetProperty("entries").EnumerateArray().ToArray();
            foreach (var entry in dirs) Require(observed.Add(Text(entry, "name")), "Native continuation repeated a directory");
            pages++;
            var next = page.GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            Require(next.GetInt64() > offset, "Native continuation stopped advancing");
            if (dirs.Length == 0) emptyContinuation = true;
            offset = next.GetInt64();
            Require(pages < 10, "Directory pagination looped indefinitely");
        } while (true);
        Require(observed.Count == 503 && pages == 4 && emptyContinuation, "Actual native pages lost folders or the empty-page continuation");
        Console.WriteLine("PASS actual Mac native listing + old-APK catalog/create receipt: private modes, literal paths, roots, files filtering and no-follow symlinks");
    }

    private static void SymlinkFences(string root)
    {
        PrivateDirectory(root);
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        var linkedAccount = Path.Combine(root, "account-link"); Directory.CreateSymbolicLink(linkedAccount, outside);
        ExpectIOException(() => { using var ignored = new PortableAiDirectories(linkedAccount); });
        var account = Path.Combine(root, "account"); PrivateDirectory(account);
        Directory.CreateSymbolicLink(Path.Combine(account, "AiWorkspaces"), outside);
        ExpectIOException(() => { using var ignored = new PortableAiDirectories(account); });
        Directory.Delete(Path.Combine(account, "AiWorkspaces"));
        using (var directories = new PortableAiDirectories(account))
        {
            Directory.Delete(directories.WorkspaceRoot);
            Directory.CreateSymbolicLink(directories.WorkspaceRoot, outside);
            try { directories.CreateCliWorkspace(); throw new InvalidOperationException("A replaced workspace root was followed"); }
            catch (AiOperationException e) { Require(e.Code == "AI_WORKSPACE_CREATE_FAILED", "Unsafe root returned an unexpected error"); }
        }
        Require(!Directory.EnumerateFileSystemEntries(outside).Any(), "A rejected linked account/root wrote outside its private tree");
        Console.WriteLine("PASS native directory handles reject linked account/root and replaced root without outside writes");
    }

    private static Task<AiAgentInstallation[]> EmptyCatalog() => Task.FromResult(Array.Empty<AiAgentInstallation>());
    private static AiSessionManager.Request Create(AiSessionManager manager) => new("create-cli-workspace", manager.HostId, Guid.NewGuid().ToString());
    private static string Encode(AiSessionManager.Request request) => AiSessionManager.Encode(request);
    private static JsonElement Parse(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) ? field.GetString() ?? "" : "";
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void ExpectIOException(Action action) { try { action(); throw new InvalidOperationException("A symlink root was accepted"); } catch (IOException) { } }
    private static async Task Error(AiSessionManager manager, Guid caller, AiSessionManager.Request request, string code) =>
        Require(Text(Parse(await manager.ExecuteAsync(caller, Encode(request), CancellationToken.None)), "error") == code, "Expected safe directory error " + code);
    private static void PrivateDirectory(string path) {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

}
