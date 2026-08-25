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

        public PortableTerminalStyleV1(int foreground, int background, PortableTerminalAttributesV1 attributes)
        {
            ValidateColor(foreground, nameof(foreground));
            ValidateColor(background, nameof(background));
            if ((attributes & ~AllAttributes) != 0)
                throw new ArgumentOutOfRangeException(nameof(attributes));
            Foreground = foreground;
            Background = background;
            Attributes = attributes;
        }

        public int Foreground { get; }
        public int Background { get; }
        public PortableTerminalAttributesV1 Attributes { get; }

        public static PortableTerminalStyleV1 Default =>
            new PortableTerminalStyleV1(DefaultColor, DefaultColor, PortableTerminalAttributesV1.None);

        public bool Equals(PortableTerminalStyleV1 other) =>
            Foreground == other.Foreground && Background == other.Background && Attributes == other.Attributes;

        public override bool Equals(object value) => value is PortableTerminalStyleV1 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Foreground, Background, (ushort)Attributes);

        internal PortableTerminalStyleV1 WithForeground(int value) =>
            new PortableTerminalStyleV1(value, Background, Attributes);

        internal PortableTerminalStyleV1 WithBackground(int value) =>
            new PortableTerminalStyleV1(Foreground, value, Attributes);

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
            new PortableTerminalStyleV1(Foreground, Background, value);

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
        private readonly ReadOnlyCollection<PortableTerminalRowV1> readOnlyScrollbackRows;
        private readonly ReadOnlyCollection<PortableTerminalRowV1> readOnlyScreenRows;

        internal PortableTerminalSnapshotV1(
            ulong sequence,
            int columns,
            int rows,
            int cursorColumn,
            int cursorRow,
            bool cursorVisible,
            PortableTerminalCursorStyleV1 cursorStyle,
            bool cursorBlink,
            bool pendingAutoWrap,
            bool bracketedPaste,
            bool focusReporting,
            PortableTerminalStyleV1 currentStyle,
            IEnumerable<PortableTerminalRowV1> scrollbackRows,
            IEnumerable<PortableTerminalRowV1> screenRows,
            ulong nextLineId)
        {
            if (sequence == 0) throw new ArgumentOutOfRangeException(nameof(sequence));
            ValidateSize(columns, rows);
            if (cursorColumn < 0 || cursorColumn >= columns) throw new ArgumentOutOfRangeException(nameof(cursorColumn));
            if (cursorRow < 0 || cursorRow >= rows) throw new ArgumentOutOfRangeException(nameof(cursorRow));
            if (nextLineId == 0) throw new ArgumentOutOfRangeException(nameof(nextLineId));
            Sequence = sequence;
            Columns = columns;
            Rows = rows;
            CursorColumn = cursorColumn;
            CursorRow = cursorRow;
            CursorVisible = cursorVisible;
            CursorStyle = cursorStyle;
            CursorBlink = cursorBlink;
            PendingAutoWrap = pendingAutoWrap;
            BracketedPaste = bracketedPaste;
            FocusReporting = focusReporting;
            CurrentStyle = currentStyle;
            NextLineId = nextLineId;
            this.scrollbackRows = CloneRows(scrollbackRows, columns, null);
            this.screenRows = CloneRows(screenRows, columns, rows);
            readOnlyScrollbackRows = Array.AsReadOnly(this.scrollbackRows);
            readOnlyScreenRows = Array.AsReadOnly(this.screenRows);
        }

        public string FormatName => CurrentFormatName;
        public uint FormatVersion => CurrentFormatVersion;
        public ulong Sequence { get; }
        public int Columns { get; }
        public int Rows { get; }
        public int CursorColumn { get; }
        public int CursorRow { get; }
        public bool CursorVisible { get; }
        public PortableTerminalCursorStyleV1 CursorStyle { get; }
        public bool CursorBlink { get; }
        public bool PendingAutoWrap { get; }
        public bool BracketedPaste { get; }
        public bool FocusReporting { get; }
        public PortableTerminalStyleV1 CurrentStyle { get; }
        public ulong NextLineId { get; }
        public IReadOnlyList<PortableTerminalRowV1> ScrollbackRows => readOnlyScrollbackRows;
        public IReadOnlyList<PortableTerminalRowV1> ScreenRows => readOnlyScreenRows;

        internal PortableTerminalRowV1[] CloneScrollbackRows() => CloneRows(scrollbackRows, Columns, null);
        internal PortableTerminalRowV1[] CloneScreenRows() => CloneRows(screenRows, Columns, Rows);

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

        private enum ParserState : byte
        {
            Ground,
            Escape,
            Csi,
            Osc,
            OscEscape
        }

        private readonly List<RowState> scrollback = new List<RowState>();
        private readonly List<RowState> screen = new List<RowState>();
        private readonly StringBuilder csi = new StringBuilder();
        private readonly byte[] utf8 = new byte[4];
        private ParserState parserState;
        private int utf8ExpectedBytes;
        private int utf8BytesSeen;
        private string permanentRejectionCode;
        private ulong permanentRejectionSequence;
        private int columns;
        private int rows;
        private int cursorColumn;
        private int cursorRow;
        private bool cursorVisible = true;
        private PortableTerminalCursorStyleV1 cursorStyle = PortableTerminalCursorStyleV1.Block;
        private bool cursorBlink = true;
        private bool pendingAutoWrap;
        private bool bracketedPaste;
        private bool focusReporting;
        private bool synchronizedOutput;
        private PortableTerminalStyleV1 currentStyle = PortableTerminalStyleV1.Default;
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
            ReplaceWithBlankScreen();
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
                if (parserState == ParserState.Osc || parserState == ParserState.OscEscape)
                    return "INCOMPLETE_OSC";
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
            if (!IsPristineForResize())
            {
                Reject("RESIZE_REFLOW_UNSUPPORTED", sequence);
                lastSequence = sequence;
                return;
            }
            columns = newColumns;
            rows = newRows;
            ReplaceWithBlankScreen();
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
            snapshot = new PortableTerminalSnapshotV1(
                lastSequence,
                columns,
                rows,
                cursorColumn,
                cursorRow,
                cursorVisible,
                cursorStyle,
                cursorBlink,
                pendingAutoWrap,
                bracketedPaste,
                focusReporting,
                currentStyle,
                SnapshotRows(scrollback),
                SnapshotRows(screen),
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
                cursorColumn = snapshot.CursorColumn,
                cursorRow = snapshot.CursorRow,
                cursorVisible = snapshot.CursorVisible,
                cursorStyle = snapshot.CursorStyle,
                cursorBlink = snapshot.CursorBlink,
                pendingAutoWrap = snapshot.PendingAutoWrap,
                bracketedPaste = snapshot.BracketedPaste,
                focusReporting = snapshot.FocusReporting,
                currentStyle = snapshot.CurrentStyle,
                nextLineId = snapshot.NextLineId
            };
            model.scrollback.Clear();
            foreach (var row in snapshot.CloneScrollbackRows()) model.scrollback.Add(ToState(row));
            model.screen.Clear();
            foreach (var row in snapshot.CloneScreenRows()) model.screen.Add(ToState(row));
            return model;
        }

        internal string ValidationFingerprint()
        {
            var value = new StringBuilder();
            value.Append(lastSequence).Append('|').Append(columns).Append('x').Append(rows).Append('|')
                .Append(cursorColumn).Append(',').Append(cursorRow).Append('|')
                .Append(cursorVisible ? '1' : '0').Append(',').Append((int)cursorStyle).Append(',')
                .Append(cursorBlink ? '1' : '0').Append('|').Append(pendingAutoWrap ? '1' : '0').Append('|')
                .Append(bracketedPaste ? '1' : '0').Append(',').Append(focusReporting ? '1' : '0').Append(',')
                .Append(synchronizedOutput ? '1' : '0').Append('|')
                .Append((int)currentStyle.Foreground).Append(',').Append((int)currentStyle.Background).Append(',')
                .Append((ushort)currentStyle.Attributes).Append('|')
                .Append(ParserBoundaryCode).Append('|').Append(PermanentRejectionCode).Append('@')
                .Append(permanentRejectionSequence).Append('|');
            AppendRows(value, scrollback);
            value.Append("||");
            AppendRows(value, screen);
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
            if (value == (byte)'[')
            {
                parserState = ParserState.Csi;
                csi.Clear();
                return;
            }
            if (value == (byte)'c')
            {
                ResetTerminal();
                parserState = ParserState.Ground;
                return;
            }
            if (value == (byte)']') parserState = ParserState.Osc;
            else if (value == (byte)'P') Reject("DCS_UNSUPPORTED", sequence);
            else if (value == (byte)'_' || value == (byte)'^' || value == (byte)'X')
                Reject("CONTROL_STRING_UNSUPPORTED", sequence);
            else Reject("ESC_SEQUENCE_UNSUPPORTED", sequence);
        }

        private void ProcessOsc(byte value)
        {
            if (value == 0x07)
            {
                parserState = ParserState.Ground;
                return;
            }
            if (value == 0x1B) parserState = ParserState.OscEscape;
        }

        private void ProcessOscEscape(byte value)
        {
            if (value == (byte)'\\' || value == 0x07)
            {
                parserState = ParserState.Ground;
                return;
            }
            parserState = value == 0x1B ? ParserState.OscEscape : ParserState.Osc;
        }

        private void ProcessUnicodeScalar(int scalar, ulong sequence)
        {
            if (parserState == ParserState.Osc || parserState == ParserState.OscEscape)
            {
                if (scalar == 0x009C) parserState = ParserState.Ground;
                else if (parserState == ParserState.OscEscape) parserState = ParserState.Osc;
                return;
            }
            if (parserState == ParserState.Escape)
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
                case 'G': SetCursor(CursorAbsolute(parameters, sequence) - 1, cursorRow, parameters, sequence); return;
                case 'd': SetCursor(cursorColumn, CursorAbsolute(parameters, sequence) - 1, parameters, sequence); return;
                case 'H':
                case 'f':
                    ApplyCursorPosition(parameters, sequence);
                    return;
                case 'J': ApplyEraseDisplay(parameters, sequence); return;
                case 'K': ApplyEraseLine(parameters, sequence); return;
                case 'h':
                case 'l':
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
                if (code == 0) currentStyle = PortableTerminalStyleV1.Default;
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
                    case 25:
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
                if (mode == 25) cursorVisible = enabled;
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

        private void WriteCharacter(char value, ulong sequence) =>
            WriteGrapheme(value.ToString(), 1, sequence);

        private void WriteGrapheme(string value, int displayWidth, ulong sequence)
        {
            if (pendingAutoWrap)
            {
                Wrap(sequence);
                if (permanentRejectionCode != null) return;
            }
            if (displayWidth == 2 && cursorColumn == columns - 1)
            {
                Wrap(sequence);
                if (permanentRejectionCode != null) return;
            }

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
                    pendingAutoWrap = true;
                }
                else cursorColumn += 2;
            }
            else if (cursorColumn == columns - 1) pendingAutoWrap = true;
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

            screen[row].Cells[column + 1] = BlankCell(cell.Style);
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
            pendingAutoWrap = false;
            var next = ((cursorColumn / 8) + 1) * 8;
            cursorColumn = Math.Min(columns - 1, next);
        }

        private void Wrap(ulong sequence)
        {
            screen[cursorRow].Wrapped = true;
            cursorColumn = 0;
            if (cursorRow == rows - 1) Scroll(sequence);
            else cursorRow++;
            pendingAutoWrap = false;
        }

        private void LineFeed(ulong sequence)
        {
            pendingAutoWrap = false;
            if (cursorRow == rows - 1) Scroll(sequence);
            else cursorRow++;
        }

        private void Scroll(ulong sequence)
        {
            if (scrollback.Count >= MaximumScrollbackRows)
            {
                Reject("SCROLLBACK_LIMIT_EXCEEDED", sequence);
                return;
            }
            var sealedRow = CloneRow(screen[0]);
            sealedRow.SealedAtSequence = sequence;
            scrollback.Add(sealedRow);
            screen.RemoveAt(0);
            screen.Add(NewBlankRow());
            cursorRow = rows - 1;
        }

        private void ClearRange(int row, int start, int end)
        {
            ClearCellOccupant(row, start);
            ClearCellOccupant(row, end);
            for (var column = start; column <= end; column++)
                screen[row].Cells[column] = BlankCell(currentStyle);
        }

        private void ClearCellOccupant(int row, int column)
        {
            var cell = screen[row].Cells[column];
            if (cell.WideContinuation)
            {
                screen[row].Cells[column] = BlankCell(currentStyle);
                if (column > 0) screen[row].Cells[column - 1] = BlankCell(currentStyle);
            }
            else if (cell.DisplayWidth == 2)
            {
                screen[row].Cells[column] = BlankCell(currentStyle);
                if (column + 1 < columns) screen[row].Cells[column + 1] = BlankCell(currentStyle);
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
            SetCursor(cursorColumn + deltaColumn, cursorRow + deltaRow, Array.Empty<int>(), sequence);
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
            cursorRow = Math.Max(0, Math.Min(rows - 1, row));
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

        private void AddAttribute(PortableTerminalAttributesV1 value) =>
            currentStyle = currentStyle.WithAttributes(currentStyle.Attributes | value);

        private void RemoveAttributes(PortableTerminalAttributesV1 value) =>
            currentStyle = currentStyle.WithAttributes(currentStyle.Attributes & ~value);

        private bool IsPristineForResize()
        {
            if (scrollback.Count != 0 || cursorColumn != 0 || cursorRow != 0 || pendingAutoWrap) return false;
            foreach (var row in screen)
            {
                if (row.Wrapped) return false;
                foreach (var cell in row.Cells)
                {
                    if (cell.Character != " " || cell.DisplayWidth != 1 || cell.WideContinuation ||
                        !cell.Style.Equals(PortableTerminalStyleV1.Default)) return false;
                }
            }
            return true;
        }

        private void ResetTerminal()
        {
            cursorColumn = 0;
            cursorRow = 0;
            cursorVisible = true;
            cursorStyle = PortableTerminalCursorStyleV1.Block;
            cursorBlink = true;
            pendingAutoWrap = false;
            bracketedPaste = false;
            focusReporting = false;
            synchronizedOutput = false;
            currentStyle = PortableTerminalStyleV1.Default;
            scrollback.Clear();
            ReplaceWithBlankScreen();
        }

        private void ReplaceWithBlankScreen()
        {
            screen.Clear();
            for (var row = 0; row < rows; row++) screen.Add(NewBlankRow());
        }

        private RowState NewBlankRow()
        {
            if (nextLineId == ulong.MaxValue)
                throw new InvalidOperationException("Terminal transcript line ID is exhausted.");
            var cells = new CellState[columns];
            for (var column = 0; column < columns; column++)
                cells[column] = BlankCell(PortableTerminalStyleV1.Default);
            return new RowState { Cells = cells, LineId = nextLineId++ };
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
                        .Append((ushort)cell.Style.Attributes).Append(';');
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
