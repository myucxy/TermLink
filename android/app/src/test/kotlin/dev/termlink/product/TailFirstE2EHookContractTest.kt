package dev.termlink.product

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TailFirstE2EHookContractTest {
    @Test
    fun debugHookOpenSurfacesGetEnoughTimeForTheStressFixture() {
        assertEquals(150_000L, tailFirstOpenSurfaceTimeoutMillis(null))
        assertEquals(600_000L, tailFirstOpenSurfaceTimeoutMillis("true"))
        listOf("false", "FALSE", "0").forEach {
            assertEquals(150_000L, tailFirstOpenSurfaceTimeoutMillis(it))
        }
    }

    @Test
    fun openMarkerReadinessKeepsTheLiveTailFenceOptionalForOlderHooks() {
        val last = "TLTF_run_5200"
        val live = "TLTF_LIVE_deadbeef00112233"

        assertFalse(tailFirstOpenMarkersReady("$last\n", last, live))
        assertTrue(tailFirstOpenMarkersReady("$last\n$live", last, live))
        assertTrue(tailFirstOpenMarkersReady(live, last, live))
        assertTrue(tailFirstOpenMarkersReady(last, last, null))
        assertTrue(tailFirstOpenMarkersReady("anything", null, null))
 
        // A 32-column row can split a short marker; line wrapping is not marker content.
        val wrappedLast = "TLTF\n_run\n_5200"
        val wrappedLive = "TLTF\n_LIVE\n_deadbeef00112233"
        assertTrue(tailFirstTextContainsMarker(wrappedLast, last))
        assertTrue(tailFirstTextContainsMarker(wrappedLive, live))
        assertTrue(tailFirstOpenMarkersReady("$wrappedLast\n$wrappedLive", last, live))
        assertFalse(tailFirstTextContainsMarker("TLTF_run_520", last))
    }

    @Test
    fun receiverIsExportedOnlyByTheDebugManifestForTheExplicitAction() {
        val appRoot = locateAppRoot()
        val debugManifest = parseXml(appRoot.resolve("src/debug/AndroidManifest.xml"))
        val receivers = debugManifest.getElementsByTagName("receiver")
        val receiver = (0 until receivers.length)
            .map(receivers::item)
            .single { it.attributes.getNamedItemNS(ANDROID_NAMESPACE, "name").nodeValue == RECEIVER_CLASS }

        assertEquals("true", receiver.attributes.getNamedItemNS(ANDROID_NAMESPACE, "exported").nodeValue)
        val receiverActions = receiver.childNodes.asSequence()
            .flatMap { it.childNodes.asSequence() }
            .filter { it.nodeName == "action" }
            .map { it.attributes.getNamedItemNS(ANDROID_NAMESPACE, "name").nodeValue }
            .toList()
        assertEquals(listOf(ACTION), receiverActions)

        assertFalse(readText(appRoot.resolve("src/main/AndroidManifest.xml")).contains(RECEIVER_CLASS))
        assertFalse(Files.exists(appRoot.resolve("src/main/assets/$CONTRACT_ASSET")))
        assertFalse(Files.exists(appRoot.resolve("src/release/assets/$CONTRACT_ASSET")))
        assertFalse(Files.exists(appRoot.resolve("src/main/kotlin/${RECEIVER_CLASS.replace('.', '/')}.kt")))
        assertFalse(Files.exists(appRoot.resolve("src/release/kotlin/${RECEIVER_CLASS.replace('.', '/')}.kt")))
    }

    @Test
    fun receiverFailsClosedAndUsesAtomicNoBackupStorage() {
        val receiver = readText(
            locateAppRoot().resolve("src/debug/kotlin/${RECEIVER_CLASS.replace('.', '/')}.kt"),
        )

        assertTrue(receiver.contains("component == expected"))
        assertTrue(receiver.contains("Regex(\"^[0-9a-f]{32}$\")"))
        assertTrue(receiver.contains("Regex(\"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$\")"))
        assertTrue(receiver.contains("context.noBackupFilesDir"))
        assertTrue(receiver.contains("AtomicFile"))
        assertTrue(receiver.contains("finishWrite"))
        assertTrue(receiver.contains("TailFirstE2EProductBridge.execute"))
        assertTrue(receiver.contains("request.operation == OPERATION_OPEN_SESSION"))
        assertTrue(receiver.contains("withTimeout(BRIDGE_TIMEOUT_MILLIS) { executeBridge() }"))
        assertTrue(receiver.contains("history length must not inherit the short-operation timeout"))
        assertTrue(receiver.contains("an arbitrary history restore can legitimately outlive Android's"))
        assertTrue(receiver.contains("context.applicationContext"))
        assertTrue(receiver.contains("receiverScope.launch(start = CoroutineStart.UNDISPATCHED)"))
        assertFalse(receiver.contains("goAsync()"))
        assertFalse(receiver.contains("pendingResult"))
        assertTrue(receiver.contains("OPERATION_FAILED"))
        assertTrue(receiver.contains("OPERATION_OPEN_HISTORY ->"))
        assertTrue(receiver.contains("positiveInt(\"terminal_columns\")"))
        assertTrue(receiver.contains("positiveInt(\"terminal_rows\")"))
        assertFalse(receiver.contains("exception.message"))
        assertFalse(receiver.contains("stackTraceToString"))
    }

    @Test
    fun productStateResponseContainsRequiredFieldsAndNoSensitiveFieldNames() {
        val productSource = readText(
            locateAppRoot().resolve("src/main/kotlin/dev/termlink/product/ProductActivity.kt"),
        )
        val stateResponse = productSource
            .substringAfter("private suspend fun tailFirstStateResponse(")
            .substringBefore("private fun tailFirstBoundsJson(")
        val emittedNames = Regex("""put\(\"([^\"]+)\"""")
            .findAll(stateResponse)
            .map { it.groupValues[1] }
            .toSet()
        val productEmittedNames = Regex("""put\(\"([^\"]+)\"""")
            .findAll(productSource)
            .map { it.groupValues[1] }
            .toSet()

        assertTrue(emittedNames.containsAll(STATE_RESPONSE_FIELDS))
        assertTrue(PAGE_RESPONSE_FIELDS.all { "\"$it\" to" in productSource })
        assertTrue(emittedNames.containsAll(IME_RESPONSE_FIELDS))
        assertTrue((productEmittedNames + PAGE_RESPONSE_FIELDS).none(FORBIDDEN_FIELD_NAME::containsMatchIn))
    }

    @Test
    fun productBridgeRefreshesForANewSessionAndOpensHistoryIdempotently() {
        val productSource = readText(
            locateAppRoot().resolve("src/main/kotlin/dev/termlink/product/ProductActivity.kt"),
        )
        val sessionWait = productSource
            .substringAfter("private suspend fun awaitNewTailFirstSession(")
            .substringBefore("private fun tailFirstProbeResponse(")
        val openHistory = productSource
            .substringAfter("\"open_history\" -> {")
            .substringBefore("\"page_up\" -> {")

        assertTrue(sessionWait.contains("repository.refresh()"))
        assertTrue(sessionWait.contains("existingSessionIds"))
        assertTrue(sessionWait.contains("newestNewNonExitedSession"))
        assertTrue(openHistory.contains("if (historyLoadTriggered) awaitTranscriptChange"))
        assertTrue(openHistory.contains("awaitTailFirstSurface(sessionId) { it.observe().historyOverlay }"))
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

    private fun parseXml(path: Path) = DocumentBuilderFactory.newInstance().run {
        isNamespaceAware = true
        newDocumentBuilder().parse(path.toFile())
    }

    private fun readText(path: Path): String = Files.newBufferedReader(path).use { it.readText() }

    private fun org.w3c.dom.NodeList.asSequence(): Sequence<org.w3c.dom.Node> =
        (0 until length).asSequence().map(::item)

    private companion object {
        const val ANDROID_NAMESPACE = "http://schemas.android.com/apk/res/android"
        const val CONTRACT_ASSET = "termlink-tail-first-e2e-contract-v1.json"
        const val RECEIVER_CLASS = "dev.termlink.product.debug.TailFirstE2EReceiver"
        const val ACTION = "dev.termlink.product.debug.TAIL_FIRST_E2E"
        val STATE_RESPONSE_FIELDS = setOf(
            "schema_version",
            "request_id",
            "operation",
            "ok",
            "session_id",
            "snapshot_count",
            "transcript_count",
            "chunk_count",
            "snapshot_sequence_s",
            "live_ack",
            "terminal_columns",
            "terminal_rows",
            "history_overlay",
            "first_marker_visible",
            "last_marker_visible",
            "live_marker_visible",
            "viewport_signature",
        )
        val PAGE_RESPONSE_FIELDS = setOf("pages_moved", "rows_loaded")
        val IME_RESPONSE_FIELDS = setOf(
            "ime_visible",
            "display_bounds",
            "terminal_bounds",
            "bottom_bar_bounds",
            "ime_bounds",
        )
        val FORBIDDEN_FIELD_NAME = Regex(
            "token|password|secret|authorization|cookie|private|credential|key_material",
            RegexOption.IGNORE_CASE,
        )
    }
}
