package dev.termlink.product

import dev.termlink.crypto.DeltaCrypto
import dev.termlink.crypto.TerminalSnapshotCrypto
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.network.production.ProductionStateDeltaNeedsFullSnapshotException
import dev.termlink.network.production.ProductionTerminalProfile
import dev.termlink.protocol.realtime.v1.StateDeltaMetadataV1
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalWidthProfile
import dev.termlink.terminal.nativecore.NativeDeltaOutcome
import dev.termlink.terminal.nativecore.NativeStateDelta
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.UUID
import com.google.protobuf.ByteString

internal class RetiredGraphicsDeltaBaseException : RuntimeException()

internal data class GraphicsDeltaBootstrap<T>(
    val restored: T?,
    val requireFullSnapshot: Boolean,
)

/** A rejected or retired base must bypass the same stale local checkpoint. */
internal suspend fun <T> restorePreferredGraphicsDelta(
    available: Boolean,
    restoreDelta: suspend () -> T?,
): GraphicsDeltaBootstrap<T> {
    if (!available) return GraphicsDeltaBootstrap(null, false)
    return try {
        GraphicsDeltaBootstrap(restoreDelta(), false)
    } catch (_: ProductionStateDeltaNeedsFullSnapshotException) {
        GraphicsDeltaBootstrap(null, true)
    } catch (_: RetiredGraphicsDeltaBaseException) {
        GraphicsDeltaBootstrap(null, true)
    } catch (error: SessionOpenException) {
        // A durable local checkpoint can be absent or invalid after an app
        // restart/cache eviction. Replace it with the authenticated Host
        // snapshot; other session failures remain fail-closed.
        if (error.failure.code == SessionOpenFailureCode.LOCAL_CHECKPOINT) {
            GraphicsDeltaBootstrap(null, true)
        } else {
            throw error
        }
    }
}

internal fun isCurrentGraphicsDeltaBase(checkpoint: ByteArray): Boolean {
    if (checkpoint.size < 104 || !checkpoint.copyOfRange(0, 8)
            .contentEquals("TLSNP2S\u0000".encodeToByteArray())) return false
    val header = ByteBuffer.wrap(checkpoint).order(ByteOrder.LITTLE_ENDIAN)
    return header.getShort(8).toInt() == 2 && header.getShort(10).toInt() == 22 &&
        header.getShort(12).toInt() == 22 && header.getShort(14).toInt() == 104
}

internal fun graphicsDeltaProfile(width: TerminalWidthProfile, output: TerminalOutputProfile): Int =
    width.wireValue + (if (output == TerminalOutputProfile.UTF8_WITH_STANDALONE_C1) 2 else 0)

internal fun graphicsDeltaReservationBytes(sealedBytes: Int, baseBytes: Int, targetBytes: Long): Long {
    require(sealedBytes > 0 && baseBytes > 0 && targetBytes in 104..(128L * 1024 * 1024)) {
        "State Delta memory bounds are invalid"
    }
    return maxOf(sealedBytes.toLong(), baseBytes.toLong(), targetBytes)
}

/** Owns the locally resealed target and the target-sized memory admission through checkpoint binding. */
internal class SealedGraphicsDeltaTarget(
    val snapshotSequence: ULong,
    val size: TerminalSize,
    val sealedSnapshot: ByteArray,
    private val reservation: TerminalBufferBudget.Reservation,
) : AutoCloseable {
    override fun close() {
        sealedSnapshot.fill(0)
        reservation.close()
    }
}

