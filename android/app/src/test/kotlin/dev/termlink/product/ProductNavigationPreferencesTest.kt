package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.util.UUID
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class ProductNavigationPreferencesTest {
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
    fun defaultsToDevicesWithoutASelectedSession() {
        assertEquals(ProductNavigationState(), loadProductNavigationState(context))
    }

    @Test
    fun persistsSessionsDestinationAndSelectedSessionAcrossActivityRecreation() {
        val sessionId = UUID.randomUUID()

        saveProductPrimaryDestination(context, ProductPrimaryDestination.SESSIONS)
        saveSelectedProductSession(context, sessionId)

        assertEquals(
            ProductNavigationState(ProductPrimaryDestination.SESSIONS, sessionId.toString()),
            loadProductNavigationState(context),
        )
    }

    @Test
    fun ignoresUnknownDestinationsAndMalformedSessionIds() {
        context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString("primary_destination", "REMOVED_DESTINATION")
            .putString("selected_session_id", "not-a-uuid")
            .commit()

        val restored = loadProductNavigationState(context)

        assertEquals(ProductPrimaryDestination.DEVICES, restored.primaryDestination)
        assertNull(restored.selectedSessionId)
    }

    private companion object {
        const val PREFERENCES_NAME = "termlink_product_preferences"
    }
}
