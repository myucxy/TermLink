using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using TermLink.Agent;
using TermLink.Ai;

internal static class DesktopHistoryValidation
{
    private const string Cache = "D:/Dev/Env/TermLink-AiRuntimeValidation/cache";

    internal static async Task Security(string directory)
    {
        var source = Path.Combine(directory, "unsigned-runtime", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Copy(Environment.ProcessPath!, source);
        var root = Path.Combine(directory, "must-not-cache");
        try { await CodexDesktopRuntime.PrepareAsync(source, default, root); throw new Exception("Unsigned engine accepted."); }
        catch (CryptographicException) { }
        Check(!Directory.Exists(root), "Unsigned engine created an executable cache.");
        if (Environment.GetEnvironmentVariable("TERMLINK_VALIDATE_SIGNED_DESKTOP_RUNTIME") == "1") {
            var candidate = AiAgentDiscovery.DesktopCandidates("codex", AiAgentDiscovery.InstalledDesktopRoots("codex")).First();
            var executable = await CodexDesktopRuntime.PrepareAsync(candidate.Path, default, Cache);
            using var input = File.OpenRead(candidate.Path);
            var digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            var damagedRoot = Path.Combine("D:/Dev/Env/TermLink-AiRuntimeValidation", "damaged-" + Guid.NewGuid().ToString("N"));
            var damaged = Path.Combine(damagedRoot, digest, "codex.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(damaged)!);
            File.WriteAllBytes(damaged, new byte[] { 1, 2, 3 });
            try { await CodexDesktopRuntime.PrepareAsync(candidate.Path, default, damagedRoot); throw new Exception("Changed cached bytes accepted."); }
            catch (CryptographicException) { }
            Check(File.ReadAllBytes(damaged).SequenceEqual(new byte[] { 1, 2, 3 }), "Damaged runtime silently overwritten.");
            Console.WriteLine("PASS changed runtime cache rejected without overwrite.");
            var redirectedRoot = Path.Combine("D:/Dev/Env/TermLink-AiRuntimeValidation", "redirected-" + Guid.NewGuid().ToString("N"));
            Directory.CreateSymbolicLink(redirectedRoot, Path.GetDirectoryName(executable)!);
            try { await CodexDesktopRuntime.PrepareAsync(candidate.Path, default, redirectedRoot); throw new Exception("Redirected cache accepted."); }
            catch (InvalidDataException) { }
            Console.WriteLine("PASS redirected runtime cache rejected.");
            Console.WriteLine("PASS trusted desktop runtime cached: " + executable);
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await CodexDesktopRuntime.PrepareAsync(source, cancelled.Token, root); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS desktop runtime: unsigned source rejected before copying; cancellation preserved.");
    }

    internal static async Task Real(string directory)
    {
        var roots = AiAgentDiscovery.InstalledDesktopRoots("codex").ToArray();
        var candidates = AiAgentDiscovery.DesktopCandidates("codex", roots).ToArray();
        Check(candidates.Length > 0, "No desktop-owned engine installed.");
        var fallback = new AiAgentInstallation("codex", "Codex Desktop", "", "", "", "codex", "missing");
        var agent = await AiAgentDiscovery.ProbeCandidatesAsync(fallback, candidates, default, Cache);
        Check(agent.Status == "available" && AiAgentSession.SupportsNativeHistory(agent), "Desktop-only native protocol unavailable: " + agent.Status);
        Check(agent.Path.StartsWith(Path.GetFullPath(Cache) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || candidates.Any(c => c.Path == agent.Path), "Desktop test used an unrelated CLI.");
        Console.WriteLine("Desktop-owned engine available; independent CLI excluded.");
        var marker = "TERMLINK_DESKTOP_CONTEXT_" + Guid.NewGuid().ToString("N");
        var caller = Guid.NewGuid();
        // Simulate a desktop origin using its exact bundled executable and native app-server store.
        using var process = Process.Start(AiAgentDiscovery.StartInfo(agent, directory, "app-server"))!;
        var stderr = process.StandardError.ReadToEndAsync();
        var sequence = 0;
        async Task Send(object value) { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value)); await process.StandardInput.FlushAsync(); }
        async Task<JsonElement> Read() {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            using var document = JsonDocument.Parse(line ?? throw new IOException("Desktop process closed."));
            return document.RootElement.Clone();
        }
        async Task<JsonElement> Rpc(string method, object parameters) {
            var id = ++sequence;
            await Send(new { id, method, @params = parameters });
            while (true) {
                var message = await Read();
                if (message.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id) {
                    Check(!message.TryGetProperty("error", out _), "Desktop RPC failed: " + method);
                    return message.GetProperty("result");
                }
            }
        }
        string nativeId;
        try {
            await Rpc("initialize", new { clientInfo = new { name = "Codex Desktop", version = "termlink-validation" }, capabilities = new { experimentalApi = true } });
            await Send(new { method = "initialized" });
            var thread = await Rpc("thread/start", new { cwd = directory, approvalPolicy = "on-request", sandbox = "read-only", excludeTurns = true });
            nativeId = thread.GetProperty("thread").GetProperty("id").GetString()!;
            Console.WriteLine("Created isolated desktop-origin thread: " + nativeId);
            await Rpc("turn/start", new { threadId = nativeId, input = new[] { new { type = "text", text = "Remember this exact marker: " + marker + ". Reply OK only. Do not use tools." } } });
            while (true) {
                var message = await Read();
                if (AiAgentSession.S(message, "method") == "turn/completed") {
                    Check(AiAgentSession.S(AiAgentSession.P(AiAgentSession.P(message, "params"), "turn"), "status") == "completed", "Desktop seed turn failed.");
                    break;
                }
            }
        } finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await stderr; }
        Console.WriteLine("Desktop seed reply completed; locating native history.");
        AiNativeHistoryEntry entry = null;
        var cursor = "";
        for (var n = 0; n < 12 && entry == null; n++) {
            var page = await AiAgentSession.NativeHistoryAsync(agent, cursor, default);
            entry = page.Sessions.FirstOrDefault(e => e.NativeId == nativeId); cursor = page.NextCursor;
            if (cursor.Length == 0) break;
        }
        Check(entry != null, "Desktop-origin history missing from TermLink.");
        Console.WriteLine("Native history listed; resuming the same thread through TermLink.");
        using var imported = AiAgentSession.Import(agent, entry, caller, null);
        await imported.StartAsync();
        Console.WriteLine("Native resume completed; checking history and sending continuation.");
        var record = imported.ArchiveRecord();
        Check(record.NativeId == nativeId, "Resume created a different native thread.");
        Check(AiAgentSession.S(record.NativePermissions.Sandbox, "type") == "readOnly", "Native sandbox changed.");
        Check(record.Messages.Any(m => m.Role == "user" && m.Text.Contains(marker)), "Desktop history body was not loaded.");
        await imported.ApplyAsync(caller, new("send", ControlVersion: 1, OperationId: Guid.NewGuid().ToString(), Text: "Reply only with the exact marker from my earlier message. Do not use tools."));
        for (var n = 0; n < 1200; n++) {
            var snapshot = JsonSerializer.SerializeToElement(imported.Snapshot(caller, true), AiSessionManager.Json);
            Check(AiAgentSession.S(snapshot, "error").Length == 0, "Native continuation reported error.");
            if (AiAgentSession.S(snapshot, "state") != "running") break;
            await Task.Delay(100);
        }
        var result = imported.ArchiveRecord();
        Check(result.Messages.Last(m => m.Role == "assistant").Text.Contains(marker), "Resumed reply lost desktop context.");
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, desktopEngine = agent.Path, version = agent.Version,
            threadId = nativeId, sameNativeThread = true, historicalPermissionPreserved = true, contextReplyMatched = true,
            independentCliUsed = false, messages = result.Messages.Length }));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