/** Authenticates, applies, validates and reseals one Host Delta without touching the live renderer. */
internal fun applyGraphicsStateDelta(
    pairwiseKey: ByteArray,
    sealedDelta: ByteArray,
    metadata: StateDeltaMetadataV1,
    baseCheckpoint: ByteArray,
    baseSnapshotSequence: ULong,
    baseObservedLocalInputEpoch: ULong,
    sessionId: UUID,
    pcDeviceId: UUID,
    recipientDeviceId: UUID,
    keyEpoch: Long,
    terminalProfile: ProductionTerminalProfile,
    widthProfile: TerminalWidthProfile,
    outputProfile: TerminalOutputProfile,
    budget: TerminalBufferBudget = TerminalBufferBudget.global,
    native: NativeStateDelta = NativeStateDelta(),
): SealedGraphicsDeltaTarget {
    require(terminalProfile.isGraphics && terminalProfile.widthPolicy == widthProfile.wireValue) {
        "State Delta terminal profile does not match its width policy"
    }
    val targetBytes = metadata.targetSnapshotBytes
    val context = DeltaCrypto.deriveContextId(sessionId, pcDeviceId,
        terminalProfile.stateContract)
    val baseHash = MessageDigest.getInstance("SHA-256").digest(baseCheckpoint)
    val declaredBaseHash = metadata.baseSnapshotSha256.toByteArray()
    val targetHash = metadata.targetSnapshotSha256.toByteArray()
    var reservation: TerminalBufferBudget.Reservation? = null
    var sealedSnapshot: ByteArray? = null
    var ownsReservation = true
    try {
        reservation = budget.reserve(graphicsDeltaReservationBytes(
            sealedDelta.size, baseCheckpoint.size, targetBytes))
        val targetNext = metadata.targetNextSourceSequence.toULong()
        val expectedProfile = graphicsDeltaProfile(widthProfile, outputProfile)
        require(baseSnapshotSequence < Long.MAX_VALUE.toULong() &&
            targetNext <= Long.MAX_VALUE.toULong() &&
            metadata.contextId == ByteString.copyFrom(context) &&
            metadata.profile == expectedProfile &&
            metadata.baseNextSourceSequence.toULong() == baseSnapshotSequence + 1uL &&
            metadata.baseSnapshotBytes == baseCheckpoint.size.toLong() &&
            MessageDigest.isEqual(declaredBaseHash, baseHash) &&
            targetNext > metadata.baseNextSourceSequence.toULong() &&
            targetHash.size == 32) { "State Delta base or target metadata changed" }
        val opened = DeltaCrypto.open(pairwiseKey, sealedDelta, sessionId, pcDeviceId,
            recipientDeviceId, keyEpoch, terminalProfile.stateContract,
            context, expectedProfile,
            baseSnapshotSequence + 1uL, targetNext, baseCheckpoint.size.toULong(),
            targetBytes.toULong(), baseHash, targetHash, baseObservedLocalInputEpoch)
        try {
            native.apply(baseCheckpoint, context, opened.delta, widthProfile = widthProfile,
                outputProfile = outputProfile).use { applied ->
                require(applied.outcome == NativeDeltaOutcome.APPLIED &&
                    applied.nextSourceSequence.toULong() == targetNext &&
                    applied.snapshotByteCount.toLong() == targetBytes) {
                    "Native State Delta target sequence or length changed"
                }
                val nativeHash = applied.copySha256()
                try {
                    require(MessageDigest.isEqual(nativeHash, targetHash)) {
                        "Native State Delta target hash changed"
                    }
                } finally { nativeHash.fill(0) }
                val target = applied.copySnapshot()
                try {
                    val actualHash = MessageDigest.getInstance("SHA-256").digest(target)
                    try {
                        require(MessageDigest.isEqual(actualHash, targetHash)) {
                            "State Delta target bytes failed SHA-256 verification"
                        }
                    } finally { actualHash.fill(0) }
                    val size = native.inspectGraphicsSnapshotSize(target)
                    val snapshotSequence = targetNext - 1uL
                    sealedSnapshot = TerminalSnapshotCrypto.sealTerminalCoreGraphicsSnapshot(
                        pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch,
                        snapshotSequence, size.columns, size.rows,
                        opened.targetInitialTranscriptBeforeLineId,
                        opened.targetObservedLocalInputEpoch, target,
                        stateContract = terminalProfile.stateContract)
                    val result = SealedGraphicsDeltaTarget(snapshotSequence, size,
                        requireNotNull(sealedSnapshot), requireNotNull(reservation))
                    ownsReservation = false
                    return result
                } finally { target.fill(0) }
            }
        } finally { opened.delta.fill(0) }
    } finally {
        context.fill(0)
        baseHash.fill(0)
        declaredBaseHash.fill(0)
        targetHash.fill(0)
        if (ownsReservation) {
            sealedSnapshot?.fill(0)
            reservation?.close()
        }
    }
}
