package dev.termlink.product

import android.app.Activity
import android.content.Context
import android.content.res.Configuration
import android.os.Looper
import android.view.KeyEvent
import android.view.inputmethod.EditorInfo
import androidx.test.core.app.ApplicationProvider
import dev.termlink.terminal.termux.TermuxTerminalEngine
import java.util.Locale
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONArray
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class TerminalShortcutInputTest {
    private val context: Context
        get() = ApplicationProvider.getApplicationContext()

    @Before
    fun clearPreferencesBefore() {
        context.deleteSharedPreferences(PRODUCT_PREFERENCES_NAME)
    }

    @After
    fun clearPreferencesAfter() {
        context.deleteSharedPreferences(PRODUCT_PREFERENCES_NAME)
    }

    @Test
    fun capturedLettersKeepLowercaseBaseKeyAndSeparateShiftModifier() {
        val plain = terminalCombinationFromAndroidKeyEvent(keyDown(KeyEvent.KEYCODE_P))
        val shifted = terminalCombinationFromAndroidKeyEvent(
            keyDown(KeyEvent.KEYCODE_P, KeyEvent.META_SHIFT_ON),
        )

        assertEquals("p", plain?.canonical)
        assertEquals("Shift+p", shifted?.canonical)
        assertEquals("Backspace", terminalCombinationFromAndroidKeyEvent(keyDown(KeyEvent.KEYCODE_DEL))?.canonical)
        assertEquals("Delete", terminalCombinationFromAndroidKeyEvent(keyDown(KeyEvent.KEYCODE_FORWARD_DEL))?.canonical)
        assertEquals("Home", terminalCombinationFromAndroidKeyEvent(keyDown(KeyEvent.KEYCODE_MOVE_HOME))?.canonical)
        assertEquals("Shift+PageUp", terminalCombinationFromAndroidKeyEvent(
            keyDown(KeyEvent.KEYCODE_PAGE_UP, KeyEvent.META_SHIFT_ON),
        )?.canonical)
        assertEquals("Ctrl+F12", terminalCombinationFromAndroidKeyEvent(
            keyDown(KeyEvent.KEYCODE_F12, KeyEvent.META_CTRL_ON),
        )?.canonical)
        assertNull(
            terminalCombinationFromAndroidKeyEvent(
                KeyEvent(0L, 0L, KeyEvent.ACTION_UP, KeyEvent.KEYCODE_P, 0),
            ),
        )
    }

    @Test
    fun plusShortcutAndOptionalLabelSurvivePreferenceRoundTrip() {
        val quickCommand = "codex --dangerously-bypass-approvals-and-sandbox resume"
        val value = TerminalShortcutPreferences(
            visibleActions = listOf(TerminalAction.DELETE, TerminalAction.ENTER),
            customShortcuts = listOf(
                TerminalCustomShortcut("literal-plus", "", "plus"),
                TerminalCustomShortcut("control-plus", "Add", "Ctrl+plus"),
                TerminalCustomShortcut(
                    "resume-codex",
                    "Codex",
                    quickCommand,
                    TerminalCustomShortcutType.QUICK_INPUT,
                ),
            ),
            customShortcutRows = listOf(
                listOf("resume-codex", "control-plus"),
                listOf("literal-plus"),
            ),
        ).normalized

        saveTerminalShortcutPreferences(context, value)
        val restored = loadTerminalShortcutPreferences(context)

        assertEquals(value, restored)
        assertEquals("+", restored.customShortcuts[0].keyCombination)
        assertEquals("+", restored.customShortcuts[0].displayLabel)
        assertEquals("Ctrl++", restored.customShortcuts[1].keyCombination)
        assertEquals("Add", restored.customShortcuts[1].displayLabel)
        assertEquals(TerminalCustomShortcutType.QUICK_INPUT, restored.customShortcuts[2].type)
        assertEquals(quickCommand, restored.customShortcuts[2].quickInput)
        assertEquals("Codex", restored.customShortcuts[2].displayLabel)
        assertEquals(listOf("resume-codex", "control-plus"), restored.customShortcutRows[0])
        assertEquals(listOf("literal-plus"), restored.customShortcutRows[1])
        assertEquals(
            fixedTerminalShortcutRows().flatten().map(::terminalBuiltInShortcutOrderKey),
            restored.shortcutOrder.take(TerminalAction.entries.size),
        )
    }

    @Test
    fun legacyShortcutJsonDefaultsToKeyCombinationAndIsUpgradedOnSave() {
        context.getSharedPreferences(PRODUCT_PREFERENCES_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(
                CUSTOM_SHORTCUTS_KEY,
                """[{"id":"legacy","label":"","keyCombination":"control+shift+p"}]""",
            )
            .commit()

        val restored = loadTerminalShortcutPreferences(context)

        assertEquals(1, restored.customShortcuts.size)
        assertEquals(TerminalCustomShortcutType.KEY_COMBINATION, restored.customShortcuts.single().type)
        assertEquals("Ctrl+Shift+p", restored.customShortcuts.single().value)
        assertEquals(listOf("legacy"), restored.customShortcutRows[TERMINAL_SHORTCUT_TOP_ROW])
        assertEquals(emptyList<String>(), restored.customShortcutRows[TERMINAL_SHORTCUT_BOTTOM_ROW])

        saveTerminalShortcutPreferences(context, restored)
        val stored = context.getSharedPreferences(PRODUCT_PREFERENCES_NAME, Context.MODE_PRIVATE)
            .getString(CUSTOM_SHORTCUTS_KEY, null)
        val item = JSONArray(stored).getJSONObject(0)
        assertEquals(TerminalCustomShortcutType.KEY_COMBINATION.name, item.getString("type"))
        assertEquals("Ctrl+Shift+p", item.getString("value"))
        assertEquals("Ctrl+Shift+p", item.getString("keyCombination"))
        val storedRows = context.getSharedPreferences(PRODUCT_PREFERENCES_NAME, Context.MODE_PRIVATE)
            .getString(CUSTOM_SHORTCUT_ROWS_KEY, null)
        assertEquals("legacy", JSONArray(storedRows).getJSONArray(0).getString(0))
    }

    @Test
    fun builtInActionNamesResolveInEnglishAndSimplifiedChinese() {
        assertEquals(TerminalAction.entries.size, TerminalAction.entries.map(::terminalActionLabelResource).toSet().size)
        assertEquals("Backspace", localizedActionLabel(Locale.US, TerminalAction.BACKSPACE))
        assertEquals("Delete", localizedActionLabel(Locale.US, TerminalAction.DELETE))
        assertEquals("退格键", localizedActionLabel(Locale.SIMPLIFIED_CHINESE, TerminalAction.BACKSPACE))
        assertEquals("删除键", localizedActionLabel(Locale.SIMPLIFIED_CHINESE, TerminalAction.DELETE))
    }

    @Test
    fun bothImeDeletionApisEmitBackspaceAndForwardDeleteBytes() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        val activity = controller.get()
        val engine = TermuxTerminalEngine()
        engine.setReadOnly(false)
        val view = engine.createView(activity)
        activity.setContentView(view)
        view.requestFocus()
        shadowOf(Looper.getMainLooper()).idle()
        val connection = requireNotNull(view.onCreateInputConnection(EditorInfo()))

        connection.deleteSurroundingText(2, 1)
        connection.deleteSurroundingTextInCodePoints(1, 2)
        shadowOf(Looper.getMainLooper()).idle()

        val events = runBlocking {
            withTimeout(1_000) { engine.inputEvents.take(6).toList() }
        }
        assertArrayEquals(byteArrayOf(0x7f), events[0])
        assertArrayEquals(byteArrayOf(0x7f), events[1])
        assertArrayEquals("\u001b[3~".encodeToByteArray(), events[2])
        assertArrayEquals(byteArrayOf(0x7f), events[3])
        assertArrayEquals("\u001b[3~".encodeToByteArray(), events[4])
        assertArrayEquals("\u001b[3~".encodeToByteArray(), events[5])

        engine.dispose()
        controller.destroy()
    }

    private fun localizedActionLabel(locale: Locale, action: TerminalAction): String {
        val configuration = Configuration(context.resources.configuration).apply { setLocale(locale) }
        return context.createConfigurationContext(configuration).getString(terminalActionLabelResource(action))
    }

    private fun keyDown(keyCode: Int, metaState: Int = 0): KeyEvent =
        KeyEvent(0L, 0L, KeyEvent.ACTION_DOWN, keyCode, 0, metaState)

    private companion object {
        const val PRODUCT_PREFERENCES_NAME = "termlink_product_preferences"
        const val CUSTOM_SHORTCUTS_KEY = "terminal_custom_shortcuts_v1"
        const val CUSTOM_SHORTCUT_ROWS_KEY = "terminal_custom_shortcut_rows_v1"
    }
}
