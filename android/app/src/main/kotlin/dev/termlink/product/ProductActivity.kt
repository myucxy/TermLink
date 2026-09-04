package dev.termlink.product

import android.content.Intent
import android.content.res.Configuration
import android.graphics.Color as AndroidColor
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.util.Log
import android.view.Choreographer
import android.view.View
import android.view.Window
import android.view.WindowManager
import android.view.KeyEvent as AndroidKeyEvent
import android.view.inputmethod.InputMethodManager
import android.widget.Toast
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.ViewModelProvider
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.basicMarquee
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
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
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
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
import androidx.compose.material.icons.filled.LightMode
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Replay
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
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.LinearProgressIndicator
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
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.VerticalDivider
import androidx.compose.material3.lightColorScheme
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
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
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.harness.R
import dev.termlink.harness.BuildConfig
import dev.termlink.model.SessionSummary
import dev.termlink.model.TerminalCommandCapability
import dev.termlink.model.isPcAgent
import dev.termlink.session.RemoteSessionState
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.terminal.api.ArgbColor
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalFontFamilyHintV1
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.api.TerminalTypefaceResolver
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.api.TerminalViewportEngine
import dev.termlink.terminal.termux.TermuxTerminalEngine
import dev.termlink.terminal.termux.TermuxTranscriptViewport
import java.time.Instant
import java.time.LocalTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale
import org.json.JSONArray
import org.json.JSONObject
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import androidx.core.view.WindowCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
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

private sealed interface AgentUpdateDialogState {
    val pc: PcSummary

    data class Loading(override val pc: PcSummary) : AgentUpdateDialogState
    data class Offer(
        override val pc: PcSummary,
        val eligibility: AgentUpdateEligibility,
    ) : AgentUpdateDialogState
    data class Starting(
        override val pc: PcSummary,
        val eligibility: AgentUpdateEligibility,
    ) : AgentUpdateDialogState
    data class Running(
        override val pc: PcSummary,
        val progress: AgentUpdateProgress,
    ) : AgentUpdateDialogState
    data class Failure(
        override val pc: PcSummary,
        val error: Throwable,
    ) : AgentUpdateDialogState
}

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
private val TerminalDockButtonSize = 30.dp
private val TerminalDockHorizontalContentPadding = 8.dp

internal enum class HomeGreetingPeriod { MORNING, AFTERNOON, EVENING }

internal fun homeGreetingPeriod(hourOfDay: Int): HomeGreetingPeriod {
    require(hourOfDay in 0..23) { "hourOfDay must be between 0 and 23" }
    return when (hourOfDay) {
        in 5..11 -> HomeGreetingPeriod.MORNING
        in 12..17 -> HomeGreetingPeriod.AFTERNOON
        else -> HomeGreetingPeriod.EVENING
    }
}

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
private const val PRODUCT_KEEP_SCREEN_ON_KEY = "keep_screen_on"
private const val PRODUCT_PRIMARY_DESTINATION_KEY = "primary_destination"
private const val PRODUCT_SELECTED_SESSION_ID_KEY = "selected_session_id"
private const val PRODUCT_OPENED_SESSION_IDS_KEY = "opened_session_ids_v1"
private const val PRODUCT_EXPANDED_PC_IDS_KEY = "expanded_pc_ids_v1"
private const val TERMINAL_FONT_SIZE_KEY = "terminal_font_size_sp"
private const val TERMINAL_FONT_SCHEME_KEY = "terminal_font_scheme_v1"
private const val TERMINAL_CUSTOM_FONT_NAME_KEY = "terminal_custom_font_name_v1"
private const val TERMINAL_CUSTOM_FONT_SHA256_KEY = "terminal_custom_font_sha256_v1"
private const val TERMINAL_LINE_SPACING_KEY = "terminal_line_spacing"
private const val TERMINAL_FOREGROUND_KEY = "terminal_foreground_argb"
private const val TERMINAL_BACKGROUND_KEY = "terminal_background_argb"
private const val TERMINAL_SHORTCUT_PREFIX = "terminal_shortcut_"
private const val TERMINAL_SHORTCUT_ORDER_KEY = "terminal_shortcut_order"
private const val TERMINAL_CUSTOM_SHORTCUTS_KEY = "terminal_custom_shortcuts_v1"
private const val TERMINAL_UNIFIED_SHORTCUT_ORDER_KEY = "terminal_shortcut_order_v2"
private const val TERMINAL_CUSTOM_SHORTCUT_ROWS_KEY = "terminal_custom_shortcut_rows_v1"
private const val TERMINAL_SHORTCUT_DOCK_LAYOUT_KEY = "terminal_shortcut_dock_layout_v1"
private const val TERMINAL_CUSTOM_SHORTCUT_ONE_ROW_ORDER_KEY = "terminal_custom_shortcut_one_row_order_v1"
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

/** A completed empty selector must reveal the live renderer instead of leaving a black overlay. */
internal fun shouldDismissEmptyTranscriptOverlay(state: TranscriptViewportState?): Boolean =
    state != null && !state.loading && state.error == null && state.pages.isEmpty() && !state.hasMoreBefore

