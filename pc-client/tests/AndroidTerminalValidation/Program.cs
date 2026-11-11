using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using TermLink.Agent.Connection;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

using TermLink.Agent.Terminal;
using TermLink.TerminalCore;
if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException();
PortableTerminalHost.LoadNative(args[0]);
await ValidateFragmentedHistoryAsync();
await ValidateFragmentedRelayAsync();
await ValidateTransportGapReplayAsync(args[1]+".gaps.json");
await using var pty=new PosixTerminalSessionHost();
var commands=await new PosixTerminalCommandDiscovery().DiscoverAsync(CancellationToken.None);
await using var server=new RelayFixture {AndroidMode=true};
PortableTerminalHost? host=null;
await using var service=await RelayConnectionService.CreateAsync(new MemorySecrets(),new PcAgentIdentity(Guid.NewGuid(),"Mac",PcPlatform.MacOS,PcArchitecture.Arm64,"test","0.3.16.146"),CancellationToken.None,
 authorization:new FixtureAuthorization(server),terminalHostFactory:_=>host=new PortableTerminalHost(pty,commands),terminalCommands:commands);
await service.ConfigureServerAsync(server.Origin,CancellationToken.None);await service.AuthorizeAsync("Android terminal fixture",CancellationToken.None);await service.SetRemoteAccessAsync(true,CancellationToken.None);
await Wait(()=>service.Snapshot.State==AgentConnectionState.Connected&&host!=null);
server.TerminalId=await service.StartTerminalAsync("sh",Path.GetTempPath(),CancellationToken.None);
await Wait(()=>server.AndroidMarker);
if(!server.MetadataVerified)throw new Exception("Android history/V2/catalog capabilities missing");
await Task.Delay(200);
await server.ReattachAsync(0);
await Wait(()=>server.DuplicateReplayVerified&&server.ReattachReady);
await server.RequestRangeAsync();
await Wait(()=>server.EventRangeVerified);
await server.RequestSnapshotAsync();
await Wait(()=>server.LargeSnapshotChunks>0);
server.LargeSnapshot = true;
var longCommand = "/usr/bin/awk 'BEGIN {for(i=0;i<6500;i++) printf \"%06d ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvwxyz\\n\",i}'; printf 'ANDROID_LARGE_%s\\n' 'DONE'\n";
var largeInput = new InputEventPayload { InputId=ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), LeaseId=ByteString.CopyFrom(Convert.FromHexString(server.LeaseId.ToString("N"))), LeaseVersion=1,InputSeq=2,Input=ByteString.CopyFromUtf8(longCommand) };
if(await host!.InputAsync(server.TerminalId,server.MobileId,largeInput,()=>Task.FromResult(true),CancellationToken.None) is not null)throw new Exception("large output input");
var readAfter=server.AndroidTail;var collected=new StringBuilder();
var deadline=DateTimeOffset.UtcNow.AddSeconds(30);
while(!collected.ToString().Contains("ANDROID_LARGE_DONE")) {
 var page=await host.ReadAsync(server.TerminalId,readAfter,CancellationToken.None)??throw new Exception("large output gap");
 foreach(var item in page){readAfter=item.Sequence;if(item.Type==MessageType.OutputChunk)collected.Append(Encoding.UTF8.GetString(OutputChunkPayload.Parser.ParseFrom(item.Payload).Output.Span));}
 if(DateTimeOffset.UtcNow>deadline)throw new TimeoutException("large output did not finish");await Task.Delay(30);
}
await server.RequestSnapshotAsync();
await Wait(()=>server.LargeSnapshotChunks>1);
await service.StopTerminalAsync(server.TerminalId,CancellationToken.None);
await service.SignOutAsync(CancellationToken.None);
server.WriteVectors(args[1]);
Console.WriteLine($"PASS Android fresh replay from zero, seed event, held/open windows, native render+durable ACK, Unicode output, same-socket identical ciphertext replay, recipient-sealed event-range and snapshot restore ({server.LargeSnapshotChunks} canonical 512-KiB chunks)");
async Task Wait(Func<bool> predicate){var end=DateTimeOffset.UtcNow.AddSeconds(20);while(!predicate()){if(server.Failure is {} e)throw e;if(DateTimeOffset.UtcNow>end)throw new TimeoutException("Android flow timeout "+service.Snapshot.ErrorCode);await Task.Delay(30);}}
static async Task ValidateFragmentedHistoryAsync()
{
    var commands = await new PosixTerminalCommandDiscovery().DiscoverAsync(CancellationToken.None);
    var fragments = new FragmentPty();
    await using var host = new PortableTerminalHost(fragments, commands);
    var id = await host.StartAsync("sh", Path.GetTempPath(), Guid.Empty, CancellationToken.None);
    async Task Emit(byte[] bytes)
    {
        var before=host.Sessions.Single().Sequence;
        await fragments.Output.Writer.WriteAsync(bytes);
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        while(host.Sessions.Single().Sequence==before){if(DateTimeOffset.UtcNow>deadline)throw new Exception("fragment timeout");await Task.Delay(10);}
    }
    await Emit([0xe4]); await Emit([0xb8,0xad]);
    var tail=host.Sessions.Single().Sequence;
    var page=await host.ReadAsync(id,0,CancellationToken.None);
    if(page is not null)throw new Exception("Legacy snapshot-only reader unexpectedly accepted a partial presentation");
    var history=await host.ReadHistoryAsync(id,0,CancellationToken.None)??throw new Exception("Complete split UTF8 was treated as lost history");
    using var mirror=NativeTerminalCoreV2.Create(100,30,1,10000);
    foreach(var item in history)ApplyEvent(mirror,item);
    using(var snapshot=mirror.CaptureCheckpoint())if(snapshot.Info.NextSourceSequence!=tail+1)throw new Exception("Fragment history native checkpoint");
    await Emit(Encoding.ASCII.GetBytes("\u001b[?2026hframe"));
    await Emit(Encoding.ASCII.GetBytes("\u001b[?2026l"));
    page=await host.ReadAsync(id,0,CancellationToken.None);
    history=await host.ReadHistoryAsync(id,tail,CancellationToken.None)??throw new Exception("Completed synchronized frame treated as lost history");
    foreach(var item in history)ApplyEvent(mirror,item);
    using(var snapshot=mirror.CaptureCheckpoint())if(snapshot.Info.NextSourceSequence!=host.Sessions.Single().Sequence+1)throw new Exception("Sync frame history checkpoint");
    var recovery=await host.RecoverAsync(id,Guid.NewGuid(),0,CancellationToken.None)??throw new Exception("Gap recovery absent");
    var gap=GapPayload.Parser.ParseFrom(recovery.Payload);
    if(gap.End!=recovery.Sequence-1||gap.Start!=1)throw new Exception("Android Gap end must precede envelope sequence");
    history=await host.ReadHistoryAsync(id,0,CancellationToken.None)??throw new Exception("Transport recovery poisoned retained source history");
    if(history.Any(e=>e.Type==MessageType.Gap))throw new Exception("History exposed a transport gap as missing source data");
    Console.WriteLine("PASS split UTF8/VT and synchronized-output fragments replay continuously to the authoritative native checkpoint; transport Gap does not poison retained history");
    static void ApplyEvent(NativeTerminal core,TermLink.Agent.Connection.RelayTerminalEvent item){
        if(item.Type==MessageType.OutputChunk)core.ApplyOutput(item.Sequence,OutputChunkPayload.Parser.ParseFrom(item.Payload).Output.ToByteArray());
        else core.ApplyAdvance(item.Sequence,core.GetPresentationInfo().LogicalTimeMicros);
    }
}
static async Task ValidateFragmentedRelayAsync()
{
    var commands=await new PosixTerminalCommandDiscovery().DiscoverAsync(CancellationToken.None);
    var fragments=new FragmentPty();PortableTerminalHost? host=null;
    await using var server=new RelayFixture {AndroidMode=true};
    await using var service=await RelayConnectionService.CreateAsync(new MemorySecrets(),new PcAgentIdentity(Guid.NewGuid(),"Mac",PcPlatform.MacOS,PcArchitecture.Arm64,"fragment-test","0.3.16.146"),CancellationToken.None,
        authorization:new FixtureAuthorization(server),terminalHostFactory:_=>host=new PortableTerminalHost(fragments,commands),terminalCommands:commands);
    await service.ConfigureServerAsync(server.Origin,CancellationToken.None);await service.AuthorizeAsync("VT fragment fixture",CancellationToken.None);await service.SetRemoteAccessAsync(true,CancellationToken.None);
    async Task Wait(Func<bool> predicate){var deadline=DateTimeOffset.UtcNow.AddSeconds(12);while(!predicate()){if(server.Failure is {} failure)throw failure;if(DateTimeOffset.UtcNow>deadline)throw new Exception("Fragment relay timeout");await Task.Delay(20);}}
    await Wait(()=>host is not null&&service.Snapshot.State==AgentConnectionState.Connected);
    server.TerminalId=await service.StartTerminalAsync("sh",Path.GetTempPath(),CancellationToken.None);
    await Wait(()=>server.AndroidReady);
    async Task Emit(byte[] bytes){var before=host!.Sessions.Single().Sequence;await fragments.Output.Writer.WriteAsync(bytes);await Wait(()=>host.Sessions.Single().Sequence>before);}
    var utf8=Encoding.UTF8.GetBytes("ANDROID_中文_OK\r\n");
    await Emit(utf8[..9]); await Emit(utf8[9..]);
    await Emit(Encoding.ASCII.GetBytes("\u001b[?2026h")); await Emit(Encoding.ASCII.GetBytes("drawn-frame\u001b[?2026l"));
    await Wait(()=>server.AndroidMarker&&server.AndroidTail==host!.Sessions.Single().Sequence);
    await server.RequestRangeAsync();await Wait(()=>server.EventRangeVerified);
    await service.StopTerminalAsync(server.TerminalId,CancellationToken.None);await service.SignOutAsync(CancellationToken.None);
    Console.WriteLine("PASS E2EE Android-shaped live and event-range replay of split UTF8 / DEC2026 reaches the same source cursor without false Gap or H116");
}
static async Task ValidateTransportGapReplayAsync(string vectorPath)
{
    var commands=await new PosixTerminalCommandDiscovery().DiscoverAsync(CancellationToken.None);
    var fragments=new FragmentPty();PortableTerminalHost? host=null;
    await using var server=new RelayFixture{AndroidMode=true,FirstSubscribeLegacy=true,AllowGapReplay=true};
    await using var service=await RelayConnectionService.CreateAsync(new MemorySecrets(),new PcAgentIdentity(Guid.NewGuid(),"Mac",PcPlatform.MacOS,PcArchitecture.Arm64,"gap-test","0.3.16.146"),CancellationToken.None,
        authorization:new FixtureAuthorization(server),terminalHostFactory:_=>host=new PortableTerminalHost(fragments,commands),terminalCommands:commands);
    async Task Wait(Func<bool> predicate){var deadline=DateTimeOffset.UtcNow.AddSeconds(12);while(!predicate()){if(server.Failure is {} e)throw e;if(DateTimeOffset.UtcNow>deadline)throw new Exception("Gap replay timeout");await Task.Delay(20);}}
    await service.ConfigureServerAsync(server.Origin,CancellationToken.None);await service.AuthorizeAsync("Gap replay fixture",CancellationToken.None);await service.SetRemoteAccessAsync(true,CancellationToken.None);
    await Wait(()=>host is not null&&service.Snapshot.State==AgentConnectionState.Connected);
    server.TerminalId=await service.StartTerminalAsync("sh",Path.GetTempPath(),CancellationToken.None);await Wait(()=>server.AndroidReady);
    async Task Emit(byte[] bytes){var before=host!.Sessions.Single().Sequence;await fragments.Output.Writer.WriteAsync(bytes);await Wait(()=>host.Sessions.Single().Sequence>before);}
    await Emit([0xe4]);await Emit([0xb8,0xad]);await Wait(()=>server.GapReplies==1);
    var connectionCount=server.Connections;
    await server.ReattachAsync(server.LastGapSequence-1);
    await Wait(()=>server.GapReplies==2);
    if(server.Connections!=connectionCount)throw new Exception("Gap projection restarted Relay");
    server.WriteVectors(vectorPath);
    await service.StopTerminalAsync(server.TerminalId,CancellationToken.None);await service.SignOutAsync(CancellationToken.None);
    Console.WriteLine("PASS same-socket re-subscribe before a retained transport Gap replays identical Gap ciphertext and preserves Relay recovery");
}
file sealed class FragmentPty:ITerminalSessionHost
{
    public readonly System.Threading.Channels.Channel<byte[]> Output=System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    public ValueTask<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest r,CancellationToken t)=>ValueTask.FromResult(new TerminalLaunchResult(Guid.NewGuid(),1,r.CommandId));
    public ValueTask ResizeAsync(Guid id,int c,int r,CancellationToken t)=>ValueTask.CompletedTask;
    public ValueTask SendInputAsync(Guid id,ReadOnlyMemory<byte>b,CancellationToken t)=>ValueTask.CompletedTask;
    public async ValueTask<int> ReadOutputAsync(Guid id,Memory<byte>b,CancellationToken t){var x=await Output.Reader.ReadAsync(t);x.CopyTo(b);return x.Length;}
    public ValueTask SignalAsync(Guid id,TerminalSignal s,CancellationToken t)=>ValueTask.CompletedTask;
    public ValueTask<int> WaitForExitAsync(Guid id,CancellationToken t)=>ValueTask.FromResult(0);
    public ValueTask StopAsync(Guid id,CancellationToken t)=>ValueTask.CompletedTask;
}
file sealed class MemorySecrets : ISecretStore
{
    public ConcurrentDictionary<string, byte[]> Values { get; } = new();
    public bool IsAvailable => true;
    public ValueTask StoreAsync(string name, ReadOnlyMemory<byte> secret, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Values[name] = secret.ToArray(); return ValueTask.CompletedTask; }
    public ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Values.TryGetValue(name, out var b) ? b.ToArray() : null); }
    public ValueTask DeleteAsync(string name, CancellationToken token)
    { Values.TryRemove(name, out _); return ValueTask.CompletedTask; }
}

