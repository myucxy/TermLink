using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TermLink.LocalIpc;
using TermLink.SessionHost;
using TermLink.TerminalCore;

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
    bytes => outerExpectations.Add((byte[])bytes.Clone())))
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

using (var gapAuthority = new TerminalCoreAuthority(80, 24, _ => { }, _ => { }))
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

static void RequireThrowsInvalidData(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException(message);
}
