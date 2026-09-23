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
        for (fault in listOf("none", "peer", "epoch", "replay")) {
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
                    webSocket.send("""{"type":"welcome","protocol_version":1,"capabilities":["host_session_catalog_v1"]}""")
                }
                override fun onMessage(webSocket: WebSocket, bytes: okio.ByteString) {
                    val envelope = Envelope.parseFrom(bytes.toByteArray())
                    assertEquals(MessageType.SESSION_CATALOG_REQUEST, envelope.messageType)
                    assertEquals(0L, envelope.ack)
                    val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, incoming)
                    val request = try { SessionCatalogRequestPayload.parseFrom(plaintext) } finally { plaintext.fill(0) }
                    val response = catalog(if (fault == "replay") uuid() else request.requestId)
                    val reply = ProductionRealtimeClient.encryptedEnvelope(
                        pcId, if (fault == "peer") UUID.randomUUID() else pcDevice,
                        MessageType.SESSION_CATALOG_RESPONSE, CryptoDirection.PC_TO_ANDROID, envelope.seq, 0,
                        if (fault == "epoch") "epoch-00000002".encodeToByteArray() else keyId,
                        ByteArray(12) { 1 }, outgoing, response.toByteArray(), recipientId = androidId,
                    )
                    webSocket.send(okio.ByteString.of(*reply.toByteArray()))
                }
                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { webSocket.close(code, reason) }
            }))
            server.start()
            val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
            val mux = ProductionRealtimeMultiplexer(server.url("/").toString(), AccessTokenProvider { "test" },
                androidId, identity, scope, optionalAdvertisedCapabilities = setOf("host_session_catalog_v1"),
                allowCleartextForLocalTest = true)
            try {
                val result = runCatching { fetchHostSessionCatalog(mux, pcId, pcDevice, androidId, identity, host.exchange.publicKey, 1, 7) }
                if (fault == "none") assertEquals("INTERRUPTED", result.getOrThrow().sessionsList.single().lifecycle)
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
    private fun uuid() = ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(UUID.randomUUID()))
}
