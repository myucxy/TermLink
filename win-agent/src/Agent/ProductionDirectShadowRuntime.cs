using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    internal interface IProductionDirectShadowSignaling : IAsyncDisposable
    {
        Task<ProductionDirectSignalingCallResult<ProductionDirectTransportServerConfig>>
            FetchConfigAsync(CancellationToken cancellationToken = default);
        Task<ProductionDirectSignalingCallResult<ProductionValidatedDirectPathTicket>>
            IssueTicketAsync(
                Guid targetDeviceId,
                Guid connectionId,
                ProductionDirectTransportServerConfig config,
                CancellationToken cancellationToken = default);
        ProductionValidatedDirectPathTicket ValidateIncomingTicket(
            DirectPathTicketV1 ticket,
            Guid initiatorDeviceId,
            Guid connectionId,
            ProductionDirectTransportServerConfig config);
        bool Start();
        bool TrySend(TransportSignalEnvelopeV1 envelope);
        IAsyncEnumerable<TransportSignalEnvelopeV1> ReadSignalsAsync(
            CancellationToken cancellationToken = default);
    }

    internal interface IProductionDirectShadowNative : IDisposable
    {
        ProductionDirectNativeSnapshot Snapshot { get; }
        ProductionDirectNativeCommandResult Gather(ProductionDirectNativeGatherRequest request);
        ProductionDirectNativeCommandResult MarkCandidatesExchanged(
            ulong epoch,
            Guid attemptId,
            ulong generation,
            IReadOnlyList<ProductionDirectNativeEndpoint> candidates);
        ProductionDirectNativeCommandResult Nominate(ProductionDirectNativeStartRequest request);
        ProductionDirectNativeCommandResult Cancel(ulong epoch, Guid attemptId);
        ProductionDirectNativeCommandResult NetworkChanged(ulong epoch, Guid attemptId);
        ProductionDirectNativeCommandResult SubmitSignature(
            ulong epoch, Guid attemptId, ulong requestId, byte[] signature);
        IReadOnlyList<ProductionDirectNativeEvent> Poll(int maximumEvents = 32);
    }

    internal sealed class ProductionDirectShadowNative : IProductionDirectShadowNative
    {
        private readonly ProductionDirectNativeBoundary boundary;

        public ProductionDirectShadowNative() =>
            boundary = new ProductionDirectNativeBoundary(enabled: true);

        public ProductionDirectNativeSnapshot Snapshot => boundary.Snapshot;
        public ProductionDirectNativeCommandResult Gather(ProductionDirectNativeGatherRequest value) =>
            boundary.Gather(value);
        public ProductionDirectNativeCommandResult MarkCandidatesExchanged(
            ulong epoch,
            Guid attemptId,
            ulong generation,
            IReadOnlyList<ProductionDirectNativeEndpoint> candidates) =>
            boundary.MarkCandidatesExchanged(epoch, attemptId, generation, candidates);
        public ProductionDirectNativeCommandResult Nominate(ProductionDirectNativeStartRequest value) =>
            boundary.Nominate(value);
        public ProductionDirectNativeCommandResult Cancel(ulong epoch, Guid attemptId) =>
            boundary.Cancel(epoch, attemptId);
        public ProductionDirectNativeCommandResult NetworkChanged(ulong epoch, Guid attemptId) =>
            boundary.NetworkChanged(epoch, attemptId);
        public ProductionDirectNativeCommandResult SubmitSignature(
            ulong epoch, Guid attemptId, ulong requestId, byte[] signature) =>
            boundary.SubmitSignature(epoch, attemptId, requestId, signature);
        public IReadOnlyList<ProductionDirectNativeEvent> Poll(int maximumEvents = 32) =>
            boundary.Poll(maximumEvents);
        public void Dispose() => boundary.Dispose();
    }

    internal sealed class ProductionDirectPeerAuthorization : IDisposable
    {
        public ProductionDirectPeerAuthorization(
            Guid deviceId,
            byte[] pairwiseKey,
            byte[] signingPublicKey,
            long keyEpoch)
        {
            if (deviceId == Guid.Empty || pairwiseKey?.Length != 32 ||
                signingPublicKey?.Length != 32 || keyEpoch < 1)
                throw new ArgumentException("Direct peer authorization is invalid.");
            DeviceId = deviceId;
            PairwiseKey = (byte[])pairwiseKey.Clone();
            SigningPublicKey = (byte[])signingPublicKey.Clone();
            KeyEpoch = keyEpoch;
        }

        public Guid DeviceId { get; }
        public byte[] PairwiseKey { get; private set; }
        public byte[] SigningPublicKey { get; private set; }
        public long KeyEpoch { get; }

        public void Dispose()
        {
            if (PairwiseKey != null) CryptographicOperations.ZeroMemory(PairwiseKey);
            if (SigningPublicKey != null) CryptographicOperations.ZeroMemory(SigningPublicKey);
            PairwiseKey = null;
            SigningPublicKey = null;
        }
    }

    internal sealed class ProductionDirectGatherPlatform
    {
        public ProductionDirectNativeEndpoint LocalBind { get; init; }
        public IPAddress AdvertisedLocalAddress { get; init; }
        public int PrefixLength { get; init; } = 24;
        public Func<bool> IsNetworkCurrent { get; init; }
        public IReadOnlyList<ProductionDirectNativeEndpoint> StunServers { get; init; } =
            Array.Empty<ProductionDirectNativeEndpoint>();
    }

    internal sealed class ProductionDirectCandidatePairSelection
    {
        public ProductionDirectCandidatePairSelection(
            DirectCandidateV1 local,
            DirectCandidateV1 remote)
        {
            Local = local ?? throw new ArgumentNullException(nameof(local));
            Remote = remote ?? throw new ArgumentNullException(nameof(remote));
        }

        public DirectCandidateV1 Local { get; }
        public DirectCandidateV1 Remote { get; }
    }

    internal interface IProductionDirectShadowPlatform
    {
        Task<ProductionDirectGatherPlatform> PrepareGatherAsync(
            IReadOnlyList<string> stunServers,
            CancellationToken cancellationToken);

        Task<ProductionDirectGatherPlatform> PrepareGatherForPeerAsync(
            Guid peer, IReadOnlyList<IPAddress> peerHosts, IReadOnlyList<string> stunServers,
            CancellationToken cancellationToken) => PrepareGatherAsync(stunServers, cancellationToken);

        void ReportPathOutcome(Guid peer, IPAddress address, bool succeeded) { }
    }

    internal sealed class ProductionWindowsDirectShadowPlatform : IProductionDirectShadowPlatform
    {
        private readonly ProductionDirectInterfaceSelection selection = new();

        public Task<ProductionDirectGatherPlatform> PrepareGatherAsync(
            IReadOnlyList<string> stunServers,
            CancellationToken cancellationToken) =>
            PrepareGatherForPeerAsync(Guid.Empty, Array.Empty<IPAddress>(), stunServers, cancellationToken);

        public void ReportPathOutcome(Guid peer, IPAddress address, bool succeeded) =>
            selection.Report(peer, address, succeeded);

        public async Task<ProductionDirectGatherPlatform> PrepareGatherForPeerAsync(
            Guid peer, IReadOnlyList<IPAddress> peerHosts, IReadOnlyList<string> stunServers,
            CancellationToken cancellationToken)
        {
            var interfaces = ProductionDirectInterfaceSelection.Capture();
            var resolved = new List<ProductionDirectNativeEndpoint>();
            foreach (var text in stunServers ?? Array.Empty<string>())
            {
                var (host, port) = ParseStunEndpoint(text);
                using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                dnsTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host, dnsTimeout.Token).ConfigureAwait(false);
                    resolved.AddRange(addresses.Where(ProductionDirectCandidateCollector.IsAllowedHostAddress)
                        .Select(address => new ProductionDirectNativeEndpoint(address, port)));
                }
                catch (SocketException) { /* STUN DNS must not disable host/LAN candidates. */ }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var routes = resolved.Select(value => ProductionDirectInterfaceSelection.RouteSource(value.Address, value.Port))
                .Where(value => value != null).ToHashSet();
            var selected = selection.Select(peer, interfaces, peerHosts, routes);
            var address = selected.Address;
            var targets = peerHosts.Concat(resolved.Select(value => value.Address))
                .Where(value => value.AddressFamily == address.AddressFamily).Distinct().ToArray();
            var initialRoutes = targets.Select(value => ProductionDirectInterfaceSelection.RouteSource(value, 3478)).ToArray();
            return new ProductionDirectGatherPlatform
            {
                LocalBind = new ProductionDirectNativeEndpoint(address, 0),
                AdvertisedLocalAddress = address,
                PrefixLength = selected.PrefixLength,
                IsNetworkCurrent = () => ProductionDirectInterfaceSelection.Capture().Any(value =>
                    value.Address.Equals(address) && value.PrefixLength == selected.PrefixLength) &&
                    initialRoutes.SequenceEqual(targets.Select(value => ProductionDirectInterfaceSelection.RouteSource(value, 3478))),
                StunServers = resolved.Where(value => value.Address.AddressFamily == address.AddressFamily)
                    .GroupBy(value => (value.Address, value.Port)).Select(group => group.First()).Take(2).ToArray()
            };
        }

        private static (string Host, int Port) ParseStunEndpoint(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !value.StartsWith("stun:", StringComparison.Ordinal) || value.Any(char.IsWhiteSpace))
                throw new InvalidDataException("DIRECT_STUN_CONFIG_INVALID");
            var authority = value.Substring(5);
            string host;
            string portText;
            if (authority.StartsWith("[", StringComparison.Ordinal))
            {
                var close = authority.IndexOf(']');
                if (close < 2 || close + 2 >= authority.Length || authority[close + 1] != ':')
                    throw new InvalidDataException("DIRECT_STUN_CONFIG_INVALID");
                host = authority.Substring(1, close - 1);
                portText = authority.Substring(close + 2);
            }
            else
            {
                var colon = authority.LastIndexOf(':');
                if (colon < 1 || authority.IndexOf(':') != colon)
                    throw new InvalidDataException("DIRECT_STUN_CONFIG_INVALID");
                host = authority.Substring(0, colon);
                portText = authority.Substring(colon + 1);
            }
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65_535 ||
                host.Length is < 1 or > 253)
                throw new InvalidDataException("DIRECT_STUN_CONFIG_INVALID");
            return (host, port);
        }
    }

    internal enum ProductionDirectShadowState
    {
        Disabled,
        WaitingForRelay,
        Configuring,
        Gathering,
        Negotiating,
        ShadowReadyNotRouted,
        RelayOnly,
        Stopped
    }

    internal sealed class ProductionDirectShadowSnapshot
    {
        public ProductionDirectShadowState State { get; init; }
        public string ReasonCode { get; init; } = string.Empty;
        public ulong Epoch { get; init; }
        public Guid AttemptId { get; init; }
        public Guid PeerDeviceId { get; init; }
        public bool DirectRoutingPermitted => false;
    }

    /// <summary>
    /// Independent shadow-only control chain. It owns no Relay socket, terminal
    /// session, renderer, Envelope route, or durable acknowledgement state.
    /// Every error retires only the exact native attempt and leaves Relay health
    /// decisions with ProductionRealtimeConnectionSupervisor.
    /// </summary>
    internal sealed class ProductionDirectShadowRuntime : IAsyncDisposable
    {
        internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(12);
        internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
        private readonly bool enabled;
        private readonly Guid localDeviceId;
        private readonly byte[] signalKeyId;
        private readonly DeviceIdentity identity;
        private readonly ProductionDirectTransportSupervisor supervisor;
        private readonly Func<IProductionDirectShadowSignaling> signalingFactory;
        private readonly Func<IProductionDirectShadowNative> nativeFactory;
        private readonly Func<CancellationToken, Task<IReadOnlyList<ProductionDirectPeerAuthorization>>>
            peerProvider;
        private readonly IProductionDirectShadowPlatform platform;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly Func<long> clockMilliseconds;
        private readonly CancellationTokenSource lifetime = new();
        private readonly SemaphoreSlim wake = new(0, 1);
        private readonly object sync = new();
        private SignalingLane signalingLane;
        private long signalingGeneration;
        private Task worker;
        private Attempt active;
        private bool relayConnected;
        private int disposed;
        private ProductionDirectShadowSnapshot snapshot;

        public ProductionDirectShadowRuntime(
            bool enabled,
            Guid localDeviceId,
            string signalKeyId,
            DeviceIdentity identity,
            ProductionDirectTransportSupervisor supervisor,
            Func<IProductionDirectShadowSignaling> signalingFactory,
            Func<CancellationToken, Task<IReadOnlyList<ProductionDirectPeerAuthorization>>> peerProvider,
            Func<IProductionDirectShadowNative> nativeFactory = null,
            IProductionDirectShadowPlatform platform = null,
            Func<TimeSpan, CancellationToken, Task> delay = null,
            Func<long> clockMilliseconds = null)
        {
            if (localDeviceId == Guid.Empty) throw new ArgumentException("A local device is required.");
            if (!ProductionDirectTransportSignalingClient.ValidKeyId(signalKeyId))
                throw new ArgumentException("A valid responder device key ID is required.", nameof(signalKeyId));
            this.enabled = enabled;
            this.localDeviceId = localDeviceId;
            this.signalKeyId = Encoding.UTF8.GetBytes(signalKeyId);
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            this.signalingFactory = signalingFactory ?? throw new ArgumentNullException(nameof(signalingFactory));
            this.peerProvider = peerProvider ?? throw new ArgumentNullException(nameof(peerProvider));
            this.nativeFactory = nativeFactory ?? (() => new ProductionDirectShadowNative());
            this.platform = platform ?? new ProductionWindowsDirectShadowPlatform();
            this.delay = delay ?? Task.Delay;
            this.clockMilliseconds = clockMilliseconds ??
                (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            snapshot = new ProductionDirectShadowSnapshot
            {
                State = enabled
                    ? ProductionDirectShadowState.WaitingForRelay
                    : ProductionDirectShadowState.Disabled,
                ReasonCode = enabled ? string.Empty : "FEATURE_DISABLED"
            };
        }

        public ProductionDirectShadowSnapshot Snapshot
        {
            get { lock (sync) return snapshot; }
        }

        public bool Start()
        {
            if (!enabled || Volatile.Read(ref disposed) != 0) return false;
            lock (sync)
            {
                if (worker != null) return false;
                worker = RunAsync(lifetime.Token);
                if (relayConnected) ReleaseWake();
                return true;
            }
        }

        public void ObserveRelayConnected(bool connected)
        {
            Attempt current = null;
            lock (sync)
            {
                relayConnected = connected;
                if (!connected) current = active;
                if (!connected && snapshot.State != ProductionDirectShadowState.Disabled &&
                    snapshot.State != ProductionDirectShadowState.Stopped)
                    PublishLocked(ProductionDirectShadowState.WaitingForRelay, "RELAY_UNAVAILABLE", current);
            }
            if (!connected && current != null) Retire(current, "RELAY_UNAVAILABLE", networkChanged: false);
            if (connected) ReleaseWake();
        }

        public void NotifyNetworkChanged()
        {
            Attempt current;
            lock (sync) current = active;
            if (current != null) Retire(current, "NETWORK_CHANGED", networkChanged: true);
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await wake.WaitAsync(token).ConfigureAwait(false);
                if (!RelayConnected()) continue;
                try
                {
                    await RunAttemptAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Attempt current;
                    lock (sync) current = active;
                    if (current != null) Retire(current, "DIRECT_SHADOW_FAILED", networkChanged: false);
                    else Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_SHADOW_FAILED", null);
                }
                if (!token.IsCancellationRequested && RelayConnected())
                {
                    var cooldown = supervisor.Snapshot().CooldownUntilUtc - DateTime.UtcNow;
                    var retry = cooldown > RetryDelay ? cooldown : RetryDelay;
                    await delay(retry, token).ConfigureAwait(false);
                    ReleaseWake();
                }
            }
        }

        private async Task RunAttemptAsync(CancellationToken token)
        {
            Publish(ProductionDirectShadowState.Configuring, string.Empty, null);
            var lane = await GetSignalingLaneAsync(token).ConfigureAwait(false);
            var configResult = await lane.Signaling.FetchConfigAsync(token).ConfigureAwait(false);
            if (configResult.Status != ProductionDirectSignalingCallStatus.Success ||
                !ProductionDirectTransportSignalingClient.HasCurrentShadowCapabilities(configResult.Value))
            {
                Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_CONFIG_REJECTED", null);
                return;
            }
            if (!lane.Started)
            {
                if (!lane.Signaling.Start())
                {
                    lane.MarkTerminal();
                    Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_SIGNAL_START_FAILED", null);
                    return;
                }
                lane.Started = true;
                lane.Reader = PumpSignalsAsync(lane, token);
            }

            var peers = await peerProvider(token).ConfigureAwait(false);
            if (peers == null || !peers.Any(value => value != null))
            {
                Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_NO_AUTHORIZED_PEER", null);
                DisposePeers(peers, null);
                return;
            }
            InboundOffer inbound = null;
            try
            {
                using var offerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                offerTimeout.CancelAfter(AttemptTimeout);
                try
                {
                    inbound = await WaitForOfferAsync(
                        lane,
                        peers,
                        configResult.Value,
                        offerTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Publish(ProductionDirectShadowState.RelayOnly,
                        "DIRECT_OFFER_TIMEOUT", null);
                    return;
                }
                DisposePeers(peers, inbound.Peer);
                var attempt = new Attempt(
                    inbound.Peer,
                    inbound.ConnectionId,
                    inbound.Ticket,
                    Guid.NewGuid(),
                    configResult.Value,
                    lane,
                    ProductionDirectNativeRole.Responder)
                {
                    LastRemoteSequence = inbound.SignalSequence,
                    RemoteCertificateSha256 = inbound.CertificateSha256,
                    RemoteCandidates = inbound.Candidates
                };
                inbound.TransferOwnership();
                if (!supervisor.TryBeginAttempt(
                        attempt.Peer.DeviceId,
                        attempt.ConnectionId,
                        attempt.Ticket.Ticket.PathEpoch,
                        out var supervisorToken,
                        out _))
                {
                    attempt.Dispose();
                    Publish(ProductionDirectShadowState.RelayOnly, "DIRECT_ATTEMPT_REJECTED", null);
                    return;
                }
                attempt.SupervisorToken = supervisorToken;
                lock (sync)
                {
                    if (disposed != 0 || !relayConnected || active != null)
                    {
                        supervisor.TryFallbackAttempt(supervisorToken, "RELAY_UNAVAILABLE");
                        attempt.Dispose();
                        PublishLocked(ProductionDirectShadowState.WaitingForRelay,
                            "RELAY_UNAVAILABLE", null);
                        return;
                    }
                    active = attempt;
                }

                var gather = await platform.PrepareGatherAsync(
                    configResult.Value.StunServers, token).ConfigureAwait(false);
                if (!IsCurrent(attempt)) return;
                var createdNative = nativeFactory();
                lock (sync)
                {
                    if (ReferenceEquals(active, attempt)) attempt.Native = createdNative;
                }
                if (!ReferenceEquals(attempt.Native, createdNative))
                {
                    createdNative.Dispose();
                    return;
                }
                if (!createdNative.Snapshot.NativeAvailable ||
                    createdNative.Gather(new ProductionDirectNativeGatherRequest
                    {
                        Epoch = attempt.Ticket.Ticket.PathEpoch,
                        AttemptId = attempt.AttemptId,
                        NetworkHandle = 0,
                        LocalBind = gather.LocalBind,
                        AdvertisedLocalAddress = gather.AdvertisedLocalAddress,
                        StunServers = gather.StunServers,
                        GatherTimeoutMilliseconds = 1_000
                    }) != ProductionDirectNativeCommandResult.Accepted || !IsCurrent(attempt))
                {
                    Retire(attempt, "DIRECT_NATIVE_UNAVAILABLE", networkChanged: false);
                    return;
                }
                Publish(ProductionDirectShadowState.Gathering, string.Empty, attempt);
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
                var timeout = TimeSpan.FromMilliseconds(Math.Max(
                        1,
                        Math.Min(
                            AttemptTimeout.TotalMilliseconds,
                            attempt.Ticket.Ticket.ExpiresAtMs - clockMilliseconds())));
                bounded.CancelAfter(timeout);
                try
                {
                    await DriveAttemptAsync(attempt, createdNative, bounded).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Retire(attempt, "DIRECT_ATTEMPT_TIMEOUT", networkChanged: false);
                }
            }
            finally
            {
                inbound?.Dispose();
                if (inbound == null) DisposePeers(peers, null);
            }
        }

        private async Task<InboundOffer> WaitForOfferAsync(
            SignalingLane lane,
            IReadOnlyList<ProductionDirectPeerAuthorization> peers,
            ProductionDirectTransportServerConfig config,
            CancellationToken token)
        {
            var authorized = peers.Where(value => value != null)
                .GroupBy(value => value.DeviceId)
                .ToDictionary(group => group.Key, group => group.First());
            while (!token.IsCancellationRequested)
            {
                while (lane.Incoming.Reader.TryRead(out var envelope))
                {
                    if (envelope?.SignalType != TransportSignalTypeV1.PathOffer) continue;
                    Guid sender;
                    try { sender = GuidFromNetworkBytes(envelope.SenderDeviceId.Span); }
                    catch (InvalidDataException) { continue; }
                    if (!authorized.TryGetValue(sender, out var peer) ||
                        envelope.SignalSeq != 1 || envelope.SignalAck != 0 ||
                        envelope.PathEpoch == 0 || envelope.PathEpoch > long.MaxValue)
                        continue;
                    if (!TryReadOffer(lane, peer, config, envelope, out var offer))
                        throw new InvalidDataException("DIRECT_OFFER_REJECTED");
                    return offer;
                }
                if (lane.IsTerminal) throw new InvalidDataException("DIRECT_SIGNAL_FAILED");
                await delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
            }
            throw new OperationCanceledException(token);
        }

        private bool TryReadOffer(
            SignalingLane lane,
            ProductionDirectPeerAuthorization peer,
            ProductionDirectTransportServerConfig config,
            TransportSignalEnvelopeV1 envelope,
            out InboundOffer result)
        {
            result = null;
            byte[] plaintext = null;
            ProductionValidatedDirectPathTicket ticket = null;
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(envelope.KeyId.Span, signalKeyId))
                    return false;
                plaintext = ProductionDirectTransportCrypto.DecryptSignal(
                    peer.PairwiseKey, envelope);
                var payload = TransportSignalPayloadV1.Parser.ParseFrom(plaintext);
                if (payload.FormatVersion != 1 ||
                    payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.PathOffer)
                    return false;
                var canonical = CanonicalOfferPayload(payload.PathOffer).ToByteArray();
                try
                {
                    if (!plaintext.AsSpan().SequenceEqual(canonical)) return false;
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }
                var offer = payload.PathOffer;
                var connectionId = GuidFromNetworkBytes(envelope.ConnectionId.Span);
                if (offer.QuicCertificateSha256.Length != 32 ||
                    IsAllZero(offer.QuicCertificateSha256.Span) || !offer.GatheringComplete ||
                    !ValidCandidates(offer.Candidates, offer.Ticket.ExpiresAtMs) ||
                    offer.Ticket.PathEpoch != envelope.PathEpoch ||
                    !offer.Ticket.ConnectionId.Span.SequenceEqual(envelope.ConnectionId.Span) ||
                    !offer.Ticket.InitiatorDeviceId.Span.SequenceEqual(envelope.SenderDeviceId.Span) ||
                    !offer.Ticket.ResponderDeviceId.Span.SequenceEqual(GuidBytes(localDeviceId)))
                    return false;
                ticket = lane.Signaling.ValidateIncomingTicket(
                    offer.Ticket,
                    peer.DeviceId,
                    connectionId,
                    config);
                result = new InboundOffer(
                    peer,
                    connectionId,
                    ticket,
                    envelope.SignalSeq,
                    offer.QuicCertificateSha256.ToByteArray(),
                    offer.Candidates.Select(value => value.Clone()).ToArray());
                ticket = null;
                return true;
            }
            catch (Exception error) when (
                error is InvalidDataException or CryptographicException or InvalidProtocolBufferException or
                    ArgumentException or OverflowException)
            {
                return false;
            }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ticket?.WireBytes != null) CryptographicOperations.ZeroMemory(ticket.WireBytes);
            }
        }

        private static TransportSignalPayloadV1 CanonicalOfferPayload(DirectPathOfferV1 offer)
        {
            var result = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                PathOffer = new DirectPathOfferV1
                {
                    Ticket = ProductionDirectTransportSignalingClient.CanonicalTicket(offer.Ticket),
                    QuicCertificateSha256 = offer.QuicCertificateSha256,
                    GatheringComplete = offer.GatheringComplete
                }
            };
            result.PathOffer.Candidates.Add(offer.Candidates.Select(CanonicalCandidate));
            return result;
        }

        private static DirectCandidateV1 CanonicalCandidate(DirectCandidateV1 value) => new()
        {
            CandidateId = value.CandidateId,
            Kind = value.Kind,
            Carrier = value.Carrier,
            IpAddress = value.IpAddress,
            Port = value.Port,
            Priority = value.Priority,
            RelatedCandidateId = value.RelatedCandidateId,
            NetworkCost = value.NetworkCost,
            ExpiresAtMs = value.ExpiresAtMs
        };

        private async Task DriveAttemptAsync(
            Attempt attempt,
            IProductionDirectShadowNative attemptNative,
            CancellationTokenSource attemptCancellation)
        {
            var token = attemptCancellation.Token;
            while (!token.IsCancellationRequested && IsCurrent(attempt))
            {
                foreach (var item in attemptNative.Poll(16))
                {
                    try
                    {
                        if (!IsCurrent(attempt)) return;
                        if (item.Kind == ProductionDirectNativeEventKind.IgnoredStale ||
                            item.Epoch != attempt.Ticket.Ticket.PathEpoch ||
                            item.AttemptId != attempt.AttemptId)
                        {
                            continue;
                        }
                        if (item.Generation == 0 ||
                            item.Generation <= attempt.LastNativeGeneration)
                        {
                            Retire(attempt, "DIRECT_NATIVE_EVENT_STALE", false);
                            return;
                        }
                        attempt.LastNativeGeneration = item.Generation;
                        if (item.Kind == ProductionDirectNativeEventKind.IdentityReady)
                        {
                            if (attempt.IdentityReady || item.Payload == null || item.Payload.Length == 0)
                            {
                                Retire(attempt, "DIRECT_NATIVE_EVENT_REJECTED", false);
                                return;
                            }
                            attempt.IdentityReady = true;
                            attempt.LocalCertificateSha256 = SHA256.HashData(item.Payload);
                        }
                        else if (item.Kind == ProductionDirectNativeEventKind.CandidatesReady)
                        {
                            if (attempt.CandidatesReady || item.Endpoint == null ||
                                item.Candidates == null || item.Candidates.Count == 0)
                            {
                                Retire(attempt, "DIRECT_NATIVE_EVENT_REJECTED", false);
                                return;
                            }
                            attempt.CandidatesReady = true;
                            attempt.CandidatesGeneration = item.Generation;
                            attempt.NativeCandidates = item.Candidates
                                .Select(value => new ProductionDirectNativeEndpoint(
                                    value.Address, value.Port))
                                .ToArray();
                            attempt.BoundEndpoint = new ProductionDirectNativeEndpoint(
                                item.Endpoint.Address, item.Endpoint.Port);
                            attempt.LocalCandidates = EncodeCandidates(
                                attempt.NativeCandidates,
                                attempt.Ticket.Ticket.ExpiresAtMs);
                        }
                        else if (item.Kind == ProductionDirectNativeEventKind.SignRequest)
                        {
                            if (!attempt.Nominated || attempt.SignedProof || item.RequestId == 0 ||
                                item.Payload == null || item.Payload.Length == 0)
                            {
                                Retire(attempt, "DIRECT_SIGN_REQUEST_REJECTED", false);
                                return;
                            }
                            var signature = identity.Sign(item.Payload);
                            try
                            {
                                if (attemptNative.SubmitSignature(
                                        item.Epoch,
                                        item.AttemptId,
                                        item.RequestId,
                                        signature) != ProductionDirectNativeCommandResult.Accepted)
                                {
                                    Retire(attempt, "DIRECT_SIGNATURE_REJECTED", false);
                                    return;
                                }
                            }
                            finally { CryptographicOperations.ZeroMemory(signature); }
                            attempt.SignedProof = true;
                        }
                        else if (item.Kind == ProductionDirectNativeEventKind.ShadowReady)
                        {
                            if (!attempt.Nominated || !attempt.SignedProof ||
                                attempt.LocalPathBinding != null ||
                                item.Payload?.Length != 32 || IsAllZero(item.Payload))
                            {
                                Retire(attempt, "DIRECT_SHADOW_BINDING_REJECTED", false);
                                return;
                            }
                            attempt.LocalPathBinding = (byte[])item.Payload.Clone();
                            if (!SendNominationResult(attempt))
                            {
                                Retire(attempt, "DIRECT_SIGNAL_QUEUE_FAILED", false);
                                return;
                            }
                            if (!TryCompleteShadowReady(attempt)) return;
                            if (attempt.ShadowReady)
                                attemptCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                        }
                        else if (item.Kind == ProductionDirectNativeEventKind.DirectFailed)
                        {
                            Retire(attempt, "DIRECT_NATIVE_FAILED", networkChanged: false);
                            return;
                        }
                    }
                    finally
                    {
                        if (item.Payload != null) CryptographicOperations.ZeroMemory(item.Payload);
                    }
                }

                if (!attempt.OfferSent && attempt.LocalCertificateSha256?.Length == 32 &&
                    attempt.LocalCandidates?.Count > 0)
                {
                    TransportSignalPayloadV1 candidateSignal;
                    TransportSignalTypeV1 signalType;
                    if (attempt.Role == ProductionDirectNativeRole.Initiator)
                    {
                        candidateSignal = new TransportSignalPayloadV1
                        {
                            FormatVersion = 1,
                            PathOffer = new DirectPathOfferV1
                            {
                                Ticket = attempt.Ticket.Ticket.Clone(),
                                QuicCertificateSha256 = ByteString.CopyFrom(
                                    attempt.LocalCertificateSha256),
                                GatheringComplete = true
                            }
                        };
                        candidateSignal.PathOffer.Candidates.Add(attempt.LocalCandidates);
                        signalType = TransportSignalTypeV1.PathOffer;
                    }
                    else
                    {
                        candidateSignal = new TransportSignalPayloadV1
                        {
                            FormatVersion = 1,
                            PathAnswer = new DirectPathAnswerV1
                            {
                                TicketId = attempt.Ticket.Ticket.TicketId,
                                QuicCertificateSha256 = ByteString.CopyFrom(
                                    attempt.LocalCertificateSha256),
                                GatheringComplete = true,
                                Result = DirectPathResultV1.Accepted,
                                ReasonCode = string.Empty
                            }
                        };
                        candidateSignal.PathAnswer.Candidates.Add(attempt.LocalCandidates);
                        signalType = TransportSignalTypeV1.PathAnswer;
                    }
                    if (!SendEncrypted(attempt, signalType, candidateSignal))
                    {
                        Retire(attempt, "DIRECT_SIGNAL_QUEUE_FAILED", networkChanged: false);
                        return;
                    }
                    if (attemptNative.MarkCandidatesExchanged(
                            attempt.Ticket.Ticket.PathEpoch,
                            attempt.AttemptId,
                            attempt.CandidatesGeneration,
                            attempt.NativeCandidates) != ProductionDirectNativeCommandResult.Accepted)
                    {
                        Retire(attempt, "DIRECT_CANDIDATE_EXCHANGE_REJECTED", networkChanged: false);
                        return;
                    }
                    attempt.OfferSequence = attempt.LocalSequence;
                    attempt.OfferSent = true;
                    supervisor.MarkGatheringComplete(attempt.SupervisorToken);
                    Publish(ProductionDirectShadowState.Negotiating, string.Empty, attempt);
                }

                while (attempt.Lane.Incoming.Reader.TryRead(out var envelope))
                {
                    if (!Matches(attempt, envelope)) continue;
                    if (!attempt.Nominated)
                    {
                        if (!attempt.OfferSent)
                        {
                            Retire(attempt, "DIRECT_SIGNAL_REJECTED", networkChanged: false);
                            return;
                        }
                        if (attempt.Role == ProductionDirectNativeRole.Initiator)
                        {
                            if (envelope.SignalSeq != 1 ||
                                envelope.SignalAck != attempt.OfferSequence ||
                                !TryReadAnswer(attempt, envelope, out var answer) ||
                                !Nominate(attempt, attemptNative, envelope.SignalSeq, answer))
                            {
                                Retire(attempt, "DIRECT_NOMINATION_FAILED", networkChanged: false);
                                return;
                            }
                        }
                        else if (envelope.SignalSeq != 2 ||
                            envelope.SignalAck != attempt.OfferSequence ||
                            !TryReadNomination(attempt, envelope, out var nomination) ||
                            !NominateResponder(attempt, attemptNative, envelope.SignalSeq, nomination))
                        {
                            Retire(attempt, "DIRECT_NOMINATION_FAILED", networkChanged: false);
                            return;
                        }
                    }
                    else
                    {
                        var expectedSequence = attempt.Role == ProductionDirectNativeRole.Initiator
                            ? 2UL
                            : 3UL;
                        var minimumAcknowledgement = attempt.Role == ProductionDirectNativeRole.Initiator
                            ? attempt.NominationSequence
                            : attempt.OfferSequence;
                        if (envelope.SignalSeq != expectedSequence ||
                            envelope.SignalAck < minimumAcknowledgement ||
                            envelope.SignalAck > attempt.LocalSequence ||
                            !TryReadNominationResult(attempt, envelope, out var binding))
                        {
                            Retire(attempt, "DIRECT_SIGNAL_REJECTED", networkChanged: false);
                            return;
                        }
                        attempt.LastRemoteSequence = envelope.SignalSeq;
                        attempt.PeerPathBinding = binding;
                        if (!TryCompleteShadowReady(attempt)) return;
                        if (attempt.ShadowReady)
                            attemptCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                }
                await delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(false);
            }
        }

        private bool Nominate(
            Attempt attempt,
            IProductionDirectShadowNative attemptNative,
            ulong remoteSequence,
            DirectPathAnswerV1 answer)
        {
            var selected = SelectInitiatorCandidatePair(
                attempt.LocalCandidates,
                answer.Candidates);
            if (selected == null) return false;
            var localCandidate = selected.Local;
            var remoteCandidate = selected.Remote;
            var nominationId = RandomIdentifier();
            var connectivityNonce = RandomIdentifier();
            var payload = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                Nomination = new DirectNominationV1
                {
                    NominationId = ByteString.CopyFrom(nominationId),
                    Pair = new DirectCandidatePairV1
                    {
                        InitiatorCandidateId = localCandidate.CandidateId,
                        ResponderCandidateId = remoteCandidate.CandidateId
                    },
                    ConnectivityNonce = ByteString.CopyFrom(connectivityNonce)
                }
            };
            try
            {
                if (!SendEncrypted(attempt, TransportSignalTypeV1.Nomination, payload))
                    return false;
                attempt.RemoteCertificateSha256 = answer.QuicCertificateSha256.ToByteArray();
                attempt.RemoteCandidates = answer.Candidates.Select(value => value.Clone()).ToArray();
                attempt.NominationSequence = attempt.LocalSequence;
                return SubmitNativeNomination(
                    attempt,
                    attemptNative,
                    remoteSequence,
                    nominationId,
                    localCandidate.CandidateId.Span,
                    remoteCandidate.CandidateId.Span,
                    connectivityNonce);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nominationId);
                CryptographicOperations.ZeroMemory(connectivityNonce);
            }
        }

        private bool SendEncrypted(
            Attempt attempt,
            TransportSignalTypeV1 type,
            TransportSignalPayloadV1 payload)
        {
            var plaintext = payload.ToByteArray();
            try
            {
                var metadata = new TransportSignalEnvelopeV1
                {
                    ProtocolVersion = 1,
                    SignalType = type,
                    ConnectionId = ByteString.CopyFrom(GuidBytes(attempt.ConnectionId)),
                    SenderDeviceId = ByteString.CopyFrom(GuidBytes(localDeviceId)),
                    RecipientDeviceId = ByteString.CopyFrom(GuidBytes(attempt.Peer.DeviceId)),
                    PathEpoch = attempt.Ticket.Ticket.PathEpoch,
                    SignalSeq = ++attempt.LocalSequence,
                    SignalAck = attempt.LastRemoteSequence,
                    SentAtMs = clockMilliseconds(),
                    KeyId = ByteString.CopyFrom(signalKeyId)
                };
                var encrypted = ProductionDirectTransportCrypto.EncryptSignal(
                    attempt.Peer.PairwiseKey, metadata, plaintext);
                return attempt.Lane.Signaling.TrySend(encrypted);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private bool SendNominationResult(Attempt attempt)
        {
            if (attempt.LocalResultSent || attempt.LocalPathBinding?.Length != 32 ||
                attempt.NominationId?.Length != 16 ||
                attempt.InitiatorCandidateId?.Length != 16 ||
                attempt.ResponderCandidateId?.Length != 16)
                return false;
            var payload = new TransportSignalPayloadV1
            {
                FormatVersion = 1,
                NominationResult = new DirectNominationResultV1
                {
                    NominationId = ByteString.CopyFrom(attempt.NominationId),
                    Pair = new DirectCandidatePairV1
                    {
                        InitiatorCandidateId = ByteString.CopyFrom(attempt.InitiatorCandidateId),
                        ResponderCandidateId = ByteString.CopyFrom(attempt.ResponderCandidateId)
                    },
                    Result = DirectPathResultV1.Accepted,
                    ReasonCode = string.Empty,
                    DirectPathBinding = ByteString.CopyFrom(attempt.LocalPathBinding)
                }
            };
            if (!SendEncrypted(attempt, TransportSignalTypeV1.NominationResult, payload)) return false;
            attempt.LocalResultSent = true;
            return true;
        }

        private bool TryCompleteShadowReady(Attempt attempt)
        {
            if (attempt.LocalPathBinding == null || attempt.PeerPathBinding == null) return true;
            if (!CryptographicOperations.FixedTimeEquals(
                    attempt.LocalPathBinding,
                    attempt.PeerPathBinding))
            {
                Retire(attempt, "DIRECT_SHADOW_BINDING_MISMATCH", false);
                return false;
            }
            if (attempt.ShadowReady) return true;
            supervisor.MarkShadowReady(attempt.SupervisorToken, attempt.LocalPathBinding);
            attempt.ShadowReady = true;
            attempt.ReleaseNegotiationKeyMaterial();
            Publish(ProductionDirectShadowState.ShadowReadyNotRouted, string.Empty, attempt);
            return true;
        }

        private static bool TryReadAnswer(
            Attempt attempt,
            TransportSignalEnvelopeV1 envelope,
            out DirectPathAnswerV1 answer)
        {
            answer = null;
            if (envelope.SignalType != TransportSignalTypeV1.PathAnswer) return false;
            byte[] plaintext = null;
            try
            {
                plaintext = ProductionDirectTransportCrypto.DecryptSignal(
                    attempt.Peer.PairwiseKey, envelope);
                var payload = TransportSignalPayloadV1.Parser.ParseFrom(plaintext);
                if (payload.FormatVersion != 1 ||
                    payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.PathAnswer)
                    return false;
                var answerValue = payload.PathAnswer;
                var canonicalPayload = new TransportSignalPayloadV1
                {
                    FormatVersion = 1,
                    PathAnswer = new DirectPathAnswerV1
                    {
                        TicketId = answerValue.TicketId,
                        QuicCertificateSha256 = answerValue.QuicCertificateSha256,
                        GatheringComplete = answerValue.GatheringComplete,
                        Result = answerValue.Result,
                        ReasonCode = answerValue.ReasonCode
                    }
                };
                canonicalPayload.PathAnswer.Candidates.Add(
                    answerValue.Candidates.Select(CanonicalCandidate));
                var canonical = canonicalPayload.ToByteArray();
                try
                {
                    if (!plaintext.AsSpan().SequenceEqual(canonical)) return false;
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }
                answer = answerValue;
                if (!answer.TicketId.Span.SequenceEqual(attempt.Ticket.Ticket.TicketId.Span) ||
                    answer.QuicCertificateSha256.Length != 32 ||
                    answer.Result != DirectPathResultV1.Accepted || !answer.GatheringComplete ||
                    !string.IsNullOrEmpty(answer.ReasonCode) ||
                    !ValidCandidates(answer.Candidates, attempt.Ticket.Ticket.ExpiresAtMs))
                {
                    answer = null;
                    return false;
                }
                return true;
            }
            catch (Exception error) when (
                error is InvalidDataException or CryptographicException or InvalidProtocolBufferException or
                    ArgumentException or OverflowException)
            {
                answer = null;
                return false;
            }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static bool TryReadNominationResult(
            Attempt attempt,
            TransportSignalEnvelopeV1 envelope,
            out byte[] directPathBinding)
        {
            directPathBinding = null;
            if (envelope.SignalType != TransportSignalTypeV1.NominationResult) return false;
            byte[] plaintext = null;
            try
            {
                plaintext = ProductionDirectTransportCrypto.DecryptSignal(
                    attempt.Peer.PairwiseKey, envelope);
                var payload = TransportSignalPayloadV1.Parser.ParseFrom(plaintext);
                if (payload.FormatVersion != 1 ||
                    payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.NominationResult)
                    return false;
                var resultValue = payload.NominationResult;
                var canonical = new TransportSignalPayloadV1
                {
                    FormatVersion = 1,
                    NominationResult = new DirectNominationResultV1
                    {
                        NominationId = resultValue.NominationId,
                        Pair = resultValue.Pair == null
                            ? null
                            : new DirectCandidatePairV1
                            {
                                InitiatorCandidateId = resultValue.Pair.InitiatorCandidateId,
                                ResponderCandidateId = resultValue.Pair.ResponderCandidateId
                            },
                        Result = resultValue.Result,
                        ReasonCode = resultValue.ReasonCode,
                        DirectPathBinding = resultValue.DirectPathBinding
                    }
                }.ToByteArray();
                try
                {
                    if (!plaintext.AsSpan().SequenceEqual(canonical)) return false;
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }
                var result = resultValue;
                if (result.Result != DirectPathResultV1.Accepted ||
                    !string.IsNullOrEmpty(result.ReasonCode) ||
                    result.NominationId.Length != 16 || result.Pair == null ||
                    result.DirectPathBinding.Length != 32 || IsAllZero(result.DirectPathBinding.Span) ||
                    !result.NominationId.Span.SequenceEqual(attempt.NominationId) ||
                    !result.Pair.InitiatorCandidateId.Span.SequenceEqual(
                        attempt.InitiatorCandidateId) ||
                    !result.Pair.ResponderCandidateId.Span.SequenceEqual(
                        attempt.ResponderCandidateId))
                    return false;
                directPathBinding = result.DirectPathBinding.ToByteArray();
                return true;
            }
            catch (Exception error) when (
                error is InvalidDataException or CryptographicException or InvalidProtocolBufferException or
                    ArgumentException or OverflowException)
            {
                if (directPathBinding != null)
                    CryptographicOperations.ZeroMemory(directPathBinding);
                directPathBinding = null;
                return false;
            }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private static bool TryReadNomination(
            Attempt attempt,
            TransportSignalEnvelopeV1 envelope,
            out DirectNominationV1 nomination)
        {
            nomination = null;
            if (envelope.SignalType != TransportSignalTypeV1.Nomination) return false;
            byte[] plaintext = null;
            try
            {
                plaintext = ProductionDirectTransportCrypto.DecryptSignal(
                    attempt.Peer.PairwiseKey, envelope);
                var payload = TransportSignalPayloadV1.Parser.ParseFrom(plaintext);
                if (payload.FormatVersion != 1 ||
                    payload.SignalCase != TransportSignalPayloadV1.SignalOneofCase.Nomination)
                    return false;
                var value = payload.Nomination;
                var canonical = new TransportSignalPayloadV1
                {
                    FormatVersion = 1,
                    Nomination = new DirectNominationV1
                    {
                        NominationId = value.NominationId,
                        Pair = value.Pair == null
                            ? null
                            : new DirectCandidatePairV1
                            {
                                InitiatorCandidateId = value.Pair.InitiatorCandidateId,
                                ResponderCandidateId = value.Pair.ResponderCandidateId
                            },
                        ConnectivityNonce = value.ConnectivityNonce
                    }
                }.ToByteArray();
                try
                {
                    if (!plaintext.AsSpan().SequenceEqual(canonical)) return false;
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }
                if (value.NominationId.Length != 16 || IsAllZero(value.NominationId.Span) ||
                    value.ConnectivityNonce.Length != 16 ||
                    IsAllZero(value.ConnectivityNonce.Span) || value.Pair == null ||
                    value.Pair.InitiatorCandidateId.Length != 16 ||
                    value.Pair.ResponderCandidateId.Length != 16 ||
                    !attempt.RemoteCandidates.Any(candidate =>
                        candidate.CandidateId.Span.SequenceEqual(
                            value.Pair.InitiatorCandidateId.Span)) ||
                    !attempt.LocalCandidates.Any(candidate =>
                        candidate.CandidateId.Span.SequenceEqual(
                            value.Pair.ResponderCandidateId.Span)))
                    return false;
                nomination = value;
                return true;
            }
            catch (Exception error) when (
                error is InvalidDataException or CryptographicException or InvalidProtocolBufferException or
                    ArgumentException or OverflowException)
            {
                nomination = null;
                return false;
            }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        private bool NominateResponder(
            Attempt attempt,
            IProductionDirectShadowNative attemptNative,
            ulong remoteSequence,
            DirectNominationV1 nomination)
        {
            var nominationId = nomination.NominationId.ToByteArray();
            var connectivityNonce = nomination.ConnectivityNonce.ToByteArray();
            try
            {
                return SubmitNativeNomination(
                    attempt,
                    attemptNative,
                    remoteSequence,
                    nominationId,
                    nomination.Pair.InitiatorCandidateId.Span,
                    nomination.Pair.ResponderCandidateId.Span,
                    connectivityNonce);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nominationId);
                CryptographicOperations.ZeroMemory(connectivityNonce);
            }
        }

        private bool SubmitNativeNomination(
            Attempt attempt,
            IProductionDirectShadowNative attemptNative,
            ulong remoteSequence,
            byte[] nominationId,
            ReadOnlySpan<byte> initiatorCandidateId,
            ReadOnlySpan<byte> responderCandidateId,
            byte[] connectivityNonce)
        {
            byte[] localCandidateId = null;
            byte[] remoteCandidateId = null;
            byte[] localSendProbeKey = null;
            byte[] peerReceiveProbeKey = null;
            byte[] localProbeNonce = null;
            byte[] peerProbeNonce = null;
            byte[] localProofNonce = null;
            byte[] initiatorId = null;
            byte[] responderId = null;
            try
            {
                localCandidateId = (attempt.Role == ProductionDirectNativeRole.Initiator
                    ? initiatorCandidateId
                    : responderCandidateId).ToArray();
                remoteCandidateId = (attempt.Role == ProductionDirectNativeRole.Initiator
                    ? responderCandidateId
                    : initiatorCandidateId).ToArray();
                var localCandidate = attempt.LocalCandidates.Single(value =>
                    value.CandidateId.Span.SequenceEqual(localCandidateId));
                var remoteCandidate = attempt.RemoteCandidates.Single(value =>
                    value.CandidateId.Span.SequenceEqual(remoteCandidateId));
                var nominationGuid = GuidFromNetworkBytes(nominationId);
                var serverKeyId = Encoding.UTF8.GetString(
                    attempt.Ticket.Ticket.ServerSigningKeyId.Span);
                if (!attempt.Config.TicketSigningKeys.TryGetValue(serverKeyId, out var serverKey))
                    return false;

                localSendProbeKey = ProductionDirectTransportCrypto.DeriveProbeKey(
                    attempt.Peer.PairwiseKey,
                    attempt.ConnectionId,
                    attempt.Ticket.Ticket.PathEpoch,
                    nominationGuid,
                    localDeviceId,
                    attempt.Peer.DeviceId);
                peerReceiveProbeKey = ProductionDirectTransportCrypto.DeriveProbeKey(
                    attempt.Peer.PairwiseKey,
                    attempt.ConnectionId,
                    attempt.Ticket.Ticket.PathEpoch,
                    nominationGuid,
                    attempt.Peer.DeviceId,
                    localDeviceId);
                localProbeNonce = (byte[])connectivityNonce.Clone();
                peerProbeNonce = (byte[])connectivityNonce.Clone();
                localProofNonce = RandomNumberGenerator.GetBytes(32);
                initiatorId = initiatorCandidateId.ToArray();
                responderId = responderCandidateId.ToArray();
                var result = attemptNative.Nominate(new ProductionDirectNativeStartRequest
                {
                    Epoch = attempt.Ticket.Ticket.PathEpoch,
                    AttemptId = attempt.AttemptId,
                    ConnectionId = attempt.ConnectionId,
                    LocalDeviceId = localDeviceId,
                    PeerDeviceId = attempt.Peer.DeviceId,
                    Role = attempt.Role,
                    TimeoutMilliseconds = 10_000,
                    LocalBind = attempt.BoundEndpoint,
                    RemoteCandidate = new ProductionDirectNativeEndpoint(
                        new IPAddress(remoteCandidate.IpAddress.Span),
                        checked((int)remoteCandidate.Port)),
                    StunServers = Array.Empty<ProductionDirectNativeEndpoint>(),
                    Ticket = attempt.Ticket.WireBytes,
                    ServerSigningPublicKey = serverKey.PublicKey,
                    PeerCertificateSha256 = attempt.RemoteCertificateSha256,
                    PeerDeviceSigningPublicKey = attempt.Peer.SigningPublicKey,
                    LocalSendProbeHmacKey = localSendProbeKey,
                    PeerReceiveProbeHmacKey = peerReceiveProbeKey,
                    TicketId = GuidFromNetworkBytes(attempt.Ticket.Ticket.TicketId.Span),
                    NominationId = nominationGuid,
                    InitiatorCandidateId = GuidFromNetworkBytes(initiatorId),
                    ResponderCandidateId = GuidFromNetworkBytes(responderId),
                    LocalProbeNonce = localProbeNonce,
                    PeerProbeNonce = peerProbeNonce,
                    LocalProofNonce = localProofNonce,
                    NowMilliseconds = clockMilliseconds()
                });
                if (result != ProductionDirectNativeCommandResult.Accepted) return false;

                attempt.LastRemoteSequence = remoteSequence;
                attempt.NominationId = (byte[])nominationId.Clone();
                attempt.InitiatorCandidateId = (byte[])initiatorId.Clone();
                attempt.ResponderCandidateId = (byte[])responderId.Clone();
                attempt.Nominated = true;
                attempt.ReleaseNativeInputMaterial();
                return true;
            }
            finally
            {
                Zero(localSendProbeKey);
                Zero(peerReceiveProbeKey);
                Zero(localProbeNonce);
                Zero(peerProbeNonce);
                Zero(localProofNonce);
                Zero(localCandidateId);
                Zero(remoteCandidateId);
                Zero(initiatorId);
                Zero(responderId);
            }
        }

        internal static ProductionDirectCandidatePairSelection SelectInitiatorCandidatePair(
            IReadOnlyList<DirectCandidateV1> localCandidates,
            IReadOnlyList<DirectCandidateV1> remoteCandidates)
        {
            if (localCandidates == null || remoteCandidates == null) return null;
            return localCandidates
                .Where(local => local != null)
                .SelectMany(local => remoteCandidates
                    .Where(remote => remote != null &&
                        local.IpAddress.Length == remote.IpAddress.Length)
                    .Select(remote => new
                    {
                        Local = local,
                        Remote = remote,
                        Rank = CandidatePairRank(local, remote)
                    }))
                .Where(pair => pair.Rank >= 0)
                .OrderBy(pair => pair.Rank)
                .ThenByDescending(pair => Math.Min(pair.Local.Priority, pair.Remote.Priority))
                .ThenByDescending(pair => pair.Local.Priority)
                .ThenByDescending(pair => pair.Remote.Priority)
                .ThenBy(pair => Convert.ToHexString(pair.Local.CandidateId.Span),
                    StringComparer.Ordinal)
                .ThenBy(pair => Convert.ToHexString(pair.Remote.CandidateId.Span),
                    StringComparer.Ordinal)
                .Take(32)
                .Select(pair => new ProductionDirectCandidatePairSelection(
                    pair.Local,
                    pair.Remote))
                .FirstOrDefault();
        }

        private static int CandidatePairRank(
            DirectCandidateV1 local,
            DirectCandidateV1 remote)
        {
            if (local.Kind == DirectCandidateKindV1.Host &&
                remote.Kind == DirectCandidateKindV1.Host)
                return SameConservativeLan(local.IpAddress.Span, remote.IpAddress.Span)
                    ? 0
                    : -1;
            if (local.Kind == DirectCandidateKindV1.ServerReflexive &&
                remote.Kind == DirectCandidateKindV1.ServerReflexive)
                return 1;
            if (local.Kind == DirectCandidateKindV1.Host &&
                remote.Kind == DirectCandidateKindV1.ServerReflexive)
                return 2;
            if (local.Kind == DirectCandidateKindV1.ServerReflexive &&
                remote.Kind == DirectCandidateKindV1.Host)
                return 3;
            return -1;
        }

        private static bool SameConservativeLan(
            ReadOnlySpan<byte> local,
            ReadOnlySpan<byte> remote)
        {
            if (local.Length == 4 && remote.Length == 4)
            {
                var localPrivate = local[0] == 10 ||
                    (local[0] == 172 && local[1] is >= 16 and <= 31) ||
                    (local[0] == 192 && local[1] == 168);
                var remotePrivate = remote[0] == 10 ||
                    (remote[0] == 172 && remote[1] is >= 16 and <= 31) ||
                    (remote[0] == 192 && remote[1] == 168);
                return localPrivate && remotePrivate &&
                    local.Slice(0, 3).SequenceEqual(remote.Slice(0, 3));
            }
            if (local.Length == 16 && remote.Length == 16)
                return (local[0] & 0xfe) == 0xfc && (remote[0] & 0xfe) == 0xfc &&
                    local.Slice(0, 8).SequenceEqual(remote.Slice(0, 8));
            return false;
        }

        private static void Zero(byte[] value)
        {
            if (value != null) CryptographicOperations.ZeroMemory(value);
        }

        private static byte[] RandomIdentifier()
        {
            byte[] result;
            do { result = RandomNumberGenerator.GetBytes(16); }
            while (IsAllZero(result));
            return result;
        }

        private static List<DirectCandidateV1> EncodeCandidates(
            IReadOnlyList<ProductionDirectNativeEndpoint> endpoints,
            long expiresAtMilliseconds)
        {
            var result = new List<DirectCandidateV1>();
            foreach (var endpoint in endpoints ?? Array.Empty<ProductionDirectNativeEndpoint>())
            {
                if (endpoint == null || endpoint.Port < 1024 ||
                    !ProductionDirectCandidateCollector.IsAllowedHostAddress(endpoint.Address)) continue;
                var candidate = new DirectCandidateV1
                {
                    CandidateId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                    Kind = result.Count == 0
                        ? DirectCandidateKindV1.Host
                        : DirectCandidateKindV1.ServerReflexive,
                    Carrier = DirectCarrierV1.UdpQuic,
                    IpAddress = ByteString.CopyFrom(endpoint.Address.GetAddressBytes()),
                    Port = checked((uint)endpoint.Port),
                    Priority = checked((uint)(2_000_000_000 - result.Count)),
                    ExpiresAtMs = expiresAtMilliseconds
                };
                if (result.Count > 0)
                    candidate.RelatedCandidateId = result[0].CandidateId;
                result.Add(candidate);
            }
            return result;
        }

        private static bool ValidCandidate(DirectCandidateV1 value, long ticketExpiry) =>
            value != null && value.CandidateId.Length == 16 &&
            value.CandidateId.Span.IndexOfAnyExcept((byte)0) >= 0 &&
            value.Kind is DirectCandidateKindV1.Host or DirectCandidateKindV1.ServerReflexive &&
            value.Carrier == DirectCarrierV1.UdpQuic &&
            value.IpAddress.Length is 4 or 16 && value.Port is >= 1024 and <= 65_535 &&
            value.Priority is >= 1 and <= int.MaxValue && value.NetworkCost <= 999 &&
            value.ExpiresAtMs > 0 && value.ExpiresAtMs <= ticketExpiry &&
            ProductionDirectCandidateCollector.IsAllowedHostAddress(
                new IPAddress(value.IpAddress.ToByteArray())) &&
            (value.Kind == DirectCandidateKindV1.Host
                ? value.RelatedCandidateId.Length == 0
                : value.RelatedCandidateId.Length == 16);

        private static bool ValidCandidates(
            IEnumerable<DirectCandidateV1> values,
            long ticketExpiry)
        {
            var candidates = values?.ToList();
            if (candidates == null || candidates.Count is < 1 or > 8 ||
                candidates.Any(value => !ValidCandidate(value, ticketExpiry)))
                return false;
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var endpoints = new HashSet<string>(StringComparer.Ordinal);
            var hosts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                var identifier = Convert.ToHexString(candidate.CandidateId.Span);
                var endpoint = Convert.ToHexString(candidate.IpAddress.Span) + ":" + candidate.Port;
                if (!identifiers.Add(identifier) || !endpoints.Add(endpoint)) return false;
                if (candidate.Kind == DirectCandidateKindV1.Host) hosts.Add(identifier);
            }
            return candidates.All(candidate =>
                candidate.Kind == DirectCandidateKindV1.Host ||
                hosts.Contains(Convert.ToHexString(candidate.RelatedCandidateId.Span)));
        }

        private async Task<SignalingLane> GetSignalingLaneAsync(CancellationToken token)
        {
            SignalingLane stale = null;
            SignalingLane result;
            lock (sync)
            {
                if (signalingLane != null && signalingLane.IsTerminal)
                {
                    stale = signalingLane;
                    signalingLane = null;
                }
                if (signalingLane == null)
                {
                    signalingGeneration = checked(signalingGeneration + 1);
                    signalingLane = new SignalingLane(
                        signalingGeneration,
                        signalingFactory());
                }
                result = signalingLane;
            }
            if (stale != null) await stale.DisposeAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private async Task PumpSignalsAsync(SignalingLane lane, CancellationToken token)
        {
            try
            {
                await foreach (var value in lane.Signaling.ReadSignalsAsync(token).ConfigureAwait(false))
                {
                    if (!lane.Incoming.Writer.TryWrite(value))
                    {
                        Attempt current;
                        lock (sync) current = active;
                        if (current != null && ReferenceEquals(current.Lane, lane))
                            Retire(current, "DIRECT_SIGNAL_QUEUE_FULL", false);
                        throw new InvalidDataException("DIRECT_SIGNAL_QUEUE_FULL");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Attempt current;
                lock (sync) current = active;
                if (current != null && ReferenceEquals(current.Lane, lane))
                    Retire(current, "DIRECT_SIGNAL_FAILED", false);
            }
            finally
            {
                lane.MarkTerminal();
                lane.Incoming.Writer.TryComplete();
                ReleaseWake();
            }
        }

        private bool Matches(Attempt attempt, TransportSignalEnvelopeV1 envelope) =>
            envelope != null && envelope.ConnectionId.Span.SequenceEqual(GuidBytes(attempt.ConnectionId)) &&
            envelope.SenderDeviceId.Span.SequenceEqual(GuidBytes(attempt.Peer.DeviceId)) &&
            envelope.RecipientDeviceId.Span.SequenceEqual(GuidBytes(localDeviceId)) &&
            CryptographicOperations.FixedTimeEquals(envelope.KeyId.Span, signalKeyId) &&
            envelope.PathEpoch == attempt.Ticket.Ticket.PathEpoch;

        private bool IsCurrent(Attempt value)
        {
            lock (sync) return ReferenceEquals(active, value);
        }

        private bool RelayConnected()
        {
            lock (sync) return relayConnected;
        }

        private static bool IsAllZero(ReadOnlySpan<byte> value)
        {
            byte aggregate = 0;
            foreach (var item in value) aggregate |= item;
            return aggregate == 0;
        }

        private void Retire(Attempt attempt, string reason, bool networkChanged)
        {
            IProductionDirectShadowNative currentNative = null;
            lock (sync)
            {
                if (!ReferenceEquals(active, attempt)) return;
                active = null;
                currentNative = attempt.Native;
                attempt.Native = null;
                PublishLocked(
                    relayConnected ? ProductionDirectShadowState.RelayOnly :
                        ProductionDirectShadowState.WaitingForRelay,
                    reason,
                    attempt);
            }
            try
            {
                if (networkChanged)
                    currentNative?.NetworkChanged(attempt.Ticket.Ticket.PathEpoch, attempt.AttemptId);
                else
                    currentNative?.Cancel(attempt.Ticket.Ticket.PathEpoch, attempt.AttemptId);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally { currentNative?.Dispose(); }
            if (networkChanged)
                supervisor.NotifyNetworkChanged();
            else if (string.Equals(reason, "RELAY_UNAVAILABLE", StringComparison.Ordinal))
                supervisor.ObserveRelayConnected(false);
            else
                supervisor.TryFallbackAttempt(attempt.SupervisorToken, reason);
            attempt.Dispose();
            ReleaseWake();
        }

        private void Publish(
            ProductionDirectShadowState state,
            string reason,
            Attempt attempt)
        {
            lock (sync) PublishLocked(state, reason, attempt);
        }

        private void PublishLocked(
            ProductionDirectShadowState state,
            string reason,
            Attempt attempt)
        {
            snapshot = new ProductionDirectShadowSnapshot
            {
                State = state,
                ReasonCode = reason ?? string.Empty,
                Epoch = attempt?.Ticket?.Ticket?.PathEpoch ?? 0,
                AttemptId = attempt?.AttemptId ?? Guid.Empty,
                PeerDeviceId = attempt?.Peer?.DeviceId ?? Guid.Empty
            };
        }

        private void ReleaseWake()
        {
            try { wake.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        private static void DisposePeers(
            IReadOnlyList<ProductionDirectPeerAuthorization> peers,
            ProductionDirectPeerAuthorization retained)
        {
            if (peers == null) return;
            foreach (var value in peers)
                if (value != null && !ReferenceEquals(value, retained)) value.Dispose();
        }

        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
        private static Guid GuidFromNetworkBytes(ReadOnlySpan<byte> value)
        {
            if (value.Length != 16) throw new InvalidDataException("DIRECT_UUID_INVALID");
            return Guid.ParseExact(Convert.ToHexString(value), "N");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            Attempt current;
            Task running;
            SignalingLane lane;
            lock (sync)
            {
                current = active;
                running = worker;
                lane = signalingLane;
                signalingLane = null;
            }
            if (current != null) Retire(current, "STOPPED", false);
            lock (sync)
            {
                snapshot = new ProductionDirectShadowSnapshot
                {
                    State = ProductionDirectShadowState.Stopped,
                    ReasonCode = "STOPPED"
                };
            }
            if (lane != null) await lane.DisposeAsync().ConfigureAwait(false);
            if (running != null)
            {
                try { await running.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            CryptographicOperations.ZeroMemory(signalKeyId);
            lifetime.Dispose();
            wake.Dispose();
        }

        private sealed class SignalingLane : IAsyncDisposable
        {
            private int terminal;
            private int disposed;

            public SignalingLane(long generation, IProductionDirectShadowSignaling signaling)
            {
                if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
                Generation = generation;
                Signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
                Incoming = Channel.CreateBounded<TransportSignalEnvelopeV1>(
                    new BoundedChannelOptions(32)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleReader = true,
                        SingleWriter = true,
                        AllowSynchronousContinuations = false
                    });
            }

            public long Generation { get; }
            public IProductionDirectShadowSignaling Signaling { get; }
            public Channel<TransportSignalEnvelopeV1> Incoming { get; }
            public bool Started { get; set; }
            public Task Reader { get; set; }
            public bool IsTerminal => Volatile.Read(ref terminal) != 0 ||
                (Reader?.IsCompleted ?? false);

            public void MarkTerminal() => Interlocked.Exchange(ref terminal, 1);

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                MarkTerminal();
                Incoming.Writer.TryComplete();
                await Signaling.DisposeAsync().ConfigureAwait(false);
                if (Reader != null)
                {
                    try { await Reader.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                while (Incoming.Reader.TryRead(out _)) { }
            }
        }

        private sealed class InboundOffer : IDisposable
        {
            private bool ownershipTransferred;

            public InboundOffer(
                ProductionDirectPeerAuthorization peer,
                Guid connectionId,
                ProductionValidatedDirectPathTicket ticket,
                ulong signalSequence,
                byte[] certificateSha256,
                IReadOnlyList<DirectCandidateV1> candidates)
            {
                Peer = peer ?? throw new ArgumentNullException(nameof(peer));
                ConnectionId = connectionId;
                Ticket = ticket ?? throw new ArgumentNullException(nameof(ticket));
                SignalSequence = signalSequence;
                CertificateSha256 = certificateSha256 ??
                    throw new ArgumentNullException(nameof(certificateSha256));
                Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
            }

            public ProductionDirectPeerAuthorization Peer { get; }
            public Guid ConnectionId { get; }
            public ProductionValidatedDirectPathTicket Ticket { get; }
            public ulong SignalSequence { get; }
            public byte[] CertificateSha256 { get; }
            public IReadOnlyList<DirectCandidateV1> Candidates { get; }

            public void TransferOwnership() => ownershipTransferred = true;

            public void Dispose()
            {
                if (ownershipTransferred) return;
                Peer.Dispose();
                CryptographicOperations.ZeroMemory(CertificateSha256);
                CryptographicOperations.ZeroMemory(Ticket.WireBytes);
            }
        }

        private sealed class Attempt : IDisposable
        {
            public Attempt(
                ProductionDirectPeerAuthorization peer,
                Guid connectionId,
                ProductionValidatedDirectPathTicket ticket,
                Guid attemptId,
                ProductionDirectTransportServerConfig config,
                SignalingLane lane,
                ProductionDirectNativeRole role)
            {
                Peer = peer;
                ConnectionId = connectionId;
                Ticket = ticket;
                AttemptId = attemptId;
                Config = config;
                Lane = lane ?? throw new ArgumentNullException(nameof(lane));
                Role = role;
            }

            public ProductionDirectPeerAuthorization Peer { get; }
            public Guid ConnectionId { get; }
            public ProductionValidatedDirectPathTicket Ticket { get; }
            public Guid AttemptId { get; }
            public ProductionDirectTransportServerConfig Config { get; }
            public SignalingLane Lane { get; }
            public ProductionDirectNativeRole Role { get; }
            public ProductionDirectAttemptToken SupervisorToken { get; set; }
            public IProductionDirectShadowNative Native { get; set; }
            public byte[] LocalCertificateSha256 { get; set; }
            public IReadOnlyList<DirectCandidateV1> LocalCandidates { get; set; }
            public IReadOnlyList<ProductionDirectNativeEndpoint> NativeCandidates { get; set; }
            public IReadOnlyList<DirectCandidateV1> RemoteCandidates { get; set; }
            public ProductionDirectNativeEndpoint BoundEndpoint { get; set; }
            public byte[] RemoteCertificateSha256 { get; set; }
            public ulong LocalSequence { get; set; }
            public ulong LastRemoteSequence { get; set; }
            public ulong OfferSequence { get; set; }
            public ulong NominationSequence { get; set; }
            public ulong CandidatesGeneration { get; set; }
            public ulong LastNativeGeneration { get; set; }
            public byte[] NominationId { get; set; }
            public byte[] InitiatorCandidateId { get; set; }
            public byte[] ResponderCandidateId { get; set; }
            public byte[] LocalPathBinding { get; set; }
            public byte[] PeerPathBinding { get; set; }
            public bool IdentityReady { get; set; }
            public bool CandidatesReady { get; set; }
            public bool OfferSent { get; set; }
            public bool Nominated { get; set; }
            public bool SignedProof { get; set; }
            public bool LocalResultSent { get; set; }
            public bool ShadowReady { get; set; }

            public void ReleaseNativeInputMaterial()
            {
                if (Ticket?.WireBytes != null) CryptographicOperations.ZeroMemory(Ticket.WireBytes);
            }

            public void ReleaseNegotiationKeyMaterial()
            {
                Peer?.Dispose();
                ReleaseNativeInputMaterial();
            }

            public void Dispose()
            {
                Native?.Dispose();
                Native = null;
                if (LocalCertificateSha256 != null)
                    CryptographicOperations.ZeroMemory(LocalCertificateSha256);
                if (RemoteCertificateSha256 != null)
                    CryptographicOperations.ZeroMemory(RemoteCertificateSha256);
                if (NominationId != null) CryptographicOperations.ZeroMemory(NominationId);
                if (InitiatorCandidateId != null) CryptographicOperations.ZeroMemory(InitiatorCandidateId);
                if (ResponderCandidateId != null) CryptographicOperations.ZeroMemory(ResponderCandidateId);
                if (LocalPathBinding != null) CryptographicOperations.ZeroMemory(LocalPathBinding);
                if (PeerPathBinding != null) CryptographicOperations.ZeroMemory(PeerPathBinding);
                ReleaseNegotiationKeyMaterial();
                LocalCandidates = Array.Empty<DirectCandidateV1>();
                NativeCandidates = Array.Empty<ProductionDirectNativeEndpoint>();
                RemoteCandidates = Array.Empty<DirectCandidateV1>();
                BoundEndpoint = null;
            }
        }
    }
}
