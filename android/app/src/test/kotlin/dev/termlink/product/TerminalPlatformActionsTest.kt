package dev.termlink.product

import android.app.NotificationManager
import android.content.Context
import android.os.Looper
import androidx.test.core.app.ApplicationProvider
import dev.termlink.terminal.api.TerminalHostEffectKindV2
import dev.termlink.terminal.api.TerminalHostEffectV2
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class TerminalPlatformActionsTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private fun bell(sequence: Long) = listOf(TerminalHostEffectV2(TerminalHostEffectKindV2.PLATFORM_ACTION, sequence, 1, 0, byteArrayOf(1,0,0,0)))
    private fun testTickets() = CliNotificationTickets(context,
        { scope, body -> "$scope\n$body" },
        { scope, sealed -> require(sealed.startsWith("$scope\n")); sealed.removePrefix("$scope\n") })

    @Test fun permissionDeniedAndClosedSinksCannotNotifyOrReplayDeniedOutput() {
        val parent = CoroutineScope(SupervisorJob())
        val session = UUID.randomUUID()
        val manager = context.getSystemService(NotificationManager::class.java)
        val shadow = shadowOf(manager)
        val prefs = context.terminalActionPreferences()
        prefs.edit().clear().commit()
        context.getSharedPreferences("cli-notification-tickets", Context.MODE_PRIVATE).edit().clear().commit()
        val tickets = testTickets()
        val sink = AndroidTerminalPlatformActions(context, "test", session, parent, { true }, { null },
            notificationTickets = tickets)
        try {
            sink.offer(bell(1)); shadowOf(Looper.getMainLooper()).idle()
            assertTrue(shadow.allNotifications.isEmpty())
            prefs.edit().putBoolean(TERMINAL_ALERTS, true).commit()
            shadow.setNotificationsEnabled(false)
            sink.offer(bell(2)); shadowOf(Looper.getMainLooper()).idle()
            assertTrue(shadow.allNotifications.isEmpty())
            sink.close()
            val replacement = AndroidTerminalPlatformActions(context, "test", session, parent, { true }, { null },
                notificationTickets = tickets)
            shadow.setNotificationsEnabled(true)
            replacement.offer(bell(1)); replacement.offer(bell(2)); shadowOf(Looper.getMainLooper()).idle()
            assertTrue(shadow.allNotifications.isEmpty())
            replacement.offer(bell(3)); shadowOf(Looper.getMainLooper()).idle()
            assertEquals(1, shadow.allNotifications.size)
            val postedTicket = context.getSharedPreferences("cli-notification-tickets", Context.MODE_PRIVATE).all.keys.single()
            replacement.offer(bell(3)); shadowOf(Looper.getMainLooper()).idle()
            assertEquals(setOf(postedTicket),
                context.getSharedPreferences("cli-notification-tickets", Context.MODE_PRIVATE).all.keys)
            replacement.close(); shadowOf(Looper.getMainLooper()).idle()
            assertTrue(shadow.allNotifications.isEmpty())
            replacement.offer(bell(4)); shadowOf(Looper.getMainLooper()).idle()
            assertTrue(shadow.allNotifications.isEmpty())
        } finally {
            sink.close(); parent.cancel()
            context.getSharedPreferences("cli-notification-tickets", Context.MODE_PRIVATE).edit().clear().commit()
        }
    }

    @Test fun accountChangeSuppressesQueuedNotification() {
        val parent = CoroutineScope(SupervisorJob())
        context.terminalActionPreferences().edit().putBoolean(TERMINAL_ALERTS, true).putBoolean(TERMINAL_SOUNDS, false).commit()
        val sink = AndroidTerminalPlatformActions(context, "old-account", UUID.randomUUID(), parent, { false }, { null })
        try {
            sink.offer(bell(1)); shadowOf(Looper.getMainLooper()).idle()
            assertTrue(shadowOf(context.getSystemService(NotificationManager::class.java)).allNotifications.isEmpty())
        } finally { sink.close(); parent.cancel() }
    }
}
