using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class ClaudeDesktopValidation
{
    internal static async Task Real(string directory)
    {
        var roots = AiAgentDiscovery.InstalledDesktopRoots("claude").ToArray();
        var candidates = AiAgentDiscovery.DesktopCandidates("claude", roots).ToArray();
        var fallback = new AiAgentInstallation("claude", "Claude Desktop", "", "", "", "claude", "missing");
        var agent = await AiAgentDiscovery.ProbeCandidatesAsync(fallback, candidates, default);
        if (agent.Status != "available") throw new Exception("Desktop Code protocol unavailable: " + agent.Status);
        Console.WriteLine(JsonSerializer.Serialize(new { desktopEngine = agent.Path, agent.Version, agent.Status,
            desktopHistoryRoots = ClaudeNativeHistory.HistoryRoots().Length, standaloneCliUsed = false }));
        var caller = Guid.NewGuid();
        using var session = new AiAgentSession(agent, directory, caller, mode: "plan");
        await session.StartAsync();
        Console.WriteLine("PASS Claude Desktop engine stream-json initialization without standalone CLI.");
        var marker = "TERMLINK_CLAUDE_DESKTOP_" + Guid.NewGuid().ToString("N");
        await session.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Remember this exact marker: " + marker + ". Reply OK only. Do not use tools."));
        await Idle(session, caller);
        var nativeId = session.ArchiveRecord().NativeId;
        session.Dispose();
        var history = await AiAgentSession.NativeHistoryAsync(agent, "", default);
        var seed = history.Sessions.SingleOrDefault(s => s.NativeId == nativeId) ?? throw new Exception("Desktop engine history missing.");
        // Isolated desktop profile populated only with this test's transcript, never user credentials.
        var data = AiAgentDiscovery.ClaudeDesktopDataRoots().First(root => Path.GetFileName(root) == "Claude-3p");
        var profile = Path.Combine(data, "claude-code-sessions", Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "local_" + nativeId);
        var config = Path.Combine(profile, ".claude");
        var project = Path.Combine(config, "projects", Path.GetFileName(Path.GetDirectoryName(seed.SourcePath)));
        Directory.CreateDirectory(project);
        var transcript = Path.Combine(project, nativeId + ".jsonl");
        File.Copy(seed.SourcePath, transcript);
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddSeconds(1));
        history = await AiAgentSession.NativeHistoryAsync(agent, "", default);
        var entry = history.Sessions.SingleOrDefault(s => s.NativeId == nativeId) ?? throw new Exception("Desktop profile history missing.");
        if (entry.SourcePath != transcript) throw new Exception("Desktop transcript not selected.");
        Console.WriteLine("Desktop-specific transcript discovered without copying credentials.");
        using var imported = AiAgentSession.Import(agent, entry, caller, null);
        await imported.StartAsync();
        await imported.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Reply only with the exact marker from my earlier message. Do not use tools."));
        await Idle(imported, caller);
        var record = imported.ArchiveRecord();
        if (record.NativeId != nativeId || record.ClaudeTranscriptPath != transcript ||
            !record.Messages.Last(m => m.Role == "assistant").Text.Contains(marker)) throw new Exception("Claude continuation lost native context or profile.");
        var archive = new AiSessionArchive(Path.Combine(directory, "archive")); archive.Save(record);
        var persisted = archive.Read().Single();
        if (persisted.ClaudeTranscriptPath != transcript) throw new Exception("Desktop resume transcript path lost in encrypted archive.");
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, sameNativeThread = true, contextReplyMatched = true, standaloneCliUsed = false,
            desktopProfileHistory = true, historicalPermissionPreserved = record.Mode == "plan", encryptedArchiveProfilePreserved = true, nativeId }));
    }

    private static async Task Idle(AiAgentSession session, Guid caller)
    {
        for (var n = 0; n < 1200; n++) {
            var snapshot = JsonSerializer.SerializeToElement(session.Snapshot(caller, true), AiSessionManager.Json);
            var error = AiAgentSession.S(snapshot, "error");
            if (error.Length > 0) {
                var record = session.ArchiveRecord();
                Console.WriteLine(JsonSerializer.Serialize(new { error, lastAssistantReply = record.Messages.LastOrDefault(m => m.Role == "assistant")?.Text }));
                throw new Exception("Claude desktop turn failed: " + error);
            }
            if (AiAgentSession.S(snapshot, "state") == "idle") return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Claude desktop turn timed out.");
    }
}
