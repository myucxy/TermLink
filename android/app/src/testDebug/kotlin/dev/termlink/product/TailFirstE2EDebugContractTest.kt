package dev.termlink.product

import dev.termlink.harness.BuildConfig
import dev.termlink.product.debug.tailFirstLiveMarker
import dev.termlink.product.debug.tailFirstInspectMarkerIsValid
import dev.termlink.product.debug.frozenLifecycleMarkerStem
import dev.termlink.product.debug.armedSessionId
import dev.termlink.product.debug.TailFirstE2EReceiver
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.session.RemoteSessionState
import java.time.Instant
import java.util.UUID
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.net.URI
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.assertThrows
import org.junit.Test

class TailFirstE2EDebugContractTest {
    @Test
    fun recipientIdentityIsCanonicalBoundedAndContainsNoPrivateFields() {
        val receiver = TailFirstE2EReceiver()
        val id = "a0b1c2d3-e4f5-4678-9abc-def012345678"
        fun source(device: String = "\"$id\"", scheme: String = "1") =
            Json.parseToJsonElement("""{"android_device_id":$device,"terminal_theme_scheme":$scheme,"private_key":"never export"}""").jsonObject
        for (scheme in listOf("1", "2")) {
            val sanitized = receiver.sanitizeRecipientIdentity(source(scheme = scheme))
            assertEquals(setOf("android_device_id", "terminal_theme_scheme"), sanitized.keys)
            assertEquals(id, sanitized.getValue("android_device_id").jsonPrimitive.content)
            assertEquals(scheme.toInt(), sanitized.getValue("terminal_theme_scheme").jsonPrimitive.int)
        }
        for (invalid in listOf("\"${UUID(0, 0)}\"", "\"1-1-1-1-1\"", "\"${id.uppercase()}\"", "null", "1")) {
            assertThrows(IllegalStateException::class.java) { receiver.sanitizeRecipientIdentity(source(device = invalid)) }
        }
        for (invalid in listOf("0", "3", "\"1\"", "null", "true")) {
            assertThrows(IllegalStateException::class.java) { receiver.sanitizeRecipientIdentity(source(scheme = invalid)) }
        }
        assertEquals(1, tailFirstTerminalThemeScheme(TerminalDisplayPreferences(backgroundArgb = 0xFF080B10.toInt())))
        assertEquals(2, tailFirstTerminalThemeScheme(TerminalDisplayPreferences(backgroundArgb = 0xFFE9EEF5.toInt())))
    }

    @Test
    fun debugIdentityReplacesTheWholePairAndClearsOnInvalidInputOrUnregister() {
        val id = "a0b1c2d3-e4f5-4678-9abc-def012345678"
        try {
            TailFirstE2EProductBridge.configureDebugIdentity(id, 1)
            assertEquals(TailFirstE2EProductBridge.DebugIdentity(id, 1), TailFirstE2EProductBridge.debugIdentity())
            TailFirstE2EProductBridge.configureDebugIdentity(id, 2)
            assertEquals(TailFirstE2EProductBridge.DebugIdentity(id, 2), TailFirstE2EProductBridge.debugIdentity())
            TailFirstE2EProductBridge.configureDebugIdentity(id, null)
            assertEquals(null, TailFirstE2EProductBridge.debugIdentity())
            TailFirstE2EProductBridge.configureDebugIdentity(null, 1)
            assertEquals(null, TailFirstE2EProductBridge.debugIdentity())
            TailFirstE2EProductBridge.configureDebugIdentity(id.uppercase(), 1)
            assertEquals(null, TailFirstE2EProductBridge.debugIdentity())
            TailFirstE2EProductBridge.configureDebugIdentity(id, 1)
            TailFirstE2EProductBridge.unregister()
            assertEquals(null, TailFirstE2EProductBridge.debugIdentity())
        } finally {
            TailFirstE2EProductBridge.unregister()
        }
    }

