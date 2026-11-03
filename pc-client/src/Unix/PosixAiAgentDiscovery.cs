using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TermLink.Pc.Contracts;

namespace TermLink.Platform.Unix;

/// <summary>
/// Detects public CLI installations only. A successful probe does not establish
/// authentication, remote AI support, or structured protocol compatibility.
/// </summary>
public sealed class PosixAiAgentDiscovery
{
    private const int MaximumOutputBytes = 64 * 1024;
    private static readonly (string Id, string Name, string[] DesktopNames)[] Agents =
    [
        ("codex", "Codex", ["Codex.app"]),
        ("claude", "Claude Code", ["Claude.app"]),
        ("opencode", "OpenCode", ["OpenCode.app", "OpenCode Desktop.app"]),
        ("grok", "Grok Build", []),
        ("zcode", "ZCode", ["ZCode.app"]),
    ];

    private readonly Func<string, string?> find;
    private readonly Func<string, bool> desktopApplicationExists;
    private readonly TimeSpan probeTimeout;

    public PosixAiAgentDiscovery(
        Func<string, string?>? executableLookup = null,
        TimeSpan? probeTimeout = null,
        Func<string, bool>? desktopApplicationExists = null)
    {
        find = executableLookup ?? (name => ExecutableLocator.Find(name));
        this.probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(4);
        if (this.probeTimeout <= TimeSpan.Zero || this.probeTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        this.desktopApplicationExists = desktopApplicationExists ??
            (path => OperatingSystem.IsMacOS() && Directory.Exists(path));
    }

    public async ValueTask<IReadOnlyList<AiAgentCapability>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.WhenAll(Agents.Select(agent =>
            DiscoverAgentAsync(agent.Id, agent.Name, agent.DesktopNames, cancellationToken))).ConfigureAwait(false);
    }

    private async Task<AiAgentCapability> DiscoverAgentAsync(
        string id, string name, IReadOnlyList<string> desktopNames, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = find(id);
        if (path is null)
        {
            var desktop = FindDesktopApplication(desktopNames);
            return new(id, name, desktop ?? string.Empty, string.Empty,
                desktop is null ? "missing" : "desktop_only", "local_cli");
        }

        var candidate = new AiAgentCapability(id, name, path, string.Empty, "probe_failed", "local_cli");
        // Only absolute executable paths may be launched, including injected lookup
        // results. ArgumentList preserves spaces without a shell or command parsing.
        if (!Path.IsPathFullyQualified(path) || !ExecutableLocator.IsExecutable(path)) return candidate;
        try
        {
            var start = BuildStartInfo(path);
            if (start is null) return candidate with { Status = "runtime_missing" };
            if (id == "grok") start.Environment["GROK_DISABLE_AUTOUPDATER"] = "1";
            var version = await ProbeAsync(start, cancellationToken).ConfigureAwait(false);
            return candidate with { Version = version, Status = "detected" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Win32Exception error) when (error.NativeErrorCode is 1 or 13)
        {
            return candidate with { Status = "runtime_blocked" };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            return candidate;
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

    private ProcessStartInfo? BuildStartInfo(string path)
    {
        var start = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // npm/bun CLI shims commonly use env to find their runtime. Resolve these
        // against the same safe locations, so Finder's short PATH works too. Read
        // only the shebang; never inspect desktop bundles or credential files.
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[512];
        var count = file.Read(header);
        if (count >= 2 && header[0] == '#' && header[1] == '!')
        {
            var end = header[..count].IndexOf((byte)'\n');
            if (end < 0) end = count;
            var parts = Encoding.UTF8.GetString(header[2..end]).Trim()
                .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                if (!Path.IsPathFullyQualified(parts[0]) || !ExecutableLocator.IsExecutable(parts[0]))
                    return null;
                var runtimeIndex = parts[0] == "/usr/bin/env" && parts.Length == 2 ? 1 :
                    parts[0] == "/usr/bin/env" && parts.Length == 3 && parts[1] == "-S" ? 2 : 0;
                if (runtimeIndex > 0)
                {
                    var runtime = find(parts[runtimeIndex]);
                    if (runtime is null || !Path.IsPathFullyQualified(runtime) || !ExecutableLocator.IsExecutable(runtime))
                        return null;
                    start.FileName = runtime;
                    start.ArgumentList.Add(path);
                }
            }
        }
        start.ArgumentList.Add("--version");
        return start;
    }

    private async Task<string> ProbeAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probeTimeout);
        using var process = Process.Start(start) ?? throw new IOException("AI CLI probe did not start.");
        using var cancellation = timeout.Token.Register(() => Terminate(process));
        var budget = new OutputBudget(timeout);
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, budget, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, budget, timeout.Token);
        try
        {
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.Exceeded || process.ExitCode != 0) throw new IOException("AI CLI probe failed.");
            var output = Encoding.UTF8.GetString(await stdout.ConfigureAwait(false));
            if (string.IsNullOrWhiteSpace(output)) output = Encoding.UTF8.GetString(await stderr.ConfigureAwait(false));
            var firstLine = output.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
            var version = new string(firstLine.Where(value => !char.IsControl(value)).ToArray()).Trim();
            return version[..Math.Min(version.Length, 120)];
        }
        finally
        {
            Terminate(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or InvalidOperationException or Win32Exception) { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, OutputBudget budget, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[2048];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (!budget.Add(count)) throw new IOException("AI CLI probe output exceeds its limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    private sealed class OutputBudget(CancellationTokenSource timeout)
    {
        private int count;
        public bool Exceeded => Volatile.Read(ref count) > MaximumOutputBytes;

        public bool Add(int size)
        {
            if (Interlocked.Add(ref count, size) <= MaximumOutputBytes) return true;
            timeout.Cancel();
            return false;
        }
    }
}
