package dev.termlink.product

import android.content.Context
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.lightColorScheme
import androidx.compose.ui.Modifier
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dev.termlink.model.PcSummary
import dev.termlink.product.debug.AdaptiveLayoutAcceptanceActivity
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class AiAgentIconsInstrumentedTest {
    @get:Rule val compose = createAndroidComposeRule<AdaptiveLayoutAcceptanceActivity>()

    @Test fun createSheetShowsEveryBrandInDarkTheme() = checkCreateSheet(dark = true)
    @Test fun createSheetShowsEveryBrandInLightTheme() = checkCreateSheet(dark = false)

    private fun checkCreateSheet(dark: Boolean) {
        val pc = PcSummary(UUID.randomUUID(), UUID.randomUUID(), "AI icon test PC", "0.3.13.143",
            remoteRoutingEnabled = true, online = true, lastSeenAt = Instant.now(), capabilities = setOf("ai_agent_v1"))
        val snapshot = ProductSnapshot(signedIn = true, configured = true, pcs = listOf(pc),
            connectionState = ProductConnectionState.CONNECTED, authenticationState = ProductAuthenticationState.AUTHENTICATED)
        val agents = JSONArray().apply {
            aiAgentBrands.forEach { (id, brand) -> put(JSONObject().put("id", id).put("name", brand.name)
                .put("status", "available").put("strategy", if (id == "zcode") "desktop_remote" else "native")) }
        }
        var creates = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body)
                if (request.optString("action") == "create") creates++
                return JSONObject().put("hostId", "icon-fixture").put("defaultDirectory", "D:\\IconTest")
                    .put("agents", agents).put("sessions", JSONArray()).put("models", JSONArray()).toString()
            }
        }
        val scope = CoroutineScope(Dispatchers.Main.immediate + SupervisorJob())
        lateinit var workspace: AiWorkspace
        var initialized = false
        try {
            compose.runOnUiThread {
                workspace = AiWorkspace(repository, scope,
                    compose.activity.getSharedPreferences("agent-icons-${UUID.randomUUID()}", Context.MODE_PRIVATE))
                initialized = true
                workspace.beginCreate(pc.id)
                compose.activity.setContent {
                    MaterialTheme(colorScheme = if (dark) productColors else lightColorScheme()) {
                        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                            AiPage(workspace, snapshot, foreground = true, modifier = Modifier.fillMaxSize())
                        }
                    }
                }
            }
            for ((id, brand) in aiAgentBrands) {
                compose.onNodeWithTag("ai-agent-choice-icon-$id", useUnmergedTree = true).assertIsDisplayed()
                compose.onNodeWithText(brand.name, useUnmergedTree = true).assertIsDisplayed().performClick()
            }
            compose.waitForIdle()
            InstrumentationRegistry.getInstrumentation().waitForIdleSync()
            Thread.sleep(300)
            val screenshot = InstrumentationRegistry.getInstrumentation().uiAutomation.takeScreenshot()
            java.io.File(compose.activity.cacheDir, "ai-agent-icons-${if (dark) "dark" else "light"}.png").outputStream().use {
                screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it)
            }
            screenshot.recycle()
            assertEquals("Selecting a brand must not create a session", 0, creates)
        } finally {
            compose.runOnUiThread { if (initialized) workspace.retire(); scope.cancel() }
        }
    }
}
