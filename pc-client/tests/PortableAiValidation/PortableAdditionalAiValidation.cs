// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using TermLink.Agent.AI;
using TermLink.Ai;
using TermLink.Platform.Unix;

/// <summary>
/// Offline native and public-runtime fixtures. Requires a local C compiler, never
/// invokes a real provider CLI, a model, an auth command or a credential store.
/// </summary>
internal static class PortableAdditionalAiValidation
{
    private static readonly string[] Ids = ["claude", "opencode", "grok"];
    private static readonly string[] ExcludedEnvironment =
    [
        "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "XAI_API_KEY",
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_REFRESH_TOKEN", "OPENAI_ACCESS_TOKEN",
        "CHATGPT_ACCESS_TOKEN", "GROK_ACCESS_TOKEN", "OPENCODE_API_KEY", "CUSTOM_PROVIDER_CREDENTIAL",
        "TERMLINK_DISCOVERY_PRIVATE_SENTINEL", "AWS_SECRET_ACCESS_KEY", "GOOGLE_APPLICATION_CREDENTIALS",
        "OPENCODE_CONFIG", "OPENCODE_CONFIG_CONTENT", "OPENCODE_CONFIG_DIR", "OPENCODE_DB",
        "OPENCODE_TEST_HOME", "ANTHROPIC_CONFIG_DIR", "ANTHROPIC_PROFILE", "CLAUDE_CONFIG_DIR", "GROK_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
        "XDG_STATE_HOME", "XDG_CACHE_HOME", "NODE_OPTIONS", "NODE_PATH", "BASH_ENV", "ENV", "ZDOTDIR",
    ];

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Additional CLI fixtures require POSIX executable permissions.");
        PrivateDirectory(root);
        var fixture = await BuildFixtureAsync(Path.Combine(root, "fixtures with spaces ; $(literal)"));
        await DiscoveryStatusesAsync(Path.Combine(root, "statuses"), fixture);
        await LauncherAndIsolationAsync(Path.Combine(root, "isolation"), fixture);
        await ShimAndPathValidationAsync(Path.Combine(root, "shims"), fixture);
        await CatalogAsync(Path.Combine(root, "catalog"), fixture);
        await DirectorySafetyAsync(Path.Combine(root, "directory-safety"), fixture);
        Console.WriteLine("PASS portable additional CLI fixtures: Claude Code/OpenCode/Grok Build discovery, isolated native/Node launchers, catalog injection, status/path/cancellation checks; no real model or credentials");
    }

    private static async Task DiscoveryStatusesAsync(string root, string fixture)
    {
        var missing = await new PortableAdditionalAiDiscovery(root, _ => null, true).DiscoverAsync(CancellationToken.None);
        Require(missing.Select(a => a.Id).SequenceEqual(Ids) && missing.All(a => a.Status == "missing" && a.PortableStartInfo is null),
            "Missing additional CLIs lost stable identities or exposed launchers");
        var lookups = 0;
        var disabled = await new PortableAdditionalAiDiscovery(Path.Combine(root, "unsupported-platform"), _ => { lookups++; return fixture; }, false)
            .DiscoverAsync(CancellationToken.None);
        Require(lookups == 0 && disabled.All(a => a.Status == "missing"), "Unsupported platform probed native CLIs");
        foreach (var (mode, expected) in new[]
        {
            ("valid", "available"), ("mismatch", "incompatible"), ("unsupported", "incompatible"),
            ("versionfail", "probe_failed"), ("helpfail", "probe_failed"),
        })
        {
            var paths = Ids.ToDictionary(id => id, id => CopyFixture(fixture, root, id + "-" + mode));
            var agents = await new PortableAdditionalAiDiscovery(Path.Combine(root, mode), name => paths.GetValueOrDefault(name)!, true)
                .DiscoverAsync(CancellationToken.None);
            Require(agents.Select(a => a.Name).SequenceEqual(new[] { "Claude Code", "OpenCode", "Grok Build" }) &&
                agents.Select(a => a.Protocol).SequenceEqual(new[] { "claude", "acp", "acp" }) &&
                agents.All(a => a.Strategy == "native" && a.Status == expected), "Additional CLI identity/protocol/status mismatch");
            Require(agents.All(a => (a.PortableStartInfo is not null) == (expected == "available")), "Unverified CLI exposed a native launcher");
            Require(!JsonSerializer.Serialize(agents).Contains("fixture-sensitive-output-sentinel", StringComparison.Ordinal),
                "Probe output leaked into installation metadata");
            if (mode == "valid") Require(agents.All(a => a.Version == "2.1.3"), "Version metadata retained untrusted banner text");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await new PortableAdditionalAiDiscovery(root, _ => throw new InvalidOperationException("Cancelled discovery performed a lookup"), true)
                .DiscoverAsync(cancelled.Token);
            throw new InvalidOperationException("Discovery ignored caller cancellation");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS additional discovery: stable brands/protocols, missing/mismatch/unsupported/probe-failed statuses, cancellation and bounded metadata");
    }

    private static async Task LauncherAndIsolationAsync(string root, string fixture)
    {
        PrivateDirectory(root);
        var paths = Ids.ToDictionary(id => id, id => CopyFixture(fixture, root, id + "-valid"));
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalVariables = ExcludedEnvironment.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var key in ExcludedEnvironment) Environment.SetEnvironmentVariable(key, "fixture-must-not-be-inherited");
            var account = Path.Combine(root, "account A");
            var installations = await new PortableAdditionalAiDiscovery(account, name => paths.GetValueOrDefault(name)!, true)
                .DiscoverAsync(CancellationToken.None);
            var other = await new PortableAdditionalAiDiscovery(Path.Combine(root, "account B"), name => paths.GetValueOrDefault(name)!, true)
                .DiscoverAsync(CancellationToken.None);
            foreach (var agent in installations)
            {
                Require(agent.Status == "available", "Isolated native fixture failed its version/help environment assertions");
                var arguments = LaunchArguments(agent.Id);
                var start = AiAgentDiscovery.StartInfo(agent, root, arguments);
                Require(start.FileName == paths[agent.Id] && start.Arguments.Length == 0 && !start.UseShellExecute &&
                    start.ArgumentList.SequenceEqual(arguments) && start.WorkingDirectory == root && start.RedirectStandardInput &&
                    start.RedirectStandardOutput && start.RedirectStandardError, "Native launcher changed literal argv, directory or pipe ownership");
                Require((start.Environment.TryGetValue("HOME", out var childHome) ? childHome : null) == originalHome, "Provider isolation repurposed HOME");
                foreach (var key in ExcludedEnvironment)
                    Require(!start.Environment.TryGetValue(key, out var value) || value != "fixture-must-not-be-inherited",
                        "Provider launcher inherited foreign configuration or credential environment");
                Require(!start.Environment["PATH"]!.Split(Path.PathSeparator).Any(path => !Path.IsPathFullyQualified(path)),
                    "Relative search path reached a native launcher");
                var runtime = Path.Combine(account, agent.Id + "-runtime");
                if (agent.Id == "opencode")
                {
                    foreach (var (key, child) in new[] { ("XDG_CONFIG_HOME", "config"), ("XDG_DATA_HOME", "data"),
                        ("XDG_STATE_HOME", "state"), ("XDG_CACHE_HOME", "cache") })
                        Require(start.Environment[key] == Path.Combine(runtime, child), "OpenCode escaped an account XDG directory");
                    Require(agent.ConfigurationDataDirectory == Path.Combine(runtime, "config", "opencode") &&
                        agent.SessionDataDirectory == Path.Combine(runtime, "data", "opencode"), "OpenCode metadata omitted its provider subdirectory");
                }
                else
                {
                    Require(start.Environment[agent.Id == "claude" ? "CLAUDE_CONFIG_DIR" : "GROK_HOME"] == runtime &&
                        agent.ConfigurationDataDirectory == runtime && agent.SessionDataDirectory == runtime,
                        "Provider launch and metadata disagree on account isolation");
                }
                if (agent.Id == "claude") Require(start.Environment["ANTHROPIC_CONFIG_DIR"] == Path.Combine(runtime, "anthropic") &&
                    !start.Environment.ContainsKey("ANTHROPIC_PROFILE"), "Claude fell back to a global Anthropic profile");
                if (agent.Id == "grok")
                {
                    Require(start.Environment["GROK_DISABLE_AUTOUPDATER"] == "1", "Grok updater was enabled");
                    foreach (var product in new[] { "CURSOR", "CLAUDE" })
                        foreach (var kind in new[] { "SKILLS", "RULES", "AGENTS", "MCPS", "HOOKS" })
                            Require(start.Environment["GROK_" + product + "_" + kind + "_ENABLED"] == "false", "Grok inherited foreign provider settings scanners");
                }
                var result = await ExecuteAsync(start);
                Require(result.ExitCode == 0 && result.Output.Trim() == "fixture-launch-ok", "Native fixture rejected launcher isolation/arguments");
                var tracePath = Path.Combine(agent.Id == "opencode" ? Path.Combine(runtime, "state") : runtime, "fixture.trace");
                var trace = await File.ReadAllLinesAsync(tracePath);
                Require(trace.Length == 3 && trace[0] == "--version" && trace[1] == (agent.Id == "claude" ? "--help" : agent.Id == "opencode" ? "acp|--help" : "agent|--help") &&
                    trace[2] == string.Join('|', arguments), "Discovery ran unexpected commands or launcher shell-parsed arguments");
                Require(other.Single(a => a.Id == agent.Id).SessionDataDirectory != agent.SessionDataDirectory,
                    "Two accounts share a provider data directory");
                var privateDirectories = new[] { runtime }.Concat(Directory.EnumerateDirectories(runtime, "*", SearchOption.AllDirectories));
                foreach (var directory in privateDirectories)
                    if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                        Require(File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                            "A provider runtime directory is not private");
                Require(!Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories).Any(path => Path.GetFileName(path) != "fixture.trace"),
                    "Discovery copied configuration or authentication data");
            }
            Require(originalVariables.Keys.All(key => Environment.GetEnvironmentVariable(key) == "fixture-must-not-be-inherited"),
                "Child isolation mutated the host environment");
        }
        finally
        {
            foreach (var (key, value) in originalVariables) Environment.SetEnvironmentVariable(key, value);
        }
        Console.WriteLine("PASS additional launchers: native argv and probes, account separation, 0700 directories, unchanged HOME, no inherited tokens/config overrides");
    }

    private static async Task ShimAndPathValidationAsync(string root, string fixture)
    {
        PrivateDirectory(root);
        var runtime = CopyFixture(fixture, root, "fixture-node");
        foreach (var id in Ids)
        {
            var shim = Path.Combine(root, id + "-valid.fixture");
            await File.WriteAllTextAsync(shim, "#!/usr/bin/env -S node --no-warnings\n// offline interpreter fixture\n");
            Executable(shim);
            var available = (await new PortableAdditionalAiDiscovery(Path.Combine(root, id + "-node"),
                name => name == id ? shim : name == "node" ? runtime : null!, true).DiscoverAsync(CancellationToken.None)).Single(a => a.Id == id);
            Require(available.Status == "available" && available.RuntimePath == runtime, "Public Node shim did not resolve a launchable runtime");
            var start = AiAgentDiscovery.StartInfo(available, root, LaunchArguments(id));
            Require(start.FileName == runtime && start.ArgumentList.SequenceEqual(new[] { "--no-warnings", shim }.Concat(LaunchArguments(id))),
                "Node interpreter options/script path/protocol argument ordering changed");
            Require((await ExecuteAsync(start)).ExitCode == 0, "Node shim fixture failed launch isolation");
            var missing = (await new PortableAdditionalAiDiscovery(Path.Combine(root, id + "-runtime-missing"), name => name == id ? shim : null!, true)
                .DiscoverAsync(CancellationToken.None)).Single(a => a.Id == id);
            Require(missing.Status == "runtime_missing" && missing.PortableStartInfo is null, "Missing public interpreter lost runtime_missing status");
            var shell = Path.Combine(root, id + "-shell");
            var token = id == "claude" ? "stream-json" : id == "opencode" ? "ACP" : "stdio";
            await File.WriteAllTextAsync(shell, "#!/bin/sh\ncase \"$1\" in --version) echo '" + id + " 2.1.3';; *) echo '" + token + "';; esac\n");
            Executable(shell);
            var incompatible = (await new PortableAdditionalAiDiscovery(Path.Combine(root, id + "-shell-account"), name => name == id ? shell : null!, true)
                .DiscoverAsync(CancellationToken.None)).Single(a => a.Id == id);
            Require(incompatible.Status == "incompatible" && incompatible.PortableStartInfo is null, "Shell shim became a remote native launcher");
            foreach (var path in new[] { "relative-cli", "alias " + id + "='untrusted command'" })
            {
                var invalid = (await new PortableAdditionalAiDiscovery(Path.Combine(root, id + "-invalid"), name => name == id ? path : null!, true)
                    .DiscoverAsync(CancellationToken.None)).Single(a => a.Id == id);
                Require(invalid.Status == "probe_failed" && invalid.PortableStartInfo is null, "Alias or relative command was accepted as a native executable");
            }
            var unsafeShim = Path.Combine(root, id + "-unsafe.fixture");
            await File.WriteAllTextAsync(unsafeShim, "#!/usr/bin/env -S node -e untrustedCode()\n");
            Executable(unsafeShim);
            var rejected = (await new PortableAdditionalAiDiscovery(Path.Combine(root, id + "-unsafe-account"),
                name => name == id ? unsafeShim : name == "node" ? runtime : null!, true).DiscoverAsync(CancellationToken.None)).Single(a => a.Id == id);
            Require(rejected.Status == "probe_failed" && rejected.PortableStartInfo is null, "Unsafe Node eval shim was accepted");
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var blocked = (await new PortableAdditionalAiDiscovery(Path.Combine(root, id + "-blocked"), name => name == id ? shim : null!, true)
                .DiscoverAsync(CancellationToken.None)).Single(a => a.Id == id);
            Require(blocked.Status == "runtime_blocked" && blocked.PortableStartInfo is null, "Non-executable CLI lost runtime_blocked status");
            try { AiAgentDiscovery.StartInfo(available, root, LaunchArguments(id)); throw new InvalidOperationException("Launcher used a changed executable"); }
            catch (IOException) { }
        }
        Console.WriteLine("PASS additional path validation: public Node argv, missing interpreter, shell/alias/relative/eval rejection, executable permission and revalidation");
    }

    private static async Task CatalogAsync(string root, string fixture)
    {
        PrivateDirectory(root);
        var paths = Ids.ToDictionary(id => id, id => CopyFixture(fixture, root, id + "-valid"));
        var codex = new AiAgentInstallation("codex", "Codex", "", "", "", "codex", "missing");
        var zcode = new AiAgentInstallation("zcode", "ZCode", "", "", "", "zcode", "missing");
        using var deterministic = new PortableAiHost(root, () => Task.FromResult(codex), () => Task.FromResult(zcode));
        using var legacy = JsonDocument.Parse(await deterministic.ExecuteAsync(Guid.NewGuid(), "{\"action\":\"catalog\"}", CancellationToken.None, () => true));
        Require(legacy.RootElement.GetProperty("agents").GetArrayLength() == 2, "Existing injected host began machine discovery");
        var discovery = new PortableAdditionalAiDiscovery(root, name => paths.GetValueOrDefault(name)!, true);
        using var host = new PortableAiHost(root, () => Task.FromResult(codex), () => Task.FromResult(zcode), () => discovery.DiscoverAsync(CancellationToken.None));
        var json = await host.ExecuteAsync(Guid.NewGuid(), "{\"action\":\"catalog\"}", CancellationToken.None, () => true);
        using var document = JsonDocument.Parse(json);
        var agents = document.RootElement.GetProperty("agents").EnumerateArray().ToArray();
        Require(agents.Select(a => a.GetProperty("id").GetString()).SequenceEqual(new[] { "codex", "claude", "opencode", "grok", "zcode" }),
            "Additional discovery was not combined with existing native entries");
        Require(agents.Where(a => Ids.Contains(a.GetProperty("id").GetString())).All(a => a.GetProperty("status").GetString() == "available"),
            "Protocol-available additional CLIs disappeared from the catalog");
        Require(!json.Contains("fixture-sensitive-output-sentinel", StringComparison.Ordinal) && !json.Contains("verified", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("ConfigurationDataDirectory", StringComparison.OrdinalIgnoreCase) && !json.Contains("remoteUrl", StringComparison.Ordinal),
            "Catalog leaked private/probe metadata or asserted verified authentication");
        Console.WriteLine("PASS additional catalog: original injected 2-entry contract and explicit 5-brand native catalog; authentication not asserted");
    }

    private static async Task DirectorySafetyAsync(string root, string fixture)
    {
        PrivateDirectory(root);
        var account = Path.Combine(root, "account");
        var foreign = Path.Combine(root, "foreign");
        PrivateDirectory(account);
        Directory.CreateDirectory(foreign);
        var link = Path.Combine(account, "claude-runtime");
        Directory.CreateSymbolicLink(link, foreign);
        var claude = CopyFixture(fixture, root, "claude-valid");
        var result = (await new PortableAdditionalAiDiscovery(account, name => name == "claude" ? claude : null!, true)
            .DiscoverAsync(CancellationToken.None)).Single(a => a.Id == "claude");
        Require(result.Status != "available" && result.PortableStartInfo is null && !Directory.EnumerateFileSystemEntries(foreign).Any(),
            "Linked runtime escaped the account root");
        Directory.Delete(link);
        var valid = (await new PortableAdditionalAiDiscovery(account, name => name == "claude" ? claude : null!, true)
            .DiscoverAsync(CancellationToken.None)).Single(a => a.Id == "claude");
        Require(valid.Status == "available", "Runtime fixture did not recover after linked-directory rejection");
        Directory.Delete(link, recursive: true);
        Directory.CreateSymbolicLink(link, foreign);
        try { AiAgentDiscovery.StartInfo(valid, root, LaunchArguments("claude")); throw new InvalidOperationException("Launcher followed a replaced account directory"); }
        catch (IOException) { }
        Directory.Delete(link);
        var accountLink = Path.Combine(root, "linked-account");
        Directory.CreateSymbolicLink(accountLink, account);
        try { _ = new PortableAdditionalAiDiscovery(accountLink, _ => null!, true); throw new InvalidOperationException("Linked account was accepted"); }
        catch (IOException) { }
        Directory.Delete(accountLink);
        Console.WriteLine("PASS additional private directories: account/runtime symlinks rejected before probes and at launch");
    }

    private static string[] LaunchArguments(string id) => id switch
    {
        "claude" => ["-p", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json"],
        "opencode" => ["acp"],
        _ => ["--permission-mode", "default", "agent", "--no-leader", "stdio"],
    };

    private static async Task<string> BuildFixtureAsync(string root)
    {
        PrivateDirectory(root);
        var compiler = File.Exists("/usr/bin/cc") ? "/usr/bin/cc" : ExecutableLocator.Find("cc") ?? throw new InvalidOperationException("Native fixture requires an installed C compiler");
        var path = Path.Combine(root, "native-fixture");
        var start = new ProcessStartInfo(compiler)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-x", "c", "-", "-o", path }) start.ArgumentList.Add(argument);
        var result = await ExecuteAsync(start, FixtureSource);
        Require(result.ExitCode == 0, "Offline native fixture compilation failed");
        Executable(path);
        return path;
    }

    private static string CopyFixture(string fixture, string root, string name)
    {
        PrivateDirectory(root);
        var path = Path.Combine(root, name);
        File.Copy(fixture, path);
        Executable(path);
        return path;
    }
    private static void Executable(string path)
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<(int ExitCode, string Output)> ExecuteAsync(ProcessStartInfo start, string? input = null)
    {
        using var process = Process.Start(start) ?? throw new IOException("Offline fixture process did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
            return (process.ExitCode, await stdout);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private const string FixtureSource = """
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>
        static int contains(int argc, char **argv, const char *value) {
            for (int i = 1; i < argc; i++) if (!strcmp(argv[i], value)) return 1;
            return 0;
        }
        int main(int argc, char **argv) {
            const char *file = strrchr(argv[0], '/'); file = file ? file + 1 : argv[0];
            int first = 1;
            for (int i = 1; i < argc; i++) {
                if (strstr(argv[i], ".fixture")) {
                    file = strrchr(argv[i], '/'); file = file ? file + 1 : argv[i];
                    first = i + 1;
                }
            }
            const char *id = strstr(file, "claude") ? "claude" : strstr(file, "opencode") ? "opencode" : "grok";
            const char *dir = getenv(!strcmp(id, "claude") ? "CLAUDE_CONFIG_DIR" : !strcmp(id, "opencode") ? "XDG_STATE_HOME" : "GROK_HOME");
            if (!dir || strstr(dir, "fixture-must-not-be-inherited")) return 91;
            const char *secrets[] = {"OPENAI_API_KEY", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "XAI_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN",
                "CLAUDE_CODE_OAUTH_REFRESH_TOKEN", "OPENAI_ACCESS_TOKEN", "CHATGPT_ACCESS_TOKEN", "GROK_ACCESS_TOKEN", "OPENCODE_API_KEY",
                "CUSTOM_PROVIDER_CREDENTIAL", "TERMLINK_DISCOVERY_PRIVATE_SENTINEL", "AWS_SECRET_ACCESS_KEY", "GOOGLE_APPLICATION_CREDENTIALS",
                "OPENCODE_CONFIG", "OPENCODE_CONFIG_CONTENT", "OPENCODE_CONFIG_DIR", "OPENCODE_DB", "OPENCODE_TEST_HOME", "ANTHROPIC_PROFILE", "NODE_OPTIONS", "NODE_PATH", "BASH_ENV", "ENV", "ZDOTDIR"};
            for (size_t i = 0; i < sizeof(secrets)/sizeof(secrets[0]); i++) if (getenv(secrets[i])) return 92;
            if (!strcmp(id, "claude") && (!getenv("ANTHROPIC_CONFIG_DIR") || !strstr(getenv("ANTHROPIC_CONFIG_DIR"), dir))) return 97;
            if (!strcmp(id, "grok") && (!getenv("GROK_DISABLE_AUTOUPDATER") || strcmp(getenv("GROK_DISABLE_AUTOUPDATER"), "1"))) return 93;
            if (!strcmp(id, "opencode") && (!getenv("XDG_CONFIG_HOME") || !getenv("XDG_DATA_HOME") || !getenv("XDG_CACHE_HOME"))) return 94;
            char trace[8192];
            if (snprintf(trace, sizeof(trace), "%s/fixture.trace", dir) >= (int)sizeof(trace)) return 95;
            FILE *out = fopen(trace, "a"); if (!out) return 96;
            for (int i = first; i < argc; i++) fprintf(out, "%s%s", i == first ? "" : "|", argv[i]);
            fprintf(out, "\n"); fclose(out);
            if (contains(argc, argv, "--version")) {
                if (strstr(file, "versionfail")) { fprintf(stderr, "fixture-sensitive-output-sentinel\n"); return 2; }
                printf("%s 2.1.3 fixture-sensitive-output-sentinel\n", strstr(file, "mismatch") ? "Codex" : id);
                return 0;
            }
            if (contains(argc, argv, "--help")) {
                if (strstr(file, "helpfail")) { fprintf(stderr, "fixture-sensitive-output-sentinel\n"); return 2; }
                if ((!strcmp(id, "opencode") && !contains(argc, argv, "acp")) || (!strcmp(id, "grok") && !contains(argc, argv, "agent"))) return 3;
                puts(strstr(file, "unsupported") ? "No structured protocol" : !strcmp(id, "claude") ? "--input-format stream-json --output-format stream-json" : !strcmp(id, "opencode") ? "ACP agent" : "stdio ACP transport");
                return 0;
            }
            puts("fixture-launch-ok");
            return 0;
        }
        """;
    private static void PrivateDirectory(string path) {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

}
