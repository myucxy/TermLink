using System.Security.Cryptography;

namespace TermLink.TechnicalValidation.DirectE2E;

internal enum Carrier
{
    Relay,
    NegotiatingDirect,
    LanDirect,
    NatDirect,
}

internal enum DirectSendResult
{
    Ok,
    ProtocolFailure,
    QueueFull,
    CarrierFailure,
}

internal enum DirectFailureReason
{
    Protocol,
    Queue,
    Carrier,
    IngressConflict,
    NetworkChanged,
    Backgrounded,
}

internal enum EnvelopeKind
{
    Output,
    Input,
    Resize,
}

internal enum RouteActionKind
{
    DirectEnvelope,
    RelayEnvelope,
    Fallback,
    Carrier,
}

internal enum RecoveryActionKind
{
    ReplayRelayEnvelope,
    RetireDirect,
}

internal sealed record Envelope(
    string PeerId,
    string SessionId,
    long Sequence,
    EnvelopeKind Kind,
    byte[] ExactBytes)
{
    internal Envelope OwnedCopy() => this with { ExactBytes = ExactBytes.ToArray() };
    internal string Identity => $"{PeerId}/{SessionId}/{Sequence}";
}

internal sealed record SentEnvelope(string Path, int PathEpoch, Envelope Envelope);

internal sealed record RelaySupervisorSnapshot(
    string SupervisorId,
    bool SupervisorAlive,
    bool Connected,
    int SocketGeneration,
    int CloseCount,
    int RebuildCount);

internal sealed record RouteAction(
    string PeerId,
    int PathEpoch,
    RouteActionKind Kind,
    Envelope? Envelope,
    Carrier Carrier,
    DirectFailureReason? FailureReason);

internal sealed record RecoveryAction(
    string PeerId,
    int PredecessorPathEpoch,
    RecoveryActionKind Kind,
    Envelope? Envelope,
    DirectFailureReason? FailureReason);

/// <summary>
/// Adapter seam for future production-process drivers. The deterministic fake
/// below deliberately models transport outcomes without claiming socket E2E.
/// </summary>
internal interface IDirectProductAdapter
{
    bool RelaySupervisorAlive { get; }
    bool RelayConnected { get; }
    bool SignallingConnected { get; }
    string RelaySupervisorId { get; }
    int RelaySocketGeneration { get; }
    int RelayCloseCount { get; }
    int RelayRebuildCount { get; }
    IReadOnlyList<SentEnvelope> Sends { get; }
    DirectSendResult SendDirect(Envelope envelope, int pathEpoch);
    void SendRelay(Envelope envelope, int pathEpoch);
}

internal sealed class DeterministicTransportAdapter : IDirectProductAdapter
{
    private readonly List<SentEnvelope> sends = [];
    private readonly Queue<SentEnvelope> deferredRelay = [];
    private readonly Dictionary<string, Queue<DirectSendResult>> directResults = [];

    public bool RelaySupervisorAlive { get; private set; } = true;
    public bool RelayConnected { get; private set; } = true;
    public bool SignallingConnected { get; private set; } = true;
    public string RelaySupervisorId { get; } = "relay-supervisor-1";
    public int RelaySocketGeneration { get; private set; } = 1;
    public int RelayCloseCount { get; private set; }
    public int RelayRebuildCount { get; private set; }
    public int DeferredRelayCount => deferredRelay.Count;
    public IReadOnlyList<SentEnvelope> Sends => sends;

    public void SetDirectResults(string peerId, params DirectSendResult[] results) =>
        directResults[peerId] = new Queue<DirectSendResult>(results);

    public DirectSendResult SendDirect(Envelope envelope, int pathEpoch)
    {
        var result = directResults.TryGetValue(envelope.PeerId, out var values) && values.Count > 0
            ? values.Dequeue()
            : DirectSendResult.Ok;
        if (result == DirectSendResult.Ok)
        {
            sends.Add(new SentEnvelope("direct", pathEpoch, envelope.OwnedCopy()));
        }
        return result;
    }

    public void SendRelay(Envelope envelope, int pathEpoch)
    {
        if (!RelayConnected)
        {
            // Ownership transfers to the long-lived Relay supervisor even
            // while its physical socket is unavailable. A retiring Direct
            // peer must never be the final owner of recovery bytes.
            deferredRelay.Enqueue(new SentEnvelope("relay", pathEpoch, envelope.OwnedCopy()));
            return;
        }
        sends.Add(new SentEnvelope("relay", pathEpoch, envelope.OwnedCopy()));
    }

