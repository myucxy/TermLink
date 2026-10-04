using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal sealed partial class ProductionAgentRuntime
    {
        private readonly System.Collections.Generic.HashSet<Guid> mirroredDeletedSessions = new();
        private Task historyRetentionWorker;

        private bool DeleteInactiveSessionHistory(Guid id, Func<bool, bool> retention = null)
        {
            if (!registry.DeleteInactiveSession(id, () => terminalHistoryStore.MarkSessionDeleted(id), retention)) return false;
            destroyedHistorySessions.TryAdd(id, 0);
            terminalHistoryPersistenceFailures.TryRemove(id, out _);
            ApplyDestroyedSessionHistoryState(id);
            ScheduleSessionHistoryDeletion(id);
            return true;
        }

        // Local retention runs even while Relay/HTTP are disconnected. Tombstones
        // feed the existing metadata retry after connectivity returns.
        private async Task HistoryRetentionLoopAsync()
        {
            var token = cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    PruneExpiredSessionHistory(DateTimeOffset.UtcNow, token);
                    foreach (var id in terminalHistoryStore.DeletedSessionIds())
                    {
                        token.ThrowIfCancellationRequested();
                        if (Directory.Exists(Path.Combine(terminalHistoryStore.RootPath, id.ToString("N"))) || registry.HasSessionBuffer(id))
                            ScheduleSessionHistoryDeletion(id);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException) { }
                try { await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }

        private void PruneExpiredSessionHistory(DateTimeOffset now, CancellationToken token)
        {
            var ids = terminalHistoryStore.ListSessions()
                .Concat(registry.GetCatalogSummaries().Select(item => item.SessionId)).Distinct().ToArray();
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                if (terminalHistoryStore.IsSessionDeleted(id)) continue;
                try
                {
                    DeleteInactiveSessionHistory(id, alive => terminalHistoryStore.IsInactiveRetentionExpired(id, alive, now));
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                    error is System.Security.SecurityException || error is System.Security.Cryptography.CryptographicException)
                {
                    // Failed metadata reads/writes never authorize deletion; retry next pass.
                }
            }
        }

        private async Task SyncSessionDeletionsAsync(Guid[] deleted, CancellationToken token)
        {
            if (deleted.Length == 0) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            token = timeout.Token;
            await metadataSnapshotGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(new { session_ids = deleted });
                using var reply = await SendAsync(HttpMethod.Post, "/v1/pcs/" + config.PcId.ToString("D") + "/session-deletions",
                    body, true, "session-deletions-" + Guid.NewGuid().ToString("N"), token).ConfigureAwait(false);
                await EnsureServerSuccessAsync(reply, "SESSION_DELETIONS", token).ConfigureAwait(false);
                lock (mirroredDeletedSessions) mirroredDeletedSessions.UnionWith(deleted);
            }
            finally { metadataSnapshotGate.Release(); }
        }

        private async Task RetrySessionDeletionsAsync(CancellationToken token)
        {
            Guid[] pending;
            lock (mirroredDeletedSessions)
                pending = terminalHistoryStore.DeletedSessionIds().Where(id => !mirroredDeletedSessions.Contains(id)).Take(64).ToArray();
            await SyncSessionDeletionsAsync(pending, token).ConfigureAwait(false);
        }

        private async Task<SessionCleanupResponsePayload> CleanupSessionsAsync(SessionCleanupRequestPayload request, CancellationToken token)
        {
            var ids = request.SessionIds.Select(id => GuidFromBytes(id.Span)).ToArray();
            if (GuidFromBytes(request.RequestId.Span) == Guid.Empty || ids.Length < 1 || ids.Length > 64 ||
                ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
                throw new InvalidDataException("Invalid cleanup batch.");
            var response = new SessionCleanupResponsePayload { RequestId = request.RequestId };
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                var result = new SessionCleanupResult { SessionId = ByteString.CopyFrom(GuidBytes(id)), Status = "FAILED" };
                try
                {
                    if (!terminalHistoryStore.IsSessionDeleted(id) && !terminalHistoryStore.ContainsSession(id) && !registry.ContainsSessionRecord(id))
                        result.Status = "NOT_FOUND";
                    else if (DeleteInactiveSessionHistory(id)) result.Status = "DELETED";
                    else result.Status = "SKIPPED_ACTIVE";
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException ||
                    error is System.Security.SecurityException || error is System.Security.Cryptography.CryptographicException) { }
                response.Results.Add(result);
            }
            var deleted = response.Results.Where(item => item.Status == "DELETED").Select(item => GuidFromBytes(item.SessionId.Span)).ToArray();
            if (deleted.Length > 0)
            {
                // One metadata transaction per batch, after Windows has durably fenced history.
                try
                {
                    await SyncSessionDeletionsAsync(deleted, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException || error is IOException || error is OperationCanceledException)
                {
                    // Host tombstones are durable; retrying these UUIDs is safe and finishes the mirror.
                    response.ErrorCode = "MIRROR_SYNC_PENDING";
                }
            }
            return response;
        }
    }
}
