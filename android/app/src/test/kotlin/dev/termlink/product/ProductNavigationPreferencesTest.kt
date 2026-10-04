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
    fun persistsOpenedTabsSelectedSessionAndExpandedPcsAcrossActivityRecreation() {
        val sessionId = UUID.randomUUID()
        val secondSessionId = UUID.randomUUID()
        val pcId = UUID.randomUUID()

        saveProductPrimaryDestination(context, ProductPrimaryDestination.SESSIONS)
        saveProductSessionTabs(
            context,
            listOf(sessionId.toString(), secondSessionId.toString()),
            sessionId.toString(),
        )
        saveExpandedProductPcs(context, setOf(pcId.toString()))

        assertEquals(
            ProductNavigationState(
                primaryDestination = ProductPrimaryDestination.SESSIONS,
                selectedSessionId = sessionId.toString(),
                openedSessionIds = listOf(sessionId.toString(), secondSessionId.toString()),
                expandedPcIds = setOf(pcId.toString()),
            ),
            loadProductNavigationState(context),
        )
    }

    @Test
    fun dropsASelectedSessionThatIsNotAnOpenedTab() {
        val sessionId = UUID.randomUUID()

        saveSelectedProductSession(context, sessionId)

        val restored = loadProductNavigationState(context)

        assertNull(restored.selectedSessionId)
        assertEquals(emptyList<String>(), restored.openedSessionIds)
    }

    @Test
    fun removedTabAndItsInputPreferenceDoNotReturnAfterRestart() {
        val ended = UUID.randomUUID()
        val live = UUID.randomUUID().toString()
        saveProductSessionTabs(context, listOf(ended.toString(), live), ended.toString())
        saveTouchMouseEnabled(context, ended, true)
        saveProductSessionTabs(context, listOf(live), live)
        val restored = loadProductNavigationState(context)
        assertEquals(listOf(live), restored.openedSessionIds)
        assertEquals(live, restored.selectedSessionId)
        org.junit.Assert.assertFalse(loadTouchMouseEnabled(context, ended))
        saveProductSessionTabs(context, emptyList(), null)
        assertNull(loadProductNavigationState(context).selectedSessionId)
    }

    @Test
    fun ignoresUnknownDestinationsAndMalformedSessionIds() {
        context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString("primary_destination", "REMOVED_DESTINATION")
            .putString("selected_session_id", "not-a-uuid")
            .putString("opened_session_ids_v1", "[\"not-a-uuid\"]")
            .putStringSet("expanded_pc_ids_v1", setOf("also-not-a-uuid"))
            .commit()

        val restored = loadProductNavigationState(context)

        assertEquals(ProductPrimaryDestination.DEVICES, restored.primaryDestination)
        assertNull(restored.selectedSessionId)
        assertEquals(emptyList<String>(), restored.openedSessionIds)
        assertEquals(emptySet<String>(), restored.expandedPcIds)
    }

    private companion object {
        const val PREFERENCES_NAME = "termlink_product_preferences"
    }
}
