using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

var testRoot = Path.Combine(Path.GetTempPath(), "TermLink-file-operation-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    await ValidatePathsAndListingsAsync(testRoot);
    await ValidateArchiveAndHashAsync(testRoot);
    await ValidateWorkerFaultRecoveryAsync(testRoot);
    await ValidateStreamingWorkerAndCancellationAsync(testRoot);
    await ValidateLeaseCancellationAndRemoteAccessAsync(testRoot);
    await ValidateTerminalRegistrationTimeoutAsync(testRoot);
    await ValidateTerminalAbortCleanupAsync(testRoot);
    await ValidateDeterministicServerRejectionsAsync(testRoot);
    await ValidateApiContractAsync();
    await ValidateClaimAmbiguityAsync();
    await ValidateClaimBodyAmbiguityAsync();
    await ValidateClaimInProgressRecoveryAsync();
    await ValidateClaimDefinitiveRejectionRecoveryAsync();
    await ValidateClaimInvalidResponseRecoveryAsync();
    ValidateTerminalLaunch(testRoot);
    Console.WriteLine("Windows file-operation validation passed.");
}
finally
{
    try { Directory.Delete(testRoot, true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static async Task ValidateWorkerFaultRecoveryAsync(string testRoot)
{
    var stub = Path.Combine(testRoot, "recovery-stub.exe");
    File.WriteAllBytes(stub, new byte[] { 0x4d, 0x5a });

    async Task<IReadOnlyList<FileOperationWorkerDiagnostic>> RunScenarioAsync(Exception firstFailure)
    {
        var api = new RecoveringClaimApi(firstFailure, testRoot);
        var diagnostics = new List<FileOperationWorkerDiagnostic>();
        var sync = new object();
        using var worker = new ProductionFileOperationWorker(
            api,
            new DirectoryListingService(),
            new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "recovery-artifacts-" + Guid.NewGuid().ToString("N"))),
            new ManagedTerminalLauncher(new WindowsPathService(), new RecordingProcessStarter(), () => null, stub, stub, stub),
            CancellationToken.None,
            supervisorRestartDelay: TimeSpan.FromMilliseconds(25),
            diagnostic: value => { lock (sync) diagnostics.Add(value); });
        worker.Start();
        await api.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await api.ClaimWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(worker.IsRunning, "file-operation worker stopped after recovering from a claim failure");
        worker.Dispose();
        lock (sync) return diagnostics.ToArray();
    }

    var timeoutDiagnostics = await RunScenarioAsync(new TaskCanceledException("simulated HTTP timeout"));
    Require(timeoutDiagnostics.Any(value => value.Code == "FILE_OPERATION_REQUEST_TIMEOUT") &&
        timeoutDiagnostics.Any(value => value.Code == "FILE_OPERATION_WORKER_RECOVERED") &&
        timeoutDiagnostics.All(value => !string.IsNullOrWhiteSpace(value.MessageZh) &&
            !string.IsNullOrWhiteSpace(value.MessageEn)),
        "HTTP timeout recovery did not emit bounded bilingual degraded/recovered diagnostics");

    var unexpectedDiagnostics = await RunScenarioAsync(new InvalidOperationException("simulated unexpected worker failure"));
    Require(unexpectedDiagnostics.Any(value => value.Code == "FILE_OPERATION_WORKER_RESTARTING" &&
            value.RestartCount == 1 && value.Error is InvalidOperationException) &&
        unexpectedDiagnostics.Any(value => value.Code == "FILE_OPERATION_WORKER_RECOVERED" &&
            value.RestartCount == 1),
        "unexpected worker failure was not independently supervised and recovered");
}

static Task ValidatePathsAndListingsAsync(string testRoot)
{
    var paths = new WindowsPathService();
    Require(string.Equals(paths.NormalizeExistingDirectory(testRoot), Path.GetFullPath(testRoot), StringComparison.OrdinalIgnoreCase),
        "existing directory normalization changed the requested path");
    RequireFileOperationCode(() => paths.NormalizeAbsolutePath("relative"), "PATH_NOT_ABSOLUTE");

    var roots = new DirectoryListingService(paths, () => new[] { @"D:\", @"c:\", @"C:\" });
    var rootResult = roots.List(string.Empty, 0);
    Require(rootResult.CurrentPath == string.Empty && rootResult.ParentPath == null,
        "root listing did not use the virtual root shape");
    Require(rootResult.Entries.Count == 2 && rootResult.Entries.All(entry => entry.Kind == "directory"),
        "root listing did not de-duplicate directory-shaped drive roots");

    var listingRoot = Path.Combine(testRoot, "listing");
    Directory.CreateDirectory(listingRoot);
    Directory.CreateDirectory(Path.Combine(listingRoot, "b-dir"));
    Directory.CreateDirectory(Path.Combine(listingRoot, "A-dir"));
    File.WriteAllText(Path.Combine(listingRoot, "z-file.txt"), "z", Encoding.UTF8);
    File.WriteAllText(Path.Combine(listingRoot, "B-file.txt"), "b", Encoding.UTF8);
    var listing = new DirectoryListingService(paths, () => Array.Empty<string>()).List(listingRoot, 0);
    Require(listing.Entries.Select(entry => entry.Name).SequenceEqual(
        new[] { "A-dir", "b-dir", "B-file.txt", "z-file.txt" }),
        "directory listing was not sorted directories-first with invariant case-insensitive names");
    Require(listing.Entries.Where(entry => entry.Kind == "file").All(entry => entry.SizeBytes.HasValue),
        "file listing omitted file size");

    var pageRoot = Path.Combine(testRoot, "paging");
    Directory.CreateDirectory(pageRoot);
    for (var index = 0; index < 502; index++)
        File.WriteAllBytes(Path.Combine(pageRoot, "item-" + index.ToString("D3") + ".bin"), new byte[] { 1 });
    var pageService = new DirectoryListingService(paths, () => Array.Empty<string>());
    var firstPage = pageService.List(pageRoot, 0);
    var secondPage = pageService.List(pageRoot, firstPage.NextOffset!.Value);
    Require(firstPage.Entries.Count == 500 && firstPage.NextOffset == 500,
        "directory first page did not enforce the 500-entry limit");
    Require(secondPage.Entries.Count == 2 && secondPage.NextOffset == null,
        "directory final page returned an invalid continuation offset");

    var oversizedEntries = Enumerable.Range(0, 500).Select(index => new FileOperationEntry
    {
        Name = new string('n', 240) + index.ToString("D3"),
        Path = @"C:\\" + new string('p', 3500) + index.ToString("D3"),
        Kind = "file",
        SizeBytes = 1,
        ModifiedAt = DateTimeOffset.UtcNow,
        Attributes = Array.Empty<string>()
    }).ToArray();
    var byteBounded = DirectoryListingService.Page(@"C:\\long", @"C:\\", oversizedEntries, 7);
    Require(DirectoryListingService.SerializedSize(byteBounded) <=
        DirectoryListingService.MaximumSerializedResultBytes,
        "directory LIST exceeded its serialized JSON byte budget");
    Require(byteBounded.Entries.Count > 0 && byteBounded.Entries.Count < 500 &&
        byteBounded.NextOffset == 7 + byteBounded.Entries.Count,
        "byte-bounded LIST did not return the exact continuation offset");
    return Task.CompletedTask;
}

static async Task ValidateArchiveAndHashAsync(string testRoot)
{
    Require(FileTransferPreparer.MaximumTransferBytes == 5L * 1024 * 1024 * 1024,
        "Windows download limit diverged from the server object limit");
    using (var memory = new MemoryStream())
    using (var bounded = new BoundedWriteStream(memory, 4, true))
    {
        await bounded.WriteAsync(new byte[] { 1, 2, 3, 4 });
        Require(memory.Length == 4, "bounded ZIP stream rejected its exact byte boundary");
        await RequireFileOperationCodeAsync(
            async () => await bounded.WriteAsync(new byte[] { 5 }),
            "FILE_TOO_LARGE");
        Require(memory.Length == 4, "bounded ZIP stream wrote beyond its byte boundary");
    }
    var source = Path.Combine(testRoot, "archive-source");
    var empty = Path.Combine(source, "empty");
    Directory.CreateDirectory(empty);
    var content = Encoding.UTF8.GetBytes("unchanged transfer bytes");
    var file = Path.Combine(source, "payload.txt");
    File.WriteAllBytes(file, content);

    var outside = Path.Combine(testRoot, "outside");
    Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside, "must-not-follow.txt"), "secret", Encoding.UTF8);
    var link = Path.Combine(source, "linked-outside");
    var linkCreated = false;
    var fileLink = Path.Combine(source, "linked-file.txt");
    var fileLinkCreated = false;
    try
    {
        Directory.CreateSymbolicLink(link, outside);
        linkCreated = true;
    }
    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is PlatformNotSupportedException)
    {
    }
    try
    {
        File.CreateSymbolicLink(fileLink, Path.Combine(outside, "must-not-follow.txt"));
        fileLinkCreated = true;
    }
    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is PlatformNotSupportedException)
    {
    }

    Require(!FileTransferPreparer.ShouldTraverseDirectory(
        FileAttributes.Directory | FileAttributes.ReparsePoint),
        "ZIP traversal accepted a reparse-point directory");
    Require(FileTransferPreparer.ShouldTraverseDirectory(FileAttributes.Directory),
        "ZIP traversal rejected a normal directory");

    var temporaryArtifacts = Path.Combine(testRoot, "artifacts");
    var preparer = new FileTransferPreparer(new WindowsPathService(), temporaryArtifacts);
    string zipPath;
    await using (var artifact = await preparer.PrepareAsync(source, CancellationToken.None))
    {
        Require(artifact.DirectoryZip && artifact.Filename.EndsWith(".zip", StringComparison.Ordinal),
            "directory transfer was not represented as a ZIP");
        Require(artifact.SizeBytes > 0 && artifact.Sha256.Length == 32,
            "directory ZIP size or SHA-256 metadata was invalid");
        await using var stream = artifact.OpenRead();
        zipPath = ((FileStream)stream).Name;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true, Encoding.UTF8);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        var rootName = new DirectoryInfo(source).Name + "/";
        Require(names.Contains(rootName) && names.Contains(rootName + "empty/") &&
            names.Contains(rootName + "payload.txt"),
            "ZIP omitted the selected root, an empty directory, or a file");
        if (linkCreated)
            Require(!names.Any(name => name.Contains("linked-outside", StringComparison.Ordinal)),
                "ZIP followed or represented a reparse-point directory");
        if (fileLinkCreated)
            Require(!names.Any(name => name.Contains("linked-file", StringComparison.Ordinal)),
                "ZIP followed or represented a reparse-point file");
    }
    Require(!File.Exists(zipPath), "temporary ZIP was not removed after disposal");

    var resumableTransferId = Guid.NewGuid();
    string resumableZipPath;
    byte[] resumableHash;
    await using (var first = await preparer.PrepareAsync(source, resumableTransferId, CancellationToken.None))
    {
        resumableZipPath = ((FileStream)first.OpenRead()).Name;
        resumableHash = first.Sha256.ToArray();
    }
    Require(File.Exists(resumableZipPath) &&
        string.Equals(
            Path.GetDirectoryName(resumableZipPath),
            preparer.TransferDirectory(resumableTransferId),
            StringComparison.OrdinalIgnoreCase),
        "resumable directory ZIP escaped its transfer cache directory");
    await using (var resumed = await preparer.PrepareAsync(source, resumableTransferId, CancellationToken.None))
    {
        var resumedPath = ((FileStream)resumed.OpenRead()).Name;
        Require(string.Equals(resumedPath, resumableZipPath, StringComparison.OrdinalIgnoreCase) &&
            resumed.Sha256.SequenceEqual(resumableHash),
            "Agent restart did not reuse the stable resumable directory ZIP");
    }
    Require(File.Exists(resumableZipPath), "resumable ZIP was deleted before transfer finalization");
    preparer.Cleanup(resumableTransferId);
    Require(!Directory.Exists(preparer.TransferDirectory(resumableTransferId)),
        "successful or cancelled transfer cache cleanup left its task directory behind");
    var unsafeTransferId = Guid.NewGuid();
    var unsafeDirectory = preparer.TransferDirectory(unsafeTransferId);
    Directory.CreateDirectory(unsafeDirectory);
    var unexpectedCacheEntry = Path.Combine(unsafeDirectory, "unowned.data");
    File.WriteAllText(unexpectedCacheEntry, "keep", Encoding.UTF8);
    try
    {
        preparer.Cleanup(unsafeTransferId);
        throw new InvalidOperationException("unsafe transfer-cache cleanup was accepted");
    }
    catch (InvalidOperationException error) when (
        error.Message.Contains("unexpected or unsafe", StringComparison.Ordinal))
    {
    }
    Require(File.Exists(unexpectedCacheEntry),
        "transfer-cache cleanup deleted an unexpected entry");
    File.Delete(unexpectedCacheEntry);
    preparer.Cleanup(unsafeTransferId);
    var expiredTransferId = Guid.NewGuid();
    var expiredDirectory = preparer.TransferDirectory(expiredTransferId);
    Directory.CreateDirectory(expiredDirectory);
    var expiredPayload = Path.Combine(expiredDirectory, "payload.zip");
    File.WriteAllBytes(expiredPayload, new byte[] { 1, 2, 3 });
    var oldWrite = DateTime.UtcNow.Subtract(FileTransferPreparer.OrphanRetention).AddMinutes(-1);
    File.SetLastWriteTimeUtc(expiredPayload, oldWrite);
    Directory.SetLastWriteTimeUtc(expiredDirectory, oldWrite);
    preparer.CleanupExpired(DateTime.UtcNow - FileTransferPreparer.OrphanRetention);
    Require(!Directory.Exists(expiredDirectory),
        "expired transfer cache was not reclaimed by bounded startup cleanup");

    var oversizedSource = Path.Combine(testRoot, "oversized-archive-source");
    var oversizedArtifacts = Path.Combine(testRoot, "oversized-archive-artifacts");
    Directory.CreateDirectory(oversizedSource);
    var incompressible = new byte[64 * 1024];
    RandomNumberGenerator.Fill(incompressible);
    File.WriteAllBytes(Path.Combine(oversizedSource, "payload.bin"), incompressible);
    var boundedPreparer = new FileTransferPreparer(
        new WindowsPathService(),
        oversizedArtifacts,
        1024);
    await RequireFileOperationCodeAsync(
        async () => { await using var ignored = await boundedPreparer.PrepareAsync(oversizedSource, CancellationToken.None); },
        "FILE_TOO_LARGE");
    Require(Directory.Exists(oversizedArtifacts) &&
        !Directory.EnumerateFileSystemEntries(oversizedArtifacts).Any(),
        "over-limit directory ZIP left a temporary archive on disk");

    await using (var artifact = await preparer.PrepareAsync(file, CancellationToken.None))
    {
        Require(!artifact.DirectoryZip && artifact.Filename == "payload.txt",
            "plain file transfer was unexpectedly archived");
        Require(artifact.SizeBytes == content.Length &&
            artifact.Sha256.SequenceEqual(SHA256.HashData(content)),
            "plain file size or SHA-256 did not match unchanged bytes");
        await using var stream = artifact.OpenRead();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        Require(memory.ToArray().SequenceEqual(content), "plain file stream changed its bytes");
    }
}

