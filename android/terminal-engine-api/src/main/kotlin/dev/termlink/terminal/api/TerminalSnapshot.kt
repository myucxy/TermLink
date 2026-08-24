package dev.termlink.terminal.api

/**
 * Renderer-independent terminal checkpoint. Transport decoding and authentication happen before
 * this boundary; an engine must still validate every field before changing its current renderer.
 */
data class TerminalSnapshotV1(
    val formatVersion: Int = FORMAT_VERSION,
    val sequence: Long,
    val size: TerminalSize,
    val activeScreen: TerminalScreenKindV1,
    val primaryScreen: TerminalScreenSnapshotV1,
    val alternateScreen: TerminalScreenSnapshotV1,
    val modes: TerminalModesSnapshotV1,
    val palette: TerminalPaletteSnapshotV1,
    val tabStops: List<Int>,
    val styleTable: List<TerminalStyleSnapshotV1>,
    val hyperlinks: List<TerminalHyperlinkSnapshotV1> = emptyList(),
    val scrollbackRows: List<TerminalRowSnapshotV1> = emptyList(),
    val scrollbackFirstLineId: Long = 0,
    val nextTranscriptLineId: Long = 0,
    val scrollbackTruncated: Boolean = false,
    val title: String = "",
    val projection: TerminalSessionProjectionSnapshotV1 = TerminalSessionProjectionSnapshotV1(),
    val parserState: TerminalParserStateV1 = TerminalParserStateV1.GROUND,
) {
    companion object {
        const val FORMAT_VERSION = 1
        const val MAX_COLUMNS = 512
        const val MAX_ROWS = 256
        const val MAX_STYLES = 1_024
        const val MAX_TITLE_UTF8_BYTES = 4_096
    }
}

/** A detached, engine-owned checkpoint that has passed all importer validation. */
interface PreparedTerminalCheckpoint {
    val sequence: Long
    val size: TerminalSize
    val projection: TerminalSessionProjectionSnapshotV1
}

/** Prepares and installs checkpoints without exposing implementation-specific emulator state. */
interface CheckpointTerminalEngine : TerminalEngine {
    /**
     * Builds and validates a detached renderer. This method must not change the installed renderer.
     */
    suspend fun prepareSnapshot(snapshot: TerminalSnapshotV1): PreparedTerminalCheckpoint

    /**
     * Atomically installs a checkpoint prepared by this engine. Implementations must reject a
     * checkpoint if the installed renderer changed after prepare completed.
     */
    suspend fun installPrepared(checkpoint: PreparedTerminalCheckpoint): InstalledTerminalCheckpoint
}

data class InstalledTerminalCheckpoint(
    val sequence: Long,
    val size: TerminalSize,
    val projection: TerminalSessionProjectionSnapshotV1,
)

enum class TerminalScreenKindV1 { PRIMARY, ALTERNATE }

enum class TerminalParserStateV1 { GROUND, UNSPECIFIED }

enum class TerminalCursorStyleSnapshotV1 { BLOCK, UNDERLINE, BAR, UNSPECIFIED }

enum class TerminalUnderlineStyleSnapshotV1 {
    NONE,
    SINGLE,
    DOUBLE,
    CURLY,
    DOTTED,
    DASHED,
    UNSPECIFIED,
}

enum class TerminalCharsetSnapshotV1 {
    ASCII,
    DEC_SPECIAL_GRAPHICS,
    UK,
    LATIN_1,
    UNSPECIFIED,
}

enum class TerminalMouseTrackingSnapshotV1 {
    NONE,
    X10,
    NORMAL,
    BUTTON_EVENT,
    ANY_EVENT,
    UNSPECIFIED,
}

enum class TerminalMouseEncodingSnapshotV1 {
    DEFAULT,
    UTF8,
    SGR,
    URXVT,
    SGR_PIXELS,
    UNSPECIFIED,
}

enum class TerminalLifecycleSnapshotV1 { RUNNING, INTERRUPTED, ENDED, UNSPECIFIED }

sealed interface TerminalColorSnapshotV1 {
    data object Default : TerminalColorSnapshotV1

    data class Palette(val index: Int) : TerminalColorSnapshotV1

    /** Opaque-free RGB encoded as 0xRRGGBB. */
    data class Rgb(val value: Int) : TerminalColorSnapshotV1
}