    @Test
    fun closedViewHookKeepsOnlyTheSelectedSessionUntilItIsReopened() {
        val selected = java.util.UUID.randomUUID()
        val other = java.util.UUID.randomUUID()
        try {
            TailFirstE2EProductBridge.selectSession(selected)
            assertThrows(IllegalStateException::class.java) {
                TailFirstE2EProductBridge.markSelectedViewClosed(other)
            }
            TailFirstE2EProductBridge.markSelectedViewClosed(selected)
            assertEquals(selected, TailFirstE2EProductBridge.closedSelectedSession())
            TailFirstE2EProductBridge.markSelectedViewReopened(selected)
            assertEquals(null, TailFirstE2EProductBridge.closedSelectedSession())
            TailFirstE2EProductBridge.markSelectedViewClosed(selected)
            TailFirstE2EProductBridge.selectSession(other)
            assertEquals(null, TailFirstE2EProductBridge.closedSelectedSession())
        } finally {
            TailFirstE2EProductBridge.unregister()
        }
    }

    @Test
    fun closedViewResponseDropsUntrustedFieldsAndRequiresClosureProof() {
        val receiver = TailFirstE2EReceiver()
        val sessionId = java.util.UUID.randomUUID()
        val sanitized = receiver.sanitizeClosedViewState(
            Json.parseToJsonElement("""{"session_id":"$sessionId","view_closed":true,"session_running":true,"terminal_semantics":{}}""")
                .jsonObject,
        )
        assertEquals(setOf("session_id", "view_closed", "session_running"), sanitized.keys)
        assertEquals(sessionId.toString(), sanitized.getValue("session_id").jsonPrimitive.content)
        assertThrows(IllegalStateException::class.java) {
            receiver.sanitizeClosedViewState(
                Json.parseToJsonElement("""{"session_id":"$sessionId","view_closed":false,"session_running":true}""")
                    .jsonObject,
            )
        }
        assertThrows(IllegalStateException::class.java) {
            receiver.sanitizeClosedViewState(
                Json.parseToJsonElement("""{"session_id":"arbitrary","view_closed":true,"session_running":true}""")
                    .jsonObject,
            )
        }
    }

    @Test
    fun armedReselectRequiresThisRunRecentNonBaselineSession() {
        val sessionId = java.util.UUID.randomUUID()
        val now = 1_000_000L
        fun arm(run: String = "this-run", timestamp: Long = now,
                session: String = sessionId.toString(), baseline: String = "[]") =
            Json.parseToJsonElement("""{"schema_version":1,"operation":"open_session","run_id":"$run","armed":true,"armed_at_epoch_millis":$timestamp,"session_id":"$session","baseline_session_ids":$baseline}""")
                .jsonObject
        assertEquals(sessionId, armedSessionId(arm(), "this-run", now))
        assertEquals(null, armedSessionId(arm(run = "other-run"), "this-run", now))
        assertEquals(null, armedSessionId(arm(timestamp = now - 30 * 60_000L - 1), "this-run", now))
        assertEquals(null, armedSessionId(arm(baseline = "[\"$sessionId\"]"), "this-run", now))
        assertEquals(null, armedSessionId(arm(session = "not-a-uuid"), "this-run", now))
        assertEquals(null, armedSessionId(arm(session = java.util.UUID(0, 0).toString()), "this-run", now))
    }

    @Test
    fun armedReselectRejectsOtherAccountAndOfflineSessions() {
        val pcId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        val pc = PcSummary(pcId, UUID.randomUUID(), "Test PC", "test", remoteRoutingEnabled = true,
            online = true, lastSeenAt = Instant.now())
        val session = SessionSummary(sessionId, pcId, "powershell-7", null, SessionState.RUNNING,
            80, 24, 0uL, Instant.now(), null, online = true)
        val active = ProductSnapshot(signedIn = true,
            authenticationState = ProductAuthenticationState.AUTHENTICATED,
            sessions = listOf(session), pcs = listOf(pc))
        assertTrue(tailFirstArmedSessionEligible(active, sessionId))
        assertFalse(tailFirstArmedSessionEligible(active.copy(signedIn = false), sessionId))
        assertFalse(tailFirstArmedSessionEligible(active.copy(sessions = emptyList()), sessionId))
        assertFalse(tailFirstArmedSessionEligible(active.copy(pcs = emptyList()), sessionId))
        assertFalse(tailFirstArmedSessionEligible(active.copy(pcs = listOf(pc.copy(online = false))), sessionId))
        assertFalse(tailFirstArmedSessionEligible(active.copy(sessions = listOf(session.copy(online = false))), sessionId))
    }

