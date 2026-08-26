using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TermLink.Agent;

if (args.Length > 0)
{
    Require(args.Length == 2 && args[0] == "--probe-production-failure", "Invalid validation arguments.");
    ProbeProductionFailure(args[1]);
    return;
}

var sessionId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
var defaultControlPipe = TermLinkControlPipe.DefaultName();
Require(defaultControlPipe.StartsWith("termlink-agent-control-v2-", StringComparison.Ordinal) &&
    defaultControlPipe.Length == "termlink-agent-control-v2-".Length + 16,
    "The integrated runtime did not use the V2 account-scoped control-pipe namespace.");
var deviceId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
var token = Enumerable.Range(0, SessionRegistration.HandshakeTokenBytes).Select(value => (byte)value).ToArray();
var registration = new SessionRegistration(
    sessionId,
    "termlink-codec-validation",
    token,
    Process.GetCurrentProcess().Id,
    ManagedShellType.PowerShell7);

var registrationResult = RoundTripRequest(new RegistryRequest
{
    Command = RegistryCommand.Register,
    Registration = registration
});
Require(registrationResult.Command == RegistryCommand.Register, "Register command changed.");
Require(registrationResult.Registration.SessionId == sessionId, "Register session ID changed.");
Require(registrationResult.Registration.PipeName == registration.PipeName, "Register pipe changed.");
Require(registrationResult.Registration.ExpectedHostProcessId == registration.ExpectedHostProcessId, "Register PID changed.");
Require(registrationResult.Registration.ShellType == ManagedShellType.PowerShell7, "Register shell changed.");
Require(registrationResult.Registration.HandshakeToken.SequenceEqual(token), "Register token changed.");
ValidateAuthenticationAndSecretStore();

foreach (var command in new[]
{
    RegistryCommand.List,
    RegistryCommand.StopAllRemoteAccess,
    RegistryCommand.EnableRemoteAccess,
    RegistryCommand.Shutdown,
    RegistryCommand.RestartAgent,
    RegistryCommand.RefreshProductionStatus,
    RegistryCommand.RetryProductionConnection
})
{
    Require(RoundTripRequest(new RegistryRequest { Command = command }).Command == command, command + " changed.");
}

var stop = RoundTripRequest(new RegistryRequest { Command = RegistryCommand.StopSession, SessionId = sessionId });
Require(stop.SessionId == sessionId, "StopSession ID changed.");
var pairingRequest = RoundTripRequest(new RegistryRequest
{
    Command = RegistryCommand.CreatePairingChallenge,
    DeviceId = deviceId,
    PairingPermission = "CONTROL"
});
Require(pairingRequest.DeviceId == deviceId && pairingRequest.PairingPermission == "CONTROL", "Pairing request changed.");
var directGrantRequest = RoundTripRequest(new RegistryRequest
{
    Command = RegistryCommand.GrantDevice,
    DeviceId = deviceId,
    GrantPermission = "MANAGE"
});
Require(directGrantRequest.DeviceId == deviceId && directGrantRequest.GrantPermission == "MANAGE",
    "Trusted direct grant request changed.");
Require(string.IsNullOrEmpty(directGrantRequest.PairingPermission),
    "Trusted direct grant must not reuse pairing permission semantics.");
var pcId = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
Require(ProductionDeviceGrantContract.NormalizePermission(" control ") == "CONTROL",
    "Direct grant permission normalization changed.");
var grantsPath = ProductionDeviceGrantContract.GrantsPath(pcId);
Require(grantsPath == "/v1/pcs/abcdef01-2345-6789-abcd-ef0123456789/grants",
    "Direct grant endpoint path changed.");
var operationId = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10");
var grantKey = ProductionDeviceGrantContract.IdempotencyKey(pcId, deviceId, "MANAGE", operationId);
Require(grantKey == ProductionDeviceGrantContract.IdempotencyKey(pcId, deviceId, " manage ", operationId) &&
    grantKey == "pc-grant-abcdef0123456789abcdef0123456789-102132435465768798a9bacbdcedfe0f-manage-0102030405060708090a0b0c0d0e0f10",
    "Direct grant idempotency key is not stable within one operation.");
Require(grantKey != ProductionDeviceGrantContract.IdempotencyKey(
        pcId, deviceId, "MANAGE", Guid.Parse("11121314-1516-1718-191a-1b1c1d1e1f20")),
    "A later direct-grant operation must not replay an earlier grant response.");
