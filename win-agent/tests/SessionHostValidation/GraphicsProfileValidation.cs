using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.NativeDisplay;
using TermLink.SessionHost;
using TermLink.TerminalCore;

internal static class GraphicsProfileValidation
{
    internal static void Run(bool native)
    {
        var command = Convert.ToBase64String(Encoding.UTF8.GetBytes("cmd.exe"));
        var baseline = new[] { "--command-base64", command, "--managed-shell", "custom" };
        var display = new[] { "--native-display", Environment.ProcessPath! };
        Require(SessionHostOptions.Parse(baseline).SemanticProfile == TerminalSemanticProfile.Legacy, "Legacy default changed.");
        Require(SessionHostOptions.Parse(baseline.Concat(display).ToArray()).SemanticProfile == TerminalSemanticProfile.Legacy,
            "A display implicitly changed the session profile.");
        var graphics = new[] { "--terminal-state", TerminalSemanticProfile.Graphics.StateContract };
        var unicode16 = new[] { "--terminal-state", TerminalSemanticProfile.GraphicsUnicode16.StateContract };
        Require(SessionHostOptions.Parse(baseline.Concat(display).Concat(graphics).ToArray()).SemanticProfile == TerminalSemanticProfile.Graphics,
            "Explicit graphics profile was lost.");
        Require(SessionHostOptions.Parse(baseline.Concat(display).Concat(unicode16).ToArray()).SemanticProfile == TerminalSemanticProfile.GraphicsUnicode16,
            "Explicit Unicode16 graphics profile was lost.");
        RejectArgument(() => SessionHostOptions.Parse(baseline.Concat(graphics).ToArray()));
        RejectArgument(() => SessionHostOptions.Parse(baseline.Concat(unicode16).ToArray()));
        foreach (var value in new[] { "graphics", "legacy", "terminal_state_2_22_v3", TerminalSemanticProfile.Graphics.StateContract + " " })
            RejectArgument(() => SessionHostOptions.Parse(baseline.Concat(display).Concat(new[] { "--terminal-state", value }).ToArray()));
        RejectArgument(() => SessionHostOptions.Parse(baseline.Concat(display).Concat(graphics).Concat(graphics).ToArray()));
        RejectArgument(() => new TerminalCoreAuthority(8, 3, _ => { }, (_, _, _, _) => { }, semanticProfile: TerminalSemanticProfile.Graphics));
        Handshake(TerminalSemanticProfile.Graphics, TerminalSemanticProfile.Graphics).GetAwaiter().GetResult();
        Handshake(TerminalSemanticProfile.Graphics, TerminalSemanticProfile.Legacy).GetAwaiter().GetResult();
        Handshake(TerminalSemanticProfile.Legacy, TerminalSemanticProfile.Graphics).GetAwaiter().GetResult();
        Handshake(TerminalSemanticProfile.GraphicsUnicode16, TerminalSemanticProfile.GraphicsUnicode16).GetAwaiter().GetResult();
        Handshake(TerminalSemanticProfile.GraphicsUnicode16, TerminalSemanticProfile.Graphics).GetAwaiter().GetResult();
        Handshake(TerminalSemanticProfile.Graphics, TerminalSemanticProfile.GraphicsUnicode16).GetAwaiter().GetResult();
        if (native) { NativeAuthority(); NativeUnicode16Authority(); }
        Console.WriteLine(native ? "Graphics authority profile, snapshots, V5 and frozen history passed." :
            "SessionHost immutable profile options and actual pipe handshake gates passed.");
    }

