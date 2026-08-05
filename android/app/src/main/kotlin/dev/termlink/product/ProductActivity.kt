package dev.termlink.product

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.lifecycleScope
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
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
import androidx.compose.material.icons.filled.Block
import androidx.compose.material.icons.filled.CardMembership
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.LaptopWindows
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.ShoppingCart
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
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
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
import androidx.compose.runtime.key
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.harness.R
import dev.termlink.model.SessionSummary
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.ArgbColor
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.termux.TermuxTerminalEngine
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale
import kotlinx.coroutines.launch
import androidx.core.view.WindowCompat

private sealed class DestructiveProductAction {
    data class RevokeDevice(val deviceId: java.util.UUID, val reject: Boolean) : DestructiveProductAction()
    data class ChangeServer(val value: String) : DestructiveProductAction()
    data object SignOut : DestructiveProductAction()
}

private val ProductShape = RoundedCornerShape(8.dp)
private val ProductBackground = Color(0xFF0B0F14)
private val ProductSurface = Color(0xFF121820)
private val ProductSurfaceVariant = Color(0xFF1B2430)
private val ProductPrimary = Color(0xFF9CB2FF)
private val ProductPositive = Color(0xFF65D6A1)
private val ProductWarning = Color(0xFFF1C96B)
private val ProductDanger = Color(0xFFFF8C91)
private val ProductMuted = Color(0xFFAAB5C5)
private val ProductTerminalBackground = Color(0xFF080B10)

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
    private val authorizationLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        repository.handleAuthorizationResult(result.data)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        repository = ProductionProductRepository(this, authorizationLauncher::launch)
        enableEdgeToEdge()
        setContent {
            val context = LocalContext.current
            var themePreference by rememberSaveable { mutableStateOf(loadThemePreference(context)) }
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
                    themePreference = themePreference,
                    onThemePreferenceChanged = {
                        themePreference = it
                        saveThemePreference(context, it)
                    },
                )
            }
        }
    }

    override fun onStop() {
        // Backgrounding must give up every remote control lease, but it must
        // not terminate the Windows shell or the read-only viewing stream.
        if (::repository.isInitialized) {
            lifecycleScope.launch {
                runCatching { repository.releaseAllControlLeases() }
            }
        }
        super.onStop()
    }

    override fun onDestroy() {
        repository.close()
        super.onDestroy()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProductApp(
    repository: ProductRepository,
    themePreference: ProductThemePreference = ProductThemePreference.SYSTEM,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit = {},
) {
    val snapshot by repository.snapshot.collectAsStateCompat()
    var destinationName by rememberSaveable { mutableStateOf(ProductDestination.DEVICES.name) }
    val destination = ProductDestination.valueOf(destinationName)
    var deviceFilter by rememberSaveable { mutableStateOf(DeviceFilter.ALL) }
    var selectedSessionId by rememberSaveable { mutableStateOf<String?>(null) }
    var selectedHistoryId by rememberSaveable { mutableStateOf<String?>(null) }
    var pasteSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    var configureServer by remember { mutableStateOf(false) }
    var completePairing by remember { mutableStateOf(false) }
    var commandSearchOpen by remember { mutableStateOf(false) }
    var pendingDestructiveAction by remember { mutableStateOf<DestructiveProductAction?>(null) }
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }
    val unavailableOperationMessage = stringResource(R.string.unknown_server_operation)

    fun navigateTo(next: ProductDestination) {
        destinationName = next.name
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
                snackbarHostState.showSnackbar(safeOperationMessage(error, unavailableOperationMessage))
            } finally {
                onComplete()
            }
        }
    }

    fun runOperation(operation: suspend () -> Unit) = runOperation(operation, {})

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
                onRegister = { runOperation(repository::beginBrowserRegistration) },
                onAccountSetup = { runOperation(repository::beginBrowserAccountSetup) },
                onConfigure = { configureServer = true },
                modifier = Modifier.padding(innerPadding),
            )
        }
        if (configureServer) {
            ServerConfigurationDialog(
                initialValue = snapshot.serverUrl.orEmpty(),
                onDismiss = { configureServer = false },
                onConfirm = { value ->
                    configureServer = false
                    val normalized = runCatching { ServerUrlPolicy.normalize(value) }.getOrNull()
                    if (snapshot.signedIn && normalized != null && normalized != snapshot.serverUrl) {
                        pendingDestructiveAction = DestructiveProductAction.ChangeServer(value)
                    } else {
                        runOperation { repository.configureServer(value) }
                    }
                },
            )
        }
        return
    }

    BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
        val wide = maxWidth >= 700.dp
        Scaffold(
            topBar = {
                if (destination.primaryDestination().destination() != destination) {
                    ProductTopBar(
                        destination = destination,
                        onBack = { navigateTo(destination.primaryDestination().destination()) },
                        onRefresh = { runOperation(repository::refresh) },
                    )
                }
            },
            bottomBar = {
                if (!wide) {
                    ProductBottomNavigation(destination.primaryDestination(), onDestinationSelected = ::navigateToPrimary)
                }
            },
            snackbarHost = { SnackbarHost(snackbarHostState) },
            containerColor = MaterialTheme.colorScheme.background,
        ) { innerPadding ->
            Row(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(innerPadding),
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
                    snapshot = snapshot,
                    wide = wide,
                    deviceFilter = deviceFilter,
                    onDeviceFilterChanged = { deviceFilter = it },
                    selectedSessionId = selectedSessionId,
                    onSessionSelected = { selectedSessionId = it.toString() },
                    onOpenSession = { runOperation { repository.openSession(it) } },
                    terminalEngine = repository::terminalEngine,
                    historyEngine = repository::historyEngine,
                    selectedHistoryId = selectedHistoryId,
                    onHistorySelected = { selectedHistoryId = it.historyObject.id.toString() },
                    onOpenSessions = { navigateTo(ProductDestination.SESSIONS) },
                    onOpenHistory = { navigateTo(ProductDestination.HISTORY) },
                    onOpenMembership = { navigateTo(ProductDestination.MEMBERSHIP) },
                    onOpenTrustedDevices = { navigateTo(ProductDestination.TRUSTED_DEVICES) },
                    onOpenAccount = { navigateTo(ProductDestination.ACCOUNT) },
                    onOpenCommandSearch = { commandSearchOpen = true },
                    themePreference = themePreference,
                    onThemePreferenceChanged = onThemePreferenceChanged,
                    onConfigure = { configureServer = true },
                    onCompletePairing = { completePairing = true },
                    onPurchase = { runOperation { repository.purchaseProduct(it) } },
                    onRestorePurchases = { runOperation { repository.restorePurchases() } },
                    onSignIn = { runOperation(repository::signIn) },
                    onSignOut = { pendingDestructiveAction = DestructiveProductAction.SignOut },
                    onApprove = { runOperation { repository.approveDevice(it) } },
                    onRevoke = { deviceId ->
                        val device = snapshot.devices.firstOrNull { it.id == deviceId }
                        pendingDestructiveAction = DestructiveProductAction.RevokeDevice(
                            deviceId = deviceId,
                            reject = device?.trustState == DeviceTrustState.PENDING,
                        )
                    },
                    onRequestControl = { runOperation { repository.requestControl(it) } },
                    onReleaseControl = { runOperation { repository.releaseControl(it) } },
                    onPaste = { pasteSessionId = it },
                    onTerminalAction = { sessionId, action ->
                        runOperation { repository.sendTerminalAction(sessionId, action) }
                    },
                    onReplay = { runOperation { repository.replayHistory(it) } },
                )
            }
        }
    }

    pasteSessionId?.let { sessionId ->
        PasteConfirmationDialog(
            onDismiss = { pasteSessionId = null },
            onConfirm = { text ->
                pasteSessionId = null
                runOperation { repository.sendPaste(sessionId, text) }
            },
        )
    }
    if (configureServer) {
        ServerConfigurationDialog(
            initialValue = snapshot.serverUrl.orEmpty(),
            onDismiss = { configureServer = false },
            onConfirm = { value ->
                configureServer = false
                val normalized = runCatching { ServerUrlPolicy.normalize(value) }.getOrNull()
                if (snapshot.signedIn && normalized != null && normalized != snapshot.serverUrl) {
                    pendingDestructiveAction = DestructiveProductAction.ChangeServer(value)
                } else {
                    runOperation { repository.configureServer(value) }
                }
            },
        )
    }
    if (completePairing) {
        PairingCompletionDialog(
            onDismiss = { completePairing = false },
            onConfirm = { challengeId, code, nonce ->
                completePairing = false
                runOperation { repository.completePairing(challengeId, code, nonce) }
            },
        )
    }
    if (commandSearchOpen) {
        CommandSearchDialog(
            onDismiss = { commandSearchOpen = false },
            onNavigate = { next ->
                commandSearchOpen = false
                navigateTo(next)
            },
        )
    }
    if (snapshot.enrollmentConfirmationRequired && snapshot.enrollmentState == ProductEnrollmentState.PENDING_CONFIRMATION) {
        EnrollmentConfirmationDialog(
            onConfirm = { runOperation(repository::confirmEnrollment) },
            onCancel = { runOperation(repository::cancelEnrollment) },
        )
    }
    pendingDestructiveAction?.let { action ->
        val title: String
        val message: String
        val confirmLabel: String
        when (action) {
            is DestructiveProductAction.RevokeDevice -> {
                title = stringResource(if (action.reject) R.string.confirm_reject_title else R.string.confirm_revoke_title)
                message = if (action.reject) {
                    stringResource(R.string.confirm_reject_copy)
                } else {
                    stringResource(R.string.confirm_revoke_copy)
                }
                confirmLabel = stringResource(if (action.reject) R.string.confirm_reject else R.string.confirm_revoke)
            }
            is DestructiveProductAction.ChangeServer -> {
                title = stringResource(R.string.confirm_change_server_title)
                message = stringResource(R.string.confirm_change_server_copy)
                confirmLabel = stringResource(R.string.confirm_change_server)
            }
            DestructiveProductAction.SignOut -> {
                title = stringResource(R.string.confirm_sign_out_title)
                message = stringResource(R.string.confirm_sign_out_copy)
                confirmLabel = stringResource(R.string.confirm_sign_out)
            }
        }
        ConfirmActionDialog(
            title = title,
            message = message,
            confirmLabel = confirmLabel,
            icon = Icons.Filled.Warning,
            onDismiss = { pendingDestructiveAction = null },
            onConfirm = {
                pendingDestructiveAction = null
                when (action) {
                    is DestructiveProductAction.RevokeDevice -> runOperation { repository.revokeDevice(action.deviceId) }
                    is DestructiveProductAction.ChangeServer -> runOperation { repository.configureServer(action.value) }
                    DestructiveProductAction.SignOut -> runOperation(repository::signOut)
                }
            },
        )
    }
}

