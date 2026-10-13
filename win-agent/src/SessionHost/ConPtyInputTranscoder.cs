using System;

namespace TermLink.SessionHost;

// ConPTY's pipe is always UTF-8, including when an application requests C1.
// Keep UTF-8 continuation state across physical writes so CJK/emoji bytes
// cannot be mistaken for standalone controls at a transport chunk boundary.
internal sealed class ConPtyInputTranscoder
{
    private Utf8State state;

    internal byte[] Convert(byte[] bytes, int length, out int wireLength)
    {
        if (bytes == null || (uint)length > (uint)bytes.Length) throw new ArgumentOutOfRangeException(nameof(length));
        var before = state; var extra = 0;
        for (var i = 0; i < length; i++) if (state.StandaloneC1(bytes[i])) extra++;
        wireLength = checked(length + extra);
        if (extra == 0) return bytes;
        var result = new byte[wireLength]; var offset = 0;
        for (var i = 0; i < length; i++) {
            if (before.StandaloneC1(bytes[i])) result[offset++] = 0xc2;
            result[offset++] = bytes[i];
        }
        return result;
    }

    private struct Utf8State
    {
        private int remaining;
        private byte minimum, maximum;
        internal bool StandaloneC1(byte value)
        {
            if (remaining != 0) {
                if (value >= minimum && value <= maximum) {
                    remaining--; minimum = 0x80; maximum = 0xbf; return false;
                }
                remaining = 0;
            }
            minimum = 0x80; maximum = 0xbf;
            if (value >= 0xc2 && value <= 0xdf) remaining = 1;
            else if (value >= 0xe0 && value <= 0xef) { remaining = 2; if (value == 0xe0) minimum = 0xa0; if (value == 0xed) maximum = 0x9f; }
            else if (value >= 0xf0 && value <= 0xf4) { remaining = 3; if (value == 0xf0) minimum = 0x90; if (value == 0xf4) maximum = 0x8f; }
            return value >= 0x80 && value <= 0x9f;
        }
    }
}