    public void InterruptRelay()
    {
        RelayConnected = false;
        RelayCloseCount++;
    }

    public void ReconnectRelay()
    {
        RelayConnected = true;
        RelaySocketGeneration++;
        RelayRebuildCount++;
        while (deferredRelay.TryDequeue(out var pending))
        {
            sends.Add(pending);
        }
    }

    public void InterruptSignalling() => SignallingConnected = false;

    public void ReconnectSignalling() => SignallingConnected = true;

    public RelaySupervisorSnapshot SnapshotRelay() => new(
        RelaySupervisorId,
        RelaySupervisorAlive,
        RelayConnected,
        RelaySocketGeneration,
        RelayCloseCount,
        RelayRebuildCount);

    public void AssertRelaySupervisorAlive()
    {
        Check.True(RelaySupervisorAlive, "the account Relay supervisor must remain resident");
    }

    public void AssertRelayLifecycleUnchanged(
        RelaySupervisorSnapshot before,
        string message = "Direct failure changed the existing Relay lifecycle")
    {
        var after = SnapshotRelay();
        Check.Equal(before.SupervisorId, after.SupervisorId, $"{message}: supervisor identity");
        Check.Equal(before.SupervisorAlive, after.SupervisorAlive, $"{message}: supervisor state");
        Check.Equal(before.Connected, after.Connected, $"{message}: connection state");
        Check.Equal(before.SocketGeneration, after.SocketGeneration, $"{message}: socket generation");
        Check.Equal(before.CloseCount, after.CloseCount, $"{message}: close count");
        Check.Equal(before.RebuildCount, after.RebuildCount, $"{message}: rebuild count");
    }
}

internal sealed class DirectRouteModel
{
    private sealed class PeerState
    {
        internal Carrier Carrier = Carrier.Relay;
        internal int PathEpoch = 1;
        internal int DuplicateGraceFrames;
        internal readonly Dictionary<string, Envelope> Pending = [];
        internal readonly Queue<RecoveryAction> PredecessorRecovery = new();
    }

    private readonly IDirectProductAdapter adapter;
    private readonly Dictionary<string, PeerState> peers = [];
    private readonly Dictionary<string, byte[]> applied = [];
    private readonly Dictionary<string, int> applyCounts = [];
    private readonly Dictionary<string, List<long>> appliedSequences = [];
    private readonly Dictionary<string, SortedDictionary<long, Envelope>> ingressHolds = [];
    private readonly Dictionary<string, long> nextIngressSequence = [];
    private readonly Dictionary<string, HashSet<long>> acceptedSequences = [];
    private readonly Dictionary<string, long> durableAcks = [];
    private readonly List<RouteAction> routeActions = [];

    internal DirectRouteModel(IDirectProductAdapter adapter) => this.adapter = adapter;

    internal Carrier CarrierFor(string peerId) => State(peerId).Carrier;
    internal int PathEpochFor(string peerId) => State(peerId).PathEpoch;
    internal int ApplyCount(Envelope envelope) => applyCounts.GetValueOrDefault(envelope.Identity);
    internal IReadOnlyList<long> AppliedSequences(string peerId, string sessionId) =>
        appliedSequences.TryGetValue(StreamKey(peerId, sessionId), out var values) ? values : [];
    internal long DurableAck(string sessionId) => durableAcks.GetValueOrDefault(sessionId);
    internal IReadOnlyList<RouteAction> RouteActions => routeActions;
    internal IReadOnlyList<RecoveryAction> PendingPredecessorRecovery(string peerId) =>
        State(peerId).PredecessorRecovery.ToArray();

    internal void BeginNegotiation(string peerId)
    {
        if (adapter.RelayConnected && adapter.SignallingConnected && adapter.RelaySupervisorAlive)
        {
            State(peerId).Carrier = Carrier.NegotiatingDirect;
        }
    }

    internal void Commit(string peerId, Carrier carrier, int duplicateGraceFrames = 0)
    {
        Check.True(carrier is Carrier.LanDirect or Carrier.NatDirect, "only a Direct carrier can commit");
        var state = State(peerId);
        Check.Equal(Carrier.NegotiatingDirect, state.Carrier, "commit requires negotiation");
        state.PathEpoch++;
        state.Carrier = carrier;
        state.DuplicateGraceFrames = duplicateGraceFrames;
        routeActions.Add(new RouteAction(
            peerId,
            state.PathEpoch,
            RouteActionKind.Carrier,
            null,
            carrier,
            null));
    }

