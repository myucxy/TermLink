package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalRenderCellV1
import dev.termlink.terminal.api.TerminalRenderColorV1
import dev.termlink.terminal.api.TerminalRenderCursorShapeV2
import dev.termlink.terminal.api.TerminalRenderCursorV2
import dev.termlink.terminal.api.TerminalRenderFrameV3
import dev.termlink.terminal.api.TerminalRenderGraphemeV1
import dev.termlink.terminal.api.TerminalRenderHyperlinkV1
import dev.termlink.terminal.api.TerminalRenderPaletteV1
import dev.termlink.terminal.api.TerminalRenderRenditionV3
import dev.termlink.terminal.api.TerminalRenderUnderlineStyleV3
import dev.termlink.terminal.api.TerminalRenderRowV1
import dev.termlink.terminal.api.TerminalRenderScreenV1
import dev.termlink.terminal.api.TerminalSize
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets

/** A deterministic rejection of a malformed or unsupported TermLink Render Frame V3 payload. */
class RenderFrameV3DecodeException internal constructor(message: String) :
    IllegalArgumentException("render frame v3: $message")

/** Strict, allocation-bounded decoder for the canonical TermLink Render Frame V3 wire format. */
object RenderFrameV3Decoder {
    fun decode(bytes: ByteArray): TerminalRenderFrameV3 {
        try {
            return Decoder(bytes).decode()
        } catch (error: RenderFrameV3DecodeException) {
            throw error
        } catch (error: ArithmeticException) {
            fail("integer overflow")
        } catch (error: IndexOutOfBoundsException) {
            fail("truncated data")
        } catch (error: IllegalArgumentException) {
            fail(error.message ?: "invalid value")
        }
    }

    private class Decoder(private val bytes: ByteArray) {
        private val reader = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)

