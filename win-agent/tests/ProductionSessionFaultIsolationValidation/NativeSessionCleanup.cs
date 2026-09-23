using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.LocalIpc;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.ProductionSessionFaultIsolation.Validation
{
    internal static partial class Program
    {
        private sealed class CleanupMirror : HttpMessageHandler
        {
            internal int Requests;
            internal bool FailNext;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/session-deletions"), "Unexpected cleanup transport.");
                using var body = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(token));
                Require(body.RootElement.GetProperty("session_ids").GetArrayLength() is >= 1 and <= 64, "Mirror batch is unbounded.");
                Requests++;
                var status = FailNext ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent;
                FailNext = false;
                return new HttpResponseMessage(status) { Content = new StringContent("{}") };
            }
        }

        private static async Task ValidateNativeSessionCleanupAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "termlink-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var config = new ProductionAgentConfig { PcId = Guid.NewGuid(), MachineId = "cleanup-test-" + Guid.NewGuid().ToString("N"),
                ServerUrl = "https://cleanup.invalid", KeyEpoch = 1 };
            var historyRoot = Path.Combine(Path.GetDirectoryName(ProductionAgentConfig.DefaultPath), "Data", "History", config.MachineId);
            Require(!Directory.Exists(historyRoot), "Test history path already exists.");
            using var credentials = new ProductionCredentials();
            foreach (var item in new (string Name, object Value)[] { ("DeviceId", Guid.NewGuid()), ("Identity", TermLink.Protocol.DeviceIdentity.Generate()),
                ("AccessToken", "cleanup-test"), ("AccessTokenExpiresAtUtc", DateTime.MaxValue) })
                typeof(ProductionCredentials).GetProperty(item.Name).SetValue(credentials, item.Value);
            ProductionAgentRuntime runtime = null;
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(ProductionAgentRuntime);
                runtime = (ProductionAgentRuntime)type.GetConstructor(flags, null,
                    new[] { typeof(ProductionAgentConfig), typeof(ProductionCredentials) }, null).Invoke(new object[] { config, credentials });
                var store = (ProductionTerminalHistoryStore)type.GetField("terminalHistoryStore", flags).GetValue(runtime);
                var sessions = Enumerable.Range(0, 245).Select(_ => Guid.NewGuid()).ToArray();
                var alive = Guid.NewGuid();
                using var process = Process.GetCurrentProcess();
                var registrations = new DpapiRegistryRegistrationStore(Path.Combine(root, "registrations.dpapi"));
                var liveRegistration = new SessionRegistration(alive, "cleanup-test", RandomNumberGenerator.GetBytes(32), process.Id,
                    ManagedShellType.PowerShell7, process.StartTime.ToUniversalTime(), DateTime.UtcNow).AsRetired();
                registrations.Upsert(liveRegistration);
                registrations.Upsert(new SessionRegistration(sessions[0], "cleanup-dead", RandomNumberGenerator.GetBytes(32), int.MaxValue,
                    ManagedShellType.PowerShell7, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow).AsRetired());
                using var registry = new SessionRegistry(root, 1200, 1024 * 1024, 20);
                type.GetField("registry", flags).SetValue(runtime, registry);
                foreach (var id in sessions.Append(alive)) store.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, id, 1, new byte[] { 65 }));
                var httpField = type.GetField("http", flags);
                ((HttpClient)httpField.GetValue(runtime)).Dispose();
                var mirror = new CleanupMirror();
                httpField.SetValue(runtime, new HttpClient(mirror));
                var cleanup = type.GetMethod("CleanupSessionsAsync", flags);
                async Task<SessionCleanupResponsePayload> Run(params Guid[] ids)
                {
                    var request = new SessionCleanupRequestPayload { RequestId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray(true)) };
                    request.SessionIds.AddRange(ids.Select(id => ByteString.CopyFrom(id.ToByteArray(true))));
                    var response = await (Task<SessionCleanupResponsePayload>)cleanup.Invoke(runtime, new object[] { request, CancellationToken.None });
                    Require(response.RequestId == request.RequestId, "Cleanup did not correlate its response.");
                    return response;
                }
                foreach (var invalid in new[] { Array.Empty<Guid>(), new[] { sessions[0], sessions[0] }, new[] { sessions[0], Guid.Empty }, sessions.Take(65).ToArray() })
                {
                    try { await Run(invalid); throw new Exception("Invalid batch accepted."); }
                    catch (InvalidDataException) { }
                }
                Require(!store.IsSessionDeleted(sessions[0]) && mirror.Requests == 0, "Invalid batch partially deleted history.");
                var protectedResult = await Run(alive, Guid.NewGuid());
                Require(protectedResult.Results[0].Status == "SKIPPED_ACTIVE" && protectedResult.Results[1].Status == "NOT_FOUND" &&
                    !store.IsSessionDeleted(alive) && mirror.Requests == 0, "Live host or absent history was deleted.");
                var timer = Stopwatch.StartNew();
                foreach (var batch in sessions.Chunk(64))
                    Require((await Run(batch)).Results.All(item => item.Status == "DELETED"), "Inactive history was not deleted.");
                Require(mirror.Requests == 4 && sessions.All(store.IsSessionDeleted), "245 deletions did not use exactly four mirror requests.");
                Console.WriteLine("Native cleanup: 245 sessions, 4 batches, " + timer.ElapsedMilliseconds + " ms (isolated local mirror).");
                Require(!registry.TryRegister(new SessionRegistration(sessions[0], "late", RandomNumberGenerator.GetBytes(32), process.Id,
                    ManagedShellType.PowerShell7), out _), "Deleted registered ID was reattached.");
                Require(!registry.TryRegister(new SessionRegistration(sessions[1], "late-unregistered", RandomNumberGenerator.GetBytes(32), process.Id,
                    ManagedShellType.PowerShell7), out _), "Deleted history-only ID was reattached.");
                Require((await Run(sessions[0])).Results.Single().Status == "DELETED", "Deletion replay was not idempotent.");
                var pending = Guid.NewGuid();
                store.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, pending, 1, new byte[] { 66 }));
                mirror.FailNext = true;
                Require((await Run(pending)).ErrorCode == "MIRROR_SYNC_PENDING" && store.IsSessionDeleted(pending), "Mirror failure lost durable deletion.");
                var beforeRetry = mirror.Requests;
                await (Task)type.GetMethod("RetrySessionDeletionsAsync", flags).Invoke(runtime, new object[] { CancellationToken.None });
                Require(mirror.Requests == beforeRetry + 1, "Pending tombstone was not retried.");
                await (Task)type.GetMethod("RetrySessionDeletionsAsync", flags).Invoke(runtime, new object[] { CancellationToken.None });
                Require(mirror.Requests == beforeRetry + 1, "Confirmed tombstones were needlessly retried.");
                var tasks = (ConcurrentDictionary<Guid, Task>)type.GetField("historyDeletionTasks", flags).GetValue(runtime);
                await Task.WhenAll(tasks.Values).WaitAsync(TimeSpan.FromSeconds(15));
                File.WriteAllText(Path.Combine(historyRoot, ".delete-x.marker"), "malformed");
                var reopened = new ProductionTerminalHistoryStore(historyRoot, credentials.DeviceId);
                Require(reopened.DeletedSessionIds().Length == 246 && reopened.ContainsSession(alive), "Restart lost tombstones or live history.");
                type.GetField("registry", flags).SetValue(runtime, null);
            }
            finally
            {
                runtime?.Dispose();
                Directory.Delete(root, recursive: true);
                if (Path.GetFileName(historyRoot) == config.MachineId && config.MachineId.StartsWith("cleanup-test-"))
                    Directory.Delete(historyRoot, recursive: true);
            }
        }
    }
}
