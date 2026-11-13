// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using TermLink.Agent.AI;
using TermLink.Ai;
using TermLink.Platform.Unix;

/// <summary>Authored native Node fixture only; never executes an installed Node or Electron.</summary>
internal static class PortableZcodeNodeValidation
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Directory.CreateDirectory(root);
        var app = Path.Combine(root, "ZCode ; $(literal) App.app");
        var scripts = Path.Combine(app, "Contents", "Resources", "glm");
        Directory.CreateDirectory(scripts);
        var script = Path.Combine(scripts, "zcode.cjs");
        await File.WriteAllTextAsync(script, "// app-server fixture, not interpreted");
        var publicConfig = Path.Combine(app, "Contents", "Resources", "config", "provider");
        Directory.CreateDirectory(publicConfig);
        await File.WriteAllTextAsync(Path.Combine(publicConfig, "zcode-builtin.json"), "{}");
        var logs = Path.Combine(root, "trace"); Directory.CreateDirectory(logs);
        var source = Path.Combine(root, "fake-node.c");
        await File.WriteAllTextAsync(source, """
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>
        #include <unistd.h>
        int main(int argc,char **argv) {
          char mode[4096], trace[4096],name[4096];
          const char *end=strrchr(argv[0],'/'); if(!end)return 61;
          snprintf(mode,sizeof(mode),"%.*s/mode",(int)(end-argv[0]),argv[0]);
          snprintf(trace,sizeof(trace),"%.*s/trace",(int)(end-argv[0]),argv[0]);
          FILE *f=fopen(trace,"a"); if(!f)return 62;
          for(int i=0;i<argc;i++)fprintf(f,"arg%d=%s\n",i,argv[i]);
          fprintf(f,"electron=%s\npid=%ld\n",getenv("ELECTRON_RUN_AS_NODE")?getenv("ELECTRON_RUN_AS_NODE"):"absent",(long)getpid()); fclose(f);
          char behavior[64]="valid";f=fopen(mode,"r");if(f){fgets(behavior,sizeof(behavior),f);fclose(f);}
          if(argc==2&&!strcmp(argv[1],"--version")){
            if(!strcmp(behavior,"bad-version")){puts("ZCode Desktop 3.17.2");return 0;}
            if(!strcmp(behavior,"bare-version")){puts("26.13.0");return 0;}
            if(!strcmp(behavior,"node-failure"))return 7;
            puts("v26.13.0");return 0;
          }
          const char *base=getenv("ZCODE_DATA_BASE_DIR"),*config=getenv("ZCODE_PERSONAL_PROVIDER_CONFIG_FILE");
          if(!base||!config||strncmp(base,config,strlen(base))||getenv("ELECTRON_RUN_AS_NODE"))return 63;
          if(argc!=3||!strstr(argv[1],"/Contents/Resources/glm/zcode.cjs"))return 64;
          if(!strcmp(behavior,"hang")){for(;;)sleep(1);}
          if(!strcmp(argv[2],"--help")){
            if(!strcmp(behavior,"cli-failure"))return 8;
            puts(!strcmp(behavior,"unsupported")?"no structured protocol":"app-server fixture");return 0;
          }
          if(!strcmp(argv[2],"--version")){puts("0.17.1");return 0;}
          if(!strcmp(argv[2],"app-server")){puts("fixture app-server only");return 0;}
          return 65;
        }
        """);
        var native = Path.Combine(root, "native Node ; whitespace"); Directory.CreateDirectory(native);
        var node = Path.Combine(native, "node");
        await CommandAsync("/usr/bin/cc", [source, "-o", node], root);
        var modePath = Path.Combine(native, "mode"); var tracePath = Path.Combine(native, "trace");
        var discoveries = 0;
        string? Find(string name) { discoveries++; return name == "node" ? node : null; }
        var noApp = await new PortableZcodeDiscovery(Path.Combine(root, "missing-account"), () => [], Find!).DiscoverAsync(CancellationToken.None);
        Require(noApp.Status == "missing" && discoveries == 0, "Missing App incorrectly became missing runtime or ran Node");
        var noNode = await new PortableZcodeDiscovery(Path.Combine(root, "no-node-account"), () => [app], _ => null!).DiscoverAsync(CancellationToken.None);
        Require(noNode.Status == "runtime_missing" && noNode.Path == script && noNode.RuntimePath == "" && noNode.PortableStartInfo is null,
            "Installed script with no Node was erased or exposed a launcher");
        Require(!File.Exists(tracePath), "No-Node discovery executed an external runtime");

        foreach (var (mode, expected, scriptAllowed) in new[] {
            ("bad-version", "runtime_missing", false), ("bare-version", "runtime_missing", false), ("node-failure", "runtime_missing", false),
            ("unsupported", "incompatible", true), ("cli-failure", "probe_failed", true), ("valid", "available", true) })
        {
            await File.WriteAllTextAsync(modePath, mode); File.Delete(tracePath);
            var installation = await new PortableZcodeDiscovery(Path.Combine(root, "account-" + mode), () => [app], Find!).DiscoverAsync(CancellationToken.None);
            Require(installation.Status == expected && (installation.PortableStartInfo is not null) == (mode == "valid"), "ZCode Node probe status mismatch: " + mode);
            var trace = await File.ReadAllTextAsync(tracePath);
            Require(trace.Contains("arg1=--version\n") && !trace.Contains("electron=1"), "Node branding probe or Electron isolation missing");
            Require(trace.Contains("arg1=" + script + "\n") == scriptAllowed, "Unverified Node executed CLI script");
            if (mode == "valid")
            {
                Require(installation.Version == "0.17.1" && installation.RuntimePath == node, "Node version replaced ZCode version or wrong runtime selected");
                var launch = AiAgentDiscovery.StartInfo(installation, root, "app-server");
                Require(launch.FileName == node && launch.Arguments == "" && launch.ArgumentList.SequenceEqual([script, "app-server"]) &&
                    !launch.UseShellExecute && launch.RedirectStandardInput && launch.RedirectStandardOutput && launch.RedirectStandardError &&
                    !launch.Environment.ContainsKey("ELECTRON_RUN_AS_NODE"), "Launcher escaped fixed Node argv/redirection");
                await ExecuteAsync(launch);
                File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Reject(() => AiAgentDiscovery.StartInfo(installation, root, "app-server"));
                File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var original = Path.Combine(native, "original-node"); File.Move(node, original);
                try
                {
                    var electron = Path.Combine(app, "Contents", "MacOS", "ZCode"); Directory.CreateDirectory(Path.GetDirectoryName(electron)!); File.Copy(original, electron);
                    File.CreateSymbolicLink(node, electron);
                    Reject(() => AiAgentDiscovery.StartInfo(installation, root, "app-server"));
                    File.Delete(node);
                }
                finally { if (File.Exists(node) || new FileInfo(node).LinkTarget != null) File.Delete(node); File.Move(original, node); }
                var savedScript = script + ".saved"; File.Move(script, savedScript); File.CreateSymbolicLink(script, savedScript);
                try { Reject(() => AiAgentDiscovery.StartInfo(installation, root, "app-server")); }
                finally { File.Delete(script); File.Move(savedScript, script); }
            }
        }
        await File.WriteAllTextAsync(modePath, "valid");
        foreach (var candidate in new[] { "relative/node", Path.Combine(app, "Contents", "MacOS", "ZCode"), "/bin/sh" })
        {
            File.Delete(tracePath);
            var result = await new PortableZcodeDiscovery(Path.Combine(root, "unsafe-" + Guid.NewGuid().ToString("N")), () => [app], _ => candidate).DiscoverAsync(CancellationToken.None);
            Require(result.Status == "runtime_missing" && !File.Exists(tracePath), "Unsafe runtime path was executed or promoted");
        }
        var shell = Path.Combine(root, "shim", "node"); Directory.CreateDirectory(Path.GetDirectoryName(shell)!);
        await File.WriteAllTextAsync(shell, "#!/bin/sh\nexit 99\n"); File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var rejectedShim = await new PortableZcodeDiscovery(Path.Combine(root, "shell-account"), () => [app], _ => shell).DiscoverAsync(CancellationToken.None);
        Require(rejectedShim.Status == "runtime_missing", "A Node shell shim became a native launcher");
        var nodejs = Path.Combine(native, "nodejs"); File.Copy(node, nodejs);
        var fallbackNode = await new PortableZcodeDiscovery(Path.Combine(root, "nodejs-account"), () => [app], name => name == "nodejs" ? nodejs : null!).DiscoverAsync(CancellationToken.None);
        Require(fallbackNode.Status == "available" && fallbackNode.RuntimePath == nodejs, "Native nodejs lookup fallback missing");
        // A symlink to a genuine external Node is resolved before it is retained.
        var linkRoot = Path.Combine(root, "link-bin"); Directory.CreateDirectory(linkRoot); var nodeLink = Path.Combine(linkRoot, "node"); File.CreateSymbolicLink(nodeLink, node);
        var linked = await new PortableZcodeDiscovery(Path.Combine(root, "node-link-account"), () => [app], _ => nodeLink).DiscoverAsync(CancellationToken.None);
        Require(linked.Status == "available" && linked.RuntimePath == node, "External native Node symlink was not resolved");
        await File.WriteAllTextAsync(modePath, "hang"); File.Delete(tracePath);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { await new PortableZcodeDiscovery(Path.Combine(root, "cancel-account"), () => [app], Find!).DiscoverAsync(cancelled.Token); throw new InvalidOperationException("ZCode cancellation ignored"); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        await NvmDiscoveryAsync(Path.Combine(root, "nvm-fixtures"), app, node);
        await StaticPublicCliFallbackAsync(Path.Combine(root, "static-cli"));
        Console.WriteLine("PASS ZCode external Node: strict branding, fixed cjs argv, app-vs-runtime missing states, no Electron fallback, unsafe runtime/script rejection, native symlinks, CLI compatibility and cancellation");
    }

    private static async Task NvmDiscoveryAsync(string root, string app, string fixtureNode)
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Use the physical spelling of macOS system aliases for exact launcher
        // comparisons; discovery intentionally rejects arbitrary directory links.
        root = Path.GetFullPath(root);
        foreach (var alias in new[] { "/tmp", "/var", "/etc" })
            if (root == alias || root.StartsWith(alias + "/", StringComparison.Ordinal)) { root = "/private" + root; break; }
        // All homes and executables belong to this test. The lookup explicitly
        // models Finder's system-only PATH and never runs an installed Node.
        var finderHome = Path.Combine(root, "Finder home ; $(literal)");
        var newest = CopyNvmNode(finderHome, "v26.13.0", fixtureNode);
        var lookups = new List<string>();
        string FinderLookup(string name)
        {
            lookups.Add(name);
            Require(ExecutableLocator.Find(name, "/usr/bin:/bin") is null, "Finder fixture requires a system-only PATH without Node");
            return null!;
        }
        var finder = await new PortableZcodeDiscovery(Path.Combine(root, "finder-account"), () => [app], FinderLookup, finderHome).DiscoverAsync(CancellationToken.None);
        Require(finder.Status == "available" && finder.RuntimePath == newest && finder.Version == "0.17.1" && finder.PortableStartInfo is not null &&
            lookups.SequenceEqual(new[] { "node", "nodejs" }), "Finder PATH failed to discover verified standard nvm Node: status=" + finder.Status + ", expectedRuntime=" + (finder.RuntimePath == newest) + ", version=" + finder.Version);
        var launch = finder.PortableStartInfo!(root, ["app-server"]);
        Require(launch.FileName == newest && launch.ArgumentList.SequenceEqual(new[] { finder.Path, "app-server" }) && !launch.UseShellExecute &&
            !launch.Environment.ContainsKey("ELECTRON_RUN_AS_NODE"), "nvm discovery did not retain the verified external Node launcher");

        File.Delete(Trace(newest));
        var direct = await new PortableZcodeDiscovery(Path.Combine(root, "path-first-account"), () => [app], name => name == "node" ? newest : null!,
            Path.Combine(root, "must-not-enumerate")).DiscoverAsync(CancellationToken.None);
        Require(direct.Status == "available" && direct.RuntimePath == newest, "PATH Node no longer takes precedence over nvm");

        var orderedHome = Path.Combine(root, "numeric-order");
        var older = CopyNvmNode(orderedHome, "v9.20.1", fixtureNode);
        var current = CopyNvmNode(orderedHome, "v10.2.0", fixtureNode);
        var ordered = await DiscoverNvmAsync(root, "numeric-order", app, orderedHome);
        Require(ordered.Status == "available" && ordered.RuntimePath == current && !File.Exists(Trace(older)), "nvm versions were not sorted numerically and deterministically");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(current)!, "mode"), "unsupported");
        File.Delete(Trace(current));
        var compatible = await DiscoverNvmAsync(root, "compatible-fallback", app, orderedHome);
        Require(compatible.Status == "available" && compatible.RuntimePath == older && File.Exists(Trace(current)), "Incompatible nvm candidate prevented a verified compatible fallback");
        var pathFallback = await new PortableZcodeDiscovery(Path.Combine(root, "path-incompatible-account"), () => [app], name => name == "node" ? current : null!, finderHome)
            .DiscoverAsync(CancellationToken.None);
        Require(pathFallback.Status == "available" && pathFallback.RuntimePath == newest, "Incompatible PATH Node prevented nvm compatibility probing");

        var rejectedHome = Path.Combine(root, "rejected");
        var rejected = new List<string>();
        foreach (var name in new[] { "latest", "v1.2", "v1.2.3-beta", "v01.2.3", "v1.2.3.4", "v2147483648.0.0", "v99999999999.0.0" })
            rejected.Add(CopyNvmNode(rejectedHome, name, fixtureNode));
        var shell = CopyNvmNode(rejectedHome, "v90.0.0", fixtureNode);
        File.Delete(shell); File.WriteAllText(shell, "#!/bin/sh\nexit 79\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); rejected.Add(shell);
        var outside = Path.Combine(root, "outside", "bin"); Directory.CreateDirectory(outside);
        File.Copy(fixtureNode, Path.Combine(outside, "node"));
        var versionLink = Path.Combine(NvmRoot(rejectedHome), "v91.0.0");
        Directory.CreateSymbolicLink(versionLink, Path.GetDirectoryName(outside)!);
        var binLinkVersion = Path.Combine(NvmRoot(rejectedHome), "v92.0.0"); Directory.CreateDirectory(binLinkVersion);
        Directory.CreateSymbolicLink(Path.Combine(binLinkVersion, "bin"), outside);
        var nodeLink = CopyNvmNode(rejectedHome, "v93.0.0", fixtureNode); File.Delete(nodeLink);
        File.CreateSymbolicLink(nodeLink, Path.Combine(outside, "node"));
        var electron = Path.Combine(root, "Electron.app", "Contents", "MacOS", "node"); Directory.CreateDirectory(Path.GetDirectoryName(electron)!);
        File.Copy(fixtureNode, electron);
        var electronLink = CopyNvmNode(rejectedHome, "v94.0.0", fixtureNode); File.Delete(electronLink); File.CreateSymbolicLink(electronLink, electron);
        var invalid = await DiscoverNvmAsync(root, "rejected", app, rejectedHome);
        Require(invalid.Status == "runtime_missing" && invalid.PortableStartInfo is null && rejected.All(path => !File.Exists(Trace(path))) &&
            !File.Exists(Path.Combine(outside, "trace")) && !File.Exists(Trace(electron)), "Non-version, out-of-tree, linked-directory, script or Electron nvm candidate was executed");
        var linkedHome = Path.Combine(root, "linked-home"); Directory.CreateSymbolicLink(linkedHome, finderHome);
        File.Delete(Trace(newest));
        var unsafeHome = await DiscoverNvmAsync(root, "home-link", app, linkedHome);
        Require(unsafeHome.Status == "runtime_missing" && !File.Exists(Trace(newest)), "Symlinked nvm home was followed");
        Require((await DiscoverNvmAsync(root, "relative-home", app, "relative-home")).Status == "runtime_missing", "Relative nvm home was trusted");

        var boundedHome = Path.Combine(root, "bounded-tree");
        var boundedNode = CopyNvmNode(boundedHome, "v26.13.0", fixtureNode);
        for (var i = 0; i < 64; i++) Directory.CreateDirectory(Path.Combine(NvmRoot(boundedHome), "ignored-" + i));
        var bounded = await DiscoverNvmAsync(root, "bounded-tree", app, boundedHome);
        Require(bounded.Status == "runtime_missing" && !File.Exists(Trace(boundedNode)), "Oversized nvm tree was scanned or selected from a nondeterministic prefix");
        var limitedHome = Path.Combine(root, "bounded-probes");
        var limitedNodes = new List<string>();
        for (var i = 1; i <= 9; i++)
        {
            var candidate = CopyNvmNode(limitedHome, "v" + i + ".0.0", fixtureNode);
            if (i > 1) File.WriteAllText(Path.Combine(Path.GetDirectoryName(candidate)!, "mode"), "bad-version");
            limitedNodes.Add(candidate);
        }
        var limited = await DiscoverNvmAsync(root, "bounded-probes", app, limitedHome);
        Require(limited.Status == "runtime_missing" && !File.Exists(Trace(limitedNodes[0])) && limitedNodes.Skip(1).All(path => File.Exists(Trace(path))),
            "nvm candidate probe cap was not eight deterministic newest versions");

        var cancelledHome = Path.Combine(root, "cancelled");
        var cancelledNode = CopyNvmNode(cancelledHome, "v26.13.0", fixtureNode);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(cancelledNode)!, "mode"), "hang");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { await new PortableZcodeDiscovery(Path.Combine(root, "nvm-cancel-account"), () => [app], _ => null!, cancelledHome).DiscoverAsync(cancellation.Token);
            throw new InvalidOperationException("nvm probe ignored cancellation"); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        foreach (var line in File.ReadAllLines(Trace(cancelledNode)).Where(line => line.StartsWith("pid=", StringComparison.Ordinal)))
        {
            var pid = int.Parse(line.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture);
            try { using var process = Process.GetProcessById(pid); Require(process.HasExited, "Cancelled nvm fixture was left running"); }
            catch (ArgumentException) { }
        }
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        var lookupCalled = false;
        try { await new PortableZcodeDiscovery(Path.Combine(root, "pre-cancel-account"), () => [app], _ => { lookupCalled = true; return null!; }, finderHome)
            .DiscoverAsync(alreadyCancelled.Token); throw new InvalidOperationException("Pre-cancelled discovery continued"); }
        catch (OperationCanceledException) when (alreadyCancelled.IsCancellationRequested) { }
        Require(!lookupCalled, "Pre-cancelled discovery performed runtime lookup");
        Console.WriteLine("PASS ZCode nvm: Finder short PATH, external native launcher, PATH priority, numeric ordering, CLI fallback, strict names, link/escape/script/Electron rejection, bounded enumeration/probes and cancellation cleanup");
    }

    private static string NvmRoot(string home) => Path.Combine(home, ".nvm", "versions", "node");
    private static string Trace(string node) => Path.Combine(Path.GetDirectoryName(node)!, "trace");
    private static string CopyNvmNode(string home, string version, string fixture)
    {
        var bin = Path.Combine(NvmRoot(home), version, "bin"); Directory.CreateDirectory(bin);
        var node = Path.Combine(bin, "node"); File.Copy(fixture, node);
        return node;
    }
    private static Task<AiAgentInstallation> DiscoverNvmAsync(string root, string name, string app, string home) =>
        new PortableZcodeDiscovery(Path.Combine(root, name + "-account"), () => [app], _ => null!, home).DiscoverAsync(CancellationToken.None);

    private static async Task StaticPublicCliFallbackAsync(string root)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var bin = Path.Combine(root, "bin"); Directory.CreateDirectory(bin);
        var shim = Path.Combine(bin, "zcode"); var marker = Path.Combine(root, "must-not-run");
        // This launcher-shaped command would leave a marker if discovery ever
        // executed it. It is static metadata, never passed to Process.Start.
        await File.WriteAllTextAsync(shim, "#!/bin/sh\nprintf unsafe > '" + marker.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        static AiAgentInstallation Missing(string id, string protocol) => new(id, id, "", "", "", protocol, "missing", "native");
        var previous = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin);
            var catalog = new PortableAiCatalog(Path.Combine(root, "account"), _ => Task.FromResult(Missing("codex", "codex")),
                _ => Task.FromResult(Missing("zcode", "zcode")), _ => Task.FromResult(new[] { Missing("claude", "claude"), Missing("opencode", "acp"), Missing("grok", "acp") }), null!);
            var snapshot = await catalog.DiscoverAsync(CancellationToken.None);
            var card = snapshot.Capabilities(true).Single(agent => agent.Id == "zcode");
            Require(card.Path == shim && card.Strategy == "local_cli" && card.Status == "probe_failed" && card.ProtocolStatus == "not_checked" &&
                card.DetectionCode == "AI_PROBE_SKIPPED" && !card.RemoteExecutionAvailable && !File.Exists(marker),
                "Public zcode shim was run, erased or incorrectly reported as verified");
            Require(snapshot.Installations.Single(agent => agent.Id == "zcode").Status == "missing", "Static public CLI became the supported runtime");
        }
        finally { Environment.SetEnvironmentVariable("PATH", previous); }
    }

    private static async Task CommandAsync(string file, string[] arguments, string directory)
    { var start = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var argument in arguments) start.ArgumentList.Add(argument); await ExecuteAsync(start); }
    private static async Task ExecuteAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("Fixture failed to start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)); Require(process.ExitCode == 0, "Controlled fixture exited unsuccessfully"); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private static void Reject(Action action) { try { action(); throw new InvalidOperationException("Unsafe ZCode runtime accepted"); } catch (IOException) { } }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
