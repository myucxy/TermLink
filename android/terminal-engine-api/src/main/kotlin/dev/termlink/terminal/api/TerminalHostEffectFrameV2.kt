package dev.termlink.terminal.api

import java.util.Collections
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Host-observed effect kinds currently defined by Host Effect Frame V2. */
enum class TerminalHostEffectKindV2 {
    PTY_REPLY,
    RESIZE_REQUEST,
    PLATFORM_ACTION,
}

/** One immutable, source-keyed PTY reply from a Host Effect Frame V2. */
class TerminalHostEffectV2(
    val kind: TerminalHostEffectKindV2,
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
        require(ordinal in 0..MAX_UNSIGNED_INT) {
            "host effect ordinal must fit an unsigned 32-bit integer"
        }
        require(ownedPayload.size in 1..TerminalHostEffectFrameV2.MAX_REPLY_BYTES) {
            "PTY reply payload exceeds the Host Effect Frame V2 reply bound"
        }
        require(when (kind) {
            TerminalHostEffectKindV2.PTY_REPLY -> isCanonicalHostEffectReplyV2(ownedPayload)
            TerminalHostEffectKindV2.RESIZE_REQUEST -> isCanonicalResizeRequest(ownedPayload)
            TerminalHostEffectKindV2.PLATFORM_ACTION -> runCatching { TerminalPlatformAction.decode(ownedPayload) }.isSuccess
        }) {
            "Host effect payload is not canonical for its kind"
        }
    }

    val payloadByteCount: Int
        get() = ownedPayload.size

    fun copyPayload(): ByteArray = ownedPayload.copyOf()

    private companion object {
        const val MAX_SOURCE_OFFSET = 1024 * 1024
        const val MAX_UNSIGNED_INT = 0xffff_ffffL
    }
}

/** Immutable, renderer-neutral result of decoding a canonical Host Effect Frame V2. */
class TerminalHostEffectFrameV2(
    rawFrame: ByteArray,
    effects: List<TerminalHostEffectV2>,
) {
    private val ownedRawFrame = rawFrame.copyOf()

    val formatVersion: Int = FORMAT_VERSION
    val effects: List<TerminalHostEffectV2> =
        Collections.unmodifiableList(ArrayList(effects))

    val payloadByteCount: Long

    init {
        require(ownedRawFrame.size in HEADER_BYTES..MAX_FRAME_BYTES) {
            "Host Effect Frame V2 raw bytes are outside the frame bound"
        }
        require(this.effects.size <= MAX_EFFECTS) {
            "Host Effect Frame V2 effect count exceeds $MAX_EFFECTS"
        }
        require(this.effects.count { it.kind == TerminalHostEffectKindV2.RESIZE_REQUEST } <= 64) {
            "Resize requests exceed the capture bound"
        }
        var totalPayloadBytes = 0L
        var previousSequence: Long? = null
        var previousOffset = 0
        var previousOrdinal = 0L
        this.effects.forEach { effect ->
            totalPayloadBytes = Math.addExact(totalPayloadBytes, effect.payloadByteCount.toLong())
            require(totalPayloadBytes <= MAX_PAYLOAD_BYTES) {
                "Host Effect Frame V2 payload bytes exceed $MAX_PAYLOAD_BYTES"
            }
            val previous = previousSequence
            if (previous == null || effect.sourceSequence > previous || effect.sourceOffsetEnd > previousOffset) {
                if (previous != null) {
                    require(
                        effect.sourceSequence > previous ||
                            (effect.sourceSequence == previous && effect.sourceOffsetEnd > previousOffset),
                    ) { "host effect source keys regress" }
                }
                require(effect.ordinal == 0L) { "a new host effect source key must start at ordinal zero" }
            } else {
                require(effect.sourceSequence == previous && effect.sourceOffsetEnd == previousOffset) {
                    "host effect source keys regress"
                }
                require(previousOrdinal < 0xffff_ffffL && effect.ordinal == previousOrdinal + 1L) {
                    "same-key host effect ordinals must be contiguous"
                }
            }
            previousSequence = effect.sourceSequence
            previousOffset = effect.sourceOffsetEnd
            previousOrdinal = effect.ordinal
        }
        payloadByteCount = totalPayloadBytes
    }

    fun copyRawFrame(): ByteArray = ownedRawFrame.copyOf()

    companion object {
        const val FORMAT_VERSION = 2
        const val HEADER_BYTES = 64
        const val MAX_EFFECTS = 1_398_102
        const val MAX_REPLY_BYTES = 65_536
        const val MAX_PAYLOAD_BYTES = 67_108_864L
        const val MAX_FRAME_BYTES = 123_033_008
    }
}