        fun decode(): TerminalRenderFrameV3 {
            if (bytes.size < HEADER_SIZE) fail("header is truncated")
            if (bytes.size > MAX_FRAME_BYTES) fail("frame exceeds 16 MiB")
            if (!bytes.regionMatches(0, MAGIC, 0, MAGIC.size)) fail("bad magic")

            expectU16(8, MAJOR, "major version")
            expectU16(10, MINOR, "minor version")
            expectU16(12, HEADER_SIZE, "header size")
            expectU16(14, DIRECTORY_ENTRY_SIZE, "directory entry size")
            expectU16(16, SECTION_COUNT, "section count")
            val frameFlags = u16(18)
            if (frameFlags and FRAME_FLAGS_MASK.inv() != 0) fail("unknown frame flags")
            val total = u32(20, "total length")
            if (total != bytes.size.toLong()) fail("total length does not match input")
            val revision = u64(24, "presentation revision")
            val nextSequence = u64(32, "next source sequence")
            val columnsLong = u32(40, "columns")
            val rowsLong = u32(44, "rows")
            if (columnsLong !in 1..MAX_COLUMNS.toLong()) fail("columns are out of range")
            if (rowsLong !in 1..MAX_ROWS.toLong()) fail("rows are out of range")
            val cellCountLong = multiply(columnsLong, rowsLong, "cell count")
            if (cellCountLong > MAX_CELLS) fail("cell count exceeds the hard limit")
            val columns = checkedInt(columnsLong, "columns")
            val rowCount = checkedInt(rowsLong, "rows")
            val cellCount = checkedInt(cellCountLong, "cell count")

            val cursorRow = checkedInt(u32(48, "cursor row"), "cursor row")
            val cursorColumn = checkedInt(u32(52, "cursor column"), "cursor column")
            if (cursorRow !in 0 until rowCount || cursorColumn !in 0 until columns) {
                fail("cursor is outside the frame")
            }
            val cursorFlags = u32(56, "cursor flags")
            if (cursorFlags and CURSOR_FLAGS_MASK.toLong().inv() != 0L) {
                fail("unknown cursor flags")
            }
            if (cursorFlags and CURSOR_FLAG_WRAP != 0L && cursorColumn != columns - 1) {
                fail("wrap-pending cursor must be in the last column")
            }
            expectU32(60, WIDTH_PROFILE, "width profile")
            expectU32(64, DIRECTORY_OFFSET, "directory offset")
            val cursorShape = when (u8(68)) {
                0 -> TerminalRenderCursorShapeV2.BLOCK
                1 -> TerminalRenderCursorShapeV2.UNDERLINE
                2 -> TerminalRenderCursorShapeV2.BAR
                else -> fail("cursor shape is invalid")
            }
            val cursorBlinking = when (u8(69)) {
                0 -> false
                1 -> true
                else -> fail("cursor blinking flag is invalid")
            }
            expectU16(70, 0, "cursor reserved field")
            if (u64(72, "required features") != REQUIRED_FEATURES) {
                fail("unsupported required features")
            }
            expectU64(80, 0, "header reserved field")
            val payloadCrc = u32(88, "payload CRC32C")
            val headerCrc = u32(92, "header CRC32C")
            if (crc32c(0, HEADER_CRC_END) != headerCrc) fail("header CRC32C mismatch")
            if (crc32c(HEADER_SIZE, bytes.size) != payloadCrc) fail("payload CRC32C mismatch")

            val sections = decodeDirectory(cellCountLong)
            val graphemes = decodeGraphemes(sections[0], cellCount)
            val renditions = decodeRenditions(sections[1], cellCount)
            val palette = decodePalette(sections[2])
            val hyperlinks = decodeHyperlinks(sections[3])
            val rows = decodeRows(
                section = sections[4],
                columns = columns,
                rowCount = rowCount,
                cellCount = cellCount,
                graphemes = graphemes,
                renditions = renditions,
                hyperlinks = hyperlinks,
            )
            val title = decodeTitle(sections[5])

            return TerminalRenderFrameV3(
                presentationRevision = revision,
                nextSourceSequence = nextSequence,
                size = TerminalSize(columns, rowCount),
                activeScreen = if (frameFlags and FRAME_FLAG_ALTERNATE != 0) {
                    TerminalRenderScreenV1.ALTERNATE
                } else {
                    TerminalRenderScreenV1.PRIMARY
                },
                reverseVideo = frameFlags and FRAME_FLAG_REVERSE_VIDEO != 0,
                cursor = TerminalRenderCursorV2(
                    row = cursorRow,
                    column = cursorColumn,
                    visible = cursorFlags and CURSOR_FLAG_VISIBLE != 0L,
                    wrapPending = cursorFlags and CURSOR_FLAG_WRAP != 0L,
                    shape = cursorShape,
                    blinking = cursorBlinking,
                ),
                graphemes = graphemes,
                renditions = renditions,
                palette = palette,
                hyperlinks = hyperlinks,
                rows = rows,
                title = title,
            )
        }

