package dev.termlink.product

import dev.termlink.model.SessionState
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.TerminalCommandCapability
import dev.termlink.model.isPcAgent
import java.io.Closeable
import java.lang.ref.WeakReference
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

/** Bounded result returned to the Debug-only acceptance receiver. */
internal data class DirectProductE2EOperationResult(
    val ok: Boolean,
    val failureCode: String? = null,
    val payload: JsonObject = JsonObject(emptyMap()),
) {
    init {
        require(ok == (failureCode == null))
        require(failureCode == null || failureCode.matches(FAILURE_CODE))
    }

    companion object {
        private val FAILURE_CODE = Regex("^[A-Z0-9_]{1,96}$")

        fun success(payload: JsonObject = JsonObject(emptyMap())) =
            DirectProductE2EOperationResult(ok = true, payload = payload)

        fun blocked(code: String) = DirectProductE2EOperationResult(ok = false, failureCode = code)
    }
}

/** Interface kept small so registry fencing can be tested without constructing the product repository. */
internal interface DirectProductE2EController : Closeable {
    suspend fun execute(
        runIdSha256: String,
        operation: String,
        arguments: JsonObject,
    ): DirectProductE2EOperationResult
}

/**
 * Holds only a weak reference to the current acceptance controller. Closing an
 * older repository compares the exact binding before clearing it, so it can
 * never detach a successor created during Activity/ViewModel reconstruction.
 */
internal object ProductionDirectProductE2EBridgeRegistry {
    private val lock = Any()
    private var current: Binding? = null

    fun attach(controller: DirectProductE2EController): Closeable {
        val binding = Binding(WeakReference(controller))
        synchronized(lock) { current = binding }
        return Closeable {
            synchronized(lock) {
                if (current === binding) {
                    current = null
                }
            }
            // The repository owns this exact controller even if a successor is
            // already registered, so its local work must still be retired.
            controller.close()
        }
    }

    fun current(): DirectProductE2EController? = synchronized(lock) {
        current?.controller?.get()?.also { return@synchronized it }
        current = null
        null
    }

    private class Binding(val controller: WeakReference<DirectProductE2EController>)
}

/**
 * Debug-acceptance adapter over the real product repository.
 *
 * It does not invent Direct counters. Until terminal-core/network-core exposes
 * a single session-scoped redacted snapshot, transport inspection fails closed.
 */
