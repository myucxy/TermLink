using System;
using System.Collections.Generic;
using System.Text;

namespace TermLink.TerminalCore
{
    public sealed class VtTerminalInputEncoder : ITerminalInputEncoder
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public byte[] Encode(TerminalInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            byte[] encoded;
            switch (input.Kind)
            {
                case TerminalInputKind.Text:
                    encoded = EncodeText(input.Text, input.Modifiers);
                    break;
                case TerminalInputKind.Key:
                    encoded = EncodeKey(input.Key, input.Modifiers);
                    break;
                case TerminalInputKind.Paste:
                case TerminalInputKind.RawBytes:
                    if (input.Modifiers != TerminalInputModifiers.None)
                        throw new ArgumentException("Paste/raw input cannot carry modifiers.", nameof(input));
                    encoded = input.Bytes.ToArray();
                    break;
                default:
                    throw new ArgumentException("Unsupported terminal input kind.", nameof(input));
            }
            if (encoded.Length == 0) throw new ArgumentException("Terminal input must not encode to an empty payload.", nameof(input));
            return encoded;
        }

        private static byte[] EncodeText(string text, TerminalInputModifiers modifiers)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("Text input must not be empty.", nameof(text));
            var value = text;
            if ((modifiers & TerminalInputModifiers.Control) != 0)
            {
                if (text.Length != 1 || text[0] > 0x7f)
                    throw new ArgumentException("Control text must contain one ASCII character.", nameof(text));
                value = new string((char)ControlByte(text[0]), 1);
            }
            var bytes = Utf8.GetBytes(value);
            return PrefixEscapeIfNeeded(bytes, modifiers);
        }

        private static byte[] EncodeKey(TerminalKey key, TerminalInputModifiers modifiers)
        {
            string sequence;
            switch (key)
            {
                case TerminalKey.Enter: sequence = "\r"; break;
                case TerminalKey.Tab: sequence = (modifiers & TerminalInputModifiers.Shift) != 0 ? "\u001b[Z" : "\t"; break;
                case TerminalKey.Backspace: sequence = "\u007f"; break;
                case TerminalKey.Escape: sequence = "\u001b"; break;
                case TerminalKey.Delete: sequence = CsiTilde(3, modifiers); break;
                case TerminalKey.Insert: sequence = CsiTilde(2, modifiers); break;
                case TerminalKey.Home: sequence = CsiFinal('H', modifiers); break;
                case TerminalKey.End: sequence = CsiFinal('F', modifiers); break;
                case TerminalKey.PageUp: sequence = CsiTilde(5, modifiers); break;
                case TerminalKey.PageDown: sequence = CsiTilde(6, modifiers); break;
                case TerminalKey.ArrowUp: sequence = CsiFinal('A', modifiers); break;
                case TerminalKey.ArrowDown: sequence = CsiFinal('B', modifiers); break;
                case TerminalKey.ArrowRight: sequence = CsiFinal('C', modifiers); break;
                case TerminalKey.ArrowLeft: sequence = CsiFinal('D', modifiers); break;
                case TerminalKey.F1: sequence = FunctionKey("P", 0, modifiers); break;
                case TerminalKey.F2: sequence = FunctionKey("Q", 0, modifiers); break;
                case TerminalKey.F3: sequence = FunctionKey("R", 0, modifiers); break;
                case TerminalKey.F4: sequence = FunctionKey("S", 0, modifiers); break;
                case TerminalKey.F5: sequence = FunctionKey(null, 15, modifiers); break;
                case TerminalKey.F6: sequence = FunctionKey(null, 17, modifiers); break;
                case TerminalKey.F7: sequence = FunctionKey(null, 18, modifiers); break;
                case TerminalKey.F8: sequence = FunctionKey(null, 19, modifiers); break;
                case TerminalKey.F9: sequence = FunctionKey(null, 20, modifiers); break;
                case TerminalKey.F10: sequence = FunctionKey(null, 21, modifiers); break;
                case TerminalKey.F11: sequence = FunctionKey(null, 23, modifiers); break;
                case TerminalKey.F12: sequence = FunctionKey(null, 24, modifiers); break;
                default: throw new ArgumentException("Unsupported terminal key.", nameof(key));
            }
            var bytes = Encoding.ASCII.GetBytes(sequence);
            // Modifiers are already encoded in CSI/SS3 key sequences. Escape,
            // Enter, Tab and Backspace use an Alt/Meta ESC prefix.
            if (key == TerminalKey.Enter || key == TerminalKey.Tab || key == TerminalKey.Backspace || key == TerminalKey.Escape)
                return PrefixEscapeIfNeeded(bytes, modifiers);
            return bytes;
        }

        private static string CsiFinal(char final, TerminalInputModifiers modifiers)
        {
            var parameter = ModifierParameter(modifiers);
            return parameter == 1 ? "\u001b[" + final : "\u001b[1;" + parameter + final;
        }

        private static string CsiTilde(int key, TerminalInputModifiers modifiers)
        {
            var parameter = ModifierParameter(modifiers);
            return parameter == 1 ? $"\u001b[{key}~" : $"\u001b[{key};{parameter}~";
        }

        private static string FunctionKey(string ss3Final, int tildeKey, TerminalInputModifiers modifiers)
        {
            var parameter = ModifierParameter(modifiers);
            if (ss3Final != null)
                return parameter == 1 ? "\u001bO" + ss3Final : $"\u001b[1;{parameter}{ss3Final}";
            return parameter == 1 ? $"\u001b[{tildeKey}~" : $"\u001b[{tildeKey};{parameter}~";
        }

        private static int ModifierParameter(TerminalInputModifiers modifiers)
        {
            var parameter = 1;
            if ((modifiers & TerminalInputModifiers.Shift) != 0) parameter += 1;
            if ((modifiers & TerminalInputModifiers.Alt) != 0) parameter += 2;
            if ((modifiers & TerminalInputModifiers.Control) != 0) parameter += 4;
            if ((modifiers & TerminalInputModifiers.Meta) != 0) parameter += 8;
            return parameter;
        }

        private static byte[] PrefixEscapeIfNeeded(byte[] bytes, TerminalInputModifiers modifiers)
        {
            if ((modifiers & (TerminalInputModifiers.Alt | TerminalInputModifiers.Meta)) == 0) return bytes;
            var result = new byte[bytes.Length + 1];
            result[0] = 0x1b;
            Buffer.BlockCopy(bytes, 0, result, 1, bytes.Length);
            return result;
        }

        private static byte ControlByte(char value)
        {
            if (value >= 'a' && value <= 'z') value = char.ToUpperInvariant(value);
            if (value >= '@' && value <= '_') return (byte)(value & 0x1f);
            if (value == '?') return 0x7f;
            if (value == ' ') return 0;
            throw new ArgumentException("Character has no terminal control encoding.", nameof(value));
        }
    }
}