        private fun decodeDirectory(cellCount: Long): List<Section> {
            val directoryEnd = add(
                DIRECTORY_OFFSET.toLong(),
                multiply(SECTION_COUNT.toLong(), DIRECTORY_ENTRY_SIZE.toLong(), "directory size"),
                "directory end",
            )
            if (directoryEnd > bytes.size.toLong()) fail("directory is truncated")
            var expectedOffset = align8(directoryEnd, "first section offset")
            requireZeroRange(directoryEnd, expectedOffset, "directory padding")

            val result = ArrayList<Section>(SECTION_COUNT)
            for (index in 0 until SECTION_COUNT) {
                val base = checkedInt(
                    add(
                        DIRECTORY_OFFSET.toLong(),
                        multiply(index.toLong(), DIRECTORY_ENTRY_SIZE.toLong(), "directory entry"),
                        "directory entry",
                    ),
                    "directory entry",
                )
                val expectedTag = index + 1L
                if (u32(base, "section tag") != expectedTag) fail("section tags must be exactly 1..6")
                val expectedSectionVersion = if (expectedTag.toInt() == TAG_RENDITIONS) {
                    RENDITION_SECTION_VERSION
                } else {
                    SECTION_VERSION
                }
                expectU16(base + 4, expectedSectionVersion, "section version")
                expectU16(base + 6, SECTION_CRITICAL, "section flags")
                val offset = u32(base + 8, "section offset")
                val length = u32(base + 12, "section length")
                val count = u32(base + 16, "section logical count")
                val recordSize = u32(base + 20, "section record size")
                if (offset != expectedOffset) fail("section $expectedTag offset is not canonical")
                val end = add(offset, length, "section $expectedTag end")
                val paddedEnd = align8(end, "section $expectedTag padding")
                if (paddedEnd > bytes.size.toLong()) fail("section $expectedTag is outside the frame")
                requireZeroRange(end, paddedEnd, "section $expectedTag padding")

                when (expectedTag.toInt()) {
                    TAG_GRAPHEMES -> {
                        if (recordSize != GRAPHEME_RECORD_SIZE.toLong()) {
                            fail("grapheme record size must be 8")
                        }
                        if (count !in 1..cellCount) fail("grapheme count is out of range")
                    }
                    TAG_RENDITIONS -> {
                        if (recordSize != RENDITION_RECORD_SIZE.toLong()) {
                            fail("rendition record size must be 16")
                        }
                        if (count !in 1..cellCount) fail("rendition count is out of range")
                    }
                    TAG_PALETTE -> {
                        if (count != PALETTE_COUNT.toLong() || recordSize != 4L) {
                            fail("palette directory metadata is invalid")
                        }
                    }
                    TAG_HYPERLINKS -> {
                        if (count !in 0..MAX_HYPERLINKS.toLong() || recordSize != LINK_RECORD_SIZE.toLong()) {
                            fail("hyperlink directory metadata is invalid")
                        }
                    }
                    TAG_ROWS -> {
                        if (count != cellCount || recordSize != 0L) {
                            fail("rows directory metadata is invalid")
                        }
                    }
                    TAG_TITLE -> {
                        if (count != length || recordSize != 0L || length > MAX_TITLE_BYTES) {
                            fail("title directory metadata is invalid")
                        }
                    }
                }
                result += Section(
                    tag = expectedTag.toInt(),
                    offset = checkedInt(offset, "section offset"),
                    length = checkedInt(length, "section length"),
                    count = checkedInt(count, "section logical count"),
                )
                expectedOffset = paddedEnd
            }
            if (expectedOffset != bytes.size.toLong()) fail("frame has trailing or missing bytes")
            return result
        }

        private fun decodeGraphemes(
            section: Section,
            cellCount: Int,
        ): List<TerminalRenderGraphemeV1> {
            val recordsBytes = multiply(
                section.count.toLong(),
                GRAPHEME_RECORD_SIZE.toLong(),
                "grapheme records",
            )
            if (recordsBytes > section.length.toLong()) fail("grapheme records are truncated")
            var expectedStringOffset = recordsBytes
            var previousBytes: ByteArray? = null
            val result = ArrayList<TerminalRenderGraphemeV1>(section.count)
            repeat(section.count) { index ->
                val record = section.absolute(
                    multiply(index.toLong(), GRAPHEME_RECORD_SIZE.toLong(), "grapheme record"),
                    GRAPHEME_RECORD_SIZE,
                )
                val stringOffset = u32(record, "grapheme UTF-8 offset")
                val length = u16(record + 4)
                val width = u8(record + 6)
                val reserved = u8(record + 7)
                if (stringOffset != expectedStringOffset) fail("grapheme strings are not contiguous")
                if (length !in 1..MAX_GRAPHEME_BYTES) fail("grapheme UTF-8 length is out of range")
                if (width != 1 && width != 2) fail("grapheme width is invalid")
                if (reserved != 0) fail("grapheme reserved byte is nonzero")
                val end = add(stringOffset, length.toLong(), "grapheme UTF-8 end")
                if (end > section.length.toLong()) fail("grapheme UTF-8 bytes are truncated")
                val raw = bytes.copyOfRange(
                    section.absolute(stringOffset, length),
                    section.absolute(end, 0),
                )
                previousBytes?.let { previous ->
                    if (compareUnsigned(previous, raw) >= 0) {
                        fail("grapheme table is not strictly sorted")
                    }
                }
                result += TerminalRenderGraphemeV1(decodeUtf8(raw), width)
                previousBytes = raw
                expectedStringOffset = end
            }
            if (expectedStringOffset != section.length.toLong()) fail("grapheme section has unused bytes")
            if (result.size > cellCount) fail("grapheme count exceeds cell count")
            return result
        }

