// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using TermLink.Agent;
using TermLink.Ai;

internal static class AiRuntimeAuthenticationValidation
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var trace = Path.Combine(root, "trace");
        var before = Environment.GetEnvironmentVariable(PortableCodexFixture.TraceVariable);
        Environment.SetEnvironmentVariable(PortableCodexFixture.TraceVariable, trace);
        try
        {
            var installation = new AiAgentInstallation("codex", "Codex", Environment.ProcessPath!, "", "fixture", "codex", "available")
            {
                ConfigurationDataDirectory = root, SessionDataDirectory = root,
                PortableStartInfo = (directory, args) =>
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = directory,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in args) start.ArgumentList.Add(arg);
                    return start;
                }
            };
            using var manager = new AiSessionManager(root, catalog: () => Task.FromResult(new[] { installation }));
            var caller = Guid.NewGuid();
            var catalog = await Execute("catalog");
            var auth = catalog.GetProperty("agents")[0].GetProperty("runtimeAuthentication");
            Require(auth.GetProperty("isolated").GetBoolean() && auth.GetProperty("canStart").GetBoolean(), "Isolated auth metadata absent");
            var unknown = await Execute("authentication");
            Require(unknown.GetProperty("authentication").GetProperty("status").GetString() == "required", "Missing runtime account was treated as authenticated");
            var id = Guid.NewGuid().ToString();
            var pending = await Execute("authentication.start", id);
            var replay = await Execute("authentication.start", id);
            Require(pending.ToString() == replay.ToString() && File.ReadAllLines(trace).Count(l => l == "rpc:account/login/start") == 1, "Login replay started another authorization");
            Require(pending.GetProperty("authentication").GetProperty("userCode").GetString() == "", "Authorization challenge was retained in a mutation receipt");
            var live = await Execute("authentication");
            Require(live.GetProperty("authentication").GetProperty("verificationUrl").GetString() == "https://auth.openai.com/codex/device", "Official challenge lost");
            var other = await Execute("authentication", callerOverride: Guid.NewGuid());
            Require(other.GetProperty("error").GetString() == "RUNTIME_AUTH_BUSY" && !other.ToString().Contains("ABCD-1234"), "Another device read the challenge");
            var denied = await Execute("authentication", authorized: () => false);
            Require(denied.GetProperty("error").GetString() == "AUTHORITY_CHANGED", "Unauthorized device read auth state");
            await File.WriteAllTextAsync(Path.Combine(root,"authenticated"), "public fixture marker");
            var ready = await Execute("authentication");
            Require(ready.GetProperty("authentication").GetProperty("status").GetString() == "authenticated" &&
                !ready.ToString().Contains("must-never-leave-runtime") && !ready.ToString().Contains("never-export@") &&
                ready.GetProperty("authentication").GetProperty("userCode").GetString() == "", "Account tokens/email/challenge leaked");
            await Execute("authentication.cancel", Guid.NewGuid().ToString());
            Require(!File.ReadAllLines(trace).Any(l => l is "rpc:thread/start" or "rpc:turn/start" or "rpc:account/logout"), "Auth created a thread/turn or logged out another session");
            File.Delete(Path.Combine(root,"authenticated"));
            Environment.SetEnvironmentVariable(PortableCodexFixture.ModeVariable,"auth-invalid");
            var invalid = await Execute("authentication.start", Guid.NewGuid().ToString());
            Require(invalid.GetProperty("error").GetString() == "RUNTIME_AUTH_RESPONSE_INVALID" && !invalid.ToString().Contains("untrusted.invalid"), "Untrusted URL escaped auth boundary");
            await Execute("authentication.cancel", Guid.NewGuid().ToString());
            Console.WriteLine("PASS runtime authorization: account-only initialization, isolated metadata, device-code receipts, caller/authority fencing, redacted status, cancel, unsafe URL refusal; no model/token/history");

            async Task<JsonElement> Execute(string action, string operation = "", Guid? callerOverride = null, Func<bool>? authorized = null)
            {
                var request = JsonSerializer.Serialize(new { action, hostId = manager.HostId, agentId = "codex", operationId = operation });
                var result = await manager.ExecuteAsync(callerOverride ?? caller, request, CancellationToken.None, authorized);
                using var doc = JsonDocument.Parse(result); return doc.RootElement.Clone();
            }
        }
        finally { Environment.SetEnvironmentVariable(PortableCodexFixture.TraceVariable,before); Environment.SetEnvironmentVariable(PortableCodexFixture.ModeVariable,null); }
    }
    private static void Require(bool condition,string text) { if (!condition) throw new Exception(text); }
}