var invalidPermissionRejected = false;
try { ProductionDeviceGrantContract.NormalizePermission("PAIR"); }
catch (ArgumentException) { invalidPermissionRejected = true; }
Require(invalidPermissionRejected, "Unsupported direct grant permission was accepted.");
var revokeRequest = RoundTripRequest(new RegistryRequest { Command = RegistryCommand.RevokeDevice, DeviceId = deviceId });
Require(revokeRequest.DeviceId == deviceId, "RevokeDevice ID changed.");
var loginRequest = RoundTripRequest(new RegistryRequest
{
    Command = RegistryCommand.LoginAccount,
    AccountUsername = "codec-user",
    AccountPassword = "codec-password",
    PcName = "Codec PC"
});
Require(loginRequest.AccountUsername == "codec-user" && loginRequest.AccountPassword == "codec-password" &&
    loginRequest.PcName == "Codec PC", "Login request changed.");
var registerRequest = RoundTripRequest(new RegistryRequest
{
    Command = RegistryCommand.RegisterAccount,
    AccountUsername = "new-user",
    AccountEmail = "new@example.test",
    AccountPassword = "new-password",
    AccountPasswordConfirmation = "new-password",
    PcName = "Codec PC"
});
Require(registerRequest.AccountEmail == "new@example.test" &&
    registerRequest.AccountPasswordConfirmation == "new-password", "Registration request changed.");
var commandSettingsRequest = RoundTripRequest(new RegistryRequest
{
    Command = RegistryCommand.SaveTerminalCommands,
    TerminalCommands = new List<ProductionTerminalCommandSummary>
    {
        new ProductionTerminalCommandSummary
        {
            Id = "custom-codec",
            DisplayName = "Codec Shell",
            Executable = "codec.exe",
            Arguments = "--codec value",
            Enabled = true
        }
    }
});
Require(commandSettingsRequest.TerminalCommands.Count == 1 &&
    commandSettingsRequest.TerminalCommands[0].Arguments == "--codec value",
    "Terminal-command settings request changed.");

var now = new DateTime(638925120000000000, DateTimeKind.Utc);
var response = new RegistryResponse
{
    Success = true,
    Code = "OK",
    RemoteAccessEnabled = true,
    AffectedSessions = 2,
    TimedOutSessions = 1,
    Summaries = new List<SessionSummary>
    {
        new SessionSummary
        {
            SessionId = sessionId,
            ShellType = ManagedShellType.PowerShell7,
            Lifecycle = SessionAgentLifecycle.Connected,
            HostProcessId = 4242,
            LastSequence = 99,
            RegisteredAtUtc = now,
            LastActivityUtc = now.AddSeconds(5),
            ExitCode = 17
        }
    },
    Production = new ProductionStatus
    {
        PcId = pcId,
        AgentVersion = "0.2.0-preview.4",
        Configured = true,
        AccountLinked = true,
        AccountUsername = "codec-user",
        AccountEmail = "codec@example.test",
        ServerHost = "relay.example.test",
        ServerOrigin = "https://relay.example.test:9443",
        TerminalSnapshotEnabled = true,
        PcName = "Codec PC",
        RelayState = ProductionRelayState.Reconnecting,
        LastErrorCode = "CONNECTION_REFUSED",
        LastConnectedAtUtc = now,
        Devices = new List<ProductionDeviceSummary>
        {
            new ProductionDeviceSummary { DeviceId = deviceId, Type = "ANDROID", Name = "Phone", TrustState = "TRUSTED" }
        },
        Sessions = new List<ProductionSessionSummary>
        {
            new ProductionSessionSummary
            {
                SessionId = sessionId,
                ViewerCount = 2,
                ControllerDeviceId = deviceId,
                LeaseExpiresAtUtc = now.AddMinutes(1)
            }
        },
        TerminalCommands = new List<ProductionTerminalCommandSummary>
        {
            new ProductionTerminalCommandSummary
            {
                Id = "pwsh",
                DisplayName = "PowerShell 7",
                Executable = "C:\\PowerShell\\pwsh.exe",
                Arguments = "-NoLogo -NoProfile",
                ShellType = "powershell-7",
                Detected = true,
                Enabled = true
            }
        },
        PairingChallenge = new PairingChallengeSummary
        {
            ChallengeId = Guid.Parse("fedcba98-7654-3210-ffff-eeeeeeeeeeee"),
            TargetDeviceId = deviceId,
            TargetName = "Phone",
            TargetFingerprint = "0123456789abcdef",
            Permission = "CONTROL",
            Code = "123456",
            ChallengeNonce = "codec-nonce",
            ExpiresAtUtc = now.AddMinutes(5)
        }
    }
};