private fun isCanonicalResizeRequest(payload: ByteArray): Boolean {
    if (payload.size != 8) return false
    val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
    val columns = buffer.int
    val rows = buffer.int
    return columns in 0..512 && rows in 0..512 && (columns == 0) != (rows == 0)
}

private fun isCanonicalHostEffectReplyV2(payload: ByteArray): Boolean {
    if (payload.isEmpty() || payload.size > TerminalHostEffectFrameV2.MAX_REPLY_BYTES) return false
    normalizeTerminalC1Reply(payload)?.let { return isCanonicalHostEffectReplyV2(it) }
    if (payload.contentEquals(ascii("\u001b/Z"))) return true
    return isCanonicalLegacyReply(payload) ||
        isCanonicalKittyReply(payload) ||
        isCanonicalKittyGraphicsReply(payload) ||
        isCanonicalDecrpm(payload) ||
        isCanonicalDecrqss(payload) ||
        isCanonicalXtversion(payload) ||
        isCanonicalXtgettcap(payload) ||
        isCanonicalOscColor(payload) ||
        isCanonicalTextSize(payload) || isCanonicalExtendedQuery(payload) || isTerminalStateReport(payload)
}

private fun normalizeTerminalC1Reply(payload: ByteArray): ByteArray? {
    if (payload.size < 2) return null
    val prefix = payload[0].toInt() and 0xff
    if (prefix !in listOf(0x90, 0x9b, 0x9d, 0x9f)) return null
    val string = prefix != 0x9b
    if (string && (payload.last().toInt() and 0xff) != 0x9c) return null
    val end = payload.size - if (string) 1 else 0
    if ((1 until end).any { (payload[it].toInt() and 0xff) !in 0x20..0x7e }) return null
    return byteArrayOf(0x1b, (prefix - 0x40).toByte()) + payload.copyOfRange(1, end) +
        if (string) byteArrayOf(0x1b, 0x5c) else byteArrayOf()
}

private fun isCanonicalExtendedQuery(payload: ByteArray): Boolean {
    strip(payload, ascii("\u001b[?"), ascii("R"))?.let { body ->
        val fields = String(body, Charsets.US_ASCII).split(';')
        return fields.size == 3 && fields.withIndex().all { (index, value) ->
            val number = value.toIntOrNull()
            number != null && number.toString() == value && number in 1..(if (index == 2) 6 else 512)
        }
    }
    strip(payload, ascii("\u001b["), ascii("*{"))?.let { body ->
        return canonicalUnsigned(body, 0, body.size)?.let { it <= 16384 } == true
    }
    if (payload.contentEqualsAny("\u001bP!|00000000\u001b\\",
            "\u001b[2;1;1;128;128;1;0x", "\u001b[3;1;1;128;128;1;0x",
            "\u001b[?997;1n", "\u001b[?997;2n",
            "\u001b[?13n", "\u001b[?23n", "\u001b[?27;0;0;5n", "\u001b[?53n",
            "\u001b[?57;0n", "\u001b[?70n", "\u001b[?83n")) return true
    strip(payload, ascii("\u001b["), ascii("\"w"))?.let { body ->
        val fields = String(body, Charsets.US_ASCII).split(';')
        return fields.size == 5 && fields.withIndex().all { (index, value) ->
            val number = value.toIntOrNull()
            number != null && number.toString() == value && when (index) {
                0, 1 -> number in 1..512; 2, 3 -> number == 1; else -> number in 1..6
            }
        }
    }
    strip(payload, ascii("\u001bP"), ascii("\u001b\\"))?.let { body ->
        val separator = body.indexOf('!'.code.toByte())
        if (separator < 1 || body.size != separator + 6 || body[separator + 1] != '~'.code.toByte()) return false
        val id = canonicalUnsigned(body, 0, separator) ?: return false
        return id <= 65534 && body.copyOfRange(separator + 2, body.size).all {
            it in '0'.code.toByte()..'9'.code.toByte() || it in 'A'.code.toByte()..'F'.code.toByte()
        }
    }
    return false
}

