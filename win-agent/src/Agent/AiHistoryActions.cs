// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent;

internal sealed partial class AiAgentSession
{
    private readonly AiHistoryActionProtocol historyActionProtocol = new();
    private bool historyMutationInFlight, nativeHistoryNeedsReload;

    private void ObserveNativeRevert()
    {
        lock (sync) {
            InvalidateHistoryActions();
            ResetHistoryAfterRevert();
            nativeHistoryNeedsReload = true; truncated = true;
            settingsNotice = "电脑原生历史已变化，需要恢复会话重新读取；工作区文件未恢复。";
            SaveArchive();
            if (!historyMutationInFlight) Fail("HISTORY_CHANGED");
        }
    }

    private void RetireUncertainHistory()
    {
        lock (sync) {
            ResetHistoryAfterRevert(); nativeHistoryNeedsReload = true; truncated = true;
            settingsNotice = "历史操作结果待确认。恢复会话后重新读取电脑权威历史，不会重发回退操作。";
            SaveArchive();
            Fail("HISTORY_OUTCOME_UNKNOWN");
        }
    }

    internal async Task<object> HistoryActionsAsync()
    {
        string currentThread;
        lock (sync) currentThread = disposed ? "" : threadId;
        var result = await historyActionProtocol.ReadAsync(agent.Protocol, currentThread, RpcAsync, lifetime.Token).ConfigureAwait(false);
        lock (sync) {
            if (disposed || currentThread != threadId) {
                historyActionProtocol.Invalidate();
                return AiHistoryActionProtocol.Unavailable;
            }
            return result;
        }
    }

    // Called by the parent when a new turn starts, the native thread changes, or
    // another connection reports thread/reverted. No RPC or control-lease renewal.
    internal void InvalidateHistoryActions() => historyActionProtocol.Invalidate();

    internal void ValidateHistoryTurn(string id)
    {
        lock (sync) {
            if (disposed || agent.Protocol != "codex") {
                historyActionProtocol.Invalidate();
                throw new AiOperationException("HISTORY_ACTION_UNSUPPORTED");
            }
            historyActionProtocol.ValidateTurn(threadId, id);
        }
    }

