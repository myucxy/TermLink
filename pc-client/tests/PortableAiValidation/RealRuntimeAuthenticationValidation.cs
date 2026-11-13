// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using TermLink.Agent;
using TermLink.Agent.AI;

internal static class RealRuntimeAuthenticationValidation
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (Directory.Exists(root) || File.Exists(root) || !Path.IsPathFullyQualified(root)) throw new ArgumentException("A new private evidence directory is required.");
        Directory.CreateDirectory(root, UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var installation = await new PortableCodexDiscovery(root).DiscoverAsync(CancellationToken.None);
        if (installation.Status != "available") throw new InvalidOperationException("CODEX_PROTOCOL_UNAVAILABLE");
        using var manager = new AiSessionManager(root, catalog: () => Task.FromResult(new[]{installation}));
        var caller = Guid.NewGuid(); var observations=new Dictionary<string,object>();
        try
        {
            var before=await Request("authentication"); observations["initial_status"]=before.GetProperty("authentication").GetProperty("status").GetString()!;
            var start=await Request("authentication.start",Guid.NewGuid().ToString());
            if(start.TryGetProperty("error",out var failure)){observations["start_error"]=failure.GetString()!;return;}
            var current=await Request("authentication");var auth=current.GetProperty("authentication");
            observations["pending"]=auth.GetProperty("status").GetString()=="pending";
            observations["official_url"]=auth.GetProperty("verificationUrl").GetString()=="https://auth.openai.com/codex/device";
            observations["challenge_not_in_receipt"]=start.GetProperty("authentication").GetProperty("userCode").GetString()=="";
            var cancel=await Request("authentication.cancel",Guid.NewGuid().ToString());
            observations["cancelled"]=cancel.GetProperty("authentication").GetProperty("status").GetString()=="cancelled";
            var after=await Request("authentication");observations["final_status"]=after.GetProperty("authentication").GetProperty("status").GetString()!;
        }
        finally
        {
            try{await Request("authentication.cancel",Guid.NewGuid().ToString());}catch{}
            observations["model_requests"]=0;observations["account_login_completed"]=false;observations["tokens_or_codes_logged"]=false;
            await File.WriteAllTextAsync(Path.Combine(root,"result.json"),JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(observations));
        }
        async Task<JsonElement> Request(string action,string id="")
        {
            var result=await manager.ExecuteAsync(caller,JsonSerializer.Serialize(new{action,agentId="codex",hostId=manager.HostId,operationId=id}),CancellationToken.None);
            using var json=JsonDocument.Parse(result);return json.RootElement.Clone();
        }
    }
}
