using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TermLink.Agent;

internal sealed partial class AiAgentSession
{
    private string permissionMode = "ask", archiveError = "", diff = "", defaultModelId = "";
    private long settingsVersion = 1;
    private bool compacting;
    private JsonElement? usage;
    private string[] slashCommands = Array.Empty<string>();
    private AiSessionArchive archive;
    private readonly Dictionary<string, Attachment> attachments = new();
    private readonly HashSet<string> removedAttachments = new();
    private const long MaxFile = 20 * 1024 * 1024, MaxAttachments = 50 * 1024 * 1024;
    private sealed class Attachment
    {
        internal string Id, Name, Mime, Hash, Path;
        internal Guid Owner;
        internal long Size, Offset;
        internal bool Ready, Used;
        internal DateTime Touched = DateTime.UtcNow;
        internal bool IsImage => Mime is "image/png" or "image/jpeg" or "image/webp" or "image/gif";
    }
    private object Capabilities() => new {
        changes = true, watch = true, search = true, artifacts = true, diffRead = true, configure = CurrentConfiguration().ModelSwitch || CurrentConfiguration().PermissionSwitch,
        configuration = CurrentConfiguration(), steer = agent.Protocol == "codex",
        resume = agent.Protocol is "codex" or "claude" && threadId.Length > 0,
        fork = agent.Protocol == "codex", history = true, attachments = true, fileReferences = true,
        nativeImages = agent.Protocol is "codex" or "claude",
        maxFileBytes = MaxFile, maxTotalBytes = MaxAttachments, maxFiles = 5, chunkBytes = 24 * 1024,
        modes = Modes(agent.Protocol)
    };
    internal static string[] Modes(string protocol) => protocol == "claude" ? new[] { "ask", "plan", "accept-edits" } : protocol == "codex" ? new[] { "ask", "read-only" } : new[] { "ask" };
    private static string ClaudeMode(string mode) => mode switch { "plan" => "plan", "accept-edits" => "acceptEdits", "dont-ask" => "dontAsk", "full-access" => "bypassPermissions", _ => "default" };
    private async Task ConfigureAsync(AiSessionManager.Request request)
    {
        lock (sync) {
            if (state != "idle" || pending.Count > 0) throw new AiOperationException("AGENT_BUSY");
            if (request.ExpectedSettingsVersion != settingsVersion) throw new AiOperationException("SETTINGS_CHANGED");
        }
        if (agent.Protocol == "zcode" && AdvancedConfiguration(agent)) {
            var previous = (modelId, providerId, permissionMode, reasoningEffort);
            ReadZcodeConfiguration(await RpcAsync("session/read", new { sessionId = threadId, messageLimit = 1 }));
            if (previous != (modelId, providerId, permissionMode, reasoningEffort)) {
                lock (sync) { settingsVersion++; SaveArchive(); }
                throw new AiOperationException("SETTINGS_CHANGED");
            }
        }
        var config = CurrentConfiguration();
        if (!config.ModelSwitch && !config.PermissionSwitch) throw new AiOperationException("CONFIGURE_UNSUPPORTED");
        if (request.ModelId == null || request.ModelId.Length > 200 || request.ModelId.Any(char.IsControl) || request.ProviderId == null || request.ProviderId.Length > 200 || request.ProviderId.Any(char.IsControl) || agent.Protocol != "zcode" && request.ProviderId.Length > 0) throw new AiOperationException("INVALID_MODEL");
        var modelChanged = request.ModelId != modelId || request.ProviderId != providerId;
        if (agent.Protocol == "zcode" && modelChanged && (request.ModelId.Length == 0 || request.ProviderId.Length == 0)) throw new AiOperationException("PROVIDER_AND_MODEL_REQUIRED");
        var modeChanged = request.PermissionMode != permissionMode;
        if (modeChanged || nativePermissions == null) ValidatePermissionSelection(agent, request.PermissionMode, request.ConfirmedPermissionMode, modeChanged);
        if (!config.PermissionModes.Any(m => m.Id == request.PermissionMode)) throw new AiOperationException("PERMISSION_MODE_UNAVAILABLE");
        var nextEffort = request.ReasoningEffort ?? reasoningEffort;
        if (nextEffort.Length > 100 || nextEffort.Any(char.IsControl)) throw new AiOperationException("INVALID_REASONING_EFFORT");
        if (modeChanged && (modelChanged || nextEffort != reasoningEffort)) throw new AiOperationException("ONE_SETTING_AT_A_TIME");
        if (agent.Protocol == "claude" && nextEffort != reasoningEffort) throw new AiOperationException("REASONING_REQUIRES_NEW_SESSION");
        if (agent.Protocol == "codex" && request.ModelId.Length == 0 && modelId.Length > 0 && defaultModelId.Length == 0) throw new AiOperationException("DEFAULT_MODEL_UNAVAILABLE");
        var nextModel = request.ModelId.Length == 0 && agent.Protocol == "codex" ? defaultModelId : request.ModelId;
        var reasoning = ReasoningFor(nextModel, request.ProviderId);
        var notice = "";
        if (nextEffort.Length > 0 && !(nativePermissions != null && !modelChanged && request.ReasoningEffort == null) &&
            !(agent.Protocol == "zcode" && modelChanged) && !reasoning.Options.Any(o => o.Value == nextEffort)) {
            if (request.ReasoningEffort != null || !modelChanged) throw new AiOperationException("INVALID_REASONING_EFFORT");
            if (agent.Protocol == "claude") throw new AiOperationException("REASONING_REQUIRES_NEW_SESSION");
            nextEffort = "";
            notice = "新模型不支持原推理强度，已切换为模型默认。";
        }
        if (modelChanged && agent.Protocol == "zcode" && request.ReasoningEffort != null && request.ReasoningEffort.Length > 0) throw new AiOperationException("ONE_SETTING_AT_A_TIME");
        if (agent.Protocol == "claude") {
            try {
                if (request.ModelId != modelId) await ClaudeControlAsync(new { subtype = "set_model", model = request.ModelId.Length == 0 ? null : request.ModelId });
                if (request.PermissionMode != permissionMode) await ClaudeControlAsync(new { subtype = "set_permission_mode", mode = ClaudeMode(request.PermissionMode) });
            } catch (AiOperationException e) when (e.Code == "AGENT_TIMEOUT") { Fail("SETTINGS_OUTCOME_UNKNOWN"); throw; }
        }
        if (agent.Protocol == "zcode") {
            var parameters = new Dictionary<string, object> { ["sessionId"] = threadId };
            if (zcodeRevision.HasValue) parameters["expectedRevision"] = zcodeRevision.Value;
            string method;
            if (modelChanged) {
                method = "session/setModel"; parameters["model"] = new { modelId = request.ModelId, providerId = request.ProviderId };
                parameters["persistAsWorkspaceLastUsed"] = false;
            } else if (modeChanged) { method = "session/setMode"; parameters["mode"] = ZcodeMode(request.PermissionMode); }
            else if (nextEffort != reasoningEffort) {
                method = "session/setThoughtLevel";
                var value = nextEffort.Length > 0 ? nextEffort : reasoning.DefaultValue;
                if (value.Length == 0) throw new AiOperationException("DEFAULT_REASONING_UNAVAILABLE");
                parameters["thoughtLevel"] = value;
            } else return;
            try {
                ReadZcodeConfiguration(await RpcAsync(method, parameters));
                if (permissionMode != request.PermissionMode || modelChanged && (modelId != request.ModelId || providerId != request.ProviderId) ||
                    method == "session/setThoughtLevel" && reasoningEffort != (string)parameters["thoughtLevel"])
                    throw new AiOperationException("SETTINGS_OUTCOME_UNKNOWN");
            }
            catch (AiOperationException e) when (e.Code is "AGENT_TIMEOUT" or "SETTINGS_OUTCOME_UNKNOWN") { Fail("SETTINGS_OUTCOME_UNKNOWN"); throw; }
            // ZCode returns its current model/thought settings with the successful mutation.
            lock (sync) { settingsNotice = ""; settingsVersion++; SaveArchive(); }
            return;
        }
        // Codex's documented turn/start overrides apply on the next turn. Never label this as
        // an already applied provider setting: the snapshot explicitly reports next_turn.
        lock (sync) { if (modeChanged) nativePermissions = null; modelId = nextModel; permissionMode = request.PermissionMode; reasoningEffort = nextEffort; settingsNotice = notice; settingsVersion++; SaveArchive(); }
    }

