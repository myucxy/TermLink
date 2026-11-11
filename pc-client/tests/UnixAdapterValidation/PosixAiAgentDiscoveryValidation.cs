using System.Diagnostics;
using TermLink.Pc.Contracts;
using TermLink.Platform.Unix;

namespace TermLink.Platform.Unix.Validation;

public static class PosixAiAgentDiscoveryValidation
{
    private static int cases;

    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("AI CLI discovery validation requires Unix.");
        var root = Path.Combine(Path.GetTempPath(), $"termlink-ai-discovery-{Guid.NewGuid():N}", "tools with spaces ; $() `quoted`");
        Directory.CreateDirectory(root);
        cases = 0;
        try
        {
            await ValidateMissingAndDesktopAsync();
            await ValidateSuccessfulProbesAsync(root);
            await ValidateProtocolFailuresAsync(root);
            await ValidateVersionsAsync(root);
            await ValidateFailuresAndRuntimeAsync(root);
            await ValidateShimsAsync(root);
            await ValidateTimeoutAsync(root, help: false);
            await ValidateTimeoutAsync(root, help: true);
            await ValidateOverallBudgetAsync(root);
            await ValidateCancellationAsync(root, help: false);
            await ValidateCancellationAsync(root, help: true);
            await ValidateOutputLimitAsync(root, help: false);
            await ValidateOutputLimitAsync(root, help: true);
            ValidateLocator(root);
            ValidateBudgets();
            var commands = await new PosixTerminalCommandDiscovery().DiscoverAsync(CancellationToken.None);
            Require(commands.Count > 0 && commands.All(command => !command.RunAsAdministrator),
                "Unix shell discovery advertised administrator execution.");
            Console.WriteLine($"PASS AI CLI discovery: {cases} cases, version/help only, no authentication or remote execution");
        }
        finally { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); }
    }

    private static async Task ValidateMissingAndDesktopAsync()
    {
        var agents = await new PosixAiAgentDiscovery(_ => null,
            desktopApplicationExists: _ => false).DiscoverAsync(CancellationToken.None);
        Require(agents.Select(agent => agent.Id).SequenceEqual(["codex", "claude", "opencode", "grok", "zcode"]),
            "AI discovery must preserve five existing brands in stable order.");
        Require(agents.All(agent => agent.Status == "missing" && agent.Path == "" && agent.DetectionCode == "AI_CLI_MISSING" && agent.ProtocolStatus == "not_checked"),
            "Missing CLIs were advertised as ready.");
        RequireLocalOnly(agents);
        cases++;

        var desktop = await new PosixAiAgentDiscovery(_ => null,
            desktopApplicationExists: path => path == "/Applications/Claude.app" || path == "/Applications/ZCode.app")
            .DiscoverAsync(CancellationToken.None);
        Require(Agent(desktop, "claude") is { Status: "desktop_only", Path: "/Applications/Claude.app", ProtocolStatus: "not_checked" },
            "Desktop installation without public CLI was not reported separately.");
        Require(Agent(desktop, "zcode").Status == "desktop_only" && Agent(desktop, "codex").Status == "missing",
            "ZCode desktop fallback was missing or changed an unrelated CLI.");
        RequireLocalOnly(desktop);
        cases++;
    }

    private static async Task ValidateSuccessfulProbesAsync(string root)
    {
        var codex = await ScriptAsync(root, "codex", ProbeScript("codex-cli 0.162.1", "app-server   run the app server", "--help"));
        var claude = await ScriptAsync(root, "claude", ProbeScript("2.1.29 (Claude Code)", "--output-format stream-json", "--help", versionToStderr: true));
        var node = await ScriptAsync(root, "node", "exec /bin/sh \"$@\"\n");
        var opencode = await ScriptAsync(root, "opencode", ProbeScript("1.2.3", "Start ACP (Agent Client Protocol) server", "acp --help"), "#!/usr/bin/env node\n");
        var grok = await ScriptAsync(root, "grok", "[ \"$GROK_DISABLE_AUTOUPDATER\" = 1 ] || exit 8\n" +
            ProbeScript("Grok Build 0.2.1 (Node.js v22.4.0)", "Use stdio transport", "agent --help"));
        var zcode = await ScriptAsync(root, "zcode", ProbeScript("0.17.0", "app-server", "--help"));
        var agents = await Discovery(new()
        {
            ["codex"] = codex, ["claude"] = claude, ["node"] = node,
            ["opencode"] = opencode, ["grok"] = grok, ["zcode"] = zcode,
        }).DiscoverAsync(CancellationToken.None);
        Require(agents.All(agent => agent.Status == "detected" && agent.ProtocolStatus == "available" && agent.DetectionCode == "AI_PROTOCOL_AVAILABLE"),
            "Successful version/help probes did not detect all five protocols.");
        Require(Agent(agents, "codex").Version == "0.162.1" && Agent(agents, "claude").Version == "2.1.29" &&
            Agent(agents, "opencode").Version == "1.2.3", "Versions on stderr or without brand prefixes were rejected.");
        Require(agents.Select(agent => agent.Protocol).SequenceEqual(["codex", "claude", "acp", "acp", "zcode"]),
            "Protocol names changed from existing Windows agent semantics.");
        RequireLocalOnly(agents);
        cases += 5;
    }

    private static async Task ValidateProtocolFailuresAsync(string root)
    {
        var tools = new Dictionary<string, string>();
        foreach (var id in new[] { "codex", "claude", "opencode", "grok", "zcode" })
            tools[id] = await ScriptAsync(root, "unsupported-" + id, ProbeScript("1.2.3", "app-serverish stream-jsonish XACP stdioish", id == "opencode" ? "acp --help" : id == "grok" ? "agent --help" : "--help"));
        var agents = await Discovery(tools).DiscoverAsync(CancellationToken.None);
        Require(agents.All(agent => agent.Status == "detected" && agent.ProtocolStatus == "incompatible" && agent.DetectionCode == "AI_PROTOCOL_UNSUPPORTED"),
            "An unsupported help response or a protocol substring was accepted.");
        RequireLocalOnly(agents);
        cases += 5;

        var failing = await ScriptAsync(root, "help-fails", "if [ \"$*\" = --version ]; then printf '1.2.3\\n'; else printf 'app-server\\n'; exit 5; fi\n");
        var failed = Agent(await Discovery(new() { ["codex"] = failing }).DiscoverAsync(CancellationToken.None), "codex");
        Require(failed is { Status: "detected", Version: "1.2.3", ProtocolStatus: "probe_failed", DetectionCode: "AI_PROBE_FAILED" },
            "A nonzero protocol help lost its valid version or was marked available.");
        cases++;

        var stderr = await ScriptAsync(root, "help-stderr", "if [ \"$*\" = --version ]; then printf '1.2.3\\n'; else printf 'app-server\\n' >&2; fi\n");
        Require(Agent(await Discovery(new() { ["codex"] = stderr }).DiscoverAsync(CancellationToken.None), "codex").ProtocolStatus == "available",
            "Protocol help on stderr was discarded.");
        cases++;
    }

    private static async Task ValidateVersionsAsync(string root)
    {
        foreach (var (name, banner) in new[] { ("wrong-brand", "Claude Code 2.1.29"), ("runtime-shim", "node v22.4.0"),
            ("help-as-version", "Usage: codex 1.2.3 --help"), ("empty-version", ""), ("invalid-version", "not-a-version") })
        {
            var script = await ScriptAsync(root, name, "[ \"$*\" = --version ] || { printf called > \"$0.help\"; exit 9; }\nprintf '%s\\n' '" + banner + "'\n");
            var agent = Agent(await Discovery(new() { ["codex"] = script }).DiscoverAsync(CancellationToken.None), "codex");
            Require(agent is { Status: "incompatible", Version: "", ProtocolStatus: "not_checked", DetectionCode: "AI_VERSION_MISMATCH" } && !File.Exists(script + ".help"),
                "A mismatched/non-version shim was accepted or invoked beyond version inspection.");
            cases++;
        }
        var escaped = await ScriptAsync(root, "escape-banner", "if [ \"$*\" = --version ]; then printf '\\033]0;hidden diagnostics\\007\\033[31mcodex-cli 0.162.1\\033[0m\\r\\n'; else printf '\\033[32mapp-server\\033[0m\\n'; fi\n");
        var sanitized = Agent(await Discovery(new() { ["codex"] = escaped }).DiscoverAsync(CancellationToken.None), "codex");
        Require(sanitized is { Version: "0.162.1", ProtocolStatus: "available" } && !sanitized.Version.Any(char.IsControl),
            "ANSI/OSC output was exposed in a displayed version or broke valid protocol detection.");
        cases++;
    }

    private static async Task ValidateFailuresAndRuntimeAsync(string root)
    {
        var failed = await ScriptAsync(root, "failed-codex", "printf 'codex 1.2.3\\n'\nexit 4\n");
        var noRuntime = await ScriptAsync(root, "no-runtime", "exit 0\n", "#!/usr/bin/env missing-validation-runtime\n");
        var noInterpreter = await ScriptAsync(root, "no-interpreter", "exit 0\n", "#!/no-such-validation-interpreter\n");
        var agents = await Discovery(new()
        {
            ["codex"] = failed, ["claude"] = noRuntime, ["zcode"] = noInterpreter,
            ["opencode"] = "relative/should-not-execute",
        }).DiscoverAsync(CancellationToken.None);
        Require(Agent(agents, "codex") is { Status: "probe_failed", Version: "", DetectionCode: "AI_PROBE_FAILED" }, "A nonzero version exit was accepted.");
        Require(Agent(agents, "claude").Status == "runtime_missing" && Agent(agents, "zcode").Status == "runtime_missing",
            "A missing env/absolute interpreter was not reported.");
        Require(Agent(agents, "opencode") is { Status: "probe_failed", DetectionCode: "AI_EXECUTABLE_INVALID" },
            "A relative injected executable path was accepted.");
        cases += 4;

        var blocked = await ScriptAsync(root, "not-executable", "exit 0\n");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Require(Agent(await Discovery(new() { ["codex"] = blocked }).DiscoverAsync(CancellationToken.None), "codex") is
            { Status: "runtime_blocked", DetectionCode: "AI_RUNTIME_BLOCKED" }, "A non-executable CLI was not distinguished from a missing runtime.");
        cases++;
    }

    private static async Task ValidateShimsAsync(string root)
    {
        var node = await ScriptAsync(root, "node with arguments", "[ \"$1\" = --no-warnings ] || exit 8\nshift\nexec /bin/sh \"$@\"\n");
        var cli = await ScriptAsync(root, "shim ; $(touch NEVER_CREATED)", ProbeScript("1.2.3", "ACP", "acp --help"), "#!/usr/bin/env -S node --no-warnings\n");
        var agent = Agent(await Discovery(new() { ["opencode"] = cli, ["node"] = node }).DiscoverAsync(CancellationToken.None), "opencode");
        Require(agent.ProtocolStatus == "available" && agent.Path == cli && !File.Exists(Path.Combine(Path.GetTempPath(), "NEVER_CREATED")),
            "An env -S runtime, runtime argument or metacharacter path was not preserved literally.");
        cases++;

        var absolute = await ScriptAsync(root, "absolute-interpreter-arg", ProbeScript("1.2.3", "app-server", "--help"), "#!/bin/sh -e\n");
        Require(Agent(await Discovery(new() { ["codex"] = absolute }).DiscoverAsync(CancellationToken.None), "codex").ProtocolStatus == "available",
            "A public absolute interpreter with one argument was not supported.");
        cases++;

        var ambiguous = await ScriptAsync(root, "ambiguous-env", "printf '1.2.3\\n'\n", "#!/usr/bin/env -S node 'unparsed option'\n");
        Require(Agent(await Discovery(new() { ["codex"] = ambiguous, ["node"] = node }).DiscoverAsync(CancellationToken.None), "codex") is { Status: "probe_failed", DetectionCode: "AI_SHIM_UNSUPPORTED" },
            "Ambiguous env quoting was interpreted as a shell command.");
        cases++;

        foreach (var flag in new[] { "-c", "-l" })
        {
            var unsafeShell = await ScriptAsync(root, "unsafe-shell-" + flag, "printf called > \"$0.called\"\n", "#!/bin/sh " + flag + "\n");
            Require(Agent(await Discovery(new() { ["codex"] = unsafeShell }).DiscoverAsync(CancellationToken.None), "codex") is { Status: "probe_failed", DetectionCode: "AI_SHIM_UNSUPPORTED" } &&
                !File.Exists(unsafeShell + ".called"), "A shell shim could interpret a command string or load login startup files.");
            cases++;
        }
        var previousBashEnv = Environment.GetEnvironmentVariable("BASH_ENV");
        try
        {
            var startup = Path.Combine(root, "startup-script");
            await File.WriteAllTextAsync(startup, "printf called > \"" + startup.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + ".called\"\n");
            Environment.SetEnvironmentVariable("BASH_ENV", startup);
            var safeBash = await ScriptAsync(root, "bash-no-startup", ProbeScript("1.2.3", "app-server", "--help"), "#!/bin/bash\n");
            Require(Agent(await Discovery(new() { ["codex"] = safeBash }).DiscoverAsync(CancellationToken.None), "codex").ProtocolStatus == "available" &&
                !File.Exists(startup + ".called"), "A probe sourced a BASH_ENV startup file.");
        }
        finally { Environment.SetEnvironmentVariable("BASH_ENV", previousBashEnv); }
        cases++;

        var unsafePath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", ":.:relative:/usr/bin:/bin");
            var safe = await ScriptAsync(root, "safe-path", "case \":$PATH:\" in *\":.:\"*|*\"::\"*|*\":relative:\"*) exit 7;; esac\n" + ProbeScript("1.2.3", "app-server", "--help"));
            Require(Agent(await Discovery(new() { ["codex"] = safe }).DiscoverAsync(CancellationToken.None), "codex").ProtocolStatus == "available",
                "A probe child inherited relative or empty PATH entries.");
        }
        finally { Environment.SetEnvironmentVariable("PATH", unsafePath); }
        cases++;
    }

    private static async Task ValidateTimeoutAsync(string root, bool help)
    {
        var script = await ScriptAsync(root, help ? "timeout-help" : "timeout-version",
            (help ? "if [ \"$*\" = --version ]; then printf '1.2.3\\n'; exit 0; fi\n" : "") +
            "printf '%s' \"$$\" > \"$0.pid\"\nexec /bin/sleep 30\n");
        var watch = Stopwatch.StartNew();
        var agent = Agent(await Discovery(new() { ["codex"] = script }, TimeSpan.FromMilliseconds(250))
            .DiscoverAsync(CancellationToken.None), "codex");
        Require(agent.Status == (help ? "detected" : "probe_failed") && agent.DetectionCode == "AI_PROBE_TIMEOUT" &&
            agent.ProtocolStatus == (help ? "probe_failed" : "not_checked"), "A probe timeout lost stage-specific status.");
        Require(watch.Elapsed < TimeSpan.FromSeconds(3), "Per-probe timeout did not bound discovery.");
        await RequireStoppedAsync(script + ".pid");
        cases++;
    }

    private static async Task ValidateOverallBudgetAsync(string root)
    {
        var script = await ScriptAsync(root, "overall-timeout", "printf '%s' \"$$\" > \"$0.pid\"\nexec /bin/sleep 30\n");
        var watch = Stopwatch.StartNew();
        var agents = await new PosixAiAgentDiscovery(name => name == "codex" ? script : null,
            TimeSpan.FromSeconds(4), _ => false, TimeSpan.FromMilliseconds(200)).DiscoverAsync(CancellationToken.None);
        Require(Agent(agents, "codex").DetectionCode == "AI_DISCOVERY_TIMEOUT" && watch.Elapsed < TimeSpan.FromSeconds(3),
            "The total discovery budget was not independent of an individual probe deadline.");
        await RequireStoppedAsync(script + ".pid");
        cases++;
    }

    private static async Task ValidateCancellationAsync(string root, bool help)
    {
        var script = await ScriptAsync(root, help ? "cancel-help" : "cancel-version",
            (help ? "if [ \"$*\" = --version ]; then printf '1.2.3\\n'; exit 0; fi\n" : "") +
            "/bin/sleep 30 &\nchild=$!\nprintf '%s %s' \"$$\" \"$child\" > \"$0.pid\"\nwait\n");
        using var cancellation = new CancellationTokenSource();
        var detection = Discovery(new() { ["codex"] = script }, TimeSpan.FromSeconds(4))
            .DiscoverAsync(cancellation.Token).AsTask();
        await WaitForFileAsync(script + ".pid");
        cancellation.Cancel();
        try
        {
            await detection.WaitAsync(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("Caller cancellation was converted to a capability result.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        await RequireStoppedAsync(script + ".pid");
        cases++;
        if (help) return;
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
        Require(!lookedUp, "Canceled discovery started executable lookup.");
        cases++;
    }

    private static async Task ValidateOutputLimitAsync(string root, bool help)
    {
        // Neither stream alone reaches 64 KiB before the combined budget does.
        var chunk = new string('x', 4096);
        var script = await ScriptAsync(root, help ? "flood-help" : "flood-version",
            (help ? "if [ \"$*\" = --version ]; then printf '1.2.3\\n'; exit 0; fi\n" : "") +
            "printf '%s' \"$$\" > \"$0.pid\"\nwhile :; do\nprintf '" + chunk + "'\nprintf '" + chunk + "' >&2\ndone\n");
        var watch = Stopwatch.StartNew();
        var agent = Agent(await Discovery(new() { ["codex"] = script }, TimeSpan.FromSeconds(4))
            .DiscoverAsync(CancellationToken.None), "codex");
        Require(agent.Status == (help ? "detected" : "probe_failed") && agent.DetectionCode == "AI_PROBE_OUTPUT_LIMIT" &&
            agent.ProtocolStatus == (help ? "probe_failed" : "not_checked"), "Output limit did not cover both streams/stages.");
        Require(watch.Elapsed < TimeSpan.FromSeconds(3), "Output flooding waited for timeout instead of terminating promptly.");
        await RequireStoppedAsync(script + ".pid");
        cases++;
    }

    private static void ValidateLocator(string root)
    {
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            Require(ExecutableLocator.Find("codex", ".") is null && ExecutableLocator.Find("codex", ":.") is null,
                "Relative/empty PATH trusted the current directory.");
            Require(ExecutableLocator.Find("codex", "") is null, "Explicit empty PATH enabled default fallbacks.");
            Require(ExecutableLocator.Find("codex", root) == Path.Combine(root, "codex"), "An absolute PATH directory with spaces was not preserved.");
            Require(ExecutableLocator.Find("./codex", root) is null, "A relative executable path was accepted.");
            Require(ExecutableLocator.Find("codex", Path.Combine(root, "missing")) is null,
                "Explicit PATH added unrelated fallback directories.");
        }
        finally { Environment.CurrentDirectory = originalDirectory; }
        cases++;
    }

    private static void ValidateBudgets()
    {
        foreach (var (probe, total) in new[] { (TimeSpan.Zero, TimeSpan.FromSeconds(12)),
            (TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12)), (TimeSpan.FromSeconds(4), TimeSpan.Zero),
            (TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(13)) })
        {
            try { _ = new PosixAiAgentDiscovery(_ => null, probe, _ => false, total); }
            catch (ArgumentOutOfRangeException) { cases++; continue; }
            throw new InvalidOperationException("An unbounded probe/discovery deadline was accepted.");
        }
    }

    private static string ProbeScript(string version, string help, string helpArguments, bool versionToStderr = false) =>
        "case \"$*\" in\n--version) [ \"$#\" = 1 ] || exit 9; printf '%s\\n' '" + version + "'" + (versionToStderr ? " >&2" : "") + ";;\n'" +
        helpArguments + "') [ \"$#\" = " + (helpArguments.Contains(' ') ? "2" : "1") + " ] || exit 9; printf '%s\\n' '" + help + "';;\n*) exit 9;;\nesac\n";

    private static PosixAiAgentDiscovery Discovery(Dictionary<string, string> tools, TimeSpan? timeout = null) =>
        new(name => tools.GetValueOrDefault(name), timeout, _ => false);

    private static AiAgentCapability Agent(IReadOnlyList<AiAgentCapability> agents, string id) => agents.Single(agent => agent.Id == id);

    private static void RequireLocalOnly(IEnumerable<AiAgentCapability> agents) =>
        Require(agents.All(agent => agent.Strategy == "local_cli" && !agent.RemoteExecutionAvailable && agent.AuthenticationStatus == "not_checked"),
            "Installation/protocol discovery asserted authentication or enabled remote execution.");

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
        while ((!File.Exists(path) || new FileInfo(path).Length == 0) && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        Require(File.Exists(path) && new FileInfo(path).Length > 0, "The mock CLI did not create its process marker.");
    }

    private static async Task RequireStoppedAsync(string path)
    {
        await WaitForFileAsync(path);
        var pids = (await File.ReadAllTextAsync(path)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        var deadline = Stopwatch.StartNew();
        while (pids.Any(IsRunning) && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
        Require(pids.All(pid => !IsRunning(pid)), "A canceled, timed-out, or flooding probe left a process tree running.");
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
