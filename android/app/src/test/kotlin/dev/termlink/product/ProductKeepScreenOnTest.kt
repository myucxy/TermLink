package dev.termlink.product

import android.app.Activity
import android.content.Context
import android.view.WindowManager
import androidx.test.core.app.ApplicationProvider
import org.junit.After
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class ProductKeepScreenOnTest {
    private val context: Context
        get() = ApplicationProvider.getApplicationContext()

    @Before
    fun clearBefore() {
        context.deleteSharedPreferences(PREFERENCES_NAME)
    }

    @After
    fun clearAfter() {
        context.deleteSharedPreferences(PREFERENCES_NAME)
    }

    @Test
    fun keepScreenOnDefaultsOffAndPersistsBothStates() {
        assertFalse(loadKeepScreenOnPreference(context))

        saveKeepScreenOnPreference(context, true)
        assertTrue(loadKeepScreenOnPreference(context))

        saveKeepScreenOnPreference(context, false)
        assertFalse(loadKeepScreenOnPreference(context))
    }

    @Test
    fun appliesAndClearsTheWindowKeepScreenOnFlag() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        try {
            val window = controller.get().window

            applyKeepScreenOn(window, true)
            assertTrue(window.decorView.keepScreenOn)
            assertTrue(window.hasKeepScreenOnFlag())

            applyKeepScreenOn(window, false)
            assertFalse(window.decorView.keepScreenOn)
            assertFalse(window.hasKeepScreenOnFlag())
        } finally {
            controller.destroy()
        }
    }

    private fun android.view.Window.hasKeepScreenOnFlag(): Boolean =
        attributes.flags and WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON != 0

    private companion object {
        const val PREFERENCES_NAME = "termlink_product_preferences"
    }
}
