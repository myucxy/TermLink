using System.Reflection;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class TimelineValidation
{
    internal static async Task Run(string directory)
    {
        var viewer = Guid.NewGuid();
        using var claude = new AiAgentSession(new AiAgentInstallation("claude", "Claude", Environment.ProcessPath!, "", "1", "claude", "available"), directory, viewer);
        await Receive(claude, "ReceiveClaudeAsync", new { type = "stream_event", @event = new { type = "message_start", message = new { id = "m1" } } });
        await Receive(claude, "ReceiveClaudeAsync", new { type = "stream_event", @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Hello" } } });
        var before = Snap(claude, viewer);
        await Receive(claude, "ReceiveClaudeAsync", new { type = "stream_event", @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = " world" } } });
        await Receive(claude, "ReceiveClaudeAsync", new { type = "assistant", message = new { id = "m1", content = new[] { new { type = "text", text = "Hello world" } } } });
        var changed = JsonSerializer.SerializeToElement(claude.Changes(viewer, before.GetProperty("streamId").GetString(), before.GetProperty("cursor").GetString()), AiSessionManager.Json);
        Check(changed.GetProperty("messages").GetArrayLength() == 1 && changed.GetProperty("messages")[0].GetProperty("text").GetString() == "Hello world", "Claude deltas/final duplicated");
        var unchanged = JsonSerializer.SerializeToElement(claude.Changes(viewer, changed.GetProperty("streamId").GetString(), changed.GetProperty("cursor").GetString()), AiSessionManager.Json);
        Check(unchanged.GetProperty("messages").GetArrayLength() == 0, "No changes must not repeat text");
        await Receive(claude, "ReceiveClaudeAsync", new { type = "stream_event", @event = new { type = "content_block_delta", index = 1, delta = new { type = "thinking_delta", thinking = "Public summary" } } });
        await Receive(claude, "ReceiveClaudeAsync", new { type = "stream_event", @event = new { type = "content_block_delta", index = 1, delta = new { type = "signature_delta", signature = "PRIVATE_SIGNATURE" } } });
        var thought = Snap(claude, viewer).GetProperty("messages")[1];
        Check(thought.GetProperty("kind").GetString() == "reasoning" && thought.GetProperty("text").GetString() == "Public summary", "Reasoning/signature boundary");
        var reset = JsonSerializer.SerializeToElement(claude.Changes(viewer, "old-host", "0"), AiSessionManager.Json);
        Check(reset.GetProperty("reset").GetBoolean(), "Foreign stream must reset");
        Check(JsonSerializer.SerializeToElement(claude.Search("WORLD", 0), AiSessionManager.Json).GetProperty("items").GetArrayLength() == 1, "Host text search");
        var watchBefore = Snap(claude, viewer);
        var watch = claude.WatchAsync(viewer, watchBefore.GetProperty("streamId").GetString(), watchBefore.GetProperty("cursor").GetString(), CancellationToken.None);
        await Task.Delay(30); Check(!watch.IsCompleted, "Idle watch must wait instead of repeating snapshots");
        await Receive(claude, "ReceiveClaudeAsync", new { type = "stream_event", @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "!" } } });
        var update = JsonSerializer.SerializeToElement(await watch.WaitAsync(TimeSpan.FromSeconds(2)), AiSessionManager.Json);
        Check(update.GetProperty("messages").GetArrayLength() == 1, "Watch did not wake with bounded change");
        using (var cancel = new CancellationTokenSource(50)) {
            try { await claude.WatchAsync(viewer, update.GetProperty("streamId").GetString(), update.GetProperty("cursor").GetString(), cancel.Token); throw new Exception("Cancelled watch returned"); }
            catch (OperationCanceledException) { }
        }

        using var codex = new AiAgentSession(new AiAgentInstallation("codex", "Codex", Environment.ProcessPath!, "", "1", "codex", "available"), directory, viewer);
        await Receive(codex, "ReceiveAsync", new { method = "turn/started", @params = new { turn = new { id = "t1" } } });
        await Receive(codex, "ReceiveAsync", new { method = "item/reasoning/summaryTextDelta", @params = new { itemId = "r1", summaryIndex = 0, delta = "Plan briefly" } });
        await Receive(codex, "ReceiveAsync", new { method = "item/completed", @params = new { item = new { id = "r1", type = "reasoning", summary = new[] { new { text = "Plan briefly" } } } } });
        await Receive(codex, "ReceiveAsync", new { method = "turn/plan/updated", @params = new { plan = new[] { new { step = "Test", status = "in_progress" } } } });
        var result = Snap(codex, viewer);
        Check(result.GetProperty("messages").GetArrayLength() == 1 && result.GetProperty("messages")[0].GetProperty("status").GetString() == "completed", "Reasoning delta and complete must merge");
        Check(result.GetProperty("plan")[0].GetProperty("step").GetString() == "Test" && result.GetProperty("turnStartedAtMs").GetInt64() > 0, "Plan and generation clock");
        var largeDiff = new string('x', 32767) + "😀" + new string('y', 40000);
        await Receive(codex, "ReceiveAsync", new { method = "turn/diff/updated", @params = new { diff = largeDiff } });
        var firstDiff = JsonSerializer.SerializeToElement(codex.DiffPage(0, ""), AiSessionManager.Json);
        Check(firstDiff.GetProperty("nextOffset").GetInt64() == 32767, "Diff page split a surrogate pair");
        var diffText = firstDiff.GetProperty("text").GetString(); var diffOffset = firstDiff.GetProperty("nextOffset").GetInt64();
        var diffVersion = firstDiff.GetProperty("version").GetString()!;
        while (diffOffset < largeDiff.Length) {
            var page = JsonSerializer.SerializeToElement(codex.DiffPage(diffOffset, diffVersion), AiSessionManager.Json);
            diffText += page.GetProperty("text").GetString(); diffOffset = page.GetProperty("nextOffset").GetInt64();
        }
        Check(diffText == largeDiff, "Paged diff lost data");
        await Receive(codex, "ReceiveAsync", new { method = "turn/diff/updated", @params = new { diff = "changed" } });
        try { codex.DiffPage(0, diffVersion); throw new Exception("Stale diff version accepted"); } catch (AiOperationException) { }
        try { codex.DiffPage(-1, ""); throw new Exception("Negative diff offset accepted"); } catch (AiOperationException) { }
        await Receive(codex, "ReceiveAsync", new { method = "turn/diff/updated", @params = new { diff = new string('x', 262143) + "😀more" } });
        var cappedDiff = JsonSerializer.SerializeToElement(codex.DiffPage(262143, ""), AiSessionManager.Json);
        Check(cappedDiff.GetProperty("truncated").GetBoolean() && cappedDiff.GetProperty("size").GetInt32() == 262143, "Oversize diff must have an explicit, Unicode-safe retention limit");
        var put = typeof(AiAgentSession).GetMethod("Put", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (var i = 0; i < 10000; i++) put.Invoke(codex, new object[] { "bulk-" + i, "assistant", "x", false, "", "completed" });
        var retained = Snap(codex, viewer); var beforePage = retained.GetProperty("historyBefore").GetInt64();
        var page1 = JsonSerializer.SerializeToElement(codex.History(beforePage), AiSessionManager.Json);
        put.Invoke(codex, new object[] { "newest", "assistant", "new", false, "", "completed" });
        var page2 = JsonSerializer.SerializeToElement(codex.History(beforePage), AiSessionManager.Json);
        Check(page1.GetProperty("messages").GetRawText() == page2.GetProperty("messages").GetRawText(), "History cursor drifted after trimming old messages");
        var gap = JsonSerializer.SerializeToElement(codex.Changes(viewer, result.GetProperty("streamId").GetString(), result.GetProperty("cursor").GetString()), AiSessionManager.Json);
        Check(gap.GetProperty("reset").GetBoolean(), "Retention gaps require explicit snapshot reset");
        var live = Snap(codex, viewer);
        var endingWatch = codex.WatchAsync(viewer, live.GetProperty("streamId").GetString(), live.GetProperty("cursor").GetString(), CancellationToken.None);
        typeof(AiAgentSession).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(codex, new object[] { "AGENT_EXITED" });
        var ending = JsonSerializer.SerializeToElement(await endingWatch.WaitAsync(TimeSpan.FromSeconds(2)), AiSessionManager.Json);
        Check(ending.GetProperty("state").GetString() == "interrupted", "Agent exit must return a terminal snapshot, not a transport failure");
        Console.WriteLine("PASS timeline: protocol delta/final merging, public reasoning, watch wake/cancel, reset, stable history after retention, plan, search");
    }

    private static JsonElement Snap(AiAgentSession session, Guid viewer) => JsonSerializer.SerializeToElement(session.Snapshot(viewer, true), AiSessionManager.Json);
    private static async Task Receive(AiAgentSession session, string method, object value)
    {
        var result = typeof(AiAgentSession).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, new object[] { JsonSerializer.SerializeToElement(value) });
        if (result is Task task) await task;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
