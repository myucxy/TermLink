using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.Agent.Connection;
using TermLink.Ai;

namespace TermLink.Agent.AI;

public sealed class PortableAiHost : IRelayAiHost
{
    private readonly AiSessionManager sessions;

    public PortableAiHost(string accountDirectory)
    {
        if (!Path.IsPathFullyQualified(accountDirectory)) throw new ArgumentException("An account directory is required.");
        Directory.CreateDirectory(accountDirectory);
        if ((File.GetAttributes(accountDirectory) & FileAttributes.ReparsePoint) != 0) throw new IOException("AI_DIRECTORY_LINK_REJECTED");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(accountDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runtime = Path.Combine(accountDirectory, "zcode-runtime");
        sessions = new AiSessionManager(installRoot: accountDirectory, catalog: async () => {
            var detected = await ZcodeDesktopRuntime.DetectMacAsync(CancellationToken.None).ConfigureAwait(false);
            return new[] { detected with { SessionDataDirectory = runtime } };
        });
    }

    public Task<string> ExecuteAsync(Guid caller, string request, CancellationToken token, Func<bool> authorized) =>
        sessions.ExecuteAsync(caller, request, token, authorized);

    public void Dispose() => sessions.Dispose();
}
