using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol.Realtime.V1;
using TermLink.Agent;

namespace TermLink.ProductionSessionFaultIsolation.Validation
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            try
            {
                await ValidateTwoSessionIsolationAsync().ConfigureAwait(false);
                ValidateExactRouteRetirement();
                ValidateRetiredHostCatalog();
                await ValidateConnectionFatalSignalsEscapeAsync().ConfigureAwait(false);
                Console.WriteLine("Production session fault isolation validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidateRetiredHostCatalog()
        {
            var root = Path.Combine(Path.GetTempPath(), "termlink-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var sessionId = Guid.NewGuid();
                var registrations = new DpapiRegistryRegistrationStore(Path.Combine(root, "registrations.dpapi"));
                registrations.Upsert(new SessionRegistration(sessionId, "catalog-test", RandomNumberGenerator.GetBytes(32),
                    int.MaxValue, ManagedShellType.PowerShell7, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).AsRetired());
                using var registry = new SessionRegistry(root, 1200, 1024 * 1024, 20);
                Require(registry.GetCatalogSummaries().Single().Lifecycle == SessionAgentLifecycle.HostLost,
                    "A retired registration became a live process after restart.");
                var history = new ProductionTerminalHistoryStore(Path.Combine(root, "history"), Guid.NewGuid());
                history.AppendCommittedFrame(new LocalFrame(LocalFrameType.Lifecycle, sessionId, 1,
                    LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0)));
                history.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, sessionId, 2, new byte[] { 65 }));
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(ProductionAgentRuntime);
                var runtime = RuntimeHelpers.GetUninitializedObject(type);
                foreach (var name in new[] { "catalogSync", "catalogEntries", "catalogCursors", "destroyedHistorySessions" })
                {
                    var field = type.GetField(name, flags);
                    field.SetValue(runtime, Activator.CreateInstance(field.FieldType));
                }
                type.GetField("catalogHostInstance", flags).SetValue(runtime, Guid.NewGuid());
                type.GetField("registry", flags).SetValue(runtime, registry);
                type.GetField("terminalHistoryStore", flags).SetValue(runtime, history);
                var request = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
                var build = type.GetMethod("BuildSessionCatalog", flags);
                var catalog = (SessionCatalogResponsePayload)build.Invoke(runtime, new object[] { request });
                Require(catalog.RequestId == request && catalog.Sessions.Count == 1 &&
                    !catalog.Sessions[0].ProcessAlive && catalog.Sessions[0].Lifecycle == "INTERRUPTED" &&
                    catalog.Sessions[0].LastOutputSeq == 2,
                    "Retained RUNNING history overrode retired process authority.");
                history.AppendCommittedFrame(new LocalFrame(LocalFrameType.Lifecycle, sessionId, 3,
                    LocalPayloadCodec.Lifecycle(LocalLifecycleState.Ended, 0)));
                catalog = (SessionCatalogResponsePayload)build.Invoke(runtime, new object[] { request });
                Require(catalog.Sessions[0].Lifecycle == "EXITED", "Catalog did not observe the final local lifecycle.");
                Require(ProductionAgentRuntime.CatalogLifecycle(true, "INTERRUPTED") == "RUNNING",
                    "Connected host was not reported running.");
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        private static async Task ValidateTwoSessionIsolationAsync()
        {
            using var supervisor = CreateSupervisor();
            var generation = supervisor.MarkPhysicalConnected();
            var sender = Guid.NewGuid();
            var first = new FakeSessionRoute(Guid.NewGuid(), sender);
            var second = new FakeSessionRoute(Guid.NewGuid(), sender);
            var secondKey = (byte[])second.PairwiseKey.Clone();
            var firstFaults = 0;

            await ProductionSessionRouteFaultBoundary.ExecuteAsync(
                () => Task.FromException(new CryptographicException("tampered route payload")),
                error =>
                {
                    firstFaults++;
                    supervisor.ObserveSessionSoftFailure();
                    first.Retire();
                },
                CancellationToken.None).ConfigureAwait(false);

            Require(firstFaults == 1 && first.Retired && IsAllZero(first.PairwiseKey),
                "The failed session route was not retired and zeroized exactly once.");
            Require(!second.Retired && second.PairwiseKey.AsSpan().SequenceEqual(secondKey),
                "A session-local fault changed another session owned by the same device.");
            var afterFault = supervisor.Snapshot();
            Require(afterFault.PhysicalConnected &&
                    afterFault.PhysicalGeneration == generation &&
                    afterFault.ScheduledReconnects == 0 &&
                    afterFault.SessionSoftFailures == 1,
                "A session-local fault changed the physical Relay generation.");

            var secondHandled = false;
            await ProductionSessionRouteFaultBoundary.ExecuteAsync(
                () =>
                {
                    secondHandled = true;
                    return Task.CompletedTask;
                },
                _ => throw new InvalidOperationException("A healthy session was retired."),
                CancellationToken.None).ConfigureAwait(false);
            Require(secondHandled && !second.Retired &&
                    supervisor.Snapshot().PhysicalGeneration == generation,
                "The healthy session could not continue after its peer session failed.");
        }

        private static async Task ValidateConnectionFatalSignalsEscapeAsync()
        {
            var physical = new ProductionPhysicalRealtimeException(
                "test physical send failure",
                new IOException("socket closed"));
            await RequireThrowsAsync<ProductionPhysicalRealtimeException>(() =>
                ProductionSessionRouteFaultBoundary.ExecuteAsync(
                    () => Task.FromException(physical),
                    _ => throw new InvalidOperationException("Physical I/O was consumed as a session fault."),
                    CancellationToken.None)).ConfigureAwait(false);

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(() =>
                ProductionSessionRouteFaultBoundary.ExecuteAsync(
                    () => Task.FromException(new OperationCanceledException(canceled.Token)),
                    _ => throw new InvalidOperationException("Shared cancellation was consumed."),
                    canceled.Token)).ConfigureAwait(false);

            await RequireThrowsAsync<ProductionReauthenticationRequiredException>(() =>
                ProductionSessionRouteFaultBoundary.ExecuteAsync(
                    () => Task.FromException(new ProductionReauthenticationRequiredException()),
                    _ => throw new InvalidOperationException("Account reauthentication was consumed."),
                    CancellationToken.None)).ConfigureAwait(false);
        }

        private static void ValidateExactRouteRetirement()
        {
            const BindingFlags flags = BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic;
            var runtimeType = typeof(ProductionSessionRouteFaultBoundary).Assembly.GetType(
                "TermLink.Agent.ProductionAgentRuntime",
                throwOnError: true);
            var subscriberType = runtimeType.GetNestedType("Subscriber", BindingFlags.NonPublic);
            var subscribersField = runtimeType.GetField("subscribers", flags);
            var routesField = runtimeType.GetField("connectionSubscriberRoutes", flags);
            var hostHistoryRequestsField = runtimeType.GetField("hostHistoryRequests", flags);
            var retireRoute = runtimeType.GetMethod("RetireLocatedSessionRoute", flags);
            Require(subscriberType != null && subscribersField != null &&
                    routesField != null && hostHistoryRequestsField != null && retireRoute != null,
                "The production exact-route retirement boundary is unavailable.");

            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            var subscribers = Activator.CreateInstance(subscribersField.FieldType);
            var routes = Activator.CreateInstance(routesField.FieldType);
            var hostHistoryRequests = Activator.CreateInstance(hostHistoryRequestsField.FieldType);
            subscribersField.SetValue(runtime, subscribers);
            routesField.SetValue(runtime, routes);
            hostHistoryRequestsField.SetValue(runtime, hostHistoryRequests);

            var sender = Guid.NewGuid();
            var firstSession = Guid.NewGuid();
            var secondSession = Guid.NewGuid();
            var firstKey = RandomNumberGenerator.GetBytes(32);
            var secondKey = RandomNumberGenerator.GetBytes(32);
            var first = CreateSubscriber(subscriberType, sender, firstKey);
            var second = CreateSubscriber(subscriberType, sender, secondKey);
            AddSubscriberRoute(
                subscribersField.FieldType,
                subscribers,
                routesField.FieldType,
                routes,
                firstSession,
                sender,
                first);
            AddSubscriberRoute(
                subscribersField.FieldType,
                subscribers,
                routesField.FieldType,
                routes,
                secondSession,
                sender,
                second);

            retireRoute.Invoke(runtime, new[] { (object)firstSession, sender, first });
            Require(ContainsRoute(routes, firstSession, sender) &&
                    !ContainsSessionSubscriber(subscribers, firstSession, sender) &&
                    !IsAllZero(firstKey) &&
                    !(bool)subscriberType.GetField("Removed", flags).GetValue(first),
                "The failed active subscription did not detach while retaining its " +
                "connection-owned exact-ciphertext route.");
            Require(ContainsRoute(routes, secondSession, sender) &&
                    ContainsSessionSubscriber(subscribers, secondSession, sender) &&
                    !IsAllZero(secondKey) &&
                    !(bool)subscriberType.GetField("Removed", flags).GetValue(second),
                "Exact route retirement changed another session for the same device.");
        }

        private static object CreateSubscriber(Type subscriberType, Guid sender, byte[] key)
        {
            const BindingFlags flags = BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic;
            var subscriber = Activator.CreateInstance(subscriberType, nonPublic: true);
            subscriberType.GetField("DeviceId", flags).SetValue(subscriber, sender);
            subscriberType.GetField("ExchangePublicKey", flags).SetValue(
                subscriber,
                RandomNumberGenerator.GetBytes(32));
            subscriberType.GetField("PairwiseKey", flags).SetValue(subscriber, key);
            subscriberType.GetField("KeyEpoch", flags).SetValue(subscriber, 1L);
            subscriberType.GetField("LastActivityUtc", flags).SetValue(subscriber, DateTime.UtcNow);
            return subscriber;
        }

        private static void AddSubscriberRoute(
            Type subscribersType,
            object subscribers,
            Type routesType,
            object routes,
            Guid sessionId,
            Guid sender,
            object subscriber)
        {
            var sessionSubscribersType = subscribersType.GetGenericArguments()[1];
            var sessionSubscribers = Activator.CreateInstance(sessionSubscribersType);
            Require((bool)sessionSubscribersType.GetMethod("TryAdd").Invoke(
                    sessionSubscribers,
                    new[] { (object)sender, subscriber }),
                "A production session subscriber fixture could not be added.");
            Require((bool)subscribersType.GetMethod("TryAdd").Invoke(
                    subscribers,
                    new[] { (object)sessionId, sessionSubscribers }),
                "A production session fixture could not be added.");
            Require((bool)routesType.GetMethod("TryAdd").Invoke(
                    routes,
                    new[] { (object)RouteKey(sessionId, sender), subscriber }),
                "A production retained route fixture could not be added.");
        }

        private static bool ContainsRoute(object routes, Guid sessionId, Guid sender)
        {
            var arguments = new object[] { RouteKey(sessionId, sender), null };
            return (bool)routes.GetType().GetMethod("TryGetValue").Invoke(routes, arguments);
        }

        private static bool ContainsSessionSubscriber(
            object subscribers,
            Guid sessionId,
            Guid sender)
        {
            var sessionArguments = new object[] { sessionId, null };
            if (!(bool)subscribers.GetType().GetMethod("TryGetValue").Invoke(
                    subscribers,
                    sessionArguments))
                return false;
            var sessionSubscribers = sessionArguments[1];
            var senderArguments = new object[] { sender, null };
            return (bool)sessionSubscribers.GetType().GetMethod("TryGetValue").Invoke(
                sessionSubscribers,
                senderArguments);
        }

        private static string RouteKey(Guid sessionId, Guid sender) =>
            sessionId.ToString("N") + ":" + sender.ToString("N");

        private static ProductionRealtimeConnectionSupervisor CreateSupervisor() =>
            new ProductionRealtimeConnectionSupervisor(
                new ProductionReconnectPreferences
                {
                    InitialDelaySeconds = 1,
                    MaximumDelaySeconds = 4,
                    MaximumFastAttempts = 0
                },
                nextJitterMilliseconds: () => 0);

        private sealed class FakeSessionRoute
        {
            public FakeSessionRoute(Guid sessionId, Guid senderId)
            {
                SessionId = sessionId;
                SenderId = senderId;
                PairwiseKey = RandomNumberGenerator.GetBytes(32);
            }

            public Guid SessionId { get; }
            public Guid SenderId { get; }
            public byte[] PairwiseKey { get; }
            public bool Retired { get; private set; }

            public void Retire()
            {
                if (Retired) return;
                Retired = true;
                CryptographicOperations.ZeroMemory(PairwiseKey);
            }
        }

        private static bool IsAllZero(byte[] value)
        {
            byte aggregate = 0;
            foreach (var item in value) aggregate |= item;
            return aggregate == 0;
        }

        private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action().ConfigureAwait(false); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + " was not thrown.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