    // The caller already holds actions and has checked control, idle and pending
    // approvals. Do not acquire actions here: doing so would deadlock ApplyAsync.
    internal async Task ApplyHistoryActionAsync(string action, string turnId)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(AiHistoryActionProtocol.MaximumActionSeconds));
        lock (sync) historyMutationInFlight = action == "revert";
        try { await ApplyHistoryActionCoreAsync(action, turnId, deadline.Token).ConfigureAwait(false); }
        catch (AiOperationException failure) when (action == "revert" && failure.Code is not ("HISTORY_ACTION_UNSUPPORTED" or "INVALID_HISTORY_TURN" or "INVALID_ACTION" or "AGENT_REQUEST_REJECTED")) {
            RetireUncertainHistory(); throw new AiOperationException("HISTORY_OUTCOME_UNKNOWN");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !lifetime.IsCancellationRequested) {
            if (action == "revert") RetireUncertainHistory();
            throw new AiOperationException(action == "revert" ? "HISTORY_OUTCOME_UNKNOWN" : "NATIVE_HISTORY_TIMEOUT");
        }
        finally { lock (sync) historyMutationInFlight = false; }
    }

    private async Task ApplyHistoryActionCoreAsync(string action, string turnId, CancellationToken token)
    {
        string currentThread;
        lock (sync) currentThread = threadId;
        var result = await historyActionProtocol.ApplyAsync(agent.Protocol, currentThread, action, turnId,
            RpcAsync, token).ConfigureAwait(false);
        lock (sync) {
            if (disposed || threadId != currentThread) throw new AiOperationException("HISTORY_CHANGED");
            if (result.Action == "review") {
                // A turn may finish before review/start replies. Never resurrect a
                // turn already observed through turn/started and turn/completed.
                if (this.turnId != result.TurnId) {
                    turnStartedAtMs = 0;
                    BeginTurn();
                    this.turnId = result.TurnId;
                    if (result.Status != "inProgress") CompleteTurn(result.Status == "failed", result.Status == "interrupted");
                }
                return;
            }
            ResetHistoryAfterRevert(); nativeHistoryNeedsReload = true;
        }
        try { await LoadNativeCodexMessagesAsync(result.Reverted, token).ConfigureAwait(false); }
        catch {
            lock (sync) {
                // The native mutation already succeeded. Keep the old transcript
                // discarded even if reconstruction fails; do not retry the mutation.
                truncated = true;
                settingsNotice = "对话历史已回退，保留历史暂未加载。此操作不会恢复工作区文件。";
                PublishChange(); SaveArchive();
            }
            throw;
        }
    }

    private void ResetHistoryAfterRevert()
    {
        // A replacement has a new identity even in mutation receipts and metadata.
        // Delayed pages and snapshots from the old stream must never be merged.
        streamId = Guid.NewGuid().ToString("D");
        historyBase += messages.Count;
        messages.Clear(); messageIndex.Clear(); messageRevisions.Clear(); messageCharacters = 0;
        turnId = ""; turnStartedAtMs = 0; phase = "idle"; error = "";
        plan = null; usage = null; compacting = false; diff = ""; diffTruncated = false;
        diffVersion = Guid.NewGuid().ToString("N"); truncated = false;
        historyFloor = PublishChange();
        SaveArchive();
    }

    // Overload of the import loader: a revert returns metadata/cursors, not a
    // transcript. Reuse the bounded history reader with those authoritative anchors.
    private async Task LoadNativeCodexMessagesAsync(AiHistoryActionProtocol.RevertedState reverted, CancellationToken token)
    {
        var loaded = await AiHistoryActionProtocol.LoadRevertedAsync(reverted, RpcAsync, token).ConfigureAwait(false);
        lock (sync) {
            if (disposed || threadId != reverted.ThreadId) throw new AiOperationException("HISTORY_CHANGED");
            messages.Clear(); messageIndex.Clear(); messageRevisions.Clear(); messageCharacters = 0;
            messages.AddRange(loaded.Messages);
            EnsureMessageIndex();
            nativeHistoryNeedsReload = false; truncated = loaded.Truncated;
            settingsNotice = loaded.Truncated
                ? "对话历史已回退；保留历史仅加载了有上限的可用内容。此操作不会恢复工作区文件。"
                : "对话历史已回退；此操作不会恢复工作区文件。";
            TrimMessages();
            historyFloor = PublishChange(); SaveArchive();
        }
    }
}

// All provider calls have a deterministic callback seam. The cache is session
// local and bound to an exact native thread; neither callers nor persisted UI
// drafts can manufacture an eligible historical turn ID.
internal sealed class AiHistoryActionProtocol
{
    internal const int MaximumTurns = 30, MaximumIdLength = 256, MaximumResponseCharacters = 65536;
    // Read/Apply each issue one fixed RPC; only revert adds the existing bounded
    // history reader. The session wrapper enforces a deadline for the whole action.
    internal const int MaximumReadRpcRequests = 1, MaximumMutationRpcRequests = 1;
    internal const int MaximumReloadRpcRequests = CodexNativeHistory.MaxRequests;
    internal const int MaximumActionRpcRequests = MaximumMutationRpcRequests + MaximumReloadRpcRequests;
    internal const int RpcTimeoutSeconds = 25, MaximumActionSeconds = 50;
    private const int MaximumCursorLength = 4096;
    private readonly object gate = new();
    private readonly HashSet<string> completedTurns = new(StringComparer.Ordinal);
    private string cachedThread = "";
    private bool supported;
    private long generation;

    internal sealed record Turn(string Id, string Status);
    internal sealed record Result(bool Supported, Turn[] Turns, bool Truncated, bool Review, bool Revert, bool ForkAtTurn);
    internal sealed record RevertedState(string ThreadId, string TurnsCursor, string ItemsCursor);
    internal sealed record Mutation(string Action, string TurnId, string Status, RevertedState Reverted);
    internal static Result Unavailable => new(false, Array.Empty<Turn>(), false, false, false, false);

    internal void Invalidate() { lock (gate) Clear(); }
    private long Clear()
    {
        completedTurns.Clear(); cachedThread = ""; supported = false;
        return ++generation;
    }

