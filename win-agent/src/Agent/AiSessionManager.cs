using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Ai;
using TermLink.UpdateCore;

namespace TermLink.Agent;

// Separate from ConPTY: no AI operation can become terminal input, ACK, or resize.
internal sealed class AiSessionManager : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    internal readonly string HostId = Guid.NewGuid().ToString("D");
    private readonly object sync = new();
    private readonly Dictionary<string, AiAgentSession> sessions = new();
    private readonly Dictionary<(Guid, string), (string Body, Task<string> Result)> operations = new();
    private bool disposed;
    private long receiptBytes;
    private readonly ZcodeRemoteAddressStore remoteStore;
    private readonly string workspaceRoot;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim modelQueries = new(2, 2);
    private readonly AiSessionArchive archive;
    private bool archiveLoaded;
    internal (DesktopAiSessionSummary[] Items, int Active, bool Truncated) DesktopSummaries() {
        lock (sync) {
            var all = sessions.Values.Select(s => s.DesktopSummary()).ToArray();
            var items = new List<DesktopAiSessionSummary>(); var bytes = 0;
            foreach (var item in all) {
                var length = System.Text.Encoding.UTF8.GetByteCount(item.Id + item.Title + item.Agent + item.Directory + item.State) + 32;
                if (bytes + length > 12000 || items.Count == 64) continue;
                bytes += length; items.Add(item);
            }
            return (items.ToArray(), all.Count(s => s.State is "starting" or "running" or "awaiting_input"), items.Count != all.Length);
        }
    }
    private readonly Dictionary<(Guid Caller, string Agent, string NativeId), (AiNativeHistoryEntry Entry, DateTime At)> nativeHistory = new();
    internal AiSessionManager(ZcodeRemoteAddressStore remoteStore = null, string installRoot = null, string archiveRoot = null) {
        this.remoteStore = remoteStore ?? ZcodeRemoteAddressStore.Default;
        workspaceRoot = AiWorkspaceStorage.Under(installRoot ?? AppContext.BaseDirectory);
        if (archiveRoot != null) archive = new AiSessionArchive(archiveRoot);
    }

    internal sealed record Request(string Action, string HostId = "", string OperationId = "", string SessionId = "",
        string AgentId = "", string Directory = "", string Text = "", string RequestId = "", string Decision = "",
        long ControlVersion = 0, Dictionary<string, string[]> Answers = null, string ModelId = "", string ProviderId = "",
        string DirectoryMode = "", long Offset = 0, long ExpectedSettingsVersion = 0, string PermissionMode = "ask",
        string AttachmentId = "", string FileName = "", string Mime = "", long Size = 0, string Sha256 = "", string Data = "", string[] AttachmentIds = null, string[] FileReferences = null,
        string ReasoningEffort = null, string ConfirmedPermissionMode = "", string StreamId = "", string Cursor = "0");

    internal async Task<string> ExecuteAsync(Guid caller, string body, CancellationToken token, Func<bool> authorized = null)
    {
        if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
        if (body == null || System.Text.Encoding.UTF8.GetByteCount(body) > 48 * 1024) return Error("REQUEST_TOO_LARGE");
        Request request;
        try { request = JsonSerializer.Deserialize<Request>(body, Json) ?? throw new JsonException(); }
        catch (JsonException) { return Error("INVALID_REQUEST"); }
        if (request.Action == null || request.HostId == null || request.SessionId == null || request.AgentId == null ||
            request.Directory == null || request.Text == null || request.RequestId == null || request.Decision == null)
            return Error("INVALID_REQUEST");
        if (request.Action == "catalog") {
            var agents = await AiAgentDiscovery.CatalogAsync(remoteStore: remoteStore).WaitAsync(token).ConfigureAwait(false);
            lock (sync) {
                if (disposed) return Error("HOST_RETIRED");
                if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                if (!archiveLoaded) {
                    archiveLoaded = true;
                    foreach (var record in archive?.Read() ?? Array.Empty<AiSessionArchive.Record>()) {
                        var agent = agents.FirstOrDefault(a => a.Id == record.AgentId && a.Strategy == "native");
                        if (agent != null && !sessions.ContainsKey(record.Id)) sessions.Add(record.Id, AiAgentSession.Restore(agent, record, archive));
                    }
                }
                return Encode(new { hostId = HostId, agents = agents.Select(a => new { a.Id, a.Name, a.Path, a.RuntimePath, a.Version, a.Protocol, a.Status, a.Strategy,
                    nativeHistory = AiAgentSession.SupportsNativeHistory(a), configuration = AiAgentSession.ConfigurationFor(a) }).ToArray(), defaultDirectory = workspaceRoot, directoryMode = "per_session", directoryBrowsing = true, modelListing = true,
                    cliWorkspaceRoot = workspaceRoot,
                    configurationModes = agents.Where(a => a.Strategy == "native").ToDictionary(a => a.Id, a => AiAgentSession.Modes(a.Protocol)),
                    sessions = sessions.Values.Select(s => s.Snapshot(caller, false)).ToArray() });
            }
        }
        if (request.Action == "native-history") {
            lock (sync) { if (disposed || request.HostId != HostId) return Error("HOST_CHANGED"); }
            if (request.Text.Length > 4096) return Error("INVALID_CURSOR");
            if (!await modelQueries.WaitAsync(0, token)) return Error("MODEL_LIST_BUSY");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try {
                var installation = (await AiAgentDiscovery.CatalogAsync(remoteStore: remoteStore).WaitAsync(linked.Token)).FirstOrDefault(a => a.Id == request.AgentId);
                if (installation == null) return Error("AGENT_UNAVAILABLE");
                var page = await AiAgentSession.NativeHistoryAsync(installation, request.Text, linked.Token);
                lock (sync) {
                    if (disposed || authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    foreach (var key in nativeHistory.Where(p => p.Value.At < DateTime.UtcNow.AddMinutes(-10)).Select(p => p.Key).ToArray()) nativeHistory.Remove(key);
                    foreach (var entry in page.Sessions) {
                        if (nativeHistory.Count >= 512) nativeHistory.Remove(nativeHistory.Keys.First());
                        nativeHistory[(caller, request.AgentId, entry.NativeId)] = (entry, DateTime.UtcNow);
                    }
                    return Encode(new { hostId = HostId, agentId = request.AgentId, nativeHistory = page });
                }
            } catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException or UnauthorizedAccessException) {
                return Error(e is AiOperationException known ? known.Code : "NATIVE_HISTORY_UNAVAILABLE");
            } finally { modelQueries.Release(); }
        }
        if (request.Action == "models") {
            lock (sync) { if (disposed || request.HostId != HostId) return Error("HOST_CHANGED"); }
            if (!await modelQueries.WaitAsync(0, token)) return Error("MODEL_LIST_BUSY");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            try {
                var installation = (await AiAgentDiscovery.CatalogAsync(remoteStore: remoteStore).WaitAsync(linked.Token))
                    .FirstOrDefault(a => a.Id == request.AgentId && a.Status == "available");
                if (installation == null) return Error("AGENT_UNAVAILABLE");
                if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                var list = await AiAgentSession.ListModelsAsync(installation, linked.Token);
                lock (sync) {
                    if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                    if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    foreach (var session in sessions.Values) session.UpdateModelOptions(installation.Id, list);
                    return Encode(new { hostId = HostId, agentId = installation.Id, models = list.Models, truncated = list.Truncated });
                }
            } catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or
                System.ComponentModel.Win32Exception or JsonException or ArgumentException or UnauthorizedAccessException or TimeoutException) {
                return Error(error is AiOperationException known ? known.Code : "MODEL_LIST_UNAVAILABLE");
            } finally { modelQueries.Release(); }
        }
        if (request.Action is "directories" or "files") {
            lock (sync) { if (disposed || request.HostId != HostId) return Error("HOST_CHANGED"); }
            try {
                if (request.Directory.Length > 0 && !IsLocalDirectory(request.Directory)) return Error("LOCAL_DIRECTORY_REQUIRED");
                var page = new DirectoryListingService().List(request.Directory, request.Offset, directoriesOnly: request.Action == "directories");
                lock (sync) {
                    if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                    if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    return Encode(new { hostId = HostId, directories = page });
                }
            } catch (FileOperationException) { return Error("DIRECTORY_BROWSE_FAILED"); }
        }
        if (request.Action == "remote") {
            lock (sync) {
                if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                if (request.AgentId != "zcode") return Error("AGENT_UNAVAILABLE");
                try {
                    var url = remoteStore.Read();
                    if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    return url.Length == 0 ? Error("REMOTE_NOT_CONFIGURED") : Encode(new { hostId = HostId,
                        agentId = "zcode", strategy = "desktop_remote", remoteUrl = url });
                } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) {
                    return Error("REMOTE_CONFIGURATION_INVALID");
                }
            }
        }
        if (request.Action == "message") {
            lock (sync) {
                if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                if (!sessions.TryGetValue(request.SessionId, out var target)) return Error("SESSION_NOT_FOUND");
                try { return Encode(new { hostId = HostId, message = target.MessagePage(request.RequestId, request.Offset) }); }
                catch (AiOperationException e) { return Error(e.Code); }
            }
        }
        if (request.Action is "artifacts" or "artifact.read") {
            AiAgentSession target;
            lock (sync) {
                if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                if (!sessions.TryGetValue(request.SessionId, out target)) return Error("SESSION_NOT_FOUND");
            }
            try {
                var artifactResult = request.Action == "artifacts" ? target.Artifacts(request.Text) : target.ReadArtifact(request.RequestId, request.Offset, request.Cursor == "0" ? "" : request.Cursor);
                if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                lock (sync) {
                    if (disposed || request.HostId != HostId || !sessions.TryGetValue(request.SessionId, out var active) || active != target) return Error("HOST_CHANGED");
                    return Encode(new { hostId = HostId, artifacts = artifactResult });
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or AiOperationException) {
                return Error(e is AiOperationException known ? known.Code : "ARTIFACT_UNAVAILABLE");
            }
        }
        if (request.Action is "changes" or "search" or "diff.read") {
            lock (sync) {
                if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                if (!sessions.TryGetValue(request.SessionId, out var target)) return Error("SESSION_NOT_FOUND");
                try { if (request.Action == "diff.read") return Encode(new { hostId = HostId, diff = target.DiffPage(request.Offset, request.Text) });
                    return request.Action == "changes" ? Encode(new { hostId = HostId, session = target.Changes(caller, request.StreamId, request.Cursor) })
                    : Encode(new { hostId = HostId, search = target.Search(request.Text, request.Offset) }); }
                catch (AiOperationException e) { return Error(e.Code); }
            }
        }
        if (request.Action is "watch" or "renew") {
            AiAgentSession target;
            lock (sync) {
                if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                if (!sessions.TryGetValue(request.SessionId, out target)) return Error("SESSION_NOT_FOUND");
            }
            try {
                if (request.Action == "renew") {
                    target.RenewControl(caller, request.ControlVersion);
                    return Encode(new { hostId = HostId, renewed = true });
                }
                var update = await target.WatchAsync(caller, request.StreamId, request.Cursor, token).ConfigureAwait(false);
                lock (sync) {
                    if (disposed || request.HostId != HostId || !sessions.TryGetValue(request.SessionId, out var active) || active != target) return Error("HOST_CHANGED");
                    if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    return Encode(new { hostId = HostId, session = update });
                }
            } catch (AiOperationException e) { return Error(e.Code); }
        }
        if (request.Action is "poll" or "history") {
            lock (sync) {
                if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
                return sessions.TryGetValue(request.SessionId ?? "", out var found)
                    ? HistoryOrSnapshot(found) : Error("SESSION_NOT_FOUND");
                string HistoryOrSnapshot(AiAgentSession target) {
                    try { return request.Action == "history" ? Encode(new { hostId = HostId, history = target.History(request.Offset, request.StreamId) }) : Encode(new { hostId = HostId, session = target.Snapshot(caller, true) }); }
                    catch (AiOperationException e) { return Error(e.Code); }
                }
            }
        }
        if (request.Action.StartsWith("attachment.", StringComparison.Ordinal)) {
            // Upload replay is fenced by host, controller, attachment ID, offset and content,
            // independently of the bounded receipts for conversation mutations.
            lock (sync) if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
            return await MutateAsync(caller, request, authorized).WaitAsync(token).ConfigureAwait(false);
        }
        Task<string> result;
        lock (sync) {
            if (disposed || request.HostId != HostId) return Error("HOST_CHANGED");
            if (!Guid.TryParse(request.OperationId, out var operationId) || operationId == Guid.Empty) return Error("INVALID_OPERATION_ID");
            var key = (caller, operationId.ToString("D"));
            if (operations.TryGetValue(key, out var prior)) {
                if (prior.Body != body) return Error("OPERATION_ID_CONFLICT");
                result = prior.Result;
            } else {
                // Keep receipts for this host incarnation. Reject at capacity instead of replaying an evicted mutation.
                if (operations.Count >= 4096 || receiptBytes >= 16 * 1024 * 1024) return Error("RECEIPT_CAPACITY");
                result = Task.Run(async () => {
                    var receipt = await MutateAsync(caller, request, authorized).ConfigureAwait(false);
                    lock (sync) receiptBytes += System.Text.Encoding.UTF8.GetByteCount(body) + System.Text.Encoding.UTF8.GetByteCount(receipt);
                    return receipt;
                });
                operations.Add(key, (body, result));
            }
        }
        // A lost phone connection never cancels an accepted host operation; retry the same operation ID.
        return await result.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task<string> MutateAsync(Guid caller, Request request, Func<bool> authorized)
    {
        AiAgentSession session = null;
        try {
            if (request.Action == "import-native") {
                AiNativeHistoryEntry source;
                lock (sync) {
                    if (disposed || authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    if (!nativeHistory.TryGetValue((caller, request.AgentId, request.RequestId), out var listed) || listed.At < DateTime.UtcNow.AddMinutes(-10)) return Error("NATIVE_HISTORY_REFRESH_REQUIRED");
                    source = listed.Entry;
                    var existing = sessions.Values.FirstOrDefault(s => s.MatchesNative(request.AgentId, source.NativeId));
                    if (existing != null) return Encode(new { hostId = HostId, session = existing.Snapshot(caller, true) });
                }
                var installation = (await AiAgentDiscovery.CatalogAsync(remoteStore: remoteStore)).FirstOrDefault(a => a.Id == request.AgentId && AiAgentSession.SupportsNativeHistory(a));
                if (installation == null) return Error("NATIVE_HISTORY_UNSUPPORTED");
                if (!IsLocalDirectory(source.Directory)) return Error("LOCAL_DIRECTORY_REQUIRED");
                var imported = AiAgentSession.Import(installation, source, caller, null);
                lock (sync) {
                    if (disposed || authorized != null && !authorized()) { imported.Dispose(); return Error("AUTHORITY_CHANGED"); }
                    var existing = sessions.Values.FirstOrDefault(s => s.MatchesNative(request.AgentId, source.NativeId));
                    if (existing != null) { imported.Dispose(); return Encode(new { hostId = HostId, session = existing.Snapshot(caller, true) }); }
                    if (sessions.Count >= 64 || sessions.Values.Count(s => s.IsAlive) >= 8) { imported.Dispose(); return Error("SESSION_CAPACITY"); }
                    session = imported;
                    sessions.Add(session.Id, session);
                    if (archive != null) session.AttachArchive(archive);
                }
                await session.StartAsync();
                if (authorized != null && !authorized()) { session.Dispose(); return Error("AUTHORITY_CHANGED"); }
            } else if (request.Action == "forget") {
                lock (sync) {
                    if (disposed || authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    if (!sessions.TryGetValue(request.SessionId, out session)) return Error("SESSION_NOT_FOUND");
                    if (session.IsAlive) return Error("AGENT_BUSY");
                    if (!session.MatchesControl(request.ControlVersion)) return Error("CONTROL_CHANGED");
                    session.Dispose(); archive?.Delete(session.Id); sessions.Remove(session.Id);
                    return Encode(new { hostId = HostId, deletedSessionId = session.Id });
                }
            } else if (request.Action == "create") {
                var mode = request.DirectoryMode == "" ? (request.Directory.Length == 0 ? "managed" : "custom") : request.DirectoryMode;
                if (mode != "managed" && mode != "custom" || mode == "managed" && request.Directory.Length != 0) return Error("INVALID_DIRECTORY_MODE");
                if (mode == "custom" && !IsLocalDirectory(request.Directory)) return Error("LOCAL_DIRECTORY_REQUIRED");
                var installation = (await AiAgentDiscovery.CatalogAsync(remoteStore: remoteStore).ConfigureAwait(false))
                    .FirstOrDefault(a => a.Id == request.AgentId && a.Status == "available");
                if (installation == null) return Error("AGENT_UNAVAILABLE");
                if (installation.Strategy != "native") return Error("AGENT_STRATEGY_CHANGED");
                AiAgentSession.ValidatePermissionSelection(installation, request.PermissionMode, request.ConfirmedPermissionMode);
                if (request.ReasoningEffort != null && (request.ReasoningEffort.Length > 100 || request.ReasoningEffort.Any(char.IsControl))) return Error("INVALID_REASONING_EFFORT");
                if (authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                if (request.ModelId == null || request.ProviderId == null || request.ModelId.Length > 200 || request.ProviderId.Length > 200 ||
                    request.ModelId.Any(char.IsControl) || request.ProviderId.Any(char.IsControl)) return Error("INVALID_MODEL");
                if (installation.Protocol == "zcode" && (request.ModelId.Length == 0) != (request.ProviderId.Length == 0)) return Error("PROVIDER_AND_MODEL_REQUIRED");
                lock (sync) {
                    if (disposed) return Error("HOST_RETIRED");
                    if (sessions.Count >= 64 || sessions.Values.Count(s => s.IsAlive) >= 8) return Error("SESSION_CAPACITY");
                    var directory = mode == "managed" ? CreateWorkspace(installation.Id) : Path.GetFullPath(request.Directory);
                    session = new AiAgentSession(installation, directory, caller, request.ModelId, request.ProviderId, request.PermissionMode, request.ReasoningEffort ?? "");
                    if (archive != null) session.AttachArchive(archive);
                    sessions.Add(session.Id, session);
                }
                await session.StartAsync().ConfigureAwait(false);
                if (authorized != null && !authorized()) { session.Dispose(); return Error("AUTHORITY_CHANGED"); }
            } else if (request.Action is "resume" or "fork") {
                AiAgentSession source;
                lock (sync) {
                    if (disposed || !sessions.TryGetValue(request.SessionId, out source)) return Error("SESSION_NOT_FOUND");
                    if (sessions.Values.Count(s => s.IsAlive) >= 8 || request.Action == "fork" && sessions.Count >= 64) return Error("SESSION_CAPACITY");
                    if (request.Action == "resume" && source.IsAlive) return Error("AGENT_BUSY");
                    if (request.Action == "resume" && !source.MatchesControl(request.ControlVersion)) return Error("CONTROL_CHANGED");
                }
                var record = request.Action == "fork" ? await source.ForkAsync(caller, request.ControlVersion) : source.ArchiveRecord();
                var agent = (await AiAgentDiscovery.CatalogAsync(remoteStore: remoteStore)).FirstOrDefault(a => a.Id == record.AgentId && a.Strategy == "native" && a.Status == "available");
                if (agent == null || agent.Protocol is not ("codex" or "claude" or "acp") || record.NativeId.Length == 0) return Error("RESUME_UNAVAILABLE");
                lock (sync) {
                    if (disposed || authorized != null && !authorized()) return Error("AUTHORITY_CHANGED");
                    if (request.Action == "resume" && (!sessions.TryGetValue(source.Id, out var current) || !ReferenceEquals(source, current) || source.IsAlive)) return Error("AGENT_BUSY");
                    session = AiAgentSession.Restore(agent, record, archive, caller);
                    if (request.Action == "resume") session.AdvanceControlFrom(source);
                    if (request.Action == "resume") source.Dispose();
                    sessions[session.Id] = session;
                }
                await session.ResumeAsync();
            } else {
                lock (sync) {
                    if (disposed) return Error("HOST_RETIRED");
                    if (!sessions.TryGetValue(request.SessionId ?? "", out session)) return Error("SESSION_NOT_FOUND");
                }
                await session.ApplyAsync(caller, request, authorized).ConfigureAwait(false);
            }
            if (request.Action.StartsWith("attachment.", StringComparison.Ordinal)) return Encode(new { hostId = HostId, attachment = session.AttachmentSnapshot(request.AttachmentId) });
            return Encode(new { hostId = HostId, session = session.Snapshot(caller, true) });
        } catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException or ArgumentException or UnauthorizedAccessException or TimeoutException) {
            // Never expose provider stderr, credentials, or raw exception dumps to the phone.
            return Encode(new { hostId = HostId, error = error is AiOperationException known ? known.Code : "AGENT_OPERATION_FAILED",
                session = session?.Snapshot(caller, true) });
        }
    }

    private static bool IsLocalDirectory(string path) {
        try {
            return !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 && path.IndexOf('\0') < 0 &&
                Path.IsPathFullyQualified(path) && !Path.GetFullPath(path).StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(path);
        } catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private string CreateWorkspace(string agentId) {
        if (agentId.Length == 0 || agentId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new AiOperationException("AGENT_UNAVAILABLE");
        try {
            Directory.CreateDirectory(workspaceRoot);
            if ((File.GetAttributes(workspaceRoot) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            var path = Path.Combine(workspaceRoot, agentId + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N")[..8]);
            // Accepted operations are deduplicated before this point. Never reuse an existing workspace.
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException();
            Directory.CreateDirectory(path);
            return path;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            throw new AiOperationException("AI_WORKSPACE_CREATE_FAILED");
        }
    }

    private string Error(string code) => Encode(new { hostId = HostId, error = code });
    internal static string Encode(object value) => JsonSerializer.Serialize(value, Json);
    public void Dispose()
    {
        lock (sync) {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            foreach (var session in sessions.Values) session.Dispose();
            sessions.Clear(); operations.Clear(); nativeHistory.Clear();
        }
    }
}

internal sealed class AiOperationException(string code) : InvalidOperationException(code)
{
    internal string Code { get; } = code;
}
