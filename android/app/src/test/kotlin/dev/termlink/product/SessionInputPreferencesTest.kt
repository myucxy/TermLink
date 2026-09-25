package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class SessionInputPreferencesTest {
    @Test
    fun mousePreferenceDefaultsOffPersistsPerSessionAndIsPrunedWithTabs() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val first = UUID.randomUUID()
        val second = UUID.randomUUID()
        try {
            assertFalse(loadTouchMouseEnabled(context, first))
            saveTouchMouseEnabled(context, first, true)
            assertTrue(loadTouchMouseEnabled(context, first))
            assertFalse(loadTouchMouseEnabled(context, second))
            saveTouchMouseEnabled(context, second, true)
            retainTouchMouseSessions(context, listOf(second.toString()))
            assertFalse(loadTouchMouseEnabled(context, first))
            assertTrue(loadTouchMouseEnabled(context, second))
            saveTouchMouseEnabled(context, second, false)
            assertFalse(loadTouchMouseEnabled(context, second))
        } finally { retainTouchMouseSessions(context, emptyList()) }
    }
}
