using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using TermLink.Agent;
using TermLink.Protocol;
using TermLink.Protocol.Transport.V1;

namespace TermLink.ProductionDirectTransportSignaling.Validation
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            try
            {
                await ValidateFeatureOffPerformsNoIoAsync().ConfigureAwait(false);
                await ValidateConfigProofAndCrossLanguageTicketAsync().ConfigureAwait(false);
                await ValidateStrictConfigAndHttpFaultsAsync().ConfigureAwait(false);
                await ValidateIndependentSignalHandshakeAndFramesAsync().ConfigureAwait(false);
                await ValidateMultipairNegotiationAndSignalGateAsync().ConfigureAwait(false);
                await ValidateBoundedSignalQueuesAsync().ConfigureAwait(false);
                await ValidateIndependentFullJitterRetryAsync().ConfigureAwait(false);
                ValidateRetryBoundsAndServerOrigin();
                Console.WriteLine("Production direct transport signalling validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static async Task ValidateFeatureOffPerformsNoIoAsync()
        {
            var http = new ScriptedHttpHandler(_ =>
                throw new InvalidOperationException("Feature-off direct HTTP performed network I/O."));
            var sockets = new ScriptedSocketFactory(() =>
                throw new InvalidOperationException("Feature-off direct signalling created a socket."));
            var tokens = 0;
            using var identity = DeviceIdentity.Generate();
            await using var client = new ProductionDirectTransportSignalingClient(
                "https://direct.invalid",
                Guid.NewGuid(),
                identity,
                _ =>
                {
                    Interlocked.Increment(ref tokens);
                    return Task.FromResult("must-not-be-read");
                },
                httpHandler: http,
                socketFactory: sockets);

            var config = await client.FetchConfigAsync().ConfigureAwait(false);
            var ticket = await client.IssueTicketAsync(
                Guid.NewGuid(),
                NewVersion4Guid(),
                null).ConfigureAwait(false);
            Require(config.Status == ProductionDirectSignalingCallStatus.Disabled &&
                    ticket.Status == ProductionDirectSignalingCallStatus.Disabled &&
                    !client.Start() && !client.TrySend(new TransportSignalEnvelopeV1()) &&
                    tokens == 0 && http.RequestCount == 0 && sockets.CreateCount == 0 &&
                    client.Snapshot.State == ProductionDirectSignalingLaneState.Disabled,
                "Feature-off direct signalling was not a zero-I/O boundary.");
        }

        private static async Task ValidateConfigProofAndCrossLanguageTicketAsync()
        {
            using var vector = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "direct-transport-ticket-v1.json"))
                .ConfigureAwait(false));
            var data = vector.RootElement;
            var publicKey = DecodeBase64Url(data.GetProperty("server_public_key_base64url").GetString());
            var ticketText = data.GetProperty("ticket_protobuf_base64url").GetString();
            var ticketWire = DecodeBase64Url(ticketText);
            Require(Convert.ToHexString(SHA256.HashData(ticketWire)).ToLowerInvariant() ==
                    data.GetProperty("ticket_sha256").GetString(),
                "The direct-ticket vector hash changed.");
            var local = Guid.Parse(data.GetProperty("initiator_device_id").GetString());
            var peer = Guid.Parse(data.GetProperty("responder_device_id").GetString());
            var connection = Guid.Parse(data.GetProperty("connection_id").GetString());
            var now = data.GetProperty("issued_at_ms").GetInt64() + 1;
            var configJson = JsonSerializer.Serialize(new
            {
                protocol_version = 1,
                capabilities = new[] { "direct_signaling_v1", "direct_transport_shadow_v1" },
                direct_routing_permitted = false,
                ticket_signing_keys = new[]
                {
                    new
                    {
                        key_id = data.GetProperty("server_signing_key_id").GetString(),
                        algorithm = "Ed25519",
                        public_key = data.GetProperty("server_public_key_base64url").GetString()
                    }
                },
                stun_servers = new[] { "stun:one.test:3478", "stun:[2001:db8::1]:3478" }
            });
            var ticketJson = JsonSerializer.Serialize(new
            {
                protocol_version = 1,
                ticket_protobuf = ticketText
            });
            using var identity = DeviceIdentity.Generate();
            var requests = new ConcurrentQueue<RequestRecord>();
            var handler = new ScriptedHttpHandler(request =>
            {
                requests.Enqueue(request);
                return request.Path switch
                {
                    ProductionDirectTransportSignalingClient.ConfigPath =>
                        JsonResponse(HttpStatusCode.OK, configJson),
                    ProductionDirectTransportSignalingClient.TicketPath =>
                        JsonResponse(HttpStatusCode.Created, ticketJson),
                    _ => throw new InvalidOperationException("Unexpected direct HTTP endpoint.")
                };
            });
            var nonceCounter = 0;
            await using var client = new ProductionDirectTransportSignalingClient(
                "https://direct.example.test:8443",
                local,
                identity,
                _ => Task.FromResult("direct-access-token"),
                enabled: true,
                httpHandler: handler,
                clockMilliseconds: () => now,
                nonceFactory: () => Enumerable.Repeat(
                    checked((byte)Interlocked.Increment(ref nonceCounter)), 16).ToArray());

            var configResult = await client.FetchConfigAsync().ConfigureAwait(false);
            Require(configResult.Status == ProductionDirectSignalingCallStatus.Success &&
                    !configResult.Value.DirectRoutingPermitted &&
                    configResult.Value.Capabilities.SequenceEqual(
                        new[] { "direct_signaling_v1", "direct_transport_shadow_v1" }) &&
                    configResult.Value.StunServers.SequenceEqual(
                        new[] { "stun:one.test:3478", "stun:[2001:db8::1]:3478" }) &&
                    configResult.Value.TicketSigningKeys.Count == 1,
                "The authenticated direct config was rejected or changed.");
            var ticketResult = await client.IssueTicketAsync(
                peer, connection, configResult.Value).ConfigureAwait(false);
            Require(ticketResult.Status == ProductionDirectSignalingCallStatus.Success &&
                    ticketResult.Value.WireBytes.SequenceEqual(ticketWire) &&
                    ticketResult.Value.Ticket.PathEpoch == 7,
                "The canonical cross-language direct-ticket vector was rejected.");

            var captured = requests.ToArray();
            Require(captured.Length == 2 &&
                    captured.All(request => request.Uri.Scheme == Uri.UriSchemeHttps &&
                        request.Uri.Host == "direct.example.test" && request.Uri.Port == 8443) &&
                    captured[0].Method == "GET" && captured[0].Body.Length == 0 &&
                    captured[1].Method == "POST",
                "Direct HTTP escaped its pinned server origin or used the wrong method.");
            foreach (var request in captured)
                VerifyProof(request, identity.SigningPublicKey, "TERMLINK-DEVICE-PROOF-V1");
            using (var body = JsonDocument.Parse(captured[1].Body))
            {
                var root = body.RootElement;
                Require(root.GetProperty("target_device_id").GetGuid() == peer &&
                        root.GetProperty("connection_id").GetGuid() == connection &&
                        root.GetProperty("requested_carriers")[0].GetString() == "UDP_QUIC",
                    "Direct ticket request authority changed.");
            }

            var tampered = (byte[])ticketWire.Clone();
            tampered[^1] ^= 0x01;
            var rejectHandler = new ScriptedHttpHandler(_ => JsonResponse(
                HttpStatusCode.Created,
                JsonSerializer.Serialize(new
                {
                    protocol_version = 1,
                    ticket_protobuf = Base64Url(tampered)
                })));
            await using var rejectClient = new ProductionDirectTransportSignalingClient(
                "https://direct.example.test",
                local,
                identity,
                _ => Task.FromResult("token"),
                enabled: true,
                httpHandler: rejectHandler,
                clockMilliseconds: () => now);
            var rejected = await rejectClient.IssueTicketAsync(
                peer, connection, configResult.Value).ConfigureAwait(false);
            Require(rejected.Status == ProductionDirectSignalingCallStatus.Fault &&
                    rejected.Fault == ProductionDirectSignalingFaultCode.TicketRejected,
                "A modified direct-ticket vector retained server authority.");
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(ticketWire);
            CryptographicOperations.ZeroMemory(tampered);
        }

        private static async Task ValidateIndependentSignalHandshakeAndFramesAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var connection = NewVersion4Guid();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var identity = DeviceIdentity.Generate();
            var socket = new ScriptedSignalSocket();
            socket.EnqueueText(
                "{\"type\":\"SERVER_WELCOME\",\"protocol_version\":1," +
                "\"capabilities\":[\"direct_transport_shadow_v1\"]," +
                "\"heartbeat_interval_ms\":15000,\"max_signal_frame_bytes\":65536," +
                "\"future_optional_field\":true}");
            var factory = new ScriptedSocketFactory(() => socket);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var nonceCounter = 0;
            await using var client = new ProductionDirectTransportSignalingClient(
                "https://signal.example.test",
                local,
                identity,
                _ => Task.FromResult("signal-token"),
                enabled: true,
                httpHandler: new ScriptedHttpHandler(_ =>
                    throw new InvalidOperationException("Signals test unexpectedly used HTTP.")),
                socketFactory: factory,
                clockMilliseconds: () => now,
                nonceFactory: () => Enumerable.Repeat(
                    checked((byte)Interlocked.Increment(ref nonceCounter)), 16).ToArray());
            client.StateChanged += state =>
            {
                if (state.State == ProductionDirectSignalingLaneState.Connected)
                    connected.TrySetResult(true);
            };
            Require(client.Start(), "The independent signal lane did not start.");
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(socket.Endpoint == new Uri("wss://signal.example.test/v1/transport/signals") &&
                    socket.Subprotocol == ProductionDirectTransportSignalingClient.SignalSubprotocol &&
                    socket.Sent.Any(frame => frame.Type == WebSocketMessageType.Text &&
                        Encoding.UTF8.GetString(frame.Payload).Contains("\"role\":\"pc\"", StringComparison.Ordinal) &&
                        Encoding.UTF8.GetString(frame.Payload).Contains(
                            "\"capabilities\":[\"direct_transport_v1\"]", StringComparison.Ordinal)),
                "The WSS direct signal handshake is invalid.");
            VerifyProof(
                new RequestRecord(
                    "GET",
                    new Uri("https://signal.example.test/v1/transport/signals"),
                    Array.Empty<byte>(),
                    socket.Headers),
                identity.SigningPublicKey,
                "TERMLINK-TRANSPORT-SIGNAL-PROOF-V1");

            var outgoing = Signal(connection, local, peer, now, sequence: 1);
            Require(client.TrySend(outgoing), "A valid outgoing direct signal was rejected.");
            await WaitUntilAsync(
                () => socket.Sent.Any(frame => frame.Type == WebSocketMessageType.Binary),
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var binary = socket.Sent.Single(frame => frame.Type == WebSocketMessageType.Binary);
            Require(binary.Payload.SequenceEqual(outgoing.ToByteArray()),
                "The independent signal writer changed an opaque E2EE frame.");

            var incoming = Signal(connection, peer, local, now, sequence: 1);
            socket.EnqueueBinary(incoming.ToByteArray());
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            TransportSignalEnvelopeV1 received = null;
            await foreach (var value in client.ReadSignalsAsync(readTimeout.Token).ConfigureAwait(false))
            {
                received = value;
                break;
            }
            Require(received != null && received.ToByteArray().SequenceEqual(incoming.ToByteArray()) &&
                    client.Snapshot.State == ProductionDirectSignalingLaneState.Connected,
                "A valid incoming direct signal was not independently delivered.");
            socket.EnqueueBinary(incoming.ToByteArray());
            await WaitUntilAsync(
                () => client.Snapshot.Fault == ProductionDirectSignalingFaultCode.SignalReplayed,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(client.Snapshot.State == ProductionDirectSignalingLaneState.Fault &&
                    !client.Snapshot.WillRetry,
                "An incoming direct signal replay did not fail closed inside its own lane.");
        }

        private static async Task ValidateMultipairNegotiationAndSignalGateAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var connection = NewVersion4Guid();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var identity = DeviceIdentity.Generate();
            var socket = new ScriptedSignalSocket();
            socket.EnqueueText(
                "{\"type\":\"SERVER_WELCOME\",\"protocol_version\":1," +
                "\"capabilities\":[\"direct_transport_shadow_v1\"," +
                "\"direct_transport_shadow_multipair_v2\"]," +
                "\"heartbeat_interval_ms\":15000,\"max_signal_frame_bytes\":65536}");
            await using var client = new ProductionDirectTransportSignalingClient(
                "https://multipair-signal.example.test", local, identity,
                _ => Task.FromResult("token"), enabled: true,
                httpHandler: new ScriptedHttpHandler(_ => throw new InvalidOperationException()),
                socketFactory: new ScriptedSocketFactory(() => socket),
                clockMilliseconds: () => now,
                multipairV2Enabled: true);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.StateChanged += state =>
            {
                if (state.State == ProductionDirectSignalingLaneState.Connected) connected.TrySetResult(true);
            };
            Require(client.Start(), "Multipair signalling did not start.");
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var hello = socket.Sent.Single(frame => frame.Type == WebSocketMessageType.Text);
            var helloText = Encoding.UTF8.GetString(hello.Payload);
            Require(helloText.Contains(
                    "\"capabilities\":[\"direct_transport_v1\",\"direct_transport_shadow_multipair_v2\"]",
                    StringComparison.Ordinal),
                "ABI v4 usability did not produce the negotiated multipair Hello capability.");
            var signal = Signal(connection, local, peer, now, 1);
            signal.SignalType = TransportSignalTypeV1.ConnectivityCheckBatchV2;
            Require(client.TrySend(signal), "A negotiated type-11 signal was rejected.");

            await using var v1 = new ProductionDirectTransportSignalingClient(
                "https://v1-signal.example.test", local, identity,
                _ => Task.FromResult("token"), enabled: true,
                httpHandler: new ScriptedHttpHandler(_ => throw new InvalidOperationException()),
                socketFactory: new ScriptedSocketFactory(() => new ScriptedSignalSocket()),
                clockMilliseconds: () => now,
                multipairV2Enabled: false);
            var v2Signal = Signal(connection, local, peer, now, 1);
            v2Signal.SignalType = TransportSignalTypeV1.NominationV2;
            Require(!v1.TrySend(v2Signal) &&
                    v1.Snapshot.Fault == ProductionDirectSignalingFaultCode.SignalFrameRejected,
                "An unnegotiated type-12 signal crossed the v1 signalling boundary.");
        }

        private static async Task ValidateStrictConfigAndHttpFaultsAsync()
        {
            using var identity = DeviceIdentity.Generate();
            var local = Guid.NewGuid();
            var signingKey = Base64Url(RandomNumberGenerator.GetBytes(32));
            var invalidConfigs = new[]
            {
                (
                    Name: "legacy direct_transport_v1 capability",
                    Json: ConfigJson(
                        new[] { "direct_signaling_v1", "direct_transport_v1" },
                        directRoutingPermitted: false,
                        signingKey)),
                (
                    Name: "missing direct_transport_shadow_v1 capability",
                    Json: ConfigJson(
                        new[] { "direct_signaling_v1" },
                        directRoutingPermitted: false,
                        signingKey)),
                (
                    Name: "unknown extra capability",
                    Json: ConfigJson(
                        new[]
                        {
                            "direct_signaling_v1",
                            "direct_transport_shadow_v1",
                            "direct_future_v1"
                        },
                        directRoutingPermitted: false,
                        signingKey)),
                (
                    Name: "direct routing permission",
                    Json: ConfigJson(
                        new[] { "direct_signaling_v1", "direct_transport_shadow_v1" },
                        directRoutingPermitted: true,
                        signingKey)),
                (
                    Name: "unknown JSON field",
                    Json: ConfigJson(
                        new[] { "direct_signaling_v1", "direct_transport_shadow_v1" },
                        directRoutingPermitted: false,
                        signingKey,
                        includeUnknownField: true))
            };
            foreach (var invalidConfigCase in invalidConfigs)
            {
                await using var invalidCapabilityClient = new ProductionDirectTransportSignalingClient(
                    "https://config.example.test",
                    local,
                    identity,
                    _ => Task.FromResult("token"),
                    enabled: true,
                    httpHandler: new ScriptedHttpHandler(_ =>
                        JsonResponse(HttpStatusCode.OK, invalidConfigCase.Json)));
                var invalidCapabilityResult = await invalidCapabilityClient.FetchConfigAsync()
                    .ConfigureAwait(false);
                Require(invalidCapabilityResult.Status == ProductionDirectSignalingCallStatus.Fault &&
                        invalidCapabilityResult.Fault ==
                            ProductionDirectSignalingFaultCode.InvalidConfiguration,
                    "Direct config accepted " + invalidConfigCase.Name + ".");
            }

            var invalidConfig =
                "{\"protocol_version\":1," +
                "\"capabilities\":[\"direct_signaling_v1\",\"direct_transport_shadow_v1\"]," +
                "\"direct_routing_permitted\":false," +
                "\"ticket_signing_keys\":[{\"key_id\":\"direct-v1\"," +
                "\"algorithm\":\"Ed25519\",\"public_key\":\"" + signingKey + "\"}]," +
                "\"stun_servers\":[\"https://peer-controlled.invalid\"]}";
            await using (var invalidClient = new ProductionDirectTransportSignalingClient(
                "https://config.example.test",
                local,
                identity,
                _ => Task.FromResult("token"),
                enabled: true,
                httpHandler: new ScriptedHttpHandler(_ => JsonResponse(HttpStatusCode.OK, invalidConfig))))
            {
                var result = await invalidClient.FetchConfigAsync().ConfigureAwait(false);
                Require(result.Status == ProductionDirectSignalingCallStatus.Fault &&
                        result.Fault == ProductionDirectSignalingFaultCode.InvalidConfiguration,
                    "A peer-controlled non-STUN endpoint entered the server allowlist.");
            }

            await using var unauthorizedClient = new ProductionDirectTransportSignalingClient(
                "https://config.example.test",
                local,
                identity,
                _ => Task.FromResult("token"),
                enabled: true,
                httpHandler: new ScriptedHttpHandler(_ =>
                    new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            var unauthorized = await unauthorizedClient.FetchConfigAsync().ConfigureAwait(false);
            Require(unauthorized.Status == ProductionDirectSignalingCallStatus.Fault &&
                    unauthorized.Fault == ProductionDirectSignalingFaultCode.AuthenticationRejected &&
                    unauthorized.HttpStatus == 401,
                "A direct HTTP authentication rejection was not contained as a direct fault.");
        }

        private static async Task ValidateBoundedSignalQueuesAsync()
        {
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var connection = NewVersion4Guid();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var identity = DeviceIdentity.Generate();
            await using (var frameClient = IdleClient(local, identity, now))
            {
                for (var index = 1; index <= ProductionDirectTransportSignalingClient.MaximumQueuedSignalFrames; index++)
                    Require(frameClient.TrySend(Signal(connection, local, peer, now, (ulong)index)),
                        "The bounded direct queue rejected an in-budget frame.");
                Require(!frameClient.TrySend(Signal(
                            connection,
                            local,
                            peer,
                            now,
                            (ulong)ProductionDirectTransportSignalingClient.MaximumQueuedSignalFrames + 1)) &&
                        frameClient.Snapshot.Fault == ProductionDirectSignalingFaultCode.SignalQueueOverflow,
                    "The direct signal frame-count budget was not enforced.");
            }

            await using (var byteClient = IdleClient(local, identity, now))
            {
                var accepted = 0;
                for (var index = 1; index <= 16; index++)
                {
                    var large = Signal(connection, local, peer, now, (ulong)index, ciphertextBytes: 64_000);
                    if (!byteClient.TrySend(large)) break;
                    accepted++;
                }
                Require(accepted > 1 && accepted < 16 &&
                        byteClient.Snapshot.Fault == ProductionDirectSignalingFaultCode.SignalQueueOverflow,
                    "The 512 KiB direct signal byte budget was not enforced.");
            }

            await using (var replayClient = IdleClient(local, identity, now))
            {
                var first = Signal(connection, local, peer, now, sequence: 1);
                Require(replayClient.TrySend(first) && !replayClient.TrySend(first.Clone()) &&
                        replayClient.Snapshot.Fault == ProductionDirectSignalingFaultCode.SignalReplayed,
                    "An outgoing direct signal sequence/nonce replay was accepted.");
            }

            await using (var authorityClient = IdleClient(local, identity, now))
            {
                var staleEpoch = Signal(connection, local, peer, now, sequence: 1);
                staleEpoch.PathEpoch = 0;
                var invalidNonce = Signal(connection, local, peer, now, sequence: 2);
                invalidNonce.Nonce = ByteString.CopyFrom(new byte[11]);
                var invalidSequence = Signal(connection, local, peer, now, sequence: 0);
                var oversized = Signal(
                    connection,
                    local,
                    peer,
                    now,
                    sequence: 3,
                    ciphertextBytes: ProductionDirectTransportSignalingClient.MaximumSignalFrameBytes);
                Require(!authorityClient.TrySend(staleEpoch) &&
                        !authorityClient.TrySend(invalidNonce) &&
                        !authorityClient.TrySend(invalidSequence) &&
                        !authorityClient.TrySend(oversized) &&
                        authorityClient.Snapshot.Fault ==
                            ProductionDirectSignalingFaultCode.SignalFrameRejected,
                    "Direct signal epoch, sequence, nonce or size bounds were not fail closed.");
            }
        }

        private static async Task ValidateIndependentFullJitterRetryAsync()
        {
            var first = new ScriptedSignalSocket
            {
                ConnectError = new WebSocketException("independent direct socket fixture failure")
            };
            var second = new ScriptedSignalSocket();
            second.EnqueueText(
                "{\"type\":\"SERVER_WELCOME\",\"protocol_version\":1," +
                "\"capabilities\":[\"direct_transport_shadow_v1\"]," +
                "\"heartbeat_interval_ms\":15000,\"max_signal_frame_bytes\":65536}");
            var sockets = new Queue<ScriptedSignalSocket>(new[] { first, second });
            var factory = new ScriptedSocketFactory(() => sockets.Dequeue());
            var delays = new ConcurrentQueue<TimeSpan>();
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var identity = DeviceIdentity.Generate();
            await using var client = new ProductionDirectTransportSignalingClient(
                "https://retry.example.test",
                Guid.NewGuid(),
                identity,
                _ => Task.FromResult("retry-token"),
                enabled: true,
                httpHandler: new ScriptedHttpHandler(_ =>
                    throw new InvalidOperationException("Retry fixture unexpectedly used HTTP.")),
                socketFactory: factory,
                retryJitter: failure => ProductionDirectTransportSignalingClient.RetryBase(failure),
                retryDelay: (delay, _) =>
                {
                    delays.Enqueue(delay);
                    return Task.CompletedTask;
                });
            client.StateChanged += state =>
            {
                if (state.State == ProductionDirectSignalingLaneState.Connected)
                    connected.TrySetResult(true);
            };
            client.Start();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(factory.CreateCount == 2 && delays.TryPeek(out var delay) &&
                    delay == ProductionDirectTransportSignalingClient.RetryFloor &&
                    second.Headers["X-TermLink-Nonce"] != first.Headers["X-TermLink-Nonce"],
                "Direct signalling did not use an independent retry generation and nonce.");
        }

        private static void ValidateRetryBoundsAndServerOrigin()
        {
            Require(ProductionDirectTransportSignalingClient.RetryBase(1) == TimeSpan.FromMilliseconds(250) &&
                    ProductionDirectTransportSignalingClient.RetryBase(2) == TimeSpan.FromMilliseconds(500) &&
                    ProductionDirectTransportSignalingClient.RetryBase(100) == TimeSpan.FromSeconds(30),
                "Direct signalling retry bounds changed.");
            using var identity = DeviceIdentity.Generate();
            RequireThrows<ArgumentException>(() => new ProductionDirectTransportSignalingClient(
                    "https://direct.example.test/prefix",
                    Guid.NewGuid(),
                    identity,
                    _ => Task.FromResult("token")),
                "A path-bearing direct server root escaped the exact endpoint allowlist.");
            RequireThrows<ArgumentException>(() => new ProductionDirectTransportSignalingClient(
                    "http://direct.example.test",
                    Guid.NewGuid(),
                    identity,
                    _ => Task.FromResult("token")),
                "A cleartext production direct server was accepted.");
        }

        private static ProductionDirectTransportSignalingClient IdleClient(
            Guid local,
            DeviceIdentity identity,
            long now) =>
            new(
                "https://idle.example.test",
                local,
                identity,
                _ => Task.FromResult("idle-token"),
                enabled: true,
                httpHandler: new ScriptedHttpHandler(_ =>
                    throw new InvalidOperationException("Idle queue fixture performed HTTP.")),
                socketFactory: new ScriptedSocketFactory(() =>
                    throw new InvalidOperationException("Idle queue fixture created a socket.")),
                clockMilliseconds: () => now);

        private static TransportSignalEnvelopeV1 Signal(
            Guid connection,
            Guid sender,
            Guid recipient,
            long now,
            ulong sequence,
            int ciphertextBytes = 16) =>
            new()
            {
                ProtocolVersion = 1,
                SignalType = TransportSignalTypeV1.PathOffer,
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(sender)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(recipient)),
                PathEpoch = 1,
                SignalSeq = sequence,
                SignalAck = 0,
                SentAtMs = now,
                KeyId = ByteString.CopyFromUtf8("direct-signal-v1"),
                Nonce = ByteString.CopyFrom(NonceFor(sequence)),
                Ciphertext = ByteString.CopyFrom(Enumerable.Repeat((byte)0x5a, ciphertextBytes).ToArray())
            };

        private static byte[] NonceFor(ulong sequence)
        {
            var nonce = new byte[12];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), sequence);
            return nonce;
        }

        private static void VerifyProof(
            RequestRecord request,
            byte[] publicKey,
            string expectedDomain)
        {
            request.Headers.TryGetValue("Authorization", out var authorization);
            request.Headers.TryGetValue("X-TermLink-Timestamp", out var timestampText);
            request.Headers.TryGetValue("X-TermLink-Nonce", out var nonce);
            request.Headers.TryGetValue("X-TermLink-Signature", out var signatureText);
            var timestampValid = long.TryParse(timestampText, out var timestamp);
            Require(authorization != null &&
                    authorization.StartsWith("Bearer ", StringComparison.Ordinal) &&
                    request.Headers.TryGetValue("X-TermLink-Device", out _) &&
                    timestampValid && nonce != null && signatureText != null,
                "Direct proof headers are incomplete.");
            var canonical = ProductionDirectTransportSignalingClient.BuildCanonicalProof(
                expectedDomain,
                request.Method,
                request.Uri.AbsolutePath,
                timestamp,
                nonce,
                request.Body);
            var signature = DecodeBase64Url(signatureText);
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(canonical, 0, canonical.Length);
            Require(verifier.VerifySignature(signature),
                "Direct proof did not verify in its required independent domain.");
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(signature);
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
            new(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        private static string ConfigJson(
            string[] capabilities,
            bool directRoutingPermitted,
            string signingKey,
            bool includeUnknownField = false)
        {
            var config = new Dictionary<string, object>
            {
                ["protocol_version"] = 1,
                ["capabilities"] = capabilities,
                ["direct_routing_permitted"] = directRoutingPermitted,
                ["ticket_signing_keys"] = new[]
                {
                    new
                    {
                        key_id = "direct-v1",
                        algorithm = "Ed25519",
                        public_key = signingKey
                    }
                },
                ["stun_servers"] = new[] { "stun:one.test:3478" }
            };
            if (includeUnknownField) config["future_field"] = true;
            return JsonSerializer.Serialize(config);
        }

        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

        private static Guid NewVersion4Guid()
        {
            while (true)
            {
                var value = Guid.NewGuid();
                if ((GuidBytes(value)[6] >> 4) == 4) return value;
            }
        }

        private static string Base64Url(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] DecodeBase64Url(string value)
        {
            var standard = value.Replace('-', '+').Replace('_', '/');
            standard += (standard.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException("Invalid fixture base64url.")
            };
            return Convert.FromBase64String(standard);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate())
            {
                if (watch.Elapsed >= timeout) throw new TimeoutException("Validation wait timed out.");
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        private static void RequireThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class ScriptedHttpHandler : HttpMessageHandler
        {
            private readonly Func<RequestRecord, HttpResponseMessage> responder;
            private int requestCount;

            public ScriptedHttpHandler(Func<RequestRecord, HttpResponseMessage> responder)
            {
                this.responder = responder;
            }

            public int RequestCount => Volatile.Read(ref requestCount);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref requestCount);
                var body = request.Content == null
                    ? Array.Empty<byte>()
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var headers = request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase);
                return responder(new RequestRecord(
                    request.Method.Method,
                    request.RequestUri,
                    body,
                    headers));
            }
        }

        private sealed class RequestRecord
        {
            public RequestRecord(
                string method,
                Uri uri,
                byte[] body,
                IReadOnlyDictionary<string, string> headers)
            {
                Method = method;
                Uri = uri;
                Body = body;
                Headers = headers;
            }

            public string Method { get; }
            public Uri Uri { get; }
            public string Path => Uri.AbsolutePath;
            public byte[] Body { get; }
            public IReadOnlyDictionary<string, string> Headers { get; }
        }

        private sealed class ScriptedSocketFactory : IProductionDirectSignalSocketFactory
        {
            private readonly Func<IProductionDirectSignalSocket> create;
            private int createCount;

            public ScriptedSocketFactory(Func<IProductionDirectSignalSocket> create)
            {
                this.create = create;
            }

            public int CreateCount => Volatile.Read(ref createCount);

            public IProductionDirectSignalSocket Create()
            {
                Interlocked.Increment(ref createCount);
                return create();
            }
        }

        private sealed class ScriptedSignalSocket : IProductionDirectSignalSocket
        {
            private readonly Channel<ProductionDirectSignalSocketMessage> incoming =
                Channel.CreateUnbounded<ProductionDirectSignalSocketMessage>();
            private readonly CancellationTokenSource aborted = new();

            public Exception ConnectError { get; init; }
            public Uri Endpoint { get; private set; }
            public string Subprotocol { get; private set; }
            public IReadOnlyDictionary<string, string> Headers { get; private set; } =
                new Dictionary<string, string>();
            public ConcurrentQueue<SentFrame> Sent { get; } = new();

            public Task ConnectAsync(
                Uri endpoint,
                string subprotocol,
                IReadOnlyDictionary<string, string> headers,
                CancellationToken cancellationToken)
            {
                Endpoint = endpoint;
                Subprotocol = subprotocol;
                Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                return ConnectError == null ? Task.CompletedTask : Task.FromException(ConnectError);
            }

            public Task SendAsync(
                ReadOnlyMemory<byte> payload,
                WebSocketMessageType messageType,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Sent.Enqueue(new SentFrame(messageType, payload.ToArray()));
                return Task.CompletedTask;
            }

            public async Task<ProductionDirectSignalSocketMessage> ReceiveAsync(
                int maximumBytes,
                CancellationToken cancellationToken)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, aborted.Token);
                var message = await incoming.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                if (message.Payload.Length > maximumBytes)
                    throw new InvalidDataException("Scripted signal exceeds client maximum.");
                return message;
            }

            public void EnqueueText(string value) => incoming.Writer.TryWrite(
                new ProductionDirectSignalSocketMessage(
                    WebSocketMessageType.Text,
                    Encoding.UTF8.GetBytes(value)));

            public void EnqueueBinary(byte[] value) => incoming.Writer.TryWrite(
                new ProductionDirectSignalSocketMessage(WebSocketMessageType.Binary, value));

            public void Abort()
            {
                if (!aborted.IsCancellationRequested) aborted.Cancel();
            }

            public ValueTask DisposeAsync()
            {
                Abort();
                aborted.Dispose();
                incoming.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class SentFrame
        {
            public SentFrame(WebSocketMessageType type, byte[] payload)
            {
                Type = type;
                Payload = payload;
            }

            public WebSocketMessageType Type { get; }
            public byte[] Payload { get; }
        }
    }
}
