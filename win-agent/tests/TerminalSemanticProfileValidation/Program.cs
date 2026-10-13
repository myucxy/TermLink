using System;
using System.IO;
using System.Linq;
using System.Text;
using TermLink.LocalIpc;

internal static class Program
{
    private static int checks;
    private static readonly Guid SessionId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private const string Legacy = "terminal_state_2_21_v2;width=1;output=0;resources=1";
    private const string Graphics = "terminal_state_2_22_v1;width=1;output=0;resources=2";
    private const string GraphicsUnicode16 = "terminal_state_2_22_v2;width=2;output=0;resources=2";

    public static void Main()
    {
        ValidateMarkers();
        ValidateHandshake();
        Console.WriteLine("Terminal semantic profiles: " + checks + " assertions passed.");
    }

    private static void ValidateMarkers()
    {
        Require(LocalPayloadCodec.TerminalStateContract == "terminal_state_2_21_v2", "Legacy state constant changed");
        Require(LocalPayloadCodec.TerminalProfileDescriptor == Legacy, "Legacy descriptor constant changed");
        Require(TerminalSemanticProfile.Legacy.StateContract == LocalPayloadCodec.TerminalStateContract &&
            !TerminalSemanticProfile.Legacy.IsGraphics, "Legacy profile properties differ");
        Require(TerminalSemanticProfile.Graphics.StateContract == "terminal_state_2_22_v1" &&
            TerminalSemanticProfile.Graphics.IsGraphics, "Graphics profile properties differ");
        Require(TerminalSemanticProfile.Graphics.Descriptor == Graphics, "Graphics descriptor differs");
        Require(TerminalSemanticProfile.Legacy.WidthProfile == 1 &&
            TerminalSemanticProfile.Graphics.WidthProfile == 1 &&
            TerminalSemanticProfile.GraphicsUnicode16.IsGraphics &&
            TerminalSemanticProfile.GraphicsUnicode16.WidthProfile == 2 &&
            TerminalSemanticProfile.GraphicsUnicode16.Descriptor == GraphicsUnicode16,
            "Unicode16 graphics profile properties differ");
        var legacy = Marker(Legacy);
        var graphics = Marker(Graphics);
        var graphicsUnicode16 = Marker(GraphicsUnicode16);
        Require(TerminalSessionProfile.Encode(SessionId).SequenceEqual(legacy), "Legacy marker bytes changed");
        Require(TerminalSessionProfile.Encode(SessionId, TerminalSemanticProfile.Legacy).SequenceEqual(legacy), "Explicit legacy marker differs");
        Require(TerminalSessionProfile.Encode(SessionId, TerminalSemanticProfile.Graphics).SequenceEqual(graphics), "Graphics marker bytes differ");
        Require(TerminalSessionProfile.Encode(SessionId, TerminalSemanticProfile.GraphicsUnicode16).SequenceEqual(graphicsUnicode16),
            "Unicode16 marker bytes differ");
        TerminalSessionProfile.Validate(legacy, SessionId);
        Require(TerminalSessionProfile.Read(legacy, SessionId) == TerminalSemanticProfile.Legacy, "Legacy marker did not resolve");
        Require(TerminalSessionProfile.Read(graphics, SessionId) == TerminalSemanticProfile.Graphics, "Graphics marker did not resolve");
        Require(TerminalSessionProfile.Read(graphicsUnicode16, SessionId) == TerminalSemanticProfile.GraphicsUnicode16,
            "Unicode16 marker did not resolve");
        Require(TerminalSessionProfile.ValidateExpected(graphics, SessionId, TerminalSemanticProfile.Graphics) == TerminalSemanticProfile.Graphics,
            "Graphics expected validation failed");
        Require(TerminalSessionProfile.ValidateExpected(legacy, SessionId, TerminalSemanticProfile.Legacy) == TerminalSemanticProfile.Legacy,
            "Legacy expected validation failed");
        RejectMarker(TerminalSessionProfile.Mismatch, () => TerminalSessionProfile.Validate(graphics, SessionId));
        RejectMarker(TerminalSessionProfile.Mismatch, () => TerminalSessionProfile.ValidateExpected(legacy, SessionId, TerminalSemanticProfile.Graphics));
        RejectMarker(TerminalSessionProfile.Mismatch, () => TerminalSessionProfile.ValidateExpected(graphics, SessionId, TerminalSemanticProfile.Legacy));
        RejectMarker(TerminalSessionProfile.Mismatch, () => TerminalSessionProfile.ValidateExpected(graphics, SessionId, TerminalSemanticProfile.GraphicsUnicode16));
        RejectMarker(TerminalSessionProfile.Mismatch, () => TerminalSessionProfile.ValidateExpected(graphicsUnicode16, SessionId, TerminalSemanticProfile.Graphics));
        RejectMarker(TerminalSessionProfile.Invalid, () => TerminalSessionProfile.Read(graphics, Guid.NewGuid()));
        foreach (var unknown in new[] { Graphics + " ", Graphics + ";extra=1", Graphics.ToUpperInvariant(),
            Graphics.Replace("width=1", "width=2"), GraphicsUnicode16.Replace("width=2", "width=1"),
            Graphics.Replace("output=0", "output=1"),
            Graphics.Replace("resources=2", "resources=1"), "terminal_state_2_23_v1;width=1;output=0;resources=2" })
            RejectMarker(TerminalSessionProfile.Mismatch, () => TerminalSessionProfile.Read(Marker(unknown), SessionId));
        foreach (var invalid in new[] { new byte[0], new byte[513], Marker(Graphics + "\n"), Marker(Graphics + "\0") })
            RejectMarker(TerminalSessionProfile.Invalid, () => TerminalSessionProfile.Read(invalid, SessionId));
        RejectMarker(TerminalSessionProfile.Invalid, () => TerminalSessionProfile.Read(null, SessionId));
        var mutated = (byte[])graphics.Clone();
        mutated[mutated.Length - 1] = 0xff;
        RejectMarker(TerminalSessionProfile.Invalid, () => TerminalSessionProfile.Read(mutated, SessionId));
        for (var index = 0; index < graphics.Length; index++)
        {
            var changed = (byte[])graphics.Clone();
            changed[index] ^= 0x80;
            RejectMarker(TerminalSessionProfile.Invalid, () => TerminalSessionProfile.Read(changed, SessionId));
        }
        Reject<ArgumentNullException>(() => TerminalSessionProfile.Encode(SessionId, null));
        Reject<ArgumentNullException>(() => TerminalSessionProfile.ValidateExpected(graphics, SessionId, null));
        Require(graphics.SequenceEqual(Marker(Graphics)), "Marker validation mutated source bytes");
    }

