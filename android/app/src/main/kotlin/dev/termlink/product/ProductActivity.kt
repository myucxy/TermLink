package dev.termlink.product

import android.content.Intent
import android.graphics.Color as AndroidColor
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.util.Log
import android.view.Choreographer
import android.view.View
import android.view.KeyEvent as AndroidKeyEvent
import android.view.inputmethod.InputMethodManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.lifecycleScope
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.basicMarquee
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.calculateEndPadding
import androidx.compose.foundation.layout.calculateStartPadding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.Login
import androidx.compose.material.icons.automirrored.filled.Logout
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.DeleteForever
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.LaptopWindows
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material.icons.filled.Terminal
import androidx.compose.material.icons.filled.Timeline
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CenterAlignedTopAppBar
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.VerticalDivider
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.key
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionOnScreen
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.harness.R
import dev.termlink.harness.BuildConfig
import dev.termlink.model.SessionSummary
import dev.termlink.session.RemoteSessionState
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.terminal.api.ArgbColor
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.termux.TermuxTerminalEngine
import dev.termlink.terminal.termux.TermuxTranscriptViewport
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale
import org.json.JSONArray
import org.json.JSONObject
import kotlinx.coroutines.launch
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import androidx.core.view.WindowCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.content.FileProvider
import java.io.File
import java.security.MessageDigest
import kotlin.math.roundToInt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlin.coroutines.resume

private sealed class DestructiveProductAction {
    data object SignOut : DestructiveProductAction()
}

internal val ProductShape = RoundedCornerShape(8.dp)
private val ProductBackground = Color(0xFF0B0F14)
private val ProductSurface = Color(0xFF121820)
private val ProductSurfaceVariant = Color(0xFF1B2430)
private val ProductPrimary = Color(0xFF9CB2FF)
private val ProductPositive = Color(0xFF65D6A1)
private val ProductWarning = Color(0xFFF1C96B)
private val ProductDanger = Color(0xFFFF8C91)
private val ProductMuted = Color(0xFFAAB5C5)

private val productColors = androidx.compose.material3.darkColorScheme(
    primary = ProductPrimary,
    onPrimary = Color(0xFF14203D),
    secondary = ProductPositive,
    onSecondary = Color(0xFF062A1A),
    tertiary = ProductWarning,
    onTertiary = Color(0xFF2A2108),
    background = ProductBackground,
    surface = ProductSurface,
    surfaceVariant = ProductSurfaceVariant,
    onSurface = Color(0xFFE9EEF5),
    onSurfaceVariant = ProductMuted,
    outline = Color(0xFF465363),
    error = ProductDanger,
)

private val productLightColors = lightColorScheme(
    primary = Color(0xFF4659A8),
    onPrimary = Color.White,
    secondary = Color(0xFF176B45),
    onSecondary = Color.White,
    tertiary = Color(0xFF715700),
    onTertiary = Color.White,
    background = Color(0xFFF7F8FC),
    surface = Color(0xFFFFFFFF),
    surfaceVariant = Color(0xFFE6E9F2),
    onSurface = Color(0xFF171A24),
    onSurfaceVariant = Color(0xFF4E5668),
    outline = Color(0xFF737B8E),
    error = Color(0xFFB3261E),
)

private const val PRODUCT_PREFERENCES = "termlink_product_preferences"
private const val PRODUCT_THEME_KEY = "theme_preference"
private const val PRODUCT_PRIMARY_DESTINATION_KEY = "primary_destination"
private const val PRODUCT_SELECTED_SESSION_ID_KEY = "selected_session_id"
private const val TERMINAL_FONT_SIZE_KEY = "terminal_font_size_sp"
private const val TERMINAL_LINE_SPACING_KEY = "terminal_line_spacing"
private const val TERMINAL_FOREGROUND_KEY = "terminal_foreground_argb"
private const val TERMINAL_BACKGROUND_KEY = "terminal_background_argb"
private const val TERMINAL_SHORTCUT_PREFIX = "terminal_shortcut_"
private const val TERMINAL_SHORTCUT_ORDER_KEY = "terminal_shortcut_order"
private const val TERMINAL_CUSTOM_SHORTCUTS_KEY = "terminal_custom_shortcuts_v1"
private const val TERMINAL_UNIFIED_SHORTCUT_ORDER_KEY = "terminal_shortcut_order_v2"
private const val REMOTE_DOWNLOAD_PC_ID_KEY = "remote_download_pc_id"
private const val REMOTE_DOWNLOAD_PC_NAME_KEY = "remote_download_pc_name"
private const val REMOTE_DOWNLOAD_PATH_KEY = "remote_download_path"
private const val REMOTE_DOWNLOAD_FILENAME_KEY = "remote_download_filename"
private const val REMOTE_DOWNLOAD_DIRECTORY_ZIP_KEY = "remote_download_directory_zip"
private const val REMOTE_DOWNLOAD_RETRY_ID_KEY = "remote_download_retry_id"

internal fun Bundle.putRemoteDownloadSelection(selection: RemoteFileDownloadSelection) {
    putString(REMOTE_DOWNLOAD_PC_ID_KEY, selection.pcId.toString())
    putString(REMOTE_DOWNLOAD_PC_NAME_KEY, selection.pcName)
    putString(REMOTE_DOWNLOAD_PATH_KEY, selection.remotePath)
    putString(REMOTE_DOWNLOAD_FILENAME_KEY, selection.suggestedFilename)
    putBoolean(REMOTE_DOWNLOAD_DIRECTORY_ZIP_KEY, selection.directoryZip)
    putString(REMOTE_DOWNLOAD_RETRY_ID_KEY, selection.retryRecordId?.toString())
}

internal fun Bundle.remoteDownloadSelection(): RemoteFileDownloadSelection? = runCatching {
    RemoteFileDownloadSelection(
        pcId = java.util.UUID.fromString(requireNotNull(getString(REMOTE_DOWNLOAD_PC_ID_KEY))),
        pcName = requireNotNull(getString(REMOTE_DOWNLOAD_PC_NAME_KEY)),
        remotePath = requireNotNull(getString(REMOTE_DOWNLOAD_PATH_KEY)),
        suggestedFilename = requireNotNull(getString(REMOTE_DOWNLOAD_FILENAME_KEY)),
        directoryZip = getBoolean(REMOTE_DOWNLOAD_DIRECTORY_ZIP_KEY),
        retryRecordId = getString(REMOTE_DOWNLOAD_RETRY_ID_KEY)?.let(java.util.UUID::fromString),
    )
}.getOrNull()

internal fun terminalTestTag(sessionId: java.util.UUID): String = "terminal:$sessionId"
internal fun terminalDockTestTag(sessionId: java.util.UUID): String = "terminal-dock:$sessionId"
internal fun terminalCommandTestTag(action: String): String = "terminal-command:$action"
internal fun transcriptOverlayTestTag(sessionId: java.util.UUID): String = "history-overlay:$sessionId"
internal fun transcriptViewportTestTag(sessionId: java.util.UUID): String = "history-viewport:$sessionId"
internal fun transcriptLoadingTestTag(sessionId: java.util.UUID): String = "history-loading:$sessionId"
internal fun transcriptRetryTestTag(sessionId: java.util.UUID): String = "history-retry:$sessionId"
internal fun transcriptReturnLiveTestTag(sessionId: java.util.UUID): String = "history-return-live:$sessionId"

internal fun shouldShowTranscriptOverlay(
    overlayRequested: Boolean,
    state: TranscriptViewportState?,
): Boolean = overlayRequested || state?.let {
    it.loading || it.error != null || it.pages.isNotEmpty()
} == true

internal data class TerminalInputResumeEffect(
    val scrollToLatest: Boolean,
    val closeDetachedOverlay: Boolean,
)

/**
 * A rejected input must leave both live and detached history viewports exactly
 * where the user put them. Once input is accepted, the live renderer resumes
 * cursor following and an actually visible detached history projection closes.
 */
internal fun terminalInputResumeEffect(
    accepted: Boolean,
    detachedOverlayActive: Boolean,
): TerminalInputResumeEffect = TerminalInputResumeEffect(
    scrollToLatest = accepted,
    closeDetachedOverlay = accepted && detachedOverlayActive,
)

/** Runs the completion hook only after the transport-facing operation returns successfully. */
internal suspend fun dispatchAcceptedTerminalInput(
    operation: suspend () -> Unit,
    onAccepted: () -> Unit,
) {
    operation()
    onAccepted()
}

/**
 * Product-level bridge for accepted input paths which deliberately bypass the
 * terminal engine (currently confirmed command submission). Registrations are
 * tokenized so an old AndroidView disposal cannot unregister its replacement.
 */
internal class TerminalInputResumeRegistry {
    private data class Entry(val token: Long, val resume: () -> Unit)

    private val entries = mutableMapOf<java.util.UUID, Entry>()
    private var nextToken = 0L

    fun register(sessionId: java.util.UUID, resume: () -> Unit): AutoCloseable {
        val token = synchronized(entries) {
            check(nextToken < Long.MAX_VALUE) { "Terminal input resume registration exhausted" }
            (++nextToken).also { entries[sessionId] = Entry(it, resume) }
        }
        return AutoCloseable {
            synchronized(entries) {
                if (entries[sessionId]?.token == token) entries.remove(sessionId)
            }
        }
    }

    fun resume(sessionId: java.util.UUID): Boolean {
        val callback = synchronized(entries) { entries[sessionId]?.resume } ?: return false
        callback()
        return true
    }
}

internal fun canRequestOlderTranscript(
    state: TranscriptViewportState?,
    localLoadingGuard: Boolean,
    rendererFailed: Boolean = false,
): Boolean = state != null && state.hasMoreBefore && !state.loading && !localLoadingGuard && !rendererFailed

internal enum class TranscriptRetryAction {
    REAPPLY_CURRENT_WINDOW,
    LOAD_OLDER_PAGE,
    NONE,
}

/** Renderer retries are local and must never consume the authenticated history cursor. */
internal fun transcriptRetryAction(
    state: TranscriptViewportState?,
    rendererFailed: Boolean,
    localLoadingGuard: Boolean,
): TranscriptRetryAction = when {
    rendererFailed && !state?.pages.isNullOrEmpty() -> TranscriptRetryAction.REAPPLY_CURRENT_WINDOW
    canRequestOlderTranscript(state, localLoadingGuard) -> TranscriptRetryAction.LOAD_OLDER_PAGE
    else -> TranscriptRetryAction.NONE
}

internal fun dispatchTranscriptRetry(
    action: TranscriptRetryAction,
    reapplyCurrentWindow: () -> Unit,
    loadOlderPage: () -> Unit,
) {
    when (action) {
        TranscriptRetryAction.REAPPLY_CURRENT_WINDOW -> reapplyCurrentWindow()
        TranscriptRetryAction.LOAD_OLDER_PAGE -> loadOlderPage()
        TranscriptRetryAction.NONE -> Unit
    }
}

/** Converts malformed/depth-exhausted renderer projections into durable UI state for this frame. */
internal fun applyTranscriptViewportPagesSafely(
    viewport: TermuxTranscriptViewport,
    pages: List<TerminalTranscriptPageV1>,
    authoritativeColumns: Int,
): Exception? = try {
    if (pages.isEmpty()) viewport.clear(authoritativeColumns)
    else viewport.replacePages(pages, authoritativeColumns)
    null
} catch (error: Exception) {
    error
}

internal fun newestNewNonExitedSession(
    sessions: List<SessionSummary>,
    existingSessionIds: Set<java.util.UUID>,
): SessionSummary? = sessions
    .asSequence()
    .filter { it.id !in existingSessionIds && it.online }
    .maxByOrNull(SessionSummary::createdAt)

internal fun displaySessionTitle(session: SessionSummary, runtimeTitle: String?): String =
    runtimeTitle?.takeIf(String::isNotBlank) ?: session.shellType

/** Debug receiver boundary. No exported component references this object in release builds. */
internal object TailFirstE2EProductBridge {
    internal fun interface Controller {
        suspend fun execute(
            operation: String,
            requestId: String,
            arguments: Map<String, String>,
        ): JsonObject
    }

    @Volatile
    private var controller: Controller? = null
    @Volatile
    private var selectedSessionId: java.util.UUID? = null
    @Volatile
    private var firstMarker: String? = null
    @Volatile
    private var lastMarker: String? = null
    @Volatile
    private var liveMarker: String? = null

    fun register(value: Controller) {
        if (BuildConfig.DEBUG) controller = value
    }

    fun unregister() {
        controller = null
        selectedSessionId = null
        firstMarker = null
        lastMarker = null
        liveMarker = null
    }

    fun selectSession(sessionId: java.util.UUID) {
        selectedSessionId = sessionId
    }

    fun selectedSession(): java.util.UUID? = selectedSessionId

    fun rememberMarkers(arguments: Map<String, String>) {
        arguments["first_marker"]?.let { firstMarker = it }
        arguments["last_marker"]?.let { lastMarker = it }
        arguments["live_marker"]?.let { liveMarker = it }
    }

    fun markers(): Triple<String?, String?, String?> = Triple(firstMarker, lastMarker, liveMarker)

    suspend fun execute(
        operation: String,
        requestId: String,
        arguments: Map<String, String>,
    ): JsonObject {
        val active = controller.takeIf { BuildConfig.DEBUG }
        return active?.execute(operation, requestId, arguments)
            ?: tailFirstFailure(requestId, operation, "BRIDGE_UNAVAILABLE")
    }
}

internal data class TailFirstPixelBounds(
    val left: Int,
    val top: Int,
    val right: Int,
    val bottom: Int,
) {
    val isReportable: Boolean
        get() = left >= 0 && top >= 0 && right > left && bottom > top
}

internal data class TailFirstImeLayoutSample(
    val imeVisible: Boolean,
    val displayBounds: TailFirstPixelBounds? = null,
    val terminalBounds: TailFirstPixelBounds?,
    val bottomBarBounds: TailFirstPixelBounds?,
    val imeBounds: TailFirstPixelBounds?,
) {
    val isReady: Boolean
        get() {
            val terminal = terminalBounds
            val dock = bottomBarBounds
            val keyboard = imeBounds
            if (!imeVisible || terminal?.isReportable != true ||
                dock?.isReportable != true || keyboard?.isReportable != true
            ) return false
            val terminalToDockGap = dock.top - terminal.bottom
            val dockToImeGap = keyboard.top - dock.bottom
            val contained = displayBounds?.takeIf(TailFirstPixelBounds::isReportable)?.let { display ->
                listOf(terminal, dock, keyboard).all { bounds ->
                    bounds.left >= display.left && bounds.top >= display.top &&
                        bounds.right <= display.right && bounds.bottom <= display.bottom
                }
            } ?: true
            return contained &&
                terminalToDockGap in 0..TAIL_FIRST_IME_MAX_LAYOUT_GAP_PIXELS &&
                dockToImeGap in 0..TAIL_FIRST_IME_MAX_LAYOUT_GAP_PIXELS
        }
}

internal fun imeDockGapPixels(sample: TailFirstImeLayoutSample): Int? {
    val dock = sample.bottomBarBounds ?: return null
    val keyboard = sample.imeBounds ?: return null
    return keyboard.top - dock.bottom
}

internal data class TailFirstImeStabilityDiagnostics(
    val observedFrames: Int,
    val stableFrames: Int,
    val requiredStableFrames: Int,
    val latestSample: TailFirstImeLayoutSample?,
)

internal class TailFirstImeStabilityTracker(
    private val requiredStableFrames: Int = TAIL_FIRST_IME_REQUIRED_STABLE_FRAMES,
) {
    init {
        require(requiredStableFrames >= 2)
    }

    private var previousReadySample: TailFirstImeLayoutSample? = null
    private var latestSample: TailFirstImeLayoutSample? = null
    private var observedFrames = 0
    private var stableFrames = 0

    fun recordFrame(sample: TailFirstImeLayoutSample): Boolean {
        observedFrames += 1
        latestSample = sample
        if (!sample.isReady) {
            previousReadySample = null
            stableFrames = 0
            return false
        }
        stableFrames = if (sample == previousReadySample) stableFrames + 1 else 1
        previousReadySample = sample
        return stableFrames >= requiredStableFrames
    }

    fun diagnostics(): TailFirstImeStabilityDiagnostics = TailFirstImeStabilityDiagnostics(
        observedFrames = observedFrames,
        stableFrames = stableFrames,
        requiredStableFrames = requiredStableFrames,
        latestSample = latestSample,
    )
}

private class TailFirstImeStabilityTimeout(
    val diagnostics: TailFirstImeStabilityDiagnostics,
) : RuntimeException("IME layout did not stabilize")

private data class TailFirstSurfaceObservation(
    val historyOverlay: Boolean,
    val visibleText: String,
    val liveText: String,
    val viewportSignature: String,
    val displayBounds: TailFirstPixelBounds? = null,
    val terminalBounds: TailFirstPixelBounds? = null,
    val bottomBarBounds: TailFirstPixelBounds? = null,
    val imeBounds: TailFirstPixelBounds? = null,
    val imeVisible: Boolean = false,
) {
    fun imeLayoutSample(): TailFirstImeLayoutSample = TailFirstImeLayoutSample(
        imeVisible = imeVisible,
        displayBounds = displayBounds,
        terminalBounds = terminalBounds,
        bottomBarBounds = bottomBarBounds,
        imeBounds = imeBounds,
    )
}

private const val TAIL_FIRST_IME_REQUIRED_STABLE_FRAMES = 2
private const val TAIL_FIRST_IME_MAX_LAYOUT_GAP_PIXELS = 1
private const val TAIL_FIRST_IME_STABILITY_TIMEOUT_MILLIS = 10_000L
private const val TAIL_FIRST_E2E_LOG_TAG = "TermLinkTailFirst"

private interface TailFirstTerminalSurfaceController {
    fun observe(): TailFirstSurfaceObservation
    fun pageUp()
    /** Returns true only when this call crossed the live terminal's top boundary. */
    fun openHistory(): Boolean
    fun closeHistory()
    fun showIme()
}

private object TailFirstTerminalSurfaceRegistry {
    private val surfaces = mutableMapOf<java.util.UUID, TailFirstTerminalSurfaceController>()

    @Synchronized
    fun register(sessionId: java.util.UUID, controller: TailFirstTerminalSurfaceController) {
        surfaces[sessionId] = controller
    }

    @Synchronized
    fun unregister(sessionId: java.util.UUID) {
        surfaces.remove(sessionId)
    }

    @Synchronized
    fun get(sessionId: java.util.UUID): TailFirstTerminalSurfaceController? = surfaces[sessionId]
}

private fun loadThemePreference(context: android.content.Context): ProductThemePreference =
    ProductThemePreference.fromWireValue(
        context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
            .getString(PRODUCT_THEME_KEY, ProductThemePreference.SYSTEM.wireValue),
    )

private fun saveThemePreference(context: android.content.Context, preference: ProductThemePreference) {
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putString(PRODUCT_THEME_KEY, preference.wireValue)
        .apply()
}

