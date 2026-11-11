// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Ai;
using TermLink.Platform.Unix;

namespace TermLink.Agent.AI;

/// <summary>
/// Public macOS Codex CLI only. Installation/protocol support never asserts a
/// logged-in model account. TermLink's Codex home is intentionally independent.
/// </summary>
internal sealed class PortableCodexDiscovery
{
    private readonly string runtime;
    private readonly string sqlite;
    private readonly Func<string, string> find;
    private readonly bool supportedPlatform;

    internal PortableCodexDiscovery(string accountDirectory, Func<string, string> executableLookup = null,
        bool? supportedPlatform = null)
    {
        if (!Path.IsPathFullyQualified(accountDirectory)) throw new ArgumentException("AI_DIRECTORY_INVALID");
        runtime = PrivateDirectory(Path.Combine(accountDirectory, "codex-runtime"));
        sqlite = PrivateDirectory(Path.Combine(runtime, "sqlite"));
        find = executableLookup ?? (name => ExecutableLocator.Find(name));
        this.supportedPlatform = supportedPlatform ?? OperatingSystem.IsMacOS();
    }

    internal async Task<AiAgentInstallation> DiscoverAsync(CancellationToken token)
    {
        var missing = new AiAgentInstallation("codex", "Codex", "", "", "", "codex", "missing", "native");
        token.ThrowIfCancellationRequested();
        if (!supportedPlatform) return missing;
        // Only Codex and its possible public npm/bun runtime are looked up.
        // The other four products are not probed by this native-owner discovery.
        var local = new PosixAiAgentDiscovery(
            name => name is "codex" or "node" or "nodejs" or "bun" ? find(name) : null,
            desktopApplicationExists: _ => false, configureStartInfo: Configure);
        var capability = (await local.DiscoverAsync(token).ConfigureAwait(false)).Single(agent => agent.Id == "codex");
        var status = capability.Status == "detected"
            ? capability.ProtocolStatus == "available" ? "available" : capability.ProtocolStatus == "incompatible" ? "incompatible" : "probe_failed"
            : capability.Status;
        var candidate = missing with { Path = capability.Path, Version = capability.Version, Status = status,
            SessionDataDirectory = runtime, ConfigurationDataDirectory = runtime };
        if (status != "available") return candidate;
        try
        {
            var launch = PosixCliLaunchDescriptor.Resolve(capability.Path, name => find(name));
            if (!launch.NativeAgentCompatible) return candidate with { Status = "incompatible" };
            return candidate with { RuntimePath = launch.ExecutablePath == launch.CliPath ? "" : launch.ExecutablePath,
                PortableStartInfo = (directory, arguments) => {
                    var start = launch.CreateStartInfo(directory, arguments);
                    Configure(start);
                    return start;
                } };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return candidate with { Status = "runtime_missing" };
        }
    }

    private void Configure(ProcessStartInfo start)
    {
        // Do not copy auth.json/config.toml, use Keychain fallback, import the
        // user's thread DB or borrow a provider token from TermLink's process.
        start.Environment["CODEX_HOME"] = runtime;
        start.Environment["CODEX_SQLITE_HOME"] = sqlite;
        foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_ACCESS_TOKEN", "CHATGPT_ACCESS_TOKEN",
            "CODEX_AUTH_FILE", "CODEX_CONFIG", "CODEX_CONFIG_FILE" }) start.Environment.Remove(name);
        // Runtime configuration is inherited from no other Codex home. File
        // credential storage prevents implicit system-Keychain authorization.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("sqlite_home=" + System.Text.Json.JsonSerializer.Serialize(sqlite));
    }

    private static string PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("AI_DIRECTORY_LINK_REJECTED");
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
