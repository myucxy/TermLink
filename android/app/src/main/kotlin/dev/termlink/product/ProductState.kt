package dev.termlink.product

import dev.termlink.billing.BillingSnapshot
import dev.termlink.model.CatalogPlan
import dev.termlink.model.DeviceSummary
import dev.termlink.model.Entitlement
import dev.termlink.model.HistoryObject
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionSummary
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.termux.TermuxTerminalEngine
import java.io.Closeable
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Destinations exposed by the product shell. */
enum class ProductDestination(val label: String) {
    DEVICES("Devices"),
    SESSIONS("Sessions"),
    ACTIVITY("Activity"),
    HISTORY("History"),
    MEMBERSHIP("Membership"),
    SETTINGS("Settings"),
}

enum class DeviceFilter(val label: String) {
    ALL("All"),
    TRUSTED("Trusted"),
    PENDING("Pending"),
}

data class ActivityEntry(
    val id: String,
    val occurredAt: Instant,
    val title: String,
    val detail: String,
    val severity: Severity = Severity.INFO,
)

data class PairingChallengeState(
    val challengeId: UUID,
    val code: String,
    val challengeNonce: String,
    val expiresAt: Instant? = null,
    val targetName: String? = null,
)

enum class Severity { INFO, SUCCESS, WARNING, ERROR }

enum class TerminalAction {
    ESCAPE,
    TAB,
    ENTER,
    ARROW_LEFT,
    ARROW_UP,
    ARROW_DOWN,
    ARROW_RIGHT,
    CTRL_C,
}

data class HistoryEntry(
    val sessionId: UUID,
    val sessionLabel: String,
    val historyObject: HistoryObject,
    val createdAt: Instant? = null,
)

/**
 * This is the state consumed by the product UI. It is deliberately server-shaped:
 * an empty or null value means that the server has not supplied the information.
 * The app never infers a plan, quota, or trust state locally.
 */
data class ProductSnapshot(
    val signedIn: Boolean = false,
    val configured: Boolean = false,
    val accountLabel: String? = null,
    val serverUrl: String? = null,
    val devices: List<DeviceSummary> = emptyList(),
    val pcs: List<PcSummary> = emptyList(),
    val sessions: List<SessionSummary> = emptyList(),
    val sessionStates: Map<UUID, RemoteSessionState> = emptyMap(),
    val activity: List<ActivityEntry> = emptyList(),
    val pairingChallenge: PairingChallengeState? = null,
    val history: List<HistoryEntry> = emptyList(),
    val replayedHistoryObjectIds: Set<UUID> = emptySet(),
    val catalogPlans: List<CatalogPlan> = emptyList(),
    val entitlement: Entitlement? = null,
    val catalogPlan: CatalogPlan? = null,
    val billing: BillingSnapshot = BillingSnapshot(),
    val lastSyncedAt: Instant? = null,
)

/**
 * Boundary for the production app. A real implementation can back this with
 * ProductionApiClient, ProductionRealtimeClient, and Room without making the
 * composables aware of transport or persistence details.
 */
interface ProductRepository : Closeable {
    val snapshot: StateFlow<ProductSnapshot>

    suspend fun refresh()
    suspend fun signIn()
    suspend fun configureServer(url: String)
    suspend fun signOut()
    suspend fun approveDevice(deviceId: UUID)
    suspend fun completePairing(challengeId: String, code: String, challengeNonce: String)
    suspend fun purchaseProduct(productId: String)
    suspend fun restorePurchases()
    suspend fun revokeDevice(deviceId: UUID)
    suspend fun requestControl(sessionId: UUID)
    suspend fun releaseControl(sessionId: UUID)
    suspend fun openSession(sessionId: UUID)
    fun terminalEngine(sessionId: UUID): TermuxTerminalEngine?
    fun historyEngine(historyObjectId: UUID): TermuxTerminalEngine?
    suspend fun stopSession(sessionId: UUID)
    suspend fun sendTerminalAction(sessionId: UUID, action: TerminalAction)
    suspend fun sendPaste(sessionId: UUID, text: String)
    suspend fun replayHistory(entry: HistoryEntry)
}

/**
 * The product launcher starts in a truthful state when no account/endpoint has
 * been configured. It does not manufacture devices or a demo entitlement.
 */
class UnavailableProductRepository : ProductRepository {
    private val mutableSnapshot = MutableStateFlow(ProductSnapshot())
    override val snapshot: StateFlow<ProductSnapshot> = mutableSnapshot.asStateFlow()

    override suspend fun refresh() = unavailable()
    override suspend fun signIn() = unavailable()
    override suspend fun configureServer(url: String) = unavailable()
    override suspend fun signOut() = unavailable()
    override suspend fun approveDevice(deviceId: UUID) = unavailable()
    override suspend fun completePairing(challengeId: String, code: String, challengeNonce: String) = unavailable()
    override suspend fun purchaseProduct(productId: String) = unavailable()
    override suspend fun restorePurchases() = unavailable()
    override suspend fun revokeDevice(deviceId: UUID) = unavailable()
    override suspend fun requestControl(sessionId: UUID) = unavailable()
    override suspend fun releaseControl(sessionId: UUID) = unavailable()
    override suspend fun openSession(sessionId: UUID) = unavailable()
    override fun terminalEngine(sessionId: UUID): TermuxTerminalEngine? = null
    override fun historyEngine(historyObjectId: UUID): TermuxTerminalEngine? = null
    override suspend fun stopSession(sessionId: UUID) = unavailable()
    override suspend fun sendTerminalAction(sessionId: UUID, action: TerminalAction) = unavailable()
    override suspend fun sendPaste(sessionId: UUID, text: String) = unavailable()
    override suspend fun replayHistory(entry: HistoryEntry) = unavailable()

    override fun close() = Unit

    private fun unavailable(): Nothing = error("Sign in and configure a TermLink server to continue")
}

object ProductFormatter {
    fun quotaLabel(used: Int, limit: Int): String =
        if (limit > 0) "$used / $limit" else "$used / unavailable"

    fun trustLabel(value: dev.termlink.model.DeviceTrustState): String =
        value.name.lowercase().replace('_', ' ').replaceFirstChar { it.uppercase() }

    fun sessionLabel(value: dev.termlink.model.SessionState): String =
        value.name.lowercase().replaceFirstChar { it.uppercase() }

    fun objectKindLabel(value: HistoryObject.Kind): String =
        value.name.lowercase().replaceFirstChar { it.uppercase() }
}
