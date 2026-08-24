using System;
using System.Collections;
using System.Collections.Concurrent;
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
                ValidateStaleLeaseReleaseDoesNotBlockPublication(runtimeType);
                ValidateLeaseReleaseDoesNotSwallowUnexpectedFailures(runtimeType);
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
