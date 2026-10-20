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
        await InstalledBuildReconciliation();
        await ReconciliationContract();
    }

    private static async Task ReconciliationContract()
    {
        var pc=Guid.NewGuid();var local=Guid.NewGuid();
        var keys=new List<string>();
        var retryApi=new ProductionAgentUpdateApi(pc,(_,_,_,_,key,_)=>{
            keys.Add(key);
            if(keys.Count==1)throw new HttpRequestException("fixture lost accepted confirmation response");
            return Task.FromResult(new HttpResponseMessage(keys.Count==3?HttpStatusCode.Conflict:HttpStatusCode.OK){Content=new StringContent("{\"agent_updates\":[]}")});
        });
        try{await retryApi.ReconcileInstalledAsync("0.3.11.141",141,Guid.Empty,default);}catch(HttpRequestException){}
        await retryApi.ReconcileInstalledAsync("0.3.11.141",141,Guid.Empty,default);
        try{await retryApi.ReconcileInstalledAsync("0.3.11.141",141,Guid.Empty,default);}catch(HttpRequestException){}
        await retryApi.ReconcileInstalledAsync("0.3.11.141",141,Guid.Empty,default);
        if(keys[0]!=keys[1] || keys[1]==keys[2] || keys[2]==keys[3])throw new Exception("Installed confirmation retries lost ambiguous identity or cached a definitive rejection.");
        foreach(var status in new[]{HttpStatusCode.NotFound,HttpStatusCode.MethodNotAllowed}) {
            var old=new ProductionAgentUpdateApi(pc,(_,_,_,_,_,_)=>Task.FromResult(new HttpResponseMessage(status)));
            if(await old.ReconcileInstalledAsync("0.3.11.141",141,local,default))throw new Exception("Old server claimed installed confirmation support.");
        }
        foreach(var invalid in new[]{"wrong-pc","above-target","wrong-status","malformed"}) {
            var api=new ProductionAgentUpdateApi(pc,(_,_,_,_,_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(invalid=="malformed"?"{}":JsonSerializer.Serialize(new{agent_updates=new[]{new{id=local,pc_id=invalid=="wrong-pc"?Guid.NewGuid():pc,status=invalid=="wrong-status"?"PROCESSING":"SUCCEEDED",target_build=invalid=="above-target"?142:141}}}))}));
            try{await api.ReconcileInstalledAsync("0.3.11.141",141,local,default);throw new Exception("Invalid installed confirmation was accepted: "+invalid);}
            catch(InvalidDataException){}
            catch(KeyNotFoundException) when(invalid=="malformed"){}
        }
        Console.WriteLine("PASS installed confirmation rejects wrong PC, newer unsatisfied target, nonterminal response and malformed contract; older Server keeps leased path.");
    }

    private static async Task InstalledBuildReconciliation()
    {
        foreach (var build in new long[] {139,140}) {
            var id=Guid.NewGuid();var pc=Guid.NewGuid();var dir=RemoteUpdateTransactionStore.CreateTransactionRoot(id);
            var install=Path.Combine(dir,"installed");Directory.CreateDirectory(install);
            File.WriteAllBytes(Path.Combine(install,"TermLink.Agent.exe"),new byte[]{1});
            File.WriteAllBytes(Path.Combine(install,"TermLink.Agent.dll"),new byte[]{1});
            var setup=Path.Combine(dir,"TermLink-Setup.exe");File.WriteAllBytes(setup,new byte[]{1});
            var journal=RemoteUpdateTransactionStore.JournalPath(dir);
            RemoteUpdateTransactionStore.Save(journal,new RemoteUpdateTransactionRecord{UpdateId=id,PcId=pc,State=RemoteUpdateTransactionState.SetupRunning,
                CurrentVersion="0.3.8.138",CurrentBuild=138,TargetVersion="0.3.9",TargetBuild=139,InstallRoot=install,
                SetupPath=setup,ArtifactSizeBytes=1,ArtifactSha256=new string('a',64),ExpectedSignerFingerprint=new string('b',40),PreviousProcessId=1,HealthDeadlineUtc=DateTimeOffset.UtcNow.AddMinutes(3)});
            var requests=0;var relay=false;
            var api=new ProductionAgentUpdateApi(pc,(method,path,body,proof,key,token)=>{
                if(!relay || !path.EndsWith("/reconcile"))throw new Exception("Installed confirmation ran without Relay or tried to install again.");
                using var json=JsonDocument.Parse(body);
                if(json.RootElement.GetProperty("observed_agent_build").GetInt64()!=build || json.RootElement.GetProperty("local_update_id").GetGuid()!=id)throw new Exception("Installed confirmation lost build or task identity.");
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{agent_updates=new[]{new{id,pc_id=pc,status="SUCCEEDED",target_build=139}}}))});
            });
            try {
                using var worker=new ProductionAgentUpdateWorker(api,pc,"0.3.10."+build,build,default,()=>new List<Guid>(),_=>throw new Exception("Manual install terminated a session"),()=>relay,()=>throw new Exception("Manual install entered install gate"),()=>{},"fixture");
                using(var cancel=new System.Threading.CancellationTokenSource(25)){
                    try{await worker.ReconcileInstalledBuildAsync(cancel.Token);throw new Exception("Disconnected Agent completed reconciliation");}
                    catch(OperationCanceledException){}
                }
                if(requests!=0 || RemoteUpdateTransactionStore.TryLoadReceipt(dir)!=null)throw new Exception("Offline installation was committed");
                relay=true;
                await worker.ReconcileInstalledBuildAsync(default);
                await worker.ReconcileInstalledBuildAsync(default);
                var receipt=RemoteUpdateTransactionStore.TryLoadReceipt(dir);
                if(requests!=1 || receipt?.ObservedAgentBuild!=build || RemoteUpdateTransactionStore.Load(journal).State!=RemoteUpdateTransactionState.Succeeded)throw new Exception("Installed target did not commit once with its actual observed build");
            }finally{Directory.Delete(dir,true);}
        }
        Console.WriteLine("PASS manual equal/newer installed target: waits for Relay, confirms exact task, writes receipt and never reinstalls or drains sessions.");
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