static async Task ValidateStreamingWorkerAndCancellationAsync(string testRoot)
{
    var file = Path.Combine(testRoot, "stream.bin");
    var content = new byte[256 * 1024 + 17];
    RandomNumberGenerator.Fill(content);
    File.WriteAllBytes(file, content);

    var api = new RecordingApi(new ClaimedFileOperation
    {
        Id = Guid.NewGuid(),
        Operation = "DOWNLOAD",
        Path = file,
        LeaseToken = "lease",
        RequesterDeviceId = Guid.NewGuid()
    });
    var stub = Path.Combine(testRoot, "stub.exe");
    File.WriteAllBytes(stub, new byte[] { 0x4d, 0x5a });
    var terminal = new ManagedTerminalLauncher(
        new WindowsPathService(),
        new RecordingProcessStarter(),
        () => null,
        stub, stub, stub);
    using var worker = new ProductionFileOperationWorker(
        api,
        new DirectoryListingService(),
        new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "worker-artifacts")),
        terminal,
        CancellationToken.None);
    worker.Start();
    await api.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await api.ClaimWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
    worker.Dispose();
    Require(api.UploadedBytes.SequenceEqual(content), "worker upload did not stream the exact file bytes");
    Require(api.MaximumReadRequest <= 8192, "upload abstraction requested an unbounded whole-file read");
    Require(api.CompletedOperation, "download operation was not completed after upload");
    Require(api.ClaimCancelled, "disposing the worker did not cancel its independent claim loop");
}