file sealed class FixtureAuthorization(RelayFixture server) : IRelayAuthorization
{
    public string Subject { get; set; } = "A";
    public int ExpiresIn { get; set; } = 3600;
    public Task<RelayTokens> AuthorizeAsync(RelayOidcConfiguration config, HttpClient http, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new RelayTokens(server.TokenFor(Subject), "refresh-" + Subject, ExpiresIn));
    }
}

file sealed class RelayFixture : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task loop;
    private readonly ConcurrentBag<Task> clients = [];
    private readonly ConcurrentDictionary<Guid, Device> devices = new();
    private readonly ConcurrentDictionary<string, bool> nonces = new();
    private readonly ConcurrentDictionary<string, string> tokens = new();
    private int connections, pings, upserts, invalidProofs, conflicts, refreshes;
    private Guid metadataPCId;
    public bool AndroidMode;
    public bool FirstSubscribeLegacy,AllowGapReplay;
    public int GapReplies; public ulong LastGapSequence; private byte[]? retainedGapWire;
    public bool AndroidReady, AndroidMarker, EventRangeVerified, ReattachReady, DuplicateReplayVerified;
    public bool MetadataVerified;
    private ulong androidFlow, androidTail;
    private int androidAttach;
    private readonly Dictionary<ulong, byte[]> androidWires = new();
    private readonly MemoryStream androidOutput = new();
    private readonly MemoryStream androidHistory = new();
    public bool LargeSnapshot;
    public int LargeSnapshotChunks;
    private bool sentMarker;

    public Guid TerminalId;
    public Guid LeaseId = Guid.NewGuid();
    public int Snapshots, InputsAccepted, InputsRejected, Outputs, Resizes;
    public bool MarkerSeen, AllowLease = true;
    public readonly StringBuilder OutputText = new();
    private WebSocket? liveSocket;
    private Guid livePc;
    private Device? livePeer;
    public async Task RequestRangeAsync() {
        await SendFlowAsync(liveSocket!,livePc,livePeer!,true);
        var request = new HostHistoryRequestPayload { RequestId=Id(Guid.NewGuid()),Kind=HostHistoryRequestKindV1.EventRange,AfterSeq=0,ThroughSeq=androidTail,MaxEvents=512 };
        request.SubscriberCapabilities.Add("terminal_state_2_21_v2");androidHistory.SetLength(0);
        await SendTerminalAsync(liveSocket!,livePc,livePeer!,MessageType.HostHistoryRequest,request);
    }
    public async Task RequestSnapshotAsync() {
        await SendFlowAsync(liveSocket!,livePc,livePeer!,true);
        var request = new HostHistoryRequestPayload { RequestId=Id(Guid.NewGuid()),Kind=HostHistoryRequestKindV1.LatestSnapshot };
        request.SubscriberCapabilities.Add("terminal_state_2_21_v2");androidHistory.SetLength(0);
        await SendTerminalAsync(liveSocket!,livePc,livePeer!,MessageType.HostHistoryRequest,request);
    }
    public async Task ReattachAsync(ulong cursor) {
        var request=new SubscribePayload {RequestId=Id(Guid.NewGuid()),ResumeFromSeq=cursor,SubscriberExchangePublicKey=ByteString.CopyFrom(mobile.Exchange.PublicKey)};
        request.SubscriberCapabilities.AddRange(new[]{"terminal_state_2_22_v2","terminal_state_2_22_v1","terminal_state_2_21_v2","host_history_v1","terminal_broker_captures_v1"});
        await SendTerminalAsync(liveSocket!,livePc,livePeer!,MessageType.Subscribe,request);
    }
    public ulong AndroidTail => androidTail;
    private int attachRound;
    private readonly List<object> vectors = new();
    public void WriteVectors(string path) => File.WriteAllText(path, JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true }));
    private ulong requestSeq;
    private InputEventPayload? originalInput;
    private readonly MemoryStream historyBytes = new();
    private NativeTerminal? replica;
    public Exception? Failure;
    public bool EnableAi { get; init; }
    public bool RevokeMobile { get; set; }
    public bool AiMetadata { get; private set; }
    public Guid MobileId { get; } = Guid.NewGuid();
    private readonly DeviceIdentity mobile = DeviceIdentity.Generate();
    public TaskCompletionSource<string> AiReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Origin { get; }
    public string Issuer => Origin + "/realms/desktop";
    public int Connections => Volatile.Read(ref connections);
    public int Pings => Volatile.Read(ref pings);
    public int Upserts => Volatile.Read(ref upserts);
    public int InvalidProofs => Volatile.Read(ref invalidProofs);
    public int Conflicts => Volatile.Read(ref conflicts);
    public int Refreshes => Volatile.Read(ref refreshes);
    public bool DropNext { get; set; }
    public bool WrongSelfKey { get; set; }
    public bool DenySelf { get; set; }
    public bool WrongDiscoveryEndpoint { get; set; }
    public bool InvalidWelcome { get; set; }
    public bool EmptyProductCapabilities { get; private set; }
    public Guid LastDeviceId { get; private set; }
    public string RefreshSubject { get; set; } = "A";
    private sealed record Device(string Signing, string Exchange, string Name, string Subject);

    public RelayFixture()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Origin = "http://127.0.0.1:" + port;
        listener.Prefixes.Add(Origin + "/");
        listener.Start();
        loop = Task.Run(async () =>
        {
            try { while (!shutdown.IsCancellationRequested) clients.Add(Handle(await listener.GetContextAsync().WaitAsync(shutdown.Token))); }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        });
    }

    public string TokenFor(string subject) => tokens.GetOrAdd(subject, key => "eyJhbGciOiJub25lIn0." + RelaySecurity.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
    { iss = Issuer, azp = "desktop", sub = subject, preferred_username = "user-" + subject, name = "User " + subject,
        exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() })) + ".fixture-signature");

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/v1/client-config") { await Json(context, new { oidc_issuer = Issuer, windows_oidc_client_id = "desktop", windows_redirect_uri = "http://127.0.0.1/oauth2/callback" }); return; }
            if (path == "/realms/desktop/.well-known/openid-configuration")
            { await Json(context, new { issuer = Issuer, authorization_endpoint = Origin + "/authorize", token_endpoint = WrongDiscoveryEndpoint ? "https://foreign.invalid/token" : Origin + "/token" }); return; }
            if (path == "/token")
            { Interlocked.Increment(ref refreshes); await Json(context, new { access_token = TokenFor(RefreshSubject), refresh_token = "rotated-refresh-" + RefreshSubject, expires_in = 3600 }); return; }
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body, shutdown.Token);
            var bytes = body.ToArray();
            var bearer = context.Request.Headers["Authorization"] ?? "";
            var subject = bearer == "Bearer " + TokenFor("A") ? "A" : "B";
            if (path == "/v1/devices/register")
            {
                using var json = JsonDocument.Parse(bytes);
                var value = json.RootElement;
                var id = value.GetProperty("id").GetGuid();
                var signing = value.GetProperty("signing_public_key").GetString()!;
                var exchange = value.GetProperty("exchange_public_key").GetString()!;
                var canonical = string.Join("\n", "TERMLINK-DEVICE-REGISTER-V1", id.ToString("D"), "PC_AGENT", value.GetProperty("name").GetString(),
                    signing, exchange, value.GetProperty("key_id").GetString(), value.GetProperty("timestamp_ms").GetInt64().ToString(), value.GetProperty("nonce").GetString());
                if (value.GetProperty("type").GetString() != "PC_AGENT" || !Verify(signing, canonical, value.GetProperty("signature").GetString()!))
                { Interlocked.Increment(ref invalidProofs); context.Response.StatusCode = 401; context.Response.Close(); return; }
                var record = new Device(signing, exchange, value.GetProperty("name").GetString()!, subject);
                var added = devices.TryAdd(id, record);
                LastDeviceId = id;
                if (!added) Interlocked.Increment(ref conflicts);
                await Json(context, new { }, added ? 201 : 409);
                return;
            }
            if (!Guid.TryParse(context.Request.Headers["X-TermLink-Device"], out var deviceId) || !devices.TryGetValue(deviceId, out var device) ||
                !ValidateProof(context.Request, bytes, device))
            { Interlocked.Increment(ref invalidProofs); context.Response.StatusCode = 401; context.Response.Close(); return; }
            if (path == "/v1/devices/self")
            { await Json(context, new { device = new { id = deviceId, type = "PC_AGENT", trust_state = DenySelf ? "REVOKED" : "TRUSTED", signing_public_key = WrongSelfKey ? "wrong" : Convert.ToBase64String(Decode(device.Signing)), exchange_public_key = Convert.ToBase64String(Decode(device.Exchange)) } }); return; }
            if (path == "/v1/devices")
            { await Json(context, new { items = new[] { new { id = Guid.NewGuid(), name = "iPhone", type = "ANDROID", trust_state = "TRUSTED", online = true } } }); return; }
            if (path.EndsWith("/grants", StringComparison.Ordinal) && path.StartsWith("/v1/pcs/", StringComparison.Ordinal)) {
                var items = RevokeMobile ? Array.Empty<object>() : new object[] { new { device_id = MobileId, device_type = "IOS", permission = "MANAGE",
                    exchange_public_key = Convert.ToBase64String(mobile.Exchange.PublicKey), signing_public_key = Convert.ToBase64String(mobile.SigningPublicKey) } };
                await Json(context, new { key_epoch = 1, items }); return;
            }
            if (path.StartsWith("/v1/sessions/", StringComparison.Ordinal)) {
                if (path.EndsWith("/lease", StringComparison.Ordinal)) {
                    if (!AllowLease) { await Json(context, new {}, 403); return; }
                    await Json(context, new { session_id = TerminalId, authorized_sender_device_id = MobileId, lease_id = LeaseId, version = 1, expires_at = DateTimeOffset.UtcNow.AddMinutes(1) }); return;
                }
                await Json(context, new {}); return;
            }
            if (path.EndsWith("/file-operations/claim", StringComparison.Ordinal)) { context.Response.StatusCode = 204; context.Response.Close(); return; }
            if (path.StartsWith("/v1/pcs/", StringComparison.Ordinal))
            {
                metadataPCId = Guid.Parse(path.Split('/')[3]);
                using var metadata = JsonDocument.Parse(bytes);
                var value = metadata.RootElement;
                var pcCapabilities = value.GetProperty("capabilities").EnumerateArray().Select(v => v.GetString()).ToHashSet();
                MetadataVerified = new[] { "host_history_v1", "host_session_catalog_v1", "terminal_snapshot_v2", "realtime_terminal_v1" }.All(pcCapabilities.Contains);
                AiMetadata = value.GetProperty("capabilities").EnumerateArray().Any(v => v.GetString() == "ai_agent_v1");
                EmptyProductCapabilities = value.GetProperty("platform").GetString() == "macos" &&
                    value.GetProperty("architecture").GetString() == "arm64" && value.GetProperty("capabilities").GetArrayLength() == 0 &&
                    value.GetProperty("supported_shells").GetArrayLength() == 0 && !value.GetProperty("remote_agent_update_capable").GetBoolean();
                Interlocked.Increment(ref upserts);
                await Json(context, new { }); return;
            }
            if (path == "/v1/realtime")
            {
                var accepted = await context.AcceptWebSocketAsync("termlink.realtime.v1");
                using var socket = accepted.WebSocket;
                using var hello = await Receive(socket);
                if (hello.RootElement.GetProperty("role").GetString() != "pc") throw new InvalidOperationException("Wrong role.");
                var capabilities = hello.RootElement.GetProperty("capabilities").EnumerateArray().Select(v => v.GetString()).ToArray();
                Interlocked.Increment(ref connections);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                { type = "welcome", protocol_version = InvalidWelcome ? 9 : 1, connection_id = Guid.NewGuid().ToString("D"), heartbeat_seconds = 1, capabilities }), WebSocketMessageType.Text, true, shutdown.Token);
                if (DropNext) { DropNext = false; await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "fixture reconnect", shutdown.Token); return; }
                var sentAi = false; var subscribed = false;
                while (!shutdown.IsCancellationRequested)
                {
                    var data = new byte[1024 * 1024];
                    var used = 0;
                    ValueWebSocketReceiveResult received;
                    do { received = await socket.ReceiveAsync(data.AsMemory(used), shutdown.Token); used += received.Count; } while (!received.EndOfMessage);
                    if (received.MessageType == WebSocketMessageType.Binary) {
                        await TerminalReplyAsync(socket, deviceId, device, Envelope.Parser.ParseFrom(data, 0, used)); continue;
                    }
                    if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType == WebSocketMessageType.Binary && EnableAi) {
                        var reply = Envelope.Parser.ParseFrom(data, 0, received.Count);
                        var pc = new Guid(Convert.ToHexString(reply.SessionId.Span));
                        var pairwise = SessionCrypto.DerivePairwiseKey(mobile.Exchange, Decode(device.Exchange), deviceId, MobileId, 1);
                        var key = SessionCrypto.DeriveSessionKey(pairwise, pc, CryptoDirection.PcToAndroid, reply.KeyId.ToByteArray());
                        var decrypted = SessionCrypto.Decrypt(key, reply.Nonce.ToByteArray(), SessionCrypto.AssociatedData(reply), reply.Ciphertext.ToByteArray());
                        try { AiReply.TrySetResult(AiAgentResponsePayload.Parser.ParseFrom(decrypted).Json); }
                        finally { CryptographicOperations.ZeroMemory(decrypted); CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pairwise); }
                        continue;
                    }
                    using var ping = JsonDocument.Parse(data.AsMemory(0, used));
                    Interlocked.Increment(ref pings);
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "pong", probe_id = ping.RootElement.GetProperty("probe_id").GetString() }), WebSocketMessageType.Text, true, shutdown.Token);
                    if (TerminalId != Guid.Empty && !subscribed) {
                        subscribed = true; attachRound++;
                        var subscribe = new SubscribePayload { RequestId = Id(Guid.NewGuid()), SubscriberExchangePublicKey = ByteString.CopyFrom(mobile.Exchange.PublicKey) };
                        subscribe.SubscriberCapabilities.Add("terminal_state_2_21_v2");
                        if(!FirstSubscribeLegacy)subscribe.SubscriberCapabilities.Add("host_history_v1");
                        await SendTerminalAsync(socket, deviceId, device, MessageType.Subscribe, subscribe);
                    }
                    if (EnableAi && !sentAi) {
                        sentAi = true;
                        var stored = devices[deviceId];
                        var pcPath = metadataPCId;
                        var pairwise = SessionCrypto.DerivePairwiseKey(mobile.Exchange, Decode(stored.Exchange), deviceId, MobileId, 1);
                        var keyId = Encoding.UTF8.GetBytes("epoch-00000001");
                        var key = SessionCrypto.DeriveSessionKey(pairwise, pcPath, CryptoDirection.AndroidToPc, keyId);
                        var request = new Envelope { ProtocolVersion = 1, MessageType = MessageType.AiAgentRequest, Direction = Direction.AndroidToPc,
                            SessionId = ByteString.CopyFrom(Convert.FromHexString(pcPath.ToString("N"))), SenderDeviceId = ByteString.CopyFrom(Convert.FromHexString(MobileId.ToString("N"))),
                            RecipientDeviceId = ByteString.CopyFrom(Convert.FromHexString(deviceId.ToString("N"))), Seq = 1, KeyId = ByteString.CopyFrom(keyId),
                            Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)), SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                        var payload = new AiAgentRequestPayload { RequestId = ByteString.CopyFrom(Convert.FromHexString(Guid.NewGuid().ToString("N"))), Json = "{\"action\":\"catalog\"}" }.ToByteArray();
                        request.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(key, request.Nonce.ToByteArray(), SessionCrypto.AssociatedData(request), payload));
                        var corrupt = request.Clone(); var bad = corrupt.Ciphertext.ToByteArray(); bad[^1] ^= 1; corrupt.Ciphertext = ByteString.CopyFrom(bad);
                        await socket.SendAsync(corrupt.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
                        var wrongRecipient = request.Clone(); wrongRecipient.RecipientDeviceId = ByteString.CopyFrom(new byte[16]);
                        await socket.SendAsync(wrongRecipient.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
                        await socket.SendAsync(request.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
                        CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pairwise);
                    }
                }
                return;
            }
            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException)
        { context.Response.Abort(); }
    }

    private static ByteString Id(Guid id) => ByteString.CopyFrom(Convert.FromHexString(id.ToString("N")));
    private async Task SendTerminalAsync(WebSocket socket, Guid pc, Device peer, MessageType type, IMessage payload)
    {
        var pair = SessionCrypto.DerivePairwiseKey(mobile.Exchange, Decode(peer.Exchange), pc, MobileId, 1);
        var keyId = Encoding.UTF8.GetBytes("epoch-00000001");
        var key = SessionCrypto.DeriveSessionKey(pair, TerminalId, CryptoDirection.AndroidToPc, keyId);
        var envelope = new Envelope { ProtocolVersion = 1, SessionId = Id(TerminalId), SenderDeviceId = Id(MobileId), RecipientDeviceId = Id(pc),
            Direction = Direction.AndroidToPc, MessageType = type, Seq = ++requestSeq, KeyId = ByteString.CopyFrom(keyId), Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)) };
        envelope.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(key, envelope.Nonce.ToByteArray(), SessionCrypto.AssociatedData(envelope), payload.ToByteArray()));
        if (type == MessageType.Subscribe) {
            var corrupt = envelope.Clone(); var tampered = corrupt.Ciphertext.ToByteArray(); tampered[^1] ^= 1; corrupt.Ciphertext = ByteString.CopyFrom(tampered);
            await socket.SendAsync(corrupt.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
            var wrong = envelope.Clone(); wrong.RecipientDeviceId = Id(Guid.NewGuid());
            await socket.SendAsync(wrong.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
        }
        await socket.SendAsync(envelope.ToByteArray(), WebSocketMessageType.Binary, true, shutdown.Token);
        CryptographicOperations.ZeroMemory(pair); CryptographicOperations.ZeroMemory(key);
    }
    private async Task TerminalReplyAsync(WebSocket socket, Guid pc, Device peer, Envelope envelope)
    {
        try {
            if (AndroidMode) { await AndroidReplyAsync(socket, pc, peer, envelope); return; }
            if (attachRound == 1) vectors.Add(new { type = (int)envelope.MessageType, seq = envelope.Seq, ack = envelope.Ack, sha256 = Convert.ToHexString(SHA256.HashData(envelope.ToByteArray())).ToLowerInvariant() });
            var pair = SessionCrypto.DerivePairwiseKey(mobile.Exchange, Decode(peer.Exchange), pc, MobileId, 1);
            var key = SessionCrypto.DeriveSessionKey(pair, TerminalId, CryptoDirection.PcToAndroid, envelope.KeyId.ToByteArray());
            var plain = SessionCrypto.Decrypt(key, envelope.Nonce.ToByteArray(), SessionCrypto.AssociatedData(envelope), envelope.Ciphertext.ToByteArray());
            switch (envelope.MessageType) {
                case MessageType.TerminalAttachResult:
                    if (TerminalAttachResultPayload.Parser.ParseFrom(plain).ErrorCode != "") throw new Exception("attach rejected");
                    var request = new HostHistoryRequestPayload { RequestId = Id(Guid.NewGuid()), Kind = (HostHistoryRequestKindV1)1 };
                    request.SubscriberCapabilities.Add("terminal_state_2_21_v2"); historyBytes.SetLength(0);
                    await SendTerminalAsync(socket, pc, peer, MessageType.HostHistoryRequest, request); break;
                case MessageType.HostHistoryResponse:
                    var h = HostHistoryResponsePayload.Parser.ParseFrom(plain);
                    if ((int)h.Status != 1) throw new Exception("snapshot rejected");
                    historyBytes.Write(h.ObjectChunk.Span);
                    await SendTerminalAsync(socket, pc, peer, MessageType.HostHistoryControl, new HostHistoryControlPayload { RequestId = h.RequestId, ObjectId = h.ObjectId, Action = (HostHistoryControlActionV1)1, ChunkIndex = h.ChunkIndex });
                    if (h.ChunkIndex + 1 != h.ChunkCount) break;
                    var sealedBytes = historyBytes.ToArray();
                    if (!SHA256.HashData(sealedBytes).AsSpan().SequenceEqual(h.ObjectSha256.Span)) throw new Exception("snapshot digest");
                    var raw = SnapshotCrypto.OpenTerminalCoreSnapshot(pair, sealedBytes, TerminalId, pc, MobileId, 1, h.KeyId, h.SnapshotSeq, h.Columns, h.Rows, 1, out var epoch);
                    replica?.Dispose(); replica = NativeTerminalCoreV2.ImportSnapshot(raw);
                    if (replica.GetPresentationInfo().NextSourceSequence != h.SnapshotSeq + 1 || epoch != 0) throw new Exception("snapshot native sequence");
                    Snapshots++;
                    await SendTerminalAsync(socket, pc, peer, MessageType.OutputAck, new OutputAckPayload { HighestContiguousSeq = h.SnapshotSeq });
                    if (attachRound == 1) {
                        AllowLease = false;
                        originalInput = new InputEventPayload { InputId = Id(Guid.NewGuid()), LeaseId = Id(LeaseId), LeaseVersion = 1, InputSeq = 1,
                            Input = ByteString.CopyFromUtf8("printf 'CLI_%s\\n' '中文_" + "OK'\n") };
                        await SendTerminalAsync(socket, pc, peer, MessageType.InputEvent, originalInput);
                    }
                    break;
                case MessageType.InputRejected:
                    replica!.ApplyAdvance(envelope.Seq, 0);
                    await SendTerminalAsync(socket, pc, peer, MessageType.OutputAck, new OutputAckPayload { HighestContiguousSeq = envelope.Seq });
                    InputsRejected++; AllowLease = true;
                    await SendTerminalAsync(socket, pc, peer, MessageType.InputEvent, originalInput!); break;
                case MessageType.InputAccepted:
                    replica!.ApplyAdvance(envelope.Seq, 0);
                    await SendTerminalAsync(socket, pc, peer, MessageType.OutputAck, new OutputAckPayload { HighestContiguousSeq = envelope.Seq });
                    InputsAccepted++;
                    if (InputsAccepted == 1) await SendTerminalAsync(socket, pc, peer, MessageType.InputEvent, originalInput!);
                    if (InputsAccepted == 2) await SendTerminalAsync(socket, pc, peer, MessageType.ResizeRequest, new ResizePayload {
                        Columns = 92, Rows = 28, LeaseId = Id(LeaseId), LeaseVersion = 1, RequestId = Id(Guid.NewGuid()), ExpiresAtMs = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds() });
                    break;
                case MessageType.OutputChunk:
                    var output = OutputChunkPayload.Parser.ParseFrom(plain);
                    if (replica is null || replica.GetPresentationInfo().NextSourceSequence != envelope.Seq) throw new Exception("output sequence");
                    replica.ApplyOutput(envelope.Seq, output.Output.ToByteArray());
                    OutputText.Append(Encoding.UTF8.GetString(output.Output.Span));
                    if (OutputText.ToString().Contains("CLI_中文_OK")) MarkerSeen = true;
                    Outputs++;
                    await SendTerminalAsync(socket, pc, peer, MessageType.OutputAck, new OutputAckPayload { HighestContiguousSeq = envelope.Seq }); break;
                case MessageType.ResizeApplied:
                    var resize = ResizePayload.Parser.ParseFrom(plain); replica!.ApplyResize(envelope.Seq, resize.Columns, resize.Rows);
                    Resizes++; await SendTerminalAsync(socket, pc, peer, MessageType.OutputAck, new OutputAckPayload { HighestContiguousSeq = envelope.Seq }); break;
                case MessageType.SessionEnded:
                case MessageType.SessionInterrupted:
                    replica!.ApplyAdvance(envelope.Seq, 0); break;
            }
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pair);
        } catch (Exception e) { Failure = e; throw; }
    }
    private async Task SendFlowAsync(WebSocket socket,Guid pc,Device peer,bool paused) {
        await SendTerminalAsync(socket,pc,peer,MessageType.FlowControl,new FlowControlPayload {HighestContiguousSeq=androidTail,ControlEpoch=++androidFlow,
            Paused=paused,MaxUnackedEvents=paused?0u:32u,MaxUnackedWireBytes=paused?0u:3u*1024*1024});
    }
    private async Task AndroidReplyAsync(WebSocket socket,Guid pc,Device peer,Envelope envelope) {
        vectors.Add(new { type=(int)envelope.MessageType,seq=envelope.Seq,ack=envelope.Ack,sha256=Convert.ToHexString(SHA256.HashData(envelope.ToByteArray())).ToLowerInvariant() });
        liveSocket=socket;livePc=pc;livePeer=peer;
        var pair=SessionCrypto.DerivePairwiseKey(mobile.Exchange,Decode(peer.Exchange),pc,MobileId,1);
        var key=SessionCrypto.DeriveSessionKey(pair,TerminalId,CryptoDirection.PcToAndroid,envelope.KeyId.ToByteArray());
        var plain=SessionCrypto.Decrypt(key,envelope.Nonce.ToByteArray(),SessionCrypto.AssociatedData(envelope),envelope.Ciphertext.ToByteArray());
        try {
            switch(envelope.MessageType) {
                case MessageType.TerminalAttachResult:
                    if(TerminalAttachResultPayload.Parser.ParseFrom(plain).ErrorCode!="")throw new Exception("Android attach rejected");
                    androidAttach++;
                    if(androidAttach==1) replica=NativeTerminalCoreV2.Create(100,30);
                    await SendFlowAsync(socket,pc,peer,true);
                    await SendFlowAsync(socket,pc,peer,false);
                    if(androidAttach>1)ReattachReady=true;
                    break;
                case MessageType.HostHistoryResponse:
                    var h=HostHistoryResponsePayload.Parser.ParseFrom(plain);
                    if(h.Status!=HostHistoryResponseStatusV1.Ok)throw new Exception("Android history rejected "+h.ErrorCode);
                    if(h.ChunkCount!=(h.ObjectBytes+512*1024-1)/(512*1024))throw new Exception("Android rejects chunk count");
                    var offset=h.ChunkIndex*512*1024ul;
                    if((ulong)h.ObjectChunk.Length!=Math.Min(512*1024ul,h.ObjectBytes-offset))throw new Exception("Android rejects chunk size");
                    androidHistory.Write(h.ObjectChunk.Span);
                    await SendTerminalAsync(socket,pc,peer,MessageType.HostHistoryControl,new HostHistoryControlPayload {RequestId=h.RequestId,ObjectId=h.ObjectId,Action=HostHistoryControlActionV1.ChunkAck,ChunkIndex=h.ChunkIndex});
                    if(h.ChunkIndex+1!=h.ChunkCount)break;
                    var sealedBytes=androidHistory.ToArray();
                    if(!SHA256.HashData(sealedBytes).AsSpan().SequenceEqual(h.ObjectSha256.Span))throw new Exception("Android history digest");
                    if(h.Kind==HostHistoryObjectKindV1.EventChunk) {
                        var events=HistoryCrypto.Open(pair,sealedBytes,TerminalId,pc,MobileId,1,h.KeyId,h.StartSeq,h.EndSeq);
                        using var replay=NativeTerminalCoreV2.Create(100,30);
                        foreach(var item in events.Events)Apply(replay,item.MessageType,item.Seq,item.Payload.ToByteArray());
                        if(replay.GetPresentationInfo().NextSourceSequence!=h.EndSeq+1)throw new Exception("event range did not render");
                        if(h.StartSeq!=1||h.EndSeq!=androidTail||h.HasMoreEvents)throw new Exception("Android event range bounds");
                        EventRangeVerified=true;
                    } else {
                        var raw=SnapshotCrypto.OpenTerminalCoreSnapshot(pair,sealedBytes,TerminalId,pc,MobileId,1,h.KeyId,h.SnapshotSeq,h.Columns,h.Rows,h.InitialTranscriptBeforeLineId,out _);
                        using var imported=NativeTerminalCoreV2.ImportSnapshot(raw);
                        if(imported.GetPresentationInfo().NextSourceSequence!=h.SnapshotSeq+1)throw new Exception("Android snapshot sequence");
                        if(LargeSnapshot&&h.ChunkCount<2)throw new Exception("large snapshot fixture too small");
                        LargeSnapshotChunks=(int)h.ChunkCount;
                    }
                    break;
                case MessageType.OutputChunk:
                case MessageType.SessionStarted:
                case MessageType.SessionUpsert:
                case MessageType.InputAccepted:
                case MessageType.InputRejected:
                case MessageType.ResizeApplied:
                case MessageType.SessionEnded:
                case MessageType.SessionInterrupted:
                    if(envelope.Seq<=androidTail) {
                        if(!androidWires.TryGetValue(envelope.Seq,out var previous)||!previous.AsSpan().SequenceEqual(envelope.ToByteArray()))throw new Exception("replay changed ciphertext");
                        DuplicateReplayVerified=true;
                        break;
                    }
                    if(envelope.Seq!=androidTail+1)throw new Exception("Android strict contiguous source sequence");
                    Apply(replica!,envelope.MessageType,envelope.Seq,plain);
                    androidTail=envelope.Seq;androidWires[androidTail]=envelope.ToByteArray();
                    if(envelope.MessageType==MessageType.OutputChunk) {
                        androidOutput.Write(OutputChunkPayload.Parser.ParseFrom(plain).Output.Span);
                        AndroidMarker=Encoding.UTF8.GetString(androidOutput.ToArray()).Contains("ANDROID_中文_OK");
                    }
                    await SendTerminalAsync(socket,pc,peer,MessageType.OutputAck,new OutputAckPayload {HighestContiguousSeq=androidTail});
                    AndroidReady=true;
                    if(!sentMarker) {
                        sentMarker=true;
                        await SendTerminalAsync(socket,pc,peer,MessageType.InputEvent,new InputEventPayload {InputId=Id(Guid.NewGuid()),LeaseId=Id(LeaseId),LeaseVersion=1,InputSeq=1,
                            Input=ByteString.CopyFromUtf8("printf 'ANDROID_%s\\n' '中文_"+"OK'\n")});
                    }
                    break;
                case MessageType.Gap:
                    if(!AllowGapReplay)throw new Exception("Unexpected Android bootstrap gap");
                    var gap=GapPayload.Parser.ParseFrom(plain);
                    if(gap.End!=envelope.Seq-1)throw new Exception("Noncanonical Android gap end");
                    if(retainedGapWire is not null&&!retainedGapWire.AsSpan().SequenceEqual(envelope.ToByteArray()))throw new Exception("Retained gap wire changed");
                    retainedGapWire=envelope.ToByteArray();LastGapSequence=envelope.Seq;GapReplies++;break;
            }
        }catch(Exception e){Failure=e;throw;}
        finally{CryptographicOperations.ZeroMemory(pair);CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    private static void Apply(NativeTerminal native,MessageType type,ulong sequence,byte[] plain) {
        if(type==MessageType.OutputChunk)native.ApplyOutput(sequence,OutputChunkPayload.Parser.ParseFrom(plain).Output.ToByteArray());
        else if(type==MessageType.ResizeApplied){var resize=ResizePayload.Parser.ParseFrom(plain);native.ApplyResize(sequence,resize.Columns,resize.Rows);}
        else native.ApplyAdvance(sequence,native.GetPresentationInfo().LogicalTimeMicros);
        // Android's native engine accepts complete source events while parser
        // or DEC2026 presentation is deferred. ACK persistence is independent
        // of exporting a snapshot after every partial terminal token.
        if(native.TryCaptureCheckpoint(out var committed,out _))
        { using(committed) if(committed.Info.NextSourceSequence!=sequence+1)throw new Exception("Android durable source sequence"); }
    }
    private bool ValidateProof(HttpListenerRequest request, byte[] body, Device device)
    {
        if (request.Headers["Authorization"] != "Bearer " + TokenFor(device.Subject)) return false;
        var headers = request.Headers;
        var nonce = headers["X-TermLink-Nonce"] ?? "";
        var timestamp = headers["X-TermLink-Timestamp"];
        if (!long.TryParse(timestamp, out var milliseconds) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - milliseconds) > 120000 ||
            nonce.Length < 16 || !nonces.TryAdd(nonce, true)) return false;
        var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", request.HttpMethod, request.RawUrl, timestamp, nonce,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        return Verify(device.Signing, canonical, headers["X-TermLink-Signature"] ?? "");
    }

    private static bool Verify(string key, string message, string signature)
    {
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(Decode(key), 0));
        var bytes = Encoding.UTF8.GetBytes(message);
        signer.BlockUpdate(bytes, 0, bytes.Length);
        return signer.VerifySignature(Decode(signature));
    }
    private static byte[] Decode(string value)
    { var text = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '=')); }
    private async Task<JsonDocument> Receive(WebSocket socket)
    {
        var bytes = new byte[16384];
        var result = await socket.ReceiveAsync(bytes.AsMemory(), shutdown.Token);
        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Closed fixture.");
        return JsonDocument.Parse(bytes.AsMemory(0, result.Count));
    }
    private async Task Json(HttpListenerContext context, object value, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value), shutdown.Token);
        context.Response.Close();
    }
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); listener.Close();
        await loop;
        await Task.WhenAll(clients);
        shutdown.Dispose(); mobile.Dispose();
    }
}
