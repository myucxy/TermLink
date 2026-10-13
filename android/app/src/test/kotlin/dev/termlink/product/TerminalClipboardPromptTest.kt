package dev.termlink.product

import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Looper
import androidx.activity.ComponentActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowAlertDialog

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class TerminalClipboardPromptTest {
    @Test fun onlyAnExplicitForegroundConfirmationWithTheSameLeaseCanWrite() {
        val controller = Robolectric.buildActivity(ComponentActivity::class.java).setup().visible()
        val activity = controller.get()
        controller.windowFocusChanged(true)
        val parent = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        val original = TerminalClipboardPermit("lease", 7)
        var permit: TerminalClipboardPermit? = original
        var visible = true
        var active = true
        val prefs = activity.terminalActionPreferences()
        val clipboard = activity.getSystemService(ClipboardManager::class.java)
        clipboard.setPrimaryClip(ClipData.newPlainText("test", "unchanged"))
        val prompt = TerminalClipboardPrompt(activity, parent, { activity }, { permit }, { visible }, { active })
        fun current() = clipboard.primaryClip!!.getItemAt(0).text.toString()
        fun confirm() {
            shadowOf(Looper.getMainLooper()).idle()
            val dialog = ShadowAlertDialog.getLatestAlertDialog()
            assertTrue(dialog.isShowing)
            val root = org.robolectric.util.ReflectionHelpers.callInstanceMethod<Any>(dialog.window!!.decorView, "getViewRootImpl")
            org.robolectric.shadow.api.Shadow.extract<org.robolectric.shadows.ShadowViewRootImpl>(root).callWindowFocusChanged(true)
            shadowOf(Looper.getMainLooper()).idle()
            assertTrue(dialog.window!!.decorView.hasWindowFocus())
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).performClick()
            shadowOf(Looper.getMainLooper()).idle()
        }
        try {
            prefs.edit().putBoolean(TERMINAL_CLIPBOARD, false).commit()
            prompt.offer("blocked", original)
            assertTrue(ShadowAlertDialog.getLatestAlertDialog()?.isShowing != true)
            prefs.edit().putBoolean(TERMINAL_CLIPBOARD, true).commit()
            prompt.offer("no lease", null)
            assertTrue(ShadowAlertDialog.getLatestAlertDialog()?.isShowing != true)
            permit = null
            prompt.offer("read only", original)
            assertTrue(ShadowAlertDialog.getLatestAlertDialog()?.isShowing != true)
            permit = original
            visible = false
            prompt.offer("history overlay", original)
            assertTrue(ShadowAlertDialog.getLatestAlertDialog()?.isShowing != true)
            visible = true
            controller.windowFocusChanged(false)
            prompt.offer("background", original)
            assertTrue(ShadowAlertDialog.getLatestAlertDialog()?.isShowing != true)
            controller.windowFocusChanged(true)
            active = false
            prompt.offer("signed out", original)
            assertTrue(ShadowAlertDialog.getLatestAlertDialog()?.isShowing != true)
            active = true
            prompt.offer("stale", original)
            permit = TerminalClipboardPermit("lease", 8)
            confirm(); assertEquals("unchanged", current())
            permit = original
            controller.windowFocusChanged(true)
            prompt.offer("hidden", original); visible = false
            confirm(); assertEquals("unchanged", current())
            visible = true; controller.windowFocusChanged(true)
            prompt.offer("中文\nexact", original)
            assertEquals("unchanged", current())
            confirm(); assertEquals("中文\nexact", current())
            controller.windowFocusChanged(true)
            prompt.offer("cancelled", original)
            ShadowAlertDialog.getLatestAlertDialog().getButton(AlertDialog.BUTTON_NEGATIVE).performClick()
            shadowOf(Looper.getMainLooper()).idle()
            assertEquals("中文\nexact", current())
        } finally { prompt.close(); parent.cancel(); controller.pause().stop().destroy() }
    }

    @Test fun previewEscapesControlAndBidiCharactersWithoutChangingTheCopySource() {
        assertEquals("a\\u001Bb\\u202Ec\\t\n", clipboardPreview("a\u001bb\u202ec\t\n"))
        assertEquals(241, clipboardPreview("x".repeat(1000)).length)
    }
}
