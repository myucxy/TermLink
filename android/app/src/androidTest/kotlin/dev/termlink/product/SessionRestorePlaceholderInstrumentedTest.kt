package dev.termlink.product

import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.harness.R
import dev.termlink.product.debug.AdaptiveLayoutAcceptanceActivity
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/** Pure renderer-missing UI fixture; no real account, session or network. */
@RunWith(AndroidJUnit4::class)
class SessionRestorePlaceholderInstrumentedTest {
    @get:Rule val compose = createAndroidComposeRule<AdaptiveLayoutAcceptanceActivity>()

    @Test fun onlineRestoreShowsVisibleExplanationAndRetryWithoutClaimingAnEmptyTerminalIsReady() {
        checkPlaceholder(hostOnline = true, message = R.string.terminal_session_restore_pending)
    }

    @Test fun offlineRestoreShowsConnectionRequirementAndRetry() {
        checkPlaceholder(hostOnline = false, message = R.string.terminal_session_restore_offline)
    }

    private fun checkPlaceholder(hostOnline: Boolean, message: Int) {
        val id = UUID.randomUUID()
        var retries = 0
        compose.runOnUiThread {
            compose.activity.setContent {
                MaterialTheme {
                    SessionRestorePlaceholder(id, hostOnline, onRetry = { retries++ })
                }
            }
        }
        compose.onNodeWithTag("session-restore-pending:$id").assertIsDisplayed()
        compose.onNodeWithText(compose.activity.getString(message)).assertIsDisplayed()
        compose.onNodeWithTag("session-restore-retry:$id").assertIsDisplayed().performClick()
        compose.runOnIdle { assertEquals(1, retries) }
    }
}
