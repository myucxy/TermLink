package dev.termlink.terminal.termux

import com.termux.terminal.TerminalEmulator
import com.termux.terminal.TerminalRestorableState
import com.termux.terminal.TextStyle
import dev.termlink.terminal.api.TerminalCellAttributesSnapshotV1
import dev.termlink.terminal.api.TerminalCellSnapshotV1
import dev.termlink.terminal.api.TerminalCharsetSnapshotV1
import dev.termlink.terminal.api.TerminalCharsetStateSnapshotV1
import dev.termlink.terminal.api.TerminalColorSnapshotV1
import dev.termlink.terminal.api.TerminalCursorSnapshotV1
import dev.termlink.terminal.api.TerminalCursorStyleSnapshotV1
import dev.termlink.terminal.api.TerminalLifecycleSnapshotV1
import dev.termlink.terminal.api.TerminalMouseEncodingSnapshotV1
import dev.termlink.terminal.api.TerminalMouseTrackingSnapshotV1
import dev.termlink.terminal.api.TerminalParserStateV1
import dev.termlink.terminal.api.TerminalRowSnapshotV1
import dev.termlink.terminal.api.TerminalScreenKindV1
import dev.termlink.terminal.api.TerminalScreenSnapshotV1
import dev.termlink.terminal.api.TerminalSnapshotV1
import dev.termlink.terminal.api.TerminalStyleSnapshotV1
import dev.termlink.terminal.api.TerminalUnderlineStyleSnapshotV1

/** Maps the portable checkpoint into a fully validated, Termux-shaped detached state. */
internal object TermuxSnapshotMapper {
    fun map(snapshot: TerminalSnapshotV1): TerminalRestorableState {
        require(snapshot.formatVersion == TerminalSnapshotV1.FORMAT_VERSION) {
            "Unsupported terminal snapshot format ${snapshot.formatVersion}"
        }
        require(snapshot.sequence > 0) { "Snapshot sequence must be positive" }
        require(snapshot.size.columns in 2..TerminalSnapshotV1.MAX_COLUMNS) {
            "Snapshot columns are outside the Termux Phase 1 range"
        }
        require(snapshot.size.rows in 2..TerminalSnapshotV1.MAX_ROWS) {
            "Snapshot rows are outside the Termux Phase 1 range"
        }
        require(snapshot.parserState == TerminalParserStateV1.GROUND) {
            "Termux Phase 1 only restores a GROUND parser"
        }
        require(snapshot.scrollbackRows.isEmpty()) {
            "Termux Phase 1 snapshots must not contain scrollback"
        }
        require(snapshot.title.encodeToByteArray().size <= TerminalSnapshotV1.MAX_TITLE_UTF8_BYTES) {
            "Snapshot title exceeds the UTF-8 limit"
        }
        validateProjection(snapshot)
        validateModes(snapshot)
        val styles = mapStyles(snapshot)
        val palette = mapPalette(snapshot)
        val tabs = mapTabStops(snapshot)
        val primary = mapScreen(snapshot, snapshot.primaryScreen, styles, "primary")
        val alternate = mapScreen(snapshot, snapshot.alternateScreen, styles, "alternate")
        validateSharedTermuxState(snapshot, primary = snapshot.primaryScreen, alternate = snapshot.alternateScreen)

        return TerminalRestorableState(
            snapshot.size.columns,
            snapshot.size.rows,
            snapshot.activeScreen == TerminalScreenKindV1.ALTERNATE,
            primary,
            alternate,
            TerminalRestorableState.Modes(
                snapshot.modes.autoWrap,
                snapshot.modes.origin,
                snapshot.modes.insert,
                snapshot.modes.reverseVideo,
                snapshot.modes.applicationCursor,
                snapshot.modes.applicationKeypad,
                snapshot.modes.bracketedPaste,
                snapshot.modes.focusReporting,
                snapshot.modes.leftRightMargin,
                when (snapshot.modes.mouseTracking) {
                    TerminalMouseTrackingSnapshotV1.NONE -> 0
                    TerminalMouseTrackingSnapshotV1.NORMAL -> 1
                    TerminalMouseTrackingSnapshotV1.BUTTON_EVENT -> 2
                    else -> error("Unsupported Termux mouse tracking mode")
                },
                snapshot.modes.mouseEncoding == TerminalMouseEncodingSnapshotV1.SGR,
            ),
            palette,
            tabs,
            snapshot.title,
        )
    }

