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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.Protocol.Transport.V1;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class Program
    {
        private const uint StunMagicCookie = 0x2112A442;
        private static bool expectDirectDataPlaneEnabled;

        private static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.SequenceEqual(new[] { "--relay-outbox-only" }))
                {
                    await ProductionRelayExactOutboxValidation.ValidateAsync().ConfigureAwait(false);
                    Console.WriteLine("Relay exact outbox validation passed (no LAN/NAT tests).");
                    return 0;
                }
                if (args.SequenceEqual(new[] { "--broker-capabilities-only" }))
                {
                    ValidateBrokerCaptureCapabilityNegotiation();
                    Console.WriteLine("Broker capture Relay capability validation passed.");
                    return 0;
                }
                var unsupportedArguments = args.Where(value =>
                        !string.Equals(
                            value,
                            "--expect-direct-data-plane-acceptance",
                            StringComparison.Ordinal) &&
                        !string.Equals(value, "--nologo", StringComparison.Ordinal))
                    .ToArray();
                if (unsupportedArguments.Length != 0)
                {
                    throw new ArgumentException(
                        "Unsupported validation argument: " + unsupportedArguments[0]);
                }
#if TERMLINK_DIRECT_DATA_PLANE
                expectDirectDataPlaneEnabled = true;
#else
                expectDirectDataPlaneEnabled = false;
#endif
                ValidateRelayFallbackAndFoundationBoundary();
                ValidateFeatureAdvertisementDefaultsOff();
                ValidateRealtimeCapabilityNegotiation();
                ValidateDirectDataPlaneAdmission();
                ValidateExactRelayEnvelopeIngress();
                ValidateDirectRouteEligibilityPolicy();
                ValidateDirectCutoverCodec();
                ProductionDirectCutoverSignalCodecValidation.Validate();
                await ValidatePerAndroidDeviceRouteActorsAsync().ConfigureAwait(false);
                await ProductionDirectDataRuntimeValidation.ValidateAsync().ConfigureAwait(false);
                await ProductionRelayExactOutboxValidation.ValidateAsync().ConfigureAwait(false);
                await ProductionAgentRuntimeDirectWiringValidation.ValidateAsync().ConfigureAwait(false);
#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
                await ProductionDirectAcceptanceDiagnosticsValidation.ValidateAsync()
                    .ConfigureAwait(false);
