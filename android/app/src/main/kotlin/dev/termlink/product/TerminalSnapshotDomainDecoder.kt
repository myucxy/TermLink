package dev.termlink.product

import dev.termlink.crypto.TerminalSnapshotCrypto
import dev.termlink.protocol.realtime.v1.SnapshotParserStateV1
import dev.termlink.protocol.realtime.v1.TerminalCharsetStateV1 as WireCharsetState
import dev.termlink.protocol.realtime.v1.TerminalCharsetV1 as WireCharset
import dev.termlink.protocol.realtime.v1.TerminalColorRefV1 as WireColor
import dev.termlink.protocol.realtime.v1.TerminalCursorStateV1 as WireCursor
import dev.termlink.protocol.realtime.v1.TerminalCursorStyleV1 as WireCursorStyle
import dev.termlink.protocol.realtime.v1.TerminalLifecycleV1 as WireLifecycle
import dev.termlink.protocol.realtime.v1.TerminalModesV1 as WireModes
import dev.termlink.protocol.realtime.v1.TerminalMouseEncodingV1 as WireMouseEncoding
import dev.termlink.protocol.realtime.v1.TerminalMouseTrackingV1 as WireMouseTracking
import dev.termlink.protocol.realtime.v1.TerminalRowV1 as WireRow
import dev.termlink.protocol.realtime.v1.TerminalScreenKindV1 as WireScreenKind
import dev.termlink.protocol.realtime.v1.TerminalScreenV1 as WireScreen
import dev.termlink.protocol.realtime.v1.TerminalSnapshotV1 as WireSnapshot
import dev.termlink.protocol.realtime.v1.TerminalStyleV1 as WireStyle
import dev.termlink.protocol.realtime.v1.TerminalUnderlineStyleV1 as WireUnderlineStyle
import dev.termlink.terminal.api.TerminalCellAttributesSnapshotV1
import dev.termlink.terminal.api.TerminalCellSnapshotV1
import dev.termlink.terminal.api.TerminalCharsetSnapshotV1
import dev.termlink.terminal.api.TerminalCharsetStateSnapshotV1
import dev.termlink.terminal.api.TerminalColorSnapshotV1
import dev.termlink.terminal.api.TerminalCursorSnapshotV1
import dev.termlink.terminal.api.TerminalCursorStyleSnapshotV1
import dev.termlink.terminal.api.TerminalHyperlinkSnapshotV1
import dev.termlink.terminal.api.TerminalLifecycleSnapshotV1
import dev.termlink.terminal.api.TerminalModesSnapshotV1
import dev.termlink.terminal.api.TerminalMouseEncodingSnapshotV1
import dev.termlink.terminal.api.TerminalMouseTrackingSnapshotV1
import dev.termlink.terminal.api.TerminalPaletteSnapshotV1
import dev.termlink.terminal.api.TerminalParserStateV1
import dev.termlink.terminal.api.TerminalRowSnapshotV1
import dev.termlink.terminal.api.TerminalScreenKindV1
import dev.termlink.terminal.api.TerminalScreenSnapshotV1
import dev.termlink.terminal.api.TerminalSessionProjectionSnapshotV1
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalSnapshotV1
import dev.termlink.terminal.api.TerminalStyleSnapshotV1
import dev.termlink.terminal.api.TerminalUnderlineStyleSnapshotV1

/** The only protobuf/RLE to renderer-domain conversion for terminal snapshot V1. */
internal object TerminalSnapshotDomainDecoder {
    fun decode(source: WireSnapshot): TerminalSnapshotV1 {
        TerminalSnapshotCrypto.validateSnapshot(source)
        val sequence = source.snapshotSeq.toSupportedLong("snapshot sequence")
        val columns = source.columns
        val rows = source.rows
        val hyperlinks = source.hyperlinkTableList.map { value ->
            TerminalHyperlinkSnapshotV1(value.id, value.uri, value.params)
        }
        return TerminalSnapshotV1(
            formatVersion = source.formatVersion,
            sequence = sequence,
            size = TerminalSize(columns, rows),
            activeScreen = when (source.activeScreen) {
                WireScreenKind.TERMINAL_SCREEN_KIND_V1_PRIMARY -> TerminalScreenKindV1.PRIMARY
                WireScreenKind.TERMINAL_SCREEN_KIND_V1_ALTERNATE -> TerminalScreenKindV1.ALTERNATE
                else -> error("Unsupported terminal screen kind")
            },
            primaryScreen = screen(source.primaryScreen, columns, rows),
            alternateScreen = screen(source.alternateScreen, columns, rows),
            modes = modes(source.modes),
            palette = TerminalPaletteSnapshotV1(
                colorsRgb = source.palette.colorsRgbList,
                defaultForegroundRgb = source.palette.defaultForegroundRgb,
                defaultBackgroundRgb = source.palette.defaultBackgroundRgb,
                defaultCursorRgb = source.palette.defaultCursorRgb,
            ),
            tabStops = source.tabStopsList,
            styleTable = source.styleTableList.map(::style),
            hyperlinks = hyperlinks,
            scrollbackRows = source.scrollbackRowsList.map { row(it, columns) },
            scrollbackFirstLineId = source.scrollbackFirstLineId.toSupportedLong("scrollback first line ID"),
            nextTranscriptLineId = source.nextTranscriptLineId.toSupportedLong("next transcript line ID"),
            scrollbackTruncated = source.scrollbackTruncated,
            title = source.title,
            projection = TerminalSessionProjectionSnapshotV1(
                lifecycle = when (source.projection.lifecycle) {
                    WireLifecycle.TERMINAL_LIFECYCLE_V1_RUNNING -> TerminalLifecycleSnapshotV1.RUNNING
                    WireLifecycle.TERMINAL_LIFECYCLE_V1_INTERRUPTED -> TerminalLifecycleSnapshotV1.INTERRUPTED
                    WireLifecycle.TERMINAL_LIFECYCLE_V1_ENDED -> TerminalLifecycleSnapshotV1.ENDED
                    else -> error("Unsupported terminal lifecycle")
                },
                exitCode = source.projection.takeIf { it.hasExitCode() }?.exitCode,
                observedLocalInputEpoch = source.projection.observedLocalInputEpoch
                    .toSupportedLong("observed local input epoch"),
            ),
            parserState = when (source.parserState) {
                SnapshotParserStateV1.SNAPSHOT_PARSER_STATE_V1_GROUND -> TerminalParserStateV1.GROUND
                else -> error("Unsupported terminal parser state")
            },
        )
    }

