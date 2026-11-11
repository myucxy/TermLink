using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Pc.Contracts;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent.Connection;

public sealed partial class RelayConnectionService
{
    private static readonly string[] TerminalCapabilities = ["realtime_terminal_v1", "terminal_snapshot_v1", "terminal_snapshot_v2", "host_history_v1", "host_session_catalog_v1", "terminal_commands_v1", "terminal_window_resize_v1", "terminal_host_resize_v1"];
    private static readonly string[] TerminalTransportCapabilities = ["host_history_v1", "host_session_catalog_v1", "terminal_attach_v1", "terminal_snapshot_v1", "terminal_snapshot_v2", "bounded_event_replay_v1", "terminal_window_resize_v1", "terminal_host_resize_v1"];
    private readonly Func<string, IRelayTerminalHost>? terminalHostFactory;
    private readonly IReadOnlyList<TerminalCommandCapability> terminalCommands;
    private IRelayTerminalHost? terminalHost;
    public bool SupportsTerminal => terminalHostFactory is not null;
    public IReadOnlySet<string> Capabilities => (SupportsAi ? new[] { "ai_agent_v1" } : []).Concat(SupportsTerminal ? TerminalCapabilities : []).Concat(SupportsFiles ? new[] { "file_operations_v1", "file_operation_name_filter_v1" } : []).ToHashSet(StringComparer.Ordinal);
    public IReadOnlyList<ManagedSessionSnapshot> TerminalSessions => terminalHost?.Sessions.Select(s => new ManagedSessionSnapshot(s.Id, s.Title, s.Shell,
        s.State == "RUNNING" ? (Snapshot.State == AgentConnectionState.Connected ? ManagedSessionState.Connected : ManagedSessionState.Disconnected) :
        s.State == "EXITED" ? ManagedSessionState.Ended : ManagedSessionState.Interrupted, 0, s.ActivityAt, s.Sequence)).ToArray() ?? [];

