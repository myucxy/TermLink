using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent;

internal sealed partial class AiAgentSession
{
    private string streamId = Guid.NewGuid().ToString("D");
    private readonly Dictionary<string, int> messageIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> messageRevisions = new(StringComparer.Ordinal);
    private long revision, historyFloor, historyBase, messageCharacters, turnStartedAtMs, lastActivityAtMs;
    private string phase = "idle";
    private readonly Dictionary<string, ClaudeStreamState> claudeStreams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> claudeTaskOwners = new(StringComparer.Ordinal);
    private sealed class ClaudeStreamState
    {
        internal string MessageId = "";
        internal int StoppedBlock = -1;
        internal readonly Dictionary<int, string> Blocks = new();
        internal readonly Dictionary<int, ClaudeToolInput> Tools = new();
        internal readonly Dictionary<string, int> Finals = new(StringComparer.Ordinal);
    }
    private sealed class ClaudeToolInput
    {
        internal string Id = "", Input = "";
        internal bool Truncated;
    }
    private JsonElement? plan;
    private string diffVersion = "";
    private bool diffTruncated;

    private void UpdateDiff(string value) {
        lock (sync) {
            diffTruncated = value.Length > 262144;
            var length = Math.Min(value.Length, 262144);
            if (diffTruncated && char.IsHighSurrogate(value[length - 1])) length--;
            diff = value[..length]; diffVersion = Guid.NewGuid().ToString("N"); PublishChange();
        }
    }

    internal object DiffPage(long offset, string version) {
        lock (sync) {
            if (offset < 0 || offset > diff.Length) throw new AiOperationException("INVALID_OFFSET");
            if (!string.IsNullOrEmpty(version) && version != diffVersion) throw new AiOperationException("DIFF_CHANGED");
            var length = Math.Min(32768, diff.Length - (int)offset);
            if (length > 0 && offset + length < diff.Length && char.IsHighSurrogate(diff[(int)offset + length - 1])) length--;
            return new { text = diff.Substring((int)offset, length), offset, nextOffset = offset + length, size = diff.Length, version = diffVersion, truncated = diffTruncated };
        }
    }
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long PublishChange()
    {
        lock (sync) {
            var pendingSignal = signal;
            signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            revision++;
            pendingSignal.TrySetResult();
            return revision;
        }
    }

    // Bounded event wait over the compatible encrypted request/reply route. This is
    // long polling, not a v2 pushed subscription; commands have independent slots.
    internal async Task<object> WatchAsync(Guid viewer, string expectedStream, string cursor, CancellationToken token)
    {
        Task changed;
        lock (sync) {
            EnsureMessageIndex();
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var after) || after < 0) throw new AiOperationException("INVALID_CURSOR");
            if (expectedStream != streamId || after != revision) return Changes(viewer, expectedStream, cursor, false);
            changed = signal.Task;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try { await changed.WaitAsync(TimeSpan.FromSeconds(20), linked.Token).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Changes(viewer, expectedStream, cursor, false); }
        // Batch token bursts without holding the provider or command locks.
        try { await Task.Delay(100, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return Changes(viewer, expectedStream, cursor, false);
    }

    internal void RenewControl(Guid viewer, long expectedVersion)
    {
        lock (sync) {
            ExpireLease();
            if (!IsAlive) throw new AiOperationException("SESSION_ENDED");
            if (owner != viewer || controlVersion != expectedVersion) throw new AiOperationException("CONTROL_CHANGED");
            leaseExpires = DateTime.UtcNow.AddMinutes(2);
        }
    }

    private void EnsureMessageIndex()
    {
        if (messageIndex.Count == messages.Count) return;
        messageIndex.Clear(); messageCharacters = 0;
        for (var i = 0; i < messages.Count; i++) {
            messageIndex[messages[i].Id] = i;
            messageCharacters += messages[i].Text.Length;
            if (!messageRevisions.ContainsKey(messages[i].Id)) messageRevisions[messages[i].Id] = ++revision;
        }
    }

    private void ReplaceMessage(int index, Message next)
    {
        var previous = messages[index];
        if (previous == next) return;
        messageCharacters += next.Text.Length - previous.Text.Length;
        messages[index] = next;
        messageRevisions[next.Id] = PublishChange();
        lastActivityAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private void BeginTurn()
    {
        InvalidateHistoryActions();
        lock (sync) {
            if (state != "running" || turnStartedAtMs == 0) turnStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            state = "running"; phase = "waiting";
            lastActivityAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PublishChange();
        }
    }

    // Cursor is a decimal string on the JS boundary, never a floating-point JSON number.
    internal object Changes(Guid viewer, string expectedStream, string cursor, bool renewControl = true)
    {
        lock (sync) {
            EnsureMessageIndex();
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var after) || after < 0)
                throw new AiOperationException("INVALID_CURSOR");
            var reset = expectedStream != streamId || after > revision || after < historyFloor;
            var selected = reset ? RecentMessages() : messages.Where(m => messageRevisions.GetValueOrDefault(m.Id) > after).Take(64).Select(PageMessage).ToArray();
            // A bounded page must not acknowledge later changes it did not include.
            if (!reset && (selected.Sum(m => m.Text.Length) > 32000 || messages.Count(m => messageRevisions.GetValueOrDefault(m.Id) > after) > 64)) {
                reset = true; selected = RecentMessages();
            }
            return Snapshot(viewer, true, selected, reset, renewControl);
        }
    }

