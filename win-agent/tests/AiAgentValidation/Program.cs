using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

if (Environment.GetEnvironmentVariable("TERMLINK_AI_DISCOVERY_FIXTURE") == "1" &&
    (args.Contains("--help") || args.Contains("--version"))) {
    if (args.Contains("acp")) { Console.Error.WriteLine("OpenCode ACP server"); return; }
    Console.WriteLine(args.Contains("--help") ? "app-server stream-json" : "codex-cli 0.162.1"); return;
}
if (args.Contains("stdio") && args.Contains("agent")) { await AcpValidation.Fixture(); return; }
if (args.Contains("acp")) {
    if (args.Contains("--help")) { Console.Error.WriteLine("OpenCode ACP server"); return; }
    await OpenCodeValidation.Fixture(); return;
}
if (args.Contains("--fixture")) {
    if (args.Contains("--help")) { Console.WriteLine("app-server stream-json"); return; }
    if (args.Contains("--version")) { Console.WriteLine(Environment.GetEnvironmentVariable("TERMLINK_AI_FIXTURE_CONFIGURATION") == "1" ? "codex-cli 0.160.0" : "fixture 1"); return; }
    await Fixture(); return;
}
if (args.Contains("app-server") || args.Contains("-p")) { await Fixture(); return; }

