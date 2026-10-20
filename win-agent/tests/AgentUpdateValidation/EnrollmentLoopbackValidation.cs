using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TermLink.Agent;
using TermLink.Protocol;

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
                server.ExpectedIdentity = initial.Identity;
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
                server.ExpectedIdentity = replacement.Identity;
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

                foreach (var status in new[] { DeviceStatus.Disabled, DeviceStatus.Malformed, DeviceStatus.Unavailable })
                {
                    server.Status = status;
                    var registrationsBefore = server.RegistrationRequests;
                    ExpectRejectedLogin(server);
                    Require(server.RegistrationRequests == registrationsBefore &&
                        Hash(ProductionCredentials.DefaultPath) == credentialHash &&
                        Hash(ProductionAgentConfig.DefaultPath) == configHash &&
                        Hash(ProductionAccountStorage.IdentityStorePath) == retainedHash,
                        "disabled/malformed/unavailable status attempted recovery or changed local account files");
                }

                foreach (var registrationStatus in new[] { 401, 403, 409, 500, 200 })
                {
                    server.Status = DeviceStatus.Missing;
                    server.RegistrationStatus = registrationStatus;
                    var registrationsBefore = server.RegistrationRequests;
                    var statusBefore = server.StatusRequests;
                    ExpectRejectedLogin(server);
                    Require(server.RegistrationRequests == registrationsBefore + 1 &&
                        server.StatusRequests == statusBefore + 1 &&
                        Hash(ProductionCredentials.DefaultPath) == credentialHash &&
                        Hash(ProductionAgentConfig.DefaultPath) == configHash &&
                        Hash(ProductionAccountStorage.IdentityStorePath) == retainedHash,
                        "rejected registration was accepted, retried or changed local account files");
                }

                server.Status = DeviceStatus.Missing;
                server.RegistrationStatus = 201;
                server.RejectRecheck = true;
                var recheckRegistrations = server.RegistrationRequests;
                var recheckStatuses = server.StatusRequests;
                ExpectRejectedLogin(server);
                Require(server.RegistrationRequests == recheckRegistrations + 1 && server.StatusRequests == recheckStatuses + 2 &&
                    Hash(ProductionCredentials.DefaultPath) == credentialHash &&
                    Hash(ProductionAgentConfig.DefaultPath) == configHash &&
                    Hash(ProductionAccountStorage.IdentityStorePath) == retainedHash,
                    "successful registration without a verified device status changed local files or retried indefinitely");

                server.Status = DeviceStatus.Missing;
                server.RejectRecheck = false;
                var restoreRegistrations = server.RegistrationRequests;
                var restoreStatuses = server.StatusRequests;
                ProductionEnrollment.LoginDirect(server.Origin, "Ignored PC", "fixture", "fixture-password");
                using var restored = ProductionCredentials.TryLoad();
                var restoredConfig = ProductionAgentConfig.TryLoad();
                Require(restored.DeviceId == replacement.DeviceId &&
                    restored.Identity.SigningPublicKey.SequenceEqual(replacement.Identity.SigningPublicKey) &&
                    restored.Identity.Exchange.PublicKey.SequenceEqual(replacement.Identity.Exchange.PublicKey) &&
                    restored.LoginGeneration != replacement.LoginGeneration &&
                    restoredConfig.PcId == replacementConfig.PcId && restoredConfig.KeyEpoch == replacementConfig.KeyEpoch &&
                    server.RegistrationRequests == restoreRegistrations + 1 && server.StatusRequests == restoreStatuses + 2 &&
                    server.ResolveRequests == 2,
                    "missing-device recovery did not retain the original device keys, PC and epoch");

                ProductionEnrollment.ForgetLocalAccount();
                Require(!File.Exists(ProductionCredentials.DefaultPath) && !File.Exists(ProductionAgentConfig.DefaultPath) &&
                    File.Exists(ProductionAccountStorage.IdentityStorePath), "sign-out did not preserve only retained identities");
                server.Status = DeviceStatus.Missing;
                server.RequireLegacyRegistration = true;
                server.ExpectedIdentity = restored.Identity;
                var retainedRegistrations = server.RegistrationRequests;
                var retainedStatuses = server.StatusRequests;
                ProductionEnrollment.LoginDirect(server.Origin, "Ignored PC", "fixture", "fixture-password");
                using var restoredRetained = ProductionCredentials.TryLoad();
                Require(restoredRetained.DeviceId == restored.DeviceId &&
                    restoredRetained.Identity.SigningPublicKey.SequenceEqual(restored.Identity.SigningPublicKey) &&
                    ProductionAgentConfig.TryLoad().PcId == restoredConfig.PcId &&
                    server.RegistrationRequests == retainedRegistrations + 2 && server.StatusRequests == retainedStatuses + 2 &&
                    server.ResolveRequests == 2,
                    "signed-out retained identity or legacy Windows registration recovery failed");
                server.CheckHealthy();
                Console.WriteLine("Enrollment loopback: missing active/retained identity recovery, exact original keys/PC, legacy fallback, " +
                    "revoked rotation, blocked disabled/conflict/malformed/server errors and failed recheck persistence passed.");
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

        private static void ExpectRejectedLogin(LoopbackServer server)
        {
            try
            {
                ProductionEnrollment.LoginDirect(server.Origin, "Ignored PC", "fixture", "fixture-password");
                throw new InvalidOperationException("unverified retained identity was accepted");
            }
            catch (ProductionEnrollmentException error) when (error.Code == "ACCOUNT_DEVICE_STATUS_UNAVAILABLE") { }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private enum DeviceStatus { Revoked, OtherUnauthorized, Missing, Disabled, Malformed, Unavailable }

        private sealed class LoopbackServer : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly Task worker;
            private readonly string accessToken;
            private Exception failure;
            private string machineId;
            private DeviceStatus status;
            private bool registered;

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
            internal DeviceStatus Status { get => status; set { status = value; registered = false; } }
            internal Guid ExpectedDevice { get; set; }
            internal DeviceIdentity ExpectedIdentity { get; set; }
            internal int RegistrationStatus { get; set; } = 201;
            internal bool RequireLegacyRegistration { get; set; }
            internal bool RejectRecheck { get; set; }
            internal int StatusRequests { get; private set; }
            internal int ResolveRequests { get; private set; }
            internal int RegistrationRequests { get; private set; }

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
                    if (Status == DeviceStatus.Malformed) Respond(context, 401, "not-json");
                    else if (Status == DeviceStatus.Unavailable) Respond(context, 503, "{}");
                    else if (Status == DeviceStatus.Missing && registered && !RejectRecheck) Respond(context, 200, "{}");
                    else Respond(context, 401, JsonSerializer.Serialize(new { error = new { code = Status switch
                    {
                        DeviceStatus.Revoked => "DEVICE_REVOKED", DeviceStatus.Disabled => "DEVICE_DISABLED", _ => "UNAUTHENTICATED"
                    } } }));
                }
                else if (request.HttpMethod == "POST" && path == "/v1/devices/register")
                {
                    RegistrationRequests++;
                    using var document = JsonDocument.Parse(request.InputStream);
                    var body = document.RootElement;
                    string Value(string name) => body.GetProperty(name).GetString();
                    var timestamp = body.GetProperty("timestamp_ms").GetInt64();
                    var deviceType = Value("type");
                    Require(Value("id") == ExpectedDevice.ToString("D") && Value("name") == "Recovered PC" &&
                        request.Headers["Authorization"] == "Bearer " + accessToken &&
                        Value("signing_public_key") == Base64Url(ExpectedIdentity.SigningPublicKey) &&
                        Value("exchange_public_key") == Base64Url(ExpectedIdentity.Exchange.PublicKey) &&
                        Value("key_id") == ProductionAgentRuntime.DeviceKeyId(ExpectedDevice) &&
                        Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp) < 60_000 &&
                        Decode(Value("nonce")).Length == 16,
                        "recovery registration changed device ID, keys, name, token or proof format");
                    var canonical = string.Join("\n", "TERMLINK-DEVICE-REGISTER-V1", Value("id"), deviceType, Value("name"),
                        Value("signing_public_key"), Value("exchange_public_key"), Value("key_id"), timestamp, Value("nonce"));
                    Require(CryptographicOperations.FixedTimeEquals(ExpectedIdentity.Sign(Encoding.UTF8.GetBytes(canonical)),
                        Decode(Value("signature"))), "recovery registration did not prove possession of the original key");
                    if (RequireLegacyRegistration && deviceType == "PC_AGENT")
                        Respond(context, 403, "{\"error\":{\"code\":\"DEVICE_TYPE_NOT_ALLOWED\"}}");
                    else
                    {
                        Require(deviceType == (RequireLegacyRegistration ? "WINDOWS_PC" : "PC_AGENT"), "unexpected registration type");
                        var code = Status == DeviceStatus.Missing ? RegistrationStatus : 409;
                        registered = code == 201;
                        Respond(context, code, code == 201 ? "{}" : "{\"error\":{\"code\":\"CONFLICT\"}}");
                    }
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

            private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - value.Length % 4) % 4));

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