    internal void FailNegotiation(string peerId) => State(peerId).Carrier = Carrier.Relay;

    internal void Route(Envelope envelope)
    {
        var owned = envelope.OwnedCopy();
        var state = State(envelope.PeerId);
        var direct = state.Carrier is Carrier.LanDirect or Carrier.NatDirect;
        if (!EligibleForDirect(envelope.Kind) || !direct)
        {
            adapter.SendRelay(owned, state.PathEpoch);
            routeActions.Add(new RouteAction(
                envelope.PeerId,
                state.PathEpoch,
                RouteActionKind.RelayEnvelope,
                owned,
                Carrier.Relay,
                null));
            return;
        }

        // Retain before attempting Direct. A non-OK result therefore includes
        // the current Envelope in the exact Relay fallback set.
        state.Pending[owned.Identity] = owned;
        if (state.DuplicateGraceFrames > 0)
        {
            adapter.SendRelay(owned, state.PathEpoch);
            routeActions.Add(new RouteAction(
                envelope.PeerId,
                state.PathEpoch,
                RouteActionKind.RelayEnvelope,
                owned,
                Carrier.Relay,
                null));
            state.DuplicateGraceFrames--;
        }

        var result = adapter.SendDirect(owned, state.PathEpoch);
        if (result == DirectSendResult.Ok)
        {
            routeActions.Add(new RouteAction(
                envelope.PeerId,
                state.PathEpoch,
                RouteActionKind.DirectEnvelope,
                owned,
                state.Carrier,
                null));
            return;
        }

        Fallback(envelope.PeerId, FailureReason(result));
    }

    internal void ReceiveTransportReceipt(Envelope envelope)
    {
        // A receipt is transport-only. The semantic item and durable ACK stay
        // untouched until the application protocol accepts/applies it.
        Check.True(State(envelope.PeerId).Pending.ContainsKey(envelope.Identity),
            "a receipt must refer to a retained Direct Envelope");
    }

    internal void ApplicationAccepted(Envelope envelope)
    {
        State(envelope.PeerId).Pending.Remove(envelope.Identity);
        if (!acceptedSequences.TryGetValue(envelope.SessionId, out var accepted))
        {
            accepted = [];
            acceptedSequences.Add(envelope.SessionId, accepted);
        }
        accepted.Add(envelope.Sequence);
        var contiguous = DurableAck(envelope.SessionId);
        while (contiguous < long.MaxValue && accepted.Remove(contiguous + 1))
        {
            contiguous++;
        }
        durableAcks[envelope.SessionId] = contiguous;
    }

    /// <summary>
    /// Feed both carriers through one serialized ingress. Out-of-order data is
    /// held until its contiguous predecessor arrives; an exact duplicate is
    /// ignored, while a conflicting identity retires only Direct.
    /// </summary>
    internal bool Ingress(Envelope envelope)
    {
        var identity = envelope.Identity;
        if (applied.TryGetValue(identity, out var prior))
        {
            if (CryptographicOperations.FixedTimeEquals(prior, envelope.ExactBytes))
            {
                return false;
            }
            Fallback(envelope.PeerId, DirectFailureReason.IngressConflict);
            return false;
        }

        var streamKey = StreamKey(envelope.PeerId, envelope.SessionId);
        var next = nextIngressSequence.GetValueOrDefault(streamKey, 1);
        if (envelope.Sequence < next)
        {
            // A previously unseen old sequence cannot be inserted into the
            // serialized stream after its cursor has advanced.
            Fallback(envelope.PeerId, DirectFailureReason.IngressConflict);
            return false;
        }

        var holds = Holds(streamKey);
        if (envelope.Sequence > next)
        {
            if (holds.TryGetValue(envelope.Sequence, out var held))
            {
                if (!CryptographicOperations.FixedTimeEquals(held.ExactBytes, envelope.ExactBytes))
                {
                    Fallback(envelope.PeerId, DirectFailureReason.IngressConflict);
                }
                return false;
            }
            holds.Add(envelope.Sequence, envelope.OwnedCopy());
            return false;
        }

        ApplyIngress(envelope);
        while (holds.Remove(next + 1, out var contiguous))
        {
            ApplyIngress(contiguous);
            next++;
        }
        nextIngressSequence[streamKey] = next + 1;
        return true;
    }