var decoded = RoundTripResponse(response);
Require(decoded.Success && decoded.Code == "OK" && decoded.RemoteAccessEnabled, "Response header changed.");
Require(decoded.AffectedSessions == 2 && decoded.TimedOutSessions == 1, "Response counts changed.");
Require(decoded.Summaries.Count == 1 && decoded.Summaries[0].ExitCode == 17, "Session summary changed.");
Require(decoded.Production.Configured &&
    decoded.Production.RelayState == ProductionRelayState.Reconnecting &&
    decoded.Production.LastErrorCode == "CONNECTION_REFUSED",
    "Production connection state or failure reason changed.");
Require(decoded.Production.AccountUsername == "codec-user" && decoded.Production.AccountEmail == "codec@example.test", "Account identity changed.");
Require(decoded.Production.ServerOrigin == "https://relay.example.test:9443", "Server origin changed.");
Require(decoded.Production.PcId == pcId && decoded.Production.AgentVersion == "0.2.0-preview.4",
    "V8 PC identity or Agent version changed.");
Require(decoded.Production.TerminalSnapshotEnabled, "Terminal Snapshot capability changed.");
Require(decoded.Production.TerminalCommands.Count == 1 &&
    decoded.Production.TerminalCommands[0].Executable == "C:\\PowerShell\\pwsh.exe",
    "V8 terminal-command settings changed.");
Require(decoded.Production.Devices.Count == 1 && decoded.Production.Devices[0].DeviceId == deviceId, "Device summary changed.");
Require(decoded.Production.Sessions.Count == 1 && decoded.Production.Sessions[0].ControllerDeviceId == deviceId, "Production session changed.");
Require(decoded.Production.PairingChallenge != null &&
    decoded.Production.PairingChallenge.Code == "123456" &&
    decoded.Production.PairingChallenge.Permission == "CONTROL" &&
    decoded.Production.PairingChallenge.TargetFingerprint == "0123456789abcdef", "Pairing challenge changed.");

var defaultProduction = RoundTripResponse(new RegistryResponse
{
    Success = false,
    Code = "NOT_CONFIGURED",
    Summaries = new List<SessionSummary>()
});
Require(defaultProduction.Production.RelayState == ProductionRelayState.NotConfigured, "Default production state changed.");
Require(defaultProduction.Production.Devices.Count == 0 && defaultProduction.Production.Sessions.Count == 0, "Default production collections changed.");
Require(string.IsNullOrEmpty(defaultProduction.Production.ServerOrigin), "Default production status exposed a Server origin.");
Require(!defaultProduction.Production.TerminalSnapshotEnabled, "Default production status enabled Terminal Snapshot.");

using (var legacyRequestStream = new MemoryStream())
{
    RegistryControlCodec.WriteRequest(
        legacyRequestStream,
        new RegistryRequest { Command = RegistryCommand.RefreshProductionStatus },
        5);
    legacyRequestStream.Position = 0;
    byte legacyRequestVersion;
    var legacyRequest = RegistryControlCodec.ReadRequest(legacyRequestStream, out legacyRequestVersion);
    Require(legacyRequestVersion == 5 && legacyRequest.Command == RegistryCommand.RefreshProductionStatus,
        "Pipe V5 request compatibility changed.");
}

using (var legacyV7RequestStream = new MemoryStream())
{
    var legacySecret = Enumerable.Range(0, RegistryControlAuthentication.SecretBytes)
        .Select(value => (byte)(value + 7))
        .ToArray();
    var legacyV7 = new RegistryRequest { Command = RegistryCommand.List };
    RegistryControlAuthentication.Prepare(legacyV7, legacySecret, DateTime.UtcNow, 7);
    RegistryControlCodec.WriteRequest(legacyV7RequestStream, legacyV7, 7);
    legacyV7RequestStream.Position = 0;
    byte legacyV7Version;
    var decodedLegacyV7 = RegistryControlCodec.ReadRequest(legacyV7RequestStream, out legacyV7Version);
    Require(legacyV7Version == 7 &&
        RegistryControlAuthentication.Verify(decodedLegacyV7, legacySecret, DateTime.UtcNow),
        "Authenticated Pipe V7 request compatibility changed.");
    Array.Clear(legacySecret, 0, legacySecret.Length);
}

using (var legacyResponseStream = new MemoryStream())
{
    RegistryControlCodec.WriteResponse(legacyResponseStream, response, 5);
    legacyResponseStream.Position = 0;
    var legacyResponse = RegistryControlCodec.ReadResponse(legacyResponseStream);
    Require(string.IsNullOrEmpty(legacyResponse.Production.ServerOrigin),
        "Pipe V5 response synthesized a Server origin.");
    Require(!legacyResponse.Production.TerminalSnapshotEnabled,
        "Pipe V5 response synthesized Terminal Snapshot capability.");
}

