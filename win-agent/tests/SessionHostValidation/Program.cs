using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using TermLink.LocalIpc;
using TermLink.SessionHost;
using TermLink.TerminalCore;

if (args.Length == 1 && args[0] == "--graphics-startup-probe") { await GraphicsStartupValidation.Run(); return; }
if (args.Length == 1 && args[0] == "--input-bridge-fault-probe") { await GraphicsStartupValidation.RunInputBridgeFailure(); return; }
if (args.Length == 1 && args[0] == "--graphics-startup-child") { GraphicsStartupValidation.Child(); return; }
if (args.Length > 1 && args[0] == "--graphics-startup-host") { GraphicsStartupValidation.Host(args[1..]); return; }
if (args.Length == 3 && args[0] == "--native-terminal") { await GraphicsStartupValidation.Display(args[1]); return; }
if (args.Length == 1 && args[0] == "--graphics-profile-probe") { GraphicsProfileValidation.Run(false); return; }
if (args.Length == 1 && args[0] == "--graphics-profile-native-probe") { GraphicsProfileValidation.Run(true); return; }
if (args.Length == 4 && args[0] == "--kitty-shm-child")
{ KittySharedMemoryBrokerValidation.Child(args[1], int.Parse(args[2]), args[3]); return; }
if (args.Length == 2 && args[0] == "--kitty-relay-child") { KittyRelayChild.Child(args[1]); return; }
if (args.Length == 1 && args[0] == "--mouse-probe-child") { MouseBridgeValidation.Child(); return; }
if (args.Length == 2 && args[0] == "--shell-integration-probe") { ShellIntegrationValidation.Run(args[1]); return; }
if (args.Length == 1 && args[0] == "--protocol-probe-child") { ProtocolTransportValidation.Child(); return; }
if (args.Length == 2 && args[0] == "--protocol-reply-relay-child")
{ ProtocolReplyRelayChild.Child(args[1]); return; }
if (args.Length == 1 && args[0] == "--traditional-input-child") { TraditionalInputTransportValidation.Child(); return; }
if (args.Length == 1 && args[0] == "--traditional-input-probe") { TraditionalInputTransportValidation.Run(); return; }
if (args.Length == 1 && args[0] == "--protocol-transport-probe") { ProtocolTransportValidation.Run(); return; }
if (args.Length == 1 && args[0] == "--native-display-probe") { NativeDisplayValidation.Run(); return; }
if (args.Length == 1 && args[0] == "--native-window-child") { NativeWindowValidation.Child(); return; }
if (args.Length == 2 && args[0] == "--native-window-child") { NativeWindowValidation.Child(args[1]); return; }
if (args.Length == 2 && args[0] == "--native-window-probe") { NativeWindowValidation.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--protocol-reply-window-probe") { NativeWindowValidation.RunProtocolReply(args[1]); return; }
if (args.Length == 1 && args[0] == "--native-window-effects-selftest") { NativeWindowEffectsValidation.SelfTest(); return; }
if (args.Length == 2 && args[0] == "--native-window-effects-child") { NativeWindowEffectsValidation.Child(args[1]); return; }
if (args.Length == 7 && args[0] == "--native-window-effects-probe")
{
    try { NativeWindowEffectsValidation.Run(args[1], args[2], args[3], args[4], args[5], args[6]); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}
if (args.Length == 6 && args[0] == "--select-owned-display-theme")
{ NativeWindowValidation.SelectOwnedDisplayTheme(args[1], args[2], args[3], args[4], args[5]); return; }
if (args.Length == 3 && args[0] == "--claude-startup-probe")
{
    MouseBridgeValidation.RunClaudeStartup(args[1], args[2]);
    return;
}
MouseInputValidation.Run();
TerminalHandshakeValidation.Run();
await BrokerCaptureTransportValidation.Run();
TerminalFailureStoreValidation.Run();
MouseBridgeValidation.Run();
GeometryReplyValidation.Run();
NativeDisplayValidation.Run();
KittyFileBrokerValidation.Run();
BrokerCaptureCoordinatorValidation.Run();
await KittySharedMemoryBrokerValidation.Run();

var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes("cmd.exe"));
var firstOptions = SessionHostOptions.Parse(new[] { "--command-base64", encodedCommand, "--managed-shell", "custom" });
var secondOptions = SessionHostOptions.Parse(new[] { "--command-base64", encodedCommand, "--managed-shell", "custom" });
Require(firstOptions.SessionId != Guid.Empty && secondOptions.SessionId != Guid.Empty &&
    firstOptions.SessionId != secondOptions.SessionId,
    "new managed SessionHost instances reused a Session ID");
Require(!firstOptions.EnableBrokerCapture, "Broker capture changed the default SessionHost behavior");
var brokerArgs = new[]
{
    "--command-base64", encodedCommand,
    "--managed-shell", "custom",
    "--native-display", Environment.ProcessPath!,
    "--terminal-state", TerminalSemanticProfile.Graphics.StateContract,
    "--broker-captures"
};
Require(SessionHostOptions.Parse(brokerArgs).EnableBrokerCapture,
    "Graphics managed SessionHost did not enable broker captures");
var agentBrokerArgs = new[]
{
    "--command-base64", encodedCommand,
    "--agent-pipe", "broker-test", "--session-id", Guid.NewGuid().ToString("D"),
    "--handshake-token", Convert.ToBase64String(new byte[LocalPayloadCodec.HandshakeTokenBytes]),
    "--native-display", Environment.ProcessPath!,
    "--terminal-state", TerminalSemanticProfile.Graphics.StateContract,
    "--broker-captures"
};
Require(SessionHostOptions.Parse(agentBrokerArgs).EnableBrokerCapture,
    "Graphics Agent SessionHost did not enable broker captures");
RequireThrows(() => SessionHostOptions.Parse(brokerArgs.Concat(new[] { "--broker-captures" }).ToArray()),
    "duplicate broker-captures flag was accepted");
RequireThrows(() => SessionHostOptions.Parse(brokerArgs.Where(arg => arg != "--terminal-state" &&
    arg != TerminalSemanticProfile.Graphics.StateContract).ToArray()),
    "broker captures without graphics profile were accepted");
RequireThrows(() => SessionHostOptions.Parse(brokerArgs.Where(arg => arg != "--native-display" &&
    arg != Environment.ProcessPath).ToArray()),
    "broker captures without native display were accepted");
RequireThrows(() => SessionHostOptions.Parse(brokerArgs.Where(arg => arg != "--managed-shell" &&
    arg != "custom").ToArray()),
    "broker captures without a managed shell or Agent were accepted");

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
Require(ConPtySessionHost.ShouldAbortInputBridge(new InvalidOperationException("input"), false) &&
    ConPtySessionHost.ShouldAbortInputBridge(null, true) &&
    !ConPtySessionHost.ShouldAbortInputBridge(null, false),
    "SessionHost failed to observe a faulted input bridge before session cleanup");
const uint processedInput = 0x0001;
const uint lineInput = 0x0002;
const uint echoInput = 0x0004;
const uint windowInput = 0x0008;
const uint mouseInput = 0x0010;
const uint quickEdit = 0x0040;
const uint extendedFlags = 0x0080;
const uint virtualTerminalInput = 0x0200;
var outerInputMode = ConPtySessionHost.BuildOuterInputMode(
    processedInput | lineInput | echoInput | mouseInput | quickEdit | virtualTerminalInput);
Require((outerInputMode & (processedInput | lineInput | echoInput | mouseInput)) == 0 &&
    (outerInputMode & (windowInput | extendedFlags | quickEdit | virtualTerminalInput)) ==
        (windowInput | extendedFlags | quickEdit | virtualTerminalInput),
    "SessionHost outer input mode still captures mouse actions or drops existing Terminal flags");
var suppressedColorEnvironment = new Hashtable(StringComparer.OrdinalIgnoreCase)
{
    ["NO_COLOR"] = "1",
    ["TERM"] = "dumb",
    ["COLORTERM"] = string.Empty,
    ["PATH"] = @"C:\Windows\System32"
};
var sanitizedColorBlock = ConPtySessionHost.BuildSanitizedChildEnvironmentBlock(suppressedColorEnvironment);
var sanitizedColorEntries = sanitizedColorBlock.Split('\0', StringSplitOptions.RemoveEmptyEntries);
Require(sanitizedColorEntries.SequenceEqual(new[] { @"PATH=C:\Windows\System32" }),
    "SessionHost propagated inherited color-suppression variables into its ConPTY child");
var capableColorEnvironment = new Hashtable(StringComparer.OrdinalIgnoreCase)
{
    ["TERM"] = "xterm-256color",
    ["COLORTERM"] = "truecolor"
};
var capableColorBlock = ConPtySessionHost.BuildSanitizedChildEnvironmentBlock(capableColorEnvironment);
Require(capableColorBlock.Contains("TERM=xterm-256color", StringComparison.Ordinal) &&
    capableColorBlock.Contains("COLORTERM=truecolor", StringComparison.Ordinal),
    "SessionHost removed valid terminal color capability declarations");
var brokerEnvironment = new Hashtable(StringComparer.Ordinal)
{
    ["Temp"] = @"C:\old-one", ["TEMP"] = @"C:\old-two", ["TMP"] = @"C:\old-three"
};
var brokerEntries = ConPtySessionHost.BuildSanitizedChildEnvironmentBlock(brokerEnvironment,
    @"C:\private-broker-temp").Split('\0', StringSplitOptions.RemoveEmptyEntries);
Require(brokerEntries.SequenceEqual(new[] { @"TEMP=C:\private-broker-temp", @"TMP=C:\private-broker-temp" }),
    "SessionHost did not replace every inherited TEMP/TMP spelling for broker children");
var sharedEnvironment = new Hashtable(StringComparer.OrdinalIgnoreCase)
{
    [KittySharedMemoryBroker.EnvironmentVariable] = "untrusted-pipe"
};
var sharedEntries = ConPtySessionHost.BuildSanitizedChildEnvironmentBlock(sharedEnvironment,
    @"C:\private-broker-temp", "trusted-pipe").Split('\0', StringSplitOptions.RemoveEmptyEntries);
Require(sharedEntries.Contains(KittySharedMemoryBroker.EnvironmentVariable + "=trusted-pipe") &&
    !sharedEntries.Any(entry => entry.Contains("untrusted-pipe", StringComparison.Ordinal)) &&
    !ConPtySessionHost.BuildSanitizedChildEnvironmentBlock(sharedEnvironment).Contains(
        KittySharedMemoryBroker.EnvironmentVariable, StringComparison.Ordinal),
    "SessionHost accepted an inherited shared-memory broker pipe or failed to inject its own");
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
foreach (var selector in new[] { "21", "0021", "" })
{
    var aliasTracker = new TerminalTitleTracker();
    var aliasTitles = new List<string>();
    foreach (var value in Encoding.UTF8.GetBytes("\u001b]" + selector + ";中文标题\u001b\\"))
        aliasTracker.Process(new[] { value }, aliasTitles.Add);
    Require(aliasTitles.SequenceEqual(new[] { "中文标题" }), "split window-title alias was lost");
}

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

using var suppressor = new OuterTerminalReplySuppressor();
suppressor.Expect(Encoding.ASCII.GetBytes("\u001b[?1;2c"));
var primaryWithUserInput = Encoding.ASCII.GetBytes("\u001b[?1;0cuser");
Require(
    Encoding.ASCII.GetString(suppressor.Filter(primaryWithUserInput, primaryWithUserInput.Length)) == "user" &&
        suppressor.PendingCount == 0,
    "Windows Terminal primary-DA compatibility reply was not suppressed semantically");
suppressor.Expect(Encoding.ASCII.GetBytes("\u001b[3;4R"));
var ordinaryArrow = Encoding.ASCII.GetBytes("\u001b[A");
Require(suppressor.Filter(ordinaryArrow, ordinaryArrow.Length).SequenceEqual(ordinaryArrow) &&
    suppressor.PendingCount == 1,
    "ordinary user key input consumed a terminal reply expectation");
var cursorReply = Encoding.ASCII.GetBytes("\u001b[20;70R");
Require(suppressor.Filter(cursorReply, cursorReply.Length).Length == 0 &&
    suppressor.PendingCount == 0,
    "cursor-position compatibility reply was not suppressed");

suppressor.Expect(Encoding.ASCII.GetBytes("\u001b[?1;2c"));
var splitCsiParts = new[] { "\u001b", "[?1;", "0cuser" };
var splitCsiOutput = new List<byte>();
foreach (var part in splitCsiParts)
{
    var bytes = Encoding.ASCII.GetBytes(part);
    splitCsiOutput.AddRange(suppressor.Filter(bytes, bytes.Length));
}
Require(Encoding.ASCII.GetString(splitCsiOutput.ToArray()) == "user" &&
    suppressor.BufferedCandidateBytes == 0 && suppressor.PendingCount == 0,
    "a CSI terminal reply split across reads was not suppressed exactly");

suppressor.Expect(Encoding.ASCII.GetBytes("\u001b]10;rgb:ffff/ffff/ffff\u0007"));
foreach (var part in new[] { "\u001b]10;rgb:", "0000/1111/", "2222\u0007" })
{
    var bytes = Encoding.ASCII.GetBytes(part);
    Require(suppressor.Filter(bytes, bytes.Length).Length == 0,
        "an OSC BEL reply fragment escaped suppression");
}
Require(suppressor.PendingCount == 0,
    "an OSC BEL reply split across reads did not consume its expectation");

suppressor.Expect(Encoding.ASCII.GetBytes("\u001b]11;rgb:0000/0000/0000\u001b\\"));
foreach (var part in new[] { "\u001b]11;rgb:ffff/", "eeee/dddd\u001b", "\\" })
{
    var bytes = Encoding.ASCII.GetBytes(part);
    Require(suppressor.Filter(bytes, bytes.Length).Length == 0,
        "an OSC ST reply fragment escaped suppression");
}
Require(suppressor.PendingCount == 0,
    "an OSC ST reply split across reads did not consume its expectation");

using (var reorderedReplies = new OuterTerminalReplySuppressor())
{
    reorderedReplies.Expect(Encoding.ASCII.GetBytes("\u001b]10;rgb:ffff/ffff/ffff\u001b\\"));
    reorderedReplies.Expect(Encoding.ASCII.GetBytes("\u001b]11;rgb:0000/0000/0000\u001b\\"));
    var backgroundFirst = Encoding.ASCII.GetBytes("\u001b]11;rgb:0000/2b2b/3636\u001b\\");
    var foregroundSecond = Encoding.ASCII.GetBytes("\u001b]10;rgb:8383/9494/9696\u0007user");
    Require(reorderedReplies.Filter(backgroundFirst, backgroundFirst.Length).Length == 0 &&
        Encoding.ASCII.GetString(reorderedReplies.Filter(foregroundSecond, foregroundSecond.Length)) == "user" &&
        reorderedReplies.PendingCount == 0,
        "out-of-order foreground/background replies were blocked by the expectation queue head");
}

using (var missingEarlierReply = new OuterTerminalReplySuppressor())
{
    missingEarlierReply.Expect(Encoding.ASCII.GetBytes("\u001b[?1;2c"));
    missingEarlierReply.Expect(Encoding.ASCII.GetBytes("\u001b]10;rgb:ffff/ffff/ffff\u001b\\"));
    var laterColor = Encoding.ASCII.GetBytes("\u001b]10;rgb:8383/9494/9696\u001b\\");
    Require(missingEarlierReply.Filter(laterColor, laterColor.Length).Length == 0 &&
        missingEarlierReply.PendingCount == 1,
        "a missing earlier reply blocked a later independent semantic expectation");
    var deviceAttributes = Encoding.ASCII.GetBytes("\u001b[?1;0c");
    Require(missingEarlierReply.Filter(deviceAttributes, deviceAttributes.Length).Length == 0 &&
        missingEarlierReply.PendingCount == 0,
        "the earlier expectation was not retained after an out-of-order match");
}

using (var strictColorGrammar = new OuterTerminalReplySuppressor())
{
    strictColorGrammar.Expect(Encoding.ASCII.GetBytes("\u001b]10;rgb:ffff/ffff/ffff\u001b\\"));
    var ordinarySetter = Encoding.ASCII.GetBytes("\u001b]10;red\u0007");
    Require(strictColorGrammar.Filter(ordinarySetter, ordinarySetter.Length).SequenceEqual(ordinarySetter) &&
        strictColorGrammar.PendingCount == 1,
        "an ordinary OSC color setter consumed a color-query reply token");
    var validReply = Encoding.ASCII.GetBytes("\u001b]10;rgb:83/949/9696\u0007");
    Require(strictColorGrammar.Filter(validReply, validReply.Length).Length == 0 &&
        strictColorGrammar.PendingCount == 0,
        "a bounded standards-compatible rgb reply was not recognized");
}

var pixelReplyPassThrough = new List<byte[]>();
using (var textAreaPixelRouter = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => pixelReplyPassThrough.Add(bytes.ToArray())))
{
    foreach (var part in new[] { "\u001b[1", "4", "t" })
    {
        var bytes = Encoding.ASCII.GetBytes(part);
        textAreaPixelRouter.ObserveOutput(bytes, bytes.Length);
    }
    Require(textAreaPixelRouter.PendingCount == 1,
        "a projected CSI 14 t query did not create a renderer-local reply expectation");

    var ordinaryArrowDisposition = new List<bool>();
    var ordinaryArrowBytes = Encoding.ASCII.GetBytes("\u001b[A");
    textAreaPixelRouter.FilterFragment(
        ordinaryArrowBytes,
        ordinaryArrowBytes.Length,
        passed => ordinaryArrowDisposition.Add(passed));
    Require(ordinaryArrowDisposition.SequenceEqual(new[] { true }) &&
        textAreaPixelRouter.PendingCount == 1,
        "ordinary keyboard input consumed a text-area pixel reply expectation");

    var pixelReplyDispositions = new List<bool>();
    foreach (var value in Encoding.ASCII.GetBytes("\u001b[4;684;936t"))
    {
        textAreaPixelRouter.FilterFragment(
            new[] { value },
            1,
            passed => pixelReplyDispositions.Add(passed));
    }
    Require(pixelReplyDispositions.Count == 12 && pixelReplyDispositions.All(passed => !passed) &&
        textAreaPixelRouter.PendingCount == 0 && pixelReplyPassThrough.Count == 0,
        "the 12-key Windows Terminal pixel report escaped as local keyboard input");

    var unpromptedPixelReply = Encoding.ASCII.GetBytes("\u001b[4;720;1280t");
    Require(textAreaPixelRouter.Filter(unpromptedPixelReply, unpromptedPixelReply.Length)
            .SequenceEqual(unpromptedPixelReply),
        "an unprompted pixel report was suppressed without a projected CSI 14 t query");
}

