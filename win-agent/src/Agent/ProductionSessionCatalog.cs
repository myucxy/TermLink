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
        private readonly Dictionary<Guid, SessionCatalogEntry> catalogEntries = new();
        private readonly Dictionary<Guid, ulong> catalogCursors = new();
        // ponytail: serialize metadata snapshots with per-session REST writes; split only if metadata throughput demands it.
        private readonly SemaphoreSlim metadataSnapshotGate = new(1, 1);

        private async Task PublishSessionSnapshotAsync(CancellationToken token)
        {
            if (registry == null) return;
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
            lock (catalogSync)
            {
                var response = new SessionCatalogResponsePayload
                {
                    RequestId = requestId,
                    HostInstanceId = ByteString.CopyFrom(GuidBytes(catalogHostInstance)),
                    ObservedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var summaries = registry.GetCatalogSummaries().ToDictionary(item => item.SessionId);
                var retained = terminalHistoryStore.ListSessions().ToHashSet();
                retained.UnionWith(summaries.Keys);
                foreach (var old in catalogEntries.Keys.Where(id => !retained.Contains(id)).ToArray())
                {
                    catalogEntries.Remove(old); catalogCursors.Remove(old);
                }
                foreach (var sessionId in retained.OrderBy(id => id))
                {
                    if (destroyedHistorySessions.ContainsKey(sessionId) || terminalHistoryStore.IsSessionDeleted(sessionId)) continue;
                    summaries.TryGetValue(sessionId, out var local);
                    if (!catalogEntries.TryGetValue(sessionId, out var entry))
                    {
                        entry = new SessionCatalogEntry
                        {
                            SessionId = ByteString.CopyFrom(GuidBytes(sessionId)),
                            ShellType = local == null ? "custom" : ShellTypeText(local.ShellType),
                            Lifecycle = "INTERRUPTED", Columns = 80, Rows = 24,
                            CreatedAtMs = local == null ? 0 : new DateTimeOffset(local.RegisteredAtUtc).ToUnixTimeMilliseconds()
                        };
                        catalogEntries.Add(sessionId, entry);
                    }
                    catalogCursors.TryGetValue(sessionId, out var cursor);
                    if (local != null) entry.ShellType = ShellTypeText(local.ShellType);
                    var frames = terminalHistoryStore.ReadFramesAfter(sessionId, cursor);
                    try
                    {
                        foreach (var frame in frames)
                        {
                            if (frame.Type == LocalFrameType.Title) entry.Title = LocalPayloadCodec.ParseTitle(frame.Payload);
                            if (frame.Type == LocalFrameType.Resize)
                            {
                                LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                                entry.Columns = (uint)columns; entry.Rows = (uint)rows;
                            }
                            if (frame.Type == LocalFrameType.Lifecycle)
                                entry.Lifecycle = LifecycleText(LocalPayloadCodec.ParseLifecycle(frame.Payload, out _));
                            if (frame.Type == LocalFrameType.Output) entry.LastOutputSeq = frame.Sequence;
                            cursor = Math.Max(cursor, frame.Sequence);
                        }
                        catalogCursors[sessionId] = cursor;
                    }
                    finally
                    {
                        foreach (var frame in frames) if (frame.Payload != null) CryptographicOperations.ZeroMemory(frame.Payload);
                    }
                    var result = entry.Clone();
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
