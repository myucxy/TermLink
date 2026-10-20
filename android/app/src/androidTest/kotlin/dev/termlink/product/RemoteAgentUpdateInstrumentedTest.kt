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
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.harness.R
import dev.termlink.model.*
import dev.termlink.product.debug.AdaptiveLayoutAcceptanceActivity
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.channels.Channel
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class RemoteAgentUpdateInstrumentedTest {
    @get:Rule val compose = createAndroidComposeRule<AdaptiveLayoutAcceptanceActivity>()
    private var installations = 0

    private fun showDevice(platform: String = "windows") {
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
    }

    @Test fun longPressChecksAndConfirmsOneWindowsUpgrade() {
        showDevice()
        compose.onNodeWithText("Remote update test PC").performTouchInput { longClick() }
        val label = compose.activity.getString(R.string.agent_update_action)
        compose.onNodeWithText(label).assertIsDisplayed().performClick()
        compose.waitForIdle()
        assertEquals(0, installations)
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_session_warning, 2)).assertIsDisplayed()
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_confirm)).performClick()
        compose.waitForIdle()
        assertEquals(1, installations)
        compose.onNode(hasText(compose.activity.getString(R.string.agent_update_succeeded)) and
            hasAnyAncestor(hasTestTag("agent-update-dialog"))).assertIsDisplayed()
    }

    @Test fun nonWindowsDeviceDoesNotOfferWindowsInstaller() {
        showDevice("linux")
        compose.onNodeWithText("Remote update test PC").performTouchInput { longClick() }
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
            compose.onNodeWithText(pc.name).performTouchInput { longClick() }
            compose.onNodeWithText(compose.activity.getString(R.string.agent_update_action)).performClick()
            compose.onNodeWithText(compose.activity.getString(R.string.agent_update_confirm)).performClick()
            compose.onNodeWithTag("agent-update-dialog").assertIsDisplayed()
            compose.onNodeWithText(compose.activity.getString(R.string.agent_update_minimize)).performClick()
            compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
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
        compose.onNode(hasText(compose.activity.getString(R.string.agent_update_view_progress)) and
            hasAnyAncestor(hasTestTag("agent-update-card-${first.id}"))).performClick()
        compose.onNodeWithTag("agent-update-dialog").assertIsDisplayed()
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_cancel)).assertDoesNotExist()
        check(androidx.test.platform.app.InstrumentationRegistry.getInstrumentation().uiAutomation
            .performGlobalAction(android.accessibilityservice.AccessibilityService.GLOBAL_ACTION_BACK))
        compose.waitUntil(5_000) { compose.onAllNodesWithTag("agent-update-dialog").fetchSemanticsNodes().isEmpty() }
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.runOnUiThread {
            events.getValue(first.id).trySend(active.getValue(first.id).copy(status = "SUCCEEDED", phase = "SUCCEEDED", progressPercent = 100,
                observedAgentVersion = "0.3.12.142", observedAgentBuild = 142)).getOrThrow()
        }
        compose.waitForIdle()
        compose.onNodeWithTag("agent-update-dialog").assertDoesNotExist()
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_installed_version, "0.3.12.142")).assertIsDisplayed()
        compose.onNodeWithTag("agent-update-card-${second.id}").assertIsDisplayed()
        assertEquals(mapOf(first.id to 1, second.id to 1), starts)
        compose.runOnUiThread {
            events.getValue(second.id).trySend(active.getValue(second.id).copy(status = "SUCCEEDED", phase = "SUCCEEDED", progressPercent = 100)).getOrThrow()
        }
        compose.waitForIdle()
        compose.onAllNodesWithText(compose.activity.getString(R.string.agent_update_view_result)).fetchSemanticsNodes().also {
            assertEquals(2, it.size)
        }
    }
}