    internal IReadOnlyList<RecoveryAction> DrainPredecessorRecovery(string peerId)
    {
        var state = State(peerId);
        var drained = state.PredecessorRecovery.ToArray();
        state.PredecessorRecovery.Clear();
        return drained;
    }

    internal void DirectInterrupted(string peerId) => Fallback(peerId, DirectFailureReason.Carrier);
    internal void NetworkChanged(string peerId) => Fallback(peerId, DirectFailureReason.NetworkChanged);
    internal void EnterBackground(string peerId) => Fallback(peerId, DirectFailureReason.Backgrounded);

    private void Fallback(string peerId, DirectFailureReason reason)
    {
        var state = State(peerId);
        if (state.Carrier is not (Carrier.LanDirect or Carrier.NatDirect))
        {
            return;
        }

        var predecessorEpoch = state.PathEpoch;
        foreach (var envelope in state.Pending.Values.OrderBy(value => value.Sequence))
        {
            adapter.SendRelay(envelope, predecessorEpoch);
            routeActions.Add(new RouteAction(
                peerId,
                predecessorEpoch,
                RouteActionKind.RelayEnvelope,
                envelope,
                Carrier.Relay,
                reason));
            state.PredecessorRecovery.Enqueue(new RecoveryAction(
                peerId,
                predecessorEpoch,
                RecoveryActionKind.ReplayRelayEnvelope,
                envelope,
                reason));
        }
        state.Pending.Clear();
        state.PredecessorRecovery.Enqueue(new RecoveryAction(
            peerId,
            predecessorEpoch,
            RecoveryActionKind.RetireDirect,
            null,
            reason));
        routeActions.Add(new RouteAction(
            peerId,
            predecessorEpoch,
            RouteActionKind.Fallback,
            null,
            Carrier.Relay,
            reason));
        state.Carrier = Carrier.Relay;
        state.DuplicateGraceFrames = 0;
    }

    private void ApplyIngress(Envelope envelope)
    {
        var identity = envelope.Identity;
        applied[identity] = envelope.ExactBytes.ToArray();
        applyCounts[identity] = applyCounts.GetValueOrDefault(identity) + 1;
        var streamKey = StreamKey(envelope.PeerId, envelope.SessionId);
        if (!appliedSequences.TryGetValue(streamKey, out var sequenceList))
        {
            sequenceList = [];
            appliedSequences.Add(streamKey, sequenceList);
        }
        sequenceList.Add(envelope.Sequence);
    }

    private SortedDictionary<long, Envelope> Holds(string streamKey)
    {
        if (!ingressHolds.TryGetValue(streamKey, out var holds))
        {
            holds = [];
            ingressHolds.Add(streamKey, holds);
        }
        return holds;
    }

    private static string StreamKey(string peerId, string sessionId) => $"{peerId}/{sessionId}";

    private PeerState State(string peerId)
    {
        if (!peers.TryGetValue(peerId, out var state))
        {
            state = new PeerState();
            peers.Add(peerId, state);
        }
        return state;
    }

    private static bool EligibleForDirect(EnvelopeKind kind) =>
        kind is EnvelopeKind.Output or EnvelopeKind.Input;

    private static DirectFailureReason FailureReason(DirectSendResult result) => result switch
    {
        DirectSendResult.ProtocolFailure => DirectFailureReason.Protocol,
        DirectSendResult.QueueFull => DirectFailureReason.Queue,
        DirectSendResult.CarrierFailure => DirectFailureReason.Carrier,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "not a Direct failure"),
    };
}

internal sealed record MatrixCase(string Name, Action Test);

internal static class Program
{
    private const string AndroidA = "android-a";
    private const string AndroidB = "android-b";
    private const string Session = "session-1";

