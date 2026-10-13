using System;
using System.Buffers.Binary;

namespace TermLink.TerminalCore;

/// <summary>Ordered measurements from the selected display; zero pairs mean unavailable.</summary>
public readonly record struct NativeTerminalHostGeometryV1(
    uint CellWidth, uint CellHeight, uint DisplayWidth, uint DisplayHeight,
    uint WindowWidth, uint WindowHeight)
{
    public const int EncodedBytes = 32;

    public byte[] Encode()
    {
        ValidatePair(CellWidth, CellHeight, 1024);
        ValidatePair(DisplayWidth, DisplayHeight, 32768);
        ValidatePair(WindowWidth, WindowHeight, 32768);
        var bytes = new byte[EncodedBytes];
        var words = new uint[] { 1, CellWidth, CellHeight, DisplayWidth, DisplayHeight, WindowWidth, WindowHeight, 0 };
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), words[index]);
        return bytes;
    }

    public static NativeTerminalHostGeometryV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28)) != 0)
            throw new ArgumentException("Invalid terminal host geometry header.");
        var value = new NativeTerminalHostGeometryV1(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(20)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24)));
        ValidatePair(value.CellWidth, value.CellHeight, 1024);
        ValidatePair(value.DisplayWidth, value.DisplayHeight, 32768);
        ValidatePair(value.WindowWidth, value.WindowHeight, 32768);
        return value;
    }

    private static void ValidatePair(uint width, uint height, uint maximum)
    {
        if ((width == 0) != (height == 0) || width > maximum || height > maximum)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid terminal host geometry dimension pair.");
    }
}