class ProductActivity : ComponentActivity() {
    private lateinit var repository: ProductionProductRepository
    private var productForeground by mutableStateOf(false)
    private var productForegroundGeneration by mutableStateOf(0L)
    private var pendingUpdateApk: File? = null
    private var pendingRemoteDownloadSelection: RemoteFileDownloadSelection? = null
    private val authorizationLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        repository.handleAuthorizationResult(result.data)
    }
    private val installPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) {
        val apk = pendingUpdateApk
        if (apk != null && packageManager.canRequestPackageInstalls()) {
            launchSystemApkInstaller(apk)
        } else {
            pendingUpdateApk = null
        }
    }
    private val remoteDocumentLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { result ->
        val selection = pendingRemoteDownloadSelection
        pendingRemoteDownloadSelection = null
        val data = result.data
        val uri = data?.data
        if (result.resultCode != android.app.Activity.RESULT_OK || selection == null || uri == null) {
            return@registerForActivityResult
        }
        val takeFlags = data.flags and (
            Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
            )
        runCatching {
            if (takeFlags != 0) contentResolver.takePersistableUriPermission(uri, takeFlags)
        }
        runCatching {
            val retryId = selection.retryRecordId
            if (retryId == null) repository.startRemoteDownload(selection, uri)
            else repository.retryRemoteDownload(retryId, uri)
        }.onFailure {
            lifecycleScope.launch(Dispatchers.IO) {
                cleanupIncompleteRemoteDocument(contentResolver, uri)
                releaseRemoteDocumentPermission(contentResolver, uri)
            }
            Toast.makeText(this, R.string.remote_download_start_failed, Toast.LENGTH_LONG).show()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        repository = ProductionProductRepository(this, authorizationLauncher::launch)
        pendingRemoteDownloadSelection = savedInstanceState?.remoteDownloadSelection()
        enableEdgeToEdge()
        setContent {
            val context = LocalContext.current
            var themePreference by rememberSaveable { mutableStateOf(loadThemePreference(context)) }
            var terminalPreferences by remember { mutableStateOf(loadTerminalDisplayPreferences(context)) }
            var shortcutPreferences by remember { mutableStateOf(loadTerminalShortcutPreferences(context)) }
            val systemDark = isSystemInDarkTheme()
            val dark = when (themePreference) {
                ProductThemePreference.SYSTEM -> systemDark
                ProductThemePreference.LIGHT -> false
                ProductThemePreference.DARK -> true
            }
            val colors = if (dark) productColors else productLightColors
            SideEffect {
                val controller = WindowCompat.getInsetsController(window, window.decorView)
                controller.isAppearanceLightStatusBars = !dark
                controller.isAppearanceLightNavigationBars = !dark
            }
            MaterialTheme(colorScheme = colors) {
                ProductApp(
                    repository = repository,
                    foreground = productForeground,
                    foregroundGeneration = productForegroundGeneration,
                    themePreference = themePreference,
                    onThemePreferenceChanged = {
                        themePreference = it
                        saveThemePreference(context, it)
                    },
                    terminalPreferences = terminalPreferences,
                    onTerminalPreferencesChanged = {
                        terminalPreferences = it.normalized
                        saveTerminalDisplayPreferences(context, terminalPreferences)
                    },
                    shortcutPreferences = shortcutPreferences,
                    onShortcutPreferencesChanged = {
                        shortcutPreferences = it.normalized
                        saveTerminalShortcutPreferences(context, shortcutPreferences)
                    },
                    onInstallUpdate = ::requestUpdateInstall,
                    onCreateRemoteDocument = ::requestRemoteDownloadDocument,
                )
            }
        }
    }

    override fun onStop() {
        productForeground = false
        // Backgrounding must give up every remote control lease, but it must
        // not terminate the Windows shell or the read-only viewing stream.
        if (::repository.isInitialized) {
            lifecycleScope.launch {
                try {
                    repository.releaseAllControlLeases()
                } catch (error: CancellationException) {
                    throw error
                } catch (_: Throwable) {
                    // The repository publishes actionable release failures.
                }
            }
        }
        super.onStop()
    }

    override fun onStart() {
        super.onStart()
        // A Boolean can be conflated by Compose when stop/start happen before
        // the next frame. The monotonic generation preserves every foreground
        // entry and therefore gives the selected session one fresh lease
        // opportunity after its background release.
        productForegroundGeneration = nextForegroundGeneration(productForegroundGeneration)
        productForeground = true
        if (::repository.isInitialized) {
            lifecycleScope.launch {
                // Match NuanYa's startup/foreground check: the repository
                // throttles this to one request per minute and reuses an
                // ETag-bound response when the cache is still fresh.
                runCatching { repository.checkForUpdate(force = false) }
            }
        }
    }

    override fun onDestroy() {
        repository.close()
        super.onDestroy()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        pendingRemoteDownloadSelection?.let { outState.putRemoteDownloadSelection(it) }
        super.onSaveInstanceState(outState)
    }

    private fun requestRemoteDownloadDocument(selection: RemoteFileDownloadSelection) {
        pendingRemoteDownloadSelection = selection
        val intent = Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
            addCategory(Intent.CATEGORY_OPENABLE)
            type = if (selection.directoryZip) "application/zip" else "application/octet-stream"
            putExtra(Intent.EXTRA_TITLE, selection.suggestedFilename)
            addFlags(
                Intent.FLAG_GRANT_READ_URI_PERMISSION or
                    Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                    Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION,
            )
        }
        runCatching { remoteDocumentLauncher.launch(intent) }
            .onFailure {
                pendingRemoteDownloadSelection = null
                Toast.makeText(this, R.string.remote_document_picker_unavailable, Toast.LENGTH_LONG).show()
            }
    }

    private fun requestUpdateInstall(apkPath: String) {
        val updateDirectory = File(cacheDir, "updates").canonicalFile
        val apk = runCatching { File(apkPath).canonicalFile }.getOrNull()
        val contained = apk != null &&
            apk.extension.equals("apk", ignoreCase = true) &&
            apk.isFile &&
            apk.path.startsWith(updateDirectory.path + File.separator)
        if (!contained) {
            Toast.makeText(this, R.string.update_install_file_missing, Toast.LENGTH_LONG).show()
            return
        }
        if (packageManager.canRequestPackageInstalls()) {
            launchSystemApkInstaller(apk)
        } else {
            pendingUpdateApk = apk
            installPermissionLauncher.launch(
                Intent(
                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:$packageName"),
                ),
            )
        }
    }

    private fun launchSystemApkInstaller(apk: File) {
        pendingUpdateApk = null
        val contentUri = FileProvider.getUriForFile(this, "$packageName.fileprovider", apk)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(contentUri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        runCatching { startActivity(intent) }
            .onFailure {
                Toast.makeText(this, R.string.update_installer_unavailable, Toast.LENGTH_LONG).show()
            }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProductApp(
    repository: ProductRepository,
    foreground: Boolean = true,
    foregroundGeneration: Long = if (foreground) 1L else 0L,
    themePreference: ProductThemePreference = ProductThemePreference.SYSTEM,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit = {},
    terminalPreferences: TerminalDisplayPreferences = TerminalDisplayPreferences(),
    onTerminalPreferencesChanged: (TerminalDisplayPreferences) -> Unit = {},
    shortcutPreferences: TerminalShortcutPreferences = TerminalShortcutPreferences(),
    onShortcutPreferencesChanged: (TerminalShortcutPreferences) -> Unit = {},
    onInstallUpdate: (String) -> Unit = {},
    onCreateRemoteDocument: (RemoteFileDownloadSelection) -> Unit = {},
) {
    val context = LocalContext.current
    val snapshot by repository.snapshot.collectAsStateCompat()
    val restoredNavigation = remember(context) { loadProductNavigationState(context) }
    var destinationName by rememberSaveable {
        mutableStateOf(restoredNavigation.primaryDestination.destination().name)
    }
    val storedDestination = runCatching { ProductDestination.valueOf(destinationName) }
        .getOrDefault(ProductDestination.DEVICES)
    // A destination saved by an older build must not resurrect hidden product
    // surfaces after an upgrade.
    val destination = storedDestination.takeIf { it.isVisibleInProductShell } ?: ProductDestination.DEVICES
    var selectedSessionId by rememberSaveable { mutableStateOf(restoredNavigation.selectedSessionId) }
    var selectedHistoryId by rememberSaveable { mutableStateOf<String?>(null) }
    var selectedRemotePcId by rememberSaveable { mutableStateOf<String?>(null) }
    var selectedRemotePcName by rememberSaveable { mutableStateOf("") }
    var commandSearchOpen by remember { mutableStateOf(false) }
    var pasteSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    val terminalInputResumeRegistry = remember { TerminalInputResumeRegistry() }
    var pendingStopSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    var pendingDeleteSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    var refreshInFlight by remember { mutableStateOf(false) }
    var signOutConfirmation by remember { mutableStateOf(false) }
    var registrationOpen by remember { mutableStateOf(false) }
    val deletableSessionId = pendingDeleteSessionId?.takeIf { sessionId ->
        val session = snapshot.sessions.firstOrNull { it.id == sessionId } ?: return@takeIf false
        !isDeviceSessionActive(session.online)
    }
    val stoppableSessionId = pendingStopSessionId?.takeIf { sessionId ->
        val session = snapshot.sessions.firstOrNull { it.id == sessionId } ?: return@takeIf false
        isDeviceSessionActive(session.online)
    }
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }
    val unavailableOperationMessage = stringResource(R.string.unknown_server_operation)
    val activeSessionDeleteMessage = stringResource(R.string.failure_session_active)
    val sessionCloseRequestSentMessage = stringResource(R.string.session_close_request_sent)
    val sessionCloseRequestFailedMessage = stringResource(R.string.failure_session_close_request)

    LaunchedEffect(pendingDeleteSessionId, deletableSessionId) {
        // A refresh may make the PC/session active while the confirmation is
        // open. In that case the destructive affordance disappears as well.
        if (pendingDeleteSessionId != null && deletableSessionId == null) {
            pendingDeleteSessionId = null
        }
    }
    LaunchedEffect(pendingStopSessionId, stoppableSessionId) {
        // The catalog may report that the shell ended while confirmation is
        // open. Do not send a stale close request in that case.
        if (pendingStopSessionId != null && stoppableSessionId == null) {
            pendingStopSessionId = null
        }
    }

    fun navigateTo(next: ProductDestination) {
        if (next.isVisibleInProductShell) {
            destinationName = next.name
            if (next == next.primaryDestination().destination()) {
                saveProductPrimaryDestination(context, next.primaryDestination())
            }
        }
    }

    fun navigateToPrimary(next: ProductPrimaryDestination) {
        navigateTo(next.destination())
    }

    BackHandler(enabled = destination.primaryDestination().destination() != destination) {
        navigateTo(destination.primaryDestination().destination())
    }

    fun runOperation(operation: suspend () -> Unit, onComplete: () -> Unit) {
        scope.launch {
            try {
                operation()
            } catch (error: Throwable) {
                snackbarHostState.showSnackbar(
                    safeOperationMessage(error, unavailableOperationMessage, activeSessionDeleteMessage),
                )
            } finally {
                onComplete()
            }
        }
    }

    fun runOperation(operation: suspend () -> Unit) = runOperation(operation, {})

    fun refresh() {
        if (refreshInFlight || snapshot.connectionState == ProductConnectionState.CONNECTING) return
        refreshInFlight = true
        runOperation(repository::refresh) { refreshInFlight = false }
    }

    suspend fun runSessionOperation(operation: suspend () -> Unit) {
        try {
            operation()
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            snackbarHostState.showSnackbar(
                safeOperationMessage(error, unavailableOperationMessage, activeSessionDeleteMessage),
            )
        }
    }

    if (BuildConfig.DEBUG) {
        val tailFirstController = TailFirstE2EProductBridge.Controller { operation, requestId, arguments ->
            executeTailFirstProductOperation(
                repository = repository,
                operation = operation,
                requestId = requestId,
                arguments = arguments,
                onSelectSession = { sessionId ->
                    withContext(Dispatchers.Main.immediate) {
                        selectedSessionId = sessionId.toString()
                        saveSelectedProductSession(context, sessionId)
                        navigateTo(ProductDestination.SESSIONS)
                    }
                },
            )
        }
        SideEffect { TailFirstE2EProductBridge.register(tailFirstController) }
        DisposableEffect(Unit) {
            onDispose { TailFirstE2EProductBridge.unregister() }
        }
    }

    if (!snapshot.signedIn) {
        Scaffold(
            snackbarHost = { SnackbarHost(snackbarHostState) },
            containerColor = MaterialTheme.colorScheme.background,
        ) { innerPadding ->
            ProductSignInPage(
                snapshot = snapshot,
                onSignIn = { username, password, onComplete ->
                    val submittedPassword = password.copyOf()
                    password.fill('\u0000')
                    runOperation({ repository.signIn(username, submittedPassword) }, onComplete)
                },
                onRegister = { registrationOpen = true },
                modifier = Modifier.padding(innerPadding),
            )
        }
        if (registrationOpen) {
            val registrationFailureMessage = stringResource(R.string.registration_failure)
            RegistrationDialog(
                onDismiss = { registrationOpen = false },
                onSubmit = { username, email, firstName, lastName, password, confirm, onComplete ->
                    scope.launch {
                        try {
                            val result = try {
                                submitAccountRegistration {
                                    repository.register(
                                        username = username,
                                        email = email,
                                        firstName = firstName,
                                        lastName = lastName,
                                        password = password,
                                        confirm = confirm,
                                    )
                                }
                            } finally {
                                // The repository also owns a wipe, but this boundary must
                                // remain safe for fakes and alternate implementations.
                                password.fill('\u0000')
                                confirm.fill('\u0000')
                            }
                            when (result) {
                                AccountRegistrationResult.Success -> {
                                    onComplete(null)
                                    registrationOpen = false
                                    snackbarHostState.showSnackbar(
                                        context.getString(R.string.registration_success),
                                    )
                                }
                                is AccountRegistrationResult.Failure -> {
                                    onComplete(registrationFailureMessage)
                                }
                            }
                        } catch (error: CancellationException) {
                            // Restore the dialog affordances if cancellation happens while
                            // this composable remains present, then preserve cancellation.
                            onComplete(registrationFailureMessage)
                            throw error
                        }
                    }
                },
            )
        }
        ProductUpdateDialog(
            update = snapshot.updateStatus,
            onDismiss = repository::dismissUpdate,
            onDownload = { runOperation(repository::downloadUpdate) },
            onInstall = onInstallUpdate,
        )
        return
    }

    BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
        val wide = maxWidth >= 700.dp
        val density = LocalDensity.current
        val sessionWide = useWideSessionLayout(
            widthDp = maxWidth.value.roundToInt(),
            heightDp = maxHeight.value.roundToInt(),
            imeVisible = WindowInsets.ime.getBottom(density) > 0,
        )
        Scaffold(
            topBar = {
                if (destination.primaryDestination().destination() != destination && destination != ProductDestination.SESSIONS) {
                    ProductTopBar(
                        destination = destination,
                        onBack = if (destination == ProductDestination.SESSIONS) {
                            null
                        } else {
                            { navigateTo(destination.primaryDestination().destination()) }
                        },
                        onRefresh = ::refresh,
                    )
                }
            },
            bottomBar = {
                if (!wide) {
                    ProductBottomNavigation(destination.primaryDestination(), onDestinationSelected = ::navigateToPrimary)
                }
            },
            snackbarHost = { SnackbarHost(snackbarHostState) },
            floatingActionButton = {
                if (destination == ProductDestination.DEVICES) {
                    val refreshing = refreshInFlight ||
                        snapshot.connectionState == ProductConnectionState.CONNECTING
                    FloatingActionButton(
                        onClick = { if (!refreshing) refresh() },
                        containerColor = if (refreshing) {
                            MaterialTheme.colorScheme.surfaceVariant
                        } else {
                            MaterialTheme.colorScheme.primary
                        },
                    ) {
                        if (refreshing) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(24.dp),
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                strokeWidth = 2.dp,
                            )
                        } else {
                            Icon(
                                Icons.Filled.Refresh,
                                contentDescription = stringResource(R.string.action_refresh),
                            )
                        }
                    }
                }
            },
            containerColor = MaterialTheme.colorScheme.background,
        ) { innerPadding ->
            val layoutDirection = LocalLayoutDirection.current
            val imeBottomPixels = WindowInsets.ime.getBottom(density)
            val scaffoldBottomPixels = with(density) { innerPadding.calculateBottomPadding().roundToPx() }
            val imeAvoidance = sessionImeAvoidance(
                imeBottom = if (destination == ProductDestination.SESSIONS) imeBottomPixels else 0,
                scaffoldBottom = scaffoldBottomPixels,
            )
            val contentPadding = PaddingValues(
                start = innerPadding.calculateStartPadding(layoutDirection),
                top = innerPadding.calculateTopPadding(),
                end = innerPadding.calculateEndPadding(layoutDirection),
                bottom = with(density) { imeAvoidance.scaffoldBottom.toDp() },
            )
            Row(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(contentPadding),
            ) {
                if (wide) {
                    ProductNavigationRail(destination.primaryDestination(), onDestinationSelected = ::navigateToPrimary)
                    VerticalDivider(
                        modifier = Modifier
                            .fillMaxHeight()
                            .width(1.dp),
                        color = MaterialTheme.colorScheme.outline.copy(alpha = 0.5f),
                    )
                }
                ProductContent(
                    modifier = Modifier.weight(1f),
                    destination = destination,
                    sessionContentBottomPixels = if (destination == ProductDestination.SESSIONS) {
                        imeAvoidance.contentBottom
                    } else {
                        0
                    },
                    snapshot = snapshot,
                    foreground = foreground,
                    foregroundGeneration = foregroundGeneration,
                    wide = if (destination == ProductDestination.SESSIONS) sessionWide else wide,
                    selectedSessionId = selectedSessionId,
                    onSessionSelected = {
                        selectedSessionId = it.toString()
                        saveSelectedProductSession(context, it)
                    },
                    // Opening a session is the command-line entry point. The
                    // server lease is requested here, so the terminal has no
                    // separate "申请控制" button.
                    onOpenSession = { runSessionOperation { repository.requestControl(it) } },
                    onLoadTranscript = { sessionId ->
                        scope.launch { runSessionOperation { repository.loadTranscriptBefore(sessionId) } }
                    },
                    onClearTranscript = repository::clearTranscript,
                    terminalEngine = repository::terminalEngine,
                    historyEngine = repository::historyEngine,
                    selectedHistoryId = selectedHistoryId,
                    onHistorySelected = { selectedHistoryId = it.historyObject.id.toString() },
                    onOpenSessions = { navigateTo(ProductDestination.SESSIONS) },
                    onOpenHistory = { navigateTo(ProductDestination.HISTORY) },
                    onOpenAccount = { navigateTo(ProductDestination.ACCOUNT) },
                    selectedRemotePcId = selectedRemotePcId,
                    selectedRemotePcName = selectedRemotePcName,
                    onBrowsePc = { pc ->
                        selectedRemotePcId = pc.id.toString()
                        selectedRemotePcName = pc.name
                        navigateTo(ProductDestination.FILES)
                        runOperation {
                            repository.browseRemoteDirectory(pc.id, pc.name, "", null)
                        }
                    },
                    onOpenPcDownloads = { pc ->
                        selectedRemotePcId = pc.id.toString()
                        selectedRemotePcName = pc.name
                        navigateTo(ProductDestination.DOWNLOADS)
                    },
                    onBrowseRemoteDirectory = { pcId, pcName, path, offset ->
                        runOperation { repository.browseRemoteDirectory(pcId, pcName, path, offset) }
                    },
                    onDownloadRemoteEntry = { pcId, pcName, entry ->
                        onCreateRemoteDocument(remoteDownloadSelection(pcId, pcName, entry))
                    },
                    onOpenRemoteShell = { pcId, path, shellType ->
                        scope.launch {
                            try {
                                val sessionId = repository.openRemoteShell(pcId, path, shellType)
                                selectedSessionId = sessionId.toString()
                                saveSelectedProductSession(context, sessionId)
                                navigateTo(ProductDestination.SESSIONS)
                            } catch (error: CancellationException) {
                                throw error
                            } catch (error: Throwable) {
                                snackbarHostState.showSnackbar(
                                    safeOperationMessage(error, unavailableOperationMessage, activeSessionDeleteMessage),
                                )
                            }
                        }
                    },
                    onCancelRemoteDownload = { runOperation { repository.cancelRemoteDownload(it) } },
                    onRetryRemoteDownload = { record ->
                        onCreateRemoteDocument(
                            RemoteFileDownloadSelection(
                                pcId = record.pcId,
                                pcName = record.pcName,
                                remotePath = record.remotePath,
                                suggestedFilename = record.filename,
                                directoryZip = record.directoryZip,
                                retryRecordId = record.id,
                            ),
                        )
                    },
                    onDeleteRemoteDownloadRecord = repository::deleteRemoteDownloadRecord,
                    onOpenCommandSearch = { commandSearchOpen = true },
                    themePreference = themePreference,
                    onThemePreferenceChanged = onThemePreferenceChanged,
                    onSignIn = { runOperation(repository::signIn) },
                    onSignOut = { signOutConfirmation = true },
                    onCheckForUpdate = { runOperation { repository.checkForUpdate(force = true) } },
                    onTerminalAction = { sessionId, action ->
                        runOperation { repository.sendTerminalAction(sessionId, action) }
                    },
                    onTerminalCustomShortcut = { sessionId, shortcut ->
                        runOperation {
                            dispatchTerminalCustomShortcut(
                                shortcut = shortcut,
                                sendKeyCombination = { repository.sendTerminalKeyCombination(sessionId, it) },
                                sendQuickInput = { repository.sendPaste(sessionId, it) },
                            )
                        }
                    },
                    onPasteRequested = { pasteSessionId = it },
                    terminalInputResumeRegistry = terminalInputResumeRegistry,
                    onStopSessionRequested = { pendingStopSessionId = it },
                    onDeleteSessionRequested = { pendingDeleteSessionId = it },
                    terminalPreferences = terminalPreferences,
                    onTerminalPreferencesChanged = onTerminalPreferencesChanged,
                    shortcutPreferences = shortcutPreferences,
                    onShortcutPreferencesChanged = onShortcutPreferencesChanged,
                    onReplay = { runOperation { repository.replayHistory(it) } },
                )
            }
        }
    }

    ProductUpdateDialog(
        update = snapshot.updateStatus,
        onDismiss = repository::dismissUpdate,
        onDownload = { runOperation(repository::downloadUpdate) },
        onInstall = onInstallUpdate,
    )

    if (commandSearchOpen) {
        CommandSearchDialog(
            onDismiss = { commandSearchOpen = false },
            onNavigate = { next ->
                commandSearchOpen = false
                navigateTo(next)
            },
        )
    }
    if (pasteSessionId != null) {
        PasteConfirmationDialog(
            onDismiss = { pasteSessionId = null },
            onConfirm = { text ->
                val sessionId = pasteSessionId
                pasteSessionId = null
                if (sessionId != null) {
                    runOperation {
                        dispatchAcceptedTerminalInput(
                            operation = { repository.sendCommand(sessionId, text) },
                            onAccepted = { terminalInputResumeRegistry.resume(sessionId) },
                        )
                    }
                }
            },
        )
    }
    if (deletableSessionId != null) {
        ConfirmActionDialog(
            title = stringResource(R.string.confirm_delete_session_title),
            message = stringResource(R.string.confirm_delete_session_copy),
            confirmLabel = stringResource(R.string.confirm_delete_session),
            icon = Icons.Filled.DeleteForever,
            onDismiss = { pendingDeleteSessionId = null },
            onConfirm = {
                val sessionId = deletableSessionId
                pendingDeleteSessionId = null
                runOperation { repository.deleteSession(sessionId) }
            },
        )
    }
    if (stoppableSessionId != null) {
        ConfirmActionDialog(
            title = stringResource(R.string.confirm_close_session_title),
            message = stringResource(R.string.confirm_close_session_copy),
            confirmLabel = stringResource(R.string.confirm_close_session),
            icon = Icons.Filled.Close,
            onDismiss = { pendingStopSessionId = null },
            onConfirm = {
                val sessionId = stoppableSessionId
                pendingStopSessionId = null
                scope.launch {
                    try {
                        repository.stopSession(sessionId)
                        snackbarHostState.showSnackbar(sessionCloseRequestSentMessage)
                    } catch (error: CancellationException) {
                        throw error
                    } catch (_: Throwable) {
                        snackbarHostState.showSnackbar(sessionCloseRequestFailedMessage)
                    }
                }
            },
        )
    }
    if (signOutConfirmation) {
        ConfirmActionDialog(
            title = stringResource(R.string.confirm_sign_out_title),
            message = stringResource(R.string.confirm_sign_out_copy),
            confirmLabel = stringResource(R.string.confirm_sign_out),
            icon = Icons.Filled.Warning,
            onDismiss = { signOutConfirmation = false },
            onConfirm = {
                signOutConfirmation = false
                runOperation(repository::signOut)
            },
        )
    }
}