    internal async Task<Result> ReadAsync(string protocol, string threadId, Func<string, object, Task<JsonElement>> rpc,
        CancellationToken token = default)
    {
        long current;
        lock (gate) current = Clear();
        try {
            token.ThrowIfCancellationRequested();
            if (protocol != "codex" || !SafeId(threadId)) return Unavailable;
            var response = await Request(rpc, "thread/turns/list", new {
                threadId, limit = MaximumTurns, sortDirection = "desc", itemsView = "notLoaded"
            }, token).ConfigureAwait(false);
            CheckResponse(response, "NATIVE_HISTORY_UNAVAILABLE");
            var data = AiAgentSession.P(response, "data");
            if (data.ValueKind != JsonValueKind.Array) throw new AiOperationException("NATIVE_HISTORY_UNAVAILABLE");
            var next = Cursor(response, "nextCursor", "NATIVE_HISTORY_UNAVAILABLE");
            var truncated = next.Length > 0 || data.GetArrayLength() > MaximumTurns;
            var selected = new List<Turn>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in data.EnumerateArray().Take(MaximumTurns)) {
                var id = AiAgentSession.S(row, "id");
                var status = AiAgentSession.S(row, "status");
                if (!SafeId(id)) { truncated = true; continue; }
                if (!seen.Add(id)) { duplicates.Add(id); truncated = true; continue; }
                if (status == "completed") selected.Add(new(id, status));
                else if (status is not ("inProgress" or "failed" or "interrupted")) truncated = true;
            }
            // A conflicting duplicate cannot certify a completed turn. Discard
            // every occurrence rather than trusting whichever happened to be first.
            selected.RemoveAll(t => duplicates.Contains(t.Id));
            lock (gate) {
                if (current != generation) return Unavailable;
                cachedThread = threadId; supported = true;
                foreach (var turn in selected) completedTurns.Add(turn.Id);
                return new(true, selected.ToArray(), truncated, true, selected.Count > 0, selected.Count > 0);
            }
        }
        catch (AiOperationException exception) when (Unsupported(exception)) {
            lock (gate) if (current == generation) Clear();
            return Unavailable;
        }
        catch {
            lock (gate) if (current == generation) Clear();
            throw;
        }
    }

    internal void ValidateTurn(string threadId, string id)
    {
        lock (gate) {
            if (!supported || cachedThread != threadId || !SafeId(id) || !completedTurns.Contains(id)) {
                Clear();
                throw new AiOperationException("INVALID_HISTORY_TURN");
            }
        }
    }

    internal async Task<Mutation> ApplyAsync(string protocol, string threadId, string action, string turnId,
        Func<string, object, Task<JsonElement>> rpc, CancellationToken token = default)
    {
        try {
            token.ThrowIfCancellationRequested();
            lock (gate) {
                if (action is not ("review" or "revert")) throw new AiOperationException("INVALID_ACTION");
                if (protocol != "codex" || !supported || cachedThread != threadId || !SafeId(threadId))
                    throw new AiOperationException("HISTORY_ACTION_UNSUPPORTED");
                if (action == "revert") ValidateTurn(threadId, turnId);
                else if (!string.IsNullOrEmpty(turnId)) throw new AiOperationException("INVALID_HISTORY_TURN");
                // Consume the selection before the RPC, including on transport
                // failure/timeout. Only a fresh successful read can authorize reuse.
                Clear();
            }
            if (action == "review") {
                var response = await Request(rpc, "review/start", new {
                    threadId, target = new { type = "uncommittedChanges" }, delivery = "inline"
                }, token).ConfigureAwait(false);
                CheckResponse(response, "HISTORY_ACTION_RESULT_INVALID");
                var turn = AiAgentSession.P(response, "turn");
                var id = AiAgentSession.S(turn, "id"); var status = AiAgentSession.S(turn, "status");
                if (AiAgentSession.S(response, "reviewThreadId") != threadId || !SafeId(id) ||
                    status is not ("inProgress" or "completed" or "failed" or "interrupted"))
                    throw new AiOperationException("HISTORY_ACTION_RESULT_INVALID");
                return new(action, id, status, null);
            }
            var result = await Request(rpc, "thread/revert", new { threadId, beforeTurnId = turnId }, token).ConfigureAwait(false);
            CheckResponse(result, "HISTORY_ACTION_RESULT_INVALID");
            var thread = AiAgentSession.P(result, "thread");
            var turns = AiAgentSession.P(thread, "turns");
            if (AiAgentSession.S(thread, "id") != threadId || AiAgentSession.P(thread, "ephemeral").ValueKind != JsonValueKind.False ||
                turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() != 0)
                throw new AiOperationException("HISTORY_ACTION_RESULT_INVALID");
            var turnsCursor = Cursor(result, "turnsBackwardsCursor", "HISTORY_ACTION_RESULT_INVALID");
            var itemsCursor = Cursor(result, "itemsBackwardsCursor", "HISTORY_ACTION_RESULT_INVALID");
            if (turnsCursor.Length == 0 && itemsCursor.Length > 0) throw new AiOperationException("HISTORY_ACTION_RESULT_INVALID");
            return new(action, turnId, "completed", new(threadId, turnsCursor, itemsCursor));
        }
        catch (AiOperationException exception) when (Unsupported(exception)) {
            throw new AiOperationException("HISTORY_ACTION_UNSUPPORTED");
        }
        finally { Invalidate(); }
    }

    internal static Task<CodexNativeHistory.ReadResult> LoadRevertedAsync(RevertedState reverted,
        Func<string, object, Task<JsonElement>> rpc, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (reverted == null || !SafeId(reverted.ThreadId) || !SafeCursor(reverted.TurnsCursor) || !SafeCursor(reverted.ItemsCursor) ||
            reverted.TurnsCursor.Length == 0 && reverted.ItemsCursor.Length > 0)
            throw new AiOperationException("HISTORY_ACTION_RESULT_INVALID");
        return CodexNativeHistory.ReadAsync(reverted.ThreadId, Anchored, token);

        async Task<JsonElement> Anchored(string method, object parameters)
        {
            var args = JsonSerializer.SerializeToElement(parameters);
            var cursor = AiAgentSession.S(args, "cursor");
            if (cursor.Length > 0) return await rpc(method, parameters).ConfigureAwait(false);
            var anchor = method == "thread/turns/list" ? reverted.TurnsCursor : reverted.ItemsCursor;
            if (anchor.Length == 0) return JsonSerializer.SerializeToElement(new { data = Array.Empty<object>(), nextCursor = (string)null });
            var values = args.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone(), StringComparer.Ordinal);
            values["cursor"] = anchor;
            try { return await rpc(method, values).ConfigureAwait(false); }
            catch (AiOperationException exception) when (Unsupported(exception)) {
                // This is a cursor issued by the successful mutation, not an API
                // discovery probe. A bad anchor must never trigger a legacy retry.
                throw new AiOperationException("NATIVE_HISTORY_PAGE_FAILED");
            }
        }
    }

    private static async Task<JsonElement> Request(Func<string, object, Task<JsonElement>> rpc, string method, object parameters,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await rpc(method, parameters).WaitAsync(TimeSpan.FromSeconds(RpcTimeoutSeconds), token).ConfigureAwait(false); }
        catch (TimeoutException) { throw new AiOperationException("NATIVE_HISTORY_TIMEOUT"); }
    }
    private static void CheckResponse(JsonElement response, string code)
    {
        if (response.ValueKind != JsonValueKind.Object || response.GetRawText().Length > MaximumResponseCharacters)
            throw new AiOperationException(code);
    }
    private static string Cursor(JsonElement response, string name, string code)
    {
        var value = AiAgentSession.P(response, name);
        if (value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String) throw new AiOperationException(code);
        var cursor = value.GetString() ?? "";
        if (!SafeCursor(cursor)) throw new AiOperationException(code);
        return cursor;
    }
    private static bool SafeCursor(string value) => value != null && value.Length <= MaximumCursorLength && !value.Any(char.IsControl);
    private static bool SafeId(string value) => value != null && value.Length is > 0 and <= MaximumIdLength &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Unsupported(AiOperationException exception) => exception.Code is "AGENT_METHOD_NOT_FOUND" or "AGENT_INVALID_PARAMS";
}
