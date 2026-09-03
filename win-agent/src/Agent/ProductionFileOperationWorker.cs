using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    internal sealed class ClaimedFileOperation
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; }

        [JsonPropertyName("offset")]
        public long? Offset { get; set; }

        [JsonPropertyName("shell_type")]
        public string ShellType { get; set; }

        [JsonPropertyName("terminal_command_id")]
        public string TerminalCommandId { get; set; }

        [JsonIgnore]
        public string RequestedTerminalCommandId =>
            string.IsNullOrWhiteSpace(TerminalCommandId) ? ShellType : TerminalCommandId;

        [JsonPropertyName("lease_token")]
        public string LeaseToken { get; set; }

        [JsonPropertyName("lease_expires_at")]
        public DateTimeOffset? LeaseExpiresAt { get; set; }
    }

    internal sealed class FileOperationUploadTarget
    {
        internal Uri Url { get; set; }
        internal HttpMethod Method { get; set; }
        internal IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; set; }
    }

    internal interface IFileOperationApi
    {
        Task<ClaimedFileOperation> ClaimAsync(CancellationToken token);
        Task<FileOperationUploadTarget> CreateUploadAsync(
            ClaimedFileOperation operation,
            FileTransferArtifact artifact,
            CancellationToken token);
        Task UploadAsync(
            FileOperationUploadTarget target,
            Stream content,
            long sizeBytes,
            CancellationToken token);
        Task<DateTimeOffset?> RenewAsync(
            ClaimedFileOperation operation,
            CancellationToken token);
        Task CompleteAsync(
            ClaimedFileOperation operation,
            JsonElement? result,
            CancellationToken token);
        Task FailAsync(
            ClaimedFileOperation operation,
            string errorCode,
            string errorMessage,
            CancellationToken token);
    }

    internal class FileOperationRemoteException : HttpRequestException
    {
        internal FileOperationRemoteException(string message, bool retryable = true) : base(message)
        {
            Retryable = retryable;
        }
        internal FileOperationRemoteException(string message, Exception innerException)
            : base(message, innerException) { Retryable = true; }
        internal bool Retryable { get; }
    }

    internal sealed class FileOperationLeaseLostException : FileOperationRemoteException
    {
        internal FileOperationLeaseLostException(string message) : base(message, false) { }
    }

    internal sealed class ProductionFileOperationApi : IFileOperationApi
    {
        internal delegate Task<HttpResponseMessage> AuthenticatedSendAsync(
            HttpMethod method,
            string path,
            byte[] body,
            bool deviceProof,
            string idempotencyKey,
            CancellationToken token);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        private readonly Guid pcId;
        private readonly HttpClient uploadHttp;
        private readonly AuthenticatedSendAsync send;
        private readonly ProductionClaimIdempotencyTracker claimIdempotency =
            new ProductionClaimIdempotencyTracker();
        private long renewalSequence;
        private long uploadSequence;

        internal ProductionFileOperationApi(
            Guid pcId,
            HttpClient uploadHttp,
            AuthenticatedSendAsync send)
        {
            if (pcId == Guid.Empty) throw new ArgumentException("PC ID is required.", nameof(pcId));
            this.pcId = pcId;
            this.uploadHttp = uploadHttp ?? throw new ArgumentNullException(nameof(uploadHttp));
            this.send = send ?? throw new ArgumentNullException(nameof(send));
        }

        public async Task<ClaimedFileOperation> ClaimAsync(CancellationToken token)
        {
            var idempotencyKey = claimIdempotency.BeginAttempt();
            using var response = await send(
                HttpMethod.Post,
                BasePath + "/claim",
                Array.Empty<byte>(),
                true,
                idempotencyKey,
                token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                claimIdempotency.ObserveDefinitiveResponse(idempotencyKey);
                return null;
            }
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var errorBytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (HasErrorCode(errorBytes, "IDEMPOTENCY_IN_PROGRESS"))
                {
                    claimIdempotency.ObserveInProgress(idempotencyKey);
                    throw new FileOperationRemoteException(
                        "File-operation claim idempotency is still in progress.");
                }
            }
            if (!response.IsSuccessStatusCode)
            {
                // The status is definitive and no lease can be stranded by
                // rotating the key, even if the optional error body is broken.
                claimIdempotency.ObserveDefinitiveResponse(idempotencyKey);
                await EnsureSuccessAsync(response, "File-operation claim failed.", token).ConfigureAwait(false);
            }

            // Complete the body read before rotating a successful claim. A
            // transport failure after response headers is still ambiguous and
            // must replay the same key to recover the already-created lease.
            var responseBytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            claimIdempotency.ObserveDefinitiveResponse(idempotencyKey);
            using var document = JsonDocument.Parse(responseBytes);
            if (!document.RootElement.TryGetProperty("file_operation", out var operationElement) ||
                operationElement.ValueKind != JsonValueKind.Object)
            {
                throw new FileOperationRemoteException("File-operation claim response is incomplete.");
            }
            var operation = operationElement.Deserialize<ClaimedFileOperation>(JsonOptions);
            if (!document.RootElement.TryGetProperty("lease_token", out var leaseTokenElement) ||
                leaseTokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(leaseTokenElement.GetString()))
            {
                throw new FileOperationRemoteException("File-operation claim lease is incomplete.");
            }
            operation.LeaseToken = leaseTokenElement.GetString();
            ValidateClaim(operation);
            return operation;
        }

        private static bool HasErrorCode(byte[] body, string expected)
        {
            if (body == null || body.Length == 0 || body.Length > 4096) return false;
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String &&
                    string.Equals(code.GetString(), expected, StringComparison.Ordinal);
            }
            catch (JsonException) { return false; }
        }

        public async Task<FileOperationUploadTarget> CreateUploadAsync(
            ClaimedFileOperation operation,
            FileTransferArtifact artifact,
            CancellationToken token)
        {
            ValidateOperationLease(operation);
            if (artifact == null) throw new ArgumentNullException(nameof(artifact));
            var requestBody = Serialize(new
            {
                lease_token = operation.LeaseToken,
                filename = artifact.Filename,
                size_bytes = artifact.SizeBytes,
                sha256 = artifact.Sha256Base64Url,
                directory_zip = artifact.DirectoryZip
            });
            var uploadAttempt = Interlocked.Increment(ref uploadSequence);
            using var response = await send(
                HttpMethod.Post,
                OperationPath(operation.Id) + "/upload",
                requestBody,
                true,
                LeaseIdempotencyKey(operation, "upload-" + uploadAttempt.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
                token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "File-operation upload allocation failed.", token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("upload", out var upload) ||
                upload.ValueKind != JsonValueKind.Object ||
                !upload.TryGetProperty("url", out var urlElement) ||
                !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url) ||
                !upload.TryGetProperty("method", out var methodElement))
            {
                throw new FileOperationRemoteException("File-operation upload response is incomplete.");
            }
            var methodText = methodElement.GetString();
            if (!string.Equals(methodText, "PUT", StringComparison.OrdinalIgnoreCase))
                throw new FileOperationRemoteException("File-operation upload method is unsupported.");
            if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
                throw new FileOperationRemoteException("File-operation upload URL is invalid.");
            var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (upload.TryGetProperty("headers", out var headerElement) &&
                headerElement.ValueKind != JsonValueKind.Null)
            {
                if (headerElement.ValueKind != JsonValueKind.Object)
                    throw new FileOperationRemoteException("File-operation upload headers are invalid.");
                foreach (var property in headerElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(property.Name))
                    {
                        throw new FileOperationRemoteException("File-operation upload headers are invalid.");
                    }
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        headers[property.Name] = new[] { property.Value.GetString() };
                        continue;
                    }
                    if (property.Value.ValueKind != JsonValueKind.Array)
                        throw new FileOperationRemoteException("File-operation upload headers are invalid.");
                    var values = new List<string>();
                    foreach (var value in property.Value.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.String)
                            throw new FileOperationRemoteException("File-operation upload headers are invalid.");
                        values.Add(value.GetString());
                    }
                    if (values.Count == 0)
                        throw new FileOperationRemoteException("File-operation upload headers are invalid.");
                    headers[property.Name] = values;
                }
            }
            return new FileOperationUploadTarget
            {
                Url = url,
                Method = HttpMethod.Put,
                Headers = headers
            };
        }

        public async Task UploadAsync(
            FileOperationUploadTarget target,
            Stream content,
            long sizeBytes,
            CancellationToken token)
        {
            if (target == null || target.Url == null || target.Method == null)
                throw new ArgumentException("Upload target is incomplete.", nameof(target));
            if (content == null || !content.CanRead) throw new ArgumentException("Upload content is unreadable.", nameof(content));
            using var request = new HttpRequestMessage(target.Method, target.Url);
            request.Content = new StreamContent(content, 128 * 1024);
            request.Content.Headers.ContentLength = sizeBytes;
            foreach (var header in target.Headers ??
                new Dictionary<string, IReadOnlyList<string>>())
            {
                if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) &&
                    !request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    throw new FileOperationRemoteException("File-operation upload contains an unsupported header.");
                }
            }
            using var response = await uploadHttp.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "File-operation content upload failed.", token).ConfigureAwait(false);
        }

        public async Task<DateTimeOffset?> RenewAsync(
            ClaimedFileOperation operation,
            CancellationToken token)
        {
            ValidateOperationLease(operation);
            var requestBody = Serialize(new { lease_token = operation.LeaseToken });
            var renewal = Interlocked.Increment(ref renewalSequence);
            using var response = await send(
                HttpMethod.Post,
                OperationPath(operation.Id) + "/renew",
                requestBody,
                true,
                LeaseIdempotencyKey(operation, "renew-" + renewal.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
                token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new FileOperationLeaseLostException("File-operation lease is no longer active.");
            await EnsureSuccessAsync(response, "File-operation lease renewal failed.", token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("file_operation", out var operationElement) ||
                operationElement.ValueKind != JsonValueKind.Object)
            {
                throw new FileOperationRemoteException("File-operation renewal response is incomplete.");
            }
            var renewed = operationElement.Deserialize<ClaimedFileOperation>(JsonOptions);
            if (!renewed.LeaseExpiresAt.HasValue)
                throw new FileOperationRemoteException("File-operation renewal lease expiry is incomplete.");
            operation.LeaseExpiresAt = renewed.LeaseExpiresAt;
            return operation.LeaseExpiresAt;
        }

        public async Task CompleteAsync(
            ClaimedFileOperation operation,
            JsonElement? result,
            CancellationToken token)
        {
            ValidateOperationLease(operation);
            var body = result.HasValue
                ? Serialize(new { lease_token = operation.LeaseToken, status = "SUCCEEDED", result = result.Value })
                : Serialize(new { lease_token = operation.LeaseToken, status = "SUCCEEDED" });
            using var response = await send(
                HttpMethod.Post,
                OperationPath(operation.Id) + "/complete",
                body,
                true,
                LeaseIdempotencyKey(operation, "complete"),
                token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "File-operation completion failed.", token).ConfigureAwait(false);
        }

        public async Task FailAsync(
            ClaimedFileOperation operation,
            string errorCode,
            string errorMessage,
            CancellationToken token)
        {
            ValidateOperationLease(operation);
            if (string.IsNullOrWhiteSpace(errorCode) || string.IsNullOrWhiteSpace(errorMessage))
                throw new ArgumentException("A stable file-operation error is required.");
            var stableError = errorCode + ": " + errorMessage;
            var body = Serialize(new
            {
                lease_token = operation.LeaseToken,
                status = "FAILED",
                error = Truncate(stableError, 1024)
            });
            using var response = await send(
                HttpMethod.Post,
                OperationPath(operation.Id) + "/complete",
                body,
                true,
                LeaseIdempotencyKey(operation, "fail"),
                token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "File-operation failure report failed.", token).ConfigureAwait(false);
        }

        private string BasePath => "/v1/pcs/" + pcId.ToString("D") + "/file-operations";
        private string OperationPath(Guid operationId) => BasePath + "/" + operationId.ToString("D");
        private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

        private static string LeaseIdempotencyKey(ClaimedFileOperation operation, string action)
        {
            ValidateOperationLease(operation);
            var leaseBytes = Encoding.UTF8.GetBytes(operation.LeaseToken);
            byte[] digest = null;
            try
            {
                digest = SHA256.HashData(leaseBytes);
                return operation.Id.ToString("N") + ":" + action + ":" +
                    Convert.ToHexString(digest).ToLowerInvariant();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(leaseBytes);
                if (digest != null) CryptographicOperations.ZeroMemory(digest);
            }
        }

        private static void ValidateClaim(ClaimedFileOperation operation)
        {
            ValidateOperationLease(operation);
            if (!string.Equals(operation.Operation, "LIST", StringComparison.Ordinal) &&
                !string.Equals(operation.Operation, "DOWNLOAD", StringComparison.Ordinal) &&
                !string.Equals(operation.Operation, "OPEN_TERMINAL", StringComparison.Ordinal))
            {
                throw new FileOperationRemoteException("File-operation claim contains an unsupported operation.");
            }
        }

        private static void ValidateOperationLease(ClaimedFileOperation operation)
        {
            if (operation == null || operation.Id == Guid.Empty || string.IsNullOrWhiteSpace(operation.LeaseToken))
                throw new FileOperationRemoteException("File-operation lease is incomplete.");
        }

        private static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            string message,
            CancellationToken token)
        {
            if (response.IsSuccessStatusCode) return;
            var detail = string.Empty;
            var idempotencyInProgress = false;
            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (bytes.Length > 0 && bytes.Length <= 4096) detail = Encoding.UTF8.GetString(bytes);
                idempotencyInProgress = HasErrorCode(bytes, "IDEMPOTENCY_IN_PROGRESS");
            }
            catch (Exception error) when (error is IOException || error is InvalidOperationException) { }
            throw new FileOperationRemoteException(
                message + " HTTP " + ((int)response.StatusCode).ToString() +
                (string.IsNullOrWhiteSpace(detail) ? "." : ": " + Truncate(detail, 512)),
                idempotencyInProgress || IsRetryable(response.StatusCode));
        }

        private static bool IsRetryable(HttpStatusCode status) =>
            status == HttpStatusCode.RequestTimeout ||
            (int)status == 429 ||
            (int)status >= 500;

        private static string Truncate(string value, int maximum) =>
            value.Length <= maximum ? value : value.Substring(0, maximum);
    }

    internal sealed class FileOperationWorkerDiagnostic
    {
        internal FileOperationWorkerDiagnostic(
            string code,
            string messageZh,
            string messageEn,
            Exception error,
            int restartCount)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            MessageZh = messageZh ?? throw new ArgumentNullException(nameof(messageZh));
            MessageEn = messageEn ?? throw new ArgumentNullException(nameof(messageEn));
            Error = error;
            RestartCount = restartCount;
        }

        internal string Code { get; }
        internal string MessageZh { get; }
        internal string MessageEn { get; }
        internal Exception Error { get; }
        internal int RestartCount { get; }
    }

    internal sealed class ProductionFileOperationWorker : IDisposable
    {
        private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan RemoteAccessPollInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan DefaultLeaseRenewInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DefaultRegistrationWait = TimeSpan.FromSeconds(32);
        private static readonly TimeSpan DefaultSupervisorRestartDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan FallbackLeaseDuration = TimeSpan.FromHours(1);
        private readonly IFileOperationApi api;
        private readonly DirectoryListingService listings;
        private readonly FileTransferPreparer transfers;
        private readonly ManagedTerminalLauncher terminals;
        private readonly Func<bool> remoteAccessEnabled;
        private readonly Func<Guid, bool> sessionVisible;
        private readonly Func<Guid, bool> terminateSession;
        private readonly Action<FileOperationWorkerDiagnostic> diagnostic;
        private readonly TimeSpan leaseRenewInterval;
        private readonly TimeSpan registrationWait;
        private readonly TimeSpan supervisorRestartDelay;
        private readonly CancellationTokenSource cancellation;
        private readonly object lifecycleSync = new object();
        private Task worker;
        private int recoveryPending;
        private int restartCount;
        private long nextSupervisorRestartDelayTicks;
        private int disposed;

        internal ProductionFileOperationWorker(
            IFileOperationApi api,
            DirectoryListingService listings,
            FileTransferPreparer transfers,
            ManagedTerminalLauncher terminals,
            CancellationToken parentToken,
            Func<bool> remoteAccessEnabled = null,
            Func<Guid, bool> sessionVisible = null,
            Func<Guid, bool> terminateSession = null,
            TimeSpan? leaseRenewInterval = null,
            TimeSpan? registrationWait = null,
            TimeSpan? supervisorRestartDelay = null,
            Action<FileOperationWorkerDiagnostic> diagnostic = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.listings = listings ?? throw new ArgumentNullException(nameof(listings));
            this.transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
            this.terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
            this.remoteAccessEnabled = remoteAccessEnabled ?? (() => true);
            this.sessionVisible = sessionVisible ?? (_ => true);
            this.terminateSession = terminateSession ?? (_ => false);
            this.leaseRenewInterval = leaseRenewInterval ?? DefaultLeaseRenewInterval;
            this.registrationWait = registrationWait ?? DefaultRegistrationWait;
            this.supervisorRestartDelay = supervisorRestartDelay ?? DefaultSupervisorRestartDelay;
            this.diagnostic = diagnostic;
            if (this.leaseRenewInterval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(leaseRenewInterval));
            if (this.registrationWait <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(registrationWait));
            if (this.supervisorRestartDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(supervisorRestartDelay));
            nextSupervisorRestartDelayTicks = this.supervisorRestartDelay.Ticks;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        }

        internal int RestartCount => Volatile.Read(ref restartCount);

        internal bool IsRunning
        {
            get
            {
                lock (lifecycleSync)
                    return worker != null && !worker.IsCompleted && Volatile.Read(ref disposed) == 0;
            }
        }

        internal void Start()
        {
            lock (lifecycleSync)
            {
                if (Volatile.Read(ref disposed) != 0)
                    throw new ObjectDisposedException(nameof(ProductionFileOperationWorker));
                if (worker != null) throw new InvalidOperationException("File-operation worker is already started.");
                worker = Task.Run(() => SuperviseAsync(cancellation.Token));
            }
        }

        private async Task SuperviseAsync(CancellationToken token)
        {
            while (true)
            {
                try
                {
                    await RunAsync(token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    var currentRestart = Interlocked.Increment(ref restartCount);
                    if (Interlocked.CompareExchange(ref recoveryPending, 1, 0) == 0)
                    {
                        ReportDiagnostic(
                            "FILE_OPERATION_WORKER_RESTARTING",
                            "目录与文件任务线程发生异常，正在独立恢复，不会重启整个 Agent。",
                            "The directory and file-operation worker failed and is recovering independently without restarting the Agent.",
                            error,
                            currentRestart);
                    }
                    var delay = TimeSpan.FromTicks(Interlocked.Read(ref nextSupervisorRestartDelayTicks));
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    Interlocked.Exchange(
                        ref nextSupervisorRestartDelayTicks,
                        NextRetryDelay(delay).Ticks);
                }
            }
        }

        internal async Task RunAsync(CancellationToken token)
        {
            var retryDelay = TimeSpan.FromSeconds(1);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!remoteAccessEnabled())
                    {
                        retryDelay = TimeSpan.FromSeconds(1);
                        await Task.Delay(RemoteAccessPollInterval, token).ConfigureAwait(false);
                        continue;
                    }
                    var operation = await api.ClaimAsync(token).ConfigureAwait(false);
                    MarkRecovered();
                    if (operation == null)
                    {
                        retryDelay = TimeSpan.FromSeconds(1);
                        await Task.Delay(IdlePollInterval, token).ConfigureAwait(false);
                        continue;
                    }
                    await ProcessAsync(operation, token).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromSeconds(1);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException error)
                {
                    MarkDegraded(
                        "FILE_OPERATION_REQUEST_TIMEOUT",
                        "目录与文件任务请求超时，Worker 将继续重试。",
                        "The directory and file-operation request timed out; the worker will keep retrying.",
                        error);
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                    retryDelay = NextRetryDelay(retryDelay);
                }
                catch (TimeoutException error)
                {
                    MarkDegraded(
                        "FILE_OPERATION_REQUEST_TIMEOUT",
                        "目录与文件任务请求超时，Worker 将继续重试。",
                        "The directory and file-operation request timed out; the worker will keep retrying.",
                        error);
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                    retryDelay = NextRetryDelay(retryDelay);
                }
                catch (FileOperationRemoteException error)
                {
                    MarkDegraded(
                        "FILE_OPERATION_REMOTE_UNAVAILABLE",
                        "目录与文件服务暂时不可用，Worker 将继续重试。",
                        "The directory and file-operation service is temporarily unavailable; the worker will keep retrying.",
                        error);
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                    retryDelay = NextRetryDelay(retryDelay);
                }
                catch (HttpRequestException error)
                {
                    MarkDegraded(
                        "FILE_OPERATION_REMOTE_UNAVAILABLE",
                        "目录与文件服务暂时不可用，Worker 将继续重试。",
                        "The directory and file-operation service is temporarily unavailable; the worker will keep retrying.",
                        error);
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                    retryDelay = NextRetryDelay(retryDelay);
                }
                catch (ProductionReauthenticationRequiredException error)
                {
                    MarkDegraded(
                        "FILE_OPERATION_ACCOUNT_REAUTH_REQUIRED",
                        "账户授权需要刷新，目录与文件 Worker 将等待重新登录。",
                        "Account authorization must be renewed; the directory and file-operation worker will wait for sign-in.",
                        error);
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                    retryDelay = NextRetryDelay(retryDelay);
                }
                catch (JsonException error)
                {
                    MarkDegraded(
                        "FILE_OPERATION_RESPONSE_INVALID",
                        "目录与文件服务返回了无效响应，Worker 将继续重试。",
                        "The directory and file-operation service returned an invalid response; the worker will keep retrying.",
                        error);
                    await Task.Delay(retryDelay, token).ConfigureAwait(false);
                    retryDelay = NextRetryDelay(retryDelay);
                }
            }
        }

        private static TimeSpan NextRetryDelay(TimeSpan current) =>
            TimeSpan.FromSeconds(Math.Min(15, current.TotalSeconds * 2));

        private void MarkDegraded(
            string code,
            string messageZh,
            string messageEn,
            Exception error)
        {
            if (Interlocked.CompareExchange(ref recoveryPending, 1, 0) != 0) return;
            ReportDiagnostic(code, messageZh, messageEn, error, Volatile.Read(ref restartCount));
        }

        private void MarkRecovered()
        {
            if (Interlocked.Exchange(ref recoveryPending, 0) == 0) return;
            Interlocked.Exchange(ref nextSupervisorRestartDelayTicks, supervisorRestartDelay.Ticks);
            ReportDiagnostic(
                "FILE_OPERATION_WORKER_RECOVERED",
                "目录与文件任务 Worker 已恢复。",
                "The directory and file-operation worker has recovered.",
                null,
                Volatile.Read(ref restartCount));
        }

        private void ReportDiagnostic(
            string code,
            string messageZh,
            string messageEn,
            Exception error,
            int currentRestartCount)
        {
            if (diagnostic == null) return;
            try
            {
                diagnostic(new FileOperationWorkerDiagnostic(
                    code,
                    messageZh,
                    messageEn,
                    error,
                    currentRestartCount));
            }
            catch
            {
                // Diagnostics must never become part of file-operation authority.
            }
        }

        private async Task ProcessAsync(ClaimedFileOperation operation, CancellationToken token)
        {
            var execution = new OperationExecutionState();
            Guid? terminalSessionId = null;
            var terminateOperationTerminal = false;
            var operationCompleted = false;
            var terminalTerminationPerformed = false;
            void TerminateCurrentOperationTerminal()
            {
                if (!terminalSessionId.HasValue || terminalTerminationPerformed || operationCompleted) return;
                terminalTerminationPerformed = true;
                // The ID is derived from this file operation and cannot
                // address an unrelated manually-created terminal. This also
                // reclaims a prior ambiguous-retry launch.
                terminateSession(terminalSessionId.Value);
            }
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var remoteMonitor = MonitorRemoteAccessAsync(
                execution,
                operationCancellation,
                monitorCancellation.Token);
            var leaseMonitor = MonitorLeaseAsync(
                operation,
                execution,
                operationCancellation,
                monitorCancellation.Token);
            try
            {
                JsonElement? result = null;
                FileOperationException localFailure = null;
                try
                {
                    switch (operation.Operation)
                    {
                        case "LIST":
                            result = JsonSerializer.SerializeToElement(
                                listings.List(operation.Path, operation.Offset.GetValueOrDefault()),
                                ProductionJsonOptions);
                            break;
                        case "DOWNLOAD":
                            await ExecuteDownloadAsync(operation, operationCancellation.Token).ConfigureAwait(false);
                            break;
                        case "OPEN_TERMINAL":
                            terminalSessionId = StableSessionId(operation.Id);
                            result = await ExecuteOpenTerminalAsync(
                                operation,
                                terminalSessionId.Value,
                                operationCancellation.Token).ConfigureAwait(false);
                            break;
                        default:
                            throw new FileOperationException(
                                "OPERATION_UNSUPPORTED",
                                "The requested operation is unsupported.");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested) { }
                catch (Exception error)
                {
                    localFailure = StableFailure(error);
                }

                if (execution.Reason == OperationStopReason.LeaseLost)
                {
                    terminateOperationTerminal = true;
                    TerminateCurrentOperationTerminal();
                    return;
                }
                if (execution.Reason == OperationStopReason.RemoteAccessDisabled)
                {
                    terminateOperationTerminal = true;
                    TerminateCurrentOperationTerminal();
                    await ReportRemoteAccessDisabledAsync(operation, token).ConfigureAwait(false);
                    return;
                }
                operationCancellation.Token.ThrowIfCancellationRequested();
                if (localFailure == null)
                {
                    if (!execution.TryBeginFinalization())
                    {
                        terminateOperationTerminal = execution.Reason != OperationStopReason.None;
                        if (terminateOperationTerminal) TerminateCurrentOperationTerminal();
                        if (execution.Reason == OperationStopReason.RemoteAccessDisabled)
                            await ReportRemoteAccessDisabledAsync(operation, token).ConfigureAwait(false);
                        return;
                    }
                    try
                    {
                        await RetryRemoteAsync(
                            cancellationToken => api.CompleteAsync(operation, result, cancellationToken),
                            operationCancellation.Token).ConfigureAwait(false);
                        operationCompleted = true;
                    }
                    catch (FileOperationRemoteException error) when (!error.Retryable)
                    {
                        terminateOperationTerminal = true;
                        TerminateCurrentOperationTerminal();
                        await RetryRemoteAsync(
                            cancellationToken => api.FailAsync(
                                operation,
                                "SERVER_REJECTED_RESULT",
                                "The server rejected the file-operation result.",
                                cancellationToken),
                            operationCancellation.Token).ConfigureAwait(false);
                    }
                }
                else
                {
                    terminateOperationTerminal = true;
                    TerminateCurrentOperationTerminal();
                    await RetryRemoteAsync(
                        cancellationToken => api.FailAsync(
                            operation,
                            localFailure.Code,
                            localFailure.Message,
                            cancellationToken),
                        operationCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (
                !token.IsCancellationRequested && operationCancellation.IsCancellationRequested)
            {
                terminateOperationTerminal = execution.Reason != OperationStopReason.None;
                if (terminateOperationTerminal) TerminateCurrentOperationTerminal();
                if (execution.Reason == OperationStopReason.RemoteAccessDisabled)
                    await ReportRemoteAccessDisabledAsync(operation, token).ConfigureAwait(false);
            }
            finally
            {
                monitorCancellation.Cancel();
                await IgnoreMonitorCancellationAsync(remoteMonitor).ConfigureAwait(false);
                await IgnoreMonitorCancellationAsync(leaseMonitor).ConfigureAwait(false);
                if (terminalSessionId.HasValue && !operationCompleted &&
                    (terminateOperationTerminal || execution.Reason != OperationStopReason.None))
                {
                    TerminateCurrentOperationTerminal();
                }
            }
        }

        private async Task ExecuteDownloadAsync(ClaimedFileOperation operation, CancellationToken token)
        {
            await using var artifact = await transfers.PrepareAsync(operation.Path, token).ConfigureAwait(false);
            var stream = artifact.OpenRead();
            var delay = TimeSpan.FromSeconds(1);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var upload = await api.CreateUploadAsync(operation, artifact, token).ConfigureAwait(false);
                    if (stream.CanSeek) stream.Position = 0;
                    await api.UploadAsync(upload, stream, artifact.SizeBytes, token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (FileOperationRemoteException error) when (error.Retryable)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
                }
                catch (HttpRequestException error) when (error is not FileOperationRemoteException)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
                }
                catch (ProductionReauthenticationRequiredException)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
                }
            }
        }

        private async Task<JsonElement> ExecuteOpenTerminalAsync(
            ClaimedFileOperation operation,
            Guid sessionId,
            CancellationToken token)
        {
            var workingDirectory = terminals.NormalizeWorkingDirectory(operation.Path);
            var commandId = operation.RequestedTerminalCommandId;
            var managedShellType = terminals.ManagedShellTypeFor(commandId);
            if (!sessionVisible(sessionId))
                terminals.Launch(commandId, workingDirectory, sessionId);
            var deadline = DateTime.UtcNow.Add(registrationWait);
            while (!sessionVisible(sessionId))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new FileOperationException(
                        "TERMINAL_REGISTRATION_TIMEOUT",
                        "SessionHost did not register with the local Agent in time.");
                }
                await Task.Delay(RemoteAccessPollInterval, token).ConfigureAwait(false);
            }
            if (ManagedTerminalLauncher.IsBuiltInShellType(commandId))
            {
                return JsonSerializer.SerializeToElement(new
                {
                    session_id = sessionId,
                    shell_type = managedShellType,
                    cwd = operation.Path
                });
            }
            return JsonSerializer.SerializeToElement(new
            {
                session_id = sessionId,
                terminal_command_id = commandId,
                shell_type = managedShellType,
                cwd = operation.Path
            });
        }

        internal static Guid StableSessionId(Guid operationId)
        {
            if (operationId == Guid.Empty) throw new ArgumentException("Operation ID is required.", nameof(operationId));
            var scope = Encoding.UTF8.GetBytes("TermLink/file-operation/session/v1");
            var input = new byte[scope.Length + 16];
            Buffer.BlockCopy(scope, 0, input, 0, scope.Length);
            operationId.TryWriteBytes(input.AsSpan(scope.Length));
            var digest = SHA256.HashData(input);
            var bytes = digest.AsSpan(0, 16).ToArray();
            // Mark this as an RFC 4122 variant/version-5-shaped deterministic
            // identifier even though SHA-256 is used for collision resistance.
            bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
            bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
            return new Guid(bytes);
        }

        private async Task MonitorRemoteAccessAsync(
            OperationExecutionState execution,
            CancellationTokenSource operationCancellation,
            CancellationToken token)
        {
            while (true)
            {
                if (!remoteAccessEnabled())
                {
                    execution.TryStop(OperationStopReason.RemoteAccessDisabled);
                    operationCancellation.Cancel();
                    return;
                }
                await Task.Delay(RemoteAccessPollInterval, token).ConfigureAwait(false);
            }
        }

        private async Task MonitorLeaseAsync(
            ClaimedFileOperation operation,
            OperationExecutionState execution,
            CancellationTokenSource operationCancellation,
            CancellationToken token)
        {
            var leaseDeadline = operation.LeaseExpiresAt ?? DateTimeOffset.UtcNow.Add(FallbackLeaseDuration);
            while (true)
            {
                await Task.Delay(leaseRenewInterval, token).ConfigureAwait(false);
                try
                {
                    var renewed = await api.RenewAsync(operation, token).ConfigureAwait(false);
                    leaseDeadline = renewed ?? DateTimeOffset.UtcNow.Add(FallbackLeaseDuration);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (FileOperationLeaseLostException)
                {
                    // A concurrent /complete may already have committed and
                    // merely lost its response. During finalization the stable
                    // idempotent completion replay is authoritative; a renew
                    // 409 alone cannot distinguish that success from cancel.
                    if (execution.TryLoseLease()) operationCancellation.Cancel();
                    return;
                }
                catch (FileOperationRemoteException error) when (!error.Retryable)
                {
                    if (execution.TryLoseLease()) operationCancellation.Cancel();
                    return;
                }
                catch (Exception error) when (
                    (error is FileOperationRemoteException remote && remote.Retryable) ||
                    (error is HttpRequestException && error is not FileOperationRemoteException) ||
                    error is ProductionReauthenticationRequiredException ||
                    error is JsonException)
                {
                    if (DateTimeOffset.UtcNow < leaseDeadline) continue;
                    if (execution.TryLoseLease()) operationCancellation.Cancel();
                    return;
                }
            }
        }

        private async Task ReportRemoteAccessDisabledAsync(
            ClaimedFileOperation operation,
            CancellationToken token)
        {
            using var reporting = CancellationTokenSource.CreateLinkedTokenSource(token);
            reporting.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await api.FailAsync(
                    operation,
                    "REMOTE_ACCESS_DISABLED",
                    "Remote access was disabled before the file operation completed.",
                    reporting.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is OperationCanceledException ||
                error is FileOperationRemoteException ||
                error is HttpRequestException ||
                error is ProductionReauthenticationRequiredException)
            {
            }
        }

        private static async Task IgnoreMonitorCancellationAsync(Task monitor)
        {
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        private static async Task RetryRemoteAsync(
            Func<CancellationToken, Task> action,
            CancellationToken token)
        {
            var delay = TimeSpan.FromMilliseconds(250);
            while (true)
            {
                try
                {
                    await action(token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (FileOperationRemoteException error) when (error.Retryable)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
                }
                catch (HttpRequestException error) when (error is not FileOperationRemoteException)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
                }
                catch (ProductionReauthenticationRequiredException)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 2));
                }
            }
        }

        private static FileOperationException StableFailure(Exception error)
        {
            if (error is FileOperationException operationError) return operationError;
            if (error is FileOperationRemoteException remoteError && !remoteError.Retryable)
                return new FileOperationException(
                    "REMOTE_REQUEST_REJECTED",
                    "The server rejected the file-operation request.");
            if (error is UnauthorizedAccessException)
                return new FileOperationException("ACCESS_DENIED", "Access to the requested Windows path was denied.");
            if (error is DirectoryNotFoundException || error is FileNotFoundException)
                return new FileOperationException("PATH_NOT_FOUND", "The requested Windows path no longer exists.");
            if (error is PathTooLongException || error is ArgumentException || error is NotSupportedException)
                return new FileOperationException("PATH_INVALID", "The requested Windows path is invalid.");
            if (error is IOException)
                return new FileOperationException("WINDOWS_IO_FAILED", "Windows could not complete the requested file operation.");
            return new FileOperationException("OPERATION_FAILED", "The Windows file operation failed.");
        }

        private static readonly JsonSerializerOptions ProductionJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            Task active;
            lock (lifecycleSync) active = worker;
            if (active != null)
            {
                try { active.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (AggregateException) { }
            }
            cancellation.Dispose();
        }

        private enum OperationStopReason
        {
            None,
            RemoteAccessDisabled,
            LeaseLost
        }

        private sealed class OperationExecutionState
        {
            private readonly object sync = new object();
            private int reason;
            private bool finalizing;
            internal OperationStopReason Reason
            {
                get { lock (sync) return (OperationStopReason)reason; }
            }
            internal bool TryBeginFinalization()
            {
                lock (sync)
                {
                    if (reason != (int)OperationStopReason.None) return false;
                    finalizing = true;
                    return true;
                }
            }

            internal bool TryLoseLease()
            {
                lock (sync)
                {
                    if (finalizing || reason != (int)OperationStopReason.None) return false;
                    reason = (int)OperationStopReason.LeaseLost;
                    return true;
                }
            }

            internal bool TryStop(OperationStopReason value)
            {
                lock (sync)
                {
                    if (reason != (int)OperationStopReason.None) return false;
                    reason = (int)value;
                    return true;
                }
            }
        }
    }
}
