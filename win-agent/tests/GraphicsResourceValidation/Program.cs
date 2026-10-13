using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;
using TermLink.TerminalCore;

namespace TermLink.GraphicsResource.Validation;

internal static class Program
{
    private static int assertions;

    private static void Main()
    {
        Require(Crc("123456789"u8) == 0xe3069283, "CRC32C standard check value");
        ValidResourcesAreDetached();
        InspectRejectsChangedInput();
        HeaderAndTruncationFailures();
        ResourceFailures();
        BudgetBoundaries();
        LateErrorsDoNotAllocatePixels();
        Console.WriteLine("PASS TLGRS1 graphics resources: " + assertions + " assertions");
        V5Tests.Run();
    }

    private static void ValidResourcesAreDetached()
    {
        var empty = NativeTerminalGraphicsResourcesDecoder.Decode(Encode(1));
        Require(empty.NextId == 1 && empty.PixelBytes == 0 && empty.Entries.Count == 0, "empty resources");
        Require(!empty.TryGet(1, out _), "empty lookup");
        Require(NativeTerminalGraphicsResourcesDecoder.Decode(Encode(ulong.MaxValue)).NextId == ulong.MaxValue,
            "exhausted next ID is preserved");

        var bytes = Encode(ulong.MaxValue,
            new Entry(7, 1, 1, 1, new byte[] { 250, 128, 64, 1 }),
            new Entry(ulong.MaxValue - 1, 3, 1, 2, new byte[] { 0, 127, 255 }));
        var directory = NativeTerminalGraphicsResourcesDecoder.Inspect(bytes);
        Require(directory.NextId == ulong.MaxValue && directory.PixelBytes == 7 && directory.Entries.Length == 2, "directory totals");
        Require(directory.TryGet(ulong.MaxValue - 1, out var mask) && mask.Width == 3 && mask.Height == 1 &&
            mask.Format == NativeTerminalGraphicsImageFormat.Alpha8 && mask.PixelBytes == 3, "unsigned ID and A8 metadata");
        Require(!directory.TryGet(0, out _) && !directory.TryGet(8, out _) && !directory.TryGet(ulong.MaxValue, out _), "directory missing IDs");
        var resources = directory.Materialize();
        Array.Clear(bytes);
        Require(resources.TryGet(7, out var rgba) && rgba.Width == 1 && rgba.Height == 1 &&
            rgba.Format == NativeTerminalGraphicsImageFormat.Rgba8888 && rgba.Pixels.SequenceEqual(new byte[] { 250, 128, 64, 1 }),
            "RGBA is detached and stays unpremultiplied");
        Require(resources.TryGet(ulong.MaxValue - 1, out var alpha) && alpha.Pixels.SequenceEqual(new byte[] { 0, 127, 255 }),
            "A8 is detached");
        Require(!resources.TryGet(0, out _) && !resources.TryGet(8, out _) && !resources.TryGet(ulong.MaxValue, out _), "resource missing IDs");
        var list = (IList<NativeTerminalGraphicsResource>)resources.Entries;
        try { list[0] = alpha; throw new Exception("entries were mutable"); }
        catch (NotSupportedException) { assertions++; }

        var checkVector = Encode(2, new Entry(1, 9, 1, 2, "123456789"u8.ToArray()));
        Require(BinaryPrimitives.ReadUInt32LittleEndian(checkVector.AsSpan(72)) == 0xe3069283, "known payload CRC in fixture");
        Require(NativeTerminalGraphicsResourcesDecoder.Decode(checkVector).Entries[0].Pixels.SequenceEqual("123456789"u8),
            "standard CRC payload accepted");
    }

    private static void InspectRejectsChangedInput()
    {
        var bytes = Sample();
        var directory = NativeTerminalGraphicsResourcesDecoder.Inspect(bytes);
        bytes[80] ^= 1;
        RejectMaterialize(directory, "unsealed pixel change");
        Write32(bytes, 72, Crc(bytes.AsSpan(80, 3)));
        Seal(bytes);
        RejectMaterialize(directory, "resealed pixel change");

        bytes = Sample();
        directory = NativeTerminalGraphicsResourcesDecoder.Inspect(bytes);
        Write32(bytes, 56, 1);
        Write32(bytes, 60, 3);
        Seal(bytes);
        RejectMaterialize(directory, "resealed metadata change");

        bytes = Sample();
        directory = NativeTerminalGraphicsResourcesDecoder.Inspect(bytes);
        Write64(bytes, 16, 3);
        Seal(bytes);
        RejectMaterialize(directory, "resealed next ID change");
        RejectMaterialize(default, "uninitialized directory");
    }