    @Test
    fun clipboardCheckerNeedsFocusedAttachedUnexpiredControl() {
        val pcId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        val now = 1_000_000L
        val pc = PcSummary(pcId, UUID.randomUUID(), "Test PC", "test", remoteRoutingEnabled = true,
            online = true, lastSeenAt = Instant.now())
        val session = SessionSummary(sessionId, pcId, "powershell-7", null, SessionState.RUNNING,
            80, 24, 0uL, Instant.now(), null, online = true)
        val active = ProductSnapshot(signedIn = true,
            authenticationState = ProductAuthenticationState.AUTHENTICATED,
            sessions = listOf(session), pcs = listOf(pc),
            sessionStates = mapOf(sessionId to RemoteSessionState.Controlling(0, "lease", now + 1000)))
        fun eligible(state: ProductSnapshot = active, focused: Boolean = true,
                     selected: UUID? = sessionId, surface: Boolean = true) =
            tailFirstClipboardFixtureEligible(state, sessionId, focused, selected, surface, now)
        assertTrue(eligible())
        assertFalse(eligible(focused = false))
        assertFalse(eligible(selected = UUID.randomUUID()))
        assertFalse(eligible(surface = false))
        assertFalse(eligible(active.copy(sessionStates = emptyMap())))
        assertFalse(eligible(active.copy(sessionStates = mapOf(sessionId to
            RemoteSessionState.Controlling(0, "lease", now)))))
        assertFalse(eligible(active.copy(sessions = listOf(session.copy(online = false)))))
    }

    @Test
    fun inspectDiagnosticsExposeOnlyValidatedCountersAndHashes() {
        val receiver = TailFirstE2EReceiver()
        val digest = "a".repeat(64)
        val requestId = UUID.randomUUID().toString()
        val transfer = """"delta_chunks_accepted":2,"delta_duplicate_chunks":1,"delta_chunk_acks_sent":3,"broker_chunks_accepted":1,"broker_duplicate_chunks":0,"broker_chunk_acks_sent":1,"delta_request_id":"$requestId""""
        val restore = receiver.sanitizeRestoreDiagnostics(
            Json.parseToJsonElement("""{"attempt":3,"delta_request_count":1,"delta_install_count":1,"delta_expected_chunk_count":2,"broker_resolved_count":4,"full_snapshot_install_count":1,"full_snapshot_sequence":7,"full_snapshot_sha256":"$digest","base_snapshot_sequence":7,"base_sha256":"$digest","private_text":"secret","future_unknown":true,$transfer}""")
                .jsonObject,
        )
        assertFalse("private_text" in restore)
        assertFalse("future_unknown" in restore)
        assertEquals(digest, restore.getValue("base_sha256").jsonPrimitive.content)
        assertEquals(1, restore.getValue("full_snapshot_install_count").jsonPrimitive.int)
        assertEquals("7", restore.getValue("full_snapshot_sequence").jsonPrimitive.content)
        assertEquals(digest, restore.getValue("full_snapshot_sha256").jsonPrimitive.content)
        assertEquals(requestId, restore.getValue("delta_request_id").jsonPrimitive.content)
        assertEquals("3", restore.getValue("delta_chunk_acks_sent").jsonPrimitive.content)
        assertThrows(IllegalStateException::class.java) {
            receiver.sanitizeRestoreDiagnostics(Json.parseToJsonElement(
                restore.toString().replace(requestId, "1-1-1-1-1"),
            ).jsonObject)
        }
        assertThrows(IllegalStateException::class.java) {
            receiver.sanitizeRestoreDiagnostics(
                Json.parseToJsonElement("""{"attempt":3,"delta_request_count":1,"delta_install_count":1,"delta_expected_chunk_count":2,"broker_resolved_count":4,"base_snapshot_sequence":7,$transfer}""")
                    .jsonObject,
            )
        }
        assertThrows(IllegalStateException::class.java) {
            receiver.sanitizeRestoreDiagnostics(
                Json.parseToJsonElement("""{"attempt":3,"delta_request_count":0,"delta_install_count":0,"delta_expected_chunk_count":0,"broker_resolved_count":0,"full_snapshot_install_count":1,"full_snapshot_sequence":7}""")
                    .jsonObject,
            )
        }
        val frozen = receiver.sanitizeFrozenHistoryState(
            Json.parseToJsonElement("""{"active":true,"capture_id":1,"revision":2,"page_count":1,"first_line_id":3,"last_line_id":4,"first_page_first_logical_row":0,"first_page_last_logical_row":5,"loaded_state_sha256":"$digest","raw_payload":"secret"}""")
                .jsonObject,
        )
        assertFalse("raw_payload" in frozen)
        assertEquals(digest, frozen.getValue("loaded_state_sha256").jsonPrimitive.content)
    }

