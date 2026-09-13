using System.Collections.ObjectModel;
using TermLink.Pc.Contracts;
using TermLink.Presentation;

var identity = new PcAgentIdentity(
    Guid.Parse("11111111-1111-1111-1111-111111111111"),
    "validation-pc",
    PcPlatform.Windows,
    PcArchitecture.X64,
    "Windows validation",
    "0.2.0-preview.1");
var first = Snapshot(1, identity, Session("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "one"));
var second = Snapshot(
    2,
    identity,
    Session("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "two"),
    Session("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "one updated"));
var fake = new FakeAgentClient(first);
await using var store = new AgentStateStore(fake, TimeSpan.FromMinutes(1));
var changes = 0;
store.Changed += (_, _) => changes++;
await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.RefreshAsync()));
Require(fake.StatusCalls == 1, "Concurrent refreshes were not coalesced.");
Require(changes == 1, "The first status was not published exactly once.");
await store.RefreshAsync();
Require(changes == 1, "An unchanged revision was republished.");

var collection = new ObservableCollection<ManagedSessionSnapshot>(first.Sessions);
ObservableCollectionDiff.Apply(collection, second.Sessions, value => value.SessionId);
Require(collection.Count == 2, "Differential collection count is incorrect.");
Require(collection[0].Title == "two" && collection[1].Title == "one updated", "Differential collection order/update is incorrect.");

fake.Status = second;
await store.RefreshAsync();
Require(changes == 2 && store.Current?.Revision == 2, "A changed revision was not published.");

var invalid = second with { TerminalCommands = Enumerable.Range(0, 25)
    .Select(index => new TerminalCommandCapability($"id-{index}", $"name-{index}", "shell", Array.Empty<string>(), true))
    .ToArray() };
var rejected = false;
try { PcContractValidator.Validate(invalid); }
catch (InvalidDataException) { rejected = true; }
Require(rejected, "An oversized terminal command capability set was accepted.");

Require(SessionDataPlaneCarrierContract.ReadFromAgentSummary(new { Carrier = "LAN_DIRECT" }) ==
        SessionDataPlaneCarrier.LanDirect,
    "The optional Agent carrier field did not map LAN direct.");
Require(SessionDataPlaneCarrierContract.ReadFromAgentSummary(new { DataPlaneCarrier = "NAT_DIRECT" }) ==
        SessionDataPlaneCarrier.NatDirect,
    "The optional Agent carrier alias did not map NAT direct.");
Require(SessionDataPlaneCarrierContract.ReadFromAgentSummary(new { Carrier = "LocalAreaNetworkDirect" }) ==
        SessionDataPlaneCarrier.LanDirect,
    "The Windows Agent carrier name did not map LAN direct.");
Require(SessionDataPlaneCarrierContract.ReadFromAgentSummary(new { Carrier = "NatTraversalDirect" }) ==
        SessionDataPlaneCarrier.NatDirect,
    "The Windows Agent carrier name did not map NAT direct.");
Require(SessionDataPlaneCarrierContract.ReadFromAgentSummary(new { Carrier = "future-direct-mode" }) ==
        SessionDataPlaneCarrier.Relay,
    "An unknown Agent carrier was not failed closed to Relay.");
Require(SessionDataPlaneCarrierContract.ReadFromAgentSummary(new { }) ==
        SessionDataPlaneCarrier.Relay,
    "A legacy Agent response without a carrier was not failed closed to Relay.");

Console.WriteLine("PC architecture contracts, refresh coalescing, revision suppression, and differential collections passed.");

static ManagedSessionSnapshot Session(string id, string title) =>
    new(Guid.Parse(id), title, "shell", ManagedSessionState.Connected, 0, DateTimeOffset.UnixEpoch, 1);

static AgentStatusSnapshot Snapshot(long revision, PcAgentIdentity identity, params ManagedSessionSnapshot[] sessions) =>
    new(
        revision,
        DateTimeOffset.UnixEpoch.AddSeconds(revision),
        identity,
        AgentConnectionState.Connected,
        true,
        "validation",
        "Validation User",
        "https://example.invalid",
        true,
        null,
        null,
        new HashSet<string> { PcCapabilityIds.RealtimeTerminalV1 },
        Array.Empty<TerminalCommandCapability>(),
        sessions,
        Array.Empty<AccountDeviceSnapshot>());

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
file sealed class FakeAgentClient(AgentStatusSnapshot status) : IAgentClient
{
    private int statusCalls;
    public int StatusCalls => Volatile.Read(ref statusCalls);
    public AgentStatusSnapshot Status { get; set; } = status;

    public async ValueTask<AgentStatusSnapshot> GetStatusAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref statusCalls);
        await Task.Delay(50, cancellationToken);
        return Status;
    }

    public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask RetryConnectionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StopSessionAsync(Guid sessionId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask GrantDeviceAsync(Guid deviceId, string permission, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask BeginAccountAuthorizationAsync(string pcName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask LoginAsync(string username, string password, string pcName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask RegisterAsync(string username, string nickname, string email, string password, string passwordConfirmation, string pcName, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask SignOutAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask SaveTerminalCommandsAsync(IReadOnlyList<TerminalCommandCapability> commands, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
