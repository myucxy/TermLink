package dev.termlink.session

import dev.termlink.terminal.api.TerminalSize
import java.util.UUID

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
    ) : RemoteSessionEvent

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
)

data class RemoteControlLease(
    val leaseId: String,
    val expiresAtEpochMillis: Long,
    val version: Long,
)

interface RemoteSessionOutbound {
    suspend fun resume(lastAckedSequence: Long)

    suspend fun acknowledge(highestContiguousSequence: Long)

    suspend fun sendInput(input: RemoteInput)

    suspend fun sendResize(size: TerminalSize)

    /**
     * Lease operations are optional for transports that do not expose control
     * (for example the fixed test relay). Production transports override these
     * hooks; defaults preserve the original outbound contract.
     */
    suspend fun renewLease(leaseId: String, leaseVersion: Long): RemoteControlLease? = null
}

sealed interface EventAcceptance {
    data class Applied(val sequence: Long) : EventAcceptance
    data class Duplicate(val sequence: Long) : EventAcceptance
    data class HistoryGap(val expectedSequence: Long, val receivedSequence: Long) : EventAcceptance
    data object IgnoredAfterExit : EventAcceptance
}

sealed interface InputSubmission {
    data class Sent(val inputId: UUID) : InputSubmission
    data class TransportFailed(val inputId: UUID) : InputSubmission
    data object RejectedNoLease : InputSubmission
    data object RejectedExited : InputSubmission
    data object RejectedEmpty : InputSubmission
}
