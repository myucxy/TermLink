package dev.termlink.product.debug


import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ClipboardManager
import android.app.Notification
import android.app.NotificationManager
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteException
import android.util.AtomicFile
import dev.termlink.harness.R
import dev.termlink.product.TailFirstE2EProductBridge
import java.io.File
import java.io.FileOutputStream
import java.net.URI
import java.security.MessageDigest
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

internal fun tailFirstLiveMarker(runId: String): String {
    val digest = MessageDigest.getInstance("SHA-256").digest(runId.encodeToByteArray())
    return buildString(LIVE_MARKER_PREFIX.length + LIVE_MARKER_DIGEST_HEX_LENGTH) {
        append(LIVE_MARKER_PREFIX)
        repeat(LIVE_MARKER_DIGEST_BYTES) { index ->
            val value = digest[index].toInt() and 0xff
            append(LOWERCASE_HEX[value ushr 4])
            append(LOWERCASE_HEX[value and 0x0f])
        }
    }
}

internal fun tailFirstInspectMarkerIsValid(runId: String, marker: String?): Boolean {
    val live = tailFirstLiveMarker(runId)
    val digest = live.removePrefix(LIVE_MARKER_PREFIX)
    return marker == live || marker == "TLF_COL32_${digest.take(8)}" ||
        marker == "TLTF_FROZEN_$digest"
}

internal fun frozenLifecycleMarkerStem(runId: String): String {
    val digest = MessageDigest.getInstance("SHA-256").digest(runId.encodeToByteArray())
    return buildString(15) {
        append("TLTF32_")
        repeat(4) { index ->
            val value = digest[index].toInt() and 0xff
            append(LOWERCASE_HEX[value ushr 4])
            append(LOWERCASE_HEX[value and 0x0f])
        }
    }
}

private const val LIVE_MARKER_PREFIX = "TLTF_LIVE_"
private const val LIVE_MARKER_DIGEST_BYTES = 8
private const val LIVE_MARKER_DIGEST_HEX_LENGTH = LIVE_MARKER_DIGEST_BYTES * 2
private const val LOWERCASE_HEX = "0123456789abcdef"

