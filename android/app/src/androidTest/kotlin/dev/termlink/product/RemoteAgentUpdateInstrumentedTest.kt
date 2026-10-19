package dev.termlink.product

import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.harness.R
import dev.termlink.model.*
import dev.termlink.product.debug.AdaptiveLayoutAcceptanceActivity
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
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
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_succeeded)).assertIsDisplayed()
    }

    @Test fun nonWindowsDeviceDoesNotOfferWindowsInstaller() {
        showDevice("linux")
        compose.onNodeWithText("Remote update test PC").performTouchInput { longClick() }
        compose.onNodeWithText(compose.activity.getString(R.string.agent_update_action)).assertDoesNotExist()
        assertEquals(0, installations)
    }
}