#endif
                DirectTransportDiagnosticsValidation.Validate();
                ValidateEpochRateAndFailureCooldown();
                ValidatePeerIsolationAndStaleGenerationCallbacks();
                ValidateCandidateFilteringAndSocketLease();
                await ProductionDirectInterfaceSelectionValidation.ValidateAsync().ConfigureAwait(false);
                ValidateStunTransactionAndXorMappedAddress();
                ValidateCanonicalTicketCrossLanguageVector();
                DirectTransportCryptoVectorValidation.Validate();
                DirectTransportMultipairV2Validation.Validate();
                ValidateTicketSignalAndProbeCryptography();
                await ValidateBidirectionalUdpProbeAsync().ConfigureAwait(false);
                await ProductionDirectShadowRuntimeValidation.ValidateAsync().ConfigureAwait(false);
                await ProductionDirectMultipairRuntimeValidation.ValidateAsync().ConfigureAwait(false);
                Console.WriteLine("Production direct transport foundation validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidateRelayFallbackAndFoundationBoundary()
        {
            var supervisor = new ProductionDirectTransportSupervisor();
            supervisor.ObserveRelayConnected(true);
            Require(supervisor.TryBeginAttempt(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    1,
                    out var attempt,
                    out var rejection) &&
                    rejection.Length == 0,
                "A healthy Relay did not permit direct gathering.");
            supervisor.MarkGatheringComplete(attempt);
            var authenticatedProbeSha256 = SHA256.HashData(
                Encoding.ASCII.GetBytes("authenticated-probe-transcript"));
            supervisor.MarkConnectivityProbeVerified(attempt, authenticatedProbeSha256);
            var verified = supervisor.Snapshot();
            Require(verified.State == ProductionDirectPathState.ProbeVerifiedNotDirect &&
                    verified.RelayConnected && !verified.DirectRoutingPermitted &&
                    verified.AttemptGeneration == attempt.Generation &&
                    verified.AuthenticatedProbeSha256.SequenceEqual(authenticatedProbeSha256),
                "A verified probe escaped the Relay-only foundation boundary.");

            Require(supervisor.TryFallbackAttempt(attempt, "PROBE_ONLY_COMPLETE"),
                "The current direct attempt could not synchronously fall back.");
            var fallback = supervisor.Snapshot();
            Require(fallback.State == ProductionDirectPathState.RelayOnly && fallback.RelayConnected &&
                    fallback.ConnectionId == Guid.Empty && fallback.PeerDeviceId == Guid.Empty &&
                    fallback.PathEpoch == 0 && fallback.FallbackReason == "PROBE_ONLY_COMPLETE" &&
                    fallback.AuthenticatedProbeSha256.Length == 0 &&
                    !supervisor.TryFallbackAttempt(attempt, "STALE_CALLBACK"),
                "A direct failure did not synchronously return to an intact Relay.");

            var invalidOrder = new ProductionDirectTransportSupervisor();
            invalidOrder.ObserveRelayConnected(true);
            Require(invalidOrder.TryBeginAttempt(
                    Guid.NewGuid(), Guid.NewGuid(), 1, out var currentAttempt, out _),
                "Ordered-transition fixture could not start.");
            RequireThrows<InvalidOperationException>(
                () => invalidOrder.MarkGatheringComplete(default),
                "A missing attempt token was accepted.");
            invalidOrder.MarkGatheringComplete(currentAttempt);
            RequireThrows<ArgumentException>(
                () => invalidOrder.MarkConnectivityProbeVerified(currentAttempt, new byte[32]),
                "An empty authenticated-probe digest was accepted.");
            invalidOrder.ObserveRelayConnected(false);
            Require(invalidOrder.Snapshot().State == ProductionDirectPathState.RelayOnly,
                "Loss of the mandatory Relay fallback left negotiation active.");
        }

        private static void ValidateFeatureAdvertisementDefaultsOff()
        {
            const string runtimeDataPlaneFlag = "TERMLINK_ENABLE_DIRECT_DATA_PLANE";
            var previousRuntimeFlag = Environment.GetEnvironmentVariable(runtimeDataPlaneFlag);
            try
            {
                Environment.SetEnvironmentVariable(runtimeDataPlaneFlag, "true");
                Require(ProductionAgentRuntime.DirectTransportDataPlaneBuildEnabled ==
                        expectDirectDataPlaneEnabled,
                    "A runtime environment variable changed the compile-time Direct data-plane gate.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(runtimeDataPlaneFlag, previousRuntimeFlag);
            }
            Require(!ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled(null) &&
                    !ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled(string.Empty) &&
                    !ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled("0") &&
                    !ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled("false") &&
                    !ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled("yes") &&
                    ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled(" 1 ") &&
                    ProductionAgentRuntime.IsDirectTransportShadowRuntimeEnabled(" TrUe "),
                "The Direct shadow runtime flag was not strict and default-off.");
            using var hello = JsonDocument.Parse(
                ProductionAgentRuntime.BuildHelloPayloadWithFeatures(
                    terminalSnapshotEnabled: true,
                    typedInputEnabled: true));
            var capabilities = hello.RootElement.GetProperty("capabilities")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Require(!capabilities.Any(value =>
                    value?.Contains("direct", StringComparison.OrdinalIgnoreCase) == true),
                "A Direct transport capability leaked into the Relay HELLO.");
            Require(ProductionAgentRuntime.DirectTransportDataPlaneBuildEnabled ==
                    expectDirectDataPlaneEnabled,
                "The compiled Direct data-plane gate did not match the build configuration.");

            using var directHello = JsonDocument.Parse(
                ProductionAgentRuntime.BuildHelloPayloadWithFeatures(
                    terminalSnapshotEnabled: true,
                    typedInputEnabled: true,
                    directTransportDataEnabled: true));
            var directCapabilities = directHello.RootElement.GetProperty("capabilities")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Require(directCapabilities.Count(value => string.Equals(
                        value,
                        ProductionRealtimeCapabilities.DirectTransportDataV1,
                        StringComparison.Ordinal)) == 1,
                "The explicit Direct data capability flag was not advertised exactly once.");
            Require(directCapabilities.Count(value => string.Equals(
                        value,
                        ProductionRealtimeCapabilities.DirectSessionCutoverV1,
                        StringComparison.Ordinal)) == 1,
                "The explicit Direct session-cutover capability was not advertised exactly once.");
        }

        private static void ValidateRealtimeCapabilityNegotiation()
        {
            var advertised = ProductionRealtimeCapabilities.BuildAdvertisedCapabilities(
                terminalSnapshotEnabled: true,
                typedInputEnabled: true,
                directTransportDataEnabled: true);
            var required = new[]
            {
                "cumulative_ack",
                "e2ee_x25519_chacha20poly1305",
                "flow_control_v1",
                "lease_v1"
            };

            var withoutDirect = ProductionRealtimeCapabilities.ParseWelcome(
                Welcome(required),
                advertised);
            Require(!withoutDirect.DirectTransportDataV1,
                "A missing optional Welcome echo was synthesized as Direct support.");

            var dataOnlyCapabilities = required
                .Concat(new[] { ProductionRealtimeCapabilities.DirectTransportDataV1 })
                .ToArray();
            var dataOnly = ProductionRealtimeCapabilities.ParseWelcome(
                Welcome(dataOnlyCapabilities),
                advertised);
            Require(dataOnly.DirectTransportDataV1 &&
                    !dataOnly.DirectSessionCutoverV1 &&
                    !dataOnly.DirectDataPlaneV1,
                "A legacy data-only Relay Welcome was promoted to session-safe Direct.");

            var sessionOnlyCapabilities = required
                .Concat(new[] { ProductionRealtimeCapabilities.DirectSessionCutoverV1 })
                .ToArray();
            var sessionOnly = ProductionRealtimeCapabilities.ParseWelcome(
                Welcome(sessionOnlyCapabilities),
                advertised);
            Require(!sessionOnly.DirectTransportDataV1 &&
                    sessionOnly.DirectSessionCutoverV1 &&
                    !sessionOnly.DirectDataPlaneV1,
                "A cutover-only Relay Welcome was promoted to Direct data.");

            var withDirectCapabilities = required.Concat(new[]
            {
                ProductionRealtimeCapabilities.DirectTransportDataV1,
                ProductionRealtimeCapabilities.DirectSessionCutoverV1
            }).ToArray();
            var withDirect = ProductionRealtimeCapabilities.ParseWelcome(
                Welcome(withDirectCapabilities), advertised);
            Require(withDirect.DirectTransportDataV1 &&
                    withDirect.DirectSessionCutoverV1 &&
                    withDirect.DirectDataPlaneV1,
                "The complete Direct data/session capability pair was not negotiated.");

            var state = new ProductionRealtimeCapabilityState();
            Require(state.TryMarkConnected(1, withDirect),
                "The first negotiated Relay generation was rejected.");
            var first = state.Snapshot();
            Require(first.Connected && first.PhysicalGeneration == 1 &&
                    first.DirectTransportDataV1 && first.DirectSessionCutoverV1 &&
                    first.DirectDataPlaneV1,
                "The negotiated optional capability was not generation-bound.");
            state.Clear();
            var disconnected = state.Snapshot();
            Require(!disconnected.Connected && disconnected.PhysicalGeneration == 0 &&
                    disconnected.Capabilities.Count == 0 &&
                    !disconnected.DirectTransportDataV1 &&
                    !disconnected.DirectSessionCutoverV1 &&
                    !disconnected.DirectDataPlaneV1,
                "Disconnect retained a negotiated optional capability.");
            Require(state.TryMarkConnected(2, withoutDirect),
                "The next Relay generation was rejected.");
            var withdrawn = state.Snapshot();
            Require(withdrawn.Connected && withdrawn.PhysicalGeneration == 2 &&
                    !withdrawn.DirectTransportDataV1,
                "The next Welcome did not withdraw the old generation's optional capability.");
            Require(!state.TryMarkConnected(1, withDirect) &&
                    state.Snapshot().PhysicalGeneration == 2 &&
                    !state.Snapshot().DirectTransportDataV1,
                "A stale generation restored an obsolete optional capability.");

            RequireThrows<InvalidDataException>(
                () => ProductionRealtimeCapabilities.ParseWelcome(
                    Welcome(required.Where(value => value != "lease_v1").ToArray()),
                    advertised),
                "A Welcome missing a required Relay capability was accepted.");
            RequireThrows<InvalidDataException>(
                () => ProductionRealtimeCapabilities.ParseWelcome(
                    Welcome(required.Concat(new[] { "cumulative_ack" }).ToArray()),
                    advertised),
                "A duplicate Welcome capability was accepted.");

            var malformed = new[]
            {
                "{}",
                "[]",
                "{\"type\":\"SERVER_WELCOME\",\"protocol_version\":1,\"capabilities\":[]}",
                "{\"type\":\"welcome\",\"protocol_version\":2,\"capabilities\":[]}",
                "{\"type\":\"welcome\",\"type\":\"welcome\",\"protocol_version\":1,\"capabilities\":[]}",
                "{\"type\":\"welcome\",\"protocol_version\":1,\"protocol_version\":1,\"capabilities\":[]}",
                "{\"type\":\"welcome\",\"protocol_version\":1,\"capabilities\":[],\"capabilities\":[]}",
                "{\"type\":\"welcome\",\"protocol_version\":1,\"capabilities\":\"cumulative_ack\"}",
                "{\"type\":\"welcome\",\"protocol_version\":1,\"capabilities\":[\"CUMULATIVE_ACK\"]}",
                "{\"type\":\"welcome\",\"protocol_version\":1,\"capabilities\":[\"unoffered_v1\"]}",
                "{\"type\":\"welcome\",\"protocol_version\":1,\"capabilities\":[]} trailing"
            };
            foreach (var value in malformed)
            {
                RequireThrows<InvalidDataException>(
                    () => ProductionRealtimeCapabilities.ParseWelcome(
                        Encoding.UTF8.GetBytes(value),
                        advertised),
                    "A malformed or non-negotiated Welcome was accepted: " + value);
            }

            var tooMany = Enumerable.Range(0, ProductionRealtimeCapabilities.MaximumCapabilities + 1)
                .Select(index => "capability_" + index)
                .ToArray();
            RequireThrows<InvalidDataException>(
                () => ProductionRealtimeCapabilities.ParseWelcome(
                    Welcome(tooMany),
                    tooMany.Concat(required).ToArray()),
                "An over-count Welcome capability list was accepted.");
            var overlong = new string('a',
                ProductionRealtimeCapabilities.MaximumCapabilityBytes + 1);
            RequireThrows<InvalidDataException>(
                () => ProductionRealtimeCapabilities.ParseWelcome(
                    Welcome(required.Concat(new[] { overlong }).ToArray()),
                    required.Concat(new[] { overlong }).ToArray()),
                "An over-length Welcome capability was accepted.");
            RequireThrows<InvalidDataException>(
                () => ProductionRealtimeCapabilities.ParseWelcome(
                    new byte[ProductionRealtimeCapabilities.MaximumWelcomeBytes + 1],
                    advertised),
                "An oversized Welcome payload was accepted.");
        }

        private static void ValidateBrokerCaptureCapabilityNegotiation()
        {
            var broker = ProductionRealtimeCapabilities.TerminalBrokerCapturesV1;
            var relay = new[] { "host_history_v1", broker };
            var subscriber = new[] { "host_history_v1", broker };
            var advertised = ProductionRealtimeCapabilities.BuildAdvertisedCapabilities(
                terminalSnapshotEnabled: true, typedInputEnabled: true,
                directTransportDataEnabled: false, brokerCaptureEnabled: true);
            Require(advertised.Count(value => value == broker) == 1,
                "The Broker capture feature was not advertised exactly once.");
            using (var hello = JsonDocument.Parse(ProductionAgentRuntime.BuildHelloPayloadWithFeatures(
                terminalSnapshotEnabled: true, typedInputEnabled: true,
                directTransportDataEnabled: false, brokerCaptureEnabled: true)))
            {
                var values = hello.RootElement.GetProperty("capabilities").EnumerateArray()
                    .Select(value => value.GetString()).ToArray();
                Require(values.Count(value => value == broker) == 1 &&
                    values.SequenceEqual(advertised),
                    "The actual Relay HELLO and Welcome allowlist advertised different capabilities.");
            }
            Require(!ProductionRealtimeCapabilities.BuildAdvertisedCapabilities(
                    true, true, false, brokerCaptureEnabled: false).Contains(broker),
                "The disabled Broker capture feature leaked into Relay HELLO.");
            Require(ProductionRealtimeCapabilities.ValidateBrokerCaptureSession(
                    true, true, relay, subscriber).Length == 0,
                "A fully negotiated Broker capture session was rejected.");
            Require(ProductionRealtimeCapabilities.ValidateBrokerCaptureSession(
                    true, true, new[] { "host_history_v1" }, subscriber) ==
                    "TERMINAL_RELAY_UPGRADE_REQUIRED" &&
                ProductionRealtimeCapabilities.ValidateBrokerCaptureSession(
                    true, true, new[] { broker }, subscriber) ==
                    "TERMINAL_RELAY_UPGRADE_REQUIRED",
                "An old Relay was misreported as an old Android client.");
            Require(ProductionRealtimeCapabilities.ValidateBrokerCaptureSession(
                    true, true, relay, new[] { "host_history_v1" }) ==
                    "TERMINAL_CLIENT_UPGRADE_REQUIRED" &&
                ProductionRealtimeCapabilities.ValidateBrokerCaptureSession(
                    true, true, relay, new[] { broker }) ==
                    "TERMINAL_CLIENT_UPGRADE_REQUIRED",
                "An old Android client was accepted for Broker capture.");
            Require(ProductionRealtimeCapabilities.ValidateBrokerCaptureSession(
                    true, false, Array.Empty<string>(), Array.Empty<string>()).Length == 0 &&
                ProductionRealtimeCapabilities.ValidateBrokerCaptureSession(
                    false, true, Array.Empty<string>(), Array.Empty<string>()).Length == 0,
                "Legacy and disabled sessions incorrectly require Broker capture capabilities.");
        }

        private static void ValidateDirectDataPlaneAdmission()
        {
            var config = new ProductionDirectTransportServerConfig(
                1,
                new[]
                {
                    ProductionDirectTransportSignalingClient.DirectSignalingCapability,
                    ProductionDirectTransportSignalingClient.DirectShadowCapability,
                    ProductionDirectTransportSignalingClient.DirectDataCapability,
                    ProductionDirectTransportSignalingClient.DirectSessionCutoverCapability
                },
                new Dictionary<string, ProductionDirectTicketSigningKey>(),
                Array.Empty<string>(),
                directRoutingPermitted: true);
            var realtime = new ProductionRealtimeCapabilityState();
            var welcome = new ProductionRealtimeWelcome(new[]
            {
                "cumulative_ack",
                "e2ee_x25519_chacha20poly1305",
                "flow_control_v1",
                "lease_v1",
                ProductionRealtimeCapabilities.DirectTransportDataV1,
                ProductionRealtimeCapabilities.DirectSessionCutoverV1
            });
            Require(realtime.TryMarkConnected(7, welcome),
                "Direct admission fixture could not establish Relay generation 7.");

            var disabled = new ProductionDirectDataPlaneAdmission(buildEnabled: false);
            disabled.ObserveServerConfig(config);
            disabled.ObserveRelay(realtime.Snapshot());
            disabled.ObserveSignalling(ConnectedDirectSignalling(3), 7);
            Require(!disabled.TryCapture(out _),
                "The production compile-time Direct gate was bypassed.");

            var gate = new ProductionDirectDataPlaneAdmission(buildEnabled: true);
            gate.ObserveServerConfig(config);
            gate.ObserveRelay(realtime.Snapshot());
            gate.ObserveSignalling(ConnectedDirectSignalling(3), 7);
            Require(gate.TryCapture(out var first) && gate.IsCurrent(first),
                "Two currently negotiated lanes and the server gate did not authorize cutover.");

            gate.ObserveSignalling(new ProductionDirectSignalingSnapshot
            {
                State = ProductionDirectSignalingLaneState.Fault,
                Generation = 3,
                DirectDataV1Negotiated = false
            }, 7);
            Require(!gate.TryCapture(out _) && !gate.IsCurrent(first),
                "A signalling fault retained a Direct cutover authorization.");

            gate.ObserveSignalling(ConnectedDirectSignalling(4), 7);
            Require(gate.TryCapture(out var second) && gate.IsCurrent(second) &&
                    !gate.IsCurrent(first),
                "A new signalling generation revived an obsolete cutover token.");

            realtime.Clear();
            gate.ObserveRelay(realtime.Snapshot());
            Require(!gate.TryCapture(out _) && !gate.IsCurrent(second),
                "A Relay disconnect retained Direct authorization.");

            Require(realtime.TryMarkConnected(8, welcome),
                "Direct admission fixture could not establish Relay generation 8.");
            gate.ObserveRelay(realtime.Snapshot());
            gate.ObserveSignalling(ConnectedDirectSignalling(5), 7);
            Require(!gate.TryCapture(out _),
                "A signalling callback was rebound to a stale Relay generation.");
            gate.ObserveSignalling(ConnectedDirectSignalling(5), 8);
            Require(gate.TryCapture(out var third) && gate.IsCurrent(third),
                "The signalling lane could not bind to the current Relay generation.");

            gate.ObserveServerConfig(new ProductionDirectTransportServerConfig(
                1,
                config.Capabilities,
                config.TicketSigningKeys,
                config.StunServers,
                directRoutingPermitted: false));
            Require(!gate.TryCapture(out _) && !gate.IsCurrent(third),
                "Withdrawing the server routing gate retained Direct authorization.");

            gate.ObserveServerConfig(config);
            Require(gate.TryCapture(out var fourth) && gate.IsCurrent(fourth),
                "Restoring the server routing gate did not restore admission.");
            gate.ObserveServerConfigUnavailable();
            Require(!gate.TryCapture(out _) && !gate.IsCurrent(fourth),
                "An unavailable server policy retained Direct authorization.");

            var dataOnlyConfig = new ProductionDirectTransportServerConfig(
                1,
                config.Capabilities.Where(value => value !=
                    ProductionDirectTransportSignalingClient.DirectSessionCutoverCapability)
                    .ToArray(),
                config.TicketSigningKeys,
                config.StunServers,
                directRoutingPermitted: true);
            gate.ObserveServerConfig(dataOnlyConfig);
            Require(!gate.TryCapture(out _),
                "A legacy data-only server config authorized session cutover.");

            gate.ObserveServerConfig(config);
            var dataOnlyRelay = new ProductionRealtimeWelcome(new[]
            {
                "cumulative_ack",
                "e2ee_x25519_chacha20poly1305",
                "flow_control_v1",
                "lease_v1",
                ProductionRealtimeCapabilities.DirectTransportDataV1
            });
            Require(realtime.TryMarkConnected(9, dataOnlyRelay),
                "The data-only Relay fixture could not advance its generation.");
            gate.ObserveRelay(realtime.Snapshot());
            gate.ObserveSignalling(ConnectedDirectSignalling(6), 9);
            Require(!gate.TryCapture(out _) &&
                    gate.Snapshot.RelayDirectDataNegotiated &&
                    !gate.Snapshot.RelaySessionCutoverNegotiated,
                "A data-only Relay Welcome authorized Direct cutover.");

            Require(realtime.TryMarkConnected(10, welcome),
                "The complete Relay fixture could not restore both capabilities.");
            gate.ObserveRelay(realtime.Snapshot());
            gate.ObserveSignalling(ConnectedDirectSignalling(
                7,
                directData: true,
                sessionCutover: false), 10);
            Require(!gate.TryCapture(out _) &&
                    gate.Snapshot.SignallingDirectDataNegotiated &&
                    !gate.Snapshot.SignallingSessionCutoverNegotiated,
                "A data-only signalling Welcome authorized Direct cutover.");

            gate.ObserveSignalling(ConnectedDirectSignalling(
                8,
                directData: false,
                sessionCutover: true), 10);
            Require(!gate.TryCapture(out _),
                "A cutover-only signalling Welcome authorized Direct data.");
        }

        private static ProductionDirectSignalingSnapshot ConnectedDirectSignalling(
            long generation,
            bool directData = true,
            bool sessionCutover = true) => new()
        {
            State = ProductionDirectSignalingLaneState.Connected,
            Generation = generation,
            DirectDataV1Negotiated = directData,
            DirectSessionCutoverV1Negotiated = sessionCutover
        };

        private static byte[] Welcome(string[] capabilities)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "welcome",
                protocol_version = 1,
                heartbeat_seconds = 15,
                connection_id = Guid.NewGuid().ToString("D"),
                capabilities
            });
        }

        private static void ValidateExactRelayEnvelopeIngress()
        {
            var sessionId = Guid.NewGuid();
            var senderDeviceId = Guid.NewGuid();
            var recipientDeviceId = Guid.NewGuid();
            var firstRaw = BuildNonCanonicalEnvelope(
                sessionId,
                senderDeviceId,
                recipientDeviceId);
            var secondRaw = (byte[])firstRaw.Clone();
            var expectedDigest = SHA256.HashData(firstRaw);
            byte[] firstDigest;
            byte[] canonical;

            using (var first = ProductionInboundEnvelope.Parse(firstRaw))
            {
                Require(first.RawBytes.Span.SequenceEqual(firstRaw),
                    "Relay ingress did not retain the exact assembled frame bytes.");
                firstDigest = ProductionAgentRuntime.ComputeExactEnvelopeSha256(
                    first.RawBytes.Span);
                canonical = first.Envelope.ToByteArray();
                Require(!first.RawBytes.Span.SequenceEqual(canonical),
                    "The non-canonical wire fixture unexpectedly matched protobuf reserialization.");
            }
            Require(firstRaw.All(value => value == 0),
                "Relay ingress did not clear the owned raw bytes on disposal.");

            byte[] secondDigest;
            using (var second = ProductionInboundEnvelope.Parse(secondRaw))
            {
                secondDigest = ProductionAgentRuntime.ComputeExactEnvelopeSha256(
                    second.RawBytes.Span);
            }
            Require(expectedDigest.SequenceEqual(firstDigest) &&
                    expectedDigest.SequenceEqual(secondDigest),
                "An exact Relay retransmission did not retain a stable raw-envelope digest.");
            Require(!expectedDigest.SequenceEqual(SHA256.HashData(canonical)),
                "Input replay identity still matched a reserialized protobuf instead of the exact wire bytes.");
            Require(secondRaw.All(value => value == 0),
                "The duplicate Relay ingress did not clear its owned raw bytes on disposal.");

            var replayKeys = new HashSet<string>(StringComparer.Ordinal);
            var deliveries = 0;
            foreach (var digest in new[] { firstDigest, secondDigest })
            {
                if (replayKeys.Add(Convert.ToHexString(digest))) deliveries++;
                CryptographicOperations.ZeroMemory(digest);
            }
            Require(deliveries == 1,
                "Two exact Relay copies were not collapsed to one input delivery key.");
            CryptographicOperations.ZeroMemory(expectedDigest);
            CryptographicOperations.ZeroMemory(canonical);
        }

        private static byte[] BuildNonCanonicalEnvelope(
            Guid sessionId,
            Guid senderDeviceId,
            Guid recipientDeviceId)
        {
            using var stream = new MemoryStream();
            var output = new CodedOutputStream(stream);
            output.WriteTag(12, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(recipientDeviceId.ToByteArray()));
            output.WriteTag(99, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFromUtf8("unknown-wire-field"));
            output.WriteTag(4, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(senderDeviceId.ToByteArray()));
            output.WriteTag(11, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(new byte[32]));
            output.WriteTag(3, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(sessionId.ToByteArray()));
            output.WriteTag(10, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(new byte[12]));
            output.WriteTag(1, WireFormat.WireType.Varint);
            output.WriteUInt32(1);
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteEnum((int)MessageType.InputEvent);
            output.WriteTag(5, WireFormat.WireType.Varint);
            output.WriteEnum((int)Direction.AndroidToPc);
            output.WriteTag(6, WireFormat.WireType.Varint);
            output.WriteUInt64(7);
            output.WriteTag(8, WireFormat.WireType.Varint);
            output.WriteInt64(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            output.WriteTag(9, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFromUtf8("key-epoch-1"));
            output.Flush();
            return stream.ToArray();
        }

        private static void ValidateDirectRouteEligibilityPolicy()
        {
            foreach (var messageType in Enum.GetValues<MessageType>())
            {
                var pcToAndroid = ProductionDirectRoutePolicy.Evaluate(
                    messageType,
                    Direction.PcToAndroid);
                var androidToPc = ProductionDirectRoutePolicy.Evaluate(
                    messageType,
                    Direction.AndroidToPc);
                Require(pcToAndroid == (messageType == MessageType.OutputChunk
                            ? ProductionDirectRouteEligibility.DirectEligible
                            : ProductionDirectRouteEligibility.RelayOnly) &&
                        androidToPc == (messageType == MessageType.InputEvent
                            ? ProductionDirectRouteEligibility.DirectEligible
                            : ProductionDirectRouteEligibility.RelayOnly),
                    "The Direct route policy admitted a non-terminal-data Envelope: " +
                    messageType + ".");
            }

            Require(ProductionDirectRoutePolicy.Evaluate(
                        (MessageType)999,
                        Direction.PcToAndroid) == ProductionDirectRouteEligibility.RelayOnly &&
                    ProductionDirectRoutePolicy.Evaluate(
                        MessageType.OutputChunk,
                        (Direction)999) == ProductionDirectRouteEligibility.RelayOnly &&
                    ProductionDirectRoutePolicy.Evaluate(
                        MessageType.OutputChunk,
                        Direction.AndroidToPc) == ProductionDirectRouteEligibility.RelayOnly &&
                    ProductionDirectRoutePolicy.Evaluate(
                        MessageType.InputEvent,
                        Direction.PcToAndroid) == ProductionDirectRouteEligibility.RelayOnly,
                "An unknown or reversed route value escaped the Relay-only default.");

            var pc = Guid.NewGuid();
            var android = Guid.NewGuid();
            var outbound = new Envelope
            {
                MessageType = MessageType.OutputChunk,
                Direction = Direction.PcToAndroid,
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(pc)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(android))
            };
            Require(ProductionDirectRoutePolicy.Evaluate(outbound, out var outboundAndroid) ==
                    ProductionDirectRouteEligibility.DirectEligible &&
                    outboundAndroid == android,
                "PC-to-Android routing did not key the peer by recipient Android device ID.");

            var inbound = new Envelope
            {
                MessageType = MessageType.InputEvent,
                Direction = Direction.AndroidToPc,
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(android)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(pc))
            };
            Require(ProductionDirectRoutePolicy.Evaluate(inbound, out var inboundAndroid) ==
                    ProductionDirectRouteEligibility.DirectEligible &&
                    inboundAndroid == android,
                "Android-to-PC routing did not key the peer by sender Android device ID.");

            outbound.RecipientDeviceId = ByteString.CopyFrom(new byte[16]);
            Require(ProductionDirectRoutePolicy.Evaluate(outbound, out var invalidAndroid) ==
                    ProductionDirectRouteEligibility.RelayOnly && invalidAndroid == Guid.Empty,
                "An empty Android device ID was admitted to Direct routing.");
        }

        private static void ValidateDirectCutoverCodec()
        {
            ValidateCrossPlatformDirectCutoverVector();

            var connectionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
            var barrierId = Guid.Parse("10000000-0000-0000-0000-000000000001");
            var sessionOne = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var sessionTwo = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var binding = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            var proposal = ProductionDirectCutoverCodec.CreateProposal(
                connectionId,
                5,
                Direction.PcToAndroid,
                barrierId,
                binding,
                new[]
                {
                    new ProductionDirectSessionCutoverBarrier
                    {
                        SessionId = sessionTwo,
                        FirstDirectSequence = 9
                    },
                    new ProductionDirectSessionCutoverBarrier
                    {
                        SessionId = sessionOne,
                        FirstDirectSequence = 7
                    }
                },
                250);
            const string expectedWire =
                "0A101000000000000000000000000000000112200102030405060708090A0B0C0D0E0F" +
                "101112131415161718191A1B1C1D1E1F201A140A100000000000000000000000000000" +
                "000110071A140A1000000000000000000000000000000002100920FA01";
            const string expectedDigest =
                "901CB6CE674E3FFA476EA30398DAD8CA321505DC994001D308775E41B1FABBEE";
            Require(Convert.ToHexString(proposal.WireBytes) == expectedWire &&
                    Convert.ToHexString(proposal.BarrierSha256) == expectedDigest &&
                    proposal.SessionBarriers.Select(value => value.SessionId)
                        .SequenceEqual(new[] { sessionOne, sessionTwo }),
                "The canonical Direct cutover vector changed or retained caller ordering.");
            Require(ProductionDirectCutoverCodec.TryParseProposal(
                        proposal.WireBytes,
                        connectionId,
                        5,
                        Direction.PcToAndroid,
                        out var parsed) &&
                    parsed.BarrierId == barrierId &&
                    parsed.Direction == Direction.PcToAndroid &&
                    parsed.DirectPathBinding.SequenceEqual(binding) &&
                    parsed.BarrierSha256.SequenceEqual(proposal.BarrierSha256) &&
                    parsed.WireBytes.SequenceEqual(proposal.WireBytes),
                "A canonical Direct cutover proposal did not round-trip byte-identically.");

            var unknownField = proposal.WireBytes.Concat(new byte[] { 0xf8, 0x06, 0x01 }).ToArray();
            Require(!ProductionDirectCutoverCodec.TryParseProposal(
                    unknownField,
                    connectionId,
                    5,
                    Direction.PcToAndroid,
                    out _),
                "A Direct cutover proposal with an unknown field was accepted.");

            var reversed = DirectCutoverProposalV1.Parser.ParseFrom(proposal.WireBytes);
            var first = reversed.SessionBarriers[0];
            reversed.SessionBarriers[0] = reversed.SessionBarriers[1];
            reversed.SessionBarriers[1] = first;
            Require(!ProductionDirectCutoverCodec.TryParseProposal(
                    reversed.ToByteArray(),
                    connectionId,
                    5,
                    Direction.PcToAndroid,
                    out _),
                "A non-canonical Direct cutover session order was accepted.");

            var conflictingDirection = DirectCutoverProposalV1.Parser.ParseFrom(
                proposal.WireBytes);
            conflictingDirection.SessionBarriers[0].AndroidToPcFirstDirectSeq = 7;
            Require(!ProductionDirectCutoverCodec.TryParseProposal(
                        conflictingDirection.ToByteArray(),
                        connectionId,
                        5,
                        Direction.PcToAndroid,
                        out _) &&
                    !ProductionDirectCutoverCodec.TryParseProposal(
                        proposal.WireBytes,
                        connectionId,
                        5,
                        Direction.AndroidToPc,
                        out _),
                "A cutover proposal crossed or mixed its authenticated direction.");

            var duplicateSession = DirectCutoverProposalV1.Parser.ParseFrom(
                proposal.WireBytes);
            duplicateSession.SessionBarriers[1].SessionId =
                duplicateSession.SessionBarriers[0].SessionId;
            var zeroSequence = DirectCutoverProposalV1.Parser.ParseFrom(proposal.WireBytes);
            zeroSequence.SessionBarriers[0].PcToAndroidFirstDirectSeq = 0;
            var excessiveGrace = DirectCutoverProposalV1.Parser.ParseFrom(proposal.WireBytes);
            excessiveGrace.DuplicateGraceMs = 5_001;
            Require(!ProductionDirectCutoverCodec.TryParseProposal(
                        duplicateSession.ToByteArray(), connectionId, 5,
                        Direction.PcToAndroid, out _) &&
                    !ProductionDirectCutoverCodec.TryParseProposal(
                        zeroSequence.ToByteArray(), connectionId, 5,
                        Direction.PcToAndroid, out _) &&
                    !ProductionDirectCutoverCodec.TryParseProposal(
                        excessiveGrace.ToByteArray(), connectionId, 5,
                        Direction.PcToAndroid, out _) &&
                    !ProductionDirectCutoverCodec.TryParseProposal(
                        new byte[ProductionDirectCutoverCodec.MaximumProposalBytes + 1],
                        connectionId, 5, Direction.PcToAndroid, out _),
                "A bounded Direct cutover field limit was not enforced.");

            RequireThrows<ArgumentException>(
                () => ProductionDirectCutoverCodec.CreateProposal(
                    connectionId,
                    5,
                    Direction.PcToAndroid,
                    barrierId,
                    binding,
                    new[]
                    {
                        new ProductionDirectSessionCutoverBarrier
                        {
                            SessionId = sessionOne,
                            FirstDirectSequence = 7
                        },
                        new ProductionDirectSessionCutoverBarrier
                        {
                            SessionId = sessionOne,
                            FirstDirectSequence = 8
                        }
                    },
                    250),
                "The cutover builder admitted a duplicate session barrier.");

            var proposalSignal = ProductionDirectCutoverSignalCodec.CreateProposal(proposal);
            Require(ProductionDirectCutoverSignalCodec.TryParseProposal(
                        proposalSignal, connectionId, 5, Direction.PcToAndroid,
                        out var signalProposal) &&
                    signalProposal.WireBytes.SequenceEqual(proposal.WireBytes) &&
                    !ProductionDirectCutoverSignalCodec.TryParseProposal(
                        proposalSignal.Concat(new byte[] { 0xf8, 0x06, 0x01 }).ToArray(),
                        connectionId, 5, Direction.PcToAndroid, out _),
                "The encrypted cutover proposal wrapper was not canonical and exact.");

            var acceptedSignal = ProductionDirectCutoverSignalCodec.CreateAccept(
                barrierId, proposal.BarrierSha256, accepted: true, string.Empty);
            Require(ProductionDirectCutoverSignalCodec.TryParseAccept(
                        acceptedSignal, out var accepted) && accepted.Accepted &&
                    accepted.BarrierId == barrierId &&
                    accepted.BarrierSha256.SequenceEqual(proposal.BarrierSha256),
                "A canonical Direct cutover accept did not round-trip.");
            var rejectedSignal = ProductionDirectCutoverSignalCodec.CreateAccept(
                barrierId, proposal.BarrierSha256, accepted: false,
                "DIRECT_PREFIX_TIMEOUT");
            Require(ProductionDirectCutoverSignalCodec.TryParseAccept(
                        rejectedSignal, out var rejected) && !rejected.Accepted &&
                    rejected.ReasonCode == "DIRECT_PREFIX_TIMEOUT" &&
                    !ProductionDirectCutoverSignalCodec.TryParseAccept(
                        rejectedSignal.Concat(new byte[] { 0xf8, 0x06, 0x01 }).ToArray(), out _),
                "A rejected or non-canonical Direct accept was mishandled.");

            var commitSignal = ProductionDirectCutoverSignalCodec.CreateCommit(
                barrierId, proposal.BarrierSha256);
            Require(ProductionDirectCutoverSignalCodec.TryParseCommit(
                        commitSignal, out var commit) && commit.BarrierId == barrierId &&
                    commit.BarrierSha256.SequenceEqual(proposal.BarrierSha256),
                "A canonical Direct cutover commit did not round-trip.");
            var fallbackSignal = ProductionDirectCutoverSignalCodec.CreateFallback(
                barrierId, "DIRECT_FAILURE", 17);
            Require(ProductionDirectCutoverSignalCodec.TryParseFallback(
                        fallbackSignal, out var fallbackSignalValue) &&
                    fallbackSignalValue.BarrierId == barrierId &&
                    fallbackSignalValue.ReasonCode == "DIRECT_FAILURE" &&
                    fallbackSignalValue.LastReceivedDirectFrameSequence == 17,
                "A canonical Direct fallback did not round-trip.");
            RequireThrows<ArgumentException>(
                () => ProductionDirectCutoverSignalCodec.CreateAccept(
                    barrierId, proposal.BarrierSha256, accepted: true, "NOT_EMPTY"),
                "An accepted cutover signal retained a rejection reason.");
        }

        private static void ValidateCrossPlatformDirectCutoverVector()
        {
            var connectionId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var barrierId = Guid.Parse("fedcba98-7654-3210-ffff-eeee00001111");
            var binding = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            var proposal = ProductionDirectCutoverCodec.CreateProposal(
                connectionId,
                0x0102030405060708UL,
                Direction.AndroidToPc,
                barrierId,
                binding,
                new[]
                {
                    new ProductionDirectSessionCutoverBarrier
                    {
                        SessionId = Guid.Parse("ffffffff-0000-0000-0000-000000000001"),
                        FirstDirectSequence = 300
                    },
                    new ProductionDirectSessionCutoverBarrier
                    {
                        SessionId = Guid.Parse("00000000-ffff-eeee-dddd-ccccbbbb0002"),
                        FirstDirectSequence = 1
                    }
                },
                1_250);
            const string expectedWire =
                "0A10FEDCBA9876543210FFFFEEEE0000111112200102030405060708090A0B0C0D0E0F10" +
                "1112131415161718191A1B1C1D1E1F201A140A1000000000FFFFEEEEDDDDCCCCBBBB0002" +
                "18011A150A10FFFFFFFF00000000000000000000000118AC0220E209";
            const string expectedDigest =
                "8322FE77ACA31E9DF68620A041E9B596A84F00357A290CC88A1493A5E63303EC";
            Require(Convert.ToHexString(proposal.WireBytes) == expectedWire &&
                    Convert.ToHexString(proposal.BarrierSha256) == expectedDigest,
                "The shared Android/Windows Direct cutover vector changed.");
            Require(ProductionDirectCutoverCodec.TryParseProposal(
                        proposal.WireBytes,
                        connectionId,
                        0x0102030405060708UL,
                        Direction.AndroidToPc,
                        out var parsed) &&
                    parsed.BarrierSha256.SequenceEqual(proposal.BarrierSha256) &&
                    parsed.WireBytes.SequenceEqual(proposal.WireBytes),
                "The shared Android/Windows Direct cutover vector did not round-trip exactly.");
        }

        private static async Task ValidatePerAndroidDeviceRouteActorsAsync()
        {
            using var registry = new ProductionDirectPeerRouteRegistry();
            registry.ObserveRelay(connected: true, generation: 17);
            var androidOne = Guid.NewGuid();
            var androidTwo = Guid.NewGuid();

            var parallel = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() =>
                {
                    Require(registry.TryGetOrCreate(androidOne, out var value),
                        "The peer registry rejected a valid Android device ID.");
                    return value;
                }))
                .ToArray();
            var actors = await Task.WhenAll(parallel).ConfigureAwait(false);
            Require(registry.TryGetOrCreate(androidTwo, out var actorTwo),
                "The peer registry rejected the second Android device ID.");
            Require(actors.All(value => ReferenceEquals(value, actors[0])) &&
                    !ReferenceEquals(actors[0], actorTwo) &&
                    !registry.TryGetOrCreate(Guid.Empty, out _),
                "The route registry did not maintain one actor per Android device ID.");
            var actorOne = actors[0];

            var firstFence = RouteFence(1);
            Require(actorOne.TryBeginDirect(
                        firstFence,
                        ProductionDirectAuthenticatedPathKind.LocalAreaNetwork) &&
                    actorOne.Snapshot.Carrier == ProductionDirectPeerCarrier.NegotiatingDirect &&
                    actorOne.Snapshot.RelayGeneration == 17 && actorOne.Snapshot.RelayConnected,
                "A valid peer did not enter Direct negotiation beside the current Relay.");
            Require(actorOne.TryMarkDirectionActive(
                        firstFence,
                        ProductionDirectPeerDirection.PcToAndroid) &&
                    actorOne.Snapshot.Carrier == ProductionDirectPeerCarrier.NegotiatingDirect,
                "A single active direction incorrectly reported a Direct carrier.");
            Require(actorOne.TryMarkDirectionActive(
                        firstFence,
                        ProductionDirectPeerDirection.AndroidToPc) &&
                    actorOne.Snapshot.Carrier == ProductionDirectPeerCarrier.LocalAreaNetworkDirect,
                "Two active LAN directions did not aggregate to LAN Direct.");

            var secondFence = RouteFence(2);
            Require(actorTwo.TryBeginDirect(
                        secondFence,
                        ProductionDirectAuthenticatedPathKind.NatTraversal) &&
                    actorTwo.TryMarkDirectionActive(
                        secondFence,
                        ProductionDirectPeerDirection.AndroidToPc) &&
                    actorTwo.TryMarkDirectionActive(
                        secondFence,
                        ProductionDirectPeerDirection.PcToAndroid) &&
                    actorTwo.Snapshot.Carrier == ProductionDirectPeerCarrier.NatTraversalDirect,
                "Two active NAT directions did not aggregate to NAT Direct.");

            Require(!actorOne.TryBeginDirect(
                        RouteFence(0),
                        ProductionDirectAuthenticatedPathKind.LocalAreaNetwork) &&
                    !actorOne.TryBeginDirect(
                        RouteFence(3),
                        (ProductionDirectAuthenticatedPathKind)999) &&
                    !actorOne.TryMarkDirectionActive(firstFence, (ProductionDirectPeerDirection)999),
                "An unknown or invalid Direct lifecycle value did not fail closed.");

            var relayBeforeFault = actorOne.Snapshot.RelayGeneration;
            Require(registry.TryRetirePeer(androidOne, firstFence, "QUIC_CLOSED") &&
                    actorOne.Snapshot.Carrier == ProductionDirectPeerCarrier.Relay &&
                    actorOne.Snapshot.RelayGeneration == relayBeforeFault &&
                    actorOne.Snapshot.RelayConnected &&
                    actorOne.Snapshot.LastDirectFault == "QUIC_CLOSED" &&
                    actorTwo.Snapshot.Carrier == ProductionDirectPeerCarrier.NatTraversalDirect &&
                    actorTwo.Snapshot.RelayGeneration == relayBeforeFault,
                "A Direct fault changed Relay generation or retired another Android peer.");
            Require(!registry.TryRetirePeer(androidOne, firstFence, "STALE") &&
                    actorTwo.Snapshot.Carrier == ProductionDirectPeerCarrier.NatTraversalDirect,
                "A stale Direct fault mutated current peer state.");

            var successor = RouteFence(4);
            Require(actorOne.TryBeginDirect(
                        successor,
                        ProductionDirectAuthenticatedPathKind.NatTraversal) &&
                    !actorOne.TryBeginDirect(
                        firstFence,
                        ProductionDirectAuthenticatedPathKind.LocalAreaNetwork) &&
                    actorOne.Snapshot.Carrier == ProductionDirectPeerCarrier.NegotiatingDirect,
                "A stale fence replaced a newer peer negotiation.");

            registry.ObserveRelay(connected: false, generation: 18);
            Require(actorOne.Snapshot.RelayGeneration == 18 && !actorOne.Snapshot.RelayConnected &&
                    actorTwo.Snapshot.RelayGeneration == 18 && !actorTwo.Snapshot.RelayConnected &&
                    actorOne.Snapshot.Carrier == ProductionDirectPeerCarrier.NegotiatingDirect &&
                    actorTwo.Snapshot.Carrier == ProductionDirectPeerCarrier.NatTraversalDirect,
                "Relay observation changed an independent Direct lifecycle or generation.");
            Require(registry.RetireAllDirect("NETWORK_CHANGED") == 2 &&
                    actorOne.Snapshot.Carrier == ProductionDirectPeerCarrier.Relay &&
                    actorTwo.Snapshot.Carrier == ProductionDirectPeerCarrier.Relay &&
                    actorOne.Snapshot.RelayGeneration == 18 && !actorOne.Snapshot.RelayConnected &&
                    actorTwo.Snapshot.RelayGeneration == 18 && !actorTwo.Snapshot.RelayConnected &&
                    actorOne.Snapshot.LastDirectFault == "NETWORK_CHANGED" &&
                    actorTwo.Snapshot.LastDirectFault == "NETWORK_CHANGED" &&
                    registry.RetireAllDirect("DUPLICATE") == 0,
                "Network retirement changed Relay state or did not retire every exact Direct fence once.");
            Require(registry.Remove(androidOne) && !registry.TryGet(androidOne, out _) &&
                    actorOne.Snapshot.Disposed &&
                    actorTwo.Snapshot.Carrier == ProductionDirectPeerCarrier.Relay,
                "Removing one Android peer affected another peer actor.");
            Require(ProductionAgentRuntime.FormatSessionCarrier(
                        ProductionDirectPeerCarrier.NegotiatingDirect) ==
                    ProductionSessionCarrier.NegotiatingDirect &&
                    ProductionAgentRuntime.FormatSessionCarrier(
                        ProductionDirectPeerCarrier.LocalAreaNetworkDirect) ==
                    ProductionSessionCarrier.LanDirect &&
                    ProductionAgentRuntime.FormatSessionCarrier(
                        ProductionDirectPeerCarrier.NatTraversalDirect) ==
                    ProductionSessionCarrier.NatDirect &&
                    ProductionAgentRuntime.FormatSessionCarrier(
                        (ProductionDirectPeerCarrier)999) == ProductionSessionCarrier.Relay,
                "The Agent control status carrier mapping did not fail closed.");
        }

        private static ProductionDirectPeerPathFence RouteFence(ulong epoch) => new()
        {
            PathEpoch = epoch,
            ConnectionId = epoch == 0 ? Guid.Empty : Guid.Parse(
                "10000000-0000-0000-0000-" + epoch.ToString("x12")),
            AttemptId = epoch == 0 ? Guid.Empty : Guid.Parse(
                "20000000-0000-0000-0000-" + epoch.ToString("x12"))
        };

        private static void ValidateEpochRateAndFailureCooldown()
        {
            var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var supervisor = new ProductionDirectTransportSupervisor(() => now);
            supervisor.ObserveRelayConnected(true);
            var peer = Guid.NewGuid();
            for (ulong epoch = 1; epoch <= 3; epoch++)
            {
                Require(supervisor.TryBeginAttempt(
                        peer, Guid.NewGuid(), epoch, out var attempt, out _),
                    "A valid monotonic epoch was rejected.");
                Require(supervisor.TryFallbackAttempt(attempt, "PROBE_TIMEOUT"),
                    "A valid attempt fallback was rejected.");
                now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
            }
            Require(!supervisor.TryBeginAttempt(
                    peer, Guid.NewGuid(), 4, out _, out var cooldown) &&
                    cooldown == "PATH_COOLDOWN",
                "Three direct failures did not enter the bounded cooldown.");
            now = now.Add(ProductionDirectTransportSupervisor.FailureCooldown);
            Require(supervisor.TryBeginAttempt(
                    peer, Guid.NewGuid(), 4, out var fourthAttempt, out _),
                "The direct-path cooldown did not expire.");
            Require(supervisor.TryFallbackAttempt(fourthAttempt, "PROBE_TIMEOUT"),
                "The post-cooldown attempt could not fall back.");
            now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
            Require(!supervisor.TryBeginAttempt(
                    peer, Guid.NewGuid(), 4, out _, out var stale) &&
                    stale == "STALE_PATH_EPOCH",
                "A stale path epoch was accepted after fallback.");
            Require(ProductionDirectTransportSupervisor.ValidReasonCode("NETWORK_CHANGED") &&
                    !ProductionDirectTransportSupervisor.ValidReasonCode("network changed") &&
                    !ProductionDirectTransportSupervisor.ValidReasonCode(new string('A', 65)),
                "Fallback reason-code bounds are not fail closed.");
        }

        private static void ValidatePeerIsolationAndStaleGenerationCallbacks()
        {
            var now = new DateTime(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc);
            var supervisor = new ProductionDirectTransportSupervisor(() => now);
            supervisor.ObserveRelayConnected(true);
            var firstPeer = Guid.NewGuid();
            var secondPeer = Guid.NewGuid();
            Require(supervisor.TryBeginAttempt(
                    firstPeer, Guid.NewGuid(), 100, out var firstToken, out _),
                "The first peer could not start an attempt.");
            Require(supervisor.TryFallbackAttempt(firstToken, "PROBE_TIMEOUT"),
                "The first peer could not return to Relay.");
            now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
            Require(supervisor.TryBeginAttempt(
                    secondPeer, Guid.NewGuid(), 1, out var secondToken, out _),
                "One peer's high epoch incorrectly rejected another peer's epoch.");
            var secondBeforeStaleCallbacks = supervisor.Snapshot();
            RequireThrows<InvalidOperationException>(
                () => supervisor.MarkGatheringComplete(firstToken),
                "A stale attempt callback advanced a newer peer attempt.");
            Require(!supervisor.TryFallbackAttempt(firstToken, "STALE_CALLBACK"),
                "A stale attempt callback retired a newer peer attempt.");
            var secondAfterStaleCallbacks = supervisor.Snapshot();
            Require(secondAfterStaleCallbacks.State == secondBeforeStaleCallbacks.State &&
                    secondAfterStaleCallbacks.PeerDeviceId == secondPeer &&
                    secondAfterStaleCallbacks.ConnectionId == secondBeforeStaleCallbacks.ConnectionId &&
                    secondAfterStaleCallbacks.PathEpoch == 1 &&
                    secondAfterStaleCallbacks.AttemptGeneration == secondToken.Generation,
                "A stale attempt callback mutated the current generation identity.");
            Require(supervisor.TryFallbackAttempt(secondToken, "PROBE_TIMEOUT"),
                "The second peer could not return to Relay.");

            for (ulong epoch = 101; epoch <= 102; epoch++)
            {
                now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
                Require(supervisor.TryBeginAttempt(
                        firstPeer, Guid.NewGuid(), epoch, out var token, out _),
                    "The first peer could not accumulate its own failure policy.");
                Require(supervisor.TryFallbackAttempt(token, "PROBE_TIMEOUT"),
                    "The first peer failure was not recorded.");
            }
            now = now.Add(ProductionDirectTransportSupervisor.MinimumAttemptInterval);
            Require(!supervisor.TryBeginAttempt(
                    firstPeer, Guid.NewGuid(), 104, out _, out var firstCooldown) &&
                    firstCooldown == "PATH_COOLDOWN",
                "Three failures did not cool down the affected peer.");
            Require(supervisor.TryBeginAttempt(
                    secondPeer, Guid.NewGuid(), 2, out var unaffectedToken, out _),
                "One peer's cooldown incorrectly blocked another peer.");
            Require(supervisor.TryFallbackAttempt(unaffectedToken, "PROBE_TIMEOUT"),
                "The unaffected peer could not return to Relay.");
        }

        private static void ValidateCandidateFilteringAndSocketLease()
        {
            Require(!ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Loopback) &&
                    !ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.IPv6Loopback) &&
                    !ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Parse("169.254.1.2")) &&
                    !ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Parse("224.0.0.1")) &&
                    ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Parse("192.168.1.10")) &&
                    ProductionDirectCandidateCollector.IsAllowedHostAddress(IPAddress.Parse("2001:db8::1")),
                "Host candidate filtering accepted an unsafe address or rejected a routable address.");

            var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(value => value.OperationalStatus == OperationalStatus.Up &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(value => value.GetIPProperties().UnicastAddresses)
                .Select(value => value.Address)
                .Where(ProductionDirectCandidateCollector.IsAllowedHostAddress)
                .Take(1)
                .ToArray();
            if (localAddresses.Length == 0) return;
            var leases = ProductionDirectCandidateCollector.BindHostCandidates(
                localAddresses,
                DateTime.UtcNow.AddMinutes(1));
            try
            {
                Require(leases.Count == 1, "An active routable interface did not produce a host candidate.");
                var lease = leases[0];
                var endpoint = (IPEndPoint)lease.Socket.LocalEndPoint;
                Require(lease.Candidate.Kind == DirectCandidateKindV1.Host &&
                        lease.Candidate.Carrier == DirectCarrierV1.UdpQuic &&
                        lease.Candidate.Port == endpoint.Port &&
                        lease.Candidate.IpAddress.Span.SequenceEqual(endpoint.Address.GetAddressBytes()),
                    "The retained UDP socket does not match its advertised candidate.");
            }
            finally
            {
                foreach (var lease in leases) lease.Dispose();
            }
        }

        private static void ValidateStunTransactionAndXorMappedAddress()
        {
            var transaction = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
            var request = ProductionStunClient.CreateBindingRequest(transaction);
            Require(request.Length == 20 && BinaryPrimitives.ReadUInt16BigEndian(request) == 1 &&
                    BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4, 4)) == StunMagicCookie &&
                    request.AsSpan(8).SequenceEqual(transaction),
                "RFC 8489 Binding request framing is invalid.");

            var expected = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 54321);
            var response = CreateStunIPv4Success(transaction, expected);
            var parsed = ProductionStunClient.ParseBindingSuccess(response, transaction);
            Require(parsed.Equals(expected), "XOR-MAPPED-ADDRESS was not decoded byte-exactly.");

            var spoofed = (byte[])transaction.Clone();
            spoofed[0] ^= 0xff;
            RequireThrows<InvalidOperationException>(
                () => ProductionStunClient.ParseBindingSuccess(response, spoofed),
                "A STUN response with the wrong transaction ID was accepted.");
            response[^1] ^= 0x01;
            var changed = ProductionStunClient.ParseBindingSuccess(response, transaction);
            Require(!changed.Equals(expected),
                "The STUN parser ignored a changed authenticated address field.");
        }

        private static byte[] CreateStunIPv4Success(byte[] transaction, IPEndPoint endpoint)
        {
            var response = new byte[32];
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), 0x0101);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 12);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4, 4), StunMagicCookie);
            transaction.CopyTo(response, 8);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20, 2), 0x0020);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22, 2), 8);
            response[25] = 0x01;
            BinaryPrimitives.WriteUInt16BigEndian(
                response.AsSpan(26, 2),
                (ushort)(endpoint.Port ^ (StunMagicCookie >> 16)));
            var address = endpoint.Address.GetAddressBytes();
            Span<byte> cookie = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(cookie, StunMagicCookie);
            for (var index = 0; index < 4; index++)
                response[28 + index] = (byte)(address[index] ^ cookie[index]);
            return response;
        }

        private static void ValidateTicketSignalAndProbeCryptography()
        {
            var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var local = Guid.NewGuid();
            var peer = Guid.NewGuid();
            var connection = Guid.NewGuid();
            var keyId = Encoding.ASCII.GetBytes("direct-key-v1");
            using var server = DeviceIdentity.Generate();
            var ticket = new DirectPathTicketV1
            {
                FormatVersion = 1,
                TicketId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                AccountId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                InitiatorDeviceId = ByteString.CopyFrom(GuidBytes(local)),
                ResponderDeviceId = ByteString.CopyFrom(GuidBytes(peer)),
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                PathEpoch = 7,
                IssuedAtMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                ExpiresAtMs = new DateTimeOffset(now.AddMinutes(20)).ToUnixTimeMilliseconds(),
                ServerNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                ServerSigningKeyId = ByteString.CopyFrom(keyId)
            };
            ticket.AllowedCarriers.Add(DirectCarrierV1.UdpQuic);
            var claims = ticket.ToByteArray();
            var ticketDomain = Encoding.ASCII.GetBytes("TERMLINK-DIRECT-TICKET-V1\n");
            var signed = ticketDomain.Concat(claims).ToArray();
            ticket.ServerSignature = ByteString.CopyFrom(server.Sign(signed));
            ProductionDirectTransportCrypto.ValidateTicket(
                ticket,
                server.SigningPublicKey,
                keyId,
                local,
                peer,
                now);
            var overlongTicket = ticket.Clone();
            overlongTicket.ExpiresAtMs++;
            RequireThrows<InvalidDataException>(
                () => ProductionDirectTransportCrypto.ValidateTicket(
                    overlongTicket, server.SigningPublicKey, keyId, local, peer, now),
                "A direct ticket longer than 20 minutes was accepted.");
            var tamperedTicket = ticket.Clone();
            tamperedTicket.PathEpoch++;
            RequireThrows<CryptographicException>(
                () => ProductionDirectTransportCrypto.ValidateTicket(
                    tamperedTicket,
                    server.SigningPublicKey,
                    keyId,
                    local,
                    peer,
                    now),
                "A modified direct ticket retained authority.");

            var pairwise = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            var signal = new TransportSignalEnvelopeV1
            {
                ProtocolVersion = 1,
                SignalType = TransportSignalTypeV1.PathOffer,
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(local)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(peer)),
                PathEpoch = 7,
                SignalSeq = 1,
                SignalAck = 0,
                SentAtMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                KeyId = ByteString.CopyFromUtf8("signal-v1")
            };
            var plaintext = Encoding.UTF8.GetBytes("opaque-candidate-payload");
            var encrypted = ProductionDirectTransportCrypto.EncryptSignal(
                pairwise,
                signal,
                plaintext);
            var decrypted = ProductionDirectTransportCrypto.DecryptSignal(pairwise, encrypted);
            Require(decrypted.SequenceEqual(plaintext),
                "Independent transport signalling AEAD did not round-trip.");
            var changedSignal = encrypted.Clone();
            var changedCipher = changedSignal.Ciphertext.ToByteArray();
            changedCipher[0] ^= 1;
            changedSignal.Ciphertext = ByteString.CopyFrom(changedCipher);
            RequireThrows<CryptographicException>(
                () => ProductionDirectTransportCrypto.DecryptSignal(pairwise, changedSignal),
                "Modified transport signal ciphertext was accepted.");

            var probe = new DirectConnectivityDatagramV1
            {
                ProtocolVersion = 1,
                Kind = DirectConnectivityDatagramKindV1.Probe,
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                PathEpoch = 7,
                TicketId = ticket.TicketId,
                NominationId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                Pair = new DirectCandidatePairV1
                {
                    InitiatorCandidateId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                    ResponderCandidateId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16))
                },
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(local)),
                ProbeNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                SentAtMs = new DateTimeOffset(now).ToUnixTimeMilliseconds()
            };
            var packet = ProductionDirectTransportCrypto.CreateProbePacket(pairwise, probe, peer);
            var verified = ProductionDirectTransportCrypto.ValidateProbePacket(
                pairwise,
                packet,
                local,
                peer,
                connection,
                7,
                now);
            Require(verified.Kind == DirectConnectivityDatagramKindV1.Probe &&
                    verified.AntiAmplificationPadding.Length == 16 &&
                    verified.AuthTag.Length == 16 &&
                    DirectConnectivityDatagramV1.AntiAmplificationPaddingFieldNumber == 12 &&
                    DirectConnectivityDatagramV1.AuthTagFieldNumber == 13,
                "Authenticated UDP probe fields 12/13 do not match the wire contract.");

            var ack = verified.Clone();
            ack.Kind = DirectConnectivityDatagramKindV1.ProbeAck;
            ack.SenderDeviceId = ByteString.CopyFrom(GuidBytes(peer));
            ack.ResponseNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16));
            ack.AntiAmplificationPadding = ByteString.Empty;
            ack.AuthTag = ByteString.Empty;
            var ackPacket = ProductionDirectTransportCrypto.CreateProbePacket(pairwise, ack, local);
            var verifiedAck = ProductionDirectTransportCrypto.ValidateProbePacket(
                pairwise,
                ackPacket,
                peer,
                local,
                connection,
                7,
                now);
            Require(verifiedAck.Kind == DirectConnectivityDatagramKindV1.ProbeAck &&
                    verifiedAck.ResponseNonce.Length == 16 &&
                    verifiedAck.AntiAmplificationPadding.Length == 0 &&
                    ackPacket.Length <= packet.Length,
                "PROBE_ACK violated the authenticated anti-amplification contract.");
            var paddedAck = ack.Clone();
            paddedAck.AntiAmplificationPadding = ByteString.CopyFrom(new byte[16]);
            RequireThrows<InvalidDataException>(
                () => ProductionDirectTransportCrypto.CreateProbePacket(pairwise, paddedAck, local),
                "A padded PROBE_ACK was accepted.");
            var extremeTimestampProbe = probe.Clone();
            extremeTimestampProbe.SentAtMs = long.MaxValue;
            var extremeTimestampPacket = ProductionDirectTransportCrypto.CreateProbePacket(
                pairwise,
                extremeTimestampProbe,
                peer);
            RequireThrows<InvalidDataException>(
                () => ProductionDirectTransportCrypto.ValidateProbePacket(
                    pairwise,
                    extremeTimestampPacket,
                    local,
                    peer,
                    connection,
                    7,
                    now),
                "An extreme authenticated probe timestamp bypassed the freshness window.");

            packet[^1] ^= 1;
            RequireThrows<CryptographicException>(
                () => ProductionDirectTransportCrypto.ValidateProbePacket(
                    pairwise,
                    packet,
                    local,
                    peer,
                    connection,
                    7,
                    now),
                "Modified UDP connectivity probe was accepted.");
        }

        private static void ValidateCanonicalTicketCrossLanguageVector()
        {
            var vectorPath = FindRepositoryFile(
                "protocol", "test-vectors", "direct-transport-ticket-v1.json");
            using var document = JsonDocument.Parse(File.ReadAllBytes(vectorPath));
            var root = document.RootElement;
            var publicKey = DecodeBase64Url(
                root.GetProperty("server_public_key_base64url").GetString());
            var ticketWire = DecodeBase64Url(
                root.GetProperty("ticket_protobuf_base64url").GetString());
            var expectedDigest = root.GetProperty("ticket_sha256").GetString();
            Require(string.Equals(
                    Convert.ToHexString(SHA256.HashData(ticketWire)).ToLowerInvariant(),
                    expectedDigest,
                    StringComparison.Ordinal),
                "The canonical direct-ticket vector digest changed.");
            var ticket = DirectPathTicketV1.Parser.ParseFrom(ticketWire);
            Require(ticketWire.SequenceEqual(ticket.ToByteArray()),
                "The server direct ticket is not canonical generated-protobuf wire.");
            var initiator = Guid.Parse(root.GetProperty("initiator_device_id").GetString());
            var responder = Guid.Parse(root.GetProperty("responder_device_id").GetString());
            var keyId = Encoding.UTF8.GetBytes(
                root.GetProperty("server_signing_key_id").GetString());
            var now = DateTimeOffset.FromUnixTimeMilliseconds(
                root.GetProperty("issued_at_ms").GetInt64() + 1_000).UtcDateTime;
            ProductionDirectTransportCrypto.ValidateTicket(
                ticket,
                publicKey,
                keyId,
                initiator,
                responder,
                now);
        }

        private static string FindRepositoryFile(params string[] segments)
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var current = new DirectoryInfo(Path.GetFullPath(start));
                while (current != null)
                {
                    var parts = new string[segments.Length + 1];
                    parts[0] = current.FullName;
                    Array.Copy(segments, 0, parts, 1, segments.Length);
                    var candidate = Path.Combine(parts);
                    if (File.Exists(candidate)) return candidate;
                    current = current.Parent;
                }
            }
            throw new FileNotFoundException(
                "The canonical direct-transport ticket vector was not found.");
        }

        private static byte[] DecodeBase64Url(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('='))
                throw new InvalidDataException("A canonical unpadded base64url value is required.");
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return Convert.FromBase64String(padded);
        }

        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

        private static async Task ValidateBidirectionalUdpProbeAsync()
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(value => value.OperationalStatus == OperationalStatus.Up &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    value.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(value => value.GetIPProperties().UnicastAddresses)
                .Select(value => value.Address)
                .FirstOrDefault(ProductionDirectCandidateCollector.IsAllowedHostAddress);
            if (address == null) return;
            var firstSocket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            var secondSocket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                firstSocket.DualMode = false;
                secondSocket.DualMode = false;
            }
            firstSocket.Bind(new IPEndPoint(address, 0));
            secondSocket.Bind(new IPEndPoint(address, 0));
            using var first = new ProductionDirectUdpCandidateLease(
                firstSocket,
                CandidateFor((IPEndPoint)firstSocket.LocalEndPoint));
            using var second = new ProductionDirectUdpCandidateLease(
                secondSocket,
                CandidateFor((IPEndPoint)secondSocket.LocalEndPoint));
            var retainedEndpoint = (IPEndPoint)first.Socket.LocalEndPoint;
            using var stunSocket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                stunSocket.DualMode = false;
            stunSocket.Bind(new IPEndPoint(address, 0));
            using var stunTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var mappedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 54321);
            var stunResponse = RespondToOneStunRequestAsync(
                stunSocket,
                mappedEndpoint,
                stunTimeout.Token);
            var discovered = await ProductionStunClient.QueryServerReflexiveAsync(
                first,
                (IPEndPoint)stunSocket.LocalEndPoint,
                TimeSpan.FromSeconds(2),
                stunTimeout.Token).ConfigureAwait(false);
            await stunResponse.ConfigureAwait(false);
            Require(discovered.Equals(mappedEndpoint) &&
                    ((IPEndPoint)first.Socket.LocalEndPoint).Equals(retainedEndpoint),
                "STUN did not retain the exact candidate socket and port for probing.");

            var firstDevice = Guid.NewGuid();
            var secondDevice = Guid.NewGuid();
            var connection = Guid.NewGuid();
            var ticket = RandomNumberGenerator.GetBytes(16);
            var nomination = RandomNumberGenerator.GetBytes(16);
            var pair = new DirectCandidatePairV1
            {
                InitiatorCandidateId = first.Candidate.CandidateId,
                ResponderCandidateId = second.Candidate.CandidateId
            };
            var firstProbe = ProbeFor(
                firstDevice,
                connection,
                ticket,
                nomination,
                pair,
                9);
            var secondProbe = ProbeFor(
                secondDevice,
                connection,
                ticket,
                nomination,
                pair,
                9);
            var pairwise = RandomNumberGenerator.GetBytes(32);
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var heldReceiveProtocol = await first.ClaimReceiveProtocolAsync(
                probeTimeout.Token).ConfigureAwait(false);
            var blockedProbeElapsed = Stopwatch.StartNew();
            var blockedProbe = await ProductionDirectConnectivityProbe.ProbeAsync(
                first,
                (IPEndPoint)second.Socket.LocalEndPoint,
                firstProbe,
                secondDevice,
                pairwise,
                probeTimeout.Token).ConfigureAwait(false);
            Require(!blockedProbe && blockedProbeElapsed.Elapsed < TimeSpan.FromSeconds(3),
                "Waiting for the receive owner exceeded the probe's hard operation budget.");
            heldReceiveProtocol.Dispose();
            var firstProbeTask = ProductionDirectConnectivityProbe.ProbeAsync(
                first,
                (IPEndPoint)second.Socket.LocalEndPoint,
                firstProbe,
                secondDevice,
                pairwise,
                probeTimeout.Token);
            var secondProbeTask = ProductionDirectConnectivityProbe.ProbeAsync(
                second,
                (IPEndPoint)first.Socket.LocalEndPoint,
                secondProbe,
                firstDevice,
                pairwise,
                probeTimeout.Token);
            var outcomes = await Task.WhenAll(
                firstProbeTask,
                secondProbeTask).ConfigureAwait(false);
            Require(outcomes.All(value => value),
                "Two retained candidate sockets did not complete authenticated simultaneous UDP probing.");
            Require(((IPEndPoint)first.Socket.LocalEndPoint).Equals(retainedEndpoint),
                "The candidate port changed between STUN and authenticated probing.");
        }

        private static async Task RespondToOneStunRequestAsync(
            Socket server,
            IPEndPoint mappedEndpoint,
            CancellationToken token)
        {
            var request = new byte[64];
            EndPoint remote = server.AddressFamily == AddressFamily.InterNetwork
                ? new IPEndPoint(IPAddress.Any, 0)
                : new IPEndPoint(IPAddress.IPv6Any, 0);
            var received = await server.ReceiveFromAsync(
                request,
                SocketFlags.None,
                remote,
                token).ConfigureAwait(false);
            if (received.ReceivedBytes != 20)
                throw new InvalidOperationException("The local STUN fixture received an invalid request.");
            var response = CreateStunIPv4Success(
                request.AsSpan(8, 12).ToArray(),
                mappedEndpoint);
            var wrongTransaction = request.AsSpan(8, 12).ToArray();
            wrongTransaction[0] ^= 0xff;
            var delayedResponse = CreateStunIPv4Success(
                wrongTransaction,
                mappedEndpoint);
            await server.SendToAsync(
                delayedResponse,
                SocketFlags.None,
                received.RemoteEndPoint,
                token).ConfigureAwait(false);
            using (var unrelated = new Socket(
                server.AddressFamily,
                SocketType.Dgram,
                ProtocolType.Udp))
            {
                if (server.AddressFamily == AddressFamily.InterNetworkV6)
                    unrelated.DualMode = false;
                var serverLocal = (IPEndPoint)server.LocalEndPoint;
                unrelated.Bind(new IPEndPoint(serverLocal.Address, 0));
                await unrelated.SendToAsync(
                    response,
                    SocketFlags.None,
                    received.RemoteEndPoint,
                    token).ConfigureAwait(false);
            }
            await server.SendToAsync(
                response,
                SocketFlags.None,
                received.RemoteEndPoint,
                token).ConfigureAwait(false);
        }

        private static DirectCandidateV1 CandidateFor(IPEndPoint endpoint)
        {
            return new DirectCandidateV1
            {
                CandidateId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                Kind = DirectCandidateKindV1.Host,
                Carrier = DirectCarrierV1.UdpQuic,
                IpAddress = ByteString.CopyFrom(endpoint.Address.GetAddressBytes()),
                Port = checked((uint)endpoint.Port),
                Priority = 1,
                ExpiresAtMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()
            };
        }

        private static DirectConnectivityDatagramV1 ProbeFor(
            Guid sender,
            Guid connection,
            byte[] ticket,
            byte[] nomination,
            DirectCandidatePairV1 pair,
            ulong epoch)
        {
            return new DirectConnectivityDatagramV1
            {
                ProtocolVersion = 1,
                Kind = DirectConnectivityDatagramKindV1.Probe,
                ConnectionId = ByteString.CopyFrom(GuidBytes(connection)),
                PathEpoch = epoch,
                TicketId = ByteString.CopyFrom(ticket),
                NominationId = ByteString.CopyFrom(nomination),
                Pair = pair.Clone(),
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(sender)),
                ProbeNonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(16)),
                SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        private static void RequireThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