data class TerminalPaletteSnapshotV1(
    val colorsRgb: List<Int>,
    val defaultForegroundRgb: Int,
    val defaultBackgroundRgb: Int,
    val defaultCursorRgb: Int,
)

data class TerminalCellAttributesSnapshotV1(
    val bold: Boolean = false,
    val dim: Boolean = false,
    val italic: Boolean = false,
    val underline: Boolean = false,
    val blink: Boolean = false,
    val inverse: Boolean = false,
    val invisible: Boolean = false,
    val strikethrough: Boolean = false,
    val protected: Boolean = false,
)

data class TerminalStyleSnapshotV1(
    val foreground: TerminalColorSnapshotV1,
    val background: TerminalColorSnapshotV1,
    val underlineColor: TerminalColorSnapshotV1 = TerminalColorSnapshotV1.Default,
    val attributes: TerminalCellAttributesSnapshotV1 = TerminalCellAttributesSnapshotV1(),
    val underlineStyle: TerminalUnderlineStyleSnapshotV1 = TerminalUnderlineStyleSnapshotV1.NONE,
    val hyperlinkIndex: Int? = null,
)

data class TerminalHyperlinkSnapshotV1(
    val id: String,
    val uri: String,
    val params: String = "",
)

/** One already-expanded terminal cell. Wide glyphs occupy a lead cell plus one continuation. */
data class TerminalCellSnapshotV1(
    val grapheme: String,
    val displayWidth: Int,
    val styleIndex: Int,
    val wideContinuation: Boolean = false,
)

data class TerminalRowSnapshotV1(
    val cells: List<TerminalCellSnapshotV1>,
    val softWrapped: Boolean = false,
    val lineId: Long = 0,
    val sealedAtSequence: Long = 0,
)

data class TerminalCharsetStateSnapshotV1(
    val g0: TerminalCharsetSnapshotV1 = TerminalCharsetSnapshotV1.ASCII,
    val g1: TerminalCharsetSnapshotV1 = TerminalCharsetSnapshotV1.ASCII,
    val g2: TerminalCharsetSnapshotV1 = TerminalCharsetSnapshotV1.ASCII,
    val g3: TerminalCharsetSnapshotV1 = TerminalCharsetSnapshotV1.ASCII,
    val gl: Int = 0,
    val gr: Int = 0,
)

data class TerminalCursorSnapshotV1(
    val column: Int,
    val row: Int,
    val visible: Boolean = true,
    val style: TerminalCursorStyleSnapshotV1 = TerminalCursorStyleSnapshotV1.BLOCK,
    val blink: Boolean = false,
    val renditionStyleIndex: Int,
    val charset: TerminalCharsetStateSnapshotV1 = TerminalCharsetStateSnapshotV1(),
    val originMode: Boolean = false,
    val pendingWrap: Boolean = false,
)

data class TerminalScreenSnapshotV1(
    val visibleRows: List<TerminalRowSnapshotV1>,
    val cursor: TerminalCursorSnapshotV1,
    val savedCursor: TerminalCursorSnapshotV1,
    val scrollTop: Int,
    val scrollBottomExclusive: Int,
    val marginLeft: Int,
    val marginRightExclusive: Int,
    val currentStyleIndex: Int,
    val pendingWrap: Boolean = false,
    val lastPrintedGrapheme: String = "",
    val charset: TerminalCharsetStateSnapshotV1 = TerminalCharsetStateSnapshotV1(),
)

data class TerminalModesSnapshotV1(
    val autoWrap: Boolean = true,
    val origin: Boolean = false,
    val insert: Boolean = false,
    val newline: Boolean = false,
    val reverseVideo: Boolean = false,
    val applicationCursor: Boolean = false,
    val applicationKeypad: Boolean = false,
    val bracketedPaste: Boolean = false,
    val focusReporting: Boolean = false,
    val leftRightMargin: Boolean = false,
    val mouseTracking: TerminalMouseTrackingSnapshotV1 = TerminalMouseTrackingSnapshotV1.NONE,
    val mouseEncoding: TerminalMouseEncodingSnapshotV1 = TerminalMouseEncodingSnapshotV1.DEFAULT,
)

data class TerminalSessionProjectionSnapshotV1(
    val lifecycle: TerminalLifecycleSnapshotV1 = TerminalLifecycleSnapshotV1.RUNNING,
    val exitCode: Int? = null,
    val observedLocalInputEpoch: Long = 0,
)
