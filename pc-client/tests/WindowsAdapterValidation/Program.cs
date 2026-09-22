using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TermLink.Agent;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Windows;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Usage: WindowsAdapterValidation <TermLink.Agent.exe>");
Require(RegistryControlClient.MaximumTimeoutMilliseconds == 360_000,
    "The Windows adapter and Agent control timeout contract diverged.");

var shellDispatchCalled = false;
WindowsDesktopOperations.OpenShellTarget(
    new ProcessStartInfo
    {
        FileName = Path.GetTempPath(),
        UseShellExecute = true,
    },
    startInfo =>
    {
        shellDispatchCalled = startInfo.UseShellExecute &&
            string.Equals(startInfo.FileName, Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        return null;
    });
Require(shellDispatchCalled,
    "A successful ShellExecute dispatch without a process handle was reported as a failure.");

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
var appShipUpdate = WindowsDesktopOperations.ParseAppShipUpdate("""
    {
      "status":"optional",
      "latest":{"versionName":"0.2.46","buildNumber":101,"releaseNotes":"Direct enabled"},
      "artifact":{
        "platform":"windows","fileKind":"exe","architecture":"x64","status":"ready",
        "downloadEnabled":true,"originalFilename":"TermLink-Setup-0.2.46.101-x64.exe",
        "sizeBytes":123,"expectedSizeBytes":123,
        "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "signerFingerprint":"91ae60bc06d866628b8eb2c62f32bde9dab56466",
        "downloadUrl":"https://appship.example/artifact/download"
      }
    }
    """, 100);
Require(appShipUpdate.Status.Decision == DesktopUpdateDecision.Optional &&
        appShipUpdate.Status.BuildNumber == 101 && appShipUpdate.SizeBytes == 123 &&
        appShipUpdate.DownloadUrl == "https://appship.example/artifact/download",
    "The Avalonia Windows adapter did not retain the verified update contract.");
RequireThrows<InvalidDataException>(() => WindowsDesktopOperations.ParseAppShipUpdate("""
    {
      "status":"optional","latest":{"versionName":"0.2.46","buildNumber":101},
      "artifact":{"platform":"windows","fileKind":"exe","architecture":"x64","status":"ready",
        "downloadEnabled":true,"originalFilename":"TermLink.exe","sizeBytes":1,"expectedSizeBytes":1,
        "sha256":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz",
        "signerFingerprint":"91ae60bc06d866628b8eb2c62f32bde9dab56466","downloadUrl":"http://unsafe.example/x.exe"}
    }
    """, 100),
    "The Avalonia Windows adapter accepted an unsafe update contract.");

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
    var agentStart = WindowsAgentClient.CreateAgentStartInfo(options);
    Require(!agentStart.UseShellExecute && agentStart.CreateNoWindow &&
            agentStart.RedirectStandardError && agentStart.RedirectStandardOutput,
        "The independent Agent startup path does not preserve bounded failure diagnostics.");
    Require(agentStart.ArgumentList.SequenceEqual(new[]
        {
            "--registry", "--control-pipe", pipe, "--buffer-root", Path.GetFullPath(buffer)
        }),
        "The independent Agent startup arguments changed unexpectedly.");
    var safeDiagnostic = WindowsAgentClient.NormalizeAgentDiagnostic(
        "Agent failed at https://identity.example/authorize?code=secret&state=private\r\nretry later");
    Require(!safeDiagnostic.Contains("secret", StringComparison.Ordinal) &&
            !safeDiagnostic.Contains("private", StringComparison.Ordinal) &&
            safeDiagnostic.Length <= 512,
        "Agent startup diagnostics exposed a URL query or exceeded the display bound.");
    Require(WindowsAgentClient.IsSingleInstanceDiagnostic(
            "TermLink Agent failed: Another TermLink background runtime already owns this account control pipe."),
        "The duplicate-Agent lifecycle diagnostic is no longer recognized.");
    Require(!WindowsAgentClient.IsAgentInstanceActive(pipe),
        "An unrelated installed Agent was mistaken for this isolated control pipe.");
    await using var client = new WindowsAgentClient(options);
    var first = await client.GetStatusAsync(false, CancellationToken.None);
    Require(WindowsAgentClient.IsAgentInstanceActive(pipe),
        "The isolated Agent did not retain its control-pipe instance marker.");
    Require(first.Identity.PcId != Guid.Empty, "The Windows adapter did not provide a stable PC identity.");
    Require(first.ConnectionState == AgentConnectionState.NotConfigured,
        "An isolated unconfigured Agent did not map to NotConfigured.");
    Require(first.Capabilities.Contains(PcCapabilityIds.RemoteAgentUpdateV1),
        "The Windows adapter did not expose the update capability.");

    await client.ConfigureServerAsync("https://term.example.test:8910", CancellationToken.None);
    var configuredServer = await client.GetStatusAsync(false, CancellationToken.None);
    Require(!configuredServer.AccountLinked &&
            string.Equals(configuredServer.ServerOrigin, "https://term.example.test:8910", StringComparison.OrdinalIgnoreCase),
        "The Agent did not persist the configured server origin while signed out.");

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

    var preservedSessionId = Guid.NewGuid();
    var preservedSessionToken = new byte[SessionRegistration.HandshakeTokenBytes];
    RandomNumberGenerator.Fill(preservedSessionToken);
    var registeredSession = RegistryControlClient.Send(
        pipe,
        new RegistryRequest
        {
            Command = RegistryCommand.Register,
            Registration = new SessionRegistration(
                preservedSessionId,
                "termlink-windows-adapter-session-" + preservedSessionId.ToString("N"),
                preservedSessionToken,
                Environment.ProcessId,
                ManagedShellType.PowerShell7)
        },
        5_000);
    Require(registeredSession.Success,
        "The local-session preservation fixture could not register.");
    var agentProcessIdBeforeSignOut = FindAgentProcessId(options.AgentExecutablePath);
    Require(agentProcessIdBeforeSignOut.HasValue,
        "The isolated Agent process was unavailable before sign-out.");
    var signOutTimer = Stopwatch.StartNew();
    await client.SignOutAsync(CancellationToken.None);
    signOutTimer.Stop();
    var signedOut = await client.GetStatusAsync(false, CancellationToken.None);
    var agentProcessIdAfterSignOut = FindAgentProcessId(options.AgentExecutablePath);
    var sessionsAfterSignOut = RegistryControlClient.Send(
        pipe,
        new RegistryRequest { Command = RegistryCommand.List },
        5_000).Summaries;
    Require(!signedOut.AccountLinked && signedOut.ConnectionState == AgentConnectionState.NotConfigured,
        "The isolated sign-out flow did not immediately publish an unconfigured state.");
    Require(agentProcessIdAfterSignOut == agentProcessIdBeforeSignOut,
        "Account sign-out restarted the Agent instead of preserving local sessions.");
    Require(sessionsAfterSignOut.Any(session => session.SessionId == preservedSessionId),
        "Account sign-out removed a still-running local session from the live registry.");
    Require(signOutTimer.Elapsed < TimeSpan.FromSeconds(5),
        "Account sign-out waited for Agent or network teardown.");

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
        "Windows Avalonia adapter validation passed: authenticated V11 status/actions, stable revisions, " +
        "isolated command settings, in-place sign-out, and complete shutdown without restart.");
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

static void RequireThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

static int? FindAgentProcessId(string executablePath)
{
    var expected = Path.GetFullPath(executablePath);
    foreach (var process in Process.GetProcessesByName("TermLink.Agent"))
    {
        using (process)
        {
            try
            {
                if (string.Equals(
                    Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return process.Id;
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
    return null;
}