static async Task ValidateLeaseCancellationAndRemoteAccessAsync(string testRoot)
{
    var payload = Path.Combine(testRoot, "cancellation.bin");
    File.WriteAllBytes(payload, new byte[32 * 1024]);
    var stub = Path.Combine(testRoot, "cancellation-stub.exe");
    File.WriteAllBytes(stub, new byte[] { 0x4d, 0x5a });

    var cancelledApi = new ControlledOperationApi(new ClaimedFileOperation
    {
        Id = Guid.NewGuid(),
        Operation = "DOWNLOAD",
        Path = payload,
        LeaseToken = "cancelled-lease",
        RequesterDeviceId = Guid.NewGuid(),
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    }) { LoseLeaseOnRenew = true };
    using (var worker = new ProductionFileOperationWorker(
        cancelledApi,
        new DirectoryListingService(),
        new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "cancel-artifacts")),
        new ManagedTerminalLauncher(new WindowsPathService(), new RecordingProcessStarter(), () => null, stub, stub, stub),
        CancellationToken.None,
        leaseRenewInterval: TimeSpan.FromMilliseconds(25)))
    {
        worker.Start();
        await cancelledApi.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelledApi.RenewAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelledApi.UploadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!cancelledApi.CompletedOperation && cancelledApi.FailureCode == null,
            "an Android-cancelled operation was incorrectly completed or failed by the Agent");
    }

    var policy = new RemotePolicy(false);
    var remoteApi = new ControlledOperationApi(new ClaimedFileOperation
    {
        Id = Guid.NewGuid(),
        Operation = "DOWNLOAD",
        Path = payload,
        LeaseToken = "remote-policy-lease",
        RequesterDeviceId = Guid.NewGuid(),
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    });
    using (var worker = new ProductionFileOperationWorker(
        remoteApi,
        new DirectoryListingService(),
        new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "remote-artifacts")),
        new ManagedTerminalLauncher(new WindowsPathService(), new RecordingProcessStarter(), () => null, stub, stub, stub),
        CancellationToken.None,
        () => policy.Enabled,
        leaseRenewInterval: TimeSpan.FromSeconds(5)))
    {
        worker.Start();
        await Task.Delay(300);
        Require(remoteApi.ClaimCount == 0, "remote-access-disabled Agent still claimed file operations");
        policy.Enabled = true;
        await remoteApi.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        policy.Enabled = false;
        await remoteApi.UploadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await remoteApi.FailureRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(remoteApi.FailureCode == "REMOTE_ACCESS_DISABLED" && !remoteApi.CompletedOperation,
            "disabling remote access did not abort and close the active operation");
    }
}

static async Task ValidateTerminalRegistrationTimeoutAsync(string testRoot)
{
    var stub = Path.Combine(testRoot, "registration-stub.exe");
    File.WriteAllBytes(stub, new byte[] { 0x4d, 0x5a });
    var operationId = Guid.NewGuid();
    var api = new ControlledOperationApi(new ClaimedFileOperation
    {
        Id = operationId,
        Operation = "OPEN_TERMINAL",
        Path = testRoot,
        ShellType = "command-prompt",
        LeaseToken = "registration-lease",
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    });
    var starts = new RecordingProcessStarter();
    var terminated = 0;
    using (var worker = new ProductionFileOperationWorker(
        api,
        new DirectoryListingService(),
        new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "terminal-artifacts")),
        new ManagedTerminalLauncher(new WindowsPathService(), starts, () => null, stub, stub, stub),
        CancellationToken.None,
        sessionVisible: _ => false,
        terminateSession: _ => { Interlocked.Increment(ref terminated); return true; },
        registrationWait: TimeSpan.FromMilliseconds(250)))
    {
        worker.Start();
        await api.FailureRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(api.FailureCode == "TERMINAL_REGISTRATION_TIMEOUT" && terminated > 0,
            "unregistered SessionHost was reported successful or was not terminated");
    }
    var stableSession = ProductionFileOperationWorker.StableSessionId(operationId);
    Require(stableSession == ProductionFileOperationWorker.StableSessionId(operationId) &&
        stableSession != ProductionFileOperationWorker.StableSessionId(Guid.NewGuid()),
        "OPEN_TERMINAL operation-to-session mapping was not stable and unique");
    Require(starts.Starts.Single().ArgumentList.Contains(stableSession.ToString("D")),
        "terminal retry did not launch the deterministic managed session ID");

    var successId = Guid.NewGuid();
    var successRequestedPath = testRoot + Path.DirectorySeparatorChar + ".";
    var successApi = new ControlledOperationApi(new ClaimedFileOperation
    {
        Id = successId,
        Operation = "OPEN_TERMINAL",
        Path = successRequestedPath,
        ShellType = "command-prompt",
        LeaseToken = "registered-lease",
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    });
    var successStarts = new RecordingProcessStarter();
    using (var worker = new ProductionFileOperationWorker(
        successApi,
        new DirectoryListingService(),
        new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "registered-artifacts")),
        new ManagedTerminalLauncher(new WindowsPathService(), successStarts, () => null, stub, stub, stub),
        CancellationToken.None,
        sessionVisible: _ => successStarts.Starts.Count > 0,
        registrationWait: TimeSpan.FromSeconds(1)))
    {
        worker.Start();
        await successApi.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = successApi.CompletionResult!.Value;
        Require(result.GetProperty("session_id").GetGuid() ==
            ProductionFileOperationWorker.StableSessionId(successId) &&
            result.GetProperty("cwd").GetString() == successRequestedPath &&
            successStarts.Starts.Single().WorkingDirectory == Path.GetFullPath(testRoot),
            "OPEN_TERMINAL did not preserve exact cwd while launching from its normalized directory");
    }
}