var directory = Path.Combine(Path.GetTempPath(), "termlink-ai-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
if (args.Contains("--discovery-only")) { await DiscoveryValidation.Run(directory); return; }
if (args.Contains("--installed-desktop-probe")) { await DiscoveryValidation.Installed(); return; }
if (args.Contains("--desktop-runtime-only")) { await DesktopHistoryValidation.Security(directory); return; }
if (args.Contains("--real-desktop-history")) { await DesktopHistoryValidation.Real(directory); return; }
if (args.Contains("--real-claude-desktop")) { await ClaudeDesktopValidation.Real(directory); return; }
if (args.Contains("--opencode-only")) { await OpenCodeValidation.Run(directory); return; }
if (args.Contains("--real-opencode")) { await OpenCodeValidation.Real(directory); return; }
if (args.Contains("--acp-only")) { await AcpValidation.Run(directory); return; }
if (args.Contains("--grok-probe")) { await AcpValidation.ProbeInstalled(); return; }
if (args.Contains("--real-grok")) { await GrokRealValidation.Run(directory); return; }
if (args.Contains("--real-grok-permissions")) { await GrokRealValidation.Permissions(directory); return; }
if (args.Length == 2 && args[0] == "--real-grok-resume") { await GrokRealValidation.Resume(args[1]); return; }
if (args.Contains("--timeline-only")) { await TimelineValidation.Run(directory); return; }
if (args.Contains("--artifacts-only")) { ArtifactValidation.Run(directory); return; }
if (args.Contains("--real-native-history")) { await NativeHistoryValidation.Real(directory, args.Contains("--only-claude") ? "claude" : args.Contains("--only-codex") ? "codex" : ""); return; }
if (args.Contains("--configuration-only")) { await ConfigurationValidation.Run(directory); return; }
if (args.Contains("--real-configuration")) { await ConfigurationValidation.Real(directory, args.Contains("--send")); return; }
if (args.Contains("--bridge")) {
    if (args.Contains("--fake-agent")) {
        File.WriteAllText(Path.Combine(directory, "codex.cmd"), "@echo off\r\n\"" + Environment.ProcessPath + "\" --fixture %*\r\n");
        Environment.SetEnvironmentVariable("PATH", directory);
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", "codex");
    }
    using var manager = new AiSessionManager(installRoot: directory);
    using var listener = new System.Net.HttpListener();
    listener.Prefixes.Add("http://127.0.0.1:18793/"); listener.Start();
    var caller = Guid.NewGuid();
    using var bridgeLifetime = new CancellationTokenSource();
    var pendingRequests = new HashSet<Task>();
    Console.WriteLine("AI acceptance bridge on loopback port 18793; test workspace: " + directory);
    while (true) {
        var context = await listener.GetContextAsync();
        if (context.Request.HttpMethod == "POST" && context.Request.Url.AbsolutePath == "/shutdown") {
            context.Response.StatusCode = 204; context.Response.Close(); break;
        }
        pendingRequests.RemoveWhere(t => t.IsCompleted);
        if (pendingRequests.Count >= 16) { context.Response.StatusCode = 429; context.Response.Close(); continue; }
        pendingRequests.Add(Serve());
        async Task Serve() { try {
            if (context.Request.HttpMethod != "POST" || context.Request.Url.AbsolutePath != "/ai" ||
                context.Request.ContentType != "application/json" || context.Request.Headers["Origin"] != null ||
                context.Request.ContentLength64 is < 0 or > 49152) { context.Response.StatusCode = 400; return; }
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var response = await manager.ExecuteAsync(caller, body, bridgeLifetime.Token);
            var bytes = System.Text.Encoding.UTF8.GetBytes(response);
            context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
        } catch (Exception e) when (e is OperationCanceledException or System.Net.HttpListenerException or IOException) { }
        finally { context.Response.Close(); } }
    }
    bridgeLifetime.Cancel(); await Task.WhenAll(pendingRequests); return;
}
if (args.Contains("--real-models")) {
    foreach (var installation in await AiAgentDiscovery.DetectAsync()) {
        if (installation.Status != "available") continue;
        try {
            var list = await AiAgentSession.ListModelsAsync(installation, CancellationToken.None);
            Console.WriteLine($"MODELS {installation.Id}: count={list.Models.Length} truncated={list.Truncated} ids={string.Join(",", list.Models.Select(m => m.ModelId))}");
            Require(list.Models.Length > 0, "Expected installed agent model options");
        } catch (AiOperationException e) when (e.Code == "MODEL_LIST_UNSUPPORTED") { Console.WriteLine($"MODELS {installation.Id}: unsupported; manual fallback"); }
    }
    return;
}
if (args.Contains("--real-features")) {
    foreach (var installation in await AiAgentDiscovery.DetectAsync()) {
        if (installation.Status != "available" || installation.Protocol is not ("codex" or "claude") ||
            args.Contains("--only-claude") && installation.Id != "claude" || args.Contains("--only-codex") && installation.Id != "codex") continue;
        var modelIndex = Array.IndexOf(args, "--model");
        var imageIndex = Array.IndexOf(args, "--image");
        await FeaturesValidation.Real(installation, directory, modelIndex >= 0 ? args[modelIndex + 1] : "", imageIndex >= 0 ? args[imageIndex + 1] : "");
    }
    return;
}
if (args.Contains("--real-smoke")) {
    var installations = await AiAgentDiscovery.DetectAsync();
    foreach (var installation in installations) {
        Console.WriteLine($"DISCOVERY {installation.Id}: {installation.Status} {installation.Version}");
        if (installation.Status != "available" || args.Contains("--only-zcode") && installation.Id != "zcode" ||
            args.Contains("--only-claude") && installation.Id != "claude" || args.Contains("--only-codex") && installation.Id != "codex") continue;
        var owner = Guid.NewGuid();
        var modelArgument = Array.IndexOf(args, "--model");
        var model = modelArgument >= 0 && modelArgument + 1 < args.Length ? args[modelArgument + 1] : "";
        using var session = new AiAgentSession(installation, directory, owner, model);
        try {
            await session.StartAsync();
            Console.WriteLine($"START {installation.Id}: {Snapshot(session, owner).GetProperty("state")}");
            if (args.Contains("--send")) {
                await session.ApplyAsync(owner, new("send", OperationId: Guid.NewGuid().ToString(), Text: "Reply exactly TERMLINK_AI_SMOKE_OK. Do not use tools, read files or run commands.", ControlVersion: 1));
                await Wait(() => Snapshot(session, owner).GetProperty("state").GetString() != "running", 90_000);
                var result = Snapshot(session, owner);
                var replyCount = result.GetProperty("messages").EnumerateArray().Count(m =>
                    m.GetProperty("role").GetString() == "assistant" && m.GetProperty("kind").GetString() == "text" && m.GetProperty("text").GetString().Contains("TERMLINK_AI_SMOKE_OK"));
                if (installation.Protocol == "claude" && replyCount != 1) Environment.ExitCode = 1;
                if (result.GetProperty("error").GetString().Length > 0 || !result.GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("role").GetString() == "assistant" && m.GetProperty("text").GetString().Contains("TERMLINK_AI_SMOKE_OK"))) Environment.ExitCode = 1;
                Console.WriteLine($"TURN {installation.Id}: state={result.GetProperty("state")} error={result.GetProperty("error")} messages={result.GetProperty("messages").GetArrayLength()} replyMatched={result.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "assistant" && m.GetProperty("text").GetString().Contains("TERMLINK_AI_SMOKE_OK"))}");
                Console.WriteLine($"TURN {installation.Id}: matching text replies={replyCount}");
            }
        } catch (Exception e) { Console.WriteLine($"FAILED {installation.Id}: {e.GetType().Name} {e.Message}"); Environment.ExitCode = 1; }
    }
    return;
}

