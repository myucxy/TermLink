package dev.termlink.product

import android.graphics.Color as AndroidColor
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.material.icons.filled.Android
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Block
import androidx.compose.material.icons.filled.CardMembership
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.LaptopWindows
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PauseCircle
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Send
import androidx.compose.material.icons.filled.ShoppingCart
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material.icons.filled.StopCircle
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
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.ScrollableTabRow
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TooltipDefaults
import androidx.compose.material3.VerticalDivider
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
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.GrantPermission
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
import kotlinx.coroutines.launch

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
    background = ProductBackground,
    surface = ProductSurface,
    surfaceVariant = ProductSurfaceVariant,
    onSurface = Color(0xFFE9EEF5),
    onSurfaceVariant = ProductMuted,
    outline = Color(0xFF465363),
    error = ProductDanger,
)

class ProductActivity : ComponentActivity() {
    private lateinit var repository: ProductionProductRepository
    private val authorizationLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        repository.handleAuthorizationResult(result.data)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        repository = ProductionProductRepository(this, authorizationLauncher::launch)
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(AndroidColor.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(AndroidColor.TRANSPARENT),
        )
        setContent {
            MaterialTheme(colorScheme = productColors) {
                ProductApp(repository)
            }
        }
    }

    override fun onDestroy() {
        repository.close()
        super.onDestroy()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProductApp(repository: ProductRepository) {
    val snapshot by repository.snapshot.collectAsStateCompat()
    var destination by rememberSaveable { mutableStateOf(ProductDestination.DEVICES) }
    var deviceFilter by rememberSaveable { mutableStateOf(DeviceFilter.ALL) }
    var selectedSessionId by rememberSaveable { mutableStateOf<String?>(null) }
    var selectedHistoryId by rememberSaveable { mutableStateOf<String?>(null) }
    var pasteSessionId by remember { mutableStateOf<java.util.UUID?>(null) }
    var configureServer by remember { mutableStateOf(false) }
    var completePairing by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }

    fun runOperation(operation: suspend () -> Unit) {
        scope.launch {
            runCatching { operation() }
                .onFailure { error ->
                    snackbarHostState.showSnackbar(
                        error.message ?: "The server operation was not available",
                    )
                }
        }
    }

    if (!snapshot.signedIn) {
        Scaffold(
            snackbarHost = { SnackbarHost(snackbarHostState) },
            containerColor = productColors.background,
        ) { innerPadding ->
            ProductSignInPage(
                snapshot = snapshot,
                onSignIn = { runOperation(repository::signIn) },
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
                    runOperation { repository.configureServer(value) }
                },
            )
        }
        return
    }

    BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
        val wide = maxWidth >= 700.dp
        Scaffold(
            topBar = {
                ProductTopBar(
                    destination = destination,
                    snapshot = snapshot,
                    onSettings = { destination = ProductDestination.SETTINGS },
                    onAccount = { destination = ProductDestination.SETTINGS },
                    onRefresh = { runOperation(repository::refresh) },
                )
            },
            bottomBar = {
                if (!wide) {
                    ProductBottomNavigation(destination, onDestinationSelected = { destination = it })
                }
            },
            snackbarHost = { SnackbarHost(snackbarHostState) },
            containerColor = productColors.background,
        ) { innerPadding ->
            Row(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(innerPadding),
            ) {
                if (wide) {
                    ProductNavigationRail(destination, onDestinationSelected = { destination = it })
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
                    onConfigure = { configureServer = true },
                    onCompletePairing = { completePairing = true },
                    onPurchase = { runOperation { repository.purchaseProduct(it) } },
                    onRestorePurchases = { runOperation { repository.restorePurchases() } },
                    onSignIn = { runOperation(repository::signIn) },
                    onSignOut = { runOperation(repository::signOut) },
                    onApprove = { runOperation { repository.approveDevice(it) } },
                    onRevoke = { runOperation { repository.revokeDevice(it) } },
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
                runOperation { repository.configureServer(value) }
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
}

@Composable
private fun ProductSignInPage(
    snapshot: ProductSnapshot,
    onSignIn: () -> Unit,
    onConfigure: () -> Unit,
    modifier: Modifier = Modifier,
) {
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
                            contentDescription = "TermLink",
                            modifier = Modifier.size(28.dp),
                        )
                    }
                }
                Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
                    Text("TermLink", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                    Text(
                        "Remote terminal, under your control",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }

            Spacer(Modifier.height(36.dp))
            Text(
                "SECURELY CONNECT YOUR DEVICES",
                color = ProductPositive,
                fontFamily = FontFamily.Monospace,
                fontSize = 12.sp,
                fontWeight = FontWeight.Bold,
            )
            Spacer(Modifier.height(8.dp))
            Text(
                "Return to\nyour terminal",
                fontSize = 38.sp,
                lineHeight = 40.sp,
                fontWeight = FontWeight.SemiBold,
                letterSpacing = (-0.5).sp,
            )
            Spacer(Modifier.height(10.dp))
            Text(
                "Sign in to register this Android device. Account access never grants PC access until an existing trusted device approves it.",
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
                    color = ProductPositive.copy(alpha = 0.14f),
                    contentColor = ProductPositive,
                ) {
                    Box(contentAlignment = Alignment.Center) {
                        Icon(Icons.Filled.Shield, contentDescription = null, modifier = Modifier.size(20.dp))
                    }
                }
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text("Verified TermLink Server", style = MaterialTheme.typography.labelMedium)
                    Text(
                        snapshot.serverUrl ?: "Server unavailable",
                        style = MaterialTheme.typography.bodySmall,
                        fontFamily = FontFamily.Monospace,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                Column(horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(1.dp)) {
                    Text("TLS healthy", style = MaterialTheme.typography.labelSmall, color = ProductPositive)
                    TextButton(onClick = onConfigure, contentPadding = ButtonDefaults.TextButtonContentPadding) {
                        Text("Change")
                    }
                }
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outline.copy(alpha = 0.45f))

            Spacer(Modifier.height(22.dp))
            Button(
                onClick = onSignIn,
                enabled = snapshot.configured,
                modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
            ) {
                Icon(Icons.AutoMirrored.Filled.Login, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("Sign in and bind this device", fontWeight = FontWeight.SemiBold)
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
                    tint = ProductPositive,
                )
                Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text("Secure browser & PKCE", style = MaterialTheme.typography.titleSmall)
                    Text(
                        "Your password is entered only on the verified server page. TermLink receives the authorization result and never stores the password.",
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
        title = { Text("Server") },
        text = {
            OutlinedTextField(
                value = value,
                onValueChange = { value = it },
                label = { Text("HTTPS URL") },
                placeholder = { Text("https://termlink.example.com") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            )
        },
        confirmButton = {
            Button(onClick = { onConfirm(value) }, enabled = value.isNotBlank()) { Text("Save") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ProductTopBar(
    destination: ProductDestination,
    snapshot: ProductSnapshot,
    onSettings: () -> Unit,
    onAccount: () -> Unit,
    onRefresh: () -> Unit,
) {
    CenterAlignedTopAppBar(
        title = {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Image(
                    painter = painterResource(R.drawable.termlink_mark_color),
                    contentDescription = null,
                    modifier = Modifier.size(25.dp),
                )
                Column(horizontalAlignment = Alignment.Start) {
                    Text("TermLink", style = MaterialTheme.typography.titleMedium)
                    Text(
                        destination.label,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        },
        navigationIcon = {
            Icon(
                imageVector = if (snapshot.signedIn) Icons.Filled.Wifi else Icons.Filled.WifiOff,
                contentDescription = if (snapshot.signedIn) "Server connected" else "Server not configured",
                tint = if (snapshot.signedIn) ProductPositive else ProductWarning,
                modifier = Modifier.padding(start = 16.dp),
            )
        },
        actions = {
            IconButton(onClick = onRefresh) {
                Icon(Icons.Filled.Refresh, contentDescription = "Refresh server data")
            }
            IconButton(onClick = onSettings) {
                Icon(Icons.Filled.Settings, contentDescription = "Open settings")
            }
            IconButton(onClick = onAccount) {
                Icon(
                    if (snapshot.signedIn) Icons.Filled.CheckCircle else Icons.AutoMirrored.Filled.Login,
                    contentDescription = if (snapshot.signedIn) "Account settings" else "Configure account",
                )
            }
        },
    )
}

@Composable
private fun ProductNavigationRail(
    destination: ProductDestination,
    onDestinationSelected: (ProductDestination) -> Unit,
) {
    NavigationRail(
        modifier = Modifier.width(88.dp),
        containerColor = productColors.background,
    ) {
        Spacer(Modifier.height(12.dp))
        ProductDestination.entries.forEach { item ->
            NavigationRailItem(
                selected = destination == item,
                onClick = { onDestinationSelected(item) },
                icon = { Icon(item.icon(), contentDescription = item.label) },
                label = { Text(item.label, maxLines = 1, overflow = TextOverflow.Ellipsis) },
            )
        }
    }
}

@Composable
private fun ProductBottomNavigation(
    destination: ProductDestination,
    onDestinationSelected: (ProductDestination) -> Unit,
) {
    NavigationBar(containerColor = productColors.surface) {
        ProductDestination.entries.forEach { item ->
            NavigationBarItem(
                selected = destination == item,
                onClick = { onDestinationSelected(item) },
                icon = { Icon(item.icon(), contentDescription = item.label) },
                label = { Text(item.label, maxLines = 1, overflow = TextOverflow.Ellipsis) },
            )
        }
    }
}

private fun ProductDestination.icon(): ImageVector = when (this) {
    ProductDestination.DEVICES -> Icons.Filled.Devices
    ProductDestination.SESSIONS -> Icons.Filled.Terminal
    ProductDestination.ACTIVITY -> Icons.Filled.Timeline
    ProductDestination.HISTORY -> Icons.Filled.History
    ProductDestination.MEMBERSHIP -> Icons.Filled.CardMembership
    ProductDestination.SETTINGS -> Icons.Filled.Settings
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
            ProductDestination.ACTIVITY -> ActivityPage(snapshot = snapshot, onConfigure = onConfigure)
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
            ProductDestination.SETTINGS -> SettingsPage(
                snapshot = snapshot,
                onConfigure = onConfigure,
                onSignIn = onSignIn,
                onSignOut = onSignOut,
            )
        }
    }
}

@Composable
private fun DevicesPage(
    snapshot: ProductSnapshot,
    filter: DeviceFilter,
    onFilterChanged: (DeviceFilter) -> Unit,
    onConfigure: () -> Unit,
    onCompletePairing: () -> Unit,
    onApprove: (java.util.UUID) -> Unit,
    onRevoke: (java.util.UUID) -> Unit,
) {
    PageHeader(
        title = "Devices",
        subtitle = "Trusted phones and PCs, with pending approvals kept visible.",
        icon = Icons.Filled.Devices,
    )
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    if (snapshot.accountLabel == "Approval pending") {
        PairingPendingCard(onCompletePairing = onCompletePairing)
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
                label = { Text(item.label) },
                leadingIcon = if (filter == item) {
                    { Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp)) }
                } else null,
                modifier = Modifier.heightIn(min = 44.dp),
            )
        }
    }
    val devices = snapshot.devices.filter { device ->
        when (filter) {
            DeviceFilter.ALL -> true
            DeviceFilter.TRUSTED -> device.trustState == DeviceTrustState.TRUSTED
            DeviceFilter.PENDING -> device.trustState == DeviceTrustState.PENDING
        }
    }
    if (devices.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Shield,
            title = if (snapshot.signedIn) "No devices in this view" else "Sign in to view devices",
            detail = if (snapshot.signedIn) {
                "The server has not returned any devices for this filter."
            } else {
                "Device trust and pending requests are server data."
            },
            actionLabel = if (!snapshot.configured) "Configure server" else null,
            onAction = onConfigure,
        )
    } else {
        devices.forEach { device ->
            DeviceRow(device = device, onApprove = onApprove, onRevoke = onRevoke)
        }
    }
    if (snapshot.pcs.isNotEmpty()) {
        PageHeader(title = "PC routing", subtitle = "Per-PC reachability and agent state.", icon = Icons.Filled.LaptopWindows)
        snapshot.pcs.forEach { pc -> PcRow(pc) }
    }
}

@Composable
private fun PairingPendingCard(onCompletePairing: () -> Unit) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = ProductWarning.copy(alpha = 0.10f),
        border = BorderStroke(1.dp, ProductWarning.copy(alpha = 0.55f)),
    ) {
        Row(
            modifier = Modifier.padding(14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Icon(Icons.Filled.Lock, contentDescription = "Approval pending", tint = ProductWarning)
            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text("Device approval pending", style = MaterialTheme.typography.titleSmall)
                Text(
                    "Enter the challenge issued by a trusted TermLink manager.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            OutlinedButton(onClick = onCompletePairing) {
                Icon(Icons.AutoMirrored.Filled.Login, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text("Complete pairing")
            }
        }
    }
}

@Composable
private fun PairingChallengeCard(challenge: PairingChallengeState) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = ProductPositive.copy(alpha = 0.08f),
        border = BorderStroke(1.dp, ProductPositive.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(Icons.Filled.Shield, contentDescription = "Pairing challenge", tint = ProductPositive)
                Text("Pairing challenge", style = MaterialTheme.typography.titleSmall)
            }
            Text(
                "${challenge.targetName ?: "Target device"}  |  challenge ${challenge.challengeId}",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text("Code  ${challenge.code.chunked(4).joinToString("-")}", fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.titleMedium)
            Text("Nonce  ${challenge.challengeNonce}", fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(
                "Show this code and nonce to the pending device. It expires ${challenge.expiresAt ?: "soon"}.",
                style = MaterialTheme.typography.bodySmall,
                color = ProductWarning,
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
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Filled.Shield, contentDescription = null) },
        title = { Text("Complete device pairing") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    value = challengeId,
                    onValueChange = { challengeId = it },
                    label = { Text("Challenge ID") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                )
                OutlinedTextField(
                    value = code,
                    onValueChange = { code = it },
                    label = { Text("Pairing code") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
                )
                OutlinedTextField(
                    value = nonce,
                    onValueChange = { nonce = it },
                    label = { Text("Challenge nonce") },
                    minLines = 2,
                    maxLines = 3,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
                )
            }
        },
        confirmButton = {
            Button(
                onClick = { onConfirm(challengeId, code, nonce) },
                enabled = challengeId.isNotBlank() && code.isNotBlank() && nonce.isNotBlank(),
            ) { Text("Complete") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
private fun DeviceRow(
    device: DeviceSummary,
    onApprove: (java.util.UUID) -> Unit,
    onRevoke: (java.util.UUID) -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = productColors.surface,
        border = BorderStroke(1.dp, productColors.outline.copy(alpha = 0.55f)),
    ) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(
                    imageVector = if (device.type == DeviceType.WINDOWS_PC) Icons.Filled.LaptopWindows else Icons.Filled.PhoneAndroid,
                    contentDescription = device.type.name,
                    tint = ProductPrimary,
                    modifier = Modifier.size(26.dp),
                )
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(device.name.ifBlank { "Unnamed device" }, style = MaterialTheme.typography.titleMedium)
                    Text(
                        "${device.type.name.replace('_', ' ')}  |  key ${device.keyId}",
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
                        Button(onClick = { onApprove(device.id) }, contentPadding = ButtonDefaults.ContentPadding) {
                            Icon(Icons.Filled.Check, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text("Approve")
                        }
                        OutlinedButton(onClick = { onRevoke(device.id) }) {
                            Icon(Icons.Filled.Block, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text("Reject")
                        }
                    }
                    DeviceTrustState.TRUSTED -> OutlinedButton(onClick = { onRevoke(device.id) }) {
                        Icon(Icons.Filled.Block, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text("Revoke trust")
                    }
                    DeviceTrustState.REVOKED -> Text(
                        "Revoked by the server",
                        style = MaterialTheme.typography.bodySmall,
                        color = ProductDanger,
                    )
                }
            }
        }
    }
}

@Composable
private fun TrustPill(state: DeviceTrustState) {
    val color = when (state) {
        DeviceTrustState.TRUSTED -> ProductPositive
        DeviceTrustState.PENDING -> ProductWarning
        DeviceTrustState.REVOKED -> ProductDanger
    }
    Text(
        ProductFormatter.trustLabel(state),
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
            .border(BorderStroke(1.dp, productColors.outline.copy(alpha = 0.4f)), ProductShape)
            .padding(12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(Icons.Filled.LaptopWindows, contentDescription = "Windows PC", tint = ProductPrimary)
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(pc.name, style = MaterialTheme.typography.titleSmall)
            Text(
                "Agent ${pc.agentVersion}  |  remote routing ${if (pc.remoteRoutingEnabled) "enabled" else "disabled"}",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Icon(
            if (pc.online) Icons.Filled.Wifi else Icons.Filled.WifiOff,
            contentDescription = if (pc.online) "PC online" else "PC offline",
            tint = if (pc.online) ProductPositive else ProductMuted,
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
        title = "Sessions",
        subtitle = "Per-PC sessions and the live terminal stream.",
        icon = Icons.Filled.Terminal,
    )
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    if (snapshot.sessions.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Terminal,
            title = if (snapshot.signedIn) "No sessions" else "Sign in to view sessions",
            detail = "Live sessions appear here after a trusted PC starts one.",
            actionLabel = if (!snapshot.configured) "Configure server" else null,
            onAction = onConfigure,
        )
        return
    }
    val selected = snapshot.sessions.firstOrNull { it.id.toString() == selectedSessionId }
        ?: snapshot.sessions.first()
    LaunchedEffect(selected.id) { onOpenSession(selected.id) }
    val selectedState = snapshot.sessionStates[selected.id]
    val selectedEngine = terminalEngine(selected.id)
    fun pcLabel(session: SessionSummary): String =
        snapshot.pcs.firstOrNull { it.id == session.pcId }?.name
            ?: "PC ${session.pcId}"
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
        color = productColors.surface,
        border = BorderStroke(1.dp, productColors.outline.copy(alpha = 0.55f)),
    ) {
        Column {
            Text("All sessions", modifier = Modifier.padding(14.dp), style = MaterialTheme.typography.titleSmall)
            sessions.forEachIndexed { index, session ->
                if (index > 0) HorizontalDivider(color = productColors.outline.copy(alpha = 0.35f))
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
        SessionState.RUNNING -> ProductPositive
        SessionState.INTERRUPTED -> ProductWarning
        SessionState.EXITED -> ProductMuted
    }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(if (selected) productColors.surfaceVariant else Color.Transparent)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 12.dp),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(Icons.Filled.Terminal, contentDescription = "Terminal session", tint = stateColor)
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(session.shellType, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                "$pcLabel  |  ${ProductFormatter.sessionLabel(session.state)}  |  ${session.columns} x ${session.rows}",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Text(
            "#${session.lastOutputSequence}",
            style = MaterialTheme.typography.labelSmall,
            color = stateColor,
        )
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
        color = productColors.surface,
        border = BorderStroke(1.dp, productColors.outline.copy(alpha = 0.55f)),
    ) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(session.shellType, style = MaterialTheme.typography.titleMedium)
                    Text(
                        "$pcLabel  |  session ${session.id}  |  server state ${ProductFormatter.sessionLabel(session.state)}",
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
                ) {
                    Icon(Icons.Filled.Lock, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("Request control")
                }
                OutlinedButton(
                    onClick = { onReleaseControl(session.id) },
                    enabled = remoteState is RemoteSessionState.Controlling,
                ) {
                    Icon(Icons.Filled.Lock, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("Release")
                }
                OutlinedButton(
                    onClick = { onPaste(session.id) },
                    enabled = remoteState is RemoteSessionState.Controlling,
                ) {
                    Icon(Icons.Filled.ContentPaste, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("Paste")
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
            .height(44.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        TerminalTextKey("Esc", TerminalAction.ESCAPE, enabled, onAction)
        TerminalTextKey("Tab", TerminalAction.TAB, enabled, onAction)
        TerminalTextKey("Enter", TerminalAction.ENTER, enabled, onAction)
        TerminalIconKey(Icons.AutoMirrored.Filled.KeyboardArrowLeft, "Left arrow", TerminalAction.ARROW_LEFT, enabled, onAction)
        TerminalIconKey(Icons.Filled.KeyboardArrowUp, "Up arrow", TerminalAction.ARROW_UP, enabled, onAction)
        TerminalIconKey(Icons.Filled.KeyboardArrowDown, "Down arrow", TerminalAction.ARROW_DOWN, enabled, onAction)
        TerminalIconKey(Icons.AutoMirrored.Filled.KeyboardArrowRight, "Right arrow", TerminalAction.ARROW_RIGHT, enabled, onAction)
        TerminalTextKey("Ctrl+C", TerminalAction.CTRL_C, enabled, onAction)
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
        modifier = Modifier.height(40.dp),
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
        modifier = Modifier.size(40.dp),
    ) {
        Icon(icon, contentDescription = description)
    }
}

@Composable
private fun SessionStatePill(state: SessionState) {
    val color = when (state) {
        SessionState.RUNNING -> ProductPositive
        SessionState.INTERRUPTED -> ProductWarning
        SessionState.EXITED -> ProductMuted
    }
    Text(
        ProductFormatter.sessionLabel(state),
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
            Text("Live terminal", style = MaterialTheme.typography.labelLarge)
            Text(
                terminalStateLabel(remoteState),
                style = MaterialTheme.typography.labelSmall,
                color = ProductWarning,
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

private fun terminalStateLabel(state: RemoteSessionState?): String = when (state) {
    is RemoteSessionState.Controlling -> "Controlling"
    is RemoteSessionState.Live -> "Live"
    is RemoteSessionState.Reconnecting -> state.reason?.let { "Reconnecting | $it" } ?: "Reconnecting"
    is RemoteSessionState.ReadOnly -> "Read only | ${state.reason}"
    is RemoteSessionState.HistoryGap -> "History gap"
    is RemoteSessionState.Exited -> "Exited"
    null -> "Connecting"
}

@Composable
private fun ActivityPage(snapshot: ProductSnapshot, onConfigure: () -> Unit) {
    PageHeader("Activity", "Audit events from the server and session transport.", Icons.Filled.Timeline)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    if (snapshot.activity.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.Timeline,
            title = if (snapshot.signedIn) "No activity yet" else "Sign in to view activity",
            detail = "Events are loaded from the server; this screen does not synthesize local history.",
            actionLabel = if (!snapshot.configured) "Configure server" else null,
            onAction = onConfigure,
        )
    } else {
        snapshot.activity.forEachIndexed { index, event ->
            if (index > 0) HorizontalDivider(color = productColors.outline.copy(alpha = 0.35f))
            ActivityRow(event)
        }
    }
}

@Composable
private fun ActivityRow(event: ActivityEntry) {
    val color = when (event.severity) {
        Severity.INFO -> ProductPrimary
        Severity.SUCCESS -> ProductPositive
        Severity.WARNING -> ProductWarning
        Severity.ERROR -> ProductDanger
    }
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 10.dp),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(Icons.Filled.Info, contentDescription = event.severity.name, tint = color)
        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(event.title, style = MaterialTheme.typography.titleSmall)
            Text(event.detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
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
    PageHeader("History", "Replay encrypted session objects without exposing plaintext metadata locally.", Icons.Filled.History)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    if (snapshot.history.isEmpty()) {
        EmptyState(
            icon = Icons.Filled.History,
            title = if (snapshot.signedIn) "No history objects" else "Sign in to view history",
            detail = "History is available only when the server returns encrypted chunks or snapshots.",
            actionLabel = if (!snapshot.configured) "Configure server" else null,
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
        color = productColors.surface,
        border = BorderStroke(1.dp, productColors.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(entry.sessionLabel, style = MaterialTheme.typography.titleMedium)
                    Text(
                        "${ProductFormatter.objectKindLabel(entry.historyObject.kind)}  |  ${entry.historyObject.sizeBytes} bytes  |  key ${entry.historyObject.keyId}",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                OutlinedButton(onClick = { onReplay(entry) }) {
                    Icon(Icons.Filled.Replay, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("Replay")
                }
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
    PageHeader("Membership", "Entitlement and quotas returned by the server catalog.", Icons.Filled.CardMembership)
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
            title = "Entitlement unavailable",
            detail = "Plans and limits are never guessed on this device. Sign in and sync the server catalog.",
            actionLabel = if (!snapshot.configured) "Configure server" else null,
            onAction = onConfigure,
        )
        return
    }
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = productColors.surface,
        border = BorderStroke(1.dp, productColors.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Icon(Icons.Filled.Shield, contentDescription = "Server entitlement", tint = ProductPositive)
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(plan?.displayName ?: entitlement.planKey, style = MaterialTheme.typography.titleMedium)
                    Text(
                        plan?.description ?: "Plan metadata is not available in this catalog response.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            Text(
                "Server entitlement v${entitlement.version}  |  catalog v${entitlement.catalogVersion}",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            QuotaRow(
                label = "Remote PCs",
                used = snapshot.pcs.size,
                limit = entitlement.maxRemotePcs,
            )
            QuotaRow(
                label = "Live sessions",
                used = snapshot.sessions.count { it.state == SessionState.RUNNING },
                limit = entitlement.maxLiveSessions,
            )
            Text(
                "Effective ${formatInstant(entitlement.effectiveAt)}" +
                    (entitlement.expiresAt?.let { "  |  expires ${formatInstant(it)}" } ?: ""),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
    val flags = entitlement.featureFlags.entries.sortedBy { it.key }
    if (flags.isNotEmpty()) {
        PageHeader("Features", "Feature flags are evaluated from the same server entitlement.", Icons.Filled.CheckCircle)
        flags.forEach { (key, enabled) ->
            Row(
                modifier = Modifier.fillMaxWidth().padding(vertical = 7.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Icon(
                    if (enabled) Icons.Filled.CheckCircle else Icons.Filled.Block,
                    contentDescription = if (enabled) "Enabled" else "Disabled",
                    tint = if (enabled) ProductPositive else ProductMuted,
                )
                Text(key, modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                Text(if (enabled) "Enabled" else "Disabled", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
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
    PageHeader("Google Play", "Prices and billing periods come from Google Play for this server catalog.", Icons.Filled.ShoppingCart)
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = productColors.surface,
        border = BorderStroke(1.dp, productColors.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            if (billing.products.isEmpty()) {
                Text(
                    billing.message ?: "No Google Play products are available in the published catalog.",
                    style = MaterialTheme.typography.bodySmall,
                    color = if (billing.phase == dev.termlink.billing.BillingPhase.ERROR) ProductDanger else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            } else {
                billing.products.forEachIndexed { index, product ->
                    if (index > 0) HorizontalDivider(color = productColors.outline.copy(alpha = 0.35f))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.Top,
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                            Text(product.title, style = MaterialTheme.typography.titleSmall)
                            Text(product.description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            Text(
                                listOfNotNull(product.formattedPrice, product.billingPeriod).joinToString("  |  ").ifBlank { "Price unavailable" },
                                style = MaterialTheme.typography.labelLarge,
                                color = ProductPrimary,
                            )
                        }
                        Button(
                            onClick = { onPurchase(product.productId) },
                            enabled = snapshot.signedIn && snapshot.configured && !busy,
                        ) {
                            Icon(Icons.Filled.ShoppingCart, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(6.dp))
                            Text("Buy")
                        }
                    }
                }
            }
            billing.message?.let { message ->
                Text(message, style = MaterialTheme.typography.bodySmall, color = ProductWarning)
            }
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                OutlinedButton(
                    onClick = onRestorePurchases,
                    enabled = snapshot.signedIn && snapshot.configured && !busy,
                ) {
                    Icon(Icons.Filled.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("Restore purchases")
                }
                Text(
                    billing.phase.name.lowercase().replace('_', ' ').replaceFirstChar { it.uppercase() },
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
            Text(ProductFormatter.quotaLabel(used, limit), style = MaterialTheme.typography.labelLarge, color = ProductPrimary)
        }
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(8.dp)
                .clip(RoundedCornerShape(4.dp))
                .background(productColors.surfaceVariant),
        ) {
            Box(
                modifier = Modifier
                    .fillMaxWidth(ratio)
                    .fillMaxHeight()
                    .background(if (used > limit && limit > 0) ProductDanger else ProductPrimary),
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
) {
    PageHeader("Settings", "Connection, account, and local safety defaults.", Icons.Filled.Settings)
    ConnectionBanner(snapshot = snapshot, onConfigure = onConfigure)
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = productColors.surface,
        border = BorderStroke(1.dp, productColors.outline.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            SettingRow(
                icon = if (snapshot.signedIn) Icons.Filled.Wifi else Icons.Filled.WifiOff,
                title = "Server connection",
                detail = if (snapshot.configured) "Configured; waiting for the next server sync." else "No server endpoint is configured.",
            )
            SettingRow(
                icon = Icons.Filled.Shield,
                title = "Device proof",
                detail = "Requests use the device identity and server-issued access token.",
            )
            SettingRow(
                icon = Icons.Filled.Lock,
                title = "Input safety",
                detail = "Terminals stay read only until a server control lease is granted.",
            )
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Button(onClick = onConfigure) {
                    Icon(Icons.Filled.Settings, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("Configure server")
                }
                if (snapshot.signedIn) {
                    OutlinedButton(onClick = onSignOut) {
                        Icon(Icons.AutoMirrored.Filled.Logout, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text("Sign out")
                    }
                } else {
                    OutlinedButton(onClick = onSignIn) {
                        Icon(Icons.AutoMirrored.Filled.Login, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text("Sign in")
                    }
                }
            }
        }
    }
    PageHeader("About", "Build and protocol details for support diagnostics.", Icons.Filled.Info)
    Text("TermLink Android product shell", style = MaterialTheme.typography.bodyMedium)
    Text("Production transport is enabled only after a configured server and account are present.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun SettingRow(icon: ImageVector, title: String, detail: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.Top) {
        Icon(icon, contentDescription = title, tint = ProductPrimary)
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun ConnectionBanner(snapshot: ProductSnapshot, onConfigure: () -> Unit) {
    val configured = snapshot.signedIn && snapshot.configured
    val color = when {
        configured -> ProductPositive
        snapshot.signedIn -> ProductWarning
        else -> ProductWarning
    }
    val icon = when {
        configured -> Icons.Filled.Wifi
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
                        configured -> "Connected to server"
                        snapshot.signedIn -> "Account configured; server endpoint missing"
                        else -> "Signed out"
                    },
                    style = MaterialTheme.typography.titleSmall,
                    color = color,
                )
                Text(
                    when {
                        configured -> "Devices, sessions, activity, and entitlement are server authoritative."
                        snapshot.signedIn -> "Configure a server before requesting devices or sessions."
                        else -> "Sign in and configure a server to load trusted devices and sessions."
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurface,
                )
            }
            TextButton(onClick = onConfigure) { Text("Settings") }
        }
    }
}

@Composable
private fun PageHeader(title: String, subtitle: String, icon: ImageVector) {
    Row(verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Icon(icon, contentDescription = title, tint = ProductPrimary, modifier = Modifier.size(28.dp))
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
        border = BorderStroke(1.dp, productColors.outline.copy(alpha = 0.55f)),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 18.dp, vertical = 26.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Icon(icon, contentDescription = null, tint = ProductMuted, modifier = Modifier.size(32.dp))
            Text(title, style = MaterialTheme.typography.titleMedium)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (actionLabel != null) {
                OutlinedButton(onClick = onAction) { Text(actionLabel) }
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
        title = { Text("Paste into remote terminal?") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("This text will be sent to the server after you confirm.")
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it },
                    modifier = Modifier.fillMaxWidth(),
                    minLines = 3,
                    maxLines = 8,
                    label = { Text("Text to send") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text),
                )
            }
        },
        confirmButton = {
            Button(onClick = { onConfirm(text) }, enabled = text.isNotEmpty()) { Text("Send") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
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
        icon = { Icon(icon, contentDescription = null, tint = ProductDanger) },
        title = { Text(title) },
        text = { Text(message) },
        confirmButton = {
            Button(
                onClick = onConfirm,
                colors = ButtonDefaults.buttonColors(containerColor = ProductDanger, contentColor = Color(0xFF2E0D10)),
            ) { Text(confirmLabel) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

private fun formatInstant(value: Instant): String = value.toString().replace('T', ' ').removeSuffix("Z").take(19)

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
