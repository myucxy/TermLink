using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
                ValidateSubscribeGenerationResetsFlowEpoch(runtimeType);
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

        private static void ValidateSubscribeGenerationResetsFlowEpoch(Type runtimeType)
        {
            var subscriberType = runtimeType.GetNestedType("Subscriber", BindingFlags.NonPublic);
            var resetCursor = runtimeType.GetMethod("ResetSubscriberCursor", StaticFlags);
            var applyFlowControl = runtimeType.GetMethod("TryApplyFlowControl", StaticFlags);
            var takeDrainRequest = runtimeType.GetMethod("TakeSubscriberDrainRequest", StaticFlags);
            Require(subscriberType != null && resetCursor != null && applyFlowControl != null &&
                takeDrainRequest != null,
                "Subscriber flow-control recovery hooks are unavailable.");

            var subscriber = Activator.CreateInstance(subscriberType, nonPublic: true);
            SetField(subscriberType, subscriber, "HighestAck", 51UL);
            SetField(subscriberType, subscriber, "LastSentSequence", 57UL);
            SetField(subscriberType, subscriber, "FlowPaused", true);
            SetField(subscriberType, subscriber, "FlowWindowEvents", 128);
            SetField(subscriberType, subscriber, "FlowWindowWireBytes", 512UL * 1024UL);
            SetField(subscriberType, subscriber, "FlowControlEpoch", 5UL);
            SetField(subscriberType, subscriber, "InFlightWireBytes", 48UL);
            SetField(subscriberType, subscriber, "DrainRequested", true);

            var sentFrames = (IDictionary)GetField(subscriberType, subscriber, "SentFrames");
            for (ulong sequence = 52; sequence <= 57; sequence++)
                sentFrames.Add(sequence, new byte[8]);

            Require((bool)resetCursor.Invoke(null, new[] { subscriber, (object)51UL }),
                "A retained ACK 51 subscriber could not start a new SUBSCRIBE generation.");
            Require((ulong)GetField(subscriberType, subscriber, "HighestAck") == 51UL &&
                sentFrames.Count == 6,
                "SUBSCRIBE reset discarded the retained ACK or unacknowledged frame cache.");
            Require((ulong)GetField(subscriberType, subscriber, "FlowControlEpoch") == 0UL,
                "A new SUBSCRIBE generation retained stale flow-control epoch 5.");
            Require((bool)GetField(subscriberType, subscriber, "FlowPaused"),
                "SUBSCRIBE reset cleared the retained paused state before FLOW_CONTROL.");
            Require((int)GetField(subscriberType, subscriber, "FlowWindowEvents") == 128 &&
                (ulong)GetField(subscriberType, subscriber, "FlowWindowWireBytes") == 512UL * 1024UL,
                "SUBSCRIBE reset discarded the retained flow-control window.");
            Require((ulong)GetField(subscriberType, subscriber, "LastSentSequence") == 57UL,
                "SUBSCRIBE reset discarded the retained last-sent sequence.");

            var freshFlow = new FlowControlPayload
            {
                HighestContiguousSeq = 51,
                ControlEpoch = 1,
                Paused = false,
                MaxUnackedEvents = 128,
                MaxUnackedWireBytes = 512UL * 1024UL
            };
            Require((bool)applyFlowControl.Invoke(null, new object[] { subscriber, freshFlow }),
                "Fresh flow-control epoch 1 was rejected after SUBSCRIBE recovery.");
            Require(!(bool)GetField(subscriberType, subscriber, "FlowPaused"),
                "Fresh FLOW_CONTROL did not unpause the retained subscriber.");
            Require((bool)takeDrainRequest.Invoke(null, new[] { subscriber }),
                "Fresh FLOW_CONTROL did not release the retained replay drain.");
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

            var createAuthorized = runtimeType.GetMethod("CreateAuthorizedSubscriber", StaticFlags);
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

        private static object GetField(Type type, object instance, string name)
        {
            var field = type.GetField(name, InstanceFlags);
            Require(field != null, "Missing subscriber field: " + name);
            return field.GetValue(instance);
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
