using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal bool MatchesNative(string agentId, string nativeId) => agent.Id == agentId && threadId == nativeId;
    internal static bool SupportsNativeHistory(AiAgentInstallation installation) => installation.Status == "available" && installation.Strategy == "native" &&
        installation.Protocol is "codex" or "claude" && AdvancedConfiguration(installation);

    internal static async Task<AiNativeHistoryPage> NativeHistoryAsync(AiAgentInstallation installation, string cursor, CancellationToken token)
    {
        if (!SupportsNativeHistory(installation)) throw new AiOperationException("NATIVE_HISTORY_UNSUPPORTED");
        if (installation.Protocol == "claude") return await Task.Run(() => ClaudeNativeHistory.List(cursor, token), token);
        using var probe = new AiAgentSession(installation, Path.GetTempPath(), Guid.Empty) { discovering = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var registration = timeout.Token.Register(probe.Dispose);
        await probe.InitializeAsync();
        var response = await probe.RpcAsync("thread/list", new { cursor = cursor.Length == 0 ? null : cursor, limit = 30, sortKey = "updated_at", modelProviders = Array.Empty<string>(), useStateDbOnly = true });
        var data = P(response, "data");
        if (data.ValueKind != JsonValueKind.Array) throw new AiOperationException("NATIVE_HISTORY_UNAVAILABLE");
        var entries = data.EnumerateArray().Select(t => new AiNativeHistoryEntry(S(t, "id"), Clip(First(t, "name", "preview"), 160), S(t, "cwd"),
            P(t, "updatedAt").TryGetInt64Safe() * 1000)).Where(t => Guid.TryParse(t.NativeId, out _) && t.Directory.Length is > 0 and <= 4096).Take(30).ToArray();
        return new(entries, S(response, "nextCursor"));
    }
    internal static AiAgentSession Import(AiAgentInstallation installation, AiNativeHistoryEntry source, Guid caller, AiSessionArchive store)
    {
        var session = new AiAgentSession(installation, source.Directory, caller, source.Model, mode: source.PermissionMode.Length == 0 ? "ask" : source.PermissionMode) {
            threadId = source.NativeId, title = source.Title, importNative = true, archive = store,
            settingsNotice = "已沿用电脑历史会话的授权。请避免在原窗口同时发送消息。"
        };
        if (installation.Protocol == "claude") {
            var loaded = ClaudeNativeHistory.Read(source.SourcePath, source.NativeId, true);
            if (!string.Equals(Path.GetFullPath(loaded.Entry.Directory), Path.GetFullPath(source.Directory), StringComparison.OrdinalIgnoreCase))
                throw new AiOperationException("NATIVE_HISTORY_REFRESH_REQUIRED");
            if (loaded.Entry.PermissionMode.Length == 0) throw new AiOperationException("HISTORICAL_PERMISSION_UNAVAILABLE");
            if (!ConfigurationFor(installation).PermissionModes.Any(m => m.Id == loaded.Entry.PermissionMode)) throw new AiOperationException("PERMISSION_MODE_UNAVAILABLE");
            session.permissionMode = loaded.Entry.PermissionMode;
            session.modelId = loaded.Entry.Model;
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
        // Bounded recent display; Codex itself retains the full context on native resume.
        var page = await RpcAsync("thread/turns/list", new { threadId, limit = 10, sortDirection = "desc", itemsView = "summary" });
        var turns = P(page, "data");
        if (turns.ValueKind != JsonValueKind.Array) return;
        foreach (var turn in turns.EnumerateArray().Reverse()) {
            var items = P(turn, "items"); if (items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray()) {
                var kind = S(item, "type"); var id = S(item, "id");
                if (id.Length == 0) continue;
                if (kind == "agentMessage") Put(id, "assistant", S(item, "text"), status: "completed");
                if (kind == "userMessage") {
                    var content = P(item, "content");
                    var text = content.ValueKind == JsonValueKind.Array ? string.Join("\n", content.EnumerateArray().Where(i => S(i, "type") == "text").Select(i => S(i, "text"))) : "";
                    Put(id, "user", text, status: "completed");
                }
            }
        }
        truncated = true; // Summary view is not a complete exported transcript.
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
    internal static string Root => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");
    internal static bool SafeFile(string path, string root)
    {
        var full = Path.GetFullPath(path); var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Path.GetFileNameWithoutExtension(full), out _) || Path.GetExtension(full) != ".jsonl") return false;
        // The user-configured .claude location may itself be moved with a junction.
        // Trust that configured boundary, but never follow links inside projects/.
        for (var current = full; current != null; current = Path.GetDirectoryName(current)) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), boundary.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        return true;
    }
    internal static AiNativeHistoryPage List(string cursor, CancellationToken token, string rootOverride = null)
    {
        var root = rootOverride ?? Root;
        if (!Directory.Exists(root)) return new(Array.Empty<AiNativeHistoryEntry>(), "");
        var offset = 0;
        if (cursor.Length > 0 && (!int.TryParse(cursor, out offset) || offset is < 0 or > 4096)) throw new AiOperationException("INVALID_CURSOR");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new AiOperationException("NATIVE_HISTORY_UNAVAILABLE");
        var files = Directory.EnumerateDirectories(root).Take(256).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl").Take(256)).Take(4096).Where(p => SafeFile(p, root))
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(p => p, StringComparer.Ordinal).ToArray();
        var entries = new List<AiNativeHistoryEntry>(); var index = offset;
        for (; index < files.Length && index < offset + 30; index++) {
            token.ThrowIfCancellationRequested();
            try { var record = Read(files[index], Path.GetFileNameWithoutExtension(files[index]), false, root); if (record.Entry.Directory.Length > 0) entries.Add(record.Entry); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or AiOperationException) { }
        }
        return new(entries.ToArray(), index < files.Length ? index.ToString() : "", files.Length == 4096);
    }
    internal sealed record ReadResult(AiNativeHistoryEntry Entry, AiAgentSession.Message[] Messages, bool Truncated);
    internal static ReadResult Read(string path, string id, bool content, string rootOverride = null)
    {
        if (!SafeFile(path, rootOverride ?? Root)) throw new AiOperationException("NATIVE_HISTORY_UNAVAILABLE");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int window = 512 * 1024;
        // Prefix for original cwd/title, tail for last saved permission and recent messages.
        var bytes = new byte[(int)Math.Min(stream.Length, window)]; stream.ReadExactly(bytes);
        var first = Encoding.UTF8.GetString(bytes); var lines = first.Split('\n').AsEnumerable();
        var truncated = stream.Length > window;
        if (truncated) {
            stream.Position = Math.Max(window, stream.Length - window); bytes = new byte[(int)(stream.Length - stream.Position)]; stream.ReadExactly(bytes);
            lines = lines.Take(Math.Max(0, first.Split('\n').Length - 1)).Concat(Encoding.UTF8.GetString(bytes).Split('\n').Skip(1));
        }
        string title = "", directory = "", permission = "", model = "", firstPrompt = "";
        var messages = new List<AiAgentSession.Message>();
        var lineNumber = 0;
        foreach (var line in lines) {
            lineNumber++;
            if (line.Length is 0 or > window) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc) {
                var value = doc.RootElement;
                if (AiAgentSession.S(value, "sessionId") is { Length: > 0 } sessionId && sessionId != id) continue;
                if (AiAgentSession.P(value, "isSidechain").ValueKind == JsonValueKind.True) continue;
                var cwd = AiAgentSession.S(value, "cwd"); if (cwd.Length > 0) directory = cwd;
                var name = AiAgentSession.S(value, "customTitle"); if (name.Length > 0) title = name;
                var mode = AiAgentSession.S(value, "permissionMode"); if (mode.Length > 0) permission = mode;
                var kind = AiAgentSession.S(value, "type");
                if (kind is not ("user" or "assistant")) continue;
                var message = AiAgentSession.P(value, "message"); var body = AiAgentSession.P(message, "content");
                var text = body.ValueKind == JsonValueKind.String ? body.GetString() : body.ValueKind == JsonValueKind.Array ? string.Join("\n", body.EnumerateArray().Where(p => AiAgentSession.S(p, "type") == "text").Select(p => AiAgentSession.S(p, "text"))) : "";
                if (kind == "user" && firstPrompt.Length == 0 && !string.IsNullOrWhiteSpace(text)) firstPrompt = text;
                var currentModel = AiAgentSession.S(message, "model"); if (currentModel.Length > 0 && !currentModel.StartsWith('<')) model = currentModel;
                if (content && !string.IsNullOrWhiteSpace(text)) {
                    var messageId = AiAgentSession.S(value, "uuid");
                    if (messageId.Length == 0) messageId = "native-line-" + lineNumber;
                    var messageValue = new AiAgentSession.Message(messageId, kind, text.Length > 16000 ? text[..16000] : text, Status: "completed");
                    var existing = messages.FindIndex(m => m.Id == messageId);
                    if (existing >= 0) messages[existing] = messageValue; else messages.Add(messageValue);
                    if (messages.Count > 128) { messages.RemoveAt(0); truncated = true; }
                }
            }
        }
        permission = permission switch { "default" => "ask", "acceptEdits" => "accept-edits", "plan" => "plan", "dontAsk" => "dont-ask", "bypassPermissions" => "full-access", _ => "" };
        title = (title.Length > 0 ? title : firstPrompt).Split('\n')[0].Trim();
        if (title.Length > 160) title = title[..160];
        if (content && truncated) messages = messages.TakeLast(64).ToList();
        return new(new(id, title, directory, new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(), permission, model, path), messages.ToArray(), truncated);
    }
}