using (var standalonePixelSuppressor = new OuterTerminalReplySuppressor())
{
    var query = Encoding.ASCII.GetBytes("\u001b[14t");
    standalonePixelSuppressor.ObserveOutput(query, query.Length);
    var reply = Encoding.ASCII.GetBytes("\u001b[4;684;936t");
    Require(standalonePixelSuppressor.Filter(reply, reply.Length).Length == 0 &&
        standalonePixelSuppressor.PendingCount == 0,
        "CSI 14 t suppression incorrectly depended on an OSC reply-forwarding callback");
}

var forwardedTerminalReplies = new List<byte[]>();
using (var colorReplyRouter = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => forwardedTerminalReplies.Add(bytes.ToArray())))
{
    foreach (var part in new[] { "\u001b]10;", "?\u001b", "\\\u001b]11;?\u0007" })
    {
        var bytes = Encoding.ASCII.GetBytes(part);
        colorReplyRouter.ObserveOutput(bytes, bytes.Length);
    }
    var foregroundReply = Encoding.ASCII.GetBytes("\u001b]10;rgb:8383/9494/9696\u001b\\");
    var backgroundReply = Encoding.ASCII.GetBytes("\u001b]11;rgb:0000/2b2b/3636\u001b\\");
    Require(colorReplyRouter.Filter(foregroundReply, foregroundReply.Length).Length == 0,
        "an expected outer foreground-color reply escaped protocol routing");
    Require(colorReplyRouter.Filter(backgroundReply, backgroundReply.Length).Length == 0,
        "an expected outer background-color reply escaped protocol routing");
    Require(forwardedTerminalReplies.Count == 2 &&
        forwardedTerminalReplies[0].SequenceEqual(foregroundReply) &&
        forwardedTerminalReplies[1].SequenceEqual(backgroundReply) &&
        colorReplyRouter.PendingCount == 0,
        "outer color replies were not forwarded to ConPTY in query order");

    var pastedWithoutQuery = Encoding.ASCII.GetBytes("\u001b]10;rgb:1111/2222/3333\u001b\\");
    Require(colorReplyRouter.Filter(pastedWithoutQuery, pastedWithoutQuery.Length)
            .SequenceEqual(pastedWithoutQuery) && forwardedTerminalReplies.Count == 2,
        "unprompted OSC input was misclassified as an outer-terminal reply");
}