internal data class TerminalInputResumeEffect(
    val resumeViewportAfterLocalInput: Boolean,
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
    resumeViewportAfterLocalInput = accepted,
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
    val firstVisibleLineId: Long? = null,
    val visibleTextSha256: String,
    val transcriptTopRow: Int = 0,
    val transcriptPageCount: Int = 0,
    val transcriptRowCount: Int = 0,
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
private const val TAIL_FIRST_IME_RETRY_FRAME_INTERVAL = 30
private const val TAIL_FIRST_IME_STABILITY_TIMEOUT_MILLIS = 30_000L
private const val TAIL_FIRST_E2E_LOG_TAG = "TermLinkTailFirst"

private interface TailFirstTerminalSurfaceController {
    fun observe(): TailFirstSurfaceObservation
    fun scrollHistoryToLine(lineId: Long): Boolean
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

internal fun loadKeepScreenOnPreference(context: android.content.Context): Boolean =
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .getBoolean(PRODUCT_KEEP_SCREEN_ON_KEY, false)

internal fun saveKeepScreenOnPreference(context: android.content.Context, enabled: Boolean) {
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putBoolean(PRODUCT_KEEP_SCREEN_ON_KEY, enabled)
        .apply()
}

internal fun applyKeepScreenOn(window: Window, enabled: Boolean) {
    window.decorView.keepScreenOn = enabled
    if (enabled) {
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    } else {
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }
}

internal fun shouldHideProductStatusBar(orientation: Int): Boolean =
    orientation == Configuration.ORIENTATION_LANDSCAPE

/**
 * Termux commit 3df69d1 uses FLAG_FULLSCREEN plus an app-owned IME workaround.
 * TermLink keeps its Compose edge-to-edge/remote-terminal inset ownership and
 * adopts only the landscape behavior through the modern compat controller.
 */
internal fun applyProductStatusBarPolicy(window: Window, orientation: Int) {
    val controller = WindowCompat.getInsetsController(window, window.decorView)
    if (shouldHideProductStatusBar(orientation)) {
        controller.systemBarsBehavior =
            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        controller.hide(WindowInsetsCompat.Type.statusBars())
    } else {
        controller.show(WindowInsetsCompat.Type.statusBars())
    }
}

class ProductActivity : ComponentActivity() {
    private lateinit var repositoryOwner: ProductRepositoryViewModel
    private lateinit var repository: ProductRepository
    private var repositoryAttachment: java.io.Closeable? = null
    private var productForeground by mutableStateOf(false)
    private var productForegroundGeneration by mutableStateOf(0L)
    private var pendingUpdateApk: File? = null
    private var pendingRemoteDownloadSelection: RemoteFileDownloadSelection? = null
    private val authorizationLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        repositoryOwner.handleAuthorizationResult(result.data)
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
        repositoryOwner = ViewModelProvider(
            this,
            ProductRepositoryViewModel.Factory(applicationContext),
        )[ProductRepositoryViewModel::class.java]
        repositoryAttachment = repositoryOwner.attach(this, authorizationLauncher::launch)
        repository = repositoryOwner.repository
        pendingRemoteDownloadSelection = savedInstanceState?.remoteDownloadSelection()
        enableEdgeToEdge()
        applyProductStatusBarPolicy(window, resources.configuration.orientation)
        val initialKeepScreenOn = loadKeepScreenOnPreference(this)
        applyKeepScreenOn(window, initialKeepScreenOn)
        setContent {
            val context = LocalContext.current
            var themePreference by rememberSaveable { mutableStateOf(loadThemePreference(context)) }
            var keepScreenOn by remember { mutableStateOf(initialKeepScreenOn) }
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
                applyKeepScreenOn(window, keepScreenOn)
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
                    keepScreenOn = keepScreenOn,
                    onKeepScreenOnChanged = {
                        keepScreenOn = it
                        applyKeepScreenOn(window, it)
                        saveKeepScreenOnPreference(context, it)
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

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            applyProductStatusBarPolicy(window, resources.configuration.orientation)
        }
    }

    override fun onStop() {
        productForeground = false
        repository.onAppBackgrounded(android.os.SystemClock.elapsedRealtime())
        // Keep this device's short-lived control lease while the Activity is
        // merely backgrounded. Releasing it here raced a fast process/activity
        // restart and could delete the freshly resumed lease. The realtime
        // client stops renewing when it is actually closed, so abandoned leases
        // still expire naturally on the server.
        super.onStop()
    }

    override fun onStart() {
        super.onStart()
        // A Boolean can be conflated by Compose when stop/start happen before
        // the next frame. The monotonic generation preserves every foreground
        // entry and therefore gives the selected session one fresh foreground
        // reconciliation opportunity.
        // The repository and its lifecycle fence survive configuration changes
        // in ProductRepositoryViewModel, so their generation source must have
        // the same lifetime. An Activity-local counter would restart at one and
        // make the first foreground edge after recreation look stale.
        productForegroundGeneration = repositoryOwner.nextForegroundGeneration()
        productForeground = true
        if (::repository.isInitialized) {
            repository.onAppForegrounded(
                generation = productForegroundGeneration,
                elapsedRealtimeMillis = android.os.SystemClock.elapsedRealtime(),
            )
            lifecycleScope.launch {
                // Match NuanYa's startup/foreground check: the repository
                // throttles this to one request per minute and reuses an
                // ETag-bound response when the cache is still fresh.
                runCatching { repository.checkForUpdate(force = false) }
            }
        }
    }

    override fun onDestroy() {
        // The ViewModel owns the device-level runtime. Configuration changes
        // detach only this Activity; a real finish clears the ViewModelStore and
        // closes the repository, shared realtime socket, engines, and database.
        repositoryAttachment?.close()
        repositoryAttachment = null
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
    keepScreenOn: Boolean = false,
    onKeepScreenOnChanged: (Boolean) -> Unit = {},
    terminalPreferences: TerminalDisplayPreferences = TerminalDisplayPreferences(),
    onTerminalPreferencesChanged: (TerminalDisplayPreferences) -> Unit = {},
    shortcutPreferences: TerminalShortcutPreferences = TerminalShortcutPreferences(),
    onShortcutPreferencesChanged: (TerminalShortcutPreferences) -> Unit = {},
    onInstallUpdate: (String) -> Unit = {},
    onCreateRemoteDocument: (RemoteFileDownloadSelection) -> Unit = {},
) {
    val context = LocalContext.current
    val snapshot by repository.snapshot.collectAsStateCompat()
    val remoteDirectoryFavoriteStore = remember(context) {
        SharedPreferencesRemoteDirectoryFavoriteStore(context)
    }
    val remoteDirectoryFavoriteScope = remoteDirectoryFavoriteAccountScope(
        snapshot.serverUrl,
        snapshot.accountLabel,
    )
    var remoteDirectoryFavorites by remember(remoteDirectoryFavoriteScope) {
        mutableStateOf(
            remoteDirectoryFavoriteScope
                ?.let(remoteDirectoryFavoriteStore::load)
                .orEmpty(),
        )
    }
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
    var openedSessionIds by remember { mutableStateOf(restoredNavigation.openedSessionIds) }
    var expandedPcIds by remember { mutableStateOf(restoredNavigation.expandedPcIds) }
    var deviceBackArmedAt by remember { mutableStateOf<Long?>(null) }
    var selectedHistoryId by rememberSaveable { mutableStateOf<String?>(null) }
    var selectedRemotePcId by rememberSaveable { mutableStateOf<String?>(null) }
    var selectedRemotePcName by rememberSaveable { mutableStateOf("") }
    var pasteSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    val terminalInputResumeRegistry = remember { TerminalInputResumeRegistry() }
    var pendingStopSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    var pendingDeleteSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    var refreshInFlight by remember { mutableStateOf(false) }
    var signOutConfirmation by remember { mutableStateOf(false) }
    var agentUpdateDialog by remember { mutableStateOf<AgentUpdateDialogState?>(null) }
    var agentUpdateJob by remember { mutableStateOf<Job?>(null) }
    var checkedAgentUpdatePcIds by remember(remoteDirectoryFavoriteScope) {
        mutableStateOf(emptySet<java.util.UUID>())
    }
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

    fun monitorAgentUpdate(pc: PcSummary, initial: AgentUpdateProgress) {
        agentUpdateJob?.cancel()
        agentUpdateDialog = AgentUpdateDialogState.Running(pc, initial)
        if (initial.terminal) {
            agentUpdateJob = null
            return
        }
        agentUpdateJob = scope.launch {
            try {
                val result = repository.monitorAgentUpdate(pc.id, initial.id) { progress ->
                    agentUpdateDialog = AgentUpdateDialogState.Running(pc, progress)
                }
                agentUpdateDialog = AgentUpdateDialogState.Running(pc, result)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Throwable) {
                agentUpdateDialog = AgentUpdateDialogState.Failure(pc, error)
            } finally {
                agentUpdateJob = null
            }
        }
    }

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

    LaunchedEffect(
        snapshot.connectionState,
        snapshot.sessions.map { Triple(it.id, it.state, it.online) },
        openedSessionIds,
    ) {
        if (snapshot.connectionState != ProductConnectionState.CONNECTED) return@LaunchedEffect
        val available = snapshot.sessions.asSequence()
            .filter(::isSessionTabOpenable)
            .mapTo(hashSetOf()) { it.id.toString() }
        val retained = openedSessionIds.filter(available::contains)
        if (retained != openedSessionIds) {
            openedSessionIds = retained
            selectedSessionId = selectedSessionId?.takeIf(retained::contains) ?: retained.lastOrNull()
            saveProductSessionTabs(context, retained, selectedSessionId)
        }
    }

    LaunchedEffect(
        snapshot.connectionState,
        snapshot.pcs.map { Triple(it.id, it.online, PcCapabilityIds.REMOTE_AGENT_UPDATE_V1 in it.capabilities) },
        agentUpdateDialog,
    ) {
        if (snapshot.connectionState != ProductConnectionState.CONNECTED || agentUpdateDialog != null) {
            return@LaunchedEffect
        }
        repeat(3) { attempt ->
            var transientFailure = false
            for (pc in snapshot.pcs.filter {
                it.platform == "windows" && PcCapabilityIds.REMOTE_AGENT_UPDATE_V1 in it.capabilities
            }) {
                if (pc.id in checkedAgentUpdatePcIds) continue
                val active = try {
                    repository.activeAgentUpdate(pc.id).also {
                        checkedAgentUpdatePcIds = checkedAgentUpdatePcIds + pc.id
                    }
                } catch (error: CancellationException) {
                    throw error
                } catch (_: Throwable) {
                    transientFailure = true
                    null
                }
                if (active != null) {
                    monitorAgentUpdate(pc, active)
                    return@LaunchedEffect
                }
            }
            if (!transientFailure || attempt == 2) return@LaunchedEffect
            delay(2_000)
            if (agentUpdateDialog != null) return@LaunchedEffect
            if (snapshot.connectionState != ProductConnectionState.CONNECTED) {
                return@LaunchedEffect
            }
        }
    }

    fun navigateTo(next: ProductDestination) {
        if (next.isVisibleInProductShell) {
            if (next != ProductDestination.DEVICES) deviceBackArmedAt = null
            destinationName = next.name
            if (next == next.primaryDestination().destination()) {
                saveProductPrimaryDestination(context, next.primaryDestination())
            }
        }
    }

    fun navigateToPrimary(next: ProductPrimaryDestination) {
        navigateTo(next.destination())
    }

    fun selectSessionTab(sessionId: java.util.UUID, addIfMissing: Boolean): Boolean {
        if (!isSessionTabOpenable(snapshot.sessions.firstOrNull { it.id == sessionId })) return false
        val value = sessionId.toString()
        val next = if (addIfMissing && value !in openedSessionIds) openedSessionIds + value else openedSessionIds
        if (value !in next) return false
        openedSessionIds = next
        selectedSessionId = value
        saveProductSessionTabs(context, next, value)
        return true
    }

    fun removeSessionTabState(sessionId: java.util.UUID) {
        val value = sessionId.toString()
        if (value !in openedSessionIds) return
        val removedIndex = openedSessionIds.indexOf(value)
        val next = openedSessionIds - value
        val nextSelected = when {
            selectedSessionId != value -> selectedSessionId?.takeIf(next::contains)
            next.isEmpty() -> null
            else -> next[removedIndex.coerceAtMost(next.lastIndex)]
        }
        openedSessionIds = next
        selectedSessionId = nextSelected
        saveProductSessionTabs(context, next, nextSelected)
    }

    fun closeLocalSessionTab(sessionId: java.util.UUID) {
        removeSessionTabState(sessionId)
        scope.launch {
            try {
                repository.closeSessionView(sessionId)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Throwable) {
                snackbarHostState.showSnackbar(
                    safeOperationMessage(error, unavailableOperationMessage, activeSessionDeleteMessage),
                )
            }
        }
    }

    val deviceBackExitMessage = stringResource(R.string.devices_back_again_to_exit)
    BackHandler(
        enabled = destination == ProductDestination.SESSIONS ||
            destination == ProductDestination.DEVICES ||
            destination.primaryDestination().destination() != destination,
    ) {
        when {
            destination == ProductDestination.SESSIONS -> {
                deviceBackArmedAt = null
                navigateTo(ProductDestination.DEVICES)
            }
            destination.primaryDestination().destination() != destination -> {
                navigateTo(destination.primaryDestination().destination())
            }
            destination == ProductDestination.DEVICES -> {
                val now = android.os.SystemClock.elapsedRealtime()
                if (shouldExitAfterDeviceBack(now, deviceBackArmedAt)) {
                    deviceBackArmedAt = null
                    (context as? android.app.Activity)?.finish()
                } else {
                    deviceBackArmedAt = now
                    Toast.makeText(context, deviceBackExitMessage, Toast.LENGTH_SHORT).show()
                }
            }
        }
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
        if (deviceRefreshInProgress(refreshInFlight, snapshot.connectionState)) return
        refreshInFlight = true
        runOperation(repository::refresh) { refreshInFlight = false }
    }

    fun refreshCurrentDestination() {
        if (destination != ProductDestination.FILES) {
            refresh()
            return
        }
        val pcId = selectedRemotePcId
            ?.let { value -> runCatching { java.util.UUID.fromString(value) }.getOrNull() }
            ?: return
        val pcName = snapshot.pcs.firstOrNull { pc -> pc.id == pcId }?.name
            ?: selectedRemotePcName
        val currentState = snapshot.remoteFileBrowser.takeIf { state -> state.pcId == pcId } ?: return
        if (currentState.loading) return
        val currentPath = currentState.currentPath
        runOperation {
            repository.browseRemoteDirectory(pcId, pcName, currentPath, null)
        }
    }

    suspend fun runSessionOperation(operation: suspend () -> Unit) {
        try {
            operation()
        } catch (error: CancellationException) {
            throw error
        } catch (error: Throwable) {
            snackbarHostState.showSnackbar(
                (error as? SessionOpenException)?.failure?.localizedMessage(context)
                    ?: safeOperationMessage(error, unavailableOperationMessage, activeSessionDeleteMessage),
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
                        selectSessionTab(sessionId, addIfMissing = true)
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
                onRegister = { onComplete ->
                    runOperation(repository::beginEmbeddedRegistration, onComplete)
                },
                modifier = Modifier.padding(innerPadding),
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
                        onRefresh = ::refreshCurrentDestination,
                        refreshEnabled = if (destination != ProductDestination.FILES) {
                            true
                        } else {
                            val selectedPcId = selectedRemotePcId?.let { value ->
                                runCatching { java.util.UUID.fromString(value) }.getOrNull()
                            }
                            selectedPcId != null &&
                                snapshot.remoteFileBrowser.pcId == selectedPcId &&
                                !snapshot.remoteFileBrowser.loading
                        },
                        refreshContentDescription = stringResource(
                            if (destination == ProductDestination.FILES) {
                                R.string.remote_files_refresh_current
                            } else {
                                R.string.action_refresh
                            },
                        ),
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
                    val refreshing = deviceRefreshInProgress(refreshInFlight, snapshot.connectionState)
                    val refreshDescription = stringResource(R.string.action_refresh)
                    IconButton(
                        onClick = ::refresh,
                        enabled = !refreshing,
                        modifier = Modifier
                            .size(48.dp)
                            .testTag(DEVICE_REFRESH_ACTION_TEST_TAG)
                            .semantics { contentDescription = refreshDescription },
                    ) {
                        if (refreshing) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(24.dp),
                                color = MaterialTheme.colorScheme.primary,
                                strokeWidth = 2.dp,
                            )
                        } else {
                            Icon(
                                Icons.Filled.Refresh,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.primary,
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
                    openedSessionIds = openedSessionIds,
                    onSessionSelected = {
                        // Device-page rows and existing session tabs share this
                        // callback. Adding is idempotent for an existing tab and
                        // is required when Devices opens a session for the first
                        // time.
                        selectSessionTab(it, addIfMissing = true)
                    },
                    onCloseSessionTab = ::closeLocalSessionTab,
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
                    selectedRemotePcId = selectedRemotePcId,
                    selectedRemotePcName = selectedRemotePcName,
                    remoteDirectoryFavorites = remoteDirectoryFavorites,
                    expandedPcIds = expandedPcIds,
                    onTogglePcExpanded = { pcId ->
                        val value = pcId.toString()
                        expandedPcIds = if (value in expandedPcIds) expandedPcIds - value else expandedPcIds + value
                        saveExpandedProductPcs(context, expandedPcIds)
                    },
                    onBrowsePc = { pc ->
                        selectedRemotePcId = pc.id.toString()
                        selectedRemotePcName = pc.name
                        navigateTo(ProductDestination.FILES)
                        runOperation {
                            repository.browseRemoteDirectory(pc.id, pc.name, "", null)
                        }
                    },
                    onAgentUpdateRequested = { pc ->
                        agentUpdateDialog = AgentUpdateDialogState.Loading(pc)
                        scope.launch {
                            try {
                                val active = repository.activeAgentUpdate(pc.id)
                                if (active != null) {
                                    monitorAgentUpdate(pc, active)
                                } else {
                                    agentUpdateDialog = AgentUpdateDialogState.Offer(
                                        pc,
                                        repository.agentUpdateEligibility(pc.id),
                                    )
                                }
                            } catch (error: CancellationException) {
                                throw error
                            } catch (error: Throwable) {
                                agentUpdateDialog = AgentUpdateDialogState.Failure(pc, error)
                            }
                        }
                    },
                    onOpenDownloads = {
                        selectedRemotePcId = null
                        selectedRemotePcName = ""
                        navigateTo(ProductDestination.DOWNLOADS)
                    },
                    onOpenRemoteFavorite = { favorite ->
                        val currentPcName = snapshot.pcs.firstOrNull { pc -> pc.id == favorite.pcId }
                            ?.name
                            ?: favorite.pcName
                        selectedRemotePcId = favorite.pcId.toString()
                        selectedRemotePcName = currentPcName
                        navigateTo(ProductDestination.FILES)
                        runOperation {
                            repository.browseRemoteDirectory(
                                favorite.pcId,
                                currentPcName,
                                favorite.path,
                                null,
                            )
                        }
                    },
                    onToggleRemoteFavorite = { pcId, pcName, entry ->
                        val accountScope = remoteDirectoryFavoriteScope
                        if (accountScope != null && entry.kind == RemoteFileKind.DIRECTORY) {
                            val next = toggleRemoteDirectoryFavorite(
                                remoteDirectoryFavorites,
                                RemoteDirectoryFavorite(
                                    pcId = pcId,
                                    pcName = pcName,
                                    directoryName = entry.name,
                                    path = entry.path,
                                ),
                            )
                            remoteDirectoryFavoriteStore.save(accountScope, next)
                            remoteDirectoryFavorites = next
                        }
                    },
                    onRemoveRemoteFavorite = { favorite ->
                        val accountScope = remoteDirectoryFavoriteScope
                        if (accountScope != null) {
                            val next = removeRemoteDirectoryFavorite(remoteDirectoryFavorites, favorite)
                            remoteDirectoryFavoriteStore.save(accountScope, next)
                            remoteDirectoryFavorites = next
                        }
                    },
                    onBrowseRemoteDirectory = { pcId, pcName, path, offset ->
                        runOperation { repository.browseRemoteDirectory(pcId, pcName, path, offset) }
                    },
                    onDownloadRemoteEntry = { pcId, pcName, entry ->
                        onCreateRemoteDocument(remoteDownloadSelection(pcId, pcName, entry))
                    },
                    onOpenRemoteShell = { pcId, path, command ->
                        scope.launch {
                            try {
                                val sessionId = repository.openRemoteShell(pcId, path, command)
                                if (selectSessionTab(sessionId, addIfMissing = true)) {
                                    navigateTo(ProductDestination.SESSIONS)
                                }
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
                    devicesRefreshing = deviceRefreshInProgress(refreshInFlight, snapshot.connectionState),
                    onRefreshDevices = ::refresh,
                    themePreference = themePreference,
                    onThemePreferenceChanged = onThemePreferenceChanged,
                    keepScreenOn = keepScreenOn,
                    onKeepScreenOnChanged = onKeepScreenOnChanged,
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
    agentUpdateDialog?.let { state ->
        AgentUpdateDialog(
            state = state,
            onDismiss = { agentUpdateDialog = null },
            onInstall = { offer ->
                val targetBuild = offer.eligibility.targetBuild ?: return@AgentUpdateDialog
                agentUpdateDialog = AgentUpdateDialogState.Starting(offer.pc, offer.eligibility)
                agentUpdateJob = scope.launch {
                    try {
                        val result = repository.runAgentUpdate(offer.pc.id, targetBuild) { progress ->
                            agentUpdateDialog = AgentUpdateDialogState.Running(offer.pc, progress)
                        }
                        agentUpdateDialog = AgentUpdateDialogState.Running(offer.pc, result)
                    } catch (error: CancellationException) {
                        // An explicit pre-install cancellation is completed by the separate cancel request.
                    } catch (error: Throwable) {
                        agentUpdateDialog = AgentUpdateDialogState.Failure(offer.pc, error)
                    } finally {
                        agentUpdateJob = null
                    }
                }
            },
            onCancel = { running ->
                if (running.progress.cancelable) {
                    agentUpdateJob?.cancel()
                    agentUpdateJob = null
                    scope.launch {
                        try {
                            val cancelled = repository.cancelAgentUpdate(running.pc.id, running.progress.id)
                            agentUpdateDialog = AgentUpdateDialogState.Running(running.pc, cancelled)
                        } catch (error: CancellationException) {
                            throw error
                        } catch (error: Throwable) {
                            agentUpdateDialog = AgentUpdateDialogState.Failure(running.pc, error)
                        }
                    }
                }
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
                scope.launch {
                    try {
                        repository.deleteSession(sessionId)
                        removeSessionTabState(sessionId)
                    } catch (error: CancellationException) {
                        throw error
                    } catch (error: Throwable) {
                        snackbarHostState.showSnackbar(
                            safeOperationMessage(error, unavailableOperationMessage, activeSessionDeleteMessage),
                        )
                    }
                }
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
                        removeSessionTabState(sessionId)
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
    onRegister: (() -> Unit) -> Unit,
    modifier: Modifier = Modifier,
) {
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var submitting by remember { mutableStateOf(false) }
    var registrationLaunching by remember { mutableStateOf(false) }
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
                onClick = {
                    if (submitting || registrationLaunching) return@OutlinedButton
                    registrationLaunching = true
                    onRegister { registrationLaunching = false }
                },
                enabled = !submitting && !registrationLaunching,
                modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
            ) {
                Text(
                    stringResource(
                        if (registrationLaunching) R.string.registration_opening else R.string.sign_in_register,
                    ),
                )
            }

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
                        stringResource(R.string.update_download_failed),
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
                val minimumTail = arguments["minimum_tail"]?.toULongOrNull() ?: 0uL
                val session = awaitNewTailFirstSession(repository, minimumTail)
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
                val existingPageRanges = before?.pages
                    ?.mapTo(mutableSetOf()) { it.firstLineId to it.lastLineId }
                    .orEmpty()
                val historyLoadTriggered = withContext(Dispatchers.Main.immediate) { surface.openHistory() }
                if (historyLoadTriggered) awaitTranscriptChange(repository, sessionId, before)
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
                        // A bounded renderer window evicts newer pages after enough
                        // prepends. Report the authenticated page accepted by this
                        // operation, not the net window delta after eviction.
                        "pages_moved" to if (loadedPage == null) 0 else 1,
                        "rows_loaded" to (loadedPage?.rows?.size ?: 0),
                    ),
                    loadedTranscriptPage = loadedPage?.let { page ->
                        TailFirstLoadedTranscriptPage(
                            requestBeforeLineId = Math.addExact(page.lastLineId, 1L),
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
                awaitTranscriptChange(repository, sessionId, before)
                val after = repository.snapshot.value.transcriptViewports[sessionId]
                if (after?.error != null) {
                    return tailFirstFailure(requestId, operation, "TRANSCRIPT_LOAD_FAILED")
                }
                val loadedPage = after?.pages?.singleOrNull {
                    (it.firstLineId to it.lastLineId) !in existingPageRanges
                }
                    ?: return tailFirstFailure(requestId, operation, "TRANSCRIPT_PAGE_NOT_RENDERED")
                // A bounded prepend preserves the user's stable visible line. Once the
                // 1,000-row window starts evicting newer pages, repository state can
                // advance without changing that visible line. Wait until Compose has
                // installed this exact authenticated page, then select its first line
                // through the read-only history viewport. This makes every acceptance
                // page visibly provable without invoking another pagination callback.
                awaitTailFirstSurface(sessionId) {
                    it.observe().historyOverlay && it.scrollHistoryToLine(loadedPage.firstLineId)
                }
                awaitTailFirstSurface(sessionId) {
                    val observation = it.observe()
                    observation.historyOverlay &&
                        observation.firstVisibleLineId == loadedPage.firstLineId &&
                        observation.viewportSignature != beforeViewport
                }
                tailFirstStateResponse(
                    repository,
                    sessionId,
                    requestId,
                    operation,
                    extras = mapOf(
                        // Keep acceptance evidence stable once the bounded
                        // viewport begins evicting its newer rendered pages.
                        "pages_moved" to 1,
                        "rows_loaded" to loadedPage.rows.size,
                    ),
                    loadedTranscriptPage = loadedPage.let { page ->
                        TailFirstLoadedTranscriptPage(
                            requestBeforeLineId = Math.addExact(page.lastLineId, 1L),
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
    } catch (error: SessionOpenException) {
        tailFirstFailure(requestId, operation, tailFirstSessionOpenFailureCode(error.failure))
    } catch (error: TimeoutCancellationException) {
        val observedFailure = TailFirstE2EProductBridge.selectedSession()
            ?.let(repository.snapshot.value.sessionOpenFailures::get)
        tailFirstFailure(
            requestId,
            operation,
            observedFailure?.let(::tailFirstSessionOpenFailureCode) ?: "OPERATION_TIMEOUT",
        )
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

internal fun tailFirstSessionOpenFailureCode(failure: SessionOpenFailure): String =
    "SESSION_OPEN_${failure.code.stableCode.replace('-', '_')}"

private fun tailFirstSelectedSession(requestId: String, operation: String): java.util.UUID? =
    TailFirstE2EProductBridge.selectedSession()

private suspend fun awaitNewTailFirstSession(
    repository: ProductRepository,
    minimumTail: ULong = 0uL,
): SessionSummary? {
    val existingSessionIds = repository.snapshot.value.sessions.mapTo(mutableSetOf(), SessionSummary::id)
    return withTimeoutOrNull(150_000) {
        while (true) {
            try {
                repository.refresh()
            } catch (error: CancellationException) {
                throw error
            } catch (_: Throwable) {
                delay(500)
                continue
            }
            newestNewNonExitedSession(repository.snapshot.value.sessions, existingSessionIds)
                ?.takeIf { it.lastOutputSequence >= minimumTail }
                ?.let {
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
        put("bootstrap_source", snapshot.bootstrapSources[sessionId]?.name ?: "UNKNOWN")
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
        put("first_visible_line_id", observation.firstVisibleLineId ?: 0L)
        put("visible_text_sha256", observation.visibleTextSha256)
        put("next_before_line_id", (viewport?.nextBeforeLineId ?: 0u).toLong())
        put("transcript_top_row", observation.transcriptTopRow)
        put("transcript_page_count", observation.transcriptPageCount)
        put("transcript_row_count", observation.transcriptRowCount)
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
    var framesSinceImeRequest = 0
    val stableObservation = withTimeoutOrNull(TAIL_FIRST_IME_STABILITY_TIMEOUT_MILLIS) {
        while (true) {
            awaitTailFirstUiFrame()
            val observation = withContext(Dispatchers.Main.immediate) { surface.observe() }
            if (tracker.recordFrame(observation.imeLayoutSample())) {
                return@withTimeoutOrNull observation
            }
            // ViewRoot focus/served-view ownership can be lost while the
            // terminal is catching up after a large history replay. Reassert
            // the same idempotent debug request periodically; readiness still
            // requires two exact, non-overlapping geometry samples.
            framesSinceImeRequest += 1
            if (framesSinceImeRequest >= TAIL_FIRST_IME_RETRY_FRAME_INTERVAL) {
                withContext(Dispatchers.Main.immediate) { surface.showIme() }
                framesSinceImeRequest = 0
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
    previous: TranscriptViewportState?,
) = withTimeout(45_000) {
    val previousPageRanges = previous?.pages?.map { it.firstLineId to it.lastLineId }.orEmpty()
    while (true) {
        val state = repository.snapshot.value.transcriptViewports[sessionId]
        val pageRanges = state?.pages?.map { it.firstLineId to it.lastLineId }.orEmpty()
        val changed = previous == null || state == null ||
            state.snapshotAnchorSequence != previous.snapshotAnchorSequence ||
            state.viewportGeneration != previous.viewportGeneration ||
            state.nextBeforeLineId != previous.nextBeforeLineId ||
            state.nextCursor != previous.nextCursor ||
            pageRanges != previousPageRanges ||
            state.error != previous.error
        if (state != null && !state.loading && changed) return@withTimeout
        delay(50)
    }
}

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

private fun tailFirstTextSha256(text: String): String = MessageDigest.getInstance("SHA-256")
    .digest(text.encodeToByteArray())
    .joinToString("") { byte -> "%02x".format(Locale.ROOT, byte.toInt() and 0xff) }

private fun loadTerminalDisplayPreferences(context: android.content.Context): TerminalDisplayPreferences {
    val preferences = context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
    val loaded = TerminalDisplayPreferences(
        fontScheme = TerminalFontScheme.fromWireValue(preferences.getString(TERMINAL_FONT_SCHEME_KEY, null)),
        customFontDisplayName = preferences.getString(TERMINAL_CUSTOM_FONT_NAME_KEY, null),
        customFontSha256 = preferences.getString(TERMINAL_CUSTOM_FONT_SHA256_KEY, null),
        fontSizeSp = preferences.getFloat(TERMINAL_FONT_SIZE_KEY, 14f),
        lineSpacingMultiplier = preferences.getFloat(TERMINAL_LINE_SPACING_KEY, 1f),
        foregroundArgb = preferences.getInt(TERMINAL_FOREGROUND_KEY, 0xFFE9EEF5.toInt()),
        backgroundArgb = preferences.getInt(TERMINAL_BACKGROUND_KEY, 0xFF080B10.toInt()),
    ).normalized
    return if (loaded.fontScheme == TerminalFontScheme.CUSTOM && !TerminalFontStore.hasCustomFont(context)) {
        loaded.copy(fontScheme = TerminalFontScheme.BUNDLED_NERD_FONT)
    } else loaded
}

private fun saveTerminalDisplayPreferences(
    context: android.content.Context,
    value: TerminalDisplayPreferences,
) {
    val normalized = value.normalized
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putString(TERMINAL_FONT_SCHEME_KEY, normalized.fontScheme.wireValue)
        .putString(TERMINAL_CUSTOM_FONT_NAME_KEY, normalized.customFontDisplayName)
        .putString(TERMINAL_CUSTOM_FONT_SHA256_KEY, normalized.customFontSha256)
        .putFloat(TERMINAL_FONT_SIZE_KEY, normalized.fontSizeSp)
        .putFloat(TERMINAL_LINE_SPACING_KEY, normalized.lineSpacingMultiplier)
        .putInt(TERMINAL_FOREGROUND_KEY, normalized.foregroundArgb)
        .putInt(TERMINAL_BACKGROUND_KEY, normalized.backgroundArgb)
        .apply()
}

internal data class ProductNavigationState(
    val primaryDestination: ProductPrimaryDestination = ProductPrimaryDestination.DEVICES,
    val selectedSessionId: String? = null,
    val openedSessionIds: List<String> = emptyList(),
    val expandedPcIds: Set<String> = emptySet(),
)

internal fun loadProductNavigationState(context: android.content.Context): ProductNavigationState {
    val preferences = context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
    val primaryDestination = preferences.getString(PRODUCT_PRIMARY_DESTINATION_KEY, null)
        ?.let { stored -> ProductPrimaryDestination.entries.firstOrNull { it.name == stored } }
        ?: ProductPrimaryDestination.DEVICES
    val openedSessionIds = parseStoredUuidArray(
        preferences.getString(PRODUCT_OPENED_SESSION_IDS_KEY, null),
    )
    val selectedSessionId = preferences.getString(PRODUCT_SELECTED_SESSION_ID_KEY, null)
        ?.takeIf { stored -> runCatching { java.util.UUID.fromString(stored) }.isSuccess }
        ?.takeIf(openedSessionIds::contains)
    val expandedPcIds = preferences.getStringSet(PRODUCT_EXPANDED_PC_IDS_KEY, emptySet())
        .orEmpty()
        .filterTo(linkedSetOf()) { stored -> runCatching { java.util.UUID.fromString(stored) }.isSuccess }
    return ProductNavigationState(primaryDestination, selectedSessionId, openedSessionIds, expandedPcIds)
}

private fun parseStoredUuidArray(value: String?): List<String> = runCatching {
    val array = JSONArray(value ?: "[]")
    buildList {
        for (index in 0 until array.length()) {
            val stored = array.optString(index)
            if (runCatching { java.util.UUID.fromString(stored) }.isSuccess && stored !in this) add(stored)
        }
    }
}.getOrDefault(emptyList())

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

internal fun clearSelectedProductSession(context: android.content.Context) {
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .remove(PRODUCT_SELECTED_SESSION_ID_KEY)
        .apply()
}

internal fun saveOpenedProductSessions(context: android.content.Context, sessionIds: List<String>) {
    val normalized = sessionIds.distinct().filter { stored ->
        runCatching { java.util.UUID.fromString(stored) }.isSuccess
    }
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putString(PRODUCT_OPENED_SESSION_IDS_KEY, JSONArray(normalized).toString())
        .apply()
}

internal fun saveProductSessionTabs(
    context: android.content.Context,
    sessionIds: List<String>,
    selectedSessionId: String?,
) {
    val normalized = sessionIds.distinct().filter { stored ->
        runCatching { java.util.UUID.fromString(stored) }.isSuccess
    }
    val selected = selectedSessionId?.takeIf(normalized::contains)
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putString(PRODUCT_OPENED_SESSION_IDS_KEY, JSONArray(normalized).toString())
        .also { editor ->
            if (selected == null) editor.remove(PRODUCT_SELECTED_SESSION_ID_KEY)
            else editor.putString(PRODUCT_SELECTED_SESSION_ID_KEY, selected)
        }
        .apply()
}

internal fun saveExpandedProductPcs(context: android.content.Context, pcIds: Set<String>) {
    val normalized = pcIds.filterTo(linkedSetOf()) { stored ->
        runCatching { java.util.UUID.fromString(stored) }.isSuccess
    }
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .putStringSet(PRODUCT_EXPANDED_PC_IDS_KEY, normalized)
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
    val customShortcutRows = if (preferences.contains(TERMINAL_CUSTOM_SHORTCUT_ROWS_KEY)) {
        runCatching {
            val rows = JSONArray(preferences.getString(TERMINAL_CUSTOM_SHORTCUT_ROWS_KEY, "[[],[]]") ?: "[[],[]]")
            List(TERMINAL_SHORTCUT_DOCK_ROW_COUNT) { rowIndex ->
                val row = rows.optJSONArray(rowIndex) ?: JSONArray()
                buildList {
                    for (index in 0 until row.length()) {
                        row.optString(index).takeIf(String::isNotBlank)?.let(::add)
                    }
                }
            }
        }.getOrDefault(emptyList())
    } else {
        emptyList()
    }
    val dockLayout = runCatching {
        TerminalShortcutDockLayout.valueOf(
            preferences.getString(
                TERMINAL_SHORTCUT_DOCK_LAYOUT_KEY,
                TerminalShortcutDockLayout.TWO_ROWS.name,
            ) ?: TerminalShortcutDockLayout.TWO_ROWS.name,
        )
    }.getOrDefault(TerminalShortcutDockLayout.TWO_ROWS)
    val singleRowCustomShortcutOrder = if (preferences.contains(TERMINAL_CUSTOM_SHORTCUT_ONE_ROW_ORDER_KEY)) {
        runCatching {
            val order = JSONArray(
                preferences.getString(TERMINAL_CUSTOM_SHORTCUT_ONE_ROW_ORDER_KEY, "[]") ?: "[]",
            )
            buildList {
                for (index in 0 until order.length()) {
                    order.optString(index).takeIf(String::isNotBlank)?.let(::add)
                }
            }
        }.getOrDefault(emptyList())
    } else {
        emptyList()
    }
    return TerminalShortcutPreferences(
        labels = TerminalAction.entries.associateWith { action ->
            preferences.getString(TERMINAL_SHORTCUT_PREFIX + action.name, defaults.label(action)) ?: defaults.label(action)
        },
        visibleActions = storedOrder,
        customShortcuts = customShortcuts,
        dockLayout = dockLayout,
        singleRowCustomShortcutOrder = singleRowCustomShortcutOrder,
        customShortcutRows = customShortcutRows,
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
    val customShortcutRows = JSONArray().apply {
        normalized.customShortcutRows.forEach { row -> put(JSONArray(row)) }
    }
    context.getSharedPreferences(PRODUCT_PREFERENCES, android.content.Context.MODE_PRIVATE)
        .edit()
        .apply {
            normalized.labels.forEach { (action, label) -> putString(TERMINAL_SHORTCUT_PREFIX + action.name, label) }
        }
        .putString(TERMINAL_SHORTCUT_ORDER_KEY, normalized.visibleActions.joinToString(",") { it.name })
        .putString(TERMINAL_CUSTOM_SHORTCUTS_KEY, customShortcuts.toString())
        .putString(TERMINAL_UNIFIED_SHORTCUT_ORDER_KEY, normalized.shortcutOrder.joinToString(","))
        .putString(TERMINAL_CUSTOM_SHORTCUT_ROWS_KEY, customShortcutRows.toString())
        .putString(TERMINAL_SHORTCUT_DOCK_LAYOUT_KEY, normalized.dockLayout.name)
        .putString(
            TERMINAL_CUSTOM_SHORTCUT_ONE_ROW_ORDER_KEY,
            JSONArray(normalized.singleRowCustomShortcutOrder).toString(),
        )
        .apply()
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ProductTopBar(
    destination: ProductDestination,
    onBack: (() -> Unit)?,
    onRefresh: () -> Unit,
    refreshEnabled: Boolean,
    refreshContentDescription: String,
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
            IconButton(onClick = onRefresh, enabled = refreshEnabled) {
                Icon(Icons.Filled.Refresh, contentDescription = refreshContentDescription)
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
    Surface(color = MaterialTheme.colorScheme.surface) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .navigationBarsPadding()
                .height(64.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ProductPrimaryDestination.entries.forEach { item ->
                NavigationBarItem(
                    selected = destination == item,
                    onClick = { onDestinationSelected(item) },
                    icon = { Icon(item.icon(), contentDescription = item.localizedLabel()) },
                    label = { Text(item.localizedLabel(), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    modifier = Modifier.weight(1f).fillMaxHeight(),
                )
            }
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

internal const val DEVICE_PULL_TO_REFRESH_TEST_TAG = "device-pull-to-refresh"
internal const val DEVICE_REFRESH_ACTION_TEST_TAG = "device-refresh-action"

internal fun deviceRefreshInProgress(
    refreshInFlight: Boolean,
    connectionState: ProductConnectionState,
): Boolean = refreshInFlight || connectionState == ProductConnectionState.CONNECTING

@OptIn(ExperimentalMaterial3Api::class)
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
    openedSessionIds: List<String>,
    onSessionSelected: (java.util.UUID) -> Boolean,
    onCloseSessionTab: (java.util.UUID) -> Unit,
    onOpenSession: suspend (java.util.UUID) -> Unit,
    onLoadTranscript: (java.util.UUID) -> Unit,
    onClearTranscript: (java.util.UUID) -> Unit,
    terminalEngine: (java.util.UUID) -> TerminalViewportEngine?,
    historyEngine: (java.util.UUID) -> TerminalViewportEngine?,
    selectedHistoryId: String?,
    onHistorySelected: (HistoryEntry) -> Unit,
    onOpenSessions: () -> Unit,
    onOpenHistory: () -> Unit,
    selectedRemotePcId: String?,
    selectedRemotePcName: String,
    remoteDirectoryFavorites: List<RemoteDirectoryFavorite>,
    expandedPcIds: Set<String>,
    onTogglePcExpanded: (java.util.UUID) -> Unit,
    onBrowsePc: (PcSummary) -> Unit,
    onAgentUpdateRequested: (PcSummary) -> Unit,
    onOpenDownloads: () -> Unit,
    onOpenRemoteFavorite: (RemoteDirectoryFavorite) -> Unit,
    onToggleRemoteFavorite: (java.util.UUID, String, RemoteFileEntry) -> Unit,
    onRemoveRemoteFavorite: (RemoteDirectoryFavorite) -> Unit,
    onBrowseRemoteDirectory: (java.util.UUID, String, String, Long?) -> Unit,
    onDownloadRemoteEntry: (java.util.UUID, String, RemoteFileEntry) -> Unit,
    onOpenRemoteShell: (java.util.UUID, String, TerminalCommandCapability) -> Unit,
    onCancelRemoteDownload: (java.util.UUID) -> Unit,
    onRetryRemoteDownload: (RemoteDownloadRecord) -> Unit,
    onDeleteRemoteDownloadRecord: (java.util.UUID) -> Unit,
    devicesRefreshing: Boolean,
    onRefreshDevices: () -> Unit,
    themePreference: ProductThemePreference,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit,
    keepScreenOn: Boolean,
    onKeepScreenOnChanged: (Boolean) -> Unit,
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
    if (destination == ProductDestination.DEVICES) {
        PullToRefreshBox(
            isRefreshing = devicesRefreshing,
            onRefresh = onRefreshDevices,
            modifier = modifier
                .fillMaxSize()
                .testTag(DEVICE_PULL_TO_REFRESH_TEST_TAG),
        ) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 16.dp, vertical = 14.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                DevicesPage(
                    snapshot = snapshot,
                    expandedPcIds = expandedPcIds,
                    onTogglePcExpanded = onTogglePcExpanded,
                    onOpenSession = { sessionId ->
                        if (onSessionSelected(sessionId)) onOpenSessions()
                    },
                    onOpenDownloads = onOpenDownloads,
                    onBrowsePc = onBrowsePc,
                    onAgentUpdateRequested = onAgentUpdateRequested,
                    onStopSessionRequested = onStopSessionRequested,
                    onDeleteSessionRequested = onDeleteSessionRequested,
                )
            }
        }
        return
    }
    if (destination == ProductDestination.FILES && remotePcId != null) {
        val remotePc = snapshot.pcs.firstOrNull { pc -> pc.id == remotePcId }
        val remotePcName = remotePc?.name ?: selectedRemotePcName
        val state = snapshot.remoteFileBrowser.let { current ->
            if (current.pcId == remotePcId) current else RemoteFileBrowserState(
                pcId = remotePcId,
                pcName = remotePcName,
            )
        }
        RemoteFileBrowserPage(
            state = state,
            supportedCommands = remotePc?.let(::supportedRemoteTerminalCommands).orEmpty(),
            favorites = remoteDirectoryFavorites,
            favoriteSupportedCommands = { favorite ->
                snapshot.pcs.firstOrNull { pc -> pc.id == favorite.pcId }
                    ?.let(::supportedRemoteTerminalCommands)
                    .orEmpty()
            },
            onOpenDirectory = { path ->
                onBrowseRemoteDirectory(remotePcId, remotePcName, path, null)
            },
            onOpenFavorite = onOpenRemoteFavorite,
            onLoadMore = { path, offset ->
                onBrowseRemoteDirectory(remotePcId, remotePcName, path, offset)
            },
            onDownload = { entry -> onDownloadRemoteEntry(remotePcId, remotePcName, entry) },
            onDownloadFavorite = { favorite ->
                val favoritePcName = snapshot.pcs.firstOrNull { pc -> pc.id == favorite.pcId }
                    ?.name
                    ?: favorite.pcName
                onDownloadRemoteEntry(
                    favorite.pcId,
                    favoritePcName,
                    RemoteFileEntry(
                        name = favorite.directoryName.ifBlank { "directory" },
                        path = favorite.path,
                        kind = RemoteFileKind.DIRECTORY,
                    ),
                )
            },
            onOpenShell = { path, shell -> onOpenRemoteShell(remotePcId, path, shell) },
            onOpenFavoriteShell = { favorite, shell ->
                onOpenRemoteShell(favorite.pcId, favorite.path, shell)
            },
            onToggleFavorite = { entry ->
                onToggleRemoteFavorite(remotePcId, remotePcName, entry)
            },
            onRemoveFavorite = onRemoveRemoteFavorite,
            modifier = modifier.fillMaxSize(),
        )
        return
    }
    if (destination == ProductDestination.DOWNLOADS) {
        RemoteDownloadCenterPage(
            pcId = remotePcId,
            pcName = selectedRemotePcName.takeIf(String::isNotBlank),
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
            openedSessionIds = openedSessionIds,
            onSessionSelected = { sessionId ->
                onSessionSelected(sessionId)
                Unit
            },
            onCloseSessionTab = onCloseSessionTab,
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
            ProductDestination.DEVICES -> Unit
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
                keepScreenOn = keepScreenOn,
                onKeepScreenOnChanged = onKeepScreenOnChanged,
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
private fun PrimaryHomeHeader(onOpenDownloads: () -> Unit) {
    val greetingPeriod by produceState(initialValue = homeGreetingPeriod(LocalTime.now().hour)) {
        while (true) {
            val now = LocalTime.now()
            value = homeGreetingPeriod(now.hour)
            val delayUntilNextMinute = (
                (60L - now.second) * 1_000L - now.nano / 1_000_000L
            ).coerceAtLeast(250L)
            delay(delayUntilNextMinute)
        }
    }
    val greeting = when (greetingPeriod) {
        HomeGreetingPeriod.MORNING -> R.string.home_greeting_morning
        HomeGreetingPeriod.AFTERNOON -> R.string.home_greeting_afternoon
        HomeGreetingPeriod.EVENING -> R.string.home_greeting_evening
    }
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(stringResource(greeting), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.secondary)
            Text(stringResource(R.string.home_title), style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.SemiBold)
        }
        IconButton(onClick = onOpenDownloads, modifier = Modifier.size(48.dp)) {
            Icon(Icons.Filled.Download, contentDescription = stringResource(R.string.action_download_center), modifier = Modifier.size(30.dp))
        }
    }
}

@Composable
private fun DevicesPage(
    snapshot: ProductSnapshot,
    expandedPcIds: Set<String>,
    onTogglePcExpanded: (java.util.UUID) -> Unit,
    onOpenSession: (java.util.UUID) -> Unit,
    onOpenDownloads: () -> Unit,
    onBrowsePc: (PcSummary) -> Unit,
    onAgentUpdateRequested: (PcSummary) -> Unit,
    onStopSessionRequested: (java.util.UUID) -> Unit,
    onDeleteSessionRequested: (java.util.UUID) -> Unit,
) {
    PrimaryHomeHeader(onOpenDownloads = onOpenDownloads)
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
        Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            pcs.forEach { pc ->
                val sessions = snapshot.sessions.filter { it.pcId == pc.id }
                val pcKey = pc.id.toString()
                PcRow(
                    pc = pc,
                    sessions = sessions,
                    sessionTitles = snapshot.sessionTitles,
                    expanded = pcKey in expandedPcIds,
                    onToggleExpanded = { onTogglePcExpanded(pc.id) },
                    onOpenSession = onOpenSession,
                    onBrowseFiles = if (PcCapabilityIds.FILE_OPERATIONS_V1 in pc.capabilities) {
                        { onBrowsePc(pc) }
                    } else {
                        null
                    },
                    onAgentUpdate = if (
                        pc.platform == "windows" && PcCapabilityIds.REMOTE_AGENT_UPDATE_V1 in pc.capabilities
                    ) {
                        { onAgentUpdateRequested(pc) }
                    } else {
                        null
                    },
                    onStopSessionRequested = onStopSessionRequested,
                    onDeleteSessionRequested = onDeleteSessionRequested,
                )
            }
        }
    }
    // Keep the final row clear of the transparent 48 dp refresh touch target.
    Spacer(Modifier.height(56.dp))
}

@Composable
private fun AgentUpdateDialog(
    state: AgentUpdateDialogState,
    onDismiss: () -> Unit,
    onInstall: (AgentUpdateDialogState.Offer) -> Unit,
    onCancel: (AgentUpdateDialogState.Running) -> Unit,
) {
    val dismissible = when (state) {
        is AgentUpdateDialogState.Running -> state.progress.terminal
        is AgentUpdateDialogState.Starting -> false
        else -> true
    }
    val title = when (state) {
        is AgentUpdateDialogState.Loading -> stringResource(R.string.agent_update_checking)
        is AgentUpdateDialogState.Offer -> if (state.eligibility.eligible) {
            stringResource(R.string.agent_update_confirm_title, state.pc.name)
        } else {
            stringResource(R.string.agent_update_unavailable_title)
        }
        is AgentUpdateDialogState.Starting -> stringResource(R.string.agent_update_starting)
        is AgentUpdateDialogState.Running -> when (state.progress.status) {
            "SUCCEEDED" -> stringResource(R.string.agent_update_succeeded)
            "ROLLED_BACK" -> stringResource(R.string.agent_update_rolled_back)
            "ROLLBACK_FAILED" -> stringResource(R.string.agent_update_rollback_failed)
            "FAILED", "EXPIRED", "CANCELLED" -> stringResource(R.string.agent_update_failed)
            else -> stringResource(R.string.agent_update_in_progress)
        }
        is AgentUpdateDialogState.Failure -> stringResource(R.string.agent_update_request_failed)
    }
    AlertDialog(
        onDismissRequest = { if (dismissible) onDismiss() },
        title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                when (state) {
                    is AgentUpdateDialogState.Loading -> {
                        CircularProgressIndicator(modifier = Modifier.size(28.dp))
                        Text(stringResource(R.string.agent_update_checking_copy, state.pc.agentVersion))
                    }
                    is AgentUpdateDialogState.Offer -> {
                        val offer = state.eligibility
                        if (offer.eligible) {
                            Text(
                                stringResource(
                                    R.string.agent_update_versions,
                                    offer.currentVersion,
                                    offer.targetVersion.orEmpty(),
                                    offer.targetBuild ?: 0L,
                                ),
                            )
                            if (!offer.releaseNotes.isNullOrBlank()) Text(offer.releaseNotes)
                            Text(
                                stringResource(
                                    R.string.agent_update_session_warning,
                                    offer.activeSessionCount,
                                ),
                                color = MaterialTheme.colorScheme.error,
                            )
                            Text(stringResource(R.string.agent_update_rollback_copy))
                        } else {
                            Text(agentUpdateReasonText(offer.reasonCode))
                            Text(stringResource(R.string.agent_update_current_version, offer.currentVersion))
                        }
                    }
                    is AgentUpdateDialogState.Starting -> {
                        CircularProgressIndicator(modifier = Modifier.size(28.dp))
                        Text(stringResource(R.string.agent_update_creating_job))
                    }
                    is AgentUpdateDialogState.Running -> {
                        val progress = state.progress
                        LinearProgressIndicator(
                            progress = { progress.progressPercent / 100f },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Text(
                            stringResource(
                                R.string.agent_update_progress,
                                agentUpdatePhaseText(progress.phase),
                                progress.progressPercent,
                            ),
                        )
                        progress.activeSessionCount?.let {
                            Text(stringResource(R.string.agent_update_active_sessions, it))
                        }
                        if (!progress.errorCode.isNullOrBlank()) {
                            Text(
                                stringResource(R.string.agent_update_error_code, progress.errorCode),
                                color = MaterialTheme.colorScheme.error,
                            )
                        }
                        if (progress.status == "ROLLED_BACK") {
                            Text(stringResource(R.string.agent_update_restored_version, progress.observedAgentVersion.orEmpty()))
                        }
                        if (!progress.cancelable && !progress.terminal) {
                            Text(stringResource(R.string.agent_update_cannot_cancel))
                        }
                    }
                    is AgentUpdateDialogState.Failure -> {
                        val apiError = state.error as? TermLinkApiException
                        Text(
                            if (apiError != null) {
                                stringResource(
                                    R.string.agent_update_server_error,
                                    apiError.status,
                                    apiError.code,
                                    apiError.traceId ?: "-",
                                )
                            } else {
                                stringResource(R.string.agent_update_unknown_error)
                            },
                            color = MaterialTheme.colorScheme.error,
                        )
                    }
                }
            }
        },
        confirmButton = {
            when (state) {
                is AgentUpdateDialogState.Offer -> if (state.eligibility.eligible) {
                    Button(onClick = { onInstall(state) }) {
                        Text(stringResource(R.string.agent_update_confirm))
                    }
                } else {
                    TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_close)) }
                }
                is AgentUpdateDialogState.Running -> if (state.progress.terminal) {
                    TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_close)) }
                }
                is AgentUpdateDialogState.Loading,
                is AgentUpdateDialogState.Failure -> {
                    TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_close)) }
                }
                is AgentUpdateDialogState.Starting -> Unit
            }
        },
        dismissButton = {
            when {
                state is AgentUpdateDialogState.Offer && state.eligibility.eligible -> {
                    TextButton(onClick = onDismiss) { Text(stringResource(R.string.update_action_later)) }
                }
                state is AgentUpdateDialogState.Running && state.progress.cancelable -> {
                    TextButton(onClick = { onCancel(state) }) { Text(stringResource(R.string.action_cancel)) }
                }
            }
        },
    )
}

@Composable
private fun agentUpdateReasonText(code: String?): String = when (code) {
    "PC_OFFLINE" -> stringResource(R.string.agent_update_reason_offline)
    "REMOTE_UPDATE_DISABLED" -> stringResource(R.string.agent_update_reason_routing_disabled)
    "REMOTE_UPDATE_UNSUPPORTED" -> stringResource(R.string.agent_update_reason_unsupported)
    "AGENT_VERSION_UNSUPPORTED" -> stringResource(R.string.agent_update_reason_seed_required)
    "UPDATE_NOT_AVAILABLE" -> stringResource(R.string.agent_update_reason_current)
    else -> stringResource(R.string.agent_update_reason_unknown, code ?: "-")
}

@Composable
private fun agentUpdatePhaseText(phase: String): String = when (phase) {
    "REQUESTED" -> stringResource(R.string.agent_update_phase_requested)
    "PRECHECKING" -> stringResource(R.string.agent_update_phase_prechecking)
    "DOWNLOADING" -> stringResource(R.string.agent_update_phase_downloading)
    "VERIFIED" -> stringResource(R.string.agent_update_phase_verified)
    "DRAINING_SESSIONS" -> stringResource(R.string.agent_update_phase_draining)
    "INSTALLING" -> stringResource(R.string.agent_update_phase_installing)
    "STARTING_NEW_AGENT" -> stringResource(R.string.agent_update_phase_starting_agent)
    "VERIFYING_CONNECTION" -> stringResource(R.string.agent_update_phase_verifying_connection)
    "ROLLING_BACK" -> stringResource(R.string.agent_update_phase_rolling_back)
    "SUCCEEDED" -> stringResource(R.string.agent_update_succeeded)
    "ROLLED_BACK" -> stringResource(R.string.agent_update_rolled_back)
    "ROLLBACK_FAILED" -> stringResource(R.string.agent_update_rollback_failed)
    else -> stringResource(R.string.agent_update_failed)
}

/**
 * Device-page projection. Repository state intentionally retains offline PCs
 * for session/history reconciliation; this surface shows only server-online
 * trusted PC Agents across all supported desktop platforms.
 */
internal fun visibleOnlineWindowsPcs(snapshot: ProductSnapshot): List<PcSummary> {
    val windowsDeviceIds = snapshot.devices.asSequence()
        .filter { it.type.isPcAgent() }
        .mapTo(hashSetOf()) { it.id }
    return snapshot.pcs.filter { it.online && it.deviceId in windowsDeviceIds }
}

@Composable
@OptIn(ExperimentalFoundationApi::class)
private fun PcRow(
    pc: PcSummary,
    sessions: List<SessionSummary> = emptyList(),
    sessionTitles: Map<java.util.UUID, String> = emptyMap(),
    expanded: Boolean = false,
    onToggleExpanded: (() -> Unit)? = null,
    onOpenSession: ((java.util.UUID) -> Unit)? = null,
    onBrowseFiles: (() -> Unit)? = null,
    onAgentUpdate: (() -> Unit)? = null,
    onStopSessionRequested: ((java.util.UUID) -> Unit)? = null,
    onDeleteSessionRequested: ((java.util.UUID) -> Unit)? = null,
) {
    var deviceMenuExpanded by remember { mutableStateOf(false) }
    val deviceMenuDescription = stringResource(R.string.device_menu_accessibility)
    val platformLabel = stringResource(
        when (pc.platform) {
            "linux" -> R.string.pc_platform_linux
            "macos" -> R.string.pc_platform_macos
            else -> R.string.pc_platform_windows
        },
    )
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .combinedClickable(
                        enabled = onToggleExpanded != null || onAgentUpdate != null || onBrowseFiles != null,
                        onClick = {
                            if (sessions.isNotEmpty()) onToggleExpanded?.invoke()
                        },
                        onLongClick = { deviceMenuExpanded = true },
                        onLongClickLabel = deviceMenuDescription,
                    )
                    .padding(start = 12.dp, top = 8.dp, end = 8.dp, bottom = 8.dp),
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(end = 28.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Icon(
                        if (pc.platform == "windows") Icons.Filled.LaptopWindows else Icons.Filled.Terminal,
                        contentDescription = platformLabel,
                        modifier = Modifier.size(22.dp),
                        tint = MaterialTheme.colorScheme.primary,
                    )
                    Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
                        Text(
                            pc.name,
                            style = MaterialTheme.typography.titleMedium,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            stringResource(
                                R.string.pc_routing_status,
                                pc.agentVersion,
                                stringResource(if (pc.remoteRoutingEnabled) R.string.remote_enabled else R.string.remote_disabled),
                                platformLabel,
                            ),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    onBrowseFiles?.let { browse ->
                        TextButton(
                            onClick = browse,
                            modifier = Modifier.heightIn(min = 36.dp),
                            contentPadding = PaddingValues(horizontal = 6.dp, vertical = 2.dp),
                        ) {
                            Icon(Icons.Filled.Folder, contentDescription = null, modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(4.dp))
                            Text(
                                stringResource(R.string.action_browse_files),
                                style = MaterialTheme.typography.labelMedium,
                            )
                        }
                    }
                }
                Box(
                    modifier = Modifier
                        .align(Alignment.TopEnd)
                        .padding(top = 5.5.dp, end = 5.5.dp),
                ) {
                    StatusDot(
                        color = if (pc.online) MaterialTheme.colorScheme.secondary else MaterialTheme.colorScheme.onSurfaceVariant,
                        contentDescription = if (pc.online) {
                            stringResource(R.string.pc_online_accessibility)
                        } else {
                            stringResource(R.string.pc_offline_accessibility)
                        },
                    )
                }
                if (sessions.isNotEmpty()) {
                    Box(
                        modifier = Modifier
                            .align(Alignment.BottomEnd)
                            .padding(bottom = 5.5.dp),
                    ) {
                        Icon(
                            imageVector = if (expanded) Icons.Filled.KeyboardArrowUp else Icons.Filled.KeyboardArrowDown,
                            contentDescription = stringResource(
                                if (expanded) R.string.action_collapse_sessions else R.string.action_expand_sessions,
                            ),
                            modifier = Modifier.size(20.dp),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                DropdownMenu(
                    expanded = deviceMenuExpanded,
                    onDismissRequest = { deviceMenuExpanded = false },
                ) {
                    onBrowseFiles?.let { browse ->
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.action_browse_files)) },
                            leadingIcon = { Icon(Icons.Filled.Folder, contentDescription = null) },
                            onClick = {
                                deviceMenuExpanded = false
                                browse()
                            },
                        )
                    }
                    onAgentUpdate?.let { update ->
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.agent_update_action)) },
                            leadingIcon = { Icon(Icons.Filled.Refresh, contentDescription = null) },
                            onClick = {
                                deviceMenuExpanded = false
                                update()
                            },
                        )
                    }
                }
            }
            if (expanded && sessions.isNotEmpty()) {
                HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(start = 42.dp, end = 10.dp, top = 5.dp, bottom = 7.dp),
                    verticalArrangement = Arrangement.spacedBy(2.dp),
                ) {
                    sessions.forEach { session ->
                        val active = isDeviceSessionActive(session.online)
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(ProductShape)
                                .clickable(
                                    enabled = onOpenSession != null && isSessionTabOpenable(session),
                                ) {
                                    onOpenSession?.invoke(session.id)
                                }
                                .padding(horizontal = 7.dp, vertical = 4.dp),
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
                                        modifier = Modifier.size(36.dp),
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
                                        modifier = Modifier.size(36.dp),
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

internal fun isSessionTabOpenable(session: SessionSummary?): Boolean =
    session != null && session.online && session.state != SessionState.EXITED

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
    openedSessionIds: List<String>,
    onSessionSelected: (java.util.UUID) -> Unit,
    onCloseSessionTab: (java.util.UUID) -> Unit,
    onOpenSession: suspend (java.util.UUID) -> Unit,
    onLoadTranscript: (java.util.UUID) -> Unit,
    onClearTranscript: (java.util.UUID) -> Unit,
    terminalEngine: (java.util.UUID) -> TerminalViewportEngine?,
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
    val opened = openedSessionIds.toHashSet()
    val openedSessions = snapshot.sessions.filter { it.id.toString() in opened }
    val requestedSelected = openedSessions.firstOrNull { it.id == requestedSelectedId }
    val requestedSelectedEngine = requestedSelected?.let { terminalEngine(it.id) }
    val activeSessions = liveSessionPageSessions(
        sessions = openedSessions,
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
                        onCloseSessionTab = onCloseSessionTab,
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
                        sessionOpenFailure = snapshot.sessionOpenFailures[selected.id],
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
                    onCloseSessionTab = onCloseSessionTab,
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
                    sessionOpenFailures = snapshot.sessionOpenFailures,
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
    onCloseSessionTab: (java.util.UUID) -> Unit,
    pcLabel: (SessionSummary) -> String,
    selectedEngine: TerminalViewportEngine?,
    sessionStates: Map<java.util.UUID, RemoteSessionState>,
    onTerminalAction: (java.util.UUID, TerminalAction) -> Unit,
    onTerminalCustomShortcut: (java.util.UUID, TerminalCustomShortcut) -> Unit,
    onPasteRequested: (java.util.UUID) -> Unit,
    terminalInputResumeRegistry: TerminalInputResumeRegistry,
    transcriptViewports: Map<java.util.UUID, TranscriptViewportState>,
    liveAcknowledgements: Map<java.util.UUID, ULong>,
    bootstrapSources: Map<java.util.UUID, ProductBootstrapSource>,
    sessionOpenFailures: Map<java.util.UUID, SessionOpenFailure>,
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
                    .height(36.dp)
                    .testTag(MOBILE_SESSION_SELECTOR_TEST_TAG),
                contentPadding = PaddingValues(horizontal = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                items(sessions, key = { it.id }) { candidate ->
                    FilterChip(
                        selected = candidate.id == session.id,
                        onClick = { onSessionSelected(candidate.id) },
                        modifier = Modifier.widthIn(min = 140.dp, max = 260.dp),
                        label = {
                            Text(
                                pcLabel(candidate) + " · " + displaySessionTitle(candidate, sessionTitles[candidate.id]),
                                maxLines = 1,
                                overflow = TextOverflow.MiddleEllipsis,
                            )
                        },
                        trailingIcon = {
                            IconButton(
                                onClick = { onCloseSessionTab(candidate.id) },
                                modifier = Modifier
                                    .size(32.dp)
                                    .testTag(mobileSessionCloseTestTag(candidate.id)),
                            ) {
                                Icon(
                                    imageVector = Icons.Filled.Close,
                                    contentDescription = stringResource(R.string.action_close_session_tab),
                                    modifier = Modifier.size(17.dp),
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
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
            sessionOpenFailure = sessionOpenFailures[session.id],
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
    onCloseSessionTab: (java.util.UUID) -> Unit,
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
                    onClose = { onCloseSessionTab(session.id) },
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
    onClose: () -> Unit,
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
        IconButton(
            onClick = onClose,
            modifier = Modifier.size(36.dp),
        ) {
            Icon(
                Icons.Filled.Close,
                contentDescription = stringResource(R.string.action_close_session_tab),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(18.dp),
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
    sessionOpenFailure: SessionOpenFailure?,
    onLoadTranscript: () -> Unit,
    onClearTranscript: () -> Unit,
    onRetryOpen: () -> Unit,
    remoteState: RemoteSessionState?,
    engine: TerminalViewportEngine?,
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
            }
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
            ) {
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
                    modifier = Modifier.fillMaxSize(),
                )
                sessionOpenFailure?.let { failure ->
                    SessionOpenFailureCard(
                        failure = failure,
                        onRetry = onRetryOpen,
                        testTag = "session-open-retry:${session.id}",
                    )
                }
            }
        }
    }
}

@Composable
private fun SessionOpenFailureCard(
    failure: SessionOpenFailure,
    onRetry: () -> Unit,
    testTag: String,
) {
    val context = LocalContext.current
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 8.dp, vertical = 4.dp),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.errorContainer,
        contentColor = MaterialTheme.colorScheme.onErrorContainer,
    ) {
        Row(
            modifier = Modifier.padding(start = 10.dp, top = 8.dp, end = 6.dp, bottom = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.Filled.Warning,
                contentDescription = null,
                modifier = Modifier.size(18.dp),
            )
            Text(
                text = failure.localizedMessage(context),
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodySmall,
            )
            if (failure.code.retryable) {
                TextButton(
                    onClick = onRetry,
                    modifier = Modifier.testTag(testTag),
                ) {
                    Text(stringResource(R.string.terminal_history_retry))
                }
            }
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
    val value = shortcutPreferences.normalized
    val customRows = orderedTerminalCustomShortcutRows(value)
    val shortcutScrollState = rememberScrollState()
    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(Color.Black.copy(alpha = 0.62f), RoundedCornerShape(7.dp))
            .padding(horizontal = 3.dp, vertical = 2.dp),
    ) {
        if (!expanded) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(3.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TerminalDockButton(
                    onClick = { onExpandedChanged(true) },
                    modifier = Modifier
                        .width(TerminalDockButtonSize)
                        .testTag(terminalCommandTestTag("dock-toggle")),
                ) {
                    color -> Icon(
                        Icons.Filled.KeyboardArrowUp,
                        contentDescription = stringResource(R.string.terminal_dock_show),
                        tint = color,
                        modifier = Modifier.size(18.dp),
                    )
                }
                TerminalDockButton(
                    onClick = onPasteRequested,
                    enabled = enabled,
                    modifier = Modifier
                        .width(TerminalDockButtonSize)
                        .testTag(terminalCommandTestTag("paste")),
                ) {
                    color -> Icon(
                        Icons.Filled.ContentPaste,
                        contentDescription = stringResource(R.string.action_paste),
                        tint = color,
                        modifier = Modifier.size(17.dp),
                    )
                }
            }
            return@Column
        }
        if (value.dockLayout == TerminalShortcutDockLayout.ONE_ROW) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(3.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TerminalDockButton(
                    onClick = { onExpandedChanged(false) },
                    modifier = Modifier
                        .width(TerminalDockButtonSize)
                        .testTag(terminalCommandTestTag("dock-toggle")),
                ) {
                    color -> Icon(
                        Icons.Filled.KeyboardArrowDown,
                        contentDescription = stringResource(R.string.terminal_dock_hide),
                        tint = color,
                        modifier = Modifier.size(18.dp),
                    )
                }
                TerminalDockButton(
                    onClick = onPasteRequested,
                    enabled = enabled,
                    modifier = Modifier
                        .width(TerminalDockButtonSize)
                        .testTag(terminalCommandTestTag("paste")),
                ) {
                    color -> Icon(
                        Icons.Filled.ContentPaste,
                        contentDescription = stringResource(R.string.action_paste),
                        tint = color,
                        modifier = Modifier.size(17.dp),
                    )
                }
                Row(
                    modifier = Modifier
                        .weight(1f)
                        .horizontalScroll(shortcutScrollState),
                    horizontalArrangement = Arrangement.spacedBy(3.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    fixedSingleRowTerminalShortcutActions().forEach { action ->
                        TerminalBuiltInDockKey(
                            action = action,
                            shortcutPreferences = value,
                            enabled = enabled,
                            onAction = onAction,
                        )
                    }
                    orderedTerminalCustomShortcuts(value).forEach { shortcut ->
                        TerminalCustomTextKey(
                            shortcut = shortcut,
                            enabled = enabled,
                            onActivate = onCustomShortcut,
                        )
                    }
                }
            }
            return@Column
        }
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(3.dp),
            verticalAlignment = Alignment.Top,
        ) {
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                TerminalDockButton(
                    onClick = { onExpandedChanged(false) },
                    modifier = Modifier
                        .width(TerminalDockButtonSize)
                        .testTag(terminalCommandTestTag("dock-toggle")),
                ) {
                    color -> Icon(
                        Icons.Filled.KeyboardArrowDown,
                        contentDescription = stringResource(R.string.terminal_dock_hide),
                        tint = color,
                        modifier = Modifier.size(18.dp),
                    )
                }
                TerminalDockButton(
                    onClick = onPasteRequested,
                    enabled = enabled,
                    modifier = Modifier
                        .width(TerminalDockButtonSize)
                        .testTag(terminalCommandTestTag("paste")),
                ) {
                    color -> Icon(
                        Icons.Filled.ContentPaste,
                        contentDescription = stringResource(R.string.action_paste),
                        tint = color,
                        modifier = Modifier.size(17.dp),
                    )
                }
            }
            Row(
                modifier = Modifier
                    .weight(1f)
                    .horizontalScroll(shortcutScrollState),
                horizontalArrangement = Arrangement.spacedBy(3.dp),
                verticalAlignment = Alignment.Top,
            ) {
                val fixedRows = fixedTerminalShortcutRows()
                fixedRows.first().indices.forEach { column ->
                    Column(
                        modifier = Modifier
                            .width(IntrinsicSize.Max)
                            .widthIn(min = TerminalDockButtonSize),
                        verticalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        TerminalBuiltInDockKey(
                            action = fixedRows[TERMINAL_SHORTCUT_TOP_ROW][column],
                            shortcutPreferences = value,
                            enabled = enabled,
                            onAction = onAction,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        TerminalBuiltInDockKey(
                            action = fixedRows[TERMINAL_SHORTCUT_BOTTOM_ROW][column],
                            shortcutPreferences = value,
                            enabled = enabled,
                            onAction = onAction,
                            modifier = Modifier.fillMaxWidth(),
                        )
                    }
                }
                val customColumnCount = customRows.maxOfOrNull { it.size } ?: 0
                repeat(customColumnCount) { column ->
                    Column(
                        modifier = Modifier
                            .width(IntrinsicSize.Max)
                            .widthIn(min = TerminalDockButtonSize),
                        verticalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        listOf(TERMINAL_SHORTCUT_TOP_ROW, TERMINAL_SHORTCUT_BOTTOM_ROW).forEach { row ->
                            val shortcut = customRows[row].getOrNull(column)
                            if (shortcut == null) {
                                Spacer(Modifier.fillMaxWidth().height(TerminalDockButtonSize))
                            } else {
                                TerminalCustomTextKey(
                                    shortcut = shortcut,
                                    enabled = enabled,
                                    onActivate = onCustomShortcut,
                                    modifier = Modifier.fillMaxWidth(),
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun TerminalBuiltInDockKey(
    action: TerminalAction,
    shortcutPreferences: TerminalShortcutPreferences,
    enabled: Boolean,
    onAction: (TerminalAction) -> Unit,
    modifier: Modifier = Modifier,
) {
    when (action) {
        TerminalAction.ARROW_LEFT -> TerminalIconKey(Icons.AutoMirrored.Filled.KeyboardArrowLeft, stringResource(R.string.arrow_left), action, enabled, onAction, modifier)
        TerminalAction.ARROW_UP -> TerminalIconKey(Icons.Filled.KeyboardArrowUp, stringResource(R.string.arrow_up), action, enabled, onAction, modifier)
        TerminalAction.ARROW_DOWN -> TerminalIconKey(Icons.Filled.KeyboardArrowDown, stringResource(R.string.arrow_down), action, enabled, onAction, modifier)
        TerminalAction.ARROW_RIGHT -> TerminalIconKey(Icons.AutoMirrored.Filled.KeyboardArrowRight, stringResource(R.string.arrow_right), action, enabled, onAction, modifier)
        else -> TerminalTextKey(shortcutPreferences.label(action), action, enabled, onAction, modifier)
    }
}

@Composable
private fun TerminalCustomTextKey(
    shortcut: TerminalCustomShortcut,
    enabled: Boolean,
    onActivate: (TerminalCustomShortcut) -> Unit,
    modifier: Modifier = Modifier,
) {
    TerminalDockButton(
        onClick = { onActivate(shortcut) },
        enabled = enabled,
        modifier = modifier
            .widthIn(min = TerminalDockButtonSize, max = 120.dp)
            .testTag(terminalCommandTestTag("custom-${shortcut.id}")),
    ) {
        color -> Text(
            shortcut.displayLabel,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.labelSmall,
            color = color,
            modifier = Modifier.padding(horizontal = TerminalDockHorizontalContentPadding),
        )
    }
}

@Composable
private fun TerminalTextKey(
    label: String,
    action: TerminalAction,
    enabled: Boolean,
    onAction: (TerminalAction) -> Unit,
    modifier: Modifier = Modifier,
) {
    TerminalDockButton(
        onClick = { onAction(action) },
        enabled = enabled,
        modifier = modifier
            .widthIn(min = TerminalDockButtonSize)
            .testTag(terminalCommandTestTag(action.name.lowercase(Locale.ROOT))),
    ) {
        color -> Text(
            label,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.labelSmall,
            color = color,
            modifier = Modifier.padding(horizontal = TerminalDockHorizontalContentPadding),
        )
    }
}

@Composable
private fun TerminalIconKey(
    icon: ImageVector,
    description: String,
    action: TerminalAction,
    enabled: Boolean,
    onAction: (TerminalAction) -> Unit,
    modifier: Modifier = Modifier,
) {
    TerminalDockButton(
        onClick = { onAction(action) },
        enabled = enabled,
        modifier = modifier
            .widthIn(min = TerminalDockButtonSize)
            .testTag(terminalCommandTestTag(action.name.lowercase(Locale.ROOT))),
    ) {
        color -> Icon(
            icon,
            contentDescription = description,
            tint = color,
            modifier = Modifier.size(18.dp),
        )
    }
}

@Composable
private fun TerminalDockButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable (Color) -> Unit,
) {
    val shape = RoundedCornerShape(5.dp)
    val contentColor = Color.White.copy(alpha = if (enabled) 0.92f else 0.38f)
    Box(
        modifier = modifier
            .height(TerminalDockButtonSize)
            .clip(shape)
            .background(Color.White.copy(alpha = if (enabled) 0.09f else 0.025f))
            .border(
                width = 1.dp,
                color = Color.White.copy(alpha = if (enabled) 0.34f else 0.12f),
                shape = shape,
            )
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        content(contentColor)
    }
}

private fun sessionStatusColor(
    state: SessionState,
    sessionOnline: Boolean,
    remoteState: RemoteSessionState?,
): Color = when {
    state == SessionState.EXITED -> ProductMuted
    remoteState is RemoteSessionState.Exited -> ProductMuted
    isEstablishingSessionConnection(remoteState) -> ProductWarning
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
        is RemoteSessionState.Reconnecting -> if (isEstablishingSessionConnection(this)) {
            R.string.terminal_connecting
        } else {
            R.string.terminal_reconnecting
        }
        is RemoteSessionState.ReadOnly -> R.string.terminal_read_only
        is RemoteSessionState.Controlling -> R.string.terminal_controlling
        is RemoteSessionState.Exited -> R.string.terminal_exited
        is RemoteSessionState.HistoryGap -> R.string.terminal_history_gap
    },
)

internal const val DEVICE_BACK_EXIT_WINDOW_MILLIS = 2_000L

internal fun shouldExitAfterDeviceBack(nowMillis: Long, armedAtMillis: Long?): Boolean =
    armedAtMillis != null && nowMillis >= armedAtMillis &&
        nowMillis - armedAtMillis <= DEVICE_BACK_EXIT_WINDOW_MILLIS

internal fun isEstablishingSessionConnection(state: RemoteSessionState?): Boolean =
    state is RemoteSessionState.Reconnecting &&
        (state.reason == SESSION_BOOTSTRAP_IN_PROGRESS_REASON ||
            state.reason == SESSION_BOOTSTRAP_FAILED_REASON)

@Composable
private fun StatusDot(
    color: Color,
    contentDescription: String,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            .semantics { this.contentDescription = contentDescription }
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
    engine: TerminalViewportEngine?,
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

    fun resumeLiveAfterAcceptedInput(resumeViewport: Boolean = true) {
        val effect = terminalInputResumeEffect(
            accepted = true,
            // Read current mutable state rather than the last composed Boolean:
            // input can be accepted in the same frame that requested history.
            detachedOverlayActive = shouldShowTranscriptOverlay(
                overlayRequested,
                latestTranscriptState.value,
            ),
        )
        if (resumeViewport && effect.resumeViewportAfterLocalInput) {
            activeEngine.resumeViewportAfterLocalInput()
        }
        if (effect.closeDetachedOverlay) {
            overlayRequested = false
            localLoadingGuard = false
            transcriptRendererFailed = false
            latestClearTranscript.value()
        }
    }

    DisposableEffect(session.id, activeEngine, terminalInputResumeRegistry) {
        // The engine applies its cursor-visibility policy at the exact input
        // acceptance point. This callback owns only the detached overlay so a
        // later render frame cannot retroactively change that viewport choice.
        activeEngine.setOnAcceptedLocalUserInputListener {
            resumeLiveAfterAcceptedInput(resumeViewport = false)
        }
        val registration = terminalInputResumeRegistry.register(session.id) {
            resumeLiveAfterAcceptedInput()
        }
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
                    firstVisibleLineId = history?.firstVisibleLineId,
                    visibleTextSha256 = tailFirstTextSha256(visibleText),
                    transcriptTopRow = history?.topRow ?: 0,
                    transcriptPageCount = history?.pageCount ?: 0,
                    transcriptRowCount = history?.rowCount ?: 0,
                    displayBounds = displayBounds,
                    terminalBounds = terminalBounds,
                    bottomBarBounds = dockBounds,
                    imeBounds = imeBounds,
                    imeVisible = imeVisible,
                )
            }

            override fun scrollHistoryToLine(lineId: Long): Boolean =
                overlayVisible && transcriptViewport.scrollToLineId(lineId)

            override fun openHistory(): Boolean {
                if (overlayVisible) return false
                activeEngine.scrollPastTopBoundary()
                return true
            }

            override fun closeHistory() = returnToLiveTail()

            override fun showIme() {
                if (overlayVisible) returnToLiveTail()
                liveViewHolder[0]?.let(::requestTailFirstTerminalIme)
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
    LaunchedEffect(
        transcriptState?.viewportGeneration,
        transcriptState?.loading,
        transcriptState?.error,
        transcriptState?.pages?.isEmpty(),
        transcriptState?.hasMoreBefore,
    ) {
        if (shouldDismissEmptyTranscriptOverlay(transcriptState)) {
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
            key(
                activeEngine,
                terminalPreferences.fontScheme,
                terminalPreferences.customFontSha256,
                terminalPreferences.fontSizeSp,
                terminalPreferences.lineSpacingMultiplier,
            ) {
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
                            terminalPreferences.fontScheme,
                            terminalPreferences.customFontSha256,
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

/**
 * Focus ownership is committed asynchronously by ViewRootImpl. Calling
 * showSoftInput in the same stack as requestFocus can therefore be rejected as
 * "view is not served" after a detached transcript overlay closes. Post the
 * request through the UI queue and use both modern insets and compatibility
 * IME paths; the short retries are idempotent and remain debug-E2E-only.
 */
private fun requestTailFirstTerminalIme(view: View) {
    view.isFocusable = true
    view.isFocusableInTouchMode = true
    view.requestFocus()
    val inputMethod = view.context.getSystemService(android.content.Context.INPUT_METHOD_SERVICE)
        as? InputMethodManager
    listOf(0L, 100L, 300L).forEach { delayMillis ->
        view.postDelayed(
            {
                if (!view.isAttachedToWindow) return@postDelayed
                if (!view.hasFocus()) view.requestFocus()
                ViewCompat.getWindowInsetsController(view)?.show(WindowInsetsCompat.Type.ime())
                inputMethod?.showSoftInput(view, InputMethodManager.SHOW_IMPLICIT)
            },
            delayMillis,
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
    historyEngine: (java.util.UUID) -> TerminalViewportEngine?,
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
    engine: TerminalViewportEngine?,
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
    engine: TerminalViewportEngine?,
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
    key(
        activeEngine,
        terminalPreferences.fontScheme,
        terminalPreferences.customFontSha256,
        terminalPreferences.fontSizeSp,
        terminalPreferences.lineSpacingMultiplier,
    ) {
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
    keepScreenOn: Boolean,
    onKeepScreenOnChanged: (Boolean) -> Unit,
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
        title = stringResource(R.string.settings_screen_title),
        subtitle = stringResource(R.string.settings_screen_copy),
        icon = Icons.Filled.LightMode,
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
                .clickable { onKeepScreenOnChanged(!keepScreenOn) }
                .padding(14.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                Text(
                    stringResource(R.string.settings_keep_screen_on),
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    stringResource(R.string.settings_keep_screen_on_copy),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Switch(
                checked = keepScreenOn,
                onCheckedChange = onKeepScreenOnChanged,
            )
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
                Text(
                    stringResource(R.string.update_error_detail),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error,
                )
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
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val customFontAvailable = TerminalFontStore.hasCustomFont(context)
    val fontImporter = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            scope.launch {
                try {
                    val imported = withContext(Dispatchers.IO) { TerminalFontStore.importFont(context, uri) }
                    onPreferencesChanged(
                        value.copy(
                            fontScheme = TerminalFontScheme.CUSTOM,
                            customFontDisplayName = imported.displayName,
                            customFontSha256 = imported.sha256,
                        ),
                    )
                    Toast.makeText(
                        context,
                        context.getString(R.string.settings_terminal_font_imported, imported.displayName),
                        Toast.LENGTH_SHORT,
                    ).show()
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (error: TerminalFontImportException) {
                    val message = when (error.failure) {
                        TerminalFontImportFailure.EMPTY -> R.string.settings_terminal_font_error_empty
                        TerminalFontImportFailure.TOO_LARGE -> R.string.settings_terminal_font_error_too_large
                        TerminalFontImportFailure.UNSUPPORTED_FORMAT ->
                            R.string.settings_terminal_font_error_unsupported
                        TerminalFontImportFailure.INVALID_FONT -> R.string.settings_terminal_font_error_invalid
                        TerminalFontImportFailure.UNREADABLE -> R.string.settings_terminal_font_error_unreadable
                    }
                    Toast.makeText(context, message, Toast.LENGTH_LONG).show()
                } catch (_: Exception) {
                    Toast.makeText(
                        context,
                        R.string.settings_terminal_font_error_unreadable,
                        Toast.LENGTH_LONG,
                    ).show()
                }
            }
        }
    }
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(
                stringResource(R.string.settings_terminal_font_scheme),
                style = MaterialTheme.typography.titleSmall,
            )
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                TerminalFontScheme.entries.forEach { scheme ->
                    FilterChip(
                        selected = value.fontScheme == scheme,
                        onClick = {
                            if (scheme == TerminalFontScheme.CUSTOM && !customFontAvailable) {
                                fontImporter.launch(TerminalFontStore.acceptedMimeTypes)
                            } else {
                                onPreferencesChanged(value.copy(fontScheme = scheme))
                            }
                        },
                        label = { Text(scheme.displayLabel()) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    )
                }
            }
            Text(
                when (value.fontScheme) {
                    TerminalFontScheme.CUSTOM -> stringResource(
                        R.string.settings_terminal_font_active_custom,
                        value.customFontDisplayName ?: stringResource(R.string.settings_terminal_font_custom),
                    )
                    else -> stringResource(
                        R.string.settings_terminal_font_active,
                        value.fontScheme.displayLabel(),
                    )
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                OutlinedButton(
                    onClick = { fontImporter.launch(TerminalFontStore.acceptedMimeTypes) },
                    modifier = Modifier.weight(1f).heightIn(min = 48.dp),
                ) {
                    Text(
                        stringResource(
                            if (customFontAvailable) R.string.settings_terminal_font_replace
                            else R.string.settings_terminal_font_import,
                        ),
                    )
                }
                if (customFontAvailable) {
                    TextButton(
                        onClick = {
                            if (TerminalFontStore.removeCustomFont(context)) {
                                onPreferencesChanged(
                                    value.copy(
                                        fontScheme = TerminalFontScheme.BUNDLED_NERD_FONT,
                                        customFontDisplayName = null,
                                        customFontSha256 = null,
                                    ),
                                )
                            } else {
                                Toast.makeText(
                                    context,
                                    R.string.settings_terminal_font_remove_failed,
                                    Toast.LENGTH_LONG,
                                ).show()
                            }
                        },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Text(stringResource(R.string.settings_terminal_font_remove))
                    }
                }
            }
            Text(
                stringResource(R.string.settings_terminal_font_local_copy),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            key(value.fontScheme, value.customFontSha256) {
                AndroidView(
                    factory = { previewContext ->
                        TextView(previewContext).apply {
                            textSize = 16f
                            setPadding(16, 14, 16, 14)
                        }
                    },
                    update = { preview ->
                        preview.text = context.getString(R.string.settings_terminal_font_preview)
                        preview.typeface = TerminalTypefaceResolver.resolve(
                            context,
                            terminalFontFamilyHint(value.fontScheme),
                        )
                        preview.setTextColor(value.foregroundArgb)
                        preview.setBackgroundColor(value.backgroundArgb)
                    },
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(8.dp)),
                )
            }
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
    var managerOpen by rememberSaveable { mutableStateOf(false) }
    var shortcutEditorOpen by rememberSaveable { mutableStateOf(false) }
    var editingShortcutId by rememberSaveable { mutableStateOf<String?>(null) }
    var editorRowName by rememberSaveable { mutableStateOf(TerminalShortcutDockRow.TOP.name) }

    fun closeEditor() {
        shortcutEditorOpen = false
        editingShortcutId = null
    }

    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Row(
            modifier = Modifier.padding(14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(
                    stringResource(
                        R.string.settings_shortcuts_summary,
                        TerminalAction.entries.size,
                        value.customShortcuts.size,
                        stringResource(
                            if (value.dockLayout == TerminalShortcutDockLayout.ONE_ROW) {
                                R.string.shortcut_dock_layout_one_row
                            } else {
                                R.string.shortcut_dock_layout_two_rows
                            },
                        ),
                    ),
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    stringResource(R.string.settings_shortcuts_manager_copy),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            OutlinedButton(
                onClick = { managerOpen = true },
                modifier = Modifier.heightIn(min = 48.dp),
            ) {
                Text(stringResource(R.string.shortcut_manage_action))
            }
        }
    }
    if (managerOpen && !shortcutEditorOpen) {
        TerminalShortcutManagerDialog(
            preferences = value,
            onDismiss = { managerOpen = false },
            onLayoutChanged = { layout ->
                onPreferencesChanged(value.copy(dockLayout = layout).normalized)
            },
            onAdd = {
                editingShortcutId = null
                val rows = orderedTerminalCustomShortcutRows(value)
                editorRowName = if (rows[TERMINAL_SHORTCUT_TOP_ROW].size <= rows[TERMINAL_SHORTCUT_BOTTOM_ROW].size) {
                    TerminalShortcutDockRow.TOP.name
                } else {
                    TerminalShortcutDockRow.BOTTOM.name
                }
                shortcutEditorOpen = true
            },
            onEdit = { shortcut, row ->
                editingShortcutId = shortcut.id
                editorRowName = row.name
                shortcutEditorOpen = true
            },
            onMove = { shortcut, delta ->
                onPreferencesChanged(moveTerminalCustomShortcut(value, shortcut.id, delta))
            },
            onMoveToRow = { shortcut, row ->
                onPreferencesChanged(placeTerminalCustomShortcut(value, shortcut.id, row))
            },
        )
    }
    if (shortcutEditorOpen) {
        val editingShortcut = editingShortcutId?.let { id ->
            value.customShortcuts.firstOrNull { it.id == id }
        }
        TerminalShortcutEditorDialog(
            initialShortcut = editingShortcut,
            initialRow = TerminalShortcutDockRow.valueOf(editorRowName),
            showDockRow = value.dockLayout == TerminalShortcutDockLayout.TWO_ROWS,
            onDismiss = ::closeEditor,
            onConfirm = { label, type, shortcutValue, row ->
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
                        placeTerminalCustomShortcut(
                            value.copy(customShortcuts = nextShortcuts),
                            shortcut.id,
                            row,
                        ),
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
private fun TerminalShortcutManagerDialog(
    preferences: TerminalShortcutPreferences,
    onDismiss: () -> Unit,
    onLayoutChanged: (TerminalShortcutDockLayout) -> Unit,
    onAdd: () -> Unit,
    onEdit: (TerminalCustomShortcut, TerminalShortcutDockRow) -> Unit,
    onMove: (TerminalCustomShortcut, Int) -> Unit,
    onMoveToRow: (TerminalCustomShortcut, TerminalShortcutDockRow) -> Unit,
) {
    val value = preferences.normalized
    val customRows = orderedTerminalCustomShortcutRows(value)
    val singleRowShortcuts = orderedTerminalCustomShortcuts(value)
    var selectedShortcutId by rememberSaveable { mutableStateOf<String?>(null) }
    val selectedRowIndex = customRows.indexOfFirst { row -> row.any { it.id == selectedShortcutId } }
    val selectedRow = TerminalShortcutDockRow.entries.getOrNull(selectedRowIndex)
    val activeSelectedSequence = if (value.dockLayout == TerminalShortcutDockLayout.ONE_ROW) {
        singleRowShortcuts
    } else {
        customRows.getOrNull(selectedRowIndex).orEmpty()
    }
    val selectedPosition = activeSelectedSequence.indexOfFirst { it.id == selectedShortcutId }
    val selectedShortcut = activeSelectedSequence.getOrNull(selectedPosition)
    val managerGroups: List<Pair<TerminalShortcutDockRow?, List<TerminalCustomShortcut>>> =
        if (value.dockLayout == TerminalShortcutDockLayout.ONE_ROW) {
            listOf(null to singleRowShortcuts)
        } else {
            TerminalShortcutDockRow.entries.map { row -> row to customRows[row.index] }
        }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.shortcut_manage_title)) },
        text = {
            LazyColumn(
                modifier = Modifier.fillMaxWidth().heightIn(max = 560.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                item(key = "dock-layout-selector") {
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(
                            stringResource(R.string.shortcut_dock_layout_label),
                            style = MaterialTheme.typography.titleSmall,
                            fontWeight = FontWeight.SemiBold,
                        )
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            TerminalShortcutDockLayout.entries.forEach { layout ->
                                FilterChip(
                                    selected = value.dockLayout == layout,
                                    onClick = {
                                        selectedShortcutId = null
                                        onLayoutChanged(layout)
                                    },
                                    label = {
                                        Text(
                                            stringResource(
                                                if (layout == TerminalShortcutDockLayout.ONE_ROW) {
                                                    R.string.shortcut_dock_layout_one_row
                                                } else {
                                                    R.string.shortcut_dock_layout_two_rows
                                                },
                                            ),
                                        )
                                    },
                                    modifier = Modifier.weight(1f),
                                )
                            }
                        }
                    }
                }
                item {
                    Text(
                        stringResource(R.string.shortcut_layout_preview_title),
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold,
                    )
                }
                item(key = "dock-layout-preview") {
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        TerminalShortcutDockLayoutPreview(
                            preferences = value,
                            selectedShortcutId = selectedShortcutId,
                            onShortcutLongPressed = { shortcut -> selectedShortcutId = shortcut.id },
                        )
                        if (selectedShortcut != null) {
                            TerminalShortcutPreviewControls(
                                shortcut = selectedShortcut,
                                layout = value.dockLayout,
                                row = selectedRow,
                                canMoveLeft = selectedPosition > 0,
                                canMoveRight = selectedPosition < activeSelectedSequence.lastIndex,
                                onMoveLeft = { onMove(selectedShortcut, -1) },
                                onMoveRight = { onMove(selectedShortcut, 1) },
                                onMoveUp = {
                                    onMoveToRow(selectedShortcut, TerminalShortcutDockRow.TOP)
                                },
                                onMoveDown = {
                                    onMoveToRow(selectedShortcut, TerminalShortcutDockRow.BOTTOM)
                                },
                            )
                        }
                    }
                }
                item {
                    Text(
                        stringResource(
                            if (value.dockLayout == TerminalShortcutDockLayout.ONE_ROW) {
                                R.string.shortcut_layout_preview_copy_one_row
                            } else {
                                R.string.shortcut_layout_preview_copy
                            },
                        ),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    HorizontalDivider(
                        modifier = Modifier.padding(top = 10.dp),
                        color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f),
                    )
                }
                managerGroups.forEach { (row, shortcuts) ->
                    val rowKey = row?.name ?: "single"
                    item(key = "custom-heading-$rowKey") {
                        Text(
                            if (row == null) {
                                stringResource(R.string.shortcut_custom_single_row_title, shortcuts.size)
                            } else {
                                stringResource(
                                    R.string.shortcut_custom_row_title,
                                    stringResource(
                                        if (row == TerminalShortcutDockRow.TOP) R.string.shortcut_row_top
                                        else R.string.shortcut_row_bottom,
                                    ),
                                    shortcuts.size,
                                )
                            },
                            style = MaterialTheme.typography.titleSmall,
                            fontWeight = FontWeight.SemiBold,
                        )
                    }
                    if (shortcuts.isEmpty()) {
                        item(key = "custom-empty-$rowKey") {
                            Text(
                                stringResource(
                                    if (row == null) R.string.shortcut_custom_empty
                                    else R.string.shortcut_row_empty,
                                ),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    } else {
                        itemsIndexed(shortcuts, key = { _, shortcut -> shortcut.id }) { index, shortcut ->
                            val storedRowIndex = customRows.indexOfFirst { storedRow ->
                                storedRow.any { it.id == shortcut.id }
                            }
                            val storedRow = TerminalShortcutDockRow.entries.getOrElse(storedRowIndex) {
                                TerminalShortcutDockRow.TOP
                            }
                            TerminalShortcutManagerRow(
                                shortcut = shortcut,
                                row = storedRow,
                                allowRowChange = row != null,
                                canMoveLeft = index > 0,
                                canMoveRight = index < shortcuts.lastIndex,
                                onEdit = { onEdit(shortcut, storedRow) },
                                onMoveLeft = { onMove(shortcut, -1) },
                                onMoveRight = { onMove(shortcut, 1) },
                                onMoveToOtherRow = {
                                    onMoveToRow(
                                        shortcut,
                                        if (storedRow == TerminalShortcutDockRow.TOP) TerminalShortcutDockRow.BOTTOM
                                        else TerminalShortcutDockRow.TOP,
                                    )
                                },
                            )
                        }
                    }
                    item(key = "custom-divider-$rowKey") {
                        HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.24f))
                    }
                }
            }
        },
        confirmButton = {
            Button(onClick = onDismiss) { Text(stringResource(R.string.action_close)) }
        },
        dismissButton = {
            TextButton(
                onClick = onAdd,
                enabled = value.customShortcuts.size < MAX_CUSTOM_TERMINAL_SHORTCUTS,
            ) {
                Text(stringResource(R.string.shortcut_add_custom))
            }
        },
    )
}

@Composable
private fun TerminalShortcutManagerRow(
    shortcut: TerminalCustomShortcut,
    row: TerminalShortcutDockRow,
    allowRowChange: Boolean,
    canMoveLeft: Boolean,
    canMoveRight: Boolean,
    onEdit: () -> Unit,
    onMoveLeft: () -> Unit,
    onMoveRight: () -> Unit,
    onMoveToOtherRow: () -> Unit,
) {
    var menuExpanded by remember { mutableStateOf(false) }
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                shortcut.displayLabel,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.labelLarge,
            )
            Text(
                stringResource(terminalCustomShortcutTypeLabelResource(shortcut.type)),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.primary,
            )
            Text(
                shortcut.value,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Box {
            IconButton(onClick = { menuExpanded = true }) {
                Icon(Icons.Filled.MoreVert, contentDescription = stringResource(R.string.action_more_options))
            }
            DropdownMenu(expanded = menuExpanded, onDismissRequest = { menuExpanded = false }) {
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.shortcut_edit_custom)) },
                    onClick = {
                        menuExpanded = false
                        onEdit()
                    },
                )
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.shortcut_move_left)) },
                    enabled = canMoveLeft,
                    onClick = {
                        menuExpanded = false
                        onMoveLeft()
                    },
                )
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.shortcut_move_right)) },
                    enabled = canMoveRight,
                    onClick = {
                        menuExpanded = false
                        onMoveRight()
                    },
                )
                if (allowRowChange) {
                    DropdownMenuItem(
                        text = {
                            Text(
                                stringResource(
                                    if (row == TerminalShortcutDockRow.TOP) R.string.shortcut_move_to_bottom
                                    else R.string.shortcut_move_to_top,
                                ),
                            )
                        },
                        onClick = {
                            menuExpanded = false
                            onMoveToOtherRow()
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun TerminalShortcutDockLayoutPreview(
    preferences: TerminalShortcutPreferences,
    selectedShortcutId: String?,
    onShortcutLongPressed: (TerminalCustomShortcut) -> Unit,
) {
    val value = preferences.normalized
    val displayRows = if (value.dockLayout == TerminalShortcutDockLayout.ONE_ROW) {
        listOf(orderedTerminalCustomShortcuts(value))
    } else {
        orderedTerminalCustomShortcutRows(value)
    }
    val rowLabels = if (value.dockLayout == TerminalShortcutDockLayout.ONE_ROW) {
        listOf(R.string.shortcut_row_single)
    } else {
        listOf(R.string.shortcut_row_top, R.string.shortcut_row_bottom)
    }
    val customColumnCount = displayRows.maxOfOrNull(List<TerminalCustomShortcut>::size) ?: 0
    val scrollState = rememberScrollState()
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
            rowLabels.forEach { label ->
                Box(
                    modifier = Modifier.height(TerminalDockButtonSize),
                    contentAlignment = Alignment.CenterStart,
                ) {
                    Text(
                        stringResource(label),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.primary,
                    )
                }
            }
        }
        Row(
            modifier = Modifier.weight(1f).horizontalScroll(scrollState),
            horizontalArrangement = Arrangement.spacedBy(3.dp),
            verticalAlignment = Alignment.Top,
        ) {
            repeat(customColumnCount.coerceAtLeast(1)) { column ->
                Column(
                    modifier = Modifier
                        .width(IntrinsicSize.Max)
                        .widthIn(min = TerminalDockButtonSize),
                    verticalArrangement = Arrangement.spacedBy(3.dp),
                ) {
                    displayRows.indices.forEach { row ->
                        val shortcut = displayRows[row].getOrNull(column)
                        if (shortcut == null) {
                            if (customColumnCount == 0) {
                                TerminalShortcutPreviewButton(
                                    label = stringResource(R.string.shortcut_row_empty),
                                    modifier = Modifier.fillMaxWidth(),
                                )
                            } else {
                                Spacer(Modifier.fillMaxWidth().height(TerminalDockButtonSize))
                            }
                        } else {
                            TerminalShortcutPreviewButton(
                                label = shortcut.displayLabel,
                                selected = shortcut.id == selectedShortcutId,
                                onLongClick = { onShortcutLongPressed(shortcut) },
                                modifier = Modifier.fillMaxWidth(),
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun TerminalShortcutPreviewButton(
    label: String,
    modifier: Modifier = Modifier,
    selected: Boolean = false,
    onLongClick: (() -> Unit)? = null,
) {
    Surface(
        modifier = modifier
            .height(TerminalDockButtonSize)
            .widthIn(min = TerminalDockButtonSize, max = 120.dp)
            .then(
                if (onLongClick == null) {
                    Modifier
                } else {
                    Modifier.combinedClickable(
                        onClick = {},
                        onLongClickLabel = stringResource(R.string.shortcut_long_press_reorder),
                        onLongClick = onLongClick,
                    )
                },
            ),
        shape = RoundedCornerShape(5.dp),
        color = MaterialTheme.colorScheme.primaryContainer,
        border = BorderStroke(
            if (selected) 2.dp else 1.dp,
            if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline.copy(alpha = 0.5f),
        ),
    ) {
        Box(contentAlignment = Alignment.Center) {
            Text(
                label,
                modifier = Modifier.padding(horizontal = TerminalDockHorizontalContentPadding),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.labelSmall,
            )
        }
    }
}

@Composable
private fun TerminalShortcutPreviewControls(
    shortcut: TerminalCustomShortcut,
    layout: TerminalShortcutDockLayout,
    row: TerminalShortcutDockRow?,
    canMoveLeft: Boolean,
    canMoveRight: Boolean,
    onMoveLeft: () -> Unit,
    onMoveRight: () -> Unit,
    onMoveUp: () -> Unit,
    onMoveDown: () -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(6.dp),
        color = MaterialTheme.colorScheme.surfaceVariant,
    ) {
        Column(
            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            Text(
                stringResource(R.string.shortcut_reorder_selected, shortcut.displayLabel),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.labelMedium,
            )
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceEvenly,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(onClick = onMoveLeft, enabled = canMoveLeft) {
                    Icon(
                        Icons.AutoMirrored.Filled.KeyboardArrowLeft,
                        contentDescription = stringResource(R.string.shortcut_move_left),
                    )
                }
                if (layout == TerminalShortcutDockLayout.TWO_ROWS) {
                    IconButton(onClick = onMoveUp, enabled = row == TerminalShortcutDockRow.BOTTOM) {
                        Icon(
                            Icons.Filled.KeyboardArrowUp,
                            contentDescription = stringResource(R.string.shortcut_move_to_top),
                        )
                    }
                    IconButton(onClick = onMoveDown, enabled = row == TerminalShortcutDockRow.TOP) {
                        Icon(
                            Icons.Filled.KeyboardArrowDown,
                            contentDescription = stringResource(R.string.shortcut_move_to_bottom),
                        )
                    }
                }
                IconButton(onClick = onMoveRight, enabled = canMoveRight) {
                    Icon(
                        Icons.AutoMirrored.Filled.KeyboardArrowRight,
                        contentDescription = stringResource(R.string.shortcut_move_right),
                    )
                }
            }
        }
    }
}

@Composable
private fun TerminalShortcutEditorDialog(
    initialShortcut: TerminalCustomShortcut? = null,
    initialRow: TerminalShortcutDockRow,
    showDockRow: Boolean,
    onDismiss: () -> Unit,
    onConfirm: (String, TerminalCustomShortcutType, String, TerminalShortcutDockRow) -> Unit,
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
    var dockRowName by rememberSaveable(initialShortcut?.id) { mutableStateOf(initialRow.name) }
    var specialKeyPickerOpen by rememberSaveable(initialShortcut?.id) { mutableStateOf(false) }
    val shortcutType = TerminalCustomShortcutType.valueOf(shortcutTypeName)
    val dockRow = TerminalShortcutDockRow.valueOf(dockRowName)
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
    if (specialKeyPickerOpen) {
        TerminalSpecialKeyPickerDialog(
            initialCombination = parsed,
            onDismiss = { specialKeyPickerOpen = false },
            onConfirm = { combination ->
                keyCombination = combination.canonical
                specialKeyPickerOpen = false
            },
        )
        return
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
                if (showDockRow) {
                    Text(stringResource(R.string.shortcut_dock_row_label), style = MaterialTheme.typography.labelLarge)
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        TerminalShortcutDockRow.entries.forEach { row ->
                            FilterChip(
                                selected = dockRow == row,
                                onClick = { dockRowName = row.name },
                                label = {
                                    Text(
                                        stringResource(
                                            if (row == TerminalShortcutDockRow.TOP) R.string.shortcut_row_top
                                            else R.string.shortcut_row_bottom,
                                        ),
                                    )
                                },
                                modifier = Modifier.weight(1f),
                            )
                        }
                    }
                }
                when (shortcutType) {
                    TerminalCustomShortcutType.KEY_COMBINATION -> {
                        OutlinedTextField(
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
                        OutlinedButton(
                            onClick = {
                                keyboardController?.hide()
                                specialKeyPickerOpen = true
                            },
                            modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
                        ) {
                            Text(stringResource(R.string.shortcut_choose_special_key))
                        }
                    }
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
                        dockRow,
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

private data class TerminalSpecialKeyOption(
    val key: String,
    val label: String = key,
)

private val terminalNavigationSpecialKeys = listOf(
    TerminalSpecialKeyOption("Home"),
    TerminalSpecialKeyOption("End"),
    TerminalSpecialKeyOption("PageUp", "Page Up"),
    TerminalSpecialKeyOption("PageDown", "Page Down"),
    TerminalSpecialKeyOption("Left", "←"),
    TerminalSpecialKeyOption("Up", "↑"),
    TerminalSpecialKeyOption("Down", "↓"),
    TerminalSpecialKeyOption("Right", "→"),
)

private val terminalEditingSpecialKeys = listOf(
    TerminalSpecialKeyOption("Esc"),
    TerminalSpecialKeyOption("Tab"),
    TerminalSpecialKeyOption("Enter"),
    TerminalSpecialKeyOption("Backspace"),
    TerminalSpecialKeyOption("Delete"),
    TerminalSpecialKeyOption("Insert"),
    TerminalSpecialKeyOption("Space"),
)

private val terminalFunctionSpecialKeys = (1..12).map { number -> TerminalSpecialKeyOption("F$number") }
private val allTerminalSpecialKeyNames = (
    terminalNavigationSpecialKeys + terminalEditingSpecialKeys + terminalFunctionSpecialKeys
).mapTo(linkedSetOf(), TerminalSpecialKeyOption::key)

@Composable
private fun TerminalSpecialKeyPickerDialog(
    initialCombination: TerminalKeyCombination?,
    onDismiss: () -> Unit,
    onConfirm: (TerminalKeyCombination) -> Unit,
) {
    var control by rememberSaveable { mutableStateOf(initialCombination?.control == true) }
    var alt by rememberSaveable { mutableStateOf(initialCombination?.alt == true) }
    var shift by rememberSaveable { mutableStateOf(initialCombination?.shift == true) }
    var selectedKey by rememberSaveable {
        mutableStateOf(initialCombination?.key?.takeIf(allTerminalSpecialKeyNames::contains))
    }
    val selection = selectedKey?.let { key -> TerminalKeyCombination(key, control, alt, shift) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.shortcut_special_key_title)) },
        text = {
            Column(
                modifier = Modifier.fillMaxWidth().heightIn(max = 560.dp).verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(stringResource(R.string.shortcut_special_modifiers), style = MaterialTheme.typography.labelLarge)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    listOf(
                        Triple("Ctrl", control) { selected: Boolean -> control = selected },
                        Triple("Alt", alt) { selected: Boolean -> alt = selected },
                        Triple("Shift", shift) { selected: Boolean -> shift = selected },
                    ).forEach { (label, selected, onSelected) ->
                        FilterChip(
                            selected = selected,
                            onClick = { onSelected(!selected) },
                            label = { Text(label) },
                            modifier = Modifier.weight(1f),
                        )
                    }
                }
                TerminalSpecialKeySection(
                    title = stringResource(R.string.shortcut_special_navigation),
                    options = terminalNavigationSpecialKeys,
                    columns = 2,
                    selectedKey = selectedKey,
                    onSelected = { selectedKey = it },
                )
                TerminalSpecialKeySection(
                    title = stringResource(R.string.shortcut_special_editing),
                    options = terminalEditingSpecialKeys,
                    columns = 2,
                    selectedKey = selectedKey,
                    onSelected = { selectedKey = it },
                )
                TerminalSpecialKeySection(
                    title = stringResource(R.string.shortcut_special_function),
                    options = terminalFunctionSpecialKeys,
                    columns = 4,
                    selectedKey = selectedKey,
                    onSelected = { selectedKey = it },
                )
                Text(
                    stringResource(
                        R.string.shortcut_special_current,
                        selection?.canonical ?: stringResource(R.string.shortcut_special_none),
                    ),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.primary,
                )
            }
        },
        confirmButton = {
            Button(
                onClick = { selection?.let(onConfirm) },
                enabled = selection != null,
            ) {
                Text(stringResource(R.string.shortcut_special_confirm))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.action_cancel)) }
        },
    )
}

@Composable
private fun TerminalSpecialKeySection(
    title: String,
    options: List<TerminalSpecialKeyOption>,
    columns: Int,
    selectedKey: String?,
    onSelected: (String) -> Unit,
) {
    Text(title, style = MaterialTheme.typography.labelLarge)
    options.chunked(columns).forEach { rowOptions ->
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            rowOptions.forEach { option ->
                FilterChip(
                    selected = selectedKey == option.key,
                    onClick = { onSelected(option.key) },
                    label = {
                        Text(option.label, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    },
                    modifier = Modifier.weight(1f),
                )
            }
            repeat(columns - rowOptions.size) { Spacer(Modifier.weight(1f)) }
        }
    }
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
    error is IllegalArgumentException -> fallback
    else -> fallback
}

@Composable
private fun TerminalFontScheme.displayLabel(): String = when (this) {
    TerminalFontScheme.BUNDLED_NERD_FONT -> stringResource(R.string.settings_terminal_font_bundled)
    TerminalFontScheme.SYSTEM_MONOSPACE -> stringResource(R.string.settings_terminal_font_system)
    TerminalFontScheme.CUSTOM -> stringResource(R.string.settings_terminal_font_custom)
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
        fontFamilyHint = terminalFontFamilyHint(normalized.fontScheme),
    )
}

private fun terminalFontFamilyHint(scheme: TerminalFontScheme): String = when (scheme) {
    TerminalFontScheme.BUNDLED_NERD_FONT -> TerminalFontFamilyHintV1.BUNDLED_NERD_FONT
    TerminalFontScheme.SYSTEM_MONOSPACE -> TerminalFontFamilyHintV1.SYSTEM_MONOSPACE
    TerminalFontScheme.CUSTOM -> TerminalFontFamilyHintV1.CUSTOM_APP_FONT
}

/** A small compatibility wrapper keeps the app free of a lifecycle-compose dependency. */
@Composable
private fun <T> kotlinx.coroutines.flow.StateFlow<T>.collectAsStateCompat(): androidx.compose.runtime.State<T> =
    collectAsState()
