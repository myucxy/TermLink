package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalTextFrame
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Collections

data class NativeHistoryLine(val id: Long, val sealedRevision: Long, val firstRow: Long, val rowCount: Long,
    val firstCellOffset: Long, val headTruncated: Boolean)

class NativeHistoryPage internal constructor(val revision: Long, val requestedAnchor: Long, val nextBeforeLineId: Long,
    val hasMoreBefore: Boolean, val headTruncated: Boolean, val columns: Int, val cells: Int, val utf8Bytes: Int,
    lines: List<NativeHistoryLine>, frames: List<TerminalTextFrame>) {
    val lines: List<NativeHistoryLine> = Collections.unmodifiableList(ArrayList(lines))
    val frames: List<TerminalTextFrame> = Collections.unmodifiableList(ArrayList(frames))
}

object NativeHistoryPageDecoder {
    const val MAX_BYTES = 16 * 1024 * 1024
    const val MAX_V2_BYTES = 64 * 1024 * 1024
    fun decode(bytes: ByteArray, captureId: Long): NativeHistoryPage = decodePage(bytes, captureId, false)
    fun decodeV2(bytes: ByteArray, captureId: Long): NativeHistoryPage = decodePage(bytes, captureId, true)

    private fun decodePage(bytes: ByteArray, captureId: Long, graphicsProfile: Boolean): NativeHistoryPage {
        fun valid(value: Boolean) = require(value) { "Malformed immutable history page" }
        valid(bytes.size in 96..(if (graphicsProfile) MAX_V2_BYTES else MAX_BYTES) && captureId > 0)
        val view = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        fun u32(offset: Int) = view.getInt(offset).toLong() and 0xffffffffL
        fun u64(offset: Int) = view.getLong(offset)
        val version = if (graphicsProfile) 2 else 1
        valid(bytes.copyOfRange(0,8).contentEquals(byteArrayOf(84,76,72,80,86,(48 + version).toByte(),0,0)) && u32(8) == 0x00600000L + version &&
            u32(12) == bytes.size.toLong() && u64(16) == captureId)
        var crc = -1
        for (index in bytes.indices) {
            if (index in 92..95) continue
            crc = crc xor (bytes[index].toInt() and 255)
            repeat(8) { crc = (crc ushr 1) xor (0x82f63b78.toInt() and -(crc and 1)) }
        }
        valid((crc.inv().toLong() and 0xffffffffL) == u32(92) &&
            u32(72) == (if (graphicsProfile) 1L else 0L) && (76..91).all { bytes[it] == 0.toByte() })
        val lineCount = u32(48)
        val frameCount = u32(52)
        val columns = u32(56)
        val cells = u32(60)
        val utf8 = u32(64)
        val flags = u32(68)
        valid(flags <= 3 && lineCount <= 128 && frameCount <= cells && cells <= 65536 && columns in 1..512 && utf8 <= 4 * 1024 * 1024)
        var offset = 96 + lineCount.toInt() * 40
        valid(offset <= bytes.size && frameCount <= (bytes.size - offset) / 100)
        val revision = u64(24)
        val anchor = u64(32)
        val next = u64(40)
        valid(revision >= 0 && anchor >= -1 && next >= -1)
        val lines = ArrayList<NativeHistoryLine>()
        val ids = HashSet<Long>()
        var totalRows = 0L
        repeat(lineCount.toInt()) { index ->
            val p = 96 + index * 40
            val line = NativeHistoryLine(u64(p),u64(p+8),u32(p+16),u32(p+20),u32(p+24),u32(p+28) == 1L)
            valid(line.id >= 0 && line.id != anchor && ids.add(line.id) && line.sealedRevision in 1..revision &&
                line.firstRow == totalRows && line.rowCount > 0 && u32(p+28) <= 1 && u64(p+32) == 0L &&
                (line.headTruncated || line.firstCellOffset == 0L) && line.firstCellOffset + line.rowCount * columns <= 0xffffffffL)
            totalRows += line.rowCount
            lines += line
        }
        valid(totalRows * columns == cells && (flags and 2 != 0L) == (lines.firstOrNull()?.headTruncated == true) && next == (lines.firstOrNull()?.id ?: anchor))
        valid(lines.isNotEmpty() || (flags == 0L && frameCount == 0L && cells == 0L && utf8 == 0L))
        val frames = ArrayList<TerminalTextFrame>()
        var seenRows = 0L
        var textBytes = 0L
        var lineIndex = 0
        repeat(frameCount.toInt()) {
            valid(bytes.size - offset >= 4)
            val length = u32(offset)
            offset += 4
            valid(length >= 96 && length <= bytes.size - offset)
            val frameBytes = bytes.copyOfRange(offset, offset + length.toInt())
            val frame = if (graphicsProfile) RenderFrameV5Decoder.decode(frameBytes) else RenderFrameV4Decoder.decode(frameBytes)
            offset += length.toInt()
            valid(frame.presentationRevision == revision && frame.nextSourceSequence == revision && frame.size.columns.toLong() == columns &&
                !frame.cursor.visible && !frame.cursor.wrapPending && (frames.isEmpty() || frame.widthProfile == frames[0].widthProfile))
            frame.rows.forEachIndexed { rowIndex, row ->
                while (lineIndex < lines.size && seenRows >= lines[lineIndex].firstRow + lines[lineIndex].rowCount) lineIndex++
                valid(lineIndex < lines.size)
                valid(row.softWrapped == (seenRows + 1 < lines[lineIndex].firstRow + lines[lineIndex].rowCount && rowIndex + 1 < frame.rows.size))
                for (col in 0 until row.meaningfulEnd) {
                    val cell = row.cells[col]
                    if (!cell.wideContinuation) textBytes += frame.graphemes[cell.graphemeId - 1].text.toByteArray(Charsets.UTF_8).size
                }
                seenRows++
            }
            frames += frame
        }
        valid(offset == bytes.size && seenRows == totalRows && textBytes == utf8)
        return NativeHistoryPage(revision, anchor, next, flags and 1 != 0L, flags and 2 != 0L, columns.toInt(), cells.toInt(), utf8.toInt(), lines, frames)
    }
}
