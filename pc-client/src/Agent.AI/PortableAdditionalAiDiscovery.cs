// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Ai;
using TermLink.Pc.Contracts;
using TermLink.Platform.Unix;

namespace TermLink.Agent.AI;

/// <summary>
/// Discovers public Claude Code, OpenCode and Grok Build CLIs. An available
/// protocol is not verified authentication: each account must configure/login
/// in its own runtime. No credentials or user configuration are imported.
/// </summary>
internal sealed class PortableAdditionalAiDiscovery
{
    // Documented directory contracts, checked 2026-11-11:
    // https://code.claude.com/docs/en/authentication#log-in-with-multiple-accounts
    // https://platform.claude.com/docs/en/manage-claude/wif-reference#configuration-directory
    // https://github.com/anomalyco/opencode/blob/dev/packages/util/src/global-roots.ts
    // https://docs.x.ai/build/settings/reference#paths-and-auth
    private static readonly (string Id, string Name, string Protocol)[] Agents =
    {
        ("claude", "Claude Code", "claude"),
        ("opencode", "OpenCode", "acp"),
        ("grok", "Grok Build", "acp"),
    };
    private static readonly HashSet<string> ProcessEnvironment = new(StringComparer.Ordinal)
    {
        "HOME", "USER", "LOGNAME", "SHELL", "PATH", "TMPDIR", "TMP", "TEMP",
        "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "LC_MESSAGES", "LC_TIME",
        "LC_NUMERIC", "LC_COLLATE", "LC_MONETARY", "TZ", "TERM", "COLORTERM",
    };
    private readonly string accountDirectory;
    private readonly Func<string, string> find;
    private readonly bool supportedPlatform;

    internal PortableAdditionalAiDiscovery(string accountDirectory, Func<string, string> executableLookup = null,
        bool? supportedPlatform = null)
    {
        if (!Path.IsPathFullyQualified(accountDirectory)) throw new ArgumentException("AI_DIRECTORY_INVALID");
        this.accountDirectory = Path.GetFullPath(accountDirectory);
        PrivateDirectory(this.accountDirectory);
        find = executableLookup ?? (name => ExecutableLocator.Find(name));
        this.supportedPlatform = supportedPlatform ?? OperatingSystem.IsMacOS();
    }

    internal async Task<AiAgentInstallation[]> DiscoverAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await Task.WhenAll(Agents.Select(agent => DiscoverAsync(agent.Id, agent.Name, agent.Protocol, token)))
            .ConfigureAwait(false);
    }

    private async Task<AiAgentInstallation> DiscoverAsync(string id, string name, string protocol, CancellationToken token)
    {
        var candidate = new AiAgentInstallation(id, name, "", "", "", protocol, "missing", "native");
        if (!supportedPlatform) return candidate;
        var runtime = Path.Combine(accountDirectory, id + "-runtime");
        var configuration = id == "opencode" ? Path.Combine(runtime, "config", "opencode") : runtime;
        var data = id == "opencode" ? Path.Combine(runtime, "data", "opencode") : runtime;
        candidate = candidate with { ConfigurationDataDirectory = configuration, SessionDataDirectory = data };
        try
        {
            // One provider per discovery gives probes the same isolated environment
            // as the later launcher, without inspecting another provider's files.
            var local = new PosixAiAgentDiscovery(
                executableLookup: executable => executable == id || executable is "node" or "nodejs" or "bun" ? find(executable) : null,
                desktopApplicationExists: _ => false,
                configureStartInfo: start => Configure(start, id, runtime));
            var capability = (await local.DiscoverAsync(token).ConfigureAwait(false)).Single(agent => agent.Id == id);
            candidate = candidate with { Path = capability.Path, Version = capability.Version, Status = Status(capability) };
            if (candidate.Status != "available") return candidate;
            var launch = PosixCliLaunchDescriptor.Resolve(capability.Path, find);
            if (!launch.NativeAgentCompatible) return candidate with { Status = "incompatible" };
            return candidate with
            {
                RuntimePath = launch.ExecutablePath == launch.CliPath ? "" : launch.ExecutablePath,
                PortableStartInfo = (directory, arguments) =>
                {
                    var start = launch.CreateStartInfo(directory, arguments, disableGrokUpdater: id == "grok");
                    Configure(start, id, runtime);
                    return start;
                },
            };
        }
        catch (UnauthorizedAccessException) { return candidate with { Status = "runtime_blocked" }; }
        catch (Exception error) when (error is IOException or ArgumentException)
        {
            // Resolution can fail if an executable/runtime disappears after the
            // help probe. Never retain an available entry without its launcher.
            return candidate with { Status = "runtime_missing" };
        }
    }

    private static string Status(AiAgentCapability capability)
    {
        if (capability.Status != "detected") return capability.Status;
        // Help may fail after a successful version probe; keep runtime failures
        // distinct from protocol incompatibility and other probe failures.
        return capability.DetectionCode switch
        {
            "AI_RUNTIME_MISSING" => "runtime_missing",
            "AI_RUNTIME_BLOCKED" => "runtime_blocked",
            _ => capability.ProtocolStatus switch
            {
                "available" => "available",
                "incompatible" => "incompatible",
                _ => "probe_failed",
            },
        };
    }

    private void Configure(ProcessStartInfo start, string id, string runtime)
    {
        // Retain only ordinary process settings, including the descriptor's safe
        // PATH and the unchanged HOME. Provider credentials, config overrides,
        // launch injection variables and TermLink access tokens are not inherited.
        // This also excludes arbitrary provider API-key variable names.
        foreach (var key in start.Environment.Keys.Where(key => !ProcessEnvironment.Contains(key)).ToArray())
            start.Environment.Remove(key);
        PrivateDirectory(accountDirectory);
        PrivateDirectory(runtime);
        switch (id)
        {
            case "claude":
                start.Environment["CLAUDE_CONFIG_DIR"] = runtime;
                // Console OAuth/WIF profiles live outside CLAUDE_CONFIG_DIR on
                // newer CLIs; the documented SDK directory must be isolated too.
                start.Environment["ANTHROPIC_CONFIG_DIR"] = PrivateDirectory(Path.Combine(runtime, "anthropic"));
                break;
            case "opencode":
                foreach (var (variable, child) in new[]
                {
                    ("XDG_CONFIG_HOME", "config"), ("XDG_DATA_HOME", "data"),
                    ("XDG_STATE_HOME", "state"), ("XDG_CACHE_HOME", "cache"),
                })
                {
                    var directory = PrivateDirectory(Path.Combine(runtime, child));
                    PrivateDirectory(Path.Combine(directory, "opencode"));
                    start.Environment[variable] = directory;
                }
                break;
            case "grok":
                // GROK_HOME is documented to include auth and sessions as well
                // as configuration. Do not invent a separate auth-path override.
                start.Environment["GROK_HOME"] = runtime;
                start.Environment["GROK_DISABLE_AUTOUPDATER"] = "1";
                // Those compatibility scanners otherwise load another product's
                // user settings/hooks/MCP definitions despite GROK_HOME isolation.
                foreach (var product in new[] { "CURSOR", "CLAUDE" })
                    foreach (var kind in new[] { "SKILLS", "RULES", "AGENTS", "MCPS", "HOOKS" })
                        start.Environment["GROK_" + product + "_" + kind + "_ENABLED"] = "false";
                break;
        }
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
