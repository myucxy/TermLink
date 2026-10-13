using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace TermLink.TerminalCore
{
    /// <summary>
    /// A deterministic rejection of malformed or unsupported TermLink Render Frame V1 data.
    /// </summary>
    public sealed class NativeTerminalRenderFrameV1DecodeException : ArgumentException
    {
        internal NativeTerminalRenderFrameV1DecodeException(string message)
            : base("render frame v1: " + message)
        {
        }
    }

    public enum NativeTerminalRenderScreenV1
    {
        Primary = 0,
        Alternate = 1
    }

    public enum NativeTerminalRenderColorKindV1
    {
        Default = 0,
        Indexed = 1,
        Rgb = 2
    }

    /// <summary>
    /// A terminal color. Value is an index for Indexed and 0xRRGGBB for Rgb.
    /// </summary>
    public readonly struct NativeTerminalRenderColorV1
    {
        internal NativeTerminalRenderColorV1(NativeTerminalRenderColorKindV1 kind, int value)
        {
            Kind = kind;
            Value = value;
        }

        public NativeTerminalRenderColorKindV1 Kind { get; }
        public int Value { get; }
    }

    public readonly struct NativeTerminalRenderCursorV1
    {
        internal NativeTerminalRenderCursorV1(
            int row,
            int column,
            bool visible,
            bool wrapPending)
        {
            Row = row;
            Column = column;
            Visible = visible;
            WrapPending = wrapPending;
        }

        public int Row { get; }
        public int Column { get; }
        public bool Visible { get; }
        public bool WrapPending { get; }
    }

    public readonly struct NativeTerminalRenderGraphemeV1
    {
        internal NativeTerminalRenderGraphemeV1(string text, int displayWidth)
        {
            Text = text;
            DisplayWidth = displayWidth;
        }

        public string Text { get; }
        public int DisplayWidth { get; }
    }

    public readonly struct NativeTerminalRenderRenditionV1
    {
        internal NativeTerminalRenderRenditionV1(
            NativeTerminalRenderColorV1 foreground,
            NativeTerminalRenderColorV1 background,
            bool bold,
            bool blink,
            bool italic,
            bool inverse,
            bool underline,
            bool conceal,
            bool strikethrough)
        {
            Foreground = foreground;
            Background = background;
            Bold = bold;
            Blink = blink;
            Italic = italic;
            Inverse = inverse;
            Underline = underline;
            Conceal = conceal;
            Strikethrough = strikethrough;
        }

        public NativeTerminalRenderColorV1 Foreground { get; }
        public NativeTerminalRenderColorV1 Background { get; }
        public bool Bold { get; }
        public bool Blink { get; }
        public bool Italic { get; }
        public bool Inverse { get; }
        public bool Underline { get; }
        public bool Conceal { get; }
        public bool Strikethrough { get; }
    }

    /// <summary>
    /// Current OSC palette/default overrides. Null delegates to the renderer theme.
    /// </summary>
    public sealed class NativeTerminalRenderPaletteV1
    {
        internal NativeTerminalRenderPaletteV1(
            int?[] colorsRgb,
            int? defaultForegroundRgb,
            int? defaultBackgroundRgb,
            int? defaultCursorRgb, int? selectionBackgroundRgb = null, int? foregroundAlias = null, int? backgroundAlias = null, int? frameForegroundAlias = null, int? frameBackgroundAlias = null)
        {
            ColorsRgb = Array.AsReadOnly(colorsRgb);
            DefaultForegroundRgb = defaultForegroundRgb;
            DefaultBackgroundRgb = defaultBackgroundRgb;
            DefaultCursorRgb = defaultCursorRgb;
            SelectionBackgroundRgb = selectionBackgroundRgb; ForegroundAlias = foregroundAlias; BackgroundAlias = backgroundAlias;
            FrameForegroundAlias = frameForegroundAlias; FrameBackgroundAlias = frameBackgroundAlias;
        }

        public IReadOnlyList<int?> ColorsRgb { get; }
        public int? DefaultForegroundRgb { get; }
        public int? DefaultBackgroundRgb { get; }
        public int? DefaultCursorRgb { get; }
        public int? SelectionBackgroundRgb { get; }
        public int? ForegroundAlias { get; }
        public int? BackgroundAlias { get; }
        public int? FrameForegroundAlias { get; }
        public int? FrameBackgroundAlias { get; }
    }

    public readonly struct NativeTerminalRenderHyperlinkV1
    {
        internal NativeTerminalRenderHyperlinkV1(int reference, string uri)
        {
            Reference = reference;
            Uri = uri;
        }

        public int Reference { get; }
        public string Uri { get; }
    }

    public readonly struct NativeTerminalRenderCellV1
    {
        internal NativeTerminalRenderCellV1(
            int graphemeId,
            int renditionId,
            bool wideContinuation,
            bool isProtected,
            int hyperlinkReference)
        {
            GraphemeId = graphemeId;
            RenditionId = renditionId;
            WideContinuation = wideContinuation;
            IsProtected = isProtected;
            HyperlinkReference = hyperlinkReference;
        }

        /// <summary>One-based grapheme ID, or zero for a wide continuation.</summary>
        public int GraphemeId { get; }

        /// <summary>Zero-based rendition ID.</summary>
        public int RenditionId { get; }

        public bool WideContinuation { get; }
        public bool IsProtected { get; }

        /// <summary>A stable visible hyperlink reference, or zero.</summary>
        public int HyperlinkReference { get; }
    }

    public enum NativeTerminalLineRendition { SingleWidth = 0, DoubleWidth = 1, DoubleHeightTop = 2, DoubleHeightBottom = 3 }

    public sealed class NativeTerminalRenderRowV1
    {
        internal NativeTerminalRenderRowV1(
            int meaningfulEnd,
            bool softWrapped,
            NativeTerminalRenderCellV1[] cells,
            NativeTerminalLineRendition lineRendition = NativeTerminalLineRendition.SingleWidth)
        {
            MeaningfulEnd = meaningfulEnd;
            SoftWrapped = softWrapped;
            Cells = Array.AsReadOnly(cells);
            LineRendition = lineRendition;
        }

        public int MeaningfulEnd { get; }
        public bool SoftWrapped { get; }
        public NativeTerminalLineRendition LineRendition { get; }
        public IReadOnlyList<NativeTerminalRenderCellV1> Cells { get; }
    }

    /// <summary>
    /// Immutable, renderer-neutral projection of the active terminal screen.
    /// It is presentation state only and cannot be used as a restorable checkpoint.
    /// </summary>
    public sealed class NativeTerminalRenderFrameV1
    {
        internal NativeTerminalRenderFrameV1(
            ulong presentationRevision,
            ulong nextSourceSequence,
            int columns,
            int rows,
            NativeTerminalRenderScreenV1 activeScreen,
            bool reverseVideo,
            NativeTerminalRenderCursorV1 cursor,
            NativeTerminalRenderGraphemeV1[] graphemes,
            NativeTerminalRenderRenditionV1[] renditions,
            NativeTerminalRenderPaletteV1 palette,
            NativeTerminalRenderHyperlinkV1[] hyperlinks,
            NativeTerminalRenderRowV1[] renderRows,
            string title)
        {
            PresentationRevision = presentationRevision;
            NextSourceSequence = nextSourceSequence;
            Columns = columns;
            Rows = rows;
            ActiveScreen = activeScreen;
            ReverseVideo = reverseVideo;
            Cursor = cursor;
            Graphemes = Array.AsReadOnly(graphemes);
            Renditions = Array.AsReadOnly(renditions);
            Palette = palette;
            Hyperlinks = Array.AsReadOnly(hyperlinks);
            RenderRows = Array.AsReadOnly(renderRows);
            Title = title;
        }

        public const int FormatVersion = 1;

        public ulong PresentationRevision { get; }
        public ulong NextSourceSequence { get; }
        public int Columns { get; }
        public int Rows { get; }
        public NativeTerminalRenderScreenV1 ActiveScreen { get; }
        public bool ReverseVideo { get; }
        public NativeTerminalRenderCursorV1 Cursor { get; }
        public IReadOnlyList<NativeTerminalRenderGraphemeV1> Graphemes { get; }
        public IReadOnlyList<NativeTerminalRenderRenditionV1> Renditions { get; }
        public NativeTerminalRenderPaletteV1 Palette { get; }
        public IReadOnlyList<NativeTerminalRenderHyperlinkV1> Hyperlinks { get; }
        public IReadOnlyList<NativeTerminalRenderRowV1> RenderRows { get; }
        public string Title { get; }
    }

    /// <summary>
    /// Strict, allocation-bounded decoder for canonical TermLink Render Frame V1 data.
    /// </summary>
    public static class NativeTerminalRenderFrameV1Decoder
    {
        public const int MaximumFrameBytes = 16 * 1024 * 1024;

        public static NativeTerminalRenderFrameV1 Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            return Decode(bytes.AsSpan());
        }

        public static NativeTerminalRenderFrameV1 Decode(ReadOnlySpan<byte> bytes)
        {
            try
            {
                return (NativeTerminalRenderFrameV1)new Decoder(bytes, 1).Decode();
            }
            catch (NativeTerminalRenderFrameV1DecodeException)
            {
                throw;
            }
            catch (OverflowException)
            {
                throw Error("integer overflow");
            }
            catch (DecoderFallbackException)
            {
                throw Error("invalid UTF-8");
            }
            catch (ArgumentOutOfRangeException)
            {
                throw Error("truncated data");
            }
        }

        internal static NativeTerminalRenderFrameV4 DecodeV4(ReadOnlySpan<byte> bytes)
        {
            try { return (NativeTerminalRenderFrameV4)new Decoder(bytes, 4).Decode(); }
            catch (NativeTerminalRenderFrameV1DecodeException error) {
                throw new NativeTerminalRenderFrameV4DecodeException(error.Message.Substring("render frame v1: ".Length));
            }
            catch (OverflowException) { throw new NativeTerminalRenderFrameV4DecodeException("integer overflow"); }
            catch (DecoderFallbackException) { throw new NativeTerminalRenderFrameV4DecodeException("invalid UTF-8"); }
            catch (ArgumentOutOfRangeException) { throw new NativeTerminalRenderFrameV4DecodeException("truncated data"); }
        }

        private static NativeTerminalRenderFrameV1DecodeException Error(string message)
        {
            return new NativeTerminalRenderFrameV1DecodeException(message);
        }

        private ref struct Decoder
        {
            private const int HeaderSize = 96;
            private const int DirectoryEntrySize = 24;
            private const int DirectoryOffset = 96;
            private const int SectionCount = 6;
            private const int HeaderCrcEnd = 92;
            private const int MaximumColumns = 512;
            private const int MaximumRows = 512;
            private const int MaximumCells = 262_144;
            private const ushort MajorVersion = 1;
            private const ushort MinorVersion = 0;
            private const ushort SectionVersion = 1;
            private const ushort CriticalSection = 1;
            private const uint WidthProfile = 1;
            private const ulong RequiredFeatures = 0x1f;
            private const ushort AlternateScreenFlag = 1;
            private const ushort ReverseVideoFlag = 2;
            private const ushort FrameFlagsMask = AlternateScreenFlag | ReverseVideoFlag;
            private const uint CursorVisibleFlag = 1;
            private const uint CursorWrapFlag = 2;
            private const uint CursorFlagsMask = CursorVisibleFlag | CursorWrapFlag;
            private const int GraphemeTag = 1;
            private const int RenditionTag = 2;
            private const int PaletteTag = 3;
            private const int HyperlinkTag = 4;
            private const int RowsCellsTag = 5;
            private const int TitleTag = 6;
            private const int GraphemeRecordSize = 8;
            private const int MaximumGraphemeBytes = 14;
            private const int RenditionRecordSize = 16;
            private const uint RenditionBold = 1;
            private const uint RenditionBlink = 2;
            private const uint RenditionItalic = 4;
            private const uint RenditionInverse = 8;
            private const uint RenditionUnderline = 16;
            private const uint RenditionConceal = 32;
            private const uint RenditionStrikethrough = 64;
            private const uint RenditionFlagsMask = 0x7f;
            private const uint ColorTagMask = 0xff00_0000;
            private const uint ColorDefault = 0x0000_0000;
            private const uint ColorIndexed = 0x0100_0000;
            private const uint ColorRgb = 0x0200_0000;
            private const uint MaximumRgb = 0x00ff_ffff;
            private const int PaletteCount = 259;
            private const int PaletteBytes = PaletteCount * 4;
            private const uint UnsetRgb = uint.MaxValue;
            private const int HyperlinkRecordSize = 16;
            private const int MaximumHyperlinks = 1_024;
            private const int MaximumUriBytes = 4_096;
            private const int MaximumTotalUriBytes = 256 * 1024;
            private const int RowsPrefixSize = 8;
            private const int RowRecordSize = 8;
            private const int CellRecordSize = 12;
            private const uint RowSoftWrapped = 1;
            private const uint RowFlagsMask = RowSoftWrapped;
            private const uint CellContinuation = 1;
            private const uint CellProtected = 2;
            private const int LinkShift = 2;
            private const uint LinkMask = 0x7ff;
            private const uint CellMetadataMask = 0x1fff;
            private const int MaximumTitleBytes = 4_096;
            private const uint Crc32CPolynomial = 0x82f6_3b78;

            private static readonly byte[] MagicV4 = Encoding.ASCII.GetBytes("TLRFRM4\0");

            private static readonly byte[] Magic =
            {
                (byte)'T', (byte)'L', (byte)'R', (byte)'F',
                (byte)'R', (byte)'M', (byte)'1', 0
            };

            private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);

            private readonly ReadOnlySpan<byte> bytes;

            private readonly int version;
            private uint widthProfile;

            internal Decoder(ReadOnlySpan<byte> bytes, int version)
            {
                this.bytes = bytes;
                this.version = version;
                this.widthProfile = 1;
            }

            internal object Decode()
            {
                if (bytes.Length < HeaderSize) throw Error("header is truncated");
                if (bytes.Length > (version == 4 ? 32 * 1024 * 1024 : MaximumFrameBytes)) throw Error("frame exceeds version byte limit");
                if (!bytes.Slice(0, Magic.Length).SequenceEqual(version == 4 ? MagicV4 : Magic)) throw Error("bad magic");

                ExpectUInt16(8, (ushort)version, "major version");
                ExpectUInt16(10, MinorVersion, "minor version");
                ExpectUInt16(12, HeaderSize, "header size");
                ExpectUInt16(14, DirectoryEntrySize, "directory entry size");
                ExpectUInt16(16, SectionCount, "section count");
                var frameFlags = ReadUInt16(18, "frame flags");
                if ((frameFlags & ~(version == 4 ? 15 : FrameFlagsMask)) != 0) throw Error("unknown frame flags");
                var totalLength = ReadUInt32(20, "total length");
                if (totalLength != (uint)bytes.Length) throw Error("total length does not match input");

                var revision = ReadUInt64(24, "presentation revision");
                var nextSequence = ReadUInt64(32, "next source sequence");
                var columnsValue = ReadUInt32(40, "columns");
                var rowsValue = ReadUInt32(44, "rows");
                if (columnsValue < 1 || columnsValue > MaximumColumns)
                    throw Error("columns are out of range");
                if (rowsValue < 1 || rowsValue > MaximumRows)
                    throw Error("rows are out of range");
                var cellCountValue = Multiply(columnsValue, rowsValue, "cell count");
                if (cellCountValue > MaximumCells) throw Error("cell count exceeds the hard limit");
                var columns = ToInt(columnsValue, "columns");
                var rowCount = ToInt(rowsValue, "rows");
                var cellCount = ToInt(cellCountValue, "cell count");

                var cursorRow = ToInt(ReadUInt32(48, "cursor row"), "cursor row");
                var cursorColumn = ToInt(ReadUInt32(52, "cursor column"), "cursor column");
                if (cursorRow < 0 || cursorRow >= rowCount ||
                    cursorColumn < 0 || cursorColumn >= columns)
                {
                    throw Error("cursor is outside the frame");
                }

                var cursorFlags = ReadUInt32(56, "cursor flags");
                if ((cursorFlags & ~CursorFlagsMask) != 0) throw Error("unknown cursor flags");
                widthProfile = ReadUInt32(60, "width profile");
                if (widthProfile != 1 && !(version == 4 && widthProfile == 2)) throw Error("unsupported width profile");
                ExpectUInt32(64, DirectoryOffset, "directory offset");
                if (version == 4) {
                    if (bytes[68] > 2 || bytes[69] > 1) throw Error("invalid cursor presentation");
                    ExpectUInt16(70, 0, "cursor reserved field");
                } else { ExpectUInt32(68, 0, "header reserved field"); }
                ExpectUInt64(72, version == 4 ? 0x7ffUL : RequiredFeatures, "required features");
                ExpectUInt64(80, 0, "header reserved field");

                var payloadCrc = ReadUInt32(88, "payload CRC32C");
                var headerCrc = ReadUInt32(92, "header CRC32C");
                if (Crc32C(bytes.Slice(0, HeaderCrcEnd)) != headerCrc)
                    throw Error("header CRC32C mismatch");
                if (Crc32C(bytes.Slice(HeaderSize)) != payloadCrc)
                    throw Error("payload CRC32C mismatch");

                var sections = DecodeDirectory(cellCount);
                var graphemes = DecodeGraphemes(sections[0]);
                var renditions = version == 1 ? DecodeRenditions(sections[1]) : Array.Empty<NativeTerminalRenderRenditionV1>();
                var modernRenditions = version == 4 ? DecodeModernRenditions(sections[1]) : Array.Empty<NativeTerminalRenderRenditionV3>();
                var palette = DecodePalette(sections[2]);
                var hyperlinks = DecodeHyperlinks(sections[3]);
                var renderRows = DecodeRows(
                    sections[4],
                    columns,
                    rowCount,
                    cellCount,
                    graphemes,
                    sections[1].Count,
                    hyperlinks);
                var title = DecodeTitle(sections[5]);

                if (version == 4) return new NativeTerminalRenderFrameV4(
                    (NativeTerminalWidthProfile)widthProfile, revision, nextSequence, columns, rowCount,
                    (frameFlags & AlternateScreenFlag) != 0 ? NativeTerminalRenderScreenV1.Alternate : NativeTerminalRenderScreenV1.Primary,
                    (frameFlags & ReverseVideoFlag) != 0,
                    new NativeTerminalRenderCursorV2(new NativeTerminalRenderCursorV1(cursorRow, cursorColumn,
                        (cursorFlags & CursorVisibleFlag) != 0, (cursorFlags & CursorWrapFlag) != 0),
                        (NativeTerminalRenderCursorShapeV2)bytes[68], bytes[69] != 0),
                    graphemes, modernRenditions, palette, hyperlinks, renderRows, title, (frameFlags & 4) != 0, (frameFlags & 8) != 0);

                return new NativeTerminalRenderFrameV1(
                    revision,
                    nextSequence,
                    columns,
                    rowCount,
                    (frameFlags & AlternateScreenFlag) != 0
                        ? NativeTerminalRenderScreenV1.Alternate
                        : NativeTerminalRenderScreenV1.Primary,
                    (frameFlags & ReverseVideoFlag) != 0,
                    new NativeTerminalRenderCursorV1(
                        cursorRow,
                        cursorColumn,
                        (cursorFlags & CursorVisibleFlag) != 0,
                        (cursorFlags & CursorWrapFlag) != 0),
                    graphemes,
                    renditions,
                    palette,
                    hyperlinks,
                    renderRows,
                    title);
            }

            private Section[] DecodeDirectory(int cellCount)
            {
                var directoryEnd = Add(
                    DirectoryOffset,
                    Multiply(SectionCount, DirectoryEntrySize, "directory size"),
                    "directory end");
                if (directoryEnd > (ulong)bytes.Length) throw Error("directory is truncated");
                var expectedOffset = Align8(directoryEnd, "first section offset");
                RequireZeroRange(directoryEnd, expectedOffset, "directory padding");

                var result = new Section[SectionCount];
                for (var index = 0; index < SectionCount; index++)
                {
                    var entryOffset = Add(
                        DirectoryOffset,
                        Multiply((ulong)index, DirectoryEntrySize, "directory entry"),
                        "directory entry");
                    var entry = ToInt(entryOffset, "directory entry");
                    var expectedTag = index + 1;
                    if (ReadUInt32(entry, "section tag") != expectedTag)
                        throw Error("section tags must be exactly 1..6");
                    ExpectUInt16(entry + 4, (ushort)(version == 4 && expectedTag == RenditionTag ? 2 : SectionVersion), "section version");
                    ExpectUInt16(entry + 6, CriticalSection, "section flags");
                    var offset = ReadUInt32(entry + 8, "section offset");
                    var length = ReadUInt32(entry + 12, "section length");
                    var count = ReadUInt32(entry + 16, "section logical count");
                    var recordSize = ReadUInt32(entry + 20, "section record size");

                    if (offset != expectedOffset)
                        throw Error("section " + expectedTag.ToString() + " offset is not canonical");
                    var end = Add(offset, length, "section " + expectedTag.ToString() + " end");
                    var paddedEnd = Align8(end, "section " + expectedTag.ToString() + " padding");
                    if (paddedEnd > (ulong)bytes.Length)
                        throw Error("section " + expectedTag.ToString() + " is outside the frame");
                    RequireZeroRange(end, paddedEnd, "section " + expectedTag.ToString() + " padding");

                    switch (expectedTag)
                    {
                        case GraphemeTag:
                            if (recordSize != GraphemeRecordSize)
                                throw Error("grapheme record size must be 8");
                            if (count < 1 || count > (uint)cellCount)
                                throw Error("grapheme count is out of range");
                            break;
                        case RenditionTag:
                            if (recordSize != RenditionRecordSize)
                                throw Error("rendition record size must be 16");
                            if (count < 1 || count > (uint)cellCount)
                                throw Error("rendition count is out of range");
                            break;
                        case PaletteTag:
                            if (count != (version == 4 ? 264 : PaletteCount) || recordSize != 4)
                                throw Error("palette directory metadata is invalid");
                            break;
                        case HyperlinkTag:
                            if (count > MaximumHyperlinks || recordSize != HyperlinkRecordSize)
                                throw Error("hyperlink directory metadata is invalid");
                            break;
                        case RowsCellsTag:
                            if (count != (uint)cellCount || recordSize != 0)
                                throw Error("rows directory metadata is invalid");
                            break;
                        case TitleTag:
                            if (count != length || recordSize != 0 || length > MaximumTitleBytes)
                                throw Error("title directory metadata is invalid");
                            break;
                    }

                    result[index] = new Section(
                        expectedTag,
                        ToInt(offset, "section offset"),
                        ToInt(length, "section length"),
                        ToInt(count, "section logical count"));
                    expectedOffset = paddedEnd;
                }

                if (expectedOffset != (ulong)bytes.Length)
                    throw Error("frame has trailing or missing bytes");
                return result;
            }

            private NativeTerminalRenderGraphemeV1[] DecodeGraphemes(Section section)
            {
                var recordsBytes = Multiply(
                    (ulong)section.Count,
                    GraphemeRecordSize,
                    "grapheme records");
                if (recordsBytes > (ulong)section.Length)
                    throw Error("grapheme records are truncated");

                var result = new NativeTerminalRenderGraphemeV1[section.Count];
                var expectedStringOffset = recordsBytes;
                var previousOffset = -1;
                var previousLength = 0;
                for (var index = 0; index < section.Count; index++)
                {
                    var record = Absolute(
                        section,
                        Multiply((ulong)index, GraphemeRecordSize, "grapheme record"),
                        GraphemeRecordSize);
                    var stringOffset = ReadUInt32(record, "grapheme UTF-8 offset");
                    var length = ReadUInt16(record + 4, "grapheme UTF-8 length");
                    var width = ReadByte(record + 6, "grapheme width");
                    var reserved = ReadByte(record + 7, "grapheme reserved byte");
                    if (stringOffset != expectedStringOffset)
                        throw Error("grapheme strings are not contiguous");
                    if (length < 1 || length > (widthProfile == 2 ? 48 : MaximumGraphemeBytes))
                        throw Error("grapheme UTF-8 length is out of range");
                    if (width != 1 && width != 2) throw Error("grapheme width is invalid");
                    if (reserved != 0) throw Error("grapheme reserved byte is nonzero");
                    var end = Add(stringOffset, length, "grapheme UTF-8 end");
                    if (end > (ulong)section.Length)
                        throw Error("grapheme UTF-8 bytes are truncated");
                    var absolute = Absolute(section, stringOffset, length);
                    var raw = bytes.Slice(absolute, length);
                    if (previousOffset >= 0 &&
                        CompareUnsigned(bytes.Slice(previousOffset, previousLength), raw) >= 0)
                    {
                        throw Error("grapheme table is not strictly sorted");
                    }

                    result[index] = new NativeTerminalRenderGraphemeV1(
                        DecodeUtf8(raw),
                        width);
                    previousOffset = absolute;
                    previousLength = length;
                    expectedStringOffset = end;
                }

                if (expectedStringOffset != (ulong)section.Length)
                    throw Error("grapheme section has unused bytes");
                return result;
            }

            private NativeTerminalRenderRenditionV1[] DecodeRenditions(Section section)
            {
                var expectedLength = Multiply(
                    (ulong)section.Count,
                    RenditionRecordSize,
                    "rendition section length");
                if (expectedLength != (ulong)section.Length)
                    throw Error("rendition section length is invalid");

                var result = new NativeTerminalRenderRenditionV1[section.Count];
                var previousOffset = -1;
                for (var index = 0; index < section.Count; index++)
                {
                    var offset = Absolute(
                        section,
                        Multiply((ulong)index, RenditionRecordSize, "rendition record"),
                        RenditionRecordSize);
                    if (previousOffset >= 0 &&
                        CompareUnsigned(
                            bytes.Slice(previousOffset, RenditionRecordSize),
                            bytes.Slice(offset, RenditionRecordSize)) >= 0)
                    {
                        throw Error("rendition table is not strictly sorted");
                    }

                    var flags = ReadUInt32(offset, "rendition flags");
                    if ((flags & ~RenditionFlagsMask) != 0)
                        throw Error("unknown rendition flags");
                    var foreground = DecodeColor(ReadUInt32(offset + 4, "foreground color"));
                    var background = DecodeColor(ReadUInt32(offset + 8, "background color"));
                    ExpectUInt32(offset + 12, 0, "rendition reserved field");
                    result[index] = new NativeTerminalRenderRenditionV1(
                        foreground,
                        background,
                        (flags & RenditionBold) != 0,
                        (flags & RenditionBlink) != 0,
                        (flags & RenditionItalic) != 0,
                        (flags & RenditionInverse) != 0,
                        (flags & RenditionUnderline) != 0,
                        (flags & RenditionConceal) != 0,
                        (flags & RenditionStrikethrough) != 0);
                    previousOffset = offset;
                }
                return result;
            }

            private NativeTerminalRenderRenditionV3[] DecodeModernRenditions(Section section)
            {
                if (Multiply((ulong)section.Count, RenditionRecordSize, "rendition bytes") != (ulong)section.Length)
                    throw Error("rendition section length is invalid");
                var result = new NativeTerminalRenderRenditionV3[section.Count];
                for (var index = 0; index < result.Length; index++) {
                    var offset = Absolute(section, Multiply((ulong)index, RenditionRecordSize, "rendition record"), RenditionRecordSize);
                    if (index > 0 && CompareUnsigned(bytes.Slice(offset - RenditionRecordSize, RenditionRecordSize), bytes.Slice(offset, RenditionRecordSize)) >= 0)
                        throw Error("rendition table is not strictly sorted");
                    var flags = ReadUInt32(offset, "rendition flags");
                    var style = (flags >> 9) & 7;
                    if ((flags & ~0xfffU) != 0 || style > 5 || ((flags & 16) != 0) != (style != 0))
                        throw Error("invalid extended rendition flags");
                    result[index] = new NativeTerminalRenderRenditionV3(
                        DecodeColor(ReadUInt32(offset + 4, "foreground")), DecodeColor(ReadUInt32(offset + 8, "background")),
                        DecodeColor(ReadUInt32(offset + 12, "underline color")),
                        (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0,
                        (flags & 32) != 0, (flags & 64) != 0, (flags & 128) != 0, (flags & 256) != 0,
                        (NativeTerminalRenderUnderlineStyleV3)style);
                }
                return result;
            }

            private NativeTerminalRenderPaletteV1 DecodePalette(Section section)
            {
                if (section.Length != (version == 4 ? 264 * 4 : PaletteBytes))
                    throw Error("palette section length is invalid");
                var values = new int?[version == 4 ? 264 : PaletteCount];
                for (var index = 0; index < values.Length; index++)
                {
                    var value = ReadUInt32(
                        Absolute(section, Multiply((ulong)index, 4, "palette record"), 4),
                        "palette value");
                    if (value == UnsetRgb)
                    {
                        values[index] = null;
                    }
                    else if (value <= (index >= 260 ? 257U : MaximumRgb) && !(index >= 260 && value == 256 + (index % 2)))
                    {
                        values[index] = (int)value;
                    }
                    else
                    {
                        throw Error("palette value is invalid");
                    }
                }

                var colors = new int?[256];
                Array.Copy(values, colors, colors.Length);
                return new NativeTerminalRenderPaletteV1(
                    colors,
                    values[256],
                    values[257],
                    values[258], version == 4 ? values[259] : null, version == 4 ? values[260] : null, version == 4 ? values[261] : null,
                    version == 4 ? values[262] : null, version == 4 ? values[263] : null);
            }

            private NativeTerminalRenderHyperlinkV1[] DecodeHyperlinks(Section section)
            {
                var recordsBytes = Multiply(
                    (ulong)section.Count,
                    HyperlinkRecordSize,
                    "hyperlink records");
                if (recordsBytes > (ulong)section.Length)
                    throw Error("hyperlink records are truncated");

                var result = new NativeTerminalRenderHyperlinkV1[section.Count];
                var expectedUriOffset = recordsBytes;
                ulong totalUriBytes = 0;
                uint previousReference = 0;
                for (var index = 0; index < section.Count; index++)
                {
                    var record = Absolute(
                        section,
                        Multiply((ulong)index, HyperlinkRecordSize, "hyperlink record"),
                        HyperlinkRecordSize);
                    var reference = ReadUInt32(record, "hyperlink reference");
                    var uriOffset = ReadUInt32(record + 4, "hyperlink URI offset");
                    var uriLength = ReadUInt32(record + 8, "hyperlink URI length");
                    ExpectUInt32(record + 12, 0, "hyperlink reserved field");
                    if (reference < 1 || reference > MaximumHyperlinks ||
                        reference <= previousReference)
                    {
                        throw Error("hyperlinks are not sorted by a valid reference");
                    }
                    if (uriOffset != expectedUriOffset)
                        throw Error("hyperlink URIs are not contiguous");
                    if (uriLength < 1 || uriLength > MaximumUriBytes)
                        throw Error("hyperlink URI length is out of range");
                    var end = Add(uriOffset, uriLength, "hyperlink URI end");
                    if (end > (ulong)section.Length)
                        throw Error("hyperlink URI bytes are truncated");
                    totalUriBytes = Add(totalUriBytes, uriLength, "hyperlink URI bytes");
                    if (totalUriBytes > MaximumTotalUriBytes)
                        throw Error("hyperlink URI bytes exceed the limit");
                    var length = ToInt(uriLength, "hyperlink URI length");
                    var raw = bytes.Slice(Absolute(section, uriOffset, length), length);
                    result[index] = new NativeTerminalRenderHyperlinkV1(
                        (int)reference,
                        DecodeUtf8(raw));
                    previousReference = reference;
                    expectedUriOffset = end;
                }

                if (expectedUriOffset != (ulong)section.Length)
                    throw Error("hyperlink section has unused bytes");
                return result;
            }

            private NativeTerminalRenderRowV1[] DecodeRows(
                Section section,
                int columns,
                int rowCount,
                int cellCount,
                NativeTerminalRenderGraphemeV1[] graphemes,
                int renditionCount,
                NativeTerminalRenderHyperlinkV1[] hyperlinks)
            {
                var rowBytes = Multiply((ulong)rowCount, RowRecordSize, "row records");
                var cellBytes = Multiply((ulong)cellCount, CellRecordSize, "cell records");
                var expectedLength = Add(
                    RowsPrefixSize,
                    Add(rowBytes, cellBytes, "rows section length"),
                    "rows section length");
                if (expectedLength != (ulong)section.Length)
                    throw Error("rows section length is invalid");
                ExpectUInt32(section.Offset, RowRecordSize, "row record size");
                ExpectUInt32(section.Offset + 4, CellRecordSize, "cell record size");

                var referencedGraphemes = new bool[graphemes.Length];
                var referencedRenditions = new bool[renditionCount];
                var knownLinks = new bool[MaximumHyperlinks + 1];
                var referencedLinks = new bool[MaximumHyperlinks + 1];
                for (var index = 0; index < hyperlinks.Length; index++)
                    knownLinks[hyperlinks[index].Reference] = true;

                var rows = new NativeTerminalRenderRowV1[rowCount];
                var cellDataOffset = Add(RowsPrefixSize, rowBytes, "cell data offset");
                for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var rowOffset = Absolute(
                        section,
                        Add(
                            RowsPrefixSize,
                            Multiply((ulong)rowIndex, RowRecordSize, "row record"),
                            "row record"),
                        RowRecordSize);
                    var meaningfulEnd = ReadUInt32(rowOffset, "meaningful end");
                    var rowFlags = ReadUInt32(rowOffset + 4, "row flags");
                    if ((rowFlags & ~(version == 4 ? 7U : RowFlagsMask)) != 0) throw Error("unknown row flags");
                    var lineRendition = (NativeTerminalLineRendition)((rowFlags >> 1) & 3);
                    var effectiveColumns = lineRendition == NativeTerminalLineRendition.SingleWidth ? columns : Math.Max(1, columns / 2);
                    if (meaningfulEnd > (uint)effectiveColumns)
                        throw Error("meaningful end is outside the row");
                    if ((rowFlags & RowSoftWrapped) != 0 && (rowIndex == rowCount - 1 || lineRendition != NativeTerminalLineRendition.SingleWidth)) throw Error("invalid scaled or final soft-wrapped row");
                    if (lineRendition != NativeTerminalLineRendition.SingleWidth && rowIndex > 0 && rows[rowIndex - 1].SoftWrapped) throw Error("soft wrap crosses a scaled row");
                    if (version == 4 && rowIndex == ReadUInt32(48, "cursor row") &&
                        (ReadUInt32(52, "cursor column") >= effectiveColumns)) throw Error("cursor is outside the effective row");

                    var cells = new NativeTerminalRenderCellV1[columns];
                    for (var column = 0; column < columns; column++)
                    {
                        var cellIndex = Add(
                            Multiply((ulong)rowIndex, (ulong)columns, "cell index"),
                            (ulong)column,
                            "cell index");
                        var cellOffset = Absolute(
                            section,
                            Add(
                                cellDataOffset,
                                Multiply(cellIndex, CellRecordSize, "cell record"),
                                "cell record"),
                            CellRecordSize);
                        var graphemeIdValue = ReadUInt32(cellOffset, "cell grapheme ID");
                        var renditionIdValue = ReadUInt32(cellOffset + 4, "cell rendition ID");
                        var metadata = ReadUInt32(cellOffset + 8, "cell metadata");
                        if ((metadata & ~CellMetadataMask) != 0)
                            throw Error("unknown cell metadata");
                        var continuation = (metadata & CellContinuation) != 0;
                        var isProtected = (metadata & CellProtected) != 0;
                        var hyperlinkReference = (int)((metadata >> LinkShift) & LinkMask);
                        if (continuation != (graphemeIdValue == 0))
                            throw Error("continuation flag and grapheme ID disagree");
                        if (graphemeIdValue > (uint)graphemes.Length)
                            throw Error("cell has a dangling grapheme ID");
                        if (renditionIdValue >= (uint)renditionCount)
                            throw Error("cell has a dangling rendition ID");
                        if (hyperlinkReference != 0)
                        {
                            if (!knownLinks[hyperlinkReference])
                                throw Error("cell has a dangling hyperlink reference");
                            referencedLinks[hyperlinkReference] = true;
                        }

                        var graphemeId = (int)graphemeIdValue;
                        var renditionId = (int)renditionIdValue;
                        if (graphemeId != 0) referencedGraphemes[graphemeId - 1] = true;
                        referencedRenditions[renditionId] = true;
                        cells[column] = new NativeTerminalRenderCellV1(
                            graphemeId,
                            renditionId,
                            continuation,
                            isProtected,
                            hyperlinkReference);
                    }

                    ValidateWideCells(cells, graphemes, rowIndex);
                    for (var column = effectiveColumns; column < columns; column++) {
                        var cell = cells[column];
                        if (cell.WideContinuation || cell.IsProtected || cell.HyperlinkReference != 0 || graphemes[cell.GraphemeId - 1].Text != " ") throw Error("nonblank cell outside scaled row");
                    }
                    rows[rowIndex] = new NativeTerminalRenderRowV1(
                        (int)meaningfulEnd,
                        (rowFlags & RowSoftWrapped) != 0,
                        cells, lineRendition);
                }

                if (HasFalse(referencedGraphemes))
                    throw Error("grapheme table contains an unreferenced entry");
                if (HasFalse(referencedRenditions))
                    throw Error("rendition table contains an unreferenced entry");
                for (var index = 0; index < hyperlinks.Length; index++)
                {
                    if (!referencedLinks[hyperlinks[index].Reference])
                        throw Error("hyperlink table contains an unreferenced entry");
                }
                return rows;
            }

            private static void ValidateWideCells(
                NativeTerminalRenderCellV1[] cells,
                NativeTerminalRenderGraphemeV1[] graphemes,
                int rowIndex)
            {
                var column = 0;
                while (column < cells.Length)
                {
                    var cell = cells[column];
                    if (cell.WideContinuation)
                    {
                        throw Error("standalone continuation at row " + rowIndex.ToString() +
                            " column " + column.ToString());
                    }

                    var width = graphemes[cell.GraphemeId - 1].DisplayWidth;
                    if (width == 2)
                    {
                        if (column + 1 >= cells.Length)
                            throw Error("wide lead at the last column");
                        var continuation = cells[column + 1];
                        if (!continuation.WideContinuation)
                            throw Error("wide lead is missing its continuation");
                        if (continuation.RenditionId != cell.RenditionId ||
                            continuation.IsProtected != cell.IsProtected ||
                            continuation.HyperlinkReference != cell.HyperlinkReference)
                        {
                            throw Error("wide continuation attributes differ from its lead");
                        }
                        column += 2;
                    }
                    else
                    {
                        column++;
                    }
                }
            }

            private string DecodeTitle(Section section)
            {
                if (section.Length > MaximumTitleBytes)
                    throw Error("title exceeds the byte limit");
                return DecodeUtf8(bytes.Slice(section.Offset, section.Length));
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
                        if (payload > byte.MaxValue) throw Error("indexed color is out of range");
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

            private string DecodeUtf8(ReadOnlySpan<byte> raw)
            {
                try
                {
                    return StrictUtf8.GetString(raw);
                }
                catch (DecoderFallbackException)
                {
                    throw Error("invalid UTF-8");
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

            private void ExpectUInt32(int offset, uint expected, string field)
            {
                if (ReadUInt32(offset, field) != expected)
                    throw Error(field + " is invalid");
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

            private int Absolute(Section section, ulong relative, int length)
            {
                if (length < 0) throw Error("section " + section.Tag.ToString() + " range is invalid");
                var end = Add(relative, (ulong)length, "section " + section.Tag.ToString() + " range");
                if (end > (ulong)section.Length)
                    throw Error("section " + section.Tag.ToString() + " range is truncated");
                return ToInt(
                    Add((ulong)section.Offset, relative, "section " + section.Tag.ToString() + " address"),
                    "section " + section.Tag.ToString() + " address");
            }

            private void RequireZeroRange(ulong start, ulong end, string field)
            {
                if (end < start || end > (ulong)bytes.Length)
                    throw Error(field + " is outside the frame");
                var first = ToInt(start, field);
                var last = ToInt(end, field);
                for (var index = first; index < last; index++)
                {
                    if (bytes[index] != 0) throw Error(field + " is nonzero");
                }
            }

            private static bool HasFalse(bool[] values)
            {
                for (var index = 0; index < values.Length; index++)
                {
                    if (!values[index]) return true;
                }
                return false;
            }

            private static int CompareUnsigned(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
            {
                var common = Math.Min(first.Length, second.Length);
                for (var index = 0; index < common; index++)
                {
                    var difference = first[index] - second[index];
                    if (difference != 0) return difference;
                }
                return first.Length - second.Length;
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

            private static ulong Align8(ulong value, string field)
            {
                return Add(value, 7, field) & ~7UL;
            }

            private static int ToInt(ulong value, string field)
            {
                if (value > int.MaxValue) throw Error(field + " exceeds signed 32-bit range");
                return (int)value;
            }

            private readonly struct Section
            {
                internal Section(int tag, int offset, int length, int count)
                {
                    Tag = tag;
                    Offset = offset;
                    Length = length;
                    Count = count;
                }

                internal int Tag { get; }
                internal int Offset { get; }
                internal int Length { get; }
                internal int Count { get; }
            }
        }
    }
}