    private fun validateProjection(snapshot: TerminalSnapshotV1) {
        require(snapshot.projection.observedLocalInputEpoch >= 0) {
            "Observed local input epoch must not be negative"
        }
        require(snapshot.projection.lifecycle != TerminalLifecycleSnapshotV1.UNSPECIFIED) {
            "Snapshot lifecycle is unspecified"
        }
        when (snapshot.projection.lifecycle) {
            TerminalLifecycleSnapshotV1.ENDED -> require(snapshot.projection.exitCode != null) {
                "An ended snapshot requires an exit code"
            }
            else -> require(snapshot.projection.exitCode == null) {
                "A non-ended snapshot must not fabricate an exit code"
            }
        }
    }

    private fun validateModes(snapshot: TerminalSnapshotV1) {
        val modes = snapshot.modes
        require(!modes.newline) { "Termux does not implement the terminal newline mode" }
        when (modes.mouseTracking) {
            TerminalMouseTrackingSnapshotV1.NONE -> require(
                modes.mouseEncoding == TerminalMouseEncodingSnapshotV1.DEFAULT,
            ) { "A snapshot without mouse tracking must use default mouse encoding" }
            TerminalMouseTrackingSnapshotV1.NORMAL,
            TerminalMouseTrackingSnapshotV1.BUTTON_EVENT,
            -> require(
                modes.mouseEncoding == TerminalMouseEncodingSnapshotV1.DEFAULT ||
                    modes.mouseEncoding == TerminalMouseEncodingSnapshotV1.SGR,
            ) { "The requested mouse encoding is not supported by Termux" }
            else -> error("The requested mouse tracking mode is not supported by Termux")
        }
    }

    private fun mapStyles(snapshot: TerminalSnapshotV1): LongArray {
        require(snapshot.styleTable.size in 1..TerminalSnapshotV1.MAX_STYLES) {
            "Snapshot style table must contain 1..${TerminalSnapshotV1.MAX_STYLES} entries"
        }
        require(snapshot.hyperlinks.isEmpty()) {
            "Termux Phase 1 cannot restore terminal hyperlinks"
        }
        return snapshot.styleTable.mapIndexed { index, style ->
            require(style.hyperlinkIndex == null) { "Style $index references an unsupported hyperlink" }
            require(style.underlineColor == TerminalColorSnapshotV1.Default) {
                "Style $index uses an underline color Termux cannot preserve"
            }
            when (style.underlineStyle) {
                TerminalUnderlineStyleSnapshotV1.NONE -> require(!style.attributes.underline) {
                    "Style $index has conflicting underline fields"
                }
                TerminalUnderlineStyleSnapshotV1.SINGLE -> require(style.attributes.underline) {
                    "Style $index has conflicting underline fields"
                }
                else -> error("Style $index uses an unsupported underline style")
            }
            TerminalRestorableState.encodeStyle(
                mapColor(style.foreground, TextStyle.COLOR_INDEX_FOREGROUND, "style $index foreground"),
                mapColor(style.background, TextStyle.COLOR_INDEX_BACKGROUND, "style $index background"),
                mapEffects(style.attributes),
            )
        }.toLongArray()
    }

    private fun mapColor(value: TerminalColorSnapshotV1, defaultIndex: Int, label: String): Int = when (value) {
        TerminalColorSnapshotV1.Default -> defaultIndex
        is TerminalColorSnapshotV1.Palette -> {
            require(value.index in 0..255) { "$label palette index is invalid" }
            value.index
        }
        is TerminalColorSnapshotV1.Rgb -> {
            require(value.value in 0..0x00ff_ffff) { "$label RGB value is invalid" }
            0xff00_0000.toInt() or value.value
        }
    }

