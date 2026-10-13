using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TermLink.TerminalCore
{
    public enum NativeTerminalGraphicsImageFormat : byte { Rgba8888 = 1, Alpha8 = 2 }

    public sealed class NativeTerminalGraphicsResourcesDecodeException : ArgumentException
    {
        internal NativeTerminalGraphicsResourcesDecodeException(string detail)
            : base("graphics resources: " + detail) { }
    }

    public sealed class NativeTerminalGraphicsResource
    {
        private readonly byte[] pixels;

        internal NativeTerminalGraphicsResource(NativeTerminalGraphicsResourceMetadata metadata, byte[] pixels)
        {
            Id = metadata.Id;
            Width = metadata.Width;
            Height = metadata.Height;
            Format = metadata.Format;
            this.pixels = pixels;
        }

        public ulong Id { get; }
        public uint Width { get; }
        public uint Height { get; }
        public NativeTerminalGraphicsImageFormat Format { get; }
        public ReadOnlySpan<byte> Pixels => pixels;

        public bool HasSameContent(NativeTerminalGraphicsResource other) =>
            ReferenceEquals(this, other) || other != null && Id == other.Id && Format == other.Format &&
            Width == other.Width && Height == other.Height && Pixels.SequenceEqual(other.Pixels);
    }

    public sealed class NativeTerminalGraphicsResources
    {
        internal NativeTerminalGraphicsResources(ulong nextId, int pixelBytes, NativeTerminalGraphicsResource[] entries)
        {
            NextId = nextId;
            PixelBytes = pixelBytes;
            Entries = Array.AsReadOnly(entries);
        }

        public ulong NextId { get; }
        public int PixelBytes { get; }
        public IReadOnlyList<NativeTerminalGraphicsResource> Entries { get; }

        public bool TryGet(ulong id, out NativeTerminalGraphicsResource resource)
        {
            var low = 0;
            var high = Entries.Count - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = Entries[middle];
                if (candidate.Id == id) { resource = candidate; return true; }
                if (candidate.Id < id) low = middle + 1;
                else high = middle - 1;
            }
            resource = null;
            return false;
        }
    }

    public readonly struct NativeTerminalGraphicsResourceMetadata
    {
        internal NativeTerminalGraphicsResourceMetadata(ulong id, uint width, uint height,
            NativeTerminalGraphicsImageFormat format, int pixelOffset, int pixelBytes, uint pixelChecksum)
        {
            Id = id;
            Width = width;
            Height = height;
            Format = format;
            PixelOffset = pixelOffset;
            PixelBytes = pixelBytes;
            PixelChecksum = pixelChecksum;
        }

        public ulong Id { get; }
        public uint Width { get; }
        public uint Height { get; }
        public NativeTerminalGraphicsImageFormat Format { get; }
        public int PixelBytes { get; }
        internal int PixelOffset { get; }
        internal uint PixelChecksum { get; }

        internal bool Matches(NativeTerminalGraphicsResourceMetadata other) =>
            Id == other.Id && Width == other.Width && Height == other.Height && Format == other.Format &&
            PixelBytes == other.PixelBytes && PixelOffset == other.PixelOffset && PixelChecksum == other.PixelChecksum;
    }

    /// <summary>A validated directory borrowing its input. Do not mutate that input concurrently with inspection or materialization.</summary>
    public readonly ref struct NativeTerminalGraphicsResourceFrame
    {
        private readonly ReadOnlySpan<byte> bytes;
        private readonly NativeTerminalGraphicsResourceMetadata[] entries;
        private readonly uint checksum;

        internal NativeTerminalGraphicsResourceFrame(ReadOnlySpan<byte> bytes, ulong nextId, int pixelBytes,
            NativeTerminalGraphicsResourceMetadata[] entries, uint checksum)
        {
            this.bytes = bytes;
            this.entries = entries;
            this.checksum = checksum;
            NextId = nextId;
            PixelBytes = pixelBytes;
        }

        public ulong NextId { get; }
        public int PixelBytes { get; }
        public ReadOnlySpan<NativeTerminalGraphicsResourceMetadata> Entries => entries;

        public bool TryGet(ulong id, out NativeTerminalGraphicsResourceMetadata metadata)
        {
            var low = 0;
            var high = Entries.Length - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var candidate = Entries[middle];
                if (candidate.Id == id) { metadata = candidate; return true; }
                if (candidate.Id < id) low = middle + 1;
                else high = middle - 1;
            }
            metadata = default;
            return false;
        }

        public NativeTerminalGraphicsResources Materialize()
        {
            // The enclosing V5 decoder can validate every reference before any pixel copy.
            var current = NativeTerminalGraphicsResourcesDecoder.Inspect(bytes);
            if (checksum != current.checksum || NextId != current.NextId || PixelBytes != current.PixelBytes ||
                Entries.Length != current.Entries.Length)
                throw new NativeTerminalGraphicsResourcesDecodeException("input changed after inspection");
            for (var index = 0; index < Entries.Length; index++)
                if (!Entries[index].Matches(current.Entries[index]))
                    throw new NativeTerminalGraphicsResourcesDecodeException("directory changed after inspection");

            var resources = new NativeTerminalGraphicsResource[Entries.Length];
            for (var index = 0; index < resources.Length; index++)
            {
                var entry = Entries[index];
                resources[index] = new NativeTerminalGraphicsResource(entry,
                    bytes.Slice(entry.PixelOffset, entry.PixelBytes).ToArray());
            }
            return new NativeTerminalGraphicsResources(NextId, PixelBytes, resources);
        }
    }

    public static class NativeTerminalGraphicsResourcesDecoder
    {
        public const int MaximumImageDimension = 4096;
        public const int MaximumImageBytes = 16 * 1024 * 1024;
        public const int MaximumSessionImageBytes = 32 * 1024 * 1024;
        public const int MaximumResources = 256;
        public const int MaximumFrameBytes = 48 + MaximumSessionImageBytes + MaximumResources * 35;
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static NativeTerminalGraphicsResources Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Decode(bytes.AsSpan());
        }

        public static NativeTerminalGraphicsResources Decode(ReadOnlySpan<byte> bytes) => Inspect(bytes).Materialize();

        public static NativeTerminalGraphicsResourceFrame Inspect(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 48 || bytes.Length > MaximumFrameBytes ||
                !bytes.Slice(0, 12).SequenceEqual("TLGRS1\0\0\x01\0\x30\0"u8))
                throw Error("invalid header or frame size");

            var nextId = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(16));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(24));
            var declaredBytes = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32));
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(40));
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12)) != bytes.Length || nextId == 0 ||
                count > MaximumResources || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(28)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(44)) != 0 ||
                declaredBytes > MaximumSessionImageBytes || checksum != Crc32C(bytes.Slice(0, 40), bytes.Slice(44)))
                throw Error("invalid header fields or CRC32C");

            var directory = new NativeTerminalGraphicsResourceMetadata[(int)count];
            var offset = 48;
            ulong lastId = 0;
            var total = 0;
            for (var index = 0; index < directory.Length; index++)
            {
                if (bytes.Length - offset < 32) throw Error("truncated resource header");
                var header = bytes.Slice(offset, 32);
                var id = BinaryPrimitives.ReadUInt64LittleEndian(header);
                var width = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8));
                var height = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12));
                var format = (NativeTerminalGraphicsImageFormat)header[16];
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20));
                var pixelChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24));
                if (id <= lastId || id >= nextId || header[17] != 0 || header[18] != 0 || header[19] != 0 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(28)) != 0 ||
                    (format != NativeTerminalGraphicsImageFormat.Rgba8888 && format != NativeTerminalGraphicsImageFormat.Alpha8) ||
                    width == 0 || width > MaximumImageDimension || height == 0 || height > MaximumImageDimension)
                    throw Error("invalid resource metadata");

                var expectedLength = checked((ulong)width * height * (format == NativeTerminalGraphicsImageFormat.Rgba8888 ? 4UL : 1UL));
                if (expectedLength > MaximumImageBytes || expectedLength != length)
                    throw Error("invalid pixel length or image budget");
                var start = checked(offset + 32);
                var end = checked(start + (int)length);
                var padded = checked(end + 3) & ~3;
                if (padded > bytes.Length) throw Error("truncated pixels or padding");
                for (var padding = end; padding < padded; padding++)
                    if (bytes[padding] != 0) throw Error("nonzero pixel padding");
                if (pixelChecksum != Crc32C(bytes.Slice(start, (int)length), ReadOnlySpan<byte>.Empty))
                    throw Error("pixel CRC32C mismatch");
                total = checked(total + (int)length);
                if (total > MaximumSessionImageBytes) throw Error("session pixel budget exceeded");
                directory[index] = new NativeTerminalGraphicsResourceMetadata(id, width, height, format, start, (int)length, pixelChecksum);
                lastId = id;
                offset = padded;
            }
            if (offset != bytes.Length || (ulong)total != declaredBytes)
                throw Error("trailing data or pixel total mismatch");
            return new NativeTerminalGraphicsResourceFrame(bytes, nextId, total, directory, checksum);
        }

        private static NativeTerminalGraphicsResourcesDecodeException Error(string detail) => new NativeTerminalGraphicsResourcesDecodeException(detail);

        private static uint Crc32C(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = uint.MaxValue;
            foreach (var value in first) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
            foreach (var value in second) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
            return ~crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var crc = index;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82f63b78U);
                table[index] = crc;
            }
            return table;
        }
    }
}
