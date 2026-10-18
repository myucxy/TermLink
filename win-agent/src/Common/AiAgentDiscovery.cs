using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TermLink.Ai;

public sealed record AiAgentInstallation(string Id, string Name, string Path, string RuntimePath,
    string Version, string Protocol, string Status, string Strategy = "native");

/// <summary>Probes never start a conversation, read model credentials, or install software.</summary>
public static partial class AiAgentDiscovery
{
    private static readonly object Sync = new();
    private static Task<AiAgentInstallation[]> detection;
    private static DateTime detectedAt;
    public static Task<AiAgentInstallation[]> CatalogAsync(bool refresh = false, ZcodeRemoteAddressStore remoteStore = null)
    {
        lock (Sync) {
            if (detection == null || detection.IsCompleted && (refresh || DateTime.UtcNow - detectedAt > TimeSpan.FromMinutes(1))) {
                detectedAt = DateTime.UtcNow;
                detection = DetectAsync();
            }
            return WithRemoteStrategyAsync(detection, remoteStore ?? ZcodeRemoteAddressStore.Default);
        }
    }
    private static async Task<AiAgentInstallation[]> WithRemoteStrategyAsync(Task<AiAgentInstallation[]> pending, ZcodeRemoteAddressStore remoteStore)
    {
        var agents = await pending.ConfigureAwait(false);
        try { return ApplyRemoteStrategy(agents, remoteStore.Read().Length > 0); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) {
            return agents.Select(a => a.Id == "zcode" ? a with { Status = "configuration_error", Strategy = "desktop_remote" } : a).ToArray();
        }
    }

    public static AiAgentInstallation[] ApplyRemoteStrategy(AiAgentInstallation[] agents, bool configured) =>
        agents.Select(a => configured && a.Id == "zcode" ? a with { Status = "available", Strategy = "desktop_remote" } : a).ToArray();

    public static async Task<AiAgentInstallation[]> DetectAsync(CancellationToken token = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var zcode = Find("zcode.exe");
        var script = FindZcodeScript();
        var candidates = new[] {
            new AiAgentInstallation("codex", "Codex", Find("codex.exe") ?? Find("codex.cmd") ?? Existing(System.IO.Path.Combine(home, ".local", "bin", "codex.exe")) ?? "", "", "", "codex", "missing"),
            new AiAgentInstallation("claude", "Claude Code", Find("claude.exe") ?? Existing(System.IO.Path.Combine(home, ".local", "bin", "claude.exe")) ?? "", "", "", "claude", "missing"),
            new AiAgentInstallation("opencode", "OpenCode", Find("opencode.exe") ?? Find("opencode.cmd") ?? "", "", "", "acp", "missing"),
            new AiAgentInstallation("grok", "Grok Build", Find("grok.exe") ?? Find("grok.cmd") ?? Existing(System.IO.Path.Combine(home, ".grok", "bin", "grok.exe")) ?? "", "", "", "acp", "missing"),
            new AiAgentInstallation("zcode", "ZCode", script ?? zcode ?? "", script == null ? "" : Find("node.exe") ?? "", "", "zcode", "missing"),
        };
        return await Task.WhenAll(candidates.Select(async candidate => {
            if (candidate.Id is "codex" or "claude") {
                var roots = InstalledDesktopRoots(candidate.Id).ToArray();
                var desktop = DesktopCandidates(candidate.Id, roots).ToArray();
                var fallback = DesktopFallback(candidate, roots);
                var local = candidate.Path.Length == 0 ? Array.Empty<AiAgentInstallation>() : new[] { candidate };
                return await ProbeCandidatesAsync(fallback, local.Concat(desktop), token).ConfigureAwait(false);
            }
            return await ProbeCandidatesAsync(candidate, new[] { candidate }, token).ConfigureAwait(false);
        })).ConfigureAwait(false);
    }