var trace = Path.Combine(directory, "discovery-trace.txt");
await DiscoveryValidation.Run(directory);
await DesktopHistoryValidation.Security(directory);
await OpenCodeValidation.Run(directory);
Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_TRACE", trace);
foreach (var protocol in new[] { "codex", "claude" }) {
    Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", protocol);
    var installation = new AiAgentInstallation(protocol, protocol, Environment.ProcessPath, "", "1", protocol, "available");
    var list = await AiAgentSession.ListModelsAsync(installation, CancellationToken.None);
    Require(list.Models.Select(m => m.ModelId).SequenceEqual(new[] { "model-a", "model-b" }), "Model parsing, deduplication, hidden filter or pagination");
    Require(list.Models[0].Name == "Model A" && list.Models.All(m => m.ProviderId == ""), "Model projection");
    if (protocol == "codex") {
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_MODEL_MODE", "cycle");
        await Reject(() => AiAgentSession.ListModelsAsync(installation, CancellationToken.None), "MODEL_LIST_UNAVAILABLE");
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_MODEL_MODE", "malformed");
        await Reject(() => AiAgentSession.ListModelsAsync(installation, CancellationToken.None), "MODEL_LIST_UNAVAILABLE");
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_MODEL_MODE", "hang");
        using var timeout = new CancellationTokenSource(700);
        try { await AiAgentSession.ListModelsAsync(installation, timeout.Token); throw new Exception("Cancelled model query returned"); }
        catch (OperationCanceledException) { }
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_MODEL_MODE", null);
    }
}
var traced = File.ReadAllLines(trace);
Require(!traced.Any(s => s is "thread/start" or "session/create" or "turn/start" or "user"), "Model discovery created conversation or turn");
foreach (var pid in traced.Where(s => s.StartsWith("pid:")).Select(s => int.Parse(s[4..]))) {
    await Wait(() => { try { using var process = System.Diagnostics.Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } });
}
Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_TRACE", null);
Console.WriteLine("PASS model listing: pagination, filtering, cancellation, no conversations, child cleanup");

var remoteStore = new ZcodeRemoteAddressStore(Path.Combine(directory, "remote.dpapi"));
var pairingLink = "https://zcode.z.ai/remote/v4?pair=fixture-only#test";
foreach (var invalid in new[] { "http://zcode.z.ai/remote/v4", "https://zcode.z.ai.evil.test/remote/v4",
    "https://user@zcode.z.ai/remote/v4", "https://zcode.z.ai:8443/remote/v4", "file:///remote/v4",
    "https://zcode.z.ai/remote/../../other", "https://zcode.z.ai/other", "javascript:alert(1)" })
    Require(!ZcodeRemoteAddressStore.IsAllowed(invalid), "Untrusted remote address accepted");
remoteStore.Save(pairingLink);
Require(remoteStore.Read() == pairingLink && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "remote.dpapi"))).Contains("fixture-only"), "DPAPI roundtrip or secret protection");
using (var remoteManager = new AiSessionManager(remoteStore)) {
    var caller = Guid.NewGuid();
    var catalog = await remoteManager.ExecuteAsync(caller, "{\"action\":\"catalog\"}", CancellationToken.None);
    using var parsedCatalog = JsonDocument.Parse(catalog);
    var remoteCapability = parsedCatalog.RootElement.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "zcode");
    Require(remoteCapability.GetProperty("strategy").GetString() == "desktop_remote" && remoteCapability.GetProperty("status").GetString() == "available", "Remote capability not available");
    Require(!catalog.Contains("fixture-only") && !catalog.Contains("remoteUrl"), "Catalog leaked pairing link");
    Require(parsedCatalog.RootElement.GetProperty("directoryMode").GetString() == "per_session" &&
        Path.GetFileName(parsedCatalog.RootElement.GetProperty("defaultDirectory").GetString()) == "AiWorkspaces", "Managed workspace capability missing");
    var body = AiSessionManager.Encode(new AiSessionManager.Request("remote", remoteManager.HostId, AgentId: "zcode"));
    var reply = await remoteManager.ExecuteAsync(caller, body, CancellationToken.None);
    using var parsedReply = JsonDocument.Parse(reply);
    Require(parsedReply.RootElement.GetProperty("remoteUrl").GetString() == pairingLink, "Remote link response");
    Require((await remoteManager.ExecuteAsync(caller, body, CancellationToken.None, () => false)).Contains("AUTHORITY_CHANGED"), "Remote authorization fence");
    Require((await remoteManager.ExecuteAsync(caller, body.Replace(remoteManager.HostId, "old"), CancellationToken.None)).Contains("HOST_CHANGED"), "Remote host fence");
    var create = AiSessionManager.Encode(new AiSessionManager.Request("create", remoteManager.HostId, Guid.NewGuid().ToString(), AgentId: "zcode", Directory: directory));
    Require((await remoteManager.ExecuteAsync(caller, create, CancellationToken.None)).Contains("AGENT_STRATEGY_CHANGED"), "Remote strategy spawned a native agent");
    remoteStore.Clear();
    Require(remoteStore.Read() == "" && (await remoteManager.ExecuteAsync(caller, body, CancellationToken.None)).Contains("REMOTE_NOT_CONFIGURED"), "Cleared URL still accessible");
    var cleared = await remoteManager.ExecuteAsync(caller, "{\"action\":\"catalog\"}", CancellationToken.None);
    using var parsedClear = JsonDocument.Parse(cleared);
    Require(parsedClear.RootElement.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "zcode").GetProperty("strategy").GetString() == "native", "Cleared config did not restore native strategy");
}
Console.WriteLine("PASS desktop remote: DPAPI, allowlist, catalog redaction, routing, auth/host fences, clearing");

