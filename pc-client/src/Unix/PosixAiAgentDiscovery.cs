using System.Text.RegularExpressions;
using System.Diagnostics;
using TermLink.Pc.Contracts;

namespace TermLink.Platform.Unix;

/// <summary>
/// Checks public CLI version/help commands only. Protocol detection does not
/// establish authentication or enable remote execution; desktop engines belong
/// to the Host's separate discovery path.
/// </summary>
public sealed class PosixAiAgentDiscovery
{
    private static readonly Agent[] Agents =
    [
        new("codex", "Codex", "codex", ["--help"], "app-server", ["Codex.app"]),
        new("claude", "Claude Code", "claude", ["--help"], "stream-json", ["Claude.app"]),
        new("opencode", "OpenCode", "acp", ["acp", "--help"], "ACP", ["OpenCode.app", "OpenCode Desktop.app"]),
        new("grok", "Grok Build", "acp", ["agent", "--help"], "stdio", []),
        new("zcode", "ZCode", "zcode", ["--help"], "app-server", ["ZCode.app"]),
    ];
    private static readonly Regex VersionNumber = new(
        @"(?<![\p{L}\p{N}])[vV]?[0-9]{1,6}\.[0-9]{1,6}(?:\.[0-9]{1,6}){0,2}(?:[-+][0-9A-Za-z.-]+)?(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ProductName = new(
        @"(?<![\p{L}\p{N}])(codex(?:-cli)?|claude(?: code)?|opencode|grok(?: build)?|zcode|node(?:\.js)?|npm|bun)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly Func<string, string?> find;
    private readonly Func<string, bool> desktopApplicationExists;
    private readonly PosixProbeRunner runner;
    private readonly TimeSpan overallTimeout;

    public PosixAiAgentDiscovery(
        Func<string, string?>? executableLookup = null,
        TimeSpan? probeTimeout = null,
        Func<string, bool>? desktopApplicationExists = null,
        TimeSpan? overallTimeout = null,
        Action<ProcessStartInfo>? configureStartInfo = null)
    {
        find = executableLookup ?? (name => ExecutableLocator.Find(name));
        var timeout = probeTimeout ?? TimeSpan.FromSeconds(4);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(4))
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        this.overallTimeout = overallTimeout ?? TimeSpan.FromSeconds(12);
        if (this.overallTimeout <= TimeSpan.Zero || this.overallTimeout > TimeSpan.FromSeconds(12))
            throw new ArgumentOutOfRangeException(nameof(overallTimeout));
        runner = new PosixProbeRunner(find, timeout, configureStartInfo);
        this.desktopApplicationExists = desktopApplicationExists ??
            (path => OperatingSystem.IsMacOS() && Directory.Exists(path));
    }