    @Test
    fun frozenStateRequiresAndSanitizesHeadTruncatedBoolean() {
        val receiver = TailFirstE2EReceiver()
        fun source(headTruncated: String) = Json.parseToJsonElement(
            """{"frozen_active":true,"frozen_render_ready":true,"frozen_page_count":1,"frozen_capture_id":1,"frozen_revision":2,"frozen_snapshot_sequence_s":1,"frozen_first_line_id":3,"frozen_last_line_id":4,"frozen_next_before_line_id":2,"frozen_has_more_before":true,"frozen_head_truncated":$headTruncated,"private_payload":"secret"}""",
        ).jsonObject

        val sanitized = receiver.sanitizeFrozenState(source("true"))
        assertEquals(true, sanitized.getValue("frozen_head_truncated").jsonPrimitive.boolean)
        assertFalse("private_payload" in sanitized)
        assertFalse(receiver.sanitizeFrozenState(source("false"))
            .getValue("frozen_head_truncated").jsonPrimitive.boolean)
        assertThrows(IllegalStateException::class.java) {
            receiver.sanitizeFrozenState(JsonObject(source("true") - "frozen_head_truncated"))
        }
        assertThrows(IllegalStateException::class.java) {
            receiver.sanitizeFrozenState(source("\"true\""))
        }
    }

    @Test
    fun debugEnablesAuthoritativeHostSnapshotRecovery() {
        assertTrue(BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME)
    }

    @Test
    fun directDataPlaneCanRunOnlyInAnIsolatedPrivateNetworkDebugBuild() {
        if (!BuildConfig.TERMLINK_DIRECT_TRANSPORT_DATA_PLANE) return
        val host = URI(BuildConfig.TERMLINK_SERVER_URL).host.lowercase()
        val octets = host.split('.').mapNotNull(String::toIntOrNull)
        val privateHost = host == "localhost" || host.endsWith(".localhost") || host == "::1" ||
            octets.size == 4 && octets.all { it in 0..255 } && (
                octets[0] == 10 || octets[0] == 127 ||
                    octets[0] == 192 && octets[1] == 168 ||
                    octets[0] == 172 && octets[1] in 16..31
                )
        assertTrue(BuildConfig.TERMLINK_DIRECT_TRANSPORT_SHADOW)
        assertTrue(privateHost)
        assertTrue(BuildConfig.APPLICATION_ID != "dev.termlink.harness")
    }

    @Test
    fun liveMarkerIsUniqueAndCannotHardWrapAtTheMinimumTerminalWidth() {
        val runId = "post-upgrade-0.1.37-streamed-20260829-r13-frame-capture-visible"
        val marker = tailFirstLiveMarker(runId)

        assertEquals("TLTF_LIVE_b16944a3f41ea46b", marker)
        assertTrue(marker.length < MINIMUM_TERMINAL_COLUMNS)
        assertTrue(marker.all { it in 'a'..'f' || it in '0'..'9' || it == '_' || it in 'A'..'Z' })
        assertFalse(marker == tailFirstLiveMarker("$runId-different"))
    }

