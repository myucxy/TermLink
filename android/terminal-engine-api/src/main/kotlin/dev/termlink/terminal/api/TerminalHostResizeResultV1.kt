package dev.termlink.terminal.api

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Ordered observation only. A separate Resize event changes the renderer geometry. */
data class TerminalHostResizeResultV1(
    val sourceSequence: Long,
    val sourceOffsetEnd: Int,
    val ordinal: Long,
    val requestedColumns: Int,
    val requestedRows: Int,
    val actualColumns: Int,
    val actualRows: Int,
    val error: Int,
) {
    init {
        require(sourceSequence > 0 && sourceOffsetEnd in 1..1024 * 1024 && ordinal in 0..0xffff_ffffL)
        require(requestedColumns in 0..512 && requestedRows in 0..512 && (requestedColumns == 0) != (requestedRows == 0))
        require(actualColumns in 0..512 && actualRows in 0..512 && error in 0..8)
        require(error != 0 || (actualColumns > 0 && actualRows > 0 &&
            (requestedColumns == 0 || requestedColumns == actualColumns) && (requestedRows == 0 || requestedRows == actualRows)))
    }

    fun describe(): String {
        val request = if (requestedColumns != 0) "<ESC>[$requestedColumns\u0024|" else "<ESC>[$requestedRows*|"
        val reason = listOf("OK", "RESIZE_UNSUPPORTED", "RESIZE_BUSY", "LOCAL_INPUT_PRIORITY", "RESIZE_EXPIRED",
            "WINDOW_RESIZE_UNSUPPORTED", "NO_DESKTOP_SIZE", "SESSION_UNAVAILABLE", "RESIZE_TIMEOUT").getOrNull(error) ?: "UNKNOWN_RESULT"
        return "终端尺寸请求（规范化） / Terminal resize (normalized) $request: $reason; actual=${actualColumns}x$actualRows; source=$sourceSequence:$sourceOffsetEnd:$ordinal"
    }

    companion object {
        fun decode(bytes: ByteArray): TerminalHostResizeResultV1 {
            require(bytes.size == 28 && bytes[0] == 1.toByte() && bytes[2] == 0.toByte() && bytes[3] == 0.toByte())
            val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            return TerminalHostResizeResultV1(buffer.getLong(4), buffer.getInt(12), buffer.getInt(16).toLong() and 0xffff_ffffL,
                buffer.getShort(20).toInt() and 0xffff, buffer.getShort(22).toInt() and 0xffff,
                buffer.getShort(24).toInt() and 0xffff, buffer.getShort(26).toInt() and 0xffff, bytes[1].toInt() and 0xff)
        }
    }
}
