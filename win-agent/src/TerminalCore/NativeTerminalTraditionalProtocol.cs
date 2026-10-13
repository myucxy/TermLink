using System;

namespace TermLink.TerminalCore
{
    public static class NativeTerminalTraditionalProtocol
    {
        /// <summary>Normalizes only a complete 8-bit response, never ordinary UTF-8 text.</summary>
        public static byte[] NormalizeC1Reply(ReadOnlySpan<byte> payload, bool allowMixedTerminators = false)
        {
            if (payload.Length < 2 || payload.Length > 64 * 1024) return null;
            if (allowMixedTerminators && payload.Length >= 3 && payload[0] == 0x1b &&
                (payload[1] == (byte)'P' || payload[1] == (byte)']') && payload[payload.Length - 1] == 0x9c)
            {
                var mixed = payload.ToArray(); mixed[0] = (byte)(mixed[1] + 0x40);
                mixed.AsSpan(2).CopyTo(mixed.AsSpan(1));
                return NormalizeC1Reply(mixed.AsSpan(0, mixed.Length - 1));
            }
            if (payload[0] != 0x90 && payload[0] != 0x9b && payload[0] != 0x9d && payload[0] != 0x9f) return null;
            var isString = payload[0] != 0x9b;
            var terminatorLength = isString ? 1 : 0;
            if (isString && payload[payload.Length - 1] != 0x9c) {
                if (!allowMixedTerminators || payload.Length < 3 || payload[payload.Length - 2] != 0x1b || payload[payload.Length - 1] != (byte)'\\') return null;
                terminatorLength = 2;
            }
            var body = payload.Slice(1, payload.Length - terminatorLength - 1);
            foreach (var value in body) if (value < 0x20 || value >= 0x7f) return null;
            var result = new byte[2 + body.Length + (isString ? 2 : 0)];
            result[0] = 0x1b; result[1] = (byte)(payload[0] - 0x40); body.CopyTo(result.AsSpan(2));
            if (isString) { result[result.Length - 2] = 0x1b; result[result.Length - 1] = (byte)'\\'; }
            return result;
        }
    }
}