    private fun mapEffects(value: TerminalCellAttributesSnapshotV1): Int =
        (if (value.bold) TextStyle.CHARACTER_ATTRIBUTE_BOLD else 0) or
            (if (value.dim) TextStyle.CHARACTER_ATTRIBUTE_DIM else 0) or
            (if (value.italic) TextStyle.CHARACTER_ATTRIBUTE_ITALIC else 0) or
            (if (value.underline) TextStyle.CHARACTER_ATTRIBUTE_UNDERLINE else 0) or
            (if (value.blink) TextStyle.CHARACTER_ATTRIBUTE_BLINK else 0) or
            (if (value.inverse) TextStyle.CHARACTER_ATTRIBUTE_INVERSE else 0) or
            (if (value.invisible) TextStyle.CHARACTER_ATTRIBUTE_INVISIBLE else 0) or
            (if (value.strikethrough) TextStyle.CHARACTER_ATTRIBUTE_STRIKETHROUGH else 0) or
            (if (value.protected) TextStyle.CHARACTER_ATTRIBUTE_PROTECTED else 0)

    private fun mapPalette(snapshot: TerminalSnapshotV1): IntArray {
        val palette = snapshot.palette
        require(palette.colorsRgb.size == 256) { "Snapshot palette must contain exactly 256 colors" }
        return IntArray(TextStyle.NUM_INDEXED_COLORS).also { result ->
            palette.colorsRgb.forEachIndexed { index, color -> result[index] = opaqueRgb(color, "palette[$index]") }
            result[TextStyle.COLOR_INDEX_FOREGROUND] = opaqueRgb(palette.defaultForegroundRgb, "default foreground")
            result[TextStyle.COLOR_INDEX_BACKGROUND] = opaqueRgb(palette.defaultBackgroundRgb, "default background")
            result[TextStyle.COLOR_INDEX_CURSOR] = opaqueRgb(palette.defaultCursorRgb, "default cursor")
        }
    }

    private fun opaqueRgb(value: Int, label: String): Int {
        require(value in 0..0x00ff_ffff) { "$label RGB value is invalid" }
        return 0xff00_0000.toInt() or value
    }

    private fun mapTabStops(snapshot: TerminalSnapshotV1): BooleanArray {
        var previous = -1
        val result = BooleanArray(snapshot.size.columns)
        snapshot.tabStops.forEach { column ->
            require(column in 0 until snapshot.size.columns && column > previous) {
                "Snapshot tab stops must be strictly increasing and in bounds"
            }
            result[column] = true
            previous = column
        }
        return result
    }

    private fun mapScreen(
        snapshot: TerminalSnapshotV1,
        screen: TerminalScreenSnapshotV1,
        styles: LongArray,
        label: String,
    ): TerminalRestorableState.ScreenState {
        require(screen.visibleRows.size == snapshot.size.rows) { "$label screen row count mismatch" }
        require(screen.scrollTop in 0 until screen.scrollBottomExclusive &&
            screen.scrollBottomExclusive <= snapshot.size.rows
        ) { "$label screen vertical margins are invalid" }
        require(screen.marginLeft in 0 until screen.marginRightExclusive &&
            screen.marginRightExclusive <= snapshot.size.columns
        ) { "$label screen horizontal margins are invalid" }
        require(screen.currentStyleIndex in styles.indices) { "$label screen current style is invalid" }
        require(screen.pendingWrap == screen.cursor.pendingWrap) {
            "$label screen pending-wrap fields disagree"
        }
        require(screen.cursor.renditionStyleIndex == screen.currentStyleIndex) {
            "$label current cursor rendition differs from the current screen rendition"
        }
        validateCursor(snapshot, screen.cursor, styles, "$label cursor")
        validateCursor(snapshot, screen.savedCursor, styles, "$label saved cursor")
        require(!screen.savedCursor.pendingWrap) {
            "$label saved cursor contains pending-wrap state Termux cannot restore"
        }
        require(screen.charset == screen.cursor.charset) {
            "$label screen and current cursor charsets disagree"
        }
        val lastPrinted = lastPrintedCodePoint(screen.lastPrintedGrapheme, label)
        return TerminalRestorableState.ScreenState(
            screen.visibleRows.mapIndexed { rowIndex, row ->
                mapRow(snapshot, row, styles, "$label row $rowIndex")
            }.toTypedArray(),
            mapCursor(screen.cursor, styles),
            mapCursor(screen.savedCursor, styles),
            screen.scrollTop,
            screen.scrollBottomExclusive,
            screen.marginLeft,
            screen.marginRightExclusive,
            styles[screen.currentStyleIndex],
            screen.pendingWrap,
            lastPrinted,
            mapCharset(screen.charset, "$label charset"),
        )
    }

