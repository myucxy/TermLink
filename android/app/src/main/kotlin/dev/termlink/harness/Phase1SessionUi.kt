package dev.termlink.harness

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.padding
import dev.termlink.network.testrelay.AcceptedInputStatus
import dev.termlink.network.testrelay.Phase1TestSessionClient
import dev.termlink.network.testrelay.Phase1TransportState
import dev.termlink.session.RemoteSessionState
import kotlinx.coroutines.flow.combine

internal data class Phase1UiSnapshot(
    val transport: Phase1TransportState,
    val session: RemoteSessionState,
    val acceptedInputs: AcceptedInputStatus,
    val flowControlWindow: Int?,
)

@Composable
internal fun rememberPhase1UiSnapshot(client: Phase1TestSessionClient?): Phase1UiSnapshot? {
    var snapshot by remember { mutableStateOf<Phase1UiSnapshot?>(null) }
    LaunchedEffect(client) {
        snapshot = null
        if (client == null) return@LaunchedEffect
        combine(
            client.transportState,
            client.sessionState,
            client.acceptedInputs,
            client.flowControlWindow,
        ) { transport, session, acceptedInputs, flowControlWindow ->
            Phase1UiSnapshot(transport, session, acceptedInputs, flowControlWindow)
        }.collect { snapshot = it }
    }
    return snapshot
}

@Composable
internal fun Phase1StatusPanel(
    client: Phase1TestSessionClient?,
    snapshot: Phase1UiSnapshot?,
    success: Color,
    warning: Color,
    danger: Color,
    onRequestControl: () -> Unit,
    onReleaseControl: () -> Unit,
    onSendTestInput: () -> Unit,
) {
    val state = snapshot?.session
    val (statusLabel, statusColor) = when (state) {
        is RemoteSessionState.Live -> "Live" to success
        is RemoteSessionState.Reconnecting -> "Reconnecting" to warning
        is RemoteSessionState.ReadOnly -> "Read only" to MaterialTheme.colorScheme.onSurfaceVariant
        is RemoteSessionState.Controlling -> "Controlling" to success
        is RemoteSessionState.Exited -> "Exited" to MaterialTheme.colorScheme.onSurfaceVariant
        is RemoteSessionState.HistoryGap -> "History gap" to warning
        null -> "Ready" to MaterialTheme.colorScheme.onSurfaceVariant
    }
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(8.dp))
            .padding(14.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Text(statusLabel, color = statusColor)
            Text(
                "TEST RELAY · NO E2EE",
                style = MaterialTheme.typography.labelSmall,
                color = warning,
            )
        }
        Text(
            transportLabel(snapshot?.transport),
            style = MaterialTheme.typography.bodySmall,
            color = transportColor(snapshot?.transport, success, warning, danger),
        )
        when (state) {
            is RemoteSessionState.Reconnecting -> state.reason?.let {
                Text(it, style = MaterialTheme.typography.bodySmall)
            }
            is RemoteSessionState.ReadOnly -> Text(
                "${state.reason} · ACK ${state.lastAckedSequence}",
                style = MaterialTheme.typography.bodySmall,
            )
            is RemoteSessionState.Controlling -> Text(
                "Lease ${state.leaseId} · ACK ${state.lastAckedSequence}",
                style = MaterialTheme.typography.bodySmall,
            )
            is RemoteSessionState.Exited -> Text(
                "ACK ${state.lastAckedSequence} · exit ${state.exitCode}",
                style = MaterialTheme.typography.bodySmall,
            )
            is RemoteSessionState.HistoryGap -> Text(
                "ACK ${state.lastAckedSequence} · missing ${state.expectedSequence}..${state.receivedSequence - 1}",
                style = MaterialTheme.typography.bodySmall,
                color = warning,
            )
            is RemoteSessionState.Live -> Text(
                "ACK ${state.lastAckedSequence}",
                style = MaterialTheme.typography.bodySmall,
            )
            null -> Text(
                "选择场景并连接 Docker 测试 Relay",
                style = MaterialTheme.typography.bodySmall,
            )
        }
        snapshot?.let {
            val flow = it.flowControlWindow?.let { window -> " · window $window" }.orEmpty()
            Text(
                "Input accepted ${it.acceptedInputs.count}$flow",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        if (client != null) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                when (state) {
                    is RemoteSessionState.Controlling -> OutlinedButton(onClick = onReleaseControl) {
                        Text("释放控制")
                    }
                    is RemoteSessionState.Live,
                    is RemoteSessionState.ReadOnly,
                    -> Button(onClick = onRequestControl) {
                        Text("申请控制")
                    }
                    else -> Button(onClick = {}, enabled = false) {
                        Text("申请控制")
                    }
                }
                OutlinedButton(
                    onClick = onSendTestInput,
                    enabled = state is RemoteSessionState.Controlling,
                ) {
                    Text("发送测试输入")
                }
            }
        }
    }
}

private fun transportLabel(state: Phase1TransportState?): String = when (state) {
    Phase1TransportState.Connecting -> "WebSocket connecting"
    Phase1TransportState.Connected -> "WebSocket connected"
    is Phase1TransportState.Reconnecting -> "Reconnect attempt ${state.attempt}"
    is Phase1TransportState.Failed -> state.reason
    Phase1TransportState.Closed -> "WebSocket closed"
    null -> "Phase 1 bidirectional fixture"
}

@Composable
private fun transportColor(
    state: Phase1TransportState?,
    success: Color,
    warning: Color,
    danger: Color,
): Color = when (state) {
    Phase1TransportState.Connected -> success
    is Phase1TransportState.Reconnecting,
    Phase1TransportState.Connecting,
    -> warning
    is Phase1TransportState.Failed -> danger
    else -> MaterialTheme.colorScheme.onSurfaceVariant
}
