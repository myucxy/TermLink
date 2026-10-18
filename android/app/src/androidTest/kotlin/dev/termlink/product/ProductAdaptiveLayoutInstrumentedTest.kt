package dev.termlink.product

import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.window.layout.FoldingFeature
import androidx.window.layout.WindowLayoutInfo
import androidx.window.testing.layout.FoldingFeature as TestFoldingFeature
import androidx.window.testing.layout.WindowLayoutInfoPublisherRule
import dev.termlink.product.debug.AdaptiveLayoutAcceptanceActivity
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.RuleChain
import org.junit.runner.RunWith
import dev.termlink.harness.R
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowCompat
import androidx.lifecycle.ViewModelProvider

/** Injects geometry through Jetpack's real tracker; physical AVD posture tests run separately. */
@RunWith(AndroidJUnit4::class)
class ProductAdaptiveLayoutInstrumentedTest {
    private val folds = WindowLayoutInfoPublisherRule()
    private val compose = createAndroidComposeRule<AdaptiveLayoutAcceptanceActivity>()
    @get:Rule val rules: RuleChain = RuleChain.outerRule(folds).around(compose)

    private fun area() = compose.onNodeWithTag("product-display-area", useUnmergedTree = true)
        .fetchSemanticsNode().boundsInRoot

    @Test fun wideAndCompactNavigationUseActualWindowWidth() {
        val width = area().width / compose.activity.resources.displayMetrics.density
        compose.onNodeWithTag(if (width >= 700) "product-navigation-rail" else "product-bottom-navigation")
            .assertExists()
        compose.onNodeWithTag(terminalTestTag(compose.activity.owner.session), useUnmergedTree = true).assertExists()
    }

    @Test fun bookAndTabletopAvoidHingesWithoutReplacingTheSessionOrEngine() {
        val before = area()
        val owner = compose.activity.owner
        val feature = TestFoldingFeature(compose.activity, size = 32,
            state = FoldingFeature.State.HALF_OPENED, orientation = FoldingFeature.Orientation.VERTICAL)
        folds.overrideWindowLayoutInfo(WindowLayoutInfo(listOf(feature)))
        compose.waitUntil(10_000) { area().width < before.width * 0.75f }
        val book = area()
        assertTrue(book.right <= feature.bounds.left + 2 || book.left >= feature.bounds.right - 2)
        assertSame(owner.engine, compose.activity.owner.engine)
        compose.onNodeWithTag(terminalTestTag(owner.session), useUnmergedTree = true).assertExists()
        val tabletop = TestFoldingFeature(compose.activity, size = 24,
            state = FoldingFeature.State.HALF_OPENED, orientation = FoldingFeature.Orientation.HORIZONTAL)
        folds.overrideWindowLayoutInfo(WindowLayoutInfo(listOf(tabletop)))
        compose.waitUntil(10_000) { area().width > before.width * 0.9f && area().height < before.height * 0.75f }
        assertTrue(area().bottom <= tabletop.bounds.top + 2)
        folds.overrideWindowLayoutInfo(WindowLayoutInfo(emptyList()))
        compose.waitUntil(10_000) { area().width >= before.width - 2 && area().height >= before.height - 2 }
        assertSame(owner, compose.activity.owner)
    }

    @Test fun configurationRecreationKeepsNativeEngineAndSelectedSession() {
        val owner = compose.activity.owner
        lateinit var workspace: AiWorkspace
        compose.runOnIdle {
            workspace = ViewModelProvider(compose.activity)[AiWorkspaceViewModel::class.java]
                .acquire(compose.activity, owner.repository, aiAccountKey(owner.snapshot.value))
            workspace.drafts[owner.session.toString()] = "unsent adaptive AI draft"
        }
        compose.activityRule.scenario.recreate()
        compose.waitForIdle()
        assertSame(owner, compose.activity.owner)
        assertSame(owner.engine, compose.activity.owner.engine)
        assertEquals(owner.session.toString(), loadProductNavigationState(compose.activity).selectedSessionId)
        compose.runOnIdle {
            val restored = ViewModelProvider(compose.activity)[AiWorkspaceViewModel::class.java]
                .acquire(compose.activity, owner.repository, aiAccountKey(owner.snapshot.value))
            assertSame(workspace, restored)
            assertEquals("unsent adaptive AI draft", restored.drafts[owner.session.toString()])
        }
        compose.onNodeWithTag(terminalTestTag(owner.session), useUnmergedTree = true).assertExists()
    }

    @Test fun usernameDraftSurvivesWindowRecreationAndFoldChanges() {
        compose.runOnIdle { compose.activity.owner.snapshot.value = compose.activity.owner.snapshot.value.copy(signedIn = false) }
        val label = compose.activity.getString(R.string.sign_in_username_label)
        compose.onNodeWithText(label).performClick().performTextInput("layout-draft@example.invalid")
        compose.waitUntil(10_000) { compose.activity.hasWindowFocus() }
        compose.runOnIdle {
            WindowCompat.getInsetsController(compose.activity.window, compose.activity.window.decorView)
                .show(WindowInsetsCompat.Type.ime())
        }
        compose.waitUntil(10_000) {
            ViewCompat.getRootWindowInsets(compose.activity.window.decorView)?.isVisible(WindowInsetsCompat.Type.ime()) == true
        }
        val feature = TestFoldingFeature(compose.activity, state = FoldingFeature.State.HALF_OPENED,
            orientation = FoldingFeature.Orientation.HORIZONTAL)
        folds.overrideWindowLayoutInfo(WindowLayoutInfo(listOf(feature)))
        compose.waitUntil(10_000) { area().bottom <= feature.bounds.top + 2 }
        compose.onNodeWithText("layout-draft@example.invalid").assertExists()
        compose.activityRule.scenario.recreate()
        compose.waitForIdle()
        compose.onNodeWithText("layout-draft@example.invalid").assertExists()
    }
}