    private fun screen(source: WireScreen, columns: Int, rows: Int): TerminalScreenSnapshotV1 =
        TerminalScreenSnapshotV1(
            visibleRows = source.visibleRowsList.map { row(it, columns) },
            cursor = cursor(source.cursor),
            savedCursor = cursor(source.savedCursor),
            scrollTop = source.scrollTop,
            scrollBottomExclusive = Math.addExact(source.scrollBottom, 1),
            marginLeft = source.marginLeft,
            marginRightExclusive = Math.addExact(source.marginRight, 1),
            currentStyleIndex = source.currentStyleIndex,
            pendingWrap = source.pendingWrap,
            lastPrintedGrapheme = source.lastPrintedGrapheme,
            charset = charset(source.charset),
        ).also {
            require(it.scrollBottomExclusive <= rows && it.marginRightExclusive <= columns) {
                "Inclusive terminal margins overflow the declared screen"
            }
        }

    private fun row(source: WireRow, columns: Int): TerminalRowSnapshotV1 {
        val cells = buildList(columns) {
            source.runsList.forEach { run ->
                repeat(run.repeat) {
                    add(
                        TerminalCellSnapshotV1(
                            grapheme = if (!run.wideContinuation && run.grapheme.isEmpty()) " " else run.grapheme,
                            displayWidth = run.displayWidth,
                            styleIndex = run.styleIndex,
                            wideContinuation = run.wideContinuation,
                        ),
                    )
                }
            }
        }
        require(cells.size == columns) { "Expanded terminal row does not match the declared width" }
        return TerminalRowSnapshotV1(
            cells = cells,
            softWrapped = source.softWrapped,
            lineId = source.lineId.toSupportedLong("terminal line ID"),
            sealedAtSequence = source.sealedAtSeq.toSupportedLong("terminal row sequence"),
        )
    }

    private fun cursor(source: WireCursor) = TerminalCursorSnapshotV1(
        column = source.column,
        row = source.row,
        visible = source.visible,
        style = when (source.style) {
            WireCursorStyle.TERMINAL_CURSOR_STYLE_V1_BLOCK -> TerminalCursorStyleSnapshotV1.BLOCK
            WireCursorStyle.TERMINAL_CURSOR_STYLE_V1_UNDERLINE -> TerminalCursorStyleSnapshotV1.UNDERLINE
            WireCursorStyle.TERMINAL_CURSOR_STYLE_V1_BAR -> TerminalCursorStyleSnapshotV1.BAR
            else -> error("Unsupported terminal cursor style")
        },
        blink = source.blink,
        renditionStyleIndex = source.renditionStyleIndex,
        charset = charset(source.charset),
        originMode = source.originMode,
        pendingWrap = source.pendingWrap,
    )

    private fun charset(source: WireCharsetState) = TerminalCharsetStateSnapshotV1(
        g0 = charset(source.g0),
        g1 = charset(source.g1),
        g2 = charset(source.g2),
        g3 = charset(source.g3),
        gl = source.gl,
        gr = source.gr,
    )

    private fun charset(source: WireCharset): TerminalCharsetSnapshotV1 = when (source) {
        WireCharset.TERMINAL_CHARSET_V1_ASCII -> TerminalCharsetSnapshotV1.ASCII
        WireCharset.TERMINAL_CHARSET_V1_DEC_SPECIAL_GRAPHICS -> TerminalCharsetSnapshotV1.DEC_SPECIAL_GRAPHICS
        WireCharset.TERMINAL_CHARSET_V1_UK -> TerminalCharsetSnapshotV1.UK
        WireCharset.TERMINAL_CHARSET_V1_LATIN_1 -> TerminalCharsetSnapshotV1.LATIN_1
        else -> error("Unsupported terminal charset")
    }

