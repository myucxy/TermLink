// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using TermLink.Agent;

internal static class AiCapabilitiesValidation
{
    internal static async Task RunAsync()
    {
        EveryProviderHasHonestControls();
        ClaudeInitializationIsPublicAndBounded();
        AcpUpdatesReplaceCommands();
        ZcodeProjectsOnlyDeclaredMethods();
        await CodexReadsAreExplicitAndScoped();
        await CodexCompatibilityDoesNotHideFailures();
        await CodexInventoriesAreBounded();
        Console.WriteLine("PASS AI capability catalog: five agents, explicit reads, missing-vs-unsupported, safe metadata, bounded pagination and error propagation");
    }

    private static void EveryProviderHasHonestControls()
    {
        var controls = Json(new { history = true, artifacts = false, attachments = false, fileReferences = false,
            nativeImages = true, configure = true, resume = false, fork = false, steer = false });
        foreach (var (agent, protocol) in new[] { ("codex", "codex"), ("claude", "claude"), ("zcode", "zcode"), ("grok", "acp"), ("opencode", "acp") }) {
            var result = AiCapabilityProjection.Catalog(agent, protocol, controls, new());
            Require(result.Agent == agent && result.Protocol == protocol, "Provider identity was lost.");
            foreach (var name in new[] { "stream", "tools", "history", "configure", "nativeImages" }) Expect(result, name, "available");
            foreach (var name in new[] { "artifacts", "attachments", "fileReferences", "resume", "fork", "steer" }) Expect(result, name, "unsupported");
            foreach (var name in new[] { "skills", "mcp", "commands", "rateLimits", "toolCatalog" }) Expect(result, name, "not-reported");
            Require(result.Skills.Length == 0 && result.Tools.Length == 0 && !result.Truncated, "Unreported inventory was invented.");
            Bound(result);
            var encoded = JsonSerializer.SerializeToElement(result, AiSessionManager.Json);
            Require(encoded.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "agent", "commands", "features", "protocol", "skills", "tools", "truncated" }), "Catalog wire shape changed.");
        }
        var unknown = AiCapabilityProjection.Catalog("future", "future", default, new());
        Expect(unknown, "stream", "not-reported"); Expect(unknown, "history", "not-reported");
        var fallback = AiCapabilityProjection.Catalog("claude", "claude", controls, new(), new[] { "help", "help", "bad\ncommand" });
        Require(fallback.Commands.Select(c => c.Name).SequenceEqual(new[] { "help" }), "Legacy slash command fallback was not sanitized.");
        Expect(fallback, "commands", "available");
    }

    private static void ClaudeInitializationIsPublicAndBounded()
    {
        var first = AiCapabilityProjection.Capture("claude", Json(new {
            commands = new[] { new { name = "review", description = "Review staged changes" }, new { name = "help", description = "Show commands" } },
            account = new { email = "fixture-private@example.invalid", token = "fixture-private-token" },
            models = new[] { new { name = "not-a-command", apiKey = "fixture-private-key" } }
        }));
        var next = AiCapabilityProjection.Capture("claude", Json(new {
            type = "system", subtype = "init", slash_commands = new[] { "review", "help" },
            skills = new[] { "test-skill", "test-skill" }, tools = new[] { "Read", "Bash", "Read" },
            mcp_servers = new[] {
                new { name = "private-server-name", status = "connected", config = "fixture-private-config" },
                new { name = "private-server-name-2", status = "needs-auth", config = "fixture-private-config" },
                new { name = "private-server-name-3", status = "failed: fixture-private-error", config = "fixture-private-config" }
            }, apiKeySource = "fixture-private-source", cwd = "/Users/private-fixture/work"
        }), first);
        var result = AiCapabilityProjection.Catalog("claude", "claude", default, next);
        Require(result.Commands.Single(c => c.Name == "review").Description == "Review staged changes", "System init lost initialize command descriptions.");
        Require(result.Tools.Length == 2 && result.Skills.Length == 1, "Public tool/skill names were not deduplicated.");
        Expect(result, "skills", "available"); Expect(result, "toolCatalog", "available"); Expect(result, "mcp", "available");
        Require(result.Features.Single(f => f.Id == "mcp").Detail.Contains("待授权 1", StringComparison.Ordinal), "MCP needs-auth status was lost.");
        var wire = AiSessionManager.Encode(result);
        Require(!wire.Contains("fixture-private", StringComparison.Ordinal) && !wire.Contains("private-server-name", StringComparison.Ordinal), "Private metadata escaped the public projection.");
        var empty = AiCapabilityProjection.Capture("claude", Json(new { commands = Array.Empty<object>() }), next);
        var noCommands = AiCapabilityProjection.Catalog("claude", "claude", default, empty, new[] { "stale-command" });
        Require(noCommands.Commands.Length == 0 && noCommands.Tools.Length == 2, "An empty list resurrected stale commands or cleared unrelated metadata.");
        var oversized = AiCapabilityProjection.Capture("claude", Json(new { commands = Enumerable.Range(0, 101).Select(i => new {
            name = "command-" + i, description = "password=fixture-sensitive-description " + new string('x', 300)
        }).ToArray() }));
        var bounded = AiCapabilityProjection.Catalog("claude", "claude", default, oversized);
        Require(bounded.Commands.Length == 100 && bounded.Truncated, "Command cap was not reported.");
        Require(!AiSessionManager.Encode(bounded).Contains("fixture-sensitive-description", StringComparison.Ordinal), "Secret in a description was retained.");
        Bound(bounded); Bound(result);
    }

    private static void AcpUpdatesReplaceCommands()
    {
        var capture = AiCapabilityProjection.Capture("acp", Json(new {
            protocolVersion = 1,
            agentCapabilities = new { loadSession = true, promptCapabilities = new { image = false }, mcpCapabilities = new { http = false } },
            _meta = new { availableCommands = new[] { new { name = "old", description = "Original" } } },
            authMethods = new[] { new { id = "private-auth-config" } }
        }));
        var result = AiCapabilityProjection.Catalog("grok", "acp", default, capture);
        Expect(result, "nativeLoad", "available"); Expect(result, "reportedImages", "unsupported");
        Expect(result, "mcpHttp", "unsupported"); Expect(result, "mcpSse", "not-reported"); Expect(result, "mcp", "not-reported");
        var update = Json(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = "private-session-id", update = new {
            sessionUpdate = "available_commands_update", availableCommands = new[] { new { name = "plan", description = "Plan changes", input = new { hint = "private-hint" } } }
        } } });
        capture = AiCapabilityProjection.Capture("acp", update, capture);
        result = AiCapabilityProjection.Catalog("opencode", "acp", default, capture);
        Require(result.Commands.Length == 1 && result.Commands[0].Name == "plan" && result.Commands[0].Description == "Plan changes", "ACP update did not replace the full command list.");
        Require(!AiSessionManager.Encode(result).Contains("private-", StringComparison.Ordinal), "ACP private fields escaped.");
        capture = AiCapabilityProjection.Capture("acp", Json(new { sessionUpdate = "available_commands_update", availableCommands = Array.Empty<object>() }), capture);
        result = AiCapabilityProjection.Catalog("opencode", "acp", default, capture, new[] { "old", "plan" });
        Require(result.Commands.Length == 0, "ACP empty update was ignored.");
        Expect(result, "commands", "available"); Expect(result, "nativeLoad", "available");
        Bound(result);
    }

    private static void ZcodeProjectsOnlyDeclaredMethods()
    {
        var capture = AiCapabilityProjection.Capture("zcode", Json(new {
            methods = new[] { "session/read", "session/subscribe", "undocumented/skills", "private-runtime-method" },
            headers = new { Authorization = "fixture-zcode-token" },
            skills = new[] { new { name = "not-a-documented-skill" } }, mcp = true
        }));
        var result = AiCapabilityProjection.Catalog("zcode", "zcode", default, capture);
        Expect(result, "runtimeSessionRead", "available"); Expect(result, "runtimeSessionStream", "available");
        Expect(result, "runtimeSessionStop", "not-reported"); Expect(result, "skills", "not-reported"); Expect(result, "mcp", "not-reported");
        Require(!AiSessionManager.Encode(result).Contains("private-", StringComparison.Ordinal) && result.Skills.Length == 0, "ZCode projected undocumented capability fields.");
        var opaque = AiCapabilityProjection.Capture("zcode", Json(new { capabilities = new { arbitrary = true } }));
        Expect(AiCapabilityProjection.Catalog("zcode", "zcode", default, opaque), "runtimeSessionRead", "not-reported");
        Bound(result);
    }

    private static async Task CodexReadsAreExplicitAndScoped()
    {
        const string cwd = "/fixture/current-workspace";
        var calls = new List<string>();
        var optional = await AiCapabilityProjection.ReadCodexAsync(cwd, (method, parameters) => {
            calls.Add(method); var args = Json(parameters);
            switch (method) {
                case "skills/list":
                    Require(args.EnumerateObject().Count() == 1 && args.GetProperty("cwds")[0].GetString() == cwd, "Skills query leaked roots or enabled reload.");
                    return Task.FromResult(Json(new { data = new[] {
                        new { cwd, skills = new[] { new { name = "test-skill", description = "Run tests", enabled = true, path = "fixture-private-path" },
                            new { name = "disabled", description = "Disabled skill", enabled = false, path = "fixture-private-path" } }, errors = new object[] { new { message = "fixture-private-error" } } },
                        new { cwd = "/fixture/another-workspace", skills = new[] { new { name = "foreign-skill", description = "Foreign", enabled = true, path = "fixture-private-path" } }, errors = Array.Empty<object>() }
                    } }));
                case "mcpServerStatus/list":
                    Require(args.EnumerateObject().Count() == 1 && args.GetProperty("limit").GetInt32() == 100, "MCP query was not bounded or added newer-only fields.");
                    return Task.FromResult(Json(new { data = new[] {
                        new { name = "repo", authStatus = "oAuth", tools = new { read = new { description = "fixture-private-schema", inputSchema = "fixture-private-config" } } },
                        new { name = "pending", authStatus = "notLoggedIn", tools = new { read = new { description = "fixture-private-schema", inputSchema = "fixture-private-config" } } }
                    }, nextCursor = "fixture-private-cursor" }));
                case "account/rateLimits/read":
                    Require(args.EnumerateObject().Count() == 0, "Rate limit read unexpectedly accepts input.");
                    return Task.FromResult(Json(new { rateLimits = new { primary = new { usedPercent = 25.5, resetsAt = 9999999999L }, secondary = new { usedPercent = 0 },
                        planType = "fixture-private-plan", credits = "fixture-private-credits" }, account = "fixture-private-account", token = "fixture-private-token" }));
                default: throw new InvalidOperationException("Capability read attempted an unapproved RPC.");
            }
        });
        Require(calls.Order().SequenceEqual(new[] { "account/rateLimits/read", "mcpServerStatus/list", "skills/list" }), "Capability reads exceeded their explicit RPC allowlist.");
        var result = AiCapabilityProjection.Catalog("codex", "codex", default, new(), codex: optional);
        Require(result.Skills.Length == 1 && result.Skills[0].Name == "test-skill", "Skills escaped the selected workspace or disabled filter.");
        Require(result.Tools.Single(t => t.Name == "repo/read").Status == "available" && result.Tools.Single(t => t.Name == "pending/read").Status == "not-reported", "MCP login status was presented as working tool access.");
        Require(result.Truncated, "Partial inventory or next page was hidden.");
        Expect(result, "rateLimits", "available");
        Require(result.Features.Single(f => f.Id == "rateLimits").Detail.Contains("25.5%", StringComparison.Ordinal), "Safe rate limit percentage was lost.");
        var wire = AiSessionManager.Encode(result);
        Require(!wire.Contains("fixture-private", StringComparison.Ordinal) && !wire.Contains("foreign-skill", StringComparison.Ordinal), "Codex private metadata escaped.");
        Bound(result);
    }

    private static async Task CodexCompatibilityDoesNotHideFailures()
    {
        foreach (var code in new[] { "AGENT_METHOD_NOT_FOUND", "AGENT_INVALID_PARAMS" }) {
            var optional = await AiCapabilityProjection.ReadCodexAsync("/fixture", (_, _) => Task.FromException<JsonElement>(new AiOperationException(code)));
            var result = AiCapabilityProjection.Catalog("codex", "codex", default, new(), codex: optional);
            foreach (var id in new[] { "skills", "mcp", "toolCatalog", "rateLimits" }) Expect(result, id, "unsupported");
            Expect(result, "tools", "available");
        }
        foreach (var code in new[] { "AGENT_AUTH_REQUIRED", "AGENT_TIMEOUT", "AGENT_NETWORK_ERROR", "AGENT_REQUEST_REJECTED", "SESSION_ENDED" }) {
            try {
                await AiCapabilityProjection.ReadCodexAsync("/fixture", (method, _) => method == "skills/list"
                    ? Task.FromException<JsonElement>(new AiOperationException(code)) : Task.FromResult(Json(new { data = Array.Empty<object>() })));
                throw new InvalidOperationException("Unexpected failure was converted into unsupported: " + code);
            } catch (AiOperationException error) when (error.Code == code) { }
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try {
            await AiCapabilityProjection.ReadCodexAsync("/fixture", (_, _) => Task.FromCanceled<JsonElement>(cancellation.Token));
            throw new InvalidOperationException("Cancellation was swallowed.");
        } catch (OperationCanceledException) { }
        var absent = await AiCapabilityProjection.ReadCodexAsync("/fixture", (_, _) => Task.FromResult(Json(new { unknown = true })));
        var unreported = AiCapabilityProjection.Catalog("codex", "codex", default, new(), codex: absent);
        foreach (var id in new[] { "skills", "mcp", "toolCatalog", "rateLimits" }) Expect(unreported, id, "not-reported");
    }

    private static async Task CodexInventoriesAreBounded()
    {
        var tools = Enumerable.Range(0, 101).ToDictionary(i => "tool-" + i, i => (object)new { secret = "fixture-private-tool-" + i });
        var optional = await AiCapabilityProjection.ReadCodexAsync("/fixture", (method, _) => Task.FromResult(method switch {
            "skills/list" => Json(new { data = new[] { new { cwd = "/fixture", skills = Enumerable.Range(0, 101).Select(i => new {
                name = "skill-" + i, description = new string('a', 199) + "😀" + "tail"
            }).ToArray() } } }),
            "mcpServerStatus/list" => Json(new { data = new[] { new { name = "tools", authStatus = "unsupported", tools } }, nextCursor = (string?)null }),
            _ => Json(new { rateLimits = new { primary = new { usedPercent = 200 }, secondary = new { usedPercent = -5 } } })
        }));
        var result = AiCapabilityProjection.Catalog("codex", "codex", default, new(), codex: optional);
        Require(result.Skills.Length == 100 && result.Tools.Length == 100 && result.Truncated, "Codex item cap is not enforced.");
        Expect(result, "rateLimits", "not-reported");
        Require(result.Skills.All(s => s.Description.Length == 199 && !char.IsHighSurrogate(s.Description[^1])), "Truncation split a surrogate pair.");
        Require(result.Tools.All(t => t.Status == "available"), "OAuth unsupported was confused with tool execution unsupported.");
        Bound(result);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static void Expect(AiCapabilityProjection.Result result, string id, string status) =>
        Require(result.Features.Single(f => f.Id == id).Status == status, $"{result.Agent}/{id}: expected {status}.");
    private static void Bound(AiCapabilityProjection.Result result)
    {
        var json = JsonSerializer.SerializeToElement(result, AiSessionManager.Json);
        Check(json);
        foreach (var feature in result.Features) Require(feature.Status is "available" or "unsupported" or "not-reported", "Unstable feature status.");
        foreach (var tool in result.Tools) Require(tool.Status is "available" or "unsupported" or "not-reported", "Raw tool status escaped.");
        static void Check(JsonElement value) {
            if (value.ValueKind == JsonValueKind.String) Require(value.GetString()!.Length <= 200, "A field exceeds the text bound.");
            else if (value.ValueKind == JsonValueKind.Array) {
                Require(value.GetArrayLength() <= 100, "An array exceeds the row bound.");
                foreach (var child in value.EnumerateArray()) Check(child);
            } else if (value.ValueKind == JsonValueKind.Object) foreach (var child in value.EnumerateObject()) Check(child.Value);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
