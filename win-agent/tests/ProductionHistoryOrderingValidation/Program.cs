using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Protocol;

namespace TermLink.ProductionHistoryOrdering.Validation;

internal static class Program
{
    private sealed class OrderingHandler : HttpMessageHandler
    {
        private readonly byte[] grants;
        private readonly object sync = new();
        private readonly Dictionary<Guid, (string Kind, ulong Sequence, string KeyId)> uploads = new();
        private int uploadNumber;

        internal readonly List<string> Operations = new();
        internal ulong ConfirmedLastOutputSequence;
        internal bool FailFirstSnapshot;
        internal bool FailFirstHistoryCursor;
        internal bool PauseFirstSnapshot;
        internal readonly TaskCompletionSource SnapshotCreateEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AllowSnapshotCreate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Dictionary<string, ulong> DurableSnapshotSequences = new(StringComparer.Ordinal);
        private bool snapshotFailed;
        private bool historyCursorFailed;
        private bool snapshotPaused;

        internal OrderingHandler(
            IEnumerable<(Guid DeviceId, byte[] ExchangePublicKey, byte[] SigningPublicKey)> recipients,
            long keyEpoch)
        {
            grants = JsonSerializer.SerializeToUtf8Bytes(new
            {
                key_epoch = keyEpoch,
                items = recipients.Select(value => new
                    {
                        device_id = value.DeviceId,
                        device_type = "ANDROID",
                        exchange_public_key = Convert.ToBase64String(value.ExchangePublicKey),
                        signing_public_key = Convert.ToBase64String(value.SigningPublicKey)
                    }).ToArray()
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri.AbsolutePath;
            var body = request.Content == null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (request.Method == HttpMethod.Get && path.EndsWith("/grants", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, grants);
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/v1/sessions/", StringComparison.Ordinal) &&
                !request.RequestUri.Host.Equals("upload.invalid", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(body);
                var confirmed = document.RootElement.GetProperty("last_output_seq").GetUInt64();
                lock (sync)
                {
                    ConfirmedLastOutputSequence = Math.Max(ConfirmedLastOutputSequence, confirmed);
                    Operations.Add("METADATA:" + confirmed);
                }
                return Json(HttpStatusCode.OK, "{}"u8.ToArray());
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/history", StringComparison.Ordinal))
            {
                Operations.Add("CURSOR");
                lock (sync)
                {
                    if (FailFirstHistoryCursor && !historyCursorFailed)
                    {
                        historyCursorFailed = true;
                        return Json(HttpStatusCode.ServiceUnavailable, "{}"u8.ToArray());
                    }
                }
                return Json(HttpStatusCode.OK, "{\"items\":[]}"u8.ToArray());
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/history/uploads", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(body);
                var kind = document.RootElement.GetProperty("kind").GetString();
                if (kind == "SNAPSHOT")
                {
                    var formatName = document.RootElement.GetProperty("format_name").GetString();
                    var formatVersion = document.RootElement.GetProperty("format_version").GetUInt32();
                    if (formatName != SnapshotCrypto.TerminalCoreSnapshotFormatName ||
                        formatVersion != SnapshotCrypto.TerminalCoreSnapshotFormatVersion)
                    {
                        throw new InvalidOperationException(
                            "Snapshot create metadata did not use terminal-core V2: " +
                            $"format_name={formatName}, format_version={formatVersion}.");
                    }
                }
                var sequence = kind == "SNAPSHOT"
                    ? document.RootElement.GetProperty("snapshot_seq").GetUInt64()
                    : document.RootElement.TryGetProperty("as_of_snapshot_seq", out var anchor)
                        ? anchor.GetUInt64()
                        : 0UL;
                var keyId = document.RootElement.GetProperty("key_id").GetString() ?? string.Empty;
                var pause = false;
                lock (sync)
                {
                    Operations.Add(kind);
                    if (kind == "SNAPSHOT" && PauseFirstSnapshot && !snapshotPaused)
                    {
                        snapshotPaused = true;
                        pause = true;
                    }
                }
                if (pause)
                {
                    SnapshotCreateEntered.TrySetResult();
                    await AllowSnapshotCreate.Task.WaitAsync(cancellationToken);
                }
                lock (sync)
                {
                    if (kind == "SNAPSHOT" && FailFirstSnapshot && !snapshotFailed)
                    {
                        snapshotFailed = true;
                        return Json(HttpStatusCode.ServiceUnavailable, "{}"u8.ToArray());
                    }
                }
                Guid uploadId;
                lock (sync)
                {
                    uploadId = Guid.Parse($"00000000-0000-4000-8000-{++uploadNumber:D12}");
                    uploads[uploadId] = (kind, sequence, keyId);
                }
                return Json(HttpStatusCode.Created, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    upload_id = uploadId,
                    upload = new
                    {
                        method = "PUT",
                        url = "https://upload.invalid/" + uploadId.ToString("N"),
                        headers = new Dictionary<string, string[]>()
                    }
                }));
            }
            if (request.Method == HttpMethod.Put &&
                request.RequestUri.Host.Equals("upload.invalid", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Post && path.Contains("/history/uploads/", StringComparison.Ordinal) &&
                path.EndsWith("/commit", StringComparison.Ordinal))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var uploadId = Guid.Parse(segments[^2]);
                lock (sync)
                {
                    if (uploads.TryGetValue(uploadId, out var upload) && upload.Kind == "SNAPSHOT")
                        DurableSnapshotSequences[upload.KeyId] = upload.Sequence;
                }
                return Json(HttpStatusCode.Created, "{}"u8.ToArray());
            }
            throw new InvalidOperationException("Unexpected validation request: " + request.Method + " " + request.RequestUri);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, byte[] body) => new(status)
        {
            Content = new ByteArrayContent(body)
        };
    }

    public static async Task<int> Main()
    {
        // This validation exercises the opt-in TerminalSnapshot publication
        // path. Keep the fixture independent of the caller's environment so
        // a clean developer shell cannot silently take the generic history
        // path and complete before the Snapshot barrier is reached.
        Environment.SetEnvironmentVariable(
            "TERMLINK_ENABLE_TERMINAL_SNAPSHOT_V2",
            "1",
            EnvironmentVariableTarget.Process);
        var root = Path.Combine(Path.GetTempPath(), "termlink-history-ordering-" + Guid.NewGuid().ToString("N"));
        object runtime = null;
        OrderingHandler handler = null;
        DeviceExchangeKeyPair remoteExchange = null;
        DeviceExchangeKeyPair secondRemoteExchange = null;
        try
        {
            Directory.CreateDirectory(root);
            var assembly = Assembly.Load("TermLink.Agent");
            var runtimeType = Type(assembly, "TermLink.Agent.ProductionAgentRuntime");
            var configType = Type(assembly, "TermLink.Agent.ProductionAgentConfig");
            var credentialsType = Type(assembly, "TermLink.Agent.ProductionCredentials");
            var storeType = Type(assembly, "TermLink.Agent.ProductionTerminalHistoryStore");
            var producerType = Type(assembly, "TermLink.Agent.ProductionTerminalHistoryProducer");
            var uploaderType = Type(assembly, "TermLink.Agent.ProductionHistoryUploader");
            var localFrameType = Type(assembly, "TermLink.LocalIpc.LocalFrame");
            var localFrameKindType = Type(assembly, "TermLink.LocalIpc.LocalFrameType");
            var payloadCodecType = Type(assembly, "TermLink.LocalIpc.LocalPayloadCodec");

            var backlogSession = Guid.Parse("00000000-0000-4000-8000-000000000010");
            var terminalSession = Guid.Parse("00000000-0000-4000-8000-000000000020");
            var activeSession = Guid.Parse("00000000-0000-4000-8000-000000000030");
            var orderedCandidates = (IEnumerable<Guid>)Method(
                runtimeType,
                "OrderHistoryUploadCandidates").Invoke(
                    null,
                    new object[]
                    {
                        new[] { backlogSession, activeSession, terminalSession, backlogSession },
                        new Func<Guid, int>(value => value == activeSession ? 0 : value == terminalSession ? 1 : 2)
                    });
            Require(
                orderedCandidates.SequenceEqual(new[] { activeSession, terminalSession, backlogSession }),
                "History upload scheduling did not prioritize active and terminal sessions ahead of old backlog.");
            var canPromoteBacklog = Method(runtimeType, "CanPromotePersistedHistoryBacklog");
            bool CanPromote(
                bool hasBacklog,
                bool hasActiveSession,
                bool hasRealtimeHistoryWork,
                long lastLiveTick,
                long nowTick,
                long idleDelay) => (bool)canPromoteBacklog.Invoke(
                    null,
                    new object[]
                    {
                        hasBacklog,
                        hasActiveSession,
                        hasRealtimeHistoryWork,
                        lastLiveTick,
                        nowTick,
                        idleDelay
                    });
            Require(!CanPromote(true, false, false, 1_000, 5_999, 5_000),
                "Persisted history backlog ignored the required idle interval.");
            Require(!CanPromote(true, true, false, 1_000, 6_000, 5_000),
                "Persisted history backlog ran while a live terminal existed.");
            Require(!CanPromote(true, false, true, 1_000, 6_000, 5_000),
                "Persisted history backlog ran while realtime history work was pending.");
            Require(CanPromote(true, false, false, 1_000, 6_000, 5_000),
                "Persisted history backlog did not resume after a verified idle interval.");

            var pcId = Guid.Parse("10000000-0000-4000-8000-000000000001");
            var pcDeviceId = Guid.Parse("20000000-0000-4000-8000-000000000002");
            var recipientDeviceId = Guid.Parse("30000000-0000-4000-8000-000000000003");
            var secondRecipientDeviceId = Guid.Parse("30000000-0000-4000-8000-000000000005");
            var sessionId = Guid.Parse("40000000-0000-4000-8000-000000000004");
            const long keyEpoch = 7;

            var config = Activator.CreateInstance(configType, nonPublic: true);
            Set(config, "ServerUrl", "https://ordering.invalid");
            Set(config, "PcId", pcId);
            Set(config, "PcName", "Ordering Validation PC");
            Set(config, "AgentVersion", "validation-1");
            Set(config, "KeyEpoch", keyEpoch);
            var credentials = Activator.CreateInstance(credentialsType, nonPublic: true);
            Set(credentials, "DeviceId", pcDeviceId);
            Set(credentials, "Identity", DeviceIdentity.Generate());
            Set(credentials, "AccessToken", "ordering-validation-token");
            Set(credentials, "AccessTokenExpiresAtUtc", DateTime.MaxValue);
            var constructor = runtimeType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { configType, credentialsType },
                modifiers: null) ?? throw new InvalidOperationException("Production runtime constructor is unavailable.");
            runtime = constructor.Invoke(new[] { config, credentials });

            remoteExchange = DeviceExchangeKeyPair.Generate();
            secondRemoteExchange = DeviceExchangeKeyPair.Generate();
            handler = new OrderingHandler(new[]
            {
                (recipientDeviceId, remoteExchange.PublicKey, Enumerable.Range(1, 32)
                    .Select(value => (byte)value).ToArray()),
                (secondRecipientDeviceId, secondRemoteExchange.PublicKey, Enumerable.Range(33, 32)
                    .Select(value => (byte)value).ToArray())
            }, keyEpoch)
            {
                FailFirstSnapshot = true,
                FailFirstHistoryCursor = true,
                PauseFirstSnapshot = true
            };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var httpField = Field(runtimeType, "http");
            ((HttpClient)httpField.GetValue(runtime)).Dispose();
            httpField.SetValue(runtime, http);

            var store = Activator.CreateInstance(storeType, root, pcDeviceId);
            var producer = Activator.CreateInstance(
                producerType,
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { store, pcDeviceId, false, true, null },
                culture: null);
            Field(runtimeType, "terminalHistoryStore").SetValue(runtime, store);
            Field(runtimeType, "terminalHistoryProducer").SetValue(runtime, producer);
            var sendType = typeof(Func<HttpMethod, string, byte[], bool, string, CancellationToken, Task<HttpResponseMessage>>);
            var send = Method(runtimeType, "SendAsync").CreateDelegate(sendType, runtime);
            var uploader = Activator.CreateInstance(
                uploaderType,
                http,
                send,
                pcDeviceId,
                store,
                true);
            Field(runtimeType, "historyUploader").SetValue(runtime, uploader);

            var frameConstructor = localFrameType.GetConstructor(new[]
            {
                localFrameKindType, typeof(Guid), typeof(ulong), typeof(byte[])
            }) ?? throw new InvalidOperationException("Local frame constructor is unavailable.");
            var resizePayload = (byte[])payloadCodecType.GetMethod(
                "Resize",
                BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { (short)4, (short)2 });
            var resize = frameConstructor.Invoke(new[]
            {
                Enum.Parse(localFrameKindType, "Resize"), sessionId, 1UL, resizePayload
            });
            var outputText = string.Join(string.Empty, Enumerable.Range(0, 205).Select(index => $"L{index:D3}\r\n"));
            var output = frameConstructor.Invoke(new[]
            {
                Enum.Parse(localFrameKindType, "Output"), sessionId, 2UL, Encoding.UTF8.GetBytes(outputText)
            });
            var append = Method(producerType, "AppendCommittedFrame", publicOnly: true);
            append.Invoke(producer, new[] { resize });
            append.Invoke(producer, new[] { output });
            var production = Method(producerType, "ProcessSession", publicOnly: true, parameterCount: 1)
                .Invoke(producer, new object[] { sessionId });

            var onCommitted = runtimeType.GetMethod("OnCommitted", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("OnCommitted is unavailable.");
            var shellType = onCommitted.GetParameters()[0].ParameterType;
            var shell = Enum.Parse(shellType, "WindowsPowerShell");
            onCommitted.Invoke(runtime, new[] { shell, resize });
            onCommitted.Invoke(runtime, new[] { shell, output });
            // This fixture invokes UploadHistoryAsync directly instead of the
            // worker that normally consumes this queue entry first.
            var pendingHistory = Field(runtimeType, "pendingHistorySessions").GetValue(runtime);
            pendingHistory.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public)
                ?.Invoke(pendingHistory, null);

            var buildEvent = Method(runtimeType, "BuildHistoryEvent");
            var events = (IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(buildEvent.ReturnType));
            events.Add(buildEvent.Invoke(null, new[] { resize }));
            events.Add(buildEvent.Invoke(null, new[] { output }));
            var uploadHistory = Method(runtimeType, "UploadHistoryAsync");
            var pendingOnlySession = Guid.Parse("40000000-0000-4000-8000-000000000006");
            var pendingOnlyResize = frameConstructor.Invoke(new[]
            {
                Enum.Parse(localFrameKindType, "Resize"),
                pendingOnlySession,
                1UL,
                (object)resizePayload
            });
            append.Invoke(producer, new[] { pendingOnlyResize });
            var pendingOnlyProduction = Method(
                producerType,
                "ProcessSession",
                publicOnly: true,
                parameterCount: 1).Invoke(producer, new object[] { pendingOnlySession });
            var pendingOnlyEvents = (IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(buildEvent.ReturnType));
            pendingOnlyEvents.Add(buildEvent.Invoke(null, new[] { pendingOnlyResize }));
            var operationCountBeforePending = handler.Operations.Count;
            var pendingOnlyDurable = await InvokeBooleanTask(
                uploadHistory,
                runtime,
                new object[]
                {
                    pendingOnlySession,
                    pendingOnlyEvents,
                    pendingOnlyProduction,
                    CancellationToken.None
                });
            Require(!pendingOnlyDurable &&
                !handler.Operations.Skip(operationCountBeforePending).Any(value =>
                    value == "CHUNK" || value == "SNAPSHOT" || value == "TRANSCRIPT"),
                "A pending first Snapshot allowed generic CHUNK publication: " +
                string.Join(", ", handler.Operations));

            var validationDeadline = Stopwatch.StartNew();
            var firstAttempt = InvokeBooleanTask(
                uploadHistory,
                runtime,
                new object[] { sessionId, events, production, CancellationToken.None });
            var snapshotEntry = handler.SnapshotCreateEntered.Task;
            var entryTimeout = Task.Delay(TimeSpan.FromSeconds(2));
            var entryWinner = await Task.WhenAny(firstAttempt, snapshotEntry, entryTimeout);
            if (entryWinner == firstAttempt)
            {
                var firstResult = await firstAttempt;
                throw new InvalidOperationException(
                    "The first history attempt completed before entering Snapshot creation " +
                    $"(durable={firstResult}, operations={string.Join(", ", handler.Operations)}).");
            }
            if (entryWinner == entryTimeout)
            {
                throw new TimeoutException(
                    "The first history attempt remained active but did not enter Snapshot creation " +
                    $"within 2 seconds (status={firstAttempt.Status}, " +
                    $"operations={string.Join(", ", handler.Operations)}).");
            }

            // Keep producing output while the first recipient Snapshot is in
            // flight, then force the independent REST publisher to observe the
            // much newer local tail.  The advertised server cursor must remain
            // within Android's one-window acceptance bound of Snapshot 2.
            for (ulong sequence = 3; sequence <= 402; sequence++)
            {
                var burst = frameConstructor.Invoke(new object[]
                {
                    Enum.Parse(localFrameKindType, "Output"),
                    sessionId,
                    sequence,
                    Encoding.UTF8.GetBytes($"burst-{sequence:D4}\r\n")
                });
                onCommitted.Invoke(runtime, new[] { shell, burst });
            }
            var publicationStates = (IDictionary)Field(runtimeType, "publicationStates").GetValue(runtime);
            var publicationState = publicationStates[sessionId]
                ?? throw new InvalidOperationException("Continuous-output publication state is unavailable.");
            var capturePublication = Method(runtimeType, "CapturePublicationSnapshot");
            var currentPublication = capturePublication.Invoke(
                null,
                new[] { (object)sessionId, publicationState });
            await InvokeTask(
                Method(runtimeType, "UpsertSessionAsync"),
                runtime,
                new object[] { publicationState, currentPublication, true, CancellationToken.None });
            Require(handler.ConfirmedLastOutputSequence >= 2 &&
                handler.ConfirmedLastOutputSequence - 2 <= 128,
                "Continuous output advanced the authoritative REST tail beyond the Snapshot acceptance window.");
            handler.AllowSnapshotCreate.TrySetResult();
            var firstAttemptDurable = await firstAttempt;

            Require(!firstAttemptDurable,
                "A failed recipient snapshot was incorrectly reported as fully durable.");
            var firstAttemptEnd = handler.Operations.Count;
            var metadataIndex = handler.Operations.FindIndex(value => value.StartsWith("METADATA:", StringComparison.Ordinal));
            var snapshotIndex = handler.Operations.IndexOf("SNAPSHOT");
            Require(metadataIndex >= 0 && snapshotIndex > metadataIndex &&
                handler.Operations.Take(firstAttemptEnd).Count(value => value == "SNAPSHOT") == 2 &&
                !handler.Operations.Take(firstAttemptEnd).Any(
                    value => value == "CHUNK" || value == "TRANSCRIPT"),
                "One failed recipient snapshot allowed bulk history before the global barrier: " +
                string.Join(", ", handler.Operations));
            Require(handler.ConfirmedLastOutputSequence >= 2,
                "Snapshot upload was not preceded by last_output_seq >= snapshot_seq metadata.");

            pendingHistory.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public)
                ?.Invoke(pendingHistory, null);
            var backgroundAttemptDurable = await InvokeBooleanTask(
                uploadHistory,
                runtime,
                new object[] { sessionId, events, production, CancellationToken.None });
            Require(!backgroundAttemptDurable,
                "A failed background history cursor was incorrectly reported as fully durable.");
            var lastSnapshotIndex = handler.Operations.FindLastIndex(value => value == "SNAPSHOT");
            var firstBulkIndex = handler.Operations.FindIndex(
                value => value == "CHUNK" || value == "TRANSCRIPT");
            Require(handler.Operations.Count(value => value == "SNAPSHOT") == 3 &&
                firstBulkIndex > lastSnapshotIndex,
                "Bulk history began before every recipient snapshot became durable: " +
                string.Join(", ", handler.Operations));

            pendingHistory.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public)
                ?.Invoke(pendingHistory, null);
            var retryDurable = await InvokeBooleanTask(
                uploadHistory,
                runtime,
                new object[] { sessionId, events, production, CancellationToken.None });
            Require(retryDurable && handler.Operations.Count(value => value == "SNAPSHOT") == 3 &&
                handler.Operations.Count(value => value == "CHUNK") == 2 &&
                !handler.Operations.Any(value => value == "TRANSCRIPT"),
                "Multi-recipient retry duplicated durable Snapshot/CHUNK work or published retired transcript artifacts: " +
                string.Join(", ", handler.Operations));
            Require(validationDeadline.Elapsed < TimeSpan.FromSeconds(10) &&
                handler.DurableSnapshotSequences.Count == 2 &&
                handler.DurableSnapshotSequences.Values.All(sequence =>
                    handler.ConfirmedLastOutputSequence >= sequence &&
                    handler.ConfirmedLastOutputSequence - sequence <= 128),
                "Continuous output did not produce a multi-recipient durable Snapshot inside the 10-second tail window.");

