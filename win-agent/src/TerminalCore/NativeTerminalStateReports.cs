using System;
using System.Text;

namespace TermLink.TerminalCore;

/// <summary>Bounded DEC reports; also shared by outer-terminal reply ownership checks.</summary>
public static class NativeTerminalStateReports
{
    // 1 = cursor, 2 = tabs, 3 = palette, 4 = UPSS. Zero means unsupported.
    public static int Classify(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 7 || payload.Length > 16384 || !payload.StartsWith("\u001bP"u8) || !payload.EndsWith("\u001b\\"u8)) return 0;
        var bodyBytes = payload.Slice(2, payload.Length - 4);
        foreach (var value in bodyBytes) if (value < 32 || value > 126) return 0;
        var body = Encoding.ASCII.GetString(bodyBytes);
        if (body.StartsWith("1$u", StringComparison.Ordinal)) return Cursor(body.Substring(3)) ? 1 : 0;
        if (body.StartsWith("2$u", StringComparison.Ordinal)) return Tabs(body.Substring(3)) ? 2 : 0;
        if (body.StartsWith("2$s", StringComparison.Ordinal)) return Colors(body.Substring(3)) ? 3 : 0;
        if (body.StartsWith("0!u", StringComparison.Ordinal) || body.StartsWith("1!u", StringComparison.Ordinal))
            return body.Substring(3) != "<" && CharsetId(body.Substring(3), body[0] == '1') ? 4 : 0;
        return 0;
    }

    private static bool Number(string text, int maximum, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 5) return false;
        foreach (var digit in text)
        {
            if (digit < '0' || digit > '9') return false;
            value = value * 10 + digit - '0';
            if (value > maximum) return false;
        }
        return true;
    }

    private static bool Flags(string text, int mask, out int value)
    {
        value = text.Length == 1 ? text[0] & mask : 0;
        return text.Length == 1 && (text[0] & ~mask) == 64;
    }

    private static bool Cursor(string data)
    {
        if (data.Length > 128) return false;
        var fields = data.Split(';');
        if (fields.Length != 10 || !Number(fields[0], 512, out _) || !Number(fields[1], 512, out _) ||
            !Number(fields[2], 65534, out _) || !Flags(fields[3], 31, out _) || !Flags(fields[4], 1, out _) ||
            !Flags(fields[5], 15, out var flags) || (flags & 6) == 6 || !Number(fields[6], 3, out _) ||
            !Number(fields[7], 3, out _) || !Flags(fields[8], 15, out var sizes)) return false;
        var ids = fields[9];
        var offset = 0;
        for (var register = 0; register < 4; register++)
        {
            if (offset >= ids.Length) return false;
            var start = offset;
            while (offset < ids.Length && ids[offset] >= 32 && ids[offset] <= 47) offset++;
            if (offset >= ids.Length || offset - start > 2) return false;
            var id = ids.Substring(start, ++offset - start);
            if (!CharsetId(id, (sizes & (1 << register)) != 0)) return false;
        }
        return offset == ids.Length;
    }

    private static bool CharsetId(string id, bool size96) => size96
        ? id.Length == 1 && "ABLFHM<".IndexOf(id[0]) >= 0
        : id.Length == 1 ? "B012A45CRQ9KYE6Z7H=f`<".IndexOf(id[0]) >= 0
        : id is "%6" or "%5" or "&4" or "&5" or "\"?" or "\">" or "\"4" or "%0" or "%2" or "%=";

    private static bool Tabs(string data)
    {
        if (data.Length == 0) return true;
        var previous = 0;
        foreach (var field in data.Split('/'))
        {
            if (!Number(field, 512, out var column) || column <= previous) return false;
            previous = column;
        }
        return true;
    }

    private static bool Colors(string data)
    {
        if (data.Length == 0) return true;
        var seen = new bool[256];
        var entries = data.Split('/');
        if (entries.Length > 256) return false;
        foreach (var entry in entries)
        {
            var fields = entry.Split(';');
            if (fields.Length != 5 || !Number(fields[0], 255, out var index) || seen[index] ||
                !Number(fields[1], 2, out var model) || model == 0 ||
                !Number(fields[2], model == 1 ? 360 : 100, out _) ||
                !Number(fields[3], 100, out _) || !Number(fields[4], 100, out _)) return false;
            seen[index] = true;
        }
        return true;
    }
}