        private fun decodeRenditions(
            section: Section,
            cellCount: Int,
        ): List<TerminalRenderRenditionV3> {
            val expectedLength = multiply(
                section.count.toLong(),
                RENDITION_RECORD_SIZE.toLong(),
                "rendition section length",
            )
            if (expectedLength != section.length.toLong()) fail("rendition section length is invalid")
            var previousOffset: Int? = null
            val result = ArrayList<TerminalRenderRenditionV3>(section.count)
            repeat(section.count) { index ->
                val offset = section.absolute(
                    multiply(index.toLong(), RENDITION_RECORD_SIZE.toLong(), "rendition record"),
                    RENDITION_RECORD_SIZE,
                )
                previousOffset?.let { previous ->
                    if (compareRangeUnsigned(previous, offset, RENDITION_RECORD_SIZE) >= 0) {
                        fail("rendition table is not strictly sorted")
                    }
                }
                val flags = u32(offset, "rendition flags")
                if (flags and RENDITION_FLAGS_MASK.toLong().inv() != 0L) {
                    fail("unknown rendition flags")
                }
                val styleValue = ((flags ushr RENDITION_STYLE_SHIFT) and RENDITION_STYLE_MASK).toInt()
                val underlineStyle = when (styleValue) {
                    0 -> TerminalRenderUnderlineStyleV3.NONE
                    1 -> TerminalRenderUnderlineStyleV3.SINGLE
                    2 -> TerminalRenderUnderlineStyleV3.DOUBLE
                    3 -> TerminalRenderUnderlineStyleV3.CURLY
                    4 -> TerminalRenderUnderlineStyleV3.DOTTED
                    5 -> TerminalRenderUnderlineStyleV3.DASHED
                    else -> fail("underline style is invalid")
                }
                val underlinePresent = flags and RENDITION_UNDERLINE_PRESENT != 0L
                if (underlinePresent != (underlineStyle != TerminalRenderUnderlineStyleV3.NONE)) {
                    fail("underline presence and style disagree")
                }
                result += TerminalRenderRenditionV3(
                    foreground = decodeColor(u32(offset + 4, "foreground color")),
                    background = decodeColor(u32(offset + 8, "background color")),
                    underlineColor = decodeColor(u32(offset + 12, "underline color")),
                    bold = flags and RENDITION_BOLD != 0L,
                    blink = flags and RENDITION_BLINK != 0L,
                    italic = flags and RENDITION_ITALIC != 0L,
                    inverse = flags and RENDITION_INVERSE != 0L,
                    underlineStyle = underlineStyle,
                    conceal = flags and RENDITION_CONCEAL != 0L,
                    strikethrough = flags and RENDITION_STRIKETHROUGH != 0L,
                    faint = flags and RENDITION_FAINT != 0L,
                    overline = flags and RENDITION_OVERLINE != 0L,
                )
                previousOffset = offset
            }
            if (result.size > cellCount) fail("rendition count exceeds cell count")
            return result
        }

        private fun decodePalette(section: Section): TerminalRenderPaletteV1 {
            if (section.length != PALETTE_BYTES) fail("palette section length is invalid")
            val values = ArrayList<Int?>(PALETTE_COUNT)
            repeat(PALETTE_COUNT) { index ->
                val offset = section.absolute(index.toLong() * 4L, 4)
                val value = u32(offset, "palette value")
                values += when {
                    value == UNSET_RGB -> null
                    value <= MAX_RGB.toLong() -> value.toInt()
                    else -> fail("palette value is invalid")
                }
            }
            return TerminalRenderPaletteV1(
                colorsRgb = values.subList(0, 256),
                defaultForegroundRgb = values[256],
                defaultBackgroundRgb = values[257],
                defaultCursorRgb = values[258],
            )
        }