    private static void ValidateHandshake()
    {
        const int processId = 0x01020304;
        var token = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var legacy = new byte[] { 1, 2, 3, 4 }.Concat(token).Concat(Encoding.ASCII.GetBytes(Legacy)).ToArray();
        var graphics = new byte[] { 1, 2, 3, 4 }.Concat(token).Concat(Encoding.ASCII.GetBytes(Graphics)).ToArray();
        var graphicsUnicode16 = new byte[] { 1, 2, 3, 4 }.Concat(token).Concat(Encoding.ASCII.GetBytes(GraphicsUnicode16)).ToArray();
        Require(LocalPayloadCodec.TerminalHello(processId, token).SequenceEqual(legacy), "Legacy hello bytes changed");
        Require(LocalPayloadCodec.TerminalHello(processId, token, TerminalSemanticProfile.Legacy).SequenceEqual(legacy), "Explicit legacy hello differs");
        Require(LocalPayloadCodec.TerminalHello(processId, token, TerminalSemanticProfile.Graphics).SequenceEqual(graphics), "Graphics hello bytes differ");
        Require(LocalPayloadCodec.TerminalHello(processId, token, TerminalSemanticProfile.GraphicsUnicode16).SequenceEqual(graphicsUnicode16),
            "Unicode16 hello bytes differ");
        Require(LocalPayloadCodec.ParseTerminalHello(legacy, token) == processId, "Legacy hello parsing failed");
        Require(LocalPayloadCodec.ParseTerminalHello(legacy, token, out TerminalSemanticProfile actualLegacy) == processId &&
            actualLegacy == TerminalSemanticProfile.Legacy, "Actual legacy hello profile differs");
        Require(LocalPayloadCodec.ParseTerminalHello(graphics, token, out TerminalSemanticProfile actualGraphics) == processId &&
            actualGraphics == TerminalSemanticProfile.Graphics, "Actual graphics hello profile differs");
        Require(LocalPayloadCodec.ParseTerminalHello(graphics, token, TerminalSemanticProfile.Graphics) == processId, "Expected graphics hello parsing failed");
        Require(LocalPayloadCodec.ParseTerminalHello(graphicsUnicode16, token, TerminalSemanticProfile.GraphicsUnicode16) == processId,
            "Expected Unicode16 hello parsing failed");
        Reject<TerminalLocalProtocolException>(() => LocalPayloadCodec.ParseTerminalHello(graphics, token));
        Reject<TerminalLocalProtocolException>(() => LocalPayloadCodec.ParseTerminalHello(graphics, token, TerminalSemanticProfile.Legacy));
        Reject<TerminalLocalProtocolException>(() => LocalPayloadCodec.ParseTerminalHello(legacy, token, TerminalSemanticProfile.Graphics));
        Reject<TerminalLocalProtocolException>(() => LocalPayloadCodec.ParseTerminalHello(graphics, token, TerminalSemanticProfile.GraphicsUnicode16));
        Reject<TerminalLocalProtocolException>(() => LocalPayloadCodec.ParseTerminalHello(graphicsUnicode16, token, TerminalSemanticProfile.Graphics));
        foreach (var invalid in new[] { new byte[0], new byte[35], new byte[257] })
            Reject<InvalidDataException>(() => LocalPayloadCodec.ParseTerminalHello(invalid, token, out _));
        Reject<InvalidDataException>(() => LocalPayloadCodec.ParseTerminalHello(null, token, out _));
        Reject<TerminalLocalProtocolException>(() => LocalPayloadCodec.ParseTerminalHello(graphics.Take(36).ToArray(), token, out _));
        for (var index = 4; index < 36; index++)
        {
            var changed = (byte[])graphics.Clone();
            changed[index] ^= 1;
            Reject<InvalidDataException>(() => LocalPayloadCodec.ParseTerminalHello(changed, token, out _));
        }
        for (var index = 36; index < graphics.Length; index++)
        {
            var changed = (byte[])graphics.Clone();
            changed[index] ^= 0x80;
            Reject<TerminalLocalProtocolException>(() => LocalPayloadCodec.ParseTerminalHello(changed, token, out _));
        }
        foreach (var invalidPid in new[] { 0u, uint.MaxValue })
        {
            var changed = (byte[])graphics.Clone();
            BitConverter.GetBytes(invalidPid).CopyTo(changed, 0);
            Reject<InvalidDataException>(() => LocalPayloadCodec.ParseTerminalHello(changed, token, out _));
        }
        var unauthenticatedUnknown = (byte[])graphics.Clone();
        unauthenticatedUnknown[4] ^= 1;
        unauthenticatedUnknown[36] = (byte)'?';
        TerminalSemanticProfile notPublished = TerminalSemanticProfile.Graphics;
        var error = Reject<InvalidDataException>(() => LocalPayloadCodec.ParseTerminalHello(unauthenticatedUnknown, token, out notPublished));
        Require(error.Message == "Local IPC handshake token mismatch.", "Profile parsed before authenticating identity");
        Require(notPublished == null, "Failed authentication published a profile");
        Reject<ArgumentNullException>(() => LocalPayloadCodec.TerminalHello(processId, token, null));
        Reject<ArgumentNullException>(() => LocalPayloadCodec.ParseTerminalHello(graphics, token, (TerminalSemanticProfile)null));
        Require(token.SequenceEqual(Enumerable.Range(0, 32).Select(i => (byte)i)), "Hello encoding or parsing mutated caller token");
        Require(graphics.SequenceEqual(new byte[] { 1, 2, 3, 4 }.Concat(token).Concat(Encoding.ASCII.GetBytes(Graphics))), "Hello parsing mutated caller bytes");
    }

    private static byte[] Marker(string descriptor) => Encoding.ASCII.GetBytes(
        "TermLink terminal profile V1\n00112233445566778899aabbccddeeff\n" + descriptor);

    private static void RejectMarker(string code, Action action)
    {
        var error = Reject<TerminalHistoryProfileException>(action);
        Require(error.Code == code, "Incorrect marker failure classification: " + error.Code);
    }

    private static T Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) when (error.GetType() == typeof(T)) { checks++; return error; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
        checks++;
    }
}
