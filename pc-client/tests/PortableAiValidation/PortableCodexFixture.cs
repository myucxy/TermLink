// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using TermLink.Agent;

/// <summary>Codex protocol simulation: never executes a model, tool or network request.</summary>
internal static class PortableCodexFixture
{
    internal const string TraceVariable = "TERMLINK_CODEX_FIXTURE_TRACE";
    internal const string ModeVariable = "TERMLINK_CODEX_FIXTURE_MODE";

    internal static async Task RunAsync(string[] arguments)
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable) ?? "valid";
        var trace = Environment.GetEnvironmentVariable(TraceVariable) ?? throw new InvalidOperationException("Fixture trace required");
        void Trace(string line) => File.AppendAllText(trace, line + "\n");
        Trace("pid:" + Environment.ProcessId);
        Trace("exec:" + Environment.ProcessPath);
        Trace("home:" + Environment.GetEnvironmentVariable("CODEX_HOME"));
        Trace("sqlite:" + Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME"));
        Trace("provider-env:" + (Environment.GetEnvironmentVariable("OPENAI_API_KEY") is null ? "absent" : "present"));
        if (arguments.Contains("--version")) { Console.WriteLine(mode == "mismatch" ? "Claude Code 2.1.29" : "codex-cli 0.162.1"); return; }
        if (arguments.Contains("--help")) { Console.WriteLine(mode == "unsupported" ? "No structured interface" : "app-server  Run the app server"); return; }
        if (!arguments.Contains("app-server")) throw new InvalidOperationException("Unexpected fixture launch");
        Trace("args:" + string.Join("|", arguments));
        var initialized = false;
        var thread = Guid.NewGuid().ToString();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = AiAgentSession.S(root, "method");
            var id = AiAgentSession.P(root, "id");
            var parameters = AiAgentSession.P(root, "params");
            if (method.Length == 0)
            {
                if (id.ToString() == "codex-approval")
                {
                    Trace("decision:" + AiAgentSession.S(AiAgentSession.P(root, "result"), "decision"));
                    Complete();
                }
                continue;
            }
            Trace("rpc:" + method);
            if (method == "initialize") { Reply(new { userAgent = "fixture", platformFamily = "unix", platformOs = "macos" }); continue; }
            if (method == "initialized") { initialized = true; continue; }
            if (!initialized) throw new InvalidOperationException("A Codex method preceded initialized");
            switch (method)
            {
                case "account/read":
                    Reply(new { account = File.Exists(Path.Combine(Path.GetDirectoryName(trace)!, "authenticated")) ? (object)new { type = "chatgpt", email = "never-export@example.invalid", accessToken = "must-never-leave-runtime" } : null, requiresOpenaiAuth = true }); break;
                case "account/login/start":
                    Reply(new { type = "chatgptDeviceCode", loginId = "11111111-2222-4333-8444-555555555555", userCode = "ABCD-1234",
                        verificationUrl = mode == "auth-invalid" ? "https://untrusted.invalid/login" : "https://auth.openai.com/codex/device" }); break;
                case "account/login/cancel": Reply(new { status = "canceled" }); break;
                case "model/list": Reply(new { data = new[] { new { id = "fixture-model", model = "fixture-model", displayName = "Fixture", isDefault = true,
                    defaultReasoningEffort = "medium", supportedReasoningEfforts = new[] { new { reasoningEffort = "medium", description = "fixture" } } } }, nextCursor = (string?)null }); break;
                case "configRequirements/read": Reply(new { requirements = new { allowedApprovalPolicies = new[] { "on-request" },
                    allowedSandboxModes = new[] { "workspace-write", "read-only" }, allowedApprovalsReviewers = new[] { "user" } } }); break;
                case "thread/start":
                    Trace("approval-policy:" + AiAgentSession.S(parameters, "approvalPolicy"));
                    Trace("sandbox:" + AiAgentSession.S(parameters, "sandbox"));
                    Reply(new { thread = new { id = thread }, model = "fixture-model" }); break;
                case "turn/start":
                    Reply(new { turn = new { id = "fixture-turn" } });
                    var text = AiAgentSession.S(AiAgentSession.P(parameters, "input")[0], "text");
                    Write(new { method = "turn/started", @params = new { turn = new { id = "fixture-turn" } } });
                    if (text == "approval") Write(new { id = "codex-approval", method = "item/commandExecution/requestApproval", @params = new { command = "fixture-only; never executed" } });
                    else if (text == "exit") return;
                    else if (text != "hold") { Write(new { method = "item/agentMessage/delta", @params = new { itemId = "fixture-response", delta = "Codex fixture response" } }); Complete(); }
                    break;
                case "turn/interrupt": Trace("interrupted"); Reply(new { }); Complete(); break;
                default: Write(new { id, error = new { code = -32601, message = "Unsupported fixture method" } }); break;
            }
            void Reply(object result) => Write(new { id, result });
        }
    }

    private static void Complete() => Write(new { method = "turn/completed", @params = new { turn = new { id = "fixture-turn", status = "completed" } } });
    private static void Write(object value) => Console.WriteLine(AiSessionManager.Encode(value));
}
