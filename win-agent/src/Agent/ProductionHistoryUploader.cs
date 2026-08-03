using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal sealed class ProductionHistoryRecipient
    {
        public Guid DeviceId;
        public byte[] PairwiseKey;
    }

    internal sealed class ProductionHistoryUploader : IDisposable
    {
        private readonly HttpClient http;
        private readonly Func<HttpMethod, string, byte[], bool, string, CancellationToken, Task<HttpResponseMessage>> send;
        private readonly Guid pcDeviceId;
        private readonly long keyEpoch;
        private readonly ConcurrentDictionary<string, ulong> cursors = new ConcurrentDictionary<string, ulong>();
        private readonly ConcurrentDictionary<string, byte> initialized = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> uploadGates = new ConcurrentDictionary<string, SemaphoreSlim>();
        private int disposed;

        public ProductionHistoryUploader(
            HttpClient http,
            Func<HttpMethod, string, byte[], bool, string, CancellationToken, Task<HttpResponseMessage>> send,
            Guid pcDeviceId,
            long keyEpoch)
        {
            this.http = http ?? throw new ArgumentNullException(nameof(http));
            this.send = send ?? throw new ArgumentNullException(nameof(send));
            this.pcDeviceId = pcDeviceId;
            this.keyEpoch = keyEpoch;
        }

        public async Task UploadAsync(Guid sessionId, ProductionHistoryRecipient recipient, IList<HistoryEventV1> events, CancellationToken token)
        {
            if (Volatile.Read(ref disposed) != 0 || recipient == null || recipient.PairwiseKey == null || events == null || events.Count == 0) return;
            var keyId = HistoryCrypto.KeyId(recipient.DeviceId, keyEpoch);
            var cursorKey = sessionId.ToString("N") + ":" + keyId;
            var uploadGate = uploadGates.GetOrAdd(cursorKey, _ => new SemaphoreSlim(1, 1));
            await uploadGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await InitializeCursorAsync(sessionId, keyId, cursorKey, token).ConfigureAwait(false);
                cursors.TryGetValue(cursorKey, out var cursor);
                var remaining = events.Where(value => value.Seq > cursor).OrderBy(value => value.Seq).ToList();
                if (remaining.Count == 0) return;

                foreach (var batch in ContiguousBatches(remaining))
                {
                    byte[] sealedObject = null;
                    byte[] digest = null;
                    byte[] createBody = null;
                    try
                    {
                        sealedObject = HistoryCrypto.Seal(recipient.PairwiseKey, sessionId, pcDeviceId, recipient.DeviceId, keyEpoch, batch);
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
                        using var create = await send(
                            HttpMethod.Post,
                            "/v1/sessions/" + sessionId.ToString("D") + "/history/uploads",
                            createBody,
                            true,
                            "history-create-" + sessionId.ToString("N") + "-" + recipient.DeviceId.ToString("N") + "-" +
                                batch[0].Seq + "-" + batch[^1].Seq + "-" + Convert.ToHexString(digest, 0, 6).ToLowerInvariant(),
                            token).ConfigureAwait(false);
                        if (create.StatusCode == HttpStatusCode.ServiceUnavailable || create.StatusCode == HttpStatusCode.Forbidden) return;
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
                    }
                    finally
                    {
                        if (createBody != null) CryptographicOperations.ZeroMemory(createBody);
                        if (digest != null) CryptographicOperations.ZeroMemory(digest);
                        if (sealedObject != null) CryptographicOperations.ZeroMemory(sealedObject);
                    }
                }
            }
            finally { uploadGate.Release(); }
        }

        private async Task InitializeCursorAsync(Guid sessionId, string keyId, string cursorKey, CancellationToken token)
        {
            if (initialized.ContainsKey(cursorKey)) return;
            using var response = await send(HttpMethod.Get, "/v1/sessions/" + sessionId.ToString("D") + "/history", Array.Empty<byte>(), true, null, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable || response.StatusCode == HttpStatusCode.Forbidden)
            {
                initialized.TryAdd(cursorKey, 0);
                return;
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

        private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
            cursors.Clear();
            initialized.Clear();
            foreach (var gate in uploadGates.Values) gate.Dispose();
            uploadGates.Clear();
        }
    }
}
