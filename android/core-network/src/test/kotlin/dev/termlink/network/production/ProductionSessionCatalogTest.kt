package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.SessionCatalogEntry
import dev.termlink.protocol.realtime.v1.SessionCatalogRequestPayload
import dev.termlink.protocol.realtime.v1.SessionCatalogResponsePayload
import dev.termlink.protocol.realtime.v1.SessionCleanupRequestPayload
import dev.termlink.protocol.realtime.v1.SessionCleanupResponsePayload
import dev.termlink.protocol.realtime.v1.SessionCleanupResult
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.*
import org.junit.Test

class ProductionSessionCatalogTest {
    @Test
    fun encryptedDeviceCatalogRoundTripRejectsWrongPeerEpochAndReplayedRequest(): Unit = runBlocking {
        for (cleanup in listOf(false, true)) for (fault in listOf("none", "late", "peer", "epoch", "replay", "capability")) {
            val capability = if (cleanup) "host_session_cleanup_v1" else "host_session_catalog_v1"
            val requestType = if (cleanup) MessageType.SESSION_CLEANUP_REQUEST else MessageType.SESSION_CATALOG_REQUEST
            val responseType = if (cleanup) MessageType.SESSION_CLEANUP_RESPONSE else MessageType.SESSION_CATALOG_RESPONSE
            val sessionId = UUID.randomUUID()
            val pcId = UUID.randomUUID()
            val pcDevice = UUID.randomUUID()
            val androidId = UUID.randomUUID()
            val identity = DeviceIdentity.generate()
            val host = DeviceIdentity.generate()
            val pairwise = SessionCrypto.derivePairwiseKey(host.exchange.privateKey, identity.exchange.publicKey, pcDevice, androidId, 1)
            val keyId = "epoch-00000001".encodeToByteArray()
            val incoming = SessionCrypto.deriveSessionKey(pairwise, pcId, CryptoDirection.ANDROID_TO_PC, keyId)
            val outgoing = SessionCrypto.deriveSessionKey(pairwise, pcId, CryptoDirection.PC_TO_ANDROID, keyId)
            pairwise.fill(0)
            val server = MockWebServer()
            server.enqueue(MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
                override fun onMessage(webSocket: WebSocket, text: String) {
                    webSocket.send("""{"type":"welcome","protocol_version":1,"capabilities":[${if (fault == "capability") "" else "\"$capability\""}]}""")
                }
                override fun onMessage(webSocket: WebSocket, bytes: okio.ByteString) {
                    val envelope = Envelope.parseFrom(bytes.toByteArray())
                    assertEquals(requestType, envelope.messageType)
                    assertEquals(0L, envelope.ack)
                    val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, incoming)
                    val requestId = try {
                        if (cleanup) SessionCleanupRequestPayload.parseFrom(plaintext).also {
                            assertEquals(listOf(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId))), it.sessionIdsList)
                        }.requestId else SessionCatalogRequestPayload.parseFrom(plaintext).requestId
                    } finally { plaintext.fill(0) }
                    val echoedId = if (fault == "replay") uuid() else requestId
                    val response = if (cleanup) cleanupResponse(echoedId, sessionId).toByteArray() else catalog(echoedId).toByteArray()
                    if (fault == "late") {
                        val old = ProductionRealtimeClient.encryptedEnvelope(
                            pcId, pcDevice, responseType, CryptoDirection.PC_TO_ANDROID, envelope.seq - 1, 0,
                            keyId, ByteArray(12) { 2 }, outgoing, response, recipientId = androidId,
                        )
                        webSocket.send(okio.ByteString.of(*old.toByteArray()))
                    }
                    val reply = ProductionRealtimeClient.encryptedEnvelope(
                        pcId, if (fault == "peer") UUID.randomUUID() else pcDevice,
                        responseType, CryptoDirection.PC_TO_ANDROID, envelope.seq, 0,
                        if (fault == "epoch") "epoch-00000002".encodeToByteArray() else keyId,
                        ByteArray(12) { 1 }, outgoing, response, recipientId = androidId,
                    )
                    webSocket.send(okio.ByteString.of(*reply.toByteArray()))
                }
                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { webSocket.close(code, reason) }
            }))
            server.start()
            val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
            val mux = ProductionRealtimeMultiplexer(server.url("/").toString(), AccessTokenProvider { "test" },
                androidId, identity, scope, optionalAdvertisedCapabilities = setOf(capability),
                allowCleartextForLocalTest = true)
            try {
                val result = runCatching {
                    if (cleanup) cleanupHostSessions(mux, pcId, pcDevice, androidId, identity, host.exchange.publicKey, 1, 7, listOf(sessionId)).resultsList.single().status
                    else fetchHostSessionCatalog(mux, pcId, pcDevice, androidId, identity, host.exchange.publicKey, 1, 7).sessionsList.single().lifecycle
                }
                if (fault == "none" || fault == "late") assertEquals(if (cleanup) "DELETED" else "INTERRUPTED", result.getOrThrow())
                else assertTrue("$fault was accepted", result.isFailure)
            } finally {
                mux.close(); scope.cancel(); server.shutdown()
                identity.destroy(); host.destroy(); incoming.fill(0); outgoing.fill(0)
            }
        }
    }

    @Test
    fun completeCatalogRejectsDuplicateIdsAndFalseRunningStatus() {
        val request = uuid()
        val valid = catalog(request)
        validateHostSessionCatalog(valid, request)
        assertThrows(IllegalArgumentException::class.java) {
            validateHostSessionCatalog(valid.toBuilder().addSessions(valid.sessionsList.single()).build(), request)
        }
        assertThrows(IllegalArgumentException::class.java) {
            validateHostSessionCatalog(valid.toBuilder().setSessions(0, valid.sessionsList.single().toBuilder().setLifecycle("RUNNING")).build(), request)
        }
    }

    private fun catalog(request: ByteString) = SessionCatalogResponsePayload.newBuilder()
        .setRequestId(request).setHostInstanceId(uuid()).setObservedAtMs(System.currentTimeMillis())
        .addSessions(SessionCatalogEntry.newBuilder().setSessionId(uuid()).setShellType("pwsh")
            .setLifecycle("INTERRUPTED").setColumns(80).setRows(24).setLastOutputSeq(9)).build()
    private fun cleanupResponse(request: ByteString, id: UUID) = SessionCleanupResponsePayload.newBuilder()
        .setRequestId(request).addResults(SessionCleanupResult.newBuilder()
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(id))).setStatus("DELETED")).build()

    @Test
    fun cleanupRequiresExactlyTheRequestedIdsAndKnownOutcomes() {
        val request = uuid()
        val id = UUID.randomUUID()
        val valid = cleanupResponse(request, id)
        validateHostSessionCleanup(valid, request, listOf(id))
        validateHostSessionCleanup(valid.toBuilder().setErrorCode("MIRROR_SYNC_PENDING").build(), request, listOf(id))
        val entry = valid.resultsList.single()
        for (invalid in listOf(valid.toBuilder().clearResults().build(),
            valid.toBuilder().addResults(entry).build(), cleanupResponse(request, UUID.randomUUID()),
            valid.toBuilder().setResults(0, entry.toBuilder().setStatus("RUNNING")).build(),
            valid.toBuilder().setErrorCode("DENIED").build())) {
            assertThrows(IllegalArgumentException::class.java) { validateHostSessionCleanup(invalid, request, listOf(id)) }
        }
    }
    private fun uuid() = ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(UUID.randomUUID()))
}
