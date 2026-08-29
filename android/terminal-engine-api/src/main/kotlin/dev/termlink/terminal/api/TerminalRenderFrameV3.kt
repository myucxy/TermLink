package dev.termlink.terminal.api

import java.util.Collections

/** Canonical underline styles carried by the TermLink Render Frame V3 rendition section. */
enum class TerminalRenderUnderlineStyleV3 {
    NONE,
    SINGLE,
    DOUBLE,
    CURLY,
    DOTTED,
    DASHED,
}

/** A Render Frame V3 rendition. */
data class TerminalRenderRenditionV3(
    val foreground: TerminalRenderColorV1,
    val background: TerminalRenderColorV1,
    val underlineColor: TerminalRenderColorV1,
    val bold: Boolean = false,
    val blink: Boolean = false,
    val italic: Boolean = false,
    val inverse: Boolean = false,
    val underlineStyle: TerminalRenderUnderlineStyleV3 = TerminalRenderUnderlineStyleV3.NONE,
    val conceal: Boolean = false,
    val strikethrough: Boolean = false,
    val faint: Boolean = false,
    val overline: Boolean = false,
) {
    /** Whether an underline is present; V3 derives this from [underlineStyle]. */
    val underline: Boolean
        get() = underlineStyle != TerminalRenderUnderlineStyleV3.NONE
}

/**
 * Immutable, renderer-neutral projection of a TermLink Render Frame V3.
 *
 * V3 adds complete underline state, faint, and overline to renditions. Graphemes, palette,
 * hyperlinks, rows, and cells remain wire-compatible with V1, while the cursor remains compatible
 * with V2. Like earlier render frames, this is presentation state and is not a checkpoint.
 */
class TerminalRenderFrameV3(
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
    val formatVersion: Int = FORMAT_VERSION
    val graphemes: List<TerminalRenderGraphemeV1> = immutableV3Copy(graphemes)
    val renditions: List<TerminalRenderRenditionV3> = immutableV3Copy(renditions)
    val hyperlinks: List<TerminalRenderHyperlinkV1> = immutableV3Copy(hyperlinks)
    val rows: List<TerminalRenderRowV1> = immutableV3Copy(rows)

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
        const val FORMAT_VERSION = 3
    }
}

private fun <T> immutableV3Copy(values: Collection<T>): List<T> =
    Collections.unmodifiableList(ArrayList(values))
