using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

return await RelayHarness.RunAsync(args);

internal static class RelayHarness
{
    private const string Token = "termlink-e2e-local-token-v1";
    private const string KeyId = "test-key-01";
    private static readonly string[] Capabilities =
    {
        "cumulative_ack", "e2ee_x25519_chacha20poly1305", "flow_control_v1", "lease_v1", "bounded_event_replay_v1",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
            var root = options.Root.ToString().TrimEnd('/');
            using var windows = Device.Create("WINDOWS_PC", "E2E Windows", "windows-e2e");
            using var android = Device.Create("ANDROID", "E2E Android surrogate", "android-e2e");
            await RegisterAsync(http, root, windows, timeout.Token);
            await RegisterAsync(http, root, android, timeout.Token);
            var pcId = Guid.NewGuid();
            await SignedJsonAsync(http, root, windows, HttpMethod.Put, $"/v1/pcs/{pcId:D}",
                JsonSerializer.SerializeToUtf8Bytes(new { name = "E2E Windows", agent_version = "staged-harness", key_epoch = 1 }), timeout.Token);
            var sessionA = Guid.NewGuid();
            var sessionB = Guid.NewGuid();
            await UpsertSessionAsync(http, root, windows, pcId, sessionA, timeout.Token);
            await UpsertSessionAsync(http, root, windows, pcId, sessionB, timeout.Token);
            var leaseA = await AcquireLeaseAsync(http, root, android, sessionA, timeout.Token);
            var leaseB = await AcquireLeaseAsync(http, root, android, sessionB, timeout.Token);
            // Each socket is independent, but the server intentionally indexes
            // subscriptions by device identity. Run the two session checks in
            // sequence so reconnect cleanup cannot race a second peer for the
            // same Android surrogate identity; both subscriptions remain real.
            var cases = new[]
            {
                await RunSessionAsync(options.Root, windows, android, sessionA, leaseA, "session-a", timeout.Token),
                await RunSessionAsync(options.Root, windows, android, sessionB, leaseB, "session-b", timeout.Token),
            };
            var assertions = new[]
            {
                Assertion.Equal("device_registration_and_catalog", true, cases.All(item => item.Registered)),
                Assertion.Equal("production_protobuf_relay_forwarding", true, cases.All(item => item.ForwardedFrames >= 7)),
                Assertion.Equal("lease_enforced_input_and_resize", true, cases.All(item => item.InputForwarded && item.ResizeForwarded)),
                Assertion.Equal("opaque_ciphertext_preserved", true, cases.All(item => item.CiphertextPreserved)),
                Assertion.Equal("controlled_android_reconnect", true, cases.All(item => item.ReconnectForwarded)),
                Assertion.Equal("two_session_subscriptions_completed", true, cases.Length == 2),
            };
            var passed = cases.All(item => item.Passed) && assertions.All(item => item.Passed);
            var report = new
            {
                schema = "termlink.e2e-technical-validation", version = 1, generated_at_utc = DateTimeOffset.UtcNow,
                status = passed ? "passed" : "failed", product_e2e = false,
                classification = "real e2e-only Go server plus production protobuf/opaque relay client; Android APK, live ConPTY and E2EE payload execution are not exercised",
                boundaries = new
                {
                    server = "real_e2e_only_process", network = "real_loopback_websocket", wire = "production_protobuf_envelope",
                    android_client = "net8_generated_identity_surrogate", windows_client = "net8_generated_identity_surrogate",
                    android_apk = "not_exercised", windows_conpty = "not_exercised", e2ee_payload_semantics = "opaque_ciphertext_only",
                },
                sessions = cases, assertions,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ResultPath))!);
            await File.WriteAllTextAsync(options.ResultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine($"__E2E_RELAY_RESULT__:status={(passed ? "passed" : "failed")};sessions={cases.Length};result={Path.GetFullPath(options.ResultPath)}");
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("E2E technical-validation relay failed: " + error);
            return 1;
        }
    }

    private static async Task RegisterAsync(HttpClient http, string root, Device device, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signing = Base64Url(device.Identity.SigningPublicKey);
        var exchange = Base64Url(device.Identity.Exchange.PublicKey);
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var canonical = string.Join("\n", "TERMLINK-DEVICE-REGISTER-V1", device.Id.ToString("D"), device.Type, device.Name,
            signing, exchange, device.KeyId, now.ToString(), nonce);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = device.Id, type = device.Type, name = device.Name, signing_public_key = signing, exchange_public_key = exchange,
            key_id = device.KeyId, timestamp_ms = now, nonce, signature = Base64Url(device.Identity.Sign(Encoding.UTF8.GetBytes(canonical))),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, root + "/v1/devices/register") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await http.SendAsync(request, token);
        await EnsureSuccessAsync(response, "device registration");
        device.Registered = true;
    }

    private static async Task UpsertSessionAsync(HttpClient http, string root, Device windows, Guid pcId, Guid sessionId, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { pc_id = pcId.ToString("D"), shell_type = "powershell", title_cipher = "", state = "RUNNING", columns = 80, rows = 24, last_output_seq = 0 });
        await SignedJsonAsync(http, root, windows, HttpMethod.Put, $"/v1/sessions/{sessionId:D}", body, token);
    }

    private static async Task<Lease> AcquireLeaseAsync(HttpClient http, string root, Device android, Guid sessionId, CancellationToken token)
    {
        using var response = await SignedAsync(http, root, android, HttpMethod.Post, $"/v1/sessions/{sessionId:D}/lease", Array.Empty<byte>(), token);
        await EnsureSuccessAsync(response, "lease acquisition");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return new Lease(Guid.Parse(document.RootElement.GetProperty("lease_id").GetString()!), document.RootElement.GetProperty("version").GetInt64());
    }

    private static async Task<SessionResult> RunSessionAsync(Uri root, Device windows, Device android, Guid sessionId, Lease lease, string name, CancellationToken token)
    {
        using var pc = await ConnectAsync(root, windows, "pc", token);
        using var firstAndroid = await ConnectAsync(root, android, "android", token);
        var first = await ExerciseAsync(pc, firstAndroid, windows, android, sessionId, lease, token);
        await firstAndroid.CloseAsync(WebSocketCloseStatus.NormalClosure, "controlled reconnect", token);
        using var secondAndroid = await ConnectAsync(root, android, "android", token);
        var reconnect = MakeEnvelope(MessageType.Subscribe, Direction.AndroidToPc, sessionId, android.Id, windows.Id, 1, 0, Payload(0x31));
        await SendAsync(secondAndroid, reconnect, token);
        var reconnectAtPc = await ReceiveExpectedAsync(pc, MessageType.Subscribe, token);
        var reconnectForwarded = reconnectAtPc.MessageType == MessageType.Subscribe && reconnectAtPc.Seq == 1;
        return first with { Id = name, ReconnectForwarded = reconnectForwarded, Passed = first.Passed && reconnectForwarded };
    }

    private static async Task<SessionResult> ExerciseAsync(ClientWebSocket pc, ClientWebSocket android, Device windows, Device androidDevice, Guid sessionId, Lease lease, CancellationToken token)
    {
        var subscribe = MakeEnvelope(MessageType.Subscribe, Direction.AndroidToPc, sessionId, androidDevice.Id, windows.Id, 1, 0, Payload(0x11));
        await SendAsync(android, subscribe, token);
        var gotSubscribe = await ReceiveExpectedAsync(pc, MessageType.Subscribe, token);
        var input = MakeEnvelope(MessageType.InputEvent, Direction.AndroidToPc, sessionId, androidDevice.Id, windows.Id, 2, 0, Payload(0x22, lease.Id, lease.Version));
        await SendAsync(android, input, token);
        var gotInput = await ReceiveExpectedAsync(pc, MessageType.InputEvent, token);
        var resize = MakeEnvelope(MessageType.ResizeRequest, Direction.AndroidToPc, sessionId, androidDevice.Id, windows.Id, 3, 0, Payload(0x33, lease.Id, lease.Version));
        await SendAsync(android, resize, token);
        var gotResize = await ReceiveExpectedAsync(pc, MessageType.ResizeRequest, token);
        var output = MakeEnvelope(MessageType.OutputChunk, Direction.PcToAndroid, sessionId, windows.Id, androidDevice.Id, 1, 0, Payload(0x44));
        await SendAsync(pc, output, token);
        var gotOutput = await ReceiveExpectedAsync(android, MessageType.OutputChunk, token);
        var accepted = MakeEnvelope(MessageType.InputAccepted, Direction.PcToAndroid, sessionId, windows.Id, androidDevice.Id, 2, 0, Payload(0x55));
        await SendAsync(pc, accepted, token);
        var gotAccepted = await ReceiveExpectedAsync(android, MessageType.InputAccepted, token);
        var applied = MakeEnvelope(MessageType.ResizeApplied, Direction.PcToAndroid, sessionId, windows.Id, androidDevice.Id, 3, 0, Payload(0x66));
        await SendAsync(pc, applied, token);
        var gotApplied = await ReceiveExpectedAsync(android, MessageType.ResizeApplied, token);
        var ack = MakeEnvelope(MessageType.OutputAck, Direction.AndroidToPc, sessionId, androidDevice.Id, windows.Id, 4, 3, Payload(0x77));
        await SendAsync(android, ack, token);
        var gotAck = await ReceiveExpectedAsync(pc, MessageType.OutputAck, token);
        var forwarded = new[] { gotSubscribe, gotInput, gotResize, gotOutput, gotAccepted, gotApplied, gotAck };
        var wanted = new[] { subscribe, input, resize, output, accepted, applied, ack };
        var preservedChecks = forwarded.Zip(wanted, (actual, expected) => actual.Ciphertext.Span.SequenceEqual(expected.Ciphertext.Span)).ToArray();
        var preserved = preservedChecks.All(value => value);
        return new SessionResult
        {
            Id = "session", Passed = forwarded.Length == 7 && preserved, Registered = windows.Registered && androidDevice.Registered,
            ForwardedFrames = forwarded.Length, InputForwarded = gotInput.MessageType == MessageType.InputEvent,
            ResizeForwarded = gotResize.MessageType == MessageType.ResizeRequest && gotApplied.MessageType == MessageType.ResizeApplied,
            CiphertextPreserved = preserved, ReconnectForwarded = false,
            Reality = "real production protobuf relay forwarding; payload ciphertext is synthetic and opaque; no APK or live shell",
            Observed = string.Join(",", forwarded.Select((item, index) => $"{item.MessageType}:{item.Seq}:{item.Ciphertext.Length}:{(item.Ciphertext.Length > 0 ? item.Ciphertext[0] : 0)}:{preservedChecks[index]}")),
            Errors = Array.Empty<string>(),
        };
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri root, Device device, string role, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("termlink.realtime.v1");
        AddProofHeaders(socket.Options, device, HttpMethod.Get, "/v1/realtime", Array.Empty<byte>());
        var scheme = root.Scheme == Uri.UriSchemeHttps ? Uri.UriSchemeWss : Uri.UriSchemeWs;
        var websocketRoot = new UriBuilder(root) { Scheme = scheme }.Uri;
        await socket.ConnectAsync(new Uri(websocketRoot, "/v1/realtime"), token);
        var hello = JsonSerializer.Serialize(new { role, protocol_versions = new[] { 1 }, capabilities = Capabilities });
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, token);
        var welcome = await ReceiveTextAsync(socket, token);
        if (!welcome.Contains("\"type\":\"welcome\"", StringComparison.Ordinal) || socket.SubProtocol != "termlink.realtime.v1")
        {
            socket.Dispose();
            throw new InvalidDataException("Production relay welcome or subprotocol was invalid.");
        }
        return socket;
    }

    private static void AddProofHeaders(ClientWebSocketOptions options, Device device, HttpMethod method, string target, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", method.Method.ToUpperInvariant(), target, timestamp, nonce, digest);
        options.SetRequestHeader("Authorization", "Bearer " + Token);
        options.SetRequestHeader("X-TermLink-Device", device.Id.ToString("D"));
        options.SetRequestHeader("X-TermLink-Timestamp", timestamp);
        options.SetRequestHeader("X-TermLink-Nonce", nonce);
        options.SetRequestHeader("X-TermLink-Signature", Base64Url(device.Identity.Sign(Encoding.UTF8.GetBytes(canonical))));
    }

    private static async Task<HttpResponseMessage> SignedAsync(HttpClient http, string root, Device device, HttpMethod method, string target, byte[] body, CancellationToken token)
    {
        var request = new HttpRequestMessage(method, root + target) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = string.Join("\n", "TERMLINK-DEVICE-PROOF-V1", method.Method.ToUpperInvariant(), target, timestamp, nonce, digest);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Add("X-TermLink-Device", device.Id.ToString("D"));
        request.Headers.Add("X-TermLink-Timestamp", timestamp);
        request.Headers.Add("X-TermLink-Nonce", nonce);
        request.Headers.Add("X-TermLink-Signature", Base64Url(device.Identity.Sign(Encoding.UTF8.GetBytes(canonical))));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static async Task SignedJsonAsync(HttpClient http, string root, Device device, HttpMethod method, string target, byte[] body, CancellationToken token)
    {
        using var response = await SignedAsync(http, root, device, method, target, body, token);
        await EnsureSuccessAsync(response, target);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException($"{operation} failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task SendAsync(ClientWebSocket socket, Envelope envelope, CancellationToken token) => await socket.SendAsync(envelope.ToByteArray(), WebSocketMessageType.Binary, true, token);
    private static async Task<Envelope> ReceiveEnvelopeAsync(ClientWebSocket socket, CancellationToken token) => Envelope.Parser.ParseFrom(await ReceiveBytesAsync(socket, token));
    private static async Task<Envelope> ReceiveExpectedAsync(ClientWebSocket socket, MessageType expected, CancellationToken token)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var envelope = await ReceiveEnvelopeAsync(socket, token);
            if (envelope.MessageType == expected) return envelope;
        }
        throw new InvalidDataException($"Relay did not forward expected {expected} frame.");
    }
    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken token) => Encoding.UTF8.GetString(await ReceiveBytesAsync(socket, token));
    private static async Task<byte[]> ReceiveBytesAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Relay closed the connection.");
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), token);
            if (result.EndOfMessage) return stream.ToArray();
        }
    }

    private static Envelope MakeEnvelope(MessageType type, Direction direction, Guid session, Guid sender, Guid recipient, ulong sequence, ulong ack, byte[] ciphertext) => new()
    {
        ProtocolVersion = 1, MessageType = type, SessionId = ByteString.CopyFrom(GuidBytes(session)), SenderDeviceId = ByteString.CopyFrom(GuidBytes(sender)),
        RecipientDeviceId = ByteString.CopyFrom(GuidBytes(recipient)), Direction = direction, Seq = sequence, Ack = ack,
        SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), KeyId = ByteString.CopyFromUtf8(KeyId),
        Nonce = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)), Ciphertext = ByteString.CopyFrom(ciphertext),
    };

    private static byte[] Payload(byte marker, Guid? leaseId = null, long? version = null)
    {
        var bytes = new byte[32]; bytes[0] = marker;
        if (leaseId.HasValue) GuidBytes(leaseId.Value).CopyTo(bytes, 1);
        if (version.HasValue) BitConverter.GetBytes(version.Value).CopyTo(bytes, 17);
        return bytes;
    }

    private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Device : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid(); public string Type { get; } public string Name { get; } public string KeyId { get; }
        public DeviceIdentity Identity { get; } public bool Registered { get; set; }
        private Device(string type, string name, string keyId) { Type = type; Name = name; KeyId = keyId; Identity = DeviceIdentity.Generate(); }
        public static Device Create(string type, string name, string keyId) => new(type, name, keyId); public void Dispose() => Identity.Dispose();
    }

    private readonly record struct Lease(Guid Id, long Version);
    private sealed record SessionResult
    {
        public required string Id { get; init; } public bool Passed { get; init; } public bool Registered { get; init; } public int ForwardedFrames { get; init; }
        public bool InputForwarded { get; init; } public bool ResizeForwarded { get; init; } public bool CiphertextPreserved { get; init; } public bool ReconnectForwarded { get; init; }
        public required string Reality { get; init; } public required string[] Errors { get; init; }
        public required string Observed { get; init; }
    }
    private sealed record Assertion(string Id, bool Passed, object Expected, object Actual) { public static Assertion Equal(string id, object expected, object actual) => new(id, Equals(expected, actual), expected, actual); }
    private sealed record Options(Uri Root, string ResultPath, int TimeoutSeconds)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i += 2) { if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Arguments must be --name value pairs."); values[args[i]] = args[i + 1]; }
            var root = new Uri(Required(values, "--root"), UriKind.Absolute); if (root.Scheme is not "http" and not "https") throw new ArgumentException("--root must be http:// or https://.");
            var seconds = values.TryGetValue("--timeout-seconds", out var raw) && int.TryParse(raw, out var parsed) ? parsed : 45; if (seconds is < 10 or > 300) throw new ArgumentOutOfRangeException("--timeout-seconds");
            return new Options(root, Path.GetFullPath(Required(values, "--result")), seconds);
        }
        private static string Required(IReadOnlyDictionary<string, string> values, string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException(name + " is required.");
    }
}
