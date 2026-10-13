using System;
using System.Buffers.Binary;

namespace TermLink.TerminalCore;

/// <summary>Selected host's actual palette. Immutable, bounded journal payload.</summary>
public sealed class NativeTerminalHostThemeV1
{
    public const int EncodedBytes = 1044;
    private readonly byte[] bytes;
    private NativeTerminalHostThemeV1(byte[] bytes) { this.bytes = bytes; }
    public byte[] Encode() => (byte[])bytes.Clone();
    public static NativeTerminalHostThemeV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1 ||
            (bytes[4] != 1 && bytes[4] != 2) || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0)
            throw new ArgumentException("Invalid terminal host theme header.");
        for (var offset = 11; offset < EncodedBytes; offset += 4)
            if (bytes[offset] != 0) throw new ArgumentException("Invalid terminal host theme color.");
        return new NativeTerminalHostThemeV1(bytes.ToArray());
    }

    public static NativeTerminalHostThemeV1 Create(bool dark, ReadOnlySpan<uint> rgb)
    {
        if (rgb.Length != 259) throw new ArgumentException("Host theme requires 259 RGB colors.");
        var payload = new byte[EncodedBytes];
        payload[0] = 1; payload[4] = dark ? (byte)1 : (byte)2;
        for (var index = 0; index < rgb.Length; index++)
        {
            if (rgb[index] > 0xffffff) throw new ArgumentException("Host theme color exceeds RGB.");
            var offset = 8 + index * 4;
            payload[offset] = (byte)(rgb[index] >> 16);
            payload[offset + 1] = (byte)(rgb[index] >> 8);
            payload[offset + 2] = (byte)rgb[index];
        }
        return Decode(payload);
    }
}