        private fun decodeHyperlinks(section: Section): List<TerminalRenderHyperlinkV1> {
            val recordsBytes = multiply(
                section.count.toLong(),
                LINK_RECORD_SIZE.toLong(),
                "hyperlink records",
            )
            if (recordsBytes > section.length.toLong()) fail("hyperlink records are truncated")
            var expectedUriOffset = recordsBytes
            var totalUriBytes = 0L
            var previousReference = 0L
            val result = ArrayList<TerminalRenderHyperlinkV1>(section.count)
            repeat(section.count) { index ->
                val record = section.absolute(index.toLong() * LINK_RECORD_SIZE, LINK_RECORD_SIZE)
                val reference = u32(record, "hyperlink reference")
                val uriOffset = u32(record + 4, "hyperlink URI offset")
                val uriLength = u32(record + 8, "hyperlink URI length")
                expectU32(record + 12, 0, "hyperlink reserved field")
                if (reference !in 1..MAX_HYPERLINKS.toLong() || reference <= previousReference) {
                    fail("hyperlinks are not sorted by a valid reference")
                }
                if (uriOffset != expectedUriOffset) fail("hyperlink URIs are not contiguous")
                if (uriLength !in 1..MAX_URI_BYTES.toLong()) fail("hyperlink URI length is out of range")
                val end = add(uriOffset, uriLength, "hyperlink URI end")
                if (end > section.length.toLong()) fail("hyperlink URI bytes are truncated")
                totalUriBytes = add(totalUriBytes, uriLength, "hyperlink URI bytes")
                if (totalUriBytes > MAX_TOTAL_URI_BYTES) fail("hyperlink URI bytes exceed the limit")
                val length = checkedInt(uriLength, "hyperlink URI length")
                val raw = bytes.copyOfRange(
                    section.absolute(uriOffset, length),
                    section.absolute(end, 0),
                )
                result += TerminalRenderHyperlinkV1(reference.toInt(), decodeUtf8(raw))
                previousReference = reference
                expectedUriOffset = end
            }
            if (expectedUriOffset != section.length.toLong()) fail("hyperlink section has unused bytes")
            return result
        }

        private fun decodeRows(
            section: Section,
            columns: Int,
            rowCount: Int,
            cellCount: Int,
            graphemes: List<TerminalRenderGraphemeV1>,
            renditions: List<TerminalRenderRenditionV3>,
            hyperlinks: List<TerminalRenderHyperlinkV1>,
        ): List<TerminalRenderRowV1> {
            val rowBytes = multiply(rowCount.toLong(), ROW_RECORD_SIZE.toLong(), "row records")
            val cellBytes = multiply(cellCount.toLong(), CELL_RECORD_SIZE.toLong(), "cell records")
            val expectedLength = add(ROWS_PREFIX_SIZE.toLong(), add(rowBytes, cellBytes, "rows section length"), "rows section length")
            if (expectedLength != section.length.toLong()) fail("rows section length is invalid")
            expectU32(section.offset, ROW_RECORD_SIZE, "row record size")
            expectU32(section.offset + 4, CELL_RECORD_SIZE, "cell record size")

            val referencedGraphemes = BooleanArray(graphemes.size)
            val referencedRenditions = BooleanArray(renditions.size)
            val knownLinks = hyperlinks.associateBy { it.reference }
            val referencedLinks = HashSet<Int>()
            val rows = ArrayList<TerminalRenderRowV1>(rowCount)
            val cellDataOffset = add(ROWS_PREFIX_SIZE.toLong(), rowBytes, "cell data offset")

            repeat(rowCount) { rowIndex ->
                val rowOffset = section.absolute(
                    add(
                        ROWS_PREFIX_SIZE.toLong(),
                        multiply(rowIndex.toLong(), ROW_RECORD_SIZE.toLong(), "row record"),
                        "row record",
                    ),
                    ROW_RECORD_SIZE,
                )
                val meaningfulEnd = u32(rowOffset, "meaningful end")
                val rowFlags = u32(rowOffset + 4, "row flags")
                if (meaningfulEnd > columns.toLong()) fail("meaningful end is outside the row")
                if (rowFlags and ROW_FLAGS_MASK.toLong().inv() != 0L) fail("unknown row flags")
                val cells = ArrayList<TerminalRenderCellV1>(columns)
                repeat(columns) { column ->
                    val cellIndex = rowIndex.toLong() * columns.toLong() + column.toLong()
                    val cellOffset = section.absolute(
                        add(
                            cellDataOffset,
                            multiply(cellIndex, CELL_RECORD_SIZE.toLong(), "cell record"),
                            "cell record",
                        ),
                        CELL_RECORD_SIZE,
                    )
                    val graphemeId = u32(cellOffset, "cell grapheme ID")
                    val renditionId = u32(cellOffset + 4, "cell rendition ID")
                    val metadata = u32(cellOffset + 8, "cell metadata")
                    if (metadata and CELL_METADATA_MASK.toLong().inv() != 0L) fail("unknown cell metadata")
                    val continuation = metadata and CELL_CONTINUATION != 0L
                    val protected = metadata and CELL_PROTECTED != 0L
                    val hyperlinkReference = ((metadata ushr LINK_SHIFT) and LINK_MASK).toInt()
                    if (continuation != (graphemeId == 0L)) {
                        fail("continuation flag and grapheme ID disagree")
                    }
                    if (graphemeId > graphemes.size.toLong()) fail("cell has a dangling grapheme ID")
                    if (renditionId >= renditions.size.toLong()) fail("cell has a dangling rendition ID")
                    if (hyperlinkReference != 0) {
                        if (knownLinks[hyperlinkReference] == null) fail("cell has a dangling hyperlink reference")
                        referencedLinks += hyperlinkReference
                    }
                    if (graphemeId != 0L) referencedGraphemes[graphemeId.toInt() - 1] = true
                    referencedRenditions[renditionId.toInt()] = true
                    cells += TerminalRenderCellV1(
                        graphemeId = graphemeId.toInt(),
                        renditionId = renditionId.toInt(),
                        wideContinuation = continuation,
                        isProtected = protected,
                        hyperlinkReference = hyperlinkReference,
                    )
                }
                validateWideCells(cells, graphemes, rowIndex)
                rows += TerminalRenderRowV1(
                    meaningfulEnd = meaningfulEnd.toInt(),
                    softWrapped = rowFlags and ROW_SOFT_WRAPPED != 0L,
                    cells = cells,
                )
            }
            if (referencedGraphemes.any { !it }) fail("grapheme table contains an unreferenced entry")
            if (referencedRenditions.any { !it }) fail("rendition table contains an unreferenced entry")
            if (hyperlinks.any { it.reference !in referencedLinks }) {
                fail("hyperlink table contains an unreferenced entry")
            }
            return rows
        }