    @Test
    fun inspectAcceptsOnlyTheThreeMarkersDerivedFromItsRun() {
        val runId = "frozen-contract-run"
        val digest = tailFirstLiveMarker(runId).removePrefix("TLTF_LIVE_")
        val live = "TLTF_LIVE_$digest"
        val width = "TLF_COL32_${digest.take(8)}"
        val frozen = "TLTF_FROZEN_$digest"
        assertEquals("TLTF32_${digest.take(8)}", frozenLifecycleMarkerStem(runId))
        for (marker in listOf(live, width, frozen)) {
            assertTrue(tailFirstInspectMarkerIsValid(runId, marker))
            assertFalse(tailFirstInspectMarkerIsValid("$runId-other", marker))
        }
        for (spoof in listOf(null, "", "TLF_COL32_00000000", "TLTF_FROZEN_${digest.take(8)}",
            "TLTF_LIVE_${digest.uppercase()}", "${frozen}x", "arbitrary")) {
            assertFalse(tailFirstInspectMarkerIsValid(runId, spoof))
        }
    }

    @Test
    fun frozenLifecycleOpenSessionUsesBoundedHashedFixtureOnly() {
        val receiver = TailFirstE2EReceiver()
        val runId = "frozen-contract-run"
        val stem = frozenLifecycleMarkerStem(runId)
        assertEquals("TLTF32_cef20f88", stem)
        val valid = mapOf(
            "first_marker" to "${stem}_0001",
            "last_marker" to "${stem}_1400",
            "marker_count" to "1400",
            "minimum_tail" to "0",
            "history_bootstrap_mode" to "SnapshotTranscript",
            "frozen_lifecycle_acceptance" to "true",
        )
        assertTrue(receiver.frozenLifecycleOpenSessionArgumentsAreValid(runId, valid))
        val live = tailFirstLiveMarker(runId)
        assertTrue(receiver.frozenLifecycleOpenSessionArgumentsAreValid(runId, valid + ("live_marker" to live)))
        assertFalse(receiver.frozenLifecycleOpenSessionArgumentsAreValid(
            runId,
            valid + ("live_marker" to live + "0"),
        ))
        assertFalse(receiver.frozenLifecycleOpenSessionArgumentsAreValid(
            runId,
            valid + ("frozen_lifecycle_acceptance" to "false"),
        ))
        assertFalse(receiver.frozenLifecycleOpenSessionArgumentsAreValid(
            runId,
            valid + ("marker_count" to "1399"),
        ))
        assertFalse(receiver.frozenLifecycleOpenSessionArgumentsAreValid(
            runId,
            valid + ("last_marker" to "${stem}_1401"),
        ))
        assertFalse(receiver.frozenLifecycleOpenSessionArgumentsAreValid(
            runId,
            valid + ("history_bootstrap_mode" to "StreamedChunkPrefix"),
        ))
        assertFalse(receiver.frozenLifecycleOpenSessionArgumentsAreValid(
            "unsafe/run",
            valid,
        ))
        assertFalse(receiver.frozenLifecycleOpenSessionArgumentsAreValid(
            runId,
            valid + ("unknown" to "value"),
        ))
    }

    @Test
    fun standardOpenSessionAcceptsAnOptionalRunBoundLiveMarkerOnly() {
        val receiver = TailFirstE2EReceiver()
        val runId = "shell-live-marker-contract"
        val stem = "TLTF_${runId.replace('-', '_').replace('.', '_')}"
        val valid = mapOf(
            "first_marker" to "${stem}_0001",
            "last_marker" to "${stem}_5200",
            "marker_count" to "5200",
            "minimum_tail" to "0",
            "history_bootstrap_mode" to "SnapshotTranscript",
        )
        assertTrue(receiver.openSessionArgumentsAreValid(runId, valid))
        assertTrue(receiver.openSessionArgumentsAreValid(
            runId,
            valid + ("live_marker" to tailFirstLiveMarker(runId)),
        ))
        assertFalse(receiver.openSessionArgumentsAreValid(
            runId,
            valid + ("live_marker" to tailFirstLiveMarker("other-run")),
        ))
        assertFalse(receiver.openSessionArgumentsAreValid(
            runId,
            valid + ("unknown" to "value"),
        ))
    }

