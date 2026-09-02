package dev.termlink.protocol

import com.google.protobuf.ByteString
import dev.termlink.protocol.transport.v1.DirectPathFrameV1
import dev.termlink.protocol.transport.v1.DirectConnectivityCheckBatchV2
import dev.termlink.protocol.transport.v1.TransportSignalPayloadV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import dev.termlink.protocol.transport.v1.SessionCutoverBarrierV1
import java.io.File
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TransportProtocolTest {
    @Test
    fun multipairV2SharedBatchHasStableWireAndDedicatedSignalCases() {
        val vector = findRepositoryFile(
            "protocol/test-vectors/direct-transport-multipair-v2.json",
        ).readText()
        fun value(name: String): String = Regex("\"$name\"\\s*:\\s*\"([^\"]+)\"")
            .find(vector)?.groupValues?.get(1) ?: error("missing $name")
        val expected = value("check_batch_protobuf_hex").hexToBytes()
        val parsed = DirectConnectivityCheckBatchV2.parseFrom(expected)

        assertArrayEquals(expected, parsed.toByteArray())
        assertEquals(1, parsed.batchSeq)
        assertTrue(parsed.planComplete)
        assertEquals(1, parsed.pairsCount)
        assertEquals(
            TransportSignalPayloadV1.SignalCase.CONNECTIVITY_CHECK_BATCH_V2,
            TransportSignalPayloadV1.newBuilder()
                .setFormatVersion(1)
                .setConnectivityCheckBatchV2(parsed)
                .build()
                .signalCase,
        )
        assertEquals(11, TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2.number)
        assertEquals(12, TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2.number)
        assertEquals(13, TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2.number)
        assertEquals(14, TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2.number)
    }

    @Test
    fun directFramePreservesExactRealtimeEnvelopeBytes() {
        val relayEnvelope = byteArrayOf(
            0x08,
            0x01,
            0x10,
            0x14,
            0x1a,
            0x03,
            0x11,
            0x22,
            0x33,
            0xf8.toByte(),
            0x01,
            0x01,
        )
        val encoded = DirectPathFrameV1.newBuilder()
            .setProtocolVersion(1)
            .setConnectionId(ByteString.copyFrom(ByteArray(16) { 0xa5.toByte() }))
            .setPathEpoch(7)
            .setFrameSeq(9)
            .setRealtimeEnvelope(ByteString.copyFrom(relayEnvelope))
            .build()
            .toByteArray()

        val parsed = DirectPathFrameV1.parseFrom(encoded)

        assertEquals(DirectPathFrameV1.BodyCase.REALTIME_ENVELOPE, parsed.bodyCase)
        assertArrayEquals(relayEnvelope, parsed.realtimeEnvelope.toByteArray())
    }

    @Test
    fun cutoverBarrierKeepsDirectionsIndependent() {
        val parsed = SessionCutoverBarrierV1.parseFrom(
            SessionCutoverBarrierV1.newBuilder()
                .setSessionId(ByteString.copyFrom(ByteArray(16) { 0x11 }))
                .setPcToAndroidFirstDirectSeq(43)
                .build()
                .toByteArray(),
        )

        assertTrue(parsed.hasPcToAndroidFirstDirectSeq())
        assertFalse(parsed.hasAndroidToPcFirstDirectSeq())
        assertEquals(43L, parsed.pcToAndroidFirstDirectSeq)
    }

    private fun findRepositoryFile(relativePath: String): File {
        var current: File? = File(requireNotNull(System.getProperty("user.dir"))).canonicalFile
        while (current != null) {
            val candidate = current.resolve(relativePath)
            if (candidate.isFile) return candidate
            current = current.parentFile
        }
        error("protocol vector was not found")
    }

    private fun String.hexToBytes(): ByteArray {
        require(length % 2 == 0)
        return ByteArray(length / 2) { index ->
            substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
    }
}
