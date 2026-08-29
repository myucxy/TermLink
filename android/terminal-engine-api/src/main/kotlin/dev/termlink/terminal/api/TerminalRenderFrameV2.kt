package dev.termlink.terminal.api

import java.util.Collections

/** Cursor shapes carried by the TermLink Render Frame V2 header. */
enum class TerminalRenderCursorShapeV2 {
    BLOCK,
    UNDERLINE,
    BAR,
}

data class TerminalRenderCursorV2(
    val row: Int,
    val column: Int,
    val visible: Boolean,
    val wrapPending: Boolean,
    val shape: TerminalRenderCursorShapeV2,
    val blinking: Boolean,
) {
    init {
        require(row >= 0) { "cursor row must be non-negative" }
        require(column >= 0) { "cursor column must be non-negative" }
    }
}

/**
 * Immutable, renderer-neutral projection of a TermLink Render Frame V2.
 *
 * V2 adds cursor shape and blinking state. Its six payload sections are byte-for-byte compatible
 * with V1, so the established V1 payload DTOs are intentionally reused instead of copied into a
 * second type hierarchy. Like V1, this is presentation state and is not a checkpoint.
 */
class TerminalRenderFrameV2(
    val presentationRevision: Long,
    val nextSourceSequence: Long,
    val size: TerminalSize,
    val activeScreen: TerminalRenderScreenV1,
    /** Global reverse-video mode; renderers invert the frame's effective foreground/background. */
    val reverseVideo: Boolean,
    val cursor: TerminalRenderCursorV2,
    graphemes: List<TerminalRenderGraphemeV1>,
    renditions: List<TerminalRenderRenditionV1>,
    val palette: TerminalRenderPaletteV1,
    hyperlinks: List<TerminalRenderHyperlinkV1>,
    rows: List<TerminalRenderRowV1>,
    val title: String,
) {
    val formatVersion: Int = FORMAT_VERSION
    val graphemes: List<TerminalRenderGraphemeV1> = immutableV2Copy(graphemes)
    val renditions: List<TerminalRenderRenditionV1> = immutableV2Copy(renditions)
    val hyperlinks: List<TerminalRenderHyperlinkV1> = immutableV2Copy(hyperlinks)
    val rows: List<TerminalRenderRowV1> = immutableV2Copy(rows)

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
        const val FORMAT_VERSION = 2
    }
}

private fun <T> immutableV2Copy(values: Collection<T>): List<T> =
    Collections.unmodifiableList(ArrayList(values))
