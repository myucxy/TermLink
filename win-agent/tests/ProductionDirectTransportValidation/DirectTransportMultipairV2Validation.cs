using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol.Transport.V1;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class DirectTransportMultipairV2Validation
    {
        internal static void Validate()
        {
            var path = FindVector();
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var pairwise = Hex(root, "pairwise_secret_hex");
            var connection = GuidHex(root, "connection_id_hex");
            var round = GuidHex(root, "check_round_id_hex");
            var local = GuidHex(root, "local_device_id_hex");
            var peer = GuidHex(root, "peer_device_id_hex");
            var nonce = Hex(root, "connectivity_nonce_hex");
            var initiatorCandidate = Guid.ParseExact(
                root.GetProperty("pair").GetProperty("initiator_candidate_id_hex").GetString(), "N");
            var responderCandidate = Guid.ParseExact(
                root.GetProperty("pair").GetProperty("responder_candidate_id_hex").GetString(), "N");
            var localKey = ProductionDirectTransportCrypto.DeriveProbeKeyV2(
                pairwise, connection, root.GetProperty("path_epoch").GetUInt64(), round, local, peer);
            var peerKey = ProductionDirectTransportCrypto.DeriveProbeKeyV2(
                pairwise, connection, root.GetProperty("path_epoch").GetUInt64(), round, peer, local);
            Require(Convert.ToHexString(localKey).Equals(
                    root.GetProperty("local_send_probe_hmac_key_hex").GetString(),
                    StringComparison.OrdinalIgnoreCase), "V2 local probe key disagrees with the shared vector.");
            Require(Convert.ToHexString(peerKey).Equals(
                    root.GetProperty("peer_receive_probe_hmac_key_hex").GetString(),
                    StringComparison.OrdinalIgnoreCase), "V2 peer probe key disagrees with the shared vector.");
            var pairNonce = ProductionDirectTransportCrypto.DerivePairProbeNonceV2(
                localKey, nonce, initiatorCandidate, responderCandidate);
            Require(Convert.ToHexString(pairNonce).Equals(
                    root.GetProperty("local_pair_probe_nonce_hex").GetString(),
                    StringComparison.OrdinalIgnoreCase), "V2 pair nonce disagrees with the shared vector.");

            var batch = new DirectConnectivityCheckBatchV2
            {
                CheckRoundId = ByteString.CopyFrom(GuidBytes(round)),
                ConnectivityNonce = ByteString.CopyFrom(nonce),
                BatchSeq = root.GetProperty("batch_seq").GetUInt32(),
                PlanComplete = root.GetProperty("plan_complete").GetBoolean()
            };
            batch.Pairs.Add(new DirectCandidatePairV1
            {
                InitiatorCandidateId = ByteString.CopyFrom(GuidBytes(initiatorCandidate)),
                ResponderCandidateId = ByteString.CopyFrom(GuidBytes(responderCandidate))
            });
            Require(batch.ToByteArray().SequenceEqual(Hex(root, "check_batch_protobuf_hex")),
                "V2 check-batch protobuf disagrees with the shared vector.");
            Require((int)TransportSignalTypeV1.ConnectivityCheckBatchV2 == 11 &&
                    (int)TransportSignalTypeV1.NominationV2 == 12 &&
                    (int)TransportSignalTypeV1.NominationAckV2 == 13 &&
                    (int)TransportSignalTypeV1.NominationResultV2 == 14,
                "Multipair signal numeric values drifted.");

            ValidateRouteSafePlannerAndFifthPair();
        }

        private static void ValidateRouteSafePlannerAndFifthPair()
        {
            var expires = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
            var local = Enumerable.Range(1, 5).Select(index => Candidate(
                Guid.ParseExact(index.ToString("x32"), "N"),
                IPAddress.Parse("192.168.50." + (10 + index)), 30_000 + index, expires,
                ProductionDirectCandidateKindV4.Host, Guid.Empty, (uint)(100 - index))).ToArray();
            var remote = Enumerable.Range(1, 5).Select(index =>
                ProductionDirectMultipairPlanner.Encode(Candidate(
                    Guid.ParseExact((index + 20).ToString("x32"), "N"),
                    IPAddress.Parse("192.168.50." + (30 + index)), 31_000 + index, expires,
                    ProductionDirectCandidateKindV4.Host, Guid.Empty, (uint)(100 - index)))).ToArray();
            var plan = ProductionDirectMultipairPlanner.Build(
                ProductionDirectNativeRole.Responder, local, remote, expires);
            Require(plan.Count == 25 && plan.Select((pair, index) => pair.Rank == index + 1).All(value => value),
                "Same-LAN multipair plan is not stable and contiguous.");
            var firstFourFailedWinner = plan[4];
            Require(firstFourFailedWinner.Rank == 5 &&
                    ProductionDirectMultipairPlanner.PairIdsEqual(
                        ProductionDirectMultipairPlanner.EncodePair(firstFourFailedWinner), firstFourFailedWinner),
                "The fifth candidate pair cannot be selected after four failed checks.");
            var unsafeRemote = new[]
            {
                ProductionDirectMultipairPlanner.Encode(Candidate(Guid.NewGuid(),
                    IPAddress.Parse("192.168.51.30"), 32_000, expires,
                    ProductionDirectCandidateKindV4.Host, Guid.Empty, 1))
            };
            Require(ProductionDirectMultipairPlanner.Build(
                    ProductionDirectNativeRole.Responder, local.Take(1).ToArray(), unsafeRemote, expires).Count == 0,
                "A different-subnet HOST pair escaped route validation.");
        }

        private static ProductionDirectCandidateV4 Candidate(Guid id, IPAddress address, int port,
            long expires, ProductionDirectCandidateKindV4 kind, Guid related, uint priority) => new()
        {
            CandidateId = id, RelatedCandidateId = related,
            Endpoint = new ProductionDirectNativeEndpoint(address, port), Kind = kind,
            Priority = priority, NetworkCost = 0, ExpiresAtMilliseconds = expires
        };

        private static string FindVector()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    var candidate = Path.Combine(directory.FullName, "protocol", "test-vectors",
                        "direct-transport-multipair-v2.json");
                    if (File.Exists(candidate)) return candidate;
                    directory = directory.Parent;
                }
            }
            throw new FileNotFoundException("The multipair V2 vector was not found.");
        }

        private static byte[] Hex(JsonElement root, string property) =>
            Convert.FromHexString(root.GetProperty(property).GetString());
        private static Guid GuidHex(JsonElement root, string property) =>
            Guid.ParseExact(root.GetProperty(property).GetString(), "N");
        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
