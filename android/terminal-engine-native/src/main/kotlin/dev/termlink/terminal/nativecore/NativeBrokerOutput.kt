package dev.termlink.terminal.nativecore

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Collections

enum class NativeBrokerOutcomeV1(val code: Int) {
    BYTES(0), DENIED(1), UNAVAILABLE(2), UNSUPPORTED(3), OVER_LIMIT(4),
}

/** One path-free parser identity and its capture result. Close after replay to clear owned bytes. */
class NativeBrokerCaptureV1(identity: ByteArray, val outcome: NativeBrokerOutcomeV1, bytes: ByteArray = byteArrayOf()) : AutoCloseable {
    init {
        require(identity.size == 96) { "Broker identity must be 96 bytes" }
        require(bytes.size <= MAX_CAPTURE_BYTES && (outcome == NativeBrokerOutcomeV1.BYTES || bytes.isEmpty())) {
            "Broker capture exceeds its bounds"
        }
        val data = ByteBuffer.wrap(identity).order(ByteOrder.LITTLE_ENDIAN)
        require(data.getInt(0) == TERMLINK_TERMINAL_ABI_VERSION && data.getInt(4) == 96 &&
            data.getInt(8) == 1 && data.getInt(12) in listOf('f'.code, 't'.code, 's'.code) &&
            data.getInt(76) in 0..1 && data.getLong(80) == 0L && data.getLong(88) == 0L &&
            data.getLong(16) in 0 until Long.MAX_VALUE - 1) {
            "Malformed redacted broker identity"
        }
    }

    private val ownedIdentity: ByteArray
    private val ownedBytes: ByteArray
    init {
        val copiedIdentity = identity.copyOf()
        try {
            ownedBytes = bytes.copyOf()
            ownedIdentity = copiedIdentity
        } catch (error: Throwable) {
            copiedIdentity.fill(0)
            throw error
        }
    }
    private var closed = false
    internal val sourceSequence: Long
    internal val sourceOffsetEnd: Long
    internal val ordinal: Long

    init {
        val data = ByteBuffer.wrap(ownedIdentity).order(ByteOrder.LITTLE_ENDIAN)
        sourceSequence = data.getLong(16)
        sourceOffsetEnd = data.getInt(24).toLong() and 0xffff_ffffL
        ordinal = data.getInt(28).toLong() and 0xffff_ffffL
    }

    val byteCount: Int get() = synchronized(this) { requireOpen(); ownedBytes.size }
    fun copyIdentity(): ByteArray = synchronized(this) { requireOpen(); ownedIdentity.copyOf() }
    fun copyBytes(): ByteArray = synchronized(this) { requireOpen(); ownedBytes.copyOf() }

    override fun close() = synchronized(this) {
        if (!closed) {
            closed = true
            ownedIdentity.fill(0)
            ownedBytes.fill(0)
        }
    }

    private fun requireOpen() { check(!closed) { "Broker capture is closed" } }

    companion object { const val MAX_CAPTURE_BYTES = 16 * 1024 * 1024 }
}

/** Replay input already captured by the authorized host; close to clear owned output and captures. */
class NativeBrokerOutputV1(sourceSequence: Long, output: ByteArray, captures: List<NativeBrokerCaptureV1>) : AutoCloseable {
    init {
        require(sourceSequence in 0 until Long.MAX_VALUE - 1 && output.size in 1..1024 * 1024 &&
            captures.size <= 64) { "Broker Output exceeds event bounds" }
        var capturedBytes = 0L
        captures.forEachIndexed { index, capture ->
            require(capture.sourceSequence == sourceSequence && capture.ordinal == index.toLong() &&
                capture.sourceOffsetEnd <= output.size) { "Broker capture identity does not match Output" }
            capturedBytes += capture.byteCount
            require(capturedBytes <= 32L * 1024 * 1024) { "Broker captures exceed 32 MiB" }
        }
    }

    val sourceSequence: Long = sourceSequence
    val captures: List<NativeBrokerCaptureV1> = Collections.unmodifiableList(ArrayList(captures))
    private val ownedOutput = output.copyOf()
    private var closed = false

    val outputByteCount: Int get() = synchronized(this) { requireOpen(); ownedOutput.size }
    fun copyOutput(): ByteArray = synchronized(this) { requireOpen(); ownedOutput.copyOf() }

    override fun close() = synchronized(this) {
        if (!closed) {
            closed = true
            ownedOutput.fill(0)
            captures.forEach { it.close() }
        }
    }

    private fun requireOpen() { check(!closed) { "Broker Output is closed" } }
}
