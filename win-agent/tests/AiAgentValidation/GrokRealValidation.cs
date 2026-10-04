using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class GrokRealValidation
{
    private static JsonElement Snap(AiAgentSession session, Guid caller) => JsonSerializer.SerializeToElement(session.Snapshot(caller, true), AiSessionManager.Json);
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception("Grok real: " + reason); }
    private static AiSessionManager.Request Configure(JsonElement state, string model = null, string effort = null) =>
        new("configure", ControlVersion: state.GetProperty("controlVersion").GetInt64(), ExpectedSettingsVersion: state.GetProperty("settingsVersion").GetInt64(),
            ModelId: model ?? state.GetProperty("modelId").GetString(), ProviderId: "", ReasoningEffort: effort, PermissionMode: state.GetProperty("permissionMode").GetString());
    private static async Task Idle(AiAgentSession session, Guid caller)
    {
        for (var i = 0; i < 1200; i++) {
            var state = Snap(session, caller);
            if (state.GetProperty("error").GetString() == "AGENT_RATE_LIMITED") {
                await Cancel(session, caller);
                throw new Exception("Grok real: upstream rate limited; cancellation passed; remaining live checks BLOCKED");
            }
            Check(state.GetProperty("pendingCount").GetInt32() == 0, "unexpected approval in text-only test");
            if (state.GetProperty("state").GetString() != "running") {
                Check(state.GetProperty("state").GetString() == "idle" && state.GetProperty("error").GetString() == "", "turn failed: " + state.GetProperty("error"));
                return;
            }
            if (i > 0 && i % 100 == 0) Console.WriteLine("  waiting for Grok, seconds=" + i / 10);
            await Task.Delay(100);
        }
        throw new TimeoutException("Grok real turn exceeded 120 seconds");
    }
    private static Task Send(AiAgentSession session, Guid caller, string text) => session.ApplyAsync(caller,
        new("send", Text: text, ControlVersion: Snap(session, caller).GetProperty("controlVersion").GetInt64(), OperationId: Guid.NewGuid().ToString()));
    private static async Task Cancel(AiAgentSession session, Guid caller)
    {
        await session.ApplyAsync(caller, new("interrupt", ControlVersion: Snap(session, caller).GetProperty("controlVersion").GetInt64()));
        for (var i = 0; i < 120 && Snap(session, caller).GetProperty("state").GetString() == "running"; i++) await Task.Delay(100);
        var state = Snap(session, caller);
        Check(state.GetProperty("state").GetString() == "idle" && state.GetProperty("error").GetString() == "" && state.GetProperty("pendingCount").GetInt32() == 0,
            "cancellation did not settle cleanly");
        Console.WriteLine("PASS real cancellation, idle with no pending approval");
    }

    internal static async Task Resume(string directory)
    {
        var root = Path.GetFullPath(directory);
        Check(Path.GetDirectoryName(root) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) &&
            Path.GetFileName(root).StartsWith("termlink-ai-validation-"), "only isolated validation archives may be resumed");
        var archive = new AiSessionArchive(Path.Combine(root, "archive"));
        var record = archive.Read().Single(r => r.AgentId == "grok");
        var marker = System.Text.RegularExpressions.Regex.Match(record.Messages.First(m => m.Role == "user").Text, @"TERMLINK_GROK_[a-f0-9]{10}").Value;
        Check(marker.Length > 0, "validation marker missing");
        var agent = (await AiAgentDiscovery.DetectAsync()).Single(a => a.Id == "grok");
        await FinishRestored(agent, record, archive, Guid.NewGuid(), marker);
    }

    internal static async Task Permissions(string directory)
    {
        var agent = (await AiAgentDiscovery.DetectAsync()).Single(a => a.Id == "grok");
        var caller = Guid.NewGuid();
        using var session = new AiAgentSession(agent, directory, caller);
        await session.StartAsync();
        Console.WriteLine("REAL GROK permission test in isolated directory=" + directory);
        foreach (var allow in new[] { false, true }) {
            var name = allow ? "allowed.txt" : "denied.txt";
            await Send(session, caller, $"Create only {name} in the current directory with content OK using the write file tool. Do not execute commands. If denied, stop and do not retry or use another tool.");
            var answered = false;
            for (var i = 0; i < 1200; i++) {
                var state = Snap(session, caller);
                if (state.GetProperty("error").GetString() == "AGENT_RATE_LIMITED") { await Cancel(session, caller); throw new Exception("Permission test BLOCKED by upstream 429"); }
                if (state.GetProperty("pending").GetArrayLength() > 0) {
                    var pending = state.GetProperty("pending")[0];
                    Check(!answered, "unexpected second approval");
                    Check(!File.Exists(Path.Combine(directory, name)), "write occurred before explicit approval");
                    await session.ApplyAsync(caller, new("answer", ControlVersion: state.GetProperty("controlVersion").GetInt64(),
                        RequestId: pending.GetProperty("id").GetString(), Decision: allow ? "allow" : "deny"));
                    answered = true;
                }
                if (state.GetProperty("state").GetString() != "running") break;
                await Task.Delay(100);
            }
            Check(answered, "native permission request was not observed");
            Check(Snap(session, caller).GetProperty("state").GetString() == "idle", "permission turn did not finish");
            Check(File.Exists(Path.Combine(directory, name)) == allow, "permission did not govern file write");
            Console.WriteLine("PASS real " + (allow ? "allow_once" : "reject_once") + "; file outcome verified");
        }
    }

    internal static async Task Run(string directory)
    {
        var agent = (await AiAgentDiscovery.DetectAsync()).Single(a => a.Id == "grok");
        Check(agent.Status == "available", "Grok is not available");
        Console.WriteLine("REAL GROK " + agent.Version + "; isolated directory=" + directory);
        var caller = Guid.NewGuid(); var marker = "TERMLINK_GROK_" + Guid.NewGuid().ToString("N")[..10];
        var archive = new AiSessionArchive(Path.Combine(directory, "archive"));
        AiSessionArchive.Record record;
        using (var session = new AiAgentSession(agent, directory, caller)) {
            session.AttachArchive(archive);
            await session.StartAsync();
            var state = Snap(session, caller);
            Console.WriteLine("PASS create; model=" + state.GetProperty("modelId") + "; effort=" + state.GetProperty("reasoningEffort"));
            var models = await AiAgentSession.ListModelsAsync(agent, CancellationToken.None);
            var originalModel = state.GetProperty("modelId").GetString();
            var alternate = models.Models.FirstOrDefault(m => m.ModelId != originalModel);
            if (alternate != null) {
                await session.ApplyAsync(caller, Configure(Snap(session, caller), model: alternate.ModelId));
                Check(Snap(session, caller).GetProperty("modelId").GetString() == alternate.ModelId, "model switch ACK");
                await session.ApplyAsync(caller, Configure(Snap(session, caller), model: originalModel));
                Console.WriteLine("PASS model switch and restore");
            } else Console.WriteLine("NOT TESTED model switch: only one model advertised");
            state = Snap(session, caller);
            var efforts = state.GetProperty("reasoning").GetProperty("options").EnumerateArray().Select(e => e.GetProperty("value").GetString()).ToArray();
            var effort = efforts.Contains("low") ? "low" : efforts.FirstOrDefault();
            if (effort != null) {
                await session.ApplyAsync(caller, Configure(Snap(session, caller), effort: effort));
                Check(Snap(session, caller).GetProperty("reasoningEffort").GetString() == effort, "effort switch ACK");
                Console.WriteLine("PASS reasoning effort=" + effort);
            }
            await Send(session, caller, "Reply with exactly " + marker + ". Do not use tools, read files, execute commands, or create subagents. Remember this marker for the next message.");
            await Idle(session, caller);
            state = Snap(session, caller);
            var replies = state.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "assistant" && m.GetProperty("kind").GetString() == "text").ToArray();
            Check(replies.Length == 1 && replies[0].GetProperty("text").GetString().Trim() == marker, "one exact reply expected");
            Check(!state.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool"), "text test unexpectedly used a tool");
            Console.WriteLine("PASS real reply, single message, no tools");
            record = session.ArchiveRecord();
        }
        var saved = archive.Read().Single(r => r.Id == record.Id);
        await FinishRestored(agent, saved, archive, caller, marker);
    }

    private static async Task FinishRestored(AiAgentInstallation agent, AiSessionArchive.Record record, AiSessionArchive archive, Guid caller, string marker)
    {
        var saved = record;
        using (var recovered = AiAgentSession.Restore(agent, saved, archive, caller)) {
            await recovered.ResumeAsync();
            var state = Snap(recovered, caller);
            Check(state.GetProperty("messages").GetArrayLength() == record.Messages.Length, "replay duplicates archived messages");
            Check(state.GetProperty("permissionMode").GetString() == record.Mode && state.GetProperty("modelId").GetString() == record.Model, "historical settings lost");
            Console.WriteLine("PASS process restart/load; historical model and authorization retained");
            await Send(recovered, caller, "Repeat only the exact TERMLINK marker from my previous message. Do not use tools, read files, execute commands, or create subagents.");
            await Idle(recovered, caller);
            var messages = Snap(recovered, caller).GetProperty("messages").EnumerateArray().ToArray();
            Check(messages.Last(m => m.GetProperty("role").GetString() == "assistant" && m.GetProperty("kind").GetString() == "text").GetProperty("text").GetString().Trim() == marker, "native context lost after restart");
            foreach (var old in record.Messages) Check(messages.Any(m => m.GetProperty("id").GetString() == old.Id && m.GetProperty("text").GetString() == old.Text), "archive overwritten after restart");
            Console.WriteLine("PASS native context resume and stable transcript");
            await Send(recovered, caller, "Without using any tools, write a detailed 1000-word explanation of sorting algorithms.");
            await Task.Delay(500);
            if (Snap(recovered, caller).GetProperty("state").GetString() == "running") {
                await Cancel(recovered, caller);
            } else Console.WriteLine("NOT TESTED cancellation: turn finished before cancellation");
        }
    }
}
