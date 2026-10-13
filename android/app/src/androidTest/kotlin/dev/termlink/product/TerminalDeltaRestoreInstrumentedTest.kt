package dev.termlink.product

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.protobuf.ByteString
import dev.termlink.crypto.TerminalSnapshotCrypto
import dev.termlink.model.HeapState
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.network.production.ProductionTerminalProfile
import dev.termlink.protocol.realtime.v1.StateDeltaMetadataV1
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalWidthProfile
import dev.termlink.terminal.nativecore.JniNativeBridge
import dev.termlink.terminal.nativecore.NativeBridge
import dev.termlink.terminal.nativecore.NativeBridgeDeltaApply
import dev.termlink.terminal.nativecore.NativeBridgeResult
import dev.termlink.terminal.nativecore.NativeStateDelta
import java.security.MessageDigest
import java.security.GeneralSecurityException
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Real JNI apply and Snapshot reseal over one C# sealed Delta and canonical graphics pair. */
@RunWith(AndroidJUnit4::class)
class TerminalDeltaRestoreInstrumentedTest {
    private val vector by lazy {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        instrumentation.context.assets.open("state-delta-product-v1.json").bufferedReader().use {
            Json.parseToJsonElement(it.readText()).jsonObject
        }
    }

    private fun field(name: String) = vector.getValue(name).jsonPrimitive.content
    private fun bytes(name: String) = field(name).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun id(name: String) = UUID.fromString(field(name))

    private fun metadata() = StateDeltaMetadataV1.newBuilder()
        .setContextId(ByteString.copyFrom(bytes("context_id")))
        .setProfile(field("profile").toInt())
        .setBaseNextSourceSequence(field("base_snapshot_sequence").toLong() + 1)
        .setBaseSnapshotBytes(bytes("base_snapshot").size.toLong())
        .setBaseSnapshotSha256(ByteString.copyFrom(bytes("base_sha256")))
        .setTargetNextSourceSequence(field("target_next_sequence").toLong())
        .setTargetSnapshotBytes(bytes("target_snapshot").size.toLong())
        .setTargetSnapshotSha256(ByteString.copyFrom(bytes("target_sha256")))
        .build()

