using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Ai;

// Consume the installed desktop's Agent; do not extract, alter or redistribute it.
public static class ZcodeDesktopRuntime
{
    public static async Task<AiAgentInstallation> DetectMacAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) return new("zcode", "ZCode", "", "", "", "zcode", "missing");
        var candidates = new[] {
            "/Applications/ZCode.app",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "ZCode.app"),
        };
        foreach (var app in candidates) {
            var agent = new AiAgentInstallation("zcode", "ZCode", Path.Combine(app, "Contents", "Resources", "glm", "zcode.cjs"),
                Path.Combine(app, "Contents", "MacOS", "ZCode"), "", "zcode", "missing");
            if (!File.Exists(agent.Path) || !File.Exists(agent.RuntimePath)) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try {
                var help = await ProbeAsync(agent, "--help", deadline.Token).ConfigureAwait(false);
                var version = (await ProbeAsync(agent, "--version", deadline.Token).ConfigureAwait(false)).Trim().Split('\n')[0].Trim();
                return agent with { Version = version[..Math.Min(version.Length, 120)],
                    Status = help.Contains("app-server", StringComparison.Ordinal) ? "available" : "incompatible" };
            } catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                System.ComponentModel.Win32Exception or OperationCanceledException) { return agent with { Status = "probe_failed" }; }
        }
        return new("zcode", "ZCode", "", "", "", "zcode", "missing");
    }

    internal static void Configure(ProcessStartInfo start, AiAgentInstallation agent, string[] arguments)
    {
        if (Path.GetFileName(agent.RuntimePath) is "ZCode" or "ZCode.exe")
            start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        var bundled = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(agent.Path)!, "..", "config", "provider", "zcode-builtin.json"));
        if (File.Exists(bundled)) {
            start.Environment["ZCODE_BUILTIN_PROVIDER_CONFIG_FILE"] = bundled;
            start.Environment["ZCODE_BUILTIN_PROVIDER_BUNDLED_CONFIG_FILE"] = bundled;
            start.Environment["ZCODE_PERSONAL_PROVIDER_CONFIG_FILE"] = Path.Combine(
                agent.ConfigurationDataDirectory.Length > 0 ? agent.ConfigurationDataDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "v2", "provider_config.json");
            if (agent.ConfigurationDataDirectory.Length > 0) start.Environment["ZCODE_DATA_BASE_DIR"] = agent.ConfigurationDataDirectory;
        }
        // The storage root alone does not override the CLI's SQLite default.
        // Use account-scoped TermLink storage; never open a desktop/CLI session DB.
        if (agent.SessionDataDirectory.Length > 0 && arguments.Contains("app-server", StringComparer.Ordinal)) {
            start.Environment["ZCODE_STORAGE_DIR"] = agent.SessionDataDirectory;
            start.Environment["ZCODE_SESSION_DB_PATH"] = Path.Combine(agent.SessionDataDirectory, "cli", "db", "db.sqlite");
        }
    }

    private static async Task<string> ProbeAsync(AiAgentInstallation agent, string argument, CancellationToken token)
    {
        using var process = Process.Start(AiAgentDiscovery.StartInfo(agent, Path.GetTempPath(), argument)) ?? throw new IOException("ZCODE_START_FAILED");
        try {
            var output = AiAgentDiscovery.ReadBoundedAsync(process.StandardOutput, 64 * 1024, token);
            var errors = AiAgentDiscovery.ReadBoundedAsync(process.StandardError, 64 * 1024, token);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors, process.WaitForExitAsync(token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("ZCODE_PROBE_FAILED");
            return await output.ConfigureAwait(false);
        } finally {
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (InvalidOperationException) { }
        }
    }
}
