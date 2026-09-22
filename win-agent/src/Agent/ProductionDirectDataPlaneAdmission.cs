using System;

namespace TermLink.Agent
{
    /// <summary>
    /// Immutable authorization for one Direct data-plane generation. The
    /// Relay and independent signalling sockets have unrelated generation
    /// counters, so both are captured together and fenced by the Relay
    /// generation to which the signalling handshake was bound.
    /// </summary>
    internal readonly struct ProductionDirectDataPlaneToken : IEquatable<ProductionDirectDataPlaneToken>
    {
        internal ProductionDirectDataPlaneToken(
            long relayGeneration,
            long signallingGeneration,
            long admissionGeneration)
        {
            RelayGeneration = relayGeneration;
            SignallingGeneration = signallingGeneration;
            AdmissionGeneration = admissionGeneration;
        }

        internal long RelayGeneration { get; }
        internal long SignallingGeneration { get; }
        internal long AdmissionGeneration { get; }
        internal bool IsValid => RelayGeneration > 0 && SignallingGeneration > 0 &&
            AdmissionGeneration > 0;

        public bool Equals(ProductionDirectDataPlaneToken other) =>
            RelayGeneration == other.RelayGeneration &&
            SignallingGeneration == other.SignallingGeneration &&
            AdmissionGeneration == other.AdmissionGeneration;

        public override bool Equals(object obj) =>
            obj is ProductionDirectDataPlaneToken other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(RelayGeneration, SignallingGeneration, AdmissionGeneration);
    }

    internal sealed class ProductionDirectDataPlaneAdmissionSnapshot
    {
        internal bool Permitted { get; init; }
        internal bool BuildEnabled { get; init; }
        internal bool ServerRoutingPermitted { get; init; }
        internal bool RelayConnected { get; init; }
        internal bool RelayDirectDataNegotiated { get; init; }
        internal bool RelaySessionCutoverNegotiated { get; init; }
        internal long RelayGeneration { get; init; }
        internal bool SignallingConnected { get; init; }
        internal bool SignallingDirectDataNegotiated { get; init; }
        internal bool SignallingSessionCutoverNegotiated { get; init; }
        internal long SignallingGeneration { get; init; }
        internal long SignallingRelayGeneration { get; init; }
        internal long AdmissionGeneration { get; init; }
    }

    /// <summary>
    /// Fail-closed composite gate for production Direct cutover. Configuration,
    /// Relay capability negotiation and signalling capability negotiation are
    /// independent authorities. None is sufficient by itself, and a token from
    /// an earlier socket/configuration generation can never become valid again.
    /// </summary>
    internal sealed class ProductionDirectDataPlaneAdmission
    {
        private readonly object sync = new();
        private readonly bool buildEnabled;
        private bool serverRoutingPermitted;
        private bool relayConnected;
        private bool relayDirectDataNegotiated;
        private bool relaySessionCutoverNegotiated;
        private long relayGeneration;
        private bool signallingConnected;
        private bool signallingDirectDataNegotiated;
        private bool signallingSessionCutoverNegotiated;
        private long signallingGeneration;
        private long signallingRelayGeneration;
        private long admissionGeneration = 1;

        internal ProductionDirectDataPlaneAdmission(bool buildEnabled)
        {
            this.buildEnabled = buildEnabled;
        }

        internal ProductionDirectDataPlaneAdmissionSnapshot Snapshot
        {
            get
            {
                lock (sync) return SnapshotLocked();
            }
        }

        internal void ObserveServerConfig(ProductionDirectTransportServerConfig config)
        {
            var permitted = buildEnabled &&
                ProductionDirectTransportSignalingClient.HasDirectDataCapabilities(config);
            ObserveServerRoutingPermission(permitted);
        }

        internal void ObserveServerConfigUnavailable()
        {
            ObserveServerRoutingPermission(false);
        }

        private void ObserveServerRoutingPermission(bool permitted)
        {
            lock (sync)
            {
                if (serverRoutingPermitted == permitted) return;
                serverRoutingPermitted = permitted;
                InvalidateLocked();
            }
        }

