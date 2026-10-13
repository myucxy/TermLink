package dev.termlink.terminal.api

/** Exact supported DEC report syntax; no restore payload is executed by the mirror decoder. */
internal fun isTerminalStateReport(payload: ByteArray): Boolean {
    if (payload.size !in 7..16384 || payload[0] != 27.toByte() || payload[1] != 'P'.code.toByte() ||
        payload[payload.lastIndex - 1] != 27.toByte() || payload.last() != '\\'.code.toByte()) return false
    val bodyBytes = payload.copyOfRange(2, payload.size - 2)
    if (bodyBytes.any { it.toInt() !in 32..126 }) return false
    val body = bodyBytes.toString(Charsets.US_ASCII)
    fun number(text: String, maximum: Int): Int? = text.takeIf {
        it.length in 1..5 && it.all { digit -> digit in '0'..'9' }
    }?.toIntOrNull()?.takeIf { it <= maximum }
    fun flags(text: String, mask: Int): Int? = text.singleOrNull()?.code?.takeIf { it and mask.inv() == 64 }?.and(mask)
    val data = body.drop(3)
    when (body.take(3)) {
        "0!u", "1!u" -> return data != "<" && isTerminalCharsetId(data, body[0] == '1')
        "1\$u" -> {
            if (data.length > 128) return false
            val fields = data.split(';')
            if (fields.size != 10 || number(fields[0], 512) == null || number(fields[1], 512) == null ||
                number(fields[2], 65534) == null || flags(fields[3], 31) == null || flags(fields[4], 1) == null ||
                number(fields[6], 3) == null || number(fields[7], 3) == null) return false
            val modeFlags = flags(fields[5], 15) ?: return false
            if (modeFlags and 6 == 6) return false
            val sizes = flags(fields[8], 15) ?: return false
            val ids = fields[9]
            var offset = 0
            repeat(4) { register ->
                val start = offset
                while (offset < ids.length && ids[offset].code in 32..47) offset++
                if (offset >= ids.length || offset - start > 2) return false
                val id = ids.substring(start, ++offset)
                if (!isTerminalCharsetId(id, sizes and (1 shl register) != 0)) return false
            }
            return offset == ids.length
        }
        "2\$u" -> {
            if (data.isEmpty()) return true
            var previous = 0
            for (field in data.split('/')) {
                val column = number(field, 512) ?: return false
                if (column <= previous) return false
                previous = column
            }
            return true
        }
        "2\$s" -> {
            if (data.isEmpty()) return true
            val entries = data.split('/')
            if (entries.size > 256) return false
            val seen = BooleanArray(256)
            for (entry in entries) {
                val fields = entry.split(';')
                if (fields.size != 5) return false
                val index = number(fields[0], 255) ?: return false
                val model = number(fields[1], 2)?.takeIf { it != 0 } ?: return false
                if (seen[index] || number(fields[2], if (model == 1) 360 else 100) == null ||
                    number(fields[3], 100) == null || number(fields[4], 100) == null) return false
                seen[index] = true
            }
            return true
        }
        else -> return false
    }
}

private fun isTerminalCharsetId(id: String, size96: Boolean): Boolean =
    if (size96) id.length == 1 && id[0] in "ABLFHM<"
    else if (id.length == 1) id[0] in "B012A45CRQ9KYE6Z7H=f`<"
    else id in setOf("%6", "%5", "&4", "&5", "\"?", "\">", "\"4", "%0", "%2", "%=")
