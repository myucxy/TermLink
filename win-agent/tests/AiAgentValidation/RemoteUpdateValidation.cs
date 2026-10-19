using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class RemoteUpdateValidation
{
    internal static async Task Run(string directory)
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldProtocol = Environment.GetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL");
        var fixture = Path.Combine(directory, "update-fixture"); Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "codex.cmd"), "@echo off\r\n\""+Environment.ProcessPath+"\" --fixture %*\r\n");
        try {
            Environment.SetEnvironmentVariable("PATH", fixture);
            Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", "codex");
            await AiAgentDiscovery.CatalogAsync(true);
            using var manager = new AiSessionManager(installRoot: fixture);
            var owner = Guid.NewGuid();
            var create = new AiSessionManager.Request("create", manager.HostId, Guid.NewGuid().ToString(), AgentId: "codex", Directory: directory);
            var result = JsonDocument.Parse(await manager.ExecuteAsync(owner, AiSessionManager.Encode(create), default));
            var session = result.RootElement.GetProperty("session").GetProperty("id").GetString();
            Check(manager.TryBeginUpdate(), "Idle AI prevented upgrade preparation.");
            Check(!manager.TryBeginUpdate(), "Two updaters acquired the same AI gate.");
            foreach (var action in new[] { "create", "send", "attachment.begin", "resume" }) {
                var request = create with { Action=action, OperationId=Guid.NewGuid().ToString(), SessionId=session, Text="approval", ControlVersion=1 };
                Check((await manager.ExecuteAsync(owner, AiSessionManager.Encode(request), default)).Contains("HOST_UPDATING"), "Mutation crossed update gate: "+action);
            }
            Check((await manager.ExecuteAsync(owner, AiSessionManager.Encode(new AiSessionManager.Request("poll", manager.HostId, SessionId: session)), default)).Contains(session!), "Read-only state was blocked by upgrade.");
            manager.EndUpdate();
            await manager.ExecuteAsync(owner, AiSessionManager.Encode(create with { Action="send", OperationId=Guid.NewGuid().ToString(), SessionId=session, Text="approval", ControlVersion=1 }), default);
            Check(!manager.TryBeginUpdate(), "Upgrade interrupted an active AI turn or pending approval.");
            await manager.ExecuteAsync(owner, AiSessionManager.Encode(create with { Action="end", OperationId=Guid.NewGuid().ToString(), SessionId=session, ControlVersion=1 }), default);
            Check(manager.TryBeginUpdate(), "AI update gate was not released after the task ended.");
            manager.EndUpdate();
            Console.WriteLine("PASS remote update AI gate: idle preparation, single owner, blocked new mutations, readable status, active task rejection and release.");
        } finally {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("TERMLINK_AI_FIXTURE_PROTOCOL", oldProtocol);
            await AiAgentDiscovery.CatalogAsync(true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
