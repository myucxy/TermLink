package dev.termlink.terminal.api

/** V4 explicitly carries width rules and allows bounded Unicode 16 clusters. */
class TerminalRenderFrameV4(
    widthProfile: TerminalWidthProfile,

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
    val mouseReporting: Boolean = false,
    val alternateScroll: Boolean = false,
) : TerminalTextFrame(4, widthProfile, presentationRevision, nextSourceSequence, size, activeScreen, reverseVideo, cursor, graphemes, renditions, palette, hyperlinks, rows, title) {
    companion object { const val FORMAT_VERSION = 4 }
}
