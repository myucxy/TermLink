using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.ProductionTerminalHistory.Validation
{
    internal static class Program
    {
        private sealed class PutHandler : HttpMessageHandler
        {
            public readonly List<byte[]> Payloads = new List<byte[]>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Require(request.Method == HttpMethod.Put, "History ciphertext transport used a non-PUT method.");
                Payloads.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        private sealed class BlockingPutHandler : HttpMessageHandler
        {
            private readonly TaskCompletionSource<bool> entered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => entered.Task;

            public void Release() => release.TrySetResult(true);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Require(request.Method == HttpMethod.Put, "History ciphertext transport used a non-PUT method.");
                await request.Content.ReadAsByteArrayAsync(cancellationToken);
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        }

        private sealed class FakeHistoryApi
        {
            private readonly Guid uploadId = Guid.Parse("76543210-abcd-4321-abcd-0123456789ab");
            public readonly List<JsonElement> Creates = new List<JsonElement>();
            public readonly List<string> IdempotencyKeys = new List<string>();
            public readonly List<JsonElement> CommittedChunks = new List<JsonElement>();
            public int Commits;
            public HttpStatusCode? NextCreateStatus;
            public bool TrackCommittedChunks;

            public Task<HttpResponseMessage> SendAsync(
                HttpMethod method,
                string path,
                byte[] body,
                bool authenticated,
                string idempotencyKey,
                CancellationToken cancellationToken)
            {
                Require(authenticated, "History upload omitted authentication.");
                if (method == HttpMethod.Get)
                {
                    Require(path.EndsWith("/history", StringComparison.Ordinal) && body.Length == 0 && idempotencyKey == null,
                        "History cursor initialization request is invalid.");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            items = TrackCommittedChunks ? CommittedChunks : new List<JsonElement>()
                        }))
                    });
                }
                if (path.EndsWith("/history/uploads", StringComparison.Ordinal))
                {
                    Require(method == HttpMethod.Post, "History create used the wrong method.");
                    using var document = JsonDocument.Parse(body);
                    Creates.Add(document.RootElement.Clone());
                    IdempotencyKeys.Add(idempotencyKey);
                    if (NextCreateStatus.HasValue)
                    {
                        var status = NextCreateStatus.Value;
                        NextCreateStatus = null;
                        return Task.FromResult(new HttpResponseMessage(status));
                    }
                    var json = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        upload_id = uploadId,
                        upload = new
                        {
                            method = "PUT",
                            url = "https://upload.invalid/object",
                            headers = new Dictionary<string, string[]>()
                        }
                    });
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new ByteArrayContent(json)
                    });
                }
                Require(path.EndsWith("/history/uploads/" + uploadId.ToString("D") + "/commit", StringComparison.Ordinal),
                    "History commit path changed.");
                Require(method == HttpMethod.Post && body.Length == 0, "History commit request is invalid.");
                IdempotencyKeys.Add(idempotencyKey);
                Commits++;
                if (TrackCommittedChunks && Creates.Count > 0 &&
                    string.Equals(
                        Creates[^1].GetProperty("kind").GetString(),
                        "CHUNK",
                        StringComparison.OrdinalIgnoreCase))
                {
                    CommittedChunks.Add(Creates[^1].Clone());
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"))
                });
            }
        }

        public static async Task<int> Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "termlink-production-terminal-history-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var pcDeviceId = Guid.Parse("12345678-abcd-4321-abcd-1234567890ab");
                var sessionId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
                ValidateUploaderConstructorCompatibility();
                ValidateRuntimeCapabilityAndPublicationGate(root, pcDeviceId);
                ValidateRecoveredTerminalHistoryClassification();
                ValidateTerminalCoreShadow(root, pcDeviceId);
                ValidateTerminalCoreShadowDispatcherBoundaries();
                ValidateTerminalHistoryLifecycleGate(root, pcDeviceId);
                ValidateTerminalCoreShadowGoldenStreams(root, pcDeviceId);
                var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
                var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId);
                AppendSupportedSession(producer, sessionId);
                ValidateScrollMarginSnapshot(
                    producer,
                    Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeef"));
                ValidatePresentationMetadataSnapshot(
                    producer,
                    Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeed"));
                ValidateCommonCsiFinals(
                    producer,
                    Guid.Parse("cccccccc-bbbb-4ccc-8ddd-eeeeeeeeeeeb"));
                ValidateTermuxProfileFailures(
                    producer,
                    Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeee0"));
                ValidateArtifactSizeFailure(
                    producer,
                    Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeec"));
                ValidateSavedCursorStateAndAlternateProfile(
                    producer,
                    Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeee8"));
                ValidateWalIdentityAndProtection(store, producer, sessionId);
                var result = producer.ProcessSession(sessionId);
                Require(result.SnapshotEligible && result.LastSequence == 2 && result.TranscriptPageCount == 2,
                    "Supported WAL did not produce the expected two-page checkpoint.");
                ValidateCheckpointAndOutbox(producer, sessionId);
                ValidateLegacyOutboxOrderingMigration(store, producer, sessionId);
                ValidateCrashRecovery(root, pcDeviceId, sessionId);
                ValidateFivePagePublication(root, pcDeviceId);
                await ValidateZeroPagePublicationAsync(root, pcDeviceId);
                ValidateNewSnapshotSupersedesIncompleteTranscript(root, pcDeviceId);
                ValidateOlderOutboxRecoveryAfterNewRejection(root, pcDeviceId);
                ValidateGapAndUnsupportedFallback(root, pcDeviceId);
                await ValidateChunkGapFallbackUploaderAsync(root, pcDeviceId);
                await ValidateChunkYieldAndResumeAsync(root, pcDeviceId);
                await ValidateConcurrentUploaderDisposeAsync(pcDeviceId);
                await ValidateRecipientSealingAndUploaderAsync(root, pcDeviceId, sessionId);
                Console.WriteLine("Production terminal history WAL/producer/outbox/uploader validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void ValidateUploaderConstructorCompatibility()
        {
            var sendType = typeof(Func<HttpMethod, string, byte[], bool, string, CancellationToken, Task<HttpResponseMessage>>);
            Require(
                typeof(ProductionHistoryUploader).GetConstructor(new[] { typeof(HttpClient), sendType, typeof(Guid) }) != null,
                "Production history uploader no longer exposes its original three-parameter constructor.");
        }

        private static void ValidateRuntimeCapabilityAndPublicationGate(string root, Guid pcDeviceId)
        {
            var runtimeType = Assembly.Load("TermLink.Agent").GetType(
                "TermLink.Agent.ProductionAgentRuntime",
                throwOnError: true);
            var staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
            var parseFlag = runtimeType.GetMethod("IsTerminalSnapshotRuntimeEnabled", staticFlags);
            var parseTerminalCoreShadow = runtimeType.GetMethod(
                "IsTerminalCoreShadowRuntimeEnabled",
                staticFlags);
            var buildHello = runtimeType.GetMethod("BuildHelloPayload", staticFlags);
            var process = runtimeType.GetMethod("ProcessTerminalHistoryIfEnabled", staticFlags);
            var recordPublication = runtimeType.GetMethod("RecordPublicationOutcome", staticFlags);
            var validateEnvelopeSize = runtimeType.GetMethod("ValidateRealtimeEnvelopeSize", staticFlags);
            Require(parseFlag != null && parseTerminalCoreShadow != null && buildHello != null &&
                process != null && recordPublication != null &&
                validateEnvelopeSize != null,
                "Production runtime terminal-snapshot gate hooks are unavailable.");

            validateEnvelopeSize.Invoke(null, new object[] { 1024 * 1024 - 1 });
            try
            {
                validateEnvelopeSize.Invoke(null, new object[] { 1024 * 1024 });
                throw new InvalidOperationException("A 1 MiB realtime Envelope was not rejected.");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
            {
            }

            var subscriberType = runtimeType.GetNestedType("Subscriber", BindingFlags.NonPublic);
            var prepareFrame = runtimeType.GetMethod("PrepareNewSubscriberFrame", staticFlags);
            var resetCursor = runtimeType.GetMethod("ResetSubscriberCursor", staticFlags);
            var getCached = runtimeType.GetMethod("GetCachedSubscriberFrameOrState", staticFlags);
            Require(subscriberType != null && prepareFrame != null && resetCursor != null && getCached != null &&
                runtimeType.GetField("connectionSubscriberRoutes", BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "Connection-scope subscriber replay retention is unavailable.");
            var subscriber = Activator.CreateInstance(subscriberType, nonPublic: true);
            var sequenceFour = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
            var retainedFour = (byte[])prepareFrame.Invoke(null, new object[] { subscriber, 4UL, sequenceFour });
            Require((bool)resetCursor.Invoke(null, new object[] { subscriber, 3UL }),
                "A retained subscriber could not resume from its durable ACK.");
            var cacheArguments = new object[] { subscriber, 4UL, false, false };
            var replayedFour = (byte[])getCached.Invoke(null, cacheArguments);
            Require(replayedFour != null && replayedFour.SequenceEqual(sequenceFour) &&
                !(bool)cacheArguments[2] && !(bool)cacheArguments[3],
                "Subscriber reattachment did not preserve byte-identical same-sequence ciphertext.");
            CryptographicOperations.ZeroMemory(sequenceFour);
            CryptographicOperations.ZeroMemory(retainedFour);
            CryptographicOperations.ZeroMemory(replayedFour);

            Require(!(bool)parseFlag.Invoke(null, new object[] { null }) &&
                (bool)parseFlag.Invoke(null, new object[] { "1" }) &&
                (bool)parseFlag.Invoke(null, new object[] { "true" }) &&
                !(bool)parseFlag.Invoke(null, new object[] { "0" }) &&
                !(bool)parseFlag.Invoke(null, new object[] { " 1" }) &&
                !(bool)parseFlag.Invoke(null, new object[] { "invalid" }),
                "Terminal snapshot runtime must fail closed unless it receives an exact explicit opt-in.");
            Require((bool)parseTerminalCoreShadow.Invoke(null, new object[] { null }) &&
                (bool)parseTerminalCoreShadow.Invoke(null, new object[] { "1" }) &&
                !(bool)parseTerminalCoreShadow.Invoke(null, new object[] { "0" }) &&
                !(bool)parseTerminalCoreShadow.Invoke(null, new object[] { "false" }) &&
                !(bool)parseTerminalCoreShadow.Invoke(null, new object[] { " false " }) &&
                !(bool)parseTerminalCoreShadow.Invoke(null, new object[] { " 0 " }) &&
                (bool)parseTerminalCoreShadow.Invoke(null, new object[] { "invalid" }),
                "Terminal-core shadow must default on and retain an explicit local rollback.");

            using (var disabledHello = JsonDocument.Parse((byte[])buildHello.Invoke(null, new object[] { false })))
            using (var enabledHello = JsonDocument.Parse((byte[])buildHello.Invoke(null, new object[] { true })))
            {
                var disabledCapabilities = disabledHello.RootElement.GetProperty("capabilities")
                    .EnumerateArray().Select(value => value.GetString()).ToList();
                var enabledCapabilities = enabledHello.RootElement.GetProperty("capabilities")
                    .EnumerateArray().Select(value => value.GetString()).ToList();
                Require(!disabledCapabilities.Contains("terminal_snapshot_v1") &&
                    enabledCapabilities.Count(value => value == "terminal_snapshot_v1") == 1,
                    "HELLO terminal_snapshot_v1 capability is not controlled by the runtime flag.");
            }

            var disabledResult = process.Invoke(null, new object[] { false, null, Guid.NewGuid() });
            Require(disabledResult == null,
                "Disabled terminal snapshot production did not fail closed before accessing the producer.");
            try
            {
                process.Invoke(null, new object[] { true, null, Guid.NewGuid() });
                throw new InvalidOperationException("Enabled terminal snapshot production did not require a producer.");
            }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentNullException)
            {
            }
            var runtimeStoreType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionTerminalHistoryStore",
                throwOnError: true);
            var runtimeProducerType = runtimeType.Assembly.GetType(
                "TermLink.Agent.ProductionTerminalHistoryProducer",
                throwOnError: true);
            var runtimeStore = Activator.CreateInstance(
                runtimeStoreType,
                new object[] { Path.Combine(root, "runtime-gate"), pcDeviceId });
            var runtimeProducer = Activator.CreateInstance(
                runtimeProducerType,
                new[] { runtimeStore, (object)pcDeviceId });
            Require(process.Invoke(null, new[] { (object)true, runtimeProducer, Guid.NewGuid() }) != null,
                "Enabled terminal snapshot production did not invoke the gated producer.");

            var sessionId = Guid.NewGuid();
            var sequences = new ConcurrentDictionary<Guid, ulong>();
            var failedTerminal = (bool)recordPublication.Invoke(
                null,
                new object[] { sequences, sessionId, 7UL, false, true });
            Require(!failedTerminal && !sequences.ContainsKey(sessionId),
                "A failed publication advanced the global cursor or marked a terminal frame delivered.");
            var deliveredNonTerminal = (bool)recordPublication.Invoke(
                null,
                new object[] { sequences, sessionId, 7UL, true, false });
            Require(!deliveredNonTerminal && sequences.TryGetValue(sessionId, out var published) && published == 7,
                "A delivered non-terminal publication did not advance the global cursor correctly.");
            var deliveredTerminal = (bool)recordPublication.Invoke(
                null,
                new object[] { sequences, sessionId, 8UL, true, true });
            Require(deliveredTerminal && sequences[sessionId] == 8,
                "A delivered final lifecycle was not marked terminal after advancing the cursor.");
        }

        private static void AppendSupportedSession(ProductionTerminalHistoryProducer producer, Guid sessionId)
        {
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Resize,
                sessionId,
                1,
                LocalPayloadCodec.Resize(4, 2)));
            var output = new StringBuilder();
            for (var line = 0; line < 206; line++) output.Append((line % 10000).ToString("D4")).Append("\r\n");
            // Leave the real active cursor away from (0,0). This catches the
            // incompatible legacy encoding where the inactive alternate
            // buffer always advertised a fixed origin cursor.
            output.Append("xy");
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                2,
                Encoding.UTF8.GetBytes(output.ToString())));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Lifecycle,
                sessionId,
                3,
                LocalPayloadCodec.Lifecycle(LocalLifecycleState.Ended, 17)));
        }

        private static void ValidateRecoveredTerminalHistoryClassification()
        {
            var runtimeType = Assembly.Load("TermLink.Agent").GetType(
                "TermLink.Agent.ProductionAgentRuntime",
                throwOnError: true);
            var classify = runtimeType.GetMethod(
                "HasValidFinalRecoveredTerminalMarker",
                BindingFlags.Static | BindingFlags.NonPublic);
            Require(classify != null,
                "The persisted terminal-history recovery classifier is missing.");
            var sessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000086");

            bool IsRecoveredTerminal(params LocalFrame[] frames)
            {
                // Local IPC sources are linked into this validation executable as well as the
                // Agent. Rehydrate the Agent assembly's internal frame identity for reflection.
                var agentFrameType = runtimeType.Assembly.GetType(
                    "TermLink.LocalIpc.LocalFrame",
                    throwOnError: true);
                var agentFrameTypeEnum = runtimeType.Assembly.GetType(
                    "TermLink.LocalIpc.LocalFrameType",
                    throwOnError: true);
                var list = (System.Collections.IList)Activator.CreateInstance(
                    typeof(List<>).MakeGenericType(agentFrameType));
                foreach (var frame in frames)
                {
                    var agentType = Enum.ToObject(agentFrameTypeEnum, (byte)frame.Type);
                    var agentFrame = Activator.CreateInstance(
                        agentFrameType,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        args: new object[]
                        {
                            agentType,
                            frame.SessionId,
                            frame.Sequence,
                            frame.Payload
                        },
                        culture: null);
                    list.Add(agentFrame);
                }
                return (bool)classify.Invoke(null, new object[] { sessionId, list });
            }

            Require(IsRecoveredTerminal(
                    new LocalFrame(
                        LocalFrameType.Output,
                        sessionId,
                        1,
                        Encoding.UTF8.GetBytes("ended")),
                    new LocalFrame(
                        LocalFrameType.Lifecycle,
                        sessionId,
                        2,
                        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Ended, 0))),
                "A persisted history stream ending in a valid Ended marker was not terminal.");
            Require(IsRecoveredTerminal(
                    new LocalFrame(
                        LocalFrameType.Lifecycle,
                        sessionId,
                        1,
                        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Ended, 0)),
                    new LocalFrame(
                        LocalFrameType.SessionRemoved,
                        sessionId,
                        2,
                        LocalPayloadCodec.SessionRemoved())),
                "A persisted history stream ending in a valid SessionRemoved marker was not terminal.");

            Require(!IsRecoveredTerminal(
                    new LocalFrame(
                        LocalFrameType.Output,
                        sessionId,
                        1,
                        Encoding.UTF8.GetBytes("active")),
                    new LocalFrame(
                        LocalFrameType.Lifecycle,
                        sessionId,
                        2,
                        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0))),
                "A recovered active session was misclassified as terminal.");
            Require(!IsRecoveredTerminal(
                    new LocalFrame(
                        LocalFrameType.Lifecycle,
                        sessionId,
                        1,
                        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Ended, 0)),
                    new LocalFrame(
                        LocalFrameType.Output,
                        sessionId,
                        2,
                        Encoding.UTF8.GetBytes("later non-terminal frame"))),
                "A non-final terminal marker released a recovered active session.");
            Require(!IsRecoveredTerminal(
                    new LocalFrame(
                        LocalFrameType.Output,
                        sessionId,
                        1,
                        Encoding.UTF8.GetBytes("identity")),
                    new LocalFrame(
                        LocalFrameType.SessionRemoved,
                        Guid.Parse("aaaaaaaa-0000-4000-8000-000000000087"),
                        2,
                        LocalPayloadCodec.SessionRemoved())),
                "A terminal marker for another session passed recovery classification.");
            Require(!IsRecoveredTerminal(
                    new LocalFrame(
                        LocalFrameType.Output,
                        sessionId,
                        2,
                        Encoding.UTF8.GetBytes("sequence")),
                    new LocalFrame(
                        LocalFrameType.SessionRemoved,
                        sessionId,
                        2,
                        LocalPayloadCodec.SessionRemoved())),
                "A non-increasing recovery sequence passed terminal classification.");
            Require(!IsRecoveredTerminal(
                    new LocalFrame(
                        LocalFrameType.SessionRemoved,
                        sessionId,
                        1,
                        new byte[] { 1 })),
                "Malformed recovered terminal metadata passed classification.");
        }

        private static void ValidateTerminalCoreShadow(string root, Guid pcDeviceId)
        {
            var sessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000079");
            var store = new ProductionTerminalHistoryStore(
                Path.Combine(root, "terminal-core-shadow"),
                pcDeviceId);
            ProductionTerminalCoreShadowResult observedShadow = null;
            using var shadowReady = new ManualResetEventSlim();
            using var producer = new ProductionTerminalHistoryProducer(
                store,
                pcDeviceId,
                terminalCoreShadowEnabled: true,
                (observedSessionId, result) =>
                {
                    if (observedSessionId != sessionId) return;
                    observedShadow = result;
                    shadowReady.Set();
                });
            var frames = new List<LocalFrame>
            {
                new LocalFrame(
                    LocalFrameType.Resize,
                    sessionId,
                    1,
                    LocalPayloadCodec.Resize(8, 3)),
                new LocalFrame(
                    LocalFrameType.Output,
                    sessionId,
                    2,
                    Encoding.UTF8.GetBytes(
                        "\u001b]0;OSC title\u0007" +
                        "\u001b[31;1m\u001b]8;;https://example.invalid\u001b\\" +
                        "hello\u001b]8;;\u001b\\\u001b[0m")),
                new LocalFrame(
                    LocalFrameType.Title,
                    sessionId,
                    3,
                    LocalPayloadCodec.Title("Structured title")),
                new LocalFrame(
                    LocalFrameType.Output,
                    sessionId,
                    4,
                    Encoding.UTF8.GetBytes("!"))
            };
            var result = producer.ProcessSession(
                sessionId,
                frames,
                persistLegacyArtifacts: false);
            Require(shadowReady.Wait(TimeSpan.FromSeconds(10)),
                "Windows terminal-core shadow did not complete asynchronously.");
            Require(observedShadow != null &&
                observedShadow.Status == "SHADOW_MATCH_LIMITED" &&
                observedShadow.ReasonCode.Split('+').Contains("TITLE_OUT_OF_BAND") &&
                observedShadow.LastAppliedSequence == 4 &&
                observedShadow.NextSourceSequence == 5 &&
                observedShadow.PresentationRevision > 0 &&
                observedShadow.SnapshotSha256.Length == 64,
                "Windows terminal-core shadow did not match the Portable projection: " +
                (observedShadow == null
                        ? "missing"
                    : observedShadow.Status + "/" +
                        observedShadow.ReasonCode));
            Require(producer.ActiveProjectionCount == 1 &&
                producer.ActiveShadowSessionCount == 1,
                "Windows terminal-core shadow state was not retained while its session was active.");
            producer.RetireShadowSession(sessionId);
            Require(producer.ActiveProjectionCount == 1 &&
                producer.ActiveShadowSessionCount == 0,
                "A terminal pass did not release native shadow state independently of durable history.");
            producer.ProcessSession(
                sessionId,
                Array.Empty<LocalFrame>(),
                persistLegacyArtifacts: false);
            Require(producer.ActiveShadowSessionCount == 0,
                "Late history retry recreated a retired native shadow session.");
            producer.ReleaseSession(sessionId);
            Require(producer.ActiveProjectionCount == 0 &&
                producer.ActiveShadowSessionCount == 0,
                "Windows terminal-core shadow state was not released with its session.");
            var released = producer.ProcessSession(
                sessionId,
                Array.Empty<LocalFrame>(),
                persistLegacyArtifacts: false);
            Require(released.IneligibilityCode == "SESSION_RELEASED" &&
                producer.ActiveProjectionCount == 0 &&
                producer.ActiveShadowSessionCount == 0,
                "Late work recreated a released terminal-core shadow session.");
            Require(result.IneligibilityCode == "LEGACY_PUBLICATION_DISABLED" &&
                store.LoadCheckpoint(sessionId) == null &&
                store.LoadOutbox(sessionId) == null,
                "Shadow-only evaluation persisted or advertised a legacy terminal artifact.");

            producer.RetireShadowSession(sessionId);
            Require(producer.ActiveShadowSessionCount == 0 &&
                producer.RetainedShadowSessionCount == 0,
                "A late retire recreated dispatcher state after final release.");
        }

        private static void ValidateTerminalCoreShadowDispatcherBoundaries()
        {
            var preRetiredSessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000078");
            var preRetiredObserverCalls = 0;
            using (var preRetiredDispatcher = new ProductionTerminalCoreShadowDispatcher(
                (_, _) => Interlocked.Increment(ref preRetiredObserverCalls)))
            {
                preRetiredDispatcher.RetireSession(preRetiredSessionId);
                Require(preRetiredDispatcher.SessionCount == 0 &&
                    preRetiredDispatcher.RetainedSessionCount == 1,
                    "Retiring before first work did not create a dispatcher tombstone.");
                preRetiredDispatcher.Enqueue(
                    preRetiredSessionId,
                    new[]
                    {
                        new LocalFrame(
                            LocalFrameType.Output,
                            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000088"),
                            1,
                            Encoding.UTF8.GetBytes("must-not-estimate"))
                    },
                    null,
                    1,
                    "PORTABLE_UNAVAILABLE");
                Require(Volatile.Read(ref preRetiredObserverCalls) == 0 &&
                    preRetiredDispatcher.SessionCount == 0 &&
                    preRetiredDispatcher.RetainedSessionCount == 1,
                    "Work recreated a shadow generation after pre-retirement.");
                preRetiredDispatcher.ReleaseSession(preRetiredSessionId);
                Require(preRetiredDispatcher.RetainedSessionCount == 0,
                    "Pre-retired dispatcher state was not finally released.");
            }

            var rejectedObserverCalls = 0;
            using (var rejectedDispatcher = new ProductionTerminalCoreShadowDispatcher(
                (_, _) => Interlocked.Increment(ref rejectedObserverCalls)))
            {
                var preRejectedSessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000081");
                rejectedDispatcher.Enqueue(
                    preRejectedSessionId,
                    new[]
                    {
                        // A mismatched identity proves rejection happens before retained-byte
                        // validation as well as before state creation.
                        new LocalFrame(
                            LocalFrameType.Output,
                            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000082"),
                            1,
                            Encoding.UTF8.GetBytes("must-not-validate"))
                    },
                    null,
                    1,
                    "PORTABLE_UNAVAILABLE",
                    rejectPredicate: () => true);
                Require(rejectedDispatcher.SessionCount == 0 &&
                    rejectedDispatcher.RetainedSessionCount == 0 &&
                    Volatile.Read(ref rejectedObserverCalls) == 0,
                    "A pre-rejected shadow dispatch validated work, created state, or notified its observer.");

                var predicateCalls = 0;
                var postCreateRejectedSessionId =
                    Guid.Parse("aaaaaaaa-0000-4000-8000-000000000083");
                rejectedDispatcher.Enqueue(
                    postCreateRejectedSessionId,
                    new[]
                    {
                        new LocalFrame(
                            LocalFrameType.Output,
                            postCreateRejectedSessionId,
                            1,
                            Encoding.UTF8.GetBytes("must-not-run"))
                    },
                    null,
                    1,
                    "PORTABLE_UNAVAILABLE",
                    rejectPredicate: () => Interlocked.Increment(ref predicateCalls) >= 2);
                Require(Volatile.Read(ref predicateCalls) == 2 &&
                    rejectedDispatcher.SessionCount == 0 &&
                    rejectedDispatcher.RetainedSessionCount == 0 &&
                    Volatile.Read(ref rejectedObserverCalls) == 0,
                    "A dispatch rejected after GetOrAdd retained state or executed work.");

                var throwingPredicateSessionId =
                    Guid.Parse("aaaaaaaa-0000-4000-8000-000000000084");
                rejectedDispatcher.Enqueue(
                    throwingPredicateSessionId,
                    new[]
                    {
                        new LocalFrame(
                            LocalFrameType.Output,
                            throwingPredicateSessionId,
                            1,
                            Encoding.UTF8.GetBytes("must-fail-closed"))
                    },
                    null,
                    1,
                    "PORTABLE_UNAVAILABLE",
                    rejectPredicate: () => throw new InvalidOperationException("predicate failure"));
                Require(rejectedDispatcher.SessionCount == 0 &&
                    rejectedDispatcher.RetainedSessionCount == 0 &&
                    Volatile.Read(ref rejectedObserverCalls) == 0,
                    "A throwing shadow rejection predicate escaped or admitted work.");
            }

            var admissionFailureSessionId =
                Guid.Parse("aaaaaaaa-0000-4000-8000-000000000086");
            using var admissionFailureObserverEntered = new ManualResetEventSlim();
            using var releaseAdmissionFailureObserver = new ManualResetEventSlim();
            var admissionFailureObserverCalls = 0;
            using (var admissionFailureDispatcher = new ProductionTerminalCoreShadowDispatcher(
                (observedSessionId, result) =>
                {
                    if (observedSessionId != admissionFailureSessionId ||
                        result.Status != "SHADOW_RETIRED") return;
                    Interlocked.Increment(ref admissionFailureObserverCalls);
                    admissionFailureObserverEntered.Set();
                    releaseAdmissionFailureObserver.Wait(TimeSpan.FromSeconds(10));
                }))
            {
                Task admissionFailureDispatch = null;
                try
                {
                    admissionFailureDispatch = Task.Run(() =>
                        admissionFailureDispatcher.Enqueue(
                            admissionFailureSessionId,
                            new[]
                            {
                                // Invalid identity deterministically fails during retained-byte
                                // estimation, before the dispatcher normally creates session state.
                                new LocalFrame(
                                    LocalFrameType.Output,
                                    Guid.Parse("aaaaaaaa-0000-4000-8000-000000000087"),
                                    1,
                                    Encoding.UTF8.GetBytes("must-retire-before-notify"))
                            },
                            null,
                            1,
                            "PORTABLE_UNAVAILABLE"));
                    Require(admissionFailureObserverEntered.Wait(TimeSpan.FromSeconds(10)),
                        "An admission failure did not reach its observer.");
                    Require(admissionFailureDispatcher.SessionCount == 0 &&
                        admissionFailureDispatcher.RetainedSessionCount == 1,
                        "Admission failure did not install its tombstone before observer notification.");
                    var callsAfterAdmissionFailure =
                        Volatile.Read(ref admissionFailureObserverCalls);
                    admissionFailureDispatcher.Enqueue(
                        admissionFailureSessionId,
                        new[]
                        {
                            new LocalFrame(
                                LocalFrameType.Output,
                                admissionFailureSessionId,
                                2,
                                Encoding.UTF8.GetBytes("must-not-run"))
                        },
                        null,
                        2,
                        "PORTABLE_UNAVAILABLE");
                    Require(admissionFailureDispatcher.SessionCount == 0 &&
                        admissionFailureDispatcher.RetainedSessionCount == 1 &&
                        Volatile.Read(ref admissionFailureObserverCalls) == callsAfterAdmissionFailure,
                        "Late work was admitted after an admission failure retired the shadow.");
                    admissionFailureDispatcher.ReleaseSession(admissionFailureSessionId);
                    Require(admissionFailureDispatcher.RetainedSessionCount == 0,
                        "Final release did not remove the admission-failure tombstone.");
                }
                finally
                {
                    releaseAdmissionFailureObserver.Set();
                    admissionFailureDispatch?.GetAwaiter().GetResult();
                }
            }

            var nativeRetiredSessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000085");
            using var nativeRetiredObserverEntered = new ManualResetEventSlim();
            using var releaseNativeRetiredObserver = new ManualResetEventSlim();
            var nativeRetiredObserverCalls = 0;
            using (var nativeRetiredDispatcher = new ProductionTerminalCoreShadowDispatcher(
                (observedSessionId, result) =>
                {
                    if (observedSessionId != nativeRetiredSessionId) return;
                    Interlocked.Increment(ref nativeRetiredObserverCalls);
                    if (result.Status != "SHADOW_RETIRED") return;
                    nativeRetiredObserverEntered.Set();
                    releaseNativeRetiredObserver.Wait(TimeSpan.FromSeconds(10));
                }))
            {
                try
                {
                    nativeRetiredDispatcher.Enqueue(
                        nativeRetiredSessionId,
                        new[]
                        {
                            // A malformed Resize is accepted by the local frame envelope but
                            // deterministically retires the native shadow during Apply.
                            new LocalFrame(
                                LocalFrameType.Resize,
                                nativeRetiredSessionId,
                                1,
                                Array.Empty<byte>())
                        },
                        null,
                        1,
                        "PORTABLE_UNAVAILABLE");
                    Require(nativeRetiredObserverEntered.Wait(TimeSpan.FromSeconds(10)),
                        "A native SHADOW_RETIRED result did not reach its observer.");
                    Require(nativeRetiredDispatcher.SessionCount == 0 &&
                        nativeRetiredDispatcher.RetainedSessionCount == 1,
                        "Native retirement did not install its tombstone before observer notification.");
                    var callsAfterNativeRetirement = Volatile.Read(ref nativeRetiredObserverCalls);
                    nativeRetiredDispatcher.Enqueue(
                        nativeRetiredSessionId,
                        new[]
                        {
                            new LocalFrame(
                                LocalFrameType.Output,
                                Guid.Parse("aaaaaaaa-0000-4000-8000-000000000089"),
                                2,
                                Encoding.UTF8.GetBytes("must-not-estimate"))
                        },
                        null,
                        2,
                        "PORTABLE_UNAVAILABLE");
                    Require(nativeRetiredDispatcher.SessionCount == 0 &&
                        nativeRetiredDispatcher.RetainedSessionCount == 1 &&
                        Volatile.Read(ref nativeRetiredObserverCalls) == callsAfterNativeRetirement,
                        "Late work was admitted after the native shadow retired.");
                    nativeRetiredDispatcher.ReleaseSession(nativeRetiredSessionId);
                    Require(nativeRetiredDispatcher.RetainedSessionCount == 0,
                        "Final release did not remove the native-retirement tombstone.");
                }
                finally
                {
                    releaseNativeRetiredObserver.Set();
                }
            }

            var queuedSessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000007a");
            using var firstObserverEntered = new ManualResetEventSlim();
            using var releaseFirstObserver = new ManualResetEventSlim();
            using var overflowObserved = new ManualResetEventSlim();
            var observerCalls = 0;
            using (var dispatcher = new ProductionTerminalCoreShadowDispatcher(
                (observedSessionId, result) =>
                {
                    if (observedSessionId != queuedSessionId) return;
                    if (Interlocked.Increment(ref observerCalls) == 1)
                    {
                        firstObserverEntered.Set();
                        releaseFirstObserver.Wait(TimeSpan.FromSeconds(10));
                    }
                    if (result.ReasonCode == "DISPATCH_QUEUE_OVERFLOW") overflowObserved.Set();
                }))
            {
                dispatcher.Enqueue(
                    queuedSessionId,
                    new[]
                    {
                        new LocalFrame(
                            LocalFrameType.Output,
                            queuedSessionId,
                            1,
                            Encoding.UTF8.GetBytes("a"))
                    },
                    null,
                    1,
                    "PORTABLE_UNAVAILABLE");
                Require(firstObserverEntered.Wait(TimeSpan.FromSeconds(10)),
                    "The shadow dispatcher did not enter its blocking observer.");
                for (ulong sequence = 2;
                    sequence <= (ulong)ProductionTerminalCoreShadowDispatcher.MaximumPendingWorkItemsPerSession + 1;
                    sequence++)
                {
                    dispatcher.Enqueue(
                        queuedSessionId,
                        new[]
                        {
                            new LocalFrame(
                                LocalFrameType.Output,
                                queuedSessionId,
                                sequence,
                                Encoding.UTF8.GetBytes("b"))
                        },
                        null,
                        sequence,
                        "PORTABLE_UNAVAILABLE");
                }
                Require(dispatcher.SessionCount == 0 && dispatcher.RetainedSessionCount == 1,
                    "Queue overload did not retain exactly one retired session tombstone.");
                releaseFirstObserver.Set();
                Require(overflowObserved.Wait(TimeSpan.FromSeconds(10)),
                    "Queue overload did not report its retirement reason.");
                var callsAfterRetirement = Volatile.Read(ref observerCalls);
                dispatcher.Enqueue(
                    queuedSessionId,
                    new[]
                    {
                        new LocalFrame(
                            LocalFrameType.Output,
                            Guid.Parse("aaaaaaaa-0000-4000-8000-00000000008a"),
                            99,
                            Encoding.UTF8.GetBytes("must-not-estimate"))
                    },
                    null,
                    99,
                    "PORTABLE_UNAVAILABLE");
                Require(dispatcher.SessionCount == 0 && dispatcher.RetainedSessionCount == 1 &&
                    Volatile.Read(ref observerCalls) == callsAfterRetirement,
                    "Late work recreated a new shadow generation after overload.");
                dispatcher.ReleaseSession(queuedSessionId);
                Require(dispatcher.RetainedSessionCount == 0,
                    "Final release did not remove the retired dispatcher tombstone.");
            }

            var exactSessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000007b");
            var exact = CreateRetainedBudgetFrames(
                exactSessionId,
                ProductionTerminalCoreShadowDispatcher.MaximumPendingRetainedBytesPerSession);
            Require(ProductionTerminalCoreShadowDispatcher.EstimateRetainedBytes(
                    exact,
                    null,
                    exactSessionId,
                    "PORTABLE_UNAVAILABLE") ==
                ProductionTerminalCoreShadowDispatcher.MaximumPendingRetainedBytesPerSession,
                "The dispatcher retained-byte boundary calculation drifted.");
            using var exactObserved = new ManualResetEventSlim();
            using (var exactDispatcher = new ProductionTerminalCoreShadowDispatcher(
                (observedSessionId, _) =>
                {
                    if (observedSessionId == exactSessionId) exactObserved.Set();
                }))
            {
                exactDispatcher.Enqueue(
                    exactSessionId,
                    exact,
                    null,
                    checked((ulong)exact.Count),
                    "PORTABLE_UNAVAILABLE");
                Require(exactDispatcher.SessionCount == 1,
                    "A work item exactly at the retained-byte limit was rejected.");
                Require(exactObserved.Wait(TimeSpan.FromSeconds(20)),
                    "A work item exactly at the retained-byte limit did not execute.");
                exactDispatcher.ReleaseSession(exactSessionId);
            }

            var oversizedSessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000007c");
            using var oversizedObserved = new ManualResetEventSlim();
            var oversized = CreateRetainedBudgetFrames(
                oversizedSessionId,
                ProductionTerminalCoreShadowDispatcher.MaximumPendingRetainedBytesPerSession + 1);
            using (var oversizedDispatcher = new ProductionTerminalCoreShadowDispatcher(
                (observedSessionId, result) =>
                {
                    if (observedSessionId == oversizedSessionId &&
                        result.ReasonCode == "DISPATCH_QUEUE_OVERFLOW")
                        oversizedObserved.Set();
                }))
            {
                oversizedDispatcher.Enqueue(
                    oversizedSessionId,
                    oversized,
                    null,
                    checked((ulong)oversized.Count),
                    "PORTABLE_UNAVAILABLE");
                Require(oversizedObserved.Wait(TimeSpan.FromSeconds(10)) &&
                    oversizedDispatcher.SessionCount == 0 &&
                    oversizedDispatcher.RetainedSessionCount == 1,
                    "A work item above the retained-byte limit did not retire its session.");
                oversizedDispatcher.ReleaseSession(oversizedSessionId);
            }
        }

        private static IReadOnlyList<LocalFrame> CreateRetainedBudgetFrames(
            Guid sessionId,
            long targetRetainedBytes)
        {
            const int frameCount = 17;
            const string reasonCode = "PORTABLE_UNAVAILABLE";
            var frames = Enumerable.Range(1, frameCount)
                .Select(sequence => new LocalFrame(
                    LocalFrameType.Capabilities,
                    sessionId,
                    checked((ulong)sequence),
                    Array.Empty<byte>()))
                .ToArray();
            var fixedBytes = ProductionTerminalCoreShadowDispatcher.EstimateRetainedBytes(
                frames,
                null,
                sessionId,
                reasonCode);
            var payloadBytes = checked(targetRetainedBytes - fixedBytes);
            var maximumPayloadBytes = LocalFrameCodec.MaximumFrameBytes - 64;
            for (var index = 0; index < frames.Length; index++)
            {
                var length = checked((int)Math.Min(payloadBytes, maximumPayloadBytes));
                frames[index] = new LocalFrame(
                    LocalFrameType.Capabilities,
                    sessionId,
                    checked((ulong)(index + 1)),
                    new byte[length]);
                payloadBytes -= length;
            }
            Require(payloadBytes == 0,
                "The retained-byte fixture could not fit within legal local frame payloads.");
            return frames;
        }

        private static void ValidateTerminalHistoryLifecycleGate(string root, Guid pcDeviceId)
        {
            var store = new ProductionTerminalHistoryStore(
                Path.Combine(root, "terminal-history-lifecycle-gate"),
                pcDeviceId);
            var producer = new ProductionTerminalHistoryProducer(
                store,
                pcDeviceId,
                terminalCoreShadowEnabled: false);
            var sessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000007d");
            producer.ProcessSession(
                sessionId,
                new[]
                {
                    new LocalFrame(
                        LocalFrameType.Output,
                        sessionId,
                        1,
                        Encoding.UTF8.GetBytes("lifecycle"))
                },
                persistLegacyArtifacts: false);
            var gateField = typeof(ProductionTerminalHistoryProducer).GetField(
                "lifecycleGate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var gate = (ReaderWriterLockSlim)gateField?.GetValue(producer);
            Require(gate != null, "The terminal history lifecycle gate is missing.");
            var statesField = typeof(ProductionTerminalHistoryProducer).GetField(
                "states",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var states = statesField?.GetValue(producer);
            var tryGetValue = states?.GetType().GetMethod("TryGetValue");
            var lookupArguments = new object[] { sessionId, null };
            Require(tryGetValue != null && (bool)tryGetValue.Invoke(states, lookupArguments),
                "The lifecycle validation could not locate its production state.");
            var state = lookupArguments[1];
            var stateSync = state?.GetType().GetField("Sync")?.GetValue(state);
            Require(stateSync != null, "The lifecycle validation could not locate the production state lock.");
            var stateLockHeld = false;
            try
            {
                Monitor.Enter(stateSync);
                stateLockHeld = true;
                var processing = Task.Run(() => producer.ProcessSession(
                    sessionId,
                    Array.Empty<LocalFrame>(),
                    persistLegacyArtifacts: false));
                Require(SpinWait.SpinUntil(
                        () => gate.CurrentReadCount > 0,
                        TimeSpan.FromSeconds(10)),
                    "A real ProcessSession call did not acquire the lifecycle read lease.");
                var dispose = Task.Run(producer.Dispose);
                Require(!dispose.Wait(TimeSpan.FromMilliseconds(200)),
                    "Producer Dispose did not wait for the active ProcessSession call.");
                Monitor.Exit(stateSync);
                stateLockHeld = false;
                Require(processing.Wait(TimeSpan.FromSeconds(10)),
                    "The blocked ProcessSession call did not complete after its state lock was released.");
                Require(dispose.Wait(TimeSpan.FromSeconds(10)),
                    "Producer Dispose did not complete after processing drained.");
                var disposed = false;
                try
                {
                    producer.ProcessSession(
                        sessionId,
                        Array.Empty<LocalFrame>(),
                        persistLegacyArtifacts: false);
                }
                catch (ObjectDisposedException) { disposed = true; }
                Require(disposed, "A disposed terminal history producer accepted new processing.");
            }
            finally
            {
                if (stateLockHeld) Monitor.Exit(stateSync);
                producer.Dispose();
            }
        }

        private static void ValidateTerminalCoreShadowGoldenStreams(string root, Guid pcDeviceId)
        {
            var goldenRoot = FindTerminalGoldenStreamsRoot();
            var cases = Directory.GetDirectories(goldenRoot)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            Require(cases.Length > 0, "No terminal golden streams were found.");

            var summaries = new List<string>();
            for (var caseIndex = 0; caseIndex < cases.Length; caseIndex++)
            {
                var caseRoot = cases[caseIndex];
                var caseName = Path.GetFileName(caseRoot);
                var output = File.ReadAllBytes(Path.Combine(caseRoot, "output.bin"));
                var sessionIdBytes = new byte[16];
                sessionIdBytes[0] = checked((byte)(caseIndex + 1));
                sessionIdBytes[6] = 0x40;
                sessionIdBytes[8] = 0x80;
                var sessionId = new Guid(sessionIdBytes);
                var frames = new List<LocalFrame>();
                ulong lastOutputSequence = 0;

                foreach (var line in File.ReadLines(Path.Combine(caseRoot, "events.jsonl")))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    var item = document.RootElement;
                    var sequence = item.GetProperty("seq").GetUInt64();
                    var type = item.GetProperty("type").GetString();
                    switch (type)
                    {
                        case "output":
                            var offset = item.GetProperty("data_offset").GetInt32();
                            var length = item.GetProperty("data_length").GetInt32();
                            var payload = new byte[length];
                            Buffer.BlockCopy(output, offset, payload, 0, length);
                            frames.Add(new LocalFrame(
                                LocalFrameType.Output,
                                sessionId,
                                sequence,
                                payload));
                            lastOutputSequence = sequence;
                            break;
                        case "resize":
                            frames.Add(new LocalFrame(
                                LocalFrameType.Resize,
                                sessionId,
                                sequence,
                                LocalPayloadCodec.Resize(
                                    checked((short)item.GetProperty("columns").GetInt32()),
                                    checked((short)item.GetProperty("rows").GetInt32()))));
                            break;
                        case "lifecycle":
                            var ended = string.Equals(
                                item.GetProperty("state").GetString(),
                                "ended",
                                StringComparison.Ordinal);
                            var exitCode = ended && item.TryGetProperty("exit_code", out var code)
                                ? code.GetInt32()
                                : 0;
                            frames.Add(new LocalFrame(
                                LocalFrameType.Lifecycle,
                                sessionId,
                                sequence,
                                LocalPayloadCodec.Lifecycle(
                                    ended ? LocalLifecycleState.Ended : LocalLifecycleState.Running,
                                    exitCode)));
                            break;
                        default:
                            throw new InvalidDataException(
                                "Unsupported golden stream event type: " + type);
                    }
                }

                Require(lastOutputSequence > 0, caseName + " has no output event.");
                var store = new ProductionTerminalHistoryStore(
                    Path.Combine(root, "terminal-core-shadow-golden", caseName),
                    pcDeviceId);
                ProductionTerminalCoreShadowResult observedShadow = null;
                using var shadowReady = new ManualResetEventSlim();
                using var producer = new ProductionTerminalHistoryProducer(
                    store,
                    pcDeviceId,
                    terminalCoreShadowEnabled: true,
                    (observedSessionId, value) =>
                    {
                        if (observedSessionId != sessionId) return;
                        observedShadow = value;
                        shadowReady.Set();
                    });
                var result = producer.ProcessSession(
                    sessionId,
                    frames,
                    persistLegacyArtifacts: false);
                Require(shadowReady.Wait(TimeSpan.FromSeconds(10)),
                    caseName + " shadow replay did not complete asynchronously.");
                var shadow = observedShadow;
                Require(shadow != null, caseName + " did not produce a shadow result.");
                Require(shadow.Status == "SHADOW_MATCH" ||
                    shadow.Status == "SHADOW_MATCH_LIMITED" ||
                    shadow.Status == "SHADOW_READY",
                    caseName + " could not produce a native checkpoint: " +
                    shadow.Status + "/" + shadow.ReasonCode);
                Require(shadow.LastAppliedSequence == lastOutputSequence &&
                    shadow.NextSourceSequence == lastOutputSequence + 1 &&
                    shadow.SnapshotSha256.Length == 64,
                    caseName + " produced inconsistent native checkpoint metadata.");
                Require(store.LoadCheckpoint(sessionId) == null &&
                    store.LoadOutbox(sessionId) == null,
                    caseName + " persisted a legacy artifact during shadow-only replay.");
                summaries.Add(caseName + "=" + shadow.Status +
                    (string.IsNullOrWhiteSpace(shadow.ReasonCode)
                        ? string.Empty
                        : "/" + shadow.ReasonCode));
            }

            Console.WriteLine("Terminal core shadow golden matrix: " +
                string.Join(", ", summaries));
        }

        private static string FindTerminalGoldenStreamsRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var current = new DirectoryInfo(start);
                while (current != null)
                {
                    var candidate = Path.Combine(
                        current.FullName,
                        "testdata",
                        "terminal-golden-streams");
                    if (Directory.Exists(candidate)) return candidate;
                    current = current.Parent;
                }
            }
            throw new DirectoryNotFoundException(
                "Repository terminal golden streams directory was not found.");
        }

        private static void ValidateFivePagePublication(string root, Guid pcDeviceId)
        {
            var sessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000071");
            var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
            var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Resize,
                sessionId,
                1,
                // Keep each D4 marker narrower than the grid so CRLF, rather
                // than an automatic wrap, seals exactly one transcript row.
                LocalPayloadCodec.Resize(16, 2)));
            var output = new StringBuilder();
            for (var line = 0; line < 1001; line++) output.Append(line.ToString("D4")).Append("\r\n");
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                2,
                Encoding.UTF8.GetBytes(output.ToString())));

            var result = producer.ProcessSession(sessionId);
            var outbox = producer.LoadOutbox(sessionId);
            var snapshotArtifact = outbox.Artifacts[0];
            var transcriptArtifacts = outbox.Artifacts.Skip(1).ToList();
            var transcriptRowCount = transcriptArtifacts.Sum(value =>
                TerminalTranscriptPageV1.Parser.ParseFrom(value.Plaintext).Rows.Count);
            var contiguousRanges = transcriptArtifacts.Zip(
                transcriptArtifacts.Skip(1),
                (left, right) => left.LastLineId + 1 == right.FirstLineId).All(value => value);
            var expectedInitialTranscriptBeforeLineId = transcriptArtifacts[^1].LastLineId + 1;
            Require(result.SnapshotEligible && result.TranscriptPageCount == 5 &&
                outbox.SnapshotSequence == 2 && outbox.Artifacts.Count == 6 &&
                snapshotArtifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot &&
                snapshotArtifact.InitialTranscriptBeforeLineId == expectedInitialTranscriptBeforeLineId &&
                snapshotArtifact.TranscriptPageCount == 5 &&
                transcriptArtifacts.All(value => value.Kind == ProductionTerminalHistoryArtifactKind.Transcript) &&
                transcriptArtifacts.Select(value => value.UploadOrder).SequenceEqual(new[] { 1, 2, 3, 4, 5 }) &&
                transcriptArtifacts.All(value =>
                    TerminalTranscriptPageV1.Parser.ParseFrom(value.Plaintext).Rows.Count == 200) &&
                transcriptRowCount == 1000 && contiguousRanges,
                "A 1000-line transcript did not produce one Snapshot followed by five oldest-to-newest pages: " +
                "eligible=" + result.SnapshotEligible +
                ", resultPages=" + result.TranscriptPageCount +
                ", artifacts=" + outbox.Artifacts.Count +
                ", before=" + snapshotArtifact.InitialTranscriptBeforeLineId +
                ", snapshotPages=" + snapshotArtifact.TranscriptPageCount +
                ", ranges=" + string.Join(",", transcriptArtifacts.Select(
                    value => value.FirstLineId + "-" + value.LastLineId)) + ".");

            var recovered = new ProductionTerminalHistoryStore(root, pcDeviceId).LoadOutbox(sessionId);
            var recoveredSnapshot = recovered.Artifacts[0];
            var clone = recovered.Clone();
            clone.Artifacts[0].InitialTranscriptBeforeLineId++;
            clone.Artifacts[0].TranscriptPageCount--;
            Require(recoveredSnapshot.InitialTranscriptBeforeLineId == expectedInitialTranscriptBeforeLineId &&
                recoveredSnapshot.TranscriptPageCount == 5,
                "Durable recovery or cloning lost Snapshot transcript publication metadata.");

            var missingBoundary = recovered.Clone();
            missingBoundary.Artifacts[0].InitialTranscriptBeforeLineId = 0;
            Expect<InvalidDataException>(() => store.SaveOutbox(missingBoundary),
                "Outbox persistence accepted a Snapshot without an initial transcript boundary.");
            var wrongCount = recovered.Clone();
            wrongCount.Artifacts[0].TranscriptPageCount = 4;
            Expect<InvalidDataException>(() => store.SaveOutbox(wrongCount),
                "Outbox persistence accepted a Snapshot with an incorrect transcript page count.");
            var excessiveCount = recovered.Clone();
            excessiveCount.Artifacts[0].TranscriptPageCount = 100_001;
            Expect<InvalidDataException>(() => store.SaveOutbox(excessiveCount),
                "Outbox persistence accepted a Snapshot page count outside the server contract.");
            var transcriptClaim = recovered.Clone();
            transcriptClaim.Artifacts[1].InitialTranscriptBeforeLineId = 1;
            Expect<InvalidDataException>(() => store.SaveOutbox(transcriptClaim),
                "Outbox persistence accepted Snapshot-only publication metadata on a Transcript.");
        }

        private static async Task ValidateZeroPagePublicationAsync(string root, Guid pcDeviceId)
        {
            var sessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000072");
            var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
            var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                1,
                Encoding.UTF8.GetBytes("tail-only")));
            var result = producer.ProcessSession(sessionId);
            var outbox = producer.LoadOutbox(sessionId);
            var snapshotArtifact = outbox.Artifacts.Single();
            Require(result.SnapshotEligible && result.TranscriptPageCount == 0 &&
                snapshotArtifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot &&
                snapshotArtifact.InitialTranscriptBeforeLineId == 1 &&
                snapshotArtifact.TranscriptPageCount == 0,
                "A zero-page publication did not retain an explicit, nonzero transcript boundary.");

            var invalidCount = outbox.Clone();
            invalidCount.Artifacts[0].TranscriptPageCount = -1;
            Expect<InvalidDataException>(() => store.SaveOutbox(invalidCount),
                "Outbox persistence accepted a negative zero-page publication count.");

            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000073"),
                PairwiseKey = Enumerable.Range(193, 32).Select(value => (byte)value).ToArray(),
                KeyEpoch = 15
            };
            var pending = producer.PrepareNextUpload(sessionId, recipient);
            var api = new FakeHistoryApi();
            var putHandler = new PutHandler();
            using var http = new HttpClient(putHandler);
            using var uploader = new ProductionHistoryUploader(http, api.SendAsync, pcDeviceId, store, true);

            var invalidUpload = new ProductionTerminalHistoryPreparedUpload
            {
                SessionId = pending.SessionId,
                RecipientDeviceId = pending.RecipientDeviceId,
                KeyEpoch = pending.KeyEpoch,
                Artifact = pending.Artifact.Clone(),
                SealedObject = (byte[])pending.SealedObject.Clone()
            };
            invalidUpload.Artifact.InitialTranscriptBeforeLineId++;
            await ExpectAsync<InvalidDataException>(
                () => uploader.UploadTerminalArtifactAsync(sessionId, recipient, invalidUpload, CancellationToken.None),
                "Uploader accepted a Snapshot publication boundary that disagreed with its ciphertext.");
            Require(api.Creates.Count == 0 && putHandler.Payloads.Count == 0,
                "Invalid Snapshot publication metadata reached the network.");

            var outcome = await uploader.UploadTerminalArtifactAsync(
                sessionId,
                recipient,
                pending,
                CancellationToken.None);
            Require(outcome == ProductionHistoryUploadOutcome.Durable && api.Creates.Count == 1 &&
                api.Creates[0].GetProperty("transcript_initial_before_line_id").GetUInt64() == 1 &&
                api.Creates[0].GetProperty("transcript_page_count").GetInt32() == 0,
                "Zero-page Snapshot publication metadata was not uploaded exactly.");
            CryptographicOperations.ZeroMemory(invalidUpload.SealedObject);
            CryptographicOperations.ZeroMemory(pending.SealedObject);
            CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
        }

        private static void ValidateWalIdentityAndProtection(
            ProductionTerminalHistoryStore store,
            ProductionTerminalHistoryProducer producer,
            Guid sessionId)
        {
            var frames = store.ReadFrames(sessionId);
            Require(frames.Count == 3 && frames.Select(value => value.Sequence).SequenceEqual(new ulong[] { 1, 2, 3 }),
                "Terminal WAL recovery order is invalid.");
            producer.AppendCommittedFrame(frames[1]);
            Expect<InvalidDataException>(() => producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                2,
                Encoding.UTF8.GetBytes("different"))),
                "Terminal WAL accepted different bytes for an existing sequence.");
            var walPath = Directory.GetFiles(Path.Combine(store.RootPath, sessionId.ToString("N"), "wal"), "*.wal.dpapi")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ElementAt(1);
            var stored = File.ReadAllBytes(walPath);
            Require(!Contains(stored, Encoding.UTF8.GetBytes("0000\r\n0001")), "Terminal WAL exposed output plaintext.");
        }

        private static void ValidateScrollMarginSnapshot(
            ProductionTerminalHistoryProducer producer,
            Guid sessionId)
        {
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                1,
                Encoding.UTF8.GetBytes("\u001b[2;4r\u001b[4;1Hmargin")));
            var result = producer.ProcessSession(sessionId);
            var checkpoint = producer.LoadCheckpoint(sessionId);
            var snapshot = TerminalSnapshotV1.Parser.ParseFrom(checkpoint.SnapshotPlaintext);
            SnapshotCrypto.ValidateSnapshotForProducer(snapshot);
            Require(result.SnapshotEligible &&
                snapshot.PrimaryScreen.ScrollTop == 1 && snapshot.PrimaryScreen.ScrollBottom == 3 &&
                snapshot.AlternateScreen.ScrollTop == 1 && snapshot.AlternateScreen.ScrollBottom == 3,
                "The producer did not publish active DECSTBM margins in its portable snapshot.");
        }

        private static void ValidatePresentationMetadataSnapshot(
            ProductionTerminalHistoryProducer producer,
            Guid sessionId)
        {
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                1,
                Encoding.UTF8.GetBytes(
                    "\u001b]4;2;#123456\u0007")));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Title,
                sessionId,
                2,
                LocalPayloadCodec.Title("Structured title")));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                3,
                Encoding.UTF8.GetBytes("!\u001b[H")));
            var result = producer.ProcessSession(sessionId);
            var checkpoint = producer.LoadCheckpoint(sessionId);
            var snapshot = TerminalSnapshotV1.Parser.ParseFrom(checkpoint.SnapshotPlaintext);
            SnapshotCrypto.ValidateSnapshotForProducer(snapshot);
            Require(result.SnapshotEligible && snapshot.Title == "Structured title" &&
                snapshot.Palette.ColorsRgb[2] == 0x00123456 &&
                snapshot.HyperlinkTable.Count == 0 &&
                snapshot.StyleTable.All(value => value.HyperlinkIndexPlusOne == 0),
                "The producer dropped title/palette state or fabricated a hyperlink table: " +
                snapshot.Title + "/" + snapshot.Palette.ColorsRgb[2] + "/" +
                snapshot.HyperlinkTable.Count + "/" +
                string.Join(",", snapshot.StyleTable.Select(value => value.HyperlinkIndexPlusOne)));
        }

        private static void ValidateTermuxProfileFailures(
            ProductionTerminalHistoryProducer producer,
            Guid firstSessionId)
        {
            var hyperlinkSession = firstSessionId;
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                hyperlinkSession,
                1,
                Encoding.UTF8.GetBytes(
                    "\u001b]8;id=docs;https://example.test/docs\u001b\\link\u001b]8;;\u001b\\\u001b[H")));
            RequireRejected(producer, hyperlinkSession, "TERMUX_HYPERLINK_UNSUPPORTED");

            var graphemeSession = IncrementGuid(firstSessionId, 1);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                graphemeSession,
                1,
                Encoding.UTF8.GetBytes("A" + string.Concat(Enumerable.Repeat("\u0301", 16)) + "\u001b[H")));
            RequireRejected(producer, graphemeSession, "TERMUX_GRAPHEME_UNSUPPORTED");

            var savedWrapSession = IncrementGuid(firstSessionId, 2);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                savedWrapSession,
                1,
                Encoding.UTF8.GetBytes("\u001b7\u001b[?7l")));
            RequireRejected(producer, savedWrapSession, "TERMUX_SAVED_AUTOWRAP_UNREPRESENTABLE");

            var savedPendingSession = IncrementGuid(firstSessionId, 3);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Resize,
                savedPendingSession,
                1,
                LocalPayloadCodec.Resize(4, 2)));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                savedPendingSession,
                2,
                Encoding.UTF8.GetBytes("1234\u001b7\u001b[H")));
            RequireRejected(producer, savedPendingSession, "TERMUX_SAVED_PENDING_WRAP_UNREPRESENTABLE");

            var styleSession = IncrementGuid(firstSessionId, 4);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Resize,
                styleSession,
                1,
                LocalPayloadCodec.Resize(512, 3)));
            var styled = new StringBuilder();
            for (var index = 0; index < 1_025; index++)
            {
                styled.Append("\u001b[38;2;")
                    .Append((index >> 16) & 0xff).Append(';')
                    .Append((index >> 8) & 0xff).Append(';')
                    .Append(index & 0xff).Append("mX");
            }
            styled.Append("\u001b[0m\u001b[H");
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                styleSession,
                2,
                Encoding.UTF8.GetBytes(styled.ToString())));
            RequireRejected(producer, styleSession, "TERMUX_STYLE_TABLE_LIMIT_EXCEEDED");

            var spacingMarkSession = IncrementGuid(firstSessionId, 5);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                spacingMarkSession,
                1,
                Encoding.UTF8.GetBytes("A\u0903\u001b[H")));
            RequireRejected(producer, spacingMarkSession, "TERMUX_GRAPHEME_UNSUPPORTED");

            var clearedTabsSession = IncrementGuid(firstSessionId, 6);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                clearedTabsSession,
                1,
                Encoding.UTF8.GetBytes("\u001b[3g\u001b[H")));
            RequireRejected(producer, clearedTabsSession, "TERMUX_TAB_STOP_STATE_UNREPRESENTABLE");

            var hiddenTabSession = IncrementGuid(firstSessionId, 7);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                hiddenTabSession,
                1,
                Encoding.UTF8.GetBytes("\u001b[1;17H\u001b[g")));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Resize,
                hiddenTabSession,
                2,
                LocalPayloadCodec.Resize(10, 24)));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                hiddenTabSession,
                3,
                Encoding.UTF8.GetBytes("\u001b[H")));
            RequireRejected(producer, hiddenTabSession, "TERMUX_TAB_STOP_STATE_UNREPRESENTABLE");

            var unusedHyperlinkSession = Guid.Parse("cccccccc-dddd-4eee-8fff-000000000001");
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                unusedHyperlinkSession,
                1,
                Encoding.UTF8.GetBytes(
                    "\u001b]8;id=unused;https://example.test/unused\u001b\\\u001b]8;;\u001b\\X\u001b[H")));
            RequireRejected(producer, unusedHyperlinkSession, "TERMUX_HYPERLINK_UNSUPPORTED");
        }

        private static void ValidateSavedCursorStateAndAlternateProfile(
            ProductionTerminalHistoryProducer producer,
            Guid firstSessionId)
        {
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                firstSessionId,
                1,
                Encoding.UTF8.GetBytes("\u001b(0\u001b7\u001b(B\u001b[H")));
            var saved = producer.ProcessSession(firstSessionId);
            var savedSnapshot = TerminalSnapshotV1.Parser.ParseFrom(
                producer.LoadCheckpoint(firstSessionId).SnapshotPlaintext);
            Require(saved.SnapshotEligible &&
                savedSnapshot.PrimaryScreen.Charset.G0 == TerminalCharsetV1.Ascii &&
                savedSnapshot.PrimaryScreen.SavedCursor.Charset.G0 == TerminalCharsetV1.DecSpecialGraphics,
                "Producer substituted current charset for the saved cursor charset.");

            var incompatible47 = IncrementGuid(firstSessionId, 1);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                incompatible47,
                1,
                Encoding.UTF8.GetBytes("\u001b[2;2H\u001b7\u001b[H\u001b[?47h")));
            RequireRejected(producer, incompatible47, "TERMUX_DUAL_SCREEN_CURSOR_UNSUPPORTED");

            var compatible1049 = IncrementGuid(firstSessionId, 2);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                compatible1049,
                1,
                Encoding.UTF8.GetBytes("\u001b[2;2H\u001b[?1049h")));
            var alternate = producer.ProcessSession(compatible1049);
            var alternateSnapshot = TerminalSnapshotV1.Parser.ParseFrom(
                producer.LoadCheckpoint(compatible1049).SnapshotPlaintext);
            Require(alternate.SnapshotEligible && alternateSnapshot.ActiveScreen == TerminalScreenKindV1.Alternate &&
                alternateSnapshot.PrimaryScreen.Cursor.Equals(alternateSnapshot.PrimaryScreen.SavedCursor),
                "A lossless 1049 alternate-screen state was not accepted exactly.");

            var savedOrigin = IncrementGuid(firstSessionId, 3);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                savedOrigin,
                1,
                Encoding.UTF8.GetBytes("\u001b[3;10r\u001b[?6h\u001b[2;1H\u001b7\u001b[?6l\u001b[H")));
            var originResult = producer.ProcessSession(savedOrigin);
            var originSnapshot = TerminalSnapshotV1.Parser.ParseFrom(
                producer.LoadCheckpoint(savedOrigin).SnapshotPlaintext);
            Require(originResult.SnapshotEligible &&
                originSnapshot.PrimaryScreen.ScrollTop == 2 &&
                originSnapshot.PrimaryScreen.SavedCursor.OriginMode &&
                originSnapshot.PrimaryScreen.SavedCursor.Row == 3,
                "Producer did not convert a relative origin-mode saved row to Termux's absolute row.");
        }

        private static void ValidateCommonCsiFinals(
            ProductionTerminalHistoryProducer producer,
            Guid sessionId)
        {
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                1,
                Encoding.UTF8.GetBytes(
                    "\u001b[1^A\u001b[5`B\u001b[2aC\u001b[2eD" +
                    "\u001b[5n\u001b[?6n\u001b[c\u001b[>0c" +
                    "\u001b[22;0;0t\u001b]0;temporary title\u0007\u001b[23;0;0t" +
                    "\u001b[11t\u001b[0x\u001b[0q" +
                    "\u001b[>4;2m\u001b[>4;m\u001b[?4m\u001b[0%m")));

            var result = producer.ProcessSession(sessionId);
            var checkpoint = producer.LoadCheckpoint(sessionId);
            Require(result.SnapshotEligible && result.LastSequence == 1 &&
                checkpoint != null && checkpoint.SnapshotEligible &&
                checkpoint.SnapshotPlaintext != null,
                "Common cursor aliases and non-rendering CSI queries disabled portable snapshots: " +
                result.IneligibilityCode + ".");
            var snapshot = TerminalSnapshotV1.Parser.ParseFrom(checkpoint.SnapshotPlaintext);
            SnapshotCrypto.ValidateSnapshotForProducer(snapshot);
            Require(snapshot.PrimaryScreen.Cursor.Row == 2 &&
                snapshot.PrimaryScreen.Cursor.Column == 9,
                "Common CSI cursor aliases did not preserve the exact terminal cursor.");
        }

        private static void ValidateArtifactSizeFailure(
            ProductionTerminalHistoryProducer producer,
            Guid sessionId)
        {
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Resize,
                sessionId,
                1,
                LocalPayloadCodec.Resize(512, 2)));
            var output = new StringBuilder();
            var grapheme = "A\u0301\u0301";
            for (var row = 0; row < 202; row++)
            {
                for (var column = 0; column < 512; column++) output.Append(grapheme);
                output.Append("\r\n");
            }
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                2,
                Encoding.UTF8.GetBytes(output.ToString())));
            RequireRejected(producer, sessionId, "TRANSCRIPT_OBJECT_LIMIT_EXCEEDED");
        }

        private static void RequireRejected(
            ProductionTerminalHistoryProducer producer,
            Guid sessionId,
            string expectedCode)
        {
            var result = producer.ProcessSession(sessionId);
            var checkpoint = producer.LoadCheckpoint(sessionId);
            Require(!result.SnapshotEligible && result.IneligibilityCode == expectedCode &&
                checkpoint != null && !checkpoint.SnapshotEligible && checkpoint.SnapshotPlaintext == null,
                "Producer did not fail closed with " + expectedCode + ": " + result.IneligibilityCode + ".");
        }

        private static Guid IncrementGuid(Guid value, byte increment)
        {
            var bytes = value.ToByteArray();
            bytes[15] = checked((byte)(bytes[15] + increment));
            return new Guid(bytes);
        }

        private static void ValidateCheckpointAndOutbox(ProductionTerminalHistoryProducer producer, Guid sessionId)
        {
            var checkpoint = producer.LoadCheckpoint(sessionId);
            Require(checkpoint != null && checkpoint.SnapshotEligible && checkpoint.LastSequence == 2 &&
                checkpoint.TranscriptPlaintexts.Count == 2,
                "Terminal checkpoint was not atomically persisted.");
            var snapshot = TerminalSnapshotV1.Parser.ParseFrom(checkpoint.SnapshotPlaintext);
            SnapshotCrypto.ValidateSnapshotForProducer(snapshot);
            Require(snapshot.ScrollbackRows.Count == 0 && snapshot.ScrollbackFirstLineId == 0,
                "Terminal snapshot embedded scrollback instead of transcript pages.");
            Require(snapshot.PrimaryScreen.VisibleRows.All(value => value.LineId > 0) &&
                snapshot.PrimaryScreen.VisibleRows.Zip(snapshot.PrimaryScreen.VisibleRows.Skip(1),
                    (left, right) => left.LineId < right.LineId).All(value => value) &&
                snapshot.NextTranscriptLineId == snapshot.PrimaryScreen.VisibleRows[^1].LineId + 1,
                "Primary line IDs are not stable and monotonic.");
            Require(snapshot.PrimaryScreen.VisibleRows
                    .SelectMany(value => value.Runs)
                    .Any(value => value.Repeat > 1 && value.Grapheme.Length == 0 &&
                        value.DisplayWidth == 1 && !value.WideContinuation),
                "Snapshot rows did not use the canonical empty-grapheme RLE form for blank cells.");
            Require(snapshot.PrimaryScreen.Charset.Gr == 0 &&
                snapshot.PrimaryScreen.Cursor.Charset.Gr == 0 &&
                snapshot.PrimaryScreen.SavedCursor.Charset.Gr == 0 &&
                snapshot.AlternateScreen.Charset.Gr == 0 &&
                snapshot.AlternateScreen.Cursor.Charset.Gr == 0 &&
                snapshot.AlternateScreen.SavedCursor.Charset.Gr == 0,
                "Produced snapshot is outside the Android Termux charset subset.");
            Require(snapshot.ActiveScreen == TerminalScreenKindV1.Primary &&
                snapshot.PrimaryScreen.Cursor.Column == 2 &&
                snapshot.PrimaryScreen.Cursor.Row == 1 &&
                snapshot.AlternateScreen.Cursor.Column == snapshot.PrimaryScreen.Cursor.Column &&
                snapshot.AlternateScreen.Cursor.Row == snapshot.PrimaryScreen.Cursor.Row &&
                snapshot.AlternateScreen.Cursor.RenditionStyleIndex == snapshot.PrimaryScreen.Cursor.RenditionStyleIndex &&
                snapshot.AlternateScreen.Cursor.Charset.Equals(snapshot.PrimaryScreen.Cursor.Charset) &&
                snapshot.AlternateScreen.Cursor.OriginMode == snapshot.PrimaryScreen.Cursor.OriginMode &&
                snapshot.AlternateScreen.Cursor.PendingWrap == snapshot.PrimaryScreen.Cursor.PendingWrap &&
                snapshot.AlternateScreen.CurrentStyleIndex == snapshot.PrimaryScreen.CurrentStyleIndex &&
                snapshot.AlternateScreen.PendingWrap == snapshot.PrimaryScreen.PendingWrap &&
                snapshot.PrimaryScreen.SavedCursor.Column == 0 &&
                snapshot.PrimaryScreen.SavedCursor.Row == 0 &&
                snapshot.AlternateScreen.SavedCursor.Column == 0 &&
                snapshot.AlternateScreen.SavedCursor.Row == 0 &&
                !snapshot.PrimaryScreen.SavedCursor.PendingWrap &&
                !snapshot.AlternateScreen.SavedCursor.PendingWrap &&
                snapshot.PrimaryScreen.SavedCursor.Visible == snapshot.PrimaryScreen.Cursor.Visible &&
                snapshot.PrimaryScreen.SavedCursor.Style == snapshot.PrimaryScreen.Cursor.Style &&
                snapshot.PrimaryScreen.SavedCursor.Blink == snapshot.PrimaryScreen.Cursor.Blink &&
                snapshot.AlternateScreen.SavedCursor.Visible == snapshot.AlternateScreen.Cursor.Visible &&
                snapshot.AlternateScreen.SavedCursor.Style == snapshot.AlternateScreen.Cursor.Style &&
                snapshot.AlternateScreen.SavedCursor.Blink == snapshot.AlternateScreen.Cursor.Blink,
                "Blank alternate screen does not preserve the active cursor state required by Android Termux.");
            Require(snapshot.SnapshotSeq == 2 &&
                snapshot.Projection.Lifecycle == TerminalLifecycleV1.Running &&
                !snapshot.Projection.HasExitCode &&
                producer.ReadFrames(sessionId).Where(value => value.Sequence > snapshot.SnapshotSeq)
                    .Select(value => value.Type).SequenceEqual(new[] { LocalFrameType.Lifecycle }),
                "Snapshot S did not leave the ordered S+1 lifecycle event for realtime replay.");

            var outbox = producer.LoadOutbox(sessionId);
            Require(outbox != null && outbox.Artifacts.Count == 3 &&
                outbox.Artifacts[0].Kind == ProductionTerminalHistoryArtifactKind.Snapshot &&
                outbox.Artifacts[0].InitialTranscriptBeforeLineId ==
                    SnapshotCrypto.InitialTranscriptBeforeLineId(snapshot) &&
                outbox.Artifacts[0].TranscriptPageCount == 2 &&
                outbox.Artifacts[1].Kind == ProductionTerminalHistoryArtifactKind.Transcript &&
                outbox.Artifacts[2].Kind == ProductionTerminalHistoryArtifactKind.Transcript,
                "Terminal outbox does not enforce snapshot-first background transcript ordering.");
            var totalRows = 0;
            for (var index = 1; index < 3; index++)
            {
                var artifact = outbox.Artifacts[index];
                var page = TerminalTranscriptPageV1.Parser.ParseFrom(artifact.Plaintext);
                SnapshotCrypto.ValidateTranscriptForRequest(page, snapshot.SnapshotSeq, artifact.ExclusiveBeforeLineId);
                Require(page.Rows.Count <= 200 && page.Rows.All(value => value.SealedAtSeq == 2),
                    "Transcript page row bound or sealed sequence is invalid.");
                totalRows += page.Rows.Count;
            }
            Require(totalRows == 205, "Unexpected number of rows rolled into transcript history: " + totalRows + ".");
        }

        private static void ValidateCrashRecovery(string root, Guid pcDeviceId, Guid sessionId)
        {
            var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
            var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId);
            var before = producer.LoadOutbox(sessionId);
            var result = producer.ProcessSession(sessionId);
            var after = producer.LoadOutbox(sessionId);
            Require(result.SnapshotEligible && before.SnapshotSequence == after.SnapshotSequence &&
                before.Artifacts.Select(value => value.Plaintext).Zip(
                    after.Artifacts.Select(value => value.Plaintext),
                    (left, right) => left.SequenceEqual(right)).All(value => value),
                "Crash recovery did not deterministically reconstruct the WAL checkpoint.");
        }

        private static void ValidateLegacyOutboxOrderingMigration(
            ProductionTerminalHistoryStore store,
            ProductionTerminalHistoryProducer producer,
            Guid sessionId)
        {
            var snapshotFirst = producer.LoadOutbox(sessionId);
            var legacy = snapshotFirst.Clone();
            var legacySnapshot = legacy.Artifacts.Single(
                value => value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot);
            var expectedInitialTranscriptBeforeLineId = legacySnapshot.InitialTranscriptBeforeLineId;
            legacySnapshot.InitialTranscriptBeforeLineId = 0;
            legacySnapshot.TranscriptPageCount = 0;
            var ordered = legacy.Artifacts
                .OrderBy(value => value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot ? 1 : 0)
                .ThenBy(value => value.UploadOrder)
                .ToList();
            for (var index = 0; index < ordered.Count; index++) ordered[index].UploadOrder = index;
            legacy.Artifacts = ordered;
            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.Parse("dddddddd-eeee-4fff-8aaa-bbbbbbbbbbbb"),
                PairwiseKey = Enumerable.Range(97, 32).Select(value => (byte)value).ToArray(),
                KeyEpoch = 12
            };
            var deliveryKey = recipient.DeviceId.ToString("N") + ":" + recipient.KeyEpoch.ToString("D8");
            var uploadedTranscript = legacy.Artifacts.First(
                value => value.Kind == ProductionTerminalHistoryArtifactKind.Transcript);
            var legacyCiphertext = Enumerable.Range(1, 48).Select(value => (byte)value).ToArray();
            var legacySnapshotCiphertext = Enumerable.Range(49, 48).Select(value => (byte)value).ToArray();
            legacy.Deliveries[deliveryKey] = new ProductionTerminalHistoryDelivery
            {
                RecipientDeviceId = recipient.DeviceId,
                KeyEpoch = recipient.KeyEpoch,
                SealedObjects = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [uploadedTranscript.ArtifactId] = (byte[])legacyCiphertext.Clone(),
                    [legacySnapshot.ArtifactId] = (byte[])legacySnapshotCiphertext.Clone()
                },
                UploadedArtifactIds = new HashSet<string>(StringComparer.Ordinal)
                {
                    uploadedTranscript.ArtifactId,
                    legacySnapshot.ArtifactId
                }
            };
            var write = typeof(ProductionTerminalHistoryStore).GetMethod(
                "WriteJson",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?.MakeGenericMethod(typeof(ProductionTerminalHistoryOutbox));
            var path = Path.Combine(store.RootPath, sessionId.ToString("N"), "outbox.dpapi");
            Require(write != null, "Legacy outbox fixture could not access durable DPAPI writing.");
            write.Invoke(store, new object[] { path, sessionId, "outbox", legacy });

            var migrated = store.LoadOutbox(sessionId);
            var retained = migrated.Deliveries[deliveryKey];
            Require(migrated.Artifacts.OrderBy(value => value.UploadOrder).First().Kind ==
                    ProductionTerminalHistoryArtifactKind.Snapshot &&
                migrated.Artifacts.First().InitialTranscriptBeforeLineId == expectedInitialTranscriptBeforeLineId &&
                migrated.Artifacts.First().TranscriptPageCount == 2 &&
                retained.UploadedArtifactIds.Contains(uploadedTranscript.ArtifactId) &&
                !retained.UploadedArtifactIds.Contains(legacySnapshot.ArtifactId) &&
                retained.SealedObjects[uploadedTranscript.ArtifactId].SequenceEqual(legacyCiphertext) &&
                retained.SealedObjects[legacySnapshot.ArtifactId].SequenceEqual(legacySnapshotCiphertext),
                "Persisted legacy delivery state was lost while reprioritizing its snapshot: " +
                "first=" + migrated.Artifacts.First().Kind +
                ", before=" + migrated.Artifacts.First().InitialTranscriptBeforeLineId +
                ", pages=" + migrated.Artifacts.First().TranscriptPageCount +
                ", transcriptUploaded=" + retained.UploadedArtifactIds.Contains(uploadedTranscript.ArtifactId) +
                ", snapshotUploaded=" + retained.UploadedArtifactIds.Contains(legacySnapshot.ArtifactId) +
                ", transcriptSealed=" + retained.SealedObjects.ContainsKey(uploadedTranscript.ArtifactId) +
                ", snapshotSealed=" + retained.SealedObjects.ContainsKey(legacySnapshot.ArtifactId) + ".");
            var next = producer.PrepareNextUpload(sessionId, recipient);
            Require(next != null && next.Artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot,
                "A partially uploaded legacy outbox did not resume with its snapshot anchor.");
            CryptographicOperations.ZeroMemory(next.SealedObject);

            // Saving the producer's canonical form for the same S must merge
            // the migrated delivery rather than rejecting order-only drift.
            store.SaveOutbox(snapshotFirst);
            var merged = store.LoadOutbox(sessionId);
            Require(merged.Deliveries.ContainsKey(deliveryKey) &&
                merged.Artifacts.First().Kind == ProductionTerminalHistoryArtifactKind.Snapshot,
                "Same-sequence canonical outbox conflicted with its migrated legacy ordering.");
            CryptographicOperations.ZeroMemory(legacyCiphertext);
            CryptographicOperations.ZeroMemory(legacySnapshotCiphertext);
            CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
        }

        private static void ValidateOlderOutboxRecoveryAfterNewRejection(string root, Guid pcDeviceId)
        {
            var producer = new ProductionTerminalHistoryProducer(
                new ProductionTerminalHistoryStore(root, pcDeviceId),
                pcDeviceId);
            var sessionId = Guid.NewGuid();
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Resize,
                sessionId,
                1,
                LocalPayloadCodec.Resize(4, 2)));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                2,
                Encoding.UTF8.GetBytes("0000\r\n0001\r\n")));
            var eligible = producer.ProcessSession(sessionId);
            Require(eligible.SnapshotEligible, "Recovery fixture did not produce an eligible snapshot.");

            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.NewGuid(),
                PairwiseKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
                KeyEpoch = 1
            };
            var pending = producer.PrepareNextUpload(sessionId, recipient);
            Require(pending != null, "Recovery fixture did not persist an incomplete recipient delivery.");

            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                3,
                Encoding.UTF8.GetBytes("\u001bPq#0;2;0;0;0~\u001b\\")));
            var rejected = producer.ProcessSession(sessionId);
            var retained = producer.LoadOutbox(sessionId);
            var retry = producer.PrepareNextUpload(sessionId, recipient);
            Require(!rejected.SnapshotEligible && rejected.PermanentlyIneligible &&
                rejected.IneligibilityCode == "DCS_SIXEL_UNSUPPORTED" &&
                retained.SnapshotSequence == eligible.LastSequence &&
                retry != null && retry.SealedObject.SequenceEqual(pending.SealedObject),
                "A newer rejected prefix stranded or replaced an incomplete older terminal outbox.");
            CryptographicOperations.ZeroMemory(pending.SealedObject);
            CryptographicOperations.ZeroMemory(retry.SealedObject);
            CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
        }

        private static void ValidateNewSnapshotSupersedesIncompleteTranscript(
            string root,
            Guid pcDeviceId)
        {
            var sessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000051");
            var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
            var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId);
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Resize,
                sessionId,
                1,
                LocalPayloadCodec.Resize(4, 2)));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                2,
                Encoding.UTF8.GetBytes(string.Concat(
                    Enumerable.Range(0, 205).Select(value => $"old-{value:D3}\r\n")))));
            var oldProduction = producer.ProcessSession(sessionId);
            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000052"),
                PairwiseKey = Enumerable.Range(129, 32).Select(value => (byte)value).ToArray(),
                KeyEpoch = 13
            };
            var oldSnapshot = producer.PrepareNextUpload(sessionId, recipient);
            Require(oldProduction.SnapshotEligible && oldProduction.LastSequence == 2 &&
                oldSnapshot?.Artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot,
                "Supersede fixture did not create its old snapshot-first outbox.");
            producer.MarkUploaded(oldSnapshot);
            CryptographicOperations.ZeroMemory(oldSnapshot.SealedObject);
            var incompleteTranscript = producer.PrepareNextUpload(sessionId, recipient);
            Require(incompleteTranscript?.Artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript,
                "Supersede fixture did not retain an incomplete old transcript.");
            var oldArtifactId = incompleteTranscript.Artifact.ArtifactId;
            var oldSealedObject = (byte[])incompleteTranscript.SealedObject.Clone();
            CryptographicOperations.ZeroMemory(incompleteTranscript.SealedObject);

            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                sessionId,
                3,
                Encoding.UTF8.GetBytes("fresh-tail")));
            var freshProduction = producer.ProcessSession(sessionId);
            var superseded = producer.LoadOutbox(sessionId);
            var directory = Path.Combine(store.RootPath, sessionId.ToString("N"));
            Require(freshProduction.SnapshotEligible && freshProduction.LastSequence == 3 &&
                superseded.SnapshotSequence == 3 &&
                superseded.Artifacts.First().Kind == ProductionTerminalHistoryArtifactKind.Snapshot &&
                superseded.Artifacts.All(value => value.SnapshotSequence == 3 && value.ArtifactId != oldArtifactId) &&
                superseded.Deliveries.Count == 0 &&
                !superseded.Deliveries.Values.Any(value => value.SealedObjects.Values.Any(
                    sealedObject => sealedObject.SequenceEqual(oldSealedObject))) &&
                Directory.GetFiles(directory, "outbox.dpapi.*.tmp").Length == 0,
                "A fresh snapshot did not atomically supersede an incomplete old transcript outbox.");
            CryptographicOperations.ZeroMemory(oldSealedObject);
            CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
        }

        private static void ValidateGapAndUnsupportedFallback(string root, Guid pcDeviceId)
        {
            var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
            var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId);
            var gapSession = Guid.NewGuid();
            producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, gapSession, 1, Encoding.UTF8.GetBytes("ok")));
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Gap,
                gapSession,
                3,
                LocalPayloadCodec.Gap(2, 2)));
            var gap = producer.ProcessSession(gapSession);
            Require(gap.SnapshotEligible && gap.LastSequence == 1 &&
                producer.ReadFrames(gapSession).Count == 2,
                "A source Gap after snapshot S did not remain available for S+1 replay/CHUNK fallback.");

            var unsupportedSession = Guid.NewGuid();
            producer.AppendCommittedFrame(new LocalFrame(
                LocalFrameType.Output,
                unsupportedSession,
                1,
                Encoding.UTF8.GetBytes("中\u001b]0;title\u0007文")));
            var unsupported = producer.ProcessSession(unsupportedSession);
            var unicodeCheckpoint = producer.LoadCheckpoint(unsupportedSession);
            var unicodeSnapshot = TerminalSnapshotV1.Parser.ParseFrom(unicodeCheckpoint.SnapshotPlaintext);
            var unicodeRuns = unicodeSnapshot.PrimaryScreen.VisibleRows[0].Runs;
            Require(unsupported.SnapshotEligible && unsupported.LastSequence == 1 &&
                producer.ReadFrames(unsupportedSession).Count == 1 &&
                unicodeRuns[0].Grapheme == "中" && unicodeRuns[0].DisplayWidth == 2 &&
                unicodeRuns[1].WideContinuation && unicodeRuns[1].DisplayWidth == 0 &&
                unicodeRuns[1].Grapheme.Length == 0 &&
                unicodeRuns[2].Grapheme == "文" && unicodeRuns[2].DisplayWidth == 2,
                "Common Chinese/OSC output permanently disabled terminal snapshots.");

            var incrementalSession = Guid.NewGuid();
            var first = new LocalFrame(
                LocalFrameType.Output,
                incrementalSession,
                1,
                Encoding.UTF8.GetBytes("first"));
            producer.AppendCommittedFrame(first);
            var incremental = producer.ProcessSession(incrementalSession, new[] { first });
            var title = new LocalFrame(
                LocalFrameType.Title,
                incrementalSession,
                2,
                LocalPayloadCodec.Title("incremental"));
            var suffix = new LocalFrame(
                LocalFrameType.Output,
                incrementalSession,
                3,
                Encoding.UTF8.GetBytes("-suffix"));
            producer.AppendCommittedFrame(title);
            producer.AppendCommittedFrame(suffix);
            incremental = producer.ProcessSession(incrementalSession, new[] { title, suffix });
            Require(incremental.SnapshotEligible && incremental.LastSequence == 3 &&
                store.ReadFramesAfter(incrementalSession, 1)
                    .Select(value => value.Sequence).SequenceEqual(new[] { 2UL, 3UL }),
                "Incremental history production rescanned or skipped its coalesced suffix.");

            var yieldingSession = Guid.NewGuid();
            var yieldingFrames = new[]
            {
                new LocalFrame(
                    LocalFrameType.Output,
                    yieldingSession,
                    1,
                    Encoding.UTF8.GetBytes("first")),
                new LocalFrame(
                    LocalFrameType.Output,
                    yieldingSession,
                    2,
                    Encoding.UTF8.GetBytes("-second"))
            };
            foreach (var frame in yieldingFrames) producer.AppendCommittedFrame(frame);
            var readChecks = 0;
            var interruptedRead = store.ReadFrames(
                yieldingSession,
                shouldYield: () => ++readChecks >= 2);
            Require(interruptedRead == null && store.ReadFrames(yieldingSession).Count == 2,
                "Yielded DPAPI WAL read changed or lost its immutable persisted frames.");
            var yieldChecks = 0;
            var yielded = producer.ProcessSession(
                yieldingSession,
                yieldingFrames,
                persistLegacyArtifacts: true,
                shouldYield: () => ++yieldChecks >= 2);
            Require(yielded == null,
                "Persisted terminal replay did not yield when live work became ready.");
            var resumed = producer.ProcessSession(
                yieldingSession,
                yieldingFrames,
                persistLegacyArtifacts: true,
                shouldYield: () => false);
            Require(resumed.SnapshotEligible && resumed.LastSequence == 2,
                "Yielded terminal replay did not resume from its ordered applied sequence.");
        }

        private static async Task ValidateRecipientSealingAndUploaderAsync(
            string root,
            Guid pcDeviceId,
            Guid sessionId)
        {
            var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
            var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId);
            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
                PairwiseKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
                KeyEpoch = 7
            };
            var first = producer.PrepareNextUpload(sessionId, recipient);
            Require(first != null && first.Artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot,
                "Terminal delivery did not start with its snapshot anchor.");
            var retry = producer.PrepareNextUpload(sessionId, recipient);
            Require(first.SealedObject.SequenceEqual(retry.SealedObject),
                "Same recipient/epoch retry did not reuse sealed bytes and nonce.");
            var restarted = new ProductionTerminalHistoryProducer(
                new ProductionTerminalHistoryStore(root, pcDeviceId),
                pcDeviceId);
            var crashRetry = restarted.PrepareNextUpload(sessionId, recipient);
            Require(first.SealedObject.SequenceEqual(crashRetry.SealedObject),
                "Crash retry did not reuse the persisted sealed object.");

            var putHandler = new PutHandler();
            var api = new FakeHistoryApi();
            using var http = new HttpClient(putHandler);
            using var disabledUploader = new ProductionHistoryUploader(
                http,
                api.SendAsync,
                pcDeviceId,
                store);
            var disabledOutcome = await disabledUploader.UploadTerminalArtifactAsync(
                sessionId,
                recipient,
                first,
                CancellationToken.None);
            Require(disabledOutcome == ProductionHistoryUploadOutcome.Unavailable &&
                api.Creates.Count == 0 && putHandler.Payloads.Count == 0 && api.Commits == 0,
                "A CHUNK-only uploader attempted to upload an unadvertised terminal object.");
            using var uploader = new ProductionHistoryUploader(http, api.SendAsync, pcDeviceId, store, true);
            var uploadedKinds = new List<ProductionTerminalHistoryArtifactKind>();
            while (true)
            {
                var pending = producer.PrepareNextUpload(sessionId, recipient);
                if (pending == null) break;
                var outcome = await uploader.UploadTerminalArtifactAsync(
                    sessionId,
                    recipient,
                    pending,
                    CancellationToken.None);
                Require(outcome == ProductionHistoryUploadOutcome.Durable, "Terminal artifact did not complete create-PUT-commit.");
                uploadedKinds.Add(pending.Artifact.Kind);
                producer.MarkUploaded(pending);
            }
            Require(uploadedKinds.SequenceEqual(new[]
                {
                    ProductionTerminalHistoryArtifactKind.Snapshot,
                    ProductionTerminalHistoryArtifactKind.Transcript,
                    ProductionTerminalHistoryArtifactKind.Transcript
                }),
                "Uploader violated snapshot-first background transcript ordering.");
            Require(producer.AreAllUploaded(sessionId, 2, recipient), "Recipient delivery completion was not durable.");
            ValidateCreateMetadata(api.Creates, first.Artifact.InitialTranscriptBeforeLineId);
            Require(putHandler.Payloads.Count == 3 && api.Commits == 3,
                "Uploader did not execute one PUT and commit per artifact.");
            Require(api.IdempotencyKeys.All(value => value != null && value.Length >= 8 && value.Length <= 128),
                "Terminal artifact upload emitted an invalid server idempotency key.");

            var epoch8 = new ProductionHistoryRecipient
            {
                DeviceId = recipient.DeviceId,
                PairwiseKey = (byte[])recipient.PairwiseKey.Clone(),
                KeyEpoch = 8
            };
            var resealed = producer.PrepareNextUpload(sessionId, epoch8);
            Require(resealed != null && !resealed.SealedObject.SequenceEqual(first.SealedObject),
                "Recipient epoch change reused stale ciphertext.");
            var container = TerminalSnapshotCiphertextV1.Parser.ParseFrom(resealed.SealedObject);
            Require(container.KeyEpoch == 8 && container.KeyId == SnapshotCrypto.SnapshotKeyId(epoch8.DeviceId, 8),
                "Recipient epoch change did not reseal with new authenticated metadata.");
            CryptographicOperations.ZeroMemory(first.SealedObject);
            CryptographicOperations.ZeroMemory(retry.SealedObject);
            CryptographicOperations.ZeroMemory(crashRetry.SealedObject);
            CryptographicOperations.ZeroMemory(resealed.SealedObject);
            CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
            CryptographicOperations.ZeroMemory(epoch8.PairwiseKey);
        }

        private static async Task ValidateChunkGapFallbackUploaderAsync(string root, Guid pcDeviceId)
        {
            var sessionId = Guid.NewGuid();
            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.NewGuid(),
                PairwiseKey = Enumerable.Range(33, 32).Select(value => (byte)value).ToArray(),
                KeyEpoch = 9
            };
            var events = new List<HistoryEventV1>
            {
                new HistoryEventV1
                {
                    Seq = 1,
                    MessageType = MessageType.OutputChunk,
                    Payload = ByteString.CopyFrom(
                        new OutputChunkPayload { Output = ByteString.CopyFromUtf8("prefix") }.ToByteArray())
                },
                new HistoryEventV1
                {
                    Seq = 3,
                    MessageType = MessageType.Gap,
                    Payload = ByteString.CopyFrom(
                        new GapPayload { Start = 2, End = 2, ReasonCode = "SOURCE_GAP" }.ToByteArray())
                }
            };
            var durableStore = new ProductionTerminalHistoryStore(root, pcDeviceId);
            var unavailableApi = new FakeHistoryApi { NextCreateStatus = HttpStatusCode.ServiceUnavailable };
            using (var unavailableHttp = new HttpClient(new PutHandler()))
            using (var unavailableUploader = new ProductionHistoryUploader(
                unavailableHttp, unavailableApi.SendAsync, pcDeviceId, durableStore))
            {
                var unavailable = await unavailableUploader.UploadAsync(
                    sessionId, recipient, events, CancellationToken.None);
                Require(unavailable == ProductionHistoryUploadOutcome.Unavailable && unavailableApi.Creates.Count == 1,
                    "CHUNK failure did not retain a retryable outbox.");
            }

            var putHandler = new PutHandler();
            var api = new FakeHistoryApi();
            using var http = new HttpClient(putHandler);
            using var uploader = new ProductionHistoryUploader(http, api.SendAsync, pcDeviceId, durableStore);
            var outcome = await uploader.UploadAsync(sessionId, recipient, events, CancellationToken.None);
            Require(outcome == ProductionHistoryUploadOutcome.Durable && api.Creates.Count == 2 &&
                api.Creates.All(value => value.GetProperty("kind").GetString() == "CHUNK") &&
                api.Creates[0].GetProperty("ciphertext_sha256").GetString() ==
                    unavailableApi.Creates[0].GetProperty("ciphertext_sha256").GetString() &&
                api.IdempotencyKeys[0] != unavailableApi.IdempotencyKeys[0] &&
                api.IdempotencyKeys[0].StartsWith("history-create-v2-chunk-", StringComparison.Ordinal) &&
                unavailableApi.IdempotencyKeys[0].StartsWith("history-create-v2-chunk-", StringComparison.Ordinal) &&
                api.IdempotencyKeys.All(value => value != null && value.Length >= 8 && value.Length <= 128) &&
                api.Creates[0].GetProperty("start_seq").GetUInt64() == 1 &&
                api.Creates[0].GetProperty("end_seq").GetUInt64() == 1 &&
                api.Creates[1].GetProperty("start_seq").GetUInt64() == 3 &&
                api.Creates[1].GetProperty("end_seq").GetUInt64() == 3 &&
                putHandler.Payloads.Count == 2 && api.Commits == 2,
                "Gap-marked WAL did not continue through CHUNK create-PUT-commit fallback.");
            CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
        }

        private static async Task ValidateChunkYieldAndResumeAsync(string root, Guid pcDeviceId)
        {
            var sessionId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000061");
            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000062"),
                PairwiseKey = Enumerable.Range(161, 32).Select(value => (byte)value).ToArray(),
                KeyEpoch = 14
            };
            var events = new List<HistoryEventV1>();
            foreach (var sequence in new[] { 1UL, 3UL, 5UL })
            {
                events.Add(new HistoryEventV1
                {
                    Seq = sequence,
                    MessageType = MessageType.OutputChunk,
                    Payload = ByteString.CopyFrom(
                        new OutputChunkPayload { Output = ByteString.CopyFromUtf8("batch-" + sequence) }.ToByteArray())
                });
            }
            var api = new FakeHistoryApi { TrackCommittedChunks = true };
            var putHandler = new PutHandler();
            var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
            using var http = new HttpClient(putHandler);
            var yieldChecks = 0;
            using (var firstUploader = new ProductionHistoryUploader(
                http,
                api.SendAsync,
                pcDeviceId,
                store))
            {
                var yielded = await firstUploader.UploadAsync(
                    sessionId,
                    recipient,
                    events,
                    CancellationToken.None,
                    () => ++yieldChecks > 1);
                Require(yielded == ProductionHistoryUploadOutcome.Unavailable &&
                    api.Creates.Count == 1 && api.Commits == 1 && putHandler.Payloads.Count == 1 &&
                    api.Creates[0].GetProperty("start_seq").GetUInt64() == 1 &&
                    api.Creates[0].GetProperty("end_seq").GetUInt64() == 1,
                    "CHUNK uploader did not yield after its first durable batch.");
            }

            using (var resumedUploader = new ProductionHistoryUploader(
                http,
                api.SendAsync,
                pcDeviceId,
                store))
            {
                var resumed = await resumedUploader.UploadAsync(
                    sessionId,
                    recipient,
                    events,
                    CancellationToken.None);
                Require(resumed == ProductionHistoryUploadOutcome.Durable &&
                    api.Creates.Count == 3 && api.Commits == 3 && putHandler.Payloads.Count == 3 &&
                    api.Creates.Select(value => value.GetProperty("start_seq").GetUInt64())
                        .SequenceEqual(new[] { 1UL, 3UL, 5UL }) &&
                    api.CommittedChunks.Select(value => value.GetProperty("end_seq").GetUInt64())
                        .SequenceEqual(new[] { 1UL, 3UL, 5UL }),
                    "CHUNK resume repeated its committed prefix or skipped a pending batch.");
            }
            CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
        }

        private static async Task ValidateConcurrentUploaderDisposeAsync(Guid pcDeviceId)
        {
            var sessionId = Guid.NewGuid();
            var recipient = new ProductionHistoryRecipient
            {
                DeviceId = Guid.NewGuid(),
                PairwiseKey = Enumerable.Range(65, 32).Select(value => (byte)value).ToArray(),
                KeyEpoch = 11
            };
            var events = new List<HistoryEventV1>
            {
                new HistoryEventV1
                {
                    Seq = 1,
                    MessageType = MessageType.OutputChunk,
                    Payload = ByteString.CopyFrom(
                        new OutputChunkPayload { Output = ByteString.CopyFromUtf8("dispose-race") }.ToByteArray())
                }
            };
            var putHandler = new BlockingPutHandler();
            var api = new FakeHistoryApi();
            using var http = new HttpClient(putHandler);
            var uploader = new ProductionHistoryUploader(http, api.SendAsync, pcDeviceId);
            var first = uploader.UploadAsync(sessionId, recipient, events, CancellationToken.None);
            await putHandler.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var second = uploader.UploadAsync(sessionId, recipient, events, CancellationToken.None);

            var activeOperations = typeof(ProductionHistoryUploader).GetField(
                "activeOperations",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Require(activeOperations != null && SpinWait.SpinUntil(
                    () => (int)activeOperations.GetValue(uploader) == 2,
                    TimeSpan.FromSeconds(5)),
                "Concurrent uploader fixture did not place a waiter behind the active per-key gate.");

            var dispose = Task.Run(uploader.Dispose);
            Require(!dispose.Wait(TimeSpan.FromMilliseconds(100)),
                "Uploader Dispose returned before active operations drained.");
            putHandler.Release();
            var outcomes = await Task.WhenAll(first, second);
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            Require(outcomes.All(value => value == ProductionHistoryUploadOutcome.Durable),
                "Uploader Dispose disrupted an operation that had already entered its per-key gate.");
            var afterDispose = await uploader.UploadAsync(sessionId, recipient, events, CancellationToken.None);
            Require(afterDispose == ProductionHistoryUploadOutcome.Unavailable,
                "Disposed uploader accepted a new operation.");
            CryptographicOperations.ZeroMemory(recipient.PairwiseKey);
        }

        private static void ValidateCreateMetadata(
            IList<JsonElement> creates,
            ulong expectedInitialTranscriptBeforeLineId)
        {
            Require(creates.Count == 3, "Unexpected history create count.");
            var snapshot = creates[0];
            Require(snapshot.GetProperty("kind").GetString() == "SNAPSHOT" &&
                snapshot.GetProperty("snapshot_seq").GetUInt64() == 2 &&
                snapshot.GetProperty("rows").GetInt32() == 2 &&
                snapshot.GetProperty("columns").GetInt32() == 4 &&
                snapshot.GetProperty("transcript_initial_before_line_id").GetUInt64() ==
                    expectedInitialTranscriptBeforeLineId &&
                snapshot.GetProperty("transcript_page_count").GetInt32() == 2 &&
                !snapshot.TryGetProperty("as_of_snapshot_seq", out _) &&
                snapshot.GetProperty("format_name").GetString() == SnapshotCrypto.SnapshotFormatName &&
                snapshot.EnumerateObject().Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(new[]
                    {
                        "ciphertext_sha256", "columns", "format_name", "format_version", "key_id", "kind", "rows",
                        "size_bytes", "snapshot_seq", "transcript_initial_before_line_id", "transcript_page_count"
                    }),
                "SNAPSHOT create metadata does not match the server contract.");
            for (var index = 1; index < 3; index++)
            {
                var item = creates[index];
                Require(item.GetProperty("kind").GetString() == "TRANSCRIPT" &&
                    item.TryGetProperty("as_of_snapshot_seq", out var asOf) && asOf.GetUInt64() == 2 &&
                    item.TryGetProperty("first_line_id", out _) && item.TryGetProperty("last_line_id", out _) &&
                    !item.TryGetProperty("snapshot_seq", out _) && !item.TryGetProperty("rows", out _) &&
                    !item.TryGetProperty("columns", out _) &&
                    !item.TryGetProperty("transcript_initial_before_line_id", out _) &&
                    !item.TryGetProperty("transcript_page_count", out _) &&
                    item.GetProperty("format_name").GetString() == SnapshotCrypto.TranscriptFormatName &&
                    item.EnumerateObject().Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(new[]
                        {
                            "as_of_snapshot_seq", "ciphertext_sha256", "first_line_id", "format_name", "format_version",
                            "key_id", "kind", "last_line_id", "size_bytes"
                        }),
                    "TRANSCRIPT create metadata does not match the server contract.");
            }
        }

        private static bool Contains(byte[] source, byte[] value)
        {
            if (value.Length == 0) return true;
            for (var index = 0; index <= source.Length - value.Length; index++)
            {
                var match = true;
                for (var offset = 0; offset < value.Length; offset++)
                {
                    if (source[index + offset] == value[offset]) continue;
                    match = false;
                    break;
                }
                if (match) return true;
            }
            return false;
        }

        private static void Expect<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static async Task ExpectAsync<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
