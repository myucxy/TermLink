package com.termux.terminal;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Objects;

/**
 * A completely validated, implementation-shaped checkpoint used to construct a detached
 * {@link TerminalEmulator}. It intentionally contains no transcript rows or parser fragments.
 */
public final class TerminalRestorableState {

    public static final int MAX_COLUMNS = 512;
    public static final int MAX_ROWS = 256;
    public static final int MAX_TITLE_UTF8_BYTES = 4096;

    public final int columns;
    public final int rows;
    public final boolean alternateScreenActive;
    public final ScreenState primaryScreen;
    public final ScreenState alternateScreen;
    public final Modes modes;
    public final int[] paletteArgb;
    public final boolean[] tabStops;
    public final String title;

    /** Encodes a validated implementation color/effect tuple for a cell or cursor rendition. */
    public static long encodeStyle(int foreground, int background, int effects) {
        validateTermuxColor(foreground, "style foreground");
        validateTermuxColor(background, "style background");
        return TextStyle.encode(foreground, background, effects);
    }

    public TerminalRestorableState(
        int columns,
        int rows,
        boolean alternateScreenActive,
        ScreenState primaryScreen,
        ScreenState alternateScreen,
        Modes modes,
        int[] paletteArgb,
        boolean[] tabStops,
        String title
    ) {
        if (columns < 2 || columns > MAX_COLUMNS) throw new IllegalArgumentException("snapshot columns are unsupported: " + columns);
        if (rows < 2 || rows > MAX_ROWS) throw new IllegalArgumentException("snapshot rows are unsupported: " + rows);
        this.columns = columns;
        this.rows = rows;
        this.alternateScreenActive = alternateScreenActive;
        this.primaryScreen = Objects.requireNonNull(primaryScreen, "primaryScreen");
        this.alternateScreen = Objects.requireNonNull(alternateScreen, "alternateScreen");
        this.modes = Objects.requireNonNull(modes, "modes");
        validateScreen(primaryScreen, "primary");
        validateScreen(alternateScreen, "alternate");

        if (paletteArgb == null || paletteArgb.length != TextStyle.NUM_INDEXED_COLORS)
            throw new IllegalArgumentException("snapshot palette must contain 259 Termux colors");
        this.paletteArgb = paletteArgb.clone();
        for (int color : this.paletteArgb) validateArgb(color, "palette color");

        if (tabStops == null || tabStops.length != columns)
            throw new IllegalArgumentException("snapshot tab-stop bitmap width mismatch");
        this.tabStops = tabStops.clone();

        this.title = Objects.requireNonNull(title, "title");
        if (title.getBytes(StandardCharsets.UTF_8).length > MAX_TITLE_UTF8_BYTES)
            throw new IllegalArgumentException("snapshot title exceeds the UTF-8 limit");
    }

    private void validateScreen(ScreenState screen, String label) {
        if (screen.visibleRows == null || screen.visibleRows.length != rows)
            throw new IllegalArgumentException(label + " screen row count mismatch");
        for (int row = 0; row < rows; row++) validateRow(screen.visibleRows[row], label, row);
        validateCursor(screen.cursor, label + " cursor");
        validateCursor(screen.savedCursor, label + " saved cursor");
        if (screen.scrollTop < 0 || screen.scrollTop >= screen.scrollBottom || screen.scrollBottom > rows)
            throw new IllegalArgumentException(label + " screen vertical margins are invalid");
        if (screen.marginLeft < 0 || screen.marginLeft >= screen.marginRight || screen.marginRight > columns)
            throw new IllegalArgumentException(label + " screen horizontal margins are invalid");
        validateStyle(screen.currentStyle, label + " current style");
        validateCharset(screen.charset, label + " charset");
        if (screen.lastPrintedCodePoint != -1 && !Character.isValidCodePoint(screen.lastPrintedCodePoint))
            throw new IllegalArgumentException(label + " screen last printed code point is invalid");
    }

