using TermLink.Ai;

internal static class DiscoveryValidation
{
    internal static async Task Run(string directory)
    {
        var root = Path.Combine(directory, "Desktop discovery");
        var resources = Path.Combine(root, "app", "resources");
        Directory.CreateDirectory(resources);
        var fallback = new AiAgentInstallation("codex", "Codex", "", "", "", "codex", "missing");
        var launcher = Path.Combine(root, "app", "ChatGPT.exe");
        File.WriteAllText(launcher, "This graphical launcher must never be executed by discovery.");
        var desktopOnly = AiAgentDiscovery.DesktopFallback(fallback, new[] { root });
        Check(desktopOnly.Status == "desktop_only" && desktopOnly.Path == launcher, "Store desktop registration was not recognized.");
        Check(!AiAgentDiscovery.DesktopCandidates("codex", new[] { root }).Any(), "A GUI launcher was offered as a Codex engine.");
        Check((await AiAgentDiscovery.ProbeCandidatesAsync(desktopOnly, Array.Empty<AiAgentInstallation>(), default)).Status == "desktop_only",
            "Desktop without an engine was marked runnable.");

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(p => p.EndsWith(".dll") || p.EndsWith(".json")))
            File.Copy(file, Path.Combine(resources, Path.GetFileName(file)), true);
        var engine = Path.Combine(resources, "codex.exe");
        File.Copy(Environment.ProcessPath!, engine, true);
        var candidates = AiAgentDiscovery.DesktopCandidates("codex", new[] { root }).ToArray();
        Check(candidates.Length == 1 && candidates[0].Path == engine, "Packaged engine path was not resolved.");
        var previous = Environment.GetEnvironmentVariable("TERMLINK_AI_DISCOVERY_FIXTURE");
        Environment.SetEnvironmentVariable("TERMLINK_AI_DISCOVERY_FIXTURE", "1");
        try {
            var found = await AiAgentDiscovery.ProbeCandidatesAsync(desktopOnly, candidates, default);
            Check(found.Status == "available" && found.Strategy == "native" && found.Name.Contains("桌面内置"), "Desktop-only Codex did not pass CLI protocol probing.");
            var broken = fallback with { Path = Path.Combine(root, "removed-codex.exe") };
            found = await AiAgentDiscovery.ProbeCandidatesAsync(desktopOnly, new[] { broken }.Concat(candidates), default);
            Check(found.Status == "available" && found.Path == engine, "A stale PATH entry hid a working desktop engine.");
            var cli = candidates[0] with { Name = "Codex CLI" };
            found = await AiAgentDiscovery.ProbeCandidatesAsync(desktopOnly, new[] { cli }.Concat(candidates), default);
            Check(found.Name == "Codex CLI", "A usable explicit CLI was not preferred.");
            var openCode = new AiAgentInstallation("opencode", "OpenCode", Environment.ProcessPath!, "", "", "acp", "missing");
            found = await AiAgentDiscovery.ProbeCandidatesAsync(openCode, new[] { openCode }, default);
            Check(found.Status == "available", "OpenCode help on stderr was ignored.");
        } finally { Environment.SetEnvironmentVariable("TERMLINK_AI_DISCOVERY_FIXTURE", previous); }

        var claudeRoot = Path.Combine(root, "Claude");
        Directory.CreateDirectory(claudeRoot);
        File.WriteAllText(Path.Combine(claudeRoot, "Claude.exe"), "Graphical launcher only.");
        var claude = fallback with { Id = "claude", Name = "Claude Code", Protocol = "claude" };
        Check(AiAgentDiscovery.DesktopFallback(claude, new[] { claudeRoot }).Status == "desktop_only", "Claude Desktop was reported missing.");
        var cache = Path.Combine(root, "Claude code cache");
        foreach (var version in new[] { "2.9.0", "2.10.0" }) {
            Directory.CreateDirectory(Path.Combine(cache, version));
            File.WriteAllText(Path.Combine(cache, version, "claude.exe"), "engine candidate");
        }
        var claudeCandidates = AiAgentDiscovery.DesktopCandidates("claude", new[] { claudeRoot }, cache).ToArray();
        Check(claudeCandidates.Length == 2 && claudeCandidates[0].Path.Contains("2.10.0"), "Claude downloaded engines did not use numeric version ordering.");
        Check(claudeCandidates.All(a => a.Path != Path.Combine(claudeRoot, "Claude.exe")), "Claude GUI was offered as stream-json CLI.");
        Directory.CreateDirectory(Path.Combine(cache, "2.11.0", "f0584be0fbaf"));
        File.WriteAllText(Path.Combine(cache, "2.11.0", "f0584be0fbaf", "claude.exe"), "desktop hash engine");
        Check(AiAgentDiscovery.DesktopCandidates("claude", new[] { claudeRoot }, cache).First().Path.Contains("f0584be0fbaf"), "Hashed Claude-3p engine layout was not discovered.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await AiAgentDiscovery.ProbeCandidatesAsync(fallback, candidates, cancellation.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS AI discovery: Store/standalone desktop, engine-only probing, stale CLI fallback, CLI priority, Claude engine versions, caller cancellation.");
    }

    internal static async Task Installed()
    {
        foreach (var id in new[] { "codex", "claude" }) {
            var roots = AiAgentDiscovery.InstalledDesktopRoots(id).ToArray();
            var fallback = new AiAgentInstallation(id, id, "", "", "", id, "missing");
            var detected = await AiAgentDiscovery.ProbeCandidatesAsync(AiAgentDiscovery.DesktopFallback(fallback, roots),
                AiAgentDiscovery.DesktopCandidates(id, roots), default, "D:/Dev/Env/TermLink-AiRuntimeValidation/cache");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { desktopOnlyProbe = true, detected.Id, detected.Name, detected.Path, detected.Version, detected.Status }));
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
