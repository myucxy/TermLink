using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TermLink.Ai;

public static partial class AiAgentDiscovery
{
    internal static IEnumerable<AiAgentInstallation> DesktopCandidates(string id, IEnumerable<string> roots, string claudeCodeRoot = null)
    {
        var binaries = id == "codex" ? new[] { "resources/codex.exe", "app/resources/codex.exe" }
            : new[] { "resources/claude-code/claude.exe", "app/resources/claude-code/claude.exe" };
        foreach (var root in roots) {
            foreach (var relative in binaries) {
                var path = Existing(System.IO.Path.Combine(root, relative));
                if (path != null) yield return new(id, id == "codex" ? "Codex（桌面内置）" : "Claude Code（桌面内置）",
                    path, "", "", id, "missing");
            }
        }
        if (id == "claude") {
            // Desktop downloads its Code engine separately from the graphical launcher.
            var cache = claudeCodeRoot ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code");
            foreach (var version in VersionDirectories(cache, "*")) {
                var path = Existing(System.IO.Path.Combine(version, "claude.exe"));
                if (path != null) yield return new(id, "Claude Code（桌面内置）", path, "", "", id, "missing");
            }
        }
    }

    internal static AiAgentInstallation DesktopFallback(AiAgentInstallation candidate, IEnumerable<string> roots)
    {
        foreach (var root in roots) {
            var names = candidate.Id == "codex" ? new[] { "ChatGPT.exe", "Codex.exe", "GPTApp.exe" }
                : new[] { "Claude.exe", "Claude Desktop.exe" };
            foreach (var name in names) {
                var path = Existing(System.IO.Path.Combine(root, name)) ?? Existing(System.IO.Path.Combine(root, "app", name));
                if (path != null) return candidate with { Name = candidate.Id == "codex" ? "Codex / ChatGPT 桌面版" : "Claude Desktop",
                    Path = path, Status = "desktop_only" };
            }
        }
        return candidate;
    }

    internal static IEnumerable<string> InstalledDesktopRoots(string id)
    {
        var roots = new List<string>();
        var names = id == "codex" ? new[] { "Codex", "ChatGPT", "GPTApp" } : new[] { "Claude", "Claude Desktop" };
        foreach (var name in names) {
            foreach (var process in Process.GetProcessesByName(name)) {
                using (process) {
                    try { if (process.MainModule?.FileName is string path) roots.Add(System.IO.Path.GetDirectoryName(path)); }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                }
            }
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine }) {
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" }) {
                try {
                    using var uninstall = hive.OpenSubKey(key);
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames()) {
                        using var entry = uninstall.OpenSubKey(name);
                        var display = entry?.GetValue("DisplayName") as string ?? "";
                        if (names.Any(n => display.Contains(n, StringComparison.OrdinalIgnoreCase)) &&
                            entry?.GetValue("InstallLocation") is string location && location.Length > 0) roots.Add(location);
                    }
                } catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        // Query current-user registrations, including Store packages on other drives; never scan WindowsApps.
        try {
            using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages != null) {
                var prefixes = id == "codex" ? new[] { "OpenAI.Codex_", "OpenAI.ChatGPT-Desktop_", "OpenAI.ChatGPT_" } : new[] { "Claude_", "Anthropic.Claude_" };
                foreach (var name in packages.GetSubKeyNames().Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(n => Version.TryParse(n.Split('_')[1], out var v) ? v : new Version())) {
                    using var package = packages.OpenSubKey(name);
                    if (package?.GetValue("PackageRootFolder") is string root && root.Length > 0) roots.Add(root);
                }
            }
        } catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var name in names) {
            roots.Add(System.IO.Path.Combine(local, "Programs", name));
            roots.Add(System.IO.Path.Combine(local, name));
            roots.Add(System.IO.Path.Combine(programFiles, name));
        }
        if (id == "claude") roots.Add(System.IO.Path.Combine(local, "AnthropicClaude"));
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(root => new[] { root }.Concat(VersionDirectories(root, "app-*"))).ToArray();
    }

    private static string[] VersionDirectories(string root, string pattern)
    {
        try {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(root, pattern).Take(64)
                .Select(path => new { Path = path, Version = Version.TryParse(System.IO.Path.GetFileName(path).Replace("app-", ""), out var v) ? v : null })
                .Where(item => item.Version != null).OrderByDescending(item => item.Version).Take(4).Select(item => item.Path).ToArray();
        } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return Array.Empty<string>(); }
    }
}
