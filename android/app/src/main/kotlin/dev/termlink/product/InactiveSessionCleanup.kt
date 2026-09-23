package dev.termlink.product

import dev.termlink.model.DeviceTrustState
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.SessionState
import dev.termlink.model.isPcAgent
import java.util.UUID
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext

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
    val interrupted: Boolean = false,
    val upgradeRequired: Boolean = false,
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
    val confirmed = sessionIds.distinct()
    for (batch in confirmed.chunked(64)) {
        currentCoroutineContext().ensureActive()
        val eligible = snapshot.value.inactiveSessionIds(pcId).toSet()
        val selected = batch.filter { it in eligible }
        result = result.copy(skipped = result.skipped + batch.size - selected.size)
        if (selected.isNotEmpty()) {
            // Cancellation stops subsequent batches; settle this bounded in-flight batch first.
            val outcomes = withContext(NonCancellable) {
                try { deleteInactiveSessionBatch(pcId, selected) }
                catch (_: dev.termlink.network.production.HostDeviceUpgradeRequiredException) {
                    result = result.copy(upgradeRequired = true)
                    null
                }
                catch (_: Exception) { null }
            }
            if (outcomes == null) {
                result = result.copy(failed = result.failed + selected.size,
                    skipped = result.skipped + confirmed.size - result.processed - selected.size,
                    interrupted = true)
                onProgress(result)
                return result
            }
            for (id in selected) {
                when (outcomes[id]) {
                    "DELETED" -> { onDeleted(id); result = result.copy(deleted = result.deleted + 1) }
                    "SKIPPED_ACTIVE", "NOT_FOUND" -> result = result.copy(skipped = result.skipped + 1)
                    else -> result = result.copy(failed = result.failed + 1)
                }
            }
        }
        onProgress(result)
    }
    return result
}