forwardedTerminalReplies.Clear();
using (var mixedColorRouter = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => forwardedTerminalReplies.Add(bytes.ToArray())))
{
    const ulong sourceSequence = 41;
    var mixedQueries = Encoding.ASCII.GetBytes("\u001b]4;1;?;2;#ff0000;2;?\u001b\\");
    mixedColorRouter.RegisterAuthorityReply(
        Encoding.ASCII.GetBytes("\u001b]4;2;rgb:ffff/0000/0000\u001b\\"),
        sourceSequence,
        checked((uint)mixedQueries.Length),
        0);
    Require(mixedColorRouter.ProjectOutput(
        mixedQueries, mixedQueries.Length, sourceSequence, () => true),
        "the mixed palette query was not projected");

    var externallyOwned = Encoding.ASCII.GetBytes("\u001b]4;1;rgb:1111/2222/3333\u001b\\");
    var authoritativeDuplicate = Encoding.ASCII.GetBytes("\u001b]4;2;rgb:aaaa/bbbb/cccc\u001b\\");
    Require(mixedColorRouter.Filter(externallyOwned, externallyOwned.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 1 && forwardedTerminalReplies[0].SequenceEqual(externallyOwned),
        "a palette query without an authority baseline was not forwarded");
    Require(mixedColorRouter.Filter(authoritativeDuplicate, authoritativeDuplicate.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 1 && mixedColorRouter.PendingCount == 0,
        "an authority-owned palette reply was forwarded or matched the wrong palette index");
}

forwardedTerminalReplies.Clear();
using (var repeatedPaletteRouter = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => forwardedTerminalReplies.Add(bytes.ToArray())))
{
    const ulong sourceSequence = 42;
    var repeatedQueries = Encoding.ASCII.GetBytes("\u001b]4;7;?;7;#123456;7;?\u001b\\");
    repeatedPaletteRouter.RegisterAuthorityReply(
        Encoding.ASCII.GetBytes("\u001b]4;7;rgb:1212/3434/5656\u001b\\"),
        sourceSequence,
        checked((uint)repeatedQueries.Length),
        0);
    Require(repeatedPaletteRouter.ProjectOutput(
        repeatedQueries, repeatedQueries.Length, sourceSequence, () => true),
        "the repeated palette query was not projected");
    var firstExternal = Encoding.ASCII.GetBytes("\u001b]4;7;rgb:aaaa/bbbb/cccc\u001b\\");
    var secondDuplicate = Encoding.ASCII.GetBytes("\u001b]4;7;rgb:1111/2222/3333\u001b\\");
    Require(repeatedPaletteRouter.Filter(firstExternal, firstExternal.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 1 && forwardedTerminalReplies[0].SequenceEqual(firstExternal),
        "a newer authority reply stole an older same-index external palette query");
    Require(repeatedPaletteRouter.Filter(secondDuplicate, secondDuplicate.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 1 && repeatedPaletteRouter.PendingCount == 0,
        "the current same-index authoritative palette reply was not suppressed exactly once");
}

forwardedTerminalReplies.Clear();
using (var authoritativeColorRouter = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => forwardedTerminalReplies.Add(bytes.ToArray())))
{
    const ulong sourceSequence = 43;
    var query = Encoding.ASCII.GetBytes("\u001b]10;#eeeeee\u001b\\\u001b]10;?\u001b\\");
    authoritativeColorRouter.RegisterAuthorityReply(
        Encoding.ASCII.GetBytes("\u001b]10;rgb:eeee/eeee/eeee\u001b\\"),
        sourceSequence,
        checked((uint)query.Length),
        0);
    Require(authoritativeColorRouter.ProjectOutput(
        query, query.Length, sourceSequence, () => true),
        "the authority-owned default color query was not projected");
    var outerDuplicate = Encoding.ASCII.GetBytes("\u001b]10;rgb:8383/9494/9696\u001b\\");
    Require(authoritativeColorRouter.Filter(outerDuplicate, outerDuplicate.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 0 && authoritativeColorRouter.PendingCount == 0,
        "an outer color reply duplicated terminal-core's authoritative PTY reply");
}

forwardedTerminalReplies.Clear();
using (var resolvedThenUnresolved = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => forwardedTerminalReplies.Add(bytes.ToArray())))
{
    var set = Encoding.ASCII.GetBytes("\u001b]10;#123456\u001b\\");
    Require(resolvedThenUnresolved.ProjectOutput(set, set.Length, 50, () => true),
        "the default-color setup output was not projected");
    var firstQuery = Encoding.ASCII.GetBytes("\u001b]10;?\u001b\\");
    var reset = Encoding.ASCII.GetBytes("\u001b]110\u001b\\");
    var batch = firstQuery.Concat(reset).Concat(firstQuery).ToArray();
    resolvedThenUnresolved.RegisterAuthorityReply(
        Encoding.ASCII.GetBytes("\u001b]10;rgb:1212/3434/5656\u001b\\"),
        51,
        checked((uint)firstQuery.Length),
        0);
    Require(resolvedThenUnresolved.ProjectOutput(batch, batch.Length, 51, () => true),
        "the resolved-to-unresolved query batch was not projected");
    var firstDuplicate = Encoding.ASCII.GetBytes("\u001b]10;rgb:aaaa/bbbb/cccc\u001b\\");
    var secondFallback = Encoding.ASCII.GetBytes("\u001b]10;rgb:1111/2222/3333\u001b\\");
    Require(resolvedThenUnresolved.Filter(firstDuplicate, firstDuplicate.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 0,
        "the resolved query's outer duplicate was forwarded");
    Require(resolvedThenUnresolved.Filter(secondFallback, secondFallback.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 1 && forwardedTerminalReplies[0].SequenceEqual(secondFallback),
        "the later unresolved same-selector query was suppressed");
}

forwardedTerminalReplies.Clear();
using (var unresolvedThenResolved = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => forwardedTerminalReplies.Add(bytes.ToArray())))
{
    var firstQuery = Encoding.ASCII.GetBytes("\u001b]10;?\u001b\\");
    var set = Encoding.ASCII.GetBytes("\u001b]10;#654321\u001b\\");
    var batch = firstQuery.Concat(set).Concat(firstQuery).ToArray();
    unresolvedThenResolved.RegisterAuthorityReply(
        Encoding.ASCII.GetBytes("\u001b]10;rgb:6565/4343/2121\u001b\\"),
        52,
        checked((uint)batch.Length),
        0);
    Require(unresolvedThenResolved.ProjectOutput(batch, batch.Length, 52, () => true),
        "the unresolved-to-resolved query batch was not projected");
    var firstFallback = Encoding.ASCII.GetBytes("\u001b]10;rgb:aaaa/bbbb/cccc\u001b\\");
    var secondDuplicate = Encoding.ASCII.GetBytes("\u001b]10;rgb:1111/2222/3333\u001b\\");
    Require(unresolvedThenResolved.Filter(firstFallback, firstFallback.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 1 && forwardedTerminalReplies[0].SequenceEqual(firstFallback),
        "the first unresolved same-selector query was suppressed");
    Require(unresolvedThenResolved.Filter(secondDuplicate, secondDuplicate.Length).Length == 0 &&
        forwardedTerminalReplies.Count == 1,
        "the later resolved same-selector duplicate was forwarded");
}

forwardedTerminalReplies.Clear();
using (var causallyFencedRouter = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => forwardedTerminalReplies.Add(bytes.ToArray())))
{
    causallyFencedRouter.Expect(Encoding.ASCII.GetBytes("\u001b[?1;2c"));
    var escape = new byte[] { 0x1b };
    Require(causallyFencedRouter.Filter(escape, escape.Length).Length == 0,
        "a candidate prefix was not retained for the causal-fence test");
    var laterQuery = Encoding.ASCII.GetBytes("\u001b]10;?\u001b\\");
    causallyFencedRouter.ObserveOutput(laterQuery, laterQuery.Length);
    var remainder = Encoding.ASCII.GetBytes("]10;rgb:1111/2222/3333\u001b\\");
    var released = causallyFencedRouter.Filter(remainder, remainder.Length);
    Require(released.SequenceEqual(Encoding.ASCII.GetBytes("\u001b]10;rgb:1111/2222/3333\u001b\\")) &&
        forwardedTerminalReplies.Count == 0 && causallyFencedRouter.PendingCount == 2,
        "a reply expectation created after candidate input began consumed earlier user input");
}

using (var deferredAuthorityRouter = new OuterTerminalReplySuppressor())
{
    var query = Encoding.ASCII.GetBytes("\u001b]10;#abcdef\u001b\\\u001b]10;?\u001b\\");
    var duplicate = Encoding.ASCII.GetBytes("\u001b]10;rgb:abab/cdcd/efef\u001b\\");
    deferredAuthorityRouter.RegisterAuthorityReply(
        duplicate,
        60,
        checked((uint)query.Length),
        0);
    Require(deferredAuthorityRouter.Filter(duplicate, duplicate.Length).SequenceEqual(duplicate) &&
        deferredAuthorityRouter.PendingCount == 0,
        "an authority expectation became active before its query was projected");
    Require(deferredAuthorityRouter.ProjectOutput(query, query.Length, 60, () => true) &&
        deferredAuthorityRouter.Filter(duplicate, duplicate.Length).Length == 0,
        "an authority expectation was not activated by successful projection");
}

using (var failedProjectionRouter = new OuterTerminalReplySuppressor())
{
    var query = Encoding.ASCII.GetBytes("\u001b]10;#abcdef\u001b\\\u001b]10;?\u001b\\");
    var duplicate = Encoding.ASCII.GetBytes("\u001b]10;rgb:abab/cdcd/efef\u001b\\");
    failedProjectionRouter.RegisterAuthorityReply(
        duplicate,
        61,
        checked((uint)query.Length),
        0);
    Require(!failedProjectionRouter.ProjectOutput(query, query.Length, 61, () => false) &&
        failedProjectionRouter.PendingCount == 0 &&
        failedProjectionRouter.Filter(duplicate, duplicate.Length).SequenceEqual(duplicate),
        "a failed projection left an active terminal-reply expectation");
}

suppressor.Expect(Encoding.ASCII.GetBytes("\u001bP1$r0m\u001b\\"));
foreach (var part in new[] { "\u001bP1$", "r1m\u001b", "\\" })
{
    var bytes = Encoding.ASCII.GetBytes(part);
    Require(suppressor.Filter(bytes, bytes.Length).Length == 0,
        "a DCS reply fragment escaped suppression");
}
Require(suppressor.PendingCount == 0,
    "a DCS reply split across reads did not consume its expectation");

var fragmentDispositions = new List<bool>();
suppressor.Expect(Encoding.ASCII.GetBytes("\u001b[6;9R"));
foreach (var part in new[] { "\u001b", "[6;", "20R" })
{
    var bytes = Encoding.ASCII.GetBytes(part);
    suppressor.FilterFragment(bytes, bytes.Length, passed => fragmentDispositions.Add(passed));
}
Require(fragmentDispositions.SequenceEqual(new[] { false, false, false }) &&
    suppressor.PendingCount == 0,
    "console fragments belonging to a split terminal reply were not resolved as suppressed");

fragmentDispositions.Clear();
suppressor.Expect(Encoding.ASCII.GetBytes("\u001b[1;1R"));
foreach (var part in new[] { "\u001b", "x" })
{
    var bytes = Encoding.ASCII.GetBytes(part);
    suppressor.FilterFragment(bytes, bytes.Length, passed => fragmentDispositions.Add(passed));
}
Require(fragmentDispositions.SequenceEqual(new[] { true, true }) &&
    suppressor.PendingCount == 1,
    "an ordinary split Alt key sequence was delayed after becoming unambiguous");
var finalExpectedCursor = Encoding.ASCII.GetBytes("\u001b[99;99R");
Require(suppressor.Filter(finalExpectedCursor, finalExpectedCursor.Length).Length == 0 &&
    suppressor.PendingCount == 0,
    "the expectation following split Alt input was not retained");

byte[] expiredRawInput = null;
using (var expirySignal = new System.Threading.ManualResetEventSlim(false))
using (var expiringSuppressor = new OuterTerminalReplySuppressor(bytes =>
{
    expiredRawInput = bytes;
    expirySignal.Set();
}))
{
    expiringSuppressor.Expect(Encoding.ASCII.GetBytes("\u001b[4;4R"));
    var loneEscape = new byte[] { 0x1b };
    Require(expiringSuppressor.Filter(loneEscape, loneEscape.Length).Length == 0,
        "a potentially split reply prefix was not buffered");
    Require(expirySignal.Wait(TimeSpan.FromSeconds(2)) &&
        expiredRawInput != null && expiredRawInput.SequenceEqual(loneEscape),
        "an expired raw reply candidate was not released without another input read");
}

var durableApplyOrder = new List<ulong>();
var authorityBridge = new AgentPipeBridge(
    "unused-authority-validation-pipe",
    Guid.NewGuid(),
    new byte[LocalPayloadCodec.HandshakeTokenBytes],
    (_, _) => { },
    (_, _, _) => LocalRemoteInputResult.Accepted,
    1024 * 1024,
    frame => durableApplyOrder.Add(frame.Sequence));
authorityBridge.Publish(LocalFrameType.Lifecycle,
    LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0));
authorityBridge.Publish(LocalFrameType.Output, Encoding.ASCII.GetBytes("durable"));
typeof(AgentPipeBridge).GetMethod("Acknowledge", BindingFlags.Instance | BindingFlags.NonPublic)!
    .Invoke(authorityBridge, new object[] { 2UL });
Require(durableApplyOrder.SequenceEqual(new[] { 1UL, 2UL }) &&
    authorityBridge.LastAcknowledgedSequence == 2,
    "cumulative Agent ACK became visible before ordered authority apply completed");
authorityBridge.Dispose();

var localOnlyApplyOrder = new List<ulong>();
var localOnlyBridge = new AgentPipeBridge(
    "unused-local-only-validation-pipe",
    Guid.NewGuid(),
    new byte[LocalPayloadCodec.HandshakeTokenBytes],
    (_, _) => { },
    (_, _, _) => LocalRemoteInputResult.Accepted,
    1024 * 1024,
    frame => localOnlyApplyOrder.Add(frame.Sequence));
localOnlyBridge.Publish(LocalFrameType.Lifecycle,
    LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0));
typeof(AgentPipeBridge).GetMethod("EnterLocalOnly", BindingFlags.Instance | BindingFlags.NonPublic)!
    .Invoke(localOnlyBridge, Array.Empty<object>());
localOnlyBridge.Publish(LocalFrameType.Output, Encoding.ASCII.GetBytes("local-only"));
Require(localOnlyApplyOrder.SequenceEqual(new[] { 1UL, 2UL }) &&
    localOnlyBridge.LastAcknowledgedSequence == 2 &&
    localOnlyBridge.LastDurablyAcknowledgedSequence == 0,
    "explicit remote-access stop did not preserve ordered local-only terminal authority");
localOnlyBridge.Dispose();

var ptyReplies = new List<byte[]>();
var outerExpectations = new List<byte[]>();
using (var authority = new TerminalCoreAuthority(
    80,
    24,
    bytes => ptyReplies.Add((byte[])bytes.Clone()),
    (bytes, _, _, _) => outerExpectations.Add((byte[])bytes.Clone())))
{
    var authoritySession = Guid.NewGuid();
    authority.ApplyDurable(new LocalFrame(
        LocalFrameType.Lifecycle,
        authoritySession,
        1,
        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0)));
    authority.ApplyDurable(new LocalFrame(
        LocalFrameType.Output,
        authoritySession,
        2,
        Encoding.ASCII.GetBytes("\u001b[5n")));
    Require(authority.LastAppliedSequence == 2 && !authority.FailedClosed &&
        ptyReplies.Count == 1 && outerExpectations.Count == 1 &&
        Encoding.ASCII.GetString(ptyReplies[0]) == "\u001b[0n" &&
        ptyReplies[0].SequenceEqual(outerExpectations[0]),
        "durable terminal-core authority did not emit exactly one ordered PTY reply");
    Require(authority.ExportSnapshot().Length > 0,
        "terminal-core authority did not expose its canonical checkpoint");

    var arrowDown = new NativeTerminalWin32KeyEvent(
        0x26, 0x48, '\0', true, 0x0100, 1);
    Require(authority.EncodeConsoleKey(arrowDown)
            .SequenceEqual(Encoding.ASCII.GetBytes("\u001b[A")),
        "normal-mode console arrow input bypassed terminal-core encoding");
    _ = authority.EncodeConsoleKey(new NativeTerminalWin32KeyEvent(
        0x26, 0x48, '\0', false, 0x0100, 1));

    authority.ApplyDurable(new LocalFrame(
        LocalFrameType.Output,
        authoritySession,
        3,
        Encoding.ASCII.GetBytes("\u001b[?1h\u001b=")));
    Require(authority.EncodeConsoleKey(arrowDown)
            .SequenceEqual(Encoding.ASCII.GetBytes("\u001bOA")),
        "application-cursor mode did not affect terminal-core console encoding");
    _ = authority.EncodeConsoleKey(new NativeTerminalWin32KeyEvent(
        0x26, 0x48, '\0', false, 0x0100, 1));
    Require(authority.EncodeConsoleKey(new NativeTerminalWin32KeyEvent(
            0x61, 0x4f, '1', true, 0, 1))
            .SequenceEqual(Encoding.ASCII.GetBytes("\u001bOq")),
        "application-keypad mode did not affect terminal-core console encoding");

    Require(authority.EncodeConsoleKey(new NativeTerminalWin32KeyEvent(
            0x58, 0x2d, 'x', true, 0, 3))
            .SequenceEqual(Encoding.ASCII.GetBytes("xxx")),
        "console repeat count was not preserved by terminal-core semantic encoding");
    Require(authority.EncodeConsoleKey(new NativeTerminalWin32KeyEvent(
            0x43, 0x2e, '\u0003', true, 0x0008, 1))
            .SequenceEqual(new byte[] { 0x03 }),
        "Ctrl+C was not resolved through terminal-core semantic input");

    authority.ApplyDurable(new LocalFrame(
        LocalFrameType.Output,
        authoritySession,
        4,
        Encoding.ASCII.GetBytes("\u001b[=3u")));
    var f3Down = new NativeTerminalWin32KeyEvent(0x72, 0x3d, '\0', true, 0, 1);
    Require(authority.EncodeConsoleKey(f3Down)
            .SequenceEqual(Encoding.ASCII.GetBytes("\u001b[13;1:1~")),
        "negotiated Kitty key press was not encoded by terminal-core V3");
    Require(authority.EncodeConsoleKey(new NativeTerminalWin32KeyEvent(
            0x72, 0x3d, '\0', false, 0, 1))
            .SequenceEqual(Encoding.ASCII.GetBytes("\u001b[13;1:3~")),
        "negotiated Kitty key release was not preserved");

    authority.ApplyDurable(new LocalFrame(
        LocalFrameType.Output,
        authoritySession,
        5,
        Encoding.ASCII.GetBytes("\u001b[?9001h")));
    Require(authority.EncodeConsoleKey(new NativeTerminalWin32KeyEvent(
            0x70, 0x3b, '\0', true, 0x0008, 2))
            .SequenceEqual(Encoding.ASCII.GetBytes("\u001b[112;59;0;1;8;2_")),
        "DEC 9001 did not preserve the complete Win32 key record");
}

