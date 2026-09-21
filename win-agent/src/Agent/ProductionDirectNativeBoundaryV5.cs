using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace TermLink.Agent
{
    internal enum ProductionDirectNativeResultV5
    {
        RelayFallback = int.MinValue,
        TicketExpired = ProductionDirectNativeAbiV5.ErrorTicketExpired,
        Timeout = ProductionDirectNativeAbiV5.ErrorTimeout,
        Disabled = ProductionDirectNativeAbiV5.ErrorDisabled,
        Protocol = ProductionDirectNativeAbiV5.ErrorProtocol,
        Limit = ProductionDirectNativeAbiV5.ErrorLimit,
        Cancelled = ProductionDirectNativeAbiV5.ErrorCancelled,
        NotReady = ProductionDirectNativeAbiV5.ErrorNotReady,
        BufferTooSmall = ProductionDirectNativeAbiV5.ErrorBufferTooSmall,
        StalePath = ProductionDirectNativeAbiV5.ErrorStalePath,
        QueueFull = ProductionDirectNativeAbiV5.ErrorQueueFull,
        InvalidArgument = ProductionDirectNativeAbiV5.ErrorInvalidArgument,
        InvalidHandle = ProductionDirectNativeAbiV5.ErrorInvalidHandle,
        Accepted = ProductionDirectNativeAbiV5.Ok,
        NoData = ProductionDirectNativeAbiV5.NoData
    }

    internal enum ProductionDirectNativeEventKindV5 : uint
    {
        PathUsed = 1, EnvelopeQueued = 2, EnvelopeAvailable = 3, TransportReceipt = 4,
        Cancelled = 5, NetworkChanged = 6, QueueLimit = 7, PathReady = 8,
        DirectFailed = 9, Ping = 10, Pong = 11, SignRequest = 12, RenewalDue = 13
    }

    [Flags]
    internal enum ProductionDirectNativeEventFlagsV5 : uint
    {
        None = 0, Outbound = 1, Inbound = 2, TransportCopy = 4, Lan = 8, Nat = 16
    }

    internal enum ProductionDirectDirectionV5 : uint
    {
        PcToAndroid = 1, AndroidToPc = 2
    }

    internal enum ProductionDirectPathKindV5 : uint
    {
        Lan = 1, Nat = 2
    }

    internal enum ProductionDirectCarrierV5 : uint
    {
        Relay = 1, LanDirect = 2, NatDirect = 3
    }

    internal enum ProductionDirectIngressSourceV5 : uint
    {
        Relay = 1, Direct = 2
    }

    internal enum ProductionDirectRouteActionKindV5 : uint
    {
        DirectEnvelope = 1, RelayEnvelope = 2, Accept = 4, Commit = 5,
        Fallback = 6, Carrier = 7, IngressDeliver = 8, IngressHold = 9, IngressDrop = 10,
        SessionCarrier = 11
    }

    internal enum ProductionDirectRouteReasonV5 : uint
    {
        None = 0,
        BufferEnvelopeLimit = 1, BufferByteLimit = 2, DirectFailure = 3, EpochReplaced = 4,
        NegotiationTimeout = 5, PeerRejected = 6, BarrierMismatch = 7, InvalidEnvelope = 8,
        ProtocolViolation = 9, RejectInvalidEpoch = 101, RejectFutureEpoch = 102,
        RejectInvalidProposal = 103, RejectInvalidTimeout = 104, RejectPathBindingMismatch = 105,
        RejectDirectionBusy = 106, RejectBarrierIdMismatch = 107, RejectBarrierHashMismatch = 108,
        RejectUnexpectedAccept = 109, RejectLateCommit = 110, RejectPrefixTimeout = 111
    }

    internal sealed class ProductionDirectPathFenceV5 : IEquatable<ProductionDirectPathFenceV5>
    {
        public ulong PathEpoch { get; init; }
        public Guid ConnectionId { get; init; }
        public Guid AttemptId { get; init; }

        public bool Equals(ProductionDirectPathFenceV5 other) => other != null &&
            PathEpoch == other.PathEpoch && ConnectionId == other.ConnectionId && AttemptId == other.AttemptId;
        public override bool Equals(object obj) => Equals(obj as ProductionDirectPathFenceV5);
        public override int GetHashCode() => HashCode.Combine(PathEpoch, ConnectionId, AttemptId);
    }

    internal sealed class ProductionDirectPromoteRequestV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectCommitRequestV4 Nomination { get; init; }
    }

    internal sealed class ProductionDirectSessionBarrierV5
    {
        public Guid SessionId { get; init; }
        public ulong FirstDirectSequence { get; init; }
    }

    internal sealed class ProductionDirectProposalRequestV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectDirectionV5 Direction { get; init; }
        public Guid BarrierId { get; init; }
        public byte[] BarrierSha256 { get; init; }
        public byte[] DirectPathBinding { get; init; }
        public uint DuplicateGraceMilliseconds { get; init; }
        public ulong NowMilliseconds { get; init; }
        public ulong TimeoutMilliseconds { get; init; }
        public IReadOnlyList<ProductionDirectSessionBarrierV5> Sessions { get; init; } =
            Array.Empty<ProductionDirectSessionBarrierV5>();
    }

    internal sealed class ProductionDirectTransitionRequestV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectDirectionV5 Direction { get; init; }
        public Guid BarrierId { get; init; }
        public byte[] BarrierSha256 { get; init; }
        public bool Accepted { get; init; }
        public bool IsEcho { get; init; }
        public ulong NowMilliseconds { get; init; }
        public ulong TimeoutMilliseconds { get; init; }
    }

    internal sealed class ProductionDirectCursorUpdateV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectDirectionV5 Direction { get; init; }
        public Guid SessionId { get; init; }
        public ulong HighestContiguous { get; init; }
    }

    internal sealed class ProductionDirectSessionDeregisterV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectDirectionV5 Direction { get; init; }
        public Guid SessionId { get; init; }
    }

    internal sealed class ProductionDirectRouteEnvelopeRequestV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectDirectionV5 Direction { get; init; }
        public bool RouteEligible { get; init; }
        public Guid SessionId { get; init; }
        public ulong EnvelopeSequence { get; init; }
        public ulong NowMilliseconds { get; init; }
    }

    internal sealed class ProductionDirectIngressRequestV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectDirectionV5 Direction { get; init; }
        public ProductionDirectIngressSourceV5 Source { get; init; }
        public Guid SessionId { get; init; }
        public ulong EnvelopeSequence { get; init; }
        public ulong NowMilliseconds { get; init; }
        public uint DuplicateGraceMilliseconds { get; init; }
    }

    internal sealed class ProductionDirectPathInfoV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectPathKindV5? PathKind { get; init; }
        public bool Ready { get; init; }
        public byte[] DirectPathBinding { get; init; } = Array.Empty<byte>();
    }

    internal sealed class ProductionDirectSigningPayloadV5
    {
        public ProductionDirectNativeResultV5 Status { get; init; }
        public ulong RequestId { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
        public uint RequiredBytes { get; init; }
    }

    internal sealed class ProductionDirectNativeEventV5
    {
        public ProductionDirectNativeEventKindV5 Kind { get; init; }
        public ProductionDirectNativeResultV5 Status { get; init; }
        public ProductionDirectNativeEventFlagsV5 Flags { get; init; }
        public ulong PathEpoch { get; init; }
        public Guid AttemptId { get; init; }
        public ulong FrameSequence { get; init; }
        public uint PayloadBytes { get; init; }
        public uint ReleasedFrames { get; init; }
    }

    internal sealed class ProductionDirectNativeReceiveV5
    {
        public ProductionDirectNativeResultV5 Status { get; init; }
        public ulong FrameSequence { get; init; }
        public byte[] Envelope { get; init; } = Array.Empty<byte>();
        public uint RequiredBytes { get; init; }
    }

    internal sealed class ProductionDirectRouteActionV5
    {
        public ProductionDirectPathFenceV5 Path { get; init; }
        public ProductionDirectRouteActionKindV5 Kind { get; init; }
        public ProductionDirectDirectionV5 Direction { get; init; }
        public Guid SessionId { get; init; }
        public ulong EnvelopeSequence { get; init; }
        public ulong DirectFrameSequence { get; init; }
        public Guid BarrierId { get; init; }
        public byte[] BarrierSha256 { get; init; } = Array.Empty<byte>();
        public bool Accepted { get; init; }
        public bool IsEcho { get; init; }
        public ProductionDirectCarrierV5? Carrier { get; init; }
        public ProductionDirectRouteReasonV5 Reason { get; init; }
        public uint PayloadBytes { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Externally synchronized owner for a v5 handle. Destroy is serialized
    /// against every call. It owns no Relay/session/durable-ACK authority.
    /// </summary>
    internal sealed class ProductionDirectNativeBoundaryV5 : IDisposable
    {
        private readonly object sync = new();
        private IProductionDirectNativeApiV5 api;
        private IntPtr handle;
        private ProductionDirectPathFenceV5 currentPath;
        private readonly HashSet<ProductionDirectPathFenceV5> drainingPaths = new();
        private readonly HashSet<ulong> pendingSigningRequests = new();
        private bool disposed;
        private bool nativeAvailable;

        internal ProductionDirectNativeBoundaryV5(
            bool enabled = false,
            uint queueCapacityFrames = ProductionDirectNativeAbiV5.MaximumFrames,
            ulong queueCapacityBytes = ProductionDirectNativeAbiV5.MaximumBytes,
            ProductionDirectPathFenceV5 initialPath = null,
            IProductionDirectNativeLoaderV5 loader = null)
        {
            if (!enabled) return;
            try
            {
                if (queueCapacityFrames is < 1 or > ProductionDirectNativeAbiV5.MaximumFrames ||
                    queueCapacityBytes is < 1 or > ProductionDirectNativeAbiV5.MaximumBytes ||
                    (initialPath != null && !ValidPath(initialPath)) ||
                    !ProductionDirectNativeAbiV5.TryValidateLayout(out _)) return;
                loader ??= new ProductionDirectNativeLibraryLoaderV5();
                if (!loader.TryLoad(out api, out _) || api == null) return;
                if (api.AbiVersion() != ProductionDirectNativeAbiV5.AbiVersion)
                {
                    ReleaseApiLocked();
                    return;
                }
                var create = new TermLinkDirectCreateV5
                {
                    AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.CreateSize, Enabled = 1,
                    QueueCapacityFrames = queueCapacityFrames, QueueCapacityBytes = queueCapacityBytes,
                    Path = initialPath == null ? ProductionDirectNativeAbiV5.EmptyPath() : EncodePath(initialPath),
                    Reserved = new ulong[2]
                };
                handle = api.Create(ref create);
                Zero(ref create);
                if (handle == IntPtr.Zero)
                {
                    ReleaseApiLocked();
                    return;
                }
                currentPath = Copy(initialPath);
                nativeAvailable = true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                FailClosedLocked();
            }
        }

        internal bool NativeAvailable { get { lock (sync) return nativeAvailable; } }
        internal bool Disposed { get { lock (sync) return disposed; } }
        internal ProductionDirectPathFenceV5 CurrentPath { get { lock (sync) return Copy(currentPath); } }

        internal ProductionDirectNativeResultV5 PathUsage(ProductionDirectPathFenceV5 path)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (!ValidPath(path)) return ProductionDirectNativeResultV5.InvalidArgument;
                if (currentPath != null && (path.PathEpoch < currentPath.PathEpoch ||
                    path.PathEpoch == currentPath.PathEpoch && !currentPath.Equals(path)))
                    return ProductionDirectNativeResultV5.StalePath;
                var native = EncodePath(path);
                var status = InvokeLocked(() => api.PathUsage(handle, ref native));
                Zero(ref native);
                if (status == ProductionDirectNativeResultV5.Accepted)
                {
                    if (currentPath != null && !currentPath.Equals(path))
                        drainingPaths.Add(Copy(currentPath));
                    currentPath = Copy(path);
                    pendingSigningRequests.Clear();
                }
                return status;
            }
        }

        internal ProductionDirectNativeResultV5 Attach(ProductionDirectPathFenceV5 path) =>
            WithCurrentPath(path, (ref TermLinkDirectPathV5 native) => api.Attach(handle, ref native));

        /// <summary>
        /// V5's lock is held before V4's lock. The V4 handle is visible only
        /// inside the private synchronous adapter and cannot escape to runtime.
        /// </summary>
        internal ProductionDirectNativeResultV5 Promote(
            IProductionDirectPromotableNativeV4 v4,
            ProductionDirectPromoteRequestV5 request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (v4 == null || request == null || !Current(request.Path) || request.Nomination == null)
                    return request?.Path != null && !Current(request.Path)
                        ? ProductionDirectNativeResultV5.StalePath
                        : ProductionDirectNativeResultV5.InvalidArgument;
                if (!TryEncodePromote(request, out var native))
                    return ProductionDirectNativeResultV5.InvalidArgument;
                try
                {
                    var operation = new PromotionOperation(api, handle, native);
                    var promoted = v4.TryPromoteNomination(request.Nomination, operation);
                    return promoted.NativeCallCompleted
                        ? InvokeLocked(() => promoted.NativeStatus)
                        : ProductionDirectNativeResultV5.RelayFallback;
                }
                finally { Zero(ref native); }
            }
        }

        internal ProductionDirectNativeResultV5 SubmitSignature(
            ProductionDirectPathFenceV5 path, ulong requestId, byte[] signature)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (!Current(path)) return ProductionDirectNativeResultV5.StalePath;
                if (requestId == 0 || !pendingSigningRequests.Contains(requestId) || signature == null ||
                    signature.Length is < 1 or > 128)
                    return ProductionDirectNativeResultV5.InvalidArgument;
                var native = new TermLinkDirectSignatureV5
                {
                    AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.SignatureSize,
                    Path = EncodePath(path), RequestId = requestId,
                    SignatureLength = checked((uint)signature.Length), Signature = Padded(signature, 128),
                    Reserved = new ulong[2]
                };
                try
                {
                    var status = InvokeLocked(() => api.SubmitSignature(handle, ref native));
                    if (status == ProductionDirectNativeResultV5.Accepted)
                        pendingSigningRequests.Remove(requestId);
                    return status;
                }
                finally { Zero(ref native); }
            }
        }

        internal ProductionDirectSigningPayloadV5 ReceiveSigningPayload(ProductionDirectPathFenceV5 path)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return SigningPayload(ProductionDirectNativeResultV5.RelayFallback);
                if (!Current(path)) return SigningPayload(ProductionDirectNativeResultV5.StalePath);
                var native = EncodePath(path);
                ulong requestId = ulong.MaxValue;
                uint required = uint.MaxValue;
                var status = InvokeLocked(() => api.ReceiveSigningPayload(handle, ref native, ref requestId,
                    IntPtr.Zero, 0, ref required));
                if (status == ProductionDirectNativeResultV5.NoData)
                    return required == 0 && requestId == ulong.MaxValue
                        ? SigningPayload(status) : MalformedSigningPayloadLocked();
                if (status != ProductionDirectNativeResultV5.BufferTooSmall || requestId != ulong.MaxValue ||
                    required is < 1 or > ProductionDirectNativeAbiV5.MaximumEnvelopeBytes)
                    return status == ProductionDirectNativeResultV5.BufferTooSmall
                        ? MalformedSigningPayloadLocked() : SigningPayload(status, requiredBytes: required);
                var output = new byte[required];
                var pinned = default(GCHandle);
                try
                {
                    pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
                    var confirmed = required;
                    requestId = ulong.MaxValue;
                    status = InvokeLocked(() => api.ReceiveSigningPayload(handle, ref native, ref requestId,
                        pinned.AddrOfPinnedObject(), checked((uint)output.Length), ref confirmed));
                    if (status != ProductionDirectNativeResultV5.Accepted)
                    {
                        Array.Clear(output); return SigningPayload(status, requiredBytes: confirmed);
                    }
                    if (requestId == 0 || confirmed != required)
                    {
                        Array.Clear(output); return MalformedSigningPayloadLocked();
                    }
                    pendingSigningRequests.Add(requestId);
                    return SigningPayload(status, requestId, output, confirmed);
                }
                finally { if (pinned.IsAllocated) pinned.Free(); Zero(ref native); }
            }
        }

        internal (ProductionDirectNativeResultV5 Status, ProductionDirectPathInfoV5 Info) PathInfo(
            ProductionDirectPathFenceV5 path)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return (ProductionDirectNativeResultV5.RelayFallback, null);
                if (!Current(path)) return (ProductionDirectNativeResultV5.StalePath, null);
                var nativePath = EncodePath(path);
                var native = ProductionDirectNativeAbiV5.NewPathInfoBuffer();
                try
                {
                    var status = InvokeLocked(() => api.PathInfo(handle, ref nativePath, ref native));
                    if (status != ProductionDirectNativeResultV5.Accepted) return (status, null);
                    if (!TryDecodePathInfo(native, path, out var result)) return (MalformedLocked(), null);
                    return (status, result);
                }
                finally
                {
                    Zero(ref nativePath);
                    Zero(ref native);
                }
            }
        }

        internal ProductionDirectNativeReceiveV5 ReceiveEnvelope(ProductionDirectPathFenceV5 path)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return Receive(ProductionDirectNativeResultV5.RelayFallback);
                if (!Current(path)) return Receive(ProductionDirectNativeResultV5.StalePath);
                var native = EncodePath(path);
                ulong frameSequence = ulong.MaxValue;
                uint required = uint.MaxValue;
                var status = InvokeLocked(() => api.ReceiveEnvelope(handle, ref native, ref frameSequence,
                    IntPtr.Zero, 0, ref required));
                if (status == ProductionDirectNativeResultV5.NoData)
                    return required == 0 && frameSequence == ulong.MaxValue
                        ? Receive(status) : MalformedReceiveLocked();
                if (status != ProductionDirectNativeResultV5.BufferTooSmall || frameSequence != ulong.MaxValue ||
                    required is < 1 or > ProductionDirectNativeAbiV5.MaximumEnvelopeBytes)
                    return status == ProductionDirectNativeResultV5.BufferTooSmall
                        ? MalformedReceiveLocked() : Receive(status, requiredBytes: required);
                var output = new byte[required];
                var pinned = default(GCHandle);
                try
                {
                    pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
                    var confirmed = required;
                    frameSequence = ulong.MaxValue;
                    status = InvokeLocked(() => api.ReceiveEnvelope(handle, ref native, ref frameSequence,
                        pinned.AddrOfPinnedObject(), checked((uint)output.Length), ref confirmed));
                    if (status != ProductionDirectNativeResultV5.Accepted)
                    {
                        Array.Clear(output); return Receive(status, requiredBytes: confirmed);
                    }
                    if (confirmed != required || !ValidFrame(frameSequence))
                    {
                        Array.Clear(output); return MalformedReceiveLocked();
                    }
                    return Receive(status, frameSequence, output, confirmed);
                }
                finally { if (pinned.IsAllocated) pinned.Free(); Zero(ref native); }
            }
        }

        internal ProductionDirectNativeResultV5 StartProposal(ProductionDirectProposalRequestV5 request) =>
            ProposalOperation(request, ProductionDirectDirectionV5.PcToAndroid,
                (ref TermLinkDirectProposalV5 value) => api.StartProposal(handle, ref value));
        internal ProductionDirectNativeResultV5 ReceiveProposal(ProductionDirectProposalRequestV5 request) =>
            ProposalOperation(request, ProductionDirectDirectionV5.AndroidToPc,
                (ref TermLinkDirectProposalV5 value) => api.ReceiveProposal(handle, ref value));
        internal ProductionDirectNativeResultV5 ReceiveAccept(ProductionDirectTransitionRequestV5 request) =>
            TransitionOperation(request, ProductionDirectDirectionV5.PcToAndroid,
                (ref TermLinkDirectTransitionV5 value) => api.ReceiveAccept(handle, ref value));
        internal ProductionDirectNativeResultV5 ReceiveCommit(ProductionDirectTransitionRequestV5 request) =>
            // A Windows endpoint receives an Android-to-PC initial commit as
            // responder and a PC-to-Android commit echo as proposer.
            TransitionOperation(request, null,
                (ref TermLinkDirectTransitionV5 value) => api.ReceiveCommit(handle, ref value));
        internal ProductionDirectNativeResultV5 CommitEchoSent(ProductionDirectTransitionRequestV5 request) =>
            TransitionOperation(request, ProductionDirectDirectionV5.AndroidToPc,
                (ref TermLinkDirectTransitionV5 value) => api.CommitEchoSent(handle, ref value));
        internal ProductionDirectNativeResultV5 ReceiveFallback(ProductionDirectTransitionRequestV5 request) =>
            TransitionOperation(request, null,
                (ref TermLinkDirectTransitionV5 value) => api.ReceiveFallback(handle, ref value));
        internal ProductionDirectNativeResultV5 Tick(ProductionDirectTransitionRequestV5 request) =>
            TransitionOperation(request, null,
                (ref TermLinkDirectTransitionV5 value) => api.Tick(handle, ref value));

        /// <summary>Notification only; it never creates or acknowledges a durable application ACK.</summary>
        internal ProductionDirectNativeResultV5 UpdateAppliedCursor(ProductionDirectCursorUpdateV5 request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (request == null || !Current(request.Path))
                    return request?.Path != null ? ProductionDirectNativeResultV5.StalePath :
                        ProductionDirectNativeResultV5.InvalidArgument;
                if (request.Direction != ProductionDirectDirectionV5.AndroidToPc ||
                    request.SessionId == Guid.Empty)
                    return ProductionDirectNativeResultV5.InvalidArgument;
                var native = new TermLinkDirectCursorV5
                {
                    AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.CursorSize,
                    Path = EncodePath(request.Path), Direction = (uint)request.Direction,
                    SessionId = GuidBytes(request.SessionId), HighestContiguous = request.HighestContiguous,
                    Reserved = new ulong[2]
                };
                try { return InvokeLocked(() => api.UpdateAppliedCursor(handle, ref native)); }
                finally { Zero(ref native); }
            }
        }

        /// <summary>
        /// Removes one session from one direction without retiring the shared
        /// authenticated path or any other committed session.
        /// </summary>
        internal ProductionDirectNativeResultV5 DeregisterSession(
            ProductionDirectSessionDeregisterV5 request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (request == null || !Current(request.Path))
                    return request?.Path != null ? ProductionDirectNativeResultV5.StalePath :
                        ProductionDirectNativeResultV5.InvalidArgument;
                if (!ValidDirection(request.Direction) || request.SessionId == Guid.Empty)
                    return ProductionDirectNativeResultV5.InvalidArgument;
                var native = new TermLinkDirectCursorV5
                {
                    AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.CursorSize,
                    Path = EncodePath(request.Path), Direction = (uint)request.Direction,
                    SessionId = GuidBytes(request.SessionId), HighestContiguous = 0,
                    Reserved = new ulong[2]
                };
                try { return InvokeLocked(() => api.DeregisterSession(handle, ref native)); }
                finally { Zero(ref native); }
            }
        }

        /// <summary>On non-OK the exact Envelope remains caller-owned and may be sent over Relay.</summary>
        internal ProductionDirectNativeResultV5 RouteEnvelope(
            ProductionDirectRouteEnvelopeRequestV5 request, byte[] exactEnvelope)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (request == null || !Current(request.Path))
                    return request?.Path != null ? ProductionDirectNativeResultV5.StalePath :
                        ProductionDirectNativeResultV5.InvalidArgument;
                if (request.Direction != ProductionDirectDirectionV5.PcToAndroid ||
                    request.SessionId == Guid.Empty ||
                    request.EnvelopeSequence == 0 || !ValidEnvelope(exactEnvelope))
                    return ProductionDirectNativeResultV5.InvalidArgument;
                var native = new TermLinkDirectRouteEnvelopeV5
                {
                    AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.RouteEnvelopeSize,
                    Path = EncodePath(request.Path), Direction = (uint)request.Direction,
                    RouteEligible = request.RouteEligible ? 1u : 0u, SessionId = GuidBytes(request.SessionId),
                    EnvelopeSequence = request.EnvelopeSequence, NowMilliseconds = request.NowMilliseconds,
                    Reserved = new ulong[2]
                };
                try
                {
                    return OwnedEnvelopeCall(exactEnvelope,
                        (address, length) => api.RouteEnvelope(handle, ref native, address, length));
                }
                finally { Zero(ref native); }
            }
        }

        internal (ProductionDirectNativeResultV5 Status, ProductionDirectRouteActionV5 Action)
            PollRouteAction(ProductionDirectPathFenceV5 path)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return (ProductionDirectNativeResultV5.RelayFallback, null);
                if (!Current(path)) return (ProductionDirectNativeResultV5.StalePath, null);
                for (var index = 0; index < ProductionDirectNativeAbiV5.MaximumFrames +
                    ProductionDirectNativeAbiV5.FailureActionTail; index++)
                {
                    var polled = PollOneRouteActionLocked();
                    if (polled.Status != ProductionDirectNativeResultV5.Accepted)
                    {
                        if (polled.Status == ProductionDirectNativeResultV5.NoData)
                            drainingPaths.Clear();
                        return polled;
                    }
                    var action = polled.Action;
                    if (action.Path.Equals(path)) return polled;
                    if (!drainingPaths.Contains(action.Path))
                        return (MalformedLocked(), null);
                    switch (action.Kind)
                    {
                        // These actions carry exact recovery bytes or a peer
                        // fallback transition and remain authoritative under
                        // the predecessor's complete fence.
                        case ProductionDirectRouteActionKindV5.RelayEnvelope:
                        case ProductionDirectRouteActionKindV5.IngressDeliver:
                        case ProductionDirectRouteActionKindV5.Fallback:
                            return polled;
                        // A predecessor cannot authorize a new Direct write or
                        // ingress transition after its successor is installed.
                        case ProductionDirectRouteActionKindV5.DirectEnvelope:
                        case ProductionDirectRouteActionKindV5.Accept:
                        case ProductionDirectRouteActionKindV5.Commit:
                        case ProductionDirectRouteActionKindV5.Carrier:
                        case ProductionDirectRouteActionKindV5.SessionCarrier:
                        case ProductionDirectRouteActionKindV5.IngressHold:
                        case ProductionDirectRouteActionKindV5.IngressDrop:
                            break;
                        default:
                            return (MalformedLocked(), null);
                    }
                }
                return (MalformedLocked(), null);
            }
        }

        private (ProductionDirectNativeResultV5 Status, ProductionDirectRouteActionV5 Action)
            PollOneRouteActionLocked()
        {
            var sentinel = ProductionDirectNativeAbiV5.NewRouteActionBuffer();
            sentinel.Kind = uint.MaxValue;
            try
            {
                uint required = uint.MaxValue;
                var status = InvokeLocked(() => api.PollRouteAction(handle, ref sentinel,
                    IntPtr.Zero, 0, ref required));
                if (status == ProductionDirectNativeResultV5.NoData)
                    return required == 0 && IsRouteActionProbe(sentinel)
                        ? (status, null) : (MalformedLocked(), null);
                if (status == ProductionDirectNativeResultV5.Accepted)
                {
                    if (required != 0 || !TryDecodeRouteAction(sentinel, Array.Empty<byte>(), out var metadata))
                        return (MalformedLocked(), null);
                    return (status, metadata);
                }
                if (status != ProductionDirectNativeResultV5.BufferTooSmall || required is < 1 or
                    > ProductionDirectNativeAbiV5.MaximumEnvelopeBytes || !IsRouteActionProbe(sentinel))
                    return status == ProductionDirectNativeResultV5.BufferTooSmall
                        ? (MalformedLocked(), null) : (status, null);
                var output = new byte[required];
                var action = ProductionDirectNativeAbiV5.NewRouteActionBuffer();
                action.Kind = uint.MaxValue;
                var pinned = default(GCHandle);
                try
                {
                    pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
                    var confirmed = required;
                    status = InvokeLocked(() => api.PollRouteAction(handle, ref action,
                        pinned.AddrOfPinnedObject(), checked((uint)output.Length), ref confirmed));
                    if (status != ProductionDirectNativeResultV5.Accepted) return (status, null);
                    if (confirmed != required || !TryDecodeRouteAction(action, output, out var decoded))
                        return (MalformedLocked(), null);
                    return (status, decoded);
                }
                finally
                {
                    if (pinned.IsAllocated) pinned.Free();
                    Zero(output);
                    Zero(ref action);
                }
            }
            finally { Zero(ref sentinel); }
        }

        internal ProductionDirectNativeResultV5 ArbitrateIngress(
            ProductionDirectIngressRequestV5 request, byte[] exactEnvelope)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (request == null || !Current(request.Path))
                    return request?.Path != null ? ProductionDirectNativeResultV5.StalePath :
                        ProductionDirectNativeResultV5.InvalidArgument;
                if (request.Direction != ProductionDirectDirectionV5.AndroidToPc || request.Source is not
                    (ProductionDirectIngressSourceV5.Relay or ProductionDirectIngressSourceV5.Direct) ||
                    request.SessionId == Guid.Empty || request.EnvelopeSequence == 0 ||
                    request.DuplicateGraceMilliseconds > 5_000 || !ValidEnvelope(exactEnvelope))
                    return ProductionDirectNativeResultV5.InvalidArgument;
                var native = new TermLinkDirectIngressV5
                {
                    AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.IngressSize,
                    Path = EncodePath(request.Path), Direction = (uint)request.Direction,
                    Source = (uint)request.Source, SessionId = GuidBytes(request.SessionId),
                    EnvelopeSequence = request.EnvelopeSequence, NowMilliseconds = request.NowMilliseconds,
                    DuplicateGraceMilliseconds = request.DuplicateGraceMilliseconds, Reserved = new ulong[2]
                };
                try
                {
                    return OwnedEnvelopeCall(exactEnvelope,
                        (address, length) => api.ArbitrateIngress(handle, ref native, address, length));
                }
                finally { Zero(ref native); }
            }
        }

        internal ProductionDirectNativeResultV5 TickIngress(
            ProductionDirectPathFenceV5 path, ulong nowMilliseconds) =>
            WithCurrentPath(path, (ref TermLinkDirectPathV5 native) =>
                api.TickIngress(handle, ref native, nowMilliseconds));

        internal IReadOnlyList<ProductionDirectNativeEventV5> Poll(int maximumEvents = 32)
        {
            lock (sync)
            {
                var result = new List<ProductionDirectNativeEventV5>();
                if (!CanCallLocked() || maximumEvents is < 1 or > 256) return result.AsReadOnly();
                for (var index = 0; index < maximumEvents; index++)
                {
                    var native = ProductionDirectNativeAbiV5.NewEventBuffer();
                    native.Kind = uint.MaxValue;
                    ProductionDirectNativeResultV5 status;
                    try { status = DecodePollStatus(api.PollEvent(handle, ref native)); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        FailClosedLocked(); break;
                    }
                    if (status == ProductionDirectNativeResultV5.NoData)
                    {
                        if (!IsEventProbe(native)) FailClosedLocked();
                        break;
                    }
                    if (status != ProductionDirectNativeResultV5.Accepted || !TryDecodeEvent(native, out var value))
                    {
                        FailClosedLocked(); break;
                    }
                    result.Add(value);
                }
                return result.AsReadOnly();
            }
        }

        internal ProductionDirectNativeResultV5 Cancel(ProductionDirectPathFenceV5 path) =>
            WithCurrentPath(path, (ref TermLinkDirectPathV5 native) => api.Cancel(handle, ref native));
        internal ProductionDirectNativeResultV5 NetworkChanged(ProductionDirectPathFenceV5 path) =>
            WithCurrentPath(path, (ref TermLinkDirectPathV5 native) => api.NetworkChanged(handle, ref native));

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                FailClosedLocked();
            }
        }

        private delegate int PathCall(ref TermLinkDirectPathV5 path);
        private delegate int ProposalCall(ref TermLinkDirectProposalV5 proposal);
        private delegate int TransitionCall(ref TermLinkDirectTransitionV5 transition);

        private ProductionDirectNativeResultV5 WithCurrentPath(ProductionDirectPathFenceV5 path, PathCall call)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (!Current(path)) return ProductionDirectNativeResultV5.StalePath;
                var native = EncodePath(path);
                try { return InvokeLocked(() => call(ref native)); }
                finally { Zero(ref native); }
            }
        }

        private ProductionDirectNativeResultV5 ProposalOperation(
            ProductionDirectProposalRequestV5 request, ProductionDirectDirectionV5 requiredDirection,
            ProposalCall call)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (request == null || !Current(request.Path))
                    return request?.Path != null ? ProductionDirectNativeResultV5.StalePath :
                        ProductionDirectNativeResultV5.InvalidArgument;
                if (request.Direction != requiredDirection || !TryEncodeProposal(request, out var native))
                    return ProductionDirectNativeResultV5.InvalidArgument;
                try { return InvokeLocked(() => call(ref native)); }
                finally { Zero(ref native); }
            }
        }

        private ProductionDirectNativeResultV5 TransitionOperation(
            ProductionDirectTransitionRequestV5 request, ProductionDirectDirectionV5? requiredDirection,
            TransitionCall call)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeResultV5.RelayFallback;
                if (request == null || !Current(request.Path))
                    return request?.Path != null ? ProductionDirectNativeResultV5.StalePath :
                        ProductionDirectNativeResultV5.InvalidArgument;
                if (requiredDirection.HasValue && request.Direction != requiredDirection.Value)
                    return ProductionDirectNativeResultV5.InvalidArgument;
                if (!TryEncodeTransition(request, out var native))
                    return ProductionDirectNativeResultV5.InvalidArgument;
                try { return InvokeLocked(() => call(ref native)); }
                finally { Zero(ref native); }
            }
        }

        private ProductionDirectNativeResultV5 OwnedEnvelopeCall(
            byte[] exactEnvelope, Func<IntPtr, uint, int> call)
        {
            var owned = exactEnvelope.ToArray();
            var pinned = default(GCHandle);
            try
            {
                pinned = GCHandle.Alloc(owned, GCHandleType.Pinned);
                return InvokeLocked(() => call(pinned.AddrOfPinnedObject(), checked((uint)owned.Length)));
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
                Array.Clear(owned);
            }
        }

        private ProductionDirectNativeResultV5 InvokeLocked(Func<int> call)
        {
            try
            {
                var raw = call();
                if (raw is < ProductionDirectNativeAbiV5.ErrorTicketExpired or
                    > ProductionDirectNativeAbiV5.NoData)
                {
                    FailClosedLocked();
                    return ProductionDirectNativeResultV5.RelayFallback;
                }
                var result = (ProductionDirectNativeResultV5)raw;
                // PROTOCOL is a valid path-level result. Native may already
                // have staged exact Relay recovery before returning it, so the
                // caller must retain this handle long enough to drain actions.
                if (result == ProductionDirectNativeResultV5.InvalidHandle)
                    FailClosedLocked();
                return result;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                FailClosedLocked();
                return ProductionDirectNativeResultV5.RelayFallback;
            }
        }

        private ProductionDirectNativeResultV5 MalformedLocked()
        {
            FailClosedLocked();
            return ProductionDirectNativeResultV5.RelayFallback;
        }

        private ProductionDirectNativeReceiveV5 MalformedReceiveLocked()
        {
            FailClosedLocked(); return Receive(ProductionDirectNativeResultV5.RelayFallback);
        }

        private ProductionDirectSigningPayloadV5 MalformedSigningPayloadLocked()
        {
            FailClosedLocked(); return SigningPayload(ProductionDirectNativeResultV5.RelayFallback);
        }

        private bool TryDecodeEvent(TermLinkDirectEventV5 value, out ProductionDirectNativeEventV5 result)
        {
            result = null;
            if (value.AbiVersion != 5 || value.StructSize != ProductionDirectNativeAbiV5.EventSize ||
                value.Kind is < 1 or > 12 || !IsKnownStatus(value.Status) ||
                (value.Flags & ~31u) != 0 || value.Reserved0 != 0 || value.PathEpoch == 0 ||
                value.AttemptId?.Length != 16 || value.AttemptId.All(item => item == 0) ||
                value.Reserved?.Length != 2 || value.Reserved.Any(item => item != 0) ||
                currentPath == null || value.PathEpoch != currentPath.PathEpoch ||
                GuidFromBytes(value.AttemptId) != currentPath.AttemptId ||
                value.PayloadBytes > ProductionDirectNativeAbiV5.MaximumEnvelopeBytes ||
                value.ReleasedFrames > ProductionDirectNativeAbiV5.MaximumFrames) return false;
            var kind = (ProductionDirectNativeEventKindV5)value.Kind;
            var flags = (ProductionDirectNativeEventFlagsV5)value.Flags;
            var hasFrame = ValidFrame(value.FrameSequence);
            var valid = kind switch
            {
                ProductionDirectNativeEventKindV5.PathUsed => NoFrame(value) && flags == 0 && value.Status == 0,
                ProductionDirectNativeEventKindV5.EnvelopeQueued => value.Status == 0 && hasFrame &&
                    value.PayloadBytes > 0 && value.ReleasedFrames == 0 && flags ==
                    (ProductionDirectNativeEventFlagsV5.Outbound |
                     ProductionDirectNativeEventFlagsV5.TransportCopy),
                ProductionDirectNativeEventKindV5.EnvelopeAvailable => value.Status == 0 && hasFrame &&
                    value.PayloadBytes > 0 && value.ReleasedFrames == 0 &&
                    flags == ProductionDirectNativeEventFlagsV5.Inbound,
                ProductionDirectNativeEventKindV5.TransportReceipt => value.Status == 0 && hasFrame &&
                    value.PayloadBytes == 0 && (flags == ProductionDirectNativeEventFlagsV5.Outbound ||
                    flags == (ProductionDirectNativeEventFlagsV5.Inbound |
                              ProductionDirectNativeEventFlagsV5.TransportCopy)) &&
                    (flags.HasFlag(ProductionDirectNativeEventFlagsV5.Outbound) ?
                        value.ReleasedFrames == 0 : true),
                ProductionDirectNativeEventKindV5.Cancelled or
                ProductionDirectNativeEventKindV5.NetworkChanged => NoFrame(value) && flags == 0 && value.Status == 0,
                ProductionDirectNativeEventKindV5.QueueLimit => NoFrame(value) && flags == 0 &&
                    value.Status == ProductionDirectNativeAbiV5.ErrorQueueFull,
                ProductionDirectNativeEventKindV5.PathReady => NoFrame(value) && value.Status == 0 &&
                    flags is ProductionDirectNativeEventFlagsV5.Lan or ProductionDirectNativeEventFlagsV5.Nat,
                ProductionDirectNativeEventKindV5.DirectFailed => NoFrame(value) && flags == 0 && value.Status < 0,
                ProductionDirectNativeEventKindV5.Ping or ProductionDirectNativeEventKindV5.Pong =>
                    value.Status == 0 && hasFrame && value.PayloadBytes == 0 && value.ReleasedFrames == 0 &&
                    flags is ProductionDirectNativeEventFlagsV5.Outbound or ProductionDirectNativeEventFlagsV5.Inbound,
                ProductionDirectNativeEventKindV5.SignRequest => value.Status == 0 && value.FrameSequence > 0 &&
                    value.PayloadBytes > 0 && value.ReleasedFrames == 0 && flags == 0,
                _ => false
            };
            if (!valid) return false;
            result = new ProductionDirectNativeEventV5
            {
                Kind = kind, Status = DecodeStatus(value.Status), Flags = flags,
                PathEpoch = value.PathEpoch, AttemptId = GuidFromBytes(value.AttemptId),
                FrameSequence = value.FrameSequence, PayloadBytes = value.PayloadBytes,
                ReleasedFrames = value.ReleasedFrames
            };
            return true;
        }

        private static bool NoFrame(TermLinkDirectEventV5 value) =>
            value.FrameSequence == 0 && value.PayloadBytes == 0 && value.ReleasedFrames == 0;

        private static bool IsRouteActionProbe(TermLinkDirectRouteActionV5 value) =>
            value.AbiVersion == 0 && value.StructSize == 0 && value.Kind == uint.MaxValue &&
            value.Direction == 0 && value.EnvelopeSequence == 0 && value.DirectFrameSequence == 0 &&
            value.Accepted == 0 && value.IsEcho == 0 && value.Carrier == 0 && value.Flags == 0 &&
            value.Reason == 0 && value.PayloadBytes == 0 && value.SessionId?.All(item => item == 0) == true &&
            value.BarrierId?.All(item => item == 0) == true &&
            value.BarrierSha256?.All(item => item == 0) == true &&
            value.Reserved?.All(item => item == 0) == true && value.Path.AbiVersion == 0 &&
            value.Path.StructSize == 0 && value.Path.PathEpoch == 0 &&
            value.Path.ConnectionId?.All(item => item == 0) == true &&
            value.Path.AttemptId?.All(item => item == 0) == true &&
            value.Path.Reserved?.All(item => item == 0) == true;

        private static bool IsEventProbe(TermLinkDirectEventV5 value) =>
            value.AbiVersion == 0 && value.StructSize == 0 && value.Kind == uint.MaxValue &&
            value.Status == 0 && value.Flags == 0 && value.Reserved0 == 0 && value.PathEpoch == 0 &&
            value.AttemptId?.All(item => item == 0) == true && value.FrameSequence == 0 &&
            value.PayloadBytes == 0 && value.ReleasedFrames == 0 &&
            value.Reserved?.All(item => item == 0) == true;

        private static bool TryDecodePathInfo(TermLinkDirectPathInfoV5 value,
            ProductionDirectPathFenceV5 expected, out ProductionDirectPathInfoV5 result)
        {
            result = null;
            if (value.AbiVersion != 5 || value.StructSize != ProductionDirectNativeAbiV5.PathInfoSize ||
                !TryDecodePath(value.Path, out var path) || !path.Equals(expected) ||
                value.PathKind > 2 || value.Ready > 1 || value.DirectPathBinding?.Length != 32 ||
                value.Reserved?.Length != 2 || value.Reserved.Any(item => item != 0) ||
                (value.Ready == 0 && (value.PathKind != 0 || value.DirectPathBinding.Any(item => item != 0))) ||
                (value.Ready == 1 && (value.PathKind == 0 || value.DirectPathBinding.All(item => item == 0))))
                return false;
            result = new ProductionDirectPathInfoV5
            {
                Path = path, PathKind = value.PathKind == 0 ? null : (ProductionDirectPathKindV5)value.PathKind,
                Ready = value.Ready == 1, DirectPathBinding = value.DirectPathBinding.ToArray()
            };
            return true;
        }

        private static bool TryDecodeRouteAction(TermLinkDirectRouteActionV5 value, byte[] payload,
            out ProductionDirectRouteActionV5 result)
        {
            result = null;
            if (value.AbiVersion != 5 || value.StructSize != ProductionDirectNativeAbiV5.RouteActionSize ||
                !TryDecodePath(value.Path, out var path) ||
                value.Kind is not (1 or 2 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11) ||
                value.Direction is < 1 or > 2 || value.SessionId?.Length != 16 ||
                value.BarrierId?.Length != 16 || value.BarrierSha256?.Length != 32 ||
                value.Accepted > 1 || value.IsEcho > 1 || value.Carrier > 3 || value.Flags != 0 ||
                !KnownReason(value.Reason) || value.PayloadBytes > ProductionDirectNativeAbiV5.MaximumEnvelopeBytes ||
                value.Reserved?.Length != 2 || value.Reserved.Any(item => item != 0)) return false;
            var kind = (ProductionDirectRouteActionKindV5)value.Kind;
            var session = GuidFromBytes(value.SessionId);
            var barrier = GuidFromBytes(value.BarrierId);
            var hashZero = value.BarrierSha256.All(item => item == 0);
            var hasEnvelope = session != Guid.Empty && value.EnvelopeSequence > 0 && value.PayloadBytes > 0;
            var payloadMatches = payload.Length == value.PayloadBytes;
            var metadataOnlyPayload = payload.Length == 0 && value.PayloadBytes > 0;
            var valid = kind switch
            {
                ProductionDirectRouteActionKindV5.DirectEnvelope => hasEnvelope && metadataOnlyPayload &&
                    ValidFrame(value.DirectFrameSequence) && value.Carrier is 2 or 3 &&
                    barrier == Guid.Empty && hashZero && value.Accepted == 0 && value.IsEcho == 0 &&
                    value.Reason is <= 9,
                ProductionDirectRouteActionKindV5.RelayEnvelope => hasEnvelope && payloadMatches &&
                    value.DirectFrameSequence == 0 && value.Carrier == 1 && barrier == Guid.Empty && hashZero &&
                    value.Accepted == 0 && value.IsEcho == 0 && value.Reason is <= 9,
                ProductionDirectRouteActionKindV5.Accept => session == Guid.Empty && value.EnvelopeSequence == 0 &&
                    value.DirectFrameSequence == 0 && barrier != Guid.Empty && !hashZero && value.IsEcho == 0 &&
                    value.Carrier == 0 && value.PayloadBytes == 0 && payload.Length == 0 &&
                    (value.Accepted == 1 ? value.Reason == 0 : value.Reason is >= 101 and <= 111),
                ProductionDirectRouteActionKindV5.Commit => session == Guid.Empty && value.EnvelopeSequence == 0 &&
                    value.DirectFrameSequence == 0 && barrier != Guid.Empty && !hashZero && value.Accepted == 0 &&
                    value.Carrier == 0 && value.Reason == 0 && value.PayloadBytes == 0 && payload.Length == 0,
                ProductionDirectRouteActionKindV5.Fallback => session == Guid.Empty && value.EnvelopeSequence == 0 &&
                    (value.DirectFrameSequence is 0 or 3 || ValidFrame(value.DirectFrameSequence)) &&
                    hashZero && value.Accepted == 0 && value.IsEcho == 0 && value.Carrier == 0 &&
                    value.Reason is >= 1 and <= 9 && value.PayloadBytes == 0 && payload.Length == 0,
                ProductionDirectRouteActionKindV5.Carrier => session == Guid.Empty && value.EnvelopeSequence == 0 &&
                    value.DirectFrameSequence == 0 && barrier == Guid.Empty && hashZero && value.Accepted == 0 &&
                    value.IsEcho == 0 && value.Carrier is >= 1 and <= 3 && value.Reason == 0 &&
                    value.PayloadBytes == 0 && payload.Length == 0,
                ProductionDirectRouteActionKindV5.SessionCarrier => session != Guid.Empty &&
                    value.EnvelopeSequence == 0 && value.DirectFrameSequence == 0 && barrier == Guid.Empty &&
                    hashZero && value.Accepted == 0 && value.IsEcho == 0 &&
                    value.Carrier is >= 1 and <= 3 && value.Reason == 0 &&
                    value.PayloadBytes == 0 && payload.Length == 0,
                ProductionDirectRouteActionKindV5.IngressDeliver => hasEnvelope && payloadMatches &&
                    value.DirectFrameSequence == 0 && barrier == Guid.Empty && hashZero && value.Accepted == 0 &&
                    value.IsEcho == 0 && value.Carrier is >= 1 and <= 3 && value.Reason == 0,
                ProductionDirectRouteActionKindV5.IngressHold or ProductionDirectRouteActionKindV5.IngressDrop =>
                    hasEnvelope && metadataOnlyPayload && value.DirectFrameSequence == 0 &&
                    barrier == Guid.Empty && hashZero && value.Accepted == 0 && value.IsEcho == 0 &&
                    value.Carrier is >= 1 and <= 3 && value.Reason == 0,
                _ => false
            };
            if (!valid) return false;
            result = new ProductionDirectRouteActionV5
            {
                Path = path, Kind = kind, Direction = (ProductionDirectDirectionV5)value.Direction,
                SessionId = session, EnvelopeSequence = value.EnvelopeSequence,
                DirectFrameSequence = value.DirectFrameSequence, BarrierId = barrier,
                BarrierSha256 = value.BarrierSha256.ToArray(), Accepted = value.Accepted == 1,
                IsEcho = value.IsEcho == 1,
                Carrier = value.Carrier == 0 ? null : (ProductionDirectCarrierV5)value.Carrier,
                Reason = (ProductionDirectRouteReasonV5)value.Reason, PayloadBytes = value.PayloadBytes,
                Payload = payload.ToArray()
            };
            return true;
        }

        private static bool TryEncodePromote(ProductionDirectPromoteRequestV5 value,
            out TermLinkDirectPromoteV5 result)
        {
            result = default;
            var nomination = value.Nomination;
            if (value.Path == null || nomination == null || value.Path.PathEpoch != nomination.Epoch ||
                value.Path.AttemptId != nomination.AttemptId || nomination.CheckRoundId == Guid.Empty ||
                nomination.NominationId == Guid.Empty || nomination.WinningPair?.Initiator == null ||
                nomination.WinningPair.Responder == null || nomination.Role is not
                    (ProductionDirectNativeRole.Initiator or ProductionDirectNativeRole.Responder) ||
                nomination.TimeoutMilliseconds is < 500 or > 10_000 ||
                !ExactNonZero(nomination.PeerCertificateSha256, 32) ||
                !ExactNonZero(nomination.PeerDeviceSigningPublicKey, 32) ||
                nomination.LocalProofNonce?.Length is < 16 or > 32 ||
                nomination.LocalProofNonce.All(item => item == 0) || nomination.NowMilliseconds < 0 ||
                !TryEncodePair(nomination.WinningPair, out var pair)) return false;
            result = new TermLinkDirectPromoteV5
            {
                AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.PromoteSize, Path = EncodePath(value.Path),
                CheckRoundId = GuidBytes(nomination.CheckRoundId), NominationId = GuidBytes(nomination.NominationId),
                WinningPair = pair, Role = (uint)nomination.Role,
                TimeoutMilliseconds = nomination.TimeoutMilliseconds, SelectionConfirmed = 1,
                LocalProofNonceLength = checked((uint)nomination.LocalProofNonce.Length),
                PeerCertificateSha256 = nomination.PeerCertificateSha256.ToArray(),
                PeerDeviceSigningPublicKey = nomination.PeerDeviceSigningPublicKey.ToArray(),
                LocalProofNonce = Padded(nomination.LocalProofNonce, 32),
                NowMilliseconds = nomination.NowMilliseconds,
                LocalOutboundDirection = (uint)ProductionDirectDirectionV5.PcToAndroid,
                Reserved = new ulong[3]
            };
            return true;
        }

        private static bool TryEncodeProposal(ProductionDirectProposalRequestV5 value,
            out TermLinkDirectProposalV5 result)
        {
            result = default;
            if (!ValidDirection(value.Direction) || value.BarrierId == Guid.Empty ||
                !ExactNonZero(value.BarrierSha256, 32) || !ExactNonZero(value.DirectPathBinding, 32) ||
                value.DuplicateGraceMilliseconds > 5_000 || value.TimeoutMilliseconds == 0 ||
                value.Sessions == null || value.Sessions.Count is < 1 or > 32 ||
                value.Sessions.Any(item => item == null || item.SessionId == Guid.Empty ||
                    item.FirstDirectSequence == 0) ||
                value.Sessions.Select(item => item.SessionId).Distinct().Count() != value.Sessions.Count) return false;
            var sessions = new TermLinkDirectSessionBarrierV5[32];
            for (var index = 0; index < value.Sessions.Count; index++)
                sessions[index] = new TermLinkDirectSessionBarrierV5
                {
                    SessionId = GuidBytes(value.Sessions[index].SessionId),
                    FirstDirectSequence = value.Sessions[index].FirstDirectSequence
                };
            for (var index = value.Sessions.Count; index < sessions.Length; index++)
                sessions[index].SessionId = new byte[16];
            result = new TermLinkDirectProposalV5
            {
                AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.ProposalSize, Path = EncodePath(value.Path),
                Direction = (uint)value.Direction, SessionCount = checked((uint)value.Sessions.Count),
                BarrierId = GuidBytes(value.BarrierId), BarrierSha256 = value.BarrierSha256.ToArray(),
                DirectPathBinding = value.DirectPathBinding.ToArray(),
                DuplicateGraceMilliseconds = value.DuplicateGraceMilliseconds,
                NowMilliseconds = value.NowMilliseconds, TimeoutMilliseconds = value.TimeoutMilliseconds,
                Sessions = sessions, Reserved = new ulong[2]
            };
            return true;
        }

        private static bool TryEncodeTransition(ProductionDirectTransitionRequestV5 value,
            out TermLinkDirectTransitionV5 result)
        {
            result = default;
            if (!ValidDirection(value.Direction) || value.BarrierId == Guid.Empty ||
                !ExactNonZero(value.BarrierSha256, 32)) return false;
            result = new TermLinkDirectTransitionV5
            {
                AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.TransitionSize,
                Path = EncodePath(value.Path), Direction = (uint)value.Direction,
                BarrierId = GuidBytes(value.BarrierId), BarrierSha256 = value.BarrierSha256.ToArray(),
                Accepted = value.Accepted ? 1u : 0u, IsEcho = value.IsEcho ? 1u : 0u,
                NowMilliseconds = value.NowMilliseconds, TimeoutMilliseconds = value.TimeoutMilliseconds,
                Reserved = new ulong[2]
            };
            return true;
        }

        private static bool TryEncodePair(ProductionDirectCandidatePairV4 value,
            out TermLinkDirectCandidatePairV5 result)
        {
            result = default;
            if (value == null || value.Rank == 0 || !TryEncodeCandidate(value.Initiator, out var initiator) ||
                !TryEncodeCandidate(value.Responder, out var responder) ||
                value.Initiator.CandidateId == value.Responder.CandidateId) return false;
            result = new TermLinkDirectCandidatePairV5
            {
                AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.CandidatePairSize,
                Rank = value.Rank, InitiatorCandidate = initiator, ResponderCandidate = responder,
                Reserved = new ulong[2]
            };
            return true;
        }

        private static bool TryEncodeCandidate(ProductionDirectCandidateV4 value,
            out TermLinkDirectCandidateV5 result)
        {
            result = default;
            if (value == null || value.CandidateId == Guid.Empty || value.Endpoint?.Address == null ||
                value.Endpoint.Port is < 1 or > 65_535 || value.Kind is not
                    (ProductionDirectCandidateKindV4.Host or ProductionDirectCandidateKindV4.ServerReflexive) ||
                value.Priority is 0 or > int.MaxValue || value.NetworkCost > 999 ||
                value.ExpiresAtMilliseconds <= 0 ||
                (value.Kind == ProductionDirectCandidateKindV4.Host ? value.RelatedCandidateId != Guid.Empty :
                    value.RelatedCandidateId == Guid.Empty)) return false;
            var bytes = value.Endpoint.Address.GetAddressBytes();
            if (bytes.Length is not (4 or 16) || value.Endpoint.Address.Equals(IPAddress.Any) ||
                value.Endpoint.Address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(value.Endpoint.Address) ||
                value.Endpoint.Address.IsIPv6Multicast) return false;
            var ip = new byte[16];
            Array.Copy(bytes, ip, bytes.Length);
            result = new TermLinkDirectCandidateV5
            {
                AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.CandidateSize,
                CandidateId = GuidBytes(value.CandidateId), RelatedCandidateId = GuidBytes(value.RelatedCandidateId),
                Endpoint = new TermLinkSocketAddressV5
                {
                    AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.SocketAddressSize,
                    Ip = ip, IpLength = checked((uint)bytes.Length), Port = checked((uint)value.Endpoint.Port),
                    Reserved = new ulong[2]
                },
                Kind = (uint)value.Kind, Carrier = 1, Priority = value.Priority,
                NetworkCost = value.NetworkCost, ExpiresAtMilliseconds = value.ExpiresAtMilliseconds,
                Reserved = new ulong[2]
            };
            return true;
        }

        private bool CanCallLocked() => !disposed && nativeAvailable && api != null && handle != IntPtr.Zero;
        private bool Current(ProductionDirectPathFenceV5 value) =>
            ValidPath(value) && currentPath != null && currentPath.Equals(value);
        private static bool ValidPath(ProductionDirectPathFenceV5 value) => value != null &&
            value.PathEpoch is >= 1 and <= ProductionDirectNativeAbiV5.MaximumFrameSequence &&
            value.ConnectionId != Guid.Empty && value.AttemptId != Guid.Empty;
        private static bool ValidDirection(ProductionDirectDirectionV5 value) =>
            value is ProductionDirectDirectionV5.PcToAndroid or ProductionDirectDirectionV5.AndroidToPc;
        private static bool ValidEnvelope(byte[] value) => value?.Length is >= 1 and
            <= ProductionDirectNativeAbiV5.MaximumEnvelopeBytes;
        private static bool ValidFrame(ulong value) => value is >= ProductionDirectNativeAbiV5.FirstDataFrameSequence
            and <= ProductionDirectNativeAbiV5.MaximumFrameSequence;
        private static bool ExactNonZero(byte[] value, int length) =>
            value?.Length == length && value.Any(item => item != 0);
        private static bool KnownReason(uint value) => value is <= 9 or >= 101 and <= 111;
        private static bool IsKnownStatus(int value) => value is >= ProductionDirectNativeAbiV5.ErrorTicketExpired
            and <= ProductionDirectNativeAbiV5.Ok;

        private static TermLinkDirectPathV5 EncodePath(ProductionDirectPathFenceV5 value) => new()
        {
            AbiVersion = 5, StructSize = ProductionDirectNativeAbiV5.PathSize, PathEpoch = value.PathEpoch,
            ConnectionId = GuidBytes(value.ConnectionId), AttemptId = GuidBytes(value.AttemptId),
            Reserved = new ulong[2]
        };

        private static bool TryDecodePath(TermLinkDirectPathV5 value, out ProductionDirectPathFenceV5 result)
        {
            result = null;
            if (value.AbiVersion != 5 || value.StructSize != ProductionDirectNativeAbiV5.PathSize ||
                value.PathEpoch is < 1 or > ProductionDirectNativeAbiV5.MaximumFrameSequence ||
                value.ConnectionId?.Length != 16 || value.ConnectionId.All(item => item == 0) ||
                value.AttemptId?.Length != 16 || value.AttemptId.All(item => item == 0) ||
                value.Reserved?.Length != 2 || value.Reserved.Any(item => item != 0)) return false;
            result = new ProductionDirectPathFenceV5
            {
                PathEpoch = value.PathEpoch, ConnectionId = GuidFromBytes(value.ConnectionId),
                AttemptId = GuidFromBytes(value.AttemptId)
            };
            return true;
        }

        private static ProductionDirectPathFenceV5 Copy(ProductionDirectPathFenceV5 value) => value == null ? null :
            new ProductionDirectPathFenceV5
            {
                PathEpoch = value.PathEpoch, ConnectionId = value.ConnectionId, AttemptId = value.AttemptId
            };
        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
        private static Guid GuidFromBytes(byte[] value) => Guid.ParseExact(Convert.ToHexString(value), "N");
        private static byte[] Padded(byte[] value, int length)
        {
            var result = new byte[length]; Array.Copy(value, result, value.Length); return result;
        }

        private static ProductionDirectNativeResultV5 DecodeStatus(int value) =>
            value is >= ProductionDirectNativeAbiV5.ErrorTicketExpired and <= ProductionDirectNativeAbiV5.NoData
                ? (ProductionDirectNativeResultV5)value : ProductionDirectNativeResultV5.Protocol;
        private static ProductionDirectNativeResultV5 DecodePollStatus(int value) => value switch
        {
            0 => ProductionDirectNativeResultV5.NoData,
            1 => ProductionDirectNativeResultV5.Accepted,
            _ => ProductionDirectNativeResultV5.Protocol
        };

        private static ProductionDirectNativeReceiveV5 Receive(ProductionDirectNativeResultV5 status,
            ulong frameSequence = 0, byte[] envelope = null, uint requiredBytes = 0) => new()
        {
            Status = status, FrameSequence = frameSequence, Envelope = envelope ?? Array.Empty<byte>(),
            RequiredBytes = requiredBytes
        };
        private static ProductionDirectSigningPayloadV5 SigningPayload(ProductionDirectNativeResultV5 status,
            ulong requestId = 0, byte[] payload = null, uint requiredBytes = 0) => new()
        {
            Status = status, RequestId = requestId, Payload = payload ?? Array.Empty<byte>(),
            RequiredBytes = requiredBytes
        };

        private static void Zero(ref TermLinkDirectPathV5 value)
        {
            Zero(value.ConnectionId); Zero(value.AttemptId); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectCreateV5 value)
        {
            Zero(ref value.Path); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectPromoteV5 value)
        {
            Zero(ref value.Path); Zero(value.CheckRoundId); Zero(value.NominationId);
            Zero(ref value.WinningPair); Zero(value.PeerCertificateSha256);
            Zero(value.PeerDeviceSigningPublicKey); Zero(value.LocalProofNonce); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectCandidatePairV5 value)
        {
            Zero(ref value.InitiatorCandidate); Zero(ref value.ResponderCandidate); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectCandidateV5 value)
        {
            Zero(value.CandidateId); Zero(value.RelatedCandidateId); Zero(value.Endpoint.Ip);
            Zero(value.Endpoint.Reserved); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectSignatureV5 value)
        {
            Zero(ref value.Path); Zero(value.Signature); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectPathInfoV5 value)
        {
            Zero(ref value.Path); Zero(value.DirectPathBinding); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectProposalV5 value)
        {
            Zero(ref value.Path); Zero(value.BarrierId); Zero(value.BarrierSha256); Zero(value.DirectPathBinding);
            foreach (var session in value.Sessions ?? Array.Empty<TermLinkDirectSessionBarrierV5>())
                Zero(session.SessionId);
            Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectTransitionV5 value)
        {
            Zero(ref value.Path); Zero(value.BarrierId); Zero(value.BarrierSha256); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectCursorV5 value)
        {
            Zero(ref value.Path); Zero(value.SessionId); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectRouteEnvelopeV5 value)
        {
            Zero(ref value.Path); Zero(value.SessionId); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectIngressV5 value)
        {
            Zero(ref value.Path); Zero(value.SessionId); Zero(value.Reserved);
        }
        private static void Zero(ref TermLinkDirectRouteActionV5 value)
        {
            Zero(ref value.Path); Zero(value.SessionId); Zero(value.BarrierId);
            Zero(value.BarrierSha256); Zero(value.Reserved);
        }
        private static void Zero(byte[] value) { if (value != null) Array.Clear(value); }
        private static void Zero(ulong[] value) { if (value != null) Array.Clear(value); }

        private sealed class PromotionOperation : IProductionDirectNativePromotionOperationV4
        {
            private readonly IProductionDirectNativeApiV5 api;
            private readonly IntPtr v5Handle;
            private TermLinkDirectPromoteV5 promote;

            internal PromotionOperation(IProductionDirectNativeApiV5 api, IntPtr v5Handle,
                TermLinkDirectPromoteV5 promote)
            {
                this.api = api;
                this.v5Handle = v5Handle;
                this.promote = promote;
            }

            public int Invoke(IntPtr transientV4Handle, ref TermLinkDirectCommitNominationV4 nomination) =>
                api.Promote(v5Handle, transientV4Handle, ref promote);
        }

        private void FailClosedLocked()
        {
            nativeAvailable = false;
            currentPath = null;
            drainingPaths.Clear();
            pendingSigningRequests.Clear();
            if (handle != IntPtr.Zero)
            {
                try { api?.Destroy(handle); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                handle = IntPtr.Zero;
            }
            ReleaseApiLocked();
        }

        private void ReleaseApiLocked()
        {
            try { api?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            api = null;
        }
    }
}
