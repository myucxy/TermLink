// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using TermLink.Agent.AI;
using TermLink.Ai;
using TermLink.Pc.Contracts;

internal static class PortableCatalogValidation
{
    internal static async Task RunAsync(string root)
    {
        await CatalogProjectionAsync(Path.Combine(root, "catalog"));
        await IsolatedZcodeAsync(Path.Combine(root, "zcode"));
        await PortableZcodeNodeValidation.RunAsync(Path.Combine(root, "zcode-node"));
        DirectoryFences(Path.Combine(root, "directory"));
        if (OperatingSystem.IsMacOS()) await NativeFileGateAsync(Path.Combine(root, "native"));
        Console.WriteLine("PASS unified AI catalog: five native brands, shared status/authorization projection, isolated ZCode probes/launcher, private directory fences and native file export gate");
    }

    private static async Task CatalogProjectionAsync(string root)
    {
        var installations = new[] { Agent("codex","codex","available"), Agent("claude","claude","available"),
            Agent("opencode","acp","available"), Agent("grok","acp","available"), Agent("zcode","zcode","available") };
        var localCalls = 0;
        var catalog = new PortableAiCatalog(root, _ => Task.FromResult(installations[0]), _ => Task.FromResult(installations[4]),
            _ => Task.FromResult(installations[1..4]), _ => { localCalls++; return Task.FromResult<IReadOnlyList<AiAgentCapability>>([]); });
        var discovered = await catalog.DiscoverAsync(CancellationToken.None);
        var cards = discovered.Capabilities(true);
        Require(cards.Count == 5 && localCalls == 0 && cards.Select(a => a.Id).SequenceEqual(installations.Select(a => a.Id)) &&
            cards.All(a => a.Strategy == "native" && a.RemoteExecutionAvailable && a.AuthenticationStatus == "isolated_required" && a.ProtocolStatus == "available"),
            "Five native brands diverged from desktop projection or fabricated authentication");
        Require(discovered.Capabilities(false).All(a => !a.RemoteExecutionAvailable), "An unavailable remote Host gained capabilities");
        using var host = new PortableAiHost(root, null!, null!, null, catalog);
        using var reply = JsonDocument.Parse(await host.ExecuteAsync(Guid.NewGuid(), "{\"action\":\"catalog\"}", CancellationToken.None, () => true));
        var agents = reply.RootElement.GetProperty("agents").EnumerateArray().ToArray();
        Require(agents.Length == 5 && agents.Zip(cards).All(pair => pair.First.GetProperty("id").GetString() == pair.Second.Id &&
            pair.First.GetProperty("status").GetString() == pair.Second.Status && pair.First.GetProperty("strategy").GetString() == pair.Second.Strategy &&
            pair.First.GetProperty("protocol").GetString() == pair.Second.Protocol), "Desktop and native Host catalog disagree");
        foreach (var status in new[] { "missing", "incompatible", "runtime_missing", "runtime_blocked", "probe_failed" })
        {
            var entries = installations.Select(a => a with { Status = status }).ToArray();
            var snapshot = new PortableAiCatalog.Snapshot(entries, []);
            Require(snapshot.Capabilities(true).All(a => !a.RemoteExecutionAvailable && a.Status == status && a.AuthenticationStatus != "verified"), "Failed/missing installation was erased or became remotely available");
        }
        var fallback = new AiAgentCapability("zcode", "ZCode", "/fixture/public-zcode", "1", "detected", "local_cli")
            { Protocol = "zcode", ProtocolStatus = "available" };
        var withLocal = new PortableAiCatalog.Snapshot(installations.Select(a => a.Id == "zcode" ? a with { Status = "missing" } : a).ToArray(), [fallback]);
        var local = withLocal.Capabilities(true).Single(a => a.Id == "zcode");
        Require(local.Path == fallback.Path && local.Status == "detected" && local.Strategy == "local_cli" && !local.RemoteExecutionAvailable,
            "A public CLI was erased or promoted to the desktop runtime");
        var publicFallback = new PortableAiCatalog(Path.Combine(root, "public-cli"), _ => Task.FromResult(installations[0]),
            _ => Task.FromResult(installations[4] with { Status = "missing", PortableStartInfo = null }),
            _ => Task.FromResult(installations[1..4]), _ => { localCalls++; return Task.FromResult<IReadOnlyList<AiAgentCapability>>([fallback]); });
        var fallbackResult = await publicFallback.DiscoverAsync(CancellationToken.None);
        Require(localCalls == 1 && fallbackResult.Installations.Single(a => a.Id == "zcode").Status == "missing" &&
            fallbackResult.Capabilities(true).Single(a => a.Id == "zcode").Path == fallback.Path &&
            !fallbackResult.Capabilities(true).Single(a => a.Id == "zcode").RemoteExecutionAvailable, "Public CLI fallback did not remain detection-only");
        var unavailableLauncher = new PortableAiCatalog(Path.Combine(root, "no-launcher"),
            _ => Task.FromResult(installations[0] with { PortableStartInfo = null }), _ => Task.FromResult(installations[4]),
            _ => Task.FromResult(installations[1..4]), _ => Task.FromResult<IReadOnlyList<AiAgentCapability>>([]));
        var refused = await unavailableLauncher.DiscoverAsync(CancellationToken.None);
        Require(refused.Installations.Single(a => a.Id == "codex").Status == "incompatible" &&
            !refused.Capabilities(true).Single(a => a.Id == "codex").RemoteExecutionAvailable, "An installation without a verified launcher became available");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await catalog.DiscoverAsync(cancellation.Token); throw new InvalidOperationException("Catalog cancellation ignored"); } catch (OperationCanceledException) { }
        Console.WriteLine("PASS unified five-brand catalog/native launcher projection, failed/missing states, detection-only fallback and cancellation");
    }

    private static async Task IsolatedZcodeAsync(string root)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var app = Path.Combine(root, "fixture ZCode.app");
        var engine = Path.Combine(app, "Contents", "MacOS"); var script = Path.Combine(app,"Contents","Resources","glm");
        var config = Path.Combine(app,"Contents","Resources","config","provider");
        Directory.CreateDirectory(engine); Directory.CreateDirectory(script); Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(script,"zcode.cjs"), "// non-executable fixture marker");
        await File.WriteAllTextAsync(Path.Combine(config,"zcode-builtin.json"), "{}");
        // This deliberately small local executable only reports its environment.
        // It never invokes the installed ZCode binary, a shell or a provider.
        var c = Path.Combine(root, "fixture.c");
        await File.WriteAllTextAsync(c, """
        #include <stdlib.h>
        #include <string.h>
        #include <stdio.h>
        int main(int argc,char**argv){
          const char*base=getenv("ZCODE_DATA_BASE_DIR");const char*provider=getenv("ZCODE_PERSONAL_PROVIDER_CONFIG_FILE");
          const char*store=getenv("ZCODE_STORAGE_DIR");const char*db=getenv("ZCODE_SESSION_DB_PATH");
          if(!base||!provider||strncmp(provider,base,strlen(base))||!strstr(provider,"/.zcode/v2/provider_config.json")||getenv("TERMLINK_FIXTURE_PROVIDER_TOKEN"))return 31;
          if(argc==2&&!strcmp(argv[1],"--version")){puts("v26.13.0");return 0;}
          if(argc!=3)return 32;
          if(!strcmp(argv[2],"--help")){puts("app-server");return 0;}
          if(!strcmp(argv[2],"--version")){puts("0.17.0");return 0;}
          if(!strcmp(argv[2],"app-server")&&store&&db&&!strncmp(store,base,strlen(base))&&!strncmp(db,base,strlen(base))){puts("isolated");return 0;}
          return 33;
        }
        """);
        var nodeDirectory = Path.Combine(root, "external native runtime ; $(literal)"); Directory.CreateDirectory(nodeDirectory);
        var node = Path.Combine(nodeDirectory, "node");
        await Command("/usr/bin/cc", [c,"-o",node], root);
        await File.WriteAllTextAsync(Path.Combine(engine,"ZCode"), "not an executable; Electron must not be needed");
        var previous = Environment.GetEnvironmentVariable("TERMLINK_FIXTURE_PROVIDER_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("TERMLINK_FIXTURE_PROVIDER_TOKEN", "fake sentinel never inherited");
            var account = Path.Combine(root, "account");
            var discovery = new PortableZcodeDiscovery(account, () => [app], name => name == "node" ? node : null!);
            var agent = await discovery.DiscoverAsync(CancellationToken.None);
            Require(agent.Status == "available" && agent.PortableStartInfo != null && agent.ConfigurationDataDirectory == Path.Combine(account, "zcode-runtime") &&
                agent.SessionDataDirectory == agent.ConfigurationDataDirectory && agent.RuntimePath == node && agent.Version == "0.17.0", "ZCode did not isolate configuration/sessions or retain CLI version/native Node");
            var start = AiAgentDiscovery.StartInfo(agent, root, "app-server");
            Require(start.Environment["ZCODE_PERSONAL_PROVIDER_CONFIG_FILE"] == Path.Combine(agent.ConfigurationDataDirectory, ".zcode", "v2", "provider_config.json") &&
                start.Environment["ZCODE_DATA_BASE_DIR"] == agent.ConfigurationDataDirectory &&
                !start.Environment.ContainsKey("TERMLINK_FIXTURE_PROVIDER_TOKEN") && !start.Environment.ContainsKey("ELECTRON_RUN_AS_NODE") &&
                start.FileName == node && start.ArgumentList.SequenceEqual(new[] { agent.Path, "app-server" }) && !start.UseShellExecute,
                "ZCode fell back to Electron/user/provider environment or changed literal argv");
            await Execute(start);
            foreach(var p in new[]{agent.ConfigurationDataDirectory,Path.Combine(agent.ConfigurationDataDirectory,".zcode"),Path.Combine(agent.ConfigurationDataDirectory,".zcode","v2")})
                Require(File.GetUnixFileMode(p)==(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute),"ZCode runtime is not private");
            Require(!File.Exists(Path.Combine(agent.ConfigurationDataDirectory,".zcode","v2","provider_config.json")), "Provider configuration was copied");
            var linkedProvider = Path.Combine(agent.ConfigurationDataDirectory,".zcode","v2","provider_config.json");
            File.CreateSymbolicLink(linkedProvider, Path.Combine(root, "never-created-provider-config"));
            Reject(() => AiAgentDiscovery.StartInfo(agent, root, "app-server"));
            File.Delete(linkedProvider);
            var outside = Path.Combine(root,"outside");Directory.CreateDirectory(outside);
            var v2 = Path.Combine(agent.ConfigurationDataDirectory,".zcode","v2");Directory.Delete(v2);Directory.CreateSymbolicLink(v2,outside);
            Reject(()=>AiAgentDiscovery.StartInfo(agent,root,"app-server"));
        }
        finally { Environment.SetEnvironmentVariable("TERMLINK_FIXTURE_PROVIDER_TOKEN",previous); }
        Console.WriteLine("PASS ZCode fixture: probes and app-server argv use the same isolated config/SQLite roots, inherited auth removed, replaced paths rejected");
    }

    private static void DirectoryFences(string root)
    {
        Directory.CreateDirectory(root);
        var account = Path.Combine(root,"private"); PortableAiRuntimeDirectory.Ensure(account);
        var linked = Path.Combine(root,"link"); Directory.CreateSymbolicLink(linked,account);
        Reject(()=>new PortableAiCatalog(linked));
        Reject(()=>new PortableZcodeDiscovery(Path.Combine(linked,"nested")));
        if(OperatingSystem.IsMacOS()||OperatingSystem.IsLinux()) {
            var unsafeRoot=Path.Combine(root,"shared");Directory.CreateDirectory(unsafeRoot);
            File.SetUnixFileMode(unsafeRoot,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.OtherRead);
            Reject(()=>new PortableAiCatalog(unsafeRoot));
            Reject(()=>new PortableAiHost(unsafeRoot,()=>Task.FromResult(Agent("codex","codex","missing")),()=>Task.FromResult(Agent("zcode","zcode","missing"))));
        }
        if (OperatingSystem.IsMacOS())
        {
            var acl = Path.Combine(root, "acl"); PortableAiRuntimeDirectory.Ensure(acl);
            var add = new ProcessStartInfo("/bin/chmod") { UseShellExecute = false }; add.ArgumentList.Add("+a"); add.ArgumentList.Add("everyone allow read,readattr,readextattr,readsecurity,file_inherit,directory_inherit"); add.ArgumentList.Add(acl);
            using var added = Process.Start(add)!; added.WaitForExit(); Require(added.ExitCode == 0, "Fixture ACL setup failed");
            try { Reject(() => new PortableAiCatalog(acl)); }
            finally { var remove = new ProcessStartInfo("/bin/chmod") { UseShellExecute = false }; remove.ArgumentList.Add("-N"); remove.ArgumentList.Add(acl); using var removed = Process.Start(remove)!; removed.WaitForExit(); }
        }
        Console.WriteLine("PASS catalog/private runtime directory guards reject linked ancestors and unsafe account modes before discovery");
    }

    private static async Task NativeFileGateAsync(string root)
    {
        Require(PortableAiCatalog.FileAccessAvailable, "Current native file exports unavailable");
        Directory.CreateDirectory(root);
        var oldC=Path.Combine(root,"old.c");await File.WriteAllTextAsync(oldC,"int termlink_files_open(void){return -1;}\n");
        var old=Path.Combine(root,"old.dylib");await Command("/usr/bin/cc",["-dynamiclib",oldC,"-o",old],root);
        foreach(var mode in new[]{"missing","old","current"}) {
            var directory=Path.Combine(root,mode);Directory.CreateDirectory(directory);
            foreach(var file in Directory.EnumerateFiles(AppContext.BaseDirectory)) {
                if(Path.GetExtension(file)==".dylib")continue;
                File.Copy(file,Path.Combine(directory,Path.GetFileName(file)));
            }
            if(mode!="missing")File.Copy(mode=="old"?old:Path.Combine(AppContext.BaseDirectory,"libtermlink-files.dylib"),Path.Combine(directory,"libtermlink-files.dylib"));
            var start=new ProcessStartInfo(Path.Combine(directory,Path.GetFileName(Environment.ProcessPath!))) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=directory };
            start.ArgumentList.Add("app-server");start.Environment["TERMLINK_FILE_CAPABILITY_PROBE"]=mode=="current"?"true":"false";
            start.Environment.Remove("DYLD_LIBRARY_PATH");
            await Execute(start);
        }
        Console.WriteLine("PASS native AI file capability: actual current bridge enables; missing/old-export libraries disable in fresh processes");
    }

    private static AiAgentInstallation Agent(string id,string protocol,string status)=>new(id,id,"/fixture/"+id,"","1.0.0",protocol,status,"native") {
        ConfigurationDataDirectory="/fixture-private/"+id,SessionDataDirectory="/fixture-private/"+id,
        PortableStartInfo=(_,_)=>throw new InvalidOperationException("Catalog must not start a model") };
    private static async Task Command(string executable,string[] args,string directory) { var s=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=directory};foreach(var a in args)s.ArgumentList.Add(a);await Execute(s); }
    private static async Task Execute(ProcessStartInfo start) {using var process=Process.Start(start)??throw new IOException("Fixture failed to start");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var o=process.StandardOutput.ReadToEndAsync(timeout.Token);var e=process.StandardError.ReadToEndAsync(timeout.Token);try{await Task.WhenAll(o,e,process.WaitForExitAsync(timeout.Token));if(process.ExitCode!=0)throw new InvalidOperationException("Controlled fixture failed (no raw environment retained): "+process.ExitCode);}finally{if(!process.HasExited)process.Kill(true);} }
    private static void Reject(Action action){try{action();throw new InvalidOperationException("Unsafe directory accepted");}catch(IOException){} }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
