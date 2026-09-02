using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.ProductionDirectNative.Validation
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            try
            {
                ValidateExactAbiV3LayoutAndHeaders();
                ValidateDisabledAndUnavailableRemainRelayOnly();
                ValidateGatherCandidatesThenNominateOwnedCopies();
                ValidateSynchronousNativeCommandFaults();
                ValidateStaleEpochAttemptAndSignatureFences();
                ValidateBoundedPollAndMalformedEventFallback();
                ValidateNetworkChangeCancelAndRelayIsolation();
                await ValidatePollDisposeExternalSynchronizationAsync().ConfigureAwait(false);
                ValidateRealAbiLibraryIfRequested();
                ProductionDirectNativeV4Validation.Validate();
                Console.WriteLine("Production direct native ABI v3/v4 validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidateExactAbiV3LayoutAndHeaders()
        {
            Require(ProductionDirectNativeAbiV3.TryValidateLayout(out var error),
                "ABI v3 layout validation failed at " + error + ".");
            Require(Marshal.SizeOf<TermLinkDirectCreateV3>() == 32 &&
                    Marshal.SizeOf<TermLinkDirectGatherV3>() == 176 &&
                    Marshal.SizeOf<TermLinkDirectNominateV3>() == 2_696 &&
                    Marshal.SizeOf<TermLinkDirectCandidatesV3>() == 80 &&
                    Marshal.SizeOf<TermLinkSocketAddressV2>() == 24 &&
                    Marshal.SizeOf<TermLinkDirectStartV2>() == 2_592 &&
                    Marshal.SizeOf<TermLinkDirectControlV2>() == 32 &&
                    Marshal.SizeOf<TermLinkDirectSignatureV2>() == 176 &&
                    Marshal.SizeOf<TermLinkDirectEventV2>() == 2_136,
                "A frozen ABI v3 or retained v2 record changed size.");
            Require(Offset<TermLinkDirectGatherV3>(nameof(TermLinkDirectGatherV3.LocalBind)) == 40 &&
                    Offset<TermLinkDirectGatherV3>(nameof(TermLinkDirectGatherV3.StunServers)) == 88 &&
                    Offset<TermLinkDirectGatherV3>(nameof(TermLinkDirectGatherV3.Reserved)) == 144 &&
                    Offset<TermLinkDirectNominateV3>(nameof(TermLinkDirectNominateV3.StartV2)) == 8 &&
                    Offset<TermLinkDirectNominateV3>(nameof(TermLinkDirectNominateV3.LocalSendProbeHmacKey)) == 2_600 &&
                    Offset<TermLinkDirectNominateV3>(nameof(TermLinkDirectNominateV3.PeerReceiveProbeHmacKey)) == 2_632 &&
                    Offset<TermLinkDirectNominateV3>(nameof(TermLinkDirectNominateV3.Reserved)) == 2_664 &&
                    Offset<TermLinkDirectCandidatesV3>(nameof(TermLinkDirectCandidatesV3.Candidates)) == 8,
                "A frozen ABI v3 field offset changed.");
            Require(GuidBytes(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"))
                    .SequenceEqual(Convert.FromHexString("00112233445566778899AABBCCDDEEFF")),
                "A native UUID used the CLR Guid mixed-endian memory layout.");
        }

        private static void ValidateDisabledAndUnavailableRemainRelayOnly()
        {
            var loader = new FakeLoader(new FakeNativeApi());
            using (var disabled = new ProductionDirectNativeBoundary(enabled: false, loader: loader))
            {
                Require(loader.LoadCount == 0 &&
                        disabled.Gather(Gather(1, Guid.NewGuid())) == ProductionDirectNativeCommandResult.RelayFallback &&
                        disabled.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                        disabled.Snapshot.Fault == ProductionDirectNativeFault.Disabled &&
                        !disabled.Snapshot.NativeAvailable && !disabled.Snapshot.DirectRoutingPermitted &&
                        loader.LoadCount == 0,
                    "Disabled v3 loaded native code or escaped RelayOnly.");
            }

            var missing = new ProductionDirectNativeLibraryLoader(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "termlink-native-missing-" + Guid.NewGuid().ToString("N") + ".dll"));
            using (var unavailable = new ProductionDirectNativeBoundary(enabled: true, loader: missing))
            {
                Require(unavailable.Gather(Gather(1, Guid.NewGuid())) == ProductionDirectNativeCommandResult.RelayFallback &&
                        unavailable.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                        unavailable.Snapshot.Fault == ProductionDirectNativeFault.NativeUnavailable &&
                        !unavailable.Snapshot.DirectRoutingPermitted,
                    "A missing v3 DLL escaped RelayOnly.");
            }

            var wrongApi = new FakeNativeApi { ReportedAbi = 2 };
            using var mismatch = new ProductionDirectNativeBoundary(
                enabled: true, loader: new FakeLoader(wrongApi));
            Require(wrongApi.CreateCalls == 0 &&
                    mismatch.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                    mismatch.Snapshot.Fault == ProductionDirectNativeFault.AbiMismatch,
                "An ABI v2 library was admitted through the ABI v3 boundary.");
        }

        private static void ValidateGatherCandidatesThenNominateOwnedCopies()
        {
            var api = new FakeNativeApi();
            using var boundary = Boundary(api);
            var attempt = Guid.NewGuid();
            var gather = Gather(4, attempt);
            Require(boundary.Gather(gather) == ProductionDirectNativeCommandResult.Accepted &&
                    api.GatherCalls == 1 && api.LastGather.AbiVersion == 3 &&
                    api.LastGather.StructSize == 176 && api.LastGather.NetworkHandle == 0 &&
                    api.LastGather.LocalBind.Port == 0 && api.LastGather.AdvertisedLocalIpLength == 4,
                "A valid ABI v3 Gather record was rejected or changed.");

            var bound = new ProductionDirectNativeEndpoint(IPAddress.Parse("192.0.2.10"), 49_152);
            api.Enqueue(CandidatesEvent(4, attempt, 1, bound,
                new[]
                {
                    bound,
                    new ProductionDirectNativeEndpoint(IPAddress.Parse("198.51.100.10"), 54_321)
                }));
            var ready = boundary.Poll().Single();
            Require(ready.Kind == ProductionDirectNativeEventKind.CandidatesReady &&
                    ready.Candidates.Count == 2 &&
                    ready.Candidates[0].Address.Equals(bound.Address) &&
                    ready.Candidates[0].Port == bound.Port &&
                    !boundary.Snapshot.DirectRoutingPermitted,
                "CandidatesReady did not decode the retained socket candidate set.");
            Require(boundary.MarkCandidatesExchanged(
                        4, attempt, ready.Generation, ready.Candidates) ==
                    ProductionDirectNativeCommandResult.Accepted,
                "The exact CandidatesReady generation was not admitted as exchanged.");

            var nomination = Nomination(4, attempt, bound);
            var ticketFirst = nomination.Ticket[0];
            Require(boundary.Nominate(nomination) == ProductionDirectNativeCommandResult.Accepted &&
                    api.NominateCalls == 1 && api.LastNominate.AbiVersion == 3 &&
                    api.LastNominate.StructSize == 2_696 &&
                    api.LastNominate.StartV2.AbiVersion == 2 &&
                    api.LastNominate.StartV2.StructSize == 2_592 &&
                    api.LastNominate.StartV2.LocalBind.Port == (uint)bound.Port &&
                    api.LastNominate.StartV2.StunCount == 0 &&
                    api.LastNominate.StartV2.StunServers.All(IsEmpty) &&
                    api.LastNominate.StartV2.PeerProofNonceLength == 0 &&
                    api.LastNominate.StartV2.PeerProofNonce.All(value => value == 0) &&
                    api.LastNominate.StartV2.ProbeHmacKey.SequenceEqual(
                        api.LastNominate.LocalSendProbeHmacKey) &&
                    !api.LastNominate.LocalSendProbeHmacKey.SequenceEqual(
                        api.LastNominate.PeerReceiveProbeHmacKey) &&
                    api.LastNominate.Reserved.All(value => value == 0),
                "Nominate did not use a v3 outer record with the frozen v2 embedded header.");
            nomination.Ticket[0] ^= 0xff;
            nomination.LocalSendProbeHmacKey[0] ^= 0xff;
            nomination.PeerReceiveProbeHmacKey[0] ^= 0xff;
            Require(api.LastNominate.StartV2.Ticket[0] == ticketFirst &&
                    api.LastNominate.StartV2.ProbeHmacKey[0] != nomination.LocalSendProbeHmacKey[0] &&
                    api.LastNominate.PeerReceiveProbeHmacKey[0] !=
                        nomination.PeerReceiveProbeHmacKey[0] &&
                    api.NominateInputBuffers.All(buffer => buffer.All(value => value == 0)),
                "The v3 nomination retained caller-owned mutable cryptographic buffers.");
        }

        private static void ValidateSynchronousNativeCommandFaults()
        {
            foreach (var fixture in new[]
            {
                (Status: -4, Fault: ProductionDirectNativeFault.QueueFull),
                (Status: -1, Fault: ProductionDirectNativeFault.WorkerStopped),
                (Status: -2, Fault: ProductionDirectNativeFault.AbiMismatch),
                (Status: -3, Fault: ProductionDirectNativeFault.NativePanic)
            })
            {
                var api = new FakeNativeApi { GatherStatus = fixture.Status };
                using var boundary = Boundary(api);
                Require(boundary.Gather(Gather(1, Guid.NewGuid())) == ProductionDirectNativeCommandResult.RelayFallback &&
                        boundary.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                        boundary.Snapshot.Fault == fixture.Fault &&
                        !boundary.Snapshot.DirectRoutingPermitted && api.DestroyCalls == 1,
                    "A v3 Gather command failure did not retire only the shadow handle.");
            }

            var nominateApi = new FakeNativeApi { NominateStatus = -4 };
            using var nominateBoundary = Boundary(nominateApi);
            var attempt = Guid.NewGuid();
            nominateBoundary.Gather(Gather(2, attempt));
            var bound = new ProductionDirectNativeEndpoint(
                IPAddress.Parse("192.0.2.10"), 49_152);
            nominateApi.Enqueue(CandidatesEvent(2, attempt, 1, bound, new[] { bound }));
            var candidates = nominateBoundary.Poll().Single();
            Require(nominateBoundary.Nominate(Nomination(2, attempt, bound)) ==
                    ProductionDirectNativeCommandResult.RelayFallback &&
                    nominateApi.NominateCalls == 0,
                "Nominate bypassed the exact candidate-exchange acknowledgement.");

            var retryApi = new FakeNativeApi { NominateStatus = -4 };
            using var retryBoundary = Boundary(retryApi);
            var retryAttempt = Guid.NewGuid();
            retryBoundary.Gather(Gather(2, retryAttempt));
            retryApi.Enqueue(CandidatesEvent(2, retryAttempt, 1, bound, new[] { bound }));
            var retryCandidates = retryBoundary.Poll().Single();
            Require(retryBoundary.MarkCandidatesExchanged(
                        2, retryAttempt, retryCandidates.Generation, retryCandidates.Candidates) ==
                    ProductionDirectNativeCommandResult.Accepted,
                "A valid candidate-exchange acknowledgement was rejected.");
            Require(retryBoundary.Nominate(Nomination(2, retryAttempt, bound)) ==
                    ProductionDirectNativeCommandResult.RelayFallback &&
                    retryBoundary.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                    retryBoundary.Snapshot.Fault == ProductionDirectNativeFault.QueueFull &&
                    retryApi.DestroyCalls == 1,
                "A v3 Nominate failure escaped the shadow-only fallback boundary.");
        }

        private static void ValidateStaleEpochAttemptAndSignatureFences()
        {
            var api = new FakeNativeApi();
            using var boundary = Boundary(api);
            var current = Guid.NewGuid();
            Require(boundary.Gather(Gather(8, current)) == ProductionDirectNativeCommandResult.Accepted &&
                    boundary.Gather(Gather(7, Guid.NewGuid())) == ProductionDirectNativeCommandResult.IgnoredStale &&
                    api.GatherCalls == 1,
                "A stale Gather crossed into native or retired its successor.");
            Require(boundary.Nominate(Nomination(8, Guid.NewGuid(),
                        new ProductionDirectNativeEndpoint(IPAddress.Parse("192.0.2.10"), 49_152))) ==
                    ProductionDirectNativeCommandResult.IgnoredStale && api.NominateCalls == 0 &&
                    boundary.Cancel(8, Guid.NewGuid()) == ProductionDirectNativeCommandResult.IgnoredStale &&
                    api.CancelCalls == 0,
                "A stale attempt nominated or cancelled the current retained socket.");

            api.Enqueue(Event(ProductionDirectNativeEventKind.SignRequest,
                ProductionDirectNativeState.Authenticating, 8, current, 1,
                requestId: 77, payload: Enumerable.Repeat((byte)0x2a, 64).ToArray()));
            Require(boundary.Poll().Single().Payload.Length == 64 &&
                    boundary.SubmitSignature(7, current, 77, new byte[64]) == ProductionDirectNativeCommandResult.IgnoredStale &&
                    boundary.SubmitSignature(8, current, 76, new byte[64]) == ProductionDirectNativeCommandResult.IgnoredStale &&
                    api.SignatureCalls == 0,
                "A stale signature epoch or request crossed into native.");
            var signature = Enumerable.Repeat((byte)0x5c, 64).ToArray();
            Require(boundary.SubmitSignature(8, current, 77, signature) == ProductionDirectNativeCommandResult.Accepted &&
                    api.LastSignature.AbiVersion == 3 && api.SignatureCalls == 1 &&
                    api.LastSignature.Signature.Take(64).SequenceEqual(signature) &&
                    boundary.SubmitSignature(8, current, 77, signature) == ProductionDirectNativeCommandResult.IgnoredStale &&
                    api.SignatureCalls == 1,
                "ABI v3 signature one-shot fencing failed.");
        }

        private static void ValidateBoundedPollAndMalformedEventFallback()
        {
            var api = new FakeNativeApi();
            using var boundary = Boundary(api);
            var attempt = Guid.NewGuid();
            boundary.Gather(Gather(3, attempt));
            for (ulong generation = 1; generation <= 10; generation++)
                api.Enqueue(Event(ProductionDirectNativeEventKind.StateChanged,
                    ProductionDirectNativeState.Gathering, 3, attempt, generation));
            Require(boundary.Poll(3).Select(value => value.Generation)
                        .SequenceEqual(new ulong[] { 1, 2, 3 }) && api.PollCalls == 3,
                "ABI v3 poll exceeded its caller bound or reordered generations.");
            Require(boundary.Poll(ProductionDirectNativeAbiV2.MaximumQueueCapacity).Count == 7 &&
                    api.PollCalls == 11 && boundary.Snapshot.NativeGeneration == 10,
                "ABI v3 poll did not drain the remaining bounded events.");

            var malformedApi = new FakeNativeApi();
            using var malformedBoundary = Boundary(malformedApi);
            var malformedAttempt = Guid.NewGuid();
            malformedBoundary.Gather(Gather(1, malformedAttempt));
            var malformed = Event(ProductionDirectNativeEventKind.StateChanged,
                ProductionDirectNativeState.Gathering, 1, malformedAttempt, 1);
            malformed.AbiVersion = 2;
            malformedApi.Enqueue(malformed);
            Require(malformedBoundary.Poll().Count == 0 &&
                    malformedBoundary.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                    malformedBoundary.Snapshot.Fault == ProductionDirectNativeFault.AbiMismatch &&
                    malformedApi.DestroyCalls == 1,
                "A v2-tagged event escaped the strict ABI v3 boundary.");

            var malformedCandidatesApi = new FakeNativeApi();
            using var malformedCandidatesBoundary = Boundary(malformedCandidatesApi);
            var candidateAttempt = Guid.NewGuid();
            malformedCandidatesBoundary.Gather(Gather(2, candidateAttempt));
            var candidate = new ProductionDirectNativeEndpoint(IPAddress.Parse("192.0.2.10"), 49_152);
            var malformedCandidates = CandidatesEvent(2, candidateAttempt, 1, candidate, new[] { candidate });
            malformedCandidates.Payload[4] = 1; // candidates.reserved must stay zero.
            malformedCandidatesApi.Enqueue(malformedCandidates);
            Require(malformedCandidatesBoundary.Poll().Count == 0 &&
                    malformedCandidatesBoundary.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                    malformedCandidatesBoundary.Snapshot.Fault == ProductionDirectNativeFault.AbiMismatch,
                "A malformed CandidatesReady payload was admitted.");
        }

        private static void ValidateNetworkChangeCancelAndRelayIsolation()
        {
            var relay = new ProductionRealtimeConnectionSupervisor(new ProductionReconnectPreferences
            {
                InitialDelaySeconds = 1,
                MaximumDelaySeconds = 4,
                MaximumFastAttempts = 0
            }, nextJitterMilliseconds: () => 0);
            var relayGeneration = relay.MarkPhysicalConnected();

            var changedApi = new FakeNativeApi();
            using (var boundary = Boundary(changedApi))
            {
                var current = Guid.NewGuid();
                boundary.Gather(Gather(5, current));
                Require(boundary.NetworkChanged(4, Guid.NewGuid()) == ProductionDirectNativeCommandResult.IgnoredStale &&
                        changedApi.NetworkChangedCalls == 0 &&
                        boundary.NetworkChanged(5, current) == ProductionDirectNativeCommandResult.Accepted &&
                        boundary.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                        boundary.Snapshot.Fault == ProductionDirectNativeFault.NetworkChanged &&
                        changedApi.NetworkChangedCalls == 1 && changedApi.LastControlAbi == 3 &&
                        changedApi.DestroyCalls == 1,
                    "Network change did not retire the exact direct epoch only.");
            }
            var afterDirectFault = relay.Snapshot();
            Require(afterDirectFault.PhysicalConnected && afterDirectFault.PhysicalGeneration == relayGeneration &&
                    afterDirectFault.ScheduledReconnects == 0,
                "A direct network-change fault replaced or reconnected a healthy Relay generation.");

            var cancelApi = new FakeNativeApi();
            using var cancelBoundary = Boundary(cancelApi);
            var cancelAttempt = Guid.NewGuid();
            cancelBoundary.Gather(Gather(6, cancelAttempt));
            Require(cancelBoundary.Cancel(6, cancelAttempt) == ProductionDirectNativeCommandResult.Accepted &&
                    cancelBoundary.Snapshot.State == ProductionDirectNativeState.RelayOnly &&
                    cancelBoundary.Snapshot.Fault == ProductionDirectNativeFault.Cancelled &&
                    cancelApi.CancelCalls == 1 && cancelApi.LastControlAbi == 3 &&
                    cancelApi.DestroyCalls == 1 && !cancelBoundary.Snapshot.DirectRoutingPermitted,
                "Cancel did not synchronously retire only the current direct epoch.");
        }

        private static async Task ValidatePollDisposeExternalSynchronizationAsync()
        {
            var api = new FakeNativeApi
            {
                PollEntered = new ManualResetEventSlim(false),
                PollRelease = new ManualResetEventSlim(false)
            };
            var boundary = Boundary(api);
            boundary.Gather(Gather(1, Guid.NewGuid()));
            var poll = Task.Run(() => boundary.Poll());
            Require(api.PollEntered.Wait(TimeSpan.FromSeconds(5)),
                "Concurrent ABI v3 poll fixture did not enter native.");
            var dispose = Task.Run(boundary.Dispose);
            await Task.Delay(50).ConfigureAwait(false);
            Require(api.DestroyCalls == 0 && !api.DestroyRacedPoll,
                "Dispose raced a live ABI v3 native poll.");
            api.PollRelease.Set();
            await Task.WhenAll(poll, dispose).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Require(api.DestroyCalls == 1 && !api.DestroyRacedPoll && boundary.Snapshot.Disposed &&
                    boundary.Snapshot.State == ProductionDirectNativeState.RelayOnly,
                "ABI v3 Poll/Dispose external synchronization failed.");
            api.PollEntered.Dispose();
            api.PollRelease.Dispose();
        }

        private static void ValidateRealAbiLibraryIfRequested()
        {
            var path = Environment.GetEnvironmentVariable("TERMLINK_DIRECT_NATIVE_TEST_DLL");
            if (string.IsNullOrWhiteSpace(path)) return;
            var address = ActiveAddress();
            var attempt = Guid.NewGuid();
            using var boundary = new ProductionDirectNativeBoundary(
                enabled: true, queueCapacity: 8, loader: new ProductionDirectNativeLibraryLoader(path));
            Require(boundary.Snapshot.NativeAvailable &&
                    boundary.Gather(new ProductionDirectNativeGatherRequest
                    {
                        Epoch = 1,
                        AttemptId = attempt,
                        LocalBind = new ProductionDirectNativeEndpoint(address, 0),
                        AdvertisedLocalAddress = address,
                        GatherTimeoutMilliseconds = 500
                    }) == ProductionDirectNativeCommandResult.Accepted,
                "The real ABI v3 DLL did not accept Gather.");
            var observed = new List<ProductionDirectNativeEvent>();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline &&
                   !observed.Any(value => value.Kind == ProductionDirectNativeEventKind.CandidatesReady))
            {
                observed.AddRange(boundary.Poll(8));
                if (!observed.Any(value => value.Kind == ProductionDirectNativeEventKind.CandidatesReady))
                    Thread.Sleep(5);
            }
            var identity = observed.FirstOrDefault(value => value.Kind == ProductionDirectNativeEventKind.IdentityReady);
            var candidates = observed.FirstOrDefault(value => value.Kind == ProductionDirectNativeEventKind.CandidatesReady);
            Require(identity != null && identity.State == ProductionDirectNativeState.Gathering &&
                    identity.Payload.Length is > 0 and <= 2_048 &&
                    candidates != null && candidates.Candidates.Count >= 1 &&
                    candidates.Endpoint != null && candidates.Endpoint.Port >= 1_024 &&
                    !boundary.Snapshot.DirectRoutingPermitted,
                "The real ABI v3 DLL did not produce owned identity/candidate events after Gather.");
            boundary.Cancel(1, attempt);
        }

        private static ProductionDirectNativeBoundary Boundary(FakeNativeApi api) =>
            new(enabled: true, queueCapacity: 32, loader: new FakeLoader(api));

        private static ProductionDirectNativeGatherRequest Gather(ulong epoch, Guid attempt) => new()
        {
            Epoch = epoch,
            AttemptId = attempt,
            NetworkHandle = 0,
            LocalBind = new ProductionDirectNativeEndpoint(IPAddress.Parse("192.0.2.10"), 0),
            AdvertisedLocalAddress = IPAddress.Parse("192.0.2.10"),
            StunServers = new[]
            {
                new ProductionDirectNativeEndpoint(IPAddress.Parse("203.0.113.30"), 3_478)
            },
            GatherTimeoutMilliseconds = 1_000
        };

        private static ProductionDirectNativeStartRequest Nomination(
            ulong epoch,
            Guid attempt,
            ProductionDirectNativeEndpoint localBind) => new()
        {
            Epoch = epoch,
            AttemptId = attempt,
            ConnectionId = Guid.NewGuid(),
            LocalDeviceId = Guid.NewGuid(),
            PeerDeviceId = Guid.NewGuid(),
            Role = ProductionDirectNativeRole.Initiator,
            TimeoutMilliseconds = 1_000,
            LocalBind = localBind,
            RemoteCandidate = new ProductionDirectNativeEndpoint(IPAddress.Parse("198.51.100.20"), 34_780),
            StunServers = Array.Empty<ProductionDirectNativeEndpoint>(),
            Ticket = Enumerable.Range(1, 128).Select(value => (byte)value).ToArray(),
            ServerSigningPublicKey = Enumerable.Repeat((byte)0x11, 32).ToArray(),
            PeerCertificateSha256 = Enumerable.Repeat((byte)0x22, 32).ToArray(),
            PeerDeviceSigningPublicKey = Enumerable.Repeat((byte)0x33, 32).ToArray(),
            LocalSendProbeHmacKey = Enumerable.Repeat((byte)0x44, 32).ToArray(),
            PeerReceiveProbeHmacKey = Enumerable.Repeat((byte)0x45, 32).ToArray(),
            TicketId = Guid.NewGuid(),
            NominationId = Guid.NewGuid(),
            InitiatorCandidateId = Guid.NewGuid(),
            ResponderCandidateId = Guid.NewGuid(),
            LocalProbeNonce = Enumerable.Repeat((byte)0x55, 16).ToArray(),
            PeerProbeNonce = Enumerable.Repeat((byte)0x55, 16).ToArray(),
            LocalProofNonce = Enumerable.Repeat((byte)0x77, 16).ToArray(),
            NowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        private static TermLinkDirectEventV2 CandidatesEvent(
            ulong epoch,
            Guid attempt,
            ulong generation,
            ProductionDirectNativeEndpoint bound,
            IReadOnlyList<ProductionDirectNativeEndpoint> candidates)
        {
            var native = new TermLinkDirectCandidatesV3
            {
                CandidateCount = checked((uint)candidates.Count),
                Candidates = Enumerable.Range(0, ProductionDirectNativeAbiV3.MaximumCandidates)
                    .Select(index => index < candidates.Count ? Encode(candidates[index]) : TermLinkSocketAddressV2.Empty())
                    .ToArray()
            };
            return Event(ProductionDirectNativeEventKind.CandidatesReady,
                ProductionDirectNativeState.Gathering, epoch, attempt, generation,
                payload: StructBytes(native), endpoint: bound);
        }

        private static TermLinkDirectEventV2 Event(
            ProductionDirectNativeEventKind kind,
            ProductionDirectNativeState state,
            ulong epoch,
            Guid attempt,
            ulong generation,
            ulong requestId = 0,
            byte[] payload = null,
            ProductionDirectNativeReason reason = ProductionDirectNativeReason.None,
            ProductionDirectNativeEndpoint endpoint = null)
        {
            payload ??= Array.Empty<byte>();
            var buffer = new byte[ProductionDirectNativeAbiV2.MaximumEventPayloadBytes];
            Array.Copy(payload, buffer, payload.Length);
            return new TermLinkDirectEventV2
            {
                AbiVersion = ProductionDirectNativeAbiV3.AbiVersion,
                StructSize = ProductionDirectNativeAbiV2.EventSize,
                Kind = (uint)kind,
                State = (uint)state,
                Reason = (uint)reason,
                PayloadLength = checked((uint)payload.Length),
                Epoch = epoch,
                Generation = generation,
                RequestId = requestId,
                AttemptId = GuidBytes(attempt),
                Endpoint = endpoint == null ? TermLinkSocketAddressV2.Empty() : Encode(endpoint),
                Payload = buffer
            };
        }

        private static IPAddress ActiveAddress() => NetworkInterface.GetAllNetworkInterfaces()
            .Where(value => value.OperationalStatus == OperationalStatus.Up &&
                value.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                value.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(value => value.GetIPProperties().UnicastAddresses)
            .Select(value => value.Address)
            .First(value => value.AddressFamily == AddressFamily.InterNetwork &&
                ProductionDirectCandidateCollector.IsAllowedHostAddress(value));

        private static TermLinkSocketAddressV2 Encode(ProductionDirectNativeEndpoint endpoint)
        {
            var bytes = endpoint.Address.GetAddressBytes();
            var ip = new byte[16];
            Array.Copy(bytes, ip, bytes.Length);
            return new TermLinkSocketAddressV2
            {
                Ip = ip,
                IpLength = checked((uint)bytes.Length),
                Port = checked((uint)endpoint.Port)
            };
        }

        private static bool IsEmpty(TermLinkSocketAddressV2 value) =>
            value.IpLength == 0 && value.Port == 0 && value.Ip.All(octet => octet == 0);

        private static byte[] StructBytes<T>(T value) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, pointer, false);
                var result = new byte[size];
                Marshal.Copy(pointer, result, 0, size);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure<T>(pointer);
                Marshal.FreeHGlobal(pointer);
            }
        }

        private static int Offset<T>(string field) where T : struct => checked((int)Marshal.OffsetOf<T>(field));
        private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

        private static TermLinkDirectGatherV3 CloneGather(TermLinkDirectGatherV3 value)
        {
            value.AttemptId = value.AttemptId?.ToArray();
            value.LocalBind.Ip = value.LocalBind.Ip?.ToArray();
            value.AdvertisedLocalIp = value.AdvertisedLocalIp?.ToArray();
            value.StunServers = value.StunServers?.Select(item =>
            {
                item.Ip = item.Ip?.ToArray();
                return item;
            }).ToArray();
            value.Reserved = value.Reserved?.ToArray();
            return value;
        }

        private static TermLinkDirectStartV2 CloneStart(TermLinkDirectStartV2 value)
        {
            value.AttemptId = value.AttemptId?.ToArray();
            value.ConnectionId = value.ConnectionId?.ToArray();
            value.LocalDeviceId = value.LocalDeviceId?.ToArray();
            value.PeerDeviceId = value.PeerDeviceId?.ToArray();
            value.LocalBind.Ip = value.LocalBind.Ip?.ToArray();
            value.RemoteCandidate.Ip = value.RemoteCandidate.Ip?.ToArray();
            value.StunServers = value.StunServers?.Select(item =>
            {
                item.Ip = item.Ip?.ToArray();
                return item;
            }).ToArray();
            value.Ticket = value.Ticket?.ToArray();
            value.ServerSigningPublicKey = value.ServerSigningPublicKey?.ToArray();
            value.PeerCertificateSha256 = value.PeerCertificateSha256?.ToArray();
            value.PeerDeviceSigningPublicKey = value.PeerDeviceSigningPublicKey?.ToArray();
            value.ProbeHmacKey = value.ProbeHmacKey?.ToArray();
            value.TicketId = value.TicketId?.ToArray();
            value.NominationId = value.NominationId?.ToArray();
            value.InitiatorCandidateId = value.InitiatorCandidateId?.ToArray();
            value.ResponderCandidateId = value.ResponderCandidateId?.ToArray();
            value.LocalProbeNonce = value.LocalProbeNonce?.ToArray();
            value.PeerProbeNonce = value.PeerProbeNonce?.ToArray();
            value.LocalProofNonce = value.LocalProofNonce?.ToArray();
            value.PeerProofNonce = value.PeerProofNonce?.ToArray();
            value.Reserved = value.Reserved?.ToArray();
            return value;
        }

        private static TermLinkDirectEventV2 CloneEvent(TermLinkDirectEventV2 value)
        {
            value.AttemptId = value.AttemptId?.ToArray();
            value.Endpoint.Ip = value.Endpoint.Ip?.ToArray();
            value.Payload = value.Payload?.ToArray();
            return value;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FakeLoader : IProductionDirectNativeLoader
        {
            private readonly IProductionDirectNativeApi api;
            private int loadCount;
            public FakeLoader(IProductionDirectNativeApi api) { this.api = api; }
            public int LoadCount => Volatile.Read(ref loadCount);
            public bool TryLoad(out IProductionDirectNativeApi value, out string error)
            {
                Interlocked.Increment(ref loadCount);
                value = api;
                error = string.Empty;
                return api != null;
            }
        }

        private sealed class FakeNativeApi : IProductionDirectNativeApi
        {
            private readonly ConcurrentQueue<TermLinkDirectEventV2> events = new();
            private int activePoll;
            private int createCalls;
            private int gatherCalls;
            private int nominateCalls;
            private int cancelCalls;
            private int networkChangedCalls;
            private int signatureCalls;
            private int pollCalls;
            private int destroyCalls;
            private int destroyRacedPoll;

            public uint ReportedAbi { get; init; } = 3;
            public int GatherStatus { get; init; }
            public int NominateStatus { get; init; }
            public int CancelStatus { get; init; }
            public int NetworkChangedStatus { get; init; }
            public int SignatureStatus { get; init; }
            public int PollStatusWhenEmpty { get; init; }
            public ManualResetEventSlim PollEntered { get; init; }
            public ManualResetEventSlim PollRelease { get; init; }
            public int CreateCalls => Volatile.Read(ref createCalls);
            public int GatherCalls => Volatile.Read(ref gatherCalls);
            public int NominateCalls => Volatile.Read(ref nominateCalls);
            public int CancelCalls => Volatile.Read(ref cancelCalls);
            public int NetworkChangedCalls => Volatile.Read(ref networkChangedCalls);
            public int SignatureCalls => Volatile.Read(ref signatureCalls);
            public int PollCalls => Volatile.Read(ref pollCalls);
            public int DestroyCalls => Volatile.Read(ref destroyCalls);
            public bool DestroyRacedPoll => Volatile.Read(ref destroyRacedPoll) != 0;
            public uint LastControlAbi { get; private set; }
            public TermLinkDirectGatherV3 LastGather { get; private set; }
            public TermLinkDirectNominateV3 LastNominate { get; private set; }
            public IReadOnlyList<byte[]> NominateInputBuffers { get; private set; } =
                Array.Empty<byte[]>();
            public TermLinkDirectSignatureV2 LastSignature { get; private set; }

            public uint AbiVersion() => ReportedAbi;
            public IntPtr Create(ref TermLinkDirectCreateV3 create)
            {
                Interlocked.Increment(ref createCalls);
                return new IntPtr(createCalls);
            }
            public int Gather(IntPtr handle, ref TermLinkDirectGatherV3 gather)
            {
                Interlocked.Increment(ref gatherCalls);
                LastGather = CloneGather(gather);
                return GatherStatus;
            }
            public int Nominate(IntPtr handle, ref TermLinkDirectNominateV3 nominate)
            {
                Interlocked.Increment(ref nominateCalls);
                NominateInputBuffers = new[]
                {
                    nominate.StartV2.Ticket,
                    nominate.StartV2.ProbeHmacKey,
                    nominate.StartV2.LocalProofNonce,
                    nominate.StartV2.PeerProofNonce,
                    nominate.LocalSendProbeHmacKey,
                    nominate.PeerReceiveProbeHmacKey
                };
                LastNominate = new TermLinkDirectNominateV3
                {
                    AbiVersion = nominate.AbiVersion,
                    StructSize = nominate.StructSize,
                    StartV2 = CloneStart(nominate.StartV2),
                    LocalSendProbeHmacKey = nominate.LocalSendProbeHmacKey?.ToArray(),
                    PeerReceiveProbeHmacKey = nominate.PeerReceiveProbeHmacKey?.ToArray(),
                    Reserved = nominate.Reserved?.ToArray()
                };
                return NominateStatus;
            }
            public int Cancel(IntPtr handle, ref TermLinkDirectControlV2 control)
            {
                Interlocked.Increment(ref cancelCalls);
                LastControlAbi = control.AbiVersion;
                return CancelStatus;
            }
            public int NetworkChanged(IntPtr handle, ref TermLinkDirectControlV2 control)
            {
                Interlocked.Increment(ref networkChangedCalls);
                LastControlAbi = control.AbiVersion;
                return NetworkChangedStatus;
            }
            public int SubmitSignature(IntPtr handle, ref TermLinkDirectSignatureV2 signature)
            {
                Interlocked.Increment(ref signatureCalls);
                var copy = signature;
                copy.AttemptId = signature.AttemptId?.ToArray();
                copy.Signature = signature.Signature?.ToArray();
                LastSignature = copy;
                return SignatureStatus;
            }
            public int PollEvent(IntPtr handle, ref TermLinkDirectEventV2 output)
            {
                Interlocked.Increment(ref pollCalls);
                Interlocked.Exchange(ref activePoll, 1);
                try
                {
                    PollEntered?.Set();
                    PollRelease?.Wait(TimeSpan.FromSeconds(5));
                    if (!events.TryDequeue(out var value)) return PollStatusWhenEmpty;
                    output = CloneEvent(value);
                    return 1;
                }
                finally { Interlocked.Exchange(ref activePoll, 0); }
            }
            public void Destroy(IntPtr handle)
            {
                if (Volatile.Read(ref activePoll) != 0) Interlocked.Exchange(ref destroyRacedPoll, 1);
                Interlocked.Increment(ref destroyCalls);
            }
            public void Enqueue(TermLinkDirectEventV2 value) => events.Enqueue(CloneEvent(value));
        }
    }
}
