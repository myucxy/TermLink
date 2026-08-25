package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class RemoteDirectoryFavoriteTest {
    private val pcId = UUID.fromString("10000000-0000-4000-8000-000000000001")
    private lateinit var context: Context
    private lateinit var store: SharedPreferencesRemoteDirectoryFavoriteStore

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        context.getSharedPreferences("termlink_remote_directory_favorites", Context.MODE_PRIVATE)
            .edit()
            .clear()
            .commit()
        store = SharedPreferencesRemoteDirectoryFavoriteStore(context)
    }

    @Test
    fun toggleUsesWindowsCaseInsensitivePathIdentity() {
        val favorite = RemoteDirectoryFavorite(pcId, "PC", "Work", "C:\\Work\\")
        val added = toggleRemoteDirectoryFavorite(emptyList(), favorite)

        assertTrue(isRemoteDirectoryFavorite(added, pcId, "c:/work"))
        assertEquals(emptyList<RemoteDirectoryFavorite>(), toggleRemoteDirectoryFavorite(
            added,
            favorite.copy(path = "c:/WORK"),
        ))
    }

    @Test
    fun storeIsAccountScopedAndRoundTripsMetadata() {
        val favorite = RemoteDirectoryFavorite(pcId, "Office PC", "Source", "D:\\Source")
        store.save("https://server.example\nalice", listOf(favorite))

        assertEquals(listOf(favorite), store.load("https://server.example\nalice"))
        assertTrue(store.load("https://server.example\nbob").isEmpty())
        assertTrue(store.load("https://other.example\nalice").isEmpty())
    }

    @Test
    fun supportedShellMappingUsesOnlyReportedWhitelistValues() {
        val pc = dev.termlink.model.PcSummary(
            id = pcId,
            deviceId = UUID.fromString("20000000-0000-4000-8000-000000000002"),
            name = "PC",
            agentVersion = "test",
            remoteRoutingEnabled = true,
            online = true,
            lastSeenAt = null,
            supportedShells = setOf("windows-powershell", "unknown-shell"),
        )

        assertEquals(listOf(RemoteShellType.WINDOWS_POWERSHELL), supportedRemoteShellTypes(pc))
        assertFalse(supportedRemoteShellTypes(pc).contains(RemoteShellType.COMMAND_PROMPT))
    }
}
