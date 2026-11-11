// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.Ai;

internal static class AiHistoryActionsValidation
{
    private const string Thread = "thr_fixture";

    internal static async Task RunAsync()
    {
        await ReadOnlyAndFiltering();
        await BoundsAndInvalidData();
        await UnsupportedAndFailures();
        await CacheAndConcurrency();
        await Mutations();
        await RevertedHistory();
        await SessionReset();
        UncertainSession();
        Console.WriteLine("PASS AI history actions: bounded metadata, thread-bound completed selections, review/revert protocol, authoritative cursors, stale-history reset, unsupported protocols and failure propagation (fixtures only)");
    }

    private static async Task ReadOnlyAndFiltering()
    {
        var helper = new AiHistoryActionProtocol();
        var calls = 0;
        var result = await helper.ReadAsync("codex", Thread, (method, parameters) => {
            calls++;
            Require(method == "thread/turns/list", "Read made a mutating or unrelated call");
            var args = Json(parameters);
            Require(args.EnumerateObject().Count() == 4 && S(args, "threadId") == Thread && args.GetProperty("limit").GetInt32() == 30 &&
                S(args, "itemsView") == "notLoaded" && S(args, "sortDirection") == "desc", "Read is not the first bounded metadata page");
            return Task.FromResult(Page(new object[] {
                Turn("good", "completed"), Turn("active", "inProgress"), Turn("failed", "failed"), Turn("cancelled", "interrupted"),
                Turn("old", "completed")
            }));
        });
        Require(calls == 1 && result.Supported && result.Review && result.Revert && result.ForkAtTurn && !result.Truncated, "Modern support flags or query bounds are wrong");
        Require(result.Turns.Select(t => t.Id).SequenceEqual(new[] { "good", "old" }) && result.Turns.All(t => t.Status == "completed"), "Active/failed turns are selectable");
        helper.ValidateTurn(Thread, "good");
        helper.ValidateTurn(Thread, "old");
        var wire = JsonSerializer.SerializeToElement(result, AiSessionManager.Json);
        Require(wire.GetProperty("supported").GetBoolean() && wire.GetProperty("forkAtTurn").GetBoolean() &&
            wire.GetProperty("turns")[0].GetProperty("id").GetString() == "good", "Public payload casing changed");
        result = await helper.ReadAsync("codex", Thread, (_, _) => Task.FromResult(Page(Array.Empty<object>())));
        Require(result.Supported && result.Review && !result.Revert && !result.ForkAtTurn && result.Turns.Length == 0, "Empty thread advertises selectable historical turns");
        foreach (var status in new[] { "inProgress", "failed", "interrupted", "unknown", "COMPLETED" }) {
            result = await helper.ReadAsync("codex", Thread, (_, _) => Task.FromResult(Page(new[] { Turn("rejected", status) })));
            Require(result.Turns.Length == 0, "Non-completed status became a selectable turn");
            Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "rejected"));
        }
    }

    private static async Task BoundsAndInvalidData()
    {
        var helper = new AiHistoryActionProtocol();
        var calls = 0;
        var result = await helper.ReadAsync("codex", Thread, (_, _) => {
            calls++; return Task.FromResult(Page(Enumerable.Range(0, 100).Select(i => Turn("turn_" + i, "completed")).ToArray(), "PRIVATE_CURSOR"));
        });
        Require(calls == 1 && result.Turns.Length == 30 && result.Truncated, "Untrusted provider bypassed the 30-row bound or triggered paging");
        Require(!JsonSerializer.Serialize(result).Contains("PRIVATE_CURSOR", StringComparison.Ordinal), "Opaque cursor escaped public metadata");
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "turn_30"));
        result = await helper.ReadAsync("codex", Thread, (_, _) => Task.FromResult(Page(new[] {
            Turn("duplicate", "completed"), Turn("duplicate", "failed"), Turn("bad/path", "completed"),
            Turn("bad\nname", "completed"), Turn("", "completed"), Turn(new string('a', 257), "completed"), Turn("valid_id-2", "completed")
        })));
        Require(result.Truncated && result.Turns.Select(t => t.Id).SequenceEqual(new[] { "valid_id-2" }), "Unsafe IDs or duplicate statuses were accepted");
        foreach (var response in new[] {
            Json(new { data = "invalid", nextCursor = (string)null! }), Json(new { data = Array.Empty<object>() }),
            Json(new { data = Array.Empty<object>(), nextCursor = 123 }),
            Page(Array.Empty<object>(), new string('c', 4097)), Page(Array.Empty<object>(), "bad\n"),
            Json(new { data = Array.Empty<object>(), nextCursor = (string)null!, unknown = new string('x', AiHistoryActionProtocol.MaximumResponseCharacters) }),
            Json(Array.Empty<object>())
        }) {
            await Seed(helper);
            await ExpectAsync("NATIVE_HISTORY_UNAVAILABLE", () => helper.ReadAsync("codex", Thread, (_, _) => Task.FromResult(response)));
            Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        }
        foreach (var id in new[] { null!, "", " ", "a/b", "bad\n", new string('a', 257) }) {
            await Seed(helper);
            Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, id));
        }
    }

    private static async Task UnsupportedAndFailures()
    {
        var helper = new AiHistoryActionProtocol();
        foreach (var protocol in new[] { "claude", "zcode", "acp", "grok", "opencode", "future" }) {
            await Seed(helper);
            var result = await helper.ReadAsync(protocol, Thread, Never);
            Require(!result.Supported && !result.Review && !result.Revert && !result.ForkAtTurn && result.Turns.Length == 0, "Unsupported protocol was advertised");
            Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        }
        foreach (var id in new[] { "", "bad/thread", new string('a', 257) })
            Require(!(await helper.ReadAsync("codex", id, Never)).Supported, "Invalid native thread caused an RPC");
        foreach (var code in new[] { "AGENT_METHOD_NOT_FOUND", "AGENT_INVALID_PARAMS" }) {
            await Seed(helper);
            var calls = 0;
            var result = await helper.ReadAsync("codex", Thread, (_, _) => { calls++; return Failure(code); });
            Require(!result.Supported && calls == 1, "Unsupported server fell back to another/unbounded history API");
            Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        }
        foreach (var code in new[] { "AUTH_REQUIRED", "AGENT_DESKTOP_AUTH_REQUIRED", "AGENT_REQUEST_REJECTED", "AGENT_TIMEOUT", "SESSION_ENDED", "STORAGE_FAILED" }) {
            await Seed(helper);
            await ExpectAsync(code, () => helper.ReadAsync("codex", Thread, (_, _) => Failure(code)));
            Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        }
        await Seed(helper);
        await ExpectAsync("NATIVE_HISTORY_TIMEOUT", () => helper.ReadAsync("codex", Thread, (_, _) => Task.FromException<JsonElement>(new TimeoutException())));
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Seed(helper);
        await Cancelled(() => helper.ReadAsync("codex", Thread, Never, cancelled.Token));
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
    }

    private static async Task CacheAndConcurrency()
    {
        var helper = new AiHistoryActionProtocol();
        await Seed(helper);
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn("other_thread", "good"));
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        await Seed(helper);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = helper.ReadAsync("codex", Thread, (_, _) => pending.Task);
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        pending.SetResult(Page(new[] { Turn("late", "completed") }));
        Require(!(await read).Supported, "A stale read repopulated invalidated cache");
        var older = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        read = helper.ReadAsync("codex", Thread, (_, _) => older.Task);
        await Seed(helper, "newer");
        older.SetResult(Page(new[] { Turn("older", "completed") }));
        Require(!(await read).Supported, "An overlapping older response replaced a newer read");
        helper.ValidateTurn(Thread, "newer");
        var lateFailure = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        read = helper.ReadAsync("codex", Thread, (_, _) => lateFailure.Task);
        await Seed(helper, "newest");
        lateFailure.SetException(new AiOperationException("AUTH_REQUIRED"));
        await ExpectAsync("AUTH_REQUIRED", () => read);
        helper.ValidateTurn(Thread, "newest");
        using var cancellation = new CancellationTokenSource();
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        read = helper.ReadAsync("codex", Thread, (_, _) => pending.Task, cancellation.Token);
        cancellation.Cancel();
        await Cancelled(() => read);
        pending.SetResult(Page(new[] { Turn("late_cancelled", "completed") }));
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "late_cancelled"));
    }

    private static async Task Mutations()
    {
        var helper = new AiHistoryActionProtocol();
        await ExpectAsync("HISTORY_ACTION_UNSUPPORTED", () => helper.ApplyAsync("codex", Thread, "review", "", Never));
        await Seed(helper);
        await ExpectAsync("INVALID_HISTORY_TURN", () => helper.ApplyAsync("codex", Thread, "revert", "unread", Never));
        await Seed(helper);
        await ExpectAsync("INVALID_ACTION", () => helper.ApplyAsync("codex", Thread, "thread/rollback", "good", Never));
        await Seed(helper);
        await ExpectAsync("HISTORY_ACTION_UNSUPPORTED", () => helper.ApplyAsync("claude", Thread, "review", "", Never));
        await Seed(helper);
        await ExpectAsync("HISTORY_ACTION_UNSUPPORTED", () => helper.ApplyAsync("codex", "foreign", "revert", "good", Never));
        await Seed(helper);
        await ExpectAsync("INVALID_HISTORY_TURN", () => helper.ApplyAsync("codex", Thread, "review", "good", Never));
        await Seed(helper);
        var result = await helper.ApplyAsync("codex", Thread, "review", "", (method, parameters) => {
            var args = Json(parameters);
            Require(method == "review/start" && args.EnumerateObject().Count() == 3 && S(args, "threadId") == Thread && S(args, "delivery") == "inline" &&
                S(args.GetProperty("target"), "type") == "uncommittedChanges" && args.GetProperty("target").EnumerateObject().Count() == 1, "Review target/delivery/scope changed");
            return Task.FromResult(Review());
        });
        Require(result.Action == "review" && result.TurnId == "review_turn" && result.Status == "inProgress" && result.Reverted == null, "Review result was lost");
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        await Seed(helper);
        result = await helper.ApplyAsync("codex", Thread, "revert", "good", (method, parameters) => {
            var args = Json(parameters);
            Require(method == "thread/revert" && args.EnumerateObject().Count() == 2 && S(args, "threadId") == Thread && S(args, "beforeTurnId") == "good", "Revert used a legacy API/count or arbitrary ID");
            return Task.FromResult(Revert("turn_anchor", "item_anchor"));
        });
        Require(result.Action == "revert" && result.Reverted?.ThreadId == Thread && result.Reverted.TurnsCursor == "turn_anchor" && result.Reverted.ItemsCursor == "item_anchor", "Authoritative revert anchors lost");
        await ExpectAsync("HISTORY_ACTION_UNSUPPORTED", () => helper.ApplyAsync("codex", Thread, "revert", "good", Never));
        foreach (var action in new[] { "review", "revert" }) {
            foreach (var code in new[] { "AGENT_METHOD_NOT_FOUND", "AGENT_INVALID_PARAMS", "AUTH_REQUIRED", "AGENT_TIMEOUT", "STORAGE_FAILED" }) {
                await Seed(helper);
                await ExpectAsync(code is "AGENT_METHOD_NOT_FOUND" or "AGENT_INVALID_PARAMS" ? "HISTORY_ACTION_UNSUPPORTED" : code,
                    () => helper.ApplyAsync("codex", Thread, action, action == "revert" ? "good" : "", (_, _) => Failure(code)));
                Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
            }
        }
        // The native event reader may invalidate selectors before the mutation
        // reply arrives. It must not retroactively revoke an already sent request.
        foreach (var action in new[] { "review", "revert" }) {
            await Seed(helper);
            result = await helper.ApplyAsync("codex", Thread, action, action == "revert" ? "good" : "", (_, _) => {
                helper.Invalidate();
                return Task.FromResult(action == "review" ? Review() : Revert(null!, null!));
            });
            Require(result.Action == action, "Own turn/reverted notification rejected an in-flight mutation");
            Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
        }
        foreach (var response in new[] {
            Review("foreign"), Json(new { reviewThreadId = Thread, turn = new { id = "bad/id", status = "inProgress" } }),
            Json(new { reviewThreadId = Thread, turn = new { id = "good", status = "unknown" } }), Json(new { })
        }) {
            await Seed(helper);
            await ExpectAsync("HISTORY_ACTION_RESULT_INVALID", () => helper.ApplyAsync("codex", Thread, "review", "", (_, _) => Task.FromResult(response)));
        }
        foreach (var response in new[] {
            Revert(null!, null!, "foreign"), Revert(null!, "invalid_anchor"),
            Json(new { thread = new { id = Thread, ephemeral = true, turns = Array.Empty<object>() }, turnsBackwardsCursor = (string)null!, itemsBackwardsCursor = (string)null! }),
            Json(new { thread = new { id = Thread, ephemeral = false, turns = new[] { Turn("old", "completed") } }, turnsBackwardsCursor = (string)null!, itemsBackwardsCursor = (string)null! }),
            Json(new { thread = new { id = Thread, ephemeral = false, turns = Array.Empty<object>() } })
        }) {
            await Seed(helper);
            await ExpectAsync("HISTORY_ACTION_RESULT_INVALID", () => helper.ApplyAsync("codex", Thread, "revert", "good", (_, _) => Task.FromResult(response)));
        }
        await Seed(helper);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Cancelled(() => helper.ApplyAsync("codex", Thread, "revert", "good", Never, cancellation.Token));
        Expect("INVALID_HISTORY_TURN", () => helper.ValidateTurn(Thread, "good"));
    }

    private static async Task RevertedHistory()
    {
        var calls = new List<string>();
        var state = new AiHistoryActionProtocol.RevertedState(Thread, "turn_anchor", "item_anchor");
        var result = await AiHistoryActionProtocol.LoadRevertedAsync(state, (method, parameters) => {
            var args = Json(parameters);
            Require(S(args, "threadId") == Thread && S(args, "sortDirection") == "desc", "Reconstruction escaped authoritative thread/direction");
            calls.Add(method + ":" + S(args, "cursor"));
            if (method == "thread/turns/list") {
                Require(S(args, "cursor") == "turn_anchor" && S(args, "itemsView") == "notLoaded", "Reconstruction did not use retained turn anchor");
                return Task.FromResult(Page(new[] { Turn("retained", "completed") }));
            }
            if (S(args, "cursor") == "item_anchor")
                return Task.FromResult(Page(new[] { new { turnId = "retained", item = new { id = "newer", type = "agentMessage", text = "kept response" } } }, "older_items"));
            Require(S(args, "cursor") == "older_items", "Later history page reused initial anchor");
            return Task.FromResult(Page(new[] { new { turnId = "retained", item = new { id = "older", type = "userMessage", content = new[] { new { type = "text", text = "kept question" } } } } }));
        });
        Require(calls.SequenceEqual(new[] { "thread/turns/list:turn_anchor", "thread/items/list:item_anchor", "thread/items/list:older_items" }) &&
            result.Messages.Select(m => m.Id).SequenceEqual(new[] { "older", "newer" }) && !result.Truncated, "Retained history reconstruction/order changed");
        result = await AiHistoryActionProtocol.LoadRevertedAsync(new(Thread, "", ""), Never);
        Require(result.Messages.Length == 0 && !result.Truncated, "Empty authoritative history queried the current head or retained stale messages");
        await ExpectAsync("AUTH_REQUIRED", () => AiHistoryActionProtocol.LoadRevertedAsync(state, (_, _) => Failure("AUTH_REQUIRED")));
        foreach (var code in new[] { "AGENT_INVALID_PARAMS", "AGENT_METHOD_NOT_FOUND" }) {
            var count = 0;
            await ExpectAsync("NATIVE_HISTORY_PAGE_FAILED", () => AiHistoryActionProtocol.LoadRevertedAsync(state, (_, _) => { count++; return Failure(code); }));
            Require(count == 1, "Rejected authoritative anchor triggered a fallback/retry");
        }
        await ExpectAsync("HISTORY_ACTION_RESULT_INVALID", () => AiHistoryActionProtocol.LoadRevertedAsync(new(Thread, new string('a', 4097), ""), Never));
        var requests = 0;
        result = await AiHistoryActionProtocol.LoadRevertedAsync(state, (method, _) => {
            requests++;
            if (method == "thread/turns/list")
                return Task.FromResult(Page(Enumerable.Range(0, 16).Select(i => Turn("turn_" + requests + "_" + i, "completed")).ToArray(), "turns_" + requests));
            return Task.FromResult(Page(Enumerable.Range(0, 64).Select(i => new {
                turnId = "retained", item = new { id = "item_" + requests + "_" + i, type = "agentMessage", text = "bounded" }
            }).ToArray(), "items_" + requests));
        });
        Require(result.Truncated && requests <= AiHistoryActionProtocol.MaximumReloadRpcRequests &&
            requests + AiHistoryActionProtocol.MaximumMutationRpcRequests <= AiHistoryActionProtocol.MaximumActionRpcRequests &&
            result.Messages.Length <= CodexNativeHistory.MaxItems, "Revert reload exceeded the combined RPC/item budget");
    }

    private static async Task SessionReset()
    {
        // Verify the actual partial-session replacement guards without a process,
        // credentials, filesystem changes, model calls or a production test hook.
        using var session = new AiAgentSession(new AiAgentInstallation("codex", "Fixture", "", "", "", "codex", "available"), "fixture", Guid.NewGuid());
        Set(session, "threadId", Thread); Set(session, "state", "idle");
        var messages = Field<List<AiAgentSession.Message>>(session, "messages");
        messages.Add(new("stale", "assistant", "removed response"));
        var before = Json(session.Snapshot(Guid.Empty, true));
        var oldCursor = S(before, "cursor"); var stream = S(before, "streamId");
        Invoke(session, "ResetHistoryAfterRevert");
        var receipt = Json(session.Snapshot(Guid.Empty, true));
        Require(S(receipt, "streamId") != stream && receipt.GetProperty("messages").GetArrayLength() == 0, "Mutation receipt reused the deleted history generation");
        var after = Json(session.Changes(Guid.Empty, stream, oldCursor));
        Require(after.GetProperty("reset").GetBoolean() && after.GetProperty("messages").GetArrayLength() == 0 && messages.Count == 0, "Revert did not reset stale message/index/revision state");
        Require(Field<Dictionary<string, int>>(session, "messageIndex").Count == 0 && Field<Dictionary<string, long>>(session, "messageRevisions").Count == 0 &&
            Field<long>(session, "messageCharacters") == 0, "Stale index/revision entries survived replacement");
        Expect("HISTORY_GAP", () => session.History(1, stream));
        var loader = typeof(AiAgentSession).GetMethod("LoadNativeCodexMessagesAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(AiHistoryActionProtocol.RevertedState), typeof(CancellationToken) }, null)!;
        await (Task)loader.Invoke(session, new object[] { new AiHistoryActionProtocol.RevertedState(Thread, "", ""), CancellationToken.None })!;
        after = Json(session.Snapshot(Guid.Empty, true));
        Require(after.GetProperty("messages").GetArrayLength() == 0 && !after.GetProperty("truncated").GetBoolean(), "Empty authoritative state did not replace the local transcript");
    }

    private static void UncertainSession()
    {
        var agent=new AiAgentInstallation("codex","Fixture","","","","codex","available");
        using var session=new AiAgentSession(agent,"fixture",Guid.NewGuid());
        Set(session,"threadId",Thread);Set(session,"state","idle");
        Field<List<AiAgentSession.Message>>(session,"messages").Add(new("removed","assistant","stale"));
        var before=Json(session.Snapshot(Guid.Empty,true));
        Invoke(session,"RetireUncertainHistory");
        var after=Json(session.Snapshot(Guid.Empty,true));
        Require(S(after,"state")=="interrupted"&&!after.GetProperty("canControl").GetBoolean()&&after.GetProperty("messages").GetArrayLength()==0&&S(after,"streamId")!=S(before,"streamId"),"Uncertain native mutation left writable stale history");
        var record=session.ArchiveRecord();
        Require(record.ReloadNativeHistory&&record.Truncated,"Uncertain native reload requirement not archived");
        using var restored=AiAgentSession.Restore(agent,record,null!);
        Require(Field<bool>(restored,"nativeHistoryNeedsReload")&&Json(restored.Snapshot(Guid.Empty,true)).GetProperty("truncated").GetBoolean(),"Restore lost partial-history/reload boundary");
    }

    private static void Set(AiAgentSession session, string name, object value) => typeof(AiAgentSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, value);
    private static T Field<T>(AiAgentSession session, string name) => (T)typeof(AiAgentSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    private static void Invoke(AiAgentSession session, string name) => typeof(AiAgentSession).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);
    private static Task<AiHistoryActionProtocol.Result> Seed(AiHistoryActionProtocol helper, string id = "good") =>
        helper.ReadAsync("codex", Thread, (_, _) => Task.FromResult(Page(new[] { Turn(id, "completed") })));
    private static object Turn(string id, string status) => new { id, status };
    private static JsonElement Page(object data, string next = null!) => Json(new { data, nextCursor = next, backwardsCursor = (string)null! });
    private static JsonElement Review(string thread = Thread) => Json(new { reviewThreadId = thread, turn = new { id = "review_turn", status = "inProgress", items = Array.Empty<object>() } });
    private static JsonElement Revert(string turns, string items, string thread = Thread) => Json(new {
        thread = new { id = thread, ephemeral = false, turns = Array.Empty<object>() }, turnsBackwardsCursor = turns, itemsBackwardsCursor = items
    });
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static string S(JsonElement value, string key) => AiAgentSession.S(value, key);
    private static Task<JsonElement> Failure(string code) => Task.FromException<JsonElement>(new AiOperationException(code));
    private static Task<JsonElement> Never(string method, object _) => throw new Exception("Unexpected RPC: " + method);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action run)
    {
        try { run(); }
        catch (AiOperationException ex) when (ex.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
    private static async Task ExpectAsync(string code, Func<Task> run)
    {
        try { await run(); }
        catch (AiOperationException ex) when (ex.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
    private static async Task Cancelled(Func<Task> run)
    {
        try { await run(); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation");
    }
}
