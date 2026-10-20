package dev.termlink.product

import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.product.debug.AdaptiveLayoutAcceptanceActivity
import dev.termlink.session.RemoteSessionState
import java.util.UUID
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class CliCatalogRecoveryInstrumentedTest {
    @get:Rule val compose = createAndroidComposeRule<AdaptiveLayoutAcceptanceActivity>()

    @Test fun retryKeepsNativeViewAndResumesOnlyAfterConfirmedCatalog() {
        val owner = compose.activity.owner
        val pc = owner.snapshot.value.pcs.single()
        val otherPc = pc.copy(id = UUID.randomUUID(), deviceId = UUID.randomUUID(), name = "Unaffected PC")
        val state = MutableStateFlow(owner.snapshot.value.copy(pcs = listOf(pc, otherPc),
            sessionStates = mapOf(owner.session to RemoteSessionState.ReadOnly(1, "reconnected")),
            pcSessionCatalogErrors = mapOf(pc.id to "TIMEOUT")))
        val refreshCompleted = CompletableDeferred<Unit>()
        var refreshes = 0
        var controlRequests = 0
        val repository = object : ProductRepository by owner.repository {
            override val snapshot = state
            override suspend fun refresh() {
                refreshes++
                refreshCompleted.await()
                state.value = state.value.copy(pcSessionCatalogErrors = emptyMap())
            }
            override suspend fun requestControl(sessionId: UUID) {
                assertEquals(owner.session, sessionId)
                controlRequests++
                state.value = state.value.copy(sessionStates = mapOf(owner.session to
                    RemoteSessionState.Controlling(1, "fixture-lease", Long.MAX_VALUE)))
            }
        }
        compose.runOnUiThread {
            compose.activity.setContent { MaterialTheme(colorScheme = productColors) { ProductApp(repository) } }
        }
        compose.onNodeWithTag(terminalTestTag(owner.session), useUnmergedTree = true).assertIsDisplayed()
        compose.onNodeWithTag("session-catalog-retry").assertIsDisplayed().performClick()
        compose.waitForIdle()
        compose.onNodeWithTag("session-catalog-retry").assertIsNotEnabled()
        assertEquals(1, refreshes)
        assertEquals(0, controlRequests)
        compose.runOnUiThread { refreshCompleted.complete(Unit) }
        compose.waitUntil(5_000) { controlRequests == 1 }
        compose.onNodeWithTag("session-catalog-retry").assertDoesNotExist()
        compose.onNodeWithTag(terminalTestTag(owner.session), useUnmergedTree = true).assertIsDisplayed()
        assertEquals(owner.engine, compose.activity.owner.engine)
        assertEquals(otherPc, state.value.pcs.single { it.id == otherPc.id })
        compose.waitForIdle()
        assertEquals(1, controlRequests)
    }
}