    private fun validateCursor(
        snapshot: TerminalSnapshotV1,
        cursor: TerminalCursorSnapshotV1,
        styles: LongArray,
        label: String,
    ) {
        require(cursor.column in 0 until snapshot.size.columns && cursor.row in 0 until snapshot.size.rows) {
            "$label is outside the screen"
        }
        require(cursor.renditionStyleIndex in styles.indices) { "$label rendition style is invalid" }
        require(cursor.style != TerminalCursorStyleSnapshotV1.UNSPECIFIED) { "$label style is unspecified" }
        mapCharset(cursor.charset, "$label charset")
    }

    private fun mapCursor(cursor: TerminalCursorSnapshotV1, styles: LongArray) =
        TerminalRestorableState.CursorState(
            cursor.column,
            cursor.row,
            cursor.visible,
            when (cursor.style) {
                TerminalCursorStyleSnapshotV1.BLOCK -> TerminalEmulator.TERMINAL_CURSOR_STYLE_BLOCK
                TerminalCursorStyleSnapshotV1.UNDERLINE -> TerminalEmulator.TERMINAL_CURSOR_STYLE_UNDERLINE
                TerminalCursorStyleSnapshotV1.BAR -> TerminalEmulator.TERMINAL_CURSOR_STYLE_BAR
                TerminalCursorStyleSnapshotV1.UNSPECIFIED -> error("Cursor style is unspecified")
            },
            cursor.blink,
            styles[cursor.renditionStyleIndex],
            cursor.originMode,
            cursor.pendingWrap,
            mapCharset(cursor.charset, "cursor charset"),
        )

    private fun mapCharset(value: TerminalCharsetStateSnapshotV1, label: String): TerminalRestorableState.CharsetState {
        fun supported(slot: TerminalCharsetSnapshotV1, name: String): Boolean = when (slot) {
            TerminalCharsetSnapshotV1.ASCII -> false
            TerminalCharsetSnapshotV1.DEC_SPECIAL_GRAPHICS -> true
            else -> error("$label $name charset is unsupported by Termux Phase 1")
        }
        val g0 = supported(value.g0, "G0")
        val g1 = supported(value.g1, "G1")
        require(value.g2 == TerminalCharsetSnapshotV1.ASCII && value.g3 == TerminalCharsetSnapshotV1.ASCII) {
            "$label G2/G3 charsets are unsupported by Termux Phase 1"
        }
        require(value.gl in 0..1 && value.gr == 0) { "$label GL/GR selection is unsupported by Termux Phase 1" }
        return TerminalRestorableState.CharsetState(g0, g1, value.gl)
    }

