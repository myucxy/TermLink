using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TermLink.Agent;
using TermLink.UpdateCore;

namespace TermLink.AgentUpdate.Validation;

internal static class UpdateProtectionValidation
{
    internal static async Task Run()
    {
        foreach (var idle in new[] { false, true }) {
            var phases = new List<string>(); var ended = false; var terminated = false; string error = "";
            var api = new ProductionAgentUpdateApi(Guid.NewGuid(), (method, path, body, proof, key, token) => {
                using var json = JsonDocument.Parse(body);
                if (path.EndsWith("/progress")) phases.Add(json.RootElement.GetProperty("phase").GetString()!);
                if (path.EndsWith("/complete")) error = json.RootElement.GetProperty("error_code").GetString()!;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });
            using var worker = new ProductionAgentUpdateWorker(api, Guid.NewGuid(), "0.3.8.138", 138, default,
                () => new List<Guid>(), _ => { terminated=true; return true; }, () => true,
                () => idle, () => ended=true, "isolated-update-test");
            await worker.ProcessAsync(new ClaimedAgentUpdate {Id=Guid.NewGuid(),LeaseToken="test-lease",CurrentBuild=138,TargetBuild=139,TargetVersion="0.3.9",ArtifactUrl="invalid"}, default);
            if (!idle && (error != "AI_TASKS_ACTIVE" || phases.Contains("DOWNLOADING") || ended)) throw new Exception("Busy AI did not stop upgrade before download.");
            if (idle && !ended) throw new Exception("Failed download did not release the AI update gate.");
            if (terminated || phases.Contains("DRAINING_SESSIONS")) throw new Exception("Failed precheck/download interrupted sessions.");
        }
        Console.WriteLine("PASS remote upgrade protection: active AI rejects before download; failed download releases gate without terminating sessions.");
        await CompletedReceipt();
    }

    private static async Task CompletedReceipt()
    {
        var id=Guid.NewGuid();var pc=Guid.NewGuid();var dir=RemoteUpdateTransactionStore.CreateTransactionRoot(id);
        var install=Path.Combine(dir,"installed");Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install,"TermLink.Agent.exe"),new byte[]{1});
        File.WriteAllBytes(Path.Combine(install,"TermLink.Agent.dll"),new byte[]{1});
        var setup=Path.Combine(dir,"TermLink-Setup.exe");File.WriteAllBytes(setup,new byte[]{1});
        var record=new RemoteUpdateTransactionRecord{UpdateId=id,PcId=pc,State=RemoteUpdateTransactionState.AwaitingNewAgent,
            CurrentVersion="0.3.8.138",CurrentBuild=138,TargetVersion="0.3.9",TargetBuild=139,InstallRoot=install,
            SetupPath=setup,ArtifactSizeBytes=1,ArtifactSha256=new string('a',64),ExpectedSignerFingerprint=new string('b',40),PreviousProcessId=1,HealthDeadlineUtc=DateTimeOffset.UtcNow.AddMinutes(3)};
        RemoteUpdateTransactionStore.Save(RemoteUpdateTransactionStore.JournalPath(dir),record);
        var completed=false;
        var api=new ProductionAgentUpdateApi(pc,(method,path,body,proof,key,token)=>{
            if(path.EndsWith("/claim"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{
                agent_update=new{id,pc_id=pc,current_version="0.3.8.138",current_build=138,target_version="0.3.9",target_build=139,artifact_url="https://example.invalid/setup.exe",artifact_filename="setup.exe",artifact_size_bytes=1,artifact_sha256=new string('a',64),phase="INSTALLING"},lease_token="test"}))});
            if(path.EndsWith("/complete"))completed=true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        try {
            using var worker=new ProductionAgentUpdateWorker(api,pc,"0.3.9.139",139,default,()=>new List<Guid>(),_=>true,()=>true,()=>true,()=>{},"fixture");
            await worker.ResumeInstalledTransactionAsync(default);
            var receipt=RemoteUpdateTransactionStore.TryLoadReceipt(dir);
            if(!completed||receipt?.Outcome!="SUCCEEDED"||receipt.ObservedAgentBuild!=139)throw new Exception("Server accepted completion but final local receipt is missing.");
            Console.WriteLine("PASS new Agent completion writes a validated final receipt after server acceptance.");
        } finally {Directory.Delete(dir,true);}
    }
}
