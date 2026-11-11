// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Agent.AI;
using TermLink.Ai;
using TermLink.Platform.Unix;

internal static class PortableCodexValidation
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await DiscoveryAndIsolationAsync(Path.Combine(root, "discovery"));
        await CatalogAndSessionAsync(Path.Combine(root, "session"));
        ValidateWindowsStartInfoUnchanged();
        Console.WriteLine("PASS portable Codex fixtures: discovery/native catalog, isolated launcher, initialize/control/approval/interrupt/end/dispose; no real model or credentials");
    }

    private static async Task DiscoveryAndIsolationAsync(string root)
    {
        using var scope = new FixtureScope(root);
        var missing = await new PortableCodexDiscovery(root, _ => null, supportedPlatform: true).DiscoverAsync(CancellationToken.None);
        Require(missing is { Id: "codex", Status: "missing", Strategy: "native" }, "Missing Codex did not preserve a native missing entry");
        var discovery = new PortableCodexDiscovery(root, name => name == "codex" ? Environment.ProcessPath! : null, supportedPlatform: true);
        foreach (var (mode, expected) in new[] { ("mismatch", "incompatible"), ("unsupported", "incompatible"), ("valid", "available") })
        {
            scope.Mode = mode;
            var agent = await discovery.DiscoverAsync(CancellationToken.None);
            Require(agent.Status == expected && agent.Strategy == "native" && agent.Protocol == "codex", "Codex probe status mismatch");
            if (expected != "available") Require(agent.PortableStartInfo is null, "Unverified Codex exposed a native launcher");
        }
        scope.Mode = "valid";
        var installation = await discovery.DiscoverAsync(CancellationToken.None);
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            // This is a fake marker, not a real credential. The child must never
            // inherit even this marker as implicit authorization.
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "fixture-must-not-be-inherited");
            var start = AiAgentDiscovery.StartInfo(installation, scope.Workspace, "app-server");
            Require(start.FileName == Environment.ProcessPath && start.ArgumentList[0] == "app-server" &&
                start.Arguments == "" && !start.UseShellExecute, "The verified absolute executable was replaced or shell-parsed");
            Require(start.Environment["CODEX_HOME"] == Path.Combine(root, "codex-runtime") &&
                start.Environment["CODEX_SQLITE_HOME"] == Path.Combine(root, "codex-runtime", "sqlite") &&
                !start.Environment.ContainsKey("OPENAI_API_KEY") && start.ArgumentList.Contains("cli_auth_credentials_store=\"file\""),
                "Codex runtime fell back to user config/auth/SQLite/provider environment");
            Require(!start.Environment["PATH"]!.Split(Path.PathSeparator).Any(path => !Path.IsPathFullyQualified(path)), "Unsafe PATH reached app-server");
            var models = await AiAgentSession.ListModelsAsync(installation, CancellationToken.None);
            Require(models.Models.Single().ModelId == "fixture-model" && scope.Count("rpc:thread/start") == 0,
                "Catalog-only initialization started a thread or lost model parsing");
        }
        finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
        foreach (var path in new[] { installation.SessionDataDirectory, Path.Combine(installation.SessionDataDirectory, "sqlite") })
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                Require(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Codex runtime is not private");
        Require(!File.Exists(Path.Combine(installation.SessionDataDirectory, "auth.json")) && !File.Exists(Path.Combine(installation.SessionDataDirectory, "config.toml")),
            "Discovery copied a user's auth/config files");
        var descriptor = PosixCliLaunchDescriptor.Resolve(Environment.ProcessPath!);
        Require(descriptor.NativeAgentCompatible && descriptor.PrefixArguments.Count == 0 && descriptor.CliPath == Environment.ProcessPath,
            "Native binary descriptor was not retained");
        var shell = Path.Combine(root, "shell shim ; literal");
        await File.WriteAllTextAsync(shell, "#!/bin/sh\nif [ \"$1\" = --version ]; then printf 'codex-cli 0.162.1\\n'; else printf 'app-server\\n'; fi\n");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var rejected = await new PortableCodexDiscovery(root, name => name == "codex" ? shell : null, supportedPlatform: true).DiscoverAsync(CancellationToken.None);
        Require(rejected.Status == "incompatible" && rejected.PortableStartInfo is null, "A shell shim became a remote native agent");
        var nodeShim = Path.Combine(root, "npm shim ; $(literal) with spaces");
        await File.WriteAllTextAsync(nodeShim, "#!/usr/bin/env -S node --no-warnings\n// fixture interpreter owns the behavior\n");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) File.SetUnixFileMode(nodeShim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var nodeAgent = await new PortableCodexDiscovery(root,
            name => name == "codex" ? nodeShim : name == "node" ? Environment.ProcessPath! : null,
            supportedPlatform: true).DiscoverAsync(CancellationToken.None);
        Require(nodeAgent.Status == "available" && nodeAgent.RuntimePath == Environment.ProcessPath, "Public Node shim was not resolved to the validated runtime");
        var nodeStart = AiAgentDiscovery.StartInfo(nodeAgent, scope.Workspace, "app-server");
        Require(nodeStart.FileName == Environment.ProcessPath && nodeStart.ArgumentList.Take(3).SequenceEqual(new[] { "--no-warnings", nodeShim, "app-server" }) && nodeStart.Arguments == "",
            "Node option/script path/app-server order or literal metacharacters changed");
        var nodeModels = await AiAgentSession.ListModelsAsync(nodeAgent, CancellationToken.None);
        Require(nodeModels.Models.Single().ModelId == "fixture-model", "Resolved Node launcher did not initialize the same protocol");
        var unsafeNode = Path.Combine(root, "node-eval-shim");
        await File.WriteAllTextAsync(unsafeNode, "#!/usr/bin/env -S node -e arbitraryCode()\n// never execute\n");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) File.SetUnixFileMode(unsafeNode, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var unsafeAgent = await new PortableCodexDiscovery(root,
            name => name == "codex" ? unsafeNode : name == "node" ? Environment.ProcessPath! : null,
            supportedPlatform: true).DiscoverAsync(CancellationToken.None);
        Require(unsafeAgent.Status == "probe_failed" && unsafeAgent.PortableStartInfo is null, "Node eval/preload arguments were executable shim options");
        await scope.RequireStoppedAsync();
        Console.WriteLine("PASS Codex discovery: missing/wrong-version/unsupported help, native path, isolated private homes, no credential fallback or thread during model listing");
    }

    private static async Task CatalogAndSessionAsync(string root)
    {
        using var scope = new FixtureScope(root);
        var codex = await new PortableCodexDiscovery(root, name => name == "codex" ? Environment.ProcessPath! : null, supportedPlatform: true).DiscoverAsync(CancellationToken.None);
        var zcode = new AiAgentInstallation("zcode", "ZCode", "", "", "", "zcode", "missing");
        using var host = new PortableAiHost(root, () => Task.FromResult(codex), () => Task.FromResult(zcode));
        var owner = Guid.NewGuid();
        var catalog = Parse(await host.ExecuteAsync(owner, "{\"action\":\"catalog\"}", CancellationToken.None, () => true));
        var agents = catalog.GetProperty("agents").EnumerateArray().ToArray();
        Require(agents.Length == 2 && Text(agents[0], "id") == "codex" && Text(agents[0], "status") == "available" &&
            Text(agents[1], "id") == "zcode" && Text(agents[1], "status") == "missing" && agents.All(a => Text(a, "strategy") == "native"),
            "Portable host did not combine Codex with the existing ZCode native entry");
        Require(!catalog.GetRawText().Contains("remoteUrl", StringComparison.Ordinal) && !catalog.GetRawText().Contains("verified", StringComparison.OrdinalIgnoreCase),
            "Protocol catalog advertised retired URLs or verified login");
        var hostId = Text(catalog, "hostId");
        var create = new AiSessionManager.Request("create", hostId, Guid.NewGuid().ToString(), AgentId: "codex", Directory: scope.Workspace, DirectoryMode: "custom");
        var denied = Parse(await host.ExecuteAsync(owner, AiSessionManager.Encode(create), CancellationToken.None, () => false));
        Require(Text(denied, "error") == "AUTHORITY_CHANGED" && scope.Count("rpc:thread/start") == 0, "Unauthorized Codex create launched a thread");
        var createBody = AiSessionManager.Encode(create);
        var created = Parse(await host.ExecuteAsync(owner, createBody, CancellationToken.None, () => true));
        Success(created); var id = Text(created.GetProperty("session"), "id");
        Require(Text(created.GetProperty("session"), "authentication") != "verified", "Initialize was treated as model authorization");
        var repeated = Parse(await host.ExecuteAsync(owner, createBody, CancellationToken.None, () => true));
        Success(repeated);
        Require(scope.Count("rpc:thread/start") == 1 && scope.Count("approval-policy:on-request") == 1 && scope.Count("sandbox:workspace-write") == 1,
            "Codex create retry duplicated a worker or weakened default permissions");
        var send = new AiSessionManager.Request("send", hostId, Guid.NewGuid().ToString(), id, Text: "approval", ControlVersion: 1);
        Success(Parse(await host.ExecuteAsync(owner, AiSessionManager.Encode(send), CancellationToken.None, () => true)));
        var pending = await Poll(host, owner, hostId, id, value => value.GetProperty("pending").GetArrayLength() == 1);
        var approval = Text(pending.GetProperty("pending")[0], "id");
        var foreign = Guid.NewGuid();
        var invalid = Parse(await host.ExecuteAsync(foreign, AiSessionManager.Encode(new AiSessionManager.Request("answer", hostId,
            Guid.NewGuid().ToString(), id, RequestId: approval, Decision: "allow", ControlVersion: 1)), CancellationToken.None, () => true));
        Require(Text(invalid, "error") == "CONTROL_CHANGED" && scope.Count("decision:accept") == 0, "A foreign controller approved a Codex interaction");
        Success(Parse(await host.ExecuteAsync(owner, AiSessionManager.Encode(new AiSessionManager.Request("answer", hostId,
            Guid.NewGuid().ToString(), id, RequestId: approval, Decision: "deny", ControlVersion: 1)), CancellationToken.None, () => true)));
        await Poll(host, owner, hostId, id, value => Text(value, "state") == "idle");
        Require(scope.Count("decision:decline") == 1, "Pending Codex approval was not denied through the protocol");
        Success(Parse(await host.ExecuteAsync(owner, AiSessionManager.Encode(new AiSessionManager.Request("send", hostId,
            Guid.NewGuid().ToString(), id, Text: "hold", ControlVersion: 1)), CancellationToken.None, () => true)));
        Success(Parse(await host.ExecuteAsync(owner, AiSessionManager.Encode(new AiSessionManager.Request("interrupt", hostId,
            Guid.NewGuid().ToString(), id, ControlVersion: 1)), CancellationToken.None, () => true)));
        Require(scope.Count("interrupted") == 1, "Codex interrupt did not reach the owned process");
        Success(Parse(await host.ExecuteAsync(owner, AiSessionManager.Encode(new AiSessionManager.Request("end", hostId,
            Guid.NewGuid().ToString(), id, ControlVersion: 1)), CancellationToken.None, () => true)));
        host.Dispose();
        await scope.RequireStoppedAsync();
        Console.WriteLine("PASS Codex host: native catalog/ZCode preserved, authorization and operation receipts, initialize then thread, pending approval ownership/deny, interrupt/end/disposal");
    }

    private static void ValidateWindowsStartInfoUnchanged()
    {
        // This path does not inject the portable factory. The public legacy
        // binary/runtime argument behavior remains identical on every host.
        var legacy = new AiAgentInstallation("codex", "Codex", "/fixture/CLI path with spaces", "/fixture/runtime", "0.162.1", "codex", "available");
        var start = AiAgentDiscovery.StartInfo(legacy, Path.GetTempPath(), "app-server");
        Require(start.FileName == legacy.RuntimePath && start.ArgumentList.SequenceEqual(new[] { legacy.Path, "app-server" }) && !start.UseShellExecute,
            "Existing Windows/runtime StartInfo behavior changed");
        var cmd = legacy with { Path = @"C:\Program Files\Codex\codex.cmd", RuntimePath = "" };
        var shim = AiAgentDiscovery.StartInfo(cmd, Path.GetTempPath(), "app-server");
        Require(shim.FileName == Path.Combine(Environment.SystemDirectory, "cmd.exe") &&
            shim.Arguments == "/d /s /c \"\"" + cmd.Path + "\" app-server\"" && shim.ArgumentList.Count == 0,
            "Windows cmd launcher quoting changed");
        try { AiAgentDiscovery.StartInfo(cmd with { Path = @"C:\Codex&unsafe.cmd" }, Path.GetTempPath(), "app-server"); throw new InvalidOperationException("Unsafe cmd path accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS legacy launch mapping regression: runtime/path argument order, cmd quoting/rejection and no shell changes");
    }

    private static async Task<JsonElement> Poll(PortableAiHost host, Guid caller, string hostId, string id, Func<JsonElement, bool> predicate)
    {
        for (var i = 0; i < 200; i++)
        {
            var value = Parse(await host.ExecuteAsync(caller, AiSessionManager.Encode(new AiSessionManager.Request("poll", hostId, SessionId: id)), CancellationToken.None, () => true));
            Success(value); var session = value.GetProperty("session");
            if (predicate(session)) return session;
            await Task.Delay(20);
        }
        throw new TimeoutException("Codex fixture did not reach expected state");
    }
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
    private static void Success(JsonElement value) => Require(Text(value, "error").Length == 0, "Fixture protocol operation failed: " + Text(value, "error"));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FixtureScope : IDisposable
    {
        private readonly string? priorTrace = Environment.GetEnvironmentVariable(PortableCodexFixture.TraceVariable);
        private readonly string? priorMode = Environment.GetEnvironmentVariable(PortableCodexFixture.ModeVariable);
        private readonly string trace;
        internal string Workspace { get; }
        internal string Mode { set => Environment.SetEnvironmentVariable(PortableCodexFixture.ModeVariable, value); }
        private string[] Lines => File.Exists(trace) ? File.ReadAllLines(trace) : [];
        private int[] Pids => Lines.Where(line => line.StartsWith("pid:", StringComparison.Ordinal)).Select(line => int.Parse(line[4..])).Distinct().ToArray();
        internal FixtureScope(string root)
        {
            Directory.CreateDirectory(root); Workspace = Path.Combine(root, "workspace with spaces ; literal"); Directory.CreateDirectory(Workspace);
            trace = Path.Combine(root, "codex-trace.txt");
            Environment.SetEnvironmentVariable(PortableCodexFixture.TraceVariable, trace);
            Mode = "valid";
        }
        internal int Count(string value) => Lines.Count(line => line == value);
        internal async Task RequireStoppedAsync()
        {
            for (var i = 0; i < 200 && Pids.Any(IsRunning); i++) await Task.Delay(20);
            Require(Pids.All(pid => !IsRunning(pid)), "Codex test left an owned worker running");
        }
        private static bool IsRunning(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; } }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PortableCodexFixture.TraceVariable, priorTrace);
            Environment.SetEnvironmentVariable(PortableCodexFixture.ModeVariable, priorMode);
            foreach (var pid in Pids) try { using var p = Process.GetProcessById(pid); if (!p.HasExited) { p.Kill(true); p.WaitForExit(3000); } }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
