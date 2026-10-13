using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.Protocol;

namespace TermLink.Agent
{
    internal static class ClipboardLeaseValidation
    {
        internal static void Run()
        {
            var id = Guid.NewGuid(); using var registry = new SessionRegistry(new FakeManagedSessionFactory(), 2);
            Require(registry.TryRegister(new SessionRegistration(id, "termlink-clipboard-lease-" + id.ToString("N"), new byte[32],
                Environment.ProcessId, ManagedShellType.Custom), out _), "Lease fixture registration failed.");
            var runtime = (ProductionAgentRuntime)RuntimeHelpers.GetUninitializedObject(typeof(ProductionAgentRuntime));
            var credentials = (ProductionCredentials)RuntimeHelpers.GetUninitializedObject(typeof(ProductionCredentials));
            using var identity = DeviceIdentity.Generate();
            Set(credentials, "tokenSync", new object());
            Property(credentials, "AccessToken", "unit-test-token"); Property(credentials, "DeviceId", Guid.NewGuid());
            Property(credentials, "AccessTokenExpiresAtUtc", DateTime.MaxValue); Property(credentials, "Identity", identity);
            var config = new ProductionAgentConfig(); Property(config, "ServerUrl", "https://unit.test");
            using var cancellation = new CancellationTokenSource();
            using var handler = new LeaseHandler(id); using var http = new HttpClient(handler);
            Set(runtime, "registry", registry); Set(runtime, "config", config); Set(runtime, "credentials", credentials);
            Set(runtime, "http", http); Set(runtime, "cancellation", cancellation);
            bool Check() => runtime.AuthorizeLocalClipboardAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            handler.Status = HttpStatusCode.NotFound; handler.Body = "{\"error\":{\"code\":\"LEASE_NOT_FOUND\"}}";
            Require(Check() && Check() && handler.Calls == 2, "Local clipboard must query fresh authorization twice.");
            handler.Status = HttpStatusCode.OK; handler.Body = "{\"holder_device_id\":\"" + Guid.NewGuid() + "\"}";
            Require(!Check(), "Existing remote lease authorized local clipboard.");
            handler.Status = HttpStatusCode.NotFound; handler.Body = "{\"error\":{\"code\":\"SESSION_NOT_FOUND\"}}";
            Require(!Check(), "Missing session was treated as no lease.");
            handler.Status = HttpStatusCode.Forbidden; Require(!Check(), "Forbidden session authorized clipboard.");
            handler.Status = HttpStatusCode.ServiceUnavailable; Require(!Check(), "Unavailable server authorized clipboard.");
            var before = handler.Calls; Set(runtime, "reauthenticationRequired", 1);
            Require(!Check() && handler.Calls == before, "Expired login attempted authorization."); Set(runtime, "reauthenticationRequired", 0);
            Require(!runtime.AuthorizeLocalClipboardAsync(Guid.NewGuid(), CancellationToken.None).GetAwaiter().GetResult(), "Unknown session authorized.");
            Console.WriteLine("Clipboard lease: fresh authenticated GET, exact no-lease code, active remote lease, missing session, forbidden/unavailable and login loss passed.");
        }
        private static void Set(object obj, string name, object value) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj,value);
        private static void Property(object obj, string name, object value) => obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(obj,value);
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private sealed class LeaseHandler : HttpMessageHandler
        {
            private readonly Guid id;
            internal int Calls;
            internal HttpStatusCode Status;
            internal string Body = "{}";
            internal LeaseHandler(Guid id) { this.id=id; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                Require(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/sessions/" + id.ToString("D") + "/lease" &&
                    request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Contains("X-TermLink-Signature"), "Unsigned or mutating clipboard authorization.");
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
            }
        }
    }
}
