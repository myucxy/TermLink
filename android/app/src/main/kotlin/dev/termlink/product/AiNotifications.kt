package dev.termlink.product

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import java.util.UUID
import org.json.JSONObject

internal const val AI_NOTIFICATION_TICKET = "dev.termlink.ai.notificationTicket"
internal class AiNotificationInbox : ViewModel() { var ticket by mutableStateOf<String?>(null) }

internal fun aiAccountKey(snapshot: ProductSnapshot): String {
    val scope = snapshot.signedIn.toString() + remoteDirectoryFavoriteAccountScope(snapshot.serverUrl, snapshot.accountLabel).orEmpty()
    return java.security.MessageDigest.getInstance("SHA-256").digest(scope.toByteArray()).joinToString("") { "%02x".format(it) }
}

/** Opaque local tickets keep exported Activity extras from becoming an approval API. */
internal class AiNotificationTickets(private val context: Context) {
    private val prefs = context.getSharedPreferences("ai-notification-tickets", Context.MODE_PRIVATE)
    private val store = OidcSessionStore(context)
    fun save(account: String, body: JSONObject): String {
        val ticket = UUID.randomUUID().toString()
        val editor = prefs.edit()
        if (prefs.all.size >= 192) prefs.all.keys.take(prefs.all.size - 128).forEach(editor::remove)
        check(editor.putString(ticket, store.sealAiOperation(account, body.toString())).commit())
        return ticket
    }
    fun read(account: String, ticket: String): JSONObject? = runCatching {
        require(UUID.fromString(ticket).toString() == ticket)
        val body = JSONObject(store.openAiOperation(account, prefs.getString(ticket, null) ?: return null))
        require(body.getLong("expiresAt") > System.currentTimeMillis())
        body
    }.getOrNull()
    fun remove(ticket: String) { prefs.edit().remove(ticket).apply() }
    fun removeSession(account: String, sessionId: String) {
        val editor = prefs.edit()
        prefs.all.forEach { (key, value) ->
            val body = runCatching { JSONObject(store.openAiOperation(account, value as String)) }.getOrNull()
            if (body?.optString("sessionId") == sessionId) editor.remove(key)
        }
        editor.apply()
    }
}

internal fun aiNotificationCanAnswer(body: JSONObject, host: String?, current: AiConversation): Boolean =
    body.optString("decision") in setOf("allow", "deny") && body.optString("hostId") == host &&
        body.optString("pcId") == current.pcId.toString() && body.optString("sessionId") == current.id &&
        current.canControl && current.controlVersion == body.optLong("controlVersion", -1) &&
        body.optLong("expiresAt") > System.currentTimeMillis() &&
        current.pending.any { it.id == body.optString("requestId") && it.kind != "question" && it.canApprove }

internal fun showAiNotification(context: Context, account: String, host: String, session: AiConversation, text: String) {
    if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return
    val manager = context.getSystemService(NotificationManager::class.java)
    manager.createNotificationChannel(NotificationChannel("ai_attention", "AI 任务提醒", NotificationManager.IMPORTANCE_DEFAULT))
    val store = AiNotificationTickets(context)
    fun intent(decision: String, requestId: String = ""): PendingIntent {
        val ticket = store.save(account, JSONObject().put("pcId", session.pcId).put("sessionId", session.id).put("hostId", host)
            .put("decision", decision).put("requestId", requestId).put("controlVersion", session.controlVersion)
            .put("expiresAt", System.currentTimeMillis() + if (decision.isEmpty()) 24 * 60 * 60_000L else 10 * 60_000L))
        val intent = Intent(context, (context as? ComponentActivity)?.javaClass ?: ProductActivity::class.java).setAction("dev.termlink.ai.$ticket")
            .putExtra(AI_NOTIFICATION_TICKET, ticket).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        return PendingIntent.getActivity(context, 0, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }
    val notification = NotificationCompat.Builder(context, "ai_attention")
        .setSmallIcon(dev.termlink.harness.R.mipmap.ic_launcher).setContentTitle("TermLink AI").setContentText(text)
        .setContentIntent(intent("")).setAutoCancel(true).setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
        .setOnlyAlertOnce(true)
    session.pending.singleOrNull()?.takeIf { it.kind != "question" && it.canApprove && session.canControl }?.let { pending ->
        for ((decision, label) in listOf("deny" to "拒绝", "allow" to "允许本次")) {
            val action = NotificationCompat.Action.Builder(0, label, intent(decision, pending.id))
            if (Build.VERSION.SDK_INT >= 31) action.setAuthenticationRequired(true)
            notification.addAction(action.build())
        }
    }
    manager.notify(session.id.hashCode(), notification.build())
}

@Composable
internal fun AiNotificationNavigation(workspace: AiWorkspace, snapshot: ProductSnapshot, navigate: () -> Unit) {
    val context = LocalContext.current
    val inbox = (context as? ComponentActivity)?.let { ViewModelProvider(it)[AiNotificationInbox::class.java] } ?: return
    val ticket = inbox.ticket
    LaunchedEffect(ticket, snapshot.signedIn, snapshot.connectionState) {
        if (ticket == null || !snapshot.signedIn || snapshot.connectionState != ProductConnectionState.CONNECTED) return@LaunchedEffect
        val store = AiNotificationTickets(context)
        val body = store.read(aiAccountKey(snapshot), ticket)
        if (body == null) { inbox.ticket = null; Toast.makeText(context, "提醒已失效或不属于当前账号，请打开会话确认。", Toast.LENGTH_LONG).show(); return@LaunchedEffect }
        try {
            val pc = UUID.fromString(body.getString("pcId")); val id = body.getString("sessionId")
            check(snapshot.pcs.any { it.id == pc && it.online && it.remoteRoutingEnabled }) { "电脑暂不可用，请稍后重新打开提醒。" }
            val current = workspace.readNotificationSession(pc, id)
            val decision = body.getString("decision")
            if (decision.isNotEmpty()) {
                check(aiNotificationCanAnswer(body, workspace.catalogs[pc]?.hostId, current) && !workspace.busy && !workspace.uncertain) {
                    "审批已处理、已过期或控制权已改变，请进入会话重新确认。"
                }
                require(decision in setOf("allow", "deny"))
                check(workspace.mutate(pc, "answer", current, JSONObject().put("requestId", body.getString("requestId")).put("decision", decision))?.await() == true) {
                    workspace.error.ifBlank { "审批尚未完成，请在会话中确认结果。" }
                }
            }
            workspace.open(workspace.conversations[id] ?: current); navigate()
            store.remove(ticket)
            context.getSystemService(NotificationManager::class.java).cancel(id.hashCode())
        } catch (e: Exception) {
            if (e is kotlinx.coroutines.CancellationException) throw e
            Toast.makeText(context, e.message ?: "无法读取提醒，请进入 AI 会话确认。", Toast.LENGTH_LONG).show()
        } finally { inbox.ticket = null }
    }
}
