// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Ai;
using TermLink.Platform.Unix;

namespace TermLink.Agent.AI;

/// <summary>Runs the installed ZCode CLI with an external native Node runtime, never Electron.</summary>
internal sealed class PortableZcodeDiscovery
{
    private static readonly HashSet<string> OrdinaryEnvironment = new(StringComparer.Ordinal) {
        "HOME", "USER", "LOGNAME", "SHELL", "PATH", "TMPDIR", "TMP", "TEMP", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "TZ", "TERM", "COLORTERM"
    };
    private static readonly Regex NodeVersion = new(@"^v[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex NvmVersion = new(@"^v(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private const int MaximumNvmEntries = 64;
    private const int MaximumNvmCandidates = 8;
    private readonly string runtime;
    private readonly Func<string[]> applications;
    private readonly Func<string, string> findNode;
    private readonly string nodeHome;

    internal PortableZcodeDiscovery(string accountDirectory, Func<string[]> applications = null, Func<string, string> findNode = null, string nodeHome = null)
    {
        PortableAiRuntimeDirectory.Ensure(accountDirectory);
        runtime = PortableAiRuntimeDirectory.Ensure(Path.Combine(accountDirectory, "zcode-runtime"));
        PortableAiRuntimeDirectory.Ensure(Path.Combine(runtime, ".zcode"));
        PortableAiRuntimeDirectory.Ensure(Path.Combine(runtime, ".zcode", "v2"));
        this.applications = applications ?? (() => new[] { "/Applications/ZCode.app",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "ZCode.app") });
        this.findNode = findNode ?? (name => ExecutableLocator.Find(name));
        // Injected lookups stay isolated unless a private test home is supplied.
        this.nodeHome = nodeHome ?? (findNode == null ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : null);
    }

    internal async Task<AiAgentInstallation> DiscoverAsync(CancellationToken token)
    {
        var missing = new AiAgentInstallation("zcode", "ZCode", "", "", "", "zcode", "missing", "native")
            { ConfigurationDataDirectory = runtime, SessionDataDirectory = runtime };
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) return missing;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        foreach (var app in applications())
        {
            var agent = missing with { Path = Path.Combine(app, "Contents", "Resources", "glm", "zcode.cjs") };
            if (!File.Exists(agent.Path)) continue;
            // An installed script with no usable external Node is different from
            // a missing installation. There is deliberately no Electron fallback.
            agent = agent with { Status = "runtime_missing" };
            var usableNode = false;
            try
            {
                ValidateScript(agent.Path);
                AiAgentInstallation failure = null;
                foreach (var candidate in NodeCandidates(deadline.Token))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var candidateAgent = agent;
                    var verifiedNode = false;
                    try
                    {
                        var node = ResolveNode(candidate.Path);
                        if (candidate.NvmBin != null && !string.Equals(Path.GetDirectoryName(node), candidate.NvmBin, StringComparison.Ordinal)) continue;
                        var version = await RunAsync(CreateNodeStartInfo(node, Path.GetTempPath(), new[] { "--version" }), deadline.Token).ConfigureAwait(false);
                        if (!NodeVersion.IsMatch(version.Trim())) continue;
                        candidateAgent = agent with { RuntimePath = node };
                        verifiedNode = usableNode = true;
                        var help = await RunAsync(CreateStartInfo(candidateAgent, Path.GetTempPath(), new[] { "--help" }), deadline.Token).ConfigureAwait(false);
                        var zcodeVersion = (await RunAsync(CreateStartInfo(candidateAgent, Path.GetTempPath(), new[] { "--version" }), deadline.Token).ConfigureAwait(false))
                            .Trim().Split('\n')[0].Trim();
                        // Each candidate must run this CLI, not just report a Node
                        // version. An incompatible newer Node may fall back safely.
                        var result = candidateAgent with { Version = zcodeVersion[..Math.Min(zcodeVersion.Length, 120)],
                            Status = help.Contains("app-server", StringComparison.Ordinal) ? "available" : "incompatible" };
                        if (result.Status == "available") return result with { PortableStartInfo = (directory, args) => CreateStartInfo(result, directory, args) };
                        failure ??= result;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                    {
                        if (verifiedNode) failure ??= candidateAgent with { Status = "probe_failed" };
                    }
                }
                return failure ?? agent;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (UnauthorizedAccessException) { return agent with { Status = "runtime_blocked" }; }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { return agent with { Status = usableNode ? "probe_failed" : "runtime_missing" }; }
        }
        return missing;
    }

    private IEnumerable<(string Path, string NvmBin)> NodeCandidates(CancellationToken token)
    {
        foreach (var name in new[] { "node", "nodejs" })
        {
            token.ThrowIfCancellationRequested();
            var path = findNode(name);
            if (path != null) yield return (path, null);
        }
        foreach (var path in NvmCandidates(token)) yield return (path, Path.GetDirectoryName(path));
    }

    private string[] NvmCandidates(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nodeHome) || !Path.IsPathFullyQualified(nodeHome) || nodeHome.Contains('\0')) return Array.Empty<string>();
        try
        {
            // This exact public installation tree is the only home directory
            // enumerated. No shell profile, alias, .nvmrc or nvm command is read.
            var root = ValidateNvmDirectory(Path.Combine(nodeHome, ".nvm", "versions", "node"));
            var versions = new List<(Version Version, string Path)>();
            var entries = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                token.ThrowIfCancellationRequested();
                // Fail closed on an oversized tree rather than select a random
                // filesystem prefix or scan an unbounded number of entries.
                if (++entries > MaximumNvmEntries) return Array.Empty<string>();
                var name = Path.GetFileName(path);
                if (!NvmVersion.IsMatch(name) || !Version.TryParse(name[1..], out var version)) continue;
                try
                {
                    var bin = ValidateNvmDirectory(Path.Combine(ValidateNvmDirectory(path), "bin"));
                    versions.Add((version, Path.Combine(bin, "node")));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return versions.OrderByDescending(item => item.Version).ThenBy(item => item.Path, StringComparer.Ordinal)
                .Take(MaximumNvmCandidates).Select(item => item.Path).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return Array.Empty<string>(); }
    }