    private void validateRow(RowState row, String label, int rowIndex) {
        if (row == null || row.cells == null || row.cells.length != columns)
            throw new IllegalArgumentException(label + " screen row " + rowIndex + " width mismatch");
        boolean continuationExpected = false;
        long wideLeadStyle = 0;
        for (int column = 0; column < columns; column++) {
            CellState cell = row.cells[column];
            if (cell == null) throw new IllegalArgumentException(label + " screen contains a null cell");
            validateStyle(cell.style, label + " cell style");
            if (cell.wideContinuation) {
                if (!continuationExpected || !cell.grapheme.isEmpty() || cell.displayWidth != 0 ||
                    cell.style != wideLeadStyle)
                    throw new IllegalArgumentException(label + " screen has an invalid wide continuation");
                continuationExpected = false;
                continue;
            }
            if (continuationExpected)
                throw new IllegalArgumentException(label + " screen wide glyph is missing its continuation");
            validateGrapheme(cell, label);
            if (cell.displayWidth == 2) {
                if (column + 1 >= columns)
                    throw new IllegalArgumentException(label + " screen wide glyph starts in the last column");
                continuationExpected = true;
                wideLeadStyle = cell.style;
            }
        }
        if (continuationExpected)
            throw new IllegalArgumentException(label + " screen ends before a wide continuation");
    }

    private static void validateGrapheme(CellState cell, String label) {
        if (cell.grapheme == null || cell.grapheme.isEmpty() || (cell.displayWidth != 1 && cell.displayWidth != 2))
            throw new IllegalArgumentException(label + " screen has an unsupported grapheme cell");
        int index = 0;
        int first = cell.grapheme.codePointAt(index);
        if (WcWidth.width(first) != cell.displayWidth)
            throw new IllegalArgumentException(label + " screen grapheme display width does not match Termux");
        index += Character.charCount(first);
        int combiningCount = 0;
        while (index < cell.grapheme.length()) {
            int codePoint = cell.grapheme.codePointAt(index);
            if (WcWidth.width(codePoint) > 0)
                throw new IllegalArgumentException(label + " screen grapheme has multiple spacing code points");
            combiningCount++;
            if (combiningCount > 15)
                throw new IllegalArgumentException(label + " screen grapheme exceeds the Termux combining limit");
            index += Character.charCount(codePoint);
        }
    }

    private void validateCursor(CursorState cursor, String label) {
        if (cursor == null || cursor.column < 0 || cursor.column >= columns || cursor.row < 0 || cursor.row >= rows)
            throw new IllegalArgumentException(label + " is outside the screen");
        if (cursor.cursorStyle < TerminalEmulator.TERMINAL_CURSOR_STYLE_BLOCK ||
            cursor.cursorStyle > TerminalEmulator.TERMINAL_CURSOR_STYLE_BAR)
            throw new IllegalArgumentException(label + " style is unsupported");
        validateStyle(cursor.renditionStyle, label + " rendition");
        validateCharset(cursor.charset, label + " charset");
    }

    private static void validateStyle(long style, String label) {
        validateTermuxColor(TextStyle.decodeForeColor(style), label + " foreground");
        validateTermuxColor(TextStyle.decodeBackColor(style), label + " background");
        int effects = TextStyle.decodeEffect(style);
        int supported = TextStyle.CHARACTER_ATTRIBUTE_BOLD |
            TextStyle.CHARACTER_ATTRIBUTE_DIM |
            TextStyle.CHARACTER_ATTRIBUTE_ITALIC |
            TextStyle.CHARACTER_ATTRIBUTE_UNDERLINE |
            TextStyle.CHARACTER_ATTRIBUTE_BLINK |
            TextStyle.CHARACTER_ATTRIBUTE_INVERSE |
            TextStyle.CHARACTER_ATTRIBUTE_INVISIBLE |
            TextStyle.CHARACTER_ATTRIBUTE_STRIKETHROUGH |
            TextStyle.CHARACTER_ATTRIBUTE_PROTECTED;
        if ((effects & ~supported) != 0) throw new IllegalArgumentException(label + " has unsupported effects");
    }

    private static void validateTermuxColor(int color, String label) {
        if ((color & 0xff000000) == 0xff000000) return;
        if (color < 0 || color >= TextStyle.NUM_INDEXED_COLORS)
            throw new IllegalArgumentException(label + " is outside the Termux palette");
    }

    private static void validateArgb(int color, String label) {
        if ((color & 0xff000000) != 0xff000000)
            throw new IllegalArgumentException(label + " must be opaque ARGB");
    }

    private static void validateCharset(CharsetState charset, String label) {
        if (charset == null) throw new IllegalArgumentException(label + " is missing");
        if (charset.gl != 0 && charset.gl != 1)
            throw new IllegalArgumentException(label + " GL slot is unsupported");
    }

    public static final class Modes {
        public final boolean autoWrap;
        public final boolean origin;
        public final boolean insert;
        public final boolean reverseVideo;
        public final boolean applicationCursor;
        public final boolean applicationKeypad;
        public final boolean bracketedPaste;
        public final boolean focusReporting;
        public final boolean leftRightMargin;
        /** 0 none, 1 normal press/release, 2 button-event. */
        public final int mouseTracking;
        public final boolean sgrMouseEncoding;

