package dev.termlink.product.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.material3.MaterialTheme
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.core.view.WindowCompat
import dev.termlink.harness.BuildConfig
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.product.*
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.nativecore.NativeTerminalEngine
import java.nio.ByteBuffer
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.runBlocking

/** Product UI plus real JNI renderer; isolated local fixture, without a host or a Relay login. */
class AdaptiveLayoutAcceptanceActivity : ComponentActivity() {
    lateinit var owner: Owner
        private set

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        check(BuildConfig.DEBUG && BuildConfig.APPLICATION_ID.endsWith(".adaptivevalidation"))
        enableEdgeToEdge()
        WindowCompat.getInsetsController(window, window.decorView).isAppearanceLightStatusBars = false
        owner = ViewModelProvider(this)[Owner::class.java]
        if (savedInstanceState == null) {
            saveProductPrimaryDestination(this, ProductPrimaryDestination.SESSIONS)
            saveProductSessionTabs(this, listOf(owner.session.toString()), owner.session.toString())
        }
        setContent { MaterialTheme(colorScheme = productColors) { ProductApp(owner.repository) } }
    }

    class Owner : ViewModel() {
        val session: UUID = UUID.fromString("6df73384-731c-4acf-aeda-884cb436e231")
        private val pc = UUID.fromString("299586f5-404b-42fd-ac72-2d34cce841fb")
        val engine = NativeTerminalEngine()
        val snapshot = MutableStateFlow(ProductSnapshot(signedIn = true, configured = true,
            serverUrl = BuildConfig.TERMLINK_SERVER_URL, accountLabel = "adaptive-layout-fixture",
            connectionState = ProductConnectionState.CONNECTED,
            authenticationState = ProductAuthenticationState.AUTHENTICATED,
            pcs = listOf(PcSummary(pc, UUID.fromString("55ddd859-d21c-46f9-bebe-ae510db26810"),
                "Windows layout fixture", "test", remoteRoutingEnabled = true, online = true, lastSeenAt = Instant.now())),
            sessions = listOf(SessionSummary(session, pc, "pwsh", null, SessionState.RUNNING,
                80, 24, 1u, Instant.now(), null, online = true)),
            sessionStates = mapOf(session to RemoteSessionState.ReadOnly(1, "Layout fixture")),
            sessionTitles = mapOf(session to "Fold / tablet layout")))
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = this@Owner.snapshot
            override fun terminalEngine(sessionId: UUID) = engine.takeIf { sessionId == session }
            override suspend fun openSession(sessionId: UUID) = Unit
            override suspend fun requestControl(sessionId: UUID) = Unit
            override suspend fun refresh() = Unit
            override suspend fun signOut() { snapshot.value = snapshot.value.copy(signedIn = false) }
        }

        init {
            runBlocking { engine.feed(1, ByteBuffer.wrap(("\u001b[32mTermLink adaptive screen verification\u001b[0m\r\n"+
                "Same native terminal and session across posture changes.\r\n"+
                "PS C:\\LayoutTest> echo fold-tablet-ready\r\nfold-tablet-ready\r\nPS C:\\LayoutTest> ").toByteArray())) }
        }

        override fun onCleared() { engine.dispose() }
    }
}
