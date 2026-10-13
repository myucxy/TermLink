package dev.termlink.product

import android.content.Context
import android.database.sqlite.SQLiteFullException
import androidx.annotation.StringRes
import dev.termlink.harness.R
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.terminal.nativecore.NativeTerminalContractException
import dev.termlink.terminal.nativecore.NativeTerminalException
import dev.termlink.terminal.nativecore.NativeTerminalStatus
import java.io.IOException
import java.net.SocketTimeoutException

/** Stable, non-sensitive failure categories exposed by the Android session UI. */
enum class SessionOpenFailureCode(
    val stableCode: String,
    val retryable: Boolean,
    @StringRes val detailResource: Int,
    @StringRes val actionResource: Int,
) {
    SESSION_UNAVAILABLE("TL-S001", false, R.string.terminal_error_session_unavailable, R.string.terminal_error_action_refresh),
    SESSION_OFFLINE("TL-S002", true, R.string.terminal_error_session_offline, R.string.terminal_error_action_wait_online),
    SESSION_ENDED("TL-S003", false, R.string.terminal_error_session_ended, R.string.terminal_error_action_close_tab),
    PC_UNAVAILABLE("TL-S004", true, R.string.terminal_error_pc_unavailable, R.string.terminal_error_action_refresh),
    PC_NOT_TRUSTED("TL-S005", false, R.string.terminal_error_pc_not_trusted, R.string.terminal_error_action_check_trust),
    AUTHENTICATION_EXPIRED("TL-A001", false, R.string.terminal_error_authentication_expired, R.string.terminal_error_action_sign_in),
    ACCESS_DENIED("TL-A002", false, R.string.terminal_error_access_denied, R.string.terminal_error_action_check_access),
    SERVICE_TIMEOUT("TL-N001", true, R.string.terminal_error_service_timeout, R.string.terminal_error_action_check_network),
    SERVICE_UNREACHABLE("TL-N002", true, R.string.terminal_error_service_unreachable, R.string.terminal_error_action_check_network),
    SERVER_REJECTED("TL-N003", true, R.string.terminal_error_server_rejected, R.string.terminal_error_action_retry),
    SNAPSHOT_NOT_READY("TL-H101", true, R.string.terminal_error_snapshot_not_ready, R.string.terminal_error_action_retry_later),
    SNAPSHOT_STALE("TL-H102", true, R.string.terminal_error_snapshot_stale, R.string.terminal_error_action_retry_later),
    SNAPSHOT_METADATA("TL-H103", true, R.string.terminal_error_snapshot_metadata, R.string.terminal_error_action_retry),
    SNAPSHOT_DOWNLOAD("TL-H104", true, R.string.terminal_error_snapshot_download, R.string.terminal_error_action_check_network),
    SNAPSHOT_INTEGRITY("TL-H105", true, R.string.terminal_error_snapshot_integrity, R.string.terminal_error_action_retry),
    SNAPSHOT_RECIPIENT("TL-H106", false, R.string.terminal_error_snapshot_recipient, R.string.terminal_error_action_republish),
    SNAPSHOT_KEY("TL-H107", false, R.string.terminal_error_snapshot_key, R.string.terminal_error_action_refresh_keys),
    SNAPSHOT_DECRYPTION("TL-H108", false, R.string.terminal_error_snapshot_decryption, R.string.terminal_error_action_republish),
    SNAPSHOT_FORMAT("TL-H109", false, R.string.terminal_error_snapshot_format, R.string.terminal_error_action_update),
    TERMINAL_RENDERER("TL-H110", false, R.string.terminal_error_terminal_renderer, R.string.terminal_error_action_update),
    LOCAL_CHECKPOINT("TL-H111", true, R.string.terminal_error_local_checkpoint, R.string.terminal_error_action_retry),
    SNAPSHOT_INSTALL("TL-H112", true, R.string.terminal_error_snapshot_install, R.string.terminal_error_action_retry),
    HISTORY_NOT_READY("TL-H113", true, R.string.terminal_error_history_not_ready, R.string.terminal_error_action_wait_history),
    // A history gap can self-heal: fresh host output republishes the
    // snapshot, so the session re-opens automatically once the agent
    // produces output again instead of demanding a manual retry.
    HISTORY_GAP("TL-H114", true, R.string.terminal_error_history_gap, R.string.terminal_error_action_republish_history),
    HISTORY_REPLAY("TL-H116", true, R.string.terminal_error_history_replay, R.string.terminal_error_action_retry),
    SNAPSHOT_UNSUPPORTED("TL-H117", false, R.string.terminal_error_snapshot_unsupported, R.string.terminal_error_action_update),
    SNAPSHOT_PRODUCTION_FAILED("TL-H118", true, R.string.terminal_error_snapshot_production_failed, R.string.terminal_error_action_retry),
    LOCAL_STORAGE_FULL("TL-H119", true, R.string.terminal_error_local_storage_full, R.string.terminal_error_action_free_storage),
    NATIVE_CORE_UNAVAILABLE("TL-H120", false, R.string.terminal_error_native_core_unavailable, R.string.terminal_error_action_update),
    NATIVE_RENDERER_FAILED_CLOSED("TL-H121", true, R.string.terminal_error_native_renderer_failed_closed, R.string.terminal_error_action_retry),
    UNSUPPORTED_TERMINAL_CAPABILITY("TL-H122", false, R.string.terminal_error_unsupported_terminal_capability, R.string.terminal_error_action_update),
    HISTORY_CORE_REBUILD_FAILED("TL-H123", true, R.string.terminal_error_history_core_rebuild_failed, R.string.terminal_error_action_retry),
    GAP_RECOVERY_REQUIRED("TL-H124", true, R.string.terminal_error_gap_recovery_required, R.string.terminal_error_action_wait_history),
    HOST_HISTORY_REQUIRED("TL-H125", false, R.string.terminal_error_host_history_required, R.string.terminal_error_action_update),
    MALFORMED_TERMINAL_SEQUENCE("TL-H126", false, R.string.terminal_error_malformed_sequence, R.string.terminal_error_action_update),
    TERMINAL_RESOURCE_LIMIT("TL-H127", false, R.string.terminal_error_resource_limit, R.string.terminal_error_action_update),
    TERMINAL_PROTOCOL_SERVER("TL-H128", false, R.string.terminal_error_protocol_server, R.string.terminal_error_action_update),
    TERMINAL_PROTOCOL_HOST("TL-H129", false, R.string.terminal_error_protocol_host, R.string.terminal_error_action_update),
    TERMINAL_PROTOCOL_CLIENT("TL-H130", false, R.string.terminal_error_protocol_client, R.string.terminal_error_action_update),
    TERMINAL_NEGOTIATION_TIMEOUT("TL-H131", true, R.string.terminal_error_negotiation_timeout, R.string.terminal_error_action_retry),
    TERMINAL_NEGOTIATION_INVALID("TL-H132", false, R.string.terminal_error_negotiation_invalid, R.string.terminal_error_action_update),
    TERMINAL_LOCAL_PROTOCOL("TL-H133", false, R.string.terminal_error_local_protocol, R.string.terminal_error_action_update),
    TERMINAL_HISTORY_PROFILE_MISSING("TL-H134", false, R.string.terminal_error_history_profile_missing, R.string.terminal_error_action_new_session),
    TERMINAL_HISTORY_PROFILE_INVALID("TL-H135", false, R.string.terminal_error_history_profile_invalid, R.string.terminal_error_action_new_session),
    TERMINAL_HISTORY_PROFILE_MISMATCH("TL-H136", false, R.string.terminal_error_history_profile_mismatch, R.string.terminal_error_action_new_session),
    TERMINAL_STATE_MISMATCH("TL-H137", false, R.string.terminal_error_state_mismatch, R.string.terminal_error_action_update),
    TERMINAL_HISTORY_MEMORY_LIMIT("TL-H138", true, R.string.terminal_error_history_memory_limit, R.string.terminal_error_action_free_memory),
    REALTIME_CONNECTION("TL-R001", true, R.string.terminal_error_realtime_connection, R.string.terminal_error_action_check_network),
    CONTROL_LEASE_HELD("TL-C001", true, R.string.terminal_error_control_lease, R.string.terminal_error_action_wait_lease),
    UNKNOWN("TL-X001", true, R.string.terminal_error_unknown, R.string.terminal_error_action_retry),
}