    private Message[] RecentMessages()
    {
        var chars = 0;
        return messages.AsEnumerable().Reverse().Select(PageMessage).TakeWhile(m => (chars += m.Text.Length) <= 32000 || chars == m.Text.Length).Take(64).Reverse().ToArray();
    }
    private static Message PageMessage(Message message) {
        var length = Math.Min(message.Text.Length, 16000);
        if (length > 0 && length < message.Text.Length && char.IsHighSurrogate(message.Text[length - 1])) length--;
        return message with { Text = message.Text[..length], FullLength = message.Text.Length };
    }
    internal object History(long before, string expectedStream = "")
    {
        lock (sync) {
            if (!string.IsNullOrEmpty(expectedStream) && expectedStream != streamId) throw new AiOperationException("HISTORY_GAP");
            if (before < 0 || before > historyBase + messages.Count || before != 0 && before <= historyBase) throw new AiOperationException("HISTORY_GAP");
            var end = before == 0 ? messages.Count : (int)(before - historyBase);
            var start = end; var chars = 0;
            while (start > 0 && end - start < 64 && (chars == 0 || chars + Math.Min(messages[start - 1].Text.Length, 16001) <= 64000)) chars += Math.Min(messages[--start].Text.Length, 16001);
            return new { messages = messages.Skip(start).Take(end - start).Select(PageMessage).ToArray(), before = start > 0 ? historyBase + start : 0, count = messages.Count, streamId, truncated };
        }
    }
    internal object MessagePage(string id, long offset)
    {
        lock (sync) {
            var message = messages.FirstOrDefault(m => m.Id == id) ?? throw new AiOperationException("MESSAGE_NOT_FOUND");
            if (offset < 0 || offset > message.Text.Length) throw new AiOperationException("INVALID_OFFSET");
            var length = Math.Min(32768, message.Text.Length - (int)offset);
            if (length > 0 && char.IsHighSurrogate(message.Text[(int)offset + length - 1])) length--;
            return new { id, offset, text = message.Text.Substring((int)offset, length), nextOffset = offset + length, length = message.Text.Length };
        }
    }
    internal AiSessionArchive.Record ArchiveRecord() { lock (sync) return new(Id, agent.Id, directory, threadId, title, modelId, providerId, permissionMode, settingsVersion, messages.ToArray(), defaultModelId, reasoningEffort, nativePermissions); }
    internal void AttachArchive(AiSessionArchive store) { lock (sync) { archive = store; SaveArchive(); } }
    internal static AiAgentSession Restore(TermLink.Ai.AiAgentInstallation agent, AiSessionArchive.Record record, AiSessionArchive store, Guid caller = default)
    {
        var session = new AiAgentSession(agent, record.Directory, caller, record.Model, record.Provider) {
            Id = record.Id, threadId = record.NativeId, title = record.Title, permissionMode = record.Mode,
            settingsVersion = record.Version, state = caller == Guid.Empty ? "suspended" : "starting", archive = store, defaultModelId = record.DefaultModel ?? "", reasoningEffort = record.ReasoningEffort ?? "", nativePermissions = record.NativePermissions
        };
        session.settingsNotice = "恢复时沿用历史授权；不支持的授权会明确报错。";
        session.messages.AddRange(record.Messages);
        return session;
    }
    internal async Task ResumeAsync() { lock (sync) state = "starting"; await StartAsync(); }
    internal void AdvanceControlFrom(AiAgentSession previous) { lock (previous.sync) controlVersion = previous.controlVersion + 1; }
    internal bool MatchesControl(long version) { lock (sync) return version == controlVersion; }
    internal async Task<AiSessionArchive.Record> ForkAsync(Guid caller, long version)
    {
        await actions.WaitAsync(lifetime.Token);
        try {
            lock (sync) {
                ExpireLease();
                if (owner != caller || controlVersion != version) throw new AiOperationException("CONTROL_CHANGED");
                if (agent.Protocol != "codex" || state != "idle" || pending.Count != 0) throw new AiOperationException("FORK_UNAVAILABLE");
            }
            var result = await RpcAsync("thread/fork", new { threadId, persistExtendedHistory = true });
            var nativeId = S(P(result, "thread"), "id");
            if (nativeId.Length == 0) throw new AiOperationException("AGENT_PROTOCOL_INCOMPATIBLE");
            return ArchiveRecord() with { Id = Guid.NewGuid().ToString("D"), NativeId = nativeId, Title = Clip(title, 65) + " · 分支" };
        } finally { actions.Release(); }
    }
    private void SaveArchive()
    {
        if (archive == null || discovering) return;
        try { archive.Save(ArchiveRecord()); archiveError = ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { archiveError = "HISTORY_SAVE_FAILED"; }
    }

    // Uploads use bounded E2EE AI frames, not chat text. Offset/content checks provide replay
    // safety without retaining multi-megabyte chunks in the mutation receipt cache.
    private void AttachmentAction(Guid caller, AiSessionManager.Request request)
    {
        if (!Guid.TryParseExact(request.AttachmentId, "D", out _)) throw new AiOperationException("INVALID_ATTACHMENT");
        if (removedAttachments.Count >= 4096) throw new AiOperationException("ATTACHMENT_LIMIT");
        foreach (var expired in attachments.Values.Where(a => !a.Used && a.Touched < DateTime.UtcNow.AddMinutes(-30)).ToArray()) {
            CheckAttachmentPath(expired.Path); File.Delete(expired.Path); attachments.Remove(expired.Id); removedAttachments.Add(expired.Id);
        }
        if (request.Action == "attachment.remove" && !attachments.ContainsKey(request.AttachmentId)) { removedAttachments.Add(request.AttachmentId); return; }
        if (removedAttachments.Contains(request.AttachmentId)) throw new AiOperationException("ATTACHMENT_NOT_FOUND");
        if (request.Action == "attachment.begin") {
            if (attachments.TryGetValue(request.AttachmentId, out var previous)) {
                if (previous.Owner != caller || previous.Hash != request.Sha256 || previous.Size != request.Size || previous.Name != request.FileName || previous.Mime != request.Mime) throw new AiOperationException("ATTACHMENT_CONFLICT");
                previous.Touched = DateTime.UtcNow;
                return;
            }
            if (request.Size <= 0 || request.Size > MaxFile || request.FileName == null || request.FileName.Length is < 1 or > 180 || request.FileName.Any(char.IsControl) ||
                request.Sha256 == null || request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit) || request.Mime == null || request.Mime.Length > 120 || request.Mime.Any(char.IsControl)) throw new AiOperationException("INVALID_ATTACHMENT");
            if (attachments.Values.Count(a => !a.Used) >= 5 || attachments.Values.Where(a => !a.Used).Sum(a => a.Size) + request.Size > MaxAttachments || attachments.Values.Sum(a => a.Size) + request.Size > 200 * 1024 * 1024) throw new AiOperationException("ATTACHMENT_LIMIT");
            if (request.Mime.StartsWith("image/", StringComparison.Ordinal) && agent.Protocol == "claude" && request.Size > 5 * 1024 * 1024) throw new AiOperationException("IMAGE_TOO_LARGE");
            var root = Path.Combine(directory, ".termlink", "attachments", Id);
            CheckAttachmentPath(root);
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, request.AttachmentId + ".upload");
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            attachments.Add(request.AttachmentId, new() { Id = request.AttachmentId, Name = request.FileName, Mime = request.Mime, Size = request.Size, Hash = request.Sha256, Path = path, Owner = caller });
            return;
        }
        if (!attachments.TryGetValue(request.AttachmentId, out var file) || file.Owner != caller) throw new AiOperationException("ATTACHMENT_NOT_FOUND");
        file.Touched = DateTime.UtcNow;
        CheckAttachmentPath(file.Path);
        if (request.Action == "attachment.remove") {
            if (file.Used) throw new AiOperationException("ATTACHMENT_IN_USE");
            File.Delete(file.Path); attachments.Remove(file.Id); removedAttachments.Add(file.Id); return;
        }
        if (request.Action == "attachment.chunk") {
            if (file.Ready || request.Data == null || request.Data.Length > 32768) throw new AiOperationException("INVALID_ATTACHMENT_CHUNK");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(request.Data); } catch (FormatException) { throw new AiOperationException("INVALID_ATTACHMENT_CHUNK"); }
            if (bytes.Length is < 1 or > 24576 || request.Offset < 0 || request.Offset > file.Offset || request.Offset + bytes.Length > file.Size) throw new AiOperationException("ATTACHMENT_OFFSET");
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Position = request.Offset;
            if (request.Offset < file.Offset) {
                if (request.Offset + bytes.Length > file.Offset) throw new AiOperationException("ATTACHMENT_OFFSET");
                var existing = new byte[bytes.Length]; stream.ReadExactly(existing);
                if (!CryptographicOperations.FixedTimeEquals(bytes, existing)) throw new AiOperationException("ATTACHMENT_CONFLICT");
            } else { stream.Write(bytes); stream.Flush(true); file.Offset += bytes.Length; }
        } else if (request.Action == "attachment.commit") {
            if (file.Ready) return;
            using (var stream = File.OpenRead(file.Path)) {
                if (file.Offset != file.Size || stream.Length != file.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Hash, StringComparison.OrdinalIgnoreCase)) throw new AiOperationException("ATTACHMENT_HASH");
                if (file.IsImage) {
                    stream.Position = 0; var header = new byte[12]; var count = stream.Read(header);
                    var valid = file.Mime switch {
                        "image/png" => count >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}),
                        "image/jpeg" => count >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255,
                        "image/gif" => count >= 6 && Encoding.ASCII.GetString(header, 0, 6) is "GIF87a" or "GIF89a",
                        "image/webp" => count >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP", _ => false };
                    if (!valid) throw new AiOperationException("INVALID_IMAGE");
                }
            }
            var completed = Path.ChangeExtension(file.Path, "ready"); File.Move(file.Path, completed); file.Path = completed; file.Ready = true;
        }
    }
    private void CheckAttachmentPath(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        var current = root;
        foreach (var part in Path.GetFullPath(path)[root.Length..].Split(Path.DirectorySeparatorChar)) {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AiOperationException("ATTACHMENT_PATH_UNSAFE");
        }
    }
    internal object AttachmentSnapshot(string id) { lock (sync) return attachments.TryGetValue(id ?? "", out var a) ? new { id = a.Id, offset = a.Offset, ready = a.Ready } : null; }
    private Attachment[] ResolveAttachments(Guid caller, string[] ids)
    {
        if (ids == null || ids.Length == 0) return Array.Empty<Attachment>();
        if (ids.Length > 5 || ids.Distinct().Count() != ids.Length) throw new AiOperationException("ATTACHMENT_LIMIT");
        var files = ids.Select(id => {
            if (id == null || !attachments.TryGetValue(id, out var a) || !a.Ready || a.Owner != caller) throw new AiOperationException("ATTACHMENT_NOT_READY");
            CheckAttachmentPath(a.Path);
            if (!File.Exists(a.Path)) throw new AiOperationException("ATTACHMENT_NOT_FOUND");
            using (var input = File.OpenRead(a.Path)) if (input.Length != a.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(a.Hash, StringComparison.OrdinalIgnoreCase)) throw new AiOperationException("ATTACHMENT_HASH");
            return a;
        }).ToArray();
        if (files.Sum(a => a.Size) > MaxAttachments) throw new AiOperationException("ATTACHMENT_LIMIT");
        foreach (var a in files) a.Used = true;
        return files;
    }
    private void RemoveUnusedAttachments()
    {
        foreach (var file in attachments.Values.Where(a => !a.Used)) {
            try { CheckAttachmentPath(file.Path); File.Delete(file.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or AiOperationException) { }
        }
    }
    private string AttachmentPrompt(string text, Attachment[] files) => text + string.Concat(files.Where(a => !a.IsImage || agent.Protocol == "zcode")
        .Select(a => "\nAttached local file (user supplied content): " + JsonSerializer.Serialize(a.Name) + " at " + JsonSerializer.Serialize(a.Path)));
}

