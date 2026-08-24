package dev.termlink.terminal.termux

import com.termux.terminal.TerminalRestorableState
import com.termux.terminal.TextStyle
import com.termux.terminal.WcWidth
import dev.termlink.terminal.api.TerminalCellAttributesSnapshotV1
import dev.termlink.terminal.api.TerminalCellSnapshotV1
import dev.termlink.terminal.api.TerminalColorSnapshotV1
import dev.termlink.terminal.api.TerminalPaletteSnapshotV1
import dev.termlink.terminal.api.TerminalRowSnapshotV1
import dev.termlink.terminal.api.TerminalStyleSnapshotV1
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.api.TerminalUnderlineStyleSnapshotV1

internal data class TermuxTranscriptProjection(
    val snapshotAnchorSequence: Long,
    val columns: Int,
    val rows: Array<TerminalRestorableState.RowState>,
    val lineIds: LongArray,
    val paletteArgb: IntArray,
)

/** Converts authenticated transcript pages directly into immutable renderer rows. */
internal object TermuxTranscriptMapper {
    fun map(
        pages: List<TerminalTranscriptPageV1>,
        authoritativeColumns: Int = pages.maxOfOrNull(TerminalTranscriptPageV1::columns) ?: 1,
    ): TermuxTranscriptProjection {
        require(pages.isNotEmpty()) { "At least one transcript page is required" }
        require(authoritativeColumns in 1..TerminalTranscriptPageV1.MAX_COLUMNS) {
            "Authoritative transcript width is unsupported"
        }
        var projectionRowCount = 0
        pages.forEachIndexed { pageIndex, page ->
            require(page.rows.size <= TermuxTranscriptViewport.MAX_RENDERED_ROWS - projectionRowCount) {
                "Transcript projection exceeds the mobile renderer capacity at page $pageIndex"
            }
            projectionRowCount += page.rows.size
        }
        val anchor = pages.first().snapshotAnchorSequence
        val palette = pages.first().palette
        require(anchor > 0) { "Transcript snapshot anchor must be positive" }
        require(pages.all { it.snapshotAnchorSequence == anchor }) {
            "Transcript pages must share one snapshot anchor"
        }
        require(pages.all { it.palette == palette }) {
            "Transcript pages must share one palette"
        }
        val columns = maxOf(
            authoritativeColumns,
            pages.maxOf(TerminalTranscriptPageV1::columns),
        ).coerceAtLeast(2)
        require(columns in 1..TerminalTranscriptPageV1.MAX_COLUMNS) {
            "Transcript projection width is unsupported"
        }

        var previousLineId = 0L
        val mappedRows = mutableListOf<TerminalRestorableState.RowState>()
        val lineIds = mutableListOf<Long>()
        pages.forEachIndexed { pageIndex, page ->
            validatePage(page, pageIndex)
            val styles = mapStyles(page.styleTable, page.hyperlinks.size)
            page.rows.forEachIndexed { rowIndex, row ->
                require(row.lineId > previousLineId) {
                    "Transcript pages must be ordered oldest to newest without duplicate line IDs"
                }
                require(row.sealedAtSequence in 1..anchor) {
                    "Transcript row sequence is outside its snapshot anchor"
                }
                mappedRows += mapRow(row, columns, styles, "page $pageIndex row $rowIndex")
                lineIds += row.lineId
                previousLineId = row.lineId
            }
        }

        return TermuxTranscriptProjection(
            snapshotAnchorSequence = anchor,
            columns = columns,
            rows = mappedRows.toTypedArray(),
            lineIds = lineIds.toLongArray(),
            paletteArgb = mapPalette(palette),
        )
    }

    private fun validatePage(page: TerminalTranscriptPageV1, pageIndex: Int) {
        require(page.formatVersion == TerminalTranscriptPageV1.FORMAT_VERSION) {
            "Unsupported transcript page format ${page.formatVersion}"
        }
        require(page.rows.size in 1..TerminalTranscriptPageV1.MAX_ROWS) {
            "Transcript page $pageIndex row count is invalid"
        }
        require(page.columns in 1..TerminalTranscriptPageV1.MAX_COLUMNS) {
            "Transcript page $pageIndex width is invalid"
        }
        require(page.rows.maxOf { it.cells.size } == page.columns) {
            "Transcript page $pageIndex maximum row width is inconsistent"
        }
        require(page.firstLineId > 0 && page.lastLineId >= page.firstLineId) {
            "Transcript page $pageIndex line range is invalid"
        }
        require(page.rows.first().lineId == page.firstLineId &&
            page.rows.last().lineId == page.lastLineId
        ) { "Transcript page $pageIndex line range does not match its rows" }
        require(
            page.hasMoreBefore && page.nextBeforeLineId == page.firstLineId ||
                !page.hasMoreBefore && page.nextBeforeLineId == 0L,
        ) { "Transcript page $pageIndex cursor is invalid" }
    }