        internal void ObserveRelay(ProductionRealtimeCapabilitySnapshot value)
        {
            var connected = value?.Connected == true;
            var generation = connected ? value.PhysicalGeneration : 0;
            var direct = connected && value.DirectTransportDataV1;
            var sessionCutover = connected && value.DirectSessionCutoverV1;
            lock (sync)
            {
                if (relayConnected == connected && relayGeneration == generation &&
                    relayDirectDataNegotiated == direct &&
                    relaySessionCutoverNegotiated == sessionCutover) return;
                relayConnected = connected;
                relayGeneration = generation;
                relayDirectDataNegotiated = direct;
                relaySessionCutoverNegotiated = sessionCutover;

                // A signalling Welcome is authorized only for the exact Relay
                // generation that was current when its owner observed it.
                if (!connected || signallingRelayGeneration != generation)
                {
                    signallingConnected = false;
                    signallingDirectDataNegotiated = false;
                    signallingSessionCutoverNegotiated = false;
                    signallingGeneration = 0;
                    signallingRelayGeneration = 0;
                }
                InvalidateLocked();
            }
        }

        internal void ObserveSignalling(
            ProductionDirectSignalingSnapshot value,
            long boundRelayGeneration)
        {
            var connected = value?.State == ProductionDirectSignalingLaneState.Connected;
            var generation = connected ? value.Generation : 0;
            var direct = connected && value.DirectDataV1Negotiated;
            var sessionCutover = connected && value.DirectSessionCutoverV1Negotiated;
            lock (sync)
            {
                // Never bind a newly connected signalling lane to a stale or
                // absent Relay generation supplied by an asynchronous callback.
                if (!connected || boundRelayGeneration <= 0 || !relayConnected ||
                    boundRelayGeneration != relayGeneration)
                {
                    connected = false;
                    direct = false;
                    sessionCutover = false;
                    generation = 0;
                    boundRelayGeneration = 0;
                }
                if (signallingConnected == connected &&
                    signallingGeneration == generation &&
                    signallingDirectDataNegotiated == direct &&
                    signallingSessionCutoverNegotiated == sessionCutover &&
                    signallingRelayGeneration == boundRelayGeneration) return;
                signallingConnected = connected;
                signallingGeneration = generation;
                signallingDirectDataNegotiated = direct;
                signallingSessionCutoverNegotiated = sessionCutover;
                signallingRelayGeneration = boundRelayGeneration;
                InvalidateLocked();
            }
        }

        internal bool TryCapture(out ProductionDirectDataPlaneToken token)
        {
            lock (sync)
            {
                if (!PermittedLocked())
                {
                    token = default;
                    return false;
                }
                token = new ProductionDirectDataPlaneToken(
                    relayGeneration,
                    signallingGeneration,
                    admissionGeneration);
                return true;
            }
        }

        internal bool IsCurrent(ProductionDirectDataPlaneToken token)
        {
            lock (sync)
            {
                return token.IsValid && PermittedLocked() &&
                    token.RelayGeneration == relayGeneration &&
                    token.SignallingGeneration == signallingGeneration &&
                    token.AdmissionGeneration == admissionGeneration;
            }
        }

        internal void Invalidate()
        {
            lock (sync) InvalidateLocked();
        }

        private bool PermittedLocked() => buildEnabled && serverRoutingPermitted &&
            relayConnected && relayDirectDataNegotiated &&
            relaySessionCutoverNegotiated && relayGeneration > 0 &&
            signallingConnected && signallingDirectDataNegotiated &&
            signallingSessionCutoverNegotiated &&
            signallingGeneration > 0 && signallingRelayGeneration == relayGeneration;

        private ProductionDirectDataPlaneAdmissionSnapshot SnapshotLocked() => new()
        {
            Permitted = PermittedLocked(),
            BuildEnabled = buildEnabled,
            ServerRoutingPermitted = serverRoutingPermitted,
            RelayConnected = relayConnected,
            RelayDirectDataNegotiated = relayDirectDataNegotiated,
            RelaySessionCutoverNegotiated = relaySessionCutoverNegotiated,
            RelayGeneration = relayGeneration,
            SignallingConnected = signallingConnected,
            SignallingDirectDataNegotiated = signallingDirectDataNegotiated,
            SignallingSessionCutoverNegotiated = signallingSessionCutoverNegotiated,
            SignallingGeneration = signallingGeneration,
            SignallingRelayGeneration = signallingRelayGeneration,
            AdmissionGeneration = admissionGeneration
        };

        private void InvalidateLocked()
        {
            admissionGeneration = admissionGeneration == long.MaxValue
                ? 1
                : admissionGeneration + 1;
        }
    }
}
