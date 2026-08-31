package dev.termlink.product

import android.net.Uri
import dev.termlink.billing.BillingSnapshot
import dev.termlink.harness.BuildConfig
import dev.termlink.model.CatalogPlan
import dev.termlink.model.DeviceSummary
import dev.termlink.model.Entitlement
import dev.termlink.model.HistoryObject
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionSummary
import dev.termlink.model.TerminalCommandCapability
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.api.TerminalViewportEngine
import dev.termlink.terminal.termux.TermuxTranscriptViewport
import java.io.Closeable
import java.time.Instant
import java.util.UUID
import java.util.Locale
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Destinations exposed by the product shell. */
enum class ProductDestination(val label: String) {
    DEVICES("Devices"),
    FILES("Files"),
    DOWNLOADS("Downloads"),
    SESSIONS("Sessions"),
    ACTIVITY("Activity"),
    HISTORY("History"),
    MEMBERSHIP("Membership"),
    TRUSTED_DEVICES("Trusted devices"),
    ACCOUNT("Account"),
    SETTINGS("Settings"),
}

/**
 * Release is pinned to the public service. Debug may inject a build-time LAN
 * endpoint, but the UI and every repository transport still consume the same
 * normalized value declared beside this state contract.
 */
const val PRODUCT_DEFAULT_SERVER_URL = "https://termlink.kget.cc:8910"

/**
 * Resolves the only server root used by the Android product. Release builds
 * remain pinned even if a build environment accidentally supplies an override.
 */
internal fun configuredProductServerUrl(
    configuredUrl: String?,
    isDebug: Boolean,
    allowCleartextLocalTest: Boolean = false,
): String {
    val candidate = configuredUrl?.takeIf { isDebug && it.isNotBlank() } ?: PRODUCT_DEFAULT_SERVER_URL
    val localCleartext = allowCleartextLocalTest && isDebug && isExplicitLocalTestUrl(candidate)
    return ServerUrlPolicy.normalize(candidate, allowCleartextForLocalTest = localCleartext)
}

private fun isExplicitLocalTestUrl(value: String): Boolean {
    val uri = runCatching { java.net.URI(value.trim()) }.getOrNull() ?: return false
    if (!uri.scheme.equals("http", ignoreCase = true)) return false
    val host = uri.host?.lowercase() ?: return false
    if (host == "localhost" || host.endsWith(".localhost") || host == "::1") return true
    val octets = host.split('.').mapNotNull(String::toIntOrNull)
    if (octets.size != 4 || octets.any { it !in 0..255 }) return false
    return octets[0] == 10 ||
        octets[0] == 127 ||
        octets[0] == 192 && octets[1] == 168 ||
        octets[0] == 172 && octets[1] in 16..31
}

val PRODUCT_PUBLIC_SERVER_URL: String = configuredProductServerUrl(
    configuredUrl = BuildConfig.TERMLINK_SERVER_URL,
    isDebug = BuildConfig.DEBUG,
    allowCleartextLocalTest = BuildConfig.TERMLINK_ALLOW_CLEARTEXT_LOCAL_TEST,
)

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

sealed interface ProductUpdateDownloadState {
    data object Idle : ProductUpdateDownloadState
    data class Downloading(val downloadedBytes: Long, val totalBytes: Long) : ProductUpdateDownloadState
    data object Verifying : ProductUpdateDownloadState
    data class Ready(val apkPath: String) : ProductUpdateDownloadState
    data class Failed(val message: String) : ProductUpdateDownloadState
}