var actor = Guid.NewGuid();
Require(AiAgentSession.ToolPresentation("{\"type\":\"commandExecution\",\"command\":\"npm test\",\"status\":\"completed\",\"exitCode\":1}").Status == "failed", "Exit failure shown as success");
Require(AiAgentSession.ToolPresentation("{\"type\":\"fileChange\",\"changes\":[{\"path\":\"src/a.cs\"}]}").Summary == "src/a.cs", "File summary missing");
Require(AiAgentSession.ToolPresentation("{\"tool_name\":\"Bash\",\"input\":{\"command\":\"echo approval\"}}", "claude/tool").Summary == "echo approval", "Approval summary missing");
Require(AiAgentSession.ToolPresentation("invalid JSON").Status == "unknown", "Unknown record shown as completed");
foreach (var protocol in new[] { "codex", "zcode", "claude" }) {
    Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", protocol);
    using var session = new AiAgentSession(new(protocol, protocol, Environment.ProcessPath, "", "1", protocol, "available"), directory, actor,
        protocol == "claude" ? "fixture-model" : "");
    await session.StartAsync();
    Require(Snapshot(session, actor).GetProperty("state").GetString() == "idle", protocol + " initialization");
    await session.ApplyAsync(actor, new("send", Text: "approval", OperationId: Guid.NewGuid().ToString(), ControlVersion: 1));
    await Wait(() => Snapshot(session, actor).GetProperty("pending").GetArrayLength() == 1);
    var catalogItem = JsonSerializer.SerializeToElement(session.Snapshot(actor, false), AiSessionManager.Json);
    Require(catalogItem.GetProperty("title").GetString() == "approval" &&
        catalogItem.GetProperty("preview").GetString() == "approval" &&
        catalogItem.GetProperty("pendingCount").GetInt32() == 1 &&
        catalogItem.GetProperty("messages").GetArrayLength() == 0, "catalog summary without full transcript");
    var pending = Snapshot(session, actor).GetProperty("pending")[0].GetProperty("id").GetString();
    var other = Guid.NewGuid();
    await session.ApplyAsync(other, new("control"));
    await Reject(() => session.ApplyAsync(actor, new("answer", RequestId: pending, Decision: "allow", ControlVersion: 1)), "CONTROL_CHANGED");
    await session.ApplyAsync(other, new("answer", RequestId: pending, Decision: "deny", ControlVersion: 2));
    await Wait(() => Snapshot(session, other).GetProperty("state").GetString() == "idle");
    await Reject(() => session.ApplyAsync(other, new("answer", RequestId: pending, Decision: "allow", ControlVersion: 2)), "APPROVAL_EXPIRED");
    await session.ApplyAsync(other, new("send", Text: "hello", OperationId: Guid.NewGuid().ToString(), ControlVersion: 2));
    await Wait(() => Snapshot(session, other).GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("text").GetString() == "fixture response"));
    await Wait(() => Snapshot(session, other).GetProperty("state").GetString() == "idle");
    await session.ApplyAsync(other, new("send", Text: "question", OperationId: Guid.NewGuid().ToString(), ControlVersion: 2));
    await Wait(() => Snapshot(session, other).GetProperty("pending").GetArrayLength() == 1);
    var question = Snapshot(session, other).GetProperty("pending")[0];
    var questionId = question.GetProperty("id").GetString();
    await Reject(() => session.ApplyAsync(other, new("answer", RequestId: questionId, Decision: "allow", ControlVersion: 2)), "ANSWER_REQUIRED");
    var answers = new Dictionary<string, string[]> { [question.GetProperty("questions")[0].GetProperty("id").GetString()] = new[] { "Yes" } };
    var invalidAnswers = new Dictionary<string, string[]> { [answers.Keys.Single()] = null };
    await Reject(() => session.ApplyAsync(other, new("answer", RequestId: questionId, Decision: "allow", ControlVersion: 2, Answers: invalidAnswers)), "ANSWER_REQUIRED");
    await session.ApplyAsync(other, new("answer", RequestId: questionId, Decision: "allow", ControlVersion: 2, Answers: answers));
    await Wait(() => Snapshot(session, other).GetProperty("state").GetString() == "idle");
    await session.ApplyAsync(other, new("send", Text: "tools-ui", OperationId: Guid.NewGuid().ToString(), ControlVersion: 2));
    await Wait(() => Snapshot(session, other).GetProperty("state").GetString() == "idle");
    var toolMessages = Snapshot(session, other).GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
    Require(toolMessages.Length == 3, "Tool result/start duplicated timeline rows");
    Require(toolMessages[0].GetProperty("summary").GetString() == "echo safe-preview" &&
        toolMessages[0].GetProperty("title").GetString() == "运行命令" && toolMessages[0].GetProperty("status").GetString() == "completed", "Tool presentation missing");
    Require(toolMessages[1].GetProperty("status").GetString() == "failed" && toolMessages[2].GetProperty("status").GetString() == "unknown", "Failed/unknown tool misreported");
    Require(toolMessages[0].GetProperty("text").GetString().Contains("safe-preview"), "Tool details lost");
    await session.ApplyAsync(other, new("end", ControlVersion: 2));
    Require(!session.IsAlive, "end must stop owned child");
    Console.WriteLine("PASS adapter " + protocol + ": approval denial, stale control/request, message, end");
}
Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", "codex");
var oldPath = Environment.GetEnvironmentVariable("PATH");
try {
    File.WriteAllText(Path.Combine(directory, "codex.cmd"), "@echo off\r\n\"" + Environment.ProcessPath + "\" --fixture %*\r\n");
    Environment.SetEnvironmentVariable("PATH", directory);
    await AiAgentDiscovery.CatalogAsync(true);
    using var manager = new AiSessionManager(installRoot: directory);
    var modelBody = AiSessionManager.Encode(new AiSessionManager.Request("models", manager.HostId, AgentId: "codex"));
    Require((await manager.ExecuteAsync(actor, modelBody, CancellationToken.None)).Contains("model-a"), "Model bridge without mutation ID");
    Require((await manager.ExecuteAsync(actor, modelBody.Replace(manager.HostId, "old"), CancellationToken.None)).Contains("HOST_CHANGED"), "Model host fence");
    Require((await manager.ExecuteAsync(actor, modelBody, CancellationToken.None, () => false)).Contains("AUTHORITY_CHANGED"), "Model authority fence");
    Require(!Directory.Exists(Path.Combine(directory, "AiWorkspaces")), "Model listing created managed workspace");
    var body = AiSessionManager.Encode(new AiSessionManager.Request("create", manager.HostId, Guid.NewGuid().ToString(), AgentId: "codex", Directory: directory));
    var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => manager.ExecuteAsync(actor, body, CancellationToken.None)));
    Require(replies.Distinct().Count() == 1 && !replies[0].Contains("\"error\":\"" + "AGENT"), "duplicate create receipt");
    using var first = JsonDocument.Parse(replies[0]);
    Require(first.RootElement.TryGetProperty("session", out var created), "create failed: " + replies[0]);
    var id = created.GetProperty("id").GetString();
    var conflict = await manager.ExecuteAsync(actor, body.Replace(directory.Replace("\\", "\\\\"), "Z:\\\\missing"), CancellationToken.None);
    Require(conflict.Contains("OPERATION_ID_CONFLICT"), "conflicting operation");
    var oldHost = await manager.ExecuteAsync(actor, AiSessionManager.Encode(new AiSessionManager.Request("end", "old", Guid.NewGuid().ToString(), SessionId: id, ControlVersion: 1)), CancellationToken.None);
    Require(oldHost.Contains("HOST_CHANGED"), "host incarnation fence");
    var managedBody = AiSessionManager.Encode(new AiSessionManager.Request("create", manager.HostId, Guid.NewGuid().ToString(), AgentId: "codex", DirectoryMode: "managed"));
    var managedReplies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => manager.ExecuteAsync(actor, managedBody, CancellationToken.None)));
    Require(managedReplies.Distinct().Count() == 1, "Managed duplicate receipt");
    using var managedReply = JsonDocument.Parse(managedReplies[0]);
    var managedPath = managedReply.RootElement.GetProperty("session").GetProperty("directory").GetString();
    var workspaceRoot = Path.Combine(directory, "AiWorkspaces");
    Require(Path.GetDirectoryName(managedPath) == workspaceRoot && Directory.Exists(managedPath) &&
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(managedPath), @"^codex_\d{8}_\d{6}_\d{3}_[a-f0-9]{8}$"), "Managed workspace location/name");
    Require(Directory.GetDirectories(workspaceRoot).Length == 1, "Retry created extra directories");
    var secondBody = AiSessionManager.Encode(new AiSessionManager.Request("create", manager.HostId, Guid.NewGuid().ToString(), AgentId: "codex", DirectoryMode: "managed"));
    using var secondReply = JsonDocument.Parse(await manager.ExecuteAsync(actor, secondBody, CancellationToken.None));
    Require(secondReply.RootElement.GetProperty("session").GetProperty("directory").GetString() != managedPath &&
        Directory.GetDirectories(workspaceRoot).Length == 2, "New session reused directory");
    Require(created.GetProperty("directory").GetString() == directory, "Custom directory changed");
    File.WriteAllText(Path.Combine(workspaceRoot, "not-a-folder.txt"), "keep");
    var browse = AiSessionManager.Encode(new AiSessionManager.Request("directories", manager.HostId, Directory: workspaceRoot));
    using var folders = JsonDocument.Parse(await manager.ExecuteAsync(actor, browse, CancellationToken.None));
    Require(folders.RootElement.GetProperty("directories").GetProperty("entries").GetArrayLength() == 2, "Browser included files");
    Require((await manager.ExecuteAsync(actor, browse, CancellationToken.None, () => false)).Contains("AUTHORITY_CHANGED"), "Browser authorization");
    Require((await manager.ExecuteAsync(actor, browse.Replace(manager.HostId, "old"), CancellationToken.None)).Contains("HOST_CHANGED"), "Browser host fence");
    Require((await manager.ExecuteAsync(actor, AiSessionManager.Encode(new AiSessionManager.Request("directories", manager.HostId, Directory: @"\\localhost\C$")), CancellationToken.None)).Contains("LOCAL_DIRECTORY_REQUIRED"), "Network directory accepted");
    var blockedRoot = Path.Combine(directory, "blocked-install");
    Directory.CreateDirectory(blockedRoot); File.WriteAllText(Path.Combine(blockedRoot, "AiWorkspaces"), "not a directory");
    using var blockedManager = new AiSessionManager(installRoot: blockedRoot);
    var blockedCreate = AiSessionManager.Encode(new AiSessionManager.Request("create", blockedManager.HostId, Guid.NewGuid().ToString(), AgentId: "codex", DirectoryMode: "managed"));
    Require((await blockedManager.ExecuteAsync(actor, blockedCreate, CancellationToken.None)).Contains("AI_WORKSPACE_CREATE_FAILED"), "Workspace creation failure not actionable");
    Console.WriteLine("PASS workspaces: timestamps, per-session isolation, duplicate receipts, custom directory, encrypted directory browsing, write failure");
    var forget = new AiSessionManager.Request("forget", manager.HostId, Guid.NewGuid().ToString(), SessionId: id, ControlVersion: 1);
    Require((await manager.ExecuteAsync(actor, AiSessionManager.Encode(forget), CancellationToken.None)).Contains("AGENT_BUSY"), "Cannot forget live process");
    await manager.ExecuteAsync(actor, AiSessionManager.Encode(new AiSessionManager.Request("end", manager.HostId, Guid.NewGuid().ToString(), SessionId: id, ControlVersion: 1)), CancellationToken.None);
    var forgetBody = AiSessionManager.Encode(forget with { OperationId = Guid.NewGuid().ToString() });
    var forgotten = await manager.ExecuteAsync(actor, forgetBody, CancellationToken.None);
    Require(forgotten.Contains("deletedSessionId") && forgotten == await manager.ExecuteAsync(actor, forgetBody, CancellationToken.None), "forget receipt replay");
    Require((await manager.ExecuteAsync(actor, AiSessionManager.Encode(new AiSessionManager.Request("poll", manager.HostId, SessionId: id)), CancellationToken.None)).Contains("SESSION_NOT_FOUND"), "forgotten session remains visible");
    Require(Directory.Exists(directory), "forget deleted workspace");
    Console.WriteLine("PASS manager: concurrent duplicate create, conflict, host fence");
} finally { Environment.SetEnvironmentVariable("PATH", oldPath); Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", null); }
await FeaturesValidation.Run(directory);
await TimelineValidation.Run(directory);
ArtifactValidation.Run(directory);
await ConfigurationValidation.Run(directory);
await NativeHistoryValidation.Run(directory);
await AcpValidation.Run(directory);
Console.WriteLine("AI VALIDATION PASS");

static JsonElement Snapshot(AiAgentSession session, Guid viewer) => JsonSerializer.SerializeToElement(session.Snapshot(viewer, true), AiSessionManager.Json);
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Wait(Func<bool> ready, int timeout = 5000) {
    var deadline = Environment.TickCount64 + timeout;
    while (!ready()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException("AI validation timed out"); await Task.Delay(30); }
}
static async Task Reject(Func<Task> action, string code) {
    try { await action(); } catch (AiOperationException error) when (error.Code == code) { return; }
    throw new Exception("Expected " + code);
}
static async Task Fixture() {
    var protocol = Environment.GetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL") ?? "codex";
    var trace = Environment.GetEnvironmentVariable("TERMLINK_AI_FIXTURE_TRACE");
    if (trace != null) File.AppendAllText(trace, "pid:" + Environment.ProcessId + "\n");
    var configurationTrace = Environment.GetEnvironmentVariable("TERMLINK_AI_CONFIGURATION_TRACE");
    if (configurationTrace != null) File.AppendAllText(configurationTrace, "args:" + string.Join(" ", Environment.GetCommandLineArgs()) + "\n");
    var configModel = "model-a"; var configProvider = "provider-a"; var configMode = "build"; var configEffort = "low"; var configRevision = 1;
    void Write(object value) => Console.WriteLine(AiSessionManager.Encode(value));
    void Complete() {
        if (protocol == "claude") Write(new { type = "result", is_error = false });
        else if (protocol == "zcode") Write(new { method = "session/event", @params = new { type = "turn.completed", payload = new { } } });
        else Write(new { method = "turn/completed", @params = new { turn = new { id = "turn" } } });
    }
    string line;
    while ((line = await Console.In.ReadLineAsync()) != null) {
        using var json = JsonDocument.Parse(line); var root = json.RootElement;
        if (ConfigurationValidation.Fixture(root, protocol, ref configModel, ref configProvider, ref configMode, ref configEffort, ref configRevision)) continue;
        var method = AiAgentSession.S(root, "method"); var type = AiAgentSession.S(root, "type"); var id = AiAgentSession.P(root, "id");
        if (trace != null) File.AppendAllText(trace, (method.Length > 0 ? method : type) + "\n");
        if (method == "model/list") {
            var mode = Environment.GetEnvironmentVariable("TERMLINK_AI_FIXTURE_MODEL_MODE");
            if (mode == "hang") { await Task.Delay(30000); continue; }
            if (mode == "malformed") { Write(new { id, result = new { data = "wrong" } }); continue; }
            var next = AiAgentSession.S(AiAgentSession.P(root, "params"), "cursor").Length == 0;
            Write(new { id, result = new { data = new[] {
                new { id = "catalog-id", model = next ? "model-a" : "model-b", displayName = next ? "Model A" : "Model B", hidden = false, isDefault = next },
                new { id = "duplicate", model = "model-a", displayName = "duplicate", hidden = false, isDefault = false },
                new { id = "hidden", model = "hidden-model", displayName = "hidden", hidden = true, isDefault = false },
                new { id = "invalid", model = "bad\nmodel", displayName = "invalid", hidden = false, isDefault = false }
            }, nextCursor = next || mode == "cycle" ? "page2" : null, apiKey = "must-never-be-forwarded" } });
            continue;
        }
        if (type == "control_request") {
            if (AiAgentSession.S(AiAgentSession.P(root, "request"), "subtype") == "set_model")
                Write(new { type = "control_response", response = new { subtype = "success", request_id = AiAgentSession.S(root, "request_id") } });
            else Write(new { type = "control_response", response = new { subtype = "success", request_id = AiAgentSession.S(root, "request_id"), response = new { models = new[] {
                new { value = "model-a", displayName = "Model A" }, new { value = "model-b", displayName = "Model B" }, new { value = "model-a", displayName = "Duplicate" }
            }, account = new { apiKey = "must-never-be-forwarded" } } } });
            continue;
        }
        if (type == "control_response" || method.Length == 0 && id.ValueKind != JsonValueKind.Undefined) {
            if (protocol == "zcode" && id.ToString() == "question-1") {
                var answer = root.GetProperty("result").GetProperty("content").GetProperty("answers").GetProperty("Continue?")[0].GetString();
                Require(answer == "yes_value", "ZCode option label must map to its protocol value");
            }
            Complete(); continue;
        }
        if (method == "initialized") continue;
        if (method == "turn/start" || method == "session/send" || type == "user") {
            var parameters = AiAgentSession.P(root, "params");
            var content = type == "user" ? AiAgentSession.P(AiAgentSession.P(root, "message"), "content") : method == "turn/start" ? AiAgentSession.P(parameters, "input") : AiAgentSession.P(parameters, "content");
            var prompt = content.ValueKind == JsonValueKind.String ? content.GetString() : content.ValueKind == JsonValueKind.Array ? string.Join("\n", content.EnumerateArray().Select(c => AiAgentSession.S(c, "text"))) : "";
            if (type != "user") Write(new { id, result = new { turn = new { id = "turn" }, accepted = true } });
            if (prompt.Contains("stream-ui") && protocol == "codex") {
                for (var n = 1; n <= 25; n++) {
                    Write(new { method = "item/agentMessage/delta", @params = new { itemId = "stream-preview", delta = $"\n\n流式第 {n} 段：新内容继续到达，向上阅读应保持位置。" } });
                    await Task.Delay(700);
                }
                Complete();
            } else if (prompt.Contains("web-ui")) {
                var html = "<!doctype html><html><head><style>body{font-family:sans-serif;padding:20px;background:#eef3ff}h1{color:#405bba}</style></head><body><h1>TermLink Preview</h1><p>Static HTML from Windows workspace.</p><script>parent.hacked=1</script></body></html>";
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "report.html"), html);
                File.WriteAllBytes(Path.Combine(Environment.CurrentDirectory, "pixel.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1cAAAAASUVORK5CYII="));
                Write(new { method = "turn/plan/updated", @params = new { plan = new[] { new { step = "预览与消息桥联合测试", status = "in_progress" } } } });
                Write(new { method = "item/reasoning/summaryTextDelta", @params = new { itemId = "web-thought", summaryIndex = 0, delta = "检查工作目录边界，生成静态 HTML 并验证预览。" } });
                Write(new { method = "turn/diff/updated", @params = new { diff = "diff --git a/report.html b/report.html\n--- /dev/null\n+++ b/report.html\n@@ -0,0 +1 @@\n+<h1>TermLink Preview</h1>" } });
                Write(new { method = "thread/tokenUsage/updated", @params = new { tokenUsage = new { last = new { totalTokens = 2000 }, total = new { inputTokens = 1500, outputTokens = 500, totalTokens = 2000 }, modelContextWindow = 128000 } } });
                for (var n = 1; n <= 6; n++) {
                    Write(new { method = "item/agentMessage/delta", @params = new { itemId = "web-reply", delta = n == 1 ? "## Web 会话联合测试\n\n" : $"步骤 {n} 已完成。\n\n" } });
                    await Task.Delay(350);
                }
                Write(new { method = "item/agentMessage/delta", @params = new { itemId = "web-reply", delta = "[查看报告](report.html)\n\n```html\n" + html + "\n```" } });
                Write(new { method = "turn/plan/updated", @params = new { plan = new[] { new { step = "预览与消息桥联合测试", status = "completed" } } } });
                Complete();
            } else if (prompt.Contains("tools-ui")) {
                for (var i = 0; i < 3; i++) {
                    var toolId = "preview-" + i;
                    if (protocol == "claude") {
                        Write(new { type = "assistant", message = new { id = "tools", content = new[] { new { type = "tool_use", id = toolId, name = "Bash", input = new { command = "echo safe-preview" } } } } });
                        if (i < 2) Write(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = toolId, is_error = i == 1, content = "safe-preview" } } } });
                    } else if (protocol == "zcode") Write(new { method = "session/event", @params = new { type = "tool.updated", payload = new {
                        toolCallId = toolId, toolName = "Bash", input = new { command = "echo safe-preview" }, status = i == 0 ? "completed" : i == 1 ? "failed" : "unknown" } } });
                    else {
                        Write(new { method = "item/started", @params = new { item = new { id = toolId, type = "commandExecution", command = "echo safe-preview", status = "inProgress" } } });
                        if (i < 2) Write(new { method = "item/completed", @params = new { item = new { id = toolId, type = "commandExecution", command = "echo safe-preview", status = i == 0 ? "completed" : "failed" } } });
                    }
                }
                var text = "## 会话页验证\n\n工具详情保持可读，**失败状态**单独显示。\n\n- 普通列表\n- 行内 `code`\n\n| 项目 | 结果 |\n| --- | --- |\n| 消息 | 通过 |\n| 工具 | 可展开 |\n\n```kotlin\nval greeting = \"Hello TermLink\"\n```\n\n[项目链接](https://github.com)\n\n" +
                    string.Join("\n\n", Enumerable.Range(1, 18).Select(n => $"第 {n} 段阅读定位验证：流式追加内容时，向上浏览保持原位置。"));
                if (protocol == "claude") Write(new { type = "assistant", message = new { id = "preview-reply", content = new[] { new { type = "text", text } } } });
                else if (protocol == "zcode") Write(new { method = "session/event", @params = new { type = "model.streaming", payload = new { kind = "text_delta", assistantMessageId = "preview-reply", delta = text } } });
                else Write(new { method = "item/completed", @params = new { item = new { id = "preview-reply", type = "agentMessage", text } } });
                Complete();
            } else if (prompt.Contains("question")) {
                var questions = new[] { new { id = "q1", question = "Continue?", header = "Choice", options = new[] { new { label = "Yes", value = "yes_value", description = "Continue" } }, multiSelect = false } };
                if (protocol == "claude") Write(new { type = "control_request", request_id = "question-1", request = new { subtype = "can_use_tool", tool_name = "AskUserQuestion", input = new { questions } } });
                else Write(new { id = "question-1", method = protocol == "zcode" ? "interaction/requestUserInput" : "item/tool/requestUserInput", @params = new { questions } });
            } else if (prompt.Contains("approval")) {
                if (protocol == "claude") Write(new { type = "control_request", request_id = "approval-1", request = new { subtype = "can_use_tool", tool_name = "Bash", input = new { command = "echo fixture" } } });
                else Write(new { id = "approval-1", method = protocol == "zcode" ? "interaction/requestPermission" : "item/commandExecution/requestApproval", @params = new { command = "echo fixture" } });
            } else {
                if (protocol == "claude") Write(new { type = "assistant", message = new { id = "reply", content = new[] { new { type = "text", text = "fixture response" } } } });
                else if (protocol == "zcode") Write(new { method = "session/event", @params = new { type = "model.streaming", payload = new { kind = "text_delta", assistantMessageId = "reply", delta = "fixture response" } } });
                else Write(new { method = "item/completed", @params = new { item = new { id = "reply", type = "agentMessage", text = "fixture response" } } });
                Complete();
            }
        } else if (id.ValueKind != JsonValueKind.Undefined) Write(new { id, result = new { sessionId = "session", thread = new { id = "thread" } } });
    }
}
