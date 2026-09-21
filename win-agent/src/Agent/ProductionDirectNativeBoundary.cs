using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TermLink.Agent
{
    internal enum ProductionDirectNativeState : uint
    {
        RelayOnly = 1,
        Gathering = 2,
        Probing = 3,
        Authenticating = 4,
        ShadowDirect = 5
    }

    internal enum ProductionDirectNativeReason : uint
    {
        None = 0,
        Disabled = 1,
        InvalidAbi = 2,
        InvalidEpoch = 3,
        InvalidPlatform = 4,
        InvalidCandidate = 5,
        InvalidTicket = 6,
        Socket = 7,
        Probe = 8,
        Quic = 9,
        PeerProof = 10,
        SignatureTimeout = 11,
        Cancelled = 12,
        NetworkChanged = 13,
        QueueFull = 14,
        Internal = 15,
        InvalidPlan = 16,
        NominationRejected = 17
    }

    internal enum ProductionDirectNativeFault
    {
        None,
        Disabled,
        NativeUnavailable,
        AbiMismatch,
        InvalidRequest,
        QueueFull,
        WorkerStopped,
        NativePanic,
        Cancelled,
        NetworkChanged,
        Internal
    }

    internal enum ProductionDirectNativeCommandResult
    {
        Accepted,
        IgnoredStale,
        RelayFallback
    }

    internal enum ProductionDirectNativeRole : uint
    {
        Initiator = 1,
        Responder = 2
    }

    internal enum ProductionDirectNativeEventKind : uint
    {
        IdentityReady = 1,
        StateChanged = 2,
        DirectFailed = 3,
        SignRequest = 4,
        ShadowReady = 5,
        IgnoredStale = 6,
        CandidatesReady = 7,
        RenewalDue = 13
    }

    internal sealed class ProductionDirectNativeSnapshot
    {
        public ProductionDirectNativeState State { get; init; }
        public ProductionDirectNativeReason Reason { get; init; }
        public ProductionDirectNativeFault Fault { get; init; }
        public ulong Epoch { get; init; }
        public Guid AttemptId { get; init; }
        public ulong NativeGeneration { get; init; }
        public bool NativeAvailable { get; init; }
        public bool Disposed { get; init; }
        public bool DirectRoutingPermitted => false;
    }

    internal sealed class ProductionDirectNativeEndpoint
    {
        public ProductionDirectNativeEndpoint(IPAddress address, int port)
        {
            Address = address ?? throw new ArgumentNullException(nameof(address));
            if (port is < 0 or > 65_535) throw new ArgumentOutOfRangeException(nameof(port));
            Port = port;
        }

        public IPAddress Address { get; }
        public int Port { get; }
    }

    internal sealed class ProductionDirectNativeStartRequest
    {
        public ulong Epoch { get; init; }
        public Guid AttemptId { get; init; }
        public Guid ConnectionId { get; init; }
        public Guid LocalDeviceId { get; init; }
        public Guid PeerDeviceId { get; init; }
        public ProductionDirectNativeRole Role { get; init; }
        public uint TimeoutMilliseconds { get; init; }
        public ProductionDirectNativeEndpoint LocalBind { get; init; }
        public ProductionDirectNativeEndpoint RemoteCandidate { get; init; }
        public IReadOnlyList<ProductionDirectNativeEndpoint> StunServers { get; init; } =
            Array.Empty<ProductionDirectNativeEndpoint>();
        public byte[] Ticket { get; init; }
        public byte[] ServerSigningPublicKey { get; init; }
        public byte[] PeerCertificateSha256 { get; init; }
        public byte[] PeerDeviceSigningPublicKey { get; init; }
        public byte[] LocalSendProbeHmacKey { get; init; }
        public byte[] PeerReceiveProbeHmacKey { get; init; }
        public Guid TicketId { get; init; }
        public Guid NominationId { get; init; }
        public Guid InitiatorCandidateId { get; init; }
        public Guid ResponderCandidateId { get; init; }
        public byte[] LocalProbeNonce { get; init; }
        public byte[] PeerProbeNonce { get; init; }
        public byte[] LocalProofNonce { get; init; }
        public long NowMilliseconds { get; init; }
    }

    internal sealed class ProductionDirectNativeGatherRequest
    {
        public ulong Epoch { get; init; }
        public Guid AttemptId { get; init; }
        public ulong NetworkHandle { get; init; }
        public ProductionDirectNativeEndpoint LocalBind { get; init; }
        public IPAddress AdvertisedLocalAddress { get; init; }
        public IReadOnlyList<ProductionDirectNativeEndpoint> StunServers { get; init; } =
            Array.Empty<ProductionDirectNativeEndpoint>();
        public uint GatherTimeoutMilliseconds { get; init; } = 1_000;
    }

    internal sealed class ProductionDirectNativeEvent
    {
        public ProductionDirectNativeEventKind Kind { get; init; }
        public ProductionDirectNativeState State { get; init; }
        public ProductionDirectNativeReason Reason { get; init; }
        public ulong Epoch { get; init; }
        public ulong Generation { get; init; }
        public ulong RequestId { get; init; }
        public Guid AttemptId { get; init; }
        public ProductionDirectNativeEndpoint Endpoint { get; init; }
        public byte[] Payload { get; init; }
        public IReadOnlyList<ProductionDirectNativeEndpoint> Candidates { get; init; } =
            Array.Empty<ProductionDirectNativeEndpoint>();
    }

    /// <summary>
    /// Strict owned-copy CLR boundary for termlink-direct-ffi ABI v3. This type
    /// is shadow-only: every native fault retires its handle synchronously and
    /// DirectRoutingPermitted is permanently false. It has no dependency on any
    /// Relay socket, terminal process, renderer, Envelope or durable ACK store.
    /// </summary>
    internal sealed class ProductionDirectNativeBoundary : IDisposable
    {
        private readonly object sync = new();
        private readonly bool enabled;
        private readonly uint queueCapacity;
        private readonly IProductionDirectNativeLoader loader;
        private readonly HashSet<PendingSignature> pendingSignatures = new();
        private IProductionDirectNativeApi api;
        private IntPtr handle;
        private ulong authoritativeEpoch;
        private Guid currentAttempt;
        private ulong lastGeneration;
        private ulong candidatesReadyGeneration;
        private IReadOnlyList<ProductionDirectNativeEndpoint> gatheredCandidates =
            Array.Empty<ProductionDirectNativeEndpoint>();
        private bool candidatesExchanged;
        private bool nominationSubmitted;
        private bool disposed;
        private ProductionDirectNativeSnapshot snapshot;

        public ProductionDirectNativeBoundary(
            bool enabled = false,
            uint queueCapacity = 32,
            IProductionDirectNativeLoader loader = null)
        {
            this.enabled = enabled;
            this.queueCapacity = queueCapacity;
            this.loader = enabled
                ? loader ?? new ProductionDirectNativeLibraryLoader()
                : loader;
            snapshot = RelaySnapshot(
                enabled ? ProductionDirectNativeFault.None : ProductionDirectNativeFault.Disabled,
                enabled ? ProductionDirectNativeReason.None : ProductionDirectNativeReason.Disabled,
                nativeAvailable: false);
            if (!enabled) return;
            lock (sync)
            {
                if (queueCapacity is < 1 or > ProductionDirectNativeAbiV2.MaximumQueueCapacity)
                {
                    FallbackLocked(ProductionDirectNativeFault.InvalidRequest,
                        ProductionDirectNativeReason.InvalidAbi, retireHandle: true);
                    return;
                }
                if (!ProductionDirectNativeAbiV3.TryValidateLayout(out _))
                {
                    FallbackLocked(ProductionDirectNativeFault.AbiMismatch,
                        ProductionDirectNativeReason.InvalidAbi, retireHandle: true);
                    return;
                }
                try
                {
                    if (!this.loader.TryLoad(out api, out _) || api == null)
                    {
                        FallbackLocked(ProductionDirectNativeFault.NativeUnavailable,
                            ProductionDirectNativeReason.Internal, retireHandle: true);
                        return;
                    }
                    if (api.AbiVersion() != ProductionDirectNativeAbiV3.AbiVersion)
                    {
                        FallbackLocked(ProductionDirectNativeFault.AbiMismatch,
                            ProductionDirectNativeReason.InvalidAbi, retireHandle: true);
                        return;
                    }
                    if (!CreateHandleLocked()) return;
                    snapshot = RelaySnapshot(ProductionDirectNativeFault.None,
                        ProductionDirectNativeReason.None, nativeAvailable: true);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    FallbackLocked(ProductionDirectNativeFault.NativeUnavailable,
                        ProductionDirectNativeReason.Internal, retireHandle: true);
                }
            }
        }

        public event Action<ProductionDirectNativeSnapshot> StateChanged;

        public ProductionDirectNativeSnapshot Snapshot
        {
            get { lock (sync) return snapshot; }
        }

        public ProductionDirectNativeCommandResult Gather(ProductionDirectNativeGatherRequest request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (request == null || !ValidEpoch(request.Epoch) || request.AttemptId == Guid.Empty)
                    return InvalidRequestLocked();
                if (request.Epoch <= authoritativeEpoch)
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                TermLinkDirectGatherV3 native;
                try { native = EncodeGather(request); }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException or OverflowException)
                {
                    return InvalidRequestLocked();
                }
                authoritativeEpoch = request.Epoch;
                currentAttempt = request.AttemptId;
                pendingSignatures.Clear();
                ResetCandidateExchangeLocked();
                snapshot = RelaySnapshot(ProductionDirectNativeFault.None,
                    ProductionDirectNativeReason.None, nativeAvailable: true);
                PublishLocked();
                try
                {
                    var status = api.Gather(handle, ref native);
                    return InterpretStatusLocked(status);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    FallbackLocked(ProductionDirectNativeFault.NativePanic,
                        ProductionDirectNativeReason.Internal, retireHandle: true);
                    return ProductionDirectNativeCommandResult.RelayFallback;
                }
                finally { ZeroGather(ref native); }
            }
        }

        public ProductionDirectNativeCommandResult Nominate(ProductionDirectNativeStartRequest request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (request == null || !IsCurrentLocked(request.Epoch, request.AttemptId))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                if (!candidatesExchanged || nominationSubmitted)
                    return InvalidRequestLocked();
                TermLinkDirectNominateV3 native;
                try
                {
                    native = new TermLinkDirectNominateV3
                    {
                        AbiVersion = ProductionDirectNativeAbiV3.AbiVersion,
                        StructSize = ProductionDirectNativeAbiV3.NominateSize,
                        StartV2 = EncodeStart(request),
                        LocalSendProbeHmacKey = ExactCopy(request.LocalSendProbeHmacKey, 32),
                        PeerReceiveProbeHmacKey = ExactCopy(request.PeerReceiveProbeHmacKey, 32),
                        Reserved = new ulong[4]
                    };
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException or OverflowException)
                {
                    return InvalidRequestLocked();
                }
                try
                {
                    var result = InterpretStatusLocked(api.Nominate(handle, ref native));
                    if (result == ProductionDirectNativeCommandResult.Accepted)
                        nominationSubmitted = true;
                    return result;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    FallbackLocked(ProductionDirectNativeFault.NativePanic,
                        ProductionDirectNativeReason.Internal, retireHandle: true);
                    return ProductionDirectNativeCommandResult.RelayFallback;
                }
                finally { ZeroNominate(ref native); }
            }
        }

        /// <summary>
        /// Records that the exact CandidatesReady snapshot was accepted by the
        /// authenticated signalling lane. This is a managed orchestration fence;
        /// it deliberately does not add another native ABI command.
        /// </summary>
        public ProductionDirectNativeCommandResult MarkCandidatesExchanged(
            ulong epoch,
            Guid attemptId,
            ulong generation,
            IReadOnlyList<ProductionDirectNativeEndpoint> candidates)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (!IsCurrentLocked(epoch, attemptId))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                if (generation == 0 || generation != candidatesReadyGeneration ||
                    nominationSubmitted || !EndpointsEqual(candidates, gatheredCandidates))
                    return InvalidRequestLocked();
                candidatesExchanged = true;
                return ProductionDirectNativeCommandResult.Accepted;
            }
        }

        public ProductionDirectNativeCommandResult Cancel(ulong epoch, Guid attemptId)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (!IsCurrentLocked(epoch, attemptId))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                var native = Control(epoch, attemptId);
                FallbackLocked(ProductionDirectNativeFault.Cancelled,
                    ProductionDirectNativeReason.Cancelled, retireHandle: false);
                try
                {
                    var status = api.Cancel(handle, ref native);
                    var result = InterpretStatusLocked(status);
                    if (result == ProductionDirectNativeCommandResult.Accepted)
                        FallbackLocked(ProductionDirectNativeFault.Cancelled,
                            ProductionDirectNativeReason.Cancelled, retireHandle: true);
                    return result;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    FallbackLocked(ProductionDirectNativeFault.NativePanic,
                        ProductionDirectNativeReason.Internal, retireHandle: true);
                    return ProductionDirectNativeCommandResult.RelayFallback;
                }
            }
        }

        public ProductionDirectNativeCommandResult NetworkChanged(ulong epoch, Guid attemptId)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (!IsCurrentLocked(epoch, attemptId))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                var native = Control(epoch, attemptId);
                FallbackLocked(ProductionDirectNativeFault.NetworkChanged,
                    ProductionDirectNativeReason.NetworkChanged, retireHandle: false);
                try
                {
                    var status = api.NetworkChanged(handle, ref native);
                    var result = InterpretStatusLocked(status);
                    if (result == ProductionDirectNativeCommandResult.Accepted)
                        FallbackLocked(ProductionDirectNativeFault.NetworkChanged,
                            ProductionDirectNativeReason.NetworkChanged, retireHandle: true);
                    return result;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    FallbackLocked(ProductionDirectNativeFault.NativePanic,
                        ProductionDirectNativeReason.Internal, retireHandle: true);
                    return ProductionDirectNativeCommandResult.RelayFallback;
                }
            }
        }

        public ProductionDirectNativeCommandResult SubmitSignature(
            ulong epoch,
            Guid attemptId,
            ulong requestId,
            byte[] signature)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                var pending = new PendingSignature(epoch, attemptId, requestId);
                if (!IsCurrentLocked(epoch, attemptId) || requestId == 0 ||
                    !pendingSignatures.Contains(pending))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                if (signature == null || signature.Length is < 1 or > ProductionDirectNativeAbiV2.MaximumSignatureBytes)
                    return InvalidRequestLocked();
                var native = new TermLinkDirectSignatureV2
                {
                    AbiVersion = ProductionDirectNativeAbiV3.AbiVersion,
                    StructSize = ProductionDirectNativeAbiV2.SignatureSize,
                    Epoch = epoch,
                    AttemptId = GuidBytes(attemptId),
                    RequestId = requestId,
                    SignatureLength = checked((uint)signature.Length),
                    Signature = PaddedCopy(signature, ProductionDirectNativeAbiV2.MaximumSignatureBytes)
                };
                pendingSignatures.Remove(pending);
                try
                {
                    return InterpretStatusLocked(api.SubmitSignature(handle, ref native));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    FallbackLocked(ProductionDirectNativeFault.NativePanic,
                        ProductionDirectNativeReason.Internal, retireHandle: true);
                    return ProductionDirectNativeCommandResult.RelayFallback;
                }
                finally { CryptographicOperations.ZeroMemory(native.Signature); }
            }
        }

        public IReadOnlyList<ProductionDirectNativeEvent> Poll(int maximumEvents = 32)
        {
            if (maximumEvents is < 1 or > ProductionDirectNativeAbiV2.MaximumQueueCapacity)
                throw new ArgumentOutOfRangeException(nameof(maximumEvents));
            lock (sync)
            {
                if (!CanCallLocked()) return Array.Empty<ProductionDirectNativeEvent>();
                var result = new List<ProductionDirectNativeEvent>(maximumEvents);
                for (var index = 0; index < maximumEvents; index++)
                {
                    var native = ProductionDirectNativeAbiV3.NewEventBuffer();
                    int status;
                    try { status = api.PollEvent(handle, ref native); }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        FallbackLocked(ProductionDirectNativeFault.NativePanic,
                            ProductionDirectNativeReason.Internal, retireHandle: true);
                        break;
                    }
                    if (status == 0) break;
                    if (status != 1)
                    {
                        InterpretStatusLocked(status);
                        break;
                    }
                    if (!TryDecodeEventLocked(native, out var decoded))
                    {
                        FallbackLocked(ProductionDirectNativeFault.AbiMismatch,
                            ProductionDirectNativeReason.InvalidAbi, retireHandle: true);
                        CryptographicOperations.ZeroMemory(native.Payload);
                        break;
                    }
                    CryptographicOperations.ZeroMemory(native.Payload);
                    if (decoded.Kind != ProductionDirectNativeEventKind.IgnoredStale &&
                        decoded.Kind != ProductionDirectNativeEventKind.IdentityReady &&
                        decoded.Kind != ProductionDirectNativeEventKind.RenewalDue &&
                        !IsCurrentLocked(decoded.Epoch, decoded.AttemptId))
                    {
                        result.Add(new ProductionDirectNativeEvent
                        {
                            Kind = ProductionDirectNativeEventKind.IgnoredStale,
                            State = ProductionDirectNativeState.RelayOnly,
                            Epoch = decoded.Epoch,
                            Generation = decoded.Generation,
                            AttemptId = decoded.AttemptId,
                            Payload = Array.Empty<byte>()
                        });
                        continue;
                    }
                    ApplyEventLocked(decoded);
                    result.Add(decoded);
                    if (handle == IntPtr.Zero) break;
                }
                return result.AsReadOnly();
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                pendingSignatures.Clear();
                ResetCandidateExchangeLocked();
                RetireHandleLocked();
                snapshot = new ProductionDirectNativeSnapshot
                {
                    State = ProductionDirectNativeState.RelayOnly,
                    Reason = ProductionDirectNativeReason.Cancelled,
                    Fault = ProductionDirectNativeFault.Cancelled,
                    Epoch = authoritativeEpoch,
                    AttemptId = currentAttempt,
                    NativeGeneration = lastGeneration,
                    NativeAvailable = false,
                    Disposed = true
                };
                PublishLocked();
            }
        }

        private bool CreateHandleLocked()
        {
            if (handle != IntPtr.Zero) return true;
            if (api == null) return false;
            var create = new TermLinkDirectCreateV3
            {
                AbiVersion = ProductionDirectNativeAbiV3.AbiVersion,
                StructSize = ProductionDirectNativeAbiV3.CreateSize,
                Enabled = 1,
                QueueCapacity = queueCapacity,
                Platform = ProductionDirectNativeAbiV3.PlatformWindows,
                Reserved = new uint[3]
            };
            handle = api.Create(ref create);
            lastGeneration = 0;
            pendingSignatures.Clear();
            if (handle != IntPtr.Zero) return true;
            FallbackLocked(ProductionDirectNativeFault.NativeUnavailable,
                ProductionDirectNativeReason.Internal, retireHandle: false);
            return false;
        }

        private bool CanCallLocked()
        {
            if (!enabled || disposed || api == null) return false;
            if (handle != IntPtr.Zero) return true;
            try { return CreateHandleLocked(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                FallbackLocked(ProductionDirectNativeFault.NativeUnavailable,
                    ProductionDirectNativeReason.Internal, retireHandle: true);
                return false;
            }
        }

        private ProductionDirectNativeCommandResult InterpretStatusLocked(int status)
        {
            if (status == 0) return ProductionDirectNativeCommandResult.Accepted;
            var fault = status switch
            {
                -1 => ProductionDirectNativeFault.WorkerStopped,
                -2 => ProductionDirectNativeFault.AbiMismatch,
                -3 => ProductionDirectNativeFault.NativePanic,
                -4 => ProductionDirectNativeFault.QueueFull,
                _ => ProductionDirectNativeFault.Internal
            };
            var reason = status switch
            {
                -2 => ProductionDirectNativeReason.InvalidAbi,
                -4 => ProductionDirectNativeReason.QueueFull,
                _ => ProductionDirectNativeReason.Internal
            };
            FallbackLocked(fault, reason, retireHandle: true);
            return ProductionDirectNativeCommandResult.RelayFallback;
        }

        private void ApplyEventLocked(ProductionDirectNativeEvent value)
        {
            switch (value.Kind)
            {
                case ProductionDirectNativeEventKind.IdentityReady:
                    break;
                case ProductionDirectNativeEventKind.StateChanged:
                    snapshot = new ProductionDirectNativeSnapshot
                    {
                        State = value.State,
                        Reason = value.Reason,
                        Fault = ProductionDirectNativeFault.None,
                        Epoch = authoritativeEpoch,
                        AttemptId = currentAttempt,
                        NativeGeneration = value.Generation,
                        NativeAvailable = true
                    };
                    PublishLocked();
                    break;
                case ProductionDirectNativeEventKind.DirectFailed:
                    FallbackLocked(FaultForReason(value.Reason), value.Reason, retireHandle: true);
                    break;
                case ProductionDirectNativeEventKind.SignRequest:
                    if (value.RequestId == 0 || value.Payload.Length == 0)
                    {
                        FallbackLocked(ProductionDirectNativeFault.AbiMismatch,
                            ProductionDirectNativeReason.InvalidAbi, retireHandle: true);
                        return;
                    }
                    pendingSignatures.Add(new PendingSignature(
                        value.Epoch, value.AttemptId, value.RequestId));
                    snapshot = new ProductionDirectNativeSnapshot
                    {
                        State = ProductionDirectNativeState.Authenticating,
                        Fault = ProductionDirectNativeFault.None,
                        Epoch = authoritativeEpoch,
                        AttemptId = currentAttempt,
                        NativeGeneration = value.Generation,
                        NativeAvailable = true
                    };
                    PublishLocked();
                    break;
                case ProductionDirectNativeEventKind.ShadowReady:
                    snapshot = new ProductionDirectNativeSnapshot
                    {
                        State = ProductionDirectNativeState.ShadowDirect,
                        Fault = ProductionDirectNativeFault.None,
                        Epoch = authoritativeEpoch,
                        AttemptId = currentAttempt,
                        NativeGeneration = value.Generation,
                        NativeAvailable = true
                    };
                    PublishLocked();
                    break;
                case ProductionDirectNativeEventKind.CandidatesReady:
                    candidatesReadyGeneration = value.Generation;
                    gatheredCandidates = value.Candidates
                        .Select(endpoint => new ProductionDirectNativeEndpoint(endpoint.Address, endpoint.Port))
                        .ToArray();
                    candidatesExchanged = false;
                    nominationSubmitted = false;
                    break;
                case ProductionDirectNativeEventKind.IgnoredStale:
                    break;
            }
        }

        private bool TryDecodeEventLocked(
            TermLinkDirectEventV2 native,
            out ProductionDirectNativeEvent result)
        {
            result = null;
            if (native.AbiVersion != ProductionDirectNativeAbiV3.AbiVersion ||
                native.StructSize != ProductionDirectNativeAbiV2.EventSize ||
                native.Kind is < 1 or > 13 || native.State is < 1 or > 5 || native.Reason > 15 ||
                native.PayloadLength > ProductionDirectNativeAbiV2.MaximumEventPayloadBytes ||
                native.AttemptId == null || native.AttemptId.Length != 16 ||
                native.Payload == null || native.Payload.Length != ProductionDirectNativeAbiV2.MaximumEventPayloadBytes ||
                native.Endpoint.Ip == null || native.Endpoint.Ip.Length != 16 ||
                native.Generation == 0 || native.Generation <= lastGeneration)
                return false;
            if (!TryDecodeEndpoint(native.Endpoint, out var endpoint)) return false;
            var endpointEmpty = endpoint == null;
            var attemptEmpty = native.AttemptId.All(value => value == 0);
            var trailingPayloadIsZero = native.Payload
                .Skip(checked((int)native.PayloadLength))
                .All(value => value == 0);
            if (!trailingPayloadIsZero || native.Kind switch
                {
                    (uint)ProductionDirectNativeEventKind.IdentityReady =>
                        native.State != (uint)ProductionDirectNativeState.Gathering ||
                        native.Reason != 0 || native.Epoch == 0 || native.RequestId != 0 ||
                        attemptEmpty || !endpointEmpty || native.PayloadLength == 0,
                    (uint)ProductionDirectNativeEventKind.StateChanged =>
                        native.Reason != 0 || native.Epoch == 0 || native.RequestId != 0 ||
                        attemptEmpty || !endpointEmpty || native.PayloadLength != 0,
                    (uint)ProductionDirectNativeEventKind.DirectFailed =>
                        native.State != (uint)ProductionDirectNativeState.RelayOnly ||
                        native.Reason is < 1 or > 15 || native.Epoch == 0 ||
                        native.RequestId != 0 || attemptEmpty || !endpointEmpty ||
                        native.PayloadLength != 0,
                    (uint)ProductionDirectNativeEventKind.SignRequest =>
                        native.State != (uint)ProductionDirectNativeState.Authenticating ||
                        native.Reason != 0 || native.Epoch == 0 || native.RequestId == 0 ||
                        attemptEmpty || !endpointEmpty || native.PayloadLength == 0,
                    (uint)ProductionDirectNativeEventKind.ShadowReady =>
                        native.State != (uint)ProductionDirectNativeState.ShadowDirect ||
                        native.Reason != 0 || native.Epoch == 0 || native.RequestId != 0 ||
                        attemptEmpty || endpointEmpty || native.PayloadLength != 32,
                    (uint)ProductionDirectNativeEventKind.IgnoredStale =>
                        native.State != (uint)ProductionDirectNativeState.RelayOnly ||
                        native.Reason != 0 || native.RequestId == 0 || attemptEmpty ||
                        !endpointEmpty || native.PayloadLength != 0,
                    (uint)ProductionDirectNativeEventKind.CandidatesReady =>
                        native.State != (uint)ProductionDirectNativeState.Gathering ||
                        native.Reason != 0 || native.Epoch == 0 || native.RequestId != 0 ||
                        attemptEmpty || endpointEmpty ||
                        native.PayloadLength != ProductionDirectNativeAbiV3.CandidatesSize,
                    (uint)ProductionDirectNativeEventKind.RenewalDue =>
                        native.Reason != 0 || native.Epoch == 0 ||
                        native.RequestId != 0 || attemptEmpty || !endpointEmpty ||
                        native.PayloadLength != 0,
                    _ => true
                })
                return false;
            IReadOnlyList<ProductionDirectNativeEndpoint> candidates =
                Array.Empty<ProductionDirectNativeEndpoint>();
            if (native.Kind == (uint)ProductionDirectNativeEventKind.CandidatesReady &&
                !TryDecodeCandidates(native.Payload, endpoint, out candidates))
                return false;
            lastGeneration = native.Generation;
            var payload = new byte[native.PayloadLength];
            Array.Copy(native.Payload, payload, payload.Length);
            result = new ProductionDirectNativeEvent
            {
                Kind = (ProductionDirectNativeEventKind)native.Kind,
                State = (ProductionDirectNativeState)native.State,
                Reason = (ProductionDirectNativeReason)native.Reason,
                Epoch = native.Epoch,
                Generation = native.Generation,
                RequestId = native.RequestId,
                AttemptId = GuidFromBytes(native.AttemptId),
                Endpoint = endpoint,
                Payload = payload,
                Candidates = candidates
            };
            return true;
        }

        private static bool TryDecodeCandidates(
            byte[] payload,
            ProductionDirectNativeEndpoint boundEndpoint,
            out IReadOnlyList<ProductionDirectNativeEndpoint> result)
        {
            result = Array.Empty<ProductionDirectNativeEndpoint>();
            if (payload == null || payload.Length < ProductionDirectNativeAbiV3.CandidatesSize ||
                boundEndpoint == null)
                return false;
            var exact = payload.Take(ProductionDirectNativeAbiV3.CandidatesSize).ToArray();
            var memory = IntPtr.Zero;
            try
            {
                memory = Marshal.AllocHGlobal(exact.Length);
                Marshal.Copy(exact, 0, memory, exact.Length);
                var native = Marshal.PtrToStructure<TermLinkDirectCandidatesV3>(memory);
                if (native.Reserved != 0 || native.CandidateCount is < 1 or > ProductionDirectNativeAbiV3.MaximumCandidates ||
                    native.Candidates == null || native.Candidates.Length != ProductionDirectNativeAbiV3.MaximumCandidates)
                    return false;
                var decoded = new List<ProductionDirectNativeEndpoint>(checked((int)native.CandidateCount));
                for (var index = 0; index < native.Candidates.Length; index++)
                {
                    var encoded = native.Candidates[index];
                    if (index >= native.CandidateCount)
                    {
                        if (encoded.Ip == null || encoded.Ip.Any(value => value != 0) ||
                            encoded.IpLength != 0 || encoded.Port != 0)
                            return false;
                        continue;
                    }
                    if (!TryDecodeEndpoint(encoded, out var endpoint) || endpoint == null || endpoint.Port < 1_024)
                        return false;
                    if (decoded.Any(value => value.Port == endpoint.Port && value.Address.Equals(endpoint.Address)))
                        return false;
                    decoded.Add(endpoint);
                }
                if (!decoded[0].Address.Equals(boundEndpoint.Address) || decoded[0].Port != boundEndpoint.Port)
                    return false;
                result = decoded.AsReadOnly();
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exact);
                if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
            }
        }

        private static TermLinkDirectGatherV3 EncodeGather(ProductionDirectNativeGatherRequest value)
        {
            if (!ValidEpoch(value.Epoch) || value.AttemptId == Guid.Empty || value.NetworkHandle != 0 ||
                value.LocalBind == null || value.AdvertisedLocalAddress == null || value.StunServers == null ||
                value.StunServers.Count > ProductionDirectNativeAbiV2.MaximumStunServers ||
                value.StunServers.Any(endpoint => endpoint == null) ||
                value.GatherTimeoutMilliseconds is < 100 or > 5_000)
                throw new ArgumentException("Direct native gather metadata is invalid.");
            var advertised = value.AdvertisedLocalAddress.GetAddressBytes();
            if (advertised.Length is not 4 and not 16 ||
                value.LocalBind.Address.AddressFamily != value.AdvertisedLocalAddress.AddressFamily)
                throw new ArgumentException("Direct native gather address family is invalid.");
            // Reuse endpoint validation for the advertised address while retaining port zero.
            _ = EncodeEndpoint(new ProductionDirectNativeEndpoint(value.AdvertisedLocalAddress, 0), allowZeroPort: true);
            var stuns = Enumerable.Range(0, ProductionDirectNativeAbiV2.MaximumStunServers)
                .Select(_ => TermLinkSocketAddressV2.Empty())
                .ToArray();
            for (var index = 0; index < value.StunServers.Count; index++)
                stuns[index] = EncodeEndpoint(value.StunServers[index], allowZeroPort: false);
            var advertisedPadded = new byte[16];
            Array.Copy(advertised, advertisedPadded, advertised.Length);
            return new TermLinkDirectGatherV3
            {
                AbiVersion = ProductionDirectNativeAbiV3.AbiVersion,
                StructSize = ProductionDirectNativeAbiV3.GatherSize,
                Epoch = value.Epoch,
                AttemptId = GuidBytes(value.AttemptId),
                NetworkHandle = 0,
                LocalBind = EncodeEndpoint(value.LocalBind, allowZeroPort: true),
                AdvertisedLocalIp = advertisedPadded,
                AdvertisedLocalIpLength = checked((uint)advertised.Length),
                StunCount = checked((uint)value.StunServers.Count),
                StunServers = stuns,
                GatherTimeoutMilliseconds = value.GatherTimeoutMilliseconds,
                Reserved = new ulong[4]
            };
        }

        private static TermLinkDirectStartV2 EncodeStart(ProductionDirectNativeStartRequest value)
        {
            ValidateStart(value);
            var stuns = new TermLinkSocketAddressV2[ProductionDirectNativeAbiV2.MaximumStunServers];
            for (var index = 0; index < stuns.Length; index++)
                stuns[index] = index < value.StunServers.Count
                    ? EncodeEndpoint(value.StunServers[index], allowZeroPort: false)
                    : TermLinkSocketAddressV2.Empty();
            return new TermLinkDirectStartV2
            {
                AbiVersion = ProductionDirectNativeAbiV2.AbiVersion,
                StructSize = ProductionDirectNativeAbiV2.StartSize,
                Epoch = value.Epoch,
                AttemptId = GuidBytes(value.AttemptId),
                ConnectionId = GuidBytes(value.ConnectionId),
                LocalDeviceId = GuidBytes(value.LocalDeviceId),
                PeerDeviceId = GuidBytes(value.PeerDeviceId),
                Role = (uint)value.Role,
                TimeoutMilliseconds = value.TimeoutMilliseconds,
                NetworkHandle = 0,
                LocalBind = EncodeEndpoint(value.LocalBind, allowZeroPort: false),
                RemoteCandidate = EncodeEndpoint(value.RemoteCandidate, allowZeroPort: false),
                StunCount = 0,
                StunServers = stuns,
                TicketLength = checked((uint)value.Ticket.Length),
                Ticket = PaddedCopy(value.Ticket, ProductionDirectNativeAbiV2.MaximumTicketBytes),
                ServerSigningPublicKey = ExactCopy(value.ServerSigningPublicKey, 32),
                PeerCertificateSha256 = ExactCopy(value.PeerCertificateSha256, 32),
                PeerDeviceSigningPublicKey = ExactCopy(value.PeerDeviceSigningPublicKey, 32),
                ProbeHmacKey = ExactCopy(value.LocalSendProbeHmacKey, 32),
                TicketId = GuidBytes(value.TicketId),
                NominationId = GuidBytes(value.NominationId),
                InitiatorCandidateId = GuidBytes(value.InitiatorCandidateId),
                ResponderCandidateId = GuidBytes(value.ResponderCandidateId),
                LocalProbeNonce = ExactCopy(value.LocalProbeNonce, 16),
                PeerProbeNonce = ExactCopy(value.PeerProbeNonce, 16),
                LocalProofNonceLength = checked((uint)value.LocalProofNonce.Length),
                // The peer proof nonce is authenticated inside the received
                // DirectPeerProofV1 frame. It is not caller-predicted ABI input.
                PeerProofNonceLength = 0,
                LocalProofNonce = PaddedCopy(value.LocalProofNonce,
                    ProductionDirectNativeAbiV2.MaximumProofNonceBytes),
                PeerProofNonce = new byte[ProductionDirectNativeAbiV2.MaximumProofNonceBytes],
                NowMilliseconds = value.NowMilliseconds,
                Reserved = new ulong[4]
            };
        }

        private static void ValidateStart(ProductionDirectNativeStartRequest value)
        {
            if (!ValidEpoch(value.Epoch) || value.AttemptId == Guid.Empty ||
                value.ConnectionId == Guid.Empty || value.LocalDeviceId == Guid.Empty ||
                value.PeerDeviceId == Guid.Empty || value.LocalDeviceId == value.PeerDeviceId ||
                value.Role is not ProductionDirectNativeRole.Initiator and not ProductionDirectNativeRole.Responder ||
                value.TimeoutMilliseconds is < 500 or > 10_000 || value.LocalBind == null ||
                value.RemoteCandidate == null || value.StunServers == null ||
                value.StunServers.Count != 0 ||
                value.Ticket == null || value.Ticket.Length is < 1 or > ProductionDirectNativeAbiV2.MaximumTicketBytes ||
                value.PeerCertificateSha256 == null || value.PeerCertificateSha256.Length != 32 ||
                value.TicketId == Guid.Empty || value.NominationId == Guid.Empty ||
                value.InitiatorCandidateId == Guid.Empty || value.ResponderCandidateId == Guid.Empty ||
                value.NowMilliseconds <= 0)
                throw new ArgumentException("Direct native start metadata is invalid.");
            if (value.ServerSigningPublicKey == null || value.ServerSigningPublicKey.Length != 32 ||
                value.PeerDeviceSigningPublicKey == null || value.PeerDeviceSigningPublicKey.Length != 32 ||
                value.LocalSendProbeHmacKey == null || value.LocalSendProbeHmacKey.Length != 32 ||
                value.PeerReceiveProbeHmacKey == null || value.PeerReceiveProbeHmacKey.Length != 32 ||
                IsAllZero(value.LocalSendProbeHmacKey) ||
                IsAllZero(value.PeerReceiveProbeHmacKey) ||
                value.LocalProbeNonce == null || value.LocalProbeNonce.Length != 16 ||
                value.PeerProbeNonce == null || value.PeerProbeNonce.Length != 16 ||
                IsAllZero(value.LocalProbeNonce) ||
                !CryptographicOperations.FixedTimeEquals(
                    value.LocalProbeNonce,
                    value.PeerProbeNonce))
                throw new ArgumentException("Direct native key or probe nonce is invalid.");
            if (value.LocalProofNonce == null || value.LocalProofNonce.Length is < 16 or > 32 ||
                IsAllZero(value.LocalProofNonce))
                throw new ArgumentException("Direct native proof nonce is invalid.");
        }

        private static TermLinkSocketAddressV2 EncodeEndpoint(
            ProductionDirectNativeEndpoint endpoint,
            bool allowZeroPort)
        {
            if (endpoint == null || endpoint.Address.Equals(IPAddress.Any) ||
                endpoint.Address.Equals(IPAddress.IPv6Any) || endpoint.Address.Equals(IPAddress.Loopback) ||
                endpoint.Address.Equals(IPAddress.IPv6Loopback) || IsMulticast(endpoint.Address) ||
                (!allowZeroPort && endpoint.Port < 1_024))
                throw new ArgumentException("Direct native endpoint is invalid.");
            var address = endpoint.Address.GetAddressBytes();
            if (address.Length is not 4 and not 16)
                throw new ArgumentException("Direct native address family is invalid.");
            var encoded = TermLinkSocketAddressV2.Empty();
            Array.Copy(address, encoded.Ip, address.Length);
            encoded.IpLength = checked((uint)address.Length);
            encoded.Port = checked((uint)endpoint.Port);
            return encoded;
        }

        private static bool TryDecodeEndpoint(
            TermLinkSocketAddressV2 value,
            out ProductionDirectNativeEndpoint endpoint)
        {
            endpoint = null;
            if (value.Ip == null || value.Ip.Length != 16 || value.Port > 65_535)
                return false;
            if (value.IpLength == 0 && value.Port == 0 && value.Ip.All(octet => octet == 0))
                return true;
            if (value.IpLength is not 4 and not 16) return false;
            if (value.IpLength == 4 && value.Ip.Skip(4).Any(octet => octet != 0)) return false;
            var bytes = value.Ip.Take(checked((int)value.IpLength)).ToArray();
            var address = new IPAddress(bytes);
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || IsMulticast(address))
                return false;
            endpoint = new ProductionDirectNativeEndpoint(address, checked((int)value.Port));
            return true;
        }

        private static bool IsMulticast(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6Multicast;
            var bytes = address.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] is >= 224 and <= 239;
        }

        private static void ZeroStart(ref TermLinkDirectStartV2 value)
        {
            Zero(value.Ticket);
            Zero(value.ServerSigningPublicKey);
            Zero(value.PeerCertificateSha256);
            Zero(value.PeerDeviceSigningPublicKey);
            Zero(value.ProbeHmacKey);
            Zero(value.LocalProbeNonce);
            Zero(value.PeerProbeNonce);
            Zero(value.LocalProofNonce);
            Zero(value.PeerProofNonce);
        }

        private static void ZeroNominate(ref TermLinkDirectNominateV3 value)
        {
            ZeroStart(ref value.StartV2);
            Zero(value.LocalSendProbeHmacKey);
            Zero(value.PeerReceiveProbeHmacKey);
        }

        private static void ZeroGather(ref TermLinkDirectGatherV3 value)
        {
            Zero(value.AttemptId);
            Zero(value.AdvertisedLocalIp);
            if (value.LocalBind.Ip != null) Zero(value.LocalBind.Ip);
            if (value.StunServers == null) return;
            for (var index = 0; index < value.StunServers.Length; index++)
            {
                var endpoint = value.StunServers[index];
                Zero(endpoint.Ip);
            }
        }

        private ProductionDirectNativeCommandResult InvalidRequestLocked()
        {
            FallbackLocked(ProductionDirectNativeFault.InvalidRequest,
                ProductionDirectNativeReason.InvalidAbi, retireHandle: true);
            return ProductionDirectNativeCommandResult.RelayFallback;
        }

        private bool IsCurrentLocked(ulong epoch, Guid attemptId) =>
            ValidEpoch(epoch) && epoch == authoritativeEpoch && attemptId != Guid.Empty &&
            attemptId == currentAttempt;

        private static bool ValidEpoch(ulong epoch) => epoch is >= 1 and <= long.MaxValue;

        private static bool EndpointsEqual(
            IReadOnlyList<ProductionDirectNativeEndpoint> left,
            IReadOnlyList<ProductionDirectNativeEndpoint> right)
        {
            if (left == null || right == null || left.Count == 0 || left.Count != right.Count)
                return false;
            for (var index = 0; index < left.Count; index++)
            {
                if (left[index] == null || right[index] == null ||
                    left[index].Port != right[index].Port ||
                    !left[index].Address.Equals(right[index].Address))
                    return false;
            }
            return true;
        }

        private void ResetCandidateExchangeLocked()
        {
            candidatesReadyGeneration = 0;
            gatheredCandidates = Array.Empty<ProductionDirectNativeEndpoint>();
            candidatesExchanged = false;
            nominationSubmitted = false;
        }

        private static TermLinkDirectControlV2 Control(ulong epoch, Guid attemptId) => new()
        {
            AbiVersion = ProductionDirectNativeAbiV3.AbiVersion,
            StructSize = ProductionDirectNativeAbiV2.ControlSize,
            Epoch = epoch,
            AttemptId = GuidBytes(attemptId)
        };

        private void FallbackLocked(
            ProductionDirectNativeFault fault,
            ProductionDirectNativeReason reason,
            bool retireHandle)
        {
            snapshot = RelaySnapshot(fault, reason, api != null && handle != IntPtr.Zero);
            pendingSignatures.Clear();
            ResetCandidateExchangeLocked();
            if (retireHandle) RetireHandleLocked();
            if (retireHandle)
            {
                snapshot = new ProductionDirectNativeSnapshot
                {
                    State = ProductionDirectNativeState.RelayOnly,
                    Reason = reason,
                    Fault = fault,
                    Epoch = authoritativeEpoch,
                    AttemptId = currentAttempt,
                    NativeGeneration = lastGeneration,
                    NativeAvailable = handle != IntPtr.Zero,
                    Disposed = disposed
                };
            }
            PublishLocked();
        }

        private ProductionDirectNativeSnapshot RelaySnapshot(
            ProductionDirectNativeFault fault,
            ProductionDirectNativeReason reason,
            bool nativeAvailable) => new()
        {
            State = ProductionDirectNativeState.RelayOnly,
            Reason = reason,
            Fault = fault,
            Epoch = authoritativeEpoch,
            AttemptId = currentAttempt,
            NativeGeneration = lastGeneration,
            NativeAvailable = nativeAvailable,
            Disposed = disposed
        };

        private void RetireHandleLocked()
        {
            if (handle == IntPtr.Zero || api == null) return;
            var retiring = handle;
            handle = IntPtr.Zero;
            try { api.Destroy(retiring); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            pendingSignatures.Clear();
        }

        private void PublishLocked()
        {
            try { StateChanged?.Invoke(snapshot); }
            catch (Exception) { }
        }

        private static ProductionDirectNativeFault FaultForReason(ProductionDirectNativeReason reason) =>
            reason switch
            {
                ProductionDirectNativeReason.Disabled => ProductionDirectNativeFault.Disabled,
                ProductionDirectNativeReason.InvalidAbi => ProductionDirectNativeFault.AbiMismatch,
                ProductionDirectNativeReason.QueueFull => ProductionDirectNativeFault.QueueFull,
                ProductionDirectNativeReason.Cancelled => ProductionDirectNativeFault.Cancelled,
                ProductionDirectNativeReason.NetworkChanged => ProductionDirectNativeFault.NetworkChanged,
                _ => ProductionDirectNativeFault.Internal
            };

        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

        private static Guid GuidFromBytes(byte[] value)
        {
            if (value == null || value.Length != 16) throw new ArgumentException("A native UUID requires 16 bytes.");
            return Guid.ParseExact(Convert.ToHexString(value), "N");
        }

        private static byte[] ExactCopy(byte[] value, int length)
        {
            if (value == null || value.Length != length)
                throw new ArgumentException("Direct native byte field has the wrong size.");
            return (byte[])value.Clone();
        }

        private static byte[] PaddedCopy(byte[] value, int maximum)
        {
            if (value == null || value.Length > maximum)
                throw new ArgumentException("Direct native byte field exceeds its bound.");
            var result = new byte[maximum];
            Array.Copy(value, result, value.Length);
            return result;
        }

        private static void Zero(byte[] value)
        {
            if (value != null) CryptographicOperations.ZeroMemory(value);
        }

        private static bool IsAllZero(ReadOnlySpan<byte> value)
        {
            byte aggregate = 0;
            foreach (var item in value) aggregate |= item;
            return aggregate == 0;
        }

        private readonly struct PendingSignature : IEquatable<PendingSignature>
        {
            public PendingSignature(ulong epoch, Guid attemptId, ulong requestId)
            {
                Epoch = epoch;
                AttemptId = attemptId;
                RequestId = requestId;
            }

            private ulong Epoch { get; }
            private Guid AttemptId { get; }
            private ulong RequestId { get; }

            public bool Equals(PendingSignature other) =>
                Epoch == other.Epoch && AttemptId == other.AttemptId && RequestId == other.RequestId;
            public override bool Equals(object obj) => obj is PendingSignature other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Epoch, AttemptId, RequestId);
        }
    }
}
