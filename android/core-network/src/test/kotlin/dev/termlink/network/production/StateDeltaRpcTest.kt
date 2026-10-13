package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.DeltaCrypto
import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.protocol.realtime.v1.HostHistoryObjectKindV1
import dev.termlink.protocol.realtime.v1.HostHistoryResponsePayload
import dev.termlink.protocol.realtime.v1.HostHistoryResponseStatusV1
import dev.termlink.protocol.realtime.v1.StateDeltaMetadataV1
import java.security.MessageDigest
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.test.assertFailsWith

class StateDeltaRpcTest {
    private val contextId = ByteString.copyFrom(ByteArray(32) { 1 })
    private val baseHash = ByteString.copyFrom(ByteArray(32) { 2 })
    private val targetHash = ByteString.copyFrom(ByteArray(32) { 3 })
    private val context = StateDeltaRequestContext(0uL, contextId, 1, 42uL, 2048uL,
        baseHash, 7, "delta-key")

    private fun metadata() = StateDeltaMetadataV1.newBuilder()
        .setContextId(contextId).setProfile(1).setBaseNextSourceSequence(42)
        .setTargetNextSourceSequence(44).setBaseSnapshotBytes(2048)
        .setTargetSnapshotBytes(4096).setBaseSnapshotSha256(baseHash)
        .setTargetSnapshotSha256(targetHash).build()

