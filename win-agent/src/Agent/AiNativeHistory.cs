using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Ai;

namespace TermLink.Agent;

internal sealed record AiNativeHistoryEntry(string NativeId, string Title, string Directory, long UpdatedAtMs,
    string PermissionMode = "", string Model = "", [property: JsonIgnore] string SourcePath = "");
internal sealed record AiNativeHistoryPage(AiNativeHistoryEntry[] Sessions, string NextCursor, bool Truncated = false);

internal sealed partial class AiAgentSession
{
    // Native Codex policies may contain workspace roots/network grants not represented by our presets.
    // Keep the original policy intact until the user explicitly chooses another preset.
    internal sealed record NativePermissions(JsonElement Approval, JsonElement Sandbox, string Reviewer);
    private NativePermissions nativePermissions;
    private bool importNative;
    private string claudeTranscriptPath = "";
    internal bool MatchesNative(string agentId, string nativeId) => agent.Id == agentId && threadId == nativeId;
    internal static bool SupportsNativeHistory(AiAgentInstallation installation) => installation.Status == "available" && installation.Strategy == "native" &&
        (installation.Id == "opencode" && installation.Protocol == "acp" || (installation.Protocol is "codex" or "claude") && AdvancedConfiguration(installation));

    internal static async Task<AiNativeHistoryPage> NativeHistoryAsync(AiAgentInstallation installation, string cursor, CancellationToken token)
    {
        if (!SupportsNativeHistory(installation)) throw new AiOperationException("NATIVE_HISTORY_UNSUPPORTED");
        if (installation.Protocol == "claude") return await Task.Run(() => ClaudeNativeHistory.List(cursor, token, installation.ConfigurationDataDirectory.Length > 0 ? Path.Combine(installation.ConfigurationDataDirectory, "projects") : null), token);
        using var probe = new AiAgentSession(installation, Path.GetTempPath(), Guid.Empty) { discovering = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var registration = timeout.Token.Register(probe.Dispose);
        await probe.InitializeAsync();
        if (installation.Id == "opencode") {
            if (!probe.acpLoadSession) throw new AiOperationException("NATIVE_HISTORY_UNSUPPORTED");
            var offset = 0;
            if (cursor.Length > 0 && (!int.TryParse(cursor, out offset) || offset is < 0 or > 4096)) throw new AiOperationException("INVALID_CURSOR");
            var listing = await probe.RpcAsync("session/list", new { });
            var values = P(listing, "sessions");
            if (values.ValueKind != JsonValueKind.Array) throw new AiOperationException("NATIVE_HISTORY_UNAVAILABLE");
            var rows = values.EnumerateArray().Select(t => new AiNativeHistoryEntry(S(t, "sessionId"), Clip(S(t, "title"), 160), S(t, "cwd"),
                DateTimeOffset.TryParse(S(t, "updatedAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date.ToUnixTimeMilliseconds() : 0))
                .Where(t => ValidOpenCodeSessionId(t.NativeId) && t.Directory.Length is > 0 and <= 4096).DistinctBy(t => t.NativeId).Take(4096).ToArray();
            return new(rows.Skip(offset).Take(30).ToArray(), offset + 30 < rows.Length ? (offset + 30).ToString(System.Globalization.CultureInfo.InvariantCulture) : "", rows.Length == 4096);
        }
        var response = await probe.RpcAsync("thread/list", new { cursor = cursor.Length == 0 ? null : cursor, limit = 30, sortKey = "updated_at", modelProviders = Array.Empty<string>(),
            sourceKinds = new[] { "cli", "vscode", "appServer" }, useStateDbOnly = true });
        var data = P(response, "data");
        if (data.ValueKind != JsonValueKind.Array) throw new AiOperationException("NATIVE_HISTORY_UNAVAILABLE");
        var entries = data.EnumerateArray().Select(t => new AiNativeHistoryEntry(S(t, "id"), Clip(First(t, "name", "preview"), 160), S(t, "cwd"),
            P(t, "updatedAt").TryGetInt64Safe() * 1000)).Where(t => Guid.TryParse(t.NativeId, out _) && t.Directory.Length is > 0 and <= 4096).Take(30).ToArray();
        return new(entries, S(response, "nextCursor"));
    }
    internal static bool ValidOpenCodeSessionId(string id) => id != null && id.StartsWith("ses_", StringComparison.Ordinal) && id.Length is > 4 and <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    internal static AiAgentSession Import(AiAgentInstallation installation, AiNativeHistoryEntry source, Guid caller, AiSessionArchive store)
    {
        var session = new AiAgentSession(installation, source.Directory, caller, source.Model, mode: source.PermissionMode.Length == 0 ? "ask" : source.PermissionMode) {
            threadId = source.NativeId, title = source.Title, importNative = true, archive = store,
            settingsNotice = "已沿用电脑历史会话的授权。请避免在原窗口同时发送消息。"
        };
        if (installation.Protocol == "claude") {
            var loaded = ClaudeNativeHistory.Read(source.SourcePath, source.NativeId, true, installation.ConfigurationDataDirectory.Length > 0 ? Path.Combine(installation.ConfigurationDataDirectory, "projects") : null);
            if (!string.Equals(Path.GetFullPath(loaded.Entry.Directory), Path.GetFullPath(source.Directory), ClaudeNativeHistory.PathComparison))
                throw new AiOperationException("NATIVE_HISTORY_REFRESH_REQUIRED");
            if (loaded.Entry.PermissionMode.Length == 0) throw new AiOperationException("HISTORICAL_PERMISSION_UNAVAILABLE");
            if (!ConfigurationFor(installation).PermissionModes.Any(m => m.Id == loaded.Entry.PermissionMode)) throw new AiOperationException("PERMISSION_MODE_UNAVAILABLE");
            session.permissionMode = loaded.Entry.PermissionMode;
            session.modelId = loaded.Entry.Model;
            session.claudeTranscriptPath = source.SourcePath;
            session.messages.AddRange(loaded.Messages);
            session.truncated = loaded.Truncated;
        }
        return session;
    }
    private void ReadNativeCodexPermissions(JsonElement response)
    {
        var approval = P(response, "approvalPolicy"); var sandbox = P(response, "sandbox"); var reviewer = S(response, "approvalsReviewer");
        if (approval.ValueKind is not (JsonValueKind.String or JsonValueKind.Object) || sandbox.ValueKind != JsonValueKind.Object || reviewer.Length == 0)
            throw new AiOperationException("HISTORICAL_PERMISSION_UNAVAILABLE");
        nativePermissions = new(approval.Clone(), sandbox.Clone(), reviewer);
        permissionMode = "historical";
        modelId = S(response, "model"); reasoningEffort = S(response, "reasoningEffort");
    }
    private async Task LoadNativeCodexMessagesAsync()
    {
        CodexNativeHistory.ReadResult loaded;
        try { loaded = await CodexNativeHistory.ReadAsync(threadId, RpcAsync, lifetime.Token); }
        catch (AiOperationException) { lock (sync) truncated = true; throw; }
        lock (sync) {
            EnsureMessageIndex();
            foreach (var message in loaded.Messages) {
                if (messageIndex.TryGetValue(message.Id, out var index)) ReplaceMessage(index, message);
                else {
                    messageIndex[message.Id] = messages.Count;
                    messages.Add(message);
                    messageCharacters += message.Text.Length;
                    messageRevisions[message.Id] = PublishChange();
                }
            }
            truncated |= loaded.Truncated;
            if (loaded.Truncated) settingsNotice += " 原生历史仅加载了有上限的可用内容，较早记录或部分详情可能未显示。";
            TrimMessages();
        }
    }
}

internal static class NativeHistoryJson
{
    internal static long TryGetInt64Safe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : 0;
}

// Claude CLI has no stream-json session listing RPC. Read only the documented local
// projects/<project>/<uuid>.jsonl area; never inspect credentials/settings or follow links.
internal static class ClaudeNativeHistory
{
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal const int WindowBytes = 512 * 1024;
    private const int MaxLineBytes = 256 * 1024;
    internal static string Root => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");
    internal static string[] HistoryRoots(System.Collections.Generic.IEnumerable<string> desktopRoots = null)
    {
        var roots = new List<string> { Root };
        foreach (var data in desktopRoots ?? AiAgentDiscovery.ClaudeDesktopDataRoots()) {
            var storage = Path.Combine(data, "claude-code-sessions");
            foreach (var account in SafeDirectories(storage)) foreach (var organization in SafeDirectories(account)) {
                Add(organization);
                foreach (var session in SafeDirectories(organization, localSessions: true)) Add(session);
            }
        }
        void Add(string folder) {
            var config = Path.Combine(folder, ".claude");
            if (Directory.Exists(config) && (File.GetAttributes(config) & FileAttributes.ReparsePoint) == 0)
                roots.Add(Path.Combine(config, "projects"));
        }
        return roots.Where(Directory.Exists).Distinct(PathComparer).Take(256).ToArray();
    }
    private static string[] SafeDirectories(string root, bool localSessions = false)
    {
        try {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return Array.Empty<string>();
            return Directory.EnumerateDirectories(root).Take(256).Where(path =>
                (Guid.TryParse(Path.GetFileName(path), out _) || localSessions && Path.GetFileName(path).StartsWith("local_", StringComparison.Ordinal) && Guid.TryParse(Path.GetFileName(path)[6..], out _)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0).ToArray();
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
    private static string RootFor(string path) => HistoryRoots().Where(root =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison))
        .OrderByDescending(root => root.Length).FirstOrDefault();
    internal static bool SafeFile(string path, string root)
    {
        var full = Path.GetFullPath(path); var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, PathComparison) || !Guid.TryParse(Path.GetFileNameWithoutExtension(full), out _) || Path.GetExtension(full) != ".jsonl") return false;
        // The user-configured .claude location may itself be moved with a junction.
        // Trust that configured boundary, but never follow links inside projects/.
        for (var current = full; current != null; current = Path.GetDirectoryName(current)) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), boundary.TrimEnd(Path.DirectorySeparatorChar), PathComparison)) break;
        }
        return true;
    }
    internal static AiNativeHistoryPage List(string cursor, CancellationToken token, string rootOverride = null)
    {
        var roots = rootOverride == null ? HistoryRoots() : new[] { rootOverride };
        var offset = 0;
        if (cursor.Length > 0 && (!int.TryParse(cursor, out offset) || offset is < 0 or > 4096)) throw new AiOperationException("INVALID_CURSOR");
        var files = roots.Where(Directory.Exists).SelectMany(root => {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new AiOperationException("NATIVE_HISTORY_UNAVAILABLE");
            return Directory.EnumerateDirectories(root).Take(256).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl").Take(256)).Where(p => SafeFile(p, root)).Select(p => (Path: p, Root: root));
        }).Take(4096).OrderByDescending(p => File.GetLastWriteTimeUtc(p.Path)).ThenBy(p => p.Path, StringComparer.Ordinal).ToArray();
        var entries = new List<AiNativeHistoryEntry>(); var index = offset;
        for (; index < files.Length && index < offset + 30; index++) {
            token.ThrowIfCancellationRequested();
            try { var record = Read(files[index].Path, Path.GetFileNameWithoutExtension(files[index].Path), false, files[index].Root); if (record.Entry.Directory.Length > 0 && entries.All(e => e.NativeId != record.Entry.NativeId)) entries.Add(record.Entry); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or AiOperationException) { }
        }
        return new(entries.ToArray(), index < files.Length ? index.ToString() : "", files.Length == 4096);
    }
    internal sealed record ReadResult(AiNativeHistoryEntry Entry, AiAgentSession.Message[] Messages, bool Truncated);
    internal static ReadResult Read(string path, string id, bool content, string rootOverride = null)
    {
        var root = rootOverride ?? RootFor(path);
        if (root == null || !SafeFile(path, root)) throw new AiOperationException("NATIVE_HISTORY_UNAVAILABLE");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var transcript = new AiHistoryPresentation.Buffer();
        // At most 1 MiB is read. Keep complete prefix lines for metadata and complete
        // tail lines for recent content; byte offsets keep fallback IDs stable on append.
        transcript.Truncated = length > WindowBytes * 2;
        string title = "", directory = "", permission = "", model = "", firstPrompt = "";
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var results = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (start, bytes, head, tail) in Windows()) {
            var visitedLines = 0;
            var offset = 0;
            if (!head) {
                var newline = Array.IndexOf(bytes, (byte)'\n');
                if (newline < 0) continue;
                offset = newline + 1;
            }
            while (offset < bytes.Length) {
                var end = Array.IndexOf(bytes, (byte)'\n', offset);
                if (end < 0) { if (!tail) break; end = bytes.Length; }
                var lineOffset = start + offset;
                var count = end - offset;
                var begin = offset;
                offset = end + 1;
                if (count == 0 || count == 1 && bytes[begin] == '\r') continue;
                if (++visitedLines > 8192) { transcript.Truncated = true; break; }
                if (count > MaxLineBytes) { transcript.Truncated = true; continue; }
                JsonDocument doc;
                try { doc = JsonDocument.Parse(bytes.AsMemory(begin, count), new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { transcript.Truncated = true; continue; }
                using (doc) {
                    var value = doc.RootElement;
                    if (value.ValueKind != JsonValueKind.Object) { transcript.Truncated = true; continue; }
                    if (AiAgentSession.S(value, "sessionId") is { Length: > 0 } sessionId && sessionId != id) continue;
                    if (AiAgentSession.P(value, "isSidechain").ValueKind == JsonValueKind.True) continue;
                    var cwd = AiAgentSession.S(value, "cwd"); if (cwd.Length is > 0 and <= 4096) directory = cwd;
                    var name = AiAgentSession.S(value, "customTitle"); if (name.Length > 0) title = AiHistoryPresentation.Clip(name, 160);
                    var mode = AiAgentSession.S(value, "permissionMode"); if (mode.Length > 0) permission = mode;
                    var kind = AiAgentSession.S(value, "type");
                    if (kind is not ("user" or "assistant")) continue;
                    var message = AiAgentSession.P(value, "message"); var body = AiAgentSession.P(message, "content");
                    if (kind == "user" && firstPrompt.Length == 0) firstPrompt = AiHistoryPresentation.PromptText(body);
                    var currentModel = AiAgentSession.S(message, "model");
                    if (currentModel.Length > 256 || currentModel.StartsWith('<')) currentModel = "";
                    if (currentModel.Length > 0) model = currentModel;
                    if (!content) continue;
                    var messageId = AiAgentSession.S(value, "uuid");
                    if (messageId.Length == 0) messageId = AiAgentSession.S(message, "id");
                    if (messageId.Length == 0) messageId = "native-byte-" + lineOffset.ToString(CultureInfo.InvariantCulture);
                    if (messageId.Length > 256) { transcript.Truncated = true; messageId = AiHistoryPresentation.StableId("claude", messageId); }
                    AiHistoryPresentation.AddClaude(transcript, messageId, kind, body, currentModel, calls, results);
                }
            }
        }
        if (content && results.Any(tool => !calls.Contains(tool))) transcript.Truncated = true;
        permission = permission switch { "default" => "ask", "acceptEdits" => "accept-edits", "plan" => "plan", "dontAsk" => "dont-ask", "bypassPermissions" => "full-access", _ => "" };
        title = (title.Length > 0 ? title : firstPrompt).Split('\n')[0].Trim();
        title = AiHistoryPresentation.Clip(title, 160);
        return new(new(id, title, directory, new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(), permission, model, path), transcript.Messages, transcript.Truncated);

        IEnumerable<(long Start, byte[] Bytes, bool Head, bool Tail)> Windows()
        {
            var size = (int)Math.Min(length, length <= WindowBytes * 2 ? WindowBytes * 2 : WindowBytes);
            var bytes = new byte[size]; stream.ReadExactly(bytes);
            yield return (0, bytes, true, length <= WindowBytes * 2);
            if (length <= WindowBytes * 2) yield break;
            stream.Position = length - WindowBytes;
            bytes = new byte[WindowBytes]; stream.ReadExactly(bytes);
            yield return (length - WindowBytes, bytes, false, true);
        }
    }
}

// Read-only history requests only. Never use legacy thread/read(includeTurns=true):
// the server cannot bound that response. Failures other than explicit method/parameter
// incompatibility propagate, so authentication/storage/transport failures are not hidden.
internal static class CodexNativeHistory
{
    internal const int MaxRequests = 24;
    internal const int MaxTurns = 64;
    internal const int MaxItems = 1024;
    private const int MaxPageCharacters = 1024 * 1024;
    private const int MaxResponseCharacters = 4 * 1024 * 1024;
    internal sealed record ReadResult(AiAgentSession.Message[] Messages, bool Truncated);
    private sealed record Item(JsonElement Value, string TurnId, string Status);
    private sealed class Budget
    {
        internal int Requests, Characters;
        internal bool Truncated;
    }

    internal static async Task<ReadResult> ReadAsync(string threadId, Func<string, object, Task<JsonElement>> rpc,
        CancellationToken token = default)
    {
        var budget = new Budget();
        var started = Stopwatch.GetTimestamp();
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var newest = new List<Item>();
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        try {
            // Turn metadata is bounded separately. Item pages contain full persisted
            // items and are preferred over summaries even for a single very long turn.
            await Turns("notLoaded", false);
            await Items();
        }
        catch (AiOperationException exception) when (OlderFallback(exception)) {
            newest.Clear(); seenItems.Clear(); statuses.Clear();
            try { await Turns("full", true); }
            catch (AiOperationException older) when (OlderFallback(older)) {
                newest.Clear(); seenItems.Clear(); statuses.Clear();
                budget.Truncated = true; // A summary response cannot certify full details.
                try { await Turns("summary", true); }
                catch (AiOperationException unsupported) when (OlderFallback(unsupported)) {
                    // Neither bounded API is available. Report partial history rather
                    // than silently issuing an unbounded legacy read.
                    newest.Clear();
                }
            }
        }
        var transcript = new AiHistoryPresentation.Buffer { Truncated = budget.Truncated };
        foreach (var item in newest.AsEnumerable().Reverse())
            AiHistoryPresentation.AddCodex(transcript, item.Value, item.TurnId, item.Status);
        return new(transcript.Messages, transcript.Truncated);

        async Task<JsonElement> Request(string method, object parameters)
        {
            token.ThrowIfCancellationRequested();
            if (budget.Requests >= MaxRequests || budget.Characters >= MaxResponseCharacters) {
                budget.Truncated = true; return default;
            }
            budget.Requests++;
            var remaining = TimeSpan.FromSeconds(25) - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new AiOperationException("NATIVE_HISTORY_TIMEOUT");
            JsonElement response;
            try { response = await rpc(method, parameters).WaitAsync(remaining, token).ConfigureAwait(false); }
            catch (TimeoutException) { throw new AiOperationException("NATIVE_HISTORY_TIMEOUT"); }
            if (response.ValueKind != JsonValueKind.Object) { budget.Truncated = true; return default; }
            var size = response.GetRawText().Length;
            if (size > MaxPageCharacters || size > MaxResponseCharacters - budget.Characters) {
                budget.Truncated = true; return default;
            }
            budget.Characters += size;
            return response;
        }

        async Task Turns(string view, bool collect)
        {
            var seenTurns = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            for (var pageNumber = 0; pageNumber < 8; pageNumber++) {
                JsonElement page;
                try { page = await Request("thread/turns/list", new { threadId, cursor, limit = 16, sortDirection = "desc", itemsView = view }); }
                catch (AiOperationException exception) when (cursor != null && OlderFallback(exception)) {
                    throw new AiOperationException("NATIVE_HISTORY_PAGE_FAILED");
                }
                if (!Page(page, out var data, out var next)) break;
                var count = 0;
                foreach (var turn in data.EnumerateArray()) {
                    if (++count > 16) { budget.Truncated = true; break; }
                    if (turn.ValueKind != JsonValueKind.Object) { budget.Truncated = true; continue; }
                    var turnId = AiAgentSession.S(turn, "id");
                    if (turnId.Length is 0 or > 256) { budget.Truncated = true; continue; }
                    if (!seenTurns.Add(turnId)) continue;
                    if (seenTurns.Count > MaxTurns) { budget.Truncated = true; return; }
                    var status = AiToolPresentation.Status(turn, "unknown");
                    statuses[turnId] = status;
                    if (!collect) continue;
                    var items = AiAgentSession.P(turn, "items");
                    if (items.ValueKind != JsonValueKind.Array) { budget.Truncated = true; continue; }
                    // Turns descend, but items inside a turn are chronological.
                    foreach (var item in items.EnumerateArray().Reverse()) if (!Add(item, turnId, status)) return;
                    if (AiAgentSession.P(turn, "itemsTruncated").ValueKind == JsonValueKind.True ||
                        AiAgentSession.P(turn, "truncated").ValueKind == JsonValueKind.True) budget.Truncated = true;
                }
                if (next.Length == 0) break;
                if (seenTurns.Count >= MaxTurns || !cursors.Add(next) || pageNumber == 7) { budget.Truncated = true; break; }
                cursor = next;
            }
        }

        async Task Items()
        {
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            for (var pageNumber = 0; pageNumber < 16; pageNumber++) {
                JsonElement page;
                try { page = await Request("thread/items/list", new { threadId, cursor, limit = 64, sortDirection = "desc" }); }
                catch (AiOperationException exception) when (cursor != null && OlderFallback(exception)) {
                    throw new AiOperationException("NATIVE_HISTORY_PAGE_FAILED");
                }
                if (!Page(page, out var data, out var next)) break;
                var count = 0;
                foreach (var entry in data.EnumerateArray()) {
                    if (++count > 64) { budget.Truncated = true; break; }
                    var turnId = AiAgentSession.S(entry, "turnId");
                    var item = AiAgentSession.P(entry, "item");
                    if (turnId.Length is 0 or > 256 || item.ValueKind != JsonValueKind.Object) { budget.Truncated = true; continue; }
                    if (!Add(item, turnId, statuses.GetValueOrDefault(turnId, "unknown"))) return;
                }
                if (next.Length == 0) break;
                if (!cursors.Add(next) || pageNumber == 15) { budget.Truncated = true; break; }
                cursor = next;
            }
        }

        bool Page(JsonElement response, out JsonElement data, out string next)
        {
            data = AiAgentSession.P(response, "data");
            next = "";
            if (data.ValueKind != JsonValueKind.Array) { budget.Truncated = true; return false; }
            var continuation = AiAgentSession.P(response, "nextCursor");
            if (continuation.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) {
                budget.Truncated = true;
            } else if (continuation.ValueKind == JsonValueKind.String) {
                next = continuation.GetString();
                if (next.Length > 4096) { next = ""; budget.Truncated = true; }
            }
            if (AiAgentSession.P(response, "truncated").ValueKind == JsonValueKind.True) budget.Truncated = true;
            return true;
        }

        bool Add(JsonElement item, string turn, string status)
        {
            if (item.ValueKind != JsonValueKind.Object) { budget.Truncated = true; return true; }
            var identity = AiHistoryPresentation.CodexId(item, turn);
            if (!seenItems.Add(identity)) return true;
            if (newest.Count >= MaxItems) { budget.Truncated = true; return false; }
            newest.Add(new(item.Clone(), turn, status));
            return true;
        }
    }

    // These are explicit JSON-RPC -32601/-32602 mappings from the adapter, never
    // message-text matching and never the generic AGENT_REQUEST_FAILED code.
    internal static bool OlderFallback(AiOperationException exception) => exception.Code is
        "AGENT_METHOD_NOT_FOUND" or "AGENT_INVALID_PARAMS";
}

internal static class AiHistoryPresentation
{
    internal const int MaxMessages = 1024;
    internal const int MaxCharacters = 2 * 1024 * 1024;
    internal const int MaxText = 64 * 1024;
    private const string Omitted = "\n[历史内容已截断]";

    internal sealed class Buffer
    {
        private readonly List<AiAgentSession.Message> messages = new();
        private readonly Dictionary<string, AiAgentSession.Message> byId = new(StringComparer.Ordinal);
        private int characters;
        internal bool Truncated;
        internal AiAgentSession.Message[] Messages => messages.ToArray();
        internal AiAgentSession.Message Find(string id) => byId.GetValueOrDefault(id);
        internal void Put(AiAgentSession.Message message)
        {
            if (byId.TryGetValue(message.Id, out var previous)) {
                characters -= previous.Text.Length;
                messages[messages.FindIndex(m => m.Id == message.Id)] = message;
            } else messages.Add(message);
            byId[message.Id] = message;
            characters += message.Text.Length;
            while (messages.Count > MaxMessages || characters > MaxCharacters) {
                var old = messages[0]; messages.RemoveAt(0); byId.Remove(old.Id); characters -= old.Text.Length;
                Truncated = true;
            }
        }
    }

    internal static string Clip(string text, int length)
    {
        if (text.Length <= length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
    internal static string StableId(string scope, string value) => "native-" + scope + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    internal static string CodexId(JsonElement item, string turn)
    {
        var id = AiAgentSession.S(item, "id");
        return id.Length is > 0 and <= 256 ? id : StableId("codex", turn + "\n" + item.GetRawText());
    }

    internal static void AddCodex(Buffer target, JsonElement item, string turn, string status)
    {
        var id = CodexId(item, turn); var kind = AiAgentSession.S(item, "type");
        if (AiAgentSession.S(item, "id").Length is 0 or > 256) target.Truncated = true;
        switch (kind) {
            case "userMessage":
                Text(target, id, "user", Content(AiAgentSession.P(item, "content"), target), status: status);
                break;
            case "agentMessage":
                Text(target, id, "assistant", AiAgentSession.S(item, "text"), status: status);
                break;
            case "reasoning":
                // Only provider-exposed summary text is rendered. Raw/encrypted
                // reasoning fields must never be copied into the transcript.
                var summary = AiAgentSession.P(item, "summary");
                var text = summary.ValueKind == JsonValueKind.String ? summary.GetString() :
                    summary.ValueKind == JsonValueKind.Array ? string.Join("\n", summary.EnumerateArray().Take(256)
                        .Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() : AiAgentSession.S(s, "text"))) : "";
                if (summary.ValueKind == JsonValueKind.Array && summary.GetArrayLength() > 256) target.Truncated = true;
                if (text.Length > 0) Text(target, id, "assistant", text, "reasoning", status);
                break;
            case "plan":
                var planText = AiAgentSession.S(item, "text");
                if (planText.Length == 0) {
                    var steps = AiAgentSession.P(item, "plan");
                    if (steps.ValueKind == JsonValueKind.Array) {
                        planText = string.Join("\n", steps.EnumerateArray().Take(128).Select(step =>
                            "- [" + AiAgentSession.S(step, "status") + "] " + AiAgentSession.S(step, "step")));
                        if (steps.GetArrayLength() > 128) target.Truncated = true;
                    }
                }
                Text(target, id, "assistant", planText, "plan", status);
                break;
            case "contextCompaction":
                Text(target, id, "assistant", "[历史上下文已压缩]", "text", status);
                break;
            default:
                if (kind.Length == 0) { target.Truncated = true; break; }
                Tool(target, id, item, kind, AiToolPresentation.Status(item, "unknown"));
                break;
        }
    }

    internal static void AddClaude(Buffer target, string messageId, string role, JsonElement body, string model,
        HashSet<string> calls, HashSet<string> results)
    {
        if (body.ValueKind == JsonValueKind.String) { Text(target, messageId, role, body.GetString(), model: model); return; }
        if (body.ValueKind != JsonValueKind.Array) { target.Truncated = true; return; }
        var index = 0;
        foreach (var part in body.EnumerateArray()) {
            if (index >= 256) { target.Truncated = true; break; }
            var id = index == 0 ? messageId : messageId + "/" + index.ToString(CultureInfo.InvariantCulture);
            index++;
            switch (AiAgentSession.S(part, "type")) {
                case "text": Text(target, id, role, AiAgentSession.S(part, "text"), model: model); break;
                case "thinking": Text(target, id, "assistant", AiAgentSession.S(part, "thinking"), "reasoning", model: model); break;
                case "redacted_thinking": Text(target, id, "assistant", "[思考摘要已隐藏]", "reasoning", model: model); break;
                case "tool_use":
                case "server_tool_use":
                    var callId = AiAgentSession.S(part, "id");
                    if (callId.Length is 0 or > 256 || AiAgentSession.S(part, "name").Length == 0) { target.Truncated = true; break; }
                    if (calls.Count < 4096) calls.Add(callId); else target.Truncated = true;
                    Tool(target, "tool-" + callId, part, "tool_use", target.Find("tool-" + callId)?.Status ?? "unknown", model);
                    break;
                case "tool_result":
                    var resultId = AiAgentSession.S(part, "tool_use_id");
                    if (resultId.Length is 0 or > 256) { target.Truncated = true; break; }
                    if (results.Count < 4096) results.Add(resultId); else target.Truncated = true;
                    Tool(target, "tool-" + resultId, part, "tool_result",
                        AiAgentSession.P(part, "is_error").ValueKind == JsonValueKind.True ? "failed" : "completed", model);
                    break;
                default:
                    var reference = Reference(part, target);
                    if (reference.Length > 0) Text(target, id, role, reference, "reference", model: model);
                    break;
            }
        }
    }

    private static void Text(Buffer target, string id, string role, string text, string kind = "text", string status = "completed", string model = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > MaxText) { text = Clip(text, MaxText - Omitted.Length) + Omitted; target.Truncated = true; }
        target.Put(new(id, role, text, kind, Status: status, Model: model));
    }
    private static void Tool(Buffer target, string id, JsonElement value, string kind, string status, string model = "")
    {
        var previous = target.Find(id);
        var text = AiToolPresentation.Normalize(value, kind, previous?.Text ?? "");
        var presentation = AiToolPresentation.Describe(text, kind);
        // Normalize is responsible for bounded, valid JSON. Preserve its explicit
        // truncation flag in the session, rather than slicing the serialized JSON.
        using (var doc = JsonDocument.Parse(text))
            if (AiAgentSession.P(doc.RootElement, "truncated").ValueKind == JsonValueKind.True) target.Truncated = true;
        target.Put(new(id, "tool", text, "tool", presentation.Title, presentation.Summary,
            status, model.Length > 0 ? model : previous?.Model ?? ""));
    }

    internal static string PromptText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return Clip(content.GetString(), 160);
        if (content.ValueKind != JsonValueKind.Array) return "";
        return Clip(string.Join("\n", content.EnumerateArray().Take(16).Where(p => AiAgentSession.S(p, "type") == "text")
            .Select(p => Clip(AiAgentSession.S(p, "text"), 160))), 160);
    }
    private static string Content(JsonElement content, Buffer target)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) { target.Truncated = true; return ""; }
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray().Take(256)) {
            var value = AiAgentSession.S(part, "type") == "text" ? AiAgentSession.S(part, "text") : Reference(part, target);
            if (value.Length == 0) continue;
            if (text.Length > 0) text.Append('\n');
            // Stop after a bounded prefix; Text will apply the visible omission marker.
            text.Append(Clip(value, MaxText + 1));
            if (text.Length > MaxText) { target.Truncated = true; break; }
        }
        if (content.GetArrayLength() > 256) target.Truncated = true;
        return text.ToString();
    }
    private static string Reference(JsonElement part, Buffer target)
    {
        var type = AiAgentSession.S(part, "type");
        var source = AiAgentSession.P(part, "source");
        var reference = AiAgentSession.S(part, "path");
        if (reference.Length == 0) reference = AiAgentSession.S(part, "url");
        if (reference.Length == 0) reference = AiAgentSession.S(part, "uri");
        if (reference.Length == 0) reference = AiAgentSession.S(source, "url");
        if (reference.Length == 0) reference = AiAgentSession.S(part, "name");
        if (reference.Length == 0) reference = AiAgentSession.S(part, "title");
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) reference = "内嵌内容";
        if (reference.Length > 2048) { reference = Clip(reference, 2048) + "…"; target.Truncated = true; }
        reference = reference.Replace('\r', ' ').Replace('\n', ' ');
        var label = type switch {
            "image" or "localImage" => "图片", "document" => "文档", "skill" => "技能", "mention" => "引用",
            "resource" or "resource_link" => "资源", _ => "未支持的历史内容"
        };
        if (label == "未支持的历史内容") { target.Truncated = true; reference = Clip(type, 80); }
        if (reference.Length == 0) reference = AiAgentSession.S(source, "media_type");
        return "[" + label + (reference.Length > 0 ? "：" + Clip(reference, 2048) : "") + "]";
    }
}
