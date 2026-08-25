package dev.termlink.product

import android.content.Context
import androidx.annotation.StringRes
import dev.termlink.harness.R
import dev.termlink.network.production.TermLinkApiException
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
    REALTIME_CONNECTION("TL-R001", true, R.string.terminal_error_realtime_connection, R.string.terminal_error_action_check_network),
    CONTROL_LEASE_HELD("TL-C001", true, R.string.terminal_error_control_lease, R.string.terminal_error_action_wait_lease),
    UNKNOWN("TL-X001", true, R.string.terminal_error_unknown, R.string.terminal_error_action_retry),
}

data class SessionOpenFailure(
    val code: SessionOpenFailureCode,
    /** Safe transport metadata only. Never store a URL, token, key ID, or terminal text here. */
    val httpStatus: Int? = null,
)

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
    REALTIME,
    CONTROL,
}

internal fun sessionOpenException(
    code: SessionOpenFailureCode,
    cause: Throwable? = null,
    httpStatus: Int? = null,
): SessionOpenException = SessionOpenException(SessionOpenFailure(code, httpStatus), cause)

/** Converts implementation exceptions to a bounded public taxonomy without echoing exception text. */
internal fun classifySessionOpenException(
    error: Throwable,
    stage: SessionFailureStage,
): SessionOpenException {
    if (error is SessionOpenException) return error
    if (error is TermLinkApiException) {
        val code = when {
            error.status == 401 -> SessionOpenFailureCode.AUTHENTICATION_EXPIRED
            error.status == 403 -> SessionOpenFailureCode.ACCESS_DENIED
            error.status == 409 && error.code == "LEASE_HELD" -> SessionOpenFailureCode.CONTROL_LEASE_HELD
            else -> when (stage) {
                SessionFailureStage.SNAPSHOT_METADATA -> SessionOpenFailureCode.SNAPSHOT_METADATA
                SessionFailureStage.SNAPSHOT_DOWNLOAD -> SessionOpenFailureCode.SNAPSHOT_DOWNLOAD
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
    return context.getString(R.string.terminal_error_message, code.stableCode, detail, status, action)
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

private fun Throwable.chainClassNameContains(value: String): Boolean {
    var current: Throwable? = this
    repeat(8) {
        if (current == null) return false
        if (current!!::class.java.simpleName.contains(value, ignoreCase = true)) return true
        current = current?.cause
    }
    return false
}
