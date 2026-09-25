using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.SessionHost
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowsConsoleMouseRecord
    {
        public short X, Y;
        public uint ButtonState, ControlKeyState, EventFlags;
    }

    /// <summary>Win32 records contain buffer cells and signed wheel deltas, not VT bytes.</summary>
    internal sealed class WindowsConsoleMouseInput
    {
        private NativeTerminalMouseButtons held;
        private int verticalRemainder, horizontalRemainder;

        public IReadOnlyList<NativeTerminalMouseEvent> Decode(
            WindowsConsoleMouseRecord record, int windowLeft, int windowTop, int columns, int rows)
        {
            var result = new List<NativeTerminalMouseEvent>();
            if (columns < 1 || rows < 1 || (record.EventFlags & ~0x0fU) != 0) return result;
            // Buffer origin can move while Terminal browses history. Clamp captured releases
            // at an edge rather than dropping them and leaving a button pressed remotely.
            var position = NativeTerminalMousePosition.Cell(
                (uint)Math.Clamp(record.X - windowLeft, 0, columns - 1),
                (uint)Math.Clamp(record.Y - windowTop, 0, rows - 1));
            var buttons = NativeTerminalMouseButtons.None;
            if ((record.ButtonState & 1) != 0) buttons |= NativeTerminalMouseButtons.Left;
            if ((record.ButtonState & 2) != 0) buttons |= NativeTerminalMouseButtons.Right;
            if ((record.ButtonState & 4) != 0) buttons |= NativeTerminalMouseButtons.Middle;
            var modifiers = NativeTerminalModifiers.None;
            if ((record.ControlKeyState & 0x10) != 0) modifiers |= NativeTerminalModifiers.Shift;
            if ((record.ControlKeyState & 0x03) != 0) modifiers |= NativeTerminalModifiers.Alt;
            if ((record.ControlKeyState & 0x0c) != 0) modifiers |= NativeTerminalModifiers.Control;
            foreach (var button in new[] { NativeTerminalMouseButton.Left, NativeTerminalMouseButton.Middle, NativeTerminalMouseButton.Right })
            {
                var flag = button == NativeTerminalMouseButton.Left ? NativeTerminalMouseButtons.Left :
                    button == NativeTerminalMouseButton.Middle ? NativeTerminalMouseButtons.Middle : NativeTerminalMouseButtons.Right;
                if ((buttons & flag) == (held & flag)) continue;
                if ((buttons & flag) != 0)
                {
                    held |= flag;
                    result.Add(NativeTerminalMouseEvent.Press(button, held, position, modifiers));
                }
                else
                {
                    held &= ~flag;
                    result.Add(NativeTerminalMouseEvent.Release(button, held, position, modifiers));
                }
            }
            // DOUBLE_CLICK is the second press, already represented by the button transition.
            if ((record.EventFlags & 1) != 0)
                result.Add(NativeTerminalMouseEvent.Move(held, position, modifiers));
            if ((record.EventFlags & 0x0c) != 0)
            {
                var horizontal = (record.EventFlags & 8) != 0;
                ref var remainder = ref (horizontal ? ref horizontalRemainder : ref verticalRemainder);
                remainder += unchecked((short)(record.ButtonState >> 16));
                var steps = remainder / 120;
                remainder %= 120;
                var direction = horizontal
                    ? (steps > 0 ? NativeTerminalMouseWheelDirection.Right : NativeTerminalMouseWheelDirection.Left)
                    : (steps > 0 ? NativeTerminalMouseWheelDirection.Up : NativeTerminalMouseWheelDirection.Down);
                for (var i = 0; i < Math.Abs(steps); i++)
                    result.Add(NativeTerminalMouseEvent.Wheel(direction, held, position, modifiers));
            }
            return result;
        }

        internal static bool IsUserAction(NativeTerminalMouseEvent input) =>
            input.Action != NativeTerminalMouseAction.Move || input.ButtonsHeld != NativeTerminalMouseButtons.None;

        // Standalone Host has no authoritative parser. SGR is the ConPTY transport's
        // lossless cell-mouse representation; ConPTY delivers records to its child.
        // Managed sessions use terminal-core's negotiated encoder instead.
        internal static byte[] EncodeStandalone(NativeTerminalMouseEvent input)
        {
            int ButtonCode(NativeTerminalMouseButton? button) => button switch
            {
                NativeTerminalMouseButton.Left => 0,
                NativeTerminalMouseButton.Middle => 1,
                NativeTerminalMouseButton.Right => 2,
                _ => 3,
            };
            var code = input.Action == NativeTerminalMouseAction.Wheel ? input.WheelDirection switch
            {
                NativeTerminalMouseWheelDirection.Up => 64,
                NativeTerminalMouseWheelDirection.Down => 65,
                NativeTerminalMouseWheelDirection.Left => 66,
                _ => 67,
            } : input.Action == NativeTerminalMouseAction.Move
                ? 32 + ((input.ButtonsHeld & NativeTerminalMouseButtons.Left) != 0 ? 0 :
                    (input.ButtonsHeld & NativeTerminalMouseButtons.Middle) != 0 ? 1 :
                    (input.ButtonsHeld & NativeTerminalMouseButtons.Right) != 0 ? 2 : 3)
                : ButtonCode(input.Button);
            if ((input.Modifiers & NativeTerminalModifiers.Shift) != 0) code |= 4;
            if ((input.Modifiers & NativeTerminalModifiers.Alt) != 0) code |= 8;
            if ((input.Modifiers & NativeTerminalModifiers.Control) != 0) code |= 16;
            return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"\u001b[<{code};{input.Position.CellColumn + 1};{input.Position.CellRow + 1}{(input.Action == NativeTerminalMouseAction.Release ? 'm' : 'M')}"));
        }
    }
}
