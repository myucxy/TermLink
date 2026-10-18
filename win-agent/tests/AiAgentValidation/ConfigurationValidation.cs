using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class ConfigurationValidation
{
    internal static async Task Real(string directory, bool send)
    {
        foreach (var installation in await AiAgentDiscovery.DetectAsync()) {
            if (installation.Status != "available" || installation.Protocol is not ("codex" or "claude" or "zcode")) continue;
            var caller = Guid.NewGuid();
            Console.WriteLine($"NATIVE {installation.Id} {installation.Version}");
            try {
                using var session = new AiAgentSession(installation, directory, caller);
                await session.StartAsync();
                var snap = Snap(session, caller);
                Console.WriteLine($"  start={snap.GetProperty("state")} effortStatus={snap.GetProperty("reasoning").GetProperty("status")} efforts={snap.GetProperty("reasoning").GetProperty("options").GetArrayLength()}");
                foreach (var mode in snap.GetProperty("capabilities").GetProperty("configuration").GetProperty("permissionModes").EnumerateArray()) {
                    var name = mode.GetProperty("id").GetString();
                    await session.ApplyAsync(caller, Configure(Snap(session, caller), mode: name, confirmed: name));
                    Check(Snap(session, caller).GetProperty("permissionMode").GetString() == name, "Mode ACK mismatch");
                    Console.WriteLine("  configuration acknowledged " + name + (installation.Protocol == "codex" ? " (next turn)" : ""));
                    if (send && installation.Protocol == "codex" && name is "auto-review" or "full-access") {
                        await session.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Reply exactly OK. Do not use tools, read files or run commands."));
                        for (var n = 0; n < 900 && Snap(session, caller).GetProperty("state").GetString() == "running"; n++) await Task.Delay(100);
                        Check(Snap(session, caller).GetProperty("state").GetString() == "idle" && Snap(session, caller).GetProperty("error").GetString() == "", "Native mode turn failed");
                        Console.WriteLine("  native turn accepted " + name);
                    }
                }
                await session.ApplyAsync(caller, Configure(Snap(session, caller), mode: "ask"));
                snap = Snap(session, caller);
                var option = snap.GetProperty("reasoning").GetProperty("options").EnumerateArray().FirstOrDefault();
                if (option.ValueKind == JsonValueKind.Object) {
                    var effort = option.GetProperty("value").GetString();
                    if (installation.Protocol == "claude") {
                        using var fresh = new AiAgentSession(installation, directory, caller, effort: effort);
                        await fresh.StartAsync();
                        Check(Snap(fresh, caller).GetProperty("reasoningEffort").GetString() == effort, "Claude creation effort failed");
                        Console.WriteLine("  new session effort accepted " + effort);
                    } else {
                        await session.ApplyAsync(caller, Configure(snap, effort: effort));
                        Console.WriteLine("  reasoning acknowledged " + effort);
                    }
                }
                if (send && installation.Protocol is "codex" or "claude") {
                    await session.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Reply exactly TERMLINK_CONFIGURATION_OK. Do not use any tools, read files, or execute commands."));
                    for (var i = 0; i < 1200 && Snap(session, caller).GetProperty("state").GetString() == "running"; i++) await Task.Delay(100);
                    var final = Snap(session, caller);
                    Check(final.GetProperty("state").GetString() == "idle" && final.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "assistant" && m.GetProperty("text").GetString().Contains("TERMLINK_CONFIGURATION_OK")), "Native reply missing");
                    Console.WriteLine("  native reply marker verified");
                }
            } catch (Exception e) { Console.WriteLine("  FAILED " + e.GetType().Name + " " + e.Message); Environment.ExitCode = 1; }
        }
    }
    private static JsonElement Snap(AiAgentSession s, Guid caller) => JsonSerializer.SerializeToElement(s.Snapshot(caller, true), AiSessionManager.Json);
    private static void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }
    private static async Task Reject(Func<Task> action, string code) {
        try { await action(); } catch (AiOperationException e) when (e.Code == code) { return; }
        throw new Exception("Expected " + code);
    }
    private static AiAgentInstallation Installation(string protocol) => new(protocol, protocol, Environment.ProcessPath, "",
        protocol == "codex" ? "0.160.0" : protocol == "claude" ? "2.1.116" : "0.16.9", protocol, "available");
    private static AiSessionManager.Request Configure(JsonElement s, string model = null, string provider = null, string mode = null, string effort = null, string confirmed = "") =>
        new("configure", ControlVersion: s.GetProperty("controlVersion").GetInt64(), ExpectedSettingsVersion: s.GetProperty("settingsVersion").GetInt64(),
            ModelId: model ?? s.GetProperty("modelId").GetString(), ProviderId: provider ?? s.GetProperty("providerId").GetString(),
            PermissionMode: mode ?? s.GetProperty("permissionMode").GetString(), ReasoningEffort: effort, ConfirmedPermissionMode: confirmed);
    internal static async Task Run(string directory)
    {
        var trace = Path.Combine(directory, "configuration-frames.jsonl");
        Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_CONFIGURATION", "1");
        Environment.SetEnvironmentVariable("TERMLINK_AI_CONFIGURATION_TRACE", trace);
        var caller = Guid.NewGuid();
        try {
            foreach (var protocol in new[] { "codex", "claude", "zcode" }) {
                Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", protocol);
                var installation = Installation(protocol);
                var caps = AiAgentSession.ConfigurationFor(installation);
                Check(caps.ModelListing == (protocol != "zcode"), "ZCode must not inherit model listing");
                Check(AiAgentSession.ConfigurationFor(installation with { Strategy = "desktop_remote" }).PermissionModes.Length == 0, "Web settings are web-owned");
                await Reject(() => { AiAgentSession.ValidatePermissionSelection(installation, "full-access", ""); return Task.CompletedTask; }, "PERMISSION_CONFIRMATION_REQUIRED");
                using var s = new AiAgentSession(installation, directory, caller, "model-a", protocol == "zcode" ? "provider-a" : "", effort: "high");
                await s.StartAsync();
                var snapshot = Snap(s, caller);
                Check(snapshot.GetProperty("reasoningEffort").GetString() == "high", "Initial effort lost: " + protocol);
                Check(snapshot.GetProperty("reasoning").GetProperty("options").GetArrayLength() == 2, "Effort dedup/invalid filtering: " + protocol);
                var valid = Configure(snapshot, effort: "low");
                await Reject(() => s.ApplyAsync(Guid.NewGuid(), valid), "CONTROL_CHANGED");
                await Reject(() => s.ApplyAsync(caller, valid with { ExpectedSettingsVersion = 99 }), "SETTINGS_CHANGED");
                await Reject(() => s.ApplyAsync(caller, Configure(snapshot, mode: "full-access")), "PERMISSION_CONFIRMATION_REQUIRED");
                await Reject(() => s.ApplyAsync(caller, Configure(snapshot, mode: "full-access", model: "model-b", confirmed: "full-access")), "ONE_SETTING_AT_A_TIME");
                if (protocol == "claude") {
                    await Reject(() => s.ApplyAsync(caller, valid), "REASONING_REQUIRES_NEW_SESSION");
                    await Reject(() => s.ApplyAsync(caller, Configure(snapshot, model: "model-b")), "REASONING_REQUIRES_NEW_SESSION");
                } else {
                    await Reject(() => s.ApplyAsync(caller, Configure(snapshot, effort: "made-up")), "INVALID_REASONING_EFFORT");
                    await s.ApplyAsync(caller, valid);
                    Check(Snap(s, caller).GetProperty("reasoningEffort").GetString() == "low", "Changed effort not acknowledged");
                    await s.ApplyAsync(caller, Configure(Snap(s, caller), model: "model-b"));
                    snapshot = Snap(s, caller);
                    Check(snapshot.GetProperty("reasoning").GetProperty("options")[0].GetProperty("value").GetString() == "ultra", "Per-model effort not updated");
                    Check(snapshot.GetProperty("reasoningEffort").GetString() == (protocol == "codex" ? "" : "ultra"), "Model fallback retained incompatible effort");
                    if (protocol == "codex") Check(snapshot.GetProperty("settingsNotice").GetString().Length > 0, "Fallback notice absent");
                    if (protocol == "zcode") {
                        await s.ApplyAsync(caller, Configure(snapshot, model: "model-a", provider: "provider-b"));
                        Check(Snap(s, caller).GetProperty("reasoning").GetProperty("options")[0].GetProperty("value").GetString() == "provider-effort", "Provider-specific effort lost");
                    }
                }
                await s.ApplyAsync(caller, Configure(Snap(s, caller), mode: "full-access", confirmed: "full-access"));
                var record = s.ArchiveRecord();
                using var restored = AiAgentSession.Restore(installation, record, null);
                Check(Snap(restored, caller).GetProperty("permissionMode").GetString() == "full-access", "Historical grant was reset");
                using var downgraded = AiAgentSession.Restore(installation with { Version = "1" }, record, null);
                await Reject(downgraded.StartAsync, "PERMISSION_MODE_UNAVAILABLE");
                await s.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "config test"));
                await Idle(s, caller);
                if (protocol == "codex") {
                    await s.ApplyAsync(caller, Configure(Snap(s, caller), mode: "auto-review", confirmed: "auto-review"));
                    await s.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "config test"));
                    await Idle(s, caller);
                }
                Console.WriteLine("PASS configuration " + protocol + ": model/provider effort, CAS, control, explicit grant, restore, native mappings");
            }
            var frames = File.ReadAllLines(trace).Where(l => l.StartsWith('{')).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
            var turns = frames.Where(f => AiAgentSession.S(f, "method") == "turn/start").Select(f => f.GetProperty("params")).ToArray();
            Check(turns.Any(p => p.GetProperty("effort").GetString() == "ultra" && p.GetProperty("approvalPolicy").GetString() == "never" && p.GetProperty("sandboxPolicy").GetProperty("type").GetString() == "dangerFullAccess"), "Codex default effort/full-access mapping");
            Check(turns.Any(p => p.GetProperty("approvalsReviewer").GetString() == "auto_review" && p.GetProperty("approvalPolicy").GetString() == "on-request" && p.GetProperty("sandboxPolicy").GetProperty("type").GetString() == "workspaceWrite"), "Auto-review incorrectly mapped to bypass");
            Check(frames.Any(f => AiAgentSession.S(AiAgentSession.P(f, "request"), "mode") == "bypassPermissions"), "Claude bypass mapping");
            Check(File.ReadAllText(trace).Contains("--effort high"), "Claude initial effort argument missing");
            Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", "codex");
            Environment.SetEnvironmentVariable("TERMLINK_AI_REQUIREMENTS", "restricted");
            using (var managed = new AiAgentSession(Installation("codex"), directory, caller)) {
                await managed.StartAsync();
                var snap = Snap(managed, caller);
                Check(snap.GetProperty("capabilities").GetProperty("configuration").GetProperty("permissionModes").GetArrayLength() == 1, "Managed restrictions not filtered");
                await Reject(() => managed.ApplyAsync(caller, Configure(snap, mode: "full-access", confirmed: "full-access")), "PERMISSION_MODE_UNAVAILABLE");
            }
            Environment.SetEnvironmentVariable("TERMLINK_AI_REQUIREMENTS", null);
            Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", "zcode");
            Environment.SetEnvironmentVariable("TERMLINK_AI_BAD_SETTINGS", "1");
            using (var malformed = new AiAgentSession(Installation("zcode"), directory, caller)) {
                await malformed.StartAsync();
                await Reject(() => malformed.ApplyAsync(caller, Configure(Snap(malformed, caller), mode: "edit", confirmed: "edit")), "SETTINGS_OUTCOME_UNKNOWN");
                Check(!malformed.IsAlive, "Unknown native outcome must not remain writable");
            }
            Console.WriteLine("PASS native parameter assertions, managed restrictions, malformed ACK fails closed");
        } finally {
            foreach (var name in new[] { "TERMLINK_AI_FIXTURE_CONFIGURATION", "TERMLINK_AI_CONFIGURATION_TRACE", "TERMLINK_AI_REQUIREMENTS", "TERMLINK_AI_BAD_SETTINGS" }) Environment.SetEnvironmentVariable(name, null);
        }
    }
    private static async Task Idle(AiAgentSession s, Guid caller) {
        for (var n = 0; n < 500; n++) { if (Snap(s, caller).GetProperty("state").GetString() == "idle") return; await Task.Delay(10); }
        throw new TimeoutException();
    }
    internal static bool Fixture(JsonElement frame, string protocol, ref string model, ref string provider, ref string mode, ref string effort, ref int revision)
    {
        if (Environment.GetEnvironmentVariable("TERMLINK_AI_FIXTURE_CONFIGURATION") != "1") return false;
        var trace = Environment.GetEnvironmentVariable("TERMLINK_AI_CONFIGURATION_TRACE");
        if (trace != null) File.AppendAllText(trace, frame.GetRawText() + "\n");
        var method = AiAgentSession.S(frame, "method"); var p = AiAgentSession.P(frame, "params"); var id = AiAgentSession.P(frame, "id");
        void Reply(object value) => Console.WriteLine(AiSessionManager.Encode(new { id, result = value }));
        if (method == "model/list") {
            Reply(new { data = new[] { new { model = "model-a", displayName = "Model A", isDefault = true, supportedReasoningEfforts = new[] { new { reasoningEffort = "low", description = "Fast" }, new { reasoningEffort = "high", description = "Careful" }, new { reasoningEffort = "high", description = "duplicate" }, new { reasoningEffort = "bad\nvalue", description = "bad" } }, defaultReasoningEffort = "low" },
                new { model = "model-b", displayName = "Model B", isDefault = false, supportedReasoningEfforts = new[] { new { reasoningEffort = "ultra", description = "Native custom value" } }, defaultReasoningEffort = "ultra" } }, nextCursor = (string)null }); return true;
        }
        if (AiAgentSession.S(frame, "type") == "control_request" && AiAgentSession.S(AiAgentSession.P(frame, "request"), "subtype") == "initialize") {
            Console.WriteLine(AiSessionManager.Encode(new { type = "control_response", response = new { subtype = "success", request_id = AiAgentSession.S(frame, "request_id"), response = new { models = new[] {
                new { value = "model-a", displayName = "Model A", supportsEffort = true, supportedEffortLevels = new[] { "low", "high", "high", "bad\nvalue" } },
                new { value = "model-b", displayName = "Model B", supportsEffort = false, supportedEffortLevels = Array.Empty<string>() } } } } })); return true;
        }
        if (method == "configRequirements/read") {
            Reply(new { requirements = Environment.GetEnvironmentVariable("TERMLINK_AI_REQUIREMENTS") == "restricted" ? (object)new { allowedApprovalPolicies = new[] { "on-request" }, allowedSandboxModes = new[] { "workspace-write" }, allowedApprovalsReviewers = new[] { "user" } } : null }); return true;
        }
        if (method == "thread/list") { Reply(new { data = Array.Empty<object>(), nextCursor = (string)null }); return true; }
        if (method == "thread/turns/list") { Reply(new { data = Array.Empty<object>(), nextCursor = (string)null }); return true; }
        if (method == "thread/resume" && Environment.GetEnvironmentVariable("TERMLINK_AI_NATIVE_FIXTURE") == "1") {
            Reply(new { thread = new { id = "native-thread" }, model = "model-a", approvalPolicy = new { granular = new { sandbox_approval = true } },
                sandbox = new { type = "workspaceWrite", writableRoots = new[] { "D:/native-root" }, networkAccess = true }, approvalsReviewer = "auto_review", reasoningEffort = "native-custom-effort" }); return true;
        }
        if (method is "thread/start" or "thread/resume") { Reply(new { thread = new { id = "thread" }, model = "model-a" }); return true; }
        if (protocol == "zcode" && (method == "session/read" || method.StartsWith("session/set"))) {
            if (method.StartsWith("session/set")) {
                Check(p.GetProperty("expectedRevision").GetInt32() == revision, "ZCode revision mismatch");
                if (Environment.GetEnvironmentVariable("TERMLINK_AI_BAD_SETTINGS") == "1") { Reply(new { }); return true; }
                revision++;
                if (method == "session/setModel") {
                    Check(!p.GetProperty("persistAsWorkspaceLastUsed").GetBoolean(), "Mutated global model preference");
                    model = AiAgentSession.S(AiAgentSession.P(p, "model"), "modelId"); provider = AiAgentSession.S(AiAgentSession.P(p, "model"), "providerId");
                    effort = provider == "provider-b" ? "provider-effort" : model == "model-b" ? "ultra" : "low";
                }
                if (method == "session/setThoughtLevel") effort = AiAgentSession.S(p, "thoughtLevel");
                if (method == "session/setMode") mode = AiAgentSession.S(p, "mode");
            }
            var levels = provider == "provider-b" ? new[] { "provider-effort" } : model == "model-b" ? new[] { "ultra" } : new[] { "low", "high" };
            Reply(new { runtime = new { stateRevision = revision }, settings = new { model = new { current = new { modelId = model, providerId = provider } }, mode = new { current = mode },
                thoughtLevel = new { enabled = true, current = effort, defaultLevel = levels[0], available = levels.Select(v => new { value = v, description = v }) } } }); return true;
        }
        return false;
    }
}