var integratedForwardedReplies = new List<byte[]>();
using (var integratedRouter = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => integratedForwardedReplies.Add(bytes.ToArray())))
using (var integratedAuthority = new TerminalCoreAuthority(
    80,
    24,
    _ => { },
    integratedRouter.RegisterAuthorityReply))
{
    var sessionId = Guid.NewGuid();
    integratedAuthority.ApplyDurable(new LocalFrame(
        LocalFrameType.Lifecycle,
        sessionId,
        1,
        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0)));
    var resolvedQuery = Encoding.ASCII.GetBytes("\u001b]10;#123456\u001b\\\u001b]10;?\u001b\\");
    integratedAuthority.ApplyDurable(new LocalFrame(
        LocalFrameType.Output,
        sessionId,
        2,
        resolvedQuery));
    Require(integratedRouter.ProjectOutput(resolvedQuery, resolvedQuery.Length, 2, () => true),
        "the integrated authority color query was not projected");
    var duplicate = Encoding.ASCII.GetBytes("\u001b]10;rgb:aaaa/bbbb/cccc\u001b\\");
    Require(integratedRouter.Filter(duplicate, duplicate.Length).Length == 0 &&
        integratedForwardedReplies.Count == 0,
        "the integrated source coordinate did not suppress an authority-owned color reply");

    var unresolvedQuery = Encoding.ASCII.GetBytes("\u001b]110\u001b\\\u001b]10;?\u001b\\");
    integratedAuthority.ApplyDurable(new LocalFrame(
        LocalFrameType.Output,
        sessionId,
        3,
        unresolvedQuery));
    Require(integratedRouter.ProjectOutput(unresolvedQuery, unresolvedQuery.Length, 3, () => true),
        "the integrated unresolved color query was not projected");
    var fallback = Encoding.ASCII.GetBytes("\u001b]10;rgb:1111/2222/3333\u001b\\");
    Require(integratedRouter.Filter(fallback, fallback.Length).Length == 0 &&
        integratedForwardedReplies.Count == 1 && integratedForwardedReplies[0].SequenceEqual(fallback),
        "the integrated unresolved color reply was not forwarded as protocol input");
}