    public static int Main()
    {
        MatrixCase[] cases =
        [
            new("relay-lan-direct-relay-lossless-ordered", RelayLanDirectRelay),
            new("relay-nat-direct-relay-lossless-ordered", RelayNatDirectRelay),
            new("direct-protocol-failure-preserves-relay", DirectProtocolFailure),
            new("direct-queue-failure-includes-current-envelope", DirectQueueFailure),
            new("direct-carrier-failure-preserves-relay", DirectCarrierFailure),
            new("predecessor-recovery-drains-before-successor", PredecessorRecovery),
            new("symmetric-nat-falls-back-to-relay", SymmetricNatFallback),
            new("relay-interruption-keeps-supervisor-resident", RelayInterruption),
            new("direct-failure-while-relay-offline-defers-exact-recovery", OfflineRelayRecovery),
            new("signalling-interruption-is-isolated", SignallingInterruption),
            new("network-switch-retires-only-direct", NetworkSwitch),
            new("screen-lock-and-resume", ScreenLockRecovery),
            new("two-android-peer-failure-isolation", TwoAndroidIsolation),
            new("bad-frame-conflict-fails-closed", BadFrame),
            new("transport-receipt-does-not-advance-ack", ReceiptAuthority),
            new("long-output-and-resize-burst", LongOutputAndResizeBurst),
        ];

        var passed = 0;
        foreach (var matrixCase in cases)
        {
            try
            {
                matrixCase.Test();
                Console.WriteLine($"PASS {matrixCase.Name}");
                passed++;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL {matrixCase.Name}: {error.Message}");
            }
        }

        Console.WriteLine($"MODEL COVERAGE: {passed}/{cases.Length} deterministic fault cases passed.");
        Console.WriteLine("REAL E2E COVERAGE: 0 production sockets/devices; production adapters remain to be connected.");
        return passed == cases.Length ? 0 : 1;
    }

