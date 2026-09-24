package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.*

internal data class NativeTranscriptProjection(
    val frame: TerminalRenderFrameV3,
    val lineIds: List<Long>,
    val anchor: Long?,
    val pageCount: Int,
)

/** Read-only presentation, never a parser checkpoint or a source of live sequence/ACK state. */
internal object NativeTranscriptMapper {
    fun map(pages: List<TerminalTranscriptPageV1>, authoritativeColumns: Int): NativeTranscriptProjection {
        require(authoritativeColumns in 1..TerminalTranscriptPageV1.MAX_COLUMNS)
        require(pages.size <= TerminalTranscriptPageV1.MAX_VIEWPORT_ROWS)
        var rowCount = 0
        pages.forEach {
            require(it.rows.size in 1..TerminalTranscriptPageV1.MAX_ROWS)
            require(it.rows.size <= TerminalTranscriptPageV1.MAX_VIEWPORT_ROWS - rowCount)
            rowCount += it.rows.size
        }
        val anchor = pages.firstOrNull()?.snapshotAnchorSequence
        val palette = pages.firstOrNull()?.palette
        val columns = maxOf(authoritativeColumns, pages.maxOfOrNull { it.columns } ?: 1)
        require(columns in 1..TerminalTranscriptPageV1.MAX_COLUMNS)
        val graphemes = linkedMapOf(TerminalRenderGraphemeV1(" ", 1) to 1)
        val renditions = linkedMapOf(
            TerminalRenderRenditionV3(
                TerminalRenderColorV1.Default, TerminalRenderColorV1.Default, TerminalRenderColorV1.Default,
            ) to 0,
        )
        val blank = TerminalRenderCellV1(1, 0, false, false, 0)
        val rows = mutableListOf<TerminalRenderRowV1>()
        val lineIds = mutableListOf<Long>()
        var previousLineId = 0L
        for (page in pages) {
            require(page.formatVersion == TerminalTranscriptPageV1.FORMAT_VERSION)
            require(page.snapshotAnchorSequence > 0 && page.snapshotAnchorSequence == anchor)
            require(page.palette == palette) { "Transcript pages must share one palette" }
            require(page.columns in 1..TerminalTranscriptPageV1.MAX_COLUMNS)
            require(page.rows.maxOf { it.cells.size } == page.columns)
            require(page.firstLineId > 0 && page.lastLineId >= page.firstLineId)
            require(page.rows.first().lineId == page.firstLineId && page.rows.last().lineId == page.lastLineId)
            require(page.nextBeforeLineId == if (page.hasMoreBefore) page.firstLineId else 0L)
            require(page.styleTable.size in 1..TerminalSnapshotV1.MAX_STYLES)
            require(page.hyperlinks.size <= 1_024)
            page.hyperlinks.forEach {
                require(it.uri.isNotEmpty() && it.uri.encodeToByteArray().size <= 4_096)
            }
            val styleIds = page.styleTable.map { style ->
                style.hyperlinkIndex?.let { require(it in page.hyperlinks.indices) }
                require(style.underlineStyle != TerminalUnderlineStyleSnapshotV1.UNSPECIFIED)
                require(style.attributes.underline == (style.underlineStyle != TerminalUnderlineStyleSnapshotV1.NONE))
                val attrs = style.attributes
                val rendition = TerminalRenderRenditionV3(
                    foreground = color(style.foreground), background = color(style.background),
                    underlineColor = color(style.underlineColor), bold = attrs.bold, blink = attrs.blink,
                    italic = attrs.italic, inverse = attrs.inverse, conceal = attrs.invisible,
                    strikethrough = attrs.strikethrough, faint = attrs.dim,
                    underlineStyle = TerminalRenderUnderlineStyleV3.valueOf(style.underlineStyle.name),
                )
                renditions.getOrPut(rendition) { renditions.size }
            }
            for (row in page.rows) {
                require(row.lineId > previousLineId) { "Transcript line IDs must increase without overlap" }
                require(row.sealedAtSequence in 1..page.snapshotAnchorSequence)
                require(row.cells.isNotEmpty() && row.cells.size <= page.columns)
                var wideStyle: Int? = null
                val cells = row.cells.mapIndexed { index, cell ->
                    require(cell.styleIndex in styleIds.indices)
                    val graphemeId = if (cell.wideContinuation) {
                        require(wideStyle == cell.styleIndex && cell.grapheme.isEmpty() && cell.displayWidth == 0)
                        wideStyle = null
                        0
                    } else {
                        require(wideStyle == null) { "Missing wide continuation" }
                        validateGrapheme(cell)
                        if (cell.displayWidth == 2) {
                            require(index + 1 < row.cells.size) { "Truncated wide cell" }
                            wideStyle = cell.styleIndex
                        }
                        // Width is already resolved by the host core; do not reinterpret it with wcwidth.
                        graphemes.getOrPut(TerminalRenderGraphemeV1(cell.grapheme, cell.displayWidth)) {
                            graphemes.size + 1
                        }
                    }
                    TerminalRenderCellV1(
                        graphemeId, styleIds[cell.styleIndex], cell.wideContinuation,
                        page.styleTable[cell.styleIndex].attributes.protected,
                        // History remains display/copy only, as before; OSC links are never activated.
                        0,
                    )
                }
                require(wideStyle == null)
                rows += TerminalRenderRowV1(row.cells.size, row.softWrapped, cells + List(columns - cells.size) { blank })
                lineIds += row.lineId
                previousLineId = row.lineId
            }
        }
        if (rows.isEmpty()) rows += TerminalRenderRowV1(0, false, List(columns) { blank })
        return NativeTranscriptProjection(
            frame = TerminalRenderFrameV3(
                presentationRevision = 0, nextSourceSequence = 0,
                size = TerminalSize(columns, rows.size), activeScreen = TerminalRenderScreenV1.PRIMARY,
                reverseVideo = false,
                cursor = TerminalRenderCursorV2(0, 0, false, false, TerminalRenderCursorShapeV2.BLOCK, false),
                graphemes = graphemes.keys.toList(), renditions = renditions.keys.toList(),
                palette = TerminalRenderPaletteV1(
                    palette?.colorsRgb ?: List(256) { null }, palette?.defaultForegroundRgb,
                    palette?.defaultBackgroundRgb, palette?.defaultCursorRgb,
                ),
                hyperlinks = emptyList(), rows = rows, title = "",
            ),
            lineIds = lineIds.toList(), anchor = anchor, pageCount = pages.size,
        )
    }

    private fun color(value: TerminalColorSnapshotV1): TerminalRenderColorV1 = when (value) {
        TerminalColorSnapshotV1.Default -> TerminalRenderColorV1.Default
        is TerminalColorSnapshotV1.Palette -> TerminalRenderColorV1.Indexed(value.index)
        is TerminalColorSnapshotV1.Rgb -> TerminalRenderColorV1.Rgb(value.value)
    }

    private fun validateGrapheme(cell: TerminalCellSnapshotV1) {
        require(cell.displayWidth in 1..2 && cell.grapheme.isNotEmpty())
        require(cell.grapheme.encodeToByteArray().size <= 64)
        var offset = 0
        while (offset < cell.grapheme.length) {
            val codePoint = cell.grapheme.codePointAt(offset)
            require(codePoint !in 0..31 && codePoint !in 127..159 && codePoint !in 0xd800..0xdfff) {
                "Transcript contains a control character or unpaired surrogate"
            }
            offset += Character.charCount(codePoint)
        }
    }
}