    internal object Search(string query, long offset)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || offset < 0) throw new AiOperationException("INVALID_SEARCH");
        lock (sync) {
            var hits = messages.Where(m => m.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).Skip((int)Math.Min(offset, int.MaxValue)).Take(51).ToArray();
            return new { items = hits.Take(50).Select(m => {
                var start = Math.Max(0, m.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) - 60);
                return new { m.Id, m.Role, m.Kind, text = Clip(m.Text[start..], 200) };
            }).ToArray(), nextOffset = hits.Length > 50 ? offset + 50 : -1, truncated };
        }
    }

    private void ReceiveClaudeStream(JsonElement value)
    {
        lock (sync) {
            var e = P(value, "event"); var type = S(e, "type");
            var parent = S(value, "parent_tool_use_id");
            if (type == "error") { RecordAgentError(P(e, "error")); return; }
            if (type == "message_start") {
                var id = S(P(e, "message"), "id"); if (id.Length == 0) return;
                if (!claudeStreams.ContainsKey(parent) && claudeStreams.Count >= 64) return;
                claudeStreams[parent] = new ClaudeStreamState { MessageId = id };
                return;
            }
            if (!claudeStreams.TryGetValue(parent, out var stream)) return;
            var index = P(e, "index").ValueKind == JsonValueKind.Number && P(e, "index").TryGetInt32(out var number) ? number : -1;
            if (index is < 0 or > 1024) return;
            var key = stream.MessageId + "/" + index;
            if (type == "content_block_start") {
                var block = P(e, "content_block"); var blockType = S(block, "type");
                stream.StoppedBlock = -1; stream.Blocks[index] = blockType;
                if (blockType == "text") Put(key, "assistant", S(block, "text"));
                else if (blockType == "thinking") Put(key, "assistant", S(block, "thinking"), kind: "reasoning", status: "running");
                else if (blockType == "tool_use" && S(block, "id").Length > 0) {
                    stream.Tools[index] = new ClaudeToolInput { Id = S(block, "id") };
                    PutTool("tool-" + S(block, "id"), block, "tool_use", "running");
                }
            } else if (type == "content_block_delta") {
                var delta = P(e, "delta"); var deltaType = S(delta, "type");
                if (deltaType == "text_delta") { stream.Blocks[index] = "text"; Put(key, "assistant", S(delta, "text"), true); }
                else if (deltaType == "thinking_delta") { stream.Blocks[index] = "thinking"; Put(key, "assistant", S(delta, "thinking"), true, "reasoning", "running"); }
                else if (deltaType == "input_json_delta" && stream.Tools.TryGetValue(index, out var tool)) {
                    var addition = S(delta, "partial_json");
                    var remaining = Math.Max(0, Math.Min(32768 - tool.Input.Length,
                        4 * 1024 * 1024 - claudeStreams.Values.Sum(s => s.Tools.Values.Sum(t => t.Input.Length))));
                    if (addition.Length > remaining) tool.Truncated = true;
                    tool.Input += AiToolPresentation.Prefix(addition, remaining);
                    PublishClaudeInput(tool);
                }
                // Signatures and redacted thinking are never exposed as thoughts.
            } else if (type == "content_block_stop") {
                stream.StoppedBlock = index;
                if (stream.Tools.TryGetValue(index, out var tool)) {
                    PublishClaudeInput(tool);
                    // End of tool INPUT is not end of tool EXECUTION.
                } else {
                    EnsureMessageIndex();
                    if (messageIndex.TryGetValue(key, out var at)) ReplaceMessage(at, messages[at] with { Status = "completed" });
                }
            }
        }
    }

    private void PublishClaudeInput(ClaudeToolInput tool)
    {
        JsonElement input = default;
        if (!tool.Truncated && tool.Input.Length > 0) {
            try { using var document = JsonDocument.Parse(tool.Input); input = document.RootElement.Clone(); }
            catch (JsonException) { /* A partial JSON fragment is expected until input finishes. */ }
        }
        // Avoid publishing half a secret-bearing JSON string before it can be
        // inspected structurally. Progress still changes with every input delta.
        if (input.ValueKind != JsonValueKind.Undefined)
            PutTool("tool-" + tool.Id, JsonSerializer.SerializeToElement(new { input }), "tool_use", "running", progress: "");
        else PutTool("tool-" + tool.Id, JsonSerializer.SerializeToElement(new {
            schema = AiToolPresentation.Schema, truncated = tool.Truncated,
        }), "tool_use", "running", progress: tool.Truncated ? "工具参数超过显示限制" : "正在接收工具参数（" + tool.Input.Length + " 字符）");
    }

    private int ClaudeFinalBlockIndex(JsonElement value, JsonElement message, JsonElement part, int index, int count)
    {
        // Singleton assistant envelopes still refer to the original stream index.
        if (count != 1 || !claudeStreams.TryGetValue(S(value, "parent_tool_use_id"), out var stream) || S(message, "id") != stream.MessageId) return index;
        var uuid = S(value, "uuid");
        if (uuid.Length > 0 && stream.Finals.TryGetValue(uuid, out var known)) return known;
        var type = S(part, "type");
        var resolved = stream.Blocks.GetValueOrDefault(stream.StoppedBlock) == type ? stream.StoppedBlock
            : stream.Blocks.Where(b => b.Value == type).Select(b => b.Key).DefaultIfEmpty(index).Max();
        if (uuid.Length > 0 && stream.Finals.Count < 1025) stream.Finals[uuid] = resolved;
        return resolved;
    }

    private void ReceiveClaudeTask(JsonElement value)
    {
        var taskId = S(value, "task_id"); if (taskId.Length is 0 or > 512) return;
        var toolId = S(value, "tool_use_id");
        lock (sync) {
            var key = toolId.Length > 0 ? "tool-" + toolId : claudeTaskOwners.GetValueOrDefault(taskId, "claude/task/" + taskId);
            if (!claudeTaskOwners.ContainsKey(taskId) && claudeTaskOwners.Count >= 256) claudeTaskOwners.Remove(claudeTaskOwners.Keys.First());
            claudeTaskOwners[taskId] = key;
            EnsureMessageIndex();
            var children = new List<JsonElement>(); JsonElement prior = default;
            if (messageIndex.TryGetValue(key, out var index)) {
                using var document = JsonDocument.Parse(messages[index].Text);
                var old = P(document.RootElement, "children");
                if (old.ValueKind == JsonValueKind.Array) {
                    children.AddRange(old.EnumerateArray().Where(c => S(c, "id") != taskId).Take(63).Select(c => c.Clone()));
                    var found = old.EnumerateArray().FirstOrDefault(c => S(c, "id") == taskId);
                    if (found.ValueKind != JsonValueKind.Undefined) prior = found.Clone();
                }
            }
            var update = S(value, "subtype") == "task_updated" ? P(value, "patch") : value;
            var oldStatus = S(prior, "status");
            var status = AiToolPresentation.Status(update, oldStatus.Length > 0 ? oldStatus : S(value, "subtype") == "task_notification" ? "unknown" : "running");
            if (oldStatus is "completed" or "failed" or "cancelled" && status is "running" or "unknown") status = oldStatus;
            var prompt = S(value, "description"); if (prompt.Length == 0) prompt = S(prior, "prompt");
            children.Add(JsonSerializer.SerializeToElement(new { id = taskId, status, prompt }));
            var patch = new Dictionary<string, object> { ["schema"] = AiToolPresentation.Schema, ["kind"] = "agent", ["children"] = children };
            var progress = S(value, "summary").Length > 0 ? S(value, "summary") : S(value, "description");
            if (progress.Length > 0) patch["progress"] = progress;
            PutTool(key, JsonSerializer.SerializeToElement(patch), status: key.StartsWith("claude/task/", StringComparison.Ordinal) ? status : "");
        }
    }

    private void CodexReasoning(JsonElement item)
    {
        var summaries = P(item, "summary");
        if (summaries.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var summary in summaries.EnumerateArray().Take(64))
            Put(S(item, "id") + "/summary/" + index++, "assistant", S(summary, "text"), kind: "reasoning", status: "completed");
    }

    private void ReadPlan(JsonElement value)
    {
        var steps = P(value, "plan");
        if (steps.ValueKind != JsonValueKind.Array) return;
        lock (sync) {
            plan = JsonSerializer.SerializeToElement(steps.EnumerateArray().Take(64).Select(step => new {
                step = Clip(S(step, "step").Length > 0 ? S(step, "step") : S(step, "content"), 1000),
                status = S(step, "status") is "completed" or "in_progress" ? S(step, "status") : "pending"
            }).ToArray());
            PublishChange();
        }
    }
}
