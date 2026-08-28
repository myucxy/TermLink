using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace TermLink.Agent
{
    [Flags]
    internal enum PortableTerminalAttributesV1 : ushort
    {
        None = 0,
        Bold = 1 << 0,
        Dim = 1 << 1,
        Italic = 1 << 2,
        Underline = 1 << 3,
        Blink = 1 << 4,
        Inverse = 1 << 5,
        Invisible = 1 << 6,
        Strikethrough = 1 << 7
    }

    internal readonly struct PortableTerminalStyleV1 : IEquatable<PortableTerminalStyleV1>
    {
        public const int DefaultColor = -1;
        private const int RgbFlag = 1 << 24;
        private const int RgbMask = 0x00ffffff;

        public PortableTerminalStyleV1(
            int foreground,
            int background,
            PortableTerminalAttributesV1 attributes,
            int hyperlinkIndexPlusOne = 0)
        {
            ValidateColor(foreground, nameof(foreground));
            ValidateColor(background, nameof(background));
            if ((attributes & ~AllAttributes) != 0)
                throw new ArgumentOutOfRangeException(nameof(attributes));
            if (hyperlinkIndexPlusOne < 0 || hyperlinkIndexPlusOne > 256)
                throw new ArgumentOutOfRangeException(nameof(hyperlinkIndexPlusOne));
            Foreground = foreground;
            Background = background;
            Attributes = attributes;
            HyperlinkIndexPlusOne = hyperlinkIndexPlusOne;
        }

        public int Foreground { get; }
        public int Background { get; }
        public PortableTerminalAttributesV1 Attributes { get; }
        public int HyperlinkIndexPlusOne { get; }

        public static PortableTerminalStyleV1 Default =>
            new PortableTerminalStyleV1(DefaultColor, DefaultColor, PortableTerminalAttributesV1.None);

        public bool Equals(PortableTerminalStyleV1 other) =>
            Foreground == other.Foreground && Background == other.Background &&
            Attributes == other.Attributes && HyperlinkIndexPlusOne == other.HyperlinkIndexPlusOne;

        public override bool Equals(object value) => value is PortableTerminalStyleV1 other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Foreground, Background, (ushort)Attributes, HyperlinkIndexPlusOne);

        internal PortableTerminalStyleV1 WithForeground(int value) =>
            new PortableTerminalStyleV1(value, Background, Attributes, HyperlinkIndexPlusOne);

        internal PortableTerminalStyleV1 WithBackground(int value) =>
            new PortableTerminalStyleV1(Foreground, value, Attributes, HyperlinkIndexPlusOne);

        internal static int PaletteColor(int index)
        {
            if (index < 0 || index > 255) throw new ArgumentOutOfRangeException(nameof(index));
            return index;
        }

        internal static int RgbColor(int red, int green, int blue)
        {
            if ((uint)red > 255 || (uint)green > 255 || (uint)blue > 255)
                throw new ArgumentOutOfRangeException(nameof(red), "RGB components must be bytes.");
            return RgbFlag | (red << 16) | (green << 8) | blue;
        }

        internal static bool TryGetRgb(int value, out int rgb)
        {
            rgb = value & RgbMask;
            return value >= RgbFlag && value <= (RgbFlag | RgbMask);
        }

        internal PortableTerminalStyleV1 WithAttributes(PortableTerminalAttributesV1 value) =>
            new PortableTerminalStyleV1(Foreground, Background, value, HyperlinkIndexPlusOne);

        internal PortableTerminalStyleV1 WithHyperlink(int value) =>
            new PortableTerminalStyleV1(Foreground, Background, Attributes, value);

        private static PortableTerminalAttributesV1 AllAttributes =>
            PortableTerminalAttributesV1.Bold |
            PortableTerminalAttributesV1.Dim |
            PortableTerminalAttributesV1.Italic |
            PortableTerminalAttributesV1.Underline |
            PortableTerminalAttributesV1.Blink |
            PortableTerminalAttributesV1.Inverse |
            PortableTerminalAttributesV1.Invisible |
            PortableTerminalAttributesV1.Strikethrough;

        private static void ValidateColor(int value, string name)
        {
            if (value != DefaultColor && (value < 0 ||
                (value > 255 && (value < RgbFlag || value > (RgbFlag | RgbMask)))))
            {
                throw new ArgumentOutOfRangeException(name, "Terminal color must be default, palette, or RGB.");
            }
        }
    }

    internal sealed class PortableTerminalHyperlinkV1
    {
        public PortableTerminalHyperlinkV1(string id, string uri, string parameters)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Uri = uri ?? throw new ArgumentNullException(nameof(uri));
            Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            if (Uri.Length == 0) throw new ArgumentException("A terminal hyperlink URI is empty.", nameof(uri));
        }

        public string Id { get; }
        public string Uri { get; }
        public string Parameters { get; }
    }

    internal enum PortableTerminalCursorStyleV1 : byte
    {
        Block,
        Underline,
        Bar
    }

    internal sealed class PortableTerminalCellV1
    {
        public PortableTerminalCellV1(char character, PortableTerminalStyleV1 style)
            : this(character.ToString(), style, 1, false)
        {
        }

        public PortableTerminalCellV1(
            string character,
            PortableTerminalStyleV1 style,
            int displayWidth = 1,
            bool wideContinuation = false)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (wideContinuation)
            {
                if (character.Length != 0 || displayWidth != 0)
                    throw new ArgumentException("A wide continuation must be empty and width zero.", nameof(character));
            }
            else
            {
                if (character.Length == 0 || (displayWidth != 1 && displayWidth != 2))
                    throw new ArgumentException("A terminal grapheme must be non-empty and have width one or two.", nameof(character));
                try
                {
                    if (new UTF8Encoding(false, true).GetByteCount(character) > 64)
                        throw new ArgumentOutOfRangeException(nameof(character), "A terminal grapheme exceeds the V1 UTF-8 bound.");
                }
                catch (EncoderFallbackException error)
                {
                    throw new ArgumentException("A terminal grapheme contains invalid UTF-16.", nameof(character), error);
                }
            }
            Character = character;
            Style = style;
            DisplayWidth = displayWidth;
            WideContinuation = wideContinuation;
        }

        public string Character { get; }
        public PortableTerminalStyleV1 Style { get; }
        public int DisplayWidth { get; }
        public bool WideContinuation { get; }
    }

    internal sealed class PortableTerminalRowV1
    {
        private readonly PortableTerminalCellV1[] cells;
        private readonly ReadOnlyCollection<PortableTerminalCellV1> readOnlyCells;

        public PortableTerminalRowV1(IEnumerable<PortableTerminalCellV1> cells, bool wrapped)
            : this(cells, wrapped, 0, 0)
        {
        }

        internal PortableTerminalRowV1(
            IEnumerable<PortableTerminalCellV1> cells,
            bool wrapped,
            ulong lineId,
            ulong sealedAtSequence)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (sealedAtSequence > 0 && lineId == 0)
                throw new ArgumentException("A sealed terminal row must have a line ID.", nameof(lineId));
            this.cells = CloneCells(cells);
            if (this.cells.Length == 0) throw new ArgumentException("A terminal row cannot be empty.", nameof(cells));
            readOnlyCells = Array.AsReadOnly(this.cells);
            Wrapped = wrapped;
            LineId = lineId;
            SealedAtSequence = sealedAtSequence;
        }

        public IReadOnlyList<PortableTerminalCellV1> Cells => readOnlyCells;
        public bool Wrapped { get; }
        public ulong LineId { get; }
        public ulong SealedAtSequence { get; }

        internal PortableTerminalCellV1[] CloneCellArray() => CloneCells(cells);

        private static PortableTerminalCellV1[] CloneCells(IEnumerable<PortableTerminalCellV1> values)
        {
            var result = new List<PortableTerminalCellV1>();
            foreach (var cell in values)
            {
                if (cell == null) throw new ArgumentException("A terminal row contains a null cell.", nameof(values));
                result.Add(new PortableTerminalCellV1(
                    cell.Character,
                    cell.Style,
                    cell.DisplayWidth,
                    cell.WideContinuation));
            }
            var cells = result.ToArray();
            for (var index = 0; index < cells.Length; index++)
            {
                var cell = cells[index];
                if (cell.WideContinuation)
                {
                    if (index == 0 || cells[index - 1].DisplayWidth != 2 ||
                        !cells[index - 1].Style.Equals(cell.Style))
                        throw new ArgumentException("A terminal row contains an orphaned wide continuation.", nameof(values));
                }
                else if (cell.DisplayWidth == 2)
                {
                    if (index + 1 >= cells.Length || !cells[index + 1].WideContinuation ||
                        !cells[index + 1].Style.Equals(cell.Style))
                        throw new ArgumentException("A terminal row contains an incomplete wide grapheme.", nameof(values));
                }
            }
            return cells;
        }
    }

    // This DTO is deliberately local to the Windows producer. It is not the
    // negotiated TerminalSnapshotV1 wire format and does not enable a runtime
    // capability. Only a model at a proven parser boundary can produce it.
    internal sealed class PortableTerminalSnapshotV1
    {
        public const string CurrentFormatName = "termlink.portable-terminal-model-v1";
        public const uint CurrentFormatVersion = 1;
        private readonly PortableTerminalRowV1[] scrollbackRows;
        private readonly PortableTerminalRowV1[] screenRows;
        private readonly PortableTerminalRowV1[] alternateScreenRows;
        private readonly int[] tabStops;
        private readonly int[] paletteRgb;
        private readonly PortableTerminalHyperlinkV1[] hyperlinks;
        private readonly ReadOnlyCollection<PortableTerminalRowV1> readOnlyScrollbackRows;
        private readonly ReadOnlyCollection<PortableTerminalRowV1> readOnlyScreenRows;
        private readonly ReadOnlyCollection<PortableTerminalRowV1> readOnlyAlternateScreenRows;
        private readonly ReadOnlyCollection<int> readOnlyTabStops;
        private readonly ReadOnlyCollection<int> readOnlyPaletteRgb;
        private readonly ReadOnlyCollection<PortableTerminalHyperlinkV1> readOnlyHyperlinks;

        internal PortableTerminalSnapshotV1(
            ulong sequence,
            int columns,
            int rows,
            int scrollTop,
            int scrollBottomExclusive,
            int configuredScrollBottomExclusive,
            int cursorColumn,
            int cursorRow,
            int savedCursorColumn,
            int savedCursorRow,
            bool savedCursorPendingAutoWrap,
            bool savedCursorOriginMode,
            bool savedCursorAutoWrap,
            bool savedCursorLineDrawingG0,
            bool savedCursorLineDrawingG1,
            int savedCursorCharsetGl,
            bool cursorVisible,
            PortableTerminalCursorStyleV1 cursorStyle,
            bool cursorBlink,
            bool pendingAutoWrap,
            bool autoWrap,
            bool originMode,
            bool insertMode,
            bool bracketedPaste,
            bool focusReporting,
            bool lineDrawingG0,
            bool lineDrawingG1,
            int charsetGl,
            PortableTerminalStyleV1 currentStyle,
            PortableTerminalStyleV1 savedCursorStyle,
            bool activeAlternateScreen,
            int alternateScrollTop,
            int alternateScrollBottomExclusive,
            int alternateCursorColumn,
            int alternateCursorRow,
            int alternateSavedCursorColumn,
            int alternateSavedCursorRow,
            bool alternateSavedCursorPendingAutoWrap,
            bool alternateSavedCursorOriginMode,
            bool alternateSavedCursorAutoWrap,
            bool alternateSavedCursorLineDrawingG0,
            bool alternateSavedCursorLineDrawingG1,
            int alternateSavedCursorCharsetGl,
            bool alternatePendingAutoWrap,
            PortableTerminalStyleV1 alternateCurrentStyle,
            PortableTerminalStyleV1 alternateSavedCursorStyle,
            string title,
            IEnumerable<int> paletteRgb,
            int defaultForegroundRgb,
            int defaultBackgroundRgb,
            int defaultCursorRgb,
            IEnumerable<PortableTerminalHyperlinkV1> hyperlinks,
            int tabStopCapacity,
            IEnumerable<int> tabStops,
            bool defaultTabStopsEnabled,
            IEnumerable<PortableTerminalRowV1> scrollbackRows,
            IEnumerable<PortableTerminalRowV1> screenRows,
            IEnumerable<PortableTerminalRowV1> alternateScreenRows,
            ulong nextLineId)
        {
            if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            ValidateSize(columns, rows);
            if (scrollTop < 0 || scrollTop >= scrollBottomExclusive || scrollBottomExclusive > rows)
                throw new ArgumentOutOfRangeException(nameof(scrollTop));
            if (configuredScrollBottomExclusive <= scrollTop || configuredScrollBottomExclusive > 1000)
                throw new ArgumentOutOfRangeException(nameof(configuredScrollBottomExclusive));
            if (scrollBottomExclusive != Math.Min(configuredScrollBottomExclusive, rows))
                throw new ArgumentException("The active scroll bottom must match the configured bottom clamped to the viewport.",
                    nameof(scrollBottomExclusive));
            if (tabStopCapacity < columns || tabStopCapacity > 1000)
                throw new ArgumentOutOfRangeException(nameof(tabStopCapacity));
            if (cursorColumn < 0 || cursorColumn >= columns) throw new ArgumentOutOfRangeException(nameof(cursorColumn));
            if (cursorRow < 0 || cursorRow >= rows) throw new ArgumentOutOfRangeException(nameof(cursorRow));
            if (savedCursorColumn < 0 || savedCursorColumn >= columns)
                throw new ArgumentOutOfRangeException(nameof(savedCursorColumn));
            if (savedCursorRow < 0 || savedCursorRow >= rows)
                throw new ArgumentOutOfRangeException(nameof(savedCursorRow));
            if (alternateScrollTop < 0 || alternateScrollTop >= alternateScrollBottomExclusive ||
                alternateScrollBottomExclusive > rows)
                throw new ArgumentOutOfRangeException(nameof(alternateScrollTop));
            if (alternateCursorColumn < 0 || alternateCursorColumn >= columns ||
                alternateSavedCursorColumn < 0 || alternateSavedCursorColumn >= columns)
                throw new ArgumentOutOfRangeException(nameof(alternateCursorColumn));
            if (alternateCursorRow < 0 || alternateCursorRow >= rows ||
                alternateSavedCursorRow < 0 || alternateSavedCursorRow >= rows)
                throw new ArgumentOutOfRangeException(nameof(alternateCursorRow));
            if (nextLineId == 0) throw new ArgumentOutOfRangeException(nameof(nextLineId));
            Sequence = sequence;
            Columns = columns;
            Rows = rows;
            ScrollTop = scrollTop;
            ScrollBottomExclusive = scrollBottomExclusive;
            ConfiguredScrollBottomExclusive = configuredScrollBottomExclusive;
            CursorColumn = cursorColumn;
            CursorRow = cursorRow;
            SavedCursorColumn = savedCursorColumn;
            SavedCursorRow = savedCursorRow;
            SavedCursorPendingAutoWrap = savedCursorPendingAutoWrap;
            SavedCursorOriginMode = savedCursorOriginMode;
            SavedCursorAutoWrap = savedCursorAutoWrap;
            SavedCursorLineDrawingG0 = savedCursorLineDrawingG0;
            SavedCursorLineDrawingG1 = savedCursorLineDrawingG1;
            if (savedCursorCharsetGl < 0 || savedCursorCharsetGl > 1)
                throw new ArgumentOutOfRangeException(nameof(savedCursorCharsetGl));
            SavedCursorCharsetGl = savedCursorCharsetGl;
            CursorVisible = cursorVisible;
            CursorStyle = cursorStyle;
            CursorBlink = cursorBlink;
            PendingAutoWrap = pendingAutoWrap;
            AutoWrap = autoWrap;
            OriginMode = originMode;
            InsertMode = insertMode;
            BracketedPaste = bracketedPaste;
            FocusReporting = focusReporting;
            LineDrawingG0 = lineDrawingG0;
            LineDrawingG1 = lineDrawingG1;
            if (charsetGl < 0 || charsetGl > 1) throw new ArgumentOutOfRangeException(nameof(charsetGl));
            CharsetGl = charsetGl;
            CurrentStyle = currentStyle;
            SavedCursorStyle = savedCursorStyle;
            ActiveAlternateScreen = activeAlternateScreen;
            AlternateScrollTop = alternateScrollTop;
            AlternateScrollBottomExclusive = alternateScrollBottomExclusive;
            AlternateCursorColumn = alternateCursorColumn;
            AlternateCursorRow = alternateCursorRow;
            AlternateSavedCursorColumn = alternateSavedCursorColumn;
            AlternateSavedCursorRow = alternateSavedCursorRow;
            AlternateSavedCursorPendingAutoWrap = alternateSavedCursorPendingAutoWrap;
            AlternateSavedCursorOriginMode = alternateSavedCursorOriginMode;
            AlternateSavedCursorAutoWrap = alternateSavedCursorAutoWrap;
            AlternateSavedCursorLineDrawingG0 = alternateSavedCursorLineDrawingG0;
            AlternateSavedCursorLineDrawingG1 = alternateSavedCursorLineDrawingG1;
            if (alternateSavedCursorCharsetGl < 0 || alternateSavedCursorCharsetGl > 1)
                throw new ArgumentOutOfRangeException(nameof(alternateSavedCursorCharsetGl));
            AlternateSavedCursorCharsetGl = alternateSavedCursorCharsetGl;
            AlternatePendingAutoWrap = alternatePendingAutoWrap;
            AlternateCurrentStyle = alternateCurrentStyle;
            AlternateSavedCursorStyle = alternateSavedCursorStyle;
            Title = title ?? throw new ArgumentNullException(nameof(title));
            DefaultForegroundRgb = ValidateRgb(defaultForegroundRgb, nameof(defaultForegroundRgb));
            DefaultBackgroundRgb = ValidateRgb(defaultBackgroundRgb, nameof(defaultBackgroundRgb));
            DefaultCursorRgb = ValidateRgb(defaultCursorRgb, nameof(defaultCursorRgb));
            NextLineId = nextLineId;
            this.paletteRgb = ClonePalette(paletteRgb);
            this.hyperlinks = CloneHyperlinks(hyperlinks);
            TabStopCapacity = tabStopCapacity;
            this.tabStops = CloneTabStops(tabStops, tabStopCapacity);
            DefaultTabStopsEnabled = defaultTabStopsEnabled;
            this.scrollbackRows = CloneRows(scrollbackRows, columns, null);
            this.screenRows = CloneRows(screenRows, columns, rows);
            this.alternateScreenRows = CloneRows(alternateScreenRows, columns, rows);
            readOnlyTabStops = Array.AsReadOnly(this.tabStops);
            readOnlyPaletteRgb = Array.AsReadOnly(this.paletteRgb);
            readOnlyHyperlinks = Array.AsReadOnly(this.hyperlinks);
            readOnlyScrollbackRows = Array.AsReadOnly(this.scrollbackRows);
            readOnlyScreenRows = Array.AsReadOnly(this.screenRows);
            readOnlyAlternateScreenRows = Array.AsReadOnly(this.alternateScreenRows);
        }

        public string FormatName => CurrentFormatName;
        public uint FormatVersion => CurrentFormatVersion;
        public ulong Sequence { get; }
        public int Columns { get; }
        public int Rows { get; }
        public int ScrollTop { get; }
        public int ScrollBottomExclusive { get; }
        public int ConfiguredScrollBottomExclusive { get; }
        public int CursorColumn { get; }
        public int CursorRow { get; }
        public int SavedCursorColumn { get; }
        public int SavedCursorRow { get; }
        public bool SavedCursorPendingAutoWrap { get; }
        public bool SavedCursorOriginMode { get; }
        public bool SavedCursorAutoWrap { get; }
        public bool SavedCursorLineDrawingG0 { get; }
        public bool SavedCursorLineDrawingG1 { get; }
        public int SavedCursorCharsetGl { get; }
        public bool CursorVisible { get; }
        public PortableTerminalCursorStyleV1 CursorStyle { get; }
        public bool CursorBlink { get; }
        public bool PendingAutoWrap { get; }
        public bool AutoWrap { get; }
        public bool OriginMode { get; }
        public bool InsertMode { get; }
        public bool BracketedPaste { get; }
        public bool FocusReporting { get; }
        public bool LineDrawingG0 { get; }
        public bool LineDrawingG1 { get; }
        public int CharsetGl { get; }
        public PortableTerminalStyleV1 CurrentStyle { get; }
        public PortableTerminalStyleV1 SavedCursorStyle { get; }
        public bool ActiveAlternateScreen { get; }
        public int AlternateScrollTop { get; }
        public int AlternateScrollBottomExclusive { get; }
        public int AlternateCursorColumn { get; }
        public int AlternateCursorRow { get; }
        public int AlternateSavedCursorColumn { get; }
        public int AlternateSavedCursorRow { get; }
        public bool AlternateSavedCursorPendingAutoWrap { get; }
        public bool AlternateSavedCursorOriginMode { get; }
        public bool AlternateSavedCursorAutoWrap { get; }
        public bool AlternateSavedCursorLineDrawingG0 { get; }
        public bool AlternateSavedCursorLineDrawingG1 { get; }
        public int AlternateSavedCursorCharsetGl { get; }
        public bool AlternatePendingAutoWrap { get; }
        public PortableTerminalStyleV1 AlternateCurrentStyle { get; }
        public PortableTerminalStyleV1 AlternateSavedCursorStyle { get; }
        public string Title { get; }
        public int DefaultForegroundRgb { get; }
        public int DefaultBackgroundRgb { get; }
        public int DefaultCursorRgb { get; }
        public ulong NextLineId { get; }
        public bool DefaultTabStopsEnabled { get; }
        public int TabStopCapacity { get; }
        public IReadOnlyList<int> TabStops => readOnlyTabStops;
        public IReadOnlyList<int> PaletteRgb => readOnlyPaletteRgb;
        public IReadOnlyList<PortableTerminalHyperlinkV1> Hyperlinks => readOnlyHyperlinks;
        public IReadOnlyList<PortableTerminalRowV1> ScrollbackRows => readOnlyScrollbackRows;
        public IReadOnlyList<PortableTerminalRowV1> ScreenRows => readOnlyScreenRows;
        public IReadOnlyList<PortableTerminalRowV1> AlternateScreenRows => readOnlyAlternateScreenRows;

        internal PortableTerminalRowV1[] CloneScrollbackRows() => CloneRows(scrollbackRows, Columns, null);
        internal PortableTerminalRowV1[] CloneScreenRows() => CloneRows(screenRows, Columns, Rows);
        internal PortableTerminalRowV1[] CloneAlternateScreenRows() =>
            CloneRows(alternateScreenRows, Columns, Rows);

        private static int[] CloneTabStops(IEnumerable<int> values, int tabStopCapacity)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var result = new List<int>();
            var previous = -1;
            foreach (var value in values)
            {
                if (value <= previous || value < 0 || value >= tabStopCapacity)
                    throw new ArgumentException("Terminal tab stops must be ordered and within the supported width.", nameof(values));
                result.Add(value);
                previous = value;
            }
            return result.ToArray();
        }

        private static int[] ClonePalette(IEnumerable<int> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var result = new List<int>(values).ToArray();
            if (result.Length != 256) throw new ArgumentException("Terminal palette must have 256 entries.", nameof(values));
            for (var index = 0; index < result.Length; index++) ValidateRgb(result[index], nameof(values));
            return result;
        }

        private static PortableTerminalHyperlinkV1[] CloneHyperlinks(IEnumerable<PortableTerminalHyperlinkV1> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var result = new List<PortableTerminalHyperlinkV1>();
            foreach (var value in values)
            {
                if (value == null) throw new ArgumentException("Terminal hyperlink table contains null.", nameof(values));
                result.Add(new PortableTerminalHyperlinkV1(value.Id, value.Uri, value.Parameters));
            }
            if (result.Count > 256) throw new ArgumentException("Terminal hyperlink table is too large.", nameof(values));
            return result.ToArray();
        }

        private static int ValidateRgb(int value, string name)
        {
            if (value < 0 || value > 0x00ffffff) throw new ArgumentOutOfRangeException(name);
            return value;
        }

        internal static void ValidateSize(int columns, int rows)
        {
            if (columns < 2 || columns > 1000) throw new ArgumentOutOfRangeException(nameof(columns));
            if (rows < 2 || rows > 1000) throw new ArgumentOutOfRangeException(nameof(rows));
        }

        private static PortableTerminalRowV1[] CloneRows(
            IEnumerable<PortableTerminalRowV1> values,
            int columns,
            int? requiredCount)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var result = new List<PortableTerminalRowV1>();
            foreach (var row in values)
            {
                if (row == null) throw new ArgumentException("A terminal snapshot contains a null row.", nameof(values));
                var cells = row.CloneCellArray();
                if (cells.Length != columns) throw new ArgumentException("A terminal snapshot row has the wrong width.", nameof(values));
                result.Add(new PortableTerminalRowV1(cells, row.Wrapped, row.LineId, row.SealedAtSequence));
            }
            if (requiredCount.HasValue && result.Count != requiredCount.Value)
                throw new ArgumentException("A terminal snapshot has the wrong visible row count.", nameof(values));
            return result.ToArray();
        }
    }

    internal sealed class PortableTerminalModelV1
    {
        private const int MaximumCsiBytes = 64;
        private const int MaximumScrollbackRows = 2000;
        private const int MaximumOscCharacters = 8192;

        private enum ParserState : byte
        {
            Ground,
            Escape,
            Csi,
            Osc,
            OscEscape,
            EscapeCharset,
            Dcs,
            DcsEscape
        }

        private readonly List<RowState> scrollback = new List<RowState>();
        private List<RowState> screen = new List<RowState>();
        private List<RowState> inactiveScreen = new List<RowState>();
        private readonly StringBuilder csi = new StringBuilder();
        private readonly StringBuilder osc = new StringBuilder();
        private readonly StringBuilder dcsIntroducer = new StringBuilder();
        private bool dcsFinalSeen;
        private readonly List<PortableTerminalHyperlinkV1> hyperlinks =
            new List<PortableTerminalHyperlinkV1>();
        private readonly byte[] utf8 = new byte[4];
        private ParserState parserState;
        private int utf8ExpectedBytes;
        private int utf8BytesSeen;
        private string permanentRejectionCode;
        private ulong permanentRejectionSequence;
        private int columns;
        private int rows;
        private int scrollTop;
        private int scrollBottomExclusive;
        private int configuredScrollBottomExclusive;
        private int cursorColumn;
        private int cursorRow;
        private int savedCursorColumn;
        private int savedCursorRow;
        private bool savedCursorPendingAutoWrap;
        private bool savedCursorOriginMode;
        private bool savedCursorAutoWrap = true;
        private bool savedCursorLineDrawingG0;
        private bool savedCursorLineDrawingG1;
        private int savedCursorCharsetGl;
        private bool cursorVisible = true;
        private PortableTerminalCursorStyleV1 cursorStyle = PortableTerminalCursorStyleV1.Block;
        private bool cursorBlink = true;
        private bool pendingAutoWrap;
        private bool autoWrap = true;
        private bool originMode;
        private bool insertMode;
        private bool bracketedPaste;
        private bool focusReporting;
        private bool lineDrawingG0;
        private bool lineDrawingG1;
        private int charsetGl;
        private int pendingCharsetSlot;
        private bool synchronizedOutput;
        private PortableTerminalStyleV1 currentStyle = PortableTerminalStyleV1.Default;
        private PortableTerminalStyleV1 savedCursorStyle = PortableTerminalStyleV1.Default;
        private int inactiveCursorColumn;
        private int inactiveCursorRow;
        private int inactiveSavedCursorColumn;
        private int inactiveSavedCursorRow;
        private bool inactiveSavedCursorPendingAutoWrap;
        private bool inactiveSavedCursorOriginMode;
        private bool inactiveSavedCursorAutoWrap = true;
        private bool inactiveSavedCursorLineDrawingG0;
        private bool inactiveSavedCursorLineDrawingG1;
        private int inactiveSavedCursorCharsetGl;
        private bool inactivePendingAutoWrap;
        private PortableTerminalStyleV1 inactiveCurrentStyle = PortableTerminalStyleV1.Default;
        private PortableTerminalStyleV1 inactiveSavedCursorStyle = PortableTerminalStyleV1.Default;
        private bool activeAlternateScreen;
        private bool[] tabStops;
        private bool defaultTabStopsEnabled = true;
        private int[] paletteRgb = CreateDefaultPaletteRgb();
        private int defaultForegroundRgb = 0x00c0c0c0;
        private int defaultBackgroundRgb;
        private int defaultCursorRgb = 0x00c0c0c0;
        private string title = string.Empty;
        private ulong lastSequence;
        private ulong nextLineId = 1;

        private sealed class RowState
        {
            public CellState[] Cells;
            public bool Wrapped;
            public ulong LineId;
            public ulong SealedAtSequence;
        }

        private struct CellState
        {
            public string Character;
            public PortableTerminalStyleV1 Style;
            public byte DisplayWidth;
            public bool WideContinuation;
        }

        public PortableTerminalModelV1(int columns, int rows)
        {
            PortableTerminalSnapshotV1.ValidateSize(columns, rows);
            this.columns = columns;
            this.rows = rows;
            scrollBottomExclusive = rows;
            configuredScrollBottomExclusive = rows;
            tabStops = CreateDefaultTabStops(columns);
            ReplaceWithBlankScreen();
            ReplaceInactiveWithBlankScreen(true);
        }

        public int Columns => columns;
        public int Rows => rows;
        public ulong LastSequence => lastSequence;
        public string PermanentRejectionCode => permanentRejectionCode ?? string.Empty;
        public ulong PermanentRejectionSequence => permanentRejectionSequence;
        public string ParserBoundaryCode
        {
            get
            {
                if (permanentRejectionCode != null) return "PERMANENTLY_INELIGIBLE";
                if (utf8ExpectedBytes > 0) return "INCOMPLETE_UTF8";
                if (parserState == ParserState.Escape) return "INCOMPLETE_ESCAPE";
                if (parserState == ParserState.Csi) return "INCOMPLETE_CSI";
                if (parserState == ParserState.EscapeCharset) return "INCOMPLETE_ESCAPE";
                if (parserState == ParserState.Osc || parserState == ParserState.OscEscape)
                    return "INCOMPLETE_OSC";
                if (parserState == ParserState.Dcs || parserState == ParserState.DcsEscape)
                    return "INCOMPLETE_DCS";
                if (synchronizedOutput) return "SYNCHRONIZED_OUTPUT_ACTIVE";
                return "GROUND";
            }
        }

        public bool SnapshotEligible =>
            permanentRejectionCode == null && parserState == ParserState.Ground &&
            utf8ExpectedBytes == 0 && !synchronizedOutput;

        public void AdvanceNonTerminalEvent(ulong sequence)
        {
            if (!BeginEvent(sequence)) return;
            lastSequence = sequence;
        }

        public void ApplyTitle(ulong sequence, string value)
        {
            if (!BeginEvent(sequence)) return;
            title = value ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(title) > 4096) Reject("TITLE_TOO_LONG", sequence);
            lastSequence = sequence;
        }

        public void MarkSourceGap(ulong sequence)
        {
            if (!BeginEvent(sequence)) return;
            Reject("SOURCE_GAP", sequence);
            lastSequence = sequence;
        }

        public void ApplyResize(ulong sequence, int newColumns, int newRows)
        {
            if (!BeginEvent(sequence)) return;
            try
            {
                PortableTerminalSnapshotV1.ValidateSize(newColumns, newRows);
            }
            catch (ArgumentOutOfRangeException)
            {
                Reject("RESIZE_DIMENSIONS_UNSUPPORTED", sequence);
                lastSequence = sequence;
                return;
            }

            if (newColumns == columns && newRows == rows)
            {
                lastSequence = sequence;
                return;
            }
            // Windows Terminal resizes the active alternate buffer traditionally and defers the
            // main-buffer reflow until it returns to the main screen. V1 has no deferred-resize
            // state, so approximating both buffers here would make a later suffix diverge.
            if (activeAlternateScreen)
            {
                Reject("ACTIVE_ALTERNATE_RESIZE_UNSUPPORTED", sequence);
                lastSequence = sequence;
                return;
            }
            var oldRows = rows;
            var oldScrollTop = scrollTop;
            var oldConfiguredScrollBottomExclusive = configuredScrollBottomExclusive;
            var hadFullHeightMargins = oldScrollTop == 0 &&
                oldConfiguredScrollBottomExclusive == oldRows;
            var restoreAlternate = activeAlternateScreen;
            if (restoreAlternate)
            {
                SwapActiveScreenState();
                activeAlternateScreen = false;
            }
            var primarySource = new List<RowState>(scrollback.Count + screen.Count);
            primarySource.AddRange(scrollback);
            primarySource.AddRange(screen);
            var primaryCursorSourceRow = scrollback.Count + cursorRow;
            var primary = ReflowRows(
                primarySource,
                primaryCursorSourceRow,
                cursorColumn,
                newColumns,
                true);
            var alternate = ReflowRows(
                inactiveScreen,
                inactiveCursorRow,
                inactiveCursorColumn,
                newColumns,
                false);

            var primaryViewportStart = PrimaryViewportStart(primary, newRows);
            if (primaryViewportStart > MaximumScrollbackRows)
            {
                if (restoreAlternate)
                {
                    SwapActiveScreenState();
                    activeAlternateScreen = true;
                }
                Reject("SCROLLBACK_LIMIT_EXCEEDED", sequence);
                lastSequence = sequence;
                return;
            }

            columns = newColumns;
            rows = newRows;
            InstallReflowedPrimary(primary, newRows, primaryViewportStart, sequence);
            InstallReflowedAlternate(alternate, newRows);
            if (hadFullHeightMargins || oldScrollTop >= rows - 1)
            {
                scrollTop = 0;
                scrollBottomExclusive = rows;
                configuredScrollBottomExclusive = rows;
            }
            else
            {
                scrollTop = oldScrollTop;
                configuredScrollBottomExclusive = oldConfiguredScrollBottomExclusive;
                scrollBottomExclusive = Math.Min(configuredScrollBottomExclusive, rows);
            }
            inactiveSavedCursorColumn = Math.Min(inactiveSavedCursorColumn, columns - 1);
            inactiveSavedCursorRow = Math.Min(inactiveSavedCursorRow, rows - 1);
            savedCursorColumn = Math.Min(savedCursorColumn, columns - 1);
            savedCursorRow = Math.Min(savedCursorRow, rows - 1);
            pendingAutoWrap = false;
            inactivePendingAutoWrap = false;
            ResizeTabStops(newColumns);
            if (restoreAlternate)
            {
                SwapActiveScreenState();
                activeAlternateScreen = true;
            }
            lastSequence = sequence;
        }

        public void ApplyOutput(ulong sequence, ReadOnlySpan<byte> output)
        {
            if (!BeginEvent(sequence)) return;
            for (var index = 0; index < output.Length && permanentRejectionCode == null; index++)
                ProcessByte(output[index], sequence);
            lastSequence = sequence;
        }

        public bool TryCreateSnapshot(out PortableTerminalSnapshotV1 snapshot)
        {
            snapshot = null;
            if (!SnapshotEligible || lastSequence == 0) return false;
            var primaryRows = activeAlternateScreen ? inactiveScreen : screen;
            var alternateRows = activeAlternateScreen ? screen : inactiveScreen;
            var primaryCursorColumn = activeAlternateScreen ? inactiveCursorColumn : cursorColumn;
            var primaryCursorRow = activeAlternateScreen ? inactiveCursorRow : cursorRow;
            var primarySavedColumn = activeAlternateScreen ? inactiveSavedCursorColumn : savedCursorColumn;
            var primarySavedRow = activeAlternateScreen ? inactiveSavedCursorRow : savedCursorRow;
            var primaryPendingWrap = activeAlternateScreen ? inactivePendingAutoWrap : pendingAutoWrap;
            var primaryStyle = activeAlternateScreen ? inactiveCurrentStyle : currentStyle;
            var primarySavedStyle = activeAlternateScreen ? inactiveSavedCursorStyle : savedCursorStyle;
            var primarySavedPendingWrap = activeAlternateScreen
                ? inactiveSavedCursorPendingAutoWrap : savedCursorPendingAutoWrap;
            var primarySavedOrigin = activeAlternateScreen
                ? inactiveSavedCursorOriginMode : savedCursorOriginMode;
            var primarySavedAutoWrap = activeAlternateScreen
                ? inactiveSavedCursorAutoWrap : savedCursorAutoWrap;
            var primarySavedG0 = activeAlternateScreen
                ? inactiveSavedCursorLineDrawingG0 : savedCursorLineDrawingG0;
            var primarySavedG1 = activeAlternateScreen
                ? inactiveSavedCursorLineDrawingG1 : savedCursorLineDrawingG1;
            var primarySavedGl = activeAlternateScreen
                ? inactiveSavedCursorCharsetGl : savedCursorCharsetGl;
            var alternateCursorColumn = activeAlternateScreen ? cursorColumn : inactiveCursorColumn;
            var alternateCursorRow = activeAlternateScreen ? cursorRow : inactiveCursorRow;
            var alternateSavedColumn = activeAlternateScreen ? savedCursorColumn : inactiveSavedCursorColumn;
            var alternateSavedRow = activeAlternateScreen ? savedCursorRow : inactiveSavedCursorRow;
            var alternatePendingWrap = activeAlternateScreen ? pendingAutoWrap : inactivePendingAutoWrap;
            var alternateStyle = activeAlternateScreen ? currentStyle : inactiveCurrentStyle;
            var alternateSavedStyle = activeAlternateScreen ? savedCursorStyle : inactiveSavedCursorStyle;
            var alternateSavedPendingWrap = activeAlternateScreen
                ? savedCursorPendingAutoWrap : inactiveSavedCursorPendingAutoWrap;
            var alternateSavedOrigin = activeAlternateScreen
                ? savedCursorOriginMode : inactiveSavedCursorOriginMode;
            var alternateSavedAutoWrap = activeAlternateScreen
                ? savedCursorAutoWrap : inactiveSavedCursorAutoWrap;
            var alternateSavedG0 = activeAlternateScreen
                ? savedCursorLineDrawingG0 : inactiveSavedCursorLineDrawingG0;
            var alternateSavedG1 = activeAlternateScreen
                ? savedCursorLineDrawingG1 : inactiveSavedCursorLineDrawingG1;
            var alternateSavedGl = activeAlternateScreen
                ? savedCursorCharsetGl : inactiveSavedCursorCharsetGl;
            snapshot = new PortableTerminalSnapshotV1(
                lastSequence,
                columns,
                rows,
                scrollTop,
                scrollBottomExclusive,
                configuredScrollBottomExclusive,
                primaryCursorColumn,
                primaryCursorRow,
                primarySavedColumn,
                primarySavedRow,
                primarySavedPendingWrap,
                primarySavedOrigin,
                primarySavedAutoWrap,
                primarySavedG0,
                primarySavedG1,
                primarySavedGl,
                cursorVisible,
                cursorStyle,
                cursorBlink,
                primaryPendingWrap,
                autoWrap,
                originMode,
                insertMode,
                bracketedPaste,
                focusReporting,
                lineDrawingG0,
                lineDrawingG1,
                charsetGl,
                primaryStyle,
                primarySavedStyle,
                activeAlternateScreen,
                scrollTop,
                scrollBottomExclusive,
                alternateCursorColumn,
                alternateCursorRow,
                alternateSavedColumn,
                alternateSavedRow,
                alternateSavedPendingWrap,
                alternateSavedOrigin,
                alternateSavedAutoWrap,
                alternateSavedG0,
                alternateSavedG1,
                alternateSavedGl,
                alternatePendingWrap,
                alternateStyle,
                alternateSavedStyle,
                title,
                paletteRgb,
                defaultForegroundRgb,
                defaultBackgroundRgb,
                defaultCursorRgb,
                hyperlinks,
                tabStops.Length,
                EnumerateTabStops(),
                defaultTabStopsEnabled,
                SnapshotRows(scrollback),
                SnapshotRows(primaryRows),
                SnapshotRows(alternateRows),
                nextLineId);
            return true;
        }

        public static PortableTerminalModelV1 Restore(PortableTerminalSnapshotV1 snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!string.Equals(snapshot.FormatName, PortableTerminalSnapshotV1.CurrentFormatName, StringComparison.Ordinal) ||
                snapshot.FormatVersion != PortableTerminalSnapshotV1.CurrentFormatVersion)
            {
                throw new ArgumentException("Unsupported portable terminal snapshot format.", nameof(snapshot));
            }
            if (snapshot.ScrollbackRows.Count > MaximumScrollbackRows)
                throw new ArgumentException("Portable terminal snapshot exceeds the scrollback bound.", nameof(snapshot));

            var model = new PortableTerminalModelV1(snapshot.Columns, snapshot.Rows)
            {
                lastSequence = snapshot.Sequence,
                scrollTop = snapshot.ScrollTop,
                scrollBottomExclusive = snapshot.ScrollBottomExclusive,
                configuredScrollBottomExclusive = snapshot.ConfiguredScrollBottomExclusive,
                cursorColumn = snapshot.CursorColumn,
                cursorRow = snapshot.CursorRow,
                savedCursorColumn = snapshot.SavedCursorColumn,
                savedCursorRow = snapshot.SavedCursorRow,
                savedCursorPendingAutoWrap = snapshot.SavedCursorPendingAutoWrap,
                savedCursorOriginMode = snapshot.SavedCursorOriginMode,
                savedCursorAutoWrap = snapshot.SavedCursorAutoWrap,
                savedCursorLineDrawingG0 = snapshot.SavedCursorLineDrawingG0,
                savedCursorLineDrawingG1 = snapshot.SavedCursorLineDrawingG1,
                savedCursorCharsetGl = snapshot.SavedCursorCharsetGl,
                cursorVisible = snapshot.CursorVisible,
                cursorStyle = snapshot.CursorStyle,
                cursorBlink = snapshot.CursorBlink,
                pendingAutoWrap = snapshot.PendingAutoWrap,
                autoWrap = snapshot.AutoWrap,
                originMode = snapshot.OriginMode,
                insertMode = snapshot.InsertMode,
                bracketedPaste = snapshot.BracketedPaste,
                focusReporting = snapshot.FocusReporting,
                lineDrawingG0 = snapshot.LineDrawingG0,
                lineDrawingG1 = snapshot.LineDrawingG1,
                charsetGl = snapshot.CharsetGl,
                currentStyle = snapshot.CurrentStyle,
                savedCursorStyle = snapshot.SavedCursorStyle,
                title = snapshot.Title,
                paletteRgb = new List<int>(snapshot.PaletteRgb).ToArray(),
                defaultForegroundRgb = snapshot.DefaultForegroundRgb,
                defaultBackgroundRgb = snapshot.DefaultBackgroundRgb,
                defaultCursorRgb = snapshot.DefaultCursorRgb,
                nextLineId = snapshot.NextLineId
            };
            foreach (var link in snapshot.Hyperlinks)
                model.hyperlinks.Add(new PortableTerminalHyperlinkV1(link.Id, link.Uri, link.Parameters));
            model.tabStops = new bool[snapshot.TabStopCapacity];
            foreach (var stop in snapshot.TabStops) model.tabStops[stop] = true;
            model.defaultTabStopsEnabled = snapshot.DefaultTabStopsEnabled;
            model.scrollback.Clear();
            foreach (var row in snapshot.CloneScrollbackRows()) model.scrollback.Add(ToState(row));
            model.screen.Clear();
            foreach (var row in snapshot.CloneScreenRows()) model.screen.Add(ToState(row));
            model.inactiveScreen.Clear();
            foreach (var row in snapshot.CloneAlternateScreenRows()) model.inactiveScreen.Add(ToState(row));
            model.inactiveCursorColumn = snapshot.AlternateCursorColumn;
            model.inactiveCursorRow = snapshot.AlternateCursorRow;
            model.inactiveSavedCursorColumn = snapshot.AlternateSavedCursorColumn;
            model.inactiveSavedCursorRow = snapshot.AlternateSavedCursorRow;
            model.inactiveSavedCursorPendingAutoWrap = snapshot.AlternateSavedCursorPendingAutoWrap;
            model.inactiveSavedCursorOriginMode = snapshot.AlternateSavedCursorOriginMode;
            model.inactiveSavedCursorAutoWrap = snapshot.AlternateSavedCursorAutoWrap;
            model.inactiveSavedCursorLineDrawingG0 = snapshot.AlternateSavedCursorLineDrawingG0;
            model.inactiveSavedCursorLineDrawingG1 = snapshot.AlternateSavedCursorLineDrawingG1;
            model.inactiveSavedCursorCharsetGl = snapshot.AlternateSavedCursorCharsetGl;
            model.inactivePendingAutoWrap = snapshot.AlternatePendingAutoWrap;
            model.inactiveCurrentStyle = snapshot.AlternateCurrentStyle;
            model.inactiveSavedCursorStyle = snapshot.AlternateSavedCursorStyle;
            if (snapshot.ActiveAlternateScreen)
            {
                model.SwapActiveScreenState();
                model.activeAlternateScreen = true;
            }
            return model;
        }

        internal string ValidationFingerprint()
        {
            var value = new StringBuilder();
            value.Append(lastSequence).Append('|').Append(columns).Append('x').Append(rows).Append('|')
                .Append(scrollTop).Append('-').Append(scrollBottomExclusive).Append('/')
                .Append(configuredScrollBottomExclusive).Append('|')
                .Append(cursorColumn).Append(',').Append(cursorRow).Append('|')
                .Append(savedCursorColumn).Append(',').Append(savedCursorRow).Append(',')
                .Append(savedCursorStyle.GetHashCode()).Append(',')
                .Append(savedCursorPendingAutoWrap ? '1' : '0').Append(',')
                .Append(savedCursorOriginMode ? '1' : '0').Append(',')
                .Append(savedCursorAutoWrap ? '1' : '0').Append(',')
                .Append(savedCursorLineDrawingG0 ? '1' : '0').Append(',')
                .Append(savedCursorLineDrawingG1 ? '1' : '0').Append(',')
                .Append(savedCursorCharsetGl).Append('|')
                .Append(cursorVisible ? '1' : '0').Append(',').Append((int)cursorStyle).Append(',')
                .Append(cursorBlink ? '1' : '0').Append('|').Append(pendingAutoWrap ? '1' : '0').Append('|')
                .Append(autoWrap ? '1' : '0').Append(',').Append(originMode ? '1' : '0').Append(',')
                .Append(insertMode ? '1' : '0').Append('|')
                .Append(bracketedPaste ? '1' : '0').Append(',').Append(focusReporting ? '1' : '0').Append(',')
                .Append(synchronizedOutput ? '1' : '0').Append('|')
                .Append(lineDrawingG0 ? '1' : '0').Append(',').Append(lineDrawingG1 ? '1' : '0')
                .Append(',').Append(charsetGl).Append('|')
                .Append((int)currentStyle.Foreground).Append(',').Append((int)currentStyle.Background).Append(',')
                .Append((ushort)currentStyle.Attributes).Append(',')
                .Append(currentStyle.HyperlinkIndexPlusOne).Append('|')
                .Append(title.Length).Append(':').Append(title).Append('|')
                .Append(defaultForegroundRgb).Append(',').Append(defaultBackgroundRgb).Append(',')
                .Append(defaultCursorRgb).Append('|');
            foreach (var color in paletteRgb) value.Append(color).Append(',');
            value.Append('|');
            foreach (var link in hyperlinks)
                value.Append(link.Id.Length).Append(':').Append(link.Id).Append(';')
                    .Append(link.Uri.Length).Append(':').Append(link.Uri).Append(';')
                    .Append(link.Parameters.Length).Append(':').Append(link.Parameters).Append('|');
            value
                .Append(ParserBoundaryCode).Append('|').Append(PermanentRejectionCode).Append('@')
                .Append(permanentRejectionSequence).Append('|');
            value.Append(defaultTabStopsEnabled ? '1' : '0').Append(':')
                .Append(tabStops.Length).Append(':');
            foreach (var stop in EnumerateTabStops()) value.Append(stop).Append(',');
            value.Append('|').Append(activeAlternateScreen ? 'A' : 'P').Append('|')
                .Append(inactiveCursorColumn).Append(',').Append(inactiveCursorRow).Append('|')
                .Append(inactiveSavedCursorColumn).Append(',').Append(inactiveSavedCursorRow).Append(',')
                .Append(inactiveSavedCursorPendingAutoWrap ? '1' : '0').Append(',')
                .Append(inactiveSavedCursorOriginMode ? '1' : '0').Append(',')
                .Append(inactiveSavedCursorAutoWrap ? '1' : '0').Append(',')
                .Append(inactiveSavedCursorLineDrawingG0 ? '1' : '0').Append(',')
                .Append(inactiveSavedCursorLineDrawingG1 ? '1' : '0').Append(',')
                .Append(inactiveSavedCursorCharsetGl).Append('|')
                .Append(inactivePendingAutoWrap ? '1' : '0').Append('|')
                .Append(inactiveCurrentStyle.GetHashCode()).Append(',')
                .Append(inactiveSavedCursorStyle.GetHashCode()).Append('|');
            AppendRows(value, scrollback);
            value.Append("||");
            AppendRows(value, screen);
            value.Append("||");
            AppendRows(value, inactiveScreen);
            return value.ToString();
        }

        private bool BeginEvent(ulong sequence)
        {
            if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            if (lastSequence == ulong.MaxValue) throw new InvalidOperationException("Terminal event sequence is exhausted.");
            if (sequence != lastSequence + 1)
            {
                if (permanentRejectionCode == null) Reject("SEQUENCE_NOT_CONTIGUOUS", sequence);
                lastSequence = sequence;
                return false;
            }
            if (permanentRejectionCode != null)
            {
                lastSequence = sequence;
                return false;
            }
            return true;
        }

        private void ProcessByte(byte value, ulong sequence)
        {
            if (utf8ExpectedBytes > 0)
            {
                if ((value & 0xC0) != 0x80)
                {
                    Reject("INVALID_UTF8", sequence);
                    return;
                }
                utf8[utf8BytesSeen++] = value;
                if (utf8BytesSeen == utf8ExpectedBytes)
                {
                    if (!TryDecodeUtf8Scalar(utf8, utf8ExpectedBytes, out var scalar))
                        Reject("INVALID_UTF8", sequence);
                    else
                        ProcessUnicodeScalar(scalar, sequence);
                    utf8ExpectedBytes = 0;
                    utf8BytesSeen = 0;
                }
                return;
            }

            if (value >= 0x80)
            {
                var expected = Utf8Length(value);
                if (expected == 0)
                {
                    Reject("INVALID_UTF8", sequence);
                    return;
                }
                utf8[0] = value;
                utf8ExpectedBytes = expected;
                utf8BytesSeen = 1;
                return;
            }

            // CAN and SUB cancel any in-progress escape or control string from any parser state.
            if (value == 0x18 || value == 0x1A)
            {
                CancelControlSequence();
                return;
            }

            // ESC terminates DCS immediately and starts a fresh escape sequence. OSC is the
            // exception because it needs one byte of lookahead to recognize ST (ESC \).
            if (value == 0x1B && (parserState == ParserState.Dcs || parserState == ParserState.DcsEscape))
            {
                dcsIntroducer.Clear();
                dcsFinalSeen = false;
                parserState = ParserState.Escape;
                return;
            }

            if (parserState == ParserState.Escape)
            {
                ProcessEscape(value, sequence);
                return;
            }
            if (parserState == ParserState.Csi)
            {
                ProcessCsi(value, sequence);
                return;
            }
            if (parserState == ParserState.EscapeCharset)
            {
                ProcessCharsetDesignation(value, sequence);
                return;
            }
            if (parserState == ParserState.Osc)
            {
                ProcessOsc(value);
                return;
            }
            if (parserState == ParserState.OscEscape)
            {
                ProcessOscEscape(value);
                return;
            }
            if (parserState == ParserState.Dcs)
            {
                ProcessDcs(value, sequence);
                return;
            }
            if (parserState == ParserState.DcsEscape)
            {
                ProcessDcsEscape(value, sequence);
                return;
            }

            switch (value)
            {
                case 0x00: // NUL has no terminal model state.
                case 0x07: // BEL is transient and has no restorable state.
                    return;
                case 0x08:
                    Backspace();
                    return;
                case 0x09:
                    HorizontalTab();
                    return;
                case 0x0A:
                case 0x0B:
                case 0x0C:
                    LineFeed(sequence);
                    return;
                case 0x0D:
                    cursorColumn = 0;
                    pendingAutoWrap = false;
                    return;
                case 0x0E:
                    charsetGl = 1;
                    return;
                case 0x0F:
                    charsetGl = 0;
                    return;
                case 0x1B:
                    parserState = ParserState.Escape;
                    return;
                default:
                    if (value >= 0x20 && value <= 0x7E)
                    {
                        WriteCharacter((char)value, sequence);
                        return;
                    }
                    Reject("C0_CONTROL_UNSUPPORTED", sequence);
                    return;
            }
        }

        private void ProcessEscape(byte value, ulong sequence)
        {
            if (value == 0x1B)
            {
                parserState = ParserState.Escape;
                return;
            }
            if (value == (byte)'[')
            {
                parserState = ParserState.Csi;
                csi.Clear();
                return;
            }
            if (value == (byte)'(' || value == (byte)')')
            {
                pendingCharsetSlot = value == (byte)'(' ? 0 : 1;
                parserState = ParserState.EscapeCharset;
                return;
            }
            if (value == (byte)'c')
            {
                ResetTerminal();
                parserState = ParserState.Ground;
                return;
            }
            if (value == (byte)'\\')
            {
                // ST after a DCS interrupt is an inert escape dispatch.
                parserState = ParserState.Ground;
                return;
            }
            if (value == (byte)'7')
            {
                SaveCursor();
                parserState = ParserState.Ground;
            }
            else if (value == (byte)'8')
            {
                RestoreCursor();
                parserState = ParserState.Ground;
            }
            else if (value == (byte)'H')
            {
                tabStops[cursorColumn] = true;
                parserState = ParserState.Ground;
            }
            else if (value == (byte)'D')
            {
                LineFeed(sequence);
                parserState = ParserState.Ground;
            }
            else if (value == (byte)'E')
            {
                cursorColumn = 0;
                LineFeed(sequence);
                parserState = ParserState.Ground;
            }
            else if (value == (byte)'M')
            {
                ReverseIndex();
                parserState = ParserState.Ground;
            }
            else if (value == (byte)']')
            {
                osc.Clear();
                parserState = ParserState.Osc;
            }
            else if (value == (byte)'P')
            {
                dcsIntroducer.Clear();
                dcsFinalSeen = false;
                parserState = ParserState.Dcs;
            }
            else if (value == (byte)'_' || value == (byte)'^' || value == (byte)'X')
                Reject("CONTROL_STRING_UNSUPPORTED", sequence);
            else Reject("ESC_SEQUENCE_UNSUPPORTED", sequence);
        }

        private void ProcessCharsetDesignation(byte value, ulong sequence)
        {
            if (value != (byte)'B' && value != (byte)'0')
            {
                Reject("CHARSET_UNSUPPORTED", sequence);
                return;
            }
            var lineDrawing = value == (byte)'0';
            if (pendingCharsetSlot == 0) lineDrawingG0 = lineDrawing;
            else lineDrawingG1 = lineDrawing;
            parserState = ParserState.Ground;
        }

        private void ProcessDcs(byte value, ulong sequence)
        {
            if (!dcsFinalSeen)
            {
                if (value <= 0x1F || value == 0x7F) return;
                if (dcsIntroducer.Length >= MaximumOscCharacters)
                {
                    Reject("DCS_TOO_LONG", sequence);
                    return;
                }
                dcsIntroducer.Append((char)value);
                if (value >= 0x40 && value <= 0x7E)
                {
                    dcsFinalSeen = true;
                    if (value == (byte)'q' && !DcsIntroducerHasIntermediate())
                    {
                        Reject("DCS_SIXEL_UNSUPPORTED", sequence);
                    }
                    else
                    {
                        // DCS handlers such as DRCS, macros, user-defined keys and status
                        // queries carry state or response semantics that V1 cannot restore.
                        Reject("DCS_UNSUPPORTED", sequence);
                    }
                }
            }
        }

        private void ProcessDcsEscape(byte value, ulong sequence)
        {
            dcsIntroducer.Clear();
            dcsFinalSeen = false;
            parserState = ParserState.Escape;
            ProcessEscape(value, sequence);
        }

        private void ProcessOsc(byte value)
        {
            if (value == 0x07)
            {
                CompleteOsc();
                parserState = ParserState.Ground;
                return;
            }
            if (value == 0x1B)
            {
                parserState = ParserState.OscEscape;
                return;
            }
            if (IsOscInvalid(value)) return;
            AppendOscCharacter((char)value);
        }

        private void ProcessOscEscape(byte value)
        {
            if (value == (byte)'\\')
            {
                CompleteOsc();
                parserState = ParserState.Ground;
                return;
            }
            osc.Clear();
            parserState = ParserState.Escape;
            ProcessEscape(value, lastSequence + 1);
        }

        private bool DcsIntroducerHasIntermediate()
        {
            for (var index = 0; index + 1 < dcsIntroducer.Length; index++)
            {
                var value = dcsIntroducer[index];
                if (value >= 0x20 && value <= 0x2F) return true;
            }
            return false;
        }

        private static bool IsOscInvalid(byte value) =>
            value <= 0x17 || value == 0x19 || (value >= 0x1C && value <= 0x1F);

        private void CancelControlSequence()
        {
            csi.Clear();
            osc.Clear();
            dcsIntroducer.Clear();
            dcsFinalSeen = false;
            parserState = ParserState.Ground;
        }

        private void AppendOscCharacter(char value)
        {
            if (osc.Length >= MaximumOscCharacters)
            {
                Reject("OSC_TOO_LONG", lastSequence + 1);
                return;
            }
            osc.Append(value);
        }

        private void AppendOscText(string value)
        {
            if (osc.Length > MaximumOscCharacters - value.Length)
            {
                Reject("OSC_TOO_LONG", lastSequence + 1);
                return;
            }
            osc.Append(value);
        }

        private void CompleteOsc()
        {
            var text = osc.ToString();
            osc.Clear();
            var separator = text.IndexOf(';');
            var command = separator < 0 ? text : text.Substring(0, separator);
            var payload = separator < 0 ? string.Empty : text.Substring(separator + 1);
            if (!int.TryParse(command, out var code)) return;
            if (code == 0 || code == 2)
            {
                if (Encoding.UTF8.GetByteCount(payload) <= 4096) title = payload;
                return;
            }
            if (code == 4)
            {
                ApplyOscPalette(payload);
                return;
            }
            if (code == 8)
            {
                ApplyOscHyperlink(payload);
                return;
            }
            if (code == 10 || code == 11 || code == 12)
            {
                if (TryParseOscColor(payload, out var color))
                {
                    if (code == 10) defaultForegroundRgb = color;
                    else if (code == 11) defaultBackgroundRgb = color;
                    else defaultCursorRgb = color;
                }
                return;
            }
            if (code == 104) ResetOscPalette(payload);
            else if (code == 110) defaultForegroundRgb = 0x00c0c0c0;
            else if (code == 111) defaultBackgroundRgb = 0;
            else if (code == 112) defaultCursorRgb = 0x00c0c0c0;
        }

        private void ApplyOscPalette(string payload)
        {
            var parts = payload.Split(';');
            for (var index = 0; index + 1 < parts.Length; index += 2)
            {
                if (int.TryParse(parts[index], out var paletteIndex) &&
                    paletteIndex >= 0 && paletteIndex < paletteRgb.Length &&
                    TryParseOscColor(parts[index + 1], out var color))
                    paletteRgb[paletteIndex] = color;
            }
        }

        private void ResetOscPalette(string payload)
        {
            var defaults = CreateDefaultPaletteRgb();
            if (string.IsNullOrEmpty(payload))
            {
                paletteRgb = defaults;
                return;
            }
            foreach (var value in payload.Split(';'))
                if (int.TryParse(value, out var index) && index >= 0 && index < paletteRgb.Length)
                    paletteRgb[index] = defaults[index];
        }

        private void ApplyOscHyperlink(string payload)
        {
            var separator = payload.IndexOf(';');
            if (separator < 0) return;
            var parameters = payload.Substring(0, separator);
            var uri = payload.Substring(separator + 1);
            if (uri.Length == 0)
            {
                currentStyle = currentStyle.WithHyperlink(0);
                return;
            }
            if (Encoding.UTF8.GetByteCount(uri) > 4096 || Encoding.UTF8.GetByteCount(parameters) > 1024)
                return;
            var id = string.Empty;
            foreach (var part in parameters.Split(':'))
                if (part.StartsWith("id=", StringComparison.Ordinal)) id = part.Substring(3);
            var existing = hyperlinks.FindIndex(value => value.Id == id && value.Uri == uri && value.Parameters == parameters);
            if (existing < 0)
            {
                if (hyperlinks.Count >= 256)
                {
                    currentStyle = currentStyle.WithHyperlink(0);
                    Reject("HYPERLINK_LIMIT_EXCEEDED", lastSequence + 1);
                    return;
                }
                hyperlinks.Add(new PortableTerminalHyperlinkV1(id, uri, parameters));
                existing = hyperlinks.Count - 1;
            }
            currentStyle = currentStyle.WithHyperlink(existing + 1);
        }

        private static bool TryParseOscColor(string text, out int rgb)
        {
            rgb = 0;
            if (text.StartsWith("#", StringComparison.Ordinal) && text.Length == 7)
                return int.TryParse(text.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
            if (!text.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase)) return false;
            var components = text.Substring(4).Split('/');
            if (components.Length != 3) return false;
            var values = new int[3];
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component.Length < 1 || component.Length > 4 ||
                    !int.TryParse(component, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    return false;
                var maximum = (1 << (component.Length * 4)) - 1;
                values[index] = (value * 255 + maximum / 2) / maximum;
            }
            rgb = (values[0] << 16) | (values[1] << 8) | values[2];
            return true;
        }

        private void ProcessUnicodeScalar(int scalar, ulong sequence)
        {
            if (parserState == ParserState.Osc || parserState == ParserState.OscEscape)
            {
                if (scalar == 0x009C)
                {
                    CompleteOsc();
                    parserState = ParserState.Ground;
                }
                else
                {
                    if (parserState == ParserState.OscEscape)
                    {
                        osc.Clear();
                        parserState = ParserState.Escape;
                        Reject("ESC_SEQUENCE_UNSUPPORTED", sequence);
                    }
                    else AppendOscText(char.ConvertFromUtf32(scalar));
                }
                return;
            }
            if (parserState == ParserState.Dcs || parserState == ParserState.DcsEscape)
            {
                if (scalar == 0x009C)
                {
                    parserState = ParserState.Ground;
                    dcsIntroducer.Clear();
                    dcsFinalSeen = false;
                }
                else if (parserState == ParserState.DcsEscape)
                    parserState = ParserState.Dcs;
                return;
            }
            if (parserState == ParserState.Escape || parserState == ParserState.EscapeCharset)
            {
                Reject("ESC_SEQUENCE_UNSUPPORTED", sequence);
                return;
            }
            if (parserState == ParserState.Csi)
            {
                Reject("CSI_PARAMETER_OR_INTERMEDIATE_UNSUPPORTED", sequence);
                return;
            }
            if (scalar == 0x009D)
            {
                parserState = ParserState.Osc;
                osc.Clear();
                return;
            }
            if (scalar == 0x0090)
            {
                dcsIntroducer.Clear();
                dcsFinalSeen = false;
                parserState = ParserState.Dcs;
                return;
            }

            var width = UnicodeDisplayWidth(scalar);
            if (width < 0)
            {
                Reject("UNICODE_WIDTH_UNSUPPORTED", sequence);
                return;
            }
            var text = char.ConvertFromUtf32(scalar);
            if (width == 0) AppendCombiningScalar(text, scalar, sequence);
            else WriteGrapheme(text, width, sequence);
        }

        private void ProcessCsi(byte value, ulong sequence)
        {
            if (value >= 0x40 && value <= 0x7E)
            {
                var parameters = csi.ToString();
                csi.Clear();
                parserState = ParserState.Ground;
                ExecuteCsi(parameters, (char)value, sequence);
                return;
            }
            if ((value >= 0x20 && value <= 0x3F))
            {
                if (csi.Length >= MaximumCsiBytes)
                {
                    Reject("CSI_TOO_LONG", sequence);
                    return;
                }
                csi.Append((char)value);
                return;
            }
            Reject("CSI_PARAMETER_OR_INTERMEDIATE_UNSUPPORTED", sequence);
        }

        private void ExecuteCsi(string text, char final, ulong sequence)
        {
            if (text.StartsWith("?", StringComparison.Ordinal))
            {
                if (final == 'h' || final == 'l')
                {
                    if (ApplyPrivateModes(text.Substring(1), final == 'h', sequence)) return;
                }
                Reject("CSI_PRIVATE_MODE_UNSUPPORTED", sequence);
                return;
            }

            if (text.EndsWith(" ", StringComparison.Ordinal))
            {
                if (final == 'q' && TryParseParameters(text.Substring(0, text.Length - 1), out var cursorParameters))
                {
                    ApplyCursorStyle(cursorParameters, sequence);
                    return;
                }
                Reject("CSI_INTERMEDIATE_UNSUPPORTED", sequence);
                return;
            }

            if (text.EndsWith("$", StringComparison.Ordinal))
            {
                if (!TryParseParameters(text.Substring(0, text.Length - 1), out var rectangleParameters))
                {
                    Reject("CSI_PARAMETERS_INVALID", sequence);
                    return;
                }
                ApplyRectangularOperation(rectangleParameters, final, sequence);
                return;
            }

            if (!TryParseParameters(text, out var parameters))
            {
                Reject("CSI_PARAMETERS_INVALID", sequence);
                return;
            }

            switch (final)
            {
                case 'm':
                    ApplySgr(parameters, sequence);
                    return;
                case 'A': MoveCursorRelative(0, -CursorDistance(parameters, sequence), parameters, sequence); return;
                case 'B': MoveCursorRelative(0, CursorDistance(parameters, sequence), parameters, sequence); return;
                case 'C': MoveCursorRelative(CursorDistance(parameters, sequence), 0, parameters, sequence); return;
                case 'D': MoveCursorRelative(-CursorDistance(parameters, sequence), 0, parameters, sequence); return;
                case 'E': MoveCursorLine(CursorDistance(parameters, sequence), parameters, sequence); return;
                case 'F': MoveCursorLine(-CursorDistance(parameters, sequence), parameters, sequence); return;
                case 'G': SetCursorColumn(CursorAbsolute(parameters, sequence) - 1, parameters, sequence); return;
                case 'd': SetCursor(cursorColumn, CursorAbsolute(parameters, sequence) - 1, parameters, sequence); return;
                case 'H':
                case 'f':
                    ApplyCursorPosition(parameters, sequence);
                    return;
                case 'J': ApplyEraseDisplay(parameters, sequence); return;
                case 'K': ApplyEraseLine(parameters, sequence); return;
                case '@': InsertCharacters(parameters, sequence); return;
                case 'P': DeleteCharacters(parameters, sequence); return;
                case 'L': InsertLines(parameters, sequence); return;
                case 'M': DeleteLines(parameters, sequence); return;
                case 'S': ScrollUp(parameters, sequence); return;
                case 'T': ScrollDown(parameters, sequence); return;
                case 'X': EraseCharacters(parameters, sequence); return;
                case 'I': CursorForwardTabs(parameters, sequence); return;
                case 'Z': CursorBackwardTabs(parameters, sequence); return;
                case 'g': ClearTabStops(parameters, sequence); return;
                case 'r': ApplyScrollMargins(parameters, sequence); return;
                case 's': SaveCursor(parameters, sequence); return;
                case 'u': RestoreCursor(parameters, sequence); return;
                case 'h':
                case 'l':
                    if (parameters.Length == 1 && parameters[0] == 4)
                    {
                        insertMode = final == 'h';
                        return;
                    }
                    if (parameters.Length == 1 && parameters[0] == 25)
                    {
                        cursorVisible = final == 'h';
                        return;
                    }
                    Reject("CSI_MODE_UNSUPPORTED", sequence);
                    return;
                default:
                    Reject("CSI_FINAL_UNSUPPORTED", sequence);
                    return;
            }
        }

        private void ApplySgr(int[] parameters, ulong sequence)
        {
            var values = parameters.Length == 0 ? new[] { 0 } : parameters;
            for (var index = 0; index < values.Length; index++)
            {
                var code = values[index] < 0 ? 0 : values[index];
                if (code == 0)
                {
                    currentStyle = currentStyle
                        .WithForeground(PortableTerminalStyleV1.DefaultColor)
                        .WithBackground(PortableTerminalStyleV1.DefaultColor)
                        .WithAttributes(PortableTerminalAttributesV1.None);
                }
                else if (code == 1) AddAttribute(PortableTerminalAttributesV1.Bold);
                else if (code == 2) AddAttribute(PortableTerminalAttributesV1.Dim);
                else if (code == 3) AddAttribute(PortableTerminalAttributesV1.Italic);
                else if (code == 4) AddAttribute(PortableTerminalAttributesV1.Underline);
                else if (code == 5) AddAttribute(PortableTerminalAttributesV1.Blink);
                else if (code == 7) AddAttribute(PortableTerminalAttributesV1.Inverse);
                else if (code == 8) AddAttribute(PortableTerminalAttributesV1.Invisible);
                else if (code == 9) AddAttribute(PortableTerminalAttributesV1.Strikethrough);
                else if (code == 22) RemoveAttributes(PortableTerminalAttributesV1.Bold | PortableTerminalAttributesV1.Dim);
                else if (code == 23) RemoveAttributes(PortableTerminalAttributesV1.Italic);
                else if (code == 24) RemoveAttributes(PortableTerminalAttributesV1.Underline);
                else if (code == 25) RemoveAttributes(PortableTerminalAttributesV1.Blink);
                else if (code == 27) RemoveAttributes(PortableTerminalAttributesV1.Inverse);
                else if (code == 28) RemoveAttributes(PortableTerminalAttributesV1.Invisible);
                else if (code == 29) RemoveAttributes(PortableTerminalAttributesV1.Strikethrough);
                else if (code >= 30 && code <= 37) currentStyle = currentStyle.WithForeground(code - 30);
                else if (code == 39) currentStyle = currentStyle.WithForeground(PortableTerminalStyleV1.DefaultColor);
                else if (code >= 40 && code <= 47) currentStyle = currentStyle.WithBackground(code - 40);
                else if (code == 49) currentStyle = currentStyle.WithBackground(PortableTerminalStyleV1.DefaultColor);
                else if (code >= 90 && code <= 97) currentStyle = currentStyle.WithForeground(code - 90 + 8);
                else if (code >= 100 && code <= 107) currentStyle = currentStyle.WithBackground(code - 100 + 8);
                else if (code == 38 || code == 48)
                {
                    if (!TryReadExtendedColor(values, ref index, out var color))
                    {
                        Reject("SGR_EXTENDED_COLOR_INVALID", sequence);
                        return;
                    }
                    currentStyle = code == 38
                        ? currentStyle.WithForeground(color)
                        : currentStyle.WithBackground(color);
                }
                else
                {
                    Reject("SGR_UNSUPPORTED", sequence);
                    return;
                }
            }
        }

        private bool ApplyPrivateModes(string text, bool enabled, ulong sequence)
        {
            if (!TryParseParameters(text, out var modes) || modes.Length == 0)
            {
                Reject("CSI_PRIVATE_MODE_INVALID", sequence);
                return false;
            }
            foreach (var mode in modes)
            {
                switch (mode)
                {
                    case 6:
                    case 7:
                    case 25:
                    case 47:
                    case 1047:
                    case 1048:
                    case 1049:
                    case 1004:
                    case 2004:
                    case 2026:
                        break;
                    default:
                        return false;
                }
            }
            foreach (var mode in modes)
            {
                if (mode == 6)
                {
                    originMode = enabled;
                    cursorColumn = 0;
                    cursorRow = enabled ? scrollTop : 0;
                    pendingAutoWrap = false;
                }
                else if (mode == 7)
                {
                    autoWrap = enabled;
                    if (!enabled) pendingAutoWrap = false;
                }
                else if (mode == 25) cursorVisible = enabled;
                else if (mode == 47 || mode == 1047) SetAlternateScreen(enabled, false);
                else if (mode == 1048)
                {
                    if (enabled) SaveCursor();
                    else RestoreCursor();
                }
                else if (mode == 1049) SetAlternateScreen(enabled, true);
                else if (mode == 1004) focusReporting = enabled;
                else if (mode == 2004) bracketedPaste = enabled;
                else if (mode == 2026) synchronizedOutput = enabled;
            }
            return true;
        }

        private void ApplyCursorStyle(int[] parameters, ulong sequence)
        {
            if (!TrySingleParameter(parameters, 0, out var style))
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            switch (style)
            {
                case 0:
                case 1:
                    cursorStyle = PortableTerminalCursorStyleV1.Block;
                    cursorBlink = true;
                    break;
                case 2:
                    cursorStyle = PortableTerminalCursorStyleV1.Block;
                    cursorBlink = false;
                    break;
                case 3:
                    cursorStyle = PortableTerminalCursorStyleV1.Underline;
                    cursorBlink = true;
                    break;
                case 4:
                    cursorStyle = PortableTerminalCursorStyleV1.Underline;
                    cursorBlink = false;
                    break;
                case 5:
                    cursorStyle = PortableTerminalCursorStyleV1.Bar;
                    cursorBlink = true;
                    break;
                case 6:
                    cursorStyle = PortableTerminalCursorStyleV1.Bar;
                    cursorBlink = false;
                    break;
                default:
                    Reject("CURSOR_STYLE_UNSUPPORTED", sequence);
                    break;
            }
        }

        private static bool TryReadExtendedColor(int[] values, ref int index, out int color)
        {
            color = PortableTerminalStyleV1.DefaultColor;
            if (index + 1 >= values.Length) return false;
            var mode = values[++index];
            if (mode == 5)
            {
                if (index + 1 >= values.Length || values[index + 1] < 0 || values[index + 1] > 255)
                    return false;
                color = PortableTerminalStyleV1.PaletteColor(values[++index]);
                return true;
            }
            if (mode == 2)
            {
                if (index + 3 >= values.Length) return false;
                var red = values[++index];
                var green = values[++index];
                var blue = values[++index];
                if ((uint)red > 255 || (uint)green > 255 || (uint)blue > 255) return false;
                color = PortableTerminalStyleV1.RgbColor(red, green, blue);
                return true;
            }
            return false;
        }

        private void ApplyCursorPosition(int[] parameters, ulong sequence)
        {
            if (parameters.Length > 2)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            var row = ParameterOrDefault(parameters, 0, 1);
            var column = ParameterOrDefault(parameters, 1, 1);
            SetCursor(column - 1, row - 1, parameters, sequence);
        }

        private void ApplyEraseDisplay(int[] parameters, ulong sequence)
        {
            if (!TrySingleParameter(parameters, 0, out var mode))
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            pendingAutoWrap = false;
            if (mode == 0)
            {
                ClearRange(cursorRow, cursorColumn, columns - 1);
                for (var row = cursorRow + 1; row < rows; row++) ClearRange(row, 0, columns - 1);
            }
            else if (mode == 1)
            {
                for (var row = 0; row < cursorRow; row++) ClearRange(row, 0, columns - 1);
                ClearRange(cursorRow, 0, cursorColumn);
            }
            else if (mode == 2)
            {
                for (var row = 0; row < rows; row++) ClearRange(row, 0, columns - 1);
            }
            else Reject("ERASE_DISPLAY_MODE_UNSUPPORTED", sequence);
        }

        private void ApplyEraseLine(int[] parameters, ulong sequence)
        {
            if (!TrySingleParameter(parameters, 0, out var mode))
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            pendingAutoWrap = false;
            if (mode == 0) ClearRange(cursorRow, cursorColumn, columns - 1);
            else if (mode == 1) ClearRange(cursorRow, 0, cursorColumn);
            else if (mode == 2) ClearRange(cursorRow, 0, columns - 1);
            else Reject("ERASE_LINE_MODE_UNSUPPORTED", sequence);
        }

        private void ApplyScrollMargins(int[] parameters, ulong sequence)
        {
            if (parameters.Length > 2)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            var top = ParameterOrDefault(parameters, 0, 1);
            var bottom = ParameterOrDefault(parameters, 1, rows);
            // DECSTBM ignores an invalid region and leaves the current margins unchanged.
            if (top < 1 || bottom > rows || top >= bottom) return;
            scrollTop = top - 1;
            scrollBottomExclusive = bottom;
            configuredScrollBottomExclusive = bottom;
            cursorColumn = 0;
            cursorRow = originMode ? scrollTop : 0;
            pendingAutoWrap = false;
        }

        private void ApplyRectangularOperation(int[] parameters, char final, ulong sequence)
        {
            if (final == 'x')
            {
                if (parameters.Length < 1 || parameters.Length > 5)
                {
                    Reject("CSI_ARITY_UNSUPPORTED", sequence);
                    return;
                }
                var character = ParameterOrDefault(parameters, 0, 32);
                if (!((character >= 0x20 && character <= 0x7E) ||
                    (character >= 0xA0 && character <= 0xFF)))
                {
                    Reject("RECTANGLE_CHARACTER_UNSUPPORTED", sequence);
                    return;
                }
                var top = ParameterOrDefault(parameters, 1, 1) - 1;
                var left = ParameterOrDefault(parameters, 2, 1) - 1;
                var bottom = ParameterOrDefault(parameters, 3, rows) - 1;
                var right = ParameterOrDefault(parameters, 4, columns) - 1;
                if (!TryClipRectangle(ref top, ref left, ref bottom, ref right)) return;
                var fillCharacter = (char)character;
                if (character <= 0x7E && (charsetGl == 0 ? lineDrawingG0 : lineDrawingG1))
                    fillCharacter = MapDecSpecialGraphics(fillCharacter);
                for (var row = top; row <= bottom; row++)
                {
                    ClearCellOccupant(row, left);
                    ClearCellOccupant(row, right);
                    for (var column = left; column <= right; column++)
                        screen[row].Cells[column] = new CellState
                        {
                            Character = fillCharacter.ToString(),
                            Style = currentStyle,
                            DisplayWidth = 1
                        };
                }
                return;
            }
            if (final == 'z')
            {
                if (!TryRectangle(parameters, 0, sequence, out var top, out var left, out var bottom, out var right))
                    return;
                for (var row = top; row <= bottom; row++) ClearRange(row, left, right);
                return;
            }
            if (final == 'v')
            {
                if (parameters.Length < 8 || parameters.Length > 8)
                {
                    Reject("CSI_ARITY_UNSUPPORTED", sequence);
                    return;
                }
                var sourceTop = ParameterOrDefault(parameters, 0, 1) - 1;
                var sourceLeft = ParameterOrDefault(parameters, 1, 1) - 1;
                var sourceBottom = ParameterOrDefault(parameters, 2, rows) - 1;
                var sourceRight = ParameterOrDefault(parameters, 3, columns) - 1;
                var sourcePage = ParameterOrDefault(parameters, 4, 1);
                var targetTop = ParameterOrDefault(parameters, 5, 1) - 1;
                var targetLeft = ParameterOrDefault(parameters, 6, 1) - 1;
                var targetPage = ParameterOrDefault(parameters, 7, 1);
                if (sourcePage != 1 || targetPage != 1 ||
                    !TryClipRectangle(ref sourceTop, ref sourceLeft, ref sourceBottom, ref sourceRight)) return;
                var targetBottom = targetTop + sourceBottom - sourceTop;
                var targetRight = targetLeft + sourceRight - sourceLeft;
                if (!TryClipRectangle(ref targetTop, ref targetLeft, ref targetBottom, ref targetRight)) return;
                var height = Math.Min(sourceBottom - sourceTop + 1, targetBottom - targetTop + 1);
                var width = Math.Min(sourceRight - sourceLeft + 1, targetRight - targetLeft + 1);
                if (height <= 0 || width <= 0) return;
                var copy = new CellState[height, width];
                for (var row = 0; row < height; row++)
                    for (var column = 0; column < width; column++)
                        copy[row, column] = screen[sourceTop + row].Cells[sourceLeft + column];
                for (var row = 0; row < height; row++)
                {
                    for (var column = 0; column < width; column++)
                        screen[targetTop + row].Cells[targetLeft + column] = copy[row, column];
                    NormalizeWideCells(targetTop + row);
                }
                return;
            }
            Reject("CSI_RECTANGLE_UNSUPPORTED", sequence);
        }

        private bool TryRectangle(
            int[] parameters,
            int offset,
            ulong sequence,
            out int top,
            out int left,
            out int bottom,
            out int right)
        {
            top = left = 0;
            bottom = rows - 1;
            right = columns - 1;
            if (parameters.Length < offset || parameters.Length > offset + 4)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return false;
            }
            top = ParameterOrDefault(parameters, offset, 1) - 1;
            left = ParameterOrDefault(parameters, offset + 1, 1) - 1;
            bottom = ParameterOrDefault(parameters, offset + 2, rows) - 1;
            right = ParameterOrDefault(parameters, offset + 3, columns) - 1;
            return TryClipRectangle(ref top, ref left, ref bottom, ref right);
        }

        private bool TryClipRectangle(ref int top, ref int left, ref int bottom, ref int right)
        {
            var minimumRow = 0;
            var maximumRow = rows - 1;
            if (originMode)
            {
                top += scrollTop;
                bottom += scrollTop;
                minimumRow = scrollTop;
                maximumRow = scrollBottomExclusive - 1;
            }
            top = Math.Max(minimumRow, Math.Min(maximumRow, top));
            bottom = Math.Max(minimumRow, Math.Min(maximumRow, bottom));
            left = Math.Max(0, Math.Min(columns - 1, left));
            right = Math.Max(0, Math.Min(columns - 1, right));
            return top <= bottom && left <= right;
        }

        private void SaveCursor()
        {
            savedCursorColumn = cursorColumn;
            savedCursorRow = originMode ? cursorRow - scrollTop : cursorRow;
            savedCursorPendingAutoWrap = pendingAutoWrap;
            savedCursorOriginMode = originMode;
            savedCursorAutoWrap = autoWrap;
            savedCursorLineDrawingG0 = lineDrawingG0;
            savedCursorLineDrawingG1 = lineDrawingG1;
            savedCursorCharsetGl = charsetGl;
            savedCursorStyle = currentStyle;
        }

        private void RestoreCursor()
        {
            originMode = savedCursorOriginMode;
            cursorColumn = Math.Max(0, Math.Min(columns - 1, savedCursorColumn));
            var restoredRow = savedCursorRow + (originMode ? scrollTop : 0);
            var minimumRow = originMode ? scrollTop : 0;
            var maximumRow = originMode ? scrollBottomExclusive - 1 : rows - 1;
            cursorRow = Math.Max(minimumRow, Math.Min(maximumRow, restoredRow));
            pendingAutoWrap = savedCursorPendingAutoWrap;
            lineDrawingG0 = savedCursorLineDrawingG0;
            lineDrawingG1 = savedCursorLineDrawingG1;
            charsetGl = savedCursorCharsetGl;
            currentStyle = savedCursorStyle;
        }

        private void SetAlternateScreen(bool enabled, bool saveAndRestoreCursor)
        {
            if (enabled)
            {
                if (activeAlternateScreen) return;
                var sourceCursorColumn = cursorColumn;
                var sourceCursorRow = cursorRow;
                var eraseStyle = StandardEraseStyle();
                if (saveAndRestoreCursor) SaveCursor();
                pendingAutoWrap = false;
                SwapActiveScreenState();
                activeAlternateScreen = true;
                ReplaceActiveWithBlankAlternateScreen(eraseStyle);
                cursorColumn = sourceCursorColumn;
                cursorRow = sourceCursorRow;
                pendingAutoWrap = false;
                currentStyle = eraseStyle;
                return;
            }

            if (!activeAlternateScreen) return;
            SwapActiveScreenState();
            activeAlternateScreen = false;
            if (saveAndRestoreCursor) RestoreCursor();
        }

        private void SwapActiveScreenState()
        {
            (screen, inactiveScreen) = (inactiveScreen, screen);
            (cursorColumn, inactiveCursorColumn) = (inactiveCursorColumn, cursorColumn);
            (cursorRow, inactiveCursorRow) = (inactiveCursorRow, cursorRow);
            (savedCursorColumn, inactiveSavedCursorColumn) =
                (inactiveSavedCursorColumn, savedCursorColumn);
            (savedCursorRow, inactiveSavedCursorRow) = (inactiveSavedCursorRow, savedCursorRow);
            (savedCursorPendingAutoWrap, inactiveSavedCursorPendingAutoWrap) =
                (inactiveSavedCursorPendingAutoWrap, savedCursorPendingAutoWrap);
            (savedCursorOriginMode, inactiveSavedCursorOriginMode) =
                (inactiveSavedCursorOriginMode, savedCursorOriginMode);
            (savedCursorAutoWrap, inactiveSavedCursorAutoWrap) =
                (inactiveSavedCursorAutoWrap, savedCursorAutoWrap);
            (savedCursorLineDrawingG0, inactiveSavedCursorLineDrawingG0) =
                (inactiveSavedCursorLineDrawingG0, savedCursorLineDrawingG0);
            (savedCursorLineDrawingG1, inactiveSavedCursorLineDrawingG1) =
                (inactiveSavedCursorLineDrawingG1, savedCursorLineDrawingG1);
            (savedCursorCharsetGl, inactiveSavedCursorCharsetGl) =
                (inactiveSavedCursorCharsetGl, savedCursorCharsetGl);
            (pendingAutoWrap, inactivePendingAutoWrap) =
                (inactivePendingAutoWrap, pendingAutoWrap);
            (currentStyle, inactiveCurrentStyle) = (inactiveCurrentStyle, currentStyle);
            (savedCursorStyle, inactiveSavedCursorStyle) =
                (inactiveSavedCursorStyle, savedCursorStyle);
        }

        private void SaveCursor(int[] parameters, ulong sequence)
        {
            if (parameters.Length != 0)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            SaveCursor();
        }

        private void RestoreCursor(int[] parameters, ulong sequence)
        {
            if (parameters.Length != 0)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            RestoreCursor();
        }

        private void MoveCursorLine(int deltaRows, int[] parameters, ulong sequence)
        {
            if (permanentRejectionCode != null) return;
            if (parameters.Length > 1)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            cursorColumn = 0;
            cursorRow = ClampRelativeCursorRow(cursorRow + deltaRows);
            pendingAutoWrap = false;
        }

        private void InsertCharacters(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null) return;
            ShiftCellsRight(cursorRow, cursorColumn, count);
            pendingAutoWrap = false;
        }

        private void DeleteCharacters(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null) return;
            ShiftCellsLeft(cursorRow, cursorColumn, count);
            pendingAutoWrap = false;
        }

        private void EraseCharacters(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null) return;
            ClearRange(cursorRow, cursorColumn, Math.Min(columns - 1, cursorColumn + count - 1));
            pendingAutoWrap = false;
        }

        private void InsertLines(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null || cursorRow < scrollTop || cursorRow >= scrollBottomExclusive) return;
            ScrollRegionDown(cursorRow, scrollBottomExclusive, count);
            cursorColumn = 0;
            pendingAutoWrap = false;
        }

        private void DeleteLines(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null || cursorRow < scrollTop || cursorRow >= scrollBottomExclusive) return;
            ScrollRegionUp(cursorRow, scrollBottomExclusive, count, sequence, false);
            cursorColumn = 0;
            pendingAutoWrap = false;
        }

        private void ScrollUp(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null) return;
            ScrollRegionUp(scrollTop, scrollBottomExclusive, count, sequence, false);
            pendingAutoWrap = false;
        }

        private void ScrollDown(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null) return;
            ScrollRegionDown(scrollTop, scrollBottomExclusive, count);
            pendingAutoWrap = false;
        }

        private void CursorForwardTabs(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null) return;
            var delayedWrap = pendingAutoWrap;
            while (count-- > 0) cursorColumn = FindNextTab(cursorColumn);
            pendingAutoWrap = delayedWrap;
        }

        private void CursorBackwardTabs(int[] parameters, ulong sequence)
        {
            var count = EditCount(parameters, sequence);
            if (permanentRejectionCode != null) return;
            while (count-- > 0) cursorColumn = FindPreviousTab(cursorColumn);
            pendingAutoWrap = false;
        }

        private void ClearTabStops(int[] parameters, ulong sequence)
        {
            if (!TrySingleParameter(parameters, 0, out var mode))
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            if (mode == 0) tabStops[cursorColumn] = false;
            else if (mode == 3)
            {
                Array.Clear(tabStops, 0, tabStops.Length);
                defaultTabStopsEnabled = false;
            }
            else Reject("TAB_CLEAR_MODE_UNSUPPORTED", sequence);
        }

        private int EditCount(int[] parameters, ulong sequence)
        {
            if (parameters.Length > 1)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return 0;
            }
            return Math.Min(columns, ParameterOrDefault(parameters, 0, 1));
        }

        private void WriteCharacter(char value, ulong sequence)
        {
            var lineDrawing = charsetGl == 0 ? lineDrawingG0 : lineDrawingG1;
            WriteGrapheme(lineDrawing ? MapDecSpecialGraphics(value).ToString() : value.ToString(), 1, sequence);
        }

        private static char MapDecSpecialGraphics(char value) => value switch
        {
            '_' => ' ', '`' => '◆', 'a' => '▒', 'b' => '␉', 'c' => '␌', 'd' => '␍',
            'e' => '␊', 'f' => '°', 'g' => '±', 'h' => '␤', 'i' => '␋', 'j' => '┘', 'k' => '┐',
            'l' => '┌', 'm' => '└', 'n' => '┼', 'o' => '⎺', 'p' => '⎻', 'q' => '─',
            'r' => '⎼', 's' => '⎽', 't' => '├', 'u' => '┤', 'v' => '┴', 'w' => '┬',
            'x' => '│', 'y' => '≤', 'z' => '≥', '{' => 'π', '|' => '≠', '}' => '£',
            '~' => '·', _ => value
        };

        private void WriteGrapheme(string value, int displayWidth, ulong sequence)
        {
            if (pendingAutoWrap)
            {
                if (autoWrap) Wrap(sequence);
                else pendingAutoWrap = false;
                if (permanentRejectionCode != null) return;
            }
            if (displayWidth == 2 && cursorColumn == columns - 1)
            {
                if (!autoWrap) return;
                Wrap(sequence);
                if (permanentRejectionCode != null) return;
            }

            if (insertMode) ShiftCellsRight(cursorRow, cursorColumn, displayWidth);

            ClearCellOccupant(cursorRow, cursorColumn);
            if (displayWidth == 2) ClearCellOccupant(cursorRow, cursorColumn + 1);
            screen[cursorRow].Cells[cursorColumn] = new CellState
            {
                Character = value,
                Style = currentStyle,
                DisplayWidth = checked((byte)displayWidth)
            };
            if (displayWidth == 2)
            {
                screen[cursorRow].Cells[cursorColumn + 1] = new CellState
                {
                    Character = string.Empty,
                    Style = currentStyle,
                    DisplayWidth = 0,
                    WideContinuation = true
                };
                if (cursorColumn == columns - 2)
                {
                    cursorColumn = columns - 1;
                    pendingAutoWrap = autoWrap;
                }
                else cursorColumn += 2;
            }
            else if (cursorColumn == columns - 1) pendingAutoWrap = autoWrap;
            else cursorColumn++;
        }

        private void AppendCombiningScalar(string value, int scalar, ulong sequence)
        {
            if (!TryGetPreviousGrapheme(out var row, out var column))
            {
                Reject("COMBINING_MARK_WITHOUT_BASE", sequence);
                return;
            }
            var cell = screen[row].Cells[column];
            var combined = cell.Character + value;
            if (Encoding.UTF8.GetByteCount(combined) > 64)
            {
                Reject("GRAPHEME_TOO_LONG", sequence);
                return;
            }
            cell.Character = combined;
            if (!AdjustWidthForVariationSelector(row, column, ref cell, scalar, sequence)) return;
            screen[row].Cells[column] = cell;
        }

        private bool AdjustWidthForVariationSelector(
            int row,
            int column,
            ref CellState cell,
            int scalar,
            ulong sequence)
        {
            if ((scalar != 0xFE0E && scalar != 0xFE0F) || !IsEmojiVariationBase(cell.Character)) return true;
            var requestedWidth = scalar == 0xFE0F ? 2 : 1;
            if (cell.DisplayWidth == requestedWidth) return true;
            if (row != cursorRow ||
                (!pendingAutoWrap && column + cell.DisplayWidth != cursorColumn))
            {
                Reject("VARIATION_SELECTOR_WIDTH_UNSUPPORTED", sequence);
                return false;
            }
            if (requestedWidth == 2)
            {
                if (column + 1 >= columns)
                {
                    Reject("VARIATION_SELECTOR_WIDTH_UNSUPPORTED", sequence);
                    return false;
                }
                ClearCellOccupant(row, column + 1);
                screen[row].Cells[column + 1] = new CellState
                {
                    Character = string.Empty,
                    Style = cell.Style,
                    WideContinuation = true
                };
                cell.DisplayWidth = 2;
                if (column == columns - 2)
                {
                    cursorColumn = columns - 1;
                    pendingAutoWrap = true;
                }
                else
                {
                    cursorColumn = column + 2;
                    pendingAutoWrap = false;
                }
                return true;
            }

            screen[row].Cells[column + 1] = BlankCell(StandardEraseStyle());
            cell.DisplayWidth = 1;
            cursorColumn = column + 1;
            pendingAutoWrap = false;
            return true;
        }

        private static bool IsEmojiVariationBase(string grapheme)
        {
            if (string.IsNullOrEmpty(grapheme)) return false;
            var scalar = char.ConvertToUtf32(grapheme, 0);
            return scalar == 0x00A9 || scalar == 0x00AE || scalar == 0x203C || scalar == 0x2049 ||
                scalar == 0x2122 || scalar == 0x2139 || scalar == 0x3030 || scalar == 0x303D ||
                scalar == 0x3297 || scalar == 0x3299 ||
                (scalar >= 0x2300 && scalar <= 0x27FF) ||
                (scalar >= 0x1F000 && scalar <= 0x1FAFF);
        }

        private bool TryGetPreviousGrapheme(out int row, out int column)
        {
            row = cursorRow;
            column = pendingAutoWrap ? cursorColumn : cursorColumn - 1;
            if (column < 0) return false;
            if (screen[row].Cells[column].WideContinuation) column--;
            return column >= 0 && screen[row].Cells[column].DisplayWidth > 0;
        }

        private void Backspace()
        {
            pendingAutoWrap = false;
            if (cursorColumn == 0) return;
            cursorColumn--;
            if (screen[cursorRow].Cells[cursorColumn].WideContinuation && cursorColumn > 0)
                cursorColumn--;
        }

        private void HorizontalTab()
        {
            cursorColumn = FindNextTab(cursorColumn);
        }

        private int FindNextTab(int column)
        {
            for (var candidate = column + 1; candidate < columns; candidate++)
                if (tabStops[candidate]) return candidate;
            return columns - 1;
        }

        private int FindPreviousTab(int column)
        {
            for (var candidate = column - 1; candidate >= 0; candidate--)
                if (tabStops[candidate]) return candidate;
            return 0;
        }

        private void ReverseIndex()
        {
            pendingAutoWrap = false;
            if (cursorRow == scrollTop) ScrollRegionDown(scrollTop, scrollBottomExclusive, 1);
            else if (cursorRow > 0) cursorRow--;
        }

        private void Wrap(ulong sequence)
        {
            screen[cursorRow].Wrapped = true;
            cursorColumn = 0;
            if (cursorRow == scrollBottomExclusive - 1) Scroll(sequence);
            else cursorRow = Math.Min(rows - 1, cursorRow + 1);
            pendingAutoWrap = false;
        }

        private void LineFeed(ulong sequence)
        {
            pendingAutoWrap = false;
            if (cursorRow == scrollBottomExclusive - 1) Scroll(sequence);
            else cursorRow = Math.Min(rows - 1, cursorRow + 1);
        }

        private void Scroll(ulong sequence)
        {
            ScrollRegionUp(scrollTop, scrollBottomExclusive, 1, sequence,
                !activeAlternateScreen && scrollTop == 0 && scrollBottomExclusive == rows);
            if (permanentRejectionCode == null) cursorRow = scrollBottomExclusive - 1;
        }

        private void ScrollRegionUp(
            int top,
            int bottomExclusive,
            int requestedCount,
            ulong sequence,
            bool appendScrollback)
        {
            var count = Math.Min(requestedCount, bottomExclusive - top);
            if (count <= 0) return;
            if (appendScrollback)
            {
                if (scrollback.Count > MaximumScrollbackRows - count)
                {
                    Reject("SCROLLBACK_LIMIT_EXCEEDED", sequence);
                    return;
                }
                for (var offset = 0; offset < count; offset++)
                {
                    var sealedRow = CloneRow(screen[top + offset]);
                    sealedRow.SealedAtSequence = sequence;
                    scrollback.Add(sealedRow);
                }
                screen.RemoveRange(top, count);
                for (var offset = 0; offset < count; offset++) screen.Add(NewBlankRow(StandardEraseStyle()));
                return;
            }

            for (var row = top; row < bottomExclusive - count; row++)
            {
                var lineId = screen[row].LineId;
                var shifted = CloneRow(screen[row + count]);
                shifted.LineId = lineId;
                screen[row] = shifted;
            }
            for (var row = bottomExclusive - count; row < bottomExclusive; row++)
                screen[row] = NewBlankRow(screen[row].LineId, StandardEraseStyle());
        }

        private void ScrollRegionDown(int top, int bottomExclusive, int requestedCount)
        {
            var count = Math.Min(requestedCount, bottomExclusive - top);
            if (count <= 0) return;
            for (var row = bottomExclusive - 1; row >= top + count; row--)
            {
                var lineId = screen[row].LineId;
                var shifted = CloneRow(screen[row - count]);
                shifted.LineId = lineId;
                screen[row] = shifted;
            }
            for (var row = top; row < top + count; row++)
                screen[row] = NewBlankRow(screen[row].LineId, StandardEraseStyle());
        }

        private void ShiftCellsRight(int row, int start, int requestedCount)
        {
            var count = Math.Min(requestedCount, columns - start);
            if (count <= 0) return;
            ClearCellOccupant(row, start);
            for (var column = columns - 1; column >= start + count; column--)
                screen[row].Cells[column] = screen[row].Cells[column - count];
            for (var column = start; column < start + count; column++)
                screen[row].Cells[column] = BlankCell(StandardEraseStyle());
            NormalizeWideCells(row);
        }

        private void ShiftCellsLeft(int row, int start, int requestedCount)
        {
            var count = Math.Min(requestedCount, columns - start);
            if (count <= 0) return;
            ClearCellOccupant(row, start);
            for (var column = start; column < columns - count; column++)
                screen[row].Cells[column] = screen[row].Cells[column + count];
            for (var column = columns - count; column < columns; column++)
                screen[row].Cells[column] = BlankCell(StandardEraseStyle());
            NormalizeWideCells(row);
        }

        private void NormalizeWideCells(int row)
        {
            for (var column = 0; column < columns; column++)
            {
                var cell = screen[row].Cells[column];
                if (cell.WideContinuation)
                {
                    if (column == 0 || screen[row].Cells[column - 1].DisplayWidth != 2)
                        screen[row].Cells[column] = BlankCell(StandardEraseStyle());
                }
                else if (cell.DisplayWidth == 2 &&
                    (column + 1 >= columns || !screen[row].Cells[column + 1].WideContinuation))
                {
                    screen[row].Cells[column] = BlankCell(StandardEraseStyle());
                }
            }
        }

        private void ClearRange(int row, int start, int end)
        {
            ClearCellOccupant(row, start);
            ClearCellOccupant(row, end);
            for (var column = start; column <= end; column++)
                screen[row].Cells[column] = BlankCell(StandardEraseStyle());
        }

        private void ClearCellOccupant(int row, int column)
        {
            var cell = screen[row].Cells[column];
            if (cell.WideContinuation)
            {
                screen[row].Cells[column] = BlankCell(StandardEraseStyle());
                if (column > 0) screen[row].Cells[column - 1] = BlankCell(StandardEraseStyle());
            }
            else if (cell.DisplayWidth == 2)
            {
                screen[row].Cells[column] = BlankCell(StandardEraseStyle());
                if (column + 1 < columns) screen[row].Cells[column + 1] = BlankCell(StandardEraseStyle());
            }
        }

        private void MoveCursorRelative(int deltaColumn, int deltaRow, int[] parameters, ulong sequence)
        {
            if (permanentRejectionCode != null) return;
            if (parameters.Length > 1)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            cursorColumn = Math.Max(0, Math.Min(columns - 1, cursorColumn + deltaColumn));
            cursorRow = ClampRelativeCursorRow(cursorRow + deltaRow);
            pendingAutoWrap = false;
        }

        private int ClampRelativeCursorRow(int requestedRow)
        {
            var result = Math.Max(0, Math.Min(rows - 1, requestedRow));
            if (cursorRow >= scrollTop) result = Math.Max(scrollTop, result);
            if (cursorRow < scrollBottomExclusive) result = Math.Min(scrollBottomExclusive - 1, result);
            return result;
        }

        private void SetCursorColumn(int column, int[] parameters, ulong sequence)
        {
            if (parameters.Length > 1)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            cursorColumn = Math.Max(0, Math.Min(columns - 1, column));
            pendingAutoWrap = false;
        }

        private void SetCursor(int column, int row, int[] parameters, ulong sequence)
        {
            if (permanentRejectionCode != null) return;
            if (parameters.Length > 2)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return;
            }
            cursorColumn = Math.Max(0, Math.Min(columns - 1, column));
            var effectiveRow = row;
            var minimumRow = 0;
            var maximumRow = rows - 1;
            if (originMode)
            {
                effectiveRow += scrollTop;
                minimumRow = scrollTop;
                maximumRow = scrollBottomExclusive - 1;
            }
            cursorRow = Math.Max(minimumRow, Math.Min(maximumRow, effectiveRow));
            pendingAutoWrap = false;
        }

        private int CursorDistance(int[] parameters, ulong sequence)
        {
            if (parameters.Length > 1)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return 0;
            }
            return ParameterOrDefault(parameters, 0, 1);
        }

        private int CursorAbsolute(int[] parameters, ulong sequence)
        {
            if (parameters.Length > 1)
            {
                Reject("CSI_ARITY_UNSUPPORTED", sequence);
                return 1;
            }
            return ParameterOrDefault(parameters, 0, 1);
        }

        private static int ParameterOrDefault(int[] parameters, int index, int defaultValue)
        {
            if (index >= parameters.Length || parameters[index] <= 0) return defaultValue;
            return parameters[index];
        }

        private static bool TrySingleParameter(int[] parameters, int defaultValue, out int value)
        {
            value = defaultValue;
            if (parameters.Length > 1) return false;
            if (parameters.Length == 1 && parameters[0] >= 0) value = parameters[0];
            return true;
        }

        private static bool TryParseParameters(string text, out int[] parameters)
        {
            parameters = Array.Empty<int>();
            if (text.Length == 0) return true;
            var parts = text.Split(';');
            if (parts.Length > 16) return false;
            var parsed = new int[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (parts[index].Length == 0)
                {
                    parsed[index] = -1;
                    continue;
                }
                if (!int.TryParse(parts[index], out parsed[index]) || parsed[index] < 0 || parsed[index] > 1000000)
                    return false;
            }
            parameters = parsed;
            return true;
        }

        private static bool IsSupportedSgr(int value) =>
            value == 0 || (value >= 1 && value <= 5) || (value >= 7 && value <= 9) ||
            (value >= 22 && value <= 25) || (value >= 27 && value <= 29) ||
            (value >= 30 && value <= 37) || value == 39 ||
            (value >= 40 && value <= 47) || value == 49 ||
            (value >= 90 && value <= 97) || (value >= 100 && value <= 107);

        private sealed class ReflowResult
        {
            public readonly List<RowState> Rows = new List<RowState>();
            public int CursorRow;
            public int CursorColumn;
        }

        private sealed class LogicalRow
        {
            public readonly List<CellState> Cells = new List<CellState>();
            public int CursorOffset = -1;
        }

        private ReflowResult ReflowRows(
            IList<RowState> source,
            int sourceCursorRow,
            int sourceCursorColumn,
            int newWidth,
            bool primaryLineIds)
        {
            var logicalRows = new List<LogicalRow>();
            LogicalRow logical = null;
            for (var rowIndex = 0; rowIndex < source.Count; rowIndex++)
            {
                logical ??= new LogicalRow();
                var row = source[rowIndex];
                var length = row.Wrapped ? row.Cells.Length : MeaningfulCellCount(row);
                if (rowIndex == sourceCursorRow)
                {
                    length = Math.Max(length, Math.Min(row.Cells.Length, sourceCursorColumn + 1));
                    logical.CursorOffset = logical.Cells.Count + Math.Min(sourceCursorColumn, length);
                }
                for (var column = 0; column < length; column++) logical.Cells.Add(row.Cells[column]);
                if (!row.Wrapped || rowIndex == source.Count - 1)
                {
                    logicalRows.Add(logical);
                    logical = null;
                }
            }
            if (logical != null) logicalRows.Add(logical);
            if (logicalRows.Count == 0) logicalRows.Add(new LogicalRow { CursorOffset = 0 });

            var oldIds = new List<(ulong LineId, ulong SealedAt)>();
            foreach (var row in source) oldIds.Add((row.LineId, row.SealedAtSequence));
            var result = new ReflowResult();
            var oldIdIndex = 0;
            foreach (var logicalRow in logicalRows)
            {
                var output = NewReflowRow(newWidth, primaryLineIds, oldIds, ref oldIdIndex);
                var outputColumn = 0;
                var sourceOffset = 0;
                var cursorRecorded = false;
                for (var index = 0; index < logicalRow.Cells.Count; index++)
                {
                    var cell = logicalRow.Cells[index];
                    if (cell.WideContinuation) continue;
                    var width = Math.Max(1, (int)cell.DisplayWidth);
                    if (width == 2 && outputColumn == newWidth - 1)
                    {
                        output.Wrapped = true;
                        result.Rows.Add(output);
                        output = NewReflowRow(newWidth, primaryLineIds, oldIds, ref oldIdIndex);
                        outputColumn = 0;
                    }
                    if (!cursorRecorded && logicalRow.CursorOffset >= 0 &&
                        logicalRow.CursorOffset <= sourceOffset)
                    {
                        result.CursorRow = result.Rows.Count;
                        result.CursorColumn = outputColumn;
                        cursorRecorded = true;
                    }
                    output.Cells[outputColumn] = cell;
                    if (width == 2)
                    {
                        var continuation = index + 1 < logicalRow.Cells.Count &&
                            logicalRow.Cells[index + 1].WideContinuation
                            ? logicalRow.Cells[index + 1]
                            : new CellState
                            {
                                Character = string.Empty,
                                Style = cell.Style,
                                WideContinuation = true
                            };
                        output.Cells[outputColumn + 1] = continuation;
                    }
                    outputColumn += width;
                    sourceOffset += width;
                    if (outputColumn >= newWidth && index + width < logicalRow.Cells.Count)
                    {
                        output.Wrapped = true;
                        result.Rows.Add(output);
                        output = NewReflowRow(newWidth, primaryLineIds, oldIds, ref oldIdIndex);
                        outputColumn = 0;
                    }
                }
                if (!cursorRecorded && logicalRow.CursorOffset >= 0)
                {
                    result.CursorRow = result.Rows.Count;
                    result.CursorColumn = Math.Min(newWidth - 1, outputColumn);
                }
                result.Rows.Add(output);
            }
            if (primaryLineIds && result.Rows.Count <= oldIds.Count)
            {
                var offset = oldIds.Count - result.Rows.Count;
                for (var index = 0; index < result.Rows.Count; index++)
                {
                    result.Rows[index].LineId = oldIds[offset + index].LineId;
                    result.Rows[index].SealedAtSequence = oldIds[offset + index].SealedAt;
                }
            }
            return result;
        }

        private RowState NewReflowRow(
            int width,
            bool primaryLineIds,
            IList<(ulong LineId, ulong SealedAt)> oldIds,
            ref int oldIdIndex)
        {
            ulong lineId;
            ulong sealedAt;
            if (oldIdIndex < oldIds.Count)
            {
                lineId = primaryLineIds ? oldIds[oldIdIndex].LineId : 0;
                sealedAt = primaryLineIds ? oldIds[oldIdIndex].SealedAt : 0;
                oldIdIndex++;
            }
            else if (primaryLineIds)
            {
                if (nextLineId == ulong.MaxValue)
                    throw new InvalidOperationException("Terminal transcript line ID is exhausted.");
                lineId = nextLineId++;
                sealedAt = 0;
            }
            else
            {
                lineId = 0;
                sealedAt = 0;
            }
            var cells = new CellState[width];
            for (var column = 0; column < width; column++)
                cells[column] = BlankCell(PortableTerminalStyleV1.Default);
            return new RowState { Cells = cells, LineId = lineId, SealedAtSequence = sealedAt };
        }

        private static int MeaningfulCellCount(RowState row)
        {
            for (var column = row.Cells.Length - 1; column >= 0; column--)
            {
                var cell = row.Cells[column];
                if (cell.Character != " " || cell.WideContinuation || cell.DisplayWidth != 1 ||
                    !cell.Style.Equals(PortableTerminalStyleV1.Default)) return column + 1;
            }
            return 0;
        }

        private static int PrimaryViewportStart(ReflowResult result, int newRows)
        {
            var bottomAnchoredStart = Math.Max(0, result.Rows.Count - newRows);
            return Math.Min(bottomAnchoredStart, result.CursorRow);
        }

        private void InstallReflowedPrimary(
            ReflowResult result,
            int newRows,
            int viewportStart,
            ulong sequence)
        {
            scrollback.Clear();
            for (var index = 0; index < viewportStart; index++)
            {
                var row = result.Rows[index];
                if (row.SealedAtSequence == 0) row.SealedAtSequence = sequence;
                scrollback.Add(row);
            }
            screen.Clear();
            for (var index = viewportStart; index < result.Rows.Count && screen.Count < newRows; index++)
                screen.Add(result.Rows[index]);
            while (screen.Count < newRows) screen.Add(NewBlankRowForCurrentWidth(true));
            cursorRow = Math.Max(0, Math.Min(newRows - 1, result.CursorRow - viewportStart));
            cursorColumn = Math.Max(0, Math.Min(columns - 1, result.CursorColumn));
        }

        private void InstallReflowedAlternate(ReflowResult result, int newRows)
        {
            var viewportStart = result.CursorRow >= newRows ? result.CursorRow - newRows + 1 : 0;
            inactiveScreen.Clear();
            for (var index = viewportStart; index < result.Rows.Count && inactiveScreen.Count < newRows; index++)
                inactiveScreen.Add(result.Rows[index]);
            while (inactiveScreen.Count < newRows) inactiveScreen.Add(NewBlankRowForCurrentWidth(false));
            inactiveCursorRow = Math.Max(0, Math.Min(newRows - 1, result.CursorRow - viewportStart));
            inactiveCursorColumn = Math.Max(0, Math.Min(columns - 1, result.CursorColumn));
        }

        private RowState NewBlankRowForCurrentWidth(bool primary)
        {
            var lineId = 0UL;
            if (primary)
            {
                if (nextLineId == ulong.MaxValue)
                    throw new InvalidOperationException("Terminal transcript line ID is exhausted.");
                lineId = nextLineId++;
            }
            var cells = new CellState[columns];
            for (var column = 0; column < columns; column++)
                cells[column] = BlankCell(PortableTerminalStyleV1.Default);
            return new RowState { Cells = cells, LineId = lineId };
        }

        private void ResizeTabStops(int newWidth)
        {
            if (newWidth <= tabStops.Length) return;
            var previousLength = tabStops.Length;
            Array.Resize(ref tabStops, newWidth);
            if (!defaultTabStopsEnabled) return;
            for (var column = 8; column < newWidth; column += 8)
                if (column >= previousLength) tabStops[column] = true;
        }

        private void AddAttribute(PortableTerminalAttributesV1 value) =>
            currentStyle = currentStyle.WithAttributes(currentStyle.Attributes | value);

        private void RemoveAttributes(PortableTerminalAttributesV1 value) =>
            currentStyle = currentStyle.WithAttributes(currentStyle.Attributes & ~value);

        private PortableTerminalStyleV1 StandardEraseStyle() =>
            currentStyle.WithAttributes(PortableTerminalAttributesV1.None).WithHyperlink(0);

        private void ResetTerminal()
        {
            if (activeAlternateScreen)
            {
                SwapActiveScreenState();
                activeAlternateScreen = false;
            }
            cursorColumn = 0;
            cursorRow = 0;
            cursorVisible = true;
            cursorStyle = PortableTerminalCursorStyleV1.Block;
            cursorBlink = true;
            pendingAutoWrap = false;
            autoWrap = true;
            originMode = false;
            insertMode = false;
            bracketedPaste = false;
            focusReporting = false;
            lineDrawingG0 = false;
            lineDrawingG1 = false;
            charsetGl = 0;
            synchronizedOutput = false;
            currentStyle = PortableTerminalStyleV1.Default;
            savedCursorColumn = 0;
            savedCursorRow = 0;
            savedCursorPendingAutoWrap = false;
            savedCursorOriginMode = false;
            savedCursorAutoWrap = true;
            savedCursorLineDrawingG0 = false;
            savedCursorLineDrawingG1 = false;
            savedCursorCharsetGl = 0;
            savedCursorStyle = PortableTerminalStyleV1.Default;
            paletteRgb = CreateDefaultPaletteRgb();
            defaultForegroundRgb = 0x00c0c0c0;
            defaultBackgroundRgb = 0;
            defaultCursorRgb = 0x00c0c0c0;
            title = string.Empty;
            hyperlinks.Clear();
            tabStops = CreateDefaultTabStops(columns);
            defaultTabStopsEnabled = true;
            scrollTop = 0;
            scrollBottomExclusive = rows;
            configuredScrollBottomExclusive = rows;
            scrollback.Clear();
            ReplaceWithBlankScreen();
            inactiveCursorColumn = 0;
            inactiveCursorRow = 0;
            inactiveSavedCursorColumn = 0;
            inactiveSavedCursorRow = 0;
            inactiveSavedCursorPendingAutoWrap = false;
            inactiveSavedCursorOriginMode = false;
            inactiveSavedCursorAutoWrap = true;
            inactiveSavedCursorLineDrawingG0 = false;
            inactiveSavedCursorLineDrawingG1 = false;
            inactiveSavedCursorCharsetGl = 0;
            inactivePendingAutoWrap = false;
            inactiveCurrentStyle = PortableTerminalStyleV1.Default;
            inactiveSavedCursorStyle = PortableTerminalStyleV1.Default;
            ReplaceInactiveWithBlankScreen(true);
        }

        private void ReplaceWithBlankScreen()
        {
            screen.Clear();
            for (var row = 0; row < rows; row++) screen.Add(NewBlankRow());
        }

        private void ReplaceActiveWithBlankAlternateScreen(PortableTerminalStyleV1 style)
        {
            screen.Clear();
            for (var row = 0; row < rows; row++)
                screen.Add(NewBlankRow(0, style));
        }

        private void ReplaceInactiveWithBlankScreen(bool alternate)
        {
            inactiveScreen.Clear();
            for (var row = 0; row < rows; row++)
            {
                inactiveScreen.Add(alternate
                    ? NewBlankRow(0, PortableTerminalStyleV1.Default)
                    : NewBlankRow());
            }
        }

        private RowState NewBlankRow()
        {
            if (nextLineId == ulong.MaxValue)
                throw new InvalidOperationException("Terminal transcript line ID is exhausted.");
            return NewBlankRow(nextLineId++, PortableTerminalStyleV1.Default);
        }

        private RowState NewBlankRow(PortableTerminalStyleV1 style)
        {
            if (nextLineId == ulong.MaxValue)
                throw new InvalidOperationException("Terminal transcript line ID is exhausted.");
            return NewBlankRow(nextLineId++, style);
        }

        private RowState NewBlankRow(ulong lineId, PortableTerminalStyleV1 style)
        {
            var cells = new CellState[columns];
            for (var column = 0; column < columns; column++)
                cells[column] = BlankCell(style);
            return new RowState { Cells = cells, LineId = lineId };
        }

        private IEnumerable<int> EnumerateTabStops()
        {
            for (var index = 0; index < tabStops.Length; index++)
                if (tabStops[index]) yield return index;
        }

        private static bool[] CreateDefaultTabStops(int width)
        {
            var result = new bool[width];
            for (var column = 8; column < width; column += 8) result[column] = true;
            return result;
        }

        private static int[] CreateDefaultPaletteRgb()
        {
            var result = new int[256];
            int[] ansi =
            {
                0x000000, 0x800000, 0x008000, 0x808000,
                0x000080, 0x800080, 0x008080, 0xc0c0c0,
                0x808080, 0xff0000, 0x00ff00, 0xffff00,
                0x0000ff, 0xff00ff, 0x00ffff, 0xffffff
            };
            Array.Copy(ansi, result, ansi.Length);
            int[] levels = { 0, 95, 135, 175, 215, 255 };
            var target = 16;
            foreach (var red in levels)
                foreach (var green in levels)
                    foreach (var blue in levels)
                        result[target++] = (red << 16) | (green << 8) | blue;
            for (var index = 0; index < 24; index++)
            {
                var level = 8 + index * 10;
                result[target++] = (level << 16) | (level << 8) | level;
            }
            return result;
        }

        private static CellState BlankCell(PortableTerminalStyleV1 style) => new CellState
        {
            Character = " ",
            Style = style,
            DisplayWidth = 1
        };

        private static RowState CloneRow(RowState value)
        {
            var cells = new CellState[value.Cells.Length];
            Array.Copy(value.Cells, cells, cells.Length);
            return new RowState
            {
                Cells = cells,
                Wrapped = value.Wrapped,
                LineId = value.LineId,
                SealedAtSequence = value.SealedAtSequence
            };
        }

        private static IEnumerable<PortableTerminalRowV1> SnapshotRows(IEnumerable<RowState> values)
        {
            foreach (var row in values)
            {
                var cells = new PortableTerminalCellV1[row.Cells.Length];
                for (var index = 0; index < cells.Length; index++)
                    cells[index] = new PortableTerminalCellV1(
                        row.Cells[index].Character,
                        row.Cells[index].Style,
                        row.Cells[index].DisplayWidth,
                        row.Cells[index].WideContinuation);
                yield return new PortableTerminalRowV1(
                    cells,
                    row.Wrapped,
                    row.LineId,
                    row.SealedAtSequence);
            }
        }

        private static RowState ToState(PortableTerminalRowV1 value)
        {
            var source = value.CloneCellArray();
            var cells = new CellState[source.Length];
            for (var index = 0; index < source.Length; index++)
                cells[index] = new CellState
                {
                    Character = source[index].Character,
                    Style = source[index].Style,
                    DisplayWidth = checked((byte)source[index].DisplayWidth),
                    WideContinuation = source[index].WideContinuation
                };
            return new RowState
            {
                Cells = cells,
                Wrapped = value.Wrapped,
                LineId = value.LineId,
                SealedAtSequence = value.SealedAtSequence
            };
        }

        private static void AppendRows(StringBuilder target, IEnumerable<RowState> values)
        {
            foreach (var row in values)
            {
                target.Append(row.LineId).Append('@').Append(row.SealedAtSequence).Append(':')
                    .Append(row.Wrapped ? 'W' : 'N').Append(':');
                foreach (var cell in row.Cells)
                {
                    target.Append(cell.Character.Length).Append(':').Append(cell.Character).Append('/')
                        .Append(cell.DisplayWidth).Append(',').Append(cell.WideContinuation ? '1' : '0').Append(',')
                        .Append(cell.Style.Foreground).Append(',').Append(cell.Style.Background).Append(',')
                        .Append((ushort)cell.Style.Attributes).Append(',')
                        .Append(cell.Style.HyperlinkIndexPlusOne).Append(';');
                }
                target.Append('|');
            }
        }

        private void Reject(string code, ulong sequence)
        {
            if (permanentRejectionCode != null) return;
            permanentRejectionCode = code;
            permanentRejectionSequence = sequence;
        }

        private static int Utf8Length(byte first)
        {
            if (first >= 0xC2 && first <= 0xDF) return 2;
            if (first >= 0xE0 && first <= 0xEF) return 3;
            if (first >= 0xF0 && first <= 0xF4) return 4;
            return 0;
        }

        private static bool TryDecodeUtf8Scalar(byte[] bytes, int count, out int scalar)
        {
            scalar = 0;
            try
            {
                var strict = new UTF8Encoding(false, true);
                var text = strict.GetString(bytes, 0, count);
                if (text.Length == 1) scalar = text[0];
                else if (text.Length == 2 && char.IsSurrogatePair(text, 0))
                    scalar = char.ConvertToUtf32(text, 0);
                else return false;
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static int UnicodeDisplayWidth(int scalar)
        {
            if (IsZeroWidthScalar(scalar)) return 0;
            var text = char.ConvertFromUtf32(scalar);
            var category = CharUnicodeInfo.GetUnicodeCategory(text, 0);
            if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator ||
                category == UnicodeCategory.Surrogate || category == UnicodeCategory.OtherNotAssigned ||
                category == UnicodeCategory.PrivateUse)
            {
                return -1;
            }
            return IsWideScalar(scalar) ? 2 : 1;
        }

        private static bool IsZeroWidthScalar(int scalar)
        {
            if ((scalar >= 0xFE00 && scalar <= 0xFE0F) ||
                (scalar >= 0xE0100 && scalar <= 0xE01EF) ||
                (scalar >= 0x1F3FB && scalar <= 0x1F3FF))
            {
                return true;
            }
            var text = char.ConvertFromUtf32(scalar);
            var category = CharUnicodeInfo.GetUnicodeCategory(text, 0);
            return category == UnicodeCategory.NonSpacingMark ||
                category == UnicodeCategory.SpacingCombiningMark ||
                category == UnicodeCategory.EnclosingMark;
        }

        // Deterministic Unicode-width subset derived from the common wcwidth
        // East Asian wide/full-width ranges. Ambiguous characters remain width
        // one; controls, unassigned scalars, and private-use characters fail
        // closed instead of guessing a terminal-dependent width.
        private static bool IsWideScalar(int scalar) =>
            scalar >= 0x1100 &&
            (scalar <= 0x115F ||
             scalar == 0x2329 || scalar == 0x232A ||
             (scalar >= 0x2E80 && scalar <= 0x303E) ||
             (scalar >= 0x3040 && scalar <= 0xA4CF) ||
             (scalar >= 0xAC00 && scalar <= 0xD7A3) ||
             (scalar >= 0xF900 && scalar <= 0xFAFF) ||
             (scalar >= 0xFE10 && scalar <= 0xFE19) ||
             (scalar >= 0xFE30 && scalar <= 0xFE6F) ||
             (scalar >= 0xFF00 && scalar <= 0xFF60) ||
             (scalar >= 0xFFE0 && scalar <= 0xFFE6) ||
             (scalar >= 0x1F1E6 && scalar <= 0x1FAFF) ||
             (scalar >= 0x20000 && scalar <= 0x3FFFD));
    }
}