data class ProductUpdateStatus(
    val decision: ProductUpdateDecision = ProductUpdateDecision.NOT_CONFIGURED,
    val versionName: String? = null,
    val buildNumber: Long? = null,
    val releaseNotes: String? = null,
    val downloadUrl: String? = null,
    val message: String? = null,
    val checkedAt: Instant? = null,
    val artifactSizeBytes: Long? = null,
    val artifactSha256: String? = null,
    val minSupportedBuild: Long? = null,
    val fromCache: Boolean = false,
    val dialogVisible: Boolean = false,
    val downloadState: ProductUpdateDownloadState = ProductUpdateDownloadState.Idle,
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
    val fontScheme: TerminalFontScheme = TerminalFontScheme.BUNDLED_NERD_FONT,
    val customFontDisplayName: String? = null,
    val customFontSha256: String? = null,
    val fontSizeSp: Float = 14f,
    val lineSpacingMultiplier: Float = 1f,
    /** Packed opaque ARGB values; the settings UI edits the RGB channels. */
    val foregroundArgb: Int = 0xFFE9EEF5.toInt(),
    val backgroundArgb: Int = 0xFF080B10.toInt(),
) {
    val normalized: TerminalDisplayPreferences
        get() = copy(
            customFontDisplayName = customFontDisplayName?.trim()?.take(96)?.takeIf(String::isNotEmpty),
            customFontSha256 = customFontSha256
                ?.lowercase()
                ?.takeIf { it.matches(Regex("[0-9a-f]{64}")) },
            fontSizeSp = fontSizeSp.coerceIn(10f, 24f),
            lineSpacingMultiplier = lineSpacingMultiplier.coerceIn(0.8f, 1.8f),
            foregroundArgb = foregroundArgb or 0xFF000000.toInt(),
            backgroundArgb = backgroundArgb or 0xFF000000.toInt(),
        )
}

enum class TerminalFontScheme(val wireValue: String) {
    BUNDLED_NERD_FONT("bundled-nerd-font"),
    SYSTEM_MONOSPACE("system-monospace"),
    CUSTOM("custom"),
    ;

    companion object {
        fun fromWireValue(value: String?): TerminalFontScheme =
            entries.firstOrNull { it.wireValue == value } ?: BUNDLED_NERD_FONT
    }
}

fun ProductDestination.primaryDestination(): ProductPrimaryDestination = when (this) {
    ProductDestination.DEVICES,
    ProductDestination.FILES,
    ProductDestination.DOWNLOADS -> ProductPrimaryDestination.DEVICES
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
    /** Legacy server value retained for transport compatibility; it is never a user prompt. */
    PENDING_CONFIRMATION,
    TRUSTED,
    REVOKED,
}

/**
 * Same-account phones are usable immediately. A pending response is handled
 * through the legacy self-activation endpoint without exposing enrollment UI.
 */
internal enum class SameAccountDeviceAction {
    REFRESH,
    ACTIVATE_LEGACY_AND_REFRESH,
    BLOCK_REVOKED,
}

internal fun sameAccountDeviceAction(trustState: dev.termlink.model.DeviceTrustState): SameAccountDeviceAction =
    when (trustState) {
        dev.termlink.model.DeviceTrustState.TRUSTED -> SameAccountDeviceAction.REFRESH
        dev.termlink.model.DeviceTrustState.PENDING -> SameAccountDeviceAction.ACTIVATE_LEGACY_AND_REFRESH
        dev.termlink.model.DeviceTrustState.REVOKED -> SameAccountDeviceAction.BLOCK_REVOKED
    }

enum class ProductAuthenticationState {
    SIGNED_OUT,
    NATIVE_CREDENTIALS,
    EMBEDDED_SETUP_REQUIRED,
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
    BACKSPACE,
    DELETE,
    ARROW_LEFT,
    ARROW_UP,
    ARROW_DOWN,
    ARROW_RIGHT,
    CTRL_C,
}

