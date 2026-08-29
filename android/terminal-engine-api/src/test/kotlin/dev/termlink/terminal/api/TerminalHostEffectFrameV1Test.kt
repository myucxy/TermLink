package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class TerminalHostEffectFrameV1Test {
    @Test
    fun ownsPayloadAndEffectListWithoutExposingMutableAliases() {
        val source = "\u001b[0n".toByteArray(Charsets.US_ASCII)
        val mutableEffects = mutableListOf(
            TerminalHostEffectV1(
                kind = TerminalHostEffectKindV1.PTY_REPLY,
                sourceSequence = 7,
                sourceOffsetEnd = 4,
                ordinal = 0,
                payload = source,
            ),
        )
        val frame = TerminalHostEffectFrameV1(mutableEffects)
        source[0] = 99
        mutableEffects.clear()

        assertEquals(1, frame.formatVersion)
        assertEquals(4L, frame.payloadByteCount)
        assertContentEquals("\u001b[0n".toByteArray(Charsets.US_ASCII), frame.effects.single().copyPayload())
        frame.effects.single().copyPayload()[1] = 88
        assertContentEquals("\u001b[0n".toByteArray(Charsets.US_ASCII), frame.effects.single().copyPayload())
        assertFailsWith<UnsupportedOperationException> {
            @Suppress("UNCHECKED_CAST")
            (frame.effects as MutableList<TerminalHostEffectV1>).clear()
        }
    }

    @Test
    fun enforcesSemanticFieldBounds() {
        fun effect(
            sequence: Long = 0,
            offset: Int = 1,
            ordinal: Long = 0,
            payload: ByteArray = "\u001b[0n".toByteArray(Charsets.US_ASCII),
        ) = TerminalHostEffectV1(
            TerminalHostEffectKindV1.PTY_REPLY,
            sequence,
            offset,
            ordinal,
            payload,
        )

        assertFailsWith<IllegalArgumentException> { effect(sequence = -1) }
        assertFailsWith<IllegalArgumentException> { effect(offset = 0) }
        assertFailsWith<IllegalArgumentException> { effect(offset = 1024 * 1024 + 1) }
        assertFailsWith<IllegalArgumentException> { effect(ordinal = 1) }
        assertFailsWith<IllegalArgumentException> { effect(payload = byteArrayOf()) }
        assertFailsWith<IllegalArgumentException> {
            effect(payload = "\u001b[01;1R".toByteArray(Charsets.US_ASCII))
        }
    }
}
