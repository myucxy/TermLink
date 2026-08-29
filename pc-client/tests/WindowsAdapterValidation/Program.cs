using System.Diagnostics;
using System.Runtime.InteropServices;
using TermLink.Agent;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Windows;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Usage: WindowsAdapterValidation <TermLink.Agent.exe>");

var appShipStatus = WindowsDesktopOperations.ParseUpdateStatus("""
    {
      "schemaVersion": 1,
      "decision": "NoUpdate",
      "versionName": "0.2.4",
      "buildNumber": 45,
      "releaseNotes": " AppShip adapter fixture ",
      "message": "current"
    }
    """);
Require(appShipStatus.Decision == DesktopUpdateDecision.NoUpdate &&
        appShipStatus.BuildNumber == 45 &&
        appShipStatus.ReleaseNotes == "AppShip adapter fixture",
    "The Windows desktop adapter did not parse the bounded AppShip result contract.");

var root = Path.Combine(Path.GetTempPath(), "TermLink-WindowsAdapter-" + Guid.NewGuid().ToString("N"));
var localData = Path.Combine(root, "local-app-data");
var buffer = Path.Combine(root, "buffer");
var marker = Path.Combine(root, "acceptance-root.cer");
var pipe = "termlink-windows-adapter-" + Guid.NewGuid().ToString("N");
Directory.CreateDirectory(localData);
var originalRoot = Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT");
var originalMarker = Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE");

try
{
    Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT", localData);
    Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE", marker);
    var descriptor = new PlatformDescriptor(
        PcPlatform.Windows,
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? PcArchitecture.Arm64 : PcArchitecture.X64,
        RuntimeInformation.OSDescription,
        RuntimeInformation.RuntimeIdentifier,
        false);
    var options = new WindowsAgentClientOptions(pipe, buffer, Path.GetFullPath(args[0]), descriptor);
    await using var client = new WindowsAgentClient(options);
    var first = await client.GetStatusAsync(false, CancellationToken.None);
    Require(first.Identity.PcId != Guid.Empty, "The Windows adapter did not provide a stable PC identity.");
    Require(first.ConnectionState == AgentConnectionState.NotConfigured,
        "An isolated unconfigured Agent did not map to NotConfigured.");
    Require(first.Capabilities.Contains(PcCapabilityIds.RemoteAgentUpdateV1),
        "The Windows adapter did not expose the update capability.");

    await client.SetRemoteAccessAsync(false, CancellationToken.None);
    var stopped = await client.GetStatusAsync(false, CancellationToken.None);
    Require(!stopped.RemoteAccessEnabled && stopped.Revision > first.Revision,
        "Remote-access disable did not change the mapped snapshot.");
    await client.SetRemoteAccessAsync(true, CancellationToken.None);

    var command = new TerminalCommandCapability(
        "custom-validation",
        "Validation Command",
        Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        new[] { "/d /q" },
        false,
        true);
    await client.SaveTerminalCommandsAsync(new[] { command }, CancellationToken.None);
    var commandPath = Path.Combine(localData, "TermLink", "Agent", "terminal-commands.json");
    Require(File.Exists(commandPath), "The Agent did not persist the manual terminal command in the isolated root.");

    var parallel = await Task.WhenAll(Enumerable.Range(0, 16)
        .Select(_ => client.GetStatusAsync(false, CancellationToken.None).AsTask()));
    Require(parallel.All(snapshot => snapshot.Identity.PcId == first.Identity.PcId),
        "Concurrent status mapping changed the PC identity.");
    Require(parallel.Select(snapshot => snapshot.Revision).Distinct().Count() == 1,
        "Unchanged concurrent snapshots produced different revisions.");

    await client.SignOutAsync(CancellationToken.None);
    var signedOut = await client.GetStatusAsync(false, CancellationToken.None);
    Require(!signedOut.AccountLinked && signedOut.ConnectionState == AgentConnectionState.NotConfigured,
        "The isolated sign-out flow did not restart into an unconfigured state.");

    await client.ShutdownCompletelyAsync(CancellationToken.None);
    var afterShutdown = await client.GetStatusAsync(false, CancellationToken.None);
    Require(afterShutdown.ConnectionState == AgentConnectionState.Offline,
        "Status polling restarted the Agent after an explicit complete shutdown.");
    await Task.Delay(300);
    Require(!Process.GetProcessesByName("TermLink.Agent").Any(process =>
    {
        using (process)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                    Path.GetFullPath(args[0]),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }), "Complete shutdown left the isolated Agent process running.");
    Console.WriteLine(
        "Windows Avalonia adapter validation passed: authenticated V10 status/actions, stable revisions, " +
        "isolated command settings, sign-out restart, and complete shutdown without restart.");
}
finally
{
    try
    {
        RegistryControlClient.Send(
            pipe,
            new RegistryRequest { Command = RegistryCommand.Shutdown },
            5_000);
    }
    catch { }
    Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT", originalRoot);
    Environment.SetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE", originalMarker);
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