    @Test
    fun generatedDebugAssetMatchesTheRunnerContractAndBuildConfig() {
        val appRoot = locateAppRoot()
        val generatedContract = locateGeneratedDebugContract(appRoot)
        val contract = Json.parseToJsonElement(
            readText(generatedContract),
        ).jsonObject

        assertFalse(Files.exists(appRoot.resolve("src/debug/assets/$CONTRACT_ASSET")))
        assertTrue(generatedContract.toString().replace('\\', '/').contains("generateDebugTailFirstE2EContract"))
        assertEquals(1, contract.getValue("schema_version").jsonPrimitive.int)
        assertEquals("explicit_broadcast_run_as_file", contract.string("transport"))
        assertEquals(BuildConfig.APPLICATION_ID, contract.string("package_name"))
        assertEquals(RECEIVER_CLASS, contract.string("receiver_class"))
        assertEquals(ACTION, contract.string("broadcast_action"))
        assertEquals("no_backup/tail-first-e2e/response.json", contract.string("response_relative_path"))
        assertEquals(BuildConfig.TERMLINK_SERVER_URL, contract.string("server_origin"))
        assertEquals(
            BuildConfig.TERMLINK_TERMINAL_SNAPSHOT_RUNTIME,
            contract.getValue("terminal_snapshot_runtime").jsonPrimitive.boolean,
        )
        assertEquals(
            OPERATIONS,
            contract.getValue("operations").jsonArray.map { it.jsonPrimitive.content },
        )
    }

    private fun locateAppRoot(): Path {
        val workingDirectory = Paths.get(System.getProperty("user.dir")).toAbsolutePath()
        return listOf(
            workingDirectory,
            workingDirectory.resolve("app"),
            workingDirectory.resolve("android/app"),
        ).firstOrNull { Files.isRegularFile(it.resolve("build.gradle.kts")) && Files.isDirectory(it.resolve("src/main")) }
            ?: error("Android app root was not found from $workingDirectory")
    }

    private fun locateGeneratedDebugContract(appRoot: Path): Path {
        val generatedAssets = appRoot.resolve("build/generated/assets")
        assertTrue("Generated Debug assets are unavailable at $generatedAssets", Files.isDirectory(generatedAssets))
        return Files.walk(generatedAssets).use { paths ->
            paths
                .filter { Files.isRegularFile(it) }
                .filter { it.fileName.toString() == CONTRACT_ASSET }
                .filter { it.toString().replace('\\', '/').contains("generateDebugTailFirstE2EContract") }
                .findFirst()
                .orElseThrow { AssertionError("Generated Debug contract was not found below $generatedAssets") }
        }
    }

    private fun readText(path: Path): String = Files.newBufferedReader(path).use { it.readText() }

    private fun kotlinx.serialization.json.JsonObject.string(name: String): String =
        getValue(name).jsonPrimitive.content

    private companion object {
        const val CONTRACT_ASSET = "termlink-tail-first-e2e-contract-v1.json"
        const val RECEIVER_CLASS = "dev.termlink.product.debug.TailFirstE2EReceiver"
        const val ACTION = "dev.termlink.product.debug.TAIL_FIRST_E2E"
        const val MINIMUM_TERMINAL_COLUMNS = 80
        val OPERATIONS = listOf(
            "probe",
            "open_session",
            "close_view",
            "reopen_selected",
            "reselect_armed_session",
            "inspect",
            "page_up",
            "open_history",
            "close_history",
            "show_ime",
            "begin_frozen_history",
            "page_frozen_history",
            "release_frozen_history",
        )
    }
}