// Account-scoped DPAPI snapshots. Atomic replacement leaves the last complete revision intact
// after a crash. Provider credentials and pending permission grants are never persisted.
internal sealed class AiSessionArchive(string root)
{
    internal sealed record Record(string Id, string AgentId, string Directory, string NativeId, string Title,
        string Model, string Provider, string Mode, long Version, AiAgentSession.Message[] Messages, string DefaultModel = "", string ReasoningEffort = "", AiAgentSession.NativePermissions NativePermissions = null);
    internal void Delete(string id) {
        if (!Guid.TryParseExact(id, "D", out _)) throw new AiOperationException("SESSION_NOT_FOUND");
        File.Delete(Path.Combine(root, id + ".bin"));
        File.Delete(Path.Combine(root, id + ".bin.tmp"));
    }
    internal Record[] Read()
    {
        if (!Directory.Exists(root)) return Array.Empty<Record>();
        return Directory.EnumerateFiles(root, "*.bin").Take(64).Select(path => {
            try {
                if (new FileInfo(path).Length > 80 * 1024 * 1024) return null;
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                try { var value = JsonSerializer.Deserialize<Record>(plain, AiSessionManager.Json); return value != null && Guid.TryParseExact(value.Id, "D", out _) ? value : null; }
                finally { CryptographicOperations.ZeroMemory(plain); }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { return null; }
        }).Where(r => r != null).ToArray();
    }
    internal void Save(Record record)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, record.Id + ".bin");
        var plain = JsonSerializer.SerializeToUtf8Bytes(record, AiSessionManager.Json);
        try {
            if (plain.Length > 78 * 1024 * 1024) throw new IOException("AI history capacity");
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path + ".tmp", encrypted); File.Move(path + ".tmp", path, true);
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
