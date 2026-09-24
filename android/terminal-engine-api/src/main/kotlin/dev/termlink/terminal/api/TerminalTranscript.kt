package dev.termlink.terminal.api

/**
 * Renderer-independent, read-only terminal history page.
 *
 * Transcript rows are a projection anchored to [snapshotAnchorSequence]. They are not parser
 * state and must never be installed in a [TerminalEngine] or prepended to its live VT stream.
 */
data class TerminalTranscriptPageV1(
    val formatVersion: Int = FORMAT_VERSION,
    val snapshotAnchorSequence: Long,
    val firstLineId: Long,
    val lastLineId: Long,
    val hasMoreBefore: Boolean,
    val nextBeforeLineId: Long,
    /** Maximum expanded row width in this page; each row retains its exact width in [rows]. */
    val columns: Int,
    val rows: List<TerminalRowSnapshotV1>,
    val styleTable: List<TerminalStyleSnapshotV1>,
    val hyperlinks: List<TerminalHyperlinkSnapshotV1> = emptyList(),
    val palette: TerminalPaletteSnapshotV1,
) {
    companion object {
        const val FORMAT_VERSION = 1
        const val MAX_ROWS = 200
        /** Bounded mobile history window; pagination may continue beyond the resident window. */
        const val MAX_VIEWPORT_ROWS = 1_000
        const val MAX_COLUMNS = TerminalSnapshotV1.MAX_COLUMNS
    }
}
