// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.Ai;
using TermLink.Pc.Contracts;
using TermLink.Platform.Unix;

namespace TermLink.Agent.AI;

/// <summary>One discovery/launcher authority for the portable Host and its desktop diagnostics.</summary>
public sealed class PortableAiCatalog
{
    private readonly Func<CancellationToken, Task<AiAgentInstallation>> codex, zcode;
    private readonly Func<CancellationToken, Task<AiAgentInstallation[]>> additional;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AiAgentCapability>>> local;
    public static bool FileAccessAvailable => AiWorkspaceFiles.Available;

    public PortableAiCatalog(string accountDirectory) : this(accountDirectory, null, null, null, null) { }

    internal PortableAiCatalog(string accountDirectory, Func<CancellationToken, Task<AiAgentInstallation>> codex,
        Func<CancellationToken, Task<AiAgentInstallation>> zcode,
        Func<CancellationToken, Task<AiAgentInstallation[]>> additional,
        Func<CancellationToken, Task<IReadOnlyList<AiAgentCapability>>> local)
    {
        accountDirectory = PortableAiRuntimeDirectory.Ensure(accountDirectory);
        this.codex = codex ?? (token => new PortableCodexDiscovery(accountDirectory).DiscoverAsync(token));
        this.zcode = zcode ?? (token => new PortableZcodeDiscovery(accountDirectory).DiscoverAsync(token));
        this.additional = additional ?? (token => new PortableAdditionalAiDiscovery(accountDirectory).DiscoverAsync(token));
        this.local = local ?? (token => {
            token.ThrowIfCancellationRequested();
            // An arbitrary public zcode command can be a launcher shim for the
            // desktop. Keep only installation metadata here; never execute it.
            var path = ExecutableLocator.Find("zcode");
            IReadOnlyList<AiAgentCapability> result = path == null ? Array.Empty<AiAgentCapability>() : new[] {
                new AiAgentCapability("zcode", "ZCode", path, "", "probe_failed", "local_cli") {
                    Protocol = "zcode", ProtocolStatus = "not_checked", AuthenticationStatus = "not_checked",
                    RemoteExecutionAvailable = false, DetectionCode = "AI_PROBE_SKIPPED"
                }
            };
            return Task.FromResult(result);
        });
    }

    public async Task<Snapshot> DiscoverAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var c = codex(token); var z = zcode(token); var a = additional(token);
        // A missing official app resolves before its probe; detection of a public
        // CLI then runs alongside the other providers, rather than adding a second
        // full discovery timeout to the desktop request.
        var fallbackTask = FallbackAsync();
        await Task.WhenAll(c, z, a, fallbackTask).ConfigureAwait(false);
        var installations = new[] { await c.ConfigureAwait(false) }.Concat(await a.ConfigureAwait(false))
            .Append(await z.ConfigureAwait(false)).Select(agent => agent.Status == "available" && agent.PortableStartInfo == null
                ? agent with { Status = "incompatible" } : agent).ToArray();
        token.ThrowIfCancellationRequested();
        return new(installations, await fallbackTask.ConfigureAwait(false));

        async Task<IReadOnlyList<AiAgentCapability>> FallbackAsync() => (await z.ConfigureAwait(false)).Status == "missing"
            ? await local(token).ConfigureAwait(false) : Array.Empty<AiAgentCapability>();
    }

    public sealed class Snapshot
    {
        public IReadOnlyList<AiAgentInstallation> Installations { get; }
        private readonly IReadOnlyList<AiAgentCapability> local;
        internal Snapshot(IReadOnlyList<AiAgentInstallation> installations, IReadOnlyList<AiAgentCapability> local)
        { Installations = installations; this.local = local; }
        public IReadOnlyList<AiAgentCapability> Capabilities(bool remoteHostAvailable) => Installations.Select(agent =>
        {
            // A separately installed ZCode CLI is detection-only when the supported
            // desktop runtime is absent. Never erase that installation or grant it a launcher.
            var fallback = agent.Id == "zcode" && agent.Status == "missing" ? local.FirstOrDefault(a => a.Id == agent.Id && a.Status != "missing") : null;
            if (fallback != null) return fallback with { RemoteExecutionAvailable = false, AuthenticationStatus = "not_checked" };
            var ready = agent.Strategy == "native" && agent.Status == "available";
            return new AiAgentCapability(agent.Id, agent.Name, agent.Path, agent.Version, agent.Status, agent.Strategy)
            {
                Protocol = agent.Protocol,
                ProtocolStatus = ready ? "available" : agent.Status == "incompatible" ? "incompatible" :
                    agent.Status is "probe_failed" or "runtime_missing" or "runtime_blocked" ? "probe_failed" : "not_checked",
                AuthenticationStatus = ready && agent.ConfigurationDataDirectory.Length > 0 ? "isolated_required" : "not_checked",
                RemoteExecutionAvailable = remoteHostAvailable && ready,
                DetectionCode = agent.Status switch { "missing" => "AI_CLI_MISSING", "runtime_missing" => "AI_RUNTIME_MISSING",
                    "runtime_blocked" => "AI_RUNTIME_BLOCKED", "incompatible" => "AI_PROTOCOL_UNSUPPORTED", "probe_failed" => "AI_PROBE_FAILED", _ => "" },
            };
        }).ToArray();
    }
}
