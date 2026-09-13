using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal enum ProductionDirectRouteEligibility
    {
        RelayOnly = 0,
        DirectEligible = 1
    }

    internal enum ProductionDirectPeerDirection
    {
        PcToAndroid = 1,
        AndroidToPc = 2
    }

    internal enum ProductionDirectAuthenticatedPathKind
    {
        Unknown = 0,
        LocalAreaNetwork = 1,
        NatTraversal = 2
    }

    internal enum ProductionDirectPeerCarrier
    {
        Relay = 0,
        NegotiatingDirect = 1,
        LocalAreaNetworkDirect = 2,
        NatTraversalDirect = 3
    }

    /// <summary>
    /// Fail-closed allowlist for the optional Direct data carrier. Lifecycle,
    /// subscription, ACK, flow, lease, resize, history, and other control
    /// Envelopes remain on the authoritative Relay. Only terminal bytes in the
    /// protocol-defined direction may enter Direct routing.
    /// </summary>
    internal static class ProductionDirectRoutePolicy
    {
        internal static ProductionDirectRouteEligibility Evaluate(
            MessageType messageType,
            Direction direction) => (messageType, direction) switch
        {
            (MessageType.OutputChunk, Direction.PcToAndroid) =>
                ProductionDirectRouteEligibility.DirectEligible,
            (MessageType.InputEvent, Direction.AndroidToPc) =>
                ProductionDirectRouteEligibility.DirectEligible,
            _ => ProductionDirectRouteEligibility.RelayOnly
        };

        internal static ProductionDirectRouteEligibility Evaluate(
            Envelope envelope,
            out Guid androidDeviceId)
        {
            androidDeviceId = Guid.Empty;
            if (envelope == null ||
                !TryResolveAndroidDeviceId(envelope, out androidDeviceId))
                return ProductionDirectRouteEligibility.RelayOnly;
            return Evaluate(envelope.MessageType, envelope.Direction);
        }

        internal static bool TryResolveAndroidDeviceId(
            Envelope envelope,
            out Guid androidDeviceId)
        {
            androidDeviceId = Guid.Empty;
            if (envelope == null) return false;
            ByteString encoded = envelope.Direction switch
            {
                Direction.PcToAndroid => envelope.RecipientDeviceId,
                Direction.AndroidToPc => envelope.SenderDeviceId,
                _ => null
            };
            if (encoded == null || encoded.Length != 16 ||
                encoded.Span.IndexOfAnyExcept((byte)0) < 0) return false;
            androidDeviceId = Guid.ParseExact(
                Convert.ToHexString(encoded.Span),
                "N");
            return androidDeviceId != Guid.Empty;
        }
    }

    internal sealed class ProductionDirectPeerPathFence :
        IEquatable<ProductionDirectPeerPathFence>
    {
        public ulong PathEpoch { get; init; }
        public Guid ConnectionId { get; init; }
        public Guid AttemptId { get; init; }

        public bool Equals(ProductionDirectPeerPathFence other) => other != null &&
            PathEpoch == other.PathEpoch && ConnectionId == other.ConnectionId &&
            AttemptId == other.AttemptId;
        public override bool Equals(object obj) =>
            Equals(obj as ProductionDirectPeerPathFence);
        public override int GetHashCode() =>
            HashCode.Combine(PathEpoch, ConnectionId, AttemptId);

        internal static bool IsValid(ProductionDirectPeerPathFence value) =>
            value != null && value.PathEpoch is > 0 and <= long.MaxValue &&
            value.ConnectionId != Guid.Empty && value.AttemptId != Guid.Empty;

        internal static ProductionDirectPeerPathFence Copy(
            ProductionDirectPeerPathFence value) => value == null ? null : new()
        {
            PathEpoch = value.PathEpoch,
            ConnectionId = value.ConnectionId,
            AttemptId = value.AttemptId
        };
    }

    internal sealed class ProductionDirectPeerRouteSnapshot
    {
        public Guid AndroidDeviceId { get; init; }
        public long RelayGeneration { get; init; }
        public bool RelayConnected { get; init; }
        public ProductionDirectPeerPathFence Fence { get; init; }
        public ProductionDirectAuthenticatedPathKind PathKind { get; init; }
        public bool PcToAndroidActive { get; init; }
        public bool AndroidToPcActive { get; init; }
        public ProductionDirectPeerCarrier Carrier { get; init; }
        public string LastDirectFault { get; init; } = string.Empty;
        public bool Disposed { get; init; }
    }

    internal interface IProductionDirectPeerRouteActor : IDisposable
    {
        Guid AndroidDeviceId { get; }
        ProductionDirectPeerRouteSnapshot Snapshot { get; }
        void ObserveRelay(bool connected, long generation);
        bool TryBeginDirect(
            ProductionDirectPeerPathFence fence,
            ProductionDirectAuthenticatedPathKind pathKind);
        bool TryMarkDirectionActive(
            ProductionDirectPeerPathFence fence,
            ProductionDirectPeerDirection direction);
        bool TryRetireDirect(
            ProductionDirectPeerPathFence fence,
            string reasonCode);
    }

    /// <summary>
    /// Per-Android-device carrier aggregator. Direct lifecycle state is
    /// orthogonal to Relay physical generation. A Direct fault clears only the
    /// matching peer fence and can never advance, disconnect, or fail Relay.
    /// </summary>
    internal sealed class ProductionDirectPeerRouteActor :
        IProductionDirectPeerRouteActor
    {
        private readonly object sync = new();
        private readonly Guid androidDeviceId;
        private long relayGeneration;
        private bool relayConnected;
        private ProductionDirectPeerPathFence fence;
        private ProductionDirectAuthenticatedPathKind pathKind;
        private bool pcToAndroidActive;
        private bool androidToPcActive;
        private string lastDirectFault = string.Empty;
        private bool disposed;

        internal ProductionDirectPeerRouteActor(Guid androidDeviceId)
        {
            if (androidDeviceId == Guid.Empty)
                throw new ArgumentException("An Android device ID is required.",
                    nameof(androidDeviceId));
            this.androidDeviceId = androidDeviceId;
        }

        public Guid AndroidDeviceId => androidDeviceId;
        public ProductionDirectPeerRouteSnapshot Snapshot
        {
            get
            {
                lock (sync) return SnapshotLocked();
            }
        }

        public void ObserveRelay(bool connected, long generation)
        {
            if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
            lock (sync)
            {
                if (disposed || generation < relayGeneration) return;
                relayGeneration = generation;
                relayConnected = connected;
            }
        }

        public bool TryBeginDirect(
            ProductionDirectPeerPathFence value,
            ProductionDirectAuthenticatedPathKind valuePathKind)
        {
            if (!ProductionDirectPeerPathFence.IsValid(value) ||
                valuePathKind is not ProductionDirectAuthenticatedPathKind.LocalAreaNetwork and
                    not ProductionDirectAuthenticatedPathKind.NatTraversal) return false;
            lock (sync)
            {
                if (disposed) return false;
                if (fence != null)
                {
                    if (value.PathEpoch < fence.PathEpoch) return false;
                    if (value.PathEpoch == fence.PathEpoch)
                        return fence.Equals(value) && pathKind == valuePathKind;
                }
                fence = ProductionDirectPeerPathFence.Copy(value);
                pathKind = valuePathKind;
                pcToAndroidActive = false;
                androidToPcActive = false;
                lastDirectFault = string.Empty;
                return true;
            }
        }

        public bool TryMarkDirectionActive(
            ProductionDirectPeerPathFence value,
            ProductionDirectPeerDirection direction)
        {
            if (direction is not ProductionDirectPeerDirection.PcToAndroid and
                not ProductionDirectPeerDirection.AndroidToPc) return false;
            lock (sync)
            {
                if (disposed || fence == null || !fence.Equals(value) ||
                    pathKind is not ProductionDirectAuthenticatedPathKind.LocalAreaNetwork and
                        not ProductionDirectAuthenticatedPathKind.NatTraversal) return false;
                if (direction == ProductionDirectPeerDirection.PcToAndroid)
                    pcToAndroidActive = true;
                else
                    androidToPcActive = true;
                return true;
            }
        }

        public bool TryRetireDirect(
            ProductionDirectPeerPathFence value,
            string reasonCode)
        {
            lock (sync)
            {
                if (disposed || fence == null || !fence.Equals(value)) return false;
                fence = null;
                pathKind = ProductionDirectAuthenticatedPathKind.Unknown;
                pcToAndroidActive = false;
                androidToPcActive = false;
                lastDirectFault = NormalizeReason(reasonCode);
                return true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                fence = null;
                pathKind = ProductionDirectAuthenticatedPathKind.Unknown;
                pcToAndroidActive = false;
                androidToPcActive = false;
            }
        }

        private ProductionDirectPeerRouteSnapshot SnapshotLocked() => new()
        {
            AndroidDeviceId = androidDeviceId,
            RelayGeneration = relayGeneration,
            RelayConnected = relayConnected,
            Fence = ProductionDirectPeerPathFence.Copy(fence),
            PathKind = pathKind,
            PcToAndroidActive = pcToAndroidActive,
            AndroidToPcActive = androidToPcActive,
            Carrier = CarrierLocked(),
            LastDirectFault = lastDirectFault,
            Disposed = disposed
        };

        private ProductionDirectPeerCarrier CarrierLocked()
        {
            if (disposed || fence == null || pathKind ==
                ProductionDirectAuthenticatedPathKind.Unknown)
                return ProductionDirectPeerCarrier.Relay;
            if (!pcToAndroidActive || !androidToPcActive)
                return ProductionDirectPeerCarrier.NegotiatingDirect;
            return pathKind switch
            {
                ProductionDirectAuthenticatedPathKind.LocalAreaNetwork =>
                    ProductionDirectPeerCarrier.LocalAreaNetworkDirect,
                ProductionDirectAuthenticatedPathKind.NatTraversal =>
                    ProductionDirectPeerCarrier.NatTraversalDirect,
                _ => ProductionDirectPeerCarrier.Relay
            };
        }

        private static string NormalizeReason(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= 128 ? value : value[..128];
        }
    }

    internal interface IProductionDirectPeerRouteRegistry : IDisposable
    {
        void ObserveRelay(bool connected, long generation);
        bool TryGetOrCreate(
            Guid androidDeviceId,
            out IProductionDirectPeerRouteActor actor);
        bool TryGet(
            Guid androidDeviceId,
            out IProductionDirectPeerRouteActor actor);
        bool TryRetirePeer(
            Guid androidDeviceId,
            ProductionDirectPeerPathFence fence,
            string reasonCode);
        int RetireAllDirect(string reasonCode);
        bool Remove(Guid androidDeviceId);
        IReadOnlyList<ProductionDirectPeerRouteSnapshot> Snapshot();
    }

    internal sealed class ProductionDirectPeerRouteRegistry :
        IProductionDirectPeerRouteRegistry
    {
        private readonly object sync = new();
        private readonly Dictionary<Guid, ProductionDirectPeerRouteActor> actors = new();
        private long relayGeneration;
        private bool relayConnected;
        private bool disposed;

        public void ObserveRelay(bool connected, long generation)
        {
            if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
            ProductionDirectPeerRouteActor[] current;
            lock (sync)
            {
                if (disposed || generation < relayGeneration) return;
                relayGeneration = generation;
                relayConnected = connected;
                current = actors.Values.ToArray();
            }
            foreach (var actor in current) actor.ObserveRelay(connected, generation);
        }

        public bool TryGetOrCreate(
            Guid androidDeviceId,
            out IProductionDirectPeerRouteActor actor)
        {
            actor = null;
            if (androidDeviceId == Guid.Empty) return false;
            lock (sync)
            {
                if (disposed) return false;
                if (!actors.TryGetValue(androidDeviceId, out var current))
                {
                    current = new ProductionDirectPeerRouteActor(androidDeviceId);
                    current.ObserveRelay(relayConnected, relayGeneration);
                    actors.Add(androidDeviceId, current);
                }
                actor = current;
                return true;
            }
        }

        public bool TryGet(
            Guid androidDeviceId,
            out IProductionDirectPeerRouteActor actor)
        {
            lock (sync)
            {
                ProductionDirectPeerRouteActor current = null;
                var found = !disposed && actors.TryGetValue(androidDeviceId, out current);
                actor = found ? current : null;
                return found;
            }
        }

        public bool TryRetirePeer(
            Guid androidDeviceId,
            ProductionDirectPeerPathFence fence,
            string reasonCode)
        {
            ProductionDirectPeerRouteActor actor;
            lock (sync)
            {
                if (disposed || !actors.TryGetValue(androidDeviceId, out actor)) return false;
            }
            return actor.TryRetireDirect(fence, reasonCode);
        }

        public int RetireAllDirect(string reasonCode)
        {
            ProductionDirectPeerRouteSnapshot[] current;
            lock (sync)
            {
                if (disposed) return 0;
                current = actors.Values
                    .Select(value => value.Snapshot)
                    .Where(value => value.Fence != null)
                    .ToArray();
            }

            var retired = 0;
            foreach (var snapshot in current)
            {
                if (TryRetirePeer(
                        snapshot.AndroidDeviceId,
                        snapshot.Fence,
                        reasonCode))
                {
                    retired++;
                }
            }
            return retired;
        }

        public bool Remove(Guid androidDeviceId)
        {
            ProductionDirectPeerRouteActor actor;
            lock (sync)
            {
                if (disposed || !actors.Remove(androidDeviceId, out actor)) return false;
            }
            actor.Dispose();
            return true;
        }

        public IReadOnlyList<ProductionDirectPeerRouteSnapshot> Snapshot()
        {
            lock (sync)
            {
                return actors.Values
                    .Select(value => value.Snapshot)
                    .OrderBy(value => value.AndroidDeviceId)
                    .ToArray();
            }
        }

        public void Dispose()
        {
            ProductionDirectPeerRouteActor[] current;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                current = actors.Values.ToArray();
                actors.Clear();
            }
            foreach (var actor in current) actor.Dispose();
        }
    }
}
