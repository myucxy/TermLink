package dev.termlink.product

import android.content.Context
import dev.termlink.terminal.api.TerminalHostEffectV2
import java.security.MessageDigest
import java.util.UUID

/** Ordered source watermarks survive renderer replacement. Never evict a receipt to replay an action. */
internal class TerminalActionReceipts(context: Context, account: String, session: UUID) {
    private val preferences = context.getSharedPreferences("terminal-action-receipts-v1", Context.MODE_PRIVATE)
    private val key = MessageDigest.getInstance("SHA-256").digest(
        (account + "\u0000" + session).toByteArray(Charsets.UTF_8),
    ).joinToString("") { "%02x".format(it) }

    fun claim(effects: List<TerminalHostEffectV2>): List<TerminalHostEffectV2> = synchronized(lock) {
        if (effects.isEmpty()) return emptyList()
        // A failed commit may still update SharedPreferences' memory. Block this process too.
        if (key in failed) return emptyList()
        try {
            val previous = preferences.getString(key, null)?.let(Coordinate::parse)
            if (previous == null && preferences.all.size >= 4096) return emptyList()
            val coordinates = effects.map { Coordinate(it.sourceSequence, it.sourceOffsetEnd, it.ordinal) }
            require(coordinates.zipWithNext().all { (a, b) -> a < b })
            val accepted = effects.zip(coordinates).filter { (_, value) -> previous == null || value > previous }
            if (accepted.isEmpty()) return emptyList()
            if (!preferences.edit().putString(key, accepted.last().second.toString()).commit()) {
                failed.add(key)
                return emptyList()
            }
            accepted.map { it.first }
        } catch (_: Exception) {
            failed.add(key)
            emptyList()
        }
    }

    private data class Coordinate(val sequence: Long, val offset: Int, val ordinal: Long) : Comparable<Coordinate> {
        init { require(sequence >= 0 && offset in 1..1048576 && ordinal in 0..0xffffffffL) }
        override fun compareTo(other: Coordinate): Int = compareValuesBy(this, other,
            Coordinate::sequence, Coordinate::offset, Coordinate::ordinal)
        override fun toString() = "$sequence:$offset:$ordinal"
        companion object {
            fun parse(value: String): Coordinate {
                val fields = value.split(':')
                require(fields.size == 3)
                return Coordinate(fields[0].toLong(), fields[1].toInt(), fields[2].toLong())
            }
        }
    }

    companion object {
        private val lock = Any()
        private val failed = mutableSetOf<String>()
    }
}
