using TermLink.Platform;
using TermLink.Platform.MacOS;

internal static class MacDesktopValidation
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "tl-mac-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new MacPlatformOptions(Path.Combine(root, "agent"), Path.Combine(root, "agent.sock"),
                Path.Combine(root, "TermLink DEV"), Path.Combine(root, "LaunchAgents"), 501);
            var runner = new RecordingRunner();
            var appDirectory = Path.Combine(root, "TermLink DEV.app", "Contents", "MacOS");
            var operations = new MacDesktopOperations(options, runner, appDirectory);
            Require(operations.SupportsMacIntegration && !operations.SupportsWindowsTerminalIntegration && !operations.SupportsAutomaticUpdates,
                "macOS advertised unsupported Windows integration or automatic updates.");
            var update = await operations.CheckForUpdatesAsync(true, (_, _) => throw new Exception("Unexpected install"), null, CancellationToken.None);
            Require(update.Decision == DesktopUpdateDecision.NotConfigured && runner.Calls.Count == 0,
                "macOS tried a Windows update or reported a successful update check.");
            await operations.PerformPlatformActionAsync(DesktopPlatformAction.OpenTerminal, CancellationToken.None);
            Require(runner.Calls.Last().SequenceEqual(new[] { "-b", "com.apple.Terminal" }), "Terminal was not opened by its macOS bundle identifier.");
            await operations.PerformPlatformActionAsync(DesktopPlatformAction.OpenApplicationDirectory, CancellationToken.None);
            Require(runner.Calls.Last().SequenceEqual(new[] { "-R", "--", Path.Combine(root, "TermLink DEV.app") }),
                "Finder reveal split the application path or exposed the internal MacOS directory.");
            await operations.PerformPlatformActionAsync(DesktopPlatformAction.OpenLoginItems, CancellationToken.None);
            Require(runner.Calls.Last().Single().StartsWith("x-apple.systempreferences:", StringComparison.Ordinal), "Login items did not open system settings.");
            foreach (var agent in new[] { "codex", "claude", "opencode", "grok", "zcode" })
                await operations.OpenAgentHelpAsync(agent, CancellationToken.None);
            Require(runner.Calls.TakeLast(5).All(a => a.Count == 1 && a[0].StartsWith("https://", StringComparison.Ordinal)), "Agent help did not use allowlisted HTTPS targets.");
            var before = runner.Calls.Count;
            try { await operations.OpenAgentHelpAsync("https://evil.invalid/", CancellationToken.None); throw new Exception("Unexpected agent accepted"); }
            catch (ArgumentException) { }
            Require(runner.Calls.Count == before, "Unknown Agent opened an arbitrary URL.");
            await operations.OpenSessionDirectoryAsync(root, CancellationToken.None);
            Require(runner.Calls.Last().SequenceEqual(new[] { "--", root }), "Finder directory path was not passed as an isolated argument.");
            await operations.OpenDiagnosticsDirectoryAsync(CancellationToken.None);
            var context = await File.ReadAllTextAsync(Path.Combine(options.DataDirectory, "logs", "desktop-context.txt"));
            Require(context.Contains("Architecture:") && !context.Contains("secret-from-runner"), "Diagnostics context was empty or exposed command output.");
            runner.Fail = true;
            try { await operations.PerformPlatformActionAsync(DesktopPlatformAction.OpenTerminal, CancellationToken.None); throw new Exception("Failed open reported success"); }
            catch (InvalidOperationException error) { Require(error.Message == "PC_SHELL_OPEN_FAILED", "Native open failure leaked raw output."); }
            Console.WriteLine("PASS macOS desktop: Terminal, Finder, login settings, help allowlist, diagnostics and unsupported update isolation");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class RecordingRunner : IMacCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool Fail { get; set; }
        public ValueTask<MacProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(executable == "/usr/bin/open", "Native open used a shell or PATH resolution.");
            Calls.Add(arguments.ToArray());
            return ValueTask.FromResult(new MacProcessResult(Fail ? 1 : 0, "secret-from-runner", "secret-from-runner"));
        }
    }
}
