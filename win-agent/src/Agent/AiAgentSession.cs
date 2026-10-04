using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Ai;

namespace TermLink.Agent;

internal sealed partial class AiAgentSession : IDisposable
{
    internal string Id { get; private init; } = Guid.NewGuid().ToString("D");
    private readonly AiAgentInstallation agent;
    private readonly string directory;
    private string modelId, providerId;
    private readonly object sync = new();
    private readonly SemaphoreSlim actions = new(1, 1), writes = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> calls = new();
    private readonly List<Message> messages = new();
    private readonly Dictionary<string, Pending> pending = new();
    private Process process;
    private AiProcessJob job;
    private Guid owner;
    private long controlVersion = 1, nextCall;
    private DateTime leaseExpires = DateTime.UtcNow.AddMinutes(2);
    private string threadId = "", turnId = "", state = "starting", error = "", authentication = "unverified", title = "";
    private bool truncated, disposed, discovering;
    internal bool IsAlive { get { lock (sync) return !disposed && state is not ("exited" or "interrupted" or "suspended"); } }
    internal DesktopAiSessionSummary DesktopSummary() {
        lock (sync) return new DesktopAiSessionSummary { Id = Id, Title = title.Length > 160 ? title[..160] : title,
            Agent = agent.Name, Directory = directory, State = state };
    }
    // Text remains the bounded authoritative detail. The extra presentation fields let the
    // phone render a compact tool row without parsing provider-specific JSON.
    internal sealed record Message(string Id, string Role, string Text, string Kind = "text",
        string Title = "", string Summary = "", string Status = "", string Model = "", string Mode = "", int FullLength = 0);
    internal sealed record Question(string Id, string Text, string[] Options, bool MultiSelect);
    private sealed record Pending(string Id, JsonElement RpcId, string Method, JsonElement Parameters,
        string Kind, string Detail, Question[] Questions, DateTime Expires);

    internal AiAgentSession(AiAgentInstallation agent, string directory, Guid owner, string modelId = "", string providerId = "", string mode = "ask", string effort = "")
    { this.agent = agent; this.directory = directory; this.owner = owner; this.modelId = modelId; this.providerId = providerId; permissionMode = mode; reasoningEffort = effort; }

    internal async Task StartAsync()
    {
        try {
            if (nativePermissions == null && !ConfigurationFor(agent).PermissionModes.Any(m => m.Id == permissionMode))
                throw new AiOperationException("PERMISSION_MODE_UNAVAILABLE");
            var initialized = await InitializeAsync();
            await InitializeConfigurationAsync(initialized);
            if (agent.Protocol == "codex") {
                var created = importNative || nativePermissions != null
                    ? await RpcAsync("thread/resume", new { threadId, excludeTurns = true })
                    : await RpcAsync(threadId.Length == 0 ? "thread/start" : "thread/resume", new { threadId, cwd = directory, model = modelId.Length > 0 ? modelId : null,
                        approvalPolicy = CodexApproval(permissionMode), approvalsReviewer = CodexReviewer(permissionMode), sandbox = CodexSandbox(permissionMode), excludeTurns = true });
                threadId = S(P(created, "thread"), "id");
                if (importNative || nativePermissions != null) ReadNativeCodexPermissions(created);
                if (importNative) {
                    try { await LoadNativeCodexMessagesAsync(); }
                    catch (AiOperationException) { settingsNotice += " 最近消息摘要读取失败，原生上下文仍保留。"; }
                }
                if (defaultModelId.Length == 0 && modelId.Length == 0) defaultModelId = S(created, "model");
                if (defaultModelId.Length == 0) defaultModelId = modelOptions.FirstOrDefault(m => m.IsDefault)?.ModelId ?? "";
                if (modelId.Length == 0) modelId = S(created, "model");
                if (nativePermissions == null && reasoningEffort.Length > 0 && !ReasoningFor(modelId, providerId).Options.Any(o => o.Value == reasoningEffort)) throw new AiOperationException("INVALID_REASONING_EFFORT");
            } else if (agent.Protocol == "zcode") {
                var initialEffort = reasoningEffort;
                var parameters = new Dictionary<string, object> { ["workspace"] = new { workspaceKey = directory, workspacePath = directory }, ["mode"] = ZcodeMode(permissionMode) };
                if (modelId.Length > 0) parameters["model"] = new { modelId, providerId };
                var created = await RpcAsync("session/create", parameters);
                threadId = S(created, "sessionId");
                if (threadId.Length == 0) threadId = S(P(created, "session"), "sessionId");
                if (threadId.Length == 0) throw new AiOperationException("AGENT_PROTOCOL_INCOMPATIBLE");
                await RpcAsync("session/subscribe", new { sessionId = threadId, deliveryKind = "desktop-continuous", includeSnapshot = true });
                if (AdvancedConfiguration(agent)) ReadZcodeConfiguration(await RpcAsync("session/read", new { sessionId = threadId, messageLimit = 1 }));
                if (initialEffort.Length > 0) {
                    if (!zcodeReasoning.Options.Any(o => o.Value == initialEffort)) throw new AiOperationException("INVALID_REASONING_EFFORT");
                    ReadZcodeConfiguration(await RpcAsync("session/setThoughtLevel", new { sessionId = threadId, thoughtLevel = initialEffort, expectedRevision = zcodeRevision }));
                    if (reasoningEffort != initialEffort) throw new AiOperationException("SETTINGS_OUTCOME_UNKNOWN");
                }
            } else if (agent.Protocol == "claude") {
                if (modelId.Length > 0) await ClaudeControlAsync(new { subtype = "set_model", model = modelId });
                if (threadId.Length == 0) threadId = Id;
            } else if (agent.Protocol == "acp") {
                await StartAcpSessionAsync();
            }
            if (threadId.Length == 0) throw new AiOperationException("AGENT_PROTOCOL_INCOMPATIBLE");
            lock (sync) { if (state == "starting") state = "idle"; SaveArchive(); }
        } catch (Exception failure) {
            Fail(failure is AiOperationException known ? known.Code : "AGENT_START_FAILED");
            throw;
        }
    }

