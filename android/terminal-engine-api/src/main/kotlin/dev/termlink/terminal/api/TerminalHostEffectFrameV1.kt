package dev.termlink.terminal.api

import java.util.Collections

/** Host-observed effect kinds currently defined by Host Effect Frame V1. */
enum class TerminalHostEffectKindV1 {
    PTY_REPLY,
}

/**
 * One immutable effect emitted by the terminal authority.
 *
 * The payload is outbound host work. Consumers must not feed it back into the authority as source
 * output. [copyPayload] always returns a new caller-owned array.
 */
class TerminalHostEffectV1(
    val kind: TerminalHostEffectKindV1,
    val sourceSequence: Long,
    val sourceOffsetEnd: Int,
    val ordinal: Long,
    payload: ByteArray,
) {
    private val ownedPayload = payload.copyOf()

    init {
        require(sourceSequence >= 0) { "host effect source sequence must be non-negative" }
        require(sourceOffsetEnd in 1..MAX_SOURCE_OFFSET) {
            "host effect source offset end must be in 1..$MAX_SOURCE_OFFSET"
        }
        require(ordinal == 0L) { "Host Effect Frame V1 ordinal must be zero" }
        require(isCanonicalPtyReply(ownedPayload)) {
            "PTY reply payload is not a canonical Host Effect Frame V1 reply"
        }
    }

    val payloadByteCount: Int
        get() = ownedPayload.size

    fun copyPayload(): ByteArray = ownedPayload.copyOf()

    private companion object {
        const val MAX_SOURCE_OFFSET = 1024 * 1024
    }
}

/** Immutable, renderer-neutral result of decoding a canonical Host Effect Frame V1. */
class TerminalHostEffectFrameV1(
    effects: List<TerminalHostEffectV1>,
) {
    val formatVersion: Int = FORMAT_VERSION
    val effects: List<TerminalHostEffectV1> =
        Collections.unmodifiableList(ArrayList(effects))

    val payloadByteCount: Long = this.effects.sumOf { it.payloadByteCount.toLong() }

    companion object {
        const val FORMAT_VERSION = 1
    }
}

private fun isCanonicalPtyReply(payload: ByteArray): Boolean {
    if (payload.size !in 4..11 || payload[0] != 0x1b.toByte() || payload[1] != '['.code.toByte()) {
        return false
    }
    if (
        payload.contentEquals("\u001b[?1;2c".toByteArray(Charsets.US_ASCII)) ||
        payload.contentEquals("\u001b[>0;0;0c".toByteArray(Charsets.US_ASCII)) ||
        payload.contentEquals("\u001b[0n".toByteArray(Charsets.US_ASCII)) ||
        payload.contentEquals("\u001b[?0n".toByteArray(Charsets.US_ASCII))
    ) {
        return true
    }
    if (payload.last() != 'R'.code.toByte()) return false
    var index = 2
    if (payload[index] == '?'.code.toByte()) index++
    var semicolon = -1
    for (candidate in index until payload.lastIndex) {
        if (payload[candidate] == ';'.code.toByte()) {
            if (semicolon != -1) return false
            semicolon = candidate
        }
    }
    if (semicolon < 0) return false
    return isCanonicalCoordinate(payload, index, semicolon) &&
        isCanonicalCoordinate(payload, semicolon + 1, payload.lastIndex)
}

private fun isCanonicalCoordinate(payload: ByteArray, start: Int, end: Int): Boolean {
    if (start >= end || payload[start] == '0'.code.toByte()) return false
    var value = 0
    for (index in start until end) {
        val byte = payload[index].toInt() and 0xff
        if (byte !in '0'.code..'9'.code) return false
        value = value * 10 + byte - '0'.code
        if (value > 512) return false
    }
    return value in 1..512
}
