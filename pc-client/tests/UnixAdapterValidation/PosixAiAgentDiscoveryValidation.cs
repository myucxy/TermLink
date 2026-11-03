using System.Diagnostics;
using TermLink.Pc.Contracts;
using TermLink.Platform.Unix;

namespace TermLink.Platform.Unix.Validation;

public static class PosixAiAgentDiscoveryValidation
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("AI CLI discovery validation requires Unix.");
        var root = Path.Combine(Path.GetTempPath(), $"termlink-ai-discovery-{Guid.NewGuid():N}", "tools with spaces");
        Directory.CreateDirectory(root);
        try
        {
            await ValidateMissingAndDesktopAsync();
            await ValidateSuccessfulProbesAsync(root);
            await ValidateFailuresAndRuntimeAsync(root);
            await ValidateTimeoutAsync(root);
            await ValidateCancellationAsync(root);
            await ValidateOutputLimitAsync(root);
            ValidateLocator(root);
            var commands = await new PosixTerminalCommandDiscovery().DiscoverAsync(CancellationToken.None);
            Require(commands.Count > 0 && commands.All(command => !command.RunAsAdministrator),
                "Unix shell discovery advertised administrator execution.");
        }
        finally { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); }
    }

    private static async Task ValidateMissingAndDesktopAsync()
    {
        var agents = await new PosixAiAgentDiscovery(_ => null,
            desktopApplicationExists: _ => false).DiscoverAsync(CancellationToken.None);
        Require(agents.Select(agent => agent.Id).SequenceEqual(["codex", "claude", "opencode", "grok", "zcode"]),
            "AI discovery must preserve the five existing brands in stable order.");
        Require(agents.All(agent => agent.Status == "missing" && agent.Strategy == "local_cli" && agent.Path == ""),
            "Missing CLIs were advertised as ready or did not preserve local-only strategy.");

        var desktop = await new PosixAiAgentDiscovery(_ => null,
            desktopApplicationExists: path => path == "/Applications/Claude.app").DiscoverAsync(CancellationToken.None);
        Require(Agent(desktop, "claude") is { Status: "desktop_only", Path: "/Applications/Claude.app" },
            "A desktop installation without a CLI was not reported separately.");
        Require(Agent(desktop, "codex").Status == "missing", "An unrelated desktop app changed a missing CLI.");
    }

    private static async Task ValidateSuccessfulProbesAsync(string root)
    {
        var codex = await ScriptAsync(root, "codex", "[ \"$#\" = 1 ] && [ \"$1\" = --version ] || exit 9\nprintf 'Codex 1.2.3 你好\\nextra output\\n'\n");
        var claude = await ScriptAsync(root, "claude", "printf 'Claude 2.0\\n' >&2\n");
        var node = await ScriptAsync(root, "node", "exec /bin/sh \"$@\"\n");
        var grok = await ScriptAsync(root, "grok", "[ \"$GROK_DISABLE_AUTOUPDATER\" = 1 ] || exit 8\nprintf 'Grok 1\\n'\n");
        var opencode = await ScriptAsync(root, "opencode", "printf 'OpenCode 3\\n'\n", "#!/usr/bin/env node\n");
        var tools = new Dictionary<string, string>
        {
            ["codex"] = codex, ["claude"] = claude, ["node"] = node,
            ["grok"] = grok, ["opencode"] = opencode,
        };
        var agents = await Discovery(tools).DiscoverAsync(CancellationToken.None);
        Require(Agent(agents, "codex") is { Status: "detected", Strategy: "local_cli", Version: "Codex 1.2.3 你好" },
            "A CLI in a path containing spaces did not receive its exact --version argument.");
        Require(Agent(agents, "claude").Version == "Claude 2.0", "A version written only to stderr was lost.");
        Require(Agent(agents, "opencode").Status == "detected",
            "An env runtime was not resolved through the absolute executable lookup.");
        Require(Agent(agents, "grok").Status == "detected", "The Grok probe did not disable its automatic updater.");
        Require(agents.All(agent => agent.Status != "available"), "CLI discovery claimed remote protocol availability.");
    }

    private static async Task ValidateFailuresAndRuntimeAsync(string root)
    {
        var failed = await ScriptAsync(root, "failed-codex", "printf 'not a version\\n'\nexit 4\n");
        var noRuntime = await ScriptAsync(root, "no-runtime", "exit 0\n", "#!/usr/bin/env missing-validation-runtime\n");
        var noInterpreter = await ScriptAsync(root, "no-interpreter", "exit 0\n", "#!/no-such-validation-interpreter\n");
        var agents = await Discovery(new()
        {
            ["codex"] = failed, ["claude"] = noRuntime, ["zcode"] = noInterpreter,
            ["opencode"] = "relative/should-not-execute",
        }).DiscoverAsync(CancellationToken.None);
        Require(Agent(agents, "codex") is { Status: "probe_failed", Version: "" }, "A nonzero --version exit was accepted.");
        Require(Agent(agents, "claude").Status == "runtime_missing", "A missing env runtime was not reported.");
        Require(Agent(agents, "zcode").Status == "runtime_missing", "A missing absolute interpreter was not reported.");
        Require(Agent(agents, "opencode").Status == "probe_failed", "A relative injected executable path was accepted.");
    }

    private static async Task ValidateTimeoutAsync(string root)
    {
        var script = await ScriptAsync(root, "timeout-codex", "printf '%s' \"$$\" > \"$0.pid\"\nexec /bin/sleep 30\n");
        var watch = Stopwatch.StartNew();
        var agents = await Discovery(new() { ["codex"] = script }, TimeSpan.FromSeconds(1))
            .DiscoverAsync(CancellationToken.None);
        Require(Agent(agents, "codex").Status == "probe_failed", "A timed-out CLI was accepted.");
        Require(watch.Elapsed < TimeSpan.FromSeconds(4), "A CLI timeout did not bound discovery duration.");
        await RequireStoppedAsync(script + ".pid");
    }

    private static async Task ValidateCancellationAsync(string root)
    {
        var script = await ScriptAsync(root, "cancel-codex",
            "/bin/sleep 30 &\nchild=$!\nprintf '%s %s' \"$$\" \"$child\" > \"$0.pid\"\nwait\n");
        using var cancellation = new CancellationTokenSource();
        var detection = Discovery(new() { ["codex"] = script }, TimeSpan.FromSeconds(10))
            .DiscoverAsync(cancellation.Token).AsTask();
        await WaitForFileAsync(script + ".pid");
        cancellation.Cancel();
        try
        {
            await detection.WaitAsync(TimeSpan.FromSeconds(4));
            throw new InvalidOperationException("Caller cancellation was converted to a capability result.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        await RequireStoppedAsync(script + ".pid");

        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        var lookedUp = false;
        try
        {
            await new PosixAiAgentDiscovery(_ => { lookedUp = true; return null; })
                .DiscoverAsync(alreadyCanceled.Token);
            throw new InvalidOperationException("Already-canceled discovery did not stop.");
        }
        catch (OperationCanceledException) when (alreadyCanceled.IsCancellationRequested) { }
        Require(!lookedUp, "Canceled discovery started looking up executable paths.");
    }

    private static async Task ValidateOutputLimitAsync(string root)
    {
        // Both streams count against a single bound; an endless producer must be
        // stopped by its output budget rather than waiting for the probe timeout.
        var chunk = new string('x', 4096);
        var script = await ScriptAsync(root, "flood-codex",
            "printf '%s' \"$$\" > \"$0.pid\"\nwhile :; do\nprintf '" + chunk + "'\nprintf '" + chunk + "' >&2\ndone\n");
        var watch = Stopwatch.StartNew();
        var agents = await Discovery(new() { ["codex"] = script }, TimeSpan.FromSeconds(10))
            .DiscoverAsync(CancellationToken.None);
        Require(Agent(agents, "codex") is { Status: "probe_failed", Version: "" }, "Unbounded CLI output was accepted.");
        Require(watch.Elapsed < TimeSpan.FromSeconds(4), "Excessive CLI output did not terminate its producer promptly.");
        await RequireStoppedAsync(script + ".pid");
    }

    private static void ValidateLocator(string root)
    {
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            Require(ExecutableLocator.Find("codex", ".") is null, "A relative PATH entry executed from the current directory.");
            Require(ExecutableLocator.Find("codex", ":.") is null, "An empty PATH entry trusted the current directory.");
            Require(ExecutableLocator.Find("codex", "") is null, "An explicit empty PATH enabled default directory fallbacks.");
            Require(ExecutableLocator.Find("codex", root) == Path.Combine(root, "codex"),
                "An absolute PATH directory containing spaces was not preserved.");
            Require(ExecutableLocator.Find("./codex", root) is null, "A relative executable path was accepted.");
            Require(ExecutableLocator.Find("codex", Path.Combine(root, "missing")) is null,
                "An explicit PATH silently added unrelated fallback directories.");
        }
        finally { Environment.CurrentDirectory = originalDirectory; }
    }

    private static PosixAiAgentDiscovery Discovery(Dictionary<string, string> tools, TimeSpan? timeout = null) =>
        new(name => tools.GetValueOrDefault(name), timeout, _ => false);

    private static AiAgentCapability Agent(IReadOnlyList<AiAgentCapability> agents, string id) => agents.Single(agent => agent.Id == id);

    private static async Task<string> ScriptAsync(string root, string name, string body, string shebang = "#!/bin/sh\n")
    {
        var path = Path.Combine(root, name);
        await File.WriteAllTextAsync(path, shebang + body);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path) && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        Require(File.Exists(path), "The mock CLI did not create its process marker.");
    }

    private static async Task RequireStoppedAsync(string path)
    {
        await WaitForFileAsync(path);
        var pids = (await File.ReadAllTextAsync(path)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        var deadline = Stopwatch.StartNew();
        while (pids.Any(IsRunning) && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        Require(pids.All(pid => !IsRunning(pid)), "A canceled, timed-out, or flooding probe left its process tree running.");
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
