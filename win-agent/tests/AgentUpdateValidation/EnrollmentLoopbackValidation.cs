using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.AgentUpdate.Validation
{
    internal static class EnrollmentLoopbackValidation
    {
        internal static void Run()
        {
            var temp = Path.GetFullPath(Path.GetTempPath());
            var root = Path.Combine(temp, "termlink-enrollment-loopback-" + Guid.NewGuid().ToString("N"));
            var previousRoot = Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT");
            var previousMarker = Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE");
            Directory.CreateDirectory(root);
            try
            {
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT", root);
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE", Path.Combine(root, "acceptance.cer"));
                using var server = new LoopbackServer();

                ProductionEnrollment.LoginDirect(server.Origin, "Initial PC", "fixture", "fixture-password");
                using var initial = ProductionCredentials.TryLoad();
                var initialConfig = ProductionAgentConfig.TryLoad();
                Require(initial != null && initialConfig != null && initial.LoginGeneration != Guid.Empty &&
                    initialConfig.PcId == server.PcId && initialConfig.KeyEpoch == 7,
                    "first login did not persist the server-resolved machine identity");
                Require(server.StatusRequests == 0 && server.ResolveRequests == 1,
                    "first login unexpectedly reused a device");

                server.Status = DeviceStatus.Revoked;
                server.ExpectedDevice = initial.DeviceId;
                ProductionEnrollment.LoginDirect(server.Origin, "Ignored PC", "fixture", "fixture-password");
                using var replacement = ProductionCredentials.TryLoad();
                var replacementConfig = ProductionAgentConfig.TryLoad();
                Require(replacement != null && replacementConfig != null &&
                    replacement.DeviceId != initial.DeviceId &&
                    replacement.LoginGeneration != initial.LoginGeneration &&
                    replacementConfig.PcId == initialConfig.PcId &&
                    replacementConfig.KeyEpoch == initialConfig.KeyEpoch &&
                    server.StatusRequests == 1 && server.ResolveRequests == 2,
                    "explicit device revocation did not rotate only the device identity");

                var credentialHash = Hash(ProductionCredentials.DefaultPath);
                var configHash = Hash(ProductionAgentConfig.DefaultPath);
                var retainedHash = Hash(ProductionAccountStorage.IdentityStorePath);
                server.Status = DeviceStatus.OtherUnauthorized;
                server.ExpectedDevice = replacement.DeviceId;
                try
                {
                    ProductionEnrollment.LoginDirect(server.Origin, "Ignored PC", "fixture", "fixture-password");
                    throw new InvalidOperationException("non-revocation 401 was accepted as a device revocation");
                }
                catch (ProductionEnrollmentException error) when (error.Code == "ACCOUNT_DEVICE_STATUS_UNAVAILABLE") { }
                using var stillCurrent = ProductionCredentials.TryLoad();
                Require(stillCurrent != null && stillCurrent.DeviceId == replacement.DeviceId &&
                    stillCurrent.LoginGeneration == replacement.LoginGeneration &&
                    Hash(ProductionCredentials.DefaultPath) == credentialHash &&
                    Hash(ProductionAgentConfig.DefaultPath) == configHash &&
                    Hash(ProductionAccountStorage.IdentityStorePath) == retainedHash &&
                    server.StatusRequests == 2 && server.ResolveRequests == 2,
                    "non-revocation 401 changed credentials or machine identity");
                server.CheckHealthy();
                Console.WriteLine("Enrollment loopback: login generation, revoked rotation, PC preservation and non-revocation 401 passed.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT", previousRoot);
                Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE", previousMarker);
                var relative = Path.GetRelativePath(temp, root);
                Require(relative.StartsWith("termlink-enrollment-loopback-", StringComparison.Ordinal) &&
                    !relative.Contains(Path.DirectorySeparatorChar), "Refusing to delete outside the test TEMP root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private enum DeviceStatus { Revoked, OtherUnauthorized }

        private sealed class LoopbackServer : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly Task worker;
            private readonly string accessToken;
            private Exception failure;
            private string machineId;

            internal LoopbackServer()
            {
                using var portReservation = new TcpListener(IPAddress.Loopback, 0);
                portReservation.Start();
                var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
                portReservation.Stop();
                Origin = "http://127.0.0.1:" + port;
                PcId = Guid.NewGuid();
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    iss = Origin, sub = "enrollment-fixture", preferred_username = "fixture"
                });
                accessToken = "e30." + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
                listener.Prefixes.Add(Origin + "/");
                listener.Start();
                worker = ServeAsync();
            }

            internal string Origin { get; }
            internal Guid PcId { get; }
            internal DeviceStatus Status { get; set; }
            internal Guid ExpectedDevice { get; set; }
            internal int StatusRequests { get; private set; }
            internal int ResolveRequests { get; private set; }

            private async Task ServeAsync()
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync().ConfigureAwait(false); }
                    catch (HttpListenerException) when (!listener.IsListening) { break; }
                    catch (ObjectDisposedException) when (!listener.IsListening) { break; }
                    try { Handle(context); }
                    catch (Exception error)
                    {
                        failure = error;
                        context.Response.StatusCode = 500;
                    }
                    finally { context.Response.Close(); }
                }
            }

            private void Handle(HttpListenerContext context)
            {
                var request = context.Request;
                var path = request.Url?.AbsolutePath;
                if (request.HttpMethod == "GET" && path == "/v1/client-config")
                    Respond(context, 200, JsonSerializer.Serialize(new
                    {
                        oidc_issuer = Origin, windows_oidc_client_id = "termlink-windows",
                        windows_redirect_uri = "http://127.0.0.1/oauth2/callback"
                    }));
                else if (request.HttpMethod == "GET" && path == "/.well-known/openid-configuration")
                    Respond(context, 200, JsonSerializer.Serialize(new { token_endpoint = Origin + "/token" }));
                else if (request.HttpMethod == "POST" && path == "/token")
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    var body = reader.ReadToEnd();
                    Require(body.Contains("grant_type=password", StringComparison.Ordinal) &&
                        body.Contains("client_id=termlink-windows", StringComparison.Ordinal),
                        "direct login did not use the expected OIDC token request");
                    Respond(context, 200, JsonSerializer.Serialize(new
                    {
                        access_token = accessToken, refresh_token = "fixture-refresh", expires_in = 3600
                    }));
                }
                else if (request.HttpMethod == "GET" && path == "/v1/devices/self")
                {
                    StatusRequests++;
                    Require(request.Headers["X-TermLink-Device"] == ExpectedDevice.ToString("D") &&
                        request.Headers["X-TermLink-Signature"] != null &&
                        request.Headers["Authorization"] == "Bearer " + accessToken,
                        "retained-device request used the wrong device, token or proof");
                    Respond(context, 401, Status == DeviceStatus.Revoked
                        ? "{\"error\":{\"code\":\"DEVICE_REVOKED\"}}"
                        : "{\"error\":{\"code\":\"UNAUTHENTICATED\"}}");
                }
                else if (request.HttpMethod == "POST" && path == "/v1/pcs/resolve-machine")
                {
                    ResolveRequests++;
                    using var document = JsonDocument.Parse(request.InputStream);
                    var currentMachineId = document.RootElement.GetProperty("machine_id").GetString();
                    Require(!string.IsNullOrWhiteSpace(currentMachineId) &&
                        (machineId == null || machineId == currentMachineId),
                        "machine resolution changed the stable machine ID");
                    machineId = currentMachineId;
                    Respond(context, 200, JsonSerializer.Serialize(new
                    {
                        found = true, pc_id = PcId.ToString("D"), pc_name = "Recovered PC", key_epoch = 7
                    }));
                }
                else Respond(context, 404, "{}");
            }

            private static void Respond(HttpListenerContext context, int status, string json)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes);
            }

            internal void CheckHealthy() => Require(failure == null,
                "loopback server failed: " + failure?.Message);

            public void Dispose()
            {
                listener.Stop();
                listener.Close();
                worker.GetAwaiter().GetResult();
            }
        }
    }
}
