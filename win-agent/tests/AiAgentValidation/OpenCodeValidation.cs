using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class OpenCodeValidation
{
    private static JsonElement Snapshot(AiAgentSession session, Guid caller) => JsonSerializer.SerializeToElement(session.Snapshot(caller, true), AiSessionManager.Json);
    internal static async Task Run(string directory)
    {
        var agent = new AiAgentInstallation("opencode", "OpenCode", Environment.ProcessPath!, "", "1.19.0", "acp", "available");
        var page = await AiAgentSession.NativeHistoryAsync(agent, "", default);
        Check(page.Sessions.Length == 30 && page.NextCursor == "30", "OpenCode history paging failed.");
        var last = await AiAgentSession.NativeHistoryAsync(agent, page.NextCursor, default);
        Check(last.Sessions.Length == 2 && last.NextCursor == "", "OpenCode second history page failed.");
        Check(AiAgentSession.ValidOpenCodeSessionId("ses_history_1") && !AiAgentSession.ValidOpenCodeSessionId("ses_../secret"), "OpenCode ID boundary failed.");
        try { await AiAgentSession.NativeHistoryAsync(agent, "../bad", default); throw new Exception("Bad cursor accepted."); }
        catch (AiOperationException e) when (e.Code == "INVALID_CURSOR") { }
        var caller = Guid.NewGuid();
        using var imported = AiAgentSession.Import(agent, page.Sessions[0], caller, null);
        var notice = Snapshot(imported, caller).GetProperty("settingsNotice").GetString();
        await imported.StartAsync();
        Check(Snapshot(imported, caller).GetProperty("settingsNotice").GetString() == notice, "ACP model discovery incorrectly replaced the historical permission notice before session/load supplied its options.");
        Check(imported.ArchiveRecord().Messages.Any(m => m.Role == "user" && m.Text == "prior user") &&
            imported.ArchiveRecord().Messages.Any(m => m.Role == "assistant" && m.Text == "prior reply"), "OpenCode native replay missing.");
        await imported.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "continue"));
        await Idle(imported, caller);
        var record = imported.ArchiveRecord();
        Check(record.NativeId == page.Sessions[0].NativeId && record.Messages.Last(m => m.Role == "assistant").Text == "continued", "OpenCode resume changed native ID.");
        await imported.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "permission"));
        for (var n = 0; n < 100 && Snapshot(imported, caller).GetProperty("pendingCount").GetInt32() == 0; n++) await Task.Delay(20);
        var approval = Snapshot(imported, caller).GetProperty("pending")[0];
        var answer = new AiSessionManager.Request("answer", RequestId: approval.GetProperty("id").GetString(), Decision: "deny", ControlVersion: 1);
        try { await imported.ApplyAsync(Guid.NewGuid(), answer); throw new Exception("Foreign caller answered an OpenCode approval."); }
        catch (AiOperationException e) when (e.Code == "CONTROL_CHANGED") { }
        await imported.ApplyAsync(caller, answer);
        await Idle(imported, caller);
        Check(imported.ArchiveRecord().Messages.Last(m => m.Role == "assistant").Text == "permission rejected", "OpenCode approval denial was not propagated.");
        record = imported.ArchiveRecord();
        using var restored = AiAgentSession.Restore(agent, record, null, caller);
        await restored.ResumeAsync();
        Check(restored.ArchiveRecord().Messages.Length == record.Messages.Length, "Archived replay duplicated OpenCode messages.");
        Console.WriteLine("PASS OpenCode: native list pagination, opaque ID validation, import/replay, same-thread continuation, approval denial/control fence, archive resume without duplicate history.");
    }

    internal static async Task Real(string directory)
    {
        var agent = (await AiAgentDiscovery.DetectAsync()).Single(a => a.Id == "opencode");
        Console.WriteLine(JsonSerializer.Serialize(new { agent.Path, agent.Status, agent.Version }));
        Check(agent.Status == "available", "Installed OpenCode unavailable.");
        var caller = Guid.NewGuid();
        var marker = "TERMLINK_OPENCODE_CONTEXT_" + Guid.NewGuid().ToString("N");
        string nativeId;
        using (var original = new AiAgentSession(agent, directory, caller)) {
            await original.StartAsync();
            Console.WriteLine("OpenCode ACP initialized; new native session created.");
            var snapshot = Snapshot(original, caller);
            Console.WriteLine(JsonSerializer.Serialize(new { modelId = AiAgentSession.S(snapshot, "modelId") }));
            await original.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Remember this exact marker: " + marker + ". Reply OK only. Do not use tools."));
            await Idle(original, caller);
            nativeId = original.ArchiveRecord().NativeId;
        }
        Console.WriteLine("OpenCode seed reply completed; listing native history.");
        AiNativeHistoryEntry entry = null;
        var cursor = "";
        do {
            var page = await AiAgentSession.NativeHistoryAsync(agent, cursor, default);
            entry = page.Sessions.FirstOrDefault(s => s.NativeId == nativeId); cursor = page.NextCursor;
        } while (entry == null && cursor.Length > 0);
        Check(entry != null, "OpenCode native session missing.");
        using var imported = AiAgentSession.Import(agent, entry, caller, null);
        await imported.StartAsync();
        Check(imported.ArchiveRecord().Messages.Any(m => m.Role == "user" && m.Text.Contains(marker)), "OpenCode history body missing.");
        await imported.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Reply only with the exact marker from my earlier message. Do not use tools."));
        await Idle(imported, caller);
        var result = imported.ArchiveRecord();
        Check(result.NativeId == nativeId && result.Messages.Last(m => m.Role == "assistant" && m.Kind != "reasoning").Text.Contains(marker), "OpenCode native context lost.");
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, version = agent.Version, nativeId, sameNativeThread = true, historyReplay = true, contextReplyMatched = true }));
    }

    private static async Task Idle(AiAgentSession session, Guid caller)
    {
        for (var n = 0; n < 1200; n++) {
            var snapshot = Snapshot(session, caller);
            var error = AiAgentSession.S(snapshot, "error");
            Check(error.Length == 0, "OpenCode turn failed: " + error);
            Check(snapshot.GetProperty("pendingCount").GetInt32() == 0, "Unexpected test tool approval.");
            if (AiAgentSession.S(snapshot, "state") == "idle") return;
            await Task.Delay(100);
        }
        throw new TimeoutException("OpenCode turn timed out.");
    }

    internal static async Task Fixture()
    {
        JsonElement pendingPrompt = default;
        var sessionId = "";
        string line;
        while ((line = await Console.In.ReadLineAsync()) != null) {
            using var document = JsonDocument.Parse(line); var frame = document.RootElement;
            var id = AiAgentSession.P(frame, "id"); var method = AiAgentSession.S(frame, "method"); var p = AiAgentSession.P(frame, "params");
            if (AiAgentSession.S(p, "sessionId").Length > 0) sessionId = AiAgentSession.S(p, "sessionId");
            void Reply(object value) => Console.WriteLine(AiSessionManager.Encode(new { jsonrpc = "2.0", id, result = value }));
            void Update(string kind, string text) => Console.WriteLine(AiSessionManager.Encode(new { jsonrpc = "2.0", method = "session/update",
                @params = new { sessionId, update = new { sessionUpdate = kind, content = new { type = "text", text } } } }));
            if (method == "initialize") Reply(new { protocolVersion = 1, agentCapabilities = new { loadSession = true } });
            else if (method == "session/list") Reply(new { sessions = Enumerable.Range(0, 32).Select(i => new { sessionId = "ses_history_" + i, cwd = Path.GetTempPath(), title = "history " + i, updatedAt = DateTimeOffset.UtcNow.ToString("O") }).ToArray() });
            else if (method == "session/load") {
                Check(!p.TryGetProperty("_meta", out _), "Grok auto-approval flags leaked to OpenCode.");
                Update("user_message_chunk", "prior user"); Update("agent_message_chunk", "prior reply");
                Reply(new { models = new { currentModelId = "provider/model", availableModels = new[] { new { modelId = "provider/model", name = "Model" } } } });
            } else if (method == "session/new") Reply(new { sessionId = "ses_history_new", models = new { currentModelId = "provider/model" } });
            else if (method == "session/prompt") {
                if (p.GetProperty("prompt")[0].GetProperty("text").GetString() == "permission") {
                    pendingPrompt = id.Clone();
                    Console.WriteLine(AiSessionManager.Encode(new { jsonrpc = "2.0", id = "oc-permission", method = "session/request_permission", @params = new {
                        sessionId, toolCall = new { toolCallId = "write-test", title = "Write fixture", kind = "edit" },
                        options = new[] { new { optionId = "once", kind = "allow_once", name = "Allow once" }, new { optionId = "reject", kind = "reject_once", name = "Reject" } }
                    } }));
                } else { Update("agent_message_chunk", "continued"); Reply(new { stopReason = "end_turn" }); }
            }
            else if (id.ValueKind == JsonValueKind.String && id.GetString() == "oc-permission") {
                Check(AiAgentSession.S(AiAgentSession.P(AiAgentSession.P(frame, "result"), "outcome"), "optionId") == "reject", "OpenCode permission was automatically approved.");
                Update("agent_message_chunk", "permission rejected");
                Console.WriteLine(AiSessionManager.Encode(new { jsonrpc = "2.0", id = pendingPrompt, result = new { stopReason = "end_turn" } }));
            }
            else if (id.ValueKind == JsonValueKind.Number) Reply(new { });
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
