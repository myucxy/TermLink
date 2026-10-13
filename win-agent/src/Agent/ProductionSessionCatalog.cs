using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal sealed partial class ProductionAgentRuntime
    {
        private readonly Guid catalogHostInstance = Guid.NewGuid();
        private readonly object catalogSync = new();
        private readonly object catalogObservationSync = new();
        private long catalogObservedAtMs;
        private sealed class CatalogMetadata
        {
            public readonly SessionCatalogEntry Entry = new()
            {
                ShellType = "custom", Lifecycle = "INTERRUPTED", Columns = 80, Rows = 24
            };
            public ulong TitleSequence, SizeSequence, LifecycleSequence;
        }
        private readonly Dictionary<Guid, CatalogMetadata> catalogEntries = new();

        // Live durable events and the existing background history recovery feed
        // this small projection. A directory request never opens/decrypts WAL.
        private void ObserveCatalogFrame(LocalFrame frame)
        {
            if (frame.Type != LocalFrameType.Title && frame.Type != LocalFrameType.Resize &&
                frame.Type != LocalFrameType.Lifecycle && frame.Type != LocalFrameType.Output &&
                frame.Type != LocalFrameType.BrokerOutput) return;
            lock (catalogSync)
            {
                if (!catalogEntries.TryGetValue(frame.SessionId, out var metadata))
                    catalogEntries.Add(frame.SessionId, metadata = new CatalogMetadata());
                var entry = metadata.Entry;
                // Recovery may visit old metadata after a newer live output.
                // Fence each field independently so neither can erase the other.
                if (frame.Type == LocalFrameType.Title && frame.Sequence > metadata.TitleSequence)
                {
                    entry.Title = LocalPayloadCodec.ParseTitle(frame.Payload);
                    metadata.TitleSequence = frame.Sequence;
                }
                if (frame.Type == LocalFrameType.Resize && frame.Sequence > metadata.SizeSequence)
                {
                    LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                    entry.Columns = (uint)columns; entry.Rows = (uint)rows;
                    metadata.SizeSequence = frame.Sequence;
                }
                if (frame.Type == LocalFrameType.Lifecycle && frame.Sequence > metadata.LifecycleSequence)
                {
                    entry.Lifecycle = LifecycleText(LocalPayloadCodec.ParseLifecycle(frame.Payload, out _));
                    metadata.LifecycleSequence = frame.Sequence;
                }
                if (frame.Type is LocalFrameType.Output or LocalFrameType.BrokerOutput)
                    entry.LastOutputSeq = Math.Max(entry.LastOutputSeq, frame.Sequence);
            }
        }
        // ponytail: serialize metadata snapshots with per-session REST writes; split only if metadata throughput demands it.
        private readonly SemaphoreSlim metadataSnapshotGate = new(1, 1);

        private async Task PublishSessionSnapshotAsync(CancellationToken token)
        {
            if (registry == null) return;
            // Persisted host tombstones also recover a lost batch response or failed mirror update.
            try { await RetrySessionDeletionsAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (error is HttpRequestException || error is IOException ||
                (error is OperationCanceledException && !token.IsCancellationRequested)) { }
            await metadataSnapshotGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var catalog = BuildSessionCatalog(ByteString.CopyFrom(GuidBytes(Guid.NewGuid())));
                if (catalog.ErrorCode.Length != 0) throw new IOException("Session snapshot exceeds its bound.");
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    sessions = catalog.Sessions.Select(item => new
                    {
                        id = GuidFromBytes(item.SessionId.Span), shell_type = item.ShellType,
                        state = item.Lifecycle, columns = item.Columns, rows = item.Rows,
                        last_output_seq = item.LastOutputSeq
                    }).ToArray()
                });
                try
                {
                    // Each observation must reach storage even if an idle terminal has not changed.
                    using var response = await SendAsync(HttpMethod.Put,
                        "/v1/pcs/" + config.PcId.ToString("D") + "/session-snapshot", body, true,
                        "session-snapshot-" + Guid.NewGuid().ToString("N"), token).ConfigureAwait(false);
                    await EnsureServerSuccessAsync(response, "SESSION_SNAPSHOT", token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(body); }
            }
            finally { metadataSnapshotGate.Release(); }
        }

        private SessionCatalogResponsePayload BuildSessionCatalog(ByteString requestId)
        {
            // REST snapshots and encrypted requests share the same observation order.
            // Keep this separate from catalogSync to preserve the registry/history lock order.
            lock (catalogObservationSync) return BuildSessionCatalogObservation(requestId);
        }

        private SessionCatalogResponsePayload BuildSessionCatalogObservation(ByteString requestId)
        {
            // No history/registry lock may be acquired while holding catalogSync:
            // background recovery publishes metadata in the opposite order.
            var summaries = registry.GetCatalogSummaries().ToDictionary(item => item.SessionId);
            var retained = terminalHistoryStore.ListSessions().ToHashSet();
            retained.UnionWith(summaries.Keys);
            retained.RemoveWhere(id => destroyedHistorySessions.ContainsKey(id) || terminalHistoryStore.IsSessionDeleted(id));
            lock (catalogSync)
            {
                var response = new SessionCatalogResponsePayload
                {
                    RequestId = requestId,
                    HostInstanceId = ByteString.CopyFrom(GuidBytes(catalogHostInstance)),
                    ObservedAtMs = catalogObservedAtMs = Math.Max(
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), checked(catalogObservedAtMs + 1))
                };
                foreach (var old in catalogEntries.Keys.Where(id => !retained.Contains(id)).ToArray())
                {
                    catalogEntries.Remove(old);
                }
                foreach (var sessionId in retained.OrderBy(id => id))
                {
                    if (destroyedHistorySessions.ContainsKey(sessionId) || terminalHistoryStore.IsSessionDeleted(sessionId)) continue;
                    summaries.TryGetValue(sessionId, out var local);
                    if (!catalogEntries.TryGetValue(sessionId, out var metadata))
                        catalogEntries.Add(sessionId, metadata = new CatalogMetadata());
                    var entry = metadata.Entry;
                    var result = entry.Clone();
                    result.SessionId = ByteString.CopyFrom(GuidBytes(sessionId));
                    result.ShellType = local == null ? "custom" : ShellTypeText(local.ShellType);
                    result.CreatedAtMs = local == null ? 0 : new DateTimeOffset(local.RegisteredAtUtc).ToUnixTimeMilliseconds();
                    result.ProcessAlive = local != null && local.Lifecycle == SessionAgentLifecycle.Connected && entry.Lifecycle != "EXITED";
                    result.Lifecycle = CatalogLifecycle(result.ProcessAlive, entry.Lifecycle);
                    response.Sessions.Add(result);
                }
                if (response.Sessions.Count > 4096 || response.CalculateSize() > 900 * 1024)
                {
                    response.Sessions.Clear(); response.ErrorCode = "SESSION_CATALOG_TOO_LARGE";
                }
                return response;
            }
        }

        internal static string CatalogLifecycle(bool processAlive, string lastLifecycle) =>
            processAlive ? "RUNNING" : lastLifecycle == "EXITED" ? "EXITED" : "INTERRUPTED";
    }
}
