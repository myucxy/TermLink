package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.SessionCleanupRequestPayload
import dev.termlink.protocol.realtime.v1.SessionCleanupResponsePayload
import java.util.UUID

suspend fun cleanupHostSessions(
    multiplexer: ProductionRealtimeMultiplexer, pcId: UUID, pcDeviceId: UUID, androidDeviceId: UUID,
    identity: DeviceIdentity, pcExchangePublicKey: ByteArray, keyEpoch: Long, sequence: Long,
    sessionIds: List<UUID>,
): SessionCleanupResponsePayload {
    require(sessionIds.size in 1..64 && sessionIds.distinct().size == sessionIds.size && sessionIds.none { it == UUID(0, 0) })
    return requestHostDevice(
        multiplexer, pcId, pcDeviceId, androidDeviceId, identity, pcExchangePublicKey, keyEpoch, sequence,
        MessageType.SESSION_CLEANUP_REQUEST, MessageType.SESSION_CLEANUP_RESPONSE, "host_session_cleanup_v1", 30_000,
        request = { id -> SessionCleanupRequestPayload.newBuilder().setRequestId(id)
            .addAllSessionIds(sessionIds.map { ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(it)) }).build().toByteArray() },
        response = { bytes, id -> SessionCleanupResponsePayload.parseFrom(bytes).also { validateHostSessionCleanup(it, id, sessionIds) } },
    )
}

internal fun validateHostSessionCleanup(value: SessionCleanupResponsePayload, requestId: ByteString, expected: List<UUID>) {
    require(value.requestId == requestId && value.errorCode in setOf("", "MIRROR_SYNC_PENDING")) { "Invalid cleanup response" }
    val ids = value.resultsList.map { ProductionRealtimeClient.uuidFromBytes(it.sessionId.toByteArray()) }
    require(ids.size == expected.size && ids.toSet() == expected.toSet()) { "Incomplete cleanup results" }
    require(value.resultsList.all { it.status in setOf("DELETED", "SKIPPED_ACTIVE", "NOT_FOUND", "FAILED") }) { "Unknown cleanup result" }
}