/** A validated terminal key chord entered by the user in Settings. */
data class TerminalKeyCombination(
    val key: String,
    val control: Boolean = false,
    val alt: Boolean = false,
    val shift: Boolean = false,
) {
    val canonical: String
        get() = buildList {
            if (control) add("Ctrl")
            if (alt) add("Alt")
            if (shift) add("Shift")
            add(key)
        }.joinToString("+")

    val printableCodePoint: Int?
        get() = key.takeIf { it.codePointCount(0, it.length) == 1 }
            ?.codePointAt(0)

    companion object {
        private val aliases = mapOf(
            "esc" to "Esc",
            "escape" to "Esc",
            "tab" to "Tab",
            "enter" to "Enter",
            "return" to "Enter",
            "backspace" to "Backspace",
            "bksp" to "Backspace",
            "delete" to "Delete",
            "del" to "Delete",
            "left" to "Left",
            "arrowleft" to "Left",
            "up" to "Up",
            "arrowup" to "Up",
            "down" to "Down",
            "arrowdown" to "Down",
            "right" to "Right",
            "arrowright" to "Right",
            "home" to "Home",
            "end" to "End",
            "pageup" to "PageUp",
            "pgup" to "PageUp",
            "pagedown" to "PageDown",
            "pgdn" to "PageDown",
            "insert" to "Insert",
            "ins" to "Insert",
            "space" to "Space",
            "plus" to "+",
        )

        fun parse(raw: String): TerminalKeyCombination? {
            val trimmed = raw.trim()
            val literalPlusKey = trimmed == "+" || trimmed.endsWith("++")
            val tokenSource = if (literalPlusKey && trimmed != "+") trimmed.dropLast(1) else trimmed
            val tokens = tokenSource.split('+').map(String::trim).filter(String::isNotEmpty).toMutableList()
            if (literalPlusKey) tokens += "+"
            if (tokens.isEmpty()) return null
            var control = false
            var alt = false
            var shift = false
            val keys = mutableListOf<String>()
            tokens.forEach { token ->
                when (token.lowercase(Locale.ROOT)) {
                    "ctrl", "control" -> control = true
                    "alt", "option" -> alt = true
                    "shift" -> shift = true
                    else -> keys += token
                }
            }
            if (keys.size != 1) return null
            val rawKey = keys.single()
            val folded = rawKey.lowercase(Locale.ROOT).replace(" ", "").replace("-", "")
            val key = aliases[folded]
                ?: rawKey.uppercase(Locale.ROOT).takeIf { candidate ->
                    candidate.length in 2..3 && candidate.startsWith("F") &&
                        candidate.drop(1).toIntOrNull()?.let { it in 1..12 } == true
                }
                ?: rawKey.takeIf {
                    it.codePointCount(0, it.length) == 1 &&
                        !Character.isISOControl(it.codePointAt(0))
                }
                ?: return null
            return TerminalKeyCombination(key, control, alt, shift)
        }
    }
}

enum class TerminalCustomShortcutType {
    KEY_COMBINATION,
    QUICK_INPUT,
}

data class TerminalCustomShortcut(
    val id: String,
    val label: String,
    val value: String,
    val type: TerminalCustomShortcutType = TerminalCustomShortcutType.KEY_COMBINATION,
) {
    /** Compatibility accessor for callers and preferences created before typed shortcuts. */
    val keyCombination: String
        get() = value.takeIf { type == TerminalCustomShortcutType.KEY_COMBINATION }.orEmpty()

    val quickInput: String
        get() = value.takeIf { type == TerminalCustomShortcutType.QUICK_INPUT }.orEmpty()

    val displayLabel: String
        get() = label.ifBlank {
            value.replace(Regex("\\s+"), " ").trim()
        }

    val normalizedOrNull: TerminalCustomShortcut?
        get() {
            val normalizedId = id.trim().take(64).takeIf {
                it.isNotEmpty() && it.all { character -> character.isLetterOrDigit() || character == '-' || character == '_' }
            } ?: return null
            val normalizedLabel = label.trim().take(16)
            val normalizedValue = when (type) {
                TerminalCustomShortcutType.KEY_COMBINATION ->
                    TerminalKeyCombination.parse(value)?.canonical ?: return null
                TerminalCustomShortcutType.QUICK_INPUT ->
                    value.take(MAX_TERMINAL_QUICK_INPUT_LENGTH).takeIf(String::isNotBlank) ?: return null
            }
            return copy(
                id = normalizedId,
                label = normalizedLabel,
                value = normalizedValue,
            )
    }
}

internal suspend fun dispatchTerminalCustomShortcut(
    shortcut: TerminalCustomShortcut,
    sendKeyCombination: suspend (String) -> Unit,
    sendQuickInput: suspend (String) -> Unit,
) {
    val normalized = requireNotNull(shortcut.normalizedOrNull) { "Terminal shortcut is invalid" }
    when (normalized.type) {
        TerminalCustomShortcutType.KEY_COMBINATION -> sendKeyCombination(normalized.keyCombination)
        TerminalCustomShortcutType.QUICK_INPUT -> sendQuickInput(normalized.quickInput)
    }
}

