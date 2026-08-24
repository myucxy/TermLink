package dev.termlink.product

import dev.termlink.crypto.TerminalSnapshotCrypto
import dev.termlink.protocol.realtime.v1.TerminalColorRefV1 as WireColor
import dev.termlink.protocol.realtime.v1.TerminalRowV1 as WireRow
import dev.termlink.protocol.realtime.v1.TerminalStyleV1 as WireStyle
import dev.termlink.protocol.realtime.v1.TerminalTranscriptPageV1 as WireTranscriptPage
import dev.termlink.protocol.realtime.v1.TerminalUnderlineStyleV1 as WireUnderlineStyle
import dev.termlink.terminal.api.TerminalCellAttributesSnapshotV1
import dev.termlink.terminal.api.TerminalCellSnapshotV1
import dev.termlink.terminal.api.TerminalColorSnapshotV1
import dev.termlink.terminal.api.TerminalHyperlinkSnapshotV1
import dev.termlink.terminal.api.TerminalPaletteSnapshotV1
import dev.termlink.terminal.api.TerminalRowSnapshotV1
import dev.termlink.terminal.api.TerminalStyleSnapshotV1
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.api.TerminalUnderlineStyleSnapshotV1

/** The only protobuf/RLE to renderer-independent transcript-domain conversion. */
internal object TerminalTranscriptDomainDecoder {
    fun decode(source: WireTranscriptPage): TerminalTranscriptPageV1 {
        TerminalSnapshotCrypto.validateTranscript(source)

        val rows = source.rowsList.map(::row)
        return TerminalTranscriptPageV1(
            formatVersion = source.formatVersion,
            snapshotAnchorSequence = source.asOfSnapshotSeq.toSupportedLong("snapshot anchor sequence"),
            firstLineId = source.firstLineId.toSupportedLong("first transcript line ID"),
            lastLineId = source.lastLineId.toSupportedLong("last transcript line ID"),
            hasMoreBefore = source.hasMoreBefore,
            nextBeforeLineId = source.nextBeforeLineId.toSupportedLong("next transcript cursor"),
            columns = rows.maxOf { it.cells.size },
            rows = rows,
            styleTable = source.styleTableList.map(::style),
            hyperlinks = source.hyperlinkTableList.map { value ->
                TerminalHyperlinkSnapshotV1(value.id, value.uri, value.params)
            },
            palette = TerminalPaletteSnapshotV1(
                colorsRgb = source.palette.colorsRgbList,
                defaultForegroundRgb = source.palette.defaultForegroundRgb,
                defaultBackgroundRgb = source.palette.defaultBackgroundRgb,
                defaultCursorRgb = source.palette.defaultCursorRgb,
            ),
        )
    }

    private fun row(source: WireRow): TerminalRowSnapshotV1 = TerminalRowSnapshotV1(
        cells = buildList {
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
        },
        softWrapped = source.softWrapped,
        lineId = source.lineId.toSupportedLong("transcript line ID"),
        sealedAtSequence = source.sealedAtSeq.toSupportedLong("transcript row sequence"),
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
