using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TermLink.Agent;
using TermLink.Protocol;

namespace TermLink.Agent.Connection;

public sealed partial class RelayConnectionService
{
    private static readonly JsonSerializerOptions FileCompletionJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly Func<string, IRelayFileHost>? fileHostFactory;
    private IRelayFileHost? fileHost;
    private long fileAuthorityGeneration;
    private FileWorkerLifetime? fileWorker;

    // A file lease belongs to the signed-in account, not to one control socket.
    // Own the identity until every claimed operation has stopped; the control
    // worker disposes and recreates its identity on each Relay reconnect.
    private sealed class FileWorkerLifetime(Account account, IRelayFileHost host) : IDisposable
    {
        public Account Account { get; } = account;
        public IRelayFileHost Host { get; } = host;
        public DeviceIdentity Device { get; } = OpenIdentity(account.Identity);
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public void Dispose() { Device.Dispose(); Cancellation.Dispose(); }
    }

    private async Task StartFileWorkerAsync(Account current)
    {
        if (fileHost is null || fileWorker is { Completion.IsCompleted: false }) return;
        if (fileWorker is { } completed)
        {
            fileWorker = null;
            try { await completed.Completion.ConfigureAwait(false); }
            catch (OperationCanceledException) when (completed.Cancellation.IsCancellationRequested) { }
            finally { completed.Dispose(); }
        }
        var owner = new FileWorkerLifetime(current, fileHost);
        fileWorker = owner;
        owner.Completion = RunFileOperationsAsync(owner);
    }

    private void CancelFileWorker()
    {
        Interlocked.Increment(ref fileAuthorityGeneration);
        fileWorker?.Cancellation.Cancel();
    }

    private async Task StopFileWorkerAsync()
    {
        CancelFileWorker();
        var retired = fileWorker;
        fileWorker = null;
        if (retired is null) return;
        try { await retired.Completion.ConfigureAwait(false); }
        catch (Exception) when (retired.Cancellation.IsCancellationRequested) { }
        finally { retired.Dispose(); }
    }

    private async Task<Account> CurrentFileAccountAsync(FileWorkerLifetime owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireOwner();
        // Refresh is serialized independently from UI operations. Sign-out can
        // cancel and drain this worker while holding the UI operations gate.
        await RefreshIfNeededAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return RequireOwner();

        Account RequireOwner()
        {
            var current = account;
            if (current is null || !current.RemoteAccessEnabled || !ReferenceEquals(fileHost, owner.Host) ||
                current.Origin != owner.Account.Origin || current.Issuer != owner.Account.Issuer ||
                current.Subject != owner.Account.Subject || current.ClientId != owner.Account.ClientId ||
                current.Identity != owner.Account.Identity)
                throw new OperationCanceledException("The file account is no longer active.", token);
            return current;
        }
    }

    private async Task<HttpResponseMessage> SendFileAsync(FileWorkerLifetime owner, HttpMethod method, string path,
        byte[] body, CancellationToken token, string? idempotencyKey = null)
    {
        var current = await CurrentFileAccountAsync(owner, token).ConfigureAwait(false);
        return await SendAsync(current, owner.Device, method, path, body, true, token, idempotencyKey).ConfigureAwait(false);
    }

    private async Task<Dictionary<Guid, AiRecipient>> ReadFileRecipientsAsync(FileWorkerLifetime owner, CancellationToken token)
    {
        var current = await CurrentFileAccountAsync(owner, token).ConfigureAwait(false);
        return await ReadAiRecipientsAsync(current, owner.Device, token).ConfigureAwait(false);
    }

