package dev.termlink.product.debug

import android.os.Bundle
import android.content.Intent
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.lifecycleScope
import dev.termlink.harness.BuildConfig
import dev.termlink.model.PcSummary
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceType
import dev.termlink.model.DeviceTrustState
import dev.termlink.product.*
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.withContext
import kotlinx.coroutines.launch
import org.json.JSONObject

/** Explicit isolated UI/host-adapter acceptance. Does not substitute for E2EE relay tests. */
class AiAcceptanceActivity : ComponentActivity() {
    private val foreground = androidx.compose.runtime.mutableStateOf(false)
    override fun onStart() { super.onStart(); foreground.value = true }
    override fun onStop() { foreground.value = false; super.onStop() }
    private var acceptanceState: MutableStateFlow<ProductSnapshot>? = null
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        intent.getStringExtra(AI_NOTIFICATION_TICKET)?.let { ViewModelProvider(this)[AiNotificationInbox::class.java].ticket = it }
        if (intent.hasExtra("offline")) acceptanceState?.let { state ->
            state.value = state.value.copy(pcs = state.value.pcs.map { it.copy(online = !intent.getBooleanExtra("offline", false)) })
        }
        // Test delivery separately from the unavailable vendor push channel. Release has no such entry point.
        intent.getStringExtra("notifySession")?.let { id -> lifecycleScope.launch {
            val owner = ViewModelProvider(this@AiAcceptanceActivity)[AcceptanceOwner::class.java]
            val catalog = JSONObject(owner.repository.aiRequest(owner.pc, """{"action":"catalog"}"""))
            val result = JSONObject(owner.repository.aiRequest(owner.pc, JSONObject().put("action", "poll")
                .put("hostId", catalog.getString("hostId")).put("sessionId", UUID.fromString(id).toString()).toString()))
            showAiNotification(this@AiAcceptanceActivity, aiAccountKey(owner.snapshot.value), catalog.getString("hostId"),
                parseConversation(owner.pc, result.getJSONObject("session")), "AI 会话需要审批或回答")
        } }
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        check(BuildConfig.DEBUG && BuildConfig.APPLICATION_ID.endsWith(".aivalidation") &&
            BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST)
        intent.getStringExtra(AI_NOTIFICATION_TICKET)?.let { ViewModelProvider(this)[AiNotificationInbox::class.java].ticket = it }
        val owner = ViewModelProvider(this)[AcceptanceOwner::class.java]
        acceptanceState = owner.snapshot
        setContent { MaterialTheme(colorScheme = productColors) { ProductApp(owner.repository, foreground = foreground.value) } }
    }

    // Match production's repository lifetime so rotation exercises the real workspace retention.
    class AcceptanceOwner : ViewModel() {
        val pc = UUID.fromString("71cfc230-4749-43e0-83e2-ef60e18b5c35")
        val device = UUID.fromString("43164d72-c87a-4309-a9a5-b22dd771480d")
        val snapshot = MutableStateFlow(ProductSnapshot(signedIn = true, configured = true,
            accountLabel = "isolated-ai-acceptance", serverUrl = "http://127.0.0.1:18793",
            connectionState = ProductConnectionState.CONNECTED, authenticationState = ProductAuthenticationState.AUTHENTICATED,
            devices = listOf(DeviceSummary(device, DeviceType.WINDOWS_PC, "Windows acceptance", "test", ByteArray(32),
                trustState = DeviceTrustState.TRUSTED, createdAt = java.time.Instant.now())),
            pcs = listOf(PcSummary(pc, device, "Windows acceptance", "development", remoteRoutingEnabled = true,
                online = true, lastSeenAt = null, capabilities = setOf("ai_agent_v1")))))
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = this@AcceptanceOwner.snapshot
            override suspend fun refresh() = Unit
            override suspend fun aiRequest(pcId: UUID, body: String): String = withContext(Dispatchers.IO) {
                require(pcId == pc && body.length <= 48 * 1024)
                val connection = URL("http://127.0.0.1:18793/ai").openConnection() as HttpURLConnection
                try {
                    connection.requestMethod = "POST"; connection.doOutput = true
                    connection.connectTimeout = 3_000; connection.readTimeout = 40_000
                    connection.setRequestProperty("Content-Type", "application/json")
                    connection.outputStream.use { it.write(body.toByteArray()) }
                    connection.inputStream.bufferedReader().use { it.readText() }
                } finally { connection.disconnect() }
            }
        }
    }
}
