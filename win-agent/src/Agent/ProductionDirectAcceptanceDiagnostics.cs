#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    /// <summary>
    /// Redacted, read-only product evidence. Nullable values mean the
    /// production owner cannot measure that field. Inspect fails closed rather
    /// than converting an unknown value to zero.
    /// </summary>
    internal sealed class ProductionDirectAcceptanceDiagnosticsSnapshot
    {
        internal string BlockedReason { get; init; }
        internal int AgentPid { get; init; }
        internal string AgentStartTime { get; init; }
        internal string ProcessImageSha256 { get; init; }
        internal string Carrier { get; init; }
        internal ulong? ContiguousAppliedSequence { get; init; }
        internal ulong? DigestConflictCount { get; init; }
        internal ulong? DirectFrameReceivedCount { get; init; }
        internal ulong? DirectFrameSentCount { get; init; }
        internal ulong? DirectReceiptCount { get; init; }
        internal ulong? DuplicateApplyCount { get; init; }
        internal ulong? DuplicateDropCount { get; init; }
        internal ulong? DurableAck { get; init; }
        internal string FallbackReason { get; init; }
        internal ulong? GapCount { get; init; }
        internal ulong? LastReceivedSequence { get; init; }
        internal long? OutboxDepth { get; init; }
        internal ulong? PathEpoch { get; init; }
        internal long? QueueDepth { get; init; }
        internal long? RelayGeneration { get; init; }
        internal ulong? RelayReplayBytes { get; init; }
        internal ulong? RelayReplayCount { get; init; }
        internal int? SessionHostPid { get; init; }
        internal string SessionHostStartTime { get; init; }
        internal string SessionHostImageSha256 { get; init; }
        internal long? SignallingGeneration { get; init; }
        internal ulong? TransportReceiptHighWatermark { get; init; }

        internal bool HasCompleteObservation =>
            AgentPid > 0 && SessionHostPid > 0 &&
            IsTimestamp(AgentStartTime) && IsTimestamp(SessionHostStartTime) &&
            IsSha256(ProcessImageSha256) && IsSha256(SessionHostImageSha256) &&
            Carrier is "relay" or "negotiating" or "lan_direct" or "nat_direct" &&
            ContiguousAppliedSequence.HasValue && DigestConflictCount.HasValue &&
            DirectFrameReceivedCount.HasValue && DirectFrameSentCount.HasValue &&
            DirectReceiptCount.HasValue && DuplicateApplyCount.HasValue &&
            DuplicateDropCount.HasValue && DurableAck.HasValue &&
            FallbackReason != null && GapCount.HasValue && LastReceivedSequence.HasValue &&
            OutboxDepth >= 0 && PathEpoch.HasValue && QueueDepth >= 0 &&
            RelayGeneration >= 0 && RelayReplayBytes.HasValue && RelayReplayCount.HasValue &&
            SignallingGeneration >= 0 && TransportReceiptHighWatermark.HasValue;

        private static bool IsSha256(string value) =>
            value?.Length == 64 && value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static bool IsTimestamp(string value) =>
            DateTimeOffset.TryParse(value, out _);

        internal object ToWireValue() => new
        {
            agent_pid = AgentPid,
            agent_start_time = AgentStartTime,
            carrier = Carrier,
            contiguous_applied_sequence = ContiguousAppliedSequence,
            digest_conflict_count = DigestConflictCount,
            direct_frame_received_count = DirectFrameReceivedCount,
            direct_frame_sent_count = DirectFrameSentCount,
            direct_receipt_count = DirectReceiptCount,
            duplicate_apply_count = DuplicateApplyCount,
            duplicate_drop_count = DuplicateDropCount,
            durable_ack = DurableAck,
            fallback_reason = FallbackReason,
            gap_count = GapCount,
            last_received_sequence = LastReceivedSequence,
            outbox_depth = OutboxDepth,
            path_epoch = PathEpoch,
            process_image_sha256 = ProcessImageSha256,
            queue_depth = QueueDepth,
            relay_generation = RelayGeneration,
            relay_replay_bytes = RelayReplayBytes,
            relay_replay_count = RelayReplayCount,
            session_host_pid = SessionHostPid,
            session_host_start_time = SessionHostStartTime,
            session_host_image_sha256 = SessionHostImageSha256,
            signalling_generation = SignallingGeneration,
            transport_receipt_high_watermark = TransportReceiptHighWatermark
        };
    }

    /// <summary>
    /// Acceptance-build-only current-user pipe. It accepts exactly one bounded
    /// request per connection and exposes no mutation operation.
    /// </summary>
    internal sealed class ProductionDirectAcceptanceDiagnosticsServer : IDisposable
    {
        internal const string PipeEnvironment = "TERMLINK_DIRECT_E2E_DIAGNOSTICS_PIPE";
        internal const int MaximumRequestBytes = 64 * 1024;
        internal const int MaximumResponseBytes = 1024 * 1024;
        internal static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(5);
        private const string RequestKind = "termlink.windows-direct-diagnostics-request";
        private static readonly Regex PipeNamePattern = new(
            "^[A-Za-z0-9._-]{1,180}$", RegexOptions.CultureInvariant);
        private static readonly Regex RequestIdPattern = new(
            "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex RunIdPattern = new(
            "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex ScenarioIdPattern = new(
            "^[a-z0-9_]{1,96}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> BaseRequestProperties = new(StringComparer.Ordinal)
        {
            "schema_version", "kind", "request_id", "run_id", "scenario_id", "operation"
        };
        private static readonly HashSet<string> InspectRequestProperties = new(
            BaseRequestProperties.Concat(new[]
            {
                "session_id", "session_role", "peer_device_id"
            }),
            StringComparer.Ordinal);
        private readonly string pipeName;
        private readonly Func<Guid?, Guid?, ProductionDirectAcceptanceDiagnosticsSnapshot> capture;
        private readonly TimeSpan clientTimeout;
        private readonly CancellationTokenSource lifetime = new();
        private readonly object sync = new();
        private Task worker;
        private NamedPipeServerStream waitingServer;
        private int started;
        private int disposed;

        internal ProductionDirectAcceptanceDiagnosticsServer(
            string pipeName,
            Func<Guid?, Guid?, ProductionDirectAcceptanceDiagnosticsSnapshot> capture,
            TimeSpan? clientTimeout = null)
        {
            if (!IsValidPipeName(pipeName))
                throw new ArgumentException("The acceptance diagnostics pipe name is invalid.", nameof(pipeName));
            this.pipeName = pipeName;
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
            this.clientTimeout = clientTimeout ?? ClientTimeout;
            if (this.clientTimeout < TimeSpan.FromMilliseconds(100) ||
                this.clientTimeout > TimeSpan.FromSeconds(30))
                throw new ArgumentOutOfRangeException(nameof(clientTimeout));
        }

        internal static ProductionDirectAcceptanceDiagnosticsServer TryCreateFromEnvironment(
            Func<Guid?, Guid?, ProductionDirectAcceptanceDiagnosticsSnapshot> capture)
        {
            var value = Environment.GetEnvironmentVariable(PipeEnvironment)?.Trim();
            return IsValidPipeName(value)
                ? new ProductionDirectAcceptanceDiagnosticsServer(value, capture)
                : null;
        }

        internal bool Start()
        {
            if (Volatile.Read(ref disposed) != 0 ||
                Interlocked.CompareExchange(ref started, 1, 0) != 0) return false;
            worker = RunAsync(lifetime.Token);
            return true;
        }

        internal void Stop()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            lifetime.Cancel();
            DisposeWaitingServer();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            DisposeWaitingServer();
            try { worker?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            lifetime.Dispose();
        }

        private void DisposeWaitingServer()
        {
            lock (sync)
            {
                try { waitingServer?.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                        MaximumRequestBytes + sizeof(int),
                        MaximumResponseBytes + sizeof(int));
                    lock (sync) waitingServer = server;
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    await ProcessClientAsync(server, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
                catch (IOException) when (!token.IsCancellationRequested) { }
                finally
                {
                    lock (sync)
                    {
                        if (ReferenceEquals(waitingServer, server)) waitingServer = null;
                    }
                    server?.Dispose();
                }
            }
        }

        private async Task ProcessClientAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            var requestId = "unknown";
            object response;
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(clientTimeout);
            try
            {
                var lengthBytes = await ReadExactAsync(
                    pipe, sizeof(int), requestTimeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (length is < 2 or > MaximumRequestBytes)
                    throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_LENGTH_INVALID");
                var requestBytes = await ReadExactAsync(
                    pipe, length, requestTimeout.Token).ConfigureAwait(false);
                try
                {
                    var request = ParseRequest(requestBytes);
                    requestId = request.RequestId;
                    var snapshot = capture(request.SessionId, request.PeerDeviceId);
                    if (snapshot == null)
                        throw new DiagnosticsBlockedException("WINDOWS_DIAGNOSTICS_SNAPSHOT_UNAVAILABLE");
                    if (!string.IsNullOrWhiteSpace(snapshot.BlockedReason))
                        throw new DiagnosticsBlockedException(snapshot.BlockedReason);
                    if (request.Operation == "inspect" && !snapshot.HasCompleteObservation)
                        throw new DiagnosticsBlockedException("WINDOWS_DIAGNOSTICS_COUNTERS_UNAVAILABLE");
                    response = SuccessResponse(
                        requestId,
                        request.Operation == "probe" ? "ready" : "observed",
                        snapshot);
                }
                finally { CryptographicOperations.ZeroMemory(requestBytes); }
            }
            catch (DiagnosticsBlockedException error)
            {
                response = BlockedResponse(requestId, error.Message);
            }
            catch (InvalidDataException error)
            {
                response = BlockedResponse(requestId, NormalizeReason(error.Message));
            }
            catch (JsonException)
            {
                response = BlockedResponse(requestId, "WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
            }
            catch (EndOfStreamException)
            {
                response = BlockedResponse(requestId, "WINDOWS_DIAGNOSTICS_PIPE_EOF");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                response = BlockedResponse(requestId, "WINDOWS_DIAGNOSTICS_REQUEST_TIMEOUT");
            }
            catch (Exception error) when (error is not OutOfMemoryException &&
                error is not StackOverflowException && error is not AccessViolationException)
            {
                response = BlockedResponse(requestId, "WINDOWS_DIAGNOSTICS_FAILED");
            }
            if (token.IsCancellationRequested) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
            try
            {
                if (bytes.Length > MaximumResponseBytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    bytes = JsonSerializer.SerializeToUtf8Bytes(
                        BlockedResponse(requestId, "WINDOWS_DIAGNOSTICS_RESPONSE_TOO_LARGE"));
                }
                var prefix = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
                try
                {
                    using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    responseTimeout.CancelAfter(clientTimeout);
                    await pipe.WriteAsync(prefix, responseTimeout.Token).ConfigureAwait(false);
                    await pipe.WriteAsync(bytes, responseTimeout.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(responseTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (IOException) when (!token.IsCancellationRequested) { }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private static DiagnosticsRequest ParseRequest(byte[] bytes)
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!observed.Add(property.Name))
                    throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
            }
            if (!root.TryGetProperty("schema_version", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != 1)
                throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
            var kind = RequiredString(root, "kind");
            var requestId = RequiredString(root, "request_id");
            var runId = RequiredString(root, "run_id");
            var scenarioId = RequiredString(root, "scenario_id");
            var operation = RequiredString(root, "operation");
            if (!string.Equals(kind, RequestKind, StringComparison.Ordinal) ||
                !RequestIdPattern.IsMatch(requestId) || !RunIdPattern.IsMatch(runId) ||
                !ScenarioIdPattern.IsMatch(scenarioId) || operation is not ("probe" or "inspect"))
                throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
            if (operation == "probe")
            {
                if (!observed.SetEquals(BaseRequestProperties))
                    throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
                return new DiagnosticsRequest(requestId, operation, null, null);
            }
            if (!observed.SetEquals(InspectRequestProperties))
                throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
            var sessionText = RequiredString(root, "session_id");
            var sessionRole = RequiredString(root, "session_role");
            var peerDeviceText = RequiredString(root, "peer_device_id");
            if (!TryParseRfc4122SessionId(sessionText, out var sessionId) ||
                sessionRole is not ("primary" or "late") ||
                !TryParseRfc4122SessionId(peerDeviceText, out var peerDeviceId))
                throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
            return new DiagnosticsRequest(
                requestId, operation, sessionId, peerDeviceId);
        }

        private static bool TryParseRfc4122SessionId(string value, out Guid sessionId)
        {
            sessionId = Guid.Empty;
            if (value == null || value.Length != 36 ||
                !Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
                return false;
            var version = char.ToLowerInvariant(value[14]);
            var variant = char.ToLowerInvariant(value[19]);
            if (version is < '1' or > '5' || variant is not ('8' or '9' or 'a' or 'b'))
                return false;
            sessionId = parsed;
            return true;
        }

        private static string RequiredString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var property) ||
                property.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("WINDOWS_DIAGNOSTICS_REQUEST_INVALID");
            return property.GetString();
        }

        private static async Task<byte[]> ReadExactAsync(
            Stream stream,
            int count,
            CancellationToken token)
        {
            var bytes = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset, count - offset), token)
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("WINDOWS_DIAGNOSTICS_PIPE_EOF");
                offset += read;
            }
            return bytes;
        }

        private static object SuccessResponse(
            string requestId,
            string status,
            ProductionDirectAcceptanceDiagnosticsSnapshot snapshot) => new
            {
                schema_version = 1,
                request_id = requestId,
                ok = true,
                status,
                diagnostics = snapshot.ToWireValue()
            };

        private static object BlockedResponse(string requestId, string reason) => new
        {
            schema_version = 1,
            request_id = requestId,
            ok = false,
            status = "blocked",
            reason_code = NormalizeReason(reason)
        };

        private static string NormalizeReason(string value) =>
            value != null && Regex.IsMatch(value, "^[A-Z0-9_]{1,96}$", RegexOptions.CultureInvariant)
                ? value
                : "WINDOWS_DIAGNOSTICS_FAILED";

        private static bool IsValidPipeName(string value) =>
            value != null && PipeNamePattern.IsMatch(value);

        private sealed class DiagnosticsRequest
        {
            internal DiagnosticsRequest(
                string requestId,
                string operation,
                Guid? sessionId,
                Guid? peerDeviceId)
            {
                RequestId = requestId;
                Operation = operation;
                SessionId = sessionId;
                PeerDeviceId = peerDeviceId;
            }
            internal string RequestId { get; }
            internal string Operation { get; }
            internal Guid? SessionId { get; }
            internal Guid? PeerDeviceId { get; }
        }

        private sealed class DiagnosticsBlockedException : Exception
        {
            internal DiagnosticsBlockedException(string reason) : base(reason) { }
        }
    }
}
#endif