        private fun validateWideCells(
            cells: List<TerminalRenderCellV1>,
            graphemes: List<TerminalRenderGraphemeV1>,
            rowIndex: Int,
        ) {
            var column = 0
            while (column < cells.size) {
                val cell = cells[column]
                if (cell.wideContinuation) fail("standalone continuation at row $rowIndex column $column")
                val width = graphemes[cell.graphemeId - 1].displayWidth
                if (width == 2) {
                    if (column + 1 >= cells.size) fail("wide lead at the last column")
                    val continuation = cells[column + 1]
                    if (!continuation.wideContinuation) fail("wide lead is missing its continuation")
                    if (
                        continuation.renditionId != cell.renditionId ||
                        continuation.isProtected != cell.isProtected ||
                        continuation.hyperlinkReference != cell.hyperlinkReference
                    ) {
                        fail("wide continuation attributes differ from its lead")
                    }
                    column += 2
                } else {
                    column++
                }
            }
        }

        private fun decodeTitle(section: Section): String {
            if (section.length > MAX_TITLE_BYTES) fail("title exceeds the byte limit")
            return decodeUtf8(bytes.copyOfRange(section.offset, section.offset + section.length))
        }

        private fun decodeColor(packed: Long): TerminalRenderColorV1 {
            val tag = packed and COLOR_TAG_MASK
            val payload = packed and MAX_RGB.toLong()
            return when (tag) {
                COLOR_DEFAULT -> {
                    if (payload != 0L) fail("default color has a nonzero payload")
                    TerminalRenderColorV1.Default
                }
                COLOR_INDEXED -> {
                    if (payload > 255L) fail("indexed color is out of range")
                    TerminalRenderColorV1.Indexed(payload.toInt())
                }
                COLOR_RGB -> TerminalRenderColorV1.Rgb(payload.toInt())
                else -> fail("color tag is invalid")
            }
        }

        private fun decodeUtf8(raw: ByteArray): String {
            val decoder = StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
            return try {
                decoder.decode(ByteBuffer.wrap(raw)).toString()
            } catch (_: java.nio.charset.CharacterCodingException) {
                fail("invalid UTF-8")
            }
        }

