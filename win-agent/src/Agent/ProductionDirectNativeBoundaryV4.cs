using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TermLink.Agent
{
    internal enum ProductionDirectCandidateKindV4 : uint
    {
        Host = 1,
        ServerReflexive = 2
    }

    internal enum ProductionDirectNativeEventKindV4 : uint
    {
        IdentityReady = 1,
        StateChanged = 2,
        DirectFailed = 3,
        SignRequest = 4,
        CandidateBatch = 5,
        PairValidated = 6,
        ChecksComplete = 7,
        NominationReady = 8,
        ShadowReady = 9,
        IgnoredStale = 10
    }

    internal sealed class ProductionDirectCandidateV4 : IEquatable<ProductionDirectCandidateV4>
    {
        public Guid CandidateId { get; init; }
        public Guid RelatedCandidateId { get; init; }
        public ProductionDirectNativeEndpoint Endpoint { get; init; }
        public ProductionDirectCandidateKindV4 Kind { get; init; }
        public uint Priority { get; init; }
        public uint NetworkCost { get; init; }
        public long ExpiresAtMilliseconds { get; init; }

        public bool Equals(ProductionDirectCandidateV4 other) => other != null &&
            CandidateId == other.CandidateId && RelatedCandidateId == other.RelatedCandidateId &&
            Kind == other.Kind && Priority == other.Priority && NetworkCost == other.NetworkCost &&
            ExpiresAtMilliseconds == other.ExpiresAtMilliseconds && Endpoint != null &&
            other.Endpoint != null && Endpoint.Port == other.Endpoint.Port &&
            Endpoint.Address.Equals(other.Endpoint.Address);

        public override bool Equals(object obj) => Equals(obj as ProductionDirectCandidateV4);
        public override int GetHashCode() => HashCode.Combine(CandidateId, RelatedCandidateId, Kind,
            Priority, NetworkCost, ExpiresAtMilliseconds, Endpoint?.Address, Endpoint?.Port);
    }

    internal sealed class ProductionDirectCandidatePairV4 : IEquatable<ProductionDirectCandidatePairV4>
    {
        public uint Rank { get; init; }
        public ProductionDirectCandidateV4 Initiator { get; init; }
        public ProductionDirectCandidateV4 Responder { get; init; }

        public string Key => Initiator?.CandidateId.ToString("N") + ":" +
            Responder?.CandidateId.ToString("N");

        public bool Equals(ProductionDirectCandidatePairV4 other) => other != null && Rank == other.Rank &&
            Equals(Initiator, other.Initiator) && Equals(Responder, other.Responder);
        public override bool Equals(object obj) => Equals(obj as ProductionDirectCandidatePairV4);
        public override int GetHashCode() => HashCode.Combine(Rank, Initiator, Responder);
    }

    internal sealed class ProductionDirectGatherRequestV4
    {
        public ulong Epoch { get; init; }
        public Guid AttemptId { get; init; }
        public ulong NetworkHandle { get; init; }
        public ProductionDirectNativeEndpoint LocalBind { get; init; }
        public IPAddress AdvertisedLocalAddress { get; init; }
        public uint NetworkCost { get; init; }
        public long CandidateExpiresAtMilliseconds { get; init; }
        public IReadOnlyList<ProductionDirectNativeEndpoint> StunServers { get; init; } =
            Array.Empty<ProductionDirectNativeEndpoint>();
        public uint GatherTimeoutMilliseconds { get; init; } = 1_500;
    }

    internal sealed class ProductionDirectBeginChecksRequestV4
    {
        public ulong Epoch { get; init; }
        public Guid AttemptId { get; init; }
        public Guid ConnectionId { get; init; }
        public Guid LocalDeviceId { get; init; }
        public Guid PeerDeviceId { get; init; }
        public ulong NetworkHandle { get; init; }
        public long NowMilliseconds { get; init; }
        public uint TimeoutMilliseconds { get; init; } = 10_000;
        public ProductionDirectNativeRole Role { get; init; }
        public byte[] Ticket { get; init; }
        public Guid TicketId { get; init; }
        public Guid CheckRoundId { get; init; }
        public byte[] ConnectivityNonce { get; init; }
        public byte[] ServerSigningPublicKey { get; init; }
        public byte[] LocalSendProbeHmacKey { get; init; }
        public byte[] PeerReceiveProbeHmacKey { get; init; }
        public uint BatchSequence { get; init; }
        public uint TotalPairCount { get; init; }
        public bool PlanComplete { get; init; }
        public IReadOnlyList<ProductionDirectCandidatePairV4> Pairs { get; init; } =
            Array.Empty<ProductionDirectCandidatePairV4>();
    }

    internal sealed class ProductionDirectAppendPairsRequestV4
    {
        public ulong Epoch { get; init; }
        public Guid AttemptId { get; init; }
        public Guid CheckRoundId { get; init; }
        public uint BatchSequence { get; init; }
        public uint TotalPairCount { get; init; }
        public bool PlanComplete { get; init; }
        public IReadOnlyList<ProductionDirectCandidatePairV4> Pairs { get; init; } =
            Array.Empty<ProductionDirectCandidatePairV4>();
    }

    internal sealed class ProductionDirectCommitRequestV4
    {
        public ulong Epoch { get; init; }
        public Guid AttemptId { get; init; }
        public Guid CheckRoundId { get; init; }
        public Guid NominationId { get; init; }
        public ProductionDirectCandidatePairV4 WinningPair { get; init; }
        public ProductionDirectNativeRole Role { get; init; }
        public uint TimeoutMilliseconds { get; init; } = 10_000;
        public byte[] PeerCertificateSha256 { get; init; }
        public byte[] PeerDeviceSigningPublicKey { get; init; }
        public byte[] LocalProofNonce { get; init; }
        public long NowMilliseconds { get; init; }
    }

    internal sealed class ProductionDirectNativeEventV4
    {
        public ProductionDirectNativeEventKindV4 Kind { get; init; }
        public ProductionDirectNativeState State { get; init; }
        public ProductionDirectNativeReason Reason { get; init; }
        public ulong Epoch { get; init; }
        public ulong Generation { get; init; }
        public ulong RequestId { get; init; }
        public Guid AttemptId { get; init; }
        public ProductionDirectNativeEndpoint Endpoint { get; init; }
        public byte[] OpaquePayload { get; init; }
        public uint BatchSequence { get; init; }
        public uint TotalCount { get; init; }
        public bool Complete { get; init; }
        public IReadOnlyList<ProductionDirectCandidateV4> Candidates { get; init; } =
            Array.Empty<ProductionDirectCandidateV4>();
        public ProductionDirectCandidatePairV4 Pair { get; init; }
        public Guid CheckRoundId { get; init; }
        public Guid NominationId { get; init; }
        public uint RoundTripTimeMilliseconds { get; init; }
        public uint ProbeAttempts { get; init; }
        public uint ValidatedPairCount { get; init; }
        public uint ExhaustedPairCount { get; init; }
        public byte[] DirectPathBinding { get; init; }
    }

    /// <summary>
    /// Synchronous, one-shot bridge into the ABI v5 promotion call. The V4
    /// handle is valid only for the duration of <see cref="Invoke"/> while the
    /// V4 owner lock is held. Implementations must neither retain nor expose it.
    /// </summary>
    internal interface IProductionDirectNativePromotionOperationV4
    {
        int Invoke(
            IntPtr transientV4Handle,
            ref TermLinkDirectCommitNominationV4 nomination);
    }

    internal readonly struct ProductionDirectNativePromotionResultV4
    {
        private ProductionDirectNativePromotionResultV4(
            ProductionDirectNativeCommandResult commandResult,
            bool enteredNativeSeam,
            bool nativeCallCompleted,
            int nativeStatus)
        {
            CommandResult = commandResult;
            EnteredNativeSeam = enteredNativeSeam;
            NativeCallCompleted = nativeCallCompleted;
            NativeStatus = nativeStatus;
        }

        internal ProductionDirectNativeCommandResult CommandResult { get; }
        internal bool EnteredNativeSeam { get; }
        internal bool NativeCallCompleted { get; }
        internal int NativeStatus { get; }

        internal static ProductionDirectNativePromotionResultV4 NotInvoked(
            ProductionDirectNativeCommandResult result) => new(result, false, false, 0);

        internal static ProductionDirectNativePromotionResultV4 Completed(int status) =>
            new(status == 0 ? ProductionDirectNativeCommandResult.Accepted :
                ProductionDirectNativeCommandResult.RelayFallback, true, true, status);

        internal static ProductionDirectNativePromotionResultV4 Faulted() =>
            new(ProductionDirectNativeCommandResult.RelayFallback, true, false, 0);
    }

    internal interface IProductionDirectShadowNativeV4 : IDisposable
    {
        ProductionDirectNativeSnapshot Snapshot { get; }
        ProductionDirectNativeCommandResult Gather(ProductionDirectGatherRequestV4 request);
        ProductionDirectNativeCommandResult BeginChecks(ProductionDirectBeginChecksRequestV4 request);
        ProductionDirectNativeCommandResult AppendCheckPairs(ProductionDirectAppendPairsRequestV4 request);
        ProductionDirectNativeCommandResult CommitNomination(ProductionDirectCommitRequestV4 request);
        ProductionDirectNativeCommandResult Cancel(ulong epoch, Guid attemptId);
        ProductionDirectNativeCommandResult NetworkChanged(ulong epoch, Guid attemptId);
        ProductionDirectNativeCommandResult SubmitSignature(
            ulong epoch, Guid attemptId, ulong requestId, byte[] signature);
        IReadOnlyList<ProductionDirectNativeEventV4> Poll(int maximumEvents = 32);
    }

    /// <summary>
    /// One-shot source for transferring a nominated V4 socket into V5.  The
    /// implementation keeps the V4 owner lock held for the complete native
    /// call, so no opaque handle can escape into orchestration code.
    /// </summary>
    internal interface IProductionDirectPromotableNativeV4 :
        IProductionDirectShadowNativeV4
    {
        ProductionDirectNativePromotionResultV4 TryPromoteNomination(
            ProductionDirectCommitRequestV4 request,
            IProductionDirectNativePromotionOperationV4 operation);
    }

    /// <summary>
    /// Owned-copy CLR boundary for the independent ABI v4 socket owner. It is
    /// intentionally incapable of observing or replacing Relay/session state.
    /// Every validation failure retires only this Direct handle.
    /// </summary>
    internal sealed class ProductionDirectNativeBoundaryV4 :
        IProductionDirectPromotableNativeV4
    {
        private readonly object sync = new();
        private readonly bool enabled;
        private readonly uint queueCapacity;
        private readonly IProductionDirectNativeLoaderV4 loader;
        private readonly HashSet<(ulong Epoch, Guid Attempt, ulong Request)> pendingSignatures = new();
        private readonly Dictionary<Guid, ProductionDirectCandidateV4> localCandidates = new();
        private readonly Dictionary<string, ProductionDirectCandidatePairV4> plannedPairs =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProductionDirectCandidatePairV4> validatedPairs =
            new(StringComparer.Ordinal);
        private IProductionDirectNativeApiV4 api;
        private IntPtr handle;
        private ulong epoch;
        private Guid attemptId;
        private ulong generation;
        private uint candidateBatchSequence;
        private uint candidateTotal;
        private uint pairBatchSequence;
        private uint pairTotal;
        private Guid checkRoundId;
        private IPAddress advertisedAddress;
        private ProductionDirectNativeRole role;
        private bool gatheringComplete;
        private bool planComplete;
        private bool committed;
        private bool promotionAttempted;
        private bool disposed;
        private ProductionDirectNativeSnapshot snapshot;

        public ProductionDirectNativeBoundaryV4(
            bool enabled = false,
            uint queueCapacity = 32,
            IProductionDirectNativeLoaderV4 loader = null)
        {
            this.enabled = enabled;
            this.queueCapacity = queueCapacity;
            this.loader = enabled ? loader ?? new ProductionDirectNativeLibraryLoaderV4() : loader;
            snapshot = RelaySnapshot(enabled ? ProductionDirectNativeFault.None :
                ProductionDirectNativeFault.Disabled, enabled ? ProductionDirectNativeReason.None :
                ProductionDirectNativeReason.Disabled, false);
            if (!enabled) return;
            lock (sync)
            {
                try
                {
                    if (queueCapacity is < 1 or > ProductionDirectNativeAbiV4.MaximumQueueCapacity ||
                        !ProductionDirectNativeAbiV4.TryValidateLayout(out _) ||
                        !this.loader.TryLoad(out api, out _) || api == null)
                    {
                        FailLocked(ProductionDirectNativeFault.NativeUnavailable,
                            ProductionDirectNativeReason.InvalidAbi, true);
                        return;
                    }
                    if (api.AbiVersion() != ProductionDirectNativeAbiV4.AbiVersion || !CreateLocked())
                    {
                        FailLocked(ProductionDirectNativeFault.AbiMismatch,
                            ProductionDirectNativeReason.InvalidAbi, true);
                        return;
                    }
                    snapshot = RelaySnapshot(ProductionDirectNativeFault.None,
                        ProductionDirectNativeReason.None, true);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    FailLocked(ProductionDirectNativeFault.NativeUnavailable,
                        ProductionDirectNativeReason.Internal, true);
                }
            }
        }

        public ProductionDirectNativeSnapshot Snapshot { get { lock (sync) return snapshot; } }

        public ProductionDirectNativeCommandResult Gather(ProductionDirectGatherRequestV4 request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (request == null || request.Epoch <= epoch) return ProductionDirectNativeCommandResult.IgnoredStale;
                TermLinkDirectGatherV4 native;
                try { native = EncodeGather(request); }
                catch (Exception error) when (IsInputError(error)) { return InvalidLocked(); }
                epoch = request.Epoch;
                attemptId = request.AttemptId;
                advertisedAddress = request.AdvertisedLocalAddress;
                ResetAttemptLocked();
                snapshot = RelaySnapshot(ProductionDirectNativeFault.None,
                    ProductionDirectNativeReason.None, true);
                try { return InterpretLocked(api.Gather(handle, ref native)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { return PanicLocked(); }
                finally { ZeroGather(ref native); }
            }
        }

        public ProductionDirectNativeCommandResult BeginChecks(ProductionDirectBeginChecksRequestV4 request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (request == null || !Current(request.Epoch, request.AttemptId))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                // ABI v4 deliberately transfers the still-gathering socket owner into the
                // check round. HOST pairs may begin before STUN trickle completes; later
                // candidate/pair batches append to that same native owner.
                if (checkRoundId != Guid.Empty) return InvalidLocked();
                TermLinkDirectBeginChecksV4 native;
                try
                {
                    ValidateBegin(request);
                    native = EncodeBegin(request);
                }
                catch (Exception error) when (IsInputError(error)) { return InvalidLocked(); }
                try
                {
                    var result = InterpretLocked(api.BeginChecks(handle, ref native));
                    if (result == ProductionDirectNativeCommandResult.Accepted)
                    {
                        checkRoundId = request.CheckRoundId;
                        role = request.Role;
                        pairBatchSequence = request.BatchSequence;
                        pairTotal = request.TotalPairCount;
                        planComplete = request.PlanComplete;
                        AddPlannedPairs(request.Pairs);
                    }
                    return result;
                }
                catch (Exception error) when (error is not OutOfMemoryException) { return PanicLocked(); }
                finally { ZeroBegin(ref native); }
            }
        }

        public ProductionDirectNativeCommandResult AppendCheckPairs(ProductionDirectAppendPairsRequestV4 request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (request == null || !Current(request.Epoch, request.AttemptId))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                if (checkRoundId == Guid.Empty || request.CheckRoundId != checkRoundId || planComplete ||
                    request.BatchSequence != pairBatchSequence + 1 ||
                    request.TotalPairCount != pairTotal + (uint)(request.Pairs?.Count ?? 0))
                    return InvalidLocked();
                TermLinkDirectAppendCheckPairsV4 native;
                try
                {
                    ValidatePairBatch(request.Pairs, pairTotal + 1, request.PlanComplete, role);
                    native = EncodeAppend(request);
                }
                catch (Exception error) when (IsInputError(error)) { return InvalidLocked(); }
                try
                {
                    var result = InterpretLocked(api.AppendCheckPairs(handle, ref native));
                    if (result == ProductionDirectNativeCommandResult.Accepted)
                    {
                        pairBatchSequence = request.BatchSequence;
                        pairTotal = request.TotalPairCount;
                        planComplete = request.PlanComplete;
                        AddPlannedPairs(request.Pairs);
                    }
                    return result;
                }
                catch (Exception error) when (error is not OutOfMemoryException) { return PanicLocked(); }
                finally { ZeroAppend(ref native); }
            }
        }

        public ProductionDirectNativeCommandResult CommitNomination(ProductionDirectCommitRequestV4 request)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (request == null || !Current(request.Epoch, request.AttemptId))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                if (committed || promotionAttempted || !planComplete ||
                    request.CheckRoundId != checkRoundId || request.Role != role ||
                    request.NominationId == Guid.Empty || request.WinningPair == null ||
                    !validatedPairs.TryGetValue(request.WinningPair.Key, out var validated) ||
                    !validated.Equals(request.WinningPair)) return InvalidLocked();
                TermLinkDirectCommitNominationV4 native;
                try { native = EncodeCommit(request); }
                catch (Exception error) when (IsInputError(error)) { return InvalidLocked(); }
                try
                {
                    var result = InterpretLocked(api.CommitNomination(handle, ref native));
                    if (result == ProductionDirectNativeCommandResult.Accepted) committed = true;
                    return result;
                }
                catch (Exception error) when (error is not OutOfMemoryException) { return PanicLocked(); }
                finally { ZeroCommit(ref native); }
            }
        }

        /// <summary>
        /// Atomically consumes the current validated V4 attempt into a caller-
        /// supplied ABI v5 promotion operation. The operation executes while
        /// this boundary's owner lock is held; cancel, network change, dispose,
        /// and successor commands therefore linearize entirely before or after
        /// it. Crossing the operation seam is destructive even when native v5
        /// rejects or throws: V4 is retired in all cases and cannot be retried.
        /// </summary>
        public ProductionDirectNativePromotionResultV4 TryPromoteNomination(
            ProductionDirectCommitRequestV4 request,
            IProductionDirectNativePromotionOperationV4 operation)
        {
            lock (sync)
            {
                if (!CanCallLocked())
                    return ProductionDirectNativePromotionResultV4.NotInvoked(
                        ProductionDirectNativeCommandResult.RelayFallback);
                if (request == null || !Current(request.Epoch, request.AttemptId))
                    return ProductionDirectNativePromotionResultV4.NotInvoked(
                        ProductionDirectNativeCommandResult.IgnoredStale);
                if (operation == null || committed || promotionAttempted || !planComplete ||
                    request.CheckRoundId != checkRoundId || request.Role != role ||
                    request.NominationId == Guid.Empty || request.WinningPair == null ||
                    !validatedPairs.TryGetValue(request.WinningPair.Key, out var validated) ||
                    !validated.Equals(request.WinningPair))
                {
                    InvalidLocked();
                    return ProductionDirectNativePromotionResultV4.NotInvoked(
                        ProductionDirectNativeCommandResult.RelayFallback);
                }

                TermLinkDirectCommitNominationV4 native;
                try { native = EncodeCommit(request); }
                catch (Exception error) when (IsInputError(error))
                {
                    InvalidLocked();
                    return ProductionDirectNativePromotionResultV4.NotInvoked(
                        ProductionDirectNativeCommandResult.RelayFallback);
                }

                // This assignment is the managed one-shot linearization point.
                // It intentionally precedes the external call: once the V5
                // call can observe the V4 socket, no result permits a retry.
                promotionAttempted = true;
                try
                {
                    var status = operation.Invoke(handle, ref native);
                    snapshot = RelaySnapshot(status == 0 ? ProductionDirectNativeFault.None :
                        ProductionDirectNativeFault.WorkerStopped, status == 0 ?
                        ProductionDirectNativeReason.None : ProductionDirectNativeReason.Internal, false);
                    return ProductionDirectNativePromotionResultV4.Completed(status);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    snapshot = RelaySnapshot(ProductionDirectNativeFault.NativePanic,
                        ProductionDirectNativeReason.Internal, false);
                    return ProductionDirectNativePromotionResultV4.Faulted();
                }
                finally
                {
                    // Native V4 either transferred its sole socket or must
                    // abandon it. Destroying the bridge is safe in both cases
                    // and guarantees that a failed V5 activation cannot reuse
                    // the old attempt.
                    RetireHandleLocked();
                    ZeroCommit(ref native);
                }
            }
        }

        public ProductionDirectNativeCommandResult Cancel(ulong valueEpoch, Guid valueAttempt) =>
            Control(valueEpoch, valueAttempt, false);

        public ProductionDirectNativeCommandResult NetworkChanged(ulong valueEpoch, Guid valueAttempt) =>
            Control(valueEpoch, valueAttempt, true);

        public ProductionDirectNativeCommandResult SubmitSignature(
            ulong valueEpoch, Guid valueAttempt, ulong requestId, byte[] signature)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                var key = (valueEpoch, valueAttempt, requestId);
                if (!Current(valueEpoch, valueAttempt) || !pendingSignatures.Remove(key))
                    return ProductionDirectNativeCommandResult.IgnoredStale;
                if (signature == null || signature.Length is < 1 or > ProductionDirectNativeAbiV4.MaximumSignatureBytes)
                    return InvalidLocked();
                var native = new TermLinkDirectSignatureV4
                {
                    AbiVersion = ProductionDirectNativeAbiV4.AbiVersion,
                    StructSize = ProductionDirectNativeAbiV4.SignatureSize,
                    Epoch = valueEpoch,
                    AttemptId = GuidBytes(valueAttempt),
                    RequestId = requestId,
                    SignatureLength = checked((uint)signature.Length),
                    Signature = Padded(signature, ProductionDirectNativeAbiV4.MaximumSignatureBytes),
                    Reserved = new ulong[2]
                };
                try { return InterpretLocked(api.SubmitSignature(handle, ref native)); }
                catch (Exception error) when (error is not OutOfMemoryException) { return PanicLocked(); }
                finally { Zero(native.Signature); }
            }
        }

        public IReadOnlyList<ProductionDirectNativeEventV4> Poll(int maximumEvents = 32)
        {
            lock (sync)
            {
                var result = new List<ProductionDirectNativeEventV4>();
                if (!CanCallLocked()) return result;
                if (maximumEvents is < 1 or > 64) { InvalidLocked(); return result; }
                for (var index = 0; index < maximumEvents; index++)
                {
                    var native = ProductionDirectNativeAbiV4.NewEventBuffer();
                    int status;
                    try { status = api.PollEvent(handle, ref native); }
                    catch (Exception error) when (error is not OutOfMemoryException) { PanicLocked(); break; }
                    if (status == 0) break;
                    if (status != 1) { InterpretLocked(status); break; }
                    if (!TryDecodeEvent(native, out var decoded)) { InvalidLocked(); break; }
                    if (decoded.Kind == ProductionDirectNativeEventKindV4.IgnoredStale)
                    {
                        // Native generations are global to the handle, including
                        // diagnostics for stale commands. Consume the sequence
                        // value so the next current-owner event remains +1.
                        if (decoded.Generation != generation + 1)
                        {
                            InvalidLocked();
                            break;
                        }
                        generation = decoded.Generation;
                        result.Add(decoded);
                        continue;
                    }
                    if (!Current(decoded.Epoch, decoded.AttemptId) || decoded.Generation != generation + 1)
                    {
                        InvalidLocked();
                        break;
                    }
                    generation = decoded.Generation;
                    if (!AcceptDecodedEventLocked(decoded)) { InvalidLocked(); break; }
                    result.Add(decoded);
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
                RetireHandleLocked();
                try { api?.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                api = null;
                snapshot = RelaySnapshot(ProductionDirectNativeFault.None,
                    ProductionDirectNativeReason.None, false, true);
            }
        }

        private bool AcceptDecodedEventLocked(ProductionDirectNativeEventV4 value)
        {
            if (value.Kind == ProductionDirectNativeEventKindV4.CandidateBatch)
            {
                if (gatheringComplete || value.BatchSequence != candidateBatchSequence + 1 ||
                    value.TotalCount != candidateTotal + value.Candidates.Count || value.TotalCount > 32 ||
                    (value.Candidates.Count == 0 && !value.Complete)) return false;
                foreach (var candidate in value.Candidates)
                {
                    if (localCandidates.ContainsKey(candidate.CandidateId) ||
                        localCandidates.Values.Any(existing => SameEndpoint(existing.Endpoint, candidate.Endpoint)))
                        return false;
                    if (candidate.Kind == ProductionDirectCandidateKindV4.Host)
                    {
                        if (candidateBatchSequence != 0 || candidate.RelatedCandidateId != Guid.Empty ||
                            !candidate.Endpoint.Address.Equals(advertisedAddress)) return false;
                    }
                    else if (!localCandidates.TryGetValue(candidate.RelatedCandidateId, out var host) ||
                        host.Kind != ProductionDirectCandidateKindV4.Host) return false;
                    localCandidates.Add(candidate.CandidateId, candidate);
                }
                candidateBatchSequence = value.BatchSequence;
                candidateTotal = value.TotalCount;
                gatheringComplete = value.Complete;
            }
            else if (value.Kind == ProductionDirectNativeEventKindV4.PairValidated)
            {
                if (value.CheckRoundId != checkRoundId || value.Pair == null ||
                    !plannedPairs.TryGetValue(value.Pair.Key, out var planned) || !planned.Equals(value.Pair) ||
                    !validatedPairs.TryAdd(value.Pair.Key, value.Pair) || value.ProbeAttempts is < 1 or > 4)
                    return false;
            }
            else if (value.Kind == ProductionDirectNativeEventKindV4.ChecksComplete)
            {
                if (value.CheckRoundId != checkRoundId || !planComplete || value.TotalCount != pairTotal ||
                    value.ValidatedPairCount != validatedPairs.Count ||
                    value.ValidatedPairCount + value.ExhaustedPairCount != value.TotalCount) return false;
            }
            else if (value.Kind is ProductionDirectNativeEventKindV4.NominationReady or
                     ProductionDirectNativeEventKindV4.ShadowReady)
            {
                if (!committed || value.CheckRoundId != checkRoundId || value.NominationId == Guid.Empty ||
                    value.Pair == null || !validatedPairs.TryGetValue(value.Pair.Key, out var pair) ||
                    !pair.Equals(value.Pair)) return false;
                if (value.Kind == ProductionDirectNativeEventKindV4.ShadowReady &&
                    (value.DirectPathBinding?.Length != 32 || IsAllZero(value.DirectPathBinding))) return false;
            }
            else if (value.Kind == ProductionDirectNativeEventKindV4.SignRequest)
            {
                if (!committed || value.RequestId == 0 || value.OpaquePayload == null ||
                    value.OpaquePayload.Length == 0 ||
                    !pendingSignatures.Add((value.Epoch, value.AttemptId, value.RequestId))) return false;
            }
            else if (value.Kind == ProductionDirectNativeEventKindV4.DirectFailed)
            {
                snapshot = RelaySnapshot(FaultForReason(value.Reason), value.Reason, true);
            }
            snapshot = new ProductionDirectNativeSnapshot
            {
                State = value.State,
                Reason = value.Reason,
                Fault = value.Kind == ProductionDirectNativeEventKindV4.DirectFailed
                    ? FaultForReason(value.Reason) : ProductionDirectNativeFault.None,
                Epoch = epoch,
                AttemptId = attemptId,
                NativeGeneration = generation,
                NativeAvailable = handle != IntPtr.Zero,
                Disposed = disposed
            };
            return true;
        }

        private bool TryDecodeEvent(TermLinkDirectEventV4 value, out ProductionDirectNativeEventV4 result)
        {
            result = null;
            if (value.AbiVersion != 4 || value.StructSize != ProductionDirectNativeAbiV4.EventSize ||
                value.Kind is < 1 or > 10 || value.State is < 1 or > 6 || value.Reason > 17 ||
                value.Epoch == 0 || value.AttemptId?.Length != 16 || value.Generation == 0 ||
                value.Payload?.Length != ProductionDirectNativeAbiV4.EventPayloadSize ||
                value.Reserved?.Length != 4 || value.Reserved.Any(item => item != 0) ||
                value.PayloadSize > ProductionDirectNativeAbiV4.EventPayloadSize ||
                value.Payload.Skip(checked((int)value.PayloadSize)).Any(item => item != 0) ||
                !TryDecodeEndpoint(value.Endpoint, true, out var endpoint)) return false;
            var kind = (ProductionDirectNativeEventKindV4)value.Kind;
            var attempt = GuidFromBytes(value.AttemptId);
            var decoded = new ProductionDirectNativeEventV4
            {
                Kind = kind,
                State = DecodeState(value.State),
                Reason = (ProductionDirectNativeReason)value.Reason,
                Epoch = value.Epoch,
                Generation = value.Generation,
                RequestId = value.RequestId,
                AttemptId = attempt,
                Endpoint = endpoint
            };
            try
            {
                if (kind is ProductionDirectNativeEventKindV4.IdentityReady or
                    ProductionDirectNativeEventKindV4.SignRequest)
                {
                    if (value.PayloadSize != ProductionDirectNativeAbiV4.OpaqueBytesSize) return false;
                    var payload = BytesToStruct<TermLinkDirectOpaqueBytesV4>(value.Payload);
                    if (payload.AbiVersion != 4 || payload.StructSize != ProductionDirectNativeAbiV4.OpaqueBytesSize ||
                        payload.Data?.Length != 2_048 || payload.DataLength > 2_048 || payload.Reserved0 != 0 ||
                        payload.Reserved?.Any(item => item != 0) != false ||
                        payload.Data.Skip(checked((int)payload.DataLength)).Any(item => item != 0)) return false;
                    decoded = Copy(decoded, opaque: payload.Data.Take(checked((int)payload.DataLength)).ToArray());
                }
                else if (kind == ProductionDirectNativeEventKindV4.CandidateBatch)
                {
                    if (value.PayloadSize != ProductionDirectNativeAbiV4.CandidateBatchSize) return false;
                    var payload = BytesToStruct<TermLinkDirectCandidateBatchV4>(value.Payload);
                    if (!PayloadOwner(payload.AbiVersion, payload.StructSize, ProductionDirectNativeAbiV4.CandidateBatchSize,
                            payload.Epoch, payload.AttemptId, value) || payload.BatchSequence == 0 ||
                        payload.CandidateCount > 8 || payload.TotalCandidateCount > 32 ||
                        payload.GatheringComplete > 1 || payload.Reserved?.Any(item => item != 0) != false)
                        return false;
                    var candidates = new List<ProductionDirectCandidateV4>();
                    for (var index = 0; index < 8; index++)
                    {
                        if (index < payload.CandidateCount)
                        {
                            if (!TryDecodeCandidate(payload.Candidates[index], out var candidate)) return false;
                            candidates.Add(candidate);
                        }
                        else if (!IsZeroStruct(payload.Candidates[index])) return false;
                    }
                    decoded = Copy(decoded, batch: payload.BatchSequence, total: payload.TotalCandidateCount,
                        complete: payload.GatheringComplete == 1, candidates: candidates.AsReadOnly());
                }
                else if (kind == ProductionDirectNativeEventKindV4.PairValidated)
                {
                    if (value.PayloadSize != ProductionDirectNativeAbiV4.PairValidatedSize) return false;
                    var payload = BytesToStruct<TermLinkDirectPairValidatedV4>(value.Payload);
                    if (!PayloadOwner(payload.AbiVersion, payload.StructSize, ProductionDirectNativeAbiV4.PairValidatedSize,
                            payload.Epoch, payload.AttemptId, value) || payload.Reserved?.Any(item => item != 0) != false ||
                        !TryDecodePair(payload.Pair, out var pair)) return false;
                    decoded = Copy(decoded, pair: pair, round: GuidFromBytes(payload.CheckRoundId),
                        rtt: payload.RoundTripTimeMilliseconds, attempts: payload.ProbeAttempts);
                }
                else if (kind == ProductionDirectNativeEventKindV4.ChecksComplete)
                {
                    if (value.PayloadSize != ProductionDirectNativeAbiV4.ChecksCompleteSize) return false;
                    var payload = BytesToStruct<TermLinkDirectChecksCompleteV4>(value.Payload);
                    if (!PayloadOwner(payload.AbiVersion, payload.StructSize, ProductionDirectNativeAbiV4.ChecksCompleteSize,
                            payload.Epoch, payload.AttemptId, value) || payload.TotalPairCount > 32 || payload.Result > 3 ||
                        payload.Reserved?.Any(item => item != 0) != false) return false;
                    decoded = Copy(decoded, round: GuidFromBytes(payload.CheckRoundId), total: payload.TotalPairCount,
                        validated: payload.ValidatedPairCount, exhausted: payload.ExhaustedPairCount);
                }
                else if (kind == ProductionDirectNativeEventKindV4.NominationReady)
                {
                    if (value.PayloadSize != ProductionDirectNativeAbiV4.NominationReadySize) return false;
                    var payload = BytesToStruct<TermLinkDirectNominationReadyV4>(value.Payload);
                    if (!PayloadOwner(payload.AbiVersion, payload.StructSize, ProductionDirectNativeAbiV4.NominationReadySize,
                            payload.Epoch, payload.AttemptId, value) || payload.Role != (uint)role || payload.Reserved0 != 0 ||
                        payload.Reserved?.Any(item => item != 0) != false || !TryDecodePair(payload.WinningPair, out var pair))
                        return false;
                    decoded = Copy(decoded, pair: pair, round: GuidFromBytes(payload.CheckRoundId),
                        nomination: GuidFromBytes(payload.NominationId));
                }
                else if (kind == ProductionDirectNativeEventKindV4.ShadowReady)
                {
                    if (value.PayloadSize != ProductionDirectNativeAbiV4.ShadowReadySize) return false;
                    var payload = BytesToStruct<TermLinkDirectShadowReadyV4>(value.Payload);
                    if (!PayloadOwner(payload.AbiVersion, payload.StructSize, ProductionDirectNativeAbiV4.ShadowReadySize,
                            payload.Epoch, payload.AttemptId, value) || payload.Reserved?.Any(item => item != 0) != false ||
                        !TryDecodePair(payload.WinningPair, out var pair)) return false;
                    decoded = Copy(decoded, pair: pair, round: GuidFromBytes(payload.CheckRoundId),
                        nomination: GuidFromBytes(payload.NominationId), binding: payload.DirectPathBinding?.ToArray());
                }
                else if (value.PayloadSize != 0) return false;
            }
            catch (Exception error) when (IsInputError(error)) { return false; }
            result = decoded;
            return true;
        }

        private ProductionDirectNativeCommandResult Control(ulong valueEpoch, Guid valueAttempt, bool networkChanged)
        {
            lock (sync)
            {
                if (!CanCallLocked()) return ProductionDirectNativeCommandResult.RelayFallback;
                if (!Current(valueEpoch, valueAttempt)) return ProductionDirectNativeCommandResult.IgnoredStale;
                var control = new TermLinkDirectControlV4
                {
                    AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.ControlSize,
                    Epoch = valueEpoch, AttemptId = GuidBytes(valueAttempt), Reserved = new ulong[2]
                };
                try
                {
                    var status = networkChanged
                        ? api.NetworkChanged(handle, ref control) : api.Cancel(handle, ref control);
                    var result = InterpretLocked(status);
                    FailLocked(networkChanged ? ProductionDirectNativeFault.NetworkChanged :
                        ProductionDirectNativeFault.Cancelled, networkChanged ?
                        ProductionDirectNativeReason.NetworkChanged : ProductionDirectNativeReason.Cancelled, true);
                    return result;
                }
                catch (Exception error) when (error is not OutOfMemoryException) { return PanicLocked(); }
            }
        }

        private void ValidateBegin(ProductionDirectBeginChecksRequestV4 value)
        {
            if (value.ConnectionId == Guid.Empty || value.LocalDeviceId == Guid.Empty ||
                value.PeerDeviceId == Guid.Empty || value.LocalDeviceId == value.PeerDeviceId ||
                value.NetworkHandle != 0 || value.NowMilliseconds < 0 ||
                value.TimeoutMilliseconds is < 500 or > 10_000 || value.Role is not
                    ProductionDirectNativeRole.Initiator and not ProductionDirectNativeRole.Responder ||
                value.Ticket == null || value.Ticket.Length is < 1 or > 2_048 || value.TicketId == Guid.Empty ||
                value.CheckRoundId == Guid.Empty || value.ConnectivityNonce?.Length != 16 ||
                IsAllZero(value.ConnectivityNonce) || value.ServerSigningPublicKey?.Length != 32 ||
                value.LocalSendProbeHmacKey?.Length != 32 || value.PeerReceiveProbeHmacKey?.Length != 32 ||
                IsAllZero(value.LocalSendProbeHmacKey) || IsAllZero(value.PeerReceiveProbeHmacKey) ||
                CryptographicOperations.FixedTimeEquals(value.LocalSendProbeHmacKey, value.PeerReceiveProbeHmacKey) ||
                value.BatchSequence != 1 || value.TotalPairCount != value.Pairs?.Count) throw new ArgumentException();
            ValidatePairBatch(value.Pairs, 1, value.PlanComplete, value.Role);
        }

        private void ValidatePairBatch(
            IReadOnlyList<ProductionDirectCandidatePairV4> pairs, uint firstRank, bool complete,
            ProductionDirectNativeRole pairRole)
        {
            if (pairs == null || pairs.Count > 8 || pairTotal + pairs.Count > 32 ||
                (pairs.Count == 0 && !complete)) throw new ArgumentException();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < pairs.Count; index++)
            {
                var pair = pairs[index];
                if (!ValidPair(pair) || pair.Rank != firstRank + index || plannedPairs.ContainsKey(pair.Key) ||
                    !keys.Add(pair.Key)) throw new ArgumentException();
                var local = pairRole == ProductionDirectNativeRole.Initiator ? pair.Initiator : pair.Responder;
                if (!localCandidates.TryGetValue(local.CandidateId, out var known) || !known.Equals(local))
                    throw new ArgumentException();
                if (pair.Initiator.Kind == ProductionDirectCandidateKindV4.Host &&
                    pair.Responder.Kind == ProductionDirectCandidateKindV4.Host &&
                    !ProductionDirectMultipairPlanner.SameConservativeLan(
                        pair.Initiator.Endpoint.Address, pair.Responder.Endpoint.Address))
                    throw new ArgumentException();
            }
        }

        private bool ValidPair(ProductionDirectCandidatePairV4 pair) => pair != null && pair.Rank >= 1 &&
            ValidCandidate(pair.Initiator) && ValidCandidate(pair.Responder) &&
            pair.Initiator.CandidateId != pair.Responder.CandidateId;

        private static bool ValidCandidate(ProductionDirectCandidateV4 value) => value != null &&
            value.CandidateId != Guid.Empty && value.Endpoint != null && value.Endpoint.Port is >= 1_024 and <= 65_535 &&
            value.Priority is >= 1 and <= int.MaxValue && value.NetworkCost <= 999 &&
            value.ExpiresAtMilliseconds >= 0 && value.Kind is ProductionDirectCandidateKindV4.Host or
                ProductionDirectCandidateKindV4.ServerReflexive &&
            (value.Kind == ProductionDirectCandidateKindV4.Host ? value.RelatedCandidateId == Guid.Empty :
                value.RelatedCandidateId != Guid.Empty) && AllowedAddress(value.Endpoint.Address);

        private TermLinkDirectGatherV4 EncodeGather(ProductionDirectGatherRequestV4 value)
        {
            if (value.Epoch == 0 || value.AttemptId == Guid.Empty || value.NetworkHandle != 0 ||
                value.LocalBind == null || value.LocalBind.Port != 0 || value.AdvertisedLocalAddress == null ||
                value.StunServers == null || value.StunServers.Count > 2 || value.NetworkCost > 999 ||
                value.CandidateExpiresAtMilliseconds <= 0 || value.GatherTimeoutMilliseconds is < 100 or > 5_000 ||
                !value.LocalBind.Address.Equals(value.AdvertisedLocalAddress) || !AllowedAddress(value.AdvertisedLocalAddress))
                throw new ArgumentException();
            var stuns = Enumerable.Range(0, 2).Select(_ => TermLinkSocketAddressV4.Unused()).ToArray();
            for (var index = 0; index < value.StunServers.Count; index++)
                stuns[index] = EncodeEndpoint(value.StunServers[index], false);
            var ip = value.AdvertisedLocalAddress.GetAddressBytes();
            var paddedIp = new byte[16];
            Array.Copy(ip, paddedIp, ip.Length);
            return new TermLinkDirectGatherV4
            {
                AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.GatherSize,
                Epoch = value.Epoch, AttemptId = GuidBytes(value.AttemptId), NetworkHandle = 0,
                LocalBind = EncodeEndpoint(value.LocalBind, true), AdvertisedLocalIp = paddedIp,
                AdvertisedLocalIpLength = checked((uint)ip.Length), NetworkCost = value.NetworkCost,
                CandidateExpiresAtMilliseconds = value.CandidateExpiresAtMilliseconds,
                StunCount = checked((uint)value.StunServers.Count),
                GatherTimeoutMilliseconds = value.GatherTimeoutMilliseconds, StunServers = stuns,
                Reserved = new ulong[4]
            };
        }

        private TermLinkDirectBeginChecksV4 EncodeBegin(ProductionDirectBeginChecksRequestV4 value) => new()
        {
            AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.BeginChecksSize,
            Epoch = value.Epoch, AttemptId = GuidBytes(value.AttemptId),
            ConnectionId = GuidBytes(value.ConnectionId), LocalDeviceId = GuidBytes(value.LocalDeviceId),
            PeerDeviceId = GuidBytes(value.PeerDeviceId), NetworkHandle = 0,
            NowMilliseconds = value.NowMilliseconds, TimeoutMilliseconds = value.TimeoutMilliseconds,
            Role = (uint)value.Role, TicketLength = checked((uint)value.Ticket.Length),
            PairBatchSequence = value.BatchSequence, PairCount = checked((uint)value.Pairs.Count),
            TotalPairCount = value.TotalPairCount, PlanComplete = value.PlanComplete ? 1u : 0u,
            Ticket = Padded(value.Ticket, 2_048), ServerSigningPublicKey = Exact(value.ServerSigningPublicKey, 32),
            TicketId = GuidBytes(value.TicketId), CheckRoundId = GuidBytes(value.CheckRoundId),
            ConnectivityNonce = Exact(value.ConnectivityNonce, 16),
            LocalSendProbeHmacKey = Exact(value.LocalSendProbeHmacKey, 32),
            PeerReceiveProbeHmacKey = Exact(value.PeerReceiveProbeHmacKey, 32),
            Pairs = EncodePairs(value.Pairs), Reserved = new ulong[4]
        };

        private TermLinkDirectAppendCheckPairsV4 EncodeAppend(ProductionDirectAppendPairsRequestV4 value) => new()
        {
            AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.AppendCheckPairsSize,
            Epoch = value.Epoch, AttemptId = GuidBytes(value.AttemptId), CheckRoundId = GuidBytes(value.CheckRoundId),
            PairBatchSequence = value.BatchSequence, PairCount = checked((uint)value.Pairs.Count),
            TotalPairCount = value.TotalPairCount, PlanComplete = value.PlanComplete ? 1u : 0u,
            Pairs = EncodePairs(value.Pairs), Reserved = new ulong[4]
        };

        private TermLinkDirectCommitNominationV4 EncodeCommit(ProductionDirectCommitRequestV4 value)
        {
            if (value.TimeoutMilliseconds is < 500 or > 10_000 || value.PeerCertificateSha256?.Length != 32 ||
                value.PeerDeviceSigningPublicKey?.Length != 32 || value.LocalProofNonce?.Length is < 16 or > 32 ||
                IsAllZero(value.PeerCertificateSha256) || IsAllZero(value.PeerDeviceSigningPublicKey) ||
                IsAllZero(value.LocalProofNonce) || value.NowMilliseconds < 0) throw new ArgumentException();
            return new TermLinkDirectCommitNominationV4
            {
                AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.CommitNominationSize,
                Epoch = value.Epoch, AttemptId = GuidBytes(value.AttemptId), CheckRoundId = GuidBytes(value.CheckRoundId),
                NominationId = GuidBytes(value.NominationId), WinningPair = EncodePair(value.WinningPair),
                Role = (uint)value.Role, TimeoutMilliseconds = value.TimeoutMilliseconds, SelectionConfirmed = 1,
                LocalProofNonceLength = checked((uint)value.LocalProofNonce.Length),
                PeerCertificateSha256 = Exact(value.PeerCertificateSha256, 32),
                PeerDeviceSigningPublicKey = Exact(value.PeerDeviceSigningPublicKey, 32),
                LocalProofNonce = Padded(value.LocalProofNonce, 32), NowMilliseconds = value.NowMilliseconds,
                Reserved = new ulong[4]
            };
        }

        private static TermLinkDirectCandidatePairV4[] EncodePairs(IReadOnlyList<ProductionDirectCandidatePairV4> values)
        {
            var result = new TermLinkDirectCandidatePairV4[8];
            for (var index = 0; index < values.Count; index++) result[index] = EncodePair(values[index]);
            return result;
        }

        private static TermLinkDirectCandidatePairV4 EncodePair(ProductionDirectCandidatePairV4 value) => new()
        {
            AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.CandidatePairSize, Rank = value.Rank,
            InitiatorCandidate = EncodeCandidate(value.Initiator), ResponderCandidate = EncodeCandidate(value.Responder),
            Reserved = new ulong[2]
        };

        private static TermLinkDirectCandidateV4 EncodeCandidate(ProductionDirectCandidateV4 value) => new()
        {
            AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.CandidateSize,
            CandidateId = GuidBytes(value.CandidateId), RelatedCandidateId = GuidBytes(value.RelatedCandidateId),
            Endpoint = EncodeEndpoint(value.Endpoint, false), Kind = (uint)value.Kind, Carrier = 1,
            Priority = value.Priority, NetworkCost = value.NetworkCost,
            ExpiresAtMilliseconds = value.ExpiresAtMilliseconds, Reserved = new ulong[2]
        };

        private static TermLinkSocketAddressV4 EncodeEndpoint(ProductionDirectNativeEndpoint value, bool zeroPort)
        {
            if (value == null || !AllowedAddress(value.Address) || (zeroPort ? value.Port != 0 : value.Port < 1_024))
                throw new ArgumentException();
            var bytes = value.Address.GetAddressBytes();
            var result = TermLinkSocketAddressV4.Empty();
            Array.Copy(bytes, result.Ip, bytes.Length);
            result.IpLength = checked((uint)bytes.Length);
            result.Port = checked((uint)value.Port);
            return result;
        }

        private static bool TryDecodeCandidate(TermLinkDirectCandidateV4 value, out ProductionDirectCandidateV4 result)
        {
            result = null;
            if (value.AbiVersion != 4 || value.StructSize != ProductionDirectNativeAbiV4.CandidateSize ||
                value.Carrier != 1 || value.Kind is < 1 or > 2 || value.Reserved?.Any(item => item != 0) != false ||
                !TryDecodeEndpoint(value.Endpoint, false, out var endpoint)) return false;
            var candidate = new ProductionDirectCandidateV4
            {
                CandidateId = GuidFromBytes(value.CandidateId),
                RelatedCandidateId = GuidFromBytes(value.RelatedCandidateId), Endpoint = endpoint,
                Kind = (ProductionDirectCandidateKindV4)value.Kind, Priority = value.Priority,
                NetworkCost = value.NetworkCost, ExpiresAtMilliseconds = value.ExpiresAtMilliseconds
            };
            if (!ValidCandidate(candidate)) return false;
            result = candidate;
            return true;
        }

        private static bool TryDecodePair(TermLinkDirectCandidatePairV4 value, out ProductionDirectCandidatePairV4 result)
        {
            result = null;
            if (value.AbiVersion != 4 || value.StructSize != ProductionDirectNativeAbiV4.CandidatePairSize ||
                value.Rank == 0 || value.Reserved0 != 0 || value.Reserved?.Any(item => item != 0) != false ||
                !TryDecodeCandidate(value.InitiatorCandidate, out var initiator) ||
                !TryDecodeCandidate(value.ResponderCandidate, out var responder)) return false;
            result = new ProductionDirectCandidatePairV4 { Rank = value.Rank, Initiator = initiator, Responder = responder };
            return initiator.CandidateId != responder.CandidateId;
        }

        private static bool TryDecodeEndpoint(
            TermLinkSocketAddressV4 value, bool allowEmpty, out ProductionDirectNativeEndpoint endpoint)
        {
            endpoint = null;
            if (value.AbiVersion == 0 && value.StructSize == 0 && value.Ip?.All(item => item == 0) == true &&
                value.IpLength == 0 && value.Port == 0 && value.Reserved?.All(item => item == 0) == true)
                return allowEmpty;
            if (value.AbiVersion != 4 || value.StructSize != ProductionDirectNativeAbiV4.SocketAddressSize ||
                value.Ip?.Length != 16 || value.IpLength is not 4 and not 16 || value.Port > 65_535 ||
                value.Reserved?.Any(item => item != 0) != false ||
                (value.IpLength == 4 && value.Ip.Skip(4).Any(item => item != 0))) return false;
            var address = new IPAddress(value.Ip.Take(checked((int)value.IpLength)).ToArray());
            if (!AllowedAddress(address)) return false;
            endpoint = new ProductionDirectNativeEndpoint(address, checked((int)value.Port));
            return true;
        }

        private static bool AllowedAddress(IPAddress value)
        {
            if (value == null || value.Equals(IPAddress.Any) || value.Equals(IPAddress.IPv6Any) ||
                value.Equals(IPAddress.Loopback) || value.Equals(IPAddress.IPv6Loopback)) return false;
            if (value.AddressFamily == AddressFamily.InterNetworkV6) return !value.IsIPv6Multicast;
            var bytes = value.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] is not (>= 224 and <= 239);
        }

        private bool CreateLocked()
        {
            var create = new TermLinkDirectCreateV4
            {
                AbiVersion = 4, StructSize = ProductionDirectNativeAbiV4.CreateSize, Enabled = 1,
                QueueCapacity = queueCapacity, Platform = 1, Reserved = new uint[3]
            };
            handle = api.Create(ref create);
            return handle != IntPtr.Zero;
        }

        private void ResetAttemptLocked()
        {
            candidateBatchSequence = 0; candidateTotal = 0; pairBatchSequence = 0;
            pairTotal = 0; checkRoundId = Guid.Empty; role = default; gatheringComplete = false;
            planComplete = false; committed = false; promotionAttempted = false;
            pendingSignatures.Clear(); localCandidates.Clear();
            plannedPairs.Clear(); validatedPairs.Clear();
        }

        private void AddPlannedPairs(IEnumerable<ProductionDirectCandidatePairV4> values)
        {
            foreach (var pair in values) plannedPairs.Add(pair.Key, pair);
        }

        private bool CanCallLocked() => enabled && !disposed && !promotionAttempted &&
            api != null && handle != IntPtr.Zero;
        private bool Current(ulong valueEpoch, Guid valueAttempt) => valueEpoch == epoch && valueAttempt == attemptId &&
            valueEpoch != 0 && valueAttempt != Guid.Empty;

        private ProductionDirectNativeCommandResult InterpretLocked(int status)
        {
            if (status == 0) return ProductionDirectNativeCommandResult.Accepted;
            if (status == -4)
            {
                FailLocked(ProductionDirectNativeFault.QueueFull, ProductionDirectNativeReason.QueueFull, true);
                return ProductionDirectNativeCommandResult.RelayFallback;
            }
            if (status == -2)
            {
                FailLocked(ProductionDirectNativeFault.InvalidRequest, ProductionDirectNativeReason.InvalidAbi, true);
                return ProductionDirectNativeCommandResult.RelayFallback;
            }
            FailLocked(status == -3 ? ProductionDirectNativeFault.NativePanic :
                ProductionDirectNativeFault.WorkerStopped, ProductionDirectNativeReason.Internal, true);
            return ProductionDirectNativeCommandResult.RelayFallback;
        }

        private ProductionDirectNativeCommandResult InvalidLocked()
        {
            FailLocked(ProductionDirectNativeFault.InvalidRequest, ProductionDirectNativeReason.InvalidAbi, true);
            return ProductionDirectNativeCommandResult.RelayFallback;
        }

        private ProductionDirectNativeCommandResult PanicLocked()
        {
            FailLocked(ProductionDirectNativeFault.NativePanic, ProductionDirectNativeReason.Internal, true);
            return ProductionDirectNativeCommandResult.RelayFallback;
        }

        private void FailLocked(ProductionDirectNativeFault fault, ProductionDirectNativeReason reason, bool retire)
        {
            if (retire) RetireHandleLocked();
            snapshot = RelaySnapshot(fault, reason, handle != IntPtr.Zero);
        }

        private ProductionDirectNativeSnapshot RelaySnapshot(
            ProductionDirectNativeFault fault, ProductionDirectNativeReason reason,
            bool available, bool isDisposed = false) => new()
        {
            State = ProductionDirectNativeState.RelayOnly, Reason = reason, Fault = fault,
            Epoch = epoch, AttemptId = attemptId, NativeGeneration = generation,
            NativeAvailable = available, Disposed = isDisposed
        };

        private void RetireHandleLocked()
        {
            if (handle == IntPtr.Zero || api == null) return;
            var old = handle; handle = IntPtr.Zero;
            try { api.Destroy(old); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            pendingSignatures.Clear();
        }

        private static bool PayloadOwner(
            uint abi, uint size, int expectedSize, ulong payloadEpoch, byte[] payloadAttempt,
            TermLinkDirectEventV4 owner) => abi == 4 && size == expectedSize &&
            payloadEpoch == owner.Epoch && payloadAttempt?.SequenceEqual(owner.AttemptId) == true;

        private static ProductionDirectNativeState DecodeState(uint value) => value switch
        {
            1 => ProductionDirectNativeState.RelayOnly,
            2 => ProductionDirectNativeState.Gathering,
            3 => ProductionDirectNativeState.Probing,
            4 => ProductionDirectNativeState.Probing,
            5 => ProductionDirectNativeState.Authenticating,
            6 => ProductionDirectNativeState.ShadowDirect,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

        private static ProductionDirectNativeEventV4 Copy(
            ProductionDirectNativeEventV4 value, byte[] opaque = null, uint batch = 0, uint total = 0,
            bool complete = false, IReadOnlyList<ProductionDirectCandidateV4> candidates = null,
            ProductionDirectCandidatePairV4 pair = null, Guid round = default, Guid nomination = default,
            uint rtt = 0, uint attempts = 0, uint validated = 0, uint exhausted = 0, byte[] binding = null) => new()
        {
            Kind = value.Kind, State = value.State, Reason = value.Reason, Epoch = value.Epoch,
            Generation = value.Generation, RequestId = value.RequestId, AttemptId = value.AttemptId,
            Endpoint = value.Endpoint, OpaquePayload = opaque, BatchSequence = batch, TotalCount = total,
            Complete = complete, Candidates = candidates ?? Array.Empty<ProductionDirectCandidateV4>(),
            Pair = pair, CheckRoundId = round, NominationId = nomination,
            RoundTripTimeMilliseconds = rtt, ProbeAttempts = attempts,
            ValidatedPairCount = validated, ExhaustedPairCount = exhausted, DirectPathBinding = binding
        };

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            if (bytes == null || bytes.Length < size) throw new ArgumentException();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(bytes, 0, pointer, size);
                return Marshal.PtrToStructure<T>(pointer);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        private static bool IsZeroStruct<T>(T value) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, pointer, false);
                var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, size);
                return bytes.All(item => item == 0);
            }
            finally { Marshal.DestroyStructure<T>(pointer); Marshal.FreeHGlobal(pointer); }
        }

        private static bool IsInputError(Exception value) => value is ArgumentException or
            InvalidOperationException or OverflowException or TypeLoadException;
        private static bool SameEndpoint(ProductionDirectNativeEndpoint left, ProductionDirectNativeEndpoint right) =>
            left != null && right != null && left.Port == right.Port && left.Address.Equals(right.Address);
        private static bool IsAllZero(ReadOnlySpan<byte> value)
        { byte aggregate = 0; foreach (var item in value) aggregate |= item; return aggregate == 0; }
        private static byte[] Exact(byte[] value, int size)
        { if (value?.Length != size) throw new ArgumentException(); return value.ToArray(); }
        private static byte[] Padded(byte[] value, int size)
        { if (value == null || value.Length > size) throw new ArgumentException(); var result = new byte[size]; value.CopyTo(result, 0); return result; }
        private static byte[] GuidBytes(Guid value) => value == Guid.Empty ? new byte[16] :
            Convert.FromHexString(value.ToString("N"));
        private static Guid GuidFromBytes(byte[] value)
        { if (value?.Length != 16) throw new ArgumentException(); return Guid.ParseExact(Convert.ToHexString(value), "N"); }
        private static void Zero(byte[] value) { if (value != null) CryptographicOperations.ZeroMemory(value); }
        private static void ZeroGather(ref TermLinkDirectGatherV4 value)
        { Zero(value.AttemptId); Zero(value.AdvertisedLocalIp); Zero(value.LocalBind.Ip); foreach (var item in value.StunServers ?? Array.Empty<TermLinkSocketAddressV4>()) Zero(item.Ip); }
        private static void ZeroBegin(ref TermLinkDirectBeginChecksV4 value)
        { Zero(value.Ticket); Zero(value.ServerSigningPublicKey); Zero(value.ConnectivityNonce); Zero(value.LocalSendProbeHmacKey); Zero(value.PeerReceiveProbeHmacKey); }
        private static void ZeroAppend(ref TermLinkDirectAppendCheckPairsV4 value) { Zero(value.AttemptId); Zero(value.CheckRoundId); }
        private static void ZeroCommit(ref TermLinkDirectCommitNominationV4 value)
        { Zero(value.PeerCertificateSha256); Zero(value.PeerDeviceSigningPublicKey); Zero(value.LocalProofNonce); }
        private static ProductionDirectNativeFault FaultForReason(ProductionDirectNativeReason reason) => reason switch
        {
            ProductionDirectNativeReason.Disabled => ProductionDirectNativeFault.Disabled,
            ProductionDirectNativeReason.InvalidAbi => ProductionDirectNativeFault.AbiMismatch,
            ProductionDirectNativeReason.QueueFull => ProductionDirectNativeFault.QueueFull,
            ProductionDirectNativeReason.Cancelled => ProductionDirectNativeFault.Cancelled,
            ProductionDirectNativeReason.NetworkChanged => ProductionDirectNativeFault.NetworkChanged,
            _ => ProductionDirectNativeFault.Internal
        };
    }
}