var openCodePtyReplies = new List<byte[]>();
var openCodeForwardedReplies = new List<byte[]>();
using (var openCodeRouter = new OuterTerminalReplySuppressor(
    terminalReplyPassThrough: bytes => openCodeForwardedReplies.Add(bytes.ToArray())))
using (var openCodeAuthority = new TerminalCoreAuthority(
    120,
    36,
    bytes => openCodePtyReplies.Add(bytes.ToArray()),
    openCodeRouter.RegisterAuthorityReply))
{
    var sessionId = Guid.NewGuid();
    openCodeAuthority.ApplyDurable(new LocalFrame(
        LocalFrameType.Lifecycle,
        sessionId,
        1,
        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0)));
    var capabilityQueries = Encoding.ASCII.GetBytes(
        "\u001b[?2031h" +
        "\u001b]10;?\u0007\u001b]11;?\u0007" +
        "\u001b[>0q" +
        "\u001b[?1016$p\u001b[?2027$p\u001b[?2031$p" +
        "\u001b[?1004$p\u001b[?2004$p\u001b[?2026$p" +
        "\u001b[?u" +
        "\u001b]99;i=opentui-notifications:p=?;\u001b\\" +
        "\u001b]1337;Capabilities\u001b\\");
    openCodeAuthority.ApplyDurable(new LocalFrame(
        LocalFrameType.Output,
        sessionId,
        2,
        capabilityQueries));
    Require(openCodeRouter.ProjectOutput(
            capabilityQueries,
            capabilityQueries.Length,
            2,
            () => true),
        "the OpenCode capability query batch was not projected");

    var rendererQueries = Encoding.ASCII.GetBytes(
        "\u001b[>4;1m\u001b[?2027h\u001b[?2004h" +
        "\u001b[14t\u001b]4;0;?\u0007");
    openCodeAuthority.ApplyDurable(new LocalFrame(
        LocalFrameType.Output,
        sessionId,
        3,
        rendererQueries));
    Require(openCodeRouter.ProjectOutput(
            rendererQueries,
            rendererQueries.Length,
            3,
            () => true),
        "the OpenCode renderer query batch was not projected");

    foreach (var duplicate in openCodePtyReplies.ToArray())
    {
        Require(openCodeRouter.Filter(duplicate, duplicate.Length).Length == 0,
            "an authority-owned OpenCode terminal reply escaped duplicate suppression");
    }
    var openCodeForeground = Encoding.ASCII.GetBytes("\u001b]10;rgb:8383/9494/9696\u001b\\");
    var openCodeBackground = Encoding.ASCII.GetBytes("\u001b]11;rgb:0000/2b2b/3636\u001b\\");
    var openCodePalette = Encoding.ASCII.GetBytes("\u001b]4;0;rgb:0000/0000/0000\u001b\\");
    foreach (var externalReply in new[] { openCodeForeground, openCodeBackground, openCodePalette })
    {
        Require(openCodeRouter.Filter(externalReply, externalReply.Length).Length == 0,
            "an OpenCode outer-terminal color reply escaped protocol routing");
    }

    var localInputDecisions = new List<bool>();
    foreach (var value in Encoding.ASCII.GetBytes("\u001b[4;684;936t"))
    {
        openCodeRouter.FilterFragment(
            new[] { value },
            1,
            passed => localInputDecisions.Add(passed));
    }
    Require(localInputDecisions.Count == 12 && localInputDecisions.All(passed => !passed) &&
        openCodeForwardedReplies.Count == 3 && openCodeRouter.PendingCount == 0,
        "the complete OpenCode query/reply exchange produced false local input");
}

