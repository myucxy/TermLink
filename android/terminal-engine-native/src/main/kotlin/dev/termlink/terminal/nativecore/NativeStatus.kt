package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalEngineFailureDiagnostics
import dev.termlink.terminal.api.TerminalInputDiagnostic
import dev.termlink.terminal.api.TerminalProtocolDiagnostic

/**
 * Stable status values shared with the TermLink terminal C ABI.
 *
 * [UNKNOWN] deliberately retains the raw value on [NativeTerminalException], so a newer native
 * library cannot be mistaken for a known failure by an older Android adapter.
 */
enum class NativeTerminalStatus(val code: Int) {
    OK(0),
    NULL_POINTER(1),
    INVALID_ARGUMENT(2),
    INVALID_DIMENSIONS(3),
    LIMIT_EXCEEDED(4),
    SEQUENCE_MISMATCH(5),
    FAILED_CLOSED(6),
    UNSUPPORTED(7),
    GAP(8),
    MALFORMED_SNAPSHOT(9),
    INCOMPATIBLE_SNAPSHOT(10),
    TIME_OVERFLOW(11),
    SNAPSHOT_NOT_ELIGIBLE(12),
    PRESENTATION_DEFERRED(13),
    HOST_EFFECT_VERSION_REQUIRED(14),
    RENDER_FRAME_VERSION_REQUIRED(15),
    HISTORY_STALE_CAPTURE(16),
    HISTORY_STALE_REVISION(17),
    HISTORY_STALE_ANCHOR(18),
    HISTORY_PAGE_BUDGET(19),
    MALFORMED_DELTA(20),
    INCOMPATIBLE_DELTA(21),
    NEEDS_FULL_SNAPSHOT(22),
    PANIC(255),
    BRIDGE_FAILURE(256),
    MALFORMED_BRIDGE_RESULT(257),
    ABI_MISMATCH(258),
    UNKNOWN(-1),
    ;

    companion object {
        fun fromCode(code: Int): NativeTerminalStatus = entries.firstOrNull {
            it !== UNKNOWN && it.code == code
        } ?: UNKNOWN

        internal fun fromBridgeFailureCode(code: Int): NativeTerminalStatus = when (code) {
            BRIDGE_FAILURE.code,
            MALFORMED_BRIDGE_RESULT.code,
            ABI_MISMATCH.code,
            -> MALFORMED_BRIDGE_RESULT
            else -> fromCode(code)
        }
    }
}

class NativeTerminalException(
    val operation: String,
    val status: NativeTerminalStatus,
    val rawStatusCode: Int = status.code,
    diagnostic: String? = null,
    cause: Throwable? = null,
) : IllegalStateException(
    buildString {
        append("Native terminal operation '")
        append(operation)
        append("' failed with ")
        append(status.name)
        append(" (")
        append(rawStatusCode)
        append(')')
        // Structured output evidence belongs in the explicit local details UI, not crash telemetry.
        if (!diagnostic.isNullOrBlank() && !diagnostic.startsWith("TL-DIAG/") &&
            !diagnostic.startsWith("TL-INPUT-DIAG/")) {
            append(": ")
            append(diagnostic)
        }
    },
    cause,
), TerminalEngineFailureDiagnostics {
    override val terminalProtocolDiagnostic = TerminalProtocolDiagnostic.parse(diagnostic)
    override val terminalInputDiagnostic = TerminalInputDiagnostic.parse(diagnostic)
    override val terminalOperation: String
        get() = operation
    override val terminalStatusName: String
        get() = status.name
    override val terminalStatusCode: Int
        get() = rawStatusCode
}

class NativeTerminalContractException(
    val operation: String,
    message: String,
) : IllegalStateException(
    "Malformed result from native terminal '$operation': $message",
), TerminalEngineFailureDiagnostics {
    override val terminalOperation: String
        get() = operation
    override val terminalStatusName: String
        get() = NativeTerminalStatus.MALFORMED_BRIDGE_RESULT.name
    override val terminalStatusCode: Int
        get() = NativeTerminalStatus.MALFORMED_BRIDGE_RESULT.code
}

sealed interface NativeBridgeResult<out T : Any> {
    data class Success<T : Any>(val value: T) : NativeBridgeResult<T>

    data class Failure(
        val statusCode: Int,
        val diagnostic: String? = null,
    ) : NativeBridgeResult<Nothing> {
        init {
            require(statusCode != NativeTerminalStatus.OK.code) {
                "A failed native result cannot use TL_STATUS_OK"
            }
        }
    }
}
