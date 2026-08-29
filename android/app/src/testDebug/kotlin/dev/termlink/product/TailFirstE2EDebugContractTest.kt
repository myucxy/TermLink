package dev.termlink.product

import dev.termlink.harness.BuildConfig
import dev.termlink.product.debug.tailFirstLiveMarker
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TailFirstE2EDebugContractTest {
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
        assertEquals(PACKAGE_NAME, contract.string("package_name"))
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
        const val PACKAGE_NAME = "dev.termlink.harness"
        const val RECEIVER_CLASS = "dev.termlink.product.debug.TailFirstE2EReceiver"
        const val ACTION = "dev.termlink.product.debug.TAIL_FIRST_E2E"
        const val MINIMUM_TERMINAL_COLUMNS = 80
        val OPERATIONS = listOf(
            "probe",
            "open_session",
            "inspect",
            "page_up",
            "open_history",
            "close_history",
            "show_ime",
        )
    }
}
