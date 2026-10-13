package dev.termlink.product

import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.os.SystemClock
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import dev.termlink.harness.R
import java.util.UUID
import kotlinx.coroutines.delay
import org.json.JSONObject

internal const val CLI_NOTIFICATION_TICKET = "dev.termlink.cli.notificationTicket"
internal class CliNotificationInbox : ViewModel() { var ticket by mutableStateOf<String?>(null) }

/** Separate storage prevents terminal notices from evicting AI approval tickets. */
internal class CliNotificationTickets(
    context: Context,
    private val seal: (String, String) -> String = OidcSessionStore(context)::sealAiOperation,
    private val unseal: (String, String) -> String = OidcSessionStore(context)::openAiOperation,
) {
    private val prefs = context.getSharedPreferences("cli-notification-tickets", Context.MODE_PRIVATE)

    fun save(account: String, body: JSONObject): String {
        removeExpired(account)
        check(prefs.all.size < 256) { "Too many pending CLI notifications" }
        val ticket = UUID.randomUUID().toString()
        check(prefs.edit().putString(ticket, seal("cli:$account", body.toString())).commit())
        return ticket
    }

    fun read(account: String, ticket: String): JSONObject? = runCatching {
        require(UUID.fromString(ticket).toString() == ticket)
        val sealed = prefs.getString(ticket, null) ?: return null
        JSONObject(unseal("cli:$account", sealed))
            .takeIf { it.getLong("expiresAt") > System.currentTimeMillis() }
    }.getOrNull()

    fun remove(ticket: String) { prefs.edit().remove(ticket).apply() }

    private fun removeExpired(account: String) {
        val expired = expiredCliTicketKeys(prefs.all, account, System.currentTimeMillis()) { scope, sealed ->
            unseal(scope, sealed)
        }
        if (expired.isNotEmpty()) {
            val editor = prefs.edit()
            expired.forEach { editor.remove(it) }
            check(editor.commit())
        }
    }

    fun removeSession(account: String, sessionId: String) {
        val editor = prefs.edit()
        prefs.all.forEach { (ticket, sealed) ->
            val body = runCatching { JSONObject(unseal("cli:$account", sealed as String)) }.getOrNull()
            if (body?.optString("sessionId") == sessionId) editor.remove(ticket)
        }
        editor.apply()
    }
}

internal fun expiredCliTicketKeys(
    entries: Map<String, *>, account: String, now: Long, unseal: (String, String) -> String,
): List<String> = entries.mapNotNull { (ticket, sealed) ->
    val body = runCatching { JSONObject(unseal("cli:$account", sealed as String)) }.getOrNull()
    ticket.takeIf { body?.optLong("expiresAt", Long.MAX_VALUE)?.let { it <= now } == true }
}

internal fun cliNotificationIntent(context: Context, ticket: String): Intent =
    Intent(context, ProductActivity::class.java).setAction("dev.termlink.cli.$ticket")
        .putExtra(CLI_NOTIFICATION_TICKET, ticket)
        .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)

internal fun cliNotificationTicket(intent: Intent): String? =
    intent.getStringExtra(CLI_NOTIFICATION_TICKET)?.takeIf { ticket ->
        intent.action == "dev.termlink.cli.$ticket" &&
            runCatching { UUID.fromString(ticket).toString() == ticket }.getOrDefault(false)
    }

internal fun cliNotificationTarget(snapshot: ProductSnapshot, body: JSONObject, account: String?): UUID? {
    if (!snapshot.signedIn || snapshot.connectionState != ProductConnectionState.CONNECTED ||
        account == null || body.optString("accountScope") != account ||
        body.optString("route") != "cli") return null
    val raw = body.optString("sessionId")
    val sessionId = runCatching { UUID.fromString(raw).takeIf { it.toString() == raw } }.getOrNull() ?: return null
    val session = snapshot.sessions.singleOrNull { it.id == sessionId } ?: return null
    val pc = snapshot.pcs.singleOrNull { it.id == session.pcId && it.online && it.remoteRoutingEnabled }
        ?: return null
    if (!snapshot.hasConfirmedSessionCatalog(pc)) return null
    return sessionId.takeIf { isSessionTabOpenable(session, pc) }
}

/** An absent session is conclusive only after every known Windows catalog was observed. */
internal fun cliNotificationCatalogPending(snapshot: ProductSnapshot): Boolean =
    snapshot.lastSyncedAt == null || snapshot.pcs.isEmpty() ||
        snapshot.pcs.any { !snapshot.hasConfirmedSessionCatalog(it) }

/** A notification is navigation only; current account and live catalog decide access. */
@Composable
internal fun CliNotificationNavigation(snapshot: ProductSnapshot, openSession: (UUID) -> Boolean) {
    val context = LocalContext.current
    val activity = context as? ComponentActivity ?: return
    val inbox = ViewModelProvider(activity)[CliNotificationInbox::class.java]
    val ticket = inbox.ticket
    val startedAt = remember(ticket) { SystemClock.elapsedRealtime() }
    LaunchedEffect(ticket, snapshot.signedIn, snapshot.connectionState, snapshot.sessions, snapshot.pcs,
        snapshot.devices, snapshot.pcSessionCatalogErrors, snapshot.pcSessionCatalogObservations,
        snapshot.lastSyncedAt) {
        if (ticket == null) return@LaunchedEffect
        val store = CliNotificationTickets(context)
        val account = OidcSessionStore(context).accountScope
        if (account == null || !snapshot.signedIn) {
            store.remove(ticket)
            inbox.ticket = null
            return@LaunchedEffect
        }
        if (snapshot.connectionState != ProductConnectionState.CONNECTED) return@LaunchedEffect
        val body = store.read(account, ticket)
        val target = body?.let { cliNotificationTarget(snapshot, it, account) }
        if (body != null && target == null && cliNotificationCatalogPending(snapshot)) {
            val remaining = 30_000L - (SystemClock.elapsedRealtime() - startedAt)
            if (remaining > 0) delay(remaining)
        }
        if (target == null || !openSession(target)) {
            Toast.makeText(context, R.string.cli_notification_session_unavailable, Toast.LENGTH_LONG).show()
        } else {
            context.getSystemService(NotificationManager::class.java).cancel("terminal:$target", 1)
        }
        store.remove(ticket)
        inbox.ticket = null
    }
}