private fun isCanonicalTextSize(payload: ByteArray): Boolean {
    val body = strip(payload, ascii("\u001b["), ascii("t")) ?: return false
    val fields = body.toString(Charsets.US_ASCII).split(';')
    if (fields.size != 3) return false
    val maximum = when (fields[0]) {
        "4" -> 512 * 1024; "5", "9" -> 32768; "6" -> 1024; "8" -> 512
        else -> return false
    }
    return fields.drop(1).all { field ->
        field.isNotEmpty() && field[0] != '0' && field.all { it in '0'..'9' } &&
            field.toIntOrNull()?.let { it in 1..maximum } == true
    }
}

private fun isCanonicalLegacyReply(payload: ByteArray): Boolean {
    if (
        payload.contentEquals(ascii("\u001b[?1;2c")) ||
        payload.contentEquals(ascii("\u001b[>0;0;0c")) ||
        payload.contentEquals(ascii("\u001b[0n")) ||
        payload.contentEquals(ascii("\u001b[?0n"))
    ) {
        return true
    }
    val body = strip(payload, ascii("\u001b["), ascii("R")) ?: return false
    val coordinateBody = if (body.firstOrNull() == '?'.code.toByte()) body.copyOfRange(1, body.size) else body
    val separator = coordinateBody.indexOf(';'.code.toByte())
    return separator > 0 &&
        separator == coordinateBody.lastIndexOf(';'.code.toByte()) &&
        canonicalCoordinate(coordinateBody, 0, separator) &&
        canonicalCoordinate(coordinateBody, separator + 1, coordinateBody.size)
}

private fun isCanonicalKittyReply(payload: ByteArray): Boolean {
    val body = strip(payload, ascii("\u001b[?"), ascii("u")) ?: return false
    val flags = canonicalUnsigned(body, 0, body.size) ?: return false
    return flags <= 31L
}

private fun isCanonicalKittyGraphicsReply(payload: ByteArray): Boolean {
    val body = strip(payload, ascii("\u001b_G"), ascii("\u001b\\")) ?: return false
    val separator = body.indexOf(';'.code.toByte())
    if (separator < 1) return false
    val message = body.copyOfRange(separator + 1, body.size)
    if (!message.contentEquals(ascii("OK"))) {
        if (message.size > 240 || message.any { (it.toInt() and 0xff) !in 0x20..0x7e } ||
            listOf("EINVAL: ", "ENOTSUP: ", "ENOSPC: ").none {
                message.size > it.length && startsWith(message, ascii(it))
            }) return false
    }
    var previous = 0
    var identity = false
    for (field in split(body.copyOfRange(0, separator), ','.code.toByte())) {
        if (field.size !in 3..12 || field[1] != '='.code.toByte()) return false
        val order = when (field[0]) {
            'i'.code.toByte() -> 1
            'I'.code.toByte() -> 2
            'p'.code.toByte() -> 3
            else -> 0
        }
        if (order <= previous || (canonicalUnsigned(field, 2, field.size) ?: return false) == 0L) return false
        previous = order
        identity = identity || order <= 2
    }
    return identity
}

private fun isCanonicalDecrpm(payload: ByteArray): Boolean {
    val rawBody = strip(payload, ascii("\u001b["), ascii("\$y")) ?: return false
    val body = if (rawBody.firstOrNull() == '?'.code.toByte()) rawBody.copyOfRange(1, rawBody.size) else rawBody
    val separator = body.indexOf(';'.code.toByte())
    if (separator <= 0 || separator != body.lastIndexOf(';'.code.toByte())) return false
    return canonicalUnsigned(body, 0, separator) != null &&
        body.copyOfRange(separator + 1, body.size).contentEquals(byteArrayOf(body.last())) &&
        body.last() in ascii("01234")
}

