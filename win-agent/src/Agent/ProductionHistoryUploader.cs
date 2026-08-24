using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal sealed class ProductionHistoryRecipient
    {
        public Guid DeviceId;
        public byte[] PairwiseKey;
        public long KeyEpoch;
    }

    internal enum ProductionHistoryUploadOutcome
    {
        Durable,
        Unavailable
    }

    internal sealed class ProductionHistoryUploader : IDisposable
    {
        private const int MaximumTranscriptPublicationPageCount = 100_000;
        private readonly HttpClient http;
        private readonly Func<HttpMethod, string, byte[], bool, string, CancellationToken, Task<HttpResponseMessage>> send;
        private readonly Guid pcDeviceId;
        private readonly ProductionTerminalHistoryStore durableStore;
        private readonly bool terminalSnapshotEnabled;
        private readonly ConcurrentDictionary<string, ulong> cursors = new ConcurrentDictionary<string, ulong>();
        private readonly ConcurrentDictionary<string, byte> initialized = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> uploadGates = new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly ConcurrentDictionary<string, byte[]> chunkSealedCache = new ConcurrentDictionary<string, byte[]>();
        private readonly object operationSync = new object();
        private int activeOperations;
        private int disposed;

        public ProductionHistoryUploader(
            HttpClient http,
            Func<HttpMethod, string, byte[], bool, string, CancellationToken, Task<HttpResponseMessage>> send,
            Guid pcDeviceId)
            : this(http, send, pcDeviceId, null, false)
        {
        }

        public ProductionHistoryUploader(
            HttpClient http,
            Func<HttpMethod, string, byte[], bool, string, CancellationToken, Task<HttpResponseMessage>> send,
            Guid pcDeviceId,
            ProductionTerminalHistoryStore durableStore)
            : this(http, send, pcDeviceId, durableStore, false)
        {
        }

        public ProductionHistoryUploader(
            HttpClient http,
            Func<HttpMethod, string, byte[], bool, string, CancellationToken, Task<HttpResponseMessage>> send,
            Guid pcDeviceId,
            ProductionTerminalHistoryStore durableStore,
            bool terminalSnapshotEnabled)
        {
            this.http = http ?? throw new ArgumentNullException(nameof(http));
            this.send = send ?? throw new ArgumentNullException(nameof(send));
            this.pcDeviceId = pcDeviceId;
            this.durableStore = durableStore;
            this.terminalSnapshotEnabled = terminalSnapshotEnabled;
        }

        public async Task<ProductionHistoryUploadOutcome> UploadAsync(
            Guid sessionId,
            ProductionHistoryRecipient recipient,
            IList<HistoryEventV1> events,
            CancellationToken token,
            Func<bool> shouldYield = null)
        {
            if (recipient == null || recipient.PairwiseKey == null || recipient.KeyEpoch < 1 || events == null || events.Count == 0 ||
                !TryBeginOperation())
                return ProductionHistoryUploadOutcome.Unavailable;
            try
            {
                var keyId = HistoryCrypto.KeyId(recipient.DeviceId, recipient.KeyEpoch);
                var cursorKey = sessionId.ToString("N") + ":" + keyId;
                var uploadGate = uploadGates.GetOrAdd(cursorKey, _ => new SemaphoreSlim(1, 1));
                await uploadGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!await InitializeCursorAsync(sessionId, keyId, cursorKey, token).ConfigureAwait(false))
                        return ProductionHistoryUploadOutcome.Unavailable;
                    cursors.TryGetValue(cursorKey, out var cursor);
                    var remaining = events.Where(value => value.Seq > cursor).OrderBy(value => value.Seq).ToList();
                    if (remaining.Count == 0) return ProductionHistoryUploadOutcome.Durable;

                    foreach (var batch in ContiguousBatches(remaining))
                    {
                        // A newer terminal checkpoint is more valuable than
                        // finishing an older CHUNK backlog. Yield only between
                        // durable batches so the caller can publish the fresh
                        // Snapshot first without abandoning partial progress.
                        if (shouldYield != null && shouldYield())
                            return ProductionHistoryUploadOutcome.Unavailable;
                        byte[] sealedObject = null;
                        byte[] digest = null;
                        byte[] plaintextDigest = null;
                        byte[] createBody = null;
                        try
                        {
                            plaintextDigest = ChunkPlaintextDigest(batch);
                            sealedObject = GetOrCreateSealedChunk(sessionId, recipient, batch, plaintextDigest);
                            digest = SHA256.HashData(sealedObject);
                            createBody = JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                kind = "CHUNK",
                                key_id = keyId,
                                ciphertext_sha256 = Base64Url(digest),
                                size_bytes = sealedObject.LongLength,
                                start_seq = batch[0].Seq,
                                end_seq = batch[^1].Seq
                            });
                            var createIdempotencyKey = CreateIdempotencyKey("chunk", Guid.NewGuid());
                            using var create = await send(
                                HttpMethod.Post,
                                "/v1/sessions/" + sessionId.ToString("D") + "/history/uploads",
                                createBody,
                                true,
                                createIdempotencyKey,
                                token).ConfigureAwait(false);
                            if (create.StatusCode == HttpStatusCode.ServiceUnavailable || create.StatusCode == HttpStatusCode.Forbidden)
                                return ProductionHistoryUploadOutcome.Unavailable;
                            create.EnsureSuccessStatusCode();
                            using var document = JsonDocument.Parse(await create.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                            var uploadId = document.RootElement.GetProperty("upload_id").GetGuid();
                            var upload = document.RootElement.GetProperty("upload");
                            await PutCiphertextAsync(upload, sealedObject, token).ConfigureAwait(false);
                            using var commit = await send(
                                HttpMethod.Post,
                                "/v1/sessions/" + sessionId.ToString("D") + "/history/uploads/" + uploadId.ToString("D") + "/commit",
                                Array.Empty<byte>(),
                                true,
                                "history-commit-" + uploadId.ToString("N"),
                                token).ConfigureAwait(false);
                            commit.EnsureSuccessStatusCode();
                            cursors[cursorKey] = batch[^1].Seq;
                            MarkChunkUploaded(sessionId, recipient, batch, plaintextDigest, sealedObject);
                        }
                        finally
                        {
                            if (createBody != null) CryptographicOperations.ZeroMemory(createBody);
                            if (digest != null) CryptographicOperations.ZeroMemory(digest);
                            if (plaintextDigest != null) CryptographicOperations.ZeroMemory(plaintextDigest);
                            if (sealedObject != null) CryptographicOperations.ZeroMemory(sealedObject);
                        }
                    }
                    return ProductionHistoryUploadOutcome.Durable;
                }
                finally { uploadGate.Release(); }
            }
            finally { EndOperation(); }
        }

        public async Task<ProductionHistoryUploadOutcome> UploadTerminalArtifactAsync(
            Guid sessionId,
            ProductionHistoryRecipient recipient,
            ProductionTerminalHistoryPreparedUpload upload,
            CancellationToken token)
        {
            // Keep the terminal-object gate at the transport boundary as well
            // as in ProductionAgentRuntime. Legacy callers therefore remain
            // CHUNK-only unless the runtime explicitly passes its advertised
            // terminal_snapshot_v1 flag into this uploader instance.
            if (!terminalSnapshotEnabled || sessionId == Guid.Empty || recipient == null ||
                recipient.PairwiseKey == null || recipient.PairwiseKey.Length != 32 || recipient.KeyEpoch < 1 ||
                upload == null || upload.Artifact == null || upload.SealedObject == null || upload.SealedObject.Length == 0 ||
                upload.SessionId != sessionId || upload.RecipientDeviceId != recipient.DeviceId ||
                upload.KeyEpoch != recipient.KeyEpoch || !TryBeginOperation())
                return ProductionHistoryUploadOutcome.Unavailable;
            try
            {
                var artifact = upload.Artifact;
                var expectedKeyId = artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript
                    ? SnapshotCrypto.TranscriptKeyId(recipient.DeviceId, recipient.KeyEpoch)
                    : SnapshotCrypto.SnapshotKeyId(recipient.DeviceId, recipient.KeyEpoch);
                ValidateTerminalArtifact(sessionId, recipient, artifact, upload.SealedObject, expectedKeyId);
                var gateKey = sessionId.ToString("N") + ":" + expectedKeyId + ":" + artifact.ArtifactId;
                var uploadGate = uploadGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
                await uploadGate.WaitAsync(token).ConfigureAwait(false);
                byte[] digest = null;
                byte[] createBody = null;
                try
                {
                    digest = SHA256.HashData(upload.SealedObject);
                    var metadata = new Dictionary<string, object>
                    {
                        ["kind"] = artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript ? "TRANSCRIPT" : "SNAPSHOT",
                        ["key_id"] = expectedKeyId,
                        ["ciphertext_sha256"] = Base64Url(digest),
                        ["size_bytes"] = upload.SealedObject.LongLength,
                        ["format_name"] = artifact.FormatName,
                        ["format_version"] = artifact.FormatVersion
                    };
                    if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript)
                    {
                        metadata["as_of_snapshot_seq"] = artifact.SnapshotSequence;
                        metadata["first_line_id"] = artifact.FirstLineId;
                        metadata["last_line_id"] = artifact.LastLineId;
                    }
                    else if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
                    {
                        metadata["snapshot_seq"] = artifact.SnapshotSequence;
                        metadata["rows"] = artifact.Rows;
                        metadata["columns"] = artifact.Columns;
                        metadata["transcript_initial_before_line_id"] = artifact.InitialTranscriptBeforeLineId;
                        metadata["transcript_page_count"] = artifact.TranscriptPageCount;
                    }
                    else
                    {
                        throw new InvalidDataException("Terminal history upload kind is unsupported.");
                    }

                    createBody = JsonSerializer.SerializeToUtf8Bytes(metadata);
                    var kindName = artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript ? "transcript" : "snapshot";
                    var createIdempotencyKey = CreateIdempotencyKey(kindName, Guid.NewGuid());
                    using var create = await send(
                        HttpMethod.Post,
                        "/v1/sessions/" + sessionId.ToString("D") + "/history/uploads",
                        createBody,
                        true,
                        createIdempotencyKey,
                        token).ConfigureAwait(false);
                    if (IsTemporarilyUnavailable(create.StatusCode)) return ProductionHistoryUploadOutcome.Unavailable;
                    create.EnsureSuccessStatusCode();
                    using var document = JsonDocument.Parse(await create.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                    var uploadId = document.RootElement.GetProperty("upload_id").GetGuid();
                    await PutCiphertextAsync(document.RootElement.GetProperty("upload"), upload.SealedObject, token).ConfigureAwait(false);
                    using var commit = await send(
                        HttpMethod.Post,
                        "/v1/sessions/" + sessionId.ToString("D") + "/history/uploads/" + uploadId.ToString("D") + "/commit",
                        Array.Empty<byte>(),
                        true,
                        "history-commit-" + uploadId.ToString("N"),
                        token).ConfigureAwait(false);
                    if (IsTemporarilyUnavailable(commit.StatusCode)) return ProductionHistoryUploadOutcome.Unavailable;
                    commit.EnsureSuccessStatusCode();
                    return ProductionHistoryUploadOutcome.Durable;
                }
                finally
                {
                    if (createBody != null) CryptographicOperations.ZeroMemory(createBody);
                    if (digest != null) CryptographicOperations.ZeroMemory(digest);
                    uploadGate.Release();
                }
            }
            finally { EndOperation(); }
        }

        private async Task<bool> InitializeCursorAsync(Guid sessionId, string keyId, string cursorKey, CancellationToken token)
        {
            if (initialized.ContainsKey(cursorKey)) return true;
            using var response = await send(HttpMethod.Get, "/v1/sessions/" + sessionId.ToString("D") + "/history", Array.Empty<byte>(), true, null, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return false;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
            ulong highest = 0;
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                if (!string.Equals(item.GetProperty("kind").GetString(), "chunk", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.GetProperty("key_id").GetString(), keyId, StringComparison.Ordinal)) continue;
                if (item.TryGetProperty("end_seq", out var end) && end.ValueKind == JsonValueKind.Number && end.TryGetUInt64(out var value))
                    highest = Math.Max(highest, value);
            }
            cursors[cursorKey] = highest;
            initialized.TryAdd(cursorKey, 0);
            return true;
        }

        private async Task PutCiphertextAsync(JsonElement upload, byte[] sealedObject, CancellationToken token)
        {
            var method = upload.GetProperty("method").GetString();
            if (!string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("History upload did not return PUT.");
            using var request = new HttpRequestMessage(HttpMethod.Put, upload.GetProperty("url").GetString());
            request.Content = new ByteArrayContent(sealedObject);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (upload.TryGetProperty("headers", out var headers))
            {
                foreach (var property in headers.EnumerateObject())
                {
                    if (property.Name.Equals("host", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
                    var values = property.Value.EnumerateArray().Select(value => value.GetString()).Where(value => value != null).ToArray();
                    if (!request.Headers.TryAddWithoutValidation(property.Name, values))
                    {
                        request.Content.Headers.Remove(property.Name);
                        request.Content.Headers.TryAddWithoutValidation(property.Name, values);
                    }
                }
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        private static IEnumerable<IList<HistoryEventV1>> ContiguousBatches(IList<HistoryEventV1> values)
        {
            var batch = new List<HistoryEventV1>();
            var bytes = 0;
            foreach (var value in values)
            {
                if (batch.Count > 0 && (value.Seq != batch[^1].Seq + 1 || batch.Count >= 512 || bytes + value.CalculateSize() > 1024 * 1024))
                {
                    yield return batch;
                    batch = new List<HistoryEventV1>();
                    bytes = 0;
                }
                batch.Add(value.Clone());
                bytes += value.CalculateSize();
            }
            if (batch.Count > 0) yield return batch;
        }

        private byte[] GetOrCreateSealedChunk(
            Guid sessionId,
            ProductionHistoryRecipient recipient,
            IList<HistoryEventV1> batch,
            byte[] plaintextDigest)
        {
            if (durableStore != null)
            {
                return durableStore.GetOrCreateSealedChunk(
                    sessionId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    batch[0].Seq,
                    batch[^1].Seq,
                    plaintextDigest,
                    () => HistoryCrypto.Seal(
                        recipient.PairwiseKey,
                        sessionId,
                        pcDeviceId,
                        recipient.DeviceId,
                        recipient.KeyEpoch,
                        batch));
            }
            var key = ChunkCacheKey(sessionId, recipient, batch, plaintextDigest);
            if (chunkSealedCache.TryGetValue(key, out var existing)) return (byte[])existing.Clone();
            var created = HistoryCrypto.Seal(
                recipient.PairwiseKey,
                sessionId,
                pcDeviceId,
                recipient.DeviceId,
                recipient.KeyEpoch,
                batch);
            chunkSealedCache[key] = (byte[])created.Clone();
            return created;
        }

        private void MarkChunkUploaded(
            Guid sessionId,
            ProductionHistoryRecipient recipient,
            IList<HistoryEventV1> batch,
            byte[] plaintextDigest,
            byte[] sealedObject)
        {
            if (durableStore != null)
            {
                durableStore.MarkChunkUploaded(
                    sessionId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    batch[0].Seq,
                    batch[^1].Seq,
                    plaintextDigest,
                    sealedObject);
                return;
            }
            if (chunkSealedCache.TryRemove(ChunkCacheKey(sessionId, recipient, batch, plaintextDigest), out var cached))
                CryptographicOperations.ZeroMemory(cached);
        }

        private static byte[] ChunkPlaintextDigest(IList<HistoryEventV1> batch)
        {
            var chunk = new HistoryChunkV1();
            chunk.Events.AddRange(batch);
            var bytes = chunk.ToByteArray();
            try { return SHA256.HashData(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private static string ChunkCacheKey(
            Guid sessionId,
            ProductionHistoryRecipient recipient,
            IList<HistoryEventV1> batch,
            byte[] plaintextDigest)
        {
            return sessionId.ToString("N") + ":" + recipient.DeviceId.ToString("N") + ":" +
                recipient.KeyEpoch.ToString("D8") + ":" + batch[0].Seq.ToString("D20") + ":" +
                batch[^1].Seq.ToString("D20") + ":" + Convert.ToHexString(plaintextDigest).ToLowerInvariant();
        }

        private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static string CreateIdempotencyKey(string kind, Guid operationId)
        {
            if ((kind != "chunk" && kind != "transcript" && kind != "snapshot") || operationId == Guid.Empty)
                throw new ArgumentException("History upload idempotency identity is invalid.");
            return "history-create-v2-" + kind + "-" + operationId.ToString("N");
        }

        private void ValidateTerminalArtifact(
            Guid sessionId,
            ProductionHistoryRecipient recipient,
            ProductionTerminalHistoryArtifact artifact,
            byte[] sealedObject,
            string expectedKeyId)
        {
            if (artifact.FormatVersion != checked((int)SnapshotCrypto.FormatVersion))
                throw new InvalidDataException("Terminal history format version is unsupported.");
            if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript)
            {
                if (artifact.FormatName != SnapshotCrypto.TranscriptFormatName || artifact.FirstLineId == 0 ||
                    artifact.LastLineId < artifact.FirstLineId || artifact.ExclusiveBeforeLineId <= artifact.LastLineId ||
                    artifact.InitialTranscriptBeforeLineId != 0 || artifact.TranscriptPageCount != 0)
                    throw new InvalidDataException("Terminal transcript upload metadata is invalid.");
                SnapshotCrypto.OpenTranscript(
                    recipient.PairwiseKey,
                    sealedObject,
                    sessionId,
                    pcDeviceId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    expectedKeyId,
                    artifact.SnapshotSequence,
                    artifact.FirstLineId,
                    artifact.LastLineId,
                    artifact.ExclusiveBeforeLineId);
                return;
            }
            if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
            {
                if (artifact.FormatName != SnapshotCrypto.SnapshotFormatName || artifact.Columns < 1 || artifact.Rows < 1 ||
                    artifact.InitialTranscriptBeforeLineId == 0 || artifact.TranscriptPageCount < 0 ||
                    artifact.TranscriptPageCount > MaximumTranscriptPublicationPageCount)
                    throw new InvalidDataException("Terminal snapshot upload metadata is invalid.");
                var snapshot = SnapshotCrypto.OpenSnapshot(
                    recipient.PairwiseKey,
                    sealedObject,
                    sessionId,
                    pcDeviceId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    expectedKeyId,
                    artifact.SnapshotSequence,
                    checked((uint)artifact.Columns),
                    checked((uint)artifact.Rows));
                if (SnapshotCrypto.InitialTranscriptBeforeLineId(snapshot) != artifact.InitialTranscriptBeforeLineId)
                    throw new InvalidDataException("Terminal snapshot publication boundary does not match its ciphertext.");
                return;
            }
            throw new InvalidDataException("Terminal history upload kind is unsupported.");
        }

        private static bool IsTemporarilyUnavailable(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.ServiceUnavailable || statusCode == HttpStatusCode.Forbidden ||
                statusCode == HttpStatusCode.Conflict || statusCode == HttpStatusCode.RequestTimeout ||
                statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.BadGateway ||
                statusCode == HttpStatusCode.GatewayTimeout;
        }

        private bool TryBeginOperation()
        {
            lock (operationSync)
            {
                if (disposed != 0) return false;
                activeOperations++;
                return true;
            }
        }

        private void EndOperation()
        {
            lock (operationSync)
            {
                if (activeOperations < 1)
                    throw new InvalidOperationException("History uploader operation accounting underflowed.");
                activeOperations--;
                if (activeOperations == 0) Monitor.PulseAll(operationSync);
            }
        }

        public void Dispose()
        {
            lock (operationSync)
            {
                if (disposed != 0) return;
                disposed = 1;
                while (activeOperations != 0) Monitor.Wait(operationSync);
            }
            cursors.Clear();
            initialized.Clear();
            foreach (var value in chunkSealedCache.Values) CryptographicOperations.ZeroMemory(value);
            chunkSealedCache.Clear();
            // SemaphoreSlim.Release may still execute in an async finally while
            // Dispose is waiting for the operation drain. These gates do not
            // allocate wait handles in this class, so clearing without calling
            // Dispose avoids a Release/Dispose race and leaves no OS resource.
            uploadGates.Clear();
        }
    }
}
