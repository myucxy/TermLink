// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.Ai;

// Pure event replay through the production receive path. No processes, model
// calls, files, keys, sockets, permissions, or real workspaces are used.
internal static class AiEventParityValidation
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    public static async Task RunAsync()
    {
        Normalization();
        await CodexAsync();
        await ClaudeAsync();
        await ZcodeAsync();
        await AcpAsync("opencode");
        await AcpAsync("grok");
        await RpcErrorsAsync();
        Console.WriteLine("PASS AI event parity: bounded normalized JSON, Codex/Claude/ZCode/ACP replay, null patches, lifecycle, plans, error redaction and RPC error codes (no live models)");
    }
#if AI_EVENT_PARITY_STANDALONE
    public static Task Main() => RunAsync();
#endif

    private static void Normalization()
    {
        var first = AiToolPresentation.Normalize(Json(new { type = "commandExecution", command = "echo safe", cwd = "/work", aggregatedOutput = "a", status = "inProgress" }));
        var final = AiToolPresentation.Normalize(Json(new { type = "commandExecution", aggregatedOutput = (string?)null, exitCode = 7, status = "completed" }), previous: first);
        var value = Parse(final);
        Require(Text(value, "schema") == "termlink.tool.v1" && Text(value, "kind") == "command", "Tool schema/category missing.");
        Require(Text(value, "command") == "echo safe" && Text(value, "output") == "a" && Text(value, "cwd") == "/work", "Final nulls erased prior detail.");
        Require(AiToolPresentation.Describe(final).Status == "failed", "Nonzero exit code not reflected in presentation.");
        Require(AiToolPresentation.Status(Json(new { state = new { status = "completed" } })) == "completed", "Nested provider status missing.");
        Require(AiToolPresentation.Status(Json(new { status = "interrupted" })) == "cancelled", "Cancellation mapped to success.");
        Require(AiToolPresentation.Status(Json(new { status = "completed", success = false })) == "failed", "Dynamic tool failure mapped to success.");
        Require(AiToolPresentation.Describe("invalid JSON").Status == "unknown", "Malformed JSON was treated as success.");
        Require(AiToolPresentation.Describe("{\"tool_name\":\"Bash\",\"input\":{\"command\":\"echo approval\"}}", "claude/tool").Summary == "echo approval", "Approval presentation regressed.");
        Require(AiToolPresentation.Describe("{\"type\":\"fileChange\",\"changes\":[{\"path\":\"src/a.cs\"}]}").Summary == "src/a.cs", "Legacy file summary regressed.");
        var longText = string.Concat(Enumerable.Repeat("\"\\\n😀", 100000));
        var large = AiToolPresentation.Normalize(Json(new { type = "commandExecution", command = "long", aggregatedOutput = longText,
            changes = Enumerable.Range(0, 80).Select(i => new { path = "p" + i, diff = longText[..1000] }).ToArray() }));
        Require(large.Length <= AiToolPresentation.MaxLength && Parse(large).GetProperty("truncated").GetBoolean(), "Normalized output exceeded bound or lost truncation.");
        foreach (var limit in new[] { 64, 100, 1024, 16000 }) {
            var preview = AiToolPresentation.Preview(large, limit);
            Require(preview.Length <= limit && Text(Parse(preview), "schema") == AiToolPresentation.Schema, "Preview cut serialized JSON or exceeded limit.");
        }
        var secret = AiToolPresentation.Normalize(Json(new { name = "mcp__test", input = new { api_key = "do-not-leak-api", headers = new { Authorization = "Bearer do-not-leak-header" } },
            error = new { message = "private error payload do-not-leak-error", additionalDetails = "do-not-leak-details" } }));
        Require(!secret.Contains("do-not-leak", StringComparison.Ordinal), "Structured provider secrets escaped redaction.");
        var authCommand = AiToolPresentation.Normalize(Json(new { type = "commandExecution", command = "curl -H 'Authorization: Bearer do-not-leak-header' https://example.invalid" }));
        Require(!authCommand.Contains("do-not-leak", StringComparison.Ordinal), "Bearer header was only partially redacted.");
        Require(AiToolPresentation.Status(Json(new { type = "tool_progress", elapsed_time_seconds = "malformed" })) == "unknown", "Malformed progress gained status.");
        Parse(AiToolPresentation.Normalize(Json(new { elapsed_time_seconds = "malformed" })));
    }

    private static async Task CodexAsync()
    {
        using var s = Session("codex");
        await Notify(s, "turn/started", new { turn = new { id = "turn-1" } });
        await Item(s, "started", new { id = "cmd", type = "commandExecution", command = "echo safe", cwd = "/work", status = "inProgress", aggregatedOutput = (string?)null });
        await Notify(s, "item/commandExecution/outputDelta", new { itemId = "cmd", delta = "first\n" });
        await Notify(s, "item/commandExecution/outputDelta", new { itemId = "cmd", delta = "second\n" });
        Require(Text(Detail(s, "cmd"), "output") == "first\nsecond\n" && Message(s, "cmd").Status == "running", "Codex output delta did not append.");
        await Item(s, "completed", new { id = "cmd", type = "commandExecution", status = "completed", aggregatedOutput = (string?)null, exitCode = 0, durationMs = 22 });
        Require(Text(Detail(s, "cmd"), "output") == "first\nsecond\n" && Text(Detail(s, "cmd"), "command") == "echo safe", "Codex completion erased running detail.");
        await Notify(s, "item/commandExecution/outputDelta", new { itemId = "cmd", delta = "late" });
        Require(Message(s, "cmd").Status == "completed", "Late delta reopened finished Codex command.");
        await Item(s, "completed", new { id = "cmd", type = "commandExecution", status = "completed", aggregatedOutput = "authoritative", exitCode = 2 });
        Require(Message(s, "cmd").Status == "failed" && Text(Detail(s, "cmd"), "output") == "authoritative", "Authoritative output/exit failure lost.");
        await Item(s, "started", new { id = "mcp", type = "mcpToolCall", server = "local", tool = "list", arguments = new { query = "safe" }, status = "inProgress" });
        await Notify(s, "item/mcpToolCall/progress", new { itemId = "mcp", message = "half done" });
        Require(Text(Detail(s, "mcp"), "progress") == "half done", "MCP progress not surfaced.");
        await Item(s, "completed", new { id = "mcp", type = "mcpToolCall", status = "completed", result = new { content = new[] { new { type = "text", text = "found" } } } });
        Require(Text(Detail(s, "mcp"), "kind") == "mcp" && Text(Detail(s, "mcp"), "output") == "found", "MCP result missing.");
        await Item(s, "completed", new { id = "child", type = "collabToolCall", tool = "spawnAgent", newThreadId = "child-id", prompt = "review", status = "completed" });
        Require(Text(Detail(s, "child"), "kind") == "agent" && Text(Detail(s, "child").GetProperty("children")[0], "id") == "child-id", "Codex child metadata missing.");
        await Item(s, "completed", new { id = "search", type = "webSearch", query = "query", action = new { type = "openPage", url = "https://example.invalid/test" } });
        Require(Text(Detail(s, "search"), "kind") == "search" && Detail(s, "search").GetProperty("urls").GetArrayLength() == 1, "Codex search not represented.");
        await Item(s, "completed", new { id = "image", type = "imageView", path = "/work/picture.png" });
        Require(Text(Detail(s, "image"), "kind") == "image", "Codex image view missing.");
        await Item(s, "completed", new { id = "dynamic", type = "dynamicToolCall", tool = "custom", success = false, contentItems = new[] { new { type = "text", text = "failed" } } });
        Require(Message(s, "dynamic").Status == "failed", "Dynamic tool explicit failure lost.");
        await Notify(s, "item/plan/delta", new { itemId = "plan", delta = "draft" });
        await Item(s, "completed", new { id = "plan", type = "plan", text = "final plan" });
        Require(Message(s, "plan").Kind == "plan" && Message(s, "plan").Text == "final plan" && Message(s, "plan").Status == "completed", "Plan item not authoritative.");
        await Notify(s, "turn/plan/updated", new { plan = new[] { new { step = "verify", status = "in_progress" } } });
        Require(Text(((JsonElement)Get(s, "plan"))[0], "status") == "in_progress", "Plan steps missing.");
        await Notify(s, "error", new { error = new { message = "do-not-leak", additionalDetails = "do-not-leak-more", codexErrorInfo = "usageLimitExceeded" }, willRetry = true });
        var errors = Messages(s).Where(m => m.Kind == "error").ToArray();
        Require(errors.Length == 1 && !errors[0].Text.Contains("do-not-leak") && Text(Parse(errors[0].Text), "code") == "AGENT_RATE_LIMITED", "Codex error was not sanitized/classified.");
        await Item(s, "started", new { id = "large-output", type = "commandExecution", command = "large", status = "inProgress" });
        await Notify(s, "item/commandExecution/outputDelta", new { itemId = "large-output", delta = new string('x', 200000) });
        await Notify(s, "item/commandExecution/outputDelta", new { itemId = "large-output", delta = "tail" });
        Require(Text(Detail(s, "large-output"), "output").Length <= AiToolPresentation.OutputLimit && Detail(s, "large-output").GetProperty("truncated").GetBoolean(), "Streaming output exceeded bound or lost truncation.");
        await Item(s, "completed", new { id = "large-output", type = "commandExecution", status = "completed" });
        Require(Detail(s, "large-output").GetProperty("truncated").GetBoolean(), "Completion hid earlier output truncation.");
        await Item(s, "started", new { id = "cancel", type = "commandExecution", command = "sleep", status = "inProgress" });
        await Notify(s, "turn/completed", new { turn = new { status = "interrupted" } });
        Require(Message(s, "cancel").Status == "cancelled", "Interrupted turn left command running/complete.");
    }

    private static async Task ClaudeAsync()
    {
        using var s = Session("claude");
        await Stream(s, new { type = "message_start", message = new { id = "m1" } });
        await Stream(s, new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "bash", name = "Bash", input = new { } } });
        await Stream(s, new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "{\"command\":\"echo " } });
        Require(Text(Detail(s, "tool-bash"), "progress").Length > 0, "Claude partial parameter progress missing.");
        await Stream(s, new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "safe\"}" } });
        await Stream(s, new { type = "content_block_stop", index = 0 });
        Require(Text(Detail(s, "tool-bash"), "command") == "echo safe" && Message(s, "tool-bash").Status == "running", "Input stop completed Claude execution.");
        await Receive(s, new { type = "assistant", message = new { id = "m1", content = new[] { new { type = "tool_use", id = "bash", name = "Bash", input = new { command = "echo safe" } } } } });
        await Receive(s, new { type = "tool_progress", tool_use_id = "bash", tool_name = "Bash", elapsed_time_seconds = 1.5 });
        Require(Detail(s, "tool-bash").GetProperty("durationMs").GetDouble() == 1500, "Claude tool elapsed time missing.");
        await Receive(s, new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "bash", is_error = true,
            content = new[] { new { type = "text", text = "exit failed" } } } } } });
        Require(Text(Detail(s, "tool-bash"), "output") == "exit failed" && Text(Detail(s, "tool-bash"), "command") == "echo safe" && Message(s, "tool-bash").Status == "failed", "Claude tool result invalid or detached.");
        Require(Messages(s).Count(m => m.Id == "tool-bash") == 1, "Claude stream and final duplicated tool.");
        await Receive(s, new { type = "tool_progress", tool_use_id = "bash", elapsed_time_seconds = 2 });
        Require(Message(s, "tool-bash").Status == "failed", "Claude late progress reopened failure.");
        await Stream(s, new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "parent " } });
        await Stream(s, new { type = "message_start", message = new { id = "child-message" } }, "parent-tool");
        await Stream(s, new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "child" } }, "parent-tool");
        await Stream(s, new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "text" } });
        await Stream(s, new { type = "content_block_stop", index = 1 });
        await Receive(s, new { type = "assistant", uuid = "final-parent", message = new { id = "m1", content = new[] { new { type = "text", text = "parent text" } } } });
        Require(Message(s, "m1/1").Text == "parent text" && Message(s, "child-message/0").Text == "child", "Concurrent Claude parent/child streams crossed.");
        await Receive(s, new { type = "system", subtype = "task_started", task_id = "task1", tool_use_id = "agent", description = "review" });
        await Receive(s, new { type = "system", subtype = "task_notification", task_id = "task1", tool_use_id = "agent", status = "completed", summary = "done" });
        Require(Text(Detail(s, "tool-agent").GetProperty("children")[0], "status") == "completed" && Text(Detail(s, "tool-agent").GetProperty("children")[0], "prompt") == "review", "Claude child completion/prompt not preserved.");
        await Receive(s, new { type = "system", subtype = "task_progress", task_id = "task1", description = "late" });
        Require(Text(Detail(s, "tool-agent").GetProperty("children")[0], "status") == "completed", "Late Claude task progress reopened child or lost its tool owner.");
        await Receive(s, new { type = "system", subtype = "task_started", task_id = "background", description = "job" });
        await Receive(s, new { type = "system", subtype = "task_updated", task_id = "background", patch = new { status = "stopped" } });
        Require(Message(s, "claude/task/background").Status == "cancelled", "Claude stopped task patch not applied.");
        await Stream(s, new { type = "content_block_start", index = 2, content_block = new { type = "tool_use", id = "huge-input", name = "Bash", input = new { } } });
        await Stream(s, new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = new string('x', 40000) } });
        Require(Detail(s, "tool-huge-input").GetProperty("truncated").GetBoolean(), "Claude oversized parameter stream not bounded.");
        await Receive(s, new { type = "result", is_error = true, errors = new[] { "Bearer do-not-leak" } });
        Require(Messages(s).Any(m => m.Kind == "error") && Messages(s).All(m => !m.Text.Contains("do-not-leak")), "Claude error payload leaked.");
    }

    private static async Task ZcodeAsync()
    {
        using var s = Session("zcode");
        await Zcode(s, "turn.started", new { });
        await Zcode(s, "tool.updated", new { toolCallId = "ztool", toolName = "Bash", input = new { command = "echo safe" }, status = "running" });
        await Zcode(s, "tool.updated", new { toolCallId = "ztool", status = "completed" });
        Require(Text(Detail(s, "tool-ztool"), "command") == "echo safe" && Message(s, "tool-ztool").Status == "completed", "ZCode sparse update dropped input/status.");
        await Zcode(s, "model.streaming", new { kind = "text_delta", assistantMessageId = "answer", delta = "hello" });
        await Zcode(s, "message.upserted", new { message = new { id = "answer", role = "assistant", content = "hello world" } });
        Require(Message(s, "answer").Text == "hello world", "ZCode authoritative message lost.");
        await Zcode(s, "unknown.speculative.tool_event", new { toolCallId = "not-supported", delta = "must not appear" });
        Require(Messages(s).All(m => !m.Text.Contains("must not appear")), "Invented ZCode event accepted.");
        await Zcode(s, "turn.failed", new { error = new { code = "CONFIGURATION_ERROR", message = "do-not-leak" } });
        Require((string)Get(s, "error") == "AGENT_MODEL_REQUIRED" && Messages(s).All(m => !m.Text.Contains("do-not-leak")), "ZCode configuration error not safely mapped.");
    }

    private static async Task AcpAsync(string brand)
    {
        using var s = Session("acp", brand);
        Set(s, "threadId", "acp-session"); Set(s, "acpPromptId", "prompt"); Set(s, "acpTurnKey", "turn");
        var id = "acp/tool/turn/acp-tool";
        await Acp(s, new { sessionUpdate = "tool_call", toolCallId = "acp-tool", title = "Run tests", name = "shell", kind = "execute", status = "pending",
            rawInput = new { command = "run tests", cwd = "/work" }, content = new[] { new { type = "content", content = new { type = "text", text = "initial" } } },
            locations = new[] { new { path = "/work/a.cs" } } });
        await Acp(s, new { sessionUpdate = "tool_call_update", toolCallId = "acp-tool", rawInput = (object?)null, rawOutput = (object?)null, content = (object?)null, locations = (object?)null, name = (string?)null, status = "in_progress" });
        Require(Text(Detail(s, id), "command") == "run tests" && Text(Detail(s, id), "output") == "initial" && Detail(s, id).GetProperty("files").GetArrayLength() == 1, "ACP null patch cleared detail.");
        await Acp(s, new { sessionUpdate = "tool_call_update", toolCallId = "acp-tool", content = new[] { new { type = "content", content = new { type = "text", text = "replacement" } } } });
        Require(Text(Detail(s, id), "output") == "replacement", "ACP content snapshot appended instead of replaced.");
        await Acp(s, new { sessionUpdate = "tool_call_update", toolCallId = "acp-tool", content = Array.Empty<object>(), locations = Array.Empty<object>() });
        Require(Text(Detail(s, id), "output") == "" && Detail(s, id).GetProperty("files").GetArrayLength() == 0, "ACP empty arrays did not clear values.");
        await Acp(s, new { sessionUpdate = "tool_call_update", toolCallId = "acp-tool", status = "failed", rawOutput = "failed safely" });
        Require(Message(s, id).Status == "failed" && Text(Detail(s, id), "name") == "shell", "ACP final status/name missing.");
        await Acp(s, new { sessionUpdate = "tool_call", toolCallId = "edit", kind = "edit", status = "completed", content = new[] { new { type = "diff", path = "/work/edit.cs", oldText = "old", newText = "new" } } });
        Require(Detail(s, "acp/tool/turn/edit").GetProperty("files")[0].GetProperty("diff").GetString()!.Contains("+++ new"), "ACP file diff missing.");
        var before = (long)Get(s, "revision");
        await Acp(s, new { sessionUpdate = "plan", entries = new[] { new { content = "verify", status = "in_progress", priority = "high" } } });
        Require((long)Get(s, "revision") > before && Text(((JsonElement)Get(s, "plan"))[0], "step") == "verify", "ACP plan did not publish a revision.");
        await Acp(s, new { sessionUpdate = "tool_call_update", toolCallId = "acp-tool", rawOutput = "foreign" }, "other-session");
        Require(Text(Detail(s, id), "output") == "failed safely", "ACP foreign session contaminated tools.");
    }

    private static async Task RpcErrorsAsync()
    {
        foreach (var protocol in new[] { "codex", "zcode", "acp" }) {
            using var s = Session(protocol);
            foreach (var pair in new[] { (-32601, "AGENT_METHOD_NOT_FOUND"), (-32602, "AGENT_INVALID_PARAMS"), (-32001, "AGENT_REQUEST_REJECTED") }) {
                var calls = (ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>)Get(s, "calls");
                var completion = new TaskCompletionSource<JsonElement>(); calls["123"] = completion;
                await Receive(s, new { jsonrpc = "2.0", id = "123", error = new { code = pair.Item1, message = "Bearer do-not-leak-rpc", data = "private data" } });
                try { await completion.Task; throw new InvalidOperationException("RPC failure was accepted."); }
                catch (AiOperationException e) { Require(e.Code == pair.Item2 && !e.Message.Contains("do-not-leak"), "RPC error code collapsed or leaked upstream message."); }
                calls.Clear();
            }
        }
    }

    private static AiAgentSession Session(string protocol, string brand = "")
    {
        var s = new AiAgentSession(new AiAgentInstallation(brand.Length > 0 ? brand : protocol, protocol, "", "", "fixture", protocol, "ready"), "/unused-replay-workspace", Guid.Empty);
        Set(s, "state", "running"); return s;
    }
    private static Task Item(AiAgentSession s, string state, object item) => Notify(s, "item/" + state, new { item });
    private static Task Notify(AiAgentSession s, string method, object parameters) => Receive(s, new { method, @params = parameters });
    private static Task Stream(AiAgentSession s, object e, string parent = "") => Receive(s, new { type = "stream_event", @event = e, parent_tool_use_id = parent });
    private static Task Zcode(AiAgentSession s, string type, object payload) => Notify(s, "session/event", new { type, payload });
    private static Task Acp(AiAgentSession s, object update, string session = "acp-session") => Receive(s, new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update } });
    private static async Task Receive(AiAgentSession s, object value) => await (Task)typeof(AiAgentSession).GetMethod("ReceiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s, new object[] { Json(value) })!;
    private static List<AiAgentSession.Message> Messages(AiAgentSession s) => (List<AiAgentSession.Message>)Get(s, "messages");
    private static AiAgentSession.Message Message(AiAgentSession s, string id) => Messages(s).Single(m => m.Id == id);
    private static JsonElement Detail(AiAgentSession s, string id) => Parse(Message(s, id).Text);
    private static object Get(AiAgentSession s, string field) => typeof(AiAgentSession).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!;
    private static void Set(AiAgentSession s, string field, object value) => typeof(AiAgentSession).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(s, value);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Parse(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
