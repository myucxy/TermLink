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

foreach (var command in new[]
{
    RegistryCommand.List,
    RegistryCommand.StopAllRemoteAccess,
    RegistryCommand.EnableRemoteAccess,
    RegistryCommand.Shutdown,
    RegistryCommand.RestartAgent,
    RegistryCommand.RefreshProductionStatus
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
var revokeRequest = RoundTripRequest(new RegistryRequest { Command = RegistryCommand.RevokeDevice, DeviceId = deviceId });
Require(revokeRequest.DeviceId == deviceId, "RevokeDevice ID changed.");

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
        Configured = true,
        AccountLinked = true,
        ServerHost = "relay.example.test",
        PcName = "Codec PC",
        RelayState = ProductionRelayState.Connected,
        LastErrorCode = string.Empty,
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
Require(decoded.Production.Configured && decoded.Production.RelayState == ProductionRelayState.Connected, "Production state changed.");
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

Console.WriteLine("TermLink Agent control Pipe V4 round-trip validation passed.");

static RegistryRequest RoundTripRequest(RegistryRequest request)
{
    using var stream = new MemoryStream();
    RegistryControlCodec.WriteRequest(stream, request);
    stream.Position = 0;
    return RegistryControlCodec.ReadRequest(stream);
}

static RegistryResponse RoundTripResponse(RegistryResponse response)
{
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
