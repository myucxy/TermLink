using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.Agent.Connection;
using TermLink.Ai;

namespace TermLink.Agent.AI;

public sealed class PortableAiHost : IRelayAiHost
{
    private readonly AiSessionManager sessions;
    private readonly PortableAiDirectories directories;

    public PortableAiHost(string accountDirectory) : this(accountDirectory,
        new PortableAiCatalog(accountDirectory)) { }

    private PortableAiHost(string accountDirectory, PortableAiCatalog catalog)
        : this(accountDirectory, null, null, null, catalog) { }

    // Injected fixtures remain deterministic and never discover installed CLIs.
    internal PortableAiHost(string accountDirectory, Func<Task<AiAgentInstallation>> codexDiscovery,
        Func<Task<AiAgentInstallation>> zcodeDiscovery, Func<Task<AiAgentInstallation[]>> additionalDiscovery = null,
        PortableAiCatalog catalog = null)
    {
        accountDirectory = PortableAiRuntimeDirectory.Ensure(accountDirectory);
        var runtime = PortableAiRuntimeDirectory.Ensure(Path.Combine(accountDirectory, "zcode-runtime"));
        var codex = new PortableCodexDiscovery(accountDirectory);
        codexDiscovery ??= () => codex.DiscoverAsync(CancellationToken.None);
        zcodeDiscovery ??= () => new PortableZcodeDiscovery(accountDirectory).DiscoverAsync(CancellationToken.None);
        additionalDiscovery ??= () => Task.FromResult(Array.Empty<AiAgentInstallation>());
        if (OperatingSystem.IsMacOS()) directories = new PortableAiDirectories(accountDirectory);
        sessions = new AiSessionManager(installRoot: accountDirectory,
            listDirectories: directories == null ? null : directories.ListAsync,
            createCliWorkspace: directories == null ? null : directories.CreateCliWorkspace, catalog: async () => {
            if (catalog != null) return (await catalog.DiscoverAsync(CancellationToken.None).ConfigureAwait(false)).Installations.ToArray();
            var codexTask = codexDiscovery(); var zcodeTask = zcodeDiscovery(); var additionalTask = additionalDiscovery();
            await Task.WhenAll(codexTask, zcodeTask, additionalTask).ConfigureAwait(false);
            return new[] { await codexTask.ConfigureAwait(false) }.Concat(await additionalTask.ConfigureAwait(false))
                .Append((await zcodeTask.ConfigureAwait(false)) with { SessionDataDirectory = runtime, ConfigurationDataDirectory = runtime }).ToArray();
        });
    }

    public Task<string> ExecuteAsync(Guid caller, string request, CancellationToken token, Func<bool> authorized) =>
        sessions.ExecuteAsync(caller, request, token, authorized);

    public void Dispose() { sessions.Dispose(); directories?.Dispose(); }
}