@Composable
private fun ProductSignInPage(
    snapshot: ProductSnapshot,
    onSignIn: (String, CharArray, () -> Unit) -> Unit,
    onRegister: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var submitting by remember { mutableStateOf(false) }
    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp, vertical = 24.dp),
        verticalArrangement = Arrangement.Top,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .widthIn(max = 440.dp),
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Surface(
                    modifier = Modifier.size(34.dp),
                    shape = ProductShape,
                    color = Color(0xFF10151D),
                ) {
                    Box(contentAlignment = Alignment.Center) {
                        Image(
                            painter = painterResource(R.drawable.termlink_mark_color),
                            contentDescription = stringResource(R.string.product_title),
                            modifier = Modifier.size(28.dp),
                        )
                    }
                }
                Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
                    Text(stringResource(R.string.product_title), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                    Text(
                        stringResource(R.string.sign_in_tagline),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            Spacer(Modifier.height(36.dp))
            Text(
                stringResource(R.string.sign_in_eyebrow),
                color = MaterialTheme.colorScheme.secondary,
                fontFamily = FontFamily.Monospace,
                fontSize = 12.sp,
                fontWeight = FontWeight.Bold,
            )
            Spacer(Modifier.height(8.dp))
            Text(
                stringResource(R.string.sign_in_heading),
                fontSize = 38.sp,
                lineHeight = 40.sp,
                fontWeight = FontWeight.SemiBold,
                letterSpacing = (-0.5).sp,
            )
            Spacer(Modifier.height(10.dp))
            Text(
                stringResource(R.string.sign_in_description),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            Spacer(Modifier.height(24.dp))
            HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.45f))
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Surface(
                    modifier = Modifier.size(34.dp),
                    shape = ProductShape,
                    color = MaterialTheme.colorScheme.secondary.copy(alpha = 0.14f),
                    contentColor = MaterialTheme.colorScheme.secondary,
                ) {
                    Box(contentAlignment = Alignment.Center) {
                        Icon(Icons.Filled.Shield, contentDescription = null, modifier = Modifier.size(20.dp))
                    }
                }
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(stringResource(R.string.sign_in_server_label), style = MaterialTheme.typography.labelMedium)
                    Text(
                        PRODUCT_PUBLIC_SERVER_URL,
                        style = MaterialTheme.typography.bodySmall,
                        fontFamily = FontFamily.Monospace,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                Text(
                    stringResource(R.string.status_https_endpoint),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.tertiary,
                )
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.45f))

            if (snapshot.connectionState == ProductConnectionState.ERROR) {
                Text(
                    snapshot.connectionError ?: stringResource(R.string.connection_error_copy),
                    modifier = Modifier.padding(top = 12.dp),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error,
                )
            }

            Spacer(Modifier.height(22.dp))
            OutlinedTextField(
                value = username,
                onValueChange = { username = it },
                label = { Text(stringResource(R.string.sign_in_username_label)) },
                singleLine = true,
                enabled = !submitting,
                modifier = Modifier.fillMaxWidth(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
            )
            Spacer(Modifier.height(10.dp))
            OutlinedTextField(
                value = password,
                onValueChange = { password = it },
                label = { Text(stringResource(R.string.sign_in_password_label)) },
                singleLine = true,
                enabled = !submitting,
                visualTransformation = PasswordVisualTransformation(),
                modifier = Modifier.fillMaxWidth(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            )
            Spacer(Modifier.height(12.dp))
            Button(
                onClick = {
                    if (submitting) return@Button
                    submitting = true
                    val submittedUsername = username.trim()
                    val submittedPassword = password.toCharArray()
                    password = ""
                    onSignIn(submittedUsername, submittedPassword) { submitting = false }
                },
                enabled = !submitting && username.isNotBlank() && password.isNotEmpty(),
                modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
            ) {
                Icon(Icons.AutoMirrored.Filled.Login, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text(
                    if (submitting) stringResource(R.string.sign_in_working) else stringResource(R.string.sign_in_action),
                    fontWeight = FontWeight.SemiBold,
                )
            }
            Spacer(Modifier.height(10.dp))
            OutlinedButton(
                onClick = onRegister,
                enabled = !submitting,
                modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
            ) { Text(stringResource(R.string.sign_in_register)) }

            Spacer(Modifier.height(18.dp))
            HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
            Row(
                modifier = Modifier.padding(vertical = 14.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalAlignment = Alignment.Top,
            ) {
                Icon(
                    Icons.Filled.Shield,
                    contentDescription = null,
                    modifier = Modifier.size(20.dp),
                    tint = MaterialTheme.colorScheme.secondary,
                )
                Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(stringResource(R.string.sign_in_secure_native), style = MaterialTheme.typography.titleSmall)
                    Text(
                        stringResource(R.string.sign_in_secure_native_copy),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

@Composable
private fun ProductUpdateDialog(
    update: ProductUpdateStatus,
    onDismiss: () -> Unit,
    onDownload: () -> Unit,
    onInstall: (String) -> Unit,
) {
    if (!update.dialogVisible || update.decision !in setOf(
            ProductUpdateDecision.OPTIONAL,
            ProductUpdateDecision.REQUIRED,
        )
    ) {
        return
    }
    val download = update.downloadState
    val active = download is ProductUpdateDownloadState.Downloading ||
        download is ProductUpdateDownloadState.Verifying
    AlertDialog(
        onDismissRequest = { if (!active) onDismiss() },
        title = {
            Text(stringResource(R.string.update_dialog_title, update.versionName ?: "-"))
        },
        text = {
            Column(
                modifier = Modifier
                    .heightIn(max = 440.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(
                    stringResource(
                        R.string.update_dialog_build_size,
                        update.buildNumber ?: 0L,
                        formatProductBytes(update.artifactSizeBytes ?: 0L),
                    ),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                update.releaseNotes?.let { notes ->
                    Text(
                        stringResource(R.string.update_dialog_release_notes),
                        fontWeight = FontWeight.SemiBold,
                    )
                    Text(notes, style = MaterialTheme.typography.bodyMedium)
                }
                when (download) {
                    ProductUpdateDownloadState.Idle -> Text(
                        stringResource(R.string.update_dialog_verification_copy),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    is ProductUpdateDownloadState.Downloading -> {
                        val progress = if (download.totalBytes > 0L) {
                            (download.downloadedBytes.toFloat() / download.totalBytes)
                                .coerceIn(0f, 1f)
                        } else {
                            0f
                        }
                        LinearProgressIndicator(
                            progress = { progress },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Text(
                            stringResource(
                                R.string.update_dialog_progress,
                                formatProductBytes(download.downloadedBytes),
                                formatProductBytes(download.totalBytes),
                            ),
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                    ProductUpdateDownloadState.Verifying -> Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Text(stringResource(R.string.update_dialog_verifying))
                    }
                    is ProductUpdateDownloadState.Ready -> Text(
                        stringResource(R.string.update_dialog_ready),
                        color = MaterialTheme.colorScheme.secondary,
                        fontWeight = FontWeight.SemiBold,
                    )
                    is ProductUpdateDownloadState.Failed -> Text(
                        download.message,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        },
        confirmButton = {
            when (download) {
                ProductUpdateDownloadState.Idle -> TextButton(onClick = onDownload) {
                    Text(stringResource(R.string.update_action_download))
                }
                is ProductUpdateDownloadState.Failed -> TextButton(onClick = onDownload) {
                    Text(stringResource(R.string.update_action_retry))
                }
                is ProductUpdateDownloadState.Ready -> TextButton(
                    onClick = { onInstall(download.apkPath) },
                ) {
                    Text(stringResource(R.string.update_action_install))
                }
                is ProductUpdateDownloadState.Downloading,
                ProductUpdateDownloadState.Verifying,
                -> TextButton(onClick = {}, enabled = false) {
                    Text(stringResource(R.string.update_action_working))
                }
            }
        },
        dismissButton = {
            if (!active) {
                TextButton(onClick = onDismiss) {
                    Text(stringResource(R.string.update_action_later))
                }
            }
        },
    )
}

private fun formatProductBytes(bytes: Long): String = when {
    bytes >= 1024L * 1024L -> String.format(Locale.getDefault(), "%.1f MB", bytes / (1024f * 1024f))
    bytes >= 1024L -> String.format(Locale.getDefault(), "%.1f KB", bytes / 1024f)
    else -> "$bytes B"
}

private suspend fun executeTailFirstProductOperation(
    repository: ProductRepository,
    operation: String,
    requestId: String,
    arguments: Map<String, String>,
    onSelectSession: suspend (java.util.UUID) -> Unit,
): JsonObject {
    TailFirstE2EProductBridge.rememberMarkers(arguments)
    return try {
        when (operation) {
            "probe" -> tailFirstProbeResponse(repository, requestId, operation)
            "open_session" -> {
                val session = awaitNewTailFirstSession(repository)
                    ?: return tailFirstFailure(requestId, operation, "SESSION_UNAVAILABLE")
                TailFirstE2EProductBridge.selectSession(session.id)
                onSelectSession(session.id)
                repository.openSession(session.id)
                val lastMarker = arguments["last_marker"]
                awaitTailFirstSurface(session.id) { surface ->
                    lastMarker == null || surface.observe().liveText.contains(lastMarker)
                }
                tailFirstStateResponse(repository, session.id, requestId, operation)
            }
            "open_history" -> {
                val sessionId = tailFirstSelectedSession(requestId, operation) ?: return tailFirstFailure(
                    requestId,
                    operation,
                    "SESSION_UNAVAILABLE",
                )
                val surface = awaitTailFirstSurface(sessionId)
                val before = repository.snapshot.value.transcriptViewports[sessionId]
                val beforePages = before?.pages?.size ?: 0
                val beforeRows = transcriptRowCount(repository.snapshot.value, sessionId)
                val requestBeforeLineId = before?.nextBeforeLineId
                val existingPageRanges = before?.pages
                    ?.mapTo(mutableSetOf()) { it.firstLineId to it.lastLineId }
                    .orEmpty()
                val historyLoadTriggered = withContext(Dispatchers.Main.immediate) { surface.openHistory() }
                if (historyLoadTriggered) awaitTranscriptChange(repository, sessionId, beforeRows)
                awaitTailFirstSurface(sessionId) { it.observe().historyOverlay }
                val after = repository.snapshot.value.transcriptViewports[sessionId]
                val loadedPage = after?.pages?.singleOrNull {
                    (it.firstLineId to it.lastLineId) !in existingPageRanges
                }
                tailFirstStateResponse(
                    repository,
                    sessionId,
                    requestId,
                    operation,
                    extras = mapOf(
                        "pages_moved" to ((after?.pages?.size ?: 0) - beforePages).coerceAtLeast(0),
                        "rows_loaded" to (
                            (after?.pages?.sumOf { it.rows.size } ?: 0) - beforeRows
                            ).coerceAtLeast(0),
                    ),
                    loadedTranscriptPage = loadedPage?.let { page ->
                        TailFirstLoadedTranscriptPage(
                            requestBeforeLineId = requireNotNull(requestBeforeLineId).toLong(),
                            firstLineId = page.firstLineId,
                            lastLineId = page.lastLineId,
                            hasMoreBefore = page.hasMoreBefore,
                        )
                    },
                )
            }
            "page_up" -> {
                val sessionId = tailFirstSelectedSession(requestId, operation) ?: return tailFirstFailure(
                    requestId,
                    operation,
                    "SESSION_UNAVAILABLE",
                )
                val before = repository.snapshot.value.transcriptViewports[sessionId]
                val beforePages = before?.pages?.size ?: 0
                val beforeRows = before?.pages?.sumOf { it.rows.size } ?: 0
                val requestBeforeLineId = before?.nextBeforeLineId
                val existingPageRanges = before?.pages
                    ?.mapTo(mutableSetOf()) { it.firstLineId to it.lastLineId }
                    .orEmpty()
                val surface = awaitTailFirstSurface(sessionId)
                val beforeViewport = withContext(Dispatchers.Main.immediate) {
                    surface.observe().viewportSignature
                }
                // The live->history boundary was already exercised by open_history.
                // Subsequent acceptance pages call the same repository cursor method
                // directly so a Compose frame cannot swallow the request between a
                // StateFlow update and AndroidView attachment.
                repository.loadTranscriptBefore(sessionId)
                awaitTranscriptChange(repository, sessionId, beforeRows)
                val after = repository.snapshot.value.transcriptViewports[sessionId]
                if (after?.error != null) {
                    return tailFirstFailure(requestId, operation, "TRANSCRIPT_LOAD_FAILED")
                }
                val loadedPage = after?.pages?.singleOrNull {
                    (it.firstLineId to it.lastLineId) !in existingPageRanges
                }
                awaitTailFirstSurface(sessionId) {
                    val observation = it.observe()
                    observation.historyOverlay && observation.viewportSignature != beforeViewport
                }
                tailFirstStateResponse(
                    repository,
                    sessionId,
                    requestId,
                    operation,
                    extras = mapOf(
                        "pages_moved" to ((after?.pages?.size ?: 0) - beforePages).coerceAtLeast(0),
                        "rows_loaded" to (
                            (after?.pages?.sumOf { it.rows.size } ?: 0) - beforeRows
                            ).coerceAtLeast(0),
                    ),
                    loadedTranscriptPage = loadedPage?.let { page ->
                        TailFirstLoadedTranscriptPage(
                            requestBeforeLineId = requireNotNull(requestBeforeLineId).toLong(),
                            firstLineId = page.firstLineId,
                            lastLineId = page.lastLineId,
                            hasMoreBefore = page.hasMoreBefore,
                        )
                    },
                )
            }
            "close_history" -> {
                val sessionId = tailFirstSelectedSession(requestId, operation) ?: return tailFirstFailure(
                    requestId,
                    operation,
                    "SESSION_UNAVAILABLE",
                )
                val surface = awaitTailFirstSurface(sessionId)
                withContext(Dispatchers.Main.immediate) { surface.closeHistory() }
                awaitTailFirstSurface(sessionId) { !it.observe().historyOverlay }
                tailFirstStateResponse(repository, sessionId, requestId, operation)
            }
            "inspect" -> {
                val sessionId = tailFirstSelectedSession(requestId, operation) ?: return tailFirstFailure(
                    requestId,
                    operation,
                    "SESSION_UNAVAILABLE",
                )
                tailFirstStateResponse(repository, sessionId, requestId, operation)
            }
            "show_ime" -> {
                val sessionId = tailFirstSelectedSession(requestId, operation) ?: return tailFirstFailure(
                    requestId,
                    operation,
                    "SESSION_UNAVAILABLE",
                )
                val surface = awaitTailFirstSurface(sessionId)
                withContext(Dispatchers.Main.immediate) { surface.showIme() }
                val stableObservation = awaitStableTailFirstIme(surface)
                tailFirstStateResponse(
                    repository = repository,
                    sessionId = sessionId,
                    requestId = requestId,
                    operation = operation,
                    includeBounds = true,
                    observationOverride = stableObservation,
                )
            }
            else -> tailFirstFailure(requestId, operation, "OPERATION_UNSUPPORTED")
        }
    } catch (error: CancellationException) {
        throw error
    } catch (error: TailFirstImeStabilityTimeout) {
        logTailFirstImeStabilityFailure(error.diagnostics)
        tailFirstImeStabilityFailure(requestId, operation, error.diagnostics)
    } catch (error: Throwable) {
        Log.e(
            "TermLinkTailFirst",
            "Tail-first operation failed: operation=$operation, type=${error::class.java.simpleName}",
            error,
        )
        tailFirstFailure(requestId, operation, "OPERATION_FAILED")
    }
}

private fun tailFirstSelectedSession(requestId: String, operation: String): java.util.UUID? =
    TailFirstE2EProductBridge.selectedSession()

private suspend fun awaitNewTailFirstSession(repository: ProductRepository): SessionSummary? {
    val existingSessionIds = repository.snapshot.value.sessions.mapTo(mutableSetOf(), SessionSummary::id)
    return withTimeoutOrNull(45_000) {
        while (true) {
            try {
                repository.refresh()
            } catch (error: CancellationException) {
                throw error
            } catch (_: Throwable) {
                delay(500)
                continue
            }
            newestNewNonExitedSession(repository.snapshot.value.sessions, existingSessionIds)?.let {
                return@withTimeoutOrNull it
            }
            delay(500)
        }
        error("unreachable")
    }
}

private fun tailFirstProbeResponse(
    repository: ProductRepository,
    requestId: String,
    operation: String,
): JsonObject {
    val snapshot = repository.snapshot.value
    return buildJsonObject {
        put("schema_version", 1)
        put("request_id", requestId)
        put("operation", operation)
        put("ok", true)
        put("account_ready", snapshot.signedIn && !snapshot.serverUrl.isNullOrBlank())
        put("server_origin", snapshot.serverUrl.orEmpty())
        put(
            "capabilities",
            JsonArray(
                listOf(
                    "probe",
                    "open_session",
                    "inspect",
                    "page_up",
                    "open_history",
                    "close_history",
                    "show_ime",
                ).map(::JsonPrimitive),
            ),
        )
    }
}

private suspend fun tailFirstStateResponse(
    repository: ProductRepository,
    sessionId: java.util.UUID,
    requestId: String,
    operation: String,
    extras: Map<String, Int> = emptyMap(),
    loadedTranscriptPage: TailFirstLoadedTranscriptPage? = null,
    includeBounds: Boolean = false,
    observationOverride: TailFirstSurfaceObservation? = null,
): JsonObject {
    val surface = awaitTailFirstSurface(sessionId)
    val observation = observationOverride ?: withContext(Dispatchers.Main.immediate) { surface.observe() }
    val snapshot = repository.snapshot.value
    val session = snapshot.sessions.firstOrNull { it.id == sessionId }
        ?: error("Tail-first session metadata is unavailable")
    val viewport = snapshot.transcriptViewports[sessionId]
    val counts = (repository as? ProductionProductRepository)?.debugHistoryObjectCounts(sessionId)
    val realtime = (repository as? ProductionProductRepository)?.debugRealtimeDiagnostics(sessionId)
    val markers = TailFirstE2EProductBridge.markers()
    val displayText = observation.visibleText
    val zeroBounds = TailFirstPixelBounds(0, 0, 0, 0)
    return buildJsonObject {
        put("schema_version", 1)
        put("request_id", requestId)
        put("operation", operation)
        put("ok", true)
        put("session_id", sessionId.toString())
        put("snapshot_count", counts?.snapshots ?: 0)
        put("transcript_count", counts?.transcripts ?: 0)
        put("chunk_count", counts?.chunks ?: 0)
        put("snapshot_sequence_s", (viewport?.snapshotAnchorSequence ?: 0u).toLong())
        put("live_ack", (snapshot.liveAcknowledgements[sessionId] ?: 0u).toLong())
        put("realtime_socket_phase", realtime?.socketPhase ?: "UNKNOWN")
        put("realtime_socket_generation", realtime?.socketGeneration ?: 0)
        put("realtime_connected_generation", realtime?.connectedGeneration ?: 0)
        put("realtime_socket_state", realtime?.socketState ?: "UNKNOWN")
        put("realtime_endpoint_count", realtime?.endpointCount ?: 0)
        put("realtime_transport_state", realtime?.transportState ?: "UNKNOWN")
        put("realtime_remote_state", realtime?.remoteState ?: "UNKNOWN")
        put("realtime_transport_collector_active", realtime?.transportCollectorActive == true)
        put("realtime_reconnect_active", realtime?.reconnectActive == true)
        put("realtime_subscriber_ready", realtime?.subscriberReady == true)
        put("realtime_reconnect_attempt", realtime?.reconnectAttempt ?: 0)
        put("realtime_subscribe_send_count", realtime?.subscribeSendCount ?: 0)
        put("realtime_last_subscribe_cursor", realtime?.lastSubscribeCursor ?: 0)
        put("realtime_flow_control_send_count", realtime?.flowControlSendCount ?: 0)
        put("realtime_last_received_sequence", realtime?.lastReceivedSequence ?: 0)
        put("realtime_last_handled_sequence", realtime?.lastHandledSequence ?: 0)
        put("realtime_network_available_count", realtime?.networkAvailableCount ?: 0)
        put("realtime_network_lost_count", realtime?.networkLostCount ?: 0)
        put("realtime_network_reconnect_count", realtime?.networkReconnectCount ?: 0)
        put("terminal_columns", session.columns.coerceAtLeast(1))
        put("terminal_rows", session.rows.coerceAtLeast(1))
        put("history_overlay", observation.historyOverlay)
        put("first_marker_visible", markers.first?.let(displayText::contains) == true)
        put("last_marker_visible", markers.second?.let(displayText::contains) == true)
        put("live_marker_visible", markers.third?.let(observation.liveText::contains) == true)
        put("viewport_signature", observation.viewportSignature)
        extras.forEach { (name, value) -> put(name, value) }
        loadedTranscriptPage?.let { page ->
            put("request_before_line_id", page.requestBeforeLineId)
            put("loaded_first_line_id", page.firstLineId)
            put("loaded_last_line_id", page.lastLineId)
            put("loaded_has_more_before", page.hasMoreBefore)
        }
        if (includeBounds) {
            put("ime_visible", observation.imeVisible)
            put("display_bounds", tailFirstBoundsJson(observation.displayBounds ?: zeroBounds))
            put("terminal_bounds", tailFirstBoundsJson(observation.terminalBounds ?: zeroBounds))
            put("bottom_bar_bounds", tailFirstBoundsJson(observation.bottomBarBounds ?: zeroBounds))
            put("ime_bounds", tailFirstBoundsJson(observation.imeBounds ?: zeroBounds))
        }
    }
}

private data class TailFirstLoadedTranscriptPage(
    val requestBeforeLineId: Long,
    val firstLineId: Long,
    val lastLineId: Long,
    val hasMoreBefore: Boolean,
)

private fun tailFirstBoundsJson(bounds: TailFirstPixelBounds): JsonObject = buildJsonObject {
    put("left", bounds.left)
    put("top", bounds.top)
    put("right", bounds.right)
    put("bottom", bounds.bottom)
}

private fun tailFirstImeStabilityFailure(
    requestId: String,
    operation: String,
    diagnostics: TailFirstImeStabilityDiagnostics,
): JsonObject {
    val latest = diagnostics.latestSample
    val zeroBounds = TailFirstPixelBounds(0, 0, 0, 0)
    return buildJsonObject {
        put("schema_version", 1)
        put("request_id", requestId)
        put("operation", operation)
        put("ok", false)
        put("failure_code", "IME_LAYOUT_UNSTABLE")
        put("observed_frames", diagnostics.observedFrames)
        put("stable_frames", diagnostics.stableFrames)
        put("required_stable_frames", diagnostics.requiredStableFrames)
        put("ime_visible", latest?.imeVisible == true)
        put("bounds_complete", latest?.isReady == true)
        put("ime_dock_gap_pixels", latest?.let(::imeDockGapPixels) ?: Int.MIN_VALUE)
        put("terminal_bounds", tailFirstBoundsJson(latest?.terminalBounds ?: zeroBounds))
        put("bottom_bar_bounds", tailFirstBoundsJson(latest?.bottomBarBounds ?: zeroBounds))
        put("ime_bounds", tailFirstBoundsJson(latest?.imeBounds ?: zeroBounds))
    }
}

private fun logTailFirstImeStabilityFailure(diagnostics: TailFirstImeStabilityDiagnostics) {
    val latest = diagnostics.latestSample
    Log.w(
        TAIL_FIRST_E2E_LOG_TAG,
        "show_ime timed out: observedFrames=${diagnostics.observedFrames}, " +
            "stableFrames=${diagnostics.stableFrames}/${diagnostics.requiredStableFrames}, " +
            "imeVisible=${latest?.imeVisible == true}, terminalBounds=${latest?.terminalBounds}, " +
            "bottomBarBounds=${latest?.bottomBarBounds}, imeBounds=${latest?.imeBounds}",
    )
}

private fun tailFirstFailure(requestId: String, operation: String, failureCode: String): JsonObject =
    buildJsonObject {
        put("schema_version", 1)
        put("request_id", requestId)
        put("operation", operation)
        put("ok", false)
        put("failure_code", failureCode)
    }

private suspend fun awaitTailFirstSurface(
    sessionId: java.util.UUID,
    ready: (TailFirstTerminalSurfaceController) -> Boolean = { true },
): TailFirstTerminalSurfaceController = withTimeout(45_000) {
    while (true) {
        val surface = TailFirstTerminalSurfaceRegistry.get(sessionId)
        if (surface != null && withContext(Dispatchers.Main.immediate) { ready(surface) }) return@withTimeout surface
        delay(50)
    }
    error("unreachable")
}

private suspend fun awaitStableTailFirstIme(
    surface: TailFirstTerminalSurfaceController,
): TailFirstSurfaceObservation {
    val tracker = TailFirstImeStabilityTracker()
    val stableObservation = withTimeoutOrNull(TAIL_FIRST_IME_STABILITY_TIMEOUT_MILLIS) {
        while (true) {
            awaitTailFirstUiFrame()
            val observation = withContext(Dispatchers.Main.immediate) { surface.observe() }
            if (tracker.recordFrame(observation.imeLayoutSample())) {
                return@withTimeoutOrNull observation
            }
        }
        error("unreachable")
    }
    return stableObservation ?: throw TailFirstImeStabilityTimeout(tracker.diagnostics())
}

private suspend fun awaitTailFirstUiFrame() = withContext(Dispatchers.Main.immediate) {
    suspendCancellableCoroutine { continuation ->
        val choreographer = Choreographer.getInstance()
        val callback = Choreographer.FrameCallback {
            if (continuation.isActive) continuation.resume(Unit)
        }
        continuation.invokeOnCancellation { choreographer.removeFrameCallback(callback) }
        choreographer.postFrameCallback(callback)
    }
}

private suspend fun awaitTranscriptChange(
    repository: ProductRepository,
    sessionId: java.util.UUID,
    previousRowCount: Int,
) = withTimeout(45_000) {
    var observedLoading = false
    while (true) {
        val state = repository.snapshot.value.transcriptViewports[sessionId]
        observedLoading = observedLoading || state?.loading == true
        val rowCount = state?.pages?.sumOf { it.rows.size } ?: 0
        if (state != null && !state.loading && (
                rowCount > previousRowCount || state.error != null || observedLoading && !state.hasMoreBefore
                )
        ) return@withTimeout
        delay(50)
    }
}

private fun transcriptRowCount(snapshot: ProductSnapshot, sessionId: java.util.UUID): Int =
    snapshot.transcriptViewports[sessionId]?.pages?.sumOf { it.rows.size } ?: 0

private fun tailFirstViewBounds(view: View): TailFirstPixelBounds {
    val location = IntArray(2)
    view.getLocationOnScreen(location)
    return TailFirstPixelBounds(
        left = location[0],
        top = location[1],
        right = location[0] + view.width,
        bottom = location[1] + view.height,
    )
}

private fun tailFirstViewportSignature(
    sessionId: java.util.UUID,
    overlay: Boolean,
    state: TranscriptViewportState?,
    firstVisibleLineId: Long?,
    visibleText: String,
): String {
    val identity = buildString {
        append(sessionId)
        append('|').append(overlay)
        append('|').append(state?.snapshotAnchorSequence ?: 0u)
        append('|').append(state?.nextBeforeLineId ?: 0u)
        append('|').append(state?.pages?.size ?: 0)
        append('|').append(firstVisibleLineId ?: 0)
        append('|').append(visibleText)
    }
    return MessageDigest.getInstance("SHA-256")
        .digest(identity.encodeToByteArray())
        .joinToString("") { byte -> "%02x".format(Locale.ROOT, byte.toInt() and 0xff) }
}

private fun loadTerminalDisplayPreferences(context: android.content.Context): TerminalDisplayPreferences {
    val preferences = context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
    return TerminalDisplayPreferences(
        fontSizeSp = preferences.getFloat(TERMINAL_FONT_SIZE_KEY, 14f),
        lineSpacingMultiplier = preferences.getFloat(TERMINAL_LINE_SPACING_KEY, 1f),
        foregroundArgb = preferences.getInt(TERMINAL_FOREGROUND_KEY, 0xFFE9EEF5.toInt()),
        backgroundArgb = preferences.getInt(TERMINAL_BACKGROUND_KEY, 0xFF080B10.toInt()),
    ).normalized
}

private fun saveTerminalDisplayPreferences(
    context: android.content.Context,
    value: TerminalDisplayPreferences,
) {
    val normalized = value.normalized
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putFloat(TERMINAL_FONT_SIZE_KEY, normalized.fontSizeSp)
        .putFloat(TERMINAL_LINE_SPACING_KEY, normalized.lineSpacingMultiplier)
        .putInt(TERMINAL_FOREGROUND_KEY, normalized.foregroundArgb)
        .putInt(TERMINAL_BACKGROUND_KEY, normalized.backgroundArgb)
        .apply()
}

internal data class ProductNavigationState(
    val primaryDestination: ProductPrimaryDestination = ProductPrimaryDestination.DEVICES,
    val selectedSessionId: String? = null,
)

internal fun loadProductNavigationState(context: android.content.Context): ProductNavigationState {
    val preferences = context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
    val primaryDestination = preferences.getString(PRODUCT_PRIMARY_DESTINATION_KEY, null)
        ?.let { stored -> ProductPrimaryDestination.entries.firstOrNull { it.name == stored } }
        ?: ProductPrimaryDestination.DEVICES
    val selectedSessionId = preferences.getString(PRODUCT_SELECTED_SESSION_ID_KEY, null)
        ?.takeIf { stored -> runCatching { java.util.UUID.fromString(stored) }.isSuccess }
    return ProductNavigationState(primaryDestination, selectedSessionId)
}

internal fun saveProductPrimaryDestination(
    context: android.content.Context,
    destination: ProductPrimaryDestination,
) {
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putString(PRODUCT_PRIMARY_DESTINATION_KEY, destination.name)
        .apply()
}

internal fun saveSelectedProductSession(context: android.content.Context, sessionId: java.util.UUID) {
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putString(PRODUCT_SELECTED_SESSION_ID_KEY, sessionId.toString())
        .apply()
}

internal fun loadTerminalShortcutPreferences(context: android.content.Context): TerminalShortcutPreferences {
    val preferences = context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
    val defaults = TerminalShortcutPreferences().normalized
    val storedOrder = preferences.getString(TERMINAL_SHORTCUT_ORDER_KEY, null)
        ?.split(',')
        ?.mapNotNull { value -> runCatching { TerminalAction.valueOf(value) }.getOrNull() }
        ?: defaults.visibleActions
    val customShortcuts = runCatching {
        val array = JSONArray(preferences.getString(TERMINAL_CUSTOM_SHORTCUTS_KEY, "[]") ?: "[]")
        buildList {
            for (index in 0 until array.length()) {
                val item = array.optJSONObject(index) ?: continue
                add(
                    TerminalCustomShortcut(
                        id = item.optString("id"),
                        label = item.optString("label"),
                        value = if (item.has("value")) {
                            item.optString("value")
                        } else {
                            item.optString("keyCombination")
                        },
                        type = runCatching {
                            TerminalCustomShortcutType.valueOf(item.optString("type"))
                        }.getOrDefault(TerminalCustomShortcutType.KEY_COMBINATION),
                    ),
                )
            }
        }
    }.getOrDefault(emptyList())
    val unifiedOrder = preferences.getString(TERMINAL_UNIFIED_SHORTCUT_ORDER_KEY, null)
        ?.split(',')
        ?.map(String::trim)
        ?.filter(String::isNotEmpty)
        ?: storedOrder.map(::terminalBuiltInShortcutOrderKey) +
            customShortcuts.map { terminalCustomShortcutOrderKey(it.id) }
    return TerminalShortcutPreferences(
        labels = TerminalAction.entries.associateWith { action ->
            preferences.getString(TERMINAL_SHORTCUT_PREFIX + action.name, defaults.label(action)) ?: defaults.label(action)
        },
        visibleActions = storedOrder,
        customShortcuts = customShortcuts,
        shortcutOrder = unifiedOrder,
    ).normalized
}

internal fun saveTerminalShortcutPreferences(
    context: android.content.Context,
    value: TerminalShortcutPreferences,
) {
    val normalized = value.normalized
    val customShortcuts = JSONArray().apply {
        normalized.customShortcuts.forEach { shortcut ->
            put(
                JSONObject()
                    .put("id", shortcut.id)
                    .put("label", shortcut.label)
                    .put("type", shortcut.type.name)
                    .put("value", shortcut.value)
                    .apply {
                        if (shortcut.type == TerminalCustomShortcutType.KEY_COMBINATION) {
                            put("keyCombination", shortcut.keyCombination)
                        }
                    },
            )
        }
    }
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .apply {
            normalized.labels.forEach { (action, label) -> putString(TERMINAL_SHORTCUT_PREFIX + action.name, label) }
        }
        .putString(TERMINAL_SHORTCUT_ORDER_KEY, normalized.visibleActions.joinToString(",") { it.name })
        .putString(TERMINAL_CUSTOM_SHORTCUTS_KEY, customShortcuts.toString())
        .putString(TERMINAL_UNIFIED_SHORTCUT_ORDER_KEY, normalized.shortcutOrder.joinToString(","))
        .apply()
}

@Composable
private fun RegistrationDialog(
    onDismiss: () -> Unit,
    onSubmit: (String, String, String, String, CharArray, CharArray, (String?) -> Unit) -> Unit,
) {
    var username by rememberSaveable { mutableStateOf("") }
    var email by rememberSaveable { mutableStateOf("") }
    var firstName by rememberSaveable { mutableStateOf("") }
    var lastName by rememberSaveable { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var confirm by remember { mutableStateOf("") }
    var submitting by remember { mutableStateOf(false) }
    var submissionError by remember { mutableStateOf<String?>(null) }
    val valid = username.trim().length in 3..128 && email.trim().length in 3..320 &&
        email.trim().contains("@") && firstName.trim().length in 1..128 &&
        lastName.trim().length in 1..128 &&
        password.length in 8..1024 && password == confirm
    AlertDialog(
        onDismissRequest = { if (!submitting) onDismiss() },
        icon = { Icon(Icons.Filled.AccountCircle, contentDescription = null) },
        title = { Text(stringResource(R.string.registration_title)) },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 520.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(
                    stringResource(R.string.registration_copy),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    enabled = !submitting,
                    singleLine = true,
                    label = { Text(stringResource(R.string.registration_username_label)) },
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = email,
                    onValueChange = { email = it },
                    enabled = !submitting,
                    singleLine = true,
                    label = { Text(stringResource(R.string.registration_email_label)) },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = firstName,
                    onValueChange = { firstName = it },
                    enabled = !submitting,
                    singleLine = true,
                    label = { Text(stringResource(R.string.registration_first_name_label)) },
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = lastName,
                    onValueChange = { lastName = it },
                    enabled = !submitting,
                    singleLine = true,
                    label = { Text(stringResource(R.string.registration_last_name_label)) },
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    enabled = !submitting,
                    singleLine = true,
                    label = { Text(stringResource(R.string.registration_password_label)) },
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = confirm,
                    onValueChange = { confirm = it },
                    enabled = !submitting,
                    singleLine = true,
                    label = { Text(stringResource(R.string.registration_confirm_label)) },
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                    modifier = Modifier.fillMaxWidth(),
                )
                if (confirm.isNotEmpty() && password != confirm) {
                    Text(
                        stringResource(R.string.registration_password_mismatch),
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                submissionError?.let { message ->
                    Text(
                        message,
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    if (!valid || submitting) return@Button
                    submitting = true
                    submissionError = null
                    val nextPassword = password.toCharArray()
                    val nextConfirm = confirm.toCharArray()
                    password = ""
                    confirm = ""
                    onSubmit(
                        username.trim(),
                        email.trim(),
                        firstName.trim(),
                        lastName.trim(),
                        nextPassword,
                        nextConfirm,
                    ) { error ->
                        submitting = false
                        submissionError = error
                    }
                },
                enabled = valid && !submitting,
                modifier = Modifier.heightIn(min = 48.dp),
            ) {
                Text(if (submitting) stringResource(R.string.registration_working) else stringResource(R.string.registration_action))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss, enabled = !submitting, modifier = Modifier.heightIn(min = 48.dp)) {
                Text(stringResource(R.string.action_cancel))
            }
        },
    )
}

@Composable
private fun CommandSearchDialog(
    onDismiss: () -> Unit,
    onNavigate: (ProductDestination) -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Filled.Search, contentDescription = null) },
        title = { Text(stringResource(R.string.home_command_search_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(
                    stringResource(R.string.home_command_search_copy),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                listOf(
                    ProductDestination.DEVICES,
                    ProductDestination.SESSIONS,
                    ProductDestination.ACTIVITY,
                    ProductDestination.HISTORY,
                    ProductDestination.SETTINGS,
                ).forEach { destination ->
                    TextButton(
                        onClick = { onNavigate(destination) },
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(min = 48.dp),
                    ) {
                        Text(destination.localizedLabel(), modifier = Modifier.fillMaxWidth(), textAlign = androidx.compose.ui.text.style.TextAlign.Start)
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.action_cancel)) }
        },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ProductTopBar(
    destination: ProductDestination,
    onBack: (() -> Unit)?,
    onRefresh: () -> Unit,
) {
    CenterAlignedTopAppBar(
        title = { Text(destination.localizedLabel(), style = MaterialTheme.typography.titleLarge) },
        navigationIcon = {
            if (onBack != null) {
                IconButton(onClick = onBack) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.action_back))
                }
            }
        },
        actions = {
            IconButton(onClick = onRefresh) {
                Icon(Icons.Filled.Refresh, contentDescription = stringResource(R.string.action_refresh))
            }
        },
    )
}

@Composable
private fun ProductNavigationRail(
    destination: ProductPrimaryDestination,
    onDestinationSelected: (ProductPrimaryDestination) -> Unit,
) {
    NavigationRail(
        modifier = Modifier.width(88.dp),
        containerColor = MaterialTheme.colorScheme.background,
    ) {
        Spacer(Modifier.height(12.dp))
        ProductPrimaryDestination.entries.forEach { item ->
            NavigationRailItem(
                selected = destination == item,
                onClick = { onDestinationSelected(item) },
                icon = { Icon(item.icon(), contentDescription = item.localizedLabel()) },
                label = { Text(item.localizedLabel(), maxLines = 1, overflow = TextOverflow.Ellipsis) },
            )
        }
    }
}

@Composable
private fun ProductBottomNavigation(
    destination: ProductPrimaryDestination,
    onDestinationSelected: (ProductPrimaryDestination) -> Unit,
) {
    NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
        ProductPrimaryDestination.entries.forEach { item ->
            NavigationBarItem(
                selected = destination == item,
                onClick = { onDestinationSelected(item) },
                icon = { Icon(item.icon(), contentDescription = item.localizedLabel()) },
                label = { Text(item.localizedLabel(), maxLines = 1, overflow = TextOverflow.Ellipsis) },
            )
        }
    }
}

private fun ProductPrimaryDestination.icon(): ImageVector = when (this) {
    ProductPrimaryDestination.DEVICES -> Icons.Filled.Devices
    ProductPrimaryDestination.SESSIONS -> Icons.Filled.Terminal
    ProductPrimaryDestination.SETTINGS -> Icons.Filled.Settings
}

@Composable
private fun ProductPrimaryDestination.localizedLabel(): String = when (this) {
    ProductPrimaryDestination.DEVICES -> stringResource(R.string.nav_devices)
    ProductPrimaryDestination.SESSIONS -> stringResource(R.string.nav_sessions)
    ProductPrimaryDestination.SETTINGS -> stringResource(R.string.nav_settings)
}

@Composable
private fun ProductDestination.localizedLabel(): String = when (this) {
    ProductDestination.DEVICES -> stringResource(R.string.nav_devices)
    ProductDestination.FILES -> stringResource(R.string.nav_files)
    ProductDestination.DOWNLOADS -> stringResource(R.string.nav_downloads)
    ProductDestination.SESSIONS -> stringResource(R.string.nav_sessions)
    ProductDestination.ACTIVITY -> stringResource(R.string.nav_activity)
    ProductDestination.HISTORY -> stringResource(R.string.nav_history)
    // Retained enum values are never exposed by the shell.
    ProductDestination.MEMBERSHIP,
    ProductDestination.TRUSTED_DEVICES -> label
    ProductDestination.ACCOUNT -> stringResource(R.string.nav_account)
    ProductDestination.SETTINGS -> stringResource(R.string.nav_settings)
}

@Composable
private fun SessionState.localizedLabel(): String = when (this) {
    SessionState.RUNNING -> stringResource(R.string.session_running)
    SessionState.INTERRUPTED -> stringResource(R.string.session_interrupted)
    SessionState.EXITED -> stringResource(R.string.session_exited)
}

@Composable
private fun dev.termlink.model.HistoryObject.Kind.localizedLabel(): String = when (this) {
    dev.termlink.model.HistoryObject.Kind.CHUNK -> stringResource(R.string.history_chunk)
    dev.termlink.model.HistoryObject.Kind.SNAPSHOT -> stringResource(R.string.history_snapshot)
}

@Composable
private fun ProductContent(
    modifier: Modifier,
    destination: ProductDestination,
    sessionContentBottomPixels: Int,
    snapshot: ProductSnapshot,
    foreground: Boolean,
    foregroundGeneration: Long,
    wide: Boolean,
    selectedSessionId: String?,
    onSessionSelected: (java.util.UUID) -> Unit,
    onOpenSession: suspend (java.util.UUID) -> Unit,
    onLoadTranscript: (java.util.UUID) -> Unit,
    onClearTranscript: (java.util.UUID) -> Unit,
    terminalEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    historyEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    selectedHistoryId: String?,
    onHistorySelected: (HistoryEntry) -> Unit,
    onOpenSessions: () -> Unit,
    onOpenHistory: () -> Unit,
    onOpenAccount: () -> Unit,
    selectedRemotePcId: String?,
    selectedRemotePcName: String,
    onBrowsePc: (PcSummary) -> Unit,
    onOpenPcDownloads: (PcSummary) -> Unit,
    onBrowseRemoteDirectory: (java.util.UUID, String, String, Long?) -> Unit,
    onDownloadRemoteEntry: (java.util.UUID, String, RemoteFileEntry) -> Unit,
    onOpenRemoteShell: (java.util.UUID, String, RemoteShellType) -> Unit,
    onCancelRemoteDownload: (java.util.UUID) -> Unit,
    onRetryRemoteDownload: (RemoteDownloadRecord) -> Unit,
    onDeleteRemoteDownloadRecord: (java.util.UUID) -> Unit,
    onOpenCommandSearch: () -> Unit,
    themePreference: ProductThemePreference,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit,
    terminalPreferences: TerminalDisplayPreferences,
    onTerminalPreferencesChanged: (TerminalDisplayPreferences) -> Unit,
    shortcutPreferences: TerminalShortcutPreferences,
    onShortcutPreferencesChanged: (TerminalShortcutPreferences) -> Unit,
    onSignIn: () -> Unit,
    onSignOut: () -> Unit,
    onCheckForUpdate: () -> Unit,
    onTerminalAction: (java.util.UUID, TerminalAction) -> Unit,
    onTerminalCustomShortcut: (java.util.UUID, TerminalCustomShortcut) -> Unit,
    onPasteRequested: (java.util.UUID) -> Unit,
    terminalInputResumeRegistry: TerminalInputResumeRegistry,
    onStopSessionRequested: (java.util.UUID) -> Unit,
    onDeleteSessionRequested: (java.util.UUID) -> Unit,
    onReplay: (HistoryEntry) -> Unit,
) {
    val remotePcId = selectedRemotePcId?.let { value ->
        runCatching { java.util.UUID.fromString(value) }.getOrNull()
    }
    if (destination == ProductDestination.FILES && remotePcId != null) {
        val state = snapshot.remoteFileBrowser.let { current ->
            if (current.pcId == remotePcId) current else RemoteFileBrowserState(
                pcId = remotePcId,
                pcName = selectedRemotePcName,
            )
        }
        RemoteFileBrowserPage(
            state = state,
            onOpenDirectory = { path ->
                onBrowseRemoteDirectory(remotePcId, selectedRemotePcName, path, null)
            },
            onLoadMore = { path, offset ->
                onBrowseRemoteDirectory(remotePcId, selectedRemotePcName, path, offset)
            },
            onRefresh = { path ->
                onBrowseRemoteDirectory(remotePcId, selectedRemotePcName, path, null)
            },
            onDownload = { entry -> onDownloadRemoteEntry(remotePcId, selectedRemotePcName, entry) },
            onOpenShell = { path, shell -> onOpenRemoteShell(remotePcId, path, shell) },
            modifier = modifier.fillMaxSize(),
        )
        return
    }
    if (destination == ProductDestination.DOWNLOADS && remotePcId != null) {
        RemoteDownloadCenterPage(
            pcId = remotePcId,
            pcName = selectedRemotePcName,
            records = snapshot.remoteDownloads,
            onCancel = onCancelRemoteDownload,
            onRetry = onRetryRemoteDownload,
            onDelete = onDeleteRemoteDownloadRecord,
            modifier = modifier.fillMaxSize(),
        )
        return
    }
    if (destination == ProductDestination.SESSIONS) {
        SessionsPage(
            snapshot = snapshot,
            contentBottomPixels = sessionContentBottomPixels,
            foreground = foreground,
            foregroundGeneration = foregroundGeneration,
            wide = wide,
            selectedSessionId = selectedSessionId,
            onSessionSelected = onSessionSelected,
            onStopSessionRequested = onStopSessionRequested,
            onOpenSession = onOpenSession,
            onLoadTranscript = onLoadTranscript,
            onClearTranscript = onClearTranscript,
            terminalEngine = terminalEngine,
            onTerminalAction = onTerminalAction,
            onTerminalCustomShortcut = onTerminalCustomShortcut,
            onPasteRequested = onPasteRequested,
            terminalInputResumeRegistry = terminalInputResumeRegistry,
            terminalPreferences = terminalPreferences,
            shortcutPreferences = shortcutPreferences,
            modifier = modifier
                .fillMaxSize(),
        )
        return
    }
    Column(
        modifier = modifier
            .fillMaxHeight()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        when (destination) {
            ProductDestination.DEVICES -> DevicesPage(
                snapshot = snapshot,
                onOpenSession = { sessionId ->
                    onSessionSelected(sessionId)
                    onOpenSessions()
                },
                onOpenAccount = onOpenAccount,
                onBrowsePc = onBrowsePc,
                onOpenPcDownloads = onOpenPcDownloads,
                onOpenCommandSearch = onOpenCommandSearch,
                onStopSessionRequested = onStopSessionRequested,
                onDeleteSessionRequested = onDeleteSessionRequested,
            )
            ProductDestination.SESSIONS -> Unit
            ProductDestination.FILES,
            ProductDestination.DOWNLOADS -> EmptyState(
                icon = Icons.Filled.Folder,
                title = stringResource(R.string.remote_pc_unavailable),
                detail = stringResource(R.string.remote_pc_unavailable_copy),
                actionLabel = null,
                onAction = {},
            )
            ProductDestination.ACTIVITY -> ActivityPage(
                snapshot = snapshot,
                onOpenHistory = onOpenHistory,
            )
            ProductDestination.HISTORY -> HistoryPage(
                snapshot = snapshot,
                selectedHistoryId = selectedHistoryId,
                onHistorySelected = onHistorySelected,
                onReplay = onReplay,
                historyEngine = historyEngine,
                terminalPreferences = terminalPreferences,
            )
            // These routes remain in the enum for persistence/API compatibility,
            // but navigation is blocked by [isVisibleInProductShell].
            ProductDestination.MEMBERSHIP,
            ProductDestination.TRUSTED_DEVICES -> HiddenProductDestinationPage()
            ProductDestination.ACCOUNT -> AccountPage(snapshot = snapshot, onSignOut = onSignOut)
            ProductDestination.SETTINGS -> SettingsPage(
                snapshot = snapshot,
                onSignIn = onSignIn,
                onSignOut = onSignOut,
                onCheckForUpdate = onCheckForUpdate,
                themePreference = themePreference,
                onThemePreferenceChanged = onThemePreferenceChanged,
                terminalPreferences = terminalPreferences,
                onTerminalPreferencesChanged = onTerminalPreferencesChanged,
                shortcutPreferences = shortcutPreferences,
                onShortcutPreferencesChanged = onShortcutPreferencesChanged,
            )
        }
    }
}

/** Defensive fallback for stale deep links into removed product surfaces. */
@Composable
private fun HiddenProductDestinationPage() {
    // Do not render membership, quota, billing, trust, or pairing content.
    Spacer(Modifier.height(1.dp))
}

@Composable
private fun PrimaryHomeHeader(onOpenAccount: () -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(stringResource(R.string.home_greeting), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.secondary)
            Text(stringResource(R.string.home_title), style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
        }
        IconButton(onClick = onOpenAccount, modifier = Modifier.size(48.dp)) {
            Icon(Icons.Filled.AccountCircle, contentDescription = stringResource(R.string.action_account_settings), modifier = Modifier.size(30.dp))
        }
    }
}

@Composable
private fun DevicesPage(
    snapshot: ProductSnapshot,
    onOpenSession: (java.util.UUID) -> Unit,
    onOpenAccount: () -> Unit,
    onBrowsePc: (PcSummary) -> Unit,
    onOpenPcDownloads: (PcSummary) -> Unit,
    onOpenCommandSearch: () -> Unit,
    onStopSessionRequested: (java.util.UUID) -> Unit,
    onDeleteSessionRequested: (java.util.UUID) -> Unit,
) {
    PrimaryHomeHeader(onOpenAccount = onOpenAccount)
    OutlinedButton(
        onClick = onOpenCommandSearch,
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 48.dp),
        contentPadding = ButtonDefaults.ContentPadding,
    ) {
        Icon(Icons.Filled.Search, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(8.dp))
        Text(stringResource(R.string.home_command_search), modifier = Modifier.weight(1f), textAlign = androidx.compose.ui.text.style.TextAlign.Start)
        Text(stringResource(R.string.home_command_shortcut), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    val pcs = visibleOnlineWindowsPcs(snapshot)
    if (pcs.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Devices,
            title = if (snapshot.signedIn) stringResource(R.string.empty_no_devices) else stringResource(R.string.empty_sign_in_devices),
            detail = if (snapshot.signedIn) {
                stringResource(R.string.server_devices_empty_copy)
            } else {
                stringResource(R.string.empty_sign_in_devices)
            },
            actionLabel = null,
            onAction = {},
        )
    } else {
        var expandedPcIds by remember { mutableStateOf(emptySet<String>()) }
        pcs.forEach { pc ->
            val sessions = snapshot.sessions.filter { it.pcId == pc.id }
            val pcKey = pc.id.toString()
            PcRow(
                pc = pc,
                sessions = sessions,
                sessionTitles = snapshot.sessionTitles,
                expanded = pcKey in expandedPcIds,
                onToggleExpanded = {
                    expandedPcIds = if (pcKey in expandedPcIds) {
                        expandedPcIds - pcKey
                    } else {
                        expandedPcIds + pcKey
                    }
                },
                onOpenSession = onOpenSession,
                onBrowseFiles = { onBrowsePc(pc) },
                onOpenDownloads = { onOpenPcDownloads(pc) },
                onStopSessionRequested = onStopSessionRequested,
                onDeleteSessionRequested = onDeleteSessionRequested,
            )
        }
    }
    // Keep the final device/session row clear of the home refresh FAB.
    Spacer(Modifier.height(72.dp))
}

/**
 * Device-page projection. Repository state intentionally retains offline PCs
 * for session/history reconciliation; this surface shows only server-online
 * Windows PCs.
 */
internal fun visibleOnlineWindowsPcs(snapshot: ProductSnapshot): List<PcSummary> {
    val windowsDeviceIds = snapshot.devices.asSequence()
        .filter { it.type == DeviceType.WINDOWS_PC }
        .mapTo(hashSetOf()) { it.id }
    return snapshot.pcs.filter { it.online && it.deviceId in windowsDeviceIds }
}

@Composable
private fun PcRow(
    pc: PcSummary,
    sessions: List<SessionSummary> = emptyList(),
    sessionTitles: Map<java.util.UUID, String> = emptyMap(),
    expanded: Boolean = false,
    onToggleExpanded: (() -> Unit)? = null,
    onOpenSession: ((java.util.UUID) -> Unit)? = null,
    onBrowseFiles: (() -> Unit)? = null,
    onOpenDownloads: (() -> Unit)? = null,
    onStopSessionRequested: ((java.util.UUID) -> Unit)? = null,
    onDeleteSessionRequested: ((java.util.UUID) -> Unit)? = null,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable(enabled = sessions.isNotEmpty() && onToggleExpanded != null) {
                        onToggleExpanded?.invoke()
                    }
                    .padding(14.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Icon(Icons.Filled.LaptopWindows, contentDescription = stringResource(R.string.windows_pc_accessibility), tint = MaterialTheme.colorScheme.primary)
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(pc.name, style = MaterialTheme.typography.titleMedium)
                    Text(
                        stringResource(
                            R.string.pc_routing_status,
                            pc.agentVersion,
                            stringResource(if (pc.remoteRoutingEnabled) R.string.remote_enabled else R.string.remote_disabled),
                        ),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                StatusDot(
                    color = if (pc.online) MaterialTheme.colorScheme.secondary else MaterialTheme.colorScheme.onSurfaceVariant,
                    contentDescription = if (pc.online) stringResource(R.string.pc_online_accessibility) else stringResource(R.string.pc_offline_accessibility),
                )
                if (sessions.isNotEmpty()) {
                    Icon(
                        imageVector = if (expanded) Icons.Filled.KeyboardArrowUp else Icons.Filled.KeyboardArrowDown,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            if (onBrowseFiles != null || onOpenDownloads != null) {
                HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
                Row(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp),
                    horizontalArrangement = Arrangement.End,
                ) {
                    onBrowseFiles?.let { browse ->
                        TextButton(onClick = browse, modifier = Modifier.heightIn(min = 48.dp)) {
                            Icon(Icons.Filled.Folder, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text(stringResource(R.string.action_browse_files))
                        }
                    }
                    onOpenDownloads?.let { downloads ->
                        TextButton(onClick = downloads, modifier = Modifier.heightIn(min = 48.dp)) {
                            Icon(Icons.Filled.Download, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text(stringResource(R.string.action_download_center))
                        }
                    }
                }
            }
            if (expanded && sessions.isNotEmpty()) {
                HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(start = 42.dp, end = 12.dp, top = 8.dp, bottom = 10.dp),
                    verticalArrangement = Arrangement.spacedBy(2.dp),
                ) {
                    sessions.forEach { session ->
                        val active = isDeviceSessionActive(session.online)
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(ProductShape)
                                .clickable(enabled = onOpenSession != null) { onOpenSession?.invoke(session.id) }
                                .padding(horizontal = 8.dp, vertical = 8.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            StatusDot(
                                color = if (active) MaterialTheme.colorScheme.secondary else MaterialTheme.colorScheme.onSurfaceVariant,
                                contentDescription = session.state.localizedLabel(),
                            )
                            Text(
                                displaySessionTitle(session, sessionTitles[session.id]),
                                modifier = Modifier.weight(1f),
                                style = MaterialTheme.typography.labelMedium,
                                color = if (active) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                session.state.localizedLabel(),
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            when (deviceSessionDestructiveAction(session.online)) {
                                DeviceSessionDestructiveAction.CLOSE_ONLINE -> if (onStopSessionRequested != null) {
                                    IconButton(
                                        onClick = { onStopSessionRequested(session.id) },
                                        modifier = Modifier.size(40.dp),
                                    ) {
                                        Icon(
                                            Icons.Filled.Close,
                                            contentDescription = stringResource(R.string.action_close_session),
                                            tint = MaterialTheme.colorScheme.error,
                                        )
                                    }
                                }
                                DeviceSessionDestructiveAction.DELETE_OFFLINE_RECORD -> if (onDeleteSessionRequested != null) {
                                    IconButton(
                                        onClick = { onDeleteSessionRequested(session.id) },
                                        modifier = Modifier.size(40.dp),
                                    ) {
                                        Icon(
                                            Icons.Filled.DeleteForever,
                                            contentDescription = stringResource(R.string.action_delete_session),
                                            tint = MaterialTheme.colorScheme.error,
                                        )
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

internal fun isDeviceSessionActive(sessionOnline: Boolean): Boolean = sessionOnline

internal enum class DeviceSessionDestructiveAction {
    CLOSE_ONLINE,
    DELETE_OFFLINE_RECORD,
}

internal fun deviceSessionDestructiveAction(sessionOnline: Boolean): DeviceSessionDestructiveAction =
    if (isDeviceSessionActive(sessionOnline)) {
    DeviceSessionDestructiveAction.CLOSE_ONLINE
} else {
    DeviceSessionDestructiveAction.DELETE_OFFLINE_RECORD
}

@Composable
private fun SessionsPage(
    snapshot: ProductSnapshot,
    contentBottomPixels: Int,
    foreground: Boolean,
    foregroundGeneration: Long,
    wide: Boolean,
    selectedSessionId: String?,
    onSessionSelected: (java.util.UUID) -> Unit,
    onStopSessionRequested: (java.util.UUID) -> Unit,
    onOpenSession: suspend (java.util.UUID) -> Unit,
    onLoadTranscript: (java.util.UUID) -> Unit,
    onClearTranscript: (java.util.UUID) -> Unit,
    terminalEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    onTerminalAction: (java.util.UUID, TerminalAction) -> Unit,
    onTerminalCustomShortcut: (java.util.UUID, TerminalCustomShortcut) -> Unit,
    onPasteRequested: (java.util.UUID) -> Unit,
    terminalInputResumeRegistry: TerminalInputResumeRegistry,
    terminalPreferences: TerminalDisplayPreferences,
    shortcutPreferences: TerminalShortcutPreferences,
    modifier: Modifier,
) {
    val sessionOpenScope = rememberCoroutineScope()
    val requestedSelectedId = selectedSessionId?.let { value ->
        runCatching { java.util.UUID.fromString(value) }.getOrNull()
    }
    val requestedSelected = snapshot.sessions.firstOrNull { it.id == requestedSelectedId }
    val requestedSelectedEngine = requestedSelected?.let { terminalEngine(it.id) }
    val activeSessions = liveSessionPageSessions(
        sessions = snapshot.sessions,
        selectedId = requestedSelectedId,
        sessionStates = snapshot.sessionStates,
        selectedHasEngine = requestedSelectedEngine != null,
    )
    val density = LocalDensity.current
    val imeBottomPixels = WindowInsets.ime.getBottom(density)
    val contentBottom = contentBottomPixels
    val contentBottomPadding = with(density) { contentBottom.toDp() }
    Column(
        // ProductApp owns the one IME/scaffold allocation. SessionsPage only
        // consumes the residual inset which was not already applied outside.
        modifier = modifier.padding(bottom = contentBottomPadding),
        verticalArrangement = Arrangement.Top,
    ) {
        if (activeSessions.isEmpty()) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(horizontal = 16.dp, vertical = 14.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                ConnectionBanner(snapshot = snapshot)
                EmptyState(
                    icon = Icons.Filled.Terminal,
                    title = if (snapshot.signedIn) stringResource(R.string.empty_no_sessions) else stringResource(R.string.empty_sign_in_sessions),
                    detail = stringResource(R.string.server_sessions_empty_copy),
                    actionLabel = null,
                    onAction = {},
                )
            }
        } else {
            val selected = activeSessions.firstOrNull { it.id.toString() == selectedSessionId }
                ?: activeSessions.first()
            val selectedEngine = if (selected.id == requestedSelectedId) {
                requestedSelectedEngine
            } else {
                terminalEngine(selected.id)
            }
            val controlRequestKey = sessionControlRequestEffectKey(
                foreground = foreground,
                foregroundGeneration = foregroundGeneration,
                online = selected.online,
                remoteState = snapshot.sessionStates[selected.id],
            )
            LaunchedEffect(selected.id, controlRequestKey) {
                if (controlRequestKey != null) {
                    onOpenSession(selected.id)
                }
            }
            val unknownPcLabel = stringResource(R.string.unknown_pc)
            fun pcLabel(session: SessionSummary): String =
                snapshot.pcs.firstOrNull { it.id == session.pcId }?.name
                    ?: unknownPcLabel
            if (wide) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .fillMaxHeight()
                        .weight(1f),
                    horizontalArrangement = Arrangement.Start,
                    verticalAlignment = Alignment.Top,
                ) {
                    SessionList(
                        sessions = activeSessions,
                        sessionTitles = snapshot.sessionTitles,
                        selectedId = selected.id,
                        onSessionSelected = onSessionSelected,
                        pcLabel = ::pcLabel,
                        modifier = Modifier
                            .widthIn(min = 260.dp, max = 360.dp)
                            .fillMaxHeight()
                            .verticalScroll(rememberScrollState()),
                    )
                    VerticalDivider(
                        modifier = Modifier.fillMaxHeight(),
                        color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f),
                    )
                    SessionDetail(
                        session = selected,
                        displayTitle = displaySessionTitle(selected, snapshot.sessionTitles[selected.id]),
                        pcLabel = pcLabel(selected),
                        sessionOnline = selected.online,
                        onTerminalAction = onTerminalAction,
                        onTerminalCustomShortcut = onTerminalCustomShortcut,
                        onPasteRequested = onPasteRequested,
                        terminalInputResumeRegistry = terminalInputResumeRegistry,
                        transcriptState = snapshot.transcriptViewports[selected.id],
                        liveAcknowledgement = snapshot.liveAcknowledgements[selected.id],
                        bootstrapSource = snapshot.bootstrapSources[selected.id],
                        onLoadTranscript = { onLoadTranscript(selected.id) },
                        onClearTranscript = { onClearTranscript(selected.id) },
                        onRetryOpen = {
                            sessionOpenScope.launch { onOpenSession(selected.id) }
                        },
                        terminalPreferences = terminalPreferences,
                        shortcutPreferences = shortcutPreferences,
                        remoteState = selected.resolveDisplayedRemoteState(snapshot.sessionStates[selected.id]),
                        engine = selectedEngine,
                        modifier = Modifier
                            .fillMaxHeight()
                            .weight(1f),
                    )
                }
            } else {
                MobileSessionContent(
                    sessions = activeSessions,
                    sessionTitles = snapshot.sessionTitles,
                    selectedId = selected.id,
                    onSessionSelected = onSessionSelected,
                    onStopSessionRequested = onStopSessionRequested,
                    pcLabel = ::pcLabel,
                    selectedEngine = selectedEngine,
                    sessionStates = snapshot.sessionStates,
                    onTerminalAction = onTerminalAction,
                    onTerminalCustomShortcut = onTerminalCustomShortcut,
                    onPasteRequested = onPasteRequested,
                    terminalInputResumeRegistry = terminalInputResumeRegistry,
                    transcriptViewports = snapshot.transcriptViewports,
                    liveAcknowledgements = snapshot.liveAcknowledgements,
                    bootstrapSources = snapshot.bootstrapSources,
                    onLoadTranscript = onLoadTranscript,
                    onClearTranscript = onClearTranscript,
                    onRetryOpen = { sessionId ->
                        sessionOpenScope.launch { onOpenSession(sessionId) }
                    },
                    terminalPreferences = terminalPreferences,
                    shortcutPreferences = shortcutPreferences,
                    compactForIme = imeBottomPixels > 0,
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

@Composable
private fun MobileSessionContent(
    sessions: List<SessionSummary>,
    sessionTitles: Map<java.util.UUID, String>,
    selectedId: java.util.UUID,
    onSessionSelected: (java.util.UUID) -> Unit,
    onStopSessionRequested: (java.util.UUID) -> Unit,
    pcLabel: (SessionSummary) -> String,
    selectedEngine: TermuxTerminalEngine?,
    sessionStates: Map<java.util.UUID, RemoteSessionState>,
    onTerminalAction: (java.util.UUID, TerminalAction) -> Unit,
    onTerminalCustomShortcut: (java.util.UUID, TerminalCustomShortcut) -> Unit,
    onPasteRequested: (java.util.UUID) -> Unit,
    terminalInputResumeRegistry: TerminalInputResumeRegistry,
    transcriptViewports: Map<java.util.UUID, TranscriptViewportState>,
    liveAcknowledgements: Map<java.util.UUID, ULong>,
    bootstrapSources: Map<java.util.UUID, ProductBootstrapSource>,
    onLoadTranscript: (java.util.UUID) -> Unit,
    onClearTranscript: (java.util.UUID) -> Unit,
    onRetryOpen: (java.util.UUID) -> Unit,
    terminalPreferences: TerminalDisplayPreferences,
    shortcutPreferences: TerminalShortcutPreferences,
    compactForIme: Boolean,
    modifier: Modifier,
) {
    val session = sessions.firstOrNull { it.id == selectedId } ?: sessions.first()
    val selectedIndex = mobileSessionSelectionIndex(sessions, session.id)
    val selectorState = rememberLazyListState(initialFirstVisibleItemIndex = selectedIndex)
    LaunchedEffect(session.id, sessions.map(SessionSummary::id)) {
        selectorState.animateScrollToItem(selectedIndex)
    }
    Column(modifier = modifier.fillMaxSize()) {
        if (!compactForIme) {
            LazyRow(
                state = selectorState,
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 36.dp, max = 40.dp)
                    .testTag(MOBILE_SESSION_SELECTOR_TEST_TAG),
                contentPadding = PaddingValues(horizontal = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                items(sessions, key = { it.id }) { candidate ->
                    FilterChip(
                        selected = candidate.id == session.id,
                        onClick = { onSessionSelected(candidate.id) },
                        label = {
                            Text(
                                pcLabel(candidate) + " · " + displaySessionTitle(candidate, sessionTitles[candidate.id]),
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        },
                        trailingIcon = if (candidate.online) {
                            {
                                IconButton(
                                    onClick = { onStopSessionRequested(candidate.id) },
                                    modifier = Modifier
                                        .size(32.dp)
                                        .testTag(mobileSessionCloseTestTag(candidate.id)),
                                ) {
                                    Icon(
                                        imageVector = Icons.Filled.Close,
                                        contentDescription = stringResource(R.string.action_close_session),
                                        modifier = Modifier.size(17.dp),
                                        tint = MaterialTheme.colorScheme.error,
                                    )
                                }
                            }
                        } else {
                            null
                        },
                    )
                }
            }
        }
        SessionDetail(
            session = session,
            displayTitle = displaySessionTitle(session, sessionTitles[session.id]),
            pcLabel = pcLabel(session),
            sessionOnline = session.online,
            onTerminalAction = onTerminalAction,
            onTerminalCustomShortcut = onTerminalCustomShortcut,
            onPasteRequested = onPasteRequested,
            terminalInputResumeRegistry = terminalInputResumeRegistry,
            transcriptState = transcriptViewports[session.id],
            liveAcknowledgement = liveAcknowledgements[session.id],
            bootstrapSource = bootstrapSources[session.id],
            onLoadTranscript = { onLoadTranscript(session.id) },
            onClearTranscript = { onClearTranscript(session.id) },
            onRetryOpen = { onRetryOpen(session.id) },
            remoteState = session.resolveDisplayedRemoteState(sessionStates[session.id]),
            engine = selectedEngine,
            terminalPreferences = terminalPreferences,
            shortcutPreferences = shortcutPreferences,
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f),
        )
    }
}

internal const val MOBILE_SESSION_SWIPE_ENABLED = false
internal const val MOBILE_SESSION_SELECTOR_TEST_TAG = "mobile-session-selector"
internal const val ANDROID_VIEW_DRIVEN_TERMINAL_RESIZE_ENABLED = false

internal fun mobileSessionCloseTestTag(sessionId: java.util.UUID): String =
    "mobile-session-close:$sessionId"

/**
 * A wide session layout is useful only while it leaves enough vertical space
 * for the authoritative Windows grid. Landscape phones and an open IME use
 * the single-column session surface while the app navigation rail stays put.
 */
internal fun useWideSessionLayout(widthDp: Int, heightDp: Int, imeVisible: Boolean): Boolean =
    widthDp >= 700 && heightDp >= 520 && !imeVisible

internal fun mobileSessionSelectionIndex(
    sessions: List<SessionSummary>,
    selectedId: java.util.UUID,
): Int = sessions.indexOfFirst { it.id == selectedId }.coerceAtLeast(0)

/**
 * Keeps the current renderer mounted across a transient catalog miss. Other
 * offline sessions remain outside the live-session surface, and an explicit
 * exit always wins over a stale engine or state entry.
 */
internal fun liveSessionPageSessions(
    sessions: List<SessionSummary>,
    selectedId: java.util.UUID?,
    sessionStates: Map<java.util.UUID, RemoteSessionState>,
    selectedHasEngine: Boolean,
): List<SessionSummary> = sessions.filter { session ->
    val remoteState = sessionStates[session.id]
    val explicitlyExited = session.state == SessionState.EXITED || remoteState is RemoteSessionState.Exited
    if (explicitlyExited) {
        false
    } else {
        session.online || session.id == selectedId && (selectedHasEngine || remoteState != null)
    }
}

internal data class SessionImeAvoidance(
    val scaffoldBottom: Int,
    val contentBottom: Int,
)

internal fun sessionImeAvoidance(imeBottom: Int, scaffoldBottom: Int): SessionImeAvoidance {
    require(imeBottom >= 0 && scaffoldBottom >= 0)
    return SessionImeAvoidance(
        scaffoldBottom = scaffoldBottom,
        contentBottom = (imeBottom - scaffoldBottom).coerceAtLeast(0),
    )
}

@Composable
private fun SessionList(
    sessions: List<SessionSummary>,
    sessionTitles: Map<java.util.UUID, String>,
    selectedId: java.util.UUID,
    onSessionSelected: (java.util.UUID) -> Unit,
    pcLabel: (SessionSummary) -> String,
    modifier: Modifier,
) {
    Surface(
        modifier = modifier,
        color = MaterialTheme.colorScheme.surface,
    ) {
        Column {
            Text(
                stringResource(R.string.sessions_all),
                modifier = Modifier.padding(horizontal = 20.dp, vertical = 16.dp),
                style = MaterialTheme.typography.titleSmall,
            )
            sessions.forEachIndexed { index, session ->
                if (index > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
                SessionListRow(
                    session = session,
                    displayTitle = displaySessionTitle(session, sessionTitles[session.id]),
                    selected = session.id == selectedId,
                    pcLabel = pcLabel(session),
                    onClick = { onSessionSelected(session.id) },
                )
            }
        }
    }
}

@Composable
private fun SessionListRow(
    session: SessionSummary,
    displayTitle: String,
    selected: Boolean,
    pcLabel: String,
    onClick: () -> Unit,
) {
    val stateColor = when (session.state) {
        SessionState.RUNNING -> MaterialTheme.colorScheme.secondary
        SessionState.INTERRUPTED -> MaterialTheme.colorScheme.tertiary
        SessionState.EXITED -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(if (selected) MaterialTheme.colorScheme.surfaceVariant else Color.Transparent)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(Icons.Filled.Terminal, contentDescription = stringResource(R.string.terminal_session_accessibility), tint = stateColor)
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(displayTitle, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                stringResource(
                    R.string.session_summary,
                    pcLabel,
                    session.state.localizedLabel(),
                    session.columns,
                    session.rows,
                ),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun SessionDetail(
    session: SessionSummary,
    displayTitle: String,
    pcLabel: String,
    sessionOnline: Boolean,
    onTerminalAction: (java.util.UUID, TerminalAction) -> Unit,
    onTerminalCustomShortcut: (java.util.UUID, TerminalCustomShortcut) -> Unit,
    onPasteRequested: (java.util.UUID) -> Unit,
    terminalInputResumeRegistry: TerminalInputResumeRegistry,
    transcriptState: TranscriptViewportState?,
    liveAcknowledgement: ULong?,
    bootstrapSource: ProductBootstrapSource?,
    onLoadTranscript: () -> Unit,
    onClearTranscript: () -> Unit,
    onRetryOpen: () -> Unit,
    remoteState: RemoteSessionState?,
    engine: TermuxTerminalEngine?,
    terminalPreferences: TerminalDisplayPreferences,
    shortcutPreferences: TerminalShortcutPreferences,
    modifier: Modifier,
) {
    Surface(
        modifier = modifier,
        color = MaterialTheme.colorScheme.surface,
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.Top,
        ) {
            val statusLabel = remoteState.localizedTerminalStatus()
            Row(
                modifier = Modifier.padding(horizontal = 8.dp, vertical = 2.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                StatusDot(
                    color = sessionStatusColor(session.state, sessionOnline, remoteState),
                    contentDescription = statusLabel,
                )
                Text(
                    "$pcLabel · $displayTitle",
                    modifier = Modifier
                        .weight(1f)
                        .basicMarquee(),
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 1,
                    softWrap = false,
                )
                Text(
                    statusLabel,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                )
                if (remoteState is RemoteSessionState.Reconnecting &&
                    remoteState.reason == SESSION_BOOTSTRAP_FAILED_REASON
                ) {
                    TextButton(
                        onClick = onRetryOpen,
                        modifier = Modifier.testTag("session-open-retry:${session.id}"),
                    ) {
                        Text(stringResource(R.string.terminal_history_retry))
                    }
                }
            }
            TerminalSurface(
                session = session,
                engine = engine,
                remoteState = remoteState,
                terminalPreferences = terminalPreferences,
                shortcutPreferences = shortcutPreferences,
                onTerminalAction = { onTerminalAction(session.id, it) },
                onTerminalCustomShortcut = { onTerminalCustomShortcut(session.id, it) },
                onPasteRequested = { onPasteRequested(session.id) },
                terminalInputResumeRegistry = terminalInputResumeRegistry,
                transcriptState = transcriptState,
                liveAcknowledgement = liveAcknowledgement,
                bootstrapSource = bootstrapSource,
                onLoadTranscript = onLoadTranscript,
                onClearTranscript = onClearTranscript,
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
            )
        }
    }
}

@Composable
private fun TerminalActionBar(
    enabled: Boolean,
    onAction: (TerminalAction) -> Unit,
    onCustomShortcut: (TerminalCustomShortcut) -> Unit,
    onPasteRequested: () -> Unit,
    shortcutPreferences: TerminalShortcutPreferences,
    expanded: Boolean,
    onExpandedChanged: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(Color.Black.copy(alpha = 0.62f), RoundedCornerShape(6.dp))
            .padding(horizontal = 4.dp, vertical = 2.dp),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(2.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(
                onClick = { onExpandedChanged(!expanded) },
                modifier = Modifier
                    .size(30.dp)
                    .testTag(terminalCommandTestTag("dock-toggle")),
            ) {
                Icon(
                    if (expanded) Icons.Filled.KeyboardArrowDown else Icons.Filled.KeyboardArrowUp,
                    contentDescription = if (expanded) stringResource(R.string.terminal_dock_hide) else stringResource(R.string.terminal_dock_show),
                    tint = Color.White.copy(alpha = 0.82f),
                )
            }
            IconButton(
                onClick = onPasteRequested,
                enabled = enabled,
                modifier = Modifier
                    .size(30.dp)
                    .testTag(terminalCommandTestTag("paste")),
            ) {
                Icon(
                    Icons.Filled.ContentPaste,
                    contentDescription = stringResource(R.string.action_paste),
                )
            }
            if (expanded) {
                orderedTerminalShortcutItems(shortcutPreferences).forEach { item ->
                    when (item) {
                        is TerminalShortcutItem.BuiltIn -> when (item.action) {
                            TerminalAction.ARROW_LEFT -> TerminalIconKey(Icons.AutoMirrored.Filled.KeyboardArrowLeft, stringResource(R.string.arrow_left), item.action, enabled, onAction)
                            TerminalAction.ARROW_UP -> TerminalIconKey(Icons.Filled.KeyboardArrowUp, stringResource(R.string.arrow_up), item.action, enabled, onAction)
                            TerminalAction.ARROW_DOWN -> TerminalIconKey(Icons.Filled.KeyboardArrowDown, stringResource(R.string.arrow_down), item.action, enabled, onAction)
                            TerminalAction.ARROW_RIGHT -> TerminalIconKey(Icons.AutoMirrored.Filled.KeyboardArrowRight, stringResource(R.string.arrow_right), item.action, enabled, onAction)
                            else -> TerminalTextKey(shortcutPreferences.label(item.action), item.action, enabled, onAction)
                        }
                        is TerminalShortcutItem.Custom -> TerminalCustomTextKey(
                            shortcut = item.shortcut,
                            enabled = enabled,
                            onActivate = onCustomShortcut,
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun TerminalCustomTextKey(
    shortcut: TerminalCustomShortcut,
    enabled: Boolean,
    onActivate: (TerminalCustomShortcut) -> Unit,
) {
    OutlinedButton(
        onClick = { onActivate(shortcut) },
        enabled = enabled,
        modifier = Modifier
            .heightIn(min = 30.dp)
            .widthIn(max = 200.dp)
            .testTag(terminalCommandTestTag("custom-${shortcut.id}")),
        contentPadding = PaddingValues(horizontal = 7.dp, vertical = 0.dp),
    ) {
        Text(
            shortcut.displayLabel,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.labelSmall,
        )
    }
}

@Composable
private fun TerminalTextKey(
    label: String,
    action: TerminalAction,
    enabled: Boolean,
    onAction: (TerminalAction) -> Unit,
) {
    OutlinedButton(
        onClick = { onAction(action) },
        enabled = enabled,
        modifier = Modifier
            .heightIn(min = 30.dp)
            .testTag(terminalCommandTestTag(action.name.lowercase(Locale.ROOT))),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 7.dp, vertical = 0.dp),
    ) {
        Text(label, maxLines = 1, style = MaterialTheme.typography.labelSmall)
    }
}

@Composable
private fun TerminalIconKey(
    icon: ImageVector,
    description: String,
    action: TerminalAction,
    enabled: Boolean,
    onAction: (TerminalAction) -> Unit,
) {
    IconButton(
        onClick = { onAction(action) },
        enabled = enabled,
        modifier = Modifier
            .size(30.dp)
            .testTag(terminalCommandTestTag(action.name.lowercase(Locale.ROOT))),
    ) {
        Icon(icon, contentDescription = description)
    }
}

private fun sessionStatusColor(
    state: SessionState,
    sessionOnline: Boolean,
    remoteState: RemoteSessionState?,
): Color = when {
    state == SessionState.EXITED -> ProductMuted
    remoteState is RemoteSessionState.Exited -> ProductMuted
    remoteState is RemoteSessionState.Reconnecting || remoteState is RemoteSessionState.HistoryGap -> ProductDanger
    state == SessionState.INTERRUPTED -> ProductDanger
    !sessionOnline -> ProductMuted
    else -> ProductPositive
}

@Composable
private fun RemoteSessionState?.localizedTerminalStatus(): String = stringResource(
    when (this) {
        null -> R.string.terminal_connecting
        is RemoteSessionState.Live -> R.string.terminal_live_state
        is RemoteSessionState.Reconnecting -> R.string.terminal_reconnecting
        is RemoteSessionState.ReadOnly -> R.string.terminal_read_only
        is RemoteSessionState.Controlling -> R.string.terminal_controlling
        is RemoteSessionState.Exited -> R.string.terminal_exited
        is RemoteSessionState.HistoryGap -> R.string.terminal_history_gap
    },
)

@Composable
private fun StatusDot(color: Color, contentDescription: String) {
    Box(
        modifier = Modifier
            .size(9.dp)
            .clip(RoundedCornerShape(50))
            .background(color)
            .border(1.dp, color.copy(alpha = 0.55f), RoundedCornerShape(50)),
        contentAlignment = Alignment.Center,
    ) {}
}

internal enum class SessionControlRequestTrigger {
    NONE,
    INITIAL_OPEN,
    REALTIME_LIVE,
    FOREGROUND_RESUME,
}

internal data class SessionControlRequestEffectKey(
    val foregroundGeneration: Long,
    val trigger: SessionControlRequestTrigger,
)

internal fun nextForegroundGeneration(current: Long): Long {
    require(current >= 0) { "foreground generation must not be negative" }
    check(current < Long.MAX_VALUE) { "foreground generation exhausted" }
    return current + 1L
}

/**
 * Returns a key only while control should be requested. Including the
 * foreground generation ensures stop/start cannot be lost to Boolean state
 * conflation, while excluding ACK cursors keeps ordinary output recomposition
 * from producing another lease request.
 */
internal fun sessionControlRequestEffectKey(
    foreground: Boolean,
    foregroundGeneration: Long,
    online: Boolean,
    remoteState: RemoteSessionState?,
): SessionControlRequestEffectKey? {
    require(foregroundGeneration >= 0) { "foreground generation must not be negative" }
    val trigger = sessionControlRequestTrigger(foreground, online, remoteState)
    return if (trigger == SessionControlRequestTrigger.NONE) null else {
        SessionControlRequestEffectKey(foregroundGeneration, trigger)
    }
}

/**
 * Produces a stable Compose effect key. In particular, changing realtime ACK
 * cursors must not result in another lease request, while Reconnecting -> Live
 * must result in exactly one new opportunity to acquire control.
 */
internal fun sessionControlRequestTrigger(
    foreground: Boolean,
    online: Boolean,
    remoteState: RemoteSessionState?,
): SessionControlRequestTrigger = when {
    !foreground -> SessionControlRequestTrigger.NONE
    !online -> SessionControlRequestTrigger.NONE
    remoteState == null -> SessionControlRequestTrigger.INITIAL_OPEN
    remoteState is RemoteSessionState.Reconnecting &&
        remoteState.reason == SESSION_BOOTSTRAP_IN_PROGRESS_REASON ->
        SessionControlRequestTrigger.INITIAL_OPEN
    remoteState is RemoteSessionState.Live -> SessionControlRequestTrigger.REALTIME_LIVE
    // ReadOnly is also reached after a transport reconnect and after Windows
    // local input fences the previous lease. Both are transient arbitration
    // boundaries, not a request to leave this foreground same-account client
    // permanently unable to type. The stable effect key makes this one acquire
    // attempt per transition, so a held lease cannot create a recompose loop.
    remoteState is RemoteSessionState.ReadOnly -> SessionControlRequestTrigger.FOREGROUND_RESUME
    else -> SessionControlRequestTrigger.NONE
}

internal fun SessionSummary.resolveDisplayedRemoteState(actual: RemoteSessionState?): RemoteSessionState? = when {
    state == SessionState.EXITED -> RemoteSessionState.Exited(
        lastAckedSequence = actual?.lastAckedSequence ?: 0,
        exitCode = (actual as? RemoteSessionState.Exited)?.exitCode,
    )
    actual is RemoteSessionState.Exited -> actual
    !online -> actual as? RemoteSessionState.Reconnecting
        ?: RemoteSessionState.Reconnecting(
            lastAckedSequence = actual?.lastAckedSequence ?: 0,
            reason = "catalog temporarily reported offline",
        )
    else -> actual
}

@Composable
private fun TerminalSurface(
    session: SessionSummary,
    engine: TermuxTerminalEngine?,
    remoteState: RemoteSessionState?,
    transcriptState: TranscriptViewportState?,
    liveAcknowledgement: ULong?,
    bootstrapSource: ProductBootstrapSource?,
    onLoadTranscript: () -> Unit,
    onClearTranscript: () -> Unit,
    terminalPreferences: TerminalDisplayPreferences,
    shortcutPreferences: TerminalShortcutPreferences,
    onTerminalAction: (TerminalAction) -> Unit,
    onTerminalCustomShortcut: (TerminalCustomShortcut) -> Unit,
    onPasteRequested: () -> Unit,
    terminalInputResumeRegistry: TerminalInputResumeRegistry,
    modifier: Modifier = Modifier,
) {
    val placeholder = remember(session.id, session.columns, session.rows) {
        TermuxTerminalEngine(
            initialSize = TerminalSize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1)),
        ).also {
            it.applyTheme(productTerminalTheme(terminalPreferences))
            it.setReadOnly(true)
        }
    }
    val activeEngine = engine ?: placeholder
    val transcriptViewport = remember(session.id) { TermuxTranscriptViewport() }
    val liveViewHolder = remember(session.id) { arrayOfNulls<View>(1) }
    var dockBounds by remember(session.id) { mutableStateOf<TailFirstPixelBounds?>(null) }
    val latestTranscriptState = rememberUpdatedState(transcriptState)
    val latestLoadTranscript = rememberUpdatedState(onLoadTranscript)
    val latestClearTranscript = rememberUpdatedState(onClearTranscript)
    var overlayRequested by remember(session.id) { mutableStateOf(false) }
    var localLoadingGuard by remember(session.id) { mutableStateOf(false) }
    var transcriptRendererFailed by remember(session.id) { mutableStateOf(false) }
    var transcriptRendererRetryToken by remember(session.id) { mutableStateOf(false) }
    val overlayVisible = shouldShowTranscriptOverlay(overlayRequested, transcriptState)

    fun requestOlderRows() {
        val state = latestTranscriptState.value ?: return
        // StateFlow is the authoritative in-flight state. Compose may apply the
        // LaunchedEffect that clears this frame-local guard one frame later;
        // release a stale guard here so a completed page cannot swallow the
        // next genuine boundary request. Same-cursor races remain coalesced by
        // ProductionProductRepository.
        if (localLoadingGuard && !state.loading) localLoadingGuard = false
        if (!canRequestOlderTranscript(state, localLoadingGuard, transcriptRendererFailed)) return
        overlayRequested = true
        localLoadingGuard = true
        latestLoadTranscript.value()
    }

    fun retryTranscript() {
        dispatchTranscriptRetry(
            action = transcriptRetryAction(
                latestTranscriptState.value,
                transcriptRendererFailed,
                localLoadingGuard,
            ),
            reapplyCurrentWindow = {
                // Re-run only the detached renderer projection. The repository
                // cursor and its network loader are deliberately untouched.
                transcriptRendererRetryToken = !transcriptRendererRetryToken
            },
            loadOlderPage = ::requestOlderRows,
        )
    }

    fun returnToLiveTail() {
        overlayRequested = false
        localLoadingGuard = false
        transcriptRendererFailed = false
        activeEngine.scrollToLatest()
        latestClearTranscript.value()
    }

    fun resumeLiveAfterAcceptedInput() {
        val effect = terminalInputResumeEffect(
            accepted = true,
            // Read current mutable state rather than the last composed Boolean:
            // input can be accepted in the same frame that requested history.
            detachedOverlayActive = shouldShowTranscriptOverlay(
                overlayRequested,
                latestTranscriptState.value,
            ),
        )
        if (effect.scrollToLatest) activeEngine.scrollToLatest()
        if (effect.closeDetachedOverlay) {
            overlayRequested = false
            localLoadingGuard = false
            transcriptRendererFailed = false
            latestClearTranscript.value()
        }
    }

    DisposableEffect(session.id, activeEngine, terminalInputResumeRegistry) {
        activeEngine.setOnAcceptedLocalUserInputListener(::resumeLiveAfterAcceptedInput)
        val registration = terminalInputResumeRegistry.register(session.id, ::resumeLiveAfterAcceptedInput)
        onDispose {
            // The placeholder and this effect are siblings; disposal ordering
            // must not turn an already-disposed placeholder into a UI crash.
            runCatching { activeEngine.setOnAcceptedLocalUserInputListener(null) }
            registration.close()
        }
    }

    if (BuildConfig.DEBUG) {
        val tailFirstSurfaceController = object : TailFirstTerminalSurfaceController {
            override fun observe(): TailFirstSurfaceObservation {
                val liveText = runCatching(activeEngine::visibleText).getOrDefault("")
                val history = runCatching(transcriptViewport::debugState).getOrNull()
                val visibleText = if (overlayVisible) history?.visibleText.orEmpty() else liveText
                val liveView = liveViewHolder[0]
                val displayBounds = liveView?.rootView?.let(::tailFirstViewBounds)
                val terminalBounds = liveView?.let(::tailFirstViewBounds)
                val insets = liveView?.let(ViewCompat::getRootWindowInsets)
                val imeVisible = insets?.isVisible(WindowInsetsCompat.Type.ime()) == true
                val imeInsets = insets?.getInsets(WindowInsetsCompat.Type.ime())
                val imeBounds = if (imeVisible && displayBounds != null && imeInsets != null) {
                    TailFirstPixelBounds(
                        left = displayBounds.left + imeInsets.left,
                        top = displayBounds.bottom - imeInsets.bottom,
                        right = displayBounds.right - imeInsets.right,
                        bottom = displayBounds.bottom,
                    )
                } else {
                    null
                }
                return TailFirstSurfaceObservation(
                    historyOverlay = overlayVisible,
                    visibleText = visibleText,
                    liveText = liveText,
                    viewportSignature = tailFirstViewportSignature(
                        sessionId = session.id,
                        overlay = overlayVisible,
                        state = transcriptState,
                        firstVisibleLineId = history?.firstVisibleLineId,
                        visibleText = visibleText,
                    ),
                    displayBounds = displayBounds,
                    terminalBounds = terminalBounds,
                    bottomBarBounds = dockBounds,
                    imeBounds = imeBounds,
                    imeVisible = imeVisible,
                )
            }

            override fun pageUp() {
                // The acceptance bridge can arrive in the frame between the overlay state
                // becoming visible and AndroidView attaching its TerminalView.  Drive the
                // same bounded loader directly in that narrow debug-only path so the result
                // still proves one cursor page, without turning a missing weak view into a
                // 45-second false timeout.
                if (overlayVisible) requestOlderRows()
                else activeEngine.scrollPastTopBoundary()
            }

            override fun openHistory(): Boolean {
                if (overlayVisible) return false
                activeEngine.scrollPastTopBoundary()
                return true
            }

            override fun closeHistory() = returnToLiveTail()

            override fun showIme() {
                if (overlayVisible) returnToLiveTail()
                liveViewHolder[0]?.let { view ->
                    view.isFocusable = true
                    view.isFocusableInTouchMode = true
                    view.requestFocus()
                    val inputMethod = view.context.getSystemService(android.content.Context.INPUT_METHOD_SERVICE)
                        as? InputMethodManager
                    inputMethod?.showSoftInput(view, InputMethodManager.SHOW_IMPLICIT)
                }
            }
        }
        SideEffect {
            TailFirstTerminalSurfaceRegistry.register(session.id, tailFirstSurfaceController)
        }
        DisposableEffect(session.id) {
            onDispose { TailFirstTerminalSurfaceRegistry.unregister(session.id) }
        }
    }

    SideEffect {
        val theme = productTerminalTheme(terminalPreferences)
        activeEngine.applyTheme(theme)
        activeEngine.setReadOnly(remoteState !is RemoteSessionState.Controlling)
        transcriptViewport.applyTheme(theme)
    }
    LaunchedEffect(
        transcriptState?.viewportGeneration,
        transcriptState?.pages,
        session.columns,
        transcriptRendererRetryToken,
    ) {
        val pages = transcriptState?.pages.orEmpty()
        val failure = applyTranscriptViewportPagesSafely(
            viewport = transcriptViewport,
            pages = pages,
            authoritativeColumns = session.columns,
        )
        transcriptRendererFailed = failure != null
        if (failure != null && BuildConfig.DEBUG) {
            Log.e(
                "TermLinkTranscript",
                "Detached transcript renderer rejected the authenticated projection",
                failure,
            )
        }
    }
    LaunchedEffect(transcriptState?.loading) {
        if (transcriptState?.loading == false) localLoadingGuard = false
    }
    LaunchedEffect(transcriptState?.viewportGeneration) {
        if (transcriptState?.pages.isNullOrEmpty() && transcriptState?.loading != true) {
            overlayRequested = false
        }
    }
    DisposableEffect(placeholder) {
        onDispose { placeholder.dispose() }
    }
    DisposableEffect(transcriptViewport) {
        onDispose { transcriptViewport.close() }
    }
    var dockExpanded by rememberSaveable(session.id) { mutableStateOf(true) }
    Column(
        modifier = modifier
            .background(Color(terminalPreferences.backgroundArgb)),
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f),
        ) {
            key(activeEngine, terminalPreferences.fontSizeSp, terminalPreferences.lineSpacingMultiplier) {
                AndroidView(
                    factory = { context ->
                        activeEngine.createView(
                            context = context,
                            textSizeSp = terminalPreferences.fontSizeSp,
                            lineSpacingMultiplier = terminalPreferences.lineSpacingMultiplier,
                            onScrollPastTop = ::requestOlderRows,
                        ).also { liveViewHolder[0] = it }
                    },
                    modifier = Modifier
                        .fillMaxSize()
                        .testTag(terminalTestTag(session.id)),
                )
            }
            if (overlayVisible) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(Color(terminalPreferences.backgroundArgb))
                        .testTag(transcriptOverlayTestTag(session.id)),
                ) {
                    if (!transcriptState?.pages.isNullOrEmpty()) {
                        key(
                            transcriptViewport,
                            terminalPreferences.fontSizeSp,
                            terminalPreferences.lineSpacingMultiplier,
                        ) {
                            AndroidView(
                                factory = { context ->
                                    transcriptViewport.createView(
                                        context = context,
                                        textSizeSp = terminalPreferences.fontSizeSp,
                                        lineSpacingMultiplier = terminalPreferences.lineSpacingMultiplier,
                                        onScrollPastTop = ::requestOlderRows,
                                        onScrollPastBottom = ::returnToLiveTail,
                                    )
                                },
                                modifier = Modifier
                                    .fillMaxSize()
                                    .testTag(transcriptViewportTestTag(session.id)),
                            )
                        }
                    }
                    when {
                        transcriptState?.loading == true || localLoadingGuard -> Row(
                            modifier = Modifier
                                .align(Alignment.TopCenter)
                                .background(Color.Black.copy(alpha = 0.82f), RoundedCornerShape(6.dp))
                                .padding(horizontal = 10.dp, vertical = 6.dp)
                                .testTag(transcriptLoadingTestTag(session.id)),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
                            Text(
                                stringResource(R.string.terminal_history_loading),
                                color = Color.White,
                                style = MaterialTheme.typography.labelMedium,
                            )
                        }
                        transcriptState?.error != null || transcriptRendererFailed -> Column(
                            modifier = Modifier
                                .align(Alignment.TopCenter)
                                .background(Color.Black.copy(alpha = 0.86f), RoundedCornerShape(6.dp))
                                .padding(start = 10.dp, end = 2.dp, top = 6.dp, bottom = 2.dp),
                        ) {
                            Text(
                                stringResource(R.string.terminal_history_unavailable),
                                color = Color.White,
                                style = MaterialTheme.typography.labelMedium,
                            )
                            Row {
                                TextButton(
                                    onClick = ::retryTranscript,
                                    modifier = Modifier.testTag(transcriptRetryTestTag(session.id)),
                                ) {
                                    Text(stringResource(R.string.terminal_history_retry))
                                }
                                if (transcriptRendererFailed) {
                                    TextButton(
                                        onClick = ::returnToLiveTail,
                                        modifier = Modifier.testTag(transcriptReturnLiveTestTag(session.id)),
                                    ) {
                                        Text(stringResource(R.string.terminal_live))
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        TerminalActionBar(
            enabled = remoteState is RemoteSessionState.Controlling,
            onAction = onTerminalAction,
            onCustomShortcut = onTerminalCustomShortcut,
            onPasteRequested = onPasteRequested,
            shortcutPreferences = shortcutPreferences,
            expanded = dockExpanded,
            onExpandedChanged = { dockExpanded = it },
            modifier = Modifier
                .padding(horizontal = 6.dp)
                .onGloballyPositioned { coordinates ->
                    // AndroidView bounds are sampled with getLocationOnScreen;
                    // keep the Compose dock in that same coordinate space so
                    // IME overlap assertions do not compare unlike origins.
                    val position = coordinates.positionOnScreen()
                    dockBounds = TailFirstPixelBounds(
                        left = position.x.roundToInt(),
                        top = position.y.roundToInt(),
                        right = (position.x + coordinates.size.width).roundToInt(),
                        bottom = (position.y + coordinates.size.height).roundToInt(),
                    )
                }
                .testTag(terminalDockTestTag(session.id)),
        )
    }
}

@Composable
private fun ActivityPage(
    snapshot: ProductSnapshot,
    onOpenHistory: () -> Unit,
) {
    PageHeader(stringResource(R.string.nav_activity), stringResource(R.string.page_activity_subtitle), Icons.Filled.Timeline)
    Text(
        stringResource(R.string.activity_device_local_label),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Text(
        stringResource(R.string.activity_device_local_copy),
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    ConnectionBanner(snapshot = snapshot)
    if (snapshot.activity.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Timeline,
            title = if (snapshot.signedIn) stringResource(R.string.empty_no_activity) else stringResource(R.string.empty_sign_in_activity),
            detail = stringResource(R.string.activity_device_local_copy),
            actionLabel = null,
            onAction = {},
        )
    } else {
        snapshot.activity.forEachIndexed { index, event ->
            if (index > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
            ActivityRow(event)
        }
    }
    OutlinedButton(onClick = onOpenHistory, modifier = Modifier.heightIn(min = 48.dp)) {
        Icon(Icons.Filled.History, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(6.dp))
            Text(stringResource(R.string.action_view_history))
    }
}

@Composable
private fun ActivityRow(event: ActivityEntry) {
    val color = when (event.severity) {
        Severity.INFO -> MaterialTheme.colorScheme.primary
        Severity.SUCCESS -> MaterialTheme.colorScheme.secondary
        Severity.WARNING -> MaterialTheme.colorScheme.tertiary
        Severity.ERROR -> MaterialTheme.colorScheme.error
    }
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 10.dp),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(Icons.Filled.Info, contentDescription = event.severity.name, tint = color)
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(safeActivityDetail(event.title), style = MaterialTheme.typography.titleSmall)
            Text(safeActivityDetail(event.detail), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Text(formatInstant(event.occurredAt), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun HistoryPage(
    snapshot: ProductSnapshot,
    selectedHistoryId: String?,
    onHistorySelected: (HistoryEntry) -> Unit,
    onReplay: (HistoryEntry) -> Unit,
    historyEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    terminalPreferences: TerminalDisplayPreferences,
) {
    PageHeader(stringResource(R.string.nav_history), stringResource(R.string.page_history_subtitle), Icons.Filled.History)
    ConnectionBanner(snapshot = snapshot)
    if (snapshot.history.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.History,
            title = if (snapshot.signedIn) stringResource(R.string.empty_no_history) else stringResource(R.string.empty_sign_in_history),
            detail = stringResource(R.string.server_history_empty_copy),
            actionLabel = null,
            onAction = {},
        )
        return
    }
    val selected = snapshot.history.firstOrNull { it.historyObject.id.toString() == selectedHistoryId }
        ?: snapshot.history.first()
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        snapshot.history.forEach { entry ->
            FilterChip(
                selected = entry.historyObject.id == selected.historyObject.id,
                onClick = { onHistorySelected(entry) },
                label = { Text(entry.sessionLabel, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                leadingIcon = { Icon(Icons.Filled.History, contentDescription = null, modifier = Modifier.size(18.dp)) },
                modifier = Modifier.heightIn(min = 48.dp),
            )
        }
    }
    HistoryReplaySurface(
        entry = selected,
        engine = historyEngine(selected.historyObject.id),
        terminalPreferences = terminalPreferences,
        onReplay = onReplay,
    )
}

@Composable
private fun HistoryReplaySurface(
    entry: HistoryEntry,
    engine: TermuxTerminalEngine?,
    terminalPreferences: TerminalDisplayPreferences,
    onReplay: (HistoryEntry) -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(entry.sessionLabel, style = MaterialTheme.typography.titleMedium)
                    Text(
                        stringResource(
                            R.string.history_object_summary,
                            entry.historyObject.kind.localizedLabel(),
                            entry.historyObject.sizeBytes,
                        ),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                if (entry.historyObject.kind == dev.termlink.model.HistoryObject.Kind.CHUNK) {
                    OutlinedButton(onClick = { onReplay(entry) }, modifier = Modifier.heightIn(min = 48.dp)) {
                        Icon(Icons.Filled.Replay, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_replay_output))
                    }
                }
            }
            if (entry.historyObject.kind == dev.termlink.model.HistoryObject.Kind.SNAPSHOT) {
                Text(
                    stringResource(R.string.snapshot_replay_unavailable),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.tertiary,
                )
            }
            HistoryTerminalSurface(entry = entry, engine = engine, terminalPreferences = terminalPreferences)
        }
    }
}

@Composable
private fun HistoryTerminalSurface(
    entry: HistoryEntry,
    engine: TermuxTerminalEngine?,
    terminalPreferences: TerminalDisplayPreferences,
) {
    val placeholder = remember(entry.historyObject.id) {
        TermuxTerminalEngine().also {
            it.applyTheme(productTerminalTheme(terminalPreferences))
            it.setReadOnly(true)
        }
    }
    val activeEngine = engine ?: placeholder
    SideEffect {
        activeEngine.applyTheme(productTerminalTheme(terminalPreferences))
        activeEngine.setReadOnly(true)
    }
    DisposableEffect(placeholder) { onDispose { placeholder.dispose() } }
    key(activeEngine, terminalPreferences.fontSizeSp, terminalPreferences.lineSpacingMultiplier) {
        AndroidView(
            factory = { context ->
                activeEngine.createView(
                    context = context,
                    textSizeSp = terminalPreferences.fontSizeSp,
                    lineSpacingMultiplier = terminalPreferences.lineSpacingMultiplier,
                )
            },
            modifier = Modifier
                .fillMaxWidth()
                .height(240.dp)
                .clip(ProductShape)
                .background(Color(terminalPreferences.backgroundArgb)),
        )
    }
}

@Composable
private fun SettingsPage(
    snapshot: ProductSnapshot,
    onSignIn: () -> Unit,
    onSignOut: () -> Unit,
    onCheckForUpdate: () -> Unit,
    themePreference: ProductThemePreference,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit,
    terminalPreferences: TerminalDisplayPreferences,
    onTerminalPreferencesChanged: (TerminalDisplayPreferences) -> Unit,
    shortcutPreferences: TerminalShortcutPreferences,
    onShortcutPreferencesChanged: (TerminalShortcutPreferences) -> Unit,
) {
    PageHeader(stringResource(R.string.nav_settings), stringResource(R.string.page_settings_subtitle), Icons.Filled.Settings)

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(
                    Icons.Filled.AccountCircle,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(28.dp),
                )
                Text(
                    snapshot.accountLabel ?: stringResource(R.string.account_login_fallback),
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            SettingRow(
                icon = if (snapshot.isConnected()) Icons.Filled.Wifi else Icons.Filled.WifiOff,
                title = stringResource(R.string.settings_server_connection),
                detail = when (snapshot.connectionState) {
                    ProductConnectionState.CONNECTED -> stringResource(R.string.status_signed_in_last_sync)
                    ProductConnectionState.CONNECTING -> stringResource(R.string.status_signed_in_awaiting_sync)
                    ProductConnectionState.ERROR -> stringResource(R.string.connection_error_copy)
                    ProductConnectionState.CONFIGURED -> stringResource(R.string.status_endpoint_configured)
                    ProductConnectionState.SIGNED_OUT -> stringResource(R.string.status_server_not_configured)
                },
            )
            SettingRow(
                icon = Icons.Filled.CheckCircle,
                title = stringResource(R.string.settings_device_proof),
                detail = stringResource(R.string.settings_device_proof_copy),
            )
            SettingRow(
                icon = Icons.Filled.Lock,
                title = stringResource(R.string.settings_input_safety),
                detail = stringResource(R.string.terminal_waiting_lease),
            )
            HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (snapshot.signedIn) {
                    OutlinedButton(
                        onClick = onSignOut,
                        modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.AutoMirrored.Filled.Logout, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_sign_out))
                    }
                } else {
                    OutlinedButton(
                        onClick = onSignIn,
                        modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.AutoMirrored.Filled.Login, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_sign_in))
                    }
                }
            }
        }
    }

    SettingsSectionHeader(
        title = stringResource(R.string.settings_appearance_title),
        subtitle = stringResource(R.string.settings_appearance_copy),
        icon = Icons.Filled.Settings,
    )
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState())
                .padding(14.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            ProductThemePreference.entries.forEach { option ->
                FilterChip(
                    selected = themePreference == option,
                    onClick = { onThemePreferenceChanged(option) },
                    label = { Text(option.displayLabel()) },
                    modifier = Modifier.heightIn(min = 48.dp),
                )
            }
        }
    }

    SettingsSectionHeader(
        stringResource(R.string.settings_terminal_title),
        stringResource(R.string.settings_terminal_copy),
        Icons.Filled.Terminal,
    )
    TerminalDisplaySettings(
        preferences = terminalPreferences,
        onPreferencesChanged = onTerminalPreferencesChanged,
    )

    SettingsSectionHeader(
        stringResource(R.string.settings_shortcuts_title),
        stringResource(R.string.settings_shortcuts_copy),
        Icons.Filled.Terminal,
    )
    TerminalShortcutSettings(
        preferences = shortcutPreferences,
        onPreferencesChanged = onShortcutPreferencesChanged,
    )

    SettingsSectionHeader(
        title = stringResource(R.string.settings_about),
        subtitle = stringResource(R.string.settings_about_copy),
        icon = Icons.Filled.Info,
    )
    SettingsAboutCard(
        update = snapshot.updateStatus,
        onCheckForUpdate = onCheckForUpdate,
    )
}

@Composable
private fun SettingsSectionHeader(title: String, subtitle: String, icon: ImageVector) {
    Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Icon(
            icon,
            contentDescription = title,
            tint = MaterialTheme.colorScheme.primary,
            modifier = Modifier.size(22.dp),
        )
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(
                subtitle,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun SettingsAboutCard(
    update: ProductUpdateStatus,
    onCheckForUpdate: () -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(
                        stringResource(R.string.settings_product_shell),
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold,
                    )
                    Text(
                        stringResource(
                            R.string.settings_version,
                            BuildConfig.VERSION_NAME,
                            BuildConfig.VERSION_CODE,
                        ),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                OutlinedButton(onClick = onCheckForUpdate, modifier = Modifier.heightIn(min = 48.dp)) {
                    Icon(Icons.Filled.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.action_check_updates))
                }
            }
            Text(
                when (update.decision) {
                    ProductUpdateDecision.NOT_CONFIGURED -> stringResource(R.string.update_not_configured)
                    ProductUpdateDecision.NO_UPDATE -> stringResource(R.string.update_no_update)
                    ProductUpdateDecision.OPTIONAL -> stringResource(R.string.update_optional, update.versionName ?: "-")
                    ProductUpdateDecision.REQUIRED -> stringResource(R.string.update_required, update.versionName ?: "-")
                    ProductUpdateDecision.ERROR -> stringResource(R.string.update_error)
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (update.decision == ProductUpdateDecision.ERROR) {
                update.message?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
            update.releaseNotes?.let {
                Text(
                    it,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 3,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Text(
                stringResource(R.string.settings_updates_copy),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    stringResource(R.string.settings_author),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(
                    stringResource(R.string.settings_author_name),
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.SemiBold,
                )
            }
        }
    }
}

@Composable
private fun TerminalDisplaySettings(
    preferences: TerminalDisplayPreferences,
    onPreferencesChanged: (TerminalDisplayPreferences) -> Unit,
) {
    val value = preferences.normalized
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(
                stringResource(R.string.settings_terminal_font_size, value.fontSizeSp),
                style = MaterialTheme.typography.titleSmall,
            )
            Slider(
                value = value.fontSizeSp,
                onValueChange = { onPreferencesChanged(value.copy(fontSizeSp = it)) },
                valueRange = 10f..24f,
                steps = 13,
            )
            Text(
                stringResource(R.string.settings_terminal_line_spacing, value.lineSpacingMultiplier),
                style = MaterialTheme.typography.titleSmall,
            )
            Slider(
                value = value.lineSpacingMultiplier,
                onValueChange = { onPreferencesChanged(value.copy(lineSpacingMultiplier = it)) },
                valueRange = 0.8f..1.8f,
                steps = 9,
            )
            TerminalColorPickerSetting(
                title = stringResource(R.string.settings_terminal_foreground),
                argb = value.foregroundArgb,
                onArgbChanged = { onPreferencesChanged(value.copy(foregroundArgb = it)) },
            )
            TerminalColorPickerSetting(
                title = stringResource(R.string.settings_terminal_background),
                argb = value.backgroundArgb,
                onArgbChanged = { onPreferencesChanged(value.copy(backgroundArgb = it)) },
            )
        }
    }
}

@Composable
private fun TerminalColorPickerSetting(
    title: String,
    argb: Int,
    onArgbChanged: (Int) -> Unit,
) {
    var pickerOpen by rememberSaveable(title) { mutableStateOf(false) }
    OutlinedButton(
        onClick = { pickerOpen = true },
        modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp),
        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 8.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Box(
                modifier = Modifier
                    .size(32.dp)
                    .clip(RoundedCornerShape(7.dp))
                    .background(Color(argb))
                    .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(7.dp)),
            )
            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(title, style = MaterialTheme.typography.titleSmall)
                Text(
                    terminalColorHex(argb),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(stringResource(R.string.settings_color_picker_action))
        }
    }
    if (pickerOpen) {
        TerminalColorPickerDialog(
            title = title,
            initialArgb = argb,
            onDismiss = { pickerOpen = false },
            onConfirm = {
                onArgbChanged(it)
                pickerOpen = false
            },
        )
    }
}

@Composable
private fun TerminalColorPickerDialog(
    title: String,
    initialArgb: Int,
    onDismiss: () -> Unit,
    onConfirm: (Int) -> Unit,
) {
    val initialHsv = remember(initialArgb) { terminalColorHsv(initialArgb) }
    var hue by remember(initialArgb) { mutableStateOf(initialHsv[0]) }
    var saturation by remember(initialArgb) { mutableStateOf(initialHsv[1]) }
    var brightness by remember(initialArgb) { mutableStateOf(initialHsv[2]) }
    val selectedArgb = opaqueArgbFromHsv(hue, saturation, brightness)
    val hueColors = remember {
        (0..6).map { step -> Color(opaqueArgbFromHsv(step * 60f, 1f, 1f)) }
    }

    fun selectArgb(argb: Int) {
        val hsv = terminalColorHsv(argb)
        hue = hsv[0]
        saturation = hsv[1]
        brightness = hsv[2]
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    Box(
                        modifier = Modifier
                            .size(40.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .background(Color(selectedArgb))
                            .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(8.dp)),
                    )
                    Column {
                        Text(terminalColorHex(selectedArgb), style = MaterialTheme.typography.titleSmall)
                        Text(
                            stringResource(R.string.settings_color_picker_hint),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                Canvas(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(180.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .pointerInput(hue) {
                            awaitEachGesture {
                                val down = awaitFirstDown()
                                fun update(position: Offset) {
                                    saturation = (position.x / size.width.coerceAtLeast(1)).coerceIn(0f, 1f)
                                    brightness = (1f - position.y / size.height.coerceAtLeast(1)).coerceIn(0f, 1f)
                                }
                                update(down.position)
                                var pressed = true
                                while (pressed) {
                                    val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id }
                                    if (change == null) {
                                        pressed = false
                                    } else {
                                        if (change.pressed) update(change.position)
                                        change.consume()
                                        pressed = change.pressed
                                    }
                                }
                            }
                        },
                ) {
                    drawRect(
                        brush = Brush.horizontalGradient(
                            listOf(Color.White, Color(opaqueArgbFromHsv(hue, 1f, 1f))),
                        ),
                    )
                    drawRect(brush = Brush.verticalGradient(listOf(Color.Transparent, Color.Black)))
                    val marker = Offset(saturation * size.width, (1f - brightness) * size.height)
                    drawCircle(Color.Black.copy(alpha = 0.85f), radius = 10.dp.toPx(), center = marker)
                    drawCircle(Color.White, radius = 8.dp.toPx(), center = marker, style = Stroke(2.dp.toPx()))
                }
                Canvas(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(32.dp)
                        .clip(RoundedCornerShape(8.dp))
                        .pointerInput(Unit) {
                            awaitEachGesture {
                                val down = awaitFirstDown()
                                fun update(position: Offset) {
                                    hue = (position.x / size.width.coerceAtLeast(1) * 360f).coerceIn(0f, 359.999f)
                                }
                                update(down.position)
                                var pressed = true
                                while (pressed) {
                                    val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id }
                                    if (change == null) {
                                        pressed = false
                                    } else {
                                        if (change.pressed) update(change.position)
                                        change.consume()
                                        pressed = change.pressed
                                    }
                                }
                            }
                        },
                ) {
                    drawRect(brush = Brush.horizontalGradient(hueColors))
                    val markerX = hue / 360f * size.width
                    drawCircle(
                        color = Color.Black.copy(alpha = 0.85f),
                        radius = 9.dp.toPx(),
                        center = Offset(markerX, size.height / 2f),
                    )
                    drawCircle(
                        color = Color.White,
                        radius = 7.dp.toPx(),
                        center = Offset(markerX, size.height / 2f),
                        style = Stroke(2.dp.toPx()),
                    )
                }
                Text(
                    stringResource(R.string.settings_color_picker_presets),
                    style = MaterialTheme.typography.labelLarge,
                )
                Row(
                    modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(2.dp),
                ) {
                    terminalColorPresets.forEach { preset ->
                        val presetDescription = terminalColorHex(preset)
                        IconButton(
                            onClick = { selectArgb(preset) },
                            modifier = Modifier.semantics { contentDescription = presetDescription },
                        ) {
                            Box(
                                modifier = Modifier
                                    .size(28.dp)
                                    .clip(RoundedCornerShape(6.dp))
                                    .background(Color(preset))
                                    .border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(6.dp)),
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { onConfirm(selectedArgb) }, modifier = Modifier.heightIn(min = 48.dp)) {
                Text(stringResource(R.string.action_save))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = 48.dp)) {
                Text(stringResource(R.string.action_cancel))
            }
        },
    )
}

private val terminalColorPresets = listOf(
    0xFF080B10.toInt(),
    0xFF121820.toInt(),
    0xFF263238.toInt(),
    0xFF465363.toInt(),
    0xFFE9EEF5.toInt(),
    0xFFFFFFFF.toInt(),
    0xFF9CB2FF.toInt(),
    0xFF65D6A1.toInt(),
    0xFFF1C96B.toInt(),
    0xFFFF8C91.toInt(),
    0xFFC99DEB.toInt(),
    0xFF6EDDE0.toInt(),
)

internal fun terminalColorHsv(argb: Int): FloatArray = FloatArray(3).also { hsv ->
    AndroidColor.colorToHSV(argb or 0xFF000000.toInt(), hsv)
}

internal fun opaqueArgbFromHsv(hue: Float, saturation: Float, brightness: Float): Int =
    AndroidColor.HSVToColor(
        floatArrayOf(
            hue.coerceIn(0f, 359.999f),
            saturation.coerceIn(0f, 1f),
            brightness.coerceIn(0f, 1f),
        ),
    )

internal fun terminalColorHex(argb: Int): String = "#%06X".format(Locale.ROOT, argb and 0x00FFFFFF)

@Composable
private fun TerminalShortcutSettings(
    preferences: TerminalShortcutPreferences,
    onPreferencesChanged: (TerminalShortcutPreferences) -> Unit,
) {
    val value = preferences.normalized
    val orderedItems = orderedTerminalShortcutItems(value)
    var shortcutEditorOpen by rememberSaveable { mutableStateOf(false) }
    var editingShortcutId by rememberSaveable { mutableStateOf<String?>(null) }

    fun closeEditor() {
        shortcutEditorOpen = false
        editingShortcutId = null
    }

    fun moveItem(index: Int, delta: Int) {
        val destination = index + delta
        if (index !in orderedItems.indices || destination !in orderedItems.indices) return
        val nextOrder = value.shortcutOrder.toMutableList()
        val movingKey = orderedItems[index].orderKey
        val destinationKey = orderedItems[destination].orderKey
        val sourceOrderIndex = nextOrder.indexOf(movingKey)
        val destinationOrderIndex = nextOrder.indexOf(destinationKey)
        if (sourceOrderIndex < 0 || destinationOrderIndex < 0) return
        nextOrder[sourceOrderIndex] = destinationKey
        nextOrder[destinationOrderIndex] = movingKey
        onPreferencesChanged(value.copy(shortcutOrder = nextOrder))
    }

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            orderedItems.forEachIndexed { index, item ->
                if (index > 0) {
                    HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.24f))
                }
                val title: String
                val typeLabel: String
                val detail: String
                when (item) {
                    is TerminalShortcutItem.BuiltIn -> {
                        title = stringResource(terminalActionLabelResource(item.action))
                        typeLabel = stringResource(R.string.shortcut_type_built_in_key)
                        detail = value.label(item.action)
                    }
                    is TerminalShortcutItem.Custom -> {
                        title = item.shortcut.displayLabel
                        typeLabel = stringResource(terminalCustomShortcutTypeLabelResource(item.shortcut.type))
                        detail = item.shortcut.value
                    }
                }
                Row(
                    modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(2.dp),
                ) {
                    Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(
                            title,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.labelLarge,
                        )
                        Text(
                            typeLabel,
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.primary,
                        )
                        Text(
                            detail,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    if (item is TerminalShortcutItem.Custom) {
                        IconButton(
                            onClick = {
                                editingShortcutId = item.shortcut.id
                                shortcutEditorOpen = true
                            },
                            modifier = Modifier.size(40.dp),
                        ) {
                            Icon(
                                Icons.Filled.Edit,
                                contentDescription = stringResource(R.string.shortcut_edit_custom),
                            )
                        }
                    }
                    IconButton(
                        onClick = { moveItem(index, -1) },
                        enabled = index > 0,
                        modifier = Modifier.size(40.dp),
                    ) {
                        Icon(Icons.Filled.KeyboardArrowUp, contentDescription = stringResource(R.string.shortcut_move_up))
                    }
                    IconButton(
                        onClick = { moveItem(index, 1) },
                        enabled = index < orderedItems.lastIndex,
                        modifier = Modifier.size(40.dp),
                    ) {
                        Icon(Icons.Filled.KeyboardArrowDown, contentDescription = stringResource(R.string.shortcut_move_down))
                    }
                }
            }
            OutlinedButton(
                onClick = {
                    editingShortcutId = null
                    shortcutEditorOpen = true
                },
                enabled = value.customShortcuts.size < MAX_CUSTOM_TERMINAL_SHORTCUTS,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text(stringResource(R.string.shortcut_add_custom))
            }
            Text(
                stringResource(R.string.settings_shortcuts_direction_copy),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
    if (shortcutEditorOpen) {
        val editingShortcut = editingShortcutId?.let { id ->
            value.customShortcuts.firstOrNull { it.id == id }
        }
        TerminalShortcutEditorDialog(
            initialShortcut = editingShortcut,
            onDismiss = ::closeEditor,
            onConfirm = { label, type, shortcutValue ->
                val shortcut = TerminalCustomShortcut(
                    id = editingShortcut?.id ?: java.util.UUID.randomUUID().toString(),
                    label = label,
                    value = shortcutValue,
                    type = type,
                ).normalizedOrNull
                if (shortcut != null) {
                    val nextShortcuts = if (editingShortcut == null) {
                        value.customShortcuts + shortcut
                    } else {
                        value.customShortcuts.map { current ->
                            if (current.id == editingShortcut.id) shortcut else current
                        }
                    }
                    onPreferencesChanged(
                        value.copy(customShortcuts = nextShortcuts),
                    )
                }
                closeEditor()
            },
            onDelete = editingShortcut?.let { shortcut ->
                {
                    onPreferencesChanged(
                        value.copy(customShortcuts = value.customShortcuts.filterNot { it.id == shortcut.id }),
                    )
                    closeEditor()
                }
            },
        )
    }
}

@Composable
private fun TerminalShortcutEditorDialog(
    initialShortcut: TerminalCustomShortcut? = null,
    onDismiss: () -> Unit,
    onConfirm: (String, TerminalCustomShortcutType, String) -> Unit,
    onDelete: (() -> Unit)? = null,
) {
    var label by rememberSaveable(initialShortcut?.id) { mutableStateOf(initialShortcut?.label.orEmpty()) }
    var shortcutTypeName by rememberSaveable(initialShortcut?.id) {
        mutableStateOf(initialShortcut?.type?.name ?: TerminalCustomShortcutType.KEY_COMBINATION.name)
    }
    var keyCombination by rememberSaveable(initialShortcut?.id) {
        mutableStateOf(initialShortcut?.keyCombination.orEmpty())
    }
    var quickInput by rememberSaveable(initialShortcut?.id) {
        mutableStateOf(initialShortcut?.quickInput.orEmpty())
    }
    val shortcutType = TerminalCustomShortcutType.valueOf(shortcutTypeName)
    val parsed = TerminalKeyCombination.parse(keyCombination)
    val valid = when (shortcutType) {
        TerminalCustomShortcutType.KEY_COMBINATION -> parsed != null
        TerminalCustomShortcutType.QUICK_INPUT -> quickInput.isNotBlank()
    }
    val focusRequester = remember { FocusRequester() }
    val keyboardController = LocalSoftwareKeyboardController.current
    LaunchedEffect(shortcutType) {
        focusRequester.requestFocus()
        keyboardController?.show()
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                stringResource(
                    if (initialShortcut == null) R.string.shortcut_add_custom_title
                    else R.string.shortcut_edit_custom_title,
                ),
            )
        },
        text = {
            Column(
                modifier = Modifier
                    .heightIn(max = 520.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(
                    stringResource(R.string.shortcut_type_label),
                    style = MaterialTheme.typography.labelLarge,
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    TerminalCustomShortcutType.entries.forEach { type ->
                        FilterChip(
                            selected = shortcutType == type,
                            onClick = { shortcutTypeName = type.name },
                            label = { Text(stringResource(terminalCustomShortcutTypeLabelResource(type))) },
                            modifier = Modifier.weight(1f),
                        )
                    }
                }
                OutlinedTextField(
                    value = label,
                    onValueChange = { label = it.take(16) },
                    label = { Text(stringResource(R.string.shortcut_button_label)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                when (shortcutType) {
                    TerminalCustomShortcutType.KEY_COMBINATION -> OutlinedTextField(
                        value = keyCombination,
                        onValueChange = { keyCombination = it.take(64) },
                        label = { Text(stringResource(R.string.shortcut_key_combination)) },
                        supportingText = {
                            Text(
                                if (keyCombination.isBlank() || parsed != null) {
                                    stringResource(R.string.shortcut_key_combination_help)
                                } else {
                                    stringResource(R.string.shortcut_key_combination_invalid)
                                },
                            )
                        },
                        isError = keyCombination.isNotBlank() && parsed == null,
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
                        modifier = Modifier
                            .fillMaxWidth()
                            .focusRequester(focusRequester)
                            .onPreviewKeyEvent { event ->
                                if (event.type != KeyEventType.KeyDown) return@onPreviewKeyEvent false
                                val captured = terminalCombinationFromAndroidKeyEvent(event.nativeKeyEvent)
                                    ?: return@onPreviewKeyEvent false
                                keyCombination = captured.canonical
                                true
                            },
                    )
                    TerminalCustomShortcutType.QUICK_INPUT -> OutlinedTextField(
                        value = quickInput,
                        onValueChange = { quickInput = it.take(MAX_TERMINAL_QUICK_INPUT_LENGTH) },
                        label = { Text(stringResource(R.string.shortcut_quick_input_value)) },
                        supportingText = { Text(stringResource(R.string.shortcut_quick_input_help)) },
                        minLines = 3,
                        maxLines = 6,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text),
                        modifier = Modifier
                            .fillMaxWidth()
                            .focusRequester(focusRequester),
                    )
                }
                if (onDelete != null) {
                    HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
                    OutlinedButton(
                        onClick = onDelete,
                        modifier = Modifier.fillMaxWidth(),
                        colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
                    ) {
                        Icon(Icons.Filled.DeleteForever, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.shortcut_delete_custom_action))
                    }
                }
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    onConfirm(
                        label,
                        shortcutType,
                        when (shortcutType) {
                            TerminalCustomShortcutType.KEY_COMBINATION -> parsed?.canonical.orEmpty()
                            TerminalCustomShortcutType.QUICK_INPUT -> quickInput
                        },
                    )
                },
                enabled = valid,
            ) {
                Text(
                    stringResource(
                        if (initialShortcut == null) R.string.shortcut_save_custom
                        else R.string.shortcut_save_changes,
                    ),
                )
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text(stringResource(R.string.action_cancel))
            }
        },
    )
}

internal fun terminalCombinationFromAndroidKeyEvent(event: AndroidKeyEvent): TerminalKeyCombination? {
    if (event.action != AndroidKeyEvent.ACTION_DOWN || AndroidKeyEvent.isModifierKey(event.keyCode)) return null
    val key = when (event.keyCode) {
        AndroidKeyEvent.KEYCODE_ESCAPE -> "Esc"
        AndroidKeyEvent.KEYCODE_TAB -> "Tab"
        AndroidKeyEvent.KEYCODE_ENTER -> "Enter"
        AndroidKeyEvent.KEYCODE_DEL -> "Backspace"
        AndroidKeyEvent.KEYCODE_FORWARD_DEL -> "Delete"
        AndroidKeyEvent.KEYCODE_DPAD_LEFT -> "Left"
        AndroidKeyEvent.KEYCODE_DPAD_UP -> "Up"
        AndroidKeyEvent.KEYCODE_DPAD_DOWN -> "Down"
        AndroidKeyEvent.KEYCODE_DPAD_RIGHT -> "Right"
        AndroidKeyEvent.KEYCODE_MOVE_HOME -> "Home"
        AndroidKeyEvent.KEYCODE_MOVE_END -> "End"
        AndroidKeyEvent.KEYCODE_PAGE_UP -> "PageUp"
        AndroidKeyEvent.KEYCODE_PAGE_DOWN -> "PageDown"
        AndroidKeyEvent.KEYCODE_INSERT -> "Insert"
        AndroidKeyEvent.KEYCODE_SPACE -> "Space"
        in AndroidKeyEvent.KEYCODE_F1..AndroidKeyEvent.KEYCODE_F12 ->
            "F${event.keyCode - AndroidKeyEvent.KEYCODE_F1 + 1}"
        in AndroidKeyEvent.KEYCODE_A..AndroidKeyEvent.KEYCODE_Z ->
            ('a'.code + event.keyCode - AndroidKeyEvent.KEYCODE_A).toChar().toString()
        in AndroidKeyEvent.KEYCODE_0..AndroidKeyEvent.KEYCODE_9 ->
            ('0'.code + event.keyCode - AndroidKeyEvent.KEYCODE_0).toChar().toString()
        else -> event.unicodeChar.takeIf { it > 0 && !Character.isISOControl(it) }
            ?.let { String(Character.toChars(it)) }
    } ?: return null
    return TerminalKeyCombination(
        key = key,
        control = event.isCtrlPressed,
        alt = event.isAltPressed,
        shift = event.isShiftPressed,
    )
}

internal fun terminalActionLabelResource(action: TerminalAction): Int = when (action) {
    TerminalAction.ESCAPE -> R.string.terminal_action_escape
    TerminalAction.TAB -> R.string.terminal_action_tab
    TerminalAction.ENTER -> R.string.terminal_action_enter
    TerminalAction.BACKSPACE -> R.string.terminal_action_backspace
    TerminalAction.DELETE -> R.string.terminal_action_delete
    TerminalAction.ARROW_LEFT -> R.string.terminal_action_arrow_left
    TerminalAction.ARROW_UP -> R.string.terminal_action_arrow_up
    TerminalAction.ARROW_DOWN -> R.string.terminal_action_arrow_down
    TerminalAction.ARROW_RIGHT -> R.string.terminal_action_arrow_right
    TerminalAction.CTRL_C -> R.string.terminal_action_ctrl_c
}

internal fun terminalCustomShortcutTypeLabelResource(type: TerminalCustomShortcutType): Int = when (type) {
    TerminalCustomShortcutType.KEY_COMBINATION -> R.string.shortcut_type_key_combination
    TerminalCustomShortcutType.QUICK_INPUT -> R.string.shortcut_type_quick_input
}

@Composable
private fun ProductThemePreference.displayLabel(): String = when (this) {
    ProductThemePreference.SYSTEM -> stringResource(R.string.theme_system)
    ProductThemePreference.LIGHT -> stringResource(R.string.theme_light)
    ProductThemePreference.DARK -> stringResource(R.string.theme_dark)
}

@Composable
private fun AccountPage(
    snapshot: ProductSnapshot,
    onSignOut: () -> Unit,
) {
    PageHeader(stringResource(R.string.nav_account), stringResource(R.string.page_account_subtitle), Icons.Filled.CheckCircle)
    ConnectionBanner(snapshot = snapshot)
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            SettingRow(Icons.Filled.CheckCircle, stringResource(R.string.account_identity), snapshot.accountLabel ?: stringResource(R.string.account_no_identity))
            SettingRow(Icons.Filled.Wifi, stringResource(R.string.account_endpoint), PRODUCT_PUBLIC_SERVER_URL)
            SettingRow(Icons.Filled.Info, stringResource(R.string.account_last_sync), snapshot.lastSyncedAt?.let(::formatInstant) ?: stringResource(R.string.account_not_synced))
            Text(
                stringResource(R.string.account_access_copy),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (snapshot.signedIn) {
                OutlinedButton(onClick = onSignOut, modifier = Modifier.heightIn(min = 48.dp)) {
                    Icon(Icons.AutoMirrored.Filled.Logout, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.action_sign_out))
                }
            }
        }
    }
}

@Composable
private fun SettingRow(icon: ImageVector, title: String, detail: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.Top) {
        Icon(icon, contentDescription = title, tint = MaterialTheme.colorScheme.primary)
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun ConnectionBanner(snapshot: ProductSnapshot) {
    val configured = snapshot.configured
    val connected = snapshot.isConnected()
    val color = when {
        connected -> MaterialTheme.colorScheme.secondary
        snapshot.connectionState == ProductConnectionState.ERROR -> MaterialTheme.colorScheme.tertiary
        else -> MaterialTheme.colorScheme.tertiary
    }
    val icon = when {
        connected -> Icons.Filled.Wifi
        snapshot.signedIn -> Icons.Filled.Warning
        else -> Icons.Filled.WifiOff
    }
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = color.copy(alpha = 0.10f),
        border = BorderStroke(1.dp, color.copy(alpha = 0.45f)),
    ) {
        Row(
            modifier = Modifier.padding(12.dp),
            verticalAlignment = Alignment.Top,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Icon(icon, contentDescription = null, tint = color)
            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(
                    when {
                        connected -> stringResource(R.string.status_signed_in_last_sync)
                        snapshot.connectionState == ProductConnectionState.ERROR -> stringResource(R.string.connection_error_title)
                        snapshot.signedIn -> stringResource(R.string.status_signed_in_awaiting_sync)
                        configured -> stringResource(R.string.status_endpoint_configured)
                        else -> stringResource(R.string.status_signed_out)
                    },
                    style = MaterialTheme.typography.titleSmall,
                    color = color,
                )
                Text(
                    when {
                        connected -> stringResource(R.string.status_last_sync_copy)
                        snapshot.connectionState == ProductConnectionState.ERROR -> stringResource(R.string.connection_error_copy)
                        snapshot.signedIn -> stringResource(R.string.status_no_server_sync)
                        configured -> stringResource(R.string.status_endpoint_copy)
                        else -> stringResource(R.string.status_signed_out_copy)
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurface,
                )
            }
        }
    }
}

@Composable
private fun PageHeader(title: String, subtitle: String, icon: ImageVector) {
    Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Icon(icon, contentDescription = title, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(28.dp))
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
            Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun EmptyState(
    icon: ImageVector,
    title: String,
    detail: String,
    actionLabel: String?,
    onAction: () -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = Color.Transparent,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 18.dp, vertical = 26.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(32.dp))
            Text(title, style = MaterialTheme.typography.titleMedium)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (actionLabel != null) {
                OutlinedButton(onClick = onAction, modifier = Modifier.heightIn(min = 48.dp)) { Text(actionLabel) }
            }
        }
    }
}

@Composable
private fun PasteConfirmationDialog(onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var text by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Filled.ContentPaste, contentDescription = null) },
        title = { Text(stringResource(R.string.paste_dialog_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.paste_dialog_copy))
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it },
                    modifier = Modifier.fillMaxWidth(),
                    minLines = 3,
                    maxLines = 8,
                    label = { Text(stringResource(R.string.paste_text_label)) },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text),
                )
            }
        },
        confirmButton = {
            Button(onClick = { onConfirm(text) }, enabled = text.isNotEmpty(), modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.action_send)) }
        },
        dismissButton = { TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.action_cancel)) } },
    )
}

@Composable
private fun ConfirmActionDialog(
    title: String,
    message: String,
    confirmLabel: String,
    icon: ImageVector,
    onDismiss: () -> Unit,
    onConfirm: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.error) },
        title = { Text(title) },
        text = { Text(message) },
        confirmButton = {
            Button(
                onClick = onConfirm,
                colors = ButtonDefaults.buttonColors(
                    containerColor = MaterialTheme.colorScheme.error,
                    contentColor = MaterialTheme.colorScheme.onError,
                ),
                modifier = Modifier.heightIn(min = 48.dp),
            ) { Text(confirmLabel) }
        },
        dismissButton = { TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.action_cancel)) } },
    )
}

private fun formatInstant(value: Instant): String =
    DateTimeFormatter.ofLocalizedDateTime(FormatStyle.SHORT)
        .withLocale(Locale.getDefault())
        .withZone(ZoneId.systemDefault())
        .format(value)

private val sensitiveIdPattern = Regex("(?i)\\b[0-9a-f]{8}-[0-9a-f-]{27,}\\b")

/** Activity is a normal product surface, so never echo raw transport identifiers. */
private fun safeActivityDetail(value: String): String =
    sensitiveIdPattern.replace(value, "session")

/** Keep transport/auth failures out of transient UI; retain only local validation copy. */
private fun safeOperationMessage(
    error: Throwable,
    fallback: String,
    activeSessionDeleteMessage: String = fallback,
): String = when {
    error is TermLinkApiException && error.code == "SESSION_ACTIVE" -> activeSessionDeleteMessage
    error is IllegalArgumentException ->
        error.message?.let(::safeActivityDetail)?.takeIf { it.isNotBlank() } ?: fallback
    else -> fallback
}

private fun productTerminalTheme(preferences: TerminalDisplayPreferences): TerminalThemeV1 {
    val normalized = preferences.normalized
    return TerminalThemeV1(
        name = "TermLink Product",
        foreground = ArgbColor(normalized.foregroundArgb),
        background = ArgbColor(normalized.backgroundArgb),
        cursor = ArgbColor(normalized.foregroundArgb),
        selectionBackground = ArgbColor(0xFF3E4B70.toInt()),
        selectionForeground = null,
        ansiColors = listOf(
            normalized.backgroundArgb, 0xFFFF8C91, 0xFF65D6A1, 0xFFF1C96B,
            0xFF9CB2FF, 0xFFC99DEB, 0xFF6EDDE0, normalized.foregroundArgb,
            0xFF6F7A8B, 0xFFFFA4A8, 0xFF82E3B4, 0xFFFFD984,
            0xFFB9C7FF, 0xFFDDB8F2, 0xFF8DE7E9, 0xFFFFFFFF,
        ).map { ArgbColor(it.toInt()) },
        boldAsBright = true,
        fontFamilyHint = "Cascadia Mono",
    )
}

/** A small compatibility wrapper keeps the app free of a lifecycle-compose dependency. */
@Composable
private fun <T> kotlinx.coroutines.flow.StateFlow<T>.collectAsStateCompat(): androidx.compose.runtime.State<T> =
    collectAsState()
