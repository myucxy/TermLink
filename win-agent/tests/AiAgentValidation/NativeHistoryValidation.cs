using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class NativeHistoryValidation
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static JsonElement Snap(AiAgentSession s, Guid caller) => JsonSerializer.SerializeToElement(s.Snapshot(caller, true), AiSessionManager.Json);
    internal static async Task Run(string directory)
    {
        var root = Path.Combine(directory, "claude-config", "projects"); var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var id = Guid.NewGuid().ToString(); var path = Path.Combine(project, id + ".jsonl");
        File.WriteAllLines(path, new[] {
            AiSessionManager.Encode(new { type = "user", sessionId = id, uuid = "u1", cwd = directory, permissionMode = "default", message = new { content = "first prompt" } }),
            "{invalid",
            AiSessionManager.Encode(new { type = "assistant", sessionId = id, uuid = "a1", message = new { model = "claude-test", content = new[] { new { type = "text", text = "answer" }, new { type = "tool_use", text = "not visible" } } } }),
            AiSessionManager.Encode(new { type = "user", sessionId = id, uuid = "u2", cwd = directory, permissionMode = "bypassPermissions", message = new { content = "latest" } }),
            AiSessionManager.Encode(new { type = "custom-title", sessionId = id, customTitle = "Actual title" }),
            AiSessionManager.Encode(new { type = "user", sessionId = id, isSidechain = true, cwd = "wrong", permissionMode = "plan", message = new { content = "sidechain" } }) });
        var read = ClaudeNativeHistory.Read(path, id, true, root);
        Check(read.Entry.Title == "Actual title" && read.Entry.Directory == directory && read.Entry.PermissionMode == "full-access" && read.Entry.Model == "claude-test", "Claude metadata/mode mismatch");
        Check(read.Messages.Length == 3 && read.Messages[1].Text == "answer", "Claude content filtering");
        File.AppendAllText(path, "\n" + AiSessionManager.Encode(new { type = "assistant", sessionId = id, uuid = "a1", message = new { content = "answer updated" } }));
        var repeated = ClaudeNativeHistory.Read(path, id, true, root);
        Check(repeated.Messages.Length == 3 && repeated.Messages[1].Text == "answer updated", "Duplicate native IDs would break Android timeline keys");
        Check(!ClaudeNativeHistory.SafeFile(Path.Combine(directory, id + ".jsonl"), root), "Traversal accepted");
        for (var n = 0; n < 31; n++) File.Copy(path, Path.Combine(project, Guid.NewGuid() + ".jsonl"));
        var first = ClaudeNativeHistory.List("", CancellationToken.None, root);
        Check(first.NextCursor == "30", "Bounded history paging");
        try { ClaudeNativeHistory.List("../bad", CancellationToken.None, root); throw new Exception("Bad cursor accepted"); } catch (AiOperationException e) when (e.Code == "INVALID_CURSOR") { }
        var previousRoot = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.GetDirectoryName(root));
            var claude = new AiAgentInstallation("claude", "Claude", Environment.ProcessPath, "", "2.1.116", "claude", "available");
            using var imported = AiAgentSession.Import(claude, read.Entry, Guid.NewGuid(), null);
            Check(imported.ArchiveRecord().Mode == "full-access", "Claude historical permission reset");
            File.WriteAllText(path, AiSessionManager.Encode(new { type = "user", sessionId = id, cwd = directory, permissionMode = "unknown-new-mode" }));
            try { AiAgentSession.Import(claude, read.Entry, Guid.NewGuid(), null); throw new Exception("Unknown mode silently accepted"); } catch (AiOperationException e) when (e.Code == "HISTORICAL_PERMISSION_UNAVAILABLE") { }
        } finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousRoot); }
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_CONFIGURATION", "1");
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", "codex");
        Environment.SetEnvironmentVariable("TERMLINK_AI_NATIVE_FIXTURE", "1");
        var trace = Path.Combine(directory, "native-history-frames.jsonl");
        Environment.SetEnvironmentVariable("TERMLINK_AI_CONFIGURATION_TRACE", trace);
        try {
            var agent = new AiAgentInstallation("codex", "Codex", Environment.ProcessPath, "", "0.160.0", "codex", "available");
            var caller = Guid.NewGuid();
            using var s = AiAgentSession.Import(agent, new(id, "Native title", directory, 0), caller, null);
            await s.StartAsync();
            Check(s.ArchiveRecord().NativePermissions?.Reviewer == "auto_review", "Native reviewer lost");
            using var restored = AiAgentSession.Restore(agent, s.ArchiveRecord(), null, caller);
            await restored.ResumeAsync();
            await restored.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "config test"));
            var frames = File.ReadAllLines(trace).Where(l => l.StartsWith('{')).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
            Check(frames.Where(f => AiAgentSession.S(f, "method") == "thread/resume").All(f => !f.GetProperty("params").TryGetProperty("approvalPolicy", out _)), "Resume overwrote original permissions");
            var turn = frames.Last(f => AiAgentSession.S(f, "method") == "turn/start").GetProperty("params");
            Check(turn.GetProperty("approvalPolicy").ValueKind == JsonValueKind.Object && turn.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean(), "Custom permission details lost on next turn");
        } finally { foreach (var name in new[] { "TERMLINK_AI_FIXTURE_CONFIGURATION", "TERMLINK_AI_CONFIGURATION_TRACE", "TERMLINK_AI_NATIVE_FIXTURE" }) Environment.SetEnvironmentVariable(name, null); }
        Console.WriteLine("PASS native history: bounded parser, title, latest permission, filtering, cursor/path guards, exact Codex policy resume/archive/turn");
    }
    internal static async Task Real(string directory, string only)
    {
        foreach (var agent in await AiAgentDiscovery.DetectAsync()) {
            if (!AiAgentSession.SupportsNativeHistory(agent) || only.Length > 0 && agent.Id != only) continue;
            var caller = Guid.NewGuid(); string id;
            var mode = agent.Protocol == "claude" ? "plan" : "read-only";
            using (var original = new AiAgentSession(agent, directory, caller, mode: mode)) {
                await original.StartAsync();
                await original.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Remember marker TERMLINK_NATIVE_HISTORY_7319. Reply OK only. Do not use tools."));
                await Idle(original, caller);
                id = original.ArchiveRecord().NativeId;
            }
            AiNativeHistoryEntry entry = null; var cursor = "";
            for (var n = 0; n < 6 && entry == null; n++) {
                var page = await AiAgentSession.NativeHistoryAsync(agent, cursor, CancellationToken.None);
                entry = page.Sessions.FirstOrDefault(e => e.NativeId == id); cursor = page.NextCursor;
                if (cursor.Length == 0) break;
            }
            Check(entry != null, "Isolated native session not listed: " + agent.Id);
            using var imported = AiAgentSession.Import(agent, entry, caller, null);
            await imported.StartAsync();
            var record = imported.ArchiveRecord();
            if (agent.Protocol == "claude") Check(record.Mode == mode, "Claude resume permission changed");
            else Check(AiAgentSession.S(record.NativePermissions.Sandbox, "type") == "readOnly", "Codex resume permission changed: " + record.NativePermissions.Sandbox.GetRawText());
            await imported.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Reply only with the exact marker from my earlier message. Do not use tools."));
            await Idle(imported, caller);
            Check(Snap(imported, caller).GetProperty("messages").EnumerateArray().Last(m => AiAgentSession.S(m, "role") == "assistant").GetProperty("text").GetString().Contains("TERMLINK_NATIVE_HISTORY_7319"), "Native context lost");
            Console.WriteLine("PASS REAL " + agent.Id + ": isolated native list/import, historical permission, preserved context reply");
        }
    }
    private static async Task Idle(AiAgentSession session, Guid caller) {
        for (var n = 0; n < 1200; n++) {
            var snap = Snap(session, caller);
            if (AiAgentSession.S(snap, "state") != "running") { Check(AiAgentSession.S(snap, "error").Length == 0, "Native turn error: " + AiAgentSession.S(snap, "error")); return; }
            Check(snap.GetProperty("pendingCount").GetInt32() == 0, "Unexpected tool request"); await Task.Delay(100);
        }
        throw new TimeoutException("Native history turn");
    }
}
