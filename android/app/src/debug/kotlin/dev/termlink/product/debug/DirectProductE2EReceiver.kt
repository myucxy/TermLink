package dev.termlink.product.debug

import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.AtomicFile
import android.util.Log
import dev.termlink.harness.BuildConfig
import dev.termlink.product.DirectProductE2EController
import dev.termlink.product.DirectProductE2EOperationResult
import dev.termlink.product.ProductActivity
import dev.termlink.product.ProductionDirectProductE2EBridgeRegistry
import java.io.File
import java.io.FileOutputStream
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/** Isolated Debug acceptance boundary for the real Direct product matrix. */
class DirectProductE2EReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION ||
            intent.component != ComponentName(context.packageName, DirectProductE2EReceiver::class.java.name)
        ) return

        val requestId = intent.getStringExtra(EXTRA_REQUEST_ID)?.takeIf(REQUEST_ID::matches) ?: return
        val runId = intent.getStringExtra(EXTRA_RUN_ID)?.takeIf(RUN_ID::matches) ?: return
        val operation = intent.getStringExtra(EXTRA_OPERATION)?.takeIf(SUPPORTED_OPERATIONS::contains) ?: return
        val extras = intent.extras ?: return
        val requiredKeys = setOf(EXTRA_REQUEST_ID, EXTRA_RUN_ID, EXTRA_OPERATION) +
            if (operation == OPERATION_PROBE) emptySet() else setOf(EXTRA_ARGUMENTS_JSON)
        if (extras.keySet() != requiredKeys) return
        val arguments = if (operation == OPERATION_PROBE) {
            JsonObject(emptyMap())
        } else {
            intent.getStringExtra(EXTRA_ARGUMENTS_JSON)
                ?.let(DirectProductE2EProtocol::parseArguments)
                ?: return
        }
        val runIdSha256 = DirectProductE2EProtocol.sha256(runId)
        val pendingResult = goAsync()
        val requestScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        requestScope.launch(start = CoroutineStart.UNDISPATCHED) {
            try {
                val response = try {
                    withTimeoutOrNull(MAX_OPERATION_MILLIS) {
                        val bridge = resolveBridge(context.applicationContext)
                        if (operation == OPERATION_PROBE) {
                            probeResponse(requestId, runIdSha256, bridge != null)
                        } else if (bridge == null) {
                            DirectProductE2EProtocol.operationResponse(
                                requestId,
                                runIdSha256,
                                operation,
                                ready = false,
                                result = DirectProductE2EOperationResult.blocked(FAILURE_BRIDGE_UNAVAILABLE),
                            )
                        } else {
                            DirectProductE2EProtocol.operationResponse(
                                requestId,
                                runIdSha256,
                                operation,
                                ready = true,
                                result = bridge.execute(runIdSha256, operation, arguments),
                            )
                        }
                    } ?: DirectProductE2EProtocol.operationResponse(
                        requestId,
                        runIdSha256,
                        operation,
                        ready = bridgeReady(),
                        result = DirectProductE2EOperationResult.blocked(FAILURE_OPERATION_TIMEOUT),
                    )
                } catch (_: Throwable) {
                    DirectProductE2EProtocol.operationResponse(
                        requestId,
                        runIdSha256,
                        operation,
                        ready = bridgeReady(),
                        result = DirectProductE2EOperationResult.blocked(FAILURE_OPERATION_FAILED),
                    )
                }
                writeResponseFailClosed(
                    context = context.applicationContext,
                    requestId = requestId,
                    runIdSha256 = runIdSha256,
                    operation = operation,
                    response = response,
                )
            } finally {
                pendingResult.finish()
                requestScope.cancel()
            }
        }
    }

    private fun bridgeReady(): Boolean =
        runCatching { ProductionDirectProductE2EBridgeRegistry.current() != null }.getOrDefault(false)

    private fun writeResponseFailClosed(
        context: Context,
        requestId: String,
        runIdSha256: String,
        operation: String,
        response: JsonObject,
    ) {
        try {
            DirectProductE2EProtocol.writeResponse(context, response)
        } catch (_: Throwable) {
            val fallback = DirectProductE2EProtocol.operationResponse(
                requestId = requestId,
                runIdSha256 = runIdSha256,
                operation = operation,
                ready = false,
                result = DirectProductE2EOperationResult.blocked(FAILURE_RESPONSE_WRITE_FAILED),
            )
            runCatching { DirectProductE2EProtocol.writeResponse(context, fallback) }
                .onFailure {
                    Log.e(LOG_TAG, "response_write_failed failure_code=$FAILURE_RESPONSE_WRITE_FAILED")
                }
        }
    }

    private suspend fun resolveBridge(context: Context): DirectProductE2EController? {
        if (!BuildConfig.TERMLINK_DIRECT_TRANSPORT_SHADOW ||
            !BuildConfig.TERMLINK_DIRECT_TRANSPORT_DATA_PLANE
        ) return null
        ProductionDirectProductE2EBridgeRegistry.current()?.let { return it }
        val launched = runCatching {
            context.startActivity(
                Intent(context, ProductActivity::class.java).addFlags(
                    Intent.FLAG_ACTIVITY_NEW_TASK or
                        Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or
                        Intent.FLAG_ACTIVITY_SINGLE_TOP,
                ),
            )
        }.isSuccess
        if (!launched) return null
        repeat(BRIDGE_OBSERVATION_ATTEMPTS) {
            ProductionDirectProductE2EBridgeRegistry.current()?.let { return it }
            delay(BRIDGE_OBSERVATION_POLL_MILLIS)
        }
        return null
    }

    private fun probeResponse(
        requestId: String,
        runIdSha256: String,
        ready: Boolean,
    ): JsonObject = buildJsonObject {
        put("schema_version", 1)
        put("request_id", requestId)
        put("operation", OPERATION_PROBE)
        put("ok", ready)
        put("ready", ready)
        put("run_id_sha256", runIdSha256)
        put("failure_code", if (ready) "" else FAILURE_BRIDGE_UNAVAILABLE)
        DirectProductE2EProtocol.putFailureMessages(this, if (ready) null else FAILURE_BRIDGE_UNAVAILABLE)
        put("shadow_acceptance", BuildConfig.TERMLINK_DIRECT_TRANSPORT_SHADOW)
        put("data_plane_acceptance", BuildConfig.TERMLINK_DIRECT_TRANSPORT_DATA_PLANE)
        put("diagnostics", JsonArray(DIAGNOSTICS.map(::JsonPrimitive)))
        put("operations", JsonArray(SUPPORTED_OPERATIONS.sorted().map(::JsonPrimitive)))
    }

    private companion object {
        const val ACTION = "dev.termlink.product.debug.DIRECT_PRODUCT_E2E"
        const val EXTRA_OPERATION = "operation"
        const val EXTRA_REQUEST_ID = "request_id"
        const val EXTRA_RUN_ID = "run_id"
        const val EXTRA_ARGUMENTS_JSON = "arguments_json"
        const val OPERATION_PROBE = "probe"
        const val FAILURE_BRIDGE_UNAVAILABLE = "PRODUCT_DIRECT_BRIDGE_UNAVAILABLE"
        const val FAILURE_OPERATION_TIMEOUT = "PRODUCT_OPERATION_TIMEOUT"
        const val FAILURE_OPERATION_FAILED = "PRODUCT_OPERATION_FAILED"
        const val FAILURE_RESPONSE_WRITE_FAILED = "PRODUCT_RESPONSE_WRITE_FAILED"
        const val LOG_TAG = "TermLinkDirectE2E"
        const val MAX_OPERATION_MILLIS = 9_000L
        const val BRIDGE_OBSERVATION_ATTEMPTS = 30
        const val BRIDGE_OBSERVATION_POLL_MILLIS = 100L
        val REQUEST_ID = Regex("^[a-f0-9]{32}$")
        val RUN_ID = Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
        val SUPPORTED_OPERATIONS = setOf(
            OPERATION_PROBE,
            "inspect_transport",
            "send_text_marker",
            "send_key",
            "request_resize",
            "background",
            "foreground",
            "lock_screen",
            "wake_screen",
            "open_late_session",
            "start_long_output",
            "stop_long_output",
            "prepare_session",
            "close_test_sessions",
        )
        val DIAGNOSTICS = listOf(
            "app_process_id",
            "app_process_start_time",
            "candidate_kinds",
            "carrier",
            "contiguous_applied_sequence",
            "digest_conflict_count",
            "duplicate_apply_count",
            "duplicate_drop_count",
            "durable_ack",
            "fallback_reason",
            "gap_count",
            "last_received_sequence",
            "marker_digest",
            "network_generation",
            "path_epoch",
            "relay_generation",
            "signalling_generation",
            "transport_receipt_high_watermark",
            "viewport_digest",
        )
    }
}

