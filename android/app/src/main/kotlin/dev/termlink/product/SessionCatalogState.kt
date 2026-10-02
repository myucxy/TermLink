package dev.termlink.product

import dev.termlink.model.PcSummary
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.session.RemoteSessionState
import java.util.UUID

/** Presentation only: transport failures never rewrite the Windows lifecycle. */
internal enum class SessionDisplayState {
    RUNNING_CONNECTED, RUNNING_UNCONFIRMED, HOST_OFFLINE, INTERRUPTED, EXITED, RECONNECTING, HISTORY_GAP,
}

internal fun resolveSessionDisplayState(
    session: SessionSummary,
    pc: PcSummary?,
    catalogError: String? = null,
    remoteState: RemoteSessionState? = null,
): SessionDisplayState = when {
    pc?.online != true || !pc.remoteRoutingEnabled -> SessionDisplayState.HOST_OFFLINE
    session.state == SessionState.EXITED -> SessionDisplayState.EXITED
    session.state == SessionState.INTERRUPTED -> SessionDisplayState.INTERRUPTED
    remoteState is RemoteSessionState.Exited -> SessionDisplayState.EXITED
    remoteState is RemoteSessionState.HistoryGap -> SessionDisplayState.HISTORY_GAP
    remoteState is RemoteSessionState.Reconnecting -> SessionDisplayState.RECONNECTING
    catalogError != null || !session.online -> SessionDisplayState.RUNNING_UNCONFIRMED
    else -> SessionDisplayState.RUNNING_CONNECTED
}

/** In-memory only, scoped to the authenticated account and Windows device key. */
data class SessionCatalogObservation(
    val deviceId: UUID,
    val keyEpoch: Long,
    val hostInstanceId: UUID,
    val observedAtMillis: Long,
    val retiredHostInstances: Set<UUID> = emptySet(),
)

internal fun advanceSessionCatalogObservation(
    previous: SessionCatalogObservation?,
    incoming: SessionCatalogObservation,
): SessionCatalogObservation {
    require(incoming.hostInstanceId != UUID(0, 0) && incoming.observedAtMillis > 0)
    if (previous == null || previous.deviceId != incoming.deviceId || previous.keyEpoch != incoming.keyEpoch) {
        return incoming
    }
    require(incoming.hostInstanceId !in previous.retiredHostInstances) { "Retired host catalog" }
    if (previous.hostInstanceId == incoming.hostInstanceId) {
        require(incoming.observedAtMillis >= previous.observedAtMillis) { "Stale host catalog" }
        return incoming.copy(retiredHostInstances = previous.retiredHostInstances)
    }
    // Wall clocks are not comparable across process instances (or a clock correction).
    return incoming.copy(retiredHostInstances = previous.retiredHostInstances + previous.hostInstanceId)
}

internal fun changedSessionCatalogPcIds(
    previous: Map<UUID, SessionCatalogObservation>,
    incoming: Map<UUID, SessionCatalogObservation>,
): Set<UUID> = incoming.filter { (id, observation) ->
    previous[id]?.let {
        it.deviceId != observation.deviceId || it.keyEpoch != observation.keyEpoch ||
            it.hostInstanceId != observation.hostInstanceId
    } == true
}.keys