@Composable
private fun ProductSignInPage(
    snapshot: ProductSnapshot,
    onSignIn: (String, CharArray, () -> Unit) -> Unit,
    onRegister: () -> Unit,
    onAccountSetup: () -> Unit,
    onConfigure: () -> Unit,
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
                        snapshot.serverUrl ?: stringResource(R.string.sign_in_server_unavailable),
                        style = MaterialTheme.typography.bodySmall,
                        fontFamily = FontFamily.Monospace,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                Column(horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(1.dp)) {
                    Text(stringResource(R.string.status_https_endpoint), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.tertiary)
                    TextButton(onClick = onConfigure, contentPadding = ButtonDefaults.TextButtonContentPadding, modifier = Modifier.heightIn(min = 48.dp)) {
                        Text(stringResource(R.string.action_change_server))
                    }
                }
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.45f))

            if (snapshot.connectionState == ProductConnectionState.ERROR) {
                Text(
                    if (snapshot.authenticationState == ProductAuthenticationState.BROWSER_SETUP_REQUIRED) {
                        stringResource(R.string.sign_in_account_setup_required)
                    } else {
                        snapshot.connectionError ?: stringResource(R.string.connection_error_copy)
                    },
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
                enabled = snapshot.configured && !submitting && username.isNotBlank() && password.isNotEmpty(),
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
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                OutlinedButton(
                    onClick = { password = ""; onRegister() },
                    enabled = snapshot.configured && !submitting,
                    modifier = Modifier.weight(1f).heightIn(min = 48.dp),
                ) { Text(stringResource(R.string.sign_in_register)) }
                OutlinedButton(
                    onClick = { password = ""; onAccountSetup() },
                    enabled = snapshot.configured && !submitting,
                    modifier = Modifier.weight(1f).heightIn(min = 48.dp),
                ) { Text(stringResource(R.string.sign_in_account_setup)) }
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
                    Text(stringResource(R.string.sign_in_secure_browser), style = MaterialTheme.typography.titleSmall)
                    Text(
                        stringResource(R.string.sign_in_secure_browser_copy),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

@Composable
private fun ServerConfigurationDialog(
    initialValue: String,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit,
) {
    var value by rememberSaveable(initialValue) { mutableStateOf(initialValue) }
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Filled.Settings, contentDescription = null) },
        title = { Text(stringResource(R.string.server_dialog_title)) },
        text = {
            OutlinedTextField(
                value = value,
                onValueChange = { value = it },
                label = { Text(stringResource(R.string.server_url_label)) },
                placeholder = { Text(stringResource(R.string.server_url_hint)) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            )
        },
        confirmButton = {
            Button(onClick = { onConfirm(value) }, enabled = value.isNotBlank(), modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.action_save)) }
        },
        dismissButton = { TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.action_cancel)) } },
    )
}