    public async Task<Guid> StartTerminalAsync(string command, string directory, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (account?.RemoteAccessEnabled != true || terminalHost is null) throw new RelayOperationException("TERMINAL_CONNECTION_REQUIRED");
            using var device = OpenIdentity(account.Identity);
            var id = await terminalHost.StartAsync(command, directory, Guid.Empty, token).ConfigureAwait(false);
            await PublishTerminalSessionsAsync(account, device, token).ConfigureAwait(false);
            return id;
        }
        finally { operations.Release(); }
    }
    public Task StopTerminalAsync(Guid id, CancellationToken token) => terminalHost?.StopAsync(id, token) ?? Task.CompletedTask;
    private async Task RetireTerminalHostAsync()
    {
        var retired = terminalHost; terminalHost = null;
        if (retired is not null) await retired.DisposeAsync().ConfigureAwait(false);
    }
    private async Task PublishTerminalSessionsAsync(Account current, DeviceIdentity device, CancellationToken token)
    {
        if (terminalHost is null) return;
        foreach (var s in terminalHost.Sessions)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { pc_id = current.Identity.PcId, shell_type = s.Shell, state = s.State,
                columns = s.Columns, rows = s.Rows, last_output_seq = s.Sequence, session_heartbeat_capable = false });
            using var response = await SendAsync(current, device, HttpMethod.Put, "/v1/sessions/" + s.Id.ToString("D"), body, true, token).ConfigureAwait(false);
            RelayHttp.RequireSuccess(response);
        }
    }

    private sealed class TerminalPlane : IAsyncDisposable
    {
        private readonly RelayConnectionService owner;
        private readonly Account account;
        private readonly DeviceIdentity device;
        private readonly IRelayTerminalHost host;
        private readonly Func<byte[], WebSocketMessageType, Task> send;
        private readonly CancellationToken token;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Dictionary<(Guid, Guid), Subscription> subscribers = new();
        private readonly Guid instance = Guid.NewGuid();
        private readonly Dictionary<(Guid, Guid), ReplayRoute> replayRoutes = new();
        private readonly Queue<((Guid, Guid) Route, ulong Sequence)> replayOrder = new();
        private long replayBytes;
        public TerminalPlane(RelayConnectionService owner, Account account, DeviceIdentity device,
            IRelayTerminalHost host, Func<byte[], WebSocketMessageType, Task> send, CancellationToken token)
        { this.owner = owner; this.account = account; this.device = device; this.host = host; this.send = send; this.token = token; }

        public async Task HandleAsync(Envelope envelope)
        {
            if (envelope.ProtocolVersion != 1 || envelope.Direction != Direction.AndroidToPc || envelope.Seq == 0 ||
                envelope.SessionId.Length != 16 || envelope.SenderDeviceId.Length != 16 || envelope.RecipientDeviceId.Length != 16 ||
                envelope.Nonce.Length != 12 || envelope.Ciphertext.Length is < 16 or > 64 * 1024 || GuidOf(envelope.RecipientDeviceId) != account.Identity.DeviceId) return;
            var sender = GuidOf(envelope.SenderDeviceId); var session = GuidOf(envelope.SessionId);
            var recipients = await owner.ReadAiRecipientsAsync(account, device, token).ConfigureAwait(false);
            if (!recipients.TryGetValue(sender, out var recipient)) return;
            var keyId = Encoding.UTF8.GetBytes("epoch-" + recipient.Epoch.ToString("D8", CultureInfo.InvariantCulture));
            if (!CryptographicOperations.FixedTimeEquals(keyId, envelope.KeyId.Span)) return;
            var pairwise = SessionCrypto.DerivePairwiseKey(device.Exchange, recipient.ExchangeKey, account.Identity.DeviceId, sender, recipient.Epoch);
            var key = SessionCrypto.DeriveSessionKey(pairwise, session, CryptoDirection.AndroidToPc, keyId);
            byte[]? plain = null;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                plain = SessionCrypto.Decrypt(key, envelope.Nonce.ToByteArray(), SessionCrypto.AssociatedData(envelope), envelope.Ciphertext.ToByteArray());
                if (session == account.Identity.PcId)
                {
                    if (envelope.MessageType == MessageType.SessionCatalogRequest)
                    {
                        var request = SessionCatalogRequestPayload.Parser.ParseFrom(plain);
                        if (!ValidId(request.RequestId)) return;
                        var response = new SessionCatalogResponsePayload { RequestId = request.RequestId, HostInstanceId = Id(instance), ObservedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                        response.Sessions.AddRange(host.Sessions.Select(s => new SessionCatalogEntry { SessionId = Id(s.Id), ShellType = s.Shell, Title = s.Title,
                            Lifecycle = s.State, Columns = (uint)s.Columns, Rows = (uint)s.Rows, LastOutputSeq = s.Sequence, CreatedAtMs = s.CreatedAt.ToUnixTimeMilliseconds(), ProcessAlive = s.State == "RUNNING" }));
                        await Reply(MessageType.SessionCatalogResponse, response, envelope.Seq).ConfigureAwait(false);
                    }
                    return;
                }
                if (!host.Sessions.Any(s => s.Id == session)) return;
                var identity = (sender, session);
                if (envelope.MessageType == MessageType.Subscribe)
                {
                    var request = SubscribePayload.Parser.ParseFrom(plain);
                    if (!ValidId(request.RequestId)) return;
                    var tail = host.Sessions.First(s => s.Id == session).Sequence;
                    var accepted = request.ResumeFromSeq <= tail && request.SubscriberCapabilities.Contains("terminal_state_2_21_v2") &&
                        request.SubscriberExchangePublicKey.Span.SequenceEqual(recipient.ExchangeKey);
                    if (accepted)
                    {
                        if (!subscribers.ContainsKey(identity) && subscribers.Count >= 64) return;
                        if (subscribers.Remove(identity, out var old)) old.Dispose();
                        subscribers.Add(identity, new(sender, session, recipient.Epoch, pairwise.ToArray(), keyId.ToArray())
                        { Ack = request.ResumeFromSeq, Sent = request.ResumeFromSeq, Ready = true, Paused = true,
                            SupportsContinuousHistory = request.SubscriberCapabilities.Contains("host_history_v1") });
                    }
                    var result = new TerminalAttachResultPayload { RequestId = request.RequestId, RequiredState = "terminal_state_2_21_v2", ErrorCode = accepted ? "" : "TERMINAL_STATE_UNSUPPORTED" };
                    result.HostCapabilities.AddRange(TerminalTransportCapabilities);
                    result.HostCapabilities.Add("terminal_state_2_21_v2");
                    await Reply(MessageType.TerminalAttachResult, result, envelope.Seq).ConfigureAwait(false);
                    return;
                }
                if (!subscribers.TryGetValue(identity, out var sub) || sub.Epoch != recipient.Epoch) return;
                switch (envelope.MessageType)
                {
                    case MessageType.Unsubscribe:
                        subscribers.Remove(identity); sub.Dispose(); break;
                    case MessageType.OutputAck:
                    {
                        var ack = OutputAckPayload.Parser.ParseFrom(plain);
                        if (sub.PendingSnapshotSequence is { } snapshotCommit)
                        {
                            // Chunk ACK means transport receipt only. Android/iOS
                            // must apply and persist the native snapshot, then send
                            // OutputAck so the Go Relay also advances its replay fence.
                            if (ack.HighestContiguousSeq != snapshotCommit) break;
                            sub.Ack = sub.Sent = snapshotCommit; sub.PendingSnapshotSequence = null;
                            sub.Inflight.Clear(); sub.Ready = true;
                            if (!sub.HasFlowControl) sub.Paused = false;
                        }
                        if (ack.HighestContiguousSeq < sub.Ack || ack.HighestContiguousSeq > sub.Sent) break;
                        sub.Ack = ack.HighestContiguousSeq;
                        foreach (var seq in sub.Inflight.Keys.Where(s => s <= sub.Ack).ToArray()) sub.Inflight.Remove(seq);
                        // Missing ranges require a new authoritative snapshot, not silent loss.
                        if (ack.Missing.Count > 0) await GapAsync(sub).ConfigureAwait(false);
                        break;
                    }
                    case MessageType.FlowControl:
                    {
                        var flow = FlowControlPayload.Parser.ParseFrom(plain);
                        if (flow.ControlEpoch <= sub.ControlEpoch) break;
                        sub.ControlEpoch = flow.ControlEpoch; sub.Paused = flow.Paused; sub.HasFlowControl = true;
                        sub.WindowEvents = Math.Min(256, flow.MaxUnackedEvents);
                        sub.WindowBytes = Math.Min(1024 * 1024, flow.MaxUnackedWireBytes);
                        break;
                    }
                    case MessageType.HostHistoryRequest:
                    {
                        var request = HostHistoryRequestPayload.Parser.ParseFrom(plain);
                        if (!ValidId(request.RequestId)) break;
                        if ((int)request.Kind == 3 && request.SubscriberCapabilities.Contains("terminal_state_2_21_v2"))
                        {
                            var range = await SelectEventRangeAsync(host, session, sender, request, token).ConfigureAwait(false);
                            if (range.Events is null)
                            {
                                await Reply(MessageType.HostHistoryResponse, new HostHistoryResponsePayload { RequestId = request.RequestId,
                                    Status = range.Status, ErrorCode = range.Error }, envelope.Seq).ConfigureAwait(false);
                                break;
                            }
                            var encrypted = HistoryCrypto.Seal(pairwise, session, account.Identity.DeviceId, sender, recipient.Epoch, range.Events);
                            sub.ClearHistory();
                            sub.History = new(request.RequestId, Guid.NewGuid(), 0, 0, 0, encrypted) {
                                Kind = HostHistoryObjectKindV1.EventChunk, Start = range.Events[0].Seq, End = range.Events[^1].Seq,
                                HasMore = range.Events[^1].Seq < request.ThroughSeq, KeyId = HistoryCrypto.KeyId(sender, recipient.Epoch),
                                FormatName = "BOUNDED_EVENT_REPLAY", FormatVersion = 1 };
                            await SendHistoryAsync(sub).ConfigureAwait(false);
                            break;
                        }
                        if ((int)request.Kind != 1 || !request.SubscriberCapabilities.Contains("terminal_state_2_21_v2"))
                        {
                            await Reply(MessageType.HostHistoryResponse, new HostHistoryResponsePayload { RequestId = request.RequestId,
                                Status = (HostHistoryResponseStatusV1)5, ErrorCode = "HISTORY_KIND_UNSUPPORTED" }, envelope.Seq).ConfigureAwait(false); break;
                        }
                        var snapshot = await host.CaptureAsync(session, token).ConfigureAwait(false);
                        try
                        {
                            var sealedObject = SnapshotCrypto.SealTerminalCoreSnapshot(pairwise, session, account.Identity.DeviceId, sender,
                                recipient.Epoch, snapshot.Sequence, snapshot.Columns, snapshot.Rows, 1, 0, snapshot.Bytes);
                            if (sealedObject.Length > 16 * 1024 * 1024) throw new IOException("TERMINAL_SNAPSHOT_TOO_LARGE");
                            sub.ClearHistory();
                            sub.History = new(request.RequestId, Guid.NewGuid(), snapshot.Sequence, snapshot.Columns, snapshot.Rows, sealedObject);
                            sub.Ready = false;
                            await SendHistoryAsync(sub).ConfigureAwait(false);
                        }
                        finally { CryptographicOperations.ZeroMemory(snapshot.Bytes); }
                        break;
                    }
                    case MessageType.HostHistoryControl:
                    {
                        var control = HostHistoryControlPayload.Parser.ParseFrom(plain); var history = sub.History;
                        if (history is null || !control.RequestId.Equals(history.Request) || !control.ObjectId.Equals(Id(history.ObjectId))) break;
                        if ((int)control.Action == 2) { sub.ClearHistory(); break; }
                        if ((int)control.Action != 1 || control.ChunkIndex != history.Index) break;
                        history.Index++;
                        if (history.Index == history.Count)
                        {
                            if (history.Kind == HostHistoryObjectKindV1.Snapshot)
                            {
                                sub.PendingSnapshotSequence = history.Sequence;
                                sub.Sent = history.Sequence; sub.Inflight.Clear(); sub.Ready = false;
                            }
                            sub.ClearHistory();
                        }
                        else await SendHistoryAsync(sub).ConfigureAwait(false);
                        break;
                    }
                    case MessageType.InputEvent:
                    {
                        var request = InputEventPayload.Parser.ParseFrom(plain);
                        var error = await host.InputAsync(session, sender, request, () => LeaseAsync(sender, session, request.LeaseId, request.LeaseVersion, recipient.Permission), token).ConfigureAwait(false);
                        await host.RecordInputResultAsync(session, sender,
                            new InputResultPayload { InputId = request.InputId, Accepted = error is null, ReasonCode = error ?? "" }, token).ConfigureAwait(false);
                        break;
                    }
                    case MessageType.ResizeRequest:
                    {
                        var request = ResizePayload.Parser.ParseFrom(plain);
                        var error = await host.ResizeAsync(session, request, () => LeaseAsync(sender, session, request.LeaseId, request.LeaseVersion, recipient.Permission), token).ConfigureAwait(false);
                        await Reply(MessageType.ResizeResponse, new ResizeResponsePayload { RequestId = request.RequestId,
                            Columns = request.Restore && error is null ? 100u : request.Columns, Rows = request.Restore && error is null ? 30u : request.Rows, ErrorCode = error ?? "" }, envelope.Seq).ConfigureAwait(false);
                        break;
                    }
                    case MessageType.SessionTerminateRequest:
                    {
                        var request = SessionTerminateRequestPayload.Parser.ParseFrom(plain);
                        if (!ValidId(request.RequestId)) break;
                        var accepted = recipient.Permission == "MANAGE";
                        if (accepted) await host.StopAsync(session, token).ConfigureAwait(false);
                        await Reply(MessageType.SessionTerminateResponse, new SessionTerminateResponsePayload {
                            RequestId = request.RequestId, Accepted = accepted, ErrorCode = accepted ? "" : "MANAGE_NOT_GRANTED" }, envelope.Seq).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (Exception error) when (error is CryptographicException or InvalidProtocolBufferException or KeyNotFoundException or FormatException) { }
            finally
            {
                gate.Release(); CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(pairwise);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            }
            Task Reply(MessageType type, IMessage payload, ulong seq) => SendEnvelopeAsync(sender, session, pairwise, keyId, type, payload.ToByteArray(), seq);
        }

        private async Task<bool> LeaseAsync(Guid sender, Guid session, ByteString leaseId, long version, string permission)
        {
            if (permission is not ("CONTROL" or "MANAGE") || !ValidId(leaseId) || version < 1) return false;
            using var response = await owner.SendAsync(account, device, HttpMethod.Get, "/v1/sessions/" + session.ToString("D") + "/lease?sender_device_id=" + sender.ToString("D"), [], true, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            using var json = await RelayHttp.ReadJsonAsync(response, token).ConfigureAwait(false); var root = json.RootElement;
            return root.GetProperty("session_id").GetGuid() == session && root.GetProperty("authorized_sender_device_id").GetGuid() == sender &&
                root.GetProperty("lease_id").GetGuid() == GuidOf(leaseId) && root.GetProperty("version").GetInt64() == version &&
                root.GetProperty("expires_at").GetDateTimeOffset() > DateTimeOffset.UtcNow;
        }

        public async Task PumpAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    foreach (var sub in subscribers.Values)
                    {
                        if (sub.History is { } pending && pending.Created < DateTimeOffset.UtcNow.AddSeconds(-30)) sub.ClearHistory();
                        if (sub.NeedsRecovery) { await GapAsync(sub).ConfigureAwait(false); continue; }
                        if (!sub.Ready || sub.Paused || sub.Inflight.Count >= sub.WindowEvents) continue;
                        var events = sub.SupportsContinuousHistory
                            ? await host.ReadHistoryAsync(sub.Session, sub.Sent, token).ConfigureAwait(false)
                            : await host.ReadAsync(sub.Session, sub.Sent, token).ConfigureAwait(false);
                        if (events is null) { await GapAsync(sub).ConfigureAwait(false); continue; }
                        foreach (var item in events)
                        {
                            var size = item.Payload.Length + 256;
                            if (sub.Inflight.Count >= sub.WindowEvents || sub.Inflight.Values.Sum() + size > (long)sub.WindowBytes) break;
                            // A same-socket resubscription must replay identical ciphertext;
                            // the Go Relay rejects changing bytes at an already visible sequence.
                            if (replayRoutes.TryGetValue((sub.Sender, sub.Session), out var route) &&
                                item.Sequence <= route.HighWater && !route.Frames.ContainsKey(item.Sequence))
                            { await GapAsync(sub).ConfigureAwait(false); break; }
                            // History projects a transport-only Gap to a neutral
                            // native advance. If this exact recipient already saw
                            // the Gap, same-socket replay must retain its original
                            // wire bytes and return to snapshot recovery instead.
                            if (replayRoutes.TryGetValue((sub.Sender, sub.Session), out var sentRoute) &&
                                sentRoute.Frames.TryGetValue(item.Sequence, out var sentFrame) && sentFrame.Type == MessageType.Gap)
                            {
                                await send(sentFrame.Wire, WebSocketMessageType.Binary).ConfigureAwait(false);
                                sub.Sent = item.Sequence; sub.Ready = false; break;
                            }
                            var type = item.Type; var payload = item.Payload;
                            if (item.Recipient is { } intended && intended != sub.Sender)
                            {
                                type = MessageType.SessionUpsert;
                                payload = new SessionUpsertPayload().ToByteArray();
                            }
                            await SendEnvelopeAsync(sub.Sender, sub.Session, sub.Pairwise, sub.KeyId, type, payload, item.Sequence).ConfigureAwait(false);
                            if (type == MessageType.Gap) { sub.Ready = false; sub.Sent = item.Sequence; break; }
                            sub.Sent = item.Sequence; sub.Inflight.Add(item.Sequence, size);
                        }
                    }
                }
                finally { gate.Release(); }
            }
        }
        private async Task GapAsync(Subscription s)
        {
            s.NeedsRecovery = true;
            var recovery = await host.RecoverAsync(s.Session, s.Sender, s.Ack, token).ConfigureAwait(false);
            if (recovery is null) return;
            s.Ready = false; s.NeedsRecovery = false; s.Sent = recovery.Sequence;
            await SendEnvelopeAsync(s.Sender, s.Session, s.Pairwise, s.KeyId, MessageType.Gap, recovery.Payload, recovery.Sequence).ConfigureAwait(false);
        }
        private Task SendHistoryAsync(Subscription s)
        {
            var h = s.History!; var offset = checked((int)h.Index * History.ChunkBytes);
            var snapshot = h.Kind == HostHistoryObjectKindV1.Snapshot;
            return SendEnvelopeAsync(s.Sender, s.Session, s.Pairwise, s.KeyId, MessageType.HostHistoryResponse,
                new HostHistoryResponsePayload { RequestId = h.Request, Status = HostHistoryResponseStatusV1.Ok, Kind = h.Kind,
                    ObjectId = Id(h.ObjectId), InitialTranscriptBeforeLineId = snapshot ? 1u : 0u, SnapshotSeq = h.Sequence, Columns = h.Columns, Rows = h.Rows,
                    StartSeq = h.Start, EndSeq = h.End, HasMoreEvents = h.HasMore,
                    KeyId = snapshot ? SnapshotCrypto.TerminalCoreSnapshotKeyId(s.Sender, s.Epoch) : h.KeyId,
                    FormatName = h.FormatName, FormatVersion = h.FormatVersion,
                    ObjectSha256 = ByteString.CopyFrom(h.Hash), ObjectBytes = (ulong)h.Bytes.Length, ChunkIndex = h.Index, ChunkCount = h.Count,
                    ObjectChunk = ByteString.CopyFrom(h.Bytes, offset, Math.Min(History.ChunkBytes, h.Bytes.Length - offset)) }.ToByteArray(), snapshot ? h.Sequence : h.End);
        }
        private sealed record EventSelection(HostHistoryResponseStatusV1 Status, string Error, List<HistoryEventV1>? Events);
        private static async Task<EventSelection> SelectEventRangeAsync(IRelayTerminalHost host, Guid session, Guid recipient,
            HostHistoryRequestPayload request, CancellationToken token)
        {
            if (request.ThroughSeq == 0 || request.AfterSeq >= request.ThroughSeq || request.ThroughSeq >= long.MaxValue)
                return new(HostHistoryResponseStatusV1.Rejected, "INVALID_EVENT_RANGE", null);
            var events = new List<HistoryEventV1>();
            var maximum = request.MaxEvents == 0 ? 512u : Math.Min(4096u, request.MaxEvents);
            var after = request.AfterSeq; var bytes = 0;
            while (after < request.ThroughSeq && events.Count < maximum)
            {
                var page = await host.ReadHistoryAsync(session, after, token).ConfigureAwait(false);
                if (page is null) return new(HostHistoryResponseStatusV1.Gap, "EVENT_RANGE_GAP", null);
                if (page.Count == 0) break;
                foreach (var item in page)
                {
                    if (item.Sequence > request.ThroughSeq || events.Count >= maximum) break;
                    if (item.Sequence != after + 1 || item.RequiresSnapshot || item.Type == MessageType.Gap)
                        return new(HostHistoryResponseStatusV1.Gap, "EVENT_RANGE_GAP", null);
                    var type = item.Type; var payload = item.Payload;
                    if (item.Recipient is { } intended && intended != recipient)
                    { type = MessageType.SessionUpsert; payload = new SessionUpsertPayload().ToByteArray(); }
                    if (events.Count > 0 && bytes + payload.Length + 32 > 8 * 1024 * 1024)
                        return new(HostHistoryResponseStatusV1.Ok, "", events);
                    events.Add(new HistoryEventV1 { Seq = item.Sequence, MessageType = type, Payload = ByteString.CopyFrom(payload) });
                    bytes += payload.Length + 32; after = item.Sequence;
                }
            }
            return events.Count == 0 ? new(HostHistoryResponseStatusV1.NotReady, "EVENT_RANGE_NOT_READY", null) :
                new(HostHistoryResponseStatusV1.Ok, "", events);
        }
        private async Task SendEnvelopeAsync(Guid sender, Guid session, byte[] pairwise, byte[] keyId, MessageType type, byte[] payload, ulong seq)
        {
            var ordered = type is MessageType.OutputChunk or MessageType.ResizeApplied or MessageType.SessionStarted or
                MessageType.SessionEnded or MessageType.SessionInterrupted or MessageType.SessionUpsert or MessageType.InputAccepted or MessageType.InputRejected or MessageType.Gap;
            ReplayRoute? cachedRoute = null;
            byte[]? identityHash = null;
            if (ordered)
            {
                var identity = (sender, session);
                if (!replayRoutes.TryGetValue(identity, out cachedRoute))
                {
                    if (replayRoutes.Count >= 128) throw new RelayOperationException("TERMINAL_REPLAY_ROUTE_LIMIT");
                    replayRoutes[identity] = cachedRoute = new ReplayRoute();
                }
                identityHash = SHA256.HashData(new[] { (byte)type }.Concat(payload).ToArray());
                if (cachedRoute.Frames.TryGetValue(seq, out var retained))
                {
                    if (!CryptographicOperations.FixedTimeEquals(identityHash, retained.Hash)) throw new RelayOperationException("TERMINAL_REPLAY_CHANGED");
                    await send(retained.Wire, WebSocketMessageType.Binary).ConfigureAwait(false); return;
                }
                if (seq <= cachedRoute.HighWater) throw new RelayOperationException("TERMINAL_REPLAY_EVICTED");
            }
            var envelope = new Envelope { ProtocolVersion = 1, MessageType = type, SessionId = Id(session), SenderDeviceId = Id(account.Identity.DeviceId),
                RecipientDeviceId = Id(sender), Direction = Direction.PcToAndroid, Seq = seq, KeyId = ByteString.CopyFrom(keyId),
                SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)) };
            var key = SessionCrypto.DeriveSessionKey(pairwise, session, CryptoDirection.PcToAndroid, keyId);
            try
            {
                envelope.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(key, envelope.Nonce.ToByteArray(), SessionCrypto.AssociatedData(envelope), payload));
                var wire = envelope.ToByteArray();
                if (cachedRoute is not null)
                {
                    cachedRoute.HighWater = Math.Max(cachedRoute.HighWater, seq);
                    cachedRoute.Frames[seq] = new(wire, identityHash!, type);
                    replayOrder.Enqueue(((sender, session), seq)); replayBytes += wire.Length;
                    while (replayBytes > 8 * 1024 * 1024 || replayOrder.Count > 4096)
                    {
                        var oldest = replayOrder.Dequeue();
                        if (replayRoutes[oldest.Route].Frames.Remove(oldest.Sequence, out var retired)) replayBytes -= retired.Wire.Length;
                    }
                }
                await send(wire, WebSocketMessageType.Binary).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        public async ValueTask DisposeAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { foreach (var s in subscribers.Values) s.Dispose(); subscribers.Clear(); replayRoutes.Clear(); replayOrder.Clear(); }
            finally { gate.Release(); }
        }
        private static ByteString Id(Guid id) => ByteString.CopyFrom(Convert.FromHexString(id.ToString("N")));
        private static bool ValidId(ByteString id) => id.Length == 16 && GuidOf(id) != Guid.Empty;
        private sealed record ReplayFrame(byte[] Wire, byte[] Hash, MessageType Type);
        private sealed class ReplayRoute { public ulong HighWater; public readonly Dictionary<ulong, ReplayFrame> Frames = new(); }
        private sealed class Subscription : IDisposable
        {
            public Subscription(Guid sender, Guid session, long epoch, byte[] pairwise, byte[] keyId) { Sender = sender; Session = session; Epoch = epoch; Pairwise = pairwise; KeyId = keyId; }
            public readonly Guid Sender, Session; public readonly long Epoch; public readonly byte[] Pairwise, KeyId;
            public ulong? PendingSnapshotSequence;
            public ulong Ack, Sent, ControlEpoch, WindowBytes = 1024 * 1024; public uint WindowEvents = 128;
            public bool Ready, Paused, NeedsRecovery, HasFlowControl, SupportsContinuousHistory; public History? History; public readonly Dictionary<ulong, int> Inflight = new();
            public void ClearHistory() { if (History is { } h) CryptographicOperations.ZeroMemory(h.Bytes); History = null; }
            public void Dispose() { ClearHistory(); CryptographicOperations.ZeroMemory(Pairwise); }
        }
        private sealed class History
        {
            public const int ChunkBytes = 512 * 1024;
            public History(ByteString request, Guid id, ulong seq, uint cols, uint rows, byte[] bytes)
            { Request = request; ObjectId = id; Sequence = seq; Columns = cols; Rows = rows; Bytes = bytes; Hash = SHA256.HashData(bytes); Count = (uint)((bytes.Length + ChunkBytes - 1) / ChunkBytes); }
            public readonly ByteString Request; public readonly Guid ObjectId; public readonly ulong Sequence; public readonly uint Columns, Rows, Count;
            public HostHistoryObjectKindV1 Kind = HostHistoryObjectKindV1.Snapshot;
            public ulong Start, End; public bool HasMore;
            public string KeyId = "", FormatName = SnapshotCrypto.TerminalCoreSnapshotFormatName;
            public uint FormatVersion = SnapshotCrypto.TerminalCoreSnapshotFormatVersion;
            public readonly byte[] Bytes, Hash; public uint Index; public readonly DateTimeOffset Created = DateTimeOffset.UtcNow;
        }
    }
}