using (var gapAuthority = new TerminalCoreAuthority(80, 24, _ => { }, (_, _, _, _) => { }))
{
    var gapSession = Guid.NewGuid();
    RequireThrowsInvalidData(
        () => gapAuthority.ApplyDurable(new LocalFrame(
            LocalFrameType.Gap,
            gapSession,
            4,
            LocalPayloadCodec.Gap(1, 3))),
        "terminal-core authority accepted an unrecoverable common-journal gap");
    Require(gapAuthority.FailedClosed,
        "terminal-core authority remained usable after a common-journal gap");
}

// Exercise the actual create/resize/close exports against disposable pipes.
using (var inputPipe = new AnonymousPipeServerStream(PipeDirection.Out))
using (var outputPipe = new AnonymousPipeServerStream(PipeDirection.In))
{
    Require(PseudoConsoleApi.Create(new PseudoConsoleApi.Size { X = 80, Y = 24 },
        inputPipe.ClientSafePipeHandle.DangerousGetHandle(),
        outputPipe.ClientSafePipeHandle.DangerousGetHandle(), 0, out var console) >= 0,
        "ConPTY creation failed");
    try
    {
        Require(PseudoConsoleApi.Resize(console, new PseudoConsoleApi.Size { X = 100, Y = 30 }) >= 0,
            "ConPTY resize failed");
    }
    finally { PseudoConsoleApi.Close(console); }
}

Console.WriteLine("SessionHost title, reply routing and ConPTY lifecycle validation passed.");
WindowResizeValidation.Run();

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

static void RequireThrowsInvalidData(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException(message);
}