static async Task ValidateTerminalAbortCleanupAsync(string testRoot)
{
    var stub = Path.Combine(testRoot, "terminal-cleanup-stub.exe");
    File.WriteAllBytes(stub, new byte[] { 0x4d, 0x5a });
    var manualSessionId = Guid.NewGuid();

    async Task<(Guid Expected, List<Guid> Terminated)> RunAsync(
        ControlledOperationApi api,
        Func<bool> remoteEnabled,
        Action afterCompletionStarted,
        TimeSpan renewInterval)
    {
        var terminated = new List<Guid>();
        var sync = new object();
        var expected = ProductionFileOperationWorker.StableSessionId(api.OperationId);
        using var worker = new ProductionFileOperationWorker(
            api,
            new DirectoryListingService(),
            new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "terminal-cleanup-" + api.OperationId.ToString("N"))),
            new ManagedTerminalLauncher(new WindowsPathService(), new RecordingProcessStarter(), () => null, stub, stub, stub),
            CancellationToken.None,
            remoteEnabled,
            sessionId => sessionId == manualSessionId ||
                (sessionId == expected &&
                    (!api.DelayVisibilityUntilRenew ||
                        (!api.LoseLeaseOnRenew && api.RenewAttempted.Task.IsCompleted))),
            sessionId => { lock (sync) terminated.Add(sessionId); return true; },
            renewInterval,
            TimeSpan.FromSeconds(1));
        worker.Start();
        if (api.DelayVisibilityUntilRenew)
            await api.RenewAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        else
            await api.CompletionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        afterCompletionStarted();
        await api.TerminalOutcome.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        lock (sync) return (expected, terminated.ToList());
    }

    var leaseApi = ControlledOperationApi.Terminal(testRoot);
    leaseApi.DelayVisibilityUntilRenew = true;
    leaseApi.LoseLeaseOnRenew = true;
    var leaseResult = await RunAsync(leaseApi, () => true, () => { }, TimeSpan.FromMilliseconds(25));
    Require(leaseResult.Terminated.SequenceEqual(new[] { leaseResult.Expected }) &&
        !leaseResult.Terminated.Contains(manualSessionId),
        "lease-loss cleanup did not exclusively terminate the operation terminal");

    var policy = new RemotePolicy(true);
    var remoteApi = ControlledOperationApi.Terminal(testRoot);
    remoteApi.BlockCompletion = true;
    var remoteResult = await RunAsync(
        remoteApi,
        () => policy.Enabled,
        () => policy.Enabled = false,
        TimeSpan.FromSeconds(5));
    Require(remoteResult.Terminated.SequenceEqual(new[] { remoteResult.Expected }) &&
        !remoteResult.Terminated.Contains(manualSessionId),
        "remote-disable cleanup did not exclusively terminate the operation terminal");

    var rejectedApi = ControlledOperationApi.Terminal(testRoot);
    rejectedApi.RejectCompletion = true;
    var rejectedResult = await RunAsync(
        rejectedApi,
        () => true,
        () => { },
        TimeSpan.FromSeconds(5));
    Require(rejectedResult.Terminated.SequenceEqual(new[] { rejectedResult.Expected }) &&
        !rejectedResult.Terminated.Contains(manualSessionId),
        "completion-rejection cleanup did not exclusively terminate the operation terminal");

    var cancelledDuringCompleteApi = ControlledOperationApi.Terminal(testRoot);
    cancelledDuringCompleteApi.RejectCompletionAsCancelled = true;
    var cancelledDuringCompleteResult = await RunAsync(
        cancelledDuringCompleteApi,
        () => true,
        () => { },
        TimeSpan.FromSeconds(5));
    Require(cancelledDuringCompleteResult.Terminated.SequenceEqual(
            new[] { cancelledDuringCompleteResult.Expected }) &&
        !cancelledDuringCompleteResult.Terminated.Contains(manualSessionId),
        "Android cancellation won during completion but did not clean only the operation terminal");

    var completedApi = ControlledOperationApi.Terminal(testRoot);
    var completedResult = await RunAsync(
        completedApi,
        () => true,
        () => { },
        TimeSpan.FromSeconds(5));
    Require(completedResult.Terminated.Count == 0,
        "successful OPEN_TERMINAL completion terminated an operation or manual session");

    var ambiguousApi = ControlledOperationApi.Terminal(testRoot);
    ambiguousApi.AmbiguousCompletionOnce = true;
    ambiguousApi.RenewConflictsAfterCompletionCommit = true;
    var ambiguousTerminated = new List<Guid>();
    var ambiguousSync = new object();
    var ambiguousExpected = ProductionFileOperationWorker.StableSessionId(ambiguousApi.OperationId);
    var ambiguousStarts = new RecordingProcessStarter();
    using (var worker = new ProductionFileOperationWorker(
        ambiguousApi,
        new DirectoryListingService(),
        new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "terminal-ambiguous-complete")),
        new ManagedTerminalLauncher(new WindowsPathService(), ambiguousStarts, () => null, stub, stub, stub),
        CancellationToken.None,
        () => true,
        sessionId => sessionId == ambiguousExpected && ambiguousApi.RenewAttempted.Task.IsCompleted,
        sessionId => { lock (ambiguousSync) ambiguousTerminated.Add(sessionId); return true; },
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromSeconds(2)))
    {
        worker.Start();
        await ambiguousApi.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ambiguousApi.RenewConflictReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    lock (ambiguousSync)
    {
        Require(ambiguousApi.CompletionAttempts == 2 && ambiguousApi.CompletedOperation &&
            ambiguousStarts.Starts.Count == 1 && ambiguousTerminated.Count == 0,
            "ambiguous committed completion plus renew 409 killed the successful operation terminal");
    }
}

static async Task ValidateDeterministicServerRejectionsAsync(string testRoot)
{
    var payload = Path.Combine(testRoot, "rejected-upload.bin");
    File.WriteAllBytes(payload, new byte[] { 1, 2, 3 });
    var stub = Path.Combine(testRoot, "rejection-stub.exe");
    File.WriteAllBytes(stub, new byte[] { 0x4d, 0x5a });
    var uploadApi = new ControlledOperationApi(new ClaimedFileOperation
    {
        Id = Guid.NewGuid(), Operation = "DOWNLOAD", Path = payload,
        RequesterDeviceId = Guid.NewGuid(),
        LeaseToken = "upload-rejection", LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    }) { RejectUploadAllocation = true };
    using (var worker = new ProductionFileOperationWorker(
        uploadApi,
        new DirectoryListingService(),
        new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "rejected-upload-artifacts")),
        new ManagedTerminalLauncher(new WindowsPathService(), new RecordingProcessStarter(), () => null, stub, stub, stub),
        CancellationToken.None))
    {
        worker.Start();
        await uploadApi.FailureRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(uploadApi.UploadAllocationAttempts == 1 &&
            uploadApi.FailureCode == "REMOTE_REQUEST_REJECTED",
            "deterministic /upload 4xx was retried instead of being reported once");
    }

    var completionApi = new ControlledOperationApi(new ClaimedFileOperation
    {
        Id = Guid.NewGuid(), Operation = "LIST", Path = testRoot,
        LeaseToken = "completion-rejection", LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    }) { RejectCompletion = true };
    using (var worker = new ProductionFileOperationWorker(
        completionApi,
        new DirectoryListingService(),
        new FileTransferPreparer(new WindowsPathService(), Path.Combine(testRoot, "rejected-completion-artifacts")),
        new ManagedTerminalLauncher(new WindowsPathService(), new RecordingProcessStarter(), () => null, stub, stub, stub),
        CancellationToken.None))
    {
        worker.Start();
        await completionApi.FailureRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(completionApi.CompletionAttempts == 1 &&
            completionApi.FailureCode == "SERVER_REJECTED_RESULT",
            "deterministic completion 422 was retried until lease expiry");
    }
}

