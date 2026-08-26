using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TermLink.UpdateCore;

namespace TermLink.Agent
{
    internal sealed class ClaimedAgentUpdate
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }
        [JsonPropertyName("pc_id")]
        public Guid PcId { get; set; }
        [JsonPropertyName("current_version")]
        public string CurrentVersion { get; set; }
        [JsonPropertyName("current_build")]
        public long CurrentBuild { get; set; }
        [JsonPropertyName("target_version")]
        public string TargetVersion { get; set; }
        [JsonPropertyName("target_build")]
        public long TargetBuild { get; set; }
        [JsonPropertyName("artifact_url")]
        public string ArtifactUrl { get; set; }
        [JsonPropertyName("artifact_filename")]
        public string ArtifactFilename { get; set; }
        [JsonPropertyName("artifact_size_bytes")]
        public long ArtifactSizeBytes { get; set; }
        [JsonPropertyName("artifact_sha256")]
        public string ArtifactSha256 { get; set; }
        [JsonPropertyName("artifact_signer_fingerprint")]
        public string ArtifactSignerFingerprint { get; set; }
        [JsonPropertyName("phase")]
        public string Phase { get; set; }
        [JsonPropertyName("lease_expires_at")]
        public DateTimeOffset? LeaseExpiresAt { get; set; }
        [JsonIgnore]
        public string LeaseToken { get; set; }
    }

    internal sealed class ProductionAgentUpdateApi
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly Guid pcId;
        private readonly ProductionFileOperationApi.AuthenticatedSendAsync send;

        internal ProductionAgentUpdateApi(
            Guid pcId,
            ProductionFileOperationApi.AuthenticatedSendAsync send)
        {
            if (pcId == Guid.Empty) throw new ArgumentException("PC ID is required.", nameof(pcId));
            this.pcId = pcId;
            this.send = send ?? throw new ArgumentNullException(nameof(send));
        }

        internal async Task<ClaimedAgentUpdate> ClaimAsync(CancellationToken token)
        {
            using var response = await send(
                HttpMethod.Post,
                BasePath + "/claim",
                Array.Empty<byte>(),
                true,
                Guid.NewGuid().ToString("D"),
                token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            await EnsureSuccessAsync(response, "Agent update claim failed.", token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("agent_update", out var item) ||
                item.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("lease_token", out var lease) ||
                lease.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Agent update claim response is incomplete.");
            var result = item.Deserialize<ClaimedAgentUpdate>(JsonOptions);
            result.LeaseToken = lease.GetString();
            ValidateClaim(result);
            return result;
        }

        internal Task ProgressAsync(
            ClaimedAgentUpdate update,
            string phase,
            int percent,
            int? activeSessionCount,
            CancellationToken token) => SendMutationAsync(
                update,
                "progress",
                new
                {
                    lease_token = update.LeaseToken,
                    phase,
                    progress_percent = percent,
                    active_session_count = activeSessionCount
                },
                phase + "-" + percent,
                token);

        internal Task RenewAsync(ClaimedAgentUpdate update, CancellationToken token) =>
            SendMutationAsync(
                update,
                "renew",
                new { lease_token = update.LeaseToken },
                Guid.NewGuid().ToString("N"),
                token);

        internal Task CompleteAsync(
            ClaimedAgentUpdate update,
            string status,
            string errorCode,
            string errorDetail,
            string observedVersion,
            long? observedBuild,
            CancellationToken token) => SendMutationAsync(
                update,
                "complete",
                new
                {
                    lease_token = update.LeaseToken,
                    status,
                    error_code = errorCode,
                    error_detail = errorDetail,
                    observed_agent_version = observedVersion,
                    observed_agent_build = observedBuild
                },
                status + "-" + (observedBuild?.ToString() ?? "none"),
                token);

        private async Task SendMutationAsync(
            ClaimedAgentUpdate update,
            string operation,
            object value,
            string idempotencyDiscriminator,
            CancellationToken token)
        {
            if (update == null || update.Id == Guid.Empty || string.IsNullOrWhiteSpace(update.LeaseToken))
                throw new InvalidDataException("The Agent update lease is missing.");
            var body = JsonSerializer.SerializeToUtf8Bytes(value);
            if (string.IsNullOrWhiteSpace(idempotencyDiscriminator))
                throw new ArgumentException("The Agent update idempotency discriminator is required.", nameof(idempotencyDiscriminator));
            var idempotencyKey = "agent-update-" + update.Id.ToString("N") + "-" + operation + "-" +
                idempotencyDiscriminator;
            using var response = await send(
                HttpMethod.Post,
                BasePath + "/" + update.Id.ToString("D") + "/" + operation,
                body,
                true,
                idempotencyKey,
                token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "Agent update " + operation + " failed.", token).ConfigureAwait(false);
        }

        private static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            string message,
            CancellationToken token)
        {
            if (response.IsSuccessStatusCode) return;
            var detail = string.Empty;
            try
            {
                var body = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (body.Length <= 4096)
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("error", out var error) &&
                        error.TryGetProperty("code", out var code)) detail = code.GetString();
                }
            }
            catch (JsonException) { }
            throw new HttpRequestException(
                message + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail),
                null,
                response.StatusCode);
        }

        private void ValidateClaim(ClaimedAgentUpdate update)
        {
            if (update == null || update.Id == Guid.Empty || update.PcId != pcId ||
                string.IsNullOrWhiteSpace(update.CurrentVersion) ||
                update.CurrentBuild < 0 || string.IsNullOrWhiteSpace(update.TargetVersion) ||
                update.TargetBuild <= update.CurrentBuild ||
                string.IsNullOrWhiteSpace(update.ArtifactUrl) || update.ArtifactSizeBytes < 1 ||
                !WindowsUpdateSecurity.IsHex(
                    WindowsUpdateSecurity.NormalizeHex(update.ArtifactSha256), 64) ||
                string.IsNullOrWhiteSpace(update.LeaseToken))
                throw new InvalidDataException("The claimed Agent update contract is invalid.");
        }

        private string BasePath => "/v1/pcs/" + pcId.ToString("D") + "/agent-updates";
    }

    internal sealed class ProductionAgentUpdateWorker : IDisposable
    {
        private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RelayVerificationTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan SessionDrainTimeout = TimeSpan.FromSeconds(30);
        private readonly ProductionAgentUpdateApi api;
        private readonly HttpClient downloadClient;
        private readonly Guid pcId;
        private readonly string agentVersion;
        private readonly long agentBuild;
        private readonly Func<IList<Guid>> activeSessionIds;
        private readonly Func<Guid, bool> terminateSession;
        private readonly Func<bool> relayConnected;
        private readonly HashSet<string> launchedSupervisors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource cancellation;
        private Task worker;
        private int disposed;

        internal ProductionAgentUpdateWorker(
            ProductionAgentUpdateApi api,
            Guid pcId,
            string agentVersion,
            long agentBuild,
            CancellationToken parentToken,
            Func<IList<Guid>> activeSessionIds,
            Func<Guid, bool> terminateSession,
            Func<bool> relayConnected)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.pcId = pcId;
            this.agentVersion = agentVersion ?? throw new ArgumentNullException(nameof(agentVersion));
            if (agentBuild < 0) throw new ArgumentOutOfRangeException(nameof(agentBuild));
            this.agentBuild = agentBuild;
            this.activeSessionIds = activeSessionIds ?? throw new ArgumentNullException(nameof(activeSessionIds));
            this.terminateSession = terminateSession ?? throw new ArgumentNullException(nameof(terminateSession));
            this.relayConnected = relayConnected ?? throw new ArgumentNullException(nameof(relayConnected));
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            downloadClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        internal void Start()
        {
            if (Interlocked.CompareExchange(ref worker, Task.CompletedTask, null) != null)
                throw new InvalidOperationException("Agent update worker is already started.");
            worker = Task.Run(() => RunAsync(cancellation.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            var delay = TimeSpan.FromSeconds(1);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var recovery = await ResumeInstalledTransactionAsync(token).ConfigureAwait(false);
                    if (recovery.Handled)
                    {
                        delay = TimeSpan.FromSeconds(1);
                        await Task.Delay(IdlePollInterval, token).ConfigureAwait(false);
                        continue;
                    }
                    var update = recovery.Pending ?? await api.ClaimAsync(token).ConfigureAwait(false);
                    if (update == null)
                    {
                        delay = TimeSpan.FromSeconds(1);
                        await Task.Delay(IdlePollInterval, token).ConfigureAwait(false);
                        continue;
                    }
                    await ProcessAsync(update, token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(1);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(20, delay.TotalSeconds * 2));
                }
            }
        }

        private async Task ProcessAsync(ClaimedAgentUpdate update, CancellationToken token)
        {
            using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var monitor = RenewLeaseAsync(update, monitorCancellation.Token);
            var phase = "PRECHECKING";
            string journalPath = null;
            try
            {
                await api.ProgressAsync(update, phase, 2, activeSessionIds().Count, token).ConfigureAwait(false);
                EnsureNewerTarget(update, agentBuild);
                phase = "DOWNLOADING";
                await api.ProgressAsync(update, phase, 5, null, token).ConfigureAwait(false);
                var transactionRoot = RemoteUpdateTransactionStore.CreateTransactionRoot(update.Id);
                var setupPath = Path.Combine(transactionRoot, "TermLink-Setup.exe");
                await DownloadAsync(update, setupPath, token).ConfigureAwait(false);
                phase = "VERIFIED";
                await api.ProgressAsync(update, phase, 70, null, token).ConfigureAwait(false);
                var expectedSigner = WindowsUpdateSecurity.ResolveExpectedSignerThumbprint(AppContext.BaseDirectory);
                WindowsUpdateSecurity.ValidateIntegrity(
                    setupPath,
                    update.ArtifactSizeBytes,
                    update.ArtifactSha256);
                WindowsUpdateSecurity.ValidateAuthenticode(
                    setupPath,
                    expectedSigner,
                    update.ArtifactSignerFingerprint);
                StageExternalUpdater(transactionRoot);

                phase = "DRAINING_SESSIONS";
                var sessions = activeSessionIds();
                await api.ProgressAsync(update, phase, 75, sessions.Count, token).ConfigureAwait(false);
                foreach (var sessionId in sessions) terminateSession(sessionId);
                await WaitForSessionDrainAsync(token).ConfigureAwait(false);
                await api.ProgressAsync(update, phase, 82, 0, token).ConfigureAwait(false);

                var signer = string.IsNullOrWhiteSpace(update.ArtifactSignerFingerprint)
                    ? expectedSigner
                    : WindowsUpdateSecurity.NormalizeHex(update.ArtifactSignerFingerprint);
                var transaction = new RemoteUpdateTransactionRecord
                {
                    UpdateId = update.Id,
                    PcId = pcId,
                    State = RemoteUpdateTransactionState.Prepared,
                    CurrentVersion = update.CurrentVersion,
                    CurrentBuild = update.CurrentBuild,
                    TargetVersion = update.TargetVersion,
                    TargetBuild = update.TargetBuild,
                    SetupPath = setupPath,
                    ArtifactSizeBytes = update.ArtifactSizeBytes,
                    ArtifactSha256 = WindowsUpdateSecurity.NormalizeHex(update.ArtifactSha256),
                    InstallRoot = Path.GetFullPath(AppContext.BaseDirectory),
                    ExpectedSignerFingerprint = signer,
                    PreviousProcessId = Environment.ProcessId,
                    HealthDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(3)
                };
                journalPath = RemoteUpdateTransactionStore.JournalPath(transactionRoot);
                RemoteUpdateTransactionStore.Save(journalPath, transaction);
                await api.ProgressAsync(update, "INSTALLING", 85, 0, token).ConfigureAwait(false);
                phase = "INSTALLING";
                LaunchExternalUpdater(transactionRoot, journalPath, "remote-execute");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                if (journalPath != null && File.Exists(journalPath))
                {
                    var transaction = RemoteUpdateTransactionStore.Load(journalPath);
                    transaction.State = RemoteUpdateTransactionState.RolledBack;
                    transaction.LastErrorCode = StableErrorCode(error);
                    transaction.LastErrorDetail = SafeDetail(error);
                    RemoteUpdateTransactionStore.Save(journalPath, transaction);
                }
                if (phase == "INSTALLING")
                {
                    await api.ProgressAsync(update, "ROLLING_BACK", 90, 0, token).ConfigureAwait(false);
                    await api.CompleteAsync(
                        update,
                        "ROLLED_BACK",
                        StableErrorCode(error),
                        SafeDetail(error),
                        agentVersion,
                        agentBuild,
                        token).ConfigureAwait(false);
                }
                else
                {
                    await api.CompleteAsync(
                        update,
                        "FAILED",
                        StableErrorCode(error),
                        SafeDetail(error),
                        null,
                        null,
                        token).ConfigureAwait(false);
                }
            }
            finally
            {
                monitorCancellation.Cancel();
                try { await monitor.ConfigureAwait(false); }
                catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested) { }
            }
        }

        private async Task<(bool Handled, ClaimedAgentUpdate Pending)> ResumeInstalledTransactionAsync(
            CancellationToken token)
        {
            var journalPath = RemoteUpdateTransactionStore.FindActiveJournal(pcId);
            if (journalPath == null) return (false, null);
            var transaction = RemoteUpdateTransactionStore.Load(journalPath);
            var targetActive = transaction.State == RemoteUpdateTransactionState.AwaitingNewAgent &&
                agentBuild == transaction.TargetBuild;
            var rollbackActive = (transaction.State == RemoteUpdateTransactionState.RollingBack ||
                transaction.State == RemoteUpdateTransactionState.AwaitingRollbackAgent) &&
                agentBuild == transaction.CurrentBuild;
            var supervisorRecovery = RequiresSupervisorRecovery(transaction, agentBuild);
            var rollbackFailure = transaction.State == RemoteUpdateTransactionState.RollbackFailed &&
                !transaction.FailureReported &&
                (agentBuild == transaction.CurrentBuild || agentBuild == transaction.TargetBuild);
            if (!targetActive && !rollbackActive && !supervisorRecovery && !rollbackFailure)
                return (false, null);

            var update = await api.ClaimAsync(token).ConfigureAwait(false);
            if (update == null)
            {
                if (rollbackFailure)
                {
                    transaction.FailureReported = true;
                    RemoteUpdateTransactionStore.Save(journalPath, transaction);
                }
                return (false, null);
            }
            // A stale local journal must never consume and strand the lease for
            // a newer Server job. Hand the already-claimed job to the normal
            // processing path instead.
            if (update.Id != transaction.UpdateId) return (false, update);
            if (supervisorRecovery)
            {
                LaunchExternalUpdater(
                    Path.GetDirectoryName(journalPath),
                    journalPath,
                    "remote-resume");
                return (true, null);
            }
            if (rollbackFailure)
            {
                if (update.Phase != "ROLLING_BACK")
                    await api.ProgressAsync(update, "ROLLING_BACK", 99, 0, token).ConfigureAwait(false);
                await api.CompleteAsync(
                    update,
                    "ROLLBACK_FAILED",
                    string.IsNullOrWhiteSpace(transaction.LastErrorCode)
                        ? "ROLLBACK_FAILED"
                        : transaction.LastErrorCode,
                    string.IsNullOrWhiteSpace(transaction.LastErrorDetail)
                        ? "The previous Agent could not be restored automatically."
                        : transaction.LastErrorDetail,
                    agentVersion,
                    agentBuild,
                    token).ConfigureAwait(false);
                transaction.FailureReported = true;
                RemoteUpdateTransactionStore.Save(journalPath, transaction);
                return (true, null);
            }
            if (targetActive)
            {
                if (update.Phase == "INSTALLING")
                    await api.ProgressAsync(update, "STARTING_NEW_AGENT", 92, 0, token).ConfigureAwait(false);
                if (update.Phase == "INSTALLING" || update.Phase == "STARTING_NEW_AGENT")
                    await api.ProgressAsync(update, "VERIFYING_CONNECTION", 96, 0, token).ConfigureAwait(false);
                await WaitForRelayAsync(token).ConfigureAwait(false);
                await api.CompleteAsync(
                    update, "SUCCEEDED", null, null, agentVersion, agentBuild, token).ConfigureAwait(false);
                RemoteUpdateTransactionStore.SaveReceipt(
                    Path.GetDirectoryName(journalPath),
                    new RemoteUpdateReceipt
                    {
                        UpdateId = update.Id,
                        Outcome = "SUCCEEDED",
                        ObservedAgentVersion = agentVersion,
                        ObservedAgentBuild = agentBuild
                    });
            }
            else
            {
                if (update.Phase != "ROLLING_BACK")
                    await api.ProgressAsync(update, "ROLLING_BACK", 95, 0, token).ConfigureAwait(false);
                await WaitForRelayAsync(token).ConfigureAwait(false);
                await api.CompleteAsync(
                    update,
                    "ROLLED_BACK",
                    "AUTOMATIC_ROLLBACK",
                    "The previous Agent was restored after the target failed verification.",
                    agentVersion,
                    agentBuild,
                    token).ConfigureAwait(false);
                RemoteUpdateTransactionStore.SaveReceipt(
                    Path.GetDirectoryName(journalPath),
                    new RemoteUpdateReceipt
                    {
                        UpdateId = update.Id,
                        Outcome = "ROLLED_BACK",
                        ObservedAgentVersion = agentVersion,
                        ObservedAgentBuild = agentBuild
                    });
            }
            return (true, null);
        }

        internal static bool RequiresSupervisorRecovery(
            RemoteUpdateTransactionRecord transaction,
            long executingAgentBuild)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (executingAgentBuild < 0) throw new ArgumentOutOfRangeException(nameof(executingAgentBuild));
            if (executingAgentBuild == transaction.CurrentBuild)
            {
                return transaction.State == RemoteUpdateTransactionState.Prepared ||
                    transaction.State == RemoteUpdateTransactionState.SetupRunning ||
                    transaction.State == RemoteUpdateTransactionState.AwaitingNewAgent;
            }
            if (executingAgentBuild == transaction.TargetBuild)
            {
                return transaction.State == RemoteUpdateTransactionState.RollingBack ||
                    transaction.State == RemoteUpdateTransactionState.AwaitingRollbackAgent;
            }
            return false;
        }

        private async Task DownloadAsync(
            ClaimedAgentUpdate update,
            string destination,
            CancellationToken token)
        {
            var current = ValidateHttps(update.ArtifactUrl);
            HttpResponseMessage response = null;
            for (var redirects = 0; ; redirects++)
            {
                response = await downloadClient.GetAsync(
                    current,
                    HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode)) break;
                if (redirects >= 3 || response.Headers.Location == null)
                {
                    response.Dispose();
                    throw new HttpRequestException("The update download exceeded the HTTPS redirect limit.");
                }
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                response.Dispose();
                current = ValidateHttps(next.AbsoluteUri);
            }
            using (response)
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value != update.ArtifactSizeBytes)
                    throw new InvalidDataException("The update Content-Length does not match the fixed artifact size.");
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await WriteVerifiedDownloadAsync(
                    input,
                    destination,
                    update.ArtifactSizeBytes,
                    update.ArtifactSha256,
                    async percent => await api.ProgressAsync(
                        update,
                        "DOWNLOADING",
                        percent,
                        null,
                        token).ConfigureAwait(false),
                    token).ConfigureAwait(false);
            }
        }

        internal static async Task WriteVerifiedDownloadAsync(
            Stream input,
            string destination,
            long expectedSize,
            string expectedSha256,
            Func<int, Task> reportProgress,
            CancellationToken token)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrWhiteSpace(destination))
                throw new ArgumentException("A download destination is required.", nameof(destination));
            if (expectedSize < 1) throw new ArgumentOutOfRangeException(nameof(expectedSize));
            if (string.IsNullOrWhiteSpace(expectedSha256))
                throw new ArgumentException("An artifact digest is required.", nameof(expectedSha256));
            if (reportProgress == null) throw new ArgumentNullException(nameof(reportProgress));
            var temporary = Path.GetFullPath(destination) + ".download";
            try
            {
                long received;
                byte[] actualHash;
                using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[128 * 1024];
                    received = 0;
                    var lastPercent = 5;
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        received = checked(received + count);
                        if (received > expectedSize)
                            throw new InvalidDataException("The update download exceeded the fixed artifact size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, count);
                        var percent = 5 + (int)(received * 60 / expectedSize);
                        if (percent >= lastPercent + 5)
                        {
                            lastPercent = percent;
                            await reportProgress(percent).ConfigureAwait(false);
                        }
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    actualHash = hash.GetHashAndReset();
                }
                if (received != expectedSize ||
                    !CryptographicOperations.FixedTimeEquals(
                        actualHash,
                        Convert.FromHexString(expectedSha256)))
                    throw new CryptographicException("The downloaded Setup does not match the fixed artifact digest.");

                // FileShare.None deliberately protects the partial artifact.
                // Close that handle before promoting the verified file on Windows.
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private async Task RenewLeaseAsync(ClaimedAgentUpdate update, CancellationToken token)
        {
            while (true)
            {
                await Task.Delay(LeaseRenewInterval, token).ConfigureAwait(false);
                await api.RenewAsync(update, token).ConfigureAwait(false);
            }
        }

        private async Task WaitForSessionDrainAsync(CancellationToken token)
        {
            var deadline = DateTime.UtcNow + SessionDrainTimeout;
            while (activeSessionIds().Count != 0 && DateTime.UtcNow < deadline)
                await Task.Delay(250, token).ConfigureAwait(false);
            if (activeSessionIds().Count != 0)
                throw new InvalidOperationException("One or more managed terminal sessions could not be stopped.");
        }

        private async Task WaitForRelayAsync(CancellationToken token)
        {
            var deadline = DateTime.UtcNow + RelayVerificationTimeout;
            while (!relayConnected() && DateTime.UtcNow < deadline)
                await Task.Delay(500, token).ConfigureAwait(false);
            if (!relayConnected()) throw new TimeoutException("The Agent did not reconnect to Relay in time.");
        }

        internal static void EnsureNewerTarget(ClaimedAgentUpdate update, long executingAgentBuild)
        {
            if (update.CurrentBuild != executingAgentBuild || update.TargetBuild <= update.CurrentBuild)
                throw new InvalidDataException("The Agent update target is not newer than the installed version.");
        }

        private static void StageExternalUpdater(string transactionRoot)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "TermLink.Updater.exe");
            if (!File.Exists(source)) throw new FileNotFoundException("The installed external Updater is missing.", source);
            File.Copy(source, Path.Combine(transactionRoot, "TermLink.Updater.exe"), true);
        }

        private void LaunchExternalUpdater(string transactionRoot, string journalPath, string command)
        {
            journalPath = Path.GetFullPath(journalPath);
            if (!launchedSupervisors.Add(journalPath)) return;
            var path = Path.Combine(transactionRoot, "TermLink.Updater.exe");
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = transactionRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add(command);
                start.ArgumentList.Add("--transaction");
                start.ArgumentList.Add(journalPath);
                using var process = Process.Start(start);
                if (process == null)
                    throw new InvalidOperationException("The external remote update supervisor could not start.");
            }
            catch
            {
                launchedSupervisors.Remove(journalPath);
                throw;
            }
        }

        private static Uri ValidateHttps(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("The update download URL must be HTTPS without embedded credentials.");
            return uri;
        }

        private static bool IsRedirect(HttpStatusCode status) =>
            status == HttpStatusCode.Moved || status == HttpStatusCode.Redirect ||
            status == HttpStatusCode.RedirectMethod || status == HttpStatusCode.TemporaryRedirect ||
            (int)status == 308;

        private static string StableErrorCode(Exception error)
        {
            if (error is CryptographicException) return "UPDATE_VERIFICATION_FAILED";
            if (error is UnauthorizedAccessException) return "UPDATE_PERMISSION_DENIED";
            if (error is HttpRequestException) return "UPDATE_DOWNLOAD_FAILED";
            if (error is TimeoutException) return "UPDATE_TIMEOUT";
            if (error is IOException) return "UPDATE_IO_FAILED";
            return "UPDATE_PRECHECK_FAILED";
        }

        private static string SafeDetail(Exception error)
        {
            var value = error.GetType().Name + ": " + error.Message;
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 1000 ? value : value.Substring(0, 1000);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            try { worker?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            cancellation.Dispose();
            downloadClient.Dispose();
        }
    }
}
