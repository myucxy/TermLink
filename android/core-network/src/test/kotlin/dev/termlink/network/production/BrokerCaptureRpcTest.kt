package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.BrokerCaptureCrypto
import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.protocol.realtime.v1.BrokerCaptureSelectionV1
import dev.termlink.protocol.realtime.v1.HostHistoryObjectKindV1
import dev.termlink.protocol.realtime.v1.HostHistoryResponsePayload
import dev.termlink.protocol.realtime.v1.HostHistoryResponseStatusV1
import java.security.MessageDigest
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.test.assertFailsWith

class BrokerCaptureRpcTest {
    private val recipientDeviceId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
    private val selection = BrokerCaptureSelectionV1.newBuilder()
        .setObjectId(ByteString.copyFrom(ByteArray(16) { 4 }))
        .setSourceEventSeq(42).setCanonicalBytes(136)
        .setCanonicalSha256(ByteString.copyFrom(ByteArray(32) { 5 })).build()
    private val keyId = "broker_capture_v1:102132435465768798a9bacbdcedfe0f:7"
    private val context = BrokerCaptureRequestContext(selection, 7, keyId,
        ProductionTerminalStateContract.GRAPHICS_STATE)

    private fun response(bytes: ByteArray, canonicalBytes: Int = bytes.size - 208,
        index: Int = 0): HostHistoryResponsePayload {
        val wanted = selection.toBuilder().setCanonicalBytes(canonicalBytes).build()
        val offset = index * HOST_HISTORY_CHUNK_BYTES
        return HostHistoryResponsePayload.newBuilder()
            .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK)
            .setKind(HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_BROKER_CAPTURE)
            .setObjectId(selection.objectId).setBrokerCapture(wanted)
            .setStartSeq(42).setEndSeq(42).setKeyId(keyId)
            .setFormatName("TERMLINK_BROKER_CAPTURE_V1").setFormatVersion(1)
            .setObjectSha256(ByteString.copyFrom(MessageDigest.getInstance("SHA-256").digest(bytes)))
            .setObjectBytes(bytes.size.toLong())
            .setChunkCount((bytes.size + HOST_HISTORY_CHUNK_BYTES - 1) / HOST_HISTORY_CHUNK_BYTES)
            .setChunkIndex(index).setObjectChunk(ByteString.copyFrom(bytes, offset,
                minOf(HOST_HISTORY_CHUNK_BYTES, bytes.size - offset))).build()
    }

    @Test fun onlyAuthenticatedGraphicsPeerMayOfferBrokerCapture() {
        assertTrue(supportsBrokerCapture(ProductionTerminalProfile.GRAPHICS,
            listOf("terminal_broker_captures_v1")))
        assertTrue(supportsBrokerCapture(ProductionTerminalProfile.GRAPHICS_UNICODE16,
            listOf("terminal_broker_captures_v1")))
        assertFalse(supportsBrokerCapture(ProductionTerminalProfile.LEGACY,
            listOf("terminal_broker_captures_v1")))
        assertFalse(supportsBrokerCapture(ProductionTerminalProfile.GRAPHICS, emptyList()))
    }

    @Test fun selectionAndResponseMustMatchTheOrderedReference() {
        val valid = response(ByteArray(344))
        context.validate(valid)
        listOf(
            valid.toBuilder().setObjectId(ByteString.copyFrom(ByteArray(16) { 8 })).build(),
            valid.toBuilder().setBrokerCapture(selection.toBuilder().setSourceEventSeq(43)).build(),
            valid.toBuilder().setBrokerCapture(selection.toBuilder().setCanonicalSha256(ByteString.copyFrom(ByteArray(32)))).build(),
            valid.toBuilder().setStartSeq(41).build(),
            valid.toBuilder().setSnapshotSeq(42).build(),
            valid.toBuilder().setColumns(80).build(),
            valid.toBuilder().setKeyId("broker_capture_v1:other:7").build(),
            valid.toBuilder().setObjectBytes(345).build(),
        ).forEach { assertFailsWith<IllegalArgumentException> { context.validate(it) } }
    }

    @Test fun unicode16CaptureKeyIdCannotBeAcceptedAsGraphicsV1() {
        val v2KeyId = BrokerCaptureCrypto.keyId(recipientDeviceId, 7,
            ProductionTerminalStateContract.GRAPHICS_UNICODE16_STATE)
        assertEquals("broker_capture_v2:102132435465768798a9bacbdcedfe0f:7", v2KeyId)
        val v2 = BrokerCaptureRequestContext(selection, 7, v2KeyId,
            ProductionTerminalStateContract.GRAPHICS_UNICODE16_STATE)
        val v1Response = response(ByteArray(344))
        val v2Response = v1Response.toBuilder().setKeyId(v2KeyId).build()
        context.validate(v1Response)
        v2.validate(v2Response)
        assertFailsWith<IllegalArgumentException> { context.validate(v2Response) }
        assertFailsWith<IllegalArgumentException> { v2.validate(v1Response) }
        assertFailsWith<IllegalArgumentException> {
            BrokerCaptureRequestContext(selection, 7, v2KeyId, "terminal_state_2_22_v3")
        }
    }

    @Test fun objectMaximumRejectsBeforeMemoryReservation() {
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, broker = context)
        try {
            val oversized = response(ByteArray(344)).toBuilder()
                .setObjectBytes(BrokerCaptureCrypto.MAXIMUM_OBJECT_BYTES.toLong() + 1)
                .setBrokerCapture(selection.toBuilder().setCanonicalBytes(BrokerCaptureCrypto.MAXIMUM_CANONICAL_BYTES + 1))
                .setChunkCount(65).build()
            assertFailsWith<IllegalArgumentException> { pending.accept(oversized) }
            assertEquals(0L, budget.reservedBytes)
        } finally { pending.destroy() }
    }

    @Test fun chunkConflictAndLateChunkCannotRetainBudget() {
        val bytes = ByteArray(HOST_HISTORY_CHUNK_BYTES + 2) { (it % 251).toByte() }
        val wanted = selection.toBuilder().setCanonicalBytes(bytes.size - 208).build()
        val pendingContext = BrokerCaptureRequestContext(wanted, 7, keyId,
            ProductionTerminalStateContract.GRAPHICS_STATE)
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, broker = pendingContext)
        try {
            val first = response(bytes, wanted.canonicalBytes)
            assertEquals(null, pending.accept(first)?.completed)
            val conflict = first.toBuilder().setObjectChunk(ByteString.copyFrom(ByteArray(HOST_HISTORY_CHUNK_BYTES))).build()
            assertFailsWith<IllegalArgumentException> { pending.accept(conflict) }
            val second = response(bytes, wanted.canonicalBytes, 1)
            assertFailsWith<IllegalArgumentException> { pending.accept(second.toBuilder().setEndSeq(43).build()) }
            assertFailsWith<IllegalArgumentException> { pending.accept(second.toBuilder().setKeyId("other").build()) }
            pending.destroy()
            assertEquals(0L, budget.reservedBytes)
            assertEquals(null, pending.accept(second))
        } finally { pending.destroy() }
    }

    @Test fun reservationTransfersExactlyOnceWhileObjectStillZeroesCiphertext() {
        val bytes = ByteArray(344) { 9 }
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, broker = context)
        try {
            val completed = requireNotNull(pending.accept(response(bytes))?.completed)
            assertEquals(selection, completed.brokerCapture)
            val reservation = completed.takeReservation()
            assertFailsWith<IllegalArgumentException> { completed.takeReservation() }
            completed.destroy()
            assertTrue(completed.sealedObject.all { it == 0.toByte() })
            assertTrue(budget.reservedBytes > 0)
            reservation.close()
            assertEquals(0L, budget.reservedBytes)
        } finally { pending.destroy() }
    }
}
