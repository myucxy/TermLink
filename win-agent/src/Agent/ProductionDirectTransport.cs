using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    internal enum ProductionDirectPathState
    {
        RelayOnly,
        Gathering,
        Probing,
        ProbeVerifiedNotDirect,
        ShadowReadyNotRouted
    }

    /// <summary>
    /// Identifies one exact asynchronous direct-path attempt. The random nonce
    /// prevents a delayed callback from an older attempt from mutating a newer
    /// attempt that happens to reuse the same peer or connection identifiers.
    /// </summary>
    internal readonly struct ProductionDirectAttemptToken : IEquatable<ProductionDirectAttemptToken>
    {
        internal ProductionDirectAttemptToken(long generation, Guid nonce)
        {
            Generation = generation;
            Nonce = nonce;
        }

        public long Generation { get; }
        internal Guid Nonce { get; }
        public bool IsEmpty => Generation <= 0 || Nonce == Guid.Empty;

        public bool Equals(ProductionDirectAttemptToken other) =>
            Generation == other.Generation && Nonce == other.Nonce;

        public override bool Equals(object value) =>
            value is ProductionDirectAttemptToken other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Generation, Nonce);
    }

    /// <summary>
    /// Owns only direct-path negotiation. It deliberately has no reference to
    /// the realtime WebSocket, SessionHost, renderer, or durable ACK stores.
    /// Every path failure synchronously selects RelayOnly; callers keep the
    /// independently supervised realtime Relay connected throughout.
    /// </summary>
    internal sealed class ProductionDirectTransportSupervisor
    {
        internal static readonly TimeSpan MinimumAttemptInterval = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(1);
        private const int MaximumTrackedPeers = 64;
        private sealed class PeerAttemptHistory
        {
            public ulong LastAcceptedEpoch;
            public DateTime LastAttemptUtc = DateTime.MinValue;
            public DateTime CooldownUntilUtc = DateTime.MinValue;
            public int ConsecutiveFailures;
        }

        private readonly object sync = new object();
        private readonly Func<DateTime> utcNow;
        private readonly Dictionary<Guid, PeerAttemptHistory> peerHistories =
            new Dictionary<Guid, PeerAttemptHistory>();
        private ProductionDirectPathState state = ProductionDirectPathState.RelayOnly;
        private bool relayConnected;
        private Guid peerDeviceId;
        private Guid connectionId;
        private ulong pathEpoch;
        private long attemptGeneration;
        private Guid attemptNonce;
        private byte[] authenticatedProbeSha256 = Array.Empty<byte>();
        private PeerAttemptHistory activePeerHistory;
        private PeerAttemptHistory lastObservedPeerHistory;
        private string fallbackReason = string.Empty;

        public ProductionDirectTransportSupervisor(Func<DateTime> utcNow = null)
        {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public void ObserveRelayConnected(bool connected)
        {
            lock (sync)
            {
                relayConnected = connected;
                if (!connected && state != ProductionDirectPathState.RelayOnly)
                    FallbackLocked("RELAY_UNAVAILABLE", countFailure: false);
            }
        }

        public bool TryBeginAttempt(
            Guid peerDeviceId,
            Guid connectionId,
            ulong pathEpoch,
            out ProductionDirectAttemptToken attemptToken,
            out string rejectionReason)
        {
            lock (sync)
            {
                var now = utcNow();
                attemptToken = default;
                rejectionReason = string.Empty;
                peerHistories.TryGetValue(peerDeviceId, out var peerHistory);
                if (!relayConnected)
                    rejectionReason = "RELAY_UNAVAILABLE";
                else if (peerDeviceId == Guid.Empty || connectionId == Guid.Empty ||
                    pathEpoch == 0 || pathEpoch > long.MaxValue)
                    rejectionReason = "INVALID_PATH_IDENTITY";
                else if (state != ProductionDirectPathState.RelayOnly)
                    rejectionReason = "PATH_ATTEMPT_ACTIVE";
                else if (peerHistory != null && pathEpoch <= peerHistory.LastAcceptedEpoch)
                    rejectionReason = "STALE_PATH_EPOCH";
                else if (peerHistory != null && now < peerHistory.CooldownUntilUtc)
                    rejectionReason = "PATH_COOLDOWN";
                else if (peerHistory != null && peerHistory.LastAttemptUtc != DateTime.MinValue &&
                    now - peerHistory.LastAttemptUtc < MinimumAttemptInterval)
                    rejectionReason = "ATTEMPT_RATE_LIMITED";
                else if (attemptGeneration == long.MaxValue)
                    rejectionReason = "ATTEMPT_GENERATION_EXHAUSTED";
                else if (peerHistory == null && peerHistories.Count >= MaximumTrackedPeers)
                    rejectionReason = "PEER_HISTORY_LIMIT";
                if (rejectionReason.Length != 0) return false;

                if (peerHistory == null)
                {
                    peerHistory = new PeerAttemptHistory();
                    peerHistories.Add(peerDeviceId, peerHistory);
                }
                attemptGeneration++;
                attemptNonce = Guid.NewGuid();
                this.peerDeviceId = peerDeviceId;
                this.connectionId = connectionId;
                this.pathEpoch = pathEpoch;
                authenticatedProbeSha256 = Array.Empty<byte>();
                peerHistory.LastAcceptedEpoch = pathEpoch;
                peerHistory.LastAttemptUtc = now;
                activePeerHistory = peerHistory;
                lastObservedPeerHistory = peerHistory;
                fallbackReason = string.Empty;
                state = ProductionDirectPathState.Gathering;
                attemptToken = new ProductionDirectAttemptToken(attemptGeneration, attemptNonce);
                return true;
            }
        }

        public void MarkGatheringComplete(ProductionDirectAttemptToken attemptToken) => Transition(
            attemptToken,
            ProductionDirectPathState.Gathering,
            ProductionDirectPathState.Probing);

        /// <summary>
        /// Records only an authenticated UDP reachability result. This state is
        /// deliberately not routing authority: ticket/peer proof, QUIC path
        /// binding, the shadow gate, and the per-direction cutover barrier are
        /// not implemented by this Windows foundation.
        /// </summary>
        public void MarkConnectivityProbeVerified(
            ProductionDirectAttemptToken attemptToken,
            ReadOnlySpan<byte> authenticatedProbeSha256)
        {
            if (authenticatedProbeSha256.Length != 32 || IsAllZero(authenticatedProbeSha256))
                throw new ArgumentException(
                    "A non-zero authenticated probe SHA-256 digest is required.",
                    nameof(authenticatedProbeSha256));
            lock (sync)
            {
                TransitionLocked(
                    attemptToken,
                    ProductionDirectPathState.Probing,
                    ProductionDirectPathState.ProbeVerifiedNotDirect);
                this.authenticatedProbeSha256 = authenticatedProbeSha256.ToArray();
            }
        }

        /// <summary>
        /// Records a fully authenticated native shadow path. This is observation
        /// only and cannot grant application routing authority.
        /// </summary>
        public void MarkShadowReady(
            ProductionDirectAttemptToken attemptToken,
            ReadOnlySpan<byte> directPathBinding)
        {
            if (directPathBinding.Length != 32 || IsAllZero(directPathBinding))
                throw new ArgumentException(
                    "A non-zero direct path binding is required.",
                    nameof(directPathBinding));
            lock (sync)
            {
                TransitionLocked(
                    attemptToken,
                    ProductionDirectPathState.Probing,
                    ProductionDirectPathState.ShadowReadyNotRouted);
                authenticatedProbeSha256 = directPathBinding.ToArray();
                activePeerHistory.ConsecutiveFailures = 0;
                activePeerHistory.CooldownUntilUtc = DateTime.MinValue;
            }
        }

        public bool TryFallbackAttempt(
            ProductionDirectAttemptToken attemptToken,
            string reasonCode)
        {
            if (!ValidReasonCode(reasonCode))
                throw new ArgumentException("A bounded transport reason code is required.", nameof(reasonCode));
            lock (sync)
            {
                if (state == ProductionDirectPathState.RelayOnly ||
                    !IsCurrentAttemptLocked(attemptToken))
                    return false;
                FallbackLocked(reasonCode, countFailure: true);
                return true;
            }
        }

        internal bool TryCancelAttempt(ProductionDirectAttemptToken token)
        {
            lock (sync)
            {
                if (state == ProductionDirectPathState.RelayOnly || !IsCurrentAttemptLocked(token)) return false;
                // Explicit authenticated cancellation is not a connectivity failure.
                // Server ticket issuance still bounds retry rate and replay.
                activePeerHistory.LastAttemptUtc = DateTime.MinValue;
                FallbackLocked("DIRECT_REMOTE_PATH_CLOSED", countFailure: false);
                return true;
            }
        }

        public void NotifyNetworkChanged()
        {
            lock (sync)
            {
                if (state != ProductionDirectPathState.RelayOnly)
                    FallbackLocked("NETWORK_CHANGED", countFailure: false);
            }
        }

        public ProductionDirectTransportSnapshot Snapshot()
        {
            lock (sync)
            {
                return new ProductionDirectTransportSnapshot(
                    state,
                    relayConnected,
                    peerDeviceId,
                    connectionId,
                    pathEpoch,
                    attemptGeneration,
                    authenticatedProbeSha256,
                    (activePeerHistory ?? lastObservedPeerHistory)?.ConsecutiveFailures ?? 0,
                    (activePeerHistory ?? lastObservedPeerHistory)?.CooldownUntilUtc ?? DateTime.MinValue,
                    fallbackReason);
            }
        }

        private void Transition(
            ProductionDirectAttemptToken attemptToken,
            ProductionDirectPathState expected,
            ProductionDirectPathState next)
        {
            lock (sync)
            {
                TransitionLocked(attemptToken, expected, next);
            }
        }

        private void TransitionLocked(
            ProductionDirectAttemptToken attemptToken,
            ProductionDirectPathState expected,
            ProductionDirectPathState next)
        {
            if (!IsCurrentAttemptLocked(attemptToken))
                throw new InvalidOperationException("Direct transport attempt token is stale.");
            if (!relayConnected)
            {
                FallbackLocked("RELAY_UNAVAILABLE", countFailure: false);
                throw new InvalidOperationException("Direct negotiation requires a healthy Relay fallback.");
            }
            if (state != expected)
                throw new InvalidOperationException("Direct transport transition is out of order.");
            state = next;
        }

        private bool IsCurrentAttemptLocked(ProductionDirectAttemptToken attemptToken) =>
            !attemptToken.IsEmpty &&
            attemptToken.Generation == attemptGeneration &&
            attemptToken.Nonce == attemptNonce;

        private void FallbackLocked(string reasonCode, bool countFailure)
        {
            if (state == ProductionDirectPathState.RelayOnly)
            {
                fallbackReason = reasonCode;
                return;
            }
            state = ProductionDirectPathState.RelayOnly;
            peerDeviceId = Guid.Empty;
            connectionId = Guid.Empty;
            pathEpoch = 0;
            attemptNonce = Guid.Empty;
            authenticatedProbeSha256 = Array.Empty<byte>();
            fallbackReason = reasonCode;
            var history = activePeerHistory;
            activePeerHistory = null;
            if (!countFailure || history == null) return;
            history.ConsecutiveFailures = checked(history.ConsecutiveFailures + 1);
            if (history.ConsecutiveFailures >= 3)
                history.CooldownUntilUtc = utcNow().Add(FailureCooldown);
        }

        internal static bool ValidReasonCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
            foreach (var character in value)
            {
                if ((character < 'A' || character > 'Z') &&
                    (character < '0' || character > '9') && character != '_')
                    return false;
            }
            return true;
        }

        private static bool IsAllZero(ReadOnlySpan<byte> value)
        {
            byte aggregate = 0;
            foreach (var item in value) aggregate |= item;
            return aggregate == 0;
        }
    }

    internal sealed class ProductionDirectTransportSnapshot
    {
        public ProductionDirectTransportSnapshot(
            ProductionDirectPathState state,
            bool relayConnected,
            Guid peerDeviceId,
            Guid connectionId,
            ulong pathEpoch,
            long attemptGeneration,
            byte[] authenticatedProbeSha256,
            int consecutiveFailures,
            DateTime cooldownUntilUtc,
            string fallbackReason)
        {
            State = state;
            RelayConnected = relayConnected;
            PeerDeviceId = peerDeviceId;
            ConnectionId = connectionId;
            PathEpoch = pathEpoch;
            AttemptGeneration = attemptGeneration;
            AuthenticatedProbeSha256 = (authenticatedProbeSha256 ?? Array.Empty<byte>()).ToArray();
            ConsecutiveFailures = consecutiveFailures;
            CooldownUntilUtc = cooldownUntilUtc;
            FallbackReason = fallbackReason ?? string.Empty;
        }

        public ProductionDirectPathState State { get; }
        public bool RelayConnected { get; }
        public Guid PeerDeviceId { get; }
        public Guid ConnectionId { get; }
        public ulong PathEpoch { get; }
        public long AttemptGeneration { get; }
        public byte[] AuthenticatedProbeSha256 { get; }
        public bool DirectRoutingPermitted => false;
        public int ConsecutiveFailures { get; }
        public DateTime CooldownUntilUtc { get; }
        public string FallbackReason { get; }
    }

    /// <summary>
    /// Retains the exact UDP socket and port represented by a host candidate.
    /// STUN, connectivity probes, and a future audited QUIC binding must reuse
    /// this lease; silently changing ports would invalidate NAT hole punching.
    /// </summary>
    internal sealed class ProductionDirectUdpCandidateLease : IDisposable
    {
        private Socket socket;
        private readonly SemaphoreSlim receiveOwner = new SemaphoreSlim(1, 1);

        internal ProductionDirectUdpCandidateLease(Socket socket, DirectCandidateV1 candidate)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        }

        public DirectCandidateV1 Candidate { get; }
        internal Socket Socket => socket ??
            throw new ObjectDisposedException(nameof(ProductionDirectUdpCandidateLease));

        internal async Task<IDisposable> ClaimReceiveProtocolAsync(CancellationToken token)
        {
            await receiveOwner.WaitAsync(token).ConfigureAwait(false);
            if (socket == null)
            {
                receiveOwner.Release();
                throw new ObjectDisposedException(nameof(ProductionDirectUdpCandidateLease));
            }
            return new ReceiveProtocolClaim(receiveOwner);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref socket, null)?.Dispose();
        }

        private sealed class ReceiveProtocolClaim : IDisposable
        {
            private SemaphoreSlim owner;

            public ReceiveProtocolClaim(SemaphoreSlim owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.Release();
            }
        }
    }

    internal static class ProductionDirectCandidateCollector
    {
        private const int MaximumCandidates = 16;

        public static IReadOnlyList<ProductionDirectUdpCandidateLease> GatherHostCandidates(
            DateTime expiresAtUtc)
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(value => value.OperationalStatus == OperationalStatus.Up &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(value =>
                {
                    try { return value.GetIPProperties().UnicastAddresses.Select(item => item.Address).ToArray(); }
                    catch (NetworkInformationException) { return Array.Empty<IPAddress>(); }
                });
            return BindHostCandidates(addresses, expiresAtUtc);
        }

        internal static IReadOnlyList<ProductionDirectUdpCandidateLease> BindHostCandidates(
            IEnumerable<IPAddress> addresses,
            DateTime expiresAtUtc)
        {
            if (addresses == null) throw new ArgumentNullException(nameof(addresses));
            if (expiresAtUtc <= DateTime.UtcNow || expiresAtUtc > DateTime.UtcNow.AddMinutes(2))
                throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
            var leases = new List<ProductionDirectUdpCandidateLease>();
            foreach (var address in addresses.Distinct().Where(IsAllowedHostAddress))
            {
                if (leases.Count >= MaximumCandidates) break;
                Socket socket = null;
                try
                {
                    socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    if (address.AddressFamily == AddressFamily.InterNetworkV6)
                        socket.DualMode = false;
                    socket.Bind(new IPEndPoint(address, 0));
                    var endpoint = (IPEndPoint)socket.LocalEndPoint;
                    var id = new byte[16];
                    RandomNumberGenerator.Fill(id);
                    var candidate = new DirectCandidateV1
                    {
                        CandidateId = ByteString.CopyFrom(id),
                        Kind = DirectCandidateKindV1.Host,
                        Carrier = DirectCarrierV1.UdpQuic,
                        IpAddress = ByteString.CopyFrom(endpoint.Address.GetAddressBytes()),
                        Port = checked((uint)endpoint.Port),
                        Priority = checked((uint)(2_000_000_000 - leases.Count)),
                        NetworkCost = 0,
                        ExpiresAtMs = new DateTimeOffset(expiresAtUtc).ToUnixTimeMilliseconds()
                    };
                    leases.Add(new ProductionDirectUdpCandidateLease(socket, candidate));
                    socket = null;
                }
                catch (SocketException)
                {
                    // An interface can disappear during collection. Retire only
                    // that candidate; the Relay and other interfaces remain usable.
                }
                finally { socket?.Dispose(); }
            }
            return leases;
        }

        internal static bool IsAllowedHostAddress(IPAddress address)
        {
            if (address == null || IPAddress.IsLoopback(address) ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
                address.IsIPv6Multicast || address.IsIPv6LinkLocal ||
                address.IsIPv6SiteLocal)
                return false;
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (bytes.Length != 4 || bytes[0] >= 224 || bytes[0] == 0 ||
                    (bytes[0] == 169 && bytes[1] == 254) ||
                    (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255))
                    return false;
                return true;
            }
            return address.AddressFamily == AddressFamily.InterNetworkV6 && bytes.Length == 16;
        }
    }

    internal static class ProductionStunClient
    {
        private const uint MagicCookie = 0x2112A442;
        private const ushort BindingRequest = 0x0001;
        private const ushort BindingSuccess = 0x0101;
        private const ushort XorMappedAddress = 0x0020;
        private const int MaximumResponseBytes = 576;

        public static async Task<IPEndPoint> QueryServerReflexiveAsync(
            ProductionDirectUdpCandidateLease lease,
            IPEndPoint allowlistedServer,
            TimeSpan timeout,
            CancellationToken token)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (allowlistedServer == null || !ProductionDirectCandidateCollector.IsAllowedHostAddress(
                allowlistedServer.Address) || allowlistedServer.Port < 1)
                throw new ArgumentException("A resolved allowlisted STUN endpoint is required.", nameof(allowlistedServer));
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(3))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            var transaction = new byte[12];
            RandomNumberGenerator.Fill(transaction);
            var request = CreateBindingRequest(transaction);
            var response = new byte[MaximumResponseBytes];
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            linked.CancelAfter(timeout);
            try
            {
                using var protocolClaim = await lease.ClaimReceiveProtocolAsync(
                    linked.Token).ConfigureAwait(false);
                await lease.Socket.SendToAsync(
                    request,
                    SocketFlags.None,
                    allowlistedServer,
                    linked.Token).ConfigureAwait(false);
                while (true)
                {
                    EndPoint remote = allowlistedServer.AddressFamily == AddressFamily.InterNetwork
                        ? new IPEndPoint(IPAddress.Any, 0)
                        : new IPEndPoint(IPAddress.IPv6Any, 0);
                    var received = await lease.Socket.ReceiveFromAsync(
                        response,
                        SocketFlags.None,
                        remote,
                        linked.Token).ConfigureAwait(false);
                    if (received.RemoteEndPoint is not IPEndPoint source ||
                        !source.Equals(allowlistedServer))
                        continue;
                    IPEndPoint mapped;
                    try
                    {
                        mapped = ParseBindingSuccess(
                            response.AsSpan(0, received.ReceivedBytes),
                            transaction);
                    }
                    catch (InvalidOperationException)
                    {
                        // A delayed response for a previous transaction is not
                        // authority for this query. Keep listening within the
                        // same bounded operation instead of retiring the lease.
                        continue;
                    }
                    if (!ProductionDirectCandidateCollector.IsAllowedHostAddress(mapped.Address) ||
                        mapped.Port < 1024)
                        throw new InvalidOperationException("STUN returned an unsafe mapped endpoint.");
                    return mapped;
                }
            }
            catch (OperationCanceledException error) when (
                !token.IsCancellationRequested && linked.IsCancellationRequested)
            {
                throw new TimeoutException("STUN Binding query timed out.", error);
            }
        }

        internal static byte[] CreateBindingRequest(byte[] transactionId)
        {
            if (transactionId == null || transactionId.Length != 12)
                throw new ArgumentException("A 12-byte STUN transaction ID is required.", nameof(transactionId));
            var request = new byte[20];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), BindingRequest);
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), MagicCookie);
            transactionId.CopyTo(request, 8);
            return request;
        }

        internal static IPEndPoint ParseBindingSuccess(
            ReadOnlySpan<byte> response,
            ReadOnlySpan<byte> expectedTransactionId)
        {
            if (expectedTransactionId.Length != 12 || response.Length < 20 ||
                response.Length > MaximumResponseBytes ||
                BinaryPrimitives.ReadUInt16BigEndian(response) != BindingSuccess ||
                BinaryPrimitives.ReadUInt32BigEndian(response.Slice(4, 4)) != MagicCookie ||
                !CryptographicOperations.FixedTimeEquals(
                    response.Slice(8, 12),
                    expectedTransactionId))
                throw new InvalidOperationException("STUN Binding response authentication failed.");
            var bodyLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2, 2));
            if ((bodyLength & 3) != 0 || bodyLength != response.Length - 20)
                throw new InvalidOperationException("STUN Binding response length is invalid.");
            var cursor = 20;
            while (cursor < response.Length)
            {
                if (response.Length - cursor < 4)
                    throw new InvalidOperationException("STUN attribute header is truncated.");
                var type = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(cursor, 2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(cursor + 2, 2));
                cursor += 4;
                if (length > response.Length - cursor)
                    throw new InvalidOperationException("STUN attribute is truncated.");
                if (type == XorMappedAddress)
                    return ParseXorMappedAddress(response.Slice(cursor, length), expectedTransactionId);
                cursor += (length + 3) & ~3;
            }
            throw new InvalidOperationException("STUN response omitted XOR-MAPPED-ADDRESS.");
        }

        private static IPEndPoint ParseXorMappedAddress(
            ReadOnlySpan<byte> value,
            ReadOnlySpan<byte> transactionId)
        {
            if (value.Length != 8 && value.Length != 20 || value[0] != 0)
                throw new InvalidOperationException("STUN XOR-MAPPED-ADDRESS is invalid.");
            var family = value[1];
            var port = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2)) ^
                (ushort)(MagicCookie >> 16);
            if (port == 0) throw new InvalidOperationException("STUN mapped port is invalid.");
            var encoded = value.Slice(4);
            var decoded = new byte[encoded.Length];
            Span<byte> mask = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mask, MagicCookie);
            transactionId.CopyTo(mask.Slice(4));
            for (var index = 0; index < decoded.Length; index++)
                decoded[index] = (byte)(encoded[index] ^ mask[index]);
            if (family == 0x01 && decoded.Length == 4)
                return new IPEndPoint(new IPAddress(decoded), port);
            if (family == 0x02 && decoded.Length == 16)
                return new IPEndPoint(new IPAddress(decoded), port);
            throw new InvalidOperationException("STUN address family is invalid.");
        }
    }

    /// <summary>
    /// Performs the authenticated, bounded UDP simultaneous-open phase on the
    /// exact candidate lease. A successful result proves only bidirectional UDP
    /// reachability; it does not nominate, authenticate, or cut over a QUIC path.
    /// </summary>
    internal static class ProductionDirectConnectivityProbe
    {
        private const int MaximumAttempts = 4;
        private static readonly TimeSpan MaximumPairBudget = TimeSpan.FromMilliseconds(1500);

        public static async Task<bool> ProbeAsync(
            ProductionDirectUdpCandidateLease lease,
            IPEndPoint remoteCandidate,
            DirectConnectivityDatagramV1 localProbe,
            Guid peerDeviceId,
            byte[] pairwiseKey,
            CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            using var budgetCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            budgetCancellation.CancelAfter(MaximumPairBudget);
            var budgetToken = budgetCancellation.Token;
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (remoteCandidate == null || remoteCandidate.Port < 1024 ||
                !ProductionDirectCandidateCollector.IsAllowedHostAddress(remoteCandidate.Address))
                throw new ArgumentException("A routable high-port remote candidate is required.", nameof(remoteCandidate));
            if (localProbe == null || localProbe.Kind != DirectConnectivityDatagramKindV1.Probe)
                throw new ArgumentException("An authenticated PROBE template is required.", nameof(localProbe));
            var localDeviceId = Guid.ParseExact(
                Convert.ToHexString(localProbe.SenderDeviceId.Span), "N");
            if (peerDeviceId == Guid.Empty || peerDeviceId == localDeviceId)
                throw new ArgumentException("A distinct peer device is required.", nameof(peerDeviceId));
            var packet = ProductionDirectTransportCrypto.CreateProbePacket(
                pairwiseKey,
                localProbe,
                peerDeviceId);
            var receiveBuffer = new byte[512];
            try
            {
                using var protocolClaim = await lease.ClaimReceiveProtocolAsync(
                    budgetToken).ConfigureAwait(false);
                for (var attempt = 0; attempt < MaximumAttempts; attempt++)
                {
                    budgetToken.ThrowIfCancellationRequested();
                    await lease.Socket.SendToAsync(
                        packet,
                        SocketFlags.None,
                        remoteCandidate,
                        budgetToken).ConfigureAwait(false);
                    var attemptBudget = TimeSpan.FromMilliseconds(150 * (1 << attempt));
                    var remaining = MaximumPairBudget - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) return false;
                    if (attemptBudget > remaining) attemptBudget = remaining;
                    var attemptElapsed = Stopwatch.StartNew();
                    while (attemptElapsed.Elapsed < attemptBudget)
                    {
                        var receiveBudget = attemptBudget - attemptElapsed.Elapsed;
                        if (receiveBudget <= TimeSpan.Zero) break;
                        using var receiveCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(budgetToken);
                        receiveCancellation.CancelAfter(receiveBudget);
                        EndPoint remote = remoteCandidate.AddressFamily == AddressFamily.InterNetwork
                            ? new IPEndPoint(IPAddress.Any, 0)
                            : new IPEndPoint(IPAddress.IPv6Any, 0);
                        SocketReceiveFromResult received;
                        try
                        {
                            received = await lease.Socket.ReceiveFromAsync(
                                receiveBuffer,
                                SocketFlags.None,
                                remote,
                                receiveCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (
                            !token.IsCancellationRequested &&
                            (receiveCancellation.IsCancellationRequested ||
                             budgetCancellation.IsCancellationRequested))
                        {
                            break;
                        }
                        if (received.RemoteEndPoint is not IPEndPoint source ||
                            !source.Equals(remoteCandidate))
                            continue;
                        DirectConnectivityDatagramV1 incoming;
                        try
                        {
                            incoming = ProductionDirectTransportCrypto.ValidateProbePacket(
                                pairwiseKey,
                                receiveBuffer.AsSpan(0, received.ReceivedBytes),
                                peerDeviceId,
                                localDeviceId,
                                Guid.ParseExact(Convert.ToHexString(localProbe.ConnectionId.Span), "N"),
                                localProbe.PathEpoch,
                                DateTime.UtcNow);
                        }
                        catch (Exception error) when (
                            error is InvalidDataException || error is CryptographicException)
                        {
                            continue;
                        }
                        if (!incoming.TicketId.Span.SequenceEqual(localProbe.TicketId.Span) ||
                            !incoming.NominationId.Span.SequenceEqual(localProbe.NominationId.Span) ||
                            !incoming.Pair.Equals(localProbe.Pair))
                            continue;
                        if (incoming.Kind == DirectConnectivityDatagramKindV1.ProbeAck &&
                            incoming.ProbeNonce.Span.SequenceEqual(localProbe.ProbeNonce.Span))
                            return true;
                        if (incoming.Kind != DirectConnectivityDatagramKindV1.Probe) continue;
                        var ack = BuildProbeAck(incoming, localDeviceId);
                        var ackPacket = ProductionDirectTransportCrypto.CreateProbePacket(
                            pairwiseKey,
                            ack,
                            peerDeviceId);
                        try
                        {
                            // Never amplify an unaffiliated UDP source. Even a
                            // future protobuf/timestamp size change must keep the
                            // authenticated response no larger than this request.
                            if (ackPacket.Length > received.ReceivedBytes) continue;
                            await lease.Socket.SendToAsync(
                                ackPacket,
                                SocketFlags.None,
                                remoteCandidate,
                                budgetToken).ConfigureAwait(false);
                        }
                        finally { CryptographicOperations.ZeroMemory(ackPacket); }
                    }
                }
                return false;
            }
            catch (OperationCanceledException) when (
                !token.IsCancellationRequested && budgetCancellation.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(packet);
                CryptographicOperations.ZeroMemory(receiveBuffer);
            }
        }

        private static DirectConnectivityDatagramV1 BuildProbeAck(
            DirectConnectivityDatagramV1 remoteProbe,
            Guid localDeviceId)
        {
            return new DirectConnectivityDatagramV1
            {
                ProtocolVersion = 1,
                Kind = DirectConnectivityDatagramKindV1.ProbeAck,
                ConnectionId = remoteProbe.ConnectionId,
                PathEpoch = remoteProbe.PathEpoch,
                TicketId = remoteProbe.TicketId,
                NominationId = remoteProbe.NominationId,
                Pair = remoteProbe.Pair.Clone(),
                SenderDeviceId = ByteString.CopyFrom(Convert.FromHexString(localDeviceId.ToString("N"))),
                ProbeNonce = remoteProbe.ProbeNonce,
                ResponseNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
    }
}
