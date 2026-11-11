// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class AiHistoryParityValidation
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await CodexPagesAsync();
        await CodexFallbackAsync();
        await CodexFailureFencesAsync();
        await CodexLimitsAsync();
        ClaudeBlocks(Path.Combine(root, "blocks"));
        ClaudeLimits(Path.Combine(root, "limits"));
        ClaudeWindow(Path.Combine(root, "window"));
        ClaudeBoundaries(Path.Combine(root, "boundary"));
        CapabilityBoundaries();
        Console.WriteLine("PASS AI native history parity: paged Codex, bounded legacy fallback, rich Claude blocks/results, deterministic IDs, limits and failure fences (fixtures only)");
    }

    private static async Task CodexPagesAsync()
    {
        var first = await Read();
        var again = await Read();
        Require(!first.Truncated, "Complete item pages were reported as truncated");
        Require(first.Messages.Select(m => m.Id).SequenceEqual(new[] { "u1", "r1", "c1", "p2", "a2" }), "Newest-first pages did not restore chronological item order or deduplicate overlap");
        Require(JsonSerializer.Serialize(first.Messages) == JsonSerializer.Serialize(again.Messages), "Repeated reads changed message IDs/order/content");
        var input = first.Messages.Single(m => m.Id == "u1");
        Require(input.Text.Contains("question") && input.Text.Contains("/workspace/image.png") && input.Text.Contains("review"), "User references were discarded");
        var reasoning = first.Messages.Single(m => m.Id == "r1");
        Require(reasoning.Kind == "reasoning" && reasoning.Text == "Public summary" && !JsonSerializer.Serialize(first.Messages).Contains("PRIVATE_REASONING"), "Reasoning summary leaked hidden reasoning fields");
        var command = first.Messages.Single(m => m.Id == "c1");
        Require(command.Role == "tool" && command.Status == "completed" && command.Title.Length > 0, "Command history lost normalized presentation/status");
        Require(command.Text.Contains("pwd") && command.Text.Contains("/workspace"), "Command input/output was lost");
        using (var doc = JsonDocument.Parse(command.Text)) Require(doc.RootElement.ValueKind == JsonValueKind.Object, "Tool details are not valid JSON");
        Require(first.Messages.Single(m => m.Id == "p2").Kind == "plan", "Historical plan was flattened without its type");
        var serialized = JsonSerializer.Serialize(first.Messages, AiSessionManager.Json);
        var restored = JsonSerializer.Deserialize<AiAgentSession.Message[]>(serialized, AiSessionManager.Json)!;
        Require(first.Messages.SequenceEqual(restored), "Generic message-array archive roundtrip lost rich history");

        async Task<CodexNativeHistory.ReadResult> Read()
        {
            var script = new Script();
            script.Page("thread/turns/list", new[] { new { id = "t2", status = "completed" } }, "older-turns", view: "notLoaded");
            script.Page("thread/turns/list", new[] { new { id = "t1", status = "completed" } }, cursor: "older-turns", view: "notLoaded");
            script.Page("thread/items/list", new object[] {
                Item("t2", new { id = "a2", type = "agentMessage", text = "finished" }),
                Item("t2", new { id = "p2", type = "plan", text = "1. Inspect\n2. Verify" })
            }, "older-items");
            script.Page("thread/items/list", new object[] {
                Item("t2", new { id = "p2", type = "plan", text = "stale duplicate" }),
                Item("t1", new { id = "c1", type = "commandExecution", command = "pwd", cwd = "/workspace", status = "completed", aggregatedOutput = "/workspace\n", exitCode = 0 }),
                Item("t1", new { id = "r1", type = "reasoning", summary = new[] { "Public summary" }, content = new[] { "PRIVATE_REASONING" }, encryptedContent = "PRIVATE_REASONING" }),
                Item("t1", new { id = "u1", type = "userMessage", content = new object[] { new { type = "text", text = "question" }, new { type = "localImage", path = "/workspace/image.png" }, new { type = "skill", name = "review" } } })
            }, cursor: "older-items");
            var loaded = await CodexNativeHistory.ReadAsync("thread", script.Call);
            script.Done();
            return loaded;
        }
    }

    private static async Task CodexFallbackAsync()
    {
        var full = new Script();
        full.Error("thread/turns/list", "AGENT_INVALID_PARAMS", view: "notLoaded");
        full.Page("thread/turns/list", new[] { new { id = "t2", status = "completed", items = new[] { new { id = "a2", type = "agentMessage", text = "new" } } } }, "older", view: "full");
        full.Page("thread/turns/list", new[] { new { id = "t1", status = "completed", items = new[] { new { id = "a1", type = "agentMessage", text = "old" } } } }, cursor: "older", view: "full");
        var read = await CodexNativeHistory.ReadAsync("thread", full.Call); full.Done();
        Require(!read.Truncated && read.Messages.Select(m => m.Id).SequenceEqual(new[] { "a1", "a2" }), "Full-turn schema fallback did not retain all pages");

        var itemFallback = new Script();
        itemFallback.Page("thread/turns/list", new[] { new { id = "t1", status = "completed" } }, view: "notLoaded");
        itemFallback.Error("thread/items/list", "AGENT_METHOD_NOT_FOUND");
        itemFallback.Page("thread/turns/list", new[] { new { id = "t1", status = "completed", items = new object[] {
            new { id = "u", type = "userMessage", content = new[] { new { type = "text", text = "input" } } },
            new { id = "a", type = "agentMessage", text = "reply" }
        } } }, view: "full");
        read = await CodexNativeHistory.ReadAsync("thread", itemFallback.Call); itemFallback.Done();
        Require(!read.Truncated && read.Messages.Select(m => m.Id).SequenceEqual(new[] { "u", "a" }), "Unsupported item store did not use bounded full turns");

        var summary = new Script();
        summary.Error("thread/turns/list", "AGENT_INVALID_PARAMS", view: "notLoaded");
        summary.Error("thread/turns/list", "AGENT_INVALID_PARAMS", view: "full");
        summary.Page("thread/turns/list", new[] { new { id = "t1", status = "completed", items = new[] { new { id = "a", type = "agentMessage", text = "legacy summary" } } } }, view: "summary");
        read = await CodexNativeHistory.ReadAsync("thread", summary.Call); summary.Done();
        Require(read.Truncated && read.Messages.Single().Text == "legacy summary", "Summary fallback must remain explicitly partial");

        var unsupported = new Script();
        foreach (var view in new[] { "notLoaded", "full", "summary" }) unsupported.Error("thread/turns/list", "AGENT_METHOD_NOT_FOUND", view: view);
        read = await CodexNativeHistory.ReadAsync("thread", unsupported.Call); unsupported.Done();
        Require(read.Truncated && read.Messages.Length == 0, "Unavailable bounded history pretended to be complete");
    }

    private static async Task CodexFailureFencesAsync()
    {
        foreach (var code in new[] { "AUTH_REQUIRED", "AGENT_REQUEST_REJECTED", "NATIVE_HISTORY_TIMEOUT", "AGENT_STREAM_LOST", "STORAGE_DENIED" }) {
            var script = new Script(); script.Error("thread/turns/list", code, view: "notLoaded");
            await ErrorAsync(() => CodexNativeHistory.ReadAsync("thread", script.Call), code); script.Done();
        }
        var later = new Script();
        later.Page("thread/turns/list", new[] { new { id = "t", status = "completed" } }, view: "notLoaded");
        later.Page("thread/items/list", new[] { Item("t", new { id = "a", type = "agentMessage", text = "first page" }) }, "expired-cursor");
        later.Error("thread/items/list", "AGENT_INVALID_PARAMS", cursor: "expired-cursor");
        await ErrorAsync(() => CodexNativeHistory.ReadAsync("thread", later.Call), "NATIVE_HISTORY_PAGE_FAILED"); later.Done();

        var calls = 0;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try {
            await CodexNativeHistory.ReadAsync("thread", (_, _) => { calls++; return Task.FromResult(Json(new { })); }, cancelled.Token);
            throw new Exception("Cancelled native history was accepted");
        } catch (OperationCanceledException) { Require(calls == 0, "Cancelled history issued an RPC"); }

        calls = 0;
        try {
            await CodexNativeHistory.ReadAsync("thread", (_, _) => { calls++; throw new IOException("fixture transport failure"); });
            throw new Exception("Transport failure was swallowed");
        } catch (IOException) { Require(calls == 1, "Transport failure triggered fallback"); }
    }

    private static async Task CodexLimitsAsync()
    {
        var duplicate = new Script();
        duplicate.Page("thread/turns/list", new[] { new { id = "t", status = "completed" } }, view: "notLoaded");
        duplicate.Page("thread/items/list", new[] { Item("t", new { id = "a", type = "agentMessage", text = "answer" }) }, "loop");
        duplicate.Page("thread/items/list", new[] { Item("t", new { id = "a", type = "agentMessage", text = "duplicate" }) }, "loop", cursor: "loop");
        var loaded = await CodexNativeHistory.ReadAsync("thread", duplicate.Call); duplicate.Done();
        Require(loaded.Truncated && loaded.Messages.Length == 1, "Cursor cycle was unbounded or duplicated messages");

        var malformed = new Script();
        malformed.Raw("thread/turns/list", new { data = "bad", nextCursor = (string?)null }, view: "notLoaded");
        malformed.Page("thread/items/list", new object[] { new { turnId = "t", item = "bad" }, Item("t", new { type = "agentMessage", text = "missing id" }) });
        loaded = await CodexNativeHistory.ReadAsync("thread", malformed.Call); malformed.Done();
        Require(loaded.Truncated && loaded.Messages.Single().Id.StartsWith("native-codex-", StringComparison.Ordinal), "Malformed rows/missing IDs did not retain bounded deterministic evidence");

        var oversize = new Script();
        oversize.Page("thread/turns/list", new[] { new { id = "t", status = "completed" } }, view: "notLoaded");
        oversize.Page("thread/items/list", new[] { Item("t", new { id = "huge", type = "agentMessage", text = new string('x', 1024 * 1024) }) });
        loaded = await CodexNativeHistory.ReadAsync("thread", oversize.Call); oversize.Done();
        Require(loaded.Truncated && loaded.Messages.Length == 0, "Oversized RPC page exceeded the history response budget");

        var longText = new Script();
        longText.Page("thread/turns/list", new[] { new { id = "t", status = "completed" } }, view: "notLoaded");
        longText.Page("thread/items/list", new[] { Item("t", new { id = "long", type = "agentMessage", text = new string('x', AiHistoryPresentation.MaxText + 1000) }) });
        loaded = await CodexNativeHistory.ReadAsync("thread", longText.Call); longText.Done();
        Require(loaded.Truncated && loaded.Messages.Single().Text.Length <= AiHistoryPresentation.MaxText && loaded.Messages.Single().Text.Contains("已截断"), "Long message truncation was invisible");

        var requestCount = 0; var pages = 0;
        loaded = await CodexNativeHistory.ReadAsync("thread", (method, parameters) => {
            requestCount++;
            Require(requestCount <= CodexNativeHistory.MaxRequests, "History exceeded its RPC budget");
            if (method == "thread/turns/list") return Task.FromResult(Json(new { data = new[] { new { id = "t", status = "completed" } }, nextCursor = (string?)null }));
            Require(method == "thread/items/list", "History issued a non-read RPC");
            var page = pages++;
            return Task.FromResult(Json(new { data = Enumerable.Range(0, 64).Select(index => Item("t", new {
                id = "item-" + (2000 - page * 64 - index), type = "agentMessage", text = "bounded"
            })).ToArray(), nextCursor = "page-" + pages }));
        });
        Require(loaded.Truncated && loaded.Messages.Length <= CodexNativeHistory.MaxItems && requestCount <= CodexNativeHistory.MaxRequests, "Endless unique cursors escaped history limits");
    }

    private static void ClaudeBlocks(string root)
    {
        var fixture = Transcript(root);
        File.WriteAllLines(fixture.Path, new[] {
            Record(fixture.Id, "user", "u1", "question", root, "default"),
            Record(fixture.Id, "assistant", "a1", new object[] {
                new { type = "thinking", thinking = "Visible thought", signature = "NEVER_COPY_SIGNATURE" },
                new { type = "text", text = "Inspecting" },
                new { type = "tool_use", id = "c1", name = "Bash", input = new { command = "pwd" } },
                new { type = "tool_use", id = "pending", name = "Read", input = new { file_path = "/workspace/missing" } }
            }, model: "claude-fixture"),
            Record(fixture.Id, "user", "u2", new object[] {
                new { type = "tool_result", tool_use_id = "c1", content = new[] { new { type = "text", text = "/workspace" } }, is_error = false },
                new { type = "text", text = "continue" },
                new { type = "image", source = new { type = "base64", media_type = "image/png", data = "NEVER_COPY_BASE64" } }
            }, root, "plan"),
            Record(fixture.Id, "user", "result-update", new[] { new { type = "tool_result", tool_use_id = "c1", content = "/workspace updated", is_error = true } }),
            Record(fixture.Id, "assistant", "redacted", new[] { new { type = "redacted_thinking", data = "NEVER_COPY_HIDDEN" } }),
            JsonSerializer.Serialize(new { type = "custom-title", sessionId = fixture.Id, customTitle = "Fixture title" })
        });
        var read = ClaudeNativeHistory.Read(fixture.Path, fixture.Id, true, root);
        Require(!read.Truncated && read.Entry.Title == "Fixture title" && read.Entry.PermissionMode == "plan" && read.Entry.Model == "claude-fixture", "Mixed Claude transcript lost metadata or was incorrectly truncated");
        Require(read.Messages.Select(m => m.Id).SequenceEqual(new[] { "u1", "a1", "a1/1", "tool-c1", "tool-pending", "u2/1", "u2/2", "redacted" }), "Claude block order or linked result identity changed");
        Require(read.Messages.Single(m => m.Id == "a1").Kind == "reasoning" && read.Messages.Single(m => m.Id == "redacted").Kind == "reasoning", "Claude thinking blocks were lost");
        var tool = read.Messages.Single(m => m.Id == "tool-c1");
        Require(tool.Status == "failed" && tool.Text.Contains("pwd") && tool.Text.Contains("updated"), "Linked tool result did not merge input/output/error status");
        using (var doc = JsonDocument.Parse(tool.Text)) Require(doc.RootElement.ValueKind == JsonValueKind.Object, "Merged Claude tool JSON is invalid");
        Require(read.Messages.Single(m => m.Id == "tool-pending").Status == "unknown", "Historical call without result falsely claimed completion or live execution");
        var json = JsonSerializer.Serialize(read.Messages);
        Require(!json.Contains("NEVER_COPY_") && read.Messages.Any(m => m.Kind == "reference" && m.Text.Contains("image/png")), "Claude reference or hidden-block handling leaked binary/signature data");
        Require(json == JsonSerializer.Serialize(ClaudeNativeHistory.Read(fixture.Path, fixture.Id, true, root).Messages), "Claude stable IDs changed between reads");
        File.AppendAllText(fixture.Path, Record(fixture.Id, "user", "u1", "question updated") + "\n");
        var updated = ClaudeNativeHistory.Read(fixture.Path, fixture.Id, true, root);
        Require(updated.Messages.Count(m => m.Id == "u1") == 1 && updated.Messages[0].Text == "question updated", "Repeated transcript UUID created duplicate messages or reordered original messages");
    }

    private static void ClaudeLimits(string root)
    {
        var fixture = Transcript(root);
        File.WriteAllLines(fixture.Path, new[] {
            Record(fixture.Id, "user", "u", "first", root, "default"),
            "{broken", "[]",
            Record(Guid.NewGuid().ToString(), "assistant", "foreign", "not in this session"),
            JsonSerializer.Serialize(new { type = "assistant", sessionId = fixture.Id, isSidechain = true, uuid = "side", cwd = "/wrong", message = new { content = "sidechain" } }),
            Record(fixture.Id, "assistant", "long", new string('x', AiHistoryPresentation.MaxText + 100)),
            Record(fixture.Id, "assistant", "too-large", new string('x', 270000)),
            Record(fixture.Id, "user", "orphan", new[] { new { type = "tool_result", tool_use_id = "missing", content = "orphan output", is_error = false } }),
            Record(fixture.Id, "assistant", "last", "retained")
        });
        var read = ClaudeNativeHistory.Read(fixture.Path, fixture.Id, true, root);
        Require(read.Truncated && read.Entry.Directory == root && read.Messages.Last().Id == "last", "Malformed or oversized transcript did not retain later valid records");
        Require(read.Messages.All(m => m.Id is not ("foreign" or "side" or "too-large")) && read.Messages.Single(m => m.Id == "long").Text.Contains("已截断"), "Claude row filtering or visible truncation failed");
        Require(read.Messages.Any(m => m.Id == "tool-missing" && m.Text.Contains("orphan output")), "Orphan tool result was silently discarded");

        File.WriteAllLines(fixture.Path, Enumerable.Range(0, AiHistoryPresentation.MaxMessages + 10)
            .Select(i => Record(fixture.Id, "user", "u" + i, "message" + i, root, "default")));
        read = ClaudeNativeHistory.Read(fixture.Path, fixture.Id, true, root);
        Require(read.Truncated && read.Messages.Length == AiHistoryPresentation.MaxMessages && read.Messages[0].Id == "u10" && read.Messages.Last().Id == "u1033", "Claude retention cap did not preserve newest messages");
    }

    private static void ClaudeWindow(string root)
    {
        var fixture = Transcript(root);
        var prefix = Record(fixture.Id, "user", "u", "original title", root, "default") + "\n";
        var tail = JsonSerializer.Serialize(new { type = "assistant", sessionId = fixture.Id, message = new { content = "tail without uuid" } }) + "\n";
        File.WriteAllText(fixture.Path, prefix + new string('x', ClaudeNativeHistory.WindowBytes * 3) + "\n" + tail, new UTF8Encoding(false));
        var read = ClaudeNativeHistory.Read(fixture.Path, fixture.Id, true, root);
        var message = read.Messages.Single(m => m.Text == "tail without uuid");
        Require(read.Truncated && read.Entry.Title == "original title" && message.Id.StartsWith("native-byte-", StringComparison.Ordinal), "Bounded prefix/tail window lost metadata or stable fallback identity");
        File.AppendAllText(fixture.Path, Record(fixture.Id, "user", "append", "later", root, "plan") + "\n");
        var appended = ClaudeNativeHistory.Read(fixture.Path, fixture.Id, true, root);
        Require(appended.Messages.Single(m => m.Text == "tail without uuid").Id == message.Id && appended.Entry.PermissionMode == "plan", "Tail IDs depended on moving window line numbers or saved permission was lost");
    }

    private static void ClaudeBoundaries(string root)
    {
        var fixture = Transcript(root);
        File.WriteAllText(fixture.Path, Record(fixture.Id, "user", "u", "prompt", root, "default"));
        Require(ClaudeNativeHistory.SafeFile(fixture.Path, root), "Valid Claude transcript was rejected");
        Require(!ClaudeNativeHistory.SafeFile(Path.Combine(root + "-sibling", fixture.Id + ".jsonl"), root), "Prefix-sibling path escaped transcript root");
        Require(ClaudeNativeHistory.PathComparison == (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Claude paths use wrong OS case semantics");
        if (!OperatingSystem.IsWindows()) {
            var differentCase = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root).ToUpperInvariant(), "project", fixture.Id + ".jsonl");
            Require(!ClaudeNativeHistory.SafeFile(differentCase, root), "Unix transcript root matched a different-case path");
            var link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, Path.GetDirectoryName(fixture.Path)!);
            Require(!ClaudeNativeHistory.SafeFile(Path.Combine(link, fixture.Id + ".jsonl"), root), "Transcript traversal followed a directory symlink");
        }
    }

    private static void CapabilityBoundaries()
    {
        var openCode = new AiAgentInstallation("opencode", "OpenCode", "fixture", "", "1", "acp", "available");
        Require(AiAgentSession.SupportsNativeHistory(openCode), "OpenCode native session/list + session/load support was disabled");
        foreach (var (id, protocol) in new[] { ("grok", "acp"), ("zcode", "zcode") })
            Require(!AiAgentSession.SupportsNativeHistory(new(id, id, "fixture", "", "1", protocol, "available")), "Unavailable native history was advertised for " + id);
    }

    private static (string Path, string Id) Transcript(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "project"));
        var id = Guid.NewGuid().ToString("D"); return (Path.Combine(root, "project", id + ".jsonl"), id);
    }
    private static string Record(string sessionId, string type, string uuid, object content, string cwd = "", string permissionMode = "", string model = "") =>
        JsonSerializer.Serialize(new { type, sessionId, uuid, cwd, permissionMode, message = new { content, model } });
    private static object Item(string turnId, object item) => new { turnId, item };
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ErrorAsync(Func<Task<CodexNativeHistory.ReadResult>> action, string code)
    {
        try { await action(); throw new Exception("Expected history error: " + code); }
        catch (AiOperationException error) when (error.Code == code) { }
    }

    private sealed class Script
    {
        private sealed record Expected(string Method, string Cursor, string View, Func<JsonElement> Result);
        private readonly Queue<Expected> expected = new();
        internal void Page(string method, object data, string? next = null, string cursor = "", string view = "") =>
            Raw(method, new { data, nextCursor = next }, cursor, view);
        internal void Raw(string method, object result, string cursor = "", string view = "") =>
            expected.Enqueue(new(method, cursor, view, () => Json(result)));
        internal void Error(string method, string code, string cursor = "", string view = "") =>
            expected.Enqueue(new(method, cursor, view, () => throw new AiOperationException(code)));
        internal Task<JsonElement> Call(string method, object parameters)
        {
            Require(expected.Count > 0, "Unexpected history RPC: " + method);
            var next = expected.Dequeue(); var value = Json(parameters);
            Require(next.Method == method && AiAgentSession.S(value, "cursor") == next.Cursor && AiAgentSession.S(value, "itemsView") == next.View, "History RPC method/cursor/view mismatch: " + method);
            Require(method is "thread/turns/list" or "thread/items/list", "Native history issued a non-bounded or mutating RPC");
            Require(value.GetProperty("limit").GetInt32() is > 0 and <= 64 && AiAgentSession.S(value, "sortDirection") == "desc", "History RPC omitted a limit or changed sort direction");
            Require(!value.TryGetProperty("includeTurns", out _), "Unbounded legacy history was requested");
            return Task.FromResult(next.Result());
        }
        internal void Done() => Require(expected.Count == 0, "History did not consume expected pages/fallbacks");
    }
}
