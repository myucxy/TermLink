package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.protocol.transport.v1.DirectCarrierV1
import dev.termlink.protocol.transport.v1.DirectPathTicketV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.security.SecureRandom
import java.util.Base64
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString as OkioByteString
import org.bouncycastle.crypto.params.Ed25519PrivateKeyParameters
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectTransportSignalingClientTest {
    private val now = 1_900_000_000_000L

    @Test
    fun featureOffPerformsNoHttpOrWebSocketWork() {
        val server = MockWebServer().also(MockWebServer::start)
        val fixture = fixture(server)
        try {
            assertEquals(DirectSignalingCallResult.Disabled, fixture.client.fetchConfig())
            assertFalse(fixture.client.startSignaling())
            assertEquals(0, server.requestCount)
            assertEquals(DirectTransportSignalingState.Disabled, fixture.client.state.value)
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun configRequiresShadowCapabilitiesAndForbidsDirectRouting() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val valid = configJson(signing)
        server.enqueue(MockResponse().setResponseCode(200).setBody(valid))
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                valid.replace("\"direct_routing_permitted\":false", "\"direct_routing_permitted\":true"),
            ),
        )
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                valid.replace("direct_transport_shadow_v1", "direct_transport_v1"),
            ),
        )
        server.start()
        val fixture = fixture(server, enabled = true)
        try {
            val accepted = fixture.client.fetchConfig()
            assertTrue(accepted is DirectSignalingCallResult.Success)
            assertFalse((accepted as DirectSignalingCallResult.Success).value.directRoutingPermitted)
            repeat(2) {
                val rejected = fixture.client.fetchConfig()
                assertEquals(
                    DirectSignalingFaultCode.INVALID_CONFIGURATION,
                    (rejected as DirectSignalingCallResult.Fault).fault.code,
                )
            }
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun httpControlUsesCanonicalDeviceProofAndSignalUsesIndependentProof() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val signalOpened = CompletableDeferred<Unit>()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path) {
                DirectTransportSignalingClient.CONFIG_PATH -> MockResponse()
                    .setResponseCode(200)
                    .setBody(configJson(signing))
                DirectTransportSignalingClient.SIGNAL_PATH -> MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            signalOpened.complete(Unit)
                            webSocket.send(validWelcome())
                        }

                        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                            webSocket.close(code, reason)
                        }
                    },
                )
                else -> MockResponse().setResponseCode(404)
            }
        }
        server.start()
        val fixture = fixture(server, enabled = true)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            val configRequest = server.takeRequest(5, TimeUnit.SECONDS)
            assertNotNull(configRequest)
            assertProof(
                checkNotNull(configRequest),
                fixture.identity.signingPublicKey,
                DirectTransportSignalingProof.DEVICE_PROOF_DOMAIN,
                DirectTransportSignalingClient.CONFIG_PATH,
            )

            assertTrue(fixture.client.startSignaling())
            withTimeout(5_000) { signalOpened.await() }
            val signalRequest = server.takeRequest(5, TimeUnit.SECONDS)
            assertNotNull(signalRequest)
            assertProof(
                checkNotNull(signalRequest),
                fixture.identity.signingPublicKey,
                DirectTransportSignalingProof.SIGNAL_PROOF_DOMAIN,
                DirectTransportSignalingClient.SIGNAL_PATH,
            )
            assertTrue(
                fixture.client.state.first { it is DirectTransportSignalingState.Connected } is
                    DirectTransportSignalingState.Connected,
            )
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun multipairIsAdvertisedAndTypesElevenToFourteenAreAcceptedOnlyAfterV4Config() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val hello = CompletableDeferred<String>()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path) {
                DirectTransportSignalingClient.CONFIG_PATH -> MockResponse()
                    .setResponseCode(200)
                    .setBody(configJson(signing, multipair = true))
                DirectTransportSignalingClient.SIGNAL_PATH -> MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            hello.complete(text)
                            webSocket.send(validWelcome(multipair = true))
                        }

                        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                            webSocket.close(code, reason)
                        }
                    },
                )
                else -> MockResponse().setResponseCode(404)
            }
        }
        server.start()
        val local = UUID.randomUUID()
        val peer = UUID.randomUUID()
        val fixture = fixture(server, enabled = true, deviceId = local, multipair = true)
        try {
            val config = fixture.client.fetchConfig() as DirectSignalingCallResult.Success
            assertTrue(DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY in config.value.capabilities)
            assertTrue(fixture.client.multipairV2Active)
            assertTrue(fixture.client.startSignaling())
            assertTrue(withTimeout(5_000) { hello.await() }.contains(
                DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY,
            ))
            fixture.client.state.first { it is DirectTransportSignalingState.Connected }
            val connection = UUID.randomUUID()
            for (type in listOf(
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2,
            )) {
                val sequence = (type.number - 10).toLong()
                assertTrue(
                    fixture.client.send(
                        signal(connection, local, peer, sequence).toBuilder().setSignalType(type).build(),
                    ),
                )
            }
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun negotiatedMultipairSocketDeliversInboundTypesElevenToFourteen() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        val expected = listOf(
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2,
        )
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path) {
                DirectTransportSignalingClient.CONFIG_PATH -> MockResponse()
                    .setResponseCode(200)
                    .setBody(configJson(signing, multipair = true))
                DirectTransportSignalingClient.SIGNAL_PATH -> MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            webSocket.send(validWelcome(multipair = true))
                            expected.forEachIndexed { index, type ->
                                val envelope = signal(
                                    connectionId,
                                    pcId,
                                    androidId,
                                    sequence = (index + 1).toLong(),
                                ).toBuilder().setSignalType(type).build()
                                webSocket.send(OkioByteString.of(*envelope.toByteArray()))
                            }
                        }

                        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                            webSocket.close(code, reason)
                        }
                    },
                )
                else -> MockResponse().setResponseCode(404)
            }
        }
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = androidId, multipair = true)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            val received = async(start = CoroutineStart.UNDISPATCHED) {
                fixture.client.incoming.take(expected.size).toList()
            }
            assertTrue(fixture.client.startSignaling())
            assertEquals(expected, withTimeout(5_000) { received.await() }.map { it.signalType })
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun frozenV3SocketRejectsEveryMultipairOnlyInboundType() = runBlocking {
        val types = listOf(
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2,
        )
        for (type in types) {
            val server = MockWebServer()
            val androidId = UUID.randomUUID()
            val pcId = UUID.randomUUID()
            val connectionId = UUID.randomUUID()
            server.enqueue(
                MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            webSocket.send(validWelcome())
                            val envelope = signal(connectionId, pcId, androidId, sequence = 1)
                                .toBuilder().setSignalType(type).build()
                            webSocket.send(OkioByteString.of(*envelope.toByteArray()))
                        }
                    },
                ),
            )
            server.start()
            val fixture = fixture(server, enabled = true, deviceId = androidId, multipair = false)
            try {
                assertTrue(fixture.client.startSignaling())
                val fault = withTimeout(5_000) {
                    fixture.client.state.first {
                        it is DirectTransportSignalingState.Fault &&
                            it.fault.code == DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED
                    }
                } as DirectTransportSignalingState.Fault
                assertFalse("legacy socket retried rejected $type", fault.willRetry)
            } finally {
                fixture.close()
                server.shutdown()
            }
        }
    }

    @Test
    fun multipairSocketRejectsUnknownTypeAboveFourteen() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path) {
                DirectTransportSignalingClient.CONFIG_PATH -> MockResponse()
                    .setResponseCode(200)
                    .setBody(configJson(signing, multipair = true))
                DirectTransportSignalingClient.SIGNAL_PATH -> MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            webSocket.send(validWelcome(multipair = true))
                            val envelope = signal(connectionId, pcId, androidId, sequence = 1)
                                .toBuilder().setSignalTypeValue(15).build()
                            webSocket.send(OkioByteString.of(*envelope.toByteArray()))
                        }
                    },
                )
                else -> MockResponse().setResponseCode(404)
            }
        }
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = androidId, multipair = true)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            assertTrue(fixture.client.startSignaling())
            val fault = withTimeout(5_000) {
                fixture.client.state.first {
                    it is DirectTransportSignalingState.Fault &&
                        it.fault.code == DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED
                }
            } as DirectTransportSignalingState.Fault
            assertFalse(fault.willRetry)
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun refreshedConfigRevokesPreviouslyPublishedMultipairCapability() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing, multipair = true)))
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing, multipair = false)))
        server.start()
        val fixture = fixture(server, enabled = true, multipair = true)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            assertTrue(fixture.client.multipairV2Active)
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            assertFalse(fixture.client.multipairV2Active)
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun serverWideMultipairConfigDoesNotMakeFrozenV3AdvertiseOrSendV2() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing, multipair = true)))
        server.start()
        val local = UUID.randomUUID()
        val peer = UUID.randomUUID()
        val fixture = fixture(server, enabled = true, deviceId = local, multipair = false)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            assertFalse(fixture.client.multipairV2Active)
            assertFalse(
                fixture.client.send(
                    signal(UUID.randomUUID(), local, peer, 1).toBuilder()
                        .setSignalType(
                            TransportSignalTypeV1
                                .TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
                        )
                        .build(),
                ),
            )
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun ticketRequestIsCanonicalAndTicketValidationIsStrict() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val deviceId = UUID.randomUUID()
        val targetId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        val validTicket = signing.ticket(deviceId, targetId, connectionId, now)
        val tamperedTicket = validTicket.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }
        val wrongConnectionTicket = signing.ticket(deviceId, targetId, UUID.randomUUID(), now)
        val expiredTicket = signing.ticket(deviceId, targetId, connectionId, now - 120_001)
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(validTicket)))
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(tamperedTicket)))
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(wrongConnectionTicket)))
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(expiredTicket)))
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = deviceId)
        val config = parsedConfig(signing)
        try {
            val accepted = fixture.client.issueTicket(targetId, connectionId, config)
            assertTrue(accepted is DirectSignalingCallResult.Success)
            val ticket = (accepted as DirectSignalingCallResult.Success).value
            assertArrayEquals(validTicket, ticket.wireBytes)

            val request = checkNotNull(server.takeRequest(5, TimeUnit.SECONDS))
            assertEquals(DirectTransportSignalingClient.TICKET_PATH, request.path)
            assertEquals(
                "{\"target_device_id\":\"$targetId\",\"connection_id\":\"$connectionId\",\"requested_carriers\":[\"UDP_QUIC\"]}",
                request.body.clone().readUtf8(),
            )
            assertProof(
                request,
                fixture.identity.signingPublicKey,
                DirectTransportSignalingProof.DEVICE_PROOF_DOMAIN,
                DirectTransportSignalingClient.TICKET_PATH,
            )

            val rejected = fixture.client.issueTicket(targetId, connectionId, config)
            assertEquals(
                DirectSignalingFaultCode.TICKET_REJECTED,
                (rejected as DirectSignalingCallResult.Fault).fault.code,
            )
            repeat(2) {
                val strictRejection = fixture.client.issueTicket(targetId, connectionId, config)
                assertEquals(
                    DirectSignalingFaultCode.TICKET_REJECTED,
                    (strictRejection as DirectSignalingCallResult.Fault).fault.code,
                )
            }
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun incomingSignalSequenceReplayAndOversizeAreFenced() = runBlocking {
        val server = MockWebServer()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        val signal = signal(connectionId, pcId, androidId, sequence = 1)
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        webSocket.send(validWelcome())
                        val bytes = OkioByteString.of(*signal.toByteArray())
                        webSocket.send(bytes)
                        webSocket.send(bytes)
                    }
                },
            ),
        )
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = androidId)
        try {
            val received = async(start = CoroutineStart.UNDISPATCHED) {
                fixture.client.incoming.first()
            }
            fixture.client.startSignaling()
            assertEquals(1L, withTimeout(5_000) { received.await() }.signalSeq)
            val fault = withTimeout(5_000) {
                fixture.client.state.first {
                    it is DirectTransportSignalingState.Fault &&
                        it.fault.code == DirectSignalingFaultCode.SIGNAL_REPLAYED
                }
            } as DirectTransportSignalingState.Fault
            assertFalse(fault.willRetry)

            val oversized = signal.toBuilder()
                .setSignalSeq(2)
                .setCiphertext(ByteString.copyFrom(ByteArray(DirectTransportSignalingClient.MAX_SIGNAL_FRAME_BYTES)))
                .build()
            assertFalse(fixture.client.send(oversized))
            assertTrue(
                fixture.client.state.value is DirectTransportSignalingState.Fault,
            )
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun signalSequenceMustStartAtOneAndAdvanceExactly() = runBlocking {
        val inboundServer = MockWebServer()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        inboundServer.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        webSocket.send(validWelcome())
                        webSocket.send(
                            OkioByteString.of(
                                *signal(connectionId, pcId, androidId, sequence = 2).toByteArray(),
                            ),
                        )
                    }
                },
            ),
        )
        inboundServer.start()
        val inbound = fixture(inboundServer, enabled = true, deviceId = androidId)
        try {
            inbound.client.startSignaling()
            val fault = withTimeout(5_000) {
                inbound.client.state.first {
                    it is DirectTransportSignalingState.Fault &&
                        it.fault.code == DirectSignalingFaultCode.SIGNAL_REPLAYED
                }
            } as DirectTransportSignalingState.Fault
            assertFalse(fault.willRetry)
        } finally {
            inbound.close()
            inboundServer.shutdown()
        }

        val outboundServer = MockWebServer()
        outboundServer.enqueue(
            MockResponse().withWebSocketUpgrade(object : WebSocketListener() {}),
        )
        outboundServer.start()
        val outbound = fixture(outboundServer, enabled = true, deviceId = androidId)
        try {
            outbound.client.startSignaling()
            assertTrue(outbound.client.send(signal(connectionId, androidId, pcId, sequence = 1)))
            assertFalse(outbound.client.send(signal(connectionId, androidId, pcId, sequence = 3)))
            val fault = outbound.client.state.value as DirectTransportSignalingState.Fault
            assertEquals(DirectSignalingFaultCode.SIGNAL_REPLAYED, fault.fault.code)
            assertFalse(fault.willRetry)
        } finally {
            outbound.close()
            outboundServer.shutdown()
        }
    }

    @Test
    fun outboundQueueOverflowClosesOnlySignaling() {
        val server = MockWebServer()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    // Deliberately never Welcome, retaining the bounded pre-Welcome queue.
                },
            ),
        )
        server.start()
        val relaySentinel = AtomicBoolean(true)
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        val fixture = fixture(server, enabled = true, deviceId = androidId)
        try {
            fixture.client.startSignaling()
            repeat(DirectTransportSignalingClient.MAX_QUEUED_SIGNAL_FRAMES) { index ->
                assertTrue(fixture.client.send(signal(connectionId, androidId, pcId, (index + 1).toLong())))
            }
            assertFalse(
                fixture.client.send(
                    signal(
                        connectionId,
                        androidId,
                        pcId,
                        (DirectTransportSignalingClient.MAX_QUEUED_SIGNAL_FRAMES + 1).toLong(),
                    ),
                ),
            )
            val fault = fixture.client.state.value as DirectTransportSignalingState.Fault
            assertEquals(DirectSignalingFaultCode.SIGNAL_QUEUE_OVERFLOW, fault.fault.code)
            assertFalse(fault.willRetry)
            assertTrue("signalling overflow must not touch realtime", relaySentinel.get())
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun http503AndSignalTimeoutNeverTouchRelaySentinel() = runBlocking {
        val server = MockWebServer()
        val relaySentinel = AtomicBoolean(true)
        server.enqueue(MockResponse().setResponseCode(503).setBody("{}"))
        server.enqueue(MockResponse().setSocketPolicy(okhttp3.mockwebserver.SocketPolicy.NO_RESPONSE))
        server.start()
        val retryWaits = CopyOnWriteArrayList<Long>()
        val fixture = fixture(
            server,
            enabled = true,
            retryDelay = DirectSignalingRetryDelay { retryWaits += it },
        )
        try {
            val response = fixture.client.fetchConfig()
            val httpFault = response as DirectSignalingCallResult.Fault
            assertEquals(DirectSignalingFaultCode.HTTP_UNAVAILABLE, httpFault.fault.code)
            assertEquals(503, httpFault.fault.httpStatus)
            assertTrue(relaySentinel.get())

            fixture.client.startSignaling()
            val fault = withTimeout(15_000) {
                fixture.client.state.first { it is DirectTransportSignalingState.Fault }
            } as DirectTransportSignalingState.Fault
            assertEquals(DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED, fault.fault.code)
            assertTrue(relaySentinel.get())
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    private fun fixture(
        server: MockWebServer,
        enabled: Boolean = false,
        deviceId: UUID = UUID.randomUUID(),
        retryDelay: DirectSignalingRetryDelay = DirectSignalingRetryDelay { },
        multipair: Boolean = false,
    ): ClientFixture {
        val identity = DeviceIdentity.generate()
        val client = DirectTransportSignalingClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "access-token" },
            deviceId = deviceId,
            identity = identity,
            role = "android",
            ticketSignatureVerifier = bouncyTicketVerifier(),
            feature = DirectTransportSignalingFeature(enabled),
            multipairV2Enabled = multipair,
            clockMillis = { now },
            random = FixedSecureRandom(),
            retryDelay = retryDelay,
            retryJitter = DirectSignalingRetryJitter { 0 },
            signalHandshakeTimeoutMillis = 100,
            allowCleartextForLocalTest = true,
        )
        return ClientFixture(client, identity)
    }

    private fun parsedConfig(signing: TicketSigningFixture): DirectTransportServerConfig =
        DirectTransportServerConfig(
            protocolVersion = 1,
            capabilities = setOf("direct_signaling_v1", "direct_transport_shadow_v1"),
            directRoutingPermitted = false,
            ticketSigningKeys = mapOf(
                signing.keyId to DirectTicketSigningKey(signing.keyId, signing.publicKey),
            ),
            stunServers = listOf("stun:one.test:3478"),
        )

    private fun configJson(signing: TicketSigningFixture, multipair: Boolean = false): String {
        val optional = if (multipair) ",\"direct_transport_shadow_multipair_v2\"" else ""
        return """{"protocol_version":1,"capabilities":["direct_signaling_v1","direct_transport_shadow_v1"$optional],"direct_routing_permitted":false,"ticket_signing_keys":[{"key_id":"${signing.keyId}","algorithm":"Ed25519","public_key":"${signing.publicKey.base64Url()}"}],"stun_servers":["stun:one.test:3478"]}"""
    }

    private fun ticketResponse(ticket: ByteArray): String =
        """{"protocol_version":1,"ticket_protobuf":"${ticket.base64Url()}"}"""

    private fun validWelcome(multipair: Boolean = false): String {
        val optional = if (multipair) ",\"direct_transport_shadow_multipair_v2\"" else ""
        return """{"type":"SERVER_WELCOME","protocol_version":1,"capabilities":["direct_transport_shadow_v1"$optional],"heartbeat_interval_ms":15000,"max_signal_frame_bytes":65536}"""
    }

    private fun signal(
        connectionId: UUID,
        senderId: UUID,
        recipientId: UUID,
        sequence: Long,
    ): TransportSignalEnvelopeV1 = TransportSignalEnvelopeV1.newBuilder()
        .setProtocolVersion(1)
        .setSignalType(TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_OFFER)
        .setConnectionId(ByteString.copyFrom(connectionId.bytes()))
        .setSenderDeviceId(ByteString.copyFrom(senderId.bytes()))
        .setRecipientDeviceId(ByteString.copyFrom(recipientId.bytes()))
        .setPathEpoch(1)
        .setSignalSeq(sequence)
        .setSignalAck(0)
        .setSentAtMs(now)
        .setKeyId(ByteString.copyFromUtf8("direct-epoch-1"))
        .setNonce(ByteString.copyFrom(ByteArray(12) { sequence.toByte() }))
        .setCiphertext(ByteString.copyFrom(ByteArray(32) { 0x5a }))
        .build()

    private fun assertProof(
        request: RecordedRequest,
        publicKey: ByteArray,
        expectedDomain: String,
        expectedPath: String,
    ) {
        assertEquals(expectedPath, request.path)
        assertEquals("Bearer access-token", request.getHeader("Authorization"))
        val timestamp = checkNotNull(request.getHeader("X-TermLink-Timestamp")).toLong()
        val nonce = checkNotNull(request.getHeader("X-TermLink-Nonce"))
        val signature = checkNotNull(request.getHeader("X-TermLink-Signature")).decodeBase64Url()
        val canonical = DirectTransportSignalingProof.canonical(
            expectedDomain,
            request.method ?: "GET",
            expectedPath,
            timestamp,
            nonce,
            request.body.clone().readByteArray(),
        )
        assertTrue(verify(publicKey, canonical, signature))
    }

    private fun bouncyTicketVerifier(): DirectSignalingTicketSignatureVerifier =
        DirectSignalingTicketSignatureVerifier(::verify)

    private fun verify(publicKey: ByteArray, message: ByteArray, signature: ByteArray): Boolean {
        val verifier = Ed25519Signer()
        verifier.init(false, Ed25519PublicKeyParameters(publicKey, 0))
        verifier.update(message, 0, message.size)
        return verifier.verifySignature(signature)
    }

    private data class ClientFixture(
        val client: DirectTransportSignalingClient,
        val identity: DeviceIdentity,
    ) : AutoCloseable {
        override fun close() {
            client.close()
            identity.destroy()
        }
    }

    private inner class TicketSigningFixture {
        private val privateKey = Ed25519PrivateKeyParameters(SecureRandom())
        val publicKey: ByteArray = privateKey.generatePublicKey().encoded
        val keyId = "direct-v1-test"

        fun ticket(initiator: UUID, responder: UUID, connection: UUID, now: Long): ByteArray {
            val claims = DirectPathTicketV1.newBuilder()
                .setFormatVersion(1)
                .setTicketId(ByteString.copyFrom(UUID.randomUUID().bytes()))
                .setAccountId(ByteString.copyFrom(UUID.randomUUID().bytes()))
                .setInitiatorDeviceId(ByteString.copyFrom(initiator.bytes()))
                .setResponderDeviceId(ByteString.copyFrom(responder.bytes()))
                .setConnectionId(ByteString.copyFrom(connection.bytes()))
                .setPathEpoch(1)
                .setIssuedAtMs(now)
                .setExpiresAtMs(now + 120_000)
                .addAllowedCarriers(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
                .setServerNonce(ByteString.copyFrom(ByteArray(16) { 0x33 }))
                .setServerSigningKeyId(ByteString.copyFromUtf8(keyId))
                .build()
            val message = "TERMLINK-DIRECT-TICKET-V1\n".encodeToByteArray() + claims.toByteArray()
            val signer = Ed25519Signer()
            signer.init(true, privateKey)
            signer.update(message, 0, message.size)
            return claims.toBuilder()
                .setServerSignature(ByteString.copyFrom(signer.generateSignature()))
                .build()
                .toByteArray()
        }

        fun destroy() {
            privateKey.encoded.fill(0)
        }
    }

    private class FixedSecureRandom : SecureRandom() {
        private var next = 1
        override fun nextBytes(bytes: ByteArray) {
            bytes.indices.forEach { index -> bytes[index] = (next + index).toByte() }
            next++
        }
    }

    private fun UUID.bytes(): ByteArray = java.nio.ByteBuffer.allocate(16)
        .putLong(mostSignificantBits)
        .putLong(leastSignificantBits)
        .array()

    private fun ByteArray.base64Url(): String = Base64.getUrlEncoder().withoutPadding().encodeToString(this)
    private fun String.decodeBase64Url(): ByteArray = Base64.getUrlDecoder().decode(this)
}