@Composable
private fun EnrollmentConfirmationDialog(
    onConfirm: () -> Unit,
    onCancel: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onCancel,
        icon = { Icon(Icons.Filled.PhoneAndroid, contentDescription = null) },
        title = { Text(stringResource(R.string.enrollment_confirm_title)) },
        text = {
            Text(stringResource(R.string.enrollment_confirm_copy))
        },
        confirmButton = {
            Button(onClick = onConfirm) { Text(stringResource(R.string.enrollment_confirm_action)) }
        },
        dismissButton = {
            TextButton(onClick = onCancel) { Text(stringResource(R.string.action_cancel)) }
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
    onBack: () -> Unit,
    onRefresh: () -> Unit,
) {
    CenterAlignedTopAppBar(
        title = { Text(destination.localizedLabel(), style = MaterialTheme.typography.titleLarge) },
        navigationIcon = {
            IconButton(onClick = onBack) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.action_back))
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
    ProductPrimaryDestination.ACTIVITY -> Icons.Filled.Timeline
    ProductPrimaryDestination.SETTINGS -> Icons.Filled.Settings
}

@Composable
private fun ProductPrimaryDestination.localizedLabel(): String = when (this) {
    ProductPrimaryDestination.DEVICES -> stringResource(R.string.nav_devices)
    ProductPrimaryDestination.ACTIVITY -> stringResource(R.string.nav_activity)
    ProductPrimaryDestination.SETTINGS -> stringResource(R.string.nav_settings)
}

@Composable
private fun ProductDestination.localizedLabel(): String = when (this) {
    ProductDestination.DEVICES -> stringResource(R.string.nav_devices)
    ProductDestination.SESSIONS -> stringResource(R.string.nav_sessions)
    ProductDestination.ACTIVITY -> stringResource(R.string.nav_activity)
    ProductDestination.HISTORY -> stringResource(R.string.nav_history)
    ProductDestination.MEMBERSHIP -> stringResource(R.string.nav_membership)
    ProductDestination.TRUSTED_DEVICES -> stringResource(R.string.nav_trusted_devices)
    ProductDestination.ACCOUNT -> stringResource(R.string.nav_account)
    ProductDestination.SETTINGS -> stringResource(R.string.nav_settings)
}

@Composable
private fun DeviceTrustState.localizedLabel(): String = when (this) {
    DeviceTrustState.TRUSTED -> stringResource(R.string.trust_trusted)
    DeviceTrustState.PENDING -> stringResource(R.string.trust_pending)
    DeviceTrustState.REVOKED -> stringResource(R.string.trust_revoked)
}

@Composable
private fun DeviceType.localizedLabel(): String = when (this) {
    DeviceType.WINDOWS_PC -> stringResource(R.string.device_type_windows_pc)
    DeviceType.ANDROID -> stringResource(R.string.device_type_android)
}

@Composable
private fun DeviceFilter.localizedLabel(): String = when (this) {
    DeviceFilter.ALL -> stringResource(R.string.filter_all)
    DeviceFilter.TRUSTED -> stringResource(R.string.filter_trusted)
    DeviceFilter.PENDING -> stringResource(R.string.filter_pending)
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
private fun dev.termlink.billing.BillingPhase.localizedLabel(): String = when (this) {
    dev.termlink.billing.BillingPhase.IDLE -> stringResource(R.string.billing_idle)
    dev.termlink.billing.BillingPhase.CONNECTING -> stringResource(R.string.billing_connecting)
    dev.termlink.billing.BillingPhase.READY -> stringResource(R.string.billing_ready)
    dev.termlink.billing.BillingPhase.PURCHASING -> stringResource(R.string.billing_purchasing)
    dev.termlink.billing.BillingPhase.VERIFYING -> stringResource(R.string.billing_verifying)
    dev.termlink.billing.BillingPhase.RESTORING -> stringResource(R.string.billing_restoring)
    dev.termlink.billing.BillingPhase.PENDING -> stringResource(R.string.billing_pending)
    dev.termlink.billing.BillingPhase.CANCELED -> stringResource(R.string.billing_canceled)
    dev.termlink.billing.BillingPhase.DISCONNECTED -> stringResource(R.string.billing_disconnected)
    dev.termlink.billing.BillingPhase.ERROR -> stringResource(R.string.billing_error)
}

@Composable
private fun ProductContent(
    modifier: Modifier,
    destination: ProductDestination,
    snapshot: ProductSnapshot,
    wide: Boolean,
    deviceFilter: DeviceFilter,
    onDeviceFilterChanged: (DeviceFilter) -> Unit,
    selectedSessionId: String?,
    onSessionSelected: (java.util.UUID) -> Unit,
    onOpenSession: (java.util.UUID) -> Unit,
    terminalEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    historyEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    selectedHistoryId: String?,
    onHistorySelected: (HistoryEntry) -> Unit,
    onOpenSessions: () -> Unit,
    onOpenHistory: () -> Unit,
    onOpenMembership: () -> Unit,
    onOpenTrustedDevices: () -> Unit,
    onOpenAccount: () -> Unit,
    onOpenCommandSearch: () -> Unit,
    themePreference: ProductThemePreference,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit,
    onConfigure: () -> Unit,
    onCompletePairing: () -> Unit,
    onPurchase: (String) -> Unit,
    onRestorePurchases: () -> Unit,
    onSignIn: () -> Unit,
    onSignOut: () -> Unit,
    onApprove: (java.util.UUID) -> Unit,
    onRevoke: (java.util.UUID) -> Unit,
    onRequestControl: (java.util.UUID) -> Unit,
    onReleaseControl: (java.util.UUID) -> Unit,
    onPaste: (java.util.UUID) -> Unit,
    onTerminalAction: (java.util.UUID, TerminalAction) -> Unit,
    onReplay: (HistoryEntry) -> Unit,
) {
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
                filter = deviceFilter,
                onFilterChanged = onDeviceFilterChanged,
                onConfigure = onConfigure,
                onCompletePairing = onCompletePairing,
                onApprove = onApprove,
                onRevoke = onRevoke,
                onOpenSessions = onOpenSessions,
                onOpenAccount = onOpenAccount,
                onOpenCommandSearch = onOpenCommandSearch,
            )
            ProductDestination.SESSIONS -> SessionsPage(
                snapshot = snapshot,
                wide = wide,
                selectedSessionId = selectedSessionId,
                onSessionSelected = onSessionSelected,
                onOpenSession = onOpenSession,
                terminalEngine = terminalEngine,
                onConfigure = onConfigure,
                onRequestControl = onRequestControl,
                onReleaseControl = onReleaseControl,
                onPaste = onPaste,
                onTerminalAction = onTerminalAction,
            )
            ProductDestination.ACTIVITY -> ActivityPage(
                snapshot = snapshot,
                onConfigure = onConfigure,
                onOpenHistory = onOpenHistory,
            )
            ProductDestination.HISTORY -> HistoryPage(
                snapshot = snapshot,
                selectedHistoryId = selectedHistoryId,
                onHistorySelected = onHistorySelected,
                onConfigure = onConfigure,
                onReplay = onReplay,
                historyEngine = historyEngine,
            )
            ProductDestination.MEMBERSHIP -> MembershipPage(
                snapshot = snapshot,
                onConfigure = onConfigure,
                onPurchase = onPurchase,
                onRestorePurchases = onRestorePurchases,
            )
            ProductDestination.TRUSTED_DEVICES -> TrustedDevicesPage(
                snapshot = snapshot,
                onApprove = onApprove,
                onRevoke = onRevoke,
                onCompletePairing = onCompletePairing,
                onConfigure = onConfigure,
            )
            ProductDestination.ACCOUNT -> AccountPage(snapshot = snapshot, onSignOut = onSignOut, onConfigure = onConfigure)
            ProductDestination.SETTINGS -> SettingsPage(
                snapshot = snapshot,
                onConfigure = onConfigure,
                onSignIn = onSignIn,
                onSignOut = onSignOut,
                onOpenMembership = onOpenMembership,
                onOpenTrustedDevices = onOpenTrustedDevices,
                onOpenAccount = onOpenAccount,
                themePreference = themePreference,
                onThemePreferenceChanged = onThemePreferenceChanged,
            )
        }
    }
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
private fun HomeQuotaSummary(snapshot: ProductSnapshot) {
    val entitlement = snapshot.entitlement ?: return
    val plan = snapshot.catalogPlan?.displayName ?: entitlement.planKey
    Text(
        stringResource(
            R.string.home_quota_summary,
            plan,
            quotaValue(snapshot.pcs.size, entitlement.maxRemotePcs),
            quotaValue(snapshot.sessions.count { it.state == SessionState.RUNNING }, entitlement.maxLiveSessions),
        ),
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}

@Composable
private fun quotaValue(used: Int, limit: Int): String =
    if (limit > 0) "$used/$limit" else stringResource(R.string.quota_unavailable)

@Composable
private fun DevicesPage(
    snapshot: ProductSnapshot,
    filter: DeviceFilter,
    onFilterChanged: (DeviceFilter) -> Unit,
    onConfigure: () -> Unit,
    onCompletePairing: () -> Unit,
    onApprove: (java.util.UUID) -> Unit,
    onRevoke: (java.util.UUID) -> Unit,
    onOpenSessions: () -> Unit,
    onOpenAccount: () -> Unit,
    onOpenCommandSearch: () -> Unit,
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
    HomeQuotaSummary(snapshot)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    if (snapshot.enrollmentState == ProductEnrollmentState.PENDING_CONFIRMATION) {
        PairingPendingCard(onCompletePairing = onCompletePairing, selfEnrollment = true)
    }
    snapshot.pairingChallenge?.let { challenge ->
        PairingChallengeCard(challenge)
    }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        DeviceFilter.entries.forEach { item ->
            FilterChip(
                selected = filter == item,
                onClick = { onFilterChanged(item) },
                label = { Text(item.localizedLabel()) },
                leadingIcon = if (filter == item) {
                    { Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) }
                } else null,
                modifier = Modifier.heightIn(min = 48.dp),
            )
        }
    }
    Text(stringResource(R.string.nav_devices), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
    val devices = snapshot.devices.filter { device ->
        when (filter) {
            DeviceFilter.ALL -> true
            DeviceFilter.TRUSTED -> device.trustState == DeviceTrustState.TRUSTED
            DeviceFilter.PENDING -> device.trustState == DeviceTrustState.PENDING
        }
    }
    // A pending self-device has no authority to approve itself. Until a trusted
    // manager/PC is present, show a truthful waiting state instead of a doomed action.
    val canApprovePending = snapshot.canApprovePendingDevice()
    if (devices.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Shield,
            title = if (snapshot.signedIn) stringResource(R.string.empty_no_devices) else stringResource(R.string.empty_sign_in_devices),
            detail = if (snapshot.signedIn) {
                stringResource(R.string.server_devices_empty_copy)
            } else {
                stringResource(R.string.server_device_trust_copy)
            },
            actionLabel = if (!snapshot.configured) stringResource(R.string.action_configure_server) else null,
            onAction = onConfigure,
        )
    } else {
        devices.forEach { device ->
            DeviceRow(
                device = device,
                canApprove = canApprovePending,
                onApprove = onApprove,
                onRevoke = onRevoke,
            )
        }
    }
    if (snapshot.pcs.isNotEmpty()) {
        PageHeader(title = stringResource(R.string.pc_routing_title), subtitle = stringResource(R.string.pc_routing_subtitle), icon = Icons.Filled.LaptopWindows)
        snapshot.pcs.forEach { pc -> PcRow(pc) }
        OutlinedButton(onClick = onOpenSessions, modifier = Modifier.heightIn(min = 48.dp)) {
            Icon(Icons.Filled.Terminal, contentDescription = null, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(6.dp))
            Text(stringResource(R.string.action_view_sessions))
        }
    }
}

