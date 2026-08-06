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
    TRUSTED_DEVICES("Trusted devices"),
    ACCOUNT("Account"),
    SETTINGS("Settings"),
}

/**
 * The production app is pinned to the public service.  Keep this UI-facing
 * value alongside the state contract so screens cannot accidentally expose a
 * mutable endpoint field.  [ProductionProductRepository] uses the same
 * default URL for its persisted session store.
 */
const val PRODUCT_PUBLIC_SERVER_URL = "https://termlink.kget.cc:8910"

/** First-release account policy: same-account Android devices get full access. */
const val PRODUCT_DEFAULT_ANDROID_PERMISSION = "MANAGE"

/** Public AppShip identity for Android update checks. */
const val PRODUCT_APPSHIP_SLUG = "termlink-android"

enum class ProductUpdateDecision {
    NOT_CONFIGURED,
    NO_UPDATE,
    OPTIONAL,
    REQUIRED,
    ERROR,
}

data class ProductUpdateStatus(
    val decision: ProductUpdateDecision = ProductUpdateDecision.NOT_CONFIGURED,
    val versionName: String? = null,
    val buildNumber: Long? = null,
    val releaseNotes: String? = null,
    val downloadUrl: String? = null,
    val message: String? = null,
    val checkedAt: Instant? = null,
)

/**
 * Routes retained in the product shell.  Membership and trust/pairing state
 * remain in [ProductSnapshot] for transport compatibility, but are not part
 * of the current Android user interface.
 */
val ProductDestination.isVisibleInProductShell: Boolean
    get() = this != ProductDestination.MEMBERSHIP && this != ProductDestination.TRUSTED_DEVICES

/** The only destinations shown in the phone's primary navigation bar. */
enum class ProductPrimaryDestination(val label: String) {
    DEVICES("Devices"),
    SESSIONS("Sessions"),
    SETTINGS("Settings"),
}

/** User-facing app chrome theme. Terminal colors intentionally remain independent. */
enum class ProductThemePreference(val wireValue: String) {
    SYSTEM("system"),
    LIGHT("light"),
    DARK("dark");

    companion object {
        fun fromWireValue(value: String?): ProductThemePreference =
            entries.firstOrNull { it.wireValue == value } ?: SYSTEM
    }
}

/**
 * Local presentation preferences for the remote terminal.  These values are
 * deliberately independent from the application light/dark theme: a terminal
 * is a user's working surface and should not unexpectedly change when the
 * surrounding product shell follows the system theme.
 */
data class TerminalDisplayPreferences(
    val fontSizeSp: Float = 14f,
    val lineSpacingMultiplier: Float = 1f,
    /** Packed opaque ARGB values; the settings UI edits the RGB channels. */
    val foregroundArgb: Int = 0xFFE9EEF5.toInt(),
    val backgroundArgb: Int = 0xFF080B10.toInt(),
) {
    val normalized: TerminalDisplayPreferences
        get() = copy(
            fontSizeSp = fontSizeSp.coerceIn(10f, 24f),
            lineSpacingMultiplier = lineSpacingMultiplier.coerceIn(0.8f, 1.8f),
            foregroundArgb = foregroundArgb or 0xFF000000.toInt(),
            backgroundArgb = backgroundArgb or 0xFF000000.toInt(),
        )
}

fun ProductDestination.primaryDestination(): ProductPrimaryDestination = when (this) {
    ProductDestination.DEVICES -> ProductPrimaryDestination.DEVICES
    ProductDestination.SESSIONS -> ProductPrimaryDestination.SESSIONS
    // Activity and history remain reachable from secondary actions. Keep the
    // devices destination selected while those pages are open because the
    // former Activity dock item is now the direct Sessions entry point.
    ProductDestination.ACTIVITY, ProductDestination.HISTORY -> ProductPrimaryDestination.DEVICES
    ProductDestination.SETTINGS,
    ProductDestination.MEMBERSHIP,
    ProductDestination.TRUSTED_DEVICES,
    ProductDestination.ACCOUNT -> ProductPrimaryDestination.SETTINGS
}