static void ValidateTerminalLaunch(string testRoot)
{
    var stub = Path.Combine(testRoot, "terminal-stub.exe");
    File.WriteAllBytes(stub, new byte[] { 0x4d, 0x5a });
    var settingsPath = Path.Combine(testRoot, "terminal-commands.json");
    AppContext.SetData(TerminalCommandProfileStore.StoragePathAppContextKey, settingsPath);
    var starts = new RecordingProcessStarter { FailFirst = true };
    var launcher = new ManagedTerminalLauncher(
        new WindowsPathService(), starts, () => stub, stub, stub, stub, stub, stub);
    var sessionId = Guid.NewGuid();
    Require(launcher.Launch("windows-powershell", testRoot, sessionId) == sessionId,
        "terminal launcher did not preserve the allocated session ID");
    Require(starts.Starts.Count == 2 && starts.Starts[0].FileName == stub && starts.Starts[1].FileName == stub,
        "terminal launcher did not fall back after Windows Terminal launch failure");
    Require(starts.Starts[0].ArgumentList.Contains("--elevate"),
        "an administrator-enabled command did not retain the explicit Windows Terminal elevation path");
    var inheritedColorEnvironment = new System.Diagnostics.ProcessStartInfo();
    inheritedColorEnvironment.Environment["NO_COLOR"] = "1";
    inheritedColorEnvironment.Environment["TERM"] = "dumb";
    inheritedColorEnvironment.Environment["COLORTERM"] = string.Empty;
    ManagedTerminalLauncher.SanitizeTerminalEnvironment(inheritedColorEnvironment);
    Require(!inheritedColorEnvironment.Environment.ContainsKey("NO_COLOR") &&
        !inheritedColorEnvironment.Environment.ContainsKey("TERM") &&
        !inheritedColorEnvironment.Environment.ContainsKey("COLORTERM"),
        "managed terminal launch retained inherited color-suppression variables");
    inheritedColorEnvironment.Environment["TERM"] = "xterm-256color";
    inheritedColorEnvironment.Environment["COLORTERM"] = "truecolor";
    ManagedTerminalLauncher.SanitizeTerminalEnvironment(inheritedColorEnvironment);
    Require(inheritedColorEnvironment.Environment["TERM"] == "xterm-256color" &&
        inheritedColorEnvironment.Environment["COLORTERM"] == "truecolor",
        "managed terminal launch removed valid color capability declarations");
    var direct = starts.Starts[1];
    Require(string.Equals(direct.WorkingDirectory, Path.GetFullPath(testRoot), StringComparison.OrdinalIgnoreCase),
        "terminal launcher did not preserve the exact working directory");
    Require(direct.ArgumentList.Contains("--managed-session-id") &&
        direct.ArgumentList.Contains("--working-directory-base64") &&
        direct.ArgumentList.Contains("--registration-timeout-ms"),
        "terminal launcher omitted managed SessionHost identity or working-directory arguments");
    Require(direct.UseShellExecute && direct.Verb == "runas",
        "default terminal launch did not request administrator authorization");
    var registrationTimeoutIndex = direct.ArgumentList.IndexOf("--registration-timeout-ms");
    Require(direct.ArgumentList[registrationTimeoutIndex + 1] == "30000",
        "terminal launcher did not preserve the extended registration window");
    var commandIndex = direct.ArgumentList.IndexOf("--command-base64");
    var decodedCommand = Encoding.UTF8.GetString(Convert.FromBase64String(direct.ArgumentList[commandIndex + 1]));
    Require(decodedCommand.Contains("-NoLogo", StringComparison.Ordinal) &&
        !decodedCommand.Contains("-NoProfile", StringComparison.Ordinal),
        "Windows PowerShell launch did not use the profile-loading default");
    launcher.Launch("powershell-7", testRoot);
    var powerShell7Start = starts.Starts.Last();
    var powerShell7CommandIndex = powerShell7Start.ArgumentList.IndexOf("--command-base64");
    var powerShell7Command = Encoding.UTF8.GetString(
        Convert.FromBase64String(powerShell7Start.ArgumentList[powerShell7CommandIndex + 1]));
    Require(powerShell7Command.Contains("-NoLogo", StringComparison.Ordinal) &&
        !powerShell7Command.Contains("-NoProfile", StringComparison.Ordinal),
        "PowerShell 7 launch did not use the profile-loading default");
    launcher.Launch("wsl", testRoot);
    var wslStart = starts.Starts.Last();
    var wslCommandIndex = wslStart.ArgumentList.IndexOf("--command-base64");
    var wslCommand = Encoding.UTF8.GetString(
        Convert.FromBase64String(wslStart.ArgumentList[wslCommandIndex + 1]));
    Require(!wslCommand.Contains("-NoLogo", StringComparison.Ordinal) && wslCommand.Contains(stub, StringComparison.Ordinal),
        "WSL launch received PowerShell-only flags or lost its executable");
    RequireFileOperationCode(() => launcher.Launch("unknown-shell", testRoot), "SHELL_NOT_ALLOWED");

    try
    {
        var custom = TerminalCommandProfileStore.CreateManualProfile(
            null, "Project CLI", stub, "--project alpha --interactive", true, false);
        TerminalCommandProfileStore.SaveManualProfiles(new[] { custom });
        var roundTrip = TerminalCommandProfileStore.LoadManualProfiles().Single();
        Require(roundTrip.Id == custom.Id && roundTrip.DisplayName == "Project CLI" &&
            roundTrip.Arguments == "--project alpha --interactive" &&
            roundTrip.RunAsAdministrator,
            "manual terminal command settings did not enforce administrator launch");
        var customStarts = new RecordingProcessStarter();
        var customLauncher = new ManagedTerminalLauncher(
            new WindowsPathService(), customStarts, () => null, stub, stub, stub, stub, stub);
        customLauncher.Launch(custom.Id, testRoot);
        var customStart = customStarts.Starts.Single();
        var customCommandIndex = customStart.ArgumentList.IndexOf("--command-base64");
        var customCommand = Encoding.UTF8.GetString(
            Convert.FromBase64String(customStart.ArgumentList[customCommandIndex + 1]));
        var managedShellIndex = customStart.ArgumentList.IndexOf("--managed-shell");
        Require(customCommand.Contains("--project alpha --interactive", StringComparison.Ordinal) &&
            customStart.ArgumentList[managedShellIndex + 1] == "custom" &&
            customStart.Verb == "runas",
            "custom terminal command was not resolved, elevated, and advertised as a managed custom shell");

        var customFallbackStarts = new RecordingProcessStarter { FailFirst = true };
        var customFallbackLauncher = new ManagedTerminalLauncher(
            new WindowsPathService(), customFallbackStarts, () => stub, stub, stub, stub, stub, stub);
        customFallbackLauncher.Launch(custom.Id, testRoot);
        Require(customFallbackStarts.Starts.Count == 2 &&
                customFallbackStarts.Starts[0].ArgumentList.Contains("--elevate") &&
                customFallbackStarts.Starts[1].Verb == "runas",
            "a custom command did not enforce elevation for Windows Terminal and SessionHost fallback");

        var detectedOverride = TerminalCommandProfileStore.CreateConfiguredProfile(
            "windows-powershell", "PowerShell hidden", stub, "-NoLogo", "windows-powershell",
            true, false, false);
        TerminalCommandProfileStore.SaveConfiguredProfiles(new[] { custom, detectedOverride });
        var configuredRoundTrip = TerminalCommandProfileStore.LoadConfiguredProfiles();
        Require(configuredRoundTrip.Count == 2 &&
            configuredRoundTrip.Single(profile => profile.Id == "windows-powershell").Enabled == false &&
            configuredRoundTrip.All(profile => profile.RunAsAdministrator),
            "a command override was not persisted with the fixed administrator policy");
        var overriddenLauncher = new ManagedTerminalLauncher(
            new WindowsPathService(), new RecordingProcessStarter(), () => null, stub, stub, stub, stub, stub);
        RequireFileOperationCode(
            () => overriddenLauncher.Launch("windows-powershell", testRoot),
            "SHELL_NOT_ALLOWED");

        File.WriteAllText(settingsPath,
            "[{\"id\":\"legacy-profile\",\"display_name\":\"Legacy\",\"executable_path\":" +
            JsonSerializer.Serialize(stub) +
            ",\"arguments\":\"\",\"managed_shell\":\"custom\",\"source\":\"manual\",\"enabled\":true}]",
            Encoding.UTF8);
        Require(TerminalCommandProfileStore.LoadManualProfiles().Single().RunAsAdministrator,
            "legacy terminal-command JSON did not default administrator launch to true");

        File.WriteAllText(settingsPath, "not-json", Encoding.UTF8);
        Require(TerminalCommandProfileStore.LoadManualProfiles().Count == 0,
            "corrupt terminal command settings were not rejected fail-closed");
    }
    finally
    {
        AppContext.SetData(TerminalCommandProfileStore.StoragePathAppContextKey, null);
    }
}