    private static void RelayLanDirectRelay()
    {
        var (adapter, route) = Fixture();
        var relayBefore = adapter.SnapshotRelay();
        var first = Output(AndroidA, 1, "relay-prefix");
        route.Route(first);
        Activate(route, AndroidA, Carrier.LanDirect, duplicateGraceFrames: 2);

        var second = Output(AndroidA, 2, "lan-direct-2");
        var third = Output(AndroidA, 3, "lan-direct-3");
        var fourth = Output(AndroidA, 4, "lan-direct-4");
        route.Route(second);
        route.Route(third);
        route.ApplicationAccepted(second);
        route.DirectInterrupted(AndroidA);
        route.Route(fourth);

        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "LAN Direct failure must return to Relay");
        CheckExactFallback(adapter, third);
        CheckPath(adapter, fourth, "relay");
        DeliverCopiesInRaceOrder(route, adapter, third, second, first);
        route.Ingress(fourth);
        AssertOrderedExactlyOnce(route, 1, 2, 3, 4);
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void RelayNatDirectRelay()
    {
        var (adapter, route) = Fixture();
        var relayBefore = adapter.SnapshotRelay();
        var first = Output(AndroidA, 1, "relay-prefix-nat");
        route.Route(first);
        Activate(route, AndroidA, Carrier.NatDirect, duplicateGraceFrames: 1);

        var second = Output(AndroidA, 2, "nat-direct-2");
        var third = Output(AndroidA, 3, "nat-direct-3");
        var fourth = Output(AndroidA, 4, "nat-direct-4");
        route.Route(second);
        route.Route(third);
        route.ApplicationAccepted(second);
        route.DirectInterrupted(AndroidA);
        route.Route(fourth);

        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "NAT Direct failure must return to Relay");
        CheckExactFallback(adapter, third);
        CheckPath(adapter, fourth, "relay");
        DeliverCopiesInRaceOrder(route, adapter, third, second, first);
        route.Ingress(fourth);
        AssertOrderedExactlyOnce(route, 1, 2, 3, 4);
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void DirectProtocolFailure()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        var relayBefore = adapter.SnapshotRelay();
        adapter.SetDirectResults(AndroidA, DirectSendResult.ProtocolFailure);
        var current = Output(AndroidA, 1, "protocol-failure-current");
        route.Route(current);
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "protocol failure must retire Direct");
        CheckExactFallback(adapter, current);
        Check.Equal(0, adapter.Sends.Count(value => value.Path == "direct"),
            "protocol failure must not report an accepted Direct send");
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void DirectQueueFailure()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.NatDirect);
        var relayBefore = adapter.SnapshotRelay();
        adapter.SetDirectResults(AndroidA, DirectSendResult.Ok, DirectSendResult.QueueFull);
        var retained = Output(AndroidA, 1, "retained-before-queue-full");
        var current = Output(AndroidA, 2, "current-queue-full");
        route.Route(retained);
        route.Route(current);
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "queue saturation must return to Relay");
        CheckExactFallback(adapter, retained);
        CheckExactFallback(adapter, current);
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void DirectCarrierFailure()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.NatDirect);
        var relayBefore = adapter.SnapshotRelay();
        adapter.SetDirectResults(AndroidA, DirectSendResult.CarrierFailure);
        var current = Input(AndroidA, 1, "carrier-failure-current");
        route.Route(current);
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "carrier failure must return to Relay");
        CheckExactFallback(adapter, current);
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void PredecessorRecovery()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        var predecessorEpoch = route.PathEpochFor(AndroidA);
        var pending = Output(AndroidA, 1, "predecessor-recovery-bytes");
        adapter.SetDirectResults(AndroidA, DirectSendResult.Ok);
        route.Route(pending);
        route.DirectInterrupted(AndroidA);

        var queued = route.PendingPredecessorRecovery(AndroidA);
        Check.Equal(2, queued.Count, "Direct fallback must enqueue replay and retire actions");
        Check.Equal(RecoveryActionKind.ReplayRelayEnvelope, queued[0].Kind,
            "predecessor recovery must replay data before retiring Direct");
        Check.Equal(predecessorEpoch, queued[0].PredecessorPathEpoch,
            "replay must retain the predecessor path fence");
        Check.True(queued[0].Envelope!.ExactBytes.AsSpan().SequenceEqual(pending.ExactBytes),
            "predecessor recovery must retain exact Envelope bytes");
        Check.Equal(RecoveryActionKind.RetireDirect, queued[1].Kind,
            "predecessor recovery must end with a Direct retirement action");

        var drained = route.DrainPredecessorRecovery(AndroidA);
        Check.Equal(2, drained.Count, "successor must be able to drain predecessor actions");
        Activate(route, AndroidA, Carrier.NatDirect);
        var successorCarrier = route.RouteActions.Last(value => value.Kind == RouteActionKind.Carrier);
        Check.True(successorCarrier.PathEpoch > predecessorEpoch,
            "successor carrier must use a newer path fence");
        Check.Equal(Carrier.NatDirect, successorCarrier.Carrier,
            "successor carrier action must identify NAT Direct");
        Check.Equal(Carrier.NatDirect, route.CarrierFor(AndroidA), "successor must become authoritative");
        adapter.AssertRelaySupervisorAlive();
    }

    private static void SymmetricNatFallback()
    {
        var (adapter, route) = Fixture();
        route.BeginNegotiation(AndroidA);
        route.FailNegotiation(AndroidA);
        var envelope = Output(AndroidA, 1, "symmetric-nat");
        route.Route(envelope);
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "failed hole punching must remain Relay");
        CheckPath(adapter, envelope, "relay");
        adapter.AssertRelaySupervisorAlive();
    }

    private static void RelayInterruption()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        adapter.InterruptRelay();
        Check.Equal(Carrier.LanDirect, route.CarrierFor(AndroidA), "Relay socket loss must not kill healthy Direct");
        adapter.AssertRelaySupervisorAlive();
        adapter.ReconnectRelay();
        var resize = Resize(AndroidA, 1, "resize-after-relay-reconnect");
        route.Route(resize);
        CheckPath(adapter, resize, "relay");
        Check.Equal(2, adapter.RelaySocketGeneration, "an explicit Relay interruption must rebuild its own socket");
    }

    private static void OfflineRelayRecovery()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        var retained = Output(AndroidA, 1, "retained-before-offline-relay");
        route.Route(retained);

        adapter.InterruptRelay();
        var disconnectedRelay = adapter.SnapshotRelay();
        route.DirectInterrupted(AndroidA);
        var current = Output(AndroidA, 2, "current-while-relay-offline");
        route.Route(current);

        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA),
            "Direct failure while Relay is offline must still retire only Direct");
        Check.Equal(2, adapter.DeferredRelayCount,
            "exact recovery must transfer to the resident Relay owner while its socket is offline");
        adapter.AssertRelayLifecycleUnchanged(disconnectedRelay,
            "Direct fallback must not reconnect an offline Relay socket");

        adapter.ReconnectRelay();
        Check.Equal(0, adapter.DeferredRelayCount,
            "Relay reconnect must drain its owned recovery queue");
        CheckExactFallback(adapter, retained);
        CheckExactFallback(adapter, current);
        Check.Equal(1, adapter.Sends.Count(value =>
                value.Path == "relay" && value.Envelope.Identity == retained.Identity),
            "retained recovery must be handed to Relay exactly once");
        Check.Equal(1, adapter.Sends.Count(value =>
                value.Path == "relay" && value.Envelope.Identity == current.Identity),
            "current recovery must be handed to Relay exactly once");
        adapter.AssertRelaySupervisorAlive();
    }

    private static void SignallingInterruption()
    {
        var (adapter, route) = Fixture();
        adapter.InterruptSignalling();
        route.BeginNegotiation(AndroidA);
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "no signalling means no Direct attempt");
        adapter.ReconnectSignalling();
        Activate(route, AndroidA, Carrier.NatDirect);
        var relayBefore = adapter.SnapshotRelay();
        adapter.InterruptSignalling();
        Check.Equal(Carrier.NatDirect, route.CarrierFor(AndroidA), "signalling loss must not tear committed Direct");
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void NetworkSwitch()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.NatDirect);
        var relayBefore = adapter.SnapshotRelay();
        var envelope = Output(AndroidA, 1, "before-network-change");
        route.Route(envelope);
        route.NetworkChanged(AndroidA);
        CheckExactFallback(adapter, envelope);
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "network change retires stale Direct path");
        Activate(route, AndroidA, Carrier.LanDirect);
        Check.Equal(Carrier.LanDirect, route.CarrierFor(AndroidA), "new network uses a new negotiation");
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void ScreenLockRecovery()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        var relayBefore = adapter.SnapshotRelay();
        var envelope = Output(AndroidA, 1, "before-lock");
        route.Route(envelope);
        route.EnterBackground(AndroidA);
        CheckExactFallback(adapter, envelope);
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "background transition retires Direct");
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
        Activate(route, AndroidA, Carrier.NatDirect);
        Check.Equal(Carrier.NatDirect, route.CarrierFor(AndroidA), "resume can renegotiate after Relay is ready");
    }

    private static void TwoAndroidIsolation()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        Activate(route, AndroidB, Carrier.NatDirect);
        var relayBefore = adapter.SnapshotRelay();
        route.DirectInterrupted(AndroidA);
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "failed peer must fall back");
        Check.Equal(Carrier.NatDirect, route.CarrierFor(AndroidB), "other Android Direct path must survive");
        var survivor = Output(AndroidB, 1, "peer-b");
        route.Route(survivor);
        CheckPath(adapter, survivor, "direct");
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void BadFrame()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        var relayBefore = adapter.SnapshotRelay();
        var valid = Output(AndroidA, 1, "trusted");
        var conflicting = Output(AndroidA, 1, "tampered");
        Check.True(route.Ingress(valid), "first valid frame must apply");
        Check.False(route.Ingress(conflicting), "same identity with different bytes must fail closed");
        Check.Equal(1, route.ApplyCount(valid), "bad frame must never execute");
        Check.Equal(Carrier.Relay, route.CarrierFor(AndroidA), "bad Direct frame retires only its path");
        adapter.AssertRelayLifecycleUnchanged(relayBefore);
        adapter.AssertRelaySupervisorAlive();
    }

    private static void ReceiptAuthority()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        var envelope = Output(AndroidA, 1, "receipt-does-not-ack");
        route.Route(envelope);
        route.ReceiveTransportReceipt(envelope);
        Check.Equal(0L, route.DurableAck(Session),
            "transport receipt must not advance the contiguous durable application ACK");
        route.ApplicationAccepted(envelope);
        Check.Equal(1L, route.DurableAck(Session),
            "application acceptance must advance the durable ACK");
        adapter.AssertRelaySupervisorAlive();
    }

    private static void LongOutputAndResizeBurst()
    {
        var (adapter, route) = Fixture();
        Activate(route, AndroidA, Carrier.LanDirect);
        const int outputCount = 1_024;
        var outputs = new List<Envelope>(outputCount);
        for (var sequence = 1; sequence <= outputCount; sequence++)
        {
            var output = Output(AndroidA, sequence, $"output-{sequence:D4}-{new string('x', 2_048)}");
            outputs.Add(output);
            route.Route(output);
            route.ApplicationAccepted(output);

            // Resize is a Relay-only control operation. It deliberately uses a
            // separate sequence space so a burst cannot create an output gap.
            var resize = Resize(AndroidA, 100_000 + sequence, $"resize-{sequence:D4}");
            route.Route(resize);
        }

        Check.Equal(outputCount, adapter.Sends.Count(value => value.Path == "direct"),
            "long output must remain on Direct");
        Check.Equal(outputCount, adapter.Sends.Count(value =>
            value.Path == "relay" && value.Envelope.Kind == EnvelopeKind.Resize),
            "every resize in the burst must use Relay");

        // Deliver a reverse-order Direct arrival to exercise the serialized
        // ingress hold, then replay one exact Relay duplicate per burst edge.
        foreach (var output in outputs.AsEnumerable().Reverse())
        {
            route.Ingress(output);
        }
        foreach (var output in outputs.Where((_, index) => index % 128 == 0))
        {
            route.Ingress(output);
        }
        AssertOrderedExactlyOnce(route, Enumerable.Range(1, outputCount).Select(value => (long)value).ToArray());
        adapter.AssertRelaySupervisorAlive();
    }

    private static (DeterministicTransportAdapter Adapter, DirectRouteModel Route) Fixture()
    {
        var adapter = new DeterministicTransportAdapter();
        return (adapter, new DirectRouteModel(adapter));
    }

    private static void Activate(
        DirectRouteModel route,
        string peerId,
        Carrier carrier,
        int duplicateGraceFrames = 0)
    {
        route.BeginNegotiation(peerId);
        route.Commit(peerId, carrier, duplicateGraceFrames);
    }

    private static Envelope Output(string peerId, long sequence, string text) =>
        Make(peerId, sequence, EnvelopeKind.Output, text);

    private static Envelope Input(string peerId, long sequence, string text) =>
        Make(peerId, sequence, EnvelopeKind.Input, text);

    private static Envelope Resize(string peerId, long sequence, string text) =>
        Make(peerId, sequence, EnvelopeKind.Resize, text);

    private static Envelope Make(string peerId, long sequence, EnvelopeKind kind, string text) =>
        new(peerId, Session, sequence, kind, System.Text.Encoding.UTF8.GetBytes(text));

    private static void CheckPath(DeterministicTransportAdapter adapter, Envelope envelope, string path)
    {
        var sent = adapter.Sends.LastOrDefault(value => value.Envelope.Identity == envelope.Identity);
        Check.NotNull(sent, "Envelope must have a recorded transport send");
        Check.Equal(path, sent!.Path, "Envelope used the wrong carrier");
        Check.True(sent.Envelope.ExactBytes.AsSpan().SequenceEqual(envelope.ExactBytes),
            "transport must preserve exact Envelope bytes");
    }

    private static void CheckExactFallback(DeterministicTransportAdapter adapter, Envelope envelope)
    {
        var fallback = adapter.Sends.LastOrDefault(value =>
            value.Path == "relay" && value.Envelope.Identity == envelope.Identity);
        Check.NotNull(fallback, "pending/current Envelope must be exported to Relay");
        Check.True(fallback!.Envelope.ExactBytes.AsSpan().SequenceEqual(envelope.ExactBytes),
            "Relay fallback must use byte-identical Envelope data");
    }

    private static void DeliverCopiesInRaceOrder(
        DirectRouteModel route,
        DeterministicTransportAdapter adapter,
        Envelope fallbackEnvelope,
        Envelope directEnvelope,
        Envelope relayPrefix)
    {
        var copies = new[]
        {
            FindSend(adapter, "direct", directEnvelope),
            FindSend(adapter, "direct", fallbackEnvelope),
            FindSend(adapter, "relay", fallbackEnvelope),
            FindSend(adapter, "relay", relayPrefix),
            FindSend(adapter, "relay", directEnvelope),
        };
        foreach (var copy in copies)
        {
            route.Ingress(copy.Envelope);
        }
    }

    private static SentEnvelope FindSend(
        DeterministicTransportAdapter adapter,
        string path,
        Envelope envelope) =>
        adapter.Sends.FirstOrDefault(value =>
            value.Path == path && value.Envelope.Identity == envelope.Identity)
        ?? throw new InvalidOperationException($"missing {path} copy for {envelope.Identity}");

    private static void AssertOrderedExactlyOnce(DirectRouteModel route, params long[] expected)
    {
        var actual = route.AppliedSequences(AndroidA, Session);
        Check.True(actual.SequenceEqual(expected),
            $"application order/loss mismatch; expected=[{string.Join(',', expected)}], actual=[{string.Join(',', actual)}]");
        foreach (var sequence in expected)
        {
            // The test model's identity includes peer/session/sequence. Payload
            // equality is checked at every transport boundary; the ordered list
            // and per-identity count prove no duplicate was applied.
            var identity = $"{AndroidA}/{Session}/{sequence}";
            var count = route.AppliedSequences(AndroidA, Session).Count(value => value == sequence);
            Check.Equal(1, count, $"{identity} must be applied exactly once");
        }
    }
}

internal static class Check
{
    internal static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void False(bool condition, string message) => True(!condition, message);

    internal static void Equal<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}; expected={expected}, actual={actual}");
        }
    }

    internal static void NotNull(object? value, string message) => True(value is not null, message);
}
