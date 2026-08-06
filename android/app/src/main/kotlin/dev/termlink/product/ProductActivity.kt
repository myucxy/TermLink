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
    val storedDestination = runCatching { ProductDestination.valueOf(destinationName) }
        .getOrDefault(ProductDestination.DEVICES)
    // A destination saved by an older build must not resurrect hidden product
    // surfaces after an upgrade.
    val destination = storedDestination.takeIf { it.isVisibleInProductShell } ?: ProductDestination.DEVICES
    var selectedSessionId by rememberSaveable { mutableStateOf<String?>(null) }
    var selectedHistoryId by rememberSaveable { mutableStateOf<String?>(null) }
    var pasteSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    var commandSearchOpen by remember { mutableStateOf(false) }
    var signOutConfirmation by remember { mutableStateOf(false) }
    var registrationOpen by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }
    val unavailableOperationMessage = stringResource(R.string.unknown_server_operation)

    fun navigateTo(next: ProductDestination) {
        if (next.isVisibleInProductShell) destinationName = next.name
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
                onRegister = { registrationOpen = true },
                modifier = Modifier.padding(innerPadding),
            )
        }
        if (registrationOpen) {
            RegistrationDialog(
                onDismiss = { registrationOpen = false },
                onSubmit = { username, email, password, confirm ->
                    runOperation(
                        { repository.register(username, email, password, confirm) },
                        onComplete = { registrationOpen = false },
                    )
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
                    selectedSessionId = selectedSessionId,
                    onSessionSelected = { selectedSessionId = it.toString() },
                    onOpenSession = { runOperation { repository.openSession(it) } },
                    terminalEngine = repository::terminalEngine,
                    historyEngine = repository::historyEngine,
                    selectedHistoryId = selectedHistoryId,
                    onHistorySelected = { selectedHistoryId = it.historyObject.id.toString() },
                    onOpenSessions = { navigateTo(ProductDestination.SESSIONS) },
                    onOpenHistory = { navigateTo(ProductDestination.HISTORY) },
                    onOpenAccount = { navigateTo(ProductDestination.ACCOUNT) },
                    onOpenCommandSearch = { commandSearchOpen = true },
                    themePreference = themePreference,
                    onThemePreferenceChanged = onThemePreferenceChanged,
                    onSignIn = { runOperation(repository::signIn) },
                    onSignOut = { signOutConfirmation = true },
                    onCheckForUpdate = { runOperation { repository.checkForUpdate() } },
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
    if (commandSearchOpen) {
        CommandSearchDialog(
            onDismiss = { commandSearchOpen = false },
            onNavigate = { next ->
                commandSearchOpen = false
                navigateTo(next)
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
private fun RegistrationDialog(
    onDismiss: () -> Unit,
    onSubmit: (String, String, CharArray, CharArray) -> Unit,
) {
    var username by rememberSaveable { mutableStateOf("") }
    var email by rememberSaveable { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var confirm by remember { mutableStateOf("") }
    var submitting by remember { mutableStateOf(false) }
    val valid = username.trim().length >= 3 && email.trim().contains("@") &&
        password.length >= 8 && password == confirm
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
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    if (!valid || submitting) return@Button
                    submitting = true
                    val nextPassword = password.toCharArray()
                    val nextConfirm = confirm.toCharArray()
                    password = ""
                    confirm = ""
                    onSubmit(username.trim(), email.trim(), nextPassword, nextConfirm)
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
    // Retained enum values are never exposed by the shell.
    ProductDestination.MEMBERSHIP,
    ProductDestination.TRUSTED_DEVICES -> label
    ProductDestination.ACCOUNT -> stringResource(R.string.nav_account)
    ProductDestination.SETTINGS -> stringResource(R.string.nav_settings)
}

@Composable
private fun DeviceType.localizedLabel(): String = when (this) {
    DeviceType.WINDOWS_PC -> stringResource(R.string.device_type_windows_pc)
    DeviceType.ANDROID -> stringResource(R.string.device_type_android)
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
    snapshot: ProductSnapshot,
    wide: Boolean,
    selectedSessionId: String?,
    onSessionSelected: (java.util.UUID) -> Unit,
    onOpenSession: (java.util.UUID) -> Unit,
    terminalEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    historyEngine: (java.util.UUID) -> TermuxTerminalEngine?,
    selectedHistoryId: String?,
    onHistorySelected: (HistoryEntry) -> Unit,
    onOpenSessions: () -> Unit,
    onOpenHistory: () -> Unit,
    onOpenAccount: () -> Unit,
    onOpenCommandSearch: () -> Unit,
    themePreference: ProductThemePreference,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit,
    onSignIn: () -> Unit,
    onSignOut: () -> Unit,
    onCheckForUpdate: () -> Unit,
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
                onRequestControl = onRequestControl,
                onReleaseControl = onReleaseControl,
                onPaste = onPaste,
                onTerminalAction = onTerminalAction,
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
                onOpenAccount = onOpenAccount,
                themePreference = themePreference,
                onThemePreferenceChanged = onThemePreferenceChanged,
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
    ConnectionBanner(snapshot = snapshot)
    Text(stringResource(R.string.nav_devices), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
    val devices = snapshot.devices
    if (devices.isEmpty()) {
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
        devices.forEach { device ->
            DeviceRow(
                device = device,
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
private fun DeviceRow(
    device: DeviceSummary,
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
            }
        }
    }
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
    ConnectionBanner(snapshot = snapshot)
    if (snapshot.sessions.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Terminal,
            title = if (snapshot.signedIn) stringResource(R.string.empty_no_sessions) else stringResource(R.string.empty_sign_in_sessions),
            detail = stringResource(R.string.server_sessions_empty_copy),
            actionLabel = null,
            onAction = {},
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
private fun SettingsPage(
    snapshot: ProductSnapshot,
    onSignIn: () -> Unit,
    onSignOut: () -> Unit,
    onCheckForUpdate: () -> Unit,
    onOpenAccount: () -> Unit,
    themePreference: ProductThemePreference,
    onThemePreferenceChanged: (ProductThemePreference) -> Unit,
) {
    PageHeader(stringResource(R.string.nav_settings), stringResource(R.string.page_settings_subtitle), Icons.Filled.Settings)
    ConnectionBanner(snapshot = snapshot)
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
                icon = Icons.Filled.CheckCircle,
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
    PageHeader(stringResource(R.string.settings_account_title), stringResource(R.string.settings_account_copy), Icons.Filled.AccountCircle)
    OutlinedButton(onClick = onOpenAccount, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
        Icon(Icons.Filled.AccountCircle, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(8.dp))
        Text(stringResource(R.string.nav_account))
    }
    PageHeader(stringResource(R.string.settings_updates_title), stringResource(R.string.settings_updates_copy), Icons.Filled.Refresh)
    val update = snapshot.updateStatus
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(
                when (update.decision) {
                    ProductUpdateDecision.NOT_CONFIGURED -> stringResource(R.string.update_not_configured)
                    ProductUpdateDecision.NO_UPDATE -> stringResource(R.string.update_no_update)
                    ProductUpdateDecision.OPTIONAL -> stringResource(R.string.update_optional, update.versionName ?: "-")
                    ProductUpdateDecision.REQUIRED -> stringResource(R.string.update_required, update.versionName ?: "-")
                    ProductUpdateDecision.ERROR -> stringResource(R.string.update_error)
                },
                style = MaterialTheme.typography.titleSmall,
            )
            update.message?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            update.downloadUrl?.let {
                Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            OutlinedButton(onClick = onCheckForUpdate, modifier = Modifier.heightIn(min = 48.dp)) {
                Icon(Icons.Filled.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.action_check_updates))
            }
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