static async Task ValidateApiContractAsync()
{
    var operationId = Guid.NewGuid();
    var requesterDeviceId = Guid.NewGuid();
    var requests = new List<(string Path, string Body, string IdempotencyKey)>();
    var responses = new Queue<HttpResponseMessage>(new[]
    {
        JsonResponse("{\"file_operation\":{\"id\":\"" + operationId.ToString("D") +
            "\",\"operation\":\"DOWNLOAD\",\"path\":\"C:\\\\empty.bin\",\"offset\":null," +
            "\"shell_type\":null,\"requester_device_id\":\"" + requesterDeviceId.ToString("D") +
            "\"},\"lease_token\":\"lease\"}"),
        JsonResponse("{\"transfer\":{\"protocol\":\"REALTIME_V2\",\"transfer_id\":\"" +
            operationId.ToString("D") + "\",\"requester_device_id\":\"" + requesterDeviceId.ToString("D") + "\"}}"),
        JsonResponse("{\"file_operation\":{\"lease_expires_at\":\"2030-01-01T01:00:00Z\"}}"),
        JsonResponse("{}"),
        JsonResponse("{}"),
        JsonResponse("{}"),
        JsonResponse("{}"),
        JsonResponse("{}"),
        new HttpResponseMessage(HttpStatusCode.NoContent)
    });
    Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        byte[] body,
        bool deviceProof,
        string idempotencyKey,
        CancellationToken token)
    {
        requests.Add((path, Encoding.UTF8.GetString(body), idempotencyKey));
        return Task.FromResult(responses.Dequeue());
    }

    using var uploadHttp = new HttpClient(new ContractHandler());
    var transferSender = new RecordingFileTransferSender();
    var api = new ProductionFileOperationApi(Guid.NewGuid(), uploadHttp, Send, transferSender);
    var claimed = await api.ClaimAsync(CancellationToken.None);
    Require(claimed.Id == operationId && claimed.Operation == "DOWNLOAD" && claimed.LeaseToken == "lease",
        "claim did not combine file_operation with the top-level lease_token");
    await using var emptyArtifact = new FileTransferArtifact(
        "empty.bin", false, 0, SHA256.HashData(Array.Empty<byte>()), new MemoryStream(), null);
    var upload = await api.CreateUploadAsync(claimed, emptyArtifact, CancellationToken.None);
    Require(upload.TransferId == operationId && upload.RequesterDeviceId == requesterDeviceId &&
        upload.Protocol == "REALTIME_V2",
        "upload did not parse the realtime transfer contract");
    using (var uploadContent = new MemoryStream())
        await api.UploadAsync(upload, uploadContent, 0, CancellationToken.None);
    Require(transferSender.Calls == 1 && transferSender.Bytes.Length == 0,
        "realtime transfer sender was not invoked exactly once");
    Require(await api.RenewAsync(claimed, CancellationToken.None) ==
        DateTimeOffset.Parse("2030-01-01T01:00:00Z"),
        "renew did not parse the revised lease expiry");
    await api.CompleteAsync(claimed, null, CancellationToken.None);
    var terminalOperation = new ClaimedFileOperation
    {
        Id = Guid.NewGuid(),
        Operation = "OPEN_TERMINAL",
        Path = @"C:\",
        ShellType = "command-prompt",
        LeaseToken = "terminal-lease"
    };
    await api.CompleteAsync(terminalOperation, JsonSerializer.SerializeToElement(new
    {
        session_id = Guid.NewGuid(),
        shell_type = "command-prompt",
        cwd = terminalOperation.Path
    }), CancellationToken.None);
    var failedOperation = new ClaimedFileOperation
    {
        Id = Guid.NewGuid(),
        Operation = "DOWNLOAD",
        Path = @"C:\missing.bin",
        LeaseToken = "failure-lease",
        RequesterDeviceId = Guid.NewGuid()
    };
    await api.FailAsync(failedOperation, "PATH_NOT_FOUND", "The requested path does not exist.", CancellationToken.None);
    await api.CompleteAsync(claimed, null, CancellationToken.None);
    var renewedLease = new ClaimedFileOperation
    {
        Id = claimed.Id,
        Operation = claimed.Operation,
        Path = claimed.Path,
        LeaseToken = "renewed-lease"
    };
    await api.CompleteAsync(renewedLease, null, CancellationToken.None);
    Require(await api.ClaimAsync(CancellationToken.None) == null,
        "204 claim response did not represent an empty queue");

    using (var metadata = JsonDocument.Parse(requests[1].Body))
    {
        Require(metadata.RootElement.GetProperty("size_bytes").GetInt64() == 0,
            "zero-byte download metadata was rejected or changed");
        Require(metadata.RootElement.GetProperty("sha256").GetString() ==
            Convert.ToBase64String(SHA256.HashData(Array.Empty<byte>())).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            "upload metadata SHA-256 was not base64url encoded");
    }
    using (var renewal = JsonDocument.Parse(requests[2].Body))
    {
        Require(requests[2].Path.EndsWith("/renew", StringComparison.Ordinal) &&
            renewal.RootElement.GetProperty("lease_token").GetString() == "lease",
            "lease renewal did not use the exact POST contract");
    }
    using (var completed = JsonDocument.Parse(requests[3].Body))
    {
        Require(completed.RootElement.GetProperty("status").GetString() == "SUCCEEDED" &&
            !completed.RootElement.TryGetProperty("result", out _),
            "download completion did not use SUCCEEDED without result");
    }
    using (var terminal = JsonDocument.Parse(requests[4].Body))
    {
        var result = terminal.RootElement.GetProperty("result");
        Require(terminal.RootElement.GetProperty("status").GetString() == "SUCCEEDED" &&
            result.TryGetProperty("session_id", out _) &&
            result.GetProperty("shell_type").GetString() == "command-prompt" &&
            result.GetProperty("cwd").GetString() == terminalOperation.Path,
            "terminal completion result shape was inaccurate");
    }
    using (var failed = JsonDocument.Parse(requests[5].Body))
    {
        Require(failed.RootElement.GetProperty("status").GetString() == "FAILED" &&
            failed.RootElement.GetProperty("error").GetString()!.StartsWith("PATH_NOT_FOUND:", StringComparison.Ordinal),
            "failure completion did not use stable FAILED error text");
    }
    Require(requests[5].Path.EndsWith("/complete", StringComparison.Ordinal),
        "failure used an endpoint other than /complete");
    Require(Guid.TryParse(requests[0].IdempotencyKey, out _),
        "claim POST did not carry a unique idempotency key");
    Require(requests[3].IdempotencyKey == requests[6].IdempotencyKey,
        "same-lease completion retry did not preserve its idempotency key");
    Require(requests[3].IdempotencyKey != requests[7].IdempotencyKey,
        "renewed lease reused the previous lease's completion idempotency key");
    Require(requests[0].IdempotencyKey != requests[8].IdempotencyKey,
        "separate claim attempts reused an idempotency key");
    Require(requests.All(request => request.IdempotencyKey.Length is >= 8 and <= 128),
        "a file-operation POST used an idempotency key outside the server's 8-128 byte contract");
    Require(!requests.Any(request => request.IdempotencyKey.Contains("lease", StringComparison.Ordinal)),
        "a lease-bound idempotency key exposed the lease token");

    var rejectedResponses = new Queue<HttpResponseMessage>(new[]
    {
        new HttpResponseMessage(HttpStatusCode.BadRequest),
        new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
    });
    Task<HttpResponseMessage> Reject(
        HttpMethod method,
        string path,
        byte[] body,
        bool deviceProof,
        string idempotencyKey,
        CancellationToken token) => Task.FromResult(rejectedResponses.Dequeue());
    var rejectionApi = new ProductionFileOperationApi(Guid.NewGuid(), uploadHttp, Reject);
    await RequireRemoteNonRetryableAsync(
        async () => { await rejectionApi.CreateUploadAsync(claimed, emptyArtifact, CancellationToken.None); },
        "/upload HTTP 400 was classified as transient");
    await RequireRemoteNonRetryableAsync(
        () => rejectionApi.CompleteAsync(claimed, null, CancellationToken.None),
        "completion integrity HTTP 422 was classified as transient");
}