    internal static async Task<AiAgentInstallation> ProbeCandidatesAsync(AiAgentInstallation fallback,
        IEnumerable<AiAgentInstallation> candidates, CancellationToken token, string runtimeCacheRoot = null)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(24));
        AiAgentInstallation failure = null;
        foreach (var candidate in candidates.DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).Take(4)) {
            if (budget.IsCancellationRequested) { token.ThrowIfCancellationRequested(); break; }
            if (candidate.Path.Length == 0) continue;
            if (candidate.Path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) && candidate.RuntimePath.Length == 0) {
                failure ??= candidate with { Status = "runtime_missing" }; continue;
            }
            try {
                var output = await ProbeAsync(candidate, budget.Token, candidate.Id == "opencode" ? new[] { "acp", "--help" } : candidate.Protocol == "acp" ? new[] { "agent", "--help" } : new[] { "--help" }).ConfigureAwait(false);
                var supported = candidate.Protocol switch {
                    "codex" or "zcode" => output.Contains("app-server", StringComparison.Ordinal),
                    "claude" => output.Contains("stream-json", StringComparison.Ordinal),
                    "acp" => candidate.Id == "opencode" ? output.Contains("ACP", StringComparison.Ordinal) : candidate.Id == "grok" && output.Contains("stdio", StringComparison.Ordinal),
                    _ => false,
                };
                var version = await ProbeAsync(candidate, budget.Token, "--version").ConfigureAwait(false);
                version = version.Trim().Split('\n')[0].Trim();
                var detected = candidate with { Version = version[..Math.Min(version.Length, 120)],
                    Status = supported ? "available" : "incompatible" };
                if (supported) return detected;
                failure ??= detected;
            } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 5) {
                if (candidate.Id == "codex" && candidate.Name.Contains("桌面内置", StringComparison.Ordinal)) {
                    try {
                        var path = await CodexDesktopRuntime.PrepareAsync(candidate.Path, budget.Token, runtimeCacheRoot).ConfigureAwait(false);
                        var cached = candidate with { Path = path, Name = "Codex（桌面引擎缓存）" };
                        var ready = await ProbeCandidatesAsync(cached, new[] { cached }, budget.Token).ConfigureAwait(false);
                        if (ready.Status == "available") return ready;
                        failure ??= ready;
                    } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or OperationCanceledException or System.ComponentModel.Win32Exception) {
                        failure ??= candidate with { Status = "runtime_blocked" };
                    }
                } else failure ??= candidate with { Status = "runtime_blocked" };
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) {
                failure ??= candidate with { Status = "probe_failed" };
            }
        }
        token.ThrowIfCancellationRequested();
        return failure ?? fallback;
    }

    public static ProcessStartInfo StartInfo(AiAgentInstallation agent, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo {
            FileName = agent.RuntimePath.Length > 0 ? agent.RuntimePath : agent.Path,
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (agent.Id == "grok") start.Environment["GROK_DISABLE_AUTOUPDATER"] = "1";
        if (agent.Id == "zcode" && agent.RuntimePath.Length > 0) {
            var config = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(agent.Path), "..", "config", "provider", "zcode-builtin.json"));
            if (File.Exists(config)) {
                start.Environment["ZCODE_BUILTIN_PROVIDER_CONFIG_FILE"] = config;
                start.Environment["ZCODE_PERSONAL_PROVIDER_CONFIG_FILE"] = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "v2", "provider_config.json");
            }
        }
        if (agent.RuntimePath.Length > 0) start.ArgumentList.Add(agent.Path);
        else if (agent.Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) {
            // Only fixed probe/app-server switches reach cmd. All user text travels through stdin.
            if (agent.Path.IndexOfAny("\"&|<>^%!\r\n".ToCharArray()) >= 0 || arguments.Any(a => a.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
                throw new InvalidDataException("Unsupported executable shim path.");
            start.FileName = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Arguments = "/d /s /c \"\"" + agent.Path + "\" " + string.Join(" ", arguments) + "\"";
            return start;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task<string> ProbeAsync(AiAgentInstallation agent, CancellationToken token, params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = Process.Start(StartInfo(agent, System.IO.Path.GetTempPath(), arguments))
            ?? throw new IOException("Agent probe failed.");
        try {
            var stdout = ReadBoundedAsync(process.StandardOutput, 64 * 1024, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Agent probe failed.");
            var result = await stdout.ConfigureAwait(false);
            return result.Length > 0 ? result : await stderr.ConfigureAwait(false);
        } finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0) {
            if (result.Length + count > maximum) throw new IOException("Agent output exceeds its bound.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static string Existing(string path) => File.Exists(path) ? System.IO.Path.GetFullPath(path) : null;
    private static string Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => {
            try { return Existing(System.IO.Path.Combine(p.Trim().Trim('"'), name)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }).FirstOrDefault(p => p != null);

    private static string FindZcodeScript()
    {
        var roots = new List<string>();
        foreach (var process in Process.GetProcessesByName("ZCode")) {
            using (process) { try { if (process.MainModule?.FileName is string executable) roots.Add(System.IO.Path.GetDirectoryName(executable)); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } }
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine }) {
            try {
            using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (var name in uninstall.GetSubKeyNames()) {
                using var entry = uninstall.OpenSubKey(name);
                if ((entry?.GetValue("DisplayName") as string)?.Contains("ZCode", StringComparison.OrdinalIgnoreCase) == true &&
                    entry.GetValue("InstallLocation") is string location && location.Length > 0) roots.Add(location);
            }
            } catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        roots.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ZCode"));
        roots.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZCode"));
        return roots.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => Existing(System.IO.Path.Combine(p, "resources", "glm", "zcode.cjs"))).FirstOrDefault(p => p != null);
    }
}