    private fun modes(source: WireModes) = TerminalModesSnapshotV1(
        autoWrap = source.autoWrap,
        origin = source.origin,
        insert = source.insert,
        newline = source.newline,
        reverseVideo = source.reverseVideo,
        applicationCursor = source.applicationCursor,
        applicationKeypad = source.applicationKeypad,
        bracketedPaste = source.bracketedPaste,
        focusReporting = source.focusReporting,
        leftRightMargin = source.leftRightMargin,
        mouseTracking = when (source.mouseTracking) {
            WireMouseTracking.TERMINAL_MOUSE_TRACKING_V1_NONE -> TerminalMouseTrackingSnapshotV1.NONE
            WireMouseTracking.TERMINAL_MOUSE_TRACKING_V1_X10 -> TerminalMouseTrackingSnapshotV1.X10
            WireMouseTracking.TERMINAL_MOUSE_TRACKING_V1_NORMAL -> TerminalMouseTrackingSnapshotV1.NORMAL
            WireMouseTracking.TERMINAL_MOUSE_TRACKING_V1_BUTTON_EVENT -> TerminalMouseTrackingSnapshotV1.BUTTON_EVENT
            WireMouseTracking.TERMINAL_MOUSE_TRACKING_V1_ANY_EVENT -> TerminalMouseTrackingSnapshotV1.ANY_EVENT
            else -> error("Unsupported terminal mouse tracking mode")
        },
        mouseEncoding = when (source.mouseEncoding) {
            WireMouseEncoding.TERMINAL_MOUSE_ENCODING_V1_DEFAULT -> TerminalMouseEncodingSnapshotV1.DEFAULT
            WireMouseEncoding.TERMINAL_MOUSE_ENCODING_V1_UTF8 -> TerminalMouseEncodingSnapshotV1.UTF8
            WireMouseEncoding.TERMINAL_MOUSE_ENCODING_V1_SGR -> TerminalMouseEncodingSnapshotV1.SGR
            WireMouseEncoding.TERMINAL_MOUSE_ENCODING_V1_URXVT -> TerminalMouseEncodingSnapshotV1.URXVT
            WireMouseEncoding.TERMINAL_MOUSE_ENCODING_V1_SGR_PIXELS -> TerminalMouseEncodingSnapshotV1.SGR_PIXELS
            else -> error("Unsupported terminal mouse encoding")
        },
    )

    private fun style(source: WireStyle) = TerminalStyleSnapshotV1(
        foreground = color(source.foreground),
        background = color(source.background),
        underlineColor = color(source.underlineColor),
        attributes = TerminalCellAttributesSnapshotV1(
            bold = source.attributes has (1 shl 0),
            dim = source.attributes has (1 shl 1),
            italic = source.attributes has (1 shl 2),
            underline = source.attributes has (1 shl 3),
            blink = source.attributes has (1 shl 4),
            inverse = source.attributes has (1 shl 5),
            invisible = source.attributes has (1 shl 6),
            strikethrough = source.attributes has (1 shl 7),
            protected = source.attributes has (1 shl 8),
        ),
        underlineStyle = when (source.underlineStyle) {
            WireUnderlineStyle.TERMINAL_UNDERLINE_STYLE_V1_NONE -> TerminalUnderlineStyleSnapshotV1.NONE
            WireUnderlineStyle.TERMINAL_UNDERLINE_STYLE_V1_SINGLE -> TerminalUnderlineStyleSnapshotV1.SINGLE
            WireUnderlineStyle.TERMINAL_UNDERLINE_STYLE_V1_DOUBLE -> TerminalUnderlineStyleSnapshotV1.DOUBLE
            WireUnderlineStyle.TERMINAL_UNDERLINE_STYLE_V1_CURLY -> TerminalUnderlineStyleSnapshotV1.CURLY
            WireUnderlineStyle.TERMINAL_UNDERLINE_STYLE_V1_DOTTED -> TerminalUnderlineStyleSnapshotV1.DOTTED
            WireUnderlineStyle.TERMINAL_UNDERLINE_STYLE_V1_DASHED -> TerminalUnderlineStyleSnapshotV1.DASHED
            else -> error("Unsupported terminal underline style")
        },
        hyperlinkIndex = source.hyperlinkIndexPlusOne.takeIf { it != 0 }?.minus(1),
    )

    private fun color(source: WireColor): TerminalColorSnapshotV1 = when (source.valueCase) {
        WireColor.ValueCase.DEFAULT_COLOR -> TerminalColorSnapshotV1.Default
        WireColor.ValueCase.PALETTE_INDEX -> TerminalColorSnapshotV1.Palette(source.paletteIndex)
        WireColor.ValueCase.RGB -> TerminalColorSnapshotV1.Rgb(source.rgb)
        else -> error("Unsupported terminal color")
    }

    private infix fun Int.has(flag: Int): Boolean = this and flag != 0

    private fun Long.toSupportedLong(label: String): Long {
        require(this >= 0) { "$label exceeds the supported signed range" }
        return this
    }
}
