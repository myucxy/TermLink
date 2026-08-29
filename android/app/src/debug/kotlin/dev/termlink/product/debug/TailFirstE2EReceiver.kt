package dev.termlink.product.debug

import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteException
import android.util.AtomicFile
import dev.termlink.product.TailFirstE2EProductBridge
import java.io.File
import java.io.FileOutputStream
import java.net.URI
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.putJsonObject
import kotlinx.serialization.json.put

/** Debug-only entry point used by the real tail-first acceptance runner. */
class TailFirstE2EReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (!intent.isExpectedExplicitRequest(context)) return

        val requestId = intent.getStringExtra(EXTRA_REQUEST_ID)
        if (requestId == null || !REQUEST_ID.matches(requestId)) return
        val request = parseRequest(intent, requestId)
        val operation = safeOperation(intent.getStringExtra(EXTRA_OPERATION))
        val applicationContext = context.applicationContext

        // The runner keeps ProductActivity in the foreground while it polls the
        // atomic response file. Do not retain a BroadcastReceiver PendingResult:
        // an arbitrary history restore can legitimately outlive Android's
        // broadcast deadline and would put an ANR window in front of the terminal.
        receiverScope.launch(start = CoroutineStart.UNDISPATCHED) {
            operationMutex.withLock {
                val response = if (request == null) {
                    failureResponse(
                        requestId = requestId,
                        operation = operation,
                        failureCode = FAILURE_REQUEST_INVALID,
                    )
                } else {
                    runCatching { executeAgainstProduct(applicationContext, request) }
                        .getOrElse {
                            failureResponse(
                                requestId = request.requestId,
                                operation = request.operation,
                                failureCode = refinedFailureCode(request, FAILURE_OPERATION_FAILED),
                            )
                        }
                }
                runCatching { writeResponseAtomically(applicationContext, response) }
            }
        }
    }

    private fun Intent.isExpectedExplicitRequest(context: Context): Boolean {
        if (action != ACTION) return false
        val expected = ComponentName(context.packageName, TailFirstE2EReceiver::class.java.name)
        return component == expected
    }

    private fun parseRequest(intent: Intent, requestId: String): TailFirstE2ERequest? {
        val operation = intent.getStringExtra(EXTRA_OPERATION)
            ?.takeIf(SUPPORTED_OPERATIONS::contains)
            ?: return null
        val runId = intent.getStringExtra(EXTRA_RUN_ID)
            ?.takeIf(RUN_ID::matches)
            ?: return null
        val allowedKeys = COMMON_EXTRAS + OPERATION_EXTRAS.getValue(operation)
        val extras = intent.extras ?: return null
        if (extras.keySet() != allowedKeys || allowedKeys.any { extras.getString(it) == null }) return null

        val arguments = OPERATION_EXTRAS.getValue(operation).associateWith { extras.getString(it).orEmpty() }
        if (!argumentsAreValid(operation, runId, arguments)) return null
        return TailFirstE2ERequest(
            operation = operation,
            requestId = requestId,
            runId = runId,
            arguments = arguments,
        )
    }

    private fun argumentsAreValid(
        operation: String,
        runId: String,
        arguments: Map<String, String>,
    ): Boolean {
        val markerStem = "TLTF_${runId.replace('-', '_').replace('.', '_')}"
        return when (operation) {
            OPERATION_PROBE, OPERATION_SHOW_IME -> arguments.isEmpty()
            OPERATION_OPEN_SESSION -> {
                val markerCount = arguments[EXTRA_MARKER_COUNT]?.toIntOrNull()
                val minimumTail = arguments[EXTRA_MINIMUM_TAIL]?.toULongOrNull()
                markerCount in ALLOWED_MARKER_COUNTS &&
                    arguments[EXTRA_FIRST_MARKER] == "${markerStem}_0001" &&
                    arguments[EXTRA_LAST_MARKER] == "%s_%04d".format(markerStem, markerCount) &&
                    minimumTail == if (markerCount == STREAMED_PREFIX_MARKER_COUNT) {
                        STREAMED_PREFIX_MINIMUM_TAIL
                    } else {
                        0uL
                    }
            }
            OPERATION_PAGE_UP ->
                arguments[EXTRA_FIRST_MARKER] == "${markerStem}_0001" &&
                    arguments[EXTRA_LAST_MARKER] == "${markerStem}_1000" &&
                    arguments[EXTRA_PAGE_SIZE]?.toIntOrNull()?.let { it in 1..MAX_PAGE_SIZE } == true &&
                    arguments[EXTRA_PAGE_NUMBER]?.toIntOrNull()?.let { it in 1..MAX_PAGE_NUMBER } == true &&
                    arguments[EXTRA_EXPECTED_RESULT] in EXPECTED_PAGE_RESULTS
            OPERATION_OPEN_HISTORY ->
                arguments[EXTRA_FIRST_MARKER] == "${markerStem}_0001" &&
                    arguments[EXTRA_LAST_MARKER] == "${markerStem}_1000"
            OPERATION_INSPECT, OPERATION_CLOSE_HISTORY ->
                arguments[EXTRA_LIVE_MARKER] == "${markerStem}_LIVE_AFTER_OVERLAY"
            else -> false
        }
    }

    private suspend fun executeAgainstProduct(context: Context, request: TailFirstE2ERequest): JsonObject {
        val executeBridge: suspend () -> JsonObject = {
            TailFirstE2EProductBridge.execute(
                operation = request.operation,
                requestId = request.requestId,
                arguments = request.arguments,
            )
        }
        val productResponse = if (request.operation == OPERATION_OPEN_SESSION) {
            coroutineScope {
                // UNDISPATCHED reaches awaitNewTailFirstSession's first suspension before
                // control returns here. Its existing-session set is therefore fixed before
                // the runner is told that it may start SessionHost. An arbitrary
                // history length must not inherit the short-operation timeout;
                // runner/emulator cancellation still cancels this process.
                val pending = async(start = CoroutineStart.UNDISPATCHED) { executeBridge() }
                writeSessionArmAtomically(
                    context = context,
                    request = request,
                    baselineSessionIds = awaitBaselineSessionIds(context),
                )
                pending.await()
            }
        } else {
            withTimeout(BRIDGE_TIMEOUT_MILLIS) { executeBridge() }
        }
        return sanitizeProductResponse(request, productResponse)
    }

    private fun sanitizeProductResponse(
        request: TailFirstE2ERequest,
        productResponse: JsonObject,
    ): JsonObject {
        if (productResponse.boolean("ok") != true) {
            val productFailure = productResponse.safeFailureCode() ?: FAILURE_OPERATION_FAILED
            return failureResponse(
                request.requestId,
                request.operation,
                refinedFailureCode(request, productFailure),
            )
        }
        return buildJsonObject {
            putEnvelope(request)
            when (request.operation) {
                OPERATION_PROBE -> {
                    put("account_ready", productResponse.requiredBoolean("account_ready"))
                    put("server_origin", productResponse.safeServerOrigin())
                    put("capabilities", buildJsonArray {
                        SUPPORTED_OPERATIONS.forEach { add(JsonPrimitive(it)) }
                    })
                }
                OPERATION_OPEN_SESSION,
                OPERATION_INSPECT,
                OPERATION_CLOSE_HISTORY,
                -> putState(productResponse)
                OPERATION_OPEN_HISTORY -> {
                    putState(productResponse)
                    put("pages_moved", productResponse.nonNegativeInt("pages_moved"))
                    put("rows_loaded", productResponse.nonNegativeInt("rows_loaded"))
                    putTranscriptPage(productResponse)
                }
                OPERATION_PAGE_UP -> {
                    putState(productResponse)
                    put("pages_moved", productResponse.nonNegativeInt("pages_moved"))
                    put("rows_loaded", productResponse.nonNegativeInt("rows_loaded"))
                    putTranscriptPage(productResponse)
                }
                OPERATION_SHOW_IME -> {
                    putState(productResponse)
                    put("ime_visible", productResponse.requiredBoolean("ime_visible"))
                    putBounds("display_bounds", productResponse)
                    putBounds("terminal_bounds", productResponse)
                    putBounds("bottom_bar_bounds", productResponse)
                    putBounds("ime_bounds", productResponse)
                }
            }
        }
    }

    private fun kotlinx.serialization.json.JsonObjectBuilder.putEnvelope(request: TailFirstE2ERequest) {
        put("schema_version", SCHEMA_VERSION)
        put("request_id", request.requestId)
        put("operation", request.operation)
        put("ok", true)
    }

    private fun kotlinx.serialization.json.JsonObjectBuilder.putState(source: JsonObject) {
        put("session_id", source.safeSessionId())
        COUNT_FIELDS.forEach { put(it, source.nonNegativeInt(it)) }
        put("terminal_columns", source.positiveInt("terminal_columns"))
        put("terminal_rows", source.positiveInt("terminal_rows"))
        UNSIGNED_FIELDS.forEach { put(it, source.nonNegativeUnsignedJsonNumber(it)) }
        BOOLEAN_STATE_FIELDS.forEach { put(it, source.requiredBoolean(it)) }
        DIAGNOSTIC_NAME_FIELDS.forEach { put(it, source.safeDiagnosticName(it)) }
        DIAGNOSTIC_UNSIGNED_FIELDS.forEach { put(it, source.nonNegativeUnsignedJsonNumber(it)) }
        DIAGNOSTIC_BOOLEAN_FIELDS.forEach { put(it, source.requiredBoolean(it)) }
        put("viewport_signature", source.safeViewportSignature())
    }

    private fun kotlinx.serialization.json.JsonObjectBuilder.putBounds(name: String, source: JsonObject) {
        val bounds = source[name]?.jsonObject ?: error("Invalid bounds")
        val left = bounds.nonNegativeInt("left")
        val top = bounds.nonNegativeInt("top")
        val right = bounds.nonNegativeInt("right")
        val bottom = bounds.nonNegativeInt("bottom")
        check(right > left && bottom > top)
        putJsonObject(name) {
            put("left", left)
            put("top", top)
            put("right", right)
            put("bottom", bottom)
        }
    }

    private fun kotlinx.serialization.json.JsonObjectBuilder.putTranscriptPage(source: JsonObject) {
        val requestBefore = source.nonNegativeUnsignedJsonNumber("request_before_line_id")
        val first = source.nonNegativeUnsignedJsonNumber("loaded_first_line_id")
        val last = source.nonNegativeUnsignedJsonNumber("loaded_last_line_id")
        val hasMore = source.requiredBoolean("loaded_has_more_before")
        val requestValue = requestBefore.jsonPrimitive.content.toULong()
        val firstValue = first.jsonPrimitive.content.toULong()
        val lastValue = last.jsonPrimitive.content.toULong()
        check(firstValue > 0uL && lastValue >= firstValue && lastValue < requestValue)
        put("request_before_line_id", requestBefore)
        put("loaded_first_line_id", first)
        put("loaded_last_line_id", last)
        put("loaded_has_more_before", hasMore)
        put("next_before_line_id", if (hasMore) first else JsonPrimitive(0))
    }

    private fun JsonObject.boolean(name: String): Boolean? =
        get(name)?.jsonPrimitive?.booleanOrNull

    private fun JsonObject.requiredBoolean(name: String): Boolean =
        boolean(name) ?: error("Invalid boolean")

    private fun JsonObject.safeFailureCode(): String? =
        get("failure_code")?.jsonPrimitive?.takeIf { it.isString }?.content
            ?.takeIf(FAILURE_CODE::matches)

    private fun refinedFailureCode(request: TailFirstE2ERequest, productFailure: String): String =
        if (
            request.operation == OPERATION_PAGE_UP &&
            request.arguments[EXTRA_EXPECTED_RESULT] == EXPECTED_PAGE_RESULT_TRANSCRIPT_LOAD_FAILED &&
            productFailure == FAILURE_OPERATION_FAILED
        ) {
            // Production deliberately returns only a public OPERATION_FAILED at the
            // bridge boundary. The acceptance request is allowed to refine that code
            // only for the one page that the runner proves has a live cursor and then
            // requests with Android's network disabled.
            FAILURE_TRANSCRIPT_LOAD_FAILED
        } else {
            productFailure
        }

    private fun JsonObject.nonNegativeInt(name: String): Int =
        get(name)?.jsonPrimitive?.takeUnless { it.isString }?.intOrNull
            ?.takeIf { it >= 0 }
            ?: error("Invalid integer")

    private fun JsonObject.positiveInt(name: String): Int =
        nonNegativeInt(name).takeIf { it > 0 } ?: error("Invalid positive integer")

    private fun JsonObject.nonNegativeUnsignedJsonNumber(name: String): JsonElement {
        val value = get(name)?.jsonPrimitive?.takeUnless { it.isString } ?: error("Invalid unsigned integer")
        check(UNSIGNED_INTEGER.matches(value.content) && value.content.toULongOrNull() != null)
        return value
    }

    private fun JsonObject.safeSessionId(): String {
        val value = get("session_id")?.jsonPrimitive?.takeIf { it.isString }?.content
            ?: error("Invalid session id")
        check(runCatching { UUID.fromString(value) }.getOrNull()?.let { it != EMPTY_UUID } == true)
        return value
    }

    private fun JsonObject.safeViewportSignature(): String =
        get("viewport_signature")?.jsonPrimitive?.takeIf { it.isString }?.content
            ?.takeIf(VIEWPORT_SIGNATURE::matches)
            ?: error("Invalid viewport signature")

    private fun JsonObject.safeDiagnosticName(name: String): String =
        get(name)?.jsonPrimitive?.takeIf { it.isString }?.content
            ?.takeIf(DIAGNOSTIC_NAME::matches)
            ?: error("Invalid diagnostic state")

    private fun JsonObject.safeServerOrigin(): String {
        val value = get("server_origin")?.jsonPrimitive?.takeIf { it.isString }?.content
            ?: error("Invalid server origin")
        val uri = runCatching { URI(value) }.getOrNull() ?: error("Invalid server origin")
        check(
            uri.scheme.equals("https", ignoreCase = true) &&
                uri.rawUserInfo == null &&
                !uri.host.isNullOrBlank() &&
                (uri.rawPath.isNullOrEmpty() || uri.rawPath == "/") &&
                uri.rawQuery == null &&
                uri.rawFragment == null,
        )
        return value.trimEnd('/')
    }

    private fun safeOperation(value: String?): String =
        value?.takeIf(SUPPORTED_OPERATIONS::contains) ?: INVALID_OPERATION

    private fun failureResponse(
        requestId: String,
        operation: String,
        failureCode: String,
    ): JsonObject = buildJsonObject {
        put("schema_version", SCHEMA_VERSION)
        put("request_id", requestId)
        put("operation", operation)
        put("ok", false)
        put("failure_code", failureCode)
    }

    private fun writeResponseAtomically(context: Context, response: JsonObject) {
        val responseDirectory = File(context.noBackupFilesDir, RESPONSE_DIRECTORY)
        check(responseDirectory.isDirectory || responseDirectory.mkdirs())
        val atomicFile = AtomicFile(File(responseDirectory, RESPONSE_FILE_NAME))
        val bytes = (JSON.encodeToString(JsonObject.serializer(), response) + "\n").encodeToByteArray()
        var output: FileOutputStream? = null
        try {
            output = atomicFile.startWrite()
            output.write(bytes)
            atomicFile.finishWrite(output)
        } catch (failure: Throwable) {
            output?.let(atomicFile::failWrite)
            throw failure
        }
    }

    private fun writeSessionArmAtomically(
        context: Context,
        request: TailFirstE2ERequest,
        baselineSessionIds: List<String>,
    ) {
        val responseDirectory = File(context.noBackupFilesDir, RESPONSE_DIRECTORY)
        check(responseDirectory.isDirectory || responseDirectory.mkdirs())
        val atomicFile = AtomicFile(File(responseDirectory, SESSION_ARM_FILE_NAME))
        val response = buildJsonObject {
            put("schema_version", SCHEMA_VERSION)
            put("request_id", request.requestId)
            put("operation", request.operation)
            put("armed", true)
            put("armed_at_epoch_millis", System.currentTimeMillis())
            put("baseline_session_ids", buildJsonArray {
                baselineSessionIds.forEach { add(JsonPrimitive(it)) }
            })
        }
        val bytes = (JSON.encodeToString(JsonObject.serializer(), response) + "\n").encodeToByteArray()
        var output: FileOutputStream? = null
        try {
            output = atomicFile.startWrite()
            output.write(bytes)
            atomicFile.finishWrite(output)
        } catch (failure: Throwable) {
            output?.let(atomicFile::failWrite)
            throw failure
        }
    }

    /**
     * The runner deliberately removes Room's history database immediately
     * before a cold launch. The Activity can therefore be account-ready while
     * Room is still creating the database file and its sessions table. Keep
     * SessionHost fenced until that short schema-publication race settles.
     */
    private suspend fun awaitBaselineSessionIds(context: Context): List<String> =
        withTimeout(BASELINE_READINESS_TIMEOUT_MILLIS) {
            while (true) {
                val databaseFile = context.getDatabasePath(HISTORY_DATABASE_NAME)
                if (databaseFile.isFile) {
                    try {
                        return@withTimeout SQLiteDatabase.openDatabase(
                            databaseFile.absolutePath,
                            null,
                            SQLiteDatabase.OPEN_READONLY,
                        ).use { database ->
                            database.rawQuery("SELECT id FROM sessions ORDER BY id ASC", emptyArray()).use { cursor ->
                                buildList {
                                    while (cursor.moveToNext()) {
                                        val id = cursor.getString(0)
                                        check(
                                            runCatching { UUID.fromString(id) }.getOrNull()
                                                ?.let { it != EMPTY_UUID } == true,
                                        )
                                        add(id)
                                    }
                                }
                            }
                        }
                    } catch (error: SQLiteException) {
                        val message = error.message.orEmpty()
                        if (!message.contains("no such table: sessions") &&
                            !message.contains("database is locked")
                        ) throw error
                    }
                }
                delay(BASELINE_READINESS_POLL_MILLIS)
            }
            error("unreachable")
        }

    private data class TailFirstE2ERequest(
        val operation: String,
        val requestId: String,
        val runId: String,
        val arguments: Map<String, String>,
    )

    private companion object {
        const val ACTION = "dev.termlink.product.debug.TAIL_FIRST_E2E"
        const val SCHEMA_VERSION = 1
        const val RESPONSE_DIRECTORY = "tail-first-e2e"
        const val RESPONSE_FILE_NAME = "response.json"
        const val SESSION_ARM_FILE_NAME = "session-arm.json"
        const val HISTORY_DATABASE_NAME = "termlink-history.db"

        const val EXTRA_OPERATION = "e2e_operation"
        const val EXTRA_REQUEST_ID = "request_id"
        const val EXTRA_RUN_ID = "run_id"
        const val EXTRA_FIRST_MARKER = "first_marker"
        const val EXTRA_LAST_MARKER = "last_marker"
        const val EXTRA_LIVE_MARKER = "live_marker"
        const val EXTRA_MARKER_COUNT = "marker_count"
        const val EXTRA_MINIMUM_TAIL = "minimum_tail"
        const val EXTRA_PAGE_SIZE = "page_size"
        const val EXTRA_PAGE_NUMBER = "page_number"
        const val EXTRA_EXPECTED_RESULT = "expected_result"

        const val EXPECTED_PAGE_RESULT_SUCCESS = "success"
        const val EXPECTED_PAGE_RESULT_TRANSCRIPT_LOAD_FAILED = "transcript_load_failed"

        const val OPERATION_PROBE = "probe"
        const val OPERATION_OPEN_SESSION = "open_session"
        const val OPERATION_INSPECT = "inspect"
        const val OPERATION_PAGE_UP = "page_up"
        const val OPERATION_OPEN_HISTORY = "open_history"
        const val OPERATION_CLOSE_HISTORY = "close_history"
        const val OPERATION_SHOW_IME = "show_ime"
        const val INVALID_OPERATION = "invalid"

        const val FAILURE_REQUEST_INVALID = "REQUEST_INVALID"
        const val FAILURE_OPERATION_FAILED = "OPERATION_FAILED"
        const val FAILURE_TRANSCRIPT_LOAD_FAILED = "TRANSCRIPT_LOAD_FAILED"
        const val MAX_PAGE_SIZE = 200
        const val MAX_PAGE_NUMBER = 10
        const val BRIDGE_TIMEOUT_MILLIS = 180_000L
        const val BASELINE_READINESS_TIMEOUT_MILLIS = 10_000L
        const val BASELINE_READINESS_POLL_MILLIS = 50L
        const val SNAPSHOT_MARKER_COUNT = 1000
        const val STREAMED_PREFIX_MARKER_COUNT = 5200
        val STREAMED_PREFIX_MINIMUM_TAIL = 4097uL

        val REQUEST_ID = Regex("^[0-9a-f]{32}$")
        val RUN_ID = Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
        val UNSIGNED_INTEGER = Regex("^(0|[1-9][0-9]{0,19})$")
        val FAILURE_CODE = Regex("^[A-Z0-9_]{1,64}$")
        val VIEWPORT_SIGNATURE = Regex("^[A-Za-z0-9:_-]{1,128}$")
        val DIAGNOSTIC_NAME = Regex("^[A-Z_]{2,32}$")
        val EMPTY_UUID: UUID = UUID(0, 0)
        val EXPECTED_PAGE_RESULTS = setOf(
            EXPECTED_PAGE_RESULT_SUCCESS,
            EXPECTED_PAGE_RESULT_TRANSCRIPT_LOAD_FAILED,
        )
        val COUNT_FIELDS = listOf("snapshot_count", "transcript_count", "chunk_count")
        val UNSIGNED_FIELDS = listOf("snapshot_sequence_s", "live_ack")
        val BOOLEAN_STATE_FIELDS = listOf(
            "history_overlay",
            "first_marker_visible",
            "last_marker_visible",
            "live_marker_visible",
        )
        val DIAGNOSTIC_NAME_FIELDS = listOf(
            "bootstrap_source",
            "realtime_socket_phase",
            "realtime_socket_state",
            "realtime_transport_state",
            "realtime_remote_state",
        )
        val DIAGNOSTIC_UNSIGNED_FIELDS = listOf(
            "realtime_socket_generation",
            "realtime_connected_generation",
            "realtime_endpoint_count",
            "realtime_reconnect_attempt",
            "realtime_subscribe_send_count",
            "realtime_last_subscribe_cursor",
            "realtime_flow_control_send_count",
            "realtime_last_received_sequence",
            "realtime_last_handled_sequence",
            "realtime_network_available_count",
            "realtime_network_lost_count",
            "realtime_network_reconnect_count",
        )
        val DIAGNOSTIC_BOOLEAN_FIELDS = listOf(
            "realtime_transport_collector_active",
            "realtime_reconnect_active",
            "realtime_subscriber_ready",
        )
        val COMMON_EXTRAS = setOf(EXTRA_OPERATION, EXTRA_REQUEST_ID, EXTRA_RUN_ID)
        val SUPPORTED_OPERATIONS = linkedSetOf(
            OPERATION_PROBE,
            OPERATION_OPEN_SESSION,
            OPERATION_INSPECT,
            OPERATION_PAGE_UP,
            OPERATION_OPEN_HISTORY,
            OPERATION_CLOSE_HISTORY,
            OPERATION_SHOW_IME,
        )
        val OPERATION_EXTRAS = mapOf(
            OPERATION_PROBE to emptySet(),
            OPERATION_OPEN_SESSION to setOf(
                EXTRA_FIRST_MARKER,
                EXTRA_LAST_MARKER,
                EXTRA_MARKER_COUNT,
                EXTRA_MINIMUM_TAIL,
            ),
            OPERATION_INSPECT to setOf(EXTRA_LIVE_MARKER),
            OPERATION_PAGE_UP to setOf(
                EXTRA_FIRST_MARKER,
                EXTRA_LAST_MARKER,
                EXTRA_PAGE_SIZE,
                EXTRA_PAGE_NUMBER,
                EXTRA_EXPECTED_RESULT,
            ),
            OPERATION_OPEN_HISTORY to setOf(EXTRA_FIRST_MARKER, EXTRA_LAST_MARKER),
            OPERATION_CLOSE_HISTORY to setOf(EXTRA_LIVE_MARKER),
            OPERATION_SHOW_IME to emptySet(),
        )
        val ALLOWED_MARKER_COUNTS = setOf(SNAPSHOT_MARKER_COUNT, STREAMED_PREFIX_MARKER_COUNT)
        val JSON = Json { explicitNulls = false }
        val receiverScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val operationMutex = Mutex()
    }
}
