package dev.termlink.terminal.api

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
    presentationRevision: Long,
    nextSourceSequence: Long,
    size: TerminalSize,
    activeScreen: TerminalRenderScreenV1,
    /** Global reverse-video mode; renderers invert the frame's effective foreground/background. */
    reverseVideo: Boolean,
    cursor: TerminalRenderCursorV2,
    graphemes: List<TerminalRenderGraphemeV1>,
    renditions: List<TerminalRenderRenditionV3>,
    palette: TerminalRenderPaletteV1,
    hyperlinks: List<TerminalRenderHyperlinkV1>,
    rows: List<TerminalRenderRowV1>,
    title: String,
) : TerminalTextFrame(3, TerminalWidthProfile.LEGACY_V1, presentationRevision, nextSourceSequence, size, activeScreen, reverseVideo, cursor, graphemes, renditions, palette, hyperlinks, rows, title) {
    companion object { const val FORMAT_VERSION = 3 }
}