    private static void HeaderAndTruncationFailures()
    {
        try { NativeTerminalGraphicsResourcesDecoder.Decode((byte[])null); throw new Exception("null accepted"); }
        catch (ArgumentNullException) { assertions++; }
        var valid = Sample();
        for (var size = 0; size < valid.Length; size++)
        {
            var truncated = valid.AsSpan(0, size).ToArray();
            if (size >= 48) { Write32(truncated, 12, (uint)size); Seal(truncated); }
            Reject(truncated, "truncation at " + size);
        }
        foreach (var offset in new[] { 0, 6, 7, 8, 10, 28, 31, 44, 47 })
            MutateReject(valid, bytes => bytes[offset] ^= 1, "header byte " + offset);
        MutateReject(valid, bytes => Write32(bytes, 12, (uint)bytes.Length + 1), "total length");
        MutateReject(valid, bytes => Write64(bytes, 16, 0), "zero next ID");
        MutateReject(valid, bytes => Write32(bytes, 24, 257), "resource count limit");
        MutateReject(valid, bytes => Write32(bytes, 24, uint.MaxValue), "resource count overflow");
        MutateReject(valid, bytes => Write32(bytes, 24, 0), "unclaimed entries");
        MutateReject(valid, bytes => Write32(bytes, 24, 2), "missing resource");
        MutateReject(valid, bytes => Write64(bytes, 32, 4), "declared pixel mismatch");
        MutateReject(valid, bytes => Write64(bytes, 32, (ulong)NativeTerminalGraphicsResourcesDecoder.MaximumSessionImageBytes + 1), "declared pixel budget");
        MutateReject(valid, bytes => Write64(bytes, 32, ulong.MaxValue), "declared pixel overflow");
        var corrupt = (byte[])valid.Clone();
        corrupt[40] ^= 1;
        Reject(corrupt, "frame CRC");
        var trailing = new byte[valid.Length + 4];
        valid.CopyTo(trailing, 0);
        Write32(trailing, 12, (uint)trailing.Length);
        Seal(trailing);
        Reject(trailing, "trailing zero bytes");
        Reject(new byte[NativeTerminalGraphicsResourcesDecoder.MaximumFrameBytes + 1], "frame budget");
    }

    private static void ResourceFailures()
    {
        var valid = Sample();
        foreach (var id in new[] { 0UL, 2UL, ulong.MaxValue })
            MutateReject(valid, bytes => Write64(bytes, 48, id), "invalid resource ID");
        foreach (var format in new byte[] { 0, 3, 255 })
            MutateReject(valid, bytes => bytes[64] = format, "unknown format");
        foreach (var offset in new[] { 65, 66, 67, 76, 77, 78, 79, 83 })
            MutateReject(valid, bytes => bytes[offset] = 1, "resource reserved/padding byte " + offset);
        foreach (var dimension in new[] { 0U, 4097U, uint.MaxValue })
        {
            MutateReject(valid, bytes => Write32(bytes, 56, dimension), "invalid width");
            MutateReject(valid, bytes => Write32(bytes, 60, dimension), "invalid height");
        }
        foreach (var length in new[] { 0U, 2U, 4U, uint.MaxValue })
            MutateReject(valid, bytes => Write32(bytes, 68, length), "invalid pixel length");
        MutateReject(valid, bytes => bytes[80] ^= 1, "payload CRC independently checked");
        MutateReject(valid, bytes => bytes[72] ^= 1, "payload CRC field");
        MutateReject(valid, bytes => { Write32(bytes, 56, 4096); Write32(bytes, 60, 4096); bytes[64] = 1; Write32(bytes, 68, 64U * 1024 * 1024); }, "single image budget");
        var two = Encode(4, new Entry(1, 1, 1, 2, new byte[] { 1 }), new Entry(2, 1, 1, 2, new byte[] { 2 }));
        MutateReject(two, bytes => Write64(bytes, 84, 1), "duplicate IDs");
        MutateReject(two, bytes => { Write64(bytes, 48, 2); Write64(bytes, 84, 1); }, "descending IDs");
    }

    private static void BudgetBoundaries()
    {
        var many = new Entry[NativeTerminalGraphicsResourcesDecoder.MaximumResources];
        for (var index = 0; index < many.Length; index++) many[index] = new Entry((ulong)index + 1, 1, 1, 2, new byte[] { (byte)index });
        var resources = NativeTerminalGraphicsResourcesDecoder.Decode(Encode(257, many));
        Require(resources.Entries.Count == 256 && resources.PixelBytes == 256 && resources.Entries[255].Pixels[0] == 255,
            "256 resources accepted");

        var large = new byte[NativeTerminalGraphicsResourcesDecoder.MaximumImageBytes];
        large[0] = 17;
        large[^1] = 231;
        var exact = Encode(3, new Entry(1, 2048, 2048, 1, large), new Entry(2, 4096, 4096, 2, large));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var directory = NativeTerminalGraphicsResourcesDecoder.Inspect(exact);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated < 65536, "inspection copied pixels: " + allocated);
        Require(directory.PixelBytes == NativeTerminalGraphicsResourcesDecoder.MaximumSessionImageBytes &&
            directory.Entries[0].PixelBytes == NativeTerminalGraphicsResourcesDecoder.MaximumImageBytes && directory.Entries[1].Width == 4096,
            "exact image/session/dimension boundaries accepted");
        resources = directory.Materialize();
        Require(resources.Entries[0].Pixels[0] == 17 && resources.Entries[1].Pixels[^1] == 231, "boundary pixels materialized");

