package dev.termlink.terminal.api

import java.util.Collections

/** Layout rules are frozen by the Windows authority for a session. */
enum class TerminalWidthProfile(val wireValue: Int) { LEGACY_V1(1), UNICODE_16(2) }

/** Immutable presentation shared by versioned text frames; never a restorable checkpoint. */
sealed class TerminalTextFrame(
    val formatVersion: Int,
    val widthProfile: TerminalWidthProfile,

    val presentationRevision: Long,
    val nextSourceSequence: Long,
    val size: TerminalSize,
    val activeScreen: TerminalRenderScreenV1,
    /** Global reverse-video mode; renderers invert the frame's effective foreground/background. */
    val reverseVideo: Boolean,
    val cursor: TerminalRenderCursorV2,
    graphemes: List<TerminalRenderGraphemeV1>,
    renditions: List<TerminalRenderRenditionV3>,
    val palette: TerminalRenderPaletteV1,
    hyperlinks: List<TerminalRenderHyperlinkV1>,
    rows: List<TerminalRenderRowV1>,
    val title: String,
) {
    val graphemes: List<TerminalRenderGraphemeV1> = immutableTextCopy(graphemes)
    val renditions: List<TerminalRenderRenditionV3> = immutableTextCopy(renditions)
    val hyperlinks: List<TerminalRenderHyperlinkV1> = immutableTextCopy(hyperlinks)
    val rows: List<TerminalRenderRowV1> = immutableTextCopy(rows)

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

}

private fun <T> immutableTextCopy(values: Collection<T>): List<T> =
    Collections.unmodifiableList(ArrayList(values))
