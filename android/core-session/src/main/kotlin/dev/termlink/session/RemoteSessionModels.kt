package dev.termlink.session

import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalInput
import java.util.UUID

/**
 * Declares the cursor and generation installed before realtime starts.
 * Every non-empty state proves the renderer contains the corresponding prefix.
 * A renderer must never be anchored past content it did not install.
 */
sealed interface RendererBootstrapState {
    val sequence: Long
    val rendererGeneration: Long

    data class Empty(
        override val rendererGeneration: Long = 1,
    ) : RendererBootstrapState {
        override val sequence: Long = 0

        init {
            require(rendererGeneration > 0) { "renderer generation must be positive" }
        }
    }

    /** Created only after a verified contiguous replay from event 1 through [sequence]. */
    data class FullPrefix(
        override val sequence: Long,
        override val rendererGeneration: Long,
    ) : RendererBootstrapState {
        init {
            require(sequence > 0) { "full renderer prefix must be non-empty" }
            require(rendererGeneration > 0) { "renderer generation must be positive" }
        }
    }

    /** Created only after the exact durable checkpoint has been installed into the renderer. */
    class ExactCheckpoint(
        override val sequence: Long,
        override val rendererGeneration: Long,
        val snapshotId: UUID,
        ciphertextSha256: ByteArray,
    ) : RendererBootstrapState {
        val ciphertextSha256: ByteArray = ciphertextSha256.copyOf()

        init {
            require(sequence > 0) { "checkpoint sequence must be positive" }
            require(rendererGeneration > 0) { "renderer generation must be positive" }
            require(ciphertextSha256.size == 32) { "checkpoint digest must be SHA-256" }
        }
    }
}

data class DurableAckCommit(
    val sequence: Long,
    val rendererGeneration: Long,
) {
    init {
        require(sequence >= 0) { "durable ACK must not be negative" }
        require(rendererGeneration > 0) { "renderer generation must be positive" }
    }
}

sealed interface RemoteSessionState {
    val lastAckedSequence: Long

    data class Live(
        override val lastAckedSequence: Long,
    ) : RemoteSessionState

    data class Reconnecting(
        override val lastAckedSequence: Long,
        val reason: String? = null,
    ) : RemoteSessionState

    data class ReadOnly(
        override val lastAckedSequence: Long,
        val reason: String,
    ) : RemoteSessionState

    data class Controlling(
        override val lastAckedSequence: Long,
        val leaseId: String,
        val expiresAtEpochMillis: Long,
        val leaseVersion: Long = 0,
    ) : RemoteSessionState

    data class Exited(
        override val lastAckedSequence: Long,
        val exitCode: Int?,
    ) : RemoteSessionState

    data class HistoryGap(
        override val lastAckedSequence: Long,
        val expectedSequence: Long,
        val receivedSequence: Long,
        val generation: Long = 0,
    ) : RemoteSessionState
}

enum class RemoteLifecycle {
    RUNNING,
    INTERRUPTED,
    ENDED,
}

sealed interface RemoteSessionEvent {
    val sequence: Long

    data class Output(
        override val sequence: Long,
        val bytes: ByteArray,
        val sourceEventSequence: Long = sequence,
        val presentationRevision: Long = sourceEventSequence,
    ) : RemoteSessionEvent {
        init {
            require(sourceEventSequence > 0) { "source event sequence must be positive" }
            require(presentationRevision > 0) { "presentation revision must be positive" }
        }
    }

    data class Resize(
        override val sequence: Long,
        val size: TerminalSize,
    ) : RemoteSessionEvent

    data class Title(
        override val sequence: Long,
        val value: String,
    ) : RemoteSessionEvent

    /** An ordered session event that confirms/rejects input without rendering bytes. */
    data class InputResult(
        override val sequence: Long,
        val inputId: UUID,
        val accepted: Boolean,
        val reason: String? = null,
    ) : RemoteSessionEvent

    /** Ordered control revocation, most commonly caused by local PC input. */
    data class ControlRevoked(
        override val sequence: Long,
        val reason: String,
    ) : RemoteSessionEvent

    /** Ordered notice used by transports that deliver a lease over realtime. */
    data class ControlGranted(
        override val sequence: Long,
    ) : RemoteSessionEvent

    data class Lifecycle(
        override val sequence: Long,
        val state: RemoteLifecycle,
        val exitCode: Int? = null,
    ) : RemoteSessionEvent

    data class Gap(
        override val sequence: Long,
        val missingStartSequence: Long,
        val missingEndSequence: Long,
    ) : RemoteSessionEvent
}

data class RemoteInput(
    val inputId: UUID,
    val bytes: ByteArray,
    /** Semantic form; null is encoded as RAW_BYTES for old call sites. */
    val typedInput: TerminalInput? = null,
)

data class RemoteControlLease(
    val leaseId: String,
    val expiresAtEpochMillis: Long,
    val version: Long,
)

interface RemoteSessionOutbound {
    suspend fun resume(lastAckedSequence: Long)

    /**
     * Commits a cumulative renderer cursor to durable storage. Implementations
     * The returned sequence and generation must match the requested values.
     */
    suspend fun persistAcknowledgement(
        expectedRendererGeneration: Long,
        highestContiguousSequence: Long,
    ): DurableAckCommit

    /** Publishes an already-durable cumulative ACK to the peer. */
    suspend fun acknowledge(highestContiguousSequence: Long)

    /**
     * Clears a durable cursor after the renderer has been irreversibly reset.
     * Production implementations must remove the persisted cursor before a
     * blank renderer is allowed to resume from zero.
     */
    suspend fun resetAcknowledgement(expectedRendererGeneration: Long): DurableAckCommit

    suspend fun sendInput(input: RemoteInput)

    suspend fun sendResize(size: TerminalSize)

    /**
     * Lease operations are optional for transports that do not expose control
     * (for example the fixed test relay). Production transports override these
     * hooks; defaults preserve the original outbound contract.
     */
    suspend fun renewLease(leaseId: String, leaseVersion: Long): RemoteControlLease? = null
}

/** The PC authoritatively rejected this input before it could execute. */
class RemoteInputRejectedException(
    val reasonCode: String,
) : IllegalStateException(reasonCode)

/**
 * The sender cannot prove whether the PC accepted the input. Callers must not
 * retry it with a new input ID because doing so could execute a command twice.
 */
class RemoteInputDeliveryUnknownException(message: String) : IllegalStateException(message)

sealed interface EventAcceptance {
    data class Applied(val sequence: Long) : EventAcceptance
    data class Duplicate(val sequence: Long) : EventAcceptance
    data class HistoryGap(val expectedSequence: Long, val receivedSequence: Long) : EventAcceptance
    data object IgnoredAfterExit : EventAcceptance
}

sealed interface InputSubmission {
    data class Sent(val inputId: UUID) : InputSubmission
    data class RejectedByRemote(val inputId: UUID, val reasonCode: String) : InputSubmission
    data class DeliveryUnknown(val inputId: UUID) : InputSubmission
    data object RejectedNoLease : InputSubmission
    data object RejectedExited : InputSubmission
    data object RejectedEmpty : InputSubmission
}