internal class ProductionDirectProductE2EBridge(
    private val repository: ProductionProductRepository,
    private val captureAuthenticationGeneration: () -> Long,
    private val isAuthenticationGenerationCurrent: (Long) -> Boolean,
    private val moveProductToBackground: () -> Boolean,
    private val moveProductToForeground: () -> Boolean,
) : DirectProductE2EController {
    private val closed = AtomicBoolean(false)
    private val operationLock = Mutex()
    private var runState: RunState? = null

    override suspend fun execute(
        runIdSha256: String,
        operation: String,
        arguments: JsonObject,
    ): DirectProductE2EOperationResult = operationLock.withLock {
        if (closed.get()) return@withLock DirectProductE2EOperationResult.blocked("PRODUCT_REPOSITORY_CLOSED")
        if (!runIdSha256.matches(SHA256)) {
            return@withLock DirectProductE2EOperationResult.blocked("ARGUMENTS_INVALID")
        }
        validateArguments(operation, arguments)?.let {
            return@withLock DirectProductE2EOperationResult.blocked(it)
        }
        try {
            when (operation) {
                "prepare_session" -> prepareSession(runIdSha256)
                "inspect_transport" -> inspectTransport(runIdSha256, arguments)
                "send_text_marker" -> sendTextMarker(runIdSha256, arguments)
                "send_key" -> sendKey(runIdSha256, arguments)
                "request_resize" -> DirectProductE2EOperationResult.blocked("REMOTE_RESIZE_UNSUPPORTED")
                "background" -> background(runIdSha256)
                "foreground" -> foreground(runIdSha256)
                "lock_screen" -> DirectProductE2EOperationResult.blocked("DEVICE_LOCK_CONTROL_UNAVAILABLE")
                "wake_screen" -> DirectProductE2EOperationResult.blocked("DEVICE_WAKE_CONTROL_UNAVAILABLE")
                "open_late_session" -> openLateSession(runIdSha256)
                "start_long_output" -> startLongOutput(runIdSha256)
                "stop_long_output" -> stopLongOutput(runIdSha256)
                "close_test_sessions" -> closeTestSessions(runIdSha256)
                else -> DirectProductE2EOperationResult.blocked("OPERATION_UNSUPPORTED")
            }
        } catch (error: CancellationException) {
            throw error
        } catch (error: DirectProductE2EBlockedException) {
            DirectProductE2EOperationResult.blocked(error.code)
        } catch (_: Throwable) {
            DirectProductE2EOperationResult.blocked("PRODUCT_OPERATION_FAILED")
        }
    }

    override fun close() {
        closed.set(true)
        runState = null
    }

    private suspend fun prepareSession(runIdSha256: String): DirectProductE2EOperationResult {
        val generation = captureAuthenticationGeneration()
        repository.refresh()
        ensureGeneration(generation)
        val snapshot = repository.snapshot.value
        if (!snapshot.signedIn) throw DirectProductE2EBlockedException("ACCOUNT_UNAVAILABLE")
        val trustedPcDeviceIds = snapshot.devices.asSequence()
            .filter { it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED }
            .mapTo(hashSetOf()) { it.id }
        val eligible = snapshot.pcs.mapNotNull { pc ->
            val command = pc.terminalCommands.singleOrNull {
                it.id == ACCEPTANCE_COMMAND_ID && it.shellType == ACCEPTANCE_COMMAND_ID
            } ?: return@mapNotNull null
            if (!pc.online || pc.deviceId !in trustedPcDeviceIds ||
                PcCapabilityIds.FILE_OPERATIONS_V1 !in pc.capabilities ||
                PcCapabilityIds.TERMINAL_COMMANDS_V1 !in pc.capabilities
            ) return@mapNotNull null
            pc to command
        }
        if (eligible.isEmpty()) throw DirectProductE2EBlockedException("ACCEPTANCE_PC_UNAVAILABLE")
        if (eligible.size != 1) throw DirectProductE2EBlockedException("SESSION_SELECTION_AMBIGUOUS")
        val (pc, command) = eligible.single()
        val sessionId = repository.openRemoteShell(pc.id, WINDOWS_SYSTEM_DRIVE_ROOT, command)
        repository.openSession(sessionId)
        repository.requestControl(sessionId)
        ensureGeneration(generation)
        checkNotNull(repository.terminalEngine(sessionId))
        runState = RunState(
            runIdSha256 = runIdSha256,
            authenticationGeneration = generation,
            primarySessionId = sessionId,
            pcId = pc.id,
            peerDeviceId = pc.deviceId,
            currentSessionId = sessionId,
            command = command,
            openedSessionIds = linkedSetOf(sessionId),
            createdSessionIds = linkedSetOf(sessionId),
        )
        return DirectProductE2EOperationResult.success(
            buildJsonObject {
                put("session_id", sessionId.toString())
                put("session_id_sha256", sha256(sessionId.toString()))
                put("peer_device_id", pc.deviceId.toString())
                put("session_prepared", true)
            },
        )
    }

    private fun inspectTransport(
        runIdSha256: String,
        arguments: JsonObject,
    ): DirectProductE2EOperationResult {
        val state = requireRun(runIdSha256)
        val role = arguments.requiredString("session_role")
        state.sessionForRole(role)
        // ProductSnapshot exposes only the user-facing carrier. path_epoch,
        // native duplicate/gap counters, and transport receipt watermark are
        // not available at this layer and must never be inferred from UI state.
        return DirectProductE2EOperationResult.blocked("DIRECT_DIAGNOSTICS_UNAVAILABLE")
    }

    private suspend fun sendTextMarker(
        runIdSha256: String,
        arguments: JsonObject,
    ): DirectProductE2EOperationResult {
        val state = requireRun(runIdSha256)
        val marker = arguments.requiredString("marker")
        repository.sendCommand(state.currentSessionId, "Write-Output '$marker'")
        ensureRunCurrent(state)
        val viewportDigest = awaitMarker(state.currentSessionId, marker)
            ?: throw DirectProductE2EBlockedException("MARKER_NOT_OBSERVED")
        state.lastMarkerDigest = sha256(marker)
        state.lastViewportDigest = viewportDigest
        return DirectProductE2EOperationResult.success(
            buildJsonObject {
                put("marker_digest", state.lastMarkerDigest)
                put("viewport_digest", state.lastViewportDigest)
            },
        )
    }

    private suspend fun sendKey(
        runIdSha256: String,
        arguments: JsonObject,
    ): DirectProductE2EOperationResult {
        val state = requireRun(runIdSha256)
        repository.sendTerminalKeyCombination(state.currentSessionId, arguments.requiredString("key"))
        ensureRunCurrent(state)
        return DirectProductE2EOperationResult.success()
    }

    private suspend fun background(runIdSha256: String): DirectProductE2EOperationResult {
        val state = requireRun(runIdSha256)
        if (!moveProductToBackground()) {
            return DirectProductE2EOperationResult.blocked("PRODUCT_ACTIVITY_UNAVAILABLE")
        }
        val confirmed = awaitLifecycleState {
            repository.debugDirectProductE2ELifecycle().earliestBackgroundAtMillis != null
        }
        if (!confirmed) return DirectProductE2EOperationResult.blocked("LIFECYCLE_TRANSITION_UNCONFIRMED")
        ensureRunCurrent(state)
        return DirectProductE2EOperationResult.success()
    }

    private suspend fun foreground(runIdSha256: String): DirectProductE2EOperationResult {
        val state = requireRun(runIdSha256)
        val before = repository.debugDirectProductE2ELifecycle().lastForegroundGeneration
        if (!moveProductToForeground()) {
            return DirectProductE2EOperationResult.blocked("PRODUCT_ACTIVITY_UNAVAILABLE")
        }
        val confirmed = awaitLifecycleState {
            repository.debugDirectProductE2ELifecycle().let {
                it.earliestBackgroundAtMillis == null && it.lastForegroundGeneration > before
            }
        }
        if (!confirmed) return DirectProductE2EOperationResult.blocked("LIFECYCLE_TRANSITION_UNCONFIRMED")
        ensureRunCurrent(state)
        return DirectProductE2EOperationResult.success()
    }

    private suspend fun openLateSession(runIdSha256: String): DirectProductE2EOperationResult {
        val state = requireRun(runIdSha256)
        val snapshot = repository.snapshot.value
        val pc = snapshot.pcs.firstOrNull { it.id == state.pcId && it.online }
            ?: throw DirectProductE2EBlockedException("PC_UNAVAILABLE")
        if (pc.deviceId != state.peerDeviceId) {
            throw DirectProductE2EBlockedException("PEER_IDENTITY_CHANGED")
        }
        val command = pc.terminalCommands.singleOrNull { it.id == state.command.id }
            ?.takeIf { it == state.command }
            ?: throw DirectProductE2EBlockedException("TERMINAL_COMMAND_CHANGED")
        val sessionId = repository.openRemoteShell(pc.id, WINDOWS_SYSTEM_DRIVE_ROOT, state.command)
        repository.openSession(sessionId)
        repository.requestControl(sessionId)
        ensureRunCurrent(state)
        state.currentSessionId = sessionId
        state.lateSessionId = sessionId
        state.openedSessionIds += sessionId
        state.createdSessionIds += sessionId
        return DirectProductE2EOperationResult.success(
            buildJsonObject {
                put("session_id", sessionId.toString())
                put("session_role", "late")
                put("session_id_sha256", sha256(sessionId.toString()))
                put("peer_device_id", state.peerDeviceId.toString())
                put("late_session_opened", true)
            },
        )
    }

    private suspend fun startLongOutput(runIdSha256: String): DirectProductE2EOperationResult {
        requireRun(runIdSha256)
        // Input acceptance alone does not prove the Windows workload started.
        // Wait for a Windows-side bounded readiness signal before enabling it.
        return DirectProductE2EOperationResult.blocked("LONG_OUTPUT_CONFIRMATION_UNAVAILABLE")
    }

    private suspend fun stopLongOutput(runIdSha256: String): DirectProductE2EOperationResult {
        requireRun(runIdSha256)
        return DirectProductE2EOperationResult.blocked("LONG_OUTPUT_CONFIRMATION_UNAVAILABLE")
    }

    private suspend fun closeTestSessions(runIdSha256: String): DirectProductE2EOperationResult {
        val state = requireRun(runIdSha256)
        var failed = false
        if (state.longOutputActive) {
            runCatching { repository.sendTerminalAction(state.currentSessionId, TerminalAction.CTRL_C) }
                .onFailure { failed = true }
        }
        for (sessionId in state.createdSessionIds.toList().asReversed()) {
            runCatching { repository.stopSession(sessionId) }
                .onFailure {
                    failed = true
                    runCatching { repository.closeSessionView(sessionId) }
                }
        }
        runState = null
        return if (failed) {
            DirectProductE2EOperationResult.blocked("TEST_SESSION_CLEANUP_INCOMPLETE")
        } else {
            DirectProductE2EOperationResult.success()
        }
    }

    private fun requireRun(runIdSha256: String): RunState {
        val state = runState
            ?.takeIf { it.runIdSha256 == runIdSha256 }
            ?: throw DirectProductE2EBlockedException("TEST_SESSION_UNAVAILABLE")
        ensureRunCurrent(state)
        return state
    }

    private fun ensureRunCurrent(state: RunState) {
        if (closed.get()) throw DirectProductE2EBlockedException("PRODUCT_REPOSITORY_CLOSED")
        if (!isAuthenticationGenerationCurrent(state.authenticationGeneration)) {
            runState = null
            throw DirectProductE2EBlockedException("AUTHENTICATION_CHANGED")
        }
    }

    private fun ensureGeneration(generation: Long) {
        if (closed.get()) throw DirectProductE2EBlockedException("PRODUCT_REPOSITORY_CLOSED")
        if (!isAuthenticationGenerationCurrent(generation)) {
            throw DirectProductE2EBlockedException("AUTHENTICATION_CHANGED")
        }
    }

    private suspend fun awaitMarker(sessionId: UUID, marker: String): String? {
        repeat(MARKER_OBSERVATION_ATTEMPTS) {
            val text = repository.terminalEngine(sessionId)?.visibleText().orEmpty()
            if (marker in text) return sha256(text)
            delay(OBSERVATION_POLL_MILLIS)
        }
        return null
    }

    private suspend fun awaitLifecycleState(predicate: () -> Boolean): Boolean {
        repeat(LIFECYCLE_OBSERVATION_ATTEMPTS) {
            if (predicate()) return true
            delay(OBSERVATION_POLL_MILLIS)
        }
        return false
    }

    private data class RunState(
        val runIdSha256: String,
        val authenticationGeneration: Long,
        val primarySessionId: UUID,
        val pcId: UUID,
        val peerDeviceId: UUID,
        var currentSessionId: UUID,
        val command: TerminalCommandCapability,
        val openedSessionIds: LinkedHashSet<UUID>,
        val createdSessionIds: LinkedHashSet<UUID> = linkedSetOf(),
        var lateSessionId: UUID? = null,
        var longOutputActive: Boolean = false,
        var lastMarkerDigest: String = EMPTY_SHA256,
        var lastViewportDigest: String = EMPTY_SHA256,
    ) {
        fun sessionForRole(role: String): UUID = when (role) {
            "primary" -> primarySessionId
            "late" -> lateSessionId
                ?: throw DirectProductE2EBlockedException("LATE_SESSION_UNAVAILABLE")
            else -> throw DirectProductE2EBlockedException("ARGUMENTS_INVALID")
        }
    }

    private class DirectProductE2EBlockedException(val code: String) : IllegalStateException(code)

    private companion object {
        val SHA256 = Regex("^[a-f0-9]{64}$")
        val SCENARIO_ID = Regex("^[a-z0-9_]{1,96}$")
        val PHASE = Regex("^[a-z0-9_]{1,96}$")
        val MARKER = Regex("^TLDPE_[a-f0-9]{20}$")
        val KEY_COMBINATIONS = setOf(
            "Esc", "Tab", "Enter", "Backspace", "Delete", "Insert", "Home", "End",
            "PageUp", "PageDown", "Left", "Up", "Down", "Right",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            "Ctrl+C",
        )
        val COMMON_ARGUMENTS = setOf("scenario_id", "phase")
        const val WINDOWS_SYSTEM_DRIVE_ROOT = "C:\\"
        const val ACCEPTANCE_COMMAND_ID = "powershell-7"
        const val OBSERVATION_POLL_MILLIS = 100L
        const val MARKER_OBSERVATION_ATTEMPTS = 50
        const val LIFECYCLE_OBSERVATION_ATTEMPTS = 30
        val EMPTY_SHA256 = sha256("")

        fun validateArguments(operation: String, arguments: JsonObject): String? {
            val expectedKeys = when (operation) {
                "send_text_marker" -> COMMON_ARGUMENTS + "marker"
                "send_key" -> COMMON_ARGUMENTS + "key"
                "request_resize" -> COMMON_ARGUMENTS + setOf("columns", "rows")
                "inspect_transport" -> COMMON_ARGUMENTS + "session_role"
                "prepare_session", "background", "foreground",
                "lock_screen", "wake_screen", "open_late_session", "start_long_output",
                "stop_long_output", "close_test_sessions",
                -> COMMON_ARGUMENTS
                else -> return "OPERATION_UNSUPPORTED"
            }
            if (arguments.keys != expectedKeys) return "ARGUMENTS_INVALID"
            if (!arguments.matchesString("scenario_id", SCENARIO_ID) ||
                !arguments.matchesString("phase", PHASE)
            ) return "ARGUMENTS_INVALID"
            return when (operation) {
                "send_text_marker" -> if (arguments.matchesString("marker", MARKER)) null else "ARGUMENTS_INVALID"
                "send_key" -> if (arguments.requiredStringOrNull("key") in KEY_COMBINATIONS) null else "ARGUMENTS_INVALID"
                "request_resize" -> {
                    val columns = arguments["columns"]?.jsonPrimitive?.intOrNull
                    val rows = arguments["rows"]?.jsonPrimitive?.intOrNull
                    if (columns in 20..400 && rows in 5..200) null else "ARGUMENTS_INVALID"
                }
                "inspect_transport" ->
                    if (arguments.requiredStringOrNull("session_role") in SESSION_ROLES) null
                    else "ARGUMENTS_INVALID"
                else -> null
            }
        }

        val SESSION_ROLES = setOf("primary", "late")

        fun sha256(value: String): String {
            val bytes = value.encodeToByteArray()
            return try {
                MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
            } finally {
                bytes.fill(0)
            }
        }
    }
}

private fun JsonObject.requiredString(name: String): String =
    requiredStringOrNull(name) ?: throw IllegalArgumentException("Invalid $name")

private fun JsonObject.requiredStringOrNull(name: String): String? =
    (get(name) as? JsonPrimitive)?.takeIf { it.isString }?.contentOrNull

private fun JsonObject.matchesString(name: String, pattern: Regex): Boolean =
    requiredStringOrNull(name)?.matches(pattern) == true
