package dev.termlink.product

import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.click
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.test.SemanticsNodeInteraction
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.text.TextLayoutResult
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.harness.R
import dev.termlink.model.*
import dev.termlink.product.debug.AdaptiveLayoutAcceptanceActivity
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.channels.Channel
import org.junit.Assert.assertEquals
import java.io.IOException
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class RemoteAgentUpdateInstrumentedTest {
    @get:Rule val compose = createAndroidComposeRule<AdaptiveLayoutAcceptanceActivity>()
    private var installations = 0

    private fun capture(name: String) {
        compose.waitForIdle()
        androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().waitForIdleSync()
        // The headless emulator compositor can present after Compose semantics settle.
        Thread.sleep(300)
        val screenshot = androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot()
        java.io.File(compose.activity.cacheDir, "$name.png").outputStream().use {
            screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it)
        }
        screenshot.recycle()
    }

    private fun showDevice(platform: String = "windows"): PcSummary {
        val pc = PcSummary(UUID.randomUUID(), UUID.randomUUID(), "Remote update test PC", "0.3.8.138",
            remoteRoutingEnabled = true, online = true, lastSeenAt = Instant.now(), platform = platform,
            capabilities = setOf(PcCapabilityIds.REMOTE_AGENT_UPDATE_V1))
        val state = MutableStateFlow(ProductSnapshot(signedIn = true, configured = true,
            serverUrl = "https://termlink2.kget.cc:8910", connectionState = ProductConnectionState.CONNECTED,
            authenticationState = ProductAuthenticationState.AUTHENTICATED,
            devices = listOf(DeviceSummary(pc.deviceId, DeviceType.WINDOWS_PC, pc.name, "fixture", ByteArray(32),
                trustState = DeviceTrustState.TRUSTED, createdAt = Instant.now())), pcs = listOf(pc)))
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = state
            override suspend fun refresh() = Unit
            override suspend fun activeAgentUpdate(pcId: UUID): AgentUpdateProgress? = null
            override suspend fun agentUpdateEligibility(pcId: UUID) = AgentUpdateEligibility(pcId, true,
                currentVersion = pc.agentVersion, currentBuild = 138, targetVersion = "0.3.9", targetBuild = 139,
                activeSessionCount = 2)
            override suspend fun runAgentUpdate(pcId: UUID, expectedTargetBuild: Long, onProgress: (AgentUpdateProgress) -> Unit): AgentUpdateProgress {
                assertEquals(139L, expectedTargetBuild); installations++
                return AgentUpdateProgress(UUID.randomUUID(), pcId, pc.agentVersion, 138, "0.3.9", 139,
                    "SUCCEEDED", "VERIFYING_CONNECTION", 100).also(onProgress)
            }
        }
        compose.runOnUiThread {
            saveProductPrimaryDestination(compose.activity, ProductPrimaryDestination.DEVICES)
            compose.activity.setContent { MaterialTheme(colorScheme = productColors) { ProductApp(repository) } }
        }
        return pc
    }

    @Test fun longPressChecksAndConfirmsOneWindowsUpgrade() {
        val pc = showDevice()
        compose.onNodeWithText("Remote update test PC", useUnmergedTree = true).performTouchInput { longClick() }
        val label = compose.activity.getString(R.string.agent_update_action)
        compose.onNodeWithText(label).assertIsDisplayed().performClick()
        compose.waitForIdle()
        assertEquals(0, installations)
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_session_warning, 2)).assertIsDisplayed()
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_confirm)).performClick()
        compose.waitForIdle()
        assertEquals(1, installations)
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-card-${pc.id}").assertDoesNotExist()
    }

    @Test fun nonWindowsDeviceDoesNotOfferWindowsInstaller() {
        showDevice("linux")
        compose.onNodeWithText("Remote update test PC", useUnmergedTree = true).performTouchInput { longClick() }
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_action)).assertDoesNotExist()
        assertEquals(0, installations)
    }

    @Test fun minimizeReentryOfflineAndTwoPcUpdatesStayIndependent() {
        val first = PcSummary(UUID.randomUUID(), UUID.randomUUID(), "First update PC", "0.3.10.140",
            remoteRoutingEnabled = true, online = true, lastSeenAt = Instant.now(),
            capabilities = setOf(PcCapabilityIds.REMOTE_AGENT_UPDATE_V1))
        val second = first.copy(id = UUID.randomUUID(), deviceId = UUID.randomUUID(), name = "Second update PC")
        val pcs = listOf(first, second)
        val state = MutableStateFlow(ProductSnapshot(signedIn = true, configured = true,
            serverUrl = "https://termlink2.kget.cc:8910", accountLabel = "update-test-" + UUID.randomUUID(),
            connectionState = ProductConnectionState.CONNECTED, authenticationState = ProductAuthenticationState.AUTHENTICATED,
            devices = pcs.map { DeviceSummary(it.deviceId, DeviceType.WINDOWS_PC, it.name, "fixture", ByteArray(32),
                trustState = DeviceTrustState.TRUSTED, createdAt = Instant.now()) }, pcs = pcs))
        val active = mutableMapOf<UUID, AgentUpdateProgress>()
        val events = pcs.associate { it.id to Channel<AgentUpdateProgress>(Channel.UNLIMITED) }
        val starts = mutableMapOf<UUID, Int>()
        var generation by mutableIntStateOf(0)
        var foreground by mutableStateOf(true)
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = state
            override suspend fun refresh() = Unit
            override suspend fun activeAgentUpdate(pcId: UUID) = active[pcId]?.takeUnless { it.terminal }
            override suspend fun agentUpdateEligibility(pcId: UUID) = AgentUpdateEligibility(pcId, true,
                currentVersion = "0.3.10.140", targetVersion = "0.3.11", targetBuild = 141)
            override suspend fun runAgentUpdate(pcId: UUID, expectedTargetBuild: Long, onProgress: (AgentUpdateProgress) -> Unit): AgentUpdateProgress {
                starts[pcId] = starts.getOrDefault(pcId, 0) + 1
                active[pcId] = AgentUpdateProgress(UUID.randomUUID(), pcId, "0.3.10.140", 140, "0.3.11", 141,
                    "PROCESSING", "DOWNLOADING", 35)
                return monitorAgentUpdate(pcId, active.getValue(pcId).id, onProgress)
            }
            override suspend fun monitorAgentUpdate(pcId: UUID, updateId: UUID, onProgress: (AgentUpdateProgress) -> Unit): AgentUpdateProgress {
                var current = active.getValue(pcId)
                assertEquals(updateId, current.id)
                onProgress(current)
                while (!current.terminal) {
                    current = events.getValue(pcId).receive()
                    active[pcId] = current
                    onProgress(current)
                }
                return current
            }
        }
        compose.runOnUiThread {
            saveProductPrimaryDestination(compose.activity, ProductPrimaryDestination.DEVICES)
            compose.activity.setContent {
                key(generation) { MaterialTheme(colorScheme = productColors) { ProductApp(repository, foreground = foreground) } }
            }
        }
        fun start(pc: PcSummary) {
            compose.onNodeWithText(pc.name, useUnmergedTree = true).performTouchInput { longClick() }
            compose.onNodeWithText(compose.activity.getString(R.string.agent_update_action)).performClick()
            compose.onNodeWithText(compose.activity.getString(R.string.agent_update_confirm)).performClick()
            compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
            compose.onNodeWithTag("agent-update-card-${pc.id}").assertIsDisplayed()
            compose.onNodeWithTag("agent-update-dismiss-${pc.id}").assertDoesNotExist()
            compose.onNodeWithText(compose.activity.getString(R.string.agent_update_view_progress)).assertDoesNotExist()
            compose.onNodeWithText(compose.activity.getString(R.string.agent_update_view_result)).assertDoesNotExist()
        }
        start(first)
        compose.onNodeWithTag("agent-update-card-${first.id}").assertIsDisplayed()
        // PC A can disconnect during installation without hiding its card or blocking PC B.
        compose.runOnUiThread {
            state.value = state.value.copy(pcs = listOf(first.copy(online = false), second))
            events.getValue(first.id).trySend(active.getValue(first.id).copy(phase = "INSTALLING", progressPercent = 80)).getOrThrow()
        }
        compose.onNodeWithTag("agent-update-card-${first.id}").assertIsDisplayed()
        start(second)
        compose.runOnUiThread { foreground = false }
        compose.waitForIdle()
        compose.runOnUiThread { foreground = true; generation++ }
        compose.waitForIdle()
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-card-${first.id}").assertIsDisplayed()
        compose.onNodeWithTag("agent-update-card-${second.id}").assertIsDisplayed()
        val screenshot = androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot()
        java.io.File(compose.activity.cacheDir, "remote-agent-update-minimized.png").outputStream().use {
            screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it)
        }
        screenshot.recycle()
        compose.onNodeWithText(first.name, useUnmergedTree = true).performTouchInput { longClick() }
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_action)).performClick()
        compose.onNodeWithTag("agent-update-dialog").assertIsDisplayed()
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_cancel)).assertDoesNotExist()
        check(androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().uiAutomation
            .performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK))
        compose.waitUntil(5_000) { compose.onAllNodesWithTag("agent-update-dialog").fetchSemanticsNodes().isEmpty() }
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.runOnUiThread {
            state.value = state.value.copy(pcs = listOf(first.copy(agentVersion = "0.3.12.142"), second))
            events.getValue(first.id).trySend(active.getValue(first.id).copy(status = "SUCCEEDED", phase = "SUCCEEDED", progressPercent = 100,
                observedAgentVersion = "0.3.12.142", observedAgentBuild = 142)).getOrThrow()
        }
        compose.waitForIdle()
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-card-${first.id}").assertDoesNotExist()
        compose.onNodeWithText(first.name, useUnmergedTree = true).assertIsDisplayed()
        compose.onNodeWithTag("agent-update-card-${second.id}").assertIsDisplayed()
        assertEquals(mapOf(first.id to 1, second.id to 1), starts)
        compose.runOnUiThread {
            events.getValue(second.id).trySend(active.getValue(second.id).copy(status = "FAILED", phase = "FAILED",
                errorCode = "FIXTURE_INSTALL_FAILED")).getOrThrow()
        }
        compose.waitForIdle()
        compose.onAllNodesWithText(compose.activity.getString(R.string.agent_update_view_result)).fetchSemanticsNodes().also {
            assertEquals(1, it.size)
        }
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-dismiss-${second.id}").assertIsDisplayed()
        capture("remote-agent-update-failed")
        compose.onNode(hasText(compose.activity.getString(R.string.agent_update_view_result)) and
            hasAnyAncestor(hasTestTag("agent-update-card-${second.id}"))).performClick()
        compose.onNodeWithTag("agent-update-dialog").assertIsDisplayed()
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_error_code, "FIXTURE_INSTALL_FAILED")).assertIsDisplayed()
        compose.onNodeWithText(compose.activity.getString(R.string.action_close)).performClick()
        compose.onNodeWithTag("agent-update-dismiss-${second.id}").performClick()
        compose.onNodeWithTag("agent-update-card-${second.id}").assertDoesNotExist()
        compose.runOnUiThread { generation++ }
        compose.onNodeWithTag("agent-update-card-${first.id}").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-card-${second.id}").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
    }

    @Test fun versionBadgeAndSuccessfulResultDisappearAutomaticallyAndStayGoneAfterReentry() {
        val first = PcSummary(UUID.randomUUID(), UUID.randomUUID(), "Update available PC", "0.3.11.141",
            remoteRoutingEnabled = true, online = true, lastSeenAt = Instant.now(),
            capabilities = setOf(PcCapabilityIds.REMOTE_AGENT_UPDATE_V1))
        val second = first.copy(id = UUID.randomUUID(), deviceId = UUID.randomUUID(), name = "Current PC", agentVersion = "0.3.12.142")
        val pcs = listOf(first, second)
        val state = MutableStateFlow(ProductSnapshot(signedIn = true, configured = true,
            serverUrl = "https://termlink2.kget.cc:8910", accountLabel = "badge-test-" + UUID.randomUUID(),
            connectionState = ProductConnectionState.CONNECTED, authenticationState = ProductAuthenticationState.AUTHENTICATED,
            devices = pcs.map { DeviceSummary(it.deviceId, DeviceType.WINDOWS_PC, it.name, "fixture", ByteArray(32),
                trustState = DeviceTrustState.TRUSTED, createdAt = Instant.now()) }, pcs = pcs))
        var generation by mutableIntStateOf(0)
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = state
            override suspend fun refresh() = Unit
            override suspend fun activeAgentUpdate(pcId: UUID): AgentUpdateProgress? = null
            override suspend fun agentUpdateEligibility(pcId: UUID): AgentUpdateEligibility {
                val pc = state.value.pcs.first { it.id == pcId }
                return AgentUpdateEligibility(pcId, pc.agentVersion == first.agentVersion,
                    currentVersion = pc.agentVersion, currentBuild = if (pc.agentVersion == first.agentVersion) 141 else 142,
                    targetVersion = "0.3.12", targetBuild = 142)
            }
            override suspend fun runAgentUpdate(pcId: UUID, expectedTargetBuild: Long, onProgress: (AgentUpdateProgress) -> Unit): AgentUpdateProgress {
                installations++
                assertEquals(142L, expectedTargetBuild)
                state.value = state.value.copy(pcs = listOf(first.copy(agentVersion = "0.3.12.142"), second))
                return AgentUpdateProgress(UUID.randomUUID(), pcId, first.agentVersion, 141, "0.3.12", 142,
                    "SUCCEEDED", "SUCCEEDED", 100, observedAgentVersion = "0.3.12.142", observedAgentBuild = 142).also(onProgress)
            }
        }
        compose.runOnUiThread {
            saveProductPrimaryDestination(compose.activity, ProductPrimaryDestination.DEVICES)
            compose.activity.setContent {
                key(generation) { MaterialTheme(colorScheme = productColors) { ProductApp(repository) } }
            }
        }
        compose.onNodeWithTag("agent-update-available-${first.id}").assertIsDisplayed()
        fun textLayout(node: SemanticsNodeInteraction): TextLayoutResult {
            val layouts = mutableListOf<TextLayoutResult>()
            node.performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
            return layouts.single()
        }
        val availableLayout = textLayout(compose.onNodeWithTag("agent-update-available-${first.id}"))
        val currentDetails = compose.activity.getString(R.string.pc_agent_version, second.agentVersion) + " · " +
            compose.activity.getString(R.string.pc_routing_detail, compose.activity.getString(R.string.pc_platform_windows))
        val currentLayout = textLayout(compose.onNodeWithText(currentDetails, useUnmergedTree = true))
        assertEquals(1, availableLayout.lineCount)
        assertEquals(1, currentLayout.lineCount)
        assertEquals("The update icon must not increase metadata line height", currentLayout.size.height, availableLayout.size.height)
        compose.onNodeWithText(first.name, useUnmergedTree = true).performTouchInput { click() }
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        capture("agent-update-available")
        compose.onNodeWithTag("agent-update-available-${first.id}").performTouchInput {
            click(availableLayout.getBoundingBox(availableLayout.layoutInput.text.indexOf('↗')).center)
        }
        compose.onNodeWithTag("agent-update-available-${second.id}").assertDoesNotExist()
        assertEquals(0, installations)
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_confirm)).performClick()
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-dismiss-${first.id}").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-card-${first.id}").assertDoesNotExist()
        capture("agent-update-success-cleared")
        compose.runOnUiThread { generation++ }
        compose.waitForIdle()
        compose.onNodeWithTag("agent-update-card-${first.id}").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-available-${first.id}").assertDoesNotExist()
        compose.onNodeWithText(second.name).assertIsDisplayed()
        assertEquals(1, installations)
    }

    @Test fun dismissedUnavailableMessageStaysHiddenAfterReentryAndExplicitRetryRemainsAvailable() {
        val pc = PcSummary(UUID.randomUUID(), UUID.randomUUID(), "Unavailable update PC", "0.3.11.141",
            remoteRoutingEnabled = true, online = true, lastSeenAt = Instant.now(),
            capabilities = setOf(PcCapabilityIds.REMOTE_AGENT_UPDATE_V1))
        val state = MutableStateFlow(ProductSnapshot(signedIn = true, configured = true,
            serverUrl = "https://termlink2.kget.cc:8910", accountLabel = "dismiss-test-" + UUID.randomUUID(),
            connectionState = ProductConnectionState.CONNECTED, authenticationState = ProductAuthenticationState.AUTHENTICATED,
            devices = listOf(DeviceSummary(pc.deviceId, DeviceType.WINDOWS_PC, pc.name, "fixture", ByteArray(32),
                trustState = DeviceTrustState.TRUSTED, createdAt = Instant.now())), pcs = listOf(pc)))
        var generation by mutableIntStateOf(0)
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = state
            override suspend fun refresh() = Unit
            override suspend fun activeAgentUpdate(pcId: UUID): AgentUpdateProgress? = throw IOException("fixture outage")
            override suspend fun agentUpdateEligibility(pcId: UUID): AgentUpdateEligibility = throw IOException("fixture outage")
        }
        compose.runOnUiThread {
            saveProductPrimaryDestination(compose.activity, ProductPrimaryDestination.DEVICES)
            compose.activity.setContent {
                key(generation) { MaterialTheme(colorScheme = productColors) { ProductApp(repository) } }
            }
        }
        compose.onNodeWithTag("agent-update-available-${pc.id}").assertDoesNotExist()
        compose.onNodeWithTag("agent-update-dismiss-${pc.id}").assertIsDisplayed()
        capture("agent-update-dismissible-error")
        compose.onNodeWithTag("agent-update-dismiss-${pc.id}").performClick()
        compose.onNodeWithTag("agent-update-card-${pc.id}").assertDoesNotExist()
        compose.runOnUiThread { generation++ }
        compose.waitForIdle()
        compose.onNodeWithTag("agent-update-card-${pc.id}").assertDoesNotExist()
        compose.onNodeWithText(pc.name, useUnmergedTree = true).assertIsDisplayed().performTouchInput { longClick() }
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_action)).performClick()
        compose.onNodeWithTag("agent-update-dialog").assertIsDisplayed()
        assertEquals(0, installations)
    }
}
