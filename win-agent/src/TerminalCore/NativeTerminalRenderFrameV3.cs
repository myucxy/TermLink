using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TermLink.TerminalCore
{
    /// <summary>
    /// A deterministic rejection of malformed or unsupported TermLink Render Frame V3 data.
    /// </summary>
    public sealed class NativeTerminalRenderFrameV3DecodeException : ArgumentException
    {
        internal NativeTerminalRenderFrameV3DecodeException(string message)
            : base("render frame v3: " + message)
        {
        }
    }

    public enum NativeTerminalRenderUnderlineStyleV3
    {
        None = 0,
        Single = 1,
        Double = 2,
        Curly = 3,
        Dotted = 4,
        Dashed = 5
    }

    public readonly struct NativeTerminalRenderRenditionV3
    {
        internal NativeTerminalRenderRenditionV3(
            NativeTerminalRenderColorV1 foreground,
            NativeTerminalRenderColorV1 background,
            NativeTerminalRenderColorV1 underlineColor,
            bool bold,
            bool blink,
            bool italic,
            bool inverse,
            bool conceal,
            bool strikethrough,
            bool faint,
            bool overline,
            NativeTerminalRenderUnderlineStyleV3 underlineStyle)
        {
            Foreground = foreground;
            Background = background;
            UnderlineColor = underlineColor;
            Bold = bold;
            Blink = blink;
            Italic = italic;
            Inverse = inverse;
            Conceal = conceal;
            Strikethrough = strikethrough;
            Faint = faint;
            Overline = overline;
            UnderlineStyle = underlineStyle;
        }

        public NativeTerminalRenderColorV1 Foreground { get; }
        public NativeTerminalRenderColorV1 Background { get; }
        public NativeTerminalRenderColorV1 UnderlineColor { get; }
        public bool Bold { get; }
        public bool Blink { get; }
        public bool Italic { get; }
        public bool Inverse { get; }
        public bool Underline => UnderlineStyle != NativeTerminalRenderUnderlineStyleV3.None;
        public bool Conceal { get; }
        public bool Strikethrough { get; }
        public bool Faint { get; }
        public bool Overline { get; }
        public NativeTerminalRenderUnderlineStyleV3 UnderlineStyle { get; }
    }

    /// <summary>
    /// Immutable Render Frame V3 presentation state. Wire-identical V1 DTOs are reused for
    /// graphemes, colors, palette, hyperlinks, cells, and rows. Renditions remain V3-native so
    /// every wire rendition ID retains its one-to-one meaning.
    /// </summary>
    public sealed class NativeTerminalRenderFrameV3
    {
        private readonly NativeTerminalRenderFrameV2 sharedPayload;

        internal NativeTerminalRenderFrameV3(
            NativeTerminalRenderFrameV2 sharedPayload,
            NativeTerminalRenderRenditionV3[] renditions)
        {
            this.sharedPayload = sharedPayload ??
                throw new ArgumentNullException(nameof(sharedPayload));
            Renditions = Array.AsReadOnly(
                renditions ?? throw new ArgumentNullException(nameof(renditions)));
        }

        public const int FormatVersion = 3;

        public ulong PresentationRevision => sharedPayload.PresentationRevision;
        public ulong NextSourceSequence => sharedPayload.NextSourceSequence;
        public int Columns => sharedPayload.Columns;
        public int Rows => sharedPayload.Rows;
        public NativeTerminalRenderScreenV1 ActiveScreen => sharedPayload.ActiveScreen;
        public bool ReverseVideo => sharedPayload.ReverseVideo;
        public NativeTerminalRenderCursorV2 Cursor => sharedPayload.Cursor;
        public IReadOnlyList<NativeTerminalRenderGraphemeV1> Graphemes =>
            sharedPayload.Graphemes;
        public IReadOnlyList<NativeTerminalRenderRenditionV3> Renditions { get; }
        public NativeTerminalRenderPaletteV1 Palette => sharedPayload.Palette;
        public IReadOnlyList<NativeTerminalRenderHyperlinkV1> Hyperlinks =>
            sharedPayload.Hyperlinks;
        public IReadOnlyList<NativeTerminalRenderRowV1> RenderRows => sharedPayload.RenderRows;
        public string Title => sharedPayload.Title;
    }

    /// <summary>
    /// Strict, allocation-bounded decoder for canonical TermLink Render Frame V3 data.
    /// </summary>
    public static class NativeTerminalRenderFrameV3Decoder
    {
        public const int MaximumFrameBytes = NativeTerminalRenderFrameV1Decoder.MaximumFrameBytes;

        public static NativeTerminalRenderFrameV3 Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Decode(bytes.AsSpan());
        }

        public static NativeTerminalRenderFrameV3 Decode(ReadOnlySpan<byte> bytes)
        {
            try
            {
                return new Decoder(bytes).Decode();
            }
            catch (NativeTerminalRenderFrameV3DecodeException)
            {
                throw;
            }
            catch (OverflowException)
            {
                throw Error("integer overflow");
            }
            catch (ArgumentOutOfRangeException)
            {
                throw Error("truncated data");
            }
        }

        private static NativeTerminalRenderFrameV3DecodeException Error(string message)
        {
            return new NativeTerminalRenderFrameV3DecodeException(message);
        }

        private ref struct Decoder
        {
            private const int HeaderSize = 96;
            private const int HeaderCrcEnd = 92;
            private const int DirectoryOffset = 96;
            private const int DirectoryEntrySize = 24;
            private const int SectionCount = 6;
            private const int RenditionDirectoryOffset =
                DirectoryOffset + DirectoryEntrySize;
            private const int RenditionRecordSize = 16;
            private const int MaximumCells = 262_144;
            private const ushort MajorVersion = 3;
            private const ushort MinorVersion = 0;
            private const ushort RenditionSectionVersion = 2;
            private const ushort SharedSectionVersion = 1;
            private const ushort CriticalSection = 1;
            private const ulong RequiredFeatures = 0x7f;
            private const ulong V2RequiredFeatures = 0x3f;
            private const uint RenditionBold = 1;
            private const uint RenditionBlink = 1 << 1;
            private const uint RenditionItalic = 1 << 2;
            private const uint RenditionInverse = 1 << 3;
            private const uint RenditionUnderlinePresent = 1 << 4;
            private const uint RenditionConceal = 1 << 5;
            private const uint RenditionStrikethrough = 1 << 6;
            private const uint RenditionFaint = 1 << 7;
            private const uint RenditionOverline = 1 << 8;
            private const int RenditionUnderlineStyleShift = 9;
            private const uint RenditionUnderlineStyleMask = 7 << RenditionUnderlineStyleShift;
            private const uint RenditionFlagsMask = 0x0fff;
            private const uint ColorTagMask = 0xff00_0000;
            private const uint ColorDefault = 0x0000_0000;
            private const uint ColorIndexed = 0x0100_0000;
            private const uint ColorRgb = 0x0200_0000;
            private const uint MaximumRgb = 0x00ff_ffff;
            private const uint Crc32CPolynomial = 0x82f6_3b78;

            private static readonly byte[] Magic =
            {
                (byte)'T', (byte)'L', (byte)'R', (byte)'F',
                (byte)'R', (byte)'M', (byte)'3', 0
            };

            private static readonly byte[] V2Magic =
            {
                (byte)'T', (byte)'L', (byte)'R', (byte)'F',
                (byte)'R', (byte)'M', (byte)'2', 0
            };

            private readonly ReadOnlySpan<byte> bytes;

            internal Decoder(ReadOnlySpan<byte> bytes)
            {
                this.bytes = bytes;
            }

            internal NativeTerminalRenderFrameV3 Decode()
            {
                ValidateHeader();
                var renditionSection = ValidateDirectoryEnvelope();
                var renditions = DecodeRenditions(renditionSection);
                var projection = CreateNonCollapsingV2Projection(renditionSection);

                NativeTerminalRenderFrameV2 sharedPayload;
                try
                {
                    sharedPayload = NativeTerminalRenderFrameV2Decoder.Decode(projection);
                }
                catch (NativeTerminalRenderFrameV2DecodeException exception)
                {
                    const string prefix = "render frame v2: ";
                    var detail = exception.Message.StartsWith(prefix, StringComparison.Ordinal)
                        ? exception.Message.Substring(prefix.Length)
                        : "shared payload is invalid";
                    throw Error(detail);
                }

                return new NativeTerminalRenderFrameV3(sharedPayload, renditions);
            }

            private void ValidateHeader()
            {
                if (bytes.Length < HeaderSize) throw Error("header is truncated");
                if (bytes.Length > MaximumFrameBytes) throw Error("frame exceeds 16 MiB");
                if (!bytes.Slice(0, Magic.Length).SequenceEqual(Magic))
                    throw Error("bad magic");
                ExpectUInt16(8, MajorVersion, "major version");
                ExpectUInt16(10, MinorVersion, "minor version");
                ExpectUInt16(12, HeaderSize, "header size");
                ExpectUInt16(14, DirectoryEntrySize, "directory entry size");
                ExpectUInt16(16, SectionCount, "section count");

                var shape = ReadByte(68, "cursor shape");
                if (shape > (byte)NativeTerminalRenderCursorShapeV2.Bar)
                    throw Error("cursor shape is invalid");
                var blinking = ReadByte(69, "cursor blinking flag");
                if (blinking > 1) throw Error("cursor blinking flag is invalid");
                ExpectUInt16(70, 0, "cursor reserved field");
                ExpectUInt64(72, RequiredFeatures, "required features");

                var payloadCrc = ReadUInt32(88, "payload CRC32C");
                var headerCrc = ReadUInt32(92, "header CRC32C");
                if (Crc32C(bytes.Slice(0, HeaderCrcEnd)) != headerCrc)
                    throw Error("header CRC32C mismatch");
                if (Crc32C(bytes.Slice(HeaderSize)) != payloadCrc)
                    throw Error("payload CRC32C mismatch");
            }

            private Section ValidateDirectoryEnvelope()
            {
                var directoryEnd = checked(DirectoryOffset + SectionCount * DirectoryEntrySize);
                EnsureRange(DirectoryOffset, SectionCount * DirectoryEntrySize, "directory");
                var expectedOffset = Align8((ulong)directoryEnd);
                Section rendition = default;
                for (var index = 0; index < SectionCount; index++)
                {
                    var entry = checked(DirectoryOffset + index * DirectoryEntrySize);
                    if (ReadUInt32(entry, "section tag") != (uint)(index + 1))
                        throw Error("section tags must be exactly 1..6");
                    ExpectUInt16(
                        entry + 4,
                        index == 1 ? RenditionSectionVersion : SharedSectionVersion,
                        "section version");
                    ExpectUInt16(entry + 6, CriticalSection, "section flags");
                    var offset = ReadUInt32(entry + 8, "section offset");
                    var length = ReadUInt32(entry + 12, "section length");
                    var count = ReadUInt32(entry + 16, "section logical count");
                    var recordSize = ReadUInt32(entry + 20, "section record size");
                    if (offset != expectedOffset)
                        throw Error("section " + (index + 1).ToString() +
                            " offset is not canonical");
                    var end = Add(offset, length, "section end");
                    var paddedEnd = Align8(end);
                    if (paddedEnd > (ulong)bytes.Length)
                        throw Error("section " + (index + 1).ToString() +
                            " is outside the frame");
                    RequireZeroRange(end, paddedEnd, "section padding");

                    if (index == 1)
                    {
                        if (recordSize != RenditionRecordSize)
                            throw Error("rendition record size must be 16");
                        if (count < 1 || count > MaximumCells)
                            throw Error("rendition count is out of range");
                        if (Multiply(count, RenditionRecordSize, "rendition section length") !=
                            length)
                        {
                            throw Error("rendition section length is invalid");
                        }
                        rendition = new Section(
                            checked((int)offset),
                            checked((int)length),
                            checked((int)count));
                    }
                    expectedOffset = paddedEnd;
                }

                if (expectedOffset != (ulong)bytes.Length)
                    throw Error("frame has trailing or missing bytes");
                return rendition;
            }

            private NativeTerminalRenderRenditionV3[] DecodeRenditions(Section section)
            {
                var result = new NativeTerminalRenderRenditionV3[section.Count];
                var previousOffset = -1;
                for (var index = 0; index < section.Count; index++)
                {
                    var offset = checked(section.Offset + index * RenditionRecordSize);
                    if (previousOffset >= 0 && CompareUnsigned(
                            bytes.Slice(previousOffset, RenditionRecordSize),
                            bytes.Slice(offset, RenditionRecordSize)) >= 0)
                    {
                        throw Error("rendition table is not strictly sorted");
                    }

                    var flags = ReadUInt32(offset, "rendition flags");
                    if ((flags & ~RenditionFlagsMask) != 0)
                        throw Error("unknown rendition flags");
                    var styleValue = (flags & RenditionUnderlineStyleMask) >>
                        RenditionUnderlineStyleShift;
                    if (styleValue > (uint)NativeTerminalRenderUnderlineStyleV3.Dashed)
                        throw Error("underline style is invalid");
                    var underlinePresent = (flags & RenditionUnderlinePresent) != 0;
                    if (underlinePresent != (styleValue != 0))
                        throw Error("underline-present flag and style disagree");

                    result[index] = new NativeTerminalRenderRenditionV3(
                        DecodeColor(ReadUInt32(offset + 4, "foreground color")),
                        DecodeColor(ReadUInt32(offset + 8, "background color")),
                        DecodeColor(ReadUInt32(offset + 12, "underline color")),
                        (flags & RenditionBold) != 0,
                        (flags & RenditionBlink) != 0,
                        (flags & RenditionItalic) != 0,
                        (flags & RenditionInverse) != 0,
                        (flags & RenditionConceal) != 0,
                        (flags & RenditionStrikethrough) != 0,
                        (flags & RenditionFaint) != 0,
                        (flags & RenditionOverline) != 0,
                        (NativeTerminalRenderUnderlineStyleV3)styleValue);
                    previousOffset = offset;
                }
                return result;
            }

            private byte[] CreateNonCollapsingV2Projection(Section section)
            {
                var projection = bytes.ToArray();
                V2Magic.CopyTo(projection, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(projection.AsSpan(8, 2), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    projection.AsSpan(RenditionDirectoryOffset + 4, 2),
                    SharedSectionVersion);
                BinaryPrimitives.WriteUInt64LittleEndian(
                    projection.AsSpan(72, 8),
                    V2RequiredFeatures);

                for (var index = 0; index < section.Count; index++)
                {
                    var record = projection.AsSpan(
                        checked(section.Offset + index * RenditionRecordSize),
                        RenditionRecordSize);
                    record.Clear();
                    // RGB payload bytes are arranged in big-endian index order so the V1
                    // bytewise rendition ordering remains strict without collapsing any ID.
                    record[4] = checked((byte)(index >> 16));
                    record[5] = checked((byte)(index >> 8));
                    record[6] = checked((byte)index);
                    record[7] = 2;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(
                    projection.AsSpan(88, 4),
                    Crc32C(projection.AsSpan(HeaderSize)));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    projection.AsSpan(92, 4),
                    Crc32C(projection.AsSpan(0, HeaderCrcEnd)));
                return projection;
            }

            private NativeTerminalRenderColorV1 DecodeColor(uint packed)
            {
                var tag = packed & ColorTagMask;
                var payload = packed & MaximumRgb;
                switch (tag)
                {
                    case ColorDefault:
                        if (payload != 0) throw Error("default color has a nonzero payload");
                        return new NativeTerminalRenderColorV1(
                            NativeTerminalRenderColorKindV1.Default,
                            0);
                    case ColorIndexed:
                        if (payload > byte.MaxValue)
                            throw Error("indexed color is out of range");
                        return new NativeTerminalRenderColorV1(
                            NativeTerminalRenderColorKindV1.Indexed,
                            (int)payload);
                    case ColorRgb:
                        return new NativeTerminalRenderColorV1(
                            NativeTerminalRenderColorKindV1.Rgb,
                            (int)payload);
                    default:
                        throw Error("color tag is invalid");
                }
            }

            private byte ReadByte(int offset, string field)
            {
                EnsureRange(offset, 1, field);
                return bytes[offset];
            }

            private ushort ReadUInt16(int offset, string field)
            {
                EnsureRange(offset, 2, field);
                return BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
            }

            private uint ReadUInt32(int offset, string field)
            {
                EnsureRange(offset, 4, field);
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
            }

            private ulong ReadUInt64(int offset, string field)
            {
                EnsureRange(offset, 8, field);
                return BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
            }

            private void ExpectUInt16(int offset, int expected, string field)
            {
                if (ReadUInt16(offset, field) != expected)
                    throw Error(field + " is unsupported");
            }

            private void ExpectUInt64(int offset, ulong expected, string field)
            {
                if (ReadUInt64(offset, field) != expected)
                    throw Error(field + " is invalid");
            }

            private void EnsureRange(int offset, int length, string field)
            {
                if (offset < 0 || length < 0 ||
                    (ulong)offset + (ulong)length > (ulong)bytes.Length)
                {
                    throw Error(field + " is truncated");
                }
            }

            private void RequireZeroRange(ulong start, ulong end, string field)
            {
                if (end < start || end > (ulong)bytes.Length)
                    throw Error(field + " is outside the frame");
                for (var index = checked((int)start); index < checked((int)end); index++)
                {
                    if (bytes[index] != 0) throw Error(field + " is nonzero");
                }
            }

            private static int CompareUnsigned(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
            {
                for (var index = 0; index < first.Length; index++)
                {
                    var difference = first[index] - second[index];
                    if (difference != 0) return difference;
                }
                return 0;
            }

            private static ulong Add(ulong left, ulong right, string field)
            {
                try
                {
                    return checked(left + right);
                }
                catch (OverflowException)
                {
                    throw Error(field + " overflows");
                }
            }

            private static ulong Multiply(ulong left, ulong right, string field)
            {
                try
                {
                    return checked(left * right);
                }
                catch (OverflowException)
                {
                    throw Error(field + " overflows");
                }
            }

            private static ulong Align8(ulong value)
            {
                return Add(value, 7, "alignment") & ~7UL;
            }

            private static uint Crc32C(ReadOnlySpan<byte> data)
            {
                var crc = uint.MaxValue;
                for (var index = 0; index < data.Length; index++)
                {
                    crc ^= data[index];
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var mask = unchecked(0U - (crc & 1U));
                        crc = (crc >> 1) ^ (Crc32CPolynomial & mask);
                    }
                }
                return ~crc;
            }

            private readonly struct Section
            {
                internal Section(int offset, int length, int count)
                {
                    Offset = offset;
                    Length = length;
                    Count = count;
                }

                internal int Offset { get; }
                internal int Length { get; }
                internal int Count { get; }
            }
        }
    }
}