    private async Task<JsonElement> InitializeAsync()
    {
            var args = agent.Protocol == "claude" ? new[] { "-p", "--verbose", "--input-format", "stream-json",
                "--output-format", "stream-json", "--include-partial-messages", "--permission-prompt-tool", "stdio", "--permission-mode", ClaudeMode(permissionMode),
                threadId.Length == 0 ? "--session-id" : "--resume", threadId.Length == 0 ? Id : threadId }
                : agent.Protocol == "acp" ? new[] { "--permission-mode", permissionMode == "full-access" ? "bypassPermissions" : "default", "agent", "--no-leader", "stdio" }
                : new[] { "app-server" };
            if (agent.Protocol == "claude" && AdvancedConfiguration(agent)) args = args.Concat(new[] { "--allow-dangerously-skip-permissions" }).ToArray();
            if (agent.Protocol == "claude" && reasoningEffort.Length > 0) args = args.Concat(new[] { "--effort", reasoningEffort }).ToArray();
            lock (sync) {
                if (disposed) throw new AiOperationException("SESSION_ENDED");
                process = Process.Start(AiAgentDiscovery.StartInfo(agent, directory, args)) ?? throw new IOException();
                job = new AiProcessJob(process);
            }
            _ = ReadOutputAsync();
            _ = DrainErrorsAsync();
            if (!discovering) _ = ExpireApprovalsAsync();
            if (agent.Protocol == "codex") {
                await RpcAsync("initialize", new { clientInfo = new { name = "termlink", version = "1" }, capabilities = new { experimentalApi = true } });
                await WriteAsync(new { method = "initialized" });
            } else if (agent.Protocol == "zcode") {
                await RpcAsync("runtime/capabilities", new { });
            } else if (agent.Protocol == "claude") {
                var initialized = await ClaudeControlAsync(new { subtype = "initialize", hooks = new { } });
                var commands = P(initialized, "commands");
                if (commands.ValueKind == JsonValueKind.Array) slashCommands = commands.EnumerateArray().Select(c => S(c, "name")).Where(c => c.Length is > 0 and < 80 && !c.Any(char.IsControl)).Take(100).ToArray();
                return initialized;
            } else if (agent.Protocol == "acp") {
                return await InitializeAcpAsync();
            } else throw new AiOperationException("AGENT_PROTOCOL_UNSUPPORTED");
            return default;
    }

    internal sealed record ModelOption(string ModelId, string Name, string ProviderId = "", bool IsDefault = false, ReasoningOptions Reasoning = null);
    internal sealed record ModelList(ModelOption[] Models, bool Truncated);

