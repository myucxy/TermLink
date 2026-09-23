package dev.termlink.product

import dev.termlink.model.DeviceTrustState
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.SessionState
import dev.termlink.model.isPcAgent
import java.util.UUID
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

internal fun isInactiveSession(state: SessionState, online: Boolean): Boolean =
    !online && (state == SessionState.EXITED || state == SessionState.INTERRUPTED)

internal fun ProductSnapshot.inactiveSessionIds(pcId: UUID): List<UUID> {
    if (!signedIn || !isConnected() || pcId in pcSessionCatalogErrors) return emptyList()
    val pc = pcs.firstOrNull { it.id == pcId } ?: return emptyList()
    if (!pc.online || !pc.remoteRoutingEnabled ||
        PcCapabilityIds.HOST_HISTORY_V1 !in pc.capabilities ||
        PcCapabilityIds.HOST_SESSION_CATALOG_V1 !in pc.capabilities ||
        devices.none { it.id == pc.deviceId && it.type.isPcAgent() && it.trustState == DeviceTrustState.TRUSTED }
    ) return emptyList()
    return sessions.filter {
        it.pcId == pcId && isInactiveSession(it.state, it.online)
    }.map { it.id }.distinct()
}

internal data class InactiveSessionCleanupResult(
    val deleted: Int = 0,
    val failed: Int = 0,
    val skipped: Int = 0,
) {
    val processed: Int get() = deleted + failed + skipped
}

/** Only the UUIDs shown in the confirmation are eligible; newly ended sessions wait for another request. */
internal suspend fun ProductRepository.deleteInactiveSessions(
    pcId: UUID,
    sessionIds: List<UUID>,
    onDeleted: (UUID) -> Unit,
    onProgress: (InactiveSessionCleanupResult) -> Unit,
): InactiveSessionCleanupResult {
    var result = InactiveSessionCleanupResult()
    for (sessionId in sessionIds.distinct()) {
        currentCoroutineContext().ensureActive()
        if (sessionId !in snapshot.value.inactiveSessionIds(pcId)) {
            result = result.copy(skipped = result.skipped + 1)
        } else {
            val deleted = try {
                deleteSession(sessionId)
                true
            } catch (_: Exception) {
                // Request-local timeouts count as failures; caller cancellation stops the batch.
                currentCoroutineContext().ensureActive()
                false
            }
            if (deleted) {
                onDeleted(sessionId)
                result = result.copy(deleted = result.deleted + 1)
            } else {
                result = result.copy(failed = result.failed + 1)
            }
        }
        onProgress(result)
    }
    return result
}