        var over = Encode(4, new Entry(1, 2048, 2048, 1, large), new Entry(2, 4096, 4096, 2, large), new Entry(3, 1, 1, 2, new byte[] { 1 }));
        Write64(over, 32, NativeTerminalGraphicsResourcesDecoder.MaximumSessionImageBytes);
        Seal(over);
        Reject(over, "actual session pixels exceed budget despite forged declared total");
    }

    private static void LateErrorsDoNotAllocatePixels()
    {
        var large = new byte[NativeTerminalGraphicsResourcesDecoder.MaximumImageBytes];
        var bytes = Encode(3, new Entry(1, 2048, 2048, 1, large), new Entry(2, 1, 1, 2, new byte[] { 42 }));
        var second = 48 + 32 + large.Length;
        Write32(bytes, second + 8, 0);
        Seal(bytes);
        RejectBeforePixelAllocation(bytes, "late invalid dimensions");
        Write32(bytes, second + 8, 1);
        bytes[second + 24] ^= 1;
        Seal(bytes);
        RejectBeforePixelAllocation(bytes, "late payload CRC");
        bytes[second + 24] ^= 1;
        bytes[^1] = 1;
        Seal(bytes);
        RejectBeforePixelAllocation(bytes, "late padding");
        bytes[^1] = 0;
        Write64(bytes, 32, (ulong)large.Length);
        Seal(bytes);
        RejectBeforePixelAllocation(bytes, "late declared total mismatch");
    }

    private static void RejectBeforePixelAllocation(byte[] bytes, string name)
    {
        Reject(bytes, name + " warmup");
        var before = GC.GetAllocatedBytesForCurrentThread();
        Reject(bytes, name);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated < 65536, name + " allocated pixels before rejecting: " + allocated);
    }

    private static byte[] Sample() => Encode(2, new Entry(1, 3, 1, 2, new byte[] { 5, 127, 255 }));

    private static void MutateReject(byte[] source, Action<byte[]> change, string name)
    {
        var bytes = (byte[])source.Clone();
        change(bytes);
        Seal(bytes);
        Reject(bytes, name);
    }

    private static void Reject(byte[] bytes, string name)
    {
        try { NativeTerminalGraphicsResourcesDecoder.Decode(bytes); }
        catch (NativeTerminalGraphicsResourcesDecodeException) { assertions++; return; }
        throw new Exception("accepted malformed TLGRS1: " + name);
    }

    private static void RejectMaterialize(NativeTerminalGraphicsResourceFrame frame, string name)
    {
        try { frame.Materialize(); }
        catch (NativeTerminalGraphicsResourcesDecodeException) { assertions++; return; }
        throw new Exception("materialized changed TLGRS1: " + name);
    }

    internal static byte[] Encode(ulong nextId, params Entry[] entries)
    {
        var length = 48;
        ulong pixelBytes = 0;
        foreach (var entry in entries) { length += 32 + ((entry.Pixels.Length + 3) & ~3); pixelBytes += (ulong)entry.Pixels.Length; }
        var bytes = new byte[length];
        "TLGRS1\0\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 48);
        Write32(bytes, 12, (uint)length);
        Write64(bytes, 16, nextId);
        Write32(bytes, 24, (uint)entries.Length);
        Write64(bytes, 32, pixelBytes);
        var offset = 48;
        foreach (var entry in entries)
        {
            Write64(bytes, offset, entry.Id);
            Write32(bytes, offset + 8, entry.Width);
            Write32(bytes, offset + 12, entry.Height);
            bytes[offset + 16] = entry.Format;
            Write32(bytes, offset + 20, (uint)entry.Pixels.Length);
            Write32(bytes, offset + 24, Crc(entry.Pixels));
            entry.Pixels.CopyTo(bytes, offset + 32);
            offset += 32 + ((entry.Pixels.Length + 3) & ~3);
        }
        Seal(bytes);
        return bytes;
    }

    private static void Seal(byte[] bytes) => Write32(bytes, 40, Crc(bytes.AsSpan(0, 40), bytes.AsSpan(44)));
    internal static void Write32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    internal static void Write64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }

    // Hardware CRC in these fixtures is independent of the decoder's table implementation.
    internal static uint Crc(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
    {
        var crc = uint.MaxValue;
        foreach (var value in first) crc = CrcByte(crc, value);
        foreach (var value in second) crc = CrcByte(crc, value);
        return ~crc;
    }

    private static uint CrcByte(uint crc, byte value)
    {
        if (Sse42.IsSupported) return Sse42.Crc32(crc, value);
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82f63b78U);
        return crc;
    }

    internal sealed record Entry(ulong Id, uint Width, uint Height, byte Format, byte[] Pixels);
}
