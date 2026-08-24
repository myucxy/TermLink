using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TermLink.LocalIpc;
using TermLink.SessionHost;

var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes("cmd.exe"));
var firstOptions = SessionHostOptions.Parse(new[] { "--command-base64", encodedCommand, "--managed-shell", "custom" });
var secondOptions = SessionHostOptions.Parse(new[] { "--command-base64", encodedCommand, "--managed-shell", "custom" });
Require(firstOptions.SessionId != Guid.Empty && secondOptions.SessionId != Guid.Empty &&
    firstOptions.SessionId != secondOptions.SessionId,
    "new managed SessionHost instances reused a Session ID");

var workingDirectory = Path.GetFullPath(Path.GetTempPath());
var managedSessionId = Guid.NewGuid();
var workingDirectoryOptions = SessionHostOptions.Parse(new[]
{
    "--command-base64", encodedCommand,
    "--managed-shell", "command-prompt",
    "--managed-session-id", managedSessionId.ToString("D"),
    "--working-directory-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(workingDirectory))
});
Require(workingDirectoryOptions.SessionId == managedSessionId,
    "explicit managed SessionHost ID was not preserved");
Require(string.Equals(workingDirectoryOptions.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase),
    "SessionHost working directory was not normalized and preserved");
var registrationTimeoutOptions = SessionHostOptions.Parse(new[]
{
    "--command-base64", encodedCommand,
    "--managed-shell", "command-prompt",
    "--registration-timeout-ms", "750"
});
Require(registrationTimeoutOptions.RegistrationTimeoutMilliseconds == 750,
    "managed SessionHost did not preserve its bounded registration timeout");
Require(!ConPtySessionHost.ShouldAbortUnregisteredSession(true, false, 749, 750) &&
    ConPtySessionHost.ShouldAbortUnregisteredSession(true, false, 750, 750) &&
    !ConPtySessionHost.ShouldAbortUnregisteredSession(true, true, 750, 750),
    "SessionHost registration timeout did not exclusively stop an unregistered process tree");
RequireThrows(() => SessionHostOptions.Parse(new[]
{
    "--command-base64", encodedCommand,
    "--registration-timeout-ms", "750"
}), "registration timeout without a managed shell was accepted");
RequireThrows(() => SessionHostOptions.Parse(new[]
{
    "--command-base64", encodedCommand,
    "--managed-session-id", Guid.NewGuid().ToString("D")
}), "managed session ID without a managed shell was accepted");
RequireThrows(() => SessionHostOptions.Parse(new[]
{
    "--command-base64", encodedCommand,
    "--working-directory-base64", Convert.ToBase64String(Encoding.UTF8.GetBytes("relative"))
}), "relative SessionHost working directory was accepted");

var titles = new List<string>();
var tracker = new TerminalTitleTracker();

tracker.Process(Encoding.UTF8.GetBytes("ordinary-output\u001b]0;Power"), titles.Add);
Require(titles.Count == 0, "split OSC title completed too early");
tracker.Process(Encoding.UTF8.GetBytes("Shell — build\u0007tail"), titles.Add);
Require(titles.SequenceEqual(new[] { "PowerShell — build" }), "OSC 0 BEL title changed");

tracker.Process(Encoding.UTF8.GetBytes("\u001b]2;second\u001b"), titles.Add);
Require(titles.Count == 1, "split ST title completed too early");
tracker.Process(new byte[] { (byte)'\\' }, titles.Add);
Require(titles.SequenceEqual(new[] { "PowerShell — build", "second" }), "OSC 2 ST title changed");

tracker.Process(Encoding.UTF8.GetBytes("\u001b]9;ignored\u0007"), titles.Add);
tracker.Process(new byte[] { 0x1b, (byte)']', (byte)'0', (byte)';', 0xff, 0x07 }, titles.Add);
Require(titles.Count == 2, "unsupported or invalid UTF-8 OSC became trusted metadata");

var oversized = new byte[4096 + 6];
oversized[0] = 0x1b;
oversized[1] = (byte)']';
oversized[2] = (byte)'2';
oversized[3] = (byte)';';
Array.Fill(oversized, (byte)'x', 4, 4097);
oversized[oversized.Length - 1] = 0x07;
tracker.Process(oversized, titles.Add);
Require(titles.Count == 2, "oversized OSC title was accepted");

// Exercise the actual SessionHost publication boundary. Both the polled
// console title and OSC tracker call this method, so normalization and
// duplicate suppression must happen before consuming an ordered Agent seq.
var bridge = new AgentPipeBridge(
    "unused-title-validation-pipe",
    Guid.NewGuid(),
    new byte[LocalPayloadCodec.HandshakeTokenBytes],
    (_, _) => { },
    (_, _, _) => LocalRemoteInputResult.Accepted,
    1024 * 1024);
using (var sessionHost = new ConPtySessionHost(firstOptions))
{
    var hostType = typeof(ConPtySessionHost);
    hostType.GetField("agentPipe", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(sessionHost, bridge);
    var publishTitle = hostType.GetMethod(
        "PublishTitleIfChanged",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    var nextSequence = typeof(AgentPipeBridge).GetField(
        "nextSequence",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    publishTitle.Invoke(sessionHost, new object[] { "  synchronized title  " });
    publishTitle.Invoke(sessionHost, new object[] { "synchronized title" });
    Require((ulong)nextSequence.GetValue(bridge)! == 1,
        "identical normalized titles consumed more than one ordered sequence");

    publishTitle.Invoke(sessionHost, new object[] { "different title" });
    publishTitle.Invoke(sessionHost, new object[] { string.Empty });
    publishTitle.Invoke(sessionHost, new object[] { string.Empty });
    Require((ulong)nextSequence.GetValue(bridge)! == 3,
        "changed or explicitly cleared titles were not published exactly once");
}

Console.WriteLine("SessionHost title validation passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RequireThrows(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException(message);
}
