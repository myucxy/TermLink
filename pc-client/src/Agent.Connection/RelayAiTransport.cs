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
    private void RetireAiHost()
    {
        var retired = aiHost;
        aiHost = null;
        retired?.Dispose();
    }

    private sealed record AiRecipient(Guid Id, byte[] ExchangeKey, long Epoch);

    private async Task<Dictionary<Guid, AiRecipient>> ReadAiRecipientsAsync(Account current, DeviceIdentity device, CancellationToken token)
    {
        using var response = await SendAsync(current, device, HttpMethod.Get,
            "/v1/pcs/" + current.Identity.PcId.ToString("D") + "/grants", [], true, token).ConfigureAwait(false);
        RelayHttp.RequireSuccess(response);
        using var json = await RelayHttp.ReadJsonAsync(response, token).ConfigureAwait(false);
        var root = json.RootElement;
        var epoch = root.GetProperty("key_epoch").GetInt64();
        if (epoch < current.Identity.KeyEpoch) throw new RelayOperationException("AI_AUTHORITY_INVALID");
        var result = new Dictionary<Guid, AiRecipient>();
        foreach (var value in root.GetProperty("items").EnumerateArray()) {
            if (result.Count >= PcContractLimits.MaximumDevices) throw new RelayOperationException("AI_AUTHORITY_INVALID");
            if (value.GetProperty("device_type").GetString() is not ("ANDROID" or "IOS") ||
                value.GetProperty("permission").GetString() != "MANAGE") continue;
            if (value.TryGetProperty("expires_at", out var expires) && expires.ValueKind != JsonValueKind.Null &&
                expires.GetDateTimeOffset() <= DateTimeOffset.UtcNow) continue;
            var id = value.GetProperty("device_id").GetGuid();
            var exchange = Convert.FromBase64String(value.GetProperty("exchange_public_key").GetString()!);
            if (id == Guid.Empty || id == current.Identity.DeviceId || exchange.Length != 32 || !result.TryAdd(id, new(id, exchange, epoch)))
                throw new RelayOperationException("AI_AUTHORITY_INVALID");
        }
        return result;
    }

    private async Task PumpAiAsync(ClientWebSocket socket, Account current, DeviceIdentity device, int heartbeat, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancellation = lifetime.Token;
        using var sendGate = new SemaphoreSlim(1, 1);
        using var slots = new SemaphoreSlim(12, 12);
        using var commands = new SemaphoreSlim(8, 8);
        using var watches = new SemaphoreSlim(4, 4);
        var jobs = new List<Task>();
        var host = aiHost!;
        var recipients = await ReadAiRecipientsAsync(current, device, cancellation).ConfigureAwait(false);
        var authorityFingerprint = Fingerprint(recipients);
        string? probe = null;
        TaskCompletionSource? pong = null;
        var receive = ReceiveAsync();
        try {
            while (!cancellation.IsCancellationRequested) {
                probe = Guid.NewGuid().ToString("D");
                pong = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "ping", probe_id = probe }), WebSocketMessageType.Text).ConfigureAwait(false);
                await pong.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
                var updated = await ReadAiRecipientsAsync(current, device, cancellation).ConfigureAwait(false);
                if (Fingerprint(updated) != authorityFingerprint) {
                    // Revocation, grant or epoch changes retire the whole local AI
                    // owner, including pending tools; no accepted work keeps old authority.
                    RetireAiHost();
                    throw new RelayOperationException("AI_AUTHORITY_CHANGED");
                }
                using var response = await this.SendAsync(current, device, HttpMethod.Get, "/v1/devices", [], true, cancellation).ConfigureAwait(false);
                RelayHttp.RequireSuccess(response);
                using var json = await RelayHttp.ReadJsonAsync(response, cancellation).ConfigureAwait(false);
                var devices = json.RootElement.GetProperty("items").EnumerateArray().Take(PcContractLimits.MaximumDevices).Select(v => new AccountDeviceSnapshot(
                    v.GetProperty("id").GetGuid(), v.GetProperty("name").GetString() ?? "", v.GetProperty("type").GetString() ?? "",
                    v.GetProperty("trust_state").GetString() ?? "", v.TryGetProperty("online", out var online) && online.GetBoolean())).ToArray();
                Publish(AgentConnectionState.Connected, devices: devices);
                await UpsertAsync(current, device, cancellation).ConfigureAwait(false);
                var delay = Task.Delay(TimeSpan.FromSeconds(Math.Min(15, heartbeat)), cancellation);
                if (await Task.WhenAny(delay, receive).ConfigureAwait(false) == receive) await receive.ConfigureAwait(false);
                await delay.ConfigureAwait(false);
                if (current.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30)) {
                    await RefreshIfNeededAsync(cancellation).ConfigureAwait(false);
                    return;
                }
            }
        } finally {
            lifetime.Cancel();
            try { await receive.ConfigureAwait(false); } catch (Exception) when (cancellation.IsCancellationRequested) { }
            try { await Task.WhenAll(jobs).ConfigureAwait(false); } catch (Exception) when (cancellation.IsCancellationRequested) { }
        }

        async Task SendAsync(byte[] bytes, WebSocketMessageType type)
        {
            await sendGate.WaitAsync(cancellation).ConfigureAwait(false);
            try { await socket.SendAsync(bytes, type, true, cancellation).ConfigureAwait(false); }
            finally { sendGate.Release(); }
        }

        async Task ReceiveAsync()
        {
            try {
                while (!cancellation.IsCancellationRequested) {
                    var bytes = new byte[1024 * 1024];
                    var count = 0;
                    WebSocketMessageType? type = null;
                    while (true) {
                        if (count == bytes.Length) throw new RelayOperationException("RELAY_CONTROL_TOO_LARGE");
                        var part = await socket.ReceiveAsync(bytes.AsMemory(count), cancellation).ConfigureAwait(false);
                        if (part.MessageType == WebSocketMessageType.Close) throw new RelayOperationException("RELAY_DISCONNECTED");
                        if (type is not null && type != part.MessageType) throw new RelayOperationException("RELAY_CONTROL_INVALID");
                        type = part.MessageType;
                        count += part.Count;
                        if (part.EndOfMessage) break;
                    }
                    if (type == WebSocketMessageType.Text) {
                        if (count > 16 * 1024) throw new RelayOperationException("RELAY_CONTROL_TOO_LARGE");
                        using var control = JsonDocument.Parse(bytes.AsMemory(0, count));
                        var root = control.RootElement;
                        if (root.GetProperty("type").GetString() != "pong" || root.GetProperty("probe_id").GetString() != probe)
                            throw new RelayOperationException("RELAY_LIVENESS_INVALID");
                        pong?.TrySetResult();
                        continue;
                    }
                    if (type != WebSocketMessageType.Binary) throw new RelayOperationException("RELAY_CONTROL_INVALID");
                    Envelope envelope;
                    try { envelope = Envelope.Parser.ParseFrom(bytes, 0, count); }
                    catch (InvalidProtocolBufferException) { continue; }
                    if (envelope.MessageType != MessageType.AiAgentRequest || !slots.Wait(0)) continue;
                    jobs.RemoveAll(t => t.IsCompleted);
                    jobs.Add(DispatchAsync(envelope));
                }
            } catch (Exception error) {
                pong?.TrySetException(error);
                lifetime.Cancel();
                throw;
            }
        }

        async Task DispatchAsync(Envelope envelope)
        {
            byte[]? pairwise = null, key = null, plain = null;
            SemaphoreSlim? requestSlot = null;
            try {
                if (envelope.ProtocolVersion != 1 || envelope.Direction != Direction.AndroidToPc || envelope.Ack != 0 || envelope.Seq == 0 ||
                    envelope.SessionId.Length != 16 || envelope.SenderDeviceId.Length != 16 || envelope.RecipientDeviceId.Length != 16 ||
                    envelope.Nonce.Length != 12 || envelope.Ciphertext.Length is < 16 or > 60 * 1024 ||
                    GuidOf(envelope.SessionId) != current.Identity.PcId || GuidOf(envelope.RecipientDeviceId) != current.Identity.DeviceId) return;
                var sender = GuidOf(envelope.SenderDeviceId);
                var authorized = await ReadAiRecipientsAsync(current, device, cancellation).ConfigureAwait(false);
                if (Fingerprint(authorized) != authorityFingerprint) { RetireAiHost(); lifetime.Cancel(); return; }
                if (!authorized.TryGetValue(sender, out var recipient)) return;
                var keyId = Encoding.UTF8.GetBytes("epoch-" + recipient.Epoch.ToString("D8", CultureInfo.InvariantCulture));
                if (!CryptographicOperations.FixedTimeEquals(keyId, envelope.KeyId.Span)) return;
                pairwise = SessionCrypto.DerivePairwiseKey(device.Exchange, recipient.ExchangeKey, current.Identity.DeviceId, sender, recipient.Epoch);
                key = SessionCrypto.DeriveSessionKey(pairwise, current.Identity.PcId, CryptoDirection.AndroidToPc, keyId);
                plain = SessionCrypto.Decrypt(key, envelope.Nonce.ToByteArray(), SessionCrypto.AssociatedData(envelope), envelope.Ciphertext.ToByteArray());
                var request = AiAgentRequestPayload.Parser.ParseFrom(plain);
                if (request.RequestId.Length != 16 || GuidOf(request.RequestId) == Guid.Empty || Encoding.UTF8.GetByteCount(request.Json) > 48 * 1024) return;
                using (var body = JsonDocument.Parse(request.Json)) {
                    var watch = body.RootElement.TryGetProperty("action", out var action) && action.GetString() == "watch";
                    var selected = watch ? watches : commands;
                    if (!selected.Wait(0)) return;
                    requestSlot = selected;
                }
                bool StillAuthorized() => !cancellation.IsCancellationRequested && ReferenceEquals(host, aiHost) && account?.RemoteAccessEnabled == true;
                var response = await host.ExecuteAsync(sender, request.Json, cancellation, StillAuthorized).ConfigureAwait(false);
                if (!StillAuthorized()) return;
                authorized = await ReadAiRecipientsAsync(current, device, cancellation).ConfigureAwait(false);
                if (Fingerprint(authorized) != authorityFingerprint) { RetireAiHost(); lifetime.Cancel(); return; }
                var payload = new AiAgentResponsePayload { RequestId = request.RequestId, Json = response }.ToByteArray();
                if (payload.Length > 900 * 1024) return;
                var reply = new Envelope {
                    ProtocolVersion = 1, MessageType = MessageType.AiAgentResponse, SessionId = envelope.SessionId,
                    SenderDeviceId = envelope.RecipientDeviceId, RecipientDeviceId = envelope.SenderDeviceId,
                    Direction = Direction.PcToAndroid, Seq = envelope.Seq, KeyId = ByteString.CopyFrom(keyId),
                    SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)),
                };
                CryptographicOperations.ZeroMemory(key);
                key = SessionCrypto.DeriveSessionKey(pairwise, current.Identity.PcId, CryptoDirection.PcToAndroid, keyId);
                reply.Ciphertext = ByteString.CopyFrom(SessionCrypto.Encrypt(key, reply.Nonce.ToByteArray(), SessionCrypto.AssociatedData(reply), payload));
                CryptographicOperations.ZeroMemory(payload);
                await SendAsync(reply.ToByteArray(), WebSocketMessageType.Binary).ConfigureAwait(false);
            } catch (Exception error) when (error is InvalidProtocolBufferException or CryptographicException or JsonException or
                InvalidOperationException or IOException or HttpRequestException or OperationCanceledException or FormatException) { }
            finally {
                if (pairwise is not null) CryptographicOperations.ZeroMemory(pairwise);
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (plain is not null) CryptographicOperations.ZeroMemory(plain);
                requestSlot?.Release();
                slots.Release();
            }
        }
    }

    private static Guid GuidOf(ByteString value) => new(Convert.ToHexString(value.Span));
    private static string Fingerprint(Dictionary<Guid, AiRecipient> recipients) => string.Join("|", recipients.OrderBy(p => p.Key)
        .Select(p => p.Key.ToString("D") + ":" + p.Value.Epoch + ":" + Convert.ToBase64String(p.Value.ExchangeKey)));
}