internal object DirectProductE2EProtocol {
    private const val MAX_ARGUMENT_BYTES = 4096
    private const val MAX_RESPONSE_BYTES = 1024 * 1024
    private const val RESPONSE_RELATIVE_PATH = "direct-product-e2e/response.json"
    private val SENSITIVE_FIELD =
        Regex("token|password|secret|authorization|cookie|credential|private_key|key_material", RegexOption.IGNORE_CASE)

    fun sha256(value: String): String {
        val bytes = value.encodeToByteArray()
        return try {
            java.security.MessageDigest.getInstance("SHA-256")
                .digest(bytes)
                .joinToString("") { "%02x".format(it) }
        } finally {
            bytes.fill(0)
        }
    }

    fun parseArguments(value: String): JsonObject? {
        val bytes = value.encodeToByteArray()
        return try {
            if (bytes.size > MAX_ARGUMENT_BYTES) return null
            val document = runCatching { kotlinx.serialization.json.Json.parseToJsonElement(value) as? JsonObject }
                .getOrNull() ?: return null
            document.takeIf(::isSafeFlatObject)
        } finally {
            bytes.fill(0)
        }
    }

    private fun isSafeFlatObject(document: JsonObject): Boolean = document.all { (name, value) ->
        !name.contains(SENSITIVE_FIELD) && value is JsonPrimitive && value !is JsonNull
    }

