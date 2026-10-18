package dev.termlink.product

import android.util.Log
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.window.layout.FoldingFeature
import androidx.window.layout.WindowInfoTracker
import androidx.window.layout.WindowMetricsCalculator
import dev.termlink.product.debug.AdaptiveLayoutAcceptanceActivity
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/** Uses actual emulator/OEM WindowManager data; no folding test decorator is installed. */
@RunWith(AndroidJUnit4::class)
class ProductPhysicalWindowInstrumentedTest {
    @get:Rule val compose = createAndroidComposeRule<AdaptiveLayoutAcceptanceActivity>()

    @Test fun physicalWindowKeepsTheNativeTerminalInsideItsUsablePane() {
        val expected = InstrumentationRegistry.getArguments().getString("expectedPosture", "flat")
        val activity = compose.activity
        compose.waitUntil(10_000) { activity.hasWindowFocus() }
        val features = runBlocking {
            withTimeout(10_000) {
                WindowInfoTracker.getOrCreate(activity).windowLayoutInfo(activity).first { info ->
                    expected != "half" || info.displayFeatures.filterIsInstance<FoldingFeature>()
                        .any { it.state == FoldingFeature.State.HALF_OPENED }
                }.displayFeatures.filterIsInstance<FoldingFeature>()
            }
        }
        val metrics = WindowMetricsCalculator.getOrCreate().computeCurrentWindowMetrics(activity).bounds
        val expectedArea = productDisplayArea(metrics.width(), metrics.height(), 0, 0,
            features.filter { it.isSeparating || it.occlusionType == FoldingFeature.OcclusionType.FULL }.map {
                ProductDisplayFold(ProductDisplayArea(it.bounds.left, it.bounds.top, it.bounds.right, it.bounds.bottom),
                    it.orientation == FoldingFeature.Orientation.VERTICAL)
            })
        compose.waitUntil(10_000) {
            val bounds = compose.onNodeWithTag("product-display-area", useUnmergedTree = true).fetchSemanticsNode().boundsInRoot
            kotlin.math.abs(bounds.width - expectedArea.width) < 3 && kotlin.math.abs(bounds.height - expectedArea.height) < 3
        }
        compose.onNodeWithTag(terminalTestTag(activity.owner.session), useUnmergedTree = true).assertExists()
        assertTrue(activity.owner.engine.visibleText().contains("fold-tablet-ready"))
        assertEquals(80, activity.owner.engine.currentSize().columns)
        assertEquals(24, activity.owner.engine.currentSize().rows)
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val screenshot = checkNotNull(instrumentation.uiAutomation.takeScreenshot())
        java.io.File(activity.getExternalFilesDir(null), "adaptive-window.png").outputStream().use {
            check(screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it))
        }
        screenshot.recycle()
        Log.i("TermLinkAdaptiveTest", "posture=$expected window=$metrics pane=$expectedArea features=$features native=80x24")
    }

    @Test fun tabletFreeformResizeRetainsTheSessionAndUsesTheActualWindow() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val owner = compose.activity.owner
        val before = WindowMetricsCalculator.getOrCreate().computeCurrentWindowMetrics(compose.activity).bounds
        fun shell(command: String) {
            instrumentation.uiAutomation.executeShellCommand(command).use { descriptor ->
                android.os.ParcelFileDescriptor.AutoCloseInputStream(descriptor).use { it.readBytes() }
            }
        }
        val component = compose.activity.componentName.flattenToShortString()
        try {
            shell("am start --windowingMode 5 -n $component")
            compose.waitUntil(10_000) { compose.activity.isInMultiWindowMode }
            val width = (before.width() * 0.6).toInt()
            val height = (before.height() * 0.75).toInt()
            shell("am task resize ${compose.activity.taskId} 0 100 $width ${100 + height}")
            compose.waitUntil(10_000) {
                compose.onNodeWithTag("product-display-area", useUnmergedTree = true).fetchSemanticsNode().boundsInRoot.width < before.width() * 0.8
            }
            compose.onNodeWithTag(terminalTestTag(owner.session), useUnmergedTree = true).assertExists()
            assertTrue(compose.activity.isInMultiWindowMode)
            org.junit.Assert.assertSame(owner.engine, compose.activity.owner.engine)
            assertEquals(owner.session.toString(), loadProductNavigationState(compose.activity).selectedSessionId)
            val screenshot = checkNotNull(instrumentation.uiAutomation.takeScreenshot())
            java.io.File(compose.activity.getExternalFilesDir(null), "adaptive-freeform.png").outputStream().use {
                check(screenshot.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it))
            }
            screenshot.recycle()
        } finally { shell("am start --windowingMode 1 -n $component") }
    }
}