Console.WriteLine("TermLink Agent control Pipe V8 authentication and V7/V6/V5 compatibility validation passed.");

static RegistryRequest RoundTripRequest(RegistryRequest request)
{
    var secret = Enumerable.Range(0, RegistryControlAuthentication.SecretBytes)
        .Select(value => (byte)(value + 1))
        .ToArray();
    RegistryControlAuthentication.Prepare(request, secret, DateTime.UtcNow);
    using var stream = new MemoryStream();
    RegistryControlCodec.WriteRequest(stream, request);
    stream.Position = 0;
    var decoded = RegistryControlCodec.ReadRequest(stream);
    Require(RegistryControlAuthentication.Verify(decoded, secret, DateTime.UtcNow),
        "V7 request authentication changed during codec round trip.");
    Array.Clear(secret, 0, secret.Length);
    return decoded;
}

static void ValidateAuthenticationAndSecretStore()
{
    var secret = Enumerable.Range(0, RegistryControlAuthentication.SecretBytes)
        .Select(value => (byte)(255 - value))
        .ToArray();
    var request = new RegistryRequest { Command = RegistryCommand.List };
    var now = DateTime.UtcNow;
    RegistryControlAuthentication.Prepare(request, secret, now);
    Require(RegistryControlAuthentication.Verify(request, secret, now),
        "A valid V7 authentication tag was rejected.");
    var replay = new RegistryControlReplayWindow();
    Require(replay.TryAccept(request, now) && !replay.TryAccept(request, now),
        "A V7 nonce replay was accepted.");
    request.AuthenticationTag[0] ^= 0x80;
    Require(!RegistryControlAuthentication.Verify(request, secret, now),
        "A modified V7 authentication tag was accepted.");
    request.AuthenticationTag[0] ^= 0x80;
    Require(!RegistryControlAuthentication.Verify(
            request,
            secret,
            now + RegistryControlAuthentication.MaximumClockSkew + TimeSpan.FromSeconds(1)),
        "An expired V7 request timestamp was accepted.");
    var response = new RegistryResponse { Success = true, Code = "OK" };
    RegistryControlAuthentication.PrepareResponse(response, request, secret);
    Require(RegistryControlAuthentication.VerifyResponse(response, request, secret),
        "A valid V7 server response proof was rejected.");
    response.Code = "TAMPERED";
    Require(!RegistryControlAuthentication.VerifyResponse(response, request, secret),
        "A V7 response with a modified body was accepted.");
    response.Code = "OK";
    response.AuthenticationTag[0] ^= 0x40;
    Require(!RegistryControlAuthentication.VerifyResponse(response, request, secret),
        "A modified V7 server response proof was accepted.");

    var root = Path.Combine(Path.GetTempPath(), "TermLink-AgentControlValidation-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(root, "control.secret");
    Directory.CreateDirectory(root);
    try
    {
        var created = RegistryControlSecretStore.GetOrCreate(path);
        var loaded = RegistryControlSecretStore.Load(path);
        Require(created.SequenceEqual(loaded) && created.Length == RegistryControlAuthentication.SecretBytes,
            "The DPAPI control secret did not round trip.");
        Array.Clear(created, 0, created.Length);
        Array.Clear(loaded, 0, loaded.Length);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Array.Clear(secret, 0, secret.Length);
    }
}

static RegistryResponse RoundTripResponse(RegistryResponse response)
{
    response.AuthenticationNonce = new byte[RegistryControlAuthentication.NonceBytes];
    response.AuthenticationTag = new byte[RegistryControlAuthentication.TagBytes];
    using var stream = new MemoryStream();
    RegistryControlCodec.WriteResponse(stream, response);
    stream.Position = 0;
    return RegistryControlCodec.ReadResponse(stream);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void ProbeProductionFailure(string pipeName)
{
    var refresh = RegistryControlClient.Send(
        pipeName,
        new RegistryRequest { Command = RegistryCommand.RefreshProductionStatus },
        15_000);
    Require(!refresh.Success && refresh.Code == "PRODUCTION_REQUEST_FAILED", "Production refresh did not return a stable request failure.");

    var list = RegistryControlClient.Send(
        pipeName,
        new RegistryRequest { Command = RegistryCommand.List },
        5_000);
    Require(list.Success && list.Code == "OK", "Registry stopped after a production refresh failure.");

    var shutdown = RegistryControlClient.Send(
        pipeName,
        new RegistryRequest { Command = RegistryCommand.Shutdown },
        5_000);
    Require(shutdown.Success, "Registry did not shut down after the production failure probe.");
    Console.WriteLine("TermLink production refresh failure isolation validation passed.");
}