    @Test
    fun sealedWindowsDeltaRestoresCanonicalTargetThroughJniAndResealsAuthenticatedEpoch() {
        val pairwise = bytes("pairwise_key")
        val base = bytes("base_snapshot")
        val target = bytes("target_snapshot")
        val sealedDelta = bytes("sealed_object")
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        try {
            assertArrayEquals(bytes("base_sha256"), MessageDigest.getInstance("SHA-256").digest(base))
            assertArrayEquals(bytes("target_sha256"), MessageDigest.getInstance("SHA-256").digest(target))
            assertEquals(1, field("profile").toInt())
            val sessionId = id("session_id")
            val pcDeviceId = id("pc_device_id")
            val recipientDeviceId = id("recipient_device_id")
            val epoch = field("key_epoch").toLong()
            val targetSequence = field("target_next_sequence").toULong() - 1uL
            applyGraphicsStateDelta(
                pairwise, sealedDelta, metadata(), base,
                field("base_snapshot_sequence").toULong(),
                field("base_observed_local_input_epoch").toULong(),
                sessionId, pcDeviceId, recipientDeviceId, epoch,
                ProductionTerminalProfile.GRAPHICS,
                TerminalWidthProfile.LEGACY_V1, TerminalOutputProfile.UTF8, budget,
            ).use { result ->
                assertEquals(targetSequence, result.snapshotSequence)
                val opened = TerminalSnapshotCrypto.openTerminalCoreGraphicsSnapshot(
                    pairwise, result.sealedSnapshot, sessionId, pcDeviceId,
                    recipientDeviceId, epoch,
                    TerminalSnapshotCrypto.terminalCoreGraphicsSnapshotKeyId(recipientDeviceId, epoch,
                        ProductionTerminalProfile.GRAPHICS.stateContract),
                    targetSequence, result.size.columns, result.size.rows,
                    expectedStateContract = ProductionTerminalProfile.GRAPHICS.stateContract,
                )
                try {
                    assertArrayEquals(target, opened.checkpoint)
                    assertEquals(field("target_observed_local_input_epoch").toULong(),
                        opened.observedLocalInputEpoch)
                    assertEquals(field("target_initial_transcript_before_line_id").toULong(),
                        opened.initialTranscriptBeforeLineId)
                } finally { opened.checkpoint.fill(0) }
                assertTrue(budget.reservedBytes > 0)
            }
            assertEquals(0L, budget.reservedBytes)
            assertThrows(GeneralSecurityException::class.java) {
                applyGraphicsStateDelta(
                    pairwise, sealedDelta.copyOf().also {
                        it[it.lastIndex] = (it.last().toInt() xor 1).toByte()
                    }, metadata(), base,
                    field("base_snapshot_sequence").toULong(),
                    field("base_observed_local_input_epoch").toULong(),
                    sessionId, pcDeviceId, recipientDeviceId, epoch,
                    ProductionTerminalProfile.GRAPHICS,
                    TerminalWidthProfile.LEGACY_V1, TerminalOutputProfile.UTF8, budget,
                )
            }
            assertEquals(0L, budget.reservedBytes)
            assertThrows(IllegalArgumentException::class.java) {
                applyGraphicsStateDelta(
                    pairwise, sealedDelta,
                    metadata().toBuilder().setTargetSnapshotSha256(
                        ByteString.copyFrom(ByteArray(32))).build(), base,
                    field("base_snapshot_sequence").toULong(),
                    field("base_observed_local_input_epoch").toULong(),
                    sessionId, pcDeviceId, recipientDeviceId, epoch,
                    ProductionTerminalProfile.GRAPHICS,
                    TerminalWidthProfile.LEGACY_V1, TerminalOutputProfile.UTF8, budget,
                )
            }
            assertEquals(0L, budget.reservedBytes)
        } finally {
            pairwise.fill(0)
            base.fill(0)
            target.fill(0)
            sealedDelta.fill(0)
        }
    }

    @Test
    fun changedNativeSequenceLengthOrDigestNeverProducesASealedTarget() {
        val pairwise = bytes("pairwise_key")
        val base = bytes("base_snapshot")
        val sealedDelta = bytes("sealed_object")
        val budget = TerminalBufferBudget { HeapState(512L * 1024 * 1024, 0) }
        try {
            for (corruption in 0..2) {
                val bridge = object : NativeBridge by JniNativeBridge {
                    override fun deltaApplyV1(current: ByteArray, contextId: ByteArray,
                        profile: Int, delta: ByteArray, maxBytes: Int,
                    ): NativeBridgeResult<NativeBridgeDeltaApply> {
                        return when (val result = JniNativeBridge.deltaApplyV1(
                            current, contextId, profile, delta, maxBytes)) {
                            is NativeBridgeResult.Failure -> result
                            is NativeBridgeResult.Success -> {
                                val value = result.value
                                NativeBridgeResult.Success(when (corruption) {
                                    0 -> value.copy(nextSourceSequence = value.nextSourceSequence + 1)
                                    1 -> value.copy(snapshotBytes = value.snapshotBytes + 1)
                                    else -> value.also { it.targetSha256[0] =
                                        (it.targetSha256[0].toInt() xor 1).toByte() }
                                })
                            }
                        }
                    }
                }
                assertThrows(Exception::class.java) {
                    applyGraphicsStateDelta(
                        pairwise, sealedDelta, metadata(), base,
                        field("base_snapshot_sequence").toULong(),
                        field("base_observed_local_input_epoch").toULong(),
                        id("session_id"), id("pc_device_id"), id("recipient_device_id"),
                        field("key_epoch").toLong(), ProductionTerminalProfile.GRAPHICS,
                        TerminalWidthProfile.LEGACY_V1,
                        TerminalOutputProfile.UTF8, budget, NativeStateDelta(bridge),
                    )
                }
                assertEquals(0L, budget.reservedBytes)
            }
        } finally {
            pairwise.fill(0)
            base.fill(0)
            sealedDelta.fill(0)
        }
    }
}
