using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Google.Protobuf;
using TermLink.Protocol.Transport.V1;

namespace TermLink.Agent
{
    internal static class ProductionDirectMultipairPlanner
    {
        internal static IReadOnlyList<ProductionDirectCandidatePairV4> Build(
            ProductionDirectNativeRole localRole,
            IReadOnlyList<ProductionDirectCandidateV4> localCandidates,
            IReadOnlyList<DirectCandidateV1> remoteCandidates,
            long ticketExpiryMilliseconds)
        {
            if (localRole is not ProductionDirectNativeRole.Initiator and not ProductionDirectNativeRole.Responder ||
                localCandidates == null || remoteCandidates == null || localCandidates.Count is < 1 or > 32 ||
                remoteCandidates.Count is < 1 or > 32)
                throw new ArgumentException("DIRECT_MULTIPAIR_CANDIDATES_INVALID");
            var remote = remoteCandidates.Select(value => Decode(value, ticketExpiryMilliseconds)).ToArray();
            ValidateSet(localCandidates, ticketExpiryMilliseconds);
            ValidateSet(remote, ticketExpiryMilliseconds);
            var ordered = localCandidates.SelectMany(local => remote
                    .Where(peer => local.Endpoint.Address.AddressFamily == peer.Endpoint.Address.AddressFamily)
                    .Select(peer =>
                    {
                        var initiator = localRole == ProductionDirectNativeRole.Initiator ? local : peer;
                        var responder = localRole == ProductionDirectNativeRole.Responder ? local : peer;
                        return new
                        {
                            Initiator = initiator,
                            Responder = responder,
                            Preference = PairPreference(initiator, responder)
                        };
                    }))
                .Where(value => value.Preference >= 0)
                .OrderBy(value => value.Preference)
                .ThenByDescending(value => Math.Min(value.Initiator.Priority, value.Responder.Priority))
                .ThenByDescending(value => value.Initiator.Priority)
                .ThenByDescending(value => value.Responder.Priority)
                .ThenBy(value => value.Initiator.NetworkCost + value.Responder.NetworkCost)
                .ThenBy(value => value.Initiator.CandidateId.ToString("N"), StringComparer.Ordinal)
                .ThenBy(value => value.Responder.CandidateId.ToString("N"), StringComparer.Ordinal)
                .Take(ProductionDirectNativeAbiV4.MaximumPairsTotal)
                .ToArray();
            return ordered.Select((value, index) => new ProductionDirectCandidatePairV4
            {
                Rank = checked((uint)index + 1),
                Initiator = value.Initiator,
                Responder = value.Responder
            }).ToArray();
        }

        internal static IReadOnlyList<IReadOnlyList<ProductionDirectCandidatePairV4>> Batches(
            IReadOnlyList<ProductionDirectCandidatePairV4> pairs)
        {
            if (pairs == null || pairs.Count is < 1 or > ProductionDirectNativeAbiV4.MaximumPairsTotal ||
                pairs.Select((pair, index) => pair?.Rank == index + 1).Any(valid => !valid) ||
                pairs.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != pairs.Count)
                throw new ArgumentException("DIRECT_MULTIPAIR_PLAN_INVALID");
            var result = new List<IReadOnlyList<ProductionDirectCandidatePairV4>>();
            for (var offset = 0; offset < pairs.Count; offset += ProductionDirectNativeAbiV4.MaximumPairsPerBatch)
                result.Add(pairs.Skip(offset).Take(ProductionDirectNativeAbiV4.MaximumPairsPerBatch).ToArray());
            return result.AsReadOnly();
        }

        internal static bool SameConservativeLan(IPAddress left, IPAddress right)
        {
            if (left == null || right == null || left.AddressFamily != right.AddressFamily) return false;
            var local = left.GetAddressBytes();
            var remote = right.GetAddressBytes();
            if (local.Length == 4)
            {
                return PrivateV4(local) && PrivateV4(remote) &&
                    local.AsSpan(0, 3).SequenceEqual(remote.AsSpan(0, 3));
            }
            return local.Length == 16 && (local[0] & 0xfe) == 0xfc &&
                (remote[0] & 0xfe) == 0xfc && local.AsSpan(0, 8).SequenceEqual(remote.AsSpan(0, 8));
        }

        internal static DirectCandidateV1 Encode(ProductionDirectCandidateV4 value) => new()
        {
            CandidateId = ByteString.CopyFrom(GuidBytes(value.CandidateId)),
            Kind = value.Kind == ProductionDirectCandidateKindV4.Host
                ? DirectCandidateKindV1.Host : DirectCandidateKindV1.ServerReflexive,
            Carrier = DirectCarrierV1.UdpQuic,
            IpAddress = ByteString.CopyFrom(value.Endpoint.Address.GetAddressBytes()),
            Port = checked((uint)value.Endpoint.Port),
            Priority = value.Priority,
            RelatedCandidateId = value.RelatedCandidateId == Guid.Empty ? ByteString.Empty :
                ByteString.CopyFrom(GuidBytes(value.RelatedCandidateId)),
            NetworkCost = value.NetworkCost,
            ExpiresAtMs = value.ExpiresAtMilliseconds
        };