    private fun response(bytes: ByteArray, index: Int = 0): HostHistoryResponsePayload {
        val start = index * HOST_HISTORY_CHUNK_BYTES
        return HostHistoryResponsePayload.newBuilder()
            .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_OK)
            .setKind(HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_STATE_DELTA)
            .setObjectId(ByteString.copyFrom(ByteArray(16) { 4 }))
            .setObjectSha256(ByteString.copyFrom(MessageDigest.getInstance("SHA-256").digest(bytes)))
            .setSnapshotSeq(43).setKeyId("delta-key")
            .setFormatName(DeltaCrypto.FORMAT_NAME).setFormatVersion(DeltaCrypto.FORMAT_VERSION)
            .setStateDelta(metadata()).setObjectBytes(bytes.size.toLong())
            .setChunkCount((bytes.size + HOST_HISTORY_CHUNK_BYTES - 1) / HOST_HISTORY_CHUNK_BYTES)
            .setChunkIndex(index)
            .setObjectChunk(ByteString.copyFrom(bytes, start,
                minOf(HOST_HISTORY_CHUNK_BYTES, bytes.size - start))).build()
    }

    @Test fun capabilityRequiresAuthenticatedGraphicsHost() {
        assertTrue(supportsStateDelta(ProductionTerminalProfile.GRAPHICS, listOf("state_delta_v1")))
        assertTrue(supportsStateDelta(ProductionTerminalProfile.GRAPHICS_UNICODE16, listOf("state_delta_v1")))
        assertFalse(supportsStateDelta(ProductionTerminalProfile.LEGACY, listOf("state_delta_v1")))
        assertFalse(supportsStateDelta(ProductionTerminalProfile.GRAPHICS, emptyList()))
    }

    @Test fun unicode16DeltaResponseRequiresItsOwnKeyId() {
        val recipient = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val v1KeyId = DeltaCrypto.keyId(recipient, 7,
            ProductionTerminalStateContract.GRAPHICS_STATE)
        val v2KeyId = DeltaCrypto.keyId(recipient, 7,
            ProductionTerminalStateContract.GRAPHICS_UNICODE16_STATE)
        val v2 = StateDeltaRequestContext(0uL, contextId, 1, 42uL, 2048uL,
            baseHash, 7, v2KeyId)
        val response = response(ByteArray(608)).toBuilder().setKeyId(v2KeyId).build()
        v2.validate(response)
        assertFailsWith<IllegalArgumentException> {
            StateDeltaRequestContext(0uL, contextId, 1, 42uL, 2048uL,
                baseHash, 7, v1KeyId).validate(response)
        }
        assertFailsWith<IllegalArgumentException> {
            v2.validate(response.toBuilder().setKeyId(v1KeyId).build())
        }
    }

    @Test fun responseMustBindRequestedBaseAndActualTarget() {
        val valid = response(ByteArray(608))
        context.validate(valid)
        listOf(
            valid.toBuilder().setSnapshotSeq(42).build(),
            valid.toBuilder().setStateDelta(metadata().toBuilder().setContextId(ByteString.copyFrom(ByteArray(32)))).build(),
            valid.toBuilder().setStateDelta(metadata().toBuilder().setBaseSnapshotSha256(targetHash)).build(),
            valid.toBuilder().setStateDelta(metadata().toBuilder().setTargetNextSourceSequence(45)).build(),
            valid.toBuilder().setStateDelta(metadata().toBuilder().setTargetSnapshotBytes(128L * 1024 * 1024 + 1)).build(),
            valid.toBuilder().setFormatName("TERMLINK_FROZEN_HISTORY_PAGE_V2").build(),
            valid.toBuilder().setFrozenHistory(dev.termlink.protocol.realtime.v1.FrozenHistoryMetadata.getDefaultInstance()).build(),
        ).forEach { assertFailsWith<IllegalArgumentException> { context.validate(it) } }
    }

    @Test fun eightMiBSealedLimitIsCheckedBeforeReservation() {
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, delta = context)
        try {
            val oversized = response(ByteArray(608)).toBuilder()
                .setObjectBytes(DeltaCrypto.MAXIMUM_OBJECT_BYTES.toLong() + 1)
                .setChunkCount(17).build()
            assertFailsWith<IllegalArgumentException> { pending.accept(oversized) }
            assertEquals(0L, budget.reservedBytes)
        } finally { pending.destroy() }
    }

    @Test fun outOfOrderChunksAssembleAndReleaseBudget() {
        val bytes = ByteArray(HOST_HISTORY_CHUNK_BYTES + 2) { (it % 251).toByte() }
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, delta = context)
        try {
            assertEquals(null, pending.accept(response(bytes, 1))?.completed)
            val completed = requireNotNull(pending.accept(response(bytes, 0))?.completed)
            assertTrue(completed.sealedObject.contentEquals(bytes))
            assertEquals(metadata(), completed.stateDelta)
            completed.close()
            assertEquals(0L, budget.reservedBytes)
        } finally { pending.destroy() }
    }

    @Test fun conflictingReplayAndChangedDeltaIdentityFailWithoutLeakingBudget() {
        val bytes = ByteArray(HOST_HISTORY_CHUNK_BYTES + 2) { (it % 251).toByte() }
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, delta = context)
        try {
            val first = response(bytes, 0)
            assertEquals(null, pending.accept(first)?.completed)
            val differentBytes = first.toBuilder().setObjectChunk(ByteString.copyFrom(
                ByteArray(HOST_HISTORY_CHUNK_BYTES))).build()
            assertFailsWith<IllegalArgumentException> { pending.accept(differentBytes) }
            val changedMetadata = response(bytes, 1).toBuilder()
                .setStateDelta(metadata().toBuilder().setTargetSnapshotSha256(baseHash)).build()
            assertFailsWith<IllegalArgumentException> { pending.accept(changedMetadata) }
            val changedKey = response(bytes, 1).toBuilder().setKeyId("delta-other-epoch").build()
            assertFailsWith<IllegalArgumentException> { pending.accept(changedKey) }
            pending.destroy()
            assertEquals(0L, budget.reservedBytes)
            assertEquals(null, pending.accept(response(bytes, 1)))
            assertEquals(0L, budget.reservedBytes)
        } finally { pending.destroy() }
    }

    @Test fun changedKeyOnFirstChunkRejectsBeforeAllocation() {
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, delta = context)
        try {
            val wrongEpoch = response(ByteArray(608)).toBuilder().setKeyId("delta-other-epoch").build()
            assertFailsWith<IllegalArgumentException> { pending.accept(wrongEpoch) }
            assertEquals(0L, budget.reservedBytes)
        } finally { pending.destroy() }
    }

    @Test fun corruptedCompletedDeltaFailsAndReleasesItsReservation() {
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        val pending = ProductionRealtimeSessionClient.HostHistoryAccumulator(
            ProductionTerminalProfile.GRAPHICS, budget, 1, delta = context)
        val corrupted = response(ByteArray(608) { 7 }).toBuilder()
            .setObjectSha256(ByteString.copyFrom(ByteArray(32)))
            .build()
        try {
            val failure = assertFailsWith<IllegalArgumentException> { pending.accept(corrupted) }
            assertTrue(failure.message.orEmpty().contains("digest mismatch"))
            pending.fail(failure)
            assertEquals(0L, budget.reservedBytes)
            assertEquals(null, pending.accept(response(ByteArray(608) { 7 })))
        } finally { pending.destroy() }
    }

    @Test fun onlyExplicitFullSnapshotErrorMayTriggerFallback() {
        val fallback = HostHistoryResponsePayload.newBuilder()
            .setStatus(HostHistoryResponseStatusV1.HOST_HISTORY_RESPONSE_STATUS_V1_REJECTED)
            .setErrorCode("NEEDS_FULL_SNAPSHOT").setSnapshotSeq(43).build()
        context.validateNeedsFullSnapshot(fallback)
        context.validateNeedsFullSnapshot(fallback.toBuilder().setSnapshotSeq(0).build())
        listOf(
            fallback.toBuilder().setSnapshotSeq(-1).build(),
            fallback.toBuilder().setErrorCode("AEAD_FAILURE").build(),
            fallback.toBuilder().setStateDelta(metadata()).build(),
        ).forEach { assertFailsWith<IllegalArgumentException> { context.validateNeedsFullSnapshot(it) } }
    }
}
