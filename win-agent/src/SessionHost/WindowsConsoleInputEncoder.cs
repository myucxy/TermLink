using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.SessionHost
{
    /// <summary>
    /// Adapts lossless Win32 console records to the terminal-core protocol selected by current
    /// terminal state. DEC 9001 preserves the record exactly; all other modes use Kitty V3,
    /// which also owns legacy VT, application cursor/keypad, and modifyOtherKeys encoding.
    /// </summary>
    internal sealed class WindowsConsoleInputEncoder
    {
        private const uint EnhancedKey = 0x0100;
        private const uint RightAltPressed = 0x0001;
        private const uint LeftAltPressed = 0x0002;
        private const uint RightCtrlPressed = 0x0004;
        private const uint LeftCtrlPressed = 0x0008;
        private const uint ShiftPressed = 0x0010;
        private const uint NumLockOn = 0x0020;
        private const uint CapsLockOn = 0x0080;

        private readonly NativeTerminal terminal;
        private readonly HashSet<uint> pressedKeys = new HashSet<uint>();
        private NativeTerminalWin32KeyEvent? pendingHighSurrogate;

        internal WindowsConsoleInputEncoder(NativeTerminal terminal)
        {
            this.terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        }

        internal byte[] Encode(NativeTerminalWin32KeyEvent keyEvent)
        {
            using (var output = new MemoryStream())
            {
                if (pendingHighSurrogate.HasValue)
                {
                    var high = pendingHighSurrogate.Value;
                    pendingHighSurrogate = null;
                    if (char.IsLowSurrogate(keyEvent.UnicodeCharacter) &&
                        high.KeyDown == keyEvent.KeyDown)
                    {
                        Append(output, EncodeSemantic(
                            high,
                            new Rune(high.UnicodeCharacter, keyEvent.UnicodeCharacter)));
                        return output.ToArray();
                    }

                    Append(output, EncodeSemantic(high, Rune.ReplacementChar));
                }

                if (terminal.TryEncodeWin32Key(keyEvent, out var win32Bytes))
                {
                    TrackPhysicalState(keyEvent);
                    Append(output, win32Bytes);
                    return output.ToArray();
                }

                if (char.IsHighSurrogate(keyEvent.UnicodeCharacter))
                {
                    pendingHighSurrogate = keyEvent;
                    return output.ToArray();
                }
                if (char.IsLowSurrogate(keyEvent.UnicodeCharacter))
                {
                    Append(output, EncodeSemantic(keyEvent, Rune.ReplacementChar));
                    return output.ToArray();
                }

                Append(output, EncodeSemantic(keyEvent, null));
                return output.ToArray();
            }
        }

        private byte[] EncodeSemantic(
            NativeTerminalWin32KeyEvent keyEvent,
            Rune? characterOverride)
        {
            var identity = ResolveIdentity(keyEvent, characterOverride, out var actualText);
            if (!identity.HasValue)
            {
                TrackPhysicalState(keyEvent);
                return Array.Empty<byte>();
            }

            var signature = PhysicalSignature(keyEvent);
            var wasPressed = pressedKeys.Contains(signature);
            TrackPhysicalState(keyEvent);
            var repetitions = keyEvent.KeyDown
                ? Math.Max(1, (int)keyEvent.RepeatCount)
                : 1;
            var modifiers = ResolveModifiers(keyEvent.ControlKeyState);
            var baseLayout = ResolveBaseLayoutKey(keyEvent.VirtualKey);
            var shiftedKey = (modifiers & NativeTerminalKittyModifiers.Shift) != 0 &&
                actualText.HasValue && baseLayout.HasValue && actualText.Value != baseLayout.Value
                    ? actualText
                    : null;

            using (var output = new MemoryStream())
            {
                for (var index = 0; index < repetitions; index++)
                {
                    var eventType = keyEvent.KeyDown
                        ? wasPressed || index != 0
                            ? NativeTerminalKittyKeyEventType.Repeat
                            : NativeTerminalKittyKeyEventType.Press
                        : NativeTerminalKittyKeyEventType.Release;
                    var associatedText = keyEvent.KeyDown && actualText.HasValue
                        ? actualText.Value.ToString()
                        : null;
                    var key = new NativeTerminalKittyKeyEvent(
                        eventType,
                        modifiers,
                        identity.Value,
                        shiftedKey,
                        baseLayout,
                        associatedText);
                    try
                    {
                        Append(output, terminal.EncodeKittyKey(key));
                    }
                    catch (NativeTerminalException error)
                        when (error.Status == NativeTerminalStatus.Unsupported)
                    {
                        Append(output, EncodeLegacyFallback(
                            keyEvent.KeyDown,
                            identity.Value,
                            associatedText));
                    }
                }
                return output.ToArray();
            }
        }

        private byte[] EncodeLegacyFallback(
            bool keyDown,
            NativeTerminalKittyKeyIdentity identity,
            string associatedText)
        {
            if (!keyDown) return Array.Empty<byte>();
            if (!string.IsNullOrEmpty(associatedText))
            {
                return terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    NativeTerminalKittyKeyIdentity.UnidentifiedText,
                    associatedText: associatedText));
            }
            return terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                NativeTerminalKittyKeyEventType.Press,
                NativeTerminalKittyModifiers.None,
                identity));
        }

        private static NativeTerminalKittyKeyIdentity? ResolveIdentity(
            NativeTerminalWin32KeyEvent keyEvent,
            Rune? characterOverride,
            out Rune? actualText)
        {
            actualText = null;
            var functional = ResolveFunctionalIdentity(keyEvent);
            if (functional.HasValue) return functional;

            Rune character;
            if (characterOverride.HasValue)
            {
                character = characterOverride.Value;
            }
            else if (keyEvent.UnicodeCharacter != '\0' &&
                !char.IsSurrogate(keyEvent.UnicodeCharacter) &&
                !IsC0OrC1(keyEvent.UnicodeCharacter))
            {
                character = new Rune(keyEvent.UnicodeCharacter);
            }
            else if (keyEvent.VirtualKey >= 0x41 && keyEvent.VirtualKey <= 0x5a)
            {
                character = new Rune((char)('a' + keyEvent.VirtualKey - 0x41));
            }
            else if (keyEvent.VirtualKey >= 0x30 && keyEvent.VirtualKey <= 0x39)
            {
                character = new Rune((char)keyEvent.VirtualKey);
            }
            else
            {
                return null;
            }

            if (characterOverride.HasValue ||
                (keyEvent.UnicodeCharacter != '\0' &&
                    !char.IsSurrogate(keyEvent.UnicodeCharacter) &&
                    !IsC0OrC1(keyEvent.UnicodeCharacter)))
            {
                actualText = characterOverride ?? new Rune(keyEvent.UnicodeCharacter);
            }

            var baseLayout = ResolveBaseLayoutKey(keyEvent.VirtualKey);
            return NativeTerminalKittyKeyIdentity.Character(baseLayout ?? character);
        }

        private static NativeTerminalKittyKeyIdentity? ResolveFunctionalIdentity(
            NativeTerminalWin32KeyEvent keyEvent)
        {
            var enhanced = (keyEvent.ControlKeyState & EnhancedKey) != 0;
            switch (keyEvent.VirtualKey)
            {
                case 0x08: return Common(NativeTerminalKey.Backspace);
                case 0x09: return Common(NativeTerminalKey.Tab);
                case 0x0d: return Common(enhanced
                    ? NativeTerminalKey.KeypadEnter
                    : NativeTerminalKey.Enter);
                case 0x1b: return Common(NativeTerminalKey.Escape);
                case 0x21: return enhanced ? Common(NativeTerminalKey.PageUp) : Private(57_421);
                case 0x22: return enhanced ? Common(NativeTerminalKey.PageDown) : Private(57_422);
                case 0x23: return enhanced ? Common(NativeTerminalKey.End) : Private(57_424);
                case 0x24: return enhanced ? Common(NativeTerminalKey.Home) : Private(57_423);
                case 0x25: return enhanced ? Common(NativeTerminalKey.ArrowLeft) : Private(57_417);
                case 0x26: return enhanced ? Common(NativeTerminalKey.ArrowUp) : Private(57_419);
                case 0x27: return enhanced ? Common(NativeTerminalKey.ArrowRight) : Private(57_418);
                case 0x28: return enhanced ? Common(NativeTerminalKey.ArrowDown) : Private(57_420);
                case 0x2d: return enhanced ? Common(NativeTerminalKey.Insert) : Private(57_425);
                case 0x2e: return enhanced ? Common(NativeTerminalKey.Delete) : Private(57_426);
                case 0x0c: return Private(57_427);
                case 0x2c: return Private(57_361);
                case 0x13: return Private(57_362);
                case 0x14: return Private(57_358);
                case 0x90: return Private(57_360);
                case 0x91: return Private(57_359);
                case 0x5d: return Private(57_363);
                case 0x5b: return Private(57_444);
                case 0x5c: return Private(57_450);
                case 0xa0: return Private(57_441);
                case 0xa1: return Private(57_447);
                case 0xa2: return Private(57_442);
                case 0xa3: return Private(57_448);
                case 0xa4: return Private(57_443);
                case 0xa5: return Private(57_449);
                case 0x10: return Private(keyEvent.ScanCode == 0x36 ? 57_447U : 57_441U);
                case 0x11: return Private(enhanced ? 57_448U : 57_442U);
                case 0x12: return Private(enhanced ? 57_449U : 57_443U);
                case 0xad: return Private(57_440);
                case 0xae: return Private(57_438);
                case 0xaf: return Private(57_439);
                case 0xb0: return Private(57_436);
                case 0xb1: return Private(57_437);
                case 0xb2: return Private(57_432);
                case 0xb3: return Private(57_430);
            }

            if (keyEvent.VirtualKey >= 0x70 && keyEvent.VirtualKey <= 0x7b)
                return Common((NativeTerminalKey)(
                    (uint)NativeTerminalKey.F1 + keyEvent.VirtualKey - 0x70));
            if (keyEvent.VirtualKey >= 0x7c && keyEvent.VirtualKey <= 0x87)
                return Private(57_363U + keyEvent.VirtualKey - 0x6fU);
            if (keyEvent.VirtualKey >= 0x60 && keyEvent.VirtualKey <= 0x69)
                return Common((NativeTerminalKey)(
                    (uint)NativeTerminalKey.Keypad0 + keyEvent.VirtualKey - 0x60));
            switch (keyEvent.VirtualKey)
            {
                case 0x6e: return Common(NativeTerminalKey.KeypadDecimal);
                case 0x6c: return Common(NativeTerminalKey.KeypadSeparator);
                case 0x6b: return Common(NativeTerminalKey.KeypadAdd);
                case 0x6d: return Common(NativeTerminalKey.KeypadSubtract);
                case 0x6a: return Common(NativeTerminalKey.KeypadMultiply);
                case 0x6f: return Common(NativeTerminalKey.KeypadDivide);
                default: return null;
            }
        }

        private static NativeTerminalKittyKeyIdentity Common(NativeTerminalKey key) =>
            NativeTerminalKittyKeyIdentity.Functional(key);

        private static NativeTerminalKittyKeyIdentity Private(uint key) =>
            NativeTerminalKittyKeyIdentity.Functional(key);

        private static Rune? ResolveBaseLayoutKey(ushort virtualKey)
        {
            if (virtualKey >= 0x41 && virtualKey <= 0x5a)
                return new Rune((char)('a' + virtualKey - 0x41));
            if (virtualKey >= 0x30 && virtualKey <= 0x39)
                return new Rune((char)virtualKey);
            return null;
        }

        private static NativeTerminalKittyModifiers ResolveModifiers(uint state)
        {
            var modifiers = NativeTerminalKittyModifiers.None;
            if ((state & ShiftPressed) != 0) modifiers |= NativeTerminalKittyModifiers.Shift;
            if ((state & (LeftAltPressed | RightAltPressed)) != 0)
                modifiers |= NativeTerminalKittyModifiers.Alt;
            if ((state & (LeftCtrlPressed | RightCtrlPressed)) != 0)
                modifiers |= NativeTerminalKittyModifiers.Control;
            if ((state & CapsLockOn) != 0) modifiers |= NativeTerminalKittyModifiers.CapsLock;
            if ((state & NumLockOn) != 0) modifiers |= NativeTerminalKittyModifiers.NumLock;
            return modifiers;
        }

        private void TrackPhysicalState(NativeTerminalWin32KeyEvent keyEvent)
        {
            var signature = PhysicalSignature(keyEvent);
            if (keyEvent.KeyDown) pressedKeys.Add(signature);
            else pressedKeys.Remove(signature);
        }

        private static uint PhysicalSignature(NativeTerminalWin32KeyEvent keyEvent) =>
            ((uint)keyEvent.VirtualKey << 16) | keyEvent.ScanCode;

        private static bool IsC0OrC1(char value) =>
            value <= 0x1f || (value >= 0x7f && value <= 0x9f);

        private static void Append(MemoryStream output, byte[] bytes)
        {
            if (bytes != null && bytes.Length != 0)
                output.Write(bytes, 0, bytes.Length);
        }
    }
}
