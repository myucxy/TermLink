using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Protocol;
using TermLink.Agent;

namespace TermLink.ProductionPcHeartbeat.Validation;

internal static class Program
{
    private sealed record CapturedRequest(
        HttpMethod Method,
        string Path,
        byte[] Body,
        string IdempotencyKey);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal readonly List<CapturedRequest> Requests = new();
        internal bool BlockNextPc;
        internal Exception FailNextPc;
        internal bool FailRecipientRefresh;
        internal int FailSessionResponses;
        internal bool RejectNextPCAgentRegistration;
        internal bool RejectNextGenericPCUpsert;
        internal TaskCompletionSource<bool> Entered = NewSignal();
        internal TaskCompletionSource<bool> Release = NewSignal();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var key = request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? values.Single()
                : null;
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri.AbsolutePath,
                (byte[])body.Clone(),
                key));

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/v1/devices/register" &&
                RejectNextPCAgentRegistration)
            {
                using var registration = JsonDocument.Parse(body);
                if (registration.RootElement.GetProperty("type").GetString() == "PC_AGENT")
                {
                    RejectNextPCAgentRegistration = false;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new ByteArrayContent("{\"error\":{\"code\":\"INVALID_DEVICE_TYPE\"}}"u8.ToArray())
                    };
                }
            }

            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath.StartsWith("/v1/pcs/", StringComparison.Ordinal) &&
                RejectNextGenericPCUpsert)
            {
                using var metadata = JsonDocument.Parse(body);
                if (metadata.RootElement.TryGetProperty("platform", out _))
                {
                    RejectNextGenericPCUpsert = false;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new ByteArrayContent("{\"error\":{\"code\":\"INVALID_REQUEST\"}}"u8.ToArray())
                    };
                }
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri.AbsolutePath.EndsWith("/grants", StringComparison.Ordinal) &&
                FailRecipientRefresh)
            {
                FailRecipientRefresh = false;
                throw new HttpRequestException("recipient directory temporarily unavailable");
            }

            if (request.Method == HttpMethod.Put &&
                request.RequestUri.AbsolutePath.StartsWith("/v1/sessions/", StringComparison.Ordinal) &&
                FailSessionResponses > 0)
            {
                FailSessionResponses--;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new ByteArrayContent("{}"u8.ToArray())
                };
            }

            if (request.Method == HttpMethod.Put && request.RequestUri.AbsolutePath.StartsWith("/v1/pcs/", StringComparison.Ordinal))
            {
                if (BlockNextPc)
                {
                    BlockNextPc = false;
                    Entered.TrySetResult(true);
                    await Release.Task.WaitAsync(cancellationToken);
                }
                if (FailNextPc != null)
                {
                    var error = FailNextPc;
                    FailNextPc = null;
                    throw error;
                }
            }

            return new HttpResponseMessage(
                request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("{}"u8.ToArray())
            };
        }

        internal void ResetSignals()
        {
            Entered = NewSignal();
            Release = NewSignal();
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task<int> Main()
    {
        object runtime = null;
        try
        {
            ValidateReconnectPreferences();
            var agentAssembly = Assembly.Load("TermLink.Agent");
            var runtimeType = agentAssembly.GetType("TermLink.Agent.ProductionAgentRuntime", true);
            var terminalLauncherType = agentAssembly.GetType("TermLink.Agent.ManagedTerminalLauncher", true);
            var configType = agentAssembly.GetType("TermLink.Agent.ProductionAgentConfig", true);
            var credentialsType = agentAssembly.GetType("TermLink.Agent.ProductionCredentials", true);
            var relayStateType = Field(runtimeType, "relayState").FieldType;

            var config = Activator.CreateInstance(configType, nonPublic: true);
            var pcId = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
            Set(config, "ServerUrl", "https://heartbeat.invalid");
            Set(config, "PcId", pcId);
            Set(config, "PcName", "Heartbeat Validation PC");
            Set(config, "AgentVersion", "validation-1");
            Set(config, "KeyEpoch", 7L);

            var credentials = Activator.CreateInstance(credentialsType, nonPublic: true);
            var identity = DeviceIdentity.Generate();
            Set(credentials, "DeviceId", Guid.Parse("12345678-abcd-4321-abcd-1234567890ab"));
            Set(credentials, "Identity", identity);
            Set(credentials, "AccessToken", "heartbeat-validation-token");
            Set(credentials, "AccessTokenExpiresAtUtc", DateTime.MaxValue);

            var constructor = runtimeType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { configType, credentialsType },
                modifiers: null) ?? throw new InvalidOperationException("Production runtime constructor is unavailable.");
            runtime = constructor.Invoke(new[] { config, credentials });

            var handler = new RecordingHandler();
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var httpField = Field(runtimeType, "http");
            ((HttpClient)httpField.GetValue(runtime)).Dispose();
            httpField.SetValue(runtime, client);

            var ensureRegistered = Method(runtimeType, "EnsureRegisteredAsync");
            await InvokeTask(ensureRegistered, runtime, CancellationToken.None);
            var initial = PCRequests(handler, pcId).Single();
            var registrationRequest = handler.Requests.Single(value =>
                value.Method == HttpMethod.Post && value.Path == "/v1/devices/register");
            using (var registrationJson = JsonDocument.Parse(registrationRequest.Body))
            {
                Require(registrationJson.RootElement.GetProperty("type").GetString() == "PC_AGENT",
                    "A new Windows Agent did not prefer the generic PC_AGENT device type.");
            }

            var buildBody = Method(runtimeType, "BuildPCUpsertBody");
            var legacyBody = (byte[])buildBody.Invoke(
                null,
                new object[] { "Heartbeat Validation PC", "validation-1", 7L });
            using (var legacyJson = JsonDocument.Parse(legacyBody))
            {
                Require(!legacyJson.RootElement.TryGetProperty("supported_shells", out _),
                    "The legacy PC upsert helper changed its compatibility contract.");
            }
            var buildCapabilitiesBody = Method(runtimeType, "BuildPCUpsertBodyWithCapabilities");
            var detectShells = Method(terminalLauncherType, "DetectSupportedShellTypes");
            var initialShells = (string[])detectShells.Invoke(null, null);
            var legacyCapabilitiesBody = (byte[])buildCapabilitiesBody.Invoke(
                null,
                new object[] { "Heartbeat Validation PC", "validation-1", 7L, initialShells });
            using (var legacyCapabilitiesJson = JsonDocument.Parse(legacyCapabilitiesBody))
            {
                Require(!legacyCapabilitiesJson.RootElement.TryGetProperty("terminal_commands", out _),
                    "The supported-shell compatibility helper changed its contract.");
            }
            var detectTerminalCommands = Method(terminalLauncherType, "DetectTerminalCommandCapabilities");
            var initialCommands = detectTerminalCommands.Invoke(null, null);
            var buildTerminalCommandsBody = Method(runtimeType, "BuildPCUpsertBodyWithTerminalCommands");
            var buildPCAgentBody = Method(runtimeType, "BuildPCAgentUpsertBodyWithTerminalCommands");
            var buildStablePCAgentBody = Method(runtimeType, "BuildStablePCAgentUpsertBodyWithTerminalCommands");
            var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var expectedBody = (byte[])buildPCAgentBody.Invoke(
                null,
                new[]
                {
                    (object)"Heartbeat Validation PC", "validation-1", 7L,
                    RuntimeInformation.OSDescription, architecture,
                    (bool)Field(runtimeType, "terminalSnapshotEnabled").GetValue(runtime),
                    initialShells, initialCommands
                });
            Require(initial.Method == HttpMethod.Put && initial.Body.SequenceEqual(expectedBody),
                "Initial registration did not advertise detected PC capabilities.");
            var pcUpsertKey = Method(runtimeType, "DeviceScopedPCUpsertIdempotencyKey");
            var deviceId = (Guid)credentialsType.GetProperty("DeviceId")?.GetValue(credentials);
            var sameKey = (string)pcUpsertKey.Invoke(null, new object[] { pcId, deviceId, expectedBody });
            var changedBody = (byte[])buildBody.Invoke(
                null,
                new object[] { "Heartbeat Validation PC", "validation-2", 7L });
            var changedBodyKey = (string)pcUpsertKey.Invoke(null, new object[] { pcId, deviceId, changedBody });
            var changedPcKey = (string)pcUpsertKey.Invoke(
                null, new object[] {
                    Guid.Parse("11111111-2222-4333-8444-555555555555"), deviceId, expectedBody });
            var changedDeviceKey = (string)pcUpsertKey.Invoke(
                null, new object[] {
                    pcId, Guid.Parse("99999999-2222-4333-8444-555555555555"), expectedBody });
            Require(initial.IdempotencyKey == sameKey &&
                Regex.IsMatch(
                    initial.IdempotencyKey,
                    "^pc-upsert-v3-" + pcId.ToString("N") + "-" + deviceId.ToString("N") + "-[0-9a-f]{32}$",
                    RegexOptions.CultureInvariant) &&
                changedBodyKey != sameKey && changedPcKey != sameKey && changedDeviceKey != sameKey,
                "PC upsert V3 key is not namespace-versioned, stable, and body/PC/device scoped.");
            using (var json = JsonDocument.Parse(initial.Body))
            {
                Require(json.RootElement.GetProperty("session_heartbeat_capable").GetBoolean(),
                    "PC upsert did not advertise heartbeat capability.");
                Require(
                    json.RootElement.GetProperty("supported_shells").EnumerateArray()
                        .Select(value => value.GetString()).SequenceEqual(initialShells),
                    "PC upsert did not advertise the detected managed shells.");
                Require(json.RootElement.GetProperty("terminal_commands").ValueKind == JsonValueKind.Array,
                    "PC upsert did not advertise standardized terminal commands.");
                Require(json.RootElement.GetProperty("platform").GetString() == "windows" &&
                    json.RootElement.GetProperty("architecture").GetString() == architecture &&
                    json.RootElement.GetProperty("platform_version").GetString() == RuntimeInformation.OSDescription,
                    "PC upsert did not advertise its bounded platform identity.");
                var capabilities = json.RootElement.GetProperty("capabilities").EnumerateArray()
                    .Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
                Require(capabilities.Contains("realtime_terminal_v1") &&
                    capabilities.Contains("file_operations_v1") &&
                    capabilities.Contains("terminal_commands_v1") &&
                    capabilities.Contains("remote_agent_update_v1"),
                    "PC upsert did not advertise the generic bounded capability set.");
            }
            var machineId = Convert.ToBase64String(
                    Enumerable.Range(1, 32).Select(value => (byte)value).ToArray())
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var stableBody = (byte[])buildStablePCAgentBody.Invoke(
                null,
                new[]
                {
                    (object)"Heartbeat Validation PC", "validation-1", 7L,
                    RuntimeInformation.OSDescription, architecture,
                    (bool)Field(runtimeType, "terminalSnapshotEnabled").GetValue(runtime),
                    initialShells, initialCommands, machineId
                });
            using (var stableJson = JsonDocument.Parse(stableBody))
            {
                Require(stableJson.RootElement.GetProperty("machine_id").GetString() == machineId,
                    "Stable PC upsert did not advertise the account-scoped machine identity.");
            }

            var capabilityRoot = Path.Combine(Path.GetTempPath(), "termlink-shell-capabilities-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(capabilityRoot);
            try
            {
                var sessionHost = Path.Combine(capabilityRoot, "TermLink.SessionHost.exe");
                var cmd = Path.Combine(capabilityRoot, "cmd.exe");
                var powershell = Path.Combine(capabilityRoot, "powershell.exe");
                var pwsh = Path.Combine(capabilityRoot, "pwsh.exe");
                var wsl = Path.Combine(capabilityRoot, "wsl.exe");
                File.WriteAllBytes(sessionHost, Array.Empty<byte>());
                File.WriteAllBytes(cmd, Array.Empty<byte>());
                var detectShellsAtPaths = Method(terminalLauncherType, "DetectSupportedShellTypesAtPaths");
                var detected = (string[])detectShellsAtPaths.Invoke(null, new object[] { sessionHost, cmd, powershell, pwsh, wsl });
                Require(detected.SequenceEqual(new[] { "command-prompt" }),
                    "Shell capability detection did not exclude a missing executable.");
                File.WriteAllBytes(powershell, Array.Empty<byte>());
                detected = (string[])detectShellsAtPaths.Invoke(null, new object[] { sessionHost, cmd, powershell, pwsh, wsl });
                Require(detected.SequenceEqual(new[] { "command-prompt", "windows-powershell" }),
                    "Shell capability detection is not stable and ordered.");
                File.WriteAllBytes(pwsh, Array.Empty<byte>());
                File.WriteAllBytes(wsl, Array.Empty<byte>());
                detected = (string[])detectShellsAtPaths.Invoke(null, new object[] { sessionHost, cmd, powershell, pwsh, wsl });
                Require(detected.SequenceEqual(new[] { "command-prompt", "windows-powershell", "powershell-7", "wsl" }),
                    "Extended shell capability detection is not stable and ordered.");
                File.Delete(sessionHost);
                Require(((string[])detectShellsAtPaths.Invoke(null, new object[] { sessionHost, cmd, powershell, pwsh, wsl })).Length == 0,
                    "Shell capability detection ignored a missing SessionHost.");
            }
            finally
            {
                Directory.Delete(capabilityRoot, recursive: true);
            }

            var sessionUpsertKey = Method(runtimeType, "SessionUpsertIdempotencyKey");
            var sessionId = Guid.Parse("ffffffff-eeee-4ddd-8ccc-bbbbbbbbbbbb");
            var sessionBodyA = "{\"title_cipher\":\"cipher-a\"}"u8.ToArray();
            var sessionBodyB = "{\"title_cipher\":\"cipher-b\"}"u8.ToArray();
            var sessionKeyA = (string)sessionUpsertKey.Invoke(
                null, new object[] { sessionId, 17UL, sessionBodyA });
            var sessionKeyARetry = (string)sessionUpsertKey.Invoke(
                null, new object[] { sessionId, 17UL, sessionBodyA });
            var sessionKeyBodyB = (string)sessionUpsertKey.Invoke(
                null, new object[] { sessionId, 17UL, sessionBodyB });
            var sessionKeySequence18 = (string)sessionUpsertKey.Invoke(
                null, new object[] { sessionId, 18UL, sessionBodyA });
            var maximumSessionKey = (string)sessionUpsertKey.Invoke(
                null, new object[] { sessionId, ulong.MaxValue, sessionBodyA });
            Require(sessionKeyA == sessionKeyARetry &&
                sessionKeyA != sessionKeyBodyB &&
                sessionKeyA != sessionKeySequence18 &&
                Regex.IsMatch(
                    sessionKeyA,
                    "^session-" + sessionId.ToString("N") + "-17-[0-9a-f]{64}$",
                    RegexOptions.CultureInvariant) &&
                maximumSessionKey.Length <= 128,
                "Session upsert key is not stable, full-body-scoped, sequence-scoped, or Server-compatible.");

            var publicationStateType = runtimeType.GetNestedType(
                "SessionPublicationState",
                BindingFlags.NonPublic) ?? throw new InvalidOperationException(
                    "Session publication state is unavailable.");
            var publicationState = Activator.CreateInstance(publicationStateType, nonPublic: true);
            Field(publicationStateType, "HasSize").SetValue(publicationState, true);
            Field(publicationStateType, "RestRevision").SetValue(publicationState, 4UL);
            Field(publicationStateType, "RestDirty").SetValue(publicationState, false);
            var publicationStates = Field(runtimeType, "publicationStates").GetValue(runtime);
            var added = (bool)publicationStates.GetType().GetMethod("TryAdd")?.Invoke(
                publicationStates,
                new[] { (object)sessionId, publicationState });
            Require(added, "Reconnect title validation could not arrange a live session.");

            var connected = Enum.Parse(relayStateType, "Connected");
            Method(runtimeType, "SetRelayState").Invoke(runtime, new[] { connected, string.Empty });
            var metadataSignal = (SemaphoreSlim)Field(runtimeType, "metadataSignal").GetValue(runtime);
            Require(metadataSignal.Wait(0) &&
                (bool)Field(publicationStateType, "RestDirty").GetValue(publicationState) &&
                (ulong)Field(publicationStateType, "RestRevision").GetValue(publicationState) == 5UL,
                "A newly connected Relay did not queue every quiet session title for REST re-publication.");

            var capturePublication = Method(runtimeType, "CapturePublicationSnapshot");
            var markPublication = Method(runtimeType, "MarkRestDirtyLocked");
            var confirmPublication = Method(runtimeType, "ConfirmSessionUpsert");
            var staleSnapshot = capturePublication.Invoke(
                null, new[] { (object)sessionId, publicationState });
            markPublication.Invoke(null, new[] { publicationState });
            var staleConfirmed = (bool)confirmPublication.Invoke(
                null,
                new object[] { publicationState, staleSnapshot, DateTime.UtcNow });
            Require(!staleConfirmed &&
                (bool)Field(publicationStateType, "RestDirty").GetValue(publicationState),
                "An in-flight session upsert cleared a newer title/authority publication revision.");
            var currentSnapshot = capturePublication.Invoke(
                null, new[] { (object)sessionId, publicationState });
            var currentConfirmed = (bool)confirmPublication.Invoke(
                null,
                new object[] { publicationState, currentSnapshot, DateTime.UtcNow });
            Require(currentConfirmed &&
                !(bool)Field(publicationStateType, "RestDirty").GetValue(publicationState),
                "The current session publication revision could not be confirmed.");

            // A failed authority refresh must never seal the current title to
            // a cached recipient that may have just been revoked. The empty
            // title_cipher property is the Server's authenticated
            // "preserve last ciphertext" sentinel.
            using (var staleAndroid = DeviceExchangeKeyPair.Generate())
            {
                var createAuthorized = Method(runtimeType, "CreateAuthorizedSubscriber");
                var staleRecipientId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
                var staleRecipient = createAuthorized.Invoke(
                    null,
                    new object[]
                    {
                        identity.Exchange,
                        (Guid)credentialsType.GetProperty("DeviceId")?.GetValue(credentials),
                        staleRecipientId,
                        staleAndroid.PublicKey,
                        7L
                    });
                var authorizedRecipients = Field(runtimeType, "authorizedRecipients").GetValue(runtime);
                var authorizedAdded = (bool)authorizedRecipients.GetType().GetMethod("TryAdd")?.Invoke(
                    authorizedRecipients,
                    new[] { (object)staleRecipientId, staleRecipient });
                Require(authorizedAdded, "Could not arrange the cached stale title recipient.");

                Field(publicationStateType, "Title").SetValue(publicationState, "must-not-leak-after-refresh-failure");
                Field(publicationStateType, "Columns").SetValue(publicationState, 120);
                Field(publicationStateType, "Rows").SetValue(publicationState, 40);
                Field(publicationStateType, "LastEventSequence").SetValue(publicationState, 17UL);
                Field(publicationStateType, "LastOutputSequence").SetValue(publicationState, 16UL);
                Field(publicationStateType, "RestRevision").SetValue(publicationState, 7UL);
                Field(publicationStateType, "RestDirty").SetValue(publicationState, true);
                var titleSnapshot = capturePublication.Invoke(
                    null, new[] { (object)sessionId, publicationState });
                Field(runtimeType, "authorityExpiresUtc").SetValue(runtime, DateTime.MinValue);
                handler.FailRecipientRefresh = true;
                var beforeSessionRequests = handler.Requests.Count;
                await InvokeTask(
                    Method(runtimeType, "UpsertSessionAsync"),
                    runtime,
                    new object[] { publicationState, titleSnapshot, true, CancellationToken.None });
                var sessionRequest = handler.Requests.Skip(beforeSessionRequests).Single(value =>
                    value.Method == HttpMethod.Put &&
                    value.Path == "/v1/sessions/" + sessionId.ToString("D"));
                using var sessionJson = JsonDocument.Parse(sessionRequest.Body);
                Require(sessionJson.RootElement.GetProperty("title_cipher").GetString() == string.Empty &&
                    !Encoding.UTF8.GetString(sessionRequest.Body).Contains(
                        "must-not-leak-after-refresh-failure",
                        StringComparison.Ordinal) &&
                    sessionRequest.IdempotencyKey == (string)sessionUpsertKey.Invoke(
                        null,
                        new object[] { sessionId, 17UL, sessionRequest.Body }),
                    "A failed recipient refresh used cached title authority or mismatched its REST body digest.");

                // Once one authorized generation is available, transient REST
                // retries must reuse the byte-identical envelope, body digest,
                // and idempotency key instead of re-sealing with a new nonce.
                Field(runtimeType, "authorityExpiresUtc").SetValue(
                    runtime,
                    DateTime.UtcNow.AddMinutes(1));
                Field(publicationStateType, "Title").SetValue(publicationState, "stable retry title");
                markPublication.Invoke(null, new[] { publicationState });
                var retrySnapshot = capturePublication.Invoke(
                    null, new[] { (object)sessionId, publicationState });
                handler.FailSessionResponses = 1;
                var beforeRetryRequests = handler.Requests.Count;
                await InvokeTask(
                    Method(runtimeType, "UpsertSessionAsync"),
                    runtime,
                    new object[] { publicationState, retrySnapshot, true, CancellationToken.None });
                var retryRequests = handler.Requests.Skip(beforeRetryRequests).Where(value =>
                    value.Method == HttpMethod.Put &&
                    value.Path == "/v1/sessions/" + sessionId.ToString("D")).ToArray();
                Require(retryRequests.Length == 2 &&
                    retryRequests[0].Body.SequenceEqual(retryRequests[1].Body) &&
                    retryRequests[0].IdempotencyKey == retryRequests[1].IdempotencyKey,
                    "A transient session REST retry changed its title envelope, body, or idempotency key.");
                using var retryJson = JsonDocument.Parse(retryRequests[0].Body);
                Require(!string.IsNullOrWhiteSpace(
                        retryJson.RootElement.GetProperty("title_cipher").GetString()) &&
                    !Encoding.UTF8.GetString(retryRequests[0].Body).Contains(
                        "stable retry title",
                        StringComparison.Ordinal),
                    "The authorized title retry omitted ciphertext or exposed plaintext.");
            }
            var lastAttempt = Field(runtimeType, "lastPCUpsertAttemptUtc");
            lastAttempt.SetValue(runtime, DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(16)));
            var heartbeat = Method(runtimeType, "TryHeartbeatPCAsync");
            await InvokeTask(heartbeat, runtime, CancellationToken.None);
            var repeated = PCRequests(handler, pcId).ToArray();
            Require(repeated.Length == 2 && repeated[1].Body.SequenceEqual(initial.Body) &&
                repeated[1].IdempotencyKey == initial.IdempotencyKey,
                "Periodic PC heartbeat changed the initial upsert body or idempotency key.");

            await InvokeTask(heartbeat, runtime, CancellationToken.None);
            Require(PCRequests(handler, pcId).Count() == 2,
                "PC heartbeat ignored its 15-second cadence.");

            var due = Method(runtimeType, "IsPCHeartbeatDue");
            var now = DateTime.UtcNow;
            Require(!(bool)due.Invoke(null, new object[] { false, now.AddMinutes(-1), now }) &&
                !(bool)due.Invoke(null, new object[] { true, now.AddSeconds(-14), now }) &&
                (bool)due.Invoke(null, new object[] { true, now.AddSeconds(-15), now }),
                "PC heartbeat due calculation is not connected-only at <=15 seconds.");

            handler.ResetSignals();
            handler.BlockNextPc = true;
            lastAttempt.SetValue(runtime, DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(16)));
            var blockedHeartbeat = InvokeTask(heartbeat, runtime, CancellationToken.None);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var socketGate = (SemaphoreSlim)Field(runtimeType, "socketSendGate").GetValue(runtime);
            Require(socketGate.Wait(0),
                "A blocked PC heartbeat held the terminal WebSocket send gate.");
            socketGate.Release();
            handler.Release.TrySetResult(true);
            await blockedHeartbeat;

            handler.FailNextPc = new HttpRequestException("transient heartbeat failure");
            lastAttempt.SetValue(runtime, DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(16)));
            await InvokeTask(heartbeat, runtime, CancellationToken.None);
            Require(Field(runtimeType, "relayState").GetValue(runtime).Equals(connected) &&
                socketGate.CurrentCount == 1,
                "A transient PC heartbeat failure disturbed healthy realtime state.");
            var afterFailure = PCRequests(handler, pcId).Count();
            await InvokeTask(heartbeat, runtime, CancellationToken.None);
            Require(PCRequests(handler, pcId).Count() == afterFailure,
                "A failed PC heartbeat retried before the next cadence.");
            lastAttempt.SetValue(runtime, DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(16)));
            await InvokeTask(heartbeat, runtime, CancellationToken.None);
            var allPcRequests = PCRequests(handler, pcId).ToArray();
            Require(allPcRequests.Length == afterFailure + 1 &&
                allPcRequests.All(value => value.Body.SequenceEqual(initial.Body) &&
                    value.IdempotencyKey == initial.IdempotencyKey),
                "PC heartbeat did not retry with the stable body/key on the next cadence.");

            Require(runtimeType.GetField("pcHeartbeatWorker", BindingFlags.Instance | BindingFlags.NonPublic) == null &&
                runtimeType.GetField("metadataWorker", BindingFlags.Instance | BindingFlags.NonPublic) != null,
                "PC heartbeat added a worker instead of reusing metadata scheduling.");

            var beforeLegacyFallback = handler.Requests.Count;
            handler.RejectNextPCAgentRegistration = true;
            handler.RejectNextGenericPCUpsert = true;
            await InvokeTask(ensureRegistered, runtime, CancellationToken.None);
            var fallbackRequests = handler.Requests.Skip(beforeLegacyFallback).ToArray();
            var fallbackRegistrations = fallbackRequests.Where(value =>
                value.Method == HttpMethod.Post && value.Path == "/v1/devices/register").ToArray();
            Require(fallbackRegistrations.Length == 2,
                "Legacy Server fallback did not retry registration exactly once.");
            using (var firstRegistration = JsonDocument.Parse(fallbackRegistrations[0].Body))
            using (var secondRegistration = JsonDocument.Parse(fallbackRegistrations[1].Body))
            {
                Require(firstRegistration.RootElement.GetProperty("type").GetString() == "PC_AGENT" &&
                    secondRegistration.RootElement.GetProperty("type").GetString() == "WINDOWS_PC",
                    "Legacy Server fallback did not downgrade only the device type.");
            }
            var fallbackUpserts = fallbackRequests.Where(value =>
                value.Method == HttpMethod.Put && value.Path == "/v1/pcs/" + pcId.ToString("D")).ToArray();
            Require(fallbackUpserts.Length == 2,
                "Legacy Server fallback did not retry PC metadata exactly once.");
            using (var genericMetadata = JsonDocument.Parse(fallbackUpserts[0].Body))
            using (var legacyMetadata = JsonDocument.Parse(fallbackUpserts[1].Body))
            {
                Require(genericMetadata.RootElement.TryGetProperty("capabilities", out _) &&
                    !legacyMetadata.RootElement.TryGetProperty("capabilities", out _) &&
                    !legacyMetadata.RootElement.TryGetProperty("platform", out _),
                    "Legacy Server fallback did not preserve the pre-P5 PC metadata contract.");
            }

            Set(config, "MachineId", machineId);
            var beforeStableRejection = handler.Requests.Count;
            handler.RejectNextGenericPCUpsert = true;
            var stableRejected = false;
            try { await InvokeTask(ensureRegistered, runtime, CancellationToken.None); }
            catch { stableRejected = true; }
            var stableRequests = handler.Requests.Skip(beforeStableRejection).ToArray();
            var stableUpserts = stableRequests.Where(value =>
                value.Method == HttpMethod.Put && value.Path == "/v1/pcs/" + pcId.ToString("D")).ToArray();
            Require(stableRejected && stableUpserts.Length == 1,
                "A stable-machine Agent silently downgraded to metadata that could create a duplicate PC.");
            using (var stableRejectedMetadata = JsonDocument.Parse(stableUpserts[0].Body))
            {
                Require(stableRejectedMetadata.RootElement.GetProperty("machine_id").GetString() == machineId,
                    "The fail-closed stable-machine request omitted its correlation identity.");
            }

            Console.WriteLine("Production PC heartbeat validation passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            if (runtime is IDisposable disposable) disposable.Dispose();
        }
    }

    private static void ValidateReconnectPreferences()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "TermLink-reconnect-preferences-" + Guid.NewGuid().ToString("N") + ".json");
        AppContext.SetData(
            "TermLink.ProductionReconnectPreferences.StoragePath",
            path);
        try
        {
            var saved = new ProductionReconnectPreferences
            {
                InitialDelaySeconds = 2,
                MaximumDelaySeconds = 10,
                MaximumFastAttempts = 3
            };
            ProductionReconnectPreferences.Save(saved);
            var loaded = ProductionReconnectPreferences.Load();
            Require(loaded.InitialDelaySeconds == 2 &&
                loaded.MaximumDelaySeconds == 10 &&
                loaded.MaximumFastAttempts == 3,
                "Reconnect preferences did not survive an atomic save/load round trip.");

            var delayMethod = typeof(ProductionReconnectPreferences).GetMethod(
                "DelayAfterFailure",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("Reconnect delay policy is unavailable.");
            var delays = Enumerable.Range(1, 5)
                .Select(failure => (TimeSpan)delayMethod.Invoke(loaded, new object[] { failure }))
                .Select(delay => delay.TotalSeconds)
                .ToArray();
            Require(delays.SequenceEqual(new[] { 2d, 4d, 8d, 60d, 60d }),
                "Reconnect delay policy did not honor fast attempts, exponential bounds, and slow probing.");

            loaded.MaximumDelaySeconds = 300;
            Require(((TimeSpan)delayMethod.Invoke(loaded, new object[] { 4 })).TotalSeconds == 60d,
                "Slow probing did not remain at the documented 60-second cadence when the exponential ceiling was higher.");

            File.WriteAllText(path, "{ damaged preferences", Encoding.UTF8);
            var fallback = ProductionReconnectPreferences.Load();
            Require(fallback.InitialDelaySeconds == 1 &&
                fallback.MaximumDelaySeconds == 15 &&
                fallback.MaximumFastAttempts == 0,
                "Damaged reconnect preferences did not fail safely to production defaults.");
        }
        finally
        {
            AppContext.SetData(
                "TermLink.ProductionReconnectPreferences.StoragePath",
                null);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static IEnumerable<CapturedRequest> PCRequests(RecordingHandler handler, Guid pcId) =>
        handler.Requests.Where(value =>
            value.Method == HttpMethod.Put &&
            value.Path == "/v1/pcs/" + pcId.ToString("D"));

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(
            name,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing field: " + name);

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing method: " + name);

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(target, value);

    private static async Task InvokeTask(MethodInfo method, object target, CancellationToken token)
    {
        try
        {
            await (Task)method.Invoke(target, new object[] { token });
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            throw error.InnerException;
        }
    }

    private static async Task InvokeTask(MethodInfo method, object target, object[] arguments)
    {
        try
        {
            await (Task)method.Invoke(target, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            throw error.InnerException;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
