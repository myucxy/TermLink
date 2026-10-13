using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.Protocol;

namespace TermLink.AgentUpdate.Validation
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--enrollment-loopback")
                {
                    EnrollmentLoopbackValidation.Run();
                    return 0;
                }
                ValidateBrowserAuthorizationLaunch();
                ValidateBrowserCallbackUsesDynamicPort();
                await ValidateBrowserCallbackIgnoresLocalProbeAsync();
                await ValidateBrowserCallbackSurvivesEarlyBrowserCloseAsync();
                ValidateRetainedMachineIdentity();
                await ValidateRetainedDevicePreflightAsync();
                AccountPersistenceRaceValidation.Run();
                EnrollmentLoopbackValidation.Run();
                TerminalProtocolDiagnosticPublicationValidation.Run();
                var pcId = Guid.NewGuid();
                var updateId = Guid.NewGuid();
                var mutations = new List<(string Path, string Key, byte[] Body)>();
                ProductionFileOperationApi.AuthenticatedSendAsync send =
                    (method, path, body, deviceProof, idempotencyKey, token) =>
                    {
                        if (path.EndsWith("/claim", StringComparison.Ordinal))
                        {
                            var claim = "{\"agent_update\":{" +
                                "\"id\":\"" + updateId + "\"," +
                                "\"pc_id\":\"" + pcId + "\"," +
                                "\"current_version\":\"0.1.31.37\",\"current_build\":37," +
                                "\"target_version\":\"0.1.32\",\"target_build\":38," +
                                "\"artifact_url\":\"https://updates.example/TermLink-Setup.exe\"," +
                                "\"artifact_filename\":\"TermLink-Setup.exe\",\"artifact_size_bytes\":1," +
                                "\"artifact_sha256\":\"" + new string('a', 64) + "\"," +
                                "\"phase\":\"REQUESTED\"},\"lease_token\":\"lease-token\"}";
                            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(claim))
                            });
                        }
                        mutations.Add((path, idempotencyKey, body));
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent("{}"u8.ToArray())
                        });
                    };

                var api = new ProductionAgentUpdateApi(pcId, send);
                var update = await api.ClaimAsync(CancellationToken.None);
                Assert(update.CurrentBuild == 37 && update.TargetBuild == 38,
                    "claim did not preserve build identity");
                ProductionAgentUpdateWorker.EnsureNewerTarget(update, 37);
                AssertThrows<InvalidDataException>(() =>
                    ProductionAgentUpdateWorker.EnsureNewerTarget(update, 36),
                    "an Agent with the wrong executing build accepted the claim");
                ValidateSupervisorRecoveryDecisions();
                await ValidateVerifiedDownloadPromotionAsync();
                await ValidateClaimRecoveryAsync();
                await ValidatePostRegistrationWorkerGateAsync();

                await api.ProgressAsync(update, "PRECHECKING", 2, 0, CancellationToken.None);
                await api.ProgressAsync(update, "DOWNLOADING", 5, null, CancellationToken.None);
                await api.RenewAsync(update, CancellationToken.None);
                await api.RenewAsync(update, CancellationToken.None);
                await api.CompleteAsync(
                    update, "SUCCEEDED", null, null, "0.1.32.38", 38, CancellationToken.None);

                var progressKeys = mutations.Where(value => value.Path.EndsWith("/progress", StringComparison.Ordinal))
                    .Select(value => value.Key).ToArray();
                Assert(progressKeys.Length == 2 && progressKeys.Distinct(StringComparer.Ordinal).Count() == 2,
                    "separate progress states reused an idempotency key");
                var renewKeys = mutations.Where(value => value.Path.EndsWith("/renew", StringComparison.Ordinal))
                    .Select(value => value.Key).ToArray();
                Assert(renewKeys.Length == 2 && renewKeys.Distinct(StringComparer.Ordinal).Count() == 2,
                    "separate lease renewals reused an idempotency key");
                var completion = mutations.Single(value => value.Path.EndsWith("/complete", StringComparison.Ordinal));
                using var completionJson = JsonDocument.Parse(completion.Body);
                Assert(completionJson.RootElement.GetProperty("observed_agent_build").GetInt64() == 38,
                    "completion omitted the observed Agent build");
                Assert(mutations.All(value => value.Key.Length is >= 8 and <= 128),
                    "an Agent update idempotency key is outside the Server contract");

                Console.WriteLine("Agent update API/build/idempotency validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static async Task ValidateClaimRecoveryAsync()
        {
            await ValidateAmbiguousClaimRecoveryAsync();
            await ValidateAmbiguousClaimBodyRecoveryAsync();
            await ValidateDefinitiveClaimRecoveryAsync();
            await ValidateInProgressClaimRecoveryAsync();
            await ValidateInvalidClaimRecoveryAsync();
        }

        private static async Task ValidateAmbiguousClaimRecoveryAsync()
        {
            var keys = new List<string>();
            var attempt = 0;
            ProductionFileOperationApi.AuthenticatedSendAsync send =
                (method, path, body, deviceProof, idempotencyKey, token) =>
                {
                    keys.Add(idempotencyKey);
                    if (Interlocked.Increment(ref attempt) == 1)
                        throw new HttpRequestException("response lost after Server update claim");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                };
            var api = new ProductionAgentUpdateApi(Guid.NewGuid(), send);
            try { await api.ClaimAsync(CancellationToken.None); }
            catch (HttpRequestException) { }
            Assert(await api.ClaimAsync(CancellationToken.None) == null &&
                keys.Count == 2 && keys[0] == keys[1],
                "ambiguous Agent update claim retry changed its idempotency key");
            Assert(await api.ClaimAsync(CancellationToken.None) == null && keys[2] != keys[1],
                "confirmed Agent update claim did not advance to a fresh idempotency key");
        }

        private static async Task ValidateDefinitiveClaimRecoveryAsync()
        {
            var keys = new List<string>();
            var attempt = 0;
            ProductionFileOperationApi.AuthenticatedSendAsync send =
                (method, path, body, deviceProof, idempotencyKey, token) =>
                {
                    keys.Add(idempotencyKey);
                    if (Interlocked.Increment(ref attempt) == 1)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                        {
                            Content = new StringContent(
                                "{\"error\":{\"code\":\"PC_NOT_FOUND\"}}",
                                Encoding.UTF8,
                                "application/json")
                        });
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                };
            var api = new ProductionAgentUpdateApi(Guid.NewGuid(), send);
            try { await api.ClaimAsync(CancellationToken.None); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Forbidden) { }
            Assert(await api.ClaimAsync(CancellationToken.None) == null &&
                keys.Count == 2 && keys[0] != keys[1],
                "a definitive HTTP 403 pinned Agent update polling to a cached idempotency result");
        }

        private static async Task ValidateAmbiguousClaimBodyRecoveryAsync()
        {
            var keys = new List<string>();
            var attempt = 0;
            ProductionFileOperationApi.AuthenticatedSendAsync send =
                (method, path, body, deviceProof, idempotencyKey, token) =>
                {
                    keys.Add(idempotencyKey);
                    if (Interlocked.Increment(ref attempt) == 1)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new FaultingHttpContent()
                        });
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                };
            var api = new ProductionAgentUpdateApi(Guid.NewGuid(), send);
            try { await api.ClaimAsync(CancellationToken.None); }
            catch (HttpRequestException) { }
            catch (IOException) { }
            Assert(await api.ClaimAsync(CancellationToken.None) == null &&
                keys.Count == 2 && keys[0] == keys[1],
                "an Agent update claim with an incomplete response body did not preserve its ambiguous key");
        }

        private static async Task ValidateInProgressClaimRecoveryAsync()
        {
            var keys = new List<string>();
            ProductionFileOperationApi.AuthenticatedSendAsync send =
                (method, path, body, deviceProof, idempotencyKey, token) =>
                {
                    keys.Add(idempotencyKey);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent(
                            "{\"error\":{\"code\":\"IDEMPOTENCY_IN_PROGRESS\"}}",
                            Encoding.UTF8,
                            "application/json")
                    });
                };
            var api = new ProductionAgentUpdateApi(Guid.NewGuid(), send);
            for (var index = 0; index < 5; index++)
            {
                try { await api.ClaimAsync(CancellationToken.None); }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict) { continue; }
                throw new InvalidOperationException("Agent update IDEMPOTENCY_IN_PROGRESS was not retryable.");
            }
            Assert(keys.Take(4).Distinct(StringComparer.Ordinal).Count() == 1 &&
                keys[4] != keys[3],
                "Agent update claim did not bound in-progress retries before rotating its key");
        }

        private static async Task ValidateInvalidClaimRecoveryAsync()
        {
            var keys = new List<string>();
            var attempt = 0;
            ProductionFileOperationApi.AuthenticatedSendAsync send =
                (method, path, body, deviceProof, idempotencyKey, token) =>
                {
                    keys.Add(idempotencyKey);
                    if (Interlocked.Increment(ref attempt) == 1)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{}", Encoding.UTF8, "application/json")
                        });
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                };
            var api = new ProductionAgentUpdateApi(Guid.NewGuid(), send);
            try { await api.ClaimAsync(CancellationToken.None); }
            catch (InvalidDataException) { }
            Assert(await api.ClaimAsync(CancellationToken.None) == null &&
                keys.Count == 2 && keys[0] != keys[1],
                "an invalid successful Agent update response pinned polling to its cached response");
        }

        private static async Task ValidatePostRegistrationWorkerGateAsync()
        {
            var registrations = 0;
            var fileStarts = 0;
            var updateStarts = 0;
            var rejectRegistration = true;
            var gate = new PostRegistrationWorkerStartGate(
                () => fileStarts++,
                () => updateStarts++);
            Task Register(CancellationToken token)
            {
                registrations++;
                return rejectRegistration
                    ? Task.FromException(new HttpRequestException("PC upsert rejected"))
                    : Task.CompletedTask;
            }

            try { await gate.EnsureRegisteredThenStartAsync(Register, CancellationToken.None); }
            catch (HttpRequestException) { }
            Assert(registrations == 1 && fileStarts == 0 && updateStarts == 0,
                "REST workers started before the PC registration transaction succeeded");

            rejectRegistration = false;
            await gate.EnsureRegisteredThenStartAsync(Register, CancellationToken.None);
            await gate.EnsureRegisteredThenStartAsync(Register, CancellationToken.None);
            Assert(registrations == 3 && fileStarts == 1 && updateStarts == 1,
                "post-registration workers were not started exactly once across reconnect registration");

            var retiredStarts = 0;
            var retiredGate = new PostRegistrationWorkerStartGate(() => retiredStarts++);
            var registrationRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlightRegistration = retiredGate.EnsureRegisteredThenStartAsync(
                _ => registrationRelease.Task,
                CancellationToken.None);
            retiredGate.Retire();
            registrationRelease.SetResult();
            await inFlightRegistration;
            Assert(retiredStarts == 0,
                "a registration completing during shutdown started a retired worker");
        }

        private static void ValidateBrowserAuthorizationLaunch()
        {
            var cancelled = new ProductionEnrollment.BrowserAuthorizationAttempt();
            cancelled.Cancel();
            try { cancelled.BeginCommit(); throw new InvalidOperationException("Cancelled authorization accepted a late callback."); }
            catch (OperationCanceledException) { }
            var committing = new ProductionEnrollment.BrowserAuthorizationAttempt();
            committing.BeginCommit();
            try { committing.Cancel(); throw new InvalidOperationException("Saving authorization was falsely cancelled."); }
            catch (ProductionEnrollmentException error) when (error.Code == "ACCOUNT_AUTHORIZATION_COMMITTING") { }
            const string authorizationUrl = "https://identity.example.test/authorize?client_id=termlink-windows";
            var start = ProductionEnrollment.CreateBrowserStartInfo(authorizationUrl);
            Assert(start.FileName == authorizationUrl && start.ArgumentList.Count == 0,
                "browser authorization URL changed or was passed to an executable as a file-system argument");
            Assert(start.UseShellExecute && start.Verb == "open",
                "browser authorization no longer uses the registered HTTPS handler");
            Assert(start.WindowStyle == ProcessWindowStyle.Normal,
                "browser authorization inherited the hidden Agent window state");
            Assert(!start.CreateNoWindow,
                "browser authorization was configured as a no-window process");
            AssertThrows<ArgumentException>(
                () => ProductionEnrollment.CreateBrowserStartInfo("http://identity.example.test/authorize"),
                "browser authorization accepted a non-HTTPS URL");
            var fallback = ProductionEnrollment.CreateBrowserExecutableStartInfo(
                @"C:\Browser\browser.exe",
                authorizationUrl);
            Assert(!fallback.UseShellExecute && fallback.FileName == @"C:\Browser\browser.exe" &&
                    fallback.ArgumentList.SequenceEqual(new[] { authorizationUrl }),
                "browser fallback did not pass the validated HTTPS URL as one executable argument");
        }

        private static void ValidateRetainedMachineIdentity()
        {
            const string issuer = "https://identity.example.test";
            var accountKey = "oidc-sha256:" + new string('a', 64);
            var otherAccountKey = "oidc-sha256:" + new string('b', 64);
            var machineId = ProductionMachineIdentity.Derive(
                "00112233-4455-6677-8899-aabbccddeeff", issuer, accountKey);
            var sameMachineId = ProductionMachineIdentity.Derive(
                "{00112233-4455-6677-8899-AABBCCDDEEFF}", issuer + "/", accountKey);
            Assert(ProductionMachineIdentity.IsValid(machineId) && machineId == sameMachineId,
                "machine identity derivation is not canonical and stable");
            Assert(ProductionMachineIdentity.Derive(
                    "00112233-4455-6677-8899-aabbccddeeff", issuer, otherAccountKey) != machineId,
                "machine identity was not isolated by OIDC account");
            Assert(ProductionMachineIdentity.DeriveInitialPcId(machineId) ==
                    ProductionMachineIdentity.DeriveInitialPcId(sameMachineId),
                "initial PC ID derivation is not stable");

            var root = Path.Combine(Path.GetTempPath(), "TermLink-retained-identity-" + Guid.NewGuid().ToString("N"));
            var originalRoot = Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT");
            var originalMarker = Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE");
            Directory.CreateDirectory(root);
            try
            {
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT", root);
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE", Path.Combine(root, "acceptance.cer"));
                using var identity = DeviceIdentity.Generate();
                var deviceId = Guid.NewGuid();
                var pcId = Guid.NewGuid();
                var config = new ProductionAgentConfig
                {
                    ServerUrl = "https://server.example.test",
                    PcId = pcId,
                    PcName = "Identity Test PC",
                    AccountKey = accountKey,
                    AgentVersion = "test",
                    KeyEpoch = 4,
                    OIDCIssuer = issuer,
                    OIDCClientId = "termlink-windows",
                    MachineId = machineId
                };
                ProductionIdentityStore.Save(config, deviceId, identity, accountKey);
                ProductionAccountStorage.ForgetLocalAccount(root);
                Assert(File.Exists(ProductionAccountStorage.IdentityStorePath),
                    "sign-out removed the token-free retained identity store");
                using var restored = ProductionIdentityStore.TryLoad(accountKey, issuer, machineId);
                Assert(restored != null && restored.DeviceId == deviceId && restored.PcId == pcId &&
                        restored.PcName == config.PcName && restored.KeyEpoch == config.KeyEpoch,
                    "retained identity did not round trip through DPAPI");
                Assert(ProductionIdentityStore.TryLoad(otherAccountKey, issuer, machineId) == null,
                    "retained identity crossed an OIDC account boundary");
                Assert(!ProductionIdentityStore.TrySave(config, deviceId, identity, otherAccountKey),
                    "a mismatched retained identity escaped the non-blocking account boundary");

                File.WriteAllBytes(ProductionAccountStorage.IdentityStorePath, new byte[] { 1, 2, 3, 4 });
                Assert(ProductionIdentityStore.TryLoad(accountKey, issuer, machineId) == null,
                    "a corrupt retained identity store was treated as authenticated identity");
                ProductionIdentityStore.Save(config, deviceId, identity, accountKey);
                using var recoveredAfterCorruption = ProductionIdentityStore.TryLoad(accountKey, issuer, machineId);
                Assert(recoveredAfterCorruption != null && recoveredAfterCorruption.DeviceId == deviceId &&
                        recoveredAfterCorruption.PcId == pcId,
                    "a corrupt retained identity store could not be replaced safely");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT", originalRoot);
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE", originalMarker);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static async Task ValidateRetainedDevicePreflightAsync()
        {
            using var identity = DeviceIdentity.Generate();
            var deviceId = Guid.NewGuid();
            var responses = new Queue<Func<HttpResponseMessage>>(new Func<HttpResponseMessage>[]
            {
                () => new HttpResponseMessage(HttpStatusCode.OK),
                () => DeviceStatusResponse(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"DEVICE_REVOKED\"}}"),
                () => DeviceStatusResponse(HttpStatusCode.Unauthorized, "not-json"),
                () => DeviceStatusResponse(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"UNAUTHENTICATED\"}}"),
                () => DeviceStatusResponse(HttpStatusCode.Unauthorized, new string('x', 4097)),
                () => throw new HttpRequestException("offline")
            });
            var requests = 0;
            using var http = new HttpClient(new DeviceStatusHandler(request =>
            {
                requests++;
                Assert(request.Method == HttpMethod.Get &&
                        request.RequestUri?.AbsoluteUri == "https://server.example.test/v1/devices/self" &&
                        request.Content == null &&
                        request.Headers.Authorization?.Scheme == "Bearer" &&
                        request.Headers.Authorization.Parameter == "new-access-token",
                    "retained device preflight used the wrong endpoint or token");
                var headerDevice = request.Headers.GetValues("X-TermLink-Device").Single();
                var timestamp = request.Headers.GetValues("X-TermLink-Timestamp").Single();
                var nonce = request.Headers.GetValues("X-TermLink-Nonce").Single();
                var signature = request.Headers.GetValues("X-TermLink-Signature").Single();
                Assert(headerDevice == deviceId.ToString("D") && long.TryParse(timestamp, out var millis) &&
                        Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - millis) < 60000,
                    "retained device preflight omitted the device identity or current timestamp");
                var nonceBytes = Convert.FromBase64String(nonce.Replace('-', '+').Replace('_', '/') + "==");
                Assert(nonceBytes.Length == 16, "retained device preflight nonce is invalid");
                var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", "GET", "/v1/devices/self",
                    timestamp, nonce, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant());
                var expected = identity.Sign(Encoding.UTF8.GetBytes(canonical));
                var actual = Convert.FromBase64String(signature.Replace('-', '+').Replace('_', '/') + "==");
                Assert(CryptographicOperations.FixedTimeEquals(expected, actual),
                    "retained device preflight did not sign the server's canonical request");
                return responses.Dequeue()();
            }));
            Task<bool> preflight() => ProductionEnrollment.IsDeviceRevokedAsync(
                http, "https://server.example.test", "new-access-token", deviceId, identity, CancellationToken.None);
            Assert(!await preflight(), "trusted or pending retained device was rotated");
            Assert(await preflight(), "explicitly revoked retained device was reused");
            for (var index = 0; index < 3; index++)
            {
                try { await preflight(); throw new Exception("invalid device status was accepted"); }
                catch (ProductionEnrollmentException error) when (error.Code == "ACCOUNT_DEVICE_STATUS_UNAVAILABLE") { }
            }
            try { await preflight(); throw new Exception("network failure was treated as device revocation"); }
            catch (HttpRequestException) { }
            Assert(requests == 6 && responses.Count == 0, "retained device preflight skipped a behavior case");
        }

        private static HttpResponseMessage DeviceStatusResponse(HttpStatusCode status, string body) =>
            new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class DeviceStatusHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

            internal DeviceStatusHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
                this.respond = respond;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }

        private static void ValidateBrowserCallbackUsesDynamicPort()
        {
            using var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Server.ExclusiveAddressUse = true;
            occupied.Start(1);
            var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

            using var first = ProductionEnrollment.StartCallbackListener(
                new Uri("http://127.0.0.1/oauth2/callback"), out var firstRedirect);
            using var second = ProductionEnrollment.StartCallbackListener(
                new Uri("http://127.0.0.1/oauth2/callback"), out var secondRedirect);
            var firstEndpoint = (IPEndPoint)first.LocalEndpoint;
            var secondEndpoint = (IPEndPoint)second.LocalEndpoint;
            Assert(firstEndpoint.Address.Equals(IPAddress.Loopback) && secondEndpoint.Address.Equals(IPAddress.Loopback),
                "browser callback listener escaped the IPv4 loopback interface");
            Assert(firstRedirect.Host == "127.0.0.1" && firstRedirect.AbsolutePath == "/oauth2/callback" &&
                    firstRedirect.Port == firstEndpoint.Port,
                "browser callback did not publish its assigned loopback port");
            Assert(firstRedirect.Port != occupiedPort && secondRedirect.Port != occupiedPort &&
                    firstRedirect.Port != secondRedirect.Port,
                "browser callback reused an occupied or active port");
            AssertThrows<ArgumentException>(
                () => ProductionEnrollment.StartCallbackListener(
                    new Uri("http://127.0.0.1:43971/oauth2/callback"), out _),
                "browser callback accepted a fixed-port template");
            AssertThrows<ArgumentException>(
                () => ProductionEnrollment.StartCallbackListener(
                    new Uri("http://127.0.0.1:80/oauth2/callback"), out _),
                "browser callback accepted an explicit default port");
            AssertThrows<ArgumentException>(
                () => ProductionEnrollment.StartCallbackListener(
                    new Uri("http://localhost/oauth2/callback"), out _),
                "browser callback accepted a non-literal loopback host");
        }

        private static async Task ValidateBrowserCallbackIgnoresLocalProbeAsync()
        {
            const string expectedState = "state-that-must-match-the-browser-callback";
            const string expectedCode = "opaque-authorization-code";
            using var listener = ProductionEnrollment.StartCallbackListener(
                new Uri("http://127.0.0.1/oauth2/callback"), out var redirect);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var receive = ProductionEnrollment.ReceiveAuthorizationCodeAsync(
                listener,
                redirect,
                expectedState,
                timeout.Token);

            var probeResponse = await SendBrowserCallbackAsync(
                redirect,
                "/unrelated-local-probe",
                timeout.Token);
            Assert(probeResponse.Contains("400 Bad Request", StringComparison.Ordinal),
                "an unrelated local probe did not receive a bounded rejection");
            Assert(!receive.IsCompleted,
                "an unrelated local probe consumed the OAuth callback listener");

            var callbackTarget = redirect.AbsolutePath +
                "?code=" + Uri.EscapeDataString(expectedCode) +
                "&state=" + Uri.EscapeDataString(expectedState);
            var callbackResponse = await SendBrowserCallbackAsync(
                redirect,
                callbackTarget,
                timeout.Token);
            Assert(callbackResponse.Contains("200 OK", StringComparison.Ordinal) &&
                    callbackResponse.Contains("Authorization complete", StringComparison.Ordinal),
                "the valid browser callback did not receive the bilingual completion page");
            Assert(await receive == expectedCode,
                "the valid browser callback did not return its authorization code after a probe");
        }

        private static async Task ValidateBrowserCallbackSurvivesEarlyBrowserCloseAsync()
        {
            const string expectedState = "state-bound-to-an-early-closing-browser";
            const string expectedCode = "code-from-an-early-closing-browser";
            using var listener = ProductionEnrollment.StartCallbackListener(
                new Uri("http://127.0.0.1/oauth2/callback"), out var redirect);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var receive = ProductionEnrollment.ReceiveAuthorizationCodeAsync(
                listener,
                redirect,
                expectedState,
                timeout.Token);

            using (var connection = new TcpClient(AddressFamily.InterNetwork))
            {
                await connection.ConnectAsync(IPAddress.Loopback, redirect.Port, timeout.Token);
                using var stream = connection.GetStream();
                var request = Encoding.ASCII.GetBytes(
                    "GET " + redirect.AbsolutePath +
                    "?code=" + Uri.EscapeDataString(expectedCode) +
                    "&state=" + Uri.EscapeDataString(expectedState) +
                    " HTTP/1.1\r\nHost: " + redirect.Authority + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(request, timeout.Token);
            }

            Assert(await receive == expectedCode,
                "a browser closing before the completion page was read discarded the valid authorization code");
        }

        private static async Task<string> SendBrowserCallbackAsync(
            Uri redirect,
            string target,
            CancellationToken token)
        {
            using var connection = new TcpClient(AddressFamily.InterNetwork);
            await connection.ConnectAsync(IPAddress.Loopback, redirect.Port, token);
            using var stream = connection.GetStream();
            var request = Encoding.ASCII.GetBytes(
                "GET " + target + " HTTP/1.1\r\n" +
                "Host: " + redirect.Authority + "\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(request, token);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, false);
            return await reader.ReadToEndAsync(token);
        }

        private static void ValidateSupervisorRecoveryDecisions()
        {
            var transaction = new TermLink.UpdateCore.RemoteUpdateTransactionRecord
            {
                CurrentBuild = 37,
                TargetBuild = 38
            };
            foreach (var state in new[]
            {
                TermLink.UpdateCore.RemoteUpdateTransactionState.Prepared,
                TermLink.UpdateCore.RemoteUpdateTransactionState.SetupRunning,
                TermLink.UpdateCore.RemoteUpdateTransactionState.AwaitingNewAgent
            })
            {
                transaction.State = state;
                Assert(ProductionAgentUpdateWorker.RequiresSupervisorRecovery(transaction, 37),
                    "the old Agent did not recover supervisor state " + state);
                Assert(!ProductionAgentUpdateWorker.RequiresSupervisorRecovery(transaction, 38),
                    "the target Agent tried to rerun old-build supervisor state " + state);
            }
            foreach (var state in new[]
            {
                TermLink.UpdateCore.RemoteUpdateTransactionState.RollingBack,
                TermLink.UpdateCore.RemoteUpdateTransactionState.AwaitingRollbackAgent
            })
            {
                transaction.State = state;
                Assert(ProductionAgentUpdateWorker.RequiresSupervisorRecovery(transaction, 38),
                    "the target Agent did not resume rollback supervisor state " + state);
                Assert(!ProductionAgentUpdateWorker.RequiresSupervisorRecovery(transaction, 37),
                    "the restored Agent tried to rerun completed rollback state " + state);
            }
        }

        private static async Task ValidateVerifiedDownloadPromotionAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "termlink-agent-update-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var destination = Path.Combine(directory, "TermLink-Setup.exe");
                var content = Encoding.UTF8.GetBytes("verified remote update artifact");
                var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                var progress = new List<int>();
                using var input = new MemoryStream(content, writable: false);
                await ProductionAgentUpdateWorker.WriteVerifiedDownloadAsync(
                    input,
                    destination,
                    content.LongLength,
                    digest,
                    value =>
                    {
                        progress.Add(value);
                        return Task.CompletedTask;
                    },
                    CancellationToken.None);
                Assert(File.Exists(destination), "the verified Agent update was not promoted");
                Assert(File.ReadAllBytes(destination).SequenceEqual(content),
                    "the promoted Agent update bytes changed");
                Assert(!File.Exists(destination + ".download"),
                    "the partial Agent update remained after promotion");
                Assert(progress.Count != 0 && progress[^1] == 65,
                    "the verified Agent update did not report final download progress");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }

        private static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class FaultingHttpContent : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
                Task.FromException(new IOException("simulated response body transport failure"));

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }
}