    public async ValueTask<IReadOnlyList<AiAgentCapability>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(overallTimeout);
        var result = await Task.WhenAll(Agents.Select(agent =>
            DiscoverAgentAsync(agent, budget.Token, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<AiAgentCapability> DiscoverAgentAsync(
        Agent agent, CancellationToken budget, CancellationToken cancellationToken)
    {
        var candidate = new AiAgentCapability(agent.Id, agent.Name, string.Empty, string.Empty, "missing", "local_cli")
        {
            Protocol = agent.Protocol,
            ProtocolStatus = "not_checked",
            AuthenticationStatus = "not_checked",
            RemoteExecutionAvailable = false,
            DetectionCode = "AI_CLI_MISSING",
        };
        try
        {
            budget.ThrowIfCancellationRequested();
            var path = find(agent.Id);
            if (path is null)
            {
                var desktop = FindDesktopApplication(agent.DesktopNames);
                return candidate with
                {
                    Path = desktop ?? string.Empty,
                    Status = desktop is null ? "missing" : "desktop_only",
                    DetectionCode = desktop is null ? "AI_CLI_MISSING" : "AI_DESKTOP_ONLY",
                };
            }
            candidate = candidate with { Path = path, Status = "probe_failed", DetectionCode = "AI_PROBE_FAILED" };
            var version = await runner.RunAsync(path, ["--version"], agent.Id == "grok", budget).ConfigureAwait(false);
            if (version.Status != PosixProbeStatus.Succeeded) return Failed(candidate, version.Status);
            if (!TryVersion(agent.Id, version.Output, out var text))
                return candidate with { Status = "incompatible", DetectionCode = "AI_VERSION_MISMATCH" };
            candidate = candidate with { Version = text, Status = "detected" };
            var help = await runner.RunAsync(path, agent.HelpArguments, agent.Id == "grok", budget).ConfigureAwait(false);
            if (help.Status != PosixProbeStatus.Succeeded)
                return candidate with { ProtocolStatus = "probe_failed", DetectionCode = Code(help.Status) };
            var supported = HasProtocolToken(help.Output, agent.HelpToken);
            return candidate with
            {
                ProtocolStatus = supported ? "available" : "incompatible",
                DetectionCode = supported ? "AI_PROTOCOL_AVAILABLE" : "AI_PROTOCOL_UNSUPPORTED",
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            return candidate with
            {
                ProtocolStatus = candidate.Status == "detected" ? "probe_failed" : "not_checked",
                Status = candidate.Status == "detected" ? "detected" : "probe_failed",
                DetectionCode = "AI_DISCOVERY_TIMEOUT",
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return candidate with { Status = "probe_failed", DetectionCode = "AI_PROBE_FAILED" };
        }
    }

    private string? FindDesktopApplication(IReadOnlyList<string> names)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in names)
        {
            var systemPath = Path.Combine("/Applications", name);
            if (desktopApplicationExists(systemPath)) return systemPath;
            if (!Path.IsPathFullyQualified(home)) continue;
            var userPath = Path.Combine(home, "Applications", name);
            if (desktopApplicationExists(userPath)) return userPath;
        }
        return null;
    }

    private static AiAgentCapability Failed(AiAgentCapability candidate, PosixProbeStatus status) => candidate with
    {
        Status = status switch
        {
            PosixProbeStatus.RuntimeMissing => "runtime_missing",
            PosixProbeStatus.RuntimeBlocked => "runtime_blocked",
            _ => "probe_failed",
        },
        DetectionCode = Code(status),
    };

    private static string Code(PosixProbeStatus status) => status switch
    {
        PosixProbeStatus.RuntimeMissing => "AI_RUNTIME_MISSING",
        PosixProbeStatus.RuntimeBlocked => "AI_RUNTIME_BLOCKED",
        PosixProbeStatus.InvalidPath => "AI_EXECUTABLE_INVALID",
        PosixProbeStatus.UnsupportedShim => "AI_SHIM_UNSUPPORTED",
        PosixProbeStatus.TimedOut => "AI_PROBE_TIMEOUT",
        PosixProbeStatus.OutputLimitExceeded => "AI_PROBE_OUTPUT_LIMIT",
        _ => "AI_PROBE_FAILED",
    };

    private static bool TryVersion(string id, string output, out string version)
    {
        version = string.Empty;
        foreach (var line in output.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var match = VersionNumber.Match(line);
            if (!match.Success || line.Contains("usage:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("--help", StringComparison.Ordinal)) continue;
            var products = ProductName.Matches(line).Select(value => value.Value.ToLowerInvariant()).ToArray();
            if (products.Length > 0 && !products.Any(product => product.StartsWith(id, StringComparison.Ordinal))) continue;
            // Publish only the version token; banners and diagnostics may contain
            // terminal escapes or unrelated environment details.
            version = match.Value;
            return true;
        }
        return false;
    }

    private static bool HasProtocolToken(string output, string token)
    {
        var at = 0;
        while ((at = output.IndexOf(token, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            static bool Word(char value) => char.IsLetterOrDigit(value) || value is '_' or '-';
            var end = at + token.Length;
            if ((at == 0 || !Word(output[at - 1])) && (end == output.Length || !Word(output[end]))) return true;
            at = end;
        }
        return false;
    }

    private sealed record Agent(string Id, string Name, string Protocol, string[] HelpArguments,
        string HelpToken, string[] DesktopNames);
}