    private static async Task Handshake(TerminalSemanticProfile requested, TerminalSemanticProfile echoed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeName = "termlink-profile-test-" + Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid();
        var token = Enumerable.Range(0, 32).Select(v => (byte)v).ToArray();
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var bridge = new AgentPipeBridge(pipeName, session, token, (_, _) => { },
            (_, _, _) => LocalRemoteInputResult.Accepted, 1024 * 1024, semanticProfile: requested);
        bridge.Start();
        await pipe.WaitForConnectionAsync(deadline.Token);
        var hello = await Task.Run(() => LocalFrameCodec.Read(pipe), deadline.Token).WaitAsync(deadline.Token);
        Require(hello.Type == LocalFrameType.HostHello && hello.SessionId == session, "Wrong profile handshake envelope.");
        Require(LocalPayloadCodec.ParseTerminalHello(hello.Payload, token, out TerminalSemanticProfile actual) == Environment.ProcessId && actual == requested,
            "SessionHost advertised a different profile.");
        LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.AgentHello, session, 0,
            LocalPayloadCodec.TerminalHello(Environment.ProcessId, token, echoed)));
        LocalFrame following = null;
        try { following = await Task.Run(() => LocalFrameCodec.Read(pipe), deadline.Token).WaitAsync(deadline.Token); }
        catch (IOException) when (requested != echoed) { }
        if (requested == echoed) Require(following?.Type == LocalFrameType.Capabilities, "Matching profile was rejected.");
        else Require(following == null, "Mismatched profile advanced beyond authentication.");
    }

    private static void NativeAuthority()
    {
        Require(NativeTerminalCoreV2.SupportsGraphicsV1(), "The graphics native probe requires the new DLL.");
        using (var legacy = new TerminalCoreAuthority(10, 3, _ => { }, null))
        {
            legacy.ApplyDurable(new LocalFrame(LocalFrameType.Output, Guid.Empty, 1, Encoding.ASCII.GetBytes("legacy")));
            Require(legacy.SemanticProfile == TerminalSemanticProfile.Legacy && legacy.RenderDisplay(0, 0).Status == NativeDisplayProtocol.Frame,
                "Legacy authority no longer emits V4.");
            using var restored = NativeTerminalCoreV2.ImportSnapshot(legacy.ExportSnapshot());
            Require(!restored.HasGraphicsProfile, "Legacy snapshot was migrated.");
        }
        using var authority = new TerminalCoreAuthority(10, 3, _ => { }, null, semanticProfile: TerminalSemanticProfile.Graphics);
        authority.ApplyDurable(new LocalFrame(LocalFrameType.HostGeometry, Guid.Empty, 1, new NativeTerminalHostGeometryV1(10, 20, 0, 0, 0, 0).Encode()));
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, Guid.Empty, 2,
            Encoding.ASCII.GetBytes("\x1b_Ga=T,f=32,s=1,v=1,i=1,c=1,r=1,q=2;AQIDBA==\x1b\\")));
        var live = authority.RenderDisplay(0, 0);
        Require(live.Status == NativeDisplayProtocol.FrameV5 && NativeTerminalRenderFrameV5Decoder.Decode(live.Frame).Images.Count == 1,
            "Graphics authority lost the image or emitted a legacy frame.");
        var snapshot = authority.ExportSnapshot();
        Require(snapshot[10] == 22 && snapshot[11] == 0, "Graphics authority did not export Snapshot 2.22.");
        using (var restored = NativeTerminalCoreV2.ImportGraphicsSnapshot(snapshot))
        using (var checkpoint = restored.CaptureCheckpoint())
            Require(checkpoint.ExportRenderFrameV5().SequenceEqual(live.Frame), "Authority snapshot changed its rendered state.");
        Require(authority.RenderDisplay(3, 0).Status == NativeDisplayProtocol.Unchanged, "Unchanged V5 was re-exported.");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Resize, Guid.Empty, 3, LocalPayloadCodec.Resize(8, 4)));
        var resized = NativeTerminalRenderFrameV5Decoder.Decode(authority.RenderDisplay(0, 0).Frame);
        Require(resized.Columns == 8 && resized.Rows == 4, "Graphics resize failed.");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, Guid.Empty, 4,
            Encoding.ASCII.GetBytes("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nsix\r\n")));
        var history = authority.RenderDisplay(0, uint.MaxValue);
        Require(history.Status == NativeDisplayProtocol.FrameV5 && NativeTerminalRenderFrameV5Decoder.Decode(history.Frame).Images.Count == 1,
            "Graphics frozen viewport lost its retained image.");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, Guid.Empty, 5, Encoding.ASCII.GetBytes("\x1bc")));
        Require(authority.RenderDisplay(0, uint.MaxValue).Frame.SequenceEqual(history.Frame), "RIS changed frozen graphics history.");
        var reset = authority.RenderDisplay(0, 0);
        Require(reset.Status == NativeDisplayProtocol.FrameV5 && NativeTerminalRenderFrameV5Decoder.Decode(reset.Frame).Images.Count == 0 &&
            authority.LastAppliedSequence == 5 && !authority.FailedClosed, "Returning live changed profile or retained the reset image.");
    }

    private static void NativeUnicode16Authority()
    {
        using var authority = new TerminalCoreAuthority(8, 3, _ => { }, null,
            semanticProfile: TerminalSemanticProfile.GraphicsUnicode16);
        authority.ApplyDurable(new LocalFrame(LocalFrameType.HostGeometry, Guid.Empty, 1,
            new NativeTerminalHostGeometryV1(8, 16, 0, 0, 0, 0).Encode()));
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, Guid.Empty, 2,
            Encoding.UTF8.GetBytes("A😀\r\nline two\r\nline three\r\nline four\r\n")));
        var live = authority.RenderDisplay(0, 0);
        var liveFrame = NativeTerminalRenderFrameV5Decoder.Decode(live.Frame);
        Require(live.Status == NativeDisplayProtocol.FrameV5 &&
            liveFrame.Text.WidthProfile == NativeTerminalWidthProfile.Unicode16,
            "Unicode16 authority live frame used another width profile.");
        using var restored = NativeTerminalCoreV2.ImportGraphicsSnapshot(authority.ExportSnapshot());
        using var checkpoint = restored.CaptureCheckpoint();
        Require(NativeTerminalRenderFrameV5Decoder.Decode(checkpoint.ExportRenderFrameV5()).Text.WidthProfile ==
            NativeTerminalWidthProfile.Unicode16, "Unicode16 authority snapshot lost its width profile.");
        var history = authority.RenderDisplay(0, uint.MaxValue);
        Require(history.Status == NativeDisplayProtocol.FrameV5 &&
            NativeTerminalRenderFrameV5Decoder.Decode(history.Frame).Text.WidthProfile == NativeTerminalWidthProfile.Unicode16,
            "Unicode16 authority history lost its width profile.");
    }

    private static void RejectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid graphics profile/display configuration was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