static async Task ValidateClaimAmbiguityAsync()
{
    var requests = new List<string>();
    var attempt = 0;
    Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        byte[] body,
        bool deviceProof,
        string idempotencyKey,
        CancellationToken token)
    {
        requests.Add(idempotencyKey);
        if (Interlocked.Increment(ref attempt) == 1)
            throw new HttpRequestException("response lost after server claim");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    using var uploadHttp = new HttpClient(new ContractHandler());
    var api = new ProductionFileOperationApi(Guid.NewGuid(), uploadHttp, Send);
    try { await api.ClaimAsync(CancellationToken.None); }
    catch (HttpRequestException) { }
    Require(await api.ClaimAsync(CancellationToken.None) == null &&
        requests.Count == 2 && requests[0] == requests[1],
        "ambiguous claim retry changed idempotency key and could strand the first lease");
    Require(await api.ClaimAsync(CancellationToken.None) == null && requests[2] != requests[1],
        "confirmed claim outcome did not advance to a fresh idempotency key");
}

static async Task ValidateClaimBodyAmbiguityAsync()
{
    var keys = new List<string>();
    var attempt = 0;
    Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        byte[] body,
        bool deviceProof,
        string idempotencyKey,
        CancellationToken token)
    {
        keys.Add(idempotencyKey);
        if (Interlocked.Increment(ref attempt) == 1)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new FaultingHttpContent()
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    using var uploadHttp = new HttpClient(new ContractHandler());
    var api = new ProductionFileOperationApi(Guid.NewGuid(), uploadHttp, Send);
    try { await api.ClaimAsync(CancellationToken.None); }
    catch (HttpRequestException) { }
    catch (IOException) { }
    Require(await api.ClaimAsync(CancellationToken.None) == null &&
        keys.Count == 2 && keys[0] == keys[1],
        "a successful claim with an incomplete response body did not preserve its ambiguous idempotency key");
}

static async Task ValidateClaimInProgressRecoveryAsync()
{
    var operationId = Guid.NewGuid();
    var keys = new List<string>();
    var attempt = 0;
    Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        byte[] body,
        bool deviceProof,
        string idempotencyKey,
        CancellationToken token)
    {
        keys.Add(idempotencyKey);
        attempt++;
        if (attempt <= 8)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"IDEMPOTENCY_IN_PROGRESS\"}}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
        if (attempt == 9)
        {
            return Task.FromResult(JsonResponse(
                "{\"file_operation\":{\"id\":\"" + operationId.ToString("D") +
                "\",\"operation\":\"LIST\",\"path\":\"\",\"offset\":0}," +
                "\"lease_token\":\"recovered-lease\"}"));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    using var uploadHttp = new HttpClient(new ContractHandler());
    var api = new ProductionFileOperationApi(Guid.NewGuid(), uploadHttp, Send);
    for (var index = 0; index < 8; index++)
    {
        try { await api.ClaimAsync(CancellationToken.None); }
        catch (FileOperationRemoteException error) when (error.Retryable) { continue; }
        throw new InvalidOperationException("IDEMPOTENCY_IN_PROGRESS was not retryable.");
    }
    var recovered = await api.ClaimAsync(CancellationToken.None);
    Require(recovered.Id == operationId &&
        keys.Take(4).Distinct().Count() == 1 &&
        keys.Skip(4).Take(4).Distinct().Count() == 1 &&
        keys[0] != keys[4] && keys[4] != keys[8],
        "claim did not bound same-key IDEMPOTENCY_IN_PROGRESS retries before safely rotating");
    Require(await api.ClaimAsync(CancellationToken.None) == null && keys[9] != keys[8],
        "recovered claim did not advance to a fresh queue idempotency key");
}

static async Task ValidateClaimDefinitiveRejectionRecoveryAsync()
{
    var keys = new List<string>();
    var attempt = 0;
    Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        byte[] body,
        bool deviceProof,
        string idempotencyKey,
        CancellationToken token)
    {
        keys.Add(idempotencyKey);
        if (Interlocked.Increment(ref attempt) == 1)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"PC_NOT_FOUND\"}}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    using var uploadHttp = new HttpClient(new ContractHandler());
    var api = new ProductionFileOperationApi(Guid.NewGuid(), uploadHttp, Send);
    try { await api.ClaimAsync(CancellationToken.None); }
    catch (FileOperationRemoteException error) when (!error.Retryable) { }
    Require(await api.ClaimAsync(CancellationToken.None) == null &&
        keys.Count == 2 && keys[0] != keys[1],
        "a definitive HTTP 403 claim rejection pinned the file worker to the cached idempotency result");
}

static async Task ValidateClaimInvalidResponseRecoveryAsync()
{
    var keys = new List<string>();
    var attempt = 0;
    Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        byte[] body,
        bool deviceProof,
        string idempotencyKey,
        CancellationToken token)
    {
        keys.Add(idempotencyKey);
        if (Interlocked.Increment(ref attempt) == 1)
            return Task.FromResult(JsonResponse("{}"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    using var uploadHttp = new HttpClient(new ContractHandler());
    var api = new ProductionFileOperationApi(Guid.NewGuid(), uploadHttp, Send);
    try { await api.ClaimAsync(CancellationToken.None); }
    catch (FileOperationRemoteException) { }
    Require(await api.ClaimAsync(CancellationToken.None) == null &&
        keys.Count == 2 && keys[0] != keys[1],
        "a malformed successful claim response pinned the file worker to the cached response");
}

static async Task RequireRemoteNonRetryableAsync(Func<Task> action, string message)
{
    try { await action(); }
    catch (FileOperationRemoteException error) when (!error.Retryable) { return; }
    throw new InvalidOperationException(message);
}

static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RequireFileOperationCode(Action action, string code)
{
    try { action(); }
    catch (FileOperationException error) when (error.Code == code) { return; }
    throw new InvalidOperationException("Expected file-operation error " + code + ".");
}

static async Task RequireFileOperationCodeAsync(Func<Task> action, string code)
{
    try { await action(); }
    catch (FileOperationException error) when (error.Code == code) { return; }
    throw new InvalidOperationException("Expected file-operation error " + code + ".");
}

sealed class RecordingProcessStarter : IProcessStarter
{
    internal bool FailFirst { get; set; }
    internal List<System.Diagnostics.ProcessStartInfo> Starts { get; } = new();

    public bool TryStart(System.Diagnostics.ProcessStartInfo startInfo)
    {
        return Record(startInfo);
    }

    private bool Record(System.Diagnostics.ProcessStartInfo startInfo)
    {
        Starts.Add(startInfo);
        if (FailFirst && Starts.Count == 1) return false;
        return true;
    }
}

sealed class FaultingHttpContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
        Task.FromException(new IOException("simulated response body transport failure"));

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

sealed class RemotePolicy
{
    private int enabled;
    internal RemotePolicy(bool enabled) => this.enabled = enabled ? 1 : 0;
    internal bool Enabled
    {
        get => Volatile.Read(ref enabled) != 0;
        set => Volatile.Write(ref enabled, value ? 1 : 0);
    }
}

sealed class ControlledOperationApi : IFileOperationApi
{
    private ClaimedFileOperation operation;
    internal ControlledOperationApi(ClaimedFileOperation operation)
    {
        this.operation = operation;
        OperationId = operation.Id;
    }
    internal Guid OperationId { get; }
    internal bool LoseLeaseOnRenew { get; set; }
    internal bool RejectUploadAllocation { get; set; }
    internal bool RejectCompletion { get; set; }
    internal bool RejectCompletionAsCancelled { get; set; }
    internal bool BlockCompletion { get; set; }
    internal bool AmbiguousCompletionOnce { get; set; }
    internal bool RenewConflictsAfterCompletionCommit { get; set; }
    internal bool DelayVisibilityUntilRenew { get; set; }
    internal int UploadAllocationAttempts;
    internal int CompletionAttempts;
    internal int ClaimCount;
    internal bool CompletedOperation { get; private set; }
    internal string FailureCode { get; private set; }
    internal JsonElement? CompletionResult { get; private set; }
    internal TaskCompletionSource<bool> UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> UploadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> RenewAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> FailureRecorded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> CompletionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> TerminalOutcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> CompletionCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> RenewConflictReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static ControlledOperationApi Terminal(string path) => new(new ClaimedFileOperation
    {
        Id = Guid.NewGuid(),
        Operation = "OPEN_TERMINAL",
        Path = path,
        ShellType = "command-prompt",
        LeaseToken = "terminal-cleanup-lease",
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
    });

    public async Task<ClaimedFileOperation> ClaimAsync(CancellationToken token)
    {
        Interlocked.Increment(ref ClaimCount);
        var claimed = Interlocked.Exchange(ref operation, null);
        if (claimed != null) return claimed;
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return null;
    }

    public Task<FileOperationUploadTarget> CreateUploadAsync(
        ClaimedFileOperation operation,
        FileTransferArtifact artifact,
        CancellationToken token)
    {
        Interlocked.Increment(ref UploadAllocationAttempts);
        if (RejectUploadAllocation)
            throw new FileOperationRemoteException("HTTP 400", false);
        return Task.FromResult(new FileOperationUploadTarget
        {
            TransferId = operation.Id,
            RequesterDeviceId = operation.RequesterDeviceId,
            Protocol = "REALTIME_V2",
            Filename = artifact.Filename,
            DirectoryZip = artifact.DirectoryZip,
            SizeBytes = artifact.SizeBytes,
            Sha256 = artifact.Sha256.ToArray()
        });
    }

    public async Task UploadAsync(
        FileOperationUploadTarget target,
        Stream content,
        long sizeBytes,
        CancellationToken token)
    {
        UploadStarted.TrySetResult(true);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            UploadCancelled.TrySetResult(true);
            throw;
        }
    }

    public async Task<DateTimeOffset?> RenewAsync(ClaimedFileOperation operation, CancellationToken token)
    {
        RenewAttempted.TrySetResult(true);
        if (RenewConflictsAfterCompletionCommit)
        {
            await CompletionCommitted.Task.WaitAsync(token);
            RenewConflictReturned.TrySetResult(true);
            throw new FileOperationLeaseLostException("completion already committed");
        }
        if (LoseLeaseOnRenew)
        {
            TerminalOutcome.TrySetResult(true);
            throw new FileOperationLeaseLostException("cancelled by Android");
        }
        return DateTimeOffset.UtcNow.AddHours(1);
    }

    public async Task CompleteAsync(ClaimedFileOperation operation, JsonElement? result, CancellationToken token)
    {
        Interlocked.Increment(ref CompletionAttempts);
        CompletionStarted.TrySetResult(true);
        if (BlockCompletion)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                TerminalOutcome.TrySetResult(true);
                throw;
            }
        }
        if (AmbiguousCompletionOnce)
        {
            AmbiguousCompletionOnce = false;
            CompletionCommitted.TrySetResult(true);
            throw new FileOperationRemoteException("completion response was lost", true);
        }
        if (RejectCompletionAsCancelled)
        {
            TerminalOutcome.TrySetResult(true);
            throw new FileOperationRemoteException("HTTP 409 operation cancelled", false);
        }
        if (RejectCompletion)
            throw new FileOperationRemoteException("HTTP 422", false);
        CompletedOperation = true;
        CompletionResult = result?.Clone();
        Completed.TrySetResult(true);
        TerminalOutcome.TrySetResult(true);
    }

    public Task FailAsync(
        ClaimedFileOperation operation,
        string errorCode,
        string errorMessage,
        CancellationToken token)
    {
        if (RejectCompletionAsCancelled)
            throw new FileOperationRemoteException("HTTP 409 operation cancelled", false);
        FailureCode = errorCode;
        FailureRecorded.TrySetResult(true);
        TerminalOutcome.TrySetResult(true);
        return Task.CompletedTask;
    }
}

