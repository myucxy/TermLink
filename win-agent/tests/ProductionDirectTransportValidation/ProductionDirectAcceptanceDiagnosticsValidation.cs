#if TERMLINK_DIRECT_DATA_PLANE_ACCEPTANCE
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class ProductionDirectAcceptanceDiagnosticsValidation
    {
        internal static async Task ValidateAsync()
        {
            var pipeName = "termlink-direct-diagnostics-" + Guid.NewGuid().ToString("N");
            var sessionId = Guid.NewGuid();
            var peerDeviceId = Guid.NewGuid();
            Guid? capturedSession = null;
            Guid? capturedPeer = null;
            using var server = new ProductionDirectAcceptanceDiagnosticsServer(
                pipeName,
                (session, peer) =>
                {
                    capturedSession = session;
                    capturedPeer = peer;
                    return CompleteSnapshot();
                },
                TimeSpan.FromMilliseconds(150));
            Require(server.Start(), "The acceptance diagnostics pipe did not start.");

            using (var probe = await InvokeAsync(pipeName, Request("probe")))
            {
                Require(probe.RootElement.TryGetProperty("ok", out var probeOk) &&
                        probe.RootElement.TryGetProperty("status", out var probeStatus) &&
                        probeOk.GetBoolean() &&
                        probeStatus.GetString() == "ready" &&
                        !capturedSession.HasValue && !capturedPeer.HasValue,
                    "Probe selected a session/peer or did not report ready: " +
                    probe.RootElement.GetRawText());
            }

            using (var inspect = await InvokeAsync(
                pipeName,
                Request("inspect", sessionId, peerDeviceId)))
            {
                var responseText = inspect.RootElement.GetRawText();
                Require(inspect.RootElement.GetProperty("ok").GetBoolean() &&
                        inspect.RootElement.GetProperty("status").GetString() == "observed" &&
                        capturedSession == sessionId && capturedPeer == peerDeviceId &&
                        !responseText.Contains(sessionId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                        !responseText.Contains(peerDeviceId.ToString("D"), StringComparison.OrdinalIgnoreCase),
                    "Inspect did not use the exact selector or echoed a selector UUID.");
            }

            await RequireBlockedAsync(pipeName, Request("inspect", sessionId, null),
                "An inspect without peer_device_id was accepted.").ConfigureAwait(false);
            await RequireBlockedAsync(pipeName, Request("inspect", null, peerDeviceId),
                "An inspect without session_id was accepted.").ConfigureAwait(false);
            await RequireBlockedAsync(pipeName,
                Request("probe", sessionId, peerDeviceId),
                "Probe accepted an inspect-only selector.").ConfigureAwait(false);
            await RequireBlockedAsync(pipeName,
                Request("inspect", sessionId, peerDeviceId).Replace(
                    "\"session_role\":\"primary\"",
                    "\"session_role\":\"invalid\"",
                    StringComparison.Ordinal),
                "Inspect accepted an invalid session role.").ConfigureAwait(false);
            await RequireBlockedAsync(pipeName,
                Request("inspect", sessionId, peerDeviceId).Replace(
                    sessionId.ToString("D"),
                    "11111111-1111-1111-1111-111111111111",
                    StringComparison.Ordinal),
                "Inspect accepted a non-RFC4122 UUID variant.").ConfigureAwait(false);
            await RequireBlockedAsync(pipeName,
                Request("inspect", sessionId, peerDeviceId).Replace(
                    "\"operation\":\"inspect\"",
                    "\"operation\":\"inspect\",\"operation\":\"inspect\"",
                    StringComparison.Ordinal),
                "A duplicate request field was accepted.").ConfigureAwait(false);
            await RequireBlockedAsync(pipeName,
                Request("probe").TrimEnd('}') + ",\"extra\":true}",
                "An extra request field was accepted.").ConfigureAwait(false);

            var exactRequest = Request("probe");
            exactRequest += new string(' ',
                ProductionDirectAcceptanceDiagnosticsServer.MaximumRequestBytes -
                Encoding.UTF8.GetByteCount(exactRequest));
            using (var boundary = await InvokeAsync(pipeName, exactRequest))
                Require(boundary.RootElement.GetProperty("ok").GetBoolean(),
                    "The exact 64-KiB request boundary was rejected.");
            using (var oversized = await InvokeAsync(
                pipeName,
                string.Empty,
                ProductionDirectAcceptanceDiagnosticsServer.MaximumRequestBytes + 1))
                Require(!oversized.RootElement.GetProperty("ok").GetBoolean(),
                    "An oversized request length was accepted.");

            // A client that stalls before the length prefix receives a bounded
            // timeout response; it cannot monopolize the sole pipe instance.
            using (var stalled = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await stalled.ConnectAsync(2_000).ConfigureAwait(false);
                using var timeout = await ReadResponseAsync(stalled).ConfigureAwait(false);
                Require(!timeout.RootElement.GetProperty("ok").GetBoolean() &&
                        timeout.RootElement.GetProperty("reason_code").GetString() ==
                            "WINDOWS_DIAGNOSTICS_REQUEST_TIMEOUT",
                    "A stalled diagnostics client did not time out with a bounded reason.");
            }

            // A truncated payload may disconnect before receiving its blocked
            // response, but the server must remain healthy for the next client.
            using (var truncated = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await truncated.ConnectAsync(2_000).ConfigureAwait(false);
                var prefix = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, 32);
                await truncated.WriteAsync(prefix).ConfigureAwait(false);
                await truncated.WriteAsync(new byte[] { (byte)'{' }).ConfigureAwait(false);
            }
            using (var afterTruncation = await InvokeAsync(pipeName, Request("probe")))
                Require(afterTruncation.RootElement.GetProperty("ok").GetBoolean(),
                    "A truncated client terminated the diagnostics server.");

            var incompletePipe =
                "termlink-direct-diagnostics-" + Guid.NewGuid().ToString("N");
            using var incompleteServer = new ProductionDirectAcceptanceDiagnosticsServer(
                incompletePipe,
                (_, _) => new ProductionDirectAcceptanceDiagnosticsSnapshot
                {
                    AgentPid = 1,
                    AgentStartTime = DateTimeOffset.UtcNow.ToString("O"),
                    ProcessImageSha256 = new string('a', 64),
                    Carrier = "relay",
                    FallbackReason = "NONE"
                });
            Require(incompleteServer.Start(), "The incomplete-counter fixture did not start.");
            using (var incomplete = await InvokeAsync(
                incompletePipe,
                Request("inspect", sessionId, peerDeviceId)))
            {
                Require(!incomplete.RootElement.GetProperty("ok").GetBoolean() &&
                        incomplete.RootElement.GetProperty("reason_code").GetString() ==
                            "WINDOWS_DIAGNOSTICS_COUNTERS_UNAVAILABLE",
                    "Nullable ABI counters were converted into a successful observation.");
            }

            var oversizedResponsePipe =
                "termlink-direct-diagnostics-" + Guid.NewGuid().ToString("N");
            using var oversizedResponseServer =
                new ProductionDirectAcceptanceDiagnosticsServer(
                    oversizedResponsePipe,
                    (_, _) =>
                    {
                        var snapshot = CompleteSnapshot();
                        return new ProductionDirectAcceptanceDiagnosticsSnapshot
                        {
                            AgentPid = snapshot.AgentPid,
                            AgentStartTime = snapshot.AgentStartTime,
                            ProcessImageSha256 = snapshot.ProcessImageSha256,
                            Carrier = snapshot.Carrier,
                            ContiguousAppliedSequence = 0,
                            DigestConflictCount = 0,
                            DirectFrameReceivedCount = 0,
                            DirectFrameSentCount = 0,
                            DirectReceiptCount = 0,
                            DuplicateApplyCount = 0,
                            DuplicateDropCount = 0,
                            DurableAck = 0,
                            FallbackReason = new string('A',
                                ProductionDirectAcceptanceDiagnosticsServer.MaximumResponseBytes),
                            GapCount = 0,
                            LastReceivedSequence = 0,
                            OutboxDepth = 0,
                            PathEpoch = 0,
                            QueueDepth = 0,
                            RelayGeneration = 0,
                            RelayReplayBytes = 0,
                            RelayReplayCount = 0,
                            SessionHostPid = snapshot.SessionHostPid,
                            SessionHostStartTime = snapshot.SessionHostStartTime,
                            SessionHostImageSha256 = snapshot.SessionHostImageSha256,
                            SignallingGeneration = 0,
                            TransportReceiptHighWatermark = 0
                        };
                    });
            Require(oversizedResponseServer.Start(),
                "The oversized-response fixture did not start.");
            using (var oversizedResponse = await InvokeAsync(
                oversizedResponsePipe,
                Request("inspect", sessionId, peerDeviceId)))
                Require(!oversizedResponse.RootElement.GetProperty("ok").GetBoolean() &&
                        oversizedResponse.RootElement.GetProperty("reason_code").GetString() ==
                            "WINDOWS_DIAGNOSTICS_RESPONSE_TOO_LARGE",
                    "An oversized diagnostics response was emitted.");
        }

        private static ProductionDirectAcceptanceDiagnosticsSnapshot CompleteSnapshot() => new()
        {
            AgentPid = 1,
            AgentStartTime = DateTimeOffset.UtcNow.ToString("O"),
            ProcessImageSha256 = new string('a', 64),
            Carrier = "relay",
            ContiguousAppliedSequence = 0,
            DigestConflictCount = 0,
            DirectFrameReceivedCount = 0,
            DirectFrameSentCount = 0,
            DirectReceiptCount = 0,
            DuplicateApplyCount = 0,
            DuplicateDropCount = 0,
            DurableAck = 0,
            FallbackReason = "NONE",
            GapCount = 0,
            LastReceivedSequence = 0,
            OutboxDepth = 0,
            PathEpoch = 0,
            QueueDepth = 0,
            RelayGeneration = 0,
            RelayReplayBytes = 0,
            RelayReplayCount = 0,
            SessionHostPid = 2,
            SessionHostStartTime = DateTimeOffset.UtcNow.ToString("O"),
            SessionHostImageSha256 = new string('b', 64),
            SignallingGeneration = 0,
            TransportReceiptHighWatermark = 0
        };

        private static string Request(
            string operation,
            Guid? sessionId = null,
            Guid? peerDeviceId = null)
        {
            var builder = new StringBuilder();
            builder.Append("{\"schema_version\":1,")
                .Append("\"kind\":\"termlink.windows-direct-diagnostics-request\",")
                .Append("\"request_id\":\"").Append(Guid.NewGuid().ToString("N")).Append("\",")
                .Append("\"run_id\":\"validation\",")
                .Append("\"scenario_id\":\"pipe_contract\",")
                .Append("\"operation\":\"").Append(operation).Append('"');
            if (sessionId.HasValue)
                builder.Append(",\"session_id\":\"")
                    .Append(sessionId.Value.ToString("D")).Append('"')
                    .Append(",\"session_role\":\"primary\"");
            if (peerDeviceId.HasValue)
                builder.Append(",\"peer_device_id\":\"")
                    .Append(peerDeviceId.Value.ToString("D")).Append('"');
            return builder.Append('}').ToString();
        }

        private static async Task RequireBlockedAsync(
            string pipeName,
            string request,
            string message)
        {
            using var response = await InvokeAsync(pipeName, request).ConfigureAwait(false);
            Require(!response.RootElement.GetProperty("ok").GetBoolean(), message);
        }

        private static async Task<JsonDocument> InvokeAsync(
            string pipeName,
            string request,
            int? declaredLength = null)
        {
            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2_000).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(request);
            try
            {
                var prefix = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(
                    prefix, declaredLength ?? bytes.Length);
                await pipe.WriteAsync(prefix).ConfigureAwait(false);
                if (bytes.Length > 0) await pipe.WriteAsync(bytes).ConfigureAwait(false);
                await pipe.FlushAsync().ConfigureAwait(false);
                return await ReadResponseAsync(pipe).ConfigureAwait(false);
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        private static async Task<JsonDocument> ReadResponseAsync(Stream stream)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var prefix = await ReadExactAsync(stream, sizeof(int), timeout.Token)
                .ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            Require(length is >= 2 and <=
                    ProductionDirectAcceptanceDiagnosticsServer.MaximumResponseBytes,
                "The diagnostics response length was invalid.");
            var response = await ReadExactAsync(stream, length, timeout.Token)
                .ConfigureAwait(false);
            try { return JsonDocument.Parse(Encoding.UTF8.GetString(response)); }
            finally { Array.Clear(response, 0, response.Length); }
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
                var read = await stream.ReadAsync(
                    bytes.AsMemory(offset, count - offset), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return bytes;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
