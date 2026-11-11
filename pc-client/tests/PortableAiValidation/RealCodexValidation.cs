// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TermLink.Agent.AI;
using TermLink.Ai;

/// <summary>Optional installed binary check: no thread, turn, model call or credential read.</summary>
internal static class RealCodexValidation
{
    private const string Marker = "TERMLINK_CODEX_VALIDATION_ROOT";

    internal static async Task RunSandboxedAsync(string directory)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Real Codex validation requires macOS");
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("A new absolute evidence directory is required");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var canary = Path.Combine(directory, "denied-nonsecret-canary");
        await File.WriteAllTextAsync(canary, "nonsecret validation marker");
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var policy = "(version 1)\n(allow default)\n(deny network*)\n(allow network* (local unix-socket))\n(allow network* (remote unix-socket))\n" +
            string.Concat(new[] { Path.Combine(home, ".codex"), Path.Combine(home, "Library", "Keychains") }.Select(path =>
                "(deny file-read* file-write* (subpath " + Quote(path) + "))\n")) +
            "(deny file-read* file-write* (literal " + Quote(canary) + "))\n";
        var profile = Path.Combine(directory, "isolation.sb");
        await File.WriteAllTextAsync(profile, policy);
        var start = new ProcessStartInfo("/usr/bin/sandbox-exec") { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var argument in new[] { "-f", profile, Environment.ProcessPath!, "--real-codex-child", directory }) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(name => name.Contains("KEY", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment[Marker] = directory;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("CODEX_VALIDATION_START_FAILED");
        using var cancellation = timeout.Token.Register(() => Kill(process));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            // No raw app-server output/error or initialize user-agent is logged.
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(directory, "report.json"))) throw new IOException("CODEX_ISOLATED_VALIDATION_FAILED");
            foreach (var line in (await stdout).Split('\n').Where(line => line.StartsWith("PASS real Codex ", StringComparison.Ordinal))) Console.WriteLine(line);
        }
        finally { Kill(process); }
    }

    internal static async Task RunChildAsync(string directory)
    {
        if (Environment.GetEnvironmentVariable(Marker) != directory) throw new InvalidOperationException("Sandbox entry required");
        var denied = false;
        try { using var ignored = File.OpenRead(Path.Combine(directory, "denied-nonsecret-canary")); }
        catch (UnauthorizedAccessException) { denied = true; }
        if (!denied) throw new InvalidOperationException("Sandbox denial was not enforced");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var agent = await new PortableCodexDiscovery(directory).DiscoverAsync(timeout.Token);
        if (agent.Status != "available") throw new InvalidOperationException("Installed Codex public protocol is unavailable");
        var start = AiAgentDiscovery.StartInfo(agent, directory, "app-server");
        using var process = Process.Start(start) ?? throw new IOException("CODEX_INITIALIZE_START_FAILED");
        using var cancellation = timeout.Token.Register(() => Kill(process));
        var bytes = 0;
        void Account(int size) { if (Interlocked.Add(ref bytes, size) > 65536) { timeout.Cancel(); throw new IOException("CODEX_PROBE_OUTPUT_LIMIT"); } }
        async Task DrainErrors()
        {
            var buffer = new char[2048]; int count;
            while ((count = await process.StandardError.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0) Account(Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count)));
        }
        async Task ReadInitialize()
        {
            var line = new StringBuilder(); var buffer = new char[2048]; int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                Account(Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count)));
                foreach (var value in buffer.AsSpan(0, count).ToArray())
                {
                    if (value != '\n') { line.Append(value); continue; }
                    using var document = JsonDocument.Parse(line.ToString()); line.Clear();
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != 1) continue;
                    if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                        throw new IOException("CODEX_INITIALIZE_REJECTED");
                    return;
                }
            }
            throw new IOException("CODEX_INITIALIZE_EOF");
        }
        var errors = DrainErrors();
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id = 1, method = "initialize", @params = new {
                clientInfo = new { name = "termlink_validation", title = "TermLink compatibility validation", version = "0.3.16" } } }).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            await ReadInitialize().WaitAsync(timeout.Token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(3), timeout.Token); }
            catch (TimeoutException) { Kill(process); await process.WaitForExitAsync(timeout.Token); }
            await errors;
            await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new {
                installedVersion = agent.Version, protocol = agent.Protocol, nativePathVerified = true,
                initializeSucceeded = true, initializedSent = true, authentication = "not_checked",
                threadRequests = 0, turnRequests = 0, modelRequests = 0, existingCredentialsRead = false,
                existingSessionDatabaseShared = false, ipNetworkAllowed = false, processExited = process.HasExited,
                codexHome = agent.SessionDataDirectory, boundedOutputBytes = bytes,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS real Codex public native CLI version/help + isolated stdio initialize/initialized; no thread/turn/model request, no existing credentials or database");
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
        }
    }

    private static void Kill(Process process)
    { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
}
