// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class PortableAiFixtureValidation
{
    public static async Task RunAsync(string root)
    {
        Require(!string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase),
            "Run this validation through its apphost (dotnet run), not dotnet <assembly.dll>.");
        await CatalogAndAuthorityAsync(Path.Combine(root, "catalog"));
        await OperationReceiptsAsync(Path.Combine(root, "receipts"));
        await PermissionsAndQuestionsAsync(Path.Combine(root, "permissions"));
        await AuthorizationRevocationAsync(Path.Combine(root, "revocation"));
        await InterruptedAndDisposedAsync(Path.Combine(root, "cleanup"));
        Console.WriteLine("PASS portable AI fixtures complete (protocol simulation; no real model, tools, credentials or network)");
    }

    private static async Task CatalogAndAuthorityAsync(string root)
    {
        using var fixture = new FixtureScope(root);
        var legacy = fixture.Agent with { Id = "legacy-fixture", Strategy = "desktop_remote" };
        using var manager = new AiSessionManager(installRoot: root, catalog: () => Task.FromResult(new[] { fixture.Agent, legacy }));
        var caller = Guid.NewGuid();
        var catalog = Parse(await manager.ExecuteAsync(caller, "{\"action\":\"catalog\"}", CancellationToken.None));
        var agents = catalog.GetProperty("agents").EnumerateArray().ToArray();
        Require(agents.Length == 1 && Text(agents[0], "id") == "zcode" && Text(agents[0], "strategy") == "native",
            "A legacy URL strategy was advertised by the portable catalog.");
        Require(!catalog.GetRawText().Contains("remoteUrl", StringComparison.Ordinal), "Catalog leaked a retired remote URL.");
        var remote = new AiSessionManager.Request("remote", manager.HostId, AgentId: "zcode");
        await ExpectError(manager, caller, remote, "AGENT_STRATEGY_CHANGED");
        await ExpectError(manager, caller, remote with { HostId = "old-host" }, "HOST_CHANGED");
        var denied = Parse(await manager.ExecuteAsync(caller, Encode(remote), CancellationToken.None, () => false));
        Require(Text(denied, "error") == "AUTHORITY_CHANGED", "Retired requests bypassed the authority fence.");
        Require(!denied.GetRawText().Contains("remoteUrl", StringComparison.Ordinal), "Rejected legacy request returned a URL.");
        await ExpectError(manager, caller, Create(manager, root) with { AgentId = legacy.Id }, "AGENT_STRATEGY_CHANGED", "AGENT_UNAVAILABLE");
        var create = Create(manager, root);
        await ExpectError(manager, caller, create with { OperationId = "" }, "INVALID_OPERATION_ID");
        var forbidden = Parse(await manager.ExecuteAsync(caller, Encode(create), CancellationToken.None, () => false));
        Require(Text(forbidden, "error") == "AUTHORITY_CHANGED", "Unauthorized create reached the Agent.");
        if (!OperatingSystem.IsWindows())
            await ExpectError(manager, caller, new("attachment.begin", manager.HostId, Guid.NewGuid().ToString(), AttachmentId: Guid.NewGuid().ToString()), "AI_FILE_ACCESS_UNAVAILABLE");
        Require(fixture.Pids.Length == 0, "Catalog, retired URL requests, or rejected mutations started a worker.");
        Console.WriteLine("PASS fixture catalog: native-only, retired URL rejected, host/authority fencing and no worker on rejected requests");
    }

    private static async Task OperationReceiptsAsync(string root)
    {
        using var fixture = new FixtureScope(root, startDelay: 350);
        using var manager = fixture.Manager();
        var caller = Guid.NewGuid();
        var request = Create(manager, fixture.Workspace);
        var body = Encode(request);
        using var disconnected = new CancellationTokenSource();
        var accepted = manager.ExecuteAsync(caller, body, disconnected.Token);
        var starting = Stopwatch.StartNew();
        while (fixture.Count("rpc:session/create") == 0 && starting.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
        Require(fixture.Count("rpc:session/create") == 1, "The accepted fixture operation did not start.");
        disconnected.Cancel();
        try { await accepted; throw new InvalidOperationException("Disconnected caller still received the original receipt."); }
        catch (OperationCanceledException) when (disconnected.IsCancellationRequested) { }
        var creates = await Task.WhenAll(manager.ExecuteAsync(caller, body, CancellationToken.None), manager.ExecuteAsync(caller, body, CancellationToken.None));
        Require(creates[0] == creates[1], "Concurrent retries did not share the original operation receipt.");
        var created = Parse(creates[0]); AssertSuccess(created);
        var session = created.GetProperty("session");
        var id = Text(session, "id");
        Require(Text(session, "state") == "idle" && Text(session, "agentId") == "zcode" &&
            Text(session, "directory") == fixture.Workspace, "Native ZCode creation changed the session or selected directory.");
        Require(fixture.Count("rpc:session/create") == 1 && fixture.Pids.Length == 1 &&
            fixture.Count("subscribe-delivery:desktop-continuous") == 1 && fixture.Count("rpc:session/read") == 1,
            "Creation retries started extra workers or did not subscribe/read the existing native session.");
        await ExpectError(manager, caller, request with { Text = "different body" }, "OPERATION_ID_CONFLICT");
        var send = new AiSessionManager.Request("send", manager.HostId, Guid.NewGuid().ToString(), id, Text: "hello", ControlVersion: 1);
        var replies = await Task.WhenAll(manager.ExecuteAsync(caller, Encode(send), CancellationToken.None), manager.ExecuteAsync(caller, Encode(send), CancellationToken.None));
        Require(replies[0] == replies[1] && fixture.Count("rpc:session/send") == 1, "Send replay repeated a model/protocol operation.");
        var completed = await PollUntil(manager, caller, id, value => Text(value, "state") == "idle" &&
            value.GetProperty("messages").EnumerateArray().Any(message => Text(message, "role") == "assistant"));
        Require(completed.GetProperty("messages").EnumerateArray().Count(message => Text(message, "role") == "user") == 1 &&
            completed.GetProperty("messages").EnumerateArray().Count(message => Text(message, "text") == "fixture response") == 1,
            "Idempotent send duplicated the user/assistant transcript.");
        await ExpectError(manager, Guid.NewGuid(), send with { OperationId = Guid.NewGuid().ToString() }, "CONTROL_CHANGED");
        var ended = Parse(await manager.ExecuteAsync(caller, Encode(new("end", manager.HostId, Guid.NewGuid().ToString(), id, ControlVersion: 1)), CancellationToken.None));
        AssertSuccess(ended);
        Require(Text(ended.GetProperty("session"), "state") == "exited", "End did not report the worker as exited.");
        await fixture.RequireStoppedAsync();
        Console.WriteLine("PASS fixture receipts: disconnected caller recovery, concurrent create/send deduplication, body-conflict rejection, selected directory and explicit end cleanup");
    }

    private static async Task PermissionsAndQuestionsAsync(string root)
    {
        using var fixture = new FixtureScope(root);
        using var manager = fixture.Manager();
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var unconfirmed = Create(manager, fixture.Workspace) with { PermissionMode = "full-access" };
        await ExpectError(manager, owner, unconfirmed, "PERMISSION_CONFIRMATION_REQUIRED");
        Require(fixture.Pids.Length == 0, "An unconfirmed permission expansion launched a worker.");
        var created = Parse(await manager.ExecuteAsync(owner, Encode(Create(manager, fixture.Workspace)), CancellationToken.None));
        AssertSuccess(created); var id = Text(created.GetProperty("session"), "id");
        Require(fixture.Count("create-mode:build") == 1, "Default TermLink ask mode did not preserve ZCode build mode.");
        await Send(manager, owner, id, "approval", 1);
        var awaiting = await PollUntil(manager, owner, id, value => value.GetProperty("pending").GetArrayLength() == 1);
        var approval = awaiting.GetProperty("pending")[0]; var approvalId = Text(approval, "id");
        Require(Text(approval, "kind") == "approval" && approval.GetProperty("canApprove").GetBoolean(), "Native approval was not projected as a pending interaction.");
        var acquired = Parse(await manager.ExecuteAsync(other, Encode(new("control", manager.HostId, Guid.NewGuid().ToString(), id)), CancellationToken.None));
        AssertSuccess(acquired);
        var controlVersion = acquired.GetProperty("session").GetProperty("controlVersion").GetInt64();
        Require(controlVersion > 1, "Taking control did not advance the control version.");
        await ExpectError(manager, owner, new("answer", manager.HostId, Guid.NewGuid().ToString(), id,
            RequestId: approvalId, Decision: "allow", ControlVersion: 1), "CONTROL_CHANGED");
        var deny = new AiSessionManager.Request("answer", manager.HostId, Guid.NewGuid().ToString(), id,
            RequestId: approvalId, Decision: "deny", ControlVersion: controlVersion);
        var denied = await manager.ExecuteAsync(other, Encode(deny), CancellationToken.None); AssertSuccess(Parse(denied));
        Require(await manager.ExecuteAsync(other, Encode(deny), CancellationToken.None) == denied, "An answer retry did not preserve its receipt.");
        await PollUntil(manager, other, id, value => Text(value, "state") == "idle");
        Require(fixture.Count("decision:deny") == 1 && fixture.Count("decision:allow") == 0, "A stale controller or repeated receipt approved/executed the fixture interaction.");
        await ExpectError(manager, other, deny with { OperationId = Guid.NewGuid().ToString() }, "APPROVAL_EXPIRED");

        await Send(manager, other, id, "question", controlVersion);
        var question = (await PollUntil(manager, other, id, value => value.GetProperty("pending").GetArrayLength() == 1)).GetProperty("pending")[0];
        var questionId = Text(question, "id");
        await ExpectError(manager, other, new("answer", manager.HostId, Guid.NewGuid().ToString(), id,
            RequestId: questionId, Decision: "allow", ControlVersion: controlVersion), "ANSWER_REQUIRED");
        var answer = new AiSessionManager.Request("answer", manager.HostId, Guid.NewGuid().ToString(), id,
            RequestId: questionId, Decision: "allow", ControlVersion: controlVersion, Answers: new() { ["Continue?"] = ["Yes"] });
        AssertSuccess(Parse(await manager.ExecuteAsync(other, Encode(answer), CancellationToken.None)));
        await PollUntil(manager, other, id, value => Text(value, "state") == "idle");
        Require(fixture.Count("question-answer:yes_value") == 1, "The visible ZCode option label did not map to its protocol value.");

        await Send(manager, other, id, "desktop-auth", controlVersion);
        var blocked = await PollUntil(manager, other, id, value => Text(value, "error") == "AGENT_DESKTOP_AUTH_REQUIRED" && fixture.Count("desktop-headers-applied:false") == 1);
        Require(fixture.Count("desktop-headers-applied:true") == 0 && fixture.Count("desktop-result-fields:errorMessage,headersApplied") == 1,
            "The adapter supplied or forwarded desktop account credentials.");
        Require(!blocked.GetRawText().Contains("Authorization", StringComparison.OrdinalIgnoreCase), "A blocked desktop authorization leaked headers to the phone.");
        manager.Dispose(); await fixture.RequireStoppedAsync();
        Console.WriteLine("PASS fixture permissions: explicit grant confirmation, control transfer, one-time deny, question values and desktop-auth rejection without credentials");
    }

    private static async Task AuthorizationRevocationAsync(string root)
    {
        using var fixture = new FixtureScope(root);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var manager = new AiSessionManager(installRoot: root, catalog: async () =>
        {
            requested.TrySetResult(); await release.Task; return new[] { fixture.Agent };
        });
        var caller = Guid.NewGuid(); var authorized = true;
        var create = manager.ExecuteAsync(caller, Encode(Create(manager, fixture.Workspace)), CancellationToken.None, () => authorized);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(3)); authorized = false; release.TrySetResult();
        Require(Text(Parse(await create.WaitAsync(TimeSpan.FromSeconds(3))), "error") == "AUTHORITY_CHANGED" && fixture.Pids.Length == 0,
            "Revoked authority after discovery still created an Agent process.");
        Console.WriteLine("PASS fixture authority: revocation during async discovery prevents worker creation");
    }

    private static async Task InterruptedAndDisposedAsync(string root)
    {
        using (var fixture = new FixtureScope(Path.Combine(root, "interrupt")))
        using (var manager = fixture.Manager())
        {
            var caller = Guid.NewGuid();
            var created = Parse(await manager.ExecuteAsync(caller, Encode(Create(manager, fixture.Workspace)), CancellationToken.None));
            AssertSuccess(created); var id = Text(created.GetProperty("session"), "id");
            await Send(manager, caller, id, "hold", 1);
            await PollUntil(manager, caller, id, value => Text(value, "state") == "running");
            AssertSuccess(Parse(await manager.ExecuteAsync(caller, Encode(new("interrupt", manager.HostId, Guid.NewGuid().ToString(), id, ControlVersion: 1)), CancellationToken.None)));
            await PollUntil(manager, caller, id, value => Text(value, "state") == "idle");
            Require(fixture.Count("rpc:session/stop") == 1, "Interrupt did not stop the native turn.");
            manager.Dispose(); await fixture.RequireStoppedAsync();
            Require(Text(Parse(await manager.ExecuteAsync(caller, "{\"action\":\"catalog\"}", CancellationToken.None)), "error") == "HOST_RETIRED",
                "A disposed host still advertised its sessions.");
        }
        using (var fixture = new FixtureScope(Path.Combine(root, "exit")))
        using (var manager = fixture.Manager())
        {
            var caller = Guid.NewGuid();
            var created = Parse(await manager.ExecuteAsync(caller, Encode(Create(manager, fixture.Workspace)), CancellationToken.None));
            AssertSuccess(created); var id = Text(created.GetProperty("session"), "id");
            await Send(manager, caller, id, "exit", 1);
            var exited = await PollUntil(manager, caller, id, value => Text(value, "state") == "interrupted");
            Require(Text(exited, "error") is "AGENT_EXITED" or "AGENT_STREAM_LOST", "Unexpected native process exit was reported as a successful idle conversation.");
            await fixture.RequireStoppedAsync();
        }
        Console.WriteLine("PASS fixture process lifecycle: stop, host disposal and unexpected worker exit clean up without a success claim");
    }

    private static async Task Send(AiSessionManager manager, Guid caller, string id, string text, long version)
    {
        var response = Parse(await manager.ExecuteAsync(caller, Encode(new("send", manager.HostId, Guid.NewGuid().ToString(), id, Text: text, ControlVersion: version)), CancellationToken.None));
        AssertSuccess(response);
    }

    private static AiSessionManager.Request Create(AiSessionManager manager, string directory) =>
        new("create", manager.HostId, Guid.NewGuid().ToString(), AgentId: "zcode", Directory: directory,
            DirectoryMode: "custom", ModelId: "fixture-model", ProviderId: "fixture-provider");
    private static string Encode(AiSessionManager.Request request) => AiSessionManager.Encode(request);
    private static JsonElement Parse(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString()! : "";
    private static void AssertSuccess(JsonElement value) => Require(Text(value, "error").Length == 0, "Unexpected AI operation failure: " + Text(value, "error"));
    private static async Task ExpectError(AiSessionManager manager, Guid caller, AiSessionManager.Request request, params string[] errors)
    {
        var response = Parse(await manager.ExecuteAsync(caller, Encode(request), CancellationToken.None));
        Require(errors.Contains(Text(response, "error"), StringComparer.Ordinal), "Expected " + string.Join("/", errors) + ", received " + Text(response, "error"));
    }
    private static async Task<JsonElement> PollUntil(AiSessionManager manager, Guid caller, string id, Func<JsonElement, bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            var response = Parse(await manager.ExecuteAsync(caller, Encode(new("poll", manager.HostId, SessionId: id)), CancellationToken.None));
            AssertSuccess(response); var session = response.GetProperty("session");
            if (predicate(session)) return session;
            await Task.Delay(20);
        } while (watch.Elapsed < TimeSpan.FromSeconds(5));
        throw new TimeoutException("Fixture session did not reach its expected state.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FixtureScope : IDisposable
    {
        private readonly string? previousTrace = Environment.GetEnvironmentVariable(PortableAiFixture.TraceVariable);
        private readonly string? previousDelay = Environment.GetEnvironmentVariable(PortableAiFixture.DelayVariable);
        private readonly string trace;
        public string Workspace { get; }
        public AiAgentInstallation Agent { get; }
        public int[] Pids => Lines.Where(line => line.StartsWith("pid:", StringComparison.Ordinal)).Select(line => int.Parse(line[4..])).Distinct().ToArray();
        private string[] Lines => File.Exists(trace) ? File.ReadAllLines(trace) : [];
        public FixtureScope(string root, int startDelay = 0)
        {
            Directory.CreateDirectory(root); Workspace = Path.Combine(root, "workspace with spaces"); Directory.CreateDirectory(Workspace);
            trace = Path.Combine(root, "protocol-trace.txt");
            Environment.SetEnvironmentVariable(PortableAiFixture.TraceVariable, trace);
            Environment.SetEnvironmentVariable(PortableAiFixture.DelayVariable, startDelay.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Agent = new("zcode", "ZCode", Environment.ProcessPath!, "", "0.16.9", "zcode", "available");
        }
        public AiSessionManager Manager() => new(installRoot: Workspace, catalog: () => Task.FromResult(new[] { Agent }));
        public int Count(string value) => Lines.Count(line => line == value);
        public async Task RequireStoppedAsync()
        {
            var watch = Stopwatch.StartNew();
            while (Pids.Any(IsRunning) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Require(Pids.All(pid => !IsRunning(pid)), "Host disposal/end left a fixture AI worker running.");
        }
        private static bool IsRunning(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PortableAiFixture.TraceVariable, previousTrace);
            Environment.SetEnvironmentVariable(PortableAiFixture.DelayVariable, previousDelay);
            // A failed assertion must not leave this validation's own workers alive.
            foreach (var pid in Pids)
            {
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
