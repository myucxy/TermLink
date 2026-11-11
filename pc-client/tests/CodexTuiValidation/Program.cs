using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Platform;
using TermLink.Platform.Unix;
using TermLink.Agent.Terminal;
using TermLink.TerminalCore;
using TermLink.Protocol.Realtime.V1;

PortableTerminalHost.LoadNative(args[0]);
var evidence=Path.GetFullPath(args[1]);Directory.CreateDirectory(evidence);
var home=Path.Combine(evidence,"isolated-home");Directory.CreateDirectory(home);
var codexHome=Path.Combine(home,".codex");Directory.CreateDirectory(codexHome);
await File.WriteAllTextAsync(Path.Combine(codexHome,"auth.json"),"{\"OPENAI_API_KEY\":\"termlink-nonsecret-offline-fixture\"}");
await File.WriteAllTextAsync(Path.Combine(codexHome,"config.toml"),"cli_auth_credentials_store = \"file\"\n");
var profile=Path.Combine(evidence,"isolation.sb");
string Quote(string s)=>"\""+s.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
var user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
await File.WriteAllTextAsync(profile,"(version 1)\n(allow default)\n(deny network*)\n"+
 string.Concat(new[]{Path.Combine(user,".codex"),Path.Combine(user,"Library/Keychains")}.Select(p=>"(deny file-read* file-write* (subpath "+Quote(p)+"))\n")));
await using var raw=new PosixTerminalSessionHost();
var tapping=new TappingPty(raw);
var commands=await new PosixTerminalCommandDiscovery().DiscoverAsync(CancellationToken.None);
await using var host=new PortableTerminalHost(tapping,commands);
var id=await host.StartAsync("sh",evidence,Guid.Empty,CancellationToken.None);
string Sh(string s)=>"'"+s.Replace("'","'\\''")+"'";
var command="/usr/bin/env -i HOME="+Sh(home)+" CODEX_HOME="+Sh(codexHome)+" CODEX_SQLITE_HOME="+Sh(Path.Combine(codexHome,"sqlite"))+" PATH=/usr/bin:/bin TERM=xterm-256color COLORTERM=truecolor /usr/bin/sandbox-exec -f "+Sh(profile)+" "+Sh("/Users/myucxy/.local/lib/codex/vendor/aarch64-apple-darwin/bin/codex")+"\n";
await raw.SendInputAsync(id,Encoding.UTF8.GetBytes(command),CancellationToken.None);
await Task.Delay(4500);
await raw.SendInputAsync(id,new byte[]{3},CancellationToken.None);
await Task.Delay(400);
var view=host.Sessions.Single();var read=await host.ReadAsync(id,0,CancellationToken.None);
var snapshot=await host.CaptureAsync(id,CancellationToken.None);
var retained=await host.ReadHistoryAsync(id,0,CancellationToken.None);
var frames=tapping.Outputs.ToArray();
using var mirror=NativeTerminalCoreV2.Create(100,30,1,10000);mirror.ApplyAdvance(1,0);
var deferred=0;var effects=0;ulong seq=1;
foreach(var bytes in frames){var emitted=mirror.ApplyOutputWithHostEffectsV2(++seq,bytes);effects+=emitted.Records.Count;if(!mirror.TryCaptureCheckpoint(out var cp,out _))deferred++;else cp.Dispose();}
using var current=NativeTerminalCoreV2.ImportSnapshot(snapshot.Bytes);
using var currentCheckpoint=current.CaptureCheckpoint();
using var replay=NativeTerminalCoreV2.Create(100,30,1,10000);
if(retained is null)throw new Exception("Codex TUI retained history unavailable");
foreach(var item in retained)
{
    if(item.Type==MessageType.OutputChunk)replay.ApplyOutputWithHostEffectsV2(item.Sequence,OutputChunkPayload.Parser.ParseFrom(item.Payload).Output.ToByteArray());
    else replay.ApplyAdvance(item.Sequence,replay.GetPresentationInfo().LogicalTimeMicros);
}
using var replayCheckpoint=replay.CaptureCheckpoint();
if(!currentCheckpoint.ExportRenderFrameV4().AsSpan().SequenceEqual(replayCheckpoint.ExportRenderFrameV4()))throw new Exception("Retained Codex TUI history disagrees with authoritative native frame");
await host.StopAsync(id,CancellationToken.None);
var byteStream=frames.SelectMany(x=>x).ToArray();
var rawText=Encoding.UTF8.GetString(byteStream);
var report=new{real_codex_tui_started=true,network_allowed=false,existing_credentials_read=false,model_prompt_sent=false,raw_chunks=frames.Length,codex_brand_seen=rawText.Contains("Codex",StringComparison.OrdinalIgnoreCase),sync_begin_seen=rawText.Contains("\u001b[?2026h"),native_history_available=retained is not null,deferred_chunks=deferred,host_effects=effects,source_sequence=view.Sequence,checkpoint_sequence=snapshot.Sequence,history_from_zero_available=read is not null,session_state=view.State,native_mirror_sequence=mirror.GetPresentationInfo().NextSourceSequence-1,history_native_frame_matches=true};
await File.WriteAllTextAsync(Path.Combine(evidence,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
if(!report.codex_brand_seen||!report.sync_begin_seen||report.session_state!="RUNNING")throw new Exception("Real Codex TUI was not exercised");
Console.WriteLine(JsonSerializer.Serialize(report));
file sealed class TappingPty(ITerminalSessionHost inner):ITerminalSessionHost{
 public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Outputs=new();
 public ValueTask<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest r,CancellationToken t)=>inner.LaunchAsync(r,t);
 public ValueTask ResizeAsync(Guid id,int c,int r,CancellationToken t)=>inner.ResizeAsync(id,c,r,t);
 public ValueTask SendInputAsync(Guid id,ReadOnlyMemory<byte> b,CancellationToken t)=>inner.SendInputAsync(id,b,t);
 public async ValueTask<int> ReadOutputAsync(Guid id,Memory<byte> b,CancellationToken t){var n=await inner.ReadOutputAsync(id,b,t);if(n>0)Outputs.Enqueue(b[..n].ToArray());return n;}
 public ValueTask SignalAsync(Guid id,TerminalSignal s,CancellationToken t)=>inner.SignalAsync(id,s,t);
 public ValueTask<int> WaitForExitAsync(Guid id,CancellationToken t)=>inner.WaitForExitAsync(id,t);
 public ValueTask StopAsync(Guid id,CancellationToken t)=>inner.StopAsync(id,t);
}
