package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.SessionCatalogRequestPayload
import dev.termlink.protocol.realtime.v1.SessionCatalogResponsePayload
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeout

suspend fun fetchHostSessionCatalog(
    multiplexer: ProductionRealtimeMultiplexer,
    pcId: UUID,
    pcDeviceId: UUID,
    androidDeviceId: UUID,
    identity: DeviceIdentity,
    pcExchangePublicKey: ByteArray,
    keyEpoch: Long,
    sequence: Long,
): SessionCatalogResponsePayload = requestHostDevice(
    multiplexer, pcId, pcDeviceId, androidDeviceId, identity, pcExchangePublicKey, keyEpoch, sequence,
    MessageType.SESSION_CATALOG_REQUEST, MessageType.SESSION_CATALOG_RESPONSE, "host_session_catalog_v1", 8_000,
    request = { SessionCatalogRequestPayload.newBuilder().setRequestId(it).build().toByteArray() },
    response = { bytes, id -> SessionCatalogResponsePayload.parseFrom(bytes).also { validateHostSessionCatalog(it, id) } },
)

internal suspend fun <T : Any> requestHostDevice(
    multiplexer: ProductionRealtimeMultiplexer,
    pcId: UUID, pcDeviceId: UUID, androidDeviceId: UUID,
    identity: DeviceIdentity, pcExchangePublicKey: ByteArray, keyEpoch: Long, sequence: Long,
    requestType: MessageType, responseType: MessageType, capability: String, timeoutMillis: Long,
    request: (ByteString) -> ByteArray,
    response: (ByteArray, ByteString) -> T,
): T = withTimeout(timeoutMillis) {
    coroutineScope {
        require(sequence > 0 && keyEpoch > 0 && pcId != UUID(0, 0))
        val keyId = "epoch-${keyEpoch.toString().padStart(8, '0')}".encodeToByteArray()
        val pairwise = SessionCrypto.derivePairwiseKey(identity.exchange.privateKey, pcExchangePublicKey,
            pcDeviceId, androidDeviceId, keyEpoch)
        val outbound = SessionCrypto.deriveSessionKey(pairwise, pcId, CryptoDirection.ANDROID_TO_PC, keyId)
        val inbound = SessionCrypto.deriveSessionKey(pairwise, pcId, CryptoDirection.PC_TO_ANDROID, keyId)
        pairwise.fill(0)
        val requestId = ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(UUID.randomUUID()))
        val requestGeneration = AtomicInteger(0)
        var transport: ProductionRealtimeSessionTransport? = null
        try {
            val endpoint = multiplexer.register(pcId, catalogOnly = true).also { transport = it }
            val pendingResponse = async(start = CoroutineStart.UNDISPATCHED) {
                var result: T? = null
                endpoint.receivedEnvelopes.first { received ->
                    try {
                        val envelope = received.envelope
                        require(received.generation == requestGeneration.get() &&
                            received.generation == endpoint.currentConnectedGeneration()) { "Stale catalog generation" }
                        require(envelope.messageType == responseType &&
                            envelope.direction == Direction.PC_TO_ANDROID && envelope.seq == sequence && envelope.ack == 0L &&
                            envelope.sessionId == ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(pcId)) &&
                            envelope.senderDeviceId == ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(pcDeviceId)) &&
                            envelope.recipientDeviceId == ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(androidDeviceId)) &&
                            envelope.keyId == ByteString.copyFrom(keyId)) { "Invalid catalog response route" }
                        val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, inbound)
                        val value = try { response(plaintext, requestId) } finally { plaintext.fill(0) }
                        result = value
                        true
                    } finally { received.close() }
                }
                requireNotNull(result)
            }
            endpoint.connect()
            endpoint.state.first { it is RealtimeState.Connected }
            check(capability in multiplexer.directRealtimeNegotiatedCapabilities.value.capabilities) {
                "Server upgrade required: $capability is unavailable"
            }
            val payload = request(requestId)
            val envelope = try {
                ProductionRealtimeClient.encryptedEnvelope(
                    sessionId = pcId, senderId = androidDeviceId, recipientId = pcDeviceId,
                    messageType = requestType, direction = CryptoDirection.ANDROID_TO_PC,
                    sequence = sequence, acknowledgement = 0, keyId = keyId,
                    nonce = ByteArray(12).also(SecureRandom()::nextBytes), sessionKey = outbound, plaintext = payload,
                )
            } finally { payload.fill(0) }
            requestGeneration.set(endpoint.currentConnectedGeneration())
            check(endpoint.sendForConnectedGeneration(envelope, requestGeneration.get())) { "Catalog transport unavailable" }
            pendingResponse.await()
        } finally {
            transport?.close()
            outbound.fill(0); inbound.fill(0); keyId.fill(0)
        }
    }
}

internal fun validateHostSessionCatalog(value: SessionCatalogResponsePayload, requestId: ByteString) {
    require(value.requestId == requestId && value.hostInstanceId.size() == 16 &&
        value.hostInstanceId.toByteArray().any { it != 0.toByte() } && value.observedAtMs > 0 &&
        value.sessionsCount <= 4096 && value.errorCode.isEmpty()) { "Host catalog unavailable" }
    val ids = hashSetOf<ByteString>()
    value.sessionsList.forEach { item ->
        require(item.sessionId.size() == 16 && item.sessionId.toByteArray().any { it != 0.toByte() } && ids.add(item.sessionId) &&
            item.lifecycle in setOf("RUNNING", "INTERRUPTED", "EXITED") &&
            item.processAlive == (item.lifecycle == "RUNNING") &&
            item.columns in 1..65535 && item.rows in 1..65535 && item.lastOutputSeq >= 0 &&
            item.createdAtMs >= 0 && item.title.length <= 4096 && item.shellType.length in 1..128) { "Invalid host catalog entry" }
    }
}
