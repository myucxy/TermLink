package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class TerminalHostEffectFrameV2Test {
    @Test
    fun ownsRawFramePayloadsAndEffectList() {
        val raw = ByteArray(64) { 7 }
        val payload = reply("\u001b[?31u")
        val mutableEffects = mutableListOf(effect(7, 4, 0, payload))
        val frame = TerminalHostEffectFrameV2(raw, mutableEffects)

        raw.fill(0)
        payload.fill(0)
        mutableEffects.clear()
        assertEquals(2, frame.formatVersion)
        assertEquals(1, frame.effects.size)
        assertEquals(6L, frame.payloadByteCount)
        assertTrue(frame.copyRawFrame().all { it == 7.toByte() })
        assertContentEquals(reply("\u001b[?31u"), frame.effects.single().copyPayload())
        frame.copyRawFrame().fill(3)
        frame.effects.single().copyPayload().fill(4)
        assertTrue(frame.copyRawFrame().all { it == 7.toByte() })
        assertContentEquals(reply("\u001b[?31u"), frame.effects.single().copyPayload())
        assertFailsWith<UnsupportedOperationException> {
            @Suppress("UNCHECKED_CAST")
            (frame.effects as MutableList<TerminalHostEffectV2>).clear()
        }
    }

    @Test
    fun constructorAcceptsEveryFrozenReplyFamily() {
        val replies = listOf(
            "\u001b[?1;2c",
            "\u001b[>0;0;0c",
            "\u001b[0n",
            "\u001b[?0n",
            "\u001b[512;512R",
            "\u001b[?1;512R",
            "\u001b[?0u",
            "\u001b[?31u",
            "\u001b[4;2\$y",
            "\u001b[?25;1\$y",
            "\u001bP0\$r\u001b\\",
            "\u001bP1\$r0m\u001b\\",
            "\u001bP1\$r0;1;2;3;4:5;5;7;8;9;53;38;5;255;48;2;1;2;3;58;2;255;0;128m\u001b\\",
            "\u001bP1\$r1;512r\u001b\\",
            "\u001bP1\$r1;512s\u001b\\",
            "\u001bP1\$r6 q\u001b\\",
            "\u001bP1\$r2\"q\u001b\\",
            "\u001bP1\$r2*x\u001b\\",
            "\u001bP1+r436f=323536\u001b\\",
            "\u001bP1+r436f=323536;524742=382f382f38\u001b\\",
            "\u001bP0+r\u001b\\",
            "\u001b]4;255;rgb:ffff/0000/1212\u001b\\",
            "\u001b]10;rgb:ffff/0000/1212\u001b\\",
            "\u001b]11;rgb:ffff/0000/1212\u001b\\",
            "\u001b]12;rgb:ffff/0000/1212\u001b\\",
        )
        replies.forEachIndexed { index, value ->
            val effect = effect(index.toLong(), 1, 0, reply(value))
            assertContentEquals(reply(value), effect.copyPayload(), value)
        }
    }

    @Test
    fun constructorRejectsArbitraryAndNoncanonicalReplies() {
        val invalid = listOf(
            "\u001b[31m",
            "\u001b[01;1R",
            "\u001b[513;1R",
            "\u001b[?00u",
            "\u001b[?32u",
            "\u001b[?025;1\$y",
            "\u001b[?25;5\$y",
            "\u001bP1\$rarbitrary\u001b\\",
            "\u001bP1\$r0;2;1m\u001b\\",
            "\u001bP1+r436F=323536\u001b\\",
            "\u001bP1+r436f=323536;\u001b\\",
            "\u001bP1+r436f=323536=30\u001b\\",
            "\u001bP0+r544e\u001b\\",
            "\u001b]4;01;rgb:ffff/0000/1212\u001b\\",
            "\u001b]4;1;rgb:1234/0000/1212\u001b\\",
            "\u001b]10;rgb:FFFF/0000/1212\u001b\\",
            "\u001b]10;rgb:ffff/0000/1212\u0007",
        )
        invalid.forEach { value ->
            assertFailsWith<IllegalArgumentException>(value) {
                effect(0, 1, 0, reply(value))
            }
        }
        val tooManyPairs = "\u001bP1+r" + List(257) { "41=42" }.joinToString(";") + "\u001b\\"
        assertFailsWith<IllegalArgumentException> {
            effect(0, 1, 0, reply(tooManyPairs))
        }
    }

    @Test
    fun frameConstructorEnforcesSourceKeyAndOrdinalCanonicality() {
        val valid = listOf(
            effect(7, 3, 0, reply("\u001b[0n")),
            effect(7, 3, 1, reply("\u001b[?0n")),
            effect(7, 4, 0, reply("\u001b[?1u")),
            effect(8, 1, 0, reply("\u001b[?2u")),
        )
        assertEquals(4, TerminalHostEffectFrameV2(ByteArray(64), valid).effects.size)

        listOf(
            listOf(effect(7, 3, 1, reply("\u001b[0n"))),
            listOf(valid[0], effect(7, 3, 2, reply("\u001b[?0n"))),
            listOf(valid[0], effect(7, 4, 1, reply("\u001b[?0n"))),
            listOf(valid[0], effect(6, 9, 0, reply("\u001b[?0n"))),
            listOf(valid[0], effect(7, 2, 0, reply("\u001b[?0n"))),
        ).forEach { effects ->
            assertFailsWith<IllegalArgumentException> {
                TerminalHostEffectFrameV2(ByteArray(64), effects)
            }
        }
    }

    @Test
    fun replyAndAggregateBoundsAreDefendedByDto() {
        val maximumReply = maximumXtgettcapReply()
        assertEquals(TerminalHostEffectFrameV2.MAX_REPLY_BYTES, maximumReply.size)
        effect(0, 1, 0, maximumReply)

        val tooLarge = maximumReply.copyOf(maximumReply.size + 1)
        tooLarge[tooLarge.lastIndex - 2] = '0'.code.toByte()
        tooLarge[tooLarge.lastIndex - 1] = 0x1b
        tooLarge[tooLarge.lastIndex] = '\\'.code.toByte()
        assertFailsWith<IllegalArgumentException> { effect(0, 1, 0, tooLarge) }

        val maximumEffects = List(1024) { index ->
            effect(index.toLong(), 1, 0, maximumReply)
        }
        val maximum = TerminalHostEffectFrameV2(ByteArray(64), maximumEffects)
        assertEquals(TerminalHostEffectFrameV2.MAX_PAYLOAD_BYTES, maximum.payloadByteCount)
        val above = ArrayList(maximumEffects)
        above += effect(1024, 1, 0, reply("\u001b[0n"))
        assertFailsWith<IllegalArgumentException> {
            TerminalHostEffectFrameV2(ByteArray(64), above)
        }
    }

    private fun effect(
        sequence: Long,
        offset: Int,
        ordinal: Long,
        payload: ByteArray,
    ): TerminalHostEffectV2 = TerminalHostEffectV2(
        TerminalHostEffectKindV2.PTY_REPLY,
        sequence,
        offset,
        ordinal,
        payload,
    )

    private fun maximumXtgettcapReply(): ByteArray {
        val prefix = reply("\u001bP1+r41=")
        val suffix = reply("\u001b\\")
        return ByteArray(TerminalHostEffectFrameV2.MAX_REPLY_BYTES).also { result ->
            prefix.copyInto(result)
            result.fill('0'.code.toByte(), prefix.size, result.size - suffix.size)
            suffix.copyInto(result, result.size - suffix.size)
        }
    }

    private fun reply(value: String): ByteArray = value.toByteArray(Charsets.US_ASCII)
}
