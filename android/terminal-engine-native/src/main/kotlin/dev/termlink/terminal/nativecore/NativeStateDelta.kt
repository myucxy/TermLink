package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalWidthProfile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean

enum class NativeDeltaOutcome { APPLIED, ALREADY_APPLIED }

/** A detached canonical snapshot; the caller must durably install it before advancing its ACK. */
class NativeDeltaAppliedSnapshot internal constructor(
    val outcome: NativeDeltaOutcome,
    val nextSourceSequence: Long,
    private val snapshot: ByteArray,
    private val sha256: ByteArray,
) : AutoCloseable {
    private val closed = AtomicBoolean(false)
    val snapshotByteCount: Int get() = snapshot.size

    @Synchronized fun copySnapshot(): ByteArray {
        check(!closed.get()) { "Applied Delta snapshot has been released" }
        return snapshot.copyOf()
    }

    @Synchronized fun copySha256(): ByteArray {
        check(!closed.get()) { "Applied Delta snapshot has been released" }
        return sha256.copyOf()
    }

    @Synchronized override fun close() {
        if (closed.compareAndSet(false, true)) {
            snapshot.fill(0)
            sha256.fill(0)
        }
    }
}

/** Snapshot 2.22 Delta V1 codec. Inputs are borrowed only during each synchronous call. */
class NativeStateDelta(private val bridge: NativeBridge = JniNativeBridge) {
    fun export(
        base: ByteArray,
        target: ByteArray,
        contextId: ByteArray,
        maxBytes: Int = TERMLINK_STATE_DELTA_MAX_BYTES,
        widthProfile: TerminalWidthProfile = TerminalWidthProfile.LEGACY_V1,
        outputProfile: TerminalOutputProfile = TerminalOutputProfile.UTF8,
    ): ByteArray {
        validateSnapshot(base)
        validateSnapshot(target)
        validateContext(contextId)
        validateLimit(maxBytes)
        val ownedContext = contextId.copyOf()
        return try { when (val result = bridge.deltaExportV1(base, target, ownedContext, stateProfile(widthProfile, outputProfile), maxBytes)) {
            is NativeBridgeResult.Failure -> throw result.toException("deltaExportV1")
            is NativeBridgeResult.Success -> result.value.also {
                if (it.isEmpty() || it.size > maxBytes) {
                    it.fill(0)
                    throw NativeTerminalContractException("deltaExportV1", "delta exceeds requested bound")
                }
            }
        } } finally { ownedContext.fill(0) }
    }

    fun apply(
        current: ByteArray,
        contextId: ByteArray,
        delta: ByteArray,
        maxBytes: Int = TERMLINK_STATE_DELTA_MAX_BYTES,
        widthProfile: TerminalWidthProfile = TerminalWidthProfile.LEGACY_V1,
        outputProfile: TerminalOutputProfile = TerminalOutputProfile.UTF8,
    ): NativeDeltaAppliedSnapshot {
        validateSnapshot(current)
        validateContext(contextId)
        validateLimit(maxBytes)
        require(delta.isNotEmpty() && delta.size <= maxBytes) { "Delta exceeds requested bound" }
        val ownedContext = contextId.copyOf()
        return try { when (val result = bridge.deltaApplyV1(current, ownedContext, stateProfile(widthProfile, outputProfile), delta, maxBytes)) {
            is NativeBridgeResult.Failure -> throw result.toException("deltaApplyV1")
            is NativeBridgeResult.Success -> {
                val value = result.value
                try {
                val outcome = when (value.outcome) {
                    1 -> NativeDeltaOutcome.APPLIED
                    2 -> NativeDeltaOutcome.ALREADY_APPLIED
                    else -> throw NativeTerminalContractException("deltaApplyV1", "unknown apply outcome")
                }
                if (value.nextSourceSequence < 1 || value.snapshotBytes != value.snapshot.size.toLong() ||
                    value.targetSha256.size != 32 ||
                    (outcome == NativeDeltaOutcome.ALREADY_APPLIED && !value.snapshot.contentEquals(current))
                ) {
                    throw NativeTerminalContractException("deltaApplyV1", "inconsistent snapshot result")
                }
                try {
                    validateSnapshot(value.snapshot)
                } catch (_: NativeTerminalException) {
                    throw NativeTerminalContractException("deltaApplyV1", "native returned a non-graphics snapshot")
                }
                NativeDeltaAppliedSnapshot(outcome, value.nextSourceSequence, value.snapshot, value.targetSha256)
                } catch (error: Throwable) {
                    value.snapshot.fill(0)
                    value.targetSha256.fill(0)
                    throw error
                }
            }
        } } finally { ownedContext.fill(0) }
    }

    /** Validates the detached target through native import without mutating the live renderer. */
    fun inspectGraphicsSnapshotSize(snapshot: ByteArray): TerminalSize =
        NativeTerminalHandle.importCheckpoint(bridge, snapshot, graphicsProfile = true).use {
            it.renderV5().size
        }

    private fun validateSnapshot(snapshot: ByteArray) {
        if (snapshot.size !in 104..TERMLINK_GRAPHICS_SNAPSHOT_MAX_BYTES ||
            !snapshot.copyOfRange(0, 8).contentEquals(SNAPSHOT_MAGIC)
        ) {
            throw NativeTerminalException("stateDelta.profile", NativeTerminalStatus.INCOMPATIBLE_DELTA)
        }
        val header = ByteBuffer.wrap(snapshot).order(ByteOrder.LITTLE_ENDIAN)
        if (header.getShort(8).toInt() != 2 || header.getShort(10).toInt() != 22 ||
            header.getShort(12).toInt() != 22 || header.getShort(14).toInt() != 104
        ) {
            throw NativeTerminalException("stateDelta.profile", NativeTerminalStatus.INCOMPATIBLE_DELTA)
        }
    }

    private fun validateContext(contextId: ByteArray) {
        require(contextId.size == 32 && contextId.any { it.toInt() != 0 }) {
            "Delta context ID must contain 32 nonzero-context bytes"
        }
    }

    private fun validateLimit(maxBytes: Int) {
        require(maxBytes in 1..TERMLINK_STATE_DELTA_MAX_BYTES) { "Delta limit exceeds native bound" }
    }

    private fun stateProfile(width: TerminalWidthProfile, output: TerminalOutputProfile): Int =
        width.wireValue + (if (output == TerminalOutputProfile.UTF8_WITH_STANDALONE_C1) 2 else 0)

    private fun NativeBridgeResult.Failure.toException(operation: String) = NativeTerminalException(
        operation, NativeTerminalStatus.fromCode(statusCode), statusCode, diagnostic,
    )

    private companion object {
        val SNAPSHOT_MAGIC = "TLSNP2S\u0000".encodeToByteArray()
    }
}