internal fun armedSessionId(arm: JsonObject, runId: String, nowMillis: Long): UUID? {
    if ((arm["schema_version"] as? JsonPrimitive)?.intOrNull != 1 ||
        (arm["operation"] as? JsonPrimitive)?.content != "open_session" ||
        (arm["run_id"] as? JsonPrimitive)?.content != runId ||
        (arm["armed"] as? JsonPrimitive)?.takeUnless { it.isString }?.booleanOrNull != true) return null
    val armedAt = (arm["armed_at_epoch_millis"] as? JsonPrimitive)?.content?.toLongOrNull() ?: return null
    if (armedAt <= 0 || nowMillis < armedAt || nowMillis - armedAt > 30 * 60_000L) return null
    val rawId = (arm["session_id"] as? JsonPrimitive)?.content ?: return null
    val sessionId = runCatching { UUID.fromString(rawId) }.getOrNull() ?: return null
    if (sessionId == UUID(0, 0) || sessionId.toString() != rawId) return null
    val baseline = arm["baseline_session_ids"] as? kotlinx.serialization.json.JsonArray ?: return null
    if (baseline.any { it !is JsonPrimitive } || baseline.any { it.jsonPrimitive.content == rawId }) return null
    return sessionId
}

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
        val actualKeys = extras.keySet()
        if (actualKeys != allowedKeys &&
            !(operation == OPERATION_INSPECT && (actualKeys == allowedKeys + EXTRA_CLIPBOARD_FIXTURE ||
                actualKeys == allowedKeys + EXTRA_NOTIFICATION_FIXTURE)) &&
            !(operation == OPERATION_OPEN_SESSION &&
                (actualKeys == allowedKeys + EXTRA_FROZEN_LIFECYCLE_ACCEPTANCE ||
                    actualKeys == allowedKeys + EXTRA_LIVE_MARKER ||
                    actualKeys == allowedKeys + EXTRA_FROZEN_LIFECYCLE_ACCEPTANCE + EXTRA_LIVE_MARKER))) return null
        if (actualKeys.any { extras.getString(it) == null }) return null

        val arguments = (actualKeys - COMMON_EXTRAS).associateWith { extras.getString(it).orEmpty() }
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
            OPERATION_PROBE, OPERATION_CLOSE_VIEW, OPERATION_REOPEN_SELECTED,
            OPERATION_SHOW_IME, OPERATION_RESELECT_ARMED_SESSION, OPERATION_BEGIN_FROZEN_HISTORY,
            OPERATION_PAGE_FROZEN_HISTORY, OPERATION_RELEASE_FROZEN_HISTORY -> arguments.isEmpty()
            OPERATION_OPEN_SESSION -> {
                if (EXTRA_FROZEN_LIFECYCLE_ACCEPTANCE in arguments) {
                    return frozenLifecycleOpenSessionArgumentsAreValid(runId, arguments)
                }
                openSessionArgumentsAreValid(runId, arguments)
            }
            OPERATION_PAGE_UP ->
                arguments[EXTRA_FIRST_MARKER] == "${markerStem}_0001" &&
                    arguments[EXTRA_LAST_MARKER] == "${markerStem}_5200" &&
                    arguments[EXTRA_PAGE_SIZE]?.toIntOrNull()?.let { it in 1..MAX_PAGE_SIZE } == true &&
                    arguments[EXTRA_PAGE_NUMBER]?.toIntOrNull()?.let { it in 1..MAX_PAGE_NUMBER } == true &&
                    arguments[EXTRA_EXPECTED_RESULT] in EXPECTED_PAGE_RESULTS
            OPERATION_OPEN_HISTORY ->
                arguments[EXTRA_FIRST_MARKER] == "${markerStem}_0001" &&
                    arguments[EXTRA_LAST_MARKER] == "${markerStem}_5200"
            OPERATION_INSPECT ->
                tailFirstInspectMarkerIsValid(runId, arguments[EXTRA_LIVE_MARKER]) &&
                    (arguments[EXTRA_CLIPBOARD_FIXTURE] == null ||
                        arguments[EXTRA_CLIPBOARD_FIXTURE] in CLIPBOARD_FIXTURES) &&
                    (arguments[EXTRA_NOTIFICATION_FIXTURE] == null ||
                        arguments[EXTRA_NOTIFICATION_FIXTURE] == "attention")
            OPERATION_CLOSE_HISTORY -> arguments[EXTRA_LIVE_MARKER] == tailFirstLiveMarker(runId)
            else -> false
        }
    }

    internal fun frozenLifecycleOpenSessionArgumentsAreValid(
        runId: String,
        arguments: Map<String, String>,
    ): Boolean {
        if (!RUN_ID.matches(runId) ||
            (arguments.keys != FROZEN_LIFECYCLE_OPEN_SESSION_KEYS &&
             arguments.keys != FROZEN_LIFECYCLE_OPEN_SESSION_KEYS + EXTRA_LIVE_MARKER)) return false
        val markerStem = frozenLifecycleMarkerStem(runId)
        if (arguments[EXTRA_LIVE_MARKER] != null &&
            arguments[EXTRA_LIVE_MARKER] != tailFirstLiveMarker(runId)) return false
        return arguments[EXTRA_FROZEN_LIFECYCLE_ACCEPTANCE] == FROZEN_LIFECYCLE_ACCEPTANCE_ENABLED &&
            arguments[EXTRA_FIRST_MARKER] == "${markerStem}_0001" &&
            arguments[EXTRA_LAST_MARKER] == "${markerStem}_1400" &&
            arguments[EXTRA_MARKER_COUNT] == FROZEN_LIFECYCLE_MARKER_COUNT.toString() &&
            arguments[EXTRA_MINIMUM_TAIL] == "0" &&
            arguments[EXTRA_HISTORY_BOOTSTRAP_MODE] == HISTORY_BOOTSTRAP_SNAPSHOT_TRANSCRIPT
    }

    /**
     * Standard open_session accepts an optional final live marker.  Keeping
     * it optional preserves existing hooks; when present it is still bound to
     * the run-derived marker and unknown keys remain rejected by parseRequest.
     */
    internal fun openSessionArgumentsAreValid(
        runId: String,
        arguments: Map<String, String>,
    ): Boolean {
        val markerStem = "TLTF_${runId.replace('-', '_').replace('.', '_')}"
        val required = setOf(
            EXTRA_FIRST_MARKER,
            EXTRA_LAST_MARKER,
            EXTRA_MARKER_COUNT,
            EXTRA_MINIMUM_TAIL,
            EXTRA_HISTORY_BOOTSTRAP_MODE,
        )
        if (arguments.keys != required && arguments.keys != required + EXTRA_LIVE_MARKER) return false
        val markerCount = arguments[EXTRA_MARKER_COUNT]?.toIntOrNull()
        val minimumTail = arguments[EXTRA_MINIMUM_TAIL]?.toULongOrNull()
        val bootstrapMode = arguments[EXTRA_HISTORY_BOOTSTRAP_MODE]
        return markerCount in ALLOWED_MARKER_COUNTS &&
            arguments[EXTRA_FIRST_MARKER] == "${markerStem}_0001" &&
            arguments[EXTRA_LAST_MARKER] == "%s_%04d".format(markerStem, markerCount) &&
            bootstrapMode in HISTORY_BOOTSTRAP_MODES &&
            minimumTail == if (bootstrapMode == HISTORY_BOOTSTRAP_STREAMED_PREFIX) {
                STREAMED_PREFIX_MINIMUM_TAIL
            } else {
                0uL
            } &&
            (arguments[EXTRA_LIVE_MARKER] == null ||
                arguments[EXTRA_LIVE_MARKER] == tailFirstLiveMarker(runId))
    }

    private suspend fun executeAgainstProduct(context: Context, request: TailFirstE2ERequest): JsonObject {
        val bridgeArguments = if (request.operation == OPERATION_RESELECT_ARMED_SESSION) {
            val arm = readSessionArm(context)
            val sessionId = arm?.let { armedSessionId(it, request.runId, System.currentTimeMillis()) }
                ?: return failureResponse(request.requestId, request.operation, "SESSION_ARM_UNAVAILABLE")
            mapOf("session_id" to sessionId.toString())
        } else request.arguments
        val executeBridge: suspend () -> JsonObject = {
            TailFirstE2EProductBridge.execute(
                operation = request.operation,
                requestId = request.requestId,
                arguments = bridgeArguments,
            )
        }
        var openBaseline: List<String>? = null
        val productResponse = if (request.operation == OPERATION_OPEN_SESSION) {
            coroutineScope {
                // UNDISPATCHED reaches awaitNewTailFirstSession's first suspension before
                // control returns here. Its existing-session set is therefore fixed before
                // the runner is told that it may start SessionHost. An arbitrary
                // history length must not inherit the short-operation timeout;
                // runner/emulator cancellation still cancels this process.
                val pending = async(start = CoroutineStart.UNDISPATCHED) { executeBridge() }
                val baseline = awaitBaselineSessionIds(context)
                openBaseline = baseline
                writeSessionArmAtomically(
                    context = context,
                    request = request,
                    baselineSessionIds = baseline,
                )
                pending.await()
            }
        } else {
            withTimeout(BRIDGE_TIMEOUT_MILLIS) { executeBridge() }
        }
        val sanitized = sanitizeProductResponse(context, request, productResponse)
        if (request.operation == OPERATION_OPEN_SESSION && sanitized.boolean("ok") == true) {
            val sessionId = sanitized.safeSessionId()
            check(sessionId !in openBaseline.orEmpty()) { "Test session overlaps baseline" }
            writeSessionArmAtomically(context, request, openBaseline.orEmpty(), sessionId)
        }
        return sanitized
    }

    private fun sanitizeProductResponse(
        context: Context,
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
                    if (productResponse.requiredBoolean("account_ready")) {
                        sanitizeRecipientIdentity(productResponse).forEach { (key, value) -> put(key, value) }
                    }
                    put("capabilities", buildJsonArray {
                        SUPPORTED_OPERATIONS.forEach { add(JsonPrimitive(it)) }
                    })
                }
                OPERATION_OPEN_SESSION,
                OPERATION_CLOSE_HISTORY,
                OPERATION_REOPEN_SELECTED,
                OPERATION_RESELECT_ARMED_SESSION,
                -> putState(productResponse)
                OPERATION_CLOSE_VIEW -> {
                    sanitizeClosedViewState(productResponse).forEach { (key, value) -> put(key, value) }
                }
                OPERATION_INSPECT -> {
                    putState(productResponse)
                    request.arguments[EXTRA_CLIPBOARD_FIXTURE]?.let { fixture ->
                        val armed = readSessionArm(context)?.let {
                            armedSessionId(it, request.runId, System.currentTimeMillis())
                        }
                        check(armed?.toString() == productResponse.safeSessionId() &&
                            productResponse.requiredBoolean("clipboard_fixture_ready") &&
                            productResponse.requiredBoolean("realtime_subscriber_ready"))
                        put("clipboard_fixture_matches", clipboardFixtureMatches(context, request.runId, fixture))
                    }
                    request.arguments[EXTRA_NOTIFICATION_FIXTURE]?.let {
                        val armed = readSessionArm(context)?.let { arm ->
                            armedSessionId(arm, request.runId, System.currentTimeMillis())
                        }
                        check(armed?.toString() == productResponse.safeSessionId() &&
                            productResponse.requiredBoolean("realtime_subscriber_ready"))
                        put("terminal_fixture_notification_present", terminalNotificationFixturePresent(
                            context, productResponse.safeSessionId(),
                        ))
                    }
                    productResponse["restore_diagnostics"]?.let {
                        put("restore_diagnostics", sanitizeRestoreDiagnostics(
                            it as? JsonObject ?: error("Invalid restore diagnostics"),
                        ))
                    }
                    put("frozen_history", sanitizeFrozenHistoryState(
                        productResponse["frozen_history"] as? JsonObject ?: error("Missing frozen history"),
                    ))
                    (productResponse["terminal_semantics"] as? JsonObject)?.let {
                        check(it.toString().length <= 512 * 1024) { "Semantic observation exceeds its limit" }
                        put("terminal_semantics", it)
                    }
                    productResponse["shell_semantics"]?.let {
                        check(it is JsonObject && it.toString().toByteArray(Charsets.UTF_8).size <= 32 * 1024) {
                            "Shell observation exceeds its limit or has an invalid shape"
                        }
                        put("shell_semantics", it)
                    }
                    val (count, lastSequence) = brokerCaptureEvidence(context, productResponse.safeSessionId())
                    put("broker_capture_count", count)
                    put("broker_capture_last_sequence", lastSequence)
                }
                OPERATION_BEGIN_FROZEN_HISTORY,
                OPERATION_PAGE_FROZEN_HISTORY,
                OPERATION_RELEASE_FROZEN_HISTORY,
                -> {
                    putState(productResponse)
                    sanitizeFrozenState(productResponse).forEach { (key, value) -> put(key, value) }
                }
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

    internal fun sanitizeRecipientIdentity(source: JsonObject): JsonObject = buildJsonObject {
        val id = source["android_device_id"]?.jsonPrimitive?.takeIf { it.isString }?.content
            ?: error("Missing recipient identity")
        val parsed = runCatching { UUID.fromString(id) }.getOrNull()
        check(parsed != null && parsed != EMPTY_UUID && parsed.toString() == id) {
            "Invalid recipient identity"
        }
        val scheme = source.signedInt("terminal_theme_scheme")
        check(scheme == 1 || scheme == 2) { "Invalid local terminal theme" }
        put("android_device_id", id)
        put("terminal_theme_scheme", scheme)
    }

    internal fun sanitizeClosedViewState(source: JsonObject): JsonObject = buildJsonObject {
        put("session_id", source.safeSessionId())
        check(source.requiredBoolean("view_closed"))
        put("view_closed", true)
        put("session_running", source.requiredBoolean("session_running"))
    }

    internal fun sanitizeRestoreDiagnostics(source: JsonObject): JsonObject = buildJsonObject {
        val attempt = source.nonNegativeUnsignedJsonNumber("attempt")
        check(attempt.jsonPrimitive.content.toULong() > 0uL)
        put("attempt", attempt)
        listOf("delta_request_count", "delta_install_count", "delta_expected_chunk_count", "broker_resolved_count",
            "delta_chunks_accepted", "delta_duplicate_chunks", "delta_chunk_acks_sent",
            "broker_chunks_accepted", "broker_duplicate_chunks", "broker_chunk_acks_sent",
            "full_snapshot_install_count")
            .forEach { put(it, source.nonNegativeInt(it)) }
        listOf("delta_request_id", "broker_request_id").forEach { name ->
            source[name]?.let {
                val value = it.jsonPrimitive.takeIf { primitive -> primitive.isString }?.content
                val id = requireNotNull(value).let(UUID::fromString)
                check(id != UUID(0, 0) && id.toString() == value)
                put(name, value)
            }
        }
        source["delta_fallback_reason"]?.let {
            val reason = it.jsonPrimitive.takeIf { value -> value.isString }?.content
            check(reason != null && reason in DELTA_FALLBACK_REASONS)
            put("delta_fallback_reason", requireNotNull(reason))
        }
        listOf("base", "target").forEach { prefix ->
            val sequence = source["${prefix}_snapshot_sequence"]
            val hash = source["${prefix}_sha256"]
            check((sequence == null) == (hash == null))
            if (sequence != null && hash != null) {
                put("${prefix}_snapshot_sequence", source.nonNegativeUnsignedJsonNumber("${prefix}_snapshot_sequence"))
                put("${prefix}_sha256", source.safeSha256("${prefix}_sha256"))
            }
        }
        val fullSequence = source["full_snapshot_sequence"]
        val fullHash = source["full_snapshot_sha256"]
        check((fullSequence == null) == (fullHash == null))
        if (fullSequence != null && fullHash != null) {
            put("full_snapshot_sequence", source.nonNegativeUnsignedJsonNumber("full_snapshot_sequence"))
            put("full_snapshot_sha256", source.safeSha256("full_snapshot_sha256"))
        }
    }

    internal fun sanitizeFrozenHistoryState(source: JsonObject): JsonObject = buildJsonObject {
        val active = source.requiredBoolean("active")
        val pages = source.nonNegativeInt("page_count")
        check(pages <= 2 && (active || pages == 0))
        put("active", active)
        listOf("capture_id", "revision", "first_line_id", "last_line_id",
            "first_page_first_logical_row", "first_page_last_logical_row")
            .forEach { put(it, source.nonNegativeUnsignedJsonNumber(it)) }
        put("page_count", pages)
        put("loaded_state_sha256", source.safeSha256("loaded_state_sha256"))
    }

    private fun kotlinx.serialization.json.JsonObjectBuilder.putState(source: JsonObject) {
        sanitizeRecipientIdentity(source).forEach { (key, value) -> put(key, value) }
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
        put("first_visible_line_id", source.nonNegativeUnsignedJsonNumber("first_visible_line_id"))
        put("visible_text_sha256", source.safeSha256("visible_text_sha256"))
        put("next_before_line_id", source.nonNegativeUnsignedJsonNumber("next_before_line_id"))
        put("transcript_top_row", source.signedInt("transcript_top_row"))
        put("transcript_page_count", source.nonNegativeInt("transcript_page_count"))
        put("transcript_row_count", source.nonNegativeInt("transcript_row_count"))
    }

    private fun brokerCaptureEvidence(context: Context, sessionId: String): Pair<Int, Long> {
        val databaseFile = context.getDatabasePath(HISTORY_DATABASE_NAME)
        return SQLiteDatabase.openDatabase(databaseFile.absolutePath, null, SQLiteDatabase.OPEN_READONLY).use { database ->
            database.rawQuery(
                "SELECT COUNT(*), COALESCE(MAX(sourceSequence), 0) FROM broker_captures WHERE sessionId = ?",
                arrayOf(sessionId),
            ).use { cursor ->
                check(cursor.moveToFirst())
                val count = cursor.getInt(0)
                val lastSequence = cursor.getLong(1)
                check(count >= 0 && lastSequence >= 0 && (count > 0 || lastSequence == 0L))
                count to lastSequence
            }
        }
    }

    private fun clipboardFixtureMatches(context: Context, runId: String, fixture: String): Boolean {
        check(fixture in CLIPBOARD_FIXTURES)
        val marker = "TLEF_${runId.replace(Regex("[^A-Za-z0-9]"), "_")}_COPY_${fixture.uppercase()}"
        return runCatching {
            val clipboard = context.getSystemService(ClipboardManager::class.java).primaryClip
            clipboard?.takeIf { it.itemCount == 1 }?.getItemAt(0)?.text?.toString() == marker
        }.getOrDefault(false)
    }

    private fun terminalNotificationFixturePresent(context: Context, sessionId: String): Boolean =
        runCatching {
            context.getSystemService(NotificationManager::class.java).activeNotifications.any { active ->
                active.tag == "terminal:$sessionId" && active.id == 1 &&
                    active.notification.extras.getCharSequence(Notification.EXTRA_TITLE)?.toString() == "TermLink CLI" &&
                    active.notification.extras.getCharSequence(Notification.EXTRA_TEXT)?.toString() ==
                        context.getString(R.string.terminal_attention_message)
            }
        }.getOrDefault(false)

    internal fun sanitizeFrozenState(source: JsonObject): JsonObject = buildJsonObject {
        putFrozenState(source)
    }

    private fun kotlinx.serialization.json.JsonObjectBuilder.putFrozenState(source: JsonObject) {
        val active = source.requiredBoolean("frozen_active")
        val ready = source.requiredBoolean("frozen_render_ready")
        val pages = source.nonNegativeInt("frozen_page_count")
        check(pages <= 2 && (!ready || (active && pages > 0)))
        put("frozen_active", active)
        put("frozen_render_ready", ready)
        put("frozen_page_count", pages)
        listOf("frozen_capture_id", "frozen_revision", "frozen_snapshot_sequence_s",
            "frozen_first_line_id", "frozen_last_line_id", "frozen_next_before_line_id")
            .forEach { put(it, source.nonNegativeUnsignedJsonNumber(it)) }
        put("frozen_has_more_before", source.requiredBoolean("frozen_has_more_before"))
        put("frozen_head_truncated", source.requiredBoolean("frozen_head_truncated"))
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
        get(name)?.jsonPrimitive?.takeUnless { it.isString }?.booleanOrNull

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

    private fun JsonObject.signedInt(name: String): Int =
        get(name)?.jsonPrimitive?.takeUnless { it.isString }?.intOrNull
            ?: error("Invalid signed integer")

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

    private fun JsonObject.safeSha256(name: String): String =
        get(name)?.jsonPrimitive?.takeIf { it.isString }?.content
            ?.takeIf(SHA256::matches)
            ?: error("Invalid SHA-256")

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
        sessionId: String? = null,
    ) {
        val responseDirectory = File(context.noBackupFilesDir, RESPONSE_DIRECTORY)
        check(responseDirectory.isDirectory || responseDirectory.mkdirs())
        val atomicFile = AtomicFile(File(responseDirectory, SESSION_ARM_FILE_NAME))
        val response = buildJsonObject {
            put("schema_version", SCHEMA_VERSION)
            put("request_id", request.requestId)
            put("operation", request.operation)
            put("run_id", request.runId)
            put("armed", true)
            put("armed_at_epoch_millis", System.currentTimeMillis())
            sessionId?.let { put("session_id", it) }
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

    private fun readSessionArm(context: Context): JsonObject? = runCatching {
        val file = File(File(context.noBackupFilesDir, RESPONSE_DIRECTORY), SESSION_ARM_FILE_NAME)
        if (!file.isFile || file.length() !in 1..131_072) return@runCatching null
        JSON.parseToJsonElement(file.readText(Charsets.UTF_8)) as? JsonObject
    }.getOrNull()

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
        const val EXTRA_HISTORY_BOOTSTRAP_MODE = "history_bootstrap_mode"
        const val EXTRA_FROZEN_LIFECYCLE_ACCEPTANCE = "frozen_lifecycle_acceptance"
        const val EXTRA_PAGE_SIZE = "page_size"
        const val EXTRA_PAGE_NUMBER = "page_number"
        const val EXTRA_EXPECTED_RESULT = "expected_result"
        const val EXTRA_CLIPBOARD_FIXTURE = "clipboard_fixture"
        const val EXTRA_NOTIFICATION_FIXTURE = "notification_fixture"

        const val EXPECTED_PAGE_RESULT_SUCCESS = "success"
        const val EXPECTED_PAGE_RESULT_TRANSCRIPT_LOAD_FAILED = "transcript_load_failed"

        const val OPERATION_PROBE = "probe"
        const val OPERATION_OPEN_SESSION = "open_session"
        const val OPERATION_CLOSE_VIEW = "close_view"
        const val OPERATION_REOPEN_SELECTED = "reopen_selected"
        const val OPERATION_RESELECT_ARMED_SESSION = "reselect_armed_session"
        const val OPERATION_INSPECT = "inspect"
        const val OPERATION_PAGE_UP = "page_up"
        const val OPERATION_OPEN_HISTORY = "open_history"
        const val OPERATION_CLOSE_HISTORY = "close_history"
        const val OPERATION_SHOW_IME = "show_ime"
        const val OPERATION_BEGIN_FROZEN_HISTORY = "begin_frozen_history"
        const val OPERATION_PAGE_FROZEN_HISTORY = "page_frozen_history"
        const val OPERATION_RELEASE_FROZEN_HISTORY = "release_frozen_history"
        const val INVALID_OPERATION = "invalid"

        const val FAILURE_REQUEST_INVALID = "REQUEST_INVALID"
        const val FAILURE_OPERATION_FAILED = "OPERATION_FAILED"
        const val FAILURE_TRANSCRIPT_LOAD_FAILED = "TRANSCRIPT_LOAD_FAILED"
        const val MAX_PAGE_SIZE = 200
        const val MAX_PAGE_NUMBER = 25
        const val BRIDGE_TIMEOUT_MILLIS = 180_000L
        const val BASELINE_READINESS_TIMEOUT_MILLIS = 10_000L
        const val BASELINE_READINESS_POLL_MILLIS = 50L
        const val SNAPSHOT_MARKER_COUNT = 5200
        const val STREAMED_PREFIX_MARKER_COUNT = 5200
        const val FROZEN_LIFECYCLE_MARKER_COUNT = 1400
        const val FROZEN_LIFECYCLE_ACCEPTANCE_ENABLED = "true"
        const val HISTORY_BOOTSTRAP_SNAPSHOT_TRANSCRIPT = "SnapshotTranscript"
        const val HISTORY_BOOTSTRAP_STREAMED_PREFIX = "StreamedChunkPrefix"
        val STREAMED_PREFIX_MINIMUM_TAIL = 4097uL

        val REQUEST_ID = Regex("^[0-9a-f]{32}$")
        val RUN_ID = Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
        val UNSIGNED_INTEGER = Regex("^(0|[1-9][0-9]{0,19})$")
        val FAILURE_CODE = Regex("^[A-Z0-9_]{1,64}$")
        val VIEWPORT_SIGNATURE = Regex("^[A-Za-z0-9:_-]{1,128}$")
        val SHA256 = Regex("^[0-9a-f]{64}$")
        val DIAGNOSTIC_NAME = Regex("^[A-Z_]{2,32}$")
        val EMPTY_UUID: UUID = UUID(0, 0)
        val EXPECTED_PAGE_RESULTS = setOf(
            EXPECTED_PAGE_RESULT_SUCCESS,
            EXPECTED_PAGE_RESULT_TRANSCRIPT_LOAD_FAILED,
        )
        val CLIPBOARD_FIXTURES = setOf("reject", "accept")
        val DELTA_FALLBACK_REASONS = setOf(
            "BASE_MISSING", "BASE_PROFILE_MISMATCH", "UNAVAILABLE", "NEEDS_FULL_SNAPSHOT",
            "RETIRED_BASE", "CANCELLED", "FAILED",
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
        val FROZEN_LIFECYCLE_OPEN_SESSION_KEYS = setOf(
            EXTRA_FIRST_MARKER,
            EXTRA_LAST_MARKER,
            EXTRA_MARKER_COUNT,
            EXTRA_MINIMUM_TAIL,
            EXTRA_HISTORY_BOOTSTRAP_MODE,
            EXTRA_FROZEN_LIFECYCLE_ACCEPTANCE,
        )
        val SUPPORTED_OPERATIONS = linkedSetOf(
            OPERATION_PROBE,
            OPERATION_OPEN_SESSION,
            OPERATION_CLOSE_VIEW,
            OPERATION_REOPEN_SELECTED,
            OPERATION_RESELECT_ARMED_SESSION,
            OPERATION_INSPECT,
            OPERATION_PAGE_UP,
            OPERATION_OPEN_HISTORY,
            OPERATION_CLOSE_HISTORY,
            OPERATION_SHOW_IME,
            OPERATION_BEGIN_FROZEN_HISTORY,
            OPERATION_PAGE_FROZEN_HISTORY,
            OPERATION_RELEASE_FROZEN_HISTORY,
        )
        val OPERATION_EXTRAS = mapOf(
            OPERATION_PROBE to emptySet(),
            OPERATION_OPEN_SESSION to setOf(
                EXTRA_FIRST_MARKER,
                EXTRA_LAST_MARKER,
                EXTRA_MARKER_COUNT,
                EXTRA_MINIMUM_TAIL,
                EXTRA_HISTORY_BOOTSTRAP_MODE,
            ),
            OPERATION_CLOSE_VIEW to emptySet(),
            OPERATION_REOPEN_SELECTED to emptySet(),
            OPERATION_RESELECT_ARMED_SESSION to emptySet(),
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
            OPERATION_BEGIN_FROZEN_HISTORY to emptySet(),
            OPERATION_PAGE_FROZEN_HISTORY to emptySet(),
            OPERATION_RELEASE_FROZEN_HISTORY to emptySet(),
        )
        val ALLOWED_MARKER_COUNTS = setOf(SNAPSHOT_MARKER_COUNT, STREAMED_PREFIX_MARKER_COUNT)
        val HISTORY_BOOTSTRAP_MODES = setOf(
            HISTORY_BOOTSTRAP_SNAPSHOT_TRANSCRIPT,
            HISTORY_BOOTSTRAP_STREAMED_PREFIX,
        )
        val JSON = Json { explicitNulls = false }
        val receiverScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val operationMutex = Mutex()
    }
}