    public bool SupportsFiles => fileHostFactory is not null;
    private async Task RetireFileHostAsync()
    {
        var retired = fileHost; fileHost = null;
        if (retired is not null) await retired.DisposeAsync().ConfigureAwait(false);
    }
    private async Task RunFileOperationsAsync(FileWorkerLifetime owner)
    {
        var token = owner.Cancellation.Token;
        var prefix = "/v1/pcs/" + owner.Account.Identity.PcId.ToString("D") + "/file-operations";
        var jobs = new Dictionary<Guid, Task>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancellation = lifetime.Token;
        var claims = new ProductionClaimIdempotencyTracker();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                foreach (var id in jobs.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray())
                { await jobs[id].ConfigureAwait(false); jobs.Remove(id); }
                if (jobs.Count >= 4) { await Task.Delay(100, cancellation).ConfigureAwait(false); continue; }
                var claimKey = claims.BeginAttempt();
                try
                {
                    using var claimed = await SendFileAsync(owner, HttpMethod.Post, prefix + "/claim", [], cancellation, claimKey).ConfigureAwait(false);
                    if (claimed.StatusCode == HttpStatusCode.Unauthorized) RelayHttp.RequireSuccess(claimed);
                    if (claimed.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    { claims.ObserveDefinitiveResponse(claimKey); await Task.Delay(750, cancellation).ConfigureAwait(false); continue; }
                    // Keep an ambiguous request's idempotency key until its body
                    // is fully read; otherwise a lease can be stranded.
                    if (!claimed.IsSuccessStatusCode)
                    {
                        var inProgress = false;
                        if (claimed.StatusCode == HttpStatusCode.Conflict)
                        {
                            try
                            {
                                using var failure = await RelayHttp.ReadJsonAsync(claimed, cancellation).ConfigureAwait(false);
                                inProgress = failure.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) && code.GetString() == "IDEMPOTENCY_IN_PROGRESS";
                            }
                            catch (Exception e) when (e is JsonException or IOException or RelayOperationException) { }
                        }
                        if (inProgress) claims.ObserveInProgress(claimKey); else claims.ObserveDefinitiveResponse(claimKey);
                        await Task.Delay(1000, cancellation).ConfigureAwait(false); continue;
                    }
                    using var json = await RelayHttp.ReadJsonAsync(claimed, cancellation).ConfigureAwait(false);
                    var operation = json.RootElement.GetProperty("file_operation").Clone();
                    var lease = json.RootElement.GetProperty("lease_token").GetString();
                    var id = operation.GetProperty("id").GetGuid();
                    if (id == Guid.Empty || lease is null || lease.Length is < 32 or > 256) throw new RelayFileException("OPERATION_INVALID");
                    claims.ObserveDefinitiveResponse(claimKey);
                    if (!jobs.ContainsKey(id)) jobs[id] = ProcessFileOperationAsync(owner, prefix, operation, lease, cancellation);
                }
                catch (Exception e) when (!IsAccountAuthorityFailure(e) &&
                    e is (HttpRequestException or IOException or JsonException or OperationCanceledException or RelayOperationException or ArgumentException))
                { cancellation.ThrowIfCancellationRequested(); await Task.Delay(1000, cancellation).ConfigureAwait(false); }
            }
        }
        finally
        {
            // Every exit here ends account ownership; a control-socket reconnect
            // never enters this finally. Also discard artifacts after a fatal
            // claim/refresh error before the control worker observes that error.
            Interlocked.Increment(ref fileAuthorityGeneration);
            lifetime.Cancel();
            try { await Task.WhenAll(jobs.Values).ConfigureAwait(false); } catch (Exception) when (cancellation.IsCancellationRequested) { }
        }
    }

    private async Task ProcessFileOperationAsync(FileWorkerLifetime owner, string prefix,
        JsonElement operation, string lease, CancellationToken token)
    {
        var host = owner.Host;
        var device = owner.Device;
        var authorityGeneration = Interlocked.Read(ref fileAuthorityGeneration);
        var id = operation.GetProperty("id").GetGuid();
        var requester = operation.GetProperty("requester_device_id").GetGuid();
        var kind = operation.GetProperty("operation").GetString() ?? "";
        var path = operation.GetProperty("path").GetString() ?? "";
        var target = prefix + "/" + id.ToString("D");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromHours(24));
        var cancellation = lifetime.Token;
        var monitor = Task.CompletedTask;
        Exception? authorityFailure = null;
        bool definitive = false;
        Guid? createdTerminal = null;
        try
        {
            await VerifyAsync(cancellation).ConfigureAwait(false);
            monitor = MonitorAsync();
            object? result;
            switch (kind)
            {
                case "LIST":
                    var offset = operation.TryGetProperty("offset", out var offsetValue) ? offsetValue.GetInt64() : 0;
                    var query = operation.TryGetProperty("query", out var queryValue) ? queryValue.GetString() ?? "" : "";
                    result = await host.ListAsync(path, offset, query, cancellation).ConfigureAwait(false);
                    break;
                case "DOWNLOAD":
                    await using (var artifact = await host.PrepareAsync(id, path, cancellation).ConfigureAwait(false))
                    {
                        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { lease_token = lease, filename = artifact.Filename,
                            size_bytes = artifact.SizeBytes, sha256 = RelaySecurity.Base64Url(artifact.Sha256), directory_zip = artifact.DirectoryZip });
                        using var allocated = await SendFileAsync(owner, HttpMethod.Post, target + "/upload", metadata, cancellation, "mac-file-upload-" + id.ToString("N") + "-" + LeaseHash(lease)).ConfigureAwait(false);
                        RequireOperation(allocated);
                        using var json = await RelayHttp.ReadJsonAsync(allocated, cancellation).ConfigureAwait(false);
                        var transfer = json.RootElement.GetProperty("transfer");
                        if (transfer.GetProperty("protocol").GetString() != "REALTIME_V2" || transfer.GetProperty("transfer_id").GetGuid() != id ||
                            transfer.GetProperty("requester_device_id").GetGuid() != requester) throw new RelayFileException("TRANSFER_INVALID");
                        var sender = new ProductionFileTransferSender(owner.Account.Origin, owner.Account.Identity.DeviceId,
                            async (options, route, ct) =>
                            {
                                var current = await CurrentFileAccountAsync(owner, ct).ConfigureAwait(false);
                                foreach (var proof in RelaySecurity.Proof(HttpMethod.Get, route, [], current.Identity.DeviceId, device, current.AccessToken)) options.SetRequestHeader(proof.Key, proof.Value);
                            }, async (peer, ct) =>
                            {
                                var recipients = await ReadFileRecipientsAsync(owner, ct).ConfigureAwait(false);
                                if (peer != requester || !recipients.TryGetValue(peer, out var recipient) || recipient.Permission != "MANAGE") throw new FileOperationRemoteException("FILE_AUTHORITY_CHANGED", false);
                                var key = SessionCrypto.DerivePairwiseKey(device.Exchange, recipient.ExchangeKey, owner.Account.Identity.DeviceId, peer, recipient.Epoch);
                                try { return new FileTransferPeerKeyMaterial(peer, recipient.Epoch, key); }
                                finally { CryptographicOperations.ZeroMemory(key); }
                            });
                        await sender.SendAsync(new FileOperationUploadTarget { TransferId = id, RequesterDeviceId = requester, Filename = artifact.Filename,
                            SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256, DirectoryZip = artifact.DirectoryZip }, artifact.Content, cancellation).ConfigureAwait(false);
                    }
                    result = null;
                    break;
                case "OPEN_TERMINAL":
                    if (terminalHost is null) throw new RelayFileException("TERMINAL_UNAVAILABLE");
                    var shell = operation.GetProperty("terminal_command_id").GetString() ?? "";
                    createdTerminal = await terminalHost.StartAsync(shell, path, id, cancellation).ConfigureAwait(false);
                    await PublishTerminalSessionsAsync(await CurrentFileAccountAsync(owner, cancellation).ConfigureAwait(false), device, cancellation).ConfigureAwait(false);
                    result = new { session_id = createdTerminal, terminal_command_id = shell, shell_type = shell, cwd = path };
                    break;
                default: throw new RelayFileException("OPERATION_UNSUPPORTED");
            }
            await VerifyAsync(cancellation).ConfigureAwait(false);
            await CompleteAsync(result, null, cancellation).ConfigureAwait(false);
            definitive = true;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException or CryptographicException or UnauthorizedAccessException or RelayOperationException or ArgumentException)
        {
            if (IsAccountAuthorityFailure(e)) authorityFailure = e;
            if (!IsAccountAuthorityFailure(e) && !cancellation.IsCancellationRequested)
            {
                var code = e is RelayFileException file ? file.Code : e is UnauthorizedAccessException ? "ACCESS_DENIED" : "FILE_OPERATION_FAILED";
                try { await VerifyAsync(cancellation).ConfigureAwait(false); await CompleteAsync(null, code, cancellation).ConfigureAwait(false); definitive = true; }
                catch (Exception) { }
            }
            if (createdTerminal is { } terminal && terminalHost is { } terminals)
                await terminals.StopAsync(terminal, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
            // The same immutable artifact stays open across transport retries.
            // Ending account ownership or losing authority discards it.
            if (kind == "DOWNLOAD" && (definitive || authorityFailure is not null || !token.IsCancellationRequested || authorityGeneration != Interlocked.Read(ref fileAuthorityGeneration)))
                try { await host.CleanupAsync(id, CancellationToken.None).ConfigureAwait(false); } catch (IOException) { }
            if (authorityFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(authorityFailure).Throw();
        }

        async Task VerifyAsync(CancellationToken ct)
        {
            var recipients = await ReadFileRecipientsAsync(owner, ct).ConfigureAwait(false);
            if (!recipients.TryGetValue(requester, out var authority) || authority.Permission != "MANAGE") throw new RelayFileException("MANAGE_NOT_GRANTED");
            using var renewed = await SendFileAsync(owner, HttpMethod.Post, target + "/renew", JsonSerializer.SerializeToUtf8Bytes(new { lease_token = lease }), ct).ConfigureAwait(false);
            RequireOperation(renewed);
        }
        async Task MonitorAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false)) await VerifyAsync(cancellation).ConfigureAwait(false);
            }
            catch (Exception error) { if (IsAccountAuthorityFailure(error)) authorityFailure = error; lifetime.Cancel(); }
        }
        async Task CompleteAsync(object? result, string? error, CancellationToken ct)
        {
            // The server distinguishes an omitted result from JSON null:
            // DOWNLOAD success and every failure must omit result entirely.
            var body = JsonSerializer.SerializeToUtf8Bytes(new { lease_token = lease, status = error is null ? "SUCCEEDED" : "FAILED", result, error }, FileCompletionJson);
            var key = "mac-file-complete-" + id.ToString("N") + "-" + LeaseHash(lease) + "-" + (error is null ? "ok" : "failed");
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var completed = await SendFileAsync(owner, HttpMethod.Post, target + "/complete", body, ct, key).ConfigureAwait(false);
                    RequireOperation(completed); return;
                }
                catch (Exception e) when (attempt < 4 && (e is HttpRequestException or IOException or OperationCanceledException) && !ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << attempt)), ct).ConfigureAwait(false); }
            }
        }
    }
    private static string LeaseHash(string lease) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lease)))[..16].ToLowerInvariant();
    private static void RequireOperation(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new RelayOperationException("ACCOUNT_REAUTHENTICATION_REQUIRED");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone)
            throw new RelayFileException("OPERATION_LEASE_LOST");
        throw new HttpRequestException("FILE_OPERATION_HTTP_" + (int)response.StatusCode);
    }
}