private fun isCanonicalDecrqss(payload: ByteArray): Boolean {
    val body = strip(payload, ascii("\u001bP"), ascii("\u001b\\")) ?: return false
    if (body.contentEquals(ascii("0\$r"))) return true
    if (!startsWith(body, ascii("1\$r"))) return false
    val status = body.copyOfRange(3, body.size)
    return canonicalColorAssignment(status) || canonicalSgrStatus(status) ||
        canonicalMarginStatus(status, 'r'.code.toByte()) ||
        canonicalMarginStatus(status, 's'.code.toByte()) ||
        status.contentEqualsAny("1 q", "2 q", "3 q", "4 q", "5 q", "6 q") ||
        status.contentEqualsAny("1\"q", "2\"q", "1*x", "2*x")
}

private fun isCanonicalXtversion(payload: ByteArray): Boolean =
    payload.contentEquals(ascii("\u001bP>|TermLink(1)\u001b\\"))

private fun canonicalColorAssignment(status: ByteArray): Boolean {
    val body = strip(status, byteArrayOf(), ascii(",|")) ?: return false
    val fields = split(body, ';'.code.toByte())
    if (fields.size != 3) return false
    val values = fields.map { canonicalUnsigned(it, 0, it.size) ?: return false }
    return values[0] in 1..2 && values[1] <= 257 && values[2] <= 257
}

private fun canonicalSgrStatus(status: ByteArray): Boolean {
    if (status.contentEquals(ascii("0m"))) return true
    val parameters = strip(status, ascii("0;"), ascii("m")) ?: return false
    val fields = split(parameters, ';'.code.toByte())
    var index = 0
    var previousRank = 0
    while (index < fields.size) {
        val field = fields[index]
        val rank = when {
            field.contentEquals(ascii("1")) -> 1
            field.contentEquals(ascii("2")) -> 2
            field.contentEquals(ascii("3")) -> 3
            field.contentEqualsAny("4:1", "4:2", "4:3", "4:4", "4:5") -> 4
            field.contentEquals(ascii("5")) -> 5
            field.contentEquals(ascii("7")) -> 6
            field.contentEquals(ascii("8")) -> 7
            field.contentEquals(ascii("9")) -> 8
            field.contentEquals(ascii("53")) -> 9
            field.contentEquals(ascii("38")) -> 10
            field.contentEquals(ascii("48")) -> 11
            field.contentEquals(ascii("58")) -> 12
            else -> return false
        }
        if (rank <= previousRank) return false
        if (rank >= 10) {
            val consumed = canonicalSgrColor(fields, index + 1) ?: return false
            index += consumed
        }
        previousRank = rank
        index++
    }
    return previousRank != 0
}

private fun canonicalSgrColor(fields: List<ByteArray>, start: Int): Int? {
    if (start >= fields.size) return null
    return when {
        fields[start].contentEquals(ascii("5")) &&
            start + 1 < fields.size &&
            canonicalUnsigned(fields[start + 1], 0, fields[start + 1].size)?.let { it <= 255 } == true -> 2
        fields[start].contentEquals(ascii("2")) && start + 3 < fields.size &&
            (1..3).all { delta ->
                canonicalUnsigned(fields[start + delta], 0, fields[start + delta].size)?.let { it <= 255 } == true
            } -> 4
        else -> null
    }
}

private fun canonicalMarginStatus(status: ByteArray, finalByte: Byte): Boolean {
    if (status.lastOrNull() != finalByte) return false
    val body = status.copyOf(status.size - 1)
    val separator = body.indexOf(';'.code.toByte())
    return separator > 0 && separator == body.lastIndexOf(';'.code.toByte()) &&
        canonicalCoordinate(body, 0, separator) &&
        canonicalCoordinate(body, separator + 1, body.size)
}

