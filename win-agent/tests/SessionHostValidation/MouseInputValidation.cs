using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TermLink.LocalIpc;
using TermLink.SessionHost;
using TermLink.TerminalCore;

internal static class MouseInputValidation
{
    internal static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        Check(Marshal.SizeOf<WindowsConsoleMouseRecord>() == 16, "Win32 MOUSE_EVENT_RECORD layout changed");
        var decoder = new WindowsConsoleMouseInput();
        NativeTerminalMouseEvent[] Decode(uint buttons, uint flags = 0, uint modifiers = 0, short x = 14, short y = 25) =>
            decoder.Decode(new WindowsConsoleMouseRecord { X = x, Y = y, ButtonState = buttons,
                EventFlags = flags, ControlKeyState = modifiers }, 10, 20, 80, 24).ToArray();
        uint Wheel(int delta) => unchecked((uint)(delta << 16));
        string Sgr(NativeTerminalMouseEvent input) => Encoding.ASCII.GetString(WindowsConsoleMouseInput.EncodeStandalone(input));
        Check(Sgr(Decode(1).Single()) == "\u001b[<0;5;6M", "left press / viewport origin lost");
        Check(Decode(1, 2).Length == 0, "double click duplicated an already held press");
        Check(Sgr(Decode(1, 1, 0x1a).Single()) == "\u001b[<60;5;6M", "drag modifiers lost");
        Check(Sgr(Decode(0, x: -1, y: 99).Single()) == "\u001b[<0;1;24m", "edge release lost");
        Check(Sgr(Decode(2).Single()) == "\u001b[<2;5;6M", "right click became left click");
        var transitions = Decode(4);
        Check(transitions.Length == 2 && transitions[0].Action == NativeTerminalMouseAction.Press &&
            transitions[1].Action == NativeTerminalMouseAction.Release, "simultaneous button transitions lost");
        Decode(0);
        Check(Decode(Wheel(60), 4).Length == 0, "partial vertical wheel emitted early");
        Check(Sgr(Decode(Wheel(60), 4).Single()) == "\u001b[<64;5;6M", "partial vertical wheel lost");
        Check(Decode(Wheel(-240), 4).Select(Sgr).SequenceEqual(new[] { "\u001b[<65;5;6M", "\u001b[<65;5;6M" }),
            "wheel magnitude/direction lost");
        Check(Decode(Wheel(60), 8).Length == 0 && Decode(Wheel(-60), 4).Length == 0,
            "wheel axes shared a remainder");
        Check(Sgr(Decode(Wheel(60), 8).Single()) == "\u001b[<67;5;6M", "horizontal right wheel lost");
        Check(Sgr(Decode(Wheel(-120), 8).Single()) == "\u001b[<66;5;6M", "horizontal left wheel lost");
        Check(!WindowsConsoleMouseInput.IsUserAction(Decode(0, 1).Single()), "hover steals control lease");
        Check(WindowsConsoleMouseInput.IsUserAction(Decode(1).Single()), "click does not reclaim local control");
        Decode(0);

        decoder = new WindowsConsoleMouseInput();
        using var authority = new TerminalCoreAuthority(80, 24, _ => { }, (_, _, _, _) => { });
        var id = Guid.NewGuid();
        ulong seq = 1;
        void Feed(string bytes) => authority.ApplyDurable(new LocalFrame(LocalFrameType.Output, id, seq++, Encoding.ASCII.GetBytes(bytes)));
        var emitted = new System.Collections.Generic.List<string>();
        bool Send(NativeTerminalMouseEvent input) => authority.EncodeAndWriteConsoleMouse(input,
            bytes => { emitted.Add(Encoding.Latin1.GetString(bytes)); return true; });
        var up = Decode(Wheel(120), 4).Single();
        Send(up);
        Check(emitted.Count == 0, "mouse disabled should not write input or reclaim control");
        foreach (var c in "\u001b[?1003;1006h") Feed(c.ToString());
        Send(up);
        Check(emitted.Single() == "\u001b[<64;5;6M", "managed SGR wheel failed");
        Feed("\u001b[?1003;1006l");
        Send(up);
        Check(emitted.Count == 1, "mouse reset kept reporting");
        Feed("\u001b[?1000h");
        Send(up);
        Check(emitted.Last() == "\u001b[M`%&", "legacy mouse protocol bypassed terminal-core");
        Feed("\u001b[?1000l\u001b[?1003;1006h");
        var stale = NativeTerminalMouseEvent.Move(NativeTerminalMouseButtons.None, NativeTerminalMousePosition.Cell(500, 500));
        Check(Send(stale), "stale resized cell closed input");
        Check(emitted.Count == 2, "invalid position leaked bytes");
        Check(!authority.EncodeAndWriteConsoleMouse(up, _ => false), "write failure swallowed");
        Feed("\u001b[?1016h");
        Check(Send(up) && emitted.Count == 2, "cell-only input cannot invent mode 1016 pixels");
        Feed("\u001b[?1016l\u001b[?1006h");
        Send(up);
        Check(emitted.Count == 3, "pixel rejection damaged later cell mouse input");
        Feed("\u001bc");
        Send(up);
        Check(emitted.Count == 3, "RIS did not release mouse mode");
        authority.ApplyDurable(new LocalFrame(LocalFrameType.Resize, id, seq++, LocalPayloadCodec.Resize(300, 24)));
        Feed("\u001b[?1000h");
        var wide = NativeTerminalMouseEvent.Wheel(NativeTerminalMouseWheelDirection.Up,
            NativeTerminalMouseButtons.None, NativeTerminalMousePosition.Cell(240, 5));
        Check(Send(wide) && emitted.Count == 3, "legacy coordinate overflow closed input");
        Send(up);
        Check(emitted.Count == 4, "legacy overflow damaged later mouse input");
        Console.WriteLine("Windows mouse record mapping and authority validation passed.");
    }
}
