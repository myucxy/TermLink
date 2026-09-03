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

namespace TermLink.AgentUpdate.Validation
{
    internal static class Program
    {
        public static async Task<int> Main()
        {
            try
            {
                ValidateBrowserAuthorizationLaunch();
                ValidateBrowserCallbackPortFailure();
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

        private static void ValidateBrowserAuthorizationLaunch()
        {
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

        private static void ValidateBrowserCallbackPortFailure()
        {
            using var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Server.ExclusiveAddressUse = true;
            occupied.Start(1);
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            try
            {
                using var unexpected = ProductionEnrollment.StartCallbackListener(
                    new Uri("http://127.0.0.1:" + port + "/oauth2/callback"));
                throw new InvalidOperationException("browser callback accepted an occupied loopback port");
            }
            catch (ProductionEnrollmentException error)
            {
                Assert(error.Code == "ACCOUNT_CALLBACK_PORT_IN_USE",
                    "browser callback port failure lost its stable error code");
            }
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
    }
}