private fun isCanonicalXtgettcap(payload: ByteArray): Boolean {
    val body = strip(payload, ascii("\u001bP"), ascii("\u001b\\")) ?: return false
    if (body.contentEquals(ascii("0+r"))) return true
    if (!startsWith(body, ascii("1+r"))) return false
    val pairs = split(body.copyOfRange(3, body.size), ';'.code.toByte())
    if (pairs.isEmpty() || pairs.size > 256 || pairs.any { it.isEmpty() }) return false
    return pairs.all { pair ->
        val separator = pair.indexOf('='.code.toByte())
        separator > 0 && separator == pair.lastIndexOf('='.code.toByte()) &&
            canonicalHex(pair, 0, separator, 64) &&
            canonicalHex(pair, separator + 1, pair.size, Int.MAX_VALUE)
    }
}

private fun isCanonicalOscColor(payload: ByteArray): Boolean {
    val body = strip(payload, ascii("\u001b]"), ascii("\u001b\\")) ?: return false
    val fields = split(body, ';'.code.toByte())
    return when {
        fields.size == 3 && fields[0].contentEquals(ascii("4")) ->
            canonicalUnsigned(fields[1], 0, fields[1].size)?.let { it <= 255 } == true &&
                canonicalRgb(fields[2])
        fields.size == 2 && fields[0].contentEqualsAny("10", "11", "12", "17") -> canonicalRgb(fields[1])
        else -> false
    }
}

private fun canonicalRgb(color: ByteArray): Boolean {
    if (!startsWith(color, ascii("rgb:"))) return false
    val components = split(color.copyOfRange(4, color.size), '/'.code.toByte())
    return components.size == 3 && components.all { component ->
        component.size == 4 &&
            component[0] == component[2] && component[1] == component[3] &&
            canonicalHex(component, 0, component.size, 4)
    }
}

private fun canonicalCoordinate(bytes: ByteArray, start: Int, end: Int): Boolean =
    canonicalUnsigned(bytes, start, end)?.let { it in 1..512 } == true

private fun canonicalUnsigned(bytes: ByteArray, start: Int, end: Int): Long? {
    if (start >= end || (end - start > 1 && bytes[start] == '0'.code.toByte())) return null
    var value = 0L
    for (index in start until end) {
        val byte = bytes[index].toInt() and 0xff
        if (byte !in '0'.code..'9'.code) return null
        value = try {
            Math.addExact(Math.multiplyExact(value, 10L), (byte - '0'.code).toLong())
        } catch (_: ArithmeticException) {
            return null
        }
        if (value > 0xffff_ffffL) return null
    }
    return value
}

private fun canonicalHex(bytes: ByteArray, start: Int, end: Int, maximumLength: Int): Boolean {
    val length = end - start
    if (length <= 0 || length > maximumLength || length % 2 != 0) return false
    return (start until end).all { index ->
        val byte = bytes[index].toInt() and 0xff
        byte in '0'.code..'9'.code || byte in 'a'.code..'f'.code
    }
}

private fun strip(bytes: ByteArray, prefix: ByteArray, suffix: ByteArray): ByteArray? {
    if (!startsWith(bytes, prefix) || bytes.size < prefix.size + suffix.size) return null
    val suffixStart = bytes.size - suffix.size
    if (!regionEquals(bytes, suffixStart, suffix)) return null
    return bytes.copyOfRange(prefix.size, suffixStart)
}

private fun startsWith(bytes: ByteArray, prefix: ByteArray): Boolean = regionEquals(bytes, 0, prefix)

private fun regionEquals(bytes: ByteArray, offset: Int, expected: ByteArray): Boolean {
    if (offset < 0 || offset > bytes.size - expected.size) return false
    return expected.indices.all { bytes[offset + it] == expected[it] }
}

private fun split(bytes: ByteArray, delimiter: Byte): List<ByteArray> {
    if (bytes.isEmpty()) return emptyList()
    val fields = ArrayList<ByteArray>()
    var start = 0
    for (index in bytes.indices) {
        if (bytes[index] == delimiter) {
            fields += bytes.copyOfRange(start, index)
            start = index + 1
        }
    }
    fields += bytes.copyOfRange(start, bytes.size)
    return fields
}

private fun ByteArray.contentEqualsAny(vararg expected: String): Boolean =
    expected.any { contentEquals(ascii(it)) }

private fun ascii(value: String): ByteArray = value.toByteArray(Charsets.US_ASCII)
