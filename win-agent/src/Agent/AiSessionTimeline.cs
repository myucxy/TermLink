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
    private readonly string streamId = Guid.NewGuid().ToString("D");
    private readonly Dictionary<string, int> messageIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> messageRevisions = new(StringComparer.Ordinal);
    private long revision, historyFloor, historyBase, messageCharacters, turnStartedAtMs, lastActivityAtMs;
    private string phase = "idle", claudeStreamingMessage = "";
    private readonly Dictionary<int, string> claudeStreamBlocks = new();
    private readonly Dictionary<string, int> claudeFinalBlocks = new(StringComparer.Ordinal);
    private int claudeStoppedBlock = -1;
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
        var e = P(value, "event"); var type = S(e, "type");
        if (type == "message_start") {
            claudeStreamingMessage = S(P(e, "message"), "id");
            claudeStreamBlocks.Clear(); claudeFinalBlocks.Clear(); claudeStoppedBlock = -1;
            return;
        }
        if (claudeStreamingMessage.Length == 0) return;
        var index = P(e, "index").ValueKind == JsonValueKind.Number ? P(e, "index").GetInt32() : -1;
        if (index is < 0 or > 1024) return;
        var key = claudeStreamingMessage + "/" + index;
        if (type == "content_block_start") {
            var block = P(e, "content_block");
            claudeStoppedBlock = -1;
            claudeStreamBlocks[index] = S(block, "type");
            if (S(block, "type") == "text") Put(key, "assistant", S(block, "text"));
            else if (S(block, "type") == "thinking") Put(key, "assistant", S(block, "thinking"), kind: "reasoning", status: "running");
        } else if (type == "content_block_delta") {
            var delta = P(e, "delta");
            // Some compatible providers omit block_start; retain the stream index anyway.
            if (S(delta, "type") == "text_delta") claudeStreamBlocks[index] = "text";
            else if (S(delta, "type") == "thinking_delta") claudeStreamBlocks[index] = "thinking";
            if (S(delta, "type") == "text_delta") Put(key, "assistant", S(delta, "text"), true);
            else if (S(delta, "type") == "thinking_delta") Put(key, "assistant", S(delta, "thinking"), true, "reasoning", "running");
            // Signatures/redacted blocks and private protocol metadata are not thoughts.
        } else if (type == "content_block_stop") {
            claudeStoppedBlock = index;
            lock (sync) { EnsureMessageIndex(); if (messageIndex.TryGetValue(key, out var at)) ReplaceMessage(at, messages[at] with { Status = "completed" }); }
        }
    }

    private int ClaudeFinalBlockIndex(JsonElement value, JsonElement message, JsonElement part, int index, int count)
    {
        // Claude stream-json emits an assistant envelope for EACH completed block.
        // Its singleton content[0] still belongs to the original stream index (e.g.
        // thinking /0, text /1). Full multi-block snapshots retain their own indices.
        if (count != 1 || S(message, "id") != claudeStreamingMessage) return index;
        var uuid = S(value, "uuid");
        if (uuid.Length > 0 && claudeFinalBlocks.TryGetValue(uuid, out var known)) return known;
        var type = S(part, "type");
        var resolved = claudeStreamBlocks.GetValueOrDefault(claudeStoppedBlock) == type ? claudeStoppedBlock
            : claudeStreamBlocks.Where(b => b.Value == type).Select(b => b.Key).DefaultIfEmpty(index).Max();
        if (uuid.Length > 0 && claudeFinalBlocks.Count < 1025) claudeFinalBlocks[uuid] = resolved;
        return resolved;
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
                step = Clip(S(step, "step"), 1000), status = S(step, "status") is "completed" or "in_progress" ? S(step, "status") : "pending"
            }).ToArray());
            PublishChange();
        }
    }
}