    fun operationResponse(
        requestId: String,
        runIdSha256: String,
        operation: String,
        ready: Boolean,
        result: DirectProductE2EOperationResult,
    ): JsonObject = buildJsonObject {
        put("schema_version", 1)
        put("request_id", requestId)
        put("operation", operation)
        put("ok", result.ok)
        put("ready", ready)
        put("run_id_sha256", runIdSha256)
        put("failure_code", result.failureCode.orEmpty())
        putFailureMessages(this, result.failureCode)
        result.payload.forEach { (name, value) -> put(name, value) }
    }

    fun putFailureMessages(
        builder: kotlinx.serialization.json.JsonObjectBuilder,
        failureCode: String?,
    ) {
        val messages = when (failureCode) {
            null -> "" to ""
            "PRODUCT_DIRECT_BRIDGE_UNAVAILABLE" ->
                "产品验收连接尚未就绪，请重试。" to "The product acceptance bridge is not ready. Try again."
            "PRODUCT_OPERATION_TIMEOUT" ->
                "操作超时，请重试。" to "The operation timed out. Try again."
            "PRODUCT_OPERATION_FAILED" ->
                "操作没有完成，请重试。" to "The operation did not complete. Try again."
            else ->
                "当前无法执行此操作，请稍后重试。" to "The operation is currently unavailable. Try again later."
        }
        builder.put("message_zh", messages.first)
        builder.put("message_en", messages.second)
    }

    fun writeResponse(context: Context, response: JsonObject) {
        val bytes = response.toString().encodeToByteArray()
        require(bytes.size <= MAX_RESPONSE_BYTES)
        val target = File(context.noBackupFilesDir, RESPONSE_RELATIVE_PATH)
        val parent = requireNotNull(target.parentFile)
        require(parent.isDirectory || parent.mkdirs())
        val atomic = AtomicFile(target)
        var stream: FileOutputStream? = null
        try {
            stream = atomic.startWrite()
            stream.write(bytes)
            atomic.finishWrite(stream)
            stream = null
        } catch (error: Throwable) {
            stream?.let(atomic::failWrite)
            throw error
        } finally {
            bytes.fill(0)
        }
    }
}
