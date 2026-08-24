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
        assertTrue(receiver.contains("withTimeout(BRIDGE_TIMEOUT_MILLIS)"))
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
