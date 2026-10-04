using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class AcpValidation
{
    private static JsonElement Snap(AiAgentSession s, Guid caller) => JsonSerializer.SerializeToElement(s.Snapshot(caller, true), AiSessionManager.Json);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("ACP: " + message); }
    private static async Task Wait(Func<bool> condition) {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(30);
        Check(condition(), "condition timed out");
    }
    private static async Task Reject(Func<Task> action, string expected) {
        try { await action(); } catch (AiOperationException e) when (e.Code == expected) { return; }
        throw new Exception("ACP expected " + expected);
    }
    private static AiAgentInstallation Installation() => new("grok", "Grok Build", Environment.ProcessPath, "", "1.0.46", "acp", "available");
    private static AiSessionManager.Request Configure(JsonElement state, string model = null, string effort = null, string mode = null) =>
        new("configure", ControlVersion: state.GetProperty("controlVersion").GetInt64(), ExpectedSettingsVersion: state.GetProperty("settingsVersion").GetInt64(),
            ModelId: model ?? state.GetProperty("modelId").GetString(), ProviderId: "", ReasoningEffort: effort, PermissionMode: mode ?? state.GetProperty("permissionMode").GetString());
    private static Task Send(AiAgentSession session, Guid caller, string text, long control = 1) => session.ApplyAsync(caller,
        new("send", Text: text, ControlVersion: control, OperationId: Guid.NewGuid().ToString()));

    internal static async Task ProbeInstalled()
    {
        var installed = (await AiAgentDiscovery.DetectAsync()).Single(a => a.Id == "grok");
        Check(installed.Status == "available" && installed.Protocol == "acp", "installed Grok detection");
        var list = await AiAgentSession.ListModelsAsync(installed, CancellationToken.None);
        Check(list.Models.Length > 0, "native read-only model discovery");
        Console.WriteLine($"Grok {installed.Version}: {installed.Path}; models={list.Models.Length}");
        foreach (var model in list.Models) Console.WriteLine($"  {model.ModelId}: {string.Join(",", model.Reasoning.Options.Select(o => o.Value))}");
    }

    internal static async Task Run(string directory)
    {
        var agent = Installation(); var caller = Guid.NewGuid();
        var list = await AiAgentSession.ListModelsAsync(agent, CancellationToken.None);
        Check(list.Models.Length == 2 && list.Models[0].Reasoning.Options.Length == 2, "model dedup and per-model efforts");
        Check(list.Models[1].Reasoning.Options.Single().Value == "minimal", "effort must belong to model");
        await Reject(() => { AiAgentSession.ValidatePermissionSelection(agent, "full-access", ""); return Task.CompletedTask; }, "PERMISSION_CONFIRMATION_REQUIRED");
        using var s = new AiAgentSession(agent, directory, caller);
        await s.StartAsync();
        Check(Snap(s, caller).GetProperty("modelId").GetString() == "model-a", "native default model");
        Check(!Snap(s, caller).GetProperty("capabilities").GetProperty("nativeImages").GetBoolean(), "image capability must be negotiated");
        Check(!Snap(s, caller).GetProperty("capabilities").GetProperty("configuration").GetProperty("permissionSwitch").GetBoolean(), "no unverified live grant switch");
        await Reject(() => s.ApplyAsync(caller, Configure(Snap(s, caller), mode: "full-access")), "PERMISSION_REQUIRES_NEW_SESSION");
        await s.ApplyAsync(caller, Configure(Snap(s, caller), effort: "low"));
        Check(Snap(s, caller).GetProperty("reasoningEffort").GetString() == "low", "effort ACK");
        await Reject(() => s.ApplyAsync(caller, Configure(Snap(s, caller), effort: "invented")), "INVALID_REASONING_EFFORT");
        await s.ApplyAsync(caller, Configure(Snap(s, caller), model: "model-b"));
        Check(Snap(s, caller).GetProperty("reasoningEffort").GetString() == "minimal", "model changes effort list");
        await Reject(() => s.ApplyAsync(caller, Configure(Snap(s, caller), model: "rejected-model")), "AGENT_REQUEST_REJECTED");
        Check(Snap(s, caller).GetProperty("modelId").GetString() == "model-b", "rejected config must not apply locally");
        await Send(s, caller, "stream");
        await Wait(() => Snap(s, caller).GetProperty("state").GetString() == "idle");
        var messages = Snap(s, caller).GetProperty("messages").EnumerateArray().ToArray();
        Check(messages.Count(m => m.GetProperty("role").GetString() == "user") == 1, "ignore user echo");
        Check(messages.Count(m => m.GetProperty("text").GetString() == "3000") == 1, "true deltas must not overlap-deduplicate");
        Check(messages.Count(m => m.GetProperty("kind").GetString() == "reasoning") == 1, "separate thought stream");
        var tool = messages.Single(m => m.GetProperty("role").GetString() == "tool");
        Check(tool.GetProperty("title").GetString() == "Read fixture" && tool.GetProperty("status").GetString() == "completed", "partial tool update retains title");
        Check(Snap(s, caller).GetProperty("plan")[0].GetProperty("step").GetString() == "Fixture plan", "plan rendering");
        foreach (var test in new[] { ("approve", "allow", "selected-once"), ("approve", "deny", "selected-reject"), ("always-only", "deny", "cancelled") }) {
            await Send(s, caller, test.Item1).WaitAsync(TimeSpan.FromSeconds(1));
            await Wait(() => Snap(s, caller).GetProperty("pendingCount").GetInt32() == 1);
            var approval = Snap(s, caller).GetProperty("pending")[0];
            var request = new AiSessionManager.Request("answer", ControlVersion: 1, RequestId: approval.GetProperty("id").GetString(), Decision: test.Item2);
            await Reject(() => s.ApplyAsync(Guid.NewGuid(), request), "CONTROL_CHANGED");
            if (test.Item1 == "always-only") {
                Check(!approval.GetProperty("canApprove").GetBoolean(), "no one-shot approval available");
                await Reject(() => s.ApplyAsync(caller, request with { Decision = "allow" }), "APPROVAL_OPTION_UNSUPPORTED");
            }
            await s.ApplyAsync(caller, request).WaitAsync(TimeSpan.FromSeconds(1));
            await Wait(() => Snap(s, caller).GetProperty("state").GetString() == "idle");
            Check(Snap(s, caller).GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("text").GetString() == test.Item3), "permission wire result");
            await Reject(() => s.ApplyAsync(caller, request), "APPROVAL_EXPIRED");
        }
        await Send(s, caller, "foreign-session");
        await Wait(() => Snap(s, caller).GetProperty("state").GetString() == "idle");
        Check(!Snap(s, caller).GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("text").GetString() == "FOREIGN"), "wrong-session data rejected");
        await Send(s, caller, "unsupported");
        await Wait(() => Snap(s, caller).GetProperty("state").GetString() == "idle");
        foreach (var retryCase in new[] { "rate-limit", "retry-recover", "foreign-retry" }) {
            await Send(s, caller, retryCase);
            if (retryCase == "rate-limit") {
                await Wait(() => Snap(s, caller).GetProperty("error").GetString() == "AGENT_RATE_LIMITED");
                Check(Snap(s, caller).GetProperty("phase").GetString() == "retrying", "retry phase is visible");
            } else {
                await Wait(() => Snap(s, caller).GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("text").GetString() == retryCase));
                Check(Snap(s, caller).GetProperty("error").GetString() == "", "progress clears retry; foreign retry ignored");
            }
            await s.ApplyAsync(caller, new("interrupt", ControlVersion: 1)).WaitAsync(TimeSpan.FromSeconds(1));
            await Wait(() => Snap(s, caller).GetProperty("state").GetString() == "idle");
            Check(Snap(s, caller).GetProperty("error").GetString() == "", "cancellation clears transient retry");
        }
        await Send(s, caller, "cancel");
        await s.ApplyAsync(caller, new("interrupt", ControlVersion: 1)).WaitAsync(TimeSpan.FromSeconds(1));
        await Wait(() => Snap(s, caller).GetProperty("state").GetString() == "idle");
        await Send(s, caller, "approve");
        await Wait(() => Snap(s, caller).GetProperty("pendingCount").GetInt32() == 1);
        var cancelledApproval = Snap(s, caller).GetProperty("pending")[0].GetProperty("id").GetString();
        await s.ApplyAsync(caller, new("interrupt", ControlVersion: 1));
        await Reject(() => s.ApplyAsync(caller, new("answer", ControlVersion: 1, RequestId: cancelledApproval, Decision: "allow")), "APPROVAL_EXPIRED");
        await Wait(() => Snap(s, caller).GetProperty("state").GetString() == "idle");
        var record = s.ArchiveRecord(); s.Dispose();
        using var restored = AiAgentSession.Restore(agent, record, null, caller);
        Check(Snap(restored, caller).GetProperty("capabilities").GetProperty("resume").GetBoolean(), "archive exposes resume attempt");
        await restored.ResumeAsync();
        Check(Snap(restored, caller).GetProperty("messages").GetArrayLength() == record.Messages.Length, "load replay must not duplicate archive");
        Check(Snap(restored, caller).GetProperty("modelId").GetString() == "model-b", "restored model reapplied");
        await Send(restored, caller, "stream");
        await Wait(() => Snap(restored, caller).GetProperty("state").GetString() == "idle");
        Check(Snap(restored, caller).GetProperty("messages").GetArrayLength() > record.Messages.Length, "resumed conversation accepts a new turn");
        foreach (var message in record.Messages) Check(Snap(restored, caller).GetProperty("messages").EnumerateArray().Any(m =>
            m.GetProperty("id").GetString() == message.Id && m.GetProperty("text").GetString() == message.Text), "resumed request IDs must not overwrite archive");
        using (var privileged = new AiAgentSession(agent, directory, caller, mode: "full-access")) {
            await privileged.StartAsync();
            Check(Snap(privileged, caller).GetProperty("permissionMode").GetString() == "full-access", "explicit create grant retained");
        }
        using (var exiting = new AiAgentSession(agent, directory, caller)) {
            await exiting.StartAsync(); await Send(exiting, caller, "exit");
            await Wait(() => Snap(exiting, caller).GetProperty("state").GetString() == "interrupted");
            Check(Snap(exiting, caller).GetProperty("pendingCount").GetInt32() == 0, "exit clears pending operations");
        }
        using (var mismatch = new AiAgentSession(agent, directory, caller)) {
            await mismatch.StartAsync();
            await Reject(() => mismatch.ApplyAsync(caller, Configure(Snap(mismatch, caller), model: "bad-ack")), "SETTINGS_OUTCOME_UNKNOWN");
            Check(Snap(mismatch, caller).GetProperty("state").GetString() == "interrupted", "ambiguous config must retire process");
        }
        var authDir = Path.Combine(directory, "acp-auth-required"); Directory.CreateDirectory(authDir);
        using var unauthenticated = new AiAgentSession(agent, authDir, caller);
        await Reject(unauthenticated.StartAsync, "AGENT_AUTH_REQUIRED");
        await Task.Delay(100); // Child shutdown must not replace the original failure with STREAM_LOST.
        Check(Snap(unauthenticated, caller).GetProperty("error").GetString() == "AGENT_AUTH_REQUIRED", "preserve actionable login error");
        var lateConfigDir = Path.Combine(directory, "acp-session-config"); Directory.CreateDirectory(lateConfigDir);
        using (var lateConfig = new AiAgentSession(agent, lateConfigDir, caller, modelId: "model-a", effort: "low")) {
            await lateConfig.StartAsync();
            Check(Snap(lateConfig, caller).GetProperty("reasoningEffort").GetString() == "low", "create uses session config even without initialize metadata");
        }
        using (var invalidEffort = new AiAgentSession(agent, lateConfigDir, caller, modelId: "model-a", effort: "invented")) {
            await Reject(invalidEffort.StartAsync, "INVALID_REASONING_EFFORT");
            await Task.Delay(100);
            Check(Snap(invalidEffort, caller).GetProperty("error").GetString() == "INVALID_REASONING_EFFORT", "invalid session effort rejected and error retained");
        }
        Console.WriteLine("PASS ACP: models/effort, JSON-RPC, stream deltas, tools/plan, nonblocking approval, deny, cancellation, session fence, restore, auth errors");
    }

    internal static async Task Fixture()
    {
        var model = "model-a"; var effort = "high"; var sessionId = "fixture-session";
        JsonElement promptId = default; string permissionCase = "";
        object Models() => new { currentModelId = "model-a", availableModels = new object[] {
            new { modelId = "model-a", name = "Model A", _meta = new { supportsReasoningEffort = true, reasoningEfforts = new[] {
                new { value = "high", @default = true }, new { value = "low", @default = false }, new { value = "low", @default = false }, new { value = "bad\n", @default = false } } } },
            new { modelId = "model-a", name = "Duplicate" },
            new { modelId = "model-b", name = "Model B", _meta = new { supportsReasoningEffort = true, reasoningEfforts = new[] { new { value = "minimal", @default = true } } } },
        } };
        object Config() => new { configOptions = new[] {
            new { id = "model", type = "select", currentValue = model, options = new[] { new { value = "model-a" }, new { value = "model-b" } } },
            new { id = "reasoning_effort", type = "select", currentValue = effort, options = model == "model-a" ? new[] { new { value = "high" }, new { value = "low" } } : new[] { new { value = "minimal" } } },
        } };
        void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
        void Reply(JsonElement id, object result) => Emit(new { jsonrpc = "2.0", id, result });
        void Update(object update, string sid = null) => Emit(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = sid ?? sessionId, update } });
        void Text(string text) => Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });
        void Done(string reason = "end_turn") { Reply(promptId, new { stopReason = reason }); promptId = default; }
        while (await Console.In.ReadLineAsync() is { } line) {
            using var document = JsonDocument.Parse(line); var frame = document.RootElement;
            Check(AiAgentSession.S(frame, "jsonrpc") == "2.0", "JSON-RPC version required");
            var id = AiAgentSession.P(frame, "id"); var p = AiAgentSession.P(frame, "params"); var method = AiAgentSession.S(frame, "method");
            if (method.Length > 0 && method != "session/cancel") Check(id.ValueKind == JsonValueKind.Number, "numeric request ids");
            switch (method) {
                case "initialize":
                    Check(!AiAgentSession.P(AiAgentSession.P(p, "clientCapabilities"), "terminal").GetBoolean(), "do not claim client terminal support");
                    Reply(id, new { protocolVersion = 1, agentCapabilities = new { loadSession = true, promptCapabilities = new { image = false } },
                        _meta = new { modelState = Environment.CurrentDirectory.EndsWith("acp-session-config") ? new { availableModels = Array.Empty<object>() } : Models() } }); break;
                case "session/new": case "session/load":
                    if (AiAgentSession.S(p, "cwd").EndsWith("acp-auth-required")) { Emit(new { jsonrpc = "2.0", id, error = new { code = -32000, message = "Authentication required" } }); break; }
                    Check(AiAgentSession.P(p, "mcpServers").GetArrayLength() == 0, "no injected servers");
                    var command = Environment.GetCommandLineArgs();
                    Check(AiAgentSession.P(AiAgentSession.P(p, "_meta"), "yoloMode").GetBoolean() == command.Contains("bypassPermissions"), "grant matches process policy");
                    if (method == "session/load") Text("ARCHIVED REPLAY");
                    Reply(id, new { sessionId, models = Models(), configOptions = JsonSerializer.SerializeToElement(Config()).GetProperty("configOptions") }); break;
                case "session/set_config_option":
                    Check(AiAgentSession.S(p, "sessionId") == sessionId, "configuration session");
                    Check(AiAgentSession.P(p, "value").ValueKind == JsonValueKind.String, "config option value must be a scalar string (native Grok 1.0.46)");
                    var value = AiAgentSession.S(p, "value");
                    if (value == "rejected-model") { Emit(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "unknown model" } }); break; }
                    if (value == "bad-ack") { Reply(id, new { }); break; }
                    if (AiAgentSession.S(p, "configId") == "model") { model = value; effort = model == "model-a" ? "high" : "minimal"; } else effort = value;
                    Reply(id, Config()); break;
                case "session/prompt":
                    Check(promptId.ValueKind == JsonValueKind.Undefined, "only one turn at a time");
                    promptId = id.Clone(); var text = AiAgentSession.S(AiAgentSession.P(p, "prompt")[0], "text");
                    if (text == "exit") return;
                    if (text == "cancel") break;
                    if (text is "rate-limit" or "retry-recover" or "foreign-retry") {
                        Emit(new { jsonrpc = "2.0", method = "_x.ai/session_notification", @params = new {
                            sessionId = text == "foreign-retry" ? "other-session" : sessionId,
                            update = new { sessionUpdate = "retry_state", type = "retrying", error_type = "rate_limited", reason = "private upstream detail" },
                        } });
                        if (text != "rate-limit") Text(text);
                        break;
                    }
                    if (text is "approve" or "always-only" or "foreign-session") {
                        permissionCase = text;
                        if (text == "foreign-session") Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "FOREIGN" } }, "other-session");
                        Emit(new { jsonrpc = "2.0", id = "approval", method = "session/request_permission", @params = new {
                            sessionId = text == "foreign-session" ? "other-session" : sessionId,
                            toolCall = new { toolCallId = "tool-1", title = "Write fixture" }, options = text == "always-only"
                                ? new[] { new { optionId = "always", kind = "allow_always" } }
                                : new[] { new { optionId = "always", kind = "allow_always" }, new { optionId = "once", kind = "allow_once" }, new { optionId = "reject", kind = "reject_once" } },
                        } }); break;
                    }
                    if (text == "unsupported") { Emit(new { jsonrpc = "2.0", id = "filesystem", method = "fs/read_text_file", @params = new { sessionId, path = "never-read" } }); break; }
                    Update(new { sessionUpdate = "user_message_chunk", content = new { type = "text", text } });
                    Update(new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "Thinking" } });
                    Update(new { sessionUpdate = "tool_call", toolCallId = "t1", title = "Read fixture", status = "in_progress" });
                    Update(new { sessionUpdate = "tool_call_update", toolCallId = "t1", status = "completed", rawOutput = "ok" });
                    Update(new { sessionUpdate = "plan", entries = new[] { new { content = "Fixture plan", status = "completed" } } });
                    Text("300"); Text("0"); Done(); break;
                case "session/cancel": Check(id.ValueKind == JsonValueKind.Undefined, "cancel is a notification"); Done("cancelled"); break;
                case "":
                    if (id.ToString() == "filesystem") Check(AiAgentSession.P(frame, "error").GetProperty("code").GetInt32() == -32601, "unsupported interaction rejected");
                    else {
                        var outcome = AiAgentSession.P(AiAgentSession.P(frame, "result"), "outcome"); var selected = AiAgentSession.S(outcome, "optionId");
                        Check(selected != "always", "must never choose persistent permission");
                        if (permissionCase == "foreign-session") Check(selected == "reject" || AiAgentSession.S(outcome, "outcome") == "cancelled", "foreign approval denied");
                        Text(selected.Length > 0 ? "selected-" + selected : "cancelled");
                    }
                    Done(); break;
                default: throw new Exception("Unexpected ACP method: " + method);
            }
        }
    }
}
