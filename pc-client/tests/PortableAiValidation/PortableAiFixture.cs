// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using TermLink.Agent;

/// <summary>Protocol simulation only; never starts a model, shell, tool, or network connection.</summary>
internal static class PortableAiFixture
{
    public const string TraceVariable = "TERMLINK_PORTABLE_AI_FIXTURE_TRACE";
    public const string DelayVariable = "TERMLINK_PORTABLE_AI_FIXTURE_START_DELAY";

    public static async Task RunAsync()
    {
        var trace = Environment.GetEnvironmentVariable(TraceVariable) ?? throw new InvalidOperationException("Fixture trace directory required.");
        var model = "fixture-model"; var provider = "fixture-provider"; var mode = "build";
        var pid = Environment.ProcessId;
        Trace("pid:" + pid);
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = AiAgentSession.S(root, "method");
            var id = AiAgentSession.P(root, "id");
            var parameters = AiAgentSession.P(root, "params");
            if (method.Length == 0)
            {
                var result = AiAgentSession.P(root, "result");
                if (id.ToString() == "fixture-approval")
                    Trace("decision:" + AiAgentSession.S(result, "decision"));
                else if (id.ToString() == "fixture-desktop-auth")
                {
                    Trace("desktop-headers-applied:" + (AiAgentSession.P(result, "headersApplied").ValueKind == JsonValueKind.True ? "true" : "false"));
                    Trace("desktop-result-fields:" + string.Join(",", result.EnumerateObject().Select(item => item.Name).Order()));
                }
                else if (id.ToString() == "fixture-question")
                    Trace("question-answer:" + AiAgentSession.P(AiAgentSession.P(AiAgentSession.P(result, "content"), "answers"), "Continue?")[0].GetString());
                if (id.ToString() == "fixture-desktop-auth") Complete(failed: true);
                else Complete();
                continue;
            }
            Trace("rpc:" + method);
            switch (method)
            {
                case "runtime/capabilities":
                    Reply(new { methods = new[] { "session/create", "session/subscribe", "session/read", "session/send", "session/stop" } });
                    break;
                case "session/create":
                    if (int.TryParse(Environment.GetEnvironmentVariable(DelayVariable), out var delay)) await Task.Delay(delay);
                    var chosen = AiAgentSession.P(parameters, "model");
                    if (chosen.ValueKind == JsonValueKind.Object) { model = AiAgentSession.S(chosen, "modelId"); provider = AiAgentSession.S(chosen, "providerId"); }
                    mode = AiAgentSession.S(parameters, "mode");
                    Trace("create-mode:" + mode);
                    Reply(new { sessionId = "fixture-session-" + pid });
                    break;
                case "session/subscribe":
                    Trace("subscribe-delivery:" + AiAgentSession.S(parameters, "deliveryKind"));
                    Reply(new { subscribed = true });
                    break;
                case "session/read":
                    Reply(new { settings = new { model = new { current = new { modelId = model, providerId = provider } },
                        mode = new { current = mode }, thoughtLevel = new { enabled = false, current = "", defaultLevel = "", available = Array.Empty<object>() } },
                        runtime = new { stateRevision = 1 } });
                    break;
                case "session/send":
                    Reply(new { accepted = true });
                    switch (AiAgentSession.S(parameters, "content"))
                    {
                        case "approval":
                            Write(new { id = "fixture-approval", method = "interaction/requestPermission", @params = new { command = "fixture-only; no command is executed" } });
                            break;
                        case "question":
                            Write(new { id = "fixture-question", method = "interaction/requestUserInput", @params = new { questions = new[] {
                                new { id = "q1", question = "Continue?", options = new[] { new { label = "Yes", value = "yes_value" } }, multiSelect = false } } } });
                            break;
                        case "desktop-auth":
                            Write(new { id = "fixture-desktop-auth", method = "interaction/requestProviderRuntimeHeaders", @params = new { providerId = "fixture-desktop" } });
                            break;
                        case "hold": break;
                        case "exit": return;
                        default:
                            Write(new { method = "session/event", @params = new { type = "model.streaming", payload = new {
                                kind = "text_delta", assistantMessageId = "reply-" + AiAgentSession.S(parameters, "inputId"), delta = "fixture response" } } });
                            Complete();
                            break;
                    }
                    break;
                case "session/stop": Reply(new { stopped = true }); Complete(); break;
                default: Write(new { id, error = new { code = -32601, message = "Unsupported fixture method" } }); break;
            }
            void Reply(object result) => Write(new { id, result });
        }
        void Trace(string message) => File.AppendAllText(trace, message + "\n");
    }

    private static void Complete(bool failed = false) => Write(new { method = "session/event", @params = new {
        type = failed ? "turn.failed" : "turn.completed", payload = new { error = new { code = "CONFIGURATION_ERROR" } } } });
    private static void Write(object value) => Console.WriteLine(AiSessionManager.Encode(value));
}