        private fun crc32c(start: Int, end: Int): Long {
            var crc = -1
            for (index in start until end) {
                crc = crc xor (bytes[index].toInt() and 0xff)
                repeat(8) {
                    val mask = -(crc and 1)
                    crc = (crc ushr 1) xor (CRC32C_POLYNOMIAL and mask)
                }
            }
            return (crc.inv().toLong() and 0xffff_ffffL)
        }

        private fun requireZeroRange(start: Long, end: Long, field: String) {
            if (start < 0 || end < start || end > bytes.size.toLong()) fail("$field is outside the frame")
            for (index in checkedInt(start, field) until checkedInt(end, field)) {
                if (bytes[index].toInt() != 0) fail("$field is nonzero")
            }
        }

        private fun u8(offset: Int): Int = reader.get(offset).toInt() and 0xff

        private fun u16(offset: Int): Int = reader.getShort(offset).toInt() and 0xffff

        private fun u32(offset: Int, field: String): Long {
            if (offset < 0 || offset.toLong() + 4L > bytes.size.toLong()) fail("$field is truncated")
            return reader.getInt(offset).toLong() and 0xffff_ffffL
        }

        private fun u64(offset: Int, field: String): Long {
            if (offset < 0 || offset.toLong() + 8L > bytes.size.toLong()) fail("$field is truncated")
            val value = reader.getLong(offset)
            if (value < 0) fail("$field exceeds signed 64-bit range")
            return value
        }

        private fun expectU16(offset: Int, expected: Int, field: String) {
            if (u16(offset) != expected) fail("$field is unsupported")
        }

        private fun expectU32(offset: Int, expected: Int, field: String) {
            if (u32(offset, field) != expected.toLong()) fail("$field is invalid")
        }

        private fun expectU64(offset: Int, expected: Long, field: String) {
            if (u64(offset, field) != expected) fail("$field is invalid")
        }

        private fun Section.absolute(relative: Long, byteCount: Int): Int {
            if (relative < 0 || byteCount < 0) fail("section $tag contains a negative range")
            val end = add(relative, byteCount.toLong(), "section $tag range")
            if (end > length.toLong()) fail("section $tag range is truncated")
            return checkedInt(add(offset.toLong(), relative, "section $tag address"), "section $tag address")
        }