    private fun mapRow(
        snapshot: TerminalSnapshotV1,
        row: TerminalRowSnapshotV1,
        styles: LongArray,
        label: String,
    ): TerminalRestorableState.RowState {
        require(row.cells.size == snapshot.size.columns) { "$label width mismatch" }
        var wideLead: TerminalCellSnapshotV1? = null
        val cells = row.cells.mapIndexed { column, cell ->
            require(cell.styleIndex in styles.indices) { "$label column $column has an invalid style" }
            require(cell.grapheme.encodeToByteArray().size <= 64) {
                "$label column $column grapheme exceeds the UTF-8 limit"
            }
            if (cell.wideContinuation) {
                val lead = requireNotNull(wideLead) { "$label column $column has an orphan wide continuation" }
                require(cell.grapheme.isEmpty() && cell.displayWidth == 0 && cell.styleIndex == lead.styleIndex) {
                    "$label column $column has an invalid wide continuation"
                }
                wideLead = null
            } else {
                require(wideLead == null) { "$label is missing a wide continuation before column $column" }
                require(cell.displayWidth in 1..2 && (cell.grapheme.isNotEmpty() || cell.displayWidth == 1)) {
                    "$label column $column is not a supported expanded cell"
                }
                if (cell.displayWidth == 2) wideLead = cell
            }
            TerminalRestorableState.CellState(
                cell.grapheme.ifEmpty { " " },
                cell.displayWidth,
                styles[cell.styleIndex],
                cell.wideContinuation,
            )
        }.toTypedArray()
        require(wideLead == null) { "$label ends before a wide continuation" }
        return TerminalRestorableState.RowState(cells, row.softWrapped)
    }

    private fun lastPrintedCodePoint(value: String, label: String): Int {
        if (value.isEmpty()) return -1
        require(value.codePointCount(0, value.length) == 1) {
            "$label last printed grapheme cannot be represented by Termux Phase 1"
        }
        return value.codePointAt(0)
    }

    private fun validateSharedTermuxState(
        snapshot: TerminalSnapshotV1,
        primary: TerminalScreenSnapshotV1,
        alternate: TerminalScreenSnapshotV1,
    ) {
        require(primary.scrollTop == alternate.scrollTop &&
            primary.scrollBottomExclusive == alternate.scrollBottomExclusive &&
            primary.marginLeft == alternate.marginLeft &&
            primary.marginRightExclusive == alternate.marginRightExclusive
        ) { "Termux cannot retain different margins for primary and alternate screens" }
        require(primary.lastPrintedGrapheme == alternate.lastPrintedGrapheme) {
            "Termux has one last-emitted code point shared by both screens"
        }
        require(primary.cursor.visible == alternate.cursor.visible &&
            primary.cursor.style == alternate.cursor.style &&
            primary.cursor.blink == alternate.cursor.blink
        ) { "Termux has one cursor appearance shared by both screens" }
        val active = if (snapshot.activeScreen == TerminalScreenKindV1.PRIMARY) primary else alternate
        require(active.cursor.originMode == snapshot.modes.origin) {
            "The active cursor origin mode disagrees with terminal modes"
        }
        when (snapshot.activeScreen) {
            TerminalScreenKindV1.PRIMARY -> require(retained(alternate.cursor) == retained(primary.cursor)) {
                "Entering the alternate screen would not preserve the active cursor state"
            }
            TerminalScreenKindV1.ALTERNATE -> require(retained(primary.cursor) == retained(primary.savedCursor)) {
                "Leaving the alternate screen would not restore the primary cursor state"
            }
        }
        listOf(primary, alternate).forEachIndexed { index, screen ->
            require(screen.savedCursor.visible == screen.cursor.visible &&
                screen.savedCursor.style == screen.cursor.style &&
                screen.savedCursor.blink == screen.cursor.blink
            ) { "Screen $index saved cursor has an unsupported independent appearance" }
        }
        if (!snapshot.modes.leftRightMargin) {
            require(primary.marginLeft == 0 && primary.marginRightExclusive == snapshot.size.columns) {
                "Horizontal margins require left-right margin mode"
            }
        }
        if (primary.pendingWrap) {
            require(primary.cursor.column == primary.marginRightExclusive - 1 &&
                alternate.cursor.column == alternate.marginRightExclusive - 1
            ) { "Pending wrap requires the cursor at the right margin" }
        }
    }

    private data class RetainedCursor(
        val column: Int,
        val row: Int,
        val renditionStyleIndex: Int,
        val charset: TerminalCharsetStateSnapshotV1,
        val originMode: Boolean,
        val pendingWrap: Boolean,
    )

    private fun retained(cursor: TerminalCursorSnapshotV1) = RetainedCursor(
        cursor.column,
        cursor.row,
        cursor.renditionStyleIndex,
        cursor.charset,
        cursor.originMode,
        cursor.pendingWrap,
    )
}
