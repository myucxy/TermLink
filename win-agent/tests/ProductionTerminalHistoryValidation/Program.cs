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
                var store = new ProductionTerminalHistoryStore(root, pcDeviceId);
                var producer = new ProductionTerminalHistoryProducer(store, pcDeviceId);
                AppendSupportedSession(producer, sessionId);
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
            var buildHello = runtimeType.GetMethod("BuildHelloPayload", staticFlags);
            var process = runtimeType.GetMethod("ProcessTerminalHistoryIfEnabled", staticFlags);
            var recordPublication = runtimeType.GetMethod("RecordPublicationOutcome", staticFlags);
            var validateEnvelopeSize = runtimeType.GetMethod("ValidateRealtimeEnvelopeSize", staticFlags);
            Require(parseFlag != null && buildHello != null && process != null && recordPublication != null &&
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

            Require((bool)parseFlag.Invoke(null, new object[] { null }) &&
                (bool)parseFlag.Invoke(null, new object[] { "1" }) &&
                (bool)parseFlag.Invoke(null, new object[] { "true" }) &&
                !(bool)parseFlag.Invoke(null, new object[] { "0" }) &&
                (bool)parseFlag.Invoke(null, new object[] { " 0" }),
                "Terminal snapshot runtime must default on and accept only exact 0 as the emergency rollback.");

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
                Encoding.UTF8.GetBytes("\u001bP1;2|unsupported\u001b\\")));
            var rejected = producer.ProcessSession(sessionId);
            var retained = producer.LoadOutbox(sessionId);
            var retry = producer.PrepareNextUpload(sessionId, recipient);
            Require(!rejected.SnapshotEligible && rejected.PermanentlyIneligible &&
                rejected.IneligibilityCode == "DCS_UNSUPPORTED" &&
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