@Composable
private fun PairingPendingCard(onCompletePairing: () -> Unit, selfEnrollment: Boolean = false) {
    val warning = MaterialTheme.colorScheme.tertiary
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = warning.copy(alpha = 0.10f),
        border = BorderStroke(1.dp, warning.copy(alpha = 0.55f)),
    ) {
        Row(
            modifier = Modifier.padding(14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Icon(Icons.Filled.Lock, contentDescription = stringResource(R.string.pairing_pending_accessibility), tint = warning)
            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(stringResource(R.string.status_device_approval_pending), style = MaterialTheme.typography.titleSmall)
                Text(
                    stringResource(if (selfEnrollment) R.string.status_self_enrollment_pending_copy else R.string.status_pending_copy),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            OutlinedButton(onClick = onCompletePairing, modifier = Modifier.heightIn(min = 48.dp)) {
                Icon(Icons.AutoMirrored.Filled.Login, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.action_complete_pairing))
            }
        }
    }
}

@Composable
private fun PairingChallengeCard(challenge: PairingChallengeState) {
    val positive = MaterialTheme.colorScheme.secondary
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = positive.copy(alpha = 0.08f),
        border = BorderStroke(1.dp, positive.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(Icons.Filled.Shield, contentDescription = stringResource(R.string.pairing_challenge_accessibility), tint = positive)
                Text(stringResource(R.string.status_pairing_challenge), style = MaterialTheme.typography.titleSmall)
            }
            Text(
                stringResource(R.string.status_pairing_ready, challenge.targetName ?: stringResource(R.string.unknown_target)),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(stringResource(R.string.status_pairing_code, challenge.code.chunked(4).joinToString("-")), fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.titleMedium)
            Text(
                stringResource(R.string.status_pairing_expiry, challenge.expiresAt ?: stringResource(R.string.time_soon)),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.tertiary,
            )
        }
    }
}

@Composable
private fun PairingCompletionDialog(
    onDismiss: () -> Unit,
    onConfirm: (challengeId: String, code: String, nonce: String) -> Unit,
) {
    var challengeId by rememberSaveable { mutableStateOf("") }
    var code by rememberSaveable { mutableStateOf("") }
    var nonce by rememberSaveable { mutableStateOf("") }
    var showAdvanced by rememberSaveable { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Filled.Shield, contentDescription = null) },
        title = { Text(stringResource(R.string.pairing_dialog_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    stringResource(R.string.pairing_dialog_copy),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                OutlinedTextField(
                    value = code,
                    onValueChange = { code = it },
                    label = { Text(stringResource(R.string.pairing_code_label)) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
                )
                TextButton(onClick = { showAdvanced = !showAdvanced }, modifier = Modifier.heightIn(min = 48.dp)) {
                    Text(stringResource(if (showAdvanced) R.string.pairing_advanced_hide else R.string.pairing_advanced_show))
                }
                if (showAdvanced) {
                    OutlinedTextField(
                        value = challengeId,
                        onValueChange = { challengeId = it },
                        label = { Text(stringResource(R.string.pairing_id_label)) },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    )
                    OutlinedTextField(
                        value = nonce,
                        onValueChange = { nonce = it },
                        label = { Text(stringResource(R.string.pairing_proof_label)) },
                        minLines = 2,
                        maxLines = 3,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
                    )
                }
            }
        },
        confirmButton = {
            Button(
                onClick = { onConfirm(challengeId, code, nonce) },
                enabled = challengeId.isNotBlank() && code.isNotBlank() && nonce.isNotBlank() && showAdvanced,
                modifier = Modifier.heightIn(min = 48.dp),
            ) { Text(stringResource(R.string.action_complete)) }
        },
        dismissButton = { TextButton(onClick = onDismiss, modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.action_cancel)) } },
    )
}