            Console.WriteLine("Production history ordering validation passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            // Never leave an in-flight validation upload parked if an assertion
            // fails before the normal release point.
            handler?.AllowSnapshotCreate.TrySetResult();
            if (runtime is IDisposable disposable) disposable.Dispose();
            remoteExchange?.Dispose();
            secondRemoteExchange?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Type Type(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true);

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing field: " + name);

    private static MethodInfo Method(
        Type type,
        string name,
        bool publicOnly = false,
        int? parameterCount = null)
    {
        var flags = BindingFlags.Instance | BindingFlags.Static |
            (publicOnly ? BindingFlags.Public : BindingFlags.NonPublic);
        return type.GetMethods(flags).SingleOrDefault(value =>
            value.Name == name && (!parameterCount.HasValue || value.GetParameters().Length == parameterCount.Value))
            ?? throw new InvalidOperationException("Missing method: " + name);
    }

    private static void Set(object target, string property, object value)
    {
        var member = target.GetType().GetProperty(
            property,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing property: " + property);
        member.SetValue(target, value);
    }

    private static async Task<bool> InvokeBooleanTask(
        MethodInfo method,
        object target,
        object[] arguments)
    {
        try
        {
            return await (Task<bool>)method.Invoke(target, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            throw error.InnerException;
        }
    }

    private static async Task InvokeTask(
        MethodInfo method,
        object target,
        object[] arguments)
    {
        try
        {
            await (Task)method.Invoke(target, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            throw error.InnerException;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