/**
 * Identifies where the protocol evidence was produced.
 *
 * Native renderer failures are detected by the Android mirror and keep the
 * historical mirror wording.  A diagnostic carried by a host SESSION_ENDED
 * event is already authenticated by the relay and must be presented as host
 * evidence instead of being labelled as a local renderer failure.
 */
enum class SessionProtocolDiagnosticSource {
    ANDROID_MIRROR,
    WINDOWS_HOST,
}

@androidx.annotation.StringRes
internal fun sessionProtocolDiagnosticContextResource(
    source: SessionProtocolDiagnosticSource,
): Int = when (source) {
    SessionProtocolDiagnosticSource.ANDROID_MIRROR -> R.string.terminal_protocol_mirror_context
    SessionProtocolDiagnosticSource.WINDOWS_HOST -> R.string.terminal_protocol_host_context
}

data class SessionOpenFailure(
    val code: SessionOpenFailureCode,
    /** Safe transport metadata only. Never store a URL, token, key ID, or terminal text here. */
    val httpStatus: Int? = null,
    val diagnosticCode: String? = null,
    /** Escaped/redacted native evidence, only for local error details. */
    val protocolDiagnostic: dev.termlink.terminal.api.TerminalProtocolDiagnostic? = null,
    /** Source of [protocolDiagnostic]; defaults to the Android mirror for old call sites. */
    val protocolDiagnosticSource: SessionProtocolDiagnosticSource = SessionProtocolDiagnosticSource.ANDROID_MIRROR,
) {
    init {
        require(diagnosticCode == null || diagnosticCode.matches(Regex("[A-Z0-9_]{1,64}"))) {
            "Session diagnostic code is not safe"
        }
    }
}