    // Reuse the protocol reader without creating a conversation, workspace, or user turn.
    internal static async Task<ModelList> ListModelsAsync(AiAgentInstallation agent, CancellationToken token)
    {
        if (agent.Strategy != "native" || agent.Protocol is not ("codex" or "claude" or "acp"))
            throw new AiOperationException("MODEL_LIST_UNSUPPORTED");
        using var probe = new AiAgentSession(agent, Path.GetTempPath(), Guid.Empty) { discovering = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var cancel = timeout.Token.Register(probe.Dispose);
        try {
            var initialized = await probe.InitializeAsync();
            var result = await probe.ReadModelsAsync(initialized);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            throw new AiOperationException("MODEL_LIST_TIMEOUT");
        }
    }

    private async Task<ModelList> ReadModelsAsync(JsonElement initialized)
    {
            if (agent.Protocol == "acp") return ParseAcpModels(P(P(initialized, "_meta"), "modelState"));
            var models = new List<ModelOption>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var responseBytes = 0;
            bool truncated = false;
            void Add(JsonElement values, bool claude) {
                if (values.ValueKind != JsonValueKind.Array) throw new AiOperationException("MODEL_LIST_UNAVAILABLE");
                foreach (var value in values.EnumerateArray()) {
                    if (P(value, "hidden").ValueKind == JsonValueKind.True) continue;
                    var id = S(value, claude ? "value" : "model");
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl) || !keys.Add(id)) continue;
                    if (models.Count >= 200) { truncated = true; break; }
                    var name = S(value, "displayName");
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)) name = id;
                    var option = new ModelOption(id, name, IsDefault: P(value, "isDefault").ValueKind == JsonValueKind.True, Reasoning: ParseReasoning(value, claude));
                    responseBytes += JsonSerializer.SerializeToUtf8Bytes(option, AiSessionManager.Json).Length;
                    if (responseBytes > 700 * 1024) { truncated = true; break; }
                    models.Add(option);
                }
            }
            if (agent.Protocol == "claude") Add(P(initialized, "models"), true);
            else {
                string cursor = null;
                var cursors = new HashSet<string>(StringComparer.Ordinal);
                for (var page = 0; page < 8; page++) {
                    var result = await RpcAsync("model/list", new { cursor, limit = 50, includeHidden = false });
                    Add(P(result, "data"), false);
                    cursor = S(result, "nextCursor");
                    if (cursor.Length == 0) break;
                    if (cursor.Length > 4096 || !cursors.Add(cursor)) throw new AiOperationException("MODEL_LIST_UNAVAILABLE");
                    if (truncated || models.Count >= 200 || page == 7) { truncated = true; break; }
                }
            }
            return new(models.ToArray(), truncated);
    }

    internal object Snapshot(Guid viewer, bool content, Message[] changed = null, bool reset = false, bool renewControl = true)
    {
        lock (sync) {
            ExpireLease();
            EnsureMessageIndex();
            var alive = IsAlive;
            if (owner == viewer && content && renewControl) leaseExpires = DateTime.UtcNow.AddMinutes(2);
            return new { id = Id, kind = "ai", agentId = agent.Id, agentName = agent.Name, directory,
                state = disposed ? "exited" : state, error, controlVersion, canControl = owner == viewer && alive,
                authentication, truncated, title, modelId, providerId, permissionMode, settingsVersion,
                reasoningEffort, reasoning = ReasoningFor(modelId, providerId), settingsNotice,
                configurationScope = agent.Protocol == "codex" ? "next_turn" : "session", capabilities = Capabilities(),
                historyBefore = messages.Count > RecentMessages().Length ? historyBase + messages.Count - RecentMessages().Length : 0, historyCount = messages.Count,
                usage, compacting, diff = Clip(diff, 16000), diffLength = diff.Length, diffTruncated, slashCommands, archiveError, streamId, cursor = revision.ToString(System.Globalization.CultureInfo.InvariantCulture), reset,
                phase = pending.Count > 0 ? "approval" : compacting ? "compacting" : state == "running" ? phase : state,
                turnStartedAtMs, lastActivityAtMs, plan,
                preview = Clip(messages.LastOrDefault(m => m.Role != "tool" && m.Kind != "reasoning")?.Text ?? "", 160),
                pendingCount = pending.Count,
                messages = content ? changed ?? RecentMessages() : Array.Empty<Message>(),
                pending = content ? pending.Values.Select(p => new { p.Id, p.Kind, p.Detail, p.Questions,
                    title = ToolPresentation(p.Parameters.GetRawText(), p.Method).Title,
                    summary = ToolPresentation(p.Parameters.GetRawText(), p.Method).Summary,
                    canApprove = p.Parameters.GetRawText().Length <= 8000 && (p.Method != "session/request_permission" ||
                        P(p.Parameters, "options").ValueKind == JsonValueKind.Array && P(p.Parameters, "options").EnumerateArray().Any(o => S(o, "kind") == "allow_once" && ValidOption(S(o, "optionId"), 512))),
                    expiresAtMs = new DateTimeOffset(p.Expires).ToUnixTimeMilliseconds() }).ToArray() : null };
        }
    }

    internal async Task ApplyAsync(Guid caller, AiSessionManager.Request request, Func<bool> authorized = null)
    {
        await actions.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try {
            if (authorized != null && !authorized()) throw new AiOperationException("AUTHORITY_CHANGED");
            lock (sync) {
                ExpireLease();
                if (!IsAlive) throw new AiOperationException("SESSION_ENDED");
                if (request.Action == "control") { owner = caller; controlVersion++; leaseExpires = DateTime.UtcNow.AddMinutes(2); return; }
                if (owner != caller || request.ControlVersion != controlVersion) throw new AiOperationException("CONTROL_CHANGED");
                leaseExpires = DateTime.UtcNow.AddMinutes(2);
                if (request.Action == "release") { owner = Guid.Empty; controlVersion++; return; }
            }
            switch (request.Action) {
                case "configure": await ConfigureAsync(request); break;
                case "rename":
                    if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 80 || request.Text.Any(char.IsControl)) throw new AiOperationException("INVALID_TITLE");
                    lock (sync) { title = request.Text.Trim(); SaveArchive(); } break;
                case "steer":
                    if (agent.Protocol != "codex" || state != "running" || pending.Count != 0 || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 16000) throw new AiOperationException("STEER_UNAVAILABLE");
                    try { await RpcAsync("turn/steer", new { threadId, expectedTurnId = turnId, input = new[] { new { type = "text", text = request.Text } } }); }
                    catch (AiOperationException e) when (e.Code == "AGENT_TIMEOUT") { Put("user-" + request.OperationId, "user", request.Text, status: "uncertain"); Fail("SEND_OUTCOME_UNKNOWN"); throw; }
                    Put("user-" + request.OperationId, "user", request.Text, status: "accepted"); break;
                case "attachment.begin": case "attachment.chunk": case "attachment.commit": case "attachment.remove":
                    lock (sync) AttachmentAction(caller, request); break;
                case "send":
                    if (request.Text.Length > 16000 || string.IsNullOrWhiteSpace(request.Text) && (request.AttachmentIds?.Length ?? 0) == 0 && (request.FileReferences?.Length ?? 0) == 0) throw new AiOperationException("INVALID_MESSAGE");
                    var references = request.FileReferences ?? Array.Empty<string>();
                    if (references.Length > 5 || references.Any(p => p == null || p.Length > 4096 || p.Any(char.IsControl) || !Path.IsPathFullyQualified(p) || p.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(p))) throw new AiOperationException("INVALID_FILE_REFERENCE");
                    Attachment[] selectedAttachments;
                    lock (sync) {
                        if (state != "idle" || pending.Count != 0) throw new AiOperationException("AGENT_BUSY");
                        selectedAttachments = ResolveAttachments(caller, request.AttachmentIds);
                        turnStartedAtMs = 0; BeginTurn();
                        error = "";
                    }
                    try {
                        var prompt = AttachmentPrompt(request.Text, selectedAttachments) + string.Concat(references.Select(p => "\nUser selected local file: " + JsonSerializer.Serialize(p)));
                        Put("user-" + request.OperationId, "user", request.Text + string.Concat(selectedAttachments.Select(a => "\n[附件] " + a.Name)) + string.Concat(references.Select(p => "\n[电脑文件] " + p)), status: "accepted");
                        lock (sync) { SaveArchive(); }
                        if (agent.Protocol == "codex") {
                            var input = new List<object> { new { type = "text", text = prompt } };
                            input.AddRange(selectedAttachments.Where(a => a.IsImage).Select(a => (object)new { type = "localImage", path = a.Path }));
                            input.AddRange(references.Select(p => (object)new { type = "mention", name = Path.GetFileName(p), path = p }));
                            var started = await RpcAsync("turn/start", new { threadId, input, model = modelId.Length > 0 ? modelId : null,
                                effort = EffectiveEffort(), approvalPolicy = nativePermissions != null ? (object)nativePermissions.Approval : CodexApproval(permissionMode),
                                approvalsReviewer = nativePermissions?.Reviewer ?? CodexReviewer(permissionMode), sandboxPolicy = nativePermissions != null ? (object)nativePermissions.Sandbox : CodexSandboxPolicy() });
                            turnId = S(P(started, "turn"), "id");
                        } else if (agent.Protocol == "zcode") {
                            await RpcAsync("session/send", new { sessionId = threadId, content = prompt, inputId = request.OperationId });
                        } else if (agent.Protocol == "acp") {
                            await SendAcpPromptAsync(prompt, selectedAttachments);
                        } else {
                            var content = new List<object> { new { type = "text", text = prompt } };
                            content.AddRange(selectedAttachments.Where(a => a.IsImage).Select(a => (object)new { type = "image", source = new { type = "base64", media_type = a.Mime, data = Convert.ToBase64String(File.ReadAllBytes(a.Path)) } }));
                            await WriteAsync(new { type = "user", session_id = threadId, message = new { role = "user", content }, parent_tool_use_id = (string)null });
                        }
                    } catch { Fail("SEND_OUTCOME_UNKNOWN"); throw; }
                    break;
                case "interrupt":
                    if (agent.Protocol == "codex") await RpcAsync("turn/interrupt", new { threadId, turnId });
                    else if (agent.Protocol == "zcode") await RpcAsync("session/stop", new { sessionId = threadId });
                    else if (agent.Protocol == "acp") await CancelAcpPromptAsync();
                    else await ClaudeControlAsync(new { subtype = "interrupt" });
                    break;
                case "answer":
                    Pending item;
                    lock (sync) {
                        if (!pending.TryGetValue(request.RequestId ?? "", out item) || item.Expires <= DateTime.UtcNow)
                            throw new AiOperationException("APPROVAL_EXPIRED");
                    }
                    if (request.Decision is not ("allow" or "deny")) throw new AiOperationException("INVALID_DECISION");
                    await AnswerAsync(item, request.Decision == "allow", request.Answers).ConfigureAwait(false);
                    lock (sync) pending.Remove(item.Id);
                    break;
                case "end":
                    Dispose();
                    if (process != null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    break;
                default: throw new AiOperationException("UNKNOWN_ACTION");
            }
        } finally { PublishChange(); actions.Release(); }
    }

    private void ExpireLease()
    { if (owner != Guid.Empty && leaseExpires <= DateTime.UtcNow) { owner = Guid.Empty; controlVersion++; PublishChange(); } }

    private async Task<JsonElement> RpcAsync(string method, object parameters)
    {
        var sequence = Interlocked.Increment(ref nextCall);
        var id = agent.Protocol == "acp" ? sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : "tl-" + sequence;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!calls.TryAdd(id, completion)) throw new InvalidOperationException();
        try {
            if (agent.Protocol == "acp") await WriteAsync(new { jsonrpc = "2.0", id = sequence, method, @params = parameters });
            else await WriteAsync(new { id, method, @params = parameters });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token).ConfigureAwait(false);
        } catch (TimeoutException) { throw new AiOperationException("AGENT_TIMEOUT"); }
        finally { calls.TryRemove(id, out _); }
    }

    private async Task<JsonElement> ClaudeControlAsync(object request)
    {
        var id = "tl-" + Interlocked.Increment(ref nextCall);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls[id] = completion;
        try {
            await WriteAsync(new { type = "control_request", request_id = id, request });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token).ConfigureAwait(false);
        } catch (TimeoutException) { throw new AiOperationException("AGENT_TIMEOUT"); }
        finally { calls.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object value)
    {
        var line = AiSessionManager.Encode(value);
        await writes.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { await process.StandardInput.WriteLineAsync(line.AsMemory(), lifetime.Token).ConfigureAwait(false); await process.StandardInput.FlushAsync(lifetime.Token); }
        finally { writes.Release(); }
    }

    private async Task ReadOutputAsync()
    {
        try {
            var buffer = new char[4096]; var line = new StringBuilder(); int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), lifetime.Token)) > 0) {
                for (var i = 0; i < count; i++) {
                    if (buffer[i] == '\n') {
                        if (line.Length > 0) { using var json = JsonDocument.Parse(line.ToString()); await ReceiveAsync(json.RootElement); PublishChange(); }
                        line.Clear();
                    } else { if (line.Length >= 1024 * 1024) throw new IOException(); line.Append(buffer[i]); }
                }
            }
            Fail("AGENT_EXITED"); PublishChange();
        } catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or OperationCanceledException) {
            Fail("AGENT_STREAM_LOST"); PublishChange();
        }
    }

    private async Task DrainErrorsAsync()
    {
        try { var buffer = new char[2048]; while (await process.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token) > 0) { } }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private async Task ReceiveAsync(JsonElement value)
    {
        lock (sync) if (disposed || state == "interrupted") return;
        if (agent.Protocol == "claude") { await ReceiveClaudeAsync(value); return; }
        if (agent.Protocol == "acp") { await ReceiveAcpAsync(value); return; }
        var id = P(value, "id"); var method = S(value, "method"); var parameters = P(value, "params");
        if (id.ValueKind != JsonValueKind.Undefined && method.Length == 0 && calls.TryGetValue(id.ToString(), out var call)) {
            if (P(value, "error").ValueKind != JsonValueKind.Undefined) call.TrySetException(new AiOperationException("AGENT_REQUEST_REJECTED"));
            else call.TrySetResult(P(value, "result").Clone());
            return;
        }
        if (id.ValueKind != JsonValueKind.Undefined && method.Length > 0) {
            if (discovering) { await WriteAsync(new { id, error = new { code = -32601, message = "Model discovery does not allow interactions" } }); return; }
            if (agent.Protocol == "zcode" && method == "interaction/requestProviderRuntimeHeaders") {
                lock (sync) error = "AGENT_DESKTOP_AUTH_REQUIRED";
                await WriteAsync(new { id, result = new { headersApplied = false,
                    errorMessage = "TermLink does not provide desktop account credentials. Configure a CLI API provider on the host." } });
                return;
            }
            if (agent.Protocol == "zcode" && method == "session/requestRuntimePreferences") {
                await WriteAsync(new { id, result = new { nativeSearchEnhancementsEnabled = true,
                    memoryEnabled = false, askUserQuestionAutoResolutionEnabled = false, modelContextBudgetStrategy = "preflight-v1" } });
                return;
            }
            if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or
                "item/tool/requestUserInput" or "interaction/requestPermission" or "interaction/requestUserInput") {
                var kind = method.Contains("UserInput", StringComparison.Ordinal) ? "question" : "approval";
                AddPending(id, method, parameters, kind);
            } else await WriteAsync(new { id, error = new { code = -32601, message = "Unsupported interaction" } });
            return;
        }
        if (agent.Protocol == "codex") {
            var item = P(parameters, "item"); var type = S(item, "type"); var itemId = S(item, "id");
            switch (method) {
                case "item/agentMessage/delta": Put(S(parameters, "itemId"), "assistant", S(parameters, "delta"), true); break;
                case "item/reasoning/summaryTextDelta":
                    var summaryIndex = P(parameters, "summaryIndex").ValueKind == JsonValueKind.Number ? P(parameters, "summaryIndex").GetInt32() : 0;
                    Put(S(parameters, "itemId") + "/summary/" + summaryIndex, "assistant", S(parameters, "delta"), true, "reasoning", "running"); break;
                case "turn/plan/updated": ReadPlan(parameters); break;
                case "item/started":
                    if (type == "contextCompaction") lock (sync) compacting = true;
                    if (type is "commandExecution" or "fileChange" or "mcpToolCall" or "collabAgentToolCall") Put(itemId, "tool", item.GetRawText(), kind: type, status: "running");
                    break;
                case "item/completed":
                    if (type == "contextCompaction") lock (sync) compacting = false;
                    if (type == "agentMessage") Put(itemId, "assistant", S(item, "text"));
                    else if (type == "reasoning") CodexReasoning(item);
                    else if (type is "commandExecution" or "fileChange" or "mcpToolCall" or "collabAgentToolCall") Put(itemId, "tool", item.GetRawText(), kind: type);
                    break;
                case "turn/started": lock (sync) { BeginTurn(); turnId = S(P(parameters, "turn"), "id"); } break;
                case "turn/completed": CompleteTurn(S(P(parameters, "turn"), "status") == "failed"); break;
                case "thread/tokenUsage/updated": lock (sync) usage = P(parameters, "tokenUsage").Clone(); break;
                case "turn/diff/updated": UpdateDiff(S(parameters, "diff")); break;
                case "thread/compacted": lock (sync) compacting = false; break;
                case "error": lock (sync) error = "AGENT_REPORTED_ERROR"; break;
                case "serverRequest/resolved":
                    RemovePending(P(parameters, "requestId")); break;
            }
        } else if (method == "session/event") ReceiveZcodeEvent(parameters);
    }

    private async Task ReceiveClaudeAsync(JsonElement value)
    {
        var type = S(value, "type");
        if (type == "control_response") {
            var response = P(value, "response");
            if (calls.TryGetValue(S(response, "request_id"), out var call)) {
                if (S(response, "subtype") == "success") {
                    var result = P(response, "response");
                    call.TrySetResult(result.ValueKind == JsonValueKind.Undefined ? default : result.Clone());
                }
                else call.TrySetException(new AiOperationException("AGENT_REQUEST_REJECTED"));
            }
        } else if (type == "control_request") {
            var request = P(value, "request");
            if (!discovering && S(request, "subtype") == "can_use_tool") {
                AddPending(P(value, "request_id"), "claude/tool", request,
                    S(request, "tool_name") == "AskUserQuestion" ? "question" : "approval");
            } else await WriteAsync(new { type = "control_response", response = new { subtype = "error", request_id = S(value, "request_id"), error = "Unsupported interaction" } });
        } else if (type == "stream_event") {
            ReceiveClaudeStream(value);
        } else if (type == "assistant") {
            var message = P(value, "message"); var content = P(message, "content"); var index = 0;
            if (content.ValueKind == JsonValueKind.Array) foreach (var part in content.EnumerateArray()) {
                var key = S(message, "id") + "/" + ClaudeFinalBlockIndex(value, message, part, index++, content.GetArrayLength());
                if (S(part, "type") == "text") Put(key, "assistant", S(part, "text"));
                else if (S(part, "type") == "thinking") Put(key, "assistant", S(part, "thinking"), kind: "reasoning", status: "completed");
                else if (S(part, "type") == "tool_use") Put("tool-" + S(part, "id"), "tool", part.GetRawText(), kind: "tool_use", status: "running");
            }
        } else if (type == "user") {
            var content = P(P(value, "message"), "content");
            if (content.ValueKind == JsonValueKind.Array) foreach (var part in content.EnumerateArray()) {
                if (S(part, "type") != "tool_result") continue;
                var key = "tool-" + S(part, "tool_use_id");
                lock (sync) {
                    EnsureMessageIndex();
                    if (messageIndex.TryGetValue(key, out var index)) ReplaceMessage(index, messages[index] with {
                        Text = Clip(messages[index].Text + "\n" + part.GetRawText(), 1024 * 1024),
                        Status = P(part, "is_error").ValueKind == JsonValueKind.True ? "failed" : "completed" });
                    TrimMessages();
                }
            }
        } else if (type == "result") {
            lock (sync) { var reported = P(value, "usage"); if (reported.ValueKind == JsonValueKind.Object) usage = JsonSerializer.SerializeToElement(new { tokens = reported, costUsd = P(value, "total_cost_usd").ValueKind == JsonValueKind.Number ? P(value, "total_cost_usd").GetDecimal() : (decimal?)null }); }
            CompleteTurn(P(value, "is_error").ValueKind == JsonValueKind.True);
        } else if (type == "system" && S(value, "subtype") == "init") {
            lock (sync) { var native = S(value, "session_id"); if (native.Length > 0) threadId = native; }
        } else if (type == "system" && S(value, "subtype") == "compact_boundary") {
            lock (sync) compacting = false;
        } else if (type == "system" && S(value, "subtype") == "status") {
            lock (sync) compacting = S(value, "status") == "compacting";
        } else if (type == "system" && S(value, "subtype") == "api_retry") {
            lock (sync) error = "AGENT_NETWORK_RETRY";
        } else if (type == "control_cancel_request") RemovePending(P(value, "request_id"));
    }

    private void ReceiveZcodeEvent(JsonElement value)
    {
        var type = S(value, "type"); var payload = P(value, "payload");
        if (type == "model.streaming" && S(payload, "kind") == "text_delta")
            Put(S(payload, "assistantMessageId"), "assistant", S(payload, "delta"), true);
        if (type == "tool.updated") Put("tool-" + S(payload, "toolCallId"), "tool", payload.GetRawText(), kind: "tool");
        if (type == "permission.resolved") {
            lock (sync) foreach (var item in pending.Values.Where(p =>
                S(p.Parameters, "requestId") == S(payload, "requestId")).ToArray()) pending.Remove(item.Id);
        }
        if (type == "turn.started") BeginTurn();
        if (type is "turn.completed" or "turn.failed") {
            CompleteTurn(type == "turn.failed");
            lock (sync) { if (type == "turn.failed" && error != "AGENT_DESKTOP_AUTH_REQUIRED") error =
                S(P(payload, "error"), "code") == "CONFIGURATION_ERROR" ? "AGENT_MODEL_REQUIRED" : "AGENT_REPORTED_ERROR"; }
        }
        // The provider's message events carry authoritative text; unknown events never become user input.
        if (type == "message.upserted") {
            var message = P(payload, "message"); if (message.ValueKind == JsonValueKind.Undefined) message = payload;
            var text = S(message, "content"); if (text.Length == 0) text = S(message, "text");
            if (text.Length > 0) Put(S(message, "id"), S(message, "role") == "user" ? "user" : "assistant", text);
        }
        if (type is "session.state" or "session/state" or "state") {
            var status = S(payload, "status");
            lock (sync) if (status is "idle" or "completed" or "paused") { state = "idle"; pending.Clear(); }
        }
    }

    private void AddPending(JsonElement rpcId, string method, JsonElement parameters, string kind)
    {
        var questions = P(method == "claude/tool" ? P(parameters, "input") : parameters, "questions");
        var parsed = new List<Question>();
        if (parameters.GetRawText().Length <= 8000 && questions.ValueKind == JsonValueKind.Array) foreach (var question in questions.EnumerateArray().Take(16)) {
            var text = S(question, "question");
            var key = method is "claude/tool" or "interaction/requestUserInput" ? text : S(question, "id");
            if (key.Length == 0) key = S(question, "header");
            var options = P(question, "options");
            parsed.Add(new Question(Clip(key, 1000), Clip(text, 2000), options.ValueKind == JsonValueKind.Array
                ? options.EnumerateArray().Take(16).Select(o => Clip(S(o, "label"), 500)).ToArray() : Array.Empty<string>(),
                P(question, "multiSelect").ValueKind == JsonValueKind.True));
        }
        lock (sync) {
            var duplicate = pending.Values.FirstOrDefault(p => p.Method == method &&
                (p.RpcId.ToString() == rpcId.ToString() || S(parameters, "requestId").Length > 0 &&
                    S(p.Parameters, "requestId") == S(parameters, "requestId")));
            if (duplicate != null) {
                pending[duplicate.Id] = duplicate with { RpcId = rpcId.Clone() };
                return;
            }
            if (pending.Count >= 8) throw new IOException("Too many interactions.");
            var id = Guid.NewGuid().ToString("D");
            pending.Add(id, new Pending(id, rpcId.Clone(), method, parameters.Clone(), kind,
                Clip(parameters.GetRawText(), 8000), parsed.ToArray(), DateTime.UtcNow.AddMinutes(10)));
        }
    }

    private async Task AnswerAsync(Pending item, bool allow, Dictionary<string, string[]> answers)
    {
        if (allow && item.Parameters.GetRawText().Length > 8000) throw new AiOperationException("INTERACTION_TOO_LARGE");
        if (allow && item.Kind == "question" && (answers == null || item.Questions.Length == 0 ||
            item.Questions.Any(q => !answers.TryGetValue(q.Id, out var a) || a == null || a.Length is < 1 or > 16 || a.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 4000))))
            throw new AiOperationException("ANSWER_REQUIRED");
        object response;
        if (item.Method == "session/request_permission") {
            await AnswerAcpPermissionAsync(item.RpcId, item.Parameters, allow);
            return;
        }
        if (item.Method == "claude/tool") {
            var input = P(item.Parameters, "input");
            var updated = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.GetRawText()) ?? new();
            if (item.Kind == "question" && allow) updated["answers"] = JsonSerializer.SerializeToElement(answers.ToDictionary(p => p.Key, p => string.Join(", ", p.Value)));
            response = allow ? new { behavior = "allow", updatedInput = (object)updated } : (object)new { behavior = "deny", message = "Denied by TermLink user" };
            await WriteAsync(new { type = "control_response", response = new { subtype = "success", request_id = item.RpcId.GetString(), response } });
            return;
        }
        if (item.Method == "interaction/requestPermission") response = new { decision = allow ? "allow" : "deny" };
        else if (item.Method == "interaction/requestUserInput") {
            // ZCode's visible label can differ from the protocol value (e.g. Approve -> approve).
            var mapped = new Dictionary<string, string[]>();
            if (allow) foreach (var question in P(item.Parameters, "questions").EnumerateArray().Take(item.Questions.Length)) {
                var key = S(question, "question");
                var options = P(question, "options");
                mapped[key] = answers[Clip(key, 1000)].Select(answer => {
                    if (options.ValueKind == JsonValueKind.Array) foreach (var option in options.EnumerateArray())
                        if (S(option, "label") == answer && S(option, "value").Length > 0) return S(option, "value");
                    return answer;
                }).ToArray();
            }
            response = allow ? new { action = "accept", content = (object)new { answers = mapped } } : (object)new { action = "decline" };
        }
        else if (item.Kind == "question") response = new { answers = (allow ? answers : new()).ToDictionary(p => p.Key, p => new { answers = p.Value }) };
        else response = new { decision = allow ? "accept" : "decline" };
        await WriteAsync(new { id = item.RpcId, result = response });
    }

    private async Task ExpireApprovalsAsync()
    {
        try {
            while (!lifetime.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                await actions.WaitAsync(lifetime.Token);
                try {
                    Pending[] expired; lock (sync) expired = pending.Values.Where(p => p.Expires <= DateTime.UtcNow).ToArray();
                    foreach (var item in expired) { await AnswerAsync(item, false, null); lock (sync) { pending.Remove(item.Id); PublishChange(); } }
                } finally { actions.Release(); }
            }
        } catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException) { }
    }

    private void RemovePending(JsonElement providerId)
    {
        lock (sync) foreach (var item in pending.Values.Where(p => p.RpcId.ToString() == providerId.ToString()).ToArray()) pending.Remove(item.Id);
    }

    private void CompleteTurn(bool failed)
    {
        lock (sync) {
            if (disposed || state == "interrupted") return;
            state = "idle"; pending.Clear();
            SettleUnconfirmedTools();
            if (failed) { if (error != "AGENT_DESKTOP_AUTH_REQUIRED") error = "AGENT_REPORTED_ERROR"; }
            else { error = ""; if (messages.Any(m => m.Role == "assistant")) authentication = "verified"; }
            EnsureMessageIndex(); phase = failed ? "failed" : "idle";
            for (var i = messages.Count - 1; i >= 0; i--) if (messages[i].Role == "user") { ReplaceMessage(i, messages[i] with { Status = failed ? "failed" : "completed" }); break; }
            SaveArchive();
        }
    }

    private void Put(string id, string role, string text, bool append = false, string kind = "", string status = "")
    {
        lock (sync) {
            if (disposed || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(text)) return;
            if (role == "user" && title.Length == 0) title = Clip(text.Replace('\r', ' ').Replace('\n', ' ').Trim(), 80);
            EnsureMessageIndex();
            var index = messageIndex.TryGetValue(id, out var existing) ? existing : -1;
            if (append && index >= 0) text = messages[index].Text + text;
            var presentation = role == "tool" ? ToolPresentation(text, kind) : (Title: "", Summary: "", Status: "");
            var limit = 1024 * 1024;
            if (text.Length > limit) { text = Clip(text, limit); truncated = true; }
            var message = new Message(id, role, text, role == "tool" ? "tool" : kind == "reasoning" ? "reasoning" : "text", presentation.Title, presentation.Summary,
                status.Length > 0 ? status : presentation.Status, modelId, permissionMode);
            if (index < 0) { messageIndex[id] = messages.Count; messages.Add(message); messageCharacters += text.Length; messageRevisions[id] = PublishChange(); }
            else ReplaceMessage(index, message);
            lastActivityAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (state == "running") phase = kind == "reasoning" ? "thinking" : role == "tool" ? "tool" : role == "assistant" ? "generating" : "waiting";
            TrimMessages();
        }
    }

    private void TrimMessages() {
        // Page the transcript instead of deleting it after 128 messages. A bounded host
        // retention ceiling prevents an untrusted provider from exhausting process memory.
        while (messages.Count > 10000 || messageCharacters > 12 * 1024 * 1024) {
            var removed = messages[0]; messageCharacters -= removed.Text.Length;
            messages.RemoveAt(0); messageIndex.Remove(removed.Id); messageRevisions.Remove(removed.Id);
            historyFloor = ++revision; historyBase++; truncated = true;
            messageIndex.Clear(); EnsureMessageIndex();
        }
    }

    private void SettleUnconfirmedTools() {
        EnsureMessageIndex();
        for (var i = 0; i < messages.Count; i++)
                if (messages[i].Status == "running") ReplaceMessage(i, messages[i] with { Status = messages[i].Kind == "reasoning" ? "completed" : "unknown" });
    }

    internal static (string Title, string Summary, string Status) ToolPresentation(string text, string kind = "")
    {
        try {
            using var document = JsonDocument.Parse(text);
            var value = document.RootElement;
            var type = kind.Length == 0 ? S(value, "type") : kind;
            if (type == "session/request_permission") value = P(value, "toolCall");
            var name = First(value, "tool_name", "toolName", "name", "tool");
            var title = type switch {
                "acp" or "session/request_permission" => S(value, "title").Length > 0 ? S(value, "title") : "工具操作",
                "commandExecution" => "运行命令",
                "fileChange" => "修改文件",
                "mcpToolCall" => "调用 MCP 工具",
                "collabAgentToolCall" => "子 Agent 任务",
                "item/commandExecution/requestApproval" => "运行命令",
                "item/fileChange/requestApproval" => "修改文件",
                _ => name.ToLowerInvariant() switch {
                    "bash" or "shell" or "shell_command" => "运行命令",
                    "read" or "read_file" => "读取文件",
                    "edit" or "write" or "multiedit" or "apply_patch" => "修改文件",
                    "glob" or "grep" => "搜索文件",
                    _ => name.Length > 0 ? name : "工具操作",
                },
            };
            var summary = First(value, "command", "cmd", "file_path", "path", "filePath", "pattern", "description", "reason");
            if (summary.Length == 0 && P(value, "changes").ValueKind == JsonValueKind.Array)
                summary = string.Join(" · ", P(value, "changes").EnumerateArray().Take(4).Select(c => S(c, "path")));
            if (summary.Length == 0) summary = name;
            var status = S(value, "status"); if (status.Length == 0) status = S(value, "state");
            status = status switch {
                "completed" or "success" or "succeeded" => "completed",
                "failed" or "error" => "failed", "declined" or "denied" => "denied",
                "cancelled" or "canceled" => "cancelled", "inProgress" or "running" or "pending" => "running", _ => "unknown",
            };
            var exitCode = P(value, "exitCode");
            if (exitCode.ValueKind == JsonValueKind.Number && exitCode.TryGetInt32(out var code) && code != 0) status = "failed";
            return (Clip(title.Replace('\r', ' ').Replace('\n', ' '), 80), Clip(summary.Replace('\r', ' ').Replace('\n', ' ').Trim(), 240), status);
        } catch (JsonException) { return ("工具操作", "展开查看记录", "unknown"); }
    }

    private static string First(JsonElement value, params string[] names)
    {
        foreach (var name in names) {
            var text = S(value, name); if (text.Length > 0) return text;
            var nested = P(value, "input"); text = S(nested, name); if (text.Length > 0) return text;
            nested = P(value, "item"); text = S(nested, name); if (text.Length > 0) return text;
        }
        return "";
    }

    private void Fail(string code)
    {
        lock (sync) {
            if (disposed || state == "interrupted") return; state = "interrupted"; error = code; pending.Clear(); owner = Guid.Empty; SettleUnconfirmedTools();
            for (var i = messages.Count - 1; i >= 0; i--) if (messages[i].Role == "user") { if (messages[i].Status == "accepted") ReplaceMessage(i, messages[i] with { Status = "uncertain" }); break; }
            PublishChange();
            SaveArchive();
        }
        foreach (var call in calls.Values) call.TrySetException(new AiOperationException(code));
        lifetime.Cancel();
        Kill();
    }
    private void Kill() {
        job?.Dispose();
        try { if (process != null && !process.HasExited) process.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; state = "exited"; pending.Clear(); owner = Guid.Empty; SettleUnconfirmedTools(); RemoveUnusedAttachments(); SaveArchive(); PublishChange(); }
        lifetime.Cancel(); Kill();
        foreach (var call in calls.Values) call.TrySetCanceled();
    }
    internal static JsonElement P(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    internal static string S(JsonElement value, string name) => P(value, name).ValueKind == JsonValueKind.String ? P(value, name).GetString() ?? "" : "";
    private static string Clip(string value, int limit) => value.Length > limit ? value[..limit] + "…" : value;
}
