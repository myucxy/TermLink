package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalHostEffectFrameV2
import dev.termlink.terminal.api.TerminalHostEffectKindV2
import dev.termlink.terminal.api.TerminalHostEffectKindV1
import dev.termlink.terminal.api.TerminalHostEffectV1
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.CRC32C
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class HostEffectFrameV2DecoderTest {
    @Test
    fun kittyGraphicsRepliesFollowTheCanonicalAuthorityGrammar() {
        val accepted = listOf(
            "i=1;OK", "I=4294967295;OK", "i=4294967295,I=9,p=2;OK", "I=2,p=3;OK",
            "i=1;EINVAL: invalid image", "i=1;ENOTSUP: broker medium", "i=1;ENOSPC: resource budget",
            "i=1;EINVAL: " + "x".repeat(232),
        )
        for (body in accepted) {
            val sevenBit = reply("\u001b_G$body\u001b\\")
            val eightBit = byteArrayOf(0x9f.toByte()) + sevenBit.copyOfRange(2, sevenBit.size - 2) + byteArrayOf(0x9c.toByte())
            for (payload in listOf(sevenBit, eightBit)) {
                val frame = buildFrame(listOf(WireEffect(7, 12, 0, payload)))
                assertContentEquals(payload, HostEffectFrameV2Decoder.decode(frame).effects.single().copyPayload())
                assertFailsWith<IllegalArgumentException> {
                    TerminalHostEffectV1(TerminalHostEffectKindV1.PTY_REPLY, 7, 12, 0, payload)
                }
            }
        }
        val rejected = listOf(
            ";OK", "p=1;OK", "i=0;OK", "I=0;OK", "i=01;OK", "i=+1;OK", "i=4294967296;OK",
            "i=1,i=2;OK", "I=2,i=1;OK", "i=1,p=2,I=3;OK", "i=1,p=0;OK", "i=1,;OK",
            ",i=1;OK", "i=1,,p=2;OK", "i=1,q=2;OK", "i=1;", "i=1;ok", "i=1;OK extra",
            "i=1;ENOENT: missing", "i=1;EINVAL: ", "i=1;ENOTSUP: ", "i=1;ENOSPC: ",
            "i=1;EINVAL: " + "x".repeat(233), "i=1;EINVAL: bad\u001b[2J", "i=1;EINVAL: bad\n",
        )
        for (body in rejected) {
            val payload = reply("\u001b_G$body\u001b\\")
            assertFailsWith<HostEffectFrameV2DecodeException> {
                HostEffectFrameV2Decoder.decode(buildFrame(listOf(WireEffect(7, 12, 0, payload))))
            }
        }
        for (payload in listOf(
            reply("\u001b_anything\u001b\\"), reply("\u001b_Gi=1;OK\u0007"),
            byteArrayOf(0x9f.toByte()) + reply("Gi=1;OK\u001b\\"),
            reply("\u001b_Gi=1;OK") + byteArrayOf(0x9c.toByte()),
            reply("\u001b_Gi=1;EINVAL: ") + byteArrayOf(0x80.toByte()) + reply("\u001b\\"),
        )) assertFailsWith<HostEffectFrameV2DecodeException> {
            HostEffectFrameV2Decoder.decode(buildFrame(listOf(WireEffect(7, 12, 0, payload))))
        }
    }

    @Test
    fun resizeEffectsRequireTheirFeatureAndCannotBecomePtyInput() {
        val payload = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putInt(132).putInt(0).array()
        val frame = buildFrame(listOf(WireEffect(7, 12, 0, payload))).mutateU64(40, 3).mutateU32(64, 2)
        assertEquals(TerminalHostEffectKindV2.RESIZE_REQUEST, HostEffectFrameV2Decoder.decode(frame).effects.single().kind)
        listOf(frame.mutateU64(40, 1), frame.mutateU32(104, 0), frame.mutateU32(104, 513),
            frame.mutateU32(108, 24), frame.mutateU32(64, 3)).forEach {
            assertFailsWith<HostEffectFrameV2DecodeException> { HostEffectFrameV2Decoder.decode(it) }
        }
    }

    @Test
    fun decodesEmptyCanonicalFrame() {
        val encoded = buildFrame(emptyList())
        val frame = HostEffectFrameV2Decoder.decode(encoded)

        assertEquals(2, frame.formatVersion)
        assertTrue(frame.effects.isEmpty())
        assertEquals(0L, frame.payloadByteCount)
        assertContentEquals(encoded, frame.copyRawFrame())
    }

    @Test
    fun decodesEveryReplyFamilyAndOwnsRawAndPayloadBytes() {
        val replies = listOf(
            "\u001b[?1;2c",
            "\u001b[>0;0;0c",
            "\u001b[0n",
            "\u001b[?512;512R",
            "\u001b[?31u",
            "\u001b[8;1;512t",
            "\u001b[8;512;1t",
            "\u001b[6;20;10t",
            "\u001b[4;524288;524288t",
            "\u001b[4;2\$y",
            "\u001b[?25;1\$y",
            "\u001bP0\$r\u001b\\",
            "\u001bP1\$r0;1;4:5;38;5;255;48;2;1;2;3m\u001b\\",
            "\u001bP1\$r1;512r\u001b\\",
            "\u001bP1\$r1;512s\u001b\\",
            "\u001bP1\$r6 q\u001b\\",
            "\u001bP1\$r2\"q\u001b\\",
            "\u001bP1\$r2*x\u001b\\",
            "\u001bP>|TermLink(1)\u001b\\",
            "\u001bP1+r436f=323536;524742=382f382f38\u001b\\",
            "\u001bP0+r\u001b\\",
            "\u001b]4;255;rgb:ffff/0000/1212\u001b\\",
            "\u001b]10;rgb:ffff/0000/1212\u001b\\",
            "\u001b]11;rgb:ffff/0000/1212\u001b\\",
            "\u001b]12;rgb:ffff/0000/1212\u001b\\",
        ).map(::reply)
        val encoded = buildFrame(
            replies.mapIndexed { index, payload ->
                WireEffect(index.toLong(), 1, 0, payload)
            },
        )
        val expectedRaw = encoded.copyOf()
        val frame = HostEffectFrameV2Decoder.decode(encoded)
        encoded.fill(0)

        assertEquals(replies.size, frame.effects.size)
        assertTrue(frame.effects.all { it.kind == TerminalHostEffectKindV2.PTY_REPLY })
        replies.forEachIndexed { index, expected ->
            assertContentEquals(expected, frame.effects[index].copyPayload())
        }
        assertContentEquals(expectedRaw, frame.copyRawFrame())
        frame.effects.first().copyPayload().fill(5)
        frame.copyRawFrame().fill(6)
        assertContentEquals(replies.first(), frame.effects.first().copyPayload())
        assertContentEquals(expectedRaw, frame.copyRawFrame())
    }

    @Test
    fun acceptsContiguousSameKeyOrdinalsAndRejectsGapResetAndRegressions() {
        val valid = listOf(
            WireEffect(7, 3, 0, reply("\u001b[0n")),
            WireEffect(7, 3, 1, reply("\u001b[?0n")),
            WireEffect(7, 3, 2, reply("\u001b[?1u")),
            WireEffect(7, 4, 0, reply("\u001b[?2u")),
            WireEffect(8, 1, 0, reply("\u001b[?3u")),
        )
        val frame = HostEffectFrameV2Decoder.decode(buildFrame(valid))
        assertEquals(listOf(0L, 1L, 2L, 0L, 0L), frame.effects.map { it.ordinal })

        listOf(
            listOf(valid[0].copy(ordinal = 1)),
            listOf(valid[0], valid[1].copy(ordinal = 2)),
            listOf(valid[0], valid[1].copy(ordinal = 0)),
            listOf(valid[0], WireEffect(7, 4, 1, reply("\u001b[?2u"))),
            listOf(valid[0], WireEffect(7, 2, 0, reply("\u001b[?2u"))),
            listOf(valid[0], WireEffect(6, 9, 0, reply("\u001b[?2u"))),
        ).forEach { records ->
            assertFailsWith<HostEffectFrameV2DecodeException> {
                HostEffectFrameV2Decoder.decode(buildFrame(records))
            }
        }
    }

    @Test
    fun rejectsHeaderVersionLengthFeatureCrcAndReservedMutations() {
        val baseline = buildFrame(listOf(WireEffect(1, 1, 0, reply("\u001b[0n"))))
        val mutations = listOf(
            baseline.mutate(0, 'X'.code),
            baseline.mutateU16(8, 1),
            baseline.mutateU16(10, 1),
            baseline.mutateU16(12, 63),
            baseline.mutateU16(14, 39),
            baseline.mutateU32(20, baseline.size + 1L),
            baseline.mutateU64(24, 65),
            baseline.mutateU64(32, 65),
            baseline.mutateU64(40, 3),
            baseline.mutateU64(48, 1),
            baseline.copyOf().also { it[56] = (it[56].toInt() xor 1).toByte() },
            baseline.copyOf().also { it[60] = (it[60].toInt() xor 1).toByte() },
        )
        mutations.forEach { bytes ->
            assertFailsWith<HostEffectFrameV2DecodeException> {
                HostEffectFrameV2Decoder.decode(bytes)
            }
        }
    }

    @Test
    fun rejectsRecordReservedKindFlagsLengthAndNoncontiguousPayloads() {
        val baseline = buildFrame(
            listOf(
                WireEffect(1, 1, 0, reply("\u001b[0n")),
                WireEffect(1, 2, 0, reply("\u001b[?0n")),
            ),
        )
        val mutations = listOf(
            baseline.mutateU32(64, 2),
            baseline.mutateU32(68, 1),
            baseline.mutateU32(80, 0),
            baseline.mutateU32(96, 0),
            baseline.mutateU32(100, 1),
            baseline.mutateU64(88, 145),
            baseline.mutateU64(128, 151),
            baseline.mutateU32(136, 1),
            baseline.mutateU64(72, Long.MIN_VALUE),
            baseline.mutateU64(88, Long.MIN_VALUE),
        )
        mutations.forEach { bytes ->
            assertFailsWith<HostEffectFrameV2DecodeException> {
                HostEffectFrameV2Decoder.decode(bytes.refreshCrc())
            }
        }
    }

    @Test
    fun rejectsUppercaseNoncanonicalDecimalHexColorAndArbitraryEscapePayloads() {
        val invalid = listOf(
            "\u001b[31m",
            "\u001b[8;0;1t",
            "\u001b[8;1;513t",
            "\u001b[8;01;1t",
            "\u001b[8;1;1;1t",
            "\u001b[6;1025;10t",
            "\u001b[01;1R",
            "\u001b[?01u",
            "\u001b[?32u",
            "\u001b[?025;1\$y",
            "\u001b[?25;5\$y",
            "\u001bP1\$rarbitrary\u001b\\",
            "\u001bP1\$r0;2;1m\u001b\\",
            "\u001bP>|TermLink(01)\u001b\\",
            "\u001bP>|Other(1)\u001b\\",
            "\u001bP1+r436F=323536\u001b\\",
            "\u001bP1+r436f=323536;\u001b\\",
            "\u001bP0+r544e\u001b\\",
            "\u001b]4;01;rgb:ffff/0000/1212\u001b\\",
            "\u001b]4;1;rgb:1234/0000/1212\u001b\\",
            "\u001b]10;rgb:FFFF/0000/1212\u001b\\",
            "\u001b]10;rgb:ffff/0000/1212\u0007",
        )
        invalid.forEach { value ->
            assertFailsWith<HostEffectFrameV2DecodeException>(value) {
                HostEffectFrameV2Decoder.decode(
                    buildFrame(listOf(WireEffect(0, 1, 0, reply(value)))),
                )
            }
        }
    }

    @Test
    fun enforcesReplyEffectAggregateAndFrameBoundsBeforeUnsafeAllocation() {
        val maximumReply = maximumXtgettcapReply()
        assertEquals(TerminalHostEffectFrameV2.MAX_REPLY_BYTES, maximumReply.size)
        val decoded = HostEffectFrameV2Decoder.decode(
            buildFrame(listOf(WireEffect(0, 1, 0, maximumReply))),
        )
        assertEquals(TerminalHostEffectFrameV2.MAX_REPLY_BYTES.toLong(), decoded.payloadByteCount)

        val aboveReply = maximumReply.copyOf(maximumReply.size + 1).also {
            it[it.lastIndex - 2] = '0'.code.toByte()
            it[it.lastIndex - 1] = 0x1b
            it[it.lastIndex] = '\\'.code.toByte()
        }
        assertFailsWith<HostEffectFrameV2DecodeException> {
            HostEffectFrameV2Decoder.decode(buildFrame(listOf(WireEffect(0, 1, 0, aboveReply))))
        }

        val tooMany = buildHeaderOnly(
            count = TerminalHostEffectFrameV2.MAX_EFFECTS.toLong() + 1,
            payloadOffset = 64,
            totalLength = 64,
        )
        assertFailsWith<HostEffectFrameV2DecodeException> {
            HostEffectFrameV2Decoder.decode(tooMany)
        }

        val aboveAggregate = ByteArray(64 + TerminalHostEffectFrameV2.MAX_PAYLOAD_BYTES.toInt() + 1)
        writeCanonicalHeader(
            aboveAggregate,
            count = 0,
            payloadOffset = 64,
        )
        assertFailsWith<HostEffectFrameV2DecodeException> {
            HostEffectFrameV2Decoder.decode(aboveAggregate)
        }

        assertEquals(123_033_008, TerminalHostEffectFrameV2.MAX_FRAME_BYTES)
        assertEquals(1_398_102, TerminalHostEffectFrameV2.MAX_EFFECTS)
        assertEquals(67_108_864L, TerminalHostEffectFrameV2.MAX_PAYLOAD_BYTES)
    }

    @Test
    fun rejectsTruncationAtEveryStructuralBoundary() {
        val frame = buildFrame(listOf(WireEffect(7, 3, 0, reply("\u001bP0\$r\u001b\\"))))
        for (length in listOf(0, 1, 7, 8, 11, 12, 15, 16, 19, 20, 23, 24, 31, 32, 39, 40, 47, 48, 55, 56, 59, 60, 63, 64, 79, 80, 87, 88, 95, 96, 103, frame.lastIndex)) {
            assertFailsWith<HostEffectFrameV2DecodeException>("length=$length") {
                HostEffectFrameV2Decoder.decode(frame.copyOf(length))
            }
        }
    }

    private data class WireEffect(
        val sequence: Long,
        val offset: Int,
        val ordinal: Long,
        val payload: ByteArray,
    )

    private fun buildFrame(effects: List<WireEffect>): ByteArray {
        val payloadOffset = Math.addExact(64, Math.multiplyExact(effects.size, 40))
        val payloadBytes = effects.sumOf { it.payload.size }
        val bytes = ByteArray(Math.addExact(payloadOffset, payloadBytes))
        writeCanonicalHeader(bytes, effects.size.toLong(), payloadOffset.toLong())
        var nextPayload = payloadOffset
        effects.forEachIndexed { index, effect ->
            val record = 64 + index * 40
            bytes.putU32(record, 1)
            bytes.putU64(record + 8, effect.sequence)
            bytes.putU32(record + 16, effect.offset.toLong())
            bytes.putU32(record + 20, effect.ordinal)
            bytes.putU64(record + 24, nextPayload.toLong())
            bytes.putU32(record + 32, effect.payload.size.toLong())
            effect.payload.copyInto(bytes, nextPayload)
            nextPayload += effect.payload.size
        }
        return bytes.refreshCrc()
    }

    private fun buildHeaderOnly(count: Long, payloadOffset: Long, totalLength: Int): ByteArray =
        ByteArray(totalLength).also { writeCanonicalHeader(it, count, payloadOffset) }.refreshCrc()

    private fun writeCanonicalHeader(bytes: ByteArray, count: Long, payloadOffset: Long) {
        MAGIC.copyInto(bytes)
        bytes.putU16(8, 2)
        bytes.putU16(10, 0)
        bytes.putU16(12, 64)
        bytes.putU16(14, 40)
        bytes.putU32(16, count)
        bytes.putU32(20, bytes.size.toLong())
        bytes.putU64(24, 64)
        bytes.putU64(32, payloadOffset)
        bytes.putU64(40, 1)
    }

    private fun ByteArray.refreshCrc(): ByteArray = apply {
        putU32(56, crc32c(this, 64, size))
        putU32(60, crc32c(this, 0, 60))
    }

    private fun ByteArray.mutate(offset: Int, value: Int): ByteArray =
        copyOf().also { it[offset] = value.toByte() }

    private fun ByteArray.mutateU16(offset: Int, value: Int): ByteArray =
        copyOf().also { it.putU16(offset, value) }.refreshCrc()

    private fun ByteArray.mutateU32(offset: Int, value: Long): ByteArray =
        copyOf().also { it.putU32(offset, value) }.refreshCrc()

    private fun ByteArray.mutateU64(offset: Int, value: Long): ByteArray =
        copyOf().also { it.putU64(offset, value) }.refreshCrc()

    private fun ByteArray.putU16(offset: Int, value: Int) {
        ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN).putShort(offset, value.toShort())
    }

    private fun ByteArray.putU32(offset: Int, value: Long) {
        ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN).putInt(offset, value.toInt())
    }

    private fun ByteArray.putU64(offset: Int, value: Long) {
        ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN).putLong(offset, value)
    }

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

    private fun crc32c(bytes: ByteArray, start: Int, end: Int): Long {
        val crc = CRC32C()
        crc.update(bytes, start, end - start)
        return crc.value
    }

    private companion object {
        val MAGIC = byteArrayOf(
            'T'.code.toByte(), 'L'.code.toByte(), 'H'.code.toByte(), 'E'.code.toByte(),
            'F'.code.toByte(), '2'.code.toByte(), 0, 0,
        )
    }
}