sealed class RecoveringClaimApi : IFileOperationApi
{
    private Exception firstFailure;
    private ClaimedFileOperation operation;

    internal RecoveringClaimApi(Exception firstFailure, string path)
    {
        this.firstFailure = firstFailure;
        operation = new ClaimedFileOperation
        {
            Id = Guid.NewGuid(),
            Operation = "LIST",
            Path = path,
            LeaseToken = "recovery-lease",
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
    }

    internal TaskCompletionSource<bool> Completed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> ClaimWaiting { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ClaimedFileOperation> ClaimAsync(CancellationToken token)
    {
        var failure = Interlocked.Exchange(ref firstFailure, null);
        if (failure != null) throw failure;
        var claimed = Interlocked.Exchange(ref operation, null);
        if (claimed != null) return claimed;
        ClaimWaiting.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return null;
    }

    public Task<FileOperationUploadTarget> CreateUploadAsync(
        ClaimedFileOperation operation,
        FileTransferArtifact artifact,
        CancellationToken token) => throw new NotSupportedException();

    public Task UploadAsync(
        FileOperationUploadTarget target,
        Stream content,
        long sizeBytes,
        CancellationToken token) => throw new NotSupportedException();

    public Task<DateTimeOffset?> RenewAsync(ClaimedFileOperation operation, CancellationToken token) =>
        Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddHours(1));

    public Task CompleteAsync(ClaimedFileOperation operation, JsonElement? result, CancellationToken token)
    {
        Completed.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task FailAsync(
        ClaimedFileOperation operation,
        string errorCode,
        string errorMessage,
        CancellationToken token) => throw new InvalidOperationException(errorCode + ": " + errorMessage);
}

sealed class RecordingApi : IFileOperationApi
{
    private ClaimedFileOperation operation;
    internal RecordingApi(ClaimedFileOperation operation) => this.operation = operation;
    internal TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> ClaimWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal byte[] UploadedBytes { get; private set; } = Array.Empty<byte>();
    internal int MaximumReadRequest { get; private set; }
    internal bool CompletedOperation { get; private set; }
    internal bool ClaimCancelled { get; private set; }

    public async Task<ClaimedFileOperation> ClaimAsync(CancellationToken token)
    {
        var claimed = Interlocked.Exchange(ref operation, null);
        if (claimed != null) return claimed;
        ClaimWaiting.TrySetResult(true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ClaimCancelled = true;
            throw;
        }
    }

    public Task<FileOperationUploadTarget> CreateUploadAsync(
        ClaimedFileOperation operation,
        FileTransferArtifact artifact,
        CancellationToken token) => Task.FromResult(new FileOperationUploadTarget
    {
        TransferId = operation.Id,
        RequesterDeviceId = operation.RequesterDeviceId,
        Protocol = "REALTIME_V2",
        Filename = artifact.Filename,
        DirectoryZip = artifact.DirectoryZip,
        SizeBytes = artifact.SizeBytes,
        Sha256 = artifact.Sha256.ToArray()
    });

    public Task<DateTimeOffset?> RenewAsync(ClaimedFileOperation operation, CancellationToken token) =>
        Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddHours(1));

    public async Task UploadAsync(
        FileOperationUploadTarget target,
        Stream content,
        long sizeBytes,
        CancellationToken token)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
            var read = await content.ReadAsync(buffer, token);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
        UploadedBytes = destination.ToArray();
    }

    public Task CompleteAsync(ClaimedFileOperation operation, JsonElement? result, CancellationToken token)
    {
        CompletedOperation = true;
        Completed.TrySetResult(true);
        return Task.CompletedTask;
    }

    public Task FailAsync(
        ClaimedFileOperation operation,
        string errorCode,
        string errorMessage,
        CancellationToken token) => throw new InvalidOperationException(errorCode + ": " + errorMessage);
}

sealed class RecordingFileTransferSender : IFileTransferSender
{
    internal int Calls { get; private set; }
    internal byte[] Bytes { get; private set; } = Array.Empty<byte>();

    public async Task SendAsync(
        FileOperationUploadTarget target,
        Stream content,
        CancellationToken token)
    {
        Calls++;
        using var output = new MemoryStream();
        await content.CopyToAsync(output, token);
        Bytes = output.ToArray();
    }
}

sealed class ContractHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Require(request.Method == HttpMethod.Put &&
            request.Headers.TryGetValues("x-object-token", out var values) &&
            values.SequenceEqual(new[] { "value", "second" }),
            "presigned upload method or every header value was not followed");
        await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