        public Modes(
            boolean autoWrap,
            boolean origin,
            boolean insert,
            boolean reverseVideo,
            boolean applicationCursor,
            boolean applicationKeypad,
            boolean bracketedPaste,
            boolean focusReporting,
            boolean leftRightMargin,
            int mouseTracking,
            boolean sgrMouseEncoding
        ) {
            if (mouseTracking < 0 || mouseTracking > 2)
                throw new IllegalArgumentException("snapshot mouse tracking mode is unsupported");
            if (mouseTracking == 0 && sgrMouseEncoding)
                throw new IllegalArgumentException("SGR mouse encoding requires mouse tracking");
            this.autoWrap = autoWrap;
            this.origin = origin;
            this.insert = insert;
            this.reverseVideo = reverseVideo;
            this.applicationCursor = applicationCursor;
            this.applicationKeypad = applicationKeypad;
            this.bracketedPaste = bracketedPaste;
            this.focusReporting = focusReporting;
            this.leftRightMargin = leftRightMargin;
            this.mouseTracking = mouseTracking;
            this.sgrMouseEncoding = sgrMouseEncoding;
        }
    }

    public static final class ScreenState {
        public final RowState[] visibleRows;
        public final CursorState cursor;
        public final CursorState savedCursor;
        public final int scrollTop;
        public final int scrollBottom;
        public final int marginLeft;
        public final int marginRight;
        public final long currentStyle;
        public final boolean pendingWrap;
        public final int lastPrintedCodePoint;
        public final CharsetState charset;

        public ScreenState(
            RowState[] visibleRows,
            CursorState cursor,
            CursorState savedCursor,
            int scrollTop,
            int scrollBottom,
            int marginLeft,
            int marginRight,
            long currentStyle,
            boolean pendingWrap,
            int lastPrintedCodePoint,
            CharsetState charset
        ) {
            this.visibleRows = visibleRows == null ? null : visibleRows.clone();
            this.cursor = cursor;
            this.savedCursor = savedCursor;
            this.scrollTop = scrollTop;
            this.scrollBottom = scrollBottom;
            this.marginLeft = marginLeft;
            this.marginRight = marginRight;
            this.currentStyle = currentStyle;
            this.pendingWrap = pendingWrap;
            this.lastPrintedCodePoint = lastPrintedCodePoint;
            this.charset = charset;
        }
    }

    public static final class CursorState {
        public final int column;
        public final int row;
        public final boolean visible;
        public final int cursorStyle;
        public final boolean blink;
        public final long renditionStyle;
        public final boolean originMode;
        public final boolean pendingWrap;
        public final CharsetState charset;

        public CursorState(
            int column,
            int row,
            boolean visible,
            int cursorStyle,
            boolean blink,
            long renditionStyle,
            boolean originMode,
            boolean pendingWrap,
            CharsetState charset
        ) {
            this.column = column;
            this.row = row;
            this.visible = visible;
            this.cursorStyle = cursorStyle;
            this.blink = blink;
            this.renditionStyle = renditionStyle;
            this.originMode = originMode;
            this.pendingWrap = pendingWrap;
            this.charset = charset;
        }
    }

    public static final class CharsetState {
        public final boolean lineDrawingG0;
        public final boolean lineDrawingG1;
        public final int gl;

        public CharsetState(boolean lineDrawingG0, boolean lineDrawingG1, int gl) {
            this.lineDrawingG0 = lineDrawingG0;
            this.lineDrawingG1 = lineDrawingG1;
            this.gl = gl;
        }
    }

    public static final class RowState {
        public final CellState[] cells;
        public final boolean softWrapped;

        public RowState(CellState[] cells, boolean softWrapped) {
            this.cells = cells == null ? null : cells.clone();
            this.softWrapped = softWrapped;
        }
    }

    public static final class CellState {
        public final String grapheme;
        public final int displayWidth;
        public final long style;
        public final boolean wideContinuation;

        public CellState(String grapheme, int displayWidth, long style, boolean wideContinuation) {
            this.grapheme = Objects.requireNonNull(grapheme, "grapheme");
            this.displayWidth = displayWidth;
            this.style = style;
            this.wideContinuation = wideContinuation;
        }
    }

    @Override
    public String toString() {
        return "TerminalRestorableState[" + columns + "x" + rows + ", alternate=" + alternateScreenActive +
            ", palette=" + Arrays.hashCode(paletteArgb) + "]";
    }
}