@Composable
private fun DeviceRow(
    device: DeviceSummary,
    canApprove: Boolean,
    onApprove: (java.util.UUID) -> Unit,
    onRevoke: (java.util.UUID) -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                val displayName = if (device.name.isBlank()) stringResource(R.string.unknown_device) else device.name
                Icon(
                    imageVector = if (device.type == DeviceType.WINDOWS_PC) Icons.Filled.LaptopWindows else Icons.Filled.PhoneAndroid,
                    contentDescription = device.type.name,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(26.dp),
                )
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(displayName, style = MaterialTheme.typography.titleMedium)
                    Text(
                        device.type.localizedLabel(),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                TrustPill(device.trustState)
            }
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                when (device.trustState) {
                    DeviceTrustState.PENDING -> {
                        if (canApprove) {
                            Button(onClick = { onApprove(device.id) }, contentPadding = ButtonDefaults.ContentPadding, modifier = Modifier.heightIn(min = 48.dp)) {
                                Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp))
                                Spacer(Modifier.width(6.dp))
                                Text(stringResource(R.string.action_approve))
                            }
                        } else {
                            Text(
                                stringResource(R.string.status_waiting_manager),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.tertiary,
                            )
                        }
                        OutlinedButton(onClick = { onRevoke(device.id) }, modifier = Modifier.heightIn(min = 48.dp)) {
                            Icon(Icons.Filled.Block, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text(stringResource(R.string.action_reject))
                        }
                    }
                    DeviceTrustState.TRUSTED -> OutlinedButton(onClick = { onRevoke(device.id) }, modifier = Modifier.heightIn(min = 48.dp)) {
                        Icon(Icons.Filled.Block, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_revoke_trust))
                    }
                    DeviceTrustState.REVOKED -> Text(
                        stringResource(R.string.status_revoked_server),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        }
    }
}

@Composable
private fun TrustPill(state: DeviceTrustState) {
    val color = when (state) {
        DeviceTrustState.TRUSTED -> MaterialTheme.colorScheme.secondary
        DeviceTrustState.PENDING -> MaterialTheme.colorScheme.tertiary
        DeviceTrustState.REVOKED -> MaterialTheme.colorScheme.error
    }
    Text(
        state.localizedLabel(),
        modifier = Modifier
            .border(BorderStroke(1.dp, color.copy(alpha = 0.7f)), RoundedCornerShape(50))
            .padding(horizontal = 9.dp, vertical = 5.dp),
        style = MaterialTheme.typography.labelSmall,
        color = color,
    )
}