internal class SessionOpenException(
    val failure: SessionOpenFailure,
    cause: Throwable? = null,
) : Exception(failure.code.stableCode, cause)

internal enum class SessionFailureStage {
    PRECONDITION,
    SNAPSHOT_METADATA,
    SNAPSHOT_DOWNLOAD,
    SNAPSHOT_PREPARE,
    SNAPSHOT_CHECKPOINT,
    SNAPSHOT_INSTALL,
    HISTORY_METADATA,
    HISTORY_DOWNLOAD,
    HISTORY_REPLAY,
    REALTIME,
    CONTROL,
}

internal fun sessionOpenException(
    code: SessionOpenFailureCode,
    cause: Throwable? = null,
    httpStatus: Int? = null,
): SessionOpenException {
    val memoryLimit = cause?.findCause<dev.termlink.model.TerminalBufferLimitException>()
    return SessionOpenException(SessionOpenFailure(
        code = if (memoryLimit == null) code else SessionOpenFailureCode.TERMINAL_HISTORY_MEMORY_LIMIT,
        httpStatus = httpStatus,
        diagnosticCode = memoryLimit?.code,
    ), cause)
}

/** Converts implementation exceptions to a bounded public taxonomy without echoing exception text. */
internal fun classifySessionOpenException(
    error: Throwable,
    stage: SessionFailureStage,
): SessionOpenException {
    if (error is SessionOpenException) return error
    error.findCause<dev.termlink.model.TerminalBufferLimitException>()?.let { limit ->
        return SessionOpenException(SessionOpenFailure(
            SessionOpenFailureCode.TERMINAL_HISTORY_MEMORY_LIMIT,
            diagnosticCode = limit.code,
        ), error)
    }
    error.findCause<dev.termlink.network.production.TerminalCompatibilityException>()?.let { compatibility ->
        val code = when (compatibility.code) {
            "TERMINAL_RELAY_UPGRADE_REQUIRED" -> SessionOpenFailureCode.TERMINAL_PROTOCOL_SERVER
            "TERMINAL_HOST_UPGRADE_REQUIRED" -> SessionOpenFailureCode.TERMINAL_PROTOCOL_HOST
            "TERMINAL_LOCAL_PROTOCOL_MISMATCH" -> SessionOpenFailureCode.TERMINAL_LOCAL_PROTOCOL
            "TERMINAL_HISTORY_PROFILE_MISSING" -> SessionOpenFailureCode.TERMINAL_HISTORY_PROFILE_MISSING
            "TERMINAL_HISTORY_PROFILE_INVALID" -> SessionOpenFailureCode.TERMINAL_HISTORY_PROFILE_INVALID
            "TERMINAL_HISTORY_PROFILE_MISMATCH" -> SessionOpenFailureCode.TERMINAL_HISTORY_PROFILE_MISMATCH
            "TERMINAL_STATE_MISMATCH" -> SessionOpenFailureCode.TERMINAL_STATE_MISMATCH
            "TERMINAL_CLIENT_UPGRADE_REQUIRED", "TERMINAL_STATE_UNSUPPORTED" -> SessionOpenFailureCode.TERMINAL_PROTOCOL_CLIENT
            "TERMINAL_NEGOTIATION_TIMEOUT" -> SessionOpenFailureCode.TERMINAL_NEGOTIATION_TIMEOUT
            else -> SessionOpenFailureCode.TERMINAL_NEGOTIATION_INVALID
        }
        return SessionOpenException(SessionOpenFailure(code,
            diagnosticCode = compatibility.code.takeIf { it.matches(Regex("[A-Z0-9_]{1,64}")) }), error)
    }
    if (error is SQLiteFullException || error.causedBy<SQLiteFullException>()) {
        return sessionOpenException(SessionOpenFailureCode.LOCAL_STORAGE_FULL, error)
    }
    val native = error.findCause<NativeTerminalException>()
    if (native != null) {
        val code = when (native.terminalProtocolDiagnostic?.category) {
            "malformed" -> SessionOpenFailureCode.MALFORMED_TERMINAL_SEQUENCE
            "limit_exceeded" -> SessionOpenFailureCode.TERMINAL_RESOURCE_LIMIT
            "unsupported" -> SessionOpenFailureCode.UNSUPPORTED_TERMINAL_CAPABILITY
            else -> when (native.status) {
                NativeTerminalStatus.ABI_MISMATCH,
                NativeTerminalStatus.BRIDGE_FAILURE,
                NativeTerminalStatus.MALFORMED_BRIDGE_RESULT,
                -> SessionOpenFailureCode.NATIVE_CORE_UNAVAILABLE
                NativeTerminalStatus.FAILED_CLOSED ->
                    SessionOpenFailureCode.NATIVE_RENDERER_FAILED_CLOSED
                NativeTerminalStatus.UNSUPPORTED,
                NativeTerminalStatus.HOST_EFFECT_VERSION_REQUIRED,
                -> SessionOpenFailureCode.UNSUPPORTED_TERMINAL_CAPABILITY
                NativeTerminalStatus.GAP -> SessionOpenFailureCode.GAP_RECOVERY_REQUIRED
                NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT ->
                    if (native.operation == "installCheckpoint.profile") SessionOpenFailureCode.TERMINAL_STATE_MISMATCH
                    else SessionOpenFailureCode.SNAPSHOT_FORMAT
                else -> if (stage == SessionFailureStage.HISTORY_REPLAY) {
                    SessionOpenFailureCode.HISTORY_CORE_REBUILD_FAILED
                } else {
                    SessionOpenFailureCode.NATIVE_RENDERER_FAILED_CLOSED
                }
            }
        }
        return SessionOpenException(
            SessionOpenFailure(code, protocolDiagnostic = native.terminalProtocolDiagnostic), error,
        )
    }
    if (error.findCause<NativeTerminalContractException>() != null) {
        return sessionOpenException(SessionOpenFailureCode.NATIVE_RENDERER_FAILED_CLOSED, error)
    }
    if (error is UnsatisfiedLinkError || error.chainClassNameContains("UnsatisfiedLink")) {
        return sessionOpenException(SessionOpenFailureCode.NATIVE_CORE_UNAVAILABLE, error)
    }
    if (error is TermLinkApiException) {
        val code = when {
            error.status == 401 -> SessionOpenFailureCode.AUTHENTICATION_EXPIRED
            error.status == 403 -> SessionOpenFailureCode.ACCESS_DENIED
            error.status == 409 && error.code == "LEASE_HELD" -> SessionOpenFailureCode.CONTROL_LEASE_HELD
            else -> when (stage) {
                SessionFailureStage.SNAPSHOT_METADATA -> SessionOpenFailureCode.SNAPSHOT_METADATA
                SessionFailureStage.SNAPSHOT_DOWNLOAD -> SessionOpenFailureCode.SNAPSHOT_DOWNLOAD
                SessionFailureStage.HISTORY_METADATA -> SessionOpenFailureCode.HISTORY_NOT_READY
                SessionFailureStage.HISTORY_DOWNLOAD -> SessionOpenFailureCode.HISTORY_REPLAY
                SessionFailureStage.REALTIME -> SessionOpenFailureCode.REALTIME_CONNECTION
                else -> SessionOpenFailureCode.SERVER_REJECTED
            }
        }
        return sessionOpenException(code, error, error.status)
    }
    if (error is SocketTimeoutException || error.causedBy<SocketTimeoutException>()) {
        return sessionOpenException(SessionOpenFailureCode.SERVICE_TIMEOUT, error)
    }
    if (error is IOException || error.causedBy<IOException>()) {
        val code = when (stage) {
            SessionFailureStage.SNAPSHOT_DOWNLOAD -> SessionOpenFailureCode.SNAPSHOT_DOWNLOAD
            SessionFailureStage.REALTIME -> SessionOpenFailureCode.REALTIME_CONNECTION
            else -> SessionOpenFailureCode.SERVICE_UNREACHABLE
        }
        return sessionOpenException(code, error)
    }
    val normalized = error.message.orEmpty().lowercase()
    val code = when {
        "access token" in normalized || "authentication expired" in normalized || "authorization" in normalized ->
            SessionOpenFailureCode.AUTHENTICATION_EXPIRED
        "not for this device" in normalized || "recipient" in normalized -> SessionOpenFailureCode.SNAPSHOT_RECIPIENT
        "key epoch" in normalized || "key id" in normalized || "exchange public key" in normalized -> SessionOpenFailureCode.SNAPSHOT_KEY
        "integrity" in normalized || "digest" in normalized || "size mismatch" in normalized || "was empty" in normalized ->
            SessionOpenFailureCode.SNAPSHOT_INTEGRITY
        "decrypt" in normalized || "authentication tag" in normalized || "aead" in normalized ||
            error.chainClassNameContains("badtag") ->
            SessionOpenFailureCode.SNAPSHOT_DECRYPTION
        "unsupported effects" in normalized || "renderer" in normalized || "terminal state" in normalized ->
            SessionOpenFailureCode.TERMINAL_RENDERER
        "format is unsupported" in normalized || "invalid protobuf" in normalized || "container" in normalized ||
            "parser state" in normalized -> SessionOpenFailureCode.SNAPSHOT_FORMAT
        stage == SessionFailureStage.SNAPSHOT_METADATA -> SessionOpenFailureCode.SNAPSHOT_METADATA
        stage == SessionFailureStage.SNAPSHOT_DOWNLOAD -> SessionOpenFailureCode.SNAPSHOT_DOWNLOAD
        stage == SessionFailureStage.SNAPSHOT_PREPARE -> SessionOpenFailureCode.SNAPSHOT_DECRYPTION
        stage == SessionFailureStage.SNAPSHOT_CHECKPOINT -> SessionOpenFailureCode.LOCAL_CHECKPOINT
        stage == SessionFailureStage.SNAPSHOT_INSTALL -> SessionOpenFailureCode.SNAPSHOT_INSTALL
        stage == SessionFailureStage.HISTORY_METADATA -> SessionOpenFailureCode.HISTORY_NOT_READY
        stage == SessionFailureStage.HISTORY_DOWNLOAD || stage == SessionFailureStage.HISTORY_REPLAY ->
            SessionOpenFailureCode.HISTORY_REPLAY
        stage == SessionFailureStage.REALTIME -> SessionOpenFailureCode.REALTIME_CONNECTION
        stage == SessionFailureStage.CONTROL -> SessionOpenFailureCode.SERVER_REJECTED
        else -> SessionOpenFailureCode.UNKNOWN
    }
    return sessionOpenException(code, error)
}

internal fun SessionOpenFailure.localizedMessage(context: Context): String {
    val detail = context.getString(code.detailResource)
    val action = context.getString(code.actionResource)
    val status = httpStatus?.let { context.getString(R.string.terminal_error_http_status, it) }.orEmpty()
    val diagnostic = diagnosticCode
        ?.let { context.getString(R.string.terminal_error_diagnostic_code, it) }
        .orEmpty()
    return context.getString(R.string.terminal_error_message, code.stableCode, detail, status + diagnostic, action)
}

private inline fun <reified T : Throwable> Throwable.causedBy(): Boolean {
    var current: Throwable? = cause
    repeat(8) {
        if (current == null) return false
        if (current is T) return true
        current = current?.cause
    }
    return false
}

private inline fun <reified T : Throwable> Throwable.findCause(): T? {
    var current: Throwable? = this
    repeat(8) {
        if (current is T) return current
        current = current?.cause
    }
    return null
}

private fun Throwable.chainClassNameContains(value: String): Boolean {
    var current: Throwable? = this
    repeat(8) {
        if (current == null) return false
        if (current!!::class.java.simpleName.contains(value, ignoreCase = true)) return true
        current = current?.cause
    }
    return false
}
