using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.ProductionSubscriberRecovery.Validation
{
    internal static class Program
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic;

        private static int Main()
        {
            try
            {
                var runtimeType = Assembly.Load("TermLink.Agent").GetType(
                    "TermLink.Agent.ProductionAgentRuntime",
                    throwOnError: true);
                ValidateFullSubscriberWindowRecoversAcrossSubscribeGeneration(runtimeType);
                ValidateAuthorizedRecipientSigningKeyContract(runtimeType);
                ValidateDirectPeerProviderIsolation(runtimeType);
                ValidateNetworkAvailabilityRelayFirst(runtimeType);
                ValidateRetainedRouteSkipsForcedRefresh(runtimeType);
                ValidateSharedAccountLeaseInput(runtimeType);
                ValidateLeaseFencingAndAuthorization(runtimeType);
                ValidateStaleLeaseReleaseDoesNotBlockPublication(runtimeType);
                ValidateLeaseReleaseDoesNotSwallowUnexpectedFailures(runtimeType);
                ValidateRepeatedUnauthorizedRequiresReauthentication(runtimeType);
                ValidateRejectedRefreshTokenStabilizesRuntime(runtimeType);
                ValidateForbiddenIdentityEndpointsRequireReauthentication(runtimeType);
                Console.WriteLine("Production subscriber recovery validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidateAuthorizedRecipientSigningKeyContract(Type runtimeType)
        {
            var parse = runtimeType.GetMethod("ParseAuthorizedRecipientKeys", StaticFlags);
            Require(parse != null, "Authorized-recipient key parser is unavailable.");
            var deviceId = Guid.NewGuid();
            var exchange = new byte[32];
            var signing = new byte[32];
            for (var index = 0; index < 32; index++)
            {
                exchange[index] = checked((byte)(index + 1));
                signing[index] = checked((byte)(0x80 + index));
            }
            var body = "{\"key_epoch\":7,\"items\":[{\"device_id\":\"" +
                deviceId.ToString("D") +
                "\",\"device_type\":\"ANDROID\",\"exchange_public_key\":\"" +
                Convert.ToBase64String(exchange) +
                "\",\"signing_public_key\":\"" +
                Convert.ToBase64String(signing) + "\"}]}";
            using (var document = JsonDocument.Parse(body))
            {
                var arguments = new object[] { document.RootElement, 7L, 0L };
                var parsed = (IDictionary)parse.Invoke(null, arguments);
                Require((long)arguments[2] == 7L && parsed.Count == 1 &&
                        parsed.Contains(deviceId),
                    "Authorized-recipient key epoch or device identity was not retained.");
                var material = parsed[deviceId];
                var materialType = material.GetType();
                var retainedExchange = (byte[])materialType.GetProperty(
                    "ExchangePublicKey", InstanceFlags).GetValue(material);
                var retainedSigning = (byte[])materialType.GetProperty(
                    "SigningPublicKey", InstanceFlags).GetValue(material);
                Require(retainedExchange.AsSpan().SequenceEqual(exchange) &&
                        retainedSigning.AsSpan().SequenceEqual(signing),
                    "Authorized-recipient exchange/signing keys were not retained independently.");
                ((IDisposable)material).Dispose();
                Require(IsAllZero(retainedExchange) && IsAllZero(retainedSigning),
                    "Authorized-recipient signing material was not zeroized on retirement.");
            }

            var missingSigning = "{\"key_epoch\":7,\"items\":[{\"device_id\":\"" +
                deviceId.ToString("D") +
                "\",\"device_type\":\"ANDROID\",\"exchange_public_key\":\"" +
                Convert.ToBase64String(exchange) + "\"}]}";
            using (var document = JsonDocument.Parse(missingSigning))
            {
                try
                {
                    parse.Invoke(null, new object[] { document.RootElement, 7L, 0L });
                    throw new InvalidOperationException(
                        "A recipient without a signing key crossed the Direct authorization boundary.");
                }
                catch (TargetInvocationException error) when (
                    error.InnerException is InvalidDataException)
                {
                }
            }
        }

        private static bool IsAllZero(byte[] value)
        {
            if (value == null) return false;
            foreach (var item in value)
                if (item != 0) return false;
            return true;
        }

        private static void ValidateDirectPeerProviderIsolation(Type runtimeType)
        {
            var snapshotPeers = runtimeType.GetMethod(
                "SnapshotDirectPeerAuthorizationsAsync",
                InstanceFlags);
            var createAuthorized = runtimeType.GetMethod(
                "CreateAuthorizedSubscriberWithSigningKey",
                StaticFlags);
            var retireAuthorized = runtimeType.GetMethod("RetireSubscriber", StaticFlags);
            var subscriberType = runtimeType.GetNestedType("Subscriber", BindingFlags.NonPublic);
            var publicationStateType = runtimeType.GetNestedType(
                "SessionPublicationState",
                BindingFlags.NonPublic);
            Require(snapshotPeers != null && createAuthorized != null && retireAuthorized != null &&
                    subscriberType != null && publicationStateType != null,
                "Direct peer-provider isolation hooks are unavailable.");

            var configType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionAgentConfig",
                throwOnError: true);
            var credentialsType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionCredentials",
                throwOnError: true);
            var supervisorType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionRealtimeConnectionSupervisor",
                throwOnError: true);
            var preferencesType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionReconnectPreferences",
                throwOnError: true);
            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            var config = Activator.CreateInstance(configType, nonPublic: true);
            var pcId = Guid.NewGuid();
            var pcDeviceId = Guid.NewGuid();
            var recipientDeviceId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var historySessionId = Guid.NewGuid();
            SetProperty(configType, config, "ServerUrl", "https://unit.test");
            SetProperty(configType, config, "PcId", pcId);
            SetProperty(configType, config, "KeyEpoch", 7L);
            SetProperty(configType, config, "OIDCIssuer", "https://oidc.unit.test");
            SetProperty(configType, config, "OIDCClientId", "unit-test-client");

            using var localIdentity = DeviceIdentity.Generate();
            using var remoteIdentity = DeviceIdentity.Generate();
            var credentials = RuntimeHelpers.GetUninitializedObject(credentialsType);
            SetField(credentialsType, credentials, "tokenSync", new object());
            SetProperty(credentialsType, credentials, "DeviceId", pcDeviceId);
            SetProperty(credentialsType, credentials, "Identity", localIdentity);
            SetProperty(credentialsType, credentials, "AccessToken", "unchanged-stale-token");
            SetProperty(credentialsType, credentials, "RefreshToken", "must-not-refresh");
            var accessTokenExpiry = DateTime.UtcNow.AddHours(-1);
            SetProperty(
                credentialsType,
                credentials,
                "AccessTokenExpiresAtUtc",
                accessTokenExpiry);

            var handler = new ScriptedHandler((request, token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
            using var http = new HttpClient(handler);
            using var socket = new ClientWebSocket();
            var preferences = Activator.CreateInstance(preferencesType);
            var supervisorConstructor = supervisorType.GetConstructors(InstanceFlags)[0];
            var supervisor = supervisorConstructor.Invoke(new object[]
            {
                preferences,
                (Func<int>)(() => 0),
                null,
                null
            });
            using var supervisorLifetime = (IDisposable)supervisor;
            var markConnected = supervisorType.GetMethod("MarkPhysicalConnected", InstanceFlags);
            var supervisorSnapshot = supervisorType.GetMethod("Snapshot", InstanceFlags);
            Require(markConnected != null && supervisorSnapshot != null,
                "Relay supervisor generation hooks are unavailable.");
            var relayGeneration = (long)markConnected.Invoke(supervisor, Array.Empty<object>());
            var relayBefore = supervisorSnapshot.Invoke(supervisor, Array.Empty<object>());

            SetField(runtimeType, runtime, "config", config);
            SetField(runtimeType, runtime, "credentials", credentials);
            SetField(runtimeType, runtime, "http", http);
            SetField(runtimeType, runtime, "tokenRefreshGate", new SemaphoreSlim(1, 1));
            SetField(runtimeType, runtime, "authorizedRecipientGate", new SemaphoreSlim(1, 1));
            SetField(runtimeType, runtime, "authorityExpiresUtc", DateTime.MinValue);
            SetField(runtimeType, runtime, "tokenEndpoint", "https://oidc.unit.test/token");
            SetField(runtimeType, runtime, "lifecycleSync", new object());
            SetField(runtimeType, runtime, "statusSync", new object());
            SetField(runtimeType, runtime, "connectionSupervisor", supervisor);
            SetField(runtimeType, runtime, "socket", socket);
            SetField(runtimeType, runtime, "relayErrorCode", "unchanged-relay-state");

            var authorizedRecipients = NewRuntimeField(runtimeType, "authorizedRecipients");
            var subscribers = NewRuntimeField(runtimeType, "subscribers");
            var routes = NewRuntimeField(runtimeType, "connectionSubscriberRoutes");
            var pendingHistory = NewRuntimeField(runtimeType, "pendingHistorySessions");
            var terminalHistory = NewRuntimeField(runtimeType, "terminalHistorySessions");
            var persistedHistoryBacklog = NewRuntimeField(runtimeType, "persistedHistoryBacklog");
            var persistedHistorySessions = NewRuntimeField(
                runtimeType,
                "persistedHistoryBacklogSessions");
            var historyReconcile = NewRuntimeField(runtimeType, "historyReconcileSessions");
            var publicationStates = NewRuntimeField(runtimeType, "publicationStates");
            var historySignal = new SemaphoreSlim(0, 1);
            var metadataSignal = new SemaphoreSlim(0, 1);
            SetField(runtimeType, runtime, "authorizedRecipients", authorizedRecipients);
            SetField(runtimeType, runtime, "subscribers", subscribers);
            SetField(runtimeType, runtime, "connectionSubscriberRoutes", routes);
            SetField(runtimeType, runtime, "pendingHistorySessions", pendingHistory);
            SetField(runtimeType, runtime, "terminalHistorySessions", terminalHistory);
            SetField(runtimeType, runtime, "persistedHistoryBacklog", persistedHistoryBacklog);
            SetField(
                runtimeType,
                runtime,
                "persistedHistoryBacklogSessions",
                persistedHistorySessions);
            SetField(runtimeType, runtime, "historyReconcileSessions", historyReconcile);
            SetField(runtimeType, runtime, "publicationStates", publicationStates);
            SetField(runtimeType, runtime, "historySignal", historySignal);
            SetField(runtimeType, runtime, "metadataSignal", metadataSignal);

            var signingPublicKey = new byte[32];
            for (var index = 0; index < signingPublicKey.Length; index++)
                signingPublicKey[index] = checked((byte)(0xa0 + index));
            var authorized = createAuthorized.Invoke(null, new object[]
            {
                localIdentity.Exchange,
                pcDeviceId,
                recipientDeviceId,
                remoteIdentity.Exchange.PublicKey,
                signingPublicKey,
                7L
            });
            var invalidRecipient = Activator.CreateInstance(subscriberType, nonPublic: true);
            SetField(subscriberType, invalidRecipient, "DeviceId", Guid.NewGuid());
            SetField(subscriberType, invalidRecipient, "PairwiseKey", new byte[31]);
            SetField(subscriberType, invalidRecipient, "SigningPublicKey", new byte[32]);
            SetField(subscriberType, invalidRecipient, "KeyEpoch", 7L);
            var staleRecipient = Activator.CreateInstance(subscriberType, nonPublic: true);
            SetField(subscriberType, staleRecipient, "DeviceId", Guid.NewGuid());
            SetField(subscriberType, staleRecipient, "PairwiseKey", new byte[32]);
            SetField(subscriberType, staleRecipient, "SigningPublicKey", new byte[32]);
            SetField(subscriberType, staleRecipient, "KeyEpoch", 6L);
            Require(TryAdd(authorizedRecipients, recipientDeviceId, authorized) &&
                    TryAdd(
                        authorizedRecipients,
                        GetField(subscriberType, invalidRecipient, "DeviceId"),
                        invalidRecipient) &&
                    TryAdd(
                        authorizedRecipients,
                        GetField(subscriberType, staleRecipient, "DeviceId"),
                        staleRecipient),
                "Direct peer-provider authorization fixture could not be arranged.");

            SetField(subscriberType, authorized, "HighestAck", 51UL);
            SetField(subscriberType, authorized, "LastSentSequence", 53UL);
            SetField(subscriberType, authorized, "InFlightWireBytes", 6UL);
            var sentFrames = (IDictionary)GetField(subscriberType, authorized, "SentFrames");
            var sent52 = new byte[] { 1, 2, 3 };
            var sent53 = new byte[] { 4, 5, 6 };
            sentFrames.Add(52UL, sent52);
            sentFrames.Add(53UL, sent53);
            var generationFrames = GetField(
                subscriberType,
                authorized,
                "GenerationInFlightSequences");
            generationFrames.GetType().GetMethod("Add").Invoke(
                generationFrames,
                new object[] { 52UL });
            generationFrames.GetType().GetMethod("Add").Invoke(
                generationFrames,
                new object[] { 53UL });

            var subscriberMapType = subscribers.GetType().GetGenericArguments()[1];
            var subscriberMap = Activator.CreateInstance(subscriberMapType);
            Require(TryAdd(subscriberMap, recipientDeviceId, authorized) &&
                    TryAdd(subscribers, sessionId, subscriberMap) &&
                    TryAdd(routes, sessionId.ToString("D") + ":" + recipientDeviceId.ToString("D"), authorized),
                "Direct peer-provider subscriber/route fixture could not be arranged.");
            Require(TryAdd(pendingHistory, historySessionId, (byte)0) &&
                    TryAdd(terminalHistory, historySessionId, (byte)0) &&
                    TryAdd(persistedHistorySessions, historySessionId, (byte)0) &&
                    TryAdd(historyReconcile, historySessionId, (byte)0),
                "Direct peer-provider history fixture could not be arranged.");
            persistedHistoryBacklog.GetType().GetMethod("Enqueue").Invoke(
                persistedHistoryBacklog,
                new object[] { historySessionId });
            var publicationState = Activator.CreateInstance(publicationStateType, nonPublic: true);
            SetField(publicationStateType, publicationState, "HasSize", true);
            SetField(publicationStateType, publicationState, "RestDirty", false);
            SetField(publicationStateType, publicationState, "RestRevision", 17UL);
            SetField(publicationStateType, publicationState, "Title", "unchanged-title");
            Require(TryAdd(publicationStates, sessionId, publicationState),
                "Direct peer-provider title fixture could not be arranged.");

            var storedPairwise = (byte[])GetField(subscriberType, authorized, "PairwiseKey");
            var storedSigning = (byte[])GetField(subscriberType, authorized, "SigningPublicKey");
            var expectedPairwise = (byte[])storedPairwise.Clone();
            var expectedSigning = (byte[])storedSigning.Clone();
            var initialSocketState = socket.State;
            var initialBacklogCount = CollectionCount(persistedHistoryBacklog);

            object directPeer = null;
            try
            {
                var task = (Task)snapshotPeers.Invoke(
                    runtime,
                    new object[] { CancellationToken.None });
                task.GetAwaiter().GetResult();
                var peers = (IEnumerable)task.GetType().GetProperty("Result").GetValue(task);
                var peerCount = 0;
                foreach (var peer in peers)
                {
                    directPeer = peer;
                    peerCount++;
                }
                Require(peerCount == 1 && directPeer != null,
                    "Direct peer provider did not filter invalid published authorizations.");

                Require(handler.RequestCount == 0,
                    "Direct peer provider performed shared HTTP/token refresh work.");
                Require((int)GetField(runtimeType, runtime, "reauthenticationRequired") == 0 &&
                        string.Equals(
                            (string)GetProperty(credentials, "AccessToken"),
                            "unchanged-stale-token",
                            StringComparison.Ordinal) &&
                        string.Equals(
                            (string)GetProperty(credentials, "RefreshToken"),
                            "must-not-refresh",
                            StringComparison.Ordinal) &&
                        (DateTime)GetProperty(credentials, "AccessTokenExpiresAtUtc") == accessTokenExpiry,
                    "Direct peer provider entered shared reauthentication/token mutation state.");
                var relayAfter = supervisorSnapshot.Invoke(supervisor, Array.Empty<object>());
                Require((long)GetProperty(relayBefore, "PhysicalGeneration") == relayGeneration &&
                        (long)GetProperty(relayAfter, "PhysicalGeneration") == relayGeneration &&
                        (bool)GetProperty(relayAfter, "PhysicalConnected") &&
                        ReferenceEquals(GetField(runtimeType, runtime, "socket"), socket) &&
                        socket.State == initialSocketState,
                    "Direct peer provider aborted or replaced the authoritative Relay generation.");

                Require(CollectionCount(authorizedRecipients) == 3 &&
                        ReferenceEquals(DictionaryValue(authorizedRecipients, recipientDeviceId), authorized) &&
                        CollectionCount(subscribers) == 1 &&
                        ReferenceEquals(DictionaryValue(subscriberMap, recipientDeviceId), authorized) &&
                        CollectionCount(routes) == 1 &&
                        (ulong)GetField(subscriberType, authorized, "HighestAck") == 51UL &&
                        (ulong)GetField(subscriberType, authorized, "LastSentSequence") == 53UL &&
                        (ulong)GetField(subscriberType, authorized, "InFlightWireBytes") == 6UL &&
                        sentFrames.Count == 2 &&
                        ReferenceEquals(sentFrames[52UL], sent52) &&
                        ReferenceEquals(sentFrames[53UL], sent53) &&
                        CollectionCount(generationFrames) == 2 &&
                        !(bool)GetField(subscriberType, authorized, "Removed"),
                    "Direct peer provider mutated Relay subscriber delivery state.");
                Require(CollectionCount(pendingHistory) == 1 &&
                        CollectionCount(terminalHistory) == 1 &&
                        CollectionCount(persistedHistorySessions) == 1 &&
                        CollectionCount(historyReconcile) == 1 &&
                        CollectionCount(persistedHistoryBacklog) == initialBacklogCount &&
                        historySignal.CurrentCount == 0 && metadataSignal.CurrentCount == 0 &&
                        !(bool)GetField(publicationStateType, publicationState, "RestDirty") &&
                        (ulong)GetField(publicationStateType, publicationState, "RestRevision") == 17UL &&
                        string.Equals(
                            (string)GetField(publicationStateType, publicationState, "Title"),
                            "unchanged-title",
                            StringComparison.Ordinal),
                    "Direct peer provider scheduled history replay or title republish.");

                var peerPairwise = (byte[])GetProperty(directPeer, "PairwiseKey");
                var peerSigning = (byte[])GetProperty(directPeer, "SigningPublicKey");
                Require((Guid)GetProperty(directPeer, "DeviceId") == recipientDeviceId &&
                        (long)GetProperty(directPeer, "KeyEpoch") == 7L &&
                        !ReferenceEquals(peerPairwise, storedPairwise) &&
                        !ReferenceEquals(peerSigning, storedSigning) &&
                        peerPairwise.AsSpan().SequenceEqual(expectedPairwise) &&
                        peerSigning.AsSpan().SequenceEqual(expectedSigning),
                    "Direct peer provider did not return independently owned key copies.");
                peerPairwise[0] ^= 0xff;
                peerSigning[0] ^= 0xff;
                Require(storedPairwise.AsSpan().SequenceEqual(expectedPairwise) &&
                        storedSigning.AsSpan().SequenceEqual(expectedSigning),
                    "Mutating Direct peer key copies changed stored Relay subscriber keys.");
                ((IDisposable)directPeer).Dispose();
                Require(IsAllZero(peerPairwise) && IsAllZero(peerSigning) &&
                        GetProperty(directPeer, "PairwiseKey") == null &&
                        GetProperty(directPeer, "SigningPublicKey") == null &&
                        storedPairwise.AsSpan().SequenceEqual(expectedPairwise) &&
                        storedSigning.AsSpan().SequenceEqual(expectedSigning),
                    "Direct peer disposal did not zero only its independently owned key copies.");
                directPeer = null;
            }
            finally
            {
                if (directPeer is IDisposable disposablePeer) disposablePeer.Dispose();
                retireAuthorized.Invoke(null, new[] { authorized });
                retireAuthorized.Invoke(null, new[] { invalidRecipient });
                retireAuthorized.Invoke(null, new[] { staleRecipient });
                historySignal.Dispose();
                metadataSignal.Dispose();
                ((SemaphoreSlim)GetField(runtimeType, runtime, "tokenRefreshGate")).Dispose();
                ((SemaphoreSlim)GetField(runtimeType, runtime, "authorizedRecipientGate")).Dispose();
            }

            var emptyRuntime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            SetField(
                runtimeType,
                emptyRuntime,
                "authorizedRecipients",
                NewRuntimeField(runtimeType, "authorizedRecipients"));
            var emptyTask = (Task)snapshotPeers.Invoke(
                emptyRuntime,
                new object[] { CancellationToken.None });
            emptyTask.GetAwaiter().GetResult();
            Require(CollectionCount(emptyTask.GetType().GetProperty("Result").GetValue(emptyTask)) == 0,
                "An empty published authorization snapshot did not safely return no Direct peers.");
        }

        private static void ValidateNetworkAvailabilityRelayFirst(Type runtimeType)
        {
            var dispatch = runtimeType.GetMethod(
                "DispatchNetworkAvailabilityChange",
                StaticFlags);
            Require(dispatch != null, "Network-availability isolation hook is unavailable.");

            var throwingOrder = new ConcurrentQueue<string>();
            dispatch.Invoke(null, new object[]
            {
                true,
                (Action)(() => throwingOrder.Enqueue("relay")),
                (Action)(() =>
                {
                    throwingOrder.Enqueue("direct");
                    throw new InvalidOperationException("deterministic Direct failure");
                })
            });
            Require(string.Join(",", throwingOrder.ToArray()) == "relay,direct",
                "A throwing Direct callback escaped or ran before Relay network recovery.");

            using var relayObserved = new ManualResetEventSlim(false);
            using var directStarted = new ManualResetEventSlim(false);
            using var releaseDirect = new ManualResetEventSlim(false);
            var slowOrder = new ConcurrentQueue<string>();
            var slowDispatch = Task.Run(() => dispatch.Invoke(null, new object[]
            {
                true,
                (Action)(() =>
                {
                    slowOrder.Enqueue("relay");
                    relayObserved.Set();
                }),
                (Action)(() =>
                {
                    slowOrder.Enqueue("direct");
                    directStarted.Set();
                    releaseDirect.Wait();
                })
            }));
            var directWasStarted = directStarted.Wait(TimeSpan.FromSeconds(2));
            try
            {
                Require(directWasStarted && relayObserved.IsSet,
                    "Slow Direct work began before Relay received network availability.");
                Require(!slowDispatch.IsCompleted,
                    "Slow Direct ordering fixture did not retain its deterministic gate.");
                Require(string.Join(",", slowOrder.ToArray()) == "relay,direct",
                    "Slow Direct work was ordered ahead of Relay network recovery.");
            }
            finally
            {
                releaseDirect.Set();
            }
            slowDispatch.GetAwaiter().GetResult();

            var unavailableRelayCalls = 0;
            var unavailableDirectCalls = 0;
            dispatch.Invoke(null, new object[]
            {
                false,
                (Action)(() => unavailableRelayCalls++),
                (Action)(() => unavailableDirectCalls++)
            });
            Require(unavailableRelayCalls == 0 && unavailableDirectCalls == 1,
                "Network-unavailable notification changed the existing Direct-only behavior.");
        }

        private static void ValidateFullSubscriberWindowRecoversAcrossSubscribeGeneration(
            Type runtimeType)
        {
            var subscriberType = runtimeType.GetNestedType("Subscriber", BindingFlags.NonPublic);
            var resetCursor = runtimeType.GetMethod("ResetSubscriberCursor", StaticFlags);
            var applyFlowControl = runtimeType.GetMethod("TryApplyFlowControl", StaticFlags);
            var takeDrainRequest = runtimeType.GetMethod("TakeSubscriberDrainRequest", StaticFlags);
            var prepareFrame = runtimeType.GetMethod("PrepareNewSubscriberFrame", StaticFlags);
            var prepareCachedFrame = runtimeType.GetMethod(
                "PrepareCachedSubscriberFrameForSend",
                StaticFlags);
            var prepareReplay = runtimeType.GetMethod("TryPrepareSubscriberReplay", StaticFlags);
            var advanceAck = runtimeType.GetMethod("TryAdvanceSubscriberAck", StaticFlags);
            Require(subscriberType != null && resetCursor != null && applyFlowControl != null &&
                takeDrainRequest != null && prepareFrame != null && prepareCachedFrame != null &&
                prepareReplay != null && advanceAck != null,
                "Subscriber flow-control recovery hooks are unavailable.");

            var subscriber = Activator.CreateInstance(subscriberType, nonPublic: true);
            SetField(subscriberType, subscriber, "HighestAck", 51UL);
            SetField(subscriberType, subscriber, "LastSentSequence", 179UL);
            SetField(subscriberType, subscriber, "FlowPaused", false);
            SetField(subscriberType, subscriber, "FlowWindowEvents", 128);
            SetField(subscriberType, subscriber, "FlowWindowWireBytes", 4_096UL);
            SetField(subscriberType, subscriber, "FlowControlEpoch", 5UL);

            var sentFrames = (IDictionary)GetField(subscriberType, subscriber, "SentFrames");
            var generationFrames = GetField(
                subscriberType,
                subscriber,
                "GenerationInFlightSequences");
            var originalFrames = new byte[128][];
            for (ulong sequence = 52; sequence <= 179; sequence++)
            {
                var serialized = SubscriberFrameBytes(sequence, 32);
                originalFrames[checked((int)(sequence - 52))] = serialized;
                var retained = (byte[])prepareFrame.Invoke(
                    null,
                    new object[] { subscriber, sequence, serialized });
                Require(retained != null &&
                    StructuralComparisons.StructuralEqualityComparer.Equals(retained, serialized),
                    "The initial subscriber generation did not retain its ciphertext frame.");
            }
            Require(sentFrames.Count == 128 && CollectionCount(generationFrames) == 128 &&
                (ulong)GetField(subscriberType, subscriber, "InFlightWireBytes") == 4_096UL,
                "The arranged subscriber did not exactly fill both event and byte windows.");
            Require(prepareFrame.Invoke(
                    null,
                    new object[] { subscriber, 180UL, SubscriberFrameBytes(180, 32) }) == null,
                "A 129th frame exceeded the exact full subscriber window.");
            Require((bool)GetField(subscriberType, subscriber, "DrainRequested"),
                "A full subscriber window did not retain a replay drain request.");

            Require((bool)resetCursor.Invoke(null, new[] { subscriber, (object)51UL }),
                "A retained ACK 51 subscriber could not start a new SUBSCRIBE generation.");
            Require((ulong)GetField(subscriberType, subscriber, "HighestAck") == 51UL &&
                sentFrames.Count == 128,
                "SUBSCRIBE reset discarded the retained ACK or unacknowledged frame cache.");
            Require((ulong)GetField(subscriberType, subscriber, "FlowControlEpoch") == 0UL,
                "A new SUBSCRIBE generation retained stale flow-control epoch 5.");
            Require((bool)GetField(subscriberType, subscriber, "FlowPaused"),
                "A new SUBSCRIBE generation sent before fresh FLOW_CONTROL.");
            Require(CollectionCount(generationFrames) == 0 &&
                (ulong)GetField(subscriberType, subscriber, "InFlightWireBytes") == 0UL,
                "SUBSCRIBE reset retained the prior generation's delivery budget.");
            Require((ulong)GetField(subscriberType, subscriber, "LastSentSequence") == 179UL,
                "SUBSCRIBE reset discarded the retained last-sent sequence.");

            var pausedLookup = new object[] { subscriber, 52UL, false, false };
            Require(prepareCachedFrame.Invoke(null, pausedLookup) == null &&
                !(bool)pausedLookup[2] && !(bool)pausedLookup[3],
                "Retained ciphertext replayed before fresh FLOW_CONTROL.");

            var freshFlow = new FlowControlPayload
            {
                HighestContiguousSeq = 51,
                ControlEpoch = 1,
                Paused = false,
                MaxUnackedEvents = 128,
                MaxUnackedWireBytes = 4_096UL
            };
            Require((bool)applyFlowControl.Invoke(null, new object[] { subscriber, freshFlow }),
                "Fresh flow-control epoch 1 was rejected after SUBSCRIBE recovery.");
            Require(!(bool)GetField(subscriberType, subscriber, "FlowPaused"),
                "Fresh FLOW_CONTROL did not unpause the retained subscriber.");
            Require((bool)takeDrainRequest.Invoke(null, new[] { subscriber }),
                "Fresh FLOW_CONTROL did not release the retained replay drain.");
            var replayPreparation = new object[] { subscriber, 51UL, false, 0, 0L };
            Require((bool)prepareReplay.Invoke(null, replayPreparation) &&
                (int)replayPreparation[3] == 128 && (long)replayPreparation[4] == 4_096L,
                "Current-generation replay did not receive the fresh flow-control budget.");
            Require(!(bool)GetField(subscriberType, subscriber, "FlowPaused") &&
                (ulong)GetField(subscriberType, subscriber, "FlowControlEpoch") == 1UL,
                "Current-generation replay reset the fresh flow-control state.");
            SetField(subscriberType, subscriber, "ReplayInProgress", false);

            var conflicting = (byte[])originalFrames[0].Clone();
            conflicting[0] ^= 0x7f;
            var mismatch = CaptureInvocationFailure(
                prepareFrame,
                subscriber,
                52UL,
                conflicting);
            Require(mismatch is InvalidDataException &&
                mismatch.Message == "same sequence produced different ciphertext",
                "A retained sequence accepted different ciphertext in its new generation.");

            for (ulong sequence = 52; sequence <= 179; sequence++)
            {
                var lookup = new object[] { subscriber, sequence, false, false };
                var replayed = (byte[])prepareCachedFrame.Invoke(null, lookup);
                Require(replayed != null && !(bool)lookup[2] && !(bool)lookup[3] &&
                    StructuralComparisons.StructuralEqualityComparer.Equals(
                        replayed,
                        originalFrames[checked((int)(sequence - 52))]),
                    "A retained frame was not replayed as byte-identical ciphertext.");
            }
            Require(CollectionCount(generationFrames) == 128 &&
                (ulong)GetField(subscriberType, subscriber, "InFlightWireBytes") == 4_096UL,
                "Ciphertext replay did not refill the new generation's exact window.");

            var blockedLookup = new object[] { subscriber, 180UL, false, false };
            Require(prepareCachedFrame.Invoke(null, blockedLookup) == null &&
                !(bool)blockedLookup[2] && !(bool)blockedLookup[3],
                "The new generation exceeded its full replay window.");
            Require((bool)advanceAck.Invoke(null, new object[] { subscriber, 179UL }),
                "The replayed full window acknowledgement was rejected.");
            Require(sentFrames.Count == 0 && CollectionCount(generationFrames) == 0 &&
                (ulong)GetField(subscriberType, subscriber, "InFlightWireBytes") == 0UL,
                "ACK did not prune both retained ciphertext and generation delivery budget.");
            Require((bool)takeDrainRequest.Invoke(null, new[] { subscriber }),
                "ACK did not reopen a pending drain after the full replay window.");
            Require(prepareFrame.Invoke(
                    null,
                    new object[] { subscriber, 180UL, SubscriberFrameBytes(180, 32) }) != null,
                "ACK did not restore capacity for a new ciphertext frame.");
        }

        private static void ValidateRetainedRouteSkipsForcedRefresh(Type runtimeType)
        {
            var subscriberType = runtimeType.GetNestedType("Subscriber", BindingFlags.NonPublic);
            var configType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionAgentConfig",
                throwOnError: true);
            var getSubscriber = runtimeType.GetMethod("GetSubscriberAsync", InstanceFlags);
            Require(subscriberType != null && getSubscriber != null,
                "Subscriber route lookup hook is unavailable.");

            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            var config = Activator.CreateInstance(configType, nonPublic: true);
            configType.GetProperty("KeyEpoch").SetValue(config, 7L);
            SetField(runtimeType, runtime, "config", config);

            var subscribersField = runtimeType.GetField("subscribers", InstanceFlags);
            var routesField = runtimeType.GetField("connectionSubscriberRoutes", InstanceFlags);
            Require(subscribersField != null && routesField != null,
                "Connection subscriber route storage is unavailable.");
            SetField(runtimeType, runtime, "subscribers", Activator.CreateInstance(subscribersField.FieldType));
            var routes = Activator.CreateInstance(routesField.FieldType);
            SetField(runtimeType, runtime, "connectionSubscriberRoutes", routes);

            var sessionId = Guid.NewGuid();
            var deviceId = Guid.NewGuid();
            var subscriber = Activator.CreateInstance(subscriberType, nonPublic: true);
            SetField(subscriberType, subscriber, "DeviceId", deviceId);
            SetField(subscriberType, subscriber, "KeyEpoch", 7L);
            SetField(subscriberType, subscriber, "ExchangePublicKey", new byte[32]);
            SetField(subscriberType, subscriber, "PairwiseKey", new byte[32]);
            SetField(subscriberType, subscriber, "LastActivityUtc", DateTime.UtcNow);
            var routeKey = sessionId.ToString("N") + ":" + deviceId.ToString("N");
            var added = (bool)routesField.FieldType.GetMethod("TryAdd").Invoke(
                routes,
                new[] { (object)routeKey, subscriber });
            Require(added, "The retained subscriber route could not be arranged.");

            var task = (Task)getSubscriber.Invoke(
                runtime,
                new object[] { sessionId, deviceId, CancellationToken.None, true });
            task.GetAwaiter().GetResult();
            var result = task.GetType().GetProperty("Result").GetValue(task);
            Require(ReferenceEquals(result, subscriber),
                "A repeated SUBSCRIBE did not reuse its valid retained route.");
        }

        private static void ValidateRepeatedUnauthorizedRequiresReauthentication(
            Type runtimeType)
        {
            var sendAsync = runtimeType.GetMethod("SendAsync", InstanceFlags);
            Require(sendAsync != null, "Authenticated HTTP transport hook is unavailable.");

            var resourceRequests = 0;
            var refreshRequests = 0;
            var handler = new ScriptedHandler((request, _) =>
            {
                if (string.Equals(request.RequestUri.Host, "oidc.unit.test", StringComparison.Ordinal))
                {
                    refreshRequests++;
                    Require(request.Method == HttpMethod.Post &&
                        request.RequestUri.AbsolutePath == "/token",
                        "The forced token refresh used an unexpected endpoint.");
                    return Task.FromResult(JsonResponse(
                        "{\"access_token\":\"refreshed-unit-test-token\",\"refresh_token\":\"refreshed-unit-test-refresh\",\"expires_in\":3600}"));
                }
                resourceRequests++;
                Require(request.RequestUri.AbsolutePath == "/v1/auth-check",
                    "The 401 retry used an unexpected resource endpoint.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            });

            const string acceptanceRootName = "TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT";
            const string acceptanceCAName = "TERMLINK_ACCEPTANCE_CA_FILE";
            var previousRoot = Environment.GetEnvironmentVariable(acceptanceRootName);
            var previousCA = Environment.GetEnvironmentVariable(acceptanceCAName);
            var acceptanceRoot = Path.Combine(
                Path.GetTempPath(),
                "TermLink-auth-recovery-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(acceptanceRootName, acceptanceRoot);
            Environment.SetEnvironmentVariable(acceptanceCAName, typeof(Program).Assembly.Location);
            try
            {
                using var context = CreateAuthenticationRuntime(
                    runtimeType,
                    handler,
                    refreshToken: "unit-test-refresh",
                    tokenEndpoint: "https://oidc.unit.test/token");
                var error = CaptureAsyncFailure(
                    sendAsync,
                    context.Runtime,
                    HttpMethod.Get,
                    "/v1/auth-check",
                    Array.Empty<byte>(),
                    false,
                    null,
                    CancellationToken.None);
                RequireReauthentication(error, "Repeated HTTP 401");
                Require(resourceRequests == 2 && refreshRequests == 1 && handler.RequestCount == 3,
                    "Repeated HTTP 401 did not perform exactly one refresh and one authenticated retry.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(acceptanceRootName, previousRoot);
                Environment.SetEnvironmentVariable(acceptanceCAName, previousCA);
                try
                {
                    if (Directory.Exists(acceptanceRoot))
                        Directory.Delete(acceptanceRoot, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void ValidateForbiddenIdentityEndpointsRequireReauthentication(
            Type runtimeType)
        {
            var ensureRegistered = runtimeType.GetMethod("EnsureRegisteredAsync", InstanceFlags);
            var refreshStatus = runtimeType.GetMethod("RefreshStatusAsync", InstanceFlags);
            Require(ensureRegistered != null && refreshStatus != null,
                "Account/device identity endpoint hooks are unavailable.");

            var registrationHandler = new ScriptedHandler((request, _) =>
            {
                Require(request.Method == HttpMethod.Post &&
                    request.RequestUri.AbsolutePath == "/v1/devices/register",
                    "Registration reauthentication test used an unexpected endpoint.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            });
            using (var context = CreateAuthenticationRuntime(runtimeType, registrationHandler))
            {
                var error = CaptureAsyncFailure(
                    ensureRegistered,
                    context.Runtime,
                    CancellationToken.None);
                RequireReauthentication(error, "Forbidden device registration");
                Require(registrationHandler.RequestCount == 1,
                    "Forbidden device registration unexpectedly retried as a generic failure.");
            }

            var statusHandler = new ScriptedHandler((request, _) =>
            {
                Require(request.Method == HttpMethod.Get &&
                    request.RequestUri.AbsolutePath == "/v1/devices",
                    "Status reauthentication test used an unexpected endpoint.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            });
            using (var context = CreateAuthenticationRuntime(runtimeType, statusHandler))
            {
                var error = CaptureAsyncFailure(
                    refreshStatus,
                    context.Runtime,
                    CancellationToken.None);
                RequireReauthentication(error, "Forbidden account/device status refresh");
                Require(statusHandler.RequestCount == 1,
                    "Forbidden account/device status refresh unexpectedly retried as a generic failure.");
            }
        }

        private static void ValidateRejectedRefreshTokenStabilizesRuntime(Type runtimeType)
        {
            var getAccessToken = runtimeType.GetMethod("GetAccessTokenAsync", InstanceFlags);
            var requestReconnect = runtimeType.GetMethod("RequestReconnect", InstanceFlags);
            Require(getAccessToken != null && requestReconnect != null,
                "Authentication terminal-state hooks are unavailable.");

            var handler = new ScriptedHandler((request, _) =>
            {
                Require(request.Method == HttpMethod.Post &&
                    request.RequestUri.AbsolutePath == "/token",
                    "Rejected refresh-token validation used an unexpected endpoint.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            });
            using var context = CreateAuthenticationRuntime(
                runtimeType,
                handler,
                refreshToken: "revoked-unit-test-refresh",
                tokenEndpoint: "https://oidc.unit.test/token");

            var first = CaptureAsyncFailure(
                getAccessToken,
                context.Runtime,
                true,
                CancellationToken.None);
            RequireReauthentication(first, "Rejected refresh token");
            RequireAuthenticationTerminalState(runtimeType, context.Runtime);
            Require(handler.RequestCount == 1,
                "Rejected refresh token did not perform exactly one token request.");

            var second = CaptureAsyncFailure(
                getAccessToken,
                context.Runtime,
                true,
                CancellationToken.None);
            RequireReauthentication(second, "Repeated access after rejected refresh token");
            requestReconnect.Invoke(context.Runtime, Array.Empty<object>());
            RequireAuthenticationTerminalState(runtimeType, context.Runtime);
            Require(handler.RequestCount == 1,
                "Authentication terminal state allowed another token request or manual reconnect.");
        }

        private static void ValidateSharedAccountLeaseInput(Type runtimeType)
        {
            var leaseMatches = runtimeType.GetMethod("LeaseMatchesAsync", InstanceFlags);
            var clearConnection = runtimeType.GetMethod("ClearRealtimeConnectionState", InstanceFlags);
            Require(leaseMatches != null && clearConnection != null,
                "Shared-account input lease validation hooks are unavailable.");

            var sessionId = Guid.NewGuid();
            var holderId = Guid.NewGuid();
            var secondSenderId = Guid.NewGuid();
            var leaseId = Guid.NewGuid();
            var leaseRequests = 0;
            var expiresAt = DateTime.UtcNow.AddMinutes(1);
            using var holderExchange = DeviceExchangeKeyPair.Generate();
            using var secondExchange = DeviceExchangeKeyPair.Generate();
            var handler = new ScriptedHandler((request, token) =>
            {
                Require(request.Method == HttpMethod.Get &&
                    request.RequestUri.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal),
                    "Shared lease validation issued an unexpected Server request.");
                leaseRequests++;
                return Task.FromResult(LeaseResponse(holderId, leaseId, 7L, expiresAt));
            });
            using var context = CreateLeaseValidationRuntime(
                runtimeType,
                handler,
                (holderId, holderExchange.PublicKey),
                (secondSenderId, secondExchange.PublicKey));

            Require(InvokeLeaseMatches(
                leaseMatches, context.Runtime, sessionId, holderId, leaseId, 7L),
                "The Android device that acquired the lease could not use its fence.");
            Require(InvokeLeaseMatches(
                leaseMatches, context.Runtime, sessionId, secondSenderId, leaseId, 7L),
                "A second Server-authorized same-account Android was rejected by holder identity.");
            Require(leaseRequests == 1,
                "A shared lease cache was not reused across authorized same-account senders.");

            clearConnection.Invoke(context.Runtime, Array.Empty<object>());
            Require(InvokeLeaseMatches(
                leaseMatches, context.Runtime, sessionId, secondSenderId, leaseId, 7L),
                "A reconnected same-account Android was rejected after the old holder cache was cleared.");
            Require(leaseRequests == 2,
                "Realtime reconnect did not force a fresh Server lease observation.");
        }

        private static void ValidateLeaseFencingAndAuthorization(Type runtimeType)
        {
            var leaseMatches = runtimeType.GetMethod("LeaseMatchesAsync", InstanceFlags);
            Require(leaseMatches != null, "Input lease validation hook is unavailable.");

            var sessionId = Guid.NewGuid();
            var holderId = Guid.NewGuid();
            var authorizedSenderId = Guid.NewGuid();
            var unknownSenderId = Guid.NewGuid();
            var leaseId = Guid.NewGuid();
            var expiresAt = DateTime.UtcNow.AddMinutes(1);
            using var holderExchange = DeviceExchangeKeyPair.Generate();
            using var senderExchange = DeviceExchangeKeyPair.Generate();
            var grantBody = AuthorizedRecipientsBody(
                (holderId, holderExchange.PublicKey),
                (authorizedSenderId, senderExchange.PublicKey));
            var leaseRequests = 0;
            var grantRequests = 0;
            var handler = new ScriptedHandler((request, token) =>
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal))
                {
                    leaseRequests++;
                    return Task.FromResult(LeaseResponse(holderId, leaseId, 11L, expiresAt));
                }
                Require(request.RequestUri.AbsolutePath.EndsWith("/grants", StringComparison.Ordinal),
                    "Lease authorization issued an unexpected Server request.");
                grantRequests++;
                return Task.FromResult(JsonResponse(grantBody));
            });
            using (var context = CreateLeaseValidationRuntime(
                runtimeType,
                handler,
                (holderId, holderExchange.PublicKey),
                (authorizedSenderId, senderExchange.PublicKey)))
            {
                Require(!InvokeLeaseMatches(
                    leaseMatches, context.Runtime, sessionId, authorizedSenderId, Guid.NewGuid(), 11L),
                    "Input with the wrong lease ID crossed the active fence.");
                Require(!InvokeLeaseMatches(
                    leaseMatches, context.Runtime, sessionId, authorizedSenderId, leaseId, 10L),
                    "Input with a stale lease version crossed the active fence.");
                Require(!InvokeLeaseMatches(
                    leaseMatches, context.Runtime, sessionId, unknownSenderId, leaseId, 11L),
                    "An unknown or cross-account sender used another device's valid lease.");
                Require(leaseRequests == 2 && grantRequests == 1,
                    "Lease fencing or sender authorization did not use the expected Server checks.");
            }

            var expiredRequests = 0;
            var expiredHandler = new ScriptedHandler((request, token) =>
            {
                Require(request.RequestUri.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal),
                    "Expired lease validation issued an unexpected Server request.");
                expiredRequests++;
                return Task.FromResult(LeaseResponse(
                    holderId,
                    leaseId,
                    11L,
                    DateTime.UtcNow.AddSeconds(-1)));
            });
            using (var context = CreateLeaseValidationRuntime(
                runtimeType,
                expiredHandler,
                (authorizedSenderId, senderExchange.PublicKey)))
            {
                Require(!InvokeLeaseMatches(
                    leaseMatches, context.Runtime, sessionId, authorizedSenderId, leaseId, 11L),
                    "Expired lease input was accepted.");
                Require(expiredRequests == 1,
                    "Expired lease input did not validate against the Server fence.");
            }
        }

        private static void ValidateStaleLeaseReleaseDoesNotBlockPublication(Type runtimeType)
        {
            var publish = runtimeType.GetMethod("PublishAsync", InstanceFlags);
            var record = runtimeType.GetMethod(
                "RecordPublicationOutcome",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Require(publish != null && record != null,
                "Production publication hooks are unavailable.");

            var handler = new ScriptedHandler((request, token) =>
            {
                Require(request.Method == HttpMethod.Delete &&
                    request.RequestUri.AbsolutePath.EndsWith("/lease", StringComparison.Ordinal),
                    "Local input publication did not issue the expected lease release.");
                return Task.FromResult(ServerError(HttpStatusCode.Forbidden, "FORBIDDEN"));
            });
            using var context = CreatePublicationRuntime(runtimeType, handler);
            var sessionId = Guid.NewGuid();
            var localInput = CreateCommittedEvent(
                runtimeType,
                sessionId,
                11UL,
                "LocalInputObserved",
                LocalInputEpoch(runtimeType, 3UL));
            Require(InvokePublish(publish, context.Runtime, localInput, CancellationToken.None),
                "A stale 403 lease release blocked its LocalInputObserved publication.");
            record.Invoke(null, new object[]
            {
                context.PublishedSequences, sessionId, 11UL, true, false
            });

            var followingOutput = CreateCommittedEvent(
                runtimeType,
                sessionId,
                12UL,
                "Output",
                new byte[] { 0x41 });
            Require(InvokePublish(publish, context.Runtime, followingOutput, CancellationToken.None),
                "The event following a stale lease release was not processed.");
            record.Invoke(null, new object[]
            {
                context.PublishedSequences, sessionId, 12UL, true, false
            });
            Require(context.PublishedSequences.TryGetValue(sessionId, out var published) &&
                published == 12UL && handler.RequestCount == 1,
                "A stale lease release prevented the publication cursor from advancing.");
        }

        private static void ValidateLeaseReleaseDoesNotSwallowUnexpectedFailures(Type runtimeType)
        {
            var publish = runtimeType.GetMethod("PublishAsync", InstanceFlags);
            Require(publish != null, "Production publication hook is unavailable.");

            var pendingHandler = new ScriptedHandler((request, token) =>
                Task.FromResult(ServerError(HttpStatusCode.Forbidden, "DEVICE_PENDING")));
            using (var context = CreatePublicationRuntime(runtimeType, pendingHandler))
            {
                var error = CapturePublishFailure(
                    publish,
                    context.Runtime,
                    CreateCommittedEvent(
                        runtimeType,
                        Guid.NewGuid(),
                        1UL,
                        "LocalInputObserved",
                        LocalInputEpoch(runtimeType, 1UL)),
                    CancellationToken.None);
                Require(error != null &&
                    error.GetType().Name == "ProductionServerResponseException" &&
                    string.Equals(
                        (string)error.GetType().GetProperty("ErrorCode").GetValue(error),
                        "LOCAL_INPUT_LEASE_RELEASE_DEVICE_PENDING",
                        StringComparison.Ordinal),
                    "A device-level 403 lost its actionable lease-release diagnostic.");
            }

            var canceledHandler = new ScriptedHandler((request, token) =>
                Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true)));
            using (var context = CreatePublicationRuntime(runtimeType, canceledHandler))
            {
                var error = CapturePublishFailure(
                    publish,
                    context.Runtime,
                    CreateCommittedEvent(
                        runtimeType,
                        Guid.NewGuid(),
                        1UL,
                        "LocalInputObserved",
                        LocalInputEpoch(runtimeType, 1UL)),
                    CancellationToken.None);
                Require(error is OperationCanceledException,
                    "Unexpected lease-release cancellation was swallowed.");
            }

            var programErrorHandler = new ScriptedHandler((request, token) =>
                Task.FromException<HttpResponseMessage>(
                    new InvalidOperationException("deterministic handler failure")));
            using (var context = CreatePublicationRuntime(runtimeType, programErrorHandler))
            {
                var error = CapturePublishFailure(
                    publish,
                    context.Runtime,
                    CreateCommittedEvent(
                        runtimeType,
                        Guid.NewGuid(),
                        1UL,
                        "LocalInputObserved",
                        LocalInputEpoch(runtimeType, 1UL)),
                    CancellationToken.None);
                Require(error is InvalidOperationException,
                    "Unexpected lease-release program failure was swallowed.");
            }
        }

        private static AuthenticationRuntimeContext CreateAuthenticationRuntime(
            Type runtimeType,
            HttpMessageHandler handler,
            string refreshToken = null,
            string tokenEndpoint = null)
        {
            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            var configType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionAgentConfig",
                throwOnError: true);
            var credentialsType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionCredentials",
                throwOnError: true);
            var config = Activator.CreateInstance(configType, nonPublic: true);
            SetProperty(configType, config, "ServerUrl", "https://unit.test");
            SetProperty(configType, config, "PcId", Guid.NewGuid());
            SetProperty(configType, config, "PcName", "Unit test PC");
            SetProperty(configType, config, "AgentVersion", "unit-test");
            SetProperty(configType, config, "KeyEpoch", 1L);
            SetProperty(configType, config, "OIDCIssuer", "https://oidc.unit.test");
            SetProperty(configType, config, "OIDCClientId", "unit-test-client");

            var identity = DeviceIdentity.Generate();
            var credentials = RuntimeHelpers.GetUninitializedObject(credentialsType);
            SetField(credentialsType, credentials, "tokenSync", new object());
            SetProperty(credentialsType, credentials, "DeviceId", Guid.NewGuid());
            SetProperty(credentialsType, credentials, "Identity", identity);
            SetProperty(credentialsType, credentials, "AccessToken", "unit-test-token");
            SetProperty(credentialsType, credentials, "RefreshToken", refreshToken);
            SetProperty(credentialsType, credentials, "AccessTokenExpiresAtUtc", DateTime.MaxValue);

            var http = new HttpClient(handler);
            SetField(runtimeType, runtime, "config", config);
            SetField(runtimeType, runtime, "credentials", credentials);
            SetField(runtimeType, runtime, "http", http);
            SetField(runtimeType, runtime, "tokenRefreshGate", new SemaphoreSlim(1, 1));
            SetField(runtimeType, runtime, "statusRefreshGate", new SemaphoreSlim(1, 1));
            SetField(runtimeType, runtime, "lifecycleSync", new object());
            SetField(runtimeType, runtime, "statusSync", new object());
            if (tokenEndpoint != null)
                SetField(runtimeType, runtime, "tokenEndpoint", tokenEndpoint);
            return new AuthenticationRuntimeContext(runtime, http, identity);
        }

        private static LeaseValidationRuntimeContext CreateLeaseValidationRuntime(
            Type runtimeType,
            HttpMessageHandler handler,
            params (Guid DeviceId, byte[] PublicKey)[] authorizedDevices)
        {
            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            var configType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionAgentConfig",
                throwOnError: true);
            var credentialsType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionCredentials",
                throwOnError: true);
            var config = Activator.CreateInstance(configType, nonPublic: true);
            var pcId = Guid.NewGuid();
            SetProperty(configType, config, "ServerUrl", "https://unit.test");
            SetProperty(configType, config, "PcId", pcId);
            SetProperty(configType, config, "KeyEpoch", 1L);

            var identity = DeviceIdentity.Generate();
            var pcDeviceId = Guid.NewGuid();
            var credentials = RuntimeHelpers.GetUninitializedObject(credentialsType);
            SetField(credentialsType, credentials, "tokenSync", new object());
            SetProperty(credentialsType, credentials, "DeviceId", pcDeviceId);
            SetProperty(credentialsType, credentials, "Identity", identity);
            SetProperty(credentialsType, credentials, "AccessToken", "unit-test-token");
            SetProperty(credentialsType, credentials, "AccessTokenExpiresAtUtc", DateTime.MaxValue);

            var http = new HttpClient(handler);
            SetField(runtimeType, runtime, "config", config);
            SetField(runtimeType, runtime, "credentials", credentials);
            SetField(runtimeType, runtime, "http", http);
            SetField(runtimeType, runtime, "leaseStatuses", NewRuntimeField(runtimeType, "leaseStatuses"));
            SetField(runtimeType, runtime, "authorizedRecipients", NewRuntimeField(runtimeType, "authorizedRecipients"));
            SetField(runtimeType, runtime, "subscribers", NewRuntimeField(runtimeType, "subscribers"));
            SetField(runtimeType, runtime, "connectionSubscriberRoutes", NewRuntimeField(runtimeType, "connectionSubscriberRoutes"));
            SetField(runtimeType, runtime, "authorizedRecipientGate", new SemaphoreSlim(1, 1));
            SetField(runtimeType, runtime, "authorityExpiresUtc", DateTime.UtcNow.AddMinutes(5));

            var createAuthorized = runtimeType.GetMethod(
                "CreateAuthorizedSubscriberWithSigningKey",
                StaticFlags);
            var retireAuthorized = runtimeType.GetMethod("RetireSubscriber", StaticFlags);
            var recipients = GetField(runtimeType, runtime, "authorizedRecipients");
            Require(createAuthorized != null && retireAuthorized != null,
                "Authorized recipient construction hooks are unavailable.");
            var retainedRecipients = new object[authorizedDevices.Length];
            for (var index = 0; index < authorizedDevices.Length; index++)
            {
                var item = authorizedDevices[index];
                var recipient = createAuthorized.Invoke(null, new object[]
                {
                    identity.Exchange,
                    pcDeviceId,
                    item.DeviceId,
                    item.PublicKey,
                    item.PublicKey,
                    1L
                });
                var added = (bool)recipients.GetType().GetMethod("TryAdd").Invoke(
                    recipients,
                    new[] { (object)item.DeviceId, recipient });
                Require(added, "The authorized lease sender could not be arranged.");
                retainedRecipients[index] = recipient;
            }
            return new LeaseValidationRuntimeContext(
                runtime,
                http,
                identity,
                retireAuthorized,
                retainedRecipients);
        }

        private static bool InvokeLeaseMatches(
            MethodInfo leaseMatches,
            object runtime,
            Guid sessionId,
            Guid senderId,
            Guid leaseId,
            long leaseVersion)
        {
            var task = (Task)leaseMatches.Invoke(runtime, new object[]
            {
                sessionId,
                senderId,
                leaseId,
                leaseVersion,
                CancellationToken.None
            });
            task.GetAwaiter().GetResult();
            return (bool)task.GetType().GetProperty("Result").GetValue(task);
        }

        private static HttpResponseMessage LeaseResponse(
            Guid holderId,
            Guid leaseId,
            long version,
            DateTime expiresAtUtc)
        {
            return JsonResponse(
                "{\"holder_device_id\":\"" + holderId.ToString("D") +
                "\",\"lease_id\":\"" + leaseId.ToString("D") +
                "\",\"version\":" + version +
                ",\"expires_at\":\"" + expiresAtUtc.ToUniversalTime().ToString("O") + "\"}");
        }

        private static string AuthorizedRecipientsBody(
            params (Guid DeviceId, byte[] PublicKey)[] recipients)
        {
            var body = new StringBuilder("{\"key_epoch\":1,\"items\":[");
            for (var index = 0; index < recipients.Length; index++)
            {
                if (index > 0) body.Append(',');
                body.Append("{\"device_id\":\"")
                    .Append(recipients[index].DeviceId.ToString("D"))
                    .Append("\",\"device_type\":\"ANDROID\",\"exchange_public_key\":\"")
                    .Append(Convert.ToBase64String(recipients[index].PublicKey))
                    .Append("\",\"signing_public_key\":\"")
                    .Append(Convert.ToBase64String(recipients[index].PublicKey))
                    .Append("\"}");
            }
            return body.Append("]}").ToString();
        }

        private static HttpResponseMessage JsonResponse(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static PublicationRuntimeContext CreatePublicationRuntime(
            Type runtimeType,
            HttpMessageHandler handler)
        {
            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            var configType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionAgentConfig",
                throwOnError: true);
            var credentialsType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionCredentials",
                throwOnError: true);
            var config = Activator.CreateInstance(configType, nonPublic: true);
            SetProperty(configType, config, "ServerUrl", "https://unit.test");
            SetProperty(configType, config, "KeyEpoch", 1L);

            var identity = DeviceIdentity.Generate();
            var credentials = RuntimeHelpers.GetUninitializedObject(credentialsType);
            SetField(credentialsType, credentials, "tokenSync", new object());
            SetProperty(credentialsType, credentials, "DeviceId", Guid.NewGuid());
            SetProperty(credentialsType, credentials, "Identity", identity);
            SetProperty(credentialsType, credentials, "AccessToken", "unit-test-token");
            SetProperty(credentialsType, credentials, "AccessTokenExpiresAtUtc", DateTime.MaxValue);

            var http = new HttpClient(handler);
            var publishedSequences = new ConcurrentDictionary<Guid, ulong>();
            SetField(runtimeType, runtime, "config", config);
            SetField(runtimeType, runtime, "credentials", credentials);
            SetField(runtimeType, runtime, "http", http);
            SetField(runtimeType, runtime, "publishedSequences", publishedSequences);
            SetField(runtimeType, runtime, "publicationStates", NewRuntimeField(runtimeType, "publicationStates"));
            SetField(runtimeType, runtime, "subscribers", NewRuntimeField(runtimeType, "subscribers"));
            return new PublicationRuntimeContext(runtime, publishedSequences, http, identity);
        }

        private static object NewRuntimeField(Type runtimeType, string name)
        {
            var field = runtimeType.GetField(name, InstanceFlags);
            Require(field != null, "Missing runtime field: " + name);
            return Activator.CreateInstance(field.FieldType);
        }

        private static object CreateCommittedEvent(
            Type runtimeType,
            Guid sessionId,
            ulong sequence,
            string frameTypeName,
            byte[] payload)
        {
            var eventType = runtimeType.GetNestedType("CommittedEvent", BindingFlags.NonPublic);
            var frameType = runtimeType.Assembly.GetType("TermLink.LocalIpc.LocalFrame", throwOnError: true);
            var frameKindType = runtimeType.Assembly.GetType("TermLink.LocalIpc.LocalFrameType", throwOnError: true);
            var frameKind = Enum.Parse(frameKindType, frameTypeName);
            var frame = Activator.CreateInstance(
                frameType,
                InstanceFlags,
                binder: null,
                args: new object[] { frameKind, sessionId, sequence, payload },
                culture: null);
            var committed = Activator.CreateInstance(eventType, nonPublic: true);
            var shellField = eventType.GetField("ShellType", InstanceFlags);
            SetField(eventType, committed, "ShellType", Enum.ToObject(shellField.FieldType, 5));
            SetField(eventType, committed, "Frame", frame);
            return committed;
        }

        private static byte[] LocalInputEpoch(Type runtimeType, ulong epoch)
        {
            var codecType = runtimeType.Assembly.GetType(
                "TermLink.LocalIpc.LocalPayloadCodec",
                throwOnError: true);
            return (byte[])codecType.GetMethod(
                "LocalInputEpoch",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(
                    null,
                    new object[] { epoch });
        }

        private static bool InvokePublish(
            MethodInfo publish,
            object runtime,
            object committed,
            CancellationToken token)
        {
            var task = (Task)publish.Invoke(runtime, new[] { null, committed, (object)token });
            task.GetAwaiter().GetResult();
            return (bool)task.GetType().GetProperty("Result").GetValue(task);
        }

        private static Exception CapturePublishFailure(
            MethodInfo publish,
            object runtime,
            object committed,
            CancellationToken token)
        {
            try
            {
                InvokePublish(publish, runtime, committed, token);
                return null;
            }
            catch (TargetInvocationException error)
            {
                return error.InnerException;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        private static Exception CaptureAsyncFailure(
            MethodInfo method,
            object runtime,
            params object[] arguments)
        {
            try
            {
                var task = (Task)method.Invoke(runtime, arguments);
                task.GetAwaiter().GetResult();
                return null;
            }
            catch (TargetInvocationException error)
            {
                return error.InnerException;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        private static void RequireReauthentication(Exception error, string operation)
        {
            Require(error != null &&
                error.GetType().Name == "ProductionReauthenticationRequiredException",
                operation + " did not stabilize as an account reauthentication requirement; got " +
                (error == null ? "success" : error.GetType().FullName) + ".");
        }

        private static void RequireAuthenticationTerminalState(Type runtimeType, object runtime)
        {
            var terminal = (int)runtimeType.GetField("reauthenticationRequired", InstanceFlags).GetValue(runtime);
            var relayState = runtimeType.GetField("relayState", InstanceFlags).GetValue(runtime).ToString();
            var errorCode = (string)runtimeType.GetField("relayErrorCode", InstanceFlags).GetValue(runtime);
            Require(terminal == 1 && relayState == "Failed" && errorCode == "ACCOUNT_REAUTH_REQUIRED",
                "Rejected authentication did not remain in the actionable terminal state.");
        }

        private static HttpResponseMessage ServerError(HttpStatusCode status, string code)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    "{\"error\":{\"code\":\"" + code + "\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private static byte[] SubscriberFrameBytes(ulong sequence, int length)
        {
            if (length < sizeof(ulong)) throw new ArgumentOutOfRangeException(nameof(length));
            var result = new byte[length];
            for (var index = 0; index < result.Length; index++)
                result[index] = checked((byte)((sequence + (ulong)index) & 0xff));
            return result;
        }

        private static int CollectionCount(object collection)
        {
            Require(collection != null, "Subscriber collection is unavailable.");
            var property = collection.GetType().GetProperty("Count", InstanceFlags);
            Require(property != null, "Subscriber collection count is unavailable.");
            return (int)property.GetValue(collection);
        }

        private static Exception CaptureInvocationFailure(
            MethodInfo method,
            params object[] arguments)
        {
            try
            {
                method.Invoke(null, arguments);
                return null;
            }
            catch (TargetInvocationException error)
            {
                return error.InnerException ?? error;
            }
        }

        private static object GetField(Type type, object instance, string name)
        {
            var field = type.GetField(name, InstanceFlags);
            Require(field != null, "Missing subscriber field: " + name);
            return field.GetValue(instance);
        }

        private static object GetProperty(object instance, string name)
        {
            Require(instance != null, "Property owner is unavailable: " + name);
            var property = instance.GetType().GetProperty(name, InstanceFlags);
            Require(property != null, "Missing runtime property: " + name);
            return property.GetValue(instance);
        }

        private static object DictionaryValue(object dictionary, object key)
        {
            Require(dictionary != null, "Dictionary is unavailable.");
            var item = dictionary.GetType().GetProperty("Item", InstanceFlags);
            Require(item != null, "Dictionary indexer is unavailable.");
            return item.GetValue(dictionary, new[] { key });
        }

        private static bool TryAdd(object dictionary, object key, object value)
        {
            Require(dictionary != null, "Dictionary is unavailable.");
            var tryAdd = dictionary.GetType().GetMethod("TryAdd", InstanceFlags);
            Require(tryAdd != null, "Dictionary TryAdd is unavailable.");
            return (bool)tryAdd.Invoke(dictionary, new[] { key, value });
        }

        private static void SetField(Type type, object instance, string name, object value)
        {
            var field = type.GetField(name, InstanceFlags);
            Require(field != null, "Missing runtime field: " + name);
            field.SetValue(instance, value);
        }

        private static void SetProperty(Type type, object instance, string name, object value)
        {
            var property = type.GetProperty(name, InstanceFlags);
            Require(property != null, "Missing runtime property: " + name);
            property.SetValue(instance, value);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class PublicationRuntimeContext : IDisposable
        {
            public PublicationRuntimeContext(
                object runtime,
                ConcurrentDictionary<Guid, ulong> publishedSequences,
                HttpClient http,
                DeviceIdentity identity)
            {
                Runtime = runtime;
                PublishedSequences = publishedSequences;
                Http = http;
                Identity = identity;
            }

            public object Runtime { get; }
            public ConcurrentDictionary<Guid, ulong> PublishedSequences { get; }
            private HttpClient Http { get; }
            private DeviceIdentity Identity { get; }

            public void Dispose()
            {
                Http.Dispose();
                Identity.Dispose();
            }
        }

        private sealed class AuthenticationRuntimeContext : IDisposable
        {
            private readonly HttpClient http;
            private readonly DeviceIdentity identity;

            public AuthenticationRuntimeContext(
                object runtime,
                HttpClient http,
                DeviceIdentity identity)
            {
                Runtime = runtime;
                this.http = http;
                this.identity = identity;
            }

            public object Runtime { get; }

            public void Dispose()
            {
                http.Dispose();
                identity.Dispose();
            }
        }

        private sealed class LeaseValidationRuntimeContext : IDisposable
        {
            private readonly HttpClient http;
            private readonly DeviceIdentity identity;
            private readonly MethodInfo retireRecipient;
            private readonly object[] recipients;

            public LeaseValidationRuntimeContext(
                object runtime,
                HttpClient http,
                DeviceIdentity identity,
                MethodInfo retireRecipient,
                object[] recipients)
            {
                Runtime = runtime;
                this.http = http;
                this.identity = identity;
                this.retireRecipient = retireRecipient;
                this.recipients = recipients;
            }

            public object Runtime { get; }

            public void Dispose()
            {
                foreach (var recipient in recipients)
                    retireRecipient.Invoke(null, new[] { recipient });
                http.Dispose();
                identity.Dispose();
            }
        }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;

            public ScriptedHandler(
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
            {
                this.send = send ?? throw new ArgumentNullException(nameof(send));
            }

            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                return send(request, cancellationToken);
            }
        }
    }
}
