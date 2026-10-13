package dev.termlink.terminal.api

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.UUID

/** Bounded TLBROV1 reference. Raw terminal Output remains sensitive; close after resolving. */
class TerminalBrokerOutputReferenceV1 private constructor(
    val sourceSequence: Long,
    val objectId: UUID,
    val captureBytes: Int,
    output: ByteArray,
    sha256: ByteArray,
    inlineCaptures: ByteArray,
) : AutoCloseable {
    private val ownedOutput = output.copyOf()
    private val ownedSha256 = sha256.copyOf()
    private val ownedInline = inlineCaptures.copyOf()
    private var closed = false
    val isInline: Boolean get() = objectId == NIL_UUID

    fun copyOutput(): ByteArray = synchronized(this) { requireOpen(); ownedOutput.copyOf() }
    fun copySha256(): ByteArray = synchronized(this) { requireOpen(); ownedSha256.copyOf() }
    fun copyInlineCaptures(): ByteArray = synchronized(this) { requireOpen(); ownedInline.copyOf() }

    fun encode(): ByteArray = synchronized(this) {
        requireOpen()
        ByteBuffer.allocate(HEADER_BYTES + ownedOutput.size + ownedInline.size).order(ByteOrder.LITTLE_ENDIAN).apply {
            put(MAGIC)
            putInt(HEADER_BYTES)
            putInt(if (isInline) 1 else 0)
            putLong(sourceSequence)
            // UUIDs are RFC 4122 network byte order, independent of little-endian integer fields.
            order(ByteOrder.BIG_ENDIAN)
            putLong(objectId.mostSignificantBits)
            putLong(objectId.leastSignificantBits)
            order(ByteOrder.LITTLE_ENDIAN)
            putInt(captureBytes)
            putInt(ownedOutput.size)
            put(ownedSha256)
            position(HEADER_BYTES)
            put(ownedOutput)
            put(ownedInline)
        }.array()
    }

    override fun close() = synchronized(this) {
        if (!closed) {
            closed = true
            ownedOutput.fill(0)
            ownedSha256.fill(0)
            ownedInline.fill(0)
        }
    }

    private fun requireOpen() { check(!closed) { "Broker Output reference is closed" } }

    companion object {
        const val HEADER_BYTES = 96
        const val MAX_OUTPUT_BYTES = 64 * 1024
        const val MAX_CAPTURE_BYTES = 32 * 1024 * 1024
        const val MAX_INLINE_BYTES = 32 + 64 * 104
        private val MAGIC = byteArrayOf(84, 76, 66, 82, 79, 86, 49, 0)
        private val CAPTURE_MAGIC = byteArrayOf(84, 76, 67, 65, 80, 86, 49, 0)
        private val NIL_UUID = UUID(0, 0)

        fun external(sourceSequence: Long, output: ByteArray, objectId: UUID,
            captureBytes: Int, sha256: ByteArray): TerminalBrokerOutputReferenceV1 {
            validateBounds(sourceSequence, output.size, captureBytes, sha256.size)
            require(objectId != NIL_UUID) { "Broker capture object UUID is empty" }
            return TerminalBrokerOutputReferenceV1(sourceSequence, objectId, captureBytes,
                output, sha256, byteArrayOf())
        }

        fun inlineFailures(sourceSequence: Long, output: ByteArray,
            canonicalCaptures: ByteArray): TerminalBrokerOutputReferenceV1 {
            validateBounds(sourceSequence, output.size, canonicalCaptures.size, 32)
            validateInlineFailures(canonicalCaptures, sourceSequence, output.size)
            val hash = MessageDigest.getInstance("SHA-256").digest(canonicalCaptures)
            return try {
                TerminalBrokerOutputReferenceV1(sourceSequence, NIL_UUID, canonicalCaptures.size,
                    output, hash, canonicalCaptures)
            } finally { hash.fill(0) }
        }

        fun decode(source: ByteArray): TerminalBrokerOutputReferenceV1 {
            require(source.size in HEADER_BYTES..HEADER_BYTES + MAX_OUTPUT_BYTES + MAX_INLINE_BYTES) {
                "Broker Output reference size is invalid"
            }
            val data = ByteBuffer.wrap(source).order(ByteOrder.LITTLE_ENDIAN)
            require(source.sliceArray(0 until 8).contentEquals(MAGIC) &&
                data.getInt(8) == HEADER_BYTES && source.sliceArray(80 until 96).all { it == 0.toByte() }) {
                "Broker Output reference header is invalid"
            }
            val flags = data.getInt(12)
            val sequence = data.getLong(16)
            val captureLength = data.getInt(40).toLong() and 0xffff_ffffL
            val outputLength = data.getInt(44).toLong() and 0xffff_ffffL
            require(flags in 0..1 && captureLength <= MAX_CAPTURE_BYTES && outputLength <= MAX_OUTPUT_BYTES) {
                "Broker Output reference bounds are invalid"
            }
            validateBounds(sequence, outputLength.toInt(), captureLength.toInt(), 32)
            val uuid = ByteBuffer.wrap(source, 24, 16).order(ByteOrder.BIG_ENDIAN).let {
                UUID(it.long, it.long)
            }
            val inlineLength = if (flags == 1) captureLength.toInt() else 0
            require((flags == 1) == (uuid == NIL_UUID) && inlineLength <= MAX_INLINE_BYTES &&
                source.size.toLong() == HEADER_BYTES + outputLength + inlineLength) {
                "Broker Output reference layout is invalid"
            }
            val sha = source.copyOfRange(48, 80)
            try {
                if (flags == 1) {
                    validateInlineFailures(source, sequence, outputLength.toInt(), HEADER_BYTES + outputLength.toInt())
                    val inlineForHash = source.copyOfRange(HEADER_BYTES + outputLength.toInt(), source.size)
                    try {
                        val actual = MessageDigest.getInstance("SHA-256").digest(inlineForHash)
                        try { require(MessageDigest.isEqual(sha, actual)) { "Broker inline capture hash mismatch" } }
                        finally { actual.fill(0) }
                    } finally { inlineForHash.fill(0) }
                }
                val rawOutput = source.copyOfRange(HEADER_BYTES, HEADER_BYTES + outputLength.toInt())
                try {
                    val inline = source.copyOfRange(HEADER_BYTES + outputLength.toInt(), source.size)
                    try {
                        return TerminalBrokerOutputReferenceV1(sequence, uuid, captureLength.toInt(),
                            rawOutput, sha, inline)
                    } finally { inline.fill(0) }
                } finally { rawOutput.fill(0) }
            } finally { sha.fill(0) }
        }

        private fun validateBounds(sequence: Long, outputLength: Int, captureLength: Int, hashLength: Int) {
            require(sequence in 1 until Long.MAX_VALUE && outputLength in 1..MAX_OUTPUT_BYTES &&
                captureLength in 136..MAX_CAPTURE_BYTES && hashLength == 32) {
                "Broker Output reference identity or size is invalid"
            }
        }

        private fun validateInlineFailures(bytes: ByteArray, sequence: Long, outputLength: Int,
            start: Int = 0) {
            val length = bytes.size - start
            require(length in 136..MAX_INLINE_BYTES) { "Broker inline failure object size is invalid" }
            val data = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            require(bytes.copyOfRange(start, start + 8).contentEquals(CAPTURE_MAGIC) &&
                data.getInt(start + 8) == 32 && data.getLong(start + 16) == sequence &&
                data.getInt(start + 24) == length && data.getInt(start + 28) == 0) {
                "Broker inline failure header is invalid"
            }
            val count = data.getInt(start + 12)
            require(count in 1..64 && length == 32 + count * 104) { "Broker inline failure count is invalid" }
            repeat(count) { index ->
                val record = start + 32 + index * 104
                val offset = data.getInt(record + 24).toLong() and 0xffff_ffffL
                require(data.getInt(record) == 1 && data.getInt(record + 4) == 96 &&
                    data.getInt(record + 8) == 1 &&
                    data.getInt(record + 12) in listOf('f'.code, 't'.code, 's'.code) &&
                    data.getLong(record + 16) == sequence && offset in 1..outputLength.toLong() &&
                    data.getInt(record + 28) == index && data.getInt(record + 76) in 0..1 &&
                    bytes.sliceArray(record + 80 until record + 96).all { it == 0.toByte() } &&
                    data.getInt(record + 96) in 1..4 && data.getInt(record + 100) == 0) {
                    "Broker inline object contains an invalid failure record"
                }
            }
        }
    }
}