        internal static ProductionDirectCandidateV4 Decode(
            DirectCandidateV1 value, long ticketExpiryMilliseconds)
        {
            if (value == null || value.CandidateId.Length != 16 || value.CandidateId.Span.IndexOfAnyExcept((byte)0) < 0 ||
                value.Kind is not DirectCandidateKindV1.Host and not DirectCandidateKindV1.ServerReflexive ||
                value.Carrier != DirectCarrierV1.UdpQuic || value.IpAddress.Length is not 4 and not 16 ||
                value.Port is < 1_024 or > 65_535 || value.Priority is < 1 or > int.MaxValue || value.NetworkCost > 999 ||
                value.ExpiresAtMs <= 0 || value.ExpiresAtMs > ticketExpiryMilliseconds ||
                (value.Kind == DirectCandidateKindV1.Host ? value.RelatedCandidateId.Length != 0 :
                    value.RelatedCandidateId.Length != 16))
                throw new ArgumentException("DIRECT_MULTIPAIR_CANDIDATE_INVALID");
            var address = new IPAddress(value.IpAddress.Span);
            if (!ProductionDirectCandidateCollector.IsAllowedHostAddress(address))
                throw new ArgumentException("DIRECT_MULTIPAIR_ADDRESS_INVALID");
            return new ProductionDirectCandidateV4
            {
                CandidateId = GuidFromBytes(value.CandidateId.Span),
                RelatedCandidateId = value.RelatedCandidateId.Length == 0 ? Guid.Empty :
                    GuidFromBytes(value.RelatedCandidateId.Span),
                Endpoint = new ProductionDirectNativeEndpoint(address, checked((int)value.Port)),
                Kind = value.Kind == DirectCandidateKindV1.Host
                    ? ProductionDirectCandidateKindV4.Host : ProductionDirectCandidateKindV4.ServerReflexive,
                Priority = value.Priority,
                NetworkCost = value.NetworkCost,
                ExpiresAtMilliseconds = value.ExpiresAtMs
            };
        }

        internal static DirectCandidatePairV1 EncodePair(ProductionDirectCandidatePairV4 pair) => new()
        {
            InitiatorCandidateId = ByteString.CopyFrom(GuidBytes(pair.Initiator.CandidateId)),
            ResponderCandidateId = ByteString.CopyFrom(GuidBytes(pair.Responder.CandidateId))
        };

        internal static bool PairIdsEqual(DirectCandidatePairV1 wire, ProductionDirectCandidatePairV4 pair) =>
            wire != null && pair != null && wire.InitiatorCandidateId.Span.SequenceEqual(GuidBytes(pair.Initiator.CandidateId)) &&
            wire.ResponderCandidateId.Span.SequenceEqual(GuidBytes(pair.Responder.CandidateId));

        private static int PairPreference(
            ProductionDirectCandidateV4 initiator, ProductionDirectCandidateV4 responder)
        {
            if (initiator.Kind == ProductionDirectCandidateKindV4.Host &&
                responder.Kind == ProductionDirectCandidateKindV4.Host)
                return SameConservativeLan(initiator.Endpoint.Address, responder.Endpoint.Address) ? 0 : -1;
            if (initiator.Kind == ProductionDirectCandidateKindV4.ServerReflexive &&
                responder.Kind == ProductionDirectCandidateKindV4.ServerReflexive) return 1;
            if (initiator.Kind == ProductionDirectCandidateKindV4.Host) return 2;
            if (responder.Kind == ProductionDirectCandidateKindV4.Host) return 3;
            return -1;
        }

        private static void ValidateSet(
            IReadOnlyList<ProductionDirectCandidateV4> values, long ticketExpiryMilliseconds)
        {
            var ids = new HashSet<Guid>();
            var endpoints = new HashSet<string>(StringComparer.Ordinal);
            var hosts = new HashSet<Guid>();
            foreach (var value in values)
            {
                if (value == null || value.CandidateId == Guid.Empty || value.Endpoint == null ||
                    value.Endpoint.Port is < 1_024 or > 65_535 || value.Priority is < 1 or > int.MaxValue ||
                    value.NetworkCost > 999 || value.ExpiresAtMilliseconds <= 0 ||
                    value.ExpiresAtMilliseconds > ticketExpiryMilliseconds || !ids.Add(value.CandidateId) ||
                    !endpoints.Add(value.Endpoint.Address + ":" + value.Endpoint.Port))
                    throw new ArgumentException("DIRECT_MULTIPAIR_CANDIDATE_SET_INVALID");
                if (value.Kind == ProductionDirectCandidateKindV4.Host)
                {
                    if (value.RelatedCandidateId != Guid.Empty) throw new ArgumentException();
                    hosts.Add(value.CandidateId);
                }
            }
            if (values.Any(value => value.Kind == ProductionDirectCandidateKindV4.ServerReflexive &&
                !hosts.Contains(value.RelatedCandidateId))) throw new ArgumentException();
        }

        private static bool PrivateV4(ReadOnlySpan<byte> value) => value[0] == 10 ||
            (value[0] == 172 && value[1] is >= 16 and <= 31) ||
            (value[0] == 192 && value[1] == 168);
        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
        private static Guid GuidFromBytes(ReadOnlySpan<byte> value) =>
            Guid.ParseExact(Convert.ToHexString(value), "N");
    }
}