internal fun terminalShiftedCodePoint(codePoint: Int): Int = when (codePoint) {
    '1'.code -> '!'.code
    '2'.code -> '@'.code
    '3'.code -> '#'.code
    '4'.code -> '$'.code
    '5'.code -> '%'.code
    '6'.code -> '^'.code
    '7'.code -> '&'.code
    '8'.code -> '*'.code
    '9'.code -> '('.code
    '0'.code -> ')'.code
    '-'.code -> '_'.code
    '='.code -> '+'.code
    '['.code -> '{'.code
    ']'.code -> '}'.code
    '\\'.code -> '|'.code
    ';'.code -> ':'.code
    '\''.code -> '"'.code
    ','.code -> '<'.code
    '.'.code -> '>'.code
    '/'.code -> '?'.code
    '`'.code -> '~'.code
    else -> Character.toUpperCase(codePoint)
}

/** Local labels for the compact shortcut dock; the action semantics remain fixed. */
data class TerminalShortcutPreferences(
    val labels: Map<TerminalAction, String> = defaultTerminalShortcutLabels(),
    val visibleActions: List<TerminalAction> = defaultTerminalShortcutActions(),
    val customShortcuts: List<TerminalCustomShortcut> = emptyList(),
    val shortcutOrder: List<String> = emptyList(),
) {
    fun label(action: TerminalAction): String = labels[action].orEmpty().ifBlank { defaultTerminalShortcutLabels()[action].orEmpty() }

    val normalized: TerminalShortcutPreferences
        get() {
            val normalizedActions = (visibleActions + TerminalAction.entries)
                .distinct()
                .filter(TerminalAction.entries::contains)
            val normalizedCustomShortcuts = customShortcuts
                .mapNotNull(TerminalCustomShortcut::normalizedOrNull)
                .distinctBy(TerminalCustomShortcut::id)
                .take(MAX_CUSTOM_TERMINAL_SHORTCUTS)
            val defaultOrder = normalizedActions.map(::terminalBuiltInShortcutOrderKey) +
                normalizedCustomShortcuts.map { terminalCustomShortcutOrderKey(it.id) }
            val validOrderKeys = defaultOrder.toSet()
            return copy(
                labels = TerminalAction.entries.associateWith { action ->
                label(action).trim().take(12).ifBlank { defaultTerminalShortcutLabels()[action].orEmpty() }
                },
                visibleActions = normalizedActions,
                customShortcuts = normalizedCustomShortcuts,
                shortcutOrder = (shortcutOrder + defaultOrder)
                    .distinct()
                    .filter(validOrderKeys::contains),
            )
        }
}

sealed interface TerminalShortcutItem {
    val orderKey: String

    data class BuiltIn(val action: TerminalAction) : TerminalShortcutItem {
        override val orderKey: String = terminalBuiltInShortcutOrderKey(action)
    }

    data class Custom(val shortcut: TerminalCustomShortcut) : TerminalShortcutItem {
        override val orderKey: String = terminalCustomShortcutOrderKey(shortcut.id)
    }
}

internal fun terminalBuiltInShortcutOrderKey(action: TerminalAction): String = "builtin:${action.name}"

internal fun terminalCustomShortcutOrderKey(id: String): String = "custom:$id"

internal fun orderedTerminalShortcutItems(preferences: TerminalShortcutPreferences): List<TerminalShortcutItem> {
    val normalized = preferences.normalized
    val itemsByOrderKey = buildMap {
        normalized.visibleActions.forEach { action ->
            val item = TerminalShortcutItem.BuiltIn(action)
            put(item.orderKey, item)
        }
        normalized.customShortcuts.forEach { shortcut ->
            val item = TerminalShortcutItem.Custom(shortcut)
            put(item.orderKey, item)
        }
    }
    return normalized.shortcutOrder.mapNotNull(itemsByOrderKey::get)
}

