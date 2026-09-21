using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    internal enum ProductionDirectSignalingFaultCode
    {
        None,
        FeatureDisabled,
        InvalidConfiguration,
        AuthenticationRejected,
        HttpUnavailable,
        ResponseTooLarge,
        ResponseInvalid,
        TicketRejected,
        SignalConnectFailed,
        SignalProtocolRejected,
        SignalFrameRejected,
        SignalReplayed,
        SignalQueueOverflow,
        Closed
    }

    internal enum ProductionDirectSignalingLaneState
    {
        Disabled,
        Idle,
        Connecting,
        Connected,
        Fault,
        Closed
    }

    internal sealed class ProductionDirectSignalingSnapshot
    {
        public ProductionDirectSignalingLaneState State { get; init; }
        public ProductionDirectSignalingFaultCode Fault { get; init; }
        public int? HttpStatus { get; init; }
        public int Attempt { get; init; }
        public long Generation { get; init; }
        public bool WillRetry { get; init; }
        public bool DirectDataV1Negotiated { get; init; }
        public bool DirectSessionCutoverV1Negotiated { get; init; }
        public bool DirectDataPlaneV1Negotiated =>
            DirectDataV1Negotiated && DirectSessionCutoverV1Negotiated;
    }

    internal enum ProductionDirectSignalingCallStatus
    {
        Success,
        Disabled,
        Fault
    }

    internal sealed class ProductionDirectSignalingCallResult<T>
    {
        private ProductionDirectSignalingCallResult(
            ProductionDirectSignalingCallStatus status,
            T value,
            ProductionDirectSignalingFaultCode fault,
            int? httpStatus)
        {
            Status = status;
            Value = value;
            Fault = fault;
            HttpStatus = httpStatus;
        }

        public ProductionDirectSignalingCallStatus Status { get; }
        public T Value { get; }
        public ProductionDirectSignalingFaultCode Fault { get; }
        public int? HttpStatus { get; }

        public static ProductionDirectSignalingCallResult<T> Success(T value) =>
            new(ProductionDirectSignalingCallStatus.Success, value,
                ProductionDirectSignalingFaultCode.None, null);

        public static ProductionDirectSignalingCallResult<T> Disabled() =>
            new(ProductionDirectSignalingCallStatus.Disabled, default,
                ProductionDirectSignalingFaultCode.FeatureDisabled, null);

        public static ProductionDirectSignalingCallResult<T> Failed(
            ProductionDirectSignalingFaultCode fault,
            int? httpStatus = null) =>
            new(ProductionDirectSignalingCallStatus.Fault, default, fault, httpStatus);
    }

    internal sealed class ProductionDirectTicketSigningKey
    {
        public ProductionDirectTicketSigningKey(string keyId, byte[] publicKey)
        {
            if (!ProductionDirectTransportSignalingClient.ValidKeyId(keyId))
                throw new ArgumentException("Direct ticket signing key ID is invalid.", nameof(keyId));
            if (publicKey == null || publicKey.Length != 32)
                throw new ArgumentException("Direct ticket signing key must be Ed25519.", nameof(publicKey));
            KeyId = keyId;
            PublicKey = (byte[])publicKey.Clone();
        }

        public string KeyId { get; }
        public byte[] PublicKey { get; }
    }

    internal sealed class ProductionDirectTransportServerConfig
    {
        public ProductionDirectTransportServerConfig(
            int protocolVersion,
            IReadOnlyCollection<string> capabilities,
            IReadOnlyDictionary<string, ProductionDirectTicketSigningKey> ticketSigningKeys,
            IReadOnlyList<string> stunServers,
            bool directRoutingPermitted = false)
        {
            ProtocolVersion = protocolVersion;
            Capabilities = capabilities;
            TicketSigningKeys = ticketSigningKeys;
            StunServers = stunServers;
            DirectRoutingPermitted = directRoutingPermitted;
        }

        public int ProtocolVersion { get; }
        public IReadOnlyCollection<string> Capabilities { get; }
        public IReadOnlyDictionary<string, ProductionDirectTicketSigningKey> TicketSigningKeys { get; }
        public IReadOnlyList<string> StunServers { get; }
        public bool DirectRoutingPermitted { get; }
    }

    internal sealed class ProductionValidatedDirectPathTicket
    {
        public ProductionValidatedDirectPathTicket(DirectPathTicketV1 ticket, byte[] wireBytes)
        {
            Ticket = ticket;
            WireBytes = (byte[])wireBytes.Clone();
        }

        public DirectPathTicketV1 Ticket { get; }
        public byte[] WireBytes { get; }
    }

    internal sealed class ProductionDirectSignalSocketMessage
    {
        public ProductionDirectSignalSocketMessage(
            WebSocketMessageType messageType,
            byte[] payload,
            WebSocketCloseStatus? closeStatus = null)
        {
            MessageType = messageType;
            Payload = payload ?? Array.Empty<byte>();
            CloseStatus = closeStatus;
        }

        public WebSocketMessageType MessageType { get; }
        public byte[] Payload { get; }
        public WebSocketCloseStatus? CloseStatus { get; }
    }

    internal interface IProductionDirectSignalSocket : IAsyncDisposable
    {
        Task ConnectAsync(
            Uri endpoint,
            string subprotocol,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken);

        Task SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken);

        Task<ProductionDirectSignalSocketMessage> ReceiveAsync(
            int maximumBytes,
            CancellationToken cancellationToken);

        void Abort();
    }

    internal interface IProductionDirectSignalSocketFactory
    {
        IProductionDirectSignalSocket Create();
    }

    internal sealed class ProductionClientWebSocketFactory : IProductionDirectSignalSocketFactory
    {
        public IProductionDirectSignalSocket Create() => new ProductionClientWebSocket();
    }

    internal sealed class ProductionClientWebSocket : IProductionDirectSignalSocket
    {
        private readonly ClientWebSocket socket = new();

        public async Task ConnectAsync(
            Uri endpoint,
            string subprotocol,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            socket.Options.AddSubProtocol(subprotocol);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            ConfigureKeepAliveTimeout(socket.Options);
            foreach (var header in headers)
                socket.Options.SetRequestHeader(header.Key, header.Value);
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(socket.SubProtocol, subprotocol, StringComparison.Ordinal))
                throw new ProductionDirectSignalingLaneException(
                    ProductionDirectSignalingFaultCode.SignalProtocolRejected,
                    retryable: false);
        }

        public Task SendAsync(
            ReadOnlyMemory<byte> payload,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken) =>
            socket.SendAsync(payload, messageType, true, cancellationToken).AsTask();

        public async Task<ProductionDirectSignalSocketMessage> ReceiveAsync(
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(8 * 1024, maximumBytes));
            try
            {
                using var output = new MemoryStream(Math.Min(maximumBytes, 8 * 1024));
                WebSocketMessageType? expectedType = null;
                while (true)
                {
                    var result = await socket.ReceiveAsync(
                        buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes)),
                        cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return new ProductionDirectSignalSocketMessage(
                            WebSocketMessageType.Close,
                            Array.Empty<byte>(),
                            socket.CloseStatus);
                    if (expectedType.HasValue && expectedType.Value != result.MessageType)
                        throw new ProductionDirectSignalingLaneException(
                            ProductionDirectSignalingFaultCode.SignalProtocolRejected,
                            retryable: false);
                    expectedType ??= result.MessageType;
                    if (output.Length + result.Count > maximumBytes)
                        throw new ProductionDirectSignalingLaneException(
                            ProductionDirectSignalingFaultCode.SignalFrameRejected,
                            retryable: false);
                    output.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                        return new ProductionDirectSignalSocketMessage(
                            result.MessageType,
                            output.ToArray());
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Abort() => socket.Abort();

        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }

        private static void ConfigureKeepAliveTimeout(ClientWebSocketOptions options)
        {
            var property = typeof(ClientWebSocketOptions).GetProperty("KeepAliveTimeout");
            if (property?.CanWrite == true)
                property.SetValue(options, TimeSpan.FromSeconds(10), null);
        }
    }

    /// <summary>
    /// Optional direct-transport control plane. It has its own socket, queues,
    /// proof domain, nonce domain, cancellation and retry state. It intentionally
    /// has no reference to realtime Relay, SessionHost, renderer or durable ACKs.
    /// </summary>
    internal sealed class ProductionDirectTransportSignalingClient :
        IProductionDirectShadowSignaling,
        IAsyncDisposable
    {
        internal const string ConfigPath = "/v1/transport/config";
        internal const string TicketPath = "/v1/transport/tickets";
        internal const string RenewPath = "/v1/transport/tickets/renew";
        internal const string SignalPath = "/v1/transport/signals";
        internal const string SignalSubprotocol = "termlink.transport.v1";
        internal const string DirectCapability = "direct_transport_v1";
        internal const string DirectSignalingCapability = "direct_signaling_v1";
        internal const string DirectShadowCapability = "direct_transport_shadow_v1";
        internal const string DirectMultipairShadowCapability =
            "direct_transport_shadow_multipair_v2";
        internal const string DirectDataCapability = "direct_transport_data_v1";
        internal const string DirectSessionCutoverCapability = "direct_session_cutover_v1";
        internal const int MaximumSignalFrameBytes = 64 * 1024;
        internal const int MaximumQueuedSignalFrames = 32;
        internal const long MaximumQueuedSignalBytes = 512 * 1024;
        internal static readonly TimeSpan RetryFloor = TimeSpan.FromMilliseconds(250);
        internal static readonly TimeSpan RetryCeiling = TimeSpan.FromSeconds(30);

        private const string DeviceProofDomain = "TERMLINK-DEVICE-PROOF-V1";
        private const string SignalProofDomain = "TERMLINK-TRANSPORT-SIGNAL-PROOF-V1";
        private const int ProtocolVersion = 1;
        private const int IdentifierBytes = 16;
        private const int SignalNonceBytes = 12;
        private const int MinimumCiphertextBytes = 16;
        private const int MaximumSignalCiphertextBytes = MaximumSignalFrameBytes - 128;
        private const int MaximumKeyIdBytes = 64;
        private const long MaximumSignalClockSkewMilliseconds = 30_000;
        private const long MaximumTicketClockSkewMilliseconds = 30_000;
        private const long MaximumTicketLifetimeMilliseconds = 120_000;
        private const int MinimumTicketBytes = 128;
        private const int MaximumTicketBytes = 2_048;
        private const int MaximumConfigResponseBytes = 16 * 1024;
        private const int MaximumTicketRequestBytes = 4 * 1024;
        private const int MaximumTicketResponseBytes = 8 * 1024;
        private const int MaximumHelloBytes = 4 * 1024;
        private const int MaximumConfigCapabilities = 16;
        private const int MaximumTicketSigningKeys = 4;
        private const int MaximumStunServers = 2;
        private const int MaximumStunTextCharacters = 320;
        private const int MaximumBase64TextCharacters = 4_096;
        private const int MaximumSignalCursors = 64;
        private const int MaximumSignalNonces = 512;
        private const int HttpTimeoutMilliseconds = 10_000;
        private const int SignalHandshakeTimeoutMilliseconds = 10_000;

        private static readonly Regex CapabilityPattern = new(
            "^[a-z][a-z0-9._-]{0,63}$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        private static readonly Regex KeyIdPattern = new(
            "^[A-Za-z0-9._:-]{1,64}$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly Uri root;
        private readonly Uri signalEndpoint;
        private readonly Guid deviceId;
        private readonly DeviceIdentity identity;
        private readonly Func<CancellationToken, Task<string>> accessTokenProvider;
        private readonly bool enabled;
        private readonly bool multipairV2Enabled;
        private readonly bool directDataV1Enabled;
        private readonly HttpClient http;
        private readonly IProductionDirectSignalSocketFactory socketFactory;
        private readonly Func<long> clockMilliseconds;
        private readonly Func<int, TimeSpan> retryJitter;
        private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
        private readonly Func<byte[]> nonceFactory;
        private readonly CancellationTokenSource lifetime = new();
        private readonly object sync = new();
        private readonly Channel<QueuedSignal> outgoing;
        private readonly Channel<QueuedSignal> incoming;
        private readonly BoundedCursorMap incomingCursors = new(MaximumSignalCursors);
        private readonly BoundedCursorMap outgoingCursors = new(MaximumSignalCursors);
        private readonly BoundedNonceSet incomingNonces = new(MaximumSignalNonces);
        private readonly BoundedNonceSet outgoingNonces = new(MaximumSignalNonces);
        private Task connectionTask;
        private IProductionDirectSignalSocket activeSocket;
        private long outgoingBytes;
        private long incomingBytes;
        private long generation;
        private int disposed;
        private ProductionDirectSignalingSnapshot snapshot;

        public ProductionDirectTransportSignalingClient(
            string serverRoot,
            Guid deviceId,
            DeviceIdentity identity,
            Func<CancellationToken, Task<string>> accessTokenProvider,
            bool enabled = false,
            HttpMessageHandler httpHandler = null,
            IProductionDirectSignalSocketFactory socketFactory = null,
            Func<long> clockMilliseconds = null,
            Func<int, TimeSpan> retryJitter = null,
            Func<TimeSpan, CancellationToken, Task> retryDelay = null,
            Func<byte[]> nonceFactory = null,
            bool allowInsecureForTests = false,
            bool multipairV2Enabled = false,
            bool directDataV1Enabled = false)
        {
            root = NormalizeRoot(serverRoot, allowInsecureForTests);
            signalEndpoint = BuildEndpoint(root, SignalPath, websocket: true);
            if (deviceId == Guid.Empty)
                throw new ArgumentException("A direct signalling device ID is required.", nameof(deviceId));
            this.deviceId = deviceId;
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.accessTokenProvider = accessTokenProvider ??
                throw new ArgumentNullException(nameof(accessTokenProvider));
            this.enabled = enabled;
            this.multipairV2Enabled = multipairV2Enabled;
            this.directDataV1Enabled = directDataV1Enabled;
            this.socketFactory = socketFactory ?? new ProductionClientWebSocketFactory();
            this.clockMilliseconds = clockMilliseconds ??
                (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            this.retryJitter = retryJitter ?? FullJitter;
            this.retryDelay = retryDelay ?? Task.Delay;
            this.nonceFactory = nonceFactory ?? (() => RandomNumberGenerator.GetBytes(16));
            var handler = httpHandler ?? new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromMilliseconds(HttpTimeoutMilliseconds),
                MaxConnectionsPerServer = 4,
                UseCookies = false
            };
            http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMilliseconds(HttpTimeoutMilliseconds)
            };
            outgoing = Channel.CreateBounded<QueuedSignal>(new BoundedChannelOptions(MaximumQueuedSignalFrames)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            incoming = Channel.CreateBounded<QueuedSignal>(new BoundedChannelOptions(MaximumQueuedSignalFrames)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            snapshot = new ProductionDirectSignalingSnapshot
            {
                State = enabled
                    ? ProductionDirectSignalingLaneState.Idle
                    : ProductionDirectSignalingLaneState.Disabled,
                Fault = enabled
                    ? ProductionDirectSignalingFaultCode.None
                    : ProductionDirectSignalingFaultCode.FeatureDisabled
            };
        }

        public event Action<ProductionDirectSignalingSnapshot> StateChanged;

        public ProductionDirectSignalingSnapshot Snapshot
        {
            get { lock (sync) return snapshot; }
        }

        public async Task<ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>>
            FetchConfigAsync(CancellationToken cancellationToken = default)
        {
            if (!enabled)
                return ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Disabled();
            if (Volatile.Read(ref disposed) != 0)
                return ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Failed(
                    ProductionDirectSignalingFaultCode.Closed);
            try
            {
                using var response = await SendHttpAsync(
                    HttpMethod.Get, ConfigPath, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
                var failure = HttpFailure<ProductionDirectTransportServerConfig>(response.StatusCode);
                if (failure != null) return failure;
                var raw = await ReadBoundedResponseAsync(
                    response, MaximumConfigResponseBytes, cancellationToken).ConfigureAwait(false);
                if (raw == null)
                    return ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Failed(
                        ProductionDirectSignalingFaultCode.ResponseTooLarge);
                try
                {
                    return ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Success(
                        ParseConfig(raw));
                }
                catch (Exception error) when (
                    error is JsonException or InvalidDataException or FormatException or ArgumentException or OverflowException)
                {
                    return ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Failed(
                        ProductionDirectSignalingFaultCode.InvalidConfiguration);
                }
                finally { CryptographicOperations.ZeroMemory(raw); }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>.Failed(
                    Volatile.Read(ref disposed) != 0
                        ? ProductionDirectSignalingFaultCode.Closed
                        : ProductionDirectSignalingFaultCode.HttpUnavailable);
            }
        }

        public async Task<ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>>
            IssueTicketAsync(
                Guid targetDeviceId,
                Guid connectionId,
                ProductionDirectTransportServerConfig config,
                CancellationToken cancellationToken = default)
        {
            if (!enabled)
                return ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Disabled();
            if (Volatile.Read(ref disposed) != 0)
                return ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Failed(
                    ProductionDirectSignalingFaultCode.Closed);
            if (targetDeviceId == Guid.Empty || targetDeviceId == deviceId ||
                !IsVersion4(connectionId) || config == null ||
                config.ProtocolVersion != ProtocolVersion ||
                !HasNegotiatedCapabilities(config))
                return ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Failed(
                    ProductionDirectSignalingFaultCode.TicketRejected);
            var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["target_device_id"] = targetDeviceId.ToString("D").ToLowerInvariant(),
                ["connection_id"] = connectionId.ToString("D").ToLowerInvariant(),
                ["requested_carriers"] = new[] { "UDP_QUIC" }
            });
            if (body.Length > MaximumTicketRequestBytes)
            {
                CryptographicOperations.ZeroMemory(body);
                return ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Failed(
                    ProductionDirectSignalingFaultCode.TicketRejected);
            }
            try
            {
                using var response = await SendHttpAsync(
                    HttpMethod.Post, TicketPath, body, cancellationToken).ConfigureAwait(false);
                var failure = HttpFailure<ProductionValidatedDirectPathTicket>(response.StatusCode);
                if (failure != null) return failure;
                var raw = await ReadBoundedResponseAsync(
                    response, MaximumTicketResponseBytes, cancellationToken).ConfigureAwait(false);
                if (raw == null)
                    return ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Failed(
                        ProductionDirectSignalingFaultCode.ResponseTooLarge);
                try
                {
                    return ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Success(
                        ParseAndValidateTicket(raw, targetDeviceId, connectionId, config));
                }
                catch (Exception error) when (
                    error is JsonException or InvalidDataException or FormatException or ArgumentException or
                    OverflowException or CryptographicException or InvalidProtocolBufferException)
                {
                    return ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Failed(
                        ProductionDirectSignalingFaultCode.TicketRejected);
                }
                finally { CryptographicOperations.ZeroMemory(raw); }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>.Failed(
                    Volatile.Read(ref disposed) != 0
                        ? ProductionDirectSignalingFaultCode.Closed
                        : ProductionDirectSignalingFaultCode.HttpUnavailable);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }

        /// <summary>
        /// Renews a direct-transport ticket by presenting the current ticket
        /// as proof to the server's renewal endpoint (audit #2 step S3).
        /// </summary>
        public async Task<ProductionDirectSignalingCallResult<byte[]>>
            RenewTicketAsync(
                Guid targetDeviceId,
                Guid connectionId,
                byte[] currentTicket,
                ProductionDirectTransportServerConfig config,
                CancellationToken cancellationToken = default)
        {
            if (!enabled)
                return ProductionDirectSignalingCallResult<byte[]>.Disabled();
            if (Volatile.Read(ref disposed) != 0)
                return ProductionDirectSignalingCallResult<byte[]>.Failed(
                    ProductionDirectSignalingFaultCode.Closed);
            if (targetDeviceId == Guid.Empty || targetDeviceId == deviceId ||
                currentTicket == null || currentTicket.Length < MinimumTicketBytes ||
                currentTicket.Length > MaximumTicketBytes || config == null)
                return ProductionDirectSignalingCallResult<byte[]>.Failed(
                    ProductionDirectSignalingFaultCode.TicketRejected);
            var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["target_device_id"] = targetDeviceId.ToString("D").ToLowerInvariant(),
                ["connection_id"] = connectionId.ToString("D").ToLowerInvariant(),
                ["ticket_protobuf"] = Convert.ToBase64String(currentTicket)
            });
            try
            {
                using var response = await SendHttpAsync(
                    HttpMethod.Post, RenewPath, body, cancellationToken).ConfigureAwait(false);
                var failure = HttpFailure<byte[]>(response.StatusCode);
                if (failure != null) return failure;
                var raw = await ReadBoundedResponseAsync(
                    response, MaximumTicketResponseBytes, cancellationToken).ConfigureAwait(false);
                if (raw == null)
                    return ProductionDirectSignalingCallResult<byte[]>.Failed(
                        ProductionDirectSignalingFaultCode.ResponseTooLarge);
                try
                {
                    using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 2
                    });
                    RequireObjectKeys(document.RootElement, "ticket_protobuf");
                    var renewed = DecodeBase64UrlStrict(
                        ReadExactString(document.RootElement, "ticket_protobuf"));
                    return ProductionDirectSignalingCallResult<byte[]>.Success(renewed);
                }
                catch (Exception error) when (
                    error is JsonException or InvalidDataException or FormatException or ArgumentException)
                {
                    return ProductionDirectSignalingCallResult<byte[]>.Failed(
                        ProductionDirectSignalingFaultCode.TicketRejected);
                }
            }
            catch (Exception error) when (
                error is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                return ProductionDirectSignalingCallResult<byte[]>.Failed(
                    ProductionDirectSignalingFaultCode.HttpUnavailable);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }

        public bool Start()
        {
            if (!enabled)
            {
                Publish(ProductionDirectSignalingLaneState.Disabled,
                    ProductionDirectSignalingFaultCode.FeatureDisabled);
                return false;
            }
            if (Volatile.Read(ref disposed) != 0) return false;
            lock (sync)
            {
                if (connectionTask != null) return false;
                connectionTask = RunConnectionLoopAsync(lifetime.Token);
                return true;
            }
        }

        public bool TrySend(TransportSignalEnvelopeV1 envelope)
        {
            if (!enabled || Volatile.Read(ref disposed) != 0 || envelope == null) return false;
            byte[] wire;
            try
            {
                wire = envelope.ToByteArray();
                ValidateSignal(envelope, wire, outbound: true);
            }
            catch (Exception error) when (
                error is InvalidDataException or InvalidProtocolBufferException or OverflowException)
            {
                PublishFault(ProductionDirectSignalingFaultCode.SignalFrameRejected, willRetry: false);
                return false;
            }
            lock (sync)
            {
                var requiredDirectDataGeneration = 0L;
                if (IsDirectDataControlSignal(envelope.SignalType))
                {
                    if (!HasCurrentGenerationDirectDataLocked())
                    {
                        CryptographicOperations.ZeroMemory(wire);
                        PublishFault(ProductionDirectSignalingFaultCode.SignalFrameRejected, willRetry: false);
                        return false;
                    }
                    requiredDirectDataGeneration = snapshot.Generation;
                }
                var cursor = SignalCursor.From(envelope);
                var nonce = SignalNonce.From(envelope);
                if (outgoingCursors.IsReplay(cursor, envelope.SignalSeq) || outgoingNonces.Contains(nonce))
                {
                    CryptographicOperations.ZeroMemory(wire);
                    PublishFault(ProductionDirectSignalingFaultCode.SignalReplayed, willRetry: false);
                    return false;
                }
                var reserved = TryReserve(ref outgoingBytes, wire.Length, MaximumQueuedSignalBytes);
                if (!reserved || !outgoing.Writer.TryWrite(new QueuedSignal(
                    envelope,
                    wire,
                    requiredDirectDataGeneration)))
                {
                    if (reserved) Interlocked.Add(ref outgoingBytes, -wire.Length);
                    CryptographicOperations.ZeroMemory(wire);
                    PublishFault(ProductionDirectSignalingFaultCode.SignalQueueOverflow, willRetry: false);
                    return false;
                }
                outgoingCursors.Remember(cursor, envelope.SignalSeq);
                outgoingNonces.Remember(nonce);
                return true;
            }
        }

        public async IAsyncEnumerable<TransportSignalEnvelopeV1> ReadSignalsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (await incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (incoming.Reader.TryRead(out var item))
                {
                    Interlocked.Add(ref incomingBytes, -item.Wire.Length);
                    var deliver = item.RequiredDirectDataGeneration == 0 ||
                        HasCurrentGenerationDirectData(item.RequiredDirectDataGeneration);
                    CryptographicOperations.ZeroMemory(item.Wire);
                    if (deliver) yield return item.Envelope;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            IProductionDirectSignalSocket active;
            Task running;
            lock (sync)
            {
                active = activeSocket;
                activeSocket = null;
                running = connectionTask;
            }
            try { active?.Abort(); }
            catch (ObjectDisposedException) { }
            outgoing.Writer.TryComplete();
            incoming.Writer.TryComplete();
            if (running != null)
            {
                try { await running.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            DrainAndZero(outgoing.Reader, ref outgoingBytes);
            DrainAndZero(incoming.Reader, ref incomingBytes);
            http.Dispose();
            lifetime.Dispose();
            Publish(ProductionDirectSignalingLaneState.Closed,
                ProductionDirectSignalingFaultCode.Closed);
        }

        internal static byte[] BuildCanonicalProof(
            string domain,
            string method,
            string requestTarget,
            long timestampMilliseconds,
            string nonce,
            ReadOnlySpan<byte> body)
        {
            var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            return StrictUtf8.GetBytes(string.Join("\n",
                domain,
                method.ToUpperInvariant(),
                requestTarget,
                timestampMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                nonce,
                hash));
        }

        internal static bool ValidKeyId(string value) =>
            value != null && KeyIdPattern.IsMatch(value);

        internal static TimeSpan RetryBase(int failureCount)
        {
            if (failureCount < 1) failureCount = 1;
            var milliseconds = RetryFloor.TotalMilliseconds;
            for (var index = 1; index < failureCount && milliseconds < RetryCeiling.TotalMilliseconds; index++)
                milliseconds = Math.Min(milliseconds * 2, RetryCeiling.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                var consecutiveFailures = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var nextGeneration = Interlocked.Increment(ref generation);
                    var attempt = consecutiveFailures + 1;
                    Publish(ProductionDirectSignalingLaneState.Connecting,
                        ProductionDirectSignalingFaultCode.None, attempt, nextGeneration);
                    var wasWelcomed = false;
                    try
                    {
                        await RunSocketGenerationAsync(
                            nextGeneration,
                            cancellationToken,
                            () => wasWelcomed = true).ConfigureAwait(false);
                        wasWelcomed = true;
                        consecutiveFailures = 0;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        var laneError = error as ProductionDirectSignalingLaneException;
                        var status = FindHttpStatus(error);
                        var authentication = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                        var terminalClientError = status.HasValue &&
                            (int)status.Value is >= 400 and <= 499 &&
                            status.Value != HttpStatusCode.RequestTimeout &&
                            (int)status.Value != 429;
                        var code = authentication
                            ? ProductionDirectSignalingFaultCode.AuthenticationRejected
                            : terminalClientError
                                ? ProductionDirectSignalingFaultCode.SignalProtocolRejected
                                : laneError?.Fault ??
                                    ProductionDirectSignalingFaultCode.SignalConnectFailed;
                        var retryable = !authentication && !terminalClientError &&
                            (laneError?.Retryable ?? IsExpectedNetworkFailure(error));
                        if (!retryable)
                        {
                            Publish(ProductionDirectSignalingLaneState.Fault, code,
                                attempt, nextGeneration, willRetry: false,
                                httpStatus: status.HasValue ? (int)status.Value : null);
                            return;
                        }
                        consecutiveFailures = wasWelcomed ? 1 : consecutiveFailures + 1;
                        Publish(ProductionDirectSignalingLaneState.Fault, code,
                            consecutiveFailures, nextGeneration, willRetry: true,
                            httpStatus: status.HasValue ? (int)status.Value : null);
                        var delay = retryJitter(consecutiveFailures);
                        var maximum = RetryBase(consecutiveFailures);
                        if (delay < TimeSpan.Zero || delay > maximum)
                            delay = maximum;
                        await retryDelay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally { incoming.Writer.TryComplete(); }
        }

        private async Task RunSocketGenerationAsync(
            long generationValue,
            CancellationToken cancellationToken,
            Action welcomed)
        {
            await using var socket = socketFactory.Create();
            lock (sync) activeSocket = socket;
            try
            {
                var headers = await CreateProofHeadersAsync(
                    SignalProofDomain,
                    HttpMethod.Get,
                    SignalPath,
                    Array.Empty<byte>(),
                    cancellationToken).ConfigureAwait(false);
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectTimeout.CancelAfter(HttpTimeoutMilliseconds);
                    await socket.ConnectAsync(
                        signalEndpoint,
                        SignalSubprotocol,
                        headers,
                        connectTimeout.Token).ConfigureAwait(false);
                }
                var helloCapabilities = new List<string>
                {
                    DirectCapability
                };
                if (multipairV2Enabled)
                    helloCapabilities.Add(DirectMultipairShadowCapability);
                if (directDataV1Enabled)
                {
                    helloCapabilities.Add(DirectDataCapability);
                    helloCapabilities.Add(DirectSessionCutoverCapability);
                }
                var hello = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "CLIENT_HELLO",
                    role = "pc",
                    protocol_versions = new[] { ProtocolVersion },
                    capabilities = helloCapabilities
                });
                if (hello.Length > MaximumHelloBytes)
                    throw new ProductionDirectSignalingLaneException(
                        ProductionDirectSignalingFaultCode.SignalProtocolRejected,
                        retryable: false);
                try
                {
                    await socket.SendAsync(hello, WebSocketMessageType.Text, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(hello); }
                ProductionDirectSignalSocketMessage welcome;
                using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    handshakeTimeout.CancelAfter(SignalHandshakeTimeoutMilliseconds);
                    welcome = await socket.ReceiveAsync(MaximumHelloBytes, handshakeTimeout.Token)
                        .ConfigureAwait(false);
                }
                var directDataV1Negotiated = false;
                var directSessionCutoverV1Negotiated = false;
                try
                {
                    if (welcome.MessageType != WebSocketMessageType.Text ||
                        !ValidateWelcome(
                            welcome.Payload,
                            out directDataV1Negotiated,
                            out directSessionCutoverV1Negotiated))
                        throw new ProductionDirectSignalingLaneException(
                            ProductionDirectSignalingFaultCode.SignalProtocolRejected,
                            retryable: false);
                }
                finally { CryptographicOperations.ZeroMemory(welcome.Payload); }
                welcomed();
                Publish(ProductionDirectSignalingLaneState.Connected,
                    ProductionDirectSignalingFaultCode.None, 0, generationValue,
                    directDataV1Negotiated: directDataV1Negotiated,
                    directSessionCutoverV1Negotiated: directSessionCutoverV1Negotiated);
                var directDataPlaneV1Negotiated =
                    directDataV1Negotiated && directSessionCutoverV1Negotiated;
                using var lane = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receive = ReceiveLoopAsync(
                    socket, generationValue, directDataPlaneV1Negotiated, lane.Token);
                var send = SendLoopAsync(
                    socket, generationValue, directDataPlaneV1Negotiated, lane.Token);
                var completed = await Task.WhenAny(receive, send).ConfigureAwait(false);
                try { await completed.ConfigureAwait(false); }
                finally
                {
                    lane.Cancel();
                    await ObserveLaneShutdownAsync(receive).ConfigureAwait(false);
                    await ObserveLaneShutdownAsync(send).ConfigureAwait(false);
                }
                throw new ProductionDirectSignalingLaneException(
                    ProductionDirectSignalingFaultCode.SignalConnectFailed,
                    retryable: true);
            }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(activeSocket, socket)) activeSocket = null;
                }
            }
        }

        private async Task SendLoopAsync(
            IProductionDirectSignalSocket socket,
            long generationValue,
            bool directDataV1Negotiated,
            CancellationToken cancellationToken)
        {
            while (await outgoing.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (outgoing.Reader.TryRead(out var item))
                {
                    Interlocked.Add(ref outgoingBytes, -item.Wire.Length);
                    if (item.RequiredDirectDataGeneration != 0 &&
                        (item.RequiredDirectDataGeneration != generationValue ||
                            !directDataV1Negotiated))
                    {
                        CryptographicOperations.ZeroMemory(item.Wire);
                        continue;
                    }
                    try
                    {
                        await socket.SendAsync(
                            item.Wire,
                            WebSocketMessageType.Binary,
                            cancellationToken).ConfigureAwait(false);
                    }
                    finally { CryptographicOperations.ZeroMemory(item.Wire); }
                }
            }
        }

        private async Task ReceiveLoopAsync(
            IProductionDirectSignalSocket socket,
            long generationValue,
            bool directDataV1Negotiated,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await socket.ReceiveAsync(
                    MaximumSignalFrameBytes, cancellationToken).ConfigureAwait(false);
                if (message.MessageType == WebSocketMessageType.Close)
                    throw new ProductionDirectSignalingLaneException(
                        ProductionDirectSignalingFaultCode.SignalConnectFailed,
                        retryable: message.CloseStatus is not WebSocketCloseStatus.ProtocolError and
                            not WebSocketCloseStatus.PolicyViolation and not WebSocketCloseStatus.MessageTooBig);
                if (message.MessageType != WebSocketMessageType.Binary || message.Payload.Length == 0)
                {
                    CryptographicOperations.ZeroMemory(message.Payload);
                    throw new ProductionDirectSignalingLaneException(
                        ProductionDirectSignalingFaultCode.SignalProtocolRejected,
                        retryable: false);
                }
                TransportSignalEnvelopeV1 envelope;
                try
                {
                    envelope = TransportSignalEnvelopeV1.Parser.ParseFrom(message.Payload);
                    ValidateSignal(envelope, message.Payload, outbound: false);
                    if (IsDirectDataControlSignal(envelope.SignalType) &&
                        !directDataV1Negotiated)
                        throw new InvalidDataException(
                            "Direct-data control was not negotiated for this generation.");
                }
                catch (Exception error) when (
                    error is InvalidDataException or InvalidProtocolBufferException or OverflowException)
                {
                    CryptographicOperations.ZeroMemory(message.Payload);
                    throw new ProductionDirectSignalingLaneException(
                        ProductionDirectSignalingFaultCode.SignalFrameRejected,
                        retryable: false,
                        error);
                }
                lock (sync)
                {
                    var cursor = SignalCursor.From(envelope);
                    var nonce = SignalNonce.From(envelope);
                    if (incomingCursors.IsReplay(cursor, envelope.SignalSeq) || incomingNonces.Contains(nonce))
                    {
                        CryptographicOperations.ZeroMemory(message.Payload);
                        throw new ProductionDirectSignalingLaneException(
                            ProductionDirectSignalingFaultCode.SignalReplayed,
                            retryable: false);
                    }
                    var reserved = TryReserve(
                        ref incomingBytes,
                        message.Payload.Length,
                        MaximumQueuedSignalBytes);
                    var requiredDirectDataGeneration = IsDirectDataControlSignal(envelope.SignalType)
                        ? generationValue
                        : 0;
                    if (!reserved ||
                        !incoming.Writer.TryWrite(new QueuedSignal(
                            envelope,
                            message.Payload,
                            requiredDirectDataGeneration)))
                    {
                        if (reserved) Interlocked.Add(ref incomingBytes, -message.Payload.Length);
                        CryptographicOperations.ZeroMemory(message.Payload);
                        throw new ProductionDirectSignalingLaneException(
                            ProductionDirectSignalingFaultCode.SignalQueueOverflow,
                            retryable: false);
                    }
                    incomingCursors.Remember(cursor, envelope.SignalSeq);
                    incomingNonces.Remember(nonce);
                }
            }
        }

        private async Task<HttpResponseMessage> SendHttpAsync(
            HttpMethod method,
            string path,
            byte[] body,
            CancellationToken cancellationToken)
        {
            var headers = await CreateProofHeadersAsync(
                DeviceProofDomain, method, path, body, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, BuildEndpoint(root, path, websocket: false));
            foreach (var header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (method == HttpMethod.Post)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8"
                };
            }
            return await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<IReadOnlyDictionary<string, string>> CreateProofHeadersAsync(
            string domain,
            HttpMethod method,
            string path,
            byte[] body,
            CancellationToken cancellationToken)
        {
            var accessToken = await accessTokenProvider(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16 * 1024 ||
                accessToken.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new InvalidDataException("Direct signalling access token is invalid.");
            var timestamp = clockMilliseconds();
            if (timestamp <= 0) throw new InvalidDataException("Direct signalling clock is invalid.");
            var nonceBytes = nonceFactory();
            if (nonceBytes == null || nonceBytes.Length != 16)
                throw new InvalidDataException("Direct signalling nonce is invalid.");
            var nonce = Base64Url(nonceBytes);
            var canonical = BuildCanonicalProof(
                domain, method.Method, path, timestamp, nonce, body);
            byte[] signature = null;
            try
            {
                signature = identity.Sign(canonical);
                return new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = "Bearer " + accessToken,
                    ["X-TermLink-Device"] = deviceId.ToString("D").ToLowerInvariant(),
                    ["X-TermLink-Timestamp"] = timestamp.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["X-TermLink-Nonce"] = nonce,
                    ["X-TermLink-Signature"] = Base64Url(signature)
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonceBytes);
                CryptographicOperations.ZeroMemory(canonical);
                if (signature != null) CryptographicOperations.ZeroMemory(signature);
            }
        }

        private ProductionDirectTransportServerConfig ParseConfig(byte[] raw)
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var rootElement = document.RootElement;
            RequireObjectKeys(rootElement,
                "protocol_version", "capabilities", "direct_routing_permitted",
                "ticket_signing_keys", "stun_servers");
            if (ReadExactInt32(rootElement, "protocol_version") != ProtocolVersion)
                throw new InvalidDataException("Direct config protocol is unsupported.");
            var capabilities = ReadUniqueStrings(
                rootElement, "capabilities", 1, MaximumConfigCapabilities,
                value => CapabilityPattern.IsMatch(value));
            var expectedCapabilityCount = multipairV2Enabled ? 3 : 2;
            var hasDirectDataCapability = capabilities.Contains(
                DirectDataCapability, StringComparer.Ordinal);
            var hasSessionCutoverCapability = capabilities.Contains(
                DirectSessionCutoverCapability, StringComparer.Ordinal);
            if (hasSessionCutoverCapability && !hasDirectDataCapability ||
                capabilities.Count != expectedCapabilityCount +
                    (hasDirectDataCapability ? 1 : 0) +
                    (hasSessionCutoverCapability ? 1 : 0) ||
                !capabilities.Contains(DirectSignalingCapability, StringComparer.Ordinal) ||
                !capabilities.Contains(DirectShadowCapability, StringComparer.Ordinal) ||
                (multipairV2Enabled != capabilities.Contains(
                    DirectMultipairShadowCapability, StringComparer.Ordinal)))
                throw new InvalidDataException("Direct shadow config capabilities are invalid.");
            var directRoutingPermitted = ReadExactBoolean(
                rootElement, "direct_routing_permitted");
            if (directRoutingPermitted != hasDirectDataCapability)
                throw new InvalidDataException("Direct routing capability and permission are inconsistent.");
            var keysElement = rootElement.GetProperty("ticket_signing_keys");
            if (keysElement.ValueKind != JsonValueKind.Array ||
                keysElement.GetArrayLength() is < 1 or > MaximumTicketSigningKeys)
                throw new InvalidDataException("Direct config ticket keys are invalid.");
            var keys = new Dictionary<string, ProductionDirectTicketSigningKey>(StringComparer.Ordinal);
            foreach (var element in keysElement.EnumerateArray())
            {
                RequireObjectKeys(element, "key_id", "algorithm", "public_key");
                var keyId = ReadExactString(element, "key_id");
                if (ReadExactString(element, "algorithm") != "Ed25519")
                    throw new InvalidDataException("Direct ticket algorithm is unsupported.");
                var publicKey = DecodeBase64UrlStrict(ReadExactString(element, "public_key"));
                var key = new ProductionDirectTicketSigningKey(keyId, publicKey);
                CryptographicOperations.ZeroMemory(publicKey);
                if (!keys.TryAdd(keyId, key))
                    throw new InvalidDataException("Direct ticket key ID is duplicated.");
            }
            var stunServers = ReadUniqueStrings(
                rootElement, "stun_servers", 0, MaximumStunServers, ValidStunServer);
            return new ProductionDirectTransportServerConfig(
                ProtocolVersion,
                Array.AsReadOnly(capabilities.ToArray()),
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, ProductionDirectTicketSigningKey>(keys),
                Array.AsReadOnly(stunServers.ToArray()),
                directRoutingPermitted);
        }

        private ProductionValidatedDirectPathTicket ParseAndValidateTicket(
            byte[] raw,
            Guid targetDeviceId,
            Guid connectionId,
            ProductionDirectTransportServerConfig config)
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            var response = document.RootElement;
            RequireObjectKeys(response, "protocol_version", "ticket_protobuf");
            if (ReadExactInt32(response, "protocol_version") != ProtocolVersion)
                throw new InvalidDataException("Direct ticket response protocol is invalid.");
            var wire = DecodeBase64UrlStrict(ReadExactString(response, "ticket_protobuf"));
            try
            {
                return ValidateTicketWire(
                    wire,
                    deviceId,
                    targetDeviceId,
                    connectionId,
                    config);
            }
            finally { CryptographicOperations.ZeroMemory(wire); }
        }

        public ProductionValidatedDirectPathTicket ValidateIncomingTicket(
            DirectPathTicketV1 ticket,
            Guid initiatorDeviceId,
            Guid connectionId,
            ProductionDirectTransportServerConfig config)
        {
            if (ticket == null || initiatorDeviceId == Guid.Empty ||
                initiatorDeviceId == deviceId || !IsVersion4(connectionId) ||
                !HasNegotiatedCapabilities(config))
                throw new InvalidDataException("Direct incoming ticket context is invalid.");
            var wire = ticket.ToByteArray();
            try
            {
                return ValidateTicketWire(
                    wire,
                    initiatorDeviceId,
                    deviceId,
                    connectionId,
                    config);
            }
            finally { CryptographicOperations.ZeroMemory(wire); }
        }

        private ProductionValidatedDirectPathTicket ValidateTicketWire(
            byte[] wire,
            Guid initiatorDeviceId,
            Guid responderDeviceId,
            Guid connectionId,
            ProductionDirectTransportServerConfig config)
        {
            if (wire == null || wire.Length is < MinimumTicketBytes or > MaximumTicketBytes)
                throw new InvalidDataException("Direct ticket wire size is invalid.");
            var ticket = DirectPathTicketV1.Parser.ParseFrom(wire);
            var canonical = CanonicalTicket(ticket).ToByteArray();
            try
            {
                if (!wire.AsSpan().SequenceEqual(canonical))
                    throw new InvalidDataException("Direct ticket protobuf is non-canonical.");
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }
            if (ticket.FormatVersion != ProtocolVersion ||
                !NonZeroIdentifier(ticket.TicketId) || !NonZeroIdentifier(ticket.AccountId) ||
                !ticket.InitiatorDeviceId.Span.SequenceEqual(GuidBytes(initiatorDeviceId)) ||
                !ticket.ResponderDeviceId.Span.SequenceEqual(GuidBytes(responderDeviceId)) ||
                !ticket.ConnectionId.Span.SequenceEqual(GuidBytes(connectionId)) ||
                ticket.PathEpoch == 0 || ticket.PathEpoch > long.MaxValue ||
                ticket.ServerNonce.Length != IdentifierBytes ||
                ticket.ServerSignature.Length != 64 ||
                ticket.AllowedCarriers.Count != 1 ||
                ticket.AllowedCarriers[0] != DirectCarrierV1.UdpQuic)
                throw new InvalidDataException("Direct ticket authority is invalid.");
            var keyId = StrictUtf8.GetString(ticket.ServerSigningKeyId.Span);
            if (!ValidKeyId(keyId) || !config.TicketSigningKeys.TryGetValue(keyId, out var signingKey))
                throw new InvalidDataException("Direct ticket signing key is not allowlisted.");
            var now = clockMilliseconds();
            if (ticket.IssuedAtMs <= 0 || ticket.ExpiresAtMs <= ticket.IssuedAtMs ||
                ticket.ExpiresAtMs - ticket.IssuedAtMs > MaximumTicketLifetimeMilliseconds ||
                ticket.IssuedAtMs > SaturatingAdd(now, MaximumTicketClockSkewMilliseconds) ||
                now >= ticket.ExpiresAtMs)
                throw new InvalidDataException("Direct ticket lifetime is invalid.");
            ProductionDirectTransportCrypto.ValidateTicket(
                ticket,
                signingKey.PublicKey,
                StrictUtf8.GetBytes(keyId),
                initiatorDeviceId,
                responderDeviceId,
                DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime);
            return new ProductionValidatedDirectPathTicket(ticket, wire);
        }

        private void ValidateSignal(
            TransportSignalEnvelopeV1 envelope,
            byte[] wire,
            bool outbound)
        {
            if (wire.Length is < 1 or > MaximumSignalFrameBytes)
                throw new InvalidDataException("Direct signal wire size is invalid.");
            var canonical = CanonicalSignal(envelope).ToByteArray();
            try
            {
                if (!wire.AsSpan().SequenceEqual(canonical))
                    throw new InvalidDataException("Direct signal protobuf is non-canonical.");
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }
            var own = GuidBytes(deviceId);
            var signalType = (int)envelope.SignalType;
            var authorityMatches = outbound
                ? envelope.SenderDeviceId.Span.SequenceEqual(own) &&
                    !envelope.RecipientDeviceId.Span.SequenceEqual(own)
                : envelope.RecipientDeviceId.Span.SequenceEqual(own) &&
                    !envelope.SenderDeviceId.Span.SequenceEqual(own);
            if (envelope.ProtocolVersion != ProtocolVersion ||
                signalType < 1 ||
                signalType > (multipairV2Enabled ? 14 : 10) ||
                !NonZeroIdentifier(envelope.ConnectionId) ||
                envelope.SenderDeviceId.Length != IdentifierBytes ||
                envelope.RecipientDeviceId.Length != IdentifierBytes ||
                !authorityMatches ||
                envelope.PathEpoch == 0 || envelope.PathEpoch > long.MaxValue ||
                envelope.SignalSeq == 0 || envelope.SignalSeq > long.MaxValue ||
                envelope.SignalAck > long.MaxValue ||
                envelope.SentAtMs <= 0 ||
                !WithinClockSkew(clockMilliseconds(), envelope.SentAtMs) ||
                envelope.KeyId.Length is < 1 or > MaximumKeyIdBytes ||
                envelope.Nonce.Length != SignalNonceBytes ||
                envelope.Ciphertext.Length is < MinimumCiphertextBytes or > MaximumSignalCiphertextBytes)
                throw new InvalidDataException("Direct signal authority is invalid.");
        }

        internal static DirectPathTicketV1 CanonicalTicket(DirectPathTicketV1 value)
        {
            var result = new DirectPathTicketV1
            {
                FormatVersion = value.FormatVersion,
                TicketId = value.TicketId,
                AccountId = value.AccountId,
                InitiatorDeviceId = value.InitiatorDeviceId,
                ResponderDeviceId = value.ResponderDeviceId,
                ConnectionId = value.ConnectionId,
                PathEpoch = value.PathEpoch,
                IssuedAtMs = value.IssuedAtMs,
                ExpiresAtMs = value.ExpiresAtMs,
                ServerNonce = value.ServerNonce,
                ServerSigningKeyId = value.ServerSigningKeyId,
                ServerSignature = value.ServerSignature
            };
            result.AllowedCarriers.Add(value.AllowedCarriers);
            return result;
        }

        private static TransportSignalEnvelopeV1 CanonicalSignal(TransportSignalEnvelopeV1 value) =>
            new()
            {
                ProtocolVersion = value.ProtocolVersion,
                SignalType = value.SignalType,
                ConnectionId = value.ConnectionId,
                SenderDeviceId = value.SenderDeviceId,
                RecipientDeviceId = value.RecipientDeviceId,
                PathEpoch = value.PathEpoch,
                SignalSeq = value.SignalSeq,
                SignalAck = value.SignalAck,
                SentAtMs = value.SentAtMs,
                KeyId = value.KeyId,
                Nonce = value.Nonce,
                Ciphertext = value.Ciphertext
            };

        private bool ValidateWelcome(
            byte[] raw,
            out bool directDataV1Negotiated,
            out bool directSessionCutoverV1Negotiated)
        {
            directDataV1Negotiated = false;
            directSessionCutoverV1Negotiated = false;
            try
            {
                using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object ||
                    ReadExactString(value, "type") != "SERVER_WELCOME" ||
                    ReadExactInt32(value, "protocol_version") != ProtocolVersion ||
                    ReadExactInt32(value, "heartbeat_interval_ms") is < 1_000 or > 45_000 ||
                    ReadExactInt32(value, "max_signal_frame_bytes") != MaximumSignalFrameBytes)
                    return false;
                var capabilities = ReadUniqueStrings(
                    value, "capabilities", 1, MaximumConfigCapabilities,
                    capability => CapabilityPattern.IsMatch(capability));
                var hasDirectDataCapability = capabilities.Contains(
                    DirectDataCapability, StringComparer.Ordinal);
                var hasSessionCutoverCapability = capabilities.Contains(
                    DirectSessionCutoverCapability, StringComparer.Ordinal);
                var expectedCount = (multipairV2Enabled ? 2 : 1) +
                    (hasDirectDataCapability ? 1 : 0) +
                    (hasSessionCutoverCapability ? 1 : 0);
                var valid = !hasSessionCutoverCapability || hasDirectDataCapability;
                valid = valid && capabilities.Count == expectedCount &&
                    capabilities.Contains(DirectShadowCapability, StringComparer.Ordinal) &&
                    (multipairV2Enabled == capabilities.Contains(
                        DirectMultipairShadowCapability, StringComparer.Ordinal)) &&
                    (!hasDirectDataCapability || directDataV1Enabled);
                directDataV1Negotiated = valid && hasDirectDataCapability;
                directSessionCutoverV1Negotiated = valid && hasSessionCutoverCapability;
                return valid;
            }
            catch (Exception error) when (
                error is JsonException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return false;
            }
        }

        private bool HasCurrentGenerationDirectData(long requiredGeneration = 0)
        {
            lock (sync)
            {
                return HasCurrentGenerationDirectDataLocked() &&
                    (requiredGeneration == 0 || snapshot.Generation == requiredGeneration);
            }
        }

        private bool HasCurrentGenerationDirectDataLocked() =>
            snapshot.State == ProductionDirectSignalingLaneState.Connected &&
            snapshot.Generation == Volatile.Read(ref generation) &&
            snapshot.DirectDataPlaneV1Negotiated;

        private static bool IsDirectDataControlSignal(TransportSignalTypeV1 signalType) =>
            (int)signalType is >= 6 and <= 9;

        private static async Task<byte[]> ReadBoundedResponseAsync(
            HttpResponseMessage response,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            if (response.Content == null) return null;
            if (response.Content.Headers.ContentLength > maximumBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var output = new MemoryStream(Math.Min(maximumBytes, 4 * 1024));
            var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (output.Length + read > maximumBytes) return null;
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static ProductionDirectSignalingCallResult<T> HttpFailure<T>(HttpStatusCode status)
        {
            if ((int)status is >= 200 and <= 299) return null;
            return ProductionDirectSignalingCallResult<T>.Failed(
                status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ProductionDirectSignalingFaultCode.AuthenticationRejected
                    : ProductionDirectSignalingFaultCode.HttpUnavailable,
                (int)status);
        }

        private static Uri NormalizeRoot(string value, bool allowInsecureForTests)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps &&
                    !(allowInsecureForTests && uri.Scheme == Uri.UriSchemeHttp)) ||
                string.IsNullOrWhiteSpace(uri.IdnHost) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0))
                throw new ArgumentException("Direct signalling server root is invalid.", nameof(value));
            var builder = new UriBuilder(uri)
            {
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri;
        }

        private static Uri BuildEndpoint(Uri root, string path, bool websocket)
        {
            var builder = new UriBuilder(root)
            {
                Scheme = websocket
                    ? root.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
                    : root.Scheme,
                Path = path,
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri;
        }

        private static bool ValidStunServer(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaximumStunTextCharacters ||
                !value.StartsWith("stun:", StringComparison.Ordinal) || value.Any(char.IsWhiteSpace))
                return false;
            var authority = value.Substring(5);
            string host;
            string portText;
            if (authority.StartsWith("[", StringComparison.Ordinal))
            {
                var closing = authority.IndexOf(']');
                if (closing <= 1 || closing + 2 >= authority.Length || authority[closing + 1] != ':')
                    return false;
                host = authority.Substring(1, closing - 1);
                portText = authority.Substring(closing + 2);
                if (host.Contains('%') || host != host.ToLowerInvariant() ||
                    !IPAddress.TryParse(host, out var address) ||
                    address.AddressFamily != AddressFamily.InterNetworkV6 ||
                    address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast)
                    return false;
            }
            else
            {
                var separator = authority.LastIndexOf(':');
                if (separator <= 0 || authority.Substring(0, separator).Contains(':')) return false;
                host = authority.Substring(0, separator);
                portText = authority.Substring(separator + 1);
                if (host != host.ToLowerInvariant() || host.Length > 253) return false;
                if (IPAddress.TryParse(host, out var address))
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        address.Equals(IPAddress.Any) || IsMulticast(address) ||
                        !string.Equals(address.ToString(), host, StringComparison.Ordinal))
                        return false;
                }
                else if (!host.Split('.').All(label =>
                    label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' &&
                    label.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                    return false;
            }
            return int.TryParse(portText,
                       System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var port) &&
                port is >= 1 and <= 65_535 &&
                port.ToString(System.Globalization.CultureInfo.InvariantCulture) == portText;
        }

        private static bool IsMulticast(IPAddress address) =>
            address.AddressFamily == AddressFamily.InterNetwork &&
            address.GetAddressBytes()[0] is >= 224 and <= 239;

        private static IReadOnlyList<string> ReadUniqueStrings(
            JsonElement owner,
            string name,
            int minimum,
            int maximum,
            Func<string, bool> validator)
        {
            if (!owner.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() < minimum || array.GetArrayLength() > maximum)
                throw new InvalidDataException(name + " is invalid.");
            var result = new List<string>(array.GetArrayLength());
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException(name + " is invalid.");
                var value = item.GetString();
                if (value == null || !validator(value) || !unique.Add(value))
                    throw new InvalidDataException(name + " is invalid.");
                result.Add(value);
            }
            return result;
        }

        private static void RequireObjectKeys(JsonElement value, params string[] expected)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A JSON object is required.");
            var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
            if (actual.Length != expected.Length ||
                !new HashSet<string>(actual, StringComparer.Ordinal).SetEquals(expected))
                throw new InvalidDataException("JSON fields are invalid.");
        }

        private static string ReadExactString(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(name + " is invalid.");
            return value.GetString() ?? throw new InvalidDataException(name + " is invalid.");
        }

        private static int ReadExactInt32(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var result))
                throw new InvalidDataException(name + " is invalid.");
            return result;
        }

        private static bool ReadExactBoolean(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out var value) ||
                value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                throw new InvalidDataException(name + " is invalid.");
            return value.GetBoolean();
        }

        internal static bool HasCurrentShadowCapabilities(
            ProductionDirectTransportServerConfig config) =>
            config != null && config.ProtocolVersion == ProtocolVersion &&
            config.Capabilities != null &&
            config.Capabilities.Count is >= 2 and <= 4 &&
            config.Capabilities.Contains(DirectSignalingCapability, StringComparer.Ordinal) &&
            config.Capabilities.Contains(DirectShadowCapability, StringComparer.Ordinal) &&
            (!config.Capabilities.Contains(
                    DirectSessionCutoverCapability, StringComparer.Ordinal) ||
                config.Capabilities.Contains(DirectDataCapability, StringComparer.Ordinal));

        internal static bool HasMultipairShadowCapabilities(
            ProductionDirectTransportServerConfig config) =>
            config != null && config.ProtocolVersion == ProtocolVersion &&
            config.Capabilities != null &&
            config.Capabilities.Count is >= 3 and <= 5 &&
            config.Capabilities.Contains(DirectSignalingCapability, StringComparer.Ordinal) &&
            config.Capabilities.Contains(DirectShadowCapability, StringComparer.Ordinal) &&
            config.Capabilities.Contains(DirectMultipairShadowCapability, StringComparer.Ordinal) &&
            (!config.Capabilities.Contains(
                DirectSessionCutoverCapability, StringComparer.Ordinal) ||
                config.Capabilities.Contains(DirectDataCapability, StringComparer.Ordinal));

        internal static bool HasDirectDataCapabilities(
            ProductionDirectTransportServerConfig config) =>
            config != null && config.ProtocolVersion == ProtocolVersion &&
            config.DirectRoutingPermitted && config.Capabilities != null &&
            config.Capabilities.Contains(DirectDataCapability, StringComparer.Ordinal) &&
            config.Capabilities.Contains(
                DirectSessionCutoverCapability, StringComparer.Ordinal) &&
            (HasCurrentShadowCapabilities(config) || HasMultipairShadowCapabilities(config));

        private bool HasNegotiatedCapabilities(ProductionDirectTransportServerConfig config) =>
            multipairV2Enabled ? HasMultipairShadowCapabilities(config) :
                HasCurrentShadowCapabilities(config);

        private static byte[] DecodeBase64UrlStrict(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaximumBase64TextCharacters ||
                value.Contains('='))
                throw new FormatException("Base64url value is invalid.");
            var standard = value.Replace('-', '+').Replace('_', '/');
            standard += (standard.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException("Base64url value is invalid.")
            };
            var decoded = Convert.FromBase64String(standard);
            if (!string.Equals(Base64Url(decoded), value, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(decoded);
                throw new FormatException("Base64url value is non-canonical.");
            }
            return decoded;
        }

        private static string Base64Url(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static bool NonZeroIdentifier(ByteString value) =>
            value.Length == IdentifierBytes && value.Span.IndexOfAnyExcept((byte)0) >= 0;

        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

        private static bool IsVersion4(Guid value) =>
            value != Guid.Empty && (GuidBytes(value)[6] >> 4) == 4;

        private static bool WithinClockSkew(long now, long sentAt)
        {
            try
            {
                var difference = checked(now - sentAt);
                return difference != long.MinValue &&
                    Math.Abs(difference) <= MaximumSignalClockSkewMilliseconds;
            }
            catch (OverflowException) { return false; }
        }

        private static long SaturatingAdd(long value, long increment)
        {
            try { return checked(value + increment); }
            catch (OverflowException) { return long.MaxValue; }
        }

        private static bool TryReserve(ref long counter, int amount, long maximum)
        {
            while (true)
            {
                var current = Volatile.Read(ref counter);
                if (current > maximum - amount) return false;
                if (Interlocked.CompareExchange(ref counter, current + amount, current) == current)
                    return true;
            }
        }

        private static bool IsExpectedNetworkFailure(Exception error) =>
            error is HttpRequestException or WebSocketException or IOException or SocketException or
                TimeoutException or TaskCanceledException or OperationCanceledException or InvalidDataException;

        private static HttpStatusCode? FindHttpStatus(Exception error)
        {
            for (var current = error; current != null; current = current.InnerException)
            {
                if (current is HttpRequestException request && request.StatusCode.HasValue)
                    return request.StatusCode.Value;
            }
            return null;
        }

        private static async Task ObserveCancellationAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        private static async Task ObserveLaneShutdownAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        private static TimeSpan FullJitter(int failureCount)
        {
            var ceiling = checked((int)RetryBase(failureCount).TotalMilliseconds);
            return TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(ceiling + 1));
        }

        private static void DrainAndZero(ChannelReader<QueuedSignal> reader, ref long bytes)
        {
            while (reader.TryRead(out var item))
                CryptographicOperations.ZeroMemory(item.Wire);
            Interlocked.Exchange(ref bytes, 0);
        }

        private void PublishFault(ProductionDirectSignalingFaultCode fault, bool willRetry)
        {
            var current = Snapshot;
            Publish(ProductionDirectSignalingLaneState.Fault, fault,
                current.Attempt, current.Generation, willRetry);
        }

        private void Publish(
            ProductionDirectSignalingLaneState state,
            ProductionDirectSignalingFaultCode fault,
            int attempt = 0,
            long generationValue = 0,
            bool willRetry = false,
            int? httpStatus = null,
            bool directDataV1Negotiated = false,
            bool directSessionCutoverV1Negotiated = false)
        {
            var next = new ProductionDirectSignalingSnapshot
            {
                State = state,
                Fault = fault,
                Attempt = attempt,
                Generation = generationValue,
                WillRetry = willRetry,
                HttpStatus = httpStatus,
                DirectDataV1Negotiated = directDataV1Negotiated,
                DirectSessionCutoverV1Negotiated = directSessionCutoverV1Negotiated
            };
            lock (sync) snapshot = next;
            try { StateChanged?.Invoke(next); }
            catch (Exception) { }
        }

        private sealed class QueuedSignal
        {
            public QueuedSignal(
                TransportSignalEnvelopeV1 envelope,
                byte[] wire,
                long requiredDirectDataGeneration = 0)
            {
                Envelope = envelope;
                Wire = wire;
                RequiredDirectDataGeneration = requiredDirectDataGeneration;
            }

            public TransportSignalEnvelopeV1 Envelope { get; }
            public byte[] Wire { get; }
            public long RequiredDirectDataGeneration { get; }
        }

        private readonly struct SignalCursor : IEquatable<SignalCursor>
        {
            private SignalCursor(string connectionId, string senderDeviceId, ulong pathEpoch)
            {
                ConnectionId = connectionId;
                SenderDeviceId = senderDeviceId;
                PathEpoch = pathEpoch;
            }

            private string ConnectionId { get; }
            private string SenderDeviceId { get; }
            private ulong PathEpoch { get; }

            public static SignalCursor From(TransportSignalEnvelopeV1 value) => new(
                Convert.ToHexString(value.ConnectionId.Span),
                Convert.ToHexString(value.SenderDeviceId.Span),
                value.PathEpoch);

            public bool Equals(SignalCursor other) =>
                ConnectionId == other.ConnectionId && SenderDeviceId == other.SenderDeviceId &&
                PathEpoch == other.PathEpoch;

            public override bool Equals(object obj) => obj is SignalCursor other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(ConnectionId, SenderDeviceId, PathEpoch);
        }

        private readonly struct SignalNonce : IEquatable<SignalNonce>
        {
            private SignalNonce(SignalCursor cursor, string nonce)
            {
                Cursor = cursor;
                Nonce = nonce;
            }

            private SignalCursor Cursor { get; }
            private string Nonce { get; }

            public static SignalNonce From(TransportSignalEnvelopeV1 value) =>
                new(SignalCursor.From(value), Convert.ToHexString(value.Nonce.Span));

            public bool Equals(SignalNonce other) => Cursor.Equals(other.Cursor) && Nonce == other.Nonce;
            public override bool Equals(object obj) => obj is SignalNonce other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Cursor, Nonce);
        }

        private sealed class BoundedCursorMap
        {
            private readonly int capacity;
            private readonly Dictionary<SignalCursor, ulong> values = new();
            private readonly Queue<SignalCursor> order = new();

            public BoundedCursorMap(int capacity) { this.capacity = capacity; }

            public bool IsReplay(SignalCursor cursor, ulong sequence) =>
                values.TryGetValue(cursor, out var previous) && sequence <= previous;

            public void Remember(SignalCursor cursor, ulong sequence)
            {
                if (!values.ContainsKey(cursor)) order.Enqueue(cursor);
                values[cursor] = sequence;
                while (values.Count > capacity)
                {
                    var oldest = order.Dequeue();
                    values.Remove(oldest);
                }
            }
        }

        private sealed class BoundedNonceSet
        {
            private readonly int capacity;
            private readonly HashSet<SignalNonce> values = new();
            private readonly Queue<SignalNonce> order = new();

            public BoundedNonceSet(int capacity) { this.capacity = capacity; }
            public bool Contains(SignalNonce nonce) => values.Contains(nonce);

            public void Remember(SignalNonce nonce)
            {
                if (!values.Add(nonce)) return;
                order.Enqueue(nonce);
                while (values.Count > capacity) values.Remove(order.Dequeue());
            }
        }
    }

    internal sealed class ProductionDirectSignalingLaneException : Exception
    {
        public ProductionDirectSignalingLaneException(
            ProductionDirectSignalingFaultCode fault,
            bool retryable,
            Exception inner = null)
            : base("The direct signalling lane failed.", inner)
        {
            Fault = fault;
            Retryable = retryable;
        }

        public ProductionDirectSignalingFaultCode Fault { get; }
        public bool Retryable { get; }
    }
}