@Composable
private fun PcRow(pc: PcSummary) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .border(BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.4f)), ProductShape)
            .padding(12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(Icons.Filled.LaptopWindows, contentDescription = stringResource(R.string.windows_pc_accessibility), tint = MaterialTheme.colorScheme.primary)
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(pc.name, style = MaterialTheme.typography.titleSmall)
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
        Icon(
            if (pc.online) Icons.Filled.Wifi else Icons.Filled.WifiOff,
            contentDescription = if (pc.online) stringResource(R.string.pc_online_accessibility) else stringResource(R.string.pc_offline_accessibility),
            tint = if (pc.online) MaterialTheme.colorScheme.secondary else MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun SessionsPage(
    snapshot: ProductSnapshot,
    wide: Boolean,
    selectedSessionId: String?,
    onSessionSelected: (java.util.UUID) -> Unit,
    onOpenSession: (java.util.UUID) -> Unit,
    terminalEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    onConfigure: () -> Unit,
    onRequestControl: (java.util.UUID) -> Unit,
    onReleaseControl: (java.util.UUID) -> Unit,
    onPaste: (java.util.UUID) -> Unit,
    onTerminalAction: (java.util.UUID, TerminalAction) -> Unit,
) {
    PageHeader(
        title = stringResource(R.string.nav_sessions),
        subtitle = stringResource(R.string.page_sessions_subtitle),
        icon = Icons.Filled.Terminal,
    )
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    if (snapshot.sessions.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Terminal,
            title = if (snapshot.signedIn) stringResource(R.string.empty_no_sessions) else stringResource(R.string.empty_sign_in_sessions),
            detail = stringResource(R.string.server_sessions_empty_copy),
            actionLabel = if (!snapshot.configured) stringResource(R.string.action_configure_server) else null,
            onAction = onConfigure,
        )
        return
    }
    val selected = snapshot.sessions.firstOrNull { it.id.toString() == selectedSessionId }
        ?: snapshot.sessions.first()
    LaunchedEffect(selected.id) { onOpenSession(selected.id) }
    val selectedState = snapshot.sessionStates[selected.id]
    val selectedEngine = terminalEngine(selected.id)
    val unknownPcLabel = stringResource(R.string.unknown_pc)
    fun pcLabel(session: SessionSummary): String =
        snapshot.pcs.firstOrNull { it.id == session.pcId }?.name
            ?: unknownPcLabel
    if (wide) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalAlignment = Alignment.Top,
        ) {
            SessionList(
                sessions = snapshot.sessions,
                selectedId = selected.id,
                onSessionSelected = onSessionSelected,
                pcLabel = ::pcLabel,
                modifier = Modifier.widthIn(min = 260.dp, max = 360.dp),
            )
            SessionDetail(
                session = selected,
                pcLabel = pcLabel(selected),
                onRequestControl = onRequestControl,
                onReleaseControl = onReleaseControl,
                onPaste = onPaste,
                onTerminalAction = onTerminalAction,
                remoteState = selectedState,
                engine = selectedEngine,
                modifier = Modifier.weight(1f),
            )
        }
    } else {
        SessionList(
            sessions = snapshot.sessions,
            selectedId = selected.id,
            onSessionSelected = onSessionSelected,
            pcLabel = ::pcLabel,
            modifier = Modifier.fillMaxWidth(),
        )
        SessionDetail(
            session = selected,
            pcLabel = pcLabel(selected),
            onRequestControl = onRequestControl,
            onReleaseControl = onReleaseControl,
            onPaste = onPaste,
            onTerminalAction = onTerminalAction,
            remoteState = selectedState,
            engine = selectedEngine,
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

@Composable
private fun SessionList(
    sessions: List<SessionSummary>,
    selectedId: java.util.UUID,
    onSessionSelected: (java.util.UUID) -> Unit,
    pcLabel: (SessionSummary) -> String,
    modifier: Modifier,
) {
    Surface(
        modifier = modifier,
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column {
            Text(stringResource(R.string.sessions_all), modifier = Modifier.padding(14.dp), style = MaterialTheme.typography.titleSmall)
            sessions.forEachIndexed { index, session ->
                if (index > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
                SessionListRow(
                    session = session,
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
            Text(session.shellType, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
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

@Composable
private fun SessionDetail(
    session: SessionSummary,
    pcLabel: String,
    onRequestControl: (java.util.UUID) -> Unit,
    onReleaseControl: (java.util.UUID) -> Unit,
    onPaste: (java.util.UUID) -> Unit,
    onTerminalAction: (java.util.UUID, TerminalAction) -> Unit,
    remoteState: RemoteSessionState?,
    engine: TermuxTerminalEngine?,
    modifier: Modifier,
) {
    Surface(
        modifier = modifier,
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(session.shellType, style = MaterialTheme.typography.titleMedium)
                    Text(
                        stringResource(
                            R.string.session_detail_summary,
                            pcLabel,
                            session.state.localizedLabel(),
                            session.columns,
                            session.rows,
                        ),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                SessionStatePill(session.state)
            }
            TerminalSurface(session = session, engine = engine, remoteState = remoteState)
            TerminalActionBar(
                enabled = remoteState is RemoteSessionState.Controlling,
                onAction = { onTerminalAction(session.id, it) },
            )
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .heightIn(min = 52.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Button(
                    onClick = { onRequestControl(session.id) },
                    enabled = session.state == SessionState.RUNNING && remoteState !is RemoteSessionState.Controlling,
                    modifier = Modifier.heightIn(min = 48.dp),
                ) {
                    Icon(Icons.Filled.Lock, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.action_request_control))
                }
                OutlinedButton(
                    onClick = { onReleaseControl(session.id) },
                    enabled = remoteState is RemoteSessionState.Controlling,
                    modifier = Modifier.heightIn(min = 48.dp),
                ) {
                    Icon(Icons.Filled.Lock, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.action_release))
                }
                OutlinedButton(
                    onClick = { onPaste(session.id) },
                    enabled = remoteState is RemoteSessionState.Controlling,
                    modifier = Modifier.heightIn(min = 48.dp),
                ) {
                    Icon(Icons.Filled.ContentPaste, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.action_paste))
                }
            }
        }
    }
}

@Composable
private fun TerminalActionBar(
    enabled: Boolean,
    onAction: (TerminalAction) -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .heightIn(min = 52.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        TerminalTextKey(stringResource(R.string.terminal_key_escape), TerminalAction.ESCAPE, enabled, onAction)
        TerminalTextKey(stringResource(R.string.terminal_key_tab), TerminalAction.TAB, enabled, onAction)
        TerminalTextKey(stringResource(R.string.terminal_key_enter), TerminalAction.ENTER, enabled, onAction)
        TerminalIconKey(Icons.AutoMirrored.Filled.KeyboardArrowLeft, stringResource(R.string.arrow_left), TerminalAction.ARROW_LEFT, enabled, onAction)
        TerminalIconKey(Icons.Filled.KeyboardArrowUp, stringResource(R.string.arrow_up), TerminalAction.ARROW_UP, enabled, onAction)
        TerminalIconKey(Icons.Filled.KeyboardArrowDown, stringResource(R.string.arrow_down), TerminalAction.ARROW_DOWN, enabled, onAction)
        TerminalIconKey(Icons.AutoMirrored.Filled.KeyboardArrowRight, stringResource(R.string.arrow_right), TerminalAction.ARROW_RIGHT, enabled, onAction)
        TerminalTextKey(stringResource(R.string.terminal_key_ctrl_c), TerminalAction.CTRL_C, enabled, onAction)
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
        modifier = Modifier.heightIn(min = 48.dp),
        contentPadding = ButtonDefaults.ContentPadding,
    ) {
        Text(label, maxLines = 1, style = MaterialTheme.typography.labelMedium)
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
        modifier = Modifier.size(48.dp),
    ) {
        Icon(icon, contentDescription = description)
    }
}

@Composable
private fun SessionStatePill(state: SessionState) {
    val color = when (state) {
        SessionState.RUNNING -> MaterialTheme.colorScheme.secondary
        SessionState.INTERRUPTED -> MaterialTheme.colorScheme.tertiary
        SessionState.EXITED -> MaterialTheme.colorScheme.onSurfaceVariant
    }
    Text(
        state.localizedLabel(),
        modifier = Modifier
            .border(BorderStroke(1.dp, color.copy(alpha = 0.7f)), RoundedCornerShape(50))
            .padding(horizontal = 9.dp, vertical = 5.dp),
        style = MaterialTheme.typography.labelSmall,
        color = color,
    )
}

@Composable
private fun TerminalSurface(
    session: SessionSummary,
    engine: TermuxTerminalEngine?,
    remoteState: RemoteSessionState?,
) {
    val placeholder = remember(session.id, session.columns, session.rows) {
        TermuxTerminalEngine(
            initialSize = TerminalSize(session.columns.coerceAtLeast(1), session.rows.coerceAtLeast(1)),
        ).also {
            it.applyTheme(productTerminalTheme)
            it.setReadOnly(true)
        }
    }
    val activeEngine = engine ?: placeholder
    SideEffect {
        activeEngine.applyTheme(productTerminalTheme)
    }
    DisposableEffect(placeholder) {
        onDispose { placeholder.dispose() }
    }
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .height(38.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Text(stringResource(R.string.terminal_live), style = MaterialTheme.typography.labelLarge)
            Text(
                terminalStateLabel(remoteState),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.tertiary,
            )
        }
        key(activeEngine) {
            AndroidView(
                factory = { context -> activeEngine.createView(context) },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(260.dp)
                    .clip(ProductShape)
                    .background(ProductTerminalBackground),
            )
        }
    }
}

@Composable
private fun terminalStateLabel(state: RemoteSessionState?): String = when (state) {
    is RemoteSessionState.Controlling -> stringResource(R.string.terminal_controlling)
    is RemoteSessionState.Live -> stringResource(R.string.terminal_live_state)
    is RemoteSessionState.Reconnecting -> stringResource(R.string.terminal_reconnecting)
    is RemoteSessionState.ReadOnly -> stringResource(R.string.terminal_read_only)
    is RemoteSessionState.HistoryGap -> stringResource(R.string.terminal_history_gap)
    is RemoteSessionState.Exited -> stringResource(R.string.terminal_exited)
    null -> stringResource(R.string.terminal_connecting)
}

@Composable
private fun ActivityPage(
    snapshot: ProductSnapshot,
    onConfigure: () -> Unit,
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
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    if (snapshot.activity.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Timeline,
            title = if (snapshot.signedIn) stringResource(R.string.empty_no_activity) else stringResource(R.string.empty_sign_in_activity),
            detail = stringResource(R.string.activity_device_local_copy),
            actionLabel = if (!snapshot.configured) stringResource(R.string.action_configure_server) else null,
            onAction = onConfigure,
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
    onConfigure: () -> Unit,
    onReplay: (HistoryEntry) -> Unit,
    historyEngine: (java.util.UUID) -> TermuxTerminalEngine?,
) {
    PageHeader(stringResource(R.string.nav_history), stringResource(R.string.page_history_subtitle), Icons.Filled.History)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    if (snapshot.history.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.History,
            title = if (snapshot.signedIn) stringResource(R.string.empty_no_history) else stringResource(R.string.empty_sign_in_history),
            detail = stringResource(R.string.server_history_empty_copy),
            actionLabel = if (!snapshot.configured) stringResource(R.string.action_configure_server) else null,
            onAction = onConfigure,
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
        onReplay = onReplay,
    )
}

@Composable
private fun HistoryReplaySurface(
    entry: HistoryEntry,
    engine: TermuxTerminalEngine?,
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
            HistoryTerminalSurface(entry = entry, engine = engine)
        }
    }
}

@Composable
private fun HistoryTerminalSurface(entry: HistoryEntry, engine: TermuxTerminalEngine?) {
    val placeholder = remember(entry.historyObject.id) {
        TermuxTerminalEngine().also {
            it.applyTheme(productTerminalTheme)
            it.setReadOnly(true)
        }
    }
    val activeEngine = engine ?: placeholder
    SideEffect {
        activeEngine.applyTheme(productTerminalTheme)
        activeEngine.setReadOnly(true)
    }
    DisposableEffect(placeholder) { onDispose { placeholder.dispose() } }
    key(activeEngine) {
        AndroidView(
            factory = { context -> activeEngine.createView(context) },
            modifier = Modifier
                .fillMaxWidth()
                .height(240.dp)
                .clip(ProductShape)
                .background(ProductTerminalBackground),
        )
    }
}

@Composable
private fun MembershipPage(
    snapshot: ProductSnapshot,
    onConfigure: () -> Unit,
    onPurchase: (String) -> Unit,
    onRestorePurchases: () -> Unit,
) {
    PageHeader(stringResource(R.string.nav_membership), stringResource(R.string.page_membership_subtitle), Icons.Filled.CardMembership)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    GooglePlayBillingPanel(
        snapshot = snapshot,
        onPurchase = onPurchase,
        onRestorePurchases = onRestorePurchases,
    )
    val entitlement = snapshot.entitlement
    val plan = snapshot.catalogPlan
    if (entitlement == null) {
        EmptyState(
            icon = Icons.Filled.CardMembership,
            title = stringResource(R.string.empty_entitlement),
            detail = stringResource(R.string.membership_unknown_limits),
            actionLabel = if (!snapshot.configured) stringResource(R.string.action_configure_server) else null,
            onAction = onConfigure,
        )
        return
    }
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Icon(Icons.Filled.Shield, contentDescription = stringResource(R.string.status_server_entitlement), tint = MaterialTheme.colorScheme.secondary)
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(plan?.displayName ?: entitlement.planKey, style = MaterialTheme.typography.titleMedium)
                    Text(
                        plan?.description ?: stringResource(R.string.catalog_plan_metadata_unavailable),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            Text(
                stringResource(
                    R.string.entitlement_version_summary,
                    entitlement.version,
                    entitlement.catalogVersion,
                ),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            QuotaRow(
                label = stringResource(R.string.quota_remote_pcs),
                used = snapshot.pcs.size,
                limit = entitlement.maxRemotePcs,
            )
            QuotaRow(
                label = stringResource(R.string.quota_live_sessions),
                used = snapshot.sessions.count { it.state == SessionState.RUNNING },
                limit = entitlement.maxLiveSessions,
            )
            Text(
                entitlement.expiresAt?.let {
                    stringResource(
                        R.string.entitlement_effective_until,
                        formatInstant(entitlement.effectiveAt),
                        formatInstant(it),
                    )
                } ?: stringResource(
                    R.string.entitlement_effective_from,
                    formatInstant(entitlement.effectiveAt),
                ),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
    val flags = entitlement.featureFlags.entries.sortedBy { it.key }
    if (flags.isNotEmpty()) {
        PageHeader(stringResource(R.string.membership_features), stringResource(R.string.membership_features_copy), Icons.Filled.CheckCircle)
        flags.forEach { (key, enabled) ->
            Row(
                modifier = Modifier.fillMaxWidth().padding(vertical = 7.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Icon(
                    if (enabled) Icons.Filled.CheckCircle else Icons.Filled.Block,
                    contentDescription = if (enabled) stringResource(R.string.feature_enabled) else stringResource(R.string.feature_disabled),
                    tint = if (enabled) MaterialTheme.colorScheme.secondary else MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(key, modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                Text(if (enabled) stringResource(R.string.feature_enabled) else stringResource(R.string.feature_disabled), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

@Composable
private fun GooglePlayBillingPanel(
    snapshot: ProductSnapshot,
    onPurchase: (String) -> Unit,
    onRestorePurchases: () -> Unit,
) {
    val billing = snapshot.billing
    val busy = billing.phase in setOf(
        dev.termlink.billing.BillingPhase.CONNECTING,
        dev.termlink.billing.BillingPhase.PURCHASING,
        dev.termlink.billing.BillingPhase.VERIFYING,
        dev.termlink.billing.BillingPhase.RESTORING,
    )
    PageHeader(stringResource(R.string.membership_google_play), stringResource(R.string.membership_google_play_copy), Icons.Filled.ShoppingCart)
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            if (billing.products.isEmpty()) {
                Text(
                    billing.message ?: stringResource(R.string.billing_no_products),
                    style = MaterialTheme.typography.bodySmall,
                    color = if (billing.phase == dev.termlink.billing.BillingPhase.ERROR) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            } else {
                billing.products.forEachIndexed { index, product ->
                    if (index > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.35f))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.Top,
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                            Text(product.title, style = MaterialTheme.typography.titleSmall)
                            Text(product.description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            val priceLabel = listOfNotNull(product.formattedPrice, product.billingPeriod).joinToString("  |  ")
                            Text(
                                if (priceLabel.isBlank()) stringResource(R.string.membership_price_unavailable) else priceLabel,
                                style = MaterialTheme.typography.labelLarge,
                                color = MaterialTheme.colorScheme.primary,
                            )
                        }
                        Button(
                            onClick = { onPurchase(product.productId) },
                            enabled = snapshot.signedIn && snapshot.configured && !busy,
                            modifier = Modifier.heightIn(min = 48.dp),
                        ) {
                            Icon(Icons.Filled.ShoppingCart, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text(stringResource(R.string.action_buy))
                        }
                    }
                }
            }
            billing.message?.let { message ->
                Text(message, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.tertiary)
            }
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                OutlinedButton(
                    onClick = onRestorePurchases,
                    enabled = snapshot.signedIn && snapshot.configured && !busy,
                    modifier = Modifier.heightIn(min = 48.dp),
                ) {
                    Icon(Icons.Filled.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.action_restore_purchases))
                }
                Text(
                    billing.phase.localizedLabel(),
                    modifier = Modifier.padding(horizontal = 6.dp, vertical = 11.dp),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun QuotaRow(label: String, used: Int, limit: Int) {
    val ratio = if (limit > 0) (used.toFloat() / limit).coerceIn(0f, 1f) else 0f
    Column(verticalArrangement = Arrangement.spacedBy(5.dp)) {
        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(label, style = MaterialTheme.typography.bodyMedium)
            Text(
                if (limit > 0) "$used / $limit" else "$used / ${stringResource(R.string.quota_unavailable)}",
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.primary,
            )
        }
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(8.dp)
                .clip(RoundedCornerShape(4.dp))
                .background(MaterialTheme.colorScheme.surfaceVariant),
        ) {
            Box(
                modifier = Modifier
                    .fillMaxWidth(ratio)
                    .fillMaxHeight()
                    .background(if (used > limit && limit > 0) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary),
            )
        }
    }
}

@Composable
private fun SettingsPage(
    snapshot: ProductSnapshot,
    onConfigure: () -> Unit,
    onSignIn: () -> Unit,
    onSignOut: () -> Unit,
    onOpenMembership: () -> Unit,
    onOpenTrustedDevices: () -> Unit,
    onOpenAccount: () -> Unit,
    themePreference: ProductThemePreference,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit,
) {
    PageHeader(stringResource(R.string.nav_settings), stringResource(R.string.page_settings_subtitle), Icons.Filled.Settings)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
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
                icon = Icons.Filled.Shield,
                title = stringResource(R.string.settings_device_proof),
                detail = stringResource(R.string.settings_device_proof_copy),
            )
            SettingRow(
                icon = Icons.Filled.Lock,
                title = stringResource(R.string.settings_input_safety),
                detail = stringResource(R.string.terminal_waiting_lease),
            )
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Button(onClick = onConfigure, modifier = Modifier.heightIn(min = 48.dp)) {
                    Icon(Icons.Filled.Settings, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.action_configure_server))
                }
                if (snapshot.signedIn) {
                    OutlinedButton(onClick = onSignOut, modifier = Modifier.heightIn(min = 48.dp)) {
                        Icon(Icons.AutoMirrored.Filled.Logout, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_sign_out))
                    }
                } else {
                    OutlinedButton(onClick = onSignIn, modifier = Modifier.heightIn(min = 48.dp)) {
                        Icon(Icons.AutoMirrored.Filled.Login, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_sign_in))
                    }
                }
            }
        }
    }
    PageHeader(stringResource(R.string.settings_appearance_title), stringResource(R.string.settings_appearance_copy), Icons.Filled.Settings)
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState()),
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
    PageHeader(stringResource(R.string.settings_access_title), stringResource(R.string.settings_access_copy), Icons.Filled.Shield)
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = onOpenTrustedDevices, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
            Icon(Icons.Filled.PhoneAndroid, contentDescription = null, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text(stringResource(R.string.nav_trusted_devices))
        }
        OutlinedButton(onClick = onOpenMembership, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
            Icon(Icons.Filled.CardMembership, contentDescription = null, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text(stringResource(R.string.nav_membership))
        }
        OutlinedButton(onClick = onOpenAccount, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
            Icon(Icons.Filled.CheckCircle, contentDescription = null, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text(stringResource(R.string.nav_account))
        }
    }
    PageHeader(stringResource(R.string.settings_about), stringResource(R.string.settings_about_copy), Icons.Filled.Info)
    Text(stringResource(R.string.settings_product_shell), style = MaterialTheme.typography.bodyMedium)
    Text(stringResource(R.string.settings_transport_copy), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun ProductThemePreference.displayLabel(): String = when (this) {
    ProductThemePreference.SYSTEM -> stringResource(R.string.theme_system)
    ProductThemePreference.LIGHT -> stringResource(R.string.theme_light)
    ProductThemePreference.DARK -> stringResource(R.string.theme_dark)
}

@Composable
private fun TrustedDevicesPage(
    snapshot: ProductSnapshot,
    onApprove: (java.util.UUID) -> Unit,
    onRevoke: (java.util.UUID) -> Unit,
    onCompletePairing: () -> Unit,
    onConfigure: () -> Unit,
) {
    PageHeader(stringResource(R.string.nav_trusted_devices), stringResource(R.string.page_trusted_devices_subtitle), Icons.Filled.Shield)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    snapshot.pairingChallenge?.let { PairingChallengeCard(it) }
    val canApprove = snapshot.canApprovePendingDevice()
    if (snapshot.devices.isEmpty()) {
        val canCompletePairing = snapshot.enrollmentState == ProductEnrollmentState.PENDING_CONFIRMATION
        EmptyState(
            icon = Icons.Filled.Shield,
            title = stringResource(R.string.empty_no_trusted_devices),
            detail = stringResource(R.string.empty_no_trusted_devices_copy),
            actionLabel = if (canCompletePairing) stringResource(R.string.action_complete_pairing) else null,
            onAction = onCompletePairing,
        )
    } else {
        snapshot.devices.forEach { device ->
            DeviceRow(device = device, canApprove = canApprove, onApprove = onApprove, onRevoke = onRevoke)
        }
    }
    if (snapshot.enrollmentState == ProductEnrollmentState.PENDING_CONFIRMATION) {
        PairingPendingCard(onCompletePairing = onCompletePairing, selfEnrollment = true)
    }
}

@Composable
private fun AccountPage(
    snapshot: ProductSnapshot,
    onSignOut: () -> Unit,
    onConfigure: () -> Unit,
) {
    PageHeader(stringResource(R.string.nav_account), stringResource(R.string.page_account_subtitle), Icons.Filled.CheckCircle)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            SettingRow(Icons.Filled.CheckCircle, stringResource(R.string.account_identity), snapshot.accountLabel ?: stringResource(R.string.account_no_identity))
            SettingRow(Icons.Filled.Wifi, stringResource(R.string.account_endpoint), snapshot.serverUrl ?: stringResource(R.string.account_not_configured))
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
private fun ConnectionBanner(snapshot: ProductSnapshot, onConfigure: () -> Unit) {
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
            TextButton(onClick = onConfigure, modifier = Modifier.heightIn(min = 48.dp)) { Text(stringResource(R.string.nav_settings)) }
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
private fun safeOperationMessage(error: Throwable, fallback: String): String =
    if (error is IllegalArgumentException) {
        error.message?.let(::safeActivityDetail)?.takeIf { it.isNotBlank() } ?: fallback
    } else {
        fallback
    }

private val productTerminalTheme = TerminalThemeV1(
    name = "TermLink Product",
    foreground = ArgbColor(0xFFE9EEF5.toInt()),
    background = ArgbColor(0xFF080B10.toInt()),
    cursor = ArgbColor(0xFFF5F7FA.toInt()),
    selectionBackground = ArgbColor(0xFF3E4B70.toInt()),
    selectionForeground = null,
    ansiColors = listOf(
        0xFF161B24, 0xFFFF8C91, 0xFF65D6A1, 0xFFF1C96B,
        0xFF9CB2FF, 0xFFC99DEB, 0xFF6EDDE0, 0xFFE9EEF5,
        0xFF6F7A8B, 0xFFFFA4A8, 0xFF82E3B4, 0xFFFFD984,
        0xFFB9C7FF, 0xFFDDB8F2, 0xFF8DE7E9, 0xFFFFFFFF,
    ).map { ArgbColor(it.toInt()) },
    boldAsBright = true,
    fontFamilyHint = "Cascadia Mono",
)

/** A small compatibility wrapper keeps the app free of a lifecycle-compose dependency. */
@Composable
private fun <T> kotlinx.coroutines.flow.StateFlow<T>.collectAsStateCompat(): androidx.compose.runtime.State<T> =
    collectAsState()