const val MAX_CUSTOM_TERMINAL_SHORTCUTS = 24
const val MAX_TERMINAL_QUICK_INPUT_LENGTH = 4096

private fun defaultTerminalShortcutLabels(): Map<TerminalAction, String> = mapOf(
    TerminalAction.ESCAPE to "Esc",
    TerminalAction.TAB to "Tab",
    TerminalAction.ENTER to "Enter",
    TerminalAction.BACKSPACE to "⌫",
    TerminalAction.DELETE to "Del",
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
    TerminalAction.BACKSPACE,
    TerminalAction.DELETE,
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

/** Independent read-only scrollback anchored to the snapshot installed for one live session. */
data class TranscriptViewportState(
    val sessionId: UUID,
    val snapshotAnchorSequence: ULong,
    val initialBeforeLineId: ULong,
    val nextBeforeLineId: ULong? = initialBeforeLineId.takeIf { it > 0u },
    /**
     * Server-authenticated continuation. The initial request deliberately has
     * no opaque cursor and is selected by [snapshotAnchorSequence] plus
     * [initialBeforeLineId]; every ready page after that advances only with
     * the cursor returned alongside that page.
     */
    val nextCursor: String? = null,
    /** Pages are ordered oldest to newest; each page owns its own style and palette tables. */
    val pages: List<TerminalTranscriptPageV1> = emptyList(),
    /** Object identities already admitted to this viewport, used to reject replay under another cursor. */
    val loadedObjectIds: Set<UUID> = emptySet(),
    /** Opaque selectors already consumed by a successful merge, used to reject cursor loops. */
    val consumedCursors: Set<String> = emptySet(),
    val loading: Boolean = false,
    val error: String? = null,
    val hasMoreBefore: Boolean = nextBeforeLineId != null,
    /** Fences a page completing after the viewport was cleared or re-anchored. */
    val viewportGeneration: Long = 1,
) {
    init {
        require(snapshotAnchorSequence > 0u) { "transcript snapshot anchor must be positive" }
        require(nextBeforeLineId == null || nextBeforeLineId > 0u) { "transcript cursor must be positive" }
        require(nextCursor == null || nextCursor.isNotBlank()) { "transcript opaque cursor must not be blank" }
        require(consumedCursors.none(String::isBlank)) { "consumed transcript cursors must not be blank" }
        require(loadedObjectIds.size <= MAX_TRANSCRIPT_VIEWPORT_IDENTITIES) {
            "loaded transcript identity window exceeds renderer capacity"
        }
        require(consumedCursors.size <= MAX_TRANSCRIPT_VIEWPORT_IDENTITIES) {
            "consumed transcript cursor window exceeds renderer capacity"
        }
        require(hasMoreBefore == (nextBeforeLineId != null)) { "transcript pagination state is inconsistent" }
        require(viewportGeneration > 0) { "transcript viewport generation must be positive" }
        require(pages.all { it.snapshotAnchorSequence.toULong() == snapshotAnchorSequence }) {
            "transcript page anchor changed within a viewport"
        }
        require(pages.zipWithNext().all { (older, newer) -> older.lastLineId < newer.firstLineId }) {
            "transcript pages overlap or are out of order"
        }
        require(pages.size <= MAX_TRANSCRIPT_VIEWPORT_IDENTITIES) {
            "transcript page window exceeds renderer capacity"
        }
        require(transcriptViewportRowCount(pages) <= MAX_TRANSCRIPT_VIEWPORT_ROWS) {
            "transcript row window exceeds renderer capacity"
        }
    }
}

/** The state window and detached Termux renderer deliberately share one hard capacity. */
internal const val MAX_TRANSCRIPT_VIEWPORT_ROWS = TermuxTranscriptViewport.MAX_RENDERED_ROWS
internal const val MAX_TRANSCRIPT_VIEWPORT_IDENTITIES = MAX_TRANSCRIPT_VIEWPORT_ROWS

internal fun transcriptViewportRowCount(pages: List<TerminalTranscriptPageV1>): Int {
    var total = 0
    pages.forEach { page ->
        require(page.rows.size <= MAX_TRANSCRIPT_VIEWPORT_ROWS - total) {
            "transcript row window exceeds renderer capacity"
        }
        total += page.rows.size
    }
    return total
}

/**
 * Keeps the oldest rows after a prepend. A top-boundary user's visible anchor therefore survives,
 * while newer rows leave the detached history surface and remain available again at the live tail.
 */
internal fun retainOldestTranscriptPages(
    orderedPages: List<TerminalTranscriptPageV1>,
    maxRows: Int = MAX_TRANSCRIPT_VIEWPORT_ROWS,
): List<TerminalTranscriptPageV1> {
    require(maxRows > 0) { "transcript renderer capacity must be positive" }
    require(orderedPages.all { it.rows.size <= maxRows }) {
        "one transcript page exceeds the renderer capacity"
    }
    var retainedCount = orderedPages.size
    var retainedRows = orderedPages.sumOf { it.rows.size.toLong() }
    while (retainedCount > 0 && (retainedRows > maxRows.toLong() || retainedCount > maxRows)) {
        retainedCount -= 1
        retainedRows -= orderedPages[retainedCount].rows.size
    }
    return orderedPages.take(retainedCount)
}

enum class ProductBootstrapSource {
    EMPTY,
    NATIVE_FULL_PREFIX,
    LEGACY_REPLAY,
    TAIL_REPLAY,
    BOUND_SNAPSHOT,
    REMOTE_SNAPSHOT,
}

data class AgentUpdateEligibility(
    val pcId: UUID,
    val eligible: Boolean,
    val reasonCode: String? = null,
    val currentVersion: String,
    val currentBuild: Long? = null,
    val targetVersion: String? = null,
    val targetBuild: Long? = null,
    val required: Boolean = false,
    val releaseNotes: String? = null,
    val activeSessionCount: Int = 0,
)

data class AgentUpdateProgress(
    val id: UUID,
    val pcId: UUID,
    val currentVersion: String,
    val currentBuild: Long,
    val targetVersion: String,
    val targetBuild: Long,
    val status: String,
    val phase: String,
    val progressPercent: Int,
    val activeSessionCount: Int? = null,
    val errorCode: String? = null,
    val errorDetail: String? = null,
    val observedAgentVersion: String? = null,
    val observedAgentBuild: Long? = null,
) {
    val terminal: Boolean
        get() = status in setOf("SUCCEEDED", "FAILED", "CANCELLED", "EXPIRED", "ROLLED_BACK", "ROLLBACK_FAILED")

    val cancelable: Boolean
        get() = !terminal && phase in setOf("REQUESTED", "PRECHECKING", "DOWNLOADING", "VERIFIED")
}

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
    /** Legacy compatibility flag. The current Android product never renders an enrollment prompt. */
    val enrollmentConfirmationRequired: Boolean = false,
    val accountLabel: String? = null,
    val serverUrl: String? = null,
    val devices: List<DeviceSummary> = emptyList(),
    val pcs: List<PcSummary> = emptyList(),
    val sessions: List<SessionSummary> = emptyList(),
    val remoteFileBrowser: RemoteFileBrowserState = RemoteFileBrowserState(),
    val remoteDownloads: List<RemoteDownloadRecord> = emptyList(),
    val sessionStates: Map<UUID, RemoteSessionState> = emptyMap(),
    /** Detailed, non-sensitive failures for a single session open/control path. */
    val sessionOpenFailures: Map<UUID, SessionOpenFailure> = emptyMap(),
    /** Decrypted OSC/checkpoint titles, keyed only by their owning session UUID. */
    val sessionTitles: Map<UUID, String> = emptyMap(),
    val activity: List<ActivityEntry> = emptyList(),
    val pairingChallenge: PairingChallengeState? = null,
    val history: List<HistoryEntry> = emptyList(),
    val replayedHistoryObjectIds: Set<UUID> = emptySet(),
    val transcriptViewports: Map<UUID, TranscriptViewportState> = emptyMap(),
    /** Diagnostics only; these mirrors do not participate in ACK or bootstrap decisions. */
    val liveAcknowledgements: Map<UUID, ULong> = emptyMap(),
    val bootstrapSources: Map<UUID, ProductBootstrapSource> = emptyMap(),
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

/** Removes one session and every snapshot-owned reference to its history in one state transition. */
internal fun ProductSnapshot.withoutSession(sessionId: UUID): ProductSnapshot {
    val removedHistoryObjectIds = history.asSequence()
        .filter { it.sessionId == sessionId }
        .mapTo(mutableSetOf()) { it.historyObject.id }
    return copy(
        sessions = sessions.filterNot { it.id == sessionId },
        sessionStates = sessionStates - sessionId,
        sessionOpenFailures = sessionOpenFailures - sessionId,
        sessionTitles = sessionTitles - sessionId,
        history = history.filterNot { it.sessionId == sessionId },
        replayedHistoryObjectIds = replayedHistoryObjectIds - removedHistoryObjectIds,
        transcriptViewports = transcriptViewports - sessionId,
        liveAcknowledgements = liveAcknowledgements - sessionId,
        bootstrapSources = bootstrapSources - sessionId,
    )
}

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
    /** Checks the public AppShip update decision without an account token. */
    suspend fun checkForUpdate(force: Boolean = true): ProductUpdateStatus = ProductUpdateStatus()
    /** Downloads and verifies the APK selected by [checkForUpdate]. */
    suspend fun downloadUpdate() = Unit
    /** Hides the current update prompt without discarding its verified download state. */
    fun dismissUpdate() = Unit
    /** Opens the policy-driven hosted registration page inside the app-owned OIDC surface. */
    suspend fun beginEmbeddedRegistration()
    suspend fun beginEmbeddedAccountSetup()
    suspend fun confirmEnrollment()
    suspend fun cancelEnrollment()
    suspend fun configureServer(url: String)
    suspend fun signOut()
    suspend fun approveDevice(deviceId: UUID)
    suspend fun completePairing(challengeId: String, code: String, challengeNonce: String)
    suspend fun purchaseProduct(productId: String)
    suspend fun restorePurchases()
    suspend fun revokeDevice(deviceId: UUID)
    /** Reads Server eligibility; the client never derives a target release locally. */
    suspend fun agentUpdateEligibility(pcId: UUID): AgentUpdateEligibility =
        error("Remote Agent updates are unavailable")
    /** Recovers the update created by this Android device after process restart. */
    suspend fun activeAgentUpdate(pcId: UUID): AgentUpdateProgress? = null
    /** Creates one Server-pinned update and reports each authoritative state until terminal. */
    suspend fun runAgentUpdate(
        pcId: UUID,
        expectedTargetBuild: Long,
        onProgress: (AgentUpdateProgress) -> Unit,
    ): AgentUpdateProgress = error("Remote Agent updates are unavailable")
    suspend fun monitorAgentUpdate(
        pcId: UUID,
        updateId: UUID,
        onProgress: (AgentUpdateProgress) -> Unit,
    ): AgentUpdateProgress = error("Remote Agent updates are unavailable")
    suspend fun cancelAgentUpdate(pcId: UUID, updateId: UUID): AgentUpdateProgress =
        error("Remote Agent updates are unavailable")
    suspend fun requestControl(sessionId: UUID)
    suspend fun releaseControl(sessionId: UUID)
    /** Release all active server control leases while keeping read-only viewing alive. */
    suspend fun releaseAllControlLeases() = Unit
    /** Lists one Windows directory page. An empty path requests the drive roots. */
    suspend fun browseRemoteDirectory(
        pcId: UUID,
        pcName: String,
        path: String,
        offset: Long? = null,
    ): Unit = error("Remote file browsing is unavailable")
    /** Starts the server DOWNLOAD operation only after SAF returned [targetUri]. */
    fun startRemoteDownload(selection: RemoteFileDownloadSelection, targetUri: Uri) {
        error("Remote file downloads are unavailable")
    }
    suspend fun cancelRemoteDownload(recordId: UUID): Unit = error("Remote file downloads are unavailable")
    fun retryRemoteDownload(recordId: UUID, targetUri: Uri) {
        error("Remote file downloads are unavailable")
    }
    /** Removes download-center metadata. It never deletes the completed SAF document. */
    fun deleteRemoteDownloadRecord(recordId: UUID) = Unit
    /** Opens one Agent-advertised Windows terminal command in [path]. */
    suspend fun openRemoteShell(pcId: UUID, path: String, command: TerminalCommandCapability): UUID =
        error("Remote terminal launch is unavailable")
    suspend fun openSession(sessionId: UUID)
    /** Closes only this Android viewer/transport; the Windows shell remains running. */
    suspend fun closeSessionView(sessionId: UUID) = Unit
    fun terminalEngine(sessionId: UUID): TerminalViewportEngine?
    fun historyEngine(historyObjectId: UUID): TerminalViewportEngine?
    suspend fun stopSession(sessionId: UUID)
    suspend fun deleteSession(sessionId: UUID)
    suspend fun sendTerminalAction(sessionId: UUID, action: TerminalAction)
    suspend fun sendTerminalKeyCombination(sessionId: UUID, combination: String)
    suspend fun sendPaste(sessionId: UUID, text: String)
    suspend fun sendCommand(sessionId: UUID, text: String)
    suspend fun replayHistory(entry: HistoryEntry)
    suspend fun loadTranscriptBefore(sessionId: UUID)
    fun clearTranscript(sessionId: UUID)
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
    override suspend fun checkForUpdate(force: Boolean): ProductUpdateStatus = ProductUpdateStatus(
        decision = ProductUpdateDecision.NOT_CONFIGURED,
        message = "Update service is not configured",
    )
    override suspend fun beginEmbeddedRegistration() = unavailable()
    override suspend fun beginEmbeddedAccountSetup() = unavailable()
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
    override suspend fun browseRemoteDirectory(pcId: UUID, pcName: String, path: String, offset: Long?) = unavailable()
    override fun startRemoteDownload(selection: RemoteFileDownloadSelection, targetUri: Uri) = unavailable()
    override suspend fun cancelRemoteDownload(recordId: UUID) = unavailable()
    override fun retryRemoteDownload(recordId: UUID, targetUri: Uri) = unavailable()
    override fun deleteRemoteDownloadRecord(recordId: UUID) = Unit
    override suspend fun openRemoteShell(pcId: UUID, path: String, command: TerminalCommandCapability): UUID = unavailable()
    override suspend fun openSession(sessionId: UUID) = unavailable()
    override suspend fun closeSessionView(sessionId: UUID) = Unit
    override fun terminalEngine(sessionId: UUID): TerminalViewportEngine? = null
    override fun historyEngine(historyObjectId: UUID): TerminalViewportEngine? = null
    override suspend fun stopSession(sessionId: UUID) = unavailable()
    override suspend fun deleteSession(sessionId: UUID) = unavailable()
    override suspend fun sendTerminalAction(sessionId: UUID, action: TerminalAction) = unavailable()
    override suspend fun sendTerminalKeyCombination(sessionId: UUID, combination: String) = unavailable()
    override suspend fun sendPaste(sessionId: UUID, text: String) = unavailable()
    override suspend fun sendCommand(sessionId: UUID, text: String) = unavailable()
    override suspend fun replayHistory(entry: HistoryEntry) = unavailable()
    override suspend fun loadTranscriptBefore(sessionId: UUID) = unavailable()
    override fun clearTranscript(sessionId: UUID) = Unit

    override fun close() = Unit

    private fun unavailable(): Nothing = error("Sign in and configure a TermLink server to continue")
}

object ProductFormatter {
    fun quotaLabel(used: Int, limit: Int): String =
        when {
            limit < 0 -> "$used / unlimited"
            limit > 0 -> "$used / $limit"
            else -> "$used / unavailable"
        }

    fun trustLabel(value: dev.termlink.model.DeviceTrustState): String =
        value.name.lowercase().replace('_', ' ').replaceFirstChar { it.uppercase() }

    fun sessionLabel(value: dev.termlink.model.SessionState): String =
        value.name.lowercase().replaceFirstChar { it.uppercase() }

    fun objectKindLabel(value: HistoryObject.Kind): String =
        value.name.lowercase().replaceFirstChar { it.uppercase() }
}
