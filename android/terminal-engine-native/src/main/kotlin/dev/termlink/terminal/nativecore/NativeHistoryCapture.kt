package dev.termlink.terminal.nativecore

data class NativeHistoryCaptureInfo(val captureId: Long, val ownerId: Long, val revision: Long,
    val accountedBytes: Long, val leaseMillis: Long)

sealed interface NativeHistoryRead {
    class Page(val bytes: ByteArray) : NativeHistoryRead
    data class Budget(val requiredCells: Long, val requiredUtf8Bytes: Long) : NativeHistoryRead
}

class NativeHistoryBudgetException(val requiredCells: Long, val requiredUtf8Bytes: Long) : IllegalStateException(
    "The next complete history line requires $requiredCells cells and $requiredUtf8Bytes UTF-8 bytes.",
)

/** Explicit owner: close when browsing ends. Reads do not extend the native lease. */
class NativeHistoryCapture internal constructor(private val bridge: NativeBridge, val info: NativeHistoryCaptureInfo,
    val graphicsProfile: Boolean = false) : AutoCloseable {
    private val lock = Any()
    private var closed = false

    internal fun resolveAnchor(line: Long, cell: Long): Long = synchronized(lock) {
        check(!closed) { "History capture is closed" }
        when (val result = bridge.historyResolveAnchor(info, line, cell)) {
            is NativeBridgeResult.Success -> result.value.also { require(it in 0..10000) }
            is NativeBridgeResult.Failure -> throw NativeTerminalException("historyResolveAnchor", NativeTerminalStatus.fromCode(result.statusCode), result.statusCode)
        }
    }

    internal fun viewport(offsetRows: Long): NativeScrollbackTextFrame = synchronized(lock) {
        check(!closed) { "History capture is closed" }
        require(offsetRows in 0..0xffffffffL)
        val raw = when (val result = if (graphicsProfile) bridge.historyViewportV2(info, offsetRows) else bridge.historyViewport(info, offsetRows)) {
            is NativeBridgeResult.Success -> result.value
            is NativeBridgeResult.Failure -> throw NativeTerminalException("historyViewport", NativeTerminalStatus.fromCode(result.statusCode), result.statusCode)
        }
        val maximum = if (graphicsProfile) TERMLINK_RENDER_FRAME_V5_MAX_BYTES else TERMLINK_RENDER_FRAME_V4_MAX_BYTES
        require(raw.payload.size <= maximum && raw.availableRows in 0..10000 && raw.generationRows >= 0 &&
            raw.appliedOffsetRows == offsetRows.coerceAtMost(raw.availableRows)) { "Invalid frozen viewport metadata" }
        val result = if (graphicsProfile) NativeScrollbackFrameV5(RenderFrameV5Decoder.decode(raw.payload), raw.availableRows, raw.generationRows, raw.appliedOffsetRows)
            else NativeScrollbackFrameV4(RenderFrameV4Decoder.decode(raw.payload), raw.availableRows, raw.generationRows, raw.appliedOffsetRows)
        val frame = result.renderFrame
        require(frame.presentationRevision == info.revision && frame.nextSourceSequence == info.revision && !frame.cursor.visible) { "Frozen viewport revision changed" }
        result
    }

    fun read(beforeLineId: Long = -1, maxLines: Int = 128, maxCells: Int = 65536, maxUtf8Bytes: Int = 4 * 1024 * 1024): NativeHistoryPage =
        readBytes(beforeLineId, maxLines, maxCells, maxUtf8Bytes).let {
            if (graphicsProfile) NativeHistoryPageDecoder.decodeV2(it, info.captureId) else NativeHistoryPageDecoder.decode(it, info.captureId)
        }.also {
            require(it.revision == info.revision && it.requestedAnchor == beforeLineId) { "Frozen history page metadata changed" }
        }

    fun readBytes(beforeLineId: Long = -1, maxLines: Int = 128, maxCells: Int = 65536, maxUtf8Bytes: Int = 4 * 1024 * 1024): ByteArray = synchronized(lock) {
        check(!closed) { "History capture is closed" }
        require(beforeLineId >= -1 && maxLines in 1..128 && maxCells in 1..65536 && maxUtf8Bytes in 1..4 * 1024 * 1024)
        when (val result = if (graphicsProfile) bridge.historyPageV2(info, beforeLineId, maxLines, maxCells, maxUtf8Bytes)
            else bridge.historyPage(info, beforeLineId, maxLines, maxCells, maxUtf8Bytes)) {
            is NativeBridgeResult.Failure -> throw NativeTerminalException("historyPage", NativeTerminalStatus.fromCode(result.statusCode), result.statusCode)
            is NativeBridgeResult.Success -> when (val value = result.value) {
                is NativeHistoryRead.Page -> value.bytes.also {
                    require(it.size <= if (graphicsProfile) NativeHistoryPageDecoder.MAX_V2_BYTES else NativeHistoryPageDecoder.MAX_BYTES) { "History page exceeds its profile bound" }
                }
                is NativeHistoryRead.Budget -> {
                    require(value.requiredCells >= 0 && value.requiredUtf8Bytes >= 0) { "Invalid history page budget" }
                    throw NativeHistoryBudgetException(value.requiredCells, value.requiredUtf8Bytes)
                }
            }
        }
    }
    override fun close() = synchronized(lock) {
        if (!closed) {
            closed = true
            val result = bridge.historyRelease(info)
            if (result is NativeBridgeResult.Failure && result.statusCode != NativeTerminalStatus.HISTORY_STALE_CAPTURE.code) {
                throw NativeTerminalException("historyRelease", NativeTerminalStatus.fromCode(result.statusCode), result.statusCode)
            }
        }
    }
}