fun ProductPrimaryDestination.destination(): ProductDestination = when (this) {
    ProductPrimaryDestination.DEVICES -> ProductDestination.DEVICES
    ProductPrimaryDestination.SESSIONS -> ProductDestination.SESSIONS
    ProductPrimaryDestination.SETTINGS -> ProductDestination.SETTINGS
}

enum class DeviceFilter(val label: String) {
    ALL("All"),
    TRUSTED("Trusted"),
    PENDING("Pending"),
}

/**
 * Truthful lifecycle for the account/server boundary.  In particular, an
 * endpoint being configured or an OIDC token being present does not mean the
 * server has accepted a refresh yet.
 */
enum class ProductConnectionState {
    SIGNED_OUT,
    CONFIGURED,
    CONNECTING,
    CONNECTED,
    ERROR,
}

/** Server-authoritative enrollment state for this account-scoped phone key. */
enum class ProductEnrollmentState {
    UNKNOWN,
    PENDING_CONFIRMATION,
    TRUSTED,
    REVOKED,
}

enum class ProductAuthenticationState {
    SIGNED_OUT,
    NATIVE_CREDENTIALS,
    BROWSER_SETUP_REQUIRED,
    AUTHENTICATED,
}

/** Activity currently comes only from this device; the production API has no audit feed. */
enum class ActivitySource { DEVICE_LOCAL }

const val MAX_DEVICE_LOCAL_ACTIVITY_ENTRIES = 100
const val DEVICE_LOCAL_ACTIVITY_LABEL = "Device-local activity"