    private static string ValidateNvmDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var alias in new[] { "/tmp", "/var", "/etc" })
            if (full == alias || full.StartsWith(alias + "/", StringComparison.Ordinal)) { full = "/private" + full; break; }
        if ((File.GetAttributes(full) & FileAttributes.Directory) == 0) throw new IOException("AI_RUNTIME_MISSING");
        var current = full;
        while (!string.IsNullOrEmpty(current) && current != Path.GetPathRoot(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("AI_DIRECTORY_LINK_REJECTED");
            current = Path.GetDirectoryName(current);
        }
        return full;
    }

    internal ProcessStartInfo CreateStartInfo(AiAgentInstallation agent, string directory, string[] arguments)
    {
        ValidateScript(agent.Path);
        var node = ResolveNode(agent.RuntimePath);
        var start = CreateNodeStartInfo(node, directory, Array.Empty<string>());
        // The existing helper locates bundled provider metadata using agent.Path;
        // a runtime named node/nodejs never selects ELECTRON_RUN_AS_NODE.
        ZcodeDesktopRuntime.Configure(start, agent with { RuntimePath = node }, arguments);
        start.Environment.Remove("ELECTRON_RUN_AS_NODE");
        start.ArgumentList.Add(agent.Path);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private ProcessStartInfo CreateNodeStartInfo(string node, string directory, string[] arguments)
    {
        var start = PosixCliLaunchDescriptor.Resolve(node).CreateStartInfo(directory, arguments);
        ConfigureProbeEnvironment(start);
        return start;
    }

    private static string ResolveNode(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('\0')) throw new IOException("AI_RUNTIME_MISSING");
        var info = new FileInfo(path);
        var target = info.ResolveLinkTarget(true);
        var resolved = target?.FullName ?? info.FullName;
        // Do not accept an Electron executable renamed/symlinked as node, nor
        // an embedded app engine. Node must be an external installed executable.
        if (Path.GetFileName(resolved) is not ("node" or "nodejs") ||
            resolved.Split(Path.DirectorySeparatorChar).Any(part => part.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("AI_RUNTIME_MISSING");
        var descriptor = PosixCliLaunchDescriptor.Resolve(resolved);
        if (!descriptor.NativeAgentCompatible || descriptor.PrefixArguments.Count != 0 || descriptor.ExecutablePath != descriptor.CliPath)
            throw new IOException("AI_RUNTIME_MISSING");
        using var binary = File.OpenRead(resolved);
        Span<byte> magic = stackalloc byte[4];
        if (binary.Read(magic) != magic.Length) throw new IOException("AI_RUNTIME_MISSING");
        var value = (uint)magic[0] << 24 | (uint)magic[1] << 16 | (uint)magic[2] << 8 | magic[3];
        if (value is not (0xfeedface or 0xfeedfacf or 0xcefaedfe or 0xcffaedfe or 0xcafebabe or 0xbebafeca or 0xcafebabf or 0xbfbafeca))
            throw new IOException("AI_RUNTIME_MISSING");
        return resolved;
    }

    private static void ValidateScript(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "zcode.cjs" || !File.Exists(path)) throw new IOException("AI_EXECUTABLE_CHANGED");
        var current = Path.GetFullPath(path);
        foreach (var alias in new[] { "/tmp", "/var", "/etc" })
            if (current == alias || current.StartsWith(alias + "/", StringComparison.Ordinal)) { current = "/private" + current; break; }
        while (!string.IsNullOrEmpty(current) && current != Path.GetPathRoot(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("AI_DIRECTORY_LINK_REJECTED");
            current = Path.GetDirectoryName(current);
        }
    }

    internal void ConfigureProbeEnvironment(ProcessStartInfo start)
    {
        PortableAiRuntimeDirectory.Ensure(runtime);
        PortableAiRuntimeDirectory.Ensure(Path.Combine(runtime, ".zcode"));
        var providerDirectory = PortableAiRuntimeDirectory.Ensure(Path.Combine(runtime, ".zcode", "v2"));
        var config = Path.Combine(providerDirectory, "provider_config.json");
        var providerFile = new FileInfo(config);
        if (providerFile.LinkTarget != null || providerFile.Exists && (providerFile.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("AI_DIRECTORY_LINK_REJECTED");
        foreach (var name in start.Environment.Keys.Where(name => !OrdinaryEnvironment.Contains(name)).ToArray()) start.Environment.Remove(name);
        start.Environment["ZCODE_PERSONAL_PROVIDER_CONFIG_FILE"] = config;
        start.Environment["ZCODE_DATA_BASE_DIR"] = runtime;
        start.Environment["ZCODE_STORAGE_DIR"] = runtime;
        start.Environment["ZCODE_SESSION_DB_PATH"] = Path.Combine(runtime, "cli", "db", "db.sqlite");
    }

    private static async Task<string> RunAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = Process.Start(start) ?? throw new IOException("ZCODE_START_FAILED");
        try
        {
            var output = AiAgentDiscovery.ReadBoundedAsync(process.StandardOutput, 64 * 1024, token);
            var errors = AiAgentDiscovery.ReadBoundedAsync(process.StandardError, 64 * 1024, token);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors, process.WaitForExitAsync(token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("ZCODE_PROBE_FAILED");
            return await output.ConfigureAwait(false);
        }
        finally { try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } } catch (InvalidOperationException) { } }
    }
}
