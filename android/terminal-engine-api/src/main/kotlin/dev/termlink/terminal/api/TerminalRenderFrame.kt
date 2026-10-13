package dev.termlink.terminal.api

import java.util.Collections

/**
 * Immutable, renderer-neutral projection of the active terminal screen.
 *
 * This is presentation state only. It deliberately contains no parser or restoration state and
 * must not be used as a checkpoint.
 */
class TerminalRenderFrameV1(
    val presentationRevision: Long,
    val nextSourceSequence: Long,
    val size: TerminalSize,
    val activeScreen: TerminalRenderScreenV1,
    /** Global reverse-video mode; renderers invert the frame's effective foreground/background. */
    val reverseVideo: Boolean,
    val cursor: TerminalRenderCursorV1,
    graphemes: List<TerminalRenderGraphemeV1>,
    renditions: List<TerminalRenderRenditionV1>,
    val palette: TerminalRenderPaletteV1,
    hyperlinks: List<TerminalRenderHyperlinkV1>,
    rows: List<TerminalRenderRowV1>,
    val title: String,
) {
    val formatVersion: Int = FORMAT_VERSION
    val graphemes: List<TerminalRenderGraphemeV1> = immutableCopy(graphemes)
    val renditions: List<TerminalRenderRenditionV1> = immutableCopy(renditions)
    val hyperlinks: List<TerminalRenderHyperlinkV1> = immutableCopy(hyperlinks)
    val rows: List<TerminalRenderRowV1> = immutableCopy(rows)

    init {
        require(presentationRevision >= 0) { "presentation revision must be non-negative" }
        require(nextSourceSequence >= 0) { "next source sequence must be non-negative" }
        require(cursor.row in 0 until size.rows) { "cursor row is outside the frame" }
        require(cursor.column in 0 until size.columns) { "cursor column is outside the frame" }
        require(this.rows.size == size.rows) { "row count does not match frame size" }
        require(this.rows.all { it.cells.size == size.columns }) {
            "row width does not match frame size"
        }
    }

    companion object {
        const val FORMAT_VERSION = 1
    }
}

enum class TerminalRenderScreenV1 { PRIMARY, ALTERNATE }

data class TerminalRenderCursorV1(
    val row: Int,
    val column: Int,
    val visible: Boolean,
    val wrapPending: Boolean,
) {
    init {
        require(row >= 0) { "cursor row must be non-negative" }
        require(column >= 0) { "cursor column must be non-negative" }
    }
}

data class TerminalRenderGraphemeV1(
    val text: String,
    val displayWidth: Int,
) {
    init {
        require(text.isNotEmpty()) { "render grapheme must not be empty" }
        require(displayWidth == 1 || displayWidth == 2) {
            "render grapheme display width must be one or two"
        }
    }
}

sealed interface TerminalRenderColorV1 {
    data object Default : TerminalRenderColorV1

    data class Indexed(val index: Int) : TerminalRenderColorV1 {
        init {
            require(index in 0..255) { "indexed render color must be in 0..255" }
        }
    }

    /** RGB encoded as `0xRRGGBB`. */
    data class Rgb(val value: Int) : TerminalRenderColorV1 {
        init {
            require(value in 0..0x00ff_ffff) { "render RGB color must fit in 24 bits" }
        }
    }
}

data class TerminalRenderRenditionV1(
    val foreground: TerminalRenderColorV1,
    val background: TerminalRenderColorV1,
    val bold: Boolean = false,
    val blink: Boolean = false,
    val italic: Boolean = false,
    val inverse: Boolean = false,
    val underline: Boolean = false,
    val conceal: Boolean = false,
    val strikethrough: Boolean = false,
)

/** Current OSC palette/default overrides. A null entry delegates to the renderer theme. */
class TerminalRenderPaletteV1(
    colorsRgb: List<Int?>,
    val defaultForegroundRgb: Int?,
    val defaultBackgroundRgb: Int?,
    val defaultCursorRgb: Int?,
    val selectionBackgroundRgb: Int? = null,
    val foregroundAlias: Int? = null,
    val backgroundAlias: Int? = null,
    val frameForegroundAlias: Int? = null,
    val frameBackgroundAlias: Int? = null,
) {
    val colorsRgb: List<Int?> = immutableCopy(colorsRgb)

    init {
        require(this.colorsRgb.size == COLOR_COUNT) {
            "render palette requires exactly $COLOR_COUNT entries"
        }
        this.colorsRgb.forEachIndexed { index, value -> validateRgb(value, "palette[$index]") }
        validateRgb(defaultForegroundRgb, "default foreground")
        validateRgb(defaultBackgroundRgb, "default background")
        validateRgb(defaultCursorRgb, "default cursor")
        validateRgb(selectionBackgroundRgb, "selection background")
        listOf(foregroundAlias, backgroundAlias, frameForegroundAlias, frameBackgroundAlias).forEachIndexed { index, alias ->
            require(alias == null || alias in 0..257 && alias != 256 + index % 2) { "invalid color alias" }
        }
    }

    private fun validateRgb(value: Int?, field: String) {
        require(value == null || value in 0..0x00ff_ffff) { "$field must fit in 24 bits" }
    }

    private companion object {
        const val COLOR_COUNT = 256
    }
}

data class TerminalRenderHyperlinkV1(
    val reference: Int,
    val uri: String,
) {
    init {
        require(reference in 1..1_024) { "hyperlink reference must be in 1..1024" }
        require(uri.isNotEmpty()) { "hyperlink URI must not be empty" }
    }
}

enum class TerminalLineRendition(val wireValue: Int, val horizontalScale: Int) {
    SINGLE_WIDTH(0, 1), DOUBLE_WIDTH(1, 2), DOUBLE_HEIGHT_TOP(2, 2), DOUBLE_HEIGHT_BOTTOM(3, 2);

    fun columns(physicalColumns: Int): Int = (physicalColumns / horizontalScale).coerceAtLeast(1)
}

class TerminalRenderRowV1(
    val meaningfulEnd: Int,
    val softWrapped: Boolean,
    cells: List<TerminalRenderCellV1>,
    val lineRendition: TerminalLineRendition = TerminalLineRendition.SINGLE_WIDTH,
) {
    val cells: List<TerminalRenderCellV1> = immutableCopy(cells)

    init {
        require(meaningfulEnd in 0..lineRendition.columns(this.cells.size)) { "meaningful end is outside the row" }
        require(!softWrapped || lineRendition == TerminalLineRendition.SINGLE_WIDTH)
    }
}

data class TerminalRenderCellV1(
    /** One-based index into the frame grapheme table, or zero for a wide continuation. */
    val graphemeId: Int,
    /** Zero-based index into the frame rendition table. */
    val renditionId: Int,
    val wideContinuation: Boolean,
    val isProtected: Boolean,
    /** Stable visible hyperlink reference, or zero when the cell is not linked. */
    val hyperlinkReference: Int,
) {
    init {
        require(graphemeId >= 0) { "grapheme ID must be non-negative" }
        require(renditionId >= 0) { "rendition ID must be non-negative" }
        require(wideContinuation == (graphemeId == 0)) {
            "only a wide continuation may use grapheme ID zero"
        }
        require(hyperlinkReference in 0..1_024) {
            "hyperlink reference must be in 0..1024"
        }
    }
}

private fun <T> immutableCopy(values: Collection<T>): List<T> =
    Collections.unmodifiableList(ArrayList(values))