data class ActivityEntry(
    val id: String,
    val occurredAt: Instant,
    val title: String,
    val detail: String,
    val severity: Severity = Severity.INFO,
    val source: ActivitySource = ActivitySource.DEVICE_LOCAL,
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

/** Local labels for the compact shortcut dock; the action semantics remain fixed. */
data class TerminalShortcutPreferences(
    val labels: Map<TerminalAction, String> = defaultTerminalShortcutLabels(),
    val visibleActions: List<TerminalAction> = defaultTerminalShortcutActions(),
) {
    fun label(action: TerminalAction): String = labels[action].orEmpty().ifBlank { defaultTerminalShortcutLabels()[action].orEmpty() }

    val normalized: TerminalShortcutPreferences
        get() = copy(
            labels = TerminalAction.entries.associateWith { action ->
                label(action).trim().take(12).ifBlank { defaultTerminalShortcutLabels()[action].orEmpty() }
            },
            visibleActions = (visibleActions + TerminalAction.ENTER)
                .distinct()
                .filter(TerminalAction.entries::contains),
        )
}

private fun defaultTerminalShortcutLabels(): Map<TerminalAction, String> = mapOf(
    TerminalAction.ESCAPE to "Esc",
    TerminalAction.TAB to "Tab",
    TerminalAction.ENTER to "Enter",
    TerminalAction.ARROW_LEFT to "←",
    TerminalAction.ARROW_UP to "↑",
    TerminalAction.ARROW_DOWN to "↓",
    TerminalAction.ARROW_RIGHT to "→",
    TerminalAction.CTRL_C to "Ctrl+C",
)

private fun defaultTerminalShortcutActions(): List<TerminalAction> = listOf(
    TerminalAction.ESCAPE,
    TerminalAction.TAB,
    TerminalAction.ENTER,
    TerminalAction.ARROW_LEFT,
    TerminalAction.ARROW_UP,
    TerminalAction.ARROW_DOWN,
    TerminalAction.ARROW_RIGHT,
    TerminalAction.CTRL_C,
)

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
 * [connectionState] is the only source of truth for the server lifecycle; a
 * configured endpoint or signed-in token alone is never a connected claim.
 */
data class ProductSnapshot(
    val signedIn: Boolean = false,
    val configured: Boolean = false,
    val connectionState: ProductConnectionState = ProductConnectionState.SIGNED_OUT,
    /** A short, identifier-free message suitable for the sign-in surface. */
    val connectionError: String? = null,
    val authenticationState: ProductAuthenticationState = ProductAuthenticationState.SIGNED_OUT,
    val enrollmentState: ProductEnrollmentState = ProductEnrollmentState.UNKNOWN,
    /** True only while the explicit first-login confirmation dialog is shown. */
    val enrollmentConfirmationRequired: Boolean = false,
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
    val updateStatus: ProductUpdateStatus = ProductUpdateStatus(),
    val lastSyncedAt: Instant? = null,
)

/** Prepends a local event while retaining a bounded device-local activity window. */
fun ProductSnapshot.withLocalActivity(entry: ActivityEntry): ProductSnapshot = copy(
    activity = (listOf(entry.copy(source = ActivitySource.DEVICE_LOCAL)) + activity)
        .filter { it.source == ActivitySource.DEVICE_LOCAL }
        .take(MAX_DEVICE_LOCAL_ACTIVITY_ENTRIES),
)

fun ProductSnapshot.isConnected(): Boolean = connectionState == ProductConnectionState.CONNECTED

/** Returns true only when an existing trusted PC can actually manage pending devices. */
fun ProductSnapshot.canApprovePendingDevice(): Boolean =
    enrollmentState != ProductEnrollmentState.PENDING_CONFIRMATION &&
        // Keep compatibility with snapshots produced by older callers while
        // production state now uses [enrollmentState] exclusively.
        accountLabel != "Approval pending" &&
        pcs.any { it.remoteRoutingEnabled }

/**
 * Boundary for the production app. A real implementation can back this with
 * ProductionApiClient, ProductionRealtimeClient, and Room without making the
 * composables aware of transport or persistence details.
 */
interface ProductRepository : Closeable {
    val snapshot: StateFlow<ProductSnapshot>

    suspend fun refresh()
    /** Native username/password login. Password is transient and must be cleared by the caller. */
    suspend fun signIn(username: String, password: CharArray): Unit = signIn()
    /** Legacy no-argument entry point retained for settings/fakes; it never opens a browser. */
    suspend fun signIn(): Unit = error("Enter a username and password to sign in")
    /** Creates an account through the server's direct registration boundary. */
    suspend fun register(username: String, email: String, password: CharArray, confirm: CharArray): Unit =
        error("Direct account registration is unavailable")
    /** Checks the public AppShip update decision without an account token. */
    suspend fun checkForUpdate(): ProductUpdateStatus = ProductUpdateStatus()
    /**
     * Direct registration is intentionally not part of this contract yet:
     * the current service exposes registration only through its browser/PKCE
     * flow.  The product shell displays a TODO instead of launching that flow.
     */
    suspend fun beginBrowserRegistration()
    suspend fun beginBrowserAccountSetup()
    suspend fun confirmEnrollment()
    suspend fun cancelEnrollment()
    suspend fun configureServer(url: String)
    suspend fun signOut()
    suspend fun approveDevice(deviceId: UUID)
    suspend fun completePairing(challengeId: String, code: String, challengeNonce: String)
    suspend fun purchaseProduct(productId: String)
    suspend fun restorePurchases()
    suspend fun revokeDevice(deviceId: UUID)
    suspend fun requestControl(sessionId: UUID)
    suspend fun releaseControl(sessionId: UUID)
    /** Release all active server control leases while keeping read-only viewing alive. */
    suspend fun releaseAllControlLeases() = Unit
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
    override suspend fun signIn(username: String, password: CharArray) = unavailable()
    override suspend fun register(username: String, email: String, password: CharArray, confirm: CharArray) = unavailable()
    override suspend fun checkForUpdate(): ProductUpdateStatus = ProductUpdateStatus(
        decision = ProductUpdateDecision.NOT_CONFIGURED,
        message = "Update service is not configured",
    )
    override suspend fun beginBrowserRegistration() = unavailable()
    override suspend fun beginBrowserAccountSetup() = unavailable()
    override suspend fun confirmEnrollment() = unavailable()
    override suspend fun cancelEnrollment() = unavailable()
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