        private fun compareRangeUnsigned(first: Int, second: Int, length: Int): Int {
            repeat(length) { index ->
                val difference = (bytes[first + index].toInt() and 0xff) -
                    (bytes[second + index].toInt() and 0xff)
                if (difference != 0) return difference
            }
            return 0
        }
    }

    private data class Section(
        val tag: Int,
        val offset: Int,
        val length: Int,
        val count: Int,
    )

    private fun ByteArray.regionMatches(
        thisOffset: Int,
        other: ByteArray,
        otherOffset: Int,
        length: Int,
    ): Boolean {
        if (thisOffset < 0 || otherOffset < 0 || length < 0 ||
            thisOffset + length > size || otherOffset + length > other.size
        ) return false
        repeat(length) { if (this[thisOffset + it] != other[otherOffset + it]) return false }
        return true
    }

    private fun compareUnsigned(first: ByteArray, second: ByteArray): Int {
        val common = minOf(first.size, second.size)
        repeat(common) { index ->
            val difference = (first[index].toInt() and 0xff) - (second[index].toInt() and 0xff)
            if (difference != 0) return difference
        }
        return first.size - second.size
    }

    private fun add(left: Long, right: Long, field: String): Long = try {
        Math.addExact(left, right)
    } catch (_: ArithmeticException) {
        fail("$field overflows")
    }

    private fun multiply(left: Long, right: Long, field: String): Long = try {
        Math.multiplyExact(left, right)
    } catch (_: ArithmeticException) {
        fail("$field overflows")
    }

    private fun align8(value: Long, field: String): Long =
        add(value, 7, field) and -8L

    private fun checkedInt(value: Long, field: String): Int {
        if (value !in 0..Int.MAX_VALUE.toLong()) fail("$field exceeds signed 32-bit range")
        return value.toInt()
    }

    private fun fail(message: String): Nothing = throw RenderFrameV3DecodeException(message)

    private const val HEADER_SIZE = 96
    private const val DIRECTORY_ENTRY_SIZE = 24
    private const val DIRECTORY_OFFSET = 96
    private const val SECTION_COUNT = 6
    private const val HEADER_CRC_END = 92
    private const val MAX_FRAME_BYTES = 16 * 1024 * 1024
    private const val MAX_COLUMNS = 512
    private const val MAX_ROWS = 512
    private const val MAX_CELLS = 262_144L
    private const val MAJOR = 3
    private const val MINOR = 0
    private const val SECTION_VERSION = 1
    private const val RENDITION_SECTION_VERSION = 2
    private const val SECTION_CRITICAL = 1
    private const val WIDTH_PROFILE = 1
    private const val REQUIRED_FEATURES = 0x7fL
    private const val FRAME_FLAG_ALTERNATE = 1
    private const val FRAME_FLAG_REVERSE_VIDEO = 2
    private const val FRAME_FLAGS_MASK = FRAME_FLAG_ALTERNATE or FRAME_FLAG_REVERSE_VIDEO
    private const val CURSOR_FLAG_VISIBLE = 1L
    private const val CURSOR_FLAG_WRAP = 2L
    private const val CURSOR_FLAGS_MASK = 3
    private const val TAG_GRAPHEMES = 1
    private const val TAG_RENDITIONS = 2
    private const val TAG_PALETTE = 3
    private const val TAG_HYPERLINKS = 4
    private const val TAG_ROWS = 5
    private const val TAG_TITLE = 6
    private const val GRAPHEME_RECORD_SIZE = 8
    private const val MAX_GRAPHEME_BYTES = 14
    private const val RENDITION_RECORD_SIZE = 16
    private const val RENDITION_BOLD = 1L
    private const val RENDITION_BLINK = 2L
    private const val RENDITION_ITALIC = 4L
    private const val RENDITION_INVERSE = 8L
    private const val RENDITION_UNDERLINE_PRESENT = 16L
    private const val RENDITION_CONCEAL = 32L
    private const val RENDITION_STRIKETHROUGH = 64L
    private const val RENDITION_FAINT = 128L
    private const val RENDITION_OVERLINE = 256L
    private const val RENDITION_STYLE_SHIFT = 9
    private const val RENDITION_STYLE_MASK = 7L
    private const val RENDITION_FLAGS_MASK = 0x0fff
    private const val COLOR_TAG_MASK = 0xff00_0000L
    private const val COLOR_DEFAULT = 0x0000_0000L
    private const val COLOR_INDEXED = 0x0100_0000L
    private const val COLOR_RGB = 0x0200_0000L
    private const val MAX_RGB = 0x00ff_ffff
    private const val PALETTE_COUNT = 259
    private const val PALETTE_BYTES = PALETTE_COUNT * 4
    private const val UNSET_RGB = 0xffff_ffffL
    private const val LINK_RECORD_SIZE = 16
    private const val MAX_HYPERLINKS = 1_024
    private const val MAX_URI_BYTES = 4_096
    private const val MAX_TOTAL_URI_BYTES = 256 * 1024L
    private const val ROWS_PREFIX_SIZE = 8
    private const val ROW_RECORD_SIZE = 8
    private const val CELL_RECORD_SIZE = 12
    private const val ROW_SOFT_WRAPPED = 1L
    private const val ROW_FLAGS_MASK = 1
    private const val CELL_CONTINUATION = 1L
    private const val CELL_PROTECTED = 2L
    private const val LINK_SHIFT = 2
    private const val LINK_MASK = 0x7ffL
    private const val CELL_METADATA_MASK = 0x1fff
    private const val MAX_TITLE_BYTES = 4_096
    // Reflected Castagnoli polynomial 0x82F63B78 represented as signed Int.
    private const val CRC32C_POLYNOMIAL = -2_097_792_136

    private val MAGIC = byteArrayOf('T'.code.toByte(), 'L'.code.toByte(), 'R'.code.toByte(),
        'F'.code.toByte(), 'R'.code.toByte(), 'M'.code.toByte(), '3'.code.toByte(), 0)
}
