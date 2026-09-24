package dev.termlink.harness

import android.graphics.Color as AndroidColor
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.ui.Alignment
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MenuAnchorType
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import dev.termlink.terminal.api.ArgbColor
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.network.testrelay.Phase1TestScenario
import dev.termlink.network.testrelay.Phase1TestSessionClient
import dev.termlink.network.testrelay.Phase1TestSessionConfig
import dev.termlink.terminal.replay.GoldenStreamReplayer
import dev.termlink.terminal.termux.TermuxTerminalEngine
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(AndroidColor.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(AndroidColor.TRANSPARENT),
        )
        setContent {
            MaterialTheme(colorScheme = termLinkDarkColors) {
                Surface(modifier = Modifier.fillMaxSize()) {
                    GoldenStreamHarness()
                }
            }
        }
    }

    @OptIn(ExperimentalMaterial3Api::class)
    @Composable
    private fun GoldenStreamHarness() {
        val scope = rememberCoroutineScope()
        val engine = remember {
            TermuxTerminalEngine().also {
                it.applyTheme(TERMINAL_THEME)
                it.setReadOnly(true)
            }
        }
        val connectionHolder = remember { arrayOfNulls<LanReplayConnection>(1) }
        val phase1Holder = remember { arrayOfNulls<Phase1TestSessionClient>(1) }
        var phase1Client by remember { mutableStateOf<Phase1TestSessionClient?>(null) }
        var state by remember { mutableStateOf<HarnessState>(HarnessState.Idle) }
        var address by rememberSaveable {
            mutableStateOf(intent.getStringExtra(EXTRA_RELAY_ADDRESS) ?: DEFAULT_LAN_ADDRESS)
        }
        var token by rememberSaveable {
            mutableStateOf(intent.getStringExtra(EXTRA_TEST_TOKEN) ?: DEFAULT_TEST_TOKEN)
        }
        var selectedCaseId by rememberSaveable {
            mutableStateOf(GoldenStreamCases.find(intent.getStringExtra(EXTRA_CASE_ID).orEmpty()).id)
        }
        var selectedModeId by rememberSaveable {
            mutableStateOf(HarnessMode.find(intent.getStringExtra(EXTRA_MODE).orEmpty()).id)
        }
        var selectedScenarioName by rememberSaveable {
            mutableStateOf(
                Phase1TestScenario.entries.firstOrNull {
                    it.name == intent.getStringExtra(EXTRA_PHASE1_SCENARIO)
                }?.name ?: Phase1TestScenario.DISCONNECT_ONCE.name,
            )
        }
        var autoReplayConsumed by rememberSaveable { mutableStateOf(false) }
        var caseMenuExpanded by remember { mutableStateOf(false) }
        var scenarioMenuExpanded by remember { mutableStateOf(false) }
        val selectedCase = GoldenStreamCases.find(selectedCaseId)
        val selectedMode = HarnessMode.find(selectedModeId)
        val selectedScenario = Phase1TestScenario.valueOf(selectedScenarioName)
        val phase1Snapshot = rememberPhase1UiSnapshot(phase1Client)

        DisposableEffect(Unit) {
            onDispose {
                connectionHolder[0]?.close()
                phase1Holder[0]?.close()
                engine.dispose()
            }
        }

        fun closeNetworkConnection() {
            connectionHolder[0]?.close()
            connectionHolder[0] = null
            phase1Holder[0]?.close()
            phase1Holder[0] = null
            phase1Client = null
        }

        fun replayLocal() {
            if (state is HarnessState.Running) return
            closeNetworkConnection()
            state = HarnessState.Running("正在校验本地 Bundle…")
            scope.launch {
                state = try {
                    val report = withContext(Dispatchers.Default) {
                        val bundle = GoldenStreamAssetLoader.load(assets, selectedCase)
                        GoldenStreamReplayer().replay(bundle, engine)
                    }
                    HarnessState.Complete(
                        HarnessResult(
                            source = "Local APK asset",
                            caseName = report.caseName,
                            eventCount = report.eventCount,
                            outputBytes = report.outputBytes,
                            finalSize = report.finalSize,
                            exitCode = report.exitCode,
                        ),
                    )
                } catch (error: Exception) {
                    HarnessState.Failed(error.message ?: error::class.java.simpleName)
                }
            }
        }

        fun connectLAN() {
            closeNetworkConnection()
            state = HarnessState.Running("正在连接局域网 Docker…")
            connectionHolder[0] = try {
                LanReplayConnection(address, token, engine) { update ->
                    scope.launch {
                        state = when (update) {
                            LanConnectionUpdate.Connecting ->
                                HarnessState.Running("正在建立 WebSocket…")
                            LanConnectionUpdate.Connected ->
                                HarnessState.Running("WebSocket 已连接，等待二进制帧…")
                            is LanConnectionUpdate.Receiving -> HarnessState.Running(
                                "接收 ${update.sequence}/${update.eventCount} · " +
                                    "${update.outputBytes} bytes",
                            )
                            is LanConnectionUpdate.Verified -> HarnessState.Complete(
                                HarnessResult(
                                    source = "LAN Docker WebSocket",
                                    caseName = update.report.caseName,
                                    eventCount = update.report.eventCount,
                                    outputBytes = update.report.outputBytes,
                                    finalSize = update.report.finalSize,
                                    exitCode = update.report.exitCode,
                                ),
                            )
                            is LanConnectionUpdate.Failed ->
                                HarnessState.Failed(update.message)
                        }
                    }
                }
            } catch (error: Exception) {
                state = HarnessState.Failed(error.message ?: "Docker 地址无效")
                null
            }
        }

        fun connectPhase1() {
            closeNetworkConnection()
            engine.clear()
            engine.setReadOnly(true)
            state = HarnessState.Idle
            val client = try {
                Phase1TestSessionClient(
                    engine = engine,
                    config = Phase1TestSessionConfig(
                        rootAddress = address,
                        token = token,
                        scenario = selectedScenario,
                    ),
                    parentScope = scope,
                    disposeEngineOnClose = false,
                )
            } catch (error: Exception) {
                state = HarnessState.Failed(error.message ?: "测试 Relay 地址无效")
                return
            }
            phase1Holder[0] = client
            phase1Client = client
            client.start()
        }

        LaunchedEffect(Unit) {
            if (!autoReplayConsumed &&
                selectedMode == HarnessMode.Phase1 &&
                intent.getBooleanExtra(EXTRA_AUTO_PHASE1_CONNECT, false)
            ) {
                autoReplayConsumed = true
                connectPhase1()
            } else if (!autoReplayConsumed && intent.getBooleanExtra(EXTRA_AUTO_REPLAY, false)) {
                autoReplayConsumed = true
                replayLocal()
            }
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .safeDrawingPadding()
                .padding(horizontal = 16.dp, vertical = 12.dp),
        ) {
            Text("TermLink", style = MaterialTheme.typography.labelLarge, color = Accent)
            Text("Terminal Harness", style = MaterialTheme.typography.titleLarge)
            Spacer(Modifier.height(10.dp))

            SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
                HarnessMode.entries.forEachIndexed { index, mode ->
                    SegmentedButton(
                        selected = selectedMode == mode,
                        onClick = {
                            closeNetworkConnection()
                            selectedModeId = mode.id
                            caseMenuExpanded = false
                            scenarioMenuExpanded = false
                            state = HarnessState.Idle
                        },
                        enabled = state !is HarnessState.Running || selectedMode == HarnessMode.Phase1,
                        shape = SegmentedButtonDefaults.itemShape(
                            index = index,
                            count = HarnessMode.entries.size,
                        ),
                    ) {
                        Text(mode.label)
                    }
                }
            }
            Spacer(Modifier.height(8.dp))

            if (selectedMode == HarnessMode.Local) {
                ExposedDropdownMenuBox(
                    expanded = caseMenuExpanded,
                    onExpandedChange = { expanded ->
                        if (state !is HarnessState.Running) caseMenuExpanded = expanded
                    },
                ) {
                    OutlinedTextField(
                        value = selectedCase.displayName,
                        onValueChange = {},
                        modifier = Modifier
                            .menuAnchor(
                                type = MenuAnchorType.PrimaryNotEditable,
                                enabled = state !is HarnessState.Running,
                            )
                            .fillMaxWidth(),
                        readOnly = true,
                        enabled = state !is HarnessState.Running,
                        label = { Text("本地 Golden Case") },
                        trailingIcon = {
                            ExposedDropdownMenuDefaults.TrailingIcon(expanded = caseMenuExpanded)
                        },
                    )
                    ExposedDropdownMenu(
                        expanded = caseMenuExpanded,
                        onDismissRequest = { caseMenuExpanded = false },
                    ) {
                        GoldenStreamCases.local.forEach { goldenCase ->
                            DropdownMenuItem(
                                text = {
                                    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                        Text(goldenCase.displayName)
                                        Text(
                                            goldenCase.id,
                                            style = MaterialTheme.typography.bodySmall,
                                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                                        )
                                    }
                                },
                                onClick = {
                                    selectedCaseId = goldenCase.id
                                    caseMenuExpanded = false
                                    state = HarnessState.Idle
                                },
                            )
                        }
                    }
                }
                Spacer(Modifier.height(10.dp))
                Button(
                    onClick = ::replayLocal,
                    enabled = state !is HarnessState.Running,
                ) {
                    Text("本地回放")
                }
            } else if (selectedMode == HarnessMode.Lan) {
                RelayConnectionFields(
                    address = address,
                    onAddressChanged = { address = it },
                    token = token,
                    onTokenChanged = { token = it },
                )
                Spacer(Modifier.height(10.dp))
                Button(
                    onClick = ::connectLAN,
                    enabled = state !is HarnessState.Running,
                ) {
                    Text("连接 Docker")
                }
            } else {
                if (phase1Client == null) {
                    RelayConnectionFields(
                        address = address,
                        onAddressChanged = { address = it },
                        token = token,
                        onTokenChanged = { token = it },
                    )
                    Spacer(Modifier.height(6.dp))
                    ExposedDropdownMenuBox(
                        expanded = scenarioMenuExpanded,
                        onExpandedChange = { scenarioMenuExpanded = it },
                    ) {
                        OutlinedTextField(
                            value = selectedScenario.label,
                            onValueChange = {},
                            modifier = Modifier
                                .menuAnchor(type = MenuAnchorType.PrimaryNotEditable, enabled = true)
                                .fillMaxWidth(),
                            readOnly = true,
                            label = { Text("阶段 1 测试场景") },
                            trailingIcon = {
                                ExposedDropdownMenuDefaults.TrailingIcon(
                                    expanded = scenarioMenuExpanded,
                                )
                            },
                        )
                        ExposedDropdownMenu(
                            expanded = scenarioMenuExpanded,
                            onDismissRequest = { scenarioMenuExpanded = false },
                        ) {
                            Phase1TestScenario.entries.forEach { scenario ->
                                DropdownMenuItem(
                                    text = { Text(scenario.label) },
                                    onClick = {
                                        selectedScenarioName = scenario.name
                                        scenarioMenuExpanded = false
                                    },
                                )
                            }
                        }
                    }
                    Spacer(Modifier.height(10.dp))
                    Button(onClick = ::connectPhase1) {
                        Text("连接测试 Relay")
                    }
                } else {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(selectedScenario.label, style = MaterialTheme.typography.labelLarge)
                            Text(
                                address,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                        OutlinedButton(
                            onClick = {
                                closeNetworkConnection()
                                state = HarnessState.Idle
                            },
                        ) {
                            Text("断开")
                        }
                    }
                }
            }
            Spacer(Modifier.height(12.dp))

            if (selectedMode == HarnessMode.Phase1 && state !is HarnessState.Failed) {
                Phase1StatusPanel(
                    client = phase1Client,
                    snapshot = phase1Snapshot,
                    success = Success,
                    warning = Warning,
                    danger = Danger,
                    onRequestControl = {
                        scope.launch {
                            runCatching { phase1Client?.requestControl() }
                                .onFailure {
                                    state = HarnessState.Failed(it.message ?: "控制请求失败")
                                }
                        }
                    },
                    onReleaseControl = {
                        scope.launch {
                            runCatching { phase1Client?.releaseControl() }
                                .onFailure {
                                    state = HarnessState.Failed(it.message ?: "释放控制失败")
                                }
                        }
                    },
                    onSendTestInput = { engine.submitText("phase1-input\n") },
                )
            } else {
                StatusPanel(state, selectedMode, selectedCase)
            }
            Spacer(Modifier.height(12.dp))

            AndroidView(
                factory = { context -> engine.createView(context) },
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .heightIn(min = 220.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(TerminalBackground),
            )
        }
    }

    @Composable
    private fun StatusPanel(
        state: HarnessState,
        selectedMode: HarnessMode,
        selectedCase: GoldenStreamCase,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(8.dp))
                .padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            val status = when (state) {
                HarnessState.Idle -> "Ready"
                is HarnessState.Running -> "Connecting"
                is HarnessState.Complete -> "Verified"
                is HarnessState.Failed -> "Failed"
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(status, color = if (state is HarnessState.Failed) Danger else Success)
                Text(
                    (state as? HarnessState.Complete)?.result?.source ?: selectedMode.source,
                    modifier = Modifier.weight(1f),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            when (state) {
                is HarnessState.Complete -> {
                    Text(
                        state.result.caseName,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Text(
                        "${state.result.eventCount} events · " +
                            "${state.result.outputBytes} bytes · " +
                            "${state.result.finalSize.columns}×${state.result.finalSize.rows} · " +
                            "exit ${state.result.exitCode}",
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                is HarnessState.Running -> Text(
                    state.message,
                    style = MaterialTheme.typography.bodySmall,
                )
                is HarnessState.Failed -> Text(
                    state.message,
                    style = MaterialTheme.typography.bodySmall,
                    color = Danger,
                )
                HarnessState.Idle -> Text(
                    when (selectedMode) {
                        HarnessMode.Local -> "Local · ${selectedCase.id}"
                        HarnessMode.Lan -> "LAN fixed · ${GoldenStreamCases.lanFixed.id}"
                        HarnessMode.Phase1 -> "Phase 1 bidirectional test Relay"
                    },
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
    }

    private data class HarnessResult(
        val source: String,
        val caseName: String,
        val eventCount: Int,
        val outputBytes: Int,
        val finalSize: TerminalSize,
        val exitCode: Int,
    )

    private sealed interface HarnessState {
        data object Idle : HarnessState
        data class Running(val message: String) : HarnessState
        data class Complete(val result: HarnessResult) : HarnessState
        data class Failed(val message: String) : HarnessState
    }

    @Composable
    private fun RelayConnectionFields(
        address: String,
        onAddressChanged: (String) -> Unit,
        token: String,
        onTokenChanged: (String) -> Unit,
    ) {
        OutlinedTextField(
            value = address,
            onValueChange = onAddressChanged,
            modifier = Modifier.fillMaxWidth(),
            label = { Text("Docker 局域网地址") },
            placeholder = { Text("ws://192.168.1.100:8080") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        )
        Spacer(Modifier.height(6.dp))
        OutlinedTextField(
            value = token,
            onValueChange = onTokenChanged,
            modifier = Modifier.fillMaxWidth(),
            label = { Text("测试令牌") },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
        )
    }

    private enum class HarnessMode(
        val id: String,
        val label: String,
        val source: String,
    ) {
        Local("local", "本地", "Local APK asset"),
        Lan("lan", "只读 LAN", "LAN development only"),
        Phase1("phase1", "双向会话", "Phase 1 test Relay"),
        ;

        companion object {
            fun find(id: String): HarnessMode = entries.firstOrNull { it.id == id } ?: Local
        }
    }

    private companion object {
        const val EXTRA_CASE_ID = "dev.termlink.harness.extra.CASE_ID"
        const val EXTRA_AUTO_REPLAY = "dev.termlink.harness.extra.AUTO_REPLAY"
        const val EXTRA_MODE = "dev.termlink.harness.extra.MODE"
        const val EXTRA_RELAY_ADDRESS = "dev.termlink.harness.extra.RELAY_ADDRESS"
        const val EXTRA_TEST_TOKEN = "dev.termlink.harness.extra.TEST_TOKEN"
        const val EXTRA_PHASE1_SCENARIO = "dev.termlink.harness.extra.PHASE1_SCENARIO"
        const val EXTRA_AUTO_PHASE1_CONNECT = "dev.termlink.harness.extra.AUTO_PHASE1_CONNECT"
        const val DEFAULT_LAN_ADDRESS = "ws://192.168.9.28:8080"
        const val DEFAULT_TEST_TOKEN = "termlink-lan-test"
        val Accent = Color(0xFF8D9AFF)
        val Success = Color(0xFF55D69A)
        val Danger = Color(0xFFFF7B83)
        val Warning = Color(0xFFF2C14E)
        val TerminalBackground = Color(0xFF090B10)
        val TERMINAL_THEME = TerminalThemeV1(
            name = "TermLink Quiet Dark",
            foreground = ArgbColor(0xFFE8ECF3.toInt()),
            background = ArgbColor(0xFF090B10.toInt()),
            cursor = ArgbColor(0xFFF1F4F8.toInt()),
            selectionBackground = ArgbColor(0xFF3B4568.toInt()),
            selectionForeground = null,
            ansiColors = listOf(
                0xFF151820, 0xFFFF7B83, 0xFF55D69A, 0xFFF2C14E,
                0xFF7B9CFF, 0xFFC792EA, 0xFF55DDE0, 0xFFE8ECF3,
                0xFF6B7280, 0xFFFF9AA0, 0xFF7CE3B1, 0xFFFFD978,
                0xFFA7BAFF, 0xFFD8A8F2, 0xFF86E8EA, 0xFFFFFFFF,
            ).map { ArgbColor(it.toInt()) },
            boldAsBright = true,
            fontFamilyHint = "Cascadia Mono",
        )
        val termLinkDarkColors = darkColorScheme(
            primary = Accent,
            background = Color(0xFF0D1016),
            surface = Color(0xFF121720),
            surfaceVariant = Color(0xFF1A202B),
            onSurface = Color(0xFFF1F4F8),
            onSurfaceVariant = Color(0xFFA6B0C0),
            error = Danger,
        )
    }
}

private val Phase1TestScenario.label: String
    get() = when (this) {
        Phase1TestScenario.STEADY -> "稳定连接"
        Phase1TestScenario.DISCONNECT_ONCE -> "断线一次并恢复"
        Phase1TestScenario.HISTORY_GAP -> "History Gap"
        Phase1TestScenario.SLOW_CONSUMER -> "慢消费者窗口"
        Phase1TestScenario.EXITED -> "已退出会话"
    }