    private fun mapStyles(styles: List<TerminalStyleSnapshotV1>, hyperlinkCount: Int): LongArray {
        require(styles.size in 1..1024) { "Transcript style table size is invalid" }
        return styles.mapIndexed { index, style ->
            style.hyperlinkIndex?.let { hyperlinkIndex ->
                require(hyperlinkIndex in 0 until hyperlinkCount) {
                    "Transcript style $index has an invalid hyperlink"
                }
            }
            mapColor(style.underlineColor, TextStyle.COLOR_INDEX_FOREGROUND, "style $index underline")
            when (style.underlineStyle) {
                TerminalUnderlineStyleSnapshotV1.NONE -> require(!style.attributes.underline) {
                    "Transcript style $index has conflicting underline fields"
                }
                TerminalUnderlineStyleSnapshotV1.UNSPECIFIED -> error(
                    "Transcript style $index has an unspecified underline style",
                )
                else -> require(style.attributes.underline) {
                    "Transcript style $index has conflicting underline fields"
                }
            }
            TerminalRestorableState.encodeStyle(
                mapColor(style.foreground, TextStyle.COLOR_INDEX_FOREGROUND, "style $index foreground"),
                mapColor(style.background, TextStyle.COLOR_INDEX_BACKGROUND, "style $index background"),
                mapEffects(style.attributes),
            )
        }.toLongArray()
    }

    private fun mapRow(
        row: TerminalRowSnapshotV1,
        columns: Int,
        styles: LongArray,
        label: String,
    ): TerminalRestorableState.RowState {
        require(row.cells.isNotEmpty() && row.cells.size <= columns) { "$label width is invalid" }
        var continuationExpected = false
        var wideLeadStyle = -1
        val cells = row.cells.mapIndexed { column, cell ->
            require(cell.styleIndex in styles.indices) { "$label column $column has an invalid style" }
            require(cell.grapheme.encodeToByteArray().size <= 64) {
                "$label column $column grapheme exceeds the UTF-8 limit"
            }
            if (cell.wideContinuation) {
                require(continuationExpected && cell.grapheme.isEmpty() && cell.displayWidth == 0 &&
                    cell.styleIndex == wideLeadStyle
                ) { "$label column $column has an invalid wide continuation" }
                continuationExpected = false
            } else {
                require(!continuationExpected) { "$label is missing a wide continuation before column $column" }
                validateGrapheme(cell, "$label column $column")
                if (cell.displayWidth == 2) {
                    require(column + 1 < row.cells.size) { "$label ends before a wide continuation" }
                    continuationExpected = true
                    wideLeadStyle = cell.styleIndex
                }
            }
            TerminalRestorableState.CellState(
                cell.grapheme,
                cell.displayWidth,
                styles[cell.styleIndex],
                cell.wideContinuation,
            )
        }.toMutableList()
        require(!continuationExpected) { "$label ends before a wide continuation" }
        repeat(columns - cells.size) {
            cells += TerminalRestorableState.CellState(" ", 1, defaultStyle(), false)
        }
        return TerminalRestorableState.RowState(cells.toTypedArray(), row.softWrapped)
    }

    private fun validateGrapheme(cell: TerminalCellSnapshotV1, label: String) {
        require(cell.grapheme.isNotEmpty() && cell.displayWidth in 1..2) {
            "$label is not a supported expanded cell"
        }
        var offset = 0
        val first = cell.grapheme.codePointAt(offset)
        require(WcWidth.width(first) == cell.displayWidth) { "$label display width does not match Termux" }
        offset += Character.charCount(first)
        var combiningCount = 0
        while (offset < cell.grapheme.length) {
            val codePoint = cell.grapheme.codePointAt(offset)
            require(WcWidth.width(codePoint) <= 0) { "$label contains multiple spacing code points" }
            require(++combiningCount <= 15) { "$label exceeds the Termux combining limit" }
            offset += Character.charCount(codePoint)
        }
    }

    private fun mapColor(value: TerminalColorSnapshotV1, defaultIndex: Int, label: String): Int = when (value) {
        TerminalColorSnapshotV1.Default -> defaultIndex
        is TerminalColorSnapshotV1.Palette -> value.index.also {
            require(it in 0..255) { "$label palette index is invalid" }
        }
        is TerminalColorSnapshotV1.Rgb -> (0xff00_0000.toInt() or value.value).also {
            require(value.value in 0..0x00ff_ffff) { "$label RGB value is invalid" }
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

    private fun defaultStyle(): Long = TerminalRestorableState.encodeStyle(
        TextStyle.COLOR_INDEX_FOREGROUND,
        TextStyle.COLOR_INDEX_BACKGROUND,
        0,
    )

    private fun mapPalette(palette: TerminalPaletteSnapshotV1): IntArray {
        require(palette.colorsRgb.size == 256) { "Transcript palette must contain exactly 256 colors" }
        return IntArray(TextStyle.NUM_INDEXED_COLORS).also { result ->
            palette.colorsRgb.forEachIndexed { index, color ->
                result[index] = opaqueRgb(color, "palette[$index]")
            }
            result[TextStyle.COLOR_INDEX_FOREGROUND] = opaqueRgb(
                palette.defaultForegroundRgb,
                "default foreground",
            )
            result[TextStyle.COLOR_INDEX_BACKGROUND] = opaqueRgb(
                palette.defaultBackgroundRgb,
                "default background",
            )
            result[TextStyle.COLOR_INDEX_CURSOR] = opaqueRgb(palette.defaultCursorRgb, "default cursor")
        }
    }

    private fun opaqueRgb(value: Int, label: String): Int {
        require(value in 0..0x00ff_ffff) { "$label RGB value is invalid" }
        return 0xff00_0000.toInt() or value
    }
}
