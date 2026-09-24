package dev.termlink.product

import java.nio.file.Files
import java.nio.file.Paths
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TailFirstImeStabilityTest {
    @Test
    fun showImeHookUsesABoundedFrameWaitAndEmitsTimeoutDiagnostics() {
        val workingDirectory = Paths.get(System.getProperty("user.dir")).toAbsolutePath()
        val appRoot = listOf(
            workingDirectory,
            workingDirectory.resolve("app"),
            workingDirectory.resolve("android/app"),
        ).first { Files.isRegularFile(it.resolve("build.gradle.kts")) && Files.isDirectory(it.resolve("src/main")) }
        val source = Files.newBufferedReader(
            appRoot.resolve("src/main/kotlin/dev/termlink/product/ProductActivity.kt"),
        ).use { it.readText() }
        val showImeOperation = source
            .substringAfter("\"show_ime\" -> {")
            .substringBefore("else -> tailFirstFailure")
        val stabilityWait = source
            .substringAfter("private suspend fun awaitStableTailFirstIme(")
            .substringBefore("private suspend fun awaitTranscriptChange(")
        val timeoutFailure = source
            .substringAfter("private fun tailFirstImeStabilityFailure(")
            .substringBefore("private fun tailFirstFailure(")
        val terminalSurface = source
            .substringAfter("private fun TerminalSurface(")
            .substringBefore("private fun ActivityPage(")
        val imeRequest = source
            .substringAfter("private fun requestTailFirstTerminalIme(")
            .substringBefore("@Composable\nprivate fun ActivityPage(")

        assertFalse(showImeOperation.contains("delay(750)"))
        assertTrue(showImeOperation.contains("awaitStableTailFirstIme(surface)"))
        assertTrue(stabilityWait.contains("withTimeoutOrNull(TAIL_FIRST_IME_STABILITY_TIMEOUT_MILLIS)"))
        assertTrue(stabilityWait.contains("awaitTailFirstUiFrame()"))
        assertTrue(stabilityWait.contains("surface.showIme()"))
        assertTrue(source.contains("private const val TAIL_FIRST_IME_RETRY_FRAME_INTERVAL = 30"))
        assertTrue(source.contains("private const val TAIL_FIRST_IME_STABILITY_TIMEOUT_MILLIS = 30_000L"))
        assertTrue(source.contains("private const val TAIL_FIRST_IME_REQUIRED_STABLE_FRAMES = 2"))
        assertTrue(timeoutFailure.contains("\"observed_frames\""))
        assertTrue(timeoutFailure.contains("\"stable_frames\""))
        assertTrue(timeoutFailure.contains("\"terminal_bounds\""))
        assertTrue(timeoutFailure.contains("\"bottom_bar_bounds\""))
        assertTrue(timeoutFailure.contains("\"ime_bounds\""))
        assertTrue(timeoutFailure.contains("\"ime_dock_gap_pixels\""))
        assertTrue(source.contains("show_ime timed out:"))
        assertTrue(source.contains("coordinates.positionOnScreen()"))
        assertFalse(source.contains("coordinates.positionInWindow()"))
        assertFalse(source.contains(".imePadding()"))
        assertTrue(source.contains("sessionImeAvoidance("))
        assertTrue(source.contains("imeAvoidance.scaffoldBottom.toDp()"))
        assertTrue(source.contains("contentBottom.toDp()"))
        assertTrue(terminalSurface.contains("activeEngine?.createView("))
        assertFalse(terminalSurface.contains(".resize("))
        assertFalse(terminalSurface.contains("sendResize"))
        assertFalse(terminalSurface.contains("onSize"))
        assertTrue(terminalSurface.contains(".padding(horizontal = 6.dp)"))
        assertFalse(terminalSurface.contains(".padding(6.dp)"))
        assertTrue(imeRequest.contains("view.postDelayed("))
        assertTrue(imeRequest.contains("view.hasFocus()"))
        assertTrue(imeRequest.contains("getWindowInsetsController(view)?.show(WindowInsetsCompat.Type.ime())"))
        assertTrue(imeRequest.contains("showSoftInput(view, InputMethodManager.SHOW_IMPLICIT)"))

        val manifest = Files.newBufferedReader(appRoot.resolve("src/main/AndroidManifest.xml")).use { it.readText() }
        val productActivity = manifest
            .substringAfter("android:name=\"dev.termlink.product.ProductActivity\"")
            .substringBefore("</activity>")
        assertTrue(productActivity.contains("android:windowSoftInputMode=\"adjustNothing\""))
        assertFalse(productActivity.contains("android:windowSoftInputMode=\"adjustResize\""))
    }

    @Test
    fun requiresTwoConsecutiveReadyFrames() {
        val tracker = TailFirstImeStabilityTracker()
        val ready = readySample()

        assertFalse(tracker.recordFrame(ready))
        assertTrue(tracker.recordFrame(ready))

        assertEquals(2, tracker.diagnostics().observedFrames)
        assertEquals(2, tracker.diagnostics().stableFrames)
    }

    @Test
    fun invisibleOrIncompleteFramesResetTheStableStreak() {
        val tracker = TailFirstImeStabilityTracker()
        val ready = readySample()

        assertFalse(tracker.recordFrame(ready))
        assertFalse(tracker.recordFrame(ready.copy(imeVisible = false)))
        assertFalse(tracker.recordFrame(ready))
        assertFalse(tracker.recordFrame(ready.copy(bottomBarBounds = null)))
        assertFalse(tracker.recordFrame(ready))
        assertTrue(tracker.recordFrame(ready))
    }

    @Test
    fun boundsChangeStartsANewStableStreakAndDiagnosticsKeepTheLastFrame() {
        val tracker = TailFirstImeStabilityTracker()
        val first = readySample()
        val resized = first.copy(
            terminalBounds = first.terminalBounds?.copy(top = 49),
        )

        assertFalse(tracker.recordFrame(first))
        assertFalse(tracker.recordFrame(resized))
        assertTrue(tracker.recordFrame(resized))

        val diagnostics = tracker.diagnostics()
        assertEquals(3, diagnostics.observedFrames)
        assertEquals(2, diagnostics.stableFrames)
        assertEquals(resized, diagnostics.latestSample)
    }

    @Test
    fun invalidBoundsNeverQualifyAsReady() {
        val tracker = TailFirstImeStabilityTracker()
        val invalid = readySample().copy(
            imeBounds = TailFirstPixelBounds(left = 0, top = 600, right = 1080, bottom = 600),
        )

        assertFalse(tracker.recordFrame(invalid))
        assertFalse(tracker.recordFrame(invalid))
        assertEquals(0, tracker.diagnostics().stableFrames)
    }

    @Test
    fun readyLayoutRequiresDockImmediatelyAboveImeWithoutOverlapOrDoubleGap() {
        val ready = readySample()
        assertTrue(ready.isReady)
        assertEquals(0, imeDockGapPixels(ready))

        assertFalse(
            ready.copy(
                bottomBarBounds = ready.bottomBarBounds?.copy(bottom = 700),
            ).isReady,
        )
        assertFalse(
            ready.copy(
                bottomBarBounds = ready.bottomBarBounds?.copy(bottom = 800),
            ).isReady,
        )
        assertFalse(
            ready.copy(
                terminalBounds = ready.terminalBounds?.copy(bottom = 740),
            ).isReady,
        )
        assertFalse(
            ready.copy(
                terminalBounds = ready.terminalBounds?.copy(bottom = 718),
            ).isReady,
        )
        assertFalse(
            ready.copy(
                bottomBarBounds = ready.bottomBarBounds?.copy(bottom = 778),
            ).isReady,
        )
        assertFalse(
            ready.copy(
                terminalBounds = ready.terminalBounds?.copy(left = -1),
            ).isReady,
        )
    }

    private fun readySample() = TailFirstImeLayoutSample(
        imeVisible = true,
        displayBounds = TailFirstPixelBounds(left = 0, top = 0, right = 1080, bottom = 1920),
        terminalBounds = TailFirstPixelBounds(left = 0, top = 48, right = 1080, bottom = 720),
        bottomBarBounds = TailFirstPixelBounds(left = 6, top = 720, right = 1074, bottom = 780),
        imeBounds = TailFirstPixelBounds(left = 0, top = 780, right = 1080, bottom = 1920),
    )
}
