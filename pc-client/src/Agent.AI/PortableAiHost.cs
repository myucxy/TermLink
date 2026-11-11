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

    public PortableAiHost(string accountDirectory) : this(accountDirectory, null, null,
        () => new PortableAdditionalAiDiscovery(accountDirectory).DiscoverAsync(CancellationToken.None)) { }

    // Injected discovery stays deterministic: callers that supply only the two
    // existing fixtures do not probe the machine's other installed CLIs.
    internal PortableAiHost(string accountDirectory, Func<Task<AiAgentInstallation>> codexDiscovery,
        Func<Task<AiAgentInstallation>> zcodeDiscovery, Func<Task<AiAgentInstallation[]>> additionalDiscovery = null)
    {
        if (!Path.IsPathFullyQualified(accountDirectory)) throw new ArgumentException("An account directory is required.");
        Directory.CreateDirectory(accountDirectory);
        if ((File.GetAttributes(accountDirectory) & FileAttributes.ReparsePoint) != 0) throw new IOException("AI_DIRECTORY_LINK_REJECTED");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(accountDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runtime = Path.Combine(accountDirectory, "zcode-runtime");
        var codex = new PortableCodexDiscovery(accountDirectory);
        codexDiscovery ??= () => codex.DiscoverAsync(CancellationToken.None);
        zcodeDiscovery ??= () => ZcodeDesktopRuntime.DetectMacAsync(CancellationToken.None);
        additionalDiscovery ??= () => Task.FromResult(Array.Empty<AiAgentInstallation>());
        if (OperatingSystem.IsMacOS()) directories = new PortableAiDirectories(accountDirectory);
        sessions = new AiSessionManager(installRoot: accountDirectory,
            listDirectories: directories == null ? null : directories.ListAsync,
            createCliWorkspace: directories == null ? null : directories.CreateCliWorkspace, catalog: async () => {
            var codexTask = codexDiscovery();
            var zcodeTask = zcodeDiscovery();
            var additionalTask = additionalDiscovery();
            await Task.WhenAll(codexTask, zcodeTask, additionalTask).ConfigureAwait(false);
            return new[] { await codexTask.ConfigureAwait(false) }
                .Concat(await additionalTask.ConfigureAwait(false))
                .Append((await zcodeTask.ConfigureAwait(false)) with { SessionDataDirectory = runtime }).ToArray();
        });
    }

    public Task<string> ExecuteAsync(Guid caller, string request, CancellationToken token, Func<bool> authorized) =>
        sessions.ExecuteAsync(caller, request, token, authorized);

    public void Dispose() { sessions.Dispose(); directories?.Dispose(); }
}
