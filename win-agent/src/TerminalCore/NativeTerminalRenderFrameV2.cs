using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace TermLink.TerminalCore
{
    /// <summary>
    /// A deterministic rejection of malformed or unsupported TermLink Render Frame V2 data.
    /// </summary>
    public sealed class NativeTerminalRenderFrameV2DecodeException : ArgumentException
    {
        internal NativeTerminalRenderFrameV2DecodeException(string message)
            : base("render frame v2: " + message)
        {
        }
    }

    public enum NativeTerminalRenderCursorShapeV2
    {
        Block = 0,
        Underline = 1,
        Bar = 2
    }

    public readonly struct NativeTerminalRenderCursorV2
    {
        internal NativeTerminalRenderCursorV2(
            NativeTerminalRenderCursorV1 position,
            NativeTerminalRenderCursorShapeV2 shape,
            bool blinking)
        {
            Row = position.Row;
            Column = position.Column;
            Visible = position.Visible;
            WrapPending = position.WrapPending;
            Shape = shape;
            Blinking = blinking;
        }

        public int Row { get; }
        public int Column { get; }
        public bool Visible { get; }
        public bool WrapPending { get; }
        public NativeTerminalRenderCursorShapeV2 Shape { get; }
        public bool Blinking { get; }
    }

    /// <summary>
    /// Immutable Render Frame V2 presentation state. V2 deliberately reuses the immutable
    /// V1 DTOs for its six wire-identical payload sections and adds cursor presentation state.
    /// </summary>
    public sealed class NativeTerminalRenderFrameV2
    {
        internal NativeTerminalRenderFrameV2(
            NativeTerminalRenderFrameV1 sharedPayload,
            NativeTerminalRenderCursorShapeV2 cursorShape,
            bool cursorBlinking)
        {
            SharedPayload = sharedPayload ?? throw new ArgumentNullException(nameof(sharedPayload));
            Cursor = new NativeTerminalRenderCursorV2(
                sharedPayload.Cursor,
                cursorShape,
                cursorBlinking);
        }

        public const int FormatVersion = 2;

        /// <summary>
        /// The decoded, immutable state shared with Render Frame V1. Its cursor contains the
        /// common position/visibility fields; use <see cref="Cursor"/> for the V2 cursor state.
        /// </summary>
        public NativeTerminalRenderFrameV1 SharedPayload { get; }

        public ulong PresentationRevision => SharedPayload.PresentationRevision;
        public ulong NextSourceSequence => SharedPayload.NextSourceSequence;
        public int Columns => SharedPayload.Columns;
        public int Rows => SharedPayload.Rows;
        public NativeTerminalRenderScreenV1 ActiveScreen => SharedPayload.ActiveScreen;
        public bool ReverseVideo => SharedPayload.ReverseVideo;
        public NativeTerminalRenderCursorV2 Cursor { get; }
        public IReadOnlyList<NativeTerminalRenderGraphemeV1> Graphemes => SharedPayload.Graphemes;
        public IReadOnlyList<NativeTerminalRenderRenditionV1> Renditions => SharedPayload.Renditions;
        public NativeTerminalRenderPaletteV1 Palette => SharedPayload.Palette;
        public IReadOnlyList<NativeTerminalRenderHyperlinkV1> Hyperlinks => SharedPayload.Hyperlinks;
        public IReadOnlyList<NativeTerminalRenderRowV1> RenderRows => SharedPayload.RenderRows;
        public string Title => SharedPayload.Title;
    }

    /// <summary>
    /// Strict, allocation-bounded decoder for canonical TermLink Render Frame V2 data.
    /// </summary>
    public static class NativeTerminalRenderFrameV2Decoder
    {
        public const int MaximumFrameBytes = NativeTerminalRenderFrameV1Decoder.MaximumFrameBytes;

        private const int HeaderSize = 96;
        private const int HeaderCrcEnd = 92;
        private const ushort MajorVersion = 2;
        private const ushort MinorVersion = 0;
        private const ushort CanonicalHeaderSize = 96;
        private const ushort CanonicalDirectoryEntrySize = 24;
        private const ushort CanonicalSectionCount = 6;
        private const ulong RequiredFeatures = 0x3f;
        private const ulong V1RequiredFeatures = 0x1f;
        private const uint Crc32CPolynomial = 0x82f6_3b78;

        private static readonly byte[] Magic =
        {
            (byte)'T', (byte)'L', (byte)'R', (byte)'F',
            (byte)'R', (byte)'M', (byte)'2', 0
        };

        private static readonly byte[] V1Magic =
        {
            (byte)'T', (byte)'L', (byte)'R', (byte)'F',
            (byte)'R', (byte)'M', (byte)'1', 0
        };

        public static NativeTerminalRenderFrameV2 Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Decode(bytes.AsSpan());
        }

        public static NativeTerminalRenderFrameV2 Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < HeaderSize) throw Error("header is truncated");
            if (bytes.Length > MaximumFrameBytes) throw Error("frame exceeds 16 MiB");
            if (!bytes.Slice(0, Magic.Length).SequenceEqual(Magic)) throw Error("bad magic");

            ExpectUInt16(bytes, 8, MajorVersion, "major version");
            ExpectUInt16(bytes, 10, MinorVersion, "minor version");
            ExpectUInt16(bytes, 12, CanonicalHeaderSize, "header size");
            ExpectUInt16(bytes, 14, CanonicalDirectoryEntrySize, "directory entry size");
            ExpectUInt16(bytes, 16, CanonicalSectionCount, "section count");

            var shapeValue = bytes[68];
            if (shapeValue > (byte)NativeTerminalRenderCursorShapeV2.Bar)
                throw Error("cursor shape is invalid");
            var blinkingValue = bytes[69];
            if (blinkingValue > 1) throw Error("cursor blinking flag is invalid");
            ExpectUInt16(bytes, 70, 0, "cursor reserved field");
            ExpectUInt64(bytes, 72, RequiredFeatures, "required features");

            var payloadCrc = ReadUInt32(bytes, 88);
            var headerCrc = ReadUInt32(bytes, 92);
            if (Crc32C(bytes.Slice(0, HeaderCrcEnd)) != headerCrc)
                throw Error("header CRC32C mismatch");
            if (Crc32C(bytes.Slice(HeaderSize)) != payloadCrc)
                throw Error("payload CRC32C mismatch");

            var v1Projection = bytes.ToArray();
            V1Magic.CopyTo(v1Projection, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(v1Projection.AsSpan(8, 2), 1);
            v1Projection.AsSpan(68, 4).Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(
                v1Projection.AsSpan(72, 8),
                V1RequiredFeatures);
            BinaryPrimitives.WriteUInt32LittleEndian(
                v1Projection.AsSpan(92, 4),
                Crc32C(v1Projection.AsSpan(0, HeaderCrcEnd)));

            NativeTerminalRenderFrameV1 sharedPayload;
            try
            {
                sharedPayload = NativeTerminalRenderFrameV1Decoder.Decode(v1Projection);
            }
            catch (NativeTerminalRenderFrameV1DecodeException exception)
            {
                const string v1Prefix = "render frame v1: ";
                var detail = exception.Message.StartsWith(v1Prefix, StringComparison.Ordinal)
                    ? exception.Message.Substring(v1Prefix.Length)
                    : "shared payload is invalid";
                throw Error(detail);
            }

            return new NativeTerminalRenderFrameV2(
                sharedPayload,
                (NativeTerminalRenderCursorShapeV2)shapeValue,
                blinkingValue != 0);
        }

        private static NativeTerminalRenderFrameV2DecodeException Error(string message)
        {
            return new NativeTerminalRenderFrameV2DecodeException(message);
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
        }

        private static void ExpectUInt16(
            ReadOnlySpan<byte> bytes,
            int offset,
            ushort expected,
            string field)
        {
            if (ReadUInt16(bytes, offset) != expected)
                throw Error(field + " is unsupported");
        }

        private static void ExpectUInt64(
            ReadOnlySpan<byte> bytes,
            int offset,
            ulong expected,
            string field)
        {
            if (ReadUInt64(bytes, offset) != expected)
                throw Error(field + " is invalid");
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
    }
}
