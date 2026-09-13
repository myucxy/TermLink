package dev.termlink.product

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import javax.xml.parsers.DocumentBuilderFactory
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectProductE2EContractTest {
    @Test
    fun receiverAndContractRemainDebugAcceptanceOnly() {
        val appRoot = locateAppRoot()
        val buildScript = readText(appRoot.resolve("build.gradle.kts"))
        val debugManifest = parseXml(appRoot.resolve("src/debug/AndroidManifest.xml"))
        val receiver = debugManifest.getElementsByTagName("receiver").asSequence()
            .single { it.attributes.getNamedItemNS(ANDROID_NAMESPACE, "name").nodeValue == RECEIVER_CLASS }

        assertEquals("true", receiver.attributes.getNamedItemNS(ANDROID_NAMESPACE, "exported").nodeValue)
        assertEquals(
            "${'$'}{termlinkDirectProductE2EEnabled}",
            receiver.attributes.getNamedItemNS(ANDROID_NAMESPACE, "enabled").nodeValue,
        )
        assertEquals(
            listOf(ACTION),
            receiver.childNodes.asSequence()
                .flatMap { it.childNodes.asSequence() }
                .filter { it.nodeName == "action" }
                .map { it.attributes.getNamedItemNS(ANDROID_NAMESPACE, "name").nodeValue }
                .toList(),
        )
        assertFalse(readText(appRoot.resolve("src/main/AndroidManifest.xml")).contains(RECEIVER_CLASS))
        assertFalse(Files.exists(appRoot.resolve("src/main/assets/$CONTRACT_ASSET")))
        assertFalse(Files.exists(appRoot.resolve("src/release/assets/$CONTRACT_ASSET")))
        assertTrue(buildScript.contains("if (debugDirectTransportDataPlane)"))
        assertTrue(buildScript.contains("manifestPlaceholders[\"termlinkDirectProductE2EEnabled\"] = \"false\""))
        assertTrue(buildScript.contains("debugDirectTransportDataPlane.toString()"))
        assertTrue(buildScript.contains("applicationId.set(variant.applicationId)"))
        assertTrue(buildScript.contains("serverOrigin.set(debugServerUrl)"))
        assertTrue(buildScript.contains("selector().withBuildType(\"debug\")"))
        assertTrue(buildScript.contains("directTransportShadow.set(debugDirectTransportShadow)"))
        assertTrue(buildScript.contains("directTransportDataPlane.set(debugDirectTransportDataPlane)"))
        assertFalse(buildScript.contains("withBuildType(\"release\")"))
    }

    @Test
    fun generatedContractDeclaresEveryRuntimeBoundaryWithoutSensitiveFields() {
        val source = readText(locateAppRoot().resolve("build.gradle.kts"))
            .substringAfter("abstract class GenerateDirectProductE2EContract")
            .substringBefore("val requestedDebugServerUrlProperty")

        for (operation in OPERATIONS) assertTrue("missing $operation", source.contains("\"$operation\""))
        for (diagnostic in DIAGNOSTICS) assertTrue("missing $diagnostic", source.contains("\"$diagnostic\""))
        for (capability in SERVER_CAPABILITIES) assertTrue("missing $capability", source.contains("\"$capability\""))
        assertTrue(source.contains("\"application_id\": ${'$'}{jsonString(applicationId.get())}"))
        assertTrue(source.contains("\"server_origin\": ${'$'}{jsonString(serverOrigin.get())}"))
        assertTrue(source.contains("\"build_type\": \"debug\""))
        assertTrue(FORBIDDEN_FIELD.none(source::contains))
    }

    @Test
    fun receiverIsExplicitBoundedAtomicAndFailsClosedWithoutAProductBridge() {
        val source = readText(
            locateAppRoot().resolve("src/debug/kotlin/${RECEIVER_CLASS.replace('.', '/')}.kt"),
        )
        assertTrue(source.contains("intent.component != ComponentName"))
        assertTrue(source.contains("extras.keySet() != requiredKeys"))
        assertTrue(source.contains("context.noBackupFilesDir"))
        assertTrue(source.contains("AtomicFile"))
        assertTrue(source.contains("MAX_RESPONSE_BYTES"))
        assertTrue(source.contains("PRODUCT_DIRECT_BRIDGE_UNAVAILABLE"))
        assertTrue(source.contains("ready = false"))
        assertTrue(source.contains("DirectProductE2EOperationResult.blocked(FAILURE_BRIDGE_UNAVAILABLE)"))
        assertFalse(source.contains("exception.message"))
        assertFalse(source.contains("stackTraceToString"))
    }

    @Test
    fun productBridgeReturnsTheTransientSessionIdentityRequiredByTheRunner() {
        val repositoryRoot = locateAppRoot().parent.parent
        val bridge = readText(
            locateAppRoot().resolve("src/main/kotlin/dev/termlink/product/ProductionDirectProductE2EBridge.kt"),
        )
        val runner = readText(
            repositoryRoot.resolve("technical-validation/direct-e2e/product/Invoke-DirectProductE2E.ps1"),
        )

        assertTrue(bridge.contains("put(\"session_id\", sessionId.toString())"))
        assertTrue(bridge.contains("put(\"session_role\", \"late\")"))
        assertTrue(bridge.contains("put(\"peer_device_id\", pc.deviceId.toString())"))
        assertTrue(bridge.contains("put(\"peer_device_id\", state.peerDeviceId.toString())"))
        assertTrue(runner.contains("Assert-RequiredProperties ${'$'}prepared @('session_id', 'peer_device_id')"))
        assertTrue(runner.contains("Assert-RequiredProperties ${'$'}lateSession @('session_id', 'session_role')"))
    }

    private fun locateAppRoot(): Path {
        val workingDirectory = Paths.get(System.getProperty("user.dir")).toAbsolutePath()
        return listOf(workingDirectory, workingDirectory.resolve("app"), workingDirectory.resolve("android/app"))
            .firstOrNull { Files.isRegularFile(it.resolve("build.gradle.kts")) && Files.isDirectory(it.resolve("src/main")) }
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
        const val CONTRACT_ASSET = "termlink-direct-product-e2e-contract-v1.json"
        const val RECEIVER_CLASS = "dev.termlink.product.debug.DirectProductE2EReceiver"
        const val ACTION = "dev.termlink.product.debug.DIRECT_PRODUCT_E2E"
        val OPERATIONS = setOf(
            "probe", "inspect_transport", "send_text_marker", "send_key", "request_resize",
            "background", "foreground", "lock_screen", "wake_screen", "open_late_session",
            "start_long_output", "stop_long_output", "prepare_session", "close_test_sessions",
        )
        val DIAGNOSTICS = setOf(
            "app_process_id", "app_process_start_time", "candidate_kinds", "carrier",
            "contiguous_applied_sequence", "digest_conflict_count", "duplicate_apply_count",
            "duplicate_drop_count", "durable_ack", "fallback_reason", "gap_count",
            "last_received_sequence", "marker_digest", "network_generation", "path_epoch",
            "relay_generation", "signalling_generation", "transport_receipt_high_watermark",
            "viewport_digest",
        )
        val SERVER_CAPABILITIES = setOf(
            "direct_transport_shadow_v1", "direct_transport_shadow_multipair_v2",
            "direct_transport_data_v1", "direct_session_cutover_v1",
        )
        val FORBIDDEN_FIELD = setOf(
            "access_token", "refresh_token", "password", "authorization", "cookie", "private_key",
        )
    }
}
